// ============================================================
//  MainWindow.CanvasEdit.cs — キャンバス編集タブ（シーン内キャンバスの隔離編集）
//
//  担当:
//   - インスペクタ「キャンバスを編集」ボタンからのキャンバス編集タブ開始
//   - CANVAS_EDIT_WL 応答によるタブ生成
//   - キャンバス編集タブの終了（シーンへの復帰）ヘルパー
//
//  ファイルベースのアクター編集タブと異なり、シーン世界線（0）のアクターを
//  ランタイム側で専用世界線へ「移動」して編集する（EDIT_CANVAS_BEGIN /
//  EDIT_CANVAS_END）。編集はそのままシーンに反映されるため保存確認は不要。
//  シーン保存・Play・シーン切替の前には必ずタブを閉じてアクターを
//  シーンへ戻す（開いたまま保存するとアクターが編集用世界線に居るため
//  シーンファイルへ正しく書き出されない）。
// ============================================================

using System;
using System.Linq;
using SEEDEditor.Runtime;

namespace SEEDEditor;

public partial class MainWindow
{
    // ── キャンバス編集タブの開始 ───────────────────────────────

    /// <summary>
    /// インスペクタの「キャンバスを編集」ボタンが押されたときの処理。
    /// 新しい世界線を割り当てて EDIT_CANVAS_BEGIN を送信する。
    /// タブの生成はランタイムからの CANVAS_EDIT_WL 応答（OnCanvasEditStarted）で行う。
    /// </summary>
    private void OnCanvasEditRequested(int actorDfsId)
    {
        if (_runtimeManager?.State != EditorState.Edit) return;
        if (actorDfsId < 0) return;
        // アクター編集タブ表示中はボタン自体が非表示だが、念のためガードする
        if (_activeActorPath != null) return;

        var wl = _nextWorldLineIdx++;
        SendNavCommand($"EDIT_CANVAS_BEGIN:{wl},{actorDfsId}");
        EditorLog.Write($"OnCanvasEditRequested — EDIT_CANVAS_BEGIN:{wl},{actorDfsId}");
    }

    /// <summary>
    /// ランタイムからの CANVAS_EDIT_WL 応答でキャンバス編集タブを生成する。
    /// この直後に届く ACTOR_EDIT_STARTED がヒエラルキー・インスペクタを
    /// アクター編集モード（2D）へ切り替える（OnActorEditStarted は最後のタブを参照する）。
    /// </summary>
    private void OnCanvasEditStarted(uint worldLine, bool rootIs2D, string actorName)
    {
        Dispatcher.InvokeAsync(() =>
        {
            // 合成キー（ファイルパスの代わり）。ファイルベースのタブと衝突しない形式にする。
            var key = $"canvas://{worldLine}";
            if (_actorTabs.Any(t => t.Path == key)) return;

            _actorTabs.Add(new ActorTab(
                key, actorName, worldLine,
                IsActor2D: true,            // キャンバスの中身は常に 2D 編集
                IsSceneCanvas: true,
                RootIs2D: rootIs2D));
            _activeActorPath = key;
            RebuildActorTabBar();
            EditorLog.Write($"OnCanvasEditStarted — キャンバス編集タブ生成 wl={worldLine} name={actorName}");
        });
    }

    // ── キャンバス編集タブの終了 ───────────────────────────────

    /// <summary>
    /// アクティブなキャンバス編集タブを終了してシーンモードへ戻す。閉じた場合は true。
    /// EDIT_CANVAS_END によりランタイム側がアクターをシーンへ戻して世界線 0 へ復帰し
    /// ACTOR_EDIT_ENDED を送信するため、こちらから SET_ACTIVE_WORLD_LINE:0 は送らない。
    /// 戻り先のシーンタブはキャンバス所有アクターの元の種別（2D / 3D）に合わせる。
    /// </summary>
    private bool CloseActiveSceneCanvasTab()
    {
        var tab = _actorTabs.FirstOrDefault(t => t.IsSceneCanvas && t.Path == _activeActorPath);
        if (tab is null) return false;

        _actorTabs.Remove(tab);
        _activeActorPath = null;
        // 所有アクターの種別に対応するシーンタブ（ビューポート / ワールド）へ戻す
        _sceneTabIs2D = tab.RootIs2D;
        SendNavCommand($"EDIT_CANVAS_END:{tab.WorldLine}");
        PanelHierarchy.SetActorEditMode(false);
        PanelInspector.SetActorEditMode(false);
        // シーンタブのビューモードをランタイムと同期する
        SendCurrentEditView();
        RebuildActorTabBar();
        EditorLog.Write($"CloseActiveSceneCanvasTab — EDIT_CANVAS_END:{tab.WorldLine}");
        return true;
    }

    /// <summary>
    /// アクティブでないキャンバス編集タブをすべて終了して閉じる。
    /// 別タブへ移動した直後に呼び出すこと（アクティブな世界線のまま END すると
    /// ランタイムが世界線 0 へ戻ってしまうため、先に移動先の
    /// SET_ACTIVE_WORLD_LINE / OPEN_ACTOR を送っておく必要がある）。
    /// </summary>
    private void EndInactiveSceneCanvasTabs()
    {
        bool removed = false;
        for (int i = _actorTabs.Count - 1; i >= 0; i--)
        {
            var t = _actorTabs[i];
            if (!t.IsSceneCanvas) continue;
            if (t.Path == _activeActorPath) continue;   // アクティブなタブは対象外
            _actorTabs.RemoveAt(i);
            // 非アクティブ世界線の END: ランタイムはアクターをシーンへ戻すだけで世界線は切り替えない
            _runtimeManager?.SendToRuntime($"EDIT_CANVAS_END:{t.WorldLine}");
            EditorLog.Write($"EndInactiveSceneCanvasTabs — EDIT_CANVAS_END:{t.WorldLine}");
            removed = true;
        }
        if (removed) RebuildActorTabBar();
    }
}
