// ============================================================
//  SceneSnapshotViewSession.cs — シーンの写しをエディタの編集用ランタイムへ閲覧専用で出し、元の編集中のシーンへ戻す段取り
//
//  【役割】（docs/android.md §20.17。WPF に依存しない＝単体テスト AndroidRunUiTests が偽のランタイムで確かめる）
//    表示（BeginAsync）:
//      1. SNAPSHOT_VIEW_BEGIN:<写しのパス>[|file_ok] を送る
//         → 編集用ランタイムが編集中のシーンをメモリへ退避し（地形の実データ・編集タブ・Undo 履歴・ツール）、写しを読み込む
//           （未保存の変更が無いときだけ file_ok を付ける＝メモリへ退避できなければファイルの読み直しで戻してよい）
//      2. 応答 SNAPSHOT_VIEW_READY / SNAPSHOT_VIEW_FAILED を待つ
//      3. 表示できたら、デバッグカメラを写しのメインカメラの位置と向きへ合わせる（既存の CAM_TRANSFORM）
//    戻す（EndAsync）:
//      SNAPSHOT_VIEW_END を送り、SNAPSHOT_VIEW_ENDED を待つ。未保存フラグは「表示の前の値」を返す
//      （メモリから戻したときだけ。ファイルを読み直したときは保存済みの内容なので未保存なし）
//  写しへの編集を元へ反映する機能は無い（閲覧専用。後から足すときもこのクラスの外に作る。docs/backlog.md）。
//
//  【順番とスレッド】
//    表示と戻すは 1 つずつ順に行う（前の段取りが終わるまで待つ）。応答は受信のスレッドから届くので、待ち合わせは
//    TaskCompletionSource で行う。状態（Phase）はロックの中だけで変え、変わったら PhaseChanged を（ロックの外で）知らせる。
//  【時間切れ】
//    表示の応答が時間内に来ないときは、ランタイムが後から読み込み終える場合に備えて SNAPSHOT_VIEW_END を送って戻させる
//    （ランタイムの END は表示していなければ何もしない＝none を返すので、送っても害は無い）。
//
//  WPF に依存しない（editor/tests/AndroidRunUiTests からリンクされる）。
// ============================================================

using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace SEEDEditor.SceneSnapshot;

/// <summary>写しの表示の状態。</summary>
public enum SceneSnapshotViewPhase
{
    /// <summary>表示していない（編集中のシーンが出ている）。</summary>
    Idle,

    /// <summary>表示を始めている途中（ランタイムが退避と読み込みをしている）。</summary>
    Beginning,

    /// <summary>写しを表示している（閲覧専用）。</summary>
    Showing,

    /// <summary>編集中のシーンへ戻している途中。</summary>
    Ending,
}

/// <summary>写しを出す先（エディタの編集用ランタイム）の窓口。本番は RuntimeManager、テストは偽物。</summary>
public interface ISceneSnapshotViewRuntime
{
    /// <summary>命令を送れるか（編集用ランタイムが Edit でつながっている）。</summary>
    bool CanSend { get; }

    /// <summary>1 行の命令を送る。</summary>
    /// <param name="command">命令。</param>
    /// <returns>送れたら true。</returns>
    bool Send(string command);

    /// <summary>ランタイムから 1 行届いた（受信のスレッドから）。</summary>
    event Action<string>? MessageReceived;
}

/// <summary>写しの表示の指定。</summary>
/// <param name="SnapshotPath">PC の写しのパス（.scene）。</param>
/// <param name="Camera">デバッグカメラを合わせるメインカメラの位置と向き（無ければ null＝写しのファイルに埋めた視点のまま）。</param>
/// <param name="EditorDirty">表示の前にエディタに未保存の変更があったか（戻したときに同じ値へ戻す）。</param>
public sealed record SceneSnapshotViewRequest(string SnapshotPath, SceneSnapshotCameraPose? Camera, bool EditorDirty);

/// <summary>写しの表示を始めた結果。</summary>
/// <param name="Reply">ランタイムの応答（届かなければ Malformed に理由を入れたもの）。</param>
/// <param name="Elapsed">送ってから応答までの時間。</param>
/// <param name="CameraApplied">デバッグカメラをメインカメラへ合わせたか。</param>
public sealed record SceneSnapshotViewBeginResult(SceneSnapshotViewBeginReply Reply, TimeSpan Elapsed, bool CameraApplied);

/// <summary>写しの表示をやめた結果。</summary>
/// <param name="Reply">ランタイムの応答（届かなければ Failed に理由を入れたもの）。</param>
/// <param name="Elapsed">送ってから応答までの時間。</param>
/// <param name="EditorDirty">戻した後のエディタの未保存フラグ（呼び出し側はこの値を当てる）。</param>
public sealed record SceneSnapshotViewEndResult(SceneSnapshotViewEndReply Reply, TimeSpan Elapsed, bool EditorDirty);

/// <summary>シーンの写しを閲覧専用で出し、元の編集中のシーンへ戻す段取り。</summary>
public sealed class SceneSnapshotViewSession
{
    /// <summary>既定の応答待ち（秒）。大きなシーン（地形・モデル）の読み込みでも収まる長さ。</summary>
    public const double DefaultReplyTimeoutSeconds = 120.0;

    /// <summary>送れなかったときの理由。</summary>
    public const string NotConnectedReason = "エディタの編集用ランタイムにつながっていません（Edit でないか、起動の途中です）";

    /// <summary>応答が来なかったときの理由の書式（{0}=秒数）。</summary>
    private const string NoReplyReasonFormat = "編集用ランタイムが {0:F0} 秒以内に応答しませんでした";

    /// <summary>送る先。</summary>
    private readonly ISceneSnapshotViewRuntime _runtime;

    /// <summary>応答待ちの上限。</summary>
    private readonly TimeSpan _replyTimeout;

    /// <summary>段取りを 1 つずつ行うための順番待ち。</summary>
    private readonly SemaphoreSlim _turn = new(1, 1);

    /// <summary>状態の排他。</summary>
    private readonly object _gate = new();

    /// <summary>状態。</summary>
    private SceneSnapshotViewPhase _phase = SceneSnapshotViewPhase.Idle;

    /// <summary>表示している写しのパス（表示していなければ null）。</summary>
    private string? _shownPath;

    /// <summary>表示の前のエディタの未保存フラグ（戻したときに返す）。</summary>
    private bool _dirtyBeforeView;

    /// <summary>送る先と応答待ちの上限を指定して作る。</summary>
    /// <param name="runtime">送る先（エディタの編集用ランタイム）。</param>
    /// <param name="replyTimeout">応答待ちの上限（null なら既定）。</param>
    public SceneSnapshotViewSession(ISceneSnapshotViewRuntime runtime, TimeSpan? replyTimeout = null)
    {
        _runtime = runtime;
        _replyTimeout = replyTimeout ?? TimeSpan.FromSeconds(DefaultReplyTimeoutSeconds);
    }

    /// <summary>状態が変わった（任意のスレッドから。受け手は <see cref="Phase"/> を読み直す）。</summary>
    public event Action? PhaseChanged;

    /// <summary>いまの状態。</summary>
    public SceneSnapshotViewPhase Phase
    {
        get
        {
            lock (_gate) return _phase;
        }
    }

    /// <summary>写しを出している（出し始め・戻している途中を含む。閲覧専用にする範囲）か。</summary>
    public bool IsActive => Phase != SceneSnapshotViewPhase.Idle;

    /// <summary>表示している写しのパス（表示していなければ null）。</summary>
    public string? ShownPath
    {
        get
        {
            lock (_gate) return _shownPath;
        }
    }

    /// <summary>
    /// 写しの表示を始める（前の段取りが終わるまで待つ）。既に表示していれば、ランタイムは退避を取り直さずに写しだけを差し替える。
    /// </summary>
    /// <param name="request">指定。</param>
    /// <param name="cancellationToken">中断の合図（待つのをやめるだけ。送った命令は取り消さない）。</param>
    /// <returns>結果。</returns>
    public async Task<SceneSnapshotViewBeginResult> BeginAsync(SceneSnapshotViewRequest request, CancellationToken cancellationToken)
    {
        await _turn.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_runtime.CanSend)
            {
                return Failed(SceneSnapshotViewBeginOutcome.WrongMode, request.SnapshotPath, NotConnectedReason, TimeSpan.Zero);
            }

            bool wasIdle;
            lock (_gate)
            {
                wasIdle = _phase == SceneSnapshotViewPhase.Idle;
                // 未保存フラグは最初の表示の前の値を覚える（差し替えでは写しを表示中の値なので覚え直さない）
                if (wasIdle) _dirtyBeforeView = request.EditorDirty;
                _phase = SceneSnapshotViewPhase.Beginning;
            }
            RaisePhaseChanged();

            // 未保存の変更が無ければ、メモリへ退避できなかったときにファイルの読み直しで戻してよい
            var command = SceneSnapshotWire.ViewBegin(request.SnapshotPath, allowFileRestore: !request.EditorDirty);
            var stopwatch = Stopwatch.StartNew();
            string? line;
            try
            {
                line = await RequestAsync(command, SceneSnapshotWire.IsViewBeginReply, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 待つのをやめた: 応答が無いときと同じく戻させてから中断を伝える
                AbortBegin(wasIdle);
                throw;
            }
            stopwatch.Stop();

            if (line is null)
            {
                // 応答が無い・送れない: ランタイムが後から読み込み終える場合に備えて戻させる
                AbortBegin(wasIdle);
                return Failed(SceneSnapshotViewBeginOutcome.Malformed, request.SnapshotPath,
                    string.Format(CultureInfo.InvariantCulture, NoReplyReasonFormat, _replyTimeout.TotalSeconds),
                    stopwatch.Elapsed);
            }

            var reply = SceneSnapshotViewBeginReply.Parse(line);
            if (!reply.IsReady)
            {
                // 表示できなかった（ランタイムは何も変えていない）: 前の状態のまま
                SetPhase(wasIdle ? SceneSnapshotViewPhase.Idle : SceneSnapshotViewPhase.Showing, wasIdle ? null : ShownPath);
                return new SceneSnapshotViewBeginResult(reply, stopwatch.Elapsed, CameraApplied: false);
            }

            // デバッグカメラを写しのメインカメラの位置と向きへ合わせる（既存のカメラの命令）
            var cameraApplied = request.Camera is { } pose && _runtime.Send(pose.ToCameraTransformCommand());
            SetPhase(SceneSnapshotViewPhase.Showing, request.SnapshotPath);
            return new SceneSnapshotViewBeginResult(reply, stopwatch.Elapsed, cameraApplied);
        }
        finally
        {
            _turn.Release();
        }
    }

    /// <summary>
    /// 写しの表示をやめて編集中のシーンへ戻す（前の段取りが終わるまで待つ。表示していなければ何もせず None を返す）。
    /// </summary>
    /// <param name="cancellationToken">中断の合図（待つのをやめるだけ）。</param>
    /// <returns>結果（呼び出し側は EditorDirty を未保存フラグへ当てる）。</returns>
    public async Task<SceneSnapshotViewEndResult> EndAsync(CancellationToken cancellationToken)
    {
        await _turn.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            bool dirtyBefore;
            lock (_gate)
            {
                if (_phase == SceneSnapshotViewPhase.Idle)
                {
                    return new SceneSnapshotViewEndResult(
                        new SceneSnapshotViewEndReply(SceneSnapshotRestoreKind.None, string.Empty, 0), TimeSpan.Zero, _dirtyBeforeView);
                }
                dirtyBefore = _dirtyBeforeView;
                _phase = SceneSnapshotViewPhase.Ending;
            }
            RaisePhaseChanged();

            var stopwatch = Stopwatch.StartNew();
            string? line;
            try
            {
                line = _runtime.CanSend
                    ? await RequestAsync(SceneSnapshotWire.ViewEnd, SceneSnapshotWire.IsViewEndReply, cancellationToken).ConfigureAwait(false)
                    : null;
            }
            catch (OperationCanceledException)
            {
                // 待つのをやめた: 閲覧専用は解く（END は送ってあるので、ランタイムは戻し終える）
                SetPhase(SceneSnapshotViewPhase.Idle, null);
                throw;
            }
            stopwatch.Stop();

            var reply = line is null
                ? new SceneSnapshotViewEndReply(SceneSnapshotRestoreKind.Failed,
                    _runtime.CanSend
                        ? string.Format(CultureInfo.InvariantCulture, NoReplyReasonFormat, _replyTimeout.TotalSeconds)
                        : NotConnectedReason,
                    0)
                : SceneSnapshotViewEndReply.Parse(line);

            // 閲覧専用は解く（戻せなかったときも、ランタイムの保存の突き合わせが元のシーンへの上書きを弾く）
            SetPhase(SceneSnapshotViewPhase.Idle, null);
            return new SceneSnapshotViewEndResult(reply, stopwatch.Elapsed, DecideDirtyAfterRestore(reply.Restore, dirtyBefore));
        }
        finally
        {
            _turn.Release();
        }
    }

    /// <summary>
    /// 編集用ランタイムが終わった・作り直された（表示していた写しも退避も失われた）ことを反映して、表示していない状態へ戻す。
    /// 応答は待たない（相手がいない）。
    /// </summary>
    public void Abandon()
    {
        bool changed;
        lock (_gate)
        {
            changed = _phase != SceneSnapshotViewPhase.Idle;
            _phase = SceneSnapshotViewPhase.Idle;
            _shownPath = null;
        }
        if (changed) RaisePhaseChanged();
    }

    /// <summary>
    /// 戻した後の未保存フラグを決める（純粋な処理）。メモリから戻した・表示していなかった・戻せなかったなら表示の前の値、
    /// ファイルを読み直したなら保存済みの内容なので false。
    /// </summary>
    /// <param name="restore">戻し方。</param>
    /// <param name="dirtyBeforeView">表示の前の未保存フラグ。</param>
    /// <returns>当てる未保存フラグ。</returns>
    public static bool DecideDirtyAfterRestore(SceneSnapshotRestoreKind restore, bool dirtyBeforeView) =>
        restore == SceneSnapshotRestoreKind.File ? false : dirtyBeforeView;

    // ── 部品 ───────────────────────────────────────────

    /// <summary>
    /// 命令を送り、応答の行を待つ（送れない・時間切れなら null）。応答を取りこぼさないよう、送る前に受け手を付ける。
    /// </summary>
    private async Task<string?> RequestAsync(string command, Func<string, bool> isReply, CancellationToken cancellationToken)
    {
        var reply = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnLine(string line)
        {
            if (isReply(line)) reply.TrySetResult(line);
        }

        _runtime.MessageReceived += OnLine;
        try
        {
            if (!_runtime.Send(command)) return null;
            var winner = await Task.WhenAny(reply.Task, Task.Delay(_replyTimeout, cancellationToken)).ConfigureAwait(false);
            if (reply.Task.IsCompletedSuccessfully) return reply.Task.Result;
            cancellationToken.ThrowIfCancellationRequested();
            return winner == reply.Task ? reply.Task.Result : null;
        }
        finally
        {
            _runtime.MessageReceived -= OnLine;
        }
    }

    /// <summary>
    /// 表示の始めを取りやめる（応答が無い・待つのをやめた）。ランタイムが後から読み込み終える場合に備えて END を送り
    /// （表示していなければランタイムは何もしない）、最初の表示なら表示していない状態へ、差し替えなら元の写しの表示へ戻す。
    /// </summary>
    /// <param name="wasIdle">この表示の前に何も表示していなかったか。</param>
    private void AbortBegin(bool wasIdle)
    {
        if (wasIdle)
        {
            _runtime.Send(SceneSnapshotWire.ViewEnd);
            SetPhase(SceneSnapshotViewPhase.Idle, null);
            return;
        }
        SetPhase(SceneSnapshotViewPhase.Showing, ShownPath);
    }

    /// <summary>状態と表示している写しを変えて知らせる。</summary>
    private void SetPhase(SceneSnapshotViewPhase phase, string? shownPath)
    {
        lock (_gate)
        {
            _phase = phase;
            _shownPath = shownPath;
        }
        RaisePhaseChanged();
    }

    /// <summary>表示できなかった結果を作る。</summary>
    private static SceneSnapshotViewBeginResult Failed(
        SceneSnapshotViewBeginOutcome outcome, string path, string reason, TimeSpan elapsed) =>
        new(new SceneSnapshotViewBeginReply(outcome, path, 0, 0, reason), elapsed, CameraApplied: false);

    /// <summary>状態の変化を知らせる（受け手の例外で段取りを止めない）。</summary>
    private void RaisePhaseChanged()
    {
        try
        {
            PhaseChanged?.Invoke();
        }
        catch (Exception)
        {
            // 受け手（UI）の不具合で段取りを止めない
        }
    }
}
