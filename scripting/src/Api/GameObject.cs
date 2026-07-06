namespace SEED;

/// <summary>
/// スクリプトがアタッチされたゲームオブジェクト。所有エンティティを包み、
/// そのコンポーネント（Transform / CanvasTransform / Sprite / Camera）への
/// アクセスを提供する。
///
/// スクリプトからは <c>SEEDScript.gameObject</c> / <c>SEEDScript.transform</c> で得る。
/// アクセサは薄いハンドルなので、対象コンポーネントを持たないエンティティに対する
/// 読み取りは既定値、書き込みは無視される（HasComponent で保持判定できる）。
/// </summary>
public readonly struct GameObject
{
    /// <summary>この GameObject を表すエンティティ。</summary>
    private readonly Entity _entity;

    internal GameObject(Entity entity) { _entity = entity; }

    /// <summary>基になるエンティティ。</summary>
    public Entity Entity => _entity;

    /// <summary>有効なエンティティに束縛されているか。</summary>
    public bool IsValid => _entity.IsValid;

    // ── コンポーネントアクセサ ───────────────────────────────

    /// <summary>この GameObject の 3D Transform。</summary>
    public Transform Transform => new(_entity);

    /// <summary>この GameObject の 2D キャンバストランスフォーム。</summary>
    public CanvasTransform CanvasTransform => new(_entity);

    /// <summary>この GameObject の 2D スプライト。</summary>
    public Sprite Sprite => new(_entity);

    /// <summary>この GameObject の 3D カメラ。</summary>
    public Camera Camera => new(_entity);

    // ── 保持判定 ─────────────────────────────────────────────

    /// <summary>指定名のコンポーネントを持つか（例 "Transform", "Sprite"）。</summary>
    public bool HasComponent(string component) => ScriptHost.HasComponent(_entity, component);
}
