using System;
using System.Diagnostics;
using System.IO;
using SEEDEditor.Assets;
using SpriteRigTests;   // TestHarness / Check（テストランナーは SpriteRigTests と共用）

namespace AssetsRootProbeTests;

/// <summary>
/// <see cref="AssetsRootProbe"/> の単体テスト。
///
/// 検証の柱:
///   1. 正常なフォルダ（中身あり／空）を Ok と判定する
///   2. 存在しないパス・空パスを Missing / Invalid と判定する
///   3. <b>壊れたジャンクション</b>（Directory.Exists が true を返すのに列挙で落ちるケース）を
///      BrokenLink と判定する ← 起動時クラッシュの実際の原因
///   4. 生きたジャンクションは Ok でリンク先も取れる
///
/// アクセス拒否（AccessDenied）は権限操作が環境依存なので対象外。
/// </summary>
public static class Program
{
    /// <summary>エントリポイント。全テストを実行し、失敗があれば終了コード 1 を返す。</summary>
    public static int Main()
    {
        var harness = new TestHarness();

        harness.Add("中身のある通常フォルダは Ok",             NormalDirectoryIsOk);
        harness.Add("空フォルダも Ok（中身の有無は問わない）", EmptyDirectoryIsOk);
        harness.Add("存在しないパスは Missing",                 NonExistentIsMissing);
        harness.Add("空文字・null は Invalid",                  EmptyPathIsInvalid);
        harness.Add("フォルダではなくファイルを指すと Missing", FilePathIsMissing);
        harness.Add("生きたジャンクションは Ok でリンク先が取れる", LiveJunctionIsOk);
        harness.Add("壊れたジャンクションは BrokenLink（Exists は true を返す）", BrokenJunctionIsBrokenLink);

        return harness.Run();
    }

    // ============================================================
    //  テスト本体
    // ============================================================

    /// <summary>中身のある通常フォルダは Ok になる。</summary>
    private static void NormalDirectoryIsOk()
    {
        using var temp = new TempDir();
        var dir = temp.CreateSubDirectory("assets");
        File.WriteAllText(Path.Combine(dir, "dummy.txt"), "x");

        var result = AssetsRootProbe.Check(dir);
        Check.Equal(AssetsRootStatus.Ok, result.Status, "状態");
        Check.True(result.IsAvailable, "IsAvailable が true であること");
        Check.Equal(null, result.LinkTarget, "リンク先（通常フォルダなので null）");
    }

    /// <summary>空フォルダも Ok になる（列挙が 0 件でも成功は成功）。</summary>
    private static void EmptyDirectoryIsOk()
    {
        using var temp = new TempDir();
        var dir = temp.CreateSubDirectory("empty");

        var result = AssetsRootProbe.Check(dir);
        Check.Equal(AssetsRootStatus.Ok, result.Status, "状態");
    }

    /// <summary>存在しないパスは Missing になる。</summary>
    private static void NonExistentIsMissing()
    {
        using var temp = new TempDir();
        var dir = Path.Combine(temp.Path, "does_not_exist");

        var result = AssetsRootProbe.Check(dir);
        Check.Equal(AssetsRootStatus.Missing, result.Status, "状態");
        Check.True(!result.IsAvailable, "IsAvailable が false であること");
    }

    /// <summary>空文字・null は Invalid になる（例外を投げない）。</summary>
    private static void EmptyPathIsInvalid()
    {
        Check.Equal(AssetsRootStatus.Invalid, AssetsRootProbe.Check("").Status,   "空文字");
        Check.Equal(AssetsRootStatus.Invalid, AssetsRootProbe.Check(null).Status, "null");
        Check.Equal(AssetsRootStatus.Invalid, AssetsRootProbe.Check("   ").Status, "空白のみ");
    }

    /// <summary>フォルダではなくファイルを指した場合も、例外にせず利用不可として返す。</summary>
    private static void FilePathIsMissing()
    {
        using var temp = new TempDir();
        var file = Path.Combine(temp.Path, "a_file.txt");
        File.WriteAllText(file, "x");

        var result = AssetsRootProbe.Check(file);
        Check.True(!result.IsAvailable, "ファイルを指した場合は利用不可であること");
    }

    /// <summary>リンク先が生きているジャンクションは Ok で、リンク先も取得できる。</summary>
    private static void LiveJunctionIsOk()
    {
        using var temp = new TempDir();
        var target = temp.CreateSubDirectory("real_assets");
        File.WriteAllText(Path.Combine(target, "dummy.txt"), "x");
        var link = Path.Combine(temp.Path, "assets_link");

        if (!TryCreateJunction(link, target))
        {
            Console.WriteLine("         （ジャンクションを作成できない環境のためスキップ）");
            return;
        }

        var result = AssetsRootProbe.Check(link);
        Check.Equal(AssetsRootStatus.Ok, result.Status, "状態");
        Check.True(result.LinkTarget is not null, "リンク先が取得できること");
    }

    /// <summary>
    /// 本命。ジャンクションを作ってからリンク先を削除し、
    /// 「Directory.Exists は true なのに列挙は失敗する」状態を再現して BrokenLink になることを確かめる。
    /// これは runtime/assets が未接続ドライブへのジャンクションだった場合と同じ状況。
    /// </summary>
    private static void BrokenJunctionIsBrokenLink()
    {
        using var temp = new TempDir();
        var target = temp.CreateSubDirectory("real_assets");
        var link   = Path.Combine(temp.Path, "assets_link");

        if (!TryCreateJunction(link, target))
        {
            Console.WriteLine("         （ジャンクションを作成できない環境のためスキップ）");
            return;
        }

        // リンク先を消す = ドライブが外れた状態と同じ（リンク自身は残る）
        Directory.Delete(target, recursive: true);

        // 前提の確認: Exists は騙される
        Check.True(Directory.Exists(link),
            "前提: 壊れたジャンクションでも Directory.Exists は true を返すこと");

        var result = AssetsRootProbe.Check(link);
        Check.Equal(AssetsRootStatus.BrokenLink, result.Status, "状態");
        Check.True(!result.IsAvailable, "IsAvailable が false であること");
        Check.True(result.Reason.Length > 0, "理由文が空でないこと");
    }

    // ============================================================
    //  ヘルパー
    // ============================================================

    /// <summary>
    /// ディレクトリジャンクションを作る（管理者権限不要）。
    /// mklink は cmd.exe の内部コマンドなので cmd 経由で呼ぶ。
    /// </summary>
    /// <param name="link">作成するリンクのパス。</param>
    /// <param name="target">リンク先フォルダ。</param>
    /// <returns>作成できたら true。環境的に作れなければ false（テストはスキップ扱い）。</returns>
    private static bool TryCreateJunction(string link, string target)
    {
        try
        {
            var psi = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
            {
                CreateNoWindow         = true,
                UseShellExecute        = false,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return false;
            p.WaitForExit();
            return p.ExitCode == 0 && Directory.Exists(link);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>テスト用の一時フォルダ。Dispose で丸ごと削除する。</summary>
    private sealed class TempDir : IDisposable
    {
        /// <summary>一時フォルダの絶対パス。</summary>
        public string Path { get; }

        /// <summary>一時フォルダを新規作成する。</summary>
        public TempDir()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "SEED_AssetsRootProbeTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        /// <summary>一時フォルダ直下にサブフォルダを作り、その絶対パスを返す。</summary>
        /// <param name="name">サブフォルダ名。</param>
        public string CreateSubDirectory(string name)
        {
            var dir = System.IO.Path.Combine(Path, name);
            Directory.CreateDirectory(dir);
            return dir;
        }

        /// <summary>一時フォルダを削除する（ジャンクションが残っていても中身は辿らない）。</summary>
        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { /* 後始末の失敗は無視 */ }
        }
    }
}
