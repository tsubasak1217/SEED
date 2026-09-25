// ============================================================
//  AndroidHotReloadApplier.cs — 変わったファイルの組を、動いている端末のアプリへ差し替える（docs/android.md §23）
//
//  【1 回の差し替えの流れ】（エディタの Android の実行中。変わったファイルはデバウンスでまとめた 1 組）
//    1. 分ける（AndroidHotReloadTable）: スクリプト（.cs）／シーン（.scene）／そのほかのアセット／関係ないもの
//    2. スクリプトがあれば: SeedPak --scripts-only で DLL を作り直し、files/bin/ へ送る（ScriptBinaryPusher）→ RELOAD_SCRIPTS
//    3. シーン・アセットがあれば: それらを起点に参照をたどり、端末の中身と違うものだけを files/assets へ送る
//       （AndroidAssetOverlaySync）→ 送ったものごとに RELOAD_SCENE:{相対パス}（シーン）か RELOAD_ASSET:{相対パス}
//    4. 命令をまとめて送り、応答を待つ（AndroidReloadCommandSender。ランタイムは同じフレームの要求をまとめて適用する）
//  どこかで失敗しても、できたところまでは続ける（スクリプトのコンパイルが通らなくても、アセットは差し替える）。
//  失敗・応答・所要時間は結果（AndroidHotReloadResult）に入れて返す（Output への出し方は呼び出し側）。
//
//  WPF に依存しない（コンソールツール・単体テストからリンクされる）。
// ============================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SEEDEditor.Android.Adb;
using SEEDEditor.Android.Ipc;
using SEEDEditor.Android.Pipeline;
using SEEDEditor.Android.Processes;
using SEEDEditor.Android.Project;
using SEEDEditor.Android.Steps;
using SEEDEditor.Android.Toolchain;
using SEEDEditor.Ipc;

namespace SEEDEditor.Android.HotReload;

/// <summary>差し替える相手（動いている端末のアプリ）。</summary>
/// <param name="Serial">端末のシリアル。</param>
/// <param name="ApplicationId">アプリ ID。</param>
/// <param name="Link">端末のアプリとの IPC の通信路。</param>
public sealed record AndroidHotReloadTarget(string Serial, string ApplicationId, IAndroidIpcLink Link);

/// <summary>1 回の差し替えの結果。</summary>
public sealed record AndroidHotReloadResult
{
    /// <summary>変わったスクリプト（相対パス）。</summary>
    public IReadOnlyList<string> Scripts { get; init; } = Array.Empty<string>();

    /// <summary>変わったシーン・アセット（相対パス。差し替えの起点）。</summary>
    public IReadOnlyList<string> Assets { get; init; } = Array.Empty<string>();

    /// <summary>関係ないので飛ばしたファイルの数。</summary>
    public int Ignored { get; init; }

    /// <summary>スクリプトの DLL を送った結果の 1 行（送らなかったら null）。</summary>
    public string? ScriptsPushed { get; init; }

    /// <summary>スクリプトの作り直し・転送にかかった時間（しなかったら null）。</summary>
    public TimeSpan? ScriptsElapsed { get; init; }

    /// <summary>上書き層へ送った結果（シーン・アセットが無ければ null）。</summary>
    public AndroidOverlaySyncResult? Overlay { get; init; }

    /// <summary>送った命令ごとの応答（命令の順）。</summary>
    public IReadOnlyList<AndroidReloadReply> Replies { get; init; } = Array.Empty<AndroidReloadReply>();

    /// <summary>命令を送ってから応答がそろうまでの時間（送らなかったら null）。</summary>
    public TimeSpan? ReloadElapsed { get; init; }

    /// <summary>途中の失敗（スクリプトのビルド・転送。無ければ空）。</summary>
    public IReadOnlyList<string> Failures { get; init; } = Array.Empty<string>();

    /// <summary>全体の所要時間。</summary>
    public TimeSpan Elapsed { get; init; }

    /// <summary>何もすることが無かったか（関係あるファイルが 1 つも無い）。</summary>
    public bool NothingToDo => Scripts.Count == 0 && Assets.Count == 0;
}

/// <summary>差し替えの本体。</summary>
public sealed class AndroidHotReloadApplier
{
    /// <summary>応答を待つ上限の既定値（シーンの読み直し・スクリプトの読み直しは端末で数秒かかることがある）。</summary>
    public static readonly TimeSpan DefaultReplyTimeout = TimeSpan.FromSeconds(60);

    /// <summary>道具（dotnet・adb）。</summary>
    private readonly AndroidToolchain _toolchain;

    /// <summary>エンジン側の置き場（SeedPak・APK の pak）。</summary>
    private readonly AndroidEnginePaths _engine;

    /// <summary>プロジェクト（アセットルート・スクリプトの出どころ・記録の置き場）。</summary>
    private readonly AndroidProjectInfo _project;

    /// <summary>応答を待つ上限。</summary>
    private readonly TimeSpan _replyTimeout;

    /// <summary>必要なものを指定して作る。</summary>
    /// <param name="toolchain">道具。</param>
    /// <param name="engine">エンジン側の置き場。</param>
    /// <param name="project">プロジェクト。</param>
    /// <param name="replyTimeout">応答を待つ上限（null なら既定）。</param>
    public AndroidHotReloadApplier(AndroidToolchain toolchain, AndroidEnginePaths engine, AndroidProjectInfo project, TimeSpan? replyTimeout = null)
    {
        _toolchain = toolchain;
        _engine = engine;
        _project = project;
        _replyTimeout = replyTimeout ?? DefaultReplyTimeout;
    }

    /// <summary>
    /// 変わったファイルの組を差し替える。
    /// </summary>
    /// <param name="changed">変わったファイル（アセットルートからの相対パス）。</param>
    /// <param name="target">差し替える相手。</param>
    /// <param name="info">途中の説明の 1 行の出し先（SeedPak の出力を含む）。</param>
    /// <param name="cancellationToken">中断の合図。</param>
    /// <returns>結果。</returns>
    public async Task<AndroidHotReloadResult> ApplyAsync(
        IReadOnlyCollection<string> changed, AndroidHotReloadTarget target, Action<string> info, CancellationToken cancellationToken)
    {
        var total = Stopwatch.StartNew();
        var split = Split(changed);
        var failures = new List<string>();
        var commands = new List<string>();
        var adb = new AdbClient(_toolchain.RequireAdb());

        // ── スクリプト: DLL を作り直して files/bin/ へ送る → RELOAD_SCRIPTS ──
        string? scriptsPushed = null;
        TimeSpan? scriptsElapsed = null;
        if (split.Scripts.Count > 0)
        {
            var watch = Stopwatch.StartNew();
            try
            {
                var files = await ScriptBinaryPusher.BuildAsync(
                    _toolchain.RequireDotnet(), _engine, _project, info, (_, line) => info(line), cancellationToken).ConfigureAwait(false);
                scriptsPushed = await ScriptBinaryPusher.PushAsync(adb, target.Serial, target.ApplicationId, files, cancellationToken)
                    .ConfigureAwait(false);
                commands.Add(RuntimeIpcCommands.ReloadScripts);
            }
            catch (AndroidPipelineException ex)
            {
                failures.Add($"スクリプト: {ex.Message}");
            }
            scriptsElapsed = watch.Elapsed;
        }

        // ── シーン・アセット: 参照をたどって端末と違うものを files/assets へ送る → RELOAD_SCENE / RELOAD_ASSET ──
        AndroidOverlaySyncResult? overlay = null;
        if (split.Assets.Count > 0)
        {
            try
            {
                var sync = new AndroidAssetOverlaySync(adb, target.Serial, target.ApplicationId, _project, _engine);
                overlay = await sync.PushChangedAsync(split.Assets, cancellationToken).ConfigureAwait(false);
                commands.AddRange(CommandsFor(overlay.Plan.ToPush.Select(item => item.Relative)));
            }
            catch (Exception ex) when (ex is AdbCommandException or IOException or UnauthorizedAccessException or AndroidPipelineException)
            {
                failures.Add($"アセットの転送: {ex.Message}");
            }
        }

        // ── 命令をまとめて送り、応答を待つ ──
        IReadOnlyList<AndroidReloadReply> replies = Array.Empty<AndroidReloadReply>();
        TimeSpan? reloadElapsed = null;
        if (commands.Count > 0)
        {
            var watch = Stopwatch.StartNew();
            replies = await AndroidReloadCommandSender.SendAsync(target.Link, commands, _replyTimeout, cancellationToken).ConfigureAwait(false);
            reloadElapsed = watch.Elapsed;
        }

        return new AndroidHotReloadResult
        {
            Scripts = split.Scripts,
            Assets = split.Assets,
            Ignored = split.Ignored,
            ScriptsPushed = scriptsPushed,
            ScriptsElapsed = scriptsElapsed,
            Overlay = overlay,
            Replies = replies,
            ReloadElapsed = reloadElapsed,
            Failures = failures,
            Elapsed = total.Elapsed,
        };
    }

    /// <summary>変わったファイルの分け方。</summary>
    /// <param name="Scripts">スクリプト。</param>
    /// <param name="Assets">シーン・アセット（差し替えの起点）。</param>
    /// <param name="Ignored">関係ないファイルの数。</param>
    public sealed record SplitResult(IReadOnlyList<string> Scripts, IReadOnlyList<string> Assets, int Ignored);

    /// <summary>
    /// 変わったファイルを分ける（純粋な処理。正規化できないパスは関係ないものとして数える）。
    /// </summary>
    /// <param name="changed">変わったファイル（アセットルートからの相対パス）。</param>
    /// <returns>分けた結果（それぞれ重複なし・入力の順）。</returns>
    public static SplitResult Split(IEnumerable<string> changed)
    {
        var scripts = new List<string>();
        var assets = new List<string>();
        var ignored = 0;
        foreach (var raw in changed)
        {
            var relative = AndroidReloadReplies.NormalizeRelative(raw);
            if (relative is null) { ignored++; continue; }
            switch (AndroidHotReloadTable.Classify(relative))
            {
                case AndroidHotReloadKind.Scripts:
                    AddDistinct(scripts, relative);
                    break;
                case AndroidHotReloadKind.Scene:
                case AndroidHotReloadKind.Asset:
                    AddDistinct(assets, relative);
                    break;
                default:
                    ignored++;
                    break;
            }
        }
        return new SplitResult(scripts, assets, ignored);
    }

    /// <summary>
    /// 送ったアセットごとの差し替えの命令（純粋な処理）。シーンは RELOAD_SCENE:{相対パス}（今のシーンのときだけ読み直す）、
    /// そのほかは RELOAD_ASSET:{相対パス}。スクリプト・関係ないもの（参照の閉包に入った .cs 等）には命令を出さない。
    /// </summary>
    /// <param name="pushed">送ったアセット（相対パス）。</param>
    /// <returns>命令（宛先の重複なし）。</returns>
    public static IReadOnlyList<string> CommandsFor(IEnumerable<string> pushed)
    {
        var commands = new List<string>();
        foreach (var raw in pushed)
        {
            var relative = AndroidReloadReplies.NormalizeRelative(raw);
            if (relative is null) continue;
            var command = AndroidHotReloadTable.Classify(relative) switch
            {
                AndroidHotReloadKind.Scene => RuntimeIpcCommands.ReloadSceneIfCurrent(relative),
                AndroidHotReloadKind.Asset => RuntimeIpcCommands.ReloadAsset(relative),
                _ => null,
            };
            if (command is not null && !commands.Contains(command, StringComparer.OrdinalIgnoreCase)) commands.Add(command);
        }
        return commands;
    }

    /// <summary>大文字小文字を問わずに重複を除いて足す。</summary>
    private static void AddDistinct(List<string> list, string item)
    {
        if (!list.Contains(item, StringComparer.OrdinalIgnoreCase)) list.Add(item);
    }
}
