namespace SEED;

/// <summary>
/// GameObject の「安全領域の部品」（CanvasSafeAreaComponent。W2-1b）へのアクセサ。
/// Rust ランタイムのコンポーネントを FFI 経由で読み書きする薄いラッパー（値はエンジンが保持）。
///
/// CanvasComponent を持つ 2D キャンバスのノード（パネル）に付けると、そのノードのキャンバス領域を
/// <see cref="Screen.SafeArea"/>（カメラの穴・ステータスバー・ジェスチャーバーを避けた内側）の内側へ縮める。
/// 子のアンカー・コンテナの並べ方は縮めた領域を基準にする。辺ごとに適用の有無を選べる。
/// 画面の回転とシステムバーの出し入れ（<c>SEED.Platform.Window.SetSystemBarsVisible</c>）に次のフレームから追従する。
/// PC の Play とエディタでは安全領域は画面全体（縮めない）。Sprite の大きさは変えない（背景は親に置く）。
/// 部品を持たないエンティティに対する読み取りは既定値、書き込みは無視される。
/// </summary>
public readonly struct CanvasSafeArea : IComponentHandle<CanvasSafeArea>
{
    /// <summary>この CanvasSafeArea が属するエンティティ（スロット entity）。</summary>
    private readonly Entity _entity;

    /// <summary>コンポーネント名（Rust 側レジストリのキーと一致必須）。</summary>
    private const string Comp = "CanvasSafeArea";

    internal CanvasSafeArea(Entity entity) { _entity = entity; }

    // ── IComponentHandle 実装（GetComponent 経由でのみ使われる）──
    static string IComponentHandle<CanvasSafeArea>.ComponentKindName => Comp;
    static CanvasSafeArea IComponentHandle<CanvasSafeArea>.FromEntity(Entity slotEntity) => new(slotEntity);

    /// <summary>この参照が生存しているか（[SerializeField] 参照フィールド用の生存判定）。</summary>
    public bool IsValid => ScriptHost.HasComponent(_entity, Comp);

    /// <summary>縮めるか（get/set。既定 true）。</summary>
    public bool Enabled
    {
        get => ScriptHost.TryGetBool(_entity, Comp, "enabled", out var b) && b;
        set => ScriptHost.TrySetBool(_entity, Comp, "enabled", value);
    }

    /// <summary>左の辺を安全領域へ寄せる（get/set。既定 true）。</summary>
    public bool Left
    {
        get => ScriptHost.TryGetBool(_entity, Comp, "left", out var b) && b;
        set => ScriptHost.TrySetBool(_entity, Comp, "left", value);
    }

    /// <summary>上の辺を安全領域へ寄せる（get/set。既定 true。ステータスバー・カメラの穴）。</summary>
    public bool Top
    {
        get => ScriptHost.TryGetBool(_entity, Comp, "top", out var b) && b;
        set => ScriptHost.TrySetBool(_entity, Comp, "top", value);
    }

    /// <summary>右の辺を安全領域へ寄せる（get/set。既定 true）。</summary>
    public bool Right
    {
        get => ScriptHost.TryGetBool(_entity, Comp, "right", out var b) && b;
        set => ScriptHost.TrySetBool(_entity, Comp, "right", value);
    }

    /// <summary>下の辺を安全領域へ寄せる（get/set。既定 true。ナビゲーションバー・ジェスチャーバー）。</summary>
    public bool Bottom
    {
        get => ScriptHost.TryGetBool(_entity, Comp, "bottom", out var b) && b;
        set => ScriptHost.TrySetBool(_entity, Comp, "bottom", value);
    }
}
