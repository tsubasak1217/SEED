namespace SEED;

// ============================================================
//  ScrollEvent.cs — スクロールのイベント（OnScrollStart / OnScroll / OnScrollEnd の引数。W2-3）
//
//  【重要】ScrollEventKind の数値は Rust 側 engine/core/canvas_scroll/events.rs の ScrollEventKind と必ず一致させること。
// ============================================================

/// <summary>スクロールのイベントの種類。</summary>
public enum ScrollEventKind
{
    /// <summary>スクロールが始まった（ドラッグ・慣性・ScrollTo・すぐ移す）。</summary>
    Start = 0,
    /// <summary>位置が変わった（1 フレームに 1 回まで）。</summary>
    Update = 1,
    /// <summary>スクロールが終わった（止まった・指で触れて止めた）。</summary>
    End = 2,
}

/// <summary>
/// スクロールのイベント（<see cref="SEEDScript.OnScrollStart"/> / <see cref="SEEDScript.OnScroll"/> / <see cref="SEEDScript.OnScrollEnd"/>）。
/// 値はスクロールの単位（キャンバスの単位。dp のキャンバスなら dp）。位置 0 は中身の先頭が窓の先頭。
/// </summary>
public readonly struct ScrollEvent
{
    /// <summary>種類。</summary>
    public ScrollEventKind Kind { get; }
    /// <summary>今の位置。</summary>
    public Vector2 Position { get; }
    /// <summary>前に知らせた位置からの差（Update だけ。他は 0）。</summary>
    public Vector2 Delta { get; }
    /// <summary>速度（単位/秒・位置の向き）。</summary>
    public Vector2 Velocity { get; }
    /// <summary>位置の最大（中身 − 窓）。</summary>
    public Vector2 MaxPosition { get; }
    /// <summary>窓の大きさ。</summary>
    public Vector2 ViewportSize { get; }
    /// <summary>中身の大きさ。</summary>
    public Vector2 ContentSize { get; }
    /// <summary>指でドラッグしているか。</summary>
    public bool IsDragging { get; }

    /// <summary>作る（ScriptBridge が Rust のイベントから作る）。</summary>
    public ScrollEvent(
        ScrollEventKind kind, Vector2 position, Vector2 delta, Vector2 velocity,
        Vector2 maxPosition, Vector2 viewportSize, Vector2 contentSize, bool isDragging)
    {
        Kind = kind;
        Position = position;
        Delta = delta;
        Velocity = velocity;
        MaxPosition = maxPosition;
        ViewportSize = viewportSize;
        ContentSize = contentSize;
        IsDragging = isDragging;
    }

    /// <summary>ログ用の文字列。</summary>
    public override string ToString()
        => $"{Kind} pos={Position} delta={Delta} vel={Velocity} max={MaxPosition} viewport={ViewportSize} content={ContentSize}{(IsDragging ? " dragging" : "")}";
}
