using System;

namespace SEED.UI;

// ============================================================
//  SwipeActions.cs — 行を横へずらすと操作のボタン（削除・編集）が出る（W2-3。docs/ui_scroll_list.md §7）
//
//  【作り】行のプレハブ:
//      Row（CanvasGesture: tap=false・drag=true・fling=true・drag_axis=horizontal、行のスクリプト）
//      ├─ Actions（右端に並べた操作のボタン。ずらすと見える）
//      └─ Front（行の見た目。これを横へずらす）
//  行のスクリプトが OnGestureDragStart / DragUpdate / DragEnd をこの部品へ渡し、Update で Update(dt) を呼ぶ。
//  縦の一覧の中では、最初の指の動きの向きで一覧のスクロールか行のスワイプかが決まる（W2-2 のアリーナの軸の競い）。
//
//  【振る舞い】（規則と値の出典は SwipeMath.cs）
//    - ドラッグで Front をずらす（閉じた 0 〜 開いた量）。離したら、速さか閾値で開いたまま・閉じるを決め、250ms で動く
//    - 同じ SwipeGroup の他の行が開いていたら、この行のドラッグの始まりで閉じる（開いている行は 1 つだけ）
//    - 一覧のスクロールが始まったら閉じる（SwipeGroup.CloseAll を持ち主が OnScrollStart から呼ぶ）
//    - 行を使い回すときは Reset で閉じる（ListView.Recycled から）
//  動いている間は SEED.Redraw.Request()（W2-10a の約束。on_demand でも止まらない）。
// ============================================================

/// <summary>操作のボタンがどちら側にあるか。</summary>
public enum SwipeSide
{
    /// <summary>右側（左へずらすと出る。削除など）。</summary>
    Right = 0,
    /// <summary>左側（右へずらすと出る）。</summary>
    Left = 1,
}

/// <summary>行を横へずらすと操作のボタンが出るスワイプの操作（行 1 つぶん）。</summary>
public sealed class SwipeActions
{
    /// <summary>ずらす行の見た目（Front）。</summary>
    public GameObject Front { get; }
    /// <summary>操作のボタンの側。</summary>
    public SwipeSide Side { get; }
    /// <summary>開いたときにずらす長さ（操作のボタンの幅。キャンバスの単位）。</summary>
    public float ActionsExtent { get; set; }
    /// <summary>開くとみなすずらし量の割合（0〜1。既定 0.5）。</summary>
    public float OpenThreshold { get; set; } = SwipeMath.DefaultOpenThreshold;
    /// <summary>速さで開く・閉じるを決める閾値（dp/秒。既定 120）。</summary>
    public float EscapeVelocityDp { get; set; } = SwipeMath.DefaultEscapeVelocityDp;
    /// <summary>開く・閉じるの動きの時間（秒。既定 0.25）。</summary>
    public float SettleSeconds { get; set; } = SwipeMath.DefaultSettleSeconds;
    /// <summary>開いている行を 1 つにする組（null なら組まない）。</summary>
    public SwipeGroup? Group
    {
        get => _group;
        set
        {
            _group?.Remove(this);
            _group = value;
            _group?.Add(this);
        }
    }

    /// <summary>今のずらし量（0 = 閉じた・<see cref="OpenOffset"/> = 開いた）。</summary>
    public float Offset { get; private set; }
    /// <summary>開いた状態（開いた・開く途中）か。</summary>
    public bool IsOpen { get; private set; }
    /// <summary>指でずらしている途中か。</summary>
    public bool IsDragging { get; private set; }
    /// <summary>開く・閉じるの動きの途中か。</summary>
    public bool IsAnimating => _animElapsed >= 0f;

    /// <summary>開いた（開くと決まった）ときに呼ばれる。</summary>
    public Action<SwipeActions>? Opened { get; set; }
    /// <summary>閉じた（閉じると決まった）ときに呼ばれる。</summary>
    public Action<SwipeActions>? Closed { get; set; }

    /// <summary>開いたときのずらし量（右側の操作なら負）。</summary>
    public float OpenOffset => Side == SwipeSide.Right ? -MathF.Abs(ActionsExtent) : MathF.Abs(ActionsExtent);

    private SwipeGroup? _group;
    /// <summary>閉じたときの Front の位置（X）。</summary>
    private readonly float _baseX;
    /// <summary>前のドラッグのイベントのノードのローカルの X。</summary>
    private float _lastLocalX;
    /// <summary>動きの始めのずらし量。</summary>
    private float _animFrom;
    /// <summary>動きの目標のずらし量。</summary>
    private float _animTo;
    /// <summary>動きの経過（秒。負なら動いていない）。</summary>
    private float _animElapsed = -1f;

    /// <summary>作る（Front の今の位置を閉じた位置として控える）。</summary>
    /// <param name="front">ずらす行の見た目。</param>
    /// <param name="actionsExtent">開いたときにずらす長さ（操作のボタンの幅）。</param>
    /// <param name="side">操作のボタンの側。</param>
    public SwipeActions(GameObject front, float actionsExtent, SwipeSide side = SwipeSide.Right)
    {
        Front = front;
        ActionsExtent = actionsExtent;
        Side = side;
        _baseX = front.GetComponent<CanvasTransform>() is { } ct ? ct.Position.x : 0f;
    }

    /// <summary>行の OnGestureDragStart から呼ぶ（他の開いている行を閉じ、動きを止めてずらし始める）。</summary>
    public void OnDragStart(GestureEvent e)
    {
        _group?.CloseOthers(this);
        IsDragging = true;
        _animElapsed = -1f;
        // 押した位置から slop の分は当てない（指の位置から飛ばない）
        _lastLocalX = e.LocalPosition.x;
    }

    /// <summary>行の OnGestureDragUpdate から呼ぶ（指の横の移動だけずらす）。</summary>
    public void OnDragUpdate(GestureEvent e)
    {
        if (!IsDragging) OnDragStart(e);
        float delta = e.LocalPosition.x - _lastLocalX;
        _lastLocalX = e.LocalPosition.x;
        Offset = SwipeMath.ApplyDrag(Offset, delta, OpenOffset);
        Apply();
    }

    /// <summary>行の OnGestureDragEnd から呼ぶ（速さか閾値で開いたまま・閉じるを決めて動かす。取り消しなら元の状態へ）。</summary>
    public void OnDragEnd(GestureEvent e)
    {
        IsDragging = false;
        bool open = e.Canceled
            ? IsOpen
            : SwipeMath.ShouldOpen(Offset, e.VelocityDp.x, OpenOffset, OpenThreshold, EscapeVelocityDp);
        SetOpen(open, animated: true);
    }

    /// <summary>開く。</summary>
    public void Open(bool animated = true) => SetOpen(true, animated);

    /// <summary>閉じる。</summary>
    public void Close(bool animated = true) => SetOpen(false, animated);

    /// <summary>すぐ閉じる（行を使い回すとき。ドラッグの途中もやめる）。</summary>
    public void Reset()
    {
        IsDragging = false;
        SetOpen(false, animated: false);
    }

    /// <summary>毎フレーム呼ぶ（行のスクリプトの Update から。開く・閉じるの動きを進める）。</summary>
    /// <param name="dt">経過時間（秒。Time.UnscaledDeltaTime を渡す）。</param>
    public void Update(float dt)
    {
        if (!IsAnimating) return;
        _animElapsed += MathF.Max(0f, dt);
        float t = SettleSeconds > 0f ? _animElapsed / SettleSeconds : 1f;
        Offset = _animFrom + (_animTo - _animFrom) * SwipeMath.Ease(t);
        if (t >= 1f)
        {
            Offset = _animTo;
            _animElapsed = -1f;
        }
        Apply();
        // 動いている間は描き続ける（W2-10a。止まったら何もしない）
        Redraw.Request();
    }

    /// <summary>開く・閉じるを決めて動かす（または、すぐ移す）。</summary>
    private void SetOpen(bool open, bool animated)
    {
        bool changed = open != IsOpen;
        IsOpen = open;
        float target = open ? OpenOffset : 0f;
        if (animated && MathF.Abs(target - Offset) > 0f)
        {
            _animFrom = Offset;
            _animTo = target;
            _animElapsed = 0f;
            Redraw.Request();
        }
        else
        {
            Offset = target;
            _animElapsed = -1f;
            Apply();
        }
        if (changed)
        {
            if (open) Opened?.Invoke(this); else Closed?.Invoke(this);
        }
    }

    /// <summary>Front を今のずらし量の位置へ置く。</summary>
    private void Apply()
    {
        if (Front.GetComponent<CanvasTransform>() is not { } ct) return;
        var p = ct.Position;
        ct.Position = new Vector2(_baseX + Offset, p.y);
    }
}
