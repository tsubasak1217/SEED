// ============================================================
//  HierarchyPreviewFlags.cs — HIERARCHY の 1 ノードのプレビューの印
//
//  【ワイヤ形式】（ランタイムの app/actor_utils.rs。docs/editor_screen_preview.md §8）
//  HIERARCHY の各ノードに
//    "preview":bool          … プレビュー（保存されない表示用のアクタ）の部分木の中か（根を含む）
//    "preview_root":bool     … プレビューの根か
//    "preview_source":"..."  … 根の中身のプレハブ（根以外は null）
//  欄が無い旧ランタイムでは false / false / null として読む。
//
//  【WPF 非依存】
//  単体テスト（editor/tests/ScreenPreviewTests）がリンクして試すので WPF 型を使わない。
// ============================================================

using System.Text.Json;

namespace SEEDEditor.Preview;

/// <summary>
/// HIERARCHY の 1 ノードのプレビューの印。
/// </summary>
/// <param name="IsPreview">プレビューの部分木の中か（根を含む）。</param>
/// <param name="IsPreviewRoot">プレビューの根か。</param>
/// <param name="PreviewSource">根の中身のプレハブ（根以外・不明は null）。</param>
public readonly record struct HierarchyPreviewFlags(bool IsPreview, bool IsPreviewRoot, string? PreviewSource)
{
    /// <summary>プレビューの部分木の中かの欄。</summary>
    public const string PreviewProperty = "preview";

    /// <summary>プレビューの根かの欄。</summary>
    public const string PreviewRootProperty = "preview_root";

    /// <summary>根の中身のプレハブの欄。</summary>
    public const string PreviewSourceProperty = "preview_source";

    /// <summary>プレビューの外（旧ランタイム・欄なし）。</summary>
    public static readonly HierarchyPreviewFlags None = new(false, false, null);

    /// <summary>
    /// HIERARCHY の 1 ノードから印を読む。
    /// </summary>
    /// <param name="node">ノードの JSON（オブジェクト）。</param>
    /// <returns>印（欄が無い・型が違うときはプレビューの外）。</returns>
    public static HierarchyPreviewFlags Read(JsonElement node)
    {
        if (node.ValueKind != JsonValueKind.Object) return None;

        var isRoot   = ReadBool(node, PreviewRootProperty);
        // 根は必ず部分木の中（ランタイムも in_preview = 親が中 || 根 で送る。書き損じでも食い違わせない）
        var inside   = ReadBool(node, PreviewProperty) || isRoot;
        var source   = node.TryGetProperty(PreviewSourceProperty, out var s) && s.ValueKind == JsonValueKind.String
            ? s.GetString()
            : null;
        return new HierarchyPreviewFlags(inside, isRoot, isRoot ? source : null);
    }

    /// <summary>真偽の欄を読む（無い・真偽でなければ false）。</summary>
    /// <param name="node">ノードの JSON。</param>
    /// <param name="name">欄の名前。</param>
    /// <returns>欄の値。</returns>
    private static bool ReadBool(JsonElement node, string name) =>
        node.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
}
