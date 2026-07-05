namespace SEED;

/// <summary>
/// スクリプトがアタッチされたゲームオブジェクト。所有エンティティを包み、
/// そのコンポーネント（現状は Transform）へのアクセスを提供する。
///
/// スクリプトからは <c>SEEDScript.gameObject</c> / <c>SEEDScript.transform</c> で得る。
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

    /// <summary>この GameObject の Transform。</summary>
    public Transform Transform => new(_entity);

    /// <summary>指定名のコンポーネントを持つか（例 "Transform"）。</summary>
    public bool HasComponent(string component) => ScriptHost.HasComponent(_entity, component);
}
