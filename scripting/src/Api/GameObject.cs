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

    /// <summary>この GameObject のオーディオソース。</summary>
    public AudioSource AudioSource => new(_entity);

    /// <summary>この GameObject のアニメーター（キーフレームアニメーション再生）。</summary>
    public Animator Animator => new(_entity);

    /// <summary>この GameObject のパーティクルエミッタ（GPU パーティクル放出源）。</summary>
    public ParticleEmitter ParticleEmitter => new(_entity);

    // ── 保持判定 ─────────────────────────────────────────────

    /// <summary>指定名のコンポーネントを持つか（例 "Transform", "Sprite"）。</summary>
    public bool HasComponent(string component) => ScriptHost.HasComponent(_entity, component);

    // ── シーン操作（静的 API）────────────────────────────────

    /// <summary>
    /// .actor ファイルからアクターを生成する（assets:// 仮想パス）。
    /// 戻り値の GameObject には同フレーム中に Transform.Position 等を設定できる
    /// （アクター本体の構築はフレーム末尾に行われ、設定した値が優先される）。
    /// 失敗時は IsValid=false の GameObject を返す。
    ///
    /// 注意: 2D アクター（Actor2D）の場合は構築時に Transform が CanvasTransform へ
    /// 差し替わるため、位置は翌フレーム以降に CanvasTransform.Position で設定する。
    /// </summary>
    public static GameObject Instantiate(string actorPath)
        => ScriptHost.TryInstantiate(actorPath, out var e) ? new GameObject(e) : new GameObject(Entity.None);

    /// <summary>
    /// この GameObject（アクター）をシーンから破棄する。
    /// 実際の破棄はフレーム末尾に行われる（Unity の Destroy と同じ遅延モデル）。
    /// </summary>
    public void Destroy() => ScriptHost.TryDestroy(_entity);

    /// <summary>指定 GameObject を破棄する（<see cref="Destroy()"/> の静的版）。</summary>
    public static void Destroy(GameObject target) => target.Destroy();

    /// <summary>
    /// アクターを名前で検索する（ヒエラルキーの DFS 順で最初の一致）。
    /// 見つからなければ IsValid=false の GameObject を返す。
    /// </summary>
    public static GameObject Find(string name)
        => ScriptHost.TryFindActor(name, out var e) ? new GameObject(e) : new GameObject(Entity.None);
}
