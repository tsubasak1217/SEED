using System;
using System.Collections.Generic;
using System.IO;

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

    // ── ユーザースクリプト参照 ───────────────────────────────────
    //
    // 種別名は "Script:型名"（SEED.ScriptReference.ScriptKindPrefix）。
    // 表に載せず接頭辞で動的に判定するため、スクリプトを増やしても表の更新は不要
    // （データドリブン: .cs を足すだけで参照フィールドの要求型になる）。

    /// <summary>ScriptComponent スロットの ACTOR_COMPONENTS "type" 文字列。</summary>
    public const string ScriptComponentTypeId = "ScriptComponent";

    /// <summary>種別名がユーザースクリプト参照か（"Script:PlayerMove" など）。</summary>
    public static bool IsScriptKind(string kind) => SEED.ScriptReference.IsScriptKind(kind);

    /// <summary>スクリプト参照の種別名から要求スクリプト型名を取り出す（それ以外は null）。</summary>
    public static string? ScriptTypeName(string kind) => SEED.ScriptReference.ScriptTypeNameOf(kind);

    /// <summary>
    /// コンポーネントスロット 1 件が、参照フィールドの要求種別を満たすか。
    ///
    /// エディタ内で「適合スロットか」を判断する唯一の場所。
    ///   ・スクリプト参照 … ScriptComponent かつ .cs のファイル名語幹が型名と一致
    ///     （Rust 側 <c>resolve_script_instance</c> の照合規則と完全に同じにすること）
    ///   ・それ以外       … ACTOR_COMPONENTS の "type" 文字列が一致
    /// </summary>
    public static bool Matches(ActorComponentEntry entry, string kind)
    {
        if (ScriptTypeName(kind) is { } scriptType)
            return entry.TypeId == ScriptComponentTypeId
                && string.Equals(SafeFileStem(entry.ScriptPath), scriptType, StringComparison.Ordinal);

        return SlotComponentType(kind) is { } typeId && entry.TypeId == typeId;
    }

    /// <summary>
    /// 参照値として保存するスロット名を決める。
    ///
    /// スクリプト参照でスロット名が空のときだけ null（＝型名だけで先頭スロットへ解決）を返す。
    /// 空名スロットに対して代替表示名（"PlayerMove[0]"）を保存すると、
    /// ランタイム側の名前一致で解決できなくなるためである。
    /// </summary>
    public static string? SlotNameToSave(ActorComponentEntry entry, string kind)
    {
        if (IsScriptKind(kind) && string.IsNullOrEmpty(entry.Name)) return null;
        return ActorComponentSnapshot.SlotDisplayName(entry, SlotFallbackLabel(kind));
    }

    /// <summary>スロット名が空のときの代替表示名に使うラベル（スクリプトは型名）。</summary>
    private static string SlotFallbackLabel(string kind) => ScriptTypeName(kind) ?? kind;

    /// <summary>パスからファイル名の語幹を取り出す（不正パスでも例外を投げない）。</summary>
    private static string SafeFileStem(string path)
    {
        if (string.IsNullOrEmpty(path)) return "";
        try { return Path.GetFileNameWithoutExtension(path); }
        catch { return ""; }
    }

    /// <summary>
    /// 種別名 → ACTOR_COMPONENTS JSON の "type" 文字列。
    ///
    /// ここに載っている種別だけが「アクタ内のスロットとして選択できる」。
    /// Transform / CanvasTransform / GameObject はスロットを持たないため意図的に含めない。
    /// </summary>
    private static readonly Dictionary<string, string> SlotComponentTypeByKind = new()
    {
        // 3D モデル（描画オフセットのスクリプト公開に伴い追加）
        ["Model"]           = "ModelComponent",
        ["Sprite"]          = "SpriteComponent",
        ["SkinnedSprite"]   = "SkinnedSpriteComponent",
        [CameraKind]        = "CameraComponent",
        ["Audio"]           = "AudioComponent",
        ["LineRenderer"]    = "LineRendererComponent",
        ["Skybox"]          = "SkyboxComponent",
        ["Text"]            = "TextComponent",
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
        ["Model"]             = "Model（3D モデル）",
        ["Sprite"]            = "Sprite",
        ["SkinnedSprite"]     = "SkinnedSprite",
        [CameraKind]          = "Camera",
        ["Audio"]             = "AudioSource",
        ["LineRenderer"]      = "LineRenderer",
        ["Skybox"]            = "Skybox",
        ["Text"]              = "Text",
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
    public static bool NeedsSlotSelection(string kind)
        => IsScriptKind(kind) || SlotComponentTypeByKind.ContainsKey(kind);

    /// <summary>
    /// 種別に対応する ACTOR_COMPONENTS の "type" 文字列。スロット型でなければ null。
    /// </summary>
    public static string? SlotComponentType(string kind)
        => IsScriptKind(kind)
            ? ScriptComponentTypeId
            : SlotComponentTypeByKind.TryGetValue(kind, out var t) ? t : null;

    /// <summary>種別の表示名（未登録なら種別名そのもの）。</summary>
    public static string DisplayName(string kind)
    {
        // スクリプト参照は表に載らない（型名から動的に作る）
        if (ScriptTypeName(kind) is { } scriptType) return $"{scriptType}（スクリプト）";
        return DisplayNameByKind.TryGetValue(kind, out var n) ? n : kind;
    }

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
