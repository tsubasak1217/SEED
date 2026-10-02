// ============================================================
//  InspectorPreviewInfo.cs — ACTOR_COMPONENTS の「プレビューの中か」の情報
//
//  【ワイヤ形式】（ランタイムの app/editor_preview/wire.rs の inspector_preview_json。docs/editor_screen_preview.md §8）
//  ACTOR_COMPONENTS の根に
//    "editor_preview": null
//                    | {"root_dfs":N,"is_root":bool,"prefab":"..","frame":".."|null}
//  プレビューの外・欄が無い旧ランタイムは null として読む。
//  インスペクタはプレビューの中なら帯（プレビュー・保存されません）を出し、編集の UI を押せなくする
//  （InspectorPanel.Preview.cs）。
//
//  【WPF 非依存】
//  単体テスト（editor/tests/ScreenPreviewTests）がリンクして試すので WPF 型を使わない。
// ============================================================

using System.Text.Json;

namespace SEEDEditor.Preview;

/// <summary>
/// 選んだアクタを含むプレビューの情報。
/// </summary>
/// <param name="RootDfs">そのアクタを含むいちばん近いプレビューの根の DFS 番号。</param>
/// <param name="IsRoot">そのアクタ自身が根か。</param>
/// <param name="Prefab">根の中身のプレハブ。</param>
/// <param name="Frame">根の枠のプレハブ（無ければ null）。</param>
public sealed record InspectorPreviewInfo(int RootDfs, bool IsRoot, string Prefab, string? Frame)
{
    /// <summary>ACTOR_COMPONENTS の根の欄。</summary>
    public const string PropertyName = "editor_preview";

    /// <summary>根の DFS 番号の欄。</summary>
    private const string RootDfsProperty = "root_dfs";

    /// <summary>自身が根かの欄。</summary>
    private const string IsRootProperty = "is_root";

    /// <summary>中身のプレハブの欄。</summary>
    private const string PrefabProperty = "prefab";

    /// <summary>枠のプレハブの欄。</summary>
    private const string FrameProperty = "frame";

    /// <summary>
    /// ACTOR_COMPONENTS の根から読む。
    /// </summary>
    /// <param name="root">ACTOR_COMPONENTS の根（オブジェクト）。</param>
    /// <returns>プレビューの中なら情報、外・欄なし・読めないなら null。</returns>
    public static InspectorPreviewInfo? Read(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        if (!root.TryGetProperty(PropertyName, out var value) || value.ValueKind != JsonValueKind.Object) return null;

        // 根の番号が無い（読めない）なら、消す宛先が決まらないので外として扱う
        if (!value.TryGetProperty(RootDfsProperty, out var dfsEl)
            || dfsEl.ValueKind != JsonValueKind.Number
            || !dfsEl.TryGetInt32(out var rootDfs)
            || rootDfs < 0)
            return null;

        var isRoot = value.TryGetProperty(IsRootProperty, out var r) && r.ValueKind == JsonValueKind.True;
        var prefab = value.TryGetProperty(PrefabProperty, out var p) && p.ValueKind == JsonValueKind.String
            ? p.GetString() ?? ""
            : "";
        var frame = value.TryGetProperty(FrameProperty, out var f) && f.ValueKind == JsonValueKind.String
            ? f.GetString()
            : null;
        return new InspectorPreviewInfo(rootDfs, isRoot, prefab, string.IsNullOrWhiteSpace(frame) ? null : frame);
    }
}
