// ============================================================
//  id_pass.rs — ワールド座標 + Actor ID 統合バッファ
//
//  テクスチャフォーマット: Rgba32Float
//    R: ワールド座標 X
//    G: ワールド座標 Y
//    B: ワールド座標 Z
//    A: bitcast<f32>(instance_id)  ※ 0 = 背景
//
//  ピッキング (A チャンネル) とD&Dワールド座標 (RGB) を1枚で共有する。
//
//  ## 合成（3 層モデルの第 2 層）への昇格
//  本テクスチャは元来「エディタのピッキング専用・1 ピクセルだけ読み戻す」用途だったが、
//  合成段が per-actor の厳密なマスク（例: 特定アクタだけを縁取る／別処理する）を必要とする
//  ケースのために、**シェーダからサンプルできる中間バッファ**へ昇格させた。
//    - `TextureUsages::TEXTURE_BINDING` を追加（これが無いとバインドできない）。
//    - `bind_group_layout()` / `create_bind_group()` で `texture_2d<f32>` として差せる。
//    - 解像度はウィンドウ実効サイズ＝ライティング段（HDR オフスクリーン）と同一なので、
//      ピクセル座標をそのまま共有できる（textureLoad で 1:1 に引ける）。
//
//  ## 使い分けの指針（重要）
//  ID テクスチャは Rgba32Float・フル解像度＝**16 byte/px** と高価で、しかも全ジオメトリを
//  もう一度描き直す専用パスを要する。以下の順に検討すること:
//    1. 「敵だけ」「インタラクト可能だけ」のような**種別**で足りる → G-Buffer のセマンティック
//       タグ（RT3.a の 4bit。追加パスも追加帯域もゼロ）を使う。
//    2. 「この 1 体だけ」のような**個体の厳密な同定**が要る → ID テクスチャを使う。
//  そのため Play 中の毎フレーム描画は既定で無効とし、必要なタイトルだけが
//  `SEED_ID_PASS_IN_PLAY` で有効化する（`id_pass_enabled_in_play` 参照）。
// ============================================================

use super::{
    gpu_resources::{GpuModel, InstancedModelBatch, NUM_LODS},
    pipeline::{DrawPipelines, CanvasIdUniform},
};
use crate::engine::core::loader::model::CullFace;

// ピクセルあたりのバイト数 (Rgba32Float = 4 × 4bytes)
const BYTES_PER_PIXEL: usize = 16;
// wgpu の CopyTextureToBuffer で要求される bytes_per_row の最小アライメント
const ROW_ALIGNMENT: u64 = 256;

/// ID テクスチャのフォーマット。RGB にワールド座標（実数）、A に ID をビットパターンとして
/// 積むため 32bit float が必要（16bit half では ID の 32bit ビット列を保持できない）。
const ID_BUFFER_FORMAT: wgpu::TextureFormat = wgpu::TextureFormat::Rgba32Float;

/// Play 中も ID パスを毎フレーム描くかどうか（環境変数 `SEED_ID_PASS_IN_PLAY` で有効化）。
///
/// 【既定を OFF にした理由】ID パスは全不透明ジオメトリをフル解像度でもう一度描く専用パスで、
/// コストは深度プリパス 1 本ぶんに相当する。現時点で Play 中にこの結果を消費する機能は
/// 存在せず（合成＝第 3 層は未実装）、既定 ON にすると「誰も読まない 16 byte/px のパス」を
/// 全タイトルに常時課すことになる。実測は `SEED_PERF_LOG=1` の `[PERF] ... id=` フィールドで
/// 行えるので、必要になったタイトルだけがこの環境変数で有効化する方針とした。
/// Edit / Pause 中は従来どおり常に描画される（ピッキングが依存しているため）。
static ID_PASS_IN_PLAY: std::sync::LazyLock<bool> =
    std::sync::LazyLock::new(|| std::env::var_os("SEED_ID_PASS_IN_PLAY").is_some());

/// Play 中（非 Edit・非 Pause）に ID パスを描くべきか。
#[inline]
pub fn id_pass_enabled_in_play() -> bool { *ID_PASS_IN_PLAY }

// ============================================================
//  WorldPosIdBuffer — Rgba32Float テクスチャ + CPU リードバックバッファ
// ============================================================

/// ワールド座標 (RGB) と インスタンスID (A) を格納する統合バッファ。
///
/// ID パスのピッキングと、ドラッグ&ドロップ時のワールド座標取得を
/// 1 テクスチャで兼ねる。
pub struct IdBuffer {
    pub texture:      wgpu::Texture,
    pub view:         wgpu::TextureView,
    pub readback_buf: wgpu::Buffer,
    pub width:        u32,
    pub height:       u32,
}

impl IdBuffer {
    pub fn new(device: &wgpu::Device, width: u32, height: u32) -> Self {
        let texture = device.create_texture(&wgpu::TextureDescriptor {
            label: Some("World Pos ID Buffer Texture"),
            size: wgpu::Extent3d { width, height, depth_or_array_layers: 1 },
            mip_level_count: 1,
            sample_count:    1,
            dimension:       wgpu::TextureDimension::D2,
            format:          ID_BUFFER_FORMAT,
            // TEXTURE_BINDING: 合成（第 3 層）がシェーダから per-actor マスクとして読むために必要。
            // これが無いと bind group へ差せず「合成入力」になり得ない（描いて読み戻すだけの
            // ピッキング専用テクスチャのままになる）。
            usage: wgpu::TextureUsages::RENDER_ATTACHMENT
                 | wgpu::TextureUsages::COPY_SRC
                 | wgpu::TextureUsages::TEXTURE_BINDING,
            view_formats: &[],
        });
        let view = texture.create_view(&wgpu::TextureViewDescriptor::default());

        // 1ピクセル = 16 bytes。bytes_per_row は 256 の倍数が必要なので 256 で確保。
        let readback_buf = device.create_buffer(&wgpu::BufferDescriptor {
            label:              Some("World Pos ID Readback Buffer"),
            size:               ROW_ALIGNMENT,
            usage:              wgpu::BufferUsages::COPY_DST | wgpu::BufferUsages::MAP_READ,
            mapped_at_creation: false,
        });

        Self { texture, view, readback_buf, width, height }
    }

    /// 合成パスが ID テクスチャをサンプルするための BindGroupLayout を作る。
    ///
    /// WGSL 側は `var t_id: texture_2d<f32>` として宣言し、`textureLoad(t_id, pix, 0)` で引く
    /// （ライティング段と同解像度なのでピクセル座標をそのまま共有できる）。
    /// フィルタリングは意味を持たない（ID は補間してはならない離散値）ため sampler は持たせない。
    pub fn bind_group_layout(device: &wgpu::Device) -> wgpu::BindGroupLayout {
        device.create_bind_group_layout(&wgpu::BindGroupLayoutDescriptor {
            label: Some("Id Texture BGL"),
            entries: &[wgpu::BindGroupLayoutEntry {
                binding:    0,
                visibility: wgpu::ShaderStages::FRAGMENT | wgpu::ShaderStages::COMPUTE,
                ty: wgpu::BindingType::Texture {
                    // Rgba32Float は filterable ではないため NonFiltering 相当の
                    // `sample_type: Float { filterable: false }` を指定する（textureLoad 専用）。
                    sample_type:    wgpu::TextureSampleType::Float { filterable: false },
                    view_dimension: wgpu::TextureViewDimension::D2,
                    multisampled:   false,
                },
                count: None,
            }],
        })
    }

    /// `bind_group_layout()` で作ったレイアウトに本テクスチャを差した BindGroup を作る。
    pub fn create_bind_group(
        &self,
        device: &wgpu::Device,
        layout: &wgpu::BindGroupLayout,
    ) -> wgpu::BindGroup {
        device.create_bind_group(&wgpu::BindGroupDescriptor {
            label:  Some("Id Texture BG"),
            layout,
            entries: &[wgpu::BindGroupEntry {
                binding:  0,
                resource: wgpu::BindingResource::TextureView(&self.view),
            }],
        })
    }

    /// GPU サブミット済みのコピーコマンド実行後に呼び出す。
    ///
    /// 戻り値は `(world_pos: Option<[f32; 3]>, instance_id: u32)`:
    ///   - `world_pos`: 背景ピクセル (id == 0) の場合は None
    ///   - `instance_id`: 0 = 背景、N+1 = インスタンス N
    pub fn read_pixel(&self, device: &wgpu::Device) -> (Option<[f32; 3]>, u32) {
        let buf_slice = self.readback_buf.slice(..);
        buf_slice.map_async(wgpu::MapMode::Read, |_| {});
        let _ = device.poll(wgpu::PollType::Wait);
        let data = buf_slice.get_mapped_range();

        // RGBA 各 4 bytes = 合計 16 bytes
        let x  = f32::from_ne_bytes(data[0..4].try_into().unwrap());
        let y  = f32::from_ne_bytes(data[4..8].try_into().unwrap());
        let z  = f32::from_ne_bytes(data[8..12].try_into().unwrap());
        // A チャンネルは bitcast<f32>(u32) で書き込まれているためビット変換で復元
        let id = u32::from_ne_bytes(data[12..16].try_into().unwrap());

        drop(data);
        self.readback_buf.unmap();

        let world_pos = if id != 0 { Some([x, y, z]) } else { None };
        (world_pos, id)
    }
}

// ============================================================
//  draw_id_pass — ID パスでモデルを描画
// ============================================================

// ============================================================
//  draw_canvas_id_items — キャンバスアクター ID パス描画
// ============================================================

/// キャンバスアクター ID パスにユニットクワッドを描画する。
///
/// 各アイテムはアクター raw ID と GPU 列優先モデル行列を持ち、
/// 呼び出し元で事前に GPU バッファ + バインドグループを生成してから渡す。
/// DFS 順に描画するので子が親を上書きし最前面の子が選択される。
/// `*_tex_bgs` はアイテムと 1:1 対応するテクスチャ bind_group（group 2）で、
/// スプライトなし時は white_fallback（alpha=1 全面選択）を渡す。
///
/// # 引数
/// - `ws_items` / `ws_tex_bgs`: WS アイテムとテクスチャ BG（camera_buf 使用）
/// - `ss_items` / `ss_tex_bgs`: SS アイテムとテクスチャ BG（ortho カメラ使用）
/// - `ws_camera_bg`: WS 用カメラ BG（perspective または 2D ortho）
/// - `ss_camera_bg`: SS 用 2D ortho カメラ BG（None なら SS アイテムは描画しない）
pub fn draw_canvas_id_items<'pass>(
    render_pass:  &mut wgpu::RenderPass<'pass>,
    pipelines:    &'pass DrawPipelines,
    ws_camera_bg: &'pass wgpu::BindGroup,
    ss_camera_bg: Option<&'pass wgpu::BindGroup>,
    ws_items:     &'pass [(wgpu::Buffer, wgpu::BindGroup)],
    ws_tex_bgs:   &[&'pass wgpu::BindGroup],
    ss_items:     &'pass [(wgpu::Buffer, wgpu::BindGroup)],
    ss_tex_bgs:   &[&'pass wgpu::BindGroup],
) {
    if ws_items.is_empty() && ss_items.is_empty() { return; }

    render_pass.set_pipeline(&pipelines.canvas_id.pipeline);
    // スプライトパイプラインのユニットクワッド頂点バッファを共有する
    render_pass.set_vertex_buffer(0, pipelines.sprite.unit_quad_vbuf.slice(..));

    // ワールドスペース（perspective / WS ortho カメラ）
    if !ws_items.is_empty() {
        render_pass.set_bind_group(0, ws_camera_bg, &[]);
        for ((_, bg), &tex_bg) in ws_items.iter().zip(ws_tex_bgs.iter()) {
            render_pass.set_bind_group(1, bg, &[]);
            render_pass.set_bind_group(2, tex_bg, &[]);
            render_pass.draw(0..6, 0..1);
        }
    }

    // スクリーンスペース（2D ortho カメラ）—— WS より後に描画するため常に前面に上書きされる
    if let Some(ss_bg) = ss_camera_bg {
        if !ss_items.is_empty() {
            render_pass.set_bind_group(0, ss_bg, &[]);
            for ((_, bg), &tex_bg) in ss_items.iter().zip(ss_tex_bgs.iter()) {
                render_pass.set_bind_group(1, bg, &[]);
                render_pass.set_bind_group(2, tex_bg, &[]);
                render_pass.draw(0..6, 0..1);
            }
        }
    }
}

/// コライダーピック面クワッドを ID パスへ描画する。
///
/// `draw_canvas_id_items` と同じユニットクワッド + CanvasIdUniform を使うが、
/// パイプラインを深度対応バリアント（`pick_depth_pipeline`）へ切り替えられる。
///
/// # 深度の扱い（`with_depth`）
/// - `true`（WS カメラのコライダー面）: depth_compare=LessEqual / depth_write=true。
///   メインパスのシーン深度（ID パスは LoadOp::Load で引き継ぐ）に対してテストし、
///   さらに自身も深度を書くため、コライダー面同士の重なりも「カメラに近い方優先」
///   で解決される（メッシュ ID パスと同等の可視性判定）。
/// - `false`（SS ortho カメラのコライダー面）: 通常の canvas_id パイプライン
///   （depth Always）。SS ortho の深度は 3D シーン深度と比較不能なため、
///   スプライトと同じ「描画順」の意味論に従う。
///
/// # 引数
/// - `items` / `tex_bgs`: アイテムとテクスチャ BG（1:1 対応。通常は白フォールバック）
/// - `camera_bg`: 描画に使うカメラ BG（WS または SS ortho）
pub fn draw_collider_pick_items<'pass>(
    render_pass: &mut wgpu::RenderPass<'pass>,
    pipelines:   &'pass DrawPipelines,
    camera_bg:   &'pass wgpu::BindGroup,
    items:       &'pass [(wgpu::Buffer, wgpu::BindGroup)],
    tex_bgs:     &[&'pass wgpu::BindGroup],
    with_depth:  bool,
) {
    if items.is_empty() { return; }

    // 深度対応バリアント（LessEqual + 書き込み）または通常（Always）を選択する
    let pipeline = if with_depth {
        &pipelines.canvas_id.pick_depth_pipeline
    } else {
        &pipelines.canvas_id.pipeline
    };
    render_pass.set_pipeline(pipeline);
    // スプライトパイプラインのユニットクワッド頂点バッファを共有する
    render_pass.set_vertex_buffer(0, pipelines.sprite.unit_quad_vbuf.slice(..));
    render_pass.set_bind_group(0, camera_bg, &[]);
    for ((_, bg), &tex_bg) in items.iter().zip(tex_bgs.iter()) {
        render_pass.set_bind_group(1, bg, &[]);
        render_pass.set_bind_group(2, tex_bg, &[]);
        render_pass.draw(0..6, 0..1);
    }
}

/// キャンバス ID バインドグループを生成するヘルパー。
///
/// render pass 外で呼び出し、pass ライフタイム中リソースを保持する。
pub fn prepare_canvas_id_bg(
    device:    &wgpu::Device,
    pipelines: &DrawPipelines,
    model:     [[f32; 4]; 4],
    actor_id:  u32,
) -> (wgpu::Buffer, wgpu::BindGroup) {
    use wgpu::util::DeviceExt;
    let uniform = CanvasIdUniform { model, actor_id, _pad: [0; 3] };
    let buf = device.create_buffer_init(&wgpu::util::BufferInitDescriptor {
        label:    Some("CanvasId Uniform Buf"),
        contents: bytemuck::bytes_of(&uniform),
        usage:    wgpu::BufferUsages::UNIFORM,
    });
    let bg = device.create_bind_group(&wgpu::BindGroupDescriptor {
        label:   Some("CanvasId BG"),
        layout:  &pipelines.canvas_id.canvas_id_bgl,
        entries: &[wgpu::BindGroupEntry {
            binding:  0,
            resource: buf.as_entire_binding(),
        }],
    });
    (buf, bg)
}

// ============================================================
//  draw_id_pass — ID パスでモデルを描画
// ============================================================

/// ワールド座標 + ID バッファパスでメッシュを描画する。
///
/// メインレンダーパスの後に呼び出す（深度テストで同等の可視性を再現する）。
pub fn draw_id_pass<'pass>(
    render_pass: &mut wgpu::RenderPass<'pass>,
    gpu_model:   &'pass GpuModel,
    batch:       &'pass InstancedModelBatch,
    camera_bg:   &'pass wgpu::BindGroup,
    pipelines:   &'pass DrawPipelines,
    id_base_bg:  &'pass wgpu::BindGroup,
) {
    if batch.n_prims == 0 { return; }

    for lod in 0..NUM_LODS {
        let visible = batch.lod_visible_counts[lod];
        if visible == 0 { continue; }

        let joint_bg = batch.joint_vs_bg(lod);
        let mut cur_skinned: Option<bool>     = None;
        // 現在バインド中のカリング面。node_prim_list は (is_skinned, cull_face, material_idx)
        // 順にソート済みのため、同じカリング面が連続する区間では set_pipeline を省略できる。
        // メインパス（model_drawer.rs）と同じ選択ロジックで、マテリアルのカリング面に追従する。
        let mut cur_cull:    Option<CullFace> = None;

        for draw in &batch.node_prim_list {
            let Some((_, model_bg)) = batch.lod_node_data[lod][draw.node_idx].as_ref()
                else { continue };

            let gpu_mesh = &gpu_model.meshes[draw.mesh_idx];
            let prim     = &gpu_mesh.primitives[draw.prim_idx];

            // このプリミティブのマテリアルの実効カリング面（オーバーライド適用後）。
            let cull = gpu_model.primitive_cull_face(draw.material_idx);

            // スキン有無・カリング面のどちらかが変わったときだけ set_pipeline する。
            if cur_skinned != Some(draw.is_skinned) || cur_cull != Some(cull) {
                if draw.is_skinned {
                    render_pass.set_pipeline(&pipelines.id_pass.skinned_pipelines[cull.index()]);
                    if let Some(jbg) = joint_bg {
                        render_pass.set_bind_group(3, jbg, &[]);
                    } else {
                        render_pass.set_bind_group(3, &gpu_model.identity_joints_bg, &[]);
                    }
                } else {
                    render_pass.set_pipeline(&pipelines.id_pass.mesh_pipelines[cull.index()]);
                    render_pass.set_bind_group(3, &gpu_model.identity_joints_bg, &[]);
                }
                render_pass.set_bind_group(0, camera_bg, &[]);
                render_pass.set_bind_group(2, &batch.lod_id_bgs[lod], &[]);
                render_pass.set_bind_group(4, id_base_bg, &[]);
                cur_skinned = Some(draw.is_skinned);
                cur_cull    = Some(cull);
            }

            render_pass.set_bind_group(1, model_bg, &[]);

            render_pass.set_vertex_buffer(0, prim.vertex_buffer.slice(..));
            if draw.is_skinned {
                render_pass.set_vertex_buffer(1, prim.skin_vertex_buffer.as_ref().unwrap().slice(..));
            }

            let (idx_buf, idx_count) = prim.get_lod_index_buffer(lod);
            render_pass.set_index_buffer(idx_buf.slice(..), wgpu::IndexFormat::Uint32);

            render_pass.draw_indexed(0..idx_count, 0, 0..visible);
        }
    }
}
