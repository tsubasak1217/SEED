using System;
using SpriteRigTests; // テストランナー（TestHarness / Check）を共有する

namespace ScriptEntityHandleTests;

/// <summary>
/// スクリプト API のエンティティハンドル（<see cref="SEED.Entity"/>）の有効判定テスト。
///
/// <para>直した不具合:</para>
/// <para>
/// ポーズメニューで Esc を押すとゲームは止まるのにメニューが出なかった。
/// 原因は <c>default(Entity)</c> が有効判定を通ってしまうこと。
/// <c>PauseMenu</c> は「生成済みか」を <c>menuRoot.IsValid</c> で判定していたが、
/// 一度も生成していない <c>default</c> のハンドルが <c>Index == 0</c> のため
/// 有効と判定され、<c>Instantiate</c> をせずに「既にあるものを再表示する」枝へ入り、
/// 時間スケールだけが 0 になっていた。
/// </para>
/// <para>
/// ランタイムの世代カウンタは 0 始まりなので <c>(index=0, generation=0)</c> は
/// 実在しうる正当なエンティティであり、インデックス値だけでは未束縛と区別できない。
/// 「コンストラクタを通ったか」を別に持つことで区別する、という不変条件を固定する。
/// </para>
/// </summary>
public static class Program
{
    public static int Main()
    {
        var h = new TestHarness();

        // ── 未束縛（default）は必ず無効 ─────────────────────────

        h.Add("default(Entity) は無効", () =>
        {
            Check.True(!default(SEED.Entity).IsValid,
                "一度も束縛していないハンドルが有効と判定された（生成済み判定が反転する）");
        });

        h.Add("Entity.None は無効", () =>
        {
            Check.True(!SEED.Entity.None.IsValid, "None が有効と判定された");
        });

        // ── 実在しうるエンティティは有効 ────────────────────────

        h.Add("インデックス 0・世代 0 のエンティティは有効", () =>
        {
            // ランタイムの最初の spawn がまさにこの値を返す。
            // default と同じビット列に見えるが、束縛済みなので有効でなければならない。
            Check.True(new SEED.Entity(0, 0).IsValid,
                "実在するエンティティ 0 番が無効と判定された");
        });

        h.Add("一般のエンティティは有効", () =>
        {
            Check.True(new SEED.Entity(42, 7).IsValid, "通常のエンティティが無効と判定された");
        });

        h.Add("無効インデックスで束縛しても無効", () =>
        {
            // ネイティブ側が「見つからなかった」を返すときの値。
            Check.True(!new SEED.Entity(uint.MaxValue, 0).IsValid,
                "無効インデックスのハンドルが有効と判定された");
        });

        // ── 値そのものは素通しであること ────────────────────────

        h.Add("index / generation は加工されずに読み出せる", () =>
        {
            var e = new SEED.Entity(3, 5);
            Check.Equal(3u, e.Index, "Index");
            Check.Equal(5u, e.Generation, "Generation");
        });

        return h.Run();
    }
}
