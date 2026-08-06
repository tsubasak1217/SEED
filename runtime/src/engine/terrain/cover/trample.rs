// ============================================================
//  terrain/cover/trample.rs — 轍・足跡のスタンプ（純粋関数。Phase I3.2）
//
//  【責務】
//    「動く物が接地した」という 1 回の出来事を、カバー場の踏み固めチャネル
//    （`CoverField::stamp_trample`）へ押し当てる。
//    ECS も GPU も知らない。エンジン層（app/terrain_cover_ops.rs）が
//    `InteractionSourceComponent` ＋ Transform から `CoverStampSpec` を組み立て、
//    接地しうるチャンクにだけ本ファイルの関数を呼ぶ。
//
//  【なぜ積算（accumulate.rs）と別ファイルなのか】
//    駆動する事象がまったく違う。積算は「時間が経つと降る」＝ dt 駆動で全チャンク
//    一様に走るのに対し、スタンプは「物が動いた瞬間」＝ イベント駆動で
//    接地点の周囲 1〜2 チャンクにしか触らない。頻度もコストも別物なので、
//    早期棄却の作りも別に持たせるほうが素直である。
//
//  【Y 照合（この機能の要）】
//    カバー場は XZ の 2 次元なので、真上から押すだけでは
//    「洞窟の床を歩いた轍が、頭上の地表にも付く」ことになる。
//    そこでスタンプ側は接地点のワールド Y を持ち、テクセルの基準 Y
//    （`CoverField::base_y_at` ＝ その面のワールド高さ）と許容差の内側で
//    一致するテクセルにだけ適用する。
//
//  【形状（Circle / Texture）】
//    Circle  … 半径ベースの等方な窪み（既定）。人の足跡程度ならこれで足りる。
//    Texture … グレースケール画像を「進行方向へ回転させて」押し当てる。
//              タイヤのトレッド・ブーツの靴底など、向きを持つ痕を作るための形状。
// ============================================================

use super::emit::CoverMask;
use super::field::{
    slope_scale, texel_center_uv, CoverField, CoverSurface, COVER_FIELD_RESOLUTION,
};
use super::material::CoverMaterialSet;

// ─── 定数（マジックナンバー禁止）─────────────────────────────────────────────

/// 進行方向として採用できる速度の下限（m/s）。
///
/// これ未満の動きはノイズ（数値誤差・微振動）とみなし、向きを更新しない
/// ＝直前の向きを保つ。止まった瞬間に痕の向きがくるくる回るのを防ぐ。
pub const COVER_STAMP_DIRECTION_MIN_SPEED: f32 = 0.05;

/// 向きが未確定のときの既定の進行方向（ワールド XZ）。
///
/// 一度も動いていないソース（Play 開始直後にその場で踏んだ場合）に使う。
/// エンジンの前方が +Z なので、モデルの正面と痕の向きが自然に揃う。
pub const COVER_STAMP_DEFAULT_FORWARD: [f32; 2] = [0.0, 1.0];

/// Y 照合の許容差の下限（メートル）。
///
/// 許容差はソース半径から作る（大きな獣ほど上下に余裕を持って接地判定する）が、
/// 半径が極端に小さいソースでも「1 テクセルの起伏」ぶんは許さないと
/// 平地を歩いても一切痕が付かなくなる。
pub const COVER_STAMP_Y_TOLERANCE_MIN: f32 = 0.25;

/// 踏み固めが効き始める傾斜（`slope_scale` の値）の下限。
///
/// 崖には積もらない（＝カバーが無い）ので踏む対象も無い。積算と同じ規則を
/// 使うことで「積もる面 ⇔ 踏める面」が常に一致する。
const COVER_STAMP_SLOPE_MIN: f32 = 0.0;

// ============================================================
//  CoverStampShape — 痕の形状
// ============================================================

/// 轍スタンプの形状（どんな形に踏み固めるか）。
#[derive(Clone, Debug, PartialEq)]
pub enum CoverStampShape {
    /// 等方な円（既定）。中心で最大・縁で 0 の滑らかな窪み。
    Circle,
    /// グレースケール画像を進行方向へ回転させて押し当てる。
    ///
    /// 白 = 最大の深さ、黒 = 踏まない。画像の **上方向（V=0 側）が進行方向**。
    Texture {
        /// 痕のローカルサイズ（メートル）。`[進行方向に直交する幅, 進行方向の長さ]`。
        size: [f32; 2],
        /// 進行方向（ワールド XZ の単位ベクトル）。
        forward_xz: [f32; 2],
        /// グレースケールマスク（`CoverMask` はエミッタのマスクと同じ型を使い回す）。
        mask: CoverMask,
    },
}

// ============================================================
//  CoverStampSpec — スタンプ 1 回ぶんの仕様
// ============================================================

/// 「動く物が 1 点で接地した」ことを表すスタンプ仕様（ワールド解決済み）。
#[derive(Clone, Debug, PartialEq)]
pub struct CoverStampSpec {
    /// 接地点のワールド座標。Y は **接地面の高さ**として Y 照合に使う。
    pub contact: [f32; 3],
    /// 影響半径（メートル）。`Circle` の半径であり、`Texture` でも AABB の余裕に使う。
    pub radius: f32,
    /// 踏み込みの強さ（0..1）。素材の「足跡の残りやすさ」と掛け合わせる。
    pub strength: f32,
    /// 形状。
    pub shape: CoverStampShape,
}

impl CoverStampSpec {
    /// Y 照合の許容差（メートル）。半径に比例させ、下限で底上げする。
    ///
    /// 【なぜ半径から作るのか】
    ///   接地点の Y はアクタの原点であり、足裏とは限らない（モデルによって
    ///   原点が腰にあることもある）。影響半径は「その物がどれだけの範囲に
    ///   触れているか」の宣言なので、上下方向の許容にもそのまま使うのが
    ///   データ 1 個で意図を表現できて素直である。
    pub fn y_tolerance(&self) -> f32 {
        self.radius.max(COVER_STAMP_Y_TOLERANCE_MIN)
    }

    /// このスタンプが XZ 方向に届く最大距離（メートル）。
    ///
    /// `Texture` はサイズの対角線の半分まで届きうる（どの向きに回転しても
    /// はみ出さない保守的な上界）。
    pub fn reach_xz(&self) -> f32 {
        match &self.shape {
            CoverStampShape::Circle => self.radius.max(0.0),
            CoverStampShape::Texture { size, .. } => {
                let half_diagonal = (size[0] * size[0] + size[1] * size[1]).sqrt() * 0.5;
                half_diagonal.max(self.radius.max(0.0))
            }
        }
    }

    /// このスタンプが指定 AABB（チャンクなど）に一切かからないか。
    ///
    /// Y は「接地点 ± 許容差」で判定する（別の段のチャンクを触らないため）。
    pub fn is_outside_aabb(&self, min: [f32; 3], max: [f32; 3]) -> bool {
        let reach = self.reach_xz();
        let ty = self.y_tolerance();
        self.contact[0] + reach < min[0]
            || self.contact[0] - reach > max[0]
            || self.contact[2] + reach < min[2]
            || self.contact[2] - reach > max[2]
            || self.contact[1] + ty < min[1]
            || self.contact[1] - ty > max[1]
    }

    /// 指定ワールド XZ 位置での踏み込み比率（0..1）を返す。
    ///
    /// 形状ごとの落ち方だけを決める純粋関数であり、素材の残りやすさ・強さは
    /// 呼び出し側が掛ける（責務の分離：`CoverEmitSpec::coverage_at` と同じ流儀）。
    pub fn footprint_at(&self, world_x: f32, world_z: f32) -> f32 {
        let dx = world_x - self.contact[0];
        let dz = world_z - self.contact[2];
        match &self.shape {
            // ─── 円: 中心 1.0・縁 0.0 の smoothstep ───
            CoverStampShape::Circle => {
                let r = self.radius;
                if !(r > 0.0) {
                    return 0.0;
                }
                let d = (dx * dx + dz * dz).sqrt();
                if d >= r {
                    return 0.0;
                }
                let t = 1.0 - d / r;
                // 両端で傾き 0 になる smoothstep（縁に硬い線が出ない）。
                t * t * (3.0 - 2.0 * t)
            }

            // ─── テクスチャ: 進行方向へ回した局所座標で画像を読む ───
            CoverStampShape::Texture { size, forward_xz, mask } => {
                let (sw, sl) = (size[0], size[1]);
                if !(sw > 0.0) || !(sl > 0.0) || !mask.is_valid() {
                    return 0.0;
                }
                // 進行方向（前方）と、その右手側（横方向）の 2 軸を作る。
                //   前方 f=(fx,fz) に対する右手は (fz, -fx)（左手系 Y-up の +X が右）。
                let (fx, fz) = (forward_xz[0], forward_xz[1]);
                let len = (fx * fx + fz * fz).sqrt();
                if !(len > 0.0) {
                    return 0.0;
                }
                let (fx, fz) = (fx / len, fz / len);
                let (rx, rz) = (fz, -fx);

                // 局所座標（横 = 右方向の成分・縦 = 前方向の成分）。
                let local_right = dx * rx + dz * rz;
                let local_forward = dx * fx + dz * fz;

                // 画像 UV へ。U は右方向、V は **前方が上（V=0）** になるよう反転する。
                let u = local_right / sw + 0.5;
                let v = 0.5 - local_forward / sl;
                mask.sample(u, v)
            }
        }
    }
}

// ============================================================
//  スタンプの適用（1 チャンク分）
// ============================================================

/// 1 チャンク分のカバー場へスタンプ列を押し当てる。
///
/// - `chunk_origin`: チャンク最小コーナーのワールド座標（メートル）
/// - `chunk_extent`: チャンク 1 辺のワールド長（メートル）
/// - `materials`: 素材定義（「足跡の残りやすさ」を引くために要る）
///
/// 戻り値は「カバー場が実際に変化したか」。false のときチャンクはダーティにならず、
/// 頂点の焼き直しも保存も走らない。
pub fn stamp_chunk(
    field: &mut CoverField,
    surface: &CoverSurface,
    chunk_origin: [f32; 3],
    chunk_extent: f32,
    stamps: &[CoverStampSpec],
    materials: &CoverMaterialSet,
) -> bool {
    if stamps.is_empty() || !(chunk_extent > 0.0) {
        return false;
    }

    // ─── 早期棄却: このチャンクの AABB にかかるスタンプだけを残す ───
    let aabb_min = chunk_origin;
    let aabb_max = [
        chunk_origin[0] + chunk_extent,
        chunk_origin[1] + chunk_extent,
        chunk_origin[2] + chunk_extent,
    ];
    let active: Vec<&CoverStampSpec> = stamps
        .iter()
        .filter(|s| s.strength > 0.0 && !s.is_outside_aabb(aabb_min, aabb_max))
        .collect();
    if active.is_empty() {
        return false;
    }

    let mut changed = false;
    for iz in 0..COVER_FIELD_RESOLUTION {
        for ix in 0..COVER_FIELD_RESOLUTION {
            // ─── 面が無い／積もらない急斜面のテクセルは踏めない ───
            if !surface.has_surface(ix, iz) {
                continue;
            }
            if slope_scale(surface.up_at(ix, iz)) <= COVER_STAMP_SLOPE_MIN {
                continue;
            }

            // ─── カバーが無いテクセルには轍が残らない（素の地面は変形しない）───
            let amount = field.amount_at(ix, iz);
            if amount <= 0.0 {
                continue;
            }
            let Some(material) = materials.get(field.material_at(ix, iz) as usize) else {
                continue;
            };
            // 素材が「足跡の残りやすさ」を持たない（濡れ・水膜など）なら何も起きない。
            if !(material.footprint_persistence > 0.0) {
                continue;
            }

            // ─── テクセル中心の面のワールド座標 ───
            let (u, v) = texel_center_uv(ix, iz);
            let world_x = chunk_origin[0] + u * chunk_extent;
            let world_z = chunk_origin[2] + v * chunk_extent;
            let surface_y = surface.surface_y_at(ix, iz);

            // ─── 各スタンプを評価して、最も深い踏み込みを採る ───
            let mut depth = 0.0f32;
            for s in &active {
                // Y 照合: 接地点とこのテクセルの面が上下に離れていれば無関係
                // （洞窟の床を歩いた轍が頭上の地表へ写らない）。
                if (surface_y - s.contact[1]).abs() > s.y_tolerance() {
                    continue;
                }
                let f = s.footprint_at(world_x, world_z);
                if f <= 0.0 {
                    continue;
                }
                // 踏み込み深さ = 形状 × 強さ × 素材の残りやすさ。
                //   量を超える踏み込みも許す（下地が完全に露出する）。
                let d = f * s.strength * material.footprint_persistence;
                if d > depth {
                    depth = d;
                }
            }
            if depth <= 0.0 {
                continue;
            }

            if field.stamp_trample(ix, iz, depth) {
                changed = true;
            }
        }
    }
    changed
}

// ============================================================
//  進行方向の解決
// ============================================================

/// 移動量から進行方向（ワールド XZ の単位ベクトル）を決める。
///
/// 動きが `COVER_STAMP_DIRECTION_MIN_SPEED × dt` 未満なら `previous` を返す
/// （止まった瞬間に痕がくるくる回るのを防ぐ）。`previous` が無ければ既定の +Z。
pub fn resolve_forward_xz(
    delta_xz: [f32; 2],
    dt: f32,
    previous: Option<[f32; 2]>,
) -> [f32; 2] {
    let min_move = COVER_STAMP_DIRECTION_MIN_SPEED * dt.max(0.0);
    let len = (delta_xz[0] * delta_xz[0] + delta_xz[1] * delta_xz[1]).sqrt();
    if len.is_finite() && len > min_move && len > 0.0 {
        return [delta_xz[0] / len, delta_xz[1] / len];
    }
    previous.unwrap_or(COVER_STAMP_DEFAULT_FORWARD)
}
