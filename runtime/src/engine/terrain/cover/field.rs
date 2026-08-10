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
//    同一チャンク内に上下 2 枚の面がある場合のみ、XZ 投影では区別できず上の面が勝つ。
//
//  【I3.2 で足したもの — 踏み固め（trample）と面の基準 Y（base_y）】
//    ・trample … 足跡・轍の深さ。量と同じ量子化（0..255 = 0.0..1.0）で持ち、
//                実効の盛り上がりは `max(量 - trample, 0)` になる。
//                trample が量を超えたテクセルは下地（地形レイヤ）が露出する。
//    ・base_y  … そのテクセルの「積もっている面」のワールド Y（メートル）。
//                量を持つテクセルでは必ず有限値であり、面が無ければ
//                `COVER_BASE_Y_ABSENT`（-∞）を入れる。
//
//    base_y があることで、カバー場の読み取りを **ワールド Y でも照合**できる
//    （`CoverNeighborhood` が 3×3×3 になった理由）。これは
//      ① 洞窟の床に付いた轍が頭上の地表へ写らない（スタンプ時の Y 照合）
//      ② Y（上下）チャンク境界をまたぐ複製頂点が同じ面を読む（I3.1 §7-8 の段差解消）
//    の両方を、同じ 1 つの規約「面はワールド位置の純関数として選ぶ」で解決する。
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

// ─── Y 照合（I3.2）──────────────────────────────────────────────────────────

/// 「そのテクセルには面が無い（基準 Y が未知）」ことを表す番兵値。
///
/// 有限値なら必ず実在の面の高さ、という不変条件にするため、非有限値を選ぶ。
/// 判定は常に `is_finite()` で行う（-∞ との差は必ず ∞ になり、どの閾値も超える）。
pub const COVER_BASE_Y_ABSENT: f32 = f32::NEG_INFINITY;

/// Y 照合の許容差をチャンク 1 辺から決める比率。
///
/// 【なぜチャンクサイズに比例させるのか（固定値ではない理由）】
///   Y 照合の目的は「上下に積まれたチャンクのうち、どの段の面を読むか」を
///   ワールド位置だけで一意に決めることである。段の間隔はチャンク 1 辺（既定 16m）
///   なので、許容差がそれ未満であれば **1 つ上／1 つ下の段の面は必ず除外される**。
///   除外が保証されると、境界上の複製頂点を焼く 2 つのチャンクが
///   （見えている近傍の集合は違うのに）**同じ候補集合**へ行き着く
///   ＝どちらから焼いても厳密に同じ値になる（bit 一致）。
///   0.5 なら既定で 8m。人が歩く地形の段差としては十分に緩い。
pub const COVER_Y_MATCH_TOLERANCE_RATIO: f32 = 0.5;

/// チャンク 1 辺から Y 照合の許容差（メートル）を求める。
///
/// 焼き込み側・スタンプ側の双方がこの 1 関数を通ることで、
/// 「どこから読んでも同じ面が選ばれる」規約が 1 か所に固定される。
#[inline]
pub fn cover_y_match_tolerance(chunk_extent: f32) -> f32 {
    if !(chunk_extent > 0.0) || !chunk_extent.is_finite() {
        return 0.0;
    }
    chunk_extent * COVER_Y_MATCH_TOLERANCE_RATIO
}

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
#[derive(Clone, Debug)]
pub struct CoverField {
    /// 各テクセルの素材添字（`CoverMaterialSet` の添字）。量 0 のときは無意味。
    material: Vec<u8>,
    /// 各テクセルの量（0..255 で 0.0..1.0 を表す）。
    amount: Vec<u8>,
    /// 各テクセルの踏み固め深さ（0..255 で 0.0..1.0。量と同じ単位。I3.2）。
    ///
    /// 実効の盛り上がりは `max(量 - trample, 0)`。量を超えた分は下地の露出を意味する。
    trample: Vec<u8>,
    /// 各テクセルの「積もっている面」のワールド Y（メートル。I3.2）。
    ///
    /// 面が無ければ `COVER_BASE_Y_ABSENT`。**量を持つテクセルでは必ず有限値**
    /// という不変条件を積算側（`accumulate_chunk`）が守る。
    base_y: Vec<f32>,
}

/// カバー場の等価性は「素材・量・踏み固め」で判定する（基準 Y は含めない）。
///
/// 【なぜ base_y を比べないのか】
///   base_y は密度場（地形）から導かれる派生値であって、ユーザーの編集結果ではない。
///   Undo の差分抽出（`diff_cover_fields`）がこの `PartialEq` を使うため、
///   base_y を含めると「地形を掘り直しただけ」で全チャンクが Undo 差分に載ってしまう。
impl PartialEq for CoverField {
    fn eq(&self, other: &Self) -> bool {
        self.material == other.material
            && self.amount == other.amount
            && self.trample == other.trample
    }
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
            trample: vec![0u8; COVER_FIELD_TEXELS],
            base_y: vec![COVER_BASE_Y_ABSENT; COVER_FIELD_TEXELS],
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

    /// 生の踏み固め配列（永続化で使用）。長さは常に `COVER_FIELD_TEXELS`。
    pub fn raw_trample(&self) -> &[u8] {
        &self.trample
    }

    /// 生の基準 Y 配列（永続化で使用）。長さは常に `COVER_FIELD_TEXELS`。
    pub fn raw_base_y(&self) -> &[f32] {
        &self.base_y
    }

    /// 生の配列からカバー場を復元する（永続化からの読み戻し）。
    ///
    /// 長さが `COVER_FIELD_TEXELS` と一致しない配列が 1 つでもあれば `None`
    /// （壊れたファイルを黙って読まない）。
    ///
    /// TCOVER v1（踏み固め・基準 Y を持たない旧ファイル）は、読み込み側が
    /// 「trample = 全 0 / base_y = 全て `COVER_BASE_Y_ABSENT`」を渡して移行する。
    /// 基準 Y はロード後に地表情報から再計算される（`refresh_base_y`）。
    pub fn from_raw(
        material: Vec<u8>,
        amount: Vec<u8>,
        trample: Vec<u8>,
        base_y: Vec<f32>,
    ) -> Option<Self> {
        if material.len() != COVER_FIELD_TEXELS
            || amount.len() != COVER_FIELD_TEXELS
            || trample.len() != COVER_FIELD_TEXELS
            || base_y.len() != COVER_FIELD_TEXELS
        {
            return None;
        }
        Some(Self { material, amount, trample, base_y })
    }

    /// 全テクセルの量と踏み固めが 0 か（＝保存する価値が無い＝描画にも一切影響しない）。
    ///
    /// 踏み固めも見るのは、量が 0 でも轍だけが残っている状態
    /// （下地が露出したまま雪が完全に削れた場合）をファイル削除で失わないため。
    pub fn is_empty(&self) -> bool {
        self.amount.iter().all(|&a| a == 0) && self.trample.iter().all(|&t| t == 0)
    }

    /// 全テクセルを消去する（量 0・素材なし・轍なし・基準 Y 未知）。
    pub fn clear(&mut self) {
        self.material.fill(COVER_MATERIAL_NONE);
        self.amount.fill(0);
        self.trample.fill(0);
        self.base_y.fill(COVER_BASE_Y_ABSENT);
    }

    /// 指定テクセルの量（0..1）を返す。踏み固めは引かれていない「積もった総量」。
    pub fn amount_at(&self, ix: usize, iz: usize) -> f32 {
        self.amount[texel_index(ix, iz)] as f32 / COVER_AMOUNT_QUANT_MAX
    }

    /// 指定テクセルの踏み固め深さ（0..1。量と同じ単位）を返す。
    pub fn trample_at(&self, ix: usize, iz: usize) -> f32 {
        self.trample[texel_index(ix, iz)] as f32 / COVER_AMOUNT_QUANT_MAX
    }

    /// 指定テクセルの基準 Y（面のワールド Y）。面が無ければ `COVER_BASE_Y_ABSENT`。
    pub fn base_y_at(&self, ix: usize, iz: usize) -> f32 {
        self.base_y[texel_index(ix, iz)]
    }

    /// 指定テクセルの基準 Y を設定する（積算・面情報の同期から呼ばれる）。
    pub fn set_base_y(&mut self, ix: usize, iz: usize, y: f32) {
        self.base_y[texel_index(ix, iz)] = y;
    }

    /// 地表情報から基準 Y を丸ごと再計算する（TCOVER v1 の移行・地形編集後の同期）。
    ///
    /// 面が無いテクセルは `COVER_BASE_Y_ABSENT` になる。そのテクセルに量が
    /// 残っていても描画では「面が無い」として読み飛ばされる
    /// （地形を掘って面ごと消えた場所の雪は見えなくなる、という素直な挙動）。
    pub fn refresh_base_y(&mut self, surface: &CoverSurface) {
        for iz in 0..COVER_FIELD_RESOLUTION {
            for ix in 0..COVER_FIELD_RESOLUTION {
                let i = texel_index(ix, iz);
                self.base_y[i] = if surface.has_surface(ix, iz) {
                    surface.surface_y_at(ix, iz)
                } else {
                    COVER_BASE_Y_ABSENT
                };
            }
        }
    }

    /// 指定テクセルへ踏み固めを押し当てる（I3.2 の轍スタンプ）。
    ///
    /// 【なぜ加算ではなく最大値なのか】
    ///   加算だと同じ場所に立ち続けるだけで穴が掘れてしまい、
    ///   「1 回踏んだ足跡の深さ」という直感から外れる。最大値で更新すれば
    ///   何度踏んでも形状の深さは一定に収束し、順序にも依存しない
    ///   （複数のソースが同じテクセルを踏んでも結果が決定的）。
    ///
    /// 戻り値は実際に値が変化したか。`depth` が 0 以下・非有限なら何もしない。
    pub fn stamp_trample(&mut self, ix: usize, iz: usize, depth: f32) -> bool {
        if !depth.is_finite() || depth <= 0.0 {
            return false;
        }
        let i = texel_index(ix, iz);
        let q = quantize_amount(depth);
        if q <= self.trample[i] {
            return false;
        }
        self.trample[i] = q;
        true
    }

    /// 指定テクセルの踏み固めを `delta` だけ埋め戻す（0 で下げ止まる）。
    ///
    /// 戻り値は実際に値が変化したか。
    pub fn refill_trample(&mut self, ix: usize, iz: usize, delta: f32) -> bool {
        if !delta.is_finite() || delta <= 0.0 {
            return false;
        }
        let i = texel_index(ix, iz);
        if self.trample[i] == 0 {
            return false;
        }
        let current = self.trample[i] as f32 / COVER_AMOUNT_QUANT_MAX;
        let next = quantize_amount(current - delta);
        if next == self.trample[i] {
            return false;
        }
        self.trample[i] = next;
        true
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
            let next = quantize_amount(current + delta);
            // ─── 量が 0 のままなら素材添字も書き換えない ───
            //   【なぜ必要か（毎フレーム焼き直しの原因だった）】
            //     `delta` が量子化の粒（1/255）に届かないと量は 0 のまま動かないのに、
            //     ここで素材添字だけが「素材なし（255）→ このエミッタの素材」へ変わる。
            //     呼び出し側（`accumulate_chunk`）は素材添字の変化も「変化あり」と見なすため、
            //     **見た目が 1 ピクセルも変わらないのにチャンクが焼き直し待ちへ載る**。
            //     さらに素材の違うエミッタが 2 つ同じテクセルへ届いていると、
            //     量 0 のまま添字が交互に書き換わり続けて永久に「変化あり」が立つ。
            //   量 0 のテクセルは色にも変位にも一切寄与しない（`CoverNeighborhood` が
            //   量 0 を必ず「何も無い」として読む）ので、添字を据え置いて実害は無い。
            if next == 0 {
                return;
            }
            self.material[i] = material_index;
            self.amount[i] = next;
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

    // ─── 手編集（カバーブラシ。地形編集モードの消しゴム／塗り）─────────────────

    /// 指定テクセルのカバーを `delta`（量/正規化）ぶん削る（消しゴムブラシ）。
    ///
    /// 【量と踏み固めを同時に削る理由】
    ///   このテクセルの「見えているもの」は量（盛り上がり）と踏み固め（轍の窪み）の
    ///   2 チャネルであり、`is_empty()` はその両方が 0 のときだけ真になる。
    ///   量だけを削ると轍だけが残り、消しゴムでなぞったのに `.tcover` が
    ///   消えない（＝「量 0 のチャンクはファイルを削除する」不変条件へ届かない）。
    ///   ブラシの意味は「そこにあるカバーを無かったことにする」なので、
    ///   同じ強さで両方を削るのが素直である。
    ///
    /// 【量が 0 になっても素材添字を消さない理由（黒落ちバグの修正）】
    ///   以前はここで `COVER_MATERIAL_NONE` へ戻していたが、これは 2 つの意味で害だった。
    ///   ① 目的が達成できていない —— 「消した場所へ別素材を塗ると
    ///      異素材の置き換え規則（削ってから積む）に入って 1 ストローク損をする」
    ///      のを避けるためだったが、`deposit` / `paint` の置き換え規則は
    ///      どちらも **`amount != 0` を前提**にしており、量 0 のテクセルは
    ///      素材添字に関わらず素直な上書きになる。つまり消す必要が最初から無い。
    ///   ② 消すと描画が壊れる —— 素材添字は頂点属性（uv0.y）としてラスタライザが
    ///      **線形補間**する。消した所だけ添字が別の値へ飛ぶと、消した領域の縁で
    ///      「塗った素材と無関係な素材」（たとえば添字 3 と 0 の中間＝添字 2）が
    ///      解決され、その素材の色（濡れ・泥のような暗い色）が地表へ混ざって
    ///      黒い縁が出る。消しても素材添字を保つと、消した領域と残っている領域で
    ///      添字が一致し、補間しても常に同じ素材が選ばれる。
    ///
    /// 量が 0 のテクセルは `CoverNeighborhood` の読み取りで必ず「量 0」として扱われるため、
    /// 素材添字が残っていても色や変位には一切寄与しない。
    ///
    /// 戻り値は実際に値が変化したか。`delta` が 0 以下・非有限なら何もしない。
    pub fn erase(&mut self, ix: usize, iz: usize, delta: f32) -> bool {
        if !delta.is_finite() || delta <= 0.0 {
            return false;
        }
        let i = texel_index(ix, iz);
        let mut changed = false;

        // ─── 積もった量を削る ───
        if self.amount[i] != 0 {
            let current = self.amount[i] as f32 / COVER_AMOUNT_QUANT_MAX;
            let next = quantize_amount(current - delta);
            if next != self.amount[i] {
                self.amount[i] = next;
                changed = true;
            }
        }

        // ─── 踏み固め（轍）も同じ強さで削る ───
        if self.trample[i] != 0 {
            let current = self.trample[i] as f32 / COVER_AMOUNT_QUANT_MAX;
            let next = quantize_amount(current - delta);
            if next != self.trample[i] {
                self.trample[i] = next;
                changed = true;
            }
        }

        // 素材添字は **消さない**（上のコメント参照）。
        changed
    }

    /// 指定テクセルの量を `target`（目標量 0..1）へ `delta` ぶん近づける（塗りブラシ）。
    ///
    /// 【なぜ「加算」ではなく「目標へ近づける」なのか】
    ///   エミッタの積算（`deposit`）は時間で単調に増えるので上限は飽和（1.0）でよいが、
    ///   手で敷く雪は「このくらいの厚みにしたい」という指定である。目標量を持たせて
    ///   両方向から近づけることで、なぞった場所の厚みがストローク回数に依らず
    ///   目標値へ収束する（＝何往復しても同じ絵になる）。
    ///
    /// 【異素材の扱いは `deposit` と同じ規則】
    ///   違う素材が乗っているテクセルでは、まず古い素材を `delta` ぶん削る。
    ///   削り切ってから新素材が乗り始める（落ち葉の上に雪を敷く手触りが
    ///   エミッタで降らせた場合と一致する）。
    ///
    /// 戻り値は実際に値が変化したか。`delta` が 0 以下・非有限なら何もしない。
    pub fn paint(&mut self, ix: usize, iz: usize, material_index: u8, target: f32, delta: f32) -> bool {
        if !delta.is_finite() || delta <= 0.0 {
            return false;
        }
        // 目標量は 0..1 の外・NaN を許さない（量チャネルの定義域）。
        let target = if target.is_finite() {
            target.clamp(COVER_AMOUNT_MIN, COVER_AMOUNT_MAX)
        } else {
            return false;
        };
        let i = texel_index(ix, iz);
        let current = self.amount[i] as f32 / COVER_AMOUNT_QUANT_MAX;

        // ─── 異素材が乗っている: まず古い素材を削る（`deposit` と同じ置き換え規則）───
        if self.amount[i] != 0 && self.material[i] != material_index {
            let next = if current > delta {
                // 古い素材がまだ残る（素材は変えない）。
                quantize_amount(current - delta)
            } else {
                // 削り切った。新素材へ切り替え、余りを初期量にする（目標量で頭打ち）。
                self.material[i] = material_index;
                quantize_amount((delta - current).min(target))
            };
            if next == self.amount[i] {
                return false;
            }
            self.amount[i] = next;
            return true;
        }

        // ─── 同素材（または空のテクセル）: 目標量へ `delta` ぶん近づける ───
        let next_value = if current < target {
            (current + delta).min(target)
        } else if current > target {
            (current - delta).max(target)
        } else {
            // 既に目標量ちょうど＝これ以上塗っても何も変わらない。
            return false;
        };
        let next = quantize_amount(next_value);
        // 量が 0 のまま動かないテクセルへ素材だけ書き込まない
        // （量 0 の素材添字は無意味であり、書くと Undo の差分だけが増える）。
        if next == 0 && self.amount[i] == 0 {
            return false;
        }
        let material_changed = self.material[i] != material_index;
        self.material[i] = material_index;
        if next == self.amount[i] {
            return material_changed;
        }
        self.amount[i] = next;
        true
    }
}

// ============================================================
//  CoverNeighborhood — チャンクを跨いで読むカバー場ビュー（3×3）
// ============================================================

/// テクセル中心の位置（テクセル格子の左下からの割合）。
///
/// テクセル ix の中心は正規化座標で `(ix + 0.5) / R`。逆に解くと `fx = u*R - 0.5` になる。
const COVER_TEXEL_CENTER_OFFSET: f32 = 0.5;

/// 自チャンクと **XZ 方向の隣接 8 チャンク**のカバー場をまとめた読み取り専用ビュー。
///
/// 【なぜ 1 チャンク単体で読んではいけないのか（チャンク境界の段差・隙間）】
///   チャンク境界の上にある地形メッシュの頂点は、隣り合う 2 チャンク
///   （角では 4 チャンク）のメッシュへ **複製**されている。
///   各メッシュが自分のカバー場だけを「端でクランプして」読むと、
///   物理的に同じ 1 点なのに、チャンク A では端のテクセル・チャンク B では
///   反対端のテクセルという **別の値**を読む。変位（盛り上げ）は頂点位置へ焼くので、
///   食い違った瞬間に複製頂点が別々の場所へ動き、**メッシュに隙間が開く**。
///
///   そこで読み取りをチャンク横断にし、境界上の頂点が
///   **どのチャンクのメッシュから読んでも同じ 4 テクセル・同じ重み・同じ演算順序**に
///   なるようにする。結果は f32 のビット単位で一致する
///   （回帰テスト `boundary_sample_is_bit_identical_between_neighbours`）。
///
///   これは水面グリッド（W5.1）で境界線の共有点を厳密に一致させたのと同じ原則である
///   ＝「共有される点は、どちらの所有者から見ても厳密に同じ値を読む」。
///
/// 【隣接チャンクにカバー場が無い場合】
///   **量 0（素材なし）として読む**。「まだ雪が降っていない隣のチャンク」と
///   「そもそもチャンクが存在しない世界の端」を区別しない。どちらも 0 なので
///   両側から読んだ値は必ず一致し、境界が破綻することはない
///   （世界の端では最後の半テクセルぶんだけカバーがなだらかに 0 へ落ちる）。
/// 【Y（上下）方向も 3 段を見る理由（I3.2 で 3×3 → 3×3×3 へ拡張）】
///   横（XZ）の境界が一致しても、地表がちょうど Y のチャンク境界面を横切る場所では
///   「面が上段にある列」と「下段にある列」が隣り合う。その境界面上の複製頂点は
///   上下段で別のカバー場を読むため、変位が食い違って筋が出ていた（I3.1 §7-8）。
///
///   そこで、各テクセルが持つ **基準 Y（面のワールド高さ）** と、読みたい点の
///   ワールド Y とを照合し、**最も近い段の面**を選ぶ。選び方はワールド位置だけの
///   純関数なので、上段のメッシュから読んでも下段のメッシュから読んでも同じ段が選ばれる。
///   許容差（`cover_y_match_tolerance`）はチャンク 1 辺の半分であり、
///   1 つ以上離れた段の面は必ず除外される
///   ＝両側で候補集合が実質的に一致し、bit 単位で同じ結果になる。
pub struct CoverNeighborhood<'a> {
    /// `[dy + 1][dz + 1][dx + 1]` の 3×3×3。中央 `[1][1][1]` が自チャンク。
    /// `None` はカバー場なし（＝量 0）。
    fields: [[[Option<&'a CoverField>; 3]; 3]; 3],
    /// Y 照合の許容差（メートル）。これを超えて離れた面は候補にしない。
    y_tolerance: f32,
}

/// カバー場を 1 点で読んだ結果（I3.2）。
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct CoverSample {
    /// 実効の量（0..1）。`max(積もった量 - 踏み固め, 0)` のバイリニア補間。
    ///
    /// 描画はこの値だけを見る（盛り上がりも色の混ぜ具合もこれで決まる）。
    pub amount: f32,
    /// 踏み固め深さ（0..1）のバイリニア補間。踏まれた部分を暗くするために使う。
    pub trample: f32,
    /// 素材添字（`CoverMaterialSet` の添字）。実効量 0 のときは `COVER_MATERIAL_NONE`。
    pub material: u8,
    /// 踏み固めの色係数を引くための素材添字。踏み固め 0 のときは `COVER_MATERIAL_NONE`。
    ///
    /// 【なぜ `material` と別に持つのか】
    ///   踏み固めが量を超えたテクセルは「下地が露出した」状態であり、
    ///   `material` は `COVER_MATERIAL_NONE` になる（素材色を塗ってはいけない）。
    ///   しかし轍として **暗くする**ためには、そこを踏み固めた素材が何だったかが要る。
    ///   2 つを分けることで、「色は塗らないが轍としては暗い」を表現できる。
    pub trample_material: u8,
    /// **頂点属性へ焼くための**素材添字（`material` の補間安全版）。
    ///
    /// 【なぜ `material` と別に要るのか（黒落ちバグの本体）】
    ///   素材添字は頂点属性（uv0.y）としてラスタライザが**線形補間**する。
    ///   実効量 0 の点で `material` を `COVER_MATERIAL_NONE` に落とすと、
    ///   カバーの縁（量が 0 へ落ちていく帯）で添字が「塗った素材 → なし」へ
    ///   ぐらつき、その中間値が**塗っていない別素材**として解決される。
    ///   濡れ・泥のような暗い素材が中間に居ると、縁が黒く沈む。
    ///
    ///   そこでこの値は「実効量 0 の点でも、近傍で実際に使われている素材添字」を
    ///   返す。カバーの内側から縁の外側まで添字が同じ値で埋まるため、
    ///   補間しても常に同じ素材が選ばれる。量（uv0.x）のほうは滑らかに 0 へ
    ///   落ちるので、見た目は従来どおりなめらかなまま黒縁だけが消える。
    ///
    ///   近傍にカバーが 1 つも無ければ `COVER_MATERIAL_NONE`（＝素材なし）。
    pub blend_material: u8,
}

impl<'a> CoverNeighborhood<'a> {
    /// 隣接チャンクを持たない単体ビューを作る。
    ///
    /// カバー場が 1 個しか無い場面（単体テスト・チャンクが 1 個だけの世界）用。
    /// 端のテクセルの外は「量 0」として読まれる。
    /// Y 照合の許容差は実質無限（＝どの高さから読んでも中央の面が選ばれる）。
    pub fn isolated(center: &'a CoverField) -> Self {
        Self::from_lookup(f32::INFINITY, |dx, dy, dz| {
            if dx == 0 && dy == 0 && dz == 0 {
                Some(center)
            } else {
                None
            }
        })
    }

    /// 近傍参照関数からビューを組み立てる。
    ///
    /// `lookup(dx, dy, dz)` は -1..=1 のチャンクオフセットに対するカバー場を返す
    /// （無ければ `None`）。`y_tolerance` は Y 照合の許容差（メートル）で、
    /// 呼び出し側は必ず `cover_y_match_tolerance(chunk_extent)` を使うこと
    /// （両隣のチャンクが違う値を使うと境界の一致が壊れる）。
    pub fn from_lookup(
        y_tolerance: f32,
        mut lookup: impl FnMut(i32, i32, i32) -> Option<&'a CoverField>,
    ) -> Self {
        let mut fields: [[[Option<&'a CoverField>; 3]; 3]; 3] = [[[None; 3]; 3]; 3];
        for dy in -1..=1i32 {
            for dz in -1..=1i32 {
                for dx in -1..=1i32 {
                    fields[(dy + 1) as usize][(dz + 1) as usize][(dx + 1) as usize] =
                        lookup(dx, dy, dz);
                }
            }
        }
        Self { fields, y_tolerance }
    }

    /// 自チャンクローカルの正規化 XZ 座標（0..1）と **ワールド Y** でカバー場を読む。
    ///
    /// - 量・踏み固めは **バイリニア補間**（頂点へ載せたときに階段状にならない）。
    ///   境界（u=0 / u=1）では 4 隅の半分が隣チャンクのテクセルになる。
    /// - 4 隅それぞれについて、上下 3 段のうち基準 Y が `world_y` に最も近い段を選ぶ
    ///   （許容差を超える段・面が無い段は「量 0」として扱う）。
    /// - 素材は「重み × 実効量」が最大のテクセルのもの（＝実質的に最近傍だが、
    ///   **量 0 のテクセルは決して選ばない**）。添字は補間できないので離散選択にする。
    ///
    /// 範囲外・非有限の UV は 0..1 へクランプする（頂点は必ず 0..1 に収まる契約）。
    pub fn sample(&self, u: f32, v: f32, world_y: f32) -> CoverSample {
        self.sample_extended(clamp01(u), clamp01(v), world_y)
    }

    /// `sample` の **クランプしない**版。自チャンクの外（0..1 の外側）も読める。
    ///
    /// 【何のために要るのか（法線の勾配計算）】
    ///   カバーの盛り上がりから法線を作るには、頂点の周りを ±半テクセルずらして
    ///   高さを読む必要がある。ここでクランプしてしまうと、チャンク境界上の頂点は
    ///   自チャンク側にしか踏み出せず、**同じ 1 点なのに隣のメッシュとは違う勾配**
    ///   （＝違う法線）になり、境界に照明の筋が出る。
    ///   クランプせずに読めば、隣接チャンクのテクセルを実際に読みに行くため、
    ///   複製された頂点はどちらのメッシュから見ても同じ 4 テクセル・同じ重みへ
    ///   行き着き、法線まで bit 単位で一致する（位置について §2.6 が保証しているのと同じ理屈）。
    ///
    /// 3×3 ビューの外（自チャンクから 1 チャンク以上離れた位置）は「量 0」として読まれる。
    /// 非有限な UV は 0 として扱う（`clamp01` と同じ縮退）。
    pub fn sample_extended(&self, u: f32, v: f32, world_y: f32) -> CoverSample {
        let u = if u.is_finite() { u } else { 0.0 };
        let v = if v.is_finite() { v } else { 0.0 };
        // ─── テクセル中心を基準にした連続座標へ変換する（クランプしない）───
        //   u=1.0（チャンクの端）では fx = R-0.5 となり、x1 = R すなわち
        //   隣チャンクのテクセル 0 を指す。隣から見た u=0.0 は fx = -0.5 で
        //   x0 = -1（＝こちらのテクセル R-1）・x1 = 0 となり、
        //   **同じ 2 テクセル・同じ tx=0.5** に行き着く。
        let r = COVER_FIELD_RESOLUTION as f32;
        let fx = u * r - COVER_TEXEL_CENTER_OFFSET;
        let fz = v * r - COVER_TEXEL_CENTER_OFFSET;
        let fx0 = fx.floor();
        let fz0 = fz.floor();
        let tx = fx - fx0;
        let tz = fz - fz0;
        let (x0, z0) = (fx0 as i32, fz0 as i32);
        let (x1, z1) = (x0 + 1, z0 + 1);

        // ─── 4 隅を読む（それぞれ Y 照合で段を選ぶ）───
        //   列挙順は常に (x0,z0) → (x1,z0) → (x0,z1) → (x1,z1)。
        //   x0/z0 は必ずグローバルに小さい側なので、どのチャンクから読んでも
        //   同じ順序＝同点時の選び方まで一致する（境界での bit 一致に必要）。
        let c00 = self.texel_at_global(x0, z0, world_y);
        let c10 = self.texel_at_global(x1, z0, world_y);
        let c01 = self.texel_at_global(x0, z1, world_y);
        let c11 = self.texel_at_global(x1, z1, world_y);

        // ─── 重み（バイリニア）───
        let w00 = (1.0 - tx) * (1.0 - tz);
        let w10 = tx * (1.0 - tz);
        let w01 = (1.0 - tx) * tz;
        let w11 = tx * tz;

        // ─── 実効量・踏み固めを補間する ───
        //   演算順序を固定するため、必ず「行方向 → 列方向」でまとめる
        //   （浮動小数の加算は結合則を満たさないので、順序が違うと bit 一致しない）。
        let amount = (c00.amount * (1.0 - tx) + c10.amount * tx) * (1.0 - tz)
            + (c01.amount * (1.0 - tx) + c11.amount * tx) * tz;
        let trample = (c00.trample * (1.0 - tx) + c10.trample * tx) * (1.0 - tz)
            + (c01.trample * (1.0 - tx) + c11.trample * tx) * tz;

        // ─── 素材は「重み × 実効量」が最大のテクセルから採る ───
        let candidates = [
            (w00, c00),
            (w10, c10),
            (w01, c01),
            (w11, c11),
        ];
        let mut best_amount = 0.0f32;
        let mut material = COVER_MATERIAL_NONE;
        let mut best_trample = 0.0f32;
        let mut trample_material = COVER_MATERIAL_NONE;
        // 補間安全な添字（`blend_material`）は「実効量が 0 でも、そのテクセルが
        // 記憶している素材」から選ぶ。走査順は上の 4 隅の列挙順に固定してあるので、
        // 同点時の選び方までチャンク横断で一致する（境界の bit 一致に必要）。
        let mut blend_material = COVER_MATERIAL_NONE;
        for &(w, c) in &candidates {
            if w * c.amount > best_amount {
                best_amount = w * c.amount;
                material = c.material;
            }
            // 踏み固めの色係数用は「重み × 踏み固め」で選ぶ
            // （実効量が 0 でも轍として暗くしたいので、別の基準が要る）。
            if w * c.trample > best_trample {
                best_trample = w * c.trample;
                trample_material = c.trample_material;
            }
            // 記憶している素材のうち最初に見つかったものを採る
            // （実効量で選び直す必要は無い。目的は「縁で添字がぐらつかない」ことだけ）。
            if blend_material == COVER_MATERIAL_NONE {
                blend_material = c.blend_material;
            }
        }
        // 実効量がある点では、実際に描かれる素材と補間用の添字を必ず一致させる
        // （両者がずれると、その点だけ隣と違う素材が選ばれて縁が出る）。
        if material != COVER_MATERIAL_NONE {
            blend_material = material;
        }
        CoverSample { amount, trample, material, trample_material, blend_material }
    }

    /// 3×3 を跨ぐテクセル座標を、Y 照合で選んだ段から読む。
    ///
    /// 該当チャンクが無い／面が無い／どの段も許容差の外なら「量 0・踏み固め 0・素材なし」。
    fn texel_at_global(&self, gx: i32, gz: i32, world_y: f32) -> CoverSample {
        let empty = CoverSample {
            amount: 0.0,
            trample: 0.0,
            material: COVER_MATERIAL_NONE,
            trample_material: COVER_MATERIAL_NONE,
            blend_material: COVER_MATERIAL_NONE,
        };
        let (Some((cx, ix)), Some((cz, iz))) = (split_global_texel(gx), split_global_texel(gz))
        else {
            return empty;
        };

        // ─── 上下 3 段のうち、基準 Y が world_y に最も近い段を選ぶ ───
        //   走査順は必ず下段 → 上段（dy 昇順）で、更新は厳密不等号。
        //   こうすると同着（上下の面が world_y から等距離）でも常に下段が勝ち、
        //   どのチャンクから読んでも同じ段に行き着く。
        let mut best: Option<(f32, &CoverField)> = None;
        let mut any_base_known = false;
        for dy in 0..3usize {
            let Some(field) = self.fields[dy][cz][cx] else { continue };
            let base = field.base_y_at(ix, iz);
            if !base.is_finite() {
                continue;
            }
            any_base_known = true;
            let diff = (base - world_y).abs();
            if diff > self.y_tolerance {
                continue;
            }
            match best {
                Some((best_diff, _)) if diff >= best_diff => {}
                _ => best = Some((diff, field)),
            }
        }

        // ─── 基準 Y が 1 段も分かっていない列は I3.1 と同じ挙動へ縮退する ───
        //   TCOVER v1 を読み込んだ直後など、地表情報との同期がまだ済んでいない場合に
        //   「積もっているのに何も描かれない」状態にしないための保険。
        //   同期後（`refresh_base_y`）は必ず上の分岐で解決されるため、この縮退が
        //   実際に効くのは移行の一瞬だけである。
        let field = match best {
            Some((_, f)) => f,
            None if !any_base_known => match self.fields[1][cz][cx] {
                Some(f) => f,
                None => return empty,
            },
            // 基準 Y は分かっているのにどの段も許容差の外＝この点の面ではない。
            None => return empty,
        };

        // ─── 実効量 = 積もった量 - 踏み固め（0 でクランプ）───
        let amount = field.amount_at(ix, iz);
        let trample = field.trample_at(ix, iz);
        let effective = (amount - trample).max(0.0);
        let stored_material = field.material_at(ix, iz);
        CoverSample {
            amount: effective,
            trample,
            // 実効量が 0 のテクセルは「素材なし」と同じに扱う
            // （踏み固めで下地が露出した所へ素材色を塗らないため）。
            material: if effective > 0.0 { stored_material } else { COVER_MATERIAL_NONE },
            // 轍の色係数は「踏み固めた素材」から引く（露出後も轍として暗い）。
            trample_material: if trample > 0.0 { stored_material } else { COVER_MATERIAL_NONE },
            // 補間用は実効量に関わらず「記憶している素材」をそのまま返す
            // （消しゴムで量が 0 になったテクセルも素材添字を保持している）。
            blend_material: stored_material,
        }
    }
}

/// 自チャンク基準のテクセル座標を `(近傍添字 0..3, チャンク内テクセル 0..R)` へ分解する。
///
/// 負値も正しく扱うため除算は `div_euclid` / `rem_euclid` を使う
/// （`/` と `%` は 0 方向へ丸めるので -1 が 0 になってしまう）。
#[inline]
fn split_global_texel(g: i32) -> Option<(usize, usize)> {
    let r = COVER_FIELD_RESOLUTION as i32;
    let chunk_offset = g.div_euclid(r);
    if !(-1..=1).contains(&chunk_offset) {
        return None;
    }
    Some(((chunk_offset + 1) as usize, g.rem_euclid(r) as usize))
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
                //   規約は **マーチングキューブと厳密に同じ**にする:
                //     density <  iso ⇒ SOLID
                //     density >= iso ⇒ AIR
                //
                //   【なぜ等号の置き場所が重要か（チャンク境界の不整合）】
                //     密度がちょうど iso のサンプルを「個体」側に数えると、
                //     カバー場が面を見つけるチャンクと、実際にメッシュ（セル）を
                //     生成するチャンクが **1 段ずれる**。
                //     既定の平坦地面（density = world_y）は面がちょうど y=0 に来るため、
                //     メッシュは y=-16..0 のチャンクが持つのに、面情報は y=0..16 の
                //     チャンク（実際には全て空気）が持つことになり、
                //     積もった雪がどこにも焼き込まれない（見えない）。
                //     マーチングキューブ側は `density < iso` を個体としているので、
                //     ここも同じ不等号にすることで
                //     「面を持つチャンク＝そのセルのメッシュを出すチャンク」が常に一致する。
                let mut found: Option<(usize, f32)> = None;
                for iy in (1..samples).rev() {
                    let upper = chunk.sample(sx, iy, sz);
                    let lower = chunk.sample(sx, iy - 1, sz);
                    if upper >= iso && lower < iso {
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
