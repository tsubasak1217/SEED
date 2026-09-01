// ============================================================
//  placement/rng.rs — ロジック配置の決定的擬似乱数（splitmix64）
//
//  【なぜ専用の RNG を置くのか】
//  ロジック配置は「同じシード → 同じ点列」であることが仕様であり、さらに
//  **エディタ（C#）のプレビューとランタイム（Rust）の実生成が一致する**
//  ことまで求められる。したがって乱数は
//    ・仕様が固定されたアルゴリズム（splitmix64）であること
//    ・浮動小数への写像まで含めて手順が完全に決まっていること
//  が必要になる。`rand` クレートや `DefaultHasher` はどちらも
//  「バージョン間で出力が変わらない」保証が無いため使わない。
//
//  【terrain::scatter::ScatterRng との関係】
//  アルゴリズムは同一（splitmix64）だが、あちらは地形散布層の内部型であり
//  ロジック配置が地形モジュールへ依存する筋合いは無い。ここでは
//  「純粋なパターン生成層は engine の他サブシステムに一切依存しない」
//  という切り分けを優先し、8 行のアルゴリズムを独立に持つ。
//  C# 側 `editor/src/Placement/Patterns/PlacementRng.cs` が本ファイルの
//  写しであり、両者の一致は `editor/tests/PlacementTests` と
//  本モジュールのテストが同じ既知入力で固定する。
// ============================================================

// ─── splitmix64 の混合定数（出典: Steele et al. "Fast Splittable PRNGs"）────

/// splitmix64 の状態増分（黄金比 φ の 64bit 固定小数表現）。
const SPLITMIX_GAMMA: u64 = 0x9E37_79B9_7F4A_7C15;
/// splitmix64 の第 1 乗算定数。
const SPLITMIX_MUL1: u64 = 0xBF58_476D_1CE4_E5B9;
/// splitmix64 の第 2 乗算定数。
const SPLITMIX_MUL2: u64 = 0x94D0_49BB_1331_11EB;
/// splitmix64 の第 1 シフト量。
const SPLITMIX_SHIFT1: u32 = 30;
/// splitmix64 の第 2 シフト量。
const SPLITMIX_SHIFT2: u32 = 27;
/// splitmix64 の最終シフト量。
const SPLITMIX_SHIFT3: u32 = 31;

/// `[0,1)` へ写すときに使う仮数ビット数（f32 の仮数幅）。
///
/// 上位 24 bit だけを使えば「取り得る値がすべて等確率」かつ
/// 1.0 を絶対に含まない写像になる。
const F32_MANTISSA_BITS: u32 = 24;
/// 上記に対応する除数（2^24）。
const F32_MANTISSA_SCALE: f32 = (1u32 << F32_MANTISSA_BITS) as f32;

/// ロジック配置用の決定的擬似乱数生成器。
#[derive(Clone, Debug)]
pub struct PlacementRng {
    /// 内部状態。`next_u64` のたびに `SPLITMIX_GAMMA` だけ進む。
    state: u64,
}

impl PlacementRng {
    /// 任意の 64bit 値から生成器を作る（シード 0 でも正常に動く）。
    pub fn new(seed: u64) -> Self {
        Self { state: seed }
    }

    /// 次の 64bit 乱数を返す（splitmix64 本体）。
    pub fn next_u64(&mut self) -> u64 {
        self.state = self.state.wrapping_add(SPLITMIX_GAMMA);
        let mut z = self.state;
        z = (z ^ (z >> SPLITMIX_SHIFT1)).wrapping_mul(SPLITMIX_MUL1);
        z = (z ^ (z >> SPLITMIX_SHIFT2)).wrapping_mul(SPLITMIX_MUL2);
        z ^ (z >> SPLITMIX_SHIFT3)
    }

    /// 次の 32bit 乱数を返す（64bit の上位半分＝下位ビットの偏りを避ける）。
    pub fn next_u32(&mut self) -> u32 {
        (self.next_u64() >> 32) as u32
    }

    /// 次の乱数を `[0, 1)` の f32 で返す（1.0 は決して返さない）。
    pub fn next_f32(&mut self) -> f32 {
        let bits = self.next_u32() >> (32 - F32_MANTISSA_BITS);
        bits as f32 / F32_MANTISSA_SCALE
    }

    /// 次の乱数を `[-1, 1)` の f32 で返す（ジッター・ばらつきの共通形）。
    ///
    /// **必ず乱数を 1 回だけ消費する**。呼び出し側が「ジッター量 0 なら引かない」
    /// のような分岐を入れると、設定値でストリームがずれて決定性が崩れるため、
    /// 消費数を固定できるようにこの形で提供する。
    pub fn next_signed(&mut self) -> f32 {
        self.next_f32() * 2.0 - 1.0
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    /// 同じシードなら同じ列を返すこと（決定性の最小契約）。
    #[test]
    fn same_seed_same_stream() {
        let mut a = PlacementRng::new(12345);
        let mut b = PlacementRng::new(12345);
        for _ in 0..64 {
            assert_eq!(a.next_u64(), b.next_u64());
        }
    }

    /// 異なるシードでは列が分かれること（シード指定が意味を持つこと）。
    #[test]
    fn different_seed_differs() {
        let mut a = PlacementRng::new(1);
        let mut b = PlacementRng::new(2);
        assert_ne!(a.next_u64(), b.next_u64());
    }

    /// `next_f32` が `[0,1)` に収まること（1.0 を返さないこと）。
    #[test]
    fn next_f32_in_unit_range() {
        let mut r = PlacementRng::new(0xDEAD_BEEF);
        for _ in 0..10_000 {
            let v = r.next_f32();
            assert!((0.0..1.0).contains(&v), "範囲外の乱数: {v}");
        }
    }

    /// `next_signed` が `[-1,1)` に収まること。
    #[test]
    fn next_signed_in_symmetric_range() {
        let mut r = PlacementRng::new(7);
        for _ in 0..10_000 {
            let v = r.next_signed();
            assert!((-1.0..1.0).contains(&v), "範囲外の乱数: {v}");
        }
    }

    /// **C# 実装との一致を固定する既知ベクタ**。
    ///
    /// `editor/tests/PlacementTests` が同じシード・同じ回数で同じ値を要求する。
    /// この値が変わるということは乱数アルゴリズムを変えたということであり、
    /// エディタのプレビューとランタイムの生成結果がずれる。
    #[test]
    fn known_vector_matches_csharp_mirror() {
        let mut r = PlacementRng::new(1);
        let got: Vec<u64> = (0..4).map(|_| r.next_u64()).collect();
        assert_eq!(
            got,
            vec![
                10451216379200822465,
                13757245211066428519,
                17911839290282890590,
                8196980753821780235,
            ],
            "splitmix64(seed=1) の既知列。C# 側 PlacementRng と一致させること"
        );
    }
}
