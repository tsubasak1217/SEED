namespace SEED.UI;

// ============================================================
//  ButtonNav.cs — ボタン（SEED.UI.Button）の方向キーのアダプタ（2026-10-03。L3-6）
//
//  決定 = 指のタップと同じ（触感と Clicked。Button.PerformClick）。値の増減は無い。
//  数値欄（NumberField）の子の −・＋ ボタンは移り先にしない（数値欄そのものが左右で増減する。NumberFieldNav）。
// ============================================================

/// <summary>ボタンのアダプタ。</summary>
internal sealed class ButtonNav : WidgetNav<Button>
{
    /// <summary>ボタンに被せる。</summary>
    /// <param name="button">ボタン。</param>
    public ButtonNav(Button button) : base(button) { }

    /// <inheritdoc />
    public override bool IsNavigable => !IsPartOfNumberField() && base.IsNavigable;

    /// <inheritdoc />
    public override void OnNavSubmit() => Widget.PerformClick();

    /// <summary>数値欄の子のボタンか（親のアクターに NumberField がある）。</summary>
    private bool IsPartOfNumberField() => UiWidget.Of<NumberField>(Widget.Owner.Parent) is not null;
}
