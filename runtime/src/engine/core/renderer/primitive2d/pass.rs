// ============================================================
//  primitive2d/pass.rs — 2D プリミティブの描画パス（wgpu）
//
//  【役割】
//  `PrimitiveCommand` 群を三角形へ分割し（tessellate.rs）、
//  「キャンバスローカル px → ワールド → クリップ → NDC」を **CPU で** 済ませて
//  1 本の頂点／インデックスバッファへ束ね、専用パイプラインで 1 ドローする。
//
//  【なぜ CPU で NDC まで変換するか】
//  `font/canvas_text.rs` とまったく同じ理由・同じ方式。
//  スプライトと同一の変換連鎖（アンカー／ピボット／親子スケール／3D キャンバス）を
//  そのまま通せるため、キャンバス配下の図形がスプライトと 1px もずれない。
//  1 フレームの図形数は上限つき（MAX_PRIMITIVES_PER_FRAME）なので CPU 変換は軽い。
//
//  【バッファ】
//  頂点／インデックスバッファは**永続化**し、容量不足のときだけ倍々で再確保する
//  （batch2d.rs の InstanceStream と同じ方針）。1 フレーム内で
//  `begin() → push() × N → upload()` の順に使い、push が返すレンジを draw へ渡す。
// ============================================================

use std::collections::HashMap;

use super::queue::PrimitiveCommand;
use super::tessellate::tessellate;
use crate::engine::components::CanvasDrawZone;
use crate::engine::core::renderer::ui_clip::UiClipId;
use crate::engine::ecs::Entity;

// ─── 定数 ────────────────────────────────────────────────────

/// アンチエイリアス帯の幅（描画空間 = キャンバス px）。
/// スクリーンスペースキャンバスでは 1 キャンバス px = 1 画面 px なので、
/// この値がそのまま「1 画面 px のフェザー」になる。
pub const FEATHER_UNITS: f32 = 1.0;

/// 見た目の拡張の「画面の画素のフェザー」（W2-8。`PrimitiveStyle::pixel_feather`）の帯の幅（画面の画素）。
pub const PIXEL_FEATHER_PX: f32 = 1.0;
/// 画面の画素のフェザーを描画空間へ直した幅の下限・上限（退化した行列で帯が消える・巨大になるのを防ぐ）。
const PIXEL_FEATHER_MIN_UNITS: f32 = 1e-3;
const PIXEL_FEATHER_MAX_UNITS: f32 = 1e3;
/// 1 画素あたりの NDC の長さ・面積の比をゼロとみなす大きさ。
const NDC_EPSILON: f32 = 1e-12;

/// 頂点バッファの初期容量（頂点数）。
const INITIAL_VERTEX_CAPACITY: u64 = 4096;
/// インデックスバッファの初期容量（インデックス数）。
const INITIAL_INDEX_CAPACITY: u64 = 8192;
/// 容量不足時の成長率（倍々）。
const CAPACITY_GROWTH_FACTOR: u64 = 2;

/// クリップ空間 w の下限。これ以下は視錐台の外（カメラ背後）とみなし三角形を捨てる。
const MIN_CLIP_W: f32 = 1e-6;

// ─── 頂点型 ──────────────────────────────────────────────────

/// プリミティブ頂点。primitive2d.wgsl の location 0..2 と 1:1 対応する。
#[repr(C)]
#[derive(Copy, Clone, bytemuck::Pod, bytemuck::Zeroable)]
pub struct PrimitiveVertex {
    /// NDC 座標（CPU 変換済み）。
    pub position: [f32; 3],
    /// RGBA カラー（ストレートアルファ）。
    pub color: [f32; 4],
    /// フェザー係数（1 = 内部 / 0 = 帯の外縁）。
    pub edge: f32,
}

/// position (vec3) のバイトオフセット。
const ATTR_OFFSET_POSITION: u64 = 0;
/// color (vec4) のバイトオフセット。
const ATTR_OFFSET_COLOR: u64 = 12;
/// edge (f32) のバイトオフセット。
const ATTR_OFFSET_EDGE: u64 = 28;

// ─── 描画レンジ ──────────────────────────────────────────────

/// `push` 1 回ぶんの描画レンジ（1 ドローコール）。
#[derive(Copy, Clone, Debug)]
pub struct PrimitiveRange {
    /// インデックスバッファ内の開始位置。
    pub first_index: u32,
    /// インデックス数（= 三角形数 × 3）。
    pub index_count: u32,
    /// 深度テスト（LessEqual）を行うか。
    /// - `true`  : 3D ワールドキャンバス上の図形（3D シーンに隠れる）
    /// - `false` : スクリーンスペース／2D キャンバスの UI（常に手前）
    pub depth_tested: bool,
}

impl PrimitiveRange {
    /// 描くものが無いか。
    pub fn is_empty(&self) -> bool {
        self.index_count == 0
    }
}

// ─── 座標空間 ────────────────────────────────────────────────

/// プリミティブを最終的にどのパス・どの規則で描くか。
#[derive(Copy, Clone, Debug, PartialEq, Eq)]
pub enum PrimitiveSpaceTarget {
    /// 2D キャンバス（スクリーンスペース合成）。描画ゾーンでさらに前後が分かれる。
    Canvas2d(CanvasDrawZone),
    /// 3D ワールドキャンバス配下（3D カメラでメインパスに描く・深度テストあり）。
    World3d,
}

/// プリミティブの座標空間 1 件。
///
/// `model` は `collect_sprite_items` がスプライト／テキストへ渡すのとまったく
/// 同じ「キャンバスローカル px → ワールド」の GPU 列優先行列。
#[derive(Copy, Clone, Debug)]
pub struct PrimitiveSpace {
    /// キャンバスローカル px → ワールドの GPU 列優先行列。
    pub model: [[f32; 4]; 4],
    /// 描画先の分類。
    pub target: PrimitiveSpaceTarget,
    /// 3D ワールドキャンバスの通し番号（`target = World3d` のときのみ意味を持つ）。
    ///
    /// ワールドキャンバスはキャンバスごとに独立したレイヤー空間を持つため、
    /// 描画順の統合（レイヤーマージ）はキャンバス単位で行う必要がある。
    /// そのため「どのキャンバス配下の図形か」をここで持ち回る。
    /// `Canvas2d` では常に 0（未使用）。
    pub world3d_group: u32,
    /// この空間へ描く図形の切り抜きの番号（空間の持ち主のノードと同じ。レイアウトの表の領域。W2-1a）。
    /// 3D ワールドキャンバス配下は切り抜かないので常に None。
    pub clip: Option<UiClipId>,
    /// 空間の持ち主のノードのレイヤーの底上げ（W2-7 の `CanvasLayoutItem.layer_bias` の和。W2-8）。
    /// この空間へ描く図形のレイヤーに足す（スプライト・テキストと同じ `biased_layer`）。3D ワールドキャンバス配下は 0。
    pub layer_bias: i32,
}

/// キャンバスアクター entity → 座標空間のマップ。
pub type PrimitiveSpaceMap = HashMap<Entity, PrimitiveSpace>;

/// `collect_sprite_items` が座標空間を書き込むための収集器。
///
/// 走査中のサブツリーが 3D ワールドキャンバス配下かどうかは呼び出し側しか
/// 知らないため、フラグを収集器側に持たせる（引数をこれ以上増やさないための束ね）。
#[derive(Default)]
pub struct PrimitiveSpaceCollector {
    /// 収集結果。
    pub map: PrimitiveSpaceMap,
    /// これから収集するサブツリーが 3D ワールドキャンバス配下か。
    /// `true` のとき `target` は常に `World3d` になる（ゾーン概念なし）。
    pub world3d: bool,
    /// これから収集する 3D ワールドキャンバスの通し番号
    /// （`world3d = true` のときのみ意味を持つ。キャンバスごとに呼び出し側が更新する）。
    pub world3d_group: u32,
}

impl PrimitiveSpaceCollector {
    /// 空の収集器。
    pub fn new() -> Self {
        Self::default()
    }

    /// 1 アクターぶんの座標空間を記録する。
    ///
    /// `zone` は 2D キャンバスの描画ゾーン（`world3d = true` のときは無視される）。
    /// `clip` はこの空間へ描く図形の切り抜きの番号（`world3d = true` のときは無視して None にする）。
    /// `layer_bias` は持ち主のノードのレイヤーの底上げ（W2-8。`world3d = true` のときは 0 にする）。
    pub fn insert(
        &mut self,
        entity: Entity,
        model: [[f32; 4]; 4],
        zone: CanvasDrawZone,
        clip: Option<UiClipId>,
        layer_bias: i32,
    ) {
        let target = if self.world3d {
            PrimitiveSpaceTarget::World3d
        } else {
            PrimitiveSpaceTarget::Canvas2d(zone)
        };
        // ワールドキャンバス配下のみキャンバス通し番号を記録する（2D は 0 固定）。
        let world3d_group = if self.world3d { self.world3d_group } else { 0 };
        self.map.insert(
            entity,
            PrimitiveSpace {
                model,
                target,
                world3d_group,
                // 3D ワールドキャンバス（透視）は scissor で正しく切れないので切り抜かない
                clip: if self.world3d { None } else { clip },
                // 3D ワールドキャンバスはキャンバスごとの並びなので底上げしない
                layer_bias: if self.world3d { 0 } else { layer_bias },
            },
        );
    }
}

/// 図形のレイヤーへ、座標空間の持ち主のノードのレイヤーの底上げを足す【純関数に近い: コマンドの layer だけを書き換える】（W2-8）。
///
/// スプライト・テキストは収集のときに `biased_layer(layer, layer_bias)` で並ぶので、同じ規則を図形にも当てる
/// （画面のスタックで積んだ画面の中の `SEED.Draw` が、底上げした画面の背景の下に隠れないように）。
/// スクリーンスペース（`space = None`）と、解決できない空間の図形は変えない。底上げ 0 なら従来と同じ。
pub fn apply_space_layer_bias(cmds: &mut [super::queue::PrimitiveCommand], spaces: &PrimitiveSpaceMap) {
    for c in cmds.iter_mut() {
        if let Some(bias) = c.space.and_then(|e| spaces.get(&e)).map(|s| s.layer_bias).filter(|b| *b != 0) {
            c.layer = crate::engine::core::canvas_layout::biased_layer(c.layer, bias);
        }
    }
}

// ─── レンダラ ────────────────────────────────────────────────

/// 2D プリミティブ描画器（パイプライン + 永続バッファ + CPU 蓄積バッファ）。
///
/// 生成時のカラー／深度フォーマットに紐づくため、描画先パスのアタッチメント構成
/// ごとに 1 インスタンス必要（現状はメインパスとキャンバスオーバーレイパスが
/// 同じ HDR + 深度なので 1 つで足りる）。
pub struct Primitive2dRenderer {
    /// UI 用パイプライン（深度テスト Always・書き込み無し）。
    /// スクリーンスペース／2D キャンバスの図形はテキストと同じく常に手前に出す。
    pipeline_overlay: wgpu::RenderPipeline,
    /// 3D ワールドキャンバス用パイプライン（深度テスト LessEqual・書き込み無し）。
    /// 3D キャンバススプライト（pipelines/sprite.toml）とまったく同じ深度規則。
    pipeline_depth: wgpu::RenderPipeline,
    /// 現フレームの頂点（CPU 側蓄積）。
    verts: Vec<PrimitiveVertex>,
    /// 現フレームのインデックス（CPU 側蓄積）。
    indices: Vec<u32>,
    /// 永続頂点バッファ。容量不足時のみ再確保する。
    vertex_buf: Option<wgpu::Buffer>,
    /// 永続インデックスバッファ。
    index_buf: Option<wgpu::Buffer>,
    /// 頂点バッファの容量（頂点数）。
    vertex_capacity: u64,
    /// インデックスバッファの容量（インデックス数）。
    index_capacity: u64,
}

impl Primitive2dRenderer {
    /// パイプラインを構築する。
    ///
    /// - `color_format`: 描画先カラーターゲット（メインパスは HDR）。
    /// - `depth_format`: 深度アタッチメントのフォーマット（テスト・書き込みはしない）。
    pub fn new(
        device: &wgpu::Device,
        color_format: wgpu::TextureFormat,
        depth_format: wgpu::TextureFormat,
    ) -> Self {
        let shader = device.create_shader_module(wgpu::ShaderModuleDescriptor {
            label: Some("Primitive2D Shader"),
            source: wgpu::ShaderSource::Wgsl(include_str!("../shaders/primitive2d.wgsl").into()),
        });

        // バインドグループを持たない（頂点は NDC 直値・テクスチャ無し）。
        let layout = device.create_pipeline_layout(&wgpu::PipelineLayoutDescriptor {
            label: Some("Primitive2D Pipeline Layout"),
            bind_group_layouts: &[],
            push_constant_ranges: &[],
        });

        let vertex_layout = wgpu::VertexBufferLayout {
            array_stride: std::mem::size_of::<PrimitiveVertex>() as u64,
            step_mode: wgpu::VertexStepMode::Vertex,
            attributes: &[
                wgpu::VertexAttribute {
                    format: wgpu::VertexFormat::Float32x3,
                    offset: ATTR_OFFSET_POSITION,
                    shader_location: 0,
                },
                wgpu::VertexAttribute {
                    format: wgpu::VertexFormat::Float32x4,
                    offset: ATTR_OFFSET_COLOR,
                    shader_location: 1,
                },
                wgpu::VertexAttribute {
                    format: wgpu::VertexFormat::Float32,
                    offset: ATTR_OFFSET_EDGE,
                    shader_location: 2,
                },
            ],
        };

        // 深度比較だけが異なる 2 本のパイプラインを作る
        // （UI = Always / 3D ワールドキャンバス = LessEqual）。
        // 共有のパイプラインキャッシュ（Renderer が作ったもの。未作成・非対応なら None＝従来どおりキャッシュ無し）。
        let shared_cache = crate::engine::core::renderer::pipeline_cache::shared::shared();
        let make_pipeline = |label: &str, depth_compare: wgpu::CompareFunction| {
            device.create_render_pipeline(&wgpu::RenderPipelineDescriptor {
                label: Some(label),
                layout: Some(&layout),
                vertex: wgpu::VertexState {
                    module: &shader,
                    entry_point: Some("vs_main"),
                    buffers: &[vertex_layout.clone()],
                    compilation_options: Default::default(),
                },
                fragment: Some(wgpu::FragmentState {
                    module: &shader,
                    entry_point: Some("fs_main"),
                    targets: &[Some(wgpu::ColorTargetState {
                        format: color_format,
                        blend: Some(wgpu::BlendState::ALPHA_BLENDING),
                        write_mask: wgpu::ColorWrites::ALL,
                    })],
                    compilation_options: Default::default(),
                }),
                primitive: wgpu::PrimitiveState {
                    topology: wgpu::PrimitiveTopology::TriangleList,
                    strip_index_format: None,
                    front_face: wgpu::FrontFace::Ccw,
                    // 巻き方向は図形と変換行列（Y 反転）で変わるため両面描画にする。
                    cull_mode: None,
                    ..Default::default()
                },
                // 深度書き込みはしない（半透明合成のため。スプライトと同じ）。
                depth_stencil: Some(wgpu::DepthStencilState {
                    format: depth_format,
                    depth_write_enabled: false,
                    depth_compare,
                    stencil: wgpu::StencilState::default(),
                    bias: wgpu::DepthBiasState::default(),
                }),
                multisample: wgpu::MultisampleState::default(),
                multiview: None,
                cache: shared_cache.as_ref(),
            })
        };
        let pipeline_overlay = make_pipeline(
            "Primitive2D Overlay Pipeline",
            wgpu::CompareFunction::Always,
        );
        let pipeline_depth = make_pipeline(
            "Primitive2D World Pipeline",
            wgpu::CompareFunction::LessEqual,
        );

        Self {
            pipeline_overlay,
            pipeline_depth,
            verts: Vec::new(),
            indices: Vec::new(),
            vertex_buf: None,
            index_buf: None,
            vertex_capacity: 0,
            index_capacity: 0,
        }
    }

    /// フレーム開始。CPU 蓄積バッファを空にする。
    pub fn begin(&mut self) {
        self.verts.clear();
        self.indices.clear();
    }

    /// コマンド列を 1 レンジぶん積む。
    ///
    /// - `cmds`      : 描画順（レイヤー昇順）に並べたコマンド列。
    /// - `spaces`    : キャンバスアクター entity → GPU 列優先モデル行列。
    ///                 コマンドの `space` がここに無い場合は描画をスキップする
    ///                 （非表示・別世界線・CanvasTransform 無しのアクター）。
    /// - `screen_model`: スクリーンスペース（`space = None`）用のモデル行列。
    /// - `view_proj` : カメラのビュー射影行列（**行優先** `vp[row][col]`）。
    /// - `depth_tested`: このレンジを深度テスト（LessEqual）付きで描くか。
    ///   3D ワールドキャンバス上の図形は true、UI は false。
    ///
    /// 戻り値は draw へ渡すレンジ。
    pub fn push(
        &mut self,
        cmds: &[PrimitiveCommand],
        spaces: &PrimitiveSpaceMap,
        screen_model: &[[f32; 4]; 4],
        view_proj: &[[f32; 4]; 4],
        depth_tested: bool,
    ) -> PrimitiveRange {
        let first_index = self.indices.len() as u32;
        for cmd in cmds {
            // 座標空間の解決（未解決 = そのキャンバスが描画対象でない → 捨てる）
            let model = match cmd.space {
                None => screen_model,
                Some(e) => match spaces.get(&e) {
                    Some(s) => &s.model,
                    None => continue,
                },
            };
            // フェザーの幅（W2-8）: 既定は描画空間の 1 単位（従来どおり）。見た目の拡張で画面の画素を選んだ図形は、
            // 画面の 1 画素ぶんの描画空間の長さ（dp のキャンバスでも縁がにじまない。3D ワールドキャンバスは従来どおり）
            let feather = if cmd.style.pixel_feather && !depth_tested {
                pixel_feather_units(model, screen_model, view_proj)
            } else {
                FEATHER_UNITS
            };
            let mesh = tessellate(cmd, feather);
            if mesh.is_empty() {
                continue;
            }
            // グラデーション（W2-8）の軸は SRT を掛けた描画空間の点（メッシュの頂点と同じ空間）
            let (grad_from, grad_to) = cmd
                .style
                .gradient
                .map_or(([0.0; 2], [0.0; 2]), |g| (cmd.srt.apply(g.from), cmd.srt.apply(g.to)));
            // 頂点を NDC へ変換して積む。投影に失敗した頂点は None を記録し、
            // その頂点を含む三角形は捨てる（カメラ背後の 3D キャンバスなど）。
            let base = self.verts.len() as u32;
            let mut mapped: Vec<Option<u32>> = Vec::with_capacity(mesh.verts.len());
            let mut next = base;
            for v in &mesh.verts {
                match project(v.pos[0], v.pos[1], model, view_proj) {
                    Some(ndc) => {
                        self.verts.push(PrimitiveVertex {
                            position: ndc,
                            // 単色なら従来の色。グラデーションなら頂点の位置の色（三角形の中は線形補間で正確）
                            color: cmd.style.color_at(cmd.color, grad_from, grad_to, v.pos),
                            edge: v.alpha,
                        });
                        mapped.push(Some(next));
                        next += 1;
                    }
                    None => mapped.push(None),
                }
            }
            for tri in mesh.idx.chunks_exact(3) {
                let (a, b, c) = (
                    mapped[tri[0] as usize],
                    mapped[tri[1] as usize],
                    mapped[tri[2] as usize],
                );
                if let (Some(a), Some(b), Some(c)) = (a, b, c) {
                    self.indices.extend_from_slice(&[a, b, c]);
                }
            }
        }
        PrimitiveRange {
            first_index,
            index_count: self.indices.len() as u32 - first_index,
            depth_tested,
        }
    }

    /// 蓄積した頂点／インデックスを GPU バッファへ書き込む（フレームに 1 回）。
    ///
    /// 容量が足りないときだけ倍々で再確保する。
    pub fn upload(&mut self, device: &wgpu::Device, queue: &wgpu::Queue) {
        if self.indices.is_empty() {
            return;
        }
        // ── 頂点バッファ ──
        let need_v = self.verts.len() as u64;
        if self.vertex_buf.is_none() || self.vertex_capacity < need_v {
            let mut cap = self.vertex_capacity.max(INITIAL_VERTEX_CAPACITY);
            while cap < need_v {
                cap *= CAPACITY_GROWTH_FACTOR;
            }
            self.vertex_capacity = cap;
            self.vertex_buf = Some(device.create_buffer(&wgpu::BufferDescriptor {
                label: Some("Primitive2D Vertex Buffer"),
                size: cap * std::mem::size_of::<PrimitiveVertex>() as u64,
                usage: wgpu::BufferUsages::VERTEX | wgpu::BufferUsages::COPY_DST,
                mapped_at_creation: false,
            }));
        }
        // ── インデックスバッファ ──
        let need_i = self.indices.len() as u64;
        if self.index_buf.is_none() || self.index_capacity < need_i {
            let mut cap = self.index_capacity.max(INITIAL_INDEX_CAPACITY);
            while cap < need_i {
                cap *= CAPACITY_GROWTH_FACTOR;
            }
            self.index_capacity = cap;
            self.index_buf = Some(device.create_buffer(&wgpu::BufferDescriptor {
                label: Some("Primitive2D Index Buffer"),
                size: cap * std::mem::size_of::<u32>() as u64,
                usage: wgpu::BufferUsages::INDEX | wgpu::BufferUsages::COPY_DST,
                mapped_at_creation: false,
            }));
        }
        if let (Some(vb), Some(ib)) = (&self.vertex_buf, &self.index_buf) {
            queue.write_buffer(vb, 0, bytemuck::cast_slice(&self.verts));
            queue.write_buffer(ib, 0, bytemuck::cast_slice(&self.indices));
        }
    }

    /// 1 レンジをレンダーパスへ描画する。
    ///
    /// `upload` 済みであること。空レンジ・未アップロード時は何もしない。
    pub fn draw<'pass>(&'pass self, range: &PrimitiveRange, pass: &mut wgpu::RenderPass<'pass>) {
        if range.is_empty() {
            return;
        }
        let (Some(vb), Some(ib)) = (&self.vertex_buf, &self.index_buf) else {
            return;
        };
        // 3D ワールドキャンバスの図形だけ深度テスト付きで描く
        pass.set_pipeline(if range.depth_tested {
            &self.pipeline_depth
        } else {
            &self.pipeline_overlay
        });
        pass.set_vertex_buffer(0, vb.slice(..));
        pass.set_index_buffer(ib.slice(..), wgpu::IndexFormat::Uint32);
        pass.draw_indexed(
            range.first_index..(range.first_index + range.index_count),
            0,
            0..1,
        );
    }

    /// 現フレームに積まれた三角形数（[PERF] 表示・デバッグ用）。
    pub fn triangle_count(&self) -> usize {
        self.indices.len() / 3
    }
}

// ─── 画面の画素のフェザー（W2-8）─────────────────────────────────

/// 画面の 1 画素が描画空間で何単位か【純関数】（`PrimitiveStyle::pixel_feather` の帯の幅）。
///
/// 描画空間の原点と単位ベクトル（x・y）と、スクリーンスペース（1 単位 = 1 画素）の原点と単位ベクトルを同じ射影で NDC へ写し、
/// 「描画空間の 1 単位が何画素か」（面積の比の平方根。縦横の倍率が違っても平均の大きさ）で `PIXEL_FEATHER_PX` を割る。
/// 射影できない・退化しているときは従来の `FEATHER_UNITS`。
pub fn pixel_feather_units(model: &[[f32; 4]; 4], screen_model: &[[f32; 4]; 4], view_proj: &[[f32; 4]; 4]) -> f32 {
    let local = (project(0.0, 0.0, model, view_proj), project(1.0, 0.0, model, view_proj), project(0.0, 1.0, model, view_proj));
    let screen = (
        project(0.0, 0.0, screen_model, view_proj),
        project(1.0, 0.0, screen_model, view_proj),
        project(0.0, 1.0, screen_model, view_proj),
    );
    let ((Some(o), Some(x), Some(y)), (Some(so), Some(sx), Some(sy))) = (local, screen) else {
        return FEATHER_UNITS;
    };
    // 1 画素あたりの NDC の長さ（スクリーンスペースは軸にそろっている）
    let (ndc_per_px_x, ndc_per_px_y) = (sx[0] - so[0], sy[1] - so[1]);
    if ndc_per_px_x.abs() <= NDC_EPSILON || ndc_per_px_y.abs() <= NDC_EPSILON {
        return FEATHER_UNITS;
    }
    // 描画空間の単位ベクトルを画素へ直し、その平行四辺形の面積（画素²/単位²）
    let ux = [(x[0] - o[0]) / ndc_per_px_x, (x[1] - o[1]) / ndc_per_px_y];
    let uy = [(y[0] - o[0]) / ndc_per_px_x, (y[1] - o[1]) / ndc_per_px_y];
    let area = (ux[0] * uy[1] - ux[1] * uy[0]).abs();
    if !(area.is_finite() && area > NDC_EPSILON) {
        return FEATHER_UNITS;
    }
    (PIXEL_FEATHER_PX / area.sqrt()).clamp(PIXEL_FEATHER_MIN_UNITS, PIXEL_FEATHER_MAX_UNITS)
}

// ─── 座標変換 ────────────────────────────────────────────────

/// キャンバスローカル座標 (x, y) を NDC へ射影する。
///
/// `font/canvas_text.rs` の `project` と同一の規約（そちらが正典）:
/// - `model`     : GPU 列優先（`model[col][row]`）のキャンバス → ワールド行列
/// - `view_proj` : 行優先（`vp[row][col]`）のカメラ行列
///
/// 戻り値 `None` = クリップ空間の w が 0 以下（カメラ背後・退化行列）。
fn project(
    x: f32,
    y: f32,
    model: &[[f32; 4]; 4],
    view_proj: &[[f32; 4]; 4],
) -> Option<[f32; 3]> {
    let mut world = [0.0f32; 4];
    for (row, w) in world.iter_mut().enumerate() {
        *w = model[0][row] * x + model[1][row] * y + model[3][row];
    }
    let mut clip = [0.0f32; 4];
    for (row, c) in clip.iter_mut().enumerate() {
        let r = &view_proj[row];
        *c = r[0] * world[0] + r[1] * world[1] + r[2] * world[2] + r[3] * world[3];
    }
    if clip[3] <= MIN_CLIP_W {
        return None;
    }
    let inv_w = 1.0 / clip[3];
    Some([clip[0] * inv_w, clip[1] * inv_w, clip[2] * inv_w])
}

// ============================================================
//  ユニットテスト（GPU 不要）
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;

    /// 単位行列 × 単位行列では入力座標がそのまま NDC になる。
    #[test]
    fn primitive_identity_projection_is_passthrough() {
        let ident = [
            [1.0, 0.0, 0.0, 0.0],
            [0.0, 1.0, 0.0, 0.0],
            [0.0, 0.0, 1.0, 0.0],
            [0.0, 0.0, 0.0, 1.0],
        ];
        let p = project(0.25, -0.5, &ident, &ident).expect("w=1 なので成功する");
        assert!((p[0] - 0.25).abs() < 1e-6);
        assert!((p[1] + 0.5).abs() < 1e-6);
    }

    /// シェーダーが naga で parse + validate できること（GPU 不要の静的検証）。
    #[test]
    fn primitive_shader_is_valid_wgsl() {
        let src = include_str!("../shaders/primitive2d.wgsl");
        let module = naga::front::wgsl::parse_str(src)
            .unwrap_or_else(|e| panic!("primitive2d.wgsl の parse に失敗: {e:?}"));
        let mut v = naga::valid::Validator::new(
            naga::valid::ValidationFlags::all(),
            naga::valid::Capabilities::empty(),
        );
        v.validate(&module)
            .unwrap_or_else(|e| panic!("primitive2d.wgsl の validate に失敗: {e:?}"));
    }

    /// 3D ワールドキャンバス配下で収集された空間はワールドスペース扱いになり、
    /// 2D キャンバス配下は描画ゾーン付きのキャンバス扱いになる。
    #[test]
    fn primitive_space_collector_resolves_world3d() {
        const M: [[f32; 4]; 4] = [
            [1.0, 0.0, 0.0, 0.0],
            [0.0, 1.0, 0.0, 0.0],
            [0.0, 0.0, 1.0, 0.0],
            [0.0, 0.0, 0.0, 1.0],
        ];
        let e2d = Entity::from_raw(1, 0);
        let e3d = Entity::from_raw(2, 0);

        let mut c = PrimitiveSpaceCollector::new();
        // 2D キャンバス配下（ゾーンがそのまま保持される）
        c.world3d = false;
        c.insert(e2d, M, CanvasDrawZone::Background, None, 0);
        // 3D ワールドキャンバス配下（ゾーン指定を渡してもワールドスペースになる）
        c.world3d = true;
        c.insert(e3d, M, CanvasDrawZone::Background, None, 7);

        assert_eq!(
            c.map[&e2d].target,
            PrimitiveSpaceTarget::Canvas2d(CanvasDrawZone::Background)
        );
        assert_eq!(c.map[&e3d].target, PrimitiveSpaceTarget::World3d);
        assert_eq!(c.map[&e3d].layer_bias, 0, "3D ワールドキャンバスは底上げしない");
    }

    /// レイヤーの底上げ（W2-8）: 空間の持ち主の底上げを図形のレイヤーに足す。0・スクリーンスペース・解決できない空間は変えない。
    #[test]
    fn space_layer_bias_is_added_to_primitive_layers() {
        use super::super::queue::{PrimitiveDrawMode, PrimitiveKind, Transform2d, PRIM_EXTRA_FLOATS};
        const M: [[f32; 4]; 4] = [
            [1.0, 0.0, 0.0, 0.0],
            [0.0, 1.0, 0.0, 0.0],
            [0.0, 0.0, 1.0, 0.0],
            [0.0, 0.0, 0.0, 1.0],
        ];
        let (biased, plain, missing) = (Entity::from_raw(1, 0), Entity::from_raw(2, 0), Entity::from_raw(3, 0));
        let mut c = PrimitiveSpaceCollector::new();
        c.insert(biased, M, CanvasDrawZone::Foreground, None, 10000);
        c.insert(plain, M, CanvasDrawZone::Foreground, None, 0);
        let cmd = |space: Option<Entity>, layer: i32| PrimitiveCommand {
            kind: PrimitiveKind::Polygon,
            space,
            color: [1.0; 4],
            mode: PrimitiveDrawMode::Fill,
            thickness: 1.0,
            layer,
            srt: Transform2d::IDENTITY,
            extras: [0.0; PRIM_EXTRA_FLOATS],
            points: Vec::new(),
            style: Default::default(),
        };
        let mut cmds = vec![cmd(Some(biased), 3), cmd(Some(plain), 3), cmd(None, 3), cmd(Some(missing), 3)];
        apply_space_layer_bias(&mut cmds, &c.map);
        let layers: Vec<i32> = cmds.iter().map(|c| c.layer).collect();
        assert_eq!(layers, vec![10003, 3, 3, 3]);
    }

    /// 画面の画素のフェザー（W2-8）: 描画空間の 1 単位が 2.625 画素（dp のキャンバスの Pixel 6a）なら帯は 1/2.625 単位、
    /// 1 画素なら 1 単位（従来と同じ）。縦横の倍率が違えば面積の平均。退化した行列は従来の 1 単位。
    #[test]
    fn pixel_feather_units_follow_the_space_scale() {
        let ident = [
            [1.0, 0.0, 0.0, 0.0],
            [0.0, 1.0, 0.0, 0.0],
            [0.0, 0.0, 1.0, 0.0],
            [0.0, 0.0, 0.0, 1.0],
        ];
        let scaled = |sx: f32, sy: f32| {
            let mut m = ident;
            m[0][0] = sx;
            m[1][1] = sy;
            m
        };
        assert!((pixel_feather_units(&ident, &ident, &ident) - 1.0).abs() < 1e-6);
        let f = pixel_feather_units(&scaled(2.625, 2.625), &ident, &ident);
        assert!((f - 1.0 / 2.625).abs() < 1e-6, "{f}");
        let f = pixel_feather_units(&scaled(4.0, 1.0), &ident, &ident);
        assert!((f - 0.5).abs() < 1e-6, "縦横 4 と 1 の面積の平均 2 画素: {f}");
        assert_eq!(pixel_feather_units(&scaled(0.0, 0.0), &ident, &ident), FEATHER_UNITS, "退化");
        // スクリーンスペースが 2 画素 = 1 NDC 単位（画面の倍率）でも、描画空間と同じ比なら 1 単位
        assert!((pixel_feather_units(&scaled(2.0, 2.0), &scaled(2.0, 2.0), &ident) - 1.0).abs() < 1e-6);
    }

    /// 頂点構造体のサイズと属性オフセットが一致する（wgsl のレイアウト契約）。
    #[test]
    fn primitive_vertex_layout_offsets() {
        assert_eq!(std::mem::size_of::<PrimitiveVertex>(), 32);
        assert_eq!(ATTR_OFFSET_POSITION, 0);
        assert_eq!(ATTR_OFFSET_COLOR, 12);
        assert_eq!(ATTR_OFFSET_EDGE, 28);
    }
}
