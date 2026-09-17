// ============================================================
//  IAuthGatewayClient.cs — 発行窓口（HTTP API）の境界
//
//  【役割】
//  契約（docs/seed_accounts.md 3 章）のエンドポイントを 1 対 1 で並べただけの細い境界。
//  上の層（セッション管理・Hub の画面）は HttpClient も JSON も知らない。
//
//  【なぜ境界にするのか】
//  ・ログイン → 期限前の自動更新 → 失効、という **時間のかかる流れ** を、
//    実サーバなしで（偽の窓口を差して）テストできるようにする。
//  ・サーバ担当が並行して実装しているため、契約が動いてもここの下だけで吸収できる。
//
//  【例外の約束】
//  失敗は必ず <see cref="AuthGatewayException"/> に畳んで投げる。
//  HttpRequestException / TaskCanceledException / JsonException を素通しさせない
//  （呼び出し側が catch すべき型を数え上げる羽目になる）。
//
//  【依存】
//  WPF にも LoreVcs にも依存しない（単体テストへそのままリンクできる）。
// ============================================================

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace SEEDEditor.Accounts.Http;

/// <summary>
/// 発行窓口とのやり取りの失敗。
/// </summary>
public sealed class AuthGatewayException : Exception
{
    /// <summary>サーバが返したエラーコード（通信自体が失敗したときは空）。</summary>
    public string ErrorCode { get; }

    /// <summary>HTTP ステータス（通信自体が失敗したときは 0）。</summary>
    public int StatusCode { get; }

    /// <summary>窓口へ繋がらなかった（サーバが居ない・アドレス違い）。</summary>
    public bool IsUnreachable { get; }

    /// <summary>
    /// 失敗の内容を指定して生成する。
    /// </summary>
    /// <param name="message">利用者向けの説明（日本語）。</param>
    /// <param name="errorCode">サーバのエラーコード。</param>
    /// <param name="statusCode">HTTP ステータス。</param>
    /// <param name="isUnreachable">窓口へ繋がらなかったか。</param>
    /// <param name="inner">元の例外（ログ用）。</param>
    public AuthGatewayException(
        string message,
        string errorCode = "",
        int statusCode = 0,
        bool isUnreachable = false,
        Exception? inner = null)
        : base(message, inner)
    {
        ErrorCode     = errorCode;
        StatusCode    = statusCode;
        IsUnreachable = isUnreachable;
    }

    /// <summary>エラーコードが一致するか（大小文字を区別しない）。</summary>
    /// <param name="code">比較するコード。</param>
    public bool Is(string code)
        => string.Equals(ErrorCode, code, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// 発行窓口（seed-auth）の HTTP API。
/// </summary>
public interface IAuthGatewayClient : IDisposable
{
    /// <summary>窓口の URL（ログと診断に使う）。</summary>
    Uri BaseAddress { get; }

    /// <summary>
    /// 窓口が居るか確かめる（<c>GET /v1/health</c>）。
    /// </summary>
    /// <param name="cancellationToken">中断用。</param>
    /// <returns>窓口の情報。</returns>
    Task<AuthHealthResponse> GetHealthAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 自分をそのリポジトリのオーナーとして登録する（<c>POST /v1/bootstrap</c>）。
    /// **サーバと同じ PC（ループバック）からしか成功しない。**
    /// </summary>
    /// <param name="request">登録内容。</param>
    /// <param name="cancellationToken">中断用。</param>
    Task<AuthBootstrapResponse> BootstrapAsync(
        AuthBootstrapRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// ログインのチャレンジを取る（<c>POST /v1/login/challenge</c>）。
    /// </summary>
    /// <param name="name">アカウント名。</param>
    /// <param name="cancellationToken">中断用。</param>
    Task<AuthChallengeResponse> StartLoginAsync(
        string name, CancellationToken cancellationToken = default);

    /// <summary>
    /// 署名を送ってトークンを受け取る（<c>POST /v1/login/complete</c>）。
    /// </summary>
    /// <param name="challengeId">チャレンジ識別子。</param>
    /// <param name="signature">署名の base64url。</param>
    /// <param name="cancellationToken">中断用。</param>
    Task<AuthLoginResponse> CompleteLoginAsync(
        string challengeId, string signature, CancellationToken cancellationToken = default);

    /// <summary>
    /// 招待コードを発行する（<c>POST /v1/invites</c>、オーナーのみ）。
    /// </summary>
    /// <param name="accessToken">オーナーのアクセストークン。</param>
    /// <param name="request">発行内容。</param>
    /// <param name="cancellationToken">中断用。</param>
    Task<AuthInviteResponse> CreateInviteAsync(
        string accessToken, AuthInviteRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 招待コードでプロジェクトへ参加する（<c>POST /v1/join</c>）。
    /// </summary>
    /// <param name="request">参加内容。</param>
    /// <param name="cancellationToken">中断用。</param>
    Task<AuthJoinResponse> JoinAsync(
        AuthJoinRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// 参加者を一覧する（<c>GET /v1/members</c>、オーナーのみ）。
    /// </summary>
    /// <param name="accessToken">オーナーのアクセストークン。</param>
    /// <param name="repositoryId">リポジトリ ID。</param>
    /// <param name="cancellationToken">中断用。</param>
    Task<IReadOnlyList<AuthMember>> GetMembersAsync(
        string accessToken, string repositoryId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 参加者を失効させる（<c>POST /v1/members/revoke</c>、オーナーのみ）。
    /// </summary>
    /// <param name="accessToken">オーナーのアクセストークン。</param>
    /// <param name="request">失効内容。</param>
    /// <param name="cancellationToken">中断用。</param>
    Task<AuthRevokeResponse> RevokeMemberAsync(
        string accessToken, AuthRevokeRequest request,
        CancellationToken cancellationToken = default);
}
