namespace SEED;

// ============================================================
//  GestureEvent.cs — ジェスチャーのイベント（W2-2。SEEDScript.OnGesture* の引数）
//
//  エンジンのジェスチャーアリーナ（runtime/src/engine/core/input/gesture/）が、CanvasGesture を付けたノードへ
//  タップ・長押し・ドラッグ・フリック・押下の見た目・ピンチ（W2-8）のイベントを配る。規則の正典は docs/input_gestures.md。
//  【重要】GestureKind の数値は Rust 側 gesture/events.rs の GestureEventKind と必ず一致させること（FFI で数値のまま渡る）。
// ============================================================

/// <summary>ジェスチャーのイベントの種類（<see cref="GestureEvent.Kind"/>）。</summary>
public enum GestureKind
{
    /// <summary>タップが成り立った（離したとき。PressUp の直後）。</summary>
    Tap = 0,
    /// <summary>長押しが成り立った（長押しの時間に達したとき。指はまだ触れている）。</summary>
    LongPress = 1,
    /// <summary>ドラッグが始まった（slop を超えて競いに勝ったとき）。</summary>
    DragStart = 2,
    /// <summary>ドラッグの途中（1 フレームに指 1 本につき 1 回までにまとめる）。</summary>
    DragUpdate = 3,
    /// <summary>ドラッグが終わった（離した・取り消された）。</summary>
    DragEnd = 4,
    /// <summary>フリック（ドラッグの終わりの速度がフリックの最小の速度以上。DragEnd の直後）。</summary>
    Fling = 5,
    /// <summary>押下の見た目を出す。</summary>
    PressDown = 6,
    /// <summary>押下の見た目を戻す（タップ・長押しにならなかった: 外へ出た・スクロールに負けた・複数指・取り消し）。</summary>
    PressCancel = 7,
    /// <summary>押下の見た目を戻す（タップ・長押しとして離した）。</summary>
    PressUp = 8,
    /// <summary>ピンチが始まった（W2-8。同じノードに触れた 2 本の指の間の距離が slop を超えて変わった。倍率は 1）。</summary>
    PinchStart = 9,
    /// <summary>ピンチの途中（1 フレームに 1 回までにまとめる。倍率はピンチの始まりからの比）。</summary>
    PinchUpdate = 10,
    /// <summary>ピンチが終わった（どちらかの指を離した・取り消された）。</summary>
    PinchEnd = 11,
}

/// <summary>
/// ジェスチャーのイベント（値型）。<see cref="SEEDScript"/> の OnGesture* コールバックの引数。
///
/// 座標は 3 種類: <see cref="Position"/>（キャンバスの画素。画面の中央が原点・Y 下向き。
/// <see cref="Input.MousePositionCanvas"/> と同じ）、<see cref="ScreenPosition"/>（画面の画素・左上原点。
/// <see cref="Input.MousePos"/>・<see cref="Touch.Position"/> と同じ）、<see cref="LocalPosition"/>（ノードの見た目の矩形の
/// 左上が原点・ノードの単位＝dp のキャンバスなら dp。スライダの「どこを押したか」に使う）。
/// 移動量・速度は画素と dp（端末に依らない単位）の両方で読める。軸を決めたドラッグ（横だけ・縦だけ）では、
/// 移動量・速度は軸へ射影されている（位置は射影しない）。
/// </summary>
public readonly struct GestureEvent
{
    /// <summary>種類。</summary>
    public GestureKind Kind { get; }

    /// <summary>指の番号（0 起点。ジェスチャーに参加している指の間で空いている最小の番号。<see cref="Touch.FingerId"/> とは別）。</summary>
    public int PointerId { get; }

    /// <summary>今の位置（キャンバスの画素。画面の中央が原点・Y 下向き）。</summary>
    public Vector2 Position { get; }

    /// <summary>今の位置（画面の画素・左上原点）。</summary>
    public Vector2 ScreenPosition { get; }

    /// <summary>今の位置（ノードの見た目の矩形の左上が原点・ノードの単位）。</summary>
    public Vector2 LocalPosition { get; }

    /// <summary>押した位置（キャンバスの画素）。</summary>
    public Vector2 StartPosition { get; }

    /// <summary>
    /// 移動量（画素）。DragStart: 押した位置からの移動（slop の分）、DragUpdate: 前のドラッグのイベントからの移動
    /// （1 フレームぶんを足し合わせたもの）。それ以外は 0。
    /// </summary>
    public Vector2 Delta { get; }

    /// <summary>速度（画素/秒）。ドラッグ・フリックで、直近の標本から推定し上限（8000 dp/秒）で切り詰めたもの。</summary>
    public Vector2 Velocity { get; }

    /// <summary>1 dp の画素数（端末の表示倍率。PC の 100% は 1、Pixel 6a は 2.625）。</summary>
    public float DpScale { get; }

    /// <summary>押してからの時間（秒。入力イベントの時刻で測る。フレームの時刻に依らない）。</summary>
    public float Duration { get; }

    /// <summary>取り消しで終わったか（DragEnd・PinchEnd が指の取り消し・アプリが背面へ・一時停止で来たとき true。速度は 0）。</summary>
    public bool Canceled { get; }

    /// <summary>
    /// ピンチの倍率（W2-8）: 今の 2 本の指の間の距離 ÷ ピンチが始まったときの距離（PinchStart で 1、広げると 1 より大きく、すぼめると小さい）。
    /// ピンチ以外のイベントは 1。ピンチの <see cref="Position"/> は 2 本の指の中点、<see cref="Delta"/> は前のピンチのイベントからの中点の移動。
    /// </summary>
    public float Scale { get; }

    /// <summary>ピンチの横の倍率（横の幅の比。始まったときの横の幅が 1 画素以下なら 1）。</summary>
    public float ScaleX { get; }

    /// <summary>ピンチの縦の倍率（縦の幅の比。始まったときの縦の幅が 1 画素以下なら 1）。</summary>
    public float ScaleY { get; }

    /// <summary>移動量（dp）。</summary>
    public Vector2 DeltaDp => DpScale > 0f ? Delta / DpScale : Delta;

    /// <summary>速度（dp/秒）。</summary>
    public Vector2 VelocityDp => DpScale > 0f ? Velocity / DpScale : Velocity;

    /// <summary>押した位置からの移動（キャンバスの画素。軸へは射影しない）。</summary>
    public Vector2 TotalDelta => Position - StartPosition;

    /// <summary>エンジン（FFI）から受け取った値で作る。</summary>
    internal GestureEvent(
        GestureKind kind, int pointerId, Vector2 position, Vector2 screenPosition, Vector2 localPosition,
        Vector2 startPosition, Vector2 delta, Vector2 velocity, float dpScale, float duration, bool canceled,
        float scale = 1f, float scaleX = 1f, float scaleY = 1f)
    {
        Kind = kind;
        PointerId = pointerId;
        Position = position;
        ScreenPosition = screenPosition;
        LocalPosition = localPosition;
        StartPosition = startPosition;
        Delta = delta;
        Velocity = velocity;
        DpScale = dpScale;
        Duration = duration;
        Canceled = canceled;
        Scale = scale;
        ScaleX = scaleX;
        ScaleY = scaleY;
    }

    /// <summary>デバッグ表示用（例: <c>Gesture(DragUpdate #0 pos=(10.00, 20.00) delta=(0.00, 5.00) vel=(0.00, 300.00))</c>）。</summary>
    public override string ToString()
        => $"Gesture({Kind} #{PointerId} pos={Position} delta={Delta} vel={Velocity}{(Scale != 1f ? $" scale={Scale:0.###}" : "")}{(Canceled ? " canceled" : "")})";
}
