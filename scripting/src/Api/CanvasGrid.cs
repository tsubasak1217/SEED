namespace SEED;

/// <summary>
/// GameObject の「格子に並べるコンテナ」（CanvasGridComponent。W2-1b）へのアクセサ。
/// Rust ランタイムのコンポーネントを FFI 経由で読み書きする薄いラッパー（値はエンジンが保持）。
///
/// 子を左上から行優先で格子のセルへ並べる（月の 3×4・庭の 8×6 など）。列数は固定（<see cref="Columns"/> ≥ 1）か、
/// セルの最小幅から自動（0）。セルの幅は領域の幅を列数で等分、高さはセルの幅 ÷ <see cref="CellAspectRatio"/>
/// （0 なら行ごとに中身の最大の高さ）。子のセルの中の置き方は <see cref="CellAlign"/>（既定 Stretch = セルいっぱい）。
/// 変更は次のフレームのレイアウトから効く。コンテナを持たないエンティティに対する読み取りは既定値、書き込みは無視される。
/// </summary>
public readonly struct CanvasGrid : IComponentHandle<CanvasGrid>
{
    /// <summary>この CanvasGrid が属するエンティティ（スロット entity）。</summary>
    private readonly Entity _entity;

    /// <summary>コンポーネント名（Rust 側レジストリのキーと一致必須）。</summary>
    private const string Comp = "CanvasGrid";

    /// <summary>既定の列数（Rust 側 CanvasGridComponent の既定と一致）。</summary>
    private const int DefaultColumns = 3;

    internal CanvasGrid(Entity entity) { _entity = entity; }

    // ── IComponentHandle 実装（GetComponent 経由でのみ使われる）──
    static string IComponentHandle<CanvasGrid>.ComponentKindName => Comp;
    static CanvasGrid IComponentHandle<CanvasGrid>.FromEntity(Entity slotEntity) => new(slotEntity);

    /// <summary>この参照が生存しているか（[SerializeField] 参照フィールド用の生存判定）。</summary>
    public bool IsValid => ScriptHost.HasComponent(_entity, Comp);

    /// <summary>並べるか（get/set。既定 true）。</summary>
    public bool Enabled
    {
        get => ScriptHost.TryGetBool(_entity, Comp, "enabled", out var b) && b;
        set => ScriptHost.TrySetBool(_entity, Comp, "enabled", value);
    }

    /// <summary>列数（get/set。1 以上で固定、0 でセルの最小幅から自動。負の値は無視される）。</summary>
    public int Columns
    {
        get => ScriptHost.TryGetFloat(_entity, Comp, "columns", out var v) ? (int)v : DefaultColumns;
        set => ScriptHost.TrySetFloat(_entity, Comp, "columns", value);
    }

    /// <summary>自動の列数に使うセルの最小幅（get/set。キャンバスの単位）。</summary>
    public float CellMinWidth
    {
        get => ScriptHost.TryGetFloat(_entity, Comp, "cell_min_width", out var v) ? v : 0f;
        set => ScriptHost.TrySetFloat(_entity, Comp, "cell_min_width", value);
    }

    /// <summary>セルの縦横比（get/set。幅 ÷ 高さ。1 = 正方形、0 以下 = 行の高さは中身）。</summary>
    public float CellAspectRatio
    {
        get => ScriptHost.TryGetFloat(_entity, Comp, "cell_aspect_ratio", out var v) ? v : 0f;
        set => ScriptHost.TrySetFloat(_entity, Comp, "cell_aspect_ratio", value);
    }

    /// <summary>間隔（get/set。x = 列の間隔、y = 行の間隔。キャンバスの単位）。</summary>
    public Vector2 Spacing
    {
        get => ScriptHost.TryGetVec2(_entity, Comp, "spacing", out var v) ? v : Vector2.Zero;
        set => ScriptHost.TrySetVec2(_entity, Comp, "spacing", value);
    }

    /// <summary>内側の余白（get/set。キャンバスの単位）。</summary>
    public CanvasPadding Padding
    {
        get => CanvasLayoutFieldAccess.GetPadding(_entity, Comp, "padding");
        set => CanvasLayoutFieldAccess.SetPadding(_entity, Comp, "padding", value);
    }

    /// <summary>セルの中の子の置き方（get/set。縦横とも。既定 Stretch = セルいっぱい）。</summary>
    public CrossAlign CellAlign
    {
        get => CanvasLayoutFieldAccess.GetEnum(_entity, Comp, "cell_align", CrossAlign.Stretch);
        set => CanvasLayoutFieldAccess.SetEnum(_entity, Comp, "cell_align", value);
    }

    /// <summary>幅を中身に合わせる（get/set。CanvasComponent を持つコンテナだけ）。</summary>
    public bool FitWidth
    {
        get => ScriptHost.TryGetBool(_entity, Comp, "fit_width", out var b) && b;
        set => ScriptHost.TrySetBool(_entity, Comp, "fit_width", value);
    }

    /// <summary>高さを中身に合わせる（get/set。行の数に合わせて伸びる）。</summary>
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
