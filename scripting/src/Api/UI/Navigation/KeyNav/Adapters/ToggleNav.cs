namespace SEED.UI;

// ============================================================
//  ToggleNav.cs — トグル（SEED.UI.Toggle）の方向キーのアダプタ（2026-10-03。L3-6）
//
//  決定 = 指のタップと同じ（オン・オフを切り替えて Changed。つまみは motion.short で動く）。値の増減は無い。
// ============================================================

/// <summary>トグルのアダプタ。</summary>
internal sealed class ToggleNav : WidgetNav<Toggle>
{
    /// <summary>トグルに被せる。</summary>
    /// <param name="toggle">トグル。</param>
    public ToggleNav(Toggle toggle) : base(toggle) { }

    /// <inheritdoc />
    public override void OnNavSubmit()
    {
        if (Widget.IsEnabled) Widget.SetOn(!Widget.IsOn);
    }
}
