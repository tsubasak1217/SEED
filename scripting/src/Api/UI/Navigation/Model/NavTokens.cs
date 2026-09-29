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

    // ── 角丸 ────────────────────────────────────────────────
    /// <summary>ダイアログの札。</summary>
    public const string RadiusDialog = "radius.dialog";
    /// <summary>シート・覆いの画面側の角。</summary>
    public const string RadiusSheet = "radius.sheet";
    /// <summary>トースト。</summary>
    public const string RadiusToast = "radius.toast";
    /// <summary>タブの選択の印（丸い帯）。</summary>
    public const string RadiusTabIndicator = "radius.tab_indicator";

    // ── 大きさ（キャンバスの単位＝dp）──────────────────────────
    /// <summary>下のタブの高さ（安全領域の分は別に足す）。</summary>
    public const string SizeTabBar = "size.tab_bar";
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
    /// <summary>トーストの帯（いちばん手前）。</summary>
    public const string LayerToast = "layer.toast";

    /// <summary>数のトークン（既定のテーマが持つかの検査用）。</summary>
    private static readonly string[] Numbers =
    {
        RadiusDialog, RadiusSheet, RadiusToast, RadiusTabIndicator,
        SizeTabBar, SizeTabIndicatorWidth, SizeTabIndicatorHeight, SizeDialogWidth, SizeDialogPadding, SizeDialogTitleGap, SizeDialogActionsGap,
        SizeDialogButtonHeight,
        SizeHandleWidth, SizeHandleHeight, SizeHandleArea, SizeToastHeight, SizeDragDismiss, SizeBackPreviewShift,
        MotionPush, MotionCover, MotionFade, MotionOverlay, MotionDialog, MotionSheet, MotionToast, MotionToastShort, MotionToastLong,
        OpacityScrim, OpacityDialogScrim,
        RatioPushParallax, RatioDialogScaleFrom, RatioSheetMaxHeight, RatioBackPreviewScale, SpeedFlingDismiss, CountToastVisible,
        LayerStackStep, LayerModalStep, LayerOverlay, LayerSheet, LayerDialog, LayerToast,
    };

    /// <summary>色のトークン。</summary>
    private static readonly string[] Colors = { ColorScrim, ColorInverseSurface, ColorOnInverseSurface };

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
