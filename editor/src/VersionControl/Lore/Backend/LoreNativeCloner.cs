// ============================================================
//  LoreNativeCloner.cs — ILoreCloner の実装（LoreVcs に依存する 2 つ目のファイル）
//
//  【役割】
//  LoreVcs の <c>Lore.RepositoryClone(LoreGlobalArgs, LoreRepositoryCloneArgs)</c> を呼ぶ。
//
//  【LoreVcs の clone の癖（XML ドキュメントと実機で確認）】
//  ・**クローン先は LoreRepositoryCloneArgs に無い**。
//    <c>LoreGlobalArgs.RepositoryPath</c> がクローン先フォルダになる。
//  ・<c>LoreRepositoryCloneArgs.RepositoryUrl</c> がクローン元。
//    リポジトリ名は URL の末尾に含める（専用のプロパティは無い）。
//  ・ブランチ指定のプロパティは無い（最も近いのは Revision）。既定は先端。
//  ・トークンを渡すときは Identity を空にする（LoreCredentialResolver が担当）。
//  ・**clone は IdentityToken が無いと通らない**（実サーバで確認済み）。
//    Lore v0.9.0 の auth_exchange_for_identity は、リポジトリ ID が確定していない
//    呼び出し（repository create / repository list / clone）で authorization token を
//    空にするため、AccessToken だけだと `authorization header required` で失敗する。
//    LoreCredentialResolver が 2 つのトークンへ同じ JWT を入れるので、
//    ここでは両方をそのまま写すだけでよい。
//
//  【例外を外へ出さない】
//  LoreNativeBackend と同じく、LoreError も DllNotFoundException も
//  LoreCallResult へ畳む。Hub（スタート画面）に try/catch を増やさないため。
//
//  【依存】
//  LoreVcs にのみ依存（WPF には依存しない）。
// ============================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using LoreVcs;
using LoreVcs.Types.Args;

// 自分の名前空間 SEEDEditor.VersionControl.Lore が LoreVcs の静的クラス Lore を
// 隠してしまうため、明示的な別名で参照する。
using LoreApi = global::LoreVcs.Lore;

namespace SEEDEditor.VersionControl.Lore.Backend;

/// <summary>
/// LoreVcs を呼ぶクローン実装。
/// </summary>
public sealed class LoreNativeCloner : ILoreCloner
{
    /// <summary>中断済みで実行しなかったときに詰めるメッセージ。</summary>
    private const string MESSAGE_CANCELED_BEFORE_START = "操作は開始前に中断されました。";

    /// <summary>クローン元が空のときのメッセージ。</summary>
    private const string MESSAGE_REMOTE_URL_REQUIRED = "クローン元の URL が指定されていません。";

    /// <summary>クローン先が空のときのメッセージ。</summary>
    private const string MESSAGE_DESTINATION_REQUIRED = "クローン先のフォルダが指定されていません。";

    /// <summary>リポジトリをクローンする。</summary>
    /// <param name="request">依頼内容。</param>
    /// <param name="cancellationToken">中断用。</param>
    public LoreCallResult Clone(LoreCloneRequest request, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            return LoreCallResult.Canceled(MESSAGE_CANCELED_BEFORE_START);

        if (string.IsNullOrWhiteSpace(request.RemoteUrl))
        {
            return LoreCallResult.Failure(
                LoreCallResult.RETURN_CODE_INTERNAL_FAILURE,
                new[] { MESSAGE_REMOTE_URL_REQUIRED });
        }

        if (string.IsNullOrWhiteSpace(request.DestinationDir))
        {
            return LoreCallResult.Failure(
                LoreCallResult.RETURN_CODE_INTERNAL_FAILURE,
                new[] { MESSAGE_DESTINATION_REQUIRED });
        }

        var destination = Path.GetFullPath(request.DestinationDir);
        var credentials = LoreCredentialResolver.Resolve(
            request.Account, request.FallbackIdentity);

        try
        {
            // クローン先フォルダが無いと Lore がパスを解決できない。
            Directory.CreateDirectory(destination);

            using var globalArgs = new LoreGlobalArgs
            {
                // ★クローン先は RepositoryPath。CloneArgs には出力先が無い。
                RepositoryPath   = destination,
                WorkingDirectory = destination,

                // クローンはサーバ往復そのものなので、オフラインにはしない。
                Offline       = false,
                IdentityToken = credentials.IdentityToken,
                AccessToken   = credentials.AccessToken,
                Identity      = credentials.Identity,
            };

            using var cloneArgs = new LoreRepositoryCloneArgs
            {
                RepositoryUrl = request.RemoteUrl.Trim(),
            };

            var returnCode = LoreApi.RepositoryClone(globalArgs, cloneArgs).Wait();
            return returnCode == LoreCallResult.RETURN_CODE_SUCCESS
                ? LoreCallResult.Success
                : LoreCallResult.Failure(returnCode, Array.Empty<string>());
        }
        catch (LoreError error)
        {
            return LoreCallResult.Failure(error.ReturnCode, ToMessageList(error));
        }
        catch (Exception ex)
        {
            // ネイティブ DLL が無い・フォルダを作れない等。境界の外へ例外を出さない。
            return LoreCallResult.Failure(
                LoreCallResult.RETURN_CODE_INTERNAL_FAILURE, new[] { ex.Message });
        }
    }

    /// <summary>LoreError のメッセージを取り出す（null 安全）。</summary>
    /// <param name="error">Lore の例外。</param>
    private static IReadOnlyList<string> ToMessageList(LoreError error)
    {
        if (error.Messages is null) return new[] { error.Message ?? string.Empty };

        var list = new List<string>();
        foreach (var message in error.Messages)
        {
            if (!string.IsNullOrWhiteSpace(message)) list.Add(message);
        }
        if (list.Count == 0 && !string.IsNullOrWhiteSpace(error.Message)) list.Add(error.Message);
        return list;
    }
}
