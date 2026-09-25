// ============================================================
//  AndroidPauseSnapshotViewCoordinator.cs — Android の一時停止に合わせて、端末のシーンの写しをシーンパネルへ出し・戻す段取り
//
//  【役割】（docs/android.md §20.17。WPF に依存しない＝単体テスト AndroidRunUiTests が偽の窓口で確かめる）
//    Android の実行の状態が変わるたびに（MainWindow が UI スレッドで OnRunStateChanged を呼ぶ）、次の 1 手を決めて行う:
//      Paused で写しを取り出せた（AndroidPauseSnapshotStatus.Ready）→ 出す（SceneSnapshotViewSession.BeginAsync）
//        ・出す前にエディタの編集の途中を閉じる（キャンバス編集タブ・アクター編集・押下中のカメラキー・地形モード。窓口）
//        ・編集中のシーンをメモリへ退避できなかったときだけ、保存して出すかを尋ねる（保存しなければ出さない）
//      Paused でなくなった（再開・停止・切断・アプリの終了・実行の終わり）→ 戻す（EndAsync）→ 未保存フラグを元の値へ
//    出す・戻すは 1 つずつ順に行い、終わったら最新の状態で決め直す（途中で再開されたら、出し終えてからすぐ戻す）。
//    同じ回の写しは 1 度しか出そうとしない（読み込めなかった写しを何度も試さない。あきらめた回は GivenUpGeneration）。
//  【出さないとき】PC の編集用ランタイムが Edit でない（起動・作り直しの途中）等は、窓口が理由を返し、Output に 1 行出す。
//  【閲覧専用】出している間（出し始め・戻す途中を含む）は IsViewActive が true。保存・編集の拒否は EditorReadOnlyPolicy、
//              ランタイムが捨てた命令の知らせ（SNAPSHOT_VIEW_REFUSED）は OnRefused で間引いて Output へ出す。
//
//  WPF に依存しない（editor/tests/AndroidRunUiTests からリンクされる）。
// ============================================================

using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using SEEDEditor.SceneSnapshot;

namespace SEEDEditor.AndroidRun;

/// <summary>写しを出す・戻すのに要るエディタ側の窓口（本番は MainWindow、テストは偽物。呼ぶのは UI スレッド）。</summary>
public interface IAndroidSnapshotViewHost
{
    /// <summary>エディタに未保存の変更があるか。</summary>
    bool IsEditorDirty { get; }

    /// <summary>写しを出せない理由（PC の編集用ランタイムが Edit でない等）。出せるなら null。</summary>
    string? SnapshotViewBlocker { get; }

    /// <summary>写しを出す前に、エディタの編集の途中を閉じる（キャンバス編集タブ・アクター編集・押下中のカメラキー・地形モード）。</summary>
    void PrepareForSnapshotView();

    /// <summary>
    /// 写しを出した後に呼ぶ（写しの読み込みで入れ替わったビューポートの設定〈表示モード・ポストエフェクト等〉をエディタの値へ揃え直す）。
    /// </summary>
    void OnSnapshotShown();

    /// <summary>戻した後の未保存フラグを当てる。</summary>
    /// <param name="dirty">当てる値。</param>
    void ApplyDirtyAfterRestore(bool dirty);

    /// <summary>
    /// 編集中のシーンをメモリへ退避できなかったとき、保存してから写しを出すかを尋ねる。保存が済んだら true
    /// （出さない・保存に失敗したら false）。
    /// </summary>
    /// <param name="reason">退避できなかった理由。</param>
    /// <returns>保存が済んだら true。</returns>
    Task<bool> SaveBeforeViewAsync(string reason);
}

/// <summary>次の 1 手。</summary>
public enum SnapshotViewStep
{
    /// <summary>何もしない。</summary>
    None,

    /// <summary>写しを出す。</summary>
    Begin,

    /// <summary>写しをやめて編集中のシーンへ戻す。</summary>
    End,
}

/// <summary>Android の一時停止に合わせて、端末のシーンの写しをシーンパネルへ出し・戻す段取り。</summary>
public sealed class AndroidPauseSnapshotViewCoordinator
{
    /// <summary>閲覧専用で捨てた命令の知らせを Output へ出す最小の間隔（秒）。ドラッグ等で連打されても行を溢れさせない。</summary>
    public const double RefusedNoticeIntervalSeconds = 2.0;

    /// <summary>写しを出す段取り。</summary>
    private readonly SceneSnapshotViewSession _session;

    /// <summary>エディタ側の窓口。</summary>
    private readonly IAndroidSnapshotViewHost _host;

    /// <summary>捨てた命令の知らせの間引き（最後に出した時刻）。</summary>
    private readonly Stopwatch _refusedClock = new();

    /// <summary>最新の Android の実行の写し。</summary>
    private AndroidRunSnapshot _latest = AndroidRunSnapshot.Idle;

    /// <summary>出す・戻すを行っている途中か（UI スレッドだけで読み書きする）。</summary>
    private bool _pumping;

    /// <summary>行っている途中に状態が変わったか（終わったら決め直す）。</summary>
    private bool _repump;

    /// <summary>最後に出そうとした写しの回数（同じ回を 2 度試さない）。</summary>
    private int _attemptedGeneration;

    /// <summary>出している写しの実行先の表示名（バナー・閲覧専用の文言用）。</summary>
    private string? _shownTargetText;

    /// <summary>段取りと窓口を指定して作る。</summary>
    /// <param name="session">写しを出す段取り。</param>
    /// <param name="host">エディタ側の窓口。</param>
    public AndroidPauseSnapshotViewCoordinator(SceneSnapshotViewSession session, IAndroidSnapshotViewHost host)
    {
        _session = session;
        _host = host;
        _session.PhaseChanged += RaiseStateChanged;
    }

    /// <summary>Output パネルへ出す行（UI スレッドか、段取りの続きのスレッドから）。</summary>
    public event Action<AndroidRunOutputLine>? OutputWritten;

    /// <summary>出している状態が変わった（受け手はビューポート・閲覧専用の表示を当て直す。任意のスレッドから）。</summary>
    public event Action? StateChanged;

    /// <summary>写しを出す段取りの状態。</summary>
    public SceneSnapshotViewPhase ViewPhase => _session.Phase;

    /// <summary>写しを出している（出し始め・戻す途中を含む＝閲覧専用にする範囲）か。</summary>
    public bool IsViewActive => _session.IsActive;

    /// <summary>出している写しの実行先の表示名（出していなければ null）。</summary>
    public string? ShownTargetText => IsViewActive ? _shownTargetText : null;

    /// <summary>出すのをあきらめた写しの回数（ビューポートの案内を「読み込み中」から戻すのに使う。無ければ 0）。</summary>
    public int GivenUpGeneration { get; private set; }

    /// <summary>
    /// 次の 1 手を決める（純粋な処理）。
    /// Paused で写しがあり、何も出しておらず、その回をまだ試していなければ出す。Paused で写しがある状態でなくなったのに
    /// 出していれば戻す。出し始め・戻す途中は何もしない（終わってから決め直す）。
    /// </summary>
    /// <param name="run">Android の実行の写し。</param>
    /// <param name="view">写しを出す段取りの状態。</param>
    /// <param name="attemptedGeneration">最後に出そうとした写しの回数。</param>
    /// <returns>次の 1 手。</returns>
    public static SnapshotViewStep Decide(AndroidRunSnapshot run, SceneSnapshotViewPhase view, int attemptedGeneration)
    {
        var wanted = run.Phase == AndroidRunPhase.Paused && run.PauseSnapshot.IsReady;
        if (wanted)
        {
            return view == SceneSnapshotViewPhase.Idle && run.PauseSnapshot.Generation != attemptedGeneration
                ? SnapshotViewStep.Begin
                : SnapshotViewStep.None;
        }
        return view == SceneSnapshotViewPhase.Showing ? SnapshotViewStep.End : SnapshotViewStep.None;
    }

    /// <summary>
    /// Android の実行の状態が変わった（UI スレッドから）。次の 1 手を行う（行っている途中なら、終わってから決め直す）。
    /// </summary>
    /// <param name="run">Android の実行の写し。</param>
    public void OnRunStateChanged(AndroidRunSnapshot run)
    {
        _latest = run;
        if (_pumping)
        {
            _repump = true;
            return;
        }
        _ = PumpAsync();
    }

    /// <summary>
    /// エディタの編集用ランタイムが終わった・作り直された（退避していた編集中のシーンも写しも失われた）。
    /// 出していれば、出していない状態へ戻して理由を出す（UI スレッドから）。
    /// </summary>
    public void OnEditRuntimeLost()
    {
        if (!_session.IsActive) return;
        _session.Abandon();
        GivenUpGeneration = _attemptedGeneration;
        Emit(AndroidPauseSnapshotOutputFormatter.EditRuntimeLost());
        RaiseStateChanged();
    }

    /// <summary>
    /// エディタを閉じる前に、出している写しをやめて編集中のシーンへ戻す（出していなければすぐ終わる）。
    /// 戻した後の未保存フラグを当てるので、閉じる前の「保存しますか」は元のシーンについて尋ねられる。
    /// </summary>
    /// <returns>完了。</returns>
    public async Task EndForShutdownAsync()
    {
        if (!_session.IsActive) return;
        await EndAsync();
    }

    /// <summary>
    /// ランタイムが閲覧専用のため命令を捨てた（SNAPSHOT_VIEW_REFUSED:{命令の名前}）。間引いて Output へ出す。
    /// </summary>
    /// <param name="command">命令の名前。</param>
    /// <returns>Output へ出したら true（間引いたら false。呼び出し側はトーストを出すかの判断に使う）。</returns>
    public bool OnRefused(string command)
    {
        if (_refusedClock.IsRunning && _refusedClock.Elapsed.TotalSeconds < RefusedNoticeIntervalSeconds) return false;
        _refusedClock.Restart();
        Emit(AndroidPauseSnapshotOutputFormatter.Refused(command));
        return true;
    }

    // ── 段取り ─────────────────────────────────────────────

    /// <summary>次の 1 手を、何もすることが無くなるまで順に行う。</summary>
    private async Task PumpAsync()
    {
        _pumping = true;
        try
        {
            while (true)
            {
                _repump = false;
                var step = Decide(_latest, _session.Phase, _attemptedGeneration);
                if (step == SnapshotViewStep.Begin)
                {
                    await BeginAsync(_latest);
                }
                else if (step == SnapshotViewStep.End)
                {
                    await EndAsync();
                }
                else if (!_repump)
                {
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            // 段取りの不具合でエディタを止めない（理由だけ出す）
            Emit(AndroidPauseSnapshotOutputFormatter.ViewUnavailable(ex.Message));
        }
        finally
        {
            _pumping = false;
        }
    }

    /// <summary>写しを出す（退避できなければ保存を尋ね、保存できたらもう 1 度だけ試す）。</summary>
    private async Task BeginAsync(AndroidRunSnapshot run)
    {
        var snapshot = run.PauseSnapshot;
        _attemptedGeneration = snapshot.Generation;
        if (_host.SnapshotViewBlocker is { } blocker)
        {
            GiveUp(snapshot.Generation, AndroidPauseSnapshotOutputFormatter.ViewUnavailable(blocker));
            return;
        }

        _host.PrepareForSnapshotView();
        _shownTargetText = run.TargetText;
        var result = await _session.BeginAsync(
            new SceneSnapshotViewRequest(snapshot.LocalPath!, snapshot.Camera, _host.IsEditorDirty), CancellationToken.None);

        if (result.Reply.Outcome == SceneSnapshotViewBeginOutcome.StashFailed && _host.IsEditorDirty)
        {
            // 編集中のシーンをメモリへ退避できない: 未保存の変更を守るため、保存してからでないと出さない
            var saved = await _host.SaveBeforeViewAsync(result.Reply.Reason ?? string.Empty);
            if (!saved)
            {
                GiveUp(snapshot.Generation, AndroidPauseSnapshotOutputFormatter.ViewSkippedUnsaved());
                return;
            }
            // 尋ねている間に再開・停止されていたら出さない（次の決め直しで何もしない）
            if (_latest.Phase != AndroidRunPhase.Paused || _latest.PauseSnapshot.Generation != snapshot.Generation) return;
            // 保存したので、退避できなければ保存済みのファイルを読み直して戻してよい
            result = await _session.BeginAsync(
                new SceneSnapshotViewRequest(snapshot.LocalPath!, snapshot.Camera, EditorDirty: false), CancellationToken.None);
        }

        if (result.Reply.IsReady)
        {
            _host.OnSnapshotShown();
        }
        else
        {
            GivenUpGeneration = snapshot.Generation;
        }
        Emit(AndroidPauseSnapshotOutputFormatter.ViewBegun(result));
        RaiseStateChanged();
    }

    /// <summary>写しをやめて編集中のシーンへ戻し、未保存フラグを元の値へ当てる。</summary>
    private async Task EndAsync()
    {
        var result = await _session.EndAsync(CancellationToken.None);
        _host.ApplyDirtyAfterRestore(result.EditorDirty);
        if (AndroidPauseSnapshotOutputFormatter.ViewEnded(result) is { } line) Emit(line);
        RaiseStateChanged();
    }

    /// <summary>その回の写しを出すのをあきらめ、理由を出す。</summary>
    private void GiveUp(int generation, AndroidRunOutputLine line)
    {
        GivenUpGeneration = generation;
        Emit(line);
        RaiseStateChanged();
    }

    /// <summary>1 行を出す（受け手の例外で段取りを止めない）。</summary>
    private void Emit(AndroidRunOutputLine line)
    {
        try
        {
            OutputWritten?.Invoke(line);
        }
        catch (Exception)
        {
            // 受け手（UI・ログ）の不具合で段取りを止めない
        }
    }

    /// <summary>状態の変化を知らせる（受け手の例外で段取りを止めない）。</summary>
    private void RaiseStateChanged()
    {
        try
        {
            StateChanged?.Invoke();
        }
        catch (Exception)
        {
            // 受け手（UI）の不具合で段取りを止めない
        }
    }
}
