namespace SEED.UI;

// ============================================================
//  UiTokens.cs — 部品が読むテーマのトークンの名前の一覧（W2-4）
//
//  トークン = テーマの JSON の「グループ.名前」（例 color.primary・radius.button）。値の表（既定）は
//  Theme/default_theme.json（SEEDScripting に埋め込み）。部品はここの名前だけを使い、値を直接書かない
//  （テーマの差し替えで全部品の見た目が変わる。W2-9 で本格化）。名前と役割の正典は docs/ui_components.md §5。
//  足すときは default_theme.json・docs の表にも足すこと（無い名前は既定のテーマ → 最後の既定値の順で引く）。
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
    /// <summary>エラー（W2-6 の入力欄）。</summary>
    public const string ColorError = "color.error";

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

    // ── 文字の大きさ ────────────────────────────────────────
    /// <summary>見出し。</summary>
    public const string TextTitle = "text.title";
    /// <summary>本文。</summary>
    public const string TextBody = "text.body";
    /// <summary>ボタン・項目の文字。</summary>
    public const string TextLabel = "text.label";
    /// <summary>注記。</summary>
    public const string TextCaption = "text.caption";

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

    // ── 濃さ（0..1）──────────────────────────────────────────
    /// <summary>押下の重ね色の濃さ。</summary>
    public const string OpacityPressed = "opacity.pressed";
    /// <summary>無効の部品の濃さ。</summary>
    public const string OpacityDisabled = "opacity.disabled";
    /// <summary>影の濃さ。</summary>
    public const string OpacityShadow = "opacity.shadow";

    /// <summary>部品が読むすべてのトークン（ギャラリー・テストが既定のテーマに揃っているかを確かめる）。</summary>
    public static readonly string[] All =
    {
        ColorPrimary, ColorOnPrimary, ColorBackground, ColorSurface, ColorSurfaceVariant, ColorOnSurface,
        ColorOnSurfaceMuted, ColorOutline, ColorSelected, ColorOnSelected, ColorKnob, ColorKnobOff,
        ColorStateLayer, ColorDisabled, ColorOnDisabled, ColorShadow, ColorError,
        RadiusButton, RadiusChip, RadiusCard, RadiusField, RadiusSegment, RadiusCheckbox, RadiusProgress,
        SpaceXs, SpaceS, SpaceM, SpaceL, SpaceXl,
        SizeTouchMin, SizeBorder, SizeCheckBorder, SizeToggleKnob, SizeToggleKnobOff, SizeToggleInset,
        SizeSliderTrack, SizeSliderThumb, SizeSliderThumbPressed, SizeProgressBar, SizeRingThickness,
        SizeRadioDot, SizeShadowBlur, SizeShadowOffset,
        TextTitle, TextBody, TextLabel, TextCaption,
        MotionShort, MotionMedium, MotionRepeatInterval, MotionRepeatMinInterval, MotionRepeatAccel,
        OpacityPressed, OpacityDisabled, OpacityShadow,
    };
}
