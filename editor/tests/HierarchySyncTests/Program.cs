using System;
using System.Collections.Generic;
using System.Linq;
using SEEDEditor.Panels.Hierarchy;
using SpriteRigTests;   // TestHarness / Check（テストランナーは SpriteRigTests と共用）

namespace HierarchySyncTests;

// ============================================================
//  テスト用フェイク（WPF の TreeViewItem / ItemCollection の代役）
// ============================================================

/// <summary>
/// ランタイムから届いたヒエラルキー 1 ノードの代役。
/// 実体（HierarchyPanel.ActorNode）と同じく「安定キー」と「DFS ID」を持つ。
/// </summary>
public sealed class FakeNode : IHierarchySyncNode
{
    /// <summary>表示名。安定キーの素になる。</summary>
    public string Name { get; }

    /// <summary>DFS 通し番号。アクターが増減するとズレる値（＝キーに使えない値）。</summary>
    public int Id { get; }

    /// <summary>子ノード。</summary>
    public List<FakeNode> Children { get; } = new();

    /// <summary>安定キー（親キー + / + 名前 + # + 同名兄弟内の出現番号）。</summary>
    public string StableKey { get; set; } = "";

    IReadOnlyList<IHierarchySyncNode> IHierarchySyncNode.SyncChildren => Children;

    /// <summary>名前・DFS ID・子ノードを指定して生成する。</summary>
    /// <param name="name">表示名。</param>
    /// <param name="id">DFS 通し番号。</param>
    /// <param name="children">子ノード。</param>
    public FakeNode(string name, int id, params FakeNode[] children)
    {
        Name = name;
        Id   = id;
        Children.AddRange(children);
    }

    /// <summary>
    /// 本番（HierarchyPanel.AssignStableKeys）と同じ規則で安定キーを振る。
    /// 規則がずれるとテストの意味が無くなるため、書式もそのまま合わせている。
    /// </summary>
    /// <param name="nodes">同じ階層に並ぶノード群。</param>
    /// <param name="parentKey">親の安定キー（ルート階層は空文字）。</param>
    public static void AssignKeys(List<FakeNode> nodes, string parentKey = "")
    {
        var occurrences = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var node in nodes)
        {
            occurrences.TryGetValue(node.Name, out int index);
            occurrences[node.Name] = index + 1;
            node.StableKey = parentKey + "/" + node.Name + "#" + index;
            AssignKeys(node.Children, node.StableKey);
        }
    }
}

/// <summary>
/// UI 項目（TreeViewItem 相当）の代役。
/// 「どのノードを束ねているか」だけを持ち、生成された順に一意な識別子を振る。
/// 識別子を見れば「同じ項目が再利用されたのか、作り直されたのか」を判定できる。
/// </summary>
public sealed class FakeItem
{
    /// <summary>生成のたびに増える通し番号（項目インスタンスの同一性の指標）。</summary>
    public int Serial { get; }

    /// <summary>この項目がいま束ねているノード。</summary>
    public FakeNode Node { get; set; }

    /// <summary>子階層の項目列。</summary>
    public FakeItems Children { get; } = new();

    /// <summary>通し番号とノードを指定して生成する。</summary>
    /// <param name="serial">項目の通し番号。</param>
    /// <param name="node">束ねるノード。</param>
    public FakeItem(int serial, FakeNode node)
    {
        Serial = serial;
        Node   = node;
    }
}

/// <summary>1 階層ぶんの項目列（ItemCollection 相当）。</summary>
public sealed class FakeItems : IHierarchySyncItems
{
    /// <summary>全 FakeItems で共有する項目生成カウンタ（再利用判定用）。</summary>
    private static int _serialCounter;

    /// <summary>この階層の項目。</summary>
    public List<FakeItem> Items { get; } = new();

    /// <summary>項目数。</summary>
    public int Count => Items.Count;

    /// <summary>指定位置の項目が束ねているノードの安定キー。</summary>
    /// <param name="index">位置。</param>
    public string KeyAt(int index) => Items[index].Node.StableKey;

    /// <summary>項目を移動する。</summary>
    /// <param name="from">移動元。</param>
    /// <param name="to">移動先。</param>
    public void MoveTo(int from, int to)
    {
        var item = Items[from];
        Items.RemoveAt(from);
        Items.Insert(to, item);
    }

    /// <summary>ノードから新しい項目（子孫込み）を作って挿入する。</summary>
    /// <param name="index">挿入位置。</param>
    /// <param name="node">元になるノード。</param>
    public void InsertNew(int index, IHierarchySyncNode node)
    {
        var created = new FakeItem(++_serialCounter, (FakeNode)node);
        Items.Insert(index, created);
        // 新規項目は子階層もその場で作る（本番の BuildTreeItem と同じ）。
        foreach (var child in ((FakeNode)node).Children)
            created.Children.InsertNew(created.Children.Count, child);
    }

    /// <summary>指定位置の項目を取り除く。</summary>
    /// <param name="index">位置。</param>
    public void RemoveAt(int index) => Items.RemoveAt(index);

    /// <summary>既存項目へ新しいノードを束ね直す。</summary>
    /// <param name="index">位置。</param>
    /// <param name="node">新しいノード。</param>
    public void Bind(int index, IHierarchySyncNode node) => Items[index].Node = (FakeNode)node;

    /// <summary>指定位置の項目の子階層を返す。</summary>
    /// <param name="index">位置。</param>
    public IHierarchySyncItems ChildrenAt(int index) => Items[index].Children;

    /// <summary>ノード列から初期状態のツリーを作る（同期前の「既存ツリー」）。</summary>
    /// <param name="roots">ルートノード列。</param>
    public static FakeItems Build(List<FakeNode> roots)
    {
        var items = new FakeItems();
        foreach (var r in roots) items.InsertNew(items.Count, r);
        return items;
    }
}

// ============================================================
//  テスト本体
// ============================================================

/// <summary>
/// ヒエラルキー差分更新（<see cref="HierarchyTreeSync"/>）の単体テスト。
///
/// <para>
/// 検証の中心は 1 つ:「同期後、位置 i の項目は必ず nodes[i] を束ねている」。
/// これが崩れると、行をクリックしたときに古い DFS ID がランタイムへ送られ、
/// インスペクタに別のアクターが表示される（実際に報告された不具合の形）。
/// </para>
/// </summary>
public static class Program
{
    /// <summary>エントリポイント。全テストを実行し、失敗があれば終了コード 1 を返す。</summary>
    public static int Main()
    {
        var harness = new TestHarness();

        harness.Add("変化なし: 項目は再利用され、束ねるノードも一致する", NoChangeReusesItems);
        harness.Add("途中に挿入: 以降の項目がずれずに正しいノードを束ねる", InsertInMiddle);
        harness.Add("途中を削除（同名兄弟あり）: 残る項目が正しい ID を束ねる", RemoveInMiddleWithDuplicateNames);
        harness.Add("並べ替え: 項目は作り直されず、位置だけが入れ替わる", Reorder);
        harness.Add("同名のまま ID だけズレる: 全項目が新しい ID を束ね直す", IdShiftWithSameNames);
        harness.Add("子階層も再帰的に整合する", NestedLevelsStayConsistent);
        harness.Add("末尾の余りを削除しても、生き残る項目は正しい対応を保つ", TrimKeepsMapping);
        harness.Add("別シーンへの全面差し替えでも位置とノードが 1:1 で対応する", WholeTreeReplacement);

        Console.WriteLine("=== ヒエラルキー差分更新テスト ===");
        return harness.Run();
    }

    // ── 共通ヘルパー ────────────────────────────────────────

    /// <summary>ノード列に安定キーを振ってから差分更新を実行する（本番と同じ手順）。</summary>
    /// <param name="items">更新対象の項目列。</param>
    /// <param name="nodes">正解ノード列。</param>
    private static void Sync(FakeItems items, List<FakeNode> nodes)
    {
        FakeNode.AssignKeys(nodes);
        HierarchyTreeSync.SyncLevel(items, nodes);
    }

    /// <summary>
    /// 「位置 i の項目が nodes[i] を束ねている」ことを全階層で検査する。
    /// これが本テスト群の唯一かつ最重要の不変条件。
    /// </summary>
    /// <param name="items">検査対象の項目列。</param>
    /// <param name="nodes">期待するノード列。</param>
    /// <param name="path">失敗表示用の位置。</param>
    private static void CheckMapping(FakeItems items, List<FakeNode> nodes, string path = "")
    {
        Check.Equal(nodes.Count, items.Count, $"{path} の項目数");
        for (int i = 0; i < nodes.Count; i++)
        {
            var item = items.Items[i];
            Check.Equal(nodes[i].Name, item.Node.Name, $"{path}[{i}] の名前");
            Check.Equal(nodes[i].Id,   item.Node.Id,   $"{path}[{i}] の DFS ID");
            Check.True(ReferenceEquals(nodes[i], item.Node), $"{path}[{i}] が最新ノードを束ねていない");
            CheckMapping(item.Children, nodes[i].Children, $"{path}[{i}]");
        }
    }

    /// <summary>ルート階層の項目通し番号の一覧（再利用されたかの判定に使う）。</summary>
    /// <param name="items">対象の項目列。</param>
    private static List<int> Serials(FakeItems items) => items.Items.Select(x => x.Serial).ToList();

    // ── テスト ──────────────────────────────────────────────

    /// <summary>同じツリーが再び届いても、項目は作り直されずノードだけ束ね直される。</summary>
    private static void NoChangeReusesItems()
    {
        var before = new List<FakeNode> { new("A", 0), new("B", 1), new("C", 2) };
        FakeNode.AssignKeys(before);
        var items   = FakeItems.Build(before);
        var serials = Serials(items);

        var after = new List<FakeNode> { new("A", 0), new("B", 1), new("C", 2) };
        Sync(items, after);

        CheckMapping(items, after);
        Check.True(serials.SequenceEqual(Serials(items)), "項目が作り直されている（再利用されていない）");
    }

    /// <summary>途中に新しいアクターが生まれた場合（Instantiate）。以降の ID は 1 つずつ後ろへずれる。</summary>
    private static void InsertInMiddle()
    {
        var before = new List<FakeNode> { new("A", 0), new("C", 1), new("D", 2) };
        FakeNode.AssignKeys(before);
        var items = FakeItems.Build(before);

        // A(0) / B(1・新規) / C(2) / D(3)
        var after = new List<FakeNode> { new("A", 0), new("B", 1), new("C", 2), new("D", 3) };
        Sync(items, after);

        CheckMapping(items, after);
    }

    /// <summary>
    /// 同名兄弟が並ぶ階層の途中を削除する（安定キーの出現番号がずれる最悪ケース）。
    ///
    /// kumanomi#0 が消えると kumanomi#1 が kumanomi#0 になる。キーだけを見ると
    /// 「消えた 1 体目の項目」と「生き残った 2 体目」が結び付くので、そのとき
    /// 項目が古い DFS ID を握ったままだと、クリックで別アクターが選ばれる。
    /// </summary>
    private static void RemoveInMiddleWithDuplicateNames()
    {
        var before = new List<FakeNode>
        {
            new("head", 0), new("kumanomi", 1), new("kumanomi", 2), new("tail", 3),
        };
        FakeNode.AssignKeys(before);
        var items = FakeItems.Build(before);

        // 1 体目の kumanomi が Destroy され、以降の DFS ID が 1 つ前へ詰まる
        var after = new List<FakeNode> { new("head", 0), new("kumanomi", 1), new("tail", 2) };
        Sync(items, after);

        CheckMapping(items, after);
        Check.Equal(1, items.Items[1].Node.Id, "生き残った kumanomi の DFS ID");
        Check.Equal(2, items.Items[2].Node.Id, "tail の DFS ID");
    }

    /// <summary>並べ替え（親子付け替えや ID 順の変化）。項目は再生成されず移動だけで済む。</summary>
    private static void Reorder()
    {
        var before = new List<FakeNode> { new("A", 0), new("B", 1), new("C", 2) };
        FakeNode.AssignKeys(before);
        var items = FakeItems.Build(before);
        var serialByName = items.Items.ToDictionary(x => x.Node.Name, x => x.Serial);

        var after = new List<FakeNode> { new("C", 0), new("A", 1), new("B", 2) };
        Sync(items, after);

        CheckMapping(items, after);
        foreach (var item in items.Items)
            Check.Equal(serialByName[item.Node.Name], item.Serial, $"{item.Node.Name} の項目が作り直された");
    }

    /// <summary>
    /// 名前も並びも同じで DFS ID だけが変わるケース
    /// （手前のサブツリーでアクターが増減すると、後続の ID が丸ごとずれる）。
    /// 見た目が変わらないため Header の再生成は不要だが、ID の束ね直しは必須。
    /// </summary>
    private static void IdShiftWithSameNames()
    {
        var before = new List<FakeNode> { new("A", 10), new("B", 11), new("C", 12) };
        FakeNode.AssignKeys(before);
        var items = FakeItems.Build(before);

        var after = new List<FakeNode> { new("A", 30), new("B", 31), new("C", 32) };
        Sync(items, after);

        CheckMapping(items, after);
        Check.Equal(30, items.Items[0].Node.Id, "A の DFS ID");
        Check.Equal(32, items.Items[2].Node.Id, "C の DFS ID");
    }

    /// <summary>子階層でも同じ規則（挿入・削除・ID ズレ）が成立する。</summary>
    private static void NestedLevelsStayConsistent()
    {
        var before = new List<FakeNode>
        {
            new("Managers", 0, new FakeNode("Spawner", 1), new FakeNode("Audio", 2)),
            new("trees",    3, new FakeNode("Yasi", 4)),
        };
        FakeNode.AssignKeys(before);
        var items = FakeItems.Build(before);

        // Spawner が消え、trees に Yasi(1) が増える
        var after = new List<FakeNode>
        {
            new("Managers", 0, new FakeNode("Audio", 1)),
            new("trees",    2, new FakeNode("Yasi", 3), new FakeNode("Yasi(1)", 4)),
        };
        Sync(items, after);

        CheckMapping(items, after);
    }

    /// <summary>末尾の余り削除（項目数 &gt; ノード数）で、生き残る項目の対応が壊れない。</summary>
    private static void TrimKeepsMapping()
    {
        var before = new List<FakeNode> { new("A", 0), new("B", 1), new("C", 2), new("D", 3) };
        FakeNode.AssignKeys(before);
        var items = FakeItems.Build(before);

        // B と C が消え、D だけが残る（末尾から 2 件切り詰める経路）
        var after = new List<FakeNode> { new("A", 0), new("D", 1) };
        Sync(items, after);

        CheckMapping(items, after);
    }

    /// <summary>
    /// 別シーンへの全面差し替え（Play 中のシーン遷移や Play 停止の復元で起きる）。
    /// ルート名が一部一致していても、位置とノードの 1:1 対応が崩れてはいけない。
    /// </summary>
    private static void WholeTreeReplacement()
    {
        var before = new List<FakeNode>
        {
            new("Skybox", 0), new("Ocean", 1), new("LogoCanvas", 2), new("Managers", 3),
        };
        FakeNode.AssignKeys(before);
        var items = FakeItems.Build(before);

        // 遷移先シーン: 名前が一部だけ一致し、並びも件数も違う
        var after = new List<FakeNode>
        {
            new("Skybox", 0),
            new("Managers", 1, new FakeNode("FishPool", 2)),
            new("Player", 3),
            new("Ocean", 4),
        };
        Sync(items, after);

        CheckMapping(items, after);
    }
}
