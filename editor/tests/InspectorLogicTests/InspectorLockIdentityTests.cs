// ============================================================
//  InspectorLockIdentityTests.cs — インスペクタのロックが「同じアクタ」を指し続けているかの照合
//  （2026-10-03 の 2 回目のレビュー #13）
//
//  【起きていたこと】
//  List{I0,I1,I2}（全部 "Item"）で I0 を消し、I1 をロックして別のアクタを選ぶ。Ctrl+Z で I0 が戻ると I1 は 1 つ後ろの番号へずれ、
//  ロックした番号には I0 が入る。ロックの確かめは「同じ番号に同じ名前」だけだったので続き、表示は I1 の値のまま、
//  書き先（_currentActorId）は I0 になった（ランタイムの SELECTED / ACTOR_COMPONENTS はロックで捨てられ、表示は直らない）。
//
//  【直し】ヒエラルキーが変わるたびに、ロックした番号の ACTOR_COMPONENTS を取り直し、名前と中身（ルートの種類・
//  プレハブの参照・コンポーネントの構成）をロックしたアクタの目印と照合する。違えばロックを外して知らせる。
//  同じなら取り直した値で描き直す（表示と書き先が同じアクタになる）。
//
//  【検証範囲】目印の作り方と照合（InspectorLockIdentity）。WPF の配線（取り直しの送信と応答の振り分け）は
//  InspectorPanel.Lock.cs。名前も構成も同じ兄弟（同じプレハブの行など）は見分けられない（残る制限。docs/backlog.md）。
// ============================================================

using SEEDEditor.Panels.Inspector;
using SpriteRigTests;

namespace InspectorLogicTests;

/// <summary>ロックの照合のテスト。</summary>
public static class InspectorLockIdentityTests
{
    /// <summary>ロックした I1（"Item"。Transform・Sprite・スクリプト）の ACTOR_COMPONENTS。</summary>
    private const string LockedItem = """
        {"id":4,"name":"Item","is_root":false,"prefab_source":null,"transform":{"position":[0,0,0]},
         "components":[{"slot":0,"type":"SpriteComponent","name":"Icon"},
                       {"slot":1,"type":"ScriptComponent","name":"Row","model_path":"assets://scripts/Row.cs"}]}
        """;

    /// <summary>同じアクタの値だけが変わった応答（位置とスクリプト以外の値）。</summary>
    private const string SameItemMoved = """
        {"id":4,"name":"Item","is_root":false,"prefab_source":null,"transform":{"position":[5,1,0]},
         "components":[{"slot":0,"type":"SpriteComponent","name":"Icon","color":[1,0,0,1]},
                       {"slot":1,"type":"ScriptComponent","name":"Row","model_path":"assets://scripts/Row.cs"}]}
        """;

    /// <summary>番号へずれ込んだ同じ名前の別のアクタ（構成が違う: スクリプトが無い）。</summary>
    private const string OtherItemFewerComponents = """
        {"id":4,"name":"Item","is_root":false,"prefab_source":null,"transform":{"position":[0,0,0]},
         "components":[{"slot":0,"type":"SpriteComponent","name":"Icon"}]}
        """;

    /// <summary>同じ名前・同じ構成だが、別のプレハブのインスタンス。</summary>
    private const string OtherItemOtherPrefab = """
        {"id":4,"name":"Item","is_root":false,"prefab_source":"assets://ui/OtherRow.actor","transform":{"position":[0,0,0]},
         "components":[{"slot":0,"type":"SpriteComponent","name":"Icon"},
                       {"slot":1,"type":"ScriptComponent","name":"Row","model_path":"assets://scripts/Row.cs"}]}
        """;

    /// <summary>同じ名前・同じ構成だが、2D のアクタ（CanvasTransform）。</summary>
    private const string OtherItem2D = """
        {"id":4,"name":"Item","is_root":false,"prefab_source":null,"canvas_transform":{"position":[0,0]},
         "components":[{"slot":0,"type":"SpriteComponent","name":"Icon"},
                       {"slot":1,"type":"ScriptComponent","name":"Row","model_path":"assets://scripts/Row.cs"}]}
        """;

    /// <summary>テストを登録する。</summary>
    /// <param name="h">テストランナー。</param>
    public static void Register(TestHarness h)
    {
        h.Add("ロックの照合: 値だけが変わった同じアクタは Same（ロックを続け、値を描き直す）", () =>
            Check.Equal(InspectorLockVerdict.Same, Compare(LockedItem, SameItemMoved), "値の変化でロックを外さない"));
        h.Add("ロックの照合: 同じ名前でもコンポーネントの構成が違えば Different（レビュー #13）", () =>
            Check.Equal(InspectorLockVerdict.Different, Compare(LockedItem, OtherItemFewerComponents), "ずれ込んだ別のアクタ"));
        h.Add("ロックの照合: 名前が違えば Different", () =>
            Check.Equal(InspectorLockVerdict.Different, Compare(LockedItem, LockedItem.Replace("\"Item\"", "\"Item2\"")), "名前"));
        h.Add("ロックの照合: プレハブの参照・2D/3D の違いも Different", () =>
        {
            Check.Equal(InspectorLockVerdict.Different, Compare(LockedItem, OtherItemOtherPrefab), "別のプレハブのインスタンス");
            Check.Equal(InspectorLockVerdict.Different, Compare(LockedItem, OtherItem2D), "3D と 2D");
        });
        h.Add("ロックの照合: スロットの並び・スクリプトのパスの違いも Different", () =>
        {
            Check.Equal(InspectorLockVerdict.Different,
                Compare(LockedItem, LockedItem.Replace("assets://scripts/Row.cs", "assets://scripts/Other.cs")), "スクリプトのパス");
            Check.Equal(InspectorLockVerdict.Different,
                Compare(LockedItem, LockedItem.Replace("\"slot\":1", "\"slot\":2")), "スロットの番号");
        });
        h.Add("ロックの照合: 解析できない応答は Unknown（ロックを壊さない）", () =>
        {
            Check.Equal(InspectorLockVerdict.Unknown, Compare(LockedItem, "{ 壊れた"), "壊れた応答");
            Check.Equal(InspectorLockVerdict.Unknown, Compare("[]", LockedItem), "目印が無い");
        });

        // ── 取り直すきっかけ（木の形の要約。InspectorLockShape）──────────
        h.Add("ロックの取り直し: 削除の Undo で同じ名前の兄弟がロックした番号へ入ると、名前は合うが形が変わる（レビュー #13 の手順）", () =>
        {
            // R(0) / List(1) / I1(2) / I2(3) で I1（2 番）をロック → Undo で I0 が 2 番に戻り、I1 は 3 番・I2 は 4 番へ
            var afterDelete = Nodes(("R", -1), ("List", 0), ("Item", 1), ("Item", 1));
            var afterUndo   = Nodes(("R", -1), ("List", 0), ("Item", 1), ("Item", 1), ("Item", 1));
            var before = InspectorLockShape.Evaluate(afterDelete, lockedId: 2, lockedName: "Item");
            var after  = InspectorLockShape.Evaluate(afterUndo, lockedId: 2, lockedName: "Item");
            Check.True(before.NameMatches && after.NameMatches, "従来の確かめ（同じ番号に同じ名前）は通ってしまう");
            Check.True(before.Shape != after.Shape, "形が変わるので中身を取り直す");
        });
        h.Add("ロックの取り直し: ロックしたアクタより後ろに関係の無いアクタが増えても取り直さない（Play 中の生成）", () =>
        {
            var baseTree = Nodes(("R", -1), ("Player", 0), ("Enemy", 0));
            var spawned  = Nodes(("R", -1), ("Player", 0), ("Enemy", 0), ("Bullet", 0), ("Bullet", 0));
            Check.Equal(InspectorLockShape.Evaluate(baseTree, 1, "Player").Shape, InspectorLockShape.Evaluate(spawned, 1, "Player").Shape,
                "インスペクタを描き直し続けない");
        });
        h.Add("ロックの取り直し: 手前の増減・名前の違いを見分ける", () =>
        {
            var baseTree = Nodes(("R", -1), ("A", 0), ("Target", 0));
            var inserted = Nodes(("R", -1), ("New", 0), ("A", 0), ("Target", 0));
            Check.True(InspectorLockShape.Evaluate(baseTree, 2, "Target").Shape != InspectorLockShape.Evaluate(inserted, 2, "Target").Shape,
                "手前の増減で形が変わる");
            Check.True(!InspectorLockShape.Evaluate(inserted, 2, "Target").NameMatches, "ずれて名前が合わなければ従来どおり外す");
            Check.True(!InspectorLockShape.Evaluate(baseTree, 9, "Target").NameMatches, "番号が無ければ名前は合わない");
        });
    }

    /// <summary>名前と親の番号の並びから、DFS 番号を 0 から振ったノード列を作る（親 -1 はルート）。</summary>
    private static InspectorLockNode[] Nodes(params (string Name, int Parent)[] nodes)
    {
        var result = new InspectorLockNode[nodes.Length];
        for (int i = 0; i < nodes.Length; i++) result[i] = new InspectorLockNode(i, nodes[i].Name, nodes[i].Parent);
        return result;
    }

    /// <summary>2 つの ACTOR_COMPONENTS から目印を作って照合する。</summary>
    private static InspectorLockVerdict Compare(string lockedJson, string incomingJson)
        => InspectorLockIdentity.Compare(InspectorLockIdentity.TryParse(lockedJson), InspectorLockIdentity.TryParse(incomingJson));
}
