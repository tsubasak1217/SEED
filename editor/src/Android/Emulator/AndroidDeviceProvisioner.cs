// ============================================================
//  AndroidDeviceProvisioner.cs — 実行先の端末を用意する（端末を選ぶ・要ればエミュレータを起動して起動の完了を待つ）
//
//  【流れ】（段階C-3。エディタの「Android（自動）」・端末を選んで見えないとき・SeedAndroid の --serial auto）
//    1. adb devices -l の一覧から、どうするかを決める（AndroidDeviceSelector.DecideForRun。純粋な処理）
//    2. 実機 … そのまま使う
//       エミュレータ（動いている・起動の途中）… 起動の完了（adb の状態が device かつ sys.boot_completed=1）を待って使う
//       端末が無い … AVD を決め（EmulatorAvdChooser）、emulator -avd <AVD> -gpu host を切り離して起動し、
//                    新しく現れた emulator-* を adb emu avd name で照合して自分が起動したものを見分け、起動の完了を待つ
//    3. 待っている間は一定の間隔で「待っています（経過秒・状態）」を出す。時間切れ（EmulatorTimings.BootTimeout）・
//       emulator.exe が失敗の終了コードで終わった・待っていたエミュレータが消えた、はエラー（種類は端末）
//  起動したエミュレータは、成功しても失敗・中断・時間切れでも止めない（起動に時間がかかるので次の実行で使い回す。
//  止めるのは利用者：窓を閉じる／adb emu kill）。
//
//  外の世界（adb・emulator）は IEmulatorHost、時間は WaitClock 越しに触る（単体テストは偽物で確かめる）。
//
//  WPF に依存しない（コンソールツール・単体テストからリンクされる）。
// ============================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SEEDEditor.Android.Adb;
using SEEDEditor.Android.Pipeline;
using SEEDEditor.Android.Processes;

namespace SEEDEditor.Android.Emulator;

/// <summary>実行先の端末を用意する。</summary>
public sealed class AndroidDeviceProvisioner
{
    // ── 文言 ───────────────────────────────────────────

    /// <summary>端末の一覧を取れないときの書式（{0}=理由）。</summary>
    private const string ListFailedFormat = "端末の一覧を取れません: {0}";

    /// <summary>待っている途中で一覧を取れなかったときの警告の書式（{0}=理由）。</summary>
    private const string ListRetryWarningFormat = "端末の一覧を取れませんでした（次の確認でやり直します）: {0}";

    /// <summary>決めた AVD の行の書式（{0}=AVD、{1}=理由）。</summary>
    private const string AvdChosenFormat = "AVD: {0}（{1}）";

    /// <summary>エミュレータを起動する行の書式（{0}=コマンド）。</summary>
    private const string LaunchingFormat = "エミュレータを起動します: {0}（スナップショットが無いと 1〜3 分かかります）";

    /// <summary>自分が起動したエミュレータを見つけた行の書式（{0}=シリアル、{1}=AVD、{2}=adb の状態）。</summary>
    private const string AppearedFormat = "起動したエミュレータ {0}（AVD {1}）が adb に現れました（{2}）。";

    /// <summary>別の AVD のエミュレータが現れた行の書式（{0}=シリアル、{1}=AVD）。</summary>
    private const string OtherAvdFormat = "新しく現れた {0} は別の AVD（{1}）なので使いません。";

    /// <summary>起動の完了の行の書式（{0}=シリアル、{1}=待った秒数）。</summary>
    private const string ReadyFormat = "エミュレータ {0} の起動が終わりました（{1} 秒待ちました）。";

    /// <summary>待っている行の書式（{0}=経過秒、{1}=状態）。</summary>
    private const string WaitingFormat = "エミュレータの起動を待っています（{0} 秒・{1}）…";

    /// <summary>ツールバーの進捗に出す文言の書式（{0}=経過秒）。</summary>
    private const string ProgressDetailFormat = "エミュレータの起動を待っています（{0} 秒）";

    /// <summary>状態: まだ adb に現れていない。</summary>
    private const string StateNotAppeared = "adb に現れるのを待っています";

    /// <summary>状態: まだ一度も確かめていない（動いているエミュレータを待つときの最初）。</summary>
    private const string StateUnknown = "状態を確かめています";

    /// <summary>状態: adb の状態の書式（{0}=adb の状態）。</summary>
    private const string StateAdbFormat = "adb の状態 {0}";

    /// <summary>状態: 起動の完了を待っている。</summary>
    private const string StateBooting = "起動の完了（sys.boot_completed）を待っています";

    /// <summary>待っていたエミュレータが消えたときの書式（{0}=シリアル）。</summary>
    private const string VanishedFormat = "エミュレータ {0} が adb から見えなくなりました（閉じられた・起動に失敗した）。";

    /// <summary>emulator.exe が失敗の終了コードで終わったときの書式（{0}=AVD、{1}=終了コード）。</summary>
    private const string ExitedFormat =
        "エミュレータ（AVD {0}）が起動の途中で終了しました（終了コード {1}）。Android Studio の Device Manager から同じ AVD を起動して、原因（メモリ不足・仮想化支援が無効 等）を確かめてください。";

    /// <summary>emulator.exe が 0 で終わったときの行の書式（{0}=AVD）。</summary>
    private const string ExitedCleanlyFormat = "emulator.exe（AVD {0}）は終了コード 0 で終わりました。エミュレータ本体が adb に現れるのを待ちます。";

    /// <summary>時間切れの書式（{0}=どのエミュレータか、{1}=秒、{2}=最後の状態）。</summary>
    private const string TimeoutFormat =
        "エミュレータ{0}の起動が {1} 秒以内に終わりませんでした（{2}）。エミュレータはそのまま残します。起動が終わってから、もう一度実行してください（次は起動中のエミュレータとして使います）。";

    /// <summary>時間切れの「どのエミュレータか」の書式（{0}=シリアルか AVD）。</summary>
    private const string TimeoutSubjectFormat = "（{0}）";

    /// <summary>中断したときの行。</summary>
    private const string CanceledNote = "中断しました。起動を始めたエミュレータはそのまま残します（次の実行で使います）。";

    /// <summary>決められなかったときの既定の理由。</summary>
    private const string UndecidedError = "端末を決められません。";

    /// <summary>秒数の書式（整数）。</summary>
    private const string SecondsFormat = "F0";

    /// <summary>emulator.exe が正常に終わったことを表す終了コード。</summary>
    private const int CleanExitCode = 0;

    /// <summary>ツールバーの進捗の割合（準備の途中なので 0 のまま）。</summary>
    private const double PrepareFraction = 0.0;

    // ── 状態 ─────────────────────────────────────────────

    /// <summary>adb・emulator の窓口。</summary>
    private readonly IEmulatorHost _host;

    /// <summary>時間の決まり。</summary>
    private readonly EmulatorTimings _timings;

    /// <summary>待ち合わせの時計を作る（本番は実時間。テストは仮の時計）。</summary>
    private readonly Func<WaitClock> _clockFactory;

    /// <summary>窓口と時間の決まりを指定して作る。</summary>
    /// <param name="host">adb・emulator の窓口。</param>
    /// <param name="timings">時間の決まり。</param>
    /// <param name="clockFactory">待ち合わせの時計を作る（null なら実時間）。</param>
    public AndroidDeviceProvisioner(IEmulatorHost host, EmulatorTimings timings, Func<WaitClock>? clockFactory = null)
    {
        _host = host;
        _timings = timings;
        _clockFactory = clockFactory ?? (() => new WaitClock());
    }

    /// <summary>
    /// 実行先の端末を用意する（要ればエミュレータを起動して起動の完了を待つ）。
    /// </summary>
    /// <param name="target">決め方。</param>
    /// <param name="lastUsedSerial">前回の実行先（優先する）。</param>
    /// <param name="avd">エミュレータを起動するときの AVD（null なら既定の規則）。</param>
    /// <param name="log">準備の工程のログ（説明・警告・待っている旨・ツールバーの進捗）。</param>
    /// <param name="cancellationToken">中断の合図（待つのをやめる。エミュレータは止めない）。</param>
    /// <returns>使える状態の端末。</returns>
    /// <exception cref="AndroidPipelineException">決められない・起動できない・時間切れ。</exception>
    public async Task<AdbDevice> EnsureAsync(
        AndroidDeviceTarget target, string? lastUsedSerial, string? avd, AndroidPhaseLog log, CancellationToken cancellationToken)
    {
        var devices = await ListDevicesAsync(cancellationToken).ConfigureAwait(false);
        var decision = AndroidDeviceSelector.DecideForRun(devices, target, lastUsedSerial);
        foreach (var warning in decision.Warnings) log.Warn(warning);
        foreach (var note in decision.Notes) log.Info(note);

        switch (decision.Kind)
        {
            case AndroidDeviceDecisionKind.Use when decision.Device is { Kind: AdbDeviceKind.Physical } physical:
                return physical;
            case AndroidDeviceDecisionKind.Use or AndroidDeviceDecisionKind.WaitForEmulator when decision.Device is { } emulator:
                // 動いているエミュレータも、adb に見えてから起動が終わるまでは install できないので完了を確かめる
                return await WaitUntilReadyAsync(new WaitTarget(emulator.Serial, null, EmptySerials, null), log, cancellationToken)
                    .ConfigureAwait(false);
            case AndroidDeviceDecisionKind.LaunchEmulator:
                return await LaunchAndWaitAsync(devices, avd, log, cancellationToken).ConfigureAwait(false);
            default:
                throw new AndroidPipelineException(AndroidFailureKind.Device, decision.Error ?? UndecidedError);
        }
    }

    /// <summary>空のシリアルの集まり（動いているエミュレータを待つときは見分けが要らない）。</summary>
    private static readonly IReadOnlySet<string> EmptySerials = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>
    /// AVD を決めてエミュレータを起動し、自分が起動したものを見分けて起動の完了を待つ。
    /// </summary>
    private async Task<AdbDevice> LaunchAndWaitAsync(
        IReadOnlyList<AdbDevice> devices, string? avd, AndroidPhaseLog log, CancellationToken cancellationToken)
    {
        var choice = EmulatorAvdChooser.Choose(avd, await _host.ListAvdsAsync(cancellationToken).ConfigureAwait(false));
        if (choice.Name is null) throw new AndroidPipelineException(AndroidFailureKind.Device, choice.Error ?? UndecidedError);
        log.Info(string.Format(AvdChosenFormat, choice.Name, choice.Reason));

        // 起動前から動いているエミュレータ（起動の途中を含む）は、自分が起動したものの候補から外す
        var existing = devices.Where(d => d.Kind == AdbDeviceKind.Emulator).Select(d => d.Serial).ToHashSet(StringComparer.Ordinal);
        log.Info(string.Format(LaunchingFormat, EmulatorLauncher.Describe(choice.Name)));
        IDetachedProcess process;
        try
        {
            process = _host.StartEmulator(choice.Name);
        }
        catch (ChildProcessStartException ex)
        {
            throw new AndroidPipelineException(AndroidFailureKind.Toolchain, ex.Message, ex);
        }

        using (process)
        {
            try
            {
                return await WaitUntilReadyAsync(new WaitTarget(null, choice.Name, existing, process), log, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                log.Info(CanceledNote);
                throw;
            }
        }
    }

    /// <summary>待つ相手。</summary>
    /// <param name="Serial">分かっているシリアル（自分が起動したものを見分けるときは null）。</param>
    /// <param name="Avd">見分けに使う AVD 名（自分が起動したときだけ）。</param>
    /// <param name="ExistingSerials">起動前からあったエミュレータ（見分けの候補から外す）。</param>
    /// <param name="Process">自分が起動した emulator.exe（動いているものを待つときは null）。</param>
    private sealed record WaitTarget(string? Serial, string? Avd, IReadOnlySet<string> ExistingSerials, IDetachedProcess? Process);

    /// <summary>
    /// 起動の完了（adb の状態が device かつ sys.boot_completed=1）まで待つ。自分が起動したものは先に見分ける。
    /// </summary>
    private async Task<AdbDevice> WaitUntilReadyAsync(WaitTarget target, AndroidPhaseLog log, CancellationToken cancellationToken)
    {
        var clock = _clockFactory();
        var serial = target.Serial;
        var otherAvds = new HashSet<string>(StringComparer.Ordinal);
        var nextProgressAt = _timings.ProgressInterval;
        var state = serial is null ? StateNotAppeared : StateUnknown;
        var waited = false;
        var listWarned = false;
        var exitNoted = false;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // ── 1. 一覧から相手を見つけ、状態を確かめる ──
            IReadOnlyList<AdbDevice>? devices = null;
            try
            {
                devices = await _host.ListDevicesAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is AdbCommandException or ChildProcessStartException)
            {
                // 一時的な adb の失敗で待つのをやめない（直らなければ時間切れになる）
                if (!listWarned) log.Warn(string.Format(ListRetryWarningFormat, ex.Message));
                listWarned = true;
            }

            if (devices is not null)
            {
                serial ??= await IdentifyAsync(devices, target, otherAvds, log, cancellationToken).ConfigureAwait(false);
                if (serial is not null)
                {
                    var device = devices.FirstOrDefault(d => string.Equals(d.Serial, serial, StringComparison.Ordinal))
                        ?? throw new AndroidPipelineException(AndroidFailureKind.Device, string.Format(VanishedFormat, serial));
                    if (device.IsReady && await _host.IsBootCompletedAsync(serial, cancellationToken).ConfigureAwait(false))
                    {
                        if (waited) log.Info(string.Format(ReadyFormat, serial, Seconds(clock.Elapsed)));
                        return device;
                    }
                    state = device.IsReady ? StateBooting : string.Format(StateAdbFormat, device.StateText);
                }
            }

            // ── 2. 自分が起動した emulator.exe が失敗で終わっていないか ──
            if (target.Process is { HasExited: true } process && !exitNoted)
            {
                var exitCode = process.ExitCode ?? CleanExitCode;
                if (exitCode != CleanExitCode)
                {
                    throw new AndroidPipelineException(AndroidFailureKind.Device, string.Format(ExitedFormat, target.Avd, exitCode));
                }
                // 0 で終わったなら本体へ引き継いだものとみなし、adb に現れるのを待ち続ける（時間切れで止まる）
                log.Info(string.Format(ExitedCleanlyFormat, target.Avd));
                exitNoted = true;
            }

            // ── 3. 時間切れ・待っている旨 ──
            var elapsed = clock.Elapsed;
            if (elapsed >= _timings.BootTimeout)
            {
                var subject = string.Format(TimeoutSubjectFormat, serial ?? target.Avd);
                throw new AndroidPipelineException(AndroidFailureKind.Device,
                    string.Format(TimeoutFormat, subject, Seconds(_timings.BootTimeout), state));
            }
            if (elapsed >= nextProgressAt)
            {
                log.Info(string.Format(WaitingFormat, Seconds(elapsed), state));
                nextProgressAt += _timings.ProgressInterval;
            }
            log.Report(new AndroidProgressChanged(AndroidPipelinePhase.Prepare, PrepareFraction,
                string.Format(ProgressDetailFormat, Seconds(elapsed))));

            await clock.DelayAsync(_timings.PollInterval, cancellationToken).ConfigureAwait(false);
            waited = true;
        }
    }

    /// <summary>
    /// 起動前には無かったエミュレータの AVD 名を聞き、自分が起動した AVD のものを見つける（見つからなければ null）。
    /// コンソールがまだ応答しないものは次の確認で聞き直し、別の AVD のものは以後聞かない。
    /// </summary>
    private async Task<string?> IdentifyAsync(
        IReadOnlyList<AdbDevice> devices, WaitTarget target, HashSet<string> otherAvds, AndroidPhaseLog log,
        CancellationToken cancellationToken)
    {
        var candidates = devices.Where(d => d.Kind == AdbDeviceKind.Emulator
                                            && !target.ExistingSerials.Contains(d.Serial)
                                            && !otherAvds.Contains(d.Serial));
        foreach (var candidate in candidates)
        {
            var name = await _host.GetAvdNameAsync(candidate.Serial, cancellationToken).ConfigureAwait(false);
            if (name is null) continue;
            if (string.Equals(name, target.Avd, StringComparison.OrdinalIgnoreCase))
            {
                log.Info(string.Format(AppearedFormat, candidate.Serial, name, candidate.StateText));
                return candidate.Serial;
            }
            otherAvds.Add(candidate.Serial);
            log.Info(string.Format(OtherAvdFormat, candidate.Serial, name));
        }
        return null;
    }

    /// <summary>端末の一覧（取れなければ種類「端末」のエラー）。</summary>
    private async Task<IReadOnlyList<AdbDevice>> ListDevicesAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _host.ListDevicesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is AdbCommandException or ChildProcessStartException)
        {
            throw new AndroidPipelineException(AndroidFailureKind.Device, string.Format(ListFailedFormat, ex.Message), ex);
        }
    }

    /// <summary>秒数（整数）の表記。</summary>
    private static string Seconds(TimeSpan elapsed) => elapsed.TotalSeconds.ToString(SecondsFormat, CultureInfo.InvariantCulture);
}
