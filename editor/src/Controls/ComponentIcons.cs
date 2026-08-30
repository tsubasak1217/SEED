using System;
using System.Collections.Generic;

namespace SEEDEditor.Controls;

/// <summary>
/// コンポーネント種別 ID（TypeId）からアイコンキーを引く唯一の対応表。
///
/// TypeId は Rust 側 <c>ComponentKind::display_name()</c>
/// （runtime/src/engine/components/mod.rs）が返す文字列と 1:1 に対応する。
/// インスペクタのヘッダー・コンポーネント追加ウィンドウなど、コンポーネント種別を
/// 視覚表現するすべての箇所がここを参照する（各所で switch を複製しない）。
///
/// 新しい ECS コンポーネントを追加したら、この表に 1 行足すこと
/// （手順は .claude/rules/editor-icons.md と add-ecs-component Skill を参照）。
/// </summary>
internal static class ComponentIcons
{
    /// <summary>プラグインコンポーネントの TypeId 接頭辞（"Plugin:{プラグイン名}" 形式）。</summary>
    private const string PluginTypeIdPrefix = "Plugin:";

    /// <summary>表に無い TypeId へ使うフォールバックアイコン。</summary>
    public const string FallbackIconKey = "Icon.Component.Unknown";

    /// <summary>
    /// Transform（アクタの基本情報セクション）のアイコンキー。
    /// これは ComponentKind ではなく全アクタ共通の固定セクションなので個別に公開する。
    /// </summary>
    public const string TransformIconKey = "Icon.Component.Transform";

    private static readonly Dictionary<string, string> IconKeyByTypeId = new(StringComparer.Ordinal)
    {
        // レンダリング
        ["ModelComponent"]              = "Icon.Component.Model",
        ["SkyboxComponent"]             = "Icon.Component.Skybox",
        // 環境
        ["WaterVolumeComponent"]        = "Icon.Component.WaterVolume",
        ["WaterLinkComponent"]          = "Icon.Component.WaterLink",
        ["InteractionSourceComponent"]  = "Icon.Component.InteractionSource",
        ["CoverEmitterComponent"]       = "Icon.Component.CoverEmitter",
        // UI
        ["CanvasComponent"]             = "Icon.Component.Canvas",
        ["SpriteComponent"]             = "Icon.Component.Sprite",
        ["SkinnedSpriteComponent"]      = "Icon.Component.SkinnedSprite",
        // ライト
        ["LightComponent"]              = "Icon.Component.Light",
        ["JointAttachComponent"]        = "Icon.Component.JointAttach",
        // エフェクト
        ["ParticleEmitterComponent"]    = "Icon.Component.ParticleEmitter",
        // ツール
        ["ControlPointComponent"]       = "Icon.Component.ControlPoint",
        // カメラ
        ["CameraComponent"]             = "Icon.Component.Camera",
        // 物理
        ["ColliderComponent"]           = "Icon.Component.Collider",
        ["Collider2dComponent"]         = "Icon.Component.Collider2d",
        // サウンド
        ["AudioComponent"]              = "Icon.Component.Audio",
        // アニメーション
        ["AnimatorComponent"]           = "Icon.Component.Animator",
        // 入力
        ["InputMapComponent"]           = "Icon.Component.InputMap",
        // スクリプト
        ["ScriptComponent"]             = "Icon.Component.Script",
        // 内部管理・動的
        ["PluginComponent"]             = "Icon.Component.Plugin",
        ["TerrainChunkComponent"]       = "Icon.Component.TerrainChunk",
    };

    /// <summary>
    /// TypeId に対応するアイコンキーを返す。未知の種別は
    /// <see cref="FallbackIconKey"/>（汎用の六角形）にフォールバックする。
    /// </summary>
    /// <param name="typeId">コンポーネント種別 ID（例 "LightComponent" / "Plugin:MyPlugin"）。</param>
    public static string GetIconKey(string? typeId)
    {
        if (string.IsNullOrEmpty(typeId)) return FallbackIconKey;

        // 動的プラグインは "Plugin:{名前}" で名前部分が可変なので前方一致で判定する。
        if (typeId.StartsWith(PluginTypeIdPrefix, StringComparison.Ordinal))
            return "Icon.Component.Plugin";

        return IconKeyByTypeId.TryGetValue(typeId, out var key) ? key : FallbackIconKey;
    }
}
