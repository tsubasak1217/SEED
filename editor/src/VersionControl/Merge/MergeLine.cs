// ============================================================
//  MergeLine.cs — 競合ファイルを「行 + その行自身の改行」で持つ器
//
//  【なぜ改行を行ごとに持つのか】
//  競合の解決は **ファイルを丸ごと書き戻す** 操作なので、触っていない部分の
//  改行コードまで変えてしまうと、バージョン管理上は「全行が変わった」ことになる。
//  ファイル全体で 1 つの改行コードを決め打ちすると、
//    ・CRLF と LF が混在したファイル（外部ツールが作った .json など）
//    ・末尾に改行が無いファイル
//  を書き戻した瞬間に差分が爆発する。そこで行ごとに「その行の終端」を持ち、
//  触らなかった行はバイト単位でそのまま戻せるようにしてある。
//
//  【依存】
//  WPF にも LoreVcs にも依存しない（単体テストへそのままリンクできる）。
// ============================================================

using System;
using System.Collections.Generic;
using System.Text;

namespace SEEDEditor.VersionControl.Merge;

/// <summary>
/// テキストファイルの 1 行（不変）。
/// </summary>
/// <param name="Text">改行を含まない行の中身。</param>
/// <param name="Ending">
/// その行の終端（<c>"\r\n"</c> / <c>"\n"</c> / <c>"\r"</c>）。
/// ファイル末尾が改行で終わっていない場合、最後の行だけ空文字になる。
/// </param>
public readonly record struct MergeLine(string Text, string Ending)
{
    /// <summary>改行で終わらない行（＝ファイル末尾）か。</summary>
    public bool IsUnterminated => Ending.Length == 0;

    /// <summary>ログ向けの 1 行表現。</summary>
    public override string ToString() => Text;
}

/// <summary>
/// テキストと <see cref="MergeLine"/> の列との相互変換。
/// </summary>
public static class MergeTextLines
{
    /// <summary>Windows の改行。</summary>
    public const string CRLF = "\r\n";

    /// <summary>Unix の改行。</summary>
    public const string LF = "\n";

    /// <summary>旧 Mac の改行（読めるようにするためだけに持つ。書き出しには選ばない）。</summary>
    public const string CR = "\r";

    /// <summary>UTF-8 の BOM を表す文字（<c>File.ReadAllText</c> が残すことがある）。</summary>
    public const char BOM_CHAR = '﻿';

    /// <summary>
    /// テキストを行へ分解する。各行は自分の改行を保持する。
    /// </summary>
    /// <param name="text">分解するテキスト（BOM は呼び出し側で取り除いておくこと）。</param>
    /// <returns>行の列（空文字なら 0 行）。</returns>
    public static IReadOnlyList<MergeLine> Split(string text)
    {
        var lines = new List<MergeLine>();
        if (string.IsNullOrEmpty(text)) return lines;

        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c != '\r' && c != '\n') continue;

            // CR の直後が LF なら CRLF として 1 つの改行にまとめる。
            var ending = c == '\r' && i + 1 < text.Length && text[i + 1] == '\n' ? CRLF
                       : c == '\r' ? CR
                       : LF;

            lines.Add(new MergeLine(text[start..i], ending));
            i    += ending.Length - 1;
            start = i + 1;
        }

        // 末尾が改行で終わっていない場合、残りが最後の 1 行になる。
        if (start < text.Length) lines.Add(new MergeLine(text[start..], string.Empty));

        return lines;
    }

    /// <summary>
    /// 行の列をテキストへ戻す。
    ///
    /// <para>
    /// 終端を持たない行（<see cref="MergeLine.IsUnterminated"/>）が途中に現れた場合は、
    /// <paramref name="defaultNewLine"/> を補う。これは「ファイル末尾だった行」が
    /// 合成の結果として途中へ来たときに、次の行と繋がってしまうのを防ぐため。
    /// </para>
    /// </summary>
    /// <param name="lines">行の列。</param>
    /// <param name="defaultNewLine">終端が無い行を途中で繋ぐときに補う改行。</param>
    /// <returns>組み立てたテキスト。</returns>
    public static string Join(IReadOnlyList<MergeLine> lines, string defaultNewLine)
    {
        var builder = new StringBuilder();
        for (var i = 0; i < lines.Count; i++)
        {
            var line   = lines[i];
            var isLast = i == lines.Count - 1;

            builder.Append(line.Text);
            if (!line.IsUnterminated) builder.Append(line.Ending);
            else if (!isLast)         builder.Append(defaultNewLine);
        }
        return builder.ToString();
    }

    /// <summary>
    /// 行の列に最も多く現れる改行を返す（1 つも無ければ環境既定）。
    /// 新しく足す行の終端を決めるのに使う。
    /// </summary>
    /// <param name="lines">行の列。</param>
    public static string DominantNewLine(IReadOnlyList<MergeLine> lines)
    {
        var crlf = 0;
        var lf   = 0;
        var cr   = 0;

        foreach (var line in lines)
        {
            switch (line.Ending)
            {
                case CRLF: crlf++; break;
                case LF:   lf++;   break;
                case CR:   cr++;   break;
            }
        }

        if (crlf == 0 && lf == 0 && cr == 0) return Environment.NewLine;
        if (crlf >= lf && crlf >= cr) return CRLF;
        return lf >= cr ? LF : CR;
    }

    /// <summary>行の中身だけを取り出す（差分計算は改行を見ないため）。</summary>
    /// <param name="lines">行の列。</param>
    public static IReadOnlyList<string> ToTexts(IReadOnlyList<MergeLine> lines)
    {
        var texts = new string[lines.Count];
        for (var i = 0; i < lines.Count; i++) texts[i] = lines[i].Text;
        return texts;
    }
}
