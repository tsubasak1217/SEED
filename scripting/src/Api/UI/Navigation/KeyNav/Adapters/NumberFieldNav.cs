namespace SEED.UI;

// ============================================================
//  NumberFieldNav.cs — 数値欄（SEED.UI.NumberField）の方向キーのアダプタ（2026-10-03。L3-6）
//
//  数値欄そのものを 1 つの移り先にし、左右 = 段階ぶん増減（NumberField.StepBy。−・＋ ボタンのタップと同じ）。左右はいつも受ける。
//  子の −・＋ ボタンは移り先にしない（ButtonNav が外す）。決定は何もしない。
// ============================================================

/// <summary>数値欄のアダプタ。</summary>
internal sealed class NumberFieldNav : WidgetNav<NumberField>
{
    /// <summary>数値欄に被せる。</summary>
    /// <param name="field">数値欄。</param>
    public NumberFieldNav(NumberField field) : base(field) { }

    /// <inheritdoc />
    public override NavAxis AdjustAxis => NavAxis.Horizontal;

    /// <inheritdoc />
    public override void OnNavSubmit() { }

    /// <inheritdoc />
    public override bool OnNavAdjust(int direction)
    {
        if (Widget.IsEnabled) Widget.StepBy(direction);
        return true;
    }
}
