using System.Windows.Controls;

namespace SEEDEditor.Panels;

/// <summary>
/// ヒエラルキーの閲覧専用表示（<see cref="HierarchyPanel"/> の部分クラス。docs/android.md §20.17）。
///
/// <para>
/// 端末の一時停止の写しをシーンパネルに出している間は、ツリーを見て選べる（選んだアクターはインスペクタに出る・
/// 開閉できる）が、変えられないようにする: 上の追加ボタンの列を無効にし、右クリックのメニュー・名前の変更（F2・再クリック）・
/// ドラッグでの並べ替えと親子の付け替え・ドロップを受け付けない（それぞれの入口が <see cref="IsReadOnlyView"/> を見る）。
/// </para>
///
/// <para>
/// 判断は WPF 非依存の EditorReadOnlyPolicy（MainWindow が当てる）。ランタイム側でも写しの表示中は編集の命令を捨てる。
/// </para>
/// </summary>
public partial class HierarchyPanel
{
    /// <summary>閲覧専用か（写しの表示中）。</summary>
    public bool IsReadOnlyView { get; private set; }

    /// <summary>
    /// 閲覧専用を切り替える（同じ状態なら何もしない）。
    /// </summary>
    /// <param name="denialReason">編集できない理由（追加ボタンの列のツールチップに出す）。null なら編集できる状態へ戻す。</param>
    public void SetReadOnly(string? denialReason)
    {
        var readOnly = denialReason is not null;
        if (IsReadOnlyView == readOnly) return;
        IsReadOnlyView = readOnly;
        ActorToolbar.IsEnabled = !readOnly;
        ActorToolbar.ToolTip   = denialReason;
        ToolTipService.SetShowOnDisabled(ActorToolbar, readOnly);
        // ドロップの受け付け（プロジェクトパネルからのアクターの追加・ツリー内の並べ替え）も止める
        ActorTree.AllowDrop = !readOnly;
        if (readOnly) ActorTree.ContextMenu = null;
    }
}
