// ============================================================
//  OperationReports.cs — 「送信」と「最新を取得」が返す明細
//
//  【役割】
//  結末（VersionControlOutcome）だけでは足りない、利用者へ見せたい内訳を持つ。
//    ・送信 … 何件を送ったのか、どのリビジョンになったのか
//    ・最新を取得 … 何件が更新され、どれが競合したのか
//
//  【1 ファイルにまとめている理由】
//  どちらも「1 回の操作の明細」という同じ役割で、片方だけを参照する場面が無い。
//  型ごとにファイルを割ると、対になっている事実が読み取りづらくなるため
//  ここだけは 2 型を同居させている。
//
//  【依存】
//  WPF にも LoreVcs にも依存しない（単体テストへそのままリンクできる）。
// ============================================================

using System;
using System.Collections.Generic;

namespace SEEDEditor.VersionControl.Model;

/// <summary>
/// 「送信」（変更を全部 stage → commit → push）の明細（不変）。
/// </summary>
public sealed class SubmitReport
{
    /// <summary>送信対象になったファイル数。</summary>
    public int FileCount { get; }

    /// <summary>コミット後のリビジョン番号。取得できなければ 0。</summary>
    public ulong RevisionNumber { get; }

    /// <summary>コミットメッセージ。</summary>
    public string Message { get; }

    /// <summary>
    /// commit までは成功したが push で弾かれたか。
    /// これが真のとき結末は <see cref="VersionControlOutcome.NeedsSync"/> になり、
    /// 「手元のコミットは残っている。最新を取得してからもう一度送信する」と案内できる。
    /// </summary>
    public bool CommittedButNotPushed { get; }

    /// <summary>全項目を指定して生成する。</summary>
    /// <param name="fileCount">送信対象のファイル数。</param>
    /// <param name="revisionNumber">コミット後のリビジョン番号。</param>
    /// <param name="message">コミットメッセージ。</param>
    /// <param name="committedButNotPushed">commit 成功・push 失敗か。</param>
    public SubmitReport(
        int fileCount, ulong revisionNumber, string? message, bool committedButNotPushed = false)
    {
        FileCount             = fileCount;
        RevisionNumber        = revisionNumber;
        Message               = message ?? string.Empty;
        CommittedButNotPushed = committedButNotPushed;
    }

    /// <summary>何も送らなかったことを表す明細。</summary>
    public static SubmitReport Nothing { get; } = new(0, 0, string.Empty);

    /// <summary>ログ向けの 1 行表現。</summary>
    public override string ToString()
        => $"files={FileCount} rev={RevisionNumber} pushed={!CommittedButNotPushed}";
}

/// <summary>
/// 「最新を取得」（sync）の明細（不変）。
/// </summary>
public sealed class SyncReport
{
    /// <summary>
    /// 競合して利用者の選択が必要になったファイル。
    ///
    /// <para>
    /// Lore の sync は競合しても終了コード 0 を返す。競合の有無はここの件数で判定すること
    /// （プロバイダが sync 後の status の <c>flagConflict*</c> から組み立てている）。
    /// </para>
    /// </summary>
    public IReadOnlyList<ChangedFile> Conflicts { get; }

    /// <summary>取得後のリビジョン番号。取得できなければ 0。</summary>
    public ulong RevisionNumber { get; }

    /// <summary>更新されたファイル数（競合を含む）。</summary>
    public int UpdatedFileCount { get; }

    /// <summary>競合が残っているか。</summary>
    public bool HasConflicts => Conflicts.Count > 0;

    /// <summary>全項目を指定して生成する。</summary>
    /// <param name="conflicts">競合したファイル。</param>
    /// <param name="revisionNumber">取得後のリビジョン番号。</param>
    /// <param name="updatedFileCount">更新されたファイル数。</param>
    public SyncReport(
        IReadOnlyList<ChangedFile>? conflicts, ulong revisionNumber, int updatedFileCount)
    {
        Conflicts        = conflicts ?? Array.Empty<ChangedFile>();
        RevisionNumber   = revisionNumber;
        UpdatedFileCount = updatedFileCount;
    }

    /// <summary>何も変わらなかったことを表す明細。</summary>
    public static SyncReport Nothing { get; } = new(null, 0, 0);

    /// <summary>ログ向けの 1 行表現。</summary>
    public override string ToString()
        => $"updated={UpdatedFileCount} conflicts={Conflicts.Count} rev={RevisionNumber}";
}
