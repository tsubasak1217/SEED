using System;

namespace SEED.UI;

// ============================================================
//  SwipeModel.cs — 行のスワイプの状態の移り変わり（開く・閉じる・フルスワイプの構え・確定。W2-3・W2 の手直し P2-3。純粋な計算）
//
//  【役割】エンジンの API に触れない状態の機械。SwipeActions（エンジンへ当てはめる側）が指の移動・離し・経過時間をここへ渡し、
//  ここが返す知らせ（SwipeSignal）でコールバック・触感を出し、Offset と LabelWeight で Front と「削除」の文字を置く。
//  editor/tests/UiComponentsTests から、ドラッグ → 構える → 戻る → 離すの列と構えた回数（= 触感の回数）を単体で確かめる。
//
//  【段階】
//    閉じた・開いた（ドラッグ・開く閉じるの動きを含む）
//      →（構えたまま離す・Commit）流し切りの途中（IsCommitting。Front を行の幅の外へ）
//      → 流し切った（IsDismissed。Reset まで指も Open・Close・Commit も受けない＝二重に確定しない）
//  【値】規則と既定の出典は SwipeMath.cs の冒頭。ずらし量と文字の重みの動きは UiTween（fastOutSlowIn。途中で行き先が変わっても跳ばない）。
//  設定（行の幅・割合・時間）は持ち主が操作の前に入れ直す（テーマの切り替え・行の幅の変化に追従する）。
// ============================================================

/// <summary>操作のボタンがどちら側にあるか。</summary>
public enum SwipeSide
{
    /// <summary>右側（左へずらすと出る。削除など）。</summary>
    Right = 0,
    /// <summary>左側（右へずらすと出る）。</summary>
    Left = 1,
}

/// <summary>スワイプの状態の機械が返す知らせ（1 回の操作で複数立つことがある。立った順に扱う）。</summary>
[Flags]
public enum SwipeSignal
{
    /// <summary>何も起きない。</summary>
    None = 0,
    /// <summary>フルスワイプの閾値を越えて構えた（触感を 1 回出す）。</summary>
    Armed = 1 << 0,
    /// <summary>構えを解いた。</summary>
    Disarmed = 1 << 1,
    /// <summary>開いた（開くと決まった）。</summary>
    Opened = 1 << 2,
    /// <summary>閉じた（閉じると決まった）。</summary>
    Closed = 1 << 3,
    /// <summary>確定した（流し切り始めた）。</summary>
    CommitStarted = 1 << 4,
    /// <summary>流し切った（確定のコールバックを出す）。</summary>
    Dismissed = 1 << 5,
}

/// <summary>行のスワイプの状態（行 1 つぶん。純粋な計算）。</summary>
public sealed class SwipeModel
{
    // ── 設定 ────────────────────────────────────────────────
    /// <summary>操作のボタンの側。</summary>
    public SwipeSide Side { get; }
    /// <summary>開いたときにずらす長さ（操作のボタンの幅。符号は Side で決まる）。</summary>
    public float ActionsExtent { get; set; }
    /// <summary>開くとみなすずらし量の割合（0〜1）。</summary>
    public float OpenThreshold { get; set; } = SwipeMath.DefaultOpenThreshold;
    /// <summary>速さで開く・閉じるを決める閾値（dp/秒）。</summary>
    public float EscapeVelocityDp { get; set; } = SwipeMath.DefaultEscapeVelocityDp;
    /// <summary>開く・閉じるの動きの時間（秒）。</summary>
    public float SettleSeconds { get; set; } = SwipeMath.DefaultSettleSeconds;
    /// <summary>フルスワイプ（大きく払うと確定）が有効か（既定 false = W2-3 の振る舞いのまま）。</summary>
    public bool FullSwipe { get; set; }
    /// <summary>行の幅（持ち主が解決して入れる。0 以下・NaN = 分からない → 構えない・ドラッグは開いた量まで）。</summary>
    public float RowExtent { get; set; }
    /// <summary>構える割合（行の幅に対する。ratio.swipe_full）。</summary>
    public float ArmRatio { get; set; } = SwipeMath.DefaultFullSwipeRatio;
    /// <summary>構えた後に解く割合（ratio.swipe_full_cancel）。</summary>
    public float CancelRatio { get; set; } = SwipeMath.DefaultFullSwipeCancelRatio;
    /// <summary>文字の置き場の補間の時間（秒。motion.swipe_full）。</summary>
    public float LabelSeconds { get; set; } = SwipeMath.DefaultFullSwipeLabelSeconds;
    /// <summary>確定で流し切る時間（秒。motion.swipe_dismiss）。</summary>
    public float DismissSeconds { get; set; } = SwipeMath.DefaultDismissSeconds;

    // ── 状態 ────────────────────────────────────────────────
    /// <summary>今のずらし量（0 = 閉じた・<see cref="OpenOffset"/> = 開いた・確定で行の幅の外まで）。</summary>
    public float Offset => _offset.Value;
    /// <summary>開いた状態（開いた・開く途中）か。流し切りに入った行は false（組の「開いている行」に数えない）。</summary>
    public bool IsOpen { get; private set; }
    /// <summary>指でずらしている途中か。</summary>
    public bool IsDragging { get; private set; }
    /// <summary>フルスワイプの閾値を越えて構えているか。</summary>
    public bool IsArmed { get; private set; }
    /// <summary>確定して流し切っている途中か。</summary>
    public bool IsCommitting { get; private set; }
    /// <summary>流し切った（Reset まで何も受けない）。</summary>
    public bool IsDismissed { get; private set; }
    /// <summary>確定した（流し切りの途中か、流し切った）。</summary>
    public bool IsCommitted => IsCommitting || IsDismissed;
    /// <summary>「削除」の文字の置き場の重み（0 = 元の位置・1 = Front の後ろの端に付く）。</summary>
    public float LabelWeight => _label.Value;
    /// <summary>ずらし量か文字の重みが動いている途中か。</summary>
    public bool IsAnimating => _offset.IsRunning || _label.IsRunning;
    /// <summary>構えた回数（触感を出した回数と同じ。診断・テスト用）。</summary>
    public int ArmCount { get; private set; }
    /// <summary>構えを解いた回数（診断・テスト用）。</summary>
    public int DisarmCount { get; private set; }

    /// <summary>操作の側へずらす向き（右側の操作 = −1・左側 = +1）。</summary>
    public float Direction => Side == SwipeSide.Right ? -1f : 1f;
    /// <summary>開いたときのずらし量（右側の操作なら負）。</summary>
    public float OpenOffset => Direction * MathF.Abs(ActionsExtent);
    /// <summary>ドラッグの端（フルスワイプなら操作の側へ行の幅いっぱい。SwipeMath.DragLimit）。</summary>
    public float DragLimit => SwipeMath.DragLimit(OpenOffset, Direction, RowExtent, FullSwipe);

    /// <summary>ずらし量の動き。</summary>
    private UiTween _offset = UiTween.At(0f);
    /// <summary>文字の重みの動き。</summary>
    private UiTween _label = UiTween.At(0f);

    /// <summary>作る（閉じた状態）。</summary>
    /// <param name="side">操作のボタンの側。</param>
    /// <param name="actionsExtent">開いたときにずらす長さ（操作のボタンの幅）。</param>
    public SwipeModel(SwipeSide side, float actionsExtent)
    {
        Side = side;
        ActionsExtent = actionsExtent;
    }

    /// <summary>ドラッグの始まり（動きを止めて今の位置からずらし始める）。確定の後は何もしない。</summary>
    public SwipeSignal DragStart()
    {
        if (IsCommitted) return SwipeSignal.None;
        IsDragging = true;
        _offset.Jump(_offset.Value);
        return SwipeSignal.None;
    }

    /// <summary>指の横の移動を当てる（ドラッグの端へ収め、構え・解くを決める）。確定の後は何もしない。</summary>
    /// <param name="delta">指の横の移動（キャンバスの単位。右が正）。</param>
    public SwipeSignal DragBy(float delta)
    {
        if (IsCommitted) return SwipeSignal.None;
        var signals = IsDragging ? SwipeSignal.None : DragStart();
        _offset.Jump(SwipeMath.ApplyDrag(Offset, delta, DragLimit));
        return signals | UpdateArm();
    }

    /// <summary>
    /// 指を離した（取り消しを含む）。構えていれば確定（流し切り）、そうでなければ開く・閉じるを決めて動かす
    /// （SwipeMath.DecideRelease）。確定の後は何もしない。
    /// </summary>
    /// <param name="velocityDp">離したときの横の速さ（dp/秒。右向きが正）。</param>
    /// <param name="canceled">取り消しで終わったか（構えを解いて元の状態へ）。</param>
    public SwipeSignal Release(float velocityDp, bool canceled)
    {
        if (IsCommitted) return SwipeSignal.None;
        IsDragging = false;
        var decision = SwipeMath.DecideRelease(IsArmed && FullSwipe, canceled, IsOpen, Offset, velocityDp,
            OpenOffset, OpenThreshold, EscapeVelocityDp, Direction);
        if (decision == SwipeRelease.Commit) return StartCommit();
        return SetArmed(false) | SetOpen(decision == SwipeRelease.Open, animated: true);
    }

    /// <summary>確定の流れ（流し切り → 流し切った知らせ）を始める（操作のボタンのタップなど）。確定の後は何もしない。</summary>
    public SwipeSignal Commit() => IsCommitted ? SwipeSignal.None : StartCommit();

    /// <summary>開く（確定の後は何もしない）。</summary>
    public SwipeSignal Open(bool animated = true) => IsCommitted ? SwipeSignal.None : SetOpen(true, animated);

    /// <summary>閉じる（構えも解く。確定の後は何もしない）。</summary>
    public SwipeSignal Close(bool animated = true)
        => IsCommitted ? SwipeSignal.None : SetArmed(false) | SetOpen(false, animated);

    /// <summary>
    /// すぐ閉じた状態へ戻す（ドラッグ・構え・確定・文字の重みもやめる。行の使い回し・データの差し替え）。
    /// 開いていたなら Closed を返す（W2-3 の Reset と同じ）。構えの解きは知らせない（触感を出さない）。
    /// </summary>
    public SwipeSignal Reset()
    {
        bool wasOpen = IsOpen;
        IsDragging = false;
        IsArmed = false;
        IsCommitting = false;
        IsDismissed = false;
        IsOpen = false;
        _offset.Jump(0f);
        _label.Jump(0f);
        return wasOpen ? SwipeSignal.Closed : SwipeSignal.None;
    }

    /// <summary>時間を進める（開く・閉じる・流し切り・文字の重みの動き）。流し切り終わったら Dismissed。</summary>
    /// <param name="dt">経過時間（秒。負は 0）。</param>
    public SwipeSignal Advance(float dt)
    {
        _offset.Advance(dt);
        _label.Advance(dt);
        if (IsCommitting && !_offset.IsRunning)
        {
            IsCommitting = false;
            IsDismissed = true;
            return SwipeSignal.Dismissed;
        }
        return SwipeSignal.None;
    }

    /// <summary>今のずらし量から構え・解くを決める（フルスワイプが無効なら解く）。</summary>
    private SwipeSignal UpdateArm()
        => SetArmed(FullSwipe && SwipeMath.UpdateArmed(IsArmed, Offset, Direction, OpenOffset, RowExtent, ArmRatio, CancelRatio));

    /// <summary>構えを変える（変わったら文字の重みを動かし、回数を数えて知らせる）。</summary>
    private SwipeSignal SetArmed(bool armed)
    {
        if (armed == IsArmed) return SwipeSignal.None;
        IsArmed = armed;
        _label.Retarget(armed ? 1f : 0f, LabelSeconds);
        if (armed)
        {
            ArmCount++;
            return SwipeSignal.Armed;
        }
        DisarmCount++;
        return SwipeSignal.Disarmed;
    }

    /// <summary>開く・閉じるを決めて動かす（または、すぐ移す）。変わったら知らせる。</summary>
    private SwipeSignal SetOpen(bool open, bool animated)
    {
        bool changed = open != IsOpen;
        IsOpen = open;
        MoveOffset(open ? OpenOffset : 0f, animated ? SettleSeconds : 0f);
        if (!changed) return SwipeSignal.None;
        return open ? SwipeSignal.Opened : SwipeSignal.Closed;
    }

    /// <summary>
    /// 確定: 組の「開いている行」から外し（Closed は知らせない）、文字を Front の後ろの端へ付け（重み → 1）、
    /// Front を行の幅の外まで流し始める（行の幅が分からなければ開いた量まで）。
    /// </summary>
    private SwipeSignal StartCommit()
    {
        IsDragging = false;
        IsOpen = false;
        IsCommitting = true;
        _label.Retarget(1f, LabelSeconds);
        MoveOffset(SwipeMath.DismissOffset(Direction, RowExtent, OpenOffset), DismissSeconds);
        return SwipeSignal.CommitStarted;
    }

    /// <summary>ずらし量を今の値から行き先へ動かす（時間 0 以下か同じ値ならすぐ移す）。</summary>
    private void MoveOffset(float target, float seconds)
    {
        // ドラッグで動かした後は動きの行き先が古いので、今の値から始め直す（UiTween.Retarget は同じ行き先を無視するため）
        _offset.Jump(Offset);
        if (seconds > 0f && Offset != target) _offset.Retarget(target, seconds);
        else _offset.Jump(target);
    }
}
