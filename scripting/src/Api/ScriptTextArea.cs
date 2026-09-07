using System;
using System.Text;

namespace SEED;

/// <summary>
/// <c>[TextArea]</c> を付けた <c>string</c> フィールドの
/// 「表示行数の解決」と「改行の輸送書式（エスケープ）」をまとめた共通ヘルパー。
/// エディタ（インスペクタ）とランタイム（値注入）が共有する唯一の正典である。
///
/// 【なぜエスケープが要るのか】
/// エディタ ⇔ ランタイムの IPC は「1 行 = 1 コマンド」のテキストプロトコルであり、
/// <c>SET_SCRIPT_FIELD:{actor},{slot},{field},{value}</c> の value は行末までを値とする。
/// したがって生の改行を含む値を送ると、そこで 1 コマンドが切れて壊れる。
/// そこで <c>[TextArea]</c> の string フィールドだけは、改行・タブを
/// バックスラッシュ表記の 2 文字へ畳んで送受信し、スクリプトへ入れる直前に戻す。
///
/// 【どこに適用されるか】
/// - トップレベル（および <c>[Serializable]</c> ネストクラス）の string フィールド … 適用する。
///   シーン JSON にもエスケープ済みの文字列がそのまま保存される。
/// - <c>[Serializable]</c> 構造体リストの string メンバ … 適用しない。
///   要素は JSON オブジェクト文字列として送られ、JSON 文字列リテラルの規則で
///   既に改行がエスケープされているため、二重に畳むと戻せなくなる。
///
/// 【後方互換】
/// 改行を含まない既存の文字列は Escape / Unescape を通しても
/// バックスラッシュを含まない限り変化しない。バックスラッシュを含む値も
/// エスケープ往復で元へ戻るため、値は失われない。
/// </summary>
public static class ScriptTextArea
{
    // ─── 属性名（アセンブリ ID 差異を吸収するため名前で照合する）───

    /// <summary>
    /// <c>[TextArea]</c> 属性の型名。
    /// エディタの Roslyn コンパイルとランタイムの ALC コンパイルでは属性の
    /// アセンブリ ID が異なり得るため、型一致ではなくこの名前で照合すること。
    /// </summary>
    public const string AttributeName = "TextAreaAttribute";

    // ─── 表示行数 ─────────────────────────────────────────────

    /// <summary>表示行数の下限（0 行や負値の指定を丸めるための最小値）。</summary>
    public const int MinimumLines = 1;

    /// <summary>
    /// <c>[TextArea]</c> の指定から実際の表示行数を決める。
    ///
    /// minLines / maxLines は 0 なら「指定なし」。
    /// 下限・上限の両方が指定され、かつ上限が下限を下回っている矛盾した指定では、
    /// 下限を優先する（少なくとも読める高さを確保する側に倒す）。
    /// </summary>
    /// <param name="lines">属性の Lines（省略時は既定行数が入っている）。</param>
    /// <param name="minLines">属性の MinLines（0 なら下限なし）。</param>
    /// <param name="maxLines">属性の MaxLines（0 なら上限なし）。</param>
    /// <returns>1 以上に丸めた表示行数。</returns>
    public static int ResolveLines(int lines, int minLines, int maxLines)
    {
        var result = lines;

        // 上限 → 下限の順に適用する。矛盾指定（max < min）のときは下限が最後に効く。
        if (maxLines > 0 && result > maxLines) result = maxLines;
        if (minLines > 0 && result < minLines) result = minLines;

        return result < MinimumLines ? MinimumLines : result;
    }

    // ─── 改行の輸送書式 ───────────────────────────────────────

    /// <summary>エスケープの導入文字（バックスラッシュ）。</summary>
    private const char EscapeChar = '\\';

    /// <summary>改行を表すエスケープ文字。</summary>
    private const char NewLineChar = 'n';

    /// <summary>タブを表すエスケープ文字。</summary>
    private const char TabChar = 't';

    /// <summary>
    /// 生の文字列を IPC・シーン保存用のエスケープ表記へ畳む。
    ///
    /// バックスラッシュ自体を先に 2 文字へ増やしてから改行・タブを置換するので、
    /// Unescape で正確に元へ戻せる。
    /// CR+LF・CR・LF はすべて LF へ正規化される
    /// （行末表現の差でシーンの差分が出るのを防ぐため）。
    /// </summary>
    /// <param name="raw">生の文字列（null は空文字として扱う）。</param>
    public static string Escape(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return string.Empty;

        var sb = new StringBuilder(raw!.Length);
        for (int i = 0; i < raw.Length; i++)
        {
            var c = raw[i];
            switch (c)
            {
                case EscapeChar:
                    sb.Append(EscapeChar).Append(EscapeChar);
                    break;
                case '\r':
                    // CR+LF は 1 つの改行として畳む（次の LF を読み飛ばす）
                    if (i + 1 < raw.Length && raw[i + 1] == '\n') i++;
                    sb.Append(EscapeChar).Append(NewLineChar);
                    break;
                case '\n':
                    sb.Append(EscapeChar).Append(NewLineChar);
                    break;
                case '\t':
                    sb.Append(EscapeChar).Append(TabChar);
                    break;
                default:
                    sb.Append(c);
                    break;
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Escape で畳んだ表記を生の文字列へ戻す。
    ///
    /// 未知のエスケープ（バックスラッシュ + a など）は「バックスラッシュ + その文字」を
    /// そのまま残す（勝手に文字を捨てないための保守的な方針）。
    /// 末尾が単独のバックスラッシュで終わる場合もそのまま残す。
    /// </summary>
    /// <param name="escaped">エスケープ表記の文字列（null は空文字として扱う）。</param>
    public static string Unescape(string? escaped)
    {
        if (string.IsNullOrEmpty(escaped)) return string.Empty;

        var sb = new StringBuilder(escaped!.Length);
        for (int i = 0; i < escaped.Length; i++)
        {
            var c = escaped[i];
            if (c != EscapeChar || i + 1 >= escaped.Length)
            {
                sb.Append(c);
                continue;
            }

            var next = escaped[++i];
            switch (next)
            {
                case NewLineChar: sb.Append('\n');       break;
                case TabChar:     sb.Append('\t');       break;
                case EscapeChar:  sb.Append(EscapeChar); break;
                default:
                    // 未知のエスケープは元の 2 文字をそのまま残す
                    sb.Append(EscapeChar).Append(next);
                    break;
            }
        }
        return sb.ToString();
    }
}
