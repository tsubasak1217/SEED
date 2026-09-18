// ============================================================
//  LockGatekeeper.cs — 書き込み・送信・自動ロックの唯一のゲート
//
//  【役割】
//  ・保存の直前に「他の人が編集中でないか」を確かめ、通す／注意する／止める
//  ・送信の直前に、変更ファイルへ他の人のロックが無いか確かめる
//  ・シーン・アクターを開いているあいだ、ロックを自動で取って保持する
//
//  【なぜ 1 か所へ集めるのか】
//  書き込み口はシーン・アクター・.anim・.inputmap・.sprite_mesh・地形・
//  プロジェクト設定・スクリプト・AI ツールと 9 経路ある。各所で
//  「ロックを引いて所有者を見て…」と書くと、条件が少しずつ違う 9 個の判定が
//  でき上がり、どれか 1 つが必ず間違う。判定は <see cref="LockGatePolicy"/>、
//  実行はここ、呼び出し側は 1 行（<see cref="EnsureWritable"/>）に固定する。
//
//  【止めないこと（設計の芯）】
//  サーバに繋がらない・ログインしていない・所有者が分からない — つまり
//  「他の人のものだと確かめられない」ときは必ず通す。確かめられないことを
//  根拠に保存を止めると、サーバが落ちている間じゅう誰も作業できない。
//  判定表と理由は <see cref="LockGatePolicy"/> のヘッダーにある。
//
//  【UI を固めない】
//  保存経路は同期（<c>void</c>）なので、ここも同期の入口を持つ。ただし
//  サーバ往復には <see cref="LockGateSettings.GateTimeout"/>（5 秒）の
//  短い期限を掛け、超えたら「サーバに繋がらない」へ倒して通す。
//  ロック操作の既定の期限（2 分）をそのまま使うと、サーバ無応答のとき
//  Ctrl+S でエディタが 2 分固まる。
//
//  【スレッド】
//  ・判定（<c>...Async</c>）はどのスレッドから呼んでもよい。
//  ・提示（<see cref="Present(LockGateVerdict)"/>）は **呼び出し元のスレッド**で行う。
//    同期版はこれを利用して、UI スレッドからの呼び出しでモーダルが
//    UI スレッド上で出るようにしている。
//
//  【依存】
//  WPF にも LoreVcs にも依存しない（単体テストへそのままリンクできる）。
// ============================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using SEEDEditor.VersionControl.Model;

namespace SEEDEditor.VersionControl.Locking;

/// <summary>
/// ロックのゲート（プロセスに 1 つ）。
/// </summary>
public static class LockGatekeeper
{
    /// <summary>自動で取得したロックの台帳。</summary>
    private static readonly AutoLockLedger Ledger = new();

    /// <summary>
    /// 「開いているが、まだロックを取れていない」ファイル。
    ///
    /// <para>
    /// ★これが無いと、起動時に開くシーンのロックがほぼ必ず取れない。
    /// プロジェクトを開く → シーンを読む → 自動ログインが終わる、の順で進むため、
    /// シーンを開いた瞬間はまだ匿名で、匿名では（所有者不明のロックを作らないよう）
    /// 取りに行かないからである。ログインできた時点で
    /// <see cref="RetryPendingAutoLocks"/> を呼んで取り直す。
    /// </para>
    /// </summary>
    private static readonly AutoLockLedger Pending = new();

    /// <summary>直近の照会結果（短い寿命で使い回す）。</summary>
    private static readonly Dictionary<string, CachedStatus> StatusCache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>同じ注意を出した時刻（理由 + パスごと）。</summary>
    private static readonly Dictionary<string, DateTime> WarnedAt =
        new(StringComparer.Ordinal);

    /// <summary>キャッシュと注意の記録を触るときのロック。</summary>
    private static readonly object Gate = new();

    /// <summary>設定（<see cref="Configure"/> で読み込む。未設定なら既定値）。</summary>
    private static LockGateSettings _settings = LockGateSettings.Default;

    /// <summary>診断ログの出力先（無ければ捨てる）。</summary>
    public static Action<string>? Log { get; set; }

    /// <summary>
    /// 利用者への提示（アプリ起動時に WPF 実装を差し込む）。
    /// 未設定ならログだけが残る（ヘッドレス・単体テスト）。
    /// </summary>
    public static ILockGateNotifier? Notifier { get; set; }

    /// <summary>現在の設定。</summary>
    public static LockGateSettings Settings => _settings;

    /// <summary>いま自動で保持しているロックのリポジトリ相対パス（パネルの表示用）。</summary>
    public static IReadOnlyList<string> AutoHeldPaths => Ledger.Snapshot();

    /// <summary>
    /// 設定を読み込む。アプリ起動時に 1 度だけ呼ぶ。
    /// </summary>
    /// <param name="settingsDir">エディタの設定フォルダ（`editor/settings`）。</param>
    public static void Configure(string? settingsDir)
    {
        _settings = LockGateSettings.Load(settingsDir);
        Log?.Invoke(
            $"[ロック] 方針={_settings.Policy} 自動ロック={_settings.AutoLockOpenedDocuments}");
    }

    /// <summary>
    /// 設定を直接差し替える（**テスト専用**）。
    /// </summary>
    /// <param name="settings">差し替える設定（null なら既定へ戻す）。</param>
    public static void UseSettingsForVerification(LockGateSettings? settings)
        => _settings = settings ?? LockGateSettings.Default;

    /// <summary>
    /// 覚えていること（照会のキャッシュ・注意の記録・自動ロックの台帳）を
    /// すべて捨てる（**テスト専用**）。
    ///
    /// <para>
    /// 照会には数秒の寿命があるため、結合テストで「A が解放した直後に
    /// B が保存できる」を確かめようとすると、寿命が切れるまで古い判定を
    /// 見てしまう。実際の利用では数秒待てば済むが、テストを数秒眠らせるのは
    /// 無駄なので、ここで明示的に捨てられるようにしてある。
    /// </para>
    /// <para>
    /// **台帳も空にする**ので、本番コードから呼ぶと自動ロックが
    /// 解放されないまま残る。呼ばないこと。
    /// </para>
    /// </summary>
    public static void ResetForVerification()
    {
        Ledger.TakeAll();
        Pending.TakeAll();
        ClearCaches();
    }

    // ============================================================
    //  保存ゲート
    // ============================================================

    /// <summary>
    /// このファイルへ書き込んでよいか確かめ、必要なら利用者へ知らせる（同期）。
    ///
    /// <para>
    /// 保存経路から呼ぶ唯一の入口。<c>if (!LockGatekeeper.EnsureWritable(path)) return;</c>
    /// の 1 行で使う。**UI スレッドから呼ぶこと**（止めたときのモーダルを
    /// 呼び出し元スレッドで出すため）。
    /// </para>
    /// </summary>
    /// <param name="absolutePath">書き込む先の絶対パス。</param>
    /// <returns>書き込んでよければ真。</returns>
    public static bool EnsureWritable(string? absolutePath)
    {
        LockGateVerdict verdict;
        try
        {
            // ワーカースレッドで完結する Task を待つ（継続は UI スレッドを要求しないので
            // ここで待っても行き詰まらない）。期限は Task 側に掛けてある。
            verdict = DecideForWriteAsync(absolutePath, CancellationToken.None)
                          .ConfigureAwait(false).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            // ゲートの故障で保存できなくなる方が有害。通して、痕跡だけ残す。
            Log?.Invoke($"[ロック] ゲートの判定に失敗したため保存を通しました: {ex.Message}");
            return true;
        }

        Present(verdict);
        return verdict.CanProceed;
    }

    /// <summary>
    /// 複数のファイルへまとめて書き込んでよいか確かめる（同期）。
    /// 1 件でも止められたら偽を返す（部分的に書くと不整合になる用途向け）。
    /// </summary>
    /// <param name="absolutePaths">書き込む先の絶対パス。</param>
    /// <returns>すべて書き込んでよければ真。</returns>
    public static bool EnsureAllWritable(IEnumerable<string?>? absolutePaths)
    {
        if (absolutePaths is null) return true;

        var allowed = true;
        foreach (var path in absolutePaths)
        {
            // 途中で抜けない。止められた原因を全部見せるため、最後まで判定する。
            if (!EnsureWritable(path)) allowed = false;
        }
        return allowed;
    }

    /// <summary>
    /// 書き込みの可否を判定する（提示はしない）。
    /// </summary>
    /// <param name="absolutePath">書き込む先の絶対パス。</param>
    /// <param name="cancellationToken">中断用。</param>
    /// <returns>判定結果。</returns>
    public static async Task<LockGateVerdict> DecideForWriteAsync(
        string? absolutePath, CancellationToken cancellationToken = default)
    {
        var provider = VersionControlService.Provider;
        var relative = ToRelative(absolutePath);

        // バージョン管理下に無い、または作業コピーの外（テンプレート・一時ファイル）。
        // どちらもロックという概念が無いので素通しする。
        if (!provider.IsAvailable || !provider.Locks.IsAvailable || relative is null)
        {
            return new LockGateVerdict(
                LockGateAction.Allow, LockGateReason.VersionControlUnavailable,
                message: null, relative);
        }

        var isSignedIn = IsSignedIn();

        // 1) いまの保持者を調べる（短い寿命のキャッシュあり）。
        var (holder, owner, reachable) =
            await GetStatusAsync(relative, cancellationToken).ConfigureAwait(false);

        var verdict = LockGatePolicy.DecideForWrite(new LockGateSituation(
            IsVersionControlAvailable: true,
            IsServerReachable:         reachable,
            IsSignedIn:                isSignedIn,
            Holder:                    holder,
            OwnerName:                 owner,
            Policy:                    _settings.Policy,
            RelativePath:              relative));

        // 2) 誰も持っていなければ、その場で取ってから決め直す。
        if (verdict.Action != LockGateAction.AcquireThenDecide) return verdict;

        // ★まだ存在しないファイル（新規保存・名前を付けて保存）は取りに行かない。
        //   誰も持っていないと分かっている以上、守るべき相手の編集が無い。
        //   一方で「まだ無いパスのロック」を Lore が拒む可能性があり、
        //   拒まれると新規保存そのものが止まってしまう。実在するものだけ取る。
        if (!FileExists(absolutePath))
        {
            return new LockGateVerdict(
                LockGateAction.Allow, LockGateReason.NoLock, message: null, relative);
        }

        var (acquired, acquireReachable) =
            await AcquireAsync(relative, cancellationToken).ConfigureAwait(false);

        // 照会と取得のあいだにサーバが落ちた場合は「取れなかった」ではなく
        // 「確かめられなかった」。判定表 2 行目と同じく止めない
        //（ここで止めると、オフラインでは保存できるのに回線が切れかけていると
        //  保存できない、という説明のつかない挙動になる）。
        if (!acquireReachable)
        {
            return new LockGateVerdict(
                LockGateAction.AllowWithWarning,
                LockGateReason.ServerUnreachable,
                string.Format(
                    CultureInfo.InvariantCulture,
                    VersionControlMessages.LOCK_GATE_WARN_UNREACHABLE_FORMAT, relative),
                relative);
        }

        var after = LockGatePolicy.DecideAfterAcquire(
            acquired?.Outcome ?? LockAcquireOutcome.Failed,
            acquired?.Lock.Owner,
            _settings.Policy,
            relative);

        // 保存のために取ったロックも「自動で取ったもの」として台帳へ入れる。
        // プロジェクトを閉じるときにまとめて外れる（掛けっぱなしにしない）。
        if (acquired is not null && acquired.Outcome == LockAcquireOutcome.Acquired)
        {
            Ledger.Add(relative);
        }

        return after;
    }

    // ============================================================
    //  送信ゲート
    // ============================================================

    /// <summary>
    /// 送信してよいか確かめる（提示はしない）。
    ///
    /// <para>
    /// サーバの push フックは変更ファイルの一覧を受け取れないため、
    /// 他の人のロックを守れるのはここだけ。
    /// </para>
    /// </summary>
    /// <param name="relativePaths">送信しようとしている変更ファイル（リポジトリ相対）。</param>
    /// <param name="cancellationToken">中断用。</param>
    /// <returns>判定結果。</returns>
    public static async Task<LockGateSubmitVerdict> DecideForSubmitAsync(
        IReadOnlyList<string>? relativePaths, CancellationToken cancellationToken = default)
    {
        var provider = VersionControlService.Provider;

        if (!provider.IsAvailable || !provider.Locks.IsAvailable)
        {
            return new LockGateSubmitVerdict(
                LockGateAction.Allow, LockGateReason.VersionControlUnavailable, message: null);
        }

        // 送るものが無ければ確かめるものも無い（空送信は別の理由で弾かれる）。
        if (relativePaths is null || relativePaths.Count == 0)
        {
            return new LockGateSubmitVerdict(
                LockGateAction.Allow, LockGateReason.NoLock, message: null);
        }

        // 送信前は必ずサーバへ問い合わせる（キャッシュは使わない）。
        // 保存から送信までのあいだに誰かがロックを掛けていることがあるため。
        using var timeout = CreateTimeout(cancellationToken);
        var result = await provider.Locks
                                   .GetStatusAsync(relativePaths, timeout.Token)
                                   .ConfigureAwait(false);

        var reachable = result.IsSuccess;
        if (!reachable)
        {
            Log?.Invoke($"[ロック] 送信前の照会に失敗しました: {result.Outcome} {result.Message}");
        }

        return LockGatePolicy.DecideForSubmit(
            result.Value,
            isVersionControlAvailable: true,
            isServerReachable:         reachable,
            isSignedIn:                IsSignedIn(),
            policy:                    _settings.Policy);
    }

    // ============================================================
    //  一括書き込みゲート
    // ============================================================

    /// <summary>
    /// 多数のファイルをまとめて書き換えてよいか確かめる（提示はしない）。
    ///
    /// <para>
    /// プロジェクトの形式アップグレードのように、**1 回の操作で数十〜数百ファイルを
    /// 書き換える**機能のための入口。<see cref="EnsureAllWritable"/> は 1 件ずつ
    /// 照会してロックを取りに行くため、この規模では
    /// (a) サーバ往復がファイル数ぶん走って待たされ、
    /// (b) 実行した人が大量のロックを握ったままになる。
    /// ここは **1 回の照会で全部見て、取りには行かない**。
    /// </para>
    /// </summary>
    /// <param name="absolutePaths">書き換える予定のファイルの絶対パス。</param>
    /// <param name="cancellationToken">中断用。</param>
    /// <returns>判定結果。</returns>
    public static async Task<LockGateSubmitVerdict> DecideForBulkWriteAsync(
        IEnumerable<string?>? absolutePaths, CancellationToken cancellationToken = default)
    {
        var provider = VersionControlService.Provider;

        if (!provider.IsAvailable || !provider.Locks.IsAvailable)
        {
            return new LockGateSubmitVerdict(
                LockGateAction.Allow, LockGateReason.VersionControlUnavailable, message: null);
        }

        // 作業コピーの外にあるパスはロックという概念が無いので落とす。
        // 同じパスが 2 回来ても意味が無いので重ねない。
        var relatives = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (absolutePaths is not null)
        {
            foreach (var path in absolutePaths)
            {
                var relative = ToRelative(path);
                if (relative is null) continue;
                if (seen.Add(relative)) relatives.Add(relative);
            }
        }

        if (relatives.Count == 0)
        {
            return new LockGateSubmitVerdict(
                LockGateAction.Allow, LockGateReason.NoLock, message: null);
        }

        // 実行前は必ずサーバへ問い合わせる（キャッシュは使わない）。
        // 下調べから実行までのあいだに誰かがロックを掛けていることがあるため。
        using var timeout = CreateTimeout(cancellationToken);
        var result = await provider.Locks
                                   .GetStatusAsync(relatives, timeout.Token)
                                   .ConfigureAwait(false);

        var reachable = result.IsSuccess;
        if (!reachable)
        {
            Log?.Invoke($"[ロック] 一括書き込み前の照会に失敗しました: {result.Outcome} {result.Message}");
        }

        return LockGatePolicy.DecideForBulkWrite(
            result.Value,
            isVersionControlAvailable: true,
            isServerReachable:         reachable,
            isSignedIn:                IsSignedIn(),
            policy:                    _settings.Policy);
    }

    /// <summary>
    /// 一括書き込みの判定結果を利用者へ見せる（**呼び出し元のスレッドで行う**）。
    /// </summary>
    /// <param name="verdict">判定結果。</param>
    public static void PresentBulkWrite(LockGateSubmitVerdict? verdict)
    {
        if (verdict is null || !verdict.HasMessage) return;

        if (verdict.Action == LockGateAction.Block)
        {
            Log?.Invoke($"[ロック] 一括書き込みを止めました: {verdict}");
            Notifier?.NotifyBlocked(
                VersionControlMessages.BULK_WRITE_BLOCKED_BY_LOCKS_TITLE, verdict.Message);
            return;
        }

        Log?.Invoke($"[ロック] {verdict}");
        Notifier?.NotifyWarning(verdict.Message);
    }

    // ============================================================
    //  自動ロック（開いているあいだ保持する）
    // ============================================================

    /// <summary>
    /// 開いたファイル（シーン・アクター）のロックを自動で取得する。
    ///
    /// <para>
    /// **待たない**。シーンの読み込みはサーバ往復を待つべき処理ではないので、
    /// 取得は裏で進め、結果はログにだけ出す。取れなくても開くことは妨げない
    /// （保存しようとした時点で保存ゲートが正しく止める）。
    /// </para>
    /// </summary>
    /// <param name="absolutePath">開いたファイルの絶対パス。</param>
    public static void TrackOpenedDocument(string? absolutePath)
    {
        if (!_settings.AutoLockOpenedDocuments) return;

        var relative = ToRelative(absolutePath);
        if (relative is null) return;

        // ログインしていないときは取らない。匿名で取ると所有者が <unknown> の
        // ロックができ、**誰にも解除できない**（解除できるのは自分のロックだけ）。
        // 代わりに「開いている」ことだけ覚えておき、ログインできたら取り直す。
        if (!IsSignedIn())
        {
            Pending.Add(relative);
            return;
        }

        _ = TrackOpenedDocumentAsync(relative);
    }

    /// <summary>
    /// ログインできていなくて取れなかったぶんのロックを取り直す。
    ///
    /// <para>
    /// ログイン状態が変わったときに呼ぶ（配線は <c>App.xaml.cs</c>）。
    /// ログインしていなければ何もしない（覚えたまま次の機会を待つ）。
    /// </para>
    /// </summary>
    public static void RetryPendingAutoLocks()
    {
        if (!_settings.AutoLockOpenedDocuments) return;
        if (!IsSignedIn()) return;

        var paths = Pending.TakeAll();
        if (paths.Count == 0) return;

        Log?.Invoke($"[ロック] ログインできたので「編集中」を取り直します（{paths.Count} 件）。");
        foreach (var path in paths) _ = TrackOpenedDocumentAsync(path);
    }

    /// <summary>
    /// 自動ロックの取得本体。例外を外へ出さない（fire-and-forget のため）。
    /// </summary>
    /// <param name="relativePath">リポジトリ相対パス。</param>
    private static async Task TrackOpenedDocumentAsync(string relativePath)
    {
        try
        {
            var (acquired, reachable) = await AcquireAsync(relativePath, CancellationToken.None)
                                              .ConfigureAwait(false);

            // サーバへ届かなかっただけなら、開いている事実は覚えたままにする。
            // 繋がったとき（＝次のログイン通知）に取り直せる。
            if (!reachable)
            {
                Pending.Add(relativePath);
                Log?.Invoke(string.Format(
                    CultureInfo.InvariantCulture,
                    VersionControlMessages.LOCK_GATE_AUTO_ACQUIRE_FAILED_FORMAT,
                    relativePath, VersionControlMessages.REQUIRES_CONNECTION));
                return;
            }

            if (acquired is null)
            {
                Log?.Invoke(string.Format(
                    CultureInfo.InvariantCulture,
                    VersionControlMessages.LOCK_GATE_AUTO_ACQUIRE_FAILED_FORMAT,
                    relativePath, VersionControlMessages.LOCK_ACQUIRE_FAILED));
                return;
            }

            switch (acquired.Outcome)
            {
                // このエディタが新しく取った → 閉じるときに外す責任を持つ。
                case LockAcquireOutcome.Acquired:
                    Ledger.Add(relativePath);
                    Log?.Invoke(string.Format(
                        CultureInfo.InvariantCulture,
                        VersionControlMessages.LOCK_GATE_AUTO_ACQUIRED_FORMAT, relativePath));
                    break;

                // もともと自分が持っていた（手で掛けた／前回の残り）→ 台帳へ入れない。
                // 入れると、閉じたときに利用者が意図して掛けたロックまで外れる。
                case LockAcquireOutcome.AlreadyMine:
                    break;

                default:
                    Log?.Invoke(string.Format(
                        CultureInfo.InvariantCulture,
                        VersionControlMessages.LOCK_GATE_AUTO_ACQUIRE_FAILED_FORMAT,
                        relativePath, acquired.Outcome));
                    break;
            }
        }
        catch (Exception ex)
        {
            Log?.Invoke($"[ロック] 自動取得で例外が出ました（{relativePath}）: {ex.Message}");
        }
    }

    /// <summary>
    /// 閉じた／切り替えたファイルの自動ロックを解放する。
    ///
    /// <para>
    /// 台帳に無いパス（手で掛けたロック・そもそも取れていないもの）は
    /// 何もしない。**待たない**（解放はエディタの操作を止める理由にならない）。
    /// </para>
    /// </summary>
    /// <param name="absolutePath">閉じたファイルの絶対パス。</param>
    public static void ReleaseTrackedDocument(string? absolutePath)
    {
        var relative = ToRelative(absolutePath);
        if (relative is null) return;

        // 取れないまま閉じた（ログイン前に開いて閉じた）ぶんも忘れる。
        // 残すと、あとでログインした瞬間に「もう開いていないファイル」を掴む。
        Pending.Remove(relative);

        if (!Ledger.Remove(relative)) return;

        _ = ReleaseAsync(new[] { relative });
    }

    /// <summary>
    /// 自動で取ったロックをすべて解放する（プロジェクトを閉じる・エディタ終了）。
    ///
    /// <para>
    /// 終了経路から呼ばれるので **待つ**（ただし
    /// <see cref="LockGateSettings.ReleaseWait"/> まで）。待たずに終了すると
    /// プロセスが消えて解放が届かず、他の人から見て掛かりっぱなしになる。
    /// 期限を過ぎたら諦める。解放できなくても失われるデータは無い。
    /// </para>
    /// </summary>
    public static void ReleaseAllTracked()
    {
        var paths = Ledger.TakeAll();
        Pending.TakeAll();
        ClearCaches();
        if (paths.Count == 0) return;

        try
        {
            var task = ReleaseAsync(paths);
            if (!task.Wait(LockGateSettings.ReleaseWait))
            {
                Log?.Invoke($"[ロック] 自動ロックの解放が {paths.Count} 件残ったまま終了します。");
            }
        }
        catch (Exception ex)
        {
            // 解放の失敗で終了を止めない。
            Log?.Invoke($"[ロック] 自動ロックの解放に失敗しました: {ex.Message}");
        }
    }

    /// <summary>
    /// 指定パスを台帳から外す（自動 → 手動への格上げ）。
    ///
    /// <para>
    /// 利用者がパネルから手でロックを掛けたときに呼ぶ。以後このロックは
    /// 自動解放の対象外になり、シーンを閉じても外れない。
    /// </para>
    /// </summary>
    /// <param name="relativePaths">リポジトリ相対パス。</param>
    public static void ForgetTracked(IEnumerable<string>? relativePaths)
    {
        if (relativePaths is null) return;
        foreach (var path in relativePaths) Ledger.Remove(path);
    }

    /// <summary>
    /// このパスを自動ロックとして保持しているか（パネルの「編集中」表示に使う）。
    /// </summary>
    /// <param name="relativePath">リポジトリ相対パス。</param>
    public static bool IsAutoHeld(string? relativePath) => Ledger.Contains(relativePath);

    // ============================================================
    //  提示
    // ============================================================

    /// <summary>
    /// 判定結果を利用者へ見せる（**呼び出し元のスレッドで行う**）。
    /// </summary>
    /// <param name="verdict">判定結果。</param>
    public static void Present(LockGateVerdict? verdict)
    {
        if (verdict is null) return;

        switch (verdict.Action)
        {
            case LockGateAction.Block:
                Log?.Invoke($"[ロック] 保存を止めました: {verdict}");
                Notifier?.NotifyBlocked(
                    VersionControlMessages.LOCK_GATE_BLOCKED_TITLE, verdict.Message);
                break;

            case LockGateAction.AllowWithWarning:
                Log?.Invoke($"[ロック] {verdict}");
                // 同じ注意を保存のたびに出さない（読まれなくなるため）。
                if (ShouldWarnNow(verdict.Reason, verdict.RelativePath))
                    Notifier?.NotifyWarning(verdict.Message);
                break;

            default:
                // 通した場合は何も出さない（保存のたびに何か出るのは邪魔なだけ）。
                break;
        }
    }

    /// <summary>
    /// 送信の判定結果を利用者へ見せる（**呼び出し元のスレッドで行う**）。
    /// </summary>
    /// <param name="verdict">判定結果。</param>
    public static void Present(LockGateSubmitVerdict? verdict)
    {
        if (verdict is null || !verdict.HasMessage) return;

        if (verdict.Action == LockGateAction.Block)
        {
            Log?.Invoke($"[ロック] 送信を止めました: {verdict}");
            Notifier?.NotifyBlocked(
                VersionControlMessages.SUBMIT_BLOCKED_BY_LOCKS_TITLE, verdict.Message);
            return;
        }

        Log?.Invoke($"[ロック] {verdict}");
        Notifier?.NotifyWarning(verdict.Message);
    }

    // ============================================================
    //  内部
    // ============================================================

    /// <summary>
    /// ロック状態を取得する（短い寿命のキャッシュあり）。
    /// </summary>
    /// <param name="relativePath">リポジトリ相対パス。</param>
    /// <param name="cancellationToken">中断用。</param>
    /// <returns>保持者・所有者名・サーバへ届いたか。</returns>
    private static async Task<(LockHolder Holder, string Owner, bool Reachable)> GetStatusAsync(
        string relativePath, CancellationToken cancellationToken)
    {
        // 連続保存（シーン本体 → シーン設定の自動保存など）で同じパスを
        // 何度も問い合わせない。寿命は数秒なので、他の人の操作を取り逃す幅も数秒。
        lock (Gate)
        {
            if (StatusCache.TryGetValue(relativePath, out var cached)
                && DateTime.UtcNow - cached.AtUtc < LockGateSettings.StatusCacheTtl)
            {
                return (cached.Holder, cached.Owner, cached.Reachable);
            }
        }

        using var timeout = CreateTimeout(cancellationToken);
        var result = await VersionControlService.Provider.Locks
                          .GetStatusAsync(new[] { relativePath }, timeout.Token)
                          .ConfigureAwait(false);

        var holder    = LockHolder.None;
        var owner     = string.Empty;
        var reachable = result.IsSuccess;

        if (reachable && result.Value is { Count: > 0 })
        {
            // 照会は「ロックされていないパスも 1 件返す」契約（ILockService）。
            holder = result.Value[0].Holder;
            owner  = result.Value[0].Owner;
        }
        else if (!reachable)
        {
            Log?.Invoke($"[ロック] 照会に失敗しました（{relativePath}）: "
                        + $"{result.Outcome} {result.Message}");
        }

        lock (Gate)
        {
            StatusCache[relativePath] = new CachedStatus(holder, owner, reachable, DateTime.UtcNow);
        }

        return (holder, owner, reachable);
    }

    /// <summary>
    /// ロックを 1 件取得する。取得したらそのパスのキャッシュを捨てる。
    /// </summary>
    /// <param name="relativePath">リポジトリ相対パス。</param>
    /// <param name="cancellationToken">中断用。</param>
    /// <returns>
    /// 取得結果（呼び出しごと失敗したら null）と、サーバへ届いたか。
    /// 「取れなかった」と「確かめられなかった」は扱いが違うので分けて返す。
    /// </returns>
    private static async Task<(LockAcquireResult? Result, bool Reachable)> AcquireAsync(
        string relativePath, CancellationToken cancellationToken)
    {
        using var timeout = CreateTimeout(cancellationToken);
        var result = await VersionControlService.Provider.Locks
                          .AcquireAsync(new[] { relativePath }, timeout.Token)
                          .ConfigureAwait(false);

        // 状態が変わったのでキャッシュを捨てる（次の保存が古い判定を使わないように）。
        lock (Gate) { StatusCache.Remove(relativePath); }

        // 接続できない・期限切れは「サーバへ届かなかった」。
        var reachable = result.Outcome is not (VersionControlOutcome.RequiresConnection
                                            or VersionControlOutcome.Canceled);
        if (!reachable)
        {
            Log?.Invoke($"[ロック] 取得がサーバへ届きませんでした（{relativePath}）: "
                        + $"{result.Outcome} {result.Message}");
        }

        if (!result.IsSuccess || result.Value is not { Count: > 0 }) return (null, reachable);
        return (result.Value[0], reachable);
    }

    /// <summary>
    /// ファイルが実在するか（取得を試みてよいかの判断に使う）。
    /// 判定できないときは「ある」として扱う（安全側 = ロックを取りに行く側）。
    /// </summary>
    /// <param name="absolutePath">絶対パス。</param>
    private static bool FileExists(string? absolutePath)
    {
        if (string.IsNullOrWhiteSpace(absolutePath)) return false;
        try { return System.IO.File.Exists(absolutePath); }
        catch (Exception) { return true; }
    }

    /// <summary>
    /// ロックをまとめて解放する。失敗してもログだけ残す。
    /// </summary>
    /// <param name="relativePaths">リポジトリ相対パス。</param>
    private static async Task ReleaseAsync(IReadOnlyList<string> relativePaths)
    {
        try
        {
            using var timeout = CreateTimeout(CancellationToken.None);
            var result = await VersionControlService.Provider.Locks
                              .ReleaseAsync(relativePaths, timeout.Token)
                              .ConfigureAwait(false);

            lock (Gate)
            {
                foreach (var path in relativePaths) StatusCache.Remove(path);
            }

            if (result.IsSuccess)
            {
                foreach (var path in relativePaths)
                {
                    Log?.Invoke(string.Format(
                        CultureInfo.InvariantCulture,
                        VersionControlMessages.LOCK_GATE_AUTO_RELEASED_FORMAT, path));
                }
                return;
            }

            Log?.Invoke($"[ロック] 解放に失敗しました（{relativePaths.Count} 件）: "
                        + $"{result.Outcome} {result.Message}");
        }
        catch (Exception ex)
        {
            Log?.Invoke($"[ロック] 解放で例外が出ました: {ex.Message}");
        }
    }

    /// <summary>
    /// ゲート専用の短い期限つきトークンを作る。
    /// </summary>
    /// <param name="cancellationToken">呼び出し側の中断。</param>
    private static CancellationTokenSource CreateTimeout(CancellationToken cancellationToken)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        source.CancelAfter(LockGateSettings.GateTimeout);
        return source;
    }

    /// <summary>
    /// SEED アカウントでログイン中か。
    ///
    /// <para>
    /// <see cref="VersionControlService.CredentialProvider"/> 経由で見るので、
    /// この層は <c>SEEDEditor.Accounts</c> を参照しない（依存を一方通行に保つ）。
    /// </para>
    /// </summary>
    private static bool IsSignedIn()
    {
        try
        {
            return VersionControlService.CredentialProvider?.Invoke().HasToken ?? false;
        }
        catch (Exception)
        {
            // 資格情報の取得で落ちたら「匿名」として扱う（止めない側へ倒す）。
            return false;
        }
    }

    /// <summary>
    /// 絶対パスをリポジトリ相対パスへ直す。作業コピーの外なら null。
    /// </summary>
    /// <param name="absolutePath">絶対パス。</param>
    private static string? ToRelative(string? absolutePath)
        => VersionControlPaths.ToRepositoryRelative(
               VersionControlService.Provider.WorkingCopyRoot, absolutePath);

    /// <summary>
    /// いま注意を出してよいか（同じ理由・同じパスの連発を抑える）。
    /// </summary>
    /// <param name="reason">理由。</param>
    /// <param name="relativePath">対象パス。</param>
    private static bool ShouldWarnNow(LockGateReason reason, string relativePath)
    {
        var key = $"{reason}|{relativePath}";
        var now = DateTime.UtcNow;

        lock (Gate)
        {
            if (WarnedAt.TryGetValue(key, out var last)
                && now - last < LockGateSettings.WarningRepeatInterval)
            {
                return false;
            }

            WarnedAt[key] = now;
            return true;
        }
    }

    /// <summary>
    /// キャッシュと注意の記録を捨てる（プロジェクトを閉じるとき）。
    /// </summary>
    private static void ClearCaches()
    {
        lock (Gate)
        {
            StatusCache.Clear();
            WarnedAt.Clear();
        }
    }

    /// <summary>キャッシュした 1 件の照会結果。</summary>
    /// <param name="Holder">保持者。</param>
    /// <param name="Owner">所有者名。</param>
    /// <param name="Reachable">サーバへ届いたか。</param>
    /// <param name="AtUtc">取得時刻（UTC）。</param>
    private readonly record struct CachedStatus(
        LockHolder Holder, string Owner, bool Reachable, DateTime AtUtc);
}
