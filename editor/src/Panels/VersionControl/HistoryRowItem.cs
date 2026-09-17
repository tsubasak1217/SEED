// ============================================================
//  HistoryRowItem.cs — 履歴タブの 1 行
//
//  【役割】
//  <see cref="SEEDEditor.VersionControl.Model.RevisionInfo"/> を、表示用に確定した
//  文字列だけの器へ写す。
//
//  【空欄を作らない】
//  Lore の `revision history` はリビジョン番号とハッシュしか返さず、
//  メッセージ・作者・日時はリビジョンのメタデータから補っている。
//  取得件数の上限を超えた行や、メタデータを引けなかった行は空になるため、
//  そのまま並べると「壊れている」ように見える。必ず代替の文言を入れる。
// ============================================================

using SEEDEditor.VersionControl.Model;
using SEEDEditor.VersionControl.Presentation;

namespace SEEDEditor.Panels.VersionControl;

/// <summary>
/// 履歴タブの 1 行（不変）。
/// </summary>
public sealed class HistoryRowItem
{
    /// <summary>リビジョン番号の表示（例: "#12"）。</summary>
    public string NumberText { get; }

    /// <summary>コミットメッセージ（空なら「（メッセージなし）」）。</summary>
    public string MessageText { get; }

    /// <summary>作者（不明なら「利用者不明」）。</summary>
    public string AuthorText { get; }

    /// <summary>日時（取得できていなければ「日時不明」）。</summary>
    public string TimestampText { get; }

    /// <summary>リビジョン識別子（ツールチップに出す。将来の差分表示で使う）。</summary>
    public string RevisionId { get; }

    /// <summary>リビジョン情報から表示用の行を作る。</summary>
    /// <param name="revision">元のリビジョン情報。</param>
    public HistoryRowItem(RevisionInfo revision)
    {
        NumberText    = VersionControlDisplay.ToRevisionNumberText(revision.Number);
        MessageText   = VersionControlDisplay.ToRevisionMessageText(revision.Message);
        AuthorText    = VersionControlDisplay.ToAuthorText(revision.Author);
        TimestampText = VersionControlDisplay.ToTimestampText(revision.TimestampUtc);
        RevisionId    = revision.Id;
    }
}
