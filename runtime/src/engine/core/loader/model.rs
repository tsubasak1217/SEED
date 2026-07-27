use std::path::PathBuf;
use serde::{Serialize, Deserialize};

// 地形レイヤのブレンドスロット数（＝パレット長）。頂点カラーの成分数と一致する。
use crate::engine::terrain::layers::TERRAIN_BLEND_SLOTS;

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

    // ─── 屈折率（Phase RT-Translucency）───────────────────
    /// 屈折率（IOR）。RT-Translucency（translucency=Rt）有効時、AlphaMode::Blend の
    /// 半透明フラグメントがスクリーンスペース屈折の強さに使う（1.0=屈折なし）。
    /// glTF の KHR_materials_ior 拡張から読む（無ければ 1.0）。ガラス≈1.5、水≈1.33。
    /// 旧データ互換のため #[serde(default = "default_ior")]（既定 1.0）。
    #[serde(default = "default_ior")]
    pub ior: f32,

    // ─── 透過率（ガラス表現）───────────────────────────────
    /// 透過率（transmission, 0..1）。KHR_materials_transmission 相当。
    /// アルファ（カバレッジ／被覆）と分離した「向こうがどれだけ透けるか」を制御する。
    /// 0.0 = 従来動作（アルファのみで透け具合が決まる）。1.0 = 最大限に透過する
    /// （ハイライトを保ったまま背景がよく透ける本物のガラス）。
    /// glTF の KHR_materials_transmission 拡張の transmission_factor から読む（無ければ 0.0）。
    /// 旧データ互換のため #[serde(default = "default_transmission")]（既定 0.0＝後方互換）。
    #[serde(default = "default_transmission")]
    pub transmission: f32,

    // ─── 拡散透過（葉・布・紙の逆光透け）───────────────────────
    /// 拡散透過（diffuse_transmission, 0..1）。KHR_materials_diffuse_transmission 相当の簡易版。
    /// 薄い面（葉・布・紙）が**逆光で色付きに透ける**内部散乱を表す。ガラスの鏡面透過
    /// （上の `transmission`）とは別物で、屈折を伴わず base_color で色付けした拡散光が裏へ回る。
    /// 0.0 = 従来動作（不変）。1.0 = 最大限に裏側の光を拾う。
    /// 透過色は base_color を流用する（専用の拡散透過色は将来拡張。glTF 拡張の
    /// diffuseTransmissionColorFactor に相当）。
    /// glTF ロードでは既定 0.0（gltf 1.4 クレートに KHR_materials_diffuse_transmission ヘルパが
    /// 無いため。Inspector の「拡散透過」スライダー／.mat／インライン上書きで設定する）。
    /// 旧データ互換のため #[serde(default = "default_diffuse_transmission")]（既定 0.0＝後方互換）。
    #[serde(default = "default_diffuse_transmission")]
    pub diffuse_transmission: f32,

    // ─── MR テクスチャを無視（実効 metallic/roughness を factor 直値に）───
    /// メタリック・ラフネス（MR）テクスチャを無視するトグル（既定 false）。
    /// glTF PBR では実効 metallic/roughness = factor × MR テクスチャ値のため、MR テクスチャ
    /// 持ちの面はスライダを最大にしても実効 roughness をテクスチャ値以上へ上げられない
    /// （「roughness=1 にしたのに反射が残る」混乱の原因）。
    /// true のとき MR テクスチャのサンプルをスキップし、metallic_factor / roughness_factor を
    /// そのまま最終値にする（ベースカラー・法線・AO・エミッシブのテクスチャには影響しない。MR のみ）。
    /// 分岐は surface_gather.wgsl の MR 採取 1 箇所（forward / G-Buffer 共通の合流点）で行う。
    /// 旧データ互換のため #[serde(default)]（bool の既定は false＝従来動作＝乗算）。
    #[serde(default)]
    pub mr_tex_ignore: bool,

    // ─── 頂点カラー無視（カメラプレビュー地形の簡易描画専用）───
    /// 頂点カラー（in.color）の乗算をスキップするトグル（既定 false）。
    /// true のとき surface_gather.wgsl は `base_color *= in.color` を行わず、
    /// base_color_factor（×ベースカラーテクスチャ）をそのまま使う。
    ///
    /// 【用途】カメラプレビューの地形メッシュ描画専用。地形メッシュの頂点カラーは
    /// 「レイヤブレンド重み」（成分ごとに 0..1、アルファ＝スロット3の重みで大抵 0）であり、
    /// 通常の頂点カラーとして乗算すると RGB がレイヤ重みで着色され（単層なら真っ赤等）、
    /// さらにアルファがほぼ 0 になってプレビュー合成（AlphaBlending ブリット）で
    /// 透けて消える＝「地形が映らない」不具合の直接原因になる。true にすると中立な
    /// 白アルベド・アルファ 1 の簡易表示になり、metallic=0 の簡易マテリアルと組で
    /// 形状・陰影が見える。
    ///
    /// 【serde(skip) の理由】terrain_layers と同じく、この値はランタイムが組み立てる
    /// プレビュー用マテリアルだけが true にする実行時専用フラグで、bincode アセット
    /// キャッシュへ焼く必要がない（焼くと CACHE_FORMAT_VERSION を上げねばならず既存
    /// キャッシュが読めなくなる）。デシリアライズ時は Default（false）が入る。
    #[serde(skip)]
    pub ignore_vertex_color: bool,

    // ─── 汎用ユーザーデータ回線（G-Buffer RT2.a）───────────────
    /// マテリアルが自由な意味で使える 0..1 の 1 チャンネル（既定 0.0）。
    ///
    /// エンジンは意味を一切定めない。濡れ具合・ダメージ量・経年劣化・氷結度など、
    /// タイトル側が「合成（第 3 層）でこの値をどう解釈するか」を決める汎用回線である。
    /// G-Buffer RT2.a（Rgba8Unorm）へ焼かれるため実効精度は 8bit（1/255 刻み）。
    ///
    /// 【なぜ serde(skip) にしないか】terrain_layers 等と違い、この値は**作者が設定して
    /// 保存する**データであり、glTF/OBJ 由来のマテリアルにも .mat にも載り得る。
    /// 実行時に組み立て直せないので、アセットキャッシュ（.smdl）にも焼く必要がある。
    /// そのため CACHE_FORMAT_VERSION を v15 へ上げて旧キャッシュを無効化する
    /// （bincode は自己記述型でないため #[serde(default)] だけでは旧キャッシュを読めない）。
    #[serde(default)]
    pub user_data: f32,

    // ─── シェーディングモデル ID（G-Buffer RT3.a）───────────────
    /// このマテリアルのシェーディングモデル（既定 0 = DefaultPBR）。
    ///
    /// ライティング／合成をマテリアル種別で分岐させるための識別子。現状は DefaultPBR
    /// のみが実装されており、将来トゥーン・異方性ヘア等を 1/2/3 に割り当てる。
    /// 有効ビット幅は `renderer::surface_id::SHADING_MODEL_BITS`（2bit = 4 種）で、
    /// G-Buffer へ書く直前に同モジュールのマスクで切り詰められる。
    /// キャッシュに焼く理由は `user_data` と同じ（v15）。
    #[serde(default)]
    pub shading_model: u8,

    /// glTF 由来の両面フラグ（データ出所の記録用）。
    /// 描画で参照されるのは `cull_face` であり、ロード時に本フラグから初期化される
    /// （`cull_face_from_double_sided`）。
    pub double_sided: bool,

    // ─── カリング面 ───────────────────────────────────────
    /// このマテリアルを描画するときの背面/前面カリング設定。
    /// 描画側（model_drawer / transparency）はこの値でパイプラインバリアントを選ぶ。
    /// 旧データ（本フィールドが無い JSON など）では既定の `Back` になる。
    #[serde(default)]
    pub cull_face: CullFace,

    // ─── 平均アルベド（Phase RT-GI）─────────────────────────
    /// ベースカラーテクスチャのアルファ加重平均（リニア RGB）× base_color_factor を焼いた
    /// プリミティブ平均アルベド。DDGI のヒット点シェーディング近似（bindless 回避）で使う。
    /// テクスチャ無しマテリアルは base_color_factor をそのまま実効アルベドにする。
    /// asset_cache::process_model_textures がデコード済み RGBA から算出（CACHE_FORMAT_VERSION 9）。
    /// 旧キャッシュとの互換のため #[serde(default)]（既定は白）。
    #[serde(default = "default_avg_albedo")]
    pub avg_albedo: [f32; 4],

    /// ベースカラーテクスチャのアルファ加重平均（リニア RGB）**のみ**（base_color_factor を掛ける前）。
    /// テクスチャ無しマテリアルは白 [1,1,1]。`avg_albedo.rgb` は本値 × `base_color_factor.rgb` である。
    ///
    /// 【なぜ factor 抜きの値を別に持つか（色付き影の退行修正）】
    /// インライン マテリアル編集で `base_color_factor` を変えたとき、実効アルベド（GI/色付き影の色相）は
    /// **テクスチャ平均 × 新しい factor** に更新されなければならない。`avg_albedo`（factor 折込済み）だけを
    /// 持っていると、新 factor を折り込むためにテクスチャ平均を復元する必要があるが、元 factor が 0 成分を
    /// 含む（例: 黒ベースの駒 factor=[0,0,0]）と `avg_albedo / 元factor` で復元できない（0/0）。よって
    /// factor を掛ける前のテクスチャ平均を独立に保持し、`eff_avg_albedo` が新 factor を掛け直せるようにする。
    /// 旧キャッシュ互換のため #[serde(default)]（既定は白＝テクスチャ無し相当）。
    #[serde(default = "default_base_color_tex_avg")]
    pub base_color_tex_avg: [f32; 3],

    // ─── 地形レイヤブレンド（Terrain T2）─────────────────────
    /// このマテリアルを「地形スプラットレイヤ」として描画するフラグ（既定 false）。
    ///
    /// true のとき、G-Buffer ジオメトリパスは通常の単一マテリアルシェーダではなく
    /// 地形専用パイプライン（terrain_gbuffer.rs / terrain_gbuffer_write.wgsl）を選び、
    /// 頂点カラー（= レイヤ重み 4 成分）と group3 のレイヤ定義から triplanar で
    /// base_color / roughness / metallic を合成して G-Buffer へ焼く。
    /// 地形メッシュ以外がこのフラグを立てることは想定していない
    /// （terrain_mesh_build.rs だけが true を設定する）。
    ///
    /// 【重要】`#[serde(skip)]` にしてキャッシュのバイナリ表現から完全に外す。
    /// `Material` の serde 用途はアセットキャッシュ（bincode）だけであり、bincode は
    /// 自己記述型ではないためフィールドを 1 つ足すだけで既存キャッシュ全体が
    /// 読めなくなる（`#[serde(default)]` では救えない。CACHE_FORMAT_VERSION の
    /// v8 コメント参照）。このフラグはランタイムが組み立てる地形メッシュ専用で、
    /// キャッシュ対象の glTF/OBJ 由来マテリアルでは常に false なので、
    /// 焼く必要がない＝バージョンを上げず既存キャッシュを維持できる。
    /// デシリアライズ時は Default（false）が入る。
    #[serde(skip)]
    pub terrain_layers: bool,

    /// この地形チャンクが使うレイヤ番号パレット（TERRAIN_BLEND_SLOTS 個）。
    ///
    /// 頂点カラー RGBA は「重み」だけを運び、その i 番目の成分がどのレイヤに対応するかを
    /// 本フィールドが与える（`terrain_palette[i]` = スロット i のレイヤ番号）。
    /// mesh_vertex（22 パイプライン共有）を変えずにレイヤ総数を 16 へ拡張するための仕組み。
    /// 地形以外のマテリアルでは未使用（`terrain_layers == false` なら参照されない）。
    ///
    /// 【重要】`#[serde(skip)]` 必須。`terrain_layers` と同じ理由で、bincode の
    /// アセットキャッシュ（.smdl）にフィールドを 1 つ足すだけで既存キャッシュ全体が
    /// 読めなくなる。地形メッシュはランタイム生成でキャッシュ対象外なので焼く必要がない。
    /// デシリアライズ時は `default_terrain_palette`（恒等パレット）が入る。
    #[serde(skip, default = "default_terrain_palette")]
    pub terrain_palette: [u32; TERRAIN_BLEND_SLOTS],
}

/// terrain_palette の既定値（恒等パレット = レイヤ 0..3 をスロット 0..3 へ素通し）。
///
/// T2 までの 4 層 layers.json はこのパレットで従来どおり描ける（後方互換の要）。
fn default_terrain_palette() -> [u32; TERRAIN_BLEND_SLOTS] {
    std::array::from_fn(|i| i as u32)
}

/// avg_albedo の serde 既定（白）。旧キャッシュ/JSON にキーが無い場合のフォールバック。
fn default_avg_albedo() -> [f32; 4] { [1.0, 1.0, 1.0, 1.0] }

/// base_color_tex_avg の serde 既定（白＝テクスチャ無し相当）。旧キャッシュ/JSON にキーが無い場合のフォールバック。
fn default_base_color_tex_avg() -> [f32; 3] { [1.0, 1.0, 1.0] }

/// ior の serde 既定（1.0＝屈折なし）。旧キャッシュ/JSON にキーが無い場合のフォールバック。
fn default_ior() -> f32 { 1.0 }

/// transmission の serde 既定（0.0＝透過なし＝従来動作）。旧キャッシュ/JSON にキーが無い場合のフォールバック。
fn default_transmission() -> f32 { 0.0 }

/// diffuse_transmission の serde 既定（0.0＝拡散透過なし＝従来動作）。旧キャッシュ/JSON にキーが無い場合のフォールバック。
fn default_diffuse_transmission() -> f32 { 0.0 }

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
            ior:                        1.0,
            transmission:               0.0,
            diffuse_transmission:       0.0,
            mr_tex_ignore:              false,
            ignore_vertex_color:        false,
            // 汎用ユーザーデータは 0（未設定）、シェーディングモデルは DefaultPBR。
            user_data:                  0.0,
            shading_model:              crate::engine::core::renderer::surface_id::SHADING_MODEL_DEFAULT_PBR,
            double_sided:               false,
            cull_face:                  CullFace::Back,
            avg_albedo:                 [1.0, 1.0, 1.0, 1.0],
            base_color_tex_avg:         [1.0, 1.0, 1.0],
            terrain_layers:             false,
            terrain_palette:            default_terrain_palette(),
        }
    }
}

/// マテリアルのアルファ処理方式（glTF `alphaMode` 準拠）。
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
pub enum AlphaMode {
    /// 完全不透明
    Opaque,
    /// alpha_cutoff 未満のピクセルを破棄
    Mask,
    /// アルファブレンド（半透明）
    Blend,
}

// ============================================================
//  カリング面（CullFace）
// ============================================================

/// マテリアル単位のカリング面設定。
///
/// レンダーパイプラインの `cull_mode` に 1:1 対応し、メッシュ系パイプラインは
/// この 3 値ぶんのバリアントとして生成される（`pipeline.rs`）。
/// 描画時はプリミティブのマテリアルの本値でパイプラインを選択する。
///
/// - `Back` : 背面カリング（既定。閉じた不透明形状の標準）。
/// - `Front`: 前面カリング（内側から見る形状・特殊用途）。
/// - `None` : カリングなし＝両面描画（glTF `double_sided` 相当。カーテン・葉・板ポリ）。
///
/// `Default` は `Back`。`#[serde(default)]` と組み合わせることで、本フィールドを持たない
/// 旧データ（.mat / シーン JSON）を読んでも従来どおり背面カリングになる。
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default, Serialize, Deserialize)]
pub enum CullFace {
    /// 背面カリング（既定）
    #[default]
    Back,
    /// 前面カリング
    Front,
    /// カリングなし（両面描画）
    None,
}

/// カリング面の種類数（＝メッシュ系パイプラインのバリアント本数）。
/// パイプライン配列の長さ・インデックス上限として使う（マジックナンバー回避）。
pub const CULL_FACE_COUNT: usize = 3;

/// カリング面の全バリアント。**並びは `CullFace::index()` と一致させること**
/// （パイプライン配列を `std::array::from_fn` で生成する際の走査順に使う）。
pub const CULL_FACE_VARIANTS: [CullFace; CULL_FACE_COUNT] =
    [CullFace::Back, CullFace::Front, CullFace::None];

impl CullFace {
    /// パイプライン配列のインデックス（0..CULL_FACE_COUNT）。
    /// `pipeline.rs` の `build_cull_variants` が生成する配列の並びと一致させること。
    pub fn index(self) -> usize {
        match self {
            CullFace::Back  => 0,
            CullFace::Front => 1,
            CullFace::None  => 2,
        }
    }

    /// パイプライン TOML の `cull_mode` 文字列表現（`pipeline_config` が解釈する値）。
    /// IPC / .mat の文字列表現（小文字化して使う）とも共有する。
    pub fn as_str(self) -> &'static str {
        match self {
            CullFace::Back  => "Back",
            CullFace::Front => "Front",
            CullFace::None  => "None",
        }
    }
}

/// glTF の `double_sided` フラグからカリング面を決める。
///
/// glTF 仕様上 `double_sided == true` は「両面を描画する」を意味するため `None`（カリング無し）、
/// `false` は背面カリング（`Back`）に対応する。純関数として切り出してあるのは、
/// glTF ドキュメントを用意せずにこのマッピングを単体テストできるようにするため。
pub fn cull_face_from_double_sided(double_sided: bool) -> CullFace {
    if double_sided { CullFace::None } else { CullFace::Back }
}

#[cfg(test)]
mod cull_face_tests {
    use super::*;

    /// glTF の double_sided がカリング面へ正しくマップされること。
    #[test]
    fn double_sided_maps_to_cull_none() {
        assert_eq!(cull_face_from_double_sided(true),  CullFace::None);
        assert_eq!(cull_face_from_double_sided(false), CullFace::Back);
    }

    /// Material の既定カリング面は Back（従来挙動と同一）。
    #[test]
    fn default_material_culls_back() {
        assert_eq!(Material::default().cull_face, CullFace::Back);
        assert_eq!(CullFace::default(), CullFace::Back);
    }

    /// パイプライン配列インデックスが 0..CULL_FACE_COUNT に収まり、かつ全て相異なること。
    #[test]
    fn indices_are_unique_and_in_range() {
        let idx = [CullFace::Back.index(), CullFace::Front.index(), CullFace::None.index()];
        for i in idx { assert!(i < CULL_FACE_COUNT); }
        assert_eq!(idx.iter().collect::<std::collections::HashSet<_>>().len(), CULL_FACE_COUNT);
    }

    /// CULL_FACE_VARIANTS の並びが index() と一致すること
    /// （ズレるとパイプライン配列の添字と実際の cull_mode が食い違い、描画が静かに壊れる）。
    #[test]
    fn variants_order_matches_index() {
        for (i, face) in CULL_FACE_VARIANTS.iter().enumerate() {
            assert_eq!(face.index(), i, "CULL_FACE_VARIANTS の並びが CullFace::index() と不一致");
        }
    }
}

/// テクスチャ参照（Material → TextureData のインデックス）。
#[derive(Clone, Serialize, Deserialize)]
pub struct TextureInfo {
    pub texture_index: usize,
    pub tex_coord_set: u32,
}

/// 法線マップテクスチャ参照（`TextureInfo` に強度スケールを加えたもの）。
#[derive(Clone, Serialize, Deserialize)]
pub struct NormalTextureInfo {
    pub texture_index: usize,
    pub tex_coord_set: u32,
    /// 法線強度スケール
    pub scale: f32,
}

/// アンビエントオクルージョンテクスチャ参照（`TextureInfo` に強度を加えたもの）。
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

/// 1 枚のテクスチャ（名前・ピクセル供給元・サンプラー設定）。`Model::textures` の要素。
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
    /// 未デコードの圧縮画像バイト列（PNG/JPG 等。GLB 埋め込み・data URI 由来）。
    ///
    /// 【遅延デコード】glTF ロード時に全画像を RGBA 展開するとピークメモリが
    /// 数 GB に達するため（Sponza 級で約 4.6GB）、パース段階ではエンコード済み
    /// バイトのまま保持し、`asset_cache::process_model_textures` が
    /// ストリーミング（1 枚ずつデコード → ミップ → BC 圧縮 → 即 drop）で処理する。
    EncodedBytes { bytes: Vec<u8> },
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

/// テクスチャのサンプリング設定（フィルタ・ラップモード）。
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

/// テクスチャサンプリングのフィルタ方式（glTF サンプラー準拠）。
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
pub enum FilterMode {
    Nearest,
    Linear,
    NearestMipmapNearest,
    LinearMipmapNearest,
    NearestMipmapLinear,
    LinearMipmapLinear,
}

/// テクスチャ座標が [0,1] 範囲外のときのラップ方式。
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

/// アニメーションチャンネルの補間器。タイムスタンプ列と出力値列、補間方式を持つ。
#[derive(Serialize, Deserialize)]
pub struct AnimationSampler {
    pub interpolation: Interpolation,
    /// キーフレームのタイムスタンプ（秒、昇順）
    pub timestamps: Vec<f32>,
    pub outputs:    AnimationOutputs,
}

/// アニメーションキーフレーム間の補間方式（glTF `interpolation` 準拠）。
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
pub enum Interpolation {
    Linear,
    Step,
    /// 三次スプライン（補間値とその前後の接線を含む）
    CubicSpline,
}

/// アニメーションチャンネルが駆動する対象プロパティごとの出力値列。
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
