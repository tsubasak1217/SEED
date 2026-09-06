// ============================================================
//  AnimPropertyRegistry.cs — アニメーション対象プロパティ表
//
//  トラック追加 UI（component / property ドロップダウン）で選択できる
//  「アニメーション可能なプロパティ」の一覧をハードコードしたレジストリ。
//  component/property の組から value_type（float/vec2/vec3/color/bool）を
//  一意に決定できるようにし、Rust 側 AnimationClip の対応表（アニメーション
//  対応済みプロパティ）と一致させる。
//
//  新しいアニメーション対応プロパティが Rust 側（animation_ops.rs 等）に
//  追加された場合は、ここにも対応するエントリを追加すること。
// ============================================================

using System.Collections.Generic;

namespace SEEDEditor.Panels.AnimationTimeline;

/// <summary>1 プロパティエントリ（表示名 + component/property/value_type の組）。</summary>
internal sealed record AnimPropertyEntry(
    string Component,
    string Property,
    string ValueType,
    string DisplayName);

/// <summary>
/// component/property → value_type のレジストリ。
/// トラック追加ダイアログのドロップダウン内容として使用する。
/// </summary>
internal static class AnimPropertyRegistry
{
    /// <summary>登録済みプロパティ一覧（表示順）。</summary>
    public static readonly IReadOnlyList<AnimPropertyEntry> Entries = new List<AnimPropertyEntry>
    {
        new("actor_transform",  "position", "vec3",  "Transform / 位置"),
        new("actor_transform",  "rotation", "vec3",  "Transform / 回転"),
        new("actor_transform",  "scale",    "vec3",  "Transform / スケール"),
        new("canvas_transform", "position", "vec2",  "CanvasTransform / 位置"),
        new("canvas_transform", "rotation", "float", "CanvasTransform / 回転"),
        new("canvas_transform", "scale",    "vec2",  "CanvasTransform / スケール"),
        new("sprite",           "color",    "color", "Sprite / 色"),
        new("text",             "color",    "color", "Text / 文字色"),
        new("text",             "font_size","float", "Text / フォントサイズ"),
    };

    /// <summary>component/property から value_type を引く。未登録の組は null を返す。</summary>
    public static string? ResolveValueType(string component, string property)
    {
        foreach (var e in Entries)
            if (e.Component == component && e.Property == property)
                return e.ValueType;
        return null;
    }

    /// <summary>value_type ごとのコンポーネント数（float=1, vec2=2, vec3=3, color=4, bool=1(フラグのみ)）。</summary>
    public static int ComponentCount(string valueType) => valueType switch
    {
        "vec2"  => 2,
        "vec3"  => 3,
        "color" => 4,
        _       => 1, // float / bool
    };
}
