namespace SEED.UI;

// ============================================================
//  PopupOptions.cs — 中央のポップアップ（SEED.UI.Popup）の指定（2026-10-02。lane3。docs/ui_navigation.md §3.6。backlog W3-6 (3)）
//
//  画面の真ん中に札（カード）を出し、周りの幕を押すと閉じる面（Wake or Pay のプロフィールのポップアップを汎用化）。
//  出入りはダイアログと同じ（幕の濃さ 0 → opacity.dialog_scrim・札の大きさ ratio.dialog_scale_from → 1・motion.dialog）。
//  札の幅は画面の幅 − size.popup_margin × 2（上限 size.popup_max_width）、高さは中身の高さ ＋ size.popup_padding × 2
//  （上限は安全領域の高さ × ratio.popup_max_height）。正典の計算は PopupCardMath。
// ============================================================

/// <summary>中央のポップアップの指定。</summary>
public sealed class PopupOptions
{
    /// <summary>札の中身のプレハブ（assets:// の .actor。空なら札だけ）。</summary>
    public string ContentPrefab { get; init; } = string.Empty;

    /// <summary>中身へ渡す値（中身の UiScreen.OnScreenEnter）。</summary>
    public object? Args { get; init; }

    /// <summary>幕のタップで閉じるか（既定 true）。</summary>
    public bool DismissOnScrimTap { get; init; } = true;

    /// <summary>戻るで閉じるか（false でも戻るは受ける。既定 true）。</summary>
    public bool CancelableByBack { get; init; } = true;

    /// <summary>開く動きを付けるか（false = 幕と札を最初から開いた姿で出す。NavTransition.None 相当。既定 true）。</summary>
    public bool Animate { get; init; } = true;

    /// <summary>右上の丸い閉じるボタン（プレハブの CloseButton）を出すか（既定 true）。</summary>
    public bool ShowCloseButton { get; init; } = true;

    /// <summary>札の幅（キャンバスの単位。0 以下 = 画面の幅 − size.popup_margin × 2。どちらも size.popup_max_width と画面の幅まで）。</summary>
    public float Width { get; init; }

    /// <summary>
    /// 札の中身の高さ（キャンバスの単位。札の内側の余白 size.popup_padding は含まない。0 以下 = 中身から決める: 中身の画面のスクリプトが
    /// <see cref="IPopupContentSize"/> ならその高さ、そうでなければ中身の根のレイアウトの高さ〈親に合わせる中身は上限の高さ〉）。
    /// </summary>
    public float ContentHeight { get; init; }

    /// <summary>入れる帯と戻るの層（既定 Overlay = 覆いの帯。シートの上に出すなら Dialog）。</summary>
    public ModalKind Kind { get; init; } = ModalKind.Overlay;
}
