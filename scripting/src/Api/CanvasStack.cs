namespace SEED;

/// <summary>
/// GameObject の「縦・横に並べるコンテナ」（CanvasStackComponent。W2-1b）へのアクセサ。
/// Rust ランタイムのコンポーネントを FFI 経由で読み書きする薄いラッパー（値はエンジンが保持）。
///
/// 2D キャンバスのノードに付けると、子（フォルダの中の子も含む）を縦か横に 1 列に並べる。子の位置はコンテナが決める
/// （子の Anchor・Position は使わない）。子の大きさは自分の大きさ（CanvasComponent → 最初の Sprite → Text の枠 →
/// <see cref="CanvasLayoutItem"/> の指定）か、伸ばす（<see cref="CanvasLayoutItem.Flex"/>・交差軸の <see cref="CrossAlign.Stretch"/>）。
/// 変更は次のフレームのレイアウトから効く。コンテナを持たないエンティティに対する読み取りは既定値、書き込みは無視される。
/// </summary>
public readonly struct CanvasStack : IComponentHandle<CanvasStack>
{
    /// <summary>この CanvasStack が属するエンティティ（スロット entity）。</summary>
    private readonly Entity _entity;

    /// <summary>コンポーネント名（Rust 側レジストリのキーと一致必須）。</summary>
    private const string Comp = "CanvasStack";

    internal CanvasStack(Entity entity) { _entity = entity; }

    // ── IComponentHandle 実装（GetComponent 経由でのみ使われる）──
    static string IComponentHandle<CanvasStack>.ComponentKindName => Comp;
    static CanvasStack IComponentHandle<CanvasStack>.FromEntity(Entity slotEntity) => new(slotEntity);

    /// <summary>この参照が生存しているか（[SerializeField] 参照フィールド用の生存判定）。</summary>
    public bool IsValid => ScriptHost.HasComponent(_entity, Comp);

    /// <summary>並べるか（get/set。既定 true）。false の間、子は自分の Anchor・Position に戻る。</summary>
    public bool Enabled
    {
        get => ScriptHost.TryGetBool(_entity, Comp, "enabled", out var b) && b;
        set => ScriptHost.TrySetBool(_entity, Comp, "enabled", value);
    }

    /// <summary>並べる向き（get/set。既定 Vertical）。</summary>
    public LayoutDirection Direction
    {
        get => CanvasLayoutFieldAccess.GetEnum(_entity, Comp, "direction", LayoutDirection.Vertical);
        set => CanvasLayoutFieldAccess.SetEnum(_entity, Comp, "direction", value);
    }

    /// <summary>子の間隔（get/set。キャンバスの単位）。</summary>
    public float Spacing
    {
        get => ScriptHost.TryGetFloat(_entity, Comp, "spacing", out var v) ? v : 0f;
        set => ScriptHost.TrySetFloat(_entity, Comp, "spacing", value);
    }

    /// <summary>内側の余白（get/set。キャンバスの単位）。</summary>
    public CanvasPadding Padding
    {
        get => CanvasLayoutFieldAccess.GetPadding(_entity, Comp, "padding");
        set => CanvasLayoutFieldAccess.SetPadding(_entity, Comp, "padding", value);
    }

    /// <summary>主軸の揃え（get/set。既定 Start）。</summary>
    public MainAlign MainAlign
    {
        get => CanvasLayoutFieldAccess.GetEnum(_entity, Comp, "main_align", MainAlign.Start);
        set => CanvasLayoutFieldAccess.SetEnum(_entity, Comp, "main_align", value);
    }

    /// <summary>交差軸の揃え（get/set。既定 Start。Stretch は交差軸いっぱいに伸ばす）。</summary>
    public CrossAlign CrossAlign
    {
        get => CanvasLayoutFieldAccess.GetEnum(_entity, Comp, "cross_align", CrossAlign.Start);
        set => CanvasLayoutFieldAccess.SetEnum(_entity, Comp, "cross_align", value);
    }

    /// <summary>逆順に並べる（get/set。最後の子が先頭に来る）。</summary>
    public bool Reverse
    {
        get => ScriptHost.TryGetBool(_entity, Comp, "reverse", out var b) && b;
        set => ScriptHost.TrySetBool(_entity, Comp, "reverse", value);
    }

    /// <summary>幅を中身に合わせる（get/set。CanvasComponent を持つコンテナだけ。持たなければ常に中身に合わせる）。</summary>
    public bool FitWidth
    {
        get => ScriptHost.TryGetBool(_entity, Comp, "fit_width", out var b) && b;
        set => ScriptHost.TrySetBool(_entity, Comp, "fit_width", value);
    }

    /// <summary>高さを中身に合わせる（get/set。同上）。</summary>
    public bool FitHeight
    {
        get => ScriptHost.TryGetBool(_entity, Comp, "fit_height", out var b) && b;
        set => ScriptHost.TrySetBool(_entity, Comp, "fit_height", value);
    }

    /// <summary>非表示・無効の子の扱い（get/set。既定 Collapse = 詰める）。</summary>
    public HiddenChildren HiddenChildren
    {
        get => CanvasLayoutFieldAccess.GetEnum(_entity, Comp, "hidden_children", HiddenChildren.Collapse);
        set => CanvasLayoutFieldAccess.SetEnum(_entity, Comp, "hidden_children", value);
    }
}
