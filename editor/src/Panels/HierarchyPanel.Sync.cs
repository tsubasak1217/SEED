using System.Collections.Generic;
using System.Windows.Controls;
using SEEDEditor.Panels.Hierarchy;

namespace SEEDEditor.Panels;

/// <summary>
/// HierarchyPanel の「差分更新（インクリメンタル同期）」まわり。
///
/// 同期アルゴリズムそのものは UI 非依存の <see cref="HierarchyTreeSync"/> が持ち、
/// ここは WPF の <see cref="ItemCollection"/>（TreeViewItem 群）を
/// <see cref="IHierarchySyncItems"/> として見せるアダプタだけを担当する。
/// こうしておくと「削除・挿入・並べ替えのあとに各項目が正しいノードへ束ね直されているか」を
/// WPF 無しの単体テスト（editor/tests/HierarchySyncTests）で検証できる。
/// </summary>
public partial class HierarchyPanel
{
    /// <summary>
    /// WPF の項目列（TreeViewItem の ItemCollection）を差分更新アルゴリズムへ渡すアダプタ。
    /// 項目の生成・束ね直しはパネル本体のメソッド（BuildTreeItem / UpdateItemForNode）へ委譲する。
    /// </summary>
    private sealed class TreeItemsAdapter : IHierarchySyncItems
    {
        /// <summary>対象となる 1 階層ぶんの項目列。</summary>
        private readonly ItemCollection _items;

        /// <summary>項目生成・束ね直しを委譲するパネル本体。</summary>
        private readonly HierarchyPanel _panel;

        public TreeItemsAdapter(ItemCollection items, HierarchyPanel panel)
        {
            _items = items;
            _panel = panel;
        }

        public int Count => _items.Count;

        /// <summary>
        /// 指定位置の項目が現在束ねているノードの安定キー。
        /// Tag が想定外（ノード未束縛）の項目は、どのキーにも一致しない値を返して
        /// 「消滅した項目」として末尾で回収させる。
        /// </summary>
        public string KeyAt(int index)
            => _items[index] is TreeViewItem { Tag: ActorNode node } ? node.StableKey : "";

        public void MoveTo(int from, int to)
        {
            var item = _items[from];
            _items.RemoveAt(from);
            _items.Insert(to, item);
        }

        public void InsertNew(int index, IHierarchySyncNode node)
        {
            // 差分更新は検索フィルタ非適用時のみ走るため、フィルタは空文字で構築する
            // （フィルタありのときは全再構築へ落ちる。ApplyHierarchyUpdate 参照）。
            // フィルタ空なら BuildTreeItem は必ず項目を返す。万一 null なら例外にして
            // 呼び出し側（ApplyHierarchyUpdate）の全再構築フォールバックへ落とす
            // ＝「挿入したつもりで抜けている」状態を静かに作らない。
            _items.Insert(index, _panel.BuildTreeItem((ActorNode)node, "")!);
        }

        public void RemoveAt(int index) => _items.RemoveAt(index);

        public void Bind(int index, IHierarchySyncNode node)
        {
            if (_items[index] is TreeViewItem item)
                _panel.UpdateItemForNode(item, (ActorNode)node);
        }

        public IHierarchySyncItems ChildrenAt(int index)
            => new TreeItemsAdapter(((TreeViewItem)_items[index]).Items, _panel);
    }

    /// <summary>
    /// 1 階層分の TreeViewItem 群を新しいノード列へ合わせ込む（実体は
    /// <see cref="HierarchyTreeSync.SyncLevel"/>）。
    /// </summary>
    private void SyncLevel(ItemCollection items, List<ActorNode> nodes)
        => HierarchyTreeSync.SyncLevel(new TreeItemsAdapter(items, this), nodes);
}
