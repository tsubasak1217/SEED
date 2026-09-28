namespace SEED;

// ============================================================
//  CanvasGesture.cs — ジェスチャーを受けるノード（CanvasGestureComponent。W2-2）のアクセサ
//
//  【重要】GestureDragAxis の数値は Rust 側 components/canvas_gesture_component.rs の IndexedEnum::ALL の並びと
//  必ず一致させること（FFI では数値で受け渡す）。足すときは末尾へ。
// ============================================================

/// <summary>ドラッグ・フリックの軸（<see cref="CanvasGesture.DragAxis"/>）。</summary>
public enum GestureDragAxis
{
    /// <summary>全方向（押した位置からの距離が slop を超えたら始まる）。</summary>
    Any = 0,
    /// <summary>横だけ（|dx| が slop を超えたら始まる。縦の動きでは始まらない＝親の縦スクロールへ譲る）。</summary>
    Horizontal = 1,
    /// <summary>縦だけ（|dy| が slop を超えたら始まる）。</summary>
    Vertical = 2,
}

/// <summary>
/// GameObject の「ジェスチャーを受けるノード」（CanvasGestureComponent。W2-2）へのアクセサ。
/// Rust ランタイムのコンポーネントを FFI 経由で読み書きする薄いラッパー（値はエンジンが保持）。
///
/// 2D キャンバスのノードに付けると、そのノードが指ごとのジェスチャーアリーナに参加し、
/// <see cref="SEEDScript"/> の OnGestureTap / OnGestureLongPress / OnGestureDragStart・Update・End / OnGestureFling /
/// OnGesturePressDown・Cancel・Up が届く。当たり判定の形は CanvasComponent があればキャンバス領域、無ければ最初の Sprite の矩形で、
/// 見た目が <see cref="MinHitSizeDp"/>（既定 48 dp）より小さければ中心をそろえて広げる。旗をすべて外すと「遮るだけ」のノードになる。
/// 変更は次に触れた指から効く（触れている指はそのときの値のまま）。部品を持たないエンティティに対する読み取りは既定値、書き込みは無視される。
/// </summary>
public readonly struct CanvasGesture : IComponentHandle<CanvasGesture>
{
    /// <summary>この CanvasGesture が属するエンティティ（スロット entity）。</summary>
    private readonly Entity _entity;

    /// <summary>コンポーネント名（Rust 側レジストリのキーと一致必須）。</summary>
    private const string Comp = "CanvasGesture";

    /// <summary>ヒット領域の最小の大きさの既定（dp。Rust 側 DEFAULT_MIN_HIT_SIZE_DP と同じ）。</summary>
    public const float DefaultMinHitSizeDp = 48f;

    internal CanvasGesture(Entity entity) { _entity = entity; }

    // ── IComponentHandle 実装（GetComponent 経由でのみ使われる）──
    static string IComponentHandle<CanvasGesture>.ComponentKindName => Comp;
    static CanvasGesture IComponentHandle<CanvasGesture>.FromEntity(Entity slotEntity) => new(slotEntity);

    /// <summary>この参照が生存しているか（[SerializeField] 参照フィールド用の生存判定）。</summary>
    public bool IsValid => ScriptHost.HasComponent(_entity, Comp);

    /// <summary>アリーナに参加するか（get/set。既定 true）。false の間は当たり判定の候補にもならない（後ろのノードへ届く）。</summary>
    public bool Enabled
    {
        get => ScriptHost.TryGetBool(_entity, Comp, "enabled", out var b) && b;
        set => ScriptHost.TrySetBool(_entity, Comp, "enabled", value);
    }

    /// <summary>タップを受ける（get/set。既定 true）。</summary>
    public bool Tap
    {
        get => ScriptHost.TryGetBool(_entity, Comp, "tap", out var b) && b;
        set => ScriptHost.TrySetBool(_entity, Comp, "tap", value);
    }

    /// <summary>長押しを受ける（get/set。既定 false）。</summary>
    public bool LongPress
    {
        get => ScriptHost.TryGetBool(_entity, Comp, "long_press", out var b) && b;
        set => ScriptHost.TrySetBool(_entity, Comp, "long_press", value);
    }

    /// <summary>ドラッグを受ける（get/set。既定 false）。</summary>
    public bool Drag
    {
        get => ScriptHost.TryGetBool(_entity, Comp, "drag", out var b) && b;
        set => ScriptHost.TrySetBool(_entity, Comp, "drag", value);
    }

    /// <summary>フリックを受ける（get/set。既定 false。ドラッグを受けなくても指を取れる）。</summary>
    public bool Fling
    {
        get => ScriptHost.TryGetBool(_entity, Comp, "fling", out var b) && b;
        set => ScriptHost.TrySetBool(_entity, Comp, "fling", value);
    }

    /// <summary>ドラッグ・フリックの軸（get/set。既定 Any）。</summary>
    public GestureDragAxis DragAxis
    {
        get => CanvasLayoutFieldAccess.GetEnum(_entity, Comp, "drag_axis", GestureDragAxis.Any);
        set => CanvasLayoutFieldAccess.SetEnum(_entity, Comp, "drag_axis", value);
    }

    /// <summary>
    /// ピンチを受ける（get/set。既定 false。W2-8）。同じノードに触れた 2 本の指の間の距離の変化を OnGesturePinch* で受ける
    /// （グラフの拡大縮小）。ピンチが始まると 2 本の指のタップ・長押し・ドラッグは取り消される。外側のノード（縦の一覧など）の
    /// ドラッグに取られている指ではピンチにならない。
    /// </summary>
    public bool Pinch
    {
        get => ScriptHost.TryGetBool(_entity, Comp, "pinch", out var b) && b;
        set => ScriptHost.TrySetBool(_entity, Comp, "pinch", value);
    }

    /// <summary>押下の見た目のイベント（PressDown / PressCancel / PressUp）を受ける（get/set。既定 true）。</summary>
    public bool PressFeedback
    {
        get => ScriptHost.TryGetBool(_entity, Comp, "press_feedback", out var b) && b;
        set => ScriptHost.TrySetBool(_entity, Comp, "press_feedback", value);
    }

    /// <summary>ヒット領域の最小の大きさ（dp。get/set。既定 48。0 = 広げない。負は書けない）。</summary>
    public float MinHitSizeDp
    {
        get => ScriptHost.TryGetFloat(_entity, Comp, "min_hit_size_dp", out var f) ? f : DefaultMinHitSizeDp;
        set => ScriptHost.TrySetFloat(_entity, Comp, "min_hit_size_dp", value);
    }
}
