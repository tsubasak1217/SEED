// ============================================================
//  MainWindow.SceneAutoReload.cs — シーンの自動再読込（外部変更の取り込み）
//
//  担当:
//   - SceneAutoReloader（src/Scene/SceneAutoReloader.cs）の生成と依存注入
//   - 監視対象シーンの張り替え（シーンを開いた / 名前を付けて保存した）
//   - エディタ自身の保存（SAVE_SCENE）を自己書き込みとして除外させる橋渡し
//   - Play 終了（Edit 復帰）時の保留分の消化
//   - 上部バーへのステータス表示と、「表示 > シーン」メニューの操作
//
//  再読込そのものは LoadScene（MainWindow.FileOps.cs）に完全に委譲する。
//  ＝「プロジェクトパネルからシーンを開いた」ときと同じ経路であり、
//    シーン設定・ビュー状態・ヒエラルキーの再構築もそちらの責務のまま。
// ============================================================

using System;
using System.Windows;
using System.Windows.Media;
using SEEDEditor.Runtime;
using SEEDEditor.Scene;

namespace SEEDEditor;

public partial class MainWindow
{
    /// <summary>
    /// シーン自動再読込（今開いている .scene の外部変更を監視して読み直す）。
    /// 監視の起動に失敗した場合は内部の watcher が null＝無効のまま動く。
    /// </summary>
    private SceneAutoReloader? _sceneAutoReloader;

    /// <summary>
    /// シーン自動再読込を初期化する。
    ///
    /// SceneAutoReloader はランタイムや UI を知らないため、ここで
    /// 「設定値」「未保存判定」「Play 判定」「読み込み」「状態表示」を注入する。
    /// </summary>
    private void InitSceneAutoReloader()
    {
        _sceneAutoReloader = new SceneAutoReloader(
            Dispatcher,
            // 設定トグル（表示 > シーン > シーンを自動再読込）
            isEnabled: () => EditorPreferences.Instance.AutoReloadScene,
            // 未保存の編集があるか（あるときは破棄になるため再読込しない）
            isDirty: () => _isDirty,
            // Play 中（埋め込み・別ウィンドウとも State で表現される）
            isPlaying: () => _runtimeManager?.State is EditorState.Play or EditorState.Pause,
            // 読み込みはファイルを開いたときと同じ経路
            loadScene: LoadScene,
            report: SetSceneReloadStatus);

        // 既に開いているシーンがあれば、その時点から監視を始める
        _sceneAutoReloader.SetScenePath(_currentScenePath);
    }

    /// <summary>
    /// 監視対象シーンを現在のパスへ張り替える。
    /// LoadScene（シーンを開いた）と ExecuteSave（名前を付けて保存でパスが変わった）から呼ぶ。
    /// </summary>
    private void RetargetSceneAutoReloader()
        => _sceneAutoReloader?.SetScenePath(_currentScenePath);

    /// <summary>
    /// シーン再読込の進行状態を上部バーへ表示する（数秒で自動的に消える）。
    /// 色分けはスクリプト自動再読込と共通のブラシを使う（同じ表示欄のため）。
    /// </summary>
    private void SetSceneReloadStatus(SceneReloadStatus status, string message)
    {
        var brush = status switch
        {
            SceneReloadStatus.Running => ScriptStatusBrushRunning,
            SceneReloadStatus.Success => ScriptStatusBrushSuccess,
            SceneReloadStatus.Skipped => ScriptStatusBrushWarn,
            _                         => ScriptStatusBrushError,
        };

        ShowReloadStatusText(brush, message);

        if (status != SceneReloadStatus.Running)
            EditorLog.Write($"[SceneAutoReload] {message}");
    }

    /// <summary>
    /// 「表示 > シーン > シーンを自動再読込」トグル。
    /// EditorPreferences.AutoReloadScene へ永続化する。
    /// </summary>
    private void OnToggleAutoReloadScene(object sender, RoutedEventArgs e)
    {
        bool on = MenuItemAutoReloadScene.IsChecked;
        EditorPreferences.Instance.AutoReloadScene = on;
        EditorPreferences.Save();
        EditorLog.Write($"AutoReloadScene = {on}");
    }

    /// <summary>
    /// 「表示 > シーン > シーンをディスクから再読込」（手動）。
    /// 自動再読込がオフのとき、および未保存の編集があって自動再読込を見送った
    /// ときの明示的な取り込み手段。未保存の編集は破棄されるため確認を挟む。
    /// </summary>
    private void OnMenuReloadSceneFromDisk(object sender, RoutedEventArgs e)
    {
        if (_currentScenePath is null)
        {
            ShowToast("シーンがまだ保存されていないため再読込できません");
            return;
        }

        if (_isDirty)
        {
            var result = MessageBox.Show(
                "未保存の変更があります。ディスクから再読込すると変更は失われます。続行しますか？",
                "SEED Editor",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Warning);
            if (result != MessageBoxResult.OK) return;
        }

        _sceneAutoReloader?.ForceReload();
    }
}
