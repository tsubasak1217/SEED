using System.Runtime.InteropServices;
using SEEDEditor.Android;
using SEEDEditor.Android.Adb;
using SEEDEditor.Android.Emulator;
using SEEDEditor.Android.Pipeline;
using SEEDEditor.Android.Processes;
using SEEDEditor.Android.Project;
using SEEDEditor.Android.Steps;
using SEEDEditor.Android.Toolchain;
using SEEDEditor.Tools.SeedAndroid;
using SpriteRigTests;

namespace AndroidPipelineTests;

/// <summary>
/// 段階C-3 の中核: 実行先の決め方（自動・選んだ端末が見えなければエミュレータ）、エミュレータの起動と待ち合わせ
/// （偽の adb・emulator と仮の時計で。時間切れ・起動直後の終了・中断）、AVD の決め方、起動するシーンの受け渡し
/// （パスの揃え方・am start の extra と引用）、切り離した起動のコマンドライン、SeedAndroid の --serial auto / --scene / --avd。
/// </summary>
public static class EmulatorAndSceneTests
{
    /// <summary>実機のシリアル。</summary>
    private const string PhoneSerial = "2B011JEGR02535";

    /// <summary>エミュレータのシリアル（1 台目）。</summary>
    private const string EmulatorSerial = "emulator-5554";

    /// <summary>開発用の既定の AVD。</summary>
    private const string DefaultAvd = "seed_pixel6_api35";

    /// <summary>テストを登録する。</summary>
    /// <param name="harness">テストランナー。</param>
    public static void Register(TestHarness harness)
    {
        harness.Add("決め方: 指定なし・auto・シリアル・シリアル＋切り替え の読み替え", ReadsTargets);
        harness.Add("自動の規則: 前回の実機 → ただ 1 台の実機 → 実機 2 台以上はエラー（勝手に選ばない）", AutoPrefersPhysical);
        harness.Add("自動の規則: 実機が無ければ 起動中のエミュレータ（前回優先）→ 起動の途中のもの → AVD を起動・使えない実機は警告", AutoFallsBackToEmulator);
        harness.Add("選んだ端末: 使えればそれ・起動途中のエミュレータは待つ・未許可の実機はエラー・見えなければ警告してエミュレータ", ExactOrEmulatorRules);
        harness.Add("AVD: 指定（大小文字を問わない）→ seed_pixel6_api35 → 一覧の先頭・無い指定と空の一覧はエラー", ChoosesAvd);
        harness.Add("emulator -list-avds と adb emu avd name の出力の読み取り・起動の引数", ParsesEmulatorOutputs);
        harness.Add("端末の用意: AVD を起動し、新しく現れた emulator-* を AVD 名で見分け、起動の完了まで待つ", ProvisionerLaunchesAndWaits);
        harness.Add("端末の用意: 時間切れ（300 秒）はエラー（エミュレータは残す旨）・起動を 1 回だけ", ProvisionerTimesOut);
        harness.Add("端末の用意: emulator.exe が失敗の終了コードで終わったらすぐエラー", ProvisionerDetectsEarlyExit);
        harness.Add("端末の用意: 実機はそのまま・動いているエミュレータは起動の完了だけ待つ（起動しない）", ProvisionerUsesExistingDevices);
        harness.Add("端末の用意: 選んだ実機が見えなければ警告して起動・指定の AVD が無ければ起動せずエラー・中断", ProvisionerFallbackAndErrors);
        harness.Add("シーン: 相対・\\・assets://・アセットルート内の絶対パスを揃え、外・..・空は受け付けない", NormalizesScenePath);
        harness.Add("起動の extra: シーンは --es seed.scene '<パス>'（単一引用符・' のエスケープ）・無ければ従来の引数", BuildsAmStartExtras);
        harness.Add("pak のエントリ名: 表だけを読み、区切り・大小文字を問わず引ける・壊れた pak はエラー", ReadsPakEntries);
        harness.Add("コマンドライン: C ランタイムの規則で引用し、CommandLineToArgvW で元に戻る", QuotesWindowsCommandLine);
        harness.Add("切り離した起動: CreateProcessW で起動し、終了と終了コードが分かる（cmd /c exit 3）", DetachedProcessReportsExit);
        harness.Add("道具: emulator.exe は SDK の emulator\\ から探し、無ければ理由", FindsEmulatorInSdk);
        harness.Add("SeedAndroid: --serial auto・--scene・--avd と設定 JSON の scene / avd / emulator_fallback", ParsesAutoSceneAndAvd);
    }

    // ── 部品 ───────────────────────────────────────────

    /// <summary>使える端末。</summary>
    private static AdbDevice Ready(string serial, AdbDeviceKind kind, string? model = null) =>
        new(serial, AdbDeviceState.Ready, "device", kind, model, null, null, null);

    /// <summary>使えない状態の端末。</summary>
    private static AdbDevice NotReady(string serial, AdbDeviceState state, string stateText, AdbDeviceKind kind) =>
        new(serial, state, stateText, kind, null, null, null, null);

    /// <summary>自動の決め方。</summary>
    private static readonly AndroidDeviceTarget Auto = AndroidDeviceTarget.From(AndroidDeviceTarget.AutoSerial, emulatorFallback: false);

    /// <summary>イベントを集める IProgress。</summary>
    private sealed class Collector : IProgress<AndroidPipelineEvent>
    {
        /// <summary>届いたイベント。</summary>
        public List<AndroidPipelineEvent> Events { get; } = new();

        /// <inheritdoc />
        public void Report(AndroidPipelineEvent value) => Events.Add(value);

        /// <summary>ログの本文（種類付き）。</summary>
        public IEnumerable<AndroidLogLine> Lines => Events.OfType<AndroidLogLine>();

        /// <summary>ログの本文をまとめる（確認用）。</summary>
        public string Text => string.Join("\n", Lines.Select(line => $"{line.Level}: {line.Text}"));
    }

    /// <summary>Delay で進む仮の時計（待たない）。</summary>
    private sealed class FakeClock : WaitClock
    {
        /// <summary>いまの時刻。</summary>
        public TimeSpan Now { get; private set; }

        /// <inheritdoc />
        public override TimeSpan Elapsed => Now;

        /// <inheritdoc />
        public override Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Now += delay;
            return Task.CompletedTask;
        }
    }

    /// <summary>偽の emulator.exe。</summary>
    private sealed class FakeProcess : IDetachedProcess
    {
        /// <summary>終わる時刻と終了コード（null なら終わらない）。</summary>
        public (TimeSpan At, int Code)? Exit { get; init; }

        /// <summary>時計。</summary>
        public required FakeClock Clock { get; init; }

        /// <inheritdoc />
        public int Id => 4242;

        /// <inheritdoc />
        public bool HasExited => Exit is { } exit && Clock.Now >= exit.At;

        /// <inheritdoc />
        public int? ExitCode => HasExited ? Exit!.Value.Code : null;

        /// <summary>閉じたか。</summary>
        public bool Disposed { get; private set; }

        /// <inheritdoc />
        public void Dispose() => Disposed = true;
    }

    /// <summary>偽の adb・emulator（時刻ごとの一覧を台本で返す）。</summary>
    private sealed class FakeHost : IEmulatorHost
    {
        /// <summary>時計。</summary>
        public required FakeClock Clock { get; init; }

        /// <summary>時刻（秒）→ 端末の一覧。</summary>
        public Func<double, IReadOnlyList<AdbDevice>> DevicesAt { get; init; } = _ => Array.Empty<AdbDevice>();

        /// <summary>シリアル → AVD 名（コンソールが応答しなければ null）。</summary>
        public Func<string, double, string?> AvdNameOf { get; init; } = (_, _) => DefaultAvd;

        /// <summary>シリアル・時刻（秒）→ 起動が終わったか。</summary>
        public Func<string, double, bool> BootCompletedAt { get; init; } = (_, _) => true;

        /// <summary>AVD の一覧。</summary>
        public IReadOnlyList<string> Avds { get; init; } = new[] { "wop", DefaultAvd };

        /// <summary>emulator.exe の偽物を作る（起動した AVD を受け取る）。</summary>
        public Func<string, FakeProcess>? ProcessFor { get; init; }

        /// <summary>起動した AVD。</summary>
        public List<string> Started { get; } = new();

        /// <summary>AVD の一覧を聞かれた回数。</summary>
        public int AvdListCalls { get; private set; }

        /// <summary>最後に起動した偽の emulator.exe。</summary>
        public FakeProcess? LastProcess { get; private set; }

        /// <summary>いまの時刻（秒）。</summary>
        private double Seconds => Clock.Now.TotalSeconds;

        /// <inheritdoc />
        public Task<IReadOnlyList<AdbDevice>> ListDevicesAsync(CancellationToken cancellationToken) => Task.FromResult(DevicesAt(Seconds));

        /// <inheritdoc />
        public Task<string?> GetAvdNameAsync(string serial, CancellationToken cancellationToken) => Task.FromResult(AvdNameOf(serial, Seconds));

        /// <inheritdoc />
        public Task<bool> IsBootCompletedAsync(string serial, CancellationToken cancellationToken) =>
            Task.FromResult(BootCompletedAt(serial, Seconds));

        /// <inheritdoc />
        public Task<IReadOnlyList<string>> ListAvdsAsync(CancellationToken cancellationToken)
        {
            AvdListCalls++;
            return Task.FromResult(Avds);
        }

        /// <inheritdoc />
        public IDetachedProcess StartEmulator(string avdName)
        {
            Started.Add(avdName);
            LastProcess = ProcessFor?.Invoke(avdName) ?? new FakeProcess { Clock = Clock };
            return LastProcess;
        }
    }

    /// <summary>端末の用意を動かす（同期で待つ）。</summary>
    private static AdbDevice Ensure(
        FakeHost host, AndroidDeviceTarget target, Collector log, string? lastUsed = null, string? avd = null,
        CancellationToken cancellationToken = default)
    {
        var provisioner = new AndroidDeviceProvisioner(host, EmulatorTimings.Default, () => host.Clock);
        return provisioner.EnsureAsync(target, lastUsed, avd, new AndroidPhaseLog(AndroidPipelinePhase.Prepare, log), cancellationToken)
            .GetAwaiter().GetResult();
    }

    /// <summary>例外を捕まえる（投げなければ失敗）。</summary>
    private static T Throws<T>(Action action, string what) where T : Exception
    {
        try
        {
            action();
        }
        catch (T ex)
        {
            return ex;
        }
        throw new AssertionException($"{what}: {typeof(T).Name} が投げられませんでした");
    }

    // ── 決め方 ─────────────────────────────────────────

    /// <summary>決め方の読み替え。</summary>
    private static void ReadsTargets()
    {
        Check.Equal(AndroidDeviceTargetMode.Single, AndroidDeviceTarget.From(null, false).Mode, "指定なし");
        Check.Equal(AndroidDeviceTargetMode.Single, AndroidDeviceTarget.From("  ", true).Mode, "空白だけも指定なし");
        Check.Equal(AndroidDeviceTargetMode.Auto, AndroidDeviceTarget.From(" AUTO ", false).Mode, "auto（大小文字・空白を問わない）");
        Check.True(AndroidDeviceTarget.From("auto", false).Serial is null, "自動にシリアルは無い");
        Check.Equal(AndroidDeviceTargetMode.Exact, AndroidDeviceTarget.From(PhoneSerial, false).Mode, "シリアル");
        var fallback = AndroidDeviceTarget.From($" {PhoneSerial} ", true);
        Check.Equal(AndroidDeviceTargetMode.ExactOrEmulator, fallback.Mode, "シリアル＋切り替え");
        Check.Equal(PhoneSerial, fallback.Serial, "前後の空白は落とす");
        Check.True(Auto.MayLaunchEmulator && fallback.MayLaunchEmulator, "自動と切り替えはエミュレータを起動し得る");
        Check.True(!AndroidDeviceTarget.From(PhoneSerial, false).MayLaunchEmulator, "シリアルだけは起動しない（従来どおり）");
    }

    /// <summary>自動: 実機を優先。</summary>
    private static void AutoPrefersPhysical()
    {
        var phone = Ready(PhoneSerial, AdbDeviceKind.Physical, "Pixel_6a");
        var other = Ready("R58M0000", AdbDeviceKind.Physical, "Galaxy");
        var emulator = Ready(EmulatorSerial, AdbDeviceKind.Emulator);

        var lastUsed = AndroidDeviceSelector.DecideForRun(new[] { emulator, other, phone }, Auto, PhoneSerial);
        Check.Equal(AndroidDeviceDecisionKind.Use, lastUsed.Kind, "使う");
        Check.Equal(PhoneSerial, lastUsed.Device!.Serial, "前回使った実機（エミュレータより実機）");
        Check.True(lastUsed.Notes.Single().Contains("前回使った実機"), $"理由: {lastUsed.Notes.Single()}");

        var only = AndroidDeviceSelector.DecideForRun(new[] { emulator, phone }, Auto, EmulatorSerial);
        Check.Equal(PhoneSerial, only.Device!.Serial, "前回がエミュレータでも、つながっている実機を優先");

        var ambiguous = AndroidDeviceSelector.DecideForRun(new[] { other, phone }, Auto, null);
        Check.Equal(AndroidDeviceDecisionKind.Fail, ambiguous.Kind, "実機 2 台以上で前回が無ければ選ばない");
        Check.True(ambiguous.Error!.Contains("2 台") && ambiguous.Error.Contains(PhoneSerial) && ambiguous.Error.Contains("R58M0000"),
            $"一覧を添える: {ambiguous.Error}");
    }

    /// <summary>自動: エミュレータへ。</summary>
    private static void AutoFallsBackToEmulator()
    {
        var unauthorized = NotReady(PhoneSerial, AdbDeviceState.Unauthorized, "unauthorized", AdbDeviceKind.Physical);
        var e1 = Ready(EmulatorSerial, AdbDeviceKind.Emulator);
        var e2 = Ready("emulator-5556", AdbDeviceKind.Emulator);

        var running = AndroidDeviceSelector.DecideForRun(new[] { unauthorized, e1, e2 }, Auto, "emulator-5556");
        Check.Equal(AndroidDeviceDecisionKind.Use, running.Kind, "起動中のエミュレータを使う");
        Check.Equal("emulator-5556", running.Device!.Serial, "前回使ったエミュレータを優先");
        Check.True(running.Warnings.Single().Contains("USB デバッグ"), $"使えない実機は理由付きで飛ばす: {running.Warnings.Single()}");
        Check.True(running.Notes.Any(note => note.Contains("つながっている実機が無い")), "実機が無い旨");

        var first = AndroidDeviceSelector.DecideForRun(new[] { e1, e2 }, Auto, null);
        Check.Equal(EmulatorSerial, first.Device!.Serial, "前回が無ければ一覧の先頭");

        var booting = AndroidDeviceSelector.DecideForRun(
            new[] { NotReady(EmulatorSerial, AdbDeviceState.Offline, "offline", AdbDeviceKind.Emulator) }, Auto, null);
        Check.Equal(AndroidDeviceDecisionKind.WaitForEmulator, booting.Kind, "起動の途中のエミュレータを待つ（新しく起動しない）");
        Check.Equal(EmulatorSerial, booting.Device!.Serial, "待つ相手");

        var none = AndroidDeviceSelector.DecideForRun(Array.Empty<AdbDevice>(), Auto, null);
        Check.Equal(AndroidDeviceDecisionKind.LaunchEmulator, none.Kind, "何も無ければ AVD を起動");
        Check.True(none.Notes.Any(note => note.Contains("AVD からエミュレータを起動")), "起動する旨");

        var brokenEmulator = AndroidDeviceSelector.DecideForRun(
            new[] { NotReady(EmulatorSerial, AdbDeviceState.Unauthorized, "unauthorized", AdbDeviceKind.Emulator) }, Auto, null);
        Check.Equal(AndroidDeviceDecisionKind.LaunchEmulator, brokenEmulator.Kind, "待っても使えないエミュレータは飛ばして起動");
        Check.True(brokenEmulator.Warnings.Any(w => w.Contains(EmulatorSerial)), "飛ばした旨");
    }

    /// <summary>選んだ端末＋切り替え。</summary>
    private static void ExactOrEmulatorRules()
    {
        var target = AndroidDeviceTarget.From(PhoneSerial, emulatorFallback: true);
        var phone = Ready(PhoneSerial, AdbDeviceKind.Physical, "Pixel_6a");
        var emulator = Ready(EmulatorSerial, AdbDeviceKind.Emulator);

        var found = AndroidDeviceSelector.DecideForRun(new[] { emulator, phone }, target, null);
        Check.Equal(PhoneSerial, found.Device!.Serial, "選んだ端末が使えればそれ");
        Check.Equal(0, found.Warnings.Count, "警告なし");

        var unauthorized = AndroidDeviceSelector.DecideForRun(
            new[] { NotReady(PhoneSerial, AdbDeviceState.Unauthorized, "unauthorized", AdbDeviceKind.Physical), emulator }, target, null);
        Check.Equal(AndroidDeviceDecisionKind.Fail, unauthorized.Kind, "つながっているが未許可の実機は切り替えずにエラー（許可すれば使える）");
        Check.True(unauthorized.Error!.Contains("USB デバッグ"), "対処");

        var missing = AndroidDeviceSelector.DecideForRun(new[] { emulator }, target, null);
        Check.Equal(AndroidDeviceDecisionKind.Use, missing.Kind, "見えなければ起動中のエミュレータ");
        Check.Equal(EmulatorSerial, missing.Device!.Serial, "エミュレータ");
        Check.Equal($"実機 {PhoneSerial} が見えないためエミュレータで実行します。", missing.Warnings.Single(), "利用者の要望どおりの 1 行");

        var missingNothing = AndroidDeviceSelector.DecideForRun(Array.Empty<AdbDevice>(), target, null);
        Check.Equal(AndroidDeviceDecisionKind.LaunchEmulator, missingNothing.Kind, "何も無ければ AVD を起動");

        var emulatorTarget = AndroidDeviceTarget.From("emulator-5556", emulatorFallback: true);
        var missingEmulator = AndroidDeviceSelector.DecideForRun(new[] { emulator }, emulatorTarget, null);
        Check.True(missingEmulator.Warnings.Single().StartsWith("エミュレータ emulator-5556 が見えないため"), "エミュレータを選んでいたときの文言");

        var bootingSelected = AndroidDeviceSelector.DecideForRun(
            new[] { NotReady("emulator-5556", AdbDeviceState.Offline, "offline", AdbDeviceKind.Emulator) }, emulatorTarget, null);
        Check.Equal(AndroidDeviceDecisionKind.WaitForEmulator, bootingSelected.Kind, "選んだエミュレータが起動の途中なら待つ");

        var exact = AndroidDeviceSelector.DecideForRun(new[] { emulator }, AndroidDeviceTarget.From(PhoneSerial, false), null);
        Check.Equal(AndroidDeviceDecisionKind.Fail, exact.Kind, "切り替えなしのシリアル指定は従来どおりエラー");
        var single = AndroidDeviceSelector.DecideForRun(new[] { emulator }, AndroidDeviceTarget.From(null, false), null);
        Check.Equal(EmulatorSerial, single.Device!.Serial, "指定なしは従来どおり 1 台ならそれ");
    }

    /// <summary>AVD の決め方。</summary>
    private static void ChoosesAvd()
    {
        var both = new[] { "wop", DefaultAvd };
        var configured = EmulatorAvdChooser.Choose("WOP", both);
        Check.Equal("wop", configured.Name, "指定（大小文字を問わず、一覧の表記で起動）");
        Check.True(configured.Reason.Contains("android.emulator_avd"), "理由");

        var missing = EmulatorAvdChooser.Choose("nope", both);
        Check.True(missing.Name is null && missing.Error!.Contains("nope") && missing.Error.Contains("wop"), $"無い指定はエラー（一覧を添える）: {missing.Error}");

        Check.Equal(DefaultAvd, EmulatorAvdChooser.Choose(null, both).Name, "指定なし: seed_pixel6_api35");
        var first = EmulatorAvdChooser.Choose(" ", new[] { "b_avd", "a_avd" });
        Check.Equal("b_avd", first.Name, "既定が無ければ一覧の先頭");
        Check.True(first.Reason.Contains(DefaultAvd), "先頭にした理由");
        var empty = EmulatorAvdChooser.Choose(null, Array.Empty<string>());
        Check.True(empty.Name is null && empty.Error!.Contains("Device Manager"), $"一覧が空ならエラー: {empty.Error}");
    }

    /// <summary>emulator・adb の出力。</summary>
    private static void ParsesEmulatorOutputs()
    {
        var avds = EmulatorLauncher.ParseAvdList(new[]
        {
            "INFO    | Storing crashdata in: C:\\Users\\x\\AppData\\Local\\Temp\\AndroidEmulator\\emu-crash-36.1.9.db",
            "", "seed_pixel6_api35", "  wop  ", "seed_pixel6_api35", "Pixel 9 API 35",
        });
        Check.Equal("seed_pixel6_api35,wop", string.Join(",", avds), "ログの行・空行・重複・空白を含む行は読み飛ばす");

        Check.Equal(DefaultAvd, AdbClient.ParseEmulatorAvdName(new[] { "seed_pixel6_api35", "OK" }), "1 行目が AVD 名");
        Check.Equal(DefaultAvd, AdbClient.ParseEmulatorAvdName(new[] { "", " seed_pixel6_api35\r", "OK" }), "空行・CR を落とす");
        Check.True(AdbClient.ParseEmulatorAvdName(new[] { "OK" }) is null && AdbClient.ParseEmulatorAvdName(Array.Empty<string>()) is null, "名前が無ければ null");

        Check.Equal("-avd,seed_pixel6_api35,-gpu,host", string.Join(",", EmulatorLauncher.LaunchArguments(DefaultAvd)), "起動の引数（quick boot は既定のまま）");
        Check.Equal("emulator -avd seed_pixel6_api35 -gpu host", EmulatorLauncher.Describe(DefaultAvd), "ログの表示");
    }

    // ── 端末の用意（偽の adb・emulator と仮の時計）────────────────────

    /// <summary>起動して待つ。</summary>
    private static void ProvisionerLaunchesAndWaits()
    {
        var clock = new FakeClock();
        var existing = NotReady("emulator-5556", AdbDeviceState.Unauthorized, "unauthorized", AdbDeviceKind.Emulator);
        var host = new FakeHost
        {
            Clock = clock,
            // 6 秒: 別の AVD（wop）のエミュレータが現れる・10 秒: 自分のが offline・20 秒: device・34 秒: 起動完了
            DevicesAt = t => t switch
            {
                < 6  => new[] { existing },
                < 10 => new[] { existing, Ready("emulator-5558", AdbDeviceKind.Emulator) },
                < 20 => new[] { existing, Ready("emulator-5558", AdbDeviceKind.Emulator), NotReady(EmulatorSerial, AdbDeviceState.Offline, "offline", AdbDeviceKind.Emulator) },
                _    => new[] { existing, Ready("emulator-5558", AdbDeviceKind.Emulator), Ready(EmulatorSerial, AdbDeviceKind.Emulator) },
            },
            AvdNameOf = (serial, t) => serial switch
            {
                "emulator-5558" => "wop",
                EmulatorSerial => t < 12 ? null : DefaultAvd,   // コンソールはしばらく応答しない
                _ => "other",
            },
            BootCompletedAt = (_, t) => t >= 34,
        };
        var log = new Collector();
        var device = Ensure(host, Auto, log);

        Check.Equal(EmulatorSerial, device.Serial, "自分が起動した AVD のエミュレータ");
        Check.True(device.IsReady, "使える状態で返す");
        Check.Equal(DefaultAvd, host.Started.Single(), "既定の AVD を 1 回だけ起動");
        Check.True(host.LastProcess!.Disposed, "emulator.exe のハンドルは閉じる（止めはしない）");
        Check.True(clock.Now.TotalSeconds >= 34, "起動の完了まで待った");
        var text = log.Text;
        Check.True(text.Contains("Warning: 使えない状態のエミュレータは使いません"), $"使えないエミュレータは飛ばす:\n{text}");
        Check.True(text.Contains("AVD: seed_pixel6_api35（指定なし: 開発用の既定の AVD seed_pixel6_api35）"), "AVD と理由");
        Check.True(text.Contains("エミュレータを起動します: emulator -avd seed_pixel6_api35 -gpu host"), "起動のコマンド");
        Check.True(text.Contains("新しく現れた emulator-5558 は別の AVD（wop）なので使いません"), "別の AVD を見分ける");
        Check.True(text.Contains("起動したエミュレータ emulator-5554（AVD seed_pixel6_api35）が adb に現れました（offline）"), "自分のものを見つける");
        Check.True(text.Contains("エミュレータの起動を待っています（10 秒"), "一定の間隔で待っている旨");
        Check.True(text.Contains("起動の完了（sys.boot_completed）を待っています"), "device になった後は起動の完了を待つ");
        Check.True(text.Contains("エミュレータ emulator-5554 の起動が終わりました"), "終わった旨");
        var progress = log.Events.OfType<AndroidProgressChanged>().ToList();
        Check.True(progress.Count > 0 && progress.All(p => p.Phase == AndroidPipelinePhase.Prepare && p.Message.Contains("起動を待っています")),
            "ツールバーの進捗へ準備の詳細を送る");
    }

    /// <summary>時間切れ。</summary>
    private static void ProvisionerTimesOut()
    {
        var clock = new FakeClock();
        var host = new FakeHost { Clock = clock };
        var log = new Collector();
        var error = Throws<AndroidPipelineException>(() => Ensure(host, Auto, log), "時間切れ");
        Check.Equal(AndroidFailureKind.Device, error.Kind, "種類は端末");
        Check.True(error.Message.Contains("300 秒以内に終わりませんでした") && error.Message.Contains("そのまま残します"),
            $"時間切れと、エミュレータを残す旨: {error.Message}");
        Check.True(error.Message.Contains("adb に現れるのを待っています"), "最後の状態を添える");
        Check.True(clock.Now.TotalSeconds >= EmulatorTimings.DefaultBootTimeoutSeconds, "時間切れまで待った");
        Check.Equal(1, host.Started.Count, "起動は 1 回だけ（何度も起動しない）");
    }

    /// <summary>起動直後の終了。</summary>
    private static void ProvisionerDetectsEarlyExit()
    {
        var clock = new FakeClock();
        var host = new FakeHost { Clock = clock, ProcessFor = _ => new FakeProcess { Clock = clock, Exit = (TimeSpan.FromSeconds(4), 1) } };
        var error = Throws<AndroidPipelineException>(() => Ensure(host, Auto, new Collector()), "起動直後の終了");
        Check.True(error.Message.Contains("終了コード 1") && error.Message.Contains("Device Manager"), $"終了コードと対処: {error.Message}");
        Check.True(clock.Now.TotalSeconds < EmulatorTimings.DefaultBootTimeoutSeconds, "時間切れを待たずに止める");

        // 0 で終わった emulator.exe は本体へ引き継いだとみなして待ち続ける
        var clean = new FakeClock();
        var handedOff = new FakeHost
        {
            Clock = clean,
            ProcessFor = _ => new FakeProcess { Clock = clean, Exit = (TimeSpan.FromSeconds(2), 0) },
            DevicesAt = t => t < 8 ? Array.Empty<AdbDevice>() : new[] { Ready(EmulatorSerial, AdbDeviceKind.Emulator) },
        };
        var log = new Collector();
        Check.Equal(EmulatorSerial, Ensure(handedOff, Auto, log).Serial, "0 で終わっても現れれば使う");
        Check.True(log.Text.Contains("終了コード 0"), "その旨を 1 行");
    }

    /// <summary>既にある端末。</summary>
    private static void ProvisionerUsesExistingDevices()
    {
        var clock = new FakeClock();
        var phoneHost = new FakeHost { Clock = clock, DevicesAt = _ => new[] { Ready(PhoneSerial, AdbDeviceKind.Physical, "Pixel_6a") } };
        var log = new Collector();
        Check.Equal(PhoneSerial, Ensure(phoneHost, Auto, log, lastUsed: PhoneSerial).Serial, "実機はそのまま");
        Check.True(phoneHost.AvdListCalls == 0 && phoneHost.Started.Count == 0, "エミュレータには触らない（emulator.exe も要らない）");
        Check.Equal(TimeSpan.Zero, clock.Now, "待たない");

        var bootClock = new FakeClock();
        var bootHost = new FakeHost
        {
            Clock = bootClock,
            DevicesAt = _ => new[] { Ready(EmulatorSerial, AdbDeviceKind.Emulator) },
            BootCompletedAt = (_, t) => t >= 6,
        };
        var bootLog = new Collector();
        Check.Equal(EmulatorSerial, Ensure(bootHost, Auto, bootLog).Serial, "動いているエミュレータ");
        Check.True(bootHost.Started.Count == 0, "新しく起動しない");
        Check.True(bootClock.Now.TotalSeconds >= 6, "起動の完了（sys.boot_completed）まで待つ");

        var readyClock = new FakeClock();
        var readyHost = new FakeHost { Clock = readyClock, DevicesAt = _ => new[] { Ready(EmulatorSerial, AdbDeviceKind.Emulator) } };
        var readyLog = new Collector();
        Ensure(readyHost, Auto, readyLog);
        Check.Equal(TimeSpan.Zero, readyClock.Now, "起動が終わっていれば待たない");
        Check.True(!readyLog.Text.Contains("起動が終わりました"), "待たなかったときは終わった旨を出さない");

        var vanished = new FakeHost
        {
            Clock = new FakeClock(),
            DevicesAt = t => t < 1 ? new[] { NotReady(EmulatorSerial, AdbDeviceState.Offline, "offline", AdbDeviceKind.Emulator) } : Array.Empty<AdbDevice>(),
        };
        var error = Throws<AndroidPipelineException>(() => Ensure(vanished, Auto, new Collector()), "消えた");
        Check.True(error.Message.Contains("見えなくなりました"), $"待っていたエミュレータが消えたらエラー: {error.Message}");
    }

    /// <summary>切り替え・誤り・中断。</summary>
    private static void ProvisionerFallbackAndErrors()
    {
        var clock = new FakeClock();
        var host = new FakeHost
        {
            Clock = clock,
            DevicesAt = t => t < 4 ? Array.Empty<AdbDevice>() : new[] { Ready(EmulatorSerial, AdbDeviceKind.Emulator) },
            AvdNameOf = (_, _) => "wop",
        };
        var log = new Collector();
        var device = Ensure(host, AndroidDeviceTarget.From(PhoneSerial, emulatorFallback: true), log, avd: "wop");
        Check.Equal(EmulatorSerial, device.Serial, "エミュレータで実行");
        Check.True(log.Lines.Any(line => line.Level == AndroidLogLevel.Warning && line.Text == $"実機 {PhoneSerial} が見えないためエミュレータで実行します。"),
            $"見えない旨を警告の 1 行で:\n{log.Text}");
        Check.Equal("wop", host.Started.Single(), "指定の AVD を起動");

        var missingAvd = new FakeHost { Clock = new FakeClock() };
        var error = Throws<AndroidPipelineException>(() => Ensure(missingAvd, Auto, new Collector(), avd: "nope"), "無い AVD");
        Check.Equal(AndroidFailureKind.Device, error.Kind, "種類は端末");
        Check.True(error.Message.Contains("nope") && missingAvd.Started.Count == 0, "起動せずにエラー");

        using var cancellation = new CancellationTokenSource();
        var cancelClock = new FakeClock();
        var cancelHost = new FakeHost
        {
            Clock = cancelClock,
            DevicesAt = t =>
            {
                if (t >= 6) cancellation.Cancel();
                return Array.Empty<AdbDevice>();
            },
        };
        var cancelLog = new Collector();
        Throws<OperationCanceledException>(() => Ensure(cancelHost, Auto, cancelLog, cancellationToken: cancellation.Token), "中断");
        Check.True(cancelLog.Text.Contains("そのまま残します"), "起動を始めたエミュレータは残す旨");
    }

    // ── シーンの受け渡し ──────────────────────────────────

    /// <summary>シーンのパスを揃える。</summary>
    private static void NormalizesScenePath()
    {
        var root = Path.Combine(Path.GetTempPath(), "SeedC3Scene", "Game", "assets");
        AndroidScenePathResult N(string? value) => AndroidScenePath.Normalize(value, root);

        Check.Equal(AndroidScenePathResult.None, N(null), "指定なし");
        Check.Equal(AndroidScenePathResult.None, N("   "), "空白だけも指定なし");
        Check.Equal("scenes/Main.scene", N("scenes/Main.scene").Relative, "相対パスはそのまま");
        Check.Equal("scenes/Main.scene", N(@"scenes\Main.scene").Relative, "\\ は / に");
        Check.Equal("scenes/Main.scene", N("./scenes//Main.scene").Relative, ". と空の区切りを落とす");
        Check.Equal("scenes/Main.scene", N("assets://scenes/Main.scene").Relative, "assets:// を外す");
        Check.Equal("scenes/Main.scene", N("/scenes/Main.scene").Relative, "先頭の / はアセットルート基準とみなす");
        Check.Equal("シーン/森 の 2.scene", N(Path.Combine(root, "シーン", "森 の 2.scene")).Relative, "アセットルート内の絶対パス（日本語・空白）");
        Check.Equal("scenes/X.scene", N("\"" + Path.Combine(root, "scenes", "X.scene") + "\"").Relative, "引用符で囲んだ貼り付けも読む");

        var outside = N(Path.Combine(Path.GetTempPath(), "SeedC3Scene", "Other", "X.scene"));
        Check.True(outside.Relative is null && outside.Error!.Contains("外"), $"アセットルートの外はエラー: {outside.Error}");
        var sibling = N(Path.Combine(Path.GetTempPath(), "SeedC3Scene", "Game", "assets2", "X.scene"));
        Check.True(sibling.Error is not null, "名前が前方一致するだけの隣のフォルダも外");
        Check.True(N("../x.scene").Error!.Contains(".."), ".. は使えない");
        Check.True(N("scenes/../../x.scene").Error is not null, "途中の .. も使えない");
        Check.True(N("assets://").Error is not null, "中身の無い仮想パス");
        var noProject = AndroidScenePath.Normalize(Path.Combine(root, "scenes", "Main.scene"), null);
        Check.True(noProject.Error!.Contains("相対パス"), "プロジェクトが無いと絶対パスを直せない");

        Check.Equal("assets://scenes/Main.scene", AndroidScenePath.ToVirtualPath("scenes/Main.scene"), "エンジンの仮想パス");
        using var temp = new TempDir();
        temp.WriteFile("assets/scenes/Main.scene", "{}");
        Check.True(AndroidScenePath.ExistsUnder(temp.Combine("assets"), "scenes/Main.scene"), "ある");
        Check.True(!AndroidScenePath.ExistsUnder(temp.Combine("assets"), "scenes/None.scene"), "無い");
    }

    /// <summary>am start の extra。</summary>
    private static void BuildsAmStartExtras()
    {
        const string component = "com.seedengine.runtime/com.seedengine.runtime.MainActivity";
        Check.Equal("shell am start -W -n " + component,
            string.Join(" ", AdbClient.AmStartArguments(component, LaunchStep.LaunchExtras(null))), "シーンが無ければ従来の引数");
        Check.Equal(0, LaunchStep.LaunchExtras("  ").Count, "空白だけも extra なし");

        var extras = LaunchStep.LaunchExtras("scenes/Stage 2.scene");
        Check.Equal(AndroidRuntimeContract.SceneExtraName, extras.Single().Key, "キーは seed.scene");
        Check.Equal("seed.scene", AndroidRuntimeContract.SceneExtraName, "端末側（MainActivity の接頭辞 seed. ＋ JSON のキー scene）と一致");
        var arguments = AdbClient.AmStartArguments(component, extras);
        Check.Equal("shell|am|start|-W|-n|" + component + "|--es|seed.scene|'scenes/Stage 2.scene'", string.Join("|", arguments),
            "--es キー '値'（adb shell は引数をつないで端末のシェルに渡すので値を引用する）");

        Check.Equal("'It'\\''s ステージ $HOME `x`'", AdbClient.ShellQuote("It's ステージ $HOME `x`"), "' は '\\'' に、$ や ` は単一引用符の中でそのまま");
    }

    /// <summary>pak のエントリ名。</summary>
    private static void ReadsPakEntries()
    {
        using var temp = new TempDir();
        var pak = temp.Combine("assets.pak");
        File.WriteAllBytes(pak, BuildPak(new[] { ("scenes/Main.scene", "{}"), ("シーン/森 の 2.scene", "{\"a\":1}"), ("models/A.glb", "glb") }));
        var entries = PakEntryIndex.ReadEntryPaths(pak);
        Check.Equal("scenes/Main.scene|シーン/森 の 2.scene|models/A.glb", string.Join("|", entries), "書かれた順・表記のまま");
        Check.True(PakEntryIndex.Contains(entries, "SCENES\\main.scene"), "区切りと大小文字を問わない（端末と同じ）");
        Check.True(PakEntryIndex.Contains(entries, "シーン/森 の 2.scene"), "日本語・空白");
        Check.True(!PakEntryIndex.Contains(entries, "scenes/Second.scene"), "無いシーン");

        File.WriteAllBytes(temp.Combine("bad.pak"), System.Text.Encoding.ASCII.GetBytes("NOPE\u0001\0\0\0"));
        Throws<InvalidDataException>(() => PakEntryIndex.ReadEntryPaths(temp.Combine("bad.pak")), "目印が違う");
        var truncated = BuildPak(new[] { ("scenes/Main.scene", "{}") });
        File.WriteAllBytes(temp.Combine("cut.pak"), truncated.Take(20).ToArray());
        Throws<InvalidDataException>(() => PakEntryIndex.ReadEntryPaths(temp.Combine("cut.pak")), "途中で切れている");
    }

    /// <summary>テスト用の pak（runtime/src/engine/pak の形式どおり）を組み立てる。</summary>
    private static byte[] BuildPak(IReadOnlyList<(string Path, string Content)> entries)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8);
        writer.Write(System.Text.Encoding.ASCII.GetBytes("SEED"));
        writer.Write(1u);
        writer.Write((uint)entries.Count);
        var tableBytes = entries.Sum(e => 4 + System.Text.Encoding.UTF8.GetByteCount(e.Path) + 16);
        var offset = 12L + tableBytes;
        foreach (var (path, content) in entries)
        {
            var name = System.Text.Encoding.UTF8.GetBytes(path);
            var size = System.Text.Encoding.UTF8.GetByteCount(content);
            writer.Write((uint)name.Length);
            writer.Write(name);
            writer.Write((ulong)offset);
            writer.Write((ulong)size);
            offset += size;
        }
        foreach (var (_, content) in entries) writer.Write(System.Text.Encoding.UTF8.GetBytes(content));
        writer.Flush();
        return stream.ToArray();
    }

    /// <summary>Windows のコマンドライン。</summary>
    private static void QuotesWindowsCommandLine()
    {
        Check.Equal("abc", WindowsCommandLine.QuoteArgument("abc"), "そのまま");
        Check.Equal("\"\"", WindowsCommandLine.QuoteArgument(string.Empty), "空は \"\"");
        Check.Equal("\"a b\"", WindowsCommandLine.QuoteArgument("a b"), "空白");
        Check.Equal(@"a\\\b", WindowsCommandLine.QuoteArgument(@"a\\\b"), "\" の無い \\ はそのまま");
        Check.Equal("\"C:\\dir with space\\\\\"", WindowsCommandLine.QuoteArgument("C:\\dir with space\\"), "末尾の \\ は 2 倍");
        Check.Equal("\"a\\\"b\"", WindowsCommandLine.QuoteArgument("a\"b"), "\" は \\\"");

        if (!OperatingSystem.IsWindows()) return;
        var tricky = new[] { "simple", "with space", "trailing\\", "quote\"inside", "back\\\\slashes\\\\", string.Empty, "日本語 パス", "a\\\\\"b" };
        var line = WindowsCommandLine.Build(@"C:\tools\emulator.exe", tricky);
        var parsed = CommandLineToArgs(line);
        Check.Equal(@"C:\tools\emulator.exe", parsed[0], "argv[0]");
        Check.Equal(string.Join("|", tricky), string.Join("|", parsed.Skip(1)), $"CommandLineToArgvW で元に戻る: {line}");
    }

    /// <summary>切り離した起動。</summary>
    private static void DetachedProcessReportsExit()
    {
        if (!OperatingSystem.IsWindows()) return;
        var cmd = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
        using var process = DetachedProcess.Start(cmd, new[] { "/c", "exit", "3" }, workingDirectory: null);
        Check.True(process.Id > 0, "プロセス ID");
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!process.HasExited && DateTime.UtcNow < deadline) Thread.Sleep(20);
        Check.True(process.HasExited, "終わったことが分かる");
        Check.Equal(3, process.ExitCode, "終了コード");

        var missing = Throws<ChildProcessStartException>(
            () => DetachedProcess.Start(Path.Combine(Path.GetTempPath(), "no_such_tool_c3.exe"), Array.Empty<string>(), null), "無い実行ファイル");
        Check.True(missing.Message.Contains("起動できません"), $"理由: {missing.Message}");
    }

    /// <summary>emulator.exe の場所。</summary>
    private static void FindsEmulatorInSdk()
    {
        using var temp = new TempDir();
        temp.WriteFile("sdk/platform-tools/adb.exe", "");
        var env = new Dictionary<string, string?> { ["ANDROID_HOME"] = temp.Combine("sdk"), ["PATH"] = string.Empty };
        var without = AndroidToolchain.Detect(name => env.GetValueOrDefault(name));
        var error = Throws<AndroidPipelineException>(() => without.RequireEmulator(), "emulator が無い");
        Check.Equal(AndroidFailureKind.Toolchain, error.Kind, "道具の失敗");
        Check.True(error.Message.Contains("Android Emulator") && error.Message.Contains("SDK Manager"), $"対処: {error.Message}");

        temp.WriteFile("sdk/emulator/emulator.exe", "");
        var with = AndroidToolchain.Detect(name => env.GetValueOrDefault(name));
        Check.Equal(temp.Combine("sdk/emulator/emulator.exe"), with.RequireEmulator(), "SDK の emulator\\emulator.exe");
    }

    /// <summary>SeedAndroid の引数。</summary>
    private static void ParsesAutoSceneAndAvd()
    {
        var parsed = SeedAndroidArguments.Parse(new[] { "run", "--project", "P", "--serial", "auto", "--scene", "scenes/Stage 2.scene", "--avd", "wop" });
        Check.True(parsed.Error is null, $"解釈できる: {parsed.Error}");
        var request = SeedAndroidArguments.ToRequest(parsed.CommandLine!, null);
        Check.Equal("auto", request.Serial, "--serial auto");
        Check.Equal("scenes/Stage 2.scene", request.ScenePath, "--scene");
        Check.Equal("wop", request.Avd, "--avd");
        Check.True(!request.EmulatorFallback, "CLI のシリアル指定は切り替えない（従来どおり）");

        foreach (var command in new[] { "stop", "logcat" })
        {
            var bad = SeedAndroidArguments.Parse(new[] { command, "--serial", "auto" });
            Check.True(bad.Error is not null && bad.Error.Contains("auto"), $"{command} では auto を使えない: {bad.Error}");
        }
        Check.True(SeedAndroidArguments.Parse(new[] { "build", "--serial", "auto" }).Error is null, "build では使える（端末は起動しない）");
        Check.True(SeedAndroidArguments.Parse(new[] { "run", "--scene" }).Error is not null, "--scene には値が要る");

        using var temp = new TempDir();
        var configPath = temp.WriteFile("cfg/run.json",
            "{ \"project\": \"../Game\", \"serial\": \"auto\", \"scene\": \"scenes/Main.scene\", \"avd\": \"wop\", \"emulator_fallback\": true }");
        var config = RunRequestConfig.Load(configPath, out var error)!;
        Check.True(error is null, $"読める: {error}");
        Check.Equal("scenes/Main.scene", config.ScenePath, "scene はアセットルートからの相対のまま（JSON のフォルダからの絶対パスにしない）");
        Check.Equal("wop", config.Avd, "avd");
        Check.True(config.EmulatorFallback, "emulator_fallback");
        var merged = SeedAndroidArguments.ToRequest(SeedAndroidArguments.Parse(new[] { "run", "--scene", "scenes/B.scene" }).CommandLine!, config);
        Check.Equal("scenes/B.scene", merged.ScenePath, "コマンドラインが優先");
        Check.Equal("wop", merged.Avd, "指定の無いものは設定 JSON");
    }

    // ── Win32（テストの確かめ用）────────────────────────────

    /// <summary>コマンドラインを Windows の規則で分ける（CommandLineToArgvW）。</summary>
    private static string[] CommandLineToArgs(string commandLine)
    {
        var argv = CommandLineToArgvW(commandLine, out var count);
        if (argv == IntPtr.Zero) throw new AssertionException("CommandLineToArgvW が失敗しました");
        try
        {
            var result = new string[count];
            for (var i = 0; i < count; i++)
            {
                result[i] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, i * IntPtr.Size)) ?? string.Empty;
            }
            return result;
        }
        finally
        {
            LocalFree(argv);
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CommandLineToArgvW(string commandLine, out int count);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
