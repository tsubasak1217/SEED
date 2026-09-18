// ============================================================
//  VersionControlSyncSummary.cs — 「↑ 未送信 / ↓ 未取得」の 1 行の中身
//
//  【役割】
//  <see cref="RemoteComparison"/>（リモートとの前後関係）を、
//  パネル上部の 1 行にそのまま貼れる文字列とツールチップへ直す。
//
//  【なぜ件数ではなく「あり／なし」なのか（重要）】
//  手本にした Visual Studio の「Git 変更」は「↑↓ 3 / 1」のように**件数**を出す。
//  Lore はそれを返さない。バックエンドが返すのは
//  `is_local_ahead` / `is_remote_ahead` という **真偽値だけ**で
//  （editor/src/VersionControl/Lore/Backend/LoreBackendRows.cs の
//    LoreStatusRevisionRow）、「何コミット進んでいるか」は持っていない。
//  数を書けないのに書けるふりをすると、利用者は嘘の数を信じて判断してしまう。
//  そこで件数の書式（<see cref="VersionControlMessages.PANEL_SYNC_UNPUSHED_COUNT_FORMAT"/>）は
//  将来のために残しつつ、いまは「あり／なし」で出す。
//
//  【なぜ「未確認」という状態が要るのか】
//  前後関係が分かるのは <see cref="StatusRefreshMode.ScanOnline"/> のときだけで、
//  オフライン取得（保存のたびの自動更新）では
//  <see cref="RemoteComparison.NotChecked"/> になる。
//  これを「なし」と書くと「送信するものは無い」という嘘になるため、
//  はっきり「未確認」と出し、どうすれば分かるかをツールチップで示す。
//
//  【依存】
//  WPF にも LoreVcs にも依存しない（単体テストへそのままリンクできる）。
// ============================================================

using System.Globalization;
using SEEDEditor.VersionControl.Model;

namespace SEEDEditor.VersionControl.Presentation;

/// <summary>
/// 未送信・未取得の状態の区分。
/// </summary>
public enum SyncPresence
{
    /// <summary>サーバへ問い合わせていない（または繋がらなかった）ので分からない。</summary>
    Unchecked,

    /// <summary>無い。</summary>
    None,

    /// <summary>ある（件数は分からない）。</summary>
    Present,
}

/// <summary>
/// パネル上部の「↑ 未送信 / ↓ 未取得」1 行の内容（不変）。
/// </summary>
public sealed class VersionControlSyncSummary
{
    /// <summary>未送信の状態。</summary>
    public SyncPresence Unpushed { get; }

    /// <summary>未取得の状態。</summary>
    public SyncPresence Unpulled { get; }

    /// <summary>未送信の件数（分かるときだけ非 null）。</summary>
    public int? UnpushedCount { get; }

    /// <summary>未取得の件数（分かるときだけ非 null）。</summary>
    public int? UnpulledCount { get; }

    /// <summary>未送信の表示文字列。</summary>
    public string UnpushedText { get; }

    /// <summary>未取得の表示文字列。</summary>
    public string UnpulledText { get; }

    /// <summary>1 行全体に付けるツールチップ（何も補足が要らないときは空文字）。</summary>
    public string Tooltip { get; }

    /// <summary>
    /// 全項目を指定して生成する（通常は <see cref="From"/> を使う）。
    /// </summary>
    /// <param name="unpushed">未送信の状態。</param>
    /// <param name="unpulled">未取得の状態。</param>
    /// <param name="tooltip">1 行のツールチップ。</param>
    /// <param name="unpushedCount">未送信の件数（分かるときだけ）。</param>
    /// <param name="unpulledCount">未取得の件数（分かるときだけ）。</param>
    public VersionControlSyncSummary(
        SyncPresence unpushed,
        SyncPresence unpulled,
        string tooltip = "",
        int? unpushedCount = null,
        int? unpulledCount = null)
    {
        Unpushed      = unpushed;
        Unpulled      = unpulled;
        UnpushedCount = unpushedCount;
        UnpulledCount = unpulledCount;
        Tooltip       = tooltip ?? string.Empty;

        UnpushedText = ToText(
            unpushed, unpushedCount,
            VersionControlMessages.PANEL_SYNC_UNPUSHED_COUNT_FORMAT,
            VersionControlMessages.PANEL_SYNC_UNPUSHED_PRESENT,
            VersionControlMessages.PANEL_SYNC_UNPUSHED_NONE,
            VersionControlMessages.PANEL_SYNC_UNPUSHED_UNCHECKED);

        UnpulledText = ToText(
            unpulled, unpulledCount,
            VersionControlMessages.PANEL_SYNC_UNPULLED_COUNT_FORMAT,
            VersionControlMessages.PANEL_SYNC_UNPULLED_PRESENT,
            VersionControlMessages.PANEL_SYNC_UNPULLED_NONE,
            VersionControlMessages.PANEL_SYNC_UNPULLED_UNCHECKED);
    }

    /// <summary>まだ状態を取っていないときの既定値（両方とも未確認）。</summary>
    public static VersionControlSyncSummary Unknown { get; } = new(
        SyncPresence.Unchecked,
        SyncPresence.Unchecked,
        VersionControlMessages.PANEL_SYNC_UNCHECKED_TOOLTIP);

    /// <summary>
    /// 作業コピーの状態から 1 行の内容を作る。
    /// </summary>
    /// <param name="status">作業コピーの状態（null なら「未確認」）。</param>
    public static VersionControlSyncSummary From(WorkingCopyStatus? status)
    {
        if (status is null) return Unknown;

        return status.RemoteState switch
        {
            // 問い合わせていない。ここを「なし」と書くと嘘になる。
            RemoteComparison.NotChecked => Unknown,

            // 問い合わせたが届かなかった。理由が違うのでツールチップを分ける。
            RemoteComparison.Unavailable => new VersionControlSyncSummary(
                SyncPresence.Unchecked, SyncPresence.Unchecked,
                VersionControlMessages.PANEL_SYNC_UNAVAILABLE_TOOLTIP),

            // リモートにまだこのブランチが無い＝手元のものは全部「未送信」。
            RemoteComparison.RemoteBranchMissing => new VersionControlSyncSummary(
                SyncPresence.Present, SyncPresence.None,
                VersionControlMessages.PANEL_SYNC_REMOTE_MISSING_TOOLTIP),

            RemoteComparison.InSync => new VersionControlSyncSummary(
                SyncPresence.None, SyncPresence.None),

            RemoteComparison.LocalAhead => new VersionControlSyncSummary(
                SyncPresence.Present, SyncPresence.None),

            RemoteComparison.RemoteAhead => new VersionControlSyncSummary(
                SyncPresence.None, SyncPresence.Present),

            RemoteComparison.Diverged => new VersionControlSyncSummary(
                SyncPresence.Present, SyncPresence.Present),

            // 列挙に値が増えたときは「分からない」側へ倒す（嘘をつかない）。
            _ => Unknown,
        };
    }

    /// <summary>
    /// 区分と件数から表示文字列を選ぶ。件数が分かるときだけ数を書く。
    /// </summary>
    /// <param name="presence">区分。</param>
    /// <param name="count">件数（分からなければ null）。</param>
    /// <param name="countFormat">件数つきの書式。</param>
    /// <param name="presentText">「あり」の文言。</param>
    /// <param name="noneText">「なし」の文言。</param>
    /// <param name="uncheckedText">「未確認」の文言。</param>
    private static string ToText(
        SyncPresence presence, int? count,
        string countFormat, string presentText, string noneText, string uncheckedText)
    {
        if (presence == SyncPresence.Unchecked) return uncheckedText;

        if (count is not null)
        {
            return string.Format(CultureInfo.CurrentCulture, countFormat, count.Value);
        }

        return presence == SyncPresence.Present ? presentText : noneText;
    }
}
