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
            AssetsPath, EditorPluginsPath, _currentScenePath, _runtimeManager)
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

    // ── ツールメニュー: 図鑑画像の生成 ────────────────────────────

    /// <summary>図鑑画像生成のエディタコマンド名（MCP ツール seed_generate_fish_thumbnails と同一経路）。</summary>
    private const string GenerateFishThumbnailsCommand = "generate_fish_thumbnails";

    /// <summary>図鑑画像生成のツール呼び出し ID（ログ上で経路を見分けるための固定値）。</summary>
    private const string GenerateFishThumbnailsCallId = "menu-generate-fish-thumbnails";

    /// <summary>引数を指定しないツール呼び出しの引数 JSON（既定値で実行する）。</summary>
    private const string EmptyToolArgumentsJson = "{}";

    /// <summary>
    /// 図鑑画像の生成が実行中か。メニューの二度押しで 2 本同時に走らないようにする
    /// （ランタイムへの描画依頼は 1 往復 1 応答で、並行すると応答の対応付けが壊れる）。
    /// </summary>
    private bool _generatingFishThumbnails;

    /// <summary>
    /// 「ツール → 図鑑画像を生成」: 全魚 prefab のサムネイル PNG を生成し、
    /// FishCatalog.cs を再生成する。
    ///
    /// <para>
    /// MCP ツール <c>seed_generate_fish_thumbnails</c> と**同じ経路**を通すため、
    /// AI アシスタントパネルが持つ共有 EditorCommandExecutor へ ToolCall を投げる
    /// （生成処理をここへ複製しない）。発信元は利用者自身の操作なので UserInitiated。
    /// </para>
    /// </summary>
    private async void OnGenerateFishThumbnails(object sender, RoutedEventArgs e)
    {
        // 実行中の二重起動を防ぐ（メニューは連打できてしまうため）。
        if (_generatingFishThumbnails)
        {
            EditorLog.Write("[図鑑] すでに生成中です。完了までお待ちください。");
            return;
        }

        var executor = _aiPanel?.SharedExecutor;
        if (executor is null)
        {
            EditorLog.Write("[図鑑] AI アシスタントパネルが初期化されていないため実行できません。");
            return;
        }

        _generatingFishThumbnails = true;
        try
        {
            EditorLog.Write("[図鑑] 図鑑画像の生成を開始します。");

            var result = await executor.ExecuteAsync(
                new SEEDEditor.AI.Models.ToolCall
                {
                    Id            = GenerateFishThumbnailsCallId,
                    FunctionName  = GenerateFishThumbnailsCommand,
                    ArgumentsJson = EmptyToolArgumentsJson,
                },
                // シーン情報の自動取得は不要（シーンを触らないコマンドのため）。
                includeSceneInfo: false,
                origin:           SEEDEditor.AI.AiCommandOrigin.UserInitiated);

            EditorLog.Write($"[図鑑] 生成結果: {result}");
        }
        catch (Exception ex)
        {
            // メニューハンドラ（async void）から例外を投げるとアプリごと落ちるため必ず握る。
            EditorLog.Write($"[図鑑] 生成中に例外が発生しました: {ex}");
        }
        finally
        {
            _generatingFishThumbnails = false;
        }
    }

    // 表示メニューが開くたびに実際の表示状態でチェックを更新する
    private void OnViewMenuOpened(object sender, RoutedEventArgs e)
    {
        MenuItemHierarchy.IsChecked = IsPanelVisible("hierarchy");
        MenuItemInspector.IsChecked = IsPanelVisible("inspector");
        MenuItemProject.IsChecked   = IsPanelVisible("project");
        MenuItemOutput.IsChecked    = IsPanelVisible("output");
        MenuItemAnimationTimeline.IsChecked = IsPanelVisible("animation_timeline");
        MenuItemSpriteRig.IsChecked = IsPanelVisible("sprite_rig");
        MenuItemProfiler.IsChecked = IsPanelVisible("profiler");
        // スクリプト関連ウィンドウの表示状態もチェックへ反映する
        MenuItemOpenDocuments.IsChecked = IsPanelVisible("open_documents");
        MenuItemErrorList.IsChecked     = IsPanelVisible("error_list");
        MenuItemScriptEditor.IsChecked  = IsScriptEditorVisible();
        // スクリプト自動再読込の設定値をチェック状態へ反映する（設定ファイルが正）。
        MenuItemAutoReloadScripts.IsChecked = EditorPreferences.Instance.AutoReloadScripts;
        // シーン自動再読込の設定値も同様にチェック状態へ反映する（設定ファイルが正）。
        MenuItemAutoReloadScene.IsChecked = EditorPreferences.Instance.AutoReloadScene;
        // プレハブ保存時の自動反映の設定値も同様（設定ファイルが正）。
        MenuItemPrefabAutoPropagate.IsChecked = EditorPreferences.Instance.PrefabAutoPropagateOnSave;
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
            var result = SEEDEditor.Headless.EditorDialogs.Show(
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
        // 別インスタンスがこのシーンを開いている間は保存させない
        //（後から保存した方が相手の変更を丸ごと消してしまうため）。
        if (RefuseSaveIfReadOnly()) return;
        // キャンバス編集タブ表示中の保存はシーン保存として扱う。
        // タブを閉じてアクターをシーンへ戻してから保存する（開いたまま SAVE_SCENE
        // するとアクターが編集用世界線に居るため正しく書き出されない）。
        CloseActiveSceneCanvasTab();
        EndInactiveSceneCanvasTabs();
        if (_activeActorPath != null)
            ExecuteActorSave(_activeActorPath);
        else if (_currentScenePath != null)
            ExecuteSave(_currentScenePath);
        else
            ShowSaveAsDialog();
    }

    /// <summary>
    /// 読み取り専用シーンの保存要求を弾く。弾いたら true。
    /// エラーの提示（トースト＋ログ）もここで済ませる。
    /// </summary>
    private bool RefuseSaveIfReadOnly()
    {
        var reason = SceneSaveDenialReason;
        if (reason is null) return false;

        EditorLog.Write($"保存を拒否しました: {reason}");
        // ヘッドレスではモーダルを出せないのでトーストとログのみ（非モーダル）。
        ShowToast("読み取り専用のため保存できません");
        if (!SEEDEditor.Headless.EditorStartupOptions.IsHeadless)
            MessageBox.Show(reason, "SEED Editor", MessageBoxButton.OK, MessageBoxImage.Warning);
        return true;
    }

    /// <summary>Ctrl+Shift+S / 名前を付けて保存。</summary>
    private void ShowSaveAsDialog()
    {
        if (_runtimeManager?.State != EditorState.Edit) return;
        // キャンバス編集タブ表示中の保存はシーン保存として扱う（DoQuickSave と同じ理由）
        CloseActiveSceneCanvasTab();
        EndInactiveSceneCanvasTabs();
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
                ExecuteSaveAs(dlg.FileName);
        }
    }

    /// <summary>
    /// 現在のシーンを上書き保存する（IPC <c>SAVE_SCENE</c>）。
    ///
    /// <para>
    /// **保存先を変える用途では使わないこと**。ランタイム側は「自分が読み込んでいる
    /// シーンのパスと一致するか」を検査し、違えば 1 バイトも書かずに
    /// <c>SAVE_ERROR:path_mismatch</c> を返す。別名で保存するときは
    /// <see cref="ExecuteSaveAs"/> を使う。
    /// </para>
    /// </summary>
    private void ExecuteSave(string path)
    {
        if (RefuseSaveIfReadOnly()) return;

        // シーン自動再読込へ「これから自分が書き込む」と伝える。
        // 実際に .scene を書き出すのはランタイム（SAVE_SCENE の非同期処理）のため、
        // 保存完了通知（OnSaveCompleted）までを 1 つの自己書き込み窓として扱う。
        _sceneAutoReloader?.NotifySelfSaveStarted();
        _runtimeManager?.SendToRuntime($"SAVE_SCENE:{path}");
        EditorLog.Write($"ExecuteSave — SAVE_SCENE:{path}");
    }

    /// <summary>
    /// シーンを別名で保存する（IPC <c>SAVE_SCENE_AS</c>）。
    ///
    /// ランタイム側はパス整合性チェックを行わず、保存後はこのパスを
    /// 「読み込み中のシーン」として採用する。エディタ側もビュー状態の保存キーと
    /// 自動再読込の監視対象を新しいパスへ移す。
    /// </summary>
    private void ExecuteSaveAs(string path)
    {
        if (RefuseSaveIfReadOnly()) return;

        // 保存先が変わる場合は、ビュー状態の保存キーも新しいパスへ移す。
        // 移さないと、この後の操作が旧シーンのエントリへ書き込まれてしまう。
        // 展開状態は破棄せず現在の状態を新キーへ引き継ぐ（保存でツリーが畳まれると驚きになる）。
        bool pathChanged = !string.Equals(_currentScenePath, path, StringComparison.OrdinalIgnoreCase);
        if (pathChanged)
        {
            // 旧シーンのロックを解放してから新しいパスを現在シーンにする。
            ReleaseSceneLock();
            _currentScenePath = path;
            AcquireSceneLock(path);
            PanelHierarchy.MoveSceneViewKey(path);
            PersistToolbarViewState();
            RetargetSceneAutoReloader();
            UpdateTitle();
        }

        _sceneAutoReloader?.NotifySelfSaveStarted();
        _runtimeManager?.SendToRuntime($"SAVE_SCENE_AS:{path}");
        EditorLog.Write($"ExecuteSaveAs — SAVE_SCENE_AS:{path}");
    }

    /// <summary>IPC でアクター保存コマンドを送出する。</summary>
    private void ExecuteActorSave(string path)
    {
        _isSavingActor = true;
        // 保存完了（SAVE_OK）後にシーン内インスタンスへ自動反映するため、対象パスを覚えておく。
        NotifyActorSaveStarted(path);
        _runtimeManager?.SendToRuntime($"SAVE_ACTOR:{path}");
        EditorLog.Write($"ExecuteActorSave — SAVE_ACTOR:{path}");
    }

    private void OnSaveCompleted(bool ok, string errorMsg)
    {
        Dispatcher.BeginInvoke(() =>
        {
            // 成功・失敗どちらでも自己書き込み窓は閉じる（開いたままだと
            // 以後の外部変更を自分の保存と誤認して取り込めなくなる）。
            _sceneAutoReloader?.NotifySelfSaveCompleted();

            if (ok)
            {
                EditorLog.Write("OnSaveCompleted — 保存成功");
                MarkClean();
                bool wasActorSave = _isSavingActor;
                string toast = wasActorSave ? "アクターを保存しました" : "シーンを保存しました";
                _isSavingActor = false;
                ShowToast(toast);
                UpdateTitle();

                // プレハブ（.actor）を保存したときは、設定に従ってシーン内の
                // インスタンスへ自動反映する（件数のトーストは反映完了時に出す）。
                if (wasActorSave) PropagateSavedPrefabToScene();

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
                // 保存に失敗したので自動反映もしない（覚えていたパスを捨てる）。
                _savingActorPath = null;
                _pendingSceneLoad = null;
                EditorLog.Write($"OnSaveCompleted — 保存失敗: {errorMsg}");
                SEEDEditor.Headless.EditorDialogs.Show(
                    $"保存に失敗しました:\n{DescribeSaveError(errorMsg)}", "SEED Editor",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        });
    }

    /// <summary>ランタイムの SAVE_ERROR 本文を、原因の分かる日本語へ言い換える。</summary>
    private static string DescribeSaveError(string errorMsg)
    {
        // ランタイムはパス不一致を "path_mismatch:<実際に読み込んでいるパス>" で返す。
        const string mismatchTag = "path_mismatch:";
        if (errorMsg.StartsWith(mismatchTag, StringComparison.Ordinal))
        {
            var actual = errorMsg[mismatchTag.Length..];
            return "保存先とランタイムが実際に読み込んでいるシーンが違うため、書き込みを中止しました。\n"
                 + $"ランタイムが持っているシーン: {actual}\n"
                 + "別シーンの内容で上書きしてしまう事故を防ぐための保護です。"
                 + "対象のシーンを開き直してから保存してください。";
        }
        const string noSceneTag = "no_scene_loaded";
        if (errorMsg.Contains(noSceneTag, StringComparison.Ordinal))
            return "ランタイムがシーンを保持していないため保存できません。シーンを開き直してください。";

        return errorMsg;
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
        // 読み取り専用（他インスタンスがロック保持中）は必ずタイトルへ出す。
        // 保存できないことに気づかないまま作業を続ける事故を防ぐため。
        var readOnly = _sceneReadOnly ? $" {ReadOnlyTitleMark}" : "";
        Title = _isDirty ? $"SEED Editor — {name}*{readOnly}" : $"SEED Editor — {name}{readOnly}";
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
