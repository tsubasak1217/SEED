using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace SEEDEditor.Panels;

/// <summary>
/// HierarchyPanel の「アクタへジャンプして一時ハイライト」機能。
///
/// インスペクタのアクタ参照フィールド（例: Canvas の基準領域カメラ参照）を
/// ダブルクリックしたときの飛び先として使う。
/// 選択状態は一切変更しない（＝選択変更イベントを発火させない）。あくまで
/// 「そこにある」ことを一定時間の色付けで示すだけの、副作用のない可視化である。
/// </summary>
public partial class HierarchyPanel
{
    // ── ジャンプ時の一時ハイライト定数 ────────────────────────────

    /// <summary>一時ハイライトを表示し続ける時間（ミリ秒）。</summary>
    private const int RevealHighlightDurationMs = 2000;

    /// <summary>
    /// 一時ハイライトの色（黄色・半透明）。
    /// 選択色（青系 #3399FF44）・ホバー色（白 #22FFFFFF）と明確に区別できる色にしている。
    /// </summary>
    private static readonly Brush RevealHighlightBrush =
        MakeFrozen(Color.FromArgb(0x77, 0xFF, 0xDD, 0x44));

    /// <summary>
    /// XAML の TreeViewItem テンプレート内にあるハイライト用 Border の名前。
    /// 選択・ホバーのトリガーもこの Border の Background を塗る（HierarchyPanel.xaml）。
    /// </summary>
    private const string RevealHighlightBorderName = "RowHighlight";

    // ── 一時ハイライトの状態 ──────────────────────────────────────

    /// <summary>現在ハイライト中の Border（時間切れ・次のジャンプで元に戻す対象）。</summary>
    private Border? _revealHighlightBorder;

    /// <summary>一時ハイライトの解除タイマー。</summary>
    private DispatcherTimer? _revealHighlightTimer;

    // ── 公開 API ──────────────────────────────────────────────────

    /// <summary>
    /// DFS ID で指定したアクタの行までスクロールし、一定時間ハイライトする。
    /// 選択状態は変更しない。
    /// </summary>
    /// <param name="dfsId">対象アクタの DFS ID。</param>
    /// <returns>対象が見つかってジャンプできたら true。</returns>
    public bool RevealActor(int dfsId)
        => RevealActorCore(node => node.Id == dfsId);

    /// <summary>
    /// 名前で指定したアクタの行までスクロールし、一定時間ハイライトする。
    /// 選択状態は変更しない。
    ///
    /// アクタ参照フィールドが「アクタ名」で参照を保持している場合（例: Canvas の
    /// 基準領域カメラ参照 = SET_CANVAS_VIEWPORT_REF_CAMERA のアクタ名）用の入口。
    /// 同名アクタが複数ある場合は DFS 順で最初に見つかったものへジャンプする。
    /// </summary>
    /// <param name="actorName">対象アクタの名前（完全一致・大文字小文字は区別する）。</param>
    /// <returns>対象が見つかってジャンプできたら true。</returns>
    public bool RevealActorByName(string actorName)
    {
        if (string.IsNullOrEmpty(actorName)) return false;
        return RevealActorCore(node => node.Name == actorName);
    }

    /// <summary>
    /// コンポーネント参照からのジャンプ用。コンポーネントはアクタに属するため、
    /// それを持つアクタの行へジャンプ＋ハイライトする（挙動は RevealActorByName と同じ）。
    /// slotIndex は将来コンポーネント行まで展開する拡張のために受け取るが、現状は使用しない。
    /// </summary>
    /// <param name="actorName">コンポーネントを持つアクタの名前。</param>
    /// <param name="slotIndex">コンポーネントのスロット添字（現状未使用）。</param>
    public bool RevealComponentOwner(string actorName, int slotIndex)
    {
        _ = slotIndex;   // 現状 Hierarchy はコンポーネント行を持たないため未使用
        return RevealActorByName(actorName);
    }

    /// <summary>
    /// 指定名のアクタがシーンに存在するかを返す。
    ///
    /// 参照ピッカー（ReferencePicker）が「参照先が消えていないか」を確認するための問い合わせ。
    /// ツリーの表示状態（仮想化・展開）に依存しないよう、ノードモデル側を走査する。
    /// </summary>
    /// <param name="actorName">対象アクタの名前（完全一致・大文字小文字は区別する）。</param>
    public bool ActorExistsByName(string actorName)
    {
        if (string.IsNullOrEmpty(actorName)) return false;
        foreach (var node in GetAllNodes(_roots))
            if (node.Name == actorName) return true;
        return false;
    }

    /// <summary>
    /// 指定名のアクタの DFS ID を返す（見つからなければ null）。
    ///
    /// ScriptEvent の結線先候補（そのアクタが持つスクリプト型の一覧）を引くには
    /// GET_ACTOR_COMPONENTS が要り、その宛先は DFS ID である。
    /// 一方 ScriptEvent の保存値はアクタ名なので、名前 → DFS ID の変換窓口が要る。
    /// <see cref="ActorExistsByName"/> と同じくノードモデル側を走査するため、
    /// ツリーの表示状態（仮想化・展開）に依存しない。
    /// 同名アクタが複数ある場合は DFS 順で最初に見つかったものを返す。
    /// </summary>
    /// <param name="actorName">対象アクタの名前（完全一致・大文字小文字は区別する）。</param>
    public int? ActorDfsIdByName(string actorName)
    {
        if (string.IsNullOrEmpty(actorName)) return null;
        foreach (var node in GetAllNodes(_roots))
            if (node.Name == actorName) return node.Id;
        return null;
    }

    // ── 実装 ──────────────────────────────────────────────────────

    /// <summary>
    /// 条件に一致するアクタノードを探し、祖先を展開して可視域へスクロールし、一時ハイライトする。
    /// </summary>
    private bool RevealActorCore(Func<ActorNode, bool> predicate)
    {
        var path = new List<TreeViewItem>();
        if (!TryFindActorItem(ActorTree.Items, predicate, path)) return false;
        if (path.Count == 0) return false;

        // 祖先をすべて展開する（最後の要素＝対象本体は展開しない。子を開く必要はない）
        for (int i = 0; i < path.Count - 1; i++)
            path[i].IsExpanded = true;

        var target = path[^1];

        // 展開直後はコンテナのレイアウトが未確定なので、スクロールはレイアウト確定後に行う。
        // さらに ActorTree は仮想化が有効（VirtualizingPanel.IsVirtualizing="True"）で、
        // 画面外の行はテンプレートが未適用＝RowHighlight がまだ存在しない。
        // そのため「スクロール → レイアウト確定 → もう 1 ターン待ってから着色」の 2 段構えにする。
        Dispatcher.BeginInvoke(() =>
        {
            target.BringIntoView();
            target.UpdateLayout();
            Dispatcher.BeginInvoke(() => ApplyTemporaryHighlight(target), DispatcherPriority.Loaded);
        }, DispatcherPriority.Loaded);

        return true;
    }

    /// <summary>
    /// 条件に一致する TreeViewItem を深さ優先で探す。
    /// 見つかった場合、ルートから対象までの経路を <paramref name="path"/> に積んで true を返す。
    /// </summary>
    private static bool TryFindActorItem(
        ItemCollection items, Func<ActorNode, bool> predicate, List<TreeViewItem> path)
    {
        foreach (var obj in items)
        {
            if (obj is not TreeViewItem item) continue;

            path.Add(item);
            if (item.Tag is ActorNode node && predicate(node)) return true;
            if (TryFindActorItem(item.Items, predicate, path)) return true;
            path.RemoveAt(path.Count - 1);   // この枝にはいなかったので戻す
        }
        return false;
    }

    /// <summary>
    /// 対象行のハイライト用 Border を一定時間だけ黄色く塗る。
    /// 直前のハイライトが残っていれば先に解除する（連続ジャンプで塗り残しが出ないように）。
    /// </summary>
    private void ApplyTemporaryHighlight(TreeViewItem item)
    {
        ClearTemporaryHighlight();

        // テンプレート内の RowHighlight を探す（未生成なら行全幅の RowBorder にフォールバック）
        var border = FindVisualChild<Border>(item, RevealHighlightBorderName)
                  ?? FindVisualChild<Border>(item, "RowBorder");
        if (border == null) return;

        // ローカル値として塗る。選択/ホバーのトリガーより優先されるため、
        // 解除時は ClearValue で必ずローカル値を捨ててトリガーへ制御を戻す。
        border.Background      = RevealHighlightBrush;
        _revealHighlightBorder = border;

        _revealHighlightTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(RevealHighlightDurationMs),
        };
        _revealHighlightTimer.Tick += (_, _) => ClearTemporaryHighlight();
        _revealHighlightTimer.Start();
    }

    /// <summary>一時ハイライトを解除し、選択/ホバーのトリガーへ表示制御を戻す。</summary>
    private void ClearTemporaryHighlight()
    {
        _revealHighlightTimer?.Stop();
        _revealHighlightTimer = null;

        if (_revealHighlightBorder == null) return;
        _revealHighlightBorder.ClearValue(Border.BackgroundProperty);
        _revealHighlightBorder = null;
    }
}
