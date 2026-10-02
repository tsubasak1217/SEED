using System;
using SEED.UI;

namespace SEED.Binding;

// ============================================================
//  ToggleTarget.cs — トグル（SEED.UI.Toggle）を双方向の当てる先にする（Bind.Toggle）
//
//  値 → 部品: SetOn(値, animate, notify: false)。最初の 1 回はつまみをすぐ置き（animate: false）、2 回目からは動かす。
//  部品 → 値: Toggle.Changed（利用者のタップ）。
// ============================================================

/// <summary>トグルを双方向の当てる先にする。</summary>
internal sealed class ToggleTarget : WidgetTarget<Toggle, bool>
{
    /// <summary>もう 1 回書いたか（最初の 1 回はつまみを動かさない）。</summary>
    private bool _written;

    /// <summary>当てる先を作る。</summary>
    /// <param name="widgetRef">トグルの引き当て。</param>
    internal ToggleTarget(WidgetRef<Toggle> widgetRef) : base(widgetRef) { }

    /// <inheritdoc />
    protected override void WriteTo(Toggle widget, bool value)
    {
        widget.SetOn(value, animate: _written, notify: false);
        _written = true;
    }

    /// <inheritdoc />
    protected override IDisposable ListenTo(Toggle widget, Action<bool> handler)
    {
        Action<Toggle, bool> onChanged = (_, on) => handler(on);
        widget.Changed += onChanged;
        return new DisposableAction(() => widget.Changed -= onChanged);
    }
}
