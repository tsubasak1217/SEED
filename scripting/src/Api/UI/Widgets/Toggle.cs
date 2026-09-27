using System;
using SEEDEditor.Scripting;

namespace SEED.UI;

// ============================================================
//  Toggle.cs — トグル（スイッチ。W2-4。docs/ui_components.md §3）
//
//  【プレハブ】templates/ui/prefabs/toggle.actor
//      Toggle（Sprite = 台〈ピル形の角丸〉・CanvasGesture〈タップ・押下の見た目・最小 48 dp〉・このスクリプト）
//      └─ Knob（Sprite = つまみ〈楕円〉。位置と大きさはスクリプトが決める）
//  【状態】オン・オフ・押下・無効。タップで切り替え、つまみは motion.short 秒で動く（UiTween）。
//  動いている間は SEED.Redraw.KeepAlive で描画を止めさせない（render_policy: on_demand）。見た目は ToggleLooks.ResolveSwitch。
// ============================================================

/// <summary>トグル（スイッチ）。</summary>
public sealed class Toggle : UiWidget
{
    /// <summary>子のつまみの名前。</summary>
    private const string KnobChild = "Knob";
    /// <summary>描き続けを頼むときの余裕（秒。最後の 1 フレームを描き切るため）。</summary>
    private const float KeepAliveSlack = 0.05f;

    /// <summary>オンか。</summary>
    [SerializeField(Label = "オン")]
    public bool IsOn;

    /// <summary>切り替わった（新しい値）。</summary>
    public event Action<Toggle, bool>? Changed;

    /// <summary>押している。</summary>
    public bool IsPressed { get; private set; }

    /// <summary>オンの度合いの動き（0 = オフ・1 = オン）。</summary>
    private UiTween _anim;

    /// <summary>台の大きさ（プレハブの Sprite の幅・高さ）。</summary>
    private Vector2 _trackSize;

    /// <summary>値を変える（animate = false ならつまみをすぐ動かす）。</summary>
    public void SetOn(bool on, bool animate = true, bool notify = true)
    {
        if (IsOn == on) return;
        IsOn = on;
        float to = on ? 1f : 0f;
        if (animate)
        {
            float duration = Theme.Number(UiTokens.MotionShort);
            _anim.Retarget(to, duration);
            Redraw.KeepAlive(duration + KeepAliveSlack);
        }
        else
        {
            _anim.Jump(to);
        }
        Refresh();
        if (notify) Changed?.Invoke(this, on);
    }

    /// <inheritdoc />
    protected override void OnWidgetStart()
    {
        _trackSize = SpriteOf() is { } track ? track.Size : Vector2.Zero;
        _anim = UiTween.At(IsOn ? 1f : 0f);
    }

    /// <inheritdoc />
    protected override void OnWidgetUpdate(float dt)
    {
        if (!_anim.IsRunning) return;
        _anim.Advance(dt);
        ApplyLook();
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
        SetOn(!IsOn);
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
        var look = ToggleLooks.ResolveSwitch(_anim.Value, IsPressed, !IsEnabled, _trackSize.x, _trackSize.y, Theme);
        if (SpriteOf() is { } track)
        {
            track.Color = look.Track;
            track.CornerRadius = look.TrackRadius;
            track.BorderColor = look.TrackBorder;
            track.BorderWidth = look.TrackBorder.a > 0f ? look.TrackBorderWidth : 0f;
        }
        if (SpriteOf(KnobChild) is { } knob)
        {
            knob.Shape = SpriteShapeKind.Ellipse;
            knob.Color = look.Knob;
            knob.Size = new Vector2(look.KnobSize, look.KnobSize);
        }
        if (TransformOf(KnobChild) is { } ct)
            ct.Position = new Vector2(look.KnobCenterX - look.KnobSize * 0.5f, look.KnobCenterY - look.KnobSize * 0.5f);
    }
}
