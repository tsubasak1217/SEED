using System.Windows.Controls;
using SEEDEditor.Panels.Hierarchy;

namespace SEEDEditor.Panels;

// ============================================================
//  HierarchyPanel.EyeToggle.cs — 行の目アイコン（表示 / 非表示）を押したときの送信
//
//  【押した時点の行から読む（2026-10-03 の 2 回目のレビュー #12）】
//  差分更新は安定キーが同じ行を使い回し、行の Tag だけ新しいノードへ差し替えて、見た目が変わらなければ見出しを作り直さない
//  （HeaderDiffers は DFS 番号を比べない。番号のずれだけで全行の見出しを作り直すと、Play 中の生成・破棄で重い）。
//  見出しを作ったときのノードを握ると、手前の増減の後に古い番号で SET_VISIBLE を送り、別のアクタを切り替えて保存させていた。
//  ここでは押した見出しの論理上の親（＝行の TreeViewItem）の Tag から今のノードを読み、判定は WPF 非依存の
//  HierarchyEyeClick（HierarchySyncTests が確かめる）に任せる。実物の確認は editor/tests/HierarchyPanelProbe。
// ============================================================

/// <summary>ヒエラルキーパネルのうち、行の目アイコンの送信を受け持つ部分。</summary>
public partial class HierarchyPanel
{
    /// <summary>
    /// 行の目アイコンが押された: 押した時点でその行にいるアクタの表示フラグ（自分のフラグ）を反転して送る。
    /// 行から外れた見出し（名前の変更の入力中に差し替わった古い見出し）と、画面プレビューの行は何も送らない。
    /// </summary>
    /// <param name="header">押された目アイコンを含む行の見出し（TreeViewItem.Header に入れた TextBlock）。</param>
    private void OnRowEyeClicked(TextBlock header)
    {
        // 見出しは TreeViewItem の論理上の子（HeaderedItemsControl が Header を論理の子にする）。
        // 行の Tag は差分更新が毎回新しいノードへ束ね直すので、ここが「今その行にいるアクタ」の正解。
        var rowNode = (header.Parent as TreeViewItem)?.Tag as ActorNode;
        if (HierarchyEyeClick.Decide(rowNode) is not { } toggle) return;
        _runtime?.SendToRuntime(SEEDEditor.Controls.VisibilityToggle.BuildCommand(toggle.Id, toggle.Visible));
    }
}
