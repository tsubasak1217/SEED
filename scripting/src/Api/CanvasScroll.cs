using System;

namespace SEED;

// ============================================================
//  CanvasScroll.cs — スクロールの領域（CanvasScrollComponent と実行中の状態。W2-3）のアクセサ
//
//  【重要】列挙の数値は Rust 側 components/canvas_scroll_component.rs の IndexedEnum::ALL の並び、
//  ScrollPhase は engine/core/canvas_scroll/state.rs の ScrollPhase と必ず一致させること（FFI では数値で受け渡す）。
//  足すときは末尾へ。規則の正典は docs/ui_scroll_list.md。
// ============================================================

/// <summary>スクロールの向き（<see cref="CanvasScroll.Direction"/>）。</summary>
public enum ScrollDirection
{
    /// <summary>縦だけ（一覧）。</summary>
    Vertical = 0,
    /// <summary>横だけ（帯・ページ送り）。</summary>
    Horizontal = 1,
    /// <summary>縦と横の両方。</summary>
    Both = 2,
}

/// <summary>端での振る舞い（<see cref="CanvasScroll.Edge"/>）。</summary>
public enum ScrollEdge
{
    /// <summary>端を越えて引っぱれ、離すとばねで戻る（Flutter の BouncingScrollPhysics）。</summary>
    Bounce = 0,
    /// <summary>端で止まる（Flutter の ClampingScrollPhysics・Android の ScrollView）。</summary>
    Clamp = 1,
}

/// <summary>指を離したときのスナップ（<see cref="CanvasScroll.Snap"/>）。</summary>
public enum ScrollSnap
{
    /// <summary>スナップしない。</summary>
    None = 0,
    /// <summary>ページ送り（1 回のフリックで最大 1 ページ。ページの長さは <see cref="CanvasScroll.SnapInterval"/>、0 なら窓の長さ）。</summary>
    Page = 1,
    /// <summary>間隔（慣性で止まる位置に最も近い <see cref="CanvasScroll.SnapInterval"/> の倍数へ。時刻ホイール・カルーセル）。</summary>
    Interval = 2,
}

/// <summary>中身の大きさの決め方（<see cref="CanvasScroll.ContentSizeMode"/>）。</summary>
public enum ScrollContentSize
{
    /// <summary>子から測る（子の矩形のいちばん遠い端）。</summary>
    Auto = 0,
    /// <summary><see cref="CanvasScroll.FixedContentSize"/> をそのまま使う（一覧の仮想化）。</summary>
    Fixed = 1,
}

/// <summary>スクロールの今の段階（<see cref="CanvasScroll.Phase"/>）。</summary>
public enum ScrollPhase
{
    /// <summary>止まっている。</summary>
    Idle = 0,
    /// <summary>指でドラッグしている。</summary>
    Dragging = 1,
    /// <summary>指を離した後の慣性・跳ね返り・スナップ。</summary>
    Ballistic = 2,
    /// <summary>ScrollTo で動いている。</summary>
    Animating = 3,
    /// <summary>動いている途中に指で触れて止めた（指を離すまで）。</summary>
    Held = 4,
}

/// <summary>
/// GameObject の「スクロールの領域」（CanvasScrollComponent。W2-3）へのアクセサ。
/// Rust ランタイムのコンポーネントと実行中の状態を FFI 経由で読み書きする薄いラッパー（値はエンジンが保持）。
///
/// 2D キャンバスのノードに付けると、そのノードが「中身（子）をずらして見せる窓」になる。指のドラッグ・離した後の慣性・
/// 端の跳ね返り・スナップ・入れ子（同じ向きは端で外側へ渡す）はエンジンが動かし、<see cref="SEEDScript"/> の
/// OnScrollStart / OnScroll / OnScrollEnd が届く。窓の外を切るには同じノードに CanvasClip を付ける（付けると窓の外の子を
/// 描画と当たり判定から外す）。値はキャンバスの単位（dp のキャンバスなら dp）。位置 0 は中身の先頭が窓の先頭。
/// 部品を持たないエンティティに対する読み取りは既定値、書き込みは無視される。
/// </summary>
public readonly struct CanvasScroll : IComponentHandle<CanvasScroll>
{
    /// <summary>この CanvasScroll が属するエンティティ（スロット entity）。</summary>
    private readonly Entity _entity;

    /// <summary>コンポーネント名（Rust 側レジストリのキーと一致必須）。</summary>
    private const string Comp = "CanvasScroll";

    /// <summary>見える範囲の外として飛ばす判定の余白の既定（Rust 側 DEFAULT_CACHE_EXTENT と同じ。Flutter の defaultCacheExtent）。</summary>
    public const float DefaultCacheExtent = 250f;

    /// <summary><see cref="ScrollTo"/> の時間の既定（秒）。Material の動きの時間 medium2 と同じ 300ms（記憶による。手触りは実機で詰める）。</summary>
    public const float DefaultScrollToDuration = 0.3f;

    /// <summary>scroll_to の要素数（x, y, 秒）。</summary>
    private const int ScrollToLength = 3;

    internal CanvasScroll(Entity entity) { _entity = entity; }

    // ── IComponentHandle 実装（GetComponent 経由でのみ使われる）──
    static string IComponentHandle<CanvasScroll>.ComponentKindName => Comp;
    static CanvasScroll IComponentHandle<CanvasScroll>.FromEntity(Entity slotEntity) => new(slotEntity);

    /// <summary>この参照が生存しているか（[SerializeField] 参照フィールド用の生存判定）。</summary>
    public bool IsValid => ScriptHost.HasComponent(_entity, Comp);

    // ── 設定 ──────────────────────────────────────────────

    /// <summary>スクロールするか（get/set。既定 true）。false の間は位置 0 として置き、指も取らない。</summary>
    public bool Enabled
    {
        get => ScriptHost.TryGetBool(_entity, Comp, "enabled", out var b) && b;
        set => ScriptHost.TrySetBool(_entity, Comp, "enabled", value);
    }

    /// <summary>スクロールの向き（get/set。既定 Vertical）。</summary>
    public ScrollDirection Direction
    {
        get => CanvasLayoutFieldAccess.GetEnum(_entity, Comp, "direction", ScrollDirection.Vertical);
        set => CanvasLayoutFieldAccess.SetEnum(_entity, Comp, "direction", value);
    }

    /// <summary>端での振る舞い（get/set。既定 Bounce）。</summary>
    public ScrollEdge Edge
    {
        get => CanvasLayoutFieldAccess.GetEnum(_entity, Comp, "edge", ScrollEdge.Bounce);
        set => CanvasLayoutFieldAccess.SetEnum(_entity, Comp, "edge", value);
    }

    /// <summary>指を離した後の慣性（get/set。既定 true。false なら離した所で止まる）。</summary>
    public bool Inertia
    {
        get => ScriptHost.TryGetBool(_entity, Comp, "inertia", out var b) && b;
        set => ScriptHost.TrySetBool(_entity, Comp, "inertia", value);
    }

    /// <summary>スナップ（get/set。既定 None）。</summary>
    public ScrollSnap Snap
    {
        get => CanvasLayoutFieldAccess.GetEnum(_entity, Comp, "snap", ScrollSnap.None);
        set => CanvasLayoutFieldAccess.SetEnum(_entity, Comp, "snap", value);
    }

    /// <summary>スナップの長さ（get/set。キャンバスの単位。Page で 0 なら窓の長さ・Interval で 0 ならスナップしない。負は書けない）。</summary>
    public float SnapInterval
    {
        get => ScriptHost.TryGetFloat(_entity, Comp, "snap_interval", out var f) ? f : 0f;
        set => ScriptHost.TrySetFloat(_entity, Comp, "snap_interval", value);
    }

    /// <summary>入れ子: 同じ向きの外側のスクロールへ、端に達した残りのドラッグとフリックを渡す（get/set。既定 true）。</summary>
    public bool HandOffToParent
    {
        get => ScriptHost.TryGetBool(_entity, Comp, "hand_off_to_parent", out var b) && b;
        set => ScriptHost.TrySetBool(_entity, Comp, "hand_off_to_parent", value);
    }

    /// <summary>中身の大きさの決め方（get/set。既定 Auto＝子から測る）。</summary>
    public ScrollContentSize ContentSizeMode
    {
        get => CanvasLayoutFieldAccess.GetEnum(_entity, Comp, "content_size", ScrollContentSize.Auto);
        set => CanvasLayoutFieldAccess.SetEnum(_entity, Comp, "content_size", value);
    }

    /// <summary>Fixed のときの中身の大きさ（get/set。キャンバスの単位。負は書けない）。一覧の仮想化で全体の長さを決める。</summary>
    public Vector2 FixedContentSize
    {
        get => ScriptHost.TryGetVec2(_entity, Comp, "fixed_content_size", out var v) ? v : Vector2.Zero;
        set => ScriptHost.TrySetVec2(_entity, Comp, "fixed_content_size", value);
    }

    /// <summary>見える範囲の外の子を描画と当たり判定から外す（get/set。既定 true。CanvasClip が有効なときだけ効く）。</summary>
    public bool CullOutside
    {
        get => ScriptHost.TryGetBool(_entity, Comp, "cull_outside", out var b) && b;
        set => ScriptHost.TrySetBool(_entity, Comp, "cull_outside", value);
    }

    /// <summary>見える範囲の外として飛ばす判定の余白（get/set。キャンバスの単位。既定 250）。</summary>
    public float CacheExtent
    {
        get => ScriptHost.TryGetFloat(_entity, Comp, "cache_extent", out var f) ? f : DefaultCacheExtent;
        set => ScriptHost.TrySetFloat(_entity, Comp, "cache_extent", value);
    }

    /// <summary>Clamp の慣性の摩擦（get/set。既定 0.015＝Android の ScrollFriction。大きいほど早く止まる）。</summary>
    public float FlingFriction
    {
        get => ScriptHost.TryGetFloat(_entity, Comp, "fling_friction", out var f) ? f : 0f;
        set => ScriptHost.TrySetFloat(_entity, Comp, "fling_friction", value);
    }

    /// <summary>Bounce の慣性の減衰（get/set。既定 0.135＝1 秒で速度が何倍になるか。0 と 1 の間）。</summary>
    public float BounceDrag
    {
        get => ScriptHost.TryGetFloat(_entity, Comp, "bounce_drag", out var f) ? f : 0f;
        set => ScriptHost.TrySetFloat(_entity, Comp, "bounce_drag", value);
    }

    // ── 状態（位置・速度・大きさ）──────────────────────────────

    /// <summary>
    /// 位置（get/set。キャンバスの単位。0 = 中身の先頭が窓の先頭）。書くと動きを止めて**すぐ移す**
    /// （範囲 0〜<see cref="MaxPosition"/> へ収める。同じフレームの描画から新しい位置で置かれる）。
    /// </summary>
    public Vector2 Position
    {
        get => ScriptHost.TryGetVec2(_entity, Comp, "position", out var v) ? v : Vector2.Zero;
        set => ScriptHost.TrySetVec2(_entity, Comp, "position", value);
    }

    /// <summary>速度（単位/秒。位置の向き。ドラッグ中は指の速度・慣性中はシミュレーションの速度）。</summary>
    public Vector2 Velocity => ScriptHost.TryGetVec2(_entity, Comp, "velocity", out var v) ? v : Vector2.Zero;

    /// <summary>今の段階。</summary>
    public ScrollPhase Phase => CanvasLayoutFieldAccess.GetEnum(_entity, Comp, "phase", ScrollPhase.Idle);

    /// <summary>スクロールしているか（ドラッグ・慣性・ScrollTo の間。止まっている・触れて止めた間は false）。</summary>
    public bool IsScrolling => ScriptHost.TryGetBool(_entity, Comp, "is_scrolling", out var b) && b;

    /// <summary>指でドラッグしているか。</summary>
    public bool IsDragging => ScriptHost.TryGetBool(_entity, Comp, "is_dragging", out var b) && b;

    /// <summary>窓と中身の大きさが分かったか（Play の最初の描画の後。分かるまで <see cref="ViewportSize"/> などは 0）。</summary>
    public bool HasMetrics => ScriptHost.TryGetBool(_entity, Comp, "has_metrics", out var b) && b;

    /// <summary>窓の大きさ（キャンバスの単位。前のフレームの描画が測った値）。</summary>
    public Vector2 ViewportSize => ScriptHost.TryGetVec2(_entity, Comp, "viewport_size", out var v) ? v : Vector2.Zero;

    /// <summary>中身の大きさ（キャンバスの単位。Auto なら子から測った値、Fixed なら <see cref="FixedContentSize"/>）。</summary>
    public Vector2 ContentSize => ScriptHost.TryGetVec2(_entity, Comp, "content_extent", out var v) ? v : Vector2.Zero;

    /// <summary>位置の最大（中身 − 窓。0 以上）。</summary>
    public Vector2 MaxPosition => ScriptHost.TryGetVec2(_entity, Comp, "max_position", out var v) ? v : Vector2.Zero;

    /// <summary>
    /// 中身の末尾に足す余白（キャンバスの単位。0 以上。実行中だけで保存しない。W2-6b）。入力欄（SEED.UI.TextField）が
    /// ソフトキーボードを避けるとき、窓のうちキーボードに隠れる分を足して、中身の最後までキーボードの上へスクロールできるようにする。
    /// 次のフレームの描画から <see cref="ContentSize"/>・<see cref="MaxPosition"/> に入る。
    /// </summary>
    public Vector2 EndInset
    {
        get => ScriptHost.TryGetVec2(_entity, Comp, "end_inset", out var v) ? v : Vector2.Zero;
        set => ScriptHost.TrySetVec2(_entity, Comp, "end_inset", value);
    }

    // ── 操作 ──────────────────────────────────────────────

    /// <summary>
    /// 時間をかけて位置へ動かす（Flutter の animateTo と Curves.easeInOut。範囲へ収める）。次のフレームから動き始め、
    /// 動いている間は OnScroll が届き、描画も続く（W2-10a の「動いている」）。指で触れると止まる。
    /// </summary>
    /// <param name="position">目標の位置（キャンバスの単位）。</param>
    /// <param name="duration">時間（秒）。0 以下ならすぐ移す（<see cref="JumpTo"/> と同じ）。</param>
    public bool ScrollTo(Vector2 position, float duration = DefaultScrollToDuration)
    {
        Span<float> buf = stackalloc float[ScrollToLength];
        buf[0] = position.x;
        buf[1] = position.y;
        buf[2] = duration;
        return ScriptHost.TrySetFloats(_entity, Comp, "scroll_to", buf);
    }

    /// <summary>すぐ位置へ移す（<see cref="Position"/> への書き込みと同じ）。</summary>
    public bool JumpTo(Vector2 position) => ScriptHost.TrySetVec2(_entity, Comp, "position", position);
}
