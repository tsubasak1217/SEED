// ============================================================
//  ProjectPanel.Visibility.cs — 「隠しファイル」の表示制御
//
//  【役割】
//  プロジェクトパネルに出す必要の無いファイル・フォルダ（エディタ／OS の作業ファイル、
//  エンジンが生成する地形の中間データ）を既定で隠し、ツールバーのトグルで
//  「薄く表示する」へ切り替える。
//
//  【判定は持たない】
//  何を隠すかの規則は WPF 非依存の ProjectPanelVisibilityRules（＝
//  editor/config/project_panel_rules.json）が唯一の正典。この部分クラスは
//  「ルールを読む」「トグル状態を持つ」「見た目へ落とす」だけを担当する。
// ============================================================

using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using SEEDEditor;
using SEEDEditor.Assets;

namespace SEEDEditor.Panels;

/// <summary>
/// ProjectPanel の表示フィルタ（隠しファイル）を担当する部分クラス。
/// </summary>
public partial class ProjectPanel
{
    // ── 表示ルール（全パネル共有・遅延読み込み）──────────────────

    /// <summary>
    /// 非表示ルール。初回アクセス時に editor/config から読み込み、
    /// 問題があればログへ出したうえで組み込み既定へフォールバックする。
    /// パネルを複数開いても 1 回しか読まない（static Lazy）。
    /// </summary>
    private static readonly Lazy<ProjectPanelVisibilityRules> _visibilityRules = new(LoadVisibilityRules);

    /// <summary>現在有効な非表示ルール。</summary>
    private static ProjectPanelVisibilityRules VisibilityRules => _visibilityRules.Value;

    /// <summary>
    /// 非表示ルールを editor/config から読み込み、警告をログへ出す。
    /// 読み込み自体は失敗しない（必ず組み込み既定へフォールバックする）。
    /// </summary>
    private static ProjectPanelVisibilityRules LoadVisibilityRules()
    {
        var rules = ProjectPanelVisibilityRules.LoadFromDir(SEEDEditor.Settings.EditorPaths.ConfigDir);

        foreach (var w in rules.Warnings)
            EditorLog.Write($"[ProjectPanel] {w}");

        EditorLog.Write(
            $"[ProjectPanel] 表示ルール読み込み完了 — source={rules.SourcePath ?? "(組み込み既定)"}");
        return rules;
    }

    // ── 見た目の定数 ─────────────────────────────────────────────

    /// <summary>切り取り中のタイルの不透明度（貼り付け待ちであることを示す）。</summary>
    private const double CutTileOpacity = 0.5;

    /// <summary>
    /// 隠し対象を「表示」しているときのタイルの不透明度。
    /// 通常のアセットと同じ濃さで並ぶと紛らわしいので一段薄くする。
    /// </summary>
    private const double HiddenTileOpacity = 0.45;

    /// <summary>タイルの通常の不透明度。</summary>
    private const double NormalTileOpacity = 1.0;

    /// <summary>
    /// 「隠し対象だが表示中」のタイル。<see cref="ApplyCutOpacity"/> が
    /// 切り取り状態を塗り直すときに、元の薄さへ戻すために使う。
    /// ファイルグリッドを作り直すたびにクリアする。
    /// </summary>
    private readonly HashSet<Border> _dimmedTiles = new();

    // ── トグル状態 ───────────────────────────────────────────────

    /// <summary>
    /// 隠しファイルを表示するか。実体は環境設定（プロジェクトを跨いで保たれる）。
    /// </summary>
    private static bool ShowHiddenFiles => EditorPreferences.Instance.ShowHiddenProjectFiles;

    /// <summary>
    /// ツールバーのトグルボタンを現在の環境設定に合わせる（コンストラクタから呼ぶ）。
    ///
    /// IsChecked をコードで変えると Checked/Unchecked が発火して
    /// 「読み込んだ値をそのまま保存し直す」ことになるが、値は同じなので実害は無い。
    /// ただし初期化中の再描画は無駄なので、フラグで抑止する。
    /// </summary>
    private void InitVisibilityToggle()
    {
        _suppressVisibilityToggleEvent = true;
        BtnShowHidden.IsChecked        = ShowHiddenFiles;
        _suppressVisibilityToggleEvent = false;
    }

    /// <summary>トグルの初期化中に Checked/Unchecked の本処理を抑止するフラグ。</summary>
    private bool _suppressVisibilityToggleEvent;

    /// <summary>
    /// 「隠しファイルを表示」トグルが切り替わったとき。
    /// 環境設定へ保存し、ツリーとファイル一覧を作り直す。
    /// </summary>
    private void OnShowHiddenToggled(object sender, RoutedEventArgs e)
    {
        if (_suppressVisibilityToggleEvent) return;
        if (sender is not ToggleButton toggle) return;

        EditorPreferences.Instance.ShowHiddenProjectFiles = toggle.IsChecked == true;
        EditorPreferences.Save();

        // ツリーは「隠しフォルダを畳んだ状態」で作られているため、丸ごと作り直す。
        // 現在フォルダは変わらないので、ファイル一覧も同じ場所を再描画すればよい。
        RebuildForVisibilityChange();
    }

    /// <summary>
    /// 表示ルールの切り替えに合わせてフォルダツリーとファイル一覧を作り直す。
    ///
    /// ツリーは遅延生成（展開したノードだけ実体化）なので、フィルタが変わったら
    /// 作り直すしかない。作り直すと展開状態が失われるため、現在開いているフォルダまで
    /// 開き直して選択を戻す。
    /// </summary>
    private void RebuildForVisibilityChange()
    {
        BuildFolderTree();

        // 現在フォルダが隠しフォルダ配下だとツリーに現れない場合があるが、
        // ファイル一覧は現在フォルダをそのまま描き続ける（作業中に足元が消えないように）。
        _suppressTreeEvent = true;
        try { ExpandToFolder(_currentPath); }
        finally { _suppressTreeEvent = false; }
        SyncTreeSelection(_currentPath);

        RefreshFileGrid();
    }

    // ── 判定 ─────────────────────────────────────────────────────

    /// <summary>
    /// このエントリをパネルに出してよいか（トグル状態込みの最終判定）。
    /// </summary>
    /// <param name="path">ファイル／フォルダの絶対パス。</param>
    /// <param name="isDirectory">true=フォルダ / false=ファイル。</param>
    private bool ShouldShowEntry(string path, bool isDirectory)
    {
        // 「このファイルへジャンプ」（RevealFile / タブ復元）の対象だけは、
        // 隠し対象でも必ず出す。出さないと参照フィールドのダブルクリックが
        // 「フォルダは開くのに何も選ばれない」という無反応に見える
        // （シーンが参照する地形 .tvox などが実際にこれに当たる）。
        if (_pendingSelectPath is not null && PathEquals(path, _pendingSelectPath)) return true;

        return VisibilityRules.ShouldShowPath(path, isDirectory, ShowHiddenFiles);
    }

    /// <summary>
    /// このエントリが非表示ルールに当たるか（トグル状態は見ない）。
    /// 「表示中だが隠し対象」を薄く描くために使う。
    /// </summary>
    /// <param name="path">ファイル／フォルダの絶対パス。</param>
    /// <param name="isDirectory">true=フォルダ / false=ファイル。</param>
    private static bool IsHiddenEntry(string path, bool isDirectory)
        => VisibilityRules.IsHiddenPath(path, isDirectory);

    // ── 見た目 ───────────────────────────────────────────────────

    /// <summary>
    /// 隠し対象のタイルを薄く描く（表示トグルが ON のときだけ呼ばれうる）。
    /// </summary>
    /// <param name="tile">対象のタイル。</param>
    /// <param name="path">ファイル／フォルダの絶対パス。</param>
    /// <param name="isDirectory">true=フォルダ / false=ファイル。</param>
    private void ApplyHiddenAppearance(Border tile, string path, bool isDirectory)
    {
        if (!IsHiddenEntry(path, isDirectory)) return;
        _dimmedTiles.Add(tile);
        tile.Opacity = HiddenTileOpacity;
        // ツールチップの先頭に理由を足す（なぜ薄いのかが分かるように）。
        tile.ToolTip = tile.ToolTip is string tip
            ? HiddenTileToolTipPrefix + Environment.NewLine + tip
            : HiddenTileToolTipPrefix;
    }

    /// <summary>薄く表示している理由をツールチップの先頭に出す文言。</summary>
    private const string HiddenTileToolTipPrefix =
        "（通常は非表示のファイル。エディタ・OS の作業ファイルか、エンジンが生成する中間データ）";

    /// <summary>
    /// タイルの「切り取り状態を除いた」本来の不透明度。
    /// 隠し対象を表示しているタイルは薄いままにする。
    /// </summary>
    /// <param name="tile">対象のタイル。</param>
    private double BaseTileOpacity(Border tile)
        => _dimmedTiles.Contains(tile) ? HiddenTileOpacity : NormalTileOpacity;
}
