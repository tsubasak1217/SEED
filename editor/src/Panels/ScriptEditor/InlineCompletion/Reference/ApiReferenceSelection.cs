// ============================================================
//  ApiReferenceSelection.cs — 選んだ節（切れ端）と、プロンプトへ注入する本文
//
//  【本文の並べ方】
//  選んだ切れ端を文書の順（見出しの番号順）に並べる。同じ節の隣り合う切れ端は続けて並べ、
//  後ろの見出し行・フェンスの開き/表の頭と前のフェンスの閉じを省く（全部選ぶと元の節と同じ本文）。
//  そのため本文の文字数は「選んだ切れ端の単独の文字数の和」以下で、予算を超えない。
//
//  【WPF 非依存】editor/tests/InlineCompletionTests がこのフォルダを丸ごとリンクする。
// ============================================================

using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace SEEDEditor.Panels.ScriptEditor.InlineCompletion.Reference;

/// <summary>
/// 選んだ切れ端と注入する本文（作った後は変わらない）。
/// </summary>
public sealed class ApiReferenceSelection
{
    /// <summary>ログの節の名前の区切り。</summary>
    public const string LabelJoiner = " ";

    /// <summary>空の選択（リファレンスが無い・予算 0）。</summary>
    public static readonly ApiReferenceSelection Empty = new(
        new List<ApiReferencePart>(), new HashSet<int>(), budgetChars: 0, alwaysIncludedOverflow: false);

    /// <summary>選んだ切れ端（文書の順）。</summary>
    public IReadOnlyList<ApiReferencePart> Parts { get; }

    /// <summary>選んだ切れ端のうち「常に入れる節」として入れたものの通し番号。</summary>
    public IReadOnlySet<int> AlwaysIncludedIndices { get; }

    /// <summary>注入する本文（選んだ切れ端を文書の順に並べたもの）。</summary>
    public string Text { get; }

    /// <summary>本文の文字数。</summary>
    public int Length => Text.Length;

    /// <summary>この選択の予算（本文はこれを超えない）。</summary>
    public int BudgetChars { get; }

    /// <summary>常に入れる節の一部が予算に入りきらなかったか（予算が小さすぎる合図）。</summary>
    public bool AlwaysIncludedOverflow { get; }

    /// <summary>ログ用の節の名前（同じ節の続いた切れ端はまとめる。"§7.18[1-2/6]"・全部なら "§7.18"）。</summary>
    public IReadOnlyList<string> Labels { get; }

    /// <summary>選択を組み立てる（<see cref="ApiReferenceSelector"/> から）。</summary>
    /// <param name="partsInOrder">選んだ切れ端（文書の順）。</param>
    /// <param name="alwaysIncludedIndices">常に入れる節として入れた切れ端の通し番号。</param>
    /// <param name="budgetChars">予算。</param>
    /// <param name="alwaysIncludedOverflow">常に入れる節が入りきらなかったか。</param>
    public ApiReferenceSelection(
        IReadOnlyList<ApiReferencePart> partsInOrder, IReadOnlySet<int> alwaysIncludedIndices,
        int budgetChars, bool alwaysIncludedOverflow)
    {
        Parts = partsInOrder;
        AlwaysIncludedIndices = alwaysIncludedIndices;
        BudgetChars = budgetChars;
        AlwaysIncludedOverflow = alwaysIncludedOverflow;
        Text = Render(partsInOrder);
        Labels = MakeLabels(partsInOrder);
    }

    /// <summary>この切れ端を選んだか。</summary>
    /// <param name="part">切れ端。</param>
    /// <returns>選んでいれば true。</returns>
    public bool Contains(ApiReferencePart part) => Parts.Any(p => p.Index == part.Index);

    /// <summary>ログ用に節の名前を 1 行にする（"§0 §1 §2 §7.18[1/6]"）。</summary>
    /// <returns>節の名前の並び。</returns>
    public string DescribeLabels() => string.Join(LabelJoiner, Labels);

    /// <summary>
    /// 切れ端を文書の順に並べて本文にする。同じ節の隣り合う切れ端は、後ろの見出し・前置きと
    /// 前の後置きを省いて続ける。
    /// </summary>
    /// <param name="partsInOrder">文書の順の切れ端。</param>
    /// <returns>本文。</returns>
    public static string Render(IReadOnlyList<ApiReferencePart> partsInOrder)
    {
        var sb = new StringBuilder();
        ApiReferencePart? previous = null;
        string pendingClose = string.Empty;
        foreach (var part in partsInOrder)
        {
            if (previous is not null && previous.IsFollowedBy(part))
            {
                // 同じ節の続き: 前の後置き（閉じのフェンス）と自分の見出し・前置きを省く
                sb.Append(part.Body);
            }
            else
            {
                sb.Append(pendingClose);
                sb.Append(ApiReferencePart.HeadingPrefix(part.HeadingLine)).Append(part.Open).Append(part.Body);
            }
            pendingClose = part.Close;
            previous = part;
        }
        sb.Append(pendingClose);
        return sb.ToString();
    }

    /// <summary>
    /// ログ用の名前を作る。同じ節の続いた切れ端は 1 つにまとめ、節の全部なら節の名前だけにする。
    /// </summary>
    private static List<string> MakeLabels(IReadOnlyList<ApiReferencePart> partsInOrder)
    {
        var labels = new List<string>();
        int i = 0;
        while (i < partsInOrder.Count)
        {
            // 同じ節で番号が続くところまでを 1 つの並びにする
            int j = i;
            while (j + 1 < partsInOrder.Count && partsInOrder[j].IsFollowedBy(partsInOrder[j + 1])) j++;

            var first = partsInOrder[i];
            var last = partsInOrder[j];
            if (first.PartCount == 1 || (first.PartNumber == 1 && last.PartNumber == last.PartCount))
                labels.Add(first.SectionLabel);
            else if (i == j)
                labels.Add(first.Label);
            else
                labels.Add($"{first.SectionLabel}[{first.PartNumber}-{last.PartNumber}/{first.PartCount}]");
            i = j + 1;
        }
        return labels;
    }
}
