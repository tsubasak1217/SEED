// ============================================================
//  VersionControlService.cs — プロバイダのライフサイクルと状態の配布
//
//  【役割】
//  ・プロジェクトを開いたときにプロバイダを作り、閉じたときに破棄する
//  ・状態（WorkingCopyStatus）を保持し、変わったらイベントで知らせる
//  ・保存が連続したときに status を毎回走らせないようデバウンスする
//
//  【なぜ static なのか】
//  プロジェクトは 1 プロセスに 1 つ（ProjectContext と同じ前提）。
//  パネル・保存経路・プロジェクトパネルが DI 経路を持たないため、
//  ProjectContext と同じ「プロセスに 1 つの静的な入口」に揃える。
//
//  【常に非 null であること】
//  <see cref="Provider"/> はプロジェクト未確定でも NullProvider を返す。
//  呼び出し側に null チェックを強いると、1 か所忘れた瞬間に落ちるため。
//
//  【スレッド】
//  ・Provider の各操作は内部の専用ワーカーで直列実行される（UI を塞がない）。
//  ・<see cref="StatusChanged"/> は **ワーカースレッドから発火し得る**。
//    UI を触る購読側は自分で Dispatcher へ移すこと（ここで WPF に依存しないため）。
//
//  【今回の範囲】
//  パネルがまだ無いので、配線は「プロジェクトを開くとサービスが生成され、
//  ログに検出結果が 1 行出る」まで。自動更新の購読（保存経路からの通知）は
//  パネル側の実装と合わせて次段で行う。
//
//  【依存】
//  WPF にも LoreVcs にも依存しない（プロバイダ生成だけ Factory に委ねている）。
// ============================================================

using System;
using System.Threading;
using System.Threading.Tasks;
using SEEDEditor.VersionControl.Abstractions;
using SEEDEditor.VersionControl.Model;
using SEEDEditor.VersionControl.Null;

namespace SEEDEditor.VersionControl;

/// <summary>
/// <see cref="VersionControlService.StatusChanged"/> が運ぶ情報。
/// </summary>
public sealed class VersionControlStatusChangedEventArgs : EventArgs
{
    /// <summary>新しい状態。</summary>
    public WorkingCopyStatus Status { get; }

    /// <summary>その状態を取得した操作の結果（失敗していれば理由が入る）。</summary>
    public VersionControlResult Result { get; }

    /// <summary>状態と結果を指定して生成する。</summary>
    /// <param name="status">新しい状態。</param>
    /// <param name="result">取得結果。</param>
    public VersionControlStatusChangedEventArgs(
        WorkingCopyStatus status, VersionControlResult result)
    {
        Status = status;
        Result = result;
    }
}

/// <summary>
/// バージョン管理のライフサイクル（プロセスに 1 つ）。
/// </summary>
public static class VersionControlService
{
    /// <summary>プロジェクト未確定のときに返すプロバイダ。</summary>
    private static readonly NullVersionControlProvider NoProject = new();

    /// <summary>現在のプロバイダ。</summary>
    private static IVersionControlProvider _provider = NoProject;

    /// <summary>設定（<see cref="Open"/> で渡されたもの）。</summary>
    private static VersionControlSettings _settings = VersionControlSettings.Default;

    /// <summary>デバウンス中の再取得をまとめるためのタイマー。</summary>
    private static Timer? _debounceTimer;

    /// <summary>状態などを触るときのロック（デバウンスと Close が競合するため）。</summary>
    private static readonly object Gate = new();

    /// <summary>診断ログの出力先（<see cref="Open"/> で差し込む）。</summary>
    public static Action<string>? Log { get; set; }

    /// <summary>
    /// ログイン中の SEED アカウント（トークンと名前）を返す関数。
    ///
    /// <para>
    /// アプリ起動時に 1 度だけ差し込む（<c>App.OnStartup</c>）。
    /// ここを経由することで、バージョン管理の層は
    /// <c>SEEDEditor.Accounts</c> を一切参照しなくて済む（依存の向きを一方通行に保つ）。
    /// 未設定なら常に匿名で、従来どおりの動作になる。
    /// </para>
    /// </summary>
    public static Func<Lore.Backend.LoreAccountCredential>? CredentialProvider { get; set; }

    /// <summary>現在のプロバイダ（常に非 null）。</summary>
    public static IVersionControlProvider Provider => Volatile.Read(ref _provider);

    /// <summary>直近に取得できた状態（未取得なら null）。</summary>
    public static WorkingCopyStatus? LastStatus { get; private set; }

    /// <summary>
    /// 状態が更新されたときに発火する。
    /// **ワーカースレッドから呼ばれ得る**ので、UI を触る側は Dispatcher へ移すこと。
    /// </summary>
    public static event EventHandler<VersionControlStatusChangedEventArgs>? StatusChanged;

    /// <summary>
    /// プロジェクトを開いたときに呼ぶ。プロバイダを作り、検出結果をログへ 1 行出す。
    /// </summary>
    /// <param name="projectRootDir">プロジェクトルート（.seedproj のあるフォルダ）。</param>
    /// <param name="settings">設定（省略時は既定値）。</param>
    public static void Open(string? projectRootDir, VersionControlSettings? settings = null)
    {
        // 開き直しに備えて先に閉じる（同じプロセスで 2 回開くことは想定していないが、
        // ここで前のワーカーを残すとスレッドが漏れる）。
        Close();

        _settings = settings ?? VersionControlSettings.Default;
        var created = VersionControlProviderFactory.Create(
            projectRootDir, _settings, Log, CredentialProvider);
        Volatile.Write(ref _provider, created);

        Log?.Invoke(created.IsAvailable
            ? $"バージョン管理: {created.DisplayName}（{created.WorkingCopyRoot}"
              + $" remote={Describe(created.RemoteUrl)} identity={Describe(created.Identity)}）"
            : $"バージョン管理: なし（{projectRootDir}）");
    }

    /// <summary>
    /// プロジェクトを閉じるときに呼ぶ。プロバイダとワーカーを破棄する。
    /// </summary>
    public static void Close()
    {
        Timer? timer;
        IVersionControlProvider previous;

        lock (Gate)
        {
            timer = _debounceTimer;
            _debounceTimer = null;
            previous = Volatile.Read(ref _provider);
            Volatile.Write(ref _provider, NoProject);
            LastStatus = null;
        }

        try { timer?.Dispose(); } catch { /* 後始末の失敗は無視 */ }

        if (!ReferenceEquals(previous, NoProject))
        {
            try { previous.Dispose(); }
            catch (Exception ex) { Log?.Invoke($"[VCS] プロバイダの破棄に失敗しました: {ex.Message}"); }
        }
    }

    /// <summary>
    /// 状態を取り直し、<see cref="StatusChanged"/> を発火する。
    /// </summary>
    /// <param name="mode">取得モード。</param>
    /// <param name="cancellationToken">中断用。</param>
    /// <returns>取得結果。</returns>
    public static async Task<VersionControlResult<WorkingCopyStatus>> RefreshAsync(
        StatusRefreshMode mode = StatusRefreshMode.ScanOffline,
        CancellationToken cancellationToken = default)
    {
        var provider = Provider;
        var result   = await provider.GetStatusAsync(mode, cancellationToken)
                                     .ConfigureAwait(false);

        // 失敗しても「今の状態」は更新する（値は空になるが、
        // パネルが古い状態を正しいものとして表示し続けるより良い）。
        var status = result.Value ?? WorkingCopyStatus.Empty(mode);
        LastStatus = status;

        StatusChanged?.Invoke(null, new VersionControlStatusChangedEventArgs(status, result));
        return result;
    }

    /// <summary>
    /// 状態の取り直しを予約する。短い間に何度呼んでも 1 回にまとめられる。
    ///
    /// <para>
    /// 保存のたびに呼ばれる想定。連続保存で status を毎回走らせないための入口。
    /// </para>
    /// </summary>
    /// <param name="mode">取得モード。</param>
    public static void RequestRefresh(StatusRefreshMode mode = StatusRefreshMode.TrackedOnly)
    {
        // 利用不可なら取り直す意味が無い（ワーカーも無い）。
        if (!Provider.IsAvailable) return;

        lock (Gate)
        {
            if (_debounceTimer is null)
            {
                // 単発タイマー。Change で期限を後ろへずらすことでまとめる。
                _debounceTimer = new Timer(
                    _ => _ = RefreshFromTimerAsync(mode),
                    state: null,
                    dueTime: _settings.StatusDebounce,
                    period: Timeout.InfiniteTimeSpan);
                return;
            }

            // すでに予約済み。期限を今から数えなおす（連続保存をまとめる）。
            try { _debounceTimer.Change(_settings.StatusDebounce, Timeout.InfiniteTimeSpan); }
            catch (ObjectDisposedException) { /* Close と競合。次の要求で作り直される */ }
        }
    }

    /// <summary>
    /// デバウンスタイマーから呼ばれる取り直し。例外を外へ漏らさない
    /// （タイマーのコールバックで例外を投げるとプロセスが落ちる）。
    /// </summary>
    /// <param name="mode">取得モード。</param>
    private static async Task RefreshFromTimerAsync(StatusRefreshMode mode)
    {
        try
        {
            await RefreshAsync(mode).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log?.Invoke($"[VCS] 状態の自動更新に失敗しました: {ex.Message}");
        }
    }

    /// <summary>ログに出す値（空なら「未設定」と書く）。</summary>
    /// <param name="value">表示したい値。</param>
    private static string Describe(string? value)
        => string.IsNullOrWhiteSpace(value) ? "（未設定）" : value;
}
