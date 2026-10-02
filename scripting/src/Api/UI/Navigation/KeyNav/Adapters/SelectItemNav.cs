namespace SEED.UI;

// ============================================================
//  SelectItemNav.cs — チップ（ChipGroup の項目 SelectItem）の方向キーのアダプタ（2026-10-03。L3-6）
//
//  チップは札ごとに移り先にし（折り返して並ぶ札の間を上下左右で移る）、決定 = 指のタップと同じ（選ぶ・外す。SelectionGroup.OnItemTapped）。
//  複数選ぶチップでは「左右で選択」を札の間の移動と決定に分けた（1 つの左右では複数の選び外しを表せない）。
//  セグメント・ラジオの項目は移り先にしない（グループが左右で選ぶ。SelectionGroupNav）。
// ============================================================

/// <summary>チップの札のアダプタ。</summary>
internal sealed class SelectItemNav : WidgetNav<SelectItem>
{
    /// <summary>項目に被せる。</summary>
    /// <param name="item">選択の項目。</param>
    public SelectItemNav(SelectItem item) : base(item) { }

    /// <inheritdoc />
    public override bool IsNavigable => Widget.Group is ChipGroup && base.IsNavigable;

    /// <inheritdoc />
    public override void OnNavSubmit()
    {
        if (Widget.IsEnabled) Widget.Group?.OnItemTapped(Widget);
    }
}
