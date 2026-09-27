using System;

namespace SEED.UI;

// ============================================================
//  WheelLoop.cs — ホイールの行の番号と項目・位置の計算（端をつなげる循環を含む。W2-5。純粋な計算）
//
//  【言葉】
//    項目（item）… 選べる値の番号 0..count−1（例: 分の 00〜59 なら 60 個）
//    行（row）   … 一覧（ListView）の行の番号。端をつながないなら 行 = 項目。つなげるなら 項目を cycles 回くり返した
//                  count × cycles 行を並べ、行 r の項目は r mod count（負でも 0..count−1）
//  【位置】先頭の余白 =（窓の高さ − 行の高さ）/ 2 にすると、スクロールの位置 p = r × 行の高さ のとき行 r が窓の中央に来る
//  （CanvasScroll の Interval のスナップ〈位置 0 から数えた間隔の倍数〉とそのまま一致する）。中央の行 = round(p / 行の高さ)。
//  【循環】無限の一覧は作らず、十分に長い一覧（中身の長さ ≒ LoopTargetExtent）の真ん中から始め、止まったときに真ん中から
//  遠く離れていれば、同じ項目の真ん中の周の行へ見えない飛び方をする（項目の並びが周ごとに同じなので見た目は変わらない）。
//  中身の長さを抑えるのは位置が float（スクリプトの Vector2）で表されるため（20 万で 1/64 単位の刻み）。
//  エンジンの API に触れない（editor/tests/UiComponentsTests で検算）。
// ============================================================

/// <summary>ホイールの行・項目・位置の計算（端をつなげる循環を含む）。</summary>
public static class WheelLoop
{
    /// <summary>
    /// 端をつなげるときの中身の長さの目安（キャンバスの単位）。float の刻みは 2^17 ≒ 13 万〜 26 万で 1/64 単位
    /// （dp のキャンバスで Pixel 6a の 2.625 倍でも 0.05 画素未満）。真ん中から片側 10 万ぶん＝最速のフリック（8,000 dp/秒 →
    /// 跳ね返りの慣性で約 4,000）の 25 回ぶん動いても端に着かない。
    /// </summary>
    public const float LoopTargetExtent = 200_000f;

    /// <summary>端をつなげるときの周の数の下限（真ん中の周の前後に少なくとも 1 周ずつ）。</summary>
    public const int MinCycles = 3;

    /// <summary>止まったとき、真ん中の行から「全体の行の数 × この割合」より離れていれば真ん中の周へ戻す。</summary>
    public const float RecenterFraction = 0.25f;

    /// <summary>行の高さの下限（0・負・NaN で割り算にならないように）。</summary>
    public const float MinItemExtent = 1f;

    /// <summary>半分。</summary>
    private const float Half = 0.5f;

    /// <summary>行の高さを使える値にする（下限 <see cref="MinItemExtent"/>）。</summary>
    public static float SanitizeExtent(float itemExtent)
        => float.IsFinite(itemExtent) && itemExtent >= MinItemExtent ? itemExtent : MinItemExtent;

    /// <summary>
    /// 周の数（端をつながないなら 1。つなげるなら中身の長さが <see cref="LoopTargetExtent"/> 程度になる数で、
    /// 下限 <see cref="MinCycles"/>・真ん中の周が 1 つに決まるよう奇数）。
    /// </summary>
    public static int CycleCount(int count, float itemExtent, bool looping)
    {
        if (!looping || count <= 0) return 1;
        double cycleExtent = (double)count * SanitizeExtent(itemExtent);
        // 行の数（count × cycles）が int に収まる範囲で、中身の長さが目安に届く周の数
        double byExtent = Math.Floor(LoopTargetExtent / cycleExtent);
        double byOverflow = Math.Floor((double)int.MaxValue / count);
        int cycles = (int)Math.Max(MinCycles, Math.Min(byExtent, byOverflow));
        return cycles % 2 == 0 ? cycles - 1 : cycles;
    }

    /// <summary>一覧の行の数（項目の数 × 周の数）。</summary>
    public static int TotalRows(int count, int cycles) => (int)Math.Min(int.MaxValue, (long)Math.Max(0, count) * Math.Max(1, cycles));

    /// <summary>行の項目（行 mod 項目の数。負の行・大きな行でも 0..count−1）。</summary>
    public static int ItemOfRow(int row, int count)
    {
        if (count <= 0) return 0;
        int m = row % count;
        return m < 0 ? m + count : m;
    }

    /// <summary>真ん中の周の最初の行。</summary>
    public static int CenterCycleFirstRow(int count, int cycles) => Math.Max(0, count) * (Math.Max(1, cycles) / 2);

    /// <summary>真ん中の周での項目の行（最初の置き場）。</summary>
    public static int CenterRowOfItem(int item, int count, int cycles)
        => CenterCycleFirstRow(count, cycles) + ItemOfRow(item, count);

    /// <summary>
    /// 行 <paramref name="fromRow"/> から最も近い、項目 <paramref name="item"/> の行（端をつなげる一覧で近い向きへ回る。
    /// ちょうど半周のときは進む向き）。一覧の外へ出るなら 1 周ぶん内側へ戻す。
    /// </summary>
    public static int NearestRowOfItem(int item, int fromRow, int count, int totalRows)
    {
        if (count <= 0 || totalRows <= 0) return 0;
        long delta = ItemOfRow(item, count) - ItemOfRow(fromRow, count);
        // 差を (−count/2, count/2] へ寄せる（半周ちょうどは進む向き）
        if (2 * delta > count) delta -= count;
        else if (2 * delta <= -count) delta += count;
        long row = fromRow + delta;
        while (row < 0) row += count;
        while (row >= totalRows) row -= count;
        return (int)Math.Clamp(row, 0, totalRows - 1);
    }

    /// <summary>止まった行が真ん中から遠く離れていて、真ん中の周へ戻すべきか（端をつなげるときだけ）。</summary>
    public static bool ShouldRecenter(int row, int count, int cycles)
    {
        if (count <= 0 || cycles < MinCycles) return false;
        long total = (long)count * cycles;
        long centerRow = CenterCycleFirstRow(count, cycles) + ItemOfRow(row, count);
        return Math.Abs(row - centerRow) > total * RecenterFraction;
    }

    /// <summary>行と同じ項目の、真ん中の周の行。</summary>
    public static int RecenteredRow(int row, int count, int cycles) => CenterRowOfItem(ItemOfRow(row, count), count, cycles);

    /// <summary>先頭（と末尾）の余白（行 r が位置 r × 行の高さで窓の中央に来る長さ）。</summary>
    public static float LeadPadding(float viewportExtent, float itemExtent)
        => Math.Max(0f, (viewportExtent - SanitizeExtent(itemExtent)) * Half);

    /// <summary>行 r が中央に来るスクロールの位置。</summary>
    public static float PositionOfRow(int row, float itemExtent) => row * SanitizeExtent(itemExtent);

    /// <summary>スクロールの位置で中央にある行（0..行の数−1 へ収める。半分は遠い方へ丸める＝Flutter の round と同じ）。</summary>
    public static int RowAtPosition(float position, float itemExtent, int totalRows)
    {
        if (totalRows <= 0 || !float.IsFinite(position)) return 0;
        double row = Math.Round(position / (double)SanitizeExtent(itemExtent), MidpointRounding.AwayFromZero);
        return (int)Math.Clamp(row, 0, totalRows - 1);
    }

    /// <summary>
    /// 選べる項目のうち、項目 <paramref name="item"/> に最も近いもの（同じ近さなら進む向き。端をつながないなら範囲の中だけを探す）。
    /// 選べる項目が無ければ −1。
    /// </summary>
    public static int NearestEnabledItem(int item, int count, bool looping, Func<int, bool> isEnabled)
    {
        if (count <= 0) return -1;
        int start = looping ? ItemOfRow(item, count) : Math.Clamp(item, 0, count - 1);
        for (int d = 0; d < count; d++)
        {
            // 進む向きを先に調べる（同じ近さなら進む向き）
            int forward = Candidate(start + d, count, looping);
            if (forward >= 0 && isEnabled(forward)) return forward;
            int backward = Candidate(start - d, count, looping);
            if (backward >= 0 && isEnabled(backward)) return backward;
        }
        return -1;
    }

    /// <summary>候補の項目（端をつなげるなら mod、つながないなら範囲の外は −1）。</summary>
    private static int Candidate(int item, int count, bool looping)
        => looping ? ItemOfRow(item, count) : (item >= 0 && item < count ? item : -1);

    /// <summary>
    /// 項目 <paramref name="item"/> から <paramref name="delta"/> 個進んだ項目（選べない項目は飛ばす。端をつながないなら端で止まり、
    /// その向きに選べる項目が無ければ元の項目）。
    /// </summary>
    public static int StepItem(int item, int delta, int count, bool looping, Func<int, bool> isEnabled)
    {
        if (count <= 0 || delta == 0) return item;
        int direction = Math.Sign(delta);
        int remaining = Math.Abs(delta);
        int current = item;
        int result = item;
        // 1 歩ずつ進み、選べる項目に着くたびに残りを 1 減らす（最大で count 周ぶん調べる）
        for (int guard = 0; guard < count * Math.Max(1, remaining) && remaining > 0; guard++)
        {
            int next = current + direction;
            if (looping) next = ItemOfRow(next, count);
            else if (next < 0 || next >= count) break;
            current = next;
            if (!isEnabled(current)) continue;
            result = current;
            remaining--;
        }
        return result;
    }
}
