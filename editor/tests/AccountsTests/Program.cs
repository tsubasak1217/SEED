// ============================================================
//  Program.cs — SEED アカウントの単体テスト（エントリポイント）
//
//  【検証の柱】
//   1. 鍵と署名の形式（固定のテストベクタ。公開鍵 65 バイト・署名 64 バイト）
//   2. 名前の規則（契約 2 章）
//   3. 保管の往復（DPAPI ＋ tmp → rename）
//   4. 書き出し／読み込み（パスフレーズ違い・改竄は必ず失敗する）
//   5. 窓口の URL の決め方（リモートと同じホストの 41350／利用者設定の上書き）
//   6. 偽の窓口に対する join → ログイン → 期限前の自動更新 → 失効時の失敗
//   7. 参加の段取り（トークン付きクローンと失敗時の言い分け）
//   8. Lore への資格情報の受け渡し（トークンがあれば Identity は空）
//
//  【既定では実サーバは要らない】
//  発行窓口は HttpListener で立てた偽物（FakeAuthGateway）。
//  **本番ポート（41337 / 41339 / 41350）には一切接続しない。**
//
//  【実サーバとの結合テスト】
//  環境変数 SEED_ACCOUNTS_TEST_SERVER があるときだけ ServerIntegrationTests も走る。
//  使い捨ての seed-loreserver を Lore 41357 / 41359、窓口 41361 で起動し、
//  トークン付きのクローン・送信・取得・ロックを実クラスで通す。
//  走らせ方と注意点は docs/editor_accounts.md の 4.0 節。
//
//  【利用者の実アカウントを触らない】
//  最初に SEED_ACCOUNT_DIR を一時フォルダへ向ける（TestPaths）。
// ============================================================

using System;
using SpriteRigTests;   // TestHarness / Check（テストランナーは SpriteRigTests と共用）

namespace SEEDEditor.Tests.Accounts;

/// <summary>
/// テストのエントリポイント。
/// </summary>
public static class Program
{
    /// <summary>全テストを実行し、失敗があれば終了コード 1 を返す。</summary>
    public static int Main()
    {
        // ★何よりも先に保管先を一時フォルダへ向ける。
        //   これを忘れると %APPDATA%\SEED\account\ の本物を壊し得る。
        TestPaths.RedirectAccountDir();
        Console.WriteLine($"アカウントの保管先を一時フォルダへ向けました: {TestPaths.SessionRoot}");
        Console.WriteLine();

        var harness = new TestHarness();

        CryptoTests.Register(harness);
        StorageTests.Register(harness);
        GatewayTests.Register(harness);
        JoinTests.Register(harness);
        CredentialTests.Register(harness);

        // 実サーバ結合テスト（環境変数があるときだけ）。
        // 既定では 1 件も登録しない ── ふだんのテストを遅くしないため。
        var runServerTests = ServerIntegrationTests.IsEnabled;
        if (runServerTests)
        {
            Console.WriteLine(
                $"[{ServerIntegrationTests.ENV_RUN_SERVER_TESTS}] が設定されているため、"
                + "実サーバ結合テストも実行します"
                + $"（Lore {AccountsServerFixture.PORT_QUIC_GRPC} / {AccountsServerFixture.PORT_HTTP}、"
                + $"窓口 {AccountsServerFixture.PORT_AUTH}）。");
            ServerIntegrationTests.Register(harness);
        }
        else
        {
            Console.WriteLine(
                "実サーバ結合テストは省略します"
                + $"（有効にするには環境変数 {ServerIntegrationTests.ENV_RUN_SERVER_TESTS} を設定）。");
        }
        Console.WriteLine();

        int exitCode;
        try
        {
            exitCode = harness.Run();
        }
        finally
        {
            // テストが途中で落ちても、起動したサーバは必ず止める。
            ServerIntegrationTests.Cleanup();
        }

        // 結合テストで Lore を初期化していた場合の後始末。
        // 環境変数が無いときは Lore を一度も呼んでいないので何も起きない。
        if (runServerTests)
        {
            SEEDEditor.VersionControl.Lore.Backend.LoreShutdownGuard.Shutdown(Console.WriteLine);
        }

        TestPaths.Cleanup();
        return exitCode;
    }
}
