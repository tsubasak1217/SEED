// ============================================================
//  sprite_skin.rs — 2D メッシュ変形スキニングの GPU 資源とディスパッチ
//
//  【方式（なぜ compute なのか）】
//  スキニングはコンピュートシェーダで行い、**変形後の頂点を
//  `sprite_vertex` レイアウト（pos.xy + uv.xy = 16 bytes）で書き出す**。
//  この出力バッファは既存のスプライトパイプライン（sprite.wgsl）と
//  キャンバス ID パス（canvas_id.wgsl）の slot0 頂点バッファと**同一形式**なので、
//  描画側は「ユニットクワッドの代わりにこのバッファを差して draw_indexed する」
//  だけでよい。3D の GPU スキニング（skin_system.rs / skin_compute.wgsl）と
//  同じ「compute で変形 → 通常パイプラインで描画」という構造を 2D へ流用した形。
//
//  【リソースの持ち方】
//  - `GpuSpriteMesh`  : `.sprite_mesh` 1 つぶん。バインドポーズ頂点（storage）と
//                       インデックスバッファ。**パス単位でキャッシュ・共有**する。
//  - `SpriteSkinInstance` : スロット（= 1 体）1 つぶん。ボーンパレットと
//                       変形後頂点バッファ、compute の BindGroup。
//                       **スロット Entity 単位でキャッシュ**するので、
//                       同じメッシュを複数体が使っても各体が別パレットを引く。
//
//  【ディスパッチのタイミング】
//  収集フェーズ（canvas_collect）から `prepare_instance` を呼び、その場で
//  compute を submit する。A1 時点ではスキンスプライトの体数が少ない前提のため
//  1 体 1 submit で十分単純・確実である
//  （TODO: 体数が増えたら 1 フレーム 1 エンコーダへまとめる）。
// ============================================================

use std::cell::RefCell;
use std::collections::{HashMap, HashSet};
use std::sync::Arc;

use crate::engine::components::{CanvasTransform, SkinnedSpriteComponent};
use crate::engine::core::loader::sprite_mesh::{
    IDENTITY_MAT4, MAX_BONE_INFLUENCES, SpriteMesh, SpriteMeshError,
};
use crate::engine::ecs::{Entity, World};
use crate::engine::methods::gizmo_interact::mat4x4_mul;
use crate::engine::structs::objects::Actor;

// ─── 定数（マジックナンバー排除） ─────────────────────────────

/// コンピュートのワークグループサイズ（sprite_skin.wgsl の WORKGROUP_SIZE と一致必須）。
const SPRITE_SKIN_WORKGROUP_SIZE: u32 = 64;
/// ボーンパレット 1 本ぶんの vec4 個数（2D アフィンの 2 行）。
const PALETTE_VEC4_PER_BONE: usize = 2;
/// 変形後頂点 1 つぶんのバイト数（sprite_vertex の array_stride と一致必須）。
const DEFORMED_VERTEX_SIZE: u64 = 16;

// ============================================================
//  GPU へ送るデータ型
// ============================================================

/// バインドポーズ頂点（sprite_skin.wgsl の SkinVertex と 1:1・48 bytes）。
#[repr(C)]
#[derive(Copy, Clone, bytemuck::Pod, bytemuck::Zeroable)]
struct SpriteSkinVertex {
    /// スプライトローカル位置（キャンバスピクセル）
    pos: [f32; 2],
    /// UV 座標
    uv: [f32; 2],
    /// 影響ボーンインデックス
    bones: [u32; 4],
    /// 正規化済みウェイト
    weights: [f32; 4],
}

/// コンピュートのパラメータ（sprite_skin.wgsl の SkinParams と 1:1・16 bytes）。
#[repr(C)]
#[derive(Copy, Clone, bytemuck::Pod, bytemuck::Zeroable)]
struct SpriteSkinParams {
    vertex_count: u32,
    bone_count: u32,
    _pad0: u32,
    _pad1: u32,
}

// ============================================================
//  描画側へ渡すハンドル
// ============================================================

/// スキン済みメッシュを 1 ドローで描くのに必要な GPU ハンドル一式。
///
/// スプライトバッチャ（batch2d）とキャンバス ID パスの両方がこれを受け取り、
/// slot0 頂点バッファとインデックスバッファを差し替えて `draw_indexed` する。
pub struct SkinnedSpriteDraw {
    /// 変形後頂点バッファ（`sprite_vertex` レイアウト）。
    pub vertex_buffer: Arc<wgpu::Buffer>,
    /// 三角形インデックスバッファ（Uint32）。
    pub index_buffer: Arc<wgpu::Buffer>,
    /// インデックス数。
    pub index_count: u32,
}

// ============================================================
//  SpriteSkinPipeline — コンピュートパイプライン
// ============================================================

/// 2D スキニング コンピュートパイプライン（起動時に 1 度だけ構築）。
pub struct SpriteSkinPipeline {
    pub pipeline: wgpu::ComputePipeline,
    /// group 0: バインドポーズ頂点 / パレット / パラメータ / 出力頂点。
    pub bgl: wgpu::BindGroupLayout,
}

impl SpriteSkinPipeline {
    /// パイプラインと BindGroupLayout を構築する。
    pub fn new(device: &wgpu::Device, cache: Option<&wgpu::PipelineCache>) -> Self {
        let shader = device.create_shader_module(wgpu::ShaderModuleDescriptor {
            label: Some("Sprite Skin Compute Shader"),
            source: wgpu::ShaderSource::Wgsl(include_str!("shaders/sprite_skin.wgsl").into()),
        });

        // storage バッファのエントリを組む小ヘルパー（read_only を切り替える）。
        let storage = |binding: u32, read_only: bool| wgpu::BindGroupLayoutEntry {
            binding,
            visibility: wgpu::ShaderStages::COMPUTE,
            ty: wgpu::BindingType::Buffer {
                ty: wgpu::BufferBindingType::Storage { read_only },
                has_dynamic_offset: false,
                min_binding_size: None,
            },
            count: None,
        };

        let bgl = device.create_bind_group_layout(&wgpu::BindGroupLayoutDescriptor {
            label: Some("Sprite Skin BGL"),
            entries: &[
                storage(0, true),  // バインドポーズ頂点
                storage(1, true),  // ボーンパレット
                wgpu::BindGroupLayoutEntry {
                    binding: 2,
                    visibility: wgpu::ShaderStages::COMPUTE,
                    ty: wgpu::BindingType::Buffer {
                        ty: wgpu::BufferBindingType::Uniform,
                        has_dynamic_offset: false,
                        min_binding_size: None,
                    },
                    count: None,
                },
                storage(3, false), // 変形後頂点
            ],
        });

        let layout = device.create_pipeline_layout(&wgpu::PipelineLayoutDescriptor {
            label: Some("Sprite Skin Pipeline Layout"),
            bind_group_layouts: &[&bgl],
            push_constant_ranges: &[],
        });

        let pipeline = device.create_compute_pipeline(&wgpu::ComputePipelineDescriptor {
            label: Some("Sprite Skin Compute Pipeline"),
            layout: Some(&layout),
            module: &shader,
            entry_point: Some("cs_main"),
            compilation_options: Default::default(),
            cache,
        });

        Self { pipeline, bgl }
    }
}

// ============================================================
//  GpuSpriteMesh — `.sprite_mesh` 1 つぶんの GPU 常駐データ
// ============================================================

/// パス単位でキャッシュされるメッシュ資源（複数体で共有される）。
pub struct GpuSpriteMesh {
    /// CPU 側の検証済みメッシュ（ボーン解決・CPU スキニングに使う）。
    pub mesh: Arc<SpriteMesh>,
    /// バインドポーズ頂点（read-only storage）。
    bind_vertex_buffer: wgpu::Buffer,
    /// 三角形インデックスバッファ（Uint32）。
    index_buffer: Arc<wgpu::Buffer>,
    /// インデックス数。
    index_count: u32,
}

impl GpuSpriteMesh {
    /// 検証済み `SpriteMesh` から GPU バッファを構築する。
    fn upload(device: &wgpu::Device, mesh: Arc<SpriteMesh>, label: &str) -> Self {
        use wgpu::util::DeviceExt;

        // バインドポーズ頂点を GPU レイアウトへ詰め替える
        let verts: Vec<SpriteSkinVertex> = (0..mesh.vertex_count())
            .map(|i| {
                let w = &mesh.weights[i];
                let mut bones = [0u32; MAX_BONE_INFLUENCES];
                let mut weights = [0f32; MAX_BONE_INFLUENCES];
                bones.copy_from_slice(&w.bones);
                weights.copy_from_slice(&w.weights);
                SpriteSkinVertex {
                    pos: mesh.vertices[i],
                    uv: mesh.uvs[i],
                    bones,
                    weights,
                }
            })
            .collect();

        let bind_vertex_buffer = device.create_buffer_init(&wgpu::util::BufferInitDescriptor {
            label: Some(&format!("SpriteMesh BindVerts ({label})")),
            contents: bytemuck::cast_slice(&verts),
            usage: wgpu::BufferUsages::STORAGE,
        });
        let index_buffer = Arc::new(device.create_buffer_init(&wgpu::util::BufferInitDescriptor {
            label: Some(&format!("SpriteMesh Indices ({label})")),
            contents: bytemuck::cast_slice(&mesh.triangles),
            usage: wgpu::BufferUsages::INDEX,
        }));

        Self {
            index_count: mesh.triangles.len() as u32,
            mesh,
            bind_vertex_buffer,
            index_buffer,
        }
    }
}

// ============================================================
//  SpriteSkinInstance — 1 体ぶんの変形資源
// ============================================================

/// スロット（1 体）ごとの変形資源。
struct SpriteSkinInstance {
    /// このインスタンスが使っているメッシュパス（変更検出用）。
    mesh_path: String,
    /// 参照しているメッシュ（頂点数・ボーン数の取得に使う）。
    mesh: Arc<GpuSpriteMesh>,
    /// ボーンパレット（vec4 × 2 × bone_count）。
    palette_buffer: wgpu::Buffer,
    /// パラメータ uniform。
    params_buffer: wgpu::Buffer,
    /// 変形後頂点バッファ（VERTEX | STORAGE）。
    deformed_buffer: Arc<wgpu::Buffer>,
    /// compute の BindGroup（4 つのバッファを束ねたもの）。
    bind_group: wgpu::BindGroup,
}

impl SpriteSkinInstance {
    /// メッシュに合わせて 1 体ぶんのバッファと BindGroup を作る。
    fn new(
        device: &wgpu::Device,
        pipeline: &SpriteSkinPipeline,
        mesh_path: &str,
        mesh: Arc<GpuSpriteMesh>,
    ) -> Self {
        let vcount = mesh.mesh.vertex_count() as u64;
        let bcount = mesh.mesh.bone_count() as u64;

        let palette_buffer = device.create_buffer(&wgpu::BufferDescriptor {
            label: Some("SpriteSkin Bone Palette"),
            // 1 ボーン = vec4 × PALETTE_VEC4_PER_BONE（16 bytes/vec4）
            size: (bcount * PALETTE_VEC4_PER_BONE as u64 * 16).max(16),
            usage: wgpu::BufferUsages::STORAGE | wgpu::BufferUsages::COPY_DST,
            mapped_at_creation: false,
        });
        let params_buffer = device.create_buffer(&wgpu::BufferDescriptor {
            label: Some("SpriteSkin Params"),
            size: std::mem::size_of::<SpriteSkinParams>() as u64,
            usage: wgpu::BufferUsages::UNIFORM | wgpu::BufferUsages::COPY_DST,
            mapped_at_creation: false,
        });
        let deformed_buffer = Arc::new(device.create_buffer(&wgpu::BufferDescriptor {
            label: Some("SpriteSkin Deformed Verts"),
            size: (vcount * DEFORMED_VERTEX_SIZE).max(DEFORMED_VERTEX_SIZE),
            usage: wgpu::BufferUsages::VERTEX | wgpu::BufferUsages::STORAGE,
            mapped_at_creation: false,
        }));

        let bind_group = device.create_bind_group(&wgpu::BindGroupDescriptor {
            label: Some("SpriteSkin BG"),
            layout: &pipeline.bgl,
            entries: &[
                wgpu::BindGroupEntry {
                    binding: 0,
                    resource: mesh.bind_vertex_buffer.as_entire_binding(),
                },
                wgpu::BindGroupEntry {
                    binding: 1,
                    resource: palette_buffer.as_entire_binding(),
                },
                wgpu::BindGroupEntry {
                    binding: 2,
                    resource: params_buffer.as_entire_binding(),
                },
                wgpu::BindGroupEntry {
                    binding: 3,
                    resource: deformed_buffer.as_entire_binding(),
                },
            ],
        });

        Self {
            mesh_path: mesh_path.to_string(),
            mesh,
            palette_buffer,
            params_buffer,
            deformed_buffer,
            bind_group,
        }
    }
}

// ============================================================
//  ボーン解決（CPU）
// ============================================================

/// スプライトルートアクターを基準に、相対パスでボーンアクターの合成行列を求める。
///
/// 各セグメントは子アクター名で照合し、`CanvasTransform::to_mat4()` を掛け合わせる。
/// アンカーは使わない（ボーンアクターは CanvasComponent を持たない前提のため、
/// 描画時の変換連鎖でもアンカーオフセットは 0 になる）。
fn resolve_bone_matrix_by_path(
    root: &Actor,
    world: &World,
    path: &str,
) -> Option<[[f32; 4]; 4]> {
    let mut cur = root;
    let mut m = IDENTITY_MAT4;
    for seg in path.split('/') {
        if seg.is_empty() {
            continue;
        }
        cur = cur.children().iter().find(|c| c.name == seg)?;
        let ct = world.get::<CanvasTransform>(cur.entity)?;
        m = mat4x4_mul(m, ct.to_mat4());
    }
    Some(m)
}

/// 子孫を DFS で探索し、名前一致するアクターまでの合成行列を求める（自動解決）。
///
/// 直下パスで見つからなかったボーンのフォールバック。最初に見つかった 1 つを使う。
fn resolve_bone_matrix_by_name(
    node: &Actor,
    world: &World,
    name: &str,
    acc: [[f32; 4]; 4],
) -> Option<[[f32; 4]; 4]> {
    for child in node.children() {
        let Some(ct) = world.get::<CanvasTransform>(child.entity) else {
            continue;
        };
        let child_mat = mat4x4_mul(acc, ct.to_mat4());
        if child.name == name {
            return Some(child_mat);
        }
        if let Some(found) = resolve_bone_matrix_by_name(child, world, name, child_mat) {
            return Some(found);
        }
    }
    None
}

/// 子孫を DFS で探索し、名前一致するアクターまでの**相対パス**を求める（自動解決）。
///
/// `resolve_bone_matrix_by_name` と**同じ走査順・同じ採用規則**（最初に見つかった 1 つ）
/// で動くため、返すパスは実際に変形へ使われるアクターと必ず一致する。
/// エディタのボーン対応表が「自動解決の結果」を薄字で表示するために使う。
fn resolve_bone_path_by_name(
    node: &Actor,
    world: &World,
    name: &str,
    prefix: &str,
) -> Option<String> {
    for child in node.children() {
        if world.get::<CanvasTransform>(child.entity).is_none() {
            continue;
        }
        let path = if prefix.is_empty() {
            child.name.clone()
        } else {
            format!("{prefix}/{}", child.name)
        };
        if child.name == name {
            return Some(path);
        }
        if let Some(found) = resolve_bone_path_by_name(child, world, name, &path) {
            return Some(found);
        }
    }
    None
}

/// 1 ボーンの解決結果（行列とその出どころ）。
#[derive(Clone, Debug, PartialEq)]
pub struct BoneResolution {
    /// 実際に使われたアクターの**スプライトルート基準の相対パス**。
    /// `None` = 解決できず、バインドポーズ（無変形）で描画される。
    pub path: Option<String>,
    /// `bone_overrides` の明示エントリで解決したなら true。
    pub is_override: bool,
    /// スプライトルート基準のボーンアクター合成行列（未解決なら単位行列）。
    pub current: [[f32; 4]; 4],
}

/// 1 ボーンを「明示パス → 直下名 → 子孫 DFS 名一致」の順で解決する。
///
/// ボーン解決規則の**唯一の実装**。描画（パレット構築）・エディタの対応表表示・
/// CPU ピッキングがすべてこれを通るので、三者の解決結果は必ず一致する。
pub fn resolve_bone(
    comp: &SkinnedSpriteComponent,
    root: &Actor,
    world: &World,
    bone_name: &str,
) -> BoneResolution {
    // ① 明示パス（無ければボーン名を直下パスとして）で解決を試す
    let path = comp.bone_path(bone_name);
    let is_override = comp
        .bone_overrides
        .get(bone_name)
        .is_some_and(|s| !s.is_empty());
    if let Some(current) = resolve_bone_matrix_by_path(root, world, path) {
        return BoneResolution {
            path: Some(path.to_string()),
            is_override,
            current,
        };
    }
    // ② 見つからなければ子孫 DFS の名前一致でフォールバック（自動解決）
    if let Some(current) = resolve_bone_matrix_by_name(root, world, bone_name, IDENTITY_MAT4) {
        return BoneResolution {
            path: resolve_bone_path_by_name(root, world, bone_name, ""),
            is_override: false,
            current,
        };
    }
    // ③ 全滅: バインドポーズ（無変形）
    BoneResolution {
        path: None,
        is_override,
        current: IDENTITY_MAT4,
    }
}

/// メッシュの全ボーンについて `bone_matrix = current_relative × inverse_bind` を計算する。
///
/// 戻り値: (ボーン行列列（`mesh.bones` と同順）, 解決できなかったボーン名の一覧)。
/// CPU スキニング（ピッキング・テスト）と GPU パレット構築の共通の土台。
pub fn build_bone_matrices(
    mesh: &SpriteMesh,
    comp: &SkinnedSpriteComponent,
    root: &Actor,
    world: &World,
) -> (Vec<[[f32; 4]; 4]>, Vec<String>) {
    let mut mats = Vec::with_capacity(mesh.bone_count());
    let mut unresolved = Vec::new();
    for (bi, bone) in mesh.bones.iter().enumerate() {
        let r = resolve_bone(comp, root, world, &bone.name);
        if r.path.is_none() {
            unresolved.push(bone.name.clone());
            mats.push(IDENTITY_MAT4);
        } else {
            mats.push(mat4x4_mul(r.current, mesh.inverse_bind[bi]));
        }
    }
    (mats, unresolved)
}

/// ボーンパレット（GPU へ送る vec4 × 2 × bone_count）を組み立てる。
///
/// `build_bone_matrices` の結果を GPU レイアウトへ詰め替えるだけ
/// （2D アフィンは 6 成分しか要らないので行優先 4×4 の 0/1 行目だけを送る）。
///
/// 戻り値: (パレット, 解決できなかったボーン名の一覧)
fn build_bone_palette(
    mesh: &SpriteMesh,
    comp: &SkinnedSpriteComponent,
    root: &Actor,
    world: &World,
) -> (Vec<[f32; 4]>, Vec<String>) {
    let (mats, unresolved) = build_bone_matrices(mesh, comp, root, world);
    let mut palette: Vec<[f32; 4]> = Vec::with_capacity(mats.len() * PALETTE_VEC4_PER_BONE);
    for m in &mats {
        // 行優先 4×4 の 0/1 行目を (a, b, tx) / (c, d, ty) として詰める
        palette.push([m[0][0], m[0][1], m[0][3], 0.0]);
        palette.push([m[1][0], m[1][1], m[1][3], 0.0]);
    }
    (palette, unresolved)
}

// ============================================================
//  SpriteSkinCache — メッシュ／インスタンスのキャッシュとディスパッチ
// ============================================================

/// スキンスプライトの GPU 資源キャッシュ。
///
/// `DrawContext` が `&self` 共有で持つため、内部可変（RefCell）で包む。
#[derive(Default)]
pub struct SpriteSkinCache {
    /// メッシュパス → GPU メッシュ。None = ロード失敗済み（毎フレームのリトライ防止）。
    meshes: RefCell<HashMap<String, Option<Arc<GpuSpriteMesh>>>>,
    /// スロット Entity → 1 体ぶんの変形資源。
    instances: RefCell<HashMap<Entity, SpriteSkinInstance>>,
    /// 一度だけ出す警告の既出キー（ログ爆発防止）。
    warned: RefCell<HashSet<String>>,
}

impl SpriteSkinCache {
    /// 空のキャッシュを作る。
    pub fn new() -> Self {
        Self::default()
    }

    /// 同じキーの警告を 1 度だけ出す。
    fn warn_once(&self, key: String, message: &str) {
        if self.warned.borrow_mut().insert(key) {
            eprintln!("{message}");
        }
    }

    /// メッシュをキャッシュから取得（無ければロード）する。
    ///
    /// ロード失敗（ファイルが無い・JSON 不正・検証エラー）は `None` を記録して
    /// 以降のフレームで再試行しない。失敗理由は 1 度だけ標準エラーへ出す。
    fn get_or_load_mesh(&self, device: &wgpu::Device, path: &str) -> Option<Arc<GpuSpriteMesh>> {
        if let Some(cached) = self.meshes.borrow().get(path) {
            return cached.clone();
        }
        let loaded = match crate::engine::asset_fs::read_string(path) {
            Ok(src) => match SpriteMesh::from_json(&src) {
                Ok(m) => Some(Arc::new(GpuSpriteMesh::upload(device, Arc::new(m), path))),
                Err(e) => {
                    self.warn_once(
                        format!("mesh_err:{path}"),
                        &format!("[SEED sprite_mesh] '{path}' の読み込みに失敗: {e}"),
                    );
                    None
                }
            },
            Err(e) => {
                self.warn_once(
                    format!("mesh_io:{path}"),
                    &format!(
                        "[SEED sprite_mesh] '{path}' を開けません: {}",
                        SpriteMeshError::Parse(e.to_string())
                    ),
                );
                None
            }
        };
        self.meshes
            .borrow_mut()
            .insert(path.to_string(), loaded.clone());
        loaded
    }

    /// 既に本フレームで変形済みのインスタンスの描画ハンドルを引く。
    ///
    /// `prepare_instance` を呼ばずに**変形結果だけ**を再利用したい経路
    /// （キャンバス ID パス = GPU ピッキング）が使う。まだ一度も準備されて
    /// いないスロットは None（＝ 描画対象でないのでピック対象でもない）。
    pub fn draw_handle(&self, slot_entity: Entity) -> Option<Arc<SkinnedSpriteDraw>> {
        let insts = self.instances.borrow();
        let inst = insts.get(&slot_entity)?;
        Some(Arc::new(SkinnedSpriteDraw {
            vertex_buffer: inst.deformed_buffer.clone(),
            index_buffer: inst.mesh.index_buffer.clone(),
            index_count: inst.mesh.index_count,
        }))
    }

    /// シーン切替・アセット再読込などでキャッシュを丸ごと捨てる。
    pub fn clear(&self) {
        self.meshes.borrow_mut().clear();
        self.instances.borrow_mut().clear();
        self.warned.borrow_mut().clear();
    }

    /// 1 体ぶんのスキニングを準備して、描画に必要なハンドルを返す。
    ///
    /// 具体的には:
    ///   ① メッシュをロード（キャッシュ）
    ///   ② このスロット専用の変形資源を用意（メッシュ変更時は作り直し）
    ///   ③ ボーンアクターから現在姿勢を集めてパレットを作り GPU へ書く
    ///   ④ compute をディスパッチして変形後頂点を作る
    ///
    /// メッシュが無い／壊れている場合は `None`（＝ 描画しない）。
    /// ボーンが解決できない場合はそのボーンだけ無変形（バインドポーズ）になり、
    /// 警告を 1 度だけ出す。
    #[allow(clippy::too_many_arguments)]
    pub fn prepare_instance(
        &self,
        device: &wgpu::Device,
        queue: &wgpu::Queue,
        pipeline: &SpriteSkinPipeline,
        slot_entity: Entity,
        comp: &SkinnedSpriteComponent,
        root: &Actor,
        world: &World,
    ) -> Option<Arc<SkinnedSpriteDraw>> {
        if comp.mesh_path.is_empty() {
            return None;
        }
        let gpu_mesh = self.get_or_load_mesh(device, &comp.mesh_path)?;

        // ── ② インスタンス資源（メッシュが差し替わったら作り直す）──
        {
            let mut insts = self.instances.borrow_mut();
            let needs_new = match insts.get(&slot_entity) {
                Some(inst) => inst.mesh_path != comp.mesh_path,
                None => true,
            };
            if needs_new {
                insts.insert(
                    slot_entity,
                    SpriteSkinInstance::new(device, pipeline, &comp.mesh_path, gpu_mesh.clone()),
                );
            }
        }

        // ── ③ ボーンパレット ──
        let (palette, unresolved) = build_bone_palette(&gpu_mesh.mesh, comp, root, world);
        if !unresolved.is_empty() {
            self.warn_once(
                format!("bones:{}:{}", root.name, comp.mesh_path),
                &format!(
                    "[SEED sprite_mesh] アクター '{}' のスキンスプライト（{}）で\
                     ボーンアクターが見つかりません: {:?}。該当ボーンはバインドポーズ（無変形）で描画します。",
                    root.name, comp.mesh_path, unresolved
                ),
            );
        }

        let insts = self.instances.borrow();
        let inst = insts.get(&slot_entity)?;
        queue.write_buffer(&inst.palette_buffer, 0, bytemuck::cast_slice(&palette));
        queue.write_buffer(
            &inst.params_buffer,
            0,
            bytemuck::bytes_of(&SpriteSkinParams {
                vertex_count: inst.mesh.mesh.vertex_count() as u32,
                bone_count: inst.mesh.mesh.bone_count() as u32,
                _pad0: 0,
                _pad1: 0,
            }),
        );

        // ── ④ ディスパッチ ──
        let mut encoder = device.create_command_encoder(&wgpu::CommandEncoderDescriptor {
            label: Some("Sprite Skin Encoder"),
        });
        {
            let mut pass = encoder.begin_compute_pass(&wgpu::ComputePassDescriptor {
                label: Some("Sprite Skin Pass"),
                timestamp_writes: None,
            });
            pass.set_pipeline(&pipeline.pipeline);
            pass.set_bind_group(0, &inst.bind_group, &[]);
            let groups = inst
                .mesh
                .mesh
                .vertex_count()
                .div_ceil(SPRITE_SKIN_WORKGROUP_SIZE as usize) as u32;
            pass.dispatch_workgroups(groups.max(1), 1, 1);
        }
        queue.submit(std::iter::once(encoder.finish()));

        Some(Arc::new(SkinnedSpriteDraw {
            vertex_buffer: inst.deformed_buffer.clone(),
            index_buffer: inst.mesh.index_buffer.clone(),
            index_count: inst.mesh.index_count,
        }))
    }
}

// ============================================================
//  テスト
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;
    use crate::engine::core::loader::sprite_mesh::transform_point2d;

    /// テスト用フィクスチャ（2 ボーンの帯メッシュ）。
    const TWO_BONE_ARM: &str =
        include_str!("../../../../tests/fixtures/two_bone_arm.sprite_mesh");

    /// スプライトルート + ボーン子アクター（root / root>elbow）を組む。
    ///
    /// `elbow_rotation_deg` で elbow の実行時回転を与えられる。
    fn build_rig(elbow_rotation_deg: f32) -> (World, Actor) {
        let mut world = World::new();

        // スキンスプライトを持つルートアクター
        let root_e = world.spawn();
        world.insert(root_e, CanvasTransform::default());
        let mut root = Actor::new_2d(root_e, "Character");

        // ボーン "root"（バインドポーズと同じ = 無変形）
        let bone_root_e = world.spawn();
        world.insert(bone_root_e, CanvasTransform::default());
        let mut bone_root = Actor::new_2d(bone_root_e, "root");

        // ボーン "elbow"（バインドポーズ (100,0) のまま指定角だけ回す）
        let bone_elbow_e = world.spawn();
        world.insert(
            bone_elbow_e,
            CanvasTransform {
                position: [100.0, 0.0],
                rotation: elbow_rotation_deg,
                ..CanvasTransform::default()
            },
        );
        bone_root.children.push(Actor::new_2d(bone_elbow_e, "elbow"));
        root.children.push(bone_root);

        (world, root)
    }

    /// 2 点がほぼ一致することを検査する。
    fn assert_close(a: [f32; 2], b: [f32; 2], what: &str) {
        assert!(
            (a[0] - b[0]).abs() < 1e-3 && (a[1] - b[1]).abs() < 1e-3,
            "{what}: {a:?} != {b:?}"
        );
    }

    /// 明示オーバーライド無しでも、同名の子孫アクターが名前で自動解決されること。
    /// （"root" は直下だが "elbow" は孫なので、DFS フォールバックが効く必要がある）
    #[test]
    fn bones_resolve_by_name_through_descendants() {
        let mesh = SpriteMesh::from_json(TWO_BONE_ARM).expect("パース成功");
        let comp = SkinnedSpriteComponent::default();
        let (world, root) = build_rig(0.0);

        let (palette, unresolved) = build_bone_palette(&mesh, &comp, &root, &world);
        assert!(unresolved.is_empty(), "全ボーンが解決される: {unresolved:?}");
        assert_eq!(palette.len(), mesh.bone_count() * PALETTE_VEC4_PER_BONE);

        // 無変形なので全頂点がバインドポーズのまま
        let mats = palette_to_matrices(&palette);
        for vi in 0..mesh.vertex_count() {
            assert_close(mesh.skin_vertex(vi, &mats), mesh.vertices[vi], "無変形");
        }
    }

    /// 明示オーバーライド（相対パス）でも解決できること。
    #[test]
    fn bones_resolve_by_explicit_path() {
        let mesh = SpriteMesh::from_json(TWO_BONE_ARM).expect("パース成功");
        let mut comp = SkinnedSpriteComponent::default();
        comp.bone_overrides.insert("elbow".into(), "root/elbow".into());
        let (world, root) = build_rig(0.0);

        let (_, unresolved) = build_bone_palette(&mesh, &comp, &root, &world);
        assert!(unresolved.is_empty(), "明示パスで解決される: {unresolved:?}");
    }

    /// ボーンアクターを回すと、追従頂点が期待位置へ動くこと（GPU と同じ式を CPU で検算）。
    #[test]
    fn rotating_bone_actor_moves_vertices() {
        let mesh = SpriteMesh::from_json(TWO_BONE_ARM).expect("パース成功");
        let comp = SkinnedSpriteComponent::default();
        let (world, root) = build_rig(90.0);

        let (palette, unresolved) = build_bone_palette(&mesh, &comp, &root, &world);
        assert!(unresolved.is_empty());
        let mats = palette_to_matrices(&palette);

        // root 追従の根本頂点は動かない
        assert_close(mesh.skin_vertex(0, &mats), [0.0, -10.0], "v0");
        // elbow 追従の先端頂点は elbow を中心に 90 度回る
        assert_close(mesh.skin_vertex(4, &mats), [110.0, 100.0], "v4");
        assert_close(mesh.skin_vertex(5, &mats), [90.0, 100.0], "v5");
    }

    /// ボーンアクターが 1 本も無いときは全ボーンが未解決になり、
    /// バインドポーズ（無変形）へフォールバックすること。
    #[test]
    fn missing_bone_actors_fall_back_to_bind_pose() {
        let mesh = SpriteMesh::from_json(TWO_BONE_ARM).expect("パース成功");
        let comp = SkinnedSpriteComponent::default();

        // 子アクターを持たないルートだけのリグ
        let mut world = World::new();
        let root_e = world.spawn();
        world.insert(root_e, CanvasTransform::default());
        let root = Actor::new_2d(root_e, "Character");

        let (palette, unresolved) = build_bone_palette(&mesh, &comp, &root, &world);
        assert_eq!(unresolved, std::vec!["root".to_string(), "elbow".to_string()]);
        let mats = palette_to_matrices(&palette);
        for vi in 0..mesh.vertex_count() {
            assert_close(
                mesh.skin_vertex(vi, &mats),
                mesh.vertices[vi],
                "フォールバックはバインドポーズ",
            );
        }
    }

    /// GPU パレット（vec4 × 2）を行優先 4×4 へ戻す（CPU 検算用の逆変換）。
    fn palette_to_matrices(palette: &[[f32; 4]]) -> std::vec::Vec<[[f32; 4]; 4]> {
        palette
            .chunks(PALETTE_VEC4_PER_BONE)
            .map(|c| {
                let (r0, r1) = (c[0], c[1]);
                [
                    [r0[0], r0[1], 0.0, r0[2]],
                    [r1[0], r1[1], 0.0, r1[2]],
                    [0.0, 0.0, 1.0, 0.0],
                    [0.0, 0.0, 0.0, 1.0],
                ]
            })
            .collect()
    }

    /// GPU パレットの詰め方が「行優先 4×4 の 0/1 行目」であること
    /// （sprite_skin.wgsl の apply_bone と同じ解釈になっているかの回帰テスト）。
    #[test]
    fn palette_packing_matches_shader_convention() {
        let m = crate::engine::core::loader::sprite_mesh::trs_to_mat4([7.0, -3.0], 30.0, [2.0, 0.5]);
        let packed = [
            [m[0][0], m[0][1], m[0][3], 0.0],
            [m[1][0], m[1][1], m[1][3], 0.0],
        ];
        let p = [11.0f32, 5.0];
        // シェーダ側 apply_bone と同じ式
        let shader = [
            packed[0][0] * p[0] + packed[0][1] * p[1] + packed[0][2],
            packed[1][0] * p[0] + packed[1][1] * p[1] + packed[1][2],
        ];
        assert_close(shader, transform_point2d(m, p), "パレット詰め替えの整合");
    }

    /// WGSL の静的検証（naga parse + validate）。
    /// 既存の batch2d / 各パイプラインと同じ回帰防止テスト。
    #[test]
    fn sprite_skin_shader_parses_and_validates() {
        let src = include_str!("shaders/sprite_skin.wgsl");
        let module = naga::front::wgsl::parse_str(src)
            .unwrap_or_else(|e| panic!("[sprite_skin] WGSL parse 失敗: {e:?}"));
        let mut validator = naga::valid::Validator::new(
            naga::valid::ValidationFlags::all(),
            naga::valid::Capabilities::empty(),
        );
        validator
            .validate(&module)
            .unwrap_or_else(|e| panic!("[sprite_skin] WGSL validate 失敗: {e:?}"));
    }

    /// GPU 側の頂点構造体サイズが WGSL の想定（48 bytes）と一致すること。
    #[test]
    fn skin_vertex_size_matches_shader() {
        assert_eq!(std::mem::size_of::<super::SpriteSkinVertex>(), 48);
        assert_eq!(std::mem::size_of::<super::SpriteSkinParams>(), 16);
    }

    /// 変形後頂点 1 つぶんのバイト数が sprite_vertex の array_stride と一致すること。
    /// （ここが崩れると変形結果をスプライトパイプラインへ差せなくなる）
    #[test]
    fn deformed_vertex_stride_matches_sprite_vertex() {
        assert_eq!(super::DEFORMED_VERTEX_SIZE, 16);
    }

    // ── ボーン解決の報告（Phase A2: エディタのボーン対応表が使う）────────────

    /// 自動解決したボーンは「実際に使われたアクターの相対パス」を返し、
    /// override フラグは立たない。
    #[test]
    fn resolve_bone_reports_auto_resolved_path() {
        let (world, root) = build_rig(0.0);
        let comp = SkinnedSpriteComponent::default();

        let r = resolve_bone(&comp, &root, &world, "root");
        assert_eq!(r.path.as_deref(), Some("root"));
        assert!(!r.is_override);

        // elbow は root の子（直下パスでは見つからず DFS 名一致で解決される）
        let r = resolve_bone(&comp, &root, &world, "elbow");
        assert_eq!(r.path.as_deref(), Some("root/elbow"));
        assert!(!r.is_override);
    }

    /// 明示オーバーライドで解決したボーンは override フラグが立つ。
    #[test]
    fn resolve_bone_reports_override() {
        let (world, root) = build_rig(0.0);
        let mut comp = SkinnedSpriteComponent::default();
        comp.bone_overrides
            .insert("elbow".into(), "root/elbow".into());

        let r = resolve_bone(&comp, &root, &world, "elbow");
        assert_eq!(r.path.as_deref(), Some("root/elbow"));
        assert!(r.is_override, "明示指定として報告される");
    }

    /// どこにも対応アクターが無いボーンは未解決（path=None・行列は単位）。
    #[test]
    fn resolve_bone_reports_unresolved() {
        let (world, root) = build_rig(0.0);
        let comp = SkinnedSpriteComponent::default();

        let r = resolve_bone(&comp, &root, &world, "no_such_bone");
        assert!(r.path.is_none());
        assert_eq!(r.current, IDENTITY_MAT4);
    }
}
