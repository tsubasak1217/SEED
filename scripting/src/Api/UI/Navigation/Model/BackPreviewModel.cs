using System;
using SEED.Platform;

namespace SEED.UI;

// ============================================================
//  BackPreviewModel.cs — 予測型の戻るのプレビューの移り変わり（W2 の手直し 3b。純粋な計算）
//
//  戻るの手ぶりの知らせ（3a の platform.back_started・back_progressed・back_cancelled・back_invoked）と戻るのキー（Escape）から、
//  プレビューの段階と姿勢（BackPreviewPose）を決める。呼び手（BackPreview.cs）は、ここが返す「すること」（BackPreviewStep）に従って
//  相手（IBackPreviewTarget）へ姿勢を当て、Escape で確定して戻るの段（BackDispatcher.Dispatch）へ配る。
//
//  【届く順】（3a の申し送り・docs/android.md §25.18）
//    手ぶり（Android 14 以上）  : started → progressed（毎フレーム）→ cancelled か invoked（→ 合成の KEYCODE_BACK＝Escape）
//    ボタンの戻る（14 以上）     : キーの down で started（進み具合 0）、up で invoked → Escape
//    Android 13                 : invoked → Escape だけ（プレビューなし）
//    PC の模擬（SimulateBackGesture）: 同じ知らせ。invoked の後に Escape は来ない（Esc キーで確定する）
//  invoked の知らせ（プラットフォームのイベントの箱）と Escape（入力の状態）は別の道で届き、同じフレームとは限らない。
//
//  【段階】
//    Idle        … プレビューなし
//    Tracking    … 手ぶりの途中（姿勢 = 曲線(進み具合)）
//    AwaitingKey … 確定の知らせが来て Escape を待っている（姿勢はそのまま。KeyWaitTimeout を過ぎたら諦めて元へ戻す）
//    Settling    … 取り消し・諦め・確定したのに閉じなかった: 今の姿勢から元（倍率 1）へ戻る動き（テーマの motion.short）
//  Escape（OnKey）は Tracking・AwaitingKey の手ぶりを確定する（呼び手は姿勢を保ったまま戻るを配り、閉じる動きをその姿勢から続ける）。
//
//  【番号】手ぶりの通し番号で、確定・取り消し・諦めた手ぶりの遅れた知らせ（progressed・cancelled・invoked）を捨てる。
//  started は必ず新しい手ぶり（番号は Java ではプロセスごと、模擬では Play の区切りごとに 1 から数え直すので、前と同じ番号もありうる）。
//  started の来なかった手ぶりの progressed は、新しい手ぶりとして始める。
// ============================================================

/// <summary>プレビューの段階。</summary>
public enum BackPreviewPhase
{
    /// <summary>プレビューなし。</summary>
    Idle = 0,
    /// <summary>手ぶりの途中（started〜progressed）。</summary>
    Tracking = 1,
    /// <summary>確定の知らせ（invoked）が来て、戻るのキー（Escape）を待っている。</summary>
    AwaitingKey = 2,
    /// <summary>今の姿勢から元の姿勢へ戻る動きの途中。</summary>
    Settling = 3,
}

/// <summary>知らせ・時間を受けた結果（呼び手がすること）。</summary>
public enum BackPreviewStep
{
    /// <summary>何もしない（遅れた知らせ・別の手ぶり・プレビューの無い確定）。</summary>
    None = 0,
    /// <summary>新しい手ぶり: 前の相手をすぐ元へ戻し、最初に受ける層の相手を問い、姿勢を当て始める。</summary>
    Begin = 1,
    /// <summary>姿勢が変わった: 相手へ当てる。</summary>
    Update = 2,
    /// <summary>確定の知らせ: 戻るのキーを待つ（姿勢はそのまま）。</summary>
    Await = 3,
    /// <summary>取り消し・諦め: 元へ戻る動きを始めた（以降は Update・Finish）。</summary>
    Cancel = 4,
    /// <summary>元へ戻る動きが終わった: 相手を元の姿勢にして手放す。</summary>
    Finish = 5,
}

/// <summary>予測型の戻るのプレビューの移り変わり（1 つの手ぶりずつ）。</summary>
public sealed class BackPreviewModel
{
    /// <summary>いちばん小さい倍率の既定（テーマの ratio.back_preview_scale が無いとき。Material 3 の 90%。記憶による）。</summary>
    public const float DefaultMinScale = 0.9f;

    /// <summary>ずらす量の既定（テーマの size.back_preview_shift が無いとき。キャンバスの単位＝dp。記憶による）。</summary>
    public const float DefaultMaxShift = 8f;

    /// <summary>元へ戻る時間の既定（テーマの motion.short が無いとき。秒。既定のテーマの motion.short と同じ）。</summary>
    public const float DefaultSettleDuration = 0.15f;

    /// <summary>
    /// 確定の知らせ（invoked）の後に戻るのキー（Escape）を待つ時間（秒）。Android は合成の KEYCODE_BACK が UI スレッド →
    /// GameActivity → winit を通って数フレームで届く（60 fps で 30 フレーム分の余裕）。PC の模擬は Escape を注入しないので、
    /// この間に Esc を押さなければ元へ戻す。
    /// </summary>
    public const float KeyWaitTimeout = 0.5f;

    /// <summary>手ぶりが無いときの番号（Java・模擬の番号は 1 から）。</summary>
    public const long NoGesture = 0;

    /// <summary>いちばん小さい倍率（見た目の進み 1 の倍率）。</summary>
    public float MinScale { get; set; } = DefaultMinScale;

    /// <summary>いちばん大きなずらし（キャンバスの単位）。</summary>
    public float MaxShift { get; set; } = DefaultMaxShift;

    /// <summary>手ぶりの進み具合 → 見た目の進みの曲線（既定は減速。テーマの motion.back_preview_curve）。</summary>
    public UiCurve Curve { get; set; } = UiCurve.Decelerate;

    /// <summary>元へ戻る時間（秒）。</summary>
    public float SettleDuration { get; set; } = DefaultSettleDuration;

    /// <summary>段階。</summary>
    public BackPreviewPhase Phase { get; private set; } = BackPreviewPhase.Idle;

    /// <summary>今の（Idle なら最後の）手ぶりの番号。</summary>
    public long Gesture { get; private set; } = NoGesture;

    /// <summary>終えた（確定・取り消し・諦めた）いちばん新しい手ぶりの番号（遅れた知らせを捨てる）。</summary>
    public long LastFinished { get; private set; } = NoGesture;

    /// <summary>手ぶりの進み具合（0〜1。曲線の前）。</summary>
    public float Progress { get; private set; }

    /// <summary>手ぶりを始めた端（ずらす向き）。</summary>
    public BackEdge Edge { get; private set; } = BackEdge.None;

    /// <summary>元へ戻る動き（見た目の進み → 0）。</summary>
    private UiTween _settle = UiTween.At(0f);

    /// <summary>確定の知らせの後に待った時間（秒）。</summary>
    private float _waited;

    /// <summary>確定したときの見た目の進み（確定したのに閉じなかったとき、ここから元へ戻す）。</summary>
    private float _committedVisual;

    /// <summary>プレビュー中か（Idle 以外）。</summary>
    public bool IsActive => Phase != BackPreviewPhase.Idle;

    /// <summary>見た目の進み（0 = 元の姿勢・1 = いちばん縮んだ姿勢）。</summary>
    public float Visual => Phase switch
    {
        BackPreviewPhase.Tracking or BackPreviewPhase.AwaitingKey => Curve.Evaluate(Progress),
        BackPreviewPhase.Settling => _settle.Value,
        _ => 0f,
    };

    /// <summary>今の姿勢。</summary>
    public BackPreviewPose Pose => BackPreviewMath.Pose(Visual, BackPreviewMath.ShiftSign(Edge), MinScale, MaxShift);

    /// <summary>
    /// 手ぶりの知らせを受ける。
    /// </summary>
    /// <param name="phase">段階（イベントの名前から）。</param>
    /// <param name="gesture">手ぶりの番号。</param>
    /// <param name="progress">進み具合（started・progressed だけが使う）。</param>
    /// <param name="edge">始めた端（started・progressed だけが使う）。</param>
    public BackPreviewStep Receive(BackGesturePhase phase, long gesture, float progress, BackEdge edge) => phase switch
    {
        BackGesturePhase.Started => OnStarted(gesture, progress, edge),
        BackGesturePhase.Progressed => OnProgressed(gesture, progress, edge),
        BackGesturePhase.Cancelled => OnCancelled(gesture),
        _ => OnInvoked(gesture),
    };

    /// <summary>
    /// 戻るのキー（Escape）が来た: 手ぶりの途中・確定待ちなら、この手ぶりを確定する（true。姿勢は呼び手の相手に残る）。
    /// それ以外（プレビューなし・元へ戻る途中）は false（ふつうの戻る）。
    /// </summary>
    public bool OnKey()
    {
        if (Phase is not (BackPreviewPhase.Tracking or BackPreviewPhase.AwaitingKey)) return false;
        _committedVisual = Visual;
        LastFinished = Gesture;
        Phase = BackPreviewPhase.Idle;
        return true;
    }

    /// <summary>
    /// 確定したのに相手が閉じなかった（戻るを別の層が受けた・画面が下ろさなかった）: 確定したときの姿勢から元へ戻す。
    /// </summary>
    public void SettleBack()
    {
        StartSettling(_committedVisual);
    }

    /// <summary>
    /// 時間を進める（元へ戻る動き・確定の知らせの後の待ち）。
    /// </summary>
    /// <param name="dt">経過（秒。負は 0）。</param>
    public BackPreviewStep Tick(float dt)
    {
        float step = float.IsFinite(dt) ? Math.Max(0f, dt) : 0f;
        switch (Phase)
        {
            case BackPreviewPhase.AwaitingKey:
                _waited += step;
                if (_waited < KeyWaitTimeout) return BackPreviewStep.None;
                // Escape が来なかった（PC の模擬で Esc を押さなかった等）: 諦めて元へ戻す（遅れた Escape はふつうの戻る）
                StartSettling(Visual);
                return BackPreviewStep.Cancel;
            case BackPreviewPhase.Settling:
                _settle.Advance(step);
                if (_settle.IsRunning) return BackPreviewStep.Update;
                Phase = BackPreviewPhase.Idle;
                return BackPreviewStep.Finish;
            default:
                return BackPreviewStep.None;
        }
    }

    /// <summary>プレビューをすぐやめる（相手が無効になった。呼び手が相手をすぐ元へ戻す）。</summary>
    public void Abandon()
    {
        if (Phase == BackPreviewPhase.Idle) return;
        LastFinished = Gesture;
        Phase = BackPreviewPhase.Idle;
    }

    /// <summary>すべて忘れる（スクリプトの読み直し。番号も最初から）。</summary>
    public void Reset()
    {
        Phase = BackPreviewPhase.Idle;
        Gesture = NoGesture;
        LastFinished = NoGesture;
        Progress = 0f;
        Edge = BackEdge.None;
        _settle = UiTween.At(0f);
        _waited = 0f;
        _committedVisual = 0f;
    }

    /// <summary>started: 必ず新しい手ぶり（同じ手ぶりの 2 度目の started は進み具合だけ）。</summary>
    private BackPreviewStep OnStarted(long gesture, float progress, BackEdge edge)
    {
        if (Phase == BackPreviewPhase.Tracking && gesture == Gesture)
        {
            Track(progress, edge);
            return BackPreviewStep.Update;
        }
        Start(gesture, progress, edge);
        return BackPreviewStep.Begin;
    }

    /// <summary>progressed: 今の手ぶりなら進める。終えた手ぶりの遅れた知らせは捨てる。知らない手ぶりは始める。</summary>
    private BackPreviewStep OnProgressed(long gesture, float progress, BackEdge edge)
    {
        if (gesture == Gesture || gesture == LastFinished)
        {
            if (Phase != BackPreviewPhase.Tracking || gesture != Gesture) return BackPreviewStep.None;
            Track(progress, edge);
            return BackPreviewStep.Update;
        }
        Start(gesture, progress, edge);
        return BackPreviewStep.Begin;
    }

    /// <summary>cancelled: 今の手ぶり（途中・確定待ち）なら元へ戻し始める。</summary>
    private BackPreviewStep OnCancelled(long gesture)
    {
        if (gesture != Gesture || Phase is not (BackPreviewPhase.Tracking or BackPreviewPhase.AwaitingKey)) return BackPreviewStep.None;
        StartSettling(Visual);
        return BackPreviewStep.Cancel;
    }

    /// <summary>invoked: 今の手ぶりの途中なら Escape を待つ。それ以外（Android 13・確定済みの遅れた知らせ・知らない番号）は何もしない。</summary>
    private BackPreviewStep OnInvoked(long gesture)
    {
        if (gesture != Gesture || Phase != BackPreviewPhase.Tracking) return BackPreviewStep.None;
        Phase = BackPreviewPhase.AwaitingKey;
        _waited = 0f;
        return BackPreviewStep.Await;
    }

    /// <summary>手ぶりを始める。</summary>
    private void Start(long gesture, float progress, BackEdge edge)
    {
        Gesture = gesture;
        Phase = BackPreviewPhase.Tracking;
        _waited = 0f;
        Track(progress, edge);
    }

    /// <summary>進み具合と端を覚える（0〜1 へ収める。NaN は 0）。</summary>
    private void Track(float progress, BackEdge edge)
    {
        Progress = float.IsFinite(progress) ? Math.Clamp(progress, 0f, 1f) : 0f;
        Edge = edge;
    }

    /// <summary>見た目の進み from から元へ戻る動きを始める（この手ぶりは終えたものにする）。</summary>
    private void StartSettling(float from)
    {
        LastFinished = Gesture;
        _settle = UiTween.At(from);
        _settle.Retarget(0f, SettleDuration);
        Phase = BackPreviewPhase.Settling;
    }
}
