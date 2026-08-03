namespace SEED;

/// <summary>
/// GameObject の水位グラフ開口（WaterLinkComponent）へのアクセサ（Phase W2.5）。
/// Rust ランタイムのコンポーネントを FFI 経由で読み書きする薄いラッパー（値はエンジンが保持）。
///
/// 「2 つの水域をつなぐ扉・窓・穴・バルブ」1 個を表す。
/// <b>ゲームプレイ制御の中心は <see cref="Openness"/>（開閉率 0..1）</b>で、
/// これを 0 にすればバルブ全閉＝水は 1 滴も通らず、1 にすれば全開で流れ込む。
/// 水の移動計算そのものはエンジン（水位グラフ）が Play 中に毎フレーム行う。
///
/// 接続先の水域（volume_a / volume_b）は<b>スクリプトから変更できない</b>。
/// 実行中に付け替えると水位グラフの同一性が壊れるためで、
/// 「壁を壊してつながる」演出はコンポーネントのスロットを有効化して表現する。
///
/// 開口を持たないエンティティに対する読み取りは既定値、書き込みは無視される。
/// </summary>
public readonly struct WaterLink : IComponentHandle<WaterLink>
{
    /// <summary>この WaterLink が属するエンティティ（スロット entity）。</summary>
    private readonly Entity _entity;

    /// <summary>コンポーネント名（Rust 側レジストリのキーと一致必須）。</summary>
    private const string Comp = "WaterLink";

    internal WaterLink(Entity entity) { _entity = entity; }

    // ── IComponentHandle 実装（GetComponent 経由でのみ使われる）──
    static string IComponentHandle<WaterLink>.ComponentKindName => Comp;
    static WaterLink IComponentHandle<WaterLink>.FromEntity(Entity slotEntity) => new(slotEntity);

    /// <summary>
    /// この参照が生存しているか（[SerializeField] 参照フィールド用の生存判定）。
    /// </summary>
    public bool IsValid => ScriptHost.HasComponent(_entity, Comp);

    /// <summary>
    /// 開閉率（0..1。get/set）。<b>0 = バルブ全閉（水は通らない）／1 = 全開。</b>
    /// 範囲外の値はエンジン側で 0..1 へ丸められる。
    /// </summary>
    public float Openness
    {
        get => ScriptHost.TryGetFloat(_entity, Comp, "openness", out var v) ? v : 0f;
        set => ScriptHost.TrySetFloat(_entity, Comp, "openness", value);
    }

    /// <summary>開口の幅（m。get/set。負値は 0 に丸められる）。</summary>
    public float OpeningWidth
    {
        get => ScriptHost.TryGetFloat(_entity, Comp, "opening_width", out var v) ? v : 0f;
        set => ScriptHost.TrySetFloat(_entity, Comp, "opening_width", value);
    }

    /// <summary>開口の高さ（m。get/set。負値は 0 に丸められる）。</summary>
    public float OpeningHeight
    {
        get => ScriptHost.TryGetFloat(_entity, Comp, "opening_height", out var v) ? v : 0f;
        set => ScriptHost.TrySetFloat(_entity, Comp, "opening_height", value);
    }

    /// <summary>
    /// 開口の下端 Y（<b>アクタ原点からの相対 m</b>。get/set）。
    /// 低い位置ほど早く水を通す（階段穴が先に地下へ落とす）。
    /// </summary>
    public float OpeningBottom
    {
        get => ScriptHost.TryGetFloat(_entity, Comp, "opening_bottom", out var v) ? v : 0f;
        set => ScriptHost.TrySetFloat(_entity, Comp, "opening_bottom", value);
    }

    /// <summary>
    /// 流量係数（1/s。get/set。負値は 0 に丸められる）。
    /// 大きいほど速く釣り合う（いくら大きくしても水位は発振しない）。
    /// </summary>
    public float FlowCoefficient
    {
        get => ScriptHost.TryGetFloat(_entity, Comp, "flow_coefficient", out var v) ? v : 0f;
        set => ScriptHost.TrySetFloat(_entity, Comp, "flow_coefficient", value);
    }
}
