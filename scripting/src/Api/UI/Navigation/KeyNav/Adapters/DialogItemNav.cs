namespace SEED.UI;

// ============================================================
//  DialogItemNav.cs — 選択肢のダイアログの行（SEED.UI.DialogItem）の方向キーのアダプタ（2026-10-03。L3-6）
//
//  決定 = 指のタップと同じ（触感と Tapped → ダイアログが選んで閉じる。DialogItem.PerformTap）。行は一覧の窓の中にあるので、
//  移ったら窓の中へスクロールする（UiNavigation が NavScroll で）。
// ============================================================

/// <summary>選択肢の行のアダプタ。</summary>
internal sealed class DialogItemNav : WidgetNav<DialogItem>
{
    /// <summary>選択肢の行に被せる。</summary>
    /// <param name="item">選択肢の行。</param>
    public DialogItemNav(DialogItem item) : base(item) { }

    /// <inheritdoc />
    public override void OnNavSubmit() => Widget.PerformTap();
}
