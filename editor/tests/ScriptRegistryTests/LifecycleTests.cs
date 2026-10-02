using SEED.Scripting;
using SpriteRigTests; // テストランナー（TestHarness / Check）を共有する

namespace ScriptRegistryTests;

/// <summary>
/// 登録簿の寿命のテスト: 生成を知らせる（CreateComponent）・持ち主を決める（ResolveReferenceFields / OnStart）・
/// 外す（OnDestroy の後・DestroyComponent）・全消去（スクリプトの読み直し）・エンティティの再利用。
/// </summary>
public static class LifecycleTests
{
    /// <summary>テストを登録する。</summary>
    /// <param name="h">テストランナー。</param>
    public static void Register(TestHarness h)
    {
        h.Add("寿命: 生成を知らせただけ（持ち主が未定＝ OnStart 前）ではアクタからも全体からも引けない・数には入る", () =>
        {
            var r = TestKit.NewRegistry();
            var enemy = new FakeEnemy("e1");
            r.NoteCreated(enemy);
            Check.Equal(1, r.Count, "生成を知らされた数");
            Check.Equal(0, r.BoundCount, "持ち主の決まった数");
            TestKit.Same(null, r.FirstOnOwner<FakeEnemy>(TestKit.ActorA, null), "問い合わせの口なしではアクタから引けない");
            Check.Equal(0, r.AllInstances<FakeEnemy>().Length, "全体の一覧に載らない");
            TestKit.Same(null, r.FirstInstance<FakeEnemy>(), "FirstInstance も null");
            Check.Equal(1, r.UnboundTypeCount, "問い合わせの候補の型として数える");
        });

        h.Add("寿命: 持ち主を決めると、そのアクタから引け・全体の一覧に載る・別のアクタからは引けない", () =>
        {
            var r = TestKit.NewRegistry();
            var enemy = TestKit.Started(r, new FakeEnemy("e1"), TestKit.ActorA);
            TestKit.Same(enemy, r.FirstOnOwner<FakeEnemy>(TestKit.ActorA, null), "持ち主のアクタから引ける");
            TestKit.Same(null, r.FirstOnOwner<FakeEnemy>(TestKit.ActorB, null), "別のアクタからは引けない");
            Check.Equal("e1", TestKit.Names(r.AllInstances<FakeEnemy>()), "全体の一覧");
            TestKit.Same(enemy, r.FirstInstance<FakeEnemy>(), "FirstInstance");
            Check.Equal(1, r.BoundCount, "持ち主の決まった数");
            Check.Equal(0, r.UnboundTypeCount, "問い合わせの候補から外れる");
        });

        h.Add("寿命: 同じ持ち主で何度決めても 1 回と同じ（参照の解決と OnStart の両方から呼ばれる）", () =>
        {
            var r = TestKit.NewRegistry();
            var enemy = new FakeEnemy("e1");
            r.NoteCreated(enemy);
            Check.True(r.Bind(enemy, TestKit.ActorA), "初めて決めたら true");
            Check.True(!r.Bind(enemy, TestKit.ActorA), "同じ持ち主なら false（表は変わらない）");
            Check.Equal(1, r.AllOnOwner<FakeEnemy>(TestKit.ActorA, null).Length, "アクタの一覧に 1 回だけ");
            Check.Equal(1, r.AllInstances<FakeEnemy>().Length, "全体の一覧に 1 回だけ");
            Check.Equal(1, r.BoundCount, "持ち主の決まった数");
        });

        h.Add("寿命: 外すと引けない・二重に外しても無害・空のアクタの一覧と型の一覧が残らない（旧アセンブリの型を握らない）", () =>
        {
            var r = TestKit.NewRegistry();
            var enemy = TestKit.Started(r, new FakeEnemy("e1"), TestKit.ActorA);
            Check.True(r.Remove(enemy), "外したら true");
            Check.True(!r.Remove(enemy), "二重に外すと false（無害）");
            TestKit.Same(null, r.FirstOnOwner<FakeEnemy>(TestKit.ActorA, null), "アクタから引けない");
            Check.Equal(0, r.AllInstances<FakeEnemy>().Length, "全体の一覧から消える");
            Check.Equal(0, r.Count, "数");
            Check.Equal(0, r.BoundCount, "持ち主の決まった数");
            Check.Equal(0, r.OwnerCount, "空のアクタの一覧が残らない");
            Check.Equal(0, r.TypeCount, "空の型の一覧が残らない");
        });

        h.Add("寿命: OnStart 前の破棄（持ち主が未定のまま DestroyComponent）も外れ、問い合わせの候補の型も消える", () =>
        {
            var r = TestKit.NewRegistry();
            var first = new FakeFish("f1");
            var second = new FakeFish("f2");
            r.NoteCreated(first);
            r.NoteCreated(second);
            Check.True(r.Remove(first), "未定のまま外せる");
            Check.Equal(1, r.UnboundTypeCount, "同じ型の未定がまだ居るので候補の型は残る");
            Check.True(r.Remove(second), "2 つ目も外せる");
            Check.Equal(0, r.UnboundTypeCount, "最後の 1 つで候補の型が消える");
            Check.Equal(0, r.Count, "数");
        });

        h.Add("寿命: 読み直しの全消去で全部外れ、作り直したインスタンスは新しく載る（番号は戻さないので生成順も保つ）", () =>
        {
            var r = TestKit.NewRegistry();
            TestKit.Started(r, new FakeEnemy("old1"), TestKit.ActorA);
            TestKit.Started(r, new FakeFish("old2"), TestKit.ActorB);
            r.NoteCreated(new FakeFish("oldUnbound"));
            r.Clear();
            Check.Equal(0, r.Count, "全消去の後の数");
            Check.Equal(0, r.BoundCount, "全消去の後の持ち主の決まった数");
            Check.Equal(0, r.OwnerCount + r.TypeCount + r.UnboundTypeCount, "表がすべて空");
            Check.Equal(0, r.AllInstances<FakeScript>().Length, "全体の一覧が空");

            // 読み直し後に作り直されたインスタンス（スロットの順に作られ、次の OnStart で持ち主が決まる）
            var newFirst = new FakeEnemy("new1");
            var newSecond = new FakeFish("new2");
            r.NoteCreated(newFirst);
            r.NoteCreated(newSecond);
            r.Bind(newSecond, TestKit.ActorA);
            r.Bind(newFirst, TestKit.ActorA);
            Check.Equal("new1,new2", TestKit.Names(r.AllOnOwner<FakeScript>(TestKit.ActorA, null)), "作り直した順（スロットの順）");
        });

        h.Add("寿命: エンティティの再利用（同じ index の別 generation）で古いアクタのスクリプトと混ざらない", () =>
        {
            var r = TestKit.NewRegistry();
            var oldActor = new ScriptOwnerKey(5, 0);
            var reusedActor = new ScriptOwnerKey(5, 1);
            var oldEnemy = TestKit.Started(r, new FakeEnemy("old"), oldActor);
            var newEnemy = TestKit.Started(r, new FakeEnemy("new"), reusedActor);
            // 古いアクタの破棄がまだ（フレーム末尾の前）でも、鍵の generation が違うので混ざらない
            TestKit.Same(oldEnemy, r.FirstOnOwner<FakeEnemy>(oldActor, null), "古い generation からは古いもの");
            TestKit.Same(newEnemy, r.FirstOnOwner<FakeEnemy>(reusedActor, null), "新しい generation からは新しいもの");
            Check.Equal(1, r.AllOnOwner<FakeEnemy>(reusedActor, null).Length, "新しい generation の一覧は 1 つ");
            // 古いほうを破棄しても新しいほうは残る
            r.Remove(oldEnemy);
            TestKit.Same(null, r.FirstOnOwner<FakeEnemy>(oldActor, null), "古い generation は空");
            TestKit.Same(newEnemy, r.FirstOnOwner<FakeEnemy>(reusedActor, null), "新しい generation は残る");
            Check.Equal(1, r.OwnerCount, "アクタの一覧は新しいほうだけ");
        });

        h.Add("寿命: 生成を知らされていないインスタンスを束縛しても載る（その時点で番号を振る）", () =>
        {
            var r = TestKit.NewRegistry();
            var known = new FakeEnemy("known");
            r.NoteCreated(known);
            var unknown = new FakeEnemy("unknown");
            Check.True(r.Bind(unknown, TestKit.ActorA), "知らないインスタンスも束縛できる");
            r.Bind(known, TestKit.ActorA);
            Check.Equal(2, r.Count, "数");
            Check.Equal("known,unknown", TestKit.Names(r.AllOnOwner<FakeEnemy>(TestKit.ActorA, null)), "番号は束縛した時点（known が先）");
        });

        h.Add("寿命: 持ち主が変わったら古いアクタから外れて新しいアクタへ移る（全体の一覧には 1 回だけ）", () =>
        {
            var r = TestKit.NewRegistry();
            var enemy = TestKit.Started(r, new FakeEnemy("e1"), TestKit.ActorA);
            Check.True(r.Bind(enemy, TestKit.ActorB), "別の持ち主なら true");
            TestKit.Same(null, r.FirstOnOwner<FakeEnemy>(TestKit.ActorA, null), "古いアクタから外れる");
            TestKit.Same(enemy, r.FirstOnOwner<FakeEnemy>(TestKit.ActorB, null), "新しいアクタから引ける");
            Check.Equal(1, r.AllInstances<FakeEnemy>().Length, "全体の一覧に 1 回だけ");
            Check.Equal(1, r.BoundCount, "持ち主の決まった数は変わらない");
            Check.Equal(1, r.OwnerCount, "古いアクタの空の一覧は消える");
        });

        h.Add("寿命: スクリプトが Equals を上書きしていても参照の等しさで区別する", () =>
        {
            var r = TestKit.NewRegistry();
            // 偽のスクリプトは Equals がわざと「どれも等しい」
            var first = TestKit.Started(r, new FakeEnemy("e1"), TestKit.ActorA);
            var second = TestKit.Started(r, new FakeEnemy("e2"), TestKit.ActorA);
            Check.Equal(2, r.Count, "2 つとも記録される");
            Check.Equal("e1,e2", TestKit.Names(r.AllOnOwner<FakeEnemy>(TestKit.ActorA, null)), "2 つとも引ける");
            r.Remove(second);
            TestKit.Same(first, r.FirstOnOwner<FakeEnemy>(TestKit.ActorA, null), "外したのは 2 つ目だけ");
            Check.Equal(1, r.Count, "残りの数");
        });
    }
}
