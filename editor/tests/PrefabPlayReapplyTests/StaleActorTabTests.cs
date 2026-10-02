// ============================================================
//  StaleActorTabTests.cs — 書き戻しで古くなったアクタータブの「読み直し」の印（StaleActorTabs）
//  （2026-10-03 の 2 回目のレビュー #14）
//
//  【起きていたこと】
//  印（MainWindow.Prefab.cs の _staleActorTabPaths）は「付ける」と「読み直したときに消す」の 2 か所しか無く、
//   (a) 印の付いたタブを表示しないまま閉じ、後で開き直して編集すると、次に表示したとき確認なしで読み直して編集と Undo を消した
//   (b) 未保存の編集が残ったタブも、停止時に表示中ならすぐ読み直して編集を消した
//
//  【検証範囲】
//   - 閉じた・新しく開いたタブの印は消える／書き方違いのパスは同じタブ
//   - 表示するとき: 印なし・Edit でない → そのまま／未保存なし → 読み直す／未保存あり → 確かめる。聞くのは 1 回だけ
// ============================================================

using SEEDEditor.Reload;
using SpriteRigTests;

namespace PrefabPlayReapplyTests;

/// <summary>読み直しの印のテスト。</summary>
public static class StaleActorTabTests
{
    /// <summary>テストに使うアクタータブのパス。</summary>
    private const string CardTab = @"C:\proj\assets\ui\Card.actor";

    /// <summary>同じファイルを区切りと大文字小文字だけ変えた書き方。</summary>
    private const string CardTabOtherSpelling = "c:/PROJ/assets/ui/card.actor";

    /// <summary>別のタブ。</summary>
    private const string ScreenTab = @"C:\proj\assets\ui\Screen.actor";

    /// <summary>テストを登録する。</summary>
    /// <param name="h">テストランナー。</param>
    public static void Register(TestHarness h)
    {
        h.Add("読み直しの印: 閉じたタブの印は消え、開き直して表示しても読み直さない（レビュー #14 (a)）", () =>
        {
            var tabs = new StaleActorTabs();
            Check.True(tabs.Mark(CardTab), "書き戻しで印を付ける");
            Check.True(tabs.Forget(CardTab), "閉じると印が消える");
            Check.Equal(StaleActorTabShowAction.ShowAsIs,
                tabs.TakeOnShow(CardTab, canReloadNow: true, hasUnsavedEdits: true),
                "開き直したタブを表示しても読み直さない（開き直した後の編集を消さない）");
        });
        h.Add("読み直しの印: 同じファイルを新しいタブで開くときも消す（書き方違いのパスも同じタブ）", () =>
        {
            var tabs = new StaleActorTabs();
            tabs.Mark(CardTab);
            tabs.Mark(ScreenTab);
            Check.True(tabs.Forget(CardTabOtherSpelling), "区切り・大文字小文字の違いは同じタブ");
            Check.True(!tabs.IsMarked(CardTab), "Card の印が消えた");
            Check.True(tabs.IsMarked(ScreenTab), "ほかのタブの印は残る");
            Check.True(!tabs.Forget(CardTab), "2 回目は消すものが無い");
        });
        h.Add("読み直しの印: 未保存の編集が無ければ読み直し、印は消える", () =>
        {
            var tabs = new StaleActorTabs();
            tabs.Mark(CardTab);
            Check.Equal(StaleActorTabShowAction.Reload,
                tabs.TakeOnShow(CardTab, canReloadNow: true, hasUnsavedEdits: false), "確かめずに読み直す");
            Check.Equal(0, tabs.Count, "印は消える");
            Check.Equal(StaleActorTabShowAction.ShowAsIs,
                tabs.TakeOnShow(CardTab, canReloadNow: true, hasUnsavedEdits: false), "2 回目の表示では読み直さない");
        });
        h.Add("読み直しの印: 未保存の編集があれば読み直す前に確かめ、聞くのは 1 回だけ（レビュー #14 (b)）", () =>
        {
            var tabs = new StaleActorTabs();
            tabs.Mark(CardTab);
            Check.Equal(StaleActorTabShowAction.AskBeforeReload,
                tabs.TakeOnShow(CardTab, canReloadNow: true, hasUnsavedEdits: true), "確認なしで捨てない");
            Check.Equal(StaleActorTabShowAction.ShowAsIs,
                tabs.TakeOnShow(CardTab, canReloadNow: true, hasUnsavedEdits: true), "「いいえ」の後に表示し直しても聞き直さない");
        });
        h.Add("読み直しの印: Edit でない（読み直せない）ときは印を残す", () =>
        {
            var tabs = new StaleActorTabs();
            tabs.Mark(CardTab);
            Check.Equal(StaleActorTabShowAction.ShowAsIs,
                tabs.TakeOnShow(CardTab, canReloadNow: false, hasUnsavedEdits: false), "Play 中は読み直さない");
            Check.True(tabs.IsMarked(CardTab), "印は残り、Edit で表示したときに読み直す");
        });
        h.Add("読み直しの印: 空のパスには付けない・印の無いタブはそのまま", () =>
        {
            var tabs = new StaleActorTabs();
            Check.True(!tabs.Mark("  "), "空白だけのパス");
            Check.Equal(StaleActorTabShowAction.ShowAsIs,
                tabs.TakeOnShow(ScreenTab, canReloadNow: true, hasUnsavedEdits: true), "印が無ければ確かめもしない");
        });
    }
}
