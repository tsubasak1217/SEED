// ============================================================
//  AuthGatewayClient.cs — 発行窓口の HTTP 実装（HttpClient）
//
//  【役割】
//  契約（docs/seed_accounts.md 3 章）のエンドポイントを HttpClient で叩き、
//  応答を DTO へ、失敗を AuthGatewayException へ畳む。
//
//  【エラーの畳み方】
//  ・接続できない（サーバが居ない・アドレス違い・タイムアウト）
//        → IsUnreachable = true。呼び出し側は「匿名のまま動く」へ倒す。
//  ・HTTP エラー
//        → 本文の {"error","message"} を読み、ErrorCode と日本語メッセージを持たせる。
//          本文が読めなければステータスだけを持たせる。
//  **例外を素通しさせない**（呼び出し側で catch すべき型が増えるため）。
//
//  【秘密を書かない】
//  ・アクセストークン・招待コード・署名をログにも例外メッセージにも入れない。
//  ・Authorization ヘッダは要求ごとに付ける（HttpClient の既定ヘッダへ入れない。
//    入れると別の要求にも付き、意図しない相手へトークンが飛ぶ）。
//
//  【依存】
//  System.Net.Http のみ。WPF にも LoreVcs にも依存しない
//  （偽の窓口（HttpListener）に対する単体テストへそのままリンクできる）。
// ============================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SEEDEditor.Accounts.Http;

/// <summary>
/// 発行窓口の HTTP クライアント。
/// </summary>
public sealed class AuthGatewayClient : IAuthGatewayClient
{
    // ── エンドポイント（契約 3 章の綴りそのまま）──────────────

    /// <summary>生存確認。</summary>
    private const string PATH_HEALTH = "v1/health";

    /// <summary>オーナー登録。</summary>
    private const string PATH_BOOTSTRAP = "v1/bootstrap";

    /// <summary>ログインのチャレンジ。</summary>
    private const string PATH_LOGIN_CHALLENGE = "v1/login/challenge";

    /// <summary>ログインの完了。</summary>
    private const string PATH_LOGIN_COMPLETE = "v1/login/complete";

    /// <summary>招待コードの発行。</summary>
    private const string PATH_INVITES = "v1/invites";

    /// <summary>参加。</summary>
    private const string PATH_JOIN = "v1/join";

    /// <summary>参加者の一覧（クエリは <see cref="QUERY_REPOSITORY_ID"/>）。</summary>
    private const string PATH_MEMBERS = "v1/members";

    /// <summary>参加者の失効。</summary>
    private const string PATH_MEMBERS_REVOKE = "v1/members/revoke";

    /// <summary>参加者一覧のクエリ名。</summary>
    private const string QUERY_REPOSITORY_ID = "repository_id";

    /// <summary>Bearer 認証のスキーム名。</summary>
    private const string AUTH_SCHEME_BEARER = "Bearer";

    /// <summary>送受信する本文の MIME 型。</summary>
    private const string MEDIA_TYPE_JSON = "application/json";

    // ── フィールド ──────────────────────────────────────────

    /// <summary>JSON の読み書き設定（キー名は属性で固定しているので変換規則は不要）。</summary>
    private static readonly JsonSerializerOptions JsonOptions = new();

    /// <summary>HTTP の実体。</summary>
    private readonly HttpClient _http;

    /// <summary>HttpClient をこの型が所有しているか（所有していれば Dispose で閉じる）。</summary>
    private readonly bool _ownsHttpClient;

    /// <summary>窓口の URL。</summary>
    public Uri BaseAddress { get; }

    /// <summary>
    /// 窓口の URL と設定を指定して生成する。
    /// </summary>
    /// <param name="baseAddress">窓口の URL（<see cref="AuthEndpointResolver"/> が作る）。</param>
    /// <param name="settings">設定（省略時は既定値。タイムアウトを使う）。</param>
    /// <param name="httpClient">
    /// 差し替える HttpClient（テスト用）。渡した場合、この型は破棄しない。
    /// </param>
    public AuthGatewayClient(
        Uri baseAddress, AccountSettings? settings = null, HttpClient? httpClient = null)
    {
        ArgumentNullException.ThrowIfNull(baseAddress);

        var effective = settings ?? AccountSettings.Default;
        BaseAddress   = baseAddress;

        if (httpClient is null)
        {
            _http = new HttpClient { Timeout = effective.HttpTimeout };
            _ownsHttpClient = true;
        }
        else
        {
            _http = httpClient;
            _ownsHttpClient = false;
        }
    }

    // ── エンドポイント ──────────────────────────────────────

    /// <summary>窓口が居るか確かめる。</summary>
    /// <param name="cancellationToken">中断用。</param>
    public Task<AuthHealthResponse> GetHealthAsync(CancellationToken cancellationToken = default)
        => SendAsync<AuthHealthResponse>(
            HttpMethod.Get, PATH_HEALTH, body: null, accessToken: null, cancellationToken);

    /// <summary>自分をオーナーとして登録する。</summary>
    /// <param name="request">登録内容。</param>
    /// <param name="cancellationToken">中断用。</param>
    public Task<AuthBootstrapResponse> BootstrapAsync(
        AuthBootstrapRequest request, CancellationToken cancellationToken = default)
        => SendAsync<AuthBootstrapResponse>(
            HttpMethod.Post, PATH_BOOTSTRAP, request, accessToken: null, cancellationToken);

    /// <summary>ログインのチャレンジを取る。</summary>
    /// <param name="name">アカウント名。</param>
    /// <param name="cancellationToken">中断用。</param>
    public Task<AuthChallengeResponse> StartLoginAsync(
        string name, CancellationToken cancellationToken = default)
        => SendAsync<AuthChallengeResponse>(
            HttpMethod.Post, PATH_LOGIN_CHALLENGE,
            new AuthChallengeRequest { Name = name }, accessToken: null, cancellationToken);

    /// <summary>署名を送ってトークンを受け取る。</summary>
    /// <param name="challengeId">チャレンジ識別子。</param>
    /// <param name="signature">署名の base64url。</param>
    /// <param name="cancellationToken">中断用。</param>
    public Task<AuthLoginResponse> CompleteLoginAsync(
        string challengeId, string signature, CancellationToken cancellationToken = default)
        => SendAsync<AuthLoginResponse>(
            HttpMethod.Post, PATH_LOGIN_COMPLETE,
            new AuthLoginRequest { ChallengeId = challengeId, Signature = signature },
            accessToken: null, cancellationToken);

    /// <summary>招待コードを発行する。</summary>
    /// <param name="accessToken">オーナーのアクセストークン。</param>
    /// <param name="request">発行内容。</param>
    /// <param name="cancellationToken">中断用。</param>
    public Task<AuthInviteResponse> CreateInviteAsync(
        string accessToken, AuthInviteRequest request,
        CancellationToken cancellationToken = default)
        => SendAsync<AuthInviteResponse>(
            HttpMethod.Post, PATH_INVITES, request, accessToken, cancellationToken);

    /// <summary>招待コードで参加する。</summary>
    /// <param name="request">参加内容。</param>
    /// <param name="cancellationToken">中断用。</param>
    public Task<AuthJoinResponse> JoinAsync(
        AuthJoinRequest request, CancellationToken cancellationToken = default)
        => SendAsync<AuthJoinResponse>(
            HttpMethod.Post, PATH_JOIN, request, accessToken: null, cancellationToken);

    /// <summary>参加者を一覧する。</summary>
    /// <param name="accessToken">オーナーのアクセストークン。</param>
    /// <param name="repositoryId">リポジトリ ID。</param>
    /// <param name="cancellationToken">中断用。</param>
    public async Task<IReadOnlyList<AuthMember>> GetMembersAsync(
        string accessToken, string repositoryId, CancellationToken cancellationToken = default)
    {
        var path = PATH_MEMBERS + "?" + QUERY_REPOSITORY_ID + "="
                   + Uri.EscapeDataString(repositoryId ?? string.Empty);

        var response = await SendAsync<AuthMembersResponse>(
            HttpMethod.Get, path, body: null, accessToken, cancellationToken)
            .ConfigureAwait(false);

        return response.Members;
    }

    /// <summary>参加者を失効させる。</summary>
    /// <param name="accessToken">オーナーのアクセストークン。</param>
    /// <param name="request">失効内容。</param>
    /// <param name="cancellationToken">中断用。</param>
    public Task<AuthRevokeResponse> RevokeMemberAsync(
        string accessToken, AuthRevokeRequest request,
        CancellationToken cancellationToken = default)
        => SendAsync<AuthRevokeResponse>(
            HttpMethod.Post, PATH_MEMBERS_REVOKE, request, accessToken, cancellationToken);

    // ── 共通の送信処理 ──────────────────────────────────────

    /// <summary>
    /// 1 回の要求を送り、応答を型へ写す。失敗はすべて
    /// <see cref="AuthGatewayException"/> へ畳む。
    /// </summary>
    /// <typeparam name="TResponse">応答の型。</typeparam>
    /// <param name="method">HTTP メソッド。</param>
    /// <param name="path">ベース URL からの相対パス。</param>
    /// <param name="body">本文（null なら付けない）。</param>
    /// <param name="accessToken">Bearer に載せるトークン（null なら付けない）。</param>
    /// <param name="cancellationToken">中断用。</param>
    private async Task<TResponse> SendAsync<TResponse>(
        HttpMethod method, string path, object? body, string? accessToken,
        CancellationToken cancellationToken)
    {
        // BaseAddress がパスを持つ構成でも壊れないよう、明示的に結合する。
        var uri = new Uri(BaseAddress, path);

        using var request = new HttpRequestMessage(method, uri);

        if (body is not null)
        {
            request.Content = new StringContent(
                JsonSerializer.Serialize(body, body.GetType(), JsonOptions),
                Encoding.UTF8, MEDIA_TYPE_JSON);
        }

        // ★既定ヘッダへ入れず、この要求にだけ付ける。
        if (!string.IsNullOrWhiteSpace(accessToken))
        {
            request.Headers.Authorization =
                new AuthenticationHeaderValue(AUTH_SCHEME_BEARER, accessToken);
        }

        HttpResponseMessage response;
        string responseBody;
        try
        {
            response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            responseBody = await response.Content.ReadAsStringAsync(cancellationToken)
                                                 .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 呼び出し側が中断した場合はそのまま伝える（失敗として扱わない）。
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            // サーバが居ない／アドレス違い／タイムアウト。
            // 「窓口が無い」のは異常ではなく、匿名で動く正常な構成なので区別できるようにする。
            throw new AuthGatewayException(
                AccountMessages.GATEWAY_UNREACHABLE, isUnreachable: true, inner: ex);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode) throw ToFailure(response, responseBody);

            try
            {
                var value = JsonSerializer.Deserialize<TResponse>(responseBody, JsonOptions);
                if (value is null)
                    throw new AuthGatewayException(AccountMessages.GATEWAY_UNEXPECTED_RESPONSE);
                return value;
            }
            catch (JsonException ex)
            {
                throw new AuthGatewayException(
                    AccountMessages.GATEWAY_UNEXPECTED_RESPONSE, inner: ex);
            }
        }
    }

    /// <summary>
    /// エラー応答を例外へ写す。本文の {"error","message"} を優先して使う。
    /// </summary>
    /// <param name="response">応答。</param>
    /// <param name="responseBody">応答の本文。</param>
    private static AuthGatewayException ToFailure(
        HttpResponseMessage response, string responseBody)
    {
        var status = (int)response.StatusCode;

        AuthErrorBody? error = null;
        try
        {
            error = JsonSerializer.Deserialize<AuthErrorBody>(responseBody, JsonOptions);
        }
        catch (JsonException)
        {
            // 本文が JSON でない（プロキシのエラーページ等）。ステータスだけで伝える。
        }

        if (error is null || string.IsNullOrWhiteSpace(error.Error))
        {
            return new AuthGatewayException(
                string.Format(CultureInfo.CurrentCulture,
                              AccountMessages.GATEWAY_ERROR_FORMAT,
                              AccountMessages.GATEWAY_UNEXPECTED_RESPONSE, status),
                statusCode: status);
        }

        // サーバのメッセージは日本語で来る契約なので、そのまま見せる。
        var message = string.IsNullOrWhiteSpace(error.Message)
            ? AccountMessages.GATEWAY_UNEXPECTED_RESPONSE
            : error.Message;

        return new AuthGatewayException(message, error.Error, status);
    }

    /// <summary>所有している HttpClient だけを閉じる。</summary>
    public void Dispose()
    {
        if (_ownsHttpClient) _http.Dispose();
    }
}
