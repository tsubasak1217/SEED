// ============================================================
//  ILoreCloner.cs — リポジトリのクローンの境界
//
//  【なぜ ILoreBackend と別なのか】
//  <see cref="ILoreBackend"/> は **既にある作業コピー 1 つ** に対する操作の束で、
//  生成時に作業コピーのルートを要求する。クローンは
//  「まだ作業コピーが無い状態」で走るので、その型には収まらない。
//  無理に押し込むと「ルートが空でも壊れないバックエンド」という
//  例外的な状態を全メソッドが気にすることになる。
//
//  【使う場所】
//  Hub（スタート画面）の「プロジェクトに参加」だけ。参加は
//  join → ログイン → **トークン付きでクローン** → 開く、という流れで、
//  クローンの時点ではまだ VersionControlService も動いていない。
//
//  【スレッドの約束】
//  実装は同期（ブロッキング）でよい。呼び出し側（Hub）が
//  <c>Task.Run</c> で UI から外す。
//
//  【依存】
//  WPF にも LoreVcs にも依存しない（偽の実装を差したテストへリンクできる）。
// ============================================================

using System.Threading;

namespace SEEDEditor.VersionControl.Lore.Backend;

/// <summary>
/// クローンの依頼内容。
/// </summary>
/// <param name="RemoteUrl">
/// クローン元（`lore://host:41337/&lt;プロジェクト名&gt;`）。
/// </param>
/// <param name="DestinationDir">
/// クローン先のフォルダ（絶対パス）。Lore へは
/// <c>LoreGlobalArgs.RepositoryPath</c> として渡る。
/// </param>
/// <param name="Account">
/// ログイン中のアカウント。トークンがあれば identity は空にして渡される。
/// </param>
/// <param name="FallbackIdentity">
/// トークンが無いときに使う identity（匿名構成のサーバ向け。通常は空）。
/// </param>
public readonly record struct LoreCloneRequest(
    string RemoteUrl,
    string DestinationDir,
    LoreAccountCredential Account,
    string FallbackIdentity);

/// <summary>
/// リポジトリをクローンする。
/// </summary>
public interface ILoreCloner
{
    /// <summary>
    /// リモートのリポジトリをローカルへクローンする。
    /// </summary>
    /// <param name="request">依頼内容。</param>
    /// <param name="cancellationToken">中断用（Lore に中断 API は無く、開始前にしか効かない）。</param>
    /// <returns>Lore 呼び出しの結末。</returns>
    LoreCallResult Clone(LoreCloneRequest request, CancellationToken cancellationToken);
}
