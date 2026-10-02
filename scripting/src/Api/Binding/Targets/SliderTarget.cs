using System;
using SEED.UI;

namespace SEED.Binding;

// ============================================================
//  SliderTarget.cs — スライダ（SEED.UI.Slider）を双方向の当てる先にする（Bind.Slider）
//
//  値 → 部品: SetValue(値, notify: false)。スライダは範囲（Min〜Max）と段階（Step）へ寄せるので、部品の見た目は寄せた値になる
//  （観測値は寄せる前の値のまま。利用者が動かすと寄せた値が観測値へ入る）。
//  部品 → 値: Slider.ValueChanged（指で動かした・タップした）。
// ============================================================

/// <summary>スライダを双方向の当てる先にする。</summary>
internal sealed class SliderTarget : WidgetTarget<Slider, float>
{
    /// <summary>当てる先を作る。</summary>
    /// <param name="widgetRef">スライダの引き当て。</param>
    internal SliderTarget(WidgetRef<Slider> widgetRef) : base(widgetRef) { }

    /// <inheritdoc />
    protected override void WriteTo(Slider widget, float value) => widget.SetValue(value, notify: false);

    /// <inheritdoc />
    protected override IDisposable ListenTo(Slider widget, Action<float> handler)
    {
        Action<Slider, float> onChanged = (_, value) => handler(value);
        widget.ValueChanged += onChanged;
        return new DisposableAction(() => widget.ValueChanged -= onChanged);
    }
}
