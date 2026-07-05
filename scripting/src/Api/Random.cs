using System;

namespace SEED;

/// <summary>
/// ゲーム用の乱数ユーティリティ。エンジン全体で 1 つの乱数系列を共有する。
///
/// <see cref="System.Random"/> をラップし、Unity 風の <c>Random.Range</c> /
/// <c>Random.value</c> を提供する。<c>using System;</c> と併用すると
/// <see cref="System.Random"/> と名前が衝突するため、その場合は
/// <c>SEED.Random</c> と明示して呼ぶこと。
/// </summary>
public static class Random
{
    // 既定シードは実行ごとに変わる（再現したい場合は InitState を呼ぶ）
    private static System.Random _rng = new();

    /// <summary>乱数シードを固定する（同じシードなら同じ系列を再現できる）。</summary>
    public static void InitState(int seed) => _rng = new System.Random(seed);

    /// <summary>0.0 以上 1.0 未満の float。</summary>
    public static float Value => (float)_rng.NextDouble();

    /// <summary>min 以上 max 未満の float を返す。</summary>
    public static float Range(float min, float max)
        => min + (float)_rng.NextDouble() * (max - min);

    /// <summary>min 以上 max 未満の int を返す（max は含まない）。</summary>
    public static int Range(int min, int max)
        => _rng.Next(min, max);

    /// <summary>true/false をランダムに返す。</summary>
    public static bool Bool => _rng.NextDouble() < 0.5;

    /// <summary>半径 1 の円内のランダムな点（2D）。</summary>
    public static Vector2 InsideUnitCircle
    {
        get
        {
            // 一様分布のため半径は面積補正（√）してサンプリングする
            float angle  = Range(0f, Mathf.PI * 2f);
            float radius = Mathf.Sqrt(Value);
            return new Vector2(Mathf.Cos(angle) * radius, Mathf.Sin(angle) * radius);
        }
    }

    /// <summary>長さ 1 のランダムな方向（3D）。</summary>
    public static Vector3 OnUnitSphere
    {
        get
        {
            float z     = Range(-1f, 1f);
            float angle = Range(0f, Mathf.PI * 2f);
            float r     = Mathf.Sqrt(1f - z * z);
            return new Vector3(Mathf.Cos(angle) * r, Mathf.Sin(angle) * r, z);
        }
    }
}
