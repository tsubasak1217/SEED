// ============================================================
//  terrain/cover/emit.rs — カバーエミッタの範囲評価（純粋関数）
//
//  【責務】
//    「どのワールド座標に、どれだけの割合で素材が降るか」だけを決める。
//    エミッタの実体（ECS の `CoverEmitterComponent`）や、積算の適用先
//    （`CoverField`）は一切知らない。エンジン層がコンポーネントから
//    `CoverEmitSpec` を組み立て、本ファイルの評価結果を積算へ渡す。
//
//  【範囲の 3 種】
//    Global      … ワールド全域。天候（雪が降る・雨が降る）そのもの
//    Region      … 中心＋半径（直方体）。境界はフェード幅で滑らかに落とす
//    TextureMask … 中心＋XZ サイズ＋グレースケール画像。白=満額・黒=0
//
//  【なぜ CPU で評価するのか】
//    カバー場は 1 チャンク 32×32 テクセルしかなく、エミッタも数個である。
//    GPU コンピュートへ出すと「テクスチャの読み戻し」が保存のたびに要り、
//    永続化（.tcover）との往復が非同期になって設計が一段複雑になる。
//    CPU なら積算・保存・テストがすべて同じ 1 本の同期コードで完結する。
// ============================================================

// ─── 定数（マジックナンバー禁止）─────────────────────────────────────────────

/// 範囲フェード幅の下限（メートル）。
///
/// 0 を許すとゼロ除算になるため、これ未満は「フェード無し（硬い境界）」として扱う。
const FADE_EPSILON: f32 = 1.0e-4;

/// マスク画像のグレースケール値の最大（u8）。
const MASK_VALUE_MAX: f32 = 255.0;

// ============================================================
//  CoverMask — グレースケールマスク画像（CPU 側の生ピクセル）
// ============================================================

/// エミッタの `TextureMask` 範囲が使うグレースケールマスク。
///
/// 画像デコードはエンジン層（`asset_fs::read_image`）の責務であり、
/// 本構造体は「幅・高さ・0..255 のグレー値」だけを持つ。
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct CoverMask {
    /// 画素の横幅（0 なら無効なマスク＝常に 0 を返す）。
    pub width: usize,
    /// 画素の縦幅。
    pub height: usize,
    /// グレー値（row-major・長さ = width × height）。
    pub pixels: Vec<u8>,
}

impl CoverMask {
    /// 無効な（画素を持たない）マスクを作る。常に被覆 0 を返す。
    pub fn empty() -> Self {
        Self { width: 0, height: 0, pixels: Vec::new() }
    }

    /// マスクが有効か（画素があり、長さが幅×高さと一致するか）。
    pub fn is_valid(&self) -> bool {
        self.width > 0 && self.height > 0 && self.pixels.len() == self.width * self.height
    }

    /// 正規化 UV（0..1）でマスクを読む（最近傍）。範囲外は 0。
    ///
    /// V は画像の上端が 0（画像の一般的な行順）。ワールドの +Z を画像の下方向へ
    /// 対応させるかは呼び出し側の変換に委ねる（本関数は素直に UV を読むだけ）。
    pub fn sample(&self, u: f32, v: f32) -> f32 {
        if !self.is_valid() || !u.is_finite() || !v.is_finite() {
            return 0.0;
        }
        if !(0.0..=1.0).contains(&u) || !(0.0..=1.0).contains(&v) {
            return 0.0;
        }
        let x = ((u * self.width as f32) as usize).min(self.width - 1);
        let y = ((v * self.height as f32) as usize).min(self.height - 1);
        self.pixels[x + y * self.width] as f32 / MASK_VALUE_MAX
    }
}

// ============================================================
//  CoverEmitRange — エミッタの範囲
// ============================================================

/// カバーエミッタの範囲（どこに降るか）。
#[derive(Clone, Debug, PartialEq)]
pub enum CoverEmitRange {
    /// ワールド全域（天候）。
    Global,
    /// 中心＋半径の直方体。境界は `fade` 幅で内側へ向かって 0→1 に立ち上がる。
    Region {
        /// 直方体の中心（ワールド座標）。
        center: [f32; 3],
        /// 各軸の半径（メートル）。
        half_extents: [f32; 3],
        /// 境界フェード幅（メートル）。0 なら硬い境界。
        fade: f32,
    },
    /// 中心＋XZ サイズ＋グレースケールマスク。
    ///
    /// Y 方向の制限は持たない（マスクは真上から投影されるため）。
    TextureMask {
        /// マスク矩形の中心（ワールド座標。Y は使わない）。
        center: [f32; 3],
        /// マスク矩形の XZ サイズ（メートル）。
        size_xz: [f32; 2],
        /// グレースケールマスク。
        mask: CoverMask,
    },
}

// ============================================================
//  CoverEmitSpec — 積算 1 回ぶんに必要な情報
// ============================================================

/// カバーエミッタ 1 個をワールド解決した積算仕様。
#[derive(Clone, Debug, PartialEq)]
pub struct CoverEmitSpec {
    /// 降る範囲。
    pub range: CoverEmitRange,
    /// 積む素材の添字（`CoverMaterialSet` の添字）。
    pub material_index: u8,
    /// 強度（量/秒）。範囲内の被覆 1.0 の地点に 1 秒でこれだけ積もる。
    pub rate: f32,
}

impl CoverEmitSpec {
    /// 指定ワールド座標での被覆率（0..1）を返す。
    ///
    /// 実際に積もる量は `被覆率 × rate × dt × 傾斜スケール`。
    /// 傾斜スケールは面の性質なので `field::slope_scale` の担当であり、
    /// 本関数は「エミッタの届き方」だけを返す（責務の分離）。
    pub fn coverage_at(&self, world: [f32; 3]) -> f32 {
        match &self.range {
            // ─── 全域: 常に満額 ───
            CoverEmitRange::Global => 1.0,

            // ─── 直方体: 各軸の内側方向のフェードの最小値 ───
            CoverEmitRange::Region { center, half_extents, fade } => {
                let mut coverage = 1.0f32;
                for axis in 0..3 {
                    let d = (world[axis] - center[axis]).abs();
                    let h = half_extents[axis];
                    // 半径が 0 以下の軸は「その軸には広がらない」＝常に範囲外。
                    if !(h > 0.0) || d > h {
                        return 0.0;
                    }
                    // 境界から内側へ fade 分だけ 0→1 に立ち上げる。
                    if *fade > FADE_EPSILON {
                        let inner = ((h - d) / fade).clamp(0.0, 1.0);
                        coverage = coverage.min(inner);
                    }
                }
                coverage
            }

            // ─── マスク: XZ 矩形へ投影して画像を読む ───
            CoverEmitRange::TextureMask { center, size_xz, mask } => {
                let (sx, sz) = (size_xz[0], size_xz[1]);
                if !(sx > 0.0) || !(sz > 0.0) {
                    return 0.0;
                }
                // 矩形中心を原点とした 0..1 の UV へ写す。
                let u = (world[0] - center[0]) / sx + 0.5;
                // ワールド +Z を画像の下方向（V 増加）へ対応させる。
                // 真上から見下ろした絵をそのまま地面へ貼るのが直感に合うため。
                let v = (world[2] - center[2]) / sz + 0.5;
                mask.sample(u, v)
            }
        }
    }

    /// このエミッタが指定 AABB（チャンクなど）に一切かからないか。
    ///
    /// チャンク単位で早期に弾いてループ自体を回さないための判定。
    /// `Global` は常に `false`（＝必ずかかる）。
    pub fn is_outside_aabb(&self, min: [f32; 3], max: [f32; 3]) -> bool {
        match &self.range {
            CoverEmitRange::Global => false,
            CoverEmitRange::Region { center, half_extents, .. } => {
                for axis in 0..3 {
                    let lo = center[axis] - half_extents[axis];
                    let hi = center[axis] + half_extents[axis];
                    if hi < min[axis] || lo > max[axis] {
                        return true;
                    }
                }
                false
            }
            CoverEmitRange::TextureMask { center, size_xz, .. } => {
                // XZ のみ判定する（Y には広がらない範囲種別）。
                let half = [size_xz[0] * 0.5, size_xz[1] * 0.5];
                let (lo_x, hi_x) = (center[0] - half[0], center[0] + half[0]);
                let (lo_z, hi_z) = (center[2] - half[1], center[2] + half[1]);
                hi_x < min[0] || lo_x > max[0] || hi_z < min[2] || lo_z > max[2]
            }
        }
    }
}
