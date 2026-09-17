// ============================================================
//  AuthContracts.cs — 発行窓口（HTTP API）の要求・応答の形
//
//  【役割】
//  契約（docs/seed_accounts.md 3 章）の表を、そのまま C# の型にしたもの。
//  JSON のキー名は **契約の綴りのまま**（snake_case）。ここが正典との唯一の接点で、
//  サーバの実装が変わったらこのファイルだけを直す。
//
//  【秘密の扱い】
//  `access_token` と `invite_code` が通る型がある。**ログへ出さない**。
//  ToString() を足さない（うっかりログへ流れる経路を作らない）。
//
//  【依存】
//  WPF にも LoreVcs にも依存しない（単体テストへそのままリンクできる）。
// ============================================================

using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace SEEDEditor.Accounts.Http;

// ── 共通 ────────────────────────────────────────────────────

/// <summary>エラー応答（HTTP ステータス ＋ この本文）。</summary>
public sealed class AuthErrorBody
{
    /// <summary>エラーコード（`owner_exists` / `login_failed` など）。</summary>
    [JsonPropertyName("error")]
    public string Error { get; set; } = string.Empty;

    /// <summary>日本語の説明（そのまま画面へ出してよい）。</summary>
    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;
}

/// <summary>
/// 契約 3 章で決まっているエラーコードの全一覧。
/// **ここに無いコードを実装側で作らない**（サーバと綴りが 1 つでもずれると、
/// 利用者に「サーバの生のメッセージ」しか出せなくなる）。
/// </summary>
public static class AuthErrorCodes
{
    /// <summary>400: JSON が壊れている・形式が不正・長さ超過。</summary>
    public const string INVALID_REQUEST = "invalid_request";

    /// <summary>401: ログインに失敗した（理由は一切区別されない）。</summary>
    public const string LOGIN_FAILED = "login_failed";

    /// <summary>401: Bearer が無い／不正／期限切れ。</summary>
    public const string UNAUTHORIZED = "unauthorized";

    /// <summary>403: そのリポジトリの owner ではない。</summary>
    public const string FORBIDDEN = "forbidden";

    /// <summary>403: 招待コードが不正・期限切れ・使用済み。</summary>
    public const string INVITE_INVALID = "invite_invalid";

    /// <summary>403: bootstrap をループバック以外から呼んだ。</summary>
    public const string LOOPBACK_ONLY = "loopback_only";

    /// <summary>404: 失効させようとした参加者が居ない。</summary>
    public const string NOT_FOUND = "not_found";

    /// <summary>409: その名前は別の公開鍵で使われている。</summary>
    public const string NAME_TAKEN = "name_taken";

    /// <summary>
    /// 409: そのリポジトリには既にオーナーが居る／
    /// **owner の権限を失効させようとした**（自分自身を含む）。
    /// </summary>
    public const string OWNER_EXISTS = "owner_exists";

    /// <summary>429: 発行中のチャレンジが上限に達している。</summary>
    public const string TOO_MANY_REQUESTS = "too_many_requests";

    /// <summary>500: サーバ内部の失敗。</summary>
    public const string INTERNAL = "internal";
}

/// <summary>
/// 契約 3 章の入力長の上限（文字数）。
///
/// <para>
/// サーバは超過を 400 <see cref="AuthErrorCodes.INVALID_REQUEST"/> で断る。
/// **送る前にこちらで弾く**ほうが、利用者には原因が分かりやすい
/// （とくに招待コードは貼り付けで余計な文字が混ざりやすい）。
/// </para>
/// </summary>
public static class AuthInputLimits
{
    /// <summary>名前の最大文字数。</summary>
    public const int NAME_MAX_LENGTH = 32;

    /// <summary>公開鍵（base64url）の最大文字数。</summary>
    public const int PUBLIC_KEY_MAX_LENGTH = 128;

    /// <summary>署名（base64url）の最大文字数。</summary>
    public const int SIGNATURE_MAX_LENGTH = 128;

    /// <summary>招待コードの最大文字数。</summary>
    public const int INVITE_CODE_MAX_LENGTH = 128;

    /// <summary>challenge_id の最大文字数。</summary>
    public const int CHALLENGE_ID_MAX_LENGTH = 64;

    /// <summary>プロジェクト名の最大文字数。</summary>
    public const int PROJECT_NAME_MAX_LENGTH = 128;

    /// <summary>アクセストークンの最大文字数。</summary>
    public const int ACCESS_TOKEN_MAX_LENGTH = 8192;

    /// <summary>リポジトリ ID の文字数（16 進小文字・固定長）。</summary>
    public const int REPOSITORY_ID_LENGTH = 32;
}

// ── GET /v1/health ─────────────────────────────────────────

/// <summary>窓口の生存確認の応答。</summary>
public sealed class AuthHealthResponse
{
    /// <summary>状態（"ok"）。</summary>
    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    /// <summary>発行者（JWT の iss）。</summary>
    [JsonPropertyName("issuer")]
    public string Issuer { get; set; } = string.Empty;

    /// <summary>対象（JWT の aud）。</summary>
    [JsonPropertyName("audience")]
    public string Audience { get; set; } = string.Empty;

    /// <summary>API の版。</summary>
    [JsonPropertyName("api")]
    public int Api { get; set; }
}

// ── POST /v1/bootstrap ─────────────────────────────────────

/// <summary>オーナー登録の要求（ループバックからのみ成功する）。</summary>
public sealed class AuthBootstrapRequest
{
    /// <summary>アカウント名。</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>公開鍵の base64url。</summary>
    [JsonPropertyName("public_key")]
    public string PublicKey { get; set; } = string.Empty;

    /// <summary>リポジトリ ID（`.lore/id` の値）。</summary>
    [JsonPropertyName("repository_id")]
    public string RepositoryId { get; set; } = string.Empty;

    /// <summary>プロジェクト名（表示用）。</summary>
    [JsonPropertyName("project_name")]
    public string ProjectName { get; set; } = string.Empty;
}

/// <summary>オーナー登録の応答。</summary>
public sealed class AuthBootstrapResponse
{
    /// <summary>登録された名前。</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>役割（"owner"）。</summary>
    [JsonPropertyName("role")]
    public string Role { get; set; } = string.Empty;
}

// ── POST /v1/login/challenge ───────────────────────────────

/// <summary>チャレンジの要求。</summary>
public sealed class AuthChallengeRequest
{
    /// <summary>アカウント名。</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;
}

/// <summary>チャレンジの応答。</summary>
public sealed class AuthChallengeResponse
{
    /// <summary>チャレンジ識別子。</summary>
    [JsonPropertyName("challenge_id")]
    public string ChallengeId { get; set; } = string.Empty;

    /// <summary>nonce（base64url 文字列。**そのまま署名対象へ連結する**）。</summary>
    [JsonPropertyName("nonce")]
    public string Nonce { get; set; } = string.Empty;

    /// <summary>有効秒数。</summary>
    [JsonPropertyName("expires_in")]
    public int ExpiresInSeconds { get; set; }
}

// ── POST /v1/login/complete ────────────────────────────────

/// <summary>ログイン完了の要求。</summary>
public sealed class AuthLoginRequest
{
    /// <summary>チャレンジ識別子。</summary>
    [JsonPropertyName("challenge_id")]
    public string ChallengeId { get; set; } = string.Empty;

    /// <summary>署名の base64url（P1363 の 64 バイト）。</summary>
    [JsonPropertyName("signature")]
    public string Signature { get; set; } = string.Empty;
}

/// <summary>参加しているプロジェクト 1 件。</summary>
public sealed class AuthGrant
{
    /// <summary>リポジトリ ID。</summary>
    [JsonPropertyName("repository_id")]
    public string RepositoryId { get; set; } = string.Empty;

    /// <summary>プロジェクト名（表示用）。</summary>
    [JsonPropertyName("project_name")]
    public string ProjectName { get; set; } = string.Empty;

    /// <summary>役割（"owner" / "member"）。</summary>
    [JsonPropertyName("role")]
    public string Role { get; set; } = string.Empty;
}

/// <summary>ログイン完了の応答。**access_token をログへ出さないこと。**</summary>
public sealed class AuthLoginResponse
{
    /// <summary>アクセストークン（JWT）。</summary>
    [JsonPropertyName("access_token")]
    public string AccessToken { get; set; } = string.Empty;

    /// <summary>期限（Unix ミリ秒）。</summary>
    [JsonPropertyName("expires_at")]
    public long ExpiresAtUnixMs { get; set; }

    /// <summary>ログインした名前。</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>参加しているプロジェクトの一覧。</summary>
    [JsonPropertyName("grants")]
    public List<AuthGrant> Grants { get; set; } = new();
}

// ── POST /v1/invites ───────────────────────────────────────

/// <summary>招待コード発行の要求。</summary>
public sealed class AuthInviteRequest
{
    /// <summary>リポジトリ ID。</summary>
    [JsonPropertyName("repository_id")]
    public string RepositoryId { get; set; } = string.Empty;

    /// <summary>付与する役割（"member"）。</summary>
    [JsonPropertyName("role")]
    public string Role { get; set; } = AccountSettings.ROLE_MEMBER;

    /// <summary>有効時間 [hour]。</summary>
    [JsonPropertyName("expires_in_hours")]
    public int ExpiresInHours { get; set; } = AccountSettings.DEFAULT_INVITE_EXPIRES_IN_HOURS;
}

/// <summary>招待コード発行の応答。**invite_code をログへ出さないこと。**</summary>
public sealed class AuthInviteResponse
{
    /// <summary>招待コード（**サーバはこれを 1 回しか返さない**）。</summary>
    [JsonPropertyName("invite_code")]
    public string InviteCode { get; set; } = string.Empty;

    /// <summary>期限（Unix ミリ秒）。</summary>
    [JsonPropertyName("expires_at")]
    public long ExpiresAtUnixMs { get; set; }
}

// ── POST /v1/join ──────────────────────────────────────────

/// <summary>参加の要求。**invite_code をログへ出さないこと。**</summary>
public sealed class AuthJoinRequest
{
    /// <summary>招待コード。</summary>
    [JsonPropertyName("invite_code")]
    public string InviteCode { get; set; } = string.Empty;

    /// <summary>アカウント名。</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>公開鍵の base64url。</summary>
    [JsonPropertyName("public_key")]
    public string PublicKey { get; set; } = string.Empty;
}

/// <summary>参加の応答。</summary>
public sealed class AuthJoinResponse
{
    /// <summary>参加した名前。</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>リポジトリ ID。</summary>
    [JsonPropertyName("repository_id")]
    public string RepositoryId { get; set; } = string.Empty;

    /// <summary>プロジェクト名（クローン元 URL の末尾に使う）。</summary>
    [JsonPropertyName("project_name")]
    public string ProjectName { get; set; } = string.Empty;

    /// <summary>役割。</summary>
    [JsonPropertyName("role")]
    public string Role { get; set; } = string.Empty;
}

// ── GET /v1/members ────────────────────────────────────────

/// <summary>参加者 1 件。</summary>
public sealed class AuthMember
{
    /// <summary>名前。</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>役割。</summary>
    [JsonPropertyName("role")]
    public string Role { get; set; } = string.Empty;

    /// <summary>状態（"active" / "revoked"）。</summary>
    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    /// <summary>参加日時（ISO 8601 の文字列。サーバの書式をそのまま持つ）。</summary>
    [JsonPropertyName("added_at")]
    public string AddedAt { get; set; } = string.Empty;
}

/// <summary>参加者一覧の応答。</summary>
public sealed class AuthMembersResponse
{
    /// <summary>参加者。</summary>
    [JsonPropertyName("members")]
    public List<AuthMember> Members { get; set; } = new();
}

// ── POST /v1/members/revoke ────────────────────────────────

/// <summary>失効の要求。</summary>
public sealed class AuthRevokeRequest
{
    /// <summary>リポジトリ ID。</summary>
    [JsonPropertyName("repository_id")]
    public string RepositoryId { get; set; } = string.Empty;

    /// <summary>失効させる名前。</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;
}

/// <summary>失効の応答。</summary>
public sealed class AuthRevokeResponse
{
    /// <summary>失効させた名前。</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>状態（"revoked"）。</summary>
    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;
}

// ── エディタ内部で使う軽い型 ────────────────────────────────

/// <summary>
/// ログインして得たセッション（メモリにだけ置く。**ディスクへ書かない**）。
/// </summary>
/// <param name="AccessToken">アクセストークン。</param>
/// <param name="ExpiresAtUtc">期限（UTC）。</param>
/// <param name="Name">ログインした名前。</param>
/// <param name="Grants">参加しているプロジェクト。</param>
public sealed record AuthSession(
    string AccessToken,
    DateTime ExpiresAtUtc,
    string Name,
    IReadOnlyList<AuthGrant> Grants)
{
    /// <summary>
    /// 指定のリポジトリでの役割を返す（参加していなければ空文字）。
    /// </summary>
    /// <param name="repositoryId">リポジトリ ID。</param>
    public string RoleFor(string? repositoryId)
    {
        if (string.IsNullOrWhiteSpace(repositoryId)) return string.Empty;

        foreach (var grant in Grants)
        {
            if (string.Equals(grant.RepositoryId, repositoryId, StringComparison.OrdinalIgnoreCase))
                return grant.Role ?? string.Empty;
        }
        return string.Empty;
    }

    /// <summary>指定のリポジトリでオーナーか。</summary>
    /// <param name="repositoryId">リポジトリ ID。</param>
    public bool IsOwnerOf(string? repositoryId)
        => string.Equals(RoleFor(repositoryId), AccountSettings.ROLE_OWNER,
                         StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 指定時刻に、猶予を見込んで取り直すべきか。
    /// </summary>
    /// <param name="nowUtc">現在時刻（UTC）。</param>
    /// <param name="margin">期限の何分手前で取り直すか。</param>
    public bool NeedsRefresh(DateTime nowUtc, TimeSpan margin)
        => nowUtc >= ExpiresAtUtc - margin;

    /// <summary>指定時刻で期限切れか。</summary>
    /// <param name="nowUtc">現在時刻（UTC）。</param>
    public bool IsExpired(DateTime nowUtc) => nowUtc >= ExpiresAtUtc;
}
