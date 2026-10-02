using System;
using System.Collections.Generic;
using SEED.Binding;
using SpriteRigTests; // テストランナー（TestHarness / Check）を共有する

namespace BindingTests;

/// <summary>
/// Computed（導いた観測値）のテスト: 依存 1〜3 個・等しい結果は知らせない・購読がある間だけ依存をつなぐ・計算の例外・重ねる。
/// docs/ui_binding.md §3.2。
/// </summary>
public static class ComputedTests
{
    /// <summary>テストを登録する。</summary>
    /// <param name="h">テストランナー。</param>
    public static void Register(TestHarness h)
    {
        h.Add("Computed: 依存 1 個。依存が変わったら計算し直して知らせる・結果が等しければ知らせない", () =>
        {
            var count = new Observable<int>(0);
            var hasAny = Computed.From(count, n => n > 0);
            var seen = new List<bool>();
            hasAny.Subscribe(seen.Add);
            Check.Equal(false, hasAny.Value, "最初の値");
            count.Value = 1;
            count.Value = 2;   // 結果は true のまま（知らせない）
            count.Value = 0;
            Check.Equal("True,False", string.Join(",", seen), "結果が変わったときだけ");
        });

        h.Add("Computed: 依存 2 個・3 個（どれが変わっても計算し直す）", () =>
        {
            var price = new Observable<int>(100);
            var qty = new Observable<int>(2);
            var total = Computed.From(price, qty, (p, n) => p * n);
            var totals = new List<int>();
            total.Subscribe(totals.Add);
            Check.Equal(200, total.Value, "2 個の最初の値");
            qty.Value = 3;
            price.Value = 50;
            Check.Equal("300,150", string.Join(",", totals), "どちらの変化でも");

            var a = new Observable<string>("a");
            var b = new Observable<string>("b");
            var c = new Observable<string>("c");
            var joined = Computed.From(a, b, c, (x, y, z) => x + y + z);
            var texts = new List<string>();
            joined.Subscribe(texts.Add);
            c.Value = "C";
            a.Value = "A";
            Check.Equal("abC,AbC", string.Join(",", texts), "3 個");
        });

        h.Add("Computed: 購読がある間だけ依存をつなぐ（無い間の Value は都度計算・最後の購読が外れたら依存も外れる）", () =>
        {
            var source = new Observable<int>(1);
            int computeCount = 0;
            var doubled = Computed.From(source, n => { computeCount++; return n * 2; });
            Check.Equal(0, source.SubscriberCount, "作っただけでは依存をつながない");
            Check.Equal(false, doubled.IsConnected, "つないでいない");
            Check.Equal(2, doubled.Value, "無い間の Value は計算した値");
            source.Value = 5;
            Check.Equal(10, doubled.Value, "無い間も今の依存から計算する");

            var subscription = doubled.Subscribe(_ => { });
            Check.Equal(1, source.SubscriberCount, "購読で依存をつなぐ");
            int before = computeCount;
            _ = doubled.Value;
            _ = doubled.Value;
            Check.Equal(before, computeCount, "つないでいる間の Value は覚えた値（計算しない）");

            subscription.Dispose();
            Check.Equal(0, source.SubscriberCount, "最後の購読が外れたら依存の購読も外れる");
            Check.Equal(false, doubled.IsConnected, "つないでいない");
        });

        h.Add("Computed: 計算の例外は前の値のままエラーログ・依存元の他の購読は止めない", () =>
        {
            using var log = new LogCapture();
            var source = new Observable<int>(1);
            var inverse = Computed.From(source, n => 10 / n);
            var seen = new List<int>();
            inverse.Subscribe(seen.Add);
            int otherCalls = 0;
            source.Subscribe(_ => otherCalls++);
            source.Value = 0;   // 0 で割る
            Check.Equal(10, inverse.Value, "前の値のまま");
            Check.Equal(0, seen.Count, "知らせない");
            Check.Equal(1, log.Count, "エラーログ 1 件");
            Check.Equal(1, otherCalls, "依存元の他の購読は呼ばれる");
            source.Value = 2;
            Check.Equal("5", string.Join(",", seen), "直れば知らせる");
        });

        h.Add("Computed: 重ねる（Computed の Computed）・結び付けが外れたら元まで外れる", () =>
        {
            var celsius = new Observable<float>(0f);
            var fahrenheit = Computed.From(celsius, c => c * 9f / 5f + 32f);
            var label = Computed.From(fahrenheit, f => $"{f:0}F");
            var texts = new List<string>();
            var subscription = label.Subscribe(texts.Add);
            Check.Equal("32F", label.Value, "最初の値");
            celsius.Value = 100f;
            Check.Equal("212F", string.Join(",", texts), "元の変化が届く");
            subscription.Dispose();
            Check.Equal(0, celsius.SubscriberCount, "重ねた分も外れる");
        });

        h.Add("Computed: 依存が null・計算が null なら ArgumentNullException", () =>
        {
            bool threw = false;
            try { Computed.From<int, int>(null!, n => n); }
            catch (ArgumentNullException) { threw = true; }
            Check.True(threw, "依存が null");
            threw = false;
            try { Computed.From<int, int>(new Observable<int>(), null!); }
            catch (ArgumentNullException) { threw = true; }
            Check.True(threw, "計算が null");
        });
    }
}
