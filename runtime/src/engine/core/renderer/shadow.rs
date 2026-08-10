// ============================================================
//  shadow.rs — シャドウマップ リソースとパス（Phase R2）
//
//  方向光 CSM（3 カスケード, 2048/カスケード）＋ スポット単一深度マップ
//  （1024, 最大 4 灯）の深度シャドウを提供する。
//
//  【役割分担（ECS/単一責任）】
//    - 本モジュール: シャドウ用 GPU リソース（深度テクスチャ配列・比較サンプラー・
//      シャドウ行列 UBO・カスケード/スポット用シャドウカメラバッファ）の確保、
//      カスケード分割＋タイト正射投影＋テクセルスナップの計算、深度専用シャドウ
//      パスの記録。
//    - frame_renderer 側は「カメラ情報とキャスターを渡して prepare_frame → record を
//      呼ぶ」だけに留める（ロジックは本モジュールへ集約）。
//
//  【バインドグループ】デバイスの max_bind_groups=5（group 0〜4）環境に対応する
//  ため group 5 は新設せず、シャドウ資源はライトの group 4 の binding 2〜5 に同居
//  する。複合 BindGroup の生成は lighting.rs（LightBuffer::new）が担う。
//
//  【シェーダ対応】renderer/shaders/shadow.wgsl（group 4 binding 2〜5）と定数・UBO
//  レイアウトを厳密に一致させること。減衰は shader_fragment.wgsl のライトループ。
//
//  【R2 の割り切り（TODO）】
//    - 影付きは「最初の cast_shadows=true な方向光 1 灯」＋スポット最大 4 灯。
//      point/rect の影は対象外（RT 影 R8 or キューブマップで別途）。
//    - カスケード別カリングは行わず、各カスケードへキャスター全描画。
//    - カスケード境界はスムーズブレンドせず選択のみ（境界フェードは TODO）。
//    - receive_shadows（モデル側の影受け無効化）は未対応（TODO）。
// ============================================================

use crate::engine::structs::tensor::Mat4x4;
use crate::engine::structs::tensor::vector3::Vector3;
use crate::engine::structs::tensor::vector4::Vector4;

use super::gpu_resources::{CameraBuffer, GpuModel, InstancedModelBatch, NUM_LODS};
use super::lighting::GpuLight;
use super::pipeline::ShadowDepthPipelines;
use super::uniforms::CameraUniform;
use super::lighting::{LIGHT_KIND_DIRECTIONAL, LIGHT_KIND_SPOT};

// ─── 定数（shadow.wgsl と一致させること）─────────────────────

/// 方向光カスケードシャドウマップ（CSM）のカスケード数。
pub const CSM_CASCADE_COUNT: usize = 3;
/// CSM 1 カスケードあたりの解像度（正方）。
pub const SHADOW_MAP_SIZE: u32 = 2048;
/// スポットシャドウマップの解像度（正方）。
pub const SPOT_SHADOW_SIZE: u32 = 1024;
/// 影を落とせるスポットの最大数（スポット深度配列のレイヤ数）。
pub const MAX_SHADOW_SPOTS: usize = 4;
/// 実用分割（practical split）の uniform/log ブレンド係数。
/// 0 = 完全 uniform、1 = 完全 log。0.5 で近距離の解像度を確保しつつ遠方も破綻させない。
pub const CSM_SPLIT_LAMBDA: f32 = 0.5;
/// シャドウ深度テクスチャのフォーマット。
pub const SHADOW_DEPTH_FORMAT: wgpu::TextureFormat = wgpu::TextureFormat::Depth32Float;

/// カスケード正射投影の eye をライト方向へ引く倍率（バウンディング球半径 × 本値）。
/// スライスより手前（ライト側）のキャスターを深度マップへ含めるための余裕。
const SHADOW_CASTER_PULLBACK: f32 = 3.0;
/// バウンディング球半径の量子化ステップ（カメラ回転時のシャドウちらつき抑制）。
const RADIUS_QUANTIZE: f32 = 16.0;
/// スポットシャドウ射影のニアクリップ。
const SPOT_SHADOW_NEAR: f32 = 0.05;

// ─── ShadowMatricesUbo（shadow.wgsl ShadowMatrices と一致）───

/// group 4 binding 5 のシャドウ行列 uniform（std140, 480 bytes）。
///
/// | offset | field          | size |
/// |--------|----------------|------|
/// |   0    | cascade_vp[3]  | 192  |
/// | 192    | spot_vp[4]     | 256  |
/// | 448    | cascade_splits |  16  |
/// | 464    | params         |  16  |
/// 行列は GPU 列優先（transpose 済み）で格納する。
#[repr(C)]
#[derive(Clone, Copy, bytemuck::Pod, bytemuck::Zeroable)]
pub struct ShadowMatricesUbo {
    /// カスケードごとの light view-proj（列優先）。
    pub cascade_vp:     [[[f32; 4]; 4]; CSM_CASCADE_COUNT],
    /// スポットごとの light view-proj（列優先）。
    pub spot_vp:        [[[f32; 4]; 4]; MAX_SHADOW_SPOTS],
    /// カスケード遠端のビュー空間距離（x,y,z）。w は未使用。
    pub cascade_splits: [f32; 4],
    /// x=方向光カスケード数（0=方向光影なし）, y=スポット影数, z/w=予約。
    pub params:         [u32; 4],
}

// ─── ShadowPlan ─────────────────────────────────────────────

/// このフレームで描画すべき影の内訳。
pub struct ShadowPlan {
    /// 方向光 CSM を描画するか。
    pub dir_active: bool,
    /// 描画するスポット影の数（0..MAX_SHADOW_SPOTS）。
    pub spot_count: usize,
    /// カスケードごとの light view-proj（ワールド→クリップ, 行優先）。
    /// `record()` がカスケード別キャスターカリングと静的スキップ判定に使う。
    /// `dir_active == false` のときは単位行列（未使用）。
    pub cascade_vp: [Mat4x4<f32>; CSM_CASCADE_COUNT],
}

impl ShadowPlan {
    /// 影パスを 1 つでも描画する必要があるか。
    pub fn any(&self) -> bool { self.dir_active || self.spot_count > 0 }
}

// ─── ShadowResources ────────────────────────────────────────

/// シャドウ用 GPU リソース一式。DrawContext が 1 個保持し使い回す。
pub struct ShadowResources {
    /// 方向光 CSM 深度テクスチャ（Depth32Float, CSM_CASCADE_COUNT レイヤ）。
    _dir_tex:          wgpu::Texture,
    /// カスケードごとの単層ビュー（レンダーターゲット用, D2）。
    dir_layer_views:   Vec<wgpu::TextureView>,
    /// スポット深度テクスチャ（Depth32Float, MAX_SHADOW_SPOTS レイヤ）。
    _spot_tex:         wgpu::Texture,
    /// スポットごとの単層ビュー（レンダーターゲット用, D2）。
    spot_layer_views:  Vec<wgpu::TextureView>,
    // ── group 4 複合 BindGroup（ライト＋シャドウ）生成用の公開リソース ──
    // max_bind_groups=5（group 0〜4）のデバイスがあるため group 5 は新設せず、
    // シャドウ資源はライトの group 4（binding 2〜5）へ同居する。複合 BindGroup
    // 自体は LightBuffer::new が生成する。以下はいずれも生成後不変（シャドウ
    // マップは固定解像度でリサイズ再生成も無い）ため、BG は起動時 1 回で良い。
    /// CSM 全レイヤ配列ビュー（group 4 binding 2, サンプリング用）。
    pub dir_array_view:  wgpu::TextureView,
    /// スポット全レイヤ配列ビュー（group 4 binding 3, サンプリング用）。
    pub spot_array_view: wgpu::TextureView,
    /// 比較サンプラー（group 4 binding 4, LessEqual）。
    pub sampler:         wgpu::Sampler,
    /// シャドウ行列 UBO（group 4 binding 5）。
    pub ubo:             wgpu::Buffer,
    /// カスケードごとのシャドウカメラ（group 0 相当, 深度パスの view-proj）。
    cascade_cams:      Vec<CameraBuffer>,
    /// スポットごとのシャドウカメラ。
    spot_cams:         Vec<CameraBuffer>,
    /// カスケードごとの「前フレームに深度マップへ描いた内容」の署名（静的スキップ用）。
    /// `None` = 内容不明（起動直後・方向光影が無効だったフレームの後）＝必ず描き直す。
    /// `record()` は `&self` で呼ばれるため内部可変で持つ（GPU 資源とは独立の CPU 状態）。
    last_cascade_sigs: std::cell::RefCell<Vec<Option<CascadeDrawSig>>>,
}

/// 1 カスケードが深度マップへ描いた内容を一意に決める署名（静的スキップの比較対象）。
///
/// - `vp`:      カスケードの light view-proj。**ビット一致**で比較する（誤差許容なし）。
///              ライト方向・カメラ姿勢・分割距離のどれが変わってもここに現れる。
/// - `casters`: そのカスケードで実際に描いたキャスターの内容世代を描画順に並べたもの。
///              `InstancedModelBatch::content_generation()` はプロセス内で一意なので、
///              値が一致する ⇒ そのバッチのインスタンス行列・ボーン時刻・LOD 可視数まで
///              含めて完全に同一、が保証される（バッチの解放・再確保でも衝突しない）。
///              カリング結果が変われば要素数か並びが変わるので、それも検出できる。
#[derive(Clone, Debug, PartialEq)]
struct CascadeDrawSig {
    vp:      [[f32; 4]; 4],
    casters: Vec<u64>,
}

impl ShadowResources {
    /// シャドウリソース一式を生成する。
    ///
    /// - `camera_bgl`: mesh パイプラインの group 0（シャドウカメラ用, 互換）。
    ///
    /// group 4 の複合 BindGroup（ライト＋シャドウ）は本構造体を渡して
    /// `LightBuffer::new` が生成する（生成順: ShadowResources → LightBuffer）。
    pub fn new(
        device:     &wgpu::Device,
        camera_bgl: &wgpu::BindGroupLayout,
    ) -> Self {
        // 深度テクスチャ配列を生成するヘルパー。
        let make_array_tex = |label: &str, size: u32, layers: u32| {
            device.create_texture(&wgpu::TextureDescriptor {
                label:           Some(label),
                size:            wgpu::Extent3d {
                    width:                 size,
                    height:                size,
                    depth_or_array_layers: layers,
                },
                mip_level_count: 1,
                sample_count:    1,
                dimension:       wgpu::TextureDimension::D2,
                format:          SHADOW_DEPTH_FORMAT,
                usage:           wgpu::TextureUsages::RENDER_ATTACHMENT
                                   | wgpu::TextureUsages::TEXTURE_BINDING,
                view_formats:    &[],
            })
        };
        // 単層ビュー（レンダーターゲット）を作るヘルパー。
        let make_layer_view = |tex: &wgpu::Texture, label: &str, layer: u32| {
            tex.create_view(&wgpu::TextureViewDescriptor {
                label:             Some(label),
                dimension:         Some(wgpu::TextureViewDimension::D2),
                base_array_layer:  layer,
                array_layer_count: Some(1),
                ..Default::default()
            })
        };
        // 全レイヤ配列ビュー（サンプリング）を作るヘルパー。
        let make_array_view = |tex: &wgpu::Texture, label: &str| {
            tex.create_view(&wgpu::TextureViewDescriptor {
                label:     Some(label),
                dimension: Some(wgpu::TextureViewDimension::D2Array),
                ..Default::default()
            })
        };

        let dir_tex = make_array_tex("CSM Depth Array", SHADOW_MAP_SIZE, CSM_CASCADE_COUNT as u32);
        let dir_layer_views: Vec<_> = (0..CSM_CASCADE_COUNT as u32)
            .map(|i| make_layer_view(&dir_tex, "CSM Layer View", i))
            .collect();
        let dir_array_view = make_array_view(&dir_tex, "CSM Array View");

        let spot_tex = make_array_tex("Spot Shadow Array", SPOT_SHADOW_SIZE, MAX_SHADOW_SPOTS as u32);
        let spot_layer_views: Vec<_> = (0..MAX_SHADOW_SPOTS as u32)
            .map(|i| make_layer_view(&spot_tex, "Spot Layer View", i))
            .collect();
        let spot_array_view = make_array_view(&spot_tex, "Spot Array View");

        // 比較サンプラー（LessEqual）。Linear で HW バイリニア PCF、シェーダの 3x3 と合わせて滑らかに。
        let sampler = device.create_sampler(&wgpu::SamplerDescriptor {
            label:          Some("Shadow Comparison Sampler"),
            address_mode_u: wgpu::AddressMode::ClampToEdge,
            address_mode_v: wgpu::AddressMode::ClampToEdge,
            address_mode_w: wgpu::AddressMode::ClampToEdge,
            mag_filter:     wgpu::FilterMode::Linear,
            min_filter:     wgpu::FilterMode::Linear,
            mipmap_filter:  wgpu::FilterMode::Nearest,
            compare:        Some(wgpu::CompareFunction::LessEqual),
            ..Default::default()
        });

        // シャドウ行列 UBO（ゼロ初期化）。
        let ubo = device.create_buffer(&wgpu::BufferDescriptor {
            label:              Some("Shadow Matrices UBO"),
            size:               std::mem::size_of::<ShadowMatricesUbo>() as u64,
            usage:              wgpu::BufferUsages::UNIFORM | wgpu::BufferUsages::COPY_DST,
            mapped_at_creation: false,
        });

        let cascade_cams: Vec<_> = (0..CSM_CASCADE_COUNT).map(|_| CameraBuffer::new(device, camera_bgl)).collect();
        let spot_cams:    Vec<_> = (0..MAX_SHADOW_SPOTS).map(|_| CameraBuffer::new(device, camera_bgl)).collect();

        Self {
            _dir_tex: dir_tex,
            dir_layer_views,
            _spot_tex: spot_tex,
            spot_layer_views,
            dir_array_view,
            spot_array_view,
            sampler,
            ubo,
            cascade_cams,
            spot_cams,
            // 起動直後は深度マップの内容が未定義なので、全カスケード「要描画」から始める。
            last_cascade_sigs: std::cell::RefCell::new(vec![None; CSM_CASCADE_COUNT]),
        }
    }

    /// フレームのシャドウ行列を計算し UBO とシャドウカメラへ書き込む。
    /// 併せて `lights`（frame_lights）の `shadow_index` を確定させる。
    ///
    /// - collect_gpu_lights は cast_shadows=true のライトへ `shadow_index = 1.0`
    ///   （影希望のセンチネル）を仮設定して渡す。ここで採用可否と実スロットを確定し、
    ///   採用: 方向光→0.0 / スポット→レイヤ番号、非採用: -1.0 に書き換える。
    /// - `has_casters` が false（影を落とすモデルが無い）なら全影を無効化する。
    ///
    /// 戻り値の ShadowPlan.any() が true のときのみ `record()` を呼ぶこと。
    pub fn prepare_frame(
        &self,
        queue:      &wgpu::Queue,
        cam_view:   &Mat4x4<f32>,
        near:       f32,
        far:        f32,
        fov_y:      f32,
        aspect:     f32,
        lights:     &mut [GpuLight],
        has_casters: bool,
    ) -> ShadowPlan {
        let mut ubo: ShadowMatricesUbo = bytemuck::Zeroable::zeroed();

        // 影を落とすモデルが無ければ全影を無効化して早期リターン。
        if !has_casters {
            for l in lights.iter_mut() {
                if wants_shadow(l) { l.shadow_index = -1.0; }
            }
            queue.write_buffer(&self.ubo, 0, bytemuck::bytes_of(&ubo));
            return ShadowPlan {
                dir_active: false,
                spot_count: 0,
                cascade_vp: [Mat4x4::identity(); CSM_CASCADE_COUNT],
            };
        }

        // ── 採用スロットの割り当て ────────────────────────────
        let mut dir_dir:   Option<[f32; 3]> = None;   // 採用方向光の direction
        let mut spot_srcs: Vec<GpuLight>    = Vec::new(); // 採用スポット（行列計算用にコピー）
        for l in lights.iter_mut() {
            if !wants_shadow(l) { continue; }
            match l.kind {
                LIGHT_KIND_DIRECTIONAL => {
                    if dir_dir.is_none() {
                        dir_dir = Some(l.direction);
                        l.shadow_index = 0.0;       // 方向光影は 1 灯のみ（index 0 = CSM 有効）
                    } else {
                        l.shadow_index = -1.0;      // 2 灯目以降の方向光影は不採用
                    }
                }
                LIGHT_KIND_SPOT => {
                    if spot_srcs.len() < MAX_SHADOW_SPOTS {
                        l.shadow_index = spot_srcs.len() as f32;
                        spot_srcs.push(*l);
                    } else {
                        l.shadow_index = -1.0;      // 上限超過は不採用
                    }
                }
                _ => { l.shadow_index = -1.0; }     // point/rect は R2 対象外
            }
        }

        let dir_active = dir_dir.is_some();
        let spot_count = spot_srcs.len();

        // ── 方向光 CSM 行列 ───────────────────────────────────
        // カスケード行列は record() のカリング／静的スキップ判定でも使うため plan で返す。
        let mut cascade_vp = [Mat4x4::identity(); CSM_CASCADE_COUNT];
        if let Some(dir) = dir_dir {
            let (vps, splits) = compute_cascade_matrices(cam_view, near, far, fov_y, aspect, dir);
            cascade_vp = vps;
            for i in 0..CSM_CASCADE_COUNT {
                self.cascade_cams[i].update(queue, &view_proj_uniform(&vps[i], SHADOW_MAP_SIZE as f32));
                ubo.cascade_vp[i] = vps[i].transpose().data;
            }
            ubo.cascade_splits = [splits[0], splits[1], splits[2], far];
        }

        // ── スポット行列 ──────────────────────────────────────
        for (j, s) in spot_srcs.iter().enumerate() {
            let vp = compute_spot_matrix(s);
            self.spot_cams[j].update(queue, &view_proj_uniform(&vp, SPOT_SHADOW_SIZE as f32));
            ubo.spot_vp[j] = vp.transpose().data;
        }

        ubo.params = [
            if dir_active { CSM_CASCADE_COUNT as u32 } else { 0 },
            spot_count as u32,
            0,
            0,
        ];
        queue.write_buffer(&self.ubo, 0, bytemuck::bytes_of(&ubo));

        ShadowPlan { dir_active, spot_count, cascade_vp }
    }

    /// 影パスを記録する（`plan.any()` が true のときのみ呼ぶ）。
    ///
    /// 各カスケード/スポットのレイヤへ深度専用パスを開き、キャスターを描画する。
    /// `casters` はメインパスと同じ (GpuModel, Batch) の並び（cast_shadows で事前フィルタ済み）。
    ///
    /// 方向光カスケードには 2 段の省略が入る（いずれも**描画結果は完全に不変**）:
    ///   ① カスケード別キャスターカリング: そのカスケードの正射ボリューム（クリップ空間）と
    ///      交差しないキャスターを描画から外す。GPU のクリップで捨てられるものを CPU で
    ///      先に落とすだけなので深度バッファの中身は変わらない。
    ///   ② 静的カスケードスキップ: カスケード行列（ビット一致）と、そのカスケードが描く
    ///      キャスター列（内容世代）が前フレームと完全一致するなら、深度パス自体を開かず
    ///      前フレームの深度マップを使い回す。深度テクスチャは常設で他用途に使われないため
    ///      内容は保たれる。
    pub fn record(
        &self,
        encoder:      &mut wgpu::CommandEncoder,
        shadow_pipes: &ShadowDepthPipelines,
        plan:         &ShadowPlan,
        casters:      &[(&GpuModel, &InstancedModelBatch)],
    ) {
        // 方向光カスケード。
        if plan.dir_active {
            // キャスターのワールド AABB は全カスケードで共用するので 1 回だけ求める。
            // None = 境界不明（update 未実行など）＝カリング対象外（必ず描く）。
            let caster_bounds: Vec<Option<([f32; 3], [f32; 3])>> = casters.iter()
                .map(|(_, batch)| caster_cull_bounds(batch))
                .collect();

            let mut last_sigs = self.last_cascade_sigs.borrow_mut();
            for i in 0..CSM_CASCADE_COUNT {
                let vp = &plan.cascade_vp[i];
                // ── ① カスケード別キャスターカリング ──────────────────
                let visible: Vec<usize> = (0..casters.len())
                    .filter(|&k| match caster_bounds[k] {
                        Some((mn, mx)) => !aabb_outside_clip_volume(vp, mn, mx),
                        None           => true,
                    })
                    .collect();

                // ── ② 静的カスケードスキップ ─────────────────────────
                let sig = CascadeDrawSig {
                    vp:      vp.data,
                    casters: visible.iter().map(|&k| casters[k].1.content_generation()).collect(),
                };
                if last_sigs[i].as_ref() == Some(&sig) {
                    // 行列もキャスターも 1 ビット変わっていない ⇒ 前フレームの深度が正しいまま。
                    continue;
                }
                last_sigs[i] = Some(sig);

                let mut pass = begin_depth_layer_pass(encoder, &self.dir_layer_views[i], "CSM Cascade Pass");
                for &k in &visible {
                    let (gpu, batch) = casters[k];
                    draw_caster(&mut pass, gpu, batch, &self.cascade_cams[i].bind_group, shadow_pipes);
                }
            }
        } else {
            // 方向光影が無効なフレームは深度マップの内容が保証されないので、
            // 次に有効化されたフレームで必ず描き直させる（スキップ判定をリセット）。
            for s in self.last_cascade_sigs.borrow_mut().iter_mut() { *s = None; }
        }
        // スポット。
        for j in 0..plan.spot_count {
            let mut pass = begin_depth_layer_pass(encoder, &self.spot_layer_views[j], "Spot Shadow Pass");
            for (gpu, batch) in casters {
                draw_caster(&mut pass, gpu, batch, &self.spot_cams[j].bind_group, shadow_pipes);
            }
        }
    }
}

/// ライトが影を希望しているか（collect が付けたセンチネル shadow_index≈1.0）。
fn wants_shadow(l: &GpuLight) -> bool { l.shadow_index > 0.5 }

/// Mat4x4（行優先）を CameraUniform（GPU 列優先 view_proj）へ変換する。
/// 深度シェーダ（depth_prepass.wgsl）は u_camera.view_proj のみ参照する。
///
/// inv_view_proj はこのシャドウ深度カメラでは使われないが（deferred_lighting.wgsl 専用の
/// フィールド）、CameraUniform 構造体を埋める必要があるため一律で計算する。
/// 特異行列（逆行列なし）の場合は単位行列へフォールバックする（パニックさせない）。
fn view_proj_uniform(vp: &Mat4x4<f32>, size: f32) -> CameraUniform {
    let inv_vp = vp.inverse().unwrap_or_else(Mat4x4::identity);
    CameraUniform {
        view_proj:      vp.transpose().data,
        view:           Mat4x4::identity().data,
        position:       [0.0, 0.0, 0.0],
        // シャドウ深度パスは深度しか書かずライティング（＝ShadingSurface）を作らないので、
        // 時間フィールドは参照されない。0.0 固定でよい。
        time:           0.0,
        resolution:     [size, size],
        _pad2:          [0.0, 0.0],
        inv_view_proj:  inv_vp.transpose().data,
        // シャドウ深度カメラは速度バッファを持たない（深度しか書かない）ため prev=curr。
        prev_view_proj: vp.transpose().data,
        // シャドウマップは正方アトラス全面へ描く（set_viewport のオフセットは使わない）。
        viewport:       [0.0, 0.0, size, size],
    }
}

/// 深度専用レイヤパスを開く（カラーなし・深度クリア 1.0）。
fn begin_depth_layer_pass<'a>(
    encoder: &'a mut wgpu::CommandEncoder,
    view:    &'a wgpu::TextureView,
    label:   &str,
) -> wgpu::RenderPass<'a> {
    encoder.begin_render_pass(&wgpu::RenderPassDescriptor {
        label:             Some(label),
        color_attachments: &[],
        depth_stencil_attachment: Some(wgpu::RenderPassDepthStencilAttachment {
            view,
            depth_ops: Some(wgpu::Operations {
                load:  wgpu::LoadOp::Clear(1.0),
                store: wgpu::StoreOp::Store,
            }),
            stencil_ops: None, // Depth32Float はステンシルを持たない
        }),
        occlusion_query_set: None,
        timestamp_writes:    None,
    })
}

/// 1 キャスターを深度専用パイプラインで描画する（draw_model_indirect の深度版）。
///
/// mesh 深度パイプラインは group 0(camera)+1(model) のみ。
/// skinned 深度パイプラインは group 0+1+2(空 gap)+3(joints)。
fn draw_caster<'pass>(
    pass:         &mut wgpu::RenderPass<'pass>,
    gpu_model:    &'pass GpuModel,
    batch:        &'pass InstancedModelBatch,
    cam_bg:       &'pass wgpu::BindGroup,
    shadow_pipes: &'pass ShadowDepthPipelines,
) {
    if batch.n_prims == 0 { return; }

    for lod in 0..NUM_LODS {
        let visible = batch.lod_visible_counts[lod];
        if visible == 0 { continue; }

        let joint_bg = batch.joint_vs_bg(lod);
        let mut cur_skinned: Option<bool> = None;

        for draw in &batch.node_prim_list {
            let Some((_, model_bg)) = batch.lod_node_data[lod][draw.node_idx].as_ref() else { continue };

            let gpu_mesh = &gpu_model.meshes[draw.mesh_idx];
            let prim     = &gpu_mesh.primitives[draw.prim_idx];

            // ── パイプライン切り替え（スキン/非スキン）──────────
            if cur_skinned != Some(draw.is_skinned) {
                if draw.is_skinned {
                    pass.set_pipeline(&shadow_pipes.skinned);
                    // group 2 は空 gap（depth_prepass_skinned が group3 を joints に使う都合）。
                    pass.set_bind_group(2, &shadow_pipes.empty_bg2, &[]);
                    if let Some(jbg) = joint_bg {
                        pass.set_bind_group(3, jbg, &[]);
                    } else {
                        pass.set_bind_group(3, &gpu_model.identity_joints_bg, &[]);
                    }
                } else {
                    pass.set_pipeline(&shadow_pipes.mesh);
                    // mesh 深度パイプラインは group 0/1 のみ（gap なし）。
                }
                pass.set_bind_group(0, cam_bg, &[]);
                cur_skinned = Some(draw.is_skinned);
            }

            // group 1: モデル行列（メインパスと同一 bind group を流用）。
            pass.set_bind_group(1, model_bg, &[]);

            pass.set_vertex_buffer(0, prim.vertex_buffer.slice(..));
            if draw.is_skinned {
                pass.set_vertex_buffer(1, prim.skin_vertex_buffer.as_ref().unwrap().slice(..));
            }

            let (idx_buf, idx_count) = prim.get_lod_index_buffer(lod);
            pass.set_index_buffer(idx_buf.slice(..), wgpu::IndexFormat::Uint32);
            pass.draw_indexed(0..idx_count, 0, 0..visible);
        }
    }
}

// ============================================================
//  CSM / スポット 行列計算
// ============================================================

/// practical split でカスケード遠端のビュー空間距離を求める。
/// 返り値[i] はカスケード i の遠端距離（i の範囲は [前カスケード遠端, splits[i]]）。
fn cascade_split_distances(near: f32, far: f32) -> [f32; CSM_CASCADE_COUNT] {
    let mut splits = [0.0f32; CSM_CASCADE_COUNT];
    let n = CSM_CASCADE_COUNT as f32;
    let ratio = (far / near.max(1e-4)).max(1.0);
    for i in 0..CSM_CASCADE_COUNT {
        let p   = (i as f32 + 1.0) / n;
        let log = near * ratio.powf(p);              // 対数分割（近距離に解像度集中）
        let uni = near + (far - near) * p;           // 均等分割
        splits[i] = CSM_SPLIT_LAMBDA * log + (1.0 - CSM_SPLIT_LAMBDA) * uni;
    }
    splits
}

/// 全カスケードの light view-proj とビュー空間分割距離を計算する。
///
/// 各スライスのカメラ視錐台コーナーをワールド空間で包むタイトな正射投影を作り、
/// テクセルスナップ（カメラ移動時のシャドウちらつき防止）を適用する。
fn compute_cascade_matrices(
    cam_view: &Mat4x4<f32>,
    near:     f32,
    far:      f32,
    fov_y:    f32,
    aspect:   f32,
    light_dir: [f32; 3],
) -> ([Mat4x4<f32>; CSM_CASCADE_COUNT], [f32; CSM_CASCADE_COUNT]) {
    let splits   = cascade_split_distances(near, far);
    let inv_view = cam_view.inverse().unwrap_or_else(Mat4x4::identity);
    let dir      = normalize(light_dir);
    // ライト方向がほぼ真上/真下のときは up を +Z にして look_at の縮退を避ける。
    let up = if dir[1].abs() > 0.99 { [0.0, 0.0, 1.0] } else { [0.0, 1.0, 0.0] };

    let tan_half_v = (fov_y * 0.5).tan();
    let mut mats = [Mat4x4::identity(); CSM_CASCADE_COUNT];

    for i in 0..CSM_CASCADE_COUNT {
        let slice_near = if i == 0 { near } else { splits[i - 1] };
        let slice_far  = splits[i];

        // ── スライスのビュー空間 8 コーナー → ワールド ─────────
        let mut corners = [[0.0f32; 3]; 8];
        let mut c = 0;
        for &z in &[slice_near, slice_far] {
            let hh = z * tan_half_v;
            let hw = hh * aspect;
            for &sy in &[-1.0f32, 1.0] {
                for &sx in &[-1.0f32, 1.0] {
                    let v = inv_view * Vector4::new(sx * hw, sy * hh, z, 1.0);
                    corners[c] = [v.x, v.y, v.z];
                    c += 1;
                }
            }
        }

        // ── バウンディング球（中心＝重心, 半径＝最遠コーナー距離）───
        let mut center = [0.0f32; 3];
        for cn in &corners { center[0] += cn[0]; center[1] += cn[1]; center[2] += cn[2]; }
        center = [center[0] / 8.0, center[1] / 8.0, center[2] / 8.0];
        let mut radius = 0.0f32;
        for cn in &corners {
            let d = [cn[0] - center[0], cn[1] - center[1], cn[2] - center[2]];
            radius = radius.max((d[0] * d[0] + d[1] * d[1] + d[2] * d[2]).sqrt());
        }
        // 半径を量子化してカメラ回転時の境界ちらつきを抑える。
        radius = (radius * RADIUS_QUANTIZE).ceil() / RADIUS_QUANTIZE;
        radius = radius.max(1e-3);

        // ── ライトビュー（重心をライト方向手前から見る）─────────
        let back = radius * SHADOW_CASTER_PULLBACK;
        let eye  = Vector3::new(
            center[0] - dir[0] * back,
            center[1] - dir[1] * back,
            center[2] - dir[2] * back,
        );
        let ctr  = Vector3::new(center[0], center[1], center[2]);
        let upv  = Vector3::new(up[0], up[1], up[2]);
        let light_view = Mat4x4::look_at_lh(eye, ctr, upv);

        // ── タイト正射（[-r, r]）＋ 深度レンジ ───────────────────
        // 重心のライト空間 z ≒ back。球は [back-r, back+r]、手前キャスター確保のため near=0。
        let far_z = back + radius * 2.0;
        let mut ortho = Mat4x4::orthographic_lh(-radius, radius, -radius, radius, 0.0, far_z);

        // ── テクセルスナップ ──────────────────────────────────
        // 原点をシャドウマップのテクセル格子に合わせ、サブテクセル移動によるちらつきを除去。
        let vp0    = ortho * light_view;
        let origin = vp0 * Vector4::new(0.0, 0.0, 0.0, 1.0);
        let tex    = SHADOW_MAP_SIZE as f32 * 0.5;
        let sx     = origin.x * tex;
        let sy     = origin.y * tex;
        let dx     = (sx.round() - sx) / tex;
        let dy     = (sy.round() - sy) / tex;
        // 正射の NDC 平行移動成分（第 4 列, 行優先で data[row][3]）へオフセットを加える。
        ortho.data[0][3] += dx;
        ortho.data[1][3] += dy;

        mats[i] = ortho * light_view;
    }

    (mats, splits)
}

/// スポットライトの light view-proj を計算する（円錐を包む透視投影）。
fn compute_spot_matrix(s: &GpuLight) -> Mat4x4<f32> {
    // 外側コーン半角（outer_cos = cos(half_angle)）から全画角を求める。
    let half_angle = s.outer_cos.clamp(-1.0, 1.0).acos();
    // 全画角。極端値でも perspective_lh が破綻しない範囲にクランプ。
    let fov = (half_angle * 2.0).clamp(0.1, std::f32::consts::PI * 0.95);
    let dir = normalize(s.direction);
    let up  = if dir[1].abs() > 0.99 { [0.0, 0.0, 1.0] } else { [0.0, 1.0, 0.0] };

    let eye = Vector3::new(s.position[0], s.position[1], s.position[2]);
    let ctr = Vector3::new(s.position[0] + dir[0], s.position[1] + dir[1], s.position[2] + dir[2]);
    let upv = Vector3::new(up[0], up[1], up[2]);
    let view = Mat4x4::look_at_lh(eye, ctr, upv);
    let far  = s.range.max(SPOT_SHADOW_NEAR * 2.0);
    let proj = Mat4x4::perspective_lh(fov, 1.0, SPOT_SHADOW_NEAR, far);
    proj * view
}

// ============================================================
//  カスケード別キャスターカリング
// ============================================================

/// キャスターをカスケード別カリングにかけてよいか判定し、その判定用ワールド AABB を返す。
///
/// `None` を返した場合は「境界が信用できない」＝カリングせず必ず描く（安全側）。
/// スキンメッシュを除外するのは、バッチが持つワールド AABB が **バインドポーズの
/// モデル AABB をインスタンス行列で変換したもの**であり、アニメーションで手足が
/// AABB の外へ出るとその影を誤って捨ててしまうため。
fn caster_cull_bounds(batch: &InstancedModelBatch) -> Option<([f32; 3], [f32; 3])> {
    if batch.skin.is_some() { return None; }
    batch.world_bounds()
}

/// ワールド AABB が view-proj のクリップボリュームの外側に**完全に**あるかを判定する純関数。
///
/// AABB の 8 頂点をクリップ空間へ写し、6 つのクリップ面
/// （`x ≥ -w`, `x ≤ w`, `y ≥ -w`, `y ≤ w`, `z ≥ 0`, `z ≤ w` ― wgpu の深度レンジ [0,1]）
/// のいずれか 1 つについて 8 頂点すべてが外側なら `true`（＝描いても 1 ピクセルも残らない）。
///
/// 保守的な判定であり「外側と判定したなら本当に描画結果に寄与しない」ことだけを保証する
/// （交差しているのに `false` を返す取りこぼしはあるが、その場合は従来どおり描くだけ）。
/// したがって深度バッファの内容は **カリング有無で 1 ビットも変わらない**。
///
/// 【前提】カスケードの投影は正射（w ≡ 1）であること。透視投影では視点をまたぐ AABB で
/// w が負になり判定が破綻しうるため、この関数はスポット影には使わない。
fn aabb_outside_clip_volume(vp: &Mat4x4<f32>, mn: [f32; 3], mx: [f32; 3]) -> bool {
    /// クリップ面の数（左右・上下・近遠）。
    const CLIP_PLANE_COUNT: usize = 6;
    // 各頂点について 6 面の「内側なら true」を集計する。
    let mut inside_any = [false; CLIP_PLANE_COUNT];
    for i in 0..8 {
        let p = Vector4::new(
            if i & 1 == 0 { mn[0] } else { mx[0] },
            if i & 2 == 0 { mn[1] } else { mx[1] },
            if i & 4 == 0 { mn[2] } else { mx[2] },
            1.0,
        );
        let c = *vp * p;
        let tests = [
            c.x >= -c.w,   // 左
            c.x <=  c.w,   // 右
            c.y >= -c.w,   // 下
            c.y <=  c.w,   // 上
            c.z >=  0.0,   // 近（wgpu の深度レンジは [0,1]）
            c.z <=  c.w,   // 遠
        ];
        for (k, t) in tests.iter().enumerate() {
            inside_any[k] |= *t;
        }
    }
    // どれか 1 面で「8 頂点すべて外側」なら完全に外。
    inside_any.iter().any(|&any_inside| !any_inside)
}

/// 3D ベクトルを正規化する（長さ 0 は +Z フォールバック）。
fn normalize(v: [f32; 3]) -> [f32; 3] {
    let len = (v[0] * v[0] + v[1] * v[1] + v[2] * v[2]).sqrt();
    if len < 1e-6 { [0.0, 0.0, 1.0] } else { [v[0] / len, v[1] / len, v[2] / len] }
}

// ============================================================
//  テスト
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;

    /// テスト用の正射 view-proj（ライト方向 = -Y、`center` 中心・半径 `radius`）。
    /// `compute_cascade_matrices` と同じ組み立て（look_at + orthographic_lh）を再現する。
    fn ortho_vp(center: [f32; 3], radius: f32) -> Mat4x4<f32> {
        let dir  = [0.0, -1.0, 0.0];
        let back = radius * SHADOW_CASTER_PULLBACK;
        let eye  = Vector3::new(
            center[0] - dir[0] * back,
            center[1] - dir[1] * back,
            center[2] - dir[2] * back,
        );
        let ctr  = Vector3::new(center[0], center[1], center[2]);
        // ライトが真下向きなので up は +Z（look_at の縮退回避。本体と同じ規則）。
        let view  = Mat4x4::look_at_lh(eye, ctr, Vector3::new(0.0, 0.0, 1.0));
        let ortho = Mat4x4::orthographic_lh(-radius, radius, -radius, radius, 0.0, back + radius * 2.0);
        ortho * view
    }

    /// カスケードの中心にある AABB は「外側」と判定されない（＝描画対象に残る）。
    #[test]
    fn aabb_inside_cascade_is_not_culled() {
        let vp = ortho_vp([0.0, 0.0, 0.0], 10.0);
        assert!(!aabb_outside_clip_volume(&vp, [-1.0, -1.0, -1.0], [1.0, 1.0, 1.0]));
    }

    /// カスケードの横（ライト空間 XY 方向）へ大きく外れた AABB は棄却される。
    #[test]
    fn aabb_far_aside_is_culled() {
        let vp = ortho_vp([0.0, 0.0, 0.0], 10.0);
        // ワールド X 方向へ半径のはるか外側。
        assert!(aabb_outside_clip_volume(&vp, [100.0, -1.0, -1.0], [110.0, 1.0, 1.0]));
        // ワールド Z 方向（ライト空間の縦方向）も同様。
        assert!(aabb_outside_clip_volume(&vp, [-1.0, -1.0, 100.0], [1.0, 1.0, 110.0]));
    }

    /// カスケード境界をまたぐ AABB は棄却しない（保守的判定＝見た目を変えない）。
    #[test]
    fn aabb_straddling_border_is_not_culled() {
        let vp = ortho_vp([0.0, 0.0, 0.0], 10.0);
        assert!(!aabb_outside_clip_volume(&vp, [5.0, -1.0, -1.0], [50.0, 1.0, 1.0]));
    }

    /// ライト方向の手前（eye よりさらに上）にある AABB は近クリップ外として棄却される。
    /// GPU も同じく near クリップで捨てるため、描画結果は変わらない。
    #[test]
    fn aabb_before_near_plane_is_culled() {
        let radius = 10.0f32;
        let vp = ortho_vp([0.0, 0.0, 0.0], radius);
        let eye_y = radius * SHADOW_CASTER_PULLBACK;
        assert!(aabb_outside_clip_volume(
            &vp,
            [-1.0, eye_y + 1.0, -1.0],
            [ 1.0, eye_y + 5.0,  1.0],
        ));
    }

    /// カスケード署名は行列・キャスター列のどちらが変わっても不一致になる
    /// （＝静的スキップが誤発火しない）。
    #[test]
    fn cascade_sig_detects_matrix_and_caster_changes() {
        let base = CascadeDrawSig { vp: Mat4x4::<f32>::identity().data, casters: vec![1, 2] };
        assert_eq!(base, base.clone(), "同一入力なら一致（＝スキップできる）");

        let mut moved = base.clone();
        moved.vp[0][3] += 1.0e-6;                   // 微小な差でも検出する（ビット一致比較）
        assert_ne!(base, moved, "カスケード行列の変化を検出");

        let mut updated = base.clone();
        updated.casters[1] = 3;                     // キャスターが update されて世代が変わった
        assert_ne!(base, updated, "キャスター内容の変化を検出");

        let mut culled = base.clone();
        culled.casters.pop();                       // カリング結果が変わった
        assert_ne!(base, culled, "描画キャスター数の変化を検出");
    }
}
