// ============================================================
//  Program.cs — バージョン管理中核層の単体テスト（エントリポイント）
//
//  【検証の柱】
//   1. 競合選択の対応表（KeepMine → theirs / TakeRemote → mine）が逆転していないこと
//   2. 空コミット防止（staged 0 件なら commit を呼ばない）
//   3. push 拒否 → NeedsSync への変換
//   4. LOCAL / REMOTE ブランチの統合
//   5. <unknown> 所有者の扱い（自分のものと決めつけない）
//   6. 状態フラグ → モデル変換（特に KEEP = 変更）
//   7. パス変換（作業コピーの外を弾く）
//   8. NullProvider がすべて利用不可を返すこと
//   9. 直列ワーカーが操作を重ねないこと
//  10. 変更のパス群 → フォルダー階層のツリー、その平坦化と折りたたみ節
//  11. ロックのゲートの判定表（誰のロックで止め、どこでは止めないか）
//  12. マージエディタの純粋ロジック（印の分解・行差分・合成・検査）
//  13. 実サーバ結合（SEED_LORE_TEST_SERVER があるときだけ）
// ============================================================

using System;
using SpriteRigTests;   // TestHarness / Check（テストランナーは SpriteRigTests と共用）

namespace SEEDEditor.Tests.VersionControl;

/// <summary>
/// テストのエントリポイント。
/// </summary>
public static class Program
{
    /// <summary>
    /// 実サーバ結合テストを有効にする環境変数。
    /// 値の中身は問わず、空でなければ実行する。
    /// </summary>
    public const string ENV_RUN_SERVER_TESTS = "SEED_LORE_TEST_SERVER";

    /// <summary>全テストを実行し、失敗があれば終了コード 1 を返す。</summary>
    public static int Main()
    {
        var harness = new TestHarness();

        PureLogicTests.Register(harness);
        PanelStateTests.Register(harness);
        ChangeTreeTests.Register(harness);
        LockGateTests.Register(harness);
        MergeEditorTests.Register(harness);

        var runServerTests = !string.IsNullOrWhiteSpace(
            Environment.GetEnvironmentVariable(ENV_RUN_SERVER_TESTS));

        if (runServerTests)
        {
            Console.WriteLine($"[{ENV_RUN_SERVER_TESTS}] が設定されているため、実サーバ結合テストも実行します。");
            LoreServerIntegrationTests.Register(harness);
        }
        else
        {
            Console.WriteLine(
                $"実サーバ結合テストは省略します（有効にするには環境変数 {ENV_RUN_SERVER_TESTS} を設定）。");
        }

        Console.WriteLine();
        var exitCode = harness.Run();

        // 結合テストで Lore を初期化していた場合の後始末。
        // 純粋ロジックだけのときは Lore を一度も呼んでいないので何も起きない。
        if (runServerTests)
        {
            SEEDEditor.VersionControl.Lore.Backend.LoreShutdownGuard.Shutdown(Console.WriteLine);
        }

        return exitCode;
    }
}
