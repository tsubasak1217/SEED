// ============================================================
//  MainWindow.RuntimeBuildConfig.cs
//  ランタイムのビルド構成（Debug / Develop / Release）切り替え UI
//
//  【役割】
//  ツールバー（Play ボタンの隣）のコンボボックスと、
//  RuntimeManager.SwitchBuildConfigAsync を繋ぐだけの層。
//
//  【なぜ独立ファイルか】
//  MainWindow は partial で責務ごとにファイルを分けてある（Camera / Scene / Input …）。
//  ビルド構成の切り替えは「ランタイムを落として建て直す」副作用の大きい操作で、
//  未保存確認・シーン復元・失敗時の選択巻き戻しまで含むため、1 箇所にまとめる。
//
//  【関連】
//  ・構成の定義   : editor/config/runtime_build_configs.json
//  ・選択の永続化 : editor/settings/editor_preferences.json（runtime_build_config_id）
//  ・仕様         : docs/runtime_build_configs.md
// ============================================================

using System;
using System.Windows;
using System.Windows.Controls;
using SEEDEditor.Runtime;
using SEEDEditor.Runtime.BuildConfig;

namespace SEEDEditor;

public partial class MainWindow
{
    // ── 表示文言（マジックストリングの一元化）────────────────────

    /// <summary>切り替えを受け付けたときのトースト書式（{0}=構成のラベル）。</summary>
    private const string BuildConfigToastSwitchingFormat = "ランタイムを {0} で再起動します";

    /// <summary>
    /// ランタイムをまだ起動していない（アセット不在などで見送っている）ときのトースト書式。
    /// このときは再起動せず、構成の差し替えと保存だけを行う。
    /// </summary>
    private const string BuildConfigToastDeferredFormat = "ビルド構成を {0} にしました（ランタイム起動時に反映されます）";

    /// <summary>Play 中で切り替えられなかったときのトースト。</summary>
    private const string BuildConfigToastRejected = "Play 中はビルド構成を切り替えられません。Stop してから選び直してください";

    /// <summary>切り替えに失敗したときのトースト書式（{0}=理由）。</summary>
    private const string BuildConfigToastFailedFormat = "ビルド構成の切り替えに失敗しました: {0}";

    /// <summary>未保存の変更があるときの確認ダイアログ本文。</summary>
    private const string BuildConfigDirtyConfirmMessage =
        "未保存の変更があります。\n" +
        "ビルド構成を切り替えるとランタイムを再起動するため、保存していない変更は失われます。\n\n" +
        "このまま切り替えますか？";

    /// <summary>確認ダイアログのタイトル。</summary>
    private const string BuildConfigDirtyConfirmTitle = "ビルド構成の切り替え";

    // ── 状態 ─────────────────────────────────────────────────

    /// <summary>
    /// コンボの選択をコードから書き換えている間 true。
    /// 初期化と「失敗時の巻き戻し」で SelectionChanged が再入するのを防ぐ。
    /// </summary>
    private bool _suppressRuntimeBuildConfigSelection;

    // ── 初期化 ───────────────────────────────────────────────

    /// <summary>
    /// ビルド構成コンボへカタログを流し込み、現在の選択を反映する。
    /// RuntimeManager 生成後（＝ 起動に使った構成が確定した後）に呼ぶ。
    /// </summary>
    private void InitRuntimeBuildConfigCombo()
    {
        _suppressRuntimeBuildConfigSelection = true;
        try
        {
            CmbRuntimeBuildConfig.ItemsSource  = RuntimeBuildCatalog.Configs;
            // 起動に実際に使った構成を選ぶ（環境設定の id が未知だった場合は既定へ丸められている）。
            CmbRuntimeBuildConfig.SelectedItem = _runtimeManager?.BuildConfig ?? CurrentRuntimeBuildConfig;
        }
        finally
        {
            _suppressRuntimeBuildConfigSelection = false;
        }
    }

    /// <summary>
    /// 実行状態に応じてコンボの操作可否を切り替える（ApplyUiState から呼ぶ）。
    /// Edit と Idle のときだけ切り替えられる。
    /// Play / Pause / Launching / Building 中はランタイムを差し替えられないため無効化する。
    /// </summary>
    /// <param name="state">現在のエディタ状態。</param>
    private void UpdateRuntimeBuildConfigEnabled(EditorState state)
    {
        CmbRuntimeBuildConfig.IsEnabled =
            state is EditorState.Edit or EditorState.Idle;
    }

    // ── 選択変更 ─────────────────────────────────────────────

    /// <summary>
    /// コンボの選択が変わったとき: 設定を保存し、ランタイムを新しい構成で再起動する。
    ///
    /// <para>
    /// <c>async void</c>（WPF のイベントハンドラ）なので、例外は必ずここで受け止める。
    /// 外へ漏らすとハンドルされない例外としてエディタごと落ちる。
    /// </para>
    /// </summary>
    private async void OnRuntimeBuildConfigChanged(object sender, SelectionChangedEventArgs e)
    {
        // コードからの書き換え（初期化・巻き戻し）は無視する
        if (_suppressRuntimeBuildConfigSelection) return;

        if (CmbRuntimeBuildConfig.SelectedItem is not RuntimeBuildConfig next) return;
        if (_runtimeManager is null) return;

        var current = _runtimeManager.BuildConfig;
        if (string.Equals(current.Id, next.Id, RuntimeBuildConfig.IdComparison)) return;

        // ① 未保存の変更があるなら確認する（再起動でランタイム上の編集内容は失われる）。
        //    ヘッドレスではモーダルを誰も閉じられないので確認せず進む。
        if (_isDirty && !SEEDEditor.Headless.EditorStartupOptions.IsHeadless)
        {
            var answer = MessageBox.Show(
                BuildConfigDirtyConfirmMessage, BuildConfigDirtyConfirmTitle,
                MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes)
            {
                RevertRuntimeBuildConfigSelection(current);
                return;
            }
        }

        // ② 選択を先に永続化する。再起動に失敗しても「次回は選んだ構成で起動する」ほうが
        //    ユーザーの意図に近い（失敗理由はビルドエラーで、構成そのものは正しいため）。
        EditorPreferences.Instance.RuntimeBuildConfigId = next.Id;
        EditorPreferences.Save();

        // ③ 再起動（未ビルドならここで cargo build が走るので時間がかかる）。
        //    ランタイムを一度も起動していない場合は建て直しも起きないので、文言を分ける。
        ShowToast(string.Format(
            _editRuntimeStarted ? BuildConfigToastSwitchingFormat : BuildConfigToastDeferredFormat,
            next.Label));

        try
        {
            var accepted = await _runtimeManager.SwitchBuildConfigAsync(next);
            if (!accepted)
            {
                // Play 中に切り替えようとした（コンボは無効化しているが念のため）
                ShowToast(BuildConfigToastRejected);
                RevertRuntimeBuildConfigSelection(current);
                EditorPreferences.Instance.RuntimeBuildConfigId = current.Id;
                EditorPreferences.Save();
                return;
            }

            // ④ 新しいランタイムは既定シーンで立ち上がるため、開いていたシーンを読み直す。
            //    （起動時のシーン復元は一度きりの経路なので、ここでは明示的に読み直す）
            ReloadCurrentSceneAfterRuntimeRestart();
        }
        catch (Exception ex)
        {
            // cargo build 失敗・プロセス起動失敗など。状態は Idle のまま残る。
            EditorLog.Write($"OnRuntimeBuildConfigChanged — 切り替え失敗: {ex}");
            ShowToast(string.Format(BuildConfigToastFailedFormat, ex.Message));
        }
    }

    /// <summary>
    /// コンボの選択を指定の構成へ戻す（SelectionChanged を再発火させない）。
    /// </summary>
    /// <param name="config">戻す先の構成。</param>
    private void RevertRuntimeBuildConfigSelection(RuntimeBuildConfig config)
    {
        _suppressRuntimeBuildConfigSelection = true;
        try { CmbRuntimeBuildConfig.SelectedItem = config; }
        finally { _suppressRuntimeBuildConfigSelection = false; }
    }

    /// <summary>
    /// ビルド構成の切り替えでランタイムを建て直したあと、開いていたシーンを読み直す。
    ///
    /// <para>
    /// 新しい Edit ランタイムはプロジェクトの開始シーンで立ち上がるため、
    /// これを呼ばないと切り替えのたびに別のシーンが開いてしまう。
    /// ランタイムが Edit まで来ていない（ビルド失敗・起動失敗）ときは何もしない。
    /// </para>
    /// </summary>
    private void ReloadCurrentSceneAfterRuntimeRestart()
    {
        if (_currentScenePath is null) return;
        if (_runtimeManager?.State != EditorState.Edit) return;

        EditorLog.Write($"ビルド構成切り替え後のシーン復元 — {_currentScenePath}");
        LoadScene(_currentScenePath);
    }
}
