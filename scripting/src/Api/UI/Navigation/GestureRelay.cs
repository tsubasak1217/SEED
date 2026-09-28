using System;

namespace SEED.UI;

// ============================================================
//  GestureRelay.cs — 押す・引くを持ち主の部品へ渡すだけの面（W2-7）
//
//  ジェスチャーのコールバック（OnGesture*）は CanvasGesture を付けたノードのスクリプトにだけ届く。ダイアログ・シート・覆いの
//  幕（タップで閉じる）やつまみ（ドラッグで閉じる）は部品の根とは別のノードなので、そのノードにこの部品を付け、
//  根の部品がイベントを受ける。見た目は変えない（幕の色は持ち主が決める）。
// ============================================================

/// <summary>押す・引くを持ち主へ渡すだけの面（幕・隙間・つまみ）。</summary>
public sealed class GestureRelay : UiWidget
{
    /// <summary>タップされた。</summary>
    public event Action<GestureRelay, GestureEvent>? Tapped;
    /// <summary>ドラッグが始まった。</summary>
    public event Action<GestureRelay, GestureEvent>? DragStarted;
    /// <summary>ドラッグの途中。</summary>
    public event Action<GestureRelay, GestureEvent>? DragUpdated;
    /// <summary>ドラッグが終わった（速度つき。取り消しなら Canceled）。</summary>
    public event Action<GestureRelay, GestureEvent>? DragEnded;

    /// <inheritdoc />
    public override void OnGestureTap(GestureEvent e)
    {
        if (IsEnabled) Tapped?.Invoke(this, e);
    }

    /// <inheritdoc />
    public override void OnGestureDragStart(GestureEvent e)
    {
        if (IsEnabled) DragStarted?.Invoke(this, e);
    }

    /// <inheritdoc />
    public override void OnGestureDragUpdate(GestureEvent e)
    {
        if (IsEnabled) DragUpdated?.Invoke(this, e);
    }

    /// <inheritdoc />
    public override void OnGestureDragEnd(GestureEvent e)
    {
        // 取り消しも届ける（押さえていた状態を戻すため）
        DragEnded?.Invoke(this, e);
    }

    /// <inheritdoc />
    protected override void ApplyLook() { }
}
