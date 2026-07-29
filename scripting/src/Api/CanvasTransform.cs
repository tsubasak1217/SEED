namespace SEED;

/// <summary>
/// GameObject の 2D キャンバストランスフォーム（CanvasTransform）へのアクセサ。
/// Rust ランタイムのコンポーネントを FFI 経由で読み書きする薄いラッパー（値はエンジンが保持）。
///
/// 2D キャンバス上のスプライト等の位置・回転・スケールを制御する（3D の Transform に相当）。
/// プロパティへの代入は即座にゲーム世界へ反映される。
/// </summary>
public readonly struct CanvasTransform : IComponentHandle<CanvasTransform>
{
    /// <summary>この CanvasTransform が属するエンティティ。</summary>
    private readonly Entity _entity;

    /// <summary>コンポーネント名（Rust 側レジストリのキー）。</summary>
    private const string Comp = "CanvasTransform";

    internal CanvasTransform(Entity entity) { _entity = entity; }

    // ── IComponentHandle 実装（GetComponent 経由でのみ使われる）──
    static string IComponentHandle<CanvasTransform>.ComponentKindName => Comp;
    static CanvasTransform IComponentHandle<CanvasTransform>.FromEntity(Entity slotEntity) => new(slotEntity);

    // ── 参照の生存判定 ─────────────────────────────────

    /// <summary>
    /// この参照が生存しているか（指すエンティティが実在し CanvasTransform を保持しているか）。
    ///
    /// [SerializeField] の参照フィールドで「解決できたか／破棄されていないか」を
    /// 判定するために使う。<b>null は「未設定」</b>（Nullable 宣言のみ）を意味し、
    /// <b>IsValid == false は「未解決または破棄済み」</b>を意味する。
    /// World が公開されていない場面（ライフサイクル外）でも false になる。
    /// </summary>
    public bool IsValid => ScriptHost.HasComponent(_entity, Comp);

    /// <summary>XY 平面上の位置（親 Canvas 基準の相対座標・ワールドユニット）。</summary>
    public Vector2 Position
    {
        get => ScriptHost.TryGetVec2(_entity, Comp, "position", out var v) ? v : Vector2.Zero;
        set => ScriptHost.TrySetVec2(_entity, Comp, "position", value);
    }

    /// <summary>Z 軸周りの回転（度）。</summary>
    public float Rotation
    {
        get => ScriptHost.TryGetFloat(_entity, Comp, "rotation", out var v) ? v : 0f;
        set => ScriptHost.TrySetFloat(_entity, Comp, "rotation", value);
    }

    /// <summary>XY スケール。</summary>
    public Vector2 Scale
    {
        get => ScriptHost.TryGetVec2(_entity, Comp, "scale", out var v) ? v : Vector2.One;
        set => ScriptHost.TrySetVec2(_entity, Comp, "scale", value);
    }

    /// <summary>
    /// 回転・スケールの基準点（正規化 [0,1]）。
    /// (0,0)=コンテンツ左上、(0.5,0.5)=中央、(1,1)=右下が position に対応する。
    /// </summary>
    public Vector2 Pivot
    {
        get => ScriptHost.TryGetVec2(_entity, Comp, "pivot", out var v) ? v : Vector2.Zero;
        set => ScriptHost.TrySetVec2(_entity, Comp, "pivot", value);
    }

    /// <summary>
    /// 親 Canvas 内の position 基準点（正規化 [0,1]）。
    /// (0,0)=親 Canvas 左上、(0.5,0.5)=中央、(1,1)=右下。
    /// </summary>
    public Vector2 Anchor
    {
        get => ScriptHost.TryGetVec2(_entity, Comp, "anchor", out var v) ? v : Vector2.Zero;
        set => ScriptHost.TrySetVec2(_entity, Comp, "anchor", value);
    }

    /// <summary>
    /// このアクターのスクリーン座標（ウィンドウ左上原点・ピクセル。get のみ）。
    ///
    /// 親 Canvas のアンカー・スケールモード・親チェーンの回転などをすべて反映した
    /// 最終的な描画位置（ピボット点）を返す。エンジンが描画と同一の座標変換で
    /// フレームごとに計算した値のため、Position（親 Canvas 相対）と異なり
    /// 画面上の絶対位置として扱える。2D アクターでない場合は Zero。
    /// </summary>
    public Vector2 ScreenPosition
        => ScriptHost.TryGetScreenPosition(_entity, out var v) ? v : Vector2.Zero;
}
