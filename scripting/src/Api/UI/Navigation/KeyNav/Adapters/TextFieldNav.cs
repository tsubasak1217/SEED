namespace SEED.UI;

// ============================================================
//  TextFieldNav.cs — 入力欄（SEED.UI.TextField）の方向キーのアダプタ（2026-10-03。L3-6）
//
//  決定 = 文字入力を始める（TextField.Focus = 既存の UiFocus.Request。キーボードが出て、カーソルは欄の設定どおり）。
//  入力している間（UiFocus の今の相手が入力欄）は方向キー・決定を UiNavigation が読まない（カーソルの移動・確定は欄が受ける）。
//  戻る（Escape・パッドのキャンセル）で欄がフォーカスを外すと、方向キーがまた効く。
// ============================================================

/// <summary>入力欄のアダプタ。</summary>
internal sealed class TextFieldNav : WidgetNav<TextField>
{
    /// <summary>入力欄に被せる。</summary>
    /// <param name="field">入力欄。</param>
    public TextFieldNav(TextField field) : base(field) { }

    /// <inheritdoc />
    public override void OnNavSubmit()
    {
        if (Widget.IsEnabled && !Widget.IsFocused) Widget.Focus();
    }
}
