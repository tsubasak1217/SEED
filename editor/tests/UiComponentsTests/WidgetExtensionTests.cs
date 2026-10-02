using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using SEED;
using SEED.UI;
using SpriteRigTests; // テストランナー（TestHarness / Check）を共有する

namespace UiComponentsTests;

/// <summary>
/// 2026-10-02 の部品の拡充（Wake or Pay の W3 で見つかった制限の解消）のうち、基本の部品の純粋な計算のテスト（docs/ui_components.md §6）:
/// ボタンの危険の色（ButtonTone）・文字の枠をボタンの大きさに合わせる（ButtonLabelFit）・スライダの溝をレイアウトの幅に合わせる（SliderGeometry）・
/// 大きさの見張り（LayoutSizeWatch）・文字の大きさの指定（UiTextSize）・大きさと太さの上書き（UiSizeOverride）・不定の進捗の弧（SpinnerMotion。
/// 参照の値は Flutter の progress_indicator.dart の式を Python で独立に書いて求めた）・アイコン（UiIconLooks）・入力欄の余白と選択の禁止・
/// 新しいトークンの既定の値・プレハブ（progress_spinner.actor・toast.actor・dialog_item.actor）の作り・ギャラリーの見本。
/// </summary>
public static class WidgetExtensionTests
{
    /// <summary>浮動小数の比較の許容量。</summary>
    private const double Eps = 1e-4;
    /// <summary>弧の角度の比較の許容量（度。曲線の逆算の丸めの差）。</summary>
    private const double AngleEps = 0.05;

    public static void Register(TestHarness h, UiThemeData theme)
    {
        // ── ボタンの危険の色 ─────────────────────────────────────
        h.Add("拡充 ボタン: 色の役割なしの Resolve は Primary と同じ（既定の見た目は変わらない）", () =>
        {
            foreach (ButtonVariant v in Enum.GetValues(typeof(ButtonVariant)))
                foreach (bool pressed in new[] { false, true })
                    foreach (bool disabled in new[] { false, true })
                        Check.Equal(ButtonLooks.Resolve(v, ButtonTone.Primary, pressed, disabled, theme), ButtonLooks.Resolve(v, pressed, disabled, theme),
                            $"{v} pressed={pressed} disabled={disabled}");
        });

        h.Add("拡充 ボタン: 危険（Danger）は color.error・color.on_error（Filled・Tonal は塗り、Outlined は枠と文字、Text は文字）・無効は同じ灰色", () =>
        {
            var error = theme.Color(UiTokens.ColorError);
            var onError = theme.Color(UiTokens.ColorOnError);
            var filled = ButtonLooks.Resolve(ButtonVariant.Filled, ButtonTone.Danger, false, false, theme);
            Check.Equal(error, filled.Background, "Filled の塗りはエラーの色");
            Check.Equal(onError, filled.Content, "Filled の文字はエラーの上の色");
            var tonal = ButtonLooks.Resolve(ButtonVariant.Tonal, ButtonTone.Danger, false, false, theme);
            Check.Equal(error, tonal.Background, "Tonal の危険は塗りと同じ（errorContainer のトークンが無い）");
            var outlined = ButtonLooks.Resolve(ButtonVariant.Outlined, ButtonTone.Danger, false, false, theme);
            Check.Close(0, outlined.Background.a, Eps, "Outlined の背景は透明");
            Check.Equal(error, outlined.Border, "Outlined の枠はエラーの色");
            Check.Equal(error, outlined.Content, "Outlined の文字はエラーの色");
            var text = ButtonLooks.Resolve(ButtonVariant.Text, ButtonTone.Danger, false, false, theme);
            Check.Equal(error, text.Content, "Text の文字はエラーの色");
            var pressed = ButtonLooks.Resolve(ButtonVariant.Filled, ButtonTone.Danger, true, false, theme);
            Check.True(pressed.Background != filled.Background, "押下は重ね色");
            Check.Equal(ButtonLooks.Resolve(ButtonVariant.Filled, false, true, theme), ButtonLooks.Resolve(ButtonVariant.Filled, ButtonTone.Danger, false, true, theme),
                "無効は色の役割に依らず同じ");
        });

        // ── ボタンの文字の枠 ─────────────────────────────────────
        h.Add("拡充 ボタン: 文字の枠 = レイアウトの大きさ ＋（プレハブの枠 − 大きさ）。伸ばしていなければプレハブのまま・枠なしの軸は 0 のまま", () =>
        {
            var baseSize = new Vector2(120f, 48f);
            var baseBox = new Vector2(120f, 48f);
            Check.Equal(baseBox, ButtonLabelFit.BoxSize(baseSize, baseSize, baseBox), "button.actor を伸ばしていない: プレハブの枠のまま");
            Check.Equal(new Vector2(328f, 48f), ButtonLabelFit.BoxSize(new Vector2(328f, 48f), baseSize, baseBox), "幅いっぱい（328）: 枠も 328（文字が真ん中）");
            // dialog.actor のボタン（枠 320・真ん中に置いた文字）: 差 232 を保つ
            Check.Equal(new Vector2(132f + 232f, 40f), ButtonLabelFit.BoxSize(new Vector2(132f, 40f), new Vector2(88f, 40f), new Vector2(320f, 40f)), "差を保つ");
            Check.Equal(new Vector2(0f, 48f), ButtonLabelFit.BoxSize(new Vector2(328f, 48f), baseSize, new Vector2(0f, 48f)), "枠なし（0）の軸は 0 のまま");
            Check.Equal(baseBox, ButtonLabelFit.BoxSize(new Vector2(float.NaN, 0f), baseSize, baseBox), "測れない大きさはプレハブのまま");
            Check.Equal(new Vector2(0f, 48f), ButtonLabelFit.BoxSize(new Vector2(10f, 48f), baseSize, new Vector2(40f, 48f)), "負にしない");
        });

        // ── スライダの溝 ─────────────────────────────────────────
        h.Add("拡充 スライダ: 溝の長さはレイアウトの幅 − プレハブの左右の余白・高さは割合・伸ばしていなければプレハブのまま", () =>
        {
            var b = new SliderBase(240f, 48f, 10f, 220f, 24f);
            Check.Equal(new SliderTrack(10f, 220f, 24f), SliderGeometry.FromBase(b), "プレハブのまま");
            Check.Equal(new SliderTrack(10f, 220f, 24f), SliderGeometry.Resolve(b, 240f, 48f), "伸ばしていない（240×48）: プレハブと同じ（既定の見た目は変わらない）");
            Check.Equal(new SliderTrack(10f, 308f, 24f), SliderGeometry.Resolve(b, 328f, 48f), "幅いっぱい 328: 溝 308（左右 10 ずつ）");
            Check.Equal(new SliderTrack(10f, 391.43f - 20f, 24f), SliderGeometry.Resolve(b, 391.43f, 48f), "411 dp の画面の幅");
            Check.Equal(new SliderTrack(10f, 220f, 32f), SliderGeometry.Resolve(b, 240f, 64f), "高さ 64: 溝の中心は割合 0.5 で 32");
            Check.Equal(new SliderTrack(10f, 0f, 24f), SliderGeometry.Resolve(b, 12f, 48f), "余白より狭い: 長さ 0（負にしない）");
            Check.Equal(SliderGeometry.FromBase(b), SliderGeometry.Resolve(b, float.NaN, -1f), "壊れた値: プレハブのまま");
            // 左右で余白の違うプレハブ（左 16・右 8）は余白をそれぞれ保つ
            var uneven = new SliderBase(200f, 40f, 16f, 176f, 20f);
            Check.Equal(new SliderTrack(16f, 276f, 20f), SliderGeometry.Resolve(uneven, 300f, 40f), "左 16・右 8 の余白を保つ");
            // 指の位置 → 値（伸ばした溝で端から端まで届く）
            var wide = SliderGeometry.Resolve(b, 328f, 48f);
            Check.Close(100, ValueMath.ValueAt(wide.X + wide.Length, wide.X, wide.Length, 0f, 100f, 0f), Eps, "伸ばした溝の右端は最大");
            Check.Close(50, ValueMath.ValueAt(wide.X + wide.Length / 2f, wide.X, wide.Length, 0f, 100f, 0f), Eps, "真ん中は半分");
        });

        h.Add("拡充 大きさの見張り: 最初は必ず変わった・差が許す差以下なら変わらない・壊れた値は受け入れない・Reset で数え直す", () =>
        {
            var watch = new LayoutSizeWatch();
            Check.True(watch.Last is null, "まだ無い");
            Check.True(watch.Update(new Vector2(240f, 48f)), "最初は変わった");
            Check.True(!watch.Update(new Vector2(240.005f, 48f)), "丸めの差（0.005）は変わらない");
            Check.True(watch.Update(new Vector2(328f, 48f)), "幅が変わった");
            Check.True(!watch.Update(new Vector2(float.NaN, 48f)), "NaN は受け入れない");
            Check.Equal(new Vector2(328f, 48f), watch.Last!.Value, "最後に受け入れた大きさ");
            watch.Reset();
            Check.True(watch.Update(new Vector2(328f, 48f)), "Reset の後は同じ大きさでも変わった");
        });

        // ── 文字の大きさ・大きさと太さの上書き ───────────────────────
        h.Add("拡充 文字の大きさの指定: トークン・app のトークン・数・空・読めない指定（ChipGroup・RadioGroup・Button の LabelSize）", () =>
        {
            var custom = UiThemeData.Parse("{\"app\":{\"text\":{\"chip\":18}}}", theme, out _);
            Check.True(UiTextSize.TryResolve(custom, UiTokens.TextLabel, out var label) && Math.Abs(label - 14f) < Eps, "既定 text.label = 14");
            Check.True(UiTextSize.TryResolve(custom, "app.text.chip", out var chip) && Math.Abs(chip - 18f) < Eps, "アプリ独自のトークン");
            Check.True(UiTextSize.TryResolve(custom, "17.5", out var number) && Math.Abs(number - 17.5f) < Eps, "数そのもの");
            Check.True(!UiTextSize.TryResolve(custom, "", out _), "空は変えない");
            Check.True(!UiTextSize.TryResolve(custom, "text.nope", out _), "テーマに無く数でもない名前は変えない");
            Check.True(!UiTextSize.TryResolve(custom, "-3", out _) && !UiTextSize.TryResolve(custom, "0", out _), "0 以下は変えない");
            Check.Close(9, UiTextSize.Resolve(custom, "text.nope", 9f), Eps, "決まらなければ今の大きさ");
        });

        h.Add("拡充 大きさと太さの上書き: 正の値は上書き・0 以下と壊れた値はテーマ（ProgressRing.Thickness・ProgressSpinner.Size / Thickness）", () =>
        {
            Check.Close(3, UiSizeOverride.Resolve(3f, 6f), Eps, "上書き 3");
            Check.Close(6, UiSizeOverride.Resolve(0f, 6f), Eps, "0 はテーマ");
            Check.Close(6, UiSizeOverride.Resolve(-1f, 6f), Eps, "負はテーマ");
            Check.Close(6, UiSizeOverride.Resolve(float.NaN, 6f), Eps, "NaN はテーマ");
            Check.Close(0, UiSizeOverride.Resolve(0f, float.NaN), Eps, "テーマも壊れていれば 0");
            Check.Close(theme.Number(UiTokens.SizeRingThickness), UiSizeOverride.Resolve(0f, theme.Number(UiTokens.SizeRingThickness)), Eps,
                "既定のプレハブ（Thickness 0）は従来どおり size.ring_thickness");
        });

        // ── 不定の進捗（スピナー）─────────────────────────────────
        h.Add("拡充 スピナー: Flutter の不定の動き（頭・尾・回転）と同じ弧（参照は Python で独立に計算した値）", () =>
        {
            float cycle = theme.Number(UiTokens.MotionSpinnerCycle), rotation = theme.Number(UiTokens.MotionSpinnerRotation);
            (double t, double start, double sweep)[] reference =
            {
                (0.0, 270.0000, 0.0573), (0.1, 292.9533, 17.8230), (0.3, 338.8599, 190.2018), (0.6665, 62.9838, 270.0000),
                (0.9, 251.8075, 134.7723), (1.2, 88.7948, 6.6448), (2.0, 279.0665, 269.9997), (2.222, 93.8347, 146.1878), (5.0, 277.7028, 59.9626),
            };
            foreach (var (t, start, sweep) in reference)
            {
                var arc = SpinnerMotion.At(t, cycle, rotation);
                Check.Close(start, arc.StartDegrees, AngleEps, $"t={t} の始まり");
                Check.Close(sweep, arc.SweepDegrees, AngleEps, $"t={t} の角度");
            }
        });

        h.Add("拡充 スピナー: 周期の境目で途切れない・前半は伸びるだけ・角度は 0 にならない・壊れた周期は最初の姿・角度は 0〜360", () =>
        {
            float cycle = 1.333f, rotation = 2.222f;
            // 周期の境目の前後で、始まり（360 で畳む）と角度がほぼ同じ
            var before = SpinnerMotion.At(cycle - 1e-4, cycle, rotation);
            var after = SpinnerMotion.At(cycle + 1e-4, cycle, rotation);
            double diff = Math.Abs(before.StartDegrees - after.StartDegrees);
            Check.True(Math.Min(diff, 360.0 - diff) < 0.2, $"境目の始まりがつながる（差 {diff}）");
            Check.True(Math.Abs(before.SweepDegrees - after.SweepDegrees) < 0.2, "境目の角度がつながる");
            // 前半（頭だけが進む）は角度が増えるだけ
            float previous = -1f;
            for (int i = 0; i <= 20; i++)
            {
                var a = SpinnerMotion.At(cycle * 0.5 * i / 20, cycle, rotation);
                Check.True(a.SweepDegrees >= previous - 1e-3f, $"前半は伸びる（{i}）");
                previous = a.SweepDegrees;
                Check.True(a.StartDegrees >= 0f && a.StartDegrees < 360f, "始まりは 0〜360");
                Check.True(a.SweepDegrees >= SpinnerMotion.MinSweepDegrees - 1e-6f && a.SweepDegrees <= SpinnerMotion.ArcTravelDegrees + 1e-3f, "角度は ε〜270");
            }
            Check.Equal(SpinnerMotion.At(0, cycle, rotation), SpinnerMotion.At(10, 0f, float.NaN), "周期が壊れていれば最初の姿（動かない）");
            Check.Equal(SpinnerMotion.At(0, cycle, rotation), SpinnerMotion.At(double.NaN, cycle, rotation), "壊れた時刻は 0");
            Check.Close(0, SpinnerMotion.NormalizeDegrees(360f), Eps, "360 は 0");
            Check.Close(350, SpinnerMotion.NormalizeDegrees(-10f), Eps, "−10 は 350");
            Check.Close(0, SpinnerMotion.NormalizeDegrees(-1e-7f), Eps, "ごく小さな負は 360 にならない");
        });

        // ── アイコン ─────────────────────────────────────────────
        h.Add("拡充 アイコン: 無ければ出さない・画像は白（色のまま）・図形は部品の既定の色・輪と四角はテーマ・大きさの上書き・薄め・文字の左端", () =>
        {
            var fallback = new Color(0.2f, 0.3f, 0.4f, 1f);
            Check.True(!UiIconLooks.Resolve(null, theme, fallback).Visible, "null は出さない");
            var image = UiIconLooks.Resolve(UiIcon.Image("assets://ui/textures/check.png"), theme, fallback);
            Check.True(image.Visible && image.IsImage && image.ImagePath == "assets://ui/textures/check.png", "画像");
            Check.Equal(new Color(1f, 1f, 1f, 1f), image.Color, "画像の既定の色は白");
            Check.Close(theme.Number(UiTokens.SizeIcon), image.Size, Eps, "大きさは size.icon");
            var circle = UiIconLooks.Resolve(UiIcon.Circle(), theme, fallback);
            Check.True(!circle.IsImage && circle.Shape == UiIconShape.Circle, "円");
            Check.Equal(fallback, circle.Color, "図形の既定の色は部品の色");
            var ring = UiIconLooks.Resolve(UiIcon.Ring(new Color(1f, 0f, 0f, 1f), 20f), theme, fallback);
            Check.Close(theme.Number(UiTokens.SizeCheckBorder), ring.RingWidth, Eps, "輪の太さは size.check_border");
            Check.Close(20, ring.Size, Eps, "大きさの上書き");
            Check.Equal(new Color(1f, 0f, 0f, 1f), ring.Color, "色の指定");
            var square = UiIconLooks.Resolve(UiIcon.Square(), theme, fallback);
            Check.Close(theme.Number(UiTokens.RadiusCheckbox), square.CornerRadius, Eps, "四角の角丸は radius.checkbox");
            var faded = UiIconLooks.Resolve(UiIcon.Image("x.png"), theme, fallback, 0.38f);
            Check.Close(0.38, faded.Color.a, Eps, "薄める（画像も）");
            Check.Close(16 + 24 + 12, UiIconLooks.TextStart(circle, 16f, 12f), Eps, "文字の左端 = 余白 ＋ アイコン ＋ 間");
            Check.Close(16, UiIconLooks.TextStart(UiIconLook.Hidden, 16f, 12f), Eps, "アイコンが無ければ余白のまま");
        });

        // ── 入力欄 ───────────────────────────────────────────────
        h.Add("拡充 入力欄: 欄ごとの左右の余白（負 = テーマ・0 は 0）と選択を許さない欄（長押し・フォーカスの全選択・選択を畳む）", () =>
        {
            float themePadding = theme.Number(TextFieldTokens.SizeFieldPadding);
            Check.Close(themePadding, TextFieldLayout.ResolvePadding(TextFieldLayout.ThemePadding, themePadding), Eps, "既定（−1）はテーマの 16");
            Check.Close(8, TextFieldLayout.ResolvePadding(8f, themePadding), Eps, "上書き 8");
            Check.Close(0, TextFieldLayout.ResolvePadding(0f, themePadding), Eps, "0 は 0（余白なし）");
            Check.Close(themePadding, TextFieldLayout.ResolvePadding(float.NaN, themePadding), Eps, "NaN はテーマ");
            Check.True(TextFieldSelectionPolicy.SelectAllOnFocus(true, true, false) && TextFieldSelectionPolicy.SelectAllOnFocus(true, false, true), "許す欄は全選択する");
            Check.True(!TextFieldSelectionPolicy.SelectAllOnFocus(false, true, true), "許さない欄は全選択しない");
            Check.True(TextFieldSelectionPolicy.LongPressSelectsAll(true) && !TextFieldSelectionPolicy.LongPressSelectsAll(false), "長押しの全選択");
            var selected = new TextInputState("12345", 1, 4, TextInputState.NoIndex, TextInputState.NoIndex, 0);
            Check.Equal<int?>(4, TextFieldSelectionPolicy.CollapseTo(false, selected), "許さない欄の選択はカーソル（動いた端 4）へ畳む");
            Check.Equal<int?>(null, TextFieldSelectionPolicy.CollapseTo(true, selected), "許す欄は畳まない");
            var caretOnly = new TextInputState("12345", 2, 2, TextInputState.NoIndex, TextInputState.NoIndex, 0);
            Check.Equal<int?>(null, TextFieldSelectionPolicy.CollapseTo(false, caretOnly), "選択が無ければ畳まない");
            var reversed = new TextInputState("12345", 4, 1, TextInputState.NoIndex, TextInputState.NoIndex, 0);
            Check.Equal<int?>(1, TextFieldSelectionPolicy.CollapseTo(false, reversed), "逆向きの選択も動いた端（1）へ");
        });

        h.Add("入力欄: 選択を許さない欄は場へコピー・切り取りの禁止も渡す（畳む前の Ctrl+A → Ctrl+C を写さない。レビュー #10）", () =>
        {
            Check.True(TextFieldSelectionPolicy.SessionAllowsCopy(true, true), "コピーも選択も許す欄だけ写せる（既定の欄は従来どおり）");
            Check.True(!TextFieldSelectionPolicy.SessionAllowsCopy(true, false), "選択を許さない欄は AllowCopy = true でも写さない");
            Check.True(!TextFieldSelectionPolicy.SessionAllowsCopy(false, true), "コピーを許さない欄は写さない（従来どおり）");
            Check.True(!TextFieldSelectionPolicy.SessionAllowsCopy(false, false), "どちらも許さない欄は写さない");
            // エンジンへ渡す旗（Rust の config.rs の FLAG_DISALLOW_COPY。session.rs は EditKey::Copy / Cut を allow_copy で判定する）
            var locked = new TextInputOptions { AllowCopy = TextFieldSelectionPolicy.SessionAllowsCopy(true, false) };
            Check.Equal(TextInputOptions.FlagDisallowCopy, locked.Flags & TextInputOptions.FlagDisallowCopy, "選択を許さない欄の場は FLAG_DISALLOW_COPY を立てる");
            var open = new TextInputOptions { AllowCopy = TextFieldSelectionPolicy.SessionAllowsCopy(true, true) };
            Check.Equal(0, open.Flags, "既定の欄の場は旗なし（従来どおり）");
        });

        // ── トークン ─────────────────────────────────────────────
        h.Add("拡充 トークン: 新しいトークンが既定のテーマに出典どおりの値である", () =>
        {
            Check.Close(24, theme.Number(UiTokens.SizeIcon), Eps, "size.icon 24（Material のアイコン）");
            Check.Close(12, theme.Number(UiTokens.SizeIconGap), Eps, "size.icon_gap 12");
            Check.Close(36, theme.Number(UiTokens.SizeSpinner), Eps, "size.spinner 36（Flutter の 36×36）");
            Check.Close(4, theme.Number(UiTokens.SizeSpinnerThickness), Eps, "size.spinner_thickness 4（Flutter の strokeWidth）");
            Check.Close(1.333, theme.Number(UiTokens.MotionSpinnerCycle), Eps, "motion.spinner_cycle 1.333 秒");
            Check.Close(2.222, theme.Number(UiTokens.MotionSpinnerRotation), Eps, "motion.spinner_rotation 2.222 秒");
            Check.Close(24, theme.Number(NavTokens.SizeDialogMargin), Eps, "size.dialog_margin 24（Flutter の insetPadding の縦）");
            Check.Close(0, theme.Number(NavTokens.SizeDialogActionsOverflowGap), Eps, "size.dialog_actions_overflow_gap 0（OverflowBar の既定）");
            Check.Close(48, theme.Number(NavTokens.SizeDialogItemHeight), Eps, "size.dialog_item_height 48");
            Check.Close(12, theme.Number(NavTokens.SizeDialogItemsInset), Eps, "size.dialog_items_inset 12（SimpleDialog の contentPadding の上）");
        });

        // ── プレハブ・見本 ─────────────────────────────────────────
        h.Add("拡充 プレハブ: progress_spinner.actor（弧・丸い端・スクリプト）・toast.actor（Icon は隠して並べない・Label の前）", () =>
        {
            var spinner = Load("prefabs", "progress_spinner.actor");
            var sprite = Data(spinner, "SpriteComponent");
            Check.Equal("arc", sprite.GetProperty("shape").GetProperty("kind").GetString(), "弧");
            Check.True(sprite.GetProperty("shape").GetProperty("arc_round_caps").GetBoolean(), "丸い端");
            Check.Close(theme.Number(UiTokens.SizeSpinner), sprite.GetProperty("width").GetSingle(), Eps, "大きさ = size.spinner");
            Check.Close(theme.Number(UiTokens.SizeSpinnerThickness), sprite.GetProperty("shape").GetProperty("arc_thickness").GetSingle(), Eps, "太さ");
            Check.Equal("SEED.UI.ProgressSpinner", Data(spinner, "ScriptComponent").GetProperty("type_name").GetString(), "スクリプト");

            var toast = Load("prefabs", "toast.actor");
            var names = Children(toast).Select(Name).ToArray();
            Check.Equal("Icon,Label", string.Join(",", names), "Icon は Label の前");
            var icon = Child(toast, "Icon");
            Check.True(icon.TryGetProperty("visible", out var visible) && !visible.GetBoolean(), "Icon は既定で隠す（アイコンの無いトーストは従来どおり）");
            Check.True(Data(icon, "CanvasLayoutItemComponent").GetProperty("ignore_layout").GetBoolean(), "Icon は並べない");
            float labelX = Child(toast, "Label").GetProperty("canvas_transform").GetProperty("position")[0].GetSingle();
            Check.Close(labelX, icon.GetProperty("canvas_transform").GetProperty("position")[0].GetSingle(), Eps, "Icon の左端 = Label の左端（左の余白）");
        });

        h.Add("拡充 見本: ギャラリーにスピナー・太さを変えた輪・幅いっぱいのスライダとボタン・新しいダイアログとトーストのボタンがある", () =>
        {
            var root = Load("scenes", "ui_gallery.scene").GetProperty("actors")[0];
            foreach (var name in new[] { "Spinner", "SpinnerLarge", "SpinnerSmall", "RingThin", "RingThick", "WideSlider", "WideButton",
                         "NavDanger", "NavMenu", "NavStacked", "NavProgress", "NavLong", "NavToastIcon" })
                Check.True(Find(root, name) is not null, $"{name} がある");
            var wideRow = Find(root, "WideRow")!.Value;
            Check.Equal("stretch", Data(wideRow, "CanvasStackComponent").GetProperty("cross_align").GetString(), "WideRow は子を幅いっぱいに伸ばす");
            var thin = Data(Find(root, "RingThin")!.Value, "ScriptComponent").GetProperty("fields");
            Check.Equal("3.0", thin.GetProperty("Thickness").GetString(), "太さの上書きの見本");
        });
    }

    // ── JSON の読み方 ─────────────────────────────────────────────

    /// <summary>出力へ写したプレハブ・シーンの JSON（ルート）。</summary>
    internal static JsonElement Load(string folder, string file)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, folder, file)));
        return doc.RootElement.Clone();
    }

    /// <summary>ノードの名前。</summary>
    internal static string Name(JsonElement node) => node.GetProperty("name").GetString() ?? "";

    /// <summary>ノードの子の並び。</summary>
    internal static System.Collections.Generic.IEnumerable<JsonElement> Children(JsonElement node)
        => node.TryGetProperty("children", out var kids) ? kids.EnumerateArray() : Enumerable.Empty<JsonElement>();

    /// <summary>名前の直下の子（無ければ失敗）。</summary>
    internal static JsonElement Child(JsonElement node, string name)
        => Children(node).FirstOrDefault(c => Name(c) == name) is { ValueKind: JsonValueKind.Object } found
            ? found
            : throw new AssertionException($"{Name(node)} に子 {name} が無い");

    /// <summary>名前のノードを深さ優先で探す。</summary>
    internal static JsonElement? Find(JsonElement node, string name)
    {
        if (Name(node) == name) return node;
        foreach (var c in Children(node))
            if (Find(c, name) is { } found) return found;
        return null;
    }

    /// <summary>ノードの最初のその型のコンポーネントの data（無ければ失敗）。</summary>
    internal static JsonElement Data(JsonElement node, string type)
        => node.GetProperty("components").EnumerateArray()
            .Select(c => c.GetProperty("component"))
            .First(c => c.GetProperty("type").GetString() == type)
            .GetProperty("data");
}
