// ============================================================
//  font/hint_plate.rs — 操作ガイドの背景プレート（角丸クアッド）
//
//  【責務】
//  スクリーンスペースに半透明の角丸矩形を 1 枚以上描くだけの、極小のレンダラー。
//  いまの利用者は `screen_hint.rs`（カーソル脇の操作ガイド）だけである。
//
//  【なぜ専用パイプラインなのか】
//  ガイドの文字はグリフアトラス（R8Unorm）をサンプルするテキストパイプラインで
//  描かれる。そこへ「単色の板」を混ぜるには、アトラスの中に必ず不透明な
//  テクセルがある前提を置くか、UV を細工する必要があり、どちらも
//  「アトラスの詰め方が変わった瞬間に静かに壊れる」類の結合になる。
//  頂点 5 属性・シェーダー 60 行の専用パイプラインを持つほうが、
//  結合が無く、意図も 1 か所に閉じる。
//
//  【角丸を SDF で出す理由】
//  矩形を重ねて角を落とすと、半透明どうしの重なりが濃く出てムラになる。
//  1 枚のクアッド内で符号付き距離を評価すれば重なりが生じない
//  （詳細は renderer/shaders/hint_plate.wgsl のコメント）。
// ============================================================

use wgpu::util::DeviceExt;

// ── 頂点型 ────────────────────────────────────────────────────

/// 角丸プレートの頂点。
///
/// `half` / `radius` はクアッドの 4 頂点で同じ値を持つ
/// （フラグメントで矩形中心からの距離を測るための「板の素性」であり、
///   頂点ごとに変わるのは `position` と `local` だけ）。
#[repr(C)]
#[derive(Copy, Clone, bytemuck::Pod, bytemuck::Zeroable)]
pub struct HintPlateVertex {
    /// NDC 座標（-1..1）。
    pub position: [f32; 2],
    /// 矩形中心からのオフセット [px]。
    pub local: [f32; 2],
    /// 矩形の半サイズ [px]。
    pub half: [f32; 2],
    /// `[角丸半径 px, 詰め物]`。vec2 で渡して 8 バイト境界を保つ。
    pub radius: [f32; 2],
    /// プレートの色（RGBA、ストレートアルファ）。
    pub color: [f32; 4],
}

/// 頂点 1 個ぶんのバイト数（頂点バッファのストライド）。
const VERTEX_STRIDE: u64 = std::mem::size_of::<HintPlateVertex>() as u64;

/// クアッド 1 枚を三角形 2 枚（＝頂点 6 個）で描く。
const VERTS_PER_QUAD: usize = 6;

// ── GpuHintPlateBatch ────────────────────────────────────────

/// GPU へ転送済みのプレート描画データ。
pub struct GpuHintPlateBatch {
    vertex_buf: wgpu::Buffer,
    vertex_count: u32,
}

// ── HintPlate ────────────────────────────────────────────────

/// 角丸プレートを描くレンダラー（パイプラインのみを保持する）。
pub struct HintPlate {
    pipeline: wgpu::RenderPipeline,
}

impl HintPlate {
    /// パイプラインを構築する。
    ///
    /// `depth_format` はメインパスのアタッチメント構成へ合わせるためだけに要る
    /// （深度テスト・書き込みは行わない。UI オーバーレイなので常に手前）。
    pub fn new(
        device: &wgpu::Device,
        surface_format: wgpu::TextureFormat,
        depth_format: wgpu::TextureFormat,
    ) -> Self {
        let shader = device.create_shader_module(wgpu::ShaderModuleDescriptor {
            label: Some("Hint Plate Shader"),
            source: wgpu::ShaderSource::Wgsl(
                include_str!("../renderer/shaders/hint_plate.wgsl").into(),
            ),
        });

        let layout = device.create_pipeline_layout(&wgpu::PipelineLayoutDescriptor {
            label: Some("Hint Plate Layout"),
            bind_group_layouts: &[],
            push_constant_ranges: &[],
        });

        let attrs = wgpu::vertex_attr_array![
            0 => Float32x2, // position (NDC)
            1 => Float32x2, // local  [px]
            2 => Float32x2, // half   [px]
            3 => Float32x2, // radius [px]（y は詰め物）
            4 => Float32x4, // color
        ];
        let pipeline = device.create_render_pipeline(&wgpu::RenderPipelineDescriptor {
            label: Some("Hint Plate Pipeline"),
            layout: Some(&layout),
            vertex: wgpu::VertexState {
                module: &shader,
                entry_point: Some("vs_main"),
                buffers: &[wgpu::VertexBufferLayout {
                    array_stride: VERTEX_STRIDE,
                    step_mode: wgpu::VertexStepMode::Vertex,
                    attributes: &attrs,
                }],
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

    /// 矩形群の頂点を GPU バッファへ載せる（矩形が無ければ None）。
    pub fn build(
        rects: &[HintPlateRect],
        screen_w: f32,
        screen_h: f32,
        device: &wgpu::Device,
    ) -> Option<GpuHintPlateBatch> {
        if rects.is_empty() || screen_w <= 0.0 || screen_h <= 0.0 {
            return None;
        }
        let mut verts: Vec<HintPlateVertex> = Vec::with_capacity(rects.len() * VERTS_PER_QUAD);
        for r in rects {
            verts.extend_from_slice(&r.to_vertices(screen_w, screen_h));
        }
        let vertex_buf = device.create_buffer_init(&wgpu::util::BufferInitDescriptor {
            label: Some("Hint Plate VB"),
            contents: bytemuck::cast_slice(&verts),
            usage: wgpu::BufferUsages::VERTEX,
        });
        Some(GpuHintPlateBatch { vertex_buf, vertex_count: verts.len() as u32 })
    }

    /// レンダーパスへ描画する。
    pub fn draw<'pass>(
        &'pass self,
        batch: &'pass GpuHintPlateBatch,
        pass: &mut wgpu::RenderPass<'pass>,
    ) {
        pass.set_pipeline(&self.pipeline);
        pass.set_vertex_buffer(0, batch.vertex_buf.slice(..));
        pass.draw(0..batch.vertex_count, 0..1);
    }
}

// ── HintPlateRect ────────────────────────────────────────────

/// プレート 1 枚の指定（スクリーン座標 [px]・左上原点）。
#[derive(Copy, Clone, Debug)]
pub struct HintPlateRect {
    /// 左上の X [px]。
    pub x: f32,
    /// 左上の Y [px]。
    pub y: f32,
    /// 幅 [px]。
    pub w: f32,
    /// 高さ [px]。
    pub h: f32,
    /// 角丸半径 [px]。
    pub radius: f32,
    /// 色（RGBA、ストレートアルファ）。
    pub color: [f32; 4],
}

impl HintPlateRect {
    /// 三角形 2 枚ぶん（6 頂点）へ展開する。
    ///
    /// スクリーン座標（左上原点・Y 下向き）から NDC（中心原点・Y 上向き）へ写す。
    fn to_vertices(&self, screen_w: f32, screen_h: f32) -> [HintPlateVertex; VERTS_PER_QUAD] {
        let half = [self.w * 0.5, self.h * 0.5];
        let radius = [self.radius, 0.0];
        let color = self.color;

        let to_ndc = |px: f32, py: f32| [px / screen_w * 2.0 - 1.0, 1.0 - py / screen_h * 2.0];
        // 4 隅（左上・右上・右下・左下）の「スクリーン座標」と「中心からの px オフセット」。
        let corners = [
            ([self.x, self.y], [-half[0], -half[1]]),
            ([self.x + self.w, self.y], [half[0], -half[1]]),
            ([self.x + self.w, self.y + self.h], [half[0], half[1]]),
            ([self.x, self.y + self.h], [-half[0], half[1]]),
        ];
        let v = |i: usize| {
            let (screen, local) = corners[i];
            HintPlateVertex {
                position: to_ndc(screen[0], screen[1]),
                local,
                half,
                radius,
                color,
            }
        };
        [v(0), v(1), v(2), v(0), v(2), v(3)]
    }
}

// ============================================================
//  テスト
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;

    /// 画面いっぱいの矩形が NDC の四隅へ正しく写ること（Y が反転すること）。
    #[test]
    fn full_screen_rect_maps_to_ndc_corners() {
        let r = HintPlateRect { x: 0.0, y: 0.0, w: 100.0, h: 50.0, radius: 4.0, color: [0.0; 4] };
        let v = r.to_vertices(100.0, 50.0);
        assert_eq!(v[0].position, [-1.0, 1.0], "左上 → NDC 左上");
        assert_eq!(v[1].position, [1.0, 1.0], "右上 → NDC 右上");
        assert_eq!(v[2].position, [1.0, -1.0], "右下 → NDC 右下");
    }

    /// 中心オフセットが半サイズと符号ごと一致すること（SDF の前提）。
    #[test]
    fn local_offsets_match_the_half_size() {
        let r = HintPlateRect { x: 10.0, y: 20.0, w: 40.0, h: 12.0, radius: 3.0, color: [0.0; 4] };
        let v = r.to_vertices(200.0, 100.0);
        assert_eq!(v[0].local, [-20.0, -6.0]);
        assert_eq!(v[2].local, [20.0, 6.0]);
        for vert in v {
            assert_eq!(vert.half, [20.0, 6.0], "半サイズは 4 頂点で共通");
        }
    }

    /// 三角形 2 枚（6 頂点）に展開されること。
    #[test]
    fn a_rect_expands_to_two_triangles() {
        let r = HintPlateRect { x: 0.0, y: 0.0, w: 1.0, h: 1.0, radius: 0.0, color: [0.0; 4] };
        assert_eq!(r.to_vertices(10.0, 10.0).len(), VERTS_PER_QUAD);
    }

    /// シェーダーが naga で parse + validate できること。
    ///
    /// WGSL は**実行時**にコンパイルされるので、書き間違いはビルドでは捕まらない。
    /// ここで検証しておかないと「ガイドを出した瞬間に落ちる」まで気付けない。
    #[test]
    fn shader_parses_and_validates() {
        let src = include_str!("../renderer/shaders/hint_plate.wgsl");
        let module = naga::front::wgsl::parse_str(src)
            .unwrap_or_else(|e| panic!("[hint_plate] WGSL parse 失敗: {e:?}"));
        let mut v = naga::valid::Validator::new(
            naga::valid::ValidationFlags::all(),
            naga::valid::Capabilities::empty(),
        );
        v.validate(&module)
            .unwrap_or_else(|e| panic!("[hint_plate] validate 失敗: {e:?}"));
    }

    /// 頂点属性のオフセットが WGSL の `@location` と食い違っていないこと。
    ///
    /// `vertex_attr_array!` はオフセットを型サイズから自動計算するので、
    /// フィールドを増減したときにここが崩れる。合計サイズだけは固定しておく。
    #[test]
    fn vertex_layout_size_is_stable() {
        // position(8) + local(8) + half(8) + radius(8) + color(16) = 48
        assert_eq!(VERTEX_STRIDE, 48);
    }
}
