using System;
using System.Collections.Generic;
using System.Linq;
using SEED.Binding;
using SpriteRigTests; // テストランナー（TestHarness / Check）を共有する

namespace BindingTests;

/// <summary>
/// ObservableList（観測できる一覧）のテスト: ListChange の種類と位置と項目・知らせない場合・範囲の外・
/// 再入の順序（全員が起きた順に受け取る）と上限・Dispose・列挙。docs/ui_binding.md §3.3・§5。
/// </summary>
public static class ObservableListTests
{
    /// <summary>テストを登録する。</summary>
    /// <param name="h">テストランナー。</param>
    public static void Register(TestHarness h)
    {
        h.Add("ObservableList: 変化の種類・位置・項目（Add・Insert・this[i]・Move・RemoveAt・Remove・ReplaceAll・Clear）", () =>
        {
            var list = new ObservableList<string>();
            var changes = new List<ListChange<string>>();
            list.Subscribe(changes.Add);

            list.Add("a");
            list.Add("b");
            list.Insert(0, "z");          // z a b
            list[1] = "A";                // z A b
            list.Move(2, 0);              // b z A
            list.RemoveAt(1);             // b A
            Check.True(list.Remove("A"), "あれば外して true");  // b
            list.ReplaceAll(new[] { "x", "y" });
            list.Clear();

            Check.Equal("Insert@0,Insert@1,Insert@0,Replace@1,Move 2→0,Remove@1,Remove@1,Reset,Reset",
                string.Join(",", changes), "種類と位置（起きた順）");
            Check.Equal("a", changes[0].Item, "Insert の項目");
            Check.Equal("A", changes[3].Item, "Replace の新しい項目");
            Check.Equal("a", changes[3].OldItem, "Replace の前の項目");
            Check.Equal("b", changes[4].Item, "Move の項目");
            Check.Equal(2, changes[4].OldIndex, "Move の元の位置");
            Check.Equal("z", changes[5].OldItem, "Remove の外した項目");
            Check.Equal(ListChange<string>.NoIndex, changes[7].Index, "Reset は位置を持たない");
            Check.Equal(0, list.Count, "空");
        });

        h.Add("ObservableList: 知らせない場合（空の Clear・同じ位置の Move・無い項目の Remove）と、同じ項目の置き換えは知らせる", () =>
        {
            var list = new ObservableList<int>(new[] { 1, 2, 3 });
            var changes = new List<ListChange<int>>();
            list.Subscribe(changes.Add);
            list.Move(1, 1);
            Check.True(!list.Remove(9), "無い項目は false");
            Check.Equal(0, changes.Count, "知らせない");
            list[0] = 1;
            Check.Equal("Replace@0", string.Join(",", changes), "同じ項目の置き換えは知らせる（行の描き直しの合図）");
            list.Clear();
            list.Clear();
            Check.Equal(2, changes.Count, "空の Clear は知らせない");
        });

        h.Add("ObservableList: 範囲の外は List<T> と同じ例外で、知らせず一覧も変えない", () =>
        {
            var list = new ObservableList<int>(new[] { 1, 2 });
            int calls = 0;
            list.Subscribe(_ => calls++);
            int thrown = 0;
            void Expect(Action action)
            {
                try { action(); }
                catch (ArgumentOutOfRangeException) { thrown++; }
            }
            Expect(() => list.Insert(3, 9));
            Expect(() => list.RemoveAt(2));
            Expect(() => list[-1] = 0);
            Expect(() => list.Move(0, 2));
            Expect(() => list.Move(-1, 0));
            Check.Equal(5, thrown, "5 つとも例外");
            Check.Equal(0, calls, "知らせない");
            Check.Equal("1,2", string.Join(",", list), "一覧は変わらない");
        });

        h.Add("ObservableList の再入: 購読の中の変更は今の変化を全員へ配り終えてから、起きた順に配る", () =>
        {
            using var log = new LogCapture();
            var list = new ObservableList<int>();
            var order = new List<string>();
            // A は 0 を入れられたら末尾へ 100 を足す（購読の中で一覧を変える）
            list.Subscribe(change =>
            {
                order.Add($"A:{change}");
                if (change.Kind == ListChangeKind.Insert && change.Item == 0) list.Add(100);
            });
            // B は変化を写しへ当てる（順が狂うと写しが壊れる）
            var mirror = new List<int>();
            list.Subscribe(change =>
            {
                order.Add($"B:{change}");
                if (change.Kind == ListChangeKind.Insert) mirror.Insert(change.Index, change.Item);
            });
            list.Add(0);
            Check.Equal("A:Insert@0,B:Insert@0,A:Insert@1,B:Insert@1", string.Join(",", order), "起きた順");
            Check.Equal("0,100", string.Join(",", list), "一覧");
            Check.Equal("0,100", string.Join(",", mirror), "写しが一覧と揃う");
            Check.Equal(0, log.Count, "1 段は警告しない");
        });

        h.Add($"ObservableList の再入の上限: {BindingLimits.MaxReentrantDepth} 段を超えた変化は一覧に入るが知らせず、警告は 1 回だけ", () =>
        {
            using var log = new LogCapture();
            var list = new ObservableList<int>();
            int calls = 0;
            // 知らせのたびに 1 つ足す購読（無限の連鎖）
            list.Subscribe(_ =>
            {
                calls++;
                list.Add(calls);
            });
            list.Add(0);
            Check.Equal(BindingLimits.LastRound, calls, "上限の段まで配る");
            Check.Equal(BindingLimits.LastRound + 1, list.Count, "一覧には入っている");
            Check.Equal(1, log.Count, "警告 1 件");
            list.Add(-1);
            Check.Equal(1, log.Count, "同じ一覧の警告は 1 回だけ");
        });

        h.Add("ObservableList: Dispose で止まる・列挙・IndexOf・Contains・写して作る", () =>
        {
            var source = new[] { 3, 1, 2 };
            var list = new ObservableList<int>(source);
            int calls = 0;
            var subscription = list.Subscribe(_ => calls++);
            Check.Equal(1, list.SubscriberCount, "購読 1");
            subscription.Dispose();
            list.Add(4);
            Check.Equal(0, calls, "Dispose の後は呼ばない");
            Check.Equal(0, list.SubscriberCount, "購読 0");
            Check.Equal("3,1,2,4", string.Join(",", list.Select(x => x)), "列挙（IEnumerable）");
            int sum = 0;
            foreach (int x in list) sum += x;
            Check.Equal(10, sum, "foreach");
            Check.Equal(1, list.IndexOf(1), "IndexOf");
            Check.True(list.Contains(4) && !list.Contains(9), "Contains");
            source[0] = 99;
            Check.Equal(3, list[0], "作るときに写す（元の配列とは別）");

            bool threw = false;
            try
            {
                foreach (int x in list) list.Add(x);
            }
            catch (InvalidOperationException)
            {
                threw = true;
            }
            Check.True(threw, "列挙の途中で変えると InvalidOperationException（List<T> と同じ）");
        });

        h.Add("ObservableList: ReplaceAll は自分自身の列挙を渡しても壊れない", () =>
        {
            var list = new ObservableList<int>(new[] { 1, 2 });
            list.ReplaceAll(list.Where(x => x > 1));
            Check.Equal("2", string.Join(",", list), "写してから入れ替える");
        });
    }
}
