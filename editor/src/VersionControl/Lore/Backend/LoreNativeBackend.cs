// ============================================================
//  LoreNativeBackend.cs — ILoreBackend の実装（**唯一 LoreVcs に依存するファイル**）
//
//  【役割】
//  NuGet パッケージ LoreVcs 0.9.0 を呼び、結果を SEED 側の生データ（LoreCallResult /
//  Lore*Row）へ写す。ここより上は LoreVcs の型を一切知らない。
//
//  【LoreVcs の使い方で守っていること】
//  1. FFI イベントデータ（Lore*EventDataFFI）は **コールバックの中でだけ有効**。
//     外へ持ち出すと無効なネイティブメモリを指す。必要な値はその場で
//     record struct（Lore*Row）へコピーする。
//  2. LoreExecutor.Wait() は同期ブロッキングで、失敗時は LoreError を投げる。
//     ここで捕まえて LoreCallResult へ畳み、例外を境界の外へ出さない。
//     （WaitAsync() も存在するが、中で待つだけで UI を解放しないため使わない。
//       UI を止めない責務は SerialWorkerScheduler が持つ。）
//  3. LoreGlobalArgs / Lore*Args は IDisposable な struct なので必ず using で囲む。
//  4. Lore.Shutdown()（コード上は LoreApi.Shutdown()）はプロセスで 1 回だけ。個々の作業コピーの Dispose では呼ばない
//     （LoreShutdownGuard が担当する）。
//
//  【中断について（正直な限界）】
//  LoreVcs には実行中の操作を止める API が無い（Cancel / Abort に相当するものが
//  アセンブリ全体に存在しない）。そのため CancellationToken は
//  **各 Lore 呼び出しの手前でしか効かない**。開始後は完了まで走る。
//
//  【依存】
//  LoreVcs にのみ依存（WPF には依存しない）。単体テストはこのファイルをリンクせず、
//  ILoreBackend の偽物を差し替える。
// ============================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using LoreVcs;
using LoreVcs.Types;
using LoreVcs.Types.Args;
using LoreVcs.Types.Enums;
using LoreVcs.Types.Events;

// 自分の名前空間 SEEDEditor.VersionControl.Lore が LoreVcs の静的クラス Lore を
// 隠してしまうため、明示的な別名で参照する（`Lore.` と書くと名前空間の方に解決される）。
using LoreApi = global::LoreVcs.Lore;

namespace SEEDEditor.VersionControl.Lore.Backend;

/// <summary>
/// LoreVcs を呼ぶバックエンド実装。
/// </summary>
public sealed class LoreNativeBackend : ILoreBackend
{
    /// <summary>`.lore/config.toml` で remote_url を表すキー。</summary>
    private const string CONFIG_KEY_REMOTE_URL = "remote_url";

    /// <summary>`.lore/config.toml` で identity を表すキー。</summary>
    private const string CONFIG_KEY_IDENTITY = "identity";

    /// <summary>中断済みで実行しなかったときに詰めるメッセージ。</summary>
    private const string MESSAGE_CANCELED_BEFORE_START = "操作は開始前に中断されました。";

    /// <summary>作業コピーのルート（絶対パス）。</summary>
    public string WorkingCopyRoot { get; }

    /// <summary>リモート URL（`.lore/config.toml` から読む）。</summary>
    public string RemoteUrl { get; }

    /// <summary>identity（`.lore/config.toml` から読む）。</summary>
    public string Identity { get; }

    /// <summary>
    /// 作業コピーのルートを指定して生成する。
    ///
    /// <para>
    /// リモート URL と identity は Lore を呼ばずに `.lore/config.toml` から読む。
    /// <c>Lore.RepositoryInfo</c> でも取れるがサーバ往復が入る可能性があり、
    /// プロジェクトを開いた直後にそれを払う理由が無いため。
    /// </para>
    /// </summary>
    /// <param name="workingCopyRoot">作業コピーのルート（= プロジェクトルート）。</param>
    public LoreNativeBackend(string workingCopyRoot)
    {
        if (string.IsNullOrWhiteSpace(workingCopyRoot))
            throw new ArgumentException("作業コピーのルートが空です。", nameof(workingCopyRoot));

        WorkingCopyRoot = Path.GetFullPath(workingCopyRoot);

        var config = ReadWorkingCopyConfig(WorkingCopyRoot);
        RemoteUrl = config.TryGetValue(CONFIG_KEY_REMOTE_URL, out var url) ? url : string.Empty;
        Identity  = config.TryGetValue(CONFIG_KEY_IDENTITY,   out var id)  ? id  : string.Empty;
    }

    // ── 状態 ────────────────────────────────────────────────

    /// <summary>status を実行する。</summary>
    /// <param name="request">取得条件。</param>
    /// <param name="cancellationToken">中断用。</param>
    public LoreStatusResult Status(LoreStatusRequest request, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            return LoreStatusResult.FromFailure(CanceledResult());

        // コールバックの外へ持ち出す値はここへ溜める（FFI データは外で使えない）。
        var files    = new List<LoreStatusFileRow>();
        var revision = default(LoreStatusRevisionRow);

        using var globalArgs = NewGlobalArgs(request.Offline);
        using var statusArgs = new LoreRepositoryStatusArgs
        {
            // staged は常に要求する。空コミット防止の判定に staged 件数が要るため。
            Staged = true,
            Scan   = request.Scan,
        };

        var call = Execute(() => LoreApi.RepositoryStatus(globalArgs, statusArgs)
            .Callback((evt, _) =>
            {
                switch (evt.Tag)
                {
                    case LoreEventTag.REPOSITORY_STATUS_FILE:
                    {
                        var data = evt.GetData<LoreRepositoryStatusFileEventDataFFI>();
                        files.Add(new LoreStatusFileRow(
                            Path:                   data.Path ?? string.Empty,
                            Action:                 data.Action.ToString(),
                            FromPath:               data.FromPath ?? string.Empty,
                            SizeBytes:              data.Size,
                            FlagStaged:             data.FlagStaged,
                            FlagDirty:              data.FlagDirty,
                            FlagMerged:             data.FlagMerged,
                            FlagConflict:           data.FlagConflict,
                            FlagConflictUnresolved: data.FlagConflictUnresolved,
                            FlagConflictAutomerged: data.FlagConflictAutomerged,
                            FlagConflictMine:       data.FlagConflictMine,
                            FlagConflictTheirs:     data.FlagConflictTheirs));
                        break;
                    }
                    case LoreEventTag.REPOSITORY_STATUS_REVISION:
                    {
                        var data = evt.GetData<LoreRepositoryStatusRevisionEventDataFFI>();
                        revision = new LoreStatusRevisionRow(
                            BranchName:           data.BranchName ?? string.Empty,
                            RevisionNumber:       data.RevisionNumber,
                            RemoteRevisionNumber: data.RevisionRemoteNumber,
                            IsLocalAhead:         data.IsLocalAhead,
                            IsRemoteAhead:        data.IsRemoteAhead,
                            RemoteAvailable:      data.RemoteAvailable,
                            RemoteAuthorized:     data.RemoteAuthorized,
                            // Lore 側の綴りは RemoteBranchExist（末尾に s は付かない）。
                            RemoteBranchExists:   data.RemoteBranchExist);
                        break;
                    }
                }
            })
            .Wait());

        return call.Succeeded
            ? new LoreStatusResult(call, revision, files)
            : LoreStatusResult.FromFailure(call);
    }

    // ── 変更の記録 ──────────────────────────────────────────

    /// <summary>ファイルを dirty として記録する。</summary>
    /// <param name="relativePaths">リポジトリ相対パス。</param>
    /// <param name="cancellationToken">中断用。</param>
    public LoreCallResult FileDirty(
        IReadOnlyList<string> relativePaths, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested) return CanceledResult();

        using var globalArgs = NewGlobalArgs(offline: true);
        using var args = new LoreFileDirtyArgs { Paths = ToArray(relativePaths) };
        return Execute(() => LoreApi.FileDirty(globalArgs, args).Wait());
    }

    /// <summary>ファイルの移動を記録する。</summary>
    /// <param name="fromRelativePath">移動前のリポジトリ相対パス。</param>
    /// <param name="toRelativePath">移動後のリポジトリ相対パス。</param>
    /// <param name="cancellationToken">中断用。</param>
    public LoreCallResult FileStageMove(
        string fromRelativePath, string toRelativePath, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested) return CanceledResult();

        using var globalArgs = NewGlobalArgs(offline: true);
        using var args = new LoreFileStageMoveArgs
        {
            FromPath = fromRelativePath,
            ToPath   = toRelativePath,
        };
        return Execute(() => LoreApi.FileStageMove(globalArgs, args).Wait());
    }

    /// <summary>ファイルを stage する。</summary>
    /// <param name="relativePaths">リポジトリ相対パス。</param>
    /// <param name="scan">ディレクトリを再帰的に走査するか。</param>
    /// <param name="cancellationToken">中断用。</param>
    public LoreCallResult FileStage(
        IReadOnlyList<string> relativePaths, bool scan, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested) return CanceledResult();

        using var globalArgs = NewGlobalArgs(offline: true);
        using var args = new LoreFileStageArgs
        {
            Paths = ToArray(relativePaths),
            Scan  = scan,
            // CaseChange は「大文字小文字だけが違う改名」の扱い（0 = エラー）。
            // 既定のまま（0）にしておき、必要になったら設定として外へ出す。
        };
        return Execute(() => LoreApi.FileStage(globalArgs, args).Wait());
    }

    // ── 送信・取得 ──────────────────────────────────────────

    /// <summary>コミットする。</summary>
    /// <param name="message">コミットメッセージ。</param>
    /// <param name="cancellationToken">中断用。</param>
    public LoreCallResult Commit(string message, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested) return CanceledResult();

        // commit はローカル操作なので offline でよい（サーバ往復を避ける）。
        using var globalArgs = NewGlobalArgs(offline: true);
        using var args = new LoreRevisionCommitArgs { Message = message };
        return Execute(() => LoreApi.RevisionCommit(globalArgs, args).Wait());
    }

    /// <summary>現在のブランチを push する。</summary>
    /// <param name="cancellationToken">中断用。</param>
    public LoreCallResult Push(CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested) return CanceledResult();

        using var globalArgs = NewGlobalArgs(offline: false);
        using var args = new LoreBranchPushArgs
        {
            // Branch を空にすると現在のブランチが対象になる。
            Branch = string.Empty,
            // FastForwardMerge はサーバ側での早送りマージを許可する指定。
            // 分岐を黙って混ぜると利用者が気づけないので、明示的に無効にする。
            // 分岐したときは push が失敗し、こちらから「最新を取得」を促す。
            FastForwardMerge = false,
        };
        return Execute(() => LoreApi.BranchPush(globalArgs, args).Wait());
    }

    /// <summary>ブランチ先端へ同期する。</summary>
    /// <param name="cancellationToken">中断用。</param>
    public LoreCallResult Sync(CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested) return CanceledResult();

        using var globalArgs = NewGlobalArgs(offline: false);
        using var args = new LoreRevisionSyncArgs
        {
            // Revision を空にするとブランチ先端が対象になる。
            Revision = string.Empty,
            // Reset を真にするとローカルの変更が捨てられる。絶対に立てない。
            Reset = false,
        };
        return Execute(() => LoreApi.RevisionSync(globalArgs, args).Wait());
    }

    /// <summary>競合を解決する。</summary>
    /// <param name="relativePaths">対象のリポジトリ相対パス。</param>
    /// <param name="side">どちらの親を採るか。</param>
    /// <param name="cancellationToken">中断用。</param>
    public LoreCallResult MergeResolve(
        IReadOnlyList<string> relativePaths, LoreResolveSide side,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested) return CanceledResult();

        var paths = ToArray(relativePaths);

        // ★オフラインにしてはいけない（実機で確認済み）。
        //   「リモートを採用」（Lore の resolve mine）は取り込んだ側の中身を
        //   復元するが、その実体がローカルストアに無いことがある。
        //   offline を立てるとサーバから取ってこられず、
        //   "Unable to restore path to selected state: Address not found: ..."
        //   で失敗する。「自分の変更を残す」側は中身が手元にあるので
        //   オフラインでも通ってしまい、片方だけ壊れることに気づきにくい。
        using var globalArgs = NewGlobalArgs(offline: false);

        // LoreVcs では mine / theirs は引数ではなく **別メソッド**。
        // どちらが利用者のどの選択に対応するかは LoreConflictResolutionMap が決める。
        if (side == LoreResolveSide.Mine)
        {
            using var mineArgs = new LoreBranchMergeResolveMineArgs { Paths = paths };
            return Execute(() => LoreApi.BranchMergeResolveMine(globalArgs, mineArgs).Wait());
        }

        using var theirsArgs = new LoreBranchMergeResolveTheirsArgs { Paths = paths };
        return Execute(() => LoreApi.BranchMergeResolveTheirs(globalArgs, theirsArgs).Wait());
    }

    // ── ブランチ ────────────────────────────────────────────

    /// <summary>ブランチを一覧する。</summary>
    /// <param name="cancellationToken">中断用。</param>
    public LoreRowsResult<LoreBranchRow> BranchList(CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            return LoreRowsResult<LoreBranchRow>.FromFailure(CanceledResult());

        var rows = new List<LoreBranchRow>();

        // ブランチ一覧はリモート側も見たいのでオンラインで引く。
        // サーバに繋がらなければ Lore はローカル分だけを返す。
        using var globalArgs = NewGlobalArgs(offline: false);
        using var args = new LoreBranchListArgs { Archived = false };

        var call = Execute(() => LoreApi.BranchList(globalArgs, args)
            .Callback((evt, _) =>
            {
                if (evt.Tag != LoreEventTag.BRANCH_LIST_ENTRY) return;

                var data = evt.GetData<LoreBranchListEntryEventDataFFI>();
                rows.Add(new LoreBranchRow(
                    Name:      data.Name ?? string.Empty,
                    Location:  data.Location.ToString(),
                    IsCurrent: data.IsCurrent));
            })
            .Wait());

        return new LoreRowsResult<LoreBranchRow>(call, rows);
    }

    /// <summary>ブランチを作る。</summary>
    /// <param name="name">ブランチ名。</param>
    /// <param name="cancellationToken">中断用。</param>
    public LoreCallResult BranchCreate(string name, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested) return CanceledResult();

        using var globalArgs = NewGlobalArgs(offline: false);
        using var args = new LoreBranchCreateArgs { Branch = name };
        return Execute(() => LoreApi.BranchCreate(globalArgs, args).Wait());
    }

    /// <summary>ブランチを切り替える。</summary>
    /// <param name="name">ブランチ名。</param>
    /// <param name="cancellationToken">中断用。</param>
    public LoreCallResult BranchSwitch(string name, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested) return CanceledResult();

        using var globalArgs = NewGlobalArgs(offline: false);
        using var args = new LoreBranchSwitchArgs
        {
            Branch = name,
            // Reset を真にするとローカルの変更が捨てられる。絶対に立てない。
            Reset = false,
            Bare  = false,
        };
        return Execute(() => LoreApi.BranchSwitch(globalArgs, args).Wait());
    }

    // ── 履歴 ────────────────────────────────────────────────

    /// <summary>履歴を取得する（サーバ必須）。</summary>
    /// <param name="maxCount">最大件数。</param>
    /// <param name="cancellationToken">中断用。</param>
    public LoreRowsResult<LoreRevisionRow> History(
        int maxCount, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            return LoreRowsResult<LoreRevisionRow>.FromFailure(CanceledResult());

        var rows = new List<LoreRevisionRow>();

        using var globalArgs = NewGlobalArgs(offline: false);
        using var args = new LoreRevisionHistoryArgs
        {
            Length = maxCount > 0 ? (uint)maxCount : 0u,
        };

        var call = Execute(() => LoreApi.RevisionHistory(globalArgs, args)
            .Callback((evt, _) =>
            {
                if (evt.Tag != LoreEventTag.REVISION_HISTORY_ENTRY) return;

                var data = evt.GetData<LoreRevisionHistoryEntryEventDataFFI>();
                // ★このイベントは番号・ハッシュ・親しか持たない。
                //   メッセージ・作者・日時は revision metadata を別途引かないと取れない
                //   （LoreBackendRows.cs の LoreRevisionRow のコメント参照）。
                rows.Add(new LoreRevisionRow(
                    Number:          data.RevisionNumber,
                    Id:              ToHex(data.Revision.Data),
                    Author:          string.Empty,
                    Message:         string.Empty,
                    UnixTimeSeconds: 0));
            })
            .Wait());

        return new LoreRowsResult<LoreRevisionRow>(call, rows);
    }

    // ── ロック ──────────────────────────────────────────────

    /// <summary>現在のブランチのロックを一覧する。</summary>
    /// <param name="cancellationToken">中断用。</param>
    public LoreRowsResult<LoreLockRow> LockQuery(CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            return LoreRowsResult<LoreLockRow>.FromFailure(CanceledResult());

        var rows = new List<LoreLockRow>();

        using var globalArgs = NewGlobalArgs(offline: false);
        using var args = new LoreLockFileQueryArgs
        {
            // すべて空 = 現在のブランチ・全所有者・全パス。
            Branch = string.Empty,
            Owner  = string.Empty,
            Path   = string.Empty,
        };

        var call = Execute(() => LoreApi.LockFileQuery(globalArgs, args)
            .Callback((evt, _) =>
            {
                if (evt.Tag != LoreEventTag.LOCK_FILE_QUERY) return;

                var data = evt.GetData<LoreLockFileQueryEventDataFFI>();
                // このイベントが来た＝そのパスはロックされている。
                rows.Add(new LoreLockRow(
                    Path:            data.Path ?? string.Empty,
                    Owner:           data.Owner ?? string.Empty,
                    BranchName:      string.Empty,
                    IsLocked:        true,
                    UnixTimeSeconds: ToSignedSeconds(data.LockedAt)));
            })
            .Wait());

        return new LoreRowsResult<LoreLockRow>(call, rows);
    }

    /// <summary>ロックを取得する。</summary>
    /// <param name="relativePaths">リポジトリ相対パス。</param>
    /// <param name="cancellationToken">中断用。</param>
    public LoreCallResult LockAcquire(
        IReadOnlyList<string> relativePaths, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested) return CanceledResult();

        using var globalArgs = NewGlobalArgs(offline: false);
        using var args = new LoreLockFileAcquireArgs
        {
            Paths  = ToArray(relativePaths),
            Branch = string.Empty,
        };
        return Execute(() => LoreApi.LockFileAcquire(globalArgs, args).Wait());
    }

    /// <summary>ロックを解放する。</summary>
    /// <param name="relativePaths">リポジトリ相対パス。</param>
    /// <param name="cancellationToken">中断用。</param>
    public LoreCallResult LockRelease(
        IReadOnlyList<string> relativePaths, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested) return CanceledResult();

        using var globalArgs = NewGlobalArgs(offline: false);
        using var args = new LoreLockFileReleaseArgs
        {
            Paths = ToArray(relativePaths),
            // Branch / Owner / OwnerId を空にすると「現在のブランチの自分のロック」になる。
            Branch  = string.Empty,
            Owner   = string.Empty,
            OwnerId = string.Empty,
        };
        return Execute(() => LoreApi.LockFileRelease(globalArgs, args).Wait());
    }

    /// <summary>指定パスのロック状態を取得する。</summary>
    /// <param name="relativePaths">リポジトリ相対パス。</param>
    /// <param name="cancellationToken">中断用。</param>
    public LoreRowsResult<LoreLockRow> LockStatus(
        IReadOnlyList<string> relativePaths, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            return LoreRowsResult<LoreLockRow>.FromFailure(CanceledResult());

        var rows = new List<LoreLockRow>();

        using var globalArgs = NewGlobalArgs(offline: false);
        using var args = new LoreLockFileStatusArgs
        {
            Paths  = ToArray(relativePaths),
            Branch = string.Empty,
        };

        var call = Execute(() => LoreApi.LockFileStatus(globalArgs, args)
            .Callback((evt, _) =>
            {
                if (evt.Tag != LoreEventTag.LOCK_FILE_STATUS) return;

                var data = evt.GetData<LoreLockFileStatusEventDataFFI>();
                // ロックされていないパスはイベントが来ない。
                // 欠けた分は LoreLockTranslator.ToLockInfosForPaths が補う。
                rows.Add(new LoreLockRow(
                    Path:            data.Path ?? string.Empty,
                    Owner:           data.Owner ?? string.Empty,
                    BranchName:      string.Empty,
                    IsLocked:        true,
                    UnixTimeSeconds: ToSignedSeconds(data.LockedAt)));
            })
            .Wait());

        return new LoreRowsResult<LoreLockRow>(call, rows);
    }

    // ── 共通ヘルパー ────────────────────────────────────────

    /// <summary>
    /// この作業コピー向けの共通引数を作る。
    /// </summary>
    /// <param name="offline">サーバへ接続しないか。</param>
    private LoreGlobalArgs NewGlobalArgs(bool offline) => new()
    {
        RepositoryPath = WorkingCopyRoot,

        // ★これが無いと壊れる（実機で確認済み）。
        //   Lore は引数に渡された **相対パスを WorkingDirectory から解決する** が、
        //   空だと「呼び出したプロセスのカレントディレクトリ」が使われる。
        //   エディタのカレントディレクトリは作業コピーとは無関係なので、
        //   `lock acquire assets/a.png` が
        //   `invalid path: <エディタの起動フォルダ>/assets/a.png` で失敗したり、
        //   `merge resolve` が **どのファイルにも一致せず、しかも成功（rc=0）を返して**
        //   競合が解決されないまま進んでしまう。
        WorkingDirectory = WorkingCopyRoot,

        Offline  = offline,
        // identity は `.lore/config.toml` の値が使われるので、空なら指定しない。
        Identity = Identity,
    };

    /// <summary>
    /// Lore 呼び出しを実行し、LoreError を <see cref="LoreCallResult"/> へ畳む。
    /// </summary>
    /// <param name="action">実行本体（LoreExecutor の Wait() まで）。</param>
    private static LoreCallResult Execute(Func<int> action)
    {
        try
        {
            var returnCode = action();
            // LoreVcs は非ゼロ終了コードで LoreError を投げる契約だが、
            // 万一投げずに非ゼロが返っても失敗として扱えるようにしておく。
            return returnCode == LoreCallResult.RETURN_CODE_SUCCESS
                ? LoreCallResult.Success
                : LoreCallResult.Failure(returnCode, Array.Empty<string>());
        }
        catch (LoreError error)
        {
            return LoreCallResult.Failure(error.ReturnCode, ToMessageList(error));
        }
        catch (Exception ex)
        {
            // ネイティブ DLL が見つからない（DllNotFoundException）など、
            // LoreError ではない失敗もここで畳む。例外を境界の外へ出さない。
            return LoreCallResult.Failure(
                LoreCallResult.RETURN_CODE_INTERNAL_FAILURE, new[] { ex.Message });
        }
    }

    /// <summary>中断済みを表す結果を作る。</summary>
    private static LoreCallResult CanceledResult()
        => LoreCallResult.Canceled(MESSAGE_CANCELED_BEFORE_START);

    /// <summary>LoreError のメッセージを取り出す（null 安全）。</summary>
    /// <param name="error">Lore の例外。</param>
    private static IReadOnlyList<string> ToMessageList(LoreError error)
    {
        if (error.Messages is null) return new[] { error.Message ?? string.Empty };

        var list = new List<string>();
        foreach (var message in error.Messages)
        {
            if (!string.IsNullOrWhiteSpace(message)) list.Add(message);
        }
        // Messages が空なら例外自身のメッセージを使う（診断情報を失わない）。
        if (list.Count == 0 && !string.IsNullOrWhiteSpace(error.Message)) list.Add(error.Message);
        return list;
    }

    /// <summary>パスの一覧を Lore が要求する配列へ写す。</summary>
    /// <param name="paths">リポジトリ相対パス。</param>
    private static string[] ToArray(IReadOnlyList<string>? paths)
    {
        if (paths is null || paths.Count == 0) return Array.Empty<string>();

        var array = new string[paths.Count];
        for (var i = 0; i < paths.Count; i++) array[i] = paths[i] ?? string.Empty;
        return array;
    }

    /// <summary>
    /// ハッシュのバイト列を 16 進文字列にする。
    /// LoreHash に文字列化の手段が無いため自前で行う。
    /// </summary>
    /// <param name="bytes">ハッシュのバイト列。</param>
    private static string ToHex(byte[]? bytes)
    {
        if (bytes is null || bytes.Length == 0) return string.Empty;

        var builder = new StringBuilder(bytes.Length * 2);
        foreach (var value in bytes)
        {
            builder.Append(value.ToString("x2", CultureInfo.InvariantCulture));
        }
        return builder.ToString();
    }

    /// <summary>
    /// Lore の符号なし秒を符号つきへ写す。
    /// long の範囲を超える値（明らかに不正）は 0（不明）にする。
    /// </summary>
    /// <param name="seconds">Lore が返した符号なし秒。</param>
    private static long ToSignedSeconds(ulong seconds)
        => seconds <= long.MaxValue ? (long)seconds : 0L;

    /// <summary>
    /// `.lore/config.toml` から単純な `key = "value"` 行を読む。
    ///
    /// <para>
    /// 必要なのは最上位の remote_url と identity の 2 つだけで、
    /// そのために TOML パーサを足すのは割に合わない。セクション
    /// （<c>[store]</c> など）に入ったら読むのをやめる、という最小限の読み方にする。
    /// 読めなければ空を返す（バージョン管理そのものは動く）。
    /// </para>
    /// </summary>
    /// <param name="workingCopyRoot">作業コピーのルート。</param>
    private static Dictionary<string, string> ReadWorkingCopyConfig(string workingCopyRoot)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var configPath = Path.Combine(
            workingCopyRoot,
            VersionControlSettings.LORE_METADATA_DIR_NAME,
            VersionControlSettings.LORE_CONFIG_FILE_NAME);

        try
        {
            if (!File.Exists(configPath)) return result;

            foreach (var rawLine in File.ReadLines(configPath))
            {
                var line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith('#')) continue;

                // セクションに入ったら最上位のキーは終わり。
                if (line.StartsWith('[')) break;

                var separator = line.IndexOf('=');
                if (separator <= 0) continue;

                var key   = line[..separator].Trim();
                var value = line[(separator + 1)..].Trim().Trim('"');
                if (key.Length > 0) result[key] = value;
            }
        }
        catch (Exception)
        {
            // 読めなくても致命的ではない（表示が空になるだけ）。
        }

        return result;
    }

    /// <summary>
    /// 解放するものは無い。
    ///
    /// <para>
    /// <c>Lore.Shutdown()</c> は **プロセス全体で 1 回だけ** 呼ぶもので、
    /// 作業コピー単位の破棄で呼んではいけない（他のリポジトリを開いていたら壊れる）。
    /// アプリ終了時の 1 回は <see cref="LoreShutdownGuard"/> が担当する。
    /// </para>
    /// </summary>
    public void Dispose() { }
}
