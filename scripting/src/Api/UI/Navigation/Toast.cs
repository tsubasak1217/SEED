using System;

namespace SEED.UI;

// ============================================================
//  Toast.cs — トースト 1 つ（下に一時的に出る短い知らせ。W2-7。docs/ui_navigation.md §3.5）
//
//  【プレハブ】templates/ui/prefabs/toast.actor（ToastHost がここの子として作る）:
//      Toast（Canvas・Sprite〈明るい面・角丸〉・CanvasGesture〈横のドラッグ・フリック〉・CanvasLayoutItem〈高さ〉・このスクリプト）
//      ├─ Icon（Sprite。並べない・既定は隠す。先頭のアイコン〈図形か画像〉。2026-10-02）
//      └─ Label（Text。並べない・左の中央）
//  【先頭のアイコン（2026-10-02）】Toast.Show(文字, UiIcon) で、Label の左端（プレハブの位置 = 左の余白）にアイコン（size.icon・
//  縦の真ん中）を置き、文字をアイコン ＋ size.icon_gap の右へずらす。図形の既定の色は文字の色（color.on_inverse_surface）。
//  Icon の子が無い古いプレハブでは、アイコンを出さず文字だけ（落ちない）。
//  入る: 自分の高さだけ下から上がる（TranslateFraction.y 1 → 0。motion.toast・motion.toast_curve）。
//  出る: 時間が来た・スクリプトで消した → 下へ、スワイプで消した → その向きへ横に出る。出終わったら自分を消す。
//  スワイプ: 横のドラッグで Translate.x を動かし、離したとき DragDismissMath（size.drag_dismiss・speed.fling_dismiss）で決める。
//  押さえている間は時間を止める（ToastHost.SetHeld）。
//  使い方: Toast.Show("保存しました")（シーンの ToastHost へ出す）。
// ============================================================

/// <summary>トースト 1 つ。</summary>
public sealed class Toast : UiWidget
{
    /// <summary>子の文字の名前。</summary>
    private const string LabelChild = "Label";
    /// <summary>子のアイコンの名前（2026-10-02）。</summary>
    private const string IconChild = "Icon";
    /// <summary>半分（アイコンを縦の真ん中に置く）。</summary>
    private const float Half = 0.5f;
    /// <summary>出ていくときに動く量（自分の大きさに対する割合。画面の外へ出切る）。</summary>
    private const float ExitDistance = 1.2f;
    /// <summary>入るときの最初の位置（自分の高さに対する割合。下から）。</summary>
    private const float EnterFrom = 1f;

    /// <summary>項目（ToastHost から受け取る）。</summary>
    private ToastItem? _item;
    /// <summary>持ち主の ToastHost。</summary>
    private ToastHost? _host;
    /// <summary>入る動き（0 → 1）。</summary>
    private UiTween _enter = UiTween.At(0f);
    /// <summary>出る動き（0 → 1）。</summary>
    private UiTween _exit = UiTween.At(0f);
    /// <summary>出ていく途中か。</summary>
    private bool _exiting;
    /// <summary>出ていく横の向き（−1 = 左・0 = 下・1 = 右）。</summary>
    private float _exitDirection;
    /// <summary>横に引いた距離（dp）。</summary>
    private float _dragX;
    /// <summary>離した後に戻る動き。</summary>
    private UiTween _settle = UiTween.At(0f);
    /// <summary>引いているか。</summary>
    private bool _dragging;
    /// <summary>動きの 1 フレームの進め（入る動きを始めたフレームは数えない・上限。遷移の時計の直し）。</summary>
    private MotionStep _step;
    /// <summary>プレハブの文字の左端（Label の位置 = 左の余白。アイコンを置くと右へずらすので開始時に覚える）。</summary>
    private Vector2 _labelBase;

    /// <summary>
    /// シーンの ToastHost へトーストを出す（ToastHost が無ければ null・警告）。
    /// </summary>
    public static ToastItem? Show(string message, ToastLength length = ToastLength.Short) => Show(message, null, length);

    /// <summary>
    /// シーンの ToastHost へ先頭のアイコンつきのトーストを出す（2026-10-02。ToastHost が無ければ null・警告）。
    /// </summary>
    /// <param name="message">文字。</param>
    /// <param name="icon">先頭のアイコン（図形か画像。null なら文字だけ）。</param>
    /// <param name="length">見せる長さ。</param>
    public static ToastItem? Show(string message, UiIcon? icon, ToastLength length = ToastLength.Short)
    {
        if (ToastHost.Current is { } host) return host.Show(message, icon, length);
        Debug.LogWarning("[UI] toast: ToastHost がシーンにありません");
        return null;
    }

    /// <inheritdoc />
    protected override void OnWidgetStart()
    {
        _host = ToastHost.Current;
        _item = _host?.Claim(this);
        if (_item is null)
        {
            Owner.Destroy();
            return;
        }
        if (TextOf(LabelChild) is { } label) label.Content = _item.Message;
        if (TransformOf(LabelChild) is { } labelCt) _labelBase = labelCt.Position;
        if (gameObject.GetComponent<CanvasLayoutItem>() is { } item)
            item.PreferredSize = new Vector2(0f, Theme.Number(NavTokens.SizeToastHeight));
        // 先頭のアイコン（無ければ Icon の子を隠し、文字はプレハブの位置のまま）
        ApplyIcon();
        ApplyPose();
        NavNode.SetVisible(Owner, true);
        _enter.Retarget(1f, Theme.Number(NavTokens.MotionToast));
        // 入る動きを始めたフレーム（OnStart と同じフレームの Update で最初に進める）の経過は数えない（組み立ての時間が入っている）
        _step.SkipNext();
    }

    /// <summary>出ていく（direction: −1 = 左・1 = 右へスワイプ、0 = 下へ）。</summary>
    internal void AnimateOut(float swipeDirection)
    {
        if (_exiting) return;
        _exiting = true;
        _exitDirection = Math.Sign(swipeDirection);
        _exit.Retarget(1f, Theme.Number(NavTokens.MotionToast));
        Redraw.Request();
    }

    /// <inheritdoc />
    protected override void OnWidgetUpdate(float dt)
    {
        // 1 フレームで動きに足す時間は上限まで（重いフレームで入る・出る動きが飛ばない。入る動きの始めのフレームは 0）。
        // 見せる時間（2 秒・3.5 秒）は ToastHost が実時間で数えるので、ここの上限では延びない
        float step = _step.Next(Time.UnscaledDeltaTime);
        bool moving = false;
        if (_enter.IsRunning)
        {
            _enter.Advance(step);
            moving = true;
        }
        if (_exit.IsRunning)
        {
            _exit.Advance(step);
            moving = true;
        }
        if (!_dragging && _settle.IsRunning)
        {
            _dragX = _settle.Advance(step);
            moving = true;
        }
        ApplyPose();
        if (moving) Redraw.KeepAlive(Math.Max(_enter.Remaining, Math.Max(_exit.Remaining, _settle.Remaining)));
        if (_exiting && !_exit.IsRunning && _item is not null)
        {
            var item = _item;
            _item = null;
            _host?.OnExitDone(item);
            Owner.Destroy();
        }
    }

    /// <summary>入る・出る・引いた距離 → ずらし。</summary>
    private void ApplyPose()
    {
        var curve = UiCurve.FromTheme(Theme, NavTokens.MotionToastCurve, UiCurve.FastOutSlowIn);
        float enter = curve.Evaluate(_enter.Value);
        float exit = curve.Evaluate(_exit.Value);
        var fraction = new Vector2(_exitDirection * exit * ExitDistance, EnterFrom * (1f - enter) + (_exitDirection == 0f ? exit * ExitDistance : 0f));
        NavNode.SetFraction(Owner, fraction);
        NavNode.SetTranslate(Owner, new Vector2(_dragX, 0f));
    }

    /// <inheritdoc />
    public override void OnGestureDragStart(GestureEvent e)
    {
        if (_exiting || _item is null) return;
        _dragging = true;
        _host?.SetHeld(_item, true);
        _dragX += e.DeltaDp.x;
        Redraw.Request();
    }

    /// <inheritdoc />
    public override void OnGestureDragUpdate(GestureEvent e)
    {
        if (!_dragging) return;
        _dragX += e.DeltaDp.x;
        Redraw.Request();
    }

    /// <inheritdoc />
    public override void OnGestureDragEnd(GestureEvent e)
    {
        if (!_dragging || _item is null) return;
        _dragging = false;
        _host?.SetHeld(_item, false);
        float direction = Math.Sign(_dragX != 0f ? _dragX : e.VelocityDp.x);
        if (!e.Canceled && direction != 0f
            && DragDismissMath.ShouldDismiss(MathF.Abs(_dragX), direction * e.VelocityDp.x, Theme.Number(NavTokens.SizeDragDismiss), Theme.Number(NavTokens.SpeedFlingDismiss)))
        {
            _host?.OnSwiped(this, _item, direction);
            return;
        }
        _settle.Jump(_dragX);
        _settle.Retarget(0f, Theme.Number(UiTokens.MotionShort));
        Redraw.Request();
    }

    /// <inheritdoc />
    protected override void ApplyLook()
    {
        if (SpriteOf() is { } bg)
        {
            bg.Color = Theme.Color(NavTokens.ColorInverseSurface);
            bg.CornerRadius = Theme.Number(NavTokens.RadiusToast);
        }
        if (TextOf(LabelChild) is { } label)
        {
            label.Color = Theme.Color(NavTokens.ColorOnInverseSurface);
            UiTextStyle.Apply(label, Theme, UiTokens.TextBody);
        }
        // テーマの差し替えでアイコンの既定の色・大きさも追従する（項目を受け取った後だけ）
        if (_item is not null) ApplyIcon();
    }

    /// <summary>
    /// 先頭のアイコンを当て、文字をアイコンの右へずらす（2026-10-02。アイコンが無い・Icon の子が無い古いプレハブでは文字をプレハブの位置へ）。
    /// アイコンは文字の左端（プレハブの Label の位置 = 左の余白）に、トーストの高さ（size.toast_height）の縦の真ん中に置く。
    /// </summary>
    private void ApplyIcon()
    {
        var iconNode = gameObject.FindChild(IconChild);
        var look = iconNode.IsValid
            ? UiIconLooks.Resolve(_item?.Icon, Theme, Theme.Color(NavTokens.ColorOnInverseSurface))
            : UiIconLook.Hidden;
        float height = Theme.Number(NavTokens.SizeToastHeight);
        UiIconView.Apply(iconNode, look, new Vector2(_labelBase.x, (height - look.Size) * Half));
        float labelX = UiIconLooks.TextStart(look, _labelBase.x, Theme.Number(UiTokens.SizeIconGap));
        if (TransformOf(LabelChild) is { } labelCt)
        {
            var position = new Vector2(labelX, _labelBase.y);
            if (labelCt.Position != position) labelCt.Position = position;
        }
    }
}
