using System;
using System.Collections.Generic;

namespace SEED.UI;

// ============================================================
//  UiTokenCatalog.cs — テーマのトークンの表（1 か所。W2-9。docs/ui_theme.md §8）
//
//  【役割】SEED.UI の部品・画面の組み立て・グラフが読むトークンを 1 つの表にまとめる（名前・型・使う部品）。
//    - 読み込み（UiThemeSource）は、SEED のグループ（color・radius・…）の中の名前をこの表で引き、
//      表に無い名前（打ち間違い）と型の合わない値を警告して読まない（既定・基のテーマの値が残る。例外で止めない）
//    - アプリ独自のトークンは app のグループ（例 app.kakugo.danger）に書く（表で調べず、値の形で色・数・文字列を決める）
//    - 既定値はデータ（Theme/default_theme.json）が持つ。docs の表（既定値の列）は、この表と既定のテーマから作る
//      （editor/tests/UiComponentsTests の --token-table。テストが docs の表と一致するかを確かめる）
//  名前の定数は UiTokens（W2-4・W2-5・W2-9）・NavTokens（W2-7）・ChartTokens（W2-8）。トークンを足したら 3 か所
//  （定数・この表・default_theme.json）を揃える（テストが反射で揃いを確かめる）。
// ============================================================

/// <summary>トークンの名前の調べの結果。</summary>
public enum UiTokenMatch
{
    /// <summary>表にある（型は表のもの）。</summary>
    Known = 0,
    /// <summary>アプリ独自（app のグループ。型は値の形で決める）。</summary>
    AppDefined = 1,
    /// <summary>SEED のグループの中の、表に無い名前（打ち間違いの見込み）。</summary>
    UnknownName = 2,
    /// <summary>知らないグループ。</summary>
    UnknownGroup = 3,
}

/// <summary>テーマのトークンの表。</summary>
public static class UiTokenCatalog
{
    /// <summary>トークンの区切り。</summary>
    public const char Separator = '.';

    /// <summary>アプリ独自のトークンのグループ（表で調べない）。</summary>
    public const string AppGroup = "app";

    // ── 使う部品の書き方（docs の表の列。同じ言い方を揃える）──────────
    /// <summary>グラフの 2 つの部品。</summary>
    private const string Charts = "LineChart・BarChart";
    /// <summary>選択の 3 つの部品。</summary>
    private const string Selections = "SegmentedControl・ChipGroup・RadioGroup";
    /// <summary>部品が読まない目安のトークン（プレハブ・画面を作るときの寸法）。</summary>
    private const string GuideOnly = "（部品は読まない。プレハブ・画面の寸法の目安）";
    /// <summary>文字を持つすべての部品。</summary>
    private const string AllTexts = "文字を持つ全部品（Button・" + Selections + "・NumberField・TabBar・Dialog・Toast・WheelPicker・" + Charts + "・ThemeStyle）";

    /// <summary>表（docs の表の並び）。</summary>
    private static readonly UiTokenInfo[] Entries =
    {
        // ── 色 ──────────────────────────────────────────────
        new(UiTokens.ColorPrimary, UiTokenKind.Color, "Button（Filled の塗り・Outlined と Text の文字）・Toggle（オンの台）・Checkbox（オンの塗り）・Slider・ProgressBar・ProgressRing・RadioGroup（選んだ輪と点）"),
        new(UiTokens.ColorOnPrimary, UiTokenKind.Color, "Button（Filled の文字）・Checkbox（印）"),
        new(UiTokens.ColorBackground, UiTokenKind.Color, "ScreenStack（画面の背景・動きの幕）・ThemeStyle（画面の背景）"),
        new(UiTokens.ColorSurface, UiTokenKind.Color, "NumberField・TabBar・Dialog・BottomSheet・TopSheet（面）・ThemeStyle（カード・島）・LineChart（日付線のハンドルの縁）"),
        new(UiTokens.ColorSurfaceVariant, UiTokenKind.Color, "Toggle（オフの台）・Slider（溝）・SegmentedControl（台）・ProgressBar・ProgressRing（溝）・WheelPicker・TimeWheel（中央の帯）"),
        new(UiTokens.ColorOnSurface, UiTokenKind.Color, "NumberField・" + Selections + "（文字）・TabBar（選んだタブ）・Dialog（題）・WheelPicker（行の文字）・ThemeStyle"),
        new(UiTokens.ColorOnSurfaceMuted, UiTokenKind.Color, "Checkbox（オフの枠）・RadioGroup（選んでいない輪）・TabBar（選んでいないタブ）・Dialog（本文）・BottomSheet・TopSheet（つまみ）・ThemeStyle"),
        new(UiTokens.ColorOutline, UiTokenKind.Color, "Button（Outlined の枠）・Toggle（オフの台の枠）・NumberField・ChipGroup（枠）"),
        new(UiTokens.ColorSelected, UiTokenKind.Color, "Button（Tonal）・SegmentedControl・ChipGroup（選んだ項目）・TabBar（選択の印）"),
        new(UiTokens.ColorOnSelected, UiTokenKind.Color, "Button（Tonal の文字）・SegmentedControl・ChipGroup（選んだ項目の文字）"),
        new(UiTokens.ColorKnob, UiTokenKind.Color, "Toggle（オンのつまみ）"),
        new(UiTokens.ColorKnobOff, UiTokenKind.Color, "Toggle（オフのつまみ）"),
        new(UiTokens.ColorStateLayer, UiTokenKind.Color, "Button・Toggle・Checkbox・" + Selections + "・TabBar（押下の重ね色）"),
        new(UiTokens.ColorDisabled, UiTokenKind.Color, "Button・NumberField・SegmentedControl・ChipGroup（無効の塗り・枠）"),
        new(UiTokens.ColorOnDisabled, UiTokenKind.Color, "Button・NumberField・Slider・" + Selections + "・TabBar・WheelPicker（無効の文字・選べない行）"),
        new(UiTokens.ColorShadow, UiTokenKind.Color, GuideOnly),
        new(UiTokens.ColorError, UiTokenKind.Color, "ThemeStyle（一覧の行の削除の面。templates/ui の list_row）・（W2-6 の入力欄が使う予定）"),
        new(UiTokens.ColorOnError, UiTokenKind.Color, "ThemeStyle（削除の面の上の文字。templates/ui の list_row）"),
        new(NavTokens.ColorScrim, UiTokenKind.Color, "Dialog・BottomSheet・TopSheet（幕）"),
        new(NavTokens.ColorInverseSurface, UiTokenKind.Color, "Toast（面）・" + Charts + "（吹き出しの面）"),
        new(NavTokens.ColorOnInverseSurface, UiTokenKind.Color, "Toast（文字）・" + Charts + "（吹き出しの文字）"),
        new(ChartTokens.ColorSeries1, UiTokenKind.Color, Charts + "（系列 1）"),
        new(ChartTokens.ColorSeries2, UiTokenKind.Color, Charts + "（系列 2）"),
        new(ChartTokens.ColorSeries3, UiTokenKind.Color, Charts + "（系列 3）"),
        new(ChartTokens.ColorSeries4, UiTokenKind.Color, Charts + "（系列 4）"),
        new(ChartTokens.ColorGrid, UiTokenKind.Color, Charts + "（格子線）"),
        new(ChartTokens.ColorAxis, UiTokenKind.Color, Charts + "（軸の線）"),
        new(ChartTokens.ColorLabel, UiTokenKind.Color, Charts + "（目盛りの文字）"),
        new(ChartTokens.ColorReference, UiTokenKind.Color, "LineChart（基準線とその文字）"),
        new(ChartTokens.ColorEmptyBar, UiTokenKind.Color, "BarChart（合計 0 の棒）"),
        new(ChartTokens.ColorHighlight, UiTokenKind.Color, "BarChart（選んだ列の背景）"),

        // ── 角丸 ────────────────────────────────────────────
        new(UiTokens.RadiusButton, UiTokenKind.Number, "Button"),
        new(UiTokens.RadiusChip, UiTokenKind.Number, "ChipGroup"),
        new(UiTokens.RadiusCard, UiTokenKind.Number, "ThemeStyle（カード・島）"),
        new(UiTokens.RadiusField, UiTokenKind.Number, "NumberField"),
        new(UiTokens.RadiusSegment, UiTokenKind.Number, "SegmentedControl"),
        new(UiTokens.RadiusCheckbox, UiTokenKind.Number, "Checkbox"),
        new(UiTokens.RadiusProgress, UiTokenKind.Number, "ProgressBar"),
        new(UiTokens.RadiusWheelBand, UiTokenKind.Number, "WheelPicker・TimeWheel（中央の帯）"),
        new(NavTokens.RadiusDialog, UiTokenKind.Number, "Dialog"),
        new(NavTokens.RadiusSheet, UiTokenKind.Number, "BottomSheet・TopSheet"),
        new(NavTokens.RadiusToast, UiTokenKind.Number, "Toast"),
        new(NavTokens.RadiusTabIndicator, UiTokenKind.Number, "TabBar（選択の印）"),
        new(ChartTokens.RadiusBar, UiTokenKind.Number, "BarChart（棒の先）"),
        new(ChartTokens.RadiusTooltip, UiTokenKind.Number, Charts + "（吹き出し）"),

        // ── 余白 ────────────────────────────────────────────
        new(UiTokens.SpaceXs, UiTokenKind.Number, GuideOnly),
        new(UiTokens.SpaceS, UiTokenKind.Number, "ToastHost（トーストの間隔）"),
        new(UiTokens.SpaceM, UiTokenKind.Number, GuideOnly),
        new(UiTokens.SpaceL, UiTokenKind.Number, "ToastHost（画面の端との間）・SwipeActions（フルスワイプの文字と行の見た目の端の間）"),
        new(UiTokens.SpaceXl, UiTokenKind.Number, GuideOnly),

        // ── 大きさ ──────────────────────────────────────────
        new(UiTokens.SizeTouchMin, UiTokenKind.Number, "（部品は読まない。CanvasGesture の最小のヒット領域 48 dp の目安）"),
        new(UiTokens.SizeBorder, UiTokenKind.Number, "Button（Outlined）・NumberField・ChipGroup（細い枠）"),
        new(UiTokens.SizeCheckBorder, UiTokenKind.Number, "Checkbox・RadioGroup（枠）・Toggle（オフの台の枠）"),
        new(UiTokens.SizeToggleKnob, UiTokenKind.Number, "Toggle（オン・押している間のつまみ）"),
        new(UiTokens.SizeToggleKnobOff, UiTokenKind.Number, "Toggle（オフのつまみ）"),
        new(UiTokens.SizeToggleInset, UiTokenKind.Number, GuideOnly),
        new(UiTokens.SizeSliderTrack, UiTokenKind.Number, "Slider（溝の太さ）"),
        new(UiTokens.SizeSliderThumb, UiTokenKind.Number, "Slider（つまみ）"),
        new(UiTokens.SizeSliderThumbPressed, UiTokenKind.Number, "Slider（ドラッグ中のつまみ）"),
        new(UiTokens.SizeProgressBar, UiTokenKind.Number, GuideOnly),
        new(UiTokens.SizeRingThickness, UiTokenKind.Number, "ProgressRing（輪の太さ）"),
        new(UiTokens.SizeRadioDot, UiTokenKind.Number, GuideOnly),
        new(UiTokens.SizeShadowBlur, UiTokenKind.Number, GuideOnly),
        new(UiTokens.SizeShadowOffset, UiTokenKind.Number, GuideOnly),
        new(UiTokens.SizeWheelItem, UiTokenKind.Number, "WheelPicker・TimeWheel（行の高さ）"),
        new(UiTokens.SizeWheelBandInset, UiTokenKind.Number, "WheelPicker（帯の左右の余白）"),
        new(NavTokens.SizeTabBar, UiTokenKind.Number, "TabBar（高さ）"),
        new(NavTokens.SizeTabIndicatorWidth, UiTokenKind.Number, "TabBar（選択の印の幅）"),
        new(NavTokens.SizeTabIndicatorHeight, UiTokenKind.Number, "TabBar（選択の印の高さ）"),
        new(NavTokens.SizeDialogWidth, UiTokenKind.Number, "Dialog（札の幅）"),
        new(NavTokens.SizeDialogPadding, UiTokenKind.Number, "Dialog（内側の余白）"),
        new(NavTokens.SizeDialogTitleGap, UiTokenKind.Number, "Dialog（題 → 本文の間隔）"),
        new(NavTokens.SizeDialogActionsGap, UiTokenKind.Number, "Dialog（本文〈無ければ題〉→ ボタンの行の間隔）"),
        new(NavTokens.SizeDialogButtonHeight, UiTokenKind.Number, "Dialog（ボタンの高さ）"),
        new(NavTokens.SizeHandleWidth, UiTokenKind.Number, "BottomSheet・TopSheet（つまみの幅）"),
        new(NavTokens.SizeHandleHeight, UiTokenKind.Number, "BottomSheet・TopSheet（つまみの太さ）"),
        new(NavTokens.SizeHandleArea, UiTokenKind.Number, GuideOnly),
        new(NavTokens.SizeToastHeight, UiTokenKind.Number, "Toast（高さ）"),
        new(NavTokens.SizeDragDismiss, UiTokenKind.Number, "Toast・TopSheet（引いて閉じる距離）"),
        new(NavTokens.SizeBackPreviewShift, UiTokenKind.Number, "BackDispatcher（予測型の戻るのプレビューで画面をずらす量）"),
        new(ChartTokens.SizeLine, UiTokenKind.Number, "LineChart（線の太さ）"),
        new(ChartTokens.SizeDot, UiTokenKind.Number, "LineChart（点の半径）"),
        new(ChartTokens.SizeDotSelected, UiTokenKind.Number, "LineChart（選んだ点の半径）"),
        new(ChartTokens.SizeDotMinSpacing, UiTokenKind.Number, "LineChart（点を打つ最小の間隔）"),
        new(ChartTokens.SizeGrid, UiTokenKind.Number, Charts + "（格子線の太さ）"),
        new(ChartTokens.SizeAxis, UiTokenKind.Number, Charts + "（軸の線の太さ）"),
        new(ChartTokens.SizeReference, UiTokenKind.Number, "LineChart（基準線の太さ）"),
        new(ChartTokens.SizeYAxis, UiTokenKind.Number, Charts + "（縦軸の文字の列の幅）"),
        new(ChartTokens.SizeXAxis, UiTokenKind.Number, Charts + "（横軸の文字の行の高さ）"),
        new(ChartTokens.SizeXLabelSpacing, UiTokenKind.Number, Charts + "（横軸の文字の最小の間隔）"),
        new(ChartTokens.SizeYLabelSpacing, UiTokenKind.Number, Charts + "（縦軸の文字の最小の間隔）"),
        new(ChartTokens.SizeLabelGap, UiTokenKind.Number, Charts + "（目盛りの文字と面の間）"),
        new(ChartTokens.SizePlotPad, UiTokenKind.Number, Charts + "（面の上と右の余白）"),
        new(ChartTokens.SizeTouchSlop, UiTokenKind.Number, Charts + "（吹き出しの点を選ぶ横の距離）"),
        new(ChartTokens.SizeSmoothStep, UiTokenKind.Number, "LineChart（滑らかな曲線の刻み）"),
        new(ChartTokens.SizeBarMin, UiTokenKind.Number, "BarChart（棒の最小の太さ）"),
        new(ChartTokens.SizeTooltipPadding, UiTokenKind.Number, Charts + "（吹き出しの内側の余白）"),
        new(ChartTokens.SizeTooltipGap, UiTokenKind.Number, Charts + "（吹き出しと点の間）"),
        new(ChartTokens.SizeHandle, UiTokenKind.Number, "LineChart（日付線のハンドルの直径）"),
        new(ChartTokens.SizeHandleBorder, UiTokenKind.Number, "LineChart（日付線のハンドルの縁の太さ）"),

        // ── 文字の大きさ ────────────────────────────────────
        new(UiTokens.TextTitle, UiTokenKind.Number, "Dialog（題）・ThemeStyle（見出し）"),
        new(UiTokens.TextBody, UiTokenKind.Number, "NumberField・Toast・Dialog（本文）・" + Charts + "（データが無いときの文字）・ThemeStyle"),
        new(UiTokens.TextLabel, UiTokenKind.Number, "Button・" + Selections + "・Dialog（ボタン）・ThemeStyle"),
        new(UiTokens.TextCaption, UiTokenKind.Number, "TabBar・ThemeStyle（注記）"),
        new(UiTokens.TextWheel, UiTokenKind.Number, "WheelPicker・TimeWheel（行の文字）"),
        new(ChartTokens.TextAxis, UiTokenKind.Number, Charts + "（目盛りの文字）"),
        new(ChartTokens.TextTooltip, UiTokenKind.Number, Charts + "（吹き出しの文字）"),

        // ── 書体（W2-9）──────────────────────────────────────
        new(UiTokens.FontFamily, UiTokenKind.Text, AllTexts),
        new(UiTokens.FontWeight, UiTokenKind.Number, AllTexts),
        new(UiTokens.FontWeightTitle, UiTokenKind.Number, "Dialog（題）・ThemeStyle（見出し）"),

        // ── 動き（秒）と曲線 ────────────────────────────────
        new(UiTokens.MotionShort, UiTokenKind.Number, "Toggle（つまみ）・Toast・TopSheet（引いた後の戻り）"),
        new(UiTokens.MotionMedium, UiTokenKind.Number, "ProgressBar・ProgressRing（値の伸び縮み）"),
        new(UiTokens.MotionRepeatInterval, UiTokenKind.Number, "NumberField（長押しの連続の最初の間隔）"),
        new(UiTokens.MotionRepeatMinInterval, UiTokenKind.Number, "NumberField（長押しの連続の最短の間隔）"),
        new(UiTokens.MotionRepeatAccel, UiTokenKind.Number, "NumberField（長押しの連続の加速）"),
        new(UiTokens.MotionWheel, UiTokenKind.Number, "WheelPicker・TimeWheel（タップ・キー・スクリプトで動かす時間）"),
        new(UiTokens.MotionWheelCorrect, UiTokenKind.Number, "WheelPicker（選べない行から戻す時間）"),
        new(UiTokens.MotionTheme, UiTokenKind.Number, "UiTheme（動きありの切り替えの色の補間の時間）"),
        new(UiTokens.MotionThemeCurve, UiTokenKind.Curve, "UiTheme（色の補間の曲線）"),
        new(UiTokens.MotionSwipeFull, UiTokenKind.Number, "SwipeActions（フルスワイプの文字の置き場の補間）"),
        new(UiTokens.MotionSwipeDismiss, UiTokenKind.Number, "SwipeActions（確定で行を外へ流し切る）"),
        new(UiTokens.MotionSwipeCollapse, UiTokenKind.Number, "（部品は読まない。一覧の持ち主が消した行の高さを畳む時間。見本の UiGallerySections）"),
        new(NavTokens.MotionPush, UiTokenKind.Number, "ScreenStack（押し込み）"),
        new(NavTokens.MotionPushCurve, UiTokenKind.Curve, "ScreenStack（押し込み）"),
        new(NavTokens.MotionCover, UiTokenKind.Number, "ScreenStack（覆う画面）"),
        new(NavTokens.MotionCoverCurve, UiTokenKind.Curve, "ScreenStack（覆う画面）"),
        new(NavTokens.MotionFade, UiTokenKind.Number, "ScreenStack（フェード）"),
        new(NavTokens.MotionFadeCurve, UiTokenKind.Curve, "ScreenStack（フェード）"),
        new(NavTokens.MotionOverlay, UiTokenKind.Number, "TopSheet"),
        new(NavTokens.MotionOverlayCurve, UiTokenKind.Curve, "TopSheet"),
        new(NavTokens.MotionDialog, UiTokenKind.Number, "Dialog"),
        new(NavTokens.MotionDialogCurve, UiTokenKind.Curve, "Dialog"),
        new(NavTokens.MotionSheet, UiTokenKind.Number, "BottomSheet"),
        new(NavTokens.MotionToast, UiTokenKind.Number, "Toast（出入り）"),
        new(NavTokens.MotionToastCurve, UiTokenKind.Curve, "Toast（出入り）"),
        new(NavTokens.MotionToastShort, UiTokenKind.Number, "ToastHost（短いトーストを出しておく時間）"),
        new(NavTokens.MotionToastLong, UiTokenKind.Number, "ToastHost（長いトーストを出しておく時間）"),
        new(NavTokens.MotionBackPreviewCurve, UiTokenKind.Curve, "BackDispatcher（予測型の戻るのプレビューの縮み具合）"),
        new(ChartTokens.MotionZoom, UiTokenKind.Number, Charts + "（± の拡大縮小）"),

        // ── 濃さ（0..1）────────────────────────────────────
        new(UiTokens.OpacityPressed, UiTokenKind.Number, "Button・Toggle・Checkbox・" + Selections + "・TabBar（押下の重ね色の濃さ）"),
        new(UiTokens.OpacityDisabled, UiTokenKind.Number, "Toggle・Checkbox・Slider・ProgressBar・ProgressRing・WheelPicker・TimeWheel（無効の濃さ）"),
        new(UiTokens.OpacityShadow, UiTokenKind.Number, GuideOnly),
        new(UiTokens.OpacityWheelDim, UiTokenKind.Number, "WheelPicker（帯の外の行の濃さ）"),
        new(NavTokens.OpacityScrim, UiTokenKind.Number, "BottomSheet・TopSheet（幕の濃さ）"),
        new(NavTokens.OpacityDialogScrim, UiTokenKind.Number, "Dialog（幕の濃さ）"),
        new(ChartTokens.OpacityArea, UiTokenKind.Number, "LineChart（線の下の塗りの上端）"),

        // ── 割合 ────────────────────────────────────────────
        new(UiTokens.RatioSwipeFull, UiTokenKind.Number, "SwipeActions（フルスワイプで構えるずらし量。行の幅に対する）"),
        new(UiTokens.RatioSwipeFullCancel, UiTokenKind.Number, "SwipeActions（構えを解くずらし量。行の幅に対する）"),
        new(NavTokens.RatioPushParallax, UiTokenKind.Number, "ScreenStack（押し込みの視差）"),
        new(NavTokens.RatioDialogScaleFrom, UiTokenKind.Number, "Dialog（出るときの最初の大きさ）"),
        new(NavTokens.RatioSheetMaxHeight, UiTokenKind.Number, "BottomSheet（最大の高さ）"),
        new(NavTokens.RatioBackPreviewScale, UiTokenKind.Number, "BackDispatcher（予測型の戻るのプレビューのいちばん小さい倍率）"),
        new(ChartTokens.RatioBarWidth, UiTokenKind.Number, "BarChart（棒の太さ）"),
        new(ChartTokens.RatioEmptyBar, UiTokenKind.Number, "BarChart（合計 0 の棒の高さ）"),
        new(ChartTokens.RatioFlingDrag, UiTokenKind.Number, Charts + "（払った後の慣性の減速）"),
        new(ChartTokens.RatioZoomStep, UiTokenKind.Number, Charts + "（± の 1 回の倍率）"),

        // ── 速さ（dp/秒）──────────────────────────────────────
        new(NavTokens.SpeedFlingDismiss, UiTokenKind.Number, "Toast・TopSheet（払って閉じる速さ）"),
        new(ChartTokens.SpeedFlingStop, UiTokenKind.Number, Charts + "（慣性が止まる速さ）"),

        // ── 数 ──────────────────────────────────────────────
        new(NavTokens.CountToastVisible, UiTokenKind.Number, "ToastHost（同時に見せる数）"),
        new(ChartTokens.CountYIntervals, UiTokenKind.Number, Charts + "（縦軸の区間の数の上限）"),

        // ── 重なりのレイヤーの底上げ ─────────────────────────
        new(NavTokens.LayerStackStep, UiTokenKind.Number, "ScreenStack（1 段ぶん）"),
        new(NavTokens.LayerModalStep, UiTokenKind.Number, "ModalHost（帯の中の 1 つぶん）"),
        new(NavTokens.LayerOverlay, UiTokenKind.Number, "ModalHost（上からの覆いの帯）"),
        new(NavTokens.LayerSheet, UiTokenKind.Number, "ModalHost（下からのシートの帯）"),
        new(NavTokens.LayerDialog, UiTokenKind.Number, "ModalHost（ダイアログの帯）"),
        new(NavTokens.LayerToast, UiTokenKind.Number, "ToastHost（トーストの帯）"),
    };

    /// <summary>名前 → 表の行。</summary>
    private static readonly Dictionary<string, UiTokenInfo> ByName = BuildIndex();

    /// <summary>SEED のグループ（表の名前の最初の部分）。</summary>
    private static readonly HashSet<string> KnownGroups = BuildGroups();

    /// <summary>表のすべての行（docs の表の並び）。</summary>
    public static IReadOnlyList<UiTokenInfo> All => Entries;

    /// <summary>名前の表の行を引く。</summary>
    public static bool TryGet(string name, out UiTokenInfo info) => ByName.TryGetValue(name, out info);

    /// <summary>SEED のグループか（app は含まない）。</summary>
    public static bool IsKnownGroup(string group) => KnownGroups.Contains(group);

    /// <summary>トークンのグループ（最初の「.」の前。「.」が無ければ全体）。</summary>
    public static string GroupOf(string token)
    {
        int dot = token.IndexOf(Separator);
        return dot < 0 ? token : token.Substring(0, dot);
    }

    /// <summary>
    /// JSON の葉のトークン（平らにした名前）を表で調べる。曲線の成分（例 motion.push_curve.x1）は数の型で Known。
    /// </summary>
    /// <param name="leafToken">平らにしたトークンの名前。</param>
    /// <param name="kind">Known のときの型（曲線の成分は Number。表の曲線そのものは Curve）。</param>
    public static UiTokenMatch Match(string leafToken, out UiTokenKind kind)
    {
        kind = UiTokenKind.Number;
        if (ByName.TryGetValue(leafToken, out var info))
        {
            kind = info.Kind;
            return UiTokenMatch.Known;
        }
        // 曲線の成分: 「曲線の名前 + .x1 など」
        int dot = leafToken.LastIndexOf(Separator);
        if (dot > 0 && ByName.TryGetValue(leafToken.Substring(0, dot), out var parent) && parent.Kind == UiTokenKind.Curve
            && IsCurveComponent(leafToken.Substring(dot)))
        {
            kind = UiTokenKind.Number;
            return UiTokenMatch.Known;
        }
        string group = GroupOf(leafToken);
        if (group == AppGroup) return UiTokenMatch.AppDefined;
        return KnownGroups.Contains(group) ? UiTokenMatch.UnknownName : UiTokenMatch.UnknownGroup;
    }

    /// <summary>
    /// 表の行の、テーマの表に入る葉の名前（曲線は 4 つの成分・ほかはそのまま）。
    /// </summary>
    public static IEnumerable<string> LeafTokens(UiTokenInfo info)
    {
        if (info.Kind == UiTokenKind.Curve)
        {
            foreach (var t in UiCurve.ComponentTokens(info.Name)) yield return t;
        }
        else
        {
            yield return info.Name;
        }
    }

    /// <summary>曲線の成分の接尾辞か（.x1・.y1・.x2・.y2）。</summary>
    private static bool IsCurveComponent(string suffix)
        => suffix == UiCurve.SuffixX1 || suffix == UiCurve.SuffixY1 || suffix == UiCurve.SuffixX2 || suffix == UiCurve.SuffixY2;

    /// <summary>名前の索引を作る（同じ名前が 2 度あれば最初の行。テストが重複を見つける）。</summary>
    private static Dictionary<string, UiTokenInfo> BuildIndex()
    {
        var index = new Dictionary<string, UiTokenInfo>(StringComparer.Ordinal);
        foreach (var e in Entries) index.TryAdd(e.Name, e);
        return index;
    }

    /// <summary>グループの集合を作る。</summary>
    private static HashSet<string> BuildGroups()
    {
        var groups = new HashSet<string>(StringComparer.Ordinal);
        foreach (var e in Entries) groups.Add(GroupOf(e.Name));
        return groups;
    }
}
