// ============================================================
//  BranchInfo.cs — ブランチ 1 件（LOCAL / REMOTE を統合した後の形）
//
//  【役割】
//  Lore の branch list は同じ名前のブランチを LOCAL と REMOTE で **別々のイベント**
//  として返す（例: "main" が 2 件出る）。そのままパネルへ渡すと一覧に重複が出るため、
//  名前をキーに 1 件へ統合し、「どこに在るか」をフラグとして持つ形にする。
//  統合ロジックは Lore/LoreBranchTranslator.cs に置き、ここは器だけを定義する。
//
//  【依存】
//  WPF にも LoreVcs にも依存しない（単体テストへそのままリンクできる）。
// ============================================================

namespace SEEDEditor.VersionControl.Model;

/// <summary>
/// ブランチ 1 件（不変）。
/// </summary>
public sealed class BranchInfo
{
    /// <summary>ブランチ名。</summary>
    public string Name { get; }

    /// <summary>いま切り替わっているブランチか。</summary>
    public bool IsCurrent { get; }

    /// <summary>ローカルに存在するか。</summary>
    public bool ExistsLocally { get; }

    /// <summary>リモート（サーバ）に存在するか。</summary>
    public bool ExistsOnRemote { get; }

    /// <summary>
    /// まだ一度もサーバへ送られていないか（ローカルにだけ在る）。
    /// パネルはこれを「未送信」バッジに使える。
    /// </summary>
    public bool IsLocalOnly => ExistsLocally && !ExistsOnRemote;

    /// <summary>
    /// 手元にまだ無いか（サーバにだけ在る）。切り替えると取得が発生する。
    /// </summary>
    public bool IsRemoteOnly => !ExistsLocally && ExistsOnRemote;

    /// <summary>全項目を指定して生成する。</summary>
    /// <param name="name">ブランチ名。</param>
    /// <param name="isCurrent">現在のブランチか。</param>
    /// <param name="existsLocally">ローカルに存在するか。</param>
    /// <param name="existsOnRemote">リモートに存在するか。</param>
    public BranchInfo(string name, bool isCurrent, bool existsLocally, bool existsOnRemote)
    {
        Name           = name ?? string.Empty;
        IsCurrent      = isCurrent;
        ExistsLocally  = existsLocally;
        ExistsOnRemote = existsOnRemote;
    }

    /// <summary>ログ向けの 1 行表現。</summary>
    public override string ToString()
        => $"{Name}{(IsCurrent ? " *" : "")} local={ExistsLocally} remote={ExistsOnRemote}";
}
