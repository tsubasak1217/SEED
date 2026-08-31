using System;

namespace SEED;

/// <summary>
/// 線の点列を組み立てる補助関数群（純 C#。エンジンへの FFI を一切行わない）。
///
/// <see cref="LineRenderer.SetPoints(Vector3[])"/> に渡す点列を作るためのユーティリティで、
/// 釣り糸のたわみ（カテナリー）のように「毎フレーム計算して線へ流す」形状を提供する。
/// </summary>
public static class LineHelper
{
    /// <summary>
    /// カテナリー曲線の形状定数（懸垂線パラメータ a）。
    ///
    /// たわみの「輪郭のきつさ」を決める。値を大きくすると端が急で中央が平らな
    /// ロープらしい曲線に、小さくすると放物線に近い緩い曲線になる。
    /// 2.0 は釣り糸・ロープとして自然に見える値として選んだ既定値。
    /// <b>Rust 側の検証テスト（line_renderer_ops.rs）と同じ値を使うこと。</b>
    /// </summary>
    private const float CatenaryShape = 2.0f;

    /// <summary>分割数の下限（1 = 始点と終点だけの直線）。</summary>
    private const int MinSegments = 1;

    /// <summary>
    /// 始点と終点を結ぶ、下向きにたわんだカテナリー状の点列を生成する。
    ///
    /// 竿先 → ウキの釣り糸、支柱間のロープ、投擲の予測線などに使う。
    /// 返る点列は必ず <paramref name="start"/> で始まり <paramref name="end"/> で終わる
    /// （端点は数値誤差なく厳密に一致する）。たわみは常にワールドの -Y 方向。
    /// </summary>
    /// <param name="start">始点（ワールド座標）。例: 竿先。</param>
    /// <param name="end">終点（ワールド座標）。例: ウキ。</param>
    /// <param name="slack">
    /// 中央でのたわみ量（ワールド単位）。0 で直線、値を大きくするほど深く垂れる。
    /// 負値は 0 として扱う（上向きにたわむ糸は物理的に無いため）。
    /// </param>
    /// <param name="segments">
    /// 分割数。返る点数は segments + 1。1 未満は 1、
    /// <see cref="LineRenderer.MaxPoints"/> を超える点数になる値は上限へ丸める。
    /// </param>
    /// <returns>segments + 1 個の点列。</returns>
    public static Vector3[] Catenary(Vector3 start, Vector3 end, float slack, int segments)
    {
        // 分割数を有効範囲へ丸める（点数 = segments + 1 が線の上限を超えないこと）。
        int maxSegments = LineRenderer.MaxPoints - 1;
        if (segments < MinSegments) segments = MinSegments;
        if (segments > maxSegments) segments = maxSegments;

        float sag = slack > 0f ? slack : 0f;
        var points = new Vector3[segments + 1];

        // たわみ形状の正規化係数。分母は cosh(a) - 1 で、t=0.5 のとき係数が -1 になる
        // （＝中央のたわみがちょうど sag になる）。
        double coshA = Math.Cosh(CatenaryShape);
        double denom = coshA - 1.0;

        for (int i = 0; i <= segments; i++)
        {
            float t = (float)i / segments;

            // 端点は誤差を入れずに厳密一致させる（糸が竿先／ウキから浮くのを防ぐ）。
            if (i == 0)        { points[i] = start; continue; }
            if (i == segments) { points[i] = end;   continue; }

            // 直線補間した基準位置に、-Y 方向のたわみを足す。
            var baseP = Vector3.Lerp(start, end, t);

            // 正規化カテナリー: t=0,1 で 0、t=0.5 で -1 になる。
            // denom が 0 になるのは CatenaryShape=0 のときだけで、定数なので起こらない。
            double shaped = (Math.Cosh(CatenaryShape * (2.0 * t - 1.0)) - coshA) / denom;
            points[i] = new Vector3(baseP.x, baseP.y + (float)(shaped * sag), baseP.z);
        }

        return points;
    }
}
