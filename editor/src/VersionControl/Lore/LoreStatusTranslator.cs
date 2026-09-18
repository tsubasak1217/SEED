// ============================================================
//  LoreStatusTranslator.cs — status の生の行 → モデルへの変換
//
//  【役割】
//  Lore の status が返す
//    ・action の文字列（"Add" / "Modify" / ...）
//    ・8 個の flag_*
//    ・リビジョン情報（is_local_ahead / is_remote_ahead / remote_* の 3 種）
//  を、上の層が扱う ChangedFile / WorkingCopyStatus へ畳む。
//
//  【純粋関数にしてある理由】
//  ここが「Lore の癖」を吸収する唯一の場所なので、実サーバ無しで全分岐を
//  単体テストできる形（入力＝生の行、出力＝モデル、副作用なし）にしてある。
//
//  【依存】
//  WPF にも LoreVcs にも依存しない（単体テストへそのままリンクできる）。
// ============================================================

using System;
using System.Collections.Generic;
using SEEDEditor.VersionControl.Lore.Backend;
using SEEDEditor.VersionControl.Model;

namespace SEEDEditor.VersionControl.Lore;

/// <summary>
/// status の生の行をモデルへ変換する純粋関数の集まり。
/// </summary>
public static class LoreStatusTranslator
{
    // ── Lore の action 文字列（対応表の唯一の定義）──────────
    //  LoreFileAction は KEEP / ADD / DELETE / MOVE / COPY の 5 値しかない
    //  （lore-revision/src/interface.rs の LoreFileAction。C# 側の ToString() は
    //    ScreamingSnakeCase で "KEEP" のように返る）。
    //
    //  ★重要: **「変更」を表す値は MODIFY ではなく KEEP**。
    //  Lore は「ノードは残っている（= Keep）が中身が変わった」を Keep + dirty で表し、
    //  CLI も LoreFileAction::Keep を "M" として描いている
    //  （lore-revision/src/interface.rs as_string_short: Keep => "M"）。
    //  ここを取り違えると、変更されたファイルが全部「不明」になって一覧に出なくなる。
    //
    //  比較は大文字小文字を区別しない（バインディングの綴りが変わっても拾えるように）。

    /// <summary>変更（Lore では「ノードは残る」という意味の Keep）。</summary>
    private const string ACTION_KEEP = "KEEP";

    /// <summary>追加。</summary>
    private const string ACTION_ADD = "ADD";

    /// <summary>削除。</summary>
    private const string ACTION_DELETE = "DELETE";

    /// <summary>移動・改名。</summary>
    private const string ACTION_MOVE = "MOVE";

    /// <summary>複製。</summary>
    private const string ACTION_COPY = "COPY";

    /// <summary>
    /// Lore の action 文字列を <see cref="FileChangeKind"/> へ変換する。
    /// 未知の値は <see cref="FileChangeKind.Unknown"/>（落とさない）。
    /// </summary>
    /// <param name="action">Lore が返した action の文字列表現。</param>
    public static FileChangeKind ToChangeKind(string? action)
    {
        if (string.IsNullOrWhiteSpace(action)) return FileChangeKind.Unknown;

        var trimmed = action.Trim();
        if (Matches(trimmed, ACTION_KEEP))   return FileChangeKind.Modified;
        if (Matches(trimmed, ACTION_ADD))    return FileChangeKind.Added;
        if (Matches(trimmed, ACTION_DELETE)) return FileChangeKind.Deleted;
        if (Matches(trimmed, ACTION_MOVE))   return FileChangeKind.Moved;
        if (Matches(trimmed, ACTION_COPY))   return FileChangeKind.Copied;
        return FileChangeKind.Unknown;

        // Lore 側の綴りゆれ（"ADD" / "Add" / "add"）を吸収する。
        static bool Matches(string value, string expected)
            => string.Equals(value, expected, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 4 つの競合フラグを <see cref="FileConflictState"/> の 1 値へ畳む。
    ///
    /// <para>判定の順序には意味がある:</para>
    /// <list type="number">
    ///   <item>そもそも競合していない（flag_conflict が偽）→ None</item>
    ///   <item>未解決（flag_conflict_unresolved）→ Unresolved。
    ///         未解決なら「どちらを採ったか」の値は意味を持たないので先に見る</item>
    ///   <item>自動マージ済み → AutoMerged</item>
    ///   <item>解決済み。ここで **Lore の mine / theirs の向き** を吸収する。
    ///         向きは進行中のマージの出どころで入れ替わるので
    ///         <paramref name="origin"/> が要る（LoreConflictResolutionMap 参照）</item>
    ///   <item>競合フラグだけが立っていて、未解決・自動・mine・theirs のどれでもない
    ///         → 作業コピーの中身のまま解決済み（<see cref="FileConflictState.ResolvedWithContent"/>）</item>
    /// </list>
    /// <para>
    /// 最後の項は実測（2026-09-19、v0.9.0）に基づく。<c>branch merge resolve &lt;path&gt;</c>
    /// （mine / theirs 指定なし）の直後の status は
    /// <c>flagConflict=true, flagConflictUnresolved=false, automerged=false, mine=false, theirs=false</c>
    /// で、未解決のときは <c>flagConflictUnresolved=true</c> になる。
    /// 以前はこの形を「判別できない → 未解決」へ倒していたが、そうするとマージエディタや
    /// 「両方を取り込む」で解決した直後に「まだ競合している」と誤判定し、
    /// マージのコミットが永久に行われない。
    /// </para>
    /// </summary>
    /// <param name="row">status の 1 行。</param>
    /// <param name="origin">
    /// 進行中のマージの出どころ。既定は sync（印が無いときの扱いと揃える）。
    /// </param>
    public static FileConflictState ToConflictState(
        in LoreStatusFileRow row, MergeOrigin origin = MergeOrigin.Sync)
    {
        if (!row.FlagConflict) return FileConflictState.None;
        if (row.FlagConflictUnresolved) return FileConflictState.Unresolved;
        if (row.FlagConflictAutomerged) return FileConflictState.AutoMerged;

        // 対応表は LoreConflictResolutionMap だけが持つ（向きの知識を分散させない）。
        if (row.FlagConflictMine)
            return ToResolvedState(LoreResolveSide.Mine, origin);
        if (row.FlagConflictTheirs)
            return ToResolvedState(LoreResolveSide.Theirs, origin);

        // 競合フラグだけが立っている＝作業コピーの中身のまま解決済み
        // （Lore は「未解決」を flag_conflict_unresolved で明示するので、
        //   ここに来るのは解決済みだけ。実測は summary 参照）。
        return FileConflictState.ResolvedWithContent;
    }

    /// <summary>
    /// Lore の resolve 側を「解決済み」状態へ変換する。
    /// </summary>
    /// <param name="side">Lore 側の値。</param>
    /// <param name="origin">進行中のマージの出どころ。</param>
    private static FileConflictState ToResolvedState(LoreResolveSide side, MergeOrigin origin)
        => LoreConflictResolutionMap.ToChoice(side, origin) == ConflictResolutionChoice.KeepMine
            ? FileConflictState.ResolvedKeepMine
            : FileConflictState.ResolvedTakeRemote;

    /// <summary>
    /// status の 1 行を <see cref="ChangedFile"/> へ変換する。
    /// </summary>
    /// <param name="row">status の 1 行。</param>
    /// <param name="origin">進行中のマージの出どころ。</param>
    public static ChangedFile ToChangedFile(
        in LoreStatusFileRow row, MergeOrigin origin = MergeOrigin.Sync)
        => new(
            path:      row.Path,
            kind:      ToChangeKind(row.Action),
            conflict:  ToConflictState(row, origin),
            isStaged:  row.FlagStaged,
            isDirty:   row.FlagDirty,
            fromPath:  row.FromPath,
            sizeBytes: row.SizeBytes);

    /// <summary>
    /// status の全行を <see cref="ChangedFile"/> の一覧へ変換する。
    /// </summary>
    /// <param name="rows">status の行。</param>
    /// <param name="origin">進行中のマージの出どころ。</param>
    public static IReadOnlyList<ChangedFile> ToChangedFiles(
        IReadOnlyList<LoreStatusFileRow>? rows, MergeOrigin origin = MergeOrigin.Sync)
    {
        if (rows is null || rows.Count == 0) return Array.Empty<ChangedFile>();

        var result = new List<ChangedFile>(rows.Count);
        foreach (var row in rows) result.Add(ToChangedFile(row, origin));
        return result;
    }

    /// <summary>
    /// リビジョン情報からリモートとの前後関係を判定する。
    ///
    /// <para>判定の順序:</para>
    /// <list type="number">
    ///   <item>オフライン取得だった（問い合わせていない）→ NotChecked</item>
    ///   <item>サーバへ届かない／権限が無い → Unavailable</item>
    ///   <item>リモートに同名ブランチが無い → RemoteBranchMissing（初回 push 前）</item>
    ///   <item>双方が進んでいる → Diverged</item>
    ///   <item>片方だけ進んでいる → LocalAhead / RemoteAhead</item>
    ///   <item>どちらも進んでいない → InSync</item>
    /// </list>
    /// </summary>
    /// <param name="revision">status のリビジョン情報。</param>
    /// <param name="mode">この status をどのモードで取ったか。</param>
    public static RemoteComparison ToRemoteComparison(
        in LoreStatusRevisionRow revision, StatusRefreshMode mode)
    {
        // サーバへ問い合わせないモードでは、リモート側のフラグは常に 0 で返る。
        // それを「一致している」と読むと嘘になるので、明示的に「未確認」を返す。
        if (mode != StatusRefreshMode.ScanOnline) return RemoteComparison.NotChecked;

        if (!revision.RemoteAvailable || !revision.RemoteAuthorized)
            return RemoteComparison.Unavailable;

        if (!revision.RemoteBranchExists) return RemoteComparison.RemoteBranchMissing;

        if (revision.IsLocalAhead && revision.IsRemoteAhead) return RemoteComparison.Diverged;
        if (revision.IsLocalAhead)  return RemoteComparison.LocalAhead;
        if (revision.IsRemoteAhead) return RemoteComparison.RemoteAhead;

        return RemoteComparison.InSync;
    }

    /// <summary>
    /// status の生の結果を <see cref="WorkingCopyStatus"/> へ変換する。
    /// </summary>
    /// <param name="result">status の生の結果。</param>
    /// <param name="mode">この status をどのモードで取ったか。</param>
    /// <param name="retrievedAtUtc">取得時刻（省略時は現在時刻）。</param>
    /// <param name="origin">
    /// 進行中のマージの出どころ。「どちらで解決済みか」の表示に使う
    /// （解決済みの行のラベルだけに効き、未解決の判定には影響しない）。
    /// </param>
    public static WorkingCopyStatus ToWorkingCopyStatus(
        LoreStatusResult result, StatusRefreshMode mode, DateTime? retrievedAtUtc = null,
        MergeOrigin origin = MergeOrigin.Sync)
    {
        ArgumentNullException.ThrowIfNull(result);

        return new WorkingCopyStatus(
            branchName:     result.Revision.BranchName,
            revisionNumber: result.Revision.RevisionNumber,
            changes:        ToChangedFiles(result.Files, origin),
            remoteState:    ToRemoteComparison(result.Revision, mode),
            mode:           mode,
            retrievedAtUtc: retrievedAtUtc);
    }

    /// <summary>
    /// 「送るものがあるか」を判定する。
    ///
    /// <para>
    /// Lore は未 stage のまま commit すると **空のリビジョンを作って成功を返す**。
    /// これを防ぐため、stage の直後に status を取り直し、staged が 1 件も無ければ
    /// commit を呼ばない。判定はこの関数 1 か所で行う。
    /// </para>
    /// </summary>
    /// <param name="rows">stage 直後の status の行。</param>
    /// <returns>staged が 1 件以上あれば真。</returns>
    public static bool HasStagedChanges(IReadOnlyList<LoreStatusFileRow>? rows)
    {
        if (rows is null) return false;
        foreach (var row in rows)
        {
            if (row.FlagStaged) return true;
        }
        return false;
    }

    /// <summary>staged の件数を数える（送信件数の表示に使う）。</summary>
    /// <param name="rows">status の行。</param>
    public static int CountStaged(IReadOnlyList<LoreStatusFileRow>? rows)
    {
        if (rows is null) return 0;
        var count = 0;
        foreach (var row in rows)
        {
            if (row.FlagStaged) count++;
        }
        return count;
    }

    /// <summary>
    /// 未解決の競合だけを抜き出す。
    ///
    /// <para>
    /// Lore の sync は競合しても成功（終了コード 0）を返すため、
    /// 競合の有無は必ずこの関数（= status のフラグ）で判定する。
    /// </para>
    /// </summary>
    /// <param name="rows">sync 直後の status の行。</param>
    public static IReadOnlyList<ChangedFile> ExtractUnresolvedConflicts(
        IReadOnlyList<LoreStatusFileRow>? rows)
    {
        if (rows is null || rows.Count == 0) return Array.Empty<ChangedFile>();

        var conflicts = new List<ChangedFile>();
        foreach (var row in rows)
        {
            if (ToConflictState(row) == FileConflictState.Unresolved)
                conflicts.Add(ToChangedFile(row));
        }
        return conflicts;
    }
}
