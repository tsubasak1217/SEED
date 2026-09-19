// ============================================================
//  EditorReveal.cs — キャレットのある行を「確実に」画面へ出す
//
//  【なぜ必要か】
//  AvalonEdit の TextEditor.ScrollToLine は、内部の ScrollViewer を
//  コントロールテンプレートの適用時（＝最初のレイアウト）に掴む。
//  **一度もレイアウトされていないエディタ**に対して呼ぶと、ScrollViewer がまだ無いので
//  何も起きない（例外も出ない）。実測（2026-09-19）:
//    ・作りたてのエディタへ即 ScrollToLine(400)        → VerticalOffset = 0（動かない）
//    ・レイアウト後（Loaded 優先度へ遅延）に ScrollToLine → 正しくスクロールする
//    ・過去にレイアウト済みのエディタを付け直して即呼ぶ   → 正しくスクロールする
//  そのため「まだ開いていないファイルへ F12 で飛ぶ」「閉じたタブへ履歴で戻る」
//  「エラー一覧・デバッガから未オープンのファイルへ飛ぶ」で、
//  ファイルは開くのにキャレット行が画面の外（先頭のまま）、という症状になっていた。
//
//  【やり方】
//  ・レイアウト済みならその場でスクロールする。
//  ・未レイアウトなら Loaded を 1 回だけ待ってからスクロールする。
//  ・スクロール先は「実行する瞬間のキャレット行」を使う
//    （待っている間に別の位置へ飛び直しても、最後の位置へ正しく出る）。
//
//  【依存】
//  AvalonEdit と WPF のみ。パネルの状態は持たない。
// ============================================================

using System.Windows;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit;

namespace SEEDEditor.Panels.ScriptEditor;

/// <summary>
/// エディタのキャレット行を画面へ出すヘルパ。
/// </summary>
public static class EditorReveal
{
    /// <summary>
    /// キャレットのある行を画面へ出す（ScrollToLine は行を縦方向の中央付近へ寄せる）。
    /// まだ一度もレイアウトされていないエディタでは、最初のレイアウトが済んでから行う。
    /// </summary>
    /// <param name="editor">対象のエディタ。</param>
    public static void RevealCaretLine(TextEditor editor)
    {
        if (editor is null) return;

        // レイアウト済み（テンプレート適用済みで大きさが確定している）なら、その場で出せる。
        if (editor.IsLoaded && editor.ActualHeight > 0)
        {
            editor.ScrollToLine(editor.TextArea.Caret.Line);
            return;
        }

        // 未レイアウト。Loaded を 1 回だけ待つ。
        RoutedEventHandler? onLoaded = null;
        onLoaded = (_, _) =>
        {
            editor.Loaded -= onLoaded;
            // Loaded の時点ではレイアウトが済んでいるが、同じフレームで後続の
            // 大きさ変更が入ることがあるため、もう 1 段だけ遅らせて確実にする。
            editor.Dispatcher.BeginInvoke(
                DispatcherPriority.Loaded,
                () => editor.ScrollToLine(editor.TextArea.Caret.Line));
        };
        editor.Loaded += onLoaded;
    }
}
