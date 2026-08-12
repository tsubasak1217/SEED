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
    /// <summary>シーンタブの閉じるボタンのアイコン一辺サイズ（px）。</summary>
    private const double SceneTabCloseIconSize = 10;

    // ── シーンファイル読み込み ────────────────────────────────────

    private void OnSceneFileOpened(string path)
    {
        if (_runtimeManager?.State != EditorState.Edit) return;

        // キャンバス編集タブが開いていたら先に閉じてアクターをシーンへ戻す
        //（LOAD_SCENE は世界線 > 0 のアクターを保持するため、開いたままだと
        //  旧シーンのキャンバスアクターが新シーンへ紛れ込んでしまう）
        CloseActiveSceneCanvasTab();
        EndInactiveSceneCanvasTabs();

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

        // 別タブへ移動したのでキャンバス編集タブは終了する（移動コマンド送信後に呼ぶこと）
        EndInactiveSceneCanvasTabs();
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
            var isSceneCanvas = tab?.IsSceneCanvas ?? false;
            PanelHierarchy.SetActorEditMode(true, wl, is2D, isSceneCanvas);
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
        // キャンバス編集タブから戻る場合はタブごと閉じる（EDIT_CANVAS_END で
        // ランタイムがアクターをシーンへ戻して世界線 0 へ復帰する）
        if (CloseActiveSceneCanvasTab())
        {
            EditorLog.Write("OnReturnToScene — キャンバス編集タブを終了");
            return;
        }
        SendNavCommand("SET_ACTIVE_WORLD_LINE:0");
        _activeActorPath = null;
        PanelHierarchy.SetActorEditMode(false);
        PanelInspector.SetActorEditMode(false);
        // シーンに戻ったら現在のシーンタブのビューモードを再送してランタイムと同期する
        SendCurrentEditView();
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
        PanelHierarchy.SetActorEditMode(true, tab.WorldLine, tab.IsActor2D, tab.IsSceneCanvas);
        PanelInspector.SetActorEditMode(true);
        // 別タブへ移動したのでキャンバス編集タブは終了する（移動コマンド送信後に呼ぶこと）
        EndInactiveSceneCanvasTabs();
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

        // キャンバス編集タブ: EDIT_CANVAS_END でアクターをシーンへ戻して閉じる。
        // 閉じたあとは所有アクターの種別に対応するシーンタブへ戻る（保存確認は不要。
        // 編集はシーンへ直接反映済みで SCENE_MODIFIED によりダーティ扱いになっている）。
        if (closingTab.IsSceneCanvas)
        {
            if (_activeActorPath == path)
            {
                CloseActiveSceneCanvasTab();
            }
            else
            {
                // 非アクティブなキャンバス編集タブ（通常は存在しないが念のため）
                _actorTabs.RemoveAt(idx);
                _runtimeManager?.SendToRuntime($"EDIT_CANVAS_END:{closingTab.WorldLine}");
                RebuildActorTabBar();
            }
            return;
        }

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
                // シーンタブへ復帰するので現在のビューモードを再送して同期する
                SendCurrentEditView();
            }
        }

        RebuildActorTabBar();
    }

    /// <summary>タブバー UI を再構築する。</summary>
    private void RebuildActorTabBar()
    {
        ActorTabsPanel.Children.Clear();

        // ── 固定シーンタブ（左端に常設。生成は MainWindow.SceneTabs.cs）──
        // ワールド    = カメラに撮られて相対的に映るもの（3D アクター + 3D ワールドキャンバス）
        // ビューポート = 画面/カメラ枠基準で張り付くもの（スクリーンスペースキャンバス系）
        ActorTabsPanel.Children.Add(BuildSceneTabItem("ワールド",     is2D: false));
        ActorTabsPanel.Children.Add(BuildSceneTabItem("ビューポート", is2D: true));

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

            // 閉じるボタン
            var closeBtn = new Button
            {
                Content         = SEEDEditor.Controls.AppIcon.Create("Icon.Close", SceneTabCloseIconSize),
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

        // タブバー全体の表示制御: 固定シーンタブを常設するため常に表示する
        ActorTabBar.Visibility  = Visibility.Visible;
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

        // シーン設定（.scene の settings 節）を新しいシーンから読み直し、ランタイムへ全項目を再送する。
        // LOAD_SCENE はランタイムへ非同期に届くが IPC の順序は保たれるため、
        // 必ず LOAD_SCENE の「後」に送ってシーン側の初期値を上書きする。
        LoadSceneSettingsForCurrentScene();
        if (_viewportSettingsInitialized) SyncViewportSettings();

        UpdateTitle();
        EditorLog.Write($"LoadScene — LOAD_SCENE:{path}");
    }

    /// <summary>
    /// 起動時に前回最後に開いていたシーンを復元する。
    /// 最近開いたシーン一覧（RecentProjectsManager）の先頭にある、実在する .scene を読み込む。
    /// 無ければ何もしない（ランタイム側の既定シーンのまま）。
    /// </summary>
    private void TryLoadLastScene()
    {
        try
        {
            var last = SEEDEditor.ProjectSettings.RecentProjectsManager.LoadRecentProjects()
                .FirstOrDefault(p =>
                    !string.IsNullOrEmpty(p)
                    && p.EndsWith(".scene", StringComparison.OrdinalIgnoreCase)
                    && System.IO.File.Exists(p));
            if (last is null)
            {
                EditorLog.Write("起動時シーン復元: 対象なし（既定シーンのまま）");
                return;
            }
            EditorLog.Write($"起動時シーン復元 — {last}");
            LoadScene(last);
        }
        catch (Exception ex)
        {
            EditorLog.Write($"起動時シーン復元に失敗: {ex.Message}");
        }
    }
}
