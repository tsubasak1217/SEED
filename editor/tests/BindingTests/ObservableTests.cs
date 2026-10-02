using System;
using System.Collections.Generic;
using SEED.Binding;
using SpriteRigTests; // テストランナー（TestHarness / Check）を共有する

namespace BindingTests;

/// <summary>
/// Observable（観測できる値）のテスト: 知らせの回数と順序・等しければ知らせない・Dispose で止まる・途中の付け外し・
/// 例外の隔離・Notify・再入（配り終えてから 1 段だけ・上限を超えたら警告）。docs/ui_binding.md §3.1・§5。
/// </summary>
public static class ObservableTests
{
    /// <summary>テストを登録する。</summary>
    /// <param name="h">テストランナー。</param>
    public static void Register(TestHarness h)
    {
        h.Add("Observable: 最初の値・set で即座に知らせる（値と回数）・購読した時点では呼ばない", () =>
        {
            var count = new Observable<int>(3);
            var seen = new List<int>();
            count.Subscribe(seen.Add);
            Check.Equal(3, count.Value, "最初の値");
            Check.Equal(0, seen.Count, "購読した時点の値では呼ばない");
            count.Value = 4;
            count.Value = 7;
            Check.Equal("4,7", string.Join(",", seen), "変わるたびに即座に 1 回ずつ");
            Check.Equal(7, count.Value, "今の値");
        });

        h.Add("Observable: 等しい値は知らせない（既定の比べ方・null・比べ方の指定）", () =>
        {
            var name = new Observable<string?>("a");
            int calls = 0;
            name.Subscribe(_ => calls++);
            name.Value = "a";
            Check.Equal(0, calls, "同じ文字は知らせない");
            name.Value = null;
            name.Value = null;
            Check.Equal(1, calls, "null → null は 1 回だけ");

            var caseless = new Observable<string>("ABC", StringComparer.OrdinalIgnoreCase);
            int caselessCalls = 0;
            caseless.Subscribe(_ => caselessCalls++);
            caseless.Value = "abc";
            Check.Equal(0, caselessCalls, "比べ方を渡せばそれで等しいものは知らせない");
            Check.Equal("ABC", caseless.Value, "知らせないときは値も入れない");
        });

        h.Add("Observable: 登録の順に呼ぶ", () =>
        {
            var value = new Observable<int>();
            var order = new List<string>();
            value.Subscribe(v => order.Add($"A{v}"));
            value.Subscribe(v => order.Add($"B{v}"));
            value.Subscribe(v => order.Add($"C{v}"));
            value.Value = 1;
            Check.Equal("A1,B1,C1", string.Join(",", order), "登録の順");
        });

        h.Add("Observable: Dispose で止まる（二重の Dispose は無害・購読の数）", () =>
        {
            var value = new Observable<int>();
            int calls = 0;
            var subscription = value.Subscribe(_ => calls++);
            Check.Equal(1, value.SubscriberCount, "購読 1");
            value.Value = 1;
            subscription.Dispose();
            subscription.Dispose();
            value.Value = 2;
            Check.Equal(1, calls, "Dispose の後は呼ばない");
            Check.Equal(0, value.SubscriberCount, "購読 0");
        });

        h.Add("Observable: 配っている途中に外した購読は呼ばない・足した購読はその知らせでは呼ばない", () =>
        {
            // 例外の隔離で「外した購読を呼ぼうとして失敗した」が隠れないよう、エラーログが出ないことも確かめる
            using var log = new LogCapture();
            var value = new Observable<int>();
            var order = new List<string>();
            IDisposable? second = null;
            value.Subscribe(v =>
            {
                order.Add($"A{v}");
                second?.Dispose();                                  // まだ呼んでいない B を外す
                if (v == 1) value.Subscribe(w => order.Add($"N{w}")); // 新しい購読（この知らせでは呼ばない）
            });
            second = value.Subscribe(v => order.Add($"B{v}"));
            value.Value = 1;
            Check.Equal("A1", string.Join(",", order), "外した B は呼ばない・足した N は呼ばない");
            value.Value = 2;
            Check.Equal("A1,A2,N2", string.Join(",", order), "次の知らせから N が呼ばれる");
            Check.Equal(0, log.Count, "エラーログなし");
        });

        h.Add("Observable: 購読の例外は隔離してエラーログ 1 件・残りの購読は続ける", () =>
        {
            using var log = new LogCapture();
            var value = new Observable<int>();
            var seen = new List<int>();
            value.Subscribe(_ => throw new InvalidOperationException("壊れた購読"));
            value.Subscribe(seen.Add);
            value.Value = 5;
            Check.Equal("5", string.Join(",", seen), "後ろの購読は呼ばれる");
            Check.Equal(1, log.Count, "エラーログ 1 件");
            Check.True(log.Lines[0].Contains("壊れた購読"), "例外の中身が出る");
            value.Value = 6;
            Check.Equal("5,6", string.Join(",", seen), "次の知らせも届く（配る状態が戻っている）");
        });

        h.Add("Observable: Notify は同じ値でも知らせる（参照型の中身を書き換えたとき）", () =>
        {
            var list = new Observable<List<int>>(new List<int>());
            int calls = 0;
            list.Subscribe(_ => calls++);
            list.Value.Add(1);
            list.Value = list.Value;
            Check.Equal(0, calls, "同じ参照の set は知らせない");
            list.Notify();
            Check.Equal(1, calls, "Notify は知らせる");
        });

        h.Add("Observable: null の購読は登録せず警告し、解除済みの口を返す", () =>
        {
            using var log = new LogCapture();
            var value = new Observable<int>();
            var subscription = value.Subscribe(null!);
            Check.Equal(0, value.SubscriberCount, "登録しない");
            Check.Equal(1, log.Count, "警告 1 件");
            subscription.Dispose();
            value.Value = 1;
        });

        h.Add("Observable の再入: 購読の中の変更は配り終えてからもう 1 周（全員の最後の値が最新・古い値で上書きされない）", () =>
        {
            using var log = new LogCapture();
            var value = new Observable<int>();
            var order = new List<string>();
            // A は 10 を超えたら 10 へ戻す（購読の中で値を変える）。B・C は値を写すだけ
            value.Subscribe(v =>
            {
                order.Add($"A{v}");
                if (v > 10) value.Value = 10;
            });
            int lastB = -1, lastC = -1;
            value.Subscribe(v => { order.Add($"B{v}"); lastB = v; });
            value.Subscribe(v => { order.Add($"C{v}"); lastC = v; });
            value.Value = 15;
            Check.Equal("A15,B15,C15,A10,B10,C10", string.Join(",", order), "1 周目を配り終えてから 2 周目");
            Check.Equal(10, value.Value, "今の値");
            Check.Equal(10, lastB, "B の最後の値は最新");
            Check.Equal(10, lastC, "C の最後の値は最新");
            Check.Equal(0, log.Count, "1 段は警告しない");
        });

        h.Add("Observable の再入: 周の途中で何度変えても次の周は最新の値 1 回だけ・最後に配った値へ戻っただけなら次の周は無い", () =>
        {
            var value = new Observable<int>();
            var seen = new List<int>();
            bool once = false;
            value.Subscribe(v =>
            {
                seen.Add(v);
                if (once) return;
                once = true;
                value.Value = 2;
                value.Value = 3;   // 2 は飛ばして 3 だけ配る
            });
            value.Value = 1;
            Check.Equal("1,3", string.Join(",", seen), "まとめて最新の値だけ");

            var back = new Observable<int>();
            var backSeen = new List<int>();
            bool backOnce = false;
            back.Subscribe(v =>
            {
                backSeen.Add(v);
                if (backOnce) return;
                backOnce = true;
                back.Value = 9;
                back.Value = 1;    // 配った値 1 へ戻った
            });
            back.Value = 1;
            Check.Equal("1", string.Join(",", backSeen), "配った値へ戻っただけなら知らせ直さない");
        });

        h.Add($"Observable の再入の上限: {BindingLimits.MaxReentrantDepth} 段を超えたら値は入るが知らせず、警告は 1 回だけ", () =>
        {
            using var log = new LogCapture();
            var value = new Observable<int>();
            var seen = new List<int>();
            // 知らせのたびに +1 する購読（無限の再入）
            value.Subscribe(v =>
            {
                seen.Add(v);
                value.Value = v + 1;
            });
            value.Value = 1;
            Check.Equal(BindingLimits.LastRound, seen.Count, "上限の周まで配る");
            Check.Equal("1,2", string.Join(",", seen), "1 周目と 2 周目");
            Check.Equal(3, value.Value, "値は入っている（知らせていない）");
            Check.Equal(1, log.Count, "警告 1 件");
            Check.True(log.Lines[0].Contains("再入"), "再入の警告");

            // もう一度起こしても同じ観測値の警告は 1 回だけ
            value.Value = 100;
            Check.Equal(1, log.Count, "同じ観測値の警告は 1 回だけ");
        });

        h.Add("Observable の再入: 別の観測値をまたぐ往復も各観測値の上限で止まる", () =>
        {
            using var log = new LogCapture();
            var a = new Observable<int>();
            var b = new Observable<int>();
            int aCalls = 0, bCalls = 0;
            a.Subscribe(v => { aCalls++; b.Value = v + 1; });
            b.Subscribe(v => { bCalls++; a.Value = v + 1; });
            a.Value = 1;
            Check.True(aCalls <= BindingLimits.LastRound + 1 && bCalls <= BindingLimits.LastRound + 1, $"止まる（a {aCalls} 回・b {bCalls} 回）");
            Check.True(log.Count >= 1, "警告が出る");
        });
    }
}
