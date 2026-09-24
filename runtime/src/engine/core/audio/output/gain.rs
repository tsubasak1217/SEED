// ============================================================
//  core/audio/output/gain.rs — 出力全体の音量倍率（ダッキング）と、出力バッファの書き込み
//
//  【仕組み】
//  メインスレッドが目標の倍率を OutputGain（f32 のビット列を AtomicU32 で持つ）へ書き、
//  オーディオコールバック（別スレッド）がバッファごとに 1 回だけ読む。
//  倍率が変わったバッファでは、GainRamp が 1 バッファかけて前の倍率から新しい倍率へ直線的に移す
//  （一気に切り替えると波形に段差ができ、プツッという雑音になるため）。
//  倍率が 1.0 のまま変わらない間（デスクトップは常にこれ）はサンプルに 1.0 を掛けるだけなので、
//  出力される値は倍率の仕組みが無かったとき（rodio の OutputStream）とビット単位で同じ。
// ============================================================

use std::sync::atomic::{AtomicU32, Ordering};

use rodio::cpal::{FromSample, Sample};

/// 出力全体の音量倍率（メインスレッドが書き、オーディオコールバックが読む）。
pub struct OutputGain {
    /// 倍率（f32）のビット列。
    bits: AtomicU32,
}

impl OutputGain {
    /// 倍率を決めて作る。
    pub fn new(gain: f32) -> Self {
        Self { bits: AtomicU32::new(gain.to_bits()) }
    }

    /// 倍率を変える（次のバッファから 1 バッファかけて移る）。
    pub fn set(&self, gain: f32) {
        self.bits.store(gain.to_bits(), Ordering::Release);
    }

    /// 今の目標の倍率。
    pub fn get(&self) -> f32 {
        f32::from_bits(self.bits.load(Ordering::Acquire))
    }
}

/// オーディオコールバック側が持つ「いま実際に掛けている倍率」。
///
/// コールバックのクロージャが所有する（スレッドをまたがないので同期は要らない）。
pub struct GainRamp {
    /// 直前のバッファの最後に掛けた倍率。
    current: f32,
}

impl GainRamp {
    /// 最初に掛ける倍率を決めて作る。
    pub fn new(initial: f32) -> Self {
        Self { current: initial }
    }

    /// このバッファの `index` 番目（0 始まり・全 `len` 個）のサンプルに掛ける倍率。
    ///
    /// バッファの最後のサンプルでちょうど `target` になる直線。倍率が変わらないときは `target` をそのまま返す
    /// （割り算の誤差で 1.0 からずれないように、計算そのものをしない）。
    #[inline]
    fn gain_at(&self, target: f32, index: usize, len: usize) -> f32 {
        if self.current == target {
            return target;
        }
        let progress = (index + 1) as f32 / len as f32;
        self.current + (target - self.current) * progress
    }

    /// バッファを書き終えた（次のバッファは `target` から始める）。
    #[inline]
    fn finish(&mut self, target: f32) {
        self.current = target;
    }
}

/// 出力バッファを埋める（オーディオコールバックの本体）。
///
/// `source`（ミキサー）から 1 サンプルずつ取り、全体音量の倍率を掛けて出力形式 `T` へ変換する。
/// ミキサーが鳴らす音を持っていない（None を返す）サンプルは無音（`T` の中央値）にする。
///
/// # 引数
/// * `data`   - 書き込む出力バッファ（チャンネルインターリーブ）
/// * `source` - 出力するサンプル列（rodio の DynamicMixer）
/// * `ramp`   - コールバックが持つ倍率の状態
/// * `target` - このバッファで目指す倍率（OutputGain::get をバッファごとに 1 回読んだ値）
pub fn fill_buffer<T, I>(data: &mut [T], source: &mut I, ramp: &mut GainRamp, target: f32)
where
    T: Sample + FromSample<f32>,
    I: Iterator<Item = f32>,
{
    let len = data.len();
    for (index, out) in data.iter_mut().enumerate() {
        let gain = ramp.gain_at(target, index, len);
        *out = source
            .next()
            .map(|sample| T::from_sample_(sample * gain))
            .unwrap_or(T::EQUILIBRIUM);
    }
    ramp.finish(target);
}

// ============================================================
//  ユニットテスト
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;

    /// 比較の許容誤差（f32 の直線補間の丸め分）。
    const EPSILON: f32 = 1e-6;

    /// 倍率 1.0 のままならサンプルはビット単位で変わらない（デスクトップの出力が変わらないことの根拠）。
    #[test]
    fn unity_gain_is_bit_exact() {
        let input = [0.1_f32, -0.25, 0.999, -1.0, 0.0, 1.0e-7];
        let mut out = [0.0_f32; 6];
        let mut ramp = GainRamp::new(1.0);
        fill_buffer(&mut out, &mut input.iter().copied(), &mut ramp, 1.0);
        for (a, b) in input.iter().zip(out.iter()) {
            assert_eq!(a.to_bits(), b.to_bits());
        }
    }

    /// 倍率が変わったバッファでは直線的に移り、最後のサンプルでちょうど目標になる。次のバッファは目標のまま。
    #[test]
    fn gain_change_ramps_over_one_buffer() {
        let mut ramp = GainRamp::new(1.0);
        let mut out = [0.0_f32; 4];
        fill_buffer(&mut out, &mut std::iter::repeat(1.0), &mut ramp, 0.2);
        let expected = [0.8, 0.6, 0.4, 0.2];
        for (got, want) in out.iter().zip(expected.iter()) {
            assert!((got - want).abs() < EPSILON, "{out:?}");
        }
        fill_buffer(&mut out, &mut std::iter::repeat(1.0), &mut ramp, 0.2);
        assert!(out.iter().all(|sample| (sample - 0.2).abs() < EPSILON), "{out:?}");
    }

    /// 鳴らす音が無い（ミキサーが None）サンプルは出力形式の無音（中央値）になる。
    #[test]
    fn missing_samples_become_silence() {
        let mut ramp = GainRamp::new(1.0);
        let mut floats = [9.0_f32; 3];
        fill_buffer(&mut floats, &mut std::iter::empty(), &mut ramp, 1.0);
        assert_eq!(floats, [0.0; 3]);

        let mut unsigned = [0_u16; 2];
        fill_buffer(&mut unsigned, &mut std::iter::empty(), &mut ramp, 1.0);
        assert_eq!(unsigned, [u16::EQUILIBRIUM; 2]);
    }

    /// 整数形式へも倍率を掛けてから変換する（Android の oboe は i16 のこともある）。
    #[test]
    fn integer_output_is_scaled_before_conversion() {
        let mut ramp = GainRamp::new(0.5);
        let mut out = [0_i16; 2];
        fill_buffer(&mut out, &mut [1.0_f32, -1.0].into_iter(), &mut ramp, 0.5);
        assert_eq!(out, [i16::from_sample_(0.5_f32), i16::from_sample_(-0.5_f32)]);
    }

    /// OutputGain は書いた倍率をそのまま返す。
    #[test]
    fn output_gain_stores_value() {
        let gain = OutputGain::new(1.0);
        assert_eq!(gain.get(), 1.0);
        gain.set(0.2);
        assert_eq!(gain.get(), 0.2);
    }
}
