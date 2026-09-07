// ============================================================
//  AnimHierarchyNav.cs — ヒエラルキー上の祖先探索とアクタパス生成（純ロジック）
//
//  【解決したい問題】
//   アニメーションは「Animator を持つ親アクタ」の .anim が、配下の子アクタの
//   プロパティを actor_path で指して動かす。ところが実際の編集では
//   動かしたい子（腕・エフェクト・UI 部品）を選ぶのが自然で、
//   その子は Animator を持っていない。従来はそこでタイムラインが空になっていた。
//
//   そこで「選択アクタ → 祖先を遡って最も近い Animator 保持アクタ」を
//   編集文脈とし、選択アクタ自身は "キー対象" として覚える。
//
//  【actor_path の作り方】
//   Animator 保持アクタから対象までの**アクタ名を "/" で連結**する。
//   フォルダノードは Rust 側 resolve_actor_path が透過するため
//   （runtime/src/engine/animation/system.rs 参照）パスから除外する。
//   空文字列は「Animator 自身」を意味する。
//
//  本クラスは WPF に依存しない（editor/tests から直接リンクしてテストする）。
// ============================================================

using System;
using System.Collections.Generic;
using System.Text.Json;

namespace SEEDEditor.Panels.AnimationTimeline;

/// <summary>
/// ヒエラルキー 1 ノードの最小表現（アニメーションタイムラインが必要とする情報だけ）。
/// HierarchyPanel.ActorNode とは独立に保つ（WPF 依存を持ち込まないため）。
/// </summary>
/// <param name="Id">DFS ID。</param>
/// <param name="ParentId">親の DFS ID（ルートは null）。</param>
/// <param name="Name">アクタ名（actor_path のセグメントになる）。</param>
/// <param name="IsFolder">フォルダノードか（actor_path から除外される）。</param>
internal sealed record AnimHierarchyNode(int Id, int? ParentId, string Name, bool IsFolder);

/// <summary>ヒエラルキーの祖先探索・相対パス生成を行う純粋なロジッククラス。</summary>
internal static class AnimHierarchyNav
{
    /// <summary>actor_path のセグメント区切り（Rust 側 resolve_actor_path と一致）。</summary>
    public const char PathSeparator = '/';

    // ── HIERARCHY JSON の解析 ───────────────────────────────────

    /// <summary>
    /// ランタイムが送る HIERARCHY JSON（フラット配列）を最小ノード表現へ変換する。
    /// 未知フィールドは無視し、必要な id / parent / name / is_folder だけを読む。
    /// </summary>
    /// <returns>DFS ID をキーとしたノード表。解析失敗時は空の表。</returns>
    public static Dictionary<int, AnimHierarchyNode> ParseHierarchy(string json)
    {
        var map = new Dictionary<int, AnimHierarchyNode>();
        if (string.IsNullOrWhiteSpace(json)) return map;

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return map;

            foreach (var el in doc.RootElement.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.Object) continue;
                if (!el.TryGetProperty("id", out var idEl) || idEl.ValueKind != JsonValueKind.Number)
                    continue;
                var id = idEl.GetInt32();

                int? parent = null;
                if (el.TryGetProperty("parent", out var pEl) && pEl.ValueKind == JsonValueKind.Number)
                    parent = pEl.GetInt32();

                var name = el.TryGetProperty("name", out var nEl) ? nEl.GetString() ?? "" : "";
                var isFolder = el.TryGetProperty("is_folder", out var fEl)
                            && fEl.ValueKind == JsonValueKind.True;

                map[id] = new AnimHierarchyNode(id, parent, name, isFolder);
            }
        }
        catch (JsonException)
        {
            // 壊れた JSON では祖先探索を諦める（例外を上位へ伝播させない）
            return new Dictionary<int, AnimHierarchyNode>();
        }

        return map;
    }

    // ── 祖先探索 ────────────────────────────────────────────────

    /// <summary>
    /// 自分自身を先頭に、ルートまでの祖先 ID を並べて返す。
    /// 親リンクが循環している壊れたデータでも無限ループしないよう、
    /// 訪問済み集合で打ち切る。
    /// </summary>
    public static List<int> SelfAndAncestors(IReadOnlyDictionary<int, AnimHierarchyNode> nodes, int id)
    {
        var chain   = new List<int>();
        var visited = new HashSet<int>();
        var cur     = (int?)id;

        while (cur is not null && nodes.ContainsKey(cur.Value) && visited.Add(cur.Value))
        {
            chain.Add(cur.Value);
            cur = nodes[cur.Value].ParentId;
        }
        return chain;
    }

    /// <summary>
    /// 自分自身から遡って、最初に条件（= Animator を持つ）を満たすアクタを返す。
    /// 見つからなければ null。
    /// </summary>
    /// <param name="nodes">ヒエラルキー表。</param>
    /// <param name="id">起点となる選択アクタの DFS ID。</param>
    /// <param name="hasAnimator">その DFS ID のアクタが Animator を持つか判定する述語。</param>
    public static int? FindNearestAnimator(
        IReadOnlyDictionary<int, AnimHierarchyNode> nodes, int id, Func<int, bool> hasAnimator)
    {
        foreach (var candidate in SelfAndAncestors(nodes, id))
            if (hasAnimator(candidate)) return candidate;
        return null;
    }

    // ── actor_path 生成 ─────────────────────────────────────────

    /// <summary>
    /// Animator 保持アクタ（ancestorId）から対象アクタ（targetId）への相対 actor_path を作る。
    ///
    /// - 同一アクタなら空文字列（= 自分自身）。
    /// - 途中のフォルダノードは除外する（Rust 側がフォルダを透過して解決するため）。
    /// - ancestorId が targetId の祖先でない場合は null（パスを作れない）。
    /// </summary>
    public static string? BuildActorPath(
        IReadOnlyDictionary<int, AnimHierarchyNode> nodes, int ancestorId, int targetId)
    {
        if (ancestorId == targetId) return "";

        var chain = SelfAndAncestors(nodes, targetId);
        var stop  = chain.IndexOf(ancestorId);
        if (stop < 0) return null; // 祖先関係にない

        // chain は [target, ..., ancestor] の順。ancestor を除いた区間を逆順に連結する。
        var segments = new List<string>();
        for (int i = stop - 1; i >= 0; i--)
        {
            var node = nodes[chain[i]];
            if (node.IsFolder) continue;          // フォルダは階層として数えない
            if (node.Name.Length == 0) continue;  // 無名ノードはパスに書けない
            segments.Add(node.Name);
        }
        return string.Join(PathSeparator, segments);
    }
}
