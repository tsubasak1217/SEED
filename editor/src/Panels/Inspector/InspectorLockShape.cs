using System;
using System.Collections.Generic;

namespace SEEDEditor.Panels.Inspector;

/// <summary>ヒエラルキーの 1 ノードのうち、インスペクタのロックの確かめに使う分（番号・名前・親の番号）。</summary>
/// <param name="Id">DFS 番号。</param>
/// <param name="Name">名前。</param>
/// <param name="Parent">親の DFS 番号（ルートは <see cref="InspectorLockShape.NoParent"/>）。</param>
public readonly record struct InspectorLockNode(int Id, string Name, int Parent);

/// <summary>
/// ヒエラルキーが届いたときに、ロックした番号のアクタがまだ同じ名前か（従来の確かめ）と、
/// ロックした番号に入るアクタが変わり得る「木の形」の要約を求める（WPF 非依存の純粋なロジック。2026-10-03 の 2 回目のレビュー #13）。
///
/// <para>
/// 要約が前回と変わったときだけ、ロックした番号の ACTOR_COMPONENTS を取り直して中身を照合する（<see cref="InspectorLockIdentity"/>）。
/// 取り直すたびにインスペクタを描き直すので、Play 中に関係の無いアクタが増減しても取り直さないよう、要約に入れるのは次の 2 つだけ:
///   ・ロックした番号「まで」のノード（番号・名前・親）… 手前の増減・並べ替え・付け替え
///   ・ロックしたアクタと同じ親・同じ名前の兄弟の数 … 同じ名前の兄弟がロックした番号へ入った（削除の Undo など。
///     入ったアクタは番号・名前・親が元と同じに見えるので、手前の形だけでは気づけない）
/// </para>
/// </summary>
public static class InspectorLockShape
{
    /// <summary>親の無い（ルートの）ノードの親の番号（DFS 番号には現れない負の値）。</summary>
    public const int NoParent = -1;

    /// <summary>
    /// ロックした番号のアクタが同じ名前か（<c>NameMatches</c>）と、木の形の要約（<c>Shape</c>）を求める。
    /// </summary>
    /// <param name="nodes">ヒエラルキーの全ノード（届いた順）。</param>
    /// <param name="lockedId">ロックした DFS 番号。</param>
    /// <param name="lockedName">ロックしたときの名前。</param>
    /// <returns>名前が合うか、と木の形の要約（名前が合わないときの要約は使わない）。</returns>
    public static (bool NameMatches, int Shape) Evaluate(IReadOnlyList<InspectorLockNode> nodes, int lockedId, string lockedName)
    {
        bool nameMatches = false;
        int lockedParent = NoParent;
        var shape = new HashCode();

        // ロックした番号までの形（並びは届いた順。同じ木なら同じ順で届く）
        foreach (var n in nodes)
        {
            if (n.Id > lockedId) continue;
            shape.Add(n.Id);
            shape.Add(n.Name, StringComparer.Ordinal);
            shape.Add(n.Parent);
            if (n.Id != lockedId) continue;
            nameMatches  = string.Equals(n.Name, lockedName, StringComparison.Ordinal);
            lockedParent = n.Parent;
        }

        // ロックしたアクタと同じ親・同じ名前の兄弟の数
        int sameNameSiblings = 0;
        foreach (var n in nodes)
            if (n.Parent == lockedParent && string.Equals(n.Name, lockedName, StringComparison.Ordinal)) sameNameSiblings++;
        shape.Add(sameNameSiblings);

        return (nameMatches, shape.ToHashCode());
    }
}
