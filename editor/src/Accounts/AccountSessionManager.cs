// ============================================================
//  AccountSessionManager.cs — ログインとトークンの自動更新
//
//  【役割】
//  契約（docs/seed_accounts.md 6 章）の
//  「窓口が応答すればチャレンジ応答でトークンを取り、期限の手前で自動更新する」
//  を実装する。プロジェクト 1 つにつき 1 インスタンス。
//
//  【トークンはディスクへ書かない】
//  盗まれたトークンは期限（既定 8 時間）まで有効で、失効させる手段が無い
//  （契約 7 章）。メモリにだけ置き、エディタを閉じれば消えるようにする。
//
//  【先回りで更新する理由】
//  Lore は「期限切れ」と「権限なし」をどちらも "Not authorized" で返し、
//  区別できない（契約 7 章）。期限が来てから気づく作りにすると、
//  利用者には「突然送信できなくなった」としか見えない。
//  そこで <see cref="AccountSettings.TokenRefreshMargin"/> だけ手前で取り直す。
//
//  【更新に失敗したときの方針】
//  ・まだ期限内なら **今のトークンを捨てない**。再試行間隔を空けて試し続ける
//    （一時的な通信断でログアウトさせない）。
//  ・期限を過ぎたらトークンを捨てて Failed にする
//    （期限切れのトークンを送り続けても Lore に拒否されるだけ）。
//
//  【スレッド】
//  更新はタイマー（スレッドプール）から走る。状態の読み書きは Gate で囲う。
//  <see cref="StateChanged"/> は **UI スレッドではない**ところから飛ぶので、
//  購読側が Dispatcher へ移すこと（この型は WPF に依存しない）。
//
//  【依存】
//  WPF にも LoreVcs にも依存しない（偽の窓口に対する単体テストへリンクできる）。
// ============================================================

using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using SEEDEditor.Accounts.Http;
using SEEDEditor.Accounts.Model;

namespace SEEDEditor.Accounts;

/// <summary>
/// ログインとトークンの保持・自動更新。
/// </summary>
public sealed class AccountSessionManager : IDisposable
{
    /// <summary>設定（タイムアウト・更新猶予）。</summary>
    private readonly AccountSettings _settings;

    /// <summary>診断ログの出力先（秘密は書かない）。</summary>
    private readonly Action<string>? _log;

    /// <summary>状態を触るときのロック。</summary>
    private readonly object _gate = new();

    /// <summary>窓口のクライアント（ログイン時に受け取り、更新でも使い続ける）。</summary>
    private IAuthGatewayClient? _client;

    /// <summary>クライアントをこの型が所有しているか。</summary>
    private bool _ownsClient;

    /// <summary>署名に使うアカウント（所有はしない。破棄は呼び出し側）。</summary>
    private SeedAccount? _account;

    /// <summary>現在のセッション（未ログインなら null）。</summary>
    private AuthSession? _session;

    /// <summary>次の自動更新を起こすタイマー。</summary>
    private Timer? _refreshTimer;

    /// <summary>破棄済みの印。</summary>
    private bool _disposed;

    /// <summary>現在の状態。</summary>
    public AccountAuthState State { get; private set; } = AccountAuthState.NotSignedIn;

    /// <summary>
    /// 状態が変わったときに発火する。
    /// **UI スレッドではない**ところから飛ぶので、購読側が Dispatcher へ移すこと。
    /// </summary>
    public event EventHandler<AccountAuthState>? StateChanged;

    /// <summary>
    /// 設定とログの出力先を指定して生成する。
    /// </summary>
    /// <param name="settings">設定（省略時は既定値）。</param>
    /// <param name="log">診断ログの出力先（省略可）。</param>
    public AccountSessionManager(AccountSettings? settings = null, Action<string>? log = null)
    {
        _settings = settings ?? AccountSettings.Default;
        _log      = log;
    }

    // ── 参照 ────────────────────────────────────────────────

    /// <summary>
    /// 現在のセッション（未ログイン・期限切れなら null）。
    /// </summary>
    public AuthSession? CurrentSession
    {
        get
        {
            lock (_gate)
            {
                if (_session is null) return null;
                // 期限を過ぎたものは「無い」ものとして扱う（送っても拒否されるため）。
                return _session.IsExpired(DateTime.UtcNow) ? null : _session;
            }
        }
    }

    /// <summary>
    /// Lore へ渡すアクセストークン（未ログイン・期限切れなら null）。
    /// </summary>
    public string? TryGetAccessToken() => CurrentSession?.AccessToken;

    /// <summary>ログイン中の名前（未ログインなら空文字）。</summary>
    public string SignedInName => CurrentSession?.Name ?? string.Empty;

    // ── ログイン ────────────────────────────────────────────

    /// <summary>
    /// 窓口へログインし、成功したら自動更新を仕掛ける。
    ///
    /// <para>
    /// 窓口が居ない・アカウントが参加していない場合は **失敗ではなく匿名** として扱い、
    /// 偽を返す（今までどおりバージョン管理は動く）。
    /// </para>
    /// </summary>
    /// <param name="client">窓口のクライアント。</param>
    /// <param name="account">署名に使うアカウント（破棄はしない）。</param>
    /// <param name="ownsClient">クライアントをこの型が破棄してよいか。</param>
    /// <param name="cancellationToken">中断用。</param>
    /// <returns>ログインできたら真。</returns>
    public async Task<bool> SignInAsync(
        IAuthGatewayClient client,
        SeedAccount account,
        bool ownsClient = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(account);

        // 直前のセッションがあれば片付けてから始める（開き直しでタイマーが漏れる）。
        SignOut();

        lock (_gate)
        {
            _client     = client;
            _ownsClient = ownsClient;
            _account    = account;
        }

        Publish(AccountAuthState.SigningIn(account.Name));

        try
        {
            var session = await LoginOnceAsync(client, account, cancellationToken)
                .ConfigureAwait(false);

            lock (_gate) { _session = session; }

            _log?.Invoke(string.Format(
                CultureInfo.CurrentCulture, AccountMessages.LOG_SIGNED_IN_FORMAT,
                session.Name, session.Grants.Count));

            ScheduleRefresh(session);
            Publish(AccountAuthState.SignedIn(session.Name, session.ExpiresAtUtc));
            return true;
        }
        catch (OperationCanceledException)
        {
            // 呼び出し側の中断。静かに未ログインへ戻す。
            Publish(AccountAuthState.NotSignedIn);
            return false;
        }
        catch (AuthGatewayException ex)
        {
            Publish(ToFailureState(account.Name, ex));
            _log?.Invoke(string.Format(
                CultureInfo.CurrentCulture, AccountMessages.LOG_SIGN_IN_FAILED_FORMAT, ex.Message));
            return false;
        }
    }

    /// <summary>
    /// 今すぐトークンを取り直す（自動更新と同じ処理を手で起こす）。
    /// </summary>
    /// <param name="cancellationToken">中断用。</param>
    /// <returns>取り直せたら真。</returns>
    public async Task<bool> RefreshNowAsync(CancellationToken cancellationToken = default)
    {
        IAuthGatewayClient? client;
        SeedAccount? account;
        lock (_gate)
        {
            client  = _client;
            account = _account;
        }

        // ログインしたことが無い（クライアントもアカウントも無い）なら何もしない。
        if (client is null || account is null) return false;

        try
        {
            var session = await LoginOnceAsync(client, account, cancellationToken)
                .ConfigureAwait(false);

            lock (_gate) { _session = session; }

            ScheduleRefresh(session);
            Publish(AccountAuthState.SignedIn(session.Name, session.ExpiresAtUtc));
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (AuthGatewayException ex)
        {
            HandleRefreshFailure(account.Name, ex);
            return false;
        }
    }

    /// <summary>
    /// ログインを 1 往復で行う（チャレンジ → 署名 → 完了）。
    /// </summary>
    /// <param name="client">窓口のクライアント。</param>
    /// <param name="account">署名に使うアカウント。</param>
    /// <param name="cancellationToken">中断用。</param>
    /// <returns>得られたセッション。</returns>
    private static async Task<AuthSession> LoginOnceAsync(
        IAuthGatewayClient client, SeedAccount account, CancellationToken cancellationToken)
    {
        var challenge = await client.StartLoginAsync(account.Name, cancellationToken)
                                    .ConfigureAwait(false);

        // 署名対象の組み立ては LoginSignaturePayload に閉じ込めてある。
        var signature = account.SignLoginChallenge(challenge.ChallengeId, challenge.Nonce);

        var login = await client.CompleteLoginAsync(
            challenge.ChallengeId, signature, cancellationToken).ConfigureAwait(false);

        return new AuthSession(
            login.AccessToken,
            DateTimeOffset.FromUnixTimeMilliseconds(login.ExpiresAtUnixMs).UtcDateTime,
            login.Name,
            login.Grants);
    }

    // ── 自動更新 ────────────────────────────────────────────

    /// <summary>
    /// 期限の手前で更新が走るようタイマーを仕掛け直す。
    /// </summary>
    /// <param name="session">基準にするセッション。</param>
    private void ScheduleRefresh(AuthSession session)
    {
        var delay = session.ExpiresAtUtc - _settings.TokenRefreshMargin - DateTime.UtcNow;

        // 期限が近すぎる（またはもう過ぎている）場合でも、
        // 連続実行にならないよう下限を置く。
        if (delay < _settings.MinTokenRefreshDelay) delay = _settings.MinTokenRefreshDelay;

        ArmTimer(delay);
    }

    /// <summary>
    /// 指定の待ち時間でタイマーを仕掛け直す（既存があれば作り直す）。
    /// </summary>
    /// <param name="delay">待ち時間。</param>
    private void ArmTimer(TimeSpan delay)
    {
        lock (_gate)
        {
            if (_disposed) return;

            if (_refreshTimer is null)
            {
                _refreshTimer = new Timer(
                    _ => _ = RefreshFromTimerAsync(),
                    state: null, dueTime: delay, period: Timeout.InfiniteTimeSpan);
                return;
            }

            try { _refreshTimer.Change(delay, Timeout.InfiniteTimeSpan); }
            catch (ObjectDisposedException) { /* 破棄と競合。次のログインで作り直される */ }
        }
    }

    /// <summary>
    /// タイマーから呼ばれる更新。例外を外へ漏らさない
    /// （タイマーのコールバックで投げるとプロセスが落ちる）。
    /// </summary>
    private async Task RefreshFromTimerAsync()
    {
        try
        {
            await RefreshNowAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log?.Invoke(string.Format(
                CultureInfo.CurrentCulture,
                AccountMessages.AUTH_REFRESH_FAILED_FORMAT, ex.Message));
        }
    }

    /// <summary>
    /// 更新に失敗したときの扱いを決める。
    /// </summary>
    /// <param name="accountName">アカウント名。</param>
    /// <param name="error">失敗の内容。</param>
    private void HandleRefreshFailure(string accountName, AuthGatewayException error)
    {
        bool stillValid;
        lock (_gate)
        {
            stillValid = _session is not null && !_session.IsExpired(DateTime.UtcNow);
        }

        _log?.Invoke(string.Format(
            CultureInfo.CurrentCulture,
            AccountMessages.AUTH_REFRESH_FAILED_FORMAT, error.Message));

        if (stillValid)
        {
            // まだ使えるトークンがある。一時的な不調とみなして間を置いて再試行する。
            // 状態は SignedIn のままにする（利用者に「失敗した」と見せて不安にさせない）。
            ArmTimer(_settings.TokenRetryInterval);
            return;
        }

        // 期限切れ。トークンを捨てて失敗として知らせる（匿名へ落ちる）。
        lock (_gate) { _session = null; }
        Publish(ToFailureState(accountName, error));
    }

    // ── 後始末 ──────────────────────────────────────────────

    /// <summary>
    /// ログアウトする（トークンを捨て、自動更新を止める）。
    /// クライアントを所有していれば閉じる。
    /// </summary>
    public void SignOut()
    {
        Timer? timer;
        IAuthGatewayClient? client;
        bool ownsClient;

        lock (_gate)
        {
            timer       = _refreshTimer;
            _refreshTimer = null;
            client      = _client;
            ownsClient  = _ownsClient;
            _client     = null;
            _ownsClient = false;
            _account    = null;
            _session    = null;
        }

        try { timer?.Dispose(); } catch { /* 後始末の失敗は無視 */ }
        if (ownsClient) { try { client?.Dispose(); } catch { /* 同上 */ } }

        Publish(AccountAuthState.NotSignedIn);
    }

    /// <summary>ログアウトして破棄する。</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        SignOut();
    }

    // ── 共通ヘルパー ────────────────────────────────────────

    /// <summary>
    /// 窓口の失敗を、利用者に意味のある状態へ写す。
    /// </summary>
    /// <param name="accountName">アカウント名。</param>
    /// <param name="error">失敗の内容。</param>
    private static AccountAuthState ToFailureState(string accountName, AuthGatewayException error)
    {
        // 窓口が居ないのは「失敗」ではない。匿名で動く正常な構成として扱う。
        if (error.IsUnreachable)
            return AccountAuthState.Anonymous(AccountMessages.AUTH_GATEWAY_ABSENT);

        // それ以外の言い換えは AuthFailureText の 1 か所に任せる
        // （ログイン失敗の理由はサーバが区別して返さないので、ここで推測しない）。
        return AccountAuthState.Failed(accountName, AuthFailureText.Describe(error));
    }

    /// <summary>
    /// 状態を差し替えて購読者へ知らせる（同じ値なら何もしない）。
    /// </summary>
    /// <param name="next">新しい状態。</param>
    private void Publish(AccountAuthState next)
    {
        lock (_gate)
        {
            if (State == next) return;
            State = next;
        }
        StateChanged?.Invoke(this, next);
    }
}
