using System;

namespace SEED;

/// <summary>
/// スクリプトから使う数学ユーティリティ（float 中心）。
///
/// <see cref="System.MathF"/> を土台に、ゲーム制作で頻出する補間・クランプ・
/// 角度変換などを Unity 風の名前でまとめたもの。すべて静的メソッドで、
/// スクリプトからは <c>Mathf.Lerp(...)</c> のように呼べる。
/// </summary>
public static class Mathf {
	// ── 定数 ────────────────────────────────────────────────
	/// <summary>円周率 π。</summary>
	public const float PI = MathF.PI;
	/// <summary>度 → ラジアン変換係数（π/180）。</summary>
	public const float Deg2Rad = PI / 180f;
	/// <summary>ラジアン → 度変換係数（180/π）。</summary>
	public const float Rad2Deg = 180f / PI;
	/// <summary>float でほぼ 0 とみなす微小値。</summary>
	public const float Epsilon = 1e-6f;
	/// <summary>正の無限大。</summary>
	public const float Infinity = float.PositiveInfinity;
	/// <summary>負の無限大。</summary>
	public const float NegativeInfinity = float.NegativeInfinity;

	// ── 三角関数（引数・戻り値はラジアン）───────────────────
	public static float Sin(float x) => MathF.Sin(x);
	public static float Cos(float x) => MathF.Cos(x);
	public static float Tan(float x) => MathF.Tan(x);
	public static float Asin(float x) => MathF.Asin(x);
	public static float Acos(float x) => MathF.Acos(x);
	public static float Atan(float x) => MathF.Atan(x);
	/// <summary>y/x の逆正接（象限を考慮）。戻り値はラジアン。</summary>
	public static float Atan2(float y, float x) => MathF.Atan2(y, x);

	// ── 冪・指数・対数・平方根 ───────────────────────────────
	public static float Sqrt(float x) => MathF.Sqrt(x);
	public static float Pow(float x, float p) => MathF.Pow(x, p);
	public static float Exp(float x) => MathF.Exp(x);
	public static float Log(float x) => MathF.Log(x);
	public static float Log(float x, float b) => MathF.Log(x, b);
	public static float Log10(float x) => MathF.Log10(x);

	// ── 符号・絶対値・丸め ───────────────────────────────────
	public static float Abs(float x) => MathF.Abs(x);
	public static int Abs(int x) => Math.Abs(x);
	/// <summary>符号（正=1 / 負=-1 / 0=0）。</summary>
	public static float Sign(float x) => x > 0f ? 1f : (x < 0f ? -1f : 0f);
	public static float Floor(float x) => MathF.Floor(x);
	public static float Ceil(float x) => MathF.Ceiling(x);
	public static float Round(float x) => MathF.Round(x);
	public static int FloorToInt(float x) => (int)MathF.Floor(x);
	public static int CeilToInt(float x) => (int)MathF.Ceiling(x);
	public static int RoundToInt(float x) => (int)MathF.Round(x);

	// ─ 正規化 ───────────────────────────────────────────────
	public static Vector2 Normalize(Vector2 v) {
		float m = v.Magnitude;
		return m > Epsilon ? new Vector2(v.x / m, v.y / m) : Vector2.Zero;
	}

	public static Vector3 Normalize(Vector3 v) {
		float m = v.Magnitude;
		return m > Epsilon ? new Vector3(v.x / m, v.y / m, v.z / m) : Vector3.Zero;
	}

	// ── 最小・最大・クランプ ─────────────────────────────────
	public static float Min(float a, float b) => a < b ? a : b;
	public static int Min(int a, int b) => a < b ? a : b;
	public static float Max(float a, float b) => a > b ? a : b;
	public static int Max(int a, int b) => a > b ? a : b;

	/// <summary>value を [min, max] に収めた数を返す（浮動小数）。</summary>
	public static float Clamped(float value, float min, float max)
		=> value < min ? min : (value > max ? max : value);
	/// <summary>value を書き換えて[min, max] に収める。</summary>
	public static void Clamp(ref float value, float min, float max) {
		value = Clamped(value, min, max);
	}
	/// <summary>value を [min, max] に収めた数を返す（整数）。</summary>
	public static int Clamped(int value, int min, int max)
		=> value < min ? min : (value > max ? max : value);
	/// <summary>value を書き換えて[min, max] に収める。</summary>
	public static void Clamp(ref int value, int min, int max) {
		value = Clamped(value, min, max);
	}

	/// <summary>value を [0, 1] に収める。</summary>
	public static float Clamped01(float value)
		=> value < 0f ? 0f : (value > 1f ? 1f : value);
	/// <summary>value を書き換えて [0, 1] に収める。</summary>
	public static void Clamp01(ref float value) {
		value = Clamped01(value);
	}

	// ── 補間 ────────────────────────────────────────────────
	/// <summary>a→b を t（0..1、範囲外はクランプ）で線形補間する。</summary>
	public static float Lerp(float a, float b, float t) => a + (b - a) * Clamped01(t);
	/// <summary>クランプなしの線形補間。</summary>
	public static float LerpUnclamped(float a, float b, float t) => a + (b - a) * t;
	/// <summary>value が a→b のどこにあるか（0..1）を返す（Lerp の逆）。</summary>
	public static float InverseLerp(float a, float b, float value)
		=> Approximately(a, b) ? 0f : Clamped01((value - a) / (b - a));

	/// <summary>current から target へ最大 maxDelta だけ近づける（行き過ぎない）。</summary>
	public static float MoveTowards(float current, float target, float maxDelta) {
		if (Abs(target - current) <= maxDelta) return target;
		return current + Sign(target - current) * maxDelta;
	}

	/// <summary>t を [0, length) で繰り返す（負値も正しく巻き戻す）。</summary>
	public static float Repeat(float t, float length)
		=> Clamped(t - Floor(t / length) * length, 0f, length);

	/// <summary>0→length→0 を往復する三角波。</summary>
	public static float PingPong(float t, float length) {
		t = Repeat(t, length * 2f);
		return length - Abs(t - length);
	}

	/// <summary>from→to を滑らかに（両端で速度 0）補間する。</summary>
	public static float SmoothStep(float from, float to, float t) {
		t = Clamped01(t);
		t = t * t * (3f - 2f * t);
		return from + (to - from) * t;
	}

	// ── 比較 ────────────────────────────────────────────────
	/// <summary>2 つの float が誤差の範囲で等しいか（浮動小数の直接比較を避ける）。</summary>
	public static bool Approximately(float a, float b)
		=> Abs(b - a) < Max(Epsilon * Max(Abs(a), Abs(b)), Epsilon * 8f);
}
