// ============================================================
//  AccountsServerFixture.cs — SEED アカウントの結合テスト用「使い捨て実サーバ」
//
//  【役割】
//  一時フォルダに自己署名証明書・設定・ストア・アカウントデータを用意し、
//  **リポジトリ内でビルドした** `seed-loreserver.exe` を起動する。
//  契約 `docs/seed_accounts.md` 5 章「有効化の順番」をそのまま再現できるよう、
//  次の 2 段階を切り替えられる。
//
//    第 1 段: `[seed_auth]` だけ有効（発行窓口は動くが Lore は匿名）
//    第 2 段: `[server.auth]` / `[server.auth.jwk]` / `[environment.endpoint]` を足す
//             （Lore 本体が JWT を要求する。新規リポジトリは作れなくなる）
//
//  【本番環境を絶対に触らないための約束】
//  ・ポートは Lore 41357 / 41359、発行窓口 41361
//    （本番の 41337 / 41339 / 41350 とは別。本番サーバは常駐している）
//  ・host は 127.0.0.1 固定（Windows ファイアウォールの確認も出ない）
//  ・ストア・証明書・アカウントデータ・作業コピーはすべて使い捨てフォルダ
//  ・止めるのは「自分が起動したプロセス」だけ（プロセス名で探して kill しない）
//
//  【VersionControlTests との同居について】
//  `VersionControlTests/LoreServerFixture` も 41357 / 41359 を使う。
//  **2 つのテストを同時に走らせないこと**（順に走らせれば衝突しない）。
//
//  【起動待ち】
//  seed-loreserver は起動直後は待ち受けていない。各ポートへ TCP 接続できるまで
//  短い間隔で試し、上限を超えたら失敗させる（固定スリープだと遅いか不安定）。
// ============================================================

using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace SEEDEditor.Tests.Accounts;

/// <summary>
/// 一時フォルダで動く使い捨ての seed-loreserver（発行窓口つき）。
/// </summary>
public sealed class AccountsServerFixture : IDisposable
{
    // ── 外部プログラムの場所 ────────────────────────────────

    /// <summary>リポジトリルートを見つけるための目印（このファイルが在る場所がルート）。</summary>
    private const string REPO_ROOT_MARKER = @"tools\seed-loreserver\Cargo.toml";

    /// <summary>リポジトリルートからの seed-loreserver.exe の相対パス。</summary>
    private const string SERVER_EXE_RELATIVE = @"tools\seed-loreserver\target\debug\seed-loreserver.exe";

    /// <summary>自己署名証明書を作るのに使う openssl。</summary>
    public const string OPENSSL_PATH = @"C:\Program Files\Git\usr\bin\openssl.exe";

    // ── ポート（本番と必ずずらす）────────────────────────────

    /// <summary>QUIC / gRPC の待ち受けポート（本番の 41337 とは別）。</summary>
    public const int PORT_QUIC_GRPC = 41357;

    /// <summary>HTTP の待ち受けポート（本番の 41339 とは別）。</summary>
    public const int PORT_HTTP = 41359;

    /// <summary>発行窓口の待ち受けポート（本番の 41350 とは別）。</summary>
    public const int PORT_AUTH = 41361;

    /// <summary>待ち受けアドレス（ループバック固定）。</summary>
    public const string HOST = "127.0.0.1";

    // ── 設定値 ──────────────────────────────────────────────

    /// <summary>JWT の発行者（`[seed_auth] issuer` と `[server.auth] jwt_issuer` を揃える）。</summary>
    public const string JWT_ISSUER = "seed-auth";

    /// <summary>JWT の対象（`[seed_auth] audience` と `[server.auth] jwt_audience` を揃える）。</summary>
    public const string JWT_AUDIENCE = "seed-lore";

    /// <summary>
    /// トークンの有効期間 [hour]。契約 5 章の下限が 1 なので、
    /// 結合テストで取りうる最短にする（実時間では失効を待てない）。
    /// </summary>
    private const int TOKEN_TTL_HOURS = 1;

    /// <summary>
    /// `[environment.endpoint] auth_url` に入れる値。
    /// クライアントはトークンを渡された時点で交換を短絡するので、
    /// **この URL へ接続しにはいかない**（空でなければ何でもよい）。
    /// </summary>
    private const string AUTH_URL_PLACEHOLDER = "https://seed-auth.invalid";

    /// <summary>ストアのフラッシュ間隔 [s]（テストなので短くする）。</summary>
    private const int STORE_FLUSH_DELAY_SECONDS = 1;

    // ── 待ち時間 ────────────────────────────────────────────

    /// <summary>サーバの起動を待つ上限 [ms]。</summary>
    private const int STARTUP_TIMEOUT_MS = 30_000;

    /// <summary>起動待ちの再試行間隔 [ms]。</summary>
    private const int STARTUP_POLL_INTERVAL_MS = 200;

    /// <summary>停止を待つ上限 [ms]。</summary>
    private const int SHUTDOWN_TIMEOUT_MS = 10_000;

    /// <summary>証明書生成を待つ上限 [ms]。</summary>
    private const int OPENSSL_TIMEOUT_MS = 60_000;

    /// <summary>
    /// mutable ストアの遅延書き出しが終わるのを待つ上限 [ms]。
    /// **これを待たずに止めるとリポジトリ名 → ID の対応が失われる**（下の解説を参照）。
    /// </summary>
    private const int FLUSH_WAIT_TIMEOUT_MS = 20_000;

    /// <summary>書き出し待ちの再試行間隔 [ms]。</summary>
    private const int FLUSH_POLL_INTERVAL_MS = 500;

    /// <summary>
    /// 書き出しが「落ち着いた」と判断するまでに必要な、変化が無い連続回数。
    /// 1 回だけ一致しても、直後にもう 1 バケットが書かれることがある。
    /// </summary>
    private const int FLUSH_STABLE_POLL_COUNT = 2;

    // ── フォルダ・ファイル名 ────────────────────────────────

    /// <summary>設定フォルダ名。</summary>
    private const string DIR_CONFIG = "config";

    /// <summary>証明書フォルダ名。</summary>
    private const string DIR_CERTS = "certs";

    /// <summary>ストアフォルダ名。</summary>
    private const string DIR_STORE = "store";

    /// <summary>発行窓口のデータフォルダ名（accounts.json / issuer_key.json / jwks.json）。</summary>
    private const string DIR_AUTH_DATA = "auth";

    /// <summary>作業コピーを作る親フォルダ名。</summary>
    private const string DIR_WORK = "work";

    /// <summary>ロックの保存先ファイル名。</summary>
    private const string FILE_LOCKS = "seed_locks.json";

    /// <summary>設定ファイル名（`<env>` の既定が local なのでこの名前）。</summary>
    private const string FILE_CONFIG = "local.toml";

    /// <summary>サーバのログ出力先ファイル名。</summary>
    private const string FILE_SERVER_LOG = "server.log";

    /// <summary>JWKS ファイル名（Lore 本体が `file://` で読む）。</summary>
    private const string FILE_JWKS = "jwks.json";

    /// <summary>参加者データのファイル名（招待コードの平文が残っていないことを確かめる）。</summary>
    private const string FILE_ACCOUNTS = "accounts.json";

    /// <summary>一時フォルダ名の接頭辞。</summary>
    private const string TEMP_PREFIX = "accounts_server";

    /// <summary>
    /// 一時フォルダを消さずに残す環境変数（失敗したときにサーバのログを読むため）。
    /// **残ったフォルダには証明書とサーバの署名鍵が入る**ので、確認したら消すこと。
    /// </summary>
    public const string ENV_KEEP_TEMP = "SEED_ACCOUNTS_TEST_KEEP";

    // ── 状態 ────────────────────────────────────────────────

    /// <summary>この実行専用の一時フォルダ（配下を全部使う）。</summary>
    public string RootDir { get; }

    /// <summary>作業コピーを作る親フォルダ。</summary>
    public string WorkDir { get; }

    /// <summary>発行窓口のデータフォルダ。</summary>
    public string AuthDataDir { get; }

    /// <summary>サーバのログファイル。</summary>
    public string ServerLogPath { get; }

    /// <summary>seed-loreserver.exe の絶対パス。</summary>
    public string ServerExePath { get; }

    /// <summary>いま Lore 本体の JWT 認証が有効か（第 2 段かどうか）。</summary>
    public bool LoreAuthEnabled { get; private set; }

    /// <summary>いま `[environment.endpoint] auth_url` を書いているか。</summary>
    public bool AuthUrlConfigured { get; private set; }

    /// <summary>設定フォルダ。</summary>
    private readonly string _configDir;

    /// <summary>証明書フォルダ。</summary>
    private readonly string _certsDir;

    /// <summary>ストアフォルダ。</summary>
    private readonly string _storeDir;

    /// <summary>ロックの保存先ファイル。</summary>
    private readonly string _locksPath;

    /// <summary>起動したサーバのプロセス（停止中は null）。</summary>
    private Process? _process;

    /// <summary>多重 Dispose を防ぐ印。</summary>
    private bool _disposed;

    // ── 生成 ────────────────────────────────────────────────

    /// <summary>
    /// 一時フォルダを用意し、第 1 段（`[seed_auth]` のみ・Lore は匿名）で起動する。
    /// </summary>
    public AccountsServerFixture()
    {
        ServerExePath = ResolveServerExePath();

        if (!File.Exists(ServerExePath))
        {
            throw new FileNotFoundException(
                $"seed-loreserver が見つかりません: {ServerExePath}"
                + "（`cd tools/seed-loreserver && cargo build` を先に実行してください）");
        }
        if (!File.Exists(OPENSSL_PATH))
            throw new FileNotFoundException($"openssl が見つかりません: {OPENSSL_PATH}");

        RootDir       = TestPaths.NewDirectory(TEMP_PREFIX);
        WorkDir       = Path.Combine(RootDir, DIR_WORK);
        AuthDataDir   = Path.Combine(RootDir, DIR_AUTH_DATA);
        ServerLogPath = Path.Combine(RootDir, FILE_SERVER_LOG);

        _configDir = Path.Combine(RootDir, DIR_CONFIG);
        _certsDir  = Path.Combine(RootDir, DIR_CERTS);
        _storeDir  = Path.Combine(RootDir, DIR_STORE);
        _locksPath = Path.Combine(RootDir, FILE_LOCKS);

        Directory.CreateDirectory(WorkDir);
        Directory.CreateDirectory(AuthDataDir);
        Directory.CreateDirectory(_configDir);
        Directory.CreateDirectory(_certsDir);
        Directory.CreateDirectory(Path.Combine(_storeDir, "immutable"));
        Directory.CreateDirectory(Path.Combine(_storeDir, "mutable"));

        CreateSelfSignedCertificate(_certsDir);

        Start(loreAuthEnabled: false, includeAuthUrl: false);
    }

    // ── 参照 ────────────────────────────────────────────────

    /// <summary>発行窓口の URL（`http://127.0.0.1:41361`）。</summary>
    public Uri AuthBaseAddress
        => new($"http://{HOST}:{PORT_AUTH.ToString(CultureInfo.InvariantCulture)}");

    /// <summary>JWKS ファイルの絶対パス。</summary>
    public string JwksPath => Path.Combine(AuthDataDir, FILE_JWKS);

    /// <summary>参加者データ（accounts.json）の絶対パス。</summary>
    public string AccountsJsonPath => Path.Combine(AuthDataDir, FILE_ACCOUNTS);

    /// <summary>ロックの保存先 JSON の絶対パス。</summary>
    public string LocksJsonPath => _locksPath;

    /// <summary>
    /// リポジトリの URL を組み立てる。
    /// </summary>
    /// <param name="repositoryName">リポジトリ名。</param>
    public static string RepositoryUrl(string repositoryName)
        => $"lore://{HOST}:{PORT_QUIC_GRPC.ToString(CultureInfo.InvariantCulture)}/{repositoryName}";

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

    /// <summary>
    /// クローン先に使う（まだ作らない）フォルダの絶対パスを返す。
    /// </summary>
    /// <param name="name">フォルダ名。</param>
    public string ReserveWorkingCopyDir(string name) => Path.Combine(WorkDir, name);

    // ── 段階の切り替え ──────────────────────────────────────

    /// <summary>
    /// 契約 5 章の第 2 段へ進む。サーバを止め、
    /// `[server.auth]` / `[server.auth.jwk]` / `[environment.endpoint]` を足して起動し直す。
    /// ストア・ロック・参加者データはそのまま引き継ぐ。
    /// </summary>
    /// <param name="includeAuthUrl">
    /// `[environment.endpoint] auth_url` を書くか。
    /// **push / pull（QUIC ストレージセッション）にはこれが必須**だが、
    /// リポジトリ ID が確定していない呼び出し（clone / repository create）は
    /// これがあると通らない。切り分けのために切り替えられるようにしてある。
    /// </param>
    public void RestartWithLoreAuth(bool includeAuthUrl = true)
    {
        StopProcess();
        Start(loreAuthEnabled: true, includeAuthUrl);
    }

    // ── 起動・停止 ──────────────────────────────────────────

    /// <summary>
    /// 設定を書き出してサーバを起動し、待ち受け開始まで待つ。
    /// </summary>
    /// <param name="loreAuthEnabled">Lore 本体の JWT 認証を有効にするか。</param>
    /// <param name="includeAuthUrl">`[environment.endpoint] auth_url` を書くか。</param>
    private void Start(bool loreAuthEnabled, bool includeAuthUrl)
    {
        WriteServerConfig(loreAuthEnabled, includeAuthUrl);

        var startInfo = new ProcessStartInfo(ServerExePath, $"--config \"{_configDir}\"")
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
            ?? throw new InvalidOperationException("seed-loreserver を起動できませんでした。");

        // 出力を読み捨てる。読まないとパイプが詰まってサーバが止まる。
        StartDrain(process.StandardOutput, ServerLogPath);
        StartDrain(process.StandardError, ServerLogPath);

        _process         = process;
        LoreAuthEnabled  = loreAuthEnabled;
        AuthUrlConfigured = loreAuthEnabled && includeAuthUrl;

        WaitUntilListening();
    }

    /// <summary>
    /// 起動したサーバだけを止める（プロセス名で探して kill しない）。
    ///
    /// <para>
    /// ★止める前に **mutable ストアの遅延書き出しを待つ**。
    /// Lore のローカル mutable ストアは、書き込みの
    /// <c>flush_delay_seconds</c> 秒後に別タスクでファイルへ落とす
    /// （`lore-storage/src/local/mutable_store.rs` の `flush_delayed`）。
    /// mutable ストアには **リポジトリ名 → ID の対応とブランチの先端**が入るので、
    /// 落ちる前に強制終了すると、次の起動で
    /// **そのリポジトリを名前で引けなくなる**（`RepositoryGet` が
    /// grpc NOT_FOUND "Repository &lt;名前&gt; not found" を返し、clone が
    /// 「Not found」で失敗する）。immutable ストア側は残るので、
    /// 「データはあるのにクローンだけできない」という分かりにくい壊れ方になる。
    /// </para>
    /// </summary>
    private void StopProcess()
    {
        var process = _process;
        _process = null;
        if (process is null) return;

        WaitForMutableStoreFlush();

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(SHUTDOWN_TIMEOUT_MS);
            }
        }
        catch (Exception) { /* 既に終了している場合など */ }

        try { process.Dispose(); } catch { /* 後始末の失敗は無視 */ }

        // 次の起動でポートを掴めるよう、待ち受けが解けるまで待つ。
        WaitUntilPortsFree();
    }

    // ── 準備 ────────────────────────────────────────────────

    /// <summary>
    /// このアセンブリの置き場から上へ辿ってリポジトリルートを探し、
    /// seed-loreserver.exe の絶対パスを返す。
    /// </summary>
    private static string ResolveServerExePath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, REPO_ROOT_MARKER)))
                return Path.Combine(dir.FullName, SERVER_EXE_RELATIVE);

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException(
            $"リポジトリルートを見つけられませんでした（目印: {REPO_ROOT_MARKER}）。"
            + $" 探索の起点: {AppContext.BaseDirectory}");
    }

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
    /// <param name="loreAuthEnabled">Lore 本体の JWT 認証を有効にするか（契約 5 章の第 2 段）。</param>
    /// <param name="includeAuthUrl">`[environment.endpoint] auth_url` を書くか。</param>
    private void WriteServerConfig(bool loreAuthEnabled, bool includeAuthUrl)
    {
        // TOML のパス区切りはスラッシュに統一する（円記号はエスケープが要るため）。
        static string Toml(string path) => path.Replace('\\', '/');

        static string Port(int value) => value.ToString(CultureInfo.InvariantCulture);

        var builder = new StringBuilder();
        builder.AppendLine("# SEED アカウント 結合テスト用の使い捨てサーバ設定");
        builder.AppendLine("# 本番（Lore 41337 / 41339、窓口 41350）とは別のポートを使う。");
        builder.AppendLine();
        builder.AppendLine("[server.quic]");
        builder.AppendLine($"host = \"{HOST}\"");
        builder.AppendLine($"port = {Port(PORT_QUIC_GRPC)}");
        builder.AppendLine();
        builder.AppendLine("[server.grpc]");
        builder.AppendLine($"host = \"{HOST}\"");
        builder.AppendLine($"port = {Port(PORT_QUIC_GRPC)}");
        builder.AppendLine();
        builder.AppendLine("[server.http]");
        builder.AppendLine($"host = \"{HOST}\"");
        builder.AppendLine($"port = {Port(PORT_HTTP)}");
        builder.AppendLine();
        builder.AppendLine("[server.quic.certificate]");
        builder.AppendLine($"cert_file = \"{Toml(Path.Combine(_certsDir, "cert.pem"))}\"");
        builder.AppendLine($"pkey_file = \"{Toml(Path.Combine(_certsDir, "key.pem"))}\"");
        builder.AppendLine();
        builder.AppendLine("[immutable_store.local]");
        builder.AppendLine($"path = \"{Toml(Path.Combine(_storeDir, "immutable"))}\"");
        builder.AppendLine($"flush_delay_seconds = {Port(STORE_FLUSH_DELAY_SECONDS)}");
        builder.AppendLine();
        builder.AppendLine("[mutable_store.local]");
        builder.AppendLine($"path = \"{Toml(Path.Combine(_storeDir, "mutable"))}\"");
        builder.AppendLine($"flush_delay_seconds = {Port(STORE_FLUSH_DELAY_SECONDS)}");
        builder.AppendLine();
        // ロックはファイル永続化プラグイン（本番と同じ構成）。
        // 再起動をまたいでロックが残ること・owner が誰かをファイルでも確かめられる。
        builder.AppendLine("[lock_store]");
        builder.AppendLine("mode = \"seed_file_lock_store\"");
        builder.AppendLine();
        builder.AppendLine("[plugins.seed_file_lock_store]");
        builder.AppendLine($"path = \"{Toml(_locksPath)}\"");
        builder.AppendLine();
        builder.AppendLine("[seed_auth]");
        builder.AppendLine("enabled = true");
        builder.AppendLine($"host = \"{HOST}\"");
        builder.AppendLine($"port = {Port(PORT_AUTH)}");
        builder.AppendLine($"data_dir = \"{Toml(AuthDataDir)}\"");
        builder.AppendLine($"issuer = \"{JWT_ISSUER}\"");
        builder.AppendLine($"audience = \"{JWT_AUDIENCE}\"");
        builder.AppendLine($"token_ttl_hours = {Port(TOKEN_TTL_HOURS)}");

        if (loreAuthEnabled)
        {
            builder.AppendLine();
            builder.AppendLine("# ── 契約 5 章の第 2 段: ここから匿名アクセスは一切できなくなる ──");
            builder.AppendLine("[server.auth]");
            builder.AppendLine($"jwt_issuer = \"{JWT_ISSUER}\"");
            builder.AppendLine($"jwt_audience = [\"{JWT_AUDIENCE}\"]");
            builder.AppendLine();
            builder.AppendLine("[server.auth.jwk]");
            builder.AppendLine($"endpoint = \"file:///{Toml(JwksPath)}\"");

            if (includeAuthUrl)
            {
                builder.AppendLine();
                // push / pull（QUIC ストレージセッション）にトークンを載せるのに必須。
                // この URL へ接続しにはいかない（トークンがあれば交換を短絡するため）。
                builder.AppendLine("[environment.endpoint]");
                builder.AppendLine($"auth_url = \"{AUTH_URL_PLACEHOLDER}\"");
            }
        }

        File.WriteAllText(Path.Combine(_configDir, FILE_CONFIG), builder.ToString());
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
            Name         = "seed lore server log drain",
        };
        thread.Start();
    }

    /// <summary>
    /// サーバが待ち受けを始めるまで待つ（Lore の 2 ポートと発行窓口の 3 つとも）。
    /// </summary>
    private void WaitUntilListening()
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(STARTUP_TIMEOUT_MS);

        while (DateTime.UtcNow < deadline)
        {
            // 起動に失敗して即終了していたら、待っても無駄なので早く失敗させる。
            if (_process is { HasExited: true })
            {
                throw new InvalidOperationException(
                    $"seed-loreserver が終了しました（終了コード {_process.ExitCode}）。"
                    + $" ログ: {ServerLogPath}");
            }

            if (CanConnect(PORT_HTTP) && CanConnect(PORT_QUIC_GRPC) && CanConnect(PORT_AUTH))
                return;

            Thread.Sleep(STARTUP_POLL_INTERVAL_MS);
        }

        throw new TimeoutException(
            $"seed-loreserver が {STARTUP_TIMEOUT_MS} ms 以内に待ち受けを始めませんでした。"
            + $" ログ: {ServerLogPath}");
    }

    /// <summary>
    /// mutable ストアの遅延書き出しが落ち着くまで待つ。
    /// 「ファイル数と合計サイズ」が変わらなくなったら書き出し済みとみなす。
    /// 上限まで待っても落ち着かなければ、そのまま進む（テストの失敗として現れる）。
    /// </summary>
    private void WaitForMutableStoreFlush()
    {
        var mutableDir = Path.Combine(_storeDir, "mutable");
        var deadline   = DateTime.UtcNow.AddMilliseconds(FLUSH_WAIT_TIMEOUT_MS);

        var previous = MeasureTree(mutableDir);
        var stable   = 0;

        while (DateTime.UtcNow < deadline)
        {
            Thread.Sleep(FLUSH_POLL_INTERVAL_MS);

            var current = MeasureTree(mutableDir);
            if (current == previous)
            {
                // 空のまま動かない場合も「これ以上は書かれない」とみなして進む。
                if (++stable >= FLUSH_STABLE_POLL_COUNT) return;
            }
            else
            {
                stable = 0;
            }
            previous = current;
        }
    }

    /// <summary>
    /// フォルダ配下の「ファイル数と合計サイズ」を返す（書き出しの進みを見るため）。
    /// </summary>
    /// <param name="dir">対象フォルダ。</param>
    private static (int Count, long Bytes) MeasureTree(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return (0, 0);

            var files = Directory.GetFiles(dir, "*", SearchOption.AllDirectories);
            long total = 0;
            foreach (var file in files)
            {
                try { total += new FileInfo(file).Length; }
                catch (Exception) { /* 書き込み中に消える一時ファイルは無視 */ }
            }
            return (files.Length, total);
        }
        catch (Exception)
        {
            return (0, 0);
        }
    }

    /// <summary>
    /// 3 つのポートが解放されるまで待つ（再起動のたびに「使用中」で落ちないように）。
    /// </summary>
    private static void WaitUntilPortsFree()
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(SHUTDOWN_TIMEOUT_MS);

        while (DateTime.UtcNow < deadline)
        {
            if (!CanConnect(PORT_HTTP) && !CanConnect(PORT_QUIC_GRPC) && !CanConnect(PORT_AUTH))
                return;

            Thread.Sleep(STARTUP_POLL_INTERVAL_MS);
        }
        // 解放を待ちきれなくても、次の起動待ちで失敗として現れるのでここでは投げない。
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
    /// サーバのログを読む（失敗時の診断に使う。秘密は載っていない）。
    /// </summary>
    public string ReadServerLog()
    {
        try
        {
            if (!File.Exists(ServerLogPath)) return string.Empty;

            // サーバが書いている最中でも読めるように共有指定で開く。
            using var stream = new FileStream(
                ServerLogPath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// サーバを止め、一時フォルダを消す。
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        StopProcess();

        // 失敗を追うときはログを残したいので、環境変数で消さずに置ける。
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ENV_KEEP_TEMP)))
        {
            Console.WriteLine($"[{ENV_KEEP_TEMP}] が設定されているため一時フォルダを残します: {RootDir}");
            return;
        }

        // 後始末。消えなくてもテストの結果には影響しない（使い捨てフォルダ配下）。
        try { Directory.Delete(RootDir, recursive: true); } catch { /* 無視 */ }
    }
}
