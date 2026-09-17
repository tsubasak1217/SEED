// ============================================================
//  FakeAuthGateway.cs — 偽の発行窓口（HttpListener）
//
//  【役割】
//  契約（docs/seed_accounts.md 3 章）のエンドポイントを最小限だけ実装した偽サーバ。
//  実サーバ（tools/seed-loreserver）が無くても、
//  join → ログイン → 期限前の自動更新 → 失効時の失敗
//  を本物の HTTP で通せるようにする。
//
//  【本物らしくしている点（ここを手抜きすると意味が無い）】
//  ・**署名を本当に検証する**。公開鍵は join / bootstrap で登録されたものを使い、
//    `seed-auth-login:v1:<challenge_id>:<nonce>` を P1363 の 64 バイト署名で確かめる。
//    → 署名対象の組み立て・base64url・P1363 の取り違えがあれば、ここで落ちる。
//  ・チャレンジは 1 回限り（使ったら消す）。
//  ・Bearer を要求するエンドポイントは、トークンが無ければ 401 を返す。
//
//  【ポート】
//  本番（41337 / 41339 / 41350）と、既存テスト（41357 / 41359）を避けた
//  41371 以降から空きを探す。**本番ポートへは絶対に触れない。**
//
//  【後始末】
//  Dispose で必ず Stop する。自分が立てたリスナー以外には触らない。
// ============================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SEEDEditor.Accounts;
using SEEDEditor.Accounts.Crypto;
using SEEDEditor.Accounts.Http;

namespace SEEDEditor.Tests.Accounts;

/// <summary>
/// 偽の発行窓口。テストから挙動（失効・招待コード・トークン寿命）を差し替えられる。
/// </summary>
public sealed class FakeAuthGateway : IDisposable
{
    /// <summary>探し始めるポート（本番・既存テストのポートを避ける）。</summary>
    private const int PORT_SEARCH_BEGIN = 41371;

    /// <summary>ポートを探す上限の試行回数。</summary>
    private const int PORT_SEARCH_ATTEMPTS = 40;

    /// <summary>応答の MIME 型。</summary>
    private const string MEDIA_TYPE_JSON = "application/json";

    /// <summary>Bearer 認証のスキーム名（前方一致で見る）。</summary>
    private const string AUTH_SCHEME_BEARER = "Bearer ";

    /// <summary>チャレンジの有効秒数（契約の既定値と揃える）。</summary>
    private const int CHALLENGE_EXPIRES_IN_SECONDS = 60;

    /// <summary>この窓口が名乗る発行者。</summary>
    private const string ISSUER = "seed-auth";

    /// <summary>この窓口が名乗る対象。</summary>
    private const string AUDIENCE = "seed-lore";

    /// <summary>API の版。</summary>
    private const int API_VERSION = 1;

    /// <summary>JSON の設定（属性でキー名を固定しているので変換規則は不要）。</summary>
    private static readonly JsonSerializerOptions JsonOptions = new();

    /// <summary>実体。</summary>
    private readonly HttpListener _listener = new();

    /// <summary>受付ループの中断。</summary>
    private readonly CancellationTokenSource _stopping = new();

    /// <summary>登録済みの参加者（名前 → 公開鍵）。</summary>
    private readonly Dictionary<string, string> _publicKeys = new(StringComparer.Ordinal);

    /// <summary>登録済みの参加者（名前 → 役割）。</summary>
    private readonly Dictionary<string, string> _roles = new(StringComparer.Ordinal);

    /// <summary>失効済みの参加者。</summary>
    private readonly HashSet<string> _revoked = new(StringComparer.Ordinal);

    /// <summary>未使用のチャレンジ（challenge_id → (名前, nonce)）。</summary>
    private readonly Dictionary<string, (string Name, string Nonce)> _challenges = new(StringComparer.Ordinal);

    /// <summary>発行済みのトークン（トークン文字列 → 名前）。</summary>
    private readonly Dictionary<string, string> _tokens = new(StringComparer.Ordinal);

    /// <summary>状態を触るときのロック（受付は別スレッド）。</summary>
    private readonly object _gate = new();

    /// <summary>窓口の URL。</summary>
    public Uri BaseAddress { get; }

    /// <summary>発行するトークンの寿命 [秒]（テストから短くする）。</summary>
    public int TokenTtlSeconds { get; set; } = 60;

    /// <summary>正しい招待コード。</summary>
    public string InviteCode { get; set; } = "TEST-INVITE-0001";

    /// <summary>参加させるプロジェクト名。</summary>
    public string ProjectName { get; set; } = "TestProject";

    /// <summary>参加させるリポジトリ ID。</summary>
    public string RepositoryId { get; set; } = "repo-0001";

    /// <summary>ログイン（complete）が成功した回数。自動更新の確認に使う。</summary>
    public int LoginCount { get; private set; }

    /// <summary>直近に発行した招待コード（発行 API のテスト用）。</summary>
    public string LastIssuedInviteCode { get; private set; } = string.Empty;

    /// <summary>直近の要求に付いていた Authorization ヘッダ（Bearer の確認用）。</summary>
    public string LastAuthorizationHeader { get; private set; } = string.Empty;

    /// <summary>直近の参加者一覧要求のクエリ文字列（クエリ名の確認用）。</summary>
    public string LastMembersQuery { get; private set; } = string.Empty;

    /// <summary>
    /// 偽の窓口を立てる。
    /// </summary>
    public FakeAuthGateway()
    {
        // 空いているポートを探す。HttpListener は使用中だと Start で失敗する。
        Exception? lastError = null;
        for (var offset = 0; offset < PORT_SEARCH_ATTEMPTS; offset++)
        {
            var port   = PORT_SEARCH_BEGIN + offset;
            var prefix = $"http://127.0.0.1:{port.ToString(CultureInfo.InvariantCulture)}/";
            try
            {
                _listener.Prefixes.Clear();
                _listener.Prefixes.Add(prefix);
                _listener.Start();
                BaseAddress = new Uri(prefix);
                _ = Task.Run(AcceptLoopAsync);
                return;
            }
            catch (HttpListenerException ex)
            {
                lastError = ex;
            }
        }

        throw new InvalidOperationException(
            "偽の発行窓口を立てられませんでした。", lastError);
    }

    /// <summary>参加者を直接登録する（join を経ずに用意したいとき）。</summary>
    /// <param name="name">名前。</param>
    /// <param name="publicKey">公開鍵の base64url。</param>
    /// <param name="role">役割。</param>
    public void Register(string name, string publicKey, string role = AccountSettings.ROLE_MEMBER)
    {
        lock (_gate)
        {
            _publicKeys[name] = publicKey;
            _roles[name]      = role;
            _revoked.Remove(name);
        }
    }

    /// <summary>参加者を失効させる（以後ログインできなくなる）。</summary>
    /// <param name="name">名前。</param>
    public void Revoke(string name)
    {
        lock (_gate) { _revoked.Add(name); }
    }

    // ── 受付ループ ──────────────────────────────────────────

    /// <summary>要求を受け付け続ける。</summary>
    private async Task AcceptLoopAsync()
    {
        while (!_stopping.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Stop されたか、リスナーが閉じられた。ループを抜ける。
                return;
            }

            try { Handle(context); }
            catch (Exception)
            {
                // 偽サーバの都合で落ちてもテストを巻き込まない。
                try { context.Response.Abort(); } catch { /* 後始末の失敗は無視 */ }
            }
        }
    }

    /// <summary>1 要求を処理する。</summary>
    /// <param name="context">要求と応答。</param>
    private void Handle(HttpListenerContext context)
    {
        var path = context.Request.Url?.AbsolutePath ?? string.Empty;
        var body = ReadBody(context.Request);

        LastAuthorizationHeader = context.Request.Headers["Authorization"] ?? string.Empty;

        switch (path)
        {
            case "/v1/health":
                WriteJson(context, HttpStatusCode.OK, new AuthHealthResponse
                {
                    Status = "ok", Issuer = ISSUER, Audience = AUDIENCE, Api = API_VERSION,
                });
                return;

            case "/v1/bootstrap":     HandleBootstrap(context, body);     return;
            case "/v1/login/challenge": HandleChallenge(context, body);   return;
            case "/v1/login/complete":  HandleLoginComplete(context, body); return;
            case "/v1/join":          HandleJoin(context, body);          return;
            case "/v1/invites":       HandleInvites(context, body);       return;
            case "/v1/members":       HandleMembers(context);             return;
            case "/v1/members/revoke": HandleRevoke(context, body);       return;

            default:
                WriteError(context, HttpStatusCode.NotFound, "not_found", "そのような窓口はありません。");
                return;
        }
    }

    /// <summary>オーナー登録。</summary>
    /// <param name="context">要求と応答。</param>
    /// <param name="body">要求の本文。</param>
    private void HandleBootstrap(HttpListenerContext context, string body)
    {
        var request = Deserialize<AuthBootstrapRequest>(body);
        if (request is null) { WriteBadRequest(context); return; }

        lock (_gate)
        {
            // すでにオーナーが居るなら 409（契約 3 章）。
            foreach (var role in _roles.Values)
            {
                if (string.Equals(role, AccountSettings.ROLE_OWNER, StringComparison.Ordinal))
                {
                    WriteError(context, HttpStatusCode.Conflict,
                               AuthErrorCodes.OWNER_EXISTS, "既にオーナーが居ます。");
                    return;
                }
            }

            _publicKeys[request.Name] = request.PublicKey;
            _roles[request.Name]      = AccountSettings.ROLE_OWNER;
            RepositoryId              = request.RepositoryId;
            ProjectName               = request.ProjectName;
        }

        WriteJson(context, HttpStatusCode.OK, new AuthBootstrapResponse
        {
            Name = request.Name, Role = AccountSettings.ROLE_OWNER,
        });
    }

    /// <summary>ログインのチャレンジ。</summary>
    /// <param name="context">要求と応答。</param>
    /// <param name="body">要求の本文。</param>
    private void HandleChallenge(HttpListenerContext context, string body)
    {
        var request = Deserialize<AuthChallengeRequest>(body);
        if (request is null) { WriteBadRequest(context); return; }

        var challengeId = Guid.NewGuid().ToString("N");
        var nonce       = Base64Url.Encode(Guid.NewGuid().ToByteArray());

        lock (_gate) { _challenges[challengeId] = (request.Name, nonce); }

        // ★未登録でも同じ形を返す（存在を漏らさない。契約 3 章）。
        WriteJson(context, HttpStatusCode.OK, new AuthChallengeResponse
        {
            ChallengeId      = challengeId,
            Nonce            = nonce,
            ExpiresInSeconds = CHALLENGE_EXPIRES_IN_SECONDS,
        });
    }

    /// <summary>ログインの完了（署名を本当に検証する）。</summary>
    /// <param name="context">要求と応答。</param>
    /// <param name="body">要求の本文。</param>
    private void HandleLoginComplete(HttpListenerContext context, string body)
    {
        var request = Deserialize<AuthLoginRequest>(body);
        if (request is null) { WriteBadRequest(context); return; }

        string name;
        string nonce;
        string publicKey;
        bool   revoked;

        lock (_gate)
        {
            // チャレンジは 1 回限り。使ったら消す。
            if (!_challenges.Remove(request.ChallengeId, out var challenge))
            {
                WriteError(context, HttpStatusCode.Unauthorized,
                           AuthErrorCodes.LOGIN_FAILED, "ログインできませんでした。");
                return;
            }

            name    = challenge.Name;
            nonce   = challenge.Nonce;
            revoked = _revoked.Contains(name);
            if (!_publicKeys.TryGetValue(name, out publicKey!)) publicKey = string.Empty;
        }

        // 失効済み・未登録は理由を区別せず 401（契約 3 章）。
        if (revoked || publicKey.Length == 0)
        {
            WriteError(context, HttpStatusCode.Unauthorized,
                       AuthErrorCodes.LOGIN_FAILED, "ログインできませんでした。");
            return;
        }

        // ★ここが本番と同じ検証。署名対象の組み立て・base64url・P1363 の
        //   どれかが違えば必ず落ちる。
        var payload = LoginSignaturePayload.Build(request.ChallengeId, nonce);
        if (!AccountKeyPair.Verify(publicKey, payload, request.Signature))
        {
            WriteError(context, HttpStatusCode.Unauthorized,
                       AuthErrorCodes.LOGIN_FAILED, "ログインできませんでした。");
            return;
        }

        var token     = Guid.NewGuid().ToString("N");
        var expiresAt = DateTimeOffset.UtcNow.AddSeconds(TokenTtlSeconds).ToUnixTimeMilliseconds();

        string role;
        lock (_gate)
        {
            _tokens[token] = name;
            LoginCount++;
            role = _roles.TryGetValue(name, out var found) ? found : AccountSettings.ROLE_MEMBER;
        }

        WriteJson(context, HttpStatusCode.OK, new AuthLoginResponse
        {
            AccessToken     = token,
            ExpiresAtUnixMs = expiresAt,
            Name            = name,
            Grants          = new List<AuthGrant>
            {
                new()
                {
                    RepositoryId = RepositoryId,
                    ProjectName  = ProjectName,
                    Role         = role,
                },
            },
        });
    }

    /// <summary>招待コードでの参加。</summary>
    /// <param name="context">要求と応答。</param>
    /// <param name="body">要求の本文。</param>
    private void HandleJoin(HttpListenerContext context, string body)
    {
        var request = Deserialize<AuthJoinRequest>(body);
        if (request is null) { WriteBadRequest(context); return; }

        if (!string.Equals(request.InviteCode, InviteCode, StringComparison.Ordinal))
        {
            WriteError(context, HttpStatusCode.Forbidden,
                       AuthErrorCodes.INVITE_INVALID, "招待コードが使えません。");
            return;
        }

        lock (_gate)
        {
            // 同じ名前で別の公開鍵なら衝突（契約 3 章）。
            if (_publicKeys.TryGetValue(request.Name, out var existing)
                && !string.Equals(existing, request.PublicKey, StringComparison.Ordinal))
            {
                WriteError(context, HttpStatusCode.Conflict,
                           AuthErrorCodes.NAME_TAKEN, "その名前は既に使われています。");
                return;
            }

            _publicKeys[request.Name] = request.PublicKey;
            if (!_roles.ContainsKey(request.Name))
                _roles[request.Name] = AccountSettings.ROLE_MEMBER;
        }

        WriteJson(context, HttpStatusCode.OK, new AuthJoinResponse
        {
            Name         = request.Name,
            RepositoryId = RepositoryId,
            ProjectName  = ProjectName,
            Role         = AccountSettings.ROLE_MEMBER,
        });
    }

    /// <summary>招待コードの発行（Bearer 必須）。</summary>
    /// <param name="context">要求と応答。</param>
    /// <param name="body">要求の本文。</param>
    private void HandleInvites(HttpListenerContext context, string body)
    {
        if (!RequireBearer(context, out _)) return;

        var request = Deserialize<AuthInviteRequest>(body);
        if (request is null) { WriteBadRequest(context); return; }

        LastIssuedInviteCode = "ISSUED-" + Guid.NewGuid().ToString("N");

        WriteJson(context, HttpStatusCode.OK, new AuthInviteResponse
        {
            InviteCode      = LastIssuedInviteCode,
            ExpiresAtUnixMs = DateTimeOffset.UtcNow
                .AddHours(request.ExpiresInHours).ToUnixTimeMilliseconds(),
        });
    }

    /// <summary>参加者の一覧（Bearer 必須）。</summary>
    /// <param name="context">要求と応答。</param>
    private void HandleMembers(HttpListenerContext context)
    {
        if (!RequireBearer(context, out _)) return;

        LastMembersQuery = context.Request.Url?.Query ?? string.Empty;

        var response = new AuthMembersResponse();
        lock (_gate)
        {
            foreach (var pair in _roles)
            {
                response.Members.Add(new AuthMember
                {
                    Name    = pair.Key,
                    Role    = pair.Value,
                    Status  = _revoked.Contains(pair.Key)
                        ? AccountSettings.MEMBER_STATUS_REVOKED
                        : AccountSettings.MEMBER_STATUS_ACTIVE,
                    AddedAt = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                });
            }
        }

        WriteJson(context, HttpStatusCode.OK, response);
    }

    /// <summary>参加者の失効（Bearer 必須）。</summary>
    /// <param name="context">要求と応答。</param>
    /// <param name="body">要求の本文。</param>
    private void HandleRevoke(HttpListenerContext context, string body)
    {
        if (!RequireBearer(context, out _)) return;

        var request = Deserialize<AuthRevokeRequest>(body);
        if (request is null) { WriteBadRequest(context); return; }

        Revoke(request.Name);

        WriteJson(context, HttpStatusCode.OK, new AuthRevokeResponse
        {
            Name = request.Name, Status = AccountSettings.MEMBER_STATUS_REVOKED,
        });
    }

    // ── 共通ヘルパー ────────────────────────────────────────

    /// <summary>
    /// Bearer トークンを要求する。無ければ 401 を書いて偽を返す。
    /// </summary>
    /// <param name="context">要求と応答。</param>
    /// <param name="name">トークンに対応する名前。</param>
    private bool RequireBearer(HttpListenerContext context, out string name)
    {
        name = string.Empty;

        var header = context.Request.Headers["Authorization"] ?? string.Empty;
        if (!header.StartsWith(AUTH_SCHEME_BEARER, StringComparison.Ordinal))
        {
            WriteError(context, HttpStatusCode.Unauthorized,
                       "unauthorized", "認証が必要です。");
            return false;
        }

        var token = header[AUTH_SCHEME_BEARER.Length..];
        lock (_gate)
        {
            if (!_tokens.TryGetValue(token, out name!))
            {
                WriteError(context, HttpStatusCode.Unauthorized,
                           "unauthorized", "トークンが無効です。");
                return false;
            }
        }
        return true;
    }

    /// <summary>要求の本文を読む。</summary>
    /// <param name="request">要求。</param>
    private static string ReadBody(HttpListenerRequest request)
    {
        if (!request.HasEntityBody) return string.Empty;
        using var reader = new System.IO.StreamReader(request.InputStream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    /// <summary>本文を型へ写す（読めなければ null）。</summary>
    /// <typeparam name="T">writing 先の型。</typeparam>
    /// <param name="body">本文。</param>
    private static T? Deserialize<T>(string body) where T : class
    {
        try { return JsonSerializer.Deserialize<T>(body, JsonOptions); }
        catch (JsonException) { return null; }
    }

    /// <summary>JSON を返す。</summary>
    /// <param name="context">要求と応答。</param>
    /// <param name="status">HTTP ステータス。</param>
    /// <param name="value">本文にする値。</param>
    private static void WriteJson(HttpListenerContext context, HttpStatusCode status, object value)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, JsonOptions));
        context.Response.StatusCode      = (int)status;
        context.Response.ContentType     = MEDIA_TYPE_JSON;
        context.Response.ContentLength64 = bytes.Length;
        context.Response.OutputStream.Write(bytes, 0, bytes.Length);
        context.Response.Close();
    }

    /// <summary>契約どおりのエラー本文を返す。</summary>
    /// <param name="context">要求と応答。</param>
    /// <param name="status">HTTP ステータス。</param>
    /// <param name="error">エラーコード。</param>
    /// <param name="message">日本語の説明。</param>
    private static void WriteError(
        HttpListenerContext context, HttpStatusCode status, string error, string message)
        => WriteJson(context, status, new AuthErrorBody { Error = error, Message = message });

    /// <summary>本文が読めなかったときの応答。</summary>
    /// <param name="context">要求と応答。</param>
    private static void WriteBadRequest(HttpListenerContext context)
        => WriteError(context, HttpStatusCode.BadRequest, "bad_request", "要求を解釈できません。");

    /// <summary>窓口を止める。</summary>
    public void Dispose()
    {
        _stopping.Cancel();
        try { _listener.Stop();  } catch { /* 後始末の失敗は無視 */ }
        try { _listener.Close(); } catch { /* 同上 */ }
        _stopping.Dispose();
    }
}
