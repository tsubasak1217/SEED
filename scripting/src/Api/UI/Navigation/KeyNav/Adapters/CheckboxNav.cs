namespace SEED.UI;

// ============================================================
//  CheckboxNav.cs — チェックボックス（SEED.UI.Checkbox）の方向キーのアダプタ（2026-10-03。L3-6）
//
//  決定 = 指のタップと同じ（付け外しして Changed）。値の増減は無い。
// ============================================================

/// <summary>チェックボックスのアダプタ。</summary>
internal sealed class CheckboxNav : WidgetNav<Checkbox>
{
    /// <summary>チェックボックスに被せる。</summary>
    /// <param name="checkbox">チェックボックス。</param>
    public CheckboxNav(Checkbox checkbox) : base(checkbox) { }

    /// <inheritdoc />
    public override void OnNavSubmit()
    {
        if (Widget.IsEnabled) Widget.SetChecked(!Widget.IsChecked);
    }
}
