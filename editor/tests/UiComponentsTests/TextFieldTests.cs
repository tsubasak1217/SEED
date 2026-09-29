using System;
using SEED;
using SEED.UI;
using SpriteRigTests; // テストランナー（TestHarness / Check）を共有する

namespace UiComponentsTests;

/// <summary>
/// 入力欄（W2-6b。docs/ui_text_input.md）の純粋な計算のテスト: 文字の置き場（揃え・はみ出しのスクロール）、カーソルを見せるスクロール、
/// タップの位置の添字（サロゲートの組を割らない）、点滅、キーボードを避ける量（重なり・隠れる高さ・持ち上げの上限）、数字の欄の値、
/// 記法の逃がし、状態 → 見た目、ダイアログの入力の区画と結果の文字。
/// </summary>
public static class TextFieldTests
{
    /// <summary>浮動小数の比較の許容量。</summary>
    private const double Eps = 1e-4;

    public static void Register(TestHarness h, UiThemeData theme)
    {
        h.Add("W2-6b 入力欄: 文字の左端（収まれば揃え・はみ出せばスクロール）", () =>
        {
            Check.Close(0, TextFieldLayout.TextStartX(50f, 200f, TextFieldAlign.Left, 30f), Eps, "左寄せで収まる = 0（スクロールは効かない）");
            Check.Close(75, TextFieldLayout.TextStartX(50f, 200f, TextFieldAlign.Center, 0f), Eps, "中央で収まる = (200 − 50) / 2");
            Check.Close(-30, TextFieldLayout.TextStartX(300f, 200f, TextFieldAlign.Center, 30f), Eps, "はみ出すと揃えに関係なくスクロールの分だけ左");
            Check.Close(0, TextFieldLayout.TextStartX(300f, 200f, TextFieldAlign.Left, -5f), Eps, "負のスクロールは 0");
        });

        h.Add("W2-6b 入力欄: カーソルを見せるスクロール（最小の動き・端へ収める）", () =>
        {
            Check.Close(0, TextFieldLayout.ScrollToReveal(40f, 250f, 190f, 200f, 2f), Eps, "はみ出さなければ 0");
            Check.Close(102, TextFieldLayout.ScrollToReveal(0f, 300f, 300f, 200f, 2f), Eps, "末尾のカーソル: 300 + 2 − 200");
            Check.Close(50, TextFieldLayout.ScrollToReveal(80f, 50f, 300f, 200f, 2f), Eps, "左へ出たカーソルは左端へ");
            Check.Close(80, TextFieldLayout.ScrollToReveal(80f, 150f, 300f, 200f, 2f), Eps, "見えていれば動かさない");
            Check.Close(102, TextFieldLayout.ScrollToReveal(999f, 150f, 300f, 200f, 2f), Eps, "今のスクロールも範囲へ収める");
        });

        h.Add("W2-6b 入力欄: タップの位置 → カーソルの添字（近い方・サロゲートの組を割らない）", () =>
        {
            float[] stops = { 0f, 10f, 20f, 30f };
            Check.Equal(0, TextFieldLayout.CaretIndexAt(stops, -5f, "abc"), "左の外は先頭");
            Check.Equal(1, TextFieldLayout.CaretIndexAt(stops, 14f, "abc"), "14 は 10 に近い");
            Check.Equal(2, TextFieldLayout.CaretIndexAt(stops, 16f, "abc"), "16 は 20 に近い");
            Check.Equal(3, TextFieldLayout.CaretIndexAt(stops, 99f, "abc"), "右の外は末尾");
            // "a😀" は UTF-16 で 3（😀 は 2 単位）。添字 2（組の間）は選ばない
            float[] emoji = { 0f, 10f, 10f, 30f };
            Check.Equal(3, TextFieldLayout.CaretIndexAt(emoji, 22f, "a😀"), "組の間ではなく後ろ");
            Check.Equal(1, TextFieldLayout.CaretIndexAt(emoji, 12f, "a😀"), "組の前");
            Check.Equal(0, TextFieldLayout.CaretIndexAt(new[] { 0f }, 5f, ""), "空の本文は 0");
        });

        h.Add("W2-6b 入力欄: 点滅（半周期ごとに切り替え・次の切り替えまでの秒）", () =>
        {
            Check.True(TextFieldLayout.CaretVisible(0f, 0.5f), "始めは見える");
            Check.True(TextFieldLayout.CaretVisible(0.49f, 0.5f), "半周期の手前は見える");
            Check.True(!TextFieldLayout.CaretVisible(0.51f, 0.5f), "半周期を過ぎたら消える");
            Check.True(TextFieldLayout.CaretVisible(1.01f, 0.5f), "1 周期で戻る");
            Check.True(TextFieldLayout.CaretVisible(7f, 0f), "周期 0 は点滅しない");
            Check.Close(0.3, TextFieldLayout.UntilBlinkToggle(0.2f, 0.5f), Eps, "0.2 秒 → 0.3 秒後");
            Check.True(float.IsPositiveInfinity(TextFieldLayout.UntilBlinkToggle(1f, 0f)), "点滅しなければ ∞");
        });

        h.Add("W2-6b キーボードを避ける: 重なり・窓の隠れる高さ・持ち上げの上限・画素と単位", () =>
        {
            float top = KeyboardInsetMath.KeyboardTop(2400f, 979f);
            Check.Close(1421, top, Eps, "キーボードの上端 = 2400 − 979（実機 R-2 の高さ）");
            Check.Close(2400, KeyboardInsetMath.KeyboardTop(2400f, float.NaN), Eps, "NaN は 0 とみなす（キーボードなし）");
            var field = new Rect(0f, 1500f, 400f, 100f);
            Check.Close(1600 + 16 - 1421, KeyboardInsetMath.OverlapPx(field, top, 16f), Eps, "欄の下端 ＋ 余白 − 上端");
            Check.Close(0, KeyboardInsetMath.OverlapPx(new Rect(0f, 100f, 400f, 100f), top, 16f), Eps, "上にあれば 0");
            var viewport = new Rect(0f, 200f, 1080f, 2100f);
            Check.Close(2300 - 1421, KeyboardInsetMath.HiddenPx(viewport, top), Eps, "窓の下端 − 上端");
            Check.Close(0, KeyboardInsetMath.HiddenPx(new Rect(0f, 0f, 100f, 1000f), top), Eps, "窓がキーボードより上なら 0");
            Check.Close(100, KeyboardInsetMath.HiddenPx(new Rect(0f, 2000f, 100f, 100f), top), Eps, "窓の高さより大きくしない");
            Check.Close(50, KeyboardInsetMath.PxToUnits(131.39f, 2.6278f), 1e-2, "画素 → 単位");
            Check.Close(2.6278, KeyboardInsetMath.PxPerUnit(1079.96f, 411f), 1e-3, "1 単位の画素数");
            Check.Close(1, KeyboardInsetMath.PxPerUnit(100f, 0f), Eps, "長さ 0 は 1");
            var card = new Rect(100f, 1000f, 800f, 600f);
            Check.Close(1600 + 16 - 1421, KeyboardInsetMath.LiftPx(card, top, 16f, 80f), Eps, "札の重なりの分だけ上げる");
            Check.Close(1000 - 80, KeyboardInsetMath.LiftPx(new Rect(100f, 1000f, 800f, 1400f), top, 16f, 80f), Eps, "上端の限りまで");
        });

        h.Add("W2-6b 数字の欄: 読めれば範囲へ収める・読めなければ前の値のまま", () =>
        {
            Check.True(NumberText.TryParseClamped("3", 1, 5, out long v) && v == 3, "範囲の中");
            Check.True(NumberText.TryParseClamped("9", 1, 5, out v) && v == 5, "上は最大へ");
            Check.True(NumberText.TryParseClamped("0", 1, 5, out v) && v == 1, "下は最小へ");
            Check.True(NumberText.TryParseClamped(" 42 ", 100, 1_000_000, out v) && v == 100, "前後の空白は無視");
            Check.True(NumberText.TryParseClamped("99999999999999999999999", 100, 1_000_000, out v) && v == 1_000_000, "桁あふれは最大へ");
            Check.True(!NumberText.TryParseClamped("", 1, 5, out _), "空は読めない");
            Check.True(!NumberText.TryParseClamped("1a", 1, 5, out _), "数字でない文字は読めない");
            Check.True(!NumberText.TryParseClamped("-3", 1, 5, out _), "符号は読まない（数字だけの欄）");
            Check.True(NumberText.TryParseClamped("3", 5, 1, out v) && v == 3, "最小と最大が逆でも収める");
            Check.Equal("1000", NumberText.Format(1000), "桁区切りなし");
        });

        h.Add("W2-6b 入力欄: 記法の逃がし（[ と { の前にバックスラッシュ）", () =>
        {
            Check.Equal("abc", TextMarkupEscape.Escape("abc"), "記法の入口が無ければそのまま");
            Check.Equal("\\[icon:x] \\{0}", TextMarkupEscape.Escape("[icon:x] {0}"), "[ と { を逃がす");
            Check.Equal("\\\\[", TextMarkupEscape.Escape("\\["), "打ったバックスラッシュはそのまま（後ろの [ だけ逃がす）");
            Check.Equal("", TextMarkupEscape.Escape(""), "空");
        });

        h.Add("W2-6b 入力欄: 状態 → 見た目（通常・フォーカス・エラー・無効・塗り）", () =>
        {
            var normal = TextFieldLooks.Resolve(new TextFieldLookState(false, false, false, false), theme);
            var focused = TextFieldLooks.Resolve(new TextFieldLookState(true, false, false, false), theme);
            var error = TextFieldLooks.Resolve(new TextFieldLookState(true, false, true, false), theme);
            var disabled = TextFieldLooks.Resolve(new TextFieldLookState(false, true, true, true), theme);
            var filled = TextFieldLooks.Resolve(new TextFieldLookState(false, false, false, true), theme);
            Check.Equal(theme.Color(UiTokens.ColorOutline), normal.Border, "通常の枠");
            Check.Close(theme.Number(UiTokens.SizeBorder), normal.BorderWidth, Eps, "通常の枠は細い");
            Check.Equal(theme.Color(UiTokens.ColorPrimary), focused.Border, "フォーカスの枠は主の色");
            Check.Close(theme.Number(TextFieldTokens.SizeFieldFocusBorder), focused.BorderWidth, Eps, "フォーカスの枠は太い");
            Check.Equal(theme.Color(UiTokens.ColorError), error.Border, "エラーの枠");
            Check.Equal(theme.Color(UiTokens.ColorError), error.Caret, "エラーのカーソル");
            Check.Equal(theme.Color(UiTokens.ColorDisabled), disabled.Border, "無効はエラーより強い");
            Check.Equal(theme.Color(UiTokens.ColorOnDisabled), disabled.Text, "無効の文字");
            Check.True(normal.Background.a == 0f, "既定は塗りなし（Outlined）");
            Check.Equal(theme.Color(UiTokens.ColorSurfaceVariant), filled.Background, "塗りの種類");
            Check.Close(theme.Number(UiTokens.RadiusField), normal.CornerRadius, Eps, "角丸 = radius.field");
        });

        h.Add("W2-6b ダイアログ: 入力欄の区画（本文とボタンの行の間・無ければ従来と同じ）と結果の文字", () =>
        {
            var without = DialogMetrics.Arrange(24f, DialogMetrics.Sections(28f, 22.4f, 40f, 16f, 24f));
            var withInput = DialogMetrics.Arrange(24f, DialogMetrics.Sections(28f, 22.4f, 40f, 16f, 24f, 52f));
            Check.Close(0, without.SlotHeights[DialogMetrics.InputSection], Eps, "入力が無ければ枠 0");
            Check.Close(without.CardHeight + 52 + 16, withInput.CardHeight, Eps, "入力の分（欄 ＋ 上の間隔）だけ高い");
            Check.Close(22.4 + 16, withInput.SlotHeights[DialogMetrics.MessageSection], Eps, "本文の下は入力の上の間隔");
            Check.Close(52 + 24, withInput.SlotHeights[DialogMetrics.InputSection], Eps, "入力の下はボタンの行の上の間隔");
            Check.Close(40, withInput.SlotHeights[DialogMetrics.ButtonsSection], Eps, "ボタンの行は最後");
            var spec = new DialogInputOptions();
            Check.Equal("田中太郎", spec.Finish("  田中太郎 　"), "前後の空白（全角も）を落とす");
            Check.Equal(" a ", new DialogInputOptions { TrimResult = false }.Finish(" a "), "TrimResult = false はそのまま");
        });

        h.Add("W2-6b 入力欄: トークンは既定のテーマにあり、値は docs の出どころどおり", () =>
        {
            Check.Close(52, theme.Number(TextFieldTokens.SizeFieldHeight), Eps, "欄の高さ");
            Check.Close(16, theme.Number(TextFieldTokens.SizeFieldPadding), Eps, "左右の余白");
            Check.Close(2, theme.Number(TextFieldTokens.SizeFieldFocusBorder), Eps, "フォーカスの枠");
            Check.Close(2, theme.Number(TextFieldTokens.SizeCaret), Eps, "カーソルの太さ");
            Check.Close(0.5, theme.Number(TextFieldTokens.MotionCaretBlink), Eps, "点滅の半周期");
            Check.Close(16, theme.Number(TextFieldTokens.TextField), Eps, "文字の大きさ");
            Check.Close(24, theme.Number(TextFieldTokens.TextFieldNumber), Eps, "数値の欄の文字の大きさ");
            Check.True(theme.Color(TextFieldTokens.ColorSelection).a > 0f && theme.Color(TextFieldTokens.ColorSelection).a < 1f, "選択は半透明");
        });
    }
}
