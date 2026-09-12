using System;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SEEDEditor.Controls;

namespace SEEDEditor.Panels;

/// <summary>
/// インスペクタのロック機能（<see cref="InspectorPanel"/> の部分クラス）。
///
/// <para>
/// ヘッダーの鍵アイコンを ON にすると、他のアクターを選んでも
/// インスペクタの表示対象が切り替わらなくなる（Unity の Inspector Lock 相当）。
/// 「ヒエラルキーで別のアクターを触りながら、あるアクターの値を見比べる／
/// 参照をドロップする」ための足場。
/// </para>
///
/// <para><b>止めるのは「対象の切り替え」だけ</b>:
/// 選択の入口（<see cref="InspectorPanel.SelectActor"/> と SELECTED 通知）で
/// 別アクターへの切り替えを無視するだけで、ランタイムから届く
/// ACTOR_COMPONENTS の反映（値の同期）は止めない。
/// ロック中もビューポートでの移動やスクリプトによる変更は表示へ反映され続ける。</para>
///
/// <para><b>自動解除</b>:
/// DFS ID はツリーの走査順そのもの（＝位置）なので、手前のアクターが増減すると
/// 同じ ID が別のアクターを指してしまう。ロック対象を取り違えて別アクターを
/// 編集するのが一番まずいので、ヒエラルキー更新のたびに
/// 「ロックした ID の位置に、ロックしたときと同じ名前のアクターがいるか」を確かめる。
///   ・居る   … そのまま継続
///   ・居ない … 削除されたか ID がずれたかを区別できないので解除する
///              （同名アクターは珍しくないので「名前で探し直して追従」はしない。
///                別アクターへ乗り移って編集させる方が危険なため）
/// シーンの入れ替え（HIERARCHY_RESET）とモード切替でも解除する。
/// なお「ロック対象をリネームした」場合も名前一致が崩れるので解除される。
/// 取り違えを防ぐ側に倒した判断で、実害は「もう一度ロックし直す」だけ。</para>
///
/// <para><b>状態はセッション内のみ</b>。レイアウトにも設定にも保存しない。</para>
/// </summary>
public partial class InspectorPanel
{
    // ── 見た目の定数（マジックナンバー禁止）────────────────────────────

    /// <summary>鍵アイコンの一辺サイズ（px）。アクタ名の行に収まる値。</summary>
    private const double InspectorLockIconSize = 14.0;

    /// <summary>トグルの当たり判定を広げる内側余白（px）。</summary>
    private static readonly Thickness InspectorLockTogglePadding = new(3, 1, 1, 1);

    /// <summary>ロック中のアイコン不透明度。</summary>
    private const double InspectorLockOnOpacity = 1.0;

    /// <summary>非ロック時のアイコン不透明度（主張しすぎない）。</summary>
    private const double InspectorLockOffOpacity = 0.35;

    /// <summary>ロック中のアイコン色（有効であることを一目で分かるように着色する）。</summary>
    private static readonly Color InspectorLockOnColor = Color.FromRgb(0xE5, 0xC0, 0x7B);

    // ── アイコンキー（editor/gen_icons.py の CATALOG と対応）───────────

    /// <summary>ロック中のアイコン（閉じた鍵）。</summary>
    private const string InspectorLockOnIconKey = "Icon.Lock";

    /// <summary>非ロック時のアイコン（開いた鍵）。</summary>
    private const string InspectorLockOffIconKey = "Icon.LockOpen";

    // ── 表示文言 ───────────────────────────────────────────────────────

    /// <summary>ロック中のツールチップ。</summary>
    private const string InspectorLockOnToolTip =
        "インスペクタをロック中\n他のアクターを選んでも表示は切り替わりません"
        + "（値の更新は続きます）。\nクリックで解除。対象が消えたら自動で解除されます。";

    /// <summary>非ロック時のツールチップ。</summary>
    private const string InspectorLockOffToolTip =
        "インスペクタをロック\n表示中のアクターに固定し、他を選んでも切り替えません。\n"
        + "（エディタを閉じるまでの一時設定）";

    // ── ヒエラルキー JSON のキー（runtime の build_hierarchy_json と一致必須）──

    /// <summary>ヒエラルキーノードの DFS ID キー。</summary>
    private const string HierarchyNodeIdKey = "id";

    /// <summary>ヒエラルキーノードの名前キー。</summary>
    private const string HierarchyNodeNameKey = "name";

    // ── セッション状態（永続化しない）──────────────────────────────────

    /// <summary>インスペクタがロックされているか。</summary>
    private bool _inspectorLocked;

    /// <summary>ロック対象アクターの DFS ID。非ロック時は -1。</summary>
    private int _lockedActorDfsId = -1;

    /// <summary>
    /// ロックした時点の対象アクター名。
    /// DFS ID がずれたときの同一性チェックに使う（<see cref="ValidateInspectorLock"/>）。
    /// </summary>
    private string _lockedActorName = "";

    // ── 選択入口のガード ───────────────────────────────────────────────

    /// <summary>
    /// ロックによって、この DFS ID への切り替えを無視すべきか。
    /// ロック対象そのものへの再選択は通す（表示は変わらないので害がない）。
    /// </summary>
    /// <param name="dfsId">選択されたアクターの DFS ID。</param>
    private bool IsActorSwitchBlockedByLock(int dfsId)
        => _inspectorLocked && dfsId != _lockedActorDfsId;

    /// <summary>
    /// ロックによって、ランタイムの SELECTED 通知を無視すべきか。
    /// 未選択通知（-1）や、レガシーのインスタンス直接選択も含めて止める
    /// （どちらも表示対象の切り替えを意味するため）。
    /// </summary>
    /// <param name="selectedId">SELECTED で届いた ID（仮想ノード ID またはインスタンス添字）。</param>
    private bool IsSelectionNotifyBlockedByLock(int selectedId)
    {
        if (!_inspectorLocked) return false;
        if (selectedId < VirtualActorNodeIdBase) return true;     // 未選択・インスタンス直接選択
        return selectedId - VirtualActorNodeIdBase != _lockedActorDfsId;
    }

    // ── ロックの開始・解除 ─────────────────────────────────────────────

    /// <summary>
    /// ロックを掛ける。対象は現在表示中のアクター（DFS ID と名前を控える）。
    /// 表示中のアクターが無いときは何もしない。
    /// </summary>
    private void AcquireInspectorLock()
    {
        if (_currentActorId < 0) return;
        _inspectorLocked  = true;
        _lockedActorDfsId = _currentActorId;
        // 名前はヘッダーの表示テキストではなく、ランタイムから来た生の名前
        // （ACTOR_COMPONENTS の "name"）を使う。表示テキストは名前が空のときに
        // "Actor #3" へ置き換わるため、HIERARCHY のノード名とは決して一致しない。
        _lockedActorName  = _currentActorName;
        EditorLog.Write($"[Inspector.Lock] ロック: dfsId={_lockedActorDfsId} name={_lockedActorName}");
    }

    /// <summary>
    /// ロックを解除する。
    /// </summary>
    /// <param name="clearSelection">
    /// true なら表示も未選択へ戻す。ロック対象が消えた場合に使い、
    /// 実体の無いアクターの値を編集できる状態を残さないようにする。
    /// </param>
    /// <param name="reason">ログに残す理由。</param>
    private void ReleaseInspectorLock(bool clearSelection, string reason)
    {
        if (!_inspectorLocked && !clearSelection) return;
        if (_inspectorLocked)
            EditorLog.Write($"[Inspector.Lock] 解除（{reason}）: dfsId={_lockedActorDfsId}");

        _inspectorLocked  = false;
        _lockedActorDfsId = -1;
        _lockedActorName  = "";

        if (clearSelection)
        {
            // ShowNoSelection は _currentActorId を戻さないので、ここで明示的に戻す
            // （戻さないと同じ ID を選び直したときに SelectActor が
            //   「同一アクターへの重複選択」として弾いてしまう）。
            _currentActorId = -1;
            ShowNoSelection();
        }

        RefreshInspectorLockToggle();
    }

    // ── 自動解除（ヒエラルキー監視）────────────────────────────────────

    /// <summary>
    /// アクターツリーが丸ごと入れ替わった（シーン遷移・Play 停止の復元・LOAD_SCENE）。
    /// ロック対象はもう同じものではないので無条件に解除する。
    /// </summary>
    private void OnHierarchyResetForLock()
    {
        if (!_inspectorLocked) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (!_inspectorLocked) return;
            ReleaseInspectorLock(clearSelection: true, reason: "シーン切り替え");
        });
    }

    /// <summary>
    /// ヒエラルキー更新のたびにロック対象の生存を確かめる。
    /// ロック中でなければ JSON を解析すらしない（毎秒何度も届くため）。
    /// </summary>
    private void OnHierarchyUpdatedForLock(string json)
    {
        if (!_inspectorLocked) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (!_inspectorLocked) return;
            ValidateInspectorLock(json);
        });
    }

    /// <summary>
    /// ロック対象（ロックした DFS ID の位置に同じ名前のアクター）がまだ居るかを確かめ、
    /// 居なければロックを解除する。
    /// </summary>
    /// <param name="hierarchyJson">HIERARCHY のフラットなノード配列 JSON。</param>
    private void ValidateInspectorLock(string hierarchyJson)
    {
        bool idStillValid = false;

        try
        {
            using var doc = JsonDocument.Parse(hierarchyJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return;

            foreach (var node in doc.RootElement.EnumerateArray())
            {
                if (!node.TryGetProperty(HierarchyNodeIdKey, out var idEl) ||
                    !idEl.TryGetInt32(out var id)) continue;

                var name = node.TryGetProperty(HierarchyNodeNameKey, out var nameEl)
                    ? nameEl.GetString() ?? "" : "";

                if (id != _lockedActorDfsId) continue;
                idStillValid = name == _lockedActorName;
                break;
            }
        }
        catch (Exception ex)
        {
            // 解析できない応答でロックを壊さない（次の更新で判定し直す）
            EditorLog.Write($"[Inspector.Lock] HIERARCHY 解析失敗: {ex.Message}");
            return;
        }

        // そのままの位置に居る: 何もしない
        if (idStillValid) return;

        // 居ない＝削除された、または手前のアクターが増減して DFS ID がずれた。
        // 「同じ名前のノードを探して追従する」ことも考えられるが、同名アクターは
        // 珍しくないため、削除直後に別の同名アクターへ乗り移る危険がある。
        // 取り違えて別アクターを編集させるより、解除して選び直させる方が安全。
        ReleaseInspectorLock(clearSelection: true, reason: "対象が見つからない");
    }

    // ── トグル UI ──────────────────────────────────────────────────────

    /// <summary>
    /// ヘッダーの鍵トグルを作り直して状態に合わせる。
    /// アクターを表示していないときは意味が無いので出さない
    /// （「選択に応じて無関係な UI は出さない」方針に合わせる）。
    /// </summary>
    private void RefreshInspectorLockToggle()
    {
        if (_currentActorId < 0 && !_inspectorLocked)
        {
            InspectorLockToggleHost.Visibility = Visibility.Collapsed;
            InspectorLockToggleHost.Content    = null;
            return;
        }

        var icon = AppIcon.Create(
            _inspectorLocked ? InspectorLockOnIconKey : InspectorLockOffIconKey,
            InspectorLockIconSize);
        if (_inspectorLocked) icon.Foreground = new SolidColorBrush(InspectorLockOnColor);

        var host = new Border
        {
            Child               = icon,
            Background          = Brushes.Transparent,
            Padding             = InspectorLockTogglePadding,
            Cursor              = Cursors.Hand,
            Opacity             = _inspectorLocked ? InspectorLockOnOpacity : InspectorLockOffOpacity,
            ToolTip             = _inspectorLocked ? InspectorLockOnToolTip : InspectorLockOffToolTip,
            VerticalAlignment   = VerticalAlignment.Center,
        };
        host.PreviewMouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            if (_inspectorLocked) ReleaseInspectorLock(clearSelection: false, reason: "手動");
            else                  AcquireInspectorLock();
            RefreshInspectorLockToggle();
        };

        InspectorLockToggleHost.Content    = host;
        InspectorLockToggleHost.Visibility = Visibility.Visible;
    }
}
