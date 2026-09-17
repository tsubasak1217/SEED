// ============================================================
//  TestPaths.cs — テスト用の使い捨てフォルダ
//
//  【役割】
//  アカウントの保管先を **必ず一時フォルダへ向ける**。
//
//  【なぜこれが要るのか（壊すと利用者の実害になる）】
//  既定の保管先は `%APPDATA%\SEED\account\account.json`。
//  テストがそこへ書くと **利用者の本物のアカウントを上書きする**。
//  鍵を失うと参加中のプロジェクトへ入れなくなるので、取り返しがつかない。
//  そこで環境変数 SEED_ACCOUNT_DIR をプロセスの最初に必ず立て、
//  個々のテストも明示的に一時フォルダを渡す（二重の防御）。
// ============================================================

using System;
using System.IO;
using SEEDEditor.Accounts;

namespace SEEDEditor.Tests.Accounts;

/// <summary>
/// 使い捨てフォルダの用意と後始末。
/// </summary>
public static class TestPaths
{
    /// <summary>一時フォルダの親（%TEMP% 配下）。</summary>
    private const string ROOT_DIR_NAME = "SeedAccountsTests";

    /// <summary>
    /// 一時フォルダの親を差し替える環境変数。
    /// 実サーバ結合テストは数百 MB のストアを作るので、
    /// %TEMP% 以外の作業領域へ逃がしたいことがある。
    /// </summary>
    public const string ENV_TEMP_ROOT = "SEED_ACCOUNTS_TEST_TMP";

    /// <summary>このプロセスの一時フォルダの親。</summary>
    public static string SessionRoot { get; } = Path.Combine(
        ResolveTempRoot(), ROOT_DIR_NAME, Guid.NewGuid().ToString("N"));

    /// <summary>
    /// 一時フォルダの親を決める（環境変数で上書きできる）。
    /// 指定が無い・使えない場合は %TEMP% に落とす。
    /// </summary>
    private static string ResolveTempRoot()
    {
        try
        {
            var overridden = Environment.GetEnvironmentVariable(ENV_TEMP_ROOT);
            if (!string.IsNullOrWhiteSpace(overridden))
            {
                // ★区切り文字を正規化する。スラッシュ混じりのまま使うと
                //   Path.GetFullPath を通した文字列と一致せず、
                //   「文言にフォルダ名が入ること」のような比較が環境変数の有無で変わる。
                var full = Path.GetFullPath(overridden);
                Directory.CreateDirectory(full);
                return full;
            }
        }
        catch (Exception)
        {
            // 作れない場所を指されたら黙って %TEMP% を使う（テストは止めない）。
        }

        return Path.GetTempPath();
    }

    /// <summary>
    /// 保管先の既定を一時フォルダへ向ける（プロセスの最初に 1 回だけ呼ぶ）。
    /// </summary>
    public static void RedirectAccountDir()
    {
        var dir = NewDirectory("default_account_dir");
        Environment.SetEnvironmentVariable(AccountSettings.ENV_ACCOUNT_DIR, dir);
    }

    /// <summary>
    /// 一時フォルダを 1 つ作って返す。
    /// </summary>
    /// <param name="name">用途が分かる名前。</param>
    public static string NewDirectory(string name)
    {
        var dir = Path.Combine(SessionRoot, name + "_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>
    /// このプロセスが作った一時フォルダをまとめて消す。
    /// 消せなくてもテストの結果は変えない（%TEMP% に残るだけ）。
    /// </summary>
    public static void Cleanup()
    {
        // 結合テストの失敗を追うときは、サーバのログごと残す。
        if (!string.IsNullOrWhiteSpace(
                Environment.GetEnvironmentVariable(AccountsServerFixture.ENV_KEEP_TEMP)))
        {
            return;
        }

        try
        {
            if (Directory.Exists(SessionRoot)) Directory.Delete(SessionRoot, recursive: true);
        }
        catch (Exception)
        {
            // 掴まれているファイルがあると消せない。テストの成否には影響しない。
        }
    }
}
