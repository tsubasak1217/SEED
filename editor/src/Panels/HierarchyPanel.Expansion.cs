using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace SEEDEditor.Panels;

/// <summary>
/// HierarchyPanel の「ツリー展開状態（折りたたみ）の永続化」機能。
///
/// Hierarchy は Rust ランタイムから HIERARCHY を受けるたびに TreeViewItem を全破棄して
/// 作り直す（RebuildTree）。TreeViewItem は毎回新規生成されるため、そのまま作ると
/// 展開状態が失われる ＝ 複製や並べ替えのたびに閉じていたグループが勝手に開いてしまう。
///
/// そこで「折りたたまれているノードの安定キー集合」をパネル側に退避しておき、
/// 再構築のたびに復元する。
///
/// 【なぜ「展開」ではなく「折りたたみ」を覚えるのか】
/// 既定は展開（従来挙動）である。未知のノード＝新規に現れたノードは展開状態で作られるため、
/// 「複製で生まれた子が親を勝手に開く」ことはなく（親が閉じていれば親の下に隠れる）、
/// かつシーンを開いた直後の見え方も従来と変わらない。
///
/// 【安定キーの設計】
/// ノードの Id は DFS 通し番号なので、並べ替え・複製・削除で値がズレる ＝ キーに使えない。
/// そのためルートからの「名前パス」をキーにする:
///     /Root#0/Child#1/Grandchild#0
/// 各セグメントは「名前 ＋ 同名兄弟内での出現番号」で構成する。出現番号を付けるのは、
/// 同名の兄弟が並んでいても衝突しないようにするため。
/// この方式はリネームでキーが変わる（＝そのノードの折りたたみ状態を忘れる）が、
/// 目的である「複製・並べ替えで開いてしまう」問題には影響しない。
/// </summary>
public partial class HierarchyPanel
{
    // ── 安定キーの書式定数（マジック文字列を作らないための定義）────────────

    /// <summary>安定キーの階層区切り文字。</summary>
    private const string ExpandKeySeparator = "/";

    /// <summary>安定キーの「同名兄弟内での出現番号」を区切る文字。</summary>
    private const string ExpandKeyOccurrenceMark = "#";

    /// <summary>ルート階層の親キー（空文字＝親なし）。</summary>
    private const string ExpandKeyRootParent = "";

    // ── 退避データ ────────────────────────────────────────────────

    /// <summary>
    /// 現在「折りたたまれている」ノードの安定キー集合。
    /// ここに無いノードは展開扱い（既定）になる。
    ///
    /// シーンやアクター編集タブを切り替えても意図的にクリアしない。
    /// キーは名前パスなので別ツリーと衝突しにくく、同じシーンへ戻ったときに
    /// 折りたたみ状態が残っている方が使い勝手が良いため。
    /// </summary>
    private readonly HashSet<string> _collapsedKeys = new();

    // ── 初期化 ────────────────────────────────────────────────────

    /// <summary>
    /// ツリー全体の展開／折りたたみイベントを購読して <see cref="_collapsedKeys"/> を追従させる。
    /// TreeViewItem.Expanded/Collapsed はバブリングするため、個々の項目ではなく
    /// TreeView 側で一括購読し、発生元（OriginalSource）のノードを見て更新する。
    /// </summary>
    private void AttachExpansionTracking()
    {
        ActorTree.AddHandler(TreeViewItem.ExpandedEvent,  new RoutedEventHandler(OnTreeItemExpanded));
        ActorTree.AddHandler(TreeViewItem.CollapsedEvent, new RoutedEventHandler(OnTreeItemCollapsed));
    }

    /// <summary>ユーザーがノードを開いた（または RevealActor が開いた）: 折りたたみ記録から外す。</summary>
    private void OnTreeItemExpanded(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is TreeViewItem { Tag: ActorNode node } && node.StableKey.Length > 0)
            _collapsedKeys.Remove(node.StableKey);
    }

    /// <summary>ユーザーがノードを閉じた: 折りたたみとして記録する。</summary>
    private void OnTreeItemCollapsed(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is TreeViewItem { Tag: ActorNode node } && node.StableKey.Length > 0)
            _collapsedKeys.Add(node.StableKey);
    }

    // ── 安定キーの割り当て ────────────────────────────────────────

    /// <summary>
    /// ノード木へ安定キー（<see cref="ActorNode.StableKey"/>）を再帰的に割り当てる。
    /// ツリー再構築前と、ドラッグ&amp;ドロップの即時反映（親子付け替え）後に呼ぶ。
    /// </summary>
    /// <param name="nodes">同じ階層に並ぶノード群。</param>
    /// <param name="parentKey">親ノードの安定キー（ルート階層では空文字）。</param>
    private static void AssignStableKeys(List<ActorNode> nodes, string parentKey)
    {
        // 同名兄弟を区別するための出現カウンタ（名前 → 既出件数）
        var occurrences = new Dictionary<string, int>();
        foreach (var node in nodes)
        {
            occurrences.TryGetValue(node.Name, out int index);
            occurrences[node.Name] = index + 1;

            node.StableKey = parentKey
                           + ExpandKeySeparator
                           + node.Name
                           + ExpandKeyOccurrenceMark
                           + index.ToString(CultureInfo.InvariantCulture);

            AssignStableKeys(node.Children, node.StableKey);
        }
    }

    /// <summary>現在の <c>_roots</c> に対して安定キーを振り直す。</summary>
    private void RefreshStableKeys() => AssignStableKeys(_roots, ExpandKeyRootParent);

    // ── 復元 ──────────────────────────────────────────────────────

    /// <summary>
    /// ツリー構築時にこのノードを展開状態で作るべきかを返す。
    /// </summary>
    /// <param name="node">対象ノード。</param>
    /// <param name="forceExpandAll">
    /// 検索フィルタ中など、折りたたみを無視して全展開したい場合に true。
    /// 閉じたグループの中の一致ノードが見えなくなるのを防ぐため、検索時は強制展開する。
    /// </param>
    private bool ShouldExpandOnBuild(ActorNode node, bool forceExpandAll)
        => forceExpandAll || !_collapsedKeys.Contains(node.StableKey);
}
