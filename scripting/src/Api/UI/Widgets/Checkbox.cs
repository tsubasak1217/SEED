using System;
using SEEDEditor.Scripting;

namespace SEED.UI;

// ============================================================
//  Checkbox.cs — チェックボックス（W2-4。docs/ui_components.md §3）
//
//  【プレハブ】templates/ui/prefabs/checkbox.actor
//      Checkbox（Sprite = 箱〈角丸・枠〉・CanvasGesture〈タップ・押下の見た目・最小 48 dp〉・このスクリプト）
//      └─ Mark（印のまとまり。オンのときだけ表示）
//         ├─ Short（Sprite。チェックの短い線〈回転した角丸の棒〉）
//         └─ Long（Sprite。チェックの長い線）
//  【状態】オン・オフ・押下・無効。タップで切り替え。見た目は ToggleLooks.ResolveCheckbox。
// ============================================================

/// <summary>チェックボックス。</summary>
public sealed class Checkbox : UiWidget
{
    /// <summary>子の印の名前。</summary>
    private const string MarkChild = "Mark";
    /// <summary>印の線の名前（Mark の子）。</summary>
    private static readonly string[] MarkStrokes = { "Mark/Short", "Mark/Long" };

    /// <summary>オンか。</summary>
    [SerializeField(Label = "オン")]
    public bool IsChecked;

    /// <summary>切り替わった（新しい値）。</summary>
    public event Action<Checkbox, bool>? Changed;

    /// <summary>押している。</summary>
    public bool IsPressed { get; private set; }

    /// <summary>値を変える。</summary>
    public void SetChecked(bool value, bool notify = true)
    {
        if (IsChecked == value) return;
        IsChecked = value;
        Refresh();
        if (notify) Changed?.Invoke(this, value);
    }

    /// <inheritdoc />
    public override void OnGesturePressDown(GestureEvent e)
    {
        if (!IsEnabled) return;
        IsPressed = true;
        Refresh();
    }

    /// <inheritdoc />
    public override void OnGesturePressCancel(GestureEvent e) => EndPress();

    /// <inheritdoc />
    public override void OnGesturePressUp(GestureEvent e) => EndPress();

    /// <inheritdoc />
    public override void OnGestureTap(GestureEvent e)
    {
        if (!IsEnabled) return;
        SetChecked(!IsChecked);
    }

    /// <summary>押下を終える。</summary>
    private void EndPress()
    {
        if (!IsPressed) return;
        IsPressed = false;
        Refresh();
    }

    /// <inheritdoc />
    protected override void ApplyLook()
    {
        var look = ToggleLooks.ResolveCheckbox(IsChecked, IsPressed, !IsEnabled, Theme);
        if (SpriteOf() is { } box)
        {
            box.Color = look.Box;
            box.CornerRadius = look.CornerRadius;
            box.BorderColor = look.Border;
            box.BorderWidth = look.BorderWidth;
        }
        var mark = gameObject.FindChild(MarkChild);
        if (mark.IsValid) mark.Visible = look.MarkVisible;
        foreach (var path in MarkStrokes)
            if (SpriteOf(path) is { } stroke) stroke.Color = look.Mark;
    }
}
