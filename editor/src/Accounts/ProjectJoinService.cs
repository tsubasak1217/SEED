// ============================================================
//  ProjectJoinService.cs — 「プロジェクトに参加」の段取り
//
//  【役割】
//  契約（docs/seed_accounts.md 6 章）の
//  「サーバのアドレス・招待コード・保存先 → join → ログイン → トークン付きでクローン → 開く」
//  のうち、**開く直前まで** を 1 つの関数にまとめる。
//
//  【なぜ画面から切り出すのか】
//  この流れは 4 往復（join / チャレンジ / 完了 / クローン）あり、
//  どこで失敗したかで利用者への言い方が変わる。
//  ボタンのイベントハンドラに書くと、**GUI を起動しないと 1 行も検証できない**。
//  ここへ出して、偽の窓口と偽のクローンで全分岐を固定する。
//
//  【失敗しても例外を投げない】
//  結果は必ず <see cref="JoinProjectResult"/> で返す。呼び出し側（Hub）は
//  <c>Success</c> と <c>Message</c> だけを見ればよい。
//
//  【依存】
//  WPF にも LoreVcs にも依存しない（ILoreCloner の偽物を差してテストできる）。
// ============================================================

using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SEEDEditor.Accounts.Http;
using SEEDEditor.Accounts.Model;
using SEEDEditor.VersionControl.Lore.Backend;

namespace SEEDEditor.Accounts;

/// <summary>
/// 参加の依頼内容。
/// </summary>
/// <param name="Host">サーバのアドレス（ホスト名か IP。ポートは既定を使う）。</param>
/// <param name="InviteCode">招待コード。**ログへ出さないこと。**</param>
/// <param name="DestinationDir">クローン先のフォルダ（空であること）。</param>
public readonly record struct JoinProjectRequest(
    string Host, string InviteCode, string DestinationDir);

/// <summary>
/// 参加の結果。
/// </summary>
/// <param name="Success">成功したか。</param>
/// <param name="Message">利用者へ見せる 1 行（成功時も理由を入れる）。</param>
/// <param name="ProjectName">サーバ上のプロジェクト名。</param>
/// <param name="ProjectFilePath">見つかった .seedproj の絶対パス（無ければ空）。</param>
/// <param name="RepositoryId">リポジトリ ID。</param>
public readonly record struct JoinProjectResult(
    bool Success,
    string Message,
    string ProjectName,
    string ProjectFilePath,
    string RepositoryId)
{
    /// <summary>失敗の結果を作る。</summary>
    /// <param name="message">利用者へ見せる理由。</param>
    public static JoinProjectResult Failed(string message)
        => new(false, message, string.Empty, string.Empty, string.Empty);
}

/// <summary>
/// 参加の段取り（join → ログイン → クローン → .seedproj を探す）。
/// </summary>
public static class ProjectJoinService
{
    /// <summary>
    /// 参加して、クローンまで済ませる。
    /// </summary>
    /// <param name="request">依頼内容。</param>
    /// <param name="account">自分のアカウント（公開鍵を登録し、署名でログインする）。</param>
    /// <param name="cloner">クローンの実装。</param>
    /// <param name="projectFileExtension">
    /// 探すプロジェクトファイルの拡張子（`.seedproj`）。
    /// プロジェクト層の定数を呼び出し側から渡す（ここでプロジェクト層に依存しないため）。
    /// </param>
    /// <param name="clientFactory">
    /// 窓口クライアントの生成（テストで偽物を差す）。null なら実 HTTP。
    /// </param>
    /// <param name="progress">進捗の通知先（省略可）。</param>
    /// <param name="cancellationToken">中断用。</param>
    /// <returns>参加の結果。</returns>
    public static async Task<JoinProjectResult> JoinAsync(
        JoinProjectRequest request,
        SeedAccount account,
        ILoreCloner cloner,
        string projectFileExtension,
        Func<Uri, IAuthGatewayClient>? clientFactory = null,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(cloner);

        // ── 入力の検証（サーバへ行く前に済ませる）──
        if (account is null)   return JoinProjectResult.Failed(AccountMessages.JOIN_ACCOUNT_REQUIRED);
        if (string.IsNullOrWhiteSpace(request.Host))
            return JoinProjectResult.Failed(AccountMessages.JOIN_HOST_REQUIRED);
        if (string.IsNullOrWhiteSpace(request.InviteCode))
            return JoinProjectResult.Failed(AccountMessages.JOIN_INVITE_REQUIRED);
        if (string.IsNullOrWhiteSpace(request.DestinationDir))
            return JoinProjectResult.Failed(AccountMessages.JOIN_DESTINATION_REQUIRED);

        // 招待コードは貼り付けで余計な文字が混ざりやすい。サーバは長さ超過を
        // 400 invalid_request で断るが、こちらで弾いたほうが原因が分かる。
        if (request.InviteCode.Trim().Length > AuthInputLimits.INVITE_CODE_MAX_LENGTH)
        {
            return JoinProjectResult.Failed(string.Format(
                CultureInfo.CurrentCulture,
                AccountMessages.JOIN_INVITE_TOO_LONG_FORMAT,
                AuthInputLimits.INVITE_CODE_MAX_LENGTH));
        }

        var destination = Path.GetFullPath(request.DestinationDir);

        // 既存のフォルダを上書きしない。クローンは中身を作るので、
        // 何か入っているフォルダを指されたら止める（利用者の作業を壊さない）。
        if (Directory.Exists(destination)
            && (Directory.GetFileSystemEntries(destination).Length > 0))
        {
            return JoinProjectResult.Failed(string.Format(
                CultureInfo.CurrentCulture,
                AccountMessages.JOIN_DESTINATION_NOT_EMPTY_FORMAT, destination));
        }

        var gateway = AuthEndpointResolver.BuildHttpUri(
            request.Host, AccountSettings.DEFAULT_AUTH_PORT);
        if (gateway is null)
            return JoinProjectResult.Failed(AccountMessages.GATEWAY_ADDRESS_INVALID);

        var client = clientFactory is not null
            ? clientFactory(gateway)
            : new AuthGatewayClient(gateway, AccountSettings.Default);

        try
        {
            // ── 1) 招待コードで参加する ──
            AuthJoinResponse join;
            try
            {
                join = await client.JoinAsync(
                    new AuthJoinRequest
                    {
                        InviteCode = request.InviteCode.Trim(),
                        Name       = account.Name,
                        PublicKey  = account.PublicKeyBase64Url,
                    },
                    cancellationToken).ConfigureAwait(false);
            }
            catch (AuthGatewayException ex)
            {
                return JoinProjectResult.Failed(ToJoinFailureMessage(ex, account.Name));
            }

            // ── 2) ログインしてトークンを得る（クローンに要る）──
            AuthLoginResponse login;
            try
            {
                var challenge = await client.StartLoginAsync(account.Name, cancellationToken)
                                            .ConfigureAwait(false);
                var signature = account.SignLoginChallenge(challenge.ChallengeId, challenge.Nonce);
                login = await client.CompleteLoginAsync(
                    challenge.ChallengeId, signature, cancellationToken).ConfigureAwait(false);
            }
            catch (AuthGatewayException ex)
            {
                return JoinProjectResult.Failed(ToJoinFailureMessage(ex, account.Name));
            }

            // ── 3) トークン付きでクローンする ──
            progress?.Report(AccountMessages.JOIN_CLONING);

            var remoteUrl = AuthEndpointResolver.BuildLoreRemoteUrl(request.Host, join.ProjectName);
            if (remoteUrl.Length == 0)
                return JoinProjectResult.Failed(AccountMessages.GATEWAY_ADDRESS_INVALID);

            var credential = new LoreAccountCredential(login.AccessToken, login.Name);

            // クローンは同期ブロッキング（Lore の Wait()）。UI から外して回す。
            var cloneResult = await Task.Run(
                () => cloner.Clone(
                    new LoreCloneRequest(remoteUrl, destination, credential,
                                         FallbackIdentity: string.Empty),
                    cancellationToken),
                cancellationToken).ConfigureAwait(false);

            if (!cloneResult.Succeeded)
            {
                return JoinProjectResult.Failed(string.Format(
                    CultureInfo.CurrentCulture,
                    AccountMessages.JOIN_CLONE_FAILED_FORMAT, DescribeCallFailure(cloneResult)));
            }

            // ── 4) .seedproj を探す ──
            var projectFile = FindProjectFile(destination, projectFileExtension);
            if (projectFile.Length == 0)
            {
                return new JoinProjectResult(
                    false, AccountMessages.JOIN_PROJECT_FILE_MISSING,
                    join.ProjectName, string.Empty, join.RepositoryId);
            }

            return new JoinProjectResult(
                true,
                string.Format(CultureInfo.CurrentCulture,
                              AccountMessages.JOIN_OK_FORMAT, join.ProjectName),
                join.ProjectName, projectFile, join.RepositoryId);
        }
        catch (OperationCanceledException)
        {
            return JoinProjectResult.Failed(AccountMessages.JOIN_CANCELED);
        }
        finally
        {
            client.Dispose();
        }
    }

    /// <summary>
    /// クローン先から `.seedproj` を探す（直下 → 1 階層下の順）。
    ///
    /// <para>
    /// リポジトリの作り方によっては、プロジェクトがフォルダ 1 つぶん
    /// 内側に入っていることがある。直下だけを見て「無い」と言うより
    /// 1 階層だけ覗く方が親切で、それ以上潜ると別プロジェクトの
    /// `.seedproj` を拾い得るのでやめる。
    /// </para>
    /// </summary>
    /// <param name="rootDir">探す起点。</param>
    /// <param name="extension">プロジェクトファイルの拡張子（例 `.seedproj`）。</param>
    /// <returns>見つかった絶対パス（無ければ空文字）。</returns>
    public static string FindProjectFile(string rootDir, string extension)
    {
        if (string.IsNullOrWhiteSpace(rootDir) || string.IsNullOrWhiteSpace(extension))
            return string.Empty;

        var pattern = "*" + extension;

        try
        {
            var direct = Directory.GetFiles(rootDir, pattern, SearchOption.TopDirectoryOnly);
            if (direct.Length > 0) return direct[0];

            foreach (var child in Directory.GetDirectories(rootDir))
            {
                var found = Directory.GetFiles(child, pattern, SearchOption.TopDirectoryOnly);
                if (found.Length > 0) return found[0];
            }
        }
        catch (Exception)
        {
            // アクセス権が無い等。「見つからなかった」として扱う。
        }

        return string.Empty;
    }

    /// <summary>
    /// 参加・ログインの失敗を、利用者が次に何をすればよいか分かる文へ写す。
    ///
    /// <para>
    /// 大半は <see cref="AuthFailureText.Describe"/> に任せ、
    /// ここでは「参加の文脈でだけ意味が変わるコード」（名前の衝突）を言い換える。
    /// </para>
    /// </summary>
    /// <param name="error">窓口が返した失敗。</param>
    /// <param name="accountName">自分のアカウント名。</param>
    private static string ToJoinFailureMessage(AuthGatewayException error, string accountName)
    {
        if (error.Is(AuthErrorCodes.NAME_TAKEN))
        {
            return string.Format(CultureInfo.CurrentCulture,
                                 AccountMessages.JOIN_NAME_TAKEN_FORMAT, accountName);
        }

        return AuthFailureText.Describe(error);
    }

    /// <summary>
    /// Lore の失敗を 1 行で説明する（メッセージが無ければ終了コードを出す）。
    /// </summary>
    /// <param name="result">Lore 呼び出しの結末。</param>
    private static string DescribeCallFailure(LoreCallResult result)
    {
        if (result.Messages is { Count: > 0 }) return string.Join(" / ", result.Messages);
        return result.ReturnCode.ToString(CultureInfo.InvariantCulture);
    }
}
