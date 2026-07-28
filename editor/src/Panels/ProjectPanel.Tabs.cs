using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace SEEDEditor.Panels;

/// <summary>
/// プロジェクトパネル 1 タブぶんの「開いている場所」の状態。
///
/// タブはビューを複製せず、状態だけを保持する（軽量な状態切替方式）。
/// ツリー／ファイルグリッドの実体は常に 1 組で、タブ切替時に
/// 「フォルダ位置・ツリー展開集合・選択・スクロール位置」を差し替える。
/// </summary>
internal sealed class ProjectFolderTab
{
    /// <summary>このタブが開いているフォルダの絶対パス。</summary>
    public string CurrentPath { get; set; } = "";

    /// <summary>左のフォルダツリーで展開されていたフォルダの絶対パス集合。</summary>
    public HashSet<string> ExpandedPaths { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>ファイル一覧で選択されていたアイテムの絶対パス（未選択なら null）。</summary>
    public string? SelectedPath { get; set; }

    /// <summary>ファイル一覧の垂直スクロール位置。</summary>
    public double ScrollOffset { get; set; }
}

/// <summary>
/// ProjectPanel のタブ機構（改修2）。
/// パネル上部ツールバー左側に「開いているフォルダ位置」を表すタブを並べ、
/// クリックで即座に切り替えられるようにする。永続化はせずセッション限りの状態。
/// </summary>
public partial class ProjectPanel
{
    // ── タブ表示の定数（マジックナンバー回避） ────────────────────

    /// <summary>ルートフォルダのタブに表示する名前。</summary>
    private const string RootTabTitle = "Assets";

    /// <summary>タブが最低限保持される枚数（これ以下にはできない＝最後の 1 枚は閉じられない）。</summary>
    private const int MinTabCount = 1;

    /// <summary>タブ 1 枚の最大幅（長いフォルダ名は省略記号で切る）。</summary>
    private const double TabMaxWidth = 160.0;

    /// <summary>タブ本体のパディング。</summary>
    private static readonly Thickness TabPadding = new(8, 2, 4, 2);

    /// <summary>タブ同士の間隔。</summary>
    private static readonly Thickness TabMargin = new(0, 0, 2, 0);

    /// <summary>タブ名のフォントサイズ。</summary>
    private const double TabFontSize = 11.0;

    /// <summary>閉じるボタン「×」のフォントサイズ。</summary>
    private const double TabCloseFontSize = 11.0;

    /// <summary>タブ列末尾に置く「新しいタブ」ボタン（＋）のフォントサイズ。閉じるボタンより一回り大きくして視認性を確保する。</summary>
    private const double NewTabButtonFontSize = 13.0;

    /// <summary>「新しいタブ」ボタンのツールチップ文言。</summary>
    private const string NewTabButtonToolTip = "新しいタブ";

    /// <summary>「新しいタブ」ボタンの左マージン（直前のタブとの間隔）。</summary>
    private static readonly Thickness NewTabButtonMargin = new(4, 0, 0, 0);

    /// <summary>タブ角丸の半径。</summary>
    private static readonly CornerRadius TabCornerRadius = new(3, 3, 0, 0);

    // 配色（ツールバー #252525 の上に載る前提のダークテーマ）
    private static readonly Brush TabActiveBackground   = new SolidColorBrush(Color.FromRgb(0x1E, 0x2D, 0x40));
    private static readonly Brush TabActiveForeground   = new SolidColorBrush(Color.FromRgb(0x80, 0xBB, 0xEE));
    private static readonly Brush TabActiveBorder       = new SolidColorBrush(Color.FromRgb(0x3A, 0x6A, 0x9A));
    private static readonly Brush TabInactiveBackground = new SolidColorBrush(Color.FromRgb(0x2C, 0x2C, 0x2C));
    private static readonly Brush TabInactiveForeground = new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99));
    private static readonly Brush TabInactiveBorder     = new SolidColorBrush(Color.FromRgb(0x3C, 0x3C, 0x3C));
    private static readonly Brush TabCloseForeground    = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));

    private static readonly Thickness TabBorderThickness = new(1, 1, 1, 0);

    // ── 状態 ──────────────────────────────────────────────────────

    /// <summary>開いているタブ（左から順）。常に 1 枚以上ある。</summary>
    private readonly List<ProjectFolderTab> _tabs = new();

    /// <summary>アクティブタブの添字。_tabs が空のときだけ -1。</summary>
    private int _activeTabIndex = -1;

    /// <summary>アクティブタブ（無ければ null）。</summary>
    private ProjectFolderTab? ActiveTab
        => _activeTabIndex >= 0 && _activeTabIndex < _tabs.Count ? _tabs[_activeTabIndex] : null;

    // ── 初期化・フォルダ移動の反映 ────────────────────────────────

    /// <summary>
    /// タブ機構を初期化する。アセットルートを開いたタブ 1 枚だけの状態にする。
    /// SetAssetsPath から呼ばれる（プロジェクト切替時は状態を作り直す）。
    /// </summary>
    private void InitTabs(string rootPath)
    {
        _tabs.Clear();
        _tabs.Add(new ProjectFolderTab { CurrentPath = rootPath });
        _activeTabIndex = 0;
        RebuildTabBar();
    }

    /// <summary>
    /// フォルダ移動（NavigateTo）が起きたときにアクティブタブへ反映する。
    /// タブ＝「開いているフォルダ位置」なので、移動するとタブ名も追従して変わる。
    /// </summary>
    private void OnActiveFolderChanged(string newPath)
    {
        var tab = ActiveTab;
        if (tab == null) return;
        if (PathEquals(tab.CurrentPath, newPath)) return;

        tab.CurrentPath = newPath;
        // フォルダが変わったので、前のフォルダで選択していたアイテムは持ち越さない
        tab.SelectedPath = null;
        tab.ScrollOffset = 0;
        RebuildTabBar();
    }

    // ── タブバーの構築 ────────────────────────────────────────────

    /// <summary>タブバー（ツールバー左側の StackPanel）を現在の _tabs から作り直す。末尾に「新しいタブ」ボタンを添える。</summary>
    private void RebuildTabBar()
    {
        TabBar.Children.Clear();
        for (int i = 0; i < _tabs.Count; i++)
            TabBar.Children.Add(BuildTabChip(_tabs[i], isActive: i == _activeTabIndex));
        TabBar.Children.Add(BuildNewTabButton());
    }

    /// <summary>
    /// タブ列末尾に置く「＋」ボタンの見た目を組み立てる。
    /// 閉じるボタン（×）と同系の軽量な描画（TextBlock＋クリックハンドラ）にして、
    /// タブそのものと紛れないよう控えめなスタイルに留める。
    /// StackPanel の子として TabBar 内に置くため、タブが増減してもタブ列の末尾に追従し、
    /// タブバーの横スクロールにも自然に含まれる。
    /// </summary>
    private UIElement BuildNewTabButton()
    {
        var plus = new TextBlock
        {
            Text              = "+",
            FontSize          = NewTabButtonFontSize,
            Foreground        = TabCloseForeground,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
        };

        var btn = new Border
        {
            Width           = 20,
            Height          = 20,
            CornerRadius    = TabCornerRadius,
            Margin          = NewTabButtonMargin,
            Cursor          = Cursors.Hand,
            ToolTip         = NewTabButtonToolTip,
            Background      = Brushes.Transparent,
            Child           = plus,
        };
        btn.MouseEnter += (_, _) => btn.Background = TabInactiveBackground;
        btn.MouseLeave += (_, _) => btn.Background = Brushes.Transparent;
        btn.MouseLeftButtonDown += (_, e) =>
        {
            OpenNewTabAtRoot();
            e.Handled = true;
        };

        return btn;
    }

    /// <summary>タブ 1 枚の見た目（フォルダ名＋閉じるボタン）を組み立てる。</summary>
    private UIElement BuildTabChip(ProjectFolderTab tab, bool isActive)
    {
        var title = new TextBlock
        {
            Text              = GetTabTitle(tab),
            FontSize          = TabFontSize,
            Foreground        = isActive ? TabActiveForeground : TabInactiveForeground,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming      = TextTrimming.CharacterEllipsis,
            MaxWidth          = TabMaxWidth,
        };

        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(title);

        // 閉じるボタンは最後の 1 枚には出さない（閉じられないことを見た目でも示す）
        if (_tabs.Count > MinTabCount)
        {
            var closeBtn = new TextBlock
            {
                Text              = "×",
                FontSize          = TabCloseFontSize,
                Foreground        = TabCloseForeground,
                Margin            = new Thickness(6, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Cursor            = Cursors.Hand,
                ToolTip           = "このタブを閉じる",
            };
            closeBtn.MouseLeftButtonDown += (_, e) =>
            {
                CloseTab(tab);
                e.Handled = true;   // タブ本体のクリック（アクティブ化）へ伝播させない
            };
            content.Children.Add(closeBtn);
        }

        var chip = new Border
        {
            Background      = isActive ? TabActiveBackground : TabInactiveBackground,
            BorderBrush     = isActive ? TabActiveBorder     : TabInactiveBorder,
            BorderThickness = TabBorderThickness,
            CornerRadius    = TabCornerRadius,
            Padding         = TabPadding,
            Margin          = TabMargin,
            Cursor          = Cursors.Hand,
            ToolTip         = tab.CurrentPath,
            Child           = content,
        };

        chip.MouseLeftButtonDown += (_, e) =>
        {
            ActivateTab(_tabs.IndexOf(tab));
            e.Handled = true;
        };
        // 中クリックでも閉じられるようにする（ブラウザ流の操作感）
        chip.MouseDown += (_, e) =>
        {
            if (e.ChangedButton != MouseButton.Middle) return;
            CloseTab(tab);
            e.Handled = true;
        };

        return chip;
    }

    /// <summary>タブに表示する名前を返す。アセットルートは "Assets"、それ以外はフォルダ名。</summary>
    private string GetTabTitle(ProjectFolderTab tab)
    {
        if (string.IsNullOrEmpty(tab.CurrentPath)) return RootTabTitle;
        if (!string.IsNullOrEmpty(_assetsRoot) && PathEquals(tab.CurrentPath, _assetsRoot)) return RootTabTitle;
        var name = Path.GetFileName(tab.CurrentPath.TrimEnd(Path.DirectorySeparatorChar));
        return string.IsNullOrEmpty(name) ? RootTabTitle : name;
    }

    // ── タブ操作 ──────────────────────────────────────────────────

    /// <summary>指定添字のタブをアクティブにする（現タブの状態は退避してから切り替える）。</summary>
    private void ActivateTab(int index)
    {
        if (index < 0 || index >= _tabs.Count) return;
        if (index == _activeTabIndex) return;

        CaptureActiveTabState();
        _activeTabIndex = index;
        RebuildTabBar();
        ApplyTabState(_tabs[index]);
    }

    /// <summary>
    /// タブを閉じる。最後の 1 枚は閉じられない。
    /// アクティブタブを閉じた場合は左隣（無ければ右隣）をアクティブにする。
    /// </summary>
    private void CloseTab(ProjectFolderTab tab)
    {
        if (_tabs.Count <= MinTabCount) return;

        int index = _tabs.IndexOf(tab);
        if (index < 0) return;

        bool wasActive = index == _activeTabIndex;
        _tabs.RemoveAt(index);

        if (wasActive)
        {
            // 閉じた位置の左隣を選ぶ（先頭を閉じたときは新しい先頭＝元の右隣）
            _activeTabIndex = Math.Max(0, index - 1);
            RebuildTabBar();
            ApplyTabState(_tabs[_activeTabIndex]);
        }
        else
        {
            // 閉じたタブより後ろがアクティブだった場合は添字がずれる
            if (index < _activeTabIndex) _activeTabIndex--;
            RebuildTabBar();
        }
    }

    /// <summary>
    /// 指定フォルダを開くタブをアクティブにする。
    /// 同じフォルダを開いているタブが既にあればそれをアクティブ化し、無ければ新規タブを作る。
    /// </summary>
    /// <param name="folderPath">開くフォルダの絶対パス。</param>
    /// <param name="selectPath">開いた後に選択状態にするファイル/フォルダの絶対パス（不要なら null）。</param>
    private void OpenFolderInTab(string folderPath, string? selectPath)
    {
        int existing = _tabs.FindIndex(t => PathEquals(t.CurrentPath, folderPath));
        if (existing >= 0)
        {
            // 既存タブへ切り替える。選択指定があれば反映する。
            if (existing == _activeTabIndex)
            {
                // すでにそのタブがアクティブ → 選択だけ更新する
                _pendingSelectPath = selectPath;
                RefreshFileGrid();
            }
            else
            {
                _tabs[existing].SelectedPath = selectPath;
                ActivateTab(existing);
            }
            return;
        }

        // 新規タブ: 現在のタブ状態を退避してから末尾に追加してアクティブ化する
        CaptureActiveTabState();
        _tabs.Add(new ProjectFolderTab { CurrentPath = folderPath, SelectedPath = selectPath });
        _activeTabIndex = _tabs.Count - 1;
        RebuildTabBar();
        ApplyTabState(_tabs[_activeTabIndex]);
    }

    /// <summary>
    /// タブ列の「＋」ボタンから呼ばれる。アセットルートを開く新規タブを常に新しく作ってアクティブ化する。
    /// OpenFolderInTab の「同じ場所は既存タブをアクティブ化する」ルールの例外として、
    /// ルートタブが既に存在していても構わず新規タブを積む。
    /// </summary>
    private void OpenNewTabAtRoot()
    {
        if (string.IsNullOrEmpty(_assetsRoot)) return;

        CaptureActiveTabState();
        _tabs.Add(new ProjectFolderTab { CurrentPath = _assetsRoot });
        _activeTabIndex = _tabs.Count - 1;
        RebuildTabBar();
        ApplyTabState(_tabs[_activeTabIndex]);
    }

    // ── 状態の退避と復元 ──────────────────────────────────────────

    /// <summary>アクティブタブへ現在のビュー状態（フォルダ・展開・選択・スクロール）を書き戻す。</summary>
    private void CaptureActiveTabState()
    {
        var tab = ActiveTab;
        if (tab == null) return;

        tab.CurrentPath   = _currentPath;
        tab.ExpandedPaths = CollectExpandedFolders();
        tab.SelectedPath  = GetSelectedPaths().FirstOrDefault();
        tab.ScrollOffset  = FileScrollViewer.VerticalOffset;
    }

    /// <summary>タブの状態をビューへ適用する（ツリー展開 → グリッド更新 → スクロール復元）。</summary>
    private void ApplyTabState(ProjectFolderTab tab)
    {
        // フォルダが消えている場合はルートへフォールバックする
        var target = Directory.Exists(tab.CurrentPath) ? tab.CurrentPath : _assetsRoot;
        tab.CurrentPath = target;
        _currentPath    = target;

        RestoreTreeExpansion(tab);

        _pendingSelectPath = tab.SelectedPath;
        RefreshFileGrid();
        SyncTreeSelection(target);
        RebuildTabBar();   // フォールバックでタブ名が変わる可能性があるため作り直す

        // スクロール位置はレイアウト確定後でないと反映されないため遅延実行する
        var offset = tab.ScrollOffset;
        Dispatcher.BeginInvoke(() => FileScrollViewer.ScrollToVerticalOffset(offset),
            System.Windows.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>アクティブタブの展開集合をツリーへ再適用する（ツリー再構築後の復旧用）。</summary>
    private void RestoreActiveTabTreeExpansion()
    {
        var tab = ActiveTab;
        if (tab != null) RestoreTreeExpansion(tab);
    }

    /// <summary>
    /// タブが記録している展開集合＋開いているフォルダまでの経路をツリー上で展開する。
    /// 浅い階層から順に処理しないと、遅延生成の子ノードがまだ存在せず展開できないため
    /// パス区切りの数（階層の深さ）で昇順に並べてから適用する。
    /// </summary>
    private void RestoreTreeExpansion(ProjectFolderTab tab)
    {
        // このタブで展開されているべきフォルダ集合
        // ＝記録済みの展開集合 ∪「開いているフォルダまでの経路（祖先）」。
        // 祖先を含めるのは、選択中フォルダがツリー上で見えないと状態が伝わらないため。
        var wanted = new HashSet<string>(tab.ExpandedPaths, StringComparer.OrdinalIgnoreCase);
        foreach (var ancestor in EnumerateAncestorFolders(tab.CurrentPath))
            wanted.Add(ancestor);

        _suppressTreeEvent = true;
        try
        {
            foreach (var path in wanted.OrderBy(GetDepth))
            {
                var node = ExpandToFolder(path);
                if (node != null) node.IsExpanded = true;
            }
            // 他タブで展開した余分なノードは畳んで、タブごとの見え方を独立させる
            CollapseUnwanted(FolderTree.Items, wanted);
        }
        finally { _suppressTreeEvent = false; }
    }

    /// <summary>
    /// アセットルートから <paramref name="folderPath"/> の親までの祖先フォルダを浅い順に列挙する。
    /// folderPath 自身は含めない（開いているフォルダを展開して子フォルダまで開く必要はないため）。
    /// </summary>
    private IEnumerable<string> EnumerateAncestorFolders(string folderPath)
    {
        if (string.IsNullOrEmpty(folderPath) || string.IsNullOrEmpty(_assetsRoot)) yield break;
        if (!IsUnderAssets(folderPath)) yield break;

        yield return _assetsRoot;
        if (PathEquals(folderPath, _assetsRoot)) yield break;

        var rel  = Path.GetRelativePath(_assetsRoot, folderPath);
        var segs = rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar,
                             StringSplitOptions.RemoveEmptyEntries);

        var acc = _assetsRoot;
        // 末尾（folderPath 自身）は返さないので Length - 1 まで
        for (int i = 0; i < segs.Length - 1; i++)
        {
            acc = Path.Combine(acc, segs[i]);
            yield return acc;
        }
    }

    /// <summary>
    /// 展開されているべき集合に含まれないノードを畳む（アセットルートは常に開いたままにする）。
    /// </summary>
    private void CollapseUnwanted(ItemCollection items, HashSet<string> wanted)
    {
        foreach (var obj in items)
        {
            if (obj is not TreeViewItem item) continue;

            if (item.Tag is string path && item.IsExpanded
                && !wanted.Contains(path) && !PathEquals(path, _assetsRoot))
            {
                item.IsExpanded = false;
            }
            CollapseUnwanted(item.Items, wanted);
        }
    }

    /// <summary>アセットルートからの階層の深さ（区切り文字の数）を返す。並び替えのキー。</summary>
    private static int GetDepth(string path)
        => path.Count(c => c == Path.DirectorySeparatorChar || c == Path.AltDirectorySeparatorChar);

    /// <summary>
    /// ルートから targetDir までの各階層を辿り、遅延生成ノードを実体化しながら展開する。
    /// 見つかった TreeViewItem を返す（辿れなかった場合は null）。
    /// </summary>
    private TreeViewItem? ExpandToFolder(string targetDir)
    {
        if (string.IsNullOrEmpty(targetDir) || string.IsNullOrEmpty(_assetsRoot)) return null;
        if (FolderTree.Items.Count == 0) return null;
        if (!IsUnderAssets(targetDir)) return null;

        var current = FolderTree.Items[0] as TreeViewItem;
        if (current == null) return null;
        if (PathEquals(_assetsRoot, targetDir)) return current;

        var rel  = Path.GetRelativePath(_assetsRoot, targetDir);
        var segs = rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar,
                             StringSplitOptions.RemoveEmptyEntries);

        var acc = _assetsRoot;
        foreach (var seg in segs)
        {
            // 子を実体化してから探す（未展開ノードはダミー 1 個しか持っていない）
            MaterializeChildren(current);
            acc = Path.Combine(acc, seg);
            var next = current.Items.OfType<TreeViewItem>()
                .FirstOrDefault(i => i.Tag is string p && PathEquals(p, acc));
            if (next == null) return null;

            // 途中の階層は必ず展開する（そうしないと子が可視にならない）
            current.IsExpanded = true;
            current = next;
        }
        return current;
    }

    /// <summary>現在ツリー上で展開されている（実体化済みの）フォルダの絶対パスを集める。</summary>
    private HashSet<string> CollectExpandedFolders()
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        CollectExpandedFolders(FolderTree.Items, result);
        return result;
    }

    /// <summary>CollectExpandedFolders の再帰本体。</summary>
    private static void CollectExpandedFolders(ItemCollection items, HashSet<string> result)
    {
        foreach (var obj in items)
        {
            if (obj is not TreeViewItem item) continue;
            if (item.IsExpanded && item.Tag is string path) result.Add(path);
            CollectExpandedFolders(item.Items, result);
        }
    }

    // ── 外部からのジャンプ要求（改修1の受け口） ────────────────────

    /// <summary>
    /// 指定ファイル（またはフォルダ）の場所を新しいタブで開き、そのアイテムを選択状態にする。
    /// インスペクタ等の参照フィールドをダブルクリックしたときの飛び先。
    ///
    /// 同じフォルダを開いているタブが既にあれば新規に作らずそのタブをアクティブ化する。
    /// パスが空・アセット外・実体が無い場合は何もしない（元のタブは常に保持される）。
    /// </summary>
    /// <param name="virtualOrAbsolutePath">
    /// `assets://` 仮想パスまたは絶対パス。参照フィールドの保持値をそのまま渡してよい。
    /// </param>
    public void RevealFile(string virtualOrAbsolutePath)
    {
        if (string.IsNullOrWhiteSpace(virtualOrAbsolutePath)) return;
        if (string.IsNullOrEmpty(_assetsRoot)) return;

        // 仮想パスを絶対パスへ解決する（絶対パスならそのまま返る）
        string abs;
        try { abs = VirtualPath.ToAbsolute(virtualOrAbsolutePath, _assetsRoot); }
        catch { return; }   // 不正な文字を含むパス等は黙って無視する

        bool isDir  = Directory.Exists(abs);
        bool isFile = File.Exists(abs);
        if (!isDir && !isFile) return;   // 実体が無い → 何もしない

        // ファイルならその親フォルダを、フォルダならそれ自身を開く
        var folder = isDir ? abs : Path.GetDirectoryName(abs);
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return;
        // プロジェクトパネルはアセットルート配下しか表示できないため、外部パスは対象外
        if (!IsUnderAssets(folder)) return;

        OpenFolderInTab(folder, selectPath: abs);
    }
}
