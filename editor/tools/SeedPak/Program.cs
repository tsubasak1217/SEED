// ============================================================
//  Program.cs — SeedPak（エディタを起動せずに assets.pak を作る）の入口
//
//  【流れ】
//    1. 引数の解釈（SeedPakArguments）
//    2. 入力の確定（PakInputResolver: アセットルート・runtime/src・収録ルール）
//    3. PAK 作り（AssetPakBuilder: 収集 → 報告 → 書き出し → 報告）
//       ＝ パッケージ化ウィンドウの「収録アセットの収集」「PAK 書き出し」フェーズと同じコード・同じログ
//
//  【出力】
//  <出力フォルダ>/assets.pak だけを書く。配布物で PAK の外に置く必要があるものは現状ない
//  （project_settings.json は収集の起点として必ず PAK に入る。bin/ のスクリプト DLL と .NET は
//  Windows 版のパッケージ化ウィンドウの工程で、Android では段階B まで使わない）。
//  Android の APK へ同梱するときは runtime/android/build_and_run.ps1 -ProjectDir がこのツールを呼ぶ。
// ============================================================

using System;
using SEEDEditor.Packaging;
using SEEDEditor.Packaging.Pak;

namespace SEEDEditor.Tools.SeedPak;

/// <summary>SeedPak の入口。</summary>
public static class Program
{
    // ── 終了コード（Usage の説明と一致させる）──────────────

    /// <summary>成功。</summary>
    private const int ExitSuccess = 0;

    /// <summary>引数・入力の誤り。</summary>
    private const int ExitInvalidInput = 1;

    /// <summary>収録対象が 0 件（PAK は書かない）。</summary>
    private const int ExitNothingToPack = 2;

    /// <summary>書き出しに失敗した。</summary>
    private const int ExitWriteFailed = 3;

    /// <summary>
    /// 引数を解釈して assets.pak を作る。
    /// </summary>
    /// <param name="args">コマンドライン引数（SeedPakArguments.Usage 参照）。</param>
    /// <returns>プロセス終了コード。</returns>
    public static int Main(string[] args)
    {
        // ── 1. 引数 ──
        var parsed = SeedPakArguments.Parse(args);
        if (parsed.ShowHelp)
        {
            Console.WriteLine(SeedPakArguments.Usage);
            return ExitSuccess;
        }
        if (parsed.Error is not null || parsed.Options is null)
        {
            Console.Error.WriteLine($"❌ {parsed.Error}");
            Console.Error.WriteLine();
            Console.Error.WriteLine(SeedPakArguments.Usage);
            return ExitInvalidInput;
        }

        // ── 2. 入力 ──
        var inputs = PakInputResolver.Resolve(parsed.Options, message => Console.Error.WriteLine(message));
        if (inputs is null) return ExitInvalidInput;

        Console.WriteLine("SeedPak — assets.pak の作成");
        Console.WriteLine($"アセットルート: {inputs.AssetsRoot}（{inputs.AssetsRootOrigin}）");
        Console.WriteLine($"runtime/src: {inputs.RuntimeSourceRoot ?? "（見つからないため、エンジン内蔵参照は起点に加えません）"}");
        Console.WriteLine(inputs.SettingsFound
            ? $"収録ルール: {inputs.SettingsPath}"
            : $"収録ルール: 既定値（{inputs.SettingsPath} が無いため）");
        var pakPath = PackageLayout.PakPath(inputs.OutDir);
        var watch   = System.Diagnostics.Stopwatch.StartNew();

        // ── 3. 収録アセットの収集（パッケージ化ウィンドウと同じ手順・同じログ）──
        Console.WriteLine();
        Console.WriteLine("── 収録アセットの収集 ──");
        var result = AssetPakBuilder.Collect(inputs.AssetsRoot, inputs.Settings, inputs.RuntimeSourceRoot, Console.WriteLine);
        AssetPakBuilder.ReportCollection(result, Console.WriteLine);
        if (!AssetPakBuilder.HasContent(result))
        {
            Console.WriteLine(AssetPakBuilder.EmptyCollectionMessage);
            return ExitNothingToPack;
        }

        // ── 4. PAK 書き出し ──
        Console.WriteLine();
        Console.WriteLine($"PAK 作成: {pakPath}");
        PakWriteStats stats;
        try
        {
            stats = AssetPakBuilder.Write(pakPath, inputs.AssetsRoot, result, Console.WriteLine, ReportProgress);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"❌ {PackageLayout.PakFileName} を書き出せませんでした: {ex.Message}");
            return ExitWriteFailed;
        }
        AssetPakBuilder.ReportWrite(stats, Console.WriteLine);
        Console.WriteLine($"完了: {pakPath}（{watch.Elapsed.TotalSeconds:F1} 秒）");
        return ExitSuccess;
    }

    /// <summary>PAK 書き出しの進捗を 1 行ずつ出す（大きなプロジェクトで止まって見えないように）。</summary>
    /// <param name="p">書き出し進捗。</param>
    private static void ReportProgress(PakWriteProgress p) =>
        Console.WriteLine($"  書き出し {p.FilesWritten}/{p.TotalFiles} ファイル " +
                          $"({AssetPakBuilder.ToMegabytes(p.BytesWritten):F0}/{AssetPakBuilder.ToMegabytes(p.TotalBytes):F0} MB)");
}
