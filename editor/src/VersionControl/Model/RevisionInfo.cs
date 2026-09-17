// ============================================================
//  RevisionInfo.cs — 履歴の 1 件
//
//  【役割】
//  「いつ・誰が・何を」だけをパネルへ渡す。Lore のハッシュや親リビジョンは
//  今の UI では使わないが、将来の差分表示で必要になるので Id は残す。
//
//  【取得できる条件】
//  Lore の history はサーバ必須（`history --offline` は失敗する）。
//  オフライン時はプロバイダが RequiresConnection を返し、この型は作られない。
//
//  【依存】
//  WPF にも LoreVcs にも依存しない（単体テストへそのままリンクできる）。
// ============================================================

using System;

namespace SEEDEditor.VersionControl.Model;

/// <summary>
/// 履歴 1 件（不変）。
/// </summary>
public sealed class RevisionInfo
{
    /// <summary>リビジョン番号（人が読む番号。ブランチ内で単調増加）。</summary>
    public ulong Number { get; }

    /// <summary>リビジョンの識別子（ハッシュの文字列表現）。</summary>
    public string Id { get; }

    /// <summary>
    /// コミットした人。
    /// サーバ認証が無い構成では Lore が <c>&lt;unknown&gt;</c> を返し得るため、
    /// 表示前に <see cref="LockInfo.IsUnknownOwner"/> と同じ判定を通すこと。
    /// </summary>
    public string Author { get; }

    /// <summary>コミットメッセージ。</summary>
    public string Message { get; }

    /// <summary>コミット時刻（UTC）。取得できなければ既定値。</summary>
    public DateTime TimestampUtc { get; }

    /// <summary>全項目を指定して生成する。</summary>
    /// <param name="number">リビジョン番号。</param>
    /// <param name="id">リビジョン識別子。</param>
    /// <param name="author">コミットした人。</param>
    /// <param name="message">コミットメッセージ。</param>
    /// <param name="timestampUtc">コミット時刻（UTC）。</param>
    public RevisionInfo(
        ulong number, string? id, string? author, string? message, DateTime timestampUtc)
    {
        Number       = number;
        Id           = id      ?? string.Empty;
        Author       = author  ?? string.Empty;
        Message      = message ?? string.Empty;
        TimestampUtc = timestampUtc;
    }

    /// <summary>ログ向けの 1 行表現。</summary>
    public override string ToString() => $"#{Number} {Author}: {Message}";
}
