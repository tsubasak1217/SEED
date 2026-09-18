// ============================================================
//  MigrateJsonRunner.cs — SEED.exe --migrate-json <kind> の呼び出し
//
//  【役割】
//  「JSON テキスト 1 件をランタイムへ渡し、現行版へ持ち上げたテキストを受け取る」
//  という 1 往復だけを担当する。呼び出し規約の正典は docs/asset_migration.md 6.5 (2)。
//
//  【この口がある理由】
//  .anim / .inputmap / .sprite_mesh / 地形 JSON / project_settings.json は
//  書き手がエディタ（C#）にしか無い。変換をエディタ側にも書くと
//  「ランタイムは変換して読むのに、エディタは変換せずに読む」食い違いが生まれる
//  （.inputmap の二重実装が既にその負債だった）。変換はランタイム 1 か所に保つ。
//
//  【デッドロックを避ける手順（★崩さないこと）】
//   1. プロセスを起動する
//   2. **標準出力・標準エラーの読み取りを先に始める**
//   3. そのあと標準入力へ書き、閉じる
//   4. 終了を待つ
//  2 と 3 を逆にすると、入力が大きいときに
//  「子は出力バッファが満杯で書けない ／ 親は入力を書き終えるまで読まない」で双方が止まる。
//
//  【依存】
//  WPF に依存しない（単体テストへそのままリンクできる）。
// ============================================================

using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace SEEDEditor.Migration;

// ── 結果 ─────────────────────────────────────────────────────────

/// <summary>
/// <c>--migrate-json</c> の結末。ランタイムの終了コードと 1 対 1 で対応する
/// （<see cref="ExeMissing"/> 以降はエディタ側でしか起きない）。
/// </summary>
public enum MigrateJsonOutcome
{
    /// <summary>終了コード 0。現行版へ持ち上げた JSON が得られた。</summary>
    Success,

    /// <summary>終了コード 1。このエンジンより新しい版のファイルだった。</summary>
    FutureVersion,

    /// <summary>終了コード 2。形式名の綴り違い（＝呼び出し側の不具合）。</summary>
    BadUsage,

    /// <summary>終了コード 3。JSON として読めない／変換に失敗した。</summary>
    Failed,

    /// <summary>ランタイム exe が見つからない（未ビルド・配置違い）。</summary>
    ExeMissing,

    /// <summary>期限内に終わらなかった。</summary>
    Timeout,

    /// <summary>プロセスの起動そのものに失敗した。</summary>
    LaunchFailed,
}

/// <summary>
/// <c>--migrate-json</c> の結果（不変）。
/// </summary>
/// <param name="Outcome">結末。</param>
/// <param name="Json">
/// 現行版へ持ち上げた JSON（<see cref="MigrateJsonOutcome.Success"/> のときだけ意味を持つ）。
/// </param>
/// <param name="ErrorCode">ランタイムが返した分類（<c>future_version</c> など）。無ければ空。</param>
/// <param name="Message">利用者・ログ向けの説明（日本語）。成功時は空。</param>
public sealed record MigrateJsonResult(
    MigrateJsonOutcome Outcome,
    string Json,
    string ErrorCode,
    string Message)
{
    /// <summary>変換に成功したか。</summary>
    public bool IsSuccess => Outcome == MigrateJsonOutcome.Success;
}

// ── 実行 ─────────────────────────────────────────────────────────

/// <summary>
/// ランタイムの 1 件変換コマンドを呼ぶ（状態を持たない静的クラス）。
/// </summary>
public static class MigrateJsonRunner
{
    // ── 呼び出し規約の定数（マジック文字列・マジックナンバーの一元化）──

    /// <summary>1 件変換を要求するコマンドライン引数。</summary>
    public const string MIGRATE_JSON_FLAG = "--migrate-json";

    /// <summary>終了コード: 成功。</summary>
    public const int EXIT_OK = 0;

    /// <summary>終了コード: 未来版。</summary>
    public const int EXIT_FUTURE_VERSION = 1;

    /// <summary>終了コード: 引数が不正。</summary>
    public const int EXIT_BAD_USAGE = 2;

    /// <summary>終了コード: 解析・変換の失敗。</summary>
    public const int EXIT_FAILED = 3;

    /// <summary>
    /// 変換 1 件の待ち時間の既定値。
    ///
    /// <para>
    /// 対象は「書き手がエディタの形式」＝数 KB 〜 数百 KB の JSON なので、
    /// 通常は数十ミリ秒で終わる。ここまで待っても終わらないのは
    /// プロセスが刺さっている状態なので、開く操作を人質に取らずに諦める。
    /// </para>
    /// </summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    /// <summary>標準出力・標準エラーを読み切るまでの追加の待ち時間。</summary>
    private static readonly TimeSpan StreamDrainTimeout = TimeSpan.FromSeconds(5);

    /// <summary>JSON の先頭に付きうる BOM（渡す前に落とす）。</summary>
    private const char Bom = '﻿';

    /// <summary>ランタイムのエラー行に入る分類キー。</summary>
    private const string ERROR_JSON_KEY_ERROR = "error";

    /// <summary>ランタイムのエラー行に入る説明キー。</summary>
    private const string ERROR_JSON_KEY_MESSAGE = "message";

    // ── 公開 API ─────────────────────────────────────────────

    /// <summary>
    /// JSON テキスト 1 件をランタイムへ通し、現行版へ持ち上げた JSON を得る。
    /// </summary>
    /// <param name="exePath">ランタイム exe の絶対パス（<c>RuntimeExeLocator.Resolve</c> の結果）。</param>
    /// <param name="format">対象の形式。</param>
    /// <param name="json">変換する JSON テキスト（先頭 BOM は落として渡す）。</param>
    /// <param name="timeout">待ち時間（null なら <see cref="DefaultTimeout"/>）。</param>
    /// <returns>変換結果。</returns>
    public static MigrateJsonResult Run(
        string? exePath, AssetFormat format, string json, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(format);

        // ── exe の存在確認（未ビルドの構成では普通に起こる）──
        if (string.IsNullOrWhiteSpace(exePath))
        {
            return Failure(MigrateJsonOutcome.ExeMissing, MigrationMessages.RUNTIME_EXE_UNRESOLVED);
        }
        if (!File.Exists(exePath))
        {
            return Failure(
                MigrateJsonOutcome.ExeMissing,
                string.Format(CultureInfo.InvariantCulture,
                              MigrationMessages.RUNTIME_EXE_MISSING_FORMAT, exePath));
        }

        var limit = timeout ?? DefaultTimeout;
        var start = BuildStartInfo(exePath, format);

        try
        {
            using var process = Process.Start(start);
            if (process is null)
            {
                return Failure(
                    MigrateJsonOutcome.LaunchFailed,
                    string.Format(CultureInfo.InvariantCulture,
                                  MigrationMessages.MIGRATE_LAUNCH_FAILED_FORMAT,
                                  "プロセスを起動できませんでした"));
            }

            // ★ 順序が重要: 標準入力を書く前に、出力の読み取りを始めておく。
            //    先に WaitForExit / 入力の書き切りを待つと、出力バッファが満杯になった
            //    時点で親子ともに止まる（docs/asset_migration.md 6.5 (2)）。
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();

            WriteStandardInput(process, json);

            if (!process.WaitForExit((int)limit.TotalMilliseconds))
            {
                KillQuietly(process);
                return Failure(
                    MigrateJsonOutcome.Timeout,
                    string.Format(CultureInfo.InvariantCulture,
                                  MigrationMessages.MIGRATE_TIMEOUT_FORMAT,
                                  limit.TotalSeconds.ToString("0", CultureInfo.InvariantCulture)));
            }

            // 終了後もパイプに残りがあるので読み切る（読み切れなくても結論は出せる）。
            Task.WaitAll(new Task[] { stdoutTask, stderrTask }, StreamDrainTimeout);
            var stdout = stdoutTask.IsCompletedSuccessfully ? stdoutTask.Result : string.Empty;
            var stderr = stderrTask.IsCompletedSuccessfully ? stderrTask.Result : string.Empty;

            return Interpret(process.ExitCode, stdout, stderr, format);
        }
        catch (Exception e)
        {
            // 起動の失敗（パス・権限・実行形式の不一致）で呼び出し側を落とさない。
            return Failure(
                MigrateJsonOutcome.LaunchFailed,
                string.Format(CultureInfo.InvariantCulture,
                              MigrationMessages.MIGRATE_LAUNCH_FAILED_FORMAT, e.Message));
        }
    }

    // ── 内部 ─────────────────────────────────────────────────

    /// <summary>起動情報を組み立てる（3 本のストリームをリダイレクトし、窓を出さない）。</summary>
    /// <param name="exePath">ランタイム exe の絶対パス。</param>
    /// <param name="format">対象の形式。</param>
    private static ProcessStartInfo BuildStartInfo(string exePath, AssetFormat format)
    {
        // BOM なし UTF-8。BOM を付けるとランタイム側が JSON の先頭で躓く可能性がある
        //（ランタイムは BOM を許容するが、こちらから増やす理由が無い）。
        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        var info = new ProcessStartInfo
        {
            FileName               = exePath,
            UseShellExecute        = false,
            CreateNoWindow         = true,
            RedirectStandardInput  = true,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            StandardInputEncoding  = utf8,
            StandardOutputEncoding = utf8,
            StandardErrorEncoding  = utf8,
            // 作業ディレクトリは exe の隣にする（相対パスの解釈を安定させる）。
            WorkingDirectory       = Path.GetDirectoryName(Path.GetFullPath(exePath)) ?? string.Empty,
        };
        // ArgumentList を使うと引用符の付け方を自分で考えなくて済む。
        info.ArgumentList.Add(MIGRATE_JSON_FLAG);
        info.ArgumentList.Add(format.Label);
        return info;
    }

    /// <summary>標準入力へ JSON を書いて閉じる。</summary>
    /// <param name="process">起動済みのプロセス。</param>
    /// <param name="json">書き込む JSON テキスト。</param>
    private static void WriteStandardInput(Process process, string json)
    {
        try
        {
            process.StandardInput.Write(json.TrimStart(Bom));
        }
        catch (IOException)
        {
            // 子が先に落ちた（引数不正など）。閉じて終了コードで判断する。
        }
        finally
        {
            // 閉じないとランタイムの read_to_string が終わらない。
            try { process.StandardInput.Close(); } catch (IOException) { /* 既に閉じている */ }
        }
    }

    /// <summary>終了コードと出力から結果を組み立てる。</summary>
    /// <param name="exitCode">プロセスの終了コード。</param>
    /// <param name="stdout">標準出力（成功時は変換後の JSON）。</param>
    /// <param name="stderr">標準エラー（失敗時は 1 行 JSON）。</param>
    /// <param name="format">対象の形式（メッセージ用）。</param>
    private static MigrateJsonResult Interpret(
        int exitCode, string stdout, string stderr, AssetFormat format)
    {
        if (exitCode == EXIT_OK)
        {
            return new MigrateJsonResult(
                MigrateJsonOutcome.Success, stdout, string.Empty, string.Empty);
        }

        var (code, message) = ParseErrorLine(stderr);
        if (message.Length == 0)
        {
            // ランタイムが何も言わずに落ちた場合でも、必ず何か残す。
            message = $"{format.Label} の変換に失敗しました（終了コード {exitCode}）。";
        }

        var outcome = exitCode switch
        {
            EXIT_FUTURE_VERSION => MigrateJsonOutcome.FutureVersion,
            EXIT_BAD_USAGE      => MigrateJsonOutcome.BadUsage,
            _                   => MigrateJsonOutcome.Failed,
        };
        return new MigrateJsonResult(outcome, string.Empty, code, message);
    }

    /// <summary>
    /// 標準エラーの 1 行 JSON（<c>{"kind":"error","error":"…","message":"…"}</c>）を読む。
    /// 読めなければ空を返す（結論は終了コードで決まるので、ここで失敗しても困らない）。
    /// </summary>
    /// <param name="stderr">標準エラーの全文。</param>
    private static (string Code, string Message) ParseErrorLine(string stderr)
    {
        var line = FirstNonEmptyLine(stderr);
        if (line.Length == 0) return (string.Empty, string.Empty);

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(line);
            var root = doc.RootElement;
            var code = root.TryGetProperty(ERROR_JSON_KEY_ERROR, out var c) ? c.GetString() ?? "" : "";
            var msg  = root.TryGetProperty(ERROR_JSON_KEY_MESSAGE, out var m) ? m.GetString() ?? "" : "";
            return (code, msg);
        }
        catch (System.Text.Json.JsonException)
        {
            // JSON でない出力（パニックのバックトレースなど）はそのまま説明として使う。
            return (string.Empty, line);
        }
    }

    /// <summary>空でない最初の行を返す（無ければ空文字）。</summary>
    /// <param name="text">対象のテキスト。</param>
    private static string FirstNonEmptyLine(string text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length > 0) return line;
        }
        return string.Empty;
    }

    /// <summary>刺さったプロセスを落とす（失敗しても無視する）。</summary>
    /// <param name="process">対象のプロセス。</param>
    private static void KillQuietly(Process process)
    {
        try { process.Kill(entireProcessTree: true); }
        catch (Exception) { /* 既に終わっている・権限が無い */ }
    }

    /// <summary>失敗の結果を作る。</summary>
    /// <param name="outcome">結末。</param>
    /// <param name="message">説明。</param>
    private static MigrateJsonResult Failure(MigrateJsonOutcome outcome, string message)
        => new(outcome, string.Empty, string.Empty, message);
}
