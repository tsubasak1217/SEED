namespace SEED;

/// <summary>
/// GameObject の「レイアウトの子の側の指定」（CanvasLayoutItemComponent。W2-1b）へのアクセサ。
/// Rust ランタイムのコンポーネントを FFI 経由で読み書きする薄いラッパー（値はエンジンが保持）。
///
/// コンテナ（<see cref="CanvasStack"/>・<see cref="CanvasWrap"/>・<see cref="CanvasGrid"/>）の子に付けて並べられ方を指定する
/// （付けなくても子は自分の大きさ・コンテナの揃えで並ぶ）。大きさはキャンバスの単位（Sprite の幅・高さと同じ）。
/// <see cref="FillWidth"/> / <see cref="FillHeight"/> はコンテナの外の子で使う（親の CanvasComponent の領域いっぱいに広げる）。
/// 変更は次のフレームのレイアウトから効く。指定を持たないエンティティに対する読み取りは既定値、書き込みは無視される。
/// </summary>
public readonly struct CanvasLayoutItem : IComponentHandle<CanvasLayoutItem>
{
    /// <summary>この CanvasLayoutItem が属するエンティティ（スロット entity）。</summary>
    private readonly Entity _entity;

    /// <summary>コンポーネント名（Rust 側レジストリのキーと一致必須）。</summary>
    private const string Comp = "CanvasLayoutItem";

    internal CanvasLayoutItem(Entity entity) { _entity = entity; }

    // ── IComponentHandle 実装（GetComponent 経由でのみ使われる）──
    static string IComponentHandle<CanvasLayoutItem>.ComponentKindName => Comp;
    static CanvasLayoutItem IComponentHandle<CanvasLayoutItem>.FromEntity(Entity slotEntity) => new(slotEntity);

    /// <summary>この参照が生存しているか（[SerializeField] 参照フィールド用の生存判定）。</summary>
    public bool IsValid => ScriptHost.HasComponent(_entity, Comp);

    /// <summary>コンテナに無視させる（get/set。true なら自分の Anchor・Position のまま。重ねる飾りなど）。</summary>
    public bool IgnoreLayout
    {
        get => ScriptHost.TryGetBool(_entity, Comp, "ignore_layout", out var b) && b;
        set => ScriptHost.TrySetBool(_entity, Comp, "ignore_layout", value);
    }

    /// <summary>主軸の余りを分ける重み（get/set。CanvasStack のみ。0 = 伸ばさない）。</summary>
    public float Flex
    {
        get => ScriptHost.TryGetFloat(_entity, Comp, "flex", out var v) ? v : 0f;
        set => ScriptHost.TrySetFloat(_entity, Comp, "flex", value);
    }

    /// <summary>大きさの指定（get/set。0 の軸は中身の大きさ）。</summary>
    public Vector2 PreferredSize
    {
        get => ScriptHost.TryGetVec2(_entity, Comp, "preferred_size", out var v) ? v : Vector2.Zero;
        set => ScriptHost.TrySetVec2(_entity, Comp, "preferred_size", value);
    }

    /// <summary>大きさの下限（get/set。0 = 下限なし）。</summary>
    public Vector2 MinSize
    {
        get => ScriptHost.TryGetVec2(_entity, Comp, "min_size", out var v) ? v : Vector2.Zero;
        set => ScriptHost.TrySetVec2(_entity, Comp, "min_size", value);
    }

    /// <summary>大きさの上限（get/set。0 = 上限なし）。</summary>
    public Vector2 MaxSize
    {
        get => ScriptHost.TryGetVec2(_entity, Comp, "max_size", out var v) ? v : Vector2.Zero;
        set => ScriptHost.TrySetVec2(_entity, Comp, "max_size", value);
    }

    /// <summary>交差軸の揃えの上書き（get/set。既定 Auto = コンテナに従う。Grid ではセルの中の置き方）。</summary>
    public ItemAlign AlignSelf
    {
        get => CanvasLayoutFieldAccess.GetEnum(_entity, Comp, "align_self", ItemAlign.Auto);
        set => CanvasLayoutFieldAccess.SetEnum(_entity, Comp, "align_self", value);
    }

    /// <summary>親の幅に合わせる（get/set。コンテナの外の子で使う。親の CanvasComponent の領域の幅いっぱい）。</summary>
    public bool FillWidth
    {
        get => ScriptHost.TryGetBool(_entity, Comp, "fill_width", out var b) && b;
        set => ScriptHost.TrySetBool(_entity, Comp, "fill_width", value);
    }

    /// <summary>親の高さに合わせる（get/set。同上）。</summary>
    public bool FillHeight
    {
        get => ScriptHost.TryGetBool(_entity, Comp, "fill_height", out var b) && b;
        set => ScriptHost.TrySetBool(_entity, Comp, "fill_height", value);
    }
}
