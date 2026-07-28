using System;
using System.Collections.Generic;

namespace SEEDEditor.Panels.ScriptEditor;

/// <summary>WGSL の意味解析ベース着色の分類種別。</summary>
public enum WgslSemanticKind
{
    /// <summary>関数内で宣言されたローカル変数（let / var / const）。</summary>
    Local,
    /// <summary>関数の仮引数。</summary>
    Parameter,
}

/// <summary>着色対象 1 件（文書オフセット・長さ・分類）。</summary>
/// <param name="Offset">文書先頭からのオフセット。</param>
/// <param name="Length">識別子の文字数。</param>
/// <param name="Kind">分類種別。</param>
public readonly record struct WgslSemanticSpan(int Offset, int Length, WgslSemanticKind Kind);

/// <summary>
/// WGSL 文書から「ローカル変数」と「仮引数」の出現位置を抽出する軽量セマンティック解析器。
///
/// 【なぜ必要か】
///   Wgsl.xshd（字句ハイライト）は正規表現ベースなので、識別子が変数なのか引数なのか
///   関数名なのかを区別できない。C# 側は Roslyn の分類結果を使って着色しているが、
///   WGSL にはその基盤が無いため、ここで最小限の構文追跡を行って同等の体験を用意する。
///
/// 【アルゴリズム】
///   文書を 1 パスで走査し、
///     1. コメント（行コメント・入れ子ブロックコメント）を読み飛ばしつつ、
///     2. 識別子トークンを位置付きで収集し、
///     3. 同時に「宣言」を検出して名前集合（ローカル／引数）を作る。
///   最後にもう 1 パスで、収集済みトークンのうち名前集合に含まれるものへ分類を割り当てる。
///   ＝ 宣言箇所・使用箇所の両方が同じ色で塗られる。
///
/// 【意図的な割り切り（限界）】
///   - <b>スコープを持たない</b>。名前は文書全体で 1 つの集合として扱うため、
///     ある関数のローカル変数と同名の識別子は、別の関数の中でも同じ色になる。
///     シェーディングアセットの規模（数百行・関数数本）では実害が小さいと判断した。
///   - <b>関数外（モジュールスコープ）の const / var は対象外</b>。
///     それらは契約定数（SHADING_*）と同じ「グローバル定義」であり、
///     xshd 側の分類に委ねるほうが一貫するため、波括弧の深さ 0 での宣言は拾わない。
///   - <b>struct のフィールド宣言は拾わない</b>。ただし、同名のローカル変数が
///     別の場所で宣言されていれば、フィールド宣言側も同じ色に塗られてしまう。
///   - キーワード・組み込み関数・契約シンボルは <see cref="WgslCompletion.ReservedWords"/>
///     と <see cref="AddressSpaceWords"/> で除外し、xshd の色を必ず優先する。
///   - <c>a.b</c> のメンバアクセス右辺と <c>@builtin</c> の属性名は着色対象外にする
///     （構造体フィールドと同名のローカル変数がある場合の誤着色を減らすため）。
/// </summary>
public static class WgslSemanticAnalyzer
{
    /// <summary>
    /// アドレス空間・アクセスモードの指定子。
    ///
    /// <c>var&lt;private&gt; x</c> のように宣言キーワードと変数名の間に現れるため、
    /// 「宣言直後の識別子＝変数名」という単純規則のままだと誤って変数名として拾ってしまう。
    /// また、これらは Wgsl.xshd で型キーワード色が付いているので着色対象からも外す。
    /// （Wgsl.xshd の TypeKeyword セクションと対で維持すること）
    /// </summary>
    private static readonly HashSet<string> AddressSpaceWords = new(StringComparer.Ordinal)
    {
        "function", "private", "workgroup", "uniform", "storage",
        "read", "write", "read_write",
    };

    /// <summary>直前に読んだ宣言キーワードの種類（宣言名の待ち受け状態）。</summary>
    private enum Pending
    {
        /// <summary>待ち受けなし。</summary>
        None,
        /// <summary>let / var / const の直後（次の識別子が変数名）。</summary>
        Variable,
        /// <summary>fn の直後（次の識別子が関数名）。</summary>
        Function,
    }

    /// <summary>収集した識別子トークン 1 件。</summary>
    /// <param name="Offset">文書オフセット。</param>
    /// <param name="Length">文字数。</param>
    /// <param name="Name">識別子名。</param>
    /// <param name="Colorable">着色候補にしてよいか（メンバアクセス右辺・属性名は false）。</param>
    private readonly record struct IdentToken(int Offset, int Length, string Name, bool Colorable);

    /// <summary>
    /// 文書テキストを解析し、ローカル変数・引数の着色範囲を返す。
    /// 該当が無ければ空リストを返す（例外は投げない）。
    /// </summary>
    /// <param name="text">WGSL のソース全文。</param>
    public static IReadOnlyList<WgslSemanticSpan> Analyze(string text)
    {
        if (string.IsNullOrEmpty(text)) return Array.Empty<WgslSemanticSpan>();

        var tokens     = new List<IdentToken>();
        var locals     = new HashSet<string>(StringComparer.Ordinal);
        var parameters = new HashSet<string>(StringComparer.Ordinal);

        // ── 走査状態 ────────────────────────────────────────────
        int  braceDepth    = 0;      // '{' の深さ。0 = モジュールスコープ
        int  parenDepth    = 0;      // '(' の深さ。仮引数リストの判定に使う
        bool inFnSignature = false;  // fn の仮引数リストの中か
        bool expectFnParen = false;  // fn 名を読み終え、'(' を待っている
        var  pending       = Pending.None;
        char prevChar      = '\0';   // 直前の非空白文字（メンバアクセス '.' / 属性 '@' の判定用）
        // 直前に読んだトークンが識別子のまま（間に空白しか無い）か。
        // 「識別子 + ':'」＝仮引数宣言、を判定するために使う。
        bool identIsLast    = false;
        int  lastIdentIndex = -1;

        int n = text.Length;
        for (int i = 0; i < n; )
        {
            char c = text[i];

            // ── コメントを読み飛ばす ──────────────────────────
            // 行コメント: 行末まで。
            if (c == '/' && i + 1 < n && text[i + 1] == '/')
            {
                while (i < n && text[i] != '\n') i++;
                continue;
            }
            // ブロックコメント: WGSL は入れ子を許すため深さを数える。
            if (c == '/' && i + 1 < n && text[i + 1] == '*')
            {
                int depth = 1;
                i += 2;
                while (i < n && depth > 0)
                {
                    if (i + 1 < n && text[i] == '/' && text[i + 1] == '*') { depth++; i += 2; continue; }
                    if (i + 1 < n && text[i] == '*' && text[i + 1] == '/') { depth--; i += 2; continue; }
                    i++;
                }
                continue;
            }

            // ── 空白は状態を変えずに読み飛ばす ────────────────
            if (char.IsWhiteSpace(c)) { i++; continue; }

            // ── 識別子 ────────────────────────────────────────
            if (IsIdentStart(c))
            {
                int start = i;
                i++;
                while (i < n && IsIdentPart(text[i])) i++;
                string name = text.Substring(start, i - start);

                // メンバアクセスの右辺（a.b の b）・属性名（@builtin）は着色対象外。
                bool colorable = prevChar != '.' && prevChar != '@';

                if (colorable)
                {
                    switch (pending)
                    {
                        case Pending.Variable:
                            // var<private> x のようにアドレス空間指定子を挟む場合は読み飛ばして待ち続ける
                            if (!AddressSpaceWords.Contains(name))
                            {
                                // 関数の中で宣言されたものだけをローカル変数として扱う
                                if (braceDepth > 0) locals.Add(name);
                                pending = Pending.None;
                            }
                            break;

                        case Pending.Function:
                            // 関数名そのものは着色しない（xshd の関数呼び出し色に委ねる）
                            pending       = Pending.None;
                            expectFnParen = true;
                            break;

                        default:
                            if (name is "let" or "var" or "const") pending = Pending.Variable;
                            else if (name == "fn")
                            {
                                pending = Pending.Function;
                                // モジュールスコープの fn は必ず「新しい関数の始まり」。
                                // 編集途中で括弧が閉じていない前の関数（fn broken( のような状態）が
                                // あっても、ここで括弧の追跡をリセットすれば以降の関数の
                                // 引数検出が巻き添えで壊れない。
                                if (braceDepth == 0)
                                {
                                    parenDepth    = 0;
                                    inFnSignature = false;
                                }
                            }
                            break;
                    }
                }

                tokens.Add(new IdentToken(start, name.Length, name, colorable));
                lastIdentIndex = tokens.Count - 1;
                identIsLast    = true;
                prevChar       = text[i - 1];
                continue;
            }

            // ── 記号（構造の追跡）─────────────────────────────
            switch (c)
            {
                case ':':
                    // 仮引数リスト直下の「識別子 :」だけを引数宣言とみなす。
                    // 関数内の `let x: f32` は inFnSignature が false なので対象外
                    // （そちらは既に let の宣言としてローカル変数に入っている）。
                    if (inFnSignature && parenDepth == 1 && identIsLast && lastIdentIndex >= 0)
                    {
                        var t = tokens[lastIdentIndex];
                        if (t.Colorable && !AddressSpaceWords.Contains(t.Name)) parameters.Add(t.Name);
                    }
                    break;

                case '(':
                    parenDepth++;
                    if (expectFnParen) { inFnSignature = true; expectFnParen = false; }
                    break;

                case ')':
                    if (parenDepth > 0) parenDepth--;
                    if (inFnSignature && parenDepth == 0) inFnSignature = false;
                    break;

                case '{':
                    braceDepth++;
                    // 仮引数リストが閉じないまま本体に入ることは無いが、防御的に解除する
                    inFnSignature = false;
                    expectFnParen = false;
                    break;

                case '}':
                    if (braceDepth > 0) braceDepth--;
                    break;

                case ';':
                    // 宣言待ちのまま文が終わった＝構文が崩れている。状態を捨てて復帰する。
                    pending = Pending.None;
                    break;
            }

            identIsLast = false;
            prevChar    = c;
            i++;
        }

        // ── 2 パス目: 収集トークンへ分類を割り当てる ────────────
        if (locals.Count == 0 && parameters.Count == 0) return Array.Empty<WgslSemanticSpan>();

        var spans = new List<WgslSemanticSpan>();
        foreach (var t in tokens)
        {
            if (!t.Colorable) continue;
            // キーワード・型・組み込み関数・契約シンボルは xshd の色を優先する
            if (WgslCompletion.ReservedWords.Contains(t.Name)) continue;
            if (AddressSpaceWords.Contains(t.Name)) continue;

            // 引数はローカル変数より優先する（同名なら引数色）
            if (parameters.Contains(t.Name))
                spans.Add(new WgslSemanticSpan(t.Offset, t.Length, WgslSemanticKind.Parameter));
            else if (locals.Contains(t.Name))
                spans.Add(new WgslSemanticSpan(t.Offset, t.Length, WgslSemanticKind.Local));
        }
        return spans;
    }

    /// <summary>識別子の先頭に使える文字か（WGSL は ASCII 英字と '_'）。</summary>
    private static bool IsIdentStart(char c) => c == '_' || char.IsLetter(c);

    /// <summary>識別子の 2 文字目以降に使える文字か。</summary>
    private static bool IsIdentPart(char c) => c == '_' || char.IsLetterOrDigit(c);
}
