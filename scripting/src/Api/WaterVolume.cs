namespace SEED;

/// <summary>
/// GameObject の水域（WaterVolumeComponent）へのアクセサ（Phase W2.5）。
/// Rust ランタイムのコンポーネントを FFI 経由で読み書きする薄いラッパー（値はエンジンが保持）。
///
/// 公開しているのは<b>水位グラフの読み書きに必要な最小限だけ</b>である。
/// 色・波・泡などの見た目パラメータは演出設定であってゲームロジックが触るものではないため、
/// スクリプトへは公開せずインスペクタでの編集に限定している。
///
/// 水域を持たないエンティティに対する読み取りは既定値、書き込みは無視される。
/// </summary>
public readonly struct WaterVolume : IComponentHandle<WaterVolume>
{
    /// <summary>この WaterVolume が属するエンティティ（スロット entity）。</summary>
    private readonly Entity _entity;

    /// <summary>コンポーネント名（Rust 側レジストリのキーと一致必須）。</summary>
    private const string Comp = "WaterVolume";

    internal WaterVolume(Entity entity) { _entity = entity; }

    // ── IComponentHandle 実装（GetComponent 経由でのみ使われる）──
    static string IComponentHandle<WaterVolume>.ComponentKindName => Comp;
    static WaterVolume IComponentHandle<WaterVolume>.FromEntity(Entity slotEntity) => new(slotEntity);

    /// <summary>
    /// この参照が生存しているか（[SerializeField] 参照フィールド用の生存判定）。
    /// </summary>
    public bool IsValid => ScriptHost.HasComponent(_entity, Comp);

    /// <summary>
    /// <b>現在の水面 Y（ワールド絶対値。読み取り専用）。</b>
    ///
    /// 水位グラフが動いていればその現在水位、動いていなければ設定水位を
    /// ワールド Y へ直したものを返す（Play 前後で値の意味が変わらない）。
    /// 「水位が閾値を超えたら扉を閉める」といった判定に使う。
    ///
    /// <b>書き込めないのは意図的</b>である。直接代入できてしまうと体積保存が破れ、
    /// 水位グラフの前提が壊れる。水を足す／抜く演出はリンクの開閉で表現すること。
    /// </summary>
    public float WaterLevel
        => ScriptHost.TryGetFloat(_entity, Comp, "water_level", out var v) ? v : 0f;

    /// <summary>
    /// 設定水位（get/set）。<b>Ocean = ワールド Y 絶対値／Region = アクタ原点からの相対 Y。</b>
    /// 水位グラフが動いている間は、見た目の水面はこの値ではなく
    /// <see cref="WaterLevel"/> の現在水位で決まる。
    /// </summary>
    public float SurfaceHeight
    {
        get => ScriptHost.TryGetFloat(_entity, Comp, "surface_height", out var v) ? v : 0f;
        set => ScriptHost.TrySetFloat(_entity, Comp, "surface_height", value);
    }

    /// <summary>
    /// この水域を水位グラフのノードにするか（get/set）。
    /// <c>false</c> にすると時間発展が止まり、設定水位の静止水面へ戻る。
    /// kind = Region のときだけ効く（Ocean / 川では無視される）。
    /// </summary>
    public bool SimulateLevel
    {
        get => ScriptHost.TryGetBool(_entity, Comp, "simulate_level", out var v) && v;
        set => ScriptHost.TrySetBool(_entity, Comp, "simulate_level", value);
    }
}
