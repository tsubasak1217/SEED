// ============================================================
//  terrain/cover/field.rs — カバー場（地表に積もった素材と量）
//
//  【責務】
//    地形チャンク 1 個に紐づく低解像度の 2 次元格子（既定 32×32）に
//    「素材添字 ＋ 量」を 1 層だけ保持し、
//      ・積算（どれだけ積もるか）
//      ・素材の置き換え規則（後から積もる素材が古い素材を削って置き換わる）
//      ・地表の傾斜による積もりにくさ
//    を純粋関数として提供する。
//    ECS / GPU / ファイル IO への依存は一切持たない。
//
//  【なぜ低解像度なのか】
//    カバーは「面の性質」であって模様ではない。細かい模様は素材テクスチャと
//    地形レイヤ側が担当するため、カバー場に要るのは「どこにどれだけ積もったか」
//    という滑らかな分布だけである。既定 32×32／16m チャンクで 0.5m 刻みとなり、
//    これは既定のメッシュ頂点間隔（voxel_size = 0.5m）とちょうど一致する
//    ＝頂点へ載せる段階で情報が落ちない最小の解像度である。
//    1 チャンクあたり 32×32×2 バイト = 2 KB しか使わない。
//
//  【なぜ XZ の 2 次元なのか（洞窟の扱い）】
//    カバー場はチャンク（3 次元インデックス）に紐づくため、地表と洞窟の床は
//    多くの場合そもそも別チャンクになり、別々のカバー場を持つ。
//    同一チャンク内に上下 2 枚の面がある場合のみ、XZ 投影では区別できず
//    上の面が勝つ（本フェーズの既知の制限。Y 照合チャネルは I3.2 で扱う）。
// ============================================================

use super::super::chunk_data::TerrainChunkData;
use super::super::settings::TerrainSettings;
use super::material::COVER_MATERIAL_NONE;

// ─── 解像度と量子化（マジックナンバー禁止のため定数化）───────────────────────

/// カバー場の 1 軸あたりのテクセル数。
///
/// 既定のチャンク（16m）で 0.5m 刻みになる値。上げれば足跡単位の細かさに近づくが、
/// 1 チャンクあたりのメモリと積算コストが 2 乗で増える。
pub const COVER_FIELD_RESOLUTION: usize = 32;

/// カバー場 1 チャンク分のテクセル総数。
pub const COVER_FIELD_TEXELS: usize = COVER_FIELD_RESOLUTION * COVER_FIELD_RESOLUTION;

/// 量の量子化最大値（u8 の 255 が量 1.0 に対応する）。
const COVER_AMOUNT_QUANT_MAX: f32 = 255.0;

/// 量の下限・上限（正規化値）。
const COVER_AMOUNT_MIN: f32 = 0.0;
const COVER_AMOUNT_MAX: f32 = 1.0;

// ─── 傾斜ルール（急斜面には積もりにくい）─────────────────────────────────────

/// これ以下の「法線の上向き成分」では一切積もらない閾値。
///
/// 0.34 ≒ 70 度の斜面。ほぼ崖であり、雪も落ち葉も留まらない。
pub const COVER_SLOPE_UP_MIN: f32 = 0.34;

/// これ以上の「法線の上向き成分」で満額積もる閾値。
///
/// 0.87 ≒ 30 度の斜面。これより緩ければ堆積を妨げない、という近似。
pub const COVER_SLOPE_UP_FULL: f32 = 0.87;

/// カバー場にとって「面が無い」ことを表す上向き成分の番兵値。
///
/// 実在する法線の Y 成分は -1..1 に収まるため、-2 は決して衝突しない。
pub const COVER_SURFACE_ABSENT: f32 = -2.0;

/// 傾斜による積もりやすさ（0..1）を返す。
///
/// `up_dot` は地表法線の Y 成分（1 = 完全な水平面、0 = 垂直な崖）。
/// `COVER_SLOPE_UP_MIN` 以下で 0、`COVER_SLOPE_UP_FULL` 以上で 1、
/// その間は smoothstep で滑らかにつなぐ（境界に硬い線が出ないようにする）。
///
/// 面が無いテクセル（`COVER_SURFACE_ABSENT`）は 0 を返すので、
/// 呼び出し側に個別の分岐は要らない。
pub fn slope_scale(up_dot: f32) -> f32 {
    // NaN は比較を素通りするので、有限性で先に落とす（積算が NaN 汚染するのを防ぐ）。
    if !up_dot.is_finite() || up_dot <= COVER_SLOPE_UP_MIN {
        return 0.0;
    }
    if up_dot >= COVER_SLOPE_UP_FULL {
        return 1.0;
    }
    let t = (up_dot - COVER_SLOPE_UP_MIN) / (COVER_SLOPE_UP_FULL - COVER_SLOPE_UP_MIN);
    // smoothstep（3t² - 2t³）。両端で傾き 0 になるため境界が目立たない。
    t * t * (3.0 - 2.0 * t)
}

// ============================================================
//  CoverField — 1 チャンク分のカバー場
// ============================================================

/// 地形チャンク 1 個ぶんのカバー場（素材添字 ＋ 量の 1 層）。
///
/// 添字は XZ の row-major（`index = ix + iz * COVER_FIELD_RESOLUTION`）。
/// 量は u8 量子化（0 = 何も無い、255 = 量 1.0）。
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct CoverField {
    /// 各テクセルの素材添字（`CoverMaterialSet` の添字）。量 0 のときは無意味。
    material: Vec<u8>,
    /// 各テクセルの量（0..255 で 0.0..1.0 を表す）。
    amount: Vec<u8>,
}

impl Default for CoverField {
    fn default() -> Self {
        Self::new()
    }
}

impl CoverField {
    /// 何も積もっていないカバー場を作る。
    pub fn new() -> Self {
        Self {
            material: vec![COVER_MATERIAL_NONE; COVER_FIELD_TEXELS],
            amount: vec![0u8; COVER_FIELD_TEXELS],
        }
    }

    /// 生の素材配列（永続化で使用）。長さは常に `COVER_FIELD_TEXELS`。
    pub fn raw_material(&self) -> &[u8] {
        &self.material
    }

    /// 生の量配列（永続化で使用）。長さは常に `COVER_FIELD_TEXELS`。
    pub fn raw_amount(&self) -> &[u8] {
        &self.amount
    }

    /// 生の配列からカバー場を復元する（永続化からの読み戻し）。
    ///
    /// 長さが `COVER_FIELD_TEXELS` と一致しない場合は `None`
    /// （壊れたファイルを黙って読まない）。
    pub fn from_raw(material: Vec<u8>, amount: Vec<u8>) -> Option<Self> {
        if material.len() != COVER_FIELD_TEXELS || amount.len() != COVER_FIELD_TEXELS {
            return None;
        }
        Some(Self { material, amount })
    }

    /// 全テクセルの量が 0 か（＝保存する価値が無い＝描画にも一切影響しない）。
    pub fn is_empty(&self) -> bool {
        self.amount.iter().all(|&a| a == 0)
    }

    /// 全テクセルを消去する（量 0・素材なし）。
    pub fn clear(&mut self) {
        self.material.fill(COVER_MATERIAL_NONE);
        self.amount.fill(0);
    }

    /// 指定テクセルの量（0..1）を返す。
    pub fn amount_at(&self, ix: usize, iz: usize) -> f32 {
        self.amount[texel_index(ix, iz)] as f32 / COVER_AMOUNT_QUANT_MAX
    }

    /// 指定テクセルの素材添字を返す。
    pub fn material_at(&self, ix: usize, iz: usize) -> u8 {
        self.material[texel_index(ix, iz)]
    }

    /// 指定テクセルへ素材を `delta`（量/正規化）だけ積む。
    ///
    /// 【素材置き換え規則（1 層仕様の要）】
    ///   ・同じ素材が既にある → 単純に量を足す（上限 1.0）
    ///   ・違う素材がある     → **まず古い素材の量を `delta` ぶん削る**。
    ///     削り切って余った分だけが新しい素材として積もり始める。
    ///
    ///   これにより「落ち葉の上に雪が降ると、落ち葉が見えなくなってから
    ///   雪が積もり始める」という自然な遷移が、1 層しか持たないまま得られる。
    ///   逆に言うと、厚く積もった素材を別素材で置き換えるには 2 倍の時間がかかる
    ///   （削る時間 ＋ 積む時間）。これは意図した挙動である。
    ///
    /// `delta` が 0 以下・非有限のときは何もしない。
    pub fn deposit(&mut self, ix: usize, iz: usize, material_index: u8, delta: f32) {
        if !delta.is_finite() || delta <= 0.0 {
            return;
        }
        let i = texel_index(ix, iz);
        let current = self.amount[i] as f32 / COVER_AMOUNT_QUANT_MAX;

        // ─── 同素材（または空のテクセル）は素直に加算する ───
        if self.amount[i] == 0 || self.material[i] == material_index {
            self.material[i] = material_index;
            self.amount[i] = quantize_amount(current + delta);
            return;
        }

        // ─── 異素材: まず古い素材を削り、削り切れたぶんだけ新素材が乗る ───
        if current > delta {
            // 古い素材がまだ残る（素材は変えない）。
            self.amount[i] = quantize_amount(current - delta);
        } else {
            // 古い素材を削り切った。余りが新素材の初期量になる。
            self.material[i] = material_index;
            self.amount[i] = quantize_amount(delta - current);
        }
    }

    /// チャンクローカルの正規化 XZ 座標（0..1）でカバー場を読む。
    ///
    /// 量はバイリニア補間（頂点へ載せたときに階段状にならないようにする）、
    /// 素材は最近傍（添字を補間すると存在しない素材を指してしまうため）。
    ///
    /// 範囲外の入力は端のテクセルへクランプする。
    pub fn sample(&self, u: f32, v: f32) -> (f32, u8) {
        // ─── テクセル中心を基準にした連続座標へ変換する ───
        //   テクセル ix の中心は u = (ix + 0.5) / R。逆に解くと fx = u*R - 0.5。
        let r = COVER_FIELD_RESOLUTION as f32;
        let fx = (clamp01(u) * r - 0.5).clamp(0.0, r - 1.0);
        let fz = (clamp01(v) * r - 0.5).clamp(0.0, r - 1.0);
        let x0 = fx.floor() as usize;
        let z0 = fz.floor() as usize;
        let x1 = (x0 + 1).min(COVER_FIELD_RESOLUTION - 1);
        let z1 = (z0 + 1).min(COVER_FIELD_RESOLUTION - 1);
        let tx = fx - x0 as f32;
        let tz = fz - z0 as f32;

        // ─── 量はバイリニア補間 ───
        let a00 = self.amount_at(x0, z0);
        let a10 = self.amount_at(x1, z0);
        let a01 = self.amount_at(x0, z1);
        let a11 = self.amount_at(x1, z1);
        let a = (a00 * (1.0 - tx) + a10 * tx) * (1.0 - tz) + (a01 * (1.0 - tx) + a11 * tx) * tz;

        // ─── 素材は最近傍（4 隅のうち最も近いテクセル）───
        let nx = if tx < 0.5 { x0 } else { x1 };
        let nz = if tz < 0.5 { z0 } else { z1 };
        (a, self.material_at(nx, nz))
    }
}

/// テクセル座標を配列添字へ変換する（row-major）。
///
/// 範囲外はデバッグビルドで assert（呼び出し側は必ず 0..R の値を渡す契約）。
#[inline]
fn texel_index(ix: usize, iz: usize) -> usize {
    debug_assert!(
        ix < COVER_FIELD_RESOLUTION && iz < COVER_FIELD_RESOLUTION,
        "cover texel index out of bounds: ({ix},{iz}) >= {COVER_FIELD_RESOLUTION}"
    );
    ix + iz * COVER_FIELD_RESOLUTION
}

/// 正規化量（0..1）を u8 へ量子化する（範囲外はクランプ）。
#[inline]
fn quantize_amount(v: f32) -> u8 {
    let c = v.clamp(COVER_AMOUNT_MIN, COVER_AMOUNT_MAX);
    // 四捨五入して 0..255 へ。丸め方向を固定しないと積算が非決定的になる。
    (c * COVER_AMOUNT_QUANT_MAX).round() as u8
}

/// 0..1 へのクランプ（NaN は 0 に落とす）。
#[inline]
fn clamp01(v: f32) -> f32 {
    if !v.is_finite() {
        return 0.0;
    }
    v.clamp(0.0, 1.0)
}

// ============================================================
//  CoverSurface — カバー場テクセルごとの地表情報
// ============================================================

/// カバー場テクセルごとの「積もる先の面」の情報。
///
/// 密度場から導出される派生データであり、地形を編集したら作り直す
/// （＝保存しない。`.tcover` に入るのは `CoverField` だけである）。
///
/// 【なぜメッシュ頂点ではなく密度場から作るのか】
///   描画メッシュは LOD で頂点密度が変わる（遠景は粗い）。メッシュから面情報を
///   作ると、カメラが近づいただけで積もり方が変わってしまう。密度場は LOD に
///   依らない唯一の真値なので、そこから作れば見た目に依存しない積算になる。
#[derive(Clone, Debug, PartialEq)]
pub struct CoverSurface {
    /// 各テクセルの地表法線の Y 成分。面が無ければ `COVER_SURFACE_ABSENT`。
    up: Vec<f32>,
    /// 各テクセルの地表のワールド Y 座標（メートル）。面が無ければ 0（無意味）。
    surface_y: Vec<f32>,
}

impl CoverSurface {
    /// 全テクセル「面なし」の空の面情報を作る。
    pub fn empty() -> Self {
        Self {
            up: vec![COVER_SURFACE_ABSENT; COVER_FIELD_TEXELS],
            surface_y: vec![0.0; COVER_FIELD_TEXELS],
        }
    }

    /// 密度チャンクから面情報を作る。
    ///
    /// 各テクセルについて、そのテクセル中心に最も近い密度サンプル列を
    /// **上から下へ**走査し、最初に見つかった「空気 → 個体」の境界を面とする
    /// （＝同一チャンク内に複数の面がある場合は上の面が勝つ。XZ 投影の制限）。
    ///
    /// `chunk_origin_y` はチャンク最小コーナーのワールド Y。
    pub fn from_chunk(
        chunk: &TerrainChunkData,
        settings: &TerrainSettings,
        chunk_origin_y: f32,
    ) -> Self {
        let mut me = Self::empty();
        let samples = chunk.samples_per_axis();
        // サンプルが 3 未満だと中央差分が取れない（勾配＝法線が作れない）。
        if samples < 3 {
            return me;
        }
        let iso = settings.iso_level;
        let voxel = settings.voxel_size;

        for iz in 0..COVER_FIELD_RESOLUTION {
            for ix in 0..COVER_FIELD_RESOLUTION {
                // ─── テクセル中心に対応する密度サンプル添字（中央差分のため内側へ寄せる）───
                let sx = texel_to_sample(ix, samples);
                let sz = texel_to_sample(iz, samples);

                // ─── 上から下へ走査して最初の「空気 → 個体」境界を探す ───
                //   規約: density < iso ⇒ SOLID、density > iso ⇒ AIR。
                let mut found: Option<(usize, f32)> = None;
                for iy in (1..samples).rev() {
                    let upper = chunk.sample(sx, iy, sz);
                    let lower = chunk.sample(sx, iy - 1, sz);
                    if upper > iso && lower <= iso {
                        // 境界は iy-1 と iy の間。線形補間で交点のセル内比率を得る。
                        let denom = upper - lower;
                        let t = if denom.abs() > f32::EPSILON {
                            ((iso - lower) / denom).clamp(0.0, 1.0)
                        } else {
                            0.0
                        };
                        found = Some((iy - 1, t));
                        break;
                    }
                }
                let Some((iy_lo, t)) = found else { continue };

                // ─── 面の高さ（ワールド Y）───
                let idx = texel_index(ix, iz);
                me.surface_y[idx] = chunk_origin_y + (iy_lo as f32 + t) * voxel;

                // ─── 法線 = 密度勾配の正規化（density = y の平坦地面で (0,1,0) になる）───
                //   中央差分が取れるよう、サンプル添字を内側 1 つぶんへ寄せる。
                let cy = iy_lo.clamp(1, samples - 2);
                let gx = chunk.sample(sx + 1, cy, sz) - chunk.sample(sx - 1, cy, sz);
                let gy = chunk.sample(sx, cy + 1, sz) - chunk.sample(sx, cy - 1, sz);
                let gz = chunk.sample(sx, cy, sz + 1) - chunk.sample(sx, cy, sz - 1);
                let len = (gx * gx + gy * gy + gz * gz).sqrt();
                me.up[idx] = if len > f32::EPSILON {
                    (gy / len).clamp(-1.0, 1.0)
                } else {
                    // 勾配が縮退（完全に一様な密度）＝面の向きが決まらない。
                    // 積もらせない側へ倒す（COVER_SURFACE_ABSENT）。
                    COVER_SURFACE_ABSENT
                };
            }
        }
        me
    }

    /// 指定テクセルの地表法線 Y 成分（面が無ければ `COVER_SURFACE_ABSENT`）。
    pub fn up_at(&self, ix: usize, iz: usize) -> f32 {
        self.up[texel_index(ix, iz)]
    }

    /// 指定テクセルの地表ワールド Y（面が無ければ意味を持たない）。
    pub fn surface_y_at(&self, ix: usize, iz: usize) -> f32 {
        self.surface_y[texel_index(ix, iz)]
    }

    /// 指定テクセルに面があるか。
    pub fn has_surface(&self, ix: usize, iz: usize) -> bool {
        self.up[texel_index(ix, iz)] > COVER_SURFACE_ABSENT
    }
}

/// カバー場テクセル添字 → 密度サンプル添字（中央差分が取れる内側へクランプ）。
#[inline]
fn texel_to_sample(texel: usize, samples: usize) -> usize {
    // テクセル中心の正規化位置 × セル数（= samples - 1）を四捨五入する。
    let cells = (samples - 1) as f32;
    let s = ((texel as f32 + 0.5) / COVER_FIELD_RESOLUTION as f32 * cells).round() as usize;
    s.clamp(1, samples - 2)
}

/// カバー場テクセル中心のチャンクローカル正規化座標（0..1）を返す。
///
/// 積算側が「テクセルのワールド座標」を求めるために使う。
#[inline]
pub fn texel_center_uv(ix: usize, iz: usize) -> (f32, f32) {
    let r = COVER_FIELD_RESOLUTION as f32;
    ((ix as f32 + 0.5) / r, (iz as f32 + 0.5) / r)
}
