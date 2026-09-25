// ============================================================
//  ReloadCommand.cs — reload scene / reload scripts / reload asset <相対パス>（動いている端末のアプリへ差し替えを頼む。
//                     docs/android.md §23。エディタ無しで実行中の差し替えを確かめる）
//
//  【流れ】
//    1. 動いているアプリへつなぐ（RunningAppConnector。pause / resume と同じ。run / push の起動の記録の接続トークン）
//    2. 対象ごとに
//         scene   … RELOAD_SCENE（今のシーンをディスク＝上書き層 files/assets → APK の pak から読み直す）
//         scripts … SeedPak --scripts-only で DLL を作り直して files/bin/ へ送り（ScriptBinaryPusher。push と同じ中身）→ RELOAD_SCRIPTS
//         asset   … RELOAD_ASSET:{相対パス}（そのアセットのキャッシュを捨てて取り込み直す。先に push --assets で送っておく）
//    3. 応答を待って結果と所要時間を書く（AndroidReloadCommandSender。応答の書式はランタイムの hot_reload/wire.rs）
//    4. DETACH を送ってから閉じる（端末の一時停止は据え置く）
//  終了コード: 適用した・適用しなかった（RELOAD_SKIPPED。理由を書く）は 0、失敗・応答なしは 5、DLL の作り直しの失敗は 4。
// ============================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using SEEDEditor.Android.Adb;
using SEEDEditor.Android.HotReload;
using SEEDEditor.Android.Pipeline;
using SEEDEditor.Android.Project;
using SEEDEditor.Android.Steps;
using SEEDEditor.Android.Toolchain;
using SEEDEditor.Ipc;

namespace SEEDEditor.Tools.SeedAndroid.Commands;

/// <summary>reload。</summary>
public static class ReloadCommand
{
    /// <summary>
    /// 動いている端末のアプリへ差し替えを頼む。
    /// </summary>
    /// <param name="toolchain">道具の場所（adb・dotnet）。</param>
    /// <param name="line">コマンドラインの指定。</param>
    /// <param name="config">設定 JSON の値（無ければ null）。</param>
    /// <param name="cancellationToken">中断の合図（Ctrl+C）。</param>
    /// <returns>終了コード。</returns>
    public static async Task<int> RunAsync(
        AndroidToolchain toolchain, SeedAndroidCommandLine line, AndroidRunRequest? config, CancellationToken cancellationToken)
    {
        // 命令を先に決める（パスの誤りはつなぐ前に弾く）
        string? command = line.ReloadTarget switch
        {
            SeedAndroidReloadTarget.Scene => RuntimeIpcCommands.ReloadScene,
            SeedAndroidReloadTarget.Scripts => RuntimeIpcCommands.ReloadScripts,
            _ => AndroidReloadReplies.NormalizeRelative(line.ReloadPath ?? string.Empty) is { } relative
                ? RuntimeIpcCommands.ReloadAsset(relative)
                : null,
        };
        if (command is null)
        {
            Console.Error.WriteLine($"エラー: アセットはアセットルートからの相対パスで指定してください（絶対パス・.. は使えません）: {line.ReloadPath}");
            return SeedAndroidExitCodes.InvalidRequest;
        }

        var (connection, exitCode) = await RunningAppConnector.ConnectAsync(toolchain, line, config, cancellationToken);
        if (connection is null) return exitCode;
        var total = Stopwatch.StartNew();
        try
        {
            // ── スクリプト: DLL を作り直して files/bin/ へ送る ──
            if (line.ReloadTarget == SeedAndroidReloadTarget.Scripts)
            {
                var project = AndroidProjectResolver.Resolve(connection.Request.ProjectDir, connection.Request.AssetsDir);
                if (project is null)
                {
                    Console.Error.WriteLine("エラー: reload scripts には --project（スクリプトの出どころ）を指定してください。");
                    return SeedAndroidExitCodes.InvalidRequest;
                }
                var build = Stopwatch.StartNew();
                try
                {
                    var files = await ScriptBinaryPusher.BuildAsync(
                        toolchain.RequireDotnet(), connection.Engine, project,
                        text => Console.Out.WriteLine($"  {text}"), (_, text) => Console.Out.WriteLine($"    > {text}"),
                        cancellationToken);
                    var pushed = await ScriptBinaryPusher.PushAsync(
                        new AdbClient(toolchain.RequireAdb()), connection.Device.Serial, connection.ApplicationId, files, cancellationToken);
                    Console.Out.WriteLine($"スクリプトの DLL: {pushed}（{build.Elapsed.TotalSeconds:F1} 秒）");
                }
                catch (AndroidPipelineException ex)
                {
                    Console.Error.WriteLine($"エラー: {ex.Message}");
                    return SeedAndroidExitCodes.For(ex.Kind);
                }
            }

            // ── 命令を送って応答を待つ ──
            var send = Stopwatch.StartNew();
            var replies = await AndroidReloadCommandSender.SendAsync(
                connection.Session, new List<string> { command }, AndroidHotReloadApplier.DefaultReplyTimeout, cancellationToken);
            var reply = replies[0];
            var elapsed = $"応答まで {send.Elapsed.TotalMilliseconds:F0} ms・全体 {total.Elapsed.TotalSeconds:F1} 秒";
            switch (reply.Outcome)
            {
                case AndroidReloadOutcome.Done:
                    var onDevice = reply.ElapsedMs is { } ms ? $"・端末 {ms:F1} ms" : string.Empty;
                    Console.Out.WriteLine($"差し替えました: {command} → {reply.Detail}（{elapsed}{onDevice}）");
                    return SeedAndroidExitCodes.Success;
                case AndroidReloadOutcome.Skipped:
                    Console.Out.WriteLine($"差し替えませんでした: {command} → {reply.Detail}（{elapsed}）");
                    return SeedAndroidExitCodes.Success;
                default:
                    Console.Error.WriteLine($"エラー: 差し替えに失敗しました: {command} → {reply.Detail}（{elapsed}）");
                    return SeedAndroidExitCodes.DeviceOperation;
            }
        }
        finally
        {
            // DETACH を送ってから閉じる（端末は一時停止を据え置く）。forward も外す
            await connection.Session.CloseAsync(detach: true);
        }
    }
}
