// ============================================================
//  HierarchyPanel.Preview.cs — ヒエラルキーの画面プレビュー（右クリック・表示・読み取り専用）
//
//  【役割】（HierarchyPanel の部分クラス。正典は docs/editor_screen_preview.md §1・§3・§6）
//  - 表示: プレビュー（保存されない表示用のアクタ）の部分木の行を薄くし、根の行の名前の後に「（プレビュー）」を付ける。
//  - 右クリック: 項目の組み立ては WPF 非依存の Preview/PreviewMenuModel。ここは MenuItem へ写して、押されたら
//    MainWindow へ知らせる（窓を開く・送るのは MainWindow.ScreenPreview.cs。ここはランタイムへ直接送らない）。
//      普通のノード → 既存のメニューの末尾に区切り＋「プレハブをプレビュー」（最近使ったもの／プロジェクトから選ぶ...）
//      プレビューの中 → プレビューの項目だけ（既存の追加・コピー・削除・グループ・アクタファイル化は出さない）
//      空白 → プレビューがあれば「すべてのプレビューを消す」
//  - 読み取り専用: プレビューのノードは名前を変えない（F2・再クリック）、ドラッグを始めない（並べ替え・
//    プロジェクトへのアクタファイル化も）、プレビューの中へ落とさせない（ランタイムも断る）。
//  - 差し込み先の引き直し（TryResolvePreviewParent）と、Delete の振り分け（PlanPreviewDeletion）を MainWindow へ公開する。
//
//  閲覧専用（IsReadOnlyView）のときは既存どおり右クリックのメニュー自体が出ない（OnTreeRightMouseDown）。
//  プロジェクトパネルからのファイルのドロップでノードを親にする経路はヒエラルキーには無い（受けるのは "DragIds" だけ）。
// ============================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using SEEDEditor.Preview;

namespace SEEDEditor.Panels;

/// <summary>
/// ヒエラルキーのノードの画面プレビューの印（ActorNode の部分クラス）。
/// </summary>
public partial class ActorNode
{
    /// <summary>HIERARCHY の preview / preview_root / preview_source（旧 JSON はプレビューの外）。</summary>
    public HierarchyPreviewFlags PreviewFlags { get; set; } = HierarchyPreviewFlags.None;

    /// <summary>プレビューの部分木の中か（根を含む）。</summary>
    public bool IsPreview => PreviewFlags.IsPreview;

    /// <summary>プレビューの根か。</summary>
    public bool IsPreviewRoot => PreviewFlags.IsPreviewRoot;

    /// <summary>根の中身のプレハブ（根以外は null）。</summary>
    public string? PreviewSource => PreviewFlags.PreviewSource;
}

public partial class HierarchyPanel
{
    // ── 見た目（色は新しく決めない。継いだ色のまま薄くする）──────────

    /// <summary>プレビューの行の不透明度（非アクティブ・非表示の淡色と重なるときは掛け合わせる）。</summary>
    private const double PreviewRowOpacity = 0.6;

    /// <summary>プレビューの根の名前の後に付ける印。</summary>
    private const string PreviewRootSuffix = "（プレビュー）";

    /// <summary>根の印の文字の大きさ（名前の 13 より小さめ）。</summary>
    private const double PreviewRootSuffixFontSize = 11;

    // ── 文言 ────────────────────────────────────────────────

    /// <summary>プレビューの中へ落とそうとしたときの理由。</summary>
    private const string PreviewDropRejectReason = "プレビューの中へは動かせません（プレビューは保存されません）";

    /// <summary>差し込み先のタブが切り替わっていたときの理由。</summary>
    private const string PreviewTabChangedReason = "差し込み先のタブが切り替わりました。ヒエラルキーで選び直してください";

    /// <summary>差し込み先を見失ったときの理由の書式（{0} = 親の名前）。</summary>
    private const string PreviewParentLostFormat = "差し込み先の「{0}」が見つかりません（名前の変更・移動・削除）。ヒエラルキーで選び直してください";

    /// <summary>メニューの見出しで「_」をそのまま出すための置き換え（WPF はアクセスキーの印として食べる）。</summary>
    private const string AccessKeyMark = "_";

    /// <summary>「_」を文字として出すときの書き方。</summary>
    private const string EscapedAccessKeyMark = "__";

    // ── MainWindow から差し込む状態 ──────────────────────────────

    /// <summary>最近使ったプレハブ（assets:// 仮想パス。最近の順）を返す。</summary>
    private Func<IReadOnlyList<string>> _previewRecentPrefabs = () => Array.Empty<string>();

    /// <summary>Edit か（Play 中などはプレビューの項目を押せなくする）。</summary>
    private Func<bool> _previewIsEditMode = () => true;

    /// <summary>右クリックから出すプレビューの底上げ（差し込み先の案内の表の default_layer_bias）。</summary>
    private int _previewDefaultLayerBias = PreviewHostCatalog.BuiltInDefaultLayerBias;

    // ── MainWindow へ知らせる ─────────────────────────────────────

    /// <summary>プレハブをプレビューする（差し込み先。Prefab が null なら窓で選ぶ）。</summary>
    public event Action<PreviewInsertTarget>? PreviewPrefabRequested;

    /// <summary>そのノードを含むプレビューを消す（引数は右クリックしたノードの DFS 番号）。</summary>
    public event Action<int>? PreviewClearRequested;

    /// <summary>表示中のタブのプレビューを全部消す。</summary>
    public event Action? PreviewClearAllRequested;

    /// <summary>元のプレハブ（根の中身。assets:// 仮想パス or 絶対パス）を開く。</summary>
    public event Action<string>? PreviewSourceOpenRequested;

    /// <summary>
    /// 画面プレビューの右クリックに要る状態の取り出し口を差し込む（MainWindow.InitScreenPreview から）。
    /// </summary>
    /// <param name="recentPrefabs">最近使ったプレハブを返す。</param>
    /// <param name="isEditMode">Edit かを返す。</param>
    /// <param name="defaultLayerBias">右クリックから出すプレビューの底上げ。</param>
    public void ConfigureScreenPreview(Func<IReadOnlyList<string>> recentPrefabs, Func<bool> isEditMode, int defaultLayerBias)
    {
        _previewRecentPrefabs    = recentPrefabs;
        _previewIsEditMode       = isEditMode;
        _previewDefaultLayerBias = defaultLayerBias;
    }

    // ============================================================
    //  公開（MainWindow・インスペクタの差し込みから）
    // ============================================================

    /// <summary>表示中のタブの世界線（シーンは 0）。</summary>
    public uint ActiveWorldLine => _activeWorldLine;

    /// <summary>表示中の木にプレビューがあるか。</summary>
    public bool HasAnyPreview => GetAllNodes(_roots).Any(n => n.IsPreview);

    /// <summary>
    /// ノードがプレビューの部分木の中か（根を含む）。
    /// </summary>
    /// <param name="dfs">DFS 番号。</param>
    /// <returns>中なら true（木に無ければ false）。</returns>
    public bool IsPreviewNode(int dfs) => FindNode(_roots, dfs) is { IsPreview: true };

    /// <summary>
    /// ノードからいちばん近いプレビューの根（自分を含めて祖先をたどる）。
    /// </summary>
    /// <param name="dfs">DFS 番号。</param>
    /// <returns>根の DFS 番号と中身のプレハブ。プレビューの外なら null。</returns>
    public (int RootDfs, string? Source)? PreviewRootOf(int dfs)
    {
        var byId = GetAllNodes(_roots).ToDictionary(n => n.Id);
        var current = byId.GetValueOrDefault(dfs);
        while (current is { IsPreview: true })
        {
            if (current.IsPreviewRoot) return (current.Id, current.PreviewSource);
            current = current.ParentId is int parent ? byId.GetValueOrDefault(parent) : null;
        }
        return null;
    }

    /// <summary>
    /// 差し込み先の親をいまの木で引き直す（窓を開いている間に番号がずれても同じノードへ入れる）。
    /// 規則は TryRefreshTemplateActorTarget と同じ（タブが変わったら断る・安定キー → 無ければ番号と名前）。
    /// </summary>
    /// <param name="target">作った時点の差し込み先。</param>
    /// <returns>いまの親の番号、または見失った理由。</returns>
    public PreviewParentResolution TryResolvePreviewParent(PreviewInsertTarget target)
    {
        // 別のタブ（アクタ編集・キャンバス編集）へ切り替わっていたら、同じ名前の別の木を掴まない
        if (target.WorldLine != _activeWorldLine) return new(null, PreviewTabChangedReason);

        var node = string.IsNullOrEmpty(target.ParentStableKey)
            ? GetAllNodes(_roots).FirstOrDefault(n => n.Id == target.ParentDfs && n.Name == target.ParentName)
            : GetAllNodes(_roots).FirstOrDefault(n => n.StableKey == target.ParentStableKey);
        return node is null
            ? new(null, string.Format(PreviewParentLostFormat, target.ParentName))
            : new(node.Id, null);
    }

    /// <summary>
    /// 番号だけで作った差し込み先（インスペクタの案内から）へ、表示中のタブ・親の名前・安定キーを埋める。
    /// </summary>
    /// <param name="target">番号（ParentDfs）だけが決まっている差し込み先。</param>
    /// <returns>埋めた差し込み先（親が木に無ければタブだけ埋める）。</returns>
    public PreviewInsertTarget DescribePreviewParent(PreviewInsertTarget target)
    {
        var node = FindNode(_roots, target.ParentDfs);
        return node is null
            ? target with { WorldLine = _activeWorldLine }
            : target with { WorldLine = _activeWorldLine, ParentName = node.Name, ParentStableKey = node.StableKey };
    }

    /// <summary>
    /// Delete で消す選択を、プレビューの根・中・普通のノードに分ける（番号のずれも計算する）。
    /// </summary>
    /// <param name="selectedIds">選択（DFS 番号）。</param>
    /// <returns>分けた結果。</returns>
    public PreviewDeletionPlan PlanPreviewDeletion(IReadOnlyList<int> selectedIds) =>
        PreviewDeletionPlanner.Plan(
            selectedIds,
            GetAllNodes(_roots).Select(n => new PreviewTreeNode(n.Id, n.ParentId, n.IsPreview, n.IsPreviewRoot)));

    // ============================================================
    //  表示
    // ============================================================

    /// <summary>行の見た目に効くプレビューの印が変わったか（差分更新で見出しを作り直す判定）。</summary>
    /// <param name="a">前のノード。</param>
    /// <param name="b">新しいノード。</param>
    /// <returns>変わったら true。</returns>
    private static bool PreviewHeaderDiffers(ActorNode a, ActorNode b) =>
        a.IsPreview != b.IsPreview || a.IsPreviewRoot != b.IsPreviewRoot;

    /// <summary>
    /// プレビューの行を薄くし、根の名前の後に「（プレビュー）」を付ける（色は継いだまま）。
    /// </summary>
    /// <param name="header">行の見出し（BuildItemHeader が作ったもの）。</param>
    /// <param name="node">行のノード。</param>
    private static void ApplyPreviewHeaderStyle(TextBlock header, ActorNode node)
    {
        if (!node.IsPreview) return;
        if (node.IsPreviewRoot)
            header.Inlines.Add(new Run(PreviewRootSuffix) { FontSize = PreviewRootSuffixFontSize });
        // 非アクティブ・非表示の淡色が先に入っていれば掛け合わせる
        header.Opacity *= PreviewRowOpacity;
    }

    // ============================================================
    //  右クリック
    // ============================================================

    /// <summary>
    /// 右クリックしたノードがプレビューの中なら、プレビューの項目だけのメニューを作る。
    /// </summary>
    /// <returns>メニュー（プレビューの外なら null = 既存のメニュー）。</returns>
    private ContextMenu? TryBuildPreviewNodeMenu()
    {
        if (_rightClickedNode is not { IsPreview: true } node) return null;
        var model = PreviewMenuModel.Build(CreatePreviewMenuInput(node));
        if (model.Placement != PreviewMenuPlacement.Replace) return null;

        var menu = new ContextMenu();
        foreach (var entry in model.Entries) menu.Items.Add(ToMenuElement(entry, node));
        return menu;
    }

    /// <summary>普通のノードの右クリックの末尾へ、区切り＋プレビューの項目を足す。</summary>
    /// <param name="menu">既存のメニュー。</param>
    private void AppendPreviewNodeItems(ContextMenu menu) => AppendPreviewItems(menu, _rightClickedNode);

    /// <summary>空白の右クリックの末尾へ、プレビューがあれば「すべてのプレビューを消す」を足す。</summary>
    /// <param name="menu">既存のメニュー。</param>
    private void AppendPreviewBlankItems(ContextMenu menu) => AppendPreviewItems(menu, node: null);

    /// <summary>既存のメニューの末尾へ、区切り＋プレビューの項目を足す（足すものが無ければ何もしない）。</summary>
    /// <param name="menu">既存のメニュー。</param>
    /// <param name="node">右クリックしたノード（空白なら null）。</param>
    private void AppendPreviewItems(ContextMenu menu, ActorNode? node)
    {
        var model = PreviewMenuModel.Build(CreatePreviewMenuInput(node));
        if (model.Placement != PreviewMenuPlacement.Append || model.Entries.Count == 0) return;
        menu.Items.Add(new Separator());
        foreach (var entry in model.Entries) menu.Items.Add(ToMenuElement(entry, node));
    }

    /// <summary>
    /// 右クリックした場所と状態から、項目の組み立ての材料を作る。
    /// </summary>
    /// <param name="node">右クリックしたノード（空白なら null）。</param>
    /// <returns>材料。</returns>
    private PreviewMenuInput CreatePreviewMenuInput(ActorNode? node)
    {
        var root = node is { IsPreview: true } ? PreviewRootOf(node.Id) : null;
        return new PreviewMenuInput
        {
            HasNode           = node is not null,
            NodeInPreview     = node?.IsPreview == true,
            NodeIsPreviewRoot = node?.IsPreviewRoot == true,
            NearestRootSource = root?.Source,
            TreeHasPreview    = HasAnyPreview,
            RecentPrefabs     = _previewRecentPrefabs(),
            IsEditMode        = _previewIsEditMode(),
        };
    }

    /// <summary>
    /// 項目 1 つを WPF のメニュー要素へ写す（子があれば再帰）。
    /// </summary>
    /// <param name="entry">項目。</param>
    /// <param name="node">右クリックしたノード（空白なら null）。</param>
    /// <returns>メニュー要素（区切りは Separator）。</returns>
    private Control ToMenuElement(PreviewMenuEntry entry, ActorNode? node)
    {
        if (entry.Kind == PreviewMenuEntryKind.Separator) return new Separator();

        var item = new MenuItem
        {
            // ファイル名の「_」をアクセスキーの印として食べさせない
            Header    = entry.Header.Replace(AccessKeyMark, EscapedAccessKeyMark),
            IsEnabled = entry.IsEnabled,
            ToolTip   = entry.ToolTip,
        };
        // 押せない理由はツールチップで示す（無効な項目でも出す）
        ToolTipService.SetShowOnDisabled(item, true);

        if (entry.Kind == PreviewMenuEntryKind.SubMenu)
        {
            foreach (var child in entry.Children) item.Items.Add(ToMenuElement(child, node));
        }
        else
        {
            item.Click += (_, _) => ExecutePreviewCommand(entry, node);
        }
        return item;
    }

    /// <summary>
    /// 押された項目のコマンドを実行する（MainWindow へ知らせる）。
    /// </summary>
    /// <param name="entry">押された項目。</param>
    /// <param name="node">右クリックしたノード（空白なら null）。</param>
    private void ExecutePreviewCommand(PreviewMenuEntry entry, ActorNode? node)
    {
        if (!entry.IsEnabled) return;
        switch (entry.Command)
        {
            case PreviewMenuCommand.PickPrefab when node is not null:
                PreviewPrefabRequested?.Invoke(CreatePreviewTarget(node, prefab: null));
                break;
            case PreviewMenuCommand.PreviewRecent when node is not null && entry.Argument is not null:
                PreviewPrefabRequested?.Invoke(CreatePreviewTarget(node, entry.Argument));
                break;
            case PreviewMenuCommand.ClearThis when node is not null:
                PreviewClearRequested?.Invoke(node.Id);
                break;
            case PreviewMenuCommand.ClearAll:
                PreviewClearAllRequested?.Invoke();
                break;
            case PreviewMenuCommand.OpenSource when entry.Argument is not null:
                PreviewSourceOpenRequested?.Invoke(entry.Argument);
                break;
        }
    }

    /// <summary>
    /// 右クリックしたノードの直下へ入れる差し込み先を作る（枠なし・底上げは表の default_layer_bias）。
    /// </summary>
    /// <param name="parent">親（右クリックしたノード）。</param>
    /// <param name="prefab">中身のプレハブ（null なら窓で選ぶ）。</param>
    /// <returns>差し込み先。</returns>
    private PreviewInsertTarget CreatePreviewTarget(ActorNode parent, string? prefab) => new()
    {
        WorldLine       = _activeWorldLine,
        ParentDfs       = parent.Id,
        ParentName      = parent.Name,
        ParentStableKey = parent.StableKey,
        LayerBias       = _previewDefaultLayerBias,
        Prefab          = prefab,
        Label           = PreviewInsertTarget.DescribeLabel(parent.Name, under: "", framed: false),
    };

    // ============================================================
    //  読み取り専用（ドラッグ・ドロップ）
    // ============================================================

    /// <summary>
    /// 押したノードからドラッグを始めると、プレビューのノードを運ぶことになるか
    /// （押したノードが選択に含まれていれば選択全体、そうでなければ押したノードだけを運ぶ。OnTreeMouseMove と同じ規則）。
    /// </summary>
    /// <param name="pressed">押したノード。</param>
    /// <returns>プレビューのノードを含むなら true（ドラッグを始めない）。</returns>
    private bool DragWouldIncludePreview(ActorNode pressed)
    {
        if (pressed.IsPreview) return true;
        return _selectedIds.Contains(pressed.Id) && _selectedIds.Any(IsPreviewNode);
    }

    /// <summary>
    /// 新しい親がプレビューの中なら、ドロップを断って理由を出す。
    /// </summary>
    /// <param name="e">ドラッグの引数。</param>
    /// <param name="newParent">落とすと親になるノード（ルートなら null）。</param>
    /// <param name="posInTree">カーソルの位置（ツリー基準。理由の吹き出しの位置）。</param>
    /// <returns>断ったら true。</returns>
    private bool RejectDropIntoPreview(DragEventArgs e, ActorNode? newParent, Point posInTree)
    {
        if (newParent is not { IsPreview: true }) return false;
        RejectDrop(e, PreviewDropRejectReason, posInTree);
        return true;
    }
}
