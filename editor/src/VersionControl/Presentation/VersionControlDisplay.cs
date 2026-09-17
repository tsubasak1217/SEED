// ============================================================
//  VersionControlDisplay.cs — モデルの値 → 画面に出す文字列・アイコンキー
//
//  【役割】
//  「FileChangeKind.Added をどう書くか」「ロックの保持者をどう書くか」といった
//  対応表を 1 か所へ集める。各所に switch を複製しない（プロジェクト規約）。
//
//  【アイコンはキー文字列で返す】
//  ここで ImageSource を作ると WPF に依存して単体テストへリンクできない。
//  返すのは `Icon.Vcs.*` のキー文字列だけにして、実際の描画はビューが行う
//  （`.claude/rules/editor-icons.md`: 記号文字を使わずベクターアイコンを使う）。
//
//  【依存】
//  WPF にも LoreVcs にも依存しない（単体テストへそのままリンクできる）。
// ============================================================

using System;
using System.Globalization;
using SEEDEditor.VersionControl.Model;

namespace SEEDEditor.VersionControl.Presentation;

/// <summary>
/// バージョン管理のモデル値を画面表示へ直す純関数群。
/// </summary>
public static class VersionControlDisplay
{
    // ── アイコンキー（Icons.xaml の x:Key と 1 対 1）────────────

    /// <summary>追加。</summary>
    public const string ICON_KEY_ADDED = "Icon.Vcs.Added";

    /// <summary>変更。</summary>
    public const string ICON_KEY_MODIFIED = "Icon.Vcs.Modified";

    /// <summary>削除。</summary>
    public const string ICON_KEY_DELETED = "Icon.Vcs.Deleted";

    /// <summary>移動・改名。</summary>
    public const string ICON_KEY_MOVED = "Icon.Vcs.Moved";

    /// <summary>複製。</summary>
    public const string ICON_KEY_COPIED = "Icon.Vcs.Copied";

    /// <summary>競合。</summary>
    public const string ICON_KEY_CONFLICT = "Icon.Vcs.Conflict";

    /// <summary>種類が分からない。</summary>
    public const string ICON_KEY_UNKNOWN = "Icon.Vcs.Unknown";

    /// <summary>ロック中。</summary>
    public const string ICON_KEY_LOCKED = "Icon.Lock";

    /// <summary>ロックなし。</summary>
    public const string ICON_KEY_UNLOCKED = "Icon.LockOpen";

    // ── 変更の種類 ──────────────────────────────────────────

    /// <summary>
    /// 変更の種類を画面の表示名へ直す。
    /// **未解決の競合は種類より先に「競合」と出す**（利用者が最初に知るべき情報のため）。
    /// </summary>
    /// <param name="kind">変更の種類。</param>
    /// <param name="conflict">競合の状態。</param>
    public static string ToChangeText(FileChangeKind kind, FileConflictState conflict)
    {
        if (conflict == FileConflictState.Unresolved)
        {
            return VersionControlMessages.PANEL_CHANGE_CONFLICT;
        }

        return kind switch
        {
            FileChangeKind.Added    => VersionControlMessages.PANEL_CHANGE_ADDED,
            FileChangeKind.Modified => VersionControlMessages.PANEL_CHANGE_MODIFIED,
            FileChangeKind.Deleted  => VersionControlMessages.PANEL_CHANGE_DELETED,
            FileChangeKind.Moved    => VersionControlMessages.PANEL_CHANGE_MOVED,
            FileChangeKind.Copied   => VersionControlMessages.PANEL_CHANGE_COPIED,
            _                       => VersionControlMessages.PANEL_CHANGE_UNKNOWN,
        };
    }

    /// <summary>
    /// 変更の種類に対応するアイコンキーを返す。
    /// 表示名と同じく、未解決の競合は種類より優先する。
    /// </summary>
    /// <param name="kind">変更の種類。</param>
    /// <param name="conflict">競合の状態。</param>
    public static string ToChangeIconKey(FileChangeKind kind, FileConflictState conflict)
    {
        if (conflict == FileConflictState.Unresolved) return ICON_KEY_CONFLICT;

        return kind switch
        {
            FileChangeKind.Added    => ICON_KEY_ADDED,
            FileChangeKind.Modified => ICON_KEY_MODIFIED,
            FileChangeKind.Deleted  => ICON_KEY_DELETED,
            FileChangeKind.Moved    => ICON_KEY_MOVED,
            FileChangeKind.Copied   => ICON_KEY_COPIED,
            _                       => ICON_KEY_UNKNOWN,
        };
    }

    // ── ロック ──────────────────────────────────────────────

    /// <summary>
    /// ロックの保持者を画面の表示名へ直す。
    ///
    /// <para>
    /// ★サーバに利用者認証が無い構成では所有者が <c>&lt;unknown&gt;</c> になる。
    /// これは異常ではなく普通に起こる状態なので、「不明」として淡々と出す
    /// （エラー扱いにしない）。
    /// </para>
    /// </summary>
    /// <param name="holder">保持者の区分。</param>
    /// <param name="owner">Lore が返した所有者名。</param>
    public static string ToLockHolderText(LockHolder holder, string? owner) => holder switch
    {
        LockHolder.Self    => VersionControlMessages.PANEL_LOCK_HOLDER_SELF,
        LockHolder.Other   => string.Format(
                                  VersionControlMessages.PANEL_LOCK_HOLDER_OTHER_FORMAT,
                                  string.IsNullOrWhiteSpace(owner)
                                      ? VersionControlMessages.PANEL_IDENTITY_UNKNOWN
                                      : owner),
        LockHolder.Unknown => VersionControlMessages.PANEL_LOCK_HOLDER_UNKNOWN,
        _                  => VersionControlMessages.PANEL_LOCK_HOLDER_NONE,
    };

    /// <summary>
    /// ロックを解除できるか（自分のロックだけ解除ボタンを出す）。
    ///
    /// <para>
    /// 所有者不明のロックは「自分のものかもしれない」が確定できない。
    /// ここで解除を許すと他人の編集権を黙って奪い得るので、出さない。
    /// </para>
    /// </summary>
    /// <param name="holder">保持者の区分。</param>
    public static bool CanRelease(LockHolder holder) => holder == LockHolder.Self;

    // ── 日時・リビジョン ────────────────────────────────────

    /// <summary>
    /// UTC の日時をローカル時刻の表示文字列へ直す。取得できていなければ「日時不明」。
    /// </summary>
    /// <param name="utc">UTC の日時（既定値なら不明）。</param>
    public static string ToTimestampText(DateTime utc)
    {
        if (utc == default) return VersionControlMessages.PANEL_TIMESTAMP_UNKNOWN;

        // 保持は UTC、表示はローカル。利用者が見るのは自分の時計の時刻。
        var local = utc.Kind == DateTimeKind.Utc
            ? utc.ToLocalTime()
            : DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime();

        return local.ToString(
            VersionControlMessages.PANEL_TIMESTAMP_FORMAT, CultureInfo.CurrentCulture);
    }

    /// <summary>リビジョン番号の表示文字列（例: "#12"）。</summary>
    /// <param name="number">リビジョン番号。</param>
    public static string ToRevisionNumberText(ulong number)
        => string.Format(
            CultureInfo.InvariantCulture,
            VersionControlMessages.PANEL_REVISION_NUMBER_FORMAT, number);

    /// <summary>
    /// 履歴のメッセージ表示。空なら「（メッセージなし）」にする。
    ///
    /// <para>
    /// Lore の history はメッセージを持たず、リビジョンのメタデータから補っている。
    /// メタデータを引けなかった行は空になるため、空欄のまま並べると
    /// 「壊れている」ように見える。必ず何か書く。
    /// </para>
    /// </summary>
    /// <param name="message">リビジョンのメッセージ。</param>
    public static string ToRevisionMessageText(string? message)
        => string.IsNullOrWhiteSpace(message)
            ? VersionControlMessages.PANEL_REVISION_NO_MESSAGE
            : message.Trim();

    /// <summary>
    /// 作者の表示。取得できていない・不明なら「利用者不明」にする。
    /// </summary>
    /// <param name="author">リビジョンの作者。</param>
    public static string ToAuthorText(string? author)
        => LockInfo.IsUnknownOwnerName(author)
            ? VersionControlMessages.PANEL_IDENTITY_UNKNOWN
            : author!.Trim();

    // ── ヘッダー ────────────────────────────────────────────

    /// <summary>
    /// 接続先と identity を 1 行にまとめる（例: "tsubasa — lore://127.0.0.1:41337"）。
    ///
    /// <para>
    /// SEED アカウントでログインしているときは、名前のうしろに「（ログイン中）」を付ける。
    /// 匿名（`.lore/config.toml` の identity をそのまま使っている）ときとの
    /// 違いが、パネルを見ただけで分かるようにするため
    /// ── ロックの「自分／他の人」がそもそも成立するかがここで決まる。
    /// </para>
    /// </summary>
    /// <param name="identity">現在の identity（ログイン中ならアカウント名）。</param>
    /// <param name="remoteUrl">リモート URL。</param>
    /// <param name="isSignedIn">SEED アカウントでログイン中か。</param>
    public static string ToConnectionText(
        string? identity, string? remoteUrl, bool isSignedIn = false)
    {
        var who = LockInfo.IsUnknownOwnerName(identity)
            ? VersionControlMessages.PANEL_IDENTITY_UNKNOWN
            : identity!.Trim();

        // 名前が分からないのに「ログイン中」とは出さない（矛盾した表示になる）。
        if (isSignedIn && !LockInfo.IsUnknownOwnerName(identity))
            who = string.Format(VersionControlMessages.PANEL_IDENTITY_SIGNED_IN_FORMAT, who);

        var where = string.IsNullOrWhiteSpace(remoteUrl)
            ? VersionControlMessages.PANEL_REMOTE_UNKNOWN
            : remoteUrl.Trim();

        return string.Format(VersionControlMessages.PANEL_CONNECTION_FORMAT, who, where);
    }
}
