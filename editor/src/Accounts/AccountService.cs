// ============================================================
//  AccountService.cs — アカウント機能のプロセス唯一の入口
//
//  【役割】
//  ・保存されたアカウントを 1 つだけ読み、プロセス中ずっと保持する
//  ・プロジェクトを開いたら、窓口を探して自動ログインする
//  ・Lore へ渡す資格情報（トークンと名前）を配る
//
//  【なぜ static なのか】
//  VersionControlService と同じ理由。アカウントもプロジェクトも
//  1 プロセスに 1 つで、Hub の画面・パネル・バージョン管理の層は
//  DI の経路を持たない。同じ「プロセスに 1 つの静的な入口」に揃える。
//
//  【依存の向き（重要）】
//  Accounts → VersionControl の一方通行にする。
//  バージョン管理の層は <c>VersionControlService.CredentialProvider</c> という
//  1 つの関数しか知らず、アカウントの型を一切参照しない。
//  こうしておけば、アカウント機能を外しても（差し込まなければ）
//  バージョン管理は今までどおり匿名で動く。
//
//  【匿名で動くこと（壊さない約束）】
//  窓口が居ない・アカウントが無い・参加していない、のいずれでも
//  **例外を投げず、匿名のまま** 進む。ログインはあくまで上乗せの機能。
//
//  【スレッド】
//  <see cref="AuthStateChanged"/> は **UI スレッドではない**ところから飛ぶ。
//  購読側（パネル・Hub）が Dispatcher へ移すこと。
//
//  【依存】
//  WPF には依存しない。
// ============================================================

using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using SEEDEditor.Accounts.Http;
using SEEDEditor.Accounts.Model;
using SEEDEditor.Accounts.Storage;
using SEEDEditor.VersionControl;
using SEEDEditor.VersionControl.Lore.Backend;

namespace SEEDEditor.Accounts;

/// <summary>
/// アカウント機能のライフサイクル（プロセスに 1 つ）。
/// </summary>
public static class AccountService
{
    /// <summary>状態を触るときのロック。</summary>
    private static readonly object Gate = new();

    /// <summary>トークンの取得と自動更新（プロセスに 1 つ）。</summary>
    private static readonly AccountSessionManager Sessions = new(AccountSettings.Default, WriteLog);

    /// <summary>読み込み済みのアカウント（未作成なら null）。</summary>
    private static SeedAccount? _account;

    /// <summary>アカウントの保管。</summary>
    private static IAccountStore _store = new AccountFileStore();

    /// <summary>利用者設定（窓口の上書き・自動ログインの可否）。</summary>
    private static AccountEditorSettings _editorSettings = new();

    /// <summary>エディタ設定フォルダ（保存に使う）。</summary>
    private static string _settingsDir = string.Empty;

    /// <summary>アカウントの読み込みで起きた失敗（画面に出す。無ければ空）。</summary>
    private static string _loadError = string.Empty;

    /// <summary>診断ログの出力先（秘密は書かない）。</summary>
    public static Action<string>? Log { get; set; }

    /// <summary>現在のログイン状態。</summary>
    public static AccountAuthState AuthState => Sessions.State;

    /// <summary>
    /// ログイン状態が変わったときに発火する。
    /// **UI スレッドではない**ところから飛ぶ。
    /// </summary>
    public static event EventHandler<AccountAuthState>? AuthStateChanged
    {
        add    => Sessions.StateChanged += value;
        remove => Sessions.StateChanged -= value;
    }

    /// <summary>読み込み済みアカウントの身元（未作成なら <see cref="AccountIdentity.None"/>）。</summary>
    public static AccountIdentity Identity
    {
        get { lock (Gate) { return _account?.Identity ?? AccountIdentity.None; } }
    }

    /// <summary>アカウントがあるか。</summary>
    public static bool HasAccount
    {
        get { lock (Gate) { return _account is not null; } }
    }

    /// <summary>アカウントの読み込みで起きた失敗（無ければ空）。</summary>
    public static string LoadError
    {
        get { lock (Gate) { return _loadError; } }
    }

    /// <summary>アカウントファイルの置き場（画面の案内に使う）。</summary>
    public static string AccountFilePath => _store.FilePath;

    /// <summary>現在のセッション（未ログインなら null）。招待発行などに使う。</summary>
    public static AuthSession? CurrentSession => Sessions.CurrentSession;

    // ── 初期化 ──────────────────────────────────────────────

    /// <summary>
    /// アプリ起動時に 1 度だけ呼ぶ。設定とアカウントを読み、
    /// バージョン管理の層へ資格情報の窓口を差し込む。
    /// </summary>
    /// <param name="settingsDir">エディタの設定フォルダ（`editor/settings`）。</param>
    /// <param name="store">アカウントの保管（テストで差し替える）。</param>
    public static void Initialize(string settingsDir, IAccountStore? store = null)
    {
        lock (Gate)
        {
            _settingsDir    = settingsDir ?? string.Empty;
            _editorSettings = AccountEditorSettings.Load(_settingsDir);
            if (store is not null) _store = store;
        }

        ReloadAccount();

        // ★ここが Accounts → VersionControl の唯一の接点。
        //   バージョン管理の層はこの関数しか知らない。
        SEEDEditor.VersionControl.VersionControlService.CredentialProvider = GetLoreCredential;
    }

    /// <summary>
    /// 保管されたアカウントを読み直す（作成・読み込みの直後にも使う）。
    /// </summary>
    public static void ReloadAccount()
    {
        SeedAccount? loaded = null;
        var error = string.Empty;

        try
        {
            loaded = _store.Load();
        }
        catch (AccountStoreException ex)
        {
            // 壊れている・復号できない。**起動は止めない**（匿名で動ける）。
            error = ex.Message;
            WriteLog(string.Format(
                CultureInfo.CurrentCulture,
                AccountMessages.ACCOUNT_LOAD_FAILED_FORMAT, ex.Message));
        }

        SeedAccount? previous;
        lock (Gate)
        {
            previous   = _account;
            _account   = loaded;
            _loadError = error;
        }

        if (!ReferenceEquals(previous, loaded)) previous?.Dispose();
    }

    // ── アカウントの作成・書き出し・読み込み ────────────────

    /// <summary>
    /// 新しいアカウントを作って保存する。
    /// </summary>
    /// <param name="name">アカウント名（規則は <see cref="Crypto.AccountNameRule"/>）。</param>
    /// <exception cref="AccountStoreException">名前が規則に合わない・保存できないとき。</exception>
    public static void CreateAccount(string name)
    {
        var check = Crypto.AccountNameRule.Check(name);
        if (!check.IsValid) throw new AccountStoreException(check.Error);

        var keyPair = Crypto.AccountKeyPair.Create();
        var account = new SeedAccount(name, keyPair);

        try
        {
            _store.Save(account);
        }
        catch
        {
            // 保存できなかった鍵を保持し続けない（次の作成と混ざる）。
            account.Dispose();
            throw;
        }

        SeedAccount? previous;
        lock (Gate)
        {
            previous   = _account;
            _account   = account;
            _loadError = string.Empty;
        }
        previous?.Dispose();

        WriteLog(string.Format(
            CultureInfo.CurrentCulture, AccountMessages.ACCOUNT_CREATED_FORMAT, name));
    }

    /// <summary>
    /// 現在のアカウントをパスフレーズ付きで書き出す。
    /// </summary>
    /// <param name="filePath">書き出し先。</param>
    /// <param name="passphrase">パスフレーズ。</param>
    /// <exception cref="AccountStoreException">アカウントが無い・書き出せないとき。</exception>
    public static void ExportAccount(string filePath, string passphrase)
    {
        SeedAccount account;
        lock (Gate)
        {
            account = _account ?? throw new AccountStoreException(AccountMessages.ACCOUNT_NOT_CREATED);
        }

        AccountExportFile.Export(account, passphrase, filePath);
    }

    /// <summary>
    /// 書き出しファイルからアカウントを読み込み、この PC へ保存する。
    /// </summary>
    /// <param name="filePath">読み込むファイル。</param>
    /// <param name="passphrase">パスフレーズ。</param>
    /// <returns>読み込んだアカウント名。</returns>
    /// <exception cref="AccountStoreException">パスフレーズ違い・形式違い・保存できないとき。</exception>
    public static string ImportAccount(string filePath, string passphrase)
    {
        var imported = AccountExportFile.Import(filePath, passphrase);

        try
        {
            _store.Save(imported);
        }
        catch
        {
            imported.Dispose();
            throw;
        }

        SeedAccount? previous;
        lock (Gate)
        {
            previous   = _account;
            _account   = imported;
            _loadError = string.Empty;
        }
        previous?.Dispose();

        return imported.Name;
    }

    // ── プロジェクトとの結び付け ────────────────────────────

    /// <summary>
    /// プロジェクトを開いたときに呼ぶ。窓口を探し、居ればログインする。
    ///
    /// <para>
    /// **待たせない**。窓口が無い環境ではタイムアウトぶんだけ待つことになるので、
    /// 呼び出し側は結果を待たずに進んでよい（戻り値の Task は診断とテスト用）。
    /// </para>
    /// </summary>
    /// <param name="remoteUrl">Lore のリモート URL（`.lore/config.toml` の値）。</param>
    /// <param name="cancellationToken">中断用。</param>
    /// <returns>ログインできたら真。</returns>
    public static async Task<bool> AttachToProjectAsync(
        string? remoteUrl, CancellationToken cancellationToken = default)
    {
        SeedAccount? account;
        AccountEditorSettings settings;
        lock (Gate)
        {
            account  = _account;
            settings = _editorSettings;
        }

        // アカウントが無い / 自動ログインを切っている → 匿名のまま。
        if (account is null || !settings.AutoSignIn)
        {
            Sessions.SignOut();
            return false;
        }

        var gateway = AuthEndpointResolver.ResolveGateway(remoteUrl, settings.AuthUrlOverride);
        if (gateway is null)
        {
            WriteLog(string.Format(
                CultureInfo.CurrentCulture,
                AccountMessages.LOG_GATEWAY_ABSENT_FORMAT,
                AccountMessages.GATEWAY_ADDRESS_INVALID));
            Sessions.SignOut();
            return false;
        }

        // 窓口が居るかを短いタイムアウトで確かめてからログインする。
        // 居ない環境（匿名運用）でプロジェクトを開くたびに長く待たせないため。
        var client = new AuthGatewayClient(gateway, AccountSettings.Default);
        try
        {
            using (var healthCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                healthCts.CancelAfter(AccountSettings.Default.HealthTimeout);
                await client.GetHealthAsync(healthCts.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            // 窓口が無いのは異常ではない（契約 5 章の「移行期間」の構成）。
            client.Dispose();
            Sessions.SignOut();
            WriteLog(string.Format(
                CultureInfo.CurrentCulture,
                AccountMessages.LOG_GATEWAY_ABSENT_FORMAT, DescribeBriefly(ex)));
            return false;
        }

        WriteLog(string.Format(
            CultureInfo.CurrentCulture, AccountMessages.LOG_GATEWAY_FOUND_FORMAT, gateway));

        // クライアントの所有権はセッション管理へ渡す（更新でも使い続けるため）。
        return await Sessions.SignInAsync(client, account, ownsClient: true, cancellationToken)
                             .ConfigureAwait(false);
    }

    /// <summary>プロジェクトを閉じるときに呼ぶ（トークンを捨てる）。</summary>
    public static void DetachFromProject() => Sessions.SignOut();

    /// <summary>
    /// 招待コードでプロジェクトへ参加し、クローンまで済ませる。
    ///
    /// <para>
    /// 段取りそのものは <see cref="ProjectJoinService"/>（WPF 非依存・テスト済み）が持つ。
    /// ここが受け持つのは **秘密鍵を外へ出さないこと** だけ
    /// （<see cref="SeedAccount"/> を公開すると、署名のできる実体が UI へ漏れる）。
    /// </para>
    /// </summary>
    /// <param name="request">参加の依頼内容。</param>
    /// <param name="cloner">クローンの実装。</param>
    /// <param name="projectFileExtension">探すプロジェクトファイルの拡張子（`.seedproj`）。</param>
    /// <param name="clientFactory">窓口クライアントの生成（テスト用。null なら実 HTTP）。</param>
    /// <param name="progress">進捗の通知先。</param>
    /// <param name="cancellationToken">中断用。</param>
    /// <returns>参加の結果。</returns>
    public static Task<JoinProjectResult> JoinProjectAsync(
        JoinProjectRequest request,
        ILoreCloner cloner,
        string projectFileExtension,
        Func<Uri, IAuthGatewayClient>? clientFactory = null,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        SeedAccount? account;
        lock (Gate) { account = _account; }

        if (account is null)
        {
            return Task.FromResult(
                JoinProjectResult.Failed(AccountMessages.JOIN_ACCOUNT_REQUIRED));
        }

        return ProjectJoinService.JoinAsync(
            request, account, cloner, projectFileExtension,
            clientFactory, progress, cancellationToken);
    }

    /// <summary>
    /// ログインチャレンジへ署名する（オーナー向けダイアログが自前でログインするときに使う）。
    /// 秘密鍵そのものは外へ出さない。
    /// </summary>
    /// <param name="challengeId">challenge_id。</param>
    /// <param name="nonce">nonce。</param>
    /// <returns>署名の base64url。</returns>
    /// <exception cref="AccountStoreException">アカウントが無いとき。</exception>
    public static string SignLoginChallenge(string challengeId, string nonce)
    {
        SeedAccount? account;
        lock (Gate) { account = _account; }

        if (account is null) throw new AccountStoreException(AccountMessages.ACCOUNT_NOT_CREATED);
        return account.SignLoginChallenge(challengeId, nonce);
    }

    /// <summary>
    /// 現在のプロジェクト向けの窓口クライアントを作る（呼び出し側が破棄する）。
    ///
    /// <para>
    /// オーナー向けの操作（オーナー登録・招待コード発行・参加者の一覧と失効）は、
    /// 自動ログインとは別に窓口を叩く必要がある
    /// （まだオーナーが居ないプロジェクトではログインが通らないため）。
    /// </para>
    /// </summary>
    /// <param name="remoteUrl">Lore のリモート URL。</param>
    /// <returns>クライアント（窓口の URL を決められなければ null）。</returns>
    public static IAuthGatewayClient? CreateGatewayClient(string? remoteUrl)
    {
        AccountEditorSettings settings;
        lock (Gate) { settings = _editorSettings; }

        var gateway = AuthEndpointResolver.ResolveGateway(remoteUrl, settings.AuthUrlOverride);
        return gateway is null ? null : new AuthGatewayClient(gateway, AccountSettings.Default);
    }

    // ── バージョン管理への受け渡し ──────────────────────────

    /// <summary>
    /// Lore の共通引数へ載せる資格情報を返す。
    /// <c>VersionControlService.CredentialProvider</c> に差し込まれる唯一の関数。
    /// </summary>
    public static LoreAccountCredential GetLoreCredential()
    {
        var session = Sessions.CurrentSession;
        if (session is null) return LoreAccountCredential.None;

        return new LoreAccountCredential(session.AccessToken, session.Name);
    }

    // ── 設定 ────────────────────────────────────────────────

    /// <summary>現在の利用者設定の写し（画面の編集用）。</summary>
    public static AccountEditorSettings GetEditorSettings()
    {
        lock (Gate)
        {
            return new AccountEditorSettings
            {
                AuthUrlOverride = _editorSettings.AuthUrlOverride,
                AutoSignIn      = _editorSettings.AutoSignIn,
            };
        }
    }

    /// <summary>
    /// 利用者設定を差し替えて保存する。
    /// </summary>
    /// <param name="settings">新しい設定。</param>
    /// <returns>保存できたら真。</returns>
    public static bool UpdateEditorSettings(AccountEditorSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        string settingsDir;
        lock (Gate)
        {
            _editorSettings = settings;
            settingsDir     = _settingsDir;
        }
        return settings.Save(settingsDir);
    }

    // ── 内部ヘルパー ────────────────────────────────────────

    /// <summary>
    /// 例外を 1 行で説明する（ログ用。秘密は含めない）。
    /// </summary>
    /// <param name="ex">例外。</param>
    private static string DescribeBriefly(Exception ex)
        => ex is AuthGatewayException gateway ? gateway.Message : ex.Message;

    /// <summary>ログへ 1 行書く（<see cref="Log"/> が未設定なら何もしない）。</summary>
    /// <param name="message">内容。</param>
    private static void WriteLog(string message) => Log?.Invoke(message);
}
