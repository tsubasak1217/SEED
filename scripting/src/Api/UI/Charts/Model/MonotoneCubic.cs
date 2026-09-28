using System;
using System.Collections.Generic;

namespace SEED.UI;

// ============================================================
//  MonotoneCubic.cs — 単調な 3 次補間（滑らかな曲線。W2-8。docs/ui_charts.md §3）
//
//  【何をするか】折れ線の点を通る滑らかな曲線（3 次のエルミート補間）の接線を、Fritsch–Carlson の方法で決める:
//    1. 隣の点との傾き d_k = (y_{k+1} − y_k) / (x_{k+1} − x_k)
//    2. 端の接線は隣の区間の傾き、中の接線は両側の傾きの平均。両側の傾きの符号が違う（山・谷）か一方が 0 なら 0
//    3. 傾き 0 の区間は両端の接線を 0 にする。α = m_k / d_k、β = m_{k+1} / d_k が α² + β² > 9 なら τ = 3 / √(α² + β²) を掛ける
//  こうすると曲線は点と点の間で行き過ぎない（点の間の値はその 2 点の値の間に収まる＝単調な区間は単調のまま。
//  7:00 と 7:30 の間の曲線が 7:40 まで膨らまない）。d3 の curveMonotoneX・Chart.js の monotone と同じ目的。
//  【点の数】区間ごとに、横の間隔 ÷ stepLength の数だけ分けて点を作る（上限 maxSubdivisions）。曲線は x の関数なので、横に詰まった
//  区間（全期間を縮めて見たとき。1 日 = 1.2 dp）は縦に大きく揺れていても弦と見分けがつかない＝分けない（点の数は増えない）。
//  xs は狭義の単調増加（同じ x が続く区間は傾き 0・分けない）。
// ============================================================

/// <summary>単調な 3 次補間（純粋な計算）。</summary>
public static class MonotoneCubic
{
    /// <summary>接線を縮める境目（α² + β² がこれを超えたら縮める。Fritsch–Carlson の 3 の円）。</summary>
    private const float MonotoneLimitSq = 9f;
    /// <summary>縮める倍率の分子（τ = 3 / √(α² + β²)）。</summary>
    private const float MonotoneLimit = 3f;
    /// <summary>x の差を 0 とみなす大きさ。</summary>
    private const float XEpsilon = 1e-6f;

    /// <summary>点列の接線（Fritsch–Carlson）を <paramref name="tangents"/> へ入れる（長さは点の数）。</summary>
    public static void Tangents(ReadOnlySpan<Vector2> pts, Span<float> tangents)
    {
        int n = pts.Length;
        if (n == 0) return;
        if (n == 1)
        {
            tangents[0] = 0;
            return;
        }
        // 1. 区間の傾き（x が同じ区間は 0）
        Span<float> slopes = n - 1 <= StackLimit ? stackalloc float[n - 1] : new float[n - 1];
        for (int k = 0; k < n - 1; k++)
        {
            float h = pts[k + 1].x - pts[k].x;
            slopes[k] = h > XEpsilon ? (pts[k + 1].y - pts[k].y) / h : 0f;
        }
        // 2. 端は隣の区間の傾き、中は平均（符号が違う・一方が 0 なら 0）
        tangents[0] = slopes[0];
        tangents[n - 1] = slopes[n - 2];
        for (int k = 1; k < n - 1; k++)
        {
            float a = slopes[k - 1], b = slopes[k];
            tangents[k] = a * b <= 0f ? 0f : (a + b) * 0.5f;
        }
        // 3. 行き過ぎないように縮める
        for (int k = 0; k < n - 1; k++)
        {
            float d = slopes[k];
            if (d == 0f)
            {
                tangents[k] = 0f;
                tangents[k + 1] = 0f;
                continue;
            }
            float alpha = tangents[k] / d, beta = tangents[k + 1] / d;
            float s = alpha * alpha + beta * beta;
            if (s > MonotoneLimitSq)
            {
                float tau = MonotoneLimit / MathF.Sqrt(s);
                tangents[k] = tau * alpha * d;
                tangents[k + 1] = tau * beta * d;
            }
        }
    }

    /// <summary>区間の中の値（3 次のエルミート補間。t は 0..1、h は区間の x の幅）。</summary>
    public static float Evaluate(float y0, float y1, float m0, float m1, float h, float t)
    {
        float t2 = t * t, t3 = t2 * t;
        float h00 = 2f * t3 - 3f * t2 + 1f;
        float h10 = t3 - 2f * t2 + t;
        float h01 = -2f * t3 + 3f * t2;
        float h11 = t3 - t2;
        return h00 * y0 + h10 * h * m0 + h01 * y1 + h11 * h * m1;
    }

    /// <summary>
    /// 点列を通る滑らかな曲線の点を <paramref name="output"/> へ足す（区間ごとに横の間隔 ÷ <paramref name="stepLength"/> 個に分ける。
    /// 上限 <paramref name="maxSubdivisions"/>）。元の点はすべて出力に含まれる。
    /// </summary>
    public static void Sample(ReadOnlySpan<Vector2> pts, float stepLength, int maxSubdivisions, List<Vector2> output)
    {
        int n = pts.Length;
        if (n == 0) return;
        if (n < 3 || !(stepLength > 0) || maxSubdivisions < 2)
        {
            // 2 点以下は直線（補間しても同じ）・分けない指定はそのまま
            foreach (var p in pts) output.Add(p);
            return;
        }
        Span<float> m = n <= StackLimit ? stackalloc float[n] : new float[n];
        Tangents(pts, m);
        output.Add(pts[0]);
        for (int k = 0; k < n - 1; k++)
        {
            Vector2 a = pts[k], b = pts[k + 1];
            float h = b.x - a.x;
            int sub = Subdivisions(h, stepLength, maxSubdivisions);
            if (h > XEpsilon)
            {
                for (int i = 1; i < sub; i++)
                {
                    float t = (float)i / sub;
                    output.Add(new Vector2(a.x + h * t, Evaluate(a.y, b.y, m[k], m[k + 1], h, t)));
                }
            }
            output.Add(b);
        }
    }

    /// <summary>区間を何個に分けるか（横の間隔 ÷ 刻み を切り上げ、1〜上限へ収める）。</summary>
    public static int Subdivisions(float width, float stepLength, int maxSubdivisions)
    {
        if (!(width > 0) || !(stepLength > 0)) return 1;
        return Math.Clamp((int)MathF.Ceiling(width / stepLength), 1, Math.Max(1, maxSubdivisions));
    }

    /// <summary>スタックに取る配列の上限（これを超える点の数はヒープの配列）。</summary>
    private const int StackLimit = 256;
}
