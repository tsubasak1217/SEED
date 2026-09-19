// ============================================================
//  ConflictSidePick.cs — 「並べて表示できないファイル」の 2 択と、その対応表
//
//  【役割】
//  バイナリ（png / blend など）や印の無いファイルはマージエディタで開けない。
//  そのときだけ出す小さな選択ダイアログの答えを表す型と、
//  それを <see cref="ConflictResolutionChoice"/> へ写す純関数を置く。
//
//  【なぜ選択肢の名前が「現在 / 取り込み元」なのか】
//  ダイアログに出す呼び名は進行中のマージで変わる
//  （sync なら「自分の変更 / リモート」、ブランチのマージならブランチ名）。
//  そこで型としては**画面上の位置**で呼び、表示名は呼び出し側が流し込む。
//
//  【mine / theirs の向きはここで扱わない】
//  「現在を残す」= <see cref="ConflictResolutionChoice.KeepMine"/>、
//  「取り込み元を採用」= <see cref="ConflictResolutionChoice.TakeRemote"/> で固定する。
//  ここから先の入れ替え（sync とブランチのマージで Lore の mine / theirs が逆になる）は
//  <c>LoreConflictResolutionMap</c> が 1 か所で吸収するので、この表は出どころを知らない。
//
//  【依存】
//  WPF にも LoreVcs にも依存しない（単体テストへそのままリンクできる）。
// ============================================================

using System;
using SEEDEditor.VersionControl.Model;

namespace SEEDEditor.VersionControl.Presentation;

/// <summary>
/// 「並べて表示できないファイル」の解決で、利用者が選んだ側。
/// </summary>
public enum ConflictSidePick
{
    /// <summary>取り消した（何もしない）。ヘッドレスでは必ずこれになる。</summary>
    Cancel,

    /// <summary>「現在」側（手元 / 取り込み先のブランチ）を残す。</summary>
    KeepCurrent,

    /// <summary>「取り込み元」側（リモート / 取り込み元のブランチ）を採用する。</summary>
    TakeIncoming,
}

/// <summary>
/// <see cref="ConflictSidePick"/> と <see cref="ConflictResolutionChoice"/> の対応表。
/// </summary>
public static class ConflictSidePickMap
{
    /// <summary>
    /// 選んだ側を、解決の選択肢へ写す。
    /// </summary>
    /// <param name="pick">ダイアログの答え。</param>
    /// <returns>解決の選択肢。取り消しなら <c>null</c>（＝何もしない）。</returns>
    /// <exception cref="ArgumentOutOfRangeException">未知の値が来たとき。</exception>
    public static ConflictResolutionChoice? ToResolutionChoice(ConflictSidePick pick)
        => pick switch
        {
            // 「現在」= 自分の作業コピー / 取り込み先のブランチ → 自分の変更を残す
            ConflictSidePick.KeepCurrent  => ConflictResolutionChoice.KeepMine,
            // 「取り込み元」= リモート / 取り込み元のブランチ → リモートを採用
            ConflictSidePick.TakeIncoming => ConflictResolutionChoice.TakeRemote,
            ConflictSidePick.Cancel       => null,

            // 選択肢が増えたのに表を更新し忘れたら、黙って誤った側を採るより落とす。
            _ => throw new ArgumentOutOfRangeException(
                     nameof(pick), pick, "未知の競合の側の選択です。"),
        };

    /// <summary>
    /// 選んだ側で「自分の変更が消える」かどうか。
    /// 取り返しがつかない側だけ確認ダイアログを重ねるために使う。
    /// </summary>
    /// <param name="pick">ダイアログの答え。</param>
    /// <returns>確認が要るか。</returns>
    public static bool NeedsConfirmation(ConflictSidePick pick)
        => ToResolutionChoice(pick) == ConflictResolutionChoice.TakeRemote;
}
