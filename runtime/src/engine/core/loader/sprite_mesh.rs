// ============================================================
//  sprite_mesh.rs — `.sprite_mesh` アセット（2D メッシュ変形スキニング）
//
//  Spine 風の「メッシュ変形スプライト」の**形状データ**を表す JSON アセット。
//  ボーンそのものは独立した Skeleton アセットにせず、シーン上の
//  **普通の 2D 子アクター**（CanvasTransform を持つ Actor2D）が担う。
//  本ファイルが持つ `bones` は「ボーン名 → バインドポーズ（無変形時の姿勢）」
//  の宣言に過ぎず、実行時の姿勢は子アクターの CanvasTransform から取る。
//
//  【座標系】
//   - `vertices` はスプライトローカルの**キャンバスピクセル座標**。
//     原点は SkinnedSpriteComponent を持つアクターの CanvasTransform 原点、
//     +X が右、+Y が下（既存キャンバス座標系と同じ）。
//     ボーンアクターの CanvasTransform.position もまったく同じ空間なので、
//     頂点座標とボーン座標を無変換で突き合わせられる。
//   - `uvs` は [0,1]×[0,1]（左上原点）。既存 SpriteComponent のユニットクワッドと同じ。
//   - 従来のスプライト（width=w, height=h, pivot=0）と等価なメッシュは
//     頂点 (0,0)-(w,0)-(w,h)-(0,h) ／ UV (0,0)-(1,0)-(1,1)-(0,1) である。
//
//  【スキニング式】
//     bone_matrix[b] = current_relative[b] * inverse_bind[b]
//     skinned_pos    = Σ_i weight_i * (bone_matrix[idx_i] * pos)
//   - `current_relative[b]`: スプライトルートアクターを基準とした、
//     ボーンアクターまでの CanvasTransform 合成行列（実行時に算出）。
//   - `inverse_bind[b]`: 本ファイルの `bones` から組んだバインドポーズ
//     グローバル行列の逆行列（読込時に 1 度だけ計算してキャッシュする）。
//
//  【行列表現】
//   プロジェクト共通の**行優先 `[[f32; 4]; 4]`**（CanvasTransform::to_mat4 と同じ）。
//   2D アフィンなので実質 2×3 だが、既存の mat4x4_mul をそのまま使うため 4×4 に埋める。
// ============================================================

use serde::{Deserialize, Serialize};
use std::collections::HashMap;

use crate::engine::methods::gizmo_interact::mat4x4_mul;

// ─── 定数（マジックナンバー排除） ─────────────────────────────

/// 1 頂点が受けられるボーン影響の最大本数（GPU 側の vec4 と 1:1 対応）。
pub const MAX_BONE_INFLUENCES: usize = 4;
/// 1 メッシュが持てるボーンの最大本数（GPU パレット 1 回のディスパッチ上限）。
pub const MAX_SPRITE_BONES: usize = 128;
/// ウェイト合計がこの値未満なら「実質ウェイト無し」としてエラーにする。
const MIN_WEIGHT_SUM: f32 = 1.0e-6;
/// 行列の逆行列を計算する際、行列式がこの絶対値未満なら退化とみなす。
const MIN_DETERMINANT: f32 = 1.0e-12;
/// 現在サポートする `.sprite_mesh` のスキーマバージョン。
pub const SPRITE_MESH_VERSION: u32 = 1;
/// `.sprite_mesh` アセットの拡張子（ドットなし）。
pub const SPRITE_MESH_EXTENSION: &str = "sprite_mesh";

/// 単位行列（行優先 4×4）。
pub const IDENTITY_MAT4: [[f32; 4]; 4] = [
    [1.0, 0.0, 0.0, 0.0],
    [0.0, 1.0, 0.0, 0.0],
    [0.0, 0.0, 1.0, 0.0],
    [0.0, 0.0, 0.0, 1.0],
];

// ============================================================
//  JSON スキーマ（シリアライズ表現）
// ============================================================

/// `.sprite_mesh` のボーン宣言（バインドポーズ = 無変形時のローカル姿勢）。
#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct SpriteMeshBoneData {
    /// ボーン名。シーン上の子アクター名と突き合わせて解決する（重複不可）。
    pub name: String,
    /// 親ボーン名。空文字列 = ルート（スプライトルートアクター直下扱い）。
    #[serde(default)]
    pub parent: String,
    /// バインドポーズのローカル位置（親ボーン基準・キャンバスピクセル）。
    #[serde(default)]
    pub position: [f32; 2],
    /// バインドポーズのローカル回転（度・Z 軸まわり）。
    #[serde(default)]
    pub rotation: f32,
    /// バインドポーズのローカルスケール。
    #[serde(default = "default_scale")]
    pub scale: [f32; 2],
}

/// serde 既定値: スケール [1, 1]。
fn default_scale() -> [f32; 2] {
    [1.0, 1.0]
}

/// 1 頂点に対する 1 本ぶんのボーン影響。
#[derive(Clone, Copy, Debug, Serialize, Deserialize)]
pub struct SpriteMeshInfluenceData {
    /// `bones` 配列のインデックス。
    pub bone: u32,
    /// 影響度（正規化前の生値）。
    pub weight: f32,
}

/// `.sprite_mesh` ファイル全体のシリアライズ表現。
#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct SpriteMeshData {
    /// スキーマバージョン（現行 1）。
    #[serde(default)]
    pub version: u32,
    /// 表示用の名前（省略可・実行時挙動に影響しない）。
    #[serde(default)]
    pub name: String,
    /// 制作者向けメモ（省略可・実行時挙動に影響しない）。
    #[serde(default)]
    pub comment: String,
    /// このメッシュの元画像（省略可・`.sprite_mesh` からの相対パス）。
    ///
    /// エディタのスプライトリグパネルが「メッシュを開き直したとき、
    /// どの画像に重ねて編集するか」を引き当てるためのヒントで、
    /// **実行時の描画には一切使わない**（描画テクスチャは
    /// `SkinnedSpriteComponent::texture_path` が決める）。
    /// 省略時は空文字列（version 1 導入前のファイルもそのまま読める）。
    #[serde(default)]
    pub texture: String,
    /// 頂点位置（スプライトローカルのキャンバスピクセル座標）。
    pub vertices: Vec<[f32; 2]>,
    /// 頂点 UV（[0,1]×[0,1]・左上原点）。`vertices` と同数。
    pub uvs: Vec<[f32; 2]>,
    /// 三角形インデックス（3 の倍数個）。
    pub triangles: Vec<u32>,
    /// ボーン宣言（バインドポーズ）。
    pub bones: Vec<SpriteMeshBoneData>,
    /// 頂点ごとのボーン影響（最大 `MAX_BONE_INFLUENCES` 本）。`vertices` と同数。
    pub weights: Vec<Vec<SpriteMeshInfluenceData>>,
}

// ============================================================
//  実行時表現
// ============================================================

/// 実行時のボーン情報（名前・親インデックス・バインドポーズローカル行列）。
#[derive(Clone, Debug)]
pub struct SpriteMeshBone {
    /// ボーン名（子アクター名との突き合わせキー）。
    pub name: String,
    /// 親ボーンのインデックス。None = ルート。
    pub parent: Option<usize>,
    /// バインドポーズのローカル変換行列（行優先）。
    pub local_bind: [[f32; 4]; 4],
    /// バインドポーズのローカル位置（親ボーン基準・キャンバスピクセル）。
    /// `local_bind` の材料そのもの。行列から逆算せずに済むよう保持する
    /// （エディタの「ボーンアクター生成」が CanvasTransform へそのまま写す）。
    pub bind_position: [f32; 2],
    /// バインドポーズのローカル回転（度・Z 軸まわり）。
    pub bind_rotation: f32,
    /// バインドポーズのローカルスケール。
    pub bind_scale: [f32; 2],
}

/// 1 頂点ぶんの正規化済みスキンウェイト（GPU へそのまま渡せる固定長）。
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct SpriteVertexWeights {
    /// 影響ボーンインデックス（未使用スロットは 0・ウェイト 0）。
    pub bones: [u32; MAX_BONE_INFLUENCES],
    /// 正規化済みウェイト（合計 1.0）。
    pub weights: [f32; MAX_BONE_INFLUENCES],
}

impl Default for SpriteVertexWeights {
    fn default() -> Self {
        Self {
            bones: [0; MAX_BONE_INFLUENCES],
            weights: [0.0; MAX_BONE_INFLUENCES],
        }
    }
}

/// 検証・前計算済みの `.sprite_mesh`（実行時に使う唯一の形）。
#[derive(Clone, Debug)]
pub struct SpriteMesh {
    /// 頂点位置（スプライトローカルのキャンバスピクセル）。
    pub vertices: Vec<[f32; 2]>,
    /// 頂点 UV。
    pub uvs: Vec<[f32; 2]>,
    /// 三角形インデックス。
    pub triangles: Vec<u32>,
    /// ボーン（宣言順 = GPU パレットの並び順）。
    pub bones: Vec<SpriteMeshBone>,
    /// バインドポーズ**グローバル**行列（ルート→自分までの合成。行優先）。
    pub bind_global: Vec<[[f32; 4]; 4]>,
    /// バインドポーズ逆行列（`bind_global` の逆。読込時に 1 度だけ計算）。
    pub inverse_bind: Vec<[[f32; 4]; 4]>,
    /// 頂点ごとの正規化済みウェイト。
    pub weights: Vec<SpriteVertexWeights>,
    /// 元画像への相対パス（エディタ用のヒント。空文字列 = 未指定）。
    pub texture: String,
}

/// `.sprite_mesh` の読み込み・検証で起こり得る失敗。
#[derive(Debug, Clone, PartialEq)]
pub enum SpriteMeshError {
    /// JSON として壊れている。
    Parse(String),
    /// スキーマバージョンが未対応。
    UnsupportedVersion(u32),
    /// 配列長の不整合・空・範囲外インデックスなど、内容が不正。
    Invalid(String),
}

impl std::fmt::Display for SpriteMeshError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            Self::Parse(m) => write!(f, ".sprite_mesh の JSON 解析に失敗: {m}"),
            Self::UnsupportedVersion(v) => {
                write!(f, ".sprite_mesh の version={v} は未対応（対応は {SPRITE_MESH_VERSION}）")
            }
            Self::Invalid(m) => write!(f, ".sprite_mesh の内容が不正: {m}"),
        }
    }
}

// ============================================================
//  2D アフィン行列ヘルパー
// ============================================================

/// TRS（平行移動・Z 回転・スケール）から行優先の 2D アフィン行列を組む。
///
/// `CanvasTransform::to_mat4()`（pivot=0）と完全に同じ式である
/// ＝ ボーンアクターの実行時姿勢とバインドポーズが同じ規則で行列化される。
pub fn trs_to_mat4(position: [f32; 2], rotation_deg: f32, scale: [f32; 2]) -> [[f32; 4]; 4] {
    let rad = rotation_deg.to_radians();
    let (sin, cos) = rad.sin_cos();
    let [sx, sy] = scale;
    let [px, py] = position;
    [
        [cos * sx, -sin * sy, 0.0, px],
        [sin * sx, cos * sy, 0.0, py],
        [0.0, 0.0, 1.0, 0.0],
        [0.0, 0.0, 0.0, 1.0],
    ]
}

/// 行優先 2D アフィン行列（上左 2×2 + 平行移動）の逆行列を返す。
///
/// 2×2 部分が退化（行列式 ≈ 0）している場合は単位行列を返す
/// ＝ そのボーンは「無変形」として扱われ、描画が壊れる代わりに落ちない。
pub fn invert_affine2d(m: [[f32; 4]; 4]) -> [[f32; 4]; 4] {
    let (a, b, c, d) = (m[0][0], m[0][1], m[1][0], m[1][1]);
    let det = a * d - b * c;
    if det.abs() < MIN_DETERMINANT {
        return IDENTITY_MAT4;
    }
    let inv_det = 1.0 / det;
    // 2×2 の逆行列
    let (ia, ib, ic, id) = (d * inv_det, -b * inv_det, -c * inv_det, a * inv_det);
    // 平行移動は -inv2x2 * t
    let (tx, ty) = (m[0][3], m[1][3]);
    [
        [ia, ib, 0.0, -(ia * tx + ib * ty)],
        [ic, id, 0.0, -(ic * tx + id * ty)],
        [0.0, 0.0, 1.0, 0.0],
        [0.0, 0.0, 0.0, 1.0],
    ]
}

/// 行優先 2D アフィン行列で点（x, y）を変換する。
#[inline]
pub fn transform_point2d(m: [[f32; 4]; 4], p: [f32; 2]) -> [f32; 2] {
    [
        m[0][0] * p[0] + m[0][1] * p[1] + m[0][3],
        m[1][0] * p[0] + m[1][1] * p[1] + m[1][3],
    ]
}

// ============================================================
//  読み込み・検証
// ============================================================

impl SpriteMesh {
    /// JSON 文字列から検証済みの `SpriteMesh` を構築する。
    ///
    /// 検証内容:
    /// - version が対応範囲か
    /// - vertices / uvs / weights の要素数が一致し、空でないか
    /// - triangles が 3 の倍数で、全インデックスが頂点範囲内か
    /// - bones が 1 本以上・名前重複なし・ボーン数上限以内
    /// - parent 名が実在し、親子関係に循環がないか
    /// - 各頂点のウェイトが 1〜MAX_BONE_INFLUENCES 本・非負・ボーン範囲内・合計が正
    ///
    /// ウェイトは合計 1.0 になるよう**読込時に正規化**する（GPU 側では正規化しない）。
    pub fn from_json(src: &str) -> Result<Self, SpriteMeshError> {
        let data: SpriteMeshData =
            serde_json::from_str(src).map_err(|e| SpriteMeshError::Parse(e.to_string()))?;
        Self::from_data(data)
    }

    /// パース済みのシリアライズ表現から検証済みの `SpriteMesh` を構築する。
    pub fn from_data(data: SpriteMeshData) -> Result<Self, SpriteMeshError> {
        // ── version ──
        // version 省略（0）は「バージョン導入前の手書き」とみなして受け入れる。
        if data.version != 0 && data.version != SPRITE_MESH_VERSION {
            return Err(SpriteMeshError::UnsupportedVersion(data.version));
        }

        // ── 頂点配列の整合 ──
        let vcount = data.vertices.len();
        if vcount == 0 {
            return Err(SpriteMeshError::Invalid("vertices が空".into()));
        }
        if data.uvs.len() != vcount {
            return Err(SpriteMeshError::Invalid(format!(
                "uvs の数 {} が vertices の数 {vcount} と一致しない",
                data.uvs.len()
            )));
        }
        if data.weights.len() != vcount {
            return Err(SpriteMeshError::Invalid(format!(
                "weights の数 {} が vertices の数 {vcount} と一致しない",
                data.weights.len()
            )));
        }

        // ── 三角形インデックス ──
        if data.triangles.is_empty() || data.triangles.len() % 3 != 0 {
            return Err(SpriteMeshError::Invalid(format!(
                "triangles の要素数 {} が 3 の倍数でない（または空）",
                data.triangles.len()
            )));
        }
        if let Some(&bad) = data.triangles.iter().find(|&&i| i as usize >= vcount) {
            return Err(SpriteMeshError::Invalid(format!(
                "triangles に範囲外の頂点インデックス {bad}（頂点数 {vcount}）"
            )));
        }

        // ── ボーン ──
        let bcount = data.bones.len();
        if bcount == 0 {
            return Err(SpriteMeshError::Invalid("bones が空".into()));
        }
        if bcount > MAX_SPRITE_BONES {
            return Err(SpriteMeshError::Invalid(format!(
                "ボーン数 {bcount} が上限 {MAX_SPRITE_BONES} を超えている"
            )));
        }
        // 名前 → インデックスの索引（重複検出も兼ねる）
        let mut name_index: HashMap<&str, usize> = HashMap::with_capacity(bcount);
        for (i, b) in data.bones.iter().enumerate() {
            if b.name.is_empty() {
                return Err(SpriteMeshError::Invalid(format!("bones[{i}] の name が空")));
            }
            if name_index.insert(b.name.as_str(), i).is_some() {
                return Err(SpriteMeshError::Invalid(format!(
                    "ボーン名 '{}' が重複している",
                    b.name
                )));
            }
        }
        // 親解決
        let mut bones: Vec<SpriteMeshBone> = Vec::with_capacity(bcount);
        for (i, b) in data.bones.iter().enumerate() {
            let parent = if b.parent.is_empty() {
                None
            } else {
                match name_index.get(b.parent.as_str()) {
                    Some(&p) if p != i => Some(p),
                    Some(_) => {
                        return Err(SpriteMeshError::Invalid(format!(
                            "ボーン '{}' が自分自身を親にしている",
                            b.name
                        )));
                    }
                    None => {
                        return Err(SpriteMeshError::Invalid(format!(
                            "ボーン '{}' の親 '{}' が存在しない",
                            b.name, b.parent
                        )));
                    }
                }
            };
            bones.push(SpriteMeshBone {
                name: b.name.clone(),
                parent,
                local_bind: trs_to_mat4(b.position, b.rotation, b.scale),
                bind_position: b.position,
                bind_rotation: b.rotation,
                bind_scale: b.scale,
            });
        }
        // 循環検出（各ボーンから親を辿り、ボーン数を超えたら循環）
        for i in 0..bcount {
            let mut cur = bones[i].parent;
            let mut steps = 0usize;
            while let Some(p) = cur {
                steps += 1;
                if steps > bcount {
                    return Err(SpriteMeshError::Invalid(format!(
                        "ボーン '{}' の親子関係が循環している",
                        bones[i].name
                    )));
                }
                cur = bones[p].parent;
            }
        }

        // ── バインドポーズのグローバル行列と逆行列 ──
        let bind_global = compute_bind_globals(&bones);
        let inverse_bind: Vec<[[f32; 4]; 4]> =
            bind_global.iter().map(|&m| invert_affine2d(m)).collect();

        // ── ウェイト（検証 + 正規化） ──
        let mut weights: Vec<SpriteVertexWeights> = Vec::with_capacity(vcount);
        for (vi, infl) in data.weights.iter().enumerate() {
            if infl.is_empty() {
                return Err(SpriteMeshError::Invalid(format!(
                    "weights[{vi}] が空（1 本以上のボーン影響が必要）"
                )));
            }
            if infl.len() > MAX_BONE_INFLUENCES {
                return Err(SpriteMeshError::Invalid(format!(
                    "weights[{vi}] の影響数 {} が上限 {MAX_BONE_INFLUENCES} を超えている",
                    infl.len()
                )));
            }
            let mut w = SpriteVertexWeights::default();
            let mut sum = 0.0f32;
            for (k, e) in infl.iter().enumerate() {
                if e.bone as usize >= bcount {
                    return Err(SpriteMeshError::Invalid(format!(
                        "weights[{vi}][{k}] のボーンインデックス {} が範囲外（ボーン数 {bcount}）",
                        e.bone
                    )));
                }
                if !e.weight.is_finite() || e.weight < 0.0 {
                    return Err(SpriteMeshError::Invalid(format!(
                        "weights[{vi}][{k}] のウェイト {} が不正（0 以上の有限値が必要）",
                        e.weight
                    )));
                }
                w.bones[k] = e.bone;
                w.weights[k] = e.weight;
                sum += e.weight;
            }
            if sum < MIN_WEIGHT_SUM {
                return Err(SpriteMeshError::Invalid(format!(
                    "weights[{vi}] のウェイト合計が 0（正規化不能）"
                )));
            }
            // 合計 1.0 へ正規化する（GPU 側では正規化しない前提）
            let inv = 1.0 / sum;
            for k in 0..MAX_BONE_INFLUENCES {
                w.weights[k] *= inv;
            }
            weights.push(w);
        }

        Ok(Self {
            vertices: data.vertices,
            uvs: data.uvs,
            triangles: data.triangles,
            bones,
            bind_global,
            inverse_bind,
            weights,
            texture: data.texture,
        })
    }

    /// ボーン数。
    #[inline]
    pub fn bone_count(&self) -> usize {
        self.bones.len()
    }

    /// 頂点数。
    #[inline]
    pub fn vertex_count(&self) -> usize {
        self.vertices.len()
    }

    /// ボーン名からインデックスを引く（見つからなければ None）。
    pub fn bone_index(&self, name: &str) -> Option<usize> {
        self.bones.iter().position(|b| b.name == name)
    }

    /// CPU 側で 1 頂点をスキニングする（テスト・CPU ピッキング用の正典実装）。
    ///
    /// `bone_matrices` は `current_relative * inverse_bind` 済みの行列列
    /// （GPU へ送るパレットとまったく同じ内容）。
    pub fn skin_vertex(&self, vi: usize, bone_matrices: &[[[f32; 4]; 4]]) -> [f32; 2] {
        let p = self.vertices[vi];
        let w = &self.weights[vi];
        let mut acc = [0.0f32, 0.0];
        for k in 0..MAX_BONE_INFLUENCES {
            let weight = w.weights[k];
            if weight == 0.0 {
                continue;
            }
            let Some(&m) = bone_matrices.get(w.bones[k] as usize) else {
                continue;
            };
            let q = transform_point2d(m, p);
            acc[0] += q[0] * weight;
            acc[1] += q[1] * weight;
        }
        acc
    }

    /// 全ボーンが「バインドポーズのまま」であるときのボーン行列列を返す。
    ///
    /// `current_relative == bind_global` なので結果はすべて単位行列になる
    /// ＝ 無変形。ボーンアクターが 1 本も解決できなかったときのフォールバックに使う。
    pub fn identity_bone_matrices(&self) -> Vec<[[f32; 4]; 4]> {
        vec![IDENTITY_MAT4; self.bones.len()]
    }
}

/// ボーンのローカルバインド行列からグローバルバインド行列を計算する。
///
/// 親が後方に宣言されていても正しく解けるよう、各ボーンについて
/// ルートまで遡って合成する（ボーン数は最大 `MAX_SPRITE_BONES` のため十分軽い）。
fn compute_bind_globals(bones: &[SpriteMeshBone]) -> Vec<[[f32; 4]; 4]> {
    let mut out = Vec::with_capacity(bones.len());
    for start in 0..bones.len() {
        // 自分 → 親 → … → ルート の順に集めてから逆順に合成する
        let mut chain: Vec<usize> = Vec::new();
        let mut cur = Some(start);
        while let Some(i) = cur {
            chain.push(i);
            cur = bones[i].parent;
        }
        let mut m = IDENTITY_MAT4;
        for &i in chain.iter().rev() {
            m = mat4x4_mul(m, bones[i].local_bind);
        }
        out.push(m);
    }
    out
}

// ============================================================
//  テスト
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;

    /// テスト用フィクスチャ（矩形 1 枚 + ボーン 1 本）。
    const QUAD_ONE_BONE: &str =
        include_str!("../../../../tests/fixtures/quad_one_bone.sprite_mesh");
    /// テスト用フィクスチャ（2 ボーンの帯）。
    const TWO_BONE_ARM: &str = include_str!("../../../../tests/fixtures/two_bone_arm.sprite_mesh");
    /// エディタのスプライトリグパネル（Phase B1a）が自動生成したメッシュ。
    /// `editor/tests/SpriteRigTests` が透過円から作って書き出したもので、
    /// 「エディタが吐く JSON をランタイムのパーサがそのまま受理する」ことの検証に使う。
    const GENERATED_CIRCLE: &str =
        include_str!("../../../../tests/fixtures/generated_circle.sprite_mesh");

    /// 2 点がほぼ一致することを検査する（浮動小数の許容誤差付き）。
    fn assert_close(a: [f32; 2], b: [f32; 2], eps: f32, what: &str) {
        assert!(
            (a[0] - b[0]).abs() < eps && (a[1] - b[1]).abs() < eps,
            "{what}: {a:?} != {b:?}"
        );
    }

    #[test]
    fn quad_one_bone_parses() {
        let m = SpriteMesh::from_json(QUAD_ONE_BONE).expect("パース成功");
        assert_eq!(m.vertex_count(), 4);
        assert_eq!(m.triangles.len(), 6);
        assert_eq!(m.bone_count(), 1);
        assert_eq!(m.bone_index("root"), Some(0));
        // ウェイトは 1 本 1.0、残りスロットは 0
        assert_eq!(m.weights[0].weights, [1.0, 0.0, 0.0, 0.0]);
    }

    #[test]
    fn weights_are_normalized_on_load() {
        // 合計 4.0 のウェイトを与えると 0.25 ずつに正規化されること
        let src = r#"{
            "version": 1,
            "vertices": [[0,0]], "uvs": [[0,0]], "triangles": [0,0,0],
            "bones": [
                {"name":"a","parent":""},
                {"name":"b","parent":""}
            ],
            "weights": [[{"bone":0,"weight":3.0},{"bone":1,"weight":1.0}]]
        }"#;
        let m = SpriteMesh::from_json(src).expect("パース成功");
        assert!((m.weights[0].weights[0] - 0.75).abs() < 1e-6);
        assert!((m.weights[0].weights[1] - 0.25).abs() < 1e-6);
        let sum: f32 = m.weights[0].weights.iter().sum();
        assert!((sum - 1.0).abs() < 1e-6, "合計が 1.0 に正規化される");
    }

    #[test]
    fn rejects_out_of_range_bone_index() {
        let src = r#"{
            "version": 1,
            "vertices": [[0,0]], "uvs": [[0,0]], "triangles": [0,0,0],
            "bones": [{"name":"a","parent":""}],
            "weights": [[{"bone":5,"weight":1.0}]]
        }"#;
        let err = SpriteMesh::from_json(src).unwrap_err();
        assert!(
            matches!(err, SpriteMeshError::Invalid(ref s) if s.contains("範囲外")),
            "範囲外ボーンインデックスは拒否される: {err:?}"
        );
    }

    #[test]
    fn rejects_out_of_range_triangle_index() {
        let src = r#"{
            "version": 1,
            "vertices": [[0,0]], "uvs": [[0,0]], "triangles": [0,1,2],
            "bones": [{"name":"a","parent":""}],
            "weights": [[{"bone":0,"weight":1.0}]]
        }"#;
        let err = SpriteMesh::from_json(src).unwrap_err();
        assert!(matches!(err, SpriteMeshError::Invalid(_)), "{err:?}");
    }

    #[test]
    fn rejects_zero_weight_sum_and_negative_weight() {
        let zero = r#"{
            "version": 1,
            "vertices": [[0,0]], "uvs": [[0,0]], "triangles": [0,0,0],
            "bones": [{"name":"a","parent":""}],
            "weights": [[{"bone":0,"weight":0.0}]]
        }"#;
        assert!(matches!(
            SpriteMesh::from_json(zero).unwrap_err(),
            SpriteMeshError::Invalid(_)
        ));
        let neg = r#"{
            "version": 1,
            "vertices": [[0,0]], "uvs": [[0,0]], "triangles": [0,0,0],
            "bones": [{"name":"a","parent":""}],
            "weights": [[{"bone":0,"weight":-1.0}]]
        }"#;
        assert!(matches!(
            SpriteMesh::from_json(neg).unwrap_err(),
            SpriteMeshError::Invalid(_)
        ));
    }

    #[test]
    fn rejects_unknown_parent_and_cycle() {
        let unknown = r#"{
            "version": 1,
            "vertices": [[0,0]], "uvs": [[0,0]], "triangles": [0,0,0],
            "bones": [{"name":"a","parent":"nope"}],
            "weights": [[{"bone":0,"weight":1.0}]]
        }"#;
        assert!(matches!(
            SpriteMesh::from_json(unknown).unwrap_err(),
            SpriteMeshError::Invalid(_)
        ));
        let cycle = r#"{
            "version": 1,
            "vertices": [[0,0]], "uvs": [[0,0]], "triangles": [0,0,0],
            "bones": [{"name":"a","parent":"b"},{"name":"b","parent":"a"}],
            "weights": [[{"bone":0,"weight":1.0}]]
        }"#;
        assert!(matches!(
            SpriteMesh::from_json(cycle).unwrap_err(),
            SpriteMeshError::Invalid(_)
        ));
    }

    #[test]
    fn rejects_unsupported_version() {
        let src = r#"{
            "version": 99,
            "vertices": [[0,0]], "uvs": [[0,0]], "triangles": [0,0,0],
            "bones": [{"name":"a","parent":""}],
            "weights": [[{"bone":0,"weight":1.0}]]
        }"#;
        assert_eq!(
            SpriteMesh::from_json(src).unwrap_err(),
            SpriteMeshError::UnsupportedVersion(99)
        );
    }

    #[test]
    fn inverse_bind_is_true_inverse() {
        let m = SpriteMesh::from_json(TWO_BONE_ARM).expect("パース成功");
        // elbow のバインドグローバルは (100, 0) 平行移動
        assert_close(
            [m.bind_global[1][0][3], m.bind_global[1][1][3]],
            [100.0, 0.0],
            1e-5,
            "elbow のバインドグローバル平行移動",
        );
        // inverse_bind * bind_global == 単位行列
        for i in 0..m.bone_count() {
            let id = mat4x4_mul(m.inverse_bind[i], m.bind_global[i]);
            for r in 0..2 {
                for c in 0..4 {
                    let expect = if r == c { 1.0 } else { 0.0 };
                    assert!(
                        (id[r][c] - expect).abs() < 1e-5,
                        "bone{i} の inverse_bind * bind_global が単位行列でない: {id:?}"
                    );
                }
            }
        }
    }

    /// 「矩形 + 1 ボーン恒等」が従来スプライトと完全に一致することを検査する。
    ///
    /// 従来スプライト: ユニットクワッド [0,1]² に `to_sprite_mat4(w, h)` を適用。
    /// スキンメッシュ: バインドポーズ頂点に `to_mesh_mat4(1, 1)` を適用（無変形）。
    /// 両者の 4 隅が一致すれば「従来スプライトと等価」である。
    #[test]
    fn quad_one_bone_matches_plain_sprite() {
        use crate::engine::components::CanvasTransform;

        let mesh = SpriteMesh::from_json(QUAD_ONE_BONE).expect("パース成功");
        let (w, h) = (100.0f32, 80.0f32);

        // 任意の（自明でない）キャンバストランスフォームで比較する
        let ct = CanvasTransform {
            position: [30.0, -12.0],
            rotation: 25.0,
            scale: [1.3, 0.7],
            ..CanvasTransform::default()
        };

        // 従来スプライト側: ユニットクワッドの 4 隅
        let sprite_mat = ct.to_sprite_mat4(w, h);
        let unit_corners = [[0.0, 0.0], [1.0, 0.0], [1.0, 1.0], [0.0, 1.0]];
        // スキン側: 無変形（全ボーン行列 = 単位）でスキニングしてからメッシュ行列を適用
        let mesh_mat = ct.to_mesh_mat4(1.0, 1.0);
        let palette = mesh.identity_bone_matrices();

        for (i, &uc) in unit_corners.iter().enumerate() {
            let a = transform_point2d(sprite_mat, uc);
            let skinned = mesh.skin_vertex(i, &palette);
            let b = transform_point2d(mesh_mat, skinned);
            assert_close(a, b, 1e-3, &format!("corner{i}"));
        }
    }

    /// ボーンを回転させたとき、追従頂点が期待位置に来ることを検査する。
    ///
    /// two_bone_arm の elbow（バインド位置 (100,0)）を +90 度回すと、
    /// elbow に 100% 追従する先端頂点 (200, ±10) は elbow を中心に 90 度回った
    /// 位置 (100∓10, 100) へ移動する（+Y が下・時計回りが正の 2D 回転）。
    #[test]
    fn bone_rotation_moves_vertices_as_expected() {
        let mesh = SpriteMesh::from_json(TWO_BONE_ARM).expect("パース成功");

        // 実行時のボーン姿勢: root は無変形、elbow は (100,0) のまま +90 度回転
        let current_root = trs_to_mat4([0.0, 0.0], 0.0, [1.0, 1.0]);
        let current_elbow = mat4x4_mul(current_root, trs_to_mat4([100.0, 0.0], 90.0, [1.0, 1.0]));
        let palette = vec![
            mat4x4_mul(current_root, mesh.inverse_bind[0]),
            mat4x4_mul(current_elbow, mesh.inverse_bind[1]),
        ];

        // root にだけ追従する根本頂点は動かない
        assert_close(mesh.skin_vertex(0, &palette), [0.0, -10.0], 1e-3, "v0");
        assert_close(mesh.skin_vertex(3, &palette), [100.0, 10.0], 1e-3, "v3");
        // elbow に追従する先端頂点は elbow を中心に 90 度回る
        // (200,-10) → elbow ローカル (100,-10) → 回転後 (10,100) → +elbow(100,0) = (110,100)
        assert_close(mesh.skin_vertex(4, &palette), [110.0, 100.0], 1e-3, "v4");
        // (200, 10) → ローカル (100,10) → 回転後 (-10,100) → (90,100)
        assert_close(mesh.skin_vertex(5, &palette), [90.0, 100.0], 1e-3, "v5");
    }

    /// `texture` は省略可能フィールドで、無ければ空文字列になる（後方互換）。
    #[test]
    fn texture_field_is_optional() {
        // 既存フィクスチャには texture が無い ＝ 従来ファイルがそのまま読める
        let old = SpriteMesh::from_json(QUAD_ONE_BONE).expect("パース成功");
        assert_eq!(old.texture, "", "texture 省略時は空文字列");

        // 明示された相対パスはそのまま保持される
        let src = r#"{
            "version": 1,
            "texture": "hero.png",
            "vertices": [[0,0]], "uvs": [[0,0]], "triangles": [0,0,0],
            "bones": [{"name":"a","parent":""}],
            "weights": [[{"bone":0,"weight":1.0}]]
        }"#;
        let with_texture = SpriteMesh::from_json(src).expect("パース成功");
        assert_eq!(with_texture.texture, "hero.png");
    }

    /// エディタ（スプライトリグパネル）が自動生成したメッシュを受理できることを検査する。
    ///
    /// これが通らなくなったら、エディタ側の書き出しとランタイムの検証条件がずれている。
    /// フィクスチャは `dotnet run --project editor/tests/SpriteRigTests` で再生成する。
    #[test]
    fn editor_generated_mesh_is_accepted() {
        let mesh = SpriteMesh::from_json(GENERATED_CIRCLE).expect("エディタ生成メッシュのパース成功");

        assert!(mesh.vertex_count() > 0, "頂点がある");
        assert_eq!(mesh.triangles.len() % 3, 0, "三角形インデックスは 3 の倍数");
        assert_eq!(mesh.uvs.len(), mesh.vertex_count(), "UV 数が頂点数と一致");
        assert_eq!(mesh.weights.len(), mesh.vertex_count(), "ウェイト数が頂点数と一致");
        assert_eq!(mesh.bone_count(), 1, "B1a はルート 1 本だけを書き出す");
        assert_eq!(mesh.bone_index("root"), Some(0), "ルートボーン名は root");
        assert_eq!(mesh.texture, "generated_circle.png", "texture ヒントが読める");

        // UV は [0,1]^2 に収まり、全ウェイトはルートへ 1.0 に正規化されている
        for uv in &mesh.uvs {
            assert!(
                (0.0..=1.0).contains(&uv[0]) && (0.0..=1.0).contains(&uv[1]),
                "UV が [0,1]^2 の外: {uv:?}"
            );
        }
        for w in &mesh.weights {
            assert_eq!(w.weights, [1.0, 0.0, 0.0, 0.0], "全頂点がルートへ 1.0");
        }
    }

    /// ボーンが 1 本も解決できないときのフォールバック（バインドポーズ＝無変形）。
    #[test]
    fn unresolved_bones_fall_back_to_bind_pose() {
        let mesh = SpriteMesh::from_json(TWO_BONE_ARM).expect("パース成功");
        let palette = mesh.identity_bone_matrices();
        for vi in 0..mesh.vertex_count() {
            assert_close(
                mesh.skin_vertex(vi, &palette),
                mesh.vertices[vi],
                1e-5,
                "バインドポーズのまま",
            );
        }
    }
}
