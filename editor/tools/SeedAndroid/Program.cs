// ============================================================
//  Program.cs — SeedAndroid（Android のビルド・配置・起動のコンソールツール）の入口
//
//  【流れ】
//    1. 引数の解釈（SeedAndroidArguments）と設定 JSON（--config。RunRequestConfig）
//    2. 道具の場所の解決（中核の AndroidToolchain。見つからない道具は、それが要る工程でエラーになる）
//    3. サブコマンドごとの処理（Commands/）。手順の中身はすべて editor/src/Android/ の中核
//       （エディタの実行先セレクタ＝段階C-2 と同じクラス）
//  Ctrl+C は中断の合図として中核へ渡す（子プロセスを止めて終わる）。logcat を流している間の Ctrl+C は「止めた」＝成功。
//
//  【出力の文字コード】
//  出力をファイル・パイプへ向けたとき（build_and_run.ps1 の -LogFile 以外の保存、エディタからの起動など）は UTF-8 で書く。
//  コンソールへ直接書くときはコンソールの設定のまま（コードページは変えない）。
// ============================================================

using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SEEDEditor.Android.Pipeline;
using SEEDEditor.Android.Toolchain;
using SEEDEditor.Tools.SeedAndroid.Commands;

namespace SEEDEditor.Tools.SeedAndroid;

/// <summary>SeedAndroid の入口。</summary>
public static class Program
{
    /// <summary>
    /// 引数を解釈してサブコマンドを実行する。
    /// </summary>
    /// <param name="args">コマンドライン引数（SeedAndroidArguments.Usage 参照）。</param>
    /// <returns>終了コード（SeedAndroidExitCodes）。</returns>
    public static async Task<int> Main(string[] args)
    {
        UseUtf8WhenRedirected();

        // ── 1. 引数と設定 JSON ──
        var parsed = SeedAndroidArguments.Parse(args);
        if (parsed.ShowHelp)
        {
            Console.WriteLine(SeedAndroidArguments.Usage);
            return SeedAndroidExitCodes.Success;
        }
        if (parsed.Error is not null || parsed.CommandLine is null)
        {
            Console.Error.WriteLine($"エラー: {parsed.Error}");
            Console.Error.WriteLine();
            Console.Error.WriteLine(SeedAndroidArguments.Usage);
            return SeedAndroidExitCodes.InvalidRequest;
        }
        var line = parsed.CommandLine;
        AndroidRunRequest? config = null;
        if (line.ConfigPath is not null)
        {
            config = RunRequestConfig.Load(line.ConfigPath, out var configError);
            if (config is null)
            {
                Console.Error.WriteLine($"エラー: {configError}");
                return SeedAndroidExitCodes.InvalidRequest;
            }
        }

        // ── 2. 道具と中断の合図 ──
        var toolchain = AndroidToolchain.Detect();
        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            // 1 回目は中断の合図にして後始末（子プロセスの終了・記録の保存）をさせる。2 回目はそのまま終わらせる
            if (cancellation.IsCancellationRequested) return;
            e.Cancel = true;
            cancellation.Cancel();
        };

        // ── 3. サブコマンド ──
        try
        {
            return line.Command switch
            {
                SeedAndroidCommand.Devices => await DevicesCommand.RunAsync(toolchain, line.Json, cancellation.Token),
                SeedAndroidCommand.Stop    => await StopCommand.RunAsync(toolchain, line, config, cancellation.Token),
                SeedAndroidCommand.Logcat  => await LogcatCommand.RunAsync(toolchain, line, config, cancellation.Token),
                // 動いているアプリへ IPC で 1 命令（段階D-1）
                SeedAndroidCommand.Pause or SeedAndroidCommand.Resume or SeedAndroidCommand.Screenshot
                    or SeedAndroidCommand.Snapshot
                                           => await AppControlCommand.RunAsync(toolchain, line, config, cancellation.Token),
                // 実行中の差し替え（docs/android.md §23）: 差し替えを頼む・端末と違うアセットだけを送る
                SeedAndroidCommand.Reload  => await ReloadCommand.RunAsync(toolchain, line, config, cancellation.Token),
                SeedAndroidCommand.Push when line.OverlayAssetsDir is not null
                                           => await AssetPushCommand.RunAsync(toolchain, line, config, cancellation.Token),
                // 配布用の鍵を作る・Google Play の要件を確かめる（段階D。docs/android.md §24）
                SeedAndroidCommand.Keystore => await KeystoreCommand.RunAsync(toolchain, line, cancellation.Token),
                SeedAndroidCommand.Check    => await CheckCommand.RunAsync(toolchain, line, config, cancellation.Token),
                _                          => await PipelineCommand.RunAsync(toolchain, line, config, cancellation.Token),
            };
        }
        catch (AndroidPipelineException ex)
        {
            Console.Error.WriteLine($"エラー: {ex.Message}");
            return SeedAndroidExitCodes.For(ex.Kind);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            Console.Error.WriteLine("中断しました。");
            return SeedAndroidExitCodes.Canceled;
        }
    }

    /// <summary>出力がファイル・パイプのときは UTF-8（BOM なし）で書く（コンソールのコードページは変えない）。</summary>
    private static void UseUtf8WhenRedirected()
    {
        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        if (Console.IsOutputRedirected)
        {
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), utf8) { AutoFlush = true });
        }
        if (Console.IsErrorRedirected)
        {
            Console.SetError(new StreamWriter(Console.OpenStandardError(), utf8) { AutoFlush = true });
        }
    }
}
