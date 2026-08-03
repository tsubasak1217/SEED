using System.Collections.Generic;

namespace SEEDEditor.Scripting;

/// <summary>
/// スクリプトの参照フィールド種別（SEED.ScriptReference の Kind）と、
/// エディタ側の表現（ACTOR_COMPONENTS の型名・表示名）との対応表。
///
/// 参照先アクターのコンポーネント一覧（GET_ACTOR_COMPONENTS の応答 JSON）から
/// 「その種別に該当するスロット」を抽出するために使う。
/// Rust 側の解決キー（host_api.rs の KIND_*）と C# ハンドルの ComponentKindName、
/// および ACTOR_COMPONENTS が送る type 文字列（component_ops.rs）を橋渡しする唯一の場所。
/// </summary>
internal static class ScriptReferenceCatalog
{
    /// <summary>
    /// コンポーネント種別名 → ACTOR_COMPONENTS JSON の "type" 文字列。
    ///
    /// ここに載っている種別だけが「アクター内のスロットとして選択できる」。
    /// Transform / CanvasTransform はアクターのルートに直付けでスロットを持たないため
    /// 意図的に含めない（GameObject 参照も同様）。
    /// </summary>
    private static readonly Dictionary<string, string> SlotComponentTypeByKind = new()
    {
        ["Sprite"]          = "SpriteComponent",
        ["Camera"]          = "CameraComponent",
        ["Audio"]           = "AudioComponent",
        ["Animator"]        = "AnimatorComponent",
        ["ParticleEmitter"] = "ParticleEmitterComponent",
        ["InputMap"]        = "InputMapComponent",
        // 水位グラフ（Phase W2.5）
        ["WaterVolume"]     = "WaterVolumeComponent",
        ["WaterLink"]       = "WaterLinkComponent",
    };

    /// <summary>
    /// 種別名 → インスペクタに出す日本語まじりの表示名（ドロップゾーンの説明文用）。
    /// 未登録の種別は種別名をそのまま使う。
    /// </summary>
    private static readonly Dictionary<string, string> DisplayNameByKind = new()
    {
        [SEED.ScriptReference.GameObjectKind] = "アクター",
        ["Transform"]                         = "Transform（3D アクター）",
        ["CanvasTransform"]                   = "CanvasTransform（2D アクター）",
        ["Sprite"]                            = "Sprite",
        ["Camera"]                            = "Camera",
        ["Audio"]                             = "AudioSource",
        ["Animator"]                          = "Animator",
        ["ParticleEmitter"]                   = "ParticleEmitter",
        ["InputMap"]                          = "InputMap",
        // 水位グラフ（Phase W2.5）
        ["WaterVolume"]                       = "WaterVolume（水域）",
        ["WaterLink"]                         = "WaterLink（開口・バルブ）",
    };

    /// <summary>
    /// この種別がアクター内の「スロット」に格納されるか（＝スロット選択が必要か）。
    /// false の場合はアクター名だけで参照が確定する（GameObject / Transform 系）。
    /// </summary>
    public static bool NeedsSlotSelection(string kind) => SlotComponentTypeByKind.ContainsKey(kind);

    /// <summary>
    /// 種別に対応する ACTOR_COMPONENTS の "type" 文字列。スロット型でなければ null。
    /// </summary>
    public static string? SlotComponentType(string kind)
        => SlotComponentTypeByKind.TryGetValue(kind, out var t) ? t : null;

    /// <summary>種別の表示名（未登録なら種別名そのもの）。</summary>
    public static string DisplayName(string kind)
        => DisplayNameByKind.TryGetValue(kind, out var n) ? n : kind;
}
