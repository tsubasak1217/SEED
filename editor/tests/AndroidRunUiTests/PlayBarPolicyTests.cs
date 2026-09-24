using SEEDEditor.Android.Adb;
using SEEDEditor.AndroidRun;
using SEEDEditor.Runtime;
using SpriteRigTests;

namespace AndroidRunUiTests;

/// <summary>プレイバー（実行・停止ボタン・実行先セレクタ・状態表示・進捗）の判断と PC との排他。</summary>
public static class PlayBarPolicyTests
{
    /// <summary>テストを登録する。</summary>
    /// <param name="harness">テストランナー。</param>
    public static void Register(TestHarness harness)
    {
        harness.Add("PC: 状態ごとのボタン・絵柄・状態表示が従来の ApplyUiState と同じ", PcTableMatchesLegacy);
        harness.Add("PC の実行中（Launching / Play / Pause）は実行先を変えられない・それ以外は変えられる", SelectorLockedWhilePcRuns);
        harness.Add("実行先 Android・何も動いていない: 実行＝その端末で実行・停止は押せない・状態表示は PC のまま", AndroidTargetIdle);
        harness.Add("実行先 Android: 選べない端末（未許可・未接続）は理由をツールチップに出して押せない", AndroidTargetNotRunnable);
        harness.Add("実行先 Android でも PC の実行中は PC の一時停止・停止を優先し、Android は始めない", PcRunTakesPrecedence);
        harness.Add("PC のランタイムのビルド中・待機中でも Android の実行は始められる", AndroidStartsWhilePcBuildingOrIdle);
        harness.Add("Android のビルド中: 実行は押せず、停止＝ビルドの中止、実行先は変えられない、進捗を出す", AndroidBuilding);
        harness.Add("Android の実行中: 一時停止の絵柄で押せない（理由付き）、停止＝アプリを止める", AndroidRunning);
        harness.Add("Android の停止中: 実行も停止も押せない", AndroidStopping);
        harness.Add("Android の実行中は実行先が PC でも Android の表示が優先（PC の Play を始めさせない）", AndroidActiveOverridesPcTarget);
        harness.Add("進捗の文言: 工程の数が分かる前は「準備中…」、分かれば「% [i/n] 工程名」", ProgressText);
    }

    /// <summary>材料を作る。</summary>
    private static PlayBarInput Input(EditorState pc, RunTargetEntry? target = null, AndroidRunSnapshot? android = null) =>
        new(pc, target ?? RunTargetCatalogBuilder.Pc, android ?? AndroidRunSnapshot.Idle);

    /// <summary>PC の状態ごとの表示。</summary>
    private static void PcTableMatchesLegacy()
    {
        // (状態, 実行可, 絵柄, 停止可, 文言, 色, アイコン) — 変更前の MainWindow.Camera.cs ApplyUiState の値
        var legacy = new (EditorState State, bool Play, PlayGlyph Glyph, bool Stop, string Label, PlayBarTone Tone, string Icon)[]
        {
            (EditorState.Edit,      true,  PlayGlyph.Play,  false, "EDIT",         PlayBarTone.Edit,    "Icon.Dirty"),
            (EditorState.Play,      true,  PlayGlyph.Pause, true,  "PLAY",         PlayBarTone.Running, "Icon.Play"),
            (EditorState.Pause,     true,  PlayGlyph.Play,  true,  "PAUSE",        PlayBarTone.Paused,  "Icon.Pause"),
            (EditorState.Building,  false, PlayGlyph.Play,  false, "BUILDING...",  PlayBarTone.Busy,    "Icon.Settings"),
            (EditorState.Launching, false, PlayGlyph.Play,  true,  "LAUNCHING...", PlayBarTone.Running, "Icon.Play"),
            (EditorState.Idle,      false, PlayGlyph.Play,  false, "IDLE",         PlayBarTone.Idle,    "Icon.Info"),
        };
        foreach (var row in legacy)
        {
            var view = PlayBarPolicy.Compute(Input(row.State));
            Check.Equal(row.Play, view.PlayEnabled, $"{row.State}: 実行ボタン");
            Check.Equal(row.Play ? PlayBarAction.TogglePc : PlayBarAction.None, view.PlayAction, $"{row.State}: 実行ボタンの動き");
            Check.Equal(row.Glyph, view.PlayGlyph, $"{row.State}: 絵柄");
            Check.Equal(row.Stop, view.StopEnabled, $"{row.State}: 停止ボタン");
            Check.Equal(row.Stop ? StopBarAction.StopPc : StopBarAction.None, view.StopAction, $"{row.State}: 停止ボタンの動き");
            Check.Equal(row.Label, view.StateLabel, $"{row.State}: 状態表示");
            Check.Equal(row.Tone, view.StateTone, $"{row.State}: 色");
            Check.Equal(row.Icon, view.StateIconKey, $"{row.State}: アイコン");
            Check.Equal(PlayBarPolicy.PcPlayToolTip, view.PlayToolTip, $"{row.State}: 実行のツールチップ（従来の値）");
            Check.Equal(PlayBarPolicy.PcStopToolTip, view.StopToolTip, $"{row.State}: 停止のツールチップ（従来の値）");
            Check.True(view.ProgressText is null && view.ProgressFraction is null, $"{row.State}: 進捗は出さない");
        }
    }

    /// <summary>PC の実行中の実行先セレクタ。</summary>
    private static void SelectorLockedWhilePcRuns()
    {
        foreach (var state in Enum.GetValues<EditorState>())
        {
            var locked = state is EditorState.Launching or EditorState.Play or EditorState.Pause;
            Check.Equal(!locked, PlayBarPolicy.Compute(Input(state)).TargetSelectorEnabled, $"{state}: 実行先を変えられるか（PC）");
            Check.Equal(!locked, PlayBarPolicy.Compute(Input(state, Fixtures.PhoneTarget())).TargetSelectorEnabled, $"{state}: 実行先を変えられるか（Android）");
        }
        Check.True(PlayBarPolicy.Compute(Input(EditorState.Play)).TargetSelectorToolTip.Contains("PC で実行中"), "理由を示す");
    }

    /// <summary>実行先 Android・何も動いていない。</summary>
    private static void AndroidTargetIdle()
    {
        var target = Fixtures.PhoneTarget();
        var view = PlayBarPolicy.Compute(Input(EditorState.Edit, target));
        Check.Equal(PlayBarAction.StartAndroid, view.PlayAction, "実行＝その端末で実行");
        Check.Equal(PlayGlyph.Play, view.PlayGlyph, "再生の絵柄");
        Check.True(view.PlayToolTip.StartsWith("Pixel_6a（実機） で実行"), $"実行先の名前: {view.PlayToolTip}");
        Check.Equal(StopBarAction.None, view.StopAction, "停止は押せない（何も動いていない）");
        Check.Equal("EDIT", view.StateLabel, "状態表示は PC のまま（エディタは編集できる）");
        Check.True(view.TargetSelectorEnabled, "実行先を変えられる");
    }

    /// <summary>選べない端末。</summary>
    private static void AndroidTargetNotRunnable()
    {
        var unauthorized = RunTargetCatalogBuilder.FromDevice(Fixtures.NotReady(Fixtures.PhoneSerial, AdbDeviceState.Unauthorized, "unauthorized"));
        var view = PlayBarPolicy.Compute(Input(EditorState.Edit, unauthorized));
        Check.Equal(PlayBarAction.None, view.PlayAction, "押せない");
        Check.Equal(unauthorized.ToolTip, view.PlayToolTip, "理由（端末の行のツールチップ）");

        var missing = RunTargetCatalogBuilder.Build(new RunTargetCatalogInput
        {
            Android = AndroidTargetAvailability.Available,
            Devices = Array.Empty<SEEDEditor.Android.Pipeline.AndroidDeviceEntry>(),
            PreferredId = Fixtures.PhoneSerial,
            Mode = RunTargetSelectionMode.Keep,
        }).Selected;
        var missingView = PlayBarPolicy.Compute(Input(EditorState.Edit, missing));
        Check.Equal(PlayBarAction.None, missingView.PlayAction, "未接続は押せない");
        Check.True(missingView.PlayToolTip.Contains("見えません"), $"理由: {missingView.PlayToolTip}");
    }

    /// <summary>PC の実行が優先。</summary>
    private static void PcRunTakesPrecedence()
    {
        var target = Fixtures.PhoneTarget();
        var play = PlayBarPolicy.Compute(Input(EditorState.Play, target));
        Check.Equal(PlayBarAction.TogglePc, play.PlayAction, "PC の Play 中は実行ボタン＝PC の一時停止");
        Check.Equal(PlayGlyph.Pause, play.PlayGlyph, "一時停止の絵柄");
        Check.Equal(StopBarAction.StopPc, play.StopAction, "停止＝PC の停止");
        Check.Equal("PLAY", play.StateLabel, "PC の状態表示");

        var launching = PlayBarPolicy.Compute(Input(EditorState.Launching, target));
        Check.Equal(PlayBarAction.None, launching.PlayAction, "PC の起動中は Android を始めない");
        Check.Equal(StopBarAction.StopPc, launching.StopAction, "起動の取り消しは PC の停止");
        Check.True(!PlayBarPolicy.CanStartAndroid(EditorState.Launching), "排他の判定");
    }

    /// <summary>PC のランタイムのビルド中・待機中。</summary>
    private static void AndroidStartsWhilePcBuildingOrIdle()
    {
        var target = Fixtures.PhoneTarget();
        foreach (var state in new[] { EditorState.Building, EditorState.Idle, EditorState.Edit })
        {
            Check.Equal(PlayBarAction.StartAndroid, PlayBarPolicy.Compute(Input(state, target)).PlayAction, $"{state}: Android は始められる");
            Check.True(PlayBarPolicy.CanStartAndroid(state), $"{state}: 排他の判定");
        }
    }

    /// <summary>Android のビルド中。</summary>
    private static void AndroidBuilding()
    {
        var android = new AndroidRunSnapshot
        {
            Phase = AndroidRunPhase.Building, TargetText = "Pixel_6a（実機）", StepTitle = "APK の作成（Gradle）",
            StepIndex = 4, StepCount = 7, Fraction = 3.0 / 7,
        };
        var view = PlayBarPolicy.Compute(Input(EditorState.Edit, Fixtures.PhoneTarget(), android));
        Check.Equal(PlayBarAction.None, view.PlayAction, "実行は押せない");
        Check.True(view.PlayToolTip.Contains("ビルド"), $"理由: {view.PlayToolTip}");
        Check.Equal(StopBarAction.StopAndroid, view.StopAction, "停止＝ビルドの中止");
        Check.True(view.StopToolTip.Contains("中止"), $"停止の説明: {view.StopToolTip}");
        Check.True(!view.TargetSelectorEnabled, "実行先を変えられない");
        Check.Equal(PlayBarPolicy.AndroidBuildingLabel, view.StateLabel, "状態表示");
        Check.Equal(PlayBarTone.Busy, view.StateTone, "ビルド中の色");
        Check.Equal("43% [4/7] APK の作成（Gradle）", view.ProgressText, "進捗の文言");
        Check.Close(3.0 / 7, view.ProgressFraction ?? -1, 1e-9, "進捗の割合");
    }

    /// <summary>Android の実行中。</summary>
    private static void AndroidRunning()
    {
        var android = new AndroidRunSnapshot { Phase = AndroidRunPhase.Running, TargetText = "Pixel_6a（実機）" };
        var view = PlayBarPolicy.Compute(Input(EditorState.Edit, Fixtures.PhoneTarget(), android));
        Check.Equal(PlayBarAction.None, view.PlayAction, "実行ボタンは押せない");
        Check.Equal(PlayGlyph.Pause, view.PlayGlyph, "一時停止の絵柄（Android では無効）");
        Check.True(view.PlayToolTip.Contains("一時停止できません"), $"一時停止できない理由: {view.PlayToolTip}");
        Check.Equal(StopBarAction.StopAndroid, view.StopAction, "停止＝アプリを止める");
        Check.Equal(PlayBarPolicy.AndroidRunningLabel, view.StateLabel, "状態表示");
        Check.Equal("Icon.Platform.Android", view.StateIconKey, "Android のアイコン");
        Check.Equal("Pixel_6a（実機） で実行中", view.ProgressText, "実行中の表示");
        Check.True(view.ProgressFraction is null, "割合は出さない");
    }

    /// <summary>Android の停止中。</summary>
    private static void AndroidStopping()
    {
        var android = new AndroidRunSnapshot { Phase = AndroidRunPhase.Stopping, StopReason = AndroidRunStopReason.User };
        var view = PlayBarPolicy.Compute(Input(EditorState.Edit, Fixtures.PhoneTarget(), android));
        Check.True(!view.PlayEnabled && !view.StopEnabled, "どちらも押せない");
        Check.Equal(PlayBarPolicy.AndroidStoppingLabel, view.StateLabel, "状態表示");
        Check.True(!view.TargetSelectorEnabled, "実行先を変えられない");
    }

    /// <summary>Android の実行中は実行先に関わらず Android の表示。</summary>
    private static void AndroidActiveOverridesPcTarget()
    {
        var android = new AndroidRunSnapshot { Phase = AndroidRunPhase.Running, TargetText = "x" };
        var view = PlayBarPolicy.Compute(Input(EditorState.Edit, RunTargetCatalogBuilder.Pc, android));
        Check.Equal(PlayBarAction.None, view.PlayAction, "PC の Play を始めさせない");
        Check.Equal(StopBarAction.StopAndroid, view.StopAction, "停止は Android");
    }

    /// <summary>進捗の文言。</summary>
    private static void ProgressText()
    {
        Check.Equal(PlayBarPolicy.PreparingProgressText,
            PlayBarPolicy.BuildingProgressText(new AndroidRunSnapshot { Phase = AndroidRunPhase.Building }), "工程が分かる前");
        Check.Equal("0% [1/7] libSEED.so のビルド（cargo ndk）", PlayBarPolicy.BuildingProgressText(new AndroidRunSnapshot
        {
            Phase = AndroidRunPhase.Building, StepIndex = 1, StepCount = 7, StepTitle = "libSEED.so のビルド（cargo ndk）",
        }), "最初の工程");
        Check.Equal("100% [7/7] logcat", PlayBarPolicy.BuildingProgressText(new AndroidRunSnapshot
        {
            Phase = AndroidRunPhase.Building, StepIndex = 7, StepCount = 7, StepTitle = "logcat", Fraction = 1.0,
        }), "最後");
    }
}
