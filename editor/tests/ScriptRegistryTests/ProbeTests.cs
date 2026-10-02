using SEED.Scripting;
using SpriteRigTests; // テストランナー（TestHarness / Check）を共有する

namespace ScriptRegistryTests;

/// <summary>
/// まだ OnStart を迎えていないスクリプト（持ち主が未定の記録）をランタイムのスロットへ問い合わせる口のテスト
/// （エンジンでは ScriptHost.TryResolveScriptInstance。偽のスロット FakeRuntimeSlots で代える）。
/// </summary>
public static class ProbeTests
{
    /// <summary>テストを登録する。</summary>
    /// <param name="h">テストランナー。</param>
    public static void Register(TestHarness h)
    {
        h.Add("問い合わせの口: まだ OnStart を迎えていないスクリプトもアクタのスロットから引ける（全体の一覧には載らない）", () =>
        {
            var r = TestKit.NewRegistry();
            var slots = new FakeRuntimeSlots();
            // アクタ A: [mover（OnStart 済み）, fish（まだ。同じフレームで後から OnStart を迎える）]
            var mover = TestKit.Started(r, new FakeEnemy("mover"), TestKit.ActorA);
            slots.Add(TestKit.ActorA, mover);
            var fish = new FakeFish("fish");
            r.NoteCreated(fish);
            slots.Add(TestKit.ActorA, fish);

            TestKit.Same(fish, r.FirstOnOwner<FakeFish>(TestKit.ActorA, slots.Probe), "OnStart 前の fish を引ける");
            Check.Equal("mover,fish", TestKit.Names(r.AllOnOwner<FakeScript>(TestKit.ActorA, slots.Probe)), "スロットの順に混ざる");
            Check.Equal(0, r.AllInstances<FakeFish>().Length, "全体の一覧には OnStart を迎えるまで載らない");
            TestKit.Same(null, r.FirstOnOwner<FakeFish>(TestKit.ActorA, null), "口が無ければ引けない");

            // OnStart を迎えたら（持ち主が決まったら）全体の一覧にも載り、問い合わせは要らなくなる
            r.Bind(fish, TestKit.ActorA);
            slots.Calls.Clear();
            TestKit.Same(fish, r.FirstOnOwner<FakeFish>(TestKit.ActorA, slots.Probe), "OnStart 後も引ける");
            Check.Equal(0, slots.Calls.Count, "未定の記録が無ければ問い合わせない");
            Check.Equal("fish", TestKit.Names(r.AllInstances<FakeFish>()), "全体の一覧に載る");
        });

        h.Add("問い合わせの口: OnStart 前のものがスロットの先頭側に居れば、OnStart 済みのものより先に返す（スロットの順）", () =>
        {
            var r = TestKit.NewRegistry();
            var slots = new FakeRuntimeSlots();
            var early = new FakeBoss("early");     // スロット 0（非アクティブで一度も動いていない等）
            var late = new FakeEnemy("late");      // スロット 1（OnStart 済み）
            r.NoteCreated(early);
            r.NoteCreated(late);
            r.Bind(late, TestKit.ActorA);
            slots.Add(TestKit.ActorA, early);
            slots.Add(TestKit.ActorA, late);
            TestKit.Same(early, r.FirstOnOwner<FakeEnemy>(TestKit.ActorA, slots.Probe), "スロットの先頭側の early");
            Check.Equal("early,late", TestKit.Names(r.AllOnOwner<FakeEnemy>(TestKit.ActorA, slots.Probe)), "スロットの順");
        });

        h.Add("問い合わせの口: 問い合わせるのは未定の記録の型のうち T に当たるものだけ（候補が無ければ呼ばない）", () =>
        {
            var r = TestKit.NewRegistry();
            var slots = new FakeRuntimeSlots();
            TestKit.Started(r, new FakeEnemy("enemy"), TestKit.ActorA);
            r.FirstOnOwner<FakeEnemy>(TestKit.ActorA, slots.Probe);
            Check.Equal(0, slots.Calls.Count, "未定の記録が無ければ呼ばない");

            // 未定の FakeFish が 2 つ（どこかのアクタ）・FakeBoss が 1 つ
            r.NoteCreated(new FakeFish("f1"));
            r.NoteCreated(new FakeFish("f2"));
            r.NoteCreated(new FakeBoss("b1"));
            slots.Calls.Clear();
            r.FirstOnOwner<FakeFish>(TestKit.ActorA, slots.Probe);
            Check.Equal("1:0/FakeFish", string.Join(";", slots.Calls), "FakeFish の型だけ 1 回（同じ型は 1 回）");
            slots.Calls.Clear();
            r.FirstOnOwner<FakeEnemy>(TestKit.ActorA, slots.Probe);
            Check.Equal("1:0/FakeBoss", string.Join(";", slots.Calls), "基底で引くと派生の FakeBoss を問い合わせる（FakeFish は問い合わせない）");
            slots.Calls.Clear();
            r.AllOnOwner<IFakeComponent>(TestKit.ActorA, slots.Probe);
            Check.Equal(2, slots.Calls.Count, "全部で引くと未定の型ごとに 1 回（FakeFish・FakeBoss）");
        });

        h.Add("問い合わせの口: 別のアクタのスロットに居る未定のものは引かない", () =>
        {
            var r = TestKit.NewRegistry();
            var slots = new FakeRuntimeSlots();
            var fish = new FakeFish("fishOnB");
            r.NoteCreated(fish);
            slots.Add(TestKit.ActorB, fish);
            TestKit.Same(null, r.FirstOnOwner<FakeFish>(TestKit.ActorA, slots.Probe), "アクタ A には居ない");
            TestKit.Same(fish, r.FirstOnOwner<FakeFish>(TestKit.ActorB, slots.Probe), "アクタ B には居る");
        });

        h.Add("問い合わせの口（制限）: 同じ型の名前が 1 つのアクタに複数あると、先頭が OnStart 済みなら 2 つ目の未定は引けない", () =>
        {
            var r = TestKit.NewRegistry();
            var slots = new FakeRuntimeSlots();
            var startedFish = TestKit.Started(r, new FakeFish("started"), TestKit.ActorA);
            var pendingFish = new FakeFish("pending");
            r.NoteCreated(pendingFish);
            slots.Add(TestKit.ActorA, startedFish);
            slots.Add(TestKit.ActorA, pendingFish);
            // ランタイムは型の名前で先頭のスロット（started）を返す → 持ち主の決まったものは二重に数えない
            Check.Equal("started", TestKit.Names(r.AllOnOwner<FakeFish>(TestKit.ActorA, slots.Probe)), "2 つ目は OnStart まで見えない");
            r.Bind(pendingFish, TestKit.ActorA);
            Check.Equal("started,pending", TestKit.Names(r.AllOnOwner<FakeFish>(TestKit.ActorA, slots.Probe)), "OnStart の後は両方");
        });

        h.Add("問い合わせの口: 登録簿の知らないもの・名前だけ同じ別の型は数えず、同じものを二重に数えない", () =>
        {
            var r = TestKit.NewRegistry();
            var slots = new FakeRuntimeSlots();
            // 候補の型を作るための未定の FakeFish（アクタ B）
            var realFish = new FakeFish("real");
            r.NoteCreated(realFish);
            slots.Add(TestKit.ActorB, realFish);

            // アクタ A のスロットには、登録簿の知らない FakeFish（読み直しの前に作られたもの等）だけ
            slots.Add(TestKit.ActorA, new FakeFish("ghost"));
            TestKit.Same(null, r.FirstOnOwner<FakeFish>(TestKit.ActorA, slots.Probe), "知らないものは数えない");

            // 名前だけ同じ別の型（Other.FakeFish。未定）だけをスロットに持つアクタ C
            var actorC = new ScriptOwnerKey(3, 0);
            var otherFish = new Other.FakeFish("otherFish");
            r.NoteCreated(otherFish);
            slots.Add(actorC, otherFish);
            TestKit.Same(null, r.FirstOnOwner<FakeFish>(actorC, slots.Probe), "名前だけ同じ別の型は ScriptRegistryTests.FakeFish に当たらない");
            // 基底で引くと、FakeFish と Other.FakeFish の 2 つの候補の型が同じ otherFish を返すが、1 回だけ数える
            Check.Equal("otherFish", TestKit.Names(r.AllOnOwner<FakeScript>(actorC, slots.Probe)), "二重に数えない");
        });

        h.Add("問い合わせの口: 問い合わせの途中で登録簿が変わっても壊れない（候補の型を先に写す）", () =>
        {
            var r = TestKit.NewRegistry();
            var slots = new FakeRuntimeSlots();
            var fish = new FakeFish("fish");
            var boss = new FakeBoss("boss");
            r.NoteCreated(fish);
            r.NoteCreated(boss);
            slots.Add(TestKit.ActorA, fish);
            slots.Add(TestKit.ActorA, boss);
            // 1 回目の問い合わせで boss を外す（問い合わせ中の破棄に当たる）
            bool removed = false;
            ScriptSlotProbe<IFakeComponent> probe = (owner, type) =>
            {
                if (!removed) { removed = true; r.Remove(boss); }
                return slots.Probe(owner, type);
            };
            var found = r.AllOnOwner<FakeScript>(TestKit.ActorA, probe);
            Check.Equal("fish", TestKit.Names(found), "外れた boss は数えない・例外にならない");
            Check.Equal(1, r.UnboundTypeCount, "残る未定の型は FakeFish だけ");
        });
    }
}
