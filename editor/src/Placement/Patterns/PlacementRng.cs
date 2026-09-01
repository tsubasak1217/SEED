namespace SEEDEditor.Placement.Patterns;

/// <summary>
/// ロジック配置の決定的擬似乱数（splitmix64）。
///
/// <para>
/// <b>これは Rust 側 <c>runtime/src/engine/placement/rng.rs</c> の写しである。</b>
/// ダイアログの俯瞰プレビューはランタイムと同じ点列を描かなければならず、
/// そのためには乱数の実装まで一致している必要がある。
/// どちらか一方を変更したら必ず両方を直し、両側のテスト
/// （<c>editor/tests/PlacementTests</c> と runtime の <c>placement::rng::tests</c>）
/// が持つ既知ベクタを更新すること。
/// </para>
///
/// <para>
/// <see cref="System.Random"/> を使わないのは、実装がランタイム／フレームワークの
/// バージョンに依存し、Rust と一致させられないため。
/// </para>
/// </summary>
public sealed class PlacementRng
{
    // ── splitmix64 の混合定数（出典: Steele et al. "Fast Splittable PRNGs"）──

    /// <summary>状態増分（黄金比 φ の 64bit 固定小数表現）。</summary>
    private const ulong SplitmixGamma = 0x9E3779B97F4A7C15UL;
    /// <summary>第 1 乗算定数。</summary>
    private const ulong SplitmixMul1 = 0xBF58476D1CE4E5B9UL;
    /// <summary>第 2 乗算定数。</summary>
    private const ulong SplitmixMul2 = 0x94D049BB133111EBUL;
    /// <summary>第 1 シフト量。</summary>
    private const int SplitmixShift1 = 30;
    /// <summary>第 2 シフト量。</summary>
    private const int SplitmixShift2 = 27;
    /// <summary>最終シフト量。</summary>
    private const int SplitmixShift3 = 31;

    /// <summary>[0,1) へ写すときに使う仮数ビット数（float の仮数幅）。</summary>
    private const int F32MantissaBits = 24;
    /// <summary>上記に対応する除数（2^24）。</summary>
    private const float F32MantissaScale = 1 << F32MantissaBits;

    /// <summary>内部状態。<see cref="NextUInt64"/> のたびに <see cref="SplitmixGamma"/> だけ進む。</summary>
    private ulong _state;

    /// <summary>任意の 64bit 値から生成器を作る（シード 0 でも正常に動く）。</summary>
    /// <param name="seed">乱数シード。</param>
    public PlacementRng(ulong seed) => _state = seed;

    /// <summary>次の 64bit 乱数を返す（splitmix64 本体）。</summary>
    public ulong NextUInt64()
    {
        unchecked
        {
            _state += SplitmixGamma;
            var z = _state;
            z = (z ^ (z >> SplitmixShift1)) * SplitmixMul1;
            z = (z ^ (z >> SplitmixShift2)) * SplitmixMul2;
            return z ^ (z >> SplitmixShift3);
        }
    }

    /// <summary>次の 32bit 乱数を返す（64bit の上位半分＝下位ビットの偏りを避ける）。</summary>
    public uint NextUInt32() => (uint)(NextUInt64() >> 32);

    /// <summary>次の乱数を [0, 1) の float で返す（1.0 は決して返さない）。</summary>
    public float NextFloat()
    {
        var bits = NextUInt32() >> (32 - F32MantissaBits);
        return bits / F32MantissaScale;
    }

    /// <summary>
    /// 次の乱数を [-1, 1) の float で返す（ジッター・ばらつきの共通形）。
    ///
    /// <b>必ず乱数を 1 回だけ消費する。</b>呼び出し側が「ジッター量 0 なら引かない」
    /// のような分岐を入れると、設定値でストリームがずれて決定性が崩れる。
    /// </summary>
    public float NextSigned() => NextFloat() * 2.0f - 1.0f;
}
