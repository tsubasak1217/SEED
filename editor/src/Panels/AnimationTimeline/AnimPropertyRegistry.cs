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

    /// <summary>
    /// トラック追加ドロップダウンの既定選択インデックスを、対象アクタの種別（2D/3D）に合わせて求める。
    ///
    /// 【解決したい問題】
    /// ドロップダウンは常に先頭（"Transform / 位置" = 3D 用）が選ばれた状態で開くため、
    /// 2D アクタを対象にトラックを追加すると種別違いのトラックができてしまい、
    /// あとで I キーを押した時点で初めて KindMismatch エラーに気付く（実際に起きた不具合）。
    /// 呼び出し側（AnimationTimelinePanel）はキー対象の種別が分かるたびにこれを呼び直し、
    /// コンボの既定選択をその場で作り直す。
    /// </summary>
    /// <param name="is2D">対象アクタが 2D（CanvasTransform 系）なら true。</param>
    /// <returns>Entries 内で、対象種別の Position トラックに一致する最初のインデックス。
    /// 一致するものが無ければ 0（先頭）。</returns>
    public static int DefaultIndexFor(bool is2D)
    {
        var wantComponent = is2D ? AnimActorSnapshot.CanvasTransformComponent : AnimActorSnapshot.TransformComponent;
        for (int i = 0; i < Entries.Count; i++)
            if (Entries[i].Component == wantComponent) return i;
        return 0;
    }
}
