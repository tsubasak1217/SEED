// ============================================================
//  ConflictResolutionChoice.cs — 競合を解決するときの 2 択
//
//  【役割】
//  アーティストを含む利用者に見せる語彙を「自分の変更を残す」「リモートを採用」の
//  2 つだけに固定する。Lore 側の mine / theirs という語はここより上へ出さない。
//
//  【なぜ言い換えが必須なのか（Lore の罠）】
//  Lore の `resolve mine` / `resolve theirs` は、ファイル内に書き出される
//  diff3 マーカーの ours / theirs と **逆** の意味になる。
//    ・sync でのマージコミットは parent_self = 取り込んだ側（リモート）、
//      parent_other = 手元の現リビジョン、という並びで作られる
//      （lore-revision/src/commit.rs「Merge of divergent branch history,
//        other parent is current revision」）。
//    ・resolve mine  は parents()[0] = parent_self  を採る → **リモート側**
//    ・resolve theirs は parents()[1] = parent_other を採る → **ローカル側**
//  Lore 自身の doc コメントは merge_resolve_mine を "accepting the local (mine)
//  version" と書いているが、sync マージでは上記の並びになるため実際は逆であり、
//  実機検証（docs/vcs_lore.md 3.1）でもそう確認されている。
//  対応付けは Lore/LoreConflictResolutionMap.cs の 1 か所だけで行い、
//  単体テストで固定する。
//
//  【依存】
//  WPF にも LoreVcs にも依存しない（単体テストへそのままリンクできる）。
// ============================================================

namespace SEEDEditor.VersionControl.Model;

/// <summary>
/// 競合したファイルをどう解決するか。UI に出す選択肢と 1 対 1 に対応する。
/// </summary>
public enum ConflictResolutionChoice
{
    /// <summary>
    /// 自分の変更を残す（手元のファイルの内容を採用する）。
    /// UI 表示: 「自分の変更を残す」。
    /// </summary>
    KeepMine,

    /// <summary>
    /// リモートを採用する（サーバから来た内容で上書きする）。
    /// UI 表示: 「リモートを採用」。
    /// </summary>
    TakeRemote,
}
