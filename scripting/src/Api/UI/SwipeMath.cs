using System;

namespace SEED.UI;

// ============================================================
//  SwipeMath.cs — 行のスワイプの操作の計算（ずらし量・離したときの行き先・フルスワイプの構え・文字の置き場・動きの曲線。
//                 W2-3、フルスワイプは W2 の手直し P2-3。docs/ui_scroll_list.md §7）
//
//  エンジンの API に触れない純粋な計算（editor/tests/UiListViewTests・UiComponentsTests から単体で試せる）。
//  状態の移り変わり（ドラッグ → 離す → 動き → 確定）は SwipeModel.cs、エンジンへの当てはめは SwipeActions.cs。
//
//  【開く・閉じるの規則】（Android の ItemTouchHelper に倣う。2026-09-28 に androidx のソースで値を確かめた）
//    - 離したときの横の速さが「逃げの速さ」（既定 120 dp/秒 = item_touch_helper_swipe_escape_velocity）以上なら、
//      速さの向きで決める（開く向きなら開く・逆なら閉じる）
//    - それより遅ければ、ずらした量が開いた量の「閾値」（既定 0.5 = ItemTouchHelper.getSwipeThreshold）以上なら開く
//  【ずらし量】閉じた 0 から「ドラッグの端」までに収める（端より先へは引けない）。端は開いた量（右側の操作なら負）、
//    フルスワイプが有効で行の幅が分かれば、操作の側へ行の幅いっぱい（DragLimit）。
//  【動き】開く・閉じるは 250ms（ItemTouchHelper.DEFAULT_SWIPE_ANIMATION_DURATION）、曲線は Material の
//    fastOutSlowIn = Cubic(0.4, 0.0, 0.2, 1.0)（Flutter の Curves.fastOutSlowIn と同じ）。
//
//  【フルスワイプ】（W2 の手直し P2-3。iOS のメールの「大きく払うとそのまま削除」に当たる。値は docs/backlog.md の案で、
//    iOS の閾値・時間は公開されていないので決めた値。テーマのトークン ratio.swipe_full・ratio.swipe_full_cancel・motion.swipe_* で変えられる）
//    - 構える: 操作の側へのずらし量が「行の幅 × 構える割合（0.6）」以上。ただし開いた量（操作のボタンの幅）より手前では構えない
//      （行が狭くても、ボタンを見せるだけの払いが削除にならない）
//    - 解く: 構えた後、「行の幅 × 解く割合（0.55）」を下回ったら（ヒステリシス。閾値の近くで指が揺れても構え・解くがばたつかない）
//    - 離したとき: 取り消し → 元の状態／構えている → 確定（ただし閉じる向きへ逃げの速さ以上で払って離したら確定しない＝
//      速さが勝つ開く・閉じるの規則と同じ考え方）／構えていない → 開く・閉じるの規則
//    - 「削除」の文字: 構えていない間は元の位置、構えている間は Front の後ろの端（操作の側の端）＋余白に付いて指と一緒に動く。
//      2 つの置き場を重み 0〜1 で補間する（重みの動きは SwipeModel が motion.swipe_full の時間で進める）
//    - 確定: Front を行の幅の外まで流し切る（DismissOffset）。消した行の高さは一覧の持ち主が畳む（CollapsedExtent）
// ============================================================

/// <summary>離したときの行き先（<see cref="SwipeMath.DecideRelease"/>）。</summary>
public enum SwipeRelease
{
    /// <summary>閉じる。</summary>
    Close = 0,
    /// <summary>開く（操作のボタンを見せたまま）。</summary>
    Open = 1,
    /// <summary>確定（行を外へ流し切る。フルスワイプ・操作のボタンのタップ）。</summary>
    Commit = 2,
}

/// <summary>行のスワイプの操作の計算。</summary>
public static class SwipeMath
{
    /// <summary>開くとみなすずらし量の割合の既定（ItemTouchHelper.getSwipeThreshold = 0.5）。</summary>
    public const float DefaultOpenThreshold = 0.5f;
    /// <summary>速さで決める閾値の既定（dp/秒。androidx の item_touch_helper_swipe_escape_velocity = 120dp）。</summary>
    public const float DefaultEscapeVelocityDp = 120f;
    /// <summary>開く・閉じるの動きの時間の既定（秒。ItemTouchHelper.DEFAULT_SWIPE_ANIMATION_DURATION = 250ms）。</summary>
    public const float DefaultSettleSeconds = 0.25f;

    // ── フルスワイプの既定（W2 の手直し P2-3。テーマのトークンが無いときの値。default_theme.json と同じ値にそろえる）──
    /// <summary>構える割合の既定（行の幅に対する。ratio.swipe_full。docs/backlog.md の案。iOS の値は公開されていないので決めた値）。</summary>
    public const float DefaultFullSwipeRatio = 0.6f;
    /// <summary>構えた後に解く割合の既定（ratio.swipe_full_cancel。構える割合より小さくしてばたつかない。backlog の案）。</summary>
    public const float DefaultFullSwipeCancelRatio = 0.55f;
    /// <summary>「削除」の文字の置き場の補間の時間の既定（秒。motion.swipe_full。backlog の案 0.15 秒）。</summary>
    public const float DefaultFullSwipeLabelSeconds = 0.15f;
    /// <summary>確定で行を外へ流し切る時間の既定（秒。motion.swipe_dismiss。backlog の案 0.2 秒）。</summary>
    public const float DefaultDismissSeconds = 0.2f;
    /// <summary>消した行の高さを畳む時間の既定（秒。motion.swipe_collapse。一覧の持ち主が使う。backlog の案 0.2 秒）。</summary>
    public const float DefaultCollapseSeconds = 0.2f;

    /// <summary>動きの曲線の制御点（Material fastOutSlowIn = Flutter Curves.fastOutSlowIn）。</summary>
    private static readonly float[] SettleCurve = { 0.4f, 0.0f, 0.2f, 1.0f };
    /// <summary>3 次ベジェの逆算の許容誤差（Flutter Cubic._cubicErrorBound）。</summary>
    private const float CubicErrorBound = 0.001f;
    /// <summary>二分法の上限の回数（無限の繰り返しの保護）。</summary>
    private const int MaxBisectionSteps = 64;

    /// <summary>ドラッグの移動を当てた後のずらし量（0 とドラッグの端の間へ収める）。</summary>
    /// <param name="offset">今のずらし量。</param>
    /// <param name="delta">指の横の移動（キャンバスの単位）。</param>
    /// <param name="limit">ドラッグの端（開いた量、フルスワイプなら <see cref="DragLimit"/>。右側の操作なら負）。</param>
    public static float ApplyDrag(float offset, float delta, float limit)
    {
        float next = offset + (float.IsFinite(delta) ? delta : 0f);
        float lo = MathF.Min(0f, limit);
        float hi = MathF.Max(0f, limit);
        return Math.Clamp(next, lo, hi);
    }

    /// <summary>離したときに開くか（冒頭の規則）。</summary>
    /// <param name="offset">今のずらし量。</param>
    /// <param name="velocityDp">離したときの横の速さ（dp/秒。右向きが正）。</param>
    /// <param name="openOffset">開いたときのずらし量（右側の操作なら負）。</param>
    /// <param name="threshold">開くとみなすずらし量の割合（0〜1）。</param>
    /// <param name="escapeVelocityDp">速さで決める閾値（dp/秒）。</param>
    public static bool ShouldOpen(float offset, float velocityDp, float openOffset, float threshold, float escapeVelocityDp)
    {
        if (openOffset == 0f) return false;
        float direction = MathF.Sign(openOffset);
        if (float.IsFinite(velocityDp) && MathF.Abs(velocityDp) >= escapeVelocityDp)
        {
            return MathF.Sign(velocityDp) == direction;
        }
        return MathF.Sign(offset) == direction && MathF.Abs(offset) >= Math.Clamp(threshold, 0f, 1f) * MathF.Abs(openOffset);
    }

    /// <summary>動きの曲線（fastOutSlowIn）。t は時間の割合（0〜1）。</summary>
    public static float Ease(float t)
    {
        if (!(t > 0f)) return 0f;
        if (t >= 1f) return 1f;
        float a = SettleCurve[0], b = SettleCurve[1], c = SettleCurve[2], d = SettleCurve[3];
        float start = 0f, end = 1f, mid = 0.5f;
        for (int i = 0; i < MaxBisectionSteps; i++)
        {
            mid = (start + end) / 2f;
            float estimate = Evaluate(a, c, mid);
            if (MathF.Abs(t - estimate) < CubicErrorBound) break;
            if (estimate < t) start = mid; else end = mid;
        }
        return Evaluate(b, d, mid);
    }

    // ── フルスワイプ（W2 の手直し P2-3）──────────────────────────────

    /// <summary>行の幅として使える値か（有限の正の数。0 以下・NaN・無限はフルスワイプをしない）。</summary>
    public static bool IsUsableExtent(float rowExtent) => float.IsFinite(rowExtent) && rowExtent > 0f;

    /// <summary>
    /// ドラッグの端（ずらせるいちばん先。操作の側の符号つき）。フルスワイプが有効で行の幅が使えれば、操作の側へ行の幅いっぱい
    /// （開いた量の方が大きければ開いた量）、それ以外は開いた量（W2-3 の範囲のまま）。
    /// </summary>
    /// <param name="openOffset">開いたときのずらし量（右側の操作なら負）。</param>
    /// <param name="direction">操作の側へずらす向き（右側の操作 = −1・左側 = +1）。</param>
    /// <param name="rowExtent">行の幅。</param>
    /// <param name="fullSwipe">フルスワイプが有効か。</param>
    public static float DragLimit(float openOffset, float direction, float rowExtent, bool fullSwipe)
    {
        if (!fullSwipe || !IsUsableExtent(rowExtent) || direction == 0f) return openOffset;
        return MathF.Sign(direction) * MathF.Max(MathF.Abs(openOffset), rowExtent);
    }

    /// <summary>構えるずらし量（操作の側への大きさ）= max(行の幅 × 構える割合, 開いた量)。行の幅が使えなければ無限（構えない）。</summary>
    public static float ArmDistance(float openOffset, float rowExtent, float armRatio)
    {
        if (!IsUsableExtent(rowExtent)) return float.PositiveInfinity;
        float ratio = float.IsFinite(armRatio) ? Math.Clamp(armRatio, 0f, 1f) : DefaultFullSwipeRatio;
        return MathF.Max(rowExtent * ratio, MathF.Abs(openOffset));
    }

    /// <summary>
    /// 構えた後に解くずらし量 = min(行の幅 × 解く割合, 構えるずらし量)（この値を下回ったら解く）。
    /// 解く割合を構える割合より大きくしたテーマでも、構えるずらし量を超えない（ヒステリシスが無くなるだけで矛盾しない）。
    /// </summary>
    public static float CancelDistance(float openOffset, float rowExtent, float armRatio, float cancelRatio)
    {
        float arm = ArmDistance(openOffset, rowExtent, armRatio);
        if (!float.IsFinite(arm)) return arm;
        float ratio = float.IsFinite(cancelRatio) ? Math.Clamp(cancelRatio, 0f, 1f) : DefaultFullSwipeCancelRatio;
        return MathF.Min(rowExtent * ratio, arm);
    }

    /// <summary>
    /// 構えの次の状態（ヒステリシス）: 構えていなければ「構えるずらし量」以上で構え、構えていれば「解くずらし量」を下回るまで構えたまま。
    /// 行の幅が使えないとき・操作の側と逆へずらしているときは構えない。
    /// </summary>
    /// <param name="armed">今構えているか。</param>
    /// <param name="offset">今のずらし量。</param>
    /// <param name="direction">操作の側へずらす向き（右側の操作 = −1・左側 = +1）。</param>
    /// <param name="openOffset">開いたときのずらし量。</param>
    /// <param name="rowExtent">行の幅。</param>
    /// <param name="armRatio">構える割合。</param>
    /// <param name="cancelRatio">解く割合。</param>
    public static bool UpdateArmed(bool armed, float offset, float direction, float openOffset, float rowExtent, float armRatio, float cancelRatio)
    {
        if (!IsUsableExtent(rowExtent) || direction == 0f || !float.IsFinite(offset)) return false;
        float distance = offset * MathF.Sign(direction);
        return armed
            ? distance >= CancelDistance(openOffset, rowExtent, armRatio, cancelRatio)
            : distance >= ArmDistance(openOffset, rowExtent, armRatio);
    }

    /// <summary>
    /// 離したときの行き先（冒頭の規則）: 取り消し → 元の状態（開いていたなら開く）、構えている → 確定（閉じる向きへ逃げの速さ以上で
    /// 払ったときは除く）、それ以外 → <see cref="ShouldOpen"/>。
    /// </summary>
    /// <param name="armed">構えているか（フルスワイプが有効なときだけ true を渡す）。</param>
    /// <param name="canceled">取り消し（OS の取り消し・行の使い回し）で終わったか。</param>
    /// <param name="wasOpen">ドラッグの前に開いていたか。</param>
    /// <param name="offset">今のずらし量。</param>
    /// <param name="velocityDp">離したときの横の速さ（dp/秒。右向きが正）。</param>
    /// <param name="openOffset">開いたときのずらし量。</param>
    /// <param name="threshold">開くとみなすずらし量の割合。</param>
    /// <param name="escapeVelocityDp">速さで決める閾値（dp/秒）。</param>
    /// <param name="direction">操作の側へずらす向き（右側の操作 = −1・左側 = +1）。</param>
    public static SwipeRelease DecideRelease(bool armed, bool canceled, bool wasOpen, float offset, float velocityDp,
        float openOffset, float threshold, float escapeVelocityDp, float direction)
    {
        if (canceled) return wasOpen ? SwipeRelease.Open : SwipeRelease.Close;
        if (armed)
        {
            bool escapesToClose = float.IsFinite(velocityDp) && MathF.Abs(velocityDp) >= escapeVelocityDp
                                  && MathF.Sign(velocityDp) == -MathF.Sign(direction);
            if (!escapesToClose) return SwipeRelease.Commit;
        }
        return ShouldOpen(offset, velocityDp, openOffset, threshold, escapeVelocityDp) ? SwipeRelease.Open : SwipeRelease.Close;
    }

    /// <summary>確定で Front を流し切る先のずらし量（行の幅の外 = 操作の側へ行の幅。幅が分からなければ開いた量）。</summary>
    public static float DismissOffset(float direction, float rowExtent, float openOffset)
        => IsUsableExtent(rowExtent) && direction != 0f ? MathF.Sign(direction) * rowExtent : openOffset;

    /// <summary>
    /// 文字のノードの元の置き場の左の端（行の座標。文字の親は行の左の端から行の幅いっぱいに置く前提）。
    /// エンジンの置き方: 左の端 = anchor.x × 親の幅 + position.x − pivot.x × 文字の幅（Scale 1）。
    /// </summary>
    /// <param name="anchorX">文字の anchor の x（0 = 親の左・1 = 親の右）。</param>
    /// <param name="parentWidth">親の幅（= 行の幅）。</param>
    /// <param name="positionX">文字の元の position の x。</param>
    /// <param name="pivotX">文字の pivot の x。</param>
    /// <param name="width">文字の矩形の幅（Text の枠・Sprite の幅。無ければ 0 = pivot の点）。</param>
    public static float LabelRestLeft(float anchorX, float parentWidth, float positionX, float pivotX, float width)
        => anchorX * parentWidth + positionX - pivotX * width;

    /// <summary>
    /// 構えたときの文字の左の端（行の座標）: Front の後ろの端（操作の側の端）から余白だけ操作の側へ。
    /// 右側の操作（direction −1）: Front の右の端 = 行の幅 + ずらし量 → 文字の左の端 = そこ + 余白。
    /// 左側の操作（direction +1）: Front の左の端 = ずらし量 → 文字の右の端 = そこ − 余白。
    /// </summary>
    public static float LabelArmedLeft(float offset, float direction, float rowExtent, float labelWidth, float margin)
        => direction < 0f
            ? rowExtent + offset + margin
            : offset - margin - labelWidth;

    /// <summary>
    /// 文字のノードを元の置き場から動かす量（行の座標の x）= (構えたときの左の端 − 元の左の端) × 重み。
    /// 重み 0 = 元の位置・1 = Front の後ろの端に付く（ずらし量と一緒に動く）。行の幅が使えなければ 0（動かさない）。
    /// </summary>
    /// <param name="weight">重み（0〜1。範囲の外は収める・NaN は 0）。</param>
    /// <param name="offset">今のずらし量。</param>
    /// <param name="direction">操作の側へずらす向き。</param>
    /// <param name="rowExtent">行の幅。</param>
    /// <param name="restLeft">元の左の端（<see cref="LabelRestLeft"/>）。</param>
    /// <param name="labelWidth">文字の矩形の幅。</param>
    /// <param name="margin">Front の後ろの端と文字の間の余白。</param>
    public static float LabelShift(float weight, float offset, float direction, float rowExtent, float restLeft, float labelWidth, float margin)
    {
        if (!IsUsableExtent(rowExtent) || !(weight > 0f)) return 0f;
        float w = MathF.Min(weight, 1f);
        return (LabelArmedLeft(offset, direction, rowExtent, labelWidth, margin) - restLeft) * w;
    }

    /// <summary>
    /// 消した行を畳む途中の行の長さ（一覧の持ち主が ListView.SetExtentOf で使う）= 元の長さ × (1 − fastOutSlowIn(経過 ÷ 時間))。
    /// 時間が 0 以下ならすぐ 0。
    /// </summary>
    /// <param name="fullExtent">元の行の長さ。</param>
    /// <param name="elapsed">畳み始めてからの秒。</param>
    /// <param name="duration">畳む時間（秒。motion.swipe_collapse）。</param>
    public static float CollapsedExtent(float fullExtent, float elapsed, float duration)
    {
        if (!(duration > 0f)) return 0f;
        float t = Math.Clamp(float.IsFinite(elapsed) ? elapsed / duration : 1f, 0f, 1f);
        return MathF.Max(0f, fullExtent) * (1f - Ease(t));
    }

    /// <summary>3 次ベジェの 1 成分（端点 0 と 1、制御点 a・b、媒介変数 m）。</summary>
    private static float Evaluate(float a, float b, float m)
        => 3f * a * (1f - m) * (1f - m) * m + 3f * b * (1f - m) * m * m + m * m * m;
}
