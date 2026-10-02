namespace SEED.UI;

// ============================================================
//  UiTokens.cs — 部品が読むテーマのトークンの名前の一覧（W2-4。W2-9 で書体・テーマの切り替えの動きを、
//                W2 の手直し P2-3 でスワイプのフルスワイプの割合・動きと削除の面の文字の色を足した）
//
//  トークン = テーマの JSON の「グループ.名前」（例 color.primary・radius.button）。値の表（既定）は
//  Theme/default_theme.json（SEEDScripting に埋め込み）。部品はここの名前だけを使い、値を直接書かない
//  （テーマの差し替えで全部品の見た目が変わる）。型と「使う部品」の表（1 か所）は UiTokenCatalog、正典は docs/ui_theme.md §8。
//  足すときは UiTokenCatalog・default_theme.json にも足すこと（テストが 3 つの揃いと docs の表を確かめる）。
// ============================================================

/// <summary>テーマのトークンの名前。</summary>
public static class UiTokens
{
    // ── 色（sRGB の #RRGGBB。読み込みで線形へ）─────────────────
    /// <summary>主の色（塗りのボタン・オンのスイッチ・スライダ・進捗）。</summary>
    public const string ColorPrimary = "color.primary";
    /// <summary>主の色の上の文字・つまみ。</summary>
    public const string ColorOnPrimary = "color.on_primary";
    /// <summary>画面の背景。</summary>
    public const string ColorBackground = "color.background";
    /// <summary>面（カード・島・入力欄）。</summary>
    public const string ColorSurface = "color.surface";
    /// <summary>面の変種（溝・オフのスイッチ・セグメントの台）。</summary>
    public const string ColorSurfaceVariant = "color.surface_variant";
    /// <summary>面の上の文字。</summary>
    public const string ColorOnSurface = "color.on_surface";
    /// <summary>面の上の控えめな文字。</summary>
    public const string ColorOnSurfaceMuted = "color.on_surface_muted";
    /// <summary>枠線（チップ・チェックボックス・枠のボタン）。</summary>
    public const string ColorOutline = "color.outline";
    /// <summary>選ばれた項目の背景（セグメント・チップ）。</summary>
    public const string ColorSelected = "color.selected";
    /// <summary>選ばれた項目の文字。</summary>
    public const string ColorOnSelected = "color.on_selected";
    /// <summary>スイッチのつまみ（オン）。</summary>
    public const string ColorKnob = "color.knob";
    /// <summary>スイッチのつまみ（オフ）。</summary>
    public const string ColorKnobOff = "color.knob_off";
    /// <summary>押下の重ね色（opacity.pressed の濃さで重ねる）。</summary>
    public const string ColorStateLayer = "color.state_layer";
    /// <summary>無効の塗り。</summary>
    public const string ColorDisabled = "color.disabled";
    /// <summary>無効の文字。</summary>
    public const string ColorOnDisabled = "color.on_disabled";
    /// <summary>影。</summary>
    public const string ColorShadow = "color.shadow";
    /// <summary>エラー（W2-6 の入力欄）・削除の面（見本の一覧の行 list_row の Actions。W2 の手直し P2-3）。</summary>
    public const string ColorError = "color.error";
    /// <summary>エラー・削除の面の上の文字（W2 の手直し P2-3。Material 3 の onError に当たる）。</summary>
    public const string ColorOnError = "color.on_error";

    // ── 角丸（キャンバスの単位）──────────────────────────────
    /// <summary>ボタン。</summary>
    public const string RadiusButton = "radius.button";
    /// <summary>チップ。</summary>
    public const string RadiusChip = "radius.chip";
    /// <summary>カード・島。</summary>
    public const string RadiusCard = "radius.card";
    /// <summary>入力欄・数値欄。</summary>
    public const string RadiusField = "radius.field";
    /// <summary>セグメントの台と選択の印。</summary>
    public const string RadiusSegment = "radius.segment";
    /// <summary>チェックボックス。</summary>
    public const string RadiusCheckbox = "radius.checkbox";
    /// <summary>進捗の棒。</summary>
    public const string RadiusProgress = "radius.progress";
    /// <summary>ホイールの中央の帯（W2-5。Flutter の選択の帯の角丸 8）。</summary>
    public const string RadiusWheelBand = "radius.wheel_band";

    // ── 余白（キャンバスの単位）──────────────────────────────
    /// <summary>とても小さい。</summary>
    public const string SpaceXs = "space.xs";
    /// <summary>小さい。</summary>
    public const string SpaceS = "space.s";
    /// <summary>中くらい。</summary>
    public const string SpaceM = "space.m";
    /// <summary>大きい。</summary>
    public const string SpaceL = "space.l";
    /// <summary>とても大きい。</summary>
    public const string SpaceXl = "space.xl";

    // ── 大きさ（キャンバスの単位）────────────────────────────
    /// <summary>押せる部品の最小の大きさ（48）。</summary>
    public const string SizeTouchMin = "size.touch_min";
    /// <summary>細い枠線の太さ。</summary>
    public const string SizeBorder = "size.border";
    /// <summary>チェックボックスの枠線の太さ。</summary>
    public const string SizeCheckBorder = "size.check_border";
    /// <summary>スイッチのつまみ（オン）。</summary>
    public const string SizeToggleKnob = "size.toggle_knob";
    /// <summary>スイッチのつまみ（オフ）。</summary>
    public const string SizeToggleKnobOff = "size.toggle_knob_off";
    /// <summary>スイッチのつまみと台の端の間。</summary>
    public const string SizeToggleInset = "size.toggle_inset";
    /// <summary>スライダの溝の太さ。</summary>
    public const string SizeSliderTrack = "size.slider_track";
    /// <summary>スライダのつまみ。</summary>
    public const string SizeSliderThumb = "size.slider_thumb";
    /// <summary>スライダのつまみ（押している間）。</summary>
    public const string SizeSliderThumbPressed = "size.slider_thumb_pressed";
    /// <summary>進捗の棒の太さ。</summary>
    public const string SizeProgressBar = "size.progress_bar";
    /// <summary>進捗の輪の太さ。</summary>
    public const string SizeRingThickness = "size.ring_thickness";
    /// <summary>ラジオの点。</summary>
    public const string SizeRadioDot = "size.radio_dot";
    /// <summary>影のぼかし。</summary>
    public const string SizeShadowBlur = "size.shadow_blur";
    /// <summary>影のずれ（下へ）。</summary>
    public const string SizeShadowOffset = "size.shadow_offset";
    /// <summary>ホイールの行の高さ（W2-5。Flutter の CupertinoDatePicker の itemExtent 32）。</summary>
    public const string SizeWheelItem = "size.wheel_item";
    /// <summary>ホイールの中央の帯の左右の余白（W2-5。Flutter の選択の帯の余白 9）。</summary>
    public const string SizeWheelBandInset = "size.wheel_band_inset";
    /// <summary>
    /// アイコンの大きさ（2026-10-02 の部品の拡充。トーストの先頭のアイコン・ダイアログの選択肢の一覧の項目のアイコンの既定。
    /// Material のアイコンの標準の 24）。<c>UiIcon.Size</c> が 0 のときに使う。
    /// </summary>
    public const string SizeIcon = "size.icon";
    /// <summary>アイコンと文字の間（同上。Material 3 のメニューの項目の先頭のアイコンと文字の間 12 dp。値は記憶による）。</summary>
    public const string SizeIconGap = "size.icon_gap";
    /// <summary>
    /// 不定の進捗（<c>ProgressSpinner</c>）の大きさ（弧の外側の直径。部品の Size が 0 のとき。2026-10-02。
    /// Flutter の CircularProgressIndicator の既定の 36×36〈year2023 の Material 3〉）。
    /// </summary>
    public const string SizeSpinner = "size.spinner";
    /// <summary>不定の進捗の弧の太さ（部品の Thickness が 0 のとき。Flutter の strokeWidth 4）。</summary>
    public const string SizeSpinnerThickness = "size.spinner_thickness";

    // ── 文字の大きさ ────────────────────────────────────────
    /// <summary>見出し。</summary>
    public const string TextTitle = "text.title";
    /// <summary>本文。</summary>
    public const string TextBody = "text.body";
    /// <summary>ボタン・項目の文字。</summary>
    public const string TextLabel = "text.label";
    /// <summary>注記。</summary>
    public const string TextCaption = "text.caption";
    /// <summary>ホイールの行の文字（W2-5。Flutter の日時のホイールの文字 21）。</summary>
    public const string TextWheel = "text.wheel";

    // ── 書体（W2-9）────────────────────────────────────────
    /// <summary>部品の文字の書体（assets:// の .ttf / .otf。空 = 組み込みの書体）。</summary>
    public const string FontFamily = "font.family";
    /// <summary>部品の文字の太さ（SEED.Text.Weight。キャンバスの単位。0 = 書体そのまま・正で太く）。</summary>
    public const string FontWeight = "font.weight";
    /// <summary>見出しの文字の太さ（ダイアログの題・ThemeStyle で見出しに当てたもの）。</summary>
    public const string FontWeightTitle = "font.weight_title";

    // ── 動き（秒）────────────────────────────────────────────
    /// <summary>短い動き（スイッチのつまみ・押下の色）。</summary>
    public const string MotionShort = "motion.short";
    /// <summary>中くらいの動き（進捗の伸び）。</summary>
    public const string MotionMedium = "motion.medium";
    /// <summary>長押しの連続の最初の間隔（連続は長押し〈500ms〉が決まった時点で始まる）。</summary>
    public const string MotionRepeatInterval = "motion.repeat_interval";
    /// <summary>長押しの連続の最短の間隔。</summary>
    public const string MotionRepeatMinInterval = "motion.repeat_min_interval";
    /// <summary>長押しの連続の間隔が 1 秒ごとに何倍になるか（1 未満で速くなる）。</summary>
    public const string MotionRepeatAccel = "motion.repeat_accel";
    /// <summary>ホイールをタップ・スクリプト・キーボードで動かす時間と、時刻ホイールの午前/午後の連動（W2-5。Flutter の 300ms）。</summary>
    public const string MotionWheel = "motion.wheel";
    /// <summary>ホイールが選べない行に止まったとき、選べる行へ戻す時間（W2-5。Flutter の CupertinoDatePicker の 200ms）。</summary>
    public const string MotionWheelCorrect = "motion.wheel_correct";
    /// <summary>テーマを動きありで切り替えるときの色の補間の時間（W2-9。UiTheme.Apply(…, animate: true)）。</summary>
    public const string MotionTheme = "motion.theme";
    /// <summary>テーマの色の補間の曲線（4 つの成分 .x1・.y1・.x2・.y2。W2-9）。</summary>
    public const string MotionThemeCurve = "motion.theme_curve";
    /// <summary>
    /// フルスワイプの「削除」の文字の置き場（元の位置 ↔ Front の後ろの端）の補間の時間（SwipeActions。W2 の手直し P2-3。
    /// 既定 0.15 秒は docs/backlog.md の案。iOS の値は公開されていないので決めた値）。
    /// </summary>
    public const string MotionSwipeFull = "motion.swipe_full";
    /// <summary>フルスワイプの確定で行の見た目を外へ流し切る時間（SwipeActions。W2 の手直し P2-3。既定 0.2 秒は backlog の案）。</summary>
    public const string MotionSwipeDismiss = "motion.swipe_dismiss";
    /// <summary>消した行の高さを畳む時間（部品は読まない。一覧の持ち主〈見本の UiGallerySections〉が使う。W2 の手直し P2-3。既定 0.2 秒は backlog の案）。</summary>
    public const string MotionSwipeCollapse = "motion.swipe_collapse";
    /// <summary>
    /// 不定の進捗（ProgressSpinner）の弧が伸びて縮む 1 周期（秒。2026-10-02。Flutter の CircularProgressIndicator の 1333 ms。
    /// 2026-10-02 に flutter/flutter master の progress_indicator.dart で確かめた）。
    /// </summary>
    public const string MotionSpinnerCycle = "motion.spinner_cycle";
    /// <summary>不定の進捗の全体が 1 回転する時間（秒。同じく Flutter の 2222 ms）。</summary>
    public const string MotionSpinnerRotation = "motion.spinner_rotation";

    // ── 濃さ（0..1）──────────────────────────────────────────
    /// <summary>押下の重ね色の濃さ。</summary>
    public const string OpacityPressed = "opacity.pressed";
    /// <summary>無効の部品の濃さ。</summary>
    public const string OpacityDisabled = "opacity.disabled";
    /// <summary>影の濃さ。</summary>
    public const string OpacityShadow = "opacity.shadow";
    /// <summary>ホイールの中央の帯の外の行の濃さ（W2-5。Flutter の _kOverAndUnderCenterOpacity 0.447）。</summary>
    public const string OpacityWheelDim = "opacity.wheel_dim";

    // ── 割合（0..1）──────────────────────────────────────────
    /// <summary>
    /// フルスワイプで構えるずらし量（行の幅に対する割合。SwipeActions。W2 の手直し P2-3。既定 0.6 は docs/backlog.md の案。
    /// iOS の値は公開されていないので決めた値。操作のボタンの幅より手前では構えない）。
    /// </summary>
    public const string RatioSwipeFull = "ratio.swipe_full";
    /// <summary>構えた後に解くずらし量（行の幅に対する割合。構える割合より小さくして行き来でばたつかない。既定 0.55 は backlog の案）。</summary>
    public const string RatioSwipeFullCancel = "ratio.swipe_full_cancel";

    /// <summary>
    /// 部品が読むすべてのトークン（曲線の <see cref="MotionThemeCurve"/> は 4 つの成分へ展開済み。
    /// ギャラリー・テストが既定のテーマに揃っているかを確かめる）。
    /// </summary>
    public static readonly string[] All =
    {
        ColorPrimary, ColorOnPrimary, ColorBackground, ColorSurface, ColorSurfaceVariant, ColorOnSurface,
        ColorOnSurfaceMuted, ColorOutline, ColorSelected, ColorOnSelected, ColorKnob, ColorKnobOff,
        ColorStateLayer, ColorDisabled, ColorOnDisabled, ColorShadow, ColorError, ColorOnError,
        RadiusButton, RadiusChip, RadiusCard, RadiusField, RadiusSegment, RadiusCheckbox, RadiusProgress, RadiusWheelBand,
        SpaceXs, SpaceS, SpaceM, SpaceL, SpaceXl,
        SizeTouchMin, SizeBorder, SizeCheckBorder, SizeToggleKnob, SizeToggleKnobOff, SizeToggleInset,
        SizeSliderTrack, SizeSliderThumb, SizeSliderThumbPressed, SizeProgressBar, SizeRingThickness,
        SizeRadioDot, SizeShadowBlur, SizeShadowOffset, SizeWheelItem, SizeWheelBandInset,
        SizeIcon, SizeIconGap, SizeSpinner, SizeSpinnerThickness,
        TextTitle, TextBody, TextLabel, TextCaption, TextWheel,
        FontFamily, FontWeight, FontWeightTitle,
        MotionShort, MotionMedium, MotionRepeatInterval, MotionRepeatMinInterval, MotionRepeatAccel,
        MotionWheel, MotionWheelCorrect, MotionTheme,
        MotionThemeCurve + UiCurve.SuffixX1, MotionThemeCurve + UiCurve.SuffixY1,
        MotionThemeCurve + UiCurve.SuffixX2, MotionThemeCurve + UiCurve.SuffixY2,
        MotionSwipeFull, MotionSwipeDismiss, MotionSwipeCollapse, MotionSpinnerCycle, MotionSpinnerRotation,
        OpacityPressed, OpacityDisabled, OpacityShadow, OpacityWheelDim,
        RatioSwipeFull, RatioSwipeFullCancel,
    };
}
