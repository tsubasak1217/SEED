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

    // ── 表示フラグ ───────────────────────────────────────────

    /// <summary>
    /// アクター自身の属性を表す疑似コンポーネント名（Rust 側レジストリのキーと一致必須）。
    /// ECS コンポーネントではなく Actor ツリーのフラグを読み書きする受け皿。
    /// </summary>
    private const string ActorComp = "GameObject";

    /// <summary>
    /// 表示フラグ（Unity の <c>Renderer.enabled</c> / Godot の <c>visible</c> 相当）。
    ///
    /// false にすると、このアクターと**全子孫**の描画（モデル・スプライト・Text・
    /// SkinnedSprite・パーティクル・ライト・スカイボックス）が止まる。
    /// 描画だけが止まり、スクリプトの Update・アニメーション・物理は動き続ける
    /// （＝「更新ごと止める」非アクティブとは別の概念）。
    /// 非表示のアクターはポインタイベントのヒット判定にも当たらない。
    ///
    /// get が返すのは**自分自身のフラグ**（Unity の activeSelf と同じ流儀）で、
    /// 祖先が非表示でも自分が true なら true を返す。
    /// set はフレーム末尾にエンジンへ反映されるが、同フレーム中の get は設定した値を返す。
    /// </summary>
    public bool Visible
    {
        get => !ScriptHost.TryGetBool(_entity, ActorComp, "visible", out var v) || v;
        set => ScriptHost.TrySetBool(_entity, ActorComp, "visible", value);
    }

    /// <summary>
    /// アクター名（ヒエラルキーに出る名前。<see cref="Find(string)"/> /
    /// <see cref="FindChild(string)"/> が引くキー）。
    ///
    /// get はアクターが無効なら空文字を返す。
    /// set はフレーム末尾にエンジンへ反映されるが、同フレーム中の get は設定した値を返す
    /// （<see cref="Visible"/> と同じ遅延の流儀）。空文字は無視される（名前で引けなくなるため）。
    ///
    /// <para><b>用途は「動的生成したアクタへ一意な名前を付ける」こと。</b></para>
    /// <c>Instantiate</c> で同じプレハブを複数生成すると全て同名になり、
    /// <c>Find</c> / <c>FindChild</c> では区別できない。生成直後に連番名を付けておくと、
    /// 次に同じスクリプトが走ったときに「既にあるものを見つけて使い回す」ことができる
    /// （スクリプトのホットリロードで OnStart が再実行されても二重生成しない）。
    /// この定型は <c>SpawnOnce</c>（assets://common/scripts/UI/SpawnOnce.cs）にまとめてある。
    ///
    /// <para><b>シーンに元からあるアクタの改名には使わないこと。</b></para>
    /// 他アクタが名前で参照している文字列（[SerializeField] のアクタ参照など）は
    /// 追従して書き換わらないため、参照が切れる。既存アクタの改名はエディタで行う。
    /// </summary>
    public string Name
    {
        get => ScriptHost.TryGetString(_entity, ActorComp, "name", out var v) ? v : "";
        set => ScriptHost.TrySetString(_entity, ActorComp, "name", value);
    }

    // ── ジェスチャーの取り消し（W2-3）─────────────────────────

    /// <summary>
    /// この GameObject とその子孫の、ジェスチャーの押下とドラッグを取り消す（一覧の行を使い回す前に呼ぶ）。
    ///
    /// 押している最中の行が別のデータに変わっても、元の押下の Tap が新しい行へ届かないようにする。
    /// 押していたノードには <see cref="SEEDScript.OnGesturePressCancel"/>、ドラッグ中（スワイプ）なら取り消しの
    /// <see cref="SEEDScript.OnGestureDragEnd"/>（<c>Canceled = true</c>）が**次のフレーム**に届く（押下の見た目を戻す）。
    /// 実際の取り消しはフレーム末尾に行われる（<see cref="Visible"/> と同じ遅延の流儀）。
    /// </summary>
    public void CancelGestures() => ScriptHost.TrySetBool(_entity, ActorComp, "cancel_gestures", true);

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
    ///
    /// シーン全体が対象なので、同名アクタ（プレハブを複数並べた場合など）では
    /// 意図しないものを引きうる。自分の配下を探すときは
    /// <see cref="FindChild(string)"/> を使うこと。
    /// </summary>
    public static GameObject Find(string name)
        => ScriptHost.TryFindActor(name, out var e) ? new GameObject(e) : new GameObject(Entity.None);

    /// <summary>
    /// <b>この GameObject の配下</b>から子アクターを検索する
    /// 【プレハブを複数並べても壊れない子参照の入口】。
    ///
    /// <paramref name="nameOrPath"/> は名前かパスを指定する:
    /// <list type="bullet">
    ///   <item><c>"Image"</c> … 直下の子（フォルダノードは透過）。無ければ子孫を DFS</item>
    ///   <item><c>"Body/Head"</c> … 子 Body の子 Head（セグメントごとにたどる）</item>
    ///   <item><c>"./Image"</c> … 先頭の <c>./</c> は付けても同じ意味</item>
    /// </list>
    /// 2D のフォルダノードは階層に存在しないものとして扱う（<c>Items/Image</c> の
    /// Items がフォルダなら <c>"Image"</c> だけで届く）。
    ///
    /// <see cref="Find(string)"/> と違い**シーン全体へは広がらない**ため、
    /// 同じプレハブを複数並べても常に自分の子を返す。
    /// 見つからなければ IsValid=false の GameObject を返す。
    /// </summary>
    /// <param name="nameOrPath">子アクター名、または "/" 区切りの相対パス。</param>
    public GameObject FindChild(string nameOrPath)
        => ScriptHost.TryFindActorFrom(_entity, nameOrPath, subtreeOnly: true, out var e)
            ? new GameObject(e)
            : new GameObject(Entity.None);

    // ── 子の列挙と兄弟の順番（動的ノード API）──────────────────
    //  子は「論理の子」: フォルダノードはそれ自身を数えず、中の子をその位置へ展開する（FindChild・レイアウトと同じ透過）。
    //  並びは木の順そのもの＝CanvasStack 等のレイアウトが並べる順。読みはその場（同じフレームに発行した
    //  Create / Instantiate / SetParent / SetSiblingIndex / Destroy はまだ反映されていない。Parent と同じ）。

    /// <summary>
    /// 論理の子の数（フォルダは透過）。このフレームに Create / Instantiate したばかりのノードは 0（構築はフレーム末尾）。
    /// </summary>
    public int ChildCount => ScriptHost.NodeChildCount(_entity);

    /// <summary>
    /// <paramref name="index"/> 番目の論理の子（0 始まり。並びはレイアウトの並び順）。範囲外なら IsValid=false の GameObject。
    /// </summary>
    /// <param name="index">子の番号。</param>
    public GameObject GetChild(int index)
        => ScriptHost.TryNodeChildAt(_entity, index, out var e) ? new GameObject(e) : new GameObject(Entity.None);

    /// <summary>
    /// 論理の子の一覧（その時点の写し。以後の生成・並べ替えでは変わらない）。子が無ければ空の配列。
    /// </summary>
    public GameObject[] Children
    {
        get
        {
            var entities = ScriptHost.NodeChildren(_entity);
            var result = new GameObject[entities.Length];
            for (int i = 0; i < entities.Length; i++) result[i] = new GameObject(entities[i]);
            return result;
        }
    }

    /// <summary>
    /// 論理の兄弟（同じ論理の親の子。ルートなら同じシーンのルート）の中の順番（0 始まり）。
    /// 分からない（フォルダ・破棄済み・このフレームに作ったばかり）なら -1。
    /// </summary>
    public int SiblingIndex => ScriptHost.NodeSiblingIndex(_entity);

    /// <summary>
    /// 兄弟の中の順番を変える（レイアウトの並び順が変わる）。フレーム末尾に適用される（SetParent と同じ遅延の流儀。
    /// SetParent と同じフレームに呼ぶと、発行した順に当たる）。
    ///
    /// 「取り出した後の兄弟の <paramref name="index"/> 番目の直前」へ入る（兄弟の数以上なら末尾、負なら先頭）。
    /// その兄弟がフォルダの中に居れば、フォルダの中へ入る（論理の親は変わらない）。フォルダ自身は並べ替えられない（[Script] 警告）。
    /// </summary>
    /// <param name="index">新しい順番。</param>
    public void SetSiblingIndex(int index) => ScriptHost.TryNodeSetSibling(_entity, index < 0 ? 0 : index);

    /// <summary>兄弟の先頭へ移す（<see cref="SetSiblingIndex"/>(0)。フレーム末尾に適用）。</summary>
    public void SetAsFirstSibling() => ScriptHost.TryNodeSetSibling(_entity, 0);

    /// <summary>兄弟の末尾へ移す（論理の親の直接の子の末尾。フレーム末尾に適用）。</summary>
    public void SetAsLastSibling() => ScriptHost.TryNodeSetSibling(_entity, ScriptHost.SiblingLast);

    // ── 空のアクタを作る（動的ノード API）──────────────────────

    /// <summary>
    /// 空のアクタを作り、<paramref name="parent"/> の末尾の子にする（省略・無効ならシーンのルート）。
    /// 2D / 3D は親から決める: 2D の親か Canvas を持つ 3D の親の下なら 2D（<see cref="Create2D"/>）、それ以外と親なしは 3D（<see cref="Create3D"/>）。
    ///
    /// <see cref="Instantiate(string)"/> と同じ遅延モデル: ルートは即座に予約され、戻り値へ同じフレームに
    /// Transform / CanvasTransform の値・<see cref="Name"/>・<see cref="Visible"/>・<see cref="AddComponent{T}"/>・
    /// <see cref="SetSiblingIndex"/>・<see cref="SetParent(GameObject)"/> を当てられる（発行した順にフレーム末尾で効く）。
    /// アクタの構築・親への取り付け・ヒエラルキーへの表示はフレーム末尾。<see cref="ChildCount"/> などの読み・
    /// <see cref="CanvasTransform.LayoutSize"/> は次のフレームから。
    /// 親が無効・種別が合わない（3D を 2D の親の下へ等）ならルートへ作られ、ランタイムが [Script] 警告を出す。
    /// 失敗（OnDestroy の中など）なら IsValid=false の GameObject。
    /// </summary>
    /// <param name="name">名前（空なら "Actor"）。</param>
    /// <param name="parent">親（省略でシーンのルート）。</param>
    public static GameObject Create(string name, GameObject parent = default)
        => CreateNode(name, parent, NodeCreateKind.Auto);

    /// <summary>
    /// 空の 2D アクタを作る（CanvasTransform。親相対・pivot 0・anchor 0・大きさ 0＝描くものも大きさも無い）。
    /// 遅延のモデルは <see cref="Create"/> と同じ。2D は 2D の親か Canvas を持つ 3D の親の下にだけ置ける。
    /// </summary>
    /// <param name="name">名前（空なら "Actor"）。</param>
    /// <param name="parent">親（省略でシーンのルート）。</param>
    public static GameObject Create2D(string name, GameObject parent = default)
        => CreateNode(name, parent, NodeCreateKind.TwoD);

    /// <summary>
    /// 空の 3D アクタを作る（Transform。原点・回転なし・等倍）。遅延のモデルは <see cref="Create"/> と同じ。
    /// 3D は 2D の親の下には置けない（ルートへ作られる）。
    /// </summary>
    /// <param name="name">名前（空なら "Actor"）。</param>
    /// <param name="parent">親（省略でシーンのルート）。</param>
    public static GameObject Create3D(string name, GameObject parent = default)
        => CreateNode(name, parent, NodeCreateKind.ThreeD);

    /// <summary>Create 系の共通の本体。</summary>
    private static GameObject CreateNode(string name, GameObject parent, NodeCreateKind kind)
        => ScriptHost.TryNodeCreate(name, parent.Entity, kind, out var e) ? new GameObject(e) : new GameObject(Entity.None);

    // ── コンポーネント・スクリプトの追加と削除（動的ノード API）──────────

    /// <summary>
    /// コンポーネントを既定値で足し（エディタの「コンポーネント追加」と同じ既定値）、そのハンドルを返す。
    /// コンポーネントは<b>その場で</b>作られるので、戻り値へ同じフレームに値を書ける（<c>GetComponent&lt;T&gt;()</c> も
    /// 同じフレームから返す）。スロットの登録（ヒエラルキー・インスペクタ・レイアウト・描画に載る）はフレーム末尾。
    /// 足せない種別（Transform・Model・Camera など。docs の表を参照）・無効な GameObject なら null。
    /// </summary>
    /// <typeparam name="T">コンポーネントのハンドル型（Sprite・Text・CanvasStack・AudioSource など）。</typeparam>
    public T? AddComponent<T>() where T : struct, IComponentHandle<T>
        => ScriptHost.TryNodeAddComponent(_entity, T.ComponentKindName, out var slot) ? T.FromEntity(slot) : null;

    /// <summary>
    /// <paramref name="index"/> 番目（同種の中。<see cref="GetComponent{T}(int)"/> と同じ数え方）のコンポーネントを外す。
    /// 同じフレームの <c>GetComponent&lt;T&gt;()</c> からは即座に消え、後始末（エディタの「コンポーネント削除」と同じ）は
    /// フレーム末尾。Transform / CanvasTransform は外せない。外す予約ができたら true。
    /// </summary>
    /// <typeparam name="T">コンポーネントのハンドル型。</typeparam>
    /// <param name="index">同種の中の番号（既定 0）。</param>
    public bool RemoveComponent<T>(int index = 0) where T : struct, IComponentHandle<T>
        => ScriptHost.TryNodeRemoveComponent(_entity, T.ComponentKindName, index);

    /// <summary>
    /// スクリプトを足す（フレーム末尾にスロットを足して CLR のインスタンスを作り、<b>次のフレーム</b>に OnStart が走る）。
    /// 同じフレームにはインスタンスが無いので、値を渡したいときは足したスクリプトの OnStart で自分から読む
    /// （例: 親の GameObject・名前から引く）。受けたら true（型が見つからない失敗はフレーム末尾の [Script] 警告）。
    /// </summary>
    /// <typeparam name="T">スクリプトの型（SEEDScript の派生）。</typeparam>
    public bool AddScript<T>() where T : SEEDEditor.Scripting.SEEDScript
        => ScriptHost.TryNodeAddScript(_entity, typeof(T).FullName ?? typeof(T).Name);
}
