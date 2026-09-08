using System.Collections.Generic;

/// <summary>
/// ビートパターン 1 行が対象にする<b>魚のレベル帯</b>（"[1-3,5]" の角括弧の中身）。
///
/// <b>記法</b>（カンマ区切りで範囲と単独を混在できる）
/// <code>
/// [1-3]    Lv1〜3
/// [1-3,5]  Lv1〜3 と Lv5
/// [5]      Lv5 のみ
/// [3-]     Lv3 以上
/// [-2]     Lv2 以下
/// [*]      全レベル（角括弧そのものを省略した場合も同じ）
/// </code>
/// 範囲外の値・重複は許容する（レベル上限を知らなくても書けるようにするため）。
///
/// <b>単一責任</b>: 文字列の解析と <see cref="Contains"/> の判定だけを持つ。
/// どの行を選ぶかという抽選は <see cref="BeatPatternLibrary"/> の責務。
/// </summary>
public sealed class LevelRange
{
    /// <summary>全レベルを表す記号。</summary>
    public const string AnyToken = "*";

    /// <summary>範囲の区切り文字（"1-3" のハイフン）。</summary>
    private const char RangeSeparator = '-';

    /// <summary>項目の区切り文字（"1-3,5" のカンマ）。</summary>
    private const char ItemSeparator = ',';

    /// <summary>下限を指定しないときに使う値（＝どんなレベルでも下限を満たす）。</summary>
    private const int NoLowerBound = int.MinValue;

    /// <summary>上限を指定しないときに使う値（＝どんなレベルでも上限を満たす）。</summary>
    private const int NoUpperBound = int.MaxValue;

    /// <summary>全レベルを受け入れる範囲（"[*]" と省略時に使う共有インスタンス）。</summary>
    public static readonly LevelRange Any = new LevelRange(isAny: true, spans: new List<Span>());

    /// <summary>閉区間 1 つ（下限・上限。片側だけの指定は番人値で表す）。</summary>
    private readonly struct Span
    {
        /// <summary>下限（この値以上が対象）。</summary>
        public int Min { get; }

        /// <summary>上限（この値以下が対象）。</summary>
        public int Max { get; }

        /// <summary>下限・上限から区間を作る。</summary>
        /// <param name="min">下限。</param>
        /// <param name="max">上限。</param>
        public Span(int min, int max) { Min = min; Max = max; }

        /// <summary>この区間に <paramref name="level"/> が入るか。</summary>
        /// <param name="level">判定するレベル。</param>
        public bool Contains(int level) => level >= Min && level <= Max;
    }

    /// <summary>全レベル対象か（"[*]" または省略）。</summary>
    public bool IsAny { get; }

    /// <summary>対象となる区間の一覧（<see cref="IsAny"/> が true のときは空）。</summary>
    private readonly List<Span> spans;

    /// <summary>区間の一覧から範囲を作る（生成は <see cref="TryParse"/> と <see cref="Any"/> だけ）。</summary>
    /// <param name="isAny">全レベル対象か。</param>
    /// <param name="spans">区間の一覧。</param>
    private LevelRange(bool isAny, List<Span> spans)
    {
        IsAny = isAny;
        this.spans = spans;
    }

    /// <summary>
    /// このレベル帯が <paramref name="level"/> を含むか【レベル判定の唯一の出口】。
    /// </summary>
    /// <param name="level">魚のレベル（1 始まり）。</param>
    public bool Contains(int level)
    {
        if (IsAny) { return true; }

        foreach (var span in spans)
        {
            if (span.Contains(level)) { return true; }
        }
        return false;
    }

    /// <summary>
    /// 角括弧の中身（角括弧を含まない文字列）を解析する
    /// 【レベル帯の解析の唯一の入口】。
    ///
    /// 空文字列と <see cref="AnyToken"/> は全レベル扱い。
    /// 数値でない項目・下限が上限を超える項目があれば false を返し、
    /// 理由を <paramref name="error"/> に入れる。
    /// </summary>
    /// <param name="body">角括弧の中身（例: "1-3,5"）。</param>
    /// <param name="range">解析結果（失敗時は <see cref="Any"/>）。</param>
    /// <param name="error">失敗の理由（成功時は空文字列）。</param>
    /// <returns>解析できたら true。</returns>
    public static bool TryParse(string? body, out LevelRange range, out string error)
    {
        range = Any;
        error = "";

        string text = (body ?? "").Trim();
        if (text.Length == 0 || text == AnyToken) { return true; }

        var spans = new List<Span>();
        foreach (string rawItem in text.Split(ItemSeparator))
        {
            string item = rawItem.Trim();
            if (item.Length == 0)
            {
                error = "レベル帯に空の項目があります";
                return false;
            }

            if (item == AnyToken)
            {
                // 1 項目でもアスタリスクがあれば、その行は全レベル対象になる
                range = Any;
                return true;
            }

            if (!TryParseItem(item, out Span span, out error)) { return false; }
            spans.Add(span);
        }

        range = new LevelRange(isAny: false, spans: spans);
        return true;
    }

    /// <summary>
    /// レベル帯の項目 1 つ（"5" / "1-3" / "3-" / "-2"）を区間へ直す。
    /// </summary>
    /// <param name="item">項目の文字列（前後の空白は除去済み）。</param>
    /// <param name="span">解析結果の区間。</param>
    /// <param name="error">失敗の理由。</param>
    /// <returns>解析できたら true。</returns>
    private static bool TryParseItem(string item, out Span span, out string error)
    {
        span = new Span(NoLowerBound, NoUpperBound);
        error = "";

        int separatorIndex = item.IndexOf(RangeSeparator);

        // 区切りが無い ＝ 単独のレベル（例: "5"）
        if (separatorIndex < 0)
        {
            if (!TryParseLevel(item, out int only, out error)) { return false; }
            span = new Span(only, only);
            return true;
        }

        string left = item.Substring(0, separatorIndex).Trim();
        string right = item.Substring(separatorIndex + 1).Trim();

        // ハイフンだけの項目は上限も下限も無く "[*]" と区別が付かないので、誤記として弾く
        if (left.Length == 0 && right.Length == 0)
        {
            error = $"レベル帯の項目 {item} に数値がありません";
            return false;
        }

        int min = NoLowerBound;
        int max = NoUpperBound;

        if (left.Length > 0 && !TryParseLevel(left, out min, out error)) { return false; }
        if (right.Length > 0 && !TryParseLevel(right, out max, out error)) { return false; }

        if (min > max)
        {
            error = $"レベル帯の項目 {item} は下限が上限を超えています";
            return false;
        }

        span = new Span(min, max);
        return true;
    }

    /// <summary>レベルの数値 1 つを解析する。</summary>
    /// <param name="text">数値の文字列。</param>
    /// <param name="level">解析結果。</param>
    /// <param name="error">失敗の理由。</param>
    /// <returns>解析できたら true。</returns>
    private static bool TryParseLevel(string text, out int level, out string error)
    {
        error = "";
        if (int.TryParse(text, out level)) { return true; }

        level = 0;
        error = $"レベル帯 {text} が数値ではありません";
        return false;
    }
}