// ============================================================
//  LoreLockTranslator.cs — ロック行 → モデルへの変換（所有者不明の扱い）
//
//  【役割】
//  Lore が返すロック行の owner を見て、「自分 / 他人 / 不明 / ロック無し」を決める。
//
//  【所有者が <unknown> になる条件（docs/vcs_lore.md 4 章）】
//  サーバに [server.auth]（JWT / OIDC）を設定しないと、Lore はロックの所有者も
//  push ユーザーも <unknown> として扱う。この状態では
//  「他人のロック」という概念そのものが成立しない。
//  ここで owner を自分の identity と単純比較すると、
//    ・自分のロックも他人のロックも同じ <unknown> になる
//    ・結果、他人のロックを「自分のもの」と誤判定して上書きさせてしまう
//  ので、不明は必ず不明として上へ伝える（<see cref="LockHolder.Unknown"/>）。
//
//  【二重取得の扱い】
//  Lore の lock acquire は既に取得済みのパスにも成功を返す。したがって
//  「取得できた」と「もともと誰かが持っていた」を終了コードで区別できない。
//  acquire の前後で状態を照会し、その差分から <see cref="LockAcquireOutcome"/> を決める。
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
/// ロック行をモデルへ変換する純粋関数の集まり。
/// </summary>
public static class LoreLockTranslator
{
    /// <summary>
    /// ロック行 1 件をモデルへ変換する。
    /// </summary>
    /// <param name="row">Lore が返したロック行。</param>
    /// <param name="selfIdentity">自分の identity（`.lore/config.toml` の値）。</param>
    public static LockInfo ToLockInfo(in LoreLockRow row, string? selfIdentity)
        => new(
            path:          row.Path,
            owner:         row.Owner,
            holder:        ResolveHolder(row, selfIdentity),
            branchName:    row.BranchName,
            acquiredAtUtc: ToUtc(row.UnixTimeSeconds));

    /// <summary>
    /// ロック行から「誰が持っているか」を判定する。
    /// </summary>
    /// <param name="row">Lore が返したロック行。</param>
    /// <param name="selfIdentity">自分の identity。</param>
    public static LockHolder ResolveHolder(in LoreLockRow row, string? selfIdentity)
    {
        // Lore が明示的に「ロックされていない」と言っているならそれに従う。
        if (!row.IsLocked) return LockHolder.None;

        // 所有者が <unknown>（または空）なら、自分かどうかは判断できない。
        // 「自分のもの」と決めつけないことが安全側。
        if (LockInfo.IsUnknownOwnerName(row.Owner)) return LockHolder.Unknown;

        // 自分の identity が分からなければ、同じく判断できない。
        if (string.IsNullOrWhiteSpace(selfIdentity)) return LockHolder.Unknown;

        return string.Equals(row.Owner.Trim(), selfIdentity.Trim(), StringComparison.OrdinalIgnoreCase)
            ? LockHolder.Self
            : LockHolder.Other;
    }

    /// <summary>
    /// ロック行の一覧をモデルの一覧へ変換する。
    /// </summary>
    /// <param name="rows">Lore が返したロック行。</param>
    /// <param name="selfIdentity">自分の identity。</param>
    public static IReadOnlyList<LockInfo> ToLockInfos(
        IReadOnlyList<LoreLockRow>? rows, string? selfIdentity)
    {
        if (rows is null || rows.Count == 0) return Array.Empty<LockInfo>();

        var result = new List<LockInfo>(rows.Count);
        foreach (var row in rows) result.Add(ToLockInfo(row, selfIdentity));
        return result;
    }

    /// <summary>
    /// 照会したパスごとに必ず 1 件の <see cref="LockInfo"/> を返す。
    ///
    /// <para>
    /// Lore の lock status はロックされていないパスの行を返さないことがある。
    /// 「返ってこなかった＝ロックされていない」という推測を呼び出し側にさせないため、
    /// ここで欠けたパスを <see cref="LockHolder.None"/> として補う。
    /// </para>
    /// </summary>
    /// <param name="requestedPaths">照会したリポジトリ相対パス。</param>
    /// <param name="rows">Lore が返したロック行。</param>
    /// <param name="selfIdentity">自分の identity。</param>
    /// <returns>照会したパスと同じ順序・同じ件数の一覧。</returns>
    public static IReadOnlyList<LockInfo> ToLockInfosForPaths(
        IReadOnlyList<string> requestedPaths,
        IReadOnlyList<LoreLockRow>? rows,
        string? selfIdentity)
    {
        ArgumentNullException.ThrowIfNull(requestedPaths);

        // パス比較は Windows の実運用に合わせて大文字小文字を区別しない。
        var byPath = new Dictionary<string, LoreLockRow>(StringComparer.OrdinalIgnoreCase);
        if (rows is not null)
        {
            foreach (var row in rows)
            {
                if (string.IsNullOrEmpty(row.Path)) continue;
                // 同じパスが複数返った場合は最初のものを採る。
                if (!byPath.ContainsKey(row.Path)) byPath[row.Path] = row;
            }
        }

        var result = new List<LockInfo>(requestedPaths.Count);
        foreach (var path in requestedPaths)
        {
            if (byPath.TryGetValue(path, out var row))
            {
                result.Add(ToLockInfo(row, selfIdentity));
            }
            else
            {
                // 行が無い＝ロックされていない、として明示的に 1 件返す。
                result.Add(new LockInfo(path, owner: string.Empty, holder: LockHolder.None));
            }
        }
        return result;
    }

    /// <summary>
    /// acquire の呼び出し結果と、acquire 後の状態から取得結果を決める。
    /// </summary>
    /// <param name="call">acquire の呼び出しの結末。</param>
    /// <param name="stateBefore">acquire 前の状態（自分が既に持っていたかの判定に使う）。</param>
    /// <param name="stateAfter">acquire 後の状態。</param>
    /// <returns>取得結果。</returns>
    public static LockAcquireResult ToAcquireResult(
        LoreCallResult call, LockInfo? stateBefore, LockInfo stateAfter)
    {
        ArgumentNullException.ThrowIfNull(call);
        ArgumentNullException.ThrowIfNull(stateAfter);

        // 呼び出し自体が失敗しているなら、状態を見るまでもない。
        if (!call.Succeeded) return new LockAcquireResult(LockAcquireOutcome.Failed, stateAfter);

        return stateAfter.Holder switch
        {
            // 自分が持っている。acquire 前から自分のものだったなら「もともと自分」。
            LockHolder.Self when stateBefore?.Holder == LockHolder.Self
                => new LockAcquireResult(LockAcquireOutcome.AlreadyMine, stateAfter),
            LockHolder.Self
                => new LockAcquireResult(LockAcquireOutcome.Acquired, stateAfter),

            // 他人が持っている。Lore は成功を返していても取得できていない。
            LockHolder.Other
                => new LockAcquireResult(LockAcquireOutcome.HeldByOther, stateAfter),

            // 所有者不明。サーバ認証を入れるまではここに落ちる。
            // 「取得できた」と言い切ると他人の作業を踏むので、別の結末として返す。
            LockHolder.Unknown
                => new LockAcquireResult(LockAcquireOutcome.HeldByUnknown, stateAfter),

            // 成功したはずなのにロックが無い＝状態を取れていない。失敗として扱う。
            _ => new LockAcquireResult(LockAcquireOutcome.Failed, stateAfter),
        };
    }

    /// <summary>
    /// Unix 秒を UTC の日時へ変換する。0 以下は「不明」として既定値を返す。
    /// </summary>
    /// <param name="unixTimeSeconds">Unix 秒。</param>
    public static DateTime ToUtc(long unixTimeSeconds)
    {
        if (unixTimeSeconds <= 0) return default;
        try
        {
            return DateTimeOffset.FromUnixTimeSeconds(unixTimeSeconds).UtcDateTime;
        }
        catch (ArgumentOutOfRangeException)
        {
            // Lore が桁の違う値（ミリ秒など）を返した場合に落ちないようにする。
            return default;
        }
    }
}
