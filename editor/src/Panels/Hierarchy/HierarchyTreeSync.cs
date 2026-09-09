using System.Collections.Generic;

namespace SEEDEditor.Panels.Hierarchy;

/// <summary>
/// ヒエラルキー 1 階層ぶんの「ノード（ランタイム由来の正解）」を表す抽象。
///
/// 差分更新アルゴリズムを WPF から切り離して単体テストできるようにするための
/// 最小インターフェース。実体は <c>HierarchyPanel.ActorNode</c>（本番）と
/// テスト用のフェイクノードの 2 つ。
/// </summary>
public interface IHierarchySyncNode
{
    /// <summary>
    /// 同一階層内で一意な安定キー（<c>名前#同名兄弟内の出現番号</c> のパス）。
    /// DFS ID は並べ替え・生成・破棄でズレるためキーには使えない。
    /// </summary>
    string StableKey { get; }

    /// <summary>子ノード列（表示順）。</summary>
    IReadOnlyList<IHierarchySyncNode> SyncChildren { get; }
}

/// <summary>
/// ヒエラルキー 1 階層ぶんの「UI 項目列」を表す抽象。
///
/// 本番は WPF の <c>ItemCollection</c>（TreeViewItem 群）を包むアダプタ、
/// テストは単純なリストを包むフェイクが実装する。
/// アルゴリズムは索引だけで操作し、UI 型を一切知らない。
/// </summary>
public interface IHierarchySyncItems
{
    /// <summary>現在の項目数。</summary>
    int Count { get; }

    /// <summary>指定位置の項目が現在束ねているノードの安定キー。</summary>
    string KeyAt(int index);

    /// <summary>項目を <paramref name="from"/> から <paramref name="to"/> へ移動する（同一インスタンスのまま）。</summary>
    void MoveTo(int from, int to);

    /// <summary>ノードから新しい項目を作って指定位置へ挿入する。</summary>
    void InsertNew(int index, IHierarchySyncNode node);

    /// <summary>指定位置の項目を取り除く。</summary>
    void RemoveAt(int index);

    /// <summary>
    /// 既存項目へ新しいノードを結び付け直す（ID・表示の更新）。
    /// 同じ安定キーでもランタイム側の DFS ID は変わり得るため、再利用のたびに必ず呼ぶ。
    /// </summary>
    void Bind(int index, IHierarchySyncNode node);

    /// <summary>指定位置の項目が持つ子階層の項目列を返す。</summary>
    IHierarchySyncItems ChildrenAt(int index);
}

/// <summary>
/// ヒエラルキーツリーの差分更新アルゴリズム（UI 非依存・純粋ロジック）。
///
/// <para>
/// Play 中はスクリプトの生成／破棄のたびにシーン全木が届く。毎回作り直すと
/// UI スレッドが数百 ms 占有されるため、安定キーで既存項目と突き合わせて
/// 「一致 → 再利用」「新規 → 挿入」「消滅 → 削除」だけを行う。
/// </para>
/// <para>
/// 【不変条件】同期後、位置 i の項目は必ず <c>nodes[i]</c> に束ねられている。
/// 再利用した項目にも <see cref="IHierarchySyncItems.Bind"/> を必ず通すので、
/// 「見た目は同じだが ID が古い項目」が残ることはない。これが崩れると
/// ヒエラルキーのクリックが別アクターを選ぶ不具合になる。
/// </para>
/// </summary>
public static class HierarchyTreeSync
{
    /// <summary>
    /// 1 階層ぶんの項目列を新しいノード列へ合わせ込む（子階層へ再帰）。
    /// </summary>
    /// <param name="items">更新対象の項目列。</param>
    /// <param name="nodes">この階層の正解ノード列（表示順）。</param>
    public static void SyncLevel(IHierarchySyncItems items, IReadOnlyList<IHierarchySyncNode> nodes)
    {
        // 現在の並びを安定キーの列として写し取る。以降はこの写しと項目列へ
        // 同じ操作を並行して適用するので、探索は O(1) の索引アクセスで済む
        // （UI 側へ IndexOf を投げ返さないぶん速く、テストからも追える）。
        var keys = new List<string>(items.Count);
        for (int i = 0; i < items.Count; i++) keys.Add(items.KeyAt(i));

        for (int i = 0; i < nodes.Count; i++)
        {
            var node = nodes[i];

            // 探索は i 以降だけを見る。0..i-1 は確定済みで、そこを掴んでしまうと
            // 確定済みの項目を引き剥がしてしまう（同名兄弟でキーが重複した場合の保険）。
            int cur = keys.IndexOf(node.StableKey, i);
            if (cur >= 0)
            {
                if (cur != i)
                {
                    items.MoveTo(cur, i);
                    var key = keys[cur];
                    keys.RemoveAt(cur);
                    keys.Insert(i, key);
                }
                // 再利用でも必ず束ね直す（同じキーでも DFS ID は変わり得る）。
                items.Bind(i, node);
                SyncLevel(items.ChildrenAt(i), node.SyncChildren);
            }
            else
            {
                items.InsertNew(i, node);
                keys.Insert(i, node.StableKey);
            }
        }

        // 末尾に押し出された未使用項目（＝今回のツリーに存在しないノード）を削除する。
        while (keys.Count > nodes.Count)
        {
            items.RemoveAt(keys.Count - 1);
            keys.RemoveAt(keys.Count - 1);
        }
    }
}
