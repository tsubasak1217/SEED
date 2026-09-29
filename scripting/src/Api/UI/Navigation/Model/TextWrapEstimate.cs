using System;
using System.Collections.Generic;
using System.Text;

namespace SEED.UI;

// ============================================================
//  TextWrapEstimate.cs — エンジンの自動折り返しを C# で真似る（W2 の手直し P2-1。純粋な計算）
//
//  Text.Measure（W2-6c）までの見積もり（DialogLayout）が「本文が何行になるか」を知るための計算。規則はエンジンの
//  runtime/src/engine/core/font/text_wrap.rs の冒頭の確定仕様と同じ（1 行ずつ対応させて書いた。直すときは両方を見る）:
//    0. 改行の表記をそろえる（\r\n と単独の \r は \n）。\n で段落に分け、段落ごとに折る（空の段落も 1 行）
//    1. 段落をクラスタに分ける
//       - 続く ASCII の英数字（語の中の ' と - を含む）= 1 クラスタ（語）
//       - 空白（半角スペース・タブ）は直前のクラスタに付く（行末に来たら幅に数えない＝ぶら下げる）。段落の頭の空白だけは空白のクラスタ
//       - それ以外（日本語など）は 1 文字 1 クラスタ（文字はエンジンの char と同じ Unicode の符号位置。サロゲートの組は 1 文字）
//    2. 貪欲に詰める。収まりは「行の途中までの幅（末尾の空白を含む）＋ 次のクラスタの末尾の空白を除いた幅 ≤ 枠の幅」
//       - 行頭のクラスタが 1 つで枠を超え、2 文字以上なら 1 文字ずつに分けてから詰め直す（強制分割）
//       - 1 行には必ず 1 クラスタ以上を載せる（1 文字でも入らない枠でも前へ進む）
//    3. 禁則: 次の行の頭に行頭禁則の文字（KinsokuHead）が来るなら前の行へぶら下げる（最大 MaxHangingChars 文字）。
//       行末が行末禁則の文字（KinsokuTail）なら次の行へ追い出す（行が空になる・最後の行なら追い出さない）
//  送り幅は呼び手が渡す（DialogLayout は組み込みの書体の表 BuiltInFontAdvance）。幅はすべて「送り幅（em）× 文字の大きさ」を
//  float で足す（エンジンも f32 で同じ順に足す）。収まりの判定だけ slack（呼び手が決める小さな余裕）を枠から引く:
//  浮動小数の丸めの差で「エンジンは収まるのに見積もりは入らない」側へ倒す（行が足りなくなるより 1 行余る方がよい）。
// ============================================================

/// <summary>エンジンの自動折り返し（text_wrap.rs）を真似た行の分け方。</summary>
internal static class TextWrapEstimate
{
    /// <summary>行頭禁則の文字（text_wrap.rs の KINSOKU_HEAD と同じ）。次の行の頭に来るなら前の行へぶら下げる。</summary>
    public const string KinsokuHead = "、。，．・？！」』）】〉》”’ーぁぃぅぇぉっゃゅょゎヵヶ々〜:;";

    /// <summary>行末禁則の文字（text_wrap.rs の KINSOKU_TAIL と同じ）。行末に来るなら次の行へ追い出す。</summary>
    public const string KinsokuTail = "「『（【〈《“‘";

    /// <summary>行頭禁則のぶら下げで 1 行の末尾へはみ出させる最大の文字数（text_wrap.rs の MAX_HANGING_CHARS）。</summary>
    public const int MaxHangingChars = 2;

    /// <summary>語の中の記号（続く ASCII の英数字と 1 つの語にする。text_wrap.rs の WORD_INNER_SYMBOLS）。</summary>
    private const string WordInnerSymbols = "'-";

    /// <summary>改行。</summary>
    private const char NewLine = '\n';

    /// <summary>復帰（\r\n・単独の \r は改行へそろえる）。</summary>
    private const char CarriageReturn = '\r';

    /// <summary>1 文字のクラスタでないことの印（Single の値）。</summary>
    private const int NoSingle = -1;

    /// <summary>折り返しの単位（これ以上は分けない塊。text_wrap.rs の Cluster）。</summary>
    private struct Cluster
    {
        /// <summary>最初の文字の番号（段落の文字の並びでの位置）。</summary>
        public int Start;
        /// <summary>末尾の空白を含む終わり（この番号の手前まで）。</summary>
        public int End;
        /// <summary>末尾の空白を除いた終わり（行末に来たときの範囲）。</summary>
        public int EndTrimmed;
        /// <summary>末尾の空白を含む幅（行の途中に来たとき）。</summary>
        public float WidthFull;
        /// <summary>末尾の空白を除いた幅（行末に来たとき＝収まりの判定）。</summary>
        public float WidthTrimmed;
        /// <summary>1 文字だけのクラスタならその文字（禁則の判定）。それ以外は NoSingle。</summary>
        public int Single;
        /// <summary>末尾の空白を除いた文字の数（強制分割できるか）。</summary>
        public int CoreCount;
    }

    /// <summary>
    /// 行の数（空文字は 0。描かれないため）。
    /// </summary>
    /// <param name="text">文字列。</param>
    /// <param name="fontSize">文字の大きさ（0 以下なら折り返さない）。</param>
    /// <param name="maxWidth">枠の幅（0 以下・有限でないなら折り返さない＝改行だけで分ける）。</param>
    /// <param name="slack">収まりの判定で枠から引く余裕（0 以上）。</param>
    /// <param name="advanceEm">1 文字（符号位置）の送り幅（em）。</param>
    public static int CountLines(string text, float fontSize, float maxWidth, float slack, Func<int, float> advanceEm)
        => string.IsNullOrEmpty(text) ? 0 : Wrap(text, fontSize, maxWidth, slack, advanceEm, null);

    /// <summary>
    /// 行の文字列（末尾の空白を除く。空文字は空の一覧）。検算・診断用。
    /// </summary>
    /// <param name="text">文字列。</param>
    /// <param name="fontSize">文字の大きさ。</param>
    /// <param name="maxWidth">枠の幅。</param>
    /// <param name="slack">収まりの判定で枠から引く余裕。</param>
    /// <param name="advanceEm">1 文字の送り幅（em）。</param>
    public static IReadOnlyList<string> Lines(string text, float fontSize, float maxWidth, float slack, Func<int, float> advanceEm)
    {
        var lines = new List<string>();
        if (!string.IsNullOrEmpty(text)) Wrap(text, fontSize, maxWidth, slack, advanceEm, lines);
        return lines;
    }

    /// <summary>
    /// 折り返さないときの最も広い行の幅（段落ごとの送り幅の和の最大。行末の空白も数える＝エンジンの折り返さない経路と同じ）。
    /// </summary>
    /// <param name="text">文字列。</param>
    /// <param name="fontSize">文字の大きさ。</param>
    /// <param name="advanceEm">1 文字の送り幅（em）。</param>
    public static float WidestParagraph(string text, float fontSize, Func<int, float> advanceEm)
    {
        if (string.IsNullOrEmpty(text) || !(fontSize > 0f)) return 0f;
        float widest = 0f;
        foreach (string para in Paragraphs(text))
        {
            float width = 0f;
            foreach (var rune in para.EnumerateRunes()) width += advanceEm(rune.Value) * fontSize;
            widest = Math.Max(widest, width);
        }
        return widest;
    }

    /// <summary>折り返しの本体（行の数を返す。lines があれば行の文字列も積む）。</summary>
    private static int Wrap(string text, float fontSize, float maxWidth, float slack, Func<int, float> advanceEm, List<string>? lines)
    {
        // 折り返すか（エンジン: max_width <= 0 か font_size <= 0 なら改行だけで分ける。有限でない枠も折り返さないとみなす）
        bool wraps = fontSize > 0f && maxWidth > 0f && float.IsFinite(maxWidth);
        float limit = maxWidth - Math.Max(0f, slack);
        int count = 0;
        foreach (string para in Paragraphs(text))
        {
            if (!wraps)
            {
                count++;
                lines?.Add(para);
                continue;
            }
            count += WrapParagraph(para, fontSize, limit, advanceEm, lines);
        }
        return count;
    }

    /// <summary>改行の表記をそろえて段落に分ける（\r\n と単独の \r は \n。text_wrap.rs の normalize_newlines と split('\n')）。</summary>
    private static string[] Paragraphs(string text)
    {
        string normalized = text.IndexOf(CarriageReturn) < 0
            ? text
            : text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace(CarriageReturn, NewLine);
        return normalized.Split(NewLine);
    }

    /// <summary>1 段落を折る（行の数を返す）。</summary>
    private static int WrapParagraph(string para, float fontSize, float limit, Func<int, float> advanceEm, List<string>? lines)
    {
        // 文字（符号位置）と、文字列の中の位置（行の文字列を切り出す）
        var codePoints = new List<int>();
        var offsets = new List<int>();
        int offset = 0;
        foreach (var rune in para.EnumerateRunes())
        {
            codePoints.Add(rune.Value);
            offsets.Add(offset);
            offset += rune.Utf16SequenceLength;
        }
        offsets.Add(para.Length);

        var clusters = SplitClusters(codePoints, fontSize, advanceEm);
        if (clusters.Count == 0)
        {
            // 空の段落は幅 0 の 1 行（行の数を保つ）
            lines?.Add(string.Empty);
            return 1;
        }

        int count = 0;
        int s = 0;
        while (s < clusters.Count)
        {
            // ── 1. 貪欲に詰める ──
            int e = s;
            float widthFull = 0f;
            while (e < clusters.Count)
            {
                bool fits = widthFull + clusters[e].WidthTrimmed <= limit;
                if (!fits && e > s) break;
                if (!fits && e == s && clusters[e].CoreCount > 1)
                {
                    // 行頭のクラスタが 1 つで入りきらないときだけ 1 文字ずつに分けて、分けた先頭で判定し直す
                    var pieces = Explode(clusters[e], codePoints, fontSize, advanceEm);
                    clusters.RemoveAt(e);
                    clusters.InsertRange(e, pieces);
                    continue;
                }
                widthFull += clusters[e].WidthFull;
                e++;
            }
            // 最低 1 クラスタは必ず載せる
            if (e == s) e = s + 1;

            // ── 2. 行頭禁則（次の行の頭の禁則の文字を前の行へぶら下げる）──
            int hung = 0;
            while (e < clusters.Count && hung < MaxHangingChars && IsIn(KinsokuHead, clusters[e].Single))
            {
                e++;
                hung++;
            }

            // ── 3. 行末禁則（行末の開き括弧を次の行へ。行が空になる・最後の行なら諦める）──
            if (e < clusters.Count && e - 1 > s && IsIn(KinsokuTail, clusters[e - 1].Single)) e--;

            // ── 4. 行を確定（範囲は末尾の空白を除く）──
            if (lines is not null)
            {
                int from = offsets[clusters[s].Start];
                int to = offsets[clusters[e - 1].EndTrimmed];
                lines.Add(para.Substring(from, to - from));
            }
            count++;
            s = e;
        }
        return count;
    }

    /// <summary>段落をクラスタに分ける（text_wrap.rs の split_clusters）。</summary>
    private static List<Cluster> SplitClusters(List<int> codePoints, float fontSize, Func<int, float> advanceEm)
    {
        var clusters = new List<Cluster>();
        int i = 0;
        while (i < codePoints.Count)
        {
            int c = codePoints[i];
            if (IsWrapSpace(c))
            {
                // 空白: 直前のクラスタへ付ける（無ければ空白だけのクラスタ）。末尾の空白は行の途中に来たときだけ幅に数える
                int j = i;
                float w = 0f;
                while (j < codePoints.Count && IsWrapSpace(codePoints[j]))
                {
                    w += advanceEm(codePoints[j]) * fontSize;
                    j++;
                }
                if (clusters.Count > 0)
                {
                    int lastIndex = clusters.Count - 1;
                    var last = clusters[lastIndex];
                    last.End = j;
                    last.WidthFull += w;
                    clusters[lastIndex] = last;
                }
                else
                {
                    clusters.Add(new Cluster
                    {
                        Start = i, End = j, EndTrimmed = j, WidthFull = w, WidthTrimmed = w, Single = NoSingle, CoreCount = j - i,
                    });
                }
                i = j;
                continue;
            }

            // 語（続く ASCII の英数字）か、それ以外の 1 文字
            int k = i + 1;
            if (IsWordChar(c))
            {
                while (k < codePoints.Count && IsWordChar(codePoints[k])) k++;
            }
            float width = 0f;
            for (int m = i; m < k; m++) width += advanceEm(codePoints[m]) * fontSize;
            clusters.Add(new Cluster
            {
                Start = i, End = k, EndTrimmed = k, WidthFull = width, WidthTrimmed = width,
                Single = k == i + 1 ? c : NoSingle, CoreCount = k - i,
            });
            i = k;
        }
        return clusters;
    }

    /// <summary>クラスタを 1 文字ずつのクラスタへ分ける（強制分割。末尾の空白は最後の文字へ引き継ぐ。text_wrap.rs の explode_cluster）。</summary>
    private static List<Cluster> Explode(Cluster cluster, List<int> codePoints, float fontSize, Func<int, float> advanceEm)
    {
        var pieces = new List<Cluster>(cluster.CoreCount);
        for (int m = cluster.Start; m < cluster.EndTrimmed; m++)
        {
            float w = advanceEm(codePoints[m]) * fontSize;
            pieces.Add(new Cluster
            {
                Start = m, End = m + 1, EndTrimmed = m + 1, WidthFull = w, WidthTrimmed = w, Single = codePoints[m], CoreCount = 1,
            });
        }
        if (pieces.Count > 0)
        {
            int lastIndex = pieces.Count - 1;
            var last = pieces[lastIndex];
            last.End = cluster.End;
            last.WidthFull += cluster.WidthFull - cluster.WidthTrimmed;
            pieces[lastIndex] = last;
        }
        return pieces;
    }

    /// <summary>折り返しの上の空白か（行末でぶら下げる文字。半角スペースとタブ）。</summary>
    private static bool IsWrapSpace(int c) => c == ' ' || c == '\t';

    /// <summary>語を作る文字か（ASCII の英数字と語の中の記号。text_wrap.rs の is_word_char）。</summary>
    private static bool IsWordChar(int c)
        => (c >= '0' && c <= '9') || (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z')
           || (c <= char.MaxValue && WordInnerSymbols.IndexOf((char)c) >= 0);

    /// <summary>1 文字のクラスタの文字が表にあるか（BMP の文字だけを比べる。表はすべて BMP）。</summary>
    private static bool IsIn(string table, int single)
        => single != NoSingle && single <= char.MaxValue && table.IndexOf((char)single) >= 0;
}
