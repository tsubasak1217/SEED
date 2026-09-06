namespace SEED;

/// <summary>
/// スクリプトがアタッチされたゲームオブジェクト。所有エンティティを包み、
/// そのコンポーネント（Transform / CanvasTransform / Sprite / Camera / AudioSource /
/// Animator / ParticleEmitter / InputMap …）への <c>GetComponent&lt;T&gt;()</c> アクセスを提供する。
///
/// スクリプトからは <c>SEEDScript.gameObject</c> / <c>SEEDScript.transform</c> で得る。
/// <c>GetComponent&lt;T&gt;()</c> は未アタッチなら null を返す（Nullable&lt;T&gt;）ので、
/// <c>if (go.GetComponent&lt;Camera&gt;() is { } cam) { ... }</c> のように保持判定できる。
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

    // ── コンポーネントアクセサ（GetComponent<T>）──────────────
    //  対象コンポーネントのハンドル（Transform / Camera / AudioSource / Animator /
    //  Sprite / CanvasTransform / ParticleEmitter / InputMap …）を型引数で取得する。
    //  未アタッチ・該当なしは null（Nullable<T>）。
    //    if (go.GetComponent<InputMap>() is { } im) { ... }
    //  SEED のアクターは同種コンポーネントを複数スロット持て、スロットには名前がある。

    /// <summary>0 番目のスロットのコンポーネントを取得する。未アタッチなら null。</summary>
    public T? GetComponent<T>() where T : struct, IComponentHandle<T>
        => GetComponent<T>(0);

    /// <summary>index 番目のスロットのコンポーネントを取得する。該当なしは null。</summary>
    public T? GetComponent<T>(int index) where T : struct, IComponentHandle<T>
        => ScriptHost.TryResolveComponentSlot(_entity, T.ComponentKindName, null, index, out var slot)
            ? T.FromEntity(slot)
            : null;

    /// <summary>スロット名一致のコンポーネントを取得する。該当なしは null。</summary>
    public T? GetComponent<T>(string name) where T : struct, IComponentHandle<T>
        => ScriptHost.TryResolveComponentSlot(_entity, T.ComponentKindName, name, -1, out var slot)
            ? T.FromEntity(slot)
            : null;

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
    /// .actor ファイルからアクターを生成し、<paramref name="parent"/> の
    /// 「末尾の子」として配置する（assets:// 仮想パス）。
    /// 遅延モデルは <see cref="Instantiate(string)"/> と同じで、戻り値の GameObject には
    /// 同フレーム中に Transform.Position 等を設定できる。
    ///
    /// 親が破棄済み・シーンに無い・2D/3D の種別が不整合（3D を Canvas 無しの 2D 親の下へ等）の
    /// 場合はルート直下へ生成され、ランタイムが [Script] 警告を出す（生成自体は成功する）。
    ///
    /// 注意: 2D アクター（Actor2D）の場合は構築時に Transform が CanvasTransform へ
    /// 差し替わるため、位置は翌フレーム以降に CanvasTransform.Position で設定する。
    /// </summary>
    public static GameObject Instantiate(string actorPath, GameObject parent)
        => ScriptHost.TryInstantiateUnder(actorPath, parent.Entity, out var e)
            ? new GameObject(e)
            : new GameObject(Entity.None);

    // ── 階層操作（親子付け替え）────────────────────────────

    /// <summary>
    /// このアクターの親を付け替える（新しい親の末尾の子になる）。
    /// 実際の移動はフレーム末尾に遅延適用される（Destroy と同じモデルで、
    /// Instantiate / Destroy とは発行順に処理される）。
    ///
    /// 次の場合は拒否され、ツリーは変化しない（[Script] 警告のみ）:
    /// 自分自身または自分の子孫を親に指定した／対象か親がシーンに存在しない／
    /// 3D アクターを 2D アクターの子にした／2D アクターを Canvas を持たない 3D の子にした。
    ///
    /// 変換: 3D は Transform をワールド空間で保持するためワールド位置が保たれる。
    /// 2D は CanvasTransform が親相対のためローカル値がそのまま維持される
    /// （＝画面上の位置は新しい親を基準に変わる）。
    /// </summary>
    public void SetParent(GameObject parent) => ScriptHost.TrySetParent(_entity, parent.Entity);

    /// <summary>
    /// このアクターの親を付け替える。null を渡すとシーンのルート（トップレベル）へ移動する。
    /// ルールは <see cref="SetParent(GameObject)"/> と同じ。
    /// </summary>
    public void SetParent(GameObject? parent)
        => ScriptHost.TrySetParent(_entity, parent?.Entity ?? Entity.None);

    /// <summary>
    /// 現在の親アクター。ルート直下なら IsValid=false の GameObject を返す。
    /// 同フレーム中に発行した SetParent はまだ反映されていない（遅延適用のため）。
    /// </summary>
    public GameObject Parent
        => ScriptHost.TryGetParent(_entity, out var e) ? new GameObject(e) : new GameObject(Entity.None);

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
