use std::path::PathBuf;
use serde::{Serialize, Deserialize};

// ============================================================
//  最上位: Model
// ============================================================

/// 読み込んだ 3D モデル全体。
///
/// シーングラフ (`nodes`) を頂点として持ち、`meshes` / `materials` / `textures` /
/// `animations` / `skins` がそれぞれインデックスで参照し合う構造になっている。
///
/// `serde` 派生を持つのは派生データキャッシュ（`asset_cache`）で bincode
/// シリアライズするため。GPU ハンドルは一切保持しないため丸ごと直列化できる。
#[derive(Serialize, Deserialize)]
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
#[derive(Serialize, Deserialize)]
pub struct ModelNode {
    pub name:         String,
    /// ローカル変換行列 [row][col]（行優先）
    pub local_matrix: [[f32; 4]; 4],
    /// バインドポーズ平行移動（アニメーション補間用）
    pub translation:  [f32; 3],
    /// バインドポーズ回転クォータニオン [x, y, z, w]
    pub rotation:     [f32; 4],
    /// バインドポーズスケール
    pub scale:        [f32; 3],
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
#[derive(Serialize, Deserialize)]
pub struct Mesh {
    pub name:       String,
    pub primitives: Vec<Primitive>,
}

/// 1 描画単位（1 マテリアル + 頂点/インデックスバッファ）。
///
/// `skin_vertices` は `vertices` と同じ長さ、またはスキニングなしの場合は空。
/// `lod_indices[0]` = LOD1（約50%）、`[1]` = LOD2（約25%）、`[2]` = LOD3（約10%）。
/// 空の場合は LOD 未生成（すべて LOD0 で代用）。
#[derive(Serialize, Deserialize)]
pub struct Primitive {
    pub vertices:      Vec<Vertex>,
    /// スキニング用並列配列（`is_skinned()` が true のときのみ有効）
    pub skin_vertices: Vec<SkinVertex>,
    pub indices:       Vec<u32>,
    pub material_index: Option<usize>,
    /// ロード時生成の LOD インデックスバッファ群
    pub lod_indices:   Vec<Vec<u32>>,

    // ── メッシュレット（GPU カリング第1弾, LOD0 のみ）─────────────
    /// LOD0 を meshopt で分割したメッシュレット記述子（境界球・法線コーン込み）。
    /// 空 = メッシュレット未生成（三角形数が少ない / スキン / 生成失敗）。この場合は
    /// 従来の LOD0 描画経路がそのまま使われる。`meshlet_vertices` / `meshlet_triangles`
    /// と組で意味を持つ。
    #[serde(default)]
    pub meshlets: Vec<MeshletDesc>,
    /// 全メッシュレットの「メッシュレットローカル頂点番号 → 元頂点インデックス」表を連結したもの。
    /// メッシュレット m の頂点は `meshlet_vertices[m.vertex_offset .. + m.vertex_count]`。
    #[serde(default)]
    pub meshlet_vertices: Vec<u32>,
    /// 全メッシュレットの「三角形コーナー → メッシュレットローカル頂点番号(0..vertex_count)」を
    /// 連結したもの（1 三角形 = 3 バイト）。三角形 t のコーナーは
    /// `meshlet_triangles[m.triangle_offset + t*3 .. +3]`。
    #[serde(default)]
    pub meshlet_triangles: Vec<u8>,
}

impl Primitive {
    pub fn is_skinned(&self) -> bool { !self.skin_vertices.is_empty() }
    /// LOD0 のメッシュレットデータを持つか（GPU メッシュレットカリングの対象か）。
    pub fn has_meshlets(&self) -> bool { !self.meshlets.is_empty() }
}

/// 1 メッシュレットの記述子。境界球（視錐台カリング用）と法線コーン（背面棄却用）を持つ。
///
/// `vertex_offset` / `triangle_offset` は親プリミティブの `meshlet_vertices` /
/// `meshlet_triangles` 配列へのオフセット。座標・法線はすべてモデルローカル空間で、
/// GPU カリング compute がインスタンス行列でワールド空間へ変換する。
///
/// `Pod` 派生はキャッシュ v4 で記述子列を生ブロブ領域（bytemuck ゼロコピー）へ
/// 格納するために必須（数万要素の serde 要素単位シリアライズ/デシリアライズを回避）。
/// 全フィールドが 4 バイト整列の u32/f32 × 12 ＝ 48 バイト・パディングなし。
#[derive(Debug, Clone, Copy, Default, bytemuck::Pod, bytemuck::Zeroable, Serialize, Deserialize)]
#[repr(C)]
pub struct MeshletDesc {
    /// `meshlet_vertices` 先頭からのオフセット（要素単位）
    pub vertex_offset:   u32,
    /// `meshlet_triangles` 先頭からのオフセット（バイト=コーナー単位）
    pub triangle_offset: u32,
    /// このメッシュレットが参照する元頂点の数（<= MESHLET_MAX_VERTS）
    pub vertex_count:    u32,
    /// このメッシュレットの三角形数（<= MESHLET_MAX_TRIS）
    pub triangle_count:  u32,
    /// 境界球中心（モデルローカル空間）
    pub center:      [f32; 3],
    /// 境界球半径（モデルローカル空間）
    pub radius:      f32,
    /// 法線コーン軸（単位ベクトル・モデルローカル空間）
    pub cone_axis:   [f32; 3],
    /// 法線コーンの cutoff（cos。`dot(normalize(center-cam), axis) >= cutoff + r/dist` で背面＝棄却可）
    pub cone_cutoff: f32,
}

/// 頂点データ（GPU バッファに直接アップロードできる `repr(C)` レイアウト）。
#[derive(Debug, Clone, Copy, bytemuck::Pod, bytemuck::Zeroable, Serialize, Deserialize)]
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
#[derive(Debug, Clone, Copy, bytemuck::Pod, bytemuck::Zeroable, Serialize, Deserialize)]
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
///
/// `Clone` は Phase R7 のマテリアルオーバーライド（gpu_resources::GpuModel::apply_overrides）が
/// 埋込マテリアルを複製して factor 等を差し替えた「実効マテリアル」を作るために必要。
#[derive(Clone, Serialize, Deserialize)]
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

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
pub enum AlphaMode {
    /// 完全不透明
    Opaque,
    /// alpha_cutoff 未満のピクセルを破棄
    Mask,
    /// アルファブレンド（半透明）
    Blend,
}

/// テクスチャ参照（Material → TextureData のインデックス）。
#[derive(Clone, Serialize, Deserialize)]
pub struct TextureInfo {
    pub texture_index: usize,
    pub tex_coord_set: u32,
}

#[derive(Clone, Serialize, Deserialize)]
pub struct NormalTextureInfo {
    pub texture_index: usize,
    pub tex_coord_set: u32,
    /// 法線強度スケール
    pub scale: f32,
}

#[derive(Clone, Serialize, Deserialize)]
pub struct OcclusionTextureInfo {
    pub texture_index: usize,
    pub tex_coord_set: u32,
    /// AO 強度（0.0 = 無効, 1.0 = フル）
    pub strength: f32,
}

// ============================================================
//  テクスチャ
// ============================================================

#[derive(Serialize, Deserialize)]
pub struct TextureData {
    pub name:    Option<String>,
    pub source:  TextureSource,
    pub sampler: SamplerData,
    /// true = 法線・MR・AO など線形データテクスチャ（Rgba8Unorm）
    /// false = ベースカラー・エミッシブなど sRGB テクスチャ（Rgba8UnormSrgb）
    ///
    /// `TextureSource::Ready` の場合は `format` フィールドが sRGB/線形の権威となり、
    /// この `linear` フラグは無視される（後方互換のため残置）。
    pub linear:  bool,
}

/// テクスチャの用途分類。派生キャッシュ生成時に最適な BC フォーマットを選ぶために使う。
///
/// `TextureData.linear` だけでは「法線(BC5)」と「MR/AO などの線形 RGBA(BC7)」を
/// 区別できないため、テクスチャがどのマテリアルスロットで参照されるかをローダー側で
/// 集計してこの分類を割り当てる。
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
pub enum TextureUsage {
    /// ベースカラー・エミッシブ（sRGB, RGBA） → BC3 sRGB
    ColorSrgb,
    /// 法線マップ（線形, RG のみ有効） → BC5
    NormalMap,
    /// メタリック-ラフネス・オクルージョン等の線形 RGBA データ → BC3 Unorm
    LinearData,
}

/// テクスチャのピクセル供給元。
#[derive(Serialize, Deserialize)]
pub enum TextureSource {
    /// RGBA8 に正規化済みの埋め込みピクセルデータ（デコード直後・未圧縮）
    Embedded { width: u32, height: u32, pixels: Vec<u8> },
    /// 外部ファイルパス（OBJ/MTL 等）
    FilePath(PathBuf),
    /// GPU に即アップロードできる形（派生キャッシュ由来）。
    ///
    /// BC 圧縮ブロック列、または BC 非対応 GPU 向けの RGBA8 ミップ列を保持する。
    /// `mips[0]` = 最大解像度、以降 1/2 ずつ縮小したミップチェーン。
    Ready {
        format: CachedTexFormat,
        width:  u32,
        height: u32,
        /// ミップレベルごとのバイト列（BC ブロック連結、または RGBA8 行連結）
        mips:   Vec<Vec<u8>>,
    },
}

/// 派生キャッシュに格納する GPU テクスチャフォーマット。
///
/// wgpu の型を直接 serde 直列化せず、自前の安定した列挙で保存する
/// （wgpu のバージョンアップでの表現変化から切り離すため）。
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
pub enum CachedTexFormat {
    /// 非圧縮 RGBA8（線形）。BC 非対応 GPU フォールバック用。
    Rgba8Unorm,
    /// 非圧縮 RGBA8（sRGB）。BC 非対応 GPU フォールバック用。
    Rgba8UnormSrgb,
    /// BC3（sRGB, RGBA）: アルベド・エミッシブ（アルファ対応）
    Bc3RgbaUnormSrgb,
    /// BC3（線形, RGBA）: メタリック-ラフネス・オクルージョン等
    Bc3RgbaUnorm,
    /// BC1（線形, RGB）: アルファ不要な線形データ（8B/ブロックで軽量）
    Bc1RgbaUnorm,
    /// BC5（RG, 線形）: 法線マップ
    Bc5RgUnorm,
    /// BC4（R, 線形）: 単一チャンネルデータ（AO 等）
    Bc4RUnorm,
    /// BC6H（RGB, 符号なし float）: HDR テクスチャ（将来用・フォーマット定義のみ。現状は未生成）
    Bc6hRgbUfloat,
}

impl CachedTexFormat {
    /// 1 ブロック（4×4 テクセル）あたりのバイト数。
    /// 非圧縮 RGBA8 は「1 テクセルあたり」のバイト数（4）を返す。
    pub fn block_bytes(self) -> u32 {
        match self {
            CachedTexFormat::Rgba8Unorm | CachedTexFormat::Rgba8UnormSrgb => 4, // per-texel
            CachedTexFormat::Bc1RgbaUnorm | CachedTexFormat::Bc4RUnorm => 8,
            CachedTexFormat::Bc3RgbaUnormSrgb
            | CachedTexFormat::Bc3RgbaUnorm
            | CachedTexFormat::Bc5RgUnorm
            | CachedTexFormat::Bc6hRgbUfloat => 16,
        }
    }

    /// ブロック圧縮フォーマットかどうか（false なら非圧縮 RGBA8）。
    pub fn is_block_compressed(self) -> bool {
        !matches!(self, CachedTexFormat::Rgba8Unorm | CachedTexFormat::Rgba8UnormSrgb)
    }
}

#[derive(Serialize, Deserialize)]
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

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
pub enum FilterMode {
    Nearest,
    Linear,
    NearestMipmapNearest,
    LinearMipmapNearest,
    NearestMipmapLinear,
    LinearMipmapLinear,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
pub enum WrapMode {
    Repeat,
    MirroredRepeat,
    ClampToEdge,
}

// ============================================================
//  アニメーション
// ============================================================

/// 1 アニメーションクリップ。
#[derive(Serialize, Deserialize)]
pub struct Animation {
    pub name:     String,
    /// クリップの総時間（秒）
    pub duration: f32,
    pub channels: Vec<AnimationChannel>,
}

/// アニメーションチャンネル（ノード × プロパティ）。
#[derive(Serialize, Deserialize)]
pub struct AnimationChannel {
    /// 対象ノードのインデックス（Model::nodes）
    pub target_node_index: usize,
    pub sampler:           AnimationSampler,
}

#[derive(Serialize, Deserialize)]
pub struct AnimationSampler {
    pub interpolation: Interpolation,
    /// キーフレームのタイムスタンプ（秒、昇順）
    pub timestamps: Vec<f32>,
    pub outputs:    AnimationOutputs,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
pub enum Interpolation {
    Linear,
    Step,
    /// 三次スプライン（補間値とその前後の接線を含む）
    CubicSpline,
}

#[derive(Serialize, Deserialize)]
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
#[derive(Serialize, Deserialize)]
pub struct Skin {
    pub name:   String,
    pub joints: Vec<SkinJoint>,
    /// ルートジョイントの joints 配列内インデックス（存在する場合）
    pub root_joint: Option<usize>,
}

/// スキンの 1 ジョイント。
#[derive(Serialize, Deserialize)]
pub struct SkinJoint {
    /// 対応するノードインデックス（Model::nodes）
    pub node_index: usize,
    pub name:       String,
    /// インバースバインド行列（行優先・列ベクトル規約）
    pub inverse_bind_matrix: [[f32; 4]; 4],
}
