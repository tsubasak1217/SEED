using System;
using System.Collections.Generic;
using SEED.Binding;
using SpriteRigTests; // テストランナー（TestHarness / Check）を共有する

namespace BindingTests;

/// <summary>
/// 結び付け（Bind.To・Bind.OneWay・Bind.TwoWay・Bind.List）のテスト。UI 部品の代わりに偽の当てる先（IBindTarget）を差す:
/// 作った時点で当てる・変わるたびに当てる・Dispose で止まる・当てる先が消えたら外れる・用意を待つ・往復の留め金・行の並びへの写し方。
/// docs/ui_binding.md §4。
/// </summary>
public static class BindTests
{
    /// <summary>テストを登録する。</summary>
    /// <param name="h">テストランナー。</param>
    public static void Register(TestHarness h)
    {
        // ── 一方向 ─────────────────────────────────────────────

        h.Add("Bind.To: 作った時点の値で 1 回・変わるたびに呼ぶ・Dispose で止まり購読も外れる", () =>
        {
            BindingFrame.ResetForReload();
            var count = new Observable<int>(1);
            var seen = new List<int>();
            var binding = Bind.To(count, seen.Add);
            count.Value = 2;
            count.Value = 2;
            Check.Equal("1,2", string.Join(",", seen), "作った時点 ＋ 変化（等しい値は呼ばない）");
            binding.Dispose();
            binding.Dispose();
            count.Value = 3;
            Check.Equal("1,2", string.Join(",", seen), "Dispose の後は呼ばない");
            Check.Equal(0, count.SubscriberCount, "購読も外れる");
        });

        h.Add("Bind.OneWay: 変換して当てる（Bind.Text の書式と同じ形）", () =>
        {
            BindingFrame.ResetForReload();
            var money = new Observable<int>(1200);
            var label = new FakeTarget<string>();
            using var binding = Bind.OneWay(label, money, n => $"{n:N0} 円");
            money.Value = 1234567;
            Check.Equal("1,200 円|1,234,567 円", string.Join("|", label.Writes), "変換した文字");
        });

        h.Add("一方向: 当てる先が見えない（IsAlive が false）ときは書かず、区切りで確かめて本当に消えていれば外す（見えないだけなら最新の値を当てる）", () =>
        {
            BindingFrame.ResetForReload();
            var value = new Observable<bool>(true);
            var node = new FakeTarget<bool>();
            Bind.OneWay(node, value);
            node.IsAlive = false;
            value.Value = false;
            Check.Equal(1, node.Writes.Count, "見えない間は書かない（最初の 1 回だけ）");
            Check.Equal(1, value.SubscriberCount, "すぐには外さない（OnDestroy の中で変えただけかもしれない。レビュー #7）");
            Check.Equal(1, BindingFrame.PendingCount, "区切りで確かめる");
            BindingFrame.Tick();
            Check.Equal(0, value.SubscriberCount, "区切りでも消えていれば外れる");
            Check.Equal(1, node.Writes.Count, "外れたので書かない");

            // 見えないだけ（OnDestroy の中）: 区切りで見えるようになっていれば最新の値を 1 回当てる
            var text = new Observable<int>(1);
            var label = new FakeTarget<int>();
            Bind.OneWay(label, text);
            label.IsAlive = false;
            text.Value = 2;
            text.Value = 3;
            label.IsAlive = true;
            BindingFrame.Tick();
            Check.Equal("1,3", string.Join(",", label.Writes), "区切りで最新の値を 1 回だけ当てる");
            Check.Equal(1, text.SubscriberCount, "結び付けは生きている");
        });

        h.Add("一方向: 当てる先の用意を待つ（待つ間に何度変わっても、用意ができた区切りで最新の値を 1 回）", () =>
        {
            BindingFrame.ResetForReload();
            var value = new Observable<int>(1);
            var widget = new FakeTarget<int> { IsReady = false };
            Bind.OneWay(widget, value);
            value.Value = 2;
            value.Value = 3;
            Check.Equal(0, widget.Writes.Count, "用意の前は書かない");
            Check.Equal(1, BindingFrame.PendingCount, "区切りを 1 つだけ待つ（二重に積まない）");
            BindingFrame.Tick();
            Check.Equal(0, widget.Writes.Count, "まだ用意が無ければ書かない");
            Check.Equal(1, BindingFrame.PendingCount, "待ちを続ける");
            widget.IsReady = true;
            BindingFrame.Tick();
            Check.Equal("3", string.Join(",", widget.Writes), "最新の値を 1 回");
            Check.Equal(0, BindingFrame.PendingCount, "待ちは終わり");
            value.Value = 4;
            Check.Equal("3,4", string.Join(",", widget.Writes), "その後は即時");
        });

        h.Add("一方向: 待っている間に当てる先が消えたら区切りで外れる・Dispose した結び付けは区切りで何もしない", () =>
        {
            BindingFrame.ResetForReload();
            var value = new Observable<int>(1);
            var gone = new FakeTarget<int> { IsReady = false };
            Bind.OneWay(gone, value);
            gone.IsAlive = false;
            BindingFrame.Tick();
            Check.Equal(0, value.SubscriberCount, "消えたら外れる");

            var other = new Observable<int>(1);
            var waiting = new FakeTarget<int> { IsReady = false };
            var binding = Bind.OneWay(waiting, other);
            binding.Dispose();
            waiting.IsReady = true;
            BindingFrame.Tick();
            Check.Equal(0, waiting.Writes.Count, "Dispose した結び付けは書かない");
            Check.Equal(0, BindingFrame.PendingCount, "待ちも捨てる");
        });

        h.Add("一方向: Reapply は値が同じでも変換し直して当てる（言語の切り替えで文を引き直す Bind.Text の L10n 版の形）", () =>
        {
            BindingFrame.ResetForReload();
            string language = "ja";
            var coins = new Observable<int>(3);
            var label = new FakeTarget<string>();
            var binding = Bind.CreateOneWay(label, coins, n => language == "ja" ? $"{n} 枚" : $"{n} coins");
            language = "en";
            binding.Reapply();
            binding.Dispose();
            binding.Reapply();
            Check.Equal("3 枚|3 coins", string.Join("|", label.Writes), "引き直す（Dispose の後は何もしない）");
        });

        // ── 双方向 ─────────────────────────────────────────────

        h.Add("双方向: 作った時点で観測値の値を部品へ当てる（観測値が正）・知らせの口は 1 回だけ付ける", () =>
        {
            BindingFrame.ResetForReload();
            var on = new Observable<bool>(true);
            var toggle = new FakeWidget<bool> { Current = false };
            Bind.TwoWay(toggle, on);
            Check.Equal("True", string.Join(",", toggle.Writes), "観測値の値を当てる");
            Check.Equal(1, toggle.ListenCount, "口は 1 回");
            on.Value = false;
            Check.Equal("True,False", string.Join(",", toggle.Writes), "値 → 部品");
            Check.Equal(1, toggle.ListenCount, "口は増えない");
        });

        h.Add("双方向: 部品 → 値（利用者の操作は観測値と他の購読へ届き、部品へは書き戻さない＝留め金）", () =>
        {
            BindingFrame.ResetForReload();
            var volume = new Observable<float>(0.5f);
            var slider = new FakeWidget<float>();
            Bind.TwoWay(slider, volume);
            var others = new List<float>();
            volume.Subscribe(others.Add);
            slider.UserChange(0.8f);
            Check.Equal(0.8f, volume.Value, "観測値へ入る");
            Check.Equal("0.8", string.Join(",", others), "他の購読へも届く");
            Check.Equal(1, slider.Writes.Count, "部品へは書き戻さない（最初の 1 回だけ）");
        });

        h.Add("双方向: 書くと必ず知らせる部品（SelectionGroup.Select の形）でも往復しない", () =>
        {
            BindingFrame.ResetForReload();
            using var log = new LogCapture();
            var index = new Observable<int>(1);
            var group = new FakeWidget<int> { RaiseOnWrite = true };
            Bind.TwoWay(group, index);
            int sourceCalls = 0;
            index.Subscribe(_ => sourceCalls++);
            index.Value = 2;
            Check.Equal("1,2", string.Join(",", group.Writes), "値 → 部品は 1 回ずつ");
            Check.Equal(1, sourceCalls, "部品の知らせは観測値へ返らない");
            Check.Equal(0, log.Count, "警告なし（再入の上限にも当たらない）");
        });

        h.Add("双方向: 同じ観測値を 2 つの部品へ結ぶと、片方の操作がもう片方へ届く", () =>
        {
            BindingFrame.ResetForReload();
            var on = new Observable<bool>(false);
            var a = new FakeWidget<bool>();
            var b = new FakeWidget<bool>();
            Bind.TwoWay(a, on);
            Bind.TwoWay(b, on);
            a.UserChange(true);
            Check.Equal(true, on.Value, "観測値");
            Check.Equal(true, b.Current, "もう片方の部品へ届く");
            Check.Equal("False", string.Join(",", a.Writes), "操作した部品へは書き戻さない");
        });

        h.Add("双方向: 部品の用意を待つ（OnStart の前）→ 用意ができた区切りで口を付けて最新の値を当てる", () =>
        {
            BindingFrame.ResetForReload();
            var name = new Observable<string>("a");
            var field = new FakeWidget<string> { IsReady = false };
            Bind.TwoWay(field, name);
            name.Value = "ab";
            Check.Equal(0, field.ListenCount, "用意の前は口を付けない");
            Check.Equal(0, field.Writes.Count, "用意の前は書かない");
            field.IsReady = true;
            BindingFrame.Tick();
            Check.Equal(1, field.ListenCount, "口を付ける");
            Check.Equal("ab", string.Join(",", field.Writes), "最新の値を 1 回");
            field.UserChange("abc");
            Check.Equal("abc", name.Value, "その後は部品 → 値も届く");
        });

        h.Add("双方向: Dispose で部品の口も観測値の購読も外れる・部品が消えたら外れる", () =>
        {
            BindingFrame.ResetForReload();
            var on = new Observable<bool>(false);
            var check = new FakeWidget<bool>();
            var binding = Bind.TwoWay(check, on);
            binding.Dispose();
            Check.Equal(0, check.ActiveListeners, "部品の口が外れる");
            Check.Equal(0, on.SubscriberCount, "観測値の購読が外れる");
            check.UserChange(true);
            Check.Equal(false, on.Value, "外した後の操作は届かない");

            var other = new Observable<bool>(false);
            var gone = new FakeWidget<bool>();
            Bind.TwoWay(gone, other);
            gone.IsAlive = false;
            other.Value = true;
            Check.Equal(1, gone.Writes.Count, "見えない部品へは書かない");
            Check.Equal(1, other.SubscriberCount, "すぐには外さない（区切りで確かめる。レビュー #7）");
            BindingFrame.Tick();
            Check.Equal(0, other.SubscriberCount, "区切りでも消えていれば外れる");
            Check.Equal(0, gone.ActiveListeners, "部品の口も外れる");
        });

        // ── 一覧 ──────────────────────────────────────────────

        h.Add("一覧の結び付け: 作った時点で SetCount・入れた/外した/全体 → SetCount・動かした → Refresh・置き換えた → RebindRow", () =>
        {
            BindingFrame.ResetForReload();
            var items = new ObservableList<string>(new[] { "a", "b" });
            var rows = new FakeRows();
            var binding = Bind.List(rows, items);
            items.Add("c");
            items.RemoveAt(0);
            items[1] = "C";
            items.Move(0, 1);
            items.ReplaceAll(new[] { "x" });
            items.Clear();
            Check.Equal("SetCount(2),SetCount(3),SetCount(2),RebindRow(1),Refresh,SetCount(1),SetCount(0)",
                string.Join(",", rows.Calls), "口の写し方");
            binding.Dispose();
            items.Add("y");
            Check.Equal(7, rows.Calls.Count, "Dispose の後は呼ばない");
            Check.Equal(0, items.SubscriberCount, "購読が外れる");
        });

        h.Add("一覧の結び付け: 当てる先が消えたら外れる（作った時点で消えていれば何も呼ばない）", () =>
        {
            BindingFrame.ResetForReload();
            var items = new ObservableList<int>();
            var rows = new FakeRows();
            Bind.List(rows, items);
            rows.IsAlive = false;
            items.Add(1);
            Check.Equal("SetCount(0)", string.Join(",", rows.Calls), "見えない間は呼ばない");
            Check.Equal(1, items.SubscriberCount, "すぐには外さない（区切りで確かめる。レビュー #7）");
            BindingFrame.Tick();
            Check.Equal(0, items.SubscriberCount, "区切りでも消えていれば外れる");

            var dead = new FakeRows { IsAlive = false };
            Bind.List(dead, items);
            Check.Equal(0, dead.Calls.Count, "最初から見えなければ呼ばない");
            BindingFrame.Tick();
            Check.Equal(0, items.SubscriberCount, "区切りでも消えていれば外れる");

            // 見えないだけ（OnDestroy の中で一覧を変えた）: 区切りで見えていれば数を合わせ直す
            var alive = new FakeRows();
            var list = new ObservableList<int>();
            Bind.List(alive, list);
            alive.IsAlive = false;
            list.Add(1);
            list.Add(2);
            alive.IsAlive = true;
            BindingFrame.Tick();
            Check.Equal("SetCount(0),SetCount(2)", string.Join(",", alive.Calls), "区切りで今の数を 1 回だけ合わせる");
        });

        h.Add("結び付け: 引数が null なら ArgumentNullException", () =>
        {
            int thrown = 0;
            void Expect(Action action)
            {
                try { action(); }
                catch (ArgumentNullException) { thrown++; }
            }
            Expect(() => Bind.To(new Observable<int>(), null!));
            Expect(() => Bind.OneWay<int>(null!, new Observable<int>()));
            Expect(() => Bind.TwoWay(new FakeWidget<int>(), null!));
            Expect(() => Bind.List<int>(null!, new ObservableList<int>()));
            Expect(() => Bind.Deferred<int>(null!));
            Check.Equal(5, thrown, "5 つとも例外");
        });
    }
}
