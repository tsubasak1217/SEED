// ============================================================
//  fast_hash.rs — フレームごとの使い回しの表を引くための速いハッシュ（Fx 方式。暗号用ではない）
//
//  【なぜ要るか（2026-09-28。docs/app_platform_roadmap.md §3.9.1）】
//  UI の積み込みの使い回しの表（font/text_layout_cache.rs・renderer/primitive2d/tess_cache.rs）は、毎フレーム図形・テキストの
//  数だけ（グラフの見本で 1 フレーム約 475 回）キーを要約して表を引く。std の既定（SipHash）は外からの攻撃に強い代わりに遅い。
//  ここの表の鍵は自分のフレームの中身で、外から与えられる値ではないので、速さを優先した Fx 方式
//  （rustc の FxHasher と同じ式: 語ごとに `(h.rotl(5) ^ 語) * 定数`）を使う。
//  （このハッシュだけの効き目は分けて測っていない。同じ回の変更と合わせた実機の差は roadmap §3.9.1 の段 4 → 段 5）
//
//  【取り違えは起きない】要約は表の振り分けにだけ使い、同じ要約の中ではキーそのものを全部比べる（両方の表の約束）。
//  要約がぶつかっても遅くなるだけで、別のキーの値を返すことは無い。
// ============================================================

use std::hash::{BuildHasherDefault, Hasher};

/// Fx 方式の掛ける定数（rustc の FxHasher と同じ 64 ビットの値）。
const FX_SEED: u64 = 0x51_7c_c1_b7_27_22_0a_95;

/// Fx 方式で 1 語ずつ混ぜるときの回転の量（ビット）。
const FX_ROTATE: u32 = 5;

/// 速いハッシュ（std の `Hasher`）。`HashMap<K, V, FastBuildHasher>` や、要約を自分で作るときに使う。
#[derive(Default, Clone, Copy)]
pub struct FastHasher {
    /// ここまでの要約。
    hash: u64,
}

impl FastHasher {
    /// 1 語（64 ビット）を混ぜる。
    #[inline]
    fn add(&mut self, word: u64) {
        self.hash = (self.hash.rotate_left(FX_ROTATE) ^ word).wrapping_mul(FX_SEED);
    }
}

impl Hasher for FastHasher {
    #[inline]
    fn write(&mut self, bytes: &[u8]) {
        // 8 バイトずつ混ぜ、端は残りのバイトを詰めて 1 語にする
        let mut chunks = bytes.chunks_exact(8);
        for c in &mut chunks {
            self.add(u64::from_le_bytes([c[0], c[1], c[2], c[3], c[4], c[5], c[6], c[7]]));
        }
        let rest = chunks.remainder();
        if !rest.is_empty() {
            let mut tail = [0u8; 8];
            tail[..rest.len()].copy_from_slice(rest);
            // 長さも混ぜる（"a" と "a\0" を別にする）
            self.add(u64::from_le_bytes(tail) ^ ((rest.len() as u64) << 56));
        }
    }

    #[inline]
    fn write_u8(&mut self, i: u8) {
        self.add(u64::from(i));
    }

    #[inline]
    fn write_u16(&mut self, i: u16) {
        self.add(u64::from(i));
    }

    #[inline]
    fn write_u32(&mut self, i: u32) {
        self.add(u64::from(i));
    }

    #[inline]
    fn write_u64(&mut self, i: u64) {
        self.add(i);
    }

    #[inline]
    fn write_usize(&mut self, i: usize) {
        self.add(i as u64);
    }

    #[inline]
    fn finish(&self) -> u64 {
        self.hash
    }
}

/// `HashMap` の `S`（速いハッシュを使う表）。
pub type FastBuildHasher = BuildHasherDefault<FastHasher>;

/// 32 ビットの語の並びの要約（三角形分割のキーなど。語ごとに 1 回混ぜる）。
#[inline]
pub fn digest_u32(words: &[u32]) -> u64 {
    let mut h = FastHasher::default();
    for &w in words {
        h.add(u64::from(w));
    }
    // 長さも混ぜる（末尾が 0 の語の有無を区別する）
    h.add(words.len() as u64);
    h.hash
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::hash::Hash;

    /// 同じ並びは同じ要約・1 語でも違えば（ほぼ必ず）違う要約・長さの違いも区別する。
    #[test]
    fn digest_is_deterministic_and_sensitive() {
        assert_eq!(digest_u32(&[1, 2, 3]), digest_u32(&[1, 2, 3]));
        assert_ne!(digest_u32(&[1, 2, 3]), digest_u32(&[1, 2, 4]));
        assert_ne!(digest_u32(&[1, 2, 3]), digest_u32(&[3, 2, 1]));
        assert_ne!(digest_u32(&[1, 2]), digest_u32(&[1, 2, 0]));
        assert_ne!(digest_u32(&[]), digest_u32(&[0]));
    }

    /// 文字列の要約は内容で決まり、末尾のバイトの違い・長さの違いを区別する。
    #[test]
    fn hasher_distinguishes_strings() {
        let h = |s: &str| {
            let mut x = FastHasher::default();
            s.hash(&mut x);
            x.finish()
        };
        assert_eq!(h("こんにちは"), h("こんにちは"));
        assert_ne!(h("abc"), h("abd"));
        assert_ne!(h("a"), h("a\0"));
        assert_ne!(h("12345678"), h("123456789"));
    }
}
