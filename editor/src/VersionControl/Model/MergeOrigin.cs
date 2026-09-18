// ============================================================
//  MergeOrigin.cs — いま作業コピーで進行中のマージが「どこから来たか」
//
//  【なぜこの区別が必要なのか（実機で確認した Lore の罠）】
//  Lore の `branch merge resolve mine/theirs` が指す側は、
//  **マージの作られ方によって入れ替わる**。実測（結合テスト）:
//
//  | 進行中のマージ            | Lore の mine   | Lore の theirs |
//  |---------------------------|----------------|----------------|
//  | sync（最新を取得）        | リモート側     | ローカル側     |
//  | branch merge（取り込み）  | 現在のブランチ | 取り込み元     |
//
//  sync のマージだけが逆で、branch merge は直感どおりの並びになる。
//  原因は親の並び（lore-revision/src/commit.rs の
//  "Merge of divergent branch history, other parent is current revision"）が
//  sync の経路にだけ入るため。
//
//  つまり「自分の変更を残す」を Lore の何へ対応付けるかは、
//  **どちらのマージが進行中かを知らないと決められない**。取り違えると
//  利用者の変更が黙って消えるので、この区別を型で持つ。
//
//  【依存】
//  WPF にも LoreVcs にも依存しない（単体テストへそのままリンクできる）。
// ============================================================

namespace SEEDEditor.VersionControl.Model;

/// <summary>
/// 進行中のマージの出どころ。
/// </summary>
public enum MergeOrigin
{
    /// <summary>
    /// 「最新を取得」（sync）が作ったマージ。
    /// 記録が無いときの既定でもある（従来の挙動をそのまま保つため）。
    /// </summary>
    Sync,

    /// <summary>別のブランチを取り込む「ブランチのマージ」が作ったマージ。</summary>
    BranchMerge,
}
