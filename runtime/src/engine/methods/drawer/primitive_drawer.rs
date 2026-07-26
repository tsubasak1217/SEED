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

/// デバッグ線バッチ（LineList）。
///
/// AABB・球体・レイ等のデバッグ形状を `ColorVertex`（位置+色）の頂点列として蓄積し、
/// `build` で GPU バッファ（`GpuLineBatch`）へアップロードして `draw_line_batch` で描画する。
#[derive(Default)]
pub struct LineBatch {
    vertices: Vec<ColorVertex>,
}

impl LineBatch {
    pub fn new() -> Self { Self::default() }

    pub fn clear(&mut self) { self.vertices.clear(); }

    /// 描画頂点が 0 かどうかを返す（バッチが空かどうかの確認用）。
    pub fn is_empty(&self) -> bool { self.vertices.is_empty() }

    pub fn build(&self, device: &wgpu::Device) -> GpuLineBatch {
        GpuLineBatch::new(device, &self.vertices)
    }

    /// 蓄積済みの 1px ライン（LineList: 2 頂点 = 1 セグメント）を、スクリーン空間で
    /// 一定太さに展開する太線バッチ（`GpuGizmoBatch` の太線部）へ変換する。
    ///
    /// 選択強調などで「同じ線をそのまま太く」描きたい場合に使う。各セグメントを
    /// `GizmoBatch::add_thick_line` と同一の 6 頂点（2 三角形クワッド）へ展開し、
    /// 実際の太さ付与は `gizmo_line.wgsl` の頂点シェーダ（スクリーン空間 quad 展開）が行う。
    /// 描画は `draw_thick_line_batch`（深度 LessEqual・1px ラインと同じ遮蔽）で行う。
    ///
    /// 端数（奇数頂点）は無視する（LineBatch は常にペアで push されるため通常は発生しない）。
    pub fn build_thick(&self, device: &wgpu::Device) -> GpuGizmoBatch {
        let mut line_verts: Vec<GizmoVertex> = Vec::with_capacity(self.vertices.len() * 3);
        for seg in self.vertices.chunks_exact(2) {
            let pos_a = seg[0].position;
            let pos_b = seg[1].position;
            // 色はセグメント始点の色を採用する（LineBatch は 1 セグメント同色で push する）。
            let color = seg[0].color;
            // add_thick_line と同一の頂点並び（2 三角形 = 1 クワッド）。
            let v = |t: f32, side: f32| GizmoVertex { pos_a, t, pos_b, side, color };
            line_verts.extend_from_slice(&[
                v(0.0, -1.0), v(0.0,  1.0), v(1.0, -1.0),
                v(1.0, -1.0), v(0.0,  1.0), v(1.0,  1.0),
            ]);
        }
        // ソリッド三角形は持たない（太線のみ）。
        GpuGizmoBatch::new(device, &line_verts, &[])
    }

    pub fn add_line(&mut self, start: [f32; 3], end: [f32; 3], color: [f32; 4]) {
        self.vertices.push(ColorVertex { position: start, color });
        self.vertices.push(ColorVertex { position: end,   color });
    }

    /// 両端点に異なる色を持つグラデーションラインを追加する。
    /// 深度フェードなど、端点ごとに alpha を変えたい場合に使用する。
    /// GPU が頂点間を線形補間するため、ライン全体で自然なグラデーションになる。
    pub fn add_line_grad(
        &mut self,
        start:     [f32; 3],
        end:       [f32; 3],
        col_start: [f32; 4],
        col_end:   [f32; 4],
    ) {
        self.vertices.push(ColorVertex { position: start, color: col_start });
        self.vertices.push(ColorVertex { position: end,   color: col_end });
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
        self.add_sphere_at(
            [sphere.center.x, sphere.center.y, sphere.center.z],
            sphere.radius,
            segments,
            color,
        );
    }

    /// 中心と半径を直接指定して球の 3 大円ワイヤーフレームを追加する。
    pub fn add_sphere_at(&mut self, center: [f32; 3], radius: f32, segments: usize, color: [f32; 4]) {
        let [cx, cy, cz] = center;
        let r = radius;
        let n = segments.max(4);
        for i in 0..n {
            let t0 = 2.0 * PI * i as f32 / n as f32;
            let t1 = 2.0 * PI * (i + 1) as f32 / n as f32;
            let (s0, c0) = t0.sin_cos();
            let (s1, c1) = t1.sin_cos();
            self.add_line(
                [cx + r*c0, cy,        cz + r*s0],
                [cx + r*c1, cy,        cz + r*s1], color,
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

    /// 緯線・経線グリッドのワイヤスフィアを追加する（地形ブラシプレビュー等）。
    ///
    /// `add_sphere_at` の 3 大円と違い、Y 軸を極とする緯線（水平リング）と
    /// 経線（極を通る縦の半円）のグリッドを描くため、球としての視認性が高い。
    ///
    /// - `center`    : ワールド空間での中心
    /// - `radius`    : 半径（m）
    /// - `meridians` : 経線本数（縦の半円。最小 3）
    /// - `parallels` : 緯線本数（水平リング。極を除く。最小 1）
    /// - `ring_segs` : 各円の分割数（滑らかさ。最小 6）
    /// - `color`     : 線色
    pub fn add_wire_sphere_latlong(
        &mut self,
        center:    [f32; 3],
        radius:    f32,
        meridians: usize,
        parallels: usize,
        ring_segs: usize,
        color:     [f32; 4],
    ) {
        let [cx, cy, cz] = center;
        let r = radius;
        let nm = meridians.max(3);
        let np = parallels.max(1);
        let ns = ring_segs.max(6);

        // ── 緯線（水平リング）: 高さ phi ごとに XZ 平面の円を描く ──
        // phi は極（±90°）を含まないよう (k / (np+1)) で内分する。
        for k in 1..=np {
            let phi = -0.5 * PI + PI * (k as f32) / (np as f32 + 1.0);
            let (sin_phi, cos_phi) = phi.sin_cos();
            let ry = cy + r * sin_phi;      // リングの高さ
            let rr = r * cos_phi;           // リングの半径
            for i in 0..ns {
                let a0 = 2.0 * PI * (i as f32) / (ns as f32);
                let a1 = 2.0 * PI * ((i + 1) as f32) / (ns as f32);
                let (s0, c0) = a0.sin_cos();
                let (s1, c1) = a1.sin_cos();
                self.add_line(
                    [cx + rr * c0, ry, cz + rr * s0],
                    [cx + rr * c1, ry, cz + rr * s1],
                    color,
                );
            }
        }

        // ── 経線（極を通る縦の半円）: 経度 theta ごとに phi を掃引する ──
        for m in 0..nm {
            let theta = 2.0 * PI * (m as f32) / (nm as f32);
            let (sin_t, cos_t) = theta.sin_cos();
            for i in 0..ns {
                let p0 = -0.5 * PI + PI * (i as f32) / (ns as f32);
                let p1 = -0.5 * PI + PI * ((i + 1) as f32) / (ns as f32);
                let (sp0, cp0) = p0.sin_cos();
                let (sp1, cp1) = p1.sin_cos();
                self.add_line(
                    [cx + r * cp0 * cos_t, cy + r * sp0, cz + r * cp0 * sin_t],
                    [cx + r * cp1 * cos_t, cy + r * sp1, cz + r * cp1 * sin_t],
                    color,
                );
            }
        }
    }

    /// OBB（有向バウンディングボックス）ワイヤーフレームを追加する。
    ///
    /// - `center`      : ワールド空間での中心座標
    /// - `rotation`    : クォータニオン [x, y, z, w]
    /// - `half_extents`: ローカル各軸の半サイズ（スケール適用済み）
    /// - `color`       : 線の色
    pub fn add_obb(
        &mut self,
        center:       [f32; 3],
        rotation:     [f32; 4],
        half_extents: [f32; 3],
        color:        [f32; 4],
    ) {
        let [cx, cy, cz] = center;
        let [hx, hy, hz] = half_extents;

        // ローカル 8 コーナーを回転後にワールド座標へ変換
        let corners: [[f32; 3]; 8] = [
            [-hx, -hy, -hz], [ hx, -hy, -hz], [ hx,  hy, -hz], [-hx,  hy, -hz],
            [-hx, -hy,  hz], [ hx, -hy,  hz], [ hx,  hy,  hz], [-hx,  hy,  hz],
        ].map(|lp| {
            let rp = rotate_quat(rotation, lp);
            [cx + rp[0], cy + rp[1], cz + rp[2]]
        });

        // 前面・後面・側面 各 4 辺 = 計 12 辺
        self.add_line(corners[0], corners[1], color); self.add_line(corners[1], corners[2], color);
        self.add_line(corners[2], corners[3], color); self.add_line(corners[3], corners[0], color);
        self.add_line(corners[4], corners[5], color); self.add_line(corners[5], corners[6], color);
        self.add_line(corners[6], corners[7], color); self.add_line(corners[7], corners[4], color);
        self.add_line(corners[0], corners[4], color); self.add_line(corners[1], corners[5], color);
        self.add_line(corners[2], corners[6], color); self.add_line(corners[3], corners[7], color);
    }

    /// カプセルワイヤーフレームを追加する。
    ///
    /// - `pos`         : カプセル中心（ワールド空間）
    /// - `rotation`    : クォータニオン [x, y, z, w]（Y 軸が長軸）
    /// - `radius`      : 半径（スケール適用済み）
    /// - `half_height` : 円筒部半高さ（スケール適用済み）
    /// - `segments`    : 円周分割数（最小 4）
    ///
    /// 描画内容: 円筒端の 2 つの円リング + 縦接続線 4 本 + 両端の半球弧（各 2 平面）
    pub fn add_capsule_wireframe(
        &mut self,
        pos:         [f32; 3],
        rotation:    [f32; 4],
        radius:      f32,
        half_height: f32,
        segments:    usize,
        color:       [f32; 4],
    ) {
        let n = segments.max(4);

        // ローカル Y 軸（長軸）をワールド空間に変換して両端球中心を求める
        let up     = rotate_quat(rotation, [0.0, half_height, 0.0]);
        let top    = [pos[0] + up[0], pos[1] + up[1], pos[2] + up[2]];
        let bottom = [pos[0] - up[0], pos[1] - up[1], pos[2] - up[2]];

        // 長軸の正規化ベクトル（半球弧の極方向として使用）
        let up_len = (up[0]*up[0] + up[1]*up[1] + up[2]*up[2]).sqrt().max(1e-6);
        let up_n = [up[0]/up_len, up[1]/up_len, up[2]/up_len];

        // 長軸に直交する 2 基底ベクトル（円リング・半球弧描画用）
        let (u, v_ax) = perp_basis(up);

        // ─ 上下の円リング（円筒端の輪）──────────────────────────
        for i in 0..n {
            let t0 = 2.0 * PI * i as f32 / n as f32;
            let t1 = 2.0 * PI * (i + 1) as f32 / n as f32;
            let (s0, c0) = t0.sin_cos();
            let (s1, c1) = t1.sin_cos();

            let p0 = [
                top[0] + radius * (u[0]*c0 + v_ax[0]*s0),
                top[1] + radius * (u[1]*c0 + v_ax[1]*s0),
                top[2] + radius * (u[2]*c0 + v_ax[2]*s0),
            ];
            let p1 = [
                top[0] + radius * (u[0]*c1 + v_ax[0]*s1),
                top[1] + radius * (u[1]*c1 + v_ax[1]*s1),
                top[2] + radius * (u[2]*c1 + v_ax[2]*s1),
            ];
            self.add_line(p0, p1, color);

            let q0 = [
                bottom[0] + radius * (u[0]*c0 + v_ax[0]*s0),
                bottom[1] + radius * (u[1]*c0 + v_ax[1]*s0),
                bottom[2] + radius * (u[2]*c0 + v_ax[2]*s0),
            ];
            let q1 = [
                bottom[0] + radius * (u[0]*c1 + v_ax[0]*s1),
                bottom[1] + radius * (u[1]*c1 + v_ax[1]*s1),
                bottom[2] + radius * (u[2]*c1 + v_ax[2]*s1),
            ];
            self.add_line(q0, q1, color);
        }

        // ─ 縦接続線（円筒の側面 4 本）──────────────────────────
        const N_VERTICAL: usize = 4;
        for i in 0..N_VERTICAL {
            let t = 2.0 * PI * i as f32 / N_VERTICAL as f32;
            let (s, c) = t.sin_cos();
            let dx = radius * (u[0]*c + v_ax[0]*s);
            let dy = radius * (u[1]*c + v_ax[1]*s);
            let dz = radius * (u[2]*c + v_ax[2]*s);
            self.add_line(
                [top[0]+dx,    top[1]+dy,    top[2]+dz],
                [bottom[0]+dx, bottom[1]+dy, bottom[2]+dz],
                color,
            );
        }

        // ─ 半球弧（u・v_ax 軸の 2 平面で各半円を描画）──────────
        // half_n: 半円の分割数（segments の半分。最低 4 セグメント）
        let half_n = (n / 2).max(4);

        // (u, up_n) 平面と (v_ax, up_n) 平面の 2 平面で弧を描く
        for &ax in &[u, v_ax] {
            for i in 0..half_n {
                let t0 = PI * i as f32 / half_n as f32;
                let t1 = PI * (i + 1) as f32 / half_n as f32;
                let (s0, c0) = t0.sin_cos();
                let (s1, c1) = t1.sin_cos();

                // 上半球弧: p(theta) = top + r*(cos(theta)*ax + sin(theta)*up_n)
                //   theta=0    → top + r*ax        (赤道)
                //   theta=PI/2 → top + r*up_n      (極)
                //   theta=PI   → top - r*ax        (赤道反対側)
                let tp0 = [
                    top[0] + radius * (c0*ax[0] + s0*up_n[0]),
                    top[1] + radius * (c0*ax[1] + s0*up_n[1]),
                    top[2] + radius * (c0*ax[2] + s0*up_n[2]),
                ];
                let tp1 = [
                    top[0] + radius * (c1*ax[0] + s1*up_n[0]),
                    top[1] + radius * (c1*ax[1] + s1*up_n[1]),
                    top[2] + radius * (c1*ax[2] + s1*up_n[2]),
                ];
                self.add_line(tp0, tp1, color);

                // 下半球弧: p(theta) = bottom + r*(cos(theta)*ax - sin(theta)*up_n)
                //   theta=0    → bottom + r*ax     (赤道)
                //   theta=PI/2 → bottom - r*up_n   (下極)
                //   theta=PI   → bottom - r*ax     (赤道反対側)
                let bp0 = [
                    bottom[0] + radius * (c0*ax[0] - s0*up_n[0]),
                    bottom[1] + radius * (c0*ax[1] - s0*up_n[1]),
                    bottom[2] + radius * (c0*ax[2] - s0*up_n[2]),
                ];
                let bp1 = [
                    bottom[0] + radius * (c1*ax[0] - s1*up_n[0]),
                    bottom[1] + radius * (c1*ax[1] - s1*up_n[1]),
                    bottom[2] + radius * (c1*ax[2] - s1*up_n[2]),
                ];
                self.add_line(bp0, bp1, color);
            }
        }
    }

    /// シリンダーワイヤーフレームを追加する（Y 軸が長軸）。
    ///
    /// - `pos`         : シリンダー中心（ワールド空間）
    /// - `rotation`    : クォータニオン [x, y, z, w]（Y 軸が長軸）
    /// - `radius`      : 半径（スケール適用済み）
    /// - `half_height` : 半高さ（スケール適用済み）
    /// - `segments`    : 円周分割数（最小 4）
    ///
    /// 描画内容: 上下 2 つの円リング + 縦接続線 4 本
    pub fn add_cylinder_wireframe(
        &mut self,
        pos:         [f32; 3],
        rotation:    [f32; 4],
        radius:      f32,
        half_height: f32,
        segments:    usize,
        color:       [f32; 4],
    ) {
        let n = segments.max(4);

        let up     = rotate_quat(rotation, [0.0, half_height, 0.0]);
        let top    = [pos[0] + up[0], pos[1] + up[1], pos[2] + up[2]];
        let bottom = [pos[0] - up[0], pos[1] - up[1], pos[2] - up[2]];
        let (u, v_ax) = perp_basis(up);

        // 上下の円リング
        for i in 0..n {
            let t0 = 2.0 * PI * i as f32 / n as f32;
            let t1 = 2.0 * PI * (i + 1) as f32 / n as f32;
            let (s0, c0) = t0.sin_cos();
            let (s1, c1) = t1.sin_cos();

            let p0 = [top[0]+radius*(u[0]*c0+v_ax[0]*s0), top[1]+radius*(u[1]*c0+v_ax[1]*s0), top[2]+radius*(u[2]*c0+v_ax[2]*s0)];
            let p1 = [top[0]+radius*(u[0]*c1+v_ax[0]*s1), top[1]+radius*(u[1]*c1+v_ax[1]*s1), top[2]+radius*(u[2]*c1+v_ax[2]*s1)];
            self.add_line(p0, p1, color);

            let q0 = [bottom[0]+radius*(u[0]*c0+v_ax[0]*s0), bottom[1]+radius*(u[1]*c0+v_ax[1]*s0), bottom[2]+radius*(u[2]*c0+v_ax[2]*s0)];
            let q1 = [bottom[0]+radius*(u[0]*c1+v_ax[0]*s1), bottom[1]+radius*(u[1]*c1+v_ax[1]*s1), bottom[2]+radius*(u[2]*c1+v_ax[2]*s1)];
            self.add_line(q0, q1, color);
        }

        // 縦接続線（4 本）
        const N_VERT_CYL: usize = 4;
        for i in 0..N_VERT_CYL {
            let t = 2.0 * PI * i as f32 / N_VERT_CYL as f32;
            let (s, c) = t.sin_cos();
            let dx = radius * (u[0]*c + v_ax[0]*s);
            let dy = radius * (u[1]*c + v_ax[1]*s);
            let dz = radius * (u[2]*c + v_ax[2]*s);
            self.add_line(
                [top[0]+dx,    top[1]+dy,    top[2]+dz],
                [bottom[0]+dx, bottom[1]+dy, bottom[2]+dz],
                color,
            );
        }
    }

    /// コーンワイヤーフレームを追加する（Y 軸が長軸、頂点が +Y 側）。
    ///
    /// - `pos`         : コーン中心（ワールド空間）
    /// - `rotation`    : クォータニオン [x, y, z, w]（Y 軸が長軸）
    /// - `radius`      : 底面半径（スケール適用済み）
    /// - `half_height` : 半高さ（スケール適用済み）
    /// - `segments`    : 円周分割数（最小 4）
    ///
    /// 描画内容: 底面の円リング + 頂点から底面への接続線 8 本
    pub fn add_cone_wireframe(
        &mut self,
        pos:         [f32; 3],
        rotation:    [f32; 4],
        radius:      f32,
        half_height: f32,
        segments:    usize,
        color:       [f32; 4],
    ) {
        let n = segments.max(4);

        let up     = rotate_quat(rotation, [0.0, half_height, 0.0]);
        let apex   = [pos[0] + up[0], pos[1] + up[1], pos[2] + up[2]];
        let base_c = [pos[0] - up[0], pos[1] - up[1], pos[2] - up[2]];
        let (u, v_ax) = perp_basis(up);

        // 底面の円リング
        for i in 0..n {
            let t0 = 2.0 * PI * i as f32 / n as f32;
            let t1 = 2.0 * PI * (i + 1) as f32 / n as f32;
            let (s0, c0) = t0.sin_cos();
            let (s1, c1) = t1.sin_cos();

            let p0 = [base_c[0]+radius*(u[0]*c0+v_ax[0]*s0), base_c[1]+radius*(u[1]*c0+v_ax[1]*s0), base_c[2]+radius*(u[2]*c0+v_ax[2]*s0)];
            let p1 = [base_c[0]+radius*(u[0]*c1+v_ax[0]*s1), base_c[1]+radius*(u[1]*c1+v_ax[1]*s1), base_c[2]+radius*(u[2]*c1+v_ax[2]*s1)];
            self.add_line(p0, p1, color);
        }

        // 頂点から底面への接続線（8 本）
        const N_SIDE_CONE: usize = 8;
        for i in 0..N_SIDE_CONE {
            let t = 2.0 * PI * i as f32 / N_SIDE_CONE as f32;
            let (s, c) = t.sin_cos();
            let bp = [
                base_c[0] + radius * (u[0]*c + v_ax[0]*s),
                base_c[1] + radius * (u[1]*c + v_ax[1]*s),
                base_c[2] + radius * (u[2]*c + v_ax[2]*s),
            ];
            self.add_line(apex, bp, color);
        }
    }

    /// ConvexHull ワイヤーフレームを追加する。
    ///
    /// `vertices` はワールド空間での頂点リスト。
    /// 全頂点ペアを線で繋ぐことで凸形状の稜線を可視化する。
    pub fn add_convex_hull_wireframe(
        &mut self,
        vertices: &[[f32; 3]],
        color:    [f32; 4],
    ) {
        let n = vertices.len();
        for i in 0..n {
            for j in (i + 1)..n {
                self.add_line(vertices[i], vertices[j], color);
            }
        }
    }

    /// TriangleMesh ワイヤーフレームを追加する（各三角形の 3 辺を描画）。
    ///
    /// `triangles` はワールド空間での三角形リスト（各要素は [a, b, c]）。
    pub fn add_triangle_mesh_wireframe(
        &mut self,
        triangles: &[[[f32; 3]; 3]],
        color:     [f32; 4],
    ) {
        for tri in triangles {
            self.add_line(tri[0], tri[1], color);
            self.add_line(tri[1], tri[2], color);
            self.add_line(tri[2], tri[0], color);
        }
    }

    // ── 2D キャンバス専用ワイヤーフレーム ────────────────────────────────────

    /// 2D ボックスワイヤーフレームを XY 平面に追加する（Z=depth）。
    ///
    /// - `center`      : 中心座標 [x, y] (canvas 単位)
    /// - `rotation_rad`: Z 軸周り回転（ラジアン）
    /// - `half_extents`: ローカル半サイズ [hx, hy] (canvas 単位)
    /// - `depth`       : Z 座標（前後ソート用、通常 0.0）
    /// - `color`       : 線の色
    pub fn add_box_2d(
        &mut self,
        center:       [f32; 2],
        rotation_rad: f32,
        half_extents: [f32; 2],
        depth:        f32,
        color:        [f32; 4],
    ) {
        let (sin, cos) = rotation_rad.sin_cos();
        let [cx, cy] = center;
        let [hx, hy] = half_extents;

        // ローカル 4 コーナーを回転してワールドへ変換
        let corners = [
            [-hx, -hy], [ hx, -hy], [ hx,  hy], [-hx,  hy],
        ].map(|[lx, ly]| {
            [cx + cos * lx - sin * ly, cy + sin * lx + cos * ly, depth]
        });

        // 4 辺を描画する
        self.add_line(corners[0], corners[1], color);
        self.add_line(corners[1], corners[2], color);
        self.add_line(corners[2], corners[3], color);
        self.add_line(corners[3], corners[0], color);
    }

    /// 2D 円ワイヤーフレームを XY 平面に追加する（Z=depth）。
    ///
    /// - `center`  : 中心座標 [x, y] (canvas 単位)
    /// - `radius`  : 半径 (canvas 単位)
    /// - `segments`: 分割数（最小 8）
    /// - `depth`   : Z 座標
    /// - `color`   : 線の色
    pub fn add_circle_2d(
        &mut self,
        center:   [f32; 2],
        radius:   f32,
        segments: usize,
        depth:    f32,
        color:    [f32; 4],
    ) {
        let [cx, cy] = center;
        let n = segments.max(8);
        for i in 0..n {
            let t0 = 2.0 * PI * i as f32 / n as f32;
            let t1 = 2.0 * PI * (i + 1) as f32 / n as f32;
            self.add_line(
                [cx + radius * t0.cos(), cy + radius * t0.sin(), depth],
                [cx + radius * t1.cos(), cy + radius * t1.sin(), depth],
                color,
            );
        }
    }

    /// 2D カプセルワイヤーフレームを XY 平面に追加する（Z=depth）。
    ///
    /// - `center`      : 中心座標 [x, y] (canvas 単位)
    /// - `rotation_rad`: Z 軸周り回転（ラジアン）。長軸が Y 方向。
    /// - `radius`      : 半径 (canvas 単位)
    /// - `half_height` : 円筒部の半高さ (canvas 単位)
    /// - `segments`    : 半円の分割数（最小 8）
    /// - `depth`       : Z 座標
    /// - `color`       : 線の色
    pub fn add_capsule_2d(
        &mut self,
        center:       [f32; 2],
        rotation_rad: f32,
        radius:       f32,
        half_height:  f32,
        segments:     usize,
        depth:        f32,
        color:        [f32; 4],
    ) {
        let [cx, cy] = center;
        let (sin, cos) = rotation_rad.sin_cos();
        let n = (segments.max(8) / 2).max(4);

        // ローカル座標をワールドに変換するクロージャ
        let to_world = |lx: f32, ly: f32| -> [f32; 3] {
            [cx + cos * lx - sin * ly, cy + sin * lx + cos * ly, depth]
        };

        // 上端・下端の接線線（両脇 2 本）
        self.add_line(to_world(-radius,  half_height), to_world(-radius, -half_height), color);
        self.add_line(to_world( radius,  half_height), to_world( radius, -half_height), color);

        // 上端半円（Y+方向）
        for i in 0..n {
            let t0 = PI * i as f32 / n as f32;           // 0..PI
            let t1 = PI * (i + 1) as f32 / n as f32;
            self.add_line(
                to_world(-radius * t0.sin(),  half_height + radius * t0.cos()),
                to_world(-radius * t1.sin(),  half_height + radius * t1.cos()),
                color,
            );
        }

        // 下端半円（Y-方向）
        for i in 0..n {
            let t0 = PI * i as f32 / n as f32;
            let t1 = PI * (i + 1) as f32 / n as f32;
            self.add_line(
                to_world( radius * t0.sin(), -half_height - radius * t0.cos()),
                to_world( radius * t1.sin(), -half_height - radius * t1.cos()),
                color,
            );
        }
    }

    /// 2D 凸包ワイヤーフレームを XY 平面に追加する（Z=depth）。
    ///
    /// - `vertices`: 頂点リスト [x, y] (canvas 単位)
    /// - `depth`   : Z 座標
    /// - `color`   : 線の色
    pub fn add_convex_hull_2d(
        &mut self,
        vertices: &[[f32; 2]],
        depth:    f32,
        color:    [f32; 4],
    ) {
        let n = vertices.len();
        if n < 2 { return; }
        for i in 0..n {
            let a = vertices[i];
            let b = vertices[(i + 1) % n];
            self.add_line([a[0], a[1], depth], [b[0], b[1], depth], color);
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

    /// ライン・トライアングル頂点がどちらも空かどうかを返す。
    pub fn is_empty(&self) -> bool {
        self.line_verts.is_empty() && self.tri_verts.is_empty()
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

    /// ソリッド三角形 1 枚を追加する（外部モジュールのカメラアイコン等に使用）。
    pub fn add_solid_tri(&mut self, a: [f32; 3], b: [f32; 3], c: [f32; 3], color: Color) {
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

    /// XY 平面のハンドル矩形を追加する（移動・スケール共通）。
    /// 3D の 3 平面ハンドルと、2D ギズモの XY 平面ハンドルの両方から使用する。
    fn add_plane_handle_xy(&mut self, pos: [f32; 3], radius: f32, hovered: Option<GizmoPart>) {
        let [px, py, pz] = pos;
        let o = radius * 0.5;
        let s = radius * 0.075;
        let (fxy, cxy) = if hovered == Some(GizmoPart::PlaneXY) {
            (highlight_fill(GZ_FILL), highlight(GZ))
        } else { (GZ_FILL, GZ) };
        // XY 平面（Z 軸色 = 青）
        self.add_plane_quad(
            [px+o-s, py+o-s, pz], [px+o+s, py+o-s, pz],
            [px+o+s, py+o+s, pz], [px+o-s, py+o+s, pz],
            fxy, cxy,
        );
    }

    /// XY / XZ / YZ 3 平面のハンドル矩形を追加する（移動・スケール共通）。
    fn add_plane_handles(&mut self, pos: [f32; 3], radius: f32, hovered: Option<GizmoPart>) {
        let [px, py, pz] = pos;
        let o = radius * 0.5;
        let s = radius * 0.075;

        let (fxz, cxz) = if hovered == Some(GizmoPart::PlaneXZ) {
            (highlight_fill(GY_FILL), highlight(GY))
        } else { (GY_FILL, GY) };
        let (fyz, cyz) = if hovered == Some(GizmoPart::PlaneYZ) {
            (highlight_fill(GX_FILL), highlight(GX))
        } else { (GX_FILL, GX) };

        // XY 平面（Z 軸色 = 青）
        self.add_plane_handle_xy(pos, radius, hovered);
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

    /// UV スフィアをソリッド三角形で追加する。
    ///
    /// - `stacks`: 緯度方向の分割数（最小 2）
    /// - `slices`: 経度方向の分割数（最小 3）
    pub fn add_solid_sphere(
        &mut self,
        center: [f32; 3],
        radius: f32,
        stacks: usize,
        slices: usize,
        color:  Color,
    ) {
        let stacks = stacks.max(2);
        let slices = slices.max(3);

        // (stack, slice) インデックスから球面座標→ワールド座標を返すクロージャ
        let vert = |s: usize, i: usize| -> [f32; 3] {
            let phi   = PI * s as f32 / stacks as f32;
            let theta = 2.0 * PI * i as f32 / slices as f32;
            let (sp, cp) = phi.sin_cos();
            let (st, ct) = theta.sin_cos();
            [
                center[0] + radius * sp * ct,
                center[1] + radius * cp,
                center[2] + radius * sp * st,
            ]
        };

        for s in 0..stacks {
            for i in 0..slices {
                let v00 = vert(s,     i);
                let v01 = vert(s,     i + 1);
                let v10 = vert(s + 1, i);
                let v11 = vert(s + 1, i + 1);

                if s == 0 {
                    // 北極キャップ（v00 == 北極点、縮退三角形）
                    self.add_solid_tri(v00, v11, v10, color);
                } else if s + 1 == stacks {
                    // 南極キャップ（v10 == 南極点、縮退三角形）
                    self.add_solid_tri(v00, v01, v10, color);
                } else {
                    // 中間帯: クワッドを 2 三角形に分割
                    self.add_solid_tri(v00, v01, v10, color);
                    self.add_solid_tri(v01, v11, v10, color);
                }
            }
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

    // ── 2D キャンバスモード用ギズモ ───────────────────────────────

    /// 2D 移動ギズモ（X・Y 軸 + XY 平面ハンドル + Center ハンドル）を追加する。
    /// Z 軸・XZ/YZ 平面ハンドルは 2D では不要なため描画しない。
    pub fn add_gizmo_translate_2d(&mut self, pos: [f32; 3], radius: f32, hovered: Option<GizmoPart>) {
        let [px, py, pz] = pos;
        let head_len = radius * 0.25;
        let head_r   = radius * 0.07;

        let cx = if hovered == Some(GizmoPart::AxisX) { highlight(GX) } else { GX };
        let cy = if hovered == Some(GizmoPart::AxisY) { highlight(GY) } else { GY };

        // X 軸（赤）
        let shaft_tip = [px + radius - head_len, py, pz];
        self.add_thick_line(pos, shaft_tip, cx);
        self.add_cone([px + radius, py, pz], shaft_tip, head_r, 8, cx);

        // Y 軸（緑）
        let shaft_tip = [px, py + radius - head_len, pz];
        self.add_thick_line(pos, shaft_tip, cy);
        self.add_cone([px, py + radius, pz], shaft_tip, head_r, 8, cy);

        // XY 平面ハンドル（青）: 2D でも XY 同時移動に使用する（3D ギズモと共通の見た目）
        self.add_plane_handle_xy(pos, radius, hovered);

        // 中心ハンドル
        let cc = if hovered == Some(GizmoPart::Center) { Color::YELLOW } else { Color::WHITE };
        self.add_center_dot(pos, radius * 0.055, cc);
    }

    /// 2D スケールギズモ（X・Y 軸 + XY 平面ハンドル + Center ハンドル）を追加する。
    pub fn add_gizmo_scale_2d(&mut self, pos: [f32; 3], radius: f32, hovered: Option<GizmoPart>) {
        let [px, py, pz] = pos;
        let cube_half = radius * 0.07;

        let cx = if hovered == Some(GizmoPart::AxisX) { highlight(GX) } else { GX };
        let cy = if hovered == Some(GizmoPart::AxisY) { highlight(GY) } else { GY };

        // X 軸（赤）
        let xe = [px + radius, py, pz];
        self.add_thick_line(pos, xe, cx);
        self.add_solid_cube(xe, cube_half, cx);

        // Y 軸（緑）
        let ye = [px, py + radius, pz];
        self.add_thick_line(pos, ye, cy);
        self.add_solid_cube(ye, cube_half, cy);

        // XY 平面ハンドル（青）: 2D でも XY 均一スケールに使用する（3D ギズモと共通の見た目）
        self.add_plane_handle_xy(pos, radius, hovered);

        // 中心ハンドル
        let cc = if hovered == Some(GizmoPart::Center) { Color::YELLOW } else { Color::WHITE };
        self.add_center_dot(pos, radius * 0.055, cc);
    }

    // ── 3D Canvas 子アクター向け oriented ギズモ ────────────────────

    /// キャンバス座標系に合わせた移動ギズモ（X・Y 軸 + XY 平面ハンドル）を追加する。
    ///
    /// - ax: キャンバス X 軸（ワールド空間単位ベクトル）= canvas 右方向
    /// - ay: キャンバス Y 軸（ワールド空間単位ベクトル）= canvas 下方向
    pub fn add_gizmo_translate_canvas(
        &mut self,
        pos:     [f32; 3],
        radius:  f32,
        hovered: Option<GizmoPart>,
        ax:      [f32; 3],
        ay:      [f32; 3],
    ) {
        let head_len = radius * 0.25;
        let head_r   = radius * 0.07;
        let cx = if hovered == Some(GizmoPart::AxisX)   { highlight(GX) } else { GX };
        let cy = if hovered == Some(GizmoPart::AxisY)   { highlight(GY) } else { GY };
        let cz = if hovered == Some(GizmoPart::PlaneXY) { highlight(GZ) } else { GZ };
        let cf = if hovered == Some(GizmoPart::PlaneXY) { highlight_fill(GZ_FILL) } else { GZ_FILL };
        // 各軸方向のワールド位置計算ヘルパー
        let tip = |dir: [f32; 3], t: f32| -> [f32; 3] {
            [pos[0]+dir[0]*t, pos[1]+dir[1]*t, pos[2]+dir[2]*t]
        };
        // X 軸矢印（赤 = canvas right）
        self.add_thick_line(pos, tip(ax, radius - head_len), cx);
        self.add_cone(tip(ax, radius), tip(ax, radius - head_len), head_r, 8, cx);
        // Y 軸矢印（緑 = canvas down）
        self.add_thick_line(pos, tip(ay, radius - head_len), cy);
        self.add_cone(tip(ay, radius), tip(ay, radius - head_len), head_r, 8, cy);
        // XY 平面ハンドル（青 = canvas 平面、法線 = az = ax×ay）
        let o = radius * 0.5;
        let s = radius * 0.075;
        let cx_ax = |t: f32| [pos[0]+ax[0]*t, pos[1]+ax[1]*t, pos[2]+ax[2]*t];
        let cy_ay = |t: f32| [pos[0]+ay[0]*t, pos[1]+ay[1]*t, pos[2]+ay[2]*t];
        let center = [pos[0]+ax[0]*o+ay[0]*o, pos[1]+ax[1]*o+ay[1]*o, pos[2]+ax[2]*o+ay[2]*o];
        let a = [center[0]-ax[0]*s-ay[0]*s, center[1]-ax[1]*s-ay[1]*s, center[2]-ax[2]*s-ay[2]*s];
        let b = [center[0]+ax[0]*s-ay[0]*s, center[1]+ax[1]*s-ay[1]*s, center[2]+ax[2]*s-ay[2]*s];
        let c = [center[0]+ax[0]*s+ay[0]*s, center[1]+ax[1]*s+ay[1]*s, center[2]+ax[2]*s+ay[2]*s];
        let d = [center[0]-ax[0]*s+ay[0]*s, center[1]-ax[1]*s+ay[1]*s, center[2]-ax[2]*s+ay[2]*s];
        let _ = (cx_ax, cy_ay); // unused warning suppression
        self.add_plane_quad(a, b, c, d, cf, cz);
        // 中心ハンドル
        let cc = if hovered == Some(GizmoPart::Center) { Color::YELLOW } else { Color::WHITE };
        self.add_center_dot(pos, radius * 0.055, cc);
    }

    /// キャンバス座標系に合わせたスケールギズモ（X・Y 軸 + XY 平面ハンドル）を追加する。
    pub fn add_gizmo_scale_canvas(
        &mut self,
        pos:     [f32; 3],
        radius:  f32,
        hovered: Option<GizmoPart>,
        ax:      [f32; 3],
        ay:      [f32; 3],
    ) {
        let cube_half = radius * 0.07;
        let cx = if hovered == Some(GizmoPart::AxisX)   { highlight(GX) } else { GX };
        let cy = if hovered == Some(GizmoPart::AxisY)   { highlight(GY) } else { GY };
        let cz = if hovered == Some(GizmoPart::PlaneXY) { highlight(GZ) } else { GZ };
        let cf = if hovered == Some(GizmoPart::PlaneXY) { highlight_fill(GZ_FILL) } else { GZ_FILL };
        let tip = |dir: [f32; 3], t: f32| -> [f32; 3] {
            [pos[0]+dir[0]*t, pos[1]+dir[1]*t, pos[2]+dir[2]*t]
        };
        // X 軸（赤）
        let xe = tip(ax, radius);
        self.add_thick_line(pos, xe, cx);
        self.add_solid_cube(xe, cube_half, cx);
        // Y 軸（緑）
        let ye = tip(ay, radius);
        self.add_thick_line(pos, ye, cy);
        self.add_solid_cube(ye, cube_half, cy);
        // XY 平面ハンドル（青）
        let o = radius * 0.5;
        let s = radius * 0.075;
        let center = [pos[0]+ax[0]*o+ay[0]*o, pos[1]+ax[1]*o+ay[1]*o, pos[2]+ax[2]*o+ay[2]*o];
        let a = [center[0]-ax[0]*s-ay[0]*s, center[1]-ax[1]*s-ay[1]*s, center[2]-ax[2]*s-ay[2]*s];
        let b = [center[0]+ax[0]*s-ay[0]*s, center[1]+ax[1]*s-ay[1]*s, center[2]+ax[2]*s-ay[2]*s];
        let c = [center[0]+ax[0]*s+ay[0]*s, center[1]+ax[1]*s+ay[1]*s, center[2]+ax[2]*s+ay[2]*s];
        let d = [center[0]-ax[0]*s+ay[0]*s, center[1]-ax[1]*s+ay[1]*s, center[2]-ax[2]*s+ay[2]*s];
        self.add_plane_quad(a, b, c, d, cf, cz);
        // 中心ハンドル
        let cc = if hovered == Some(GizmoPart::Center) { Color::YELLOW } else { Color::WHITE };
        self.add_center_dot(pos, radius * 0.055, cc);
    }

    /// キャンバス座標系に合わせた回転ギズモ（canvas 法線周りのリングのみ）を追加する。
    ///
    /// - az: キャンバス法線軸（AxisZ に対応）= ax×ay
    /// - ax/ay: リング構築用のキャンバス X/Y 軸
    /// カメラ向き判定で手前半円のみ表示（drag 中は全周）。
    pub fn add_gizmo_rotate_canvas(
        &mut self,
        pos:      [f32; 3],
        radius:   f32,
        segments: usize,
        cam_pos:  [f32; 3],
        hovered:  Option<GizmoPart>,
        dragging: Option<GizmoPart>,
        az:       [f32; 3],
        ax:       [f32; 3],
        ay:       [f32; 3],
    ) {
        let cz = if hovered == Some(GizmoPart::AxisZ) { highlight(GZ) } else { GZ };
        // カメラ→gizmo_pos の正規化ベクトル（半円フィルタ用）
        let cd = {
            let d = [cam_pos[0]-pos[0], cam_pos[1]-pos[1], cam_pos[2]-pos[2]];
            let len = (d[0]*d[0]+d[1]*d[1]+d[2]*d[2]).sqrt().max(1e-6);
            [d[0]/len, d[1]/len, d[2]/len]
        };
        let n = segments.max(4);
        for i in 0..n {
            let t0 = 2.0 * std::f32::consts::PI * i as f32 / n as f32;
            let t1 = 2.0 * std::f32::consts::PI * (i + 1) as f32 / n as f32;
            let tm = (t0 + t1) * 0.5;
            let (s0, c0) = t0.sin_cos();
            let (s1, c1) = t1.sin_cos();
            let (sm, cm) = tm.sin_cos();
            // リング上の点（キャンバス平面内の円）
            let p0 = [pos[0]+radius*(ax[0]*c0+ay[0]*s0),
                      pos[1]+radius*(ax[1]*c0+ay[1]*s0),
                      pos[2]+radius*(ax[2]*c0+ay[2]*s0)];
            let p1 = [pos[0]+radius*(ax[0]*c1+ay[0]*s1),
                      pos[1]+radius*(ax[1]*c1+ay[1]*s1),
                      pos[2]+radius*(ax[2]*c1+ay[2]*s1)];
            // 中点の径方向（カメラ向き判定）
            let mid_rad = [ax[0]*cm+ay[0]*sm, ax[1]*cm+ay[1]*sm, ax[2]*cm+ay[2]*sm];
            let show = match dragging {
                Some(GizmoPart::AxisZ) => true,
                Some(_) => false,
                None => mid_rad[0]*cd[0]+mid_rad[1]*cd[1]+mid_rad[2]*cd[2] > 0.0,
            };
            if show { self.add_thick_line(p0, p1, cz); }
        }
    }

    // ── Local 座標モード向け oriented ギズモ（通常 3D アクター・全 3 軸）────────

    /// オブジェクトのローカル回転軸に沿った移動ギズモ（3 軸矢印 + 3 平面ハンドル）を追加する。
    /// add_gizmo_translate のワールド軸 [1,0,0]/[0,1,0]/[0,0,1] を任意の正規直交基底
    /// ax/ay/az に置き換えた汎用版。gizmo_space = Local の通常 3D アクターに使用する。
    pub fn add_gizmo_translate_local(
        &mut self,
        pos:     [f32; 3],
        radius:  f32,
        hovered: Option<GizmoPart>,
        ax:      [f32; 3],
        ay:      [f32; 3],
        az:      [f32; 3],
    ) {
        let head_len = radius * 0.25;
        let head_r   = radius * 0.07;
        let cx = if hovered == Some(GizmoPart::AxisX) { highlight(GX) } else { GX };
        let cy = if hovered == Some(GizmoPart::AxisY) { highlight(GY) } else { GY };
        let cz = if hovered == Some(GizmoPart::AxisZ) { highlight(GZ) } else { GZ };
        let tip = |dir: [f32; 3], t: f32| -> [f32; 3] {
            [pos[0]+dir[0]*t, pos[1]+dir[1]*t, pos[2]+dir[2]*t]
        };
        // X 軸（赤）
        self.add_thick_line(pos, tip(ax, radius - head_len), cx);
        self.add_cone(tip(ax, radius), tip(ax, radius - head_len), head_r, 8, cx);
        // Y 軸（緑）
        self.add_thick_line(pos, tip(ay, radius - head_len), cy);
        self.add_cone(tip(ay, radius), tip(ay, radius - head_len), head_r, 8, cy);
        // Z 軸（青）
        self.add_thick_line(pos, tip(az, radius - head_len), cz);
        self.add_cone(tip(az, radius), tip(az, radius - head_len), head_r, 8, cz);
        // XY / XZ / YZ 平面ハンドル
        self.add_plane_handles_local(pos, radius, hovered, ax, ay, az);
        // 中心ハンドル
        let cc = if hovered == Some(GizmoPart::Center) { Color::YELLOW } else { Color::WHITE };
        self.add_center_dot(pos, radius * 0.055, cc);
    }

    /// オブジェクトのローカル回転軸に沿ったスケールギズモ（3 軸線 + 端点キューブ）を追加する。
    pub fn add_gizmo_scale_local(
        &mut self,
        pos:     [f32; 3],
        radius:  f32,
        hovered: Option<GizmoPart>,
        ax:      [f32; 3],
        ay:      [f32; 3],
        az:      [f32; 3],
    ) {
        let cube_half = radius * 0.07;
        let cx = if hovered == Some(GizmoPart::AxisX) { highlight(GX) } else { GX };
        let cy = if hovered == Some(GizmoPart::AxisY) { highlight(GY) } else { GY };
        let cz = if hovered == Some(GizmoPart::AxisZ) { highlight(GZ) } else { GZ };
        let tip = |dir: [f32; 3], t: f32| -> [f32; 3] {
            [pos[0]+dir[0]*t, pos[1]+dir[1]*t, pos[2]+dir[2]*t]
        };
        // X 軸（赤）
        let xe = tip(ax, radius);
        self.add_thick_line(pos, xe, cx);
        self.add_solid_cube(xe, cube_half, cx);
        // Y 軸（緑）
        let ye = tip(ay, radius);
        self.add_thick_line(pos, ye, cy);
        self.add_solid_cube(ye, cube_half, cy);
        // Z 軸（青）
        let ze = tip(az, radius);
        self.add_thick_line(pos, ze, cz);
        self.add_solid_cube(ze, cube_half, cz);
        // XY / XZ / YZ 平面ハンドル
        self.add_plane_handles_local(pos, radius, hovered, ax, ay, az);
        // 中心ハンドル
        let cc = if hovered == Some(GizmoPart::Center) { Color::YELLOW } else { Color::WHITE };
        self.add_center_dot(pos, radius * 0.055, cc);
    }

    /// XY / XZ / YZ 3 平面のハンドル矩形をローカル軸 ax/ay/az に沿って追加する
    /// （add_plane_handles のワールド軸版を汎用化したもの）。
    fn add_plane_handles_local(
        &mut self,
        pos:     [f32; 3],
        radius:  f32,
        hovered: Option<GizmoPart>,
        ax:      [f32; 3],
        ay:      [f32; 3],
        az:      [f32; 3],
    ) {
        let o = radius * 0.5;
        let s = radius * 0.075;
        let quad = |u: [f32; 3], v: [f32; 3]| -> [[f32; 3]; 4] {
            let center = [pos[0]+u[0]*o+v[0]*o, pos[1]+u[1]*o+v[1]*o, pos[2]+u[2]*o+v[2]*o];
            [
                [center[0]-u[0]*s-v[0]*s, center[1]-u[1]*s-v[1]*s, center[2]-u[2]*s-v[2]*s],
                [center[0]+u[0]*s-v[0]*s, center[1]+u[1]*s-v[1]*s, center[2]+u[2]*s-v[2]*s],
                [center[0]+u[0]*s+v[0]*s, center[1]+u[1]*s+v[1]*s, center[2]+u[2]*s+v[2]*s],
                [center[0]-u[0]*s+v[0]*s, center[1]-u[1]*s+v[1]*s, center[2]-u[2]*s+v[2]*s],
            ]
        };
        let (fxy, cxy) = if hovered == Some(GizmoPart::PlaneXY) { (highlight_fill(GZ_FILL), highlight(GZ)) } else { (GZ_FILL, GZ) };
        let (fxz, cxz) = if hovered == Some(GizmoPart::PlaneXZ) { (highlight_fill(GY_FILL), highlight(GY)) } else { (GY_FILL, GY) };
        let (fyz, cyz) = if hovered == Some(GizmoPart::PlaneYZ) { (highlight_fill(GX_FILL), highlight(GX)) } else { (GX_FILL, GX) };
        let [a, b, c, d] = quad(ax, ay);
        self.add_plane_quad(a, b, c, d, fxy, cxy);
        let [a, b, c, d] = quad(ax, az);
        self.add_plane_quad(a, b, c, d, fxz, cxz);
        let [a, b, c, d] = quad(ay, az);
        self.add_plane_quad(a, b, c, d, fyz, cyz);
    }

    /// オブジェクトのローカル回転軸に沿った回転ギズモ（3 軸半円リング）を追加する。
    /// add_gizmo_rotate のワールド軸版を任意の正規直交基底 ax/ay/az に汎用化したもの。
    pub fn add_gizmo_rotate_local(
        &mut self,
        pos:      [f32; 3],
        radius:   f32,
        segments: usize,
        cam_pos:  [f32; 3],
        hovered:  Option<GizmoPart>,
        dragging: Option<GizmoPart>,
        ax:       [f32; 3],
        ay:       [f32; 3],
        az:       [f32; 3],
    ) {
        let cx = if hovered == Some(GizmoPart::AxisX) { highlight(GX) } else { GX };
        let cy = if hovered == Some(GizmoPart::AxisY) { highlight(GY) } else { GY };
        let cz = if hovered == Some(GizmoPart::AxisZ) { highlight(GZ) } else { GZ };

        // オブジェクト→カメラの正規化ベクトル（半円フィルタ用）
        let cd = {
            let d = [cam_pos[0]-pos[0], cam_pos[1]-pos[1], cam_pos[2]-pos[2]];
            let len = (d[0]*d[0]+d[1]*d[1]+d[2]*d[2]).sqrt().max(1e-6);
            [d[0]/len, d[1]/len, d[2]/len]
        };
        // リング上の点（軸ペア u,v が張る平面内の円）を返すヘルパー
        let ring_pt = |u: [f32; 3], v: [f32; 3], t: f32| -> [f32; 3] {
            let (s, c) = t.sin_cos();
            [pos[0]+radius*(u[0]*c+v[0]*s), pos[1]+radius*(u[1]*c+v[1]*s), pos[2]+radius*(u[2]*c+v[2]*s)]
        };

        let n = segments.max(4);
        for i in 0..n {
            let t0 = 2.0 * PI * i as f32 / n as f32;
            let t1 = 2.0 * PI * (i + 1) as f32 / n as f32;
            let tm = (t0 + t1) * 0.5;

            // X 軸リング（YZ 平面 = ay, az が張る円。法線 = ax）
            let mid_x = [ay[0]*tm.cos()+az[0]*tm.sin(), ay[1]*tm.cos()+az[1]*tm.sin(), ay[2]*tm.cos()+az[2]*tm.sin()];
            let show_x = match dragging {
                Some(GizmoPart::AxisX) => true,
                Some(_) => false,
                None => dot3_local(mid_x, cd) > 0.0,
            };
            if show_x {
                self.add_thick_line(ring_pt(ay, az, t0), ring_pt(ay, az, t1), cx);
            }

            // Y 軸リング（XZ 平面 = ax, az。法線 = ay）
            let mid_y = [ax[0]*tm.cos()+az[0]*tm.sin(), ax[1]*tm.cos()+az[1]*tm.sin(), ax[2]*tm.cos()+az[2]*tm.sin()];
            let show_y = match dragging {
                Some(GizmoPart::AxisY) => true,
                Some(_) => false,
                None => dot3_local(mid_y, cd) > 0.0,
            };
            if show_y {
                self.add_thick_line(ring_pt(ax, az, t0), ring_pt(ax, az, t1), cy);
            }

            // Z 軸リング（XY 平面 = ax, ay。法線 = az）
            let mid_z = [ax[0]*tm.cos()+ay[0]*tm.sin(), ax[1]*tm.cos()+ay[1]*tm.sin(), ax[2]*tm.cos()+ay[2]*tm.sin()];
            let show_z = match dragging {
                Some(GizmoPart::AxisZ) => true,
                Some(_) => false,
                None => dot3_local(mid_z, cd) > 0.0,
            };
            if show_z {
                self.add_thick_line(ring_pt(ax, ay, t0), ring_pt(ax, ay, t1), cz);
            }
        }
    }

    /// 2D 回転ギズモ（Z 軸周りの完全な円）を追加する。
    /// 3D の半円表示とは異なり、カメラ真上から見下ろすため常に全周を表示する。
    pub fn add_gizmo_rotate_2d(
        &mut self,
        pos:      [f32; 3],
        radius:   f32,
        segments: usize,
        hovered:  Option<GizmoPart>,
    ) {
        let [px, py, pz] = pos;
        let cz = if hovered == Some(GizmoPart::AxisZ) { highlight(GZ) } else { GZ };

        let n = segments.max(4);
        for i in 0..n {
            let t0 = 2.0 * PI * i as f32 / n as f32;
            let t1 = 2.0 * PI * (i + 1) as f32 / n as f32;
            let (s0, c0) = t0.sin_cos();
            let (s1, c1) = t1.sin_cos();
            // Z 軸リング（XY 平面上の完全な円）
            self.add_thick_line(
                [px + radius*c0, py + radius*s0, pz],
                [px + radius*c1, py + radius*s1, pz],
                cz,
            );
        }
    }
}

// ── 数学ヘルパー ───────────────────────────────────────────────

/// クォータニオン q = [x, y, z, w] でベクトル v を回転させる（Rodrigues の公式）。
///
/// OBB・カプセルワイヤーフレームのコーナー・端点計算に使用する。
fn rotate_quat(q: [f32; 4], v: [f32; 3]) -> [f32; 3] {
    let (qx, qy, qz, qw) = (q[0], q[1], q[2], q[3]);
    let (vx, vy, vz)     = (v[0], v[1], v[2]);
    // v' = 2(u·v)u + (w²-|u|²)v + 2w(u×v)
    let dot_uv = qx*vx + qy*vy + qz*vz;
    let dot_uu = qx*qx + qy*qy + qz*qz;
    let cx = qy*vz - qz*vy;
    let cy = qz*vx - qx*vz;
    let cz = qx*vy - qy*vx;
    [
        2.0*dot_uv*qx + (qw*qw - dot_uu)*vx + 2.0*qw*cx,
        2.0*dot_uv*qy + (qw*qw - dot_uu)*vy + 2.0*qw*cy,
        2.0*dot_uv*qz + (qw*qw - dot_uu)*vz + 2.0*qw*cz,
    ]
}

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

/// 内積（add_gizmo_rotate_local の半円カメラ向き判定に使用）。
fn dot3_local(a: [f32; 3], b: [f32; 3]) -> f32 {
    a[0]*b[0] + a[1]*b[1] + a[2]*b[2]
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

/// 太線バッチ（`LineBatch::build_thick` の出力）の太線部を描画する。
///
/// `draw_gizmo_batch` と異なり、選択強調用の `thick_line_pipeline`
/// （depth_compare=LessEqual）を使うため、可視物による遮蔽の見え方が
/// 通常の 1px ライン（`draw_line_batch`）と一致する。ソリッド三角形部は描画しない。
///
/// カメラ・モデルのバインドグループは 1px ライン描画と同じものを渡すこと
/// （`camera_bg` は resolution を含む CameraUniform、`model_bg` は line_model_buf）。
pub fn draw_thick_line_batch<'pass>(
    render_pass: &mut wgpu::RenderPass<'pass>,
    batch:       &'pass GpuGizmoBatch,
    camera_bg:   &'pass wgpu::BindGroup,
    model_bg:    &'pass wgpu::BindGroup,
    pipelines:   &'pass DrawPipelines,
) {
    if batch.line_count == 0 { return; }
    if let Some(buf) = &batch.line_buffer {
        render_pass.set_pipeline(&pipelines.unlit_line.thick_line_pipeline);
        render_pass.set_bind_group(0, camera_bg, &[]);
        render_pass.set_bind_group(1, model_bg,  &[]);
        render_pass.set_vertex_buffer(0, buf.slice(..));
        render_pass.draw(0..batch.line_count, 0..1);
    }
}
