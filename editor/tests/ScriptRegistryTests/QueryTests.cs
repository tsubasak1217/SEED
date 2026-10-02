using SpriteRigTests; // テストランナー（TestHarness / Check）を共有する

namespace ScriptRegistryTests;

/// <summary>
/// 登録簿の問い合わせのテスト（持ち主の決まったもの）: 型の派生・インターフェース・スロット順（生成の順）・
/// 全体の一覧の生成順・返す配列が写しであること（列挙の途中の破棄で壊れない）。
/// </summary>
public static class QueryTests
{
    /// <summary>テストを登録する。</summary>
    /// <param name="h">テストランナー。</param>
    public static void Register(TestHarness h)
    {
        h.Add("問い合わせ: 派生型も当たる（基底で引くと派生も返る・派生で引くと基底は返らない）", () =>
        {
            var r = TestKit.NewRegistry();
            var enemy = TestKit.Started(r, new FakeEnemy("enemy"), TestKit.ActorA);
            var boss = TestKit.Started(r, new FakeBoss("boss"), TestKit.ActorA);
            Check.Equal("enemy,boss", TestKit.Names(r.AllOnOwner<FakeEnemy>(TestKit.ActorA, null)), "基底で引くと両方");
            Check.Equal("boss", TestKit.Names(r.AllOnOwner<FakeBoss>(TestKit.ActorA, null)), "派生で引くと派生だけ");
            TestKit.Same(enemy, r.FirstOnOwner<FakeEnemy>(TestKit.ActorA, null), "基底で引いた最初");
            TestKit.Same(boss, r.FirstOnOwner<FakeBoss>(TestKit.ActorA, null), "派生で引いた最初");
            TestKit.Same(null, r.FirstOnOwner<FakeFish>(TestKit.ActorA, null), "当たらない型は null");
            Check.Equal("enemy,boss", TestKit.Names(r.AllInstances<FakeEnemy>()), "全体の一覧も基底で引くと両方");
            Check.Equal("boss", TestKit.Names(r.AllInstances<FakeBoss>()), "全体の一覧を派生で引くと派生だけ");
        });

        h.Add("問い合わせ: インターフェースでも引ける・全部（IScriptComponent 相当）で引くと SEEDScript でないものも入る", () =>
        {
            var r = TestKit.NewRegistry();
            TestKit.Started(r, new FakeFish("fish"), TestKit.ActorA);
            var crate = TestKit.Started(r, new FakeCrate("crate"), TestKit.ActorA);
            TestKit.Started(r, new FakeRawComponent("raw"), TestKit.ActorA);
            TestKit.Same(crate, r.FirstOnOwner<IFakeDamageable>(TestKit.ActorA, null), "インターフェースで引ける");
            Check.Equal("fish,crate,raw", TestKit.Names(r.AllOnOwner<IFakeComponent>(TestKit.ActorA, null)), "全部");
            Check.Equal("fish,crate", TestKit.Names(r.AllOnOwner<FakeScript>(TestKit.ActorA, null)), "SEEDScript 相当だけ");
        });

        h.Add("問い合わせ: アクタの中の並びは生成の順＝スロットの順（持ち主の決まる順＝ OnStart の順に関係なく）", () =>
        {
            var r = TestKit.NewRegistry();
            var first = new FakeEnemy("slot0");
            var second = new FakeFish("slot1");
            var third = new FakeBoss("slot2");
            r.NoteCreated(first);
            r.NoteCreated(second);
            r.NoteCreated(third);
            // OnStart の順（ランタイムの走査順）がスロットの順と違っても
            r.Bind(third, TestKit.ActorA);
            r.Bind(first, TestKit.ActorA);
            r.Bind(second, TestKit.ActorA);
            Check.Equal("slot0,slot1,slot2", TestKit.Names(r.AllOnOwner<FakeScript>(TestKit.ActorA, null)), "スロットの順");
            TestKit.Same(first, r.FirstOnOwner<FakeEnemy>(TestKit.ActorA, null), "基底で引いた最初は slot0（slot2 の派生より先）");
            // 途中を外しても順は保たれる
            r.Remove(second);
            Check.Equal("slot0,slot2", TestKit.Names(r.AllOnOwner<FakeScript>(TestKit.ActorA, null)), "途中を外した後の順");
        });

        h.Add("問い合わせ: 全体の一覧は生成順（型が混ざっても・アクタが違っても）・FirstInstance は生成順で最初", () =>
        {
            var r = TestKit.NewRegistry();
            var fish1 = new FakeFish("fish1");
            var boss1 = new FakeBoss("boss1");
            var enemy1 = new FakeEnemy("enemy1");
            var fish2 = new FakeFish("fish2");
            foreach (var s in new FakeScript[] { fish1, boss1, enemy1, fish2 }) r.NoteCreated(s);
            // 持ち主が決まる順は逆にする
            r.Bind(fish2, TestKit.ActorB);
            r.Bind(enemy1, TestKit.ActorA);
            r.Bind(boss1, TestKit.ActorB);
            r.Bind(fish1, TestKit.ActorA);
            Check.Equal("fish1,boss1,enemy1,fish2", TestKit.Names(r.AllInstances<FakeScript>()), "型が混ざっても生成順");
            Check.Equal("boss1,enemy1", TestKit.Names(r.AllInstances<FakeEnemy>()), "基底で引いた生成順");
            Check.Equal("fish1,fish2", TestKit.Names(r.AllInstances<FakeFish>()), "1 つの型の生成順");
            TestKit.Same(boss1, r.FirstInstance<FakeEnemy>(), "基底で引いた最初（派生の boss1 が enemy1 より先）");
            TestKit.Same(fish1, r.FirstInstance<FakeScript>(), "全体の最初");
        });

        h.Add("問い合わせ: 全体の一覧の配列は写し（列挙の途中で外しても壊れず、配列も変わらない）", () =>
        {
            var r = TestKit.NewRegistry();
            for (int i = 0; i < 3; i++) TestKit.Started(r, new FakeFish($"fish{i}"), TestKit.ActorA);
            var snapshot = r.AllInstances<FakeFish>();
            foreach (var fish in snapshot) r.Remove(fish); // 列挙中の破棄（OnDestroy の後に外れるのに当たる）
            Check.Equal(3, snapshot.Length, "写しは変わらない");
            Check.Equal(0, r.AllInstances<FakeFish>().Length, "登録簿からは全部外れた");
            Check.Equal(0, r.TypeCount, "空の型の一覧が残らない");
        });

        h.Add("問い合わせ: アクタの一覧の配列も写し（列挙の途中で外す・足すしても壊れない）", () =>
        {
            var r = TestKit.NewRegistry();
            for (int i = 0; i < 3; i++) TestKit.Started(r, new FakeEnemy($"enemy{i}"), TestKit.ActorA);
            var snapshot = r.AllOnOwner<FakeEnemy>(TestKit.ActorA, null);
            int added = 0;
            foreach (var enemy in snapshot)
            {
                r.Remove(enemy);
                TestKit.Started(r, new FakeEnemy($"late{added++}"), TestKit.ActorA);
            }
            Check.Equal("enemy0,enemy1,enemy2", TestKit.Names(snapshot), "写しは変わらない");
            Check.Equal("late0,late1,late2", TestKit.Names(r.AllOnOwner<FakeEnemy>(TestKit.ActorA, null)), "後から足したものだけ残る");
        });

        h.Add("問い合わせ: 返した配列を書き換えても登録簿は変わらない", () =>
        {
            var r = TestKit.NewRegistry();
            TestKit.Started(r, new FakeFish("fish"), TestKit.ActorA);
            var first = r.AllInstances<FakeFish>();
            first[0] = new FakeFish("replaced");
            var second = r.AllOnOwner<FakeFish>(TestKit.ActorA, null);
            second[0] = new FakeFish("replaced");
            Check.Equal("fish", TestKit.Names(r.AllInstances<FakeFish>()), "全体の一覧は元のまま");
            Check.Equal("fish", TestKit.Names(r.AllOnOwner<FakeFish>(TestKit.ActorA, null)), "アクタの一覧も元のまま");
        });

        h.Add("問い合わせ: 見つからないときは空の配列（null ではない）・知らないアクタ・空の登録簿", () =>
        {
            var r = TestKit.NewRegistry();
            Check.Equal(0, r.AllInstances<FakeFish>().Length, "空の登録簿の全体の一覧");
            Check.Equal(0, r.AllOnOwner<FakeFish>(TestKit.ActorA, null).Length, "空の登録簿のアクタの一覧");
            TestKit.Same(null, r.FirstInstance<FakeFish>(), "空の登録簿の FirstInstance");
            TestKit.Started(r, new FakeEnemy("enemy"), TestKit.ActorA);
            Check.Equal(0, r.AllOnOwner<FakeEnemy>(TestKit.ActorB, null).Length, "知らないアクタ");
            Check.Equal(0, r.AllInstances<FakeFish>().Length, "当たらない型");
        });
    }
}
