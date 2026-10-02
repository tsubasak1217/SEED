namespace SEED.UI;

// ============================================================
//  WheelPickerNav.cs — ホイール（SEED.UI.WheelPicker。時刻ホイールの列も）の方向キーのアダプタ（2026-10-03。L3-6）
//
//  上下 = 選べる項目を 1 つずつ動かす（WheelPicker.StepBy。以前の「指で触れたホイールの上下の矢印」と同じ動き）。上下はいつも受ける。
//  左右はフォーカスを移す（時刻ホイールの時・分・午前/午後の列の間も、隣の列へ移る）。決定は何もしない。
//  UiNavigator が動いている間は、ホイール・時刻ホイール自身の矢印キーの読み取りは止まる（二重に動かない。UiNavigation.DrivesKeys）。
//  キーボードを受けない設定（WheelPicker.Keyboard = false）のホイールは移り先にしない。
// ============================================================

/// <summary>ホイールのアダプタ。</summary>
internal sealed class WheelPickerNav : WidgetNav<WheelPicker>
{
    /// <summary>ホイールに被せる。</summary>
    /// <param name="wheel">ホイール。</param>
    public WheelPickerNav(WheelPicker wheel) : base(wheel) { }

    /// <inheritdoc />
    public override bool IsNavigable => Widget.Keyboard && Widget.Count > 0 && base.IsNavigable;

    /// <inheritdoc />
    public override NavAxis AdjustAxis => NavAxis.Vertical;

    /// <inheritdoc />
    public override void OnNavSubmit() { }

    /// <inheritdoc />
    public override bool OnNavAdjust(int direction)
    {
        if (Widget.IsEnabled) Widget.StepBy(direction);
        return true;
    }
}
