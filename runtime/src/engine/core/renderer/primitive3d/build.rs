// ============================================================
//  primitive3d/build.rs — 3D プリミティブの幾何構築（純 CPU・GPU 非依存）
//
//  【役割】
//  `Primitive3dCommand` 1 件を、描画に必要な最小の 3 要素へ分解する。
//
//    - `segments` : ワールド空間の線分列。GPU 側で**画面一定幅のリボン**へ広げる
//                   （太さの解決は `primitive3d.wgsl` の仕事なので、ここでは持たない）。
//    - `tris`     : ワールド空間の三角形列（塗り。両面・アンリット）。
//    - `points`   : 常に画面を向く正方形の中心（一辺は px 指定）。
//
//  【なぜ GPU 非依存か】
//  2D 版（primitive2d/tessellate.rs）と同じ理由。図形の幾何をここに閉じ込めると、
//  GPU 無しのユニットテストで「円が指定平面上にあるか」「ワイヤ箱の辺が 12 本か」
//  といった本質だけを検証できる。パイプライン・バッファ管理は pass.rs が持つ。
//
//  【座標系】
//  すべてワールド空間・左手系（エンジン本体と同じ）。角度は度で受け取り、
//  平面内の基準軸 `u` から `v` 向きへ増加する（`basis_from_normal` を参照）。
// ============================================================

use super::queue::{Primitive3dCommand, Primitive3dDrawMode, Primitive3dKind};
use crate::engine::structs::tensor::Vector3;
use crate::engine::structs::transforms::Quaternion;

// ─── 定数（マジックナンバー禁止）────────────────────────────

/// 円弧・円の最小分割数（これ未満は潰れて図形に見えないため引き上げる）。
pub const MIN_SEGMENTS: usize = 3;
/// 円弧・円の最大分割数（1 図形が頂点を食い潰さないための安全弁）。
pub const MAX_SEGMENTS: usize = 256;

/// 全周（度）。
const FULL_CIRCLE_DEGREES: f32 = 360.0;
/// 半周（度。カプセルのキャップ弧に使う）。
const HALF_CIRCLE_DEGREES: f32 = 180.0;

/// 長さ・半径がこの値以下なら「潰れている」とみなして図形を捨てる。
const GEOMETRY_EPSILON: f32 = 1e-6;

/// 法線から基準軸を選ぶときのしきい値。
/// 法線の X 成分がこれ未満なら X 軸を、以上なら Y 軸を補助軸に使う
/// （補助軸と法線が平行になって外積が 0 になるのを避ける）。
const AXIS_PICK_THRESHOLD: f32 = 0.9;

/// 法線が退化していたときの既定法線（真上）。
const FALLBACK_NORMAL: [f32; 3] = [0.0, 1.0, 0.0];
/// 基準軸が退化していたときの既定軸（+X）。
const FALLBACK_TANGENT: [f32; 3] = [1.0, 0.0, 0.0];

/// 直方体の頂点数。
const BOX_CORNER_COUNT: usize = 8;
/// 直方体の辺数。
const BOX_EDGE_COUNT: usize = 12;
/// サイズ指定から半サイズを得る係数。
const HALF: f32 = 0.5;

/// ワイヤ球を構成する大円の数（XY / YZ / ZX 平面）。
const WIRE_SPHERE_CIRCLE_COUNT: usize = 3;
/// カプセルの側面線の本数（±u / ±v の 4 本）。
const CAPSULE_SIDE_LINE_COUNT: usize = 4;

/// 多角形（Polygon）が塗りになるための最小頂点数。
const MIN_POLYGON_VERTICES: usize = 3;

// ─── 出力データ ──────────────────────────────────────────────

/// ワールド空間の線分 1 本（GPU 側で画面一定幅のリボンへ広がる）。
#[derive(Copy, Clone, Debug, PartialEq)]
pub struct Segment3d {
    /// 始点（ワールド）。
    pub a: [f32; 3],
    /// 終点（ワールド）。
    pub b: [f32; 3],
}

/// 常に画面を向く正方形（点の描画）。
#[derive(Copy, Clone, Debug, PartialEq)]
pub struct ScreenPoint3d {
    /// 中心（ワールド）。
    pub pos: [f32; 3],
    /// 一辺の長さ（画面 px）。
    pub size_px: f32,
}

/// 1 コマンドぶんの構築結果。
#[derive(Default, Clone, Debug)]
pub struct Mesh3d {
    /// 線分（リボンとして描く）。
    pub segments: Vec<Segment3d>,
    /// 塗りの三角形（両面・アンリット）。
    pub tris: Vec<[[f32; 3]; 3]>,
    /// 画面を向く正方形。
    pub points: Vec<ScreenPoint3d>,
}

impl Mesh3d {
    /// 描くものが無いか。
    pub fn is_empty(&self) -> bool {
        self.segments.is_empty() && self.tris.is_empty() && self.points.is_empty()
    }
}

// ─── ベクトル小道具（依存を増やさないためのローカル実装）────

/// a - b。
fn sub(a: [f32; 3], b: [f32; 3]) -> [f32; 3] {
    [a[0] - b[0], a[1] - b[1], a[2] - b[2]]
}

/// a + b。
fn add(a: [f32; 3], b: [f32; 3]) -> [f32; 3] {
    [a[0] + b[0], a[1] + b[1], a[2] + b[2]]
}

/// a * s。
fn scale(a: [f32; 3], s: f32) -> [f32; 3] {
    [a[0] * s, a[1] * s, a[2] * s]
}

/// 外積 a × b。
fn cross(a: [f32; 3], b: [f32; 3]) -> [f32; 3] {
    [
        a[1] * b[2] - a[2] * b[1],
        a[2] * b[0] - a[0] * b[2],
        a[0] * b[1] - a[1] * b[0],
    ]
}

/// ベクトル長。
fn length(a: [f32; 3]) -> f32 {
    (a[0] * a[0] + a[1] * a[1] + a[2] * a[2]).sqrt()
}

/// 正規化（長さが 0 に近ければ None）。
fn normalize(a: [f32; 3]) -> Option<[f32; 3]> {
    let l = length(a);
    if l <= GEOMETRY_EPSILON {
        None
    } else {
        Some(scale(a, 1.0 / l))
    }
}

/// 中心 + u*cos + v*sin の合成（円周上の 1 点）。
fn on_circle(center: [f32; 3], u: [f32; 3], v: [f32; 3], radius: f32, rad: f32) -> [f32; 3] {
    let (s, c) = rad.sin_cos();
    add(center, add(scale(u, radius * c), scale(v, radius * s)))
}

// ─── 共通ヘルパ ──────────────────────────────────────────────

/// 法線から、その平面内の正規直交基底 (u, v) を作る。
///
/// 角度 0 度が `u` 方向、+90 度が `v` 方向になる（全図形共通の規約）。
/// 法線が退化している場合は真上 (+Y) を法線として扱う。
pub fn basis_from_normal(normal: [f32; 3]) -> ([f32; 3], [f32; 3]) {
    let n = normalize(normal).unwrap_or(FALLBACK_NORMAL);
    // 法線と平行になりにくい補助軸を選ぶ（外積が 0 になるのを避ける）
    let helper = if n[0].abs() < AXIS_PICK_THRESHOLD {
        [1.0, 0.0, 0.0]
    } else {
        [0.0, 1.0, 0.0]
    };
    let u = normalize(cross(helper, n)).unwrap_or(FALLBACK_TANGENT);
    let v = cross(n, u);
    (u, v)
}

/// 分割数を安全な範囲へ丸める（FFI の float から整数へ）。
pub fn clamp_segments(v: f32) -> usize {
    if !v.is_finite() {
        return MIN_SEGMENTS;
    }
    (v.round().max(MIN_SEGMENTS as f32) as usize).min(MAX_SEGMENTS)
}

/// 円弧上の点列を作る（`segments + 1` 点。開始角から終了角まで等分）。
fn arc_points(
    center: [f32; 3],
    u: [f32; 3],
    v: [f32; 3],
    radius: f32,
    start_deg: f32,
    end_deg: f32,
    segments: usize,
) -> Vec<[f32; 3]> {
    let start = start_deg.to_radians();
    let end = end_deg.to_radians();
    let step = (end - start) / segments as f32;
    (0..=segments)
        .map(|i| on_circle(center, u, v, radius, start + step * i as f32))
        .collect()
}

/// 全周の点列を作る（`segments` 点。終点は始点と重ならない = 閉じた輪郭用）。
fn circle_points(
    center: [f32; 3],
    u: [f32; 3],
    v: [f32; 3],
    radius: f32,
    segments: usize,
) -> Vec<[f32; 3]> {
    let step = FULL_CIRCLE_DEGREES.to_radians() / segments as f32;
    (0..segments)
        .map(|i| on_circle(center, u, v, radius, step * i as f32))
        .collect()
}

/// 点列を線分列へ変換して積む（`closed` なら末尾と先頭も繋ぐ）。
fn push_polyline(out: &mut Vec<Segment3d>, pts: &[[f32; 3]], closed: bool) {
    if pts.len() < 2 {
        return;
    }
    for w in pts.windows(2) {
        out.push(Segment3d { a: w[0], b: w[1] });
    }
    if closed {
        out.push(Segment3d {
            a: pts[pts.len() - 1],
            b: pts[0],
        });
    }
}

/// 凸多角形を扇状に三角形分割して積む（頂点 0 を要とする）。
///
/// `Triangle` / `Quad` / 円・扇形のような凸形状専用。凹多角形は結果が保証されない
/// （2D 版の耳刈りと違い、3D では「どの平面で凹か」が一意に決まらないため、
///  複雑な多角形は呼び出し側で三角形へ分けてもらう方針）。
fn push_fan(out: &mut Vec<[[f32; 3]; 3]>, pts: &[[f32; 3]]) {
    if pts.len() < MIN_POLYGON_VERTICES {
        return;
    }
    for i in 1..pts.len() - 1 {
        out.push([pts[0], pts[i], pts[i + 1]]);
    }
}

// ─── 図形ごとの構築 ──────────────────────────────────────────

/// コマンド 1 件をワールド空間の線分／三角形／点へ分解する。
///
/// 点数・パラメータが足りないコマンドは空メッシュを返す（黙って捨てる）。
pub fn build(cmd: &Primitive3dCommand) -> Mesh3d {
    let mut mesh = Mesh3d::default();
    let p = &cmd.points;
    let e = &cmd.extras;
    let outline = cmd.mode == Primitive3dDrawMode::Outline;

    match cmd.kind {
        // ── 折れ線（Line も含む）──────────────────────────
        Primitive3dKind::Polyline => {
            let closed = e[0] >= 0.5;
            push_polyline(&mut mesh.segments, p, closed);
        }

        // ── 平面多角形（Triangle / Quad）──────────────────
        Primitive3dKind::Polygon => {
            if outline {
                push_polyline(&mut mesh.segments, p, true);
            } else {
                push_fan(&mut mesh.tris, p);
            }
        }

        // ── 円（塗り = 円盤 / 輪郭 = 閉じた折れ線）─────────
        Primitive3dKind::Circle => {
            if p.len() < 2 {
                return mesh;
            }
            let radius = e[0];
            if radius <= GEOMETRY_EPSILON {
                return mesh;
            }
            let (u, v) = basis_from_normal(p[1]);
            let seg = clamp_segments(e[1]);
            let ring = circle_points(p[0], u, v, radius, seg);
            if outline {
                push_polyline(&mut mesh.segments, &ring, true);
            } else {
                // 中心を要とする扇（円盤）。輪郭の三角形が中心で交わる。
                for i in 0..ring.len() {
                    let j = (i + 1) % ring.len();
                    mesh.tris.push([p[0], ring[i], ring[j]]);
                }
            }
        }

        // ── リング（円環バンド。常に塗り）──────────────────
        Primitive3dKind::Ring => {
            if p.len() < 2 {
                return mesh;
            }
            let (inner, outer) = (e[0].min(e[1]), e[0].max(e[1]));
            if outer <= GEOMETRY_EPSILON {
                return mesh;
            }
            let (u, v) = basis_from_normal(p[1]);
            let seg = clamp_segments(e[4]);
            let inner_pts = arc_points(p[0], u, v, inner, e[2], e[3], seg);
            let outer_pts = arc_points(p[0], u, v, outer, e[2], e[3], seg);
            for i in 0..seg {
                // 1 区間 = 内外 4 点の帯（2 三角形）
                mesh.tris.push([inner_pts[i], outer_pts[i], outer_pts[i + 1]]);
                mesh.tris.push([inner_pts[i], outer_pts[i + 1], inner_pts[i + 1]]);
            }
        }

        // ── 円弧（線のみ）─────────────────────────────────
        Primitive3dKind::Arc => {
            if p.len() < 2 {
                return mesh;
            }
            let radius = e[0];
            if radius <= GEOMETRY_EPSILON {
                return mesh;
            }
            let (u, v) = basis_from_normal(p[1]);
            let seg = clamp_segments(e[3]);
            let pts = arc_points(p[0], u, v, radius, e[1], e[2], seg);
            push_polyline(&mut mesh.segments, &pts, false);
        }

        // ── ワイヤ球（3 つの大円）─────────────────────────
        Primitive3dKind::WireSphere => {
            if p.is_empty() {
                return mesh;
            }
            let radius = e[0];
            if radius <= GEOMETRY_EPSILON {
                return mesh;
            }
            let seg = clamp_segments(e[1]);
            // XY / YZ / ZX 平面の大円（法線 = Z / X / Y）
            const NORMALS: [[f32; 3]; WIRE_SPHERE_CIRCLE_COUNT] =
                [[0.0, 0.0, 1.0], [1.0, 0.0, 0.0], [0.0, 1.0, 0.0]];
            for n in NORMALS {
                let (u, v) = basis_from_normal(n);
                let ring = circle_points(p[0], u, v, radius, seg);
                push_polyline(&mut mesh.segments, &ring, true);
            }
        }

        // ── ワイヤ直方体（12 辺）──────────────────────────
        Primitive3dKind::WireBox => {
            if p.is_empty() {
                return mesh;
            }
            let half = [e[0] * HALF, e[1] * HALF, e[2] * HALF];
            // 回転はエンジン本体と同じ YXZ 規約（Quaternion::from_euler が正典）
            let q = Quaternion::from_euler(Vector3::new(
                e[3].to_radians(),
                e[4].to_radians(),
                e[5].to_radians(),
            ));
            // ローカル 8 頂点（符号の全組み合わせ）を回転してから中心へ寄せる
            let mut corners = [[0.0f32; 3]; BOX_CORNER_COUNT];
            for (i, c) in corners.iter_mut().enumerate() {
                let sx = if i & 1 == 0 { -1.0 } else { 1.0 };
                let sy = if i & 2 == 0 { -1.0 } else { 1.0 };
                let sz = if i & 4 == 0 { -1.0 } else { 1.0 };
                let local = Vector3::new(half[0] * sx, half[1] * sy, half[2] * sz);
                let r = q.rotate(local);
                *c = add(p[0], [r.x, r.y, r.z]);
            }
            // 12 辺（頂点インデックスは上のビット並びに対応）
            const EDGES: [(usize, usize); BOX_EDGE_COUNT] = [
                (0, 1), (2, 3), (4, 5), (6, 7), // X 方向
                (0, 2), (1, 3), (4, 6), (5, 7), // Y 方向
                (0, 4), (1, 5), (2, 6), (3, 7), // Z 方向
            ];
            for (i, j) in EDGES {
                mesh.segments.push(Segment3d {
                    a: corners[i],
                    b: corners[j],
                });
            }
        }

        // ── ワイヤカプセル（両端の球 + 側面線）────────────
        Primitive3dKind::WireCapsule => {
            if p.len() < 2 {
                return mesh;
            }
            let radius = e[0];
            if radius <= GEOMETRY_EPSILON {
                return mesh;
            }
            let seg = clamp_segments(e[1]);
            let axis = sub(p[1], p[0]);
            let Some(dir) = normalize(axis) else {
                // 芯が潰れている＝ただの球として描く
                let (u, v) = basis_from_normal(FALLBACK_NORMAL);
                let ring = circle_points(p[0], u, v, radius, seg);
                push_polyline(&mut mesh.segments, &ring, true);
                return mesh;
            };
            let (u, v) = basis_from_normal(dir);
            // 両端の円（芯に垂直）
            for c in [p[0], p[1]] {
                let ring = circle_points(c, u, v, radius, seg);
                push_polyline(&mut mesh.segments, &ring, true);
            }
            // 側面線 4 本（±u / ±v のオフセット）
            const SIDE_SIGNS: [(f32, f32); CAPSULE_SIDE_LINE_COUNT] =
                [(1.0, 0.0), (-1.0, 0.0), (0.0, 1.0), (0.0, -1.0)];
            for (su, sv) in SIDE_SIGNS {
                let off = add(scale(u, radius * su), scale(v, radius * sv));
                mesh.segments.push(Segment3d {
                    a: add(p[0], off),
                    b: add(p[1], off),
                });
            }
            // 半球のキャップ弧（各端 2 本ずつ。芯方向へ膨らむ半円）
            let half_seg = clamp_segments(seg as f32 * HALF);
            for (center, outward) in [(p[1], dir), (p[0], scale(dir, -1.0))] {
                for tangent in [u, v] {
                    let pts = arc_points(
                        center,
                        tangent,
                        outward,
                        radius,
                        0.0,
                        HALF_CIRCLE_DEGREES,
                        half_seg,
                    );
                    push_polyline(&mut mesh.segments, &pts, false);
                }
            }
        }

        // ── 矢印（軸 = 線 / 矢尻 = 塗りの円錐）────────────
        Primitive3dKind::Arrow => {
            if p.len() < 2 {
                return mesh;
            }
            let (from, to) = (p[0], p[1]);
            let axis = sub(to, from);
            let total_len = length(axis);
            let Some(dir) = normalize(axis) else {
                return mesh;
            };
            // 矢尻が軸より長くなると裏返るため全長でクランプする
            let head_len = e[0].max(0.0).min(total_len);
            let head_radius = e[1].max(0.0);
            let seg = clamp_segments(e[2]);
            let base = sub(to, scale(dir, head_len));
            // 軸（矢尻の根元まで）
            if total_len - head_len > GEOMETRY_EPSILON {
                mesh.segments.push(Segment3d { a: from, b: base });
            }
            if head_radius > GEOMETRY_EPSILON && head_len > GEOMETRY_EPSILON {
                let (u, v) = basis_from_normal(dir);
                let ring = circle_points(base, u, v, head_radius, seg);
                // 側面（各区間 → 先端）
                for i in 0..ring.len() {
                    let j = (i + 1) % ring.len();
                    mesh.tris.push([ring[i], ring[j], to]);
                }
                // 底面（裏から見ても穴が空かないように塞ぐ）
                push_fan(&mut mesh.tris, &ring);
            }
        }

        // ── 点（画面を向く正方形）─────────────────────────
        Primitive3dKind::Point => {
            if p.is_empty() {
                return mesh;
            }
            let size = e[0];
            if size <= GEOMETRY_EPSILON {
                return mesh;
            }
            mesh.points.push(ScreenPoint3d {
                pos: p[0],
                size_px: size,
            });
        }
    }
    mesh
}

// ============================================================
//  ユニットテスト（GPU 不要・純幾何）
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;
    use crate::engine::core::renderer::primitive3d::queue::PRIM3D_EXTRA_FLOATS;

    /// 許容誤差（f32 の三角関数を通した比較用）。
    const TEST_EPS: f32 = 1e-4;

    /// テスト用コマンドを組み立てる。
    fn cmd(
        kind: Primitive3dKind,
        mode: Primitive3dDrawMode,
        points: Vec<[f32; 3]>,
        extras: [f32; PRIM3D_EXTRA_FLOATS],
    ) -> Primitive3dCommand {
        Primitive3dCommand {
            kind,
            color: [1.0, 1.0, 1.0, 1.0],
            mode,
            thickness_px: 2.0,
            depth_test: true,
            extras,
            points,
        }
    }

    /// 内積（テスト内の平面判定用）。
    fn dot(a: [f32; 3], b: [f32; 3]) -> f32 {
        a[0] * b[0] + a[1] * b[1] + a[2] * b[2]
    }

    /// 任意法線の円は「その平面上」かつ「中心から半径ちょうど」に載る。
    #[test]
    fn primitive3d_circle_lies_in_normal_plane() {
        const CENTER: [f32; 3] = [3.0, -2.0, 7.0];
        const NORMAL: [f32; 3] = [1.0, 2.0, -3.0];
        const RADIUS: f32 = 2.5;
        const SEGMENTS: usize = 32;
        let c = cmd(
            Primitive3dKind::Circle,
            Primitive3dDrawMode::Outline,
            vec![CENTER, NORMAL],
            [RADIUS, SEGMENTS as f32, 0.0, 0.0, 0.0, 0.0],
        );
        let mesh = build(&c);
        // 閉じた輪郭なので線分数 = 分割数
        assert_eq!(mesh.segments.len(), SEGMENTS);
        let n = normalize(NORMAL).unwrap();
        for s in &mesh.segments {
            for pt in [s.a, s.b] {
                let d = sub(pt, CENTER);
                // 平面内（中心→点 が法線と直交）
                assert!(dot(d, n).abs() < TEST_EPS, "平面から外れた: {:?}", pt);
                // 半径ちょうど
                assert!(
                    (length(d) - RADIUS).abs() < TEST_EPS,
                    "半径がずれた: {}",
                    length(d)
                );
            }
        }
    }

    /// 円の塗りは中心を要とする扇（三角形数 = 分割数）になる。
    #[test]
    fn primitive3d_circle_fill_is_center_fan() {
        const SEGMENTS: usize = 12;
        let c = cmd(
            Primitive3dKind::Circle,
            Primitive3dDrawMode::Fill,
            vec![[0.0, 0.0, 0.0], [0.0, 1.0, 0.0]],
            [1.0, SEGMENTS as f32, 0.0, 0.0, 0.0, 0.0],
        );
        let mesh = build(&c);
        assert_eq!(mesh.tris.len(), SEGMENTS);
        assert!(mesh.segments.is_empty());
        for t in &mesh.tris {
            assert_eq!(t[0], [0.0, 0.0, 0.0], "扇の要は中心");
        }
    }

    /// ワイヤ箱は 12 辺で、全頂点が中心から半対角の距離にある（回転しても不変）。
    #[test]
    fn primitive3d_wire_box_has_twelve_edges() {
        const CENTER: [f32; 3] = [1.0, 2.0, 3.0];
        const SIZE: [f32; 3] = [2.0, 4.0, 6.0];
        let c = cmd(
            Primitive3dKind::WireBox,
            Primitive3dDrawMode::Outline,
            vec![CENTER],
            [SIZE[0], SIZE[1], SIZE[2], 30.0, 45.0, 60.0],
        );
        let mesh = build(&c);
        assert_eq!(mesh.segments.len(), BOX_EDGE_COUNT);
        assert!(mesh.tris.is_empty());
        // 半対角長（回転で不変）
        let half_diag = length([SIZE[0] * HALF, SIZE[1] * HALF, SIZE[2] * HALF]);
        for s in &mesh.segments {
            for pt in [s.a, s.b] {
                assert!(
                    (length(sub(pt, CENTER)) - half_diag).abs() < TEST_EPS,
                    "頂点が半対角上にない: {:?}",
                    pt
                );
            }
        }
        // 辺長の内訳は各軸サイズが 4 本ずつ
        let mut counts = [0usize; 3];
        for s in &mesh.segments {
            let l = length(sub(s.b, s.a));
            for (i, sz) in SIZE.iter().enumerate() {
                if (l - sz).abs() < TEST_EPS {
                    counts[i] += 1;
                }
            }
        }
        assert_eq!(counts, [4, 4, 4], "各軸方向の辺が 4 本ずつ");
    }

    /// 矢印は「軸の線 1 本 + 円錐（側面 n + 底面 n-2 三角形）」で、先端が終点に一致する。
    #[test]
    fn primitive3d_arrow_head_geometry() {
        const FROM: [f32; 3] = [0.0, 0.0, 0.0];
        const TO: [f32; 3] = [0.0, 10.0, 0.0];
        const HEAD_LEN: f32 = 2.0;
        const HEAD_RADIUS: f32 = 1.0;
        const SEGMENTS: usize = 8;
        let c = cmd(
            Primitive3dKind::Arrow,
            Primitive3dDrawMode::Fill,
            vec![FROM, TO],
            [HEAD_LEN, HEAD_RADIUS, SEGMENTS as f32, 0.0, 0.0, 0.0],
        );
        let mesh = build(&c);
        // 軸は 1 本（始点 → 矢尻の根元）
        assert_eq!(mesh.segments.len(), 1);
        assert_eq!(mesh.segments[0].a, FROM);
        assert!((mesh.segments[0].b[1] - (TO[1] - HEAD_LEN)).abs() < TEST_EPS);
        // 円錐: 側面 SEGMENTS + 底面扇 SEGMENTS-2
        assert_eq!(mesh.tris.len(), SEGMENTS + (SEGMENTS - 2));
        // 側面三角形の 3 頂点目は必ず先端
        for t in mesh.tris.iter().take(SEGMENTS) {
            assert_eq!(t[2], TO, "側面の頂点は矢印の先端");
        }
        // 矢尻の輪は根元平面上で半径ちょうど
        let base_y = TO[1] - HEAD_LEN;
        for t in mesh.tris.iter().take(SEGMENTS) {
            assert!((t[0][1] - base_y).abs() < TEST_EPS);
            assert!((length(sub(t[0], [0.0, base_y, 0.0])) - HEAD_RADIUS).abs() < TEST_EPS);
        }
    }

    /// 矢尻の長さが全長を超えても裏返らない（全長でクランプされ軸線が消える）。
    #[test]
    fn primitive3d_arrow_head_clamped_to_length() {
        let c = cmd(
            Primitive3dKind::Arrow,
            Primitive3dDrawMode::Fill,
            vec![[0.0, 0.0, 0.0], [1.0, 0.0, 0.0]],
            [10.0, 0.5, 8.0, 0.0, 0.0, 0.0],
        );
        let mesh = build(&c);
        assert!(mesh.segments.is_empty(), "軸線は残らない");
        // 円錐の底面は始点に一致する（矢尻長 = 全長）
        assert!((mesh.tris[0][0][0] - 0.0).abs() < TEST_EPS);
    }

    /// 折れ線は開いていれば n-1 本、閉じていれば n 本の線分になる。
    #[test]
    fn primitive3d_polyline_segment_count() {
        let pts = vec![
            [0.0, 0.0, 0.0],
            [1.0, 0.0, 0.0],
            [1.0, 1.0, 0.0],
            [0.0, 1.0, 0.0],
        ];
        let open = build(&cmd(
            Primitive3dKind::Polyline,
            Primitive3dDrawMode::Outline,
            pts.clone(),
            [0.0; PRIM3D_EXTRA_FLOATS],
        ));
        assert_eq!(open.segments.len(), pts.len() - 1);
        let closed = build(&cmd(
            Primitive3dKind::Polyline,
            Primitive3dDrawMode::Outline,
            pts.clone(),
            [1.0, 0.0, 0.0, 0.0, 0.0, 0.0],
        ));
        assert_eq!(closed.segments.len(), pts.len());
    }

    /// リングは内外半径の帯（区間ごとに 2 三角形）になり、点は指定平面上に載る。
    #[test]
    fn primitive3d_ring_band_geometry() {
        const CENTER: [f32; 3] = [0.0, 5.0, 0.0];
        const NORMAL: [f32; 3] = [0.0, 1.0, 0.0];
        const INNER: f32 = 2.0;
        const OUTER: f32 = 3.0;
        const SEGMENTS: usize = 16;
        let mesh = build(&cmd(
            Primitive3dKind::Ring,
            Primitive3dDrawMode::Fill,
            vec![CENTER, NORMAL],
            [INNER, OUTER, 0.0, 360.0, SEGMENTS as f32, 0.0],
        ));
        assert_eq!(mesh.tris.len(), SEGMENTS * 2);
        for t in &mesh.tris {
            for pt in t {
                // 法線平面（水面）上にある
                assert!((pt[1] - CENTER[1]).abs() < TEST_EPS);
                let r = length(sub(*pt, CENTER));
                assert!(
                    (r - INNER).abs() < TEST_EPS || (r - OUTER).abs() < TEST_EPS,
                    "内外どちらかの半径に載る: {r}"
                );
            }
        }
    }

    /// ワイヤ球は 3 つの大円（各 segments 本の閉じた輪郭）になる。
    #[test]
    fn primitive3d_wire_sphere_three_great_circles() {
        const SEGMENTS: usize = 24;
        const RADIUS: f32 = 4.0;
        const CENTER: [f32; 3] = [1.0, 1.0, 1.0];
        let mesh = build(&cmd(
            Primitive3dKind::WireSphere,
            Primitive3dDrawMode::Outline,
            vec![CENTER],
            [RADIUS, SEGMENTS as f32, 0.0, 0.0, 0.0, 0.0],
        ));
        assert_eq!(mesh.segments.len(), SEGMENTS * WIRE_SPHERE_CIRCLE_COUNT);
        for s in &mesh.segments {
            assert!((length(sub(s.a, CENTER)) - RADIUS).abs() < TEST_EPS);
        }
    }

    /// 点は画面向き正方形 1 個として積まれ、サイズ 0 は捨てられる。
    #[test]
    fn primitive3d_point_emits_screen_quad() {
        const POS: [f32; 3] = [1.0, 2.0, 3.0];
        const SIZE: f32 = 6.0;
        let mesh = build(&cmd(
            Primitive3dKind::Point,
            Primitive3dDrawMode::Fill,
            vec![POS],
            [SIZE, 0.0, 0.0, 0.0, 0.0, 0.0],
        ));
        assert_eq!(mesh.points.len(), 1);
        assert_eq!(mesh.points[0].pos, POS);
        assert_eq!(mesh.points[0].size_px, SIZE);

        let zero = build(&cmd(
            Primitive3dKind::Point,
            Primitive3dDrawMode::Fill,
            vec![POS],
            [0.0; PRIM3D_EXTRA_FLOATS],
        ));
        assert!(zero.is_empty());
    }

    /// 点数・半径が足りないコマンドは空メッシュになる（黙って捨てる契約）。
    #[test]
    fn primitive3d_degenerate_commands_are_empty() {
        // 点が 1 個しかない円
        assert!(build(&cmd(
            Primitive3dKind::Circle,
            Primitive3dDrawMode::Fill,
            vec![[0.0, 0.0, 0.0]],
            [1.0, 16.0, 0.0, 0.0, 0.0, 0.0],
        ))
        .is_empty());
        // 半径 0 の円
        assert!(build(&cmd(
            Primitive3dKind::Circle,
            Primitive3dDrawMode::Fill,
            vec![[0.0, 0.0, 0.0], [0.0, 1.0, 0.0]],
            [0.0, 16.0, 0.0, 0.0, 0.0, 0.0],
        ))
        .is_empty());
        // 頂点 2 個の多角形（塗り）
        assert!(build(&cmd(
            Primitive3dKind::Polygon,
            Primitive3dDrawMode::Fill,
            vec![[0.0, 0.0, 0.0], [1.0, 0.0, 0.0]],
            [0.0; PRIM3D_EXTRA_FLOATS],
        ))
        .is_empty());
        // 始点と終点が同じ矢印
        assert!(build(&cmd(
            Primitive3dKind::Arrow,
            Primitive3dDrawMode::Fill,
            vec![[1.0, 1.0, 1.0], [1.0, 1.0, 1.0]],
            [1.0, 1.0, 8.0, 0.0, 0.0, 0.0],
        ))
        .is_empty());
    }

    /// 分割数は安全範囲へ丸められる（下限・上限・非数）。
    #[test]
    fn primitive3d_segments_are_clamped() {
        assert_eq!(clamp_segments(0.0), MIN_SEGMENTS);
        assert_eq!(clamp_segments(-100.0), MIN_SEGMENTS);
        assert_eq!(clamp_segments(f32::NAN), MIN_SEGMENTS);
        assert_eq!(clamp_segments(10_000.0), MAX_SEGMENTS);
        assert_eq!(clamp_segments(48.0), 48);
    }

    /// 基底 (u, v) と法線は正規直交（角度 0 度が u・+90 度が v になる前提）。
    #[test]
    fn primitive3d_basis_is_orthonormal() {
        for n in [
            [0.0, 1.0, 0.0],
            [1.0, 0.0, 0.0],
            [0.3, -0.5, 0.81],
            [0.0, 0.0, 0.0], // 退化 → 既定法線
        ] {
            let (u, v) = basis_from_normal(n);
            assert!((length(u) - 1.0).abs() < TEST_EPS);
            assert!((length(v) - 1.0).abs() < TEST_EPS);
            assert!(dot(u, v).abs() < TEST_EPS);
        }
    }

    /// カプセルは「両端の円 + 側面 4 本 + キャップ弧 4 本」で構成される。
    #[test]
    fn primitive3d_wire_capsule_structure() {
        const SEGMENTS: usize = 16;
        let mesh = build(&cmd(
            Primitive3dKind::WireCapsule,
            Primitive3dDrawMode::Outline,
            vec![[0.0, 0.0, 0.0], [0.0, 4.0, 0.0]],
            [1.0, SEGMENTS as f32, 0.0, 0.0, 0.0, 0.0],
        ));
        let half_seg = clamp_segments(SEGMENTS as f32 * HALF);
        // 円 2 本（各 SEGMENTS）+ 側面 4 本 + キャップ弧 4 本（各 half_seg）
        let expected = SEGMENTS * 2 + CAPSULE_SIDE_LINE_COUNT + half_seg * 4;
        assert_eq!(mesh.segments.len(), expected);
    }
}
