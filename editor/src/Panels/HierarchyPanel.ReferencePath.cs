using System;
using System.Collections.Generic;
using System.Linq;

namespace SEEDEditor.Panels;

/// <summary>
/// HierarchyPanel の「参照フィールドへ保存するアクタ参照文字列」の組み立て。
///
/// 【なぜ必要か】
/// 参照フィールド（スクリプトの <c>[SerializeField]</c> など）はアクタを**文字列**で保存する。
/// 従来は素のアクタ名だけだったため、同じプレハブを複数並べると子アクタ名が重複し、
/// ランタイムのシーン全体 DFS が常に 1 個目のインスタンスの子を返してしまっていた。
///
/// ランタイム側（<c>runtime/src/engine/core/scripting/actor_ref_path.rs</c>）が
/// パス形式（<c>./Child</c> / <c>../Sibling</c> / <c>Root/Child</c>）を解決できるようになったので、
/// エディタは**ドロップされたアクタと参照の持ち主の位置関係を見て、壊れない書式を選んで**保存する。
///
/// 【選択規則】ドロップ先（参照の持ち主）を owner、落とされたアクタを target として:
///   1. target == owner                 → <c>"."</c>
///   2. target が owner の子孫           → <c>"./A/B"</c>（owner からの相対パス）
///   3. 素の名前がシーン内で一意          → 素の名前（従来どおり。読みやすさを優先）
///   4. それ以外                         → <c>"Root/Child/Grand"</c>（ルートからの絶対パス）
///
/// 【フォルダの透過】
/// 2D フォルダノードは論理階層に存在しないため、パスのセグメントから外す
/// （ランタイム側の照合もフォルダ透過なので、書いても書かなくても解決できるが、
///  フォルダの出し入れでパスが壊れないよう「書かない」側に寄せる）。
/// ただし target 自身がフォルダの場合はその名前を落とすと参照先が消えるため残す。
/// </summary>
public partial class HierarchyPanel
{
    /// <summary>パスのセグメント区切り（ランタイム側 actor_ref_path.rs と一致させること）。</summary>
    private const string ReferencePathSeparator = "/";

    /// <summary>「自分自身」を表す参照文字列。</summary>
    private const string ReferenceSelfPath = ".";

    /// <summary>自分のサブツリーを表す接頭辞。</summary>
    private const string ReferenceSelfPrefix = "./";

    /// <summary>
    /// 参照フィールドへ保存するアクタ参照文字列を組み立てる
    /// 【参照値の書式決定の唯一の場所】。
    /// </summary>
    /// <param name="ownerDfsId">参照を持つ側（インスペクタで選択中）のアクタ DFS ID。</param>
    /// <param name="targetDfsId">参照先（ドロップされた）アクタの DFS ID。</param>
    /// <returns>
    /// 保存する参照文字列。対象ノードが見つからない場合は null
    /// （呼び出し側は従来どおりアクタ名へフォールバックする）。
    /// </returns>
    public string? BuildActorReferencePath(int ownerDfsId, int targetDfsId)
    {
        var target = FindNode(_roots, targetDfsId);
        if (target is null) return null;

        // ── ① / ② 参照の持ち主から見た相対パス ──
        var owner = FindNode(_roots, ownerDfsId);
        if (owner is not null)
        {
            if (owner.Id == target.Id) return ReferenceSelfPath;

            var relative = RelativeSegments(owner, target);
            if (relative is not null)
                return ReferenceSelfPrefix + string.Join(ReferencePathSeparator, relative);
        }

        // ── ③ 名前がシーン内で一意なら素の名前（既存シーンと同じ見た目を保つ）──
        if (CountNodesNamed(target.Name) == 1) return target.Name;

        // ── ④ ルートからの絶対パス ──
        var absolute = AbsoluteSegments(target);
        return absolute.Count == 0 ? target.Name : string.Join(ReferencePathSeparator, absolute);
    }

    /// <summary>
    /// owner から target までの相対セグメント列を返す（target が owner の子孫でなければ null）。
    /// フォルダノードは中間セグメントから除外する（末端の target 自身は必ず残す）。
    /// </summary>
    /// <param name="owner">基準ノード。</param>
    /// <param name="target">参照先ノード。</param>
    private List<string>? RelativeSegments(ActorNode owner, ActorNode target)
    {
        var segments = new List<string>();
        var cur = target;

        // target → 親 …… と登り、owner に到達したら子孫だったことが確定する
        while (cur is not null)
        {
            if (cur.Id == owner.Id)
            {
                segments.Reverse();
                return segments;
            }
            // 末端（target 自身）は必ず残し、中間のフォルダだけ落とす
            if (!cur.IsFolder || cur.Id == target.Id) segments.Add(cur.Name);
            cur = cur.ParentId is { } pid ? FindNode(_roots, pid) : null;
        }
        return null;
    }

    /// <summary>
    /// ルートから target までの絶対セグメント列を返す。
    /// フォルダノードは除外する（末端の target 自身は必ず残す）。
    /// </summary>
    /// <param name="target">参照先ノード。</param>
    private List<string> AbsoluteSegments(ActorNode target)
    {
        var segments = new List<string>();
        var cur = target;
        while (cur is not null)
        {
            if (!cur.IsFolder || cur.Id == target.Id) segments.Add(cur.Name);
            cur = cur.ParentId is { } pid ? FindNode(_roots, pid) : null;
        }
        segments.Reverse();
        return segments;
    }

    /// <summary>
    /// 名前またはパスでアクタを探し、その DFS ID を返す（見つからなければ null）。
    ///
    /// 解決規則はランタイム側 <c>actor_ref_path.rs</c> の絶対パス／素の名前に揃える:
    /// <list type="bullet">
    ///   <item><c>"Root/Child/Grand"</c> … シーンのルートからの絶対パス（フォルダは透過）</item>
    ///   <item><c>"Name"</c> … ヒエラルキーの DFS 順で最初の一致</item>
    /// </list>
    /// ただし検索用途では、先頭セグメントがルートに無いときシーン全体 DFS へ緩和する
    /// （<c>"ZukanPageArea/ZukanCard2"</c> のように途中から書いたパスも通る）。
    /// <list type="bullet">
    /// </list>
    /// MCP の <c>seed_find_actor</c>（名前しか知らない AI が DFS ID を引く窓口）が使う。
    /// </summary>
    /// <param name="nameOrPath">アクタ名、または "/" 区切りの絶対パス。</param>
    public int? ActorDfsIdByPath(string nameOrPath)
    {
        if (string.IsNullOrWhiteSpace(nameOrPath)) return null;
        var text = nameOrPath.Trim();

        // 素の名前: 従来どおり DFS 順の最初の一致
        if (!text.Contains(ReferencePathSeparator, StringComparison.Ordinal))
            return ActorDfsIdByName(text);

        // 絶対パス: セグメントごとに子をたどる（フォルダノードは透過）
        var segments = text.Split(ReferencePathSeparator[0],
                                  StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0) return null;

        // 先頭セグメントはまずルート直下（フォルダ透過）で探し、無ければシーン全体 DFS で拾う。
        // 「ZukanPageArea/ZukanCard2」のように途中から書いたパスも通るようにするための緩和で、
        // 参照フィールドの保存書式（ルート起点の厳密なパス）とは別の、検索専用の規則である。
        var current = FindAmong(_roots, segments[0])
                   ?? GetAllNodes(_roots).FirstOrDefault(n => n.Name == segments[0]);
        for (int i = 1; current is not null && i < segments.Length; i++)
            current = FindAmong(current.Children, segments[i]);

        return current?.Id;
    }

    /// <summary>
    /// ノード一覧から名前一致の子を探す（フォルダノードを透過する）。
    /// ① 直下に同名があればそれ ② 無ければフォルダ配下を再帰的に探す。
    /// </summary>
    /// <param name="nodes">探索対象のノード一覧（ルート配列か、あるアクタの子一覧）。</param>
    /// <param name="name">探すアクタ名。</param>
    private static ActorNode? FindAmong(List<ActorNode> nodes, string name)
    {
        var direct = nodes.FirstOrDefault(n => n.Name == name);
        if (direct is not null) return direct;

        foreach (var folder in nodes.Where(n => n.IsFolder))
        {
            var hit = FindAmong(folder.Children, name);
            if (hit is not null) return hit;
        }
        return null;
    }

    /// <summary>指定名のアクタがシーン内に何個あるかを数える（同名判定用）。</summary>
    /// <param name="name">アクタ名。</param>
    private int CountNodesNamed(string name)
        => GetAllNodes(_roots).Count(n => n.Name == name);
}
