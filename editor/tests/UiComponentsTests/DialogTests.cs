using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using SEED.UI;
using SpriteRigTests; // テストランナー（TestHarness / Check）を共有する

namespace UiComponentsTests;

/// <summary>
/// ダイアログの大きさと動きの純粋な計算のテスト（W2 の手直し P2-1。docs/ui_navigation.md §3.2）:
/// 札の縦の割り付け（DialogMetrics。区画の枠 = 中身 ＋ 下の間隔・札の高さ）、文字の見積もり（DialogLayout。エンジンの折り返しの規則と
/// 組み込みの書体の送り幅。期待する行は作業フォルダの Python〈fontTools でエンジンの text_wrap.rs を真似た計算〉で求めた）、
/// 出入りの倍率（開き具合 × 予測型の戻るのプレビュー）、プレハブの既定の値が計算と矛盾しないこと。
/// </summary>
public static class DialogTests
{
    /// <summary>浮動小数の比較の許容量。</summary>
    private const double Eps = 1e-4;
    /// <summary>幅（画素）の比較の許容量（float の和の丸め）。</summary>
    private const double WidthEps = 1e-3;
    /// <summary>本文の大きさ（既定のテーマの text.body）と枠の幅（札 312 − 余白 24 × 2）。</summary>
    private const float Body = 16f, Box = 264f;
    /// <summary>かな 23 文字（大きさ 16 で 263.799 ≦ 264 ＝ 1 行にちょうど入る）。</summary>
    private const string Kana23 = "あいうえおかきくけこさしすせそたちつてとなにぬ";
    /// <summary>かな 46 文字。</summary>
    private const string Kana46 = Kana23 + "ねのはひふへほまみむめもやゆよらりるれろわをん";

    public static void Register(TestHarness h, UiThemeData theme)
    {
        // ── 札の縦の割り付け ─────────────────────────────────────
        h.Add("P2-1 ダイアログ: 札の高さ（題＋本文 1 行＋ボタン・題なし・本文なし・本文 3 行・ボタンだけ）と区画の下の間隔", () =>
        {
            float title = 20f * DialogLayout.LineHeightEm, line = Body * DialogLayout.LineHeightEm;
            // 題 28 ＋ 16 ＋ 本文 22.4 ＋ 24 ＋ ボタン 40、余白 24 × 2
            var full = DialogMetrics.Arrange(24f, DialogMetrics.Sections(title, line, 40f, 16f, 24f));
            Check.Close(178.4, full.CardHeight, Eps, "題＋本文 1 行＋ボタン = 24 + 28 + 16 + 22.4 + 24 + 40 + 24");
            Check.Close(44, full.SlotHeights[DialogMetrics.TitleSection], Eps, "題の枠 = 題の行 28 ＋ 下の間隔 16");
            Check.Close(46.4, full.SlotHeights[DialogMetrics.MessageSection], Eps, "本文の枠 = 22.4 ＋ 下の間隔 24");
            Check.Close(40, full.SlotHeights[DialogMetrics.ButtonsSection], Eps, "ボタンの行は最後（下の間隔なし）");
            Check.Close(16, full.GapsBelow[DialogMetrics.TitleSection], Eps, "題の下の間隔 = 本文の上の間隔");
            Check.Close(24, full.GapsBelow[DialogMetrics.MessageSection], Eps, "本文の下の間隔 = ボタンの行の上の間隔");
            Check.Close(0, full.GapsBelow[DialogMetrics.ButtonsSection], Eps, "最後の区画の下は 0");

            var noTitle = DialogMetrics.Arrange(24f, DialogMetrics.Sections(0f, line, 40f, 16f, 24f));
            Check.Close(24 + 22.4 + 24 + 40 + 24, noTitle.CardHeight, Eps, "題なし: 題とその間隔 16 は数えない");
            Check.Close(0, noTitle.SlotHeights[DialogMetrics.TitleSection], Eps, "出さない区画の枠は 0");
            Check.Close(0, noTitle.GapsBelow[DialogMetrics.TitleSection], Eps, "出さない区画の下の間隔は 0");

            var noMessage = DialogMetrics.Arrange(24f, DialogMetrics.Sections(title, 0f, 40f, 16f, 24f));
            Check.Close(24 + 28 + 24 + 40 + 24, noMessage.CardHeight, Eps, "本文なし: 題 → ボタンは本文 → ボタンと同じ 24");
            Check.Close(28 + 24, noMessage.SlotHeights[DialogMetrics.TitleSection], Eps, "題の枠 = 題 ＋ ボタンの行の上の間隔");
            Check.Close(0, noMessage.SlotHeights[DialogMetrics.MessageSection], Eps, "本文の枠は 0");

            var threeLines = DialogMetrics.Arrange(24f, DialogMetrics.Sections(title, 3 * line, 40f, 16f, 24f));
            Check.Close(24 + 28 + 16 + 67.2 + 24 + 40 + 24, threeLines.CardHeight, Eps, "本文 3 行 = 67.2");
            Check.Close(67.2 + 24, threeLines.SlotHeights[DialogMetrics.MessageSection], Eps, "本文の枠 = 3 行 ＋ 24");

            var buttonsOnly = DialogMetrics.Arrange(24f, DialogMetrics.Sections(0f, 0f, 40f, 16f, 24f));
            Check.Close(24 + 40 + 24, buttonsOnly.CardHeight, Eps, "ボタンだけ: 余白 ＋ ボタン ＋ 余白");
            Check.Close(40, buttonsOnly.SlotHeights[DialogMetrics.ButtonsSection], Eps, "ボタンの行の枠");
        });

        h.Add("P2-1 ダイアログ: トークンの値を変えても正しい（間隔 0・余白 40・ボタンの高さ 48・壊れた値）", () =>
        {
            float title = 28f, line = 22.4f;
            var noGaps = DialogMetrics.Arrange(40f, DialogMetrics.Sections(title, line, 40f, 0f, 0f));
            Check.Close(40 + 28 + 22.4 + 40 + 40, noGaps.CardHeight, Eps, "間隔 0・余白 40");
            Check.Close(28, noGaps.SlotHeights[DialogMetrics.TitleSection], Eps, "間隔 0 なら枠 = 中身");
            var tall = DialogMetrics.Arrange(24f, DialogMetrics.Sections(title, line, 48f, 16f, 24f));
            Check.Close(186.4, tall.CardHeight, Eps, "ボタンの高さ 48");
            Check.Close(48, tall.SlotHeights[DialogMetrics.ButtonsSection], Eps, "ボタンの行の枠 = ボタンの高さ");
            var wide = DialogMetrics.Arrange(24f, DialogMetrics.Sections(title, line, 40f, 20f, 32f));
            Check.Close(24 + 28 + 20 + 22.4 + 32 + 40 + 24, wide.CardHeight, Eps, "間隔 20・32");
            // 壊れた値: 余白 NaN・負の間隔は 0、NaN の高さの区画は出さない
            var broken = DialogMetrics.Arrange(float.NaN, DialogMetrics.Sections(float.NaN, line, 40f, 16f, -5f));
            Check.Close(22.4 + 40, broken.CardHeight, Eps, "余白 NaN は 0・負の間隔は 0・NaN の題は出さない");
            Check.Close(22.4, broken.SlotHeights[DialogMetrics.MessageSection], Eps, "負の間隔は足さない");
            Check.Close(264, DialogMetrics.InnerWidth(312f, 24f), Eps, "中の幅 = 312 − 24 × 2");
            Check.Close(0, DialogMetrics.InnerWidth(40f, 24f), Eps, "余白が札より広くても 0 未満にしない");
            Check.Close(312, DialogMetrics.InnerWidth(312f, float.NaN), Eps, "余白 NaN は 0");
        });

        h.Add("P2-1 ダイアログ: 間隔のトークン（題 → 本文 16・本文 → ボタン 24 = Flutter の AlertDialog の Material 3）が既定のテーマにある", () =>
        {
            Check.Close(16, theme.Number(NavTokens.SizeDialogTitleGap), Eps, "size.dialog_title_gap");
            Check.Close(24, theme.Number(NavTokens.SizeDialogActionsGap), Eps, "size.dialog_actions_gap");
            Check.Close(24, theme.Number(NavTokens.SizeDialogPadding), Eps, "size.dialog_padding はそのまま");
            Check.True(NavTokens.All.Contains(NavTokens.SizeDialogTitleGap) && NavTokens.All.Contains(NavTokens.SizeDialogActionsGap), "NavTokens.All にある");
        });

        // ── 文字の見積もり（エンジンの折り返しの規則・組み込みの書体の送り幅）────────
        h.Add("P2-1 ダイアログ: 本文の見積もり（1 行の本文・全角の長文の折り返しの位置・改行・空文字・大きさと枠の端の値）", () =>
        {
            Check.Equal(1, DialogLayout.EstimateLines("寝坊で失う最大金額が 3,000 円になります。", Body, Box), "roadmap §3.9.2 (a) の本文は 1 行（幅 232.8。以前の見積もりは 2 行）");
            Check.Equal(string.Join("|", Kana23, Kana46.Substring(Kana23.Length)), string.Join("|", DialogLayout.EstimateLineTexts(Kana46, Body, Box)),
                "かな 46 文字は 23 文字ずつ（1 文字 = 1000 / 1395 × 16 = 11.47。23 文字 263.80 ≦ 264）");
            Check.Equal("あいうえおかきくけこ|さしすせそたちつてと|なにぬねのはひふへほ|まみむめもやゆよらり|るれろわをん",
                string.Join("|", DialogLayout.EstimateLineTexts(Kana46, Body, 120f)), "枠 120 は 10 文字ずつ（114.7 ≦ 120 < 126.2）");
            Check.Equal(3, DialogLayout.EstimateLines("一行目\n二行目\n三行目", Body, Box), "改行");
            Check.Equal(2, DialogLayout.EstimateLines("改行の前の文。\r\n改行の後の文。", Body, Box), "\\r\\n は 1 つの改行");
            Check.Equal(2, DialogLayout.EstimateLines("\n", Body, Box), "改行だけは空の 2 行（エンジンと同じ）");
            Check.Equal(2, DialogLayout.EstimateLines("末尾の改行\n", Body, Box), "末尾の改行の後も 1 行");
            Check.Equal(0, DialogLayout.EstimateLines("", Body, Box), "空文字は 0 行（描かれない）");
            Check.Close(0, DialogLayout.EstimateHeight("", Body, Box), Eps, "空文字の高さは 0");
            Check.Close(2 * Body * DialogLayout.LineHeightEm, DialogLayout.EstimateHeight(Kana46, Body, Box), Eps, "高さ = 行 × 大きさ × 行の高さ");
            Check.Equal(1, DialogLayout.EstimateLines("abc", 0f, Box), "大きさ 0 は折り返さない（落ちない）");
            Check.Equal(1, DialogLayout.EstimateLines(Kana46, Body, DialogLayout.NoWrapWidth), "枠 0 は折り返さない（題）");
            Check.Equal(2, DialogLayout.EstimateLines(Kana46 + "\n" + Kana46, Body, DialogLayout.NoWrapWidth), "折り返さなくても改行では分ける");
            Check.Equal(1, DialogLayout.EstimateLines(Kana46, Body, float.MaxValue), "吹き出しの枠（float.MaxValue）は折り返さない");
            Check.Equal(1, DialogLayout.EstimateLines(Kana46, Body, float.NaN), "NaN の枠は折り返さない");
        });

        h.Add("P2-1 ダイアログ: 禁則（行末の句点のぶら下げ・ぶら下げは 2 文字まで・行末の開き括弧の追い出し）", () =>
        {
            Check.Equal(Kana23 + "。", string.Join("|", DialogLayout.EstimateLineTexts(Kana23 + "。", Body, Box)),
                "次の行の頭に来る句点は前の行へぶら下げる（1 行。以前の見積もりは 2 行）");
            Check.Equal(Kana23 + "。。|。ね", string.Join("|", DialogLayout.EstimateLineTexts(Kana23 + "。。。ね", Body, Box)), "ぶら下げは 2 文字まで");
            Check.Equal("あいうえおかきくけこさしすせそたちつてとなに|「ぬね」",
                string.Join("|", DialogLayout.EstimateLineTexts("あいうえおかきくけこさしすせそたちつてとなに「ぬね」", Body, Box)),
                "行末の開き括弧は次の行へ追い出す");
            Check.Equal(Kana23 + "|ね。", string.Join("|", DialogLayout.EstimateLineTexts(Kana23 + "ね。", Body, Box)),
                "句点の前の文字が入らなければ、句点はその文字と次の行へ");
        });

        h.Add("P2-1 ダイアログ: 英文は語の単位（空白は行末でぶら下げる）・入りきらない語は 1 文字ずつ・数字まじり・半角の幅", () =>
        {
            Check.Equal("The quick brown fox jumps over the lazy dog.|Pack my box with five dozen liquor jugs.",
                string.Join("|", DialogLayout.EstimateLineTexts("The quick brown fox jumps over the lazy dog. Pack my box with five dozen liquor jugs.", Body, Box)),
                "語の途中で折らない（行末の空白は数えない）");
            Check.Equal("Supercalifragilisticex|pialidocious-and-mor|e-words",
                string.Join("|", DialogLayout.EstimateLineTexts("Supercalifragilisticexpialidocious-and-more-words", Body, 120f)),
                "枠より長い語（- を含む 1 語）は 1 文字ずつに分けて詰める");
            Check.Equal("最大 3,000 円・毎日 07:30 に起きる（平日のみ）|「本気モード」で挑戦中",
                string.Join("|", DialogLayout.EstimateLineTexts("最大 3,000 円・毎日 07:30 に起きる（平日のみ）「本気モード」で挑戦中", Body, Box)), "数字まじり");
            Check.Equal(new string('W', 26) + "|" + new string('W', 4), string.Join("|", DialogLayout.EstimateLineTexts(new string('W', 30), Body, Box)),
                "いちばん広い半角（W 864 / 1395 em）は 26 文字で 257.7");
            Check.Equal(1, DialogLayout.EstimateLines(new string('i', 71), Body, Box), "狭い半角（i 310 / 1395 em）71 文字は 252.4 で 1 行");
            // 幅: ボタンの文字（大きさ 14。OK = O 714 + K 586、かな 3 文字 = 3000 / 1395 em）
            Check.Close(1300.0 / 1395 * 14, DialogLayout.EstimateWidth("OK", 14f), WidthEps, "OK の幅 13.05");
            Check.Close(3000.0 / 1395 * 14, DialogLayout.EstimateWidth("やめる", 14f), WidthEps, "やめるの幅 30.11");
            Check.Close(620.0 / 1395 * 16 * 4 + 278.0 / 1395 * 16, DialogLayout.EstimateWidth("3,000", 16f), WidthEps, "数字 620・読点 278");
            Check.Close(3000.0 / 1395 * 16, DialogLayout.EstimateWidth("一行目\n二行", 16f), WidthEps, "改行を含むなら最も広い行");
            Check.Close(0, DialogLayout.EstimateWidth("", 16f), Eps, "空文字は 0");
            Check.Close(0, DialogLayout.EstimateWidth("OK", 0f), Eps, "大きさ 0 は 0");
            Check.Close(500.0 / 1395 * 16 * 2, DialogLayout.EstimateWidth("ｱｲ", 16f), WidthEps, "半角カナは 500");
            Check.Close(364.0 / 1395 * 16, DialogLayout.EstimateWidth("\t", 16f), WidthEps, "タブ（グリフなし）は .notdef の 364");
        });

        h.Add("P2-1 ダイアログ: 枠にぎりぎりの行は折れる側（浮動小数の丸めで行が足りなくならない）", () =>
        {
            // かな 23 文字の幅（見積もりと同じ float の足し方）
            float exact = 0f;
            for (int i = 0; i < Kana23.Length; i++) exact += 1000f / 1395f * Body;
            Check.Equal(2, DialogLayout.EstimateLines(Kana23, Body, exact), "枠の幅ちょうどの行は折れる側に倒す（1 行余ることがある）");
            Check.Equal(1, DialogLayout.EstimateLines(Kana23, Body, exact + 0.02f), "余裕（大きさ × 0.001 = 0.016）より広ければ 1 行");
        });

        // ── 出入りの倍率 ─────────────────────────────────────────
        h.Add("P2-1 ダイアログ: 出入りの倍率（開き具合 0.9 → 1）× 予測型の戻るのプレビュー・確定の後は縮めた姿勢のまま出る", () =>
        {
            Check.Close(0.9, DialogMetrics.OpenScale(0f, 0.9f), Eps, "閉じた = ratio.dialog_scale_from");
            Check.Close(1, DialogMetrics.OpenScale(1f, 0.9f), Eps, "開いた = 1");
            Check.Close(0.95, DialogMetrics.OpenScale(0.5f, 0.9f), Eps, "半分");
            Check.Close(1, DialogMetrics.OpenScale(2f, 0.9f), Eps, "範囲の外は収める");
            Check.Close(1, DialogMetrics.OpenScale(float.NaN, 0.9f), Eps, "NaN の開き具合は開いた");
            Check.Close(1, DialogMetrics.OpenScale(0f, float.NaN), Eps, "NaN の最初の大きさは縮めない");
            Check.Close(1, DialogMetrics.CardScale(1f, 1f), Eps, "開いていてプレビューなし = 1（倍率を書かない既定）");
            Check.Close(0.9, DialogMetrics.CardScale(1f, 0.9f), Eps, "開いている間はプレビューの倍率そのもの");
            Check.Close(0.9 * 0.95, DialogMetrics.CardScale(0.95f, 0.9f), Eps, "積");
            Check.Close(0.95, DialogMetrics.CardScale(0.95f, float.NaN), Eps, "有限でない方は 1");
            // 確定: プレビューの倍率 0.93 を保ったまま、開き具合が 1 → 0 へ（曲線を通した p）。跳ばず、単調に縮み、
            // 以前の書き方（CanvasTransform.Scale = 開き具合の倍率・VisualScale = プレビュー）の合計と同じ
            const float preview = 0.93f;
            float previous = DialogMetrics.CardScale(DialogMetrics.OpenScale(1f, 0.9f), preview);
            Check.Close(preview, previous, Eps, "出始めは確定したときの姿勢（跳ばない）");
            for (int i = 1; i <= 10; i++)
            {
                float p = 1f - i / 10f;
                float s = DialogMetrics.CardScale(DialogMetrics.OpenScale(p, 0.9f), preview);
                Check.True(s <= previous + 1e-6f, $"出る間は単調に縮む（{i}）");
                Check.Close((0.9 + 0.1 * p) * preview, s, Eps, $"開き具合の倍率 × プレビュー（{i}）");
                previous = s;
            }
            Check.Close(0.9 * preview, previous, Eps, "出終わり = 0.9 × 0.93");
        });

        // ── プレハブの既定の値 ───────────────────────────────────
        h.Add("P2-1 ダイアログ: プレハブ（templates/ui/prefabs/dialog.actor）の既定の高さ・間隔・行間が計算と矛盾しない", () =>
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "prefabs", "dialog.actor")));
            var card = Child(doc.RootElement, "Card");
            var title = Child(card, "Title");
            // 2026-10-02: 本文は Body（区画の枠）→ Viewport（切り抜き・スクロールの窓）→ Message（文字）
            var body = Child(card, "Body");
            var viewport = Child(body, "Viewport");
            var message = Child(viewport, "Message");
            var buttons = Child(card, "Buttons");
            float titleSize = Data(title, "TextComponent").GetProperty("font_size").GetSingle();
            float bodySize = Data(message, "TextComponent").GetProperty("font_size").GetSingle();
            Check.Close(theme.Number(UiTokens.TextTitle), titleSize, Eps, "題の大きさ = text.title");
            Check.Close(theme.Number(UiTokens.TextBody), bodySize, Eps, "本文の大きさ = text.body");
            string titleText = Data(title, "TextComponent").GetProperty("content").GetString() ?? "";
            string bodyText = Data(message, "TextComponent").GetProperty("content").GetString() ?? "";
            float inner = DialogMetrics.InnerWidth(theme.Number(NavTokens.SizeDialogWidth), theme.Number(NavTokens.SizeDialogPadding));
            float titleHeight = DialogLayout.EstimateHeight(titleText, titleSize, DialogLayout.NoWrapWidth);
            float bodyHeight = DialogLayout.EstimateHeight(bodyText, bodySize, inner);
            float buttonHeight = theme.Number(NavTokens.SizeDialogButtonHeight);
            var layout = DialogMetrics.Arrange(theme.Number(NavTokens.SizeDialogPadding), DialogMetrics.Sections(
                titleHeight, bodyHeight, buttonHeight, theme.Number(NavTokens.SizeDialogTitleGap), theme.Number(NavTokens.SizeDialogActionsGap)));

            Check.Close(layout.CardHeight, Data(card, "CanvasComponent").GetProperty("height").GetSingle(), Eps, "札のキャンバスの高さ");
            Check.Close(layout.CardHeight, Data(card, "SpriteComponent").GetProperty("height").GetSingle(), Eps, "札の背景の高さ");
            Check.Close(layout.CardHeight, Data(card, "CanvasLayoutItemComponent").GetProperty("preferred_height").GetSingle(), Eps, "札の大きさの指定");
            Check.Close(theme.Number(NavTokens.SizeDialogWidth), Data(card, "CanvasLayoutItemComponent").GetProperty("preferred_width").GetSingle(), Eps, "札の幅");
            var stack = Data(card, "CanvasStackComponent");
            Check.Close(0, stack.GetProperty("spacing").GetSingle(), Eps, "札の Stack の等間隔は 0（間隔は区画の枠が持つ）");
            Check.Close(theme.Number(NavTokens.SizeDialogPadding), stack.GetProperty("padding").GetProperty("top").GetSingle(), Eps, "余白");
            Check.Close(layout.SlotHeights[DialogMetrics.TitleSection], Data(title, "CanvasLayoutItemComponent").GetProperty("preferred_height").GetSingle(), Eps, "題の枠");
            Check.Close(layout.SlotHeights[DialogMetrics.MessageSection], Data(body, "CanvasLayoutItemComponent").GetProperty("preferred_height").GetSingle(), Eps, "本文の区画の枠（中身 ＋ 下の間隔）");
            Check.Close(bodyHeight, Data(viewport, "CanvasLayoutItemComponent").GetProperty("preferred_height").GetSingle(), Eps, "本文の窓の高さ（中身だけ。下の間隔は窓に入れない）");
            Check.Close(layout.SlotHeights[DialogMetrics.ButtonsSection], Data(buttons, "CanvasLayoutItemComponent").GetProperty("preferred_height").GetSingle(), Eps, "ボタンの行の枠");
            Check.Close(titleHeight, Data(title, "TextComponent").GetProperty("box_height").GetSingle(), Eps, "題の文字の枠の高さ");
            Check.Close(bodyHeight, Data(message, "TextComponent").GetProperty("box_height").GetSingle(), Eps, "本文の文字の枠の高さ");
            Check.Close(DialogLayout.LineHeightEm, Data(title, "TextComponent").GetProperty("line_spacing").GetSingle(), Eps, "題の行間 = 見積もりの行の高さ");
            Check.Close(DialogLayout.LineHeightEm, Data(message, "TextComponent").GetProperty("line_spacing").GetSingle(), Eps, "本文の行間 = 見積もりの行の高さ");
        });
    }

    /// <summary>プレハブの JSON の子（名前で引く）。</summary>
    private static JsonElement Child(JsonElement node, string name)
        => node.GetProperty("children").EnumerateArray().First(c => c.GetProperty("name").GetString() == name);

    /// <summary>プレハブの JSON のコンポーネントの data（型の名前で引く）。</summary>
    private static JsonElement Data(JsonElement node, string type)
        => node.GetProperty("components").EnumerateArray()
            .Select(c => c.GetProperty("component"))
            .First(c => c.GetProperty("type").GetString() == type)
            .GetProperty("data");
}
