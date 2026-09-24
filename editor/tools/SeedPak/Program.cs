// ============================================================
//  Program.cs — SeedPak（エディタを起動せずに assets.pak を作る）の入口
//
//  【流れ】
//    1. 引数の解釈（SeedPakArguments）
//    2. 入力の確定（PakInputResolver: アセットルート・runtime/src・収録ルール）
//    3. PAK 作り（AssetPakBuilder: 収集 → 報告 → 書き出し → 報告）
//       ＝ パッケージ化ウィンドウの「収録アセットの収集」「PAK 書き出し」フェーズと同じコード・同じログ
//    4. （--scripts / --scripts-only のとき）スクリプトの同梱（ScriptPackager）
//       ＝ パッケージ化ウィンドウの「スクリプト事前コンパイル」フェーズと同じコード: アセット配下の .cs を
//          bin/SEEDUserScripts.dll へ事前コンパイルし、スクリプトホスト（SEEDScripting.dll・依存 DLL・runtimeconfig）を写す
//
//  【出力】
//  <出力フォルダ>/assets.pak（--scripts-only では作らない）と、--scripts のとき <出力フォルダ>/bin/。
//  project_settings.json は収集の起点として必ず PAK に入る。.NET ランタイム本体は入れない
//  （Windows はパッケージ化ウィンドウの DotnetRuntimeBundler、Android は SeedAndroid の DotnetRuntimeBundle が NuGet から組み立てる）。
//  Android の APK へ同梱するときは SeedAndroid（editor/tools/SeedAndroid。build_and_run.ps1 はそのラッパー）の --project が
//  このツールを --scripts 付きで呼び、DLL だけの差し替え（push / --push-scripts）では --scripts-only で呼ぶ。
// ============================================================

using System;
using System.IO;
using SEEDEditor.Packaging;
using SEEDEditor.Packaging.Pak;
using SEEDEditor.Packaging.Scripts;

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

    /// <summary>スクリプトの事前コンパイル・同梱に失敗した（--scripts / --scripts-only）。</summary>
    private const int ExitScriptsFailed = 4;

    /// <summary>
    /// 引数を解釈して assets.pak と（--scripts / --scripts-only のとき）bin/ を作る。
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
        var options = parsed.Options;
        var inputs  = PakInputResolver.Resolve(options, message => Console.Error.WriteLine(message));
        if (inputs is null) return ExitInvalidInput;

        Console.WriteLine(options.WritesPak ? "SeedPak — assets.pak の作成" : "SeedPak — スクリプトの同梱（bin/ だけ）");
        Console.WriteLine($"アセットルート: {inputs.AssetsRoot}（{inputs.AssetsRootOrigin}）");
        Console.WriteLine($"runtime/src: {inputs.RuntimeSourceRoot ?? "（見つからないため、エンジン内蔵参照は起点に加えません）"}");
        var watch = System.Diagnostics.Stopwatch.StartNew();

        if (options.WritesPak)
        {
            var pakResult = WritePak(inputs);
            if (pakResult != ExitSuccess) return pakResult;
        }

        if (options.WritesScripts)
        {
            var scriptsResult = WriteScripts(inputs);
            if (scriptsResult != ExitSuccess) return scriptsResult;
        }

        Console.WriteLine($"完了: {inputs.OutDir}（{watch.Elapsed.TotalSeconds:F1} 秒）");
        return ExitSuccess;
    }

    /// <summary>
    /// assets.pak を作る（パッケージ化ウィンドウの「収録アセットの収集」「PAK 書き出し」と同じコード・同じログ）。
    /// </summary>
    /// <param name="inputs">確定した入力。</param>
    /// <returns>終了コード（成功なら <see cref="ExitSuccess"/>）。</returns>
    private static int WritePak(PakInputs inputs)
    {
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
        Console.WriteLine($"PAK 完了: {pakPath}（{watch.Elapsed.TotalSeconds:F1} 秒）");
        return ExitSuccess;
    }

    /// <summary>
    /// bin/ にスクリプトを作る（パッケージ化ウィンドウの「スクリプト事前コンパイル」と同じ ScriptPackager）。
    /// </summary>
    /// <param name="inputs">確定した入力（アセットルート・runtime/src・出力フォルダ）。</param>
    /// <returns>終了コード（成功なら <see cref="ExitSuccess"/>）。</returns>
    private static int WriteScripts(PakInputs inputs)
    {
        // ScriptPackager はスクリプトホストのビルド出力を runtime フォルダ基準（..\scripting\bin\Debug\net10.0）で探す。
        var runtimeDir = inputs.RuntimeSourceRoot is null ? null : Path.GetDirectoryName(inputs.RuntimeSourceRoot);
        if (runtimeDir is null)
        {
            Console.Error.WriteLine(
                $"❌ runtime/ が見つからないため、スクリプトホストのビルド出力を探せません（{SeedPakArguments.RuntimeSourceOption} で runtime/src を指定してください）");
            return ExitInvalidInput;
        }

        Console.WriteLine();
        Console.WriteLine("── スクリプトの事前コンパイルと同梱 ──");
        var watch  = System.Diagnostics.Stopwatch.StartNew();
        var result = ScriptPackager.Run(runtimeDir, inputs.AssetsRoot, inputs.OutDir, Console.WriteLine);
        if (!result.Success)
        {
            ScriptPackager.LogErrors(result, message => Console.Error.WriteLine(message));
            return ExitScriptsFailed;
        }
        Console.WriteLine(
            $"スクリプト完了: {PackageLayout.BinDirectory(inputs.OutDir)}（{result.ScriptTypeCount} 型 / {result.SourceFileCount} ファイル・" +
            $"{watch.Elapsed.TotalSeconds:F1} 秒）");
        return ExitSuccess;
    }

    /// <summary>PAK 書き出しの進捗を 1 行ずつ出す（大きなプロジェクトで止まって見えないように）。</summary>
    /// <param name="p">書き出し進捗。</param>
    private static void ReportProgress(PakWriteProgress p) =>
        Console.WriteLine($"  書き出し {p.FilesWritten}/{p.TotalFiles} ファイル " +
                          $"({AssetPakBuilder.ToMegabytes(p.BytesWritten):F0}/{AssetPakBuilder.ToMegabytes(p.TotalBytes):F0} MB)");
}
