// ============================================================
//  skybox.rs — スカイボックス（天球）描画システム（Phase R9）
//
//  ECS の SkyboxComponent（データのみ）を入力に、内向き UV 球メッシュへ
//  equirectangular（正距円筒）テクスチャを方向ベースで貼って天球を描画する。
//
//  【構成（particle_system と同じ 3 フェーズ）】
//    1. collect()  … シーンを走査して描画対象（SkyboxFrame）を収集する。
//                     CameraLocked は最初の 1 つのみ採用（複数は警告して無視）、
//                     WorldAnchored は複数採用する。デバイス不要（&World のみ）。
//    2. sync_gpu() … スカイボックスごとに uniform バッファ＋BindGroup を確保・更新し、
//                     equirect テクスチャを（パスをキーに）ロードしてキャッシュする。
//    3. draw()     … HDR メインパスの最初（不透明より先）で、モード別パイプライン
//                     （CameraLocked=depth 書込 OFF・far 固定 / WorldAnchored=通常深度）で描く。
//
//  【追加コストゼロ】スカイボックスが 1 つも無い（またはテクスチャ未設定）フレームは
//    collect で frame が空になり、sync_gpu / draw は即 return する。
//
//  ※ SkyboxUniform の repr(C) レイアウトは shaders/skybox.wgsl の SkyboxUniform と
//    バイト単位で一致させること（末尾 layout_tests が固定検証する）。
//
//  【HDR 対応】.hdr（Radiance RGBE）/ .exr（OpenEXR）は float デコードし Rgba16Float（リニア）で
//    アップロードする（sRGB 変換なし・1.0 超の輝度を保持）。png/jpeg 等の LDR は従来通り
//    Rgba8UnormSrgb。分岐は拡張子で行う（load_equirect_texture）。
//
//  TODO: キューブマップ 6 枚対応（現状 equirect 1 枚のみ）。
// ============================================================

use std::collections::{HashMap, HashSet};
use std::sync::Arc;

use bytemuck::{Pod, Zeroable};
use wgpu::util::DeviceExt;

use crate::engine::components::{ComponentKind, SkyboxComponent, SkyboxMode, Transform};
use crate::engine::ecs::{Entity, World};
use crate::engine::structs::objects::Actor;

use super::pipeline_config::RenderPipelineBuilder;

// ─── 定数（マジックナンバー禁止）──────────────────────────────

/// UV 球の緯度分割数（stacks）。天球は per-fragment に方向を正規化するため中程度で十分。
const SPHERE_STACKS: u32 = 24;
/// UV 球の経度分割数（sectors）。
const SPHERE_SECTORS: u32 = 48;
/// UV 球の半径（単位球）。CameraLocked は far 固定・WorldAnchored は model でスケールされる。
const SPHERE_RADIUS: f32 = 1.0;

// ─── SkyboxUniform（uniform, 96 バイト）───────────────────────

/// スカイボックスの描画パラメータ uniform（skybox.wgsl の SkyboxUniform と一致）。
///
/// std140：vec3 tint の 4 番目スロットに intensity が同居する。repr(C) の自然オフセットが
/// std140 オフセットと一致する（layout_tests で固定検証）。
#[repr(C)]
#[derive(Copy, Clone, Pod, Zeroable)]
pub struct SkyboxUniform {
    /// WorldAnchored の配置行列（列優先＝CPU 行優先を転置済み）。CameraLocked は単位行列。offset 0
    pub model: [[f32; 4]; 4],
    /// 色味（リニア RGB 乗算）。offset 64
    pub tint: [f32; 3],
    /// 強度（色への乗算）。offset 76（tint の 4 番目スロットに同居）
    pub intensity: f32,
    /// 配置モード（0=CameraLocked / 1=WorldAnchored）。offset 80
    pub mode: u32,
    /// 16 バイト境界パディング（offset 84/88/92）。
    pub _pad0: u32,
    pub _pad1: u32,
    pub _pad2: u32,
}

// ─── SkyboxPipelines（パイプライン＋共有メッシュ・DrawPipelines が保持）──

/// スカイボックス描画パイプライン一式（DrawPipelines::new が 1 度だけ構築）。
///
/// 同一 skybox.toml から depth_write だけ異なる 2 バリアントを構築する
/// （CameraLocked=false / WorldAnchored=true）。group1 BGL は BindGroup 生成に使う。
pub struct SkyboxPipelines {
    /// CameraLocked 用（depth 書込 OFF・far 固定・背景として最初に描く）。
    pub locked: wgpu::RenderPipeline,
    /// WorldAnchored 用（通常深度・実体化）。
    pub anchored: wgpu::RenderPipeline,
    /// group1 レイアウト（skybox uniform + texture + sampler）。per-skybox BG 生成に使う。
    pub group1_bgl: wgpu::BindGroupLayout,
    /// 天球サンプラー（横 Repeat・縦 Clamp のリニアフィルタ）。
    pub sampler: wgpu::Sampler,
    /// 内向き UV 球の頂点バッファ（位置のみ・"pos3" レイアウト）。全スカイボックスで共有。
    pub sphere_vbuf: wgpu::Buffer,
    /// 内向き UV 球のインデックスバッファ（u32）。
    pub sphere_ibuf: wgpu::Buffer,
    /// インデックス数（draw_indexed の範囲）。
    pub index_count: u32,
}

impl SkyboxPipelines {
    /// パイプライン・共有メッシュ・サンプラーを構築する。
    ///
    /// - `sf` : シーン HDR フォーマット（HDR_FORMAT）。color_format="surface" がこれに解決される。
    /// - `df` : 深度フォーマット（DEPTH_FORMAT）。
    pub fn new(
        device: &wgpu::Device,
        sf: wgpu::TextureFormat,
        df: wgpu::TextureFormat,
        cache: Option<&wgpu::PipelineCache>,
    ) -> Self {
        // CameraLocked 用（TOML の depth_write=false をそのまま使う）。返り値の BGL Vec は
        // group 番号順（0=camera, 1=skybox）。group1 を BindGroup 生成用に取り出す。
        let (locked, mut bgls) =
            RenderPipelineBuilder::new(device, include_str!("pipelines/skybox.toml"), sf, df)
                .with_label("skybox_locked")
                .with_cache(cache)
                .build(super::pipeline::get_shader_source);

        // group1（skybox uniform + tex + sampler）を取り出す（index 1）。
        // wgpu の BGL 重複排除により、以降 anchored パイプラインの group1 とも互換になる。
        let group1_bgl = bgls.remove(1);

        // WorldAnchored 用（同一 TOML から depth_write=true へ上書きして再構築）。
        let (anchored, _) =
            RenderPipelineBuilder::new(device, include_str!("pipelines/skybox.toml"), sf, df)
                .with_label("skybox_anchored")
                .with_depth_write(true)
                .with_cache(cache)
                .build(super::pipeline::get_shader_source);

        // 天球サンプラー：経度方向（U）は継ぎ目でループするため Repeat、
        // 緯度方向（V）は極でクランプ。リニアフィルタ。
        let sampler = device.create_sampler(&wgpu::SamplerDescriptor {
            label: Some("Skybox Sampler"),
            address_mode_u: wgpu::AddressMode::Repeat,
            address_mode_v: wgpu::AddressMode::ClampToEdge,
            address_mode_w: wgpu::AddressMode::ClampToEdge,
            mag_filter: wgpu::FilterMode::Linear,
            min_filter: wgpu::FilterMode::Linear,
            mipmap_filter: wgpu::FilterMode::Nearest,
            ..Default::default()
        });

        // 共有の内向き UV 球メッシュ（位置のみ）を生成する。
        let (positions, indices) = generate_uv_sphere(SPHERE_STACKS, SPHERE_SECTORS, SPHERE_RADIUS);
        let sphere_vbuf = device.create_buffer_init(&wgpu::util::BufferInitDescriptor {
            label: Some("Skybox Sphere VBuf"),
            contents: bytemuck::cast_slice(&positions),
            usage: wgpu::BufferUsages::VERTEX,
        });
        let sphere_ibuf = device.create_buffer_init(&wgpu::util::BufferInitDescriptor {
            label: Some("Skybox Sphere IBuf"),
            contents: bytemuck::cast_slice(&indices),
            usage: wgpu::BufferUsages::INDEX,
        });

        Self {
            locked,
            anchored,
            group1_bgl,
            sampler,
            sphere_vbuf,
            sphere_ibuf,
            index_count: indices.len() as u32,
        }
    }
}

// ─── フレーム記述・GPU 状態 ───────────────────────────────────

/// このフレームに描画する 1 スカイボックスの記述（collect が構築）。
struct SkyboxFrame {
    /// スロットの entity（GPU リソースのキャッシュキー）。
    entity: Entity,
    /// テクスチャ参照（assets:// 仮想パス）。
    texture_path: String,
    /// 配置モード（パイプライン選択）。
    mode: SkyboxMode,
    /// GPU へ書き込む uniform（sync_gpu が model 転置・値確定済みで作る）。
    uniform: SkyboxUniform,
}

/// スカイボックス 1 個の GPU 側状態（uniform バッファ＋BindGroup）。
struct SkyboxGpu {
    /// パラメータ uniform（毎フレーム write_buffer で更新）。
    uniform_buf: wgpu::Buffer,
    /// group1 BindGroup（uniform + テクスチャビュー + サンプラー）。
    bind_group: wgpu::BindGroup,
    /// 現在 BindGroup が参照しているテクスチャパス（差し替え検知用）。
    texture_path: String,
}

// ─── SkyboxSystem 本体（App が 1 つ保持）──────────────────────

/// スカイボックス描画システム。エミッタ同様、entity をキーに GPU 状態を保持し、
/// シーンから消えた分は毎フレーム retain で破棄する。テクスチャはパス単位で共有キャッシュする。
pub struct SkyboxSystem {
    /// entity → GPU 状態。
    gpu: HashMap<Entity, SkyboxGpu>,
    /// テクスチャパス → ロード済みビュー（複数スカイボックスで共有。差し替え時のみ再ロード）。
    tex_cache: HashMap<String, Arc<wgpu::TextureView>>,
    /// このフレームの描画対象（collect が毎フレーム再構築）。
    frame: Vec<SkyboxFrame>,
}

impl Default for SkyboxSystem {
    fn default() -> Self {
        Self::new()
    }
}

impl SkyboxSystem {
    /// 空のシステムを生成する（デバイス不要）。
    pub fn new() -> Self {
        Self {
            gpu: HashMap::new(),
            tex_cache: HashMap::new(),
            frame: Vec::new(),
        }
    }

    /// このフレームに描画すべきスカイボックスが 1 つでもあるか（追加コストゼロ判定）。
    pub fn has_skyboxes(&self) -> bool {
        !self.frame.is_empty()
    }

    // ── フェーズ 1: 収集 ──────────────────────────────────────

    /// シーンを走査して描画対象を収集する（デバイス不要）。
    ///
    /// - `wl` に属し `active` なアクターの `enabled` な Skybox スロットのみ対象。
    /// - テクスチャ未設定（空文字）は除外する。
    /// - CameraLocked は最初の 1 つのみ採用（2 つ目以降は警告して無視）。WorldAnchored は複数可。
    pub fn collect(&mut self, world: &World, actors: &[Actor], wl: u32) {
        self.frame.clear();
        let mut has_camera_locked = false;
        gather_skyboxes(world, actors, wl, &mut self.frame, &mut has_camera_locked);
    }

    // ── フェーズ 2: GPU 同期 ──────────────────────────────────

    /// スカイボックスごとの uniform バッファ・BindGroup を確保・更新し、テクスチャをロードする。
    ///
    /// テクスチャのロードに失敗したスカイボックスは frame から取り除く（draw で無視される）。
    /// スカイボックス 0 個なら即 return（追加コストなし）。
    pub fn sync_gpu(
        &mut self,
        device: &wgpu::Device,
        queue: &wgpu::Queue,
        pipelines: &SkyboxPipelines,
    ) {
        if self.frame.is_empty() {
            return;
        }

        // 今フレーム存在する entity 集合（消えた GPU 状態を破棄するため）。
        let mut present: HashSet<Entity> = HashSet::with_capacity(self.frame.len());

        // テクスチャロードに失敗した記述は取り除くため、残すインデックスを別途集める。
        let mut keep: Vec<bool> = Vec::with_capacity(self.frame.len());

        for i in 0..self.frame.len() {
            let entity = self.frame[i].entity;
            let tex_path = self.frame[i].texture_path.clone();
            let uniform = self.frame[i].uniform;

            // ① テクスチャを（キャッシュ経由で）確保する。失敗ならこの記述は捨てる。
            let tex_view = match self.ensure_texture(device, queue, &tex_path) {
                Some(v) => v,
                None => {
                    keep.push(false);
                    continue;
                }
            };
            present.insert(entity);
            keep.push(true);

            // ② GPU 状態（uniform バッファ＋BG）を確保・更新する。
            let need_new_bg = match self.gpu.get(&entity) {
                Some(g) => g.texture_path != tex_path,
                None => true,
            };
            if !self.gpu.contains_key(&entity) {
                // uniform バッファを新規確保する（BG は下で作る）。
                let uniform_buf = device.create_buffer(&wgpu::BufferDescriptor {
                    label: Some("Skybox Uniform"),
                    size: std::mem::size_of::<SkyboxUniform>() as u64,
                    usage: wgpu::BufferUsages::UNIFORM | wgpu::BufferUsages::COPY_DST,
                    mapped_at_creation: false,
                });
                // 仮の BG（下の need_new_bg 分岐で必ず作り直すためダミーではなく本物を作る）。
                let bind_group = create_skybox_bg(device, pipelines, &uniform_buf, &tex_view);
                self.gpu.insert(
                    entity,
                    SkyboxGpu {
                        uniform_buf,
                        bind_group,
                        texture_path: tex_path.clone(),
                    },
                );
            } else if need_new_bg {
                // テクスチャが変わった: BG を作り直す（uniform バッファは再利用）。
                let g = self.gpu.get(&entity).expect("gpu state exists");
                let bind_group = create_skybox_bg(device, pipelines, &g.uniform_buf, &tex_view);
                let g = self.gpu.get_mut(&entity).expect("gpu state exists");
                g.bind_group = bind_group;
                g.texture_path = tex_path.clone();
            }

            // ③ uniform を書き込む。
            let g = self.gpu.get(&entity).expect("gpu state exists");
            queue.write_buffer(&g.uniform_buf, 0, bytemuck::bytes_of(&uniform));
        }

        // ロード失敗の記述を frame から取り除く（描画対象から除外）。
        let mut idx = 0usize;
        self.frame.retain(|_| {
            let k = keep[idx];
            idx += 1;
            k
        });

        // シーンから消えた GPU 状態を破棄する。
        self.gpu.retain(|e, _| present.contains(e));
    }

    /// テクスチャをキャッシュ経由でロードして共有ビューを返す（失敗時 None）。
    fn ensure_texture(
        &mut self,
        device: &wgpu::Device,
        queue: &wgpu::Queue,
        path: &str,
    ) -> Option<Arc<wgpu::TextureView>> {
        if let Some(v) = self.tex_cache.get(path) {
            return Some(Arc::clone(v));
        }
        let view = load_equirect_texture(device, queue, path)?;
        let arc = Arc::new(view);
        self.tex_cache.insert(path.to_string(), Arc::clone(&arc));
        Some(arc)
    }

    // ── フェーズ 3: 描画 ──────────────────────────────────────

    /// 全スカイボックスをモード別パイプラインで描画する（HDR メインパスの最初に呼ぶ）。
    ///
    /// group0=camera（共有カメラ BG）, group1=skybox（uniform+tex+sampler）。
    /// スカイボックス 0 個なら即 return。
    pub fn draw<'p>(
        &'p self,
        pass: &mut wgpu::RenderPass<'p>,
        pipelines: &'p SkyboxPipelines,
        camera_bg: &'p wgpu::BindGroup,
    ) {
        if self.frame.is_empty() {
            return;
        }
        // group0（camera）は両パイプラインでレイアウト共通のため 1 度だけセットする。
        pass.set_bind_group(0, camera_bg, &[]);
        pass.set_vertex_buffer(0, pipelines.sphere_vbuf.slice(..));
        pass.set_index_buffer(pipelines.sphere_ibuf.slice(..), wgpu::IndexFormat::Uint32);

        for f in &self.frame {
            let Some(g) = self.gpu.get(&f.entity) else {
                continue;
            };
            let pipeline = match f.mode {
                SkyboxMode::CameraLocked => &pipelines.locked,
                SkyboxMode::WorldAnchored => &pipelines.anchored,
            };
            pass.set_pipeline(pipeline);
            pass.set_bind_group(1, &g.bind_group, &[]);
            pass.draw_indexed(0..pipelines.index_count, 0, 0..1);
        }
    }
}

// ─── フリー関数 ───────────────────────────────────────────────

/// group1（skybox uniform + texture + sampler）の BindGroup を作る。
fn create_skybox_bg(
    device: &wgpu::Device,
    pipelines: &SkyboxPipelines,
    uniform_buf: &wgpu::Buffer,
    tex_view: &wgpu::TextureView,
) -> wgpu::BindGroup {
    device.create_bind_group(&wgpu::BindGroupDescriptor {
        label: Some("Skybox BG (group1)"),
        layout: &pipelines.group1_bgl,
        entries: &[
            wgpu::BindGroupEntry {
                binding: 0,
                resource: uniform_buf.as_entire_binding(),
            },
            wgpu::BindGroupEntry {
                binding: 1,
                resource: wgpu::BindingResource::TextureView(tex_view),
            },
            wgpu::BindGroupEntry {
                binding: 2,
                resource: wgpu::BindingResource::Sampler(&pipelines.sampler),
            },
        ],
    })
}

/// equirectangular テクスチャをロードして 2D テクスチャビューを返す（失敗時 None）。
///
/// 拡張子で LDR / HDR を分岐する:
///   - `.hdr`（Radiance RGBE）/ `.exr`（OpenEXR）: float デコードし **Rgba16Float**（リニア）で
///     アップロードする。sRGB 変換はしない（HDR は既にリニア）。1.0 超の輝度が保持され、
///     HDR メインパスでそのまま生き、Bloom で太陽が発光する。
///   - それ以外（png/jpeg 等）: 従来通り asset_fs::read_image が 8bit **Rgba8UnormSrgb** で解決する。
///     HDR 化は intensity 倍に委ねる。
///
/// ※ シェーダ（skybox.wgsl）は texture_2d<f32> で受けるため、両フォーマットとも
///   equirect サンプリングの扱いは同一（分岐不要）。
///
/// TODO: 将来課題 — マテリアル等の他テクスチャ経路への .hdr 対応拡張（現状はスカイボックスのみ）。
fn load_equirect_texture(
    device: &wgpu::Device,
    queue: &wgpu::Queue,
    path: &str,
) -> Option<wgpu::TextureView> {
    if is_hdr_path(path) {
        load_equirect_texture_hdr(device, queue, path)
    } else {
        load_equirect_texture_ldr(device, queue, path)
    }
}

/// パスが HDR（float）フォーマットか拡張子で判定する（大文字小文字非依存）。
fn is_hdr_path(path: &str) -> bool {
    let lower = path.to_ascii_lowercase();
    lower.ends_with(".hdr") || lower.ends_with(".exr")
}

/// LDR 画像（png/jpeg 等）を Rgba8UnormSrgb でアップロードする（従来経路）。
fn load_equirect_texture_ldr(
    device: &wgpu::Device,
    queue: &wgpu::Queue,
    path: &str,
) -> Option<wgpu::TextureView> {
    let img = crate::engine::asset_fs::read_image(path);
    let (w, h) = img.dimensions();
    if w == 0 || h == 0 {
        eprintln!("[SEED skybox] テクスチャの寸法が不正: path={path:?} → 描画スキップ");
        return None;
    }
    let texture = device.create_texture(&wgpu::TextureDescriptor {
        label: Some("Skybox Equirect Texture (LDR sRGB)"),
        size: wgpu::Extent3d {
            width: w,
            height: h,
            depth_or_array_layers: 1,
        },
        mip_level_count: 1,
        sample_count: 1,
        dimension: wgpu::TextureDimension::D2,
        format: wgpu::TextureFormat::Rgba8UnormSrgb,
        usage: wgpu::TextureUsages::TEXTURE_BINDING | wgpu::TextureUsages::COPY_DST,
        view_formats: &[],
    });
    queue.write_texture(
        texture.as_image_copy(),
        img.as_raw(),
        wgpu::TexelCopyBufferLayout {
            offset: 0,
            bytes_per_row: Some(4 * w),
            rows_per_image: Some(h),
        },
        wgpu::Extent3d {
            width: w,
            height: h,
            depth_or_array_layers: 1,
        },
    );
    Some(texture.create_view(&wgpu::TextureViewDescriptor::default()))
}

/// HDR 画像（.hdr / .exr）を float デコードして Rgba16Float（リニア）でアップロードする。
///
/// 失敗（読み込み不能・デコード不能・寸法 0）時は明確なエラーログを出して None を返す
/// （呼び出し側 sync_gpu が描画対象から除外し、ピンクのプレースホルダにはしない）。
fn load_equirect_texture_hdr(
    device: &wgpu::Device,
    queue: &wgpu::Queue,
    path: &str,
) -> Option<wgpu::TextureView> {
    // ① バイト列を取得（assets:// 仮想パス／PAK を解決）。
    let bytes = match crate::engine::asset_fs::read_bytes(path) {
        Ok(b) => b,
        Err(e) => {
            eprintln!("[SEED skybox] HDR 読み込み失敗: path={path:?} err={e} → 描画スキップ");
            return None;
        }
    };

    // ② float デコードして (w, h, Rgba16 データ) を得る。
    let (w, h, texels) = match decode_hdr_rgba16f(&bytes) {
        Some(v) => v,
        None => {
            eprintln!(
                "[SEED skybox] HDR デコード失敗（壊れたファイル / 非対応フォーマット）: \
                 path={path:?} → 描画スキップ"
            );
            return None;
        }
    };
    if w == 0 || h == 0 {
        eprintln!("[SEED skybox] HDR テクスチャの寸法が不正: path={path:?} → 描画スキップ");
        return None;
    }

    // ③ Rgba16Float テクスチャを確保（sRGB なし＝リニア。1.0 超の輝度を保持）。
    let texture = device.create_texture(&wgpu::TextureDescriptor {
        label: Some("Skybox Equirect Texture (HDR Rgba16Float)"),
        size: wgpu::Extent3d {
            width: w,
            height: h,
            depth_or_array_layers: 1,
        },
        mip_level_count: 1,
        sample_count: 1,
        dimension: wgpu::TextureDimension::D2,
        format: wgpu::TextureFormat::Rgba16Float,
        usage: wgpu::TextureUsages::TEXTURE_BINDING | wgpu::TextureUsages::COPY_DST,
        view_formats: &[],
    });
    // Rgba16Float: 1 テクセル = 4 チャンネル × 2 バイト = 8 バイト。
    const BYTES_PER_TEXEL: u32 = 8;
    queue.write_texture(
        texture.as_image_copy(),
        bytemuck::cast_slice(&texels),
        wgpu::TexelCopyBufferLayout {
            offset: 0,
            bytes_per_row: Some(BYTES_PER_TEXEL * w),
            rows_per_image: Some(h),
        },
        wgpu::Extent3d {
            width: w,
            height: h,
            depth_or_array_layers: 1,
        },
    );
    Some(texture.create_view(&wgpu::TextureViewDescriptor::default()))
}

/// HDR バイト列を float デコードし、(幅, 高さ, Rgba16Float テクセル列) を返す（失敗時 None）。
///
/// image クレート（hdr / exr feature）が Radiance HDR / OpenEXR を自動判別して float デコードする。
/// RGB(f32) を Rgba(f16) へ変換する（アルファは常に 1.0）。
fn decode_hdr_rgba16f(bytes: &[u8]) -> Option<(u32, u32, Vec<half::f16>)> {
    // フォーマットは image 側でマジックバイトから自動判別（.hdr=RGBE / .exr=OpenEXR）。
    let img = image::load_from_memory(bytes).ok()?;
    let rgb = img.to_rgb32f(); // ImageBuffer<Rgb<f32>, Vec<f32>>（リニア float）
    let (w, h) = rgb.dimensions();
    let mut texels: Vec<half::f16> = Vec::with_capacity((w as usize * h as usize) * 4);
    let one = half::f16::from_f32(1.0);
    for px in rgb.pixels() {
        texels.push(half::f16::from_f32(px[0]));
        texels.push(half::f16::from_f32(px[1]));
        texels.push(half::f16::from_f32(px[2]));
        texels.push(one); // アルファ（天球は不透明）。
    }
    Some((w, h, texels))
}

/// シーンを DFS 走査してスカイボックスを収集する（CameraLocked は 1 つのみ）。
fn gather_skyboxes(
    world: &World,
    actors: &[Actor],
    wl: u32,
    out: &mut Vec<SkyboxFrame>,
    has_camera_locked: &mut bool,
) {
    for actor in actors {
        if actor.world_line != wl {
            continue;
        }
        // 非アクティブアクターはサブツリーごと除外する。
        if !actor.active {
            continue;
        }

        // WorldAnchored の配置行列（無い＝単位行列）。CameraLocked では未使用。
        let world_mat = world.get::<Transform>(actor.entity).map(|t| t.to_mat4());

        for slot in actor.slots() {
            if slot.kind != ComponentKind::Skybox || !slot.enabled {
                continue;
            }
            let Some(sb) = world.get::<SkyboxComponent>(slot.entity) else {
                continue;
            };

            // テクスチャ未設定は描画しない。
            if sb.texture_path.trim().is_empty() {
                continue;
            }

            // CameraLocked は最初の 1 つのみ有効。
            if sb.mode == SkyboxMode::CameraLocked {
                if *has_camera_locked {
                    eprintln!(
                        "[SEED skybox] CameraLocked スカイボックスが複数あります。\
                         最初の 1 つのみ有効化し、以降（actor='{}'）は無視します。",
                        actor.name,
                    );
                    continue;
                }
                *has_camera_locked = true;
            }

            // 行優先（CPU）→ 列優先（GPU）へ転置する。CameraLocked は単位行列。
            let model = match sb.mode {
                SkyboxMode::WorldAnchored => transpose4x4(&world_mat.unwrap_or(IDENTITY4)),
                SkyboxMode::CameraLocked => IDENTITY4,
            };

            out.push(SkyboxFrame {
                entity: slot.entity,
                texture_path: sb.texture_path.clone(),
                mode: sb.mode,
                uniform: SkyboxUniform {
                    model,
                    tint: sb.tint,
                    intensity: sb.intensity.max(0.0),
                    mode: sb.mode.to_code(),
                    _pad0: 0,
                    _pad1: 0,
                    _pad2: 0,
                },
            });
        }

        // 子アクターを再帰処理（アクティブなアクターのみ到達する）。
        gather_skyboxes(world, actor.children(), wl, out, has_camera_locked);
    }
}

/// 単位行列（列優先＝転置しても同一）。
const IDENTITY4: [[f32; 4]; 4] = [
    [1.0, 0.0, 0.0, 0.0],
    [0.0, 1.0, 0.0, 0.0],
    [0.0, 0.0, 1.0, 0.0],
    [0.0, 0.0, 0.0, 1.0],
];

/// 行優先行列（CPU）→ 列優先行列（GPU）への転置。
fn transpose4x4(m: &[[f32; 4]; 4]) -> [[f32; 4]; 4] {
    let mut out = [[0.0f32; 4]; 4];
    for i in 0..4 {
        for j in 0..4 {
            out[i][j] = m[j][i];
        }
    }
    out
}

/// 内向き UV 球（位置のみ・半径 r）を生成する。
///
/// 経度 `sectors` × 緯度 `stacks` の格子で頂点を作り、三角形は「内側から見て正しく
/// 見える」よう内向き（法線が中心を向く巻き）で張る。頂点属性は位置のみ（stride 12）で、
/// フラグメントが位置を正規化して equirect 方向とする。
fn generate_uv_sphere(stacks: u32, sectors: u32, r: f32) -> (Vec<[f32; 3]>, Vec<u32>) {
    use std::f32::consts::PI;

    let mut positions: Vec<[f32; 3]> = Vec::with_capacity(((stacks + 1) * (sectors + 1)) as usize);
    // 頂点: stack（緯度 0..=π）× sector（経度 0..=2π）。
    for i in 0..=stacks {
        let v = i as f32 / stacks as f32; // 0..1
        let phi = v * PI; // 緯度（0=北極, π=南極）
        let (sin_phi, cos_phi) = phi.sin_cos();
        for j in 0..=sectors {
            let u = j as f32 / sectors as f32; // 0..1
            let theta = u * 2.0 * PI; // 経度
            let (sin_th, cos_th) = theta.sin_cos();
            // y-up 球面（fs の equirect 変換 u=atan2(z,x), v=acos(y) と整合させる）。
            let x = sin_phi * cos_th;
            let y = cos_phi;
            let z = sin_phi * sin_th;
            positions.push([x * r, y * r, z * r]);
        }
    }

    // インデックス: 各格子セルを 2 三角形で張る。内向き（中心から見て表）になる巻き順にする。
    let stride = sectors + 1;
    let mut indices: Vec<u32> = Vec::with_capacity((stacks * sectors * 6) as usize);
    for i in 0..stacks {
        for j in 0..sectors {
            let a = i * stride + j;
            let b = a + stride;
            // 内向きにするため、外向き（a, b, a+1 / a+1, b, b+1）の巻きを反転する。
            indices.extend_from_slice(&[a, a + 1, b]);
            indices.extend_from_slice(&[a + 1, b + 1, b]);
        }
    }
    (positions, indices)
}

// ─── レイアウト検証テスト ──────────────────────────────────────
#[cfg(test)]
mod layout_tests {
    use super::*;
    use std::mem::{offset_of, size_of};

    /// SkyboxUniform は 96 バイト。vec3 tint の 4 番目スロットに intensity が同居する。
    #[test]
    fn skybox_uniform_layout() {
        assert_eq!(size_of::<SkyboxUniform>(), 96, "SkyboxUniform は 96 バイト");
        assert_eq!(offset_of!(SkyboxUniform, model), 0);
        assert_eq!(offset_of!(SkyboxUniform, tint), 64);
        assert_eq!(offset_of!(SkyboxUniform, intensity), 76);
        assert_eq!(offset_of!(SkyboxUniform, mode), 80);
    }

    /// UV 球の頂点数・インデックス数が格子数から決まる本数であること。
    #[test]
    fn uv_sphere_counts() {
        let (pos, idx) = generate_uv_sphere(4, 6, 1.0);
        assert_eq!(pos.len(), (4 + 1) * (6 + 1));
        assert_eq!(idx.len(), (4 * 6 * 6) as usize);
        // 半径 1 の球面上にあること（先頭頂点は北極 y=1）。
        assert!((pos[0][1] - 1.0).abs() < 1e-5);
    }

    /// 拡張子による HDR / LDR 判定（大文字小文字・仮想パスを含む）。
    #[test]
    fn hdr_path_detection() {
        assert!(is_hdr_path("assets://sky/day.hdr"));
        assert!(is_hdr_path("assets://sky/DAY.HDR"));
        assert!(is_hdr_path("C:/x/env.exr"));
        assert!(!is_hdr_path("assets://sky/day.png"));
        assert!(!is_hdr_path("assets://sky/day.jpg"));
        assert!(!is_hdr_path("noextension"));
    }

    /// Radiance HDR（RGBE）を生成 → float デコード → Rgba16Float 変換を検証する。
    ///
    /// 1.0 超の輝度（4.0）が f16 で保持されること（HDR パイプラインで生きる前提）を確認する。
    #[test]
    fn decode_hdr_preserves_high_luminance() {
        use image::Rgb;
        use image::codecs::hdr::HdrEncoder;

        // 2×2 の HDR 画像を生成（各ピクセル R=1.0, G=2.0, B=4.0）。
        // 2 の冪は RGBE エンコードで（ほぼ）正確に往復する。
        const W: usize = 2;
        const H: usize = 2;
        let pixels = vec![Rgb([1.0f32, 2.0, 4.0]); W * H];

        let mut buf: Vec<u8> = Vec::new();
        HdrEncoder::new(&mut buf)
            .encode(&pixels, W, H)
            .expect("HDR エンコード成功");

        // float デコード（本番と同一経路）。
        let (w, h, texels) = decode_hdr_rgba16f(&buf).expect("HDR デコード成功");
        assert_eq!((w, h), (W as u32, H as u32));
        assert_eq!(texels.len(), W * H * 4, "Rgba16 = 4 チャンネル/テクセル");

        // 先頭テクセルの RGBA を f32 に戻して検証（RGBE 量子化 + f16 で許容誤差あり）。
        let r = texels[0].to_f32();
        let g = texels[1].to_f32();
        let b = texels[2].to_f32();
        let a = texels[3].to_f32();
        assert!((r - 1.0).abs() < 0.05, "R≈1.0 だが {r}");
        assert!((g - 2.0).abs() < 0.05, "G≈2.0 だが {g}");
        // 1.0 超が保持されていること（クランプされていない）が最重要。
        assert!(b > 3.5, "B は 1.0 超（≈4.0）を保持すべきだが {b}");
        assert!((a - 1.0).abs() < 1e-3, "アルファは 1.0 固定だが {a}");
    }
}

// ─── WGSL 静的検証（naga parse + validate）─────────────────────
#[cfg(test)]
mod shader_tests {
    /// skybox.wgsl を naga で parse + validate する（自己完結でパース可能）。
    #[test]
    fn skybox_shader_parses_and_validates() {
        let src = include_str!("shaders/skybox.wgsl");
        let module = naga::front::wgsl::parse_str(src)
            .unwrap_or_else(|e| panic!("[skybox] WGSL parse 失敗: {e:?}"));
        let mut validator = naga::valid::Validator::new(
            naga::valid::ValidationFlags::all(),
            naga::valid::Capabilities::empty(),
        );
        validator
            .validate(&module)
            .unwrap_or_else(|e| panic!("[skybox] WGSL validate 失敗: {e:?}"));
    }
}
