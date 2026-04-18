use std::path::PathBuf;

// ============================================================
//  最上位: Model
// ============================================================

/// 読み込んだ 3D モデル全体。
///
/// シーングラフ (`nodes`) を頂点として持ち、`meshes` / `materials` / `textures` /
/// `animations` / `skins` がそれぞれインデックスで参照し合う構造になっている。
pub struct Model {
    pub name:        String,
    /// 全ノード（フラット配列。子ノードは children インデックスで参照）
    pub nodes:       Vec<ModelNode>,
    /// シーンのルートノードインデックス
    pub root_nodes:  Vec<usize>,
    pub meshes:      Vec<Mesh>,
    pub materials:   Vec<Material>,
    pub textures:    Vec<TextureData>,
    pub animations:  Vec<Animation>,
    pub skins:       Vec<Skin>,
}

impl Model {
    /// アニメーションデータを持つかどうか
    pub fn is_animated(&self) -> bool { !self.animations.is_empty() }
    /// スキンデータを持つかどうか（スケルタルアニメーション）
    pub fn is_skinned(&self)  -> bool { !self.skins.is_empty() }
}

// ============================================================
//  シーングラフ
// ============================================================

/// シーングラフのノード。
///
/// `local_matrix` は行優先・列ベクトル規約（wgpu/DX12 準拠）。
/// glTF 由来の場合は列優先から転置済み。
pub struct ModelNode {
    pub name:         String,
    /// ローカル変換行列 [row][col]
    pub local_matrix: [[f32; 4]; 4],
    pub mesh_index:   Option<usize>,
    pub skin_index:   Option<usize>,
    pub children:     Vec<usize>,
    pub parent:       Option<usize>,
}

impl ModelNode {
    pub fn identity_matrix() -> [[f32; 4]; 4] {
        [
            [1.0, 0.0, 0.0, 0.0],
            [0.0, 1.0, 0.0, 0.0],
            [0.0, 0.0, 1.0, 0.0],
            [0.0, 0.0, 0.0, 1.0],
        ]
    }
}

// ============================================================
//  メッシュ
// ============================================================

/// 論理メッシュ。複数のプリミティブ（描画単位）を持てる。
///
/// glTF の Mesh / Primitive 構造に対応する。
pub struct Mesh {
    pub name:       String,
    pub primitives: Vec<Primitive>,
}

/// 1 描画単位（1 マテリアル + 頂点/インデックスバッファ）。
///
/// `skin_vertices` は `vertices` と同じ長さ、またはスキニングなしの場合は空。
pub struct Primitive {
    pub vertices:      Vec<Vertex>,
    /// スキニング用並列配列（`is_skinned()` が true のときのみ有効）
    pub skin_vertices: Vec<SkinVertex>,
    pub indices:       Vec<u32>,
    pub material_index: Option<usize>,
}

impl Primitive {
    pub fn is_skinned(&self) -> bool { !self.skin_vertices.is_empty() }
}

/// 頂点データ（GPU バッファに直接アップロードできる `repr(C)` レイアウト）。
#[derive(Debug, Clone, Copy, bytemuck::Pod, bytemuck::Zeroable)]
#[repr(C)]
pub struct Vertex {
    pub position: [f32; 3],
    /// 法線（単位ベクトル）
    pub normal:   [f32; 3],
    /// 接線 xyz + ハンドネス w（±1）。法線マップ計算で使用
    pub tangent:  [f32; 4],
    /// テクスチャ座標セット 0
    pub uv0:      [f32; 2],
    /// テクスチャ座標セット 1（ライトマップ等。未使用時は [0,0]）
    pub uv1:      [f32; 2],
    /// 頂点カラー RGBA（未指定時は [1,1,1,1]）
    pub color:    [f32; 4],
}

impl Default for Vertex {
    fn default() -> Self {
        Self {
            position: [0.0; 3],
            normal:   [0.0, 1.0, 0.0],
            tangent:  [1.0, 0.0, 0.0, 1.0],
            uv0:      [0.0; 2],
            uv1:      [0.0; 2],
            color:    [1.0; 4],
        }
    }
}

/// スキニング用頂点データ（`Vertex` と並列）。
#[derive(Debug, Clone, Copy, bytemuck::Pod, bytemuck::Zeroable)]
#[repr(C)]
pub struct SkinVertex {
    /// 影響するボーンのインデックス（最大 4 本）
    pub joints:  [u16; 4],
    /// 各ボーンのウェイト（合計 1.0 になるよう正規化済み）
    pub weights: [f32; 4],
}

// ============================================================
//  マテリアル（PBR メタリック-ラフネス）
// ============================================================

/// PBR マテリアル（glTF2.0 metallic-roughness ワークフロー準拠）。
pub struct Material {
    pub name: String,

    // ─── ベースカラー ─────────────────────────────────────
    pub base_color_factor:  [f32; 4],
    pub base_color_texture: Option<TextureInfo>,

    // ─── メタリック・ラフネス ─────────────────────────────
    pub metallic_factor:             f32,
    pub roughness_factor:            f32,
    pub metallic_roughness_texture:  Option<TextureInfo>,

    // ─── 法線マップ ───────────────────────────────────────
    pub normal_texture: Option<NormalTextureInfo>,

    // ─── アンビエントオクルージョン ───────────────────────
    pub occlusion_texture: Option<OcclusionTextureInfo>,

    // ─── エミッシブ ───────────────────────────────────────
    pub emissive_factor:  [f32; 3],
    pub emissive_texture: Option<TextureInfo>,

    // ─── ブレンド ─────────────────────────────────────────
    pub alpha_mode:   AlphaMode,
    /// AlphaMode::Mask のときのカットオフ閾値
    pub alpha_cutoff: f32,
    pub double_sided: bool,
}

impl Default for Material {
    fn default() -> Self {
        Self {
            name:                       String::from("Default"),
            base_color_factor:          [1.0, 1.0, 1.0, 1.0],
            base_color_texture:         None,
            metallic_factor:            1.0,
            roughness_factor:           1.0,
            metallic_roughness_texture: None,
            normal_texture:             None,
            occlusion_texture:          None,
            emissive_factor:            [0.0; 3],
            emissive_texture:           None,
            alpha_mode:                 AlphaMode::Opaque,
            alpha_cutoff:               0.5,
            double_sided:               false,
        }
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum AlphaMode {
    /// 完全不透明
    Opaque,
    /// alpha_cutoff 未満のピクセルを破棄
    Mask,
    /// アルファブレンド（半透明）
    Blend,
}

/// テクスチャ参照（Material → TextureData のインデックス）。
pub struct TextureInfo {
    pub texture_index: usize,
    pub tex_coord_set: u32,
}

pub struct NormalTextureInfo {
    pub texture_index: usize,
    pub tex_coord_set: u32,
    /// 法線強度スケール
    pub scale: f32,
}

pub struct OcclusionTextureInfo {
    pub texture_index: usize,
    pub tex_coord_set: u32,
    /// AO 強度（0.0 = 無効, 1.0 = フル）
    pub strength: f32,
}

// ============================================================
//  テクスチャ
// ============================================================

pub struct TextureData {
    pub name:    Option<String>,
    pub source:  TextureSource,
    pub sampler: SamplerData,
}

pub enum TextureSource {
    /// RGBA8 に正規化済みの埋め込みピクセルデータ
    Embedded { width: u32, height: u32, pixels: Vec<u8> },
    /// 外部ファイルパス（OBJ/MTL 等）
    FilePath(PathBuf),
}

pub struct SamplerData {
    pub mag_filter: FilterMode,
    pub min_filter: FilterMode,
    pub wrap_u:     WrapMode,
    pub wrap_v:     WrapMode,
}

impl Default for SamplerData {
    fn default() -> Self {
        Self {
            mag_filter: FilterMode::Linear,
            min_filter: FilterMode::LinearMipmapLinear,
            wrap_u:     WrapMode::Repeat,
            wrap_v:     WrapMode::Repeat,
        }
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum FilterMode {
    Nearest,
    Linear,
    NearestMipmapNearest,
    LinearMipmapNearest,
    NearestMipmapLinear,
    LinearMipmapLinear,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum WrapMode {
    Repeat,
    MirroredRepeat,
    ClampToEdge,
}

// ============================================================
//  アニメーション
// ============================================================

/// 1 アニメーションクリップ。
pub struct Animation {
    pub name:     String,
    /// クリップの総時間（秒）
    pub duration: f32,
    pub channels: Vec<AnimationChannel>,
}

/// アニメーションチャンネル（ノード × プロパティ）。
pub struct AnimationChannel {
    /// 対象ノードのインデックス（Model::nodes）
    pub target_node_index: usize,
    pub sampler:           AnimationSampler,
}

pub struct AnimationSampler {
    pub interpolation: Interpolation,
    /// キーフレームのタイムスタンプ（秒、昇順）
    pub timestamps: Vec<f32>,
    pub outputs:    AnimationOutputs,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Interpolation {
    Linear,
    Step,
    /// 三次スプライン（補間値とその前後の接線を含む）
    CubicSpline,
}

pub enum AnimationOutputs {
    Translations(Vec<[f32; 3]>),
    /// クォータニオン [x, y, z, w]
    Rotations(Vec<[f32; 4]>),
    Scales(Vec<[f32; 3]>),
    MorphWeights(Vec<f32>),
}

// ============================================================
//  スキン（スケルトン）
// ============================================================

/// スケルタルアニメーション用のスキンデータ。
pub struct Skin {
    pub name:   String,
    pub joints: Vec<SkinJoint>,
    /// ルートジョイントの joints 配列内インデックス（存在する場合）
    pub root_joint: Option<usize>,
}

/// スキンの 1 ジョイント。
pub struct SkinJoint {
    /// 対応するノードインデックス（Model::nodes）
    pub node_index: usize,
    pub name:       String,
    /// インバースバインド行列（行優先・列ベクトル規約）
    pub inverse_bind_matrix: [[f32; 4]; 4],
}
