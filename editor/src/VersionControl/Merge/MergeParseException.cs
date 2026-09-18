// ============================================================
//  MergeParseException.cs — 印つきテキストを解析できなかったことを表す例外
//
//  【なぜ専用の例外なのか】
//  「印が閉じていない」「印が入れ子」は、こちらで辻褄を合わせてはいけない
//  種類の異常。黙って直すと、利用者が意図していない中身をファイルへ
//  書き戻すことになる。呼び出し側が確実に気づけるよう、専用の型で投げる。
//
//  【文言をここに持つ理由】
//  行番号を含む具体的な理由（「12 行目の印が閉じていません」）は、
//  そのまま画面へ出して利用者が原因を探せる情報になる。組み立てを
//  1 か所にまとめ、呼び出し側で文言を作らせない。
//
//  【依存】
//  WPF にも LoreVcs にも依存しない（単体テストへそのままリンクできる）。
// ============================================================

using System;

namespace SEEDEditor.VersionControl.Merge;

/// <summary>
/// 印つきテキストの解析に失敗したことを表す例外。
/// </summary>
public sealed class MergeParseException : Exception
{
    /// <summary>理由を指定して生成する。</summary>
    /// <param name="message">利用者向けの理由（行番号を含む）。</param>
    public MergeParseException(string message) : base(message) { }

    /// <summary>印が閉じていないときの理由を作る。</summary>
    /// <param name="startLineNumber">ブロックが始まった行番号（1 始まり）。</param>
    public static string Unterminated(int startLineNumber)
        => string.Format(
            VersionControlMessages.MERGE_PARSE_UNTERMINATED_FORMAT, startLineNumber);

    /// <summary>ブロックの中に別の印が現れたときの理由を作る。</summary>
    /// <param name="lineNumber">見つかった行番号（1 始まり）。</param>
    /// <param name="blockStartLine">ブロックが始まった行番号（1 始まり）。</param>
    /// <param name="lineText">その行の中身。</param>
    public static string NestedMarker(int lineNumber, int blockStartLine, string lineText)
        => string.Format(
            VersionControlMessages.MERGE_PARSE_NESTED_FORMAT,
            lineNumber, blockStartLine, lineText);

    /// <summary>ブロックの外に印が転がっていたときの理由を作る。</summary>
    /// <param name="lineNumber">見つかった行番号（1 始まり）。</param>
    /// <param name="lineText">その行の中身。</param>
    public static string OrphanMarker(int lineNumber, string lineText)
        => string.Format(
            VersionControlMessages.MERGE_PARSE_ORPHAN_FORMAT, lineNumber, lineText);
}
