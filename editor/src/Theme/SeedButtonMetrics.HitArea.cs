// ============================================================
//  SeedButtonMetrics.HitArea.cs — 小さなアイコンボタンの当たり判定
//
//  【役割】
//  「×」や「∧∨」のような小さなアイコンボタンで、
//  *見た目のアイコンは小さいまま* 押せる範囲だけを広げるための寸法を決める。
//
//  【なぜ必要か】
//  10〜12px のアイコンへ直接マウスハンドラを付けると、当たり判定が
//  アイコンの絵の大きさしかなく、狙って押しづらい（利用者からの指摘）。
//  かといってアイコンを大きくすると行やタブの見た目が変わってしまうため、
//  「アイコンの大きさ × 一定倍率（下限つき）」の正方形を押せる範囲として確保し、
//  アイコンはその中央に置く、という方式に統一する。
//
//  【なぜファイルを分けてあるか】
//  SeedButtonMetrics.cs は Thickness / CornerRadius（WPF 型）を使うため、
//  WPF を持たないテストプロジェクトへリンクできない。
//  当たり判定の算出は純粋な計算なので、ここへ切り出して
//  editor/tests/ThemeContrastTests から直接検査できるようにしている。
//  partial の片割れであり、利用側からは SeedButtonMetrics 1 つに見える。
// ============================================================

using System;

namespace SEEDEditor.Theme;

public static partial class SeedButtonMetrics
{
    /// <summary>
    /// 小さなアイコンボタンの当たり判定を、アイコン一辺の何倍にするか。
    /// 「今の 1.5 倍ぐらい押しやすく」という指摘に合わせた値。
    /// </summary>
    public const double ICON_HIT_AREA_SCALE = 1.5;

    /// <summary>
    /// 当たり判定の一辺の下限 [px]。
    /// 9px のような特に小さなアイコンだと 1.5 倍でも 14px にしかならず
    /// 押しやすさが足りないため、下限を別に設ける。
    /// </summary>
    public const double ICON_HIT_AREA_MIN_PX = 18;

    /// <summary>
    /// アイコン一辺のサイズから、ボタンとして確保する当たり判定の一辺を求める。
    ///
    /// <para>
    /// 計算式は <c>max(ceil(アイコン一辺 × <see cref="ICON_HIT_AREA_SCALE"/>),
    /// <see cref="ICON_HIT_AREA_MIN_PX"/>)</c>。
    /// 端数を切り上げるのは、WPF の論理ピクセルが小数を持てるせいで
    /// 17.5px のような値になると描画位置が半端になり、
    /// 枠やホバー背景がにじんで見えるため。
    /// </para>
    /// </summary>
    /// <param name="iconSizePx">
    /// アイコンの一辺 [px]。0 以下・NaN・無限大（サイズ未設定の
    /// <c>double.NaN</c> がそのまま渡ってくる事故）のときは下限値を返し、
    /// 「押せない大きさ 0 のボタン」を作らない。
    /// </param>
    /// <returns>ボタンへ与える当たり判定の一辺 [px]（正方形）。</returns>
    public static double IconHitAreaSize(double iconSizePx)
    {
        if (double.IsNaN(iconSizePx) || double.IsInfinity(iconSizePx) || iconSizePx <= 0)
            return ICON_HIT_AREA_MIN_PX;

        return Math.Max(Math.Ceiling(iconSizePx * ICON_HIT_AREA_SCALE), ICON_HIT_AREA_MIN_PX);
    }
}
