using SEED.UI;

namespace SEED.Localization;

// ============================================================
//  ButtonLabelTarget.cs — SEED.UI.Button の文字へ当てる当てる先
//
//  ボタンの公開の API（Button.SetText。子の Label の Text と Button.Text の欄を同時に書き換える）だけを使う。
//  ボタンの見た目（色・文字の大きさ・文字の枠の合わせ）は Button 自身が今までどおり決める。
// ============================================================

/// <summary>SEED.UI.Button の文字へ当てる。</summary>
public sealed class ButtonLabelTarget : ILocalizedTarget
{
    /// <summary>表の種類の名前。</summary>
    public const string Kind = "Button";

    /// <summary>当てるボタン。</summary>
    private readonly Button _button;

    /// <summary>当てる先を作る。</summary>
    /// <param name="button">当てるボタン。</param>
    public ButtonLabelTarget(Button button)
    {
        _button = button;
    }

    /// <inheritdoc />
    public string KindName => Kind;

    /// <inheritdoc />
    public bool IsAlive => _button.Owner.IsValid && ReferenceEquals(UiWidget.Of<Button>(_button.Owner), _button);

    /// <inheritdoc />
    public bool Apply(LocalizedRequest request)
    {
        if (!_button.Owner.IsValid) return false;
        _button.SetText(request.Resolve());
        return true;
    }
}
