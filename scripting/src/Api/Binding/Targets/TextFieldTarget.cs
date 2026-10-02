using System;
using SEED.UI;

namespace SEED.Binding;

// ============================================================
//  TextFieldTarget.cs — 文字の欄（SEED.UI.TextField）を双方向の当てる先にする（Bind.TextField）
//
//  値 → 部品: SetTextUnlessFocused(値)。打っている最中（フォーカスの間）の欄は外の値で上書きしない（欄の決まり。
//  スライダとの双方向と同じ）。フォーカスの間に外で変えた値は欄へ入らず、利用者が打つと欄の本文が観測値へ入る。
//  部品 → 値: TextField.TextChanged（1 文字ごと。変換中の文字の変化を含む）。
// ============================================================

/// <summary>文字の欄を双方向の当てる先にする。</summary>
internal sealed class TextFieldTarget : WidgetTarget<TextField, string>
{
    /// <summary>当てる先を作る。</summary>
    /// <param name="widgetRef">文字の欄の引き当て。</param>
    internal TextFieldTarget(WidgetRef<TextField> widgetRef) : base(widgetRef) { }

    /// <inheritdoc />
    protected override void WriteTo(TextField widget, string value) => widget.SetTextUnlessFocused(value ?? string.Empty);

    /// <inheritdoc />
    protected override IDisposable ListenTo(TextField widget, Action<string> handler)
    {
        Action<TextField, string> onChanged = (_, text) => handler(text);
        widget.TextChanged += onChanged;
        return new DisposableAction(() => widget.TextChanged -= onChanged);
    }
}
