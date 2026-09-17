// ============================================================
//  LoreBackendRows.cs — バックエンドが返す「生のイベント行」
//
//  【役割】
//  Lore の FFI イベントデータ（lore_repository_status_file_event_data_t など）を
//  そのままの語彙・そのままのフラグ構成で受け取るための型。
//
//  【なぜ生のまま持つのか】
//  FFI のイベントデータは **コールバック内でだけ有効** で、外へ持ち出すと
//  ObjectDisposedException になる。したがってコールバックの中で必ず値をコピーする。
//  そのコピー先がここ。モデル（ChangedFile など）への変換はコールバックの外で
//  行いたいので、変換は 1 つ上の Translator に任せ、ここは器に徹する。
//
//  【1 ファイルにまとめている理由】
//  これらは「バックエンド境界のデータ契約」という 1 つの役割を構成しており、
//  片方だけを差し替える場面が無い。契約が 1 画面で読めることを優先した。
//
//  【依存】
//  WPF にも LoreVcs にも依存しない（単体テストへそのままリンクできる）。
// ============================================================

using System;
using System.Collections.Generic;

namespace SEEDEditor.VersionControl.Lore.Backend;

/// <summary>
/// status が返すファイル 1 行（Lore のフラグをそのまま保持する）。
/// </summary>
/// <param name="Path">リポジトリ相対パス。</param>
/// <param name="Action">
/// Lore の action の文字列表現（"KEEP" / "ADD" / "DELETE" / "MOVE" / "COPY"）。
/// **"KEEP" が「内容が変わった」を意味する**（Lore に MODIFY という値は無い）。
/// enum ではなく文字列で持つのは、Lore 側に値が増えても
/// バックエンド境界がビルドエラーにならないようにするため。
/// 未知の値はモデル変換で <c>Unknown</c> になる。
/// </param>
/// <param name="FromPath">移動・複製の元パス（無ければ空文字）。</param>
/// <param name="SizeBytes">ファイルサイズ [byte]。</param>
/// <param name="FlagStaged">Lore の flag_staged。</param>
/// <param name="FlagDirty">Lore の flag_dirty。</param>
/// <param name="FlagMerged">Lore の flag_merged。</param>
/// <param name="FlagConflict">Lore の flag_conflict。</param>
/// <param name="FlagConflictUnresolved">Lore の flag_conflict_unresolved。</param>
/// <param name="FlagConflictAutomerged">Lore の flag_conflict_automerged。</param>
/// <param name="FlagConflictMine">
/// Lore の flag_conflict_mine。**「リモート側を採用した」を意味する**
/// （Lore の mine / theirs は sync マージでは逆転している。
///   詳細は ConflictResolutionChoice.cs のコメント）。
/// </param>
/// <param name="FlagConflictTheirs">
/// Lore の flag_conflict_theirs。**「ローカル側を採用した」を意味する**。
/// </param>
public readonly record struct LoreStatusFileRow(
    string Path,
    string Action,
    string FromPath,
    ulong  SizeBytes,
    bool   FlagStaged,
    bool   FlagDirty,
    bool   FlagMerged,
    bool   FlagConflict,
    bool   FlagConflictUnresolved,
    bool   FlagConflictAutomerged,
    bool   FlagConflictMine,
    bool   FlagConflictTheirs);

/// <summary>
/// status が返すリビジョン情報（1 回の status につき 1 件）。
/// </summary>
/// <param name="BranchName">現在のブランチ名。</param>
/// <param name="RevisionNumber">現在のリビジョン番号。</param>
/// <param name="RemoteRevisionNumber">リモートのリビジョン番号（未取得なら 0）。</param>
/// <param name="IsLocalAhead">ローカルが進んでいるか。</param>
/// <param name="IsRemoteAhead">リモートが進んでいるか。</param>
/// <param name="RemoteAvailable">サーバへ到達できたか。</param>
/// <param name="RemoteAuthorized">サーバのリビジョンを読む権限があったか。</param>
/// <param name="RemoteBranchExists">リモートに同名ブランチがあるか。</param>
public readonly record struct LoreStatusRevisionRow(
    string BranchName,
    ulong  RevisionNumber,
    ulong  RemoteRevisionNumber,
    bool   IsLocalAhead,
    bool   IsRemoteAhead,
    bool   RemoteAvailable,
    bool   RemoteAuthorized,
    bool   RemoteBranchExists);

/// <summary>
/// branch list が返すブランチ 1 行。
/// </summary>
/// <param name="Name">ブランチ名。</param>
/// <param name="Location">
/// Lore の location の文字列表現（"LOCAL" / "REMOTE" など）。
/// 同名ブランチが LOCAL と REMOTE で 2 行に分かれて届く。
/// </param>
/// <param name="IsCurrent">現在のブランチか。</param>
public readonly record struct LoreBranchRow(string Name, string Location, bool IsCurrent);

/// <summary>
/// revision history が返すリビジョン 1 行。
///
/// <para>
/// ★Lore v0.9.0 の制限: <c>REVISION_HISTORY_ENTRY</c> イベントが持つのは
/// <c>Revision</c> / <c>RevisionNumber</c> / <c>Parent</c> だけで、
/// **コミットメッセージ・作者・日時は含まれない**
/// （LoreRevisionHistoryEntryEventDataFFI をリフレクションで確認）。
/// それらはリビジョンのメタデータ（<c>Lore.RevisionMetadataGet</c>）として
/// 別途 1 件ずつ引く必要があり、今回はそこまで行わない。
/// したがって <see cref="Author"/> / <see cref="Message"/> /
/// <see cref="UnixTimeSeconds"/> は現状いつも空・0 になる。
/// 型としては残してあるので、メタデータ取得を足せばそのまま埋まる。
/// </para>
/// </summary>
/// <param name="Number">リビジョン番号。</param>
/// <param name="Id">リビジョン識別子（ハッシュの 16 進文字列）。</param>
/// <param name="Author">コミットした人（v0.9.0 の history では取得できず空）。</param>
/// <param name="Message">コミットメッセージ（v0.9.0 の history では取得できず空）。</param>
/// <param name="UnixTimeSeconds">コミット時刻（v0.9.0 の history では取得できず 0）。</param>
public readonly record struct LoreRevisionRow(
    ulong Number, string Id, string Author, string Message, long UnixTimeSeconds);

/// <summary>
/// lock query / lock status が返すロック 1 行。
/// </summary>
/// <param name="Path">リポジトリ相対パス。</param>
/// <param name="Owner">
/// 所有者（サーバ認証が無い構成では "&lt;unknown&gt;"）。
/// 空文字は「ロックされていない」を意味しない点に注意（判定は Translator で行う）。
/// </param>
/// <param name="BranchName">ロックが取得されたブランチ名。</param>
/// <param name="IsLocked">ロックされているか（status が明示的に返す場合に使う）。</param>
/// <param name="UnixTimeSeconds">取得時刻（Unix 秒。不明なら 0）。</param>
public readonly record struct LoreLockRow(
    string Path, string Owner, string BranchName, bool IsLocked, long UnixTimeSeconds);

/// <summary>
/// status 1 回分の生の結果（呼び出しの結末 + リビジョン情報 + ファイル行）。
/// </summary>
public sealed class LoreStatusResult
{
    /// <summary>呼び出しの結末。</summary>
    public LoreCallResult Call { get; }

    /// <summary>リビジョン情報（イベントが来なければ既定値）。</summary>
    public LoreStatusRevisionRow Revision { get; }

    /// <summary>ファイル行。</summary>
    public IReadOnlyList<LoreStatusFileRow> Files { get; }

    /// <summary>全項目を指定して生成する。</summary>
    /// <param name="call">呼び出しの結末。</param>
    /// <param name="revision">リビジョン情報。</param>
    /// <param name="files">ファイル行。</param>
    public LoreStatusResult(
        LoreCallResult call,
        LoreStatusRevisionRow revision,
        IReadOnlyList<LoreStatusFileRow>? files)
    {
        Call     = call;
        Revision = revision;
        Files    = files ?? Array.Empty<LoreStatusFileRow>();
    }

    /// <summary>失敗した status 結果を作る。</summary>
    /// <param name="call">呼び出しの結末（失敗）。</param>
    public static LoreStatusResult FromFailure(LoreCallResult call)
        => new(call, default, null);
}

/// <summary>
/// 一覧を返す呼び出しの生の結果（不変）。
/// </summary>
/// <typeparam name="TRow">行の型。</typeparam>
public sealed class LoreRowsResult<TRow>
{
    /// <summary>呼び出しの結末。</summary>
    public LoreCallResult Call { get; }

    /// <summary>取得した行。</summary>
    public IReadOnlyList<TRow> Rows { get; }

    /// <summary>結末と行を指定して生成する。</summary>
    /// <param name="call">呼び出しの結末。</param>
    /// <param name="rows">取得した行。</param>
    public LoreRowsResult(LoreCallResult call, IReadOnlyList<TRow>? rows)
    {
        Call = call;
        Rows = rows ?? Array.Empty<TRow>();
    }

    /// <summary>失敗した結果を作る。</summary>
    /// <param name="call">呼び出しの結末（失敗）。</param>
    public static LoreRowsResult<TRow> FromFailure(LoreCallResult call) => new(call, null);
}
