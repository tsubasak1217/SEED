// ============================================================
//  primitive3d/pass.rs — 3D プリミティブの描画パス（wgpu）
//
//  【役割】
//  `Primitive3dCommand` 群を build.rs でワールド空間の線分／三角形／点へ分解し、
//  1 本の頂点／インデックスバッファへ束ねて専用パイプラインで描画する。
//
//  【2D 版との決定的な違い: 頂点はワールド座標のまま GPU へ渡す】
//  2D 版は CPU で NDC まで変換していたが、3D の線は「距離に依らず一定の太さ」に
//  したい。太さの押し出しは**射影後の画面 px 空間**で行う必要があるため、
//  ビュー射影行列とビューポートサイズを uniform で渡し、頂点シェーダーで
//  押し出す（`primitive3d.wgsl`）。CPU では太さを解決しない。
//
//  【CPU がやる唯一の座標処理: 近平面クリップ】
//  clip 空間 w <= 0 の頂点は射影が破綻する（カメラ背後）。線分は端点を
//  w = 近平面ぎりぎりまで縮めて残し（ワールド空間の線形補間 = clip 空間の
//  線形補間なので単純な lerp で正しい）、三角形は 1 頂点でも背後なら捨てる。
//
//  【バッファ】
//  頂点／インデックスバッファは永続化し、容量不足のときだけ倍々で再確保する
//  （primitive2d / batch2d と同じ方針）。1 フレーム内で
//  `begin() → push() × N → upload()` の順に使い、push が返すレンジを draw へ渡す。
// ============================================================

use super::build::build;
use super::queue::Primitive3dCommand;

// ─── 定数 ────────────────────────────────────────────────────

/// 頂点バッファの初期容量（頂点数）。
const INITIAL_VERTEX_CAPACITY: u64 = 4096;
/// インデックスバッファの初期容量（インデックス数）。
const INITIAL_INDEX_CAPACITY: u64 = 8192;
/// 容量不足時の成長率（倍々）。
const CAPACITY_GROWTH_FACTOR: u64 = 2;

/// クリップ空間 w の下限。これ以下は視錐台の外（カメラ背後）とみなす。
/// 線分はこの w まで縮めて残し、三角形は捨てる。
const MIN_CLIP_W: f32 = 1e-4;

/// 線の太さの下限（px）。0 以下を渡されても消えないようにする。
const MIN_THICKNESS_PX: f32 = 0.5;
/// 線の太さの上限（px）。画面を埋め尽くす暴走を防ぐ安全弁。
const MAX_THICKNESS_PX: f32 = 256.0;

/// 太さ → 片側の押し出し量。
const HALF: f32 = 0.5;

/// リボン 1 本（線分 1 本）の頂点数。
const RIBBON_VERTS_PER_SEGMENT: usize = 4;
/// 画面向き正方形 1 個の頂点数。
const QUAD_VERTS: usize = 4;

// ─── 頂点型 ──────────────────────────────────────────────────

/// プリミティブ頂点。primitive3d.wgsl の location 0..4 と 1:1 対応する。
#[repr(C)]
#[derive(Copy, Clone, Debug, PartialEq, bytemuck::Pod, bytemuck::Zeroable)]
pub struct Primitive3dVertex {
    /// ワールド座標。
    pub position: [f32; 3],
    /// リボンのもう一方の端点（ワールド）。リボン以外では position と同じ。
    pub other: [f32; 3],
    /// RGBA カラー（ストレートアルファ）。
    pub color: [f32; 4],
    /// 画面 px の追加オフセット（画面向き正方形の角）。
    pub offset: [f32; 2],
    /// リボンの押し出し量（± 太さ/2 px）。0 なら押し出さない。
    pub side: f32,
}

/// position (vec3) のバイトオフセット。
const ATTR_OFFSET_POSITION: u64 = 0;
/// other (vec3) のバイトオフセット。
const ATTR_OFFSET_OTHER: u64 = 12;
/// color (vec4) のバイトオフセット。
const ATTR_OFFSET_COLOR: u64 = 24;
/// offset (vec2) のバイトオフセット。
const ATTR_OFFSET_OFFSET: u64 = 40;
/// side (f32) のバイトオフセット。
const ATTR_OFFSET_SIDE: u64 = 48;

// ─── カメラ uniform ──────────────────────────────────────────

/// 頂点シェーダーへ渡すカメラ情報（primitive3d.wgsl の `Prim3dCamera` と一致）。
///
/// `view_proj` は **WGSL の列優先**（`mat4x4` の列ベクトル並び）で格納する。
/// エンジン内の行優先 `[[f32;4];4]` を `update_camera` が転置して書き込む。
#[repr(C)]
#[derive(Copy, Clone, Debug, bytemuck::Pod, bytemuck::Zeroable)]
pub struct Primitive3dCameraUniform {
    /// ビュー射影行列（列優先）。
    pub view_proj: [[f32; 4]; 4],
    /// ビューポートの幅・高さ（px）。
    pub viewport: [f32; 2],
    /// 16 バイト境界そろえのパディング。
    pub _pad: [f32; 2],
}

// ─── 描画レンジ ──────────────────────────────────────────────

/// `push` 1 回ぶんの描画レンジ（1 ドローコール）。
#[derive(Copy, Clone, Debug)]
pub struct Primitive3dRange {
    /// インデックスバッファ内の開始位置。
    pub first_index: u32,
    /// インデックス数（= 三角形数 × 3）。
    pub index_count: u32,
    /// 深度テスト（LessEqual）を行うか。false なら常に手前へ描く。
    pub depth_tested: bool,
}

impl Primitive3dRange {
    /// 描くものが無いか。
    pub fn is_empty(&self) -> bool {
        self.index_count == 0
    }

    /// 空レンジ（描画器が無いフレーム用）。
    pub const EMPTY: Self = Self {
        first_index: 0,
        index_count: 0,
        depth_tested: false,
    };
}

// ─── レンダラ ────────────────────────────────────────────────

/// 3D プリミティブ描画器（パイプライン + カメラ uniform + 永続バッファ）。
///
/// 生成時のカラー／深度フォーマットに紐づくため、描画先パスのアタッチメント構成
/// ごとに 1 インスタンス必要（現状はメインパスのみ）。
pub struct Primitive3dRenderer {
    /// 深度テストあり（LessEqual・書き込み無し）のパイプライン。
    pipeline_depth: wgpu::RenderPipeline,
    /// 深度テストなし（Always）のパイプライン。常に手前へ描くデバッグ表示用。
    pipeline_overlay: wgpu::RenderPipeline,
    /// カメラ uniform バッファ。
    camera_buf: wgpu::Buffer,
    /// カメラ uniform のバインドグループ（group 0）。
    camera_bind_group: wgpu::BindGroup,
    /// 現フレームの頂点（CPU 側蓄積）。
    verts: Vec<Primitive3dVertex>,
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

impl Primitive3dRenderer {
    /// パイプラインとカメラ uniform を構築する。
    ///
    /// - `color_format`: 描画先カラーターゲット（メインパスは HDR）。
    /// - `depth_format`: 深度アタッチメントのフォーマット（テストのみ・書き込みなし）。
    pub fn new(
        device: &wgpu::Device,
        color_format: wgpu::TextureFormat,
        depth_format: wgpu::TextureFormat,
    ) -> Self {
        let shader = device.create_shader_module(wgpu::ShaderModuleDescriptor {
            label: Some("Primitive3D Shader"),
            source: wgpu::ShaderSource::Wgsl(include_str!("../shaders/primitive3d.wgsl").into()),
        });

        // group 0: カメラ（ビュー射影 + ビューポート px）
        let bgl = device.create_bind_group_layout(&wgpu::BindGroupLayoutDescriptor {
            label: Some("Primitive3D Camera BGL"),
            entries: &[wgpu::BindGroupLayoutEntry {
                binding: 0,
                visibility: wgpu::ShaderStages::VERTEX,
                ty: wgpu::BindingType::Buffer {
                    ty: wgpu::BufferBindingType::Uniform,
                    has_dynamic_offset: false,
                    min_binding_size: None,
                },
                count: None,
            }],
        });
        let camera_buf = device.create_buffer(&wgpu::BufferDescriptor {
            label: Some("Primitive3D Camera Buffer"),
            size: std::mem::size_of::<Primitive3dCameraUniform>() as u64,
            usage: wgpu::BufferUsages::UNIFORM | wgpu::BufferUsages::COPY_DST,
            mapped_at_creation: false,
        });
        let camera_bind_group = device.create_bind_group(&wgpu::BindGroupDescriptor {
            label: Some("Primitive3D Camera BG"),
            layout: &bgl,
            entries: &[wgpu::BindGroupEntry {
                binding: 0,
                resource: camera_buf.as_entire_binding(),
            }],
        });

        let layout = device.create_pipeline_layout(&wgpu::PipelineLayoutDescriptor {
            label: Some("Primitive3D Pipeline Layout"),
            bind_group_layouts: &[&bgl],
            push_constant_ranges: &[],
        });

        let vertex_layout = wgpu::VertexBufferLayout {
            array_stride: std::mem::size_of::<Primitive3dVertex>() as u64,
            step_mode: wgpu::VertexStepMode::Vertex,
            attributes: &[
                wgpu::VertexAttribute {
                    format: wgpu::VertexFormat::Float32x3,
                    offset: ATTR_OFFSET_POSITION,
                    shader_location: 0,
                },
                wgpu::VertexAttribute {
                    format: wgpu::VertexFormat::Float32x3,
                    offset: ATTR_OFFSET_OTHER,
                    shader_location: 1,
                },
                wgpu::VertexAttribute {
                    format: wgpu::VertexFormat::Float32x4,
                    offset: ATTR_OFFSET_COLOR,
                    shader_location: 2,
                },
                wgpu::VertexAttribute {
                    format: wgpu::VertexFormat::Float32x2,
                    offset: ATTR_OFFSET_OFFSET,
                    shader_location: 3,
                },
                wgpu::VertexAttribute {
                    format: wgpu::VertexFormat::Float32,
                    offset: ATTR_OFFSET_SIDE,
                    shader_location: 4,
                },
            ],
        };

        // 深度比較だけが異なる 2 本のパイプラインを作る
        // （depthTest = true → LessEqual / false → Always）。
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
                    // 塗りは両面（スクリプトが与える頂点順を裏表で区別しない）。
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
                cache: None,
            })
        };
        let pipeline_depth = make_pipeline(
            "Primitive3D Depth Pipeline",
            wgpu::CompareFunction::LessEqual,
        );
        let pipeline_overlay =
            make_pipeline("Primitive3D Overlay Pipeline", wgpu::CompareFunction::Always);

        Self {
            pipeline_depth,
            pipeline_overlay,
            camera_buf,
            camera_bind_group,
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
    /// - `cmds`        : 描画順に並べたコマンド列（同じ深度テスト設定のもの）。
    /// - `view_proj`   : カメラのビュー射影行列（**行優先** `vp[row][col]`）。
    ///   近平面クリップにのみ使う。実際の射影は GPU 側で同じ行列を使うため、
    ///   `update_camera` へ**必ず同じ行列**を渡すこと。
    /// - `depth_tested`: このレンジを深度テスト付きで描くか。
    ///
    /// 戻り値は draw へ渡すレンジ。
    pub fn push(
        &mut self,
        cmds: &[Primitive3dCommand],
        view_proj: &[[f32; 4]; 4],
        depth_tested: bool,
    ) -> Primitive3dRange {
        let first_index = self.indices.len() as u32;
        for cmd in cmds {
            append_command(&mut self.verts, &mut self.indices, cmd, view_proj);
        }
        Primitive3dRange {
            first_index,
            index_count: self.indices.len() as u32 - first_index,
            depth_tested,
        }
    }

    /// カメラ uniform を更新する（フレームに 1 回・パスを開く前に呼ぶ）。
    ///
    /// - `view_proj`: **行優先** `vp[row][col]`。WGSL 用に転置して書き込む。
    /// - `viewport` : set_viewport した矩形の幅・高さ（px）。線の太さの基準。
    pub fn update_camera(
        &self,
        queue: &wgpu::Queue,
        view_proj: &[[f32; 4]; 4],
        viewport: [f32; 2],
    ) {
        let uniform = Primitive3dCameraUniform {
            view_proj: transpose(view_proj),
            viewport,
            _pad: [0.0, 0.0],
        };
        queue.write_buffer(&self.camera_buf, 0, bytemuck::bytes_of(&uniform));
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
                label: Some("Primitive3D Vertex Buffer"),
                size: cap * std::mem::size_of::<Primitive3dVertex>() as u64,
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
                label: Some("Primitive3D Index Buffer"),
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
    /// `upload` / `update_camera` 済みであること。空レンジ時は何もしない。
    pub fn draw<'pass>(&'pass self, range: &Primitive3dRange, pass: &mut wgpu::RenderPass<'pass>) {
        if range.is_empty() {
            return;
        }
        let (Some(vb), Some(ib)) = (&self.vertex_buf, &self.index_buf) else {
            return;
        };
        pass.set_pipeline(if range.depth_tested {
            &self.pipeline_depth
        } else {
            &self.pipeline_overlay
        });
        pass.set_bind_group(0, &self.camera_bind_group, &[]);
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

// ─── 頂点構築（CPU 側） ──────────────────────────────────────

/// コマンド 1 件を頂点／インデックス列へ積む（`Primitive3dRenderer::push` の本体）。
///
/// GPU 資源に触らない純関数として切り出してあるため、ユニットテストが
/// 実装そのものを検証できる（テスト用に処理を書き写さない）。
///
/// - 線分は「画面一定幅のリボン」の 4 頂点 + 6 インデックスになる。
/// - 塗りの三角形はワールド座標のまま 3 頂点。
/// - 点は画面 px オフセットを持つ 4 頂点の正方形。
fn append_command(
    verts: &mut Vec<Primitive3dVertex>,
    indices: &mut Vec<u32>,
    cmd: &Primitive3dCommand,
    view_proj: &[[f32; 4]; 4],
) {
    let mesh = build(cmd);
    if mesh.is_empty() {
        return;
    }
    let half_thickness = cmd.thickness_px.clamp(MIN_THICKNESS_PX, MAX_THICKNESS_PX) * HALF;

    // ── 線分 → 画面一定幅のリボン（四角形）──
    for seg in &mesh.segments {
        let Some((a, b)) = clip_segment_to_near(seg.a, seg.b, view_proj) else {
            continue;
        };
        let base = verts.len() as u32;
        verts.reserve(RIBBON_VERTS_PER_SEGMENT);
        // a 側は「相手 = b」・b 側は「相手 = a」で画面上の方向が反転するため、
        // 同じ側へ寄せるには b 側の side 符号を逆にする。
        verts.extend_from_slice(&[
            Primitive3dVertex {
                position: a,
                other: b,
                color: cmd.color,
                offset: [0.0, 0.0],
                side: half_thickness,
            },
            Primitive3dVertex {
                position: a,
                other: b,
                color: cmd.color,
                offset: [0.0, 0.0],
                side: -half_thickness,
            },
            Primitive3dVertex {
                position: b,
                other: a,
                color: cmd.color,
                offset: [0.0, 0.0],
                side: -half_thickness,
            },
            Primitive3dVertex {
                position: b,
                other: a,
                color: cmd.color,
                offset: [0.0, 0.0],
                side: half_thickness,
            },
        ]);
        indices.extend_from_slice(&[base, base + 1, base + 2, base + 2, base + 1, base + 3]);
    }

    // ── 塗りの三角形（ワールド座標のまま）──
    for tri in &mesh.tris {
        // 1 頂点でもカメラ背後なら捨てる（三角形は近平面で分割しない）
        if tri.iter().any(|p| clip_w(*p, view_proj) <= MIN_CLIP_W) {
            continue;
        }
        let base = verts.len() as u32;
        for p in tri {
            verts.push(Primitive3dVertex {
                position: *p,
                other: *p,
                color: cmd.color,
                offset: [0.0, 0.0],
                side: 0.0,
            });
        }
        indices.extend_from_slice(&[base, base + 1, base + 2]);
    }

    // ── 点 → 画面を向く正方形 ──
    for pt in &mesh.points {
        if clip_w(pt.pos, view_proj) <= MIN_CLIP_W {
            continue;
        }
        let h = pt.size_px * HALF;
        let base = verts.len() as u32;
        verts.reserve(QUAD_VERTS);
        for c in [[-h, -h], [h, -h], [h, h], [-h, h]] {
            verts.push(Primitive3dVertex {
                position: pt.pos,
                other: pt.pos,
                color: cmd.color,
                offset: c,
                side: 0.0,
            });
        }
        indices.extend_from_slice(&[base, base + 1, base + 2, base, base + 2, base + 3]);
    }
}

// ─── 座標処理（CPU 側） ──────────────────────────────────────

/// 行優先のビュー射影行列を WGSL の列優先へ転置する。
fn transpose(m: &[[f32; 4]; 4]) -> [[f32; 4]; 4] {
    let mut out = [[0.0f32; 4]; 4];
    for (r, row) in m.iter().enumerate() {
        for (c, v) in row.iter().enumerate() {
            out[c][r] = *v;
        }
    }
    out
}

/// ワールド座標のクリップ空間 w 成分（行優先 view_proj の第 4 行との内積）。
fn clip_w(p: [f32; 3], vp: &[[f32; 4]; 4]) -> f32 {
    let r = &vp[3];
    r[0] * p[0] + r[1] * p[1] + r[2] * p[2] + r[3]
}

/// 線分を近平面（w = MIN_CLIP_W）でクリップする。
///
/// ワールド空間の線形補間は clip 空間でも線形なので、`w` が閾値に達する
/// パラメータ `t` を求めて端点を寄せるだけで正しい交点になる。
/// 両端ともカメラ背後なら None（線分ごと捨てる）。
fn clip_segment_to_near(
    a: [f32; 3],
    b: [f32; 3],
    vp: &[[f32; 4]; 4],
) -> Option<([f32; 3], [f32; 3])> {
    let (wa, wb) = (clip_w(a, vp), clip_w(b, vp));
    let a_ok = wa > MIN_CLIP_W;
    let b_ok = wb > MIN_CLIP_W;
    match (a_ok, b_ok) {
        (true, true) => Some((a, b)),
        (false, false) => None,
        // 片側だけ背後 → 交点まで縮める
        (true, false) => {
            let t = (MIN_CLIP_W - wa) / (wb - wa);
            Some((a, lerp(a, b, t)))
        }
        (false, true) => {
            let t = (MIN_CLIP_W - wb) / (wa - wb);
            Some((lerp(b, a, t), b))
        }
    }
}

/// 線形補間。
fn lerp(a: [f32; 3], b: [f32; 3], t: f32) -> [f32; 3] {
    [
        a[0] + (b[0] - a[0]) * t,
        a[1] + (b[1] - a[1]) * t,
        a[2] + (b[2] - a[2]) * t,
    ]
}

// ============================================================
//  ユニットテスト（GPU 不要）
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;
    use crate::engine::core::renderer::primitive3d::queue::{
        Primitive3dDrawMode, Primitive3dKind, PRIM3D_EXTRA_FLOATS,
    };

    /// 許容誤差。
    const TEST_EPS: f32 = 1e-4;

    /// 単位行列（w = 1 になるので全点が視錐台内扱いになる）。
    const IDENTITY: [[f32; 4]; 4] = [
        [1.0, 0.0, 0.0, 0.0],
        [0.0, 1.0, 0.0, 0.0],
        [0.0, 0.0, 1.0, 0.0],
        [0.0, 0.0, 0.0, 1.0],
    ];

    /// 「w = z」になる射影行列（z > 0 が前方）。近平面クリップの検証用。
    const PERSPECTIVE_W_IS_Z: [[f32; 4]; 4] = [
        [1.0, 0.0, 0.0, 0.0],
        [0.0, 1.0, 0.0, 0.0],
        [0.0, 0.0, 1.0, 0.0],
        [0.0, 0.0, 1.0, 0.0],
    ];

    /// テスト用コマンド。
    fn cmd(
        kind: Primitive3dKind,
        mode: Primitive3dDrawMode,
        points: Vec<[f32; 3]>,
        extras: [f32; PRIM3D_EXTRA_FLOATS],
        thickness_px: f32,
    ) -> Primitive3dCommand {
        Primitive3dCommand {
            kind,
            color: [1.0, 0.5, 0.25, 1.0],
            mode,
            thickness_px,
            depth_test: true,
            extras,
            points,
        }
    }

    /// 実装（`append_command`）をそのまま呼んで頂点列を得る。
    ///
    /// `Primitive3dRenderer::push` はこの関数へ委譲しているため、
    /// GPU 資源なしで push の契約を検証できる。
    fn push_to_vecs(
        cmds: &[Primitive3dCommand],
        view_proj: &[[f32; 4]; 4],
    ) -> (Vec<Primitive3dVertex>, Vec<u32>) {
        let mut verts = Vec::new();
        let mut indices = Vec::new();
        for cmd in cmds {
            append_command(&mut verts, &mut indices, cmd, view_proj);
        }
        (verts, indices)
    }

    /// 頂点構造体のサイズと属性オフセットが wgsl のレイアウト契約と一致する。
    #[test]
    fn primitive3d_vertex_layout_offsets() {
        assert_eq!(std::mem::size_of::<Primitive3dVertex>(), 52);
        assert_eq!(ATTR_OFFSET_POSITION, 0);
        assert_eq!(ATTR_OFFSET_OTHER, 12);
        assert_eq!(ATTR_OFFSET_COLOR, 24);
        assert_eq!(ATTR_OFFSET_OFFSET, 40);
        assert_eq!(ATTR_OFFSET_SIDE, 48);
    }

    /// カメラ uniform のサイズは 16 バイト境界に揃う（WGSL の構造体と一致）。
    #[test]
    fn primitive3d_camera_uniform_layout() {
        assert_eq!(std::mem::size_of::<Primitive3dCameraUniform>(), 80);
        assert_eq!(std::mem::size_of::<Primitive3dCameraUniform>() % 16, 0);
    }

    /// 行優先 → 列優先の転置が正しい（WGSL の mat4x4 は列優先）。
    #[test]
    fn primitive3d_view_proj_is_transposed_for_wgsl() {
        let m = [
            [1.0, 2.0, 3.0, 4.0],
            [5.0, 6.0, 7.0, 8.0],
            [9.0, 10.0, 11.0, 12.0],
            [13.0, 14.0, 15.0, 16.0],
        ];
        let t = transpose(&m);
        for r in 0..4 {
            for c in 0..4 {
                assert_eq!(t[c][r], m[r][c]);
            }
        }
    }

    /// 線 1 本は 4 頂点 6 インデックスのリボンになり、
    /// 両端の side 符号が「同じ側へ寄る」ように反転している。
    #[test]
    fn primitive3d_line_builds_ribbon_quad() {
        const A: [f32; 3] = [0.0, 0.0, 0.0];
        const B: [f32; 3] = [10.0, 0.0, 0.0];
        const THICKNESS: f32 = 4.0;
        let c = cmd(
            Primitive3dKind::Polyline,
            Primitive3dDrawMode::Outline,
            vec![A, B],
            [0.0; PRIM3D_EXTRA_FLOATS],
            THICKNESS,
        );
        let (verts, indices) = push_to_vecs(std::slice::from_ref(&c), &IDENTITY);
        assert_eq!(verts.len(), RIBBON_VERTS_PER_SEGMENT);
        assert_eq!(indices.len(), 6);
        let half = THICKNESS * HALF;
        // a 側 2 頂点（相手 = b）
        assert_eq!(verts[0].position, A);
        assert_eq!(verts[0].other, B);
        assert!((verts[0].side - half).abs() < TEST_EPS);
        assert!((verts[1].side + half).abs() < TEST_EPS);
        // b 側 2 頂点（相手 = a・符号は反転）
        assert_eq!(verts[2].position, B);
        assert_eq!(verts[2].other, A);
        assert!((verts[2].side + half).abs() < TEST_EPS);
        assert!((verts[3].side - half).abs() < TEST_EPS);
        // 押し出しは side のみ（画面オフセットは使わない）
        for v in &verts {
            assert_eq!(v.offset, [0.0, 0.0]);
        }
    }

    /// 太さは安全範囲へクランプされる（0 でも消えない・暴走もしない）。
    #[test]
    fn primitive3d_thickness_is_clamped() {
        for (given, expected) in [(0.0, MIN_THICKNESS_PX), (10_000.0, MAX_THICKNESS_PX)] {
            let c = cmd(
                Primitive3dKind::Polyline,
                Primitive3dDrawMode::Outline,
                vec![[0.0, 0.0, 0.0], [1.0, 0.0, 0.0]],
                [0.0; PRIM3D_EXTRA_FLOATS],
                given,
            );
            let (verts, _) = push_to_vecs(std::slice::from_ref(&c), &IDENTITY);
            assert!((verts[0].side - expected * HALF).abs() < TEST_EPS);
        }
    }

    /// 点は画面 px オフセットを持つ 4 頂点の正方形になる（side は 0）。
    #[test]
    fn primitive3d_point_builds_screen_quad() {
        const SIZE: f32 = 8.0;
        let c = cmd(
            Primitive3dKind::Point,
            Primitive3dDrawMode::Fill,
            vec![[1.0, 2.0, 3.0]],
            [SIZE, 0.0, 0.0, 0.0, 0.0, 0.0],
            1.0,
        );
        let (verts, indices) = push_to_vecs(std::slice::from_ref(&c), &IDENTITY);
        assert_eq!(verts.len(), QUAD_VERTS);
        assert_eq!(indices.len(), 6);
        let h = SIZE * HALF;
        for v in &verts {
            assert_eq!(v.side, 0.0);
            assert_eq!(v.position, [1.0, 2.0, 3.0]);
            assert!((v.offset[0].abs() - h).abs() < TEST_EPS);
            assert!((v.offset[1].abs() - h).abs() < TEST_EPS);
        }
    }

    /// カメラ背後へ伸びる線分は近平面まで縮められ、両端とも背後なら捨てられる。
    #[test]
    fn primitive3d_segment_is_clipped_at_near_plane() {
        // 前方 (z=5) → 背後 (z=-5)。w = z なので途中で符号が変わる。
        let clipped = clip_segment_to_near([0.0, 0.0, 5.0], [0.0, 0.0, -5.0], &PERSPECTIVE_W_IS_Z)
            .expect("片側は前方なので残る");
        assert_eq!(clipped.0, [0.0, 0.0, 5.0]);
        assert!(
            (clipped.1[2] - MIN_CLIP_W).abs() < TEST_EPS,
            "終点が近平面へ寄る: {}",
            clipped.1[2]
        );
        // 両端とも背後 → 捨てる
        assert!(
            clip_segment_to_near([0.0, 0.0, -1.0], [0.0, 0.0, -2.0], &PERSPECTIVE_W_IS_Z).is_none()
        );
        // 両端とも前方 → そのまま
        let keep = clip_segment_to_near([0.0, 0.0, 1.0], [0.0, 0.0, 2.0], &PERSPECTIVE_W_IS_Z)
            .expect("前方のみ");
        assert_eq!(keep, ([0.0, 0.0, 1.0], [0.0, 0.0, 2.0]));
    }

    /// カメラ背後の頂点を含む三角形は捨てられる（塗りは分割せず落とす契約）。
    #[test]
    fn primitive3d_triangle_behind_camera_is_dropped() {
        let c = cmd(
            Primitive3dKind::Polygon,
            Primitive3dDrawMode::Fill,
            vec![[0.0, 0.0, 1.0], [1.0, 0.0, 1.0], [0.0, 1.0, -1.0]],
            [0.0; PRIM3D_EXTRA_FLOATS],
            1.0,
        );
        let (verts, indices) = push_to_vecs(std::slice::from_ref(&c), &PERSPECTIVE_W_IS_Z);
        assert!(verts.is_empty());
        assert!(indices.is_empty());
    }

    /// 空レンジは描画をスキップできる状態になっている。
    #[test]
    fn primitive3d_empty_range_is_skipped() {
        assert!(Primitive3dRange::EMPTY.is_empty());
        assert!(!Primitive3dRange {
            first_index: 0,
            index_count: 3,
            depth_tested: true,
        }
        .is_empty());
    }

    /// シェーダーが naga で parse + validate できること（GPU 不要の静的検証）。
    #[test]
    fn primitive3d_shader_is_valid_wgsl() {
        let src = include_str!("../shaders/primitive3d.wgsl");
        let module = naga::front::wgsl::parse_str(src)
            .unwrap_or_else(|e| panic!("primitive3d.wgsl の parse に失敗: {e:?}"));
        let mut v = naga::valid::Validator::new(
            naga::valid::ValidationFlags::all(),
            naga::valid::Capabilities::empty(),
        );
        v.validate(&module)
            .unwrap_or_else(|e| panic!("primitive3d.wgsl の validate に失敗: {e:?}"));
    }
}
