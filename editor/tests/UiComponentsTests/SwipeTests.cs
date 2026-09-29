using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using SEED.UI;
using SpriteRigTests; // テストランナー（TestHarness / Check）を共有する

namespace UiComponentsTests;

/// <summary>
/// 行のフルスワイプで削除（W2 の手直し P2-3。docs/ui_scroll_list.md §7）の純粋な計算と状態の移り変わりのテスト:
/// 構える・解くのヒステリシス（SwipeMath.UpdateArmed）、行の幅が使えないとき、ドラッグの範囲（有効・無効）、離したときの決め方
/// （SwipeMath.DecideRelease）、「削除」の文字の置き場の補間（重み 0・0.5・1、構えている間は指に付く）、状態の機械（SwipeModel）の
/// 列（越える → 戻る → 越える → 離す＝確定、越える → 戻る → 離す＝開く・閉じる、取り消し、ボタンのタップの確定）と構えた回数＝触感の回数、
/// 消した行を畳む長さ、既定のテーマの値、行のプレハブ（templates/ui/prefabs/list_row.actor）の作り。
/// </summary>
public static class SwipeTests
{
    /// <summary>浮動小数の比較の許容量。</summary>
    private const double Eps = 1e-3;
    /// <summary>行の幅（ギャラリーの一覧の 500 dp）。</summary>
    private const float RowWidth = 500f;
    /// <summary>操作のボタンの幅（ギャラリーの行の削除のボタン 96 dp）。</summary>
    private const float ActionsWidth = 96f;
    /// <summary>右側の操作の開いた量（左へ 96）。</summary>
    private const float OpenRight = -ActionsWidth;
    /// <summary>右側の操作のずらす向き。</summary>
    private const float Left = -1f;
    /// <summary>文字の余白（space.l の既定 16）。</summary>
    private const float Margin = 16f;
    /// <summary>文字の矩形の幅（list_row.actor の「削除」の枠 40）。</summary>
    private const float LabelWidth = 40f;
    /// <summary>行の高さ（list_row.actor の 56）。</summary>
    private const float RowHeight = 56f;

    /// <summary>構える割合・解く割合の既定。</summary>
    private static readonly float ArmRatio = SwipeMath.DefaultFullSwipeRatio, CancelRatio = SwipeMath.DefaultFullSwipeCancelRatio;

    /// <summary>ギャラリーと同じ設定の状態の機械（右側の操作 96・フルスワイプ・行の幅 500・時間は既定）。</summary>
    private static SwipeModel NewModel() => new(SwipeSide.Right, ActionsWidth) { FullSwipe = true, RowExtent = RowWidth };

    /// <summary>行の幅の割合だけ左へずらしたときの構えの次の状態。</summary>
    private static bool ArmedAt(bool armed, float ratio)
        => SwipeMath.UpdateArmed(armed, -ratio * RowWidth, Left, OpenRight, RowWidth, ArmRatio, CancelRatio);

    public static void Register(TestHarness h, UiThemeData theme)
    {
        // ── 構える・解く ───────────────────────────────────────
        h.Add("P2-3 フルスワイプ: 構える・解くのヒステリシス（0.59 → 構えない・0.6 → 構える・0.57 → 構えたまま・0.54 → 解く）", () =>
        {
            Check.True(!ArmedAt(false, 0.59f), "0.59 は構えない");
            Check.True(ArmedAt(false, 0.6f), "0.6 で構える");
            Check.True(ArmedAt(true, 0.57f), "構えた後に 0.57 へ戻しても構えたまま");
            Check.True(ArmedAt(true, 0.55f), "0.55 ちょうどは構えたまま（下回ったら解く）");
            Check.True(!ArmedAt(true, 0.54f), "0.54 で解く");
            Check.True(!ArmedAt(false, 0.57f), "解いた後の 0.57 は構えない（0.6 まで払い直す）");
            Check.Close(300, SwipeMath.ArmDistance(OpenRight, RowWidth, ArmRatio), Eps, "構えるずらし量 = 500 × 0.6");
            Check.Close(275, SwipeMath.CancelDistance(OpenRight, RowWidth, ArmRatio, CancelRatio), Eps, "解くずらし量 = 500 × 0.55");
            Check.True(!SwipeMath.UpdateArmed(false, 0.7f * RowWidth, Left, OpenRight, RowWidth, ArmRatio, CancelRatio), "操作と逆へずらしても構えない");
            Check.True(SwipeMath.UpdateArmed(false, 0.6f * RowWidth, 1f, ActionsWidth, RowWidth, ArmRatio, CancelRatio), "左側の操作は右へ 0.6 で構える");
        });

        h.Add("P2-3 フルスワイプ: 行の幅 0・負・NaN・無限は構えず、ドラッグの範囲は開いた量のまま・文字も動かさない", () =>
        {
            foreach (float bad in new[] { 0f, -10f, float.NaN, float.PositiveInfinity })
            {
                Check.True(!SwipeMath.UpdateArmed(false, -1000f, Left, OpenRight, bad, ArmRatio, CancelRatio), $"行の幅 {bad} は構えない");
                Check.True(!SwipeMath.UpdateArmed(true, -1000f, Left, OpenRight, bad, ArmRatio, CancelRatio), $"行の幅 {bad} は構えたままにしない");
                Check.Close(OpenRight, SwipeMath.DragLimit(OpenRight, Left, bad, true), Eps, $"行の幅 {bad} のドラッグの端は開いた量");
                Check.Close(0, SwipeMath.LabelShift(1f, -300f, Left, bad, 432f, LabelWidth, Margin), Eps, $"行の幅 {bad} では文字を動かさない");
                Check.Close(OpenRight, SwipeMath.DismissOffset(Left, bad, OpenRight), Eps, $"行の幅 {bad} の流し切る先は開いた量");
            }
            Check.Close(-RowWidth, SwipeMath.DragLimit(OpenRight, Left, RowWidth, true), Eps, "有効: 左へ行の幅いっぱいまで引ける");
            Check.Close(OpenRight, SwipeMath.DragLimit(OpenRight, Left, RowWidth, false), Eps, "無効: 開いた量のまま（W2-3）");
            Check.Close(RowWidth, SwipeMath.DragLimit(ActionsWidth, 1f, RowWidth, true), Eps, "左側の操作: 右へ行の幅いっぱい");
            Check.Close(-RowWidth, SwipeMath.DragLimit(0f, Left, RowWidth, true), Eps, "操作のボタンが無くても（開いた量 0）フルスワイプの範囲は取れる");
            float limit = SwipeMath.DragLimit(OpenRight, Left, RowWidth, true);
            Check.Close(-RowWidth, SwipeMath.ApplyDrag(-450f, -200f, limit), Eps, "行の幅より先へは引けない");
            Check.Close(0, SwipeMath.ApplyDrag(-50f, 200f, limit), Eps, "閉じた 0 より右へは行かない");
            Check.Close(-RowWidth, SwipeMath.DismissOffset(Left, RowWidth, OpenRight), Eps, "流し切る先は行の幅の外");
        });

        h.Add("P2-3 フルスワイプ: 狭い行でもボタンの幅より手前では構えない・解く割合が大きいテーマ・範囲の外の割合", () =>
        {
            const float narrow = 120f;
            Check.Close(ActionsWidth, SwipeMath.ArmDistance(OpenRight, narrow, ArmRatio), Eps, "0.6 × 120 = 72 はボタンの幅 96 より手前 → 96 から構える");
            Check.True(!SwipeMath.UpdateArmed(false, -80f, Left, OpenRight, narrow, ArmRatio, CancelRatio), "ボタンを見せるだけの払いは構えない");
            Check.True(SwipeMath.UpdateArmed(false, OpenRight, Left, OpenRight, narrow, ArmRatio, CancelRatio), "開いた量で構える");
            Check.Close(66, SwipeMath.CancelDistance(OpenRight, narrow, ArmRatio, CancelRatio), Eps, "解くのは 0.55 × 120 = 66");
            Check.Close(300, SwipeMath.CancelDistance(OpenRight, RowWidth, 0.6f, 0.8f), Eps, "解く割合 > 構える割合でも、解くずらし量は構えるずらし量を超えない");
            Check.Close(RowWidth, SwipeMath.ArmDistance(OpenRight, RowWidth, 1.5f), Eps, "割合は 0..1 へ収める");
            Check.Close(300, SwipeMath.ArmDistance(OpenRight, RowWidth, float.NaN), Eps, "NaN の割合は既定 0.6");
        });

        // ── 離したときの決め方 ─────────────────────────────────
        h.Add("P2-3 フルスワイプ: 離したときの決め方（構えている・ボタンより先・速い払い・遅い払い・取り消し）", () =>
        {
            SwipeRelease D(bool armed, bool canceled, bool wasOpen, float offset, float velocityDp)
                => SwipeMath.DecideRelease(armed, canceled, wasOpen, offset, velocityDp, OpenRight,
                    SwipeMath.DefaultOpenThreshold, SwipeMath.DefaultEscapeVelocityDp, Left);
            Check.Equal(SwipeRelease.Commit, D(true, false, false, -320f, 0f), "構えたまま止めて離す → 確定");
            Check.Equal(SwipeRelease.Commit, D(true, false, false, -320f, -900f), "構えたまま左へ払う → 確定");
            Check.Equal(SwipeRelease.Commit, D(true, false, false, -320f, 100f), "右へ遅く（逃げの速さ 120 の手前）離す → 確定");
            Check.Equal(SwipeRelease.Close, D(true, false, false, -320f, 600f), "構えていても右へ速く払えば確定しない（閉じる）");
            Check.Equal(SwipeRelease.Open, D(false, false, false, -200f, 0f), "構えていない・ボタンより先で遅く離す → 開く");
            Check.Equal(SwipeRelease.Open, D(false, false, false, -60f, -20f), "ボタンの半分を越えて遅く離す → 開く");
            Check.Equal(SwipeRelease.Close, D(false, false, false, -40f, -20f), "半分に届かない → 閉じる");
            Check.Equal(SwipeRelease.Open, D(false, false, false, -20f, -500f), "左へ速い払い → 開く（確定にはしない）");
            Check.Equal(SwipeRelease.Close, D(false, false, true, -200f, 500f), "右へ速い払い → 閉じる");
            Check.Equal(SwipeRelease.Close, D(true, true, false, -320f, 0f), "取り消し（閉じていた行）→ 閉じる（確定しない）");
            Check.Equal(SwipeRelease.Open, D(true, true, true, -320f, 0f), "取り消し（開いていた行）→ 開いた状態へ");
        });

        // ── 文字の置き場 ────────────────────────────────────────
        h.Add("P2-3 フルスワイプ: 「削除」の文字の置き場（重み 0・0.5・1、構えている間は指に付く、流し切ると行の左の端、左側の操作）", () =>
        {
            // 右の端に寄せた文字（anchor 1・pivot 0.5・position −48・幅 40）→ 元の左の端 = 500 − 48 − 20 = 432（ボタンの枠 404..500 の真ん中）
            float rest = SwipeMath.LabelRestLeft(1f, RowWidth, -ActionsWidth / 2f, 0.5f, LabelWidth);
            Check.Close(432, rest, Eps, "元の左の端");
            Check.Close(216, SwipeMath.LabelArmedLeft(-300f, Left, RowWidth, LabelWidth, Margin), Eps, "構えたとき: Front の右の端 200 + 余白 16");
            float Shift(float weight, float offset) => SwipeMath.LabelShift(weight, offset, Left, RowWidth, rest, LabelWidth, Margin);
            Check.Close(0, Shift(0f, -300f), Eps, "重み 0 は元の位置");
            Check.Close(216 - 432, Shift(1f, -300f), Eps, "重み 1 は Front の後ろの端に付く");
            Check.Close((216 - 432) * 0.5, Shift(0.5f, -300f), Eps, "重み 0.5 は 2 つの置き場の間");
            Check.Close(-50, Shift(1f, -350f) - Shift(1f, -300f), Eps, "構えている間は指に付く（ずらし量と同じだけ動く）");
            Check.Close(0, Shift(float.NaN, -300f), Eps, "重み NaN は元の位置");
            Check.Close(216 - 432, Shift(2f, -300f), Eps, "重みは 1 まで");
            Check.Close(Margin, rest + Shift(1f, -RowWidth), Eps, "流し切ると（ずらし量 −行の幅）文字は行の左の端 + 余白");
            // 左側の操作（右へずらす）: 文字の右の端 = Front の左の端 − 余白
            float restLeft = SwipeMath.LabelRestLeft(0f, RowWidth, 28f, 0f, LabelWidth);
            Check.Close(300f - Margin - LabelWidth, SwipeMath.LabelArmedLeft(300f, 1f, RowWidth, LabelWidth, Margin), Eps, "左側: 構えたときの左の端");
            Check.Close(300f - Margin - LabelWidth - 28f,
                SwipeMath.LabelShift(1f, 300f, 1f, RowWidth, restLeft, LabelWidth, Margin), Eps, "左側の重み 1");
        });

        // ── 状態の移り変わり ────────────────────────────────────
        h.Add("P2-3 状態: 越える → 戻る → 越える → 離す＝確定（構えた回数 2 = 触感 2 回・流し切り・Reset まで受けない）", () =>
        {
            var m = NewModel();
            Check.Equal(SwipeSignal.None, m.DragStart(), "ドラッグの始まり");
            Check.Equal(SwipeSignal.Armed, m.DragBy(-310f), "0.62 まで → 構える");
            Check.True(m.IsArmed && m.IsDragging, "構えている・ドラッグ中");
            Check.Equal(SwipeSignal.Disarmed, m.DragBy(40f), "0.54 へ戻す → 解く");
            Check.Equal(SwipeSignal.Armed, m.DragBy(-40f), "もう一度越える → 構える");
            Check.Equal(2, m.ArmCount, "構えた回数 = 触感の回数 2");
            Check.Equal(1, m.DisarmCount, "解いた回数 1");
            Check.Equal(SwipeSignal.CommitStarted, m.Release(0f, false), "構えたまま離す → 確定");
            Check.True(m.IsCommitting && !m.IsOpen && !m.IsDragging, "流し切りの途中・開いた行に数えない");
            Check.Equal(SwipeSignal.None, m.Advance(0.1f), "途中");
            Check.True(m.Offset < -310f && m.Offset > -RowWidth, $"外へ流れている途中（{m.Offset}）");
            Check.Equal(SwipeSignal.Dismissed, m.Advance(0.2f), "流し切った");
            Check.Close(-RowWidth, m.Offset, Eps, "行の幅の外（Front の右の端が行の左の端）");
            Check.Close(1, m.LabelWeight, Eps, "文字は Front の後ろの端に付いたまま");
            Check.True(m.IsDismissed && m.IsCommitted, "流し切った");
            Check.Equal(SwipeSignal.None, m.DragStart(), "Reset まで指を受けない");
            Check.Equal(SwipeSignal.None, m.DragBy(200f), "ずらさない");
            Check.Equal(SwipeSignal.None, m.Release(0f, false), "離しても何も起きない");
            Check.Equal(SwipeSignal.None, m.Commit(), "二重に確定しない");
            Check.Equal(SwipeSignal.None, m.Open(), "開かない");
            Check.Equal(SwipeSignal.None, m.Close(), "閉じない");
            Check.Equal(SwipeSignal.None, m.Advance(1f), "流し切りを 2 度知らせない");
            Check.Close(-RowWidth, m.Offset, Eps, "流し切ったまま");
            Check.Equal(SwipeSignal.None, m.Reset(), "Reset（開いていないので Closed は無い）");
            Check.True(!m.IsCommitted && !m.IsArmed && m.Offset == 0f && m.LabelWeight == 0f, "閉じた状態へ（文字も元の位置）");
            m.DragStart();
            Check.Equal(SwipeSignal.Armed, m.DragBy(-310f), "Reset の後はまた受ける");
            Check.Equal(3, m.ArmCount, "回数は数え続ける");
        });

        h.Add("P2-3 状態: 越える → 戻る → 離す＝開く・閉じるの判定のまま、取り消しは構えを解いて元の状態へ", () =>
        {
            var open = NewModel();
            open.DragStart();
            Check.Equal(SwipeSignal.Armed, open.DragBy(-310f), "越える");
            Check.Equal(SwipeSignal.Disarmed, open.DragBy(100f), "−210 へ戻す → 解く");
            Check.Equal(SwipeSignal.Opened, open.Release(0f, false), "ボタンより先で離す → 開く（W2-3 の規則）");
            open.Advance(0.3f);
            Check.Close(OpenRight, open.Offset, Eps, "開いた量へ戻る");
            Check.True(open.IsOpen && !open.IsCommitted && !open.IsArmed, "開いている・確定していない");
            Check.Equal(1, open.ArmCount, "触感は 1 回");

            var close = NewModel();
            close.DragStart();
            close.DragBy(-310f);
            Check.Equal(SwipeSignal.Disarmed, close.DragBy(270f), "−40 へ戻す → 解く");
            Check.Equal(SwipeSignal.None, close.Release(0f, false), "半分に届かない → 閉じる（元から閉じている）");
            close.Advance(0.3f);
            Check.Close(0, close.Offset, Eps, "閉じた");

            var canceled = NewModel();
            canceled.DragStart();
            canceled.DragBy(-320f);
            Check.Equal(SwipeSignal.Disarmed, canceled.Release(0f, true), "取り消し → 構えを解く（確定しない）");
            canceled.Advance(0.3f);
            Check.True(!canceled.IsCommitted && !canceled.IsArmed && !canceled.IsOpen, "閉じた状態");
            Check.Close(0, canceled.Offset, Eps, "元の閉じた位置");

            var wasOpen = NewModel();
            Check.Equal(SwipeSignal.Opened, wasOpen.Open(animated: false), "開いた行から");
            wasOpen.DragStart();
            Check.Equal(SwipeSignal.Armed, wasOpen.DragBy(-230f), "−326 まで → 構える");
            Check.Equal(SwipeSignal.Disarmed, wasOpen.Release(0f, true), "取り消し → 解いて開いたままへ");
            wasOpen.Advance(0.3f);
            Check.Close(OpenRight, wasOpen.Offset, Eps, "開いた量へ戻る");
            Check.True(wasOpen.IsOpen, "開いたまま");
        });

        h.Add("P2-3 状態: 開いた行のボタンのタップ（Commit）で流し切る・文字は Front の後ろの端へ・触感なし・二重に確定しない", () =>
        {
            var m = NewModel();
            Check.Equal(SwipeSignal.Opened, m.Open(animated: false), "開く");
            Check.Equal(SwipeSignal.CommitStarted, m.Commit(), "タップ → 確定");
            Check.True(!m.IsOpen && m.IsCommitting, "組の開いている行から外れる（Closed は知らせない）");
            Check.Equal(0, m.ArmCount, "タップの確定は構えではない（触感なし）");
            m.Advance(0.1f);
            Check.True(m.LabelWeight > 0f && m.LabelWeight < 1f, $"文字が Front の後ろの端へ動いている途中（{m.LabelWeight}）");
            Check.Equal(SwipeSignal.Dismissed, m.Advance(0.2f), "流し切った");
            Check.Close(-RowWidth, m.Offset, Eps, "行の幅の外");
            Check.Close(1, m.LabelWeight, Eps, "文字は Front の後ろの端");
            Check.Equal(SwipeSignal.None, m.Commit(), "二重に確定しない");
        });

        h.Add("P2-3 状態: フルスワイプ無効は W2-3 のまま（開いた量で止まる・構えない）・行の幅が分からない間は構えない", () =>
        {
            var off = new SwipeModel(SwipeSide.Right, ActionsWidth) { RowExtent = RowWidth };
            off.DragStart();
            Check.Equal(SwipeSignal.None, off.DragBy(-300f), "構えない");
            Check.Close(OpenRight, off.Offset, Eps, "開いた量で止まる");
            Check.Equal(SwipeSignal.Opened, off.Release(0f, false), "離すと開く");
            Check.Equal(0, off.ArmCount, "触感なし");

            var unknown = new SwipeModel(SwipeSide.Right, ActionsWidth) { FullSwipe = true, RowExtent = 0f };
            unknown.DragStart();
            Check.Equal(SwipeSignal.None, unknown.DragBy(-300f), "行の幅が分からない間は構えない");
            Check.Close(OpenRight, unknown.Offset, Eps, "ドラッグは開いた量まで");

            var left = new SwipeModel(SwipeSide.Left, ActionsWidth) { FullSwipe = true, RowExtent = RowWidth };
            left.DragStart();
            Check.Equal(SwipeSignal.Armed, left.DragBy(310f), "左側の操作は右へ払って構える");
            Check.Equal(SwipeSignal.CommitStarted, left.Release(0f, false), "確定");
            left.Advance(1f);
            Check.Close(RowWidth, left.Offset, Eps, "右へ流し切る");
        });

        // ── 畳む ────────────────────────────────────────────────
        h.Add("P2-3 畳む: 消した行の長さ（始めは元の長さ・時間で 0・単調に減る・前半が速い・時間 0 はすぐ 0）", () =>
        {
            const float duration = 0.2f;
            Check.Close(RowHeight, SwipeMath.CollapsedExtent(RowHeight, 0f, duration), Eps, "始め");
            Check.Close(0, SwipeMath.CollapsedExtent(RowHeight, duration, duration), Eps, "終わり");
            Check.Close(0, SwipeMath.CollapsedExtent(RowHeight, 1f, duration), Eps, "過ぎても 0");
            Check.Close(0, SwipeMath.CollapsedExtent(RowHeight, 0f, 0f), Eps, "時間 0 はすぐ 0");
            Check.Close(RowHeight, SwipeMath.CollapsedExtent(RowHeight, -1f, duration), Eps, "負の経過は始め");
            float previous = RowHeight;
            const int steps = 20;
            for (int i = 1; i <= steps; i++)
            {
                float e = SwipeMath.CollapsedExtent(RowHeight, duration * i / steps, duration);
                Check.True(e <= previous + 1e-4f, $"単調に減る（{i}: {e}）");
                previous = e;
            }
            Check.True(SwipeMath.CollapsedExtent(RowHeight, duration / 2f, duration) < RowHeight / 2f, "前半が速い（fastOutSlowIn）");
        });

        // ── 既定のテーマ・プレハブ ──────────────────────────────
        h.Add("P2-3 既定のテーマ: フルスワイプのトークンの値が SwipeMath の既定と同じ・解く < 構える", () =>
        {
            Check.Close(SwipeMath.DefaultFullSwipeRatio, theme.Number(UiTokens.RatioSwipeFull), Eps, "ratio.swipe_full");
            Check.Close(SwipeMath.DefaultFullSwipeCancelRatio, theme.Number(UiTokens.RatioSwipeFullCancel), Eps, "ratio.swipe_full_cancel");
            Check.Close(SwipeMath.DefaultFullSwipeLabelSeconds, theme.Number(UiTokens.MotionSwipeFull), Eps, "motion.swipe_full");
            Check.Close(SwipeMath.DefaultDismissSeconds, theme.Number(UiTokens.MotionSwipeDismiss), Eps, "motion.swipe_dismiss");
            Check.Close(SwipeMath.DefaultCollapseSeconds, theme.Number(UiTokens.MotionSwipeCollapse), Eps, "motion.swipe_collapse");
            Check.True(theme.Number(UiTokens.RatioSwipeFullCancel) < theme.Number(UiTokens.RatioSwipeFull), "解く割合 < 構える割合（ヒステリシス）");
            Check.Close(Margin, theme.Number(UiTokens.SpaceL), Eps, "文字の余白 space.l = 16（SwipeActions.DefaultLabelMargin と同じ）");
        });

        h.Add("P2-3 プレハブ: list_row.actor の作り（行は親の幅に合わせて横のドラッグ・削除の面と文字・遮る板の見た目）", () =>
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "prefabs", "list_row.actor")));
            var row = doc.RootElement;
            Check.True(Data(row, "CanvasLayoutItemComponent").GetProperty("fill_width").GetBoolean(), "行は親の幅に合わせる");
            Check.Close(RowHeight, Data(row, "CanvasComponent").GetProperty("height").GetSingle(), Eps, "行の高さ 56");
            var rowGesture = Data(row, "CanvasGestureComponent");
            Check.True(!rowGesture.GetProperty("tap").GetBoolean() && rowGesture.GetProperty("drag").GetBoolean()
                       && rowGesture.GetProperty("fling").GetBoolean(), "行: タップなし・ドラッグ・フリック");
            Check.Equal("horizontal", rowGesture.GetProperty("drag_axis").GetString(), "行: 横のドラッグ");

            var children = row.GetProperty("children").EnumerateArray().Select(c => c.GetProperty("name").GetString()).ToArray();
            Check.Equal("Actions,Front", string.Join(",", children), "削除の面が先（後ろ）・見た目が後（手前）");

            var actions = Child(row, "Actions");
            var actionsItem = Data(actions, "CanvasLayoutItemComponent");
            Check.True(actionsItem.GetProperty("fill_width").GetBoolean() && actionsItem.GetProperty("fill_height").GetBoolean(), "削除の面は行いっぱい");
            Check.True(HasComponent(actions, "CanvasComponent"), "削除の面は文字の anchor の基準（CanvasComponent）");
            Check.True(Data(actions, "CanvasGestureComponent").GetProperty("tap").GetBoolean(), "削除の面はタップを受ける");
            Check.Equal("color.error", ThemeField(actions, "SpriteColor"), "削除の面の色 = color.error");
            Check.True(ScriptTypes(actions).Contains("SEED.UI.GestureRelay"), "タップを行のスクリプトへ渡す（GestureRelay）");

            var label = Child(actions, "Label");
            var labelTransform = label.GetProperty("canvas_transform");
            Check.Close(1, labelTransform.GetProperty("anchor")[0].GetSingle(), Eps, "文字は右の端に寄せる（anchor 1）");
            Check.Close(-ActionsWidth / 2f, labelTransform.GetProperty("position")[0].GetSingle(), Eps, "文字の中心はボタンの枠（右の端から 96）の真ん中");
            Check.Close(0.5, labelTransform.GetProperty("pivot")[0].GetSingle(), Eps, "文字の pivot は中央");
            Check.Close(LabelWidth, Data(label, "TextComponent").GetProperty("box_width").GetSingle(), Eps, "文字の枠の幅 40");
            Check.Equal("color.on_error", ThemeField(label, "TextColor"), "文字の色 = color.on_error");

            var front = Child(row, "Front");
            Check.True(Data(front, "CanvasLayoutItemComponent").GetProperty("fill_width").GetBoolean(), "見た目は親の幅に合わせる（Translate でずらす）");
            var frontGesture = Data(front, "CanvasGestureComponent");
            Check.True(!frontGesture.GetProperty("tap").GetBoolean(), "見た目は受けるジェスチャーの無い遮る板");
            Check.Equal("color.surface", ThemeField(front, "SpriteColor"), "見た目の面 = color.surface（今の行と同じ）");
            Check.Equal("Title,Sub,Divider",
                string.Join(",", front.GetProperty("children").EnumerateArray().Select(c => c.GetProperty("name").GetString())), "今の行の見た目の子");

            // 同じレイヤーの中では文字がスプライトより手前に描かれる（renderer/ui_draw_order.rs）ので、削除の面の文字が見た目の面の上に
            // 出ないよう、見た目の部分木のいちばん低いレイヤーが削除の面の部分木のいちばん高いレイヤーより上であること
            int frontMin = Layers(front).Min(), actionsMax = Layers(actions).Max();
            Check.True(frontMin > actionsMax, $"見た目（最低 {frontMin}）が削除の面と文字（最高 {actionsMax}）より手前");
        });
    }

    /// <summary>プレハブの JSON の子（名前で引く）。</summary>
    private static JsonElement Child(JsonElement node, string name)
        => node.GetProperty("children").EnumerateArray().First(c => c.GetProperty("name").GetString() == name);

    /// <summary>プレハブの JSON のコンポーネントの data（型の名前で引く）。</summary>
    private static JsonElement Data(JsonElement node, string type)
        => node.GetProperty("components").EnumerateArray()
            .Select(c => c.GetProperty("component"))
            .First(c => c.GetProperty("type").GetString() == type)
            .GetProperty("data");

    /// <summary>ノードと子孫の Sprite・Text の表示のレイヤーの一覧。</summary>
    private static int[] Layers(JsonElement node)
    {
        var layers = node.GetProperty("components").EnumerateArray()
            .Select(c => c.GetProperty("component"))
            .Where(c => c.GetProperty("type").GetString() is "SpriteComponent" or "TextComponent")
            .Select(c => c.GetProperty("data").TryGetProperty("layer", out var l) ? l.GetInt32() : 0)
            .ToList();
        foreach (var child in node.GetProperty("children").EnumerateArray()) layers.AddRange(Layers(child));
        return layers.ToArray();
    }

    /// <summary>その型のコンポーネントがあるか。</summary>
    private static bool HasComponent(JsonElement node, string type)
        => node.GetProperty("components").EnumerateArray().Any(c => c.GetProperty("component").GetProperty("type").GetString() == type);

    /// <summary>スクリプトの型の名前の一覧。</summary>
    private static string[] ScriptTypes(JsonElement node)
        => node.GetProperty("components").EnumerateArray()
            .Select(c => c.GetProperty("component"))
            .Where(c => c.GetProperty("type").GetString() == "ScriptComponent")
            .Select(c => c.GetProperty("data").GetProperty("type_name").GetString() ?? "")
            .ToArray();

    /// <summary>ThemeStyle の欄の値（無ければ空）。</summary>
    private static string ThemeField(JsonElement node, string field)
    {
        foreach (var c in node.GetProperty("components").EnumerateArray().Select(c => c.GetProperty("component")))
        {
            if (c.GetProperty("type").GetString() != "ScriptComponent") continue;
            var data = c.GetProperty("data");
            if (data.GetProperty("type_name").GetString() != "SEED.UI.ThemeStyle") continue;
            return data.TryGetProperty("fields", out var fields) && fields.TryGetProperty(field, out var v) ? v.GetString() ?? "" : "";
        }
        return "";
    }
}
