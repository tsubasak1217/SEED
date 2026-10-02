// ============================================================
//  HierarchyEyeClickTests.cs — 行の目アイコンを押したときに送る値の判定（HierarchyEyeClick）
//  （2026-10-03 の 2 回目のレビュー #12）
//
//  【起きていたこと】
//  目アイコンのクリックは見出しを作ったときのノード（DFS 番号・自分の表示フラグ）を握っていた。差分更新は同じ安定キーの行を
//  使い回して Tag だけ差し替え、見た目が同じなら見出しを作り直さないので、R{A,B,C} で A を消した後に B の目を押すと
//  SET_VISIBLE:1（今は C）が送られ、C が隠れて保存された。
//
//  【検証範囲】
//   - 差分更新で使い回した行が束ねる「今のノード」から判定すると、今の番号を送る（作ったときのノードでは別のアクタの番号になる）
//   - 送る値は自分のフラグの反転・プレビューの行と行から外れた見出しは送らない
//  WPF の配線（見出しの論理上の親 = 行の Tag から読む）は editor/tests/HierarchyPanelProbe が本物のパネルで確かめる。
// ============================================================

using System.Collections.Generic;
using SEEDEditor.Panels.Hierarchy;
using SpriteRigTests;

namespace HierarchySyncTests;

/// <summary>行の目アイコンの判定のテスト。</summary>
public static class HierarchyEyeClickTests
{
    /// <summary>テストを登録する。</summary>
    /// <param name="h">テストランナー。</param>
    public static void Register(TestHarness h)
    {
        h.Add("目: 手前を消した後に使い回した行は、今のノードの番号を送る（作ったときの番号は別のアクタ）", ReusedRowAfterRemovalSendsCurrentId);
        h.Add("目: 手前へ増えた後に使い回した行も、今のノードの番号を送る", ReusedRowAfterInsertionSendsCurrentId);
        h.Add("目: 送る値は自分の表示フラグの反転", SendsInvertedSelfVisible);
        h.Add("目: プレビューの行と、行から外れた見出し（ノード無し）は送らない", PreviewAndDetachedSendNothing);
    }

    /// <summary>R{A,B,C} で A を消す（レビューの再現手順そのもの）。</summary>
    private static void ReusedRowAfterRemovalSendsCurrentId()
    {
        var before = new List<FakeNode> { new("A", 0), new("B", 1), new("C", 2) };
        FakeNode.AssignKeys(before);
        var items = FakeItems.Build(before);
        var rowB = items.Items[1];
        var nodeWhenBuilt = rowB.Node;   // 見出しを作ったときのノード（修正前のラムダが握っていたもの）

        var after = new List<FakeNode> { new("B", 0), new("C", 1) };
        FakeNode.AssignKeys(after);
        HierarchyTreeSync.SyncLevel(items, after);

        Check.True(ReferenceEquals(items.Items[0], rowB), "前提: B の行が使い回される（見出しは作り直されない）");
        var sent = HierarchyEyeClick.Decide(rowB.Node);
        Check.True(sent == (0, false), $"今の B（0 番）を隠すはず: 実際 {sent}");

        // 作ったときのノードで判定すると 1 番 = 今の C を隠してしまう（修正前の不具合の形）
        var stale = HierarchyEyeClick.Decide(nodeWhenBuilt);
        Check.True(stale == (1, false) && after[1].Name == "C", "作ったときの番号は今は C を指す（不具合の条件の確認）");
    }

    /// <summary>[A,B] の間へ N が増える（貼り付け・Undo・プレビューの出し入れ）。</summary>
    private static void ReusedRowAfterInsertionSendsCurrentId()
    {
        var before = new List<FakeNode> { new("A", 0), new("B", 1) };
        FakeNode.AssignKeys(before);
        var items = FakeItems.Build(before);
        var rowB = items.Items[1];

        var after = new List<FakeNode> { new("A", 0), new("N", 1), new("B", 2) };
        FakeNode.AssignKeys(after);
        HierarchyTreeSync.SyncLevel(items, after);

        Check.True(ReferenceEquals(items.Items[2], rowB), "前提: B の行が使い回される");
        var sent = HierarchyEyeClick.Decide(rowB.Node);
        Check.True(sent == (2, false), $"今の B（2 番）を隠すはず: 実際 {sent}");
    }

    /// <summary>自分のフラグの反転（実効表示ではない）。</summary>
    private static void SendsInvertedSelfVisible()
    {
        Check.True(HierarchyEyeClick.Decide(new FakeNode("Shown", 3) { SelfVisible = true }) == (3, false), "表示中は隠す");
        Check.True(HierarchyEyeClick.Decide(new FakeNode("Hidden", 4) { SelfVisible = false }) == (4, true), "非表示は表示に戻す");
    }

    /// <summary>送らない場合。</summary>
    private static void PreviewAndDetachedSendNothing()
    {
        Check.True(HierarchyEyeClick.Decide(new FakeNode("Preview", 5) { IsPreview = true }) is null, "プレビューの行で送った");
        Check.True(HierarchyEyeClick.Decide(null) is null, "行から外れた見出しで送った");
    }
}
