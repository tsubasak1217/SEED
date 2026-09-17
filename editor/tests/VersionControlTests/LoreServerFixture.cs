// ============================================================
//  LoreServerFixture.cs — 結合テスト用の使い捨て Lore サーバ
//
//  【役割】
//  一時フォルダに自己署名証明書・設定・ストアを用意し、素の loreserver.exe を
//  起動する。Dispose で必ず停止し、一時フォルダを消す。
//
//  【本番環境を絶対に触らないための約束】
//  ・ポートは 41357 / 41359（本番の 41337 / 41339 とは別）
//  ・host は 127.0.0.1 固定（Windows ファイアウォールの確認も出ない）
//  ・ストア・証明書・作業コピーはすべて %TEMP% 配下の使い捨てフォルダ
//  ・止めるのは「自分が起動したプロセス」だけ（プロセス名で探して kill しない）
//
//  【起動待ち】
//  loreserver は起動直後は待ち受けていない。HTTP ポートへ TCP 接続できるまで
//  短い間隔で試し、上限を超えたら失敗させる（固定スリープだと遅いか不安定）。
// ============================================================

using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace SEEDEditor.Tests.VersionControl;

/// <summary>
/// 一時フォルダで動く使い捨ての Lore サーバ。
/// </summary>
public sealed class LoreServerFixture : IDisposable
{
    // ── 定数（すべてここに集約する）────────────────────────

    /// <summary>loreserver.exe の場所。</summary>
    public const string SERVER_EXE_PATH = @"C:\Users\k023g\SEED_lore\bin\v0.9.0\loreserver.exe";

    /// <summary>自己署名証明書を作るのに使う openssl。</summary>
    public const string OPENSSL_PATH = @"C:\Program Files\Git\usr\bin\openssl.exe";

    /// <summary>QUIC / gRPC の待ち受けポート（本番の 41337 とは別にする）。</summary>
    public const int PORT_QUIC_GRPC = 41357;

    /// <summary>HTTP の待ち受けポート（本番の 41339 とは別にする）。</summary>
    public const int PORT_HTTP = 41359;

    /// <summary>待ち受けアドレス（ループバック固定）。</summary>
    public const string HOST = "127.0.0.1";

    /// <summary>サーバの起動を待つ上限 [ms]。</summary>
    private const int STARTUP_TIMEOUT_MS = 30_000;

    /// <summary>起動待ちの再試行間隔 [ms]。</summary>
    private const int STARTUP_POLL_INTERVAL_MS = 200;

    /// <summary>停止を待つ上限 [ms]。</summary>
    private const int SHUTDOWN_TIMEOUT_MS = 10_000;

    /// <summary>証明書生成を待つ上限 [ms]。</summary>
    private const int OPENSSL_TIMEOUT_MS = 60_000;

    /// <summary>ストアのフラッシュ間隔 [s]（テストでは短くしたいが既定に合わせる）。</summary>
    private const int STORE_FLUSH_DELAY_SECONDS = 1;

    /// <summary>一時フォルダ名の接頭辞。</summary>
    private const string TEMP_PREFIX = "SEED_VcsIntegration_";

    // ── 状態 ────────────────────────────────────────────────

    /// <summary>この実行専用の一時フォルダ（配下を全部使う）。</summary>
    public string RootDir { get; }

    /// <summary>作業コピーを作る親フォルダ。</summary>
    public string WorkDir { get; }

    /// <summary>起動したサーバのプロセス。</summary>
    private readonly Process _process;

    /// <summary>多重 Dispose を防ぐ印。</summary>
    private bool _disposed;

    /// <summary>
    /// サーバを起動する。起動できなければ例外を投げる（テストが失敗する）。
    /// </summary>
    public LoreServerFixture()
    {
        if (!File.Exists(SERVER_EXE_PATH))
            throw new FileNotFoundException($"loreserver が見つかりません: {SERVER_EXE_PATH}");
        if (!File.Exists(OPENSSL_PATH))
            throw new FileNotFoundException($"openssl が見つかりません: {OPENSSL_PATH}");

        RootDir = Path.Combine(Path.GetTempPath(), TEMP_PREFIX + Guid.NewGuid().ToString("N"));
        WorkDir = Path.Combine(RootDir, "work");

        var configDir = Path.Combine(RootDir, "config");
        var certsDir  = Path.Combine(RootDir, "certs");
        var storeDir  = Path.Combine(RootDir, "store");

        Directory.CreateDirectory(WorkDir);
        Directory.CreateDirectory(configDir);
        Directory.CreateDirectory(certsDir);
        Directory.CreateDirectory(Path.Combine(storeDir, "immutable"));
        Directory.CreateDirectory(Path.Combine(storeDir, "mutable"));

        CreateSelfSignedCertificate(certsDir);
        WriteServerConfig(configDir, certsDir, storeDir);

        _process = StartServer(configDir);
        WaitUntilListening();
    }

    /// <summary>
    /// リポジトリの URL を組み立てる。
    /// </summary>
    /// <param name="repositoryName">リポジトリ名。</param>
    public static string RepositoryUrl(string repositoryName)
        => $"lore://{HOST}:{PORT_QUIC_GRPC}/{repositoryName}";

    /// <summary>
    /// 作業コピー用のフォルダを作って絶対パスを返す。
    /// </summary>
    /// <param name="name">フォルダ名。</param>
    public string CreateWorkingCopyDir(string name)
    {
        var dir = Path.Combine(WorkDir, name);
        Directory.CreateDirectory(dir);
        return dir;
    }

    // ── 準備 ────────────────────────────────────────────────

    /// <summary>
    /// QUIC 用の自己署名証明書を作る。
    /// </summary>
    /// <param name="certsDir">出力先フォルダ。</param>
    private static void CreateSelfSignedCertificate(string certsDir)
    {
        var certPath = Path.Combine(certsDir, "cert.pem");
        var keyPath  = Path.Combine(certsDir, "key.pem");

        // localhost と 127.0.0.1 の両方を SAN に入れる
        // （クライアントがどちらで繋いでも検証できるように）。
        var arguments =
            "req -x509 -newkey rsa:2048 -nodes -days 365 " +
            $"-keyout \"{keyPath}\" -out \"{certPath}\" " +
            "-subj \"/CN=localhost\" " +
            "-addext \"subjectAltName=DNS:localhost,IP:127.0.0.1\"";

        var startInfo = new ProcessStartInfo(OPENSSL_PATH, arguments)
        {
            UseShellExecute        = false,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            CreateNoWindow         = true,
        };

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("openssl を起動できませんでした。");

        var stdErr = process.StandardError.ReadToEnd();
        if (!process.WaitForExit(OPENSSL_TIMEOUT_MS))
        {
            try { process.Kill(entireProcessTree: true); } catch { /* 停止失敗は無視 */ }
            throw new TimeoutException("openssl が時間内に終わりませんでした。");
        }

        if (process.ExitCode != 0)
            throw new InvalidOperationException($"証明書を作れませんでした: {stdErr}");

        if (!File.Exists(certPath) || !File.Exists(keyPath))
            throw new FileNotFoundException("証明書ファイルが生成されませんでした。");
    }

    /// <summary>
    /// サーバ設定（local.toml）を書く。
    /// </summary>
    /// <param name="configDir">設定フォルダ。</param>
    /// <param name="certsDir">証明書フォルダ。</param>
    /// <param name="storeDir">ストアフォルダ。</param>
    private static void WriteServerConfig(string configDir, string certsDir, string storeDir)
    {
        // TOML のパス区切りはスラッシュに統一する（円記号はエスケープが要るため）。
        static string Toml(string path) => path.Replace('\\', '/');

        var builder = new StringBuilder();
        builder.AppendLine("# SEED バージョン管理 結合テスト用の使い捨てサーバ設定");
        builder.AppendLine("# 本番（41337 / 41339）とは別のポートを使う。");
        builder.AppendLine();
        builder.AppendLine("[server.quic]");
        builder.AppendLine($"host = \"{HOST}\"");
        builder.AppendLine(
            $"port = {PORT_QUIC_GRPC.ToString(CultureInfo.InvariantCulture)}");
        builder.AppendLine();
        builder.AppendLine("[server.grpc]");
        builder.AppendLine($"host = \"{HOST}\"");
        builder.AppendLine(
            $"port = {PORT_QUIC_GRPC.ToString(CultureInfo.InvariantCulture)}");
        builder.AppendLine();
        builder.AppendLine("[server.http]");
        builder.AppendLine($"host = \"{HOST}\"");
        builder.AppendLine($"port = {PORT_HTTP.ToString(CultureInfo.InvariantCulture)}");
        builder.AppendLine();
        builder.AppendLine("[server.quic.certificate]");
        builder.AppendLine($"cert_file = \"{Toml(Path.Combine(certsDir, "cert.pem"))}\"");
        builder.AppendLine($"pkey_file = \"{Toml(Path.Combine(certsDir, "key.pem"))}\"");
        builder.AppendLine();
        builder.AppendLine("[immutable_store.local]");
        builder.AppendLine($"path = \"{Toml(Path.Combine(storeDir, "immutable"))}\"");
        builder.AppendLine(
            $"flush_delay_seconds = {STORE_FLUSH_DELAY_SECONDS.ToString(CultureInfo.InvariantCulture)}");
        builder.AppendLine();
        builder.AppendLine("[mutable_store.local]");
        builder.AppendLine($"path = \"{Toml(Path.Combine(storeDir, "mutable"))}\"");
        builder.AppendLine(
            $"flush_delay_seconds = {STORE_FLUSH_DELAY_SECONDS.ToString(CultureInfo.InvariantCulture)}");

        File.WriteAllText(Path.Combine(configDir, "local.toml"), builder.ToString());
    }

    /// <summary>
    /// サーバを起動する。
    /// </summary>
    /// <param name="configDir">設定フォルダ。</param>
    private Process StartServer(string configDir)
    {
        var startInfo = new ProcessStartInfo(SERVER_EXE_PATH, $"--config \"{configDir}\"")
        {
            UseShellExecute        = false,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            CreateNoWindow         = true,
            WorkingDirectory       = RootDir,
        };
        // RUST_LOG を付けないとログが一切出ない（失敗時の原因追跡ができない）。
        startInfo.Environment["RUST_LOG"] = "info";

        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("loreserver を起動できませんでした。");

        // 出力を読み捨てる。読まないとパイプが詰まってサーバが止まる。
        var logPath = Path.Combine(RootDir, "server.log");
        StartDrain(process.StandardOutput, logPath);
        StartDrain(process.StandardError, logPath);

        return process;
    }

    /// <summary>
    /// 標準出力・標準エラーを読み続けてファイルへ落とす。
    /// </summary>
    /// <param name="reader">読み取り元。</param>
    /// <param name="logPath">書き出し先。</param>
    private static void StartDrain(StreamReader reader, string logPath)
    {
        var thread = new Thread(() =>
        {
            try
            {
                while (reader.ReadLine() is { } line)
                {
                    // 追記なので 2 本のスレッドから来ても行が混ざるだけで壊れない。
                    try { File.AppendAllText(logPath, line + Environment.NewLine); }
                    catch { /* ログの書き出し失敗は無視 */ }
                }
            }
            catch { /* プロセス終了時の読み取り失敗は無視 */ }
        })
        {
            IsBackground = true,
            Name         = "lore server log drain",
        };
        thread.Start();
    }

    /// <summary>
    /// サーバが待ち受けを始めるまで待つ。
    /// </summary>
    private void WaitUntilListening()
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(STARTUP_TIMEOUT_MS);

        while (DateTime.UtcNow < deadline)
        {
            // 起動に失敗して即終了していたら、待っても無駄なので早く失敗させる。
            if (_process.HasExited)
            {
                throw new InvalidOperationException(
                    $"loreserver が終了しました（終了コード {_process.ExitCode}）。"
                    + $"ログ: {Path.Combine(RootDir, "server.log")}");
            }

            if (CanConnect(PORT_HTTP) && CanConnect(PORT_QUIC_GRPC)) return;

            Thread.Sleep(STARTUP_POLL_INTERVAL_MS);
        }

        throw new TimeoutException(
            $"loreserver が {STARTUP_TIMEOUT_MS} ms 以内に待ち受けを始めませんでした。"
            + $"ログ: {Path.Combine(RootDir, "server.log")}");
    }

    /// <summary>指定ポートへ TCP 接続できるか試す。</summary>
    /// <param name="port">ポート番号。</param>
    private static bool CanConnect(int port)
    {
        try
        {
            using var client = new TcpClient();
            var async = client.BeginConnect(HOST, port, null, null);
            if (!async.AsyncWaitHandle.WaitOne(STARTUP_POLL_INTERVAL_MS)) return false;
            client.EndConnect(async);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// サーバを止め、一時フォルダを消す。
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // 自分が起動したプロセスだけを止める（プロセス名で探して kill しない）。
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit(SHUTDOWN_TIMEOUT_MS);
            }
        }
        catch (Exception) { /* 既に終了している場合など */ }

        try { _process.Dispose(); } catch { /* 後始末の失敗は無視 */ }

        // ストアの後始末。消えなくてもテストの結果には影響しない（%TEMP% 配下）。
        try { Directory.Delete(RootDir, recursive: true); } catch { /* 無視 */ }
    }
}
