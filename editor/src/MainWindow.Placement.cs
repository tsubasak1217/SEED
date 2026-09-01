// ============================================================
//  MainWindow.Placement.cs — ロジック配置「配置モード」のエディタ側の受け口
//
//  担当:
//   - ランタイムからの `PLACEMENT_STATE:0|1` を受けて進行フラグを更新する
//   - 進行中だけ操作ヒント（「クリックで配置 / 右クリックで取消」）を出す
//   - Esc を「削除ダイアログ」ではなく「配置の取消」へ回す
//     （実際の分岐は MainWindow.Input.cs のキーフックが `_placementModeActive` を見る）
//
//  【モーダルトランスフォーム（MainWindow.ModalTransform.cs）との違い】
//  あちらはカーソルがシーンパネルの外へ出ても変形を続ける必要があるため、
//  低レベルマウスフックでカーソルを追跡して IPC で送り込んでいる。
//  配置モードは「シーンの中を指してクリックする」操作なのでカーソルは常に
//  シーンパネル内にあり、ランタイムの子ウィンドウがマウスイベントを直接受け取れる。
//  よってエディタ側にフックは要らず、**状態フラグと表示だけ**を持つ。
// ============================================================

using System;

namespace SEEDEditor;

public partial class MainWindow
{
    /// <summary>配置モード中に出す操作ヒント。</summary>
    private const string PlacementHintText = "配置モード: 左クリックで配置 / 右クリック・Esc で取消";

    /// <summary>
    /// 半径ドラッグで決めた半径として受け入れる下限 [m]。
    ///
    /// ランタイム側も 0 にはしないが、IPC で壊れた値が来た場合に
    /// ダイアログの前回値を壊さないための最後の砦。
    /// </summary>
    private const float PlacementMinRadius = 0.001f;

    /// <summary>
    /// ランタイムからの配置モード進行状態通知（<c>PLACEMENT_STATE:0|1</c>）を受ける。
    ///
    /// IPC 受信はバックグラウンドスレッドなので、フラグ（volatile）だけ即座に更新し、
    /// UI 要素の更新は Dispatcher へマーシャルする。
    /// </summary>
    /// <param name="active">true = 配置モードへ入った / false = 終了した。</param>
    private void OnPlacementStateChanged(bool active)
    {
        _placementModeActive = active;
        if (Dispatcher.CheckAccess()) ApplyPlacementHint();
        else                          Dispatcher.BeginInvoke(ApplyPlacementHint);
    }

    /// <summary>現在の進行フラグに合わせて操作ヒントの表示を揃える（UI スレッド専用）。</summary>
    private void ApplyPlacementHint()
    {
        if (TxtPlacementHint is null) return;
        TxtPlacementHint.Text = _placementModeActive ? PlacementHintText : "";
        TxtPlacementHint.Visibility = _placementModeActive
            ? System.Windows.Visibility.Visible
            : System.Windows.Visibility.Collapsed;
    }

    /// <summary>
    /// 配置モードを取り消すようランタイムへ依頼する（Esc 経路）。
    ///
    /// フラグはここでは落とさない。ランタイムが取消を実行して
    /// <c>PLACEMENT_STATE:0</c> を返すまで待つ
    /// （エディタが先回りして落とすと、取消が届かなかった場合に
    ///   「モード中なのにヒントが消えている」不整合が残る）。
    /// </summary>
    private void SendPlacementCancel() => _runtimeManager?.SendToRuntime("PLACEMENT_CANCEL");

    /// <summary>
    /// ビューポート上の半径ドラッグで決まった半径（<c>PLACEMENT_RADIUS:{値}</c>）を受ける。
    ///
    /// <para>
    /// ダイアログの「前回値」（<see cref="EditorPreferences.LogicPlacement"/>）へ書き戻し、
    /// 次に開いたときの初期値にする。ダイアログはこの時点で既に閉じているので、
    /// 設定オブジェクトを直接更新して保存するのが唯一の経路である。
    /// </para>
    ///
    /// <para>
    /// 前回値がまだ無い（＝一度も「配置」していない）場合は、既定値の
    /// <see cref="Placement.Patterns.PlacementSpec"/> を作ってそこへ入れる。
    /// </para>
    /// </summary>
    /// <param name="radius">ドラッグで確定した半径 [m]。</param>
    private void OnPlacementRadiusChanged(float radius)
    {
        if (!(radius >= PlacementMinRadius) || float.IsInfinity(radius)) return; // NaN もここで弾く

        void Apply()
        {
            var spec = EditorPreferences.Instance.LogicPlacement ?? new Placement.Patterns.PlacementSpec();
            spec.Radius = radius;
            EditorPreferences.Instance.LogicPlacement = spec;
            EditorPreferences.Save();
        }

        // IPC 受信はバックグラウンドスレッド。設定の保存は UI スレッドへ寄せる
        // （EditorPreferences は他の UI 操作からも触られるため）。
        if (Dispatcher.CheckAccess()) Apply();
        else                          Dispatcher.BeginInvoke(Apply);
    }
}
