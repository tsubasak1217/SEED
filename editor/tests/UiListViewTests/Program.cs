using System;
using System.Collections.Generic;
using SEED.UI;
using SpriteRigTests; // テストランナー（TestHarness / Check）を共有する

namespace UiListViewTests;

/// <summary>
/// SEED.UI の一覧とスワイプの操作の純粋な計算のテスト（W2-3。docs/ui_scroll_list.md §6・§7）。
/// </summary>
public static class Program
{
    /// <summary>浮動小数の比較の許容量。</summary>
    private const double Eps = 1e-4;

    public static int Main()
    {
        var h = new TestHarness();

        // ── 並び（固定の長さ）──────────────────────────────────
        h.Add("固定の長さ: 先頭の位置・終わり・全体の長さ（間隔・余白を含む）", () =>
        {
            var l = ListViewLayout.Fixed(1000, 72f, spacing: 8f, leadingPadding: 16f, trailingPadding: 24f);
            Check.Close(16, l.OffsetOf(0), Eps, "行 0 の先頭");
            Check.Close(16 + 80 * 10, l.OffsetOf(10), Eps, "行 10 の先頭");
            Check.Close(16 + 80 * 10 + 72, l.EndOf(10), Eps, "行 10 の終わり");
            Check.Close(16 + 1000 * 72 + 999 * 8 + 24, l.TotalExtent, Eps, "全体");
        });

        h.Add("固定の長さ: 位置から行（間隔の中は次の行・範囲の外は端の行）", () =>
        {
            var l = ListViewLayout.Fixed(100, 50f, spacing: 10f);
            Check.Equal(0, l.IndexAt(-30f), "手前は行 0");
            Check.Equal(0, l.IndexAt(49f), "行 0 の中");
            Check.Equal(1, l.IndexAt(55f), "行 0 と 1 の間隔の中は行 1");
            Check.Equal(1, l.IndexAt(60f), "行 1 の先頭");
            Check.Equal(99, l.IndexAt(1e9f), "先は最後の行");
        });

        h.Add("見える行の範囲: 窓 + 前後の余白と交わる行（固定の長さ・1,000 行）", () =>
        {
            var l = ListViewLayout.Fixed(1000, 40f);
            var r = l.VisibleRange(0f, 400f, 250f);
            Check.Equal(0, r.First, "先頭");
            Check.Equal(16, r.Last, "650 まで（行 16 は 640..680）");
            r = l.VisibleRange(20000f, 400f, 250f);
            Check.Equal(493, r.First, "19750 から（行 493 は 19720..19760）");
            Check.Equal(516, r.Last, "20650 まで（行 516 は 20640..20680）");
            r = l.VisibleRange(0f, 400f, 0f);
            Check.Equal(10, r.Last, "余白 0 なら 400 まで（行 10 の上端 400 は接する）");
            Check.True(l.VisibleRange(1e7f, 400f, 250f).IsEmpty, "中身の先は空");
            Check.True(ListViewLayout.Fixed(0, 40f).VisibleRange(0f, 400f, 250f).IsEmpty, "行 0 は空");
        });

        // ── 並び（行ごとの長さ）──────────────────────────────────
        h.Add("行ごとの長さ: 累積の先頭・二分探索の位置から行・見える範囲", () =>
        {
            // 偶数の行 30・奇数の行 90（間隔 0）
            var l = ListViewLayout.Variable(100, i => i % 2 == 0 ? 30f : 90f);
            Check.Close(0, l.OffsetOf(0), Eps, "行 0");
            Check.Close(30, l.OffsetOf(1), Eps, "行 1");
            Check.Close(120, l.OffsetOf(2), Eps, "行 2");
            Check.Close(50 * 120, l.TotalExtent, Eps, "全体");
            Check.Equal(1, l.IndexAt(100f), "30..120 は行 1");
            Check.Equal(2, l.IndexAt(120f), "120 は行 2");
            Check.Equal(5, l.IndexAt(300f), "270..360 は行 5");
            var r = l.VisibleRange(240f, 100f, 0f);
            Check.Equal(4, r.First, "行 4 は 240..270");
            Check.Equal(5, r.Last, "340 は行 5（270..360）の中");
            // 固定の長さと同じ長さを行ごとで渡せば、範囲も同じ
            var f = ListViewLayout.Fixed(500, 44f, spacing: 4f);
            var v = ListViewLayout.Variable(500, _ => 44f, spacing: 4f);
            foreach (var pos in new[] { 0f, 123f, 5000f, 23000f })
            {
                var a = f.VisibleRange(pos, 600f, 250f);
                var b = v.VisibleRange(pos, 600f, 250f);
                Check.Equal(a.First, b.First, $"位置 {pos} の先頭");
                Check.Equal(a.Last, b.Last, $"位置 {pos} の最後");
            }
        });

        h.Add("行を窓に見せる位置（揃え 0・0.5・1、範囲へ収める）", () =>
        {
            var l = ListViewLayout.Fixed(100, 50f);
            Check.Close(500, l.ScrollPositionFor(10, 300f, 0f), Eps, "先頭");
            Check.Close(500 - 125, l.ScrollPositionFor(10, 300f, 0.5f), Eps, "中央");
            Check.Close(500 - 250, l.ScrollPositionFor(10, 300f, 1f), Eps, "終わり");
            Check.Close(0, l.ScrollPositionFor(0, 300f, 1f), Eps, "0 より手前へは行かない");
            Check.Close(5000 - 300, l.ScrollPositionFor(99, 300f, 0f), Eps, "最大（全体 − 窓）で止まる");
        });

        // ── 使い回し ─────────────────────────────────────────────
        h.Add("使い回し: スロットが無ければ足りない行を返し、足したら割り当てる", () =>
        {
            var r = new ListViewRecycler();
            var result = r.Assign(new ListRange(0, 4));
            Check.Equal(5, result.Missing.Count, "5 行ぶん足りない");
            for (int i = 0; i < 5; i++) r.AddSlot();
            result = r.Assign(new ListRange(0, 4));
            Check.Equal(5, result.Bound.Count, "5 行が付く");
            Check.Equal(0, result.Missing.Count, "足りている");
            Check.True(result.Bound.TrueForAll(b => b.PreviousIndex == -1), "前の行は無い");
            result = r.Assign(new ListRange(0, 4));
            Check.Equal(0, result.Bound.Count + result.Released.Count, "変わらなければ何もしない");
        });

        h.Add("使い回し: スクロールで外れた行のスロットを先に使い、付いたままの行は動かさない", () =>
        {
            var r = new ListViewRecycler();
            for (int i = 0; i < 6; i++) r.AddSlot();
            r.Assign(new ListRange(0, 5));
            int slotOf3 = r.SlotOfIndex(3);
            var result = r.Assign(new ListRange(2, 7));
            Check.Equal(slotOf3, r.SlotOfIndex(3), "行 3 のスロットは変わらない");
            Check.Equal(2, result.Bound.Count, "行 6・7 が付く");
            Check.True(result.Bound.TrueForAll(b => b.PreviousIndex is 0 or 1), "外れた行 0・1 のスロットを使う");
            Check.Equal(0, result.Released.Count, "空いたままのスロットは無い");
            // 範囲が縮む: 外れた行のスロットは空いたまま（隠す）
            result = r.Assign(new ListRange(4, 5));
            Check.Equal(4, result.Released.Count, "行 2・3・6・7 が外れる");
            Check.Equal(2, r.BoundCount, "付いているのは 2 行");
            var released = r.ReleaseAll();
            Check.Equal(2, released.Count, "全部外す");
            Check.Equal(0, r.BoundCount, "何も付いていない");
        });

        h.Add("使い回し: ランダムなスクロールでも 1 行 1 スロット（範囲の中の行はすべて付き、スロットは重ならない）", () =>
        {
            var rng = new Random(20260928);
            var r = new ListViewRecycler();
            int slots = 0;
            for (int step = 0; step < 2000; step++)
            {
                int first = rng.Next(0, 900);
                int count = rng.Next(1, 30);
                var range = new ListRange(first, first + count - 1);
                var result = r.Assign(range);
                // 足りない分を足して、もう一度（ListView は次のフレームで割り当てる）
                for (int i = 0; i < result.Missing.Count; i++) { r.AddSlot(); slots++; }
                if (result.Missing.Count > 0) r.Assign(range);
                var seen = new HashSet<int>();
                for (int index = range.First; index <= range.Last; index++)
                {
                    int slot = r.SlotOfIndex(index);
                    Check.True(slot >= 0, $"段 {step}: 行 {index} にスロットがある");
                    Check.True(seen.Add(slot), $"段 {step}: スロット {slot} が重ならない");
                    Check.Equal(index, r.IndexOfSlot(slot), $"段 {step}: 行 {index} の逆引き");
                }
                Check.Equal(range.Count, r.BoundCount, $"段 {step}: 範囲の外の行は付いていない");
            }
            Check.True(slots <= 30, $"スロットは最大の範囲の数まで（{slots}）");
        });

        // ── スワイプ ─────────────────────────────────────────────
        h.Add("スワイプ: ずらし量は閉じた 0 と開いた量の間", () =>
        {
            Check.Close(-30, SwipeMath.ApplyDrag(0f, -30f, -96f), Eps, "左へ 30");
            Check.Close(-96, SwipeMath.ApplyDrag(-80f, -40f, -96f), Eps, "開いた量で止まる");
            Check.Close(0, SwipeMath.ApplyDrag(-10f, 50f, -96f), Eps, "閉じた 0 より右へは行かない");
            Check.Close(40, SwipeMath.ApplyDrag(20f, 20f, 96f), Eps, "左側の操作（右へずらす）");
        });

        h.Add("スワイプ: 速さ（120 dp/秒）が勝ち、遅ければ閾値（半分）で開く・閉じる", () =>
        {
            float open = -96f;
            Check.True(SwipeMath.ShouldOpen(-10f, -500f, open, 0.5f, 120f), "左へ速い → 開く（ずらしが少なくても）");
            Check.True(!SwipeMath.ShouldOpen(-90f, 500f, open, 0.5f, 120f), "右へ速い → 閉じる（ずらしが多くても）");
            Check.True(SwipeMath.ShouldOpen(-50f, -20f, open, 0.5f, 120f), "遅い・半分を越えた → 開く");
            Check.True(!SwipeMath.ShouldOpen(-40f, -20f, open, 0.5f, 120f), "遅い・半分に届かない → 閉じる");
            Check.True(SwipeMath.ShouldOpen(-50f, 119f, open, 0.5f, 120f), "速さが閾値の手前なら位置で決める（開く）");
            Check.True(!SwipeMath.ShouldOpen(0f, 0f, 0f, 0.5f, 120f), "開く量 0 は開かない");
        });

        h.Add("スワイプ: 動きの曲線（fastOutSlowIn）は 0 → 1 で増え続け、前半が速い", () =>
        {
            Check.Close(0, SwipeMath.Ease(0f), Eps, "始め");
            Check.Close(1, SwipeMath.Ease(1f), Eps, "終わり");
            float prev = 0f;
            for (int i = 1; i <= 100; i++)
            {
                float v = SwipeMath.Ease(i / 100f);
                Check.True(v + 1e-3f >= prev, $"単調（{i}）");
                prev = v;
            }
            Check.True(SwipeMath.Ease(0.5f) > 0.7f, $"前半が速い（{SwipeMath.Ease(0.5f)}）");
        });

        return h.Run();
    }
}
