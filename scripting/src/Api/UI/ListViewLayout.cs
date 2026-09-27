using System;

namespace SEED.UI;

// ============================================================
//  ListViewLayout.cs — 一覧の行の並びの計算（行の先頭の位置・全体の長さ・見える行の範囲。W2-3）
//
//  エンジンの API に触れない純粋な計算（editor/tests/UiListViewTests から単体で試せる）。一覧（ListView）が
//  スクロールの位置と窓の長さから「どの行を置くか」を決めるのに使う。
//
//  【並び】（スクロールの軸に沿って。単位はキャンバスの単位）
//      先頭の余白 | 行 0 | 間隔 | 行 1 | 間隔 | … | 行 n−1 | 末尾の余白
//  行の長さは全部同じ（固定）か、行ごと（ExtentOf）。行ごとのときは先頭の位置の累積和を持ち、位置から行を二分探索で引く。
//  【見える行】窓 [位置, 位置 + 窓の長さ] を前後に cacheExtent だけ広げた範囲と交わる行（Flutter の cacheExtent と同じ考え方。
//  窓の外の少し先まで行を作っておき、速いスクロールでも行が遅れて出ないようにする）。
// ============================================================

/// <summary>見える行の範囲（両端を含む。空なら <see cref="IsEmpty"/>）。</summary>
public readonly struct ListRange
{
    /// <summary>最初の行。</summary>
    public int First { get; }
    /// <summary>最後の行（含む）。</summary>
    public int Last { get; }

    /// <summary>作る。</summary>
    public ListRange(int first, int last) { First = first; Last = last; }

    /// <summary>空の範囲。</summary>
    public static ListRange Empty => new(0, -1);

    /// <summary>行が 1 つも無いか。</summary>
    public bool IsEmpty => Last < First;

    /// <summary>行の数。</summary>
    public int Count => IsEmpty ? 0 : Last - First + 1;

    /// <summary>行を含むか。</summary>
    public bool Contains(int index) => index >= First && index <= Last;

    /// <summary>表示用。</summary>
    public override string ToString() => IsEmpty ? "[]" : $"[{First}..{Last}]";
}

/// <summary>
/// 一覧の行の並び（行の先頭の位置・長さ・全体の長さ・見える行の範囲）を求める純粋な計算。
/// </summary>
public sealed class ListViewLayout
{
    /// <summary>行の数。</summary>
    public int Count { get; }
    /// <summary>行と行の間隔。</summary>
    public float Spacing { get; }
    /// <summary>先頭の余白。</summary>
    public float LeadingPadding { get; }
    /// <summary>末尾の余白。</summary>
    public float TrailingPadding { get; }
    /// <summary>全体の長さ（余白・間隔を含む。スクロールの中身の長さ）。</summary>
    public float TotalExtent { get; }

    /// <summary>固定の行の長さ（行ごとのときは 0）。</summary>
    private readonly float _fixedExtent;
    /// <summary>行ごとの先頭の位置（行ごとのときだけ。Count + 1 個。最後は末尾の行の終わり + 間隔）。</summary>
    private readonly float[]? _starts;
    /// <summary>行ごとの長さ（行ごとのときだけ）。</summary>
    private readonly float[]? _extents;

    private ListViewLayout(int count, float fixedExtent, float[]? extents, float spacing, float leading, float trailing)
    {
        Count = Math.Max(0, count);
        Spacing = Sanitize(spacing);
        LeadingPadding = Sanitize(leading);
        TrailingPadding = Sanitize(trailing);
        _fixedExtent = Sanitize(fixedExtent);
        if (extents is not null)
        {
            _extents = extents;
            _starts = new float[Count + 1];
            float cursor = LeadingPadding;
            for (int i = 0; i < Count; i++)
            {
                _starts[i] = cursor;
                cursor += _extents[i] + Spacing;
            }
            _starts[Count] = cursor;
        }
        TotalExtent = Count == 0
            ? LeadingPadding + TrailingPadding
            : EndOf(Count - 1) + TrailingPadding;
    }

    /// <summary>負・NaN・無限大を 0 にする。</summary>
    private static float Sanitize(float v) => float.IsFinite(v) && v > 0f ? v : 0f;

    /// <summary>行の長さが全部同じ一覧。</summary>
    /// <param name="count">行の数。</param>
    /// <param name="rowExtent">行の長さ（スクロールの軸に沿って）。</param>
    /// <param name="spacing">行と行の間隔。</param>
    /// <param name="leadingPadding">先頭の余白。</param>
    /// <param name="trailingPadding">末尾の余白。</param>
    public static ListViewLayout Fixed(int count, float rowExtent, float spacing = 0f, float leadingPadding = 0f, float trailingPadding = 0f)
        => new(count, rowExtent, null, spacing, leadingPadding, trailingPadding);

    /// <summary>行ごとに長さが違う一覧（行の数だけ <paramref name="extentOf"/> を 1 回ずつ呼ぶ）。</summary>
    public static ListViewLayout Variable(int count, Func<int, float> extentOf, float spacing = 0f, float leadingPadding = 0f, float trailingPadding = 0f)
    {
        int n = Math.Max(0, count);
        var extents = new float[n];
        for (int i = 0; i < n; i++) extents[i] = Sanitize(extentOf(i));
        return new(n, 0f, extents, spacing, leadingPadding, trailingPadding);
    }

    /// <summary>行の長さ。</summary>
    public float ExtentOf(int index) => _extents is not null ? _extents[index] : _fixedExtent;

    /// <summary>行の先頭の位置（中身の先頭から）。</summary>
    public float OffsetOf(int index)
        => _starts is not null ? _starts[index] : LeadingPadding + index * (_fixedExtent + Spacing);

    /// <summary>行の終わりの位置。</summary>
    public float EndOf(int index) => OffsetOf(index) + ExtentOf(index);

    /// <summary>
    /// 位置を含む行（間隔・余白の中なら次の行。範囲の外は先頭か最後の行へ収める）。行が無ければ −1。
    /// </summary>
    public int IndexAt(float position)
    {
        if (Count == 0) return -1;
        if (_starts is null)
        {
            float stride = _fixedExtent + Spacing;
            if (stride <= 0f) return 0;
            int i = (int)MathF.Floor((position - LeadingPadding) / stride);
            // 間隔の中（行の終わりの後）なら次の行
            if (i >= 0 && i < Count && position > EndOf(i)) i++;
            return Math.Clamp(i, 0, Count - 1);
        }
        // 行ごと: 先頭の位置が position 以下の最後の行（二分探索）
        int lo = 0, hi = Count - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (_starts[mid] <= position) lo = mid; else hi = mid - 1;
        }
        return position > EndOf(lo) && lo < Count - 1 ? lo + 1 : lo;
    }

    /// <summary>
    /// 見える行の範囲: 窓 [<paramref name="scrollPosition"/>, + <paramref name="viewportExtent"/>] を前後に
    /// <paramref name="cacheExtent"/> だけ広げた範囲と交わる行。
    /// </summary>
    public ListRange VisibleRange(float scrollPosition, float viewportExtent, float cacheExtent)
    {
        if (Count == 0 || !float.IsFinite(scrollPosition) || !float.IsFinite(viewportExtent)) return ListRange.Empty;
        float cache = Sanitize(cacheExtent);
        float start = scrollPosition - cache;
        float end = scrollPosition + Math.Max(0f, viewportExtent) + cache;
        if (end < 0f || start > TotalExtent) return ListRange.Empty;
        int first = IndexAt(Math.Max(start, 0f));
        // 最初の行が範囲の手前で終わっていれば次へ（IndexAt は間隔の中を次の行へ寄せているので、ここは行の終わりだけ見る）
        if (EndOf(first) < start && first < Count - 1) first++;
        int last = IndexAt(end);
        if (OffsetOf(last) > end && last > first) last--;
        return last < first ? ListRange.Empty : new ListRange(first, last);
    }

    /// <summary>
    /// 行を窓に見せるスクロールの位置（揃えは 0 = 行の先頭を窓の先頭へ・0.5 = 中央・1 = 行の終わりを窓の終わりへ）。
    /// 範囲 0〜(全体 − 窓) へ収める。
    /// </summary>
    public float ScrollPositionFor(int index, float viewportExtent, float alignment = 0f)
    {
        if (Count == 0) return 0f;
        int i = Math.Clamp(index, 0, Count - 1);
        float a = Math.Clamp(alignment, 0f, 1f);
        float target = OffsetOf(i) - (viewportExtent - ExtentOf(i)) * a;
        float max = Math.Max(0f, TotalExtent - viewportExtent);
        return Math.Clamp(target, 0f, max);
    }
}
