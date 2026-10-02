using System;
using System.Linq;
using System.Text.Json;
using SEED;
using SEED.UI;
using SpriteRigTests; // テストランナー（TestHarness / Check）を共有する
using static UiComponentsTests.WidgetExtensionTests;

namespace UiComponentsTests;

/// <summary>
/// 2026-10-02 の部品の拡充のうち、ダイアログとトーストの純粋な計算のテスト（docs/ui_navigation.md §3.2・§3.5）:
/// 危険のボタンと選択肢の一覧と進捗の札の決め方（DialogModel）・ボタンの横並びと縦積み（DialogActionsLayout。Flutter の OverflowBar）・
/// 区画と札の端の余白・高さの上限に合わせて縮める（DialogMetrics.Sections・Arrange・Fit・MaxCardHeight）・選択肢の行の見た目と置き場
/// （DialogItemLooks）・トーストのアイコン（ToastQueue）・プレハブ（dialog.actor の新しい区画・dialog_item.actor）の作り。
/// </summary>
public static class DialogExtensionTests
{
    /// <summary>浮動小数の比較の許容量。</summary>
    private const double Eps = 1e-4;
    /// <summary>既定のテーマの値（札の余白・区画の間隔・選択肢の行）。</summary>
    private const float Padding = 24f, TitleGap = 16f, ActionsGap = 24f, ItemsInset = 12f, RowHeight = 48f, ButtonHeight = 40f;
    /// <summary>題 1 行（20 × 1.4）・本文 1 行（16 × 1.4）の高さ。</summary>
    private const float TitleLine = 28f, BodyLine = 22.4f;

    public static void Register(TestHarness h, UiThemeData theme)
    {
        var spacing = new DialogSpacing(TitleGap, ActionsGap, ItemsInset);

        // ── 決め方 ───────────────────────────────────────────────
        h.Add("拡充 ダイアログ: ボタンの出し方（ふつうは OK・選択肢の一覧と進捗の札とボタンを隠すは出さない・文字を指定すれば出す）", () =>
        {
            Check.Equal("Positive", string.Join(",", DialogModel.Buttons(new DialogOptions())), "ふつうは OK を 1 つ（従来どおり）");
            var menu = DialogOptions.Menu("題", new DialogMenuItem("編集"), new DialogMenuItem("削除"));
            Check.Equal(0, DialogModel.Buttons(menu).Count, "選択肢の一覧はボタンなし");
            var menuWithCancel = new DialogOptions { Items = menu.Items, NegativeText = "やめる" };
            Check.Equal("Negative", string.Join(",", DialogModel.Buttons(menuWithCancel)), "選択肢の一覧でも文字を指定したボタンは出す");
            var progress = DialogOptions.ProgressCard("処理しています");
            Check.Equal(0, DialogModel.Buttons(progress).Count, "進捗の札はボタンなし");
            Check.True(progress.Progress && !progress.DismissOnScrimTap && !progress.CancelableByBack, "進捗の札は幕のタップ・戻るで閉じない");
            Check.Equal(0, DialogModel.Buttons(new DialogOptions { PositiveText = "OK", HideButtons = true }).Count, "ボタンを隠すは文字があっても出さない");
            Check.True(menu.DismissOnScrimTap && menu.CancelableByBack, "選択肢の一覧は幕のタップ・戻るで閉じる（Dismissed）");
        });

        h.Add("拡充 ダイアログ: 危険のボタンの種類 → 色の役割・選べる項目・アイコン欄・結果の留め金（Selected の番号）", () =>
        {
            var options = new DialogOptions { PositiveText = "削除", PositiveKind = DialogButtonKind.Danger, NegativeText = "やめる" };
            Check.Equal(DialogButtonKind.Danger, DialogModel.ButtonKind(options, DialogResult.Positive), "Positive は危険");
            Check.Equal(DialogButtonKind.Default, DialogModel.ButtonKind(options, DialogResult.Negative), "Negative はふつう");
            Check.Equal(ButtonTone.Danger, DialogModel.ToneOf(DialogButtonKind.Danger), "危険 → Danger");
            Check.Equal(ButtonTone.Primary, DialogModel.ToneOf(DialogButtonKind.Default), "ふつう → Primary");
            var menu = DialogOptions.Menu("", new DialogMenuItem("a"), new DialogMenuItem("b") { Enabled = false }, new DialogMenuItem("c", UiIcon.Circle()));
            Check.True(DialogModel.CanSelect(menu, 0) && !DialogModel.CanSelect(menu, 1) && DialogModel.CanSelect(menu, 2), "選べない項目は選べない");
            Check.True(!DialogModel.CanSelect(menu, -1) && !DialogModel.CanSelect(menu, 3), "範囲の外は選べない");
            Check.True(DialogModel.AnyItemIcon(menu), "どれかにアイコン → アイコン欄");
            Check.True(!DialogModel.AnyItemIcon(DialogOptions.Menu("", new DialogMenuItem("a"))), "アイコンが無ければ欄なし");
            var latch = new DialogResultLatch();
            Check.True(latch.TryComplete(DialogResult.Selected, 2) && latch.SelectedIndex == 2, "Selected は番号を残す");
            Check.True(!latch.TryComplete(DialogResult.Dismissed), "2 度目は受けない");
            var dismissed = new DialogResultLatch();
            dismissed.TryComplete(DialogResult.Dismissed, 5);
            Check.Equal(DialogModel.NoSelection, dismissed.SelectedIndex, "Selected 以外は番号を残さない");
        });

        // ── 外から閉じる（2026-10-03。レビュー #9）──────────────────
        h.Add("外から閉じる: 結果の直し方（そのまま・番号の無い Selected と null と別の型と定義の無い値は Dismissed）", () =>
        {
            foreach (var r in new[] { DialogResult.Positive, DialogResult.Negative, DialogResult.Neutral, DialogResult.Dismissed })
                Check.Equal(r, DialogModel.ExternalCloseResult(r), $"{r} はそのまま");
            Check.Equal(DialogResult.Dismissed, DialogModel.ExternalCloseResult(DialogResult.Selected),
                "Selected は選んだ項目の番号を外から渡せないので Dismissed（SelectedIndex = −1 のまま Selected にしない）");
            Check.Equal(DialogResult.Dismissed, DialogModel.ExternalCloseResult(null), "null（ModalHandle.Close() の既定）は Dismissed");
            Check.Equal(DialogResult.Dismissed, DialogModel.ExternalCloseResult("ok"), "DialogResult 以外は Dismissed");
            Check.Equal(DialogResult.Dismissed, DialogModel.ExternalCloseResult((DialogResult)99), "定義の無い値は Dismissed");
            Check.Equal(DialogResult.Dismissed, DialogModel.ExternalCloseResult(ModalCloseOrder.ResultFor(ModalKind.Dialog)),
                "全部閉じる（ModalHost.CloseAll）のダイアログの結果も Dismissed のまま");
            // 留め金と組み合わせると、外から Selected で閉じても番号の無い Selected は残らない（Dialog.Choose の流れ）
            var latch = new DialogResultLatch();
            latch.TryComplete(DialogModel.ExternalCloseResult(DialogResult.Selected), DialogModel.NoSelection);
            Check.True(latch.Result == DialogResult.Dismissed && latch.SelectedIndex == DialogModel.NoSelection, "留め金も Dismissed・番号なし");
        });

        h.Add("外から閉じる: 入力欄の結果の文字（Positive だけ・読めなければ初めの文字・TrimResult・入力の無いダイアログは null）", () =>
        {
            var options = new DialogOptions { Title = "名前の変更", Input = new DialogInputOptions { Text = "  たろう ", TrimResult = true } };
            Check.Equal("じろう", DialogModel.InputResultText(options, DialogResult.Positive, " じろう  "), "入力欄の文字（前後の空白を落とす）");
            Check.Equal("たろう", DialogModel.InputResultText(options, DialogResult.Positive, null),
                "入力欄のスクリプトがまだ始まっていない（開いてすぐ外から Close(Positive)）なら初めの文字");
            Check.Equal("", DialogModel.InputResultText(options, DialogResult.Positive, ""), "空にした欄は空（初めの文字に戻さない）");
            foreach (var r in new[] { DialogResult.Negative, DialogResult.Neutral, DialogResult.Dismissed, DialogResult.Selected })
                Check.True(DialogModel.InputResultText(options, r, "じろう") is null, $"{r} は入れない（null）");
            var raw = new DialogOptions { Input = new DialogInputOptions { Text = " a ", TrimResult = false } };
            Check.Equal(" a ", DialogModel.InputResultText(raw, DialogResult.Positive, null), "TrimResult = false は空白を残す");
            Check.True(DialogModel.InputResultText(new DialogOptions(), DialogResult.Positive, "x") is null, "入力の無いダイアログは null");
        });

        // ── ボタンの行 ───────────────────────────────────────────
        h.Add("拡充 ダイアログ: ボタンが中の幅に入れば横に並べ、入らなければ縦に積む（Flutter の OverflowBar・間 0）", () =>
        {
            float inner = DialogMetrics.InnerWidth(312f, Padding);
            var row = DialogActionsLayout.Arrange(new[] { 72f, 72f, 72f }, 8f, inner, ButtonHeight, 0f);
            Check.True(!row.Stacked, "72 × 3 ＋ 8 × 2 = 232 ≦ 264 は横");
            Check.Close(ButtonHeight, row.RowHeight, Eps, "横の行の高さ = ボタンの高さ");
            var exact = DialogActionsLayout.Arrange(new[] { 124f, 132f }, 8f, inner, ButtonHeight, 0f);
            Check.True(!exact.Stacked, "ちょうど 264 は横");
            var stacked = DialogActionsLayout.Arrange(new[] { 110f, 110f, 124f }, 8f, inner, ButtonHeight, 0f);
            Check.True(stacked.Stacked, "110 ＋ 110 ＋ 124 ＋ 16 = 360 > 264 は縦");
            Check.Close(3 * ButtonHeight, stacked.RowHeight, Eps, "縦の行の高さ = 3 × 40（間 0）");
            var gapped = DialogActionsLayout.Arrange(new[] { 200f, 200f }, 8f, inner, ButtonHeight, 8f);
            Check.Close(2 * ButtonHeight + 8f, gapped.RowHeight, Eps, "間 8 なら 2 × 40 ＋ 8");
            var wide = DialogActionsLayout.Arrange(new[] { 400f }, 8f, inner, ButtonHeight, 0f);
            Check.True(wide.Stacked && Math.Abs(wide.Widths[0] - inner) < Eps, "中の幅より広い 1 つは縦（1 つ）で幅を中の幅で切る");
            var none = DialogActionsLayout.Arrange(Array.Empty<float>(), 8f, inner, ButtonHeight, 0f);
            Check.Close(0, none.RowHeight, Eps, "ボタンが無ければ行は 0（出さない）");
            var unknown = DialogActionsLayout.Arrange(new[] { 400f, 400f }, 8f, 0f, ButtonHeight, 0f);
            Check.True(!unknown.Stacked, "中の幅が分からない（0）なら横のまま（従来どおり）");
        });

        // ── 区画 ─────────────────────────────────────────────────
        h.Add("拡充 ダイアログ: 区画（題・進捗・本文・選択肢・入力欄・ボタン）の間隔と札の端の余白（選択肢が端なら 12）", () =>
        {
            // 従来の 4 区画の形（進捗・選択肢なし）は従来と同じ高さ
            var legacy = DialogMetrics.Arrange(Padding, DialogMetrics.Sections(TitleLine, BodyLine, ButtonHeight, TitleGap, ActionsGap));
            Check.Close(178.4, legacy.CardHeight, Eps, "題＋本文＋ボタン = 178.4（従来どおり）");
            Check.Close(Padding, legacy.PaddingTop, Eps, "上の余白 24");
            Check.Close(Padding, legacy.PaddingBottom, Eps, "下の余白 24");

            // 選択肢の一覧（題 ＋ 4 行・ボタンなし）: 題の下 12・一覧の下（札の下端）12
            var menu = DialogMetrics.Arrange(Padding, DialogMetrics.Sections(new DialogContentHeights(TitleLine, 0f, 0f, 4 * RowHeight, 0f, 0f), spacing));
            Check.Close(24 + 28 + 12 + 192 + 12, menu.CardHeight, Eps, "24 ＋ 28 ＋ 12 ＋ 4 × 48 ＋ 12");
            Check.Close(TitleLine + ItemsInset, menu.SlotHeights[DialogMetrics.TitleSection], Eps, "題の枠 = 題 ＋ 12");
            Check.Close(ItemsInset, menu.PaddingBottom, Eps, "一覧が最後なら下の余白 12（SimpleDialog の contentPadding）");
            // 題なしの一覧: 上下とも 12
            var bare = DialogMetrics.Arrange(Padding, DialogMetrics.Sections(new DialogContentHeights(0f, 0f, 0f, 3 * RowHeight, 0f, 0f), spacing));
            Check.Close(12 + 144 + 12, bare.CardHeight, Eps, "題なし: 12 ＋ 3 × 48 ＋ 12");
            Check.Close(ItemsInset, bare.PaddingTop, Eps, "一覧が最初なら上の余白 12");
            // 一覧 ＋ ボタン（やめる）: 一覧の下の間隔は 12（一覧の GapBelow）、下の余白はボタンの 24
            var cancel = DialogMetrics.Arrange(Padding, DialogMetrics.Sections(new DialogContentHeights(TitleLine, 0f, 0f, 2 * RowHeight, 0f, ButtonHeight), spacing));
            Check.Close(2 * RowHeight + ItemsInset, cancel.SlotHeights[DialogMetrics.ItemsSection], Eps, "一覧の枠 = 2 行 ＋ 12");
            Check.Close(Padding, cancel.PaddingBottom, Eps, "最後がボタンなら下の余白 24");
            // 進捗の札（題 ＋ 進捗の行 36・ボタンなし）: 題 → 進捗は 16
            var progress = DialogMetrics.Arrange(Padding, DialogMetrics.Sections(new DialogContentHeights(TitleLine, 36f, 0f, 0f, 0f, 0f), spacing));
            Check.Close(24 + 28 + 16 + 36 + 24, progress.CardHeight, Eps, "24 ＋ 28 ＋ 16 ＋ 36 ＋ 24");
            // 縦に積んだボタン（3 × 40）
            var stacked = DialogMetrics.Arrange(Padding, DialogMetrics.Sections(new DialogContentHeights(TitleLine, 0f, BodyLine, 0f, 0f, 3 * ButtonHeight), spacing));
            Check.Close(24 + 28 + 16 + 22.4 + 24 + 120 + 24, stacked.CardHeight, Eps, "縦積みのボタンの行は 120");
        });

        h.Add("拡充 ダイアログ: 高さの上限（画面 − 安全領域 − 余白 × 2）を超えたら選択肢 → 本文の順に 1 行まで縮める（スクロール）", () =>
        {
            Check.Close(1200 - 32 - 21 - 48, DialogMetrics.MaxCardHeight(1200f, 32f, 21f, 24f), Eps, "上限 = 1200 − 32 − 21 − 48");
            Check.True(float.IsPositiveInfinity(DialogMetrics.MaxCardHeight(float.NaN, 0f, 0f, 24f)), "領域が分からなければ上限なし");
            Check.Close(0, DialogMetrics.MaxCardHeight(40f, 0f, 0f, 24f), Eps, "負にしない");

            // 長い本文（40 行 = 896）: 上限 600 に収まるよう本文だけ縮める
            float body = 40 * BodyLine;
            var sections = DialogMetrics.Sections(new DialogContentHeights(TitleLine, 0f, body, 0f, 0f, ButtonHeight), spacing);
            var shrink = new[] { new DialogShrink(DialogMetrics.ItemsSection, RowHeight), new DialogShrink(DialogMetrics.MessageSection, BodyLine) };
            var fit = DialogMetrics.Fit(Padding, sections, 600f, shrink);
            var layout = DialogMetrics.Arrange(Padding, fit.Sections);
            Check.Close(600, layout.CardHeight, Eps, "札は上限ちょうど");
            Check.True(fit.Scrolls[DialogMetrics.MessageSection] && !fit.Scrolls[DialogMetrics.TitleSection], "本文だけスクロール");
            Check.Close(600 - (24 + 28 + 16 + 24 + 40 + 24), fit.Sections[DialogMetrics.MessageSection].Height, Eps, "本文の窓 = 上限 − ほかの区画と間隔と余白");
            // 収まるなら縮めない（既定の見た目は変わらない）
            var small = DialogMetrics.Fit(Padding, DialogMetrics.Sections(TitleLine, BodyLine, ButtonHeight, TitleGap, ActionsGap), 600f, shrink);
            Check.True(!small.Scrolls.Any(s => s), "収まれば縮めない");
            Check.Close(BodyLine, small.Sections[DialogMetrics.MessageSection].Height, Eps, "本文はそのまま");
            var unlimited = DialogMetrics.Fit(Padding, sections, float.PositiveInfinity, shrink);
            Check.Close(body, unlimited.Sections[DialogMetrics.MessageSection].Height, Eps, "上限なしは縮めない");

            // 選択肢 30 行と本文: 選択肢を先に 1 行まで縮め、それでも超えれば本文も縮める
            var both = DialogMetrics.Sections(new DialogContentHeights(TitleLine, 0f, 10 * BodyLine, 30 * RowHeight, 0f, 0f), spacing);
            var fitBoth = DialogMetrics.Fit(Padding, both, 300f, shrink);
            Check.Close(RowHeight, fitBoth.Sections[DialogMetrics.ItemsSection].Height, Eps, "選択肢は 1 行まで");
            Check.True(fitBoth.Scrolls[DialogMetrics.ItemsSection] && fitBoth.Scrolls[DialogMetrics.MessageSection], "どちらもスクロール");
            Check.Close(300, DialogMetrics.Arrange(Padding, fitBoth.Sections).CardHeight, Eps, "札は上限ちょうど");
            // 縮めきっても超える（題とボタンだけで高い）: 下限で止まる
            var tiny = DialogMetrics.Fit(Padding, sections, 100f, shrink);
            Check.Close(BodyLine, tiny.Sections[DialogMetrics.MessageSection].Height, Eps, "本文は 1 行で止まる");
        });

        // ── 選択肢の行 ───────────────────────────────────────────
        h.Add("拡充 ダイアログ: 選択肢の行の見た目（ふつう・危険・押下・選べない）と置き場（左右 24・アイコン欄 24 ＋ 12）", () =>
        {
            var normal = DialogItemLooks.Resolve(DialogButtonKind.Default, false, false, theme);
            Check.Close(0, normal.Background.a, Eps, "ふだんは透明");
            Check.Equal(theme.Color(UiTokens.ColorOnSurface), normal.Label, "文字は on_surface");
            Check.Equal(theme.Color(UiTokens.ColorOnSurfaceMuted), normal.IconColor, "アイコンは on_surface_muted");
            var danger = DialogItemLooks.Resolve(DialogButtonKind.Danger, false, false, theme);
            Check.Equal(theme.Color(UiTokens.ColorError), danger.Label, "危険の文字は error");
            Check.Equal(theme.Color(UiTokens.ColorError), danger.IconColor, "危険のアイコンは error");
            var pressed = DialogItemLooks.Resolve(DialogButtonKind.Default, true, false, theme);
            Check.Close(theme.Number(UiTokens.OpacityPressed), pressed.Background.a, Eps, "押下は重ね色");
            var disabled = DialogItemLooks.Resolve(DialogButtonKind.Danger, true, true, theme);
            Check.Equal(theme.Color(UiTokens.ColorOnDisabled), disabled.Label, "選べない文字は on_disabled（危険でも）");
            Check.Close(0, disabled.Background.a, Eps, "選べない行は押下を出さない");
            Check.Close(theme.Number(UiTokens.OpacityDisabled), disabled.IconFade, Eps, "選べない行のアイコンは薄い");

            var plain = DialogItemLooks.Place(312f, RowHeight, Padding, false, 24f, 12f);
            Check.Close(24, plain.LabelX, Eps, "アイコン欄なし: 文字は 24 から（題とそろう）");
            Check.Close(312 - 48, plain.LabelWidth, Eps, "文字の枠 = 312 − 24 × 2");
            var withIcon = DialogItemLooks.Place(312f, RowHeight, Padding, true, 24f, 12f);
            Check.Close(24, withIcon.IconX, Eps, "アイコンは 24 から");
            Check.Close(24 + 24 + 12, withIcon.LabelX, Eps, "文字は 24 ＋ 24 ＋ 12");
            Check.Close(312 - 60 - 24, withIcon.LabelWidth, Eps, "右の余白 24 まで");
            Check.Close(12, DialogItemLooks.CenterTop(RowHeight, 24f), Eps, "アイコンは行の真ん中（(48 − 24) ÷ 2）");
            Check.Close(RowHeight, withIcon.LabelHeight, Eps, "文字の枠の高さ = 行（縦の真ん中）");
        });

        // ── トースト ─────────────────────────────────────────────
        h.Add("拡充 トースト: 先頭のアイコンは待たされた後もそのまま渡る・無ければ null（従来どおり）", () =>
        {
            var queue = new ToastQueue { MaxVisible = 1 };
            var first = queue.Enqueue("文字だけ", 2f);
            var icon = UiIcon.Circle(new Color(1f, 0f, 0f, 1f));
            var second = queue.Enqueue("アイコンつき", 2f, icon);
            Check.True(first.Icon is null, "アイコンなしは null");
            Check.True(second.Phase == ToastPhase.Pending, "2 つ目は待つ");
            queue.Tick(3f);
            var shown = queue.Remove(first.Id);
            Check.True(shown.Count == 1 && ReferenceEquals(shown[0].Icon, icon), "待った後もアイコンが渡る");
        });

        // ── プレハブ ─────────────────────────────────────────────
        h.Add("拡充 プレハブ: dialog.actor の区画（題・進捗・本文の窓・選択肢の窓・入力欄・ボタン）と dialog_item.actor の作り", () =>
        {
            var card = Child(Load("prefabs", "dialog.actor"), "Card");
            Check.Equal("Title,Progress,Body,Items,Input,Buttons", string.Join(",", Children(card).Select(Name)), "区画の並び");
            foreach (var section in new[] { "Progress", "Items" })
            {
                var node = Child(card, section);
                Check.True(node.TryGetProperty("visible", out var v) && !v.GetBoolean(), $"{section} は既定で隠す（ふつうのダイアログは従来の見た目）");
            }
            foreach (var path in new[] { new[] { "Body", "Viewport" }, new[] { "Items", "Viewport" } })
            {
                var viewport = Child(Child(card, path[0]), path[1]);
                Check.True(!Data(viewport, "CanvasScrollComponent").GetProperty("enabled").GetBoolean(), $"{path[0]} の窓のスクロールは既定で止める（縮めたときだけ動かす）");
                Check.True(!Data(viewport, "CanvasClipComponent").GetProperty("enabled").GetBoolean(), $"{path[0]} の窓の切り抜きは既定で止める（行末の句読点のぶら下げを切らない）");
                Check.Equal("vertical", Data(viewport, "CanvasStackComponent").GetProperty("direction").GetString(), $"{path[0]} の窓は縦の Stack（中身の大きさを並べた長さにする）");
            }
            Check.Equal("stretch", Data(Child(Child(card, "Items"), "Viewport"), "CanvasStackComponent").GetProperty("cross_align").GetString(), "選択肢の行は札の幅いっぱい");
            var row = Child(Child(card, "Progress"), "Row");
            Check.Equal("Spinner,Label", string.Join(",", Children(row).Select(Name)), "進捗の行はスピナーと文字");
            Check.Equal("SEED.UI.ProgressSpinner", Data(Child(row, "Spinner"), "ScriptComponent").GetProperty("type_name").GetString(), "スピナー");
            Check.Equal("center", Data(row, "CanvasStackComponent").GetProperty("cross_align").GetString(), "進捗の行は縦の真ん中");

            var item = Load("prefabs", "dialog_item.actor");
            Check.Equal("SEED.UI.DialogItem", Data(item, "ScriptComponent").GetProperty("type_name").GetString(), "行のスクリプト");
            var gesture = Data(item, "CanvasGestureComponent");
            Check.True(gesture.GetProperty("tap").GetBoolean() && !gesture.TryGetProperty("press_feedback", out _), "タップと押下の見た目（press_feedback は既定 true）");
            Check.Close(theme.Number(NavTokens.SizeDialogItemHeight), Data(item, "CanvasLayoutItemComponent").GetProperty("preferred_height").GetSingle(), Eps, "行の高さ");
            Check.Equal("Icon,Label", string.Join(",", Children(item).Select(Name)), "子は Icon と Label");
            Check.True(Child(item, "Icon").TryGetProperty("visible", out var iconVisible) && !iconVisible.GetBoolean(), "Icon は既定で隠す");
        });
    }
}
