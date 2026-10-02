using System;

namespace SEED.UI;

// ============================================================
//  ParkedPlaneLayers.cs — 面（覆い・ポップアップ）を画面のスタックの画面の下へ回すときのレイヤーの底上げ
//                         （ModalHost.Park。2026-10-02。lane3。純粋な計算。docs/ui_navigation.md §3.7。backlog W3-7 (2)）
//
//  面の帯（layer.overlay 100 万〜）は画面のスタックの段（段 i の枠 = i × 段の値。既定 1 万）より常に手前にある。覆いを閉じずに
//  全画面を積む（オプションの覆いから設定の画面へ・プロフィールのポップアップから編集の画面へ）ときは、その間だけ面の根の底上げを書き換え、
//  全画面の枠より奥・その下の画面より手前へ置く: 実効の底上げ = 全画面の枠の実効の底上げ − 段の値 × DepthFraction（半段）。
//  下の画面の中の表示（タブのスタックの段 1,000 ほど・遮る板）は段の値の半分より小さい前提（§6「画面の中の表示のレイヤーは段の値より小さく」）。
//  Wake or Pay の ParkedLayer（W3-7）と同じ決め方。
// ============================================================

/// <summary>画面の下へ回す面の底上げの計算。</summary>
public static class ParkedPlaneLayers
{
    /// <summary>全画面の枠からどれだけ奥に置くか（段の値に対する割合。0.5 = 半段）。</summary>
    public const float DepthFraction = 0.5f;

    /// <summary>
    /// 段 <paramref name="pageIndex"/> の画面の枠の実効の底上げ（スタックの枠を積む子〈Screens〉の実効の底上げ ＋ 段 × 段の値。
    /// ScreenStack が枠に当てる値〈UiLayers.ScreenBias〉と同じ決め方）。
    /// </summary>
    /// <param name="screensEffectiveBias">枠を積む子の実効の底上げ（祖先から足し合わせた値）。</param>
    /// <param name="pageIndex">画面の段（根 = 0。負なら 0）。</param>
    /// <param name="stackStep">1 段の底上げ（0 以下なら 0）。</param>
    /// <returns>実効の底上げ。</returns>
    public static int PageFrameBias(int screensEffectiveBias, int pageIndex, int stackStep) =>
        screensEffectiveBias + UiLayers.ScreenBias(pageIndex, stackStep);

    /// <summary>面を置く実効の底上げ（画面の枠の実効の底上げ − 段の値 × <see cref="DepthFraction"/>）。</summary>
    /// <param name="pageFrameEffectiveBias">画面の枠の実効の底上げ。</param>
    /// <param name="stackStep">1 段の底上げ（0 以下なら 0）。</param>
    /// <returns>実効の底上げ。</returns>
    public static int EffectiveBias(int pageFrameEffectiveBias, int stackStep) =>
        pageFrameEffectiveBias - (int)MathF.Round(Math.Max(0, stackStep) * DepthFraction);

    /// <summary>
    /// 面の根に書く底上げ（面の祖先〈帯・ModalHost〉の底上げを差し引いた値。実効 = 戻り値 ＋ 祖先 = <see cref="EffectiveBias"/>）。
    /// </summary>
    /// <param name="pageFrameEffectiveBias">画面の枠の実効の底上げ。</param>
    /// <param name="stackStep">1 段の底上げ。</param>
    /// <param name="planeAncestorsBias">面の根の祖先の底上げの和。</param>
    /// <returns>面の根の底上げ（底上げの範囲 ±UiLayers.MaxBias へ収める）。</returns>
    public static int PlaneBias(int pageFrameEffectiveBias, int stackStep, int planeAncestorsBias)
    {
        long own = (long)EffectiveBias(pageFrameEffectiveBias, stackStep) - planeAncestorsBias;
        return (int)Math.Clamp(own, -UiLayers.MaxBias, UiLayers.MaxBias);
    }
}
