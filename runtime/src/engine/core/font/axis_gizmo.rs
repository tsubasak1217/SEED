// ============================================================
//  font/axis_gizmo.rs — Blender 風ビューポート軸ギズモ
//
//  右上コーナーにカメラ向きに連動する X/Y/Z 軸を表示する。
//  デバッグビルド + エディタモードでのみ使用する想定。
//
//  レンダリング:
//    - 軸ライン: 太いクワッド（2 三角形）
//    - 軸先端ドット: 塗りつぶし円（三角形ファン）
//    - 中心ドット: 白い塗りつぶし円
//    - ラベル: FontSystem による "X" "Y" "Z" テキスト
// ============================================================

use super::{FontConfig, FontSystem, GpuTextBatch, TextBatch};
use crate::engine::structs::transforms::Quaternion;
use crate::engine::structs::utils::color::Color;
use wgpu::util::DeviceExt;

// ── AxisHit — ドットのヒット情報 ─────────────────────────────

/// 軸ギズモのドットヒット情報。
#[derive(Clone, Copy, PartialEq, Eq, Debug)]
pub struct AxisHit {
    /// 軸インデックス（0=X, 1=Y, 2=Z）
    pub axis: usize,
    /// 正端なら true、負端なら false
    pub pos: bool,
}

// ── 定数 ──────────────────────────────────────────────────────

/// 右上端からのマージン（ピクセル）
const MARGIN: f32 = 64.0;
/// 軸の長さ（ピクセル）
const RADIUS_PX: f32 = 32.0;
/// ライン太さ（ピクセル）
const THICKNESS: f32 = 3.0;
/// 軸先端ドットの半径（ピクセル）
const DOT_RADIUS: f32 = 6.5;
/// 中心ドットの半径（ピクセル）
const CTR_RADIUS: f32 = 2.5;
/// ドット円分割数
const CIRCLE_SEG: u32 = 16;
/// ラベルフォントサイズ（ピクセル）
const LABEL_SIZE: f32 = 15.0;

// ── 軸定義 ───────────────────────────────────────────────────

struct AxisDef {
    /// ビュー行列の列インデックス（0=X, 1=Y, 2=Z）
    col: usize,
    color_pos: [f32; 4],
    color_neg: [f32; 4],
    label: &'static str,
}

const AXES: [AxisDef; 3] = [
    AxisDef {
        col: 0,
        color_pos: Color::GIZMO_X.to_array(),
        color_neg: [0.55, 0.14, 0.14, 0.70],
        label: "X",
    },
    AxisDef {
        col: 1,
        color_pos: Color::GIZMO_Y.to_array(),
        color_neg: [0.28, 0.44, 0.00, 0.70],
        label: "Y",
    },
    AxisDef {
        col: 2,
        color_pos: Color::GIZMO_Z.to_array(),
        color_neg: [0.10, 0.25, 0.55, 0.70],
        label: "Z",
    },
];

// ── 頂点型 ────────────────────────────────────────────────────

#[repr(C)]
#[derive(Copy, Clone, bytemuck::Pod, bytemuck::Zeroable)]
pub struct AxisGizmoVertex {
    pub position: [f32; 2],
    pub color: [f32; 4],
}

// ── AxisGizmoPipeline ────────────────────────────────────────

pub struct AxisGizmoPipeline {
    pub pipeline: wgpu::RenderPipeline,
}

impl AxisGizmoPipeline {
    pub fn new(
        device: &wgpu::Device,
        surface_format: wgpu::TextureFormat,
        depth_format: wgpu::TextureFormat,
    ) -> Self {
        let shader = device.create_shader_module(wgpu::ShaderModuleDescriptor {
            label: Some("Axis Gizmo Shader"),
            source: wgpu::ShaderSource::Wgsl(
                include_str!("../renderer/shaders/axis_gizmo.wgsl").into(),
            ),
        });

        let layout = device.create_pipeline_layout(&wgpu::PipelineLayoutDescriptor {
            label: Some("Axis Gizmo Layout"),
            bind_group_layouts: &[],
            push_constant_ranges: &[],
        });

        let stride = std::mem::size_of::<AxisGizmoVertex>() as u64;
        let vertex_layout = wgpu::VertexBufferLayout {
            array_stride: stride,
            step_mode: wgpu::VertexStepMode::Vertex,
            attributes: &[
                wgpu::VertexAttribute {
                    format: wgpu::VertexFormat::Float32x2,
                    offset: 0,
                    shader_location: 0,
                },
                wgpu::VertexAttribute {
                    format: wgpu::VertexFormat::Float32x4,
                    offset: 8,
                    shader_location: 1,
                },
            ],
        };

        let pipeline = device.create_render_pipeline(&wgpu::RenderPipelineDescriptor {
            label: Some("Axis Gizmo Pipeline"),
            layout: Some(&layout),
            vertex: wgpu::VertexState {
                module: &shader,
                entry_point: Some("vs_main"),
                buffers: &[vertex_layout],
                compilation_options: Default::default(),
            },
            fragment: Some(wgpu::FragmentState {
                module: &shader,
                entry_point: Some("fs_main"),
                targets: &[Some(wgpu::ColorTargetState {
                    format: surface_format,
                    blend: Some(wgpu::BlendState::ALPHA_BLENDING),
                    write_mask: wgpu::ColorWrites::ALL,
                })],
                compilation_options: Default::default(),
            }),
            primitive: wgpu::PrimitiveState {
                topology: wgpu::PrimitiveTopology::TriangleList,
                cull_mode: None,
                ..Default::default()
            },
            // メインレンダーパスの深度ステンシルアタッチメントに合わせる。
            // 深度書き込み・テストは行わず、フォーマットのみ一致させる。
            depth_stencil: Some(wgpu::DepthStencilState {
                format: depth_format,
                depth_write_enabled: false,
                depth_compare: wgpu::CompareFunction::Always,
                stencil: wgpu::StencilState::default(),
                bias: wgpu::DepthBiasState::default(),
            }),
            multisample: wgpu::MultisampleState::default(),
            multiview: None,
            cache: None,
        });

        Self { pipeline }
    }
}

// ── GpuAxisGizmoBatch ─────────────────────────────────────────

pub struct GpuAxisGizmoBatch {
    pub geo_buf: wgpu::Buffer,
    pub geo_count: u32,
    pub text: Option<GpuTextBatch>,
}

// ── AxisGizmo ─────────────────────────────────────────────────

pub struct AxisGizmo {
    pub pipeline: AxisGizmoPipeline,
    pub font_system: FontSystem,
}

impl AxisGizmo {
    pub fn new(
        device: &wgpu::Device,
        surface_format: wgpu::TextureFormat,
        depth_format: wgpu::TextureFormat,
    ) -> Self {
        let pipeline = AxisGizmoPipeline::new(device, surface_format, depth_format);
        let font_system =
            FontSystem::new(device, surface_format, depth_format, FontConfig::default())
                .expect("AxisGizmo: failed to load default font");
        Self {
            pipeline,
            font_system,
        }
    }

    /// カーソル座標でドット円のヒット判定を行い、最も手前のドットを返す。
    ///
    /// `cursor_x/y` はビューポートローカルピクセル座標（左上原点）。
    pub fn hit_test(
        cursor_x: f32,
        cursor_y: f32,
        rot: Quaternion,
        screen_w: f32,
        screen_h: f32,
    ) -> Option<AxisHit> {
        let gx = screen_w - MARGIN;
        let gy = MARGIN;

        let right = rot.right();
        let up = rot.up();
        let forward = rot.forward();

        let rights = [right.x, right.y, right.z];
        let ups = [up.x, up.y, up.z];
        let forwards = [forward.x, forward.y, forward.z];

        let mut best: Option<(f32, AxisHit)> = None;

        for i in 0..3 {
            let px = rights[i];
            let py = -ups[i];

            // 正端
            let tx = gx + px * RADIUS_PX;
            let ty = gy + py * RADIUS_PX;
            let cz = forwards[i];
            let dr = DOT_RADIUS;
            let dx = cursor_x - tx;
            let dy = cursor_y - ty;
            if dx * dx + dy * dy <= dr * dr {
                if best.map_or(true, |(bz, _)| cz > bz) {
                    best = Some((cz, AxisHit { axis: i, pos: true }));
                }
            }

            // 負端
            let tx = gx - px * RADIUS_PX;
            let ty = gy - py * RADIUS_PX;
            let cz = -forwards[i];
            let dr = DOT_RADIUS * 0.7;
            let dx = cursor_x - tx;
            let dy = cursor_y - ty;
            if dx * dx + dy * dy <= dr * dr {
                if best.map_or(true, |(bz, _)| cz > bz) {
                    best = Some((
                        cz,
                        AxisHit {
                            axis: i,
                            pos: false,
                        },
                    ));
                }
            }
        }

        best.map(|(_, hit)| hit)
    }

    /// 毎フレーム呼び出し。カメラ回転クォータニオンとスクリーンサイズからバッチを構築する。
    ///
    /// アルゴリズム:
    ///   半径1のギズモローカル頂点にカメラ回転行列の逆行列(= 転置 = R^T)を掛けると
    ///   カメラ空間での座標が得られる。これは rot.right/up/forward の dot 積と等価。
    ///   6エンドポイント(±X, ±Y, ±Z)を個別に cam_z でソートしてペインタ順に描画する。
    /// `hovered` が Some の場合、対応するドットをハイライト表示する。
    pub fn build(
        &mut self,
        rot: Quaternion,
        screen_w: f32,
        screen_h: f32,
        device: &wgpu::Device,
        queue: &wgpu::Queue,
        hovered: Option<AxisHit>,
    ) -> GpuAxisGizmoBatch {
        let cx = screen_w - MARGIN;
        let cy = MARGIN;

        // カメラのローカル基底ベクトル（ワールド空間）
        let right = rot.right(); // camera +X
        let up = rot.up(); // camera +Y
        let forward = rot.forward(); // camera +Z = 奥行き方向

        // ワールド軸 i ごとのカメラ空間成分:
        //   cam.x = dot(right,   e_i)   → スクリーン右
        //   cam.y = dot(up,      e_i)   → スクリーン上(ピクセルY反転が必要)
        //   cam.z = dot(forward, e_i)   → 奥行き(大きいほど遠い)
        let rights = [right.x, right.y, right.z];
        let ups = [up.x, up.y, up.z];
        let forwards = [forward.x, forward.y, forward.z];

        struct Endpoint {
            tip_x: f32,
            tip_y: f32,
            cam_z: f32,
            is_pos: bool,
            axis_i: usize,
        }

        // 6エンドポイントを個別に生成
        let mut endpoints: Vec<Endpoint> = Vec::with_capacity(6);
        for i in 0..3 {
            let px = rights[i];
            let py = -ups[i]; // スクリーンY下向きのため反転
            endpoints.push(Endpoint {
                tip_x: cx + px * RADIUS_PX,
                tip_y: cy + py * RADIUS_PX,
                cam_z: forwards[i],
                is_pos: true,
                axis_i: i,
            });
            endpoints.push(Endpoint {
                tip_x: cx - px * RADIUS_PX,
                tip_y: cy - py * RADIUS_PX,
                cam_z: -forwards[i],
                is_pos: false,
                axis_i: i,
            });
        }

        // cam_z 降順（奥→手前）でソート
        endpoints.sort_by(|a, b| {
            b.cam_z
                .partial_cmp(&a.cam_z)
                .unwrap_or(std::cmp::Ordering::Equal)
        });

        let mut geo: Vec<AxisGizmoVertex> = Vec::new();

        for ep in &endpoints {
            let ax = &AXES[ep.axis_i];
            let (base_color, base_r) = if ep.is_pos {
                (ax.color_pos, DOT_RADIUS)
            } else {
                (ax.color_neg, DOT_RADIUS * 0.7)
            };
            // ホバー中のドットをハイライト（白に近づけてサイズを 1.35 倍に）
            let is_hovered = hovered.map_or(false, |h| h.axis == ep.axis_i && h.pos == ep.is_pos);
            let (color, dot_r) = if is_hovered {
                (brighten(base_color), base_r * 1.35)
            } else {
                (base_color, base_r)
            };
            push_thick_line_px(
                &mut geo, cx, cy, ep.tip_x, ep.tip_y, THICKNESS, screen_w, screen_h, color,
            );
            push_circle_px(
                &mut geo, ep.tip_x, ep.tip_y, dot_r, screen_w, screen_h, color,
            );
        }

        // 中心ドット（最前面）
        push_circle_px(
            &mut geo,
            cx,
            cy,
            CTR_RADIUS,
            screen_w,
            screen_h,
            [0.92, 0.92, 0.92, 1.0],
        );

        let geo_count = geo.len() as u32;
        let geo_buf = device.create_buffer_init(&wgpu::util::BufferInitDescriptor {
            label: Some("Axis Gizmo Geo Buffer"),
            contents: bytemuck::cast_slice(&geo),
            usage: wgpu::BufferUsages::VERTEX,
        });

        // ── テキストラベル（正端ドット中心に配置）──────────
        let mut text_batch = TextBatch::new();

        for ep in endpoints.iter().filter(|e| e.is_pos) {
            let ax = &AXES[ep.axis_i];
            let glyphs = self.font_system.prepare_glyphs(ax.label, LABEL_SIZE);
            let lx = ep.tip_x - LABEL_SIZE * 0.38;
            let ly = ep.tip_y - LABEL_SIZE * 0.62;
            text_batch.add_text_screen(
                ax.label,
                lx,
                ly,
                LABEL_SIZE,
                [1.0, 1.0, 1.0, 1.0],
                &glyphs,
                screen_w,
                screen_h,
            );
        }

        self.font_system.flush(queue);
        let text = self.font_system.build_gpu_batch(&text_batch, device);

        GpuAxisGizmoBatch {
            geo_buf,
            geo_count,
            text,
        }
    }

    /// メインレンダーパスに描画する（深度テストなし、UI オーバーレイ）。
    pub fn draw<'pass>(
        &'pass self,
        batch: &'pass GpuAxisGizmoBatch,
        pass: &mut wgpu::RenderPass<'pass>,
    ) {
        if batch.geo_count > 0 {
            pass.set_pipeline(&self.pipeline.pipeline);
            pass.set_vertex_buffer(0, batch.geo_buf.slice(..));
            pass.draw(0..batch.geo_count, 0..1);
        }
        if let Some(text) = &batch.text {
            self.font_system.draw_text_batch(text, pass);
        }
    }
}

// ── 色ヘルパー ────────────────────────────────────────────────

/// 色を白方向に 40% 補間して明るくする（ホバーハイライト用）。
#[inline]
fn brighten(c: [f32; 4]) -> [f32; 4] {
    [
        (c[0] + (1.0 - c[0]) * 0.4).min(1.0),
        (c[1] + (1.0 - c[1]) * 0.4).min(1.0),
        (c[2] + (1.0 - c[2]) * 0.4).min(1.0),
        c[3].max(0.9),
    ]
}

// ── ジオメトリヘルパー ────────────────────────────────────────

/// ピクセル座標を NDC に変換する。
#[inline]
fn px_to_ndc(px: f32, py: f32, sw: f32, sh: f32) -> [f32; 2] {
    [px / sw * 2.0 - 1.0, 1.0 - py / sh * 2.0]
}

/// ピクセル座標で指定した太線クワッドを追加する。
fn push_thick_line_px(
    verts: &mut Vec<AxisGizmoVertex>,
    x0: f32,
    y0: f32,
    x1: f32,
    y1: f32,
    thick: f32,
    sw: f32,
    sh: f32,
    color: [f32; 4],
) {
    let dx = x1 - x0;
    let dy = y1 - y0;
    let len = (dx * dx + dy * dy).sqrt();
    if len < 0.5 {
        return;
    }

    // 法線ベクトル（ピクセル空間）
    let px = -dy / len * thick * 0.5;
    let py = dx / len * thick * 0.5;

    let pts = [
        px_to_ndc(x0 - px, y0 - py, sw, sh),
        px_to_ndc(x0 + px, y0 + py, sw, sh),
        px_to_ndc(x1 + px, y1 + py, sw, sh),
        px_to_ndc(x1 - px, y1 - py, sw, sh),
    ];
    verts.push(AxisGizmoVertex {
        position: pts[0],
        color,
    });
    verts.push(AxisGizmoVertex {
        position: pts[1],
        color,
    });
    verts.push(AxisGizmoVertex {
        position: pts[2],
        color,
    });
    verts.push(AxisGizmoVertex {
        position: pts[0],
        color,
    });
    verts.push(AxisGizmoVertex {
        position: pts[2],
        color,
    });
    verts.push(AxisGizmoVertex {
        position: pts[3],
        color,
    });
}

/// ピクセル座標で指定した塗りつぶし円を追加する（三角形ファン）。
fn push_circle_px(
    verts: &mut Vec<AxisGizmoVertex>,
    cx: f32,
    cy: f32,
    radius: f32,
    sw: f32,
    sh: f32,
    color: [f32; 4],
) {
    let n = CIRCLE_SEG;
    let center = px_to_ndc(cx, cy, sw, sh);
    for i in 0..n {
        let a0 = i as f32 / n as f32 * std::f32::consts::TAU;
        let a1 = (i + 1) as f32 / n as f32 * std::f32::consts::TAU;
        let p0 = px_to_ndc(cx + radius * a0.cos(), cy + radius * a0.sin(), sw, sh);
        let p1 = px_to_ndc(cx + radius * a1.cos(), cy + radius * a1.sin(), sw, sh);
        verts.push(AxisGizmoVertex {
            position: center,
            color,
        });
        verts.push(AxisGizmoVertex {
            position: p0,
            color,
        });
        verts.push(AxisGizmoVertex {
            position: p1,
            color,
        });
    }
}
