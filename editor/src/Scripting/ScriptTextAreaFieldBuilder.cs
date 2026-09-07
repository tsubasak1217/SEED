using System;
using System.Windows;
using System.Windows.Controls;
using static SEEDEditor.Scripting.ScriptFieldWidgets;

namespace SEEDEditor.Scripting;

/// <summary>
/// <c>[TextArea]</c> を付けた string フィールドの行（複数行テキストボックス）を組み立てる。
///
/// 【役割】
/// 1 行テキストボックスとの違いは「高さ（行数）」「改行を受け付ける」「縦スクロール」の 3 点だけで、
/// 配色・余白・フォントサイズは他のスクリプトフィールド行と共通の部品
/// （<see cref="ScriptFieldWidgets"/>）をそのまま使う。
///
/// 【確定のタイミング】
/// Enter は改行の入力に使うため、値の確定は **フォーカスが外れたとき（LostFocus）だけ**。
/// 1 打鍵ごとに IPC を撃たないという既存フィールド行の方針とも一致する。
///
/// 【改行の輸送】
/// この行は「生の改行を含む文字列」を扱う。IPC・シーン保存用のエスケープは
/// 呼び出し側（<see cref="ScriptInspectorBuilder"/> のトップレベル行の入口）が行う。
/// 構造体リストのメンバとして使われる場合は、要素が JSON オブジェクト文字列として
/// 運ばれるためエスケープは不要（JSON 文字列リテラルの規則で既に畳まれている）。
///
/// 【CRLF の正規化（本ビルダーの責務）】
/// WPF の TextBox は Enter で CRLF（\r\n）を挿入する。トップレベル経路は
/// 呼び出し側の Escape が CRLF を LF へ正規化するが、構造体リスト経路は
/// Escape を通らず JSON へそのまま乗るため、正規化しないと \r がランタイムの
/// テキストレイアウトで未定義グリフ（tofu）として描かれてしまう。
/// そこで確定（LostFocus）時にここで <c>SEED.ScriptTextArea.NormalizeNewlines</c>
/// を通し、どちらの経路でも \r が残らないようにする
/// （トップレベル経路では Escape 側の正規化と重複するが、既に \r が無いので副作用はない）。
/// </summary>
internal static class ScriptTextAreaFieldBuilder
{
    /// <summary>1 行あたりの高さを求めるための、フォントサイズに対する行送り比。</summary>
    private const double LineHeightRatio = 1.45;

    /// <summary>テキストボックスの上下パディングとボーダーぶんの追加高さ（px）。</summary>
    private const double VerticalChromeHeight = 8;

    /// <summary>
    /// 複数行テキストボックスの 1 行を組む。
    /// </summary>
    /// <param name="field">フィールド情報（ラベル・説明・行数）。</param>
    /// <param name="lines">表示行数（1 以上。解決済みの値を渡すこと）。</param>
    /// <param name="value">現在値（生の改行を含む文字列）。</param>
    /// <param name="onChange">確定時の通知（改行を LF へ正規化した文字列を渡す）。</param>
    public static UIElement Build(
        ScriptFieldInfo field, int lines, string value, Action<string> onChange)
    {
        var tb = MakeTextBox(value);

        // 複数行編集の設定。Enter は改行なので、確定は LostFocus のみで行う。
        tb.AcceptsReturn               = true;
        tb.TextWrapping                = TextWrapping.Wrap;
        tb.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        tb.VerticalContentAlignment    = VerticalAlignment.Top;
        tb.Height                      = (lines * RowFontSize * LineHeightRatio) + VerticalChromeHeight;

        // CRLF・単独 CR を LF へ正規化してから通知する（クラスコメントの
        // 「CRLF の正規化」を参照。構造体リスト経路の \r 未定義グリフ不具合の修正）。
        tb.LostFocus += (_, _) => onChange(SEED.ScriptTextArea.NormalizeNewlines(tb.Text));

        return MakeRow(field, null, tb);
    }
}
