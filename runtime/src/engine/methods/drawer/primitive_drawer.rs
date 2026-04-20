// ============================================================
//  primitive_drawer.rs — プリミティブデバッグ描画
//
//  LineBatch  : AABB/球体/レイ等のデバッグ線（LineList）
//  GizmoBatch : 編集ギズモ専用（太線＋ソリッド先端、TriangleList）
// ============================================================

use std::f32::consts::PI;
use crate::engine::structs::primitives::{
    Aabb, Sphere, Ray,
    line::Line3, triangle::Triangle3,
};
use crate::engine::structs::utils::Color;
use crate::engine::methods::gizmo_interact::GizmoPart;
use super::{
    uniforms::{ColorVertex, GizmoVertex},
    gpu_resources::{GpuLineBatch, GpuGizmoBatch},
    pipeline::DrawPipelines,
};

// ── ギズモ色定数 ──────────────────────────────────────────────
const GX:      Color = Color::GIZMO_X;
const GY:      Color = Color::GIZMO_Y;
const GZ:      Color = Color::GIZMO_Z;
const GX_FILL: Color = Color::new(GX.r, GX.g, GX.b, 0.3);
const GY_FILL: Color = Color::new(GY.r, GY.g, GY.b, 0.3);
const GZ_FILL: Color = Color::new(GZ.r, GZ.g, GZ.b, 0.3);

/// 色を白方向に 50% 補間して明るくする（ハイライト用）。
#[inline]
fn highlight(c: Color) -> Color {
    Color::new(
        (c.r + (1.0 - c.r) * 0.5).min(1.0),
        (c.g + (1.0 - c.g) * 0.5).min(1.0),
        (c.b + (1.0 - c.b) * 0.5).min(1.0),
        c.a,
    )
}

/// 半透明塗りつぶし色のハイライト版（不透明度も上げる）。
#[inline]
fn highlight_fill(c: Color) -> Color {
    Color::new(
        (c.r + (1.0 - c.r) * 0.5).min(1.0),
        (c.g + (1.0 - c.g) * 0.5).min(1.0),
        (c.b + (1.0 - c.b) * 0.5).min(1.0),
        0.6,
    )
}

// ============================================================
//  LineBatch — デバッグ線バッチ（LineList）
// ============================================================

#[derive(Default)]
pub struct LineBatch {
    vertices: Vec<ColorVertex>,
}

impl LineBatch {
    pub fn new() -> Self { Self::default() }

    pub fn clear(&mut self) { self.vertices.clear(); }

    pub fn build(&self, device: &wgpu::Device) -> GpuLineBatch {
        GpuLineBatch::new(device, &self.vertices)
    }

    pub fn add_line(&mut self, start: [f32; 3], end: [f32; 3], color: [f32; 4]) {
        self.vertices.push(ColorVertex { position: start, color });
        self.vertices.push(ColorVertex { position: end,   color });
    }

    pub fn add_line_prim(&mut self, line: &Line3, color: [f32; 4]) {
        self.add_line(
            [line.start.x, line.start.y, line.start.z],
            [line.end.x,   line.end.y,   line.end.z],
            color,
        );
    }

    pub fn add_ray(&mut self, ray: &Ray, length: f32, color: [f32; 4]) {
        let o   = ray.origin;
        let d   = ray.direction;
        let end = [o.x + d.x * length, o.y + d.y * length, o.z + d.z * length];
        self.add_line([o.x, o.y, o.z], end, color);
    }

    pub fn add_triangle(&mut self, tri: &Triangle3, color: [f32; 4]) {
        let a = [tri.a.x, tri.a.y, tri.a.z];
        let b = [tri.b.x, tri.b.y, tri.b.z];
        let c = [tri.c.x, tri.c.y, tri.c.z];
        self.add_line(a, b, color);
        self.add_line(b, c, color);
        self.add_line(c, a, color);
    }

    pub fn add_aabb(&mut self, aabb: &Aabb, color: [f32; 4]) {
        let mn = [aabb.min.x, aabb.min.y, aabb.min.z];
        let mx = [aabb.max.x, aabb.max.y, aabb.max.z];
        let v = [
            [mn[0], mn[1], mn[2]], [mx[0], mn[1], mn[2]],
            [mx[0], mx[1], mn[2]], [mn[0], mx[1], mn[2]],
            [mn[0], mn[1], mx[2]], [mx[0], mn[1], mx[2]],
            [mx[0], mx[1], mx[2]], [mn[0], mx[1], mx[2]],
        ];
        self.add_line(v[0], v[1], color); self.add_line(v[1], v[2], color);
        self.add_line(v[2], v[3], color); self.add_line(v[3], v[0], color);
        self.add_line(v[4], v[5], color); self.add_line(v[5], v[6], color);
        self.add_line(v[6], v[7], color); self.add_line(v[7], v[4], color);
        self.add_line(v[0], v[4], color); self.add_line(v[1], v[5], color);
        self.add_line(v[2], v[6], color); self.add_line(v[3], v[7], color);
    }

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
            self.add_line(
                [cx + r*c0, cy,       cz + r*s0],
                [cx + r*c1, cy,       cz + r*s1], color,
            );
            self.add_line(
                [cx + r*c0, cy + r*s0, cz],
                [cx + r*c1, cy + r*s1, cz], color,
            );
            self.add_line(
                [cx,        cy + r*s0, cz + r*c0],
                [cx,        cy + r*s1, cz + r*c1], color,
            );
        }
    }

    pub fn add_world_axes(&mut self, origin: [f32; 3], length: f32) {
        let o = origin;
        self.add_line(o, [o[0]+length, o[1],       o[2]      ], [1.0, 0.0, 0.0, 1.0]);
        self.add_line(o, [o[0],       o[1]+length,  o[2]      ], [0.0, 1.0, 0.0, 1.0]);
        self.add_line(o, [o[0],       o[1],        o[2]+length], [0.0, 0.0, 1.0, 1.0]);
    }
}

// ============================================================
//  GizmoBatch — 編集ギズモ専用バッチ（太線 + ソリッド先端）
// ============================================================

/// 編集ギズモ描画バッチ。
///
/// `line_verts` は太線クワッド（`GizmoVertex` 形式）、
/// `tri_verts`  はソリッド三角形（`ColorVertex` 形式）。
#[derive(Default)]
pub struct GizmoBatch {
    line_verts: Vec<GizmoVertex>,
    tri_verts:  Vec<ColorVertex>,
}

impl GizmoBatch {
    pub fn new() -> Self { Self::default() }

    pub fn clear(&mut self) {
        self.line_verts.clear();
        self.tri_verts.clear();
    }

    pub fn build(&self, device: &wgpu::Device) -> GpuGizmoBatch {
        GpuGizmoBatch::new(device, &self.line_verts, &self.tri_verts)
    }

    // ── 内部ヘルパー ──────────────────────────────────────────

    fn add_thick_line(&mut self, pos_a: [f32; 3], pos_b: [f32; 3], color: Color) {
        let color = color.to_array();
        let v = |t: f32, side: f32| GizmoVertex { pos_a, t, pos_b, side, color };
        self.line_verts.extend_from_slice(&[
            v(0.0, -1.0), v(0.0,  1.0), v(1.0, -1.0),
            v(1.0, -1.0), v(0.0,  1.0), v(1.0,  1.0),
        ]);
    }

    fn add_solid_tri(&mut self, a: [f32; 3], b: [f32; 3], c: [f32; 3], color: Color) {
        let color = color.to_array();
        self.tri_verts.push(ColorVertex { position: a, color });
        self.tri_verts.push(ColorVertex { position: b, color });
        self.tri_verts.push(ColorVertex { position: c, color });
    }

    /// 平面ハンドル矩形（塗りつぶし＋輪郭）を追加する。
    /// a,b,c,d は CCW 順の 4 頂点。
    fn add_plane_quad(
        &mut self,
        a: [f32; 3], b: [f32; 3], c: [f32; 3], d: [f32; 3],
        fill:    Color,
        outline: Color,
    ) {
        self.add_solid_tri(a, b, c, fill);
        self.add_solid_tri(a, c, d, fill);
        self.add_thick_line(a, b, outline);
        self.add_thick_line(b, c, outline);
        self.add_thick_line(c, d, outline);
        self.add_thick_line(d, a, outline);
    }

    /// XY / XZ / YZ 3 平面のハンドル矩形を追加する（移動・スケール共通）。
    fn add_plane_handles(&mut self, pos: [f32; 3], radius: f32, hovered: Option<GizmoPart>) {
        let [px, py, pz] = pos;
        let o = radius * 0.5;
        let s = radius * 0.075;

        let (fxy, cxy) = if hovered == Some(GizmoPart::PlaneXY) {
            (highlight_fill(GZ_FILL), highlight(GZ))
        } else { (GZ_FILL, GZ) };
        let (fxz, cxz) = if hovered == Some(GizmoPart::PlaneXZ) {
            (highlight_fill(GY_FILL), highlight(GY))
        } else { (GY_FILL, GY) };
        let (fyz, cyz) = if hovered == Some(GizmoPart::PlaneYZ) {
            (highlight_fill(GX_FILL), highlight(GX))
        } else { (GX_FILL, GX) };

        // XY 平面（Z 軸色 = 青）
        self.add_plane_quad(
            [px+o-s, py+o-s, pz], [px+o+s, py+o-s, pz],
            [px+o+s, py+o+s, pz], [px+o-s, py+o+s, pz],
            fxy, cxy,
        );
        // XZ 平面（Y 軸色 = 緑）
        self.add_plane_quad(
            [px+o-s, py, pz+o-s], [px+o+s, py, pz+o-s],
            [px+o+s, py, pz+o+s], [px+o-s, py, pz+o+s],
            fxz, cxz,
        );
        // YZ 平面（X 軸色 = 赤）
        self.add_plane_quad(
            [px, py+o-s, pz+o-s], [px, py+o+s, pz+o-s],
            [px, py+o+s, pz+o+s], [px, py+o-s, pz+o+s],
            fyz, cyz,
        );
    }

    /// 円錐（先端 = tip、底面中心 = base_center）を TriangleList で追加する。
    fn add_cone(
        &mut self,
        tip:         [f32; 3],
        base_center: [f32; 3],
        radius:      f32,
        segs:        usize,
        color:       Color,
    ) {
        let (u, v) = perp_basis(
            [tip[0]-base_center[0], tip[1]-base_center[1], tip[2]-base_center[2]]
        );
        let n = segs.max(3);
        for i in 0..n {
            let t0 = 2.0 * PI * i as f32 / n as f32;
            let t1 = 2.0 * PI * (i + 1) as f32 / n as f32;
            let (s0, c0) = t0.sin_cos();
            let (s1, c1) = t1.sin_cos();
            let p0 = [
                base_center[0] + radius * (u[0]*c0 + v[0]*s0),
                base_center[1] + radius * (u[1]*c0 + v[1]*s0),
                base_center[2] + radius * (u[2]*c0 + v[2]*s0),
            ];
            let p1 = [
                base_center[0] + radius * (u[0]*c1 + v[0]*s1),
                base_center[1] + radius * (u[1]*c1 + v[1]*s1),
                base_center[2] + radius * (u[2]*c1 + v[2]*s1),
            ];
            self.add_solid_tri(tip, p1, p0, color);
            self.add_solid_tri(base_center, p0, p1, color);
        }
    }

    /// 原点中心ハンドル用の小球（3 平面ディスク重ね合わせ）を追加する。
    fn add_center_dot(&mut self, pos: [f32; 3], radius: f32, color: Color) {
        let [px, py, pz] = pos;
        let n = 8usize;
        for i in 0..n {
            let t0 = 2.0 * PI * i as f32 / n as f32;
            let t1 = 2.0 * PI * (i + 1) as f32 / n as f32;
            let (s0, c0) = t0.sin_cos();
            let (s1, c1) = t1.sin_cos();
            let r = radius;
            self.add_solid_tri(pos, [px+r*c0, py+r*s0, pz],      [px+r*c1, py+r*s1, pz],      color);
            self.add_solid_tri(pos, [px+r*c0, py,      pz+r*s0], [px+r*c1, py,      pz+r*s1], color);
            self.add_solid_tri(pos, [px,      py+r*c0, pz+r*s0], [px,      py+r*c1, pz+r*s1], color);
        }
    }

    /// ソリッドキューブを追加する。
    fn add_solid_cube(&mut self, center: [f32; 3], half: f32, color: Color) {
        let [cx, cy, cz] = center;
        let v = [
            [cx-half, cy-half, cz-half], [cx+half, cy-half, cz-half],
            [cx+half, cy+half, cz-half], [cx-half, cy+half, cz-half],
            [cx-half, cy-half, cz+half], [cx+half, cy-half, cz+half],
            [cx+half, cy+half, cz+half], [cx-half, cy+half, cz+half],
        ];
        // 前面 (-Z)
        self.add_solid_tri(v[0], v[2], v[1], color); self.add_solid_tri(v[0], v[3], v[2], color);
        // 背面 (+Z)
        self.add_solid_tri(v[4], v[5], v[6], color); self.add_solid_tri(v[4], v[6], v[7], color);
        // 左面 (-X)
        self.add_solid_tri(v[0], v[4], v[7], color); self.add_solid_tri(v[0], v[7], v[3], color);
        // 右面 (+X)
        self.add_solid_tri(v[1], v[2], v[6], color); self.add_solid_tri(v[1], v[6], v[5], color);
        // 下面 (-Y)
        self.add_solid_tri(v[0], v[1], v[5], color); self.add_solid_tri(v[0], v[5], v[4], color);
        // 上面 (+Y)
        self.add_solid_tri(v[3], v[7], v[6], color); self.add_solid_tri(v[3], v[6], v[2], color);
    }

    // ── 公開ギズモ API ────────────────────────────────────────

    /// 移動ギズモ（3 軸矢印）を追加する。先端はコーン（ソリッド）。
    ///
    /// - X 軸 = 赤、Y 軸 = 緑、Z 軸 = 青
    /// - `radius`: ギズモ全体の半径（デフォルト: 1.0）
    pub fn add_gizmo_translate(&mut self, pos: [f32; 3], radius: f32, hovered: Option<GizmoPart>) {
        let [px, py, pz] = pos;
        let head_len = radius * 0.25;
        let head_r   = radius * 0.07;

        let cx = if hovered == Some(GizmoPart::AxisX) { highlight(GX) } else { GX };
        let cy = if hovered == Some(GizmoPart::AxisY) { highlight(GY) } else { GY };
        let cz = if hovered == Some(GizmoPart::AxisZ) { highlight(GZ) } else { GZ };

        // X 軸（赤）
        let shaft_tip = [px + radius - head_len, py, pz];
        self.add_thick_line(pos, shaft_tip, cx);
        self.add_cone([px + radius, py, pz], shaft_tip, head_r, 8, cx);

        // Y 軸（緑）
        let shaft_tip = [px, py + radius - head_len, pz];
        self.add_thick_line(pos, shaft_tip, cy);
        self.add_cone([px, py + radius, pz], shaft_tip, head_r, 8, cy);

        // Z 軸（青）
        let shaft_tip = [px, py, pz + radius - head_len];
        self.add_thick_line(pos, shaft_tip, cz);
        self.add_cone([px, py, pz + radius], shaft_tip, head_r, 8, cz);

        self.add_plane_handles(pos, radius, hovered);

        // 中心ハンドル（白 / ホバー時は黄）
        let cc = if hovered == Some(GizmoPart::Center) { Color::YELLOW } else { Color::WHITE };
        self.add_center_dot(pos, radius * 0.055, cc);
    }

    /// スケールギズモ（3 軸線 + 端点キューブ）を追加する。先端はソリッドキューブ。
    ///
    /// - X 軸 = 赤、Y 軸 = 緑、Z 軸 = 青
    pub fn add_gizmo_scale(&mut self, pos: [f32; 3], radius: f32, hovered: Option<GizmoPart>) {
        let [px, py, pz] = pos;
        let cube_half = radius * 0.07;

        let cx = if hovered == Some(GizmoPart::AxisX) { highlight(GX) } else { GX };
        let cy = if hovered == Some(GizmoPart::AxisY) { highlight(GY) } else { GY };
        let cz = if hovered == Some(GizmoPart::AxisZ) { highlight(GZ) } else { GZ };

        // X 軸（赤）
        let xe = [px + radius, py, pz];
        self.add_thick_line(pos, xe, cx);
        self.add_solid_cube(xe, cube_half, cx);

        // Y 軸（緑）
        let ye = [px, py + radius, pz];
        self.add_thick_line(pos, ye, cy);
        self.add_solid_cube(ye, cube_half, cy);

        // Z 軸（青）
        let ze = [px, py, pz + radius];
        self.add_thick_line(pos, ze, cz);
        self.add_solid_cube(ze, cube_half, cz);

        self.add_plane_handles(pos, radius, hovered);

        // 中心ハンドル（白 / ホバー時は黄）
        let cc = if hovered == Some(GizmoPart::Center) { Color::YELLOW } else { Color::WHITE };
        self.add_center_dot(pos, radius * 0.055, cc);
    }

    /// 回転ギズモ（3 軸半円リング）を追加する。
    ///
    /// カメラ方向を各リング平面に射影し、外向きラジアル方向と内積が正の
    /// セグメントのみ描画することで Blender 風の半円表示を実現する。
    ///
    /// - X 軸リング = 赤（YZ 平面）、Y 軸リング = 緑（XZ 平面）、Z 軸リング = 青（XY 平面）
    /// - `cam_pos`  : ワールド空間でのカメラ位置（半円方向の判定に使用）
    /// - `segments` : 円の分割数（推奨: 64 — 半円で半分が描画される）
    pub fn add_gizmo_rotate(
        &mut self,
        pos:      [f32; 3],
        radius:   f32,
        segments: usize,
        cam_pos:  [f32; 3],
        hovered:  Option<GizmoPart>,
        dragging: Option<GizmoPart>,
    ) {
        let [px, py, pz] = pos;

        let cx = if hovered == Some(GizmoPart::AxisX) { highlight(GX) } else { GX };
        let cy = if hovered == Some(GizmoPart::AxisY) { highlight(GY) } else { GY };
        let cz = if hovered == Some(GizmoPart::AxisZ) { highlight(GZ) } else { GZ };

        // オブジェクト→カメラの正規化ベクトル（半円フィルタ用）
        let cd = {
            let d = [cam_pos[0]-px, cam_pos[1]-py, cam_pos[2]-pz];
            let len = (d[0]*d[0]+d[1]*d[1]+d[2]*d[2]).sqrt().max(1e-6);
            [d[0]/len, d[1]/len, d[2]/len]
        };

        let n = segments.max(4);
        for i in 0..n {
            let t0  = 2.0 * PI * i as f32 / n as f32;
            let t1  = 2.0 * PI * (i + 1) as f32 / n as f32;
            let tm  = (t0 + t1) * 0.5;
            let (sm, cm) = tm.sin_cos();
            let (s0, c0) = t0.sin_cos();
            let (s1, c1) = t1.sin_cos();

            // ドラッグ中は操作軸のみ全円、他は非表示。
            // 非ドラッグ時はカメラ向き判定で手前半円のみ表示。
            let show_x = match dragging {
                Some(GizmoPart::AxisX) => true,
                Some(_) => false,
                None => sm * cd[1] + cm * cd[2] > 0.0,
            };
            let show_y = match dragging {
                Some(GizmoPart::AxisY) => true,
                Some(_) => false,
                None => cm * cd[0] + sm * cd[2] > 0.0,
            };
            let show_z = match dragging {
                Some(GizmoPart::AxisZ) => true,
                Some(_) => false,
                None => cm * cd[0] + sm * cd[1] > 0.0,
            };

            if show_x {
                self.add_thick_line(
                    [px, py + radius*s0, pz + radius*c0],
                    [px, py + radius*s1, pz + radius*c1],
                    cx,
                );
            }
            if show_y {
                self.add_thick_line(
                    [px + radius*c0, py, pz + radius*s0],
                    [px + radius*c1, py, pz + radius*s1],
                    cy,
                );
            }
            if show_z {
                self.add_thick_line(
                    [px + radius*c0, py + radius*s0, pz],
                    [px + radius*c1, py + radius*s1, pz],
                    cz,
                );
            }
        }
    }
}

// ── 数学ヘルパー ───────────────────────────────────────────────

fn perp_basis(d: [f32; 3]) -> ([f32; 3], [f32; 3]) {
    let len = (d[0]*d[0] + d[1]*d[1] + d[2]*d[2]).sqrt().max(1e-6);
    let dn = [d[0]/len, d[1]/len, d[2]/len];
    let up = if dn[0].abs() < 0.9 { [1.0_f32, 0.0, 0.0] } else { [0.0_f32, 1.0, 0.0] };
    let u = cross_norm(dn, up);
    let v = cross(dn, u);
    (u, v)
}

fn cross(a: [f32; 3], b: [f32; 3]) -> [f32; 3] {
    [a[1]*b[2]-a[2]*b[1], a[2]*b[0]-a[0]*b[2], a[0]*b[1]-a[1]*b[0]]
}

fn cross_norm(a: [f32; 3], b: [f32; 3]) -> [f32; 3] {
    let c = cross(a, b);
    let len = (c[0]*c[0] + c[1]*c[1] + c[2]*c[2]).sqrt().max(1e-6);
    [c[0]/len, c[1]/len, c[2]/len]
}

// ============================================================
//  draw_line_batch — LineBatch をレンダーパスで描画
// ============================================================

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

// ============================================================
//  draw_gizmo_batch — GizmoBatch をレンダーパスで描画
// ============================================================

/// ギズモバッチを常に前面（深度無視）で描画する。
///
/// 太線は `gizmo_line_pipeline`（GizmoVertex + TriangleList）、
/// ソリッド先端は `gizmo_tri_pipeline`（ColorVertex + TriangleList）で描画する。
pub fn draw_gizmo_batch<'pass>(
    render_pass: &mut wgpu::RenderPass<'pass>,
    batch:       &'pass GpuGizmoBatch,
    camera_bg:   &'pass wgpu::BindGroup,
    model_bg:    &'pass wgpu::BindGroup,
    pipelines:   &'pass DrawPipelines,
) {
    // 太線クワッド
    if batch.line_count > 0 {
        if let Some(buf) = &batch.line_buffer {
            render_pass.set_pipeline(&pipelines.unlit_line.gizmo_line_pipeline);
            render_pass.set_bind_group(0, camera_bg, &[]);
            render_pass.set_bind_group(1, model_bg,  &[]);
            render_pass.set_vertex_buffer(0, buf.slice(..));
            render_pass.draw(0..batch.line_count, 0..1);
        }
    }
    // ソリッド先端
    if batch.tri_count > 0 {
        if let Some(buf) = &batch.tri_buffer {
            render_pass.set_pipeline(&pipelines.unlit_line.gizmo_tri_pipeline);
            render_pass.set_bind_group(0, camera_bg, &[]);
            render_pass.set_bind_group(1, model_bg,  &[]);
            render_pass.set_vertex_buffer(0, buf.slice(..));
            render_pass.draw(0..batch.tri_count, 0..1);
        }
    }
}
