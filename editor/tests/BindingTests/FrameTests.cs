using System;
using System.Collections.Generic;
using SEED.Binding;
using SpriteRigTests; // テストランナー（TestHarness / Check）を共有する

namespace BindingTests;

/// <summary>
/// フレームの区切りのテスト: Bind.Deferred（1 フレームのまとめ）・BindingFrame（仕事の順・続ける・例外・区切りの途中で積んだ仕事）・
/// DisposableBag（スクリプトの破棄でまとめて外す袋）。docs/ui_binding.md §5・§6。
/// </summary>
public static class FrameTests
{
    /// <summary>テストを登録する。</summary>
    /// <param name="h">テストランナー。</param>
    public static void Register(TestHarness h)
    {
        // ── Bind.Deferred ─────────────────────────────────────

        h.Add("Bind.Deferred: 1 フレームに何度変えても区切りまでは知らせず、区切りで最新の値を 1 回だけ", () =>
        {
            BindingFrame.ResetForReload();
            var score = new Observable<int>(0);
            var deferred = Bind.Deferred(score);
            var seen = new List<int>();
            deferred.Subscribe(seen.Add);
            for (int i = 1; i <= 5; i++) score.Value = i;
            Check.Equal(0, seen.Count, "区切りまでは知らせない");
            Check.Equal(0, deferred.Value, "区切りまでの Value は最後に知らせた値");
            Check.Equal(1, BindingFrame.PendingCount, "区切りの仕事は 1 つだけ積む");
            BindingFrame.Tick();
            Check.Equal("5", string.Join(",", seen), "最新の値を 1 回");
            Check.Equal(5, deferred.Value, "知らせた後の Value");
            BindingFrame.Tick();
            Check.Equal(1, seen.Count, "変化の無いフレームは知らせない");
        });

        h.Add("Bind.Deferred: 結び付けへ渡すと、作った時点で 1 回・区切りで 1 回だけ書く", () =>
        {
            BindingFrame.ResetForReload();
            var hp = new Observable<int>(100);
            var label = new FakeTarget<string>();
            var binding = Bind.OneWay(label, Bind.Deferred(hp), v => $"HP {v}");
            hp.Value = 90;
            hp.Value = 80;
            hp.Value = 70;
            Check.Equal("HP 100", string.Join("|", label.Writes), "区切りまでは書かない");
            BindingFrame.Tick();
            Check.Equal("HP 100|HP 70", string.Join("|", label.Writes), "区切りで最新の値を 1 回");
            binding.Dispose();
            Check.Equal(0, hp.SubscriberCount, "結び付けを外すと元の購読も外れる（作って渡して捨ててよい）");
        });

        h.Add("Bind.Deferred: 区切りの前に購読が全部外れたら何もしない・購読が無い間の Value は元の今の値", () =>
        {
            BindingFrame.ResetForReload();
            var source = new Observable<int>(1);
            var deferred = Bind.Deferred(source);
            Check.Equal(1, deferred.Value, "購読が無い間は元の値");
            int calls = 0;
            var subscription = deferred.Subscribe(_ => calls++);
            source.Value = 2;
            subscription.Dispose();
            BindingFrame.Tick();
            Check.Equal(0, calls, "外れた後の区切りでは呼ばない");
            Check.Equal(0, source.SubscriberCount, "元の購読も外れる");
            source.Value = 3;
            Check.Equal(3, deferred.Value, "購読が無い間は元の今の値");
        });

        h.Add("Bind.Deferred: 元の値が戻っていても、そのフレームに変化があれば区切りで 1 回知らせる（書くのは最新の値）", () =>
        {
            BindingFrame.ResetForReload();
            var source = new Observable<int>(1);
            var deferred = Bind.Deferred(source);
            var seen = new List<int>();
            deferred.Subscribe(seen.Add);
            source.Value = 2;
            source.Value = 1;
            BindingFrame.Tick();
            Check.Equal("1", string.Join(",", seen), "1 回（最新の値）");
        });

        // ── BindingFrame ──────────────────────────────────────

        h.Add("BindingFrame: 積んだ順に呼ぶ・続ける仕事は次の区切りでも呼ぶ・例外の仕事は捨てて他は続ける", () =>
        {
            BindingFrame.ResetForReload();
            using var log = new LogCapture();
            var keep = new CountingTask { KeepFor = 2 };
            var broken = new CountingTask { Throw = true };
            var once = new CountingTask();
            BindingFrame.Enqueue(keep);
            BindingFrame.Enqueue(broken);
            BindingFrame.Enqueue(once);
            BindingFrame.Tick();
            Check.Equal(1, keep.RunCount, "続ける仕事 1 回目");
            Check.Equal(1, broken.RunCount, "例外の仕事も 1 回は呼ぶ");
            Check.Equal(1, once.RunCount, "例外の後ろの仕事も呼ぶ");
            Check.Equal(1, log.Count, "エラーログ 1 件");
            BindingFrame.Tick();
            BindingFrame.Tick();
            BindingFrame.Tick();
            Check.Equal(3, keep.RunCount, "続けた分だけ呼ぶ（2 回続けて 3 回目で終わり）");
            Check.Equal(1, broken.RunCount, "例外の仕事は捨てる");
            Check.Equal(0, BindingFrame.PendingCount, "空");
        });

        h.Add("BindingFrame: 区切りの途中で積んだ仕事は次の区切りで呼ぶ（Deferred の連鎖も 1 フレームずつ進む）", () =>
        {
            BindingFrame.ResetForReload();
            var first = new Observable<int>(0);
            var second = new Observable<int>(0);
            // first のまとめが届いたら second を変える（second のまとめは次の区切り）
            Bind.Deferred(first).Subscribe(v => second.Value = v * 10);
            var seen = new List<int>();
            Bind.Deferred(second).Subscribe(seen.Add);
            first.Value = 1;
            BindingFrame.Tick();
            Check.Equal(0, seen.Count, "同じ区切りでは回り続けない");
            BindingFrame.Tick();
            Check.Equal("10", string.Join(",", seen), "次の区切りで届く");
        });

        h.Add("BindingFrame: ResetForReload で待っている仕事を全部捨てる（スクリプトの読み直し）", () =>
        {
            BindingFrame.ResetForReload();
            var task = new CountingTask();
            BindingFrame.Enqueue(task);
            BindingFrame.ResetForReload();
            BindingFrame.Tick();
            Check.Equal(0, task.RunCount, "捨てた仕事は呼ばない");
        });

        // ── DisposableBag ─────────────────────────────────────

        h.Add("DisposableBag: Dispose で預かった物を全部・預かった順に外す（二重は無害・例外は隔離）", () =>
        {
            using var log = new LogCapture();
            var bag = new DisposableBag();
            var order = new List<string>();
            bag.Add(new DisposableAction(() => order.Add("a")));
            bag.Add(new DisposableAction(() => throw new InvalidOperationException("壊れた Dispose")));
            bag.Add(new DisposableAction(() => order.Add("c")));
            bag.Add(null);
            Check.Equal(3, bag.Count, "null は預からない");
            bag.Dispose();
            bag.Dispose();
            Check.Equal("a,c", string.Join(",", order), "預かった順・例外の後ろも外す");
            Check.Equal(1, log.Count, "エラーログ 1 件");
            Check.True(bag.IsDisposed, "Dispose 済み");
        });

        h.Add("DisposableBag: Dispose の後に預けた物はその場で外す（スクリプトの破棄の後に結び付けを作っても漏れない）", () =>
        {
            var bag = new DisposableBag();
            bag.Dispose();
            var late = new CountingDisposable();
            bag.Add(late);
            Check.Equal(1, late.DisposeCount, "その場で外す");
            Check.Equal(0, bag.Count, "預からない");
        });

        h.Add("DisposableBag: 自分で外れた結び付けは袋が大きくなったときに捨てる（長生きの持ち主で膨らまない）", () =>
        {
            BindingFrame.ResetForReload();
            var bag = new DisposableBag();
            var value = new Observable<int>();
            const int bindings = 1000;
            for (int i = 0; i < bindings; i++)
            {
                var binding = Bind.To(value, _ => { });
                bag.Add(binding);
                binding.Dispose();   // すぐ外れる（当てる先が消えたのと同じ）
            }
            Check.True(bag.Count < bindings / 10, $"外れた分を捨てる（残り {bag.Count}）");
            var alive = new CountingDisposable();
            bag.Add(alive);
            bag.Dispose();
            Check.Equal(1, alive.DisposeCount, "生きている物は残っていて外れる");
        });
    }
}
