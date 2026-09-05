using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using SEEDEditor.Settings;

namespace SEEDEditor.Panels;

/// <summary>
/// HierarchyPanel の「ツリー展開状態（折りたたみ）の保持と永続化」機能。
///
/// Hierarchy は Rust ランタイムから HIERARCHY を受けるたびに TreeViewItem を作り直す
/// （RebuildTree。差分更新が効く場合は SyncLevel で再利用する）。TreeViewItem を新規生成する
/// 経路では素の IsExpanded 初期値しか持たないため、そのままでは複製・並べ替えのたびに
/// 開閉状態が失われる。そこで「展開されているノードの安定キー集合」をパネル側に退避し、
/// 構築のたびに復元する。
///
/// 【なぜ「折りたたみ」ではなく「展開」を覚えるのか】
/// 既定は折りたたみである。深い階層のシーンを開いた直後に全ノードが展開されていると
/// 目的のアクタを探せないため、「明示的に開いたものだけを覚えて開く」方が実用的で、
/// かつ新規に現れたノード（複製・スクリプトの Instantiate）が勝手に開くこともない。
///
/// 【永続化】
/// 展開キー集合は <see cref="EditorViewState"/> 経由で editor/settings/view_state.json へ
/// シーン単位で保存する。シーンを開き直しても・エディタを再起動しても復元される。
/// 保存対象のシーンは <see cref="SetSceneViewKey"/> で MainWindow から指定する。
///
/// 【安定キーの設計】
/// ノードの Id は DFS 通し番号なので、並べ替え・複製・削除で値がズレる ＝ キーに使えない。
/// そのためルートからの「名前パス」をキーにする:
///     /Root#0/Child#1/Grandchild#0
/// 各セグメントは「名前 ＋ 同名兄弟内での出現番号」で構成する。出現番号を付けるのは、
/// 同名の兄弟が並んでいても衝突しないようにするため。
/// この方式はリネームでキーが変わる（＝そのノードの展開状態を忘れる）が、
/// 目的である「複製・並べ替え・再起動で開閉が壊れる」問題には影響しない。
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
    /// 現在「展開されている」ノードの安定キー集合。
    /// ここに無いノードは折りたたみ扱い（既定）になる。
    /// </summary>
    private readonly HashSet<string> _expandedKeys = new(StringComparer.Ordinal);

    /// <summary>
    /// 展開状態の保存先シーンキー（<see cref="EditorViewState.MakeSceneKey"/> の戻り値）。
    /// null は「未保存の新規シーン」等で永続化しない状態を表す（セッション内では保持される）。
    /// </summary>
    private string? _expandSceneKey;

    /// <summary>
    /// 次回のヒエラルキー反映で差分更新を使わず全再構築させるフラグ。
    /// シーン切り替え直後は「既存 TreeViewItem のライブな開閉状態」ではなく
    /// 新しいシーンの保存値を使わなければならないため、ここで一度作り直す。
    /// </summary>
    private bool _forceFullTreeRebuild;

    // ── 初期化 ────────────────────────────────────────────────────

    /// <summary>
    /// ツリー全体の展開／折りたたみイベントを購読して <see cref="_expandedKeys"/> を追従させる。
    /// TreeViewItem.Expanded/Collapsed はバブリングするため、個々の項目ではなく
    /// TreeView 側で一括購読し、発生元（OriginalSource）のノードを見て更新する。
    /// </summary>
    private void AttachExpansionTracking()
    {
        ActorTree.AddHandler(TreeViewItem.ExpandedEvent,  new RoutedEventHandler(OnTreeItemExpanded));
        ActorTree.AddHandler(TreeViewItem.CollapsedEvent, new RoutedEventHandler(OnTreeItemCollapsed));
    }

    /// <summary>ユーザーがノードを開いた（または RevealActor が開いた）: 展開として記録する。</summary>
    private void OnTreeItemExpanded(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is TreeViewItem { Tag: ActorNode node } && node.StableKey.Length > 0
            && _expandedKeys.Add(node.StableKey))
            PersistExpansion();
    }

    /// <summary>ユーザーがノードを閉じた: 展開記録から外す。</summary>
    private void OnTreeItemCollapsed(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is TreeViewItem { Tag: ActorNode node } && node.StableKey.Length > 0
            && _expandedKeys.Remove(node.StableKey))
            PersistExpansion();
    }

    // ── 永続化 ────────────────────────────────────────────────────

    /// <summary>
    /// 表示中シーンの展開状態を切り替える。MainWindow がシーンを読み込んだ直後に呼ぶ。
    ///
    /// 直前のシーンの状態はすでに <see cref="PersistExpansion"/> で保存済みなので、
    /// ここでは新しいシーンの保存値を読み直して、次の反映で全再構築させるだけでよい。
    /// </summary>
    /// <param name="scenePath">開いたシーンファイルのパス（未保存なら null）。</param>
    public void SetSceneViewKey(string? scenePath)
    {
        var key = EditorViewState.MakeSceneKey(scenePath);
        if (string.Equals(key, _expandSceneKey, StringComparison.Ordinal)) return;

        _expandSceneKey = key;
        _expandedKeys.Clear();
        // 保存が無いシーン（初めて開く／未保存の新規シーン）は「すべて折りたたみ」で始める。
        var saved = EditorViewState.TryGetScene(key);
        if (saved is not null)
            foreach (var k in saved.HierarchyExpanded) _expandedKeys.Add(k);

        _forceFullTreeRebuild = true;
    }

    /// <summary>
    /// 展開状態を破棄せずに保存先シーンキーだけを付け替える。
    /// 「名前を付けて保存」でシーンのパスが変わったときに使う（保存しただけでツリーが
    /// 畳まれるのを避けるため、現在の展開状態をそのまま新しいキーへ引き継ぐ）。
    /// </summary>
    /// <param name="scenePath">新しいシーンファイルのパス。</param>
    public void MoveSceneViewKey(string? scenePath)
    {
        var key = EditorViewState.MakeSceneKey(scenePath);
        if (string.Equals(key, _expandSceneKey, StringComparison.Ordinal)) return;

        _expandSceneKey = key;
        PersistExpansion();
    }

    /// <summary>
    /// 現在の展開キー集合をビュー状態ストアへ書き戻し、保存を予約する（デバウンス）。
    /// </summary>
    private void PersistExpansion()
    {
        var entry = EditorViewState.GetOrCreateScene(_expandSceneKey);
        if (entry is null) return;   // 未保存シーンはセッション内保持のみ
        // 差分順で並ぶと毎回ファイル内容が入れ替わって差分が読みづらいので整列して書く
        entry.HierarchyExpanded = _expandedKeys.OrderBy(k => k, StringComparer.Ordinal).ToList();
        EditorViewState.RequestSave();
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
    /// 保存（またはセッション中の操作）で展開と記録されたノードだけを開く。
    /// </summary>
    /// <param name="node">対象ノード。</param>
    /// <param name="forceExpandAll">
    /// 検索フィルタ中など、保存状態を無視して全展開したい場合に true。
    /// 閉じたグループの中の一致ノードが見えなくなるのを防ぐため、検索時は強制展開する。
    /// </param>
    private bool ShouldExpandOnBuild(ActorNode node, bool forceExpandAll)
        => forceExpandAll || _expandedKeys.Contains(node.StableKey);
}
