namespace SEED;

/// <summary>
/// GameObject の「子を切り抜く」（CanvasClipComponent）へのアクセサ（W2-1a）。
/// Rust ランタイムのコンポーネントを FFI 経由で読み書きする薄いラッパー（値はエンジンが保持）。
///
/// 2D キャンバスのノードに付けると、そのノードのレイアウトの矩形（CanvasComponent があればキャンバス領域、
/// 無ければ最初の有効な Sprite の矩形）で子孫を切り抜く。はみ出した部分は描かれず、ポインタイベントも当たらない。
/// ノード自身の Sprite（背景の板）は切らない。入れ子にすると祖先の矩形との積で切る。
///
/// 切り抜きを持たないエンティティに対する読み取りは既定値、書き込みは無視される。
/// </summary>
public readonly struct CanvasClip : IComponentHandle<CanvasClip>
{
    /// <summary>この CanvasClip が属するエンティティ（スロット entity）。</summary>
    private readonly Entity _entity;

    /// <summary>コンポーネント名（Rust 側レジストリのキーと一致必須）。</summary>
    private const string Comp = "CanvasClip";

    internal CanvasClip(Entity entity) { _entity = entity; }

    // ── IComponentHandle 実装（GetComponent 経由でのみ使われる）──
    static string IComponentHandle<CanvasClip>.ComponentKindName => Comp;
    static CanvasClip IComponentHandle<CanvasClip>.FromEntity(Entity slotEntity) => new(slotEntity);

    /// <summary>
    /// この参照が生存しているか（[SerializeField] 参照フィールド用の生存判定）。
    /// </summary>
    public bool IsValid => ScriptHost.HasComponent(_entity, Comp);

    /// <summary>
    /// 切り抜きが有効か（get/set。既定 true）。false の間は子孫を切り抜かない（描画も当たり判定も）。
    /// 変更は次のフレームの描画から効く。
    /// </summary>
    public bool Enabled
    {
        get => ScriptHost.TryGetBool(_entity, Comp, "enabled", out var b) && b;
        set => ScriptHost.TrySetBool(_entity, Comp, "enabled", value);
    }
}
