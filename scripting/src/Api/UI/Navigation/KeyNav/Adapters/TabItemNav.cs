namespace SEED.UI;

// ============================================================
//  TabItemNav.cs — 下のタブの項目（SEED.UI.TabItem）の方向キーのアダプタ（2026-10-03。L3-6）
//
//  決定 = 指のタップと同じ（TabBar.OnItemTapped → TabHost が選ぶ・もう一度押したら根へ）。値の増減は無い（左右で隣の項目へ移る）。
//  タブのバーはシェル（根の画面）の中にあり、タブの画面のフォーカスの範囲がいちばん前でも移れる（NavScopeFilter の「中に含む範囲」）。
// ============================================================

/// <summary>タブの項目のアダプタ。</summary>
internal sealed class TabItemNav : WidgetNav<TabItem>
{
    /// <summary>タブの項目に被せる。</summary>
    /// <param name="item">タブの項目。</param>
    public TabItemNav(TabItem item) : base(item) { }

    /// <inheritdoc />
    public override bool IsNavigable => (Widget.Bar?.IsEnabled ?? true) && base.IsNavigable;

    /// <inheritdoc />
    public override void OnNavSubmit()
    {
        if (Widget.IsEnabled) Widget.Bar?.OnItemTapped(Widget);
    }
}
