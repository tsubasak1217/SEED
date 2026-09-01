namespace SEED;

/// <summary>
/// GameObject のスカイボックス（SkyboxComponent）へのアクセサ。
/// Rust ランタイムのコンポーネントを FFI 経由で読み書きする薄いラッパー（値はエンジンが保持）。
///
/// equirectangular（正距円筒）画像 1 枚を天球として描く設定と、その**色調整**
/// （色相シフト／彩度／明度／コントラスト）を実行時に動かすための API。
/// 時間帯演出（夕焼けへ色相をずらす・嵐で彩度を落とす）などをスクリプトから駆動できる。
///
/// 【重要】色調整は**背景の空だけでなく、反射・水面反射に映る空にも同時に効く**。
/// エンジン側で全ての空サンプル経路が共通のシェーダ関数を通っているため、
/// 「背景は夕焼けなのに水面の反射は昼のまま」にはならない。
///
/// 値域はエンジン側でクランプされる（色相 -180..180 度、彩度／明度／コントラスト 0..2）。
/// Skybox を持たないエンティティに対する読み取りは既定値、書き込みは無視される。
/// </summary>
public readonly struct Skybox : IComponentHandle<Skybox>
{
    /// <summary>この Skybox が属するエンティティ（スロット entity）。</summary>
    private readonly Entity _entity;

    /// <summary>コンポーネント名（Rust 側レジストリのキーと一致必須）。</summary>
    private const string Comp = "Skybox";

    // ── 読み取り失敗時に返す既定値（ランタイムの Default と一致させる）──

    /// <summary>強度の既定値（1 = 素の色）。</summary>
    private const float DefaultIntensity = 1f;
    /// <summary>色相シフトの既定値（度。0 = 無変換）。</summary>
    private const float DefaultHueShift = 0f;
    /// <summary>彩度／明度／コントラストの既定値（1 = 無変換）。</summary>
    private const float DefaultAdjust = 1f;

    internal Skybox(Entity entity) { _entity = entity; }

    // ── IComponentHandle 実装（GetComponent 経由でのみ使われる）──
    static string IComponentHandle<Skybox>.ComponentKindName => Comp;
    static Skybox IComponentHandle<Skybox>.FromEntity(Entity slotEntity) => new(slotEntity);

    /// <summary>
    /// この参照が生存しているか（指すエンティティが実在し Skybox を保持しているか）。
    /// [SerializeField] 参照フィールドの生存判定に使う。
    /// </summary>
    public bool IsValid => ScriptHost.HasComponent(_entity, Comp);

    // ── テクスチャ・基本パラメータ ─────────────────────

    /// <summary>equirectangular 天球画像の assets:// 仮想パス（空文字＝未設定＝描画しない）。</summary>
    public string TexturePath
    {
        get => ScriptHost.TryGetString(_entity, Comp, "texture_path", out var s) ? s : string.Empty;
        set => ScriptHost.TrySetString(_entity, Comp, "texture_path", value);
    }

    /// <summary>強度（テクスチャ色への乗算。1 = 素の色。1 超で発光的になり Bloom と連動する）。</summary>
    public float Intensity
    {
        get => ScriptHost.TryGetFloat(_entity, Comp, "intensity", out var v) ? v : DefaultIntensity;
        set => ScriptHost.TrySetFloat(_entity, Comp, "intensity", value);
    }

    /// <summary>色味（リニア RGB 乗算。白 = 素通し。アルファは使わない）。</summary>
    public Vector3 Tint
    {
        get => ScriptHost.TryGetVec3(_entity, Comp, "tint", out var v) ? v : Vector3.One;
        set => ScriptHost.TrySetVec3(_entity, Comp, "tint", value);
    }

    // ── 色調整（背景・反射・水面反射へ同時に効く）──────

    /// <summary>色相シフト（度。-180〜180 にクランプ。0 = 無変換）。輝度を保ったまま色相環を回す。</summary>
    public float HueShift
    {
        get => ScriptHost.TryGetFloat(_entity, Comp, "hue_shift", out var v) ? v : DefaultHueShift;
        set => ScriptHost.TrySetFloat(_entity, Comp, "hue_shift", value);
    }

    /// <summary>彩度（0〜2 にクランプ。0 = グレースケール / 1 = 無変換 / 2 = 彩度 2 倍）。</summary>
    public float Saturation
    {
        get => ScriptHost.TryGetFloat(_entity, Comp, "saturation", out var v) ? v : DefaultAdjust;
        set => ScriptHost.TrySetFloat(_entity, Comp, "saturation", value);
    }

    /// <summary>明度（0〜2 にクランプ。色への乗算。1 = 無変換）。</summary>
    public float Brightness
    {
        get => ScriptHost.TryGetFloat(_entity, Comp, "brightness", out var v) ? v : DefaultAdjust;
        set => ScriptHost.TrySetFloat(_entity, Comp, "brightness", value);
    }

    /// <summary>コントラスト（0〜2 にクランプ。中間グレー基準の伸縮。1 = 無変換）。</summary>
    public float Contrast
    {
        get => ScriptHost.TryGetFloat(_entity, Comp, "contrast", out var v) ? v : DefaultAdjust;
        set => ScriptHost.TrySetFloat(_entity, Comp, "contrast", value);
    }
}
