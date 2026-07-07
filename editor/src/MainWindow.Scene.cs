// ============================================================
//  MainWindow.Scene.cs — シーン保存・メニュー・ダーティ状態管理
//
//  担当:
//   - プロジェクト設定ダイアログ
//   - メニューバーハンドラ（保存・編集・表示・パッケージング）
//   - シーン/アクターの保存ロジック
//   - 選択オブジェクト削除
//   - ダーティ状態管理（タイトルバー * 表示）
//   - トースト通知
// ============================================================

using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using AvalonDock.Layout;
using Microsoft.Win32;
using SEEDEditor.Runtime;

namespace SEEDEditor;

public partial class MainWindow
{
    // ── プロジェクト設定 ──────────────────────────────────────────

    /// <summary>現在開いているプロジェクト設定ウィンドウ（多重起動防止用。閉じたら null）。</summary>
    private SEEDEditor.ProjectSettings.ProjectSettingsWindow? _projectSettingsWindow;

    /// <summary>
    /// 「プロジェクト設定」ボタン: プロジェクト設定ウィンドウをモーダレスで開く。
    ///
    /// モーダル（ShowDialog）だとエディタ本体の操作がブロックされ、プロジェクト
    /// パネルからシーンマネージャへの .scene ドラッグ＆ドロップができないため、
    /// Show + Owner 指定でエディタより前面を保ちつつ本体操作も可能にする。
    /// 既に開いている場合は新規に開かずアクティブ化する。
    /// 設定ファイルは AssetsPath/project_settings.json に保存される。
    /// </summary>
    private void OnOpenProjectSettings(object sender, RoutedEventArgs e)
    {
        // 既に開いていれば前面に出すだけ（多重起動防止）
        if (_projectSettingsWindow is not null)
        {
            _projectSettingsWindow.Activate();
            return;
        }

        // 現在開いているシーンのパスを渡す（シーンマネージャの「現在のシーンを追加」用）
        var win = new SEEDEditor.ProjectSettings.ProjectSettingsWindow(
            AssetsPath, EditorPluginsPath, _currentScenePath)
        {
            // Owner 指定によりエディタ本体より常に前面に表示される（モーダレスでも維持）
            Owner = this,
        };
        win.Closed += (_, _) => _projectSettingsWindow = null;
        _projectSettingsWindow = win;
        win.Show();
    }

    /// <summary>
    /// 「編集 → 環境設定...」: エディタ全体の環境設定ウィンドウをモーダルで開く。
    /// タッチパッドスクロール係数など、特定パネルに属さない操作系の設定を編集する。
    /// </summary>
    private void OnOpenEditorPreferences(object sender, RoutedEventArgs e)
    {
        var win = new EditorPreferencesWindow { Owner = this };
        win.ShowDialog();
    }

    // ── メニューバー ──────────────────────────────────────────────

    private void OnMenuQuickSave(object sender, RoutedEventArgs e)
        => DoQuickSave();

    private void OnMenuSaveAs(object sender, RoutedEventArgs e)
        => ShowSaveAsDialog();

    private void OnMenuExit(object sender, RoutedEventArgs e)
        => Close();

    /// <summary>パッケージ化ウィンドウを開く。</summary>
    private void OnOpenPackaging(object sender, RoutedEventArgs e)
    {
        var win = new SEEDEditor.Packaging.PackagingWindow(AssetsPath) { Owner = this };
        win.ShowDialog();
    }

    private void OnMenuUndo(object sender, RoutedEventArgs e)
        => _runtimeManager?.SendToRuntime("UNDO");

    private void OnMenuRedo(object sender, RoutedEventArgs e)
        => _runtimeManager?.SendToRuntime("REDO");

    private void OnMenuCopy(object sender, RoutedEventArgs e)
        => _runtimeManager?.SendToRuntime("COPY");

    private void OnMenuPaste(object sender, RoutedEventArgs e)
        => _runtimeManager?.SendToRuntime("PASTE");

    private void OnMenuDelete(object sender, RoutedEventArgs e)
        => TryDeleteSelected();

    // 表示メニューが開くたびに実際の表示状態でチェックを更新する
    private void OnViewMenuOpened(object sender, RoutedEventArgs e)
    {
        MenuItemHierarchy.IsChecked = IsPanelVisible("hierarchy");
        MenuItemInspector.IsChecked = IsPanelVisible("inspector");
        MenuItemProject.IsChecked   = IsPanelVisible("project");
        MenuItemOutput.IsChecked    = IsPanelVisible("output");
        // スクリプト関連ウィンドウの表示状態もチェックへ反映する
        MenuItemOpenDocuments.IsChecked = IsPanelVisible("open_documents");
        MenuItemErrorList.IsChecked     = IsPanelVisible("error_list");
        MenuItemScriptEditor.IsChecked  = IsScriptEditorVisible();
    }

    /// <summary>スクリプトエディタ（LayoutDocument）がレイアウト上に存在するか。</summary>
    private bool IsScriptEditorVisible() =>
        DockManager.Layout.Descendents()
            .OfType<LayoutDocument>()
            .Any(d => d.ContentId == "script_editor");

    private bool IsPanelVisible(string contentId) =>
        DockManager.Layout.Descendents()
            .OfType<LayoutAnchorable>()
            .Any(a => a.ContentId == contentId && a.IsVisible);

    private void OnTogglePanel(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem item || item.Tag is not string contentId) return;

        var panel = DockManager.Layout.Descendents()
            .OfType<LayoutAnchorable>()
            .FirstOrDefault(a => a.ContentId == contentId);
        if (panel is null) return;

        if (panel.IsVisible) panel.Hide();
        else panel.Show();

        // 実際の状態でチェックを確定する（WPF の自動トグルを上書き）
        item.IsChecked = panel.IsVisible;
    }

    // ── 選択インスタンス削除 ──────────────────────────────────

    private void TryDeleteSelected()
    {
        if (_deleteDialogOpen) return;
        if (_runtimeManager?.State != EditorState.Edit) return;

        // リネーム中（TextBox にフォーカスあり）は削除しない
        if (FocusManager.GetFocusedElement(this) is TextBox) return;

        var ids = PanelHierarchy.GetSelectedNonGroupIds();
        if (ids.Count == 0) return;

        if (!PanelHierarchy.AnyHasChildren(ids))
        {
            _runtimeManager!.SendToRuntime($"DELETE:{string.Join(",", ids)}");
            return;
        }

        _deleteDialogOpen = true;
        try
        {
            var result = MessageBox.Show(
                "選択中のオブジェクトに子オブジェクトが含まれています。\n\n" +
                "「はい」　— 子も含めてすべて削除\n" +
                "「いいえ」— 選択オブジェクトのみ削除（子は切り離してルートへ）",
                "オブジェクトの削除",
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Warning);

            var idsStr = string.Join(",", ids);
            if (result == MessageBoxResult.Yes)
                _runtimeManager!.SendToRuntime($"DELETE_RECURSIVE:{idsStr}");
            else if (result == MessageBoxResult.No)
                _runtimeManager!.SendToRuntime($"DELETE:{idsStr}");
        }
        finally
        {
            _deleteDialogOpen = false;
        }
    }

    // ── シーン保存ロジック ────────────────────────────────────────

    /// <summary>Ctrl+S: アクター編集中はアクターを、それ以外はシーンを上書き保存する。</summary>
    private void DoQuickSave()
    {
        if (_runtimeManager?.State != EditorState.Edit) return;
        if (_activeActorPath != null)
            ExecuteActorSave(_activeActorPath);
        else if (_currentScenePath != null)
            ExecuteSave(_currentScenePath);
        else
            ShowSaveAsDialog();
    }

    /// <summary>Ctrl+Shift+S / 名前を付けて保存。</summary>
    private void ShowSaveAsDialog()
    {
        if (_runtimeManager?.State != EditorState.Edit) return;
        if (_activeActorPath != null)
        {
            var dlg = new SaveFileDialog
            {
                Title            = "名前を付けてアクターを保存",
                Filter           = "Actor Files (*.actor)|*.actor|All Files (*.*)|*.*",
                DefaultExt       = ".actor",
                InitialDirectory = AssetsPath,
                OverwritePrompt  = true,
                FileName         = System.IO.Path.GetFileName(_activeActorPath),
            };
            if (dlg.ShowDialog(this) == true)
            {
                _activeActorPath = dlg.FileName;
                ExecuteActorSave(dlg.FileName);
            }
        }
        else
        {
            var dlg = new SaveFileDialog
            {
                Title            = "名前を付けてシーンを保存",
                Filter           = "Scene Files (*.scene)|*.scene|All Files (*.*)|*.*",
                DefaultExt       = ".scene",
                InitialDirectory = AssetsPath,
                OverwritePrompt  = true,
            };
            if (_currentScenePath != null)
                dlg.FileName = System.IO.Path.GetFileName(_currentScenePath);

            if (dlg.ShowDialog(this) == true)
                ExecuteSave(dlg.FileName);
        }
    }

    /// <summary>IPC でシーン保存コマンドを送出し、パスを記録する。</summary>
    private void ExecuteSave(string path)
    {
        _currentScenePath = path;
        _runtimeManager?.SendToRuntime($"SAVE_SCENE:{path}");
        EditorLog.Write($"ExecuteSave — SAVE_SCENE:{path}");
    }

    /// <summary>IPC でアクター保存コマンドを送出する。</summary>
    private void ExecuteActorSave(string path)
    {
        _isSavingActor = true;
        _runtimeManager?.SendToRuntime($"SAVE_ACTOR:{path}");
        EditorLog.Write($"ExecuteActorSave — SAVE_ACTOR:{path}");
    }

    private void OnSaveCompleted(bool ok, string errorMsg)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (ok)
            {
                EditorLog.Write("OnSaveCompleted — 保存成功");
                MarkClean();
                string toast = _isSavingActor ? "アクターを保存しました" : "シーンを保存しました";
                _isSavingActor = false;
                ShowToast(toast);
                UpdateTitle();

                // 保存→ウィンドウを閉じる（終了時確認フロー）
                if (_pendingClose)
                {
                    _pendingClose = false;
                    Close();
                    return;
                }

                // 保存→ロードの連鎖
                if (_pendingSceneLoad != null)
                {
                    var path = _pendingSceneLoad;
                    _pendingSceneLoad = null;
                    LoadScene(path);
                }
            }
            else
            {
                _isSavingActor = false;
                _pendingSceneLoad = null;
                MessageBox.Show($"保存に失敗しました:\n{errorMsg}", "SEED Editor",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        });
    }

    // ── ダーティ状態管理 ─────────────────────────────────────────

    /// <summary>
    /// シーンが変更されたことをマークする。UI・非UIスレッドどちらからでも呼べる。
    /// </summary>
    private void MarkDirty()
    {
        if (_isDirty) return;
        _isDirty = true;
        Dispatcher.BeginInvoke(UpdateTitle);
    }

    private void MarkDirtyFromHierarchy()
    {
        if (_suppressHierarchyDirtyCount > 0) { _suppressHierarchyDirtyCount--; return; }
        MarkDirty();
    }

    /// <summary>
    /// ナビゲーション系コマンドを送信する。
    /// 送信によって発生する HIERARCHY 更新をダーティ扱いしないようカウンターを +1 する。
    /// </summary>
    private void SendNavCommand(string cmd)
    {
        if (_runtimeManager != null)
            _suppressHierarchyDirtyCount++;
        _runtimeManager?.SendToRuntime(cmd);
    }

    private void MarkClean()
    {
        _isDirty = false;
    }

    private void UpdateTitle()
    {
        var name = _currentScenePath != null
            ? System.IO.Path.GetFileNameWithoutExtension(_currentScenePath)
            : "新規シーン";
        Title = _isDirty ? $"SEED Editor — {name}*" : $"SEED Editor — {name}";
        MenuQuickSave.Header = _currentScenePath != null ? "上書き保存" : "上書き保存（未保存）";
    }

    // ── トースト通知 ─────────────────────────────────────────────

    private void ShowToast(string message)
    {
        ToastText.Text         = message;
        ToastBorder.Visibility = Visibility.Visible;

        _toastTimer?.Stop();
        _toastTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(2.5),
        };
        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer?.Stop();
            ToastBorder.Visibility = Visibility.Collapsed;
        };
        _toastTimer.Start();
    }
}
