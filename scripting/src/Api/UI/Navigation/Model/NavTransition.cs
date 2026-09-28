using System;

namespace SEED.UI;

// ============================================================
//  NavTransition.cs — 画面の出入りの種類と、進み具合 → 画面の置き方（W2-7。純粋な計算）
//
//  | 種類   | 進む（積む・置き換える）                                  | 戻る（下ろす）                                   |
//  |--------|-----------------------------------------------------------|--------------------------------------------------|
//  | Push   | 新しい画面が右から入る（x: 1 → 0）。下の画面は左へ視差    | 上の画面が右へ出る（x: 0 → 1）。下の画面が戻る   |
//  | Cover  | 新しい画面が上から降りる（y: −1 → 0）。下の画面は動かない | 上の画面が上へ戻る（y: 0 → −1）                  |
//  | Fade   | 幕（背景の色）が濃くなり、半分で入れ替わり、幕が薄くなる  | 同じ                                             |
//  | None   | すぐ入れ替わる                                            | 同じ                                             |
//  位置は画面の大きさに対する割合（CanvasLayoutItem.TranslateFraction）なので、画面の大きさを知らなくてよい。
//  Group opacity（部分木ごとの透明度）がまだ無いので、フェードは「幕を通して」入れ替える（docs/backlog.md）。
//  時間と曲線はテーマ（motion.push・motion.push_curve など）。NavMotion が引く。
// ============================================================

/// <summary>画面の出入りの種類。</summary>
public enum NavTransition
{
    /// <summary>すぐ入れ替わる（動きなし）。</summary>
    None = 0,
    /// <summary>押し込み（右から入る・右へ出る）。</summary>
    Push = 1,
    /// <summary>覆う（上から降りる・上へ戻る）。</summary>
    Cover = 2,
    /// <summary>フェード（幕を通して入れ替える）。</summary>
    Fade = 3,
}

/// <summary>出入りの向き。</summary>
public enum NavDirection
{
    /// <summary>進む（積む・置き換える。入ってくる画面が上）。</summary>
    Forward = 0,
    /// <summary>戻る（下ろす。出ていく画面が上）。</summary>
    Backward = 1,
}

/// <summary>画面 1 つの置き方（大きさに対するずらしの割合と、見せるか）。</summary>
/// <param name="Fraction">ずらし（大きさに対する割合。x は右、y は下が正）。</param>
/// <param name="Visible">見せるか（フェードの半分より前・後で入れ替える）。</param>
public readonly record struct ScreenPose(Vector2 Fraction, bool Visible);

/// <summary>出入りの途中の 2 つの画面の置き方と幕の濃さ。</summary>
/// <param name="Incoming">終わったときに上にいる画面（進む: 新しい画面 / 戻る: 下から現れる画面）。</param>
/// <param name="Outgoing">終わったときに退く画面（進む: 覆われる画面 / 戻る: 下ろされる画面）。</param>
/// <param name="VeilAlpha">幕の濃さ（0〜1。フェードだけ）。</param>
public readonly record struct TransitionPoses(ScreenPose Incoming, ScreenPose Outgoing, float VeilAlpha);

/// <summary>進み具合 → 画面の置き方。</summary>
public static class TransitionMath
{
    /// <summary>フェードで画面を入れ替える進み具合（幕がいちばん濃いところ）。</summary>
    public const float FadeSwapPoint = 0.5f;

    /// <summary>
    /// 進み具合（曲線を通した後の 0〜1）→ 2 つの画面の置き方。
    /// </summary>
    /// <param name="kind">出入りの種類。</param>
    /// <param name="direction">向き。</param>
    /// <param name="progress">進み具合（0 = 始まり・1 = 終わり。範囲の外は収める）。</param>
    /// <param name="parallax">押し込みで下の画面がずれる割合（ratio.push_parallax）。</param>
    public static TransitionPoses Evaluate(NavTransition kind, NavDirection direction, float progress, float parallax)
    {
        float p = float.IsFinite(progress) ? Math.Clamp(progress, 0f, 1f) : 1f;
        float par = float.IsFinite(parallax) ? Math.Clamp(parallax, 0f, 1f) : 0f;
        var rest = new ScreenPose(Vector2.Zero, true);
        switch (kind)
        {
            case NavTransition.Push:
                return direction == NavDirection.Forward
                    ? new TransitionPoses(new ScreenPose(new Vector2(1f - p, 0f), true), new ScreenPose(new Vector2(-par * p, 0f), true), 0f)
                    : new TransitionPoses(new ScreenPose(new Vector2(-par * (1f - p), 0f), true), new ScreenPose(new Vector2(p, 0f), true), 0f);
            case NavTransition.Cover:
                return direction == NavDirection.Forward
                    ? new TransitionPoses(new ScreenPose(new Vector2(0f, p - 1f), true), rest, 0f)
                    : new TransitionPoses(rest, new ScreenPose(new Vector2(0f, -p), true), 0f);
            case NavTransition.Fade:
            {
                bool swapped = p >= FadeSwapPoint;
                float veil = swapped ? (1f - p) / (1f - FadeSwapPoint) : p / FadeSwapPoint;
                return new TransitionPoses(new ScreenPose(Vector2.Zero, swapped), new ScreenPose(Vector2.Zero, !swapped), Math.Clamp(veil, 0f, 1f));
            }
            default:
                return new TransitionPoses(rest, new ScreenPose(Vector2.Zero, false), 0f);
        }
    }

    /// <summary>動き（時間）を持つ種類か（None 以外）。</summary>
    public static bool IsAnimated(NavTransition kind) => kind != NavTransition.None;
}

/// <summary>出入りの時間と曲線（テーマの motion.* から引く）。</summary>
public static class NavMotion
{
    /// <summary>テーマに無いときの押し込み・覆う画面の時間（秒）。</summary>
    private const float FallbackScreenSeconds = 0.3f;
    /// <summary>テーマに無いときのフェードの時間（秒）。</summary>
    private const float FallbackFadeSeconds = 0.3f;

    /// <summary>出入りの時間（秒。None は 0）。</summary>
    public static float Duration(UiThemeData theme, NavTransition kind) => kind switch
    {
        NavTransition.Push => Math.Max(0f, theme.Number(NavTokens.MotionPush, FallbackScreenSeconds)),
        NavTransition.Cover => Math.Max(0f, theme.Number(NavTokens.MotionCover, FallbackScreenSeconds)),
        NavTransition.Fade => Math.Max(0f, theme.Number(NavTokens.MotionFade, FallbackFadeSeconds)),
        _ => 0f,
    };

    /// <summary>出入りの曲線（テーマに無ければ Material の標準）。</summary>
    public static UiCurve Curve(UiThemeData theme, NavTransition kind) => kind switch
    {
        NavTransition.Push => UiCurve.FromTheme(theme, NavTokens.MotionPushCurve, UiCurve.Standard),
        NavTransition.Cover => UiCurve.FromTheme(theme, NavTokens.MotionCoverCurve, UiCurve.Standard),
        NavTransition.Fade => UiCurve.FromTheme(theme, NavTokens.MotionFadeCurve, UiCurve.FastOutSlowIn),
        _ => UiCurve.Linear,
    };

    /// <summary>押し込みの視差の割合。</summary>
    public static float Parallax(UiThemeData theme) => theme.Number(NavTokens.RatioPushParallax, 0f);
}
