using System;
using SEED.UI;

namespace SEED.Binding;

// ============================================================
//  CheckboxTarget.cs — チェックボックス（SEED.UI.Checkbox）を双方向の当てる先にする（Bind.Checkbox）
//
//  値 → 部品: SetChecked(値, notify: false)。部品 → 値: Checkbox.Changed（利用者のタップ）。
// ============================================================

/// <summary>チェックボックスを双方向の当てる先にする。</summary>
internal sealed class CheckboxTarget : WidgetTarget<Checkbox, bool>
{
    /// <summary>当てる先を作る。</summary>
    /// <param name="widgetRef">チェックボックスの引き当て。</param>
    internal CheckboxTarget(WidgetRef<Checkbox> widgetRef) : base(widgetRef) { }

    /// <inheritdoc />
    protected override void WriteTo(Checkbox widget, bool value) => widget.SetChecked(value, notify: false);

    /// <inheritdoc />
    protected override IDisposable ListenTo(Checkbox widget, Action<bool> handler)
    {
        Action<Checkbox, bool> onChanged = (_, on) => handler(on);
        widget.Changed += onChanged;
        return new DisposableAction(() => widget.Changed -= onChanged);
    }
}
