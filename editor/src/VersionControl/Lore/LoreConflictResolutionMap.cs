// ============================================================
//  LoreConflictResolutionMap.cs — 競合解決の対応表（ここ 1 か所だけ）
//
//  【役割】
//  利用者向けの 2 択（自分の変更を残す / リモートを採用）を
//  Lore の resolve mine / theirs へ対応付ける。
//
//  【★対応は「進行中のマージがどちらか」で入れ替わる（実機で確認）】
//
//  | 進行中のマージ           | Lore の mine   | Lore の theirs | 「自分の変更を残す」 |
//  |--------------------------|----------------|----------------|----------------------|
//  | sync（最新を取得）       | リモート側     | ローカル側     | → Lore の theirs     |
//  | branch merge（取り込み） | 現在のブランチ | 取り込み元     | → Lore の mine       |
//
//  sync のマージだけが逆になる。根拠:
//    ・lore-revision/src/commit.rs:
//        "Merge of divergent branch history, other parent is current revision"
//      → sync の経路では parent_other（= parents()[1]）が **手元の現リビジョン**、
//        parent_self（= parents()[0]）が **取り込んだ側（リモート）**
//    ・lore-revision/src/stage.rs:
//        MergeParent::Mine   => state.parents()[0]
//        MergeParent::Theirs => state.parents()[1]
//    ・lore/src/branch.rs: merge_resolve_mine   → MergeParent::Mine
//                          merge_resolve_theirs → MergeParent::Theirs
//  branch merge（BranchMergeStart）には上の並べ替えが入らないため、
//  parents()[0] が現在のブランチ、parents()[1] が取り込み元になる。
//  どちらも結合テスト（LoreServerIntegrationTests）でファイルの中身から確認済み。
//
//  【この対応を 1 か所に閉じる理由】
//  逆転を各所で書くと、1 か所間違えただけで利用者の作業が消える。
//  対応付けはここだけで行い、単体テストで固定する。
//
//  【依存】
//  WPF にも LoreVcs にも依存しない（単体テストへそのままリンクできる）。
// ============================================================

using System;
using SEEDEditor.VersionControl.Lore.Backend;
using SEEDEditor.VersionControl.Model;

namespace SEEDEditor.VersionControl.Lore;

/// <summary>
/// 利用者の選択と Lore の resolve 側の対応表。
/// </summary>
public static class LoreConflictResolutionMap
{
    /// <summary>
    /// 利用者の選択を Lore の resolve 側へ変換する。
    /// </summary>
    /// <param name="choice">利用者の選択。</param>
    /// <param name="origin">
    /// 進行中のマージの出どころ。**これで対応が入れ替わる**ので省略できない。
    /// </param>
    /// <returns>Lore に渡す側（mine / theirs）。</returns>
    /// <exception cref="ArgumentOutOfRangeException">未知の選択肢・出どころが来たとき。</exception>
    public static LoreResolveSide ToLoreSide(ConflictResolutionChoice choice, MergeOrigin origin)
        => (choice, origin) switch
        {
            // ── sync のマージ（親の並びが逆）──
            // 「自分の変更を残す」= ローカル側 = parents()[1] = Lore の theirs
            (ConflictResolutionChoice.KeepMine,   MergeOrigin.Sync) => LoreResolveSide.Theirs,
            // 「リモートを採用」= リモート側 = parents()[0] = Lore の mine
            (ConflictResolutionChoice.TakeRemote, MergeOrigin.Sync) => LoreResolveSide.Mine,

            // ── ブランチのマージ（並びは直感どおり）──
            // 「自分の変更を残す」= 現在のブランチ = parents()[0] = Lore の mine
            (ConflictResolutionChoice.KeepMine,   MergeOrigin.BranchMerge) => LoreResolveSide.Mine,
            // 「リモートを採用」= 取り込み元 = parents()[1] = Lore の theirs
            (ConflictResolutionChoice.TakeRemote, MergeOrigin.BranchMerge) => LoreResolveSide.Theirs,

            // 選択肢や出どころが増えたのに対応表を更新し忘れたら、
            // 黙って誤った側を採るより落とす。
            _ => throw new ArgumentOutOfRangeException(
                     nameof(choice), (choice, origin), "未知の競合解決の組み合わせです。"),
        };

    /// <summary>
    /// Lore の resolve 側から利用者の選択へ逆変換する。
    /// status が返す flag_conflict_mine / flag_conflict_theirs を
    /// 「どちらで解決済みか」の表示に直すときに使う。
    /// </summary>
    /// <param name="side">Lore 側の値。</param>
    /// <param name="origin">進行中のマージの出どころ。</param>
    /// <returns>対応する利用者向けの選択肢。</returns>
    /// <exception cref="ArgumentOutOfRangeException">未知の値が来たとき。</exception>
    public static ConflictResolutionChoice ToChoice(LoreResolveSide side, MergeOrigin origin)
        => (side, origin) switch
        {
            (LoreResolveSide.Theirs, MergeOrigin.Sync) => ConflictResolutionChoice.KeepMine,
            (LoreResolveSide.Mine,   MergeOrigin.Sync) => ConflictResolutionChoice.TakeRemote,

            (LoreResolveSide.Mine,   MergeOrigin.BranchMerge) => ConflictResolutionChoice.KeepMine,
            (LoreResolveSide.Theirs, MergeOrigin.BranchMerge) => ConflictResolutionChoice.TakeRemote,

            _ => throw new ArgumentOutOfRangeException(
                     nameof(side), (side, origin), "未知の resolve 側の組み合わせです。"),
        };

    /// <summary>
    /// 利用者へ見せる選択肢の表示名を返す。
    /// </summary>
    /// <param name="choice">利用者の選択。</param>
    public static string ToDisplayName(ConflictResolutionChoice choice) => choice switch
    {
        ConflictResolutionChoice.KeepMine   => VersionControlMessages.CHOICE_KEEP_MINE,
        ConflictResolutionChoice.TakeRemote => VersionControlMessages.CHOICE_TAKE_REMOTE,
        _ => throw new ArgumentOutOfRangeException(
                 nameof(choice), choice, "未知の競合解決の選択肢です。"),
    };
}
