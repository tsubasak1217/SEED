// ============================================================
//  MainWindow.SceneTabs.cs — ワールド / ビューポート 固定タブ
//
//  分類の考え方:
//   - ワールド    = カメラに撮られて相対的に映るもの
//                   （3D アクター、および 3D ワールドキャンバス上の 2D スプライトを含む）
//   - ビューポート = 画面/カメラ枠基準で張り付くもの
//                   （スクリーンスペースキャンバスとその子孫）
//
//  担当:
//   - タブバー左端の固定シーンタブ（「ワールド」「ビューポート」）の UI 生成
//   - シーンタブ選択時の EDIT_VIEW IPC 送信とアクター編集モードからの復帰
//   - ヒエラルキー選択（ビューポート所属 is_vp）に応じたシーンタブの自動切替
//     （環境設定でオン/オフ）
//   - プロジェクトパネルからのアクタドラッグ中の仮切替と復帰
//
//  ランタイム側は Edit モード中のみ EDIT_VIEW:3d / EDIT_VIEW:2d を受け付け、
//  ワールドビュー（3d）ではスクリーンスペースキャンバスを非表示、
//  ビューポートビュー（2d）ではワールドを非表示にする（Phase 1 実装済み）。
//  ※ IPC のワイヤフォーマット（EDIT_VIEW:3d/2d）は内部表現のため変更しない。
// ============================================================

using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SEEDEditor.Runtime;

namespace SEEDEditor;

public partial class MainWindow
{
    // ── シーンタブ状態 ─────────────────────────────────────────

    /// <summary>現在選択中のシーンタブが「ビューポート」なら true（既定は「ワールド」）。</summary>
    private bool _sceneTabIs2D = false;

    // ── ドラッグ仮切替状態 ─────────────────────────────────────

    /// <summary>アクタドラッグによるシーンタブ仮切替中なら true。</summary>
    private bool _sceneTabProvisional = false;
    /// <summary>仮切替前にアクティブだったアクター編集タブのパス（シーンタブだった場合は null）。</summary>
    private string? _sceneTabPrevActorPath = null;
    /// <summary>仮切替前のシーンタブ（2D かどうか）。</summary>
    private bool _sceneTabPrev2D = false;

    /// <summary>現在のシーンタブに対応する EDIT_VIEW コマンド文字列を返す。</summary>
    private string CurrentEditViewCommand()
        => _sceneTabIs2D ? "EDIT_VIEW:2d" : "EDIT_VIEW:3d";

    /// <summary>現在のシーンタブのビューモードをランタイムへ送信する。</summary>
    private void SendCurrentEditView()
        => _runtimeManager?.SendToRuntime(CurrentEditViewCommand());

    // ── シーンタブのアクティブ化 ───────────────────────────────

    /// <summary>
    /// シーンタブ（ワールド / ビューポート）をアクティブにする。
    /// アクター編集タブから戻る場合は、既存のタブ切替と同じ手順で
    /// シーン世界線（world line 0）へ復帰してからビューモードを送信する。
    /// </summary>
    private void ActivateSceneTab(bool is2D)
    {
        if (_runtimeManager?.State != EditorState.Edit) return;

        // キャンバス編集タブ表示中ならタブごと閉じてシーンへ戻す
        //（EDIT_CANVAS_END でランタイムがアクターをシーンへ戻す）
        CloseActiveSceneCanvasTab();

        // アクター編集タブ表示中ならシーンモードへ戻す（「シーンに戻る」と同じ手順）
        if (_activeActorPath != null)
        {
            SendNavCommand("SET_ACTIVE_WORLD_LINE:0");
            _activeActorPath = null;
            PanelHierarchy.SetActorEditMode(false);
            PanelInspector.SetActorEditMode(false);
        }

        _sceneTabIs2D = is2D;
        // ランタイムへビューモード切替を送信する（Play 中はランタイム側が無視する）
        SendCurrentEditView();
        RebuildActorTabBar();
        EditorLog.Write($"ActivateSceneTab — {CurrentEditViewCommand()}");
    }

    /// <summary>固定シーンタブがクリックされたときの処理。</summary>
    private void OnSceneTabClicked(bool is2D)
    {
        // 既にアクティブ（シーンモードかつ同じタブ）なら何もしない
        if (_activeActorPath == null && _sceneTabIs2D == is2D) return;
        ActivateSceneTab(is2D);
    }

    // ── ヒエラルキー選択による自動切替 ─────────────────────────

    /// <summary>
    /// ヒエラルキー選択のワールド/ビューポート所属が一意に定まったときに呼ばれる。
    /// isViewport はアクターの「ビューポート所属」（= トップレベルルートが Actor2D の
    /// スクリーンスペースキャンバス系サブツリーに属するか）で判定される。
    /// 3D ワールドキャンバス配下の 2D スプライトはワールド所属となる。
    /// 環境設定で有効かつ Edit モードでシーンタブ表示中のときのみ、
    /// 選択アクタの所属に対応するシーンタブへ自動で切り替える。
    /// （アクター編集タブ表示中はユーザーをタブから引き剥がさない）
    /// </summary>
    private void OnHierarchySelectionKindResolved(bool isViewport)
    {
        if (!EditorPreferences.Instance.SceneTabAutoSwitch) return;
        if (_runtimeManager?.State != EditorState.Edit) return;
        if (_activeActorPath != null) return;        // アクター編集タブ表示中は切替しない
        if (_sceneTabIs2D == isViewport) return;     // 既に一致している
        ActivateSceneTab(isViewport);
    }

    // ── ドラッグ中の仮切替 ─────────────────────────────────────

    /// <summary>
    /// プロジェクトパネルのドラッグ開始時に呼ばれる。ドラッグ対象にアクタファイルが
    /// 含まれる場合、拡張子（.actor2d = ビューポート / .actor = ワールド）で種別を判定し、
    /// 現在のタブと異なるなら「仮切替」としてシーンタブを切り替える。
    /// ドロップが成立しなかった場合は <see cref="EndActorDragSceneTabSwitch"/> で元に戻す。
    /// </summary>
    internal void BeginActorDragSceneTabSwitch(string[] paths)
    {
        if (_runtimeManager?.State != EditorState.Edit) return;

        // 先頭のアクタファイルから種別を判定する（アクタファイルが無ければ何もしない）
        bool? kind2D = null;
        foreach (var p in paths)
        {
            if (p.EndsWith(".actor2d", StringComparison.OrdinalIgnoreCase)) { kind2D = true;  break; }
            if (p.EndsWith(".actor",   StringComparison.OrdinalIgnoreCase)) { kind2D = false; break; }
        }
        if (kind2D is not bool target) return;

        // 既に対応するシーンタブが表示中なら仮切替は不要
        if (_activeActorPath == null && _sceneTabIs2D == target) return;

        // 復帰用に現在のタブ状態を記憶してから切り替える
        _sceneTabPrevActorPath = _activeActorPath;
        _sceneTabPrev2D        = _sceneTabIs2D;
        _sceneTabProvisional   = true;
        ActivateSceneTab(target);
        EditorLog.Write($"BeginActorDragSceneTabSwitch — 仮切替 {(target ? "ビューポート" : "ワールド")}");
    }

    /// <summary>
    /// プロジェクトパネルのドラッグ終了時に呼ばれる。
    /// placed=true（ドロップ成立）なら仮切替したタブを維持し、
    /// false（キャンセル・枠外リリース）なら元のタブへ戻す。
    /// </summary>
    internal void EndActorDragSceneTabSwitch(bool placed)
    {
        if (!_sceneTabProvisional) return;
        _sceneTabProvisional = false;

        if (placed)
        {
            // ドロップ成立: 切り替えたタブを確定する
            _sceneTabPrevActorPath = null;
            return;
        }

        // キャンセル: 仮切替前のタブへ復帰する
        if (_sceneTabPrevActorPath is string actorPath)
            ActivateActorTab(actorPath);
        else if (_sceneTabPrev2D != _sceneTabIs2D || _activeActorPath != null)
            ActivateSceneTab(_sceneTabPrev2D);
        _sceneTabPrevActorPath = null;
        EditorLog.Write("EndActorDragSceneTabSwitch — 仮切替を復帰");
    }

    // ── 固定タブ UI 生成 ───────────────────────────────────────

    /// <summary>
    /// 固定シーンタブ（「ワールド」または「ビューポート」）の UI 要素を生成する。
    /// 見た目はアクター編集タブと揃えるが、× ボタンは持たない。
    /// RebuildActorTabBar() から呼ばれる。
    /// </summary>
    private Border BuildSceneTabItem(string label, bool is2D)
    {
        // シーンモード（アクター編集タブ非アクティブ）かつ種別一致でアクティブ表示
        bool isActive = _activeActorPath == null && _sceneTabIs2D == is2D;

        var tabBorder = new Border
        {
            Height          = 28,
            MinWidth        = 60,
            Cursor          = Cursors.Hand,
            Background      = isActive
                                ? new SolidColorBrush(Color.FromRgb(0x3C, 0x3C, 0x3C))
                                : Brushes.Transparent,
            BorderBrush     = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55)),
            BorderThickness = isActive
                                ? new Thickness(0, 2, 1, 0)   // 上 2px はアクセント色
                                : new Thickness(0, 0, 1, 0),
        };

        // アクティブタブはオレンジのトップアクセント（アクター編集タブと共通の配色）
        if (isActive)
            tabBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(0xE8, 0x78, 0x20));

        // ホバー効果
        tabBorder.MouseEnter += (s, e) =>
        {
            if (!(_activeActorPath == null && _sceneTabIs2D == is2D))
                tabBorder.Background = new SolidColorBrush(Color.FromRgb(0x2E, 0x2E, 0x2E));
        };
        tabBorder.MouseLeave += (s, e) =>
        {
            if (!(_activeActorPath == null && _sceneTabIs2D == is2D))
                tabBorder.Background = Brushes.Transparent;
        };

        // クリックでシーンタブへ切替
        tabBorder.MouseLeftButtonDown += (s, e) => OnSceneTabClicked(is2D);

        // タブ内テキスト（種別アイコン色はヒエラルキーの 2D/3D アイコン色と合わせる）
        var iconColor = is2D
            ? Color.FromRgb(0xFF, 0x88, 0x44)   // 2D: オレンジ系
            : Color.FromRgb(0x55, 0xAA, 0xFF);  // 3D: 青系
        var tb = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            Margin            = new Thickness(10, 0, 10, 0),
        };
        tb.Inlines.Add(new System.Windows.Documents.Run("◆ ")
        {
            Foreground = new SolidColorBrush(iconColor),
            FontSize   = 9,
        });
        tb.Inlines.Add(new System.Windows.Documents.Run(label)
        {
            Foreground = isActive ? Brushes.White
                                  : new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA)),
            FontSize   = 12,
        });
        tabBorder.Child = tb;
        return tabBorder;
    }
}
