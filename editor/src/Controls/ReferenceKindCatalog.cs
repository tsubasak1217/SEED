using System.Collections.Generic;

namespace SEEDEditor.Controls;

/// <summary>
/// 「参照フィールドが要求する型」の唯一の対応表。
///
/// エディタ内には “シーン内のアクタ／コンポーネントを指す参照フィールド” が複数ある
/// （スクリプトの [SerializeField]・WaterVolume の制御点参照・WaterLink の接続先・
/// Canvas の基準カメラ参照）。それらはすべて「種別名（Kind）」で要求型を表し、
/// この表で以下 3 つを引く:
///
///   1. Kind → ACTOR_COMPONENTS JSON の "type" 文字列（適合コンポーネントの抽出に使う）
///   2. Kind → 画面表示名（ドロップゾーンの説明文・選択ウィンドウの文言に使う）
///   3. Kind がアクタのルート直付け（Transform / CanvasTransform）かスロット格納かの区別
///
/// Rust 側の解決キー（host_api.rs の KIND_*）、C# スクリプトハンドルの ComponentKindName、
/// ACTOR_COMPONENTS が送る type 文字列（component_ops.rs）を橋渡しする唯一の場所であり、
/// 新しい参照型を足すときはここだけを増やせばよい（データドリブン）。
///
/// スクリプト側からの入口は <see cref="SEEDEditor.Scripting.ScriptReferenceCatalog"/>
/// （後方互換のための薄い転送）で、実体はこのクラスである。
/// </summary>
internal static class ReferenceKindCatalog
{
    // ── ルート直付け型の種別名 ───────────────────────────────────
    // アクタのルートに直接生えていてスロットを持たないため、
    // 「持っているか否か」だけを検証して参照を確定する。

    /// <summary>アクタそのものを指す種別名（スクリプトの GameObject 参照）。</summary>
    public const string GameObjectKind = SEED.ScriptReference.GameObjectKind;

    /// <summary>3D アクタのルート Transform を指す種別名。</summary>
    public const string TransformKind = "Transform";

    /// <summary>2D アクタのルート CanvasTransform を指す種別名。</summary>
    public const string CanvasTransformKind = "CanvasTransform";

    // ── スロット格納型の種別名（参照ピッカーから使う定数）─────────

    /// <summary>カメラ（Canvas の基準領域参照が要求する型）。</summary>
    public const string CameraKind = "Camera";

    /// <summary>制御点（WaterVolume の川スプライン参照が要求する型）。</summary>
    public const string ControlPointKind = "ControlPoint";

    /// <summary>水域（WaterLink の接続先 A / B が要求する型）。</summary>
    public const string WaterVolumeKind = "WaterVolume";

    /// <summary>
    /// 種別名 → ACTOR_COMPONENTS JSON の "type" 文字列。
    ///
    /// ここに載っている種別だけが「アクタ内のスロットとして選択できる」。
    /// Transform / CanvasTransform / GameObject はスロットを持たないため意図的に含めない。
    /// </summary>
    private static readonly Dictionary<string, string> SlotComponentTypeByKind = new()
    {
        ["Sprite"]          = "SpriteComponent",
        ["SkinnedSprite"]   = "SkinnedSpriteComponent",
        [CameraKind]        = "CameraComponent",
        ["Audio"]           = "AudioComponent",
        ["Animator"]        = "AnimatorComponent",
        ["ParticleEmitter"] = "ParticleEmitterComponent",
        ["InputMap"]        = "InputMapComponent",
        // 水位グラフ（Phase W2.5）
        [WaterVolumeKind]   = "WaterVolumeComponent",
        ["WaterLink"]       = "WaterLinkComponent",
        // 川スプラインの制御点参照（Phase W4.1）。スクリプト公開はまだ無いが、
        // インスペクタの参照ピッカーが要求型として使うためここに載せる。
        [ControlPointKind]  = "ControlPointComponent",
    };

    /// <summary>
    /// 種別名 → インスペクタに出す日本語まじりの表示名（ドロップゾーンの説明文用）。
    /// 未登録の種別は種別名をそのまま使う。
    /// </summary>
    private static readonly Dictionary<string, string> DisplayNameByKind = new()
    {
        [GameObjectKind]      = "アクター",
        [TransformKind]       = "Transform（3D アクター）",
        [CanvasTransformKind] = "CanvasTransform（2D アクター）",
        ["Sprite"]            = "Sprite",
        ["SkinnedSprite"]     = "SkinnedSprite",
        [CameraKind]          = "Camera",
        ["Audio"]             = "AudioSource",
        ["Animator"]          = "Animator",
        ["ParticleEmitter"]   = "ParticleEmitter",
        ["InputMap"]          = "InputMap",
        // 水位グラフ（Phase W2.5）
        [WaterVolumeKind]     = "WaterVolume（水域）",
        ["WaterLink"]         = "WaterLink（開口・バルブ）",
        [ControlPointKind]    = "ControlPoint（制御点）",
    };

    /// <summary>
    /// この種別がアクタ内の「スロット」に格納されるか（＝スロット選択が必要か）。
    /// false の場合はアクタ名だけで参照が確定する（GameObject / Transform 系）。
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

    /// <summary>
    /// ルート直付け型（Transform / CanvasTransform / GameObject）の要求を
    /// 対象アクタが満たすかを判定する。スロット格納型に対しては常に false を返すので、
    /// 呼び出し側は先に <see cref="NeedsSlotSelection"/> で分岐すること。
    /// </summary>
    /// <param name="kind">要求する種別名。</param>
    /// <param name="hasTransform">対象アクタが 3D の Transform を持つか。</param>
    /// <param name="hasCanvasTransform">対象アクタが 2D の CanvasTransform を持つか。</param>
    public static bool RootAttachedSatisfied(string kind, bool hasTransform, bool hasCanvasTransform)
        => kind switch
        {
            // GameObject はアクタそのものなので、アクタでありさえすれば満たす
            GameObjectKind      => true,
            TransformKind       => hasTransform,
            CanvasTransformKind => hasCanvasTransform,
            _                   => false,
        };
}
