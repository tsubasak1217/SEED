// ============================================================
//  LoreConflictResolutionMap.cs — 競合解決の対応表（ここ 1 か所だけ）
//
//  【役割】
//  利用者向けの 2 択（自分の変更を残す / リモートを採用）を
//  Lore の resolve mine / theirs へ対応付ける。
//
//  【なぜ逆なのか — 根拠】
//  sync でリモートを取り込むときに作られるマージの並びが、直感と逆になっている。
//    ・lore-revision/src/commit.rs:
//        "Merge of divergent branch history, other parent is current revision"
//      → parent_other（= parents()[1]）が **手元の現リビジョン**
//      → parent_self （= parents()[0]）が **取り込んだ側（リモート）**
//    ・lore-revision/src/stage.rs:
//        MergeParent::Mine   => state.parents()[0]
//        MergeParent::Theirs => state.parents()[1]
//    ・lore/src/branch.rs: merge_resolve_mine   → MergeParent::Mine
//                          merge_resolve_theirs → MergeParent::Theirs
//  したがって
//      resolve mine   → parents()[0] = リモート側
//      resolve theirs → parents()[1] = ローカル側
//  Lore 自身の doc コメントは merge_resolve_mine を "accepting the local (mine)
//  version" と書いているが、sync マージの並びでは上記のとおり逆になる。
//  実機検証（docs/vcs_lore.md 3.1）の結果とも一致する。
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
    /// <returns>Lore に渡す側（mine / theirs）。</returns>
    /// <exception cref="ArgumentOutOfRangeException">未知の選択肢が来たとき。</exception>
    public static LoreResolveSide ToLoreSide(ConflictResolutionChoice choice) => choice switch
    {
        // 「自分の変更を残す」= ローカル側 = parents()[1] = Lore の theirs
        ConflictResolutionChoice.KeepMine   => LoreResolveSide.Theirs,

        // 「リモートを採用」= リモート側 = parents()[0] = Lore の mine
        ConflictResolutionChoice.TakeRemote => LoreResolveSide.Mine,

        // 選択肢が増えたのに対応表を更新し忘れたら、黙って誤った側を採るより落とす。
        _ => throw new ArgumentOutOfRangeException(
                 nameof(choice), choice, "未知の競合解決の選択肢です。"),
    };

    /// <summary>
    /// Lore の resolve 側から利用者の選択へ逆変換する。
    /// status が返す flag_conflict_mine / flag_conflict_theirs を
    /// 「どちらで解決済みか」の表示に直すときに使う。
    /// </summary>
    /// <param name="side">Lore 側の値。</param>
    /// <returns>対応する利用者向けの選択肢。</returns>
    /// <exception cref="ArgumentOutOfRangeException">未知の値が来たとき。</exception>
    public static ConflictResolutionChoice ToChoice(LoreResolveSide side) => side switch
    {
        LoreResolveSide.Theirs => ConflictResolutionChoice.KeepMine,
        LoreResolveSide.Mine   => ConflictResolutionChoice.TakeRemote,
        _ => throw new ArgumentOutOfRangeException(
                 nameof(side), side, "未知の resolve 側です。"),
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
