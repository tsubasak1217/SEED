using System;

namespace SEED.UI;

// ============================================================
//  WheelLook.cs — ホイールの行の見た目（曲面の近似）を行の中心からの距離で決める純関数（W2-5。docs/ui_components.md §11）
//
//  【前提】行は平らな一覧として縦に並ぶ（行 i の中心は中身の座標で「窓の高さの半分 + i × 行の高さ」。先頭の余白 =（窓 − 行）/ 2 なので、
//  スクロールの位置 p = i × 行の高さ のとき行 i がちょうど窓の中央に来る＝スナップの間隔の倍数と一致する）。
//  見た目だけを円柱に巻いたように映す。入力は「行の中心が窓の中心からどれだけ離れているか」（平らな距離 d。下が正）。
//
//  【式】（Flutter の RenderListWheelViewport._paintTransformedChild と MatrixUtils.createCylindricalProjectionTransform を
//   2026-09-28 に取得して確かめ、行の中心 1 点で展開したもの）
//      θmax = 直径の比 < 1 ? π/2 : asin(1 / 直径の比)            … 窓の端に当たる角度
//      θ    = (d ÷ (窓の高さ / 2)) × θmax ÷ 詰め具合               … 行の角度（下が正）
//      r    = 窓の高さ × 直径の比 / 2                               … 円柱の半径
//      w    = 1 + 遠近 × r × (1 − cos θ)                             … 遠近の割り算（奥ほど大きい）
//      映る中心 = r sin θ / w                                        … 窓の中心からの見た目の位置
//      縦の倍率 = (cos θ × w − 遠近 × r × sin² θ) / w²              … 映る位置の θ による微分（行の中心での縦の縮み）
//      横の倍率 = 1 / w                                              … 奥ほど細い
//  縦の倍率が 0 になる角度（cos θ = a / (1 + a)。a = 遠近 × r）で映る位置が最も外へ出て、その先は裏側へ回るので描かない。
//  中央の強調（Flutter の拡大鏡と中央の外の暗さの近似）: 帯の中の度合い e = max(0, 1 − |d| ÷ 行の高さ)
//      拡大 = 1 + (拡大の倍率 − 1) × e、濃さ = lerp(帯の外の濃さ, 1, e) × lerp(1, cos θ, 面の傾きの暗さ)
//  エンジンの API に触れない（editor/tests/UiComponentsTests で検算）。
// ============================================================

/// <summary>行 1 つの見た目（<see cref="WheelLook.Resolve"/> の結果）。</summary>
public readonly struct WheelRowLook
{
    /// <summary>描くか（円柱の裏へ回った行・縦の倍率が 0 の行は描かない）。</summary>
    public bool Visible { get; init; }

    /// <summary>行の角度（ラジアン。下が正）。</summary>
    public float Angle { get; init; }

    /// <summary>窓の中心から、行の中心が映る位置までの縦の距離（キャンバスの単位。下が正）。</summary>
    public float Offset { get; init; }

    /// <summary>横の倍率（遠近で奥ほど細い）。</summary>
    public float ScaleX { get; init; }

    /// <summary>縦の倍率（円柱の傾きで縮む）。</summary>
    public float ScaleY { get; init; }

    /// <summary>中央の拡大（1 = 拡大しない）。縦横の倍率に掛ける。</summary>
    public float Magnify { get; init; }

    /// <summary>中央の帯の中の度合い（1 = ちょうど中央・0 = 1 行以上離れた）。</summary>
    public float Emphasis { get; init; }

    /// <summary>濃さ（0..1。文字の色のアルファに掛ける）。</summary>
    public float Opacity { get; init; }

    /// <summary>描かない行。</summary>
    public static WheelRowLook Hidden => new() { Visible = false, ScaleX = 0f, ScaleY = 0f, Magnify = 1f };
}

/// <summary>ホイールの行の見た目（曲面の近似）の純関数。</summary>
public static class WheelLook
{
    /// <summary>直角（ラジアン）。</summary>
    private const float HalfPi = MathF.PI * 0.5f;

    /// <summary>半分。</summary>
    private const float Half = 0.5f;

    /// <summary>長さを 0 とみなす大きさ（0 除算の保護）。</summary>
    private const float Epsilon = 1e-6f;

    /// <summary>逆算（映る位置 → 平らな距離）の二分法の回数（32 回で角度の誤差は 2^-32 × 90° 未満）。</summary>
    private const int InverseIterations = 32;

    /// <summary>
    /// 窓の端に当たる角度（Flutter の <c>_maxVisibleRadian</c>）。直径の比が 1 未満なら ±90°、1 以上なら asin(1 / 比)。
    /// </summary>
    public static float MaxVisibleRadian(float diameterRatio)
        => diameterRatio < 1f ? HalfPi : MathF.Asin(1f / diameterRatio);

    /// <summary>円柱の半径（窓の高さ × 直径の比 / 2）。</summary>
    public static float Radius(float viewportExtent, float diameterRatio) => viewportExtent * diameterRatio * Half;

    /// <summary>平らな距離 d の行の角度（ラジアン。下が正）。</summary>
    /// <param name="distance">行の中心と窓の中心の平らな距離（下が正）。</param>
    /// <param name="viewportExtent">窓の高さ。</param>
    /// <param name="p">見た目の値（<see cref="WheelLookParams.Sanitized"/> 済み）。</param>
    public static float AngleOf(float distance, float viewportExtent, in WheelLookParams p)
    {
        float half = viewportExtent * Half;
        if (half <= Epsilon) return 0f;
        return distance / half * MaxVisibleRadian(p.DiameterRatio) / p.Squeeze;
    }

    /// <summary>
    /// 描ける角度の上限（縦の倍率が 0 になる角度 = 映る位置が最も外へ出る角度。cos θ = a / (1 + a)、a = 遠近 × 半径。遠近 0 なら 90°）。
    /// </summary>
    public static float PeakAngle(float viewportExtent, in WheelLookParams p)
    {
        float a = p.Perspective * Radius(viewportExtent, p.DiameterRatio);
        return a <= Epsilon ? HalfPi : MathF.Acos(a / (1f + a));
    }

    /// <summary>角度 θ の行の中心が映る位置（窓の中心から。下が正）。</summary>
    public static float OffsetAtAngle(float angle, float viewportExtent, in WheelLookParams p)
    {
        float r = Radius(viewportExtent, p.DiameterRatio);
        float w = 1f + p.Perspective * r * (1f - MathF.Cos(angle));
        return r * MathF.Sin(angle) / w;
    }

    /// <summary>
    /// 行の見た目を求める。
    /// </summary>
    /// <param name="distance">行の中心と窓の中心の平らな距離（キャンバスの単位。下が正）。</param>
    /// <param name="viewportExtent">窓の高さ。</param>
    /// <param name="itemExtent">行の高さ（1 行ぶんのスクロールの長さ）。</param>
    /// <param name="parameters">見た目の値（中で <see cref="WheelLookParams.Sanitized"/> する）。</param>
    public static WheelRowLook Resolve(float distance, float viewportExtent, float itemExtent, in WheelLookParams parameters)
    {
        var p = parameters.Sanitized();
        if (!float.IsFinite(distance)) return WheelRowLook.Hidden;
        float emphasis = Emphasis(distance, itemExtent);
        float magnify = 1f + (p.Magnification - 1f) * emphasis;
        float centerOpacity = p.DimOpacity + (1f - p.DimOpacity) * emphasis;
        // 窓が潰れている（大きさが分からない）ときは平らに並べる（円柱にしない）
        if (viewportExtent <= Epsilon)
        {
            return new WheelRowLook
            {
                Visible = true, Angle = 0f, Offset = distance, ScaleX = 1f, ScaleY = 1f,
                Magnify = magnify, Emphasis = emphasis, Opacity = centerOpacity,
            };
        }
        float angle = AngleOf(distance, viewportExtent, p);
        if (MathF.Abs(angle) >= PeakAngle(viewportExtent, p)) return WheelRowLook.Hidden;
        float r = Radius(viewportExtent, p.DiameterRatio);
        float cos = MathF.Cos(angle);
        float sin = MathF.Sin(angle);
        float a = p.Perspective * r;
        float w = 1f + a * (1f - cos);
        float scaleY = (cos * w - a * sin * sin) / (w * w);
        if (scaleY <= 0f) return WheelRowLook.Hidden;
        float shade = 1f + (cos - 1f) * p.EdgeShade;
        return new WheelRowLook
        {
            Visible = true,
            Angle = angle,
            Offset = r * sin / w,
            ScaleX = 1f / w,
            ScaleY = scaleY,
            Magnify = magnify,
            Emphasis = emphasis,
            Opacity = Math.Clamp(centerOpacity * shade, 0f, 1f),
        };
    }

    /// <summary>中央の帯の中の度合い（1 = ちょうど中央、行の高さ以上離れると 0）。</summary>
    public static float Emphasis(float distance, float itemExtent)
        => itemExtent <= Epsilon ? (MathF.Abs(distance) <= Epsilon ? 1f : 0f) : Math.Clamp(1f - MathF.Abs(distance) / itemExtent, 0f, 1f);

    /// <summary>
    /// 描ける行の中心の平らな距離の上限（これより離れた行は円柱の裏）。一覧が前もって作る行の範囲（CacheExtent）を決めるのに使う。
    /// </summary>
    public static float MaxVisibleDistance(float viewportExtent, in WheelLookParams parameters)
    {
        var p = parameters.Sanitized();
        float half = viewportExtent * Half;
        if (half <= Epsilon) return 0f;
        return PeakAngle(viewportExtent, p) * half * p.Squeeze / MaxVisibleRadian(p.DiameterRatio);
    }

    /// <summary>
    /// 映る位置（窓の中心から。下が正）→ その位置に映る行の中心の平らな距離（<see cref="Resolve"/> の Offset の逆。タップした行を求める）。
    /// 映る位置は描ける角度の上限で最も外へ出るので、それより外は上限の角度として扱う。
    /// </summary>
    /// <param name="visualOffset">映る位置。</param>
    /// <param name="viewportExtent">窓の高さ。</param>
    /// <param name="parameters">見た目の値。</param>
    public static float DistanceAtOffset(float visualOffset, float viewportExtent, in WheelLookParams parameters)
    {
        var p = parameters.Sanitized();
        float half = viewportExtent * Half;
        if (half <= Epsilon || !float.IsFinite(visualOffset)) return 0f;
        float target = MathF.Abs(visualOffset);
        float peak = PeakAngle(viewportExtent, p);
        // 映る位置は 0..上限の角度で単調に増える（微分 ∝ 縦の倍率 > 0）ので二分法で角度を求める。上限の外は上限の角度
        float angle = peak;
        if (target < OffsetAtAngle(peak, viewportExtent, p))
        {
            float lo = 0f, hi = peak;
            for (int i = 0; i < InverseIterations; i++)
            {
                float mid = (lo + hi) * Half;
                if (OffsetAtAngle(mid, viewportExtent, p) < target) lo = mid;
                else hi = mid;
            }
            angle = (lo + hi) * Half;
        }
        float distance = angle * half * p.Squeeze / MaxVisibleRadian(p.DiameterRatio);
        return visualOffset < 0f ? -distance : distance;
    }

    /// <summary>中央の帯の高さ（Flutter の選択の帯 = 行の高さ × 拡大の倍率）。</summary>
    public static float BandHeight(float itemExtent, in WheelLookParams parameters)
        => Math.Max(0f, itemExtent) * parameters.Sanitized().Magnification;
}
