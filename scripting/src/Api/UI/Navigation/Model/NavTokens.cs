using System.Collections.Generic;

namespace SEED.UI;

// ============================================================
//  NavTokens.cs — 画面の組み立て（W2-7）が読むテーマのトークンの名前の一覧
//
//  UiTokens（W2-4・W2-5 の部品）と同じ考え方: 値は Theme/default_theme.json（SEEDScripting に埋め込み）にあり、
//  部品・画面のスタックはここの名前だけを使う（時間・曲線・幕の濃さ・大きさ・重なりのレイヤーを直接書かない）。
//  足すときは default_theme.json・docs/ui_navigation.md §7 の表にも足すこと。
//  曲線のトークン（*_curve）は 4 つの成分（.x1・.y1・.x2・.y2）を持つオブジェクト（UiCurve.FromTheme）。
// ============================================================

/// <summary>画面の組み立て（W2-7）のテーマのトークンの名前。</summary>
public static class NavTokens
{
    // ── 色 ──────────────────────────────────────────────────
    /// <summary>幕（ダイアログ・シート・覆いの後ろを暗くする）の色。濃さは opacity.scrim / opacity.dialog_scrim。</summary>
    public const string ColorScrim = "color.scrim";
    /// <summary>トーストの面（暗いテーマでは明るい面＝Material の inverse surface）。</summary>
    public const string ColorInverseSurface = "color.inverse_surface";
    /// <summary>トーストの文字。</summary>
    public const string ColorOnInverseSurface = "color.on_inverse_surface";
    /// <summary>
    /// 方向キー・パッドのフォーカスの枠（focus ring。2026-10-03。L3-6。docs/ui_navigation.md §7.2）。背景・面のどちらの上でも
    /// 見分けられる明るさ（暗い方は明るい紫、明るい方は濃い紫。主の色と同じ色相）。
    /// </summary>
    public const string ColorFocusRing = "color.focus_ring";

    // ── 角丸 ────────────────────────────────────────────────
    /// <summary>ダイアログの札。</summary>
    public const string RadiusDialog = "radius.dialog";
    /// <summary>シート・覆いの画面側の角。</summary>
    public const string RadiusSheet = "radius.sheet";
    /// <summary>トースト。</summary>
    public const string RadiusToast = "radius.toast";
    /// <summary>中央のポップアップの札（2026-10-02。既定はダイアログと同じ 28）。</summary>
    public const string RadiusPopup = "radius.popup";
    /// <summary>タブの選択の印（丸い帯）。</summary>
    public const string RadiusTabIndicator = "radius.tab_indicator";
    /// <summary>フォーカスの枠の角丸（部品が角丸でないとき。角丸の部品には部品の角丸 + 間 + 太さの同心の角丸を使う。2026-10-03）。</summary>
    public const string RadiusFocusRing = "radius.focus_ring";

    // ── 大きさ（キャンバスの単位＝dp）──────────────────────────
    /// <summary>下のタブの高さ（安全領域の分は別に足す）。</summary>
    public const string SizeTabBar = "size.tab_bar";
    /// <summary>フォーカスの枠の線の太さ（2026-10-03。Material 3 の focus indicator の 3 dp。記憶による）。</summary>
    public const string SizeFocusRingWidth = "size.focus_ring_width";
    /// <summary>フォーカスの枠の線の内側と部品の辺の間（2026-10-03。Material 3 の focus indicator の外側の 2 dp。記憶による）。</summary>
    public const string SizeFocusRingGap = "size.focus_ring_gap";
    /// <summary>タブの選択の印の幅。</summary>
    public const string SizeTabIndicatorWidth = "size.tab_indicator_width";
    /// <summary>タブの選択の印の高さ。</summary>
    public const string SizeTabIndicatorHeight = "size.tab_indicator_height";
    /// <summary>ダイアログの札の幅（Material 3 の 280〜560 の間）。</summary>
    public const string SizeDialogWidth = "size.dialog_width";
    /// <summary>ダイアログの札の内側の余白（上下左右。Material 3 の 24）。</summary>
    public const string SizeDialogPadding = "size.dialog_padding";
    /// <summary>
    /// ダイアログの題 → 本文の間隔（W2 の手直し P2-1）。既定 16 は Flutter master の AlertDialog（Material 3）の contentPadding の上 16
    /// （packages/flutter/lib/src/material/dialog.dart。2026-09-29 に取得して確かめた）。
    /// </summary>
    public const string SizeDialogTitleGap = "size.dialog_title_gap";
    /// <summary>
    /// ダイアログの本文 → ボタンの行の間隔（本文が無ければ題 → ボタンの行。W2 の手直し P2-1）。既定 24 は同じ AlertDialog の contentPadding の下 24
    /// （Material 3 の actionsPadding の上は 0）。本文の無いときの Flutter は題の下の余白 20 だが、SEED はこの値にそろえる。
    /// </summary>
    public const string SizeDialogActionsGap = "size.dialog_actions_gap";
    /// <summary>ダイアログのボタンの高さ。</summary>
    public const string SizeDialogButtonHeight = "size.dialog_button_height";
    /// <summary>
    /// ダイアログの札と画面の上下の端（安全領域の内側）の最小の間（2026-10-02）。札が「画面の高さ − 安全領域 − この値 × 2」より高くなるときは、
    /// 本文・選択肢の一覧の窓を縮めてスクロールにする（Material 3 の長い本文のダイアログ）。既定 24 は Flutter master の Dialog の
    /// insetPadding の縦 24（_defaultInsetPadding。2026-09-29 に取得した dialog.dart で確かめた）。
    /// </summary>
    public const string SizeDialogMargin = "size.dialog_margin";
    /// <summary>
    /// ボタンが札の中の幅に入らず縦に積むときのボタンの間（2026-10-02。Flutter の AlertDialog の OverflowBar の overflowSpacing の既定 0
    /// 〈actionsOverflowButtonSpacing ?? 0〉。同じ dialog.dart で確かめた）。
    /// </summary>
    public const string SizeDialogActionsOverflowGap = "size.dialog_actions_overflow_gap";
    /// <summary>
    /// 選択肢の一覧のダイアログ（Material の SimpleDialog 相当）の 1 行の高さ（2026-10-02。押せる大きさ 48。Flutter の SimpleDialogOption は
    /// 上下 8 の余白＋文字で約 36 だが、指の押しやすさを優先して size.touch_min と同じ 48 にした）。
    /// </summary>
    public const string SizeDialogItemHeight = "size.dialog_item_height";
    /// <summary>
    /// 選択肢の一覧の上下の空き（2026-10-02。題・本文と一覧の間と、一覧が札の上端・下端にあるときの札の余白。Flutter の SimpleDialog の
    /// contentPadding の上 12〈下は 16 だが、行の高さ 48 の中で文字が中央に来るので、12 で字面の下に約 24 が空く〉）。
    /// </summary>
    public const string SizeDialogItemsInset = "size.dialog_items_inset";
    /// <summary>
    /// 中央のポップアップの札と画面の端の余白（2026-10-02。左右と、高さの上限の上下。既定 16 は Wake or Pay のプロフィールのポップアップの値）。
    /// </summary>
    public const string SizePopupMargin = "size.popup_margin";
    /// <summary>中央のポップアップの札の幅の上限（2026-10-02。既定 560 = Material 3 のダイアログの最大の幅。広い画面で札が伸びすぎない）。</summary>
    public const string SizePopupMaxWidth = "size.popup_max_width";
    /// <summary>中央のポップアップの札の内側の余白（2026-10-02。上下左右。既定 8 は Wake or Pay のプロフィールのポップアップの値）。</summary>
    public const string SizePopupPadding = "size.popup_padding";
    /// <summary>つまみ（シート・覆いのグラブバー）の幅。</summary>
    public const string SizeHandleWidth = "size.handle_width";
    /// <summary>つまみの太さ。</summary>
    public const string SizeHandleHeight = "size.handle_height";
    /// <summary>つまみの行の高さ（つまみの上下の余白を含む）。</summary>
    public const string SizeHandleArea = "size.handle_area";
    /// <summary>トーストの高さ。</summary>
    public const string SizeToastHeight = "size.toast_height";
    /// <summary>ドラッグで閉じる距離（覆い・トースト。これ以上引いて離すと閉じる）。</summary>
    public const string SizeDragDismiss = "size.drag_dismiss";
    /// <summary>
    /// 予測型の戻るのプレビューで、画面を指の動く向き（左の端からの手ぶりなら右）へずらすいちばん大きな量（W2 の手直し 3b）。
    /// 既定 8 dp は Material 3 の予測型の戻るの動き（縮めた画面を端の近くへ寄せ、8 dp の余白を残す）から取った値で、記憶による。
    /// </summary>
    public const string SizeBackPreviewShift = "size.back_preview_shift";

    // ── 動き（秒）と曲線 ─────────────────────────────────────
    /// <summary>押し込み（右から入る・右へ出る）の時間。</summary>
    public const string MotionPush = "motion.push";
    /// <summary>押し込みの曲線。</summary>
    public const string MotionPushCurve = "motion.push_curve";
    /// <summary>覆う画面（上から降りる・上へ戻る）の時間。</summary>
    public const string MotionCover = "motion.cover";
    /// <summary>覆う画面の曲線。</summary>
    public const string MotionCoverCurve = "motion.cover_curve";
    /// <summary>フェード（幕を通して入れ替える）の時間。</summary>
    public const string MotionFade = "motion.fade";
    /// <summary>フェードの曲線。</summary>
    public const string MotionFadeCurve = "motion.fade_curve";
    /// <summary>上からの覆い（プロフィール・オプション）の時間（Flutter 版の top_sheet の 220ms）。</summary>
    public const string MotionOverlay = "motion.overlay";
    /// <summary>上からの覆いの曲線（Flutter 版の easeOut）。</summary>
    public const string MotionOverlayCurve = "motion.overlay_curve";
    /// <summary>ダイアログの出入りの時間。</summary>
    public const string MotionDialog = "motion.dialog";
    /// <summary>ダイアログの曲線。</summary>
    public const string MotionDialogCurve = "motion.dialog_curve";
    /// <summary>下からのシートを開く・閉じる時間（曲線は CanvasScroll の ScrollTo の easeInOut）。</summary>
    public const string MotionSheet = "motion.sheet";
    /// <summary>トーストの出入りの時間。</summary>
    public const string MotionToast = "motion.toast";
    /// <summary>トーストの曲線。</summary>
    public const string MotionToastCurve = "motion.toast_curve";
    /// <summary>トーストを出しておく時間（短い）。</summary>
    public const string MotionToastShort = "motion.toast_short";
    /// <summary>トーストを出しておく時間（長い）。</summary>
    public const string MotionToastLong = "motion.toast_long";
    /// <summary>
    /// 予測型の戻るのプレビューの、手ぶりの進み具合 → 縮み具合の曲線（W2 の手直し 3b）。既定 (0, 0, 0, 1) は Android の開発者向け文書の
    /// 「独自の予測型の戻るの動き」の例の GestureInterpolator（PathInterpolator(0, 0, 0, 1)）で、記憶による。
    /// 取り消したときに元へ戻る時間は motion.short。
    /// </summary>
    public const string MotionBackPreviewCurve = "motion.back_preview_curve";

    // ── 濃さ ────────────────────────────────────────────────
    /// <summary>覆い・シートの幕の濃さ（Flutter 版の黒 54%）。</summary>
    public const string OpacityScrim = "opacity.scrim";
    /// <summary>ダイアログの幕の濃さ（Material 3 の 32%）。</summary>
    public const string OpacityDialogScrim = "opacity.dialog_scrim";

    // ── 割合・速さ・数 ───────────────────────────────────────
    /// <summary>押し込みで下の画面が左へずれる割合（画面の幅に対する。視差）。</summary>
    public const string RatioPushParallax = "ratio.push_parallax";
    /// <summary>ダイアログが出るときの最初の大きさ（1 = そのまま）。</summary>
    public const string RatioDialogScaleFrom = "ratio.dialog_scale_from";
    /// <summary>下からのシートの最大の高さ（覆う領域の高さに対する割合）。</summary>
    public const string RatioSheetMaxHeight = "ratio.sheet_max_height";
    /// <summary>中央のポップアップの札の高さの上限（安全領域の高さに対する割合。2026-10-02。既定 0.8 は Wake or Pay のプロフィールのポップアップの値）。</summary>
    public const string RatioPopupMaxHeight = "ratio.popup_max_height";
    /// <summary>
    /// 予測型の戻るのプレビューのいちばん小さい倍率（手ぶりを最後まで引いたときの大きさ。W2 の手直し 3b）。
    /// 既定 0.9 は Material 3 の予測型の戻る（画面が 90% まで縮む）から取った値で、記憶による。
    /// </summary>
    public const string RatioBackPreviewScale = "ratio.back_preview_scale";
    /// <summary>フリックで閉じる速さ（dp/秒。覆い・トースト。Flutter 版の 300）。</summary>
    public const string SpeedFlingDismiss = "speed.fling_dismiss";
    /// <summary>同時に見せるトーストの数（それより多い分は待たせる）。</summary>
    public const string CountToastVisible = "count.toast_visible";

    // ── 重なりのレイヤーの底上げ（CanvasLayoutItem.LayerBias）──────
    /// <summary>画面のスタックの 1 段ぶん（段 i の画面は i × この値。画面の中の表示のレイヤーはこれより小さく保つ）。</summary>
    public const string LayerStackStep = "layer.stack_step";
    /// <summary>覆い・シート・ダイアログの帯の中の 1 つぶん。</summary>
    public const string LayerModalStep = "layer.modal_step";
    /// <summary>上からの覆いの帯。</summary>
    public const string LayerOverlay = "layer.overlay";
    /// <summary>下からのシートの帯。</summary>
    public const string LayerSheet = "layer.sheet";
    /// <summary>ダイアログの帯。</summary>
    public const string LayerDialog = "layer.dialog";
    /// <summary>トーストの帯。</summary>
    public const string LayerToast = "layer.toast";
    /// <summary>フォーカスの枠（いちばん手前。トーストより上。2026-10-03）。</summary>
    public const string LayerFocusRing = "layer.focus_ring";

    /// <summary>数のトークン（既定のテーマが持つかの検査用）。</summary>
    private static readonly string[] Numbers =
    {
        RadiusDialog, RadiusSheet, RadiusToast, RadiusPopup, RadiusTabIndicator, RadiusFocusRing,
        SizeFocusRingWidth, SizeFocusRingGap,
        SizeTabBar, SizeTabIndicatorWidth, SizeTabIndicatorHeight, SizeDialogWidth, SizeDialogPadding, SizeDialogTitleGap, SizeDialogActionsGap,
        SizeDialogButtonHeight, SizeDialogMargin, SizeDialogActionsOverflowGap, SizeDialogItemHeight, SizeDialogItemsInset,
        SizePopupMargin, SizePopupMaxWidth, SizePopupPadding,
        SizeHandleWidth, SizeHandleHeight, SizeHandleArea, SizeToastHeight, SizeDragDismiss, SizeBackPreviewShift,
        MotionPush, MotionCover, MotionFade, MotionOverlay, MotionDialog, MotionSheet, MotionToast, MotionToastShort, MotionToastLong,
        OpacityScrim, OpacityDialogScrim,
        RatioPushParallax, RatioDialogScaleFrom, RatioSheetMaxHeight, RatioPopupMaxHeight, RatioBackPreviewScale, SpeedFlingDismiss, CountToastVisible,
        LayerStackStep, LayerModalStep, LayerOverlay, LayerSheet, LayerDialog, LayerToast, LayerFocusRing,
    };

    /// <summary>色のトークン。</summary>
    private static readonly string[] Colors = { ColorScrim, ColorInverseSurface, ColorOnInverseSurface, ColorFocusRing };

    /// <summary>曲線のトークン（4 つの成分を持つ）。</summary>
    public static readonly string[] Curves =
    {
        MotionPushCurve, MotionCoverCurve, MotionFadeCurve, MotionOverlayCurve, MotionDialogCurve, MotionToastCurve,
        MotionBackPreviewCurve,
    };

    /// <summary>画面の組み立てが読むすべてのトークン（曲線は 4 つの成分に展開する）。</summary>
    public static IReadOnlyList<string> All
    {
        get
        {
            var all = new List<string>(Colors);
            all.AddRange(Numbers);
            foreach (var curve in Curves) all.AddRange(UiCurve.ComponentTokens(curve));
            return all;
        }
    }
}
