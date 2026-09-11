using System.Collections.Generic;

/// <summary>
/// ビートパターンのテキスト記法を解析する<b>純 C#</b> のパーサ
/// 【記法の解釈を持つ唯一の場所】。
///
/// エンジン API（SEED.*）に一切依存しないので、使い捨てのコンソールや
/// 単体テストからそのまま呼べる（＝レベルデザイン用データを机上で検証できる）。
///
/// <b>記法</b>
/// <code>
/// {1/4}t,,t,,  {1/6},,,{1/4}t,,  [1-3,5]   # 行末までコメント
/// </code>
/// - <c>{1/n}</c> … 以降のカンマ 1 つが「1/n 音符」（小節を 1.0 としたとき 1/n の長さ）
/// - <c>,</c>     … その音価で時間を 1 単位進める
/// - <c>t</c>     … <b>直後のカンマ</b>が表す音価の先頭で叩く（トリガ）
/// - <c>[...]</c> … 対象の魚レベル帯（<see cref="LevelRange"/>。省略で全レベル）
/// - <c>#</c>     … 以降は行末までコメント。空白は自由・空行は無視
///
/// <b>検証</b>
/// カンマの合計が「小節の整数倍」かつ「想定小節数と一致」でなければエラー。
/// 合計は浮動小数ではなく有理数（<see cref="Fraction"/>）で積むので、
/// 1/6 と 1/4 が混ざっても厳密に判定できる。
///
/// <b>単一責任</b>: 文字列 → <see cref="BeatPattern"/> の変換と検証だけを行う。
/// ファイル読み込み・抽選・ホットリロードは <see cref="BeatPatternLibrary"/> の責務。
/// </summary>
public static class BeatPatternParser
{
    // ─── 記法のトークン ─────────────────────────────────

    /// <summary>コメントの開始文字（これ以降は行末まで無視）。</summary>
    public const char CommentChar = '#';

    /// <summary>音価の指定を開く文字。</summary>
    private const char UnitOpen = '{';

    /// <summary>音価の指定を閉じる文字。</summary>
    private const char UnitClose = '}';

    /// <summary>レベル帯を開く文字。</summary>
    private const char LevelOpen = '[';

    /// <summary>レベル帯を閉じる文字。</summary>
    private const char LevelClose = ']';

    /// <summary>時間を 1 単位進める文字。</summary>
    private const char AdvanceChar = ',';

    /// <summary>打点（トリガ）を表す文字。大文字も受け付ける。</summary>
    private const char TriggerChar = 't';

    /// <summary>音価の分子（"1/n" の 1）。記法上つねに 1 なので定数で持つ。</summary>
    private const long UnitNumerator = 1;

    /// <summary>音価の指定の区切り（"1/n" のスラッシュ）。</summary>
    private const char UnitFractionSeparator = '/';

    /// <summary>音価の分母として許す最小値（"1/1" ＝ 全音符）。</summary>
    private const int MinUnitDenominator = 1;

    /// <summary>
    /// 音価の分母として許す最大値。
    /// 極端な値（1/1000 など）はレベルデザインの誤記とみなして弾く。
    /// </summary>
    private const int MaxUnitDenominator = 64;

    /// <summary>小節数の下限（0 小節の行は無意味なのでエラー）。</summary>
    private const int MinBars = 1;

    /// <summary>行番号の始まり（1 始まりで数える）。</summary>
    private const int FirstLineNumber = 1;

    /// <summary>音価がまだ指定されていないことを表す分母。</summary>
    private const int NoUnitDenominator = 0;

    // ─── 公開 API ────────────────────────────────────────

    /// <summary>
    /// テキスト全体を解析する【ファイル 1 本を読む唯一の入口】。
    ///
    /// 行ごとに独立して解析し、エラーになった行は<b>捨てて</b>
    /// 行番号つきのメッセージを <paramref name="errors"/> へ積む
    /// （1 行の誤記でファイル全体が使えなくならないようにするため）。
    /// </summary>
    /// <param name="text">ファイル本文（改行コードは CRLF / LF どちらでもよい）。</param>
    /// <param name="expectedBars">1 行に期待する小節数（＝戦闘サイクルの長さ）。</param>
    /// <param name="patterns">解析できたパターン（ファイル内の順序を保つ）。</param>
    /// <param name="errors">エラーメッセージ（行番号つき）。</param>
    public static void ParseText(string text, int expectedBars,
                                 List<BeatPattern> patterns, List<string> errors)
    {
        patterns.Clear();
        errors.Clear();
        if (string.IsNullOrEmpty(text)) { return; }

        // CRLF・CR・LF のいずれでも 1 行として切り出す
        string[] lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

        for (int i = 0; i < lines.Length; i++)
        {
            int lineNumber = i + FirstLineNumber;

            if (TryParseLine(lines[i], expectedBars, lineNumber, out BeatPattern? pattern, out string error))
            {
                // コメント行・空行は「エラーでもパターンでもない」ので両方 null / 空になる
                if (pattern is { } p) { patterns.Add(p); }
                continue;
            }

            errors.Add($"{lineNumber} 行目: {error}");
        }
    }

    /// <summary>
    /// 1 行を解析する【行の記法解釈の唯一の実装】。
    ///
    /// 戻り値が true でも <paramref name="pattern"/> が null になる場合がある
    /// （コメントだけの行・空行）。これは「正常だが打点は無い」という意味。
    /// </summary>
    /// <param name="rawLine">元の 1 行（コメント・空白を含んでよい）。</param>
    /// <param name="expectedBars">期待する小節数。</param>
    /// <param name="lineNumber">行番号（1 始まり。ログ用）。</param>
    /// <param name="pattern">解析結果（空行・コメント行なら null）。</param>
    /// <param name="error">失敗の理由（成功時は空文字列）。</param>
    /// <returns>解析できた（またはコメント・空行だった）なら true。</returns>
    public static bool TryParseLine(string rawLine, int expectedBars, int lineNumber,
                                    out BeatPattern? pattern, out string error)
    {
        pattern = null;
        error = "";

        // 1. コメントの除去（# 以降は行末まで）
        string line = StripComment(rawLine ?? "").Trim();
        if (line.Length == 0) { return true; }

        // 2. レベル帯（角括弧）を切り出して本文と分ける
        if (!TrySplitLevelRange(line, out string body, out LevelRange levels, out error))
        {
            return false;
        }

        // 3. 本文（音価・トリガ・カンマ）を走査して打点位置を積む
        var positions = new List<double>();
        Fraction elapsed = Fraction.Zero;          // 行頭からの経過（小節単位・厳密）
        int unitDenominator = NoUnitDenominator;   // いま有効な音価の分母（1/n の n）
        bool triggerPending = false;               // 直前に t があり、次のカンマで叩く

        for (int i = 0; i < body.Length; i++)
        {
            char c = body[i];

            if (char.IsWhiteSpace(c)) { continue; }

            if (c == UnitOpen)
            {
                if (!TryReadUnit(body, ref i, out unitDenominator, out error)) { return false; }
                continue;
            }

            if (c == TriggerChar || c == char.ToUpperInvariant(TriggerChar))
            {
                if (triggerPending)
                {
                    error = "打点 t が連続しています（t の直後には必ずカンマを 1 つ置くこと）";
                    return false;
                }
                triggerPending = true;
                continue;
            }

            if (c == AdvanceChar)
            {
                if (unitDenominator == NoUnitDenominator)
                {
                    error = "音価が未指定のままカンマが現れました（行の先頭で {1/4} のように指定すること）";
                    return false;
                }

                // 打点は「これから進む 1 単位の先頭」＝いまの経過位置に置く
                if (triggerPending)
                {
                    positions.Add(elapsed.ToDouble());
                    triggerPending = false;
                }

                elapsed = elapsed.Add(new Fraction(UnitNumerator, unitDenominator));
                continue;
            }

            error = $"解釈できない文字 '{c}' があります（使えるのは {{1/n}} ・ t ・ カンマ ・ 角括弧のみ）";
            return false;
        }

        // 4. 行全体の検証
        if (triggerPending)
        {
            error = "行の末尾に打点 t が残っています（t の直後にはカンマが必要）";
            return false;
        }

        if (!elapsed.IsPositive)
        {
            error = "音符が 1 つもありません";
            return false;
        }

        if (!elapsed.IsInteger)
        {
            error = $"音価の合計 {elapsed} が小節の整数倍になっていません";
            return false;
        }

        int bars = (int)elapsed.IntegerValue;
        if (bars < MinBars)
        {
            error = $"小節数 {bars} が短すぎます";
            return false;
        }

        if (bars != expectedBars)
        {
            error = $"小節数が {bars} ですが、想定は {expectedBars} 小節です";
            return false;
        }

        pattern = new BeatPattern(positions, bars, levels, lineNumber, line);
        return true;
    }

    // ─── 内部処理 ────────────────────────────────────────

    /// <summary>コメント（<see cref="CommentChar"/> 以降）を取り除く。</summary>
    /// <param name="line">元の行。</param>
    private static string StripComment(string line)
    {
        int index = line.IndexOf(CommentChar);
        return index < 0 ? line : line.Substring(0, index);
    }

    /// <summary>
    /// 行からレベル帯（角括弧）を切り出し、本文と分ける
    /// 【レベル帯の位置を決める唯一の場所】。
    ///
    /// 角括弧が無ければ全レベル（<see cref="LevelRange.Any"/>）。
    /// 角括弧は 1 組だけ許し、行のどこにあってもよい（末尾に置くのが慣例）。
    /// </summary>
    /// <param name="line">コメント除去済みの行。</param>
    /// <param name="body">角括弧を取り除いた本文。</param>
    /// <param name="levels">解析したレベル帯。</param>
    /// <param name="error">失敗の理由。</param>
    /// <returns>切り出せたら true。</returns>
    private static bool TrySplitLevelRange(string line, out string body,
                                           out LevelRange levels, out string error)
    {
        body = line;
        levels = LevelRange.Any;
        error = "";

        int open = line.IndexOf(LevelOpen);
        if (open < 0)
        {
            // 閉じ括弧だけがある行は書き間違いなので弾く
            if (line.IndexOf(LevelClose) >= 0)
            {
                error = "レベル帯の開き括弧 [ がありません";
                return false;
            }
            return true;
        }

        int close = line.IndexOf(LevelClose, open + 1);
        if (close < 0)
        {
            error = "レベル帯の閉じ括弧 ] がありません";
            return false;
        }

        // 2 組目の角括弧はレベル帯の書き間違いとして弾く（意味が決まらないため）
        if (line.IndexOf(LevelOpen, close + 1) >= 0 || line.IndexOf(LevelClose, close + 1) >= 0)
        {
            error = "レベル帯の角括弧が 2 組以上あります";
            return false;
        }

        string inner = line.Substring(open + 1, close - open - 1);
        if (!LevelRange.TryParse(inner, out levels, out error)) { return false; }

        body = line.Substring(0, open) + line.Substring(close + 1);
        return true;
    }

    /// <summary>
    /// 音価の指定 <c>{1/n}</c> を読み取る【音価の解釈の唯一の場所】。
    /// 読み取り後、<paramref name="index"/> は閉じ括弧の位置を指す
    /// （呼び出し側の for ループがそこから 1 つ進める）。
    /// </summary>
    /// <param name="body">本文。</param>
    /// <param name="index">開き括弧の位置（読み取り後は閉じ括弧の位置になる）。</param>
    /// <param name="denominator">読み取った分母（1/n の n）。</param>
    /// <param name="error">失敗の理由。</param>
    /// <returns>読み取れたら true。</returns>
    private static bool TryReadUnit(string body, ref int index, out int denominator, out string error)
    {
        denominator = NoUnitDenominator;
        error = "";

        int close = body.IndexOf(UnitClose, index + 1);
        if (close < 0)
        {
            error = "音価の閉じ括弧 } がありません";
            return false;
        }

        string inner = body.Substring(index + 1, close - index - 1).Trim();
        int slash = inner.IndexOf(UnitFractionSeparator);
        if (slash < 0)
        {
            error = $"音価 {{{inner}}} の書式が違います（{{1/4}} のように書くこと）";
            return false;
        }

        string numeratorText = inner.Substring(0, slash).Trim();
        string denominatorText = inner.Substring(slash + 1).Trim();

        if (!long.TryParse(numeratorText, out long numerator) || numerator != UnitNumerator)
        {
            error = $"音価 {{{inner}}} の分子は 1 だけです";
            return false;
        }

        if (!int.TryParse(denominatorText, out int parsed)
            || parsed < MinUnitDenominator || parsed > MaxUnitDenominator)
        {
            error = $"音価 {{{inner}}} の分母は {MinUnitDenominator}〜{MaxUnitDenominator} の整数にすること";
            return false;
        }

        denominator = parsed;
        index = close;
        return true;
    }
}