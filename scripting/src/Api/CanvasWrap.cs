namespace SEED;

/// <summary>
/// GameObject の「折り返して並べるコンテナ」（CanvasWrapComponent。W2-1b）へのアクセサ。
/// Rust ランタイムのコンポーネントを FFI 経由で読み書きする薄いラッパー（値はエンジンが保持）。
///
/// 子を主軸（既定 横）へ並べ、領域の幅（縦なら高さ）に収まらなくなったら次の行（列）へ折り返す（チップの並びなど）。
/// 子は自分の大きさのまま。領域の幅が決まっていなければ折り返さない。
/// 変更は次のフレームのレイアウトから効く。コンテナを持たないエンティティに対する読み取りは既定値、書き込みは無視される。
/// </summary>
public readonly struct CanvasWrap : IComponentHandle<CanvasWrap>
{
    /// <summary>この CanvasWrap が属するエンティティ（スロット entity）。</summary>
    private readonly Entity _entity;

    /// <summary>コンポーネント名（Rust 側レジストリのキーと一致必須）。</summary>
    private const string Comp = "CanvasWrap";

    internal CanvasWrap(Entity entity) { _entity = entity; }

    // ── IComponentHandle 実装（GetComponent 経由でのみ使われる）──
    static string IComponentHandle<CanvasWrap>.ComponentKindName => Comp;
    static CanvasWrap IComponentHandle<CanvasWrap>.FromEntity(Entity slotEntity) => new(slotEntity);

    /// <summary>この参照が生存しているか（[SerializeField] 参照フィールド用の生存判定）。</summary>
    public bool IsValid => ScriptHost.HasComponent(_entity, Comp);

    /// <summary>並べるか（get/set。既定 true）。</summary>
    public bool Enabled
    {
        get => ScriptHost.TryGetBool(_entity, Comp, "enabled", out var b) && b;
        set => ScriptHost.TrySetBool(_entity, Comp, "enabled", value);
    }

    /// <summary>主軸の向き（get/set。既定 Horizontal = 左から右へ並べ、下へ折り返す）。</summary>
    public LayoutDirection Direction
    {
        get => CanvasLayoutFieldAccess.GetEnum(_entity, Comp, "direction", LayoutDirection.Horizontal);
        set => CanvasLayoutFieldAccess.SetEnum(_entity, Comp, "direction", value);
    }

    /// <summary>同じ行の中の子の間隔（get/set。キャンバスの単位）。</summary>
    public float Spacing
    {
        get => ScriptHost.TryGetFloat(_entity, Comp, "spacing", out var v) ? v : 0f;
        set => ScriptHost.TrySetFloat(_entity, Comp, "spacing", value);
    }

    /// <summary>行（列）の間隔（get/set。キャンバスの単位）。</summary>
    public float RunSpacing
    {
        get => ScriptHost.TryGetFloat(_entity, Comp, "run_spacing", out var v) ? v : 0f;
        set => ScriptHost.TrySetFloat(_entity, Comp, "run_spacing", value);
    }

    /// <summary>内側の余白（get/set。キャンバスの単位）。</summary>
    public CanvasPadding Padding
    {
        get => CanvasLayoutFieldAccess.GetPadding(_entity, Comp, "padding");
        set => CanvasLayoutFieldAccess.SetPadding(_entity, Comp, "padding", value);
    }

    /// <summary>行の中の主軸の揃え（get/set。既定 Start）。</summary>
    public MainAlign MainAlign
    {
        get => CanvasLayoutFieldAccess.GetEnum(_entity, Comp, "main_align", MainAlign.Start);
        set => CanvasLayoutFieldAccess.SetEnum(_entity, Comp, "main_align", value);
    }

    /// <summary>行の中の交差軸の揃え（get/set。既定 Start。Stretch は行の高さまで伸ばす）。</summary>
    public CrossAlign CrossAlign
    {
        get => CanvasLayoutFieldAccess.GetEnum(_entity, Comp, "cross_align", CrossAlign.Start);
        set => CanvasLayoutFieldAccess.SetEnum(_entity, Comp, "cross_align", value);
    }

    /// <summary>行の塊の交差軸の揃え（get/set。既定 Start。領域の高さが決まっているときに行の塊をどこへ置くか）。</summary>
    public MainAlign RunAlign
    {
        get => CanvasLayoutFieldAccess.GetEnum(_entity, Comp, "run_align", MainAlign.Start);
        set => CanvasLayoutFieldAccess.SetEnum(_entity, Comp, "run_align", value);
    }

    /// <summary>幅を中身に合わせる（get/set。CanvasComponent を持つコンテナだけ）。</summary>
    public bool FitWidth
    {
        get => ScriptHost.TryGetBool(_entity, Comp, "fit_width", out var b) && b;
        set => ScriptHost.TrySetBool(_entity, Comp, "fit_width", value);
    }

    /// <summary>高さを中身に合わせる（get/set。折り返した行の数に合わせて伸びる）。</summary>
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
