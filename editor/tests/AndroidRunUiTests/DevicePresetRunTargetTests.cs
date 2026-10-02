using System.Globalization;
using SEEDEditor.Android.State;
using SEEDEditor.AndroidRun;
using SEEDEditor.DevicePresets;
using SEEDEditor.Runtime;
using SpriteRigTests;

namespace AndroidRunUiTests;

/// <summary>
/// 実行先「PC（端末の模擬: …）」の行の並び・選択の復元・プレイバー・起動の環境変数と引数・常駐 Play の使い回し・
/// 画面に収まるか（docs/editor_device_presets.md）。
/// </summary>
public static class DevicePresetRunTargetTests
{
    /// <summary>テストを登録する。</summary>
    /// <param name="harness">テストランナー。</param>
    public static void Register(TestHarness harness)
    {
        harness.Add("一覧（端末の模擬）: PC の直後にプリセットの順で「PC（端末の模擬: 名前）」の行・Id は pcsim:<id>・選べる・Android の行ではない・ツールチップに窓・倍率・安全領域", ListsPresetRowsAfterPc);
        harness.Add("一覧（端末の模擬）: Android を使えない環境でも PC・端末の模擬・理由の行（端末の模擬を選んでいれば戻す）", PresetRowsWithoutAndroid);
        harness.Add("選択（端末の模擬）: 起動時・取り直しとも一覧にあればその行（大文字小文字は問わない）・JSON から消えていれば PC・ほかの行の選び方は変わらない", RestoresPresetSelection);
        harness.Add("選択の記録（端末の模擬）: editor_target に pcsim:<id> を書いて読み戻す・起動時に端末の一覧を取りに行かない", StoresPresetSelection);
        harness.Add("プレイバー（端末の模擬）: PC と同じ動きで、実行ボタンと実行先のツールチップに「模擬: Pixel 6a 半分（540×1200・×1.3125）」（PC の実行中も）・PC の行は従来のまま", PlayBarShowsSimulationNote);
        harness.Add("起動の環境変数: Pixel 6a 実寸 → 窓 1080x2400・倍率 2.625・安全領域 0,132,0,63・キーボード 979・--render-quality=mobile（カルチャに依らない）", BuildsEnvironmentForFullSize);
        harness.Add("起動の環境変数: キーボード 0 は変数を消す・品質の指定なしは引数なし・エディタの環境の同じ名前は上書き／ほかの変数は残す・引数は従来の後ろへ", AppliesOverridesToStartInfo);
        harness.Add("起動の環境変数: Key は同じ条件なら同じ・違えば違う・空白や引用符の入った引数と名前の無い変数は受け付けない", LaunchKeyIdentity);
        harness.Add("常駐 Play の使い回し: 生きていて・シーンが決まっていて・起動の条件の Key が同じときだけ／埋め込みの前に閉じるのは模擬で起動したものだけ", ReusePolicy);
        harness.Add("画面に収まるか: 収まれば警告なし（ちょうども収まる）・はみ出せば端末の名前・窓・使える範囲を出す", ScreenFit);
    }

    // ── 材料 ─────────────────────────────────────────────

    /// <summary>テスト用の端末（Pixel 6a 実寸と同じ値）。</summary>
    private static DevicePreset FullSize() => new()
    {
        Id = "pixel6a-full",
        Name = "Pixel 6a 実寸",
        WidthPx = 1080,
        HeightPx = 2400,
        ScaleFactor = 2.625,
        SafeArea = new DeviceSafeArea(0, 132, 0, 63),
        KeyboardHeightPx = 979,
        RenderQuality = "mobile",
        Description = "実寸の説明",
    };

    /// <summary>テスト用の端末（組み込みの Pixel 6a 半分）。</summary>
    private static DevicePreset Half() => DevicePresetCatalog.BuiltInPreset;

    /// <summary>キーボードも品質も指定しない端末。</summary>
    private static DevicePreset Plain() => new()
    {
        Id = "plain",
        Name = "素の端末",
        WidthPx = 400,
        HeightPx = 800,
        ScaleFactor = 1.0,
        SafeArea = DeviceSafeArea.None,
        KeyboardHeightPx = 0,
    };

    /// <summary>一覧を組み立てる。</summary>
    private static RunTargetCatalog Build(
        bool androidAvailable, string? preferredId, RunTargetSelectionMode mode, params DevicePreset[] presets) =>
        RunTargetCatalogBuilder.Build(new RunTargetCatalogInput
        {
            Android = androidAvailable ? AndroidTargetAvailability.Available : AndroidTargetAvailability.Unavailable("SDK がありません"),
            Devices = androidAvailable ? new[] { Fixtures.ReadyPhone() } : null,
            PreferredId = preferredId,
            Mode = mode,
            DevicePresets = presets,
        });

    // ── 行の並び ─────────────────────────────────────────

    /// <summary>PC の直後にプリセットの行。</summary>
    private static void ListsPresetRowsAfterPc()
    {
        var catalog = Build(androidAvailable: true, preferredId: null, RunTargetSelectionMode.Keep, FullSize(), Half());
        Check.Equal("pc,pcsim:pixel6a-full,pcsim:pixel6a-half,auto," + Fixtures.PhoneSerial,
            string.Join(",", catalog.Entries.Select(entry => entry.Id)), "PC → 端末の模擬（プリセットの順）→ Android（自動）→ 端末");
        Check.Equal(RunTargetEntry.PcId, catalog.Selected.Id, "記録が無ければ PC（勝手に模擬を選ばない）");

        var half = catalog.Entries[2];
        Check.Equal(RunTargetKind.PcSimulated, half.Kind, "種類");
        Check.Equal("PC（端末の模擬: Pixel 6a 半分）", half.Text, "文言");
        Check.Equal("Pixel 6a 半分", half.Name, "名前");
        Check.Equal(DevicePresetRunTargets.IconKey, half.IconKey, "アイコン");
        Check.Equal("Icon.Platform.DeviceSimulation", half.IconKey, "アイコンのキー（Icons.xaml）");
        Check.True(half.CanRun, "いつでも選べる");
        Check.True(half.IsPcSimulated && !half.IsAndroid && !half.IsAndroidAuto, "PC の仲間（Android の行ではない）");
        Check.Equal(Half(), half.DevicePreset, "端末プリセットを持つ");
        Check.True(half.Serial is null && half.BuildAbi is null, "端末の情報は持たない");
        foreach (var part in new[] { "540×1200 px", "411×914 dp", "×1.3125", "上 66", "下 32", "高さ 490 px", "mobile", "別ウィンドウ" })
        {
            Check.True(half.ToolTip.Contains(part), $"ツールチップに「{part}」: {half.ToolTip}");
        }
        Check.True(half.ToolTip.EndsWith(Half().Description), "説明はツールチップの最後");

        var plain = DevicePresetRunTargets.FromPreset(Plain());
        Check.True(plain.ToolTip.Contains("キーボードの模擬: なし") && plain.ToolTip.Contains("指定しない"),
            $"キーボード・品質の指定が無い端末の表記: {plain.ToolTip}");

        var none = Build(androidAvailable: true, preferredId: null, RunTargetSelectionMode.Keep);
        Check.True(none.Entries.All(entry => !entry.IsPcSimulated), "プリセットを渡さなければ従来どおりの一覧");
    }

    /// <summary>Android を使えない環境。</summary>
    private static void PresetRowsWithoutAndroid()
    {
        var catalog = Build(androidAvailable: false, preferredId: "pcsim:pixel6a-half", RunTargetSelectionMode.Restore, FullSize(), Half());
        Check.Equal("pc,pcsim:pixel6a-full,pcsim:pixel6a-half," + RunTargetCatalogBuilder.UnavailableNoticeId,
            string.Join(",", catalog.Entries.Select(entry => entry.Id)), "PC・端末の模擬・理由の行（Android（自動）と端末は出さない）");
        Check.Equal("pcsim:pixel6a-half", catalog.Selected.Id, "端末の模擬は Android を使えなくても戻す");
    }

    // ── 選択 ─────────────────────────────────────────────

    /// <summary>起動時の復元と取り直し。</summary>
    private static void RestoresPresetSelection()
    {
        foreach (var mode in new[] { RunTargetSelectionMode.Restore, RunTargetSelectionMode.Keep })
        {
            var exact = Build(androidAvailable: true, "pcsim:pixel6a-full", mode, FullSize(), Half());
            Check.Equal("pcsim:pixel6a-full", exact.Selected.Id, $"{mode}: 一覧にあればその行");
            Check.True(exact.Entries.Contains(exact.Selected), $"{mode}: 選んだ行は一覧の中にある");

            var upper = Build(androidAvailable: true, "PCSIM:Pixel6A-Half", mode, FullSize(), Half());
            Check.Equal("pcsim:pixel6a-half", upper.Selected.Id, $"{mode}: 大文字小文字は問わない");

            var removed = Build(androidAvailable: true, "pcsim:deleted", mode, FullSize(), Half());
            Check.Equal(RunTargetEntry.PcId, removed.Selected.Id, $"{mode}: JSON から消えた端末は PC");
            Check.True(removed.Entries.All(entry => !entry.IsMissing), $"{mode}: 「未接続」の行は作らない（端末の行ではない）");

            // ほかの行の選び方は端末の模擬があっても変わらない
            Check.Equal(RunTargetEntry.AndroidAutoId, Build(androidAvailable: true, "auto", mode, Half()).Selected.Id, $"{mode}: auto");
            Check.Equal(Fixtures.PhoneSerial, Build(androidAvailable: true, Fixtures.PhoneSerial, mode, Half()).Selected.Id, $"{mode}: 端末");
            Check.Equal(RunTargetEntry.PcId, Build(androidAvailable: true, "pc", mode, Half()).Selected.Id, $"{mode}: pc");
        }
    }

    /// <summary>選択の記録。</summary>
    private static void StoresPresetSelection()
    {
        using var temp = new AndroidPipelineTests.TempDir();
        var id = DevicePresetRunTargets.TargetId(Half());
        Check.Equal("pcsim:pixel6a-half", id, "識別子");
        Check.True(RunTargetSelectionStore.Save(temp.Path, id), "書いた");
        Check.Equal(id, AndroidRunState.Load(AndroidRunState.PathForProject(temp.Path)).EditorTarget, "run_state.json の editor_target");

        var memory = RunTargetSelectionStore.Load(temp.Path);
        Check.Equal(id, memory.PreferredId, "読み戻す");
        Check.True(!memory.PrefersAndroidDevice, "端末の一覧を取りに行かない（adb を起こさない）");
        Check.True(new RunTargetMemory("PCSIM:x", null) is { PrefersAndroidDevice: false }, "大文字でも端末の模擬");
        Check.True(new RunTargetMemory(Fixtures.PhoneSerial, null).PrefersAndroidDevice, "端末のシリアルは従来どおり一覧を取りに行く");
        Check.True(DevicePresetRunTargets.IsTargetId(id) && !DevicePresetRunTargets.IsTargetId("pc")
                   && !DevicePresetRunTargets.IsTargetId(Fixtures.PhoneSerial) && !DevicePresetRunTargets.IsTargetId(null),
            "識別子の見分け");

        // 記録から一覧へ戻す（起動時の流れと同じ）
        var restored = Build(androidAvailable: true, memory.PreferredId, RunTargetSelectionMode.Restore, FullSize(), Half());
        Check.Equal(id, restored.Selected.Id, "起動時に選び直される");
    }

    // ── プレイバー ───────────────────────────────────────

    /// <summary>模擬の注記。</summary>
    private static void PlayBarShowsSimulationNote()
    {
        const string note = "模擬: Pixel 6a 半分（540×1200・×1.3125）";
        var target = DevicePresetRunTargets.FromPreset(Half());
        Check.Equal(note, DevicePresetFormat.SimulationNote(Half()), "注記の文言");

        var edit = PlayBarPolicy.Compute(new PlayBarInput(EditorState.Edit, target, AndroidRunSnapshot.Idle));
        Check.Equal(PlayBarAction.TogglePc, edit.PlayAction, "実行ボタンは PC の Play（OnPlayPause が別ウィンドウで起動する）");
        Check.Equal(StopBarAction.None, edit.StopAction, "停止は押せない（PC の Edit と同じ）");
        Check.Equal("EDIT", edit.StateLabel, "状態表示は PC のまま");
        Check.True(edit.PlayToolTip.StartsWith(PlayBarPolicy.PcPlayToolTip) && edit.PlayToolTip.EndsWith(note), $"実行のツールチップ: {edit.PlayToolTip}");
        Check.True(edit.TargetSelectorEnabled && edit.TargetSelectorToolTip.EndsWith(note), $"実行先のツールチップ: {edit.TargetSelectorToolTip}");

        var play = PlayBarPolicy.Compute(new PlayBarInput(EditorState.Play, target, AndroidRunSnapshot.Idle));
        Check.Equal(PlayBarAction.TogglePc, play.PlayAction, "Play 中は一時停止（PC と同じ）");
        Check.Equal(StopBarAction.StopPc, play.StopAction, "停止は PC の停止");
        Check.True(!play.TargetSelectorEnabled, "実行中は実行先を変えられない");
        Check.True(play.PlayToolTip.EndsWith(note) && play.TargetSelectorToolTip.Contains("PC で実行中") && play.TargetSelectorToolTip.EndsWith(note),
            $"実行中も注記: {play.PlayToolTip} / {play.TargetSelectorToolTip}");

        var pc = PlayBarPolicy.Compute(new PlayBarInput(EditorState.Edit, RunTargetCatalogBuilder.Pc, AndroidRunSnapshot.Idle));
        Check.Equal(PlayBarPolicy.PcPlayToolTip, pc.PlayToolTip, "PC の行の実行のツールチップは従来のまま");
        Check.Equal(PlayBarPolicy.TargetSelectorToolTip, pc.TargetSelectorToolTip, "PC の行の実行先のツールチップは注記なし");
    }

    // ── 起動の環境変数と引数 ────────────────────────────────

    /// <summary>Pixel 6a 実寸の環境変数と引数。</summary>
    private static void BuildsEnvironmentForFullSize()
    {
        // 小数点がカンマのカルチャでも "2.625"（ランタイムは "." で読む）
        var saved = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            var overrides = DevicePresetLaunchEnvironment.Build(FullSize());
            Check.Equal(
                "SEED_SIM_WINDOW_SIZE=1080x2400|SEED_SIM_SCALE_FACTOR=2.625|SEED_SIM_SAFE_AREA=0,132,0,63|SEED_SIM_KEYBOARD_HEIGHT=979",
                string.Join("|", overrides.Variables.Select(variable => $"{variable.Name}={variable.Value}")), "環境変数（並びも）");
            Check.Equal("--render-quality=mobile", string.Join(" ", overrides.Arguments), "起動引数");
            Check.Equal("端末の模擬: Pixel 6a 実寸", overrides.Label, "ログの説明");

            var half = DevicePresetLaunchEnvironment.Build(Half());
            Check.Equal("1.3125", half.Variables.Single(variable => variable.Name == DevicePresetLaunchEnvironment.ScaleFactorVariable).Value,
                "半分の倍率");
            Check.Equal("540x1200", half.Variables.Single(variable => variable.Name == DevicePresetLaunchEnvironment.WindowSizeVariable).Value,
                "半分の窓");
            Check.Equal("模擬: Pixel 6a 半分（540×1200・×1.3125）", DevicePresetFormat.SimulationNote(Half()), "注記もカルチャに依らない");
        }
        finally
        {
            CultureInfo.CurrentCulture = saved;
        }
    }

    /// <summary>子プロセスの環境と引数への当て方。</summary>
    private static void AppliesOverridesToStartInfo()
    {
        var overrides = DevicePresetLaunchEnvironment.Build(Plain());
        Check.True(overrides.Variables.Single(variable => variable.Name == DevicePresetLaunchEnvironment.KeyboardHeightVariable).Value is null,
            "キーボード 0 は消す変数（値 null）");
        Check.Equal(0, overrides.Arguments.Count, "品質の指定が無ければ引数なし");
        Check.Equal("--mode=play --pipe=p", overrides.AppendArguments("--mode=play --pipe=p"), "足す引数が無ければ従来の引数のまま");

        // エディタ自身の環境から受け継いだ値（手で付けて起動していたときの名残）を、プリセットの値で上書き・消す
        var startInfo = new System.Diagnostics.ProcessStartInfo();
        startInfo.Environment[DevicePresetLaunchEnvironment.WindowSizeVariable] = "1x1";
        startInfo.Environment[DevicePresetLaunchEnvironment.KeyboardHeightVariable] = "300";
        startInfo.Environment["SEED_HEADLESS"] = "1";
        overrides.ApplyEnvironment(startInfo.Environment);
        Check.Equal("400x800", startInfo.Environment[DevicePresetLaunchEnvironment.WindowSizeVariable], "窓は上書き");
        Check.Equal("1", startInfo.Environment[DevicePresetLaunchEnvironment.ScaleFactorVariable], "倍率 1 は \"1\"");
        Check.Equal("0,0,0,0", startInfo.Environment[DevicePresetLaunchEnvironment.SafeAreaVariable], "安全領域 0 も明示する");
        Check.True(!startInfo.Environment.ContainsKey(DevicePresetLaunchEnvironment.KeyboardHeightVariable), "キーボードは消える");
        Check.Equal("1", startInfo.Environment["SEED_HEADLESS"], "ほかの変数（ヘッドレスなど）は残す");

        var full = DevicePresetLaunchEnvironment.Build(FullSize());
        Check.Equal("--mode=play --pipe=p --parent-pid=1 --render-quality=mobile",
            full.AppendArguments("--mode=play --pipe=p --parent-pid=1"), "従来の引数（既定）の後ろへ足す");
        Check.True(full.Describe().Contains("SEED_SIM_SAFE_AREA=0,132,0,63") && full.Describe().EndsWith("--render-quality=mobile"),
            $"ログの 1 行: {full.Describe()}");
        Check.True(overrides.Describe().Contains("SEED_SIM_KEYBOARD_HEIGHT=(消す)"), $"消す変数のログ: {overrides.Describe()}");
    }

    /// <summary>起動の条件の Key と、入力の検査。</summary>
    private static void LaunchKeyIdentity()
    {
        Check.Equal(DevicePresetLaunchEnvironment.Build(Half()).Key, DevicePresetLaunchEnvironment.Build(Half()).Key, "同じ端末なら同じ Key");
        Check.True(DevicePresetLaunchEnvironment.Build(Half()).Key != DevicePresetLaunchEnvironment.Build(FullSize()).Key, "違う端末なら違う Key");
        var changedQuality = Half() with { RenderQuality = "desktop" };
        Check.True(DevicePresetLaunchEnvironment.Build(Half()).Key != DevicePresetLaunchEnvironment.Build(changedQuality).Key, "品質だけ違っても違う Key");
        var renamed = Half() with { Name = "名前だけ違う" };
        Check.Equal(DevicePresetLaunchEnvironment.Build(Half()).Key, DevicePresetLaunchEnvironment.Build(renamed).Key,
            "名前だけ違う端末は同じ条件（同じ Key。プロセスを使い回してよい）");

        void ExpectRejected(Action create, string what)
        {
            try
            {
                create();
            }
            catch (ArgumentException)
            {
                return;
            }
            throw new AssertionException($"{what}: ArgumentException になるはず");
        }
        var noVariables = Array.Empty<RuntimeLaunchVariable>();
        ExpectRejected(() => _ = new RuntimeLaunchOverrides("x", noVariables, new[] { "--a=b c" }), "空白入りの引数");
        ExpectRejected(() => _ = new RuntimeLaunchOverrides("x", noVariables, new[] { "--a=\"b\"" }), "引用符入りの引数");
        ExpectRejected(() => _ = new RuntimeLaunchOverrides("x", noVariables, new[] { string.Empty }), "空の引数");
        ExpectRejected(() => _ = new RuntimeLaunchOverrides("x", new[] { new RuntimeLaunchVariable(" ", "1") }, Array.Empty<string>()), "名前の無い変数");
    }

    // ── 常駐 Play の使い回し ────────────────────────────────

    /// <summary>使い回しと、埋め込みの前に閉じる判断。</summary>
    private static void ReusePolicy()
    {
        const string scene = "assets://scenes/a.scene";
        var halfKey = DevicePresetLaunchEnvironment.Build(Half()).Key;
        var fullKey = DevicePresetLaunchEnvironment.Build(FullSize()).Key;

        Check.True(PlayRuntimeReusePolicy.CanReuse(true, scene, null, null), "従来どおり: 上書きなし同士は使い回す");
        Check.True(PlayRuntimeReusePolicy.CanReuse(true, scene, halfKey, halfKey), "同じ端末の模擬は使い回す");
        Check.True(!PlayRuntimeReusePolicy.CanReuse(true, scene, halfKey, fullKey), "違う端末の模擬は使い回さない");
        Check.True(!PlayRuntimeReusePolicy.CanReuse(true, scene, halfKey, null), "模擬の常駐を従来の別ウィンドウ Play に使い回さない");
        Check.True(!PlayRuntimeReusePolicy.CanReuse(true, scene, null, halfKey), "従来の常駐を模擬に使い回さない");
        Check.True(!PlayRuntimeReusePolicy.CanReuse(false, scene, halfKey, halfKey), "消えていれば使い回さない");
        Check.True(!PlayRuntimeReusePolicy.CanReuse(true, null, null, null) && !PlayRuntimeReusePolicy.CanReuse(true, string.Empty, halfKey, halfKey),
            "シーンが決まっていなければ（開始シーンから）使い回さない（従来どおり）");

        Check.True(PlayRuntimeReusePolicy.MustReleaseBeforeEmbeddedPlay(true, halfKey), "埋め込みに戻るときは模擬の常駐を閉じる");
        Check.True(!PlayRuntimeReusePolicy.MustReleaseBeforeEmbeddedPlay(true, null), "従来の常駐は残す（従来の振る舞い）");
        Check.True(!PlayRuntimeReusePolicy.MustReleaseBeforeEmbeddedPlay(false, null), "常駐が無ければ何もしない");
    }

    // ── 画面に収まるか ────────────────────────────────────

    /// <summary>窓が画面に収まるかの警告。</summary>
    private static void ScreenFit()
    {
        Check.True(DevicePresetScreenFit.Warn(Half(), 1920, 1200) is null, "収まれば警告なし");
        Check.True(DevicePresetScreenFit.Warn(Half(), 540, 1200) is null, "ちょうどの大きさは収まる");

        var warning = DevicePresetScreenFit.Warn(FullSize(), 1920, 1009);
        Check.True(warning is not null, "1080×2400 は 1920×1009 に収まらない");
        foreach (var part in new[] { "Pixel 6a 実寸", "1080×2400", "1920×1009" })
        {
            Check.True(warning!.Contains(part), $"警告に「{part}」: {warning}");
        }
        Check.True(DevicePresetScreenFit.Warn(Half(), 539, 2000) is not null, "幅だけはみ出しても警告");
    }
}
