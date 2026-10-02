using SEEDEditor.Panels.Hierarchy;
using SpriteRigTests;   // TestHarness / Check（テストランナーは SpriteRigTests と共用）

namespace HierarchySyncTests;

/// <summary>
/// 差分同期の後の選択の直し方（<see cref="SelectionRestorePlan"/>）の単体テスト。
///
/// <para>
/// 守りたいこと（docs/reviews/2026-10-02_code_review.md の #1）: 同じアクターの DFS ID が変わったら
/// インスペクタにも新しい ID を知らせる。知らせないとインスペクタが古い ID を持ったまま、
/// 次の値の編集がその ID に今いる別のアクターへ当たる。
/// </para>
/// </summary>
public static class SelectionRestorePlanTests
{
    /// <summary>テストを登録する。</summary>
    public static void Register(TestHarness harness)
    {
        harness.Add("選択の直し方: 同じアクターの ID が変わったらインスペクタへ知らせる", MovedNotifiesInspector);
        harness.Add("選択の直し方: 同じ ID のままならインスペクタへ知らせない", KeepDoesNotNotify);
        harness.Add("選択の直し方: 選んでいたアクターが消えたら選択を外す", LostClearsSelection);
        harness.Add("選択の直し方: 未選択・キーが取れないときは従来どおり", NothingAndKeyless);
    }

    /// <summary>プレビューの出し入れの Ctrl+Z で Shell が 17 → 4 へ移った形。</summary>
    private static void MovedNotifiesInspector()
    {
        var plan = SelectionRestorePlan.Decide(17, "/App#0/RootStack#0/Screens#0/Shell#0", 4);
        Check.Equal(SelectionRestoreKind.Moved, plan.Kind, "種類");
        Check.Equal(4, plan.NewId, "新しい ID");
        Check.True(plan.NotifiesInspector, "インスペクタへ新しい ID を知らせる");
    }

    private static void KeepDoesNotNotify()
    {
        var plan = SelectionRestorePlan.Decide(4, "/App#0/RootStack#0/Screens#0/Shell#0", 4);
        Check.Equal(SelectionRestoreKind.Keep, plan.Kind, "種類");
        Check.Equal(4, plan.NewId, "ID はそのまま");
        Check.True(!plan.NotifiesInspector, "同じ ID なら知らせない（ヒエラルキー受信のたびに取り直さない）");
    }

    private static void LostClearsSelection()
    {
        var plan = SelectionRestorePlan.Decide(2, "/App#0/Background#0/ScreenFrame#0", null);
        Check.Equal(SelectionRestoreKind.Lost, plan.Kind, "種類");
        Check.Equal(SelectionRestorePlan.NoSelection, plan.NewId, "未選択");
        Check.True(!plan.NotifiesInspector, "消えたときはインスペクタへ新しい ID を送らない（未選択はランタイムの SELECTED:-1）");
    }

    private static void NothingAndKeyless()
    {
        var none = SelectionRestorePlan.Decide(-1, "", null);
        Check.Equal(SelectionRestoreKind.Nothing, none.Kind, "未選択なら何もしない");

        var keyless = SelectionRestorePlan.Decide(5, "", null);
        Check.Equal(SelectionRestoreKind.Keep, keyless.Kind, "行のキーが取れないときは ID を保つ（従来どおり）");
        Check.Equal(5, keyless.NewId, "ID はそのまま");
        Check.True(!keyless.NotifiesInspector, "知らせない");
    }
}
