// ============================================================
//  MainWindow.FileOps.cs — シーン・アクター・InputMap のファイル操作
//
//  担当:
//   - シーンファイルの読み込み（ダーティチェック込み）
//   - アクターファイルの編集（世界線・タブ管理）
//   - InputMap ファイルのエディタ起動
//   - アクタータブバー UI の再構築
// ============================================================

using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using SEEDEditor.Runtime;

namespace SEEDEditor;

public partial class MainWindow
{
    // ── シーンファイル読み込み ────────────────────────────────────

    private void OnSceneFileOpened(string path)
    {
        if (_runtimeManager?.State != EditorState.Edit) return;

        if (_isDirty)
        {
            var result = MessageBox.Show(
                "未保存の変更があります。シーンを切り替える前に保存しますか？",
                "SEED Editor",
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Question);

            if (result == MessageBoxResult.Cancel) return;

            if (result == MessageBoxResult.Yes)
            {
                if (_currentScenePath != null)
                {
                    // 上書き保存してから読み込み
                    _pendingSceneLoad = path;
                    ExecuteSave(_currentScenePath);
                }
                else
                {
                    // 名前を付けて保存してから読み込み
                    var dlg = new SaveFileDialog
                    {
                        Title            = "名前を付けてシーンを保存",
                        Filter           = "Scene Files (*.scene)|*.scene|All Files (*.*)|*.*",
                        DefaultExt       = ".scene",
                        InitialDirectory = AssetsPath,
                        OverwritePrompt  = true,
                    };
                    if (dlg.ShowDialog(this) == true)
                    {
                        _pendingSceneLoad = path;
                        ExecuteSave(dlg.FileName);
                    }
                    // キャンセルした場合は何もしない
                }
                return;
            }
            // No = 変更を破棄して読み込み
        }

        // アクター編集中の場合はシーンモードに戻す（タブは保持）
        if (_activeActorPath != null)
        {
            _activeActorPath = null;
            PanelHierarchy.SetActorEditMode(false);
            PanelInspector.SetActorEditMode(false);
            RebuildActorTabBar();
        }

        LoadScene(path);
    }

    // ── アクターファイル編集 ─────────────────────────────────────

    private void OnActorFileOpened(string path)
    {
        if (_runtimeManager?.State != EditorState.Edit) return;

        // 同じアクターが既にアクティブなら何もしない
        if (_activeActorPath == path) return;

        var existing = _actorTabs.FirstOrDefault(t => t.Path == path);
        if (existing != null)
        {
            // 既存タブに切り替え（再ロードなし）
            _activeActorPath = path;
            SendNavCommand($"SET_ACTIVE_WORLD_LINE:{existing.WorldLine}");
            EditorLog.Write($"OnActorFileOpened — SET_ACTIVE_WORLD_LINE:{existing.WorldLine}");
        }
        else
        {
            // 新規タブ: 世界線を割り当ててロード
            var wl   = _nextWorldLineIdx++;
            var name = System.IO.Path.GetFileNameWithoutExtension(path);
            // actor_kind を読み取って 2D アクターか判定する
            var is2D = DetectActorIs2D(path);
            _actorTabs.Add(new ActorTab(path, name, wl, is2D));
            _activeActorPath = path;
            SendNavCommand($"OPEN_ACTOR:{wl},{path}");
            EditorLog.Write($"OnActorFileOpened — OPEN_ACTOR:{wl},{path} (is2D={is2D})");
        }

        RebuildActorTabBar();
    }

    // ── InputMap ファイル編集 ────────────────────────────────────

    /// <summary>.inputmap ファイルをダブルクリックしたときに InputMap エディタを開く。</summary>
    private void OnInputMapFileOpened(string path)
    {
        var win = new SEEDEditor.InputMap.InputMapEditorWindow(path) { Owner = this };
        win.Show();
    }

    private void OnActorEditStarted()
    {
        Dispatcher.InvokeAsync(() =>
        {
            var tab  = _actorTabs.LastOrDefault();
            var wl   = tab?.WorldLine ?? 1u;
            var is2D = tab?.IsActor2D ?? false;
            PanelHierarchy.SetActorEditMode(true, wl, is2D);
            PanelInspector.SetActorEditMode(true);
            RebuildActorTabBar();
        });
    }

    private void OnActorEditEnded()
    {
        _activeActorPath = null;
        Dispatcher.InvokeAsync(() =>
        {
            PanelHierarchy.SetActorEditMode(false);
            PanelInspector.SetActorEditMode(false);
            RebuildActorTabBar();
        });
    }

    private void OnReturnToScene(object sender, RoutedEventArgs e)
    {
        if (_runtimeManager?.State != EditorState.Edit) return;
        SendNavCommand("SET_ACTIVE_WORLD_LINE:0");
        _activeActorPath = null;
        PanelHierarchy.SetActorEditMode(false);
        PanelInspector.SetActorEditMode(false);
        RebuildActorTabBar();
        EditorLog.Write("OnReturnToScene — SET_ACTIVE_WORLD_LINE:0");
    }

    /// <summary>タブをクリックしてそのアクターに切り替える。</summary>
    private void ActivateActorTab(string path)
    {
        if (_runtimeManager?.State != EditorState.Edit) return;
        if (_activeActorPath == path) return;
        var tab = _actorTabs.FirstOrDefault(t => t.Path == path);
        if (tab == null) return;
        _activeActorPath = path;
        SendNavCommand($"SET_ACTIVE_WORLD_LINE:{tab.WorldLine}");
        PanelHierarchy.SetActorEditMode(true, tab.WorldLine, tab.IsActor2D);
        PanelInspector.SetActorEditMode(true);
        RebuildActorTabBar();
    }

    /// <summary>
    /// アクターファイルを読み取り、actor_kind が "Actor2D" かどうかを返す。
    /// ファイルが読めない場合は false を返す。
    /// </summary>
    private static bool DetectActorIs2D(string path)
    {
        // .actor2d 拡張子は定義上 2D アクター。JSON 解析不要。
        if (path.EndsWith(".actor2d", StringComparison.OrdinalIgnoreCase)) return true;
        try
        {
            var json = System.IO.File.ReadAllText(path);
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("actor_kind", out var kind)
                && kind.GetString() == "Actor2D";
        }
        catch { return false; }
    }

    /// <summary>タブの × を押してそのタブを閉じる。</summary>
    private void CloseActorTab(string path)
    {
        var idx = _actorTabs.FindIndex(t => t.Path == path);
        if (idx < 0) return;

        var closingTab = _actorTabs[idx];
        _actorTabs.RemoveAt(idx);

        // 閉じるタブの世界線アクターを除去（ナビゲーションではないので SendNavCommand 不要）
        _runtimeManager?.SendToRuntime($"REMOVE_WORLD_LINE:{closingTab.WorldLine}");

        if (_activeActorPath == path)
        {
            if (_actorTabs.Count > 0)
            {
                // 隣のタブに切り替え
                var next = _actorTabs[Math.Min(idx, _actorTabs.Count - 1)];
                _activeActorPath = next.Path;
                SendNavCommand($"SET_ACTIVE_WORLD_LINE:{next.WorldLine}");
            }
            else
            {
                // タブが空になったのでシーンに戻る
                _activeActorPath = null;
                SendNavCommand("SET_ACTIVE_WORLD_LINE:0");
            }
        }

        RebuildActorTabBar();
    }

    /// <summary>タブバー UI を再構築する。</summary>
    private void RebuildActorTabBar()
    {
        ActorTabsPanel.Children.Clear();

        foreach (var tab in _actorTabs)
        {
            bool isActive = tab.Path == _activeActorPath;

            // タブ外枠
            var tabBorder = new Border
            {
                Height          = 28,
                MinWidth        = 60,
                MaxWidth        = 200,
                Cursor          = Cursors.Hand,
                Background      = isActive
                                    ? new SolidColorBrush(Color.FromRgb(0x3C, 0x3C, 0x3C))
                                    : Brushes.Transparent,
                BorderBrush     = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55)),
                BorderThickness = isActive
                                    ? new Thickness(0, 2, 1, 0)   // 上 2px はオレンジ（上書き後）
                                    : new Thickness(0, 0, 1, 0),
            };

            // アクティブタブはオレンジのトップアクセント
            if (isActive)
                tabBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(0xE8, 0x78, 0x20));

            // ホバー効果
            tabBorder.MouseEnter += (s, e) =>
            {
                if (tab.Path != _activeActorPath)
                    tabBorder.Background = new SolidColorBrush(Color.FromRgb(0x2E, 0x2E, 0x2E));
            };
            tabBorder.MouseLeave += (s, e) =>
            {
                if (tab.Path != _activeActorPath)
                    tabBorder.Background = Brushes.Transparent;
            };

            // タブクリック → アクター切り替え
            tabBorder.MouseLeftButtonDown += (s, e) => ActivateActorTab(tab.Path);

            // 内部レイアウト
            var dp = new DockPanel { Margin = new Thickness(8, 0, 4, 0) };

            // × ボタン
            var closeBtn = new Button
            {
                Content         = "×",
                Width           = 16,
                Height          = 16,
                Margin          = new Thickness(4, 0, 0, 0),
                Background      = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Foreground      = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
                FontSize        = 11,
                Cursor          = Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Center,
                Padding         = new Thickness(0),
            };
            // × ボタンのホバー
            closeBtn.MouseEnter += (s, e) =>
                closeBtn.Foreground = Brushes.White;
            closeBtn.MouseLeave += (s, e) =>
                closeBtn.Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));
            closeBtn.Click += (s, e) =>
            {
                e.Handled = true;
                CloseActorTab(tab.Path);
            };

            // アクター名テキスト
            var txt = new TextBlock
            {
                Text              = tab.Name,
                Foreground        = isActive ? Brushes.White
                                             : new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA)),
                FontSize          = 12,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming      = TextTrimming.CharacterEllipsis,
            };

            DockPanel.SetDock(closeBtn, Dock.Right);
            dp.Children.Add(closeBtn);
            dp.Children.Add(txt);
            tabBorder.Child = dp;

            ActorTabsPanel.Children.Add(tabBorder);
        }

        // タブバー全体の表示制御
        ActorTabBar.Visibility  = _actorTabs.Count > 0
            ? Visibility.Visible : Visibility.Collapsed;
        // "シーンに戻る" はアクター編集中のみ表示
        BtnReturnToScene.Visibility = _activeActorPath != null
            ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>シーンを読み込む（ダーティチェック済みの場合に直接呼ぶ）。</summary>
    private void LoadScene(string path)
    {
        _currentScenePath = path;
        _isDirty = false;
        SEEDEditor.ProjectSettings.RecentProjectsManager.AddProject(path);
        SendNavCommand($"LOAD_SCENE:{path}");
        UpdateTitle();
        EditorLog.Write($"LoadScene — LOAD_SCENE:{path}");
    }
}
