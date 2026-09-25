// ============================================================
//  PlayBarPolicy.cs — ツールバーのプレイバー（状態表示・実行ボタン・停止ボタン・実行先セレクタ・進捗）の判断（純粋な処理）
//
//  【入力】PC のランタイムの状態（EditorState）・選んでいる実行先・Android の実行の写し
//  【出力】ボタンの有効/無効・絵柄・ツールチップ・押したときに何をするか・状態表示の文言と色・進捗の表示
//  MainWindow はこの結果をそのまま画面へ当てるだけ（MainWindow.AndroidRun.cs の ApplyPlayBar）。
//
//  【決まり】
//    1. Android の実行中（Building / Running / Stopping）は Android の表示が優先:
//       実行ボタンは押せない（Running では一時停止の絵柄のまま無効。Android は一時停止できない理由をツールチップに）、
//       停止ボタンは Building / Running で「Android を止める」、実行先は変えられない
//    2. PC の実行中（Launching / Play / Pause）は従来の PC の表示（実行先が Android でも PC の一時停止・停止を優先）。
//       実行先は変えられない（PC の実行中に Android の実行を始めさせない）
//    3. どちらも動いていないとき:
//       実行先が PC → 従来の PC の表示（状態ごとの表。ApplyUiState にあったものをここへ移した）
//       実行先が Android → 実行ボタン＝「その端末で実行」（端末が選べない状態・PC のランタイムの起動中は理由付きで無効）
//
//  WPF に依存しない（editor/tests/AndroidRunUiTests からリンクされる）。
// ============================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using SEEDEditor.Runtime;

namespace SEEDEditor.AndroidRun;

/// <summary>実行ボタンの絵柄（プレイバーの PNG アイコン）。</summary>
public enum PlayGlyph
{
    /// <summary>再生（緑の地）。</summary>
    Play,

    /// <summary>一時停止（橙の地）。</summary>
    Pause,
}

/// <summary>実行ボタンを押したときにすること。</summary>
public enum PlayBarAction
{
    /// <summary>何もしない（押せない）。</summary>
    None,

    /// <summary>PC の Play / Pause / Resume（従来の OnPlayPause）。</summary>
    TogglePc,

    /// <summary>選んでいる Android の端末で実行を始める。</summary>
    StartAndroid,
}

/// <summary>停止ボタンを押したときにすること。</summary>
public enum StopBarAction
{
    /// <summary>何もしない（押せない）。</summary>
    None,

    /// <summary>PC の実行を止める（従来の OnStop）。</summary>
    StopPc,

    /// <summary>Android の実行を止める（ビルドの中止・アプリの停止）。</summary>
    StopAndroid,
}

/// <summary>状態表示の色の種類。</summary>
public enum PlayBarTone
{
    /// <summary>編集中（緑）。</summary>
    Edit,

    /// <summary>実行中（水色）。</summary>
    Running,

    /// <summary>一時停止・停止中（橙）。</summary>
    Paused,

    /// <summary>ビルド中（黄）。</summary>
    Busy,

    /// <summary>待機（灰）。</summary>
    Idle,
}

/// <summary>プレイバーの判断の材料。</summary>
/// <param name="PcState">PC のランタイムの状態。</param>
/// <param name="Target">選んでいる実行先。</param>
/// <param name="Android">Android の実行の写し。</param>
public sealed record PlayBarInput(EditorState PcState, RunTargetEntry Target, AndroidRunSnapshot Android);

/// <summary>プレイバーの見た目と動き。</summary>
public sealed record PlayBarView
{
    /// <summary>実行ボタンを押したときにすること。</summary>
    public required PlayBarAction PlayAction { get; init; }

    /// <summary>実行ボタンを押せるか。</summary>
    public bool PlayEnabled => PlayAction != PlayBarAction.None;

    /// <summary>実行ボタンの絵柄。</summary>
    public required PlayGlyph PlayGlyph { get; init; }

    /// <summary>実行ボタンのツールチップ（押せないときは理由）。</summary>
    public required string PlayToolTip { get; init; }

    /// <summary>停止ボタンを押したときにすること。</summary>
    public required StopBarAction StopAction { get; init; }

    /// <summary>停止ボタンを押せるか。</summary>
    public bool StopEnabled => StopAction != StopBarAction.None;

    /// <summary>停止ボタンのツールチップ。</summary>
    public required string StopToolTip { get; init; }

    /// <summary>実行先セレクタを変えられるか。</summary>
    public required bool TargetSelectorEnabled { get; init; }

    /// <summary>実行先セレクタのツールチップ（変えられないときは理由）。</summary>
    public required string TargetSelectorToolTip { get; init; }

    /// <summary>状態表示の文言（EDIT / PLAY / ANDROID RUN 等）。</summary>
    public required string StateLabel { get; init; }

    /// <summary>状態表示の色の種類。</summary>
    public required PlayBarTone StateTone { get; init; }

    /// <summary>状態表示のアイコン（Icons.xaml のキー）。</summary>
    public required string StateIconKey { get; init; }

    /// <summary>進捗の文言（Android の実行中だけ。それ以外は null で隠す）。</summary>
    public string? ProgressText { get; init; }

    /// <summary>進捗の割合（0〜1。Android のビルド中だけ。それ以外は null で隠す）。</summary>
    public double? ProgressFraction { get; init; }
}

/// <summary>プレイバーの判断。</summary>
public static class PlayBarPolicy
{
    // ── PC のツールチップ（従来の MainWindow.xaml の値）──────────────

    /// <summary>PC の実行ボタンのツールチップ。</summary>
    public const string PcPlayToolTip = "Play / Pause / Resume";

    /// <summary>PC の停止ボタンのツールチップ。</summary>
    public const string PcStopToolTip = "Stop";

    // ── Android の文言 ─────────────────────────────────

    /// <summary>Android の実行ボタンのツールチップの書式（{0}=実行先）。</summary>
    private const string AndroidPlayToolTipFormat = "{0} で実行（ビルド → インストール → 起動 → logcat を Output パネルへ。変更の無い工程は飛ばします）";

    /// <summary>見えなくなった端末を選んでいるときに、実行ボタンのツールチップへ足す注記（段階C-3）。</summary>
    private const string MissingTargetPlayNote = "\nこの端末は今 adb に見えないため、エミュレータ（起動中のもの、無ければ AVD を起動）で実行します。";

    /// <summary>Android（自動）を選んでいるときに、実行ボタンのツールチップへ足す注記（段階C-3）。</summary>
    private const string AutoTargetPlayNote = "\n実機（前回使ったものを優先）→ 起動中のエミュレータ → AVD を起動、の順で端末を決めます。";

    /// <summary>PC のランタイムが Play の起動中などで Android の実行を始められない理由。</summary>
    private const string PcBusyForAndroidToolTip = "PC で実行中は Android の実行を始められません（PC の実行を止めてから）。";

    /// <summary>Android の停止ボタン（何も動いていないとき）のツールチップ。</summary>
    private const string AndroidStopIdleToolTip = "Android の実行を止める（実行していません）";

    /// <summary>ビルド中の実行ボタンのツールチップ。</summary>
    private const string AndroidBuildingPlayToolTip = "Android 向けにビルド・インストール中です（停止ボタンで中止できます）。";

    /// <summary>ビルド中の停止ボタンのツールチップ。</summary>
    private const string AndroidBuildingStopToolTip = "ビルドを中止する（子プロセスの終了を待ってから止まります）";

    /// <summary>実行中の実行ボタン（一時停止の絵柄）のツールチップ。</summary>
    private const string AndroidRunningPlayToolTip = "Android の実行は一時停止できません（停止ボタンで端末のアプリを止めます）。";

    /// <summary>実行中の停止ボタンのツールチップ。</summary>
    private const string AndroidRunningStopToolTip = "端末のアプリを止める（logcat も止めます）";

    /// <summary>止めている途中のツールチップ。</summary>
    private const string AndroidStoppingToolTip = "停止しています…";

    /// <summary>実行先セレクタのツールチップ（変えられるとき）。</summary>
    public const string TargetSelectorToolTip =
        "実行先（PC／Android（自動）／Android の実機・エミュレータ）。一覧を開くと adb で端末を探し直します。";

    /// <summary>Android の実行中の実行先セレクタのツールチップ。</summary>
    private const string TargetLockedByAndroidToolTip = "Android で実行中は実行先を変えられません（停止してから）。";

    /// <summary>PC の実行中の実行先セレクタのツールチップ。</summary>
    private const string TargetLockedByPcToolTip = "PC で実行中は実行先を変えられません（停止してから）。";

    // ── 状態表示（Android）────────────────────────────────

    /// <summary>Android のビルド中の状態表示。</summary>
    public const string AndroidBuildingLabel = "ANDROID BUILD...";

    /// <summary>Android の実行中の状態表示。</summary>
    public const string AndroidRunningLabel = "ANDROID RUN";

    /// <summary>Android の停止中の状態表示。</summary>
    public const string AndroidStoppingLabel = "STOPPING...";

    /// <summary>Android の実行中の状態アイコン。</summary>
    private const string AndroidRunningIconKey = "Icon.Platform.Android";

    /// <summary>止めている途中の状態アイコン。</summary>
    private const string StoppingIconKey = "Icon.Stop";

    /// <summary>ビルド中の状態アイコン（PC のビルド中と同じ）。</summary>
    private const string BuildingIconKey = "Icon.Settings";

    // ── 進捗の文言 ─────────────────────────────────────

    /// <summary>準備中（工程の数がまだ分からない）の進捗。</summary>
    public const string PreparingProgressText = "準備中…";

    /// <summary>準備中の詳細（エミュレータの起動待ち等）がある進捗の書式（{0}=詳細）。</summary>
    private const string PreparingDetailFormat = "準備中: {0}";

    /// <summary>ビルド中の進捗の書式（{0}=割合 %、{1}=何番目、{2}=工程の数、{3}=工程の表示名）。</summary>
    private const string BuildingProgressFormat = "{0}% [{1}/{2}] {3}";

    /// <summary>実行中の進捗の書式（{0}=実行先）。</summary>
    private const string RunningProgressFormat = "{0} で実行中";

    /// <summary>割合を % にする倍率。</summary>
    private const double PercentScale = 100.0;

    /// <summary>
    /// PC の状態ごとの表示（従来 MainWindow.Camera.cs の ApplyUiState にあった表。値は変えていない）。
    /// </summary>
    private static readonly IReadOnlyDictionary<EditorState, PcRow> PcTable = new Dictionary<EditorState, PcRow>
    {
        [EditorState.Edit]      = new(PlayEnabled: true,  PlayGlyph.Play,  StopEnabled: false, "EDIT",         PlayBarTone.Edit,    "Icon.Dirty"),
        [EditorState.Play]      = new(PlayEnabled: true,  PlayGlyph.Pause, StopEnabled: true,  "PLAY",         PlayBarTone.Running, "Icon.Play"),
        [EditorState.Pause]     = new(PlayEnabled: true,  PlayGlyph.Play,  StopEnabled: true,  "PAUSE",        PlayBarTone.Paused,  "Icon.Pause"),
        [EditorState.Building]  = new(PlayEnabled: false, PlayGlyph.Play,  StopEnabled: false, "BUILDING...",  PlayBarTone.Busy,    "Icon.Settings"),
        [EditorState.Launching] = new(PlayEnabled: false, PlayGlyph.Play,  StopEnabled: true,  "LAUNCHING...", PlayBarTone.Running, "Icon.Play"),
        [EditorState.Idle]      = new(PlayEnabled: false, PlayGlyph.Play,  StopEnabled: false, "IDLE",         PlayBarTone.Idle,    "Icon.Info"),
    };

    /// <summary>PC の状態 1 つの表示。</summary>
    private sealed record PcRow(bool PlayEnabled, PlayGlyph Glyph, bool StopEnabled, string Label, PlayBarTone Tone, string IconKey);

    /// <summary>
    /// プレイバーの見た目と動きを決める。
    /// </summary>
    /// <param name="input">材料。</param>
    /// <returns>見た目と動き。</returns>
    public static PlayBarView Compute(PlayBarInput input)
    {
        if (input.Android.IsActive) return ForAndroidActive(input.Android);
        var pc = PcTable[input.PcState];
        if (IsPcRunning(input.PcState)) return ForPc(pc, TargetLockedByPcToolTip, selectorEnabled: false);
        return input.Target.IsAndroid ? ForAndroidTarget(input, pc) : ForPc(pc, TargetSelectorToolTip, selectorEnabled: true);
    }

    /// <summary>PC の実行中か（Play の起動中・実行中・一時停止中）。この間は Android の実行を始めさせない。</summary>
    /// <param name="state">PC の状態。</param>
    /// <returns>実行中なら true。</returns>
    public static bool IsPcRunning(EditorState state) =>
        state is EditorState.Launching or EditorState.Play or EditorState.Pause;

    /// <summary>
    /// Android の実行を始めてよい PC の状態か（PC の実行中でなければよい。PC のランタイムのビルド中・待機中でも、
    /// Android は別の置き場で作るので始められる。cargo のロックで順番待ちになることはある）。
    /// </summary>
    /// <param name="state">PC の状態。</param>
    /// <returns>始めてよければ true。</returns>
    public static bool CanStartAndroid(EditorState state) => !IsPcRunning(state);

    /// <summary>従来の PC の表示。</summary>
    private static PlayBarView ForPc(PcRow pc, string selectorToolTip, bool selectorEnabled) => new()
    {
        PlayAction = pc.PlayEnabled ? PlayBarAction.TogglePc : PlayBarAction.None,
        PlayGlyph = pc.Glyph,
        PlayToolTip = PcPlayToolTip,
        StopAction = pc.StopEnabled ? StopBarAction.StopPc : StopBarAction.None,
        StopToolTip = PcStopToolTip,
        TargetSelectorEnabled = selectorEnabled,
        TargetSelectorToolTip = selectorToolTip,
        StateLabel = pc.Label,
        StateTone = pc.Tone,
        StateIconKey = pc.IconKey,
    };

    /// <summary>実行先が Android で、何も動いていないとき（状態表示は PC のまま）。</summary>
    private static PlayBarView ForAndroidTarget(PlayBarInput input, PcRow pc)
    {
        var target = input.Target;
        string playToolTip;
        var canStart = false;
        if (!target.CanRun)
        {
            playToolTip = target.ToolTip;
        }
        else if (!CanStartAndroid(input.PcState))
        {
            playToolTip = PcBusyForAndroidToolTip;
        }
        else
        {
            playToolTip = string.Format(AndroidPlayToolTipFormat, target.Text)
                          + (target.IsMissing ? MissingTargetPlayNote : target.IsAndroidAuto ? AutoTargetPlayNote : string.Empty);
            canStart = true;
        }

        return new PlayBarView
        {
            PlayAction = canStart ? PlayBarAction.StartAndroid : PlayBarAction.None,
            PlayGlyph = PlayGlyph.Play,
            PlayToolTip = playToolTip,
            StopAction = StopBarAction.None,
            StopToolTip = AndroidStopIdleToolTip,
            TargetSelectorEnabled = true,
            TargetSelectorToolTip = TargetSelectorToolTip,
            StateLabel = pc.Label,
            StateTone = pc.Tone,
            StateIconKey = pc.IconKey,
        };
    }

    /// <summary>Android の実行中（Building / Running / Stopping）。</summary>
    private static PlayBarView ForAndroidActive(AndroidRunSnapshot android) => android.Phase switch
    {
        AndroidRunPhase.Building => new PlayBarView
        {
            PlayAction = PlayBarAction.None,
            PlayGlyph = PlayGlyph.Play,
            PlayToolTip = AndroidBuildingPlayToolTip,
            StopAction = StopBarAction.StopAndroid,
            StopToolTip = AndroidBuildingStopToolTip,
            TargetSelectorEnabled = false,
            TargetSelectorToolTip = TargetLockedByAndroidToolTip,
            StateLabel = AndroidBuildingLabel,
            StateTone = PlayBarTone.Busy,
            StateIconKey = BuildingIconKey,
            ProgressText = BuildingProgressText(android),
            ProgressFraction = android.Fraction,
        },
        AndroidRunPhase.Running => new PlayBarView
        {
            PlayAction = PlayBarAction.None,
            PlayGlyph = PlayGlyph.Pause,
            PlayToolTip = AndroidRunningPlayToolTip,
            StopAction = StopBarAction.StopAndroid,
            StopToolTip = AndroidRunningStopToolTip,
            TargetSelectorEnabled = false,
            TargetSelectorToolTip = TargetLockedByAndroidToolTip,
            StateLabel = AndroidRunningLabel,
            StateTone = PlayBarTone.Running,
            StateIconKey = AndroidRunningIconKey,
            ProgressText = string.Format(RunningProgressFormat, android.TargetText),
        },
        _ => new PlayBarView
        {
            PlayAction = PlayBarAction.None,
            PlayGlyph = PlayGlyph.Play,
            PlayToolTip = AndroidStoppingToolTip,
            StopAction = StopBarAction.None,
            StopToolTip = AndroidStoppingToolTip,
            TargetSelectorEnabled = false,
            TargetSelectorToolTip = TargetLockedByAndroidToolTip,
            StateLabel = AndroidStoppingLabel,
            StateTone = PlayBarTone.Paused,
            StateIconKey = StoppingIconKey,
            ProgressText = AndroidStoppingToolTip,
        },
    };

    /// <summary>
    /// ビルド中の進捗の文言（工程の数が分かる前は「準備中…」。エミュレータの起動待ちなど準備の詳細があれば「準備中: 詳細」）。
    /// </summary>
    /// <param name="android">Android の実行の写し。</param>
    /// <returns>文言。</returns>
    public static string BuildingProgressText(AndroidRunSnapshot android)
    {
        if (android.StepCount <= 0 || android.StepIndex <= 0 || android.StepTitle is null)
        {
            return string.IsNullOrWhiteSpace(android.PrepareDetail)
                ? PreparingProgressText
                : string.Format(PreparingDetailFormat, android.PrepareDetail);
        }
        var percent = (int)Math.Round(android.Fraction * PercentScale, MidpointRounding.AwayFromZero);
        return string.Format(CultureInfo.InvariantCulture, BuildingProgressFormat, percent, android.StepIndex, android.StepCount, android.StepTitle);
    }
}
