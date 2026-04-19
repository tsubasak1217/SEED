// ============================================================
//  primitive_drawer.rs — プリミティブデバッグ描画
//
//  LineBatch にラインセグメントを蓄積し、フレーム開始前に
//  build() で GpuLineBatch を生成してから描画する。
//
//  使用例:
//    // フレームごとに蓄積
//    batch.clear();
//    batch.add_aabb(&aabb, [0.0, 1.0, 0.0, 1.0]);
//    batch.add_sphere(&sphere, 16, [1.0, 1.0, 0.0, 1.0]);
//    // GPU バッファ生成（render より前）
//    let gpu_batch = batch.build(ctx.device());
//    // 描画
//    renderer.render(|pass| {
//        draw_line_batch(pass, &gpu_batch, &camera_bg, &identity_model_bg, &ctx.pipelines);
//    })?;
// ============================================================

use std::f32::consts::PI;
use crate::engine::structs::primitives::{
    Aabb, Sphere, Ray,
    line::Line3, triangle::Triangle3,
};
use super::{
    uniforms::ColorVertex,
    gpu_resources::GpuLineBatch,
    pipeline::DrawPipelines,
};

// ============================================================
//  LineBatch
// ============================================================

/// ラインセグメントを蓄積するバッファ。
///
/// 毎フレーム `clear()` してからプリミティブを追加し、
/// `build()` で描画可能な `GpuLineBatch` を生成する。
#[derive(Default)]
pub struct LineBatch {
    vertices: Vec<ColorVertex>,
}

impl LineBatch {
    pub fn new() -> Self { Self::default() }

    /// 蓄積したすべてのラインを消去する。
    pub fn clear(&mut self) { self.vertices.clear(); }

    /// GPU バッファを生成して描画可能にする。
    /// `renderer.render()` を呼ぶ**前**に実行する。
    pub fn build(&self, device: &wgpu::Device) -> GpuLineBatch {
        GpuLineBatch::new(device, &self.vertices)
    }

    // ── 基本追加 ─────────────────────────────────────────────

    /// 2 点間のラインを追加する。
    pub fn add_line(&mut self, start: [f32; 3], end: [f32; 3], color: [f32; 4]) {
        self.vertices.push(ColorVertex { position: start, color });
        self.vertices.push(ColorVertex { position: end,   color });
    }

    // ── プリミティブ構造体からの追加 ──────────────────────────

    /// `Line3` プリミティブを追加する。
    pub fn add_line_prim(&mut self, line: &Line3, color: [f32; 4]) {
        self.add_line(
            [line.start.x, line.start.y, line.start.z],
            [line.end.x,   line.end.y,   line.end.z],
            color,
        );
    }

    /// `Ray` を有限長のラインとして追加する。
    pub fn add_ray(&mut self, ray: &Ray, length: f32, color: [f32; 4]) {
        let o   = ray.origin;
        let d   = ray.direction;
        let end = [o.x + d.x * length, o.y + d.y * length, o.z + d.z * length];
        self.add_line([o.x, o.y, o.z], end, color);
    }

    /// `Triangle3` の 3 辺を追加する。
    pub fn add_triangle(&mut self, tri: &Triangle3, color: [f32; 4]) {
        let a = [tri.a.x, tri.a.y, tri.a.z];
        let b = [tri.b.x, tri.b.y, tri.b.z];
        let c = [tri.c.x, tri.c.y, tri.c.z];
        self.add_line(a, b, color);
        self.add_line(b, c, color);
        self.add_line(c, a, color);
    }

    /// `Aabb` のワイヤーフレーム（12 辺）を追加する。
    pub fn add_aabb(&mut self, aabb: &Aabb, color: [f32; 4]) {
        let mn = [aabb.min.x, aabb.min.y, aabb.min.z];
        let mx = [aabb.max.x, aabb.max.y, aabb.max.z];

        // 8 頂点
        let v = [
            [mn[0], mn[1], mn[2]],  // 0: min
            [mx[0], mn[1], mn[2]],  // 1
            [mx[0], mx[1], mn[2]],  // 2
            [mn[0], mx[1], mn[2]],  // 3
            [mn[0], mn[1], mx[2]],  // 4
            [mx[0], mn[1], mx[2]],  // 5
            [mx[0], mx[1], mx[2]],  // 6: max
            [mn[0], mx[1], mx[2]],  // 7
        ];

        // 底面
        self.add_line(v[0], v[1], color); self.add_line(v[1], v[2], color);
        self.add_line(v[2], v[3], color); self.add_line(v[3], v[0], color);
        // 天面
        self.add_line(v[4], v[5], color); self.add_line(v[5], v[6], color);
        self.add_line(v[6], v[7], color); self.add_line(v[7], v[4], color);
        // 柱
        self.add_line(v[0], v[4], color); self.add_line(v[1], v[5], color);
        self.add_line(v[2], v[6], color); self.add_line(v[3], v[7], color);
    }

    /// `Sphere` のワイヤーフレーム（XZ / XY / YZ 平面の 3 サークル）を追加する。
    ///
    /// `segments`: 各円を近似する分割数（推奨: 16〜32）
    pub fn add_sphere(&mut self, sphere: &Sphere, segments: usize, color: [f32; 4]) {
        let cx = sphere.center.x;
        let cy = sphere.center.y;
        let cz = sphere.center.z;
        let r  = sphere.radius;

        let n = segments.max(4);
        for i in 0..n {
            let t0 = 2.0 * PI * i as f32 / n as f32;
            let t1 = 2.0 * PI * (i + 1) as f32 / n as f32;
            let (s0, c0) = t0.sin_cos();
            let (s1, c1) = t1.sin_cos();

            // XZ 平面（水平リング）
            self.add_line(
                [cx + r*c0, cy,       cz + r*s0],
                [cx + r*c1, cy,       cz + r*s1],
                color,
            );
            // XY 平面
            self.add_line(
                [cx + r*c0, cy + r*s0, cz],
                [cx + r*c1, cy + r*s1, cz],
                color,
            );
            // YZ 平面
            self.add_line(
                [cx,        cy + r*s0, cz + r*c0],
                [cx,        cy + r*s1, cz + r*c1],
                color,
            );
        }
    }

    /// ワールド軸（X/Y/Z 軸）を追加する。デバッグ用。
    ///
    /// X 軸 = 赤、Y 軸 = 緑、Z 軸 = 青
    pub fn add_world_axes(&mut self, origin: [f32; 3], length: f32) {
        let o = origin;
        self.add_line(o, [o[0]+length, o[1],       o[2]      ], [1.0, 0.0, 0.0, 1.0]);
        self.add_line(o, [o[0],       o[1]+length,  o[2]      ], [0.0, 1.0, 0.0, 1.0]);
        self.add_line(o, [o[0],       o[1],        o[2]+length], [0.0, 0.0, 1.0, 1.0]);
    }

    /// 回転ギズモ（3 軸リング）を追加する。
    ///
    /// - X 軸リング = 赤（YZ 平面）、Y 軸リング = 緑（XZ 平面）、Z 軸リング = 青（XY 平面）
    /// - `segments` : 円の分割数（推奨: 32）
    pub fn add_gizmo_rotate(&mut self, pos: [f32; 3], scale: f32, segments: usize) {
        let [px, py, pz] = pos;
        let n = segments.max(4);
        for i in 0..n {
            let t0 = 2.0 * PI * i as f32 / n as f32;
            let t1 = 2.0 * PI * (i + 1) as f32 / n as f32;
            let (s0, c0) = t0.sin_cos();
            let (s1, c1) = t1.sin_cos();
            // X 軸リング（赤）: YZ 平面
            self.add_line(
                [px, py + scale*s0, pz + scale*c0],
                [px, py + scale*s1, pz + scale*c1],
                [1.0, 0.2, 0.2, 1.0],
            );
            // Y 軸リング（緑）: XZ 平面
            self.add_line(
                [px + scale*c0, py, pz + scale*s0],
                [px + scale*c1, py, pz + scale*s1],
                [0.2, 1.0, 0.2, 1.0],
            );
            // Z 軸リング（青）: XY 平面
            self.add_line(
                [px + scale*c0, py + scale*s0, pz],
                [px + scale*c1, py + scale*s1, pz],
                [0.2, 0.2, 1.0, 1.0],
            );
        }
    }

    /// スケールギズモ（3 軸線 + 端点ボックス）を追加する。
    ///
    /// - X 軸 = 赤、Y 軸 = 緑、Z 軸 = 青
    pub fn add_gizmo_scale(&mut self, pos: [f32; 3], scale: f32) {
        let [px, py, pz] = pos;
        let bs = scale * 0.1; // エンドボックスの半サイズ

        // X 軸（赤）
        let xe = [px + scale, py, pz];
        self.add_line(pos, xe, [1.0, 0.2, 0.2, 1.0]);
        let c = [1.0, 0.2, 0.2, 1.0];
        self.add_cube_wireframe(xe, bs, c);

        // Y 軸（緑）
        let ye = [px, py + scale, pz];
        self.add_line(pos, ye, [0.2, 1.0, 0.2, 1.0]);
        let c = [0.2, 1.0, 0.2, 1.0];
        self.add_cube_wireframe(ye, bs, c);

        // Z 軸（青）
        let ze = [px, py, pz + scale];
        self.add_line(pos, ze, [0.2, 0.2, 1.0, 1.0]);
        let c = [0.2, 0.2, 1.0, 1.0];
        self.add_cube_wireframe(ze, bs, c);
    }

    fn add_cube_wireframe(&mut self, center: [f32; 3], half: f32, color: [f32; 4]) {
        let [cx, cy, cz] = center;
        let v = [
            [cx-half, cy-half, cz-half],
            [cx+half, cy-half, cz-half],
            [cx+half, cy+half, cz-half],
            [cx-half, cy+half, cz-half],
            [cx-half, cy-half, cz+half],
            [cx+half, cy-half, cz+half],
            [cx+half, cy+half, cz+half],
            [cx-half, cy+half, cz+half],
        ];
        self.add_line(v[0], v[1], color); self.add_line(v[1], v[2], color);
        self.add_line(v[2], v[3], color); self.add_line(v[3], v[0], color);
        self.add_line(v[4], v[5], color); self.add_line(v[5], v[6], color);
        self.add_line(v[6], v[7], color); self.add_line(v[7], v[4], color);
        self.add_line(v[0], v[4], color); self.add_line(v[1], v[5], color);
        self.add_line(v[2], v[6], color); self.add_line(v[3], v[7], color);
    }

    /// 移動ギズモ（3 軸矢印）を追加する。
    ///
    /// - X 軸 = 赤、Y 軸 = 緑、Z 軸 = 青
    /// - `scale` : 矢印の長さ（ワールドユニット）
    pub fn add_gizmo_translate(&mut self, pos: [f32; 3], scale: f32) {
        let [px, py, pz] = pos;
        let head = scale * 0.2;  // 矢じり部の長さ

        // X 軸（赤）
        let xe = [px + scale, py, pz];
        self.add_line(pos, xe, [1.0, 0.2, 0.2, 1.0]);
        self.add_line(xe, [px + scale - head, py + head * 0.4, pz            ], [1.0, 0.2, 0.2, 1.0]);
        self.add_line(xe, [px + scale - head, py - head * 0.4, pz            ], [1.0, 0.2, 0.2, 1.0]);
        self.add_line(xe, [px + scale - head, py,              pz + head * 0.4], [1.0, 0.2, 0.2, 1.0]);
        self.add_line(xe, [px + scale - head, py,              pz - head * 0.4], [1.0, 0.2, 0.2, 1.0]);

        // Y 軸（緑）
        let ye = [px, py + scale, pz];
        self.add_line(pos, ye, [0.2, 1.0, 0.2, 1.0]);
        self.add_line(ye, [px + head * 0.4, py + scale - head, pz            ], [0.2, 1.0, 0.2, 1.0]);
        self.add_line(ye, [px - head * 0.4, py + scale - head, pz            ], [0.2, 1.0, 0.2, 1.0]);
        self.add_line(ye, [px,              py + scale - head, pz + head * 0.4], [0.2, 1.0, 0.2, 1.0]);
        self.add_line(ye, [px,              py + scale - head, pz - head * 0.4], [0.2, 1.0, 0.2, 1.0]);

        // Z 軸（青）
        let ze = [px, py, pz + scale];
        self.add_line(pos, ze, [0.2, 0.2, 1.0, 1.0]);
        self.add_line(ze, [px + head * 0.4, py,              pz + scale - head], [0.2, 0.2, 1.0, 1.0]);
        self.add_line(ze, [px - head * 0.4, py,              pz + scale - head], [0.2, 0.2, 1.0, 1.0]);
        self.add_line(ze, [px,              py + head * 0.4, pz + scale - head], [0.2, 0.2, 1.0, 1.0]);
        self.add_line(ze, [px,              py - head * 0.4, pz + scale - head], [0.2, 0.2, 1.0, 1.0]);
    }
}

// ============================================================
//  draw_line_batch — LineBatch をレンダーパスで描画
// ============================================================

/// `GpuLineBatch` をレンダーパスで描画する。
///
/// # 引数
/// - `render_pass` : アクティブなレンダーパス
/// - `batch`       : `LineBatch::build()` で生成した GPU バッファ
/// - `camera_bg`   : カメラ bind group（group 0）
/// - `model_bg`    : モデル変換 bind group（group 1）。通常は単位行列を渡す
/// - `pipelines`   : 描画パイプライン一式
pub fn draw_line_batch<'pass>(
    render_pass: &mut wgpu::RenderPass<'pass>,
    batch:       &'pass GpuLineBatch,
    camera_bg:   &'pass wgpu::BindGroup,
    model_bg:    &'pass wgpu::BindGroup,
    pipelines:   &'pass DrawPipelines,
) {
    if batch.vertex_count == 0 { return; }

    render_pass.set_pipeline(&pipelines.unlit_line.pipeline);
    render_pass.set_bind_group(0, camera_bg, &[]);
    render_pass.set_bind_group(1, model_bg,  &[]);
    render_pass.set_vertex_buffer(0, batch.vertex_buffer.slice(..));
    render_pass.draw(0..batch.vertex_count, 0..1);
}

/// ギズモ用ラインバッチを常に前面（深度無視）で描画する。
pub fn draw_gizmo_batch<'pass>(
    render_pass: &mut wgpu::RenderPass<'pass>,
    batch:       &'pass GpuLineBatch,
    camera_bg:   &'pass wgpu::BindGroup,
    model_bg:    &'pass wgpu::BindGroup,
    pipelines:   &'pass DrawPipelines,
) {
    if batch.vertex_count == 0 { return; }

    render_pass.set_pipeline(&pipelines.unlit_line.gizmo_pipeline);
    render_pass.set_bind_group(0, camera_bg, &[]);
    render_pass.set_bind_group(1, model_bg,  &[]);
    render_pass.set_vertex_buffer(0, batch.vertex_buffer.slice(..));
    render_pass.draw(0..batch.vertex_count, 0..1);
}
