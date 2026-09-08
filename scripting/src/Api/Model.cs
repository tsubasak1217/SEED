namespace SEED;

/// <summary>
/// GameObject の 3D モデル（ModelComponent）へのアクセサ。
/// Rust ランタイムのコンポーネントを FFI 経由で読み書きする薄いラッパー（値はエンジンが保持）。
///
/// 現在公開しているのは <b>描画オフセットトランスフォーム</b>（位置・回転・スケール）で、
/// アクターの <see cref="Transform"/> を動かさずに「モデルの見た目だけ」をローカルにずらす補正値。
/// 用途はモデルの原点ズレ補正や、手に持たせた道具（釣り竿など）のグリップ位置合わせ。
///
/// <para><b>描画専用</b>: オフセットは物理コライダー・レイキャスト・Transform には
/// 一切影響しない。当たり判定を動かしたい場合はコライダー側のオフセットを使うこと。</para>
///
/// プロパティへの代入は即座に描画へ反映される。
/// モデルを持たないエンティティに対する読み取りは既定値、書き込みは無視される。
/// </summary>
public readonly struct Model : IComponentHandle<Model>
{
    /// <summary>この Model が属するエンティティ（スロット entity）。</summary>
    private readonly Entity _entity;

    /// <summary>コンポーネント名（Rust 側レジストリのキーと一致必須）。</summary>
    private const string Comp = "Model";

    internal Model(Entity entity) { _entity = entity; }

    // ── IComponentHandle 実装（GetComponent 経由でのみ使われる）──
    static string IComponentHandle<Model>.ComponentKindName => Comp;
    static Model IComponentHandle<Model>.FromEntity(Entity slotEntity) => new(slotEntity);

    /// <summary>
    /// この参照が生存しているか（指すエンティティが実在し Model を保持しているか）。
    /// [SerializeField] の参照フィールドで解決済み／破棄済みを判定するために使う。
    /// </summary>
    public bool IsValid => ScriptHost.HasComponent(_entity, Comp);

    /// <summary>
    /// 描画オフセットの位置（アクターのローカル空間・既定 (0,0,0)）。
    /// アクターが回転していればオフセットも一緒に回る。
    /// </summary>
    public Vector3 OffsetPosition
    {
        get => ScriptHost.TryGetVec3(_entity, Comp, "offset_position", out var v) ? v : Vector3.Zero;
        set => ScriptHost.TrySetVec3(_entity, Comp, "offset_position", value);
    }

    /// <summary>
    /// 描画オフセットの回転（YXZ オイラー角・度・既定 (0,0,0)）。
    /// 回転規約は <see cref="Transform"/>.Rotation と同一。
    /// </summary>
    public Vector3 OffsetRotation
    {
        get => ScriptHost.TryGetVec3(_entity, Comp, "offset_rotation", out var v) ? v : Vector3.Zero;
        set => ScriptHost.TrySetVec3(_entity, Comp, "offset_rotation", value);
    }

    /// <summary>描画オフセットのスケール（既定 (1,1,1)）。</summary>
    public Vector3 OffsetScale
    {
        get => ScriptHost.TryGetVec3(_entity, Comp, "offset_scale", out var v) ? v : Vector3.One;
        set => ScriptHost.TrySetVec3(_entity, Comp, "offset_scale", value);
    }

    // ── モデルローカル境界ボックス（read のみ）──────────────────

    /// <summary>
    /// モデルローカル空間の境界ボックス（AABB）の最小側（<b>読み取り専用</b>）。
    ///
    /// <para><b>どの空間の値か</b>: 読み込んだ 3D モデルの全メッシュ頂点の範囲そのもので、
    /// <see cref="OffsetPosition"/> / <see cref="OffsetRotation"/> / <see cref="OffsetScale"/> も
    /// アクターの <see cref="Transform"/>（位置・回転・スケール）も<b>一切掛かっていない</b>。
    /// 実寸を求めるには呼び出し側で <c>LocalBoundsSize × OffsetScale × Transform.Scale</c> のように
    /// 合成すること。</para>
    ///
    /// <para>スキンメッシュはバインドポーズの寸法を返す（アニメーション変形は反映しない）。</para>
    ///
    /// <para>モデル未ロード・Model 未アタッチのときは <see cref="Vector3.Zero"/> を返す。</para>
    /// </summary>
    public Vector3 LocalBoundsMin
        => ScriptHost.TryGetVec3(_entity, Comp, "bounds_min", out var v) ? v : Vector3.Zero;

    /// <summary>
    /// モデルローカル空間の境界ボックス（AABB）の最大側（<b>読み取り専用</b>）。
    /// 空間・注意点は <see cref="LocalBoundsMin"/> と同じ。未ロード時は <see cref="Vector3.Zero"/>。
    /// </summary>
    public Vector3 LocalBoundsMax
        => ScriptHost.TryGetVec3(_entity, Comp, "bounds_max", out var v) ? v : Vector3.Zero;

    /// <summary>
    /// モデルローカル空間の境界ボックスの各軸の長さ（＝<see cref="LocalBoundsMax"/> − <see cref="LocalBoundsMin"/>）。
    /// 「このモデルは素材として何メートル四方か」を 1 行で得るための利便プロパティ。
    /// 未ロード時は <see cref="Vector3.Zero"/>。
    /// </summary>
    public Vector3 LocalBoundsSize => LocalBoundsMax - LocalBoundsMin;

    /// <summary>
    /// 描画するか（既定 true）。false にするとこのモデルだけが描かれなくなる。
    ///
    /// <para><b>止まらないもの</b>: 非表示でも Transform・親子のワールド行列伝播・
    /// JointAttach のソケット追従・コライダー・スクリプトは通常どおり動き続ける。
    /// したがって「見えないが位置は正しく追従している」オブジェクト
    /// （子アクタがカメラの注視点になっている等）を安全に隠せる。
    /// 座標を画面外へ退避させる旧来の隠し方と違い、追従先が壊れない。</para>
    ///
    /// <para>非表示のあいだは影も落とさず、選択アウトラインも出ない。</para>
    ///
    /// 読み取りに失敗した場合（Model を持たないエンティティ）はコンポーネントの
    /// 既定値と同じ true を返す。
    /// </summary>
    public bool Visible
    {
        get => !ScriptHost.TryGetBool(_entity, Comp, "visible", out var b) || b;
        set => ScriptHost.TrySetBool(_entity, Comp, "visible", value);
    }

    /// <summary>
    /// レイトレーシング（RT）の対象から外すか（既定 false ＝ RT に参加する）。
    ///
    /// <para>true にすると、このモデルは <b>RT 用 BLAS の構築と TLAS への登録</b>から除外される。
    /// 影響するのは RT 経路（レイトレ影・反射・GI・AO・トランスルーセンシー）だけで、
    /// 通常のラスタ描画・ラスタのシャドウマップ・ID ピッキング・アウトラインは一切変わらない。
    /// 画面には従来どおり映るが、他の物体への「映り込み」には現れなくなる。</para>
    ///
    /// <para><b>用途</b>: スキンモデルは 1 体ごとに毎フレーム BLAS を作り直すため、
    /// 大量に存在すると RT の構築コストだけでフレーム時間を食い潰す。
    /// 魚の群れのような「映り込みへの寄与が小さく数が多い」対象を明示的に外すためのフラグ。
    /// 除外されたインスタンスは RT の静止判定（署名）にも含まれないため、
    /// 動き続けても RT の再構築を誘発しない。</para>
    ///
    /// 読み取りに失敗した場合（Model を持たないエンティティ）はコンポーネントの
    /// 既定値と同じ false を返す。
    /// </summary>
    public bool RayTracingExcluded
    {
        get => ScriptHost.TryGetBool(_entity, Comp, "rt_exclude", out var b) && b;
        set => ScriptHost.TrySetBool(_entity, Comp, "rt_exclude", value);
    }
}
