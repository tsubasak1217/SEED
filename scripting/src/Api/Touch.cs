namespace SEED;

/// <summary>
/// 指 1 本のこのフレームでの状態（値型）。<see cref="Input.GetTouch"/> / <see cref="Input.Touches"/> で得る。
///
/// 値はフレームの間は変わらない（同じフレームに何度取得しても同じ）。
/// 位置は <see cref="Input.MousePos"/> と同じスクリーン座標（ピクセル・左上原点）。
/// </summary>
public readonly struct Touch
{
    /// <summary>
    /// 範囲外の <see cref="Input.GetTouch"/> が返す無効値
    /// （<see cref="FingerId"/> = -1、<see cref="Phase"/> = Canceled、位置・移動量 0）。
    /// </summary>
    public static readonly Touch None = new(InvalidFingerId, Vector2.Zero, Vector2.Zero, TouchPhase.Canceled);

    /// <summary>無効値の指番号。</summary>
    private const int InvalidFingerId = -1;

    /// <summary>
    /// 指番号（0 起点）。触れている間は変わらず、同じフレームに一覧へ載っている指の間で一意。
    /// 離れた指の番号は次に触れた指へ再利用される（空いている最小の番号）。
    /// </summary>
    public int FingerId { get; }

    /// <summary>位置（スクリーン座標・ピクセル・左上原点。<see cref="Input.MousePos"/> と同じ系）。</summary>
    public Vector2 Position { get; }

    /// <summary>
    /// 前フレームからの移動量（ピクセル。右=+X / 下=+Y）。
    /// 触れ始めたフレームは「触れ始めた位置」からの移動量（静止していれば 0）。
    /// </summary>
    public Vector2 DeltaPosition { get; }

    /// <summary>このフレームでの段階。</summary>
    public TouchPhase Phase { get; }

    /// <summary>有効な指か（範囲外の <see cref="Input.GetTouch"/> が返した <see cref="None"/> なら false）。</summary>
    public bool IsValid => FingerId >= 0;

    /// <summary>エンジン（FFI）から受け取った値で作る。</summary>
    internal Touch(int fingerId, Vector2 position, Vector2 deltaPosition, TouchPhase phase)
    {
        FingerId = fingerId;
        Position = position;
        DeltaPosition = deltaPosition;
        Phase = phase;
    }

    /// <summary>デバッグ表示用（例: <c>Touch(#0 Moved pos=(10.00, 20.00) delta=(1.00, 0.00))</c>）。</summary>
    public override string ToString() => $"Touch(#{FingerId} {Phase} pos={Position} delta={DeltaPosition})";
}
