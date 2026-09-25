// ============================================================
//  AndroidReloadCommandSender.cs — 差し替えの命令を端末のアプリへ送り、応答を待つ（docs/android.md §23）
//
//  【送り方】
//  命令をまとめて続けて送り（ランタイムは同じフレームに届いた要求をまとめて適用し、シーンの読み直しは 1 回で済む）、
//  命令ごとの宛先（AndroidReloadReplies.TargetOf）の応答がそろうまで待つ。応答を取りこぼさないよう、送る前に受け手を付ける。
//  時間切れ・通信路が切れたときは、応答の来なかった命令を NoReply として返す（例外にしない。呼び出し側が理由を出す）。
//
//  WPF に依存しない（コンソールツール・単体テストからリンクされる）。
// ============================================================

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SEEDEditor.Android.Ipc;

namespace SEEDEditor.Android.HotReload;

/// <summary>差し替えの命令の送り手。</summary>
public static class AndroidReloadCommandSender
{
    /// <summary>応答が無かったときの理由（時間切れ）の書式（{0}=秒数）。</summary>
    private const string TimeoutReasonFormat = "端末のアプリが {0:F0} 秒以内に応答しませんでした（アプリが背面にある・画面が消えている等）";

    /// <summary>応答が無かったときの理由（通信路が切れた）。</summary>
    private const string ClosedReason = "応答の前に端末のアプリとの通信路が切れました";

    /// <summary>送れなかったときの理由。</summary>
    private const string NotSentReason = "命令を送れませんでした（通信路が切れています）";

    /// <summary>
    /// 命令を続けて送り、命令ごとの応答を待つ。
    /// </summary>
    /// <param name="link">端末のアプリとの通信路。</param>
    /// <param name="commands">送る命令（差し替えの命令だけ。宛先の重なりは呼び出し側で除いておく）。</param>
    /// <param name="timeout">すべての応答を待つ上限。</param>
    /// <param name="cancellationToken">中断の合図。</param>
    /// <returns>命令の順の応答（届かなかったものは NoReply）。</returns>
    public static async Task<IReadOnlyList<AndroidReloadReply>> SendAsync(
        IAndroidIpcLink link, IReadOnlyList<string> commands, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var targets = commands.Select(command => AndroidReloadReplies.TargetOf(command)
            ?? throw new ArgumentException($"差し替えの命令ではありません: {command}", nameof(commands))).ToList();
        var pending = new HashSet<string>(targets, StringComparer.Ordinal);
        var received = new ConcurrentDictionary<string, AndroidReloadReply>(StringComparer.Ordinal);
        var allReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new object();

        void OnLine(string line)
        {
            if (!AndroidReloadReplies.TryParse(line, out var reply)) return;
            lock (gate)
            {
                if (!pending.Remove(reply.Target)) return;
                received[reply.Target] = reply;
                if (pending.Count == 0) allReceived.TrySetResult();
            }
        }

        // 応答を取りこぼさないよう、送る前に受け手を付ける
        link.MessageReceived += OnLine;
        try
        {
            for (var i = 0; i < commands.Count; i++)
            {
                if (link.Send(commands[i])) continue;
                // 送れなかった命令は待たない
                lock (gate)
                {
                    pending.Remove(targets[i]);
                    received[targets[i]] = new AndroidReloadReply(targets[i], AndroidReloadOutcome.NoReply, null, NotSentReason);
                    if (pending.Count == 0) allReceived.TrySetResult();
                }
            }
            lock (gate)
            {
                if (pending.Count == 0) allReceived.TrySetResult();
            }

            using var timer = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var timeoutTask = Task.Delay(timeout, timer.Token);
            var winner = await Task.WhenAny(allReceived.Task, link.Closed, timeoutTask).ConfigureAwait(false);
            timer.Cancel();
            cancellationToken.ThrowIfCancellationRequested();

            // 最後の応答の直後に閉じられることがあるので、そろっているかを先に見る
            var reason = allReceived.Task.IsCompleted ? null
                : winner == link.Closed ? ClosedReason
                : string.Format(System.Globalization.CultureInfo.InvariantCulture, TimeoutReasonFormat, timeout.TotalSeconds);
            return targets
                .Select(target => received.TryGetValue(target, out var reply)
                    ? reply
                    : new AndroidReloadReply(target, AndroidReloadOutcome.NoReply, null, reason ?? ClosedReason))
                .ToList();
        }
        finally
        {
            link.MessageReceived -= OnLine;
        }
    }
}
