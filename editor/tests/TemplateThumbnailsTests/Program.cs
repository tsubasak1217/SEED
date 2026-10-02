// ============================================================
//  Program.cs — 見本の画像の生成の道具（SeedTemplateThumbnails）の単体テスト
//
//  実行: dotnet run --project editor/tests/TemplateThumbnailsTests
//
//  【検証範囲】（道具そのものは実行しない。ランタイムも起動しない）
//   1. WorkFolderPolicy      : 作業の置き場を使ってよいかの判定（純粋な処理。全分岐）
//   2. ThumbnailWorkFolder   : 借りる（印・錠）・前の中身を消す・返す（一時フォルダの実物）
//   3. ThumbnailResultOrder  : 結果をカタログの順へ並べる鍵（ライブラリ相対パス）
//   4. QuietProcess          : 見張りのスレッドを止めてから Process を捨てる（ping を 1 つ起動）
//  （docs/reviews/2026-10-02_code_review.md の #2・#19・#17）
// ============================================================

using SpriteRigTests;

namespace SEEDEditor.Tests.TemplateThumbnails;

/// <summary>テストの登録と実行を行う入口。</summary>
public static class Program
{
    /// <summary>
    /// テストを登録して実行する。
    /// </summary>
    /// <returns>プロセスの終了コード（全成功なら 0）。</returns>
    public static int Main()
    {
        Console.WriteLine("TemplateThumbnailsTests");
        var h = new TestHarness();

        WorkFolderPolicyTests.Register(h);
        ThumbnailWorkFolderTests.Register(h);
        ResultOrderTests.Register(h);
        QuietProcessTests.Register(h);

        return h.Run();
    }
}
