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
using System.Collections.Generic;
using System.Globalization;
using System.Text;
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
    /// 変更の種類を行の右端に出す 1 文字へ直す（A / M / D / R / C / ! / ?）。
    ///
    /// <para>
    /// Visual Studio の「Git 変更」と同じ 1 文字表記にそろえる。
    /// ツリーの行は名前が長いので、種類は場所を取らない 1 文字にして右端へ寄せ、
    /// 文字数の揺れで列がガタつかないようにする
    /// （<see cref="ToChangeText"/> の「追加 / 変更 / …」はツールチップで補う）。
    /// 表示名と同じく、未解決の競合は種類より優先する。
    /// </para>
    /// </summary>
    /// <param name="kind">変更の種類。</param>
    /// <param name="conflict">競合の状態。</param>
    public static string ToChangeLetter(FileChangeKind kind, FileConflictState conflict)
    {
        if (conflict == FileConflictState.Unresolved)
        {
            return VersionControlMessages.PANEL_CHANGE_LETTER_CONFLICT;
        }

        return kind switch
        {
            FileChangeKind.Added    => VersionControlMessages.PANEL_CHANGE_LETTER_ADDED,
            FileChangeKind.Modified => VersionControlMessages.PANEL_CHANGE_LETTER_MODIFIED,
            FileChangeKind.Deleted  => VersionControlMessages.PANEL_CHANGE_LETTER_DELETED,
            FileChangeKind.Moved    => VersionControlMessages.PANEL_CHANGE_LETTER_MOVED,
            FileChangeKind.Copied   => VersionControlMessages.PANEL_CHANGE_LETTER_COPIED,
            _                       => VersionControlMessages.PANEL_CHANGE_LETTER_UNKNOWN,
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

    // ── 未送信でマージを止めたときのモーダル本文 ──────────────

    /// <summary>
    /// モーダルに並べる未送信ファイルの最大件数。これを超えた分は「ほか n 件」に畳む。
    /// 全部並べると画面からあふれ、最後の行（次に何をすればよいか）が読まれない。
    /// </summary>
    public const int UNSUBMITTED_WARNING_MAX_FILES = 10;

    /// <summary>
    /// 未送信の変更があってマージを止めたときのモーダル本文を組み立てる。
    ///
    /// <para>
    /// 「マージは実行されていません」を **1 行目**に置くのが要点。
    /// 2026-09-19 の事故では 1 行メッセージ（「送信していない変更が 1 件あります。
    /// 先に送信…」）だけで止めていたため、利用者はマージが走ったと思い込み、
    /// 邪魔していたロックファイルをそのまま送信してしまった。
    /// </para>
    /// </summary>
    /// <param name="changes">未送信の変更（走査結果そのまま）。</param>
    /// <param name="maxListedFiles">列挙する最大件数（既定は
    /// <see cref="UNSUBMITTED_WARNING_MAX_FILES"/>）。</param>
    /// <returns>モーダルへそのまま渡せる複数行の本文。</returns>
    public static string BuildUnsubmittedMergeWarning(
        IReadOnlyList<ChangedFile>? changes,
        int maxListedFiles = UNSUBMITTED_WARNING_MAX_FILES)
    {
        var count = changes?.Count ?? 0;
        var text  = new StringBuilder();

        text.AppendLine(VersionControlMessages.PANEL_BRANCH_MERGE_DIRTY_DIALOG_HEADER);
        text.AppendLine(string.Format(
            CultureInfo.CurrentCulture,
            VersionControlMessages.PANEL_BRANCH_MERGE_DIRTY_DIALOG_COUNT_FORMAT, count));

        // 列挙。件数が 0 でも（＝呼び出し側の想定外でも）本文は成立させる。
        var listed = Math.Min(count, Math.Max(0, maxListedFiles));
        for (int i = 0; i < listed; i++)
        {
            var file = changes![i];
            text.AppendLine(string.Format(
                CultureInfo.CurrentCulture,
                VersionControlMessages.PANEL_BRANCH_MERGE_DIRTY_DIALOG_FILE_FORMAT,
                file.Path, ToChangeText(file.Kind, file.Conflict)));
        }
        if (count > listed)
        {
            text.AppendLine(string.Format(
                CultureInfo.CurrentCulture,
                VersionControlMessages.PANEL_BRANCH_MERGE_DIRTY_DIALOG_MORE_FORMAT,
                count - listed));
        }

        // 最後は必ず「次に何をすればよいか」で終える。
        text.AppendLine();
        text.Append(VersionControlMessages.PANEL_BRANCH_MERGE_DIRTY_DIALOG_FOOTER);
        return text.ToString();
    }
}
