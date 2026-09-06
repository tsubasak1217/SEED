// ============================================================
//  primitive2d/tessellate.rs — 2D プリミティブの三角形分割（CPU）
//
//  【役割】
//  `PrimitiveCommand`（スクリプトが積んだ図形）を、描画空間（キャンバス px）の
//  三角形メッシュへ変換する。GPU へ渡す直前の NDC 変換は `pass.rs` が行うため、
//  ここは **純粋な 2D 幾何計算**（GPU 非依存 = ユニットテスト可能）に徹する。
//
//  【アンチエイリアス方針】
//  シェーダで解析 SDF を評価するのではなく、**輪郭の外側へ 1px の
//  フェザー帯（alpha 1 → 0）を張る**方式を採る。
//   - 図形種別ごとの専用シェーダが不要（1 パイプラインで全図形を描ける）
//   - 塗り／線／円弧／角丸のすべてに同じ処理が効く
//   - 代償として、半透明色の太い折れ線は継ぎ目（ジョイント）がわずかに
//     濃くなる（帯が重なるため）。不透明色では見えない。
//
//  【座標系】
//  入力・出力とも「描画空間の px」。X 右・Y 下（キャンバス／スクリーンと同じ）。
//  したがって回転角は時計回りが正になる。
// ============================================================

use super::queue::{
    PrimitiveCommand, PrimitiveDrawMode, PrimitiveKind, MAX_POINTS_PER_PRIMITIVE,
};

// ─── 分割数・下限の定数（マジックナンバーの集約）──────────────

/// 円・円弧を近似するときの 1 セグメントあたりの目標弧長（px）。
/// 小さいほど滑らかで頂点が増える。
const ARC_TARGET_SEGMENT_PX: f32 = 4.0;
/// 円・円弧の最小分割数（極小半径でも三角形が作れる数）。
const ARC_MIN_SEGMENTS: u32 = 8;
/// 円・円弧の最大分割数（巨大半径で頂点が爆発しないための上限）。
const ARC_MAX_SEGMENTS: u32 = 256;
/// 線の最小太さ（px）。0 以下を渡されても消えないようにする。
const MIN_THICKNESS: f32 = 0.1;
/// 折れ線のジョイント（角）を丸めるときの分割数。
const JOIN_SEGMENTS: u32 = 8;
/// ジョイントを丸める最小の太さ（px）。これ以下は角を丸めても見えないので省く。
const JOIN_MIN_THICKNESS: f32 = 1.5;
/// ベジエ曲線の最小／最大分割数。
const BEZIER_MIN_SEGMENTS: u32 = 2;
const BEZIER_MAX_SEGMENTS: u32 = 512;
/// 同一とみなす点間距離（px）。これ未満の連続点は縮退として捨てる。
const POINT_EPSILON: f32 = 1e-4;
/// 正多角形の最小頂点数。
const POLY_MIN_VERTICES: u32 = 3;

// ─── メッシュ ────────────────────────────────────────────────

/// 三角形分割後の頂点（描画空間 px + フェザー用アルファ係数）。
#[derive(Copy, Clone, Debug, PartialEq)]
pub struct Vert2d {
    /// 描画空間の位置（px）。
    pub pos: [f32; 2],
    /// アルファ係数（1 = 図形の内部 / 0 = フェザー帯の外縁）。
    pub alpha: f32,
}

/// 1 図形ぶんの三角形メッシュ。
#[derive(Clone, Debug, Default)]
pub struct Mesh2d {
    /// 頂点列。
    pub verts: Vec<Vert2d>,
    /// 三角形インデックス（3 個で 1 三角形）。
    pub idx: Vec<u32>,
}

impl Mesh2d {
    /// 空メッシュ。
    pub fn new() -> Self {
        Self::default()
    }

    /// 三角形が 1 枚も無いか。
    pub fn is_empty(&self) -> bool {
        self.idx.is_empty()
    }

    /// 三角形の枚数。
    pub fn triangle_count(&self) -> usize {
        self.idx.len() / 3
    }

    /// 頂点を 1 個追加してそのインデックスを返す。
    fn push_vert(&mut self, pos: [f32; 2], alpha: f32) -> u32 {
        self.verts.push(Vert2d { pos, alpha });
        (self.verts.len() - 1) as u32
    }

    /// 三角形を 1 枚追加する（頂点 3 個を新規に積む素朴な実装）。
    fn push_tri(&mut self, a: ([f32; 2], f32), b: ([f32; 2], f32), c: ([f32; 2], f32)) {
        let ia = self.push_vert(a.0, a.1);
        let ib = self.push_vert(b.0, b.1);
        let ic = self.push_vert(c.0, c.1);
        self.idx.extend_from_slice(&[ia, ib, ic]);
    }
}

// ─── 小さなベクトル演算 ──────────────────────────────────────

/// 2 点間の差ベクトル。
#[inline]
fn sub(a: [f32; 2], b: [f32; 2]) -> [f32; 2] {
    [a[0] - b[0], a[1] - b[1]]
}

/// 2D 外積（z 成分）。
#[inline]
fn cross(a: [f32; 2], b: [f32; 2]) -> f32 {
    a[0] * b[1] - a[1] * b[0]
}

/// 正規化（長さ 0 は None）。
#[inline]
fn normalize(v: [f32; 2]) -> Option<[f32; 2]> {
    let len = (v[0] * v[0] + v[1] * v[1]).sqrt();
    if len <= POINT_EPSILON {
        None
    } else {
        Some([v[0] / len, v[1] / len])
    }
}

/// 多角形の符号付き面積（正 = 反時計回り／数学座標系）。
fn signed_area(pts: &[[f32; 2]]) -> f32 {
    let n = pts.len();
    if n < 3 {
        return 0.0;
    }
    let mut s = 0.0;
    for i in 0..n {
        let a = pts[i];
        let b = pts[(i + 1) % n];
        s += a[0] * b[1] - b[0] * a[1];
    }
    s * 0.5
}

/// 連続する重複点を取り除く（縮退三角形の発生源を潰す）。
///
/// `closed` が true のときは先頭と末尾の重複も取り除く。
fn dedup_points(pts: &[[f32; 2]], closed: bool) -> Vec<[f32; 2]> {
    let mut out: Vec<[f32; 2]> = Vec::with_capacity(pts.len());
    for &p in pts {
        if let Some(&last) = out.last() {
            if (p[0] - last[0]).abs() < POINT_EPSILON && (p[1] - last[1]).abs() < POINT_EPSILON {
                continue;
            }
        }
        out.push(p);
    }
    if closed && out.len() >= 2 {
        let first = out[0];
        let last = *out.last().unwrap();
        if (first[0] - last[0]).abs() < POINT_EPSILON && (first[1] - last[1]).abs() < POINT_EPSILON
        {
            out.pop();
        }
    }
    out
}

/// 点が三角形の内部（境界含む）にあるか。耳刈り（ear clipping）の判定に使う。
fn point_in_triangle(p: [f32; 2], a: [f32; 2], b: [f32; 2], c: [f32; 2]) -> bool {
    let d1 = cross(sub(b, a), sub(p, a));
    let d2 = cross(sub(c, b), sub(p, b));
    let d3 = cross(sub(a, c), sub(p, c));
    let has_neg = d1 < 0.0 || d2 < 0.0 || d3 < 0.0;
    let has_pos = d1 > 0.0 || d2 > 0.0 || d3 > 0.0;
    !(has_neg && has_pos)
}

// ─── 三角形分割（耳刈り）─────────────────────────────────────

/// 単純多角形（自己交差なし）を耳刈りで三角形分割する。
///
/// 戻り値は `pts` へのインデックス三つ組の列。
/// 穴あき・自己交差多角形は対象外（結果は未定義だがクラッシュはしない）。
/// 進行不能になった場合は残りを扇形分割にフォールバックする。
pub fn triangulate_ear_clip(pts: &[[f32; 2]]) -> Vec<[u32; 3]> {
    let n = pts.len();
    if n < 3 {
        return Vec::new();
    }
    // 反時計回り（面積が正）になる順序で処理する。
    let ccw = signed_area(pts) > 0.0;
    let mut ring: Vec<usize> = if ccw {
        (0..n).collect()
    } else {
        (0..n).rev().collect()
    };

    let mut out: Vec<[u32; 3]> = Vec::with_capacity(n.saturating_sub(2));
    // 無限ループ防止のガード（1 回の走査で 1 頂点消えるのが正常）。
    let mut guard = n * n + n;
    while ring.len() > 3 && guard > 0 {
        guard -= 1;
        let m = ring.len();
        let mut clipped = false;
        for i in 0..m {
            let ia = ring[(i + m - 1) % m];
            let ib = ring[i];
            let ic = ring[(i + 1) % m];
            let (a, b, c) = (pts[ia], pts[ib], pts[ic]);
            // 凸頂点でなければ耳ではない
            if cross(sub(b, a), sub(c, b)) <= 0.0 {
                continue;
            }
            // 三角形の内部に他の頂点があれば耳ではない
            let contains = ring
                .iter()
                .any(|&k| k != ia && k != ib && k != ic && point_in_triangle(pts[k], a, b, c));
            if contains {
                continue;
            }
            out.push([ia as u32, ib as u32, ic as u32]);
            ring.remove(i);
            clipped = true;
            break;
        }
        if !clipped {
            // 自己交差など耳が見つからない形状: 残りを扇形にして打ち切る
            break;
        }
    }
    if ring.len() == 3 {
        out.push([ring[0] as u32, ring[1] as u32, ring[2] as u32]);
    } else if ring.len() > 3 {
        for i in 1..ring.len() - 1 {
            out.push([ring[0] as u32, ring[i] as u32, ring[i + 1] as u32]);
        }
    }
    out
}

// ─── 塗りつぶし（+ フェザー帯）───────────────────────────────

/// 閉じた輪郭を塗りつぶし、外側へ幅 `feather` のアンチエイリアス帯を張る。
///
/// `contour` は重複点を除いた 3 点以上の単純多角形であること。
pub fn fill_contour(mesh: &mut Mesh2d, contour: &[[f32; 2]], feather: f32) {
    let pts = dedup_points(contour, true);
    if pts.len() < 3 {
        return;
    }
    // ── 本体（alpha = 1）──
    for tri in triangulate_ear_clip(&pts) {
        mesh.push_tri(
            (pts[tri[0] as usize], 1.0),
            (pts[tri[1] as usize], 1.0),
            (pts[tri[2] as usize], 1.0),
        );
    }
    if feather <= 0.0 {
        return;
    }

    // ── フェザー帯（外向き法線へ feather だけ押し出し、alpha 1 → 0）──
    // 巻き方向によって「外側」が反転するため面積の符号で補正する。
    let flip = if signed_area(&pts) >= 0.0 { 1.0 } else { -1.0 };
    let n = pts.len();
    // 各辺の外向き法線（正規化済み）。縮退辺は None。
    let normals: Vec<Option<[f32; 2]>> = (0..n)
        .map(|i| {
            let d = normalize(sub(pts[(i + 1) % n], pts[i]))?;
            Some([d[1] * flip, -d[0] * flip])
        })
        .collect();

    for i in 0..n {
        let Some(nrm) = normals[i] else { continue };
        let a = pts[i];
        let b = pts[(i + 1) % n];
        let ao = [a[0] + nrm[0] * feather, a[1] + nrm[1] * feather];
        let bo = [b[0] + nrm[0] * feather, b[1] + nrm[1] * feather];
        // 帯（内側 alpha=1 / 外側 alpha=0）を 2 三角形で張る
        mesh.push_tri((a, 1.0), (b, 1.0), (bo, 0.0));
        mesh.push_tri((a, 1.0), (bo, 0.0), (ao, 0.0));
    }

    // 凸の角では隣接する帯の間に隙間ができるので三角形で塞ぐ
    // （凹の角は帯が重なるので不要）。
    for i in 0..n {
        let prev = (i + n - 1) % n;
        let (Some(np), Some(nc)) = (normals[prev], normals[i]) else {
            continue;
        };
        let p = pts[i];
        // 前の辺 → 次の辺で外側に開く（凸）ときだけ塞ぐ
        if cross(np, nc) * flip <= 0.0 {
            continue;
        }
        mesh.push_tri(
            (p, 1.0),
            ([p[0] + np[0] * feather, p[1] + np[1] * feather], 0.0),
            ([p[0] + nc[0] * feather, p[1] + nc[1] * feather], 0.0),
        );
    }
}

// ─── 線（折れ線）─────────────────────────────────────────────

/// 折れ線を太さ `thickness` の帯として描く。
///
/// 実装は「線分ごとの矩形 + 角の扇形」を個別の閉輪郭として
/// `fill_contour` へ流すだけ（＝アンチエイリアスも自動で乗る）。
/// 半透明色では継ぎ目がわずかに濃くなる（帯の重なり）。
pub fn stroke_polyline(
    mesh: &mut Mesh2d,
    points: &[[f32; 2]],
    closed: bool,
    thickness: f32,
    feather: f32,
) {
    let pts = dedup_points(points, closed);
    if pts.len() < 2 {
        return;
    }
    let half = thickness.max(MIN_THICKNESS) * 0.5;
    let n = pts.len();
    let seg_count = if closed { n } else { n - 1 };

    // ── 線分ごとの矩形 ──
    for i in 0..seg_count {
        let a = pts[i];
        let b = pts[(i + 1) % n];
        let Some(d) = normalize(sub(b, a)) else {
            continue;
        };
        let nx = -d[1] * half;
        let ny = d[0] * half;
        let quad = [
            [a[0] + nx, a[1] + ny],
            [b[0] + nx, b[1] + ny],
            [b[0] - nx, b[1] - ny],
            [a[0] - nx, a[1] - ny],
        ];
        fill_contour(mesh, &quad, feather);
    }

    // ── 角（ジョイント）を丸で塞ぐ ──
    if thickness >= JOIN_MIN_THICKNESS {
        let joint_range: Vec<usize> = if closed {
            (0..n).collect()
        } else {
            (1..n - 1).collect()
        };
        for i in joint_range {
            let circle = arc_points(pts[i], half, half, 0.0, 360.0, JOIN_SEGMENTS, false);
            fill_contour(mesh, &circle, feather);
        }
    }
}

// ─── 円・円弧の点列生成 ──────────────────────────────────────

/// 半径から適切な分割数を決める（弧長 `ARC_TARGET_SEGMENT_PX` を目標にする）。
pub fn arc_segments_for(radius: f32, sweep_deg: f32) -> u32 {
    let arc_len = radius.abs() * sweep_deg.abs().to_radians();
    let raw = (arc_len / ARC_TARGET_SEGMENT_PX).ceil();
    (raw as i64).clamp(ARC_MIN_SEGMENTS as i64, ARC_MAX_SEGMENTS as i64) as u32
}

/// 楕円弧上の点列を返す。
///
/// - `start_deg` → `end_deg` を `segments` 等分する（点数は segments + 1）。
/// - `include_center` が true のとき先頭に中心点を入れる（扇形の塗り用）。
/// - 角度は画面座標系（Y 下向き）なので、正の角度は時計回りに進む。
pub fn arc_points(
    center: [f32; 2],
    radius_x: f32,
    radius_y: f32,
    start_deg: f32,
    end_deg: f32,
    segments: u32,
    include_center: bool,
) -> Vec<[f32; 2]> {
    let segs = segments.max(1);
    let mut out = Vec::with_capacity(segs as usize + 2);
    if include_center {
        out.push(center);
    }
    let start = start_deg.to_radians();
    let end = end_deg.to_radians();
    for i in 0..=segs {
        let t = i as f32 / segs as f32;
        let a = start + (end - start) * t;
        out.push([
            center[0] + a.cos() * radius_x,
            center[1] + a.sin() * radius_y,
        ]);
    }
    out
}

/// 円環（リング）セクタの閉輪郭を返す。
///
/// 外周を start→end、内周を end→start と辿って 1 本の単純多角形にする。
/// 全周（360°）の場合も同じ輪郭でよい（内周と外周が縮退せず耳刈りが通る）。
pub fn ring_contour(
    center: [f32; 2],
    inner_r: f32,
    outer_r: f32,
    start_deg: f32,
    end_deg: f32,
) -> Vec<[f32; 2]> {
    let sweep = end_deg - start_deg;
    let segs = arc_segments_for(outer_r.max(inner_r), sweep);
    let mut out = arc_points(center, outer_r, outer_r, start_deg, end_deg, segs, false);
    // 内半径が 0 なら扇形（中心 1 点で閉じる）
    if inner_r <= POINT_EPSILON {
        out.push(center);
    } else {
        out.extend(arc_points(
            center, inner_r, inner_r, end_deg, start_deg, segs, false,
        ));
    }
    out
}

// ─── 角丸 ────────────────────────────────────────────────────

/// 多角形の各頂点を半径 `radius` で丸めた輪郭を返す。
///
/// 隣接辺の長さの半分を超える半径は自動的に切り詰める（辺がめり込まない）。
/// `radius <= 0` のときは入力をそのまま返す。
pub fn round_corners(pts: &[[f32; 2]], radius: f32) -> Vec<[f32; 2]> {
    let n = pts.len();
    if radius <= POINT_EPSILON || n < 3 {
        return pts.to_vec();
    }
    let mut out: Vec<[f32; 2]> = Vec::with_capacity(n * 8);
    for i in 0..n {
        let prev = pts[(i + n - 1) % n];
        let cur = pts[i];
        let next = pts[(i + 1) % n];
        let (Some(d_in), Some(d_out)) = (normalize(sub(cur, prev)), normalize(sub(next, cur)))
        else {
            out.push(cur);
            continue;
        };
        let len_in = (sub(cur, prev)[0].powi(2) + sub(cur, prev)[1].powi(2)).sqrt();
        let len_out = (sub(next, cur)[0].powi(2) + sub(next, cur)[1].powi(2)).sqrt();
        let r = radius.min(len_in * 0.5).min(len_out * 0.5);
        // 角の折れ角（0 に近い = 一直線 → 丸めない）
        let turn = cross(d_in, d_out);
        if turn.abs() <= POINT_EPSILON || r <= POINT_EPSILON {
            out.push(cur);
            continue;
        }
        // 接点（角から各辺方向へ r 戻る／進む）
        let p_start = [cur[0] - d_in[0] * r, cur[1] - d_in[1] * r];
        let p_end = [cur[0] + d_out[0] * r, cur[1] + d_out[1] * r];
        // 円弧の中心 = 接点 p_start から「曲がる側」の法線方向へ r。
        // 曲がる向き（turn の符号）に応じて d_in の左法線 / 右法線を選ぶ。
        let sign = if turn > 0.0 { 1.0 } else { -1.0 };
        let c = [
            p_start[0] - d_in[1] * r * sign,
            p_start[1] + d_in[0] * r * sign,
        ];
        let a0 = (p_start[1] - c[1]).atan2(p_start[0] - c[0]);
        let a1 = (p_end[1] - c[1]).atan2(p_end[0] - c[0]);
        // 進む向き（sign）に合わせて角度差を正規化する
        let mut delta = a1 - a0;
        while delta > std::f32::consts::PI {
            delta -= std::f32::consts::TAU;
        }
        while delta < -std::f32::consts::PI {
            delta += std::f32::consts::TAU;
        }
        let segs = arc_segments_for(r, delta.to_degrees());
        for s in 0..=segs {
            let t = s as f32 / segs as f32;
            let a = a0 + delta * t;
            out.push([c[0] + a.cos() * r, c[1] + a.sin() * r]);
        }
    }
    out
}

// ─── ベジエ ──────────────────────────────────────────────────

/// 3 次ベジエ曲線をサンプリングした点列を返す（点数 = segments + 1）。
pub fn bezier_points(
    p0: [f32; 2],
    p1: [f32; 2],
    p2: [f32; 2],
    p3: [f32; 2],
    segments: u32,
) -> Vec<[f32; 2]> {
    let segs = segments.clamp(BEZIER_MIN_SEGMENTS, BEZIER_MAX_SEGMENTS);
    (0..=segs)
        .map(|i| {
            let t = i as f32 / segs as f32;
            let u = 1.0 - t;
            let (b0, b1, b2, b3) = (
                u * u * u,
                3.0 * u * u * t,
                3.0 * u * t * t,
                t * t * t,
            );
            [
                p0[0] * b0 + p1[0] * b1 + p2[0] * b2 + p3[0] * b3,
                p0[1] * b0 + p1[1] * b1 + p2[1] * b2 + p3[1] * b3,
            ]
        })
        .collect()
}

// ─── コマンド → メッシュ ─────────────────────────────────────

/// 描画コマンド 1 件を三角形メッシュへ変換する。
///
/// - `feather`: アンチエイリアス帯の幅（描画空間 px。0 で無効）。
/// - 点数上限（`MAX_POINTS_PER_PRIMITIVE`）を超える点列は切り詰める。
/// - 描けない指定（点が足りない・半径 0 等）は空メッシュを返す（無視される）。
pub fn tessellate(cmd: &PrimitiveCommand, feather: f32) -> Mesh2d {
    let mut mesh = Mesh2d::new();
    // SRT 適用済みの入力点（上限で切り詰め）
    let src: Vec<[f32; 2]> = cmd
        .points
        .iter()
        .take(MAX_POINTS_PER_PRIMITIVE)
        .map(|&p| cmd.srt.apply(p))
        .collect();
    let outline = cmd.mode == PrimitiveDrawMode::Outline;
    let thickness = cmd.thickness;

    // 図形ごとの「閉輪郭」または「折れ線」を作って共通処理へ渡す。
    match cmd.kind {
        // ── 任意多角形（Rect / Triangle / Polygon）──
        PrimitiveKind::Polygon => {
            emit_closed(&mut mesh, &src, outline, thickness, feather);
        }
        // ── 角丸多角形 ──
        PrimitiveKind::RoundedRect => {
            let rounded = round_corners(&dedup_points(&src, true), cmd.extras[0]);
            emit_closed(&mut mesh, &rounded, outline, thickness, feather);
        }
        // ── 折れ線（Line 含む）──
        PrimitiveKind::Polyline => {
            let closed = cmd.extras[0] >= 0.5;
            stroke_polyline(&mut mesh, &src, closed, thickness, feather);
        }
        // ── 円・楕円 ──
        PrimitiveKind::Circle => {
            let Some(&center) = src.first() else {
                return mesh;
            };
            let (rx, ry) = (cmd.extras[0] * cmd.extras[1], cmd.extras[0] * cmd.extras[2]);
            if rx.abs() <= POINT_EPSILON || ry.abs() <= POINT_EPSILON {
                return mesh;
            }
            let segs = arc_segments_for(rx.abs().max(ry.abs()), 360.0);
            // 360° ちょうどだと始点と終点が重なるので最後の 1 点を落とす
            let mut c = arc_points(center, rx, ry, 0.0, 360.0, segs, false);
            c.pop();
            emit_closed(&mut mesh, &c, outline, thickness, feather);
        }
        // ── 正多角形 ──
        PrimitiveKind::RegularPolygon => {
            let Some(&center) = src.first() else {
                return mesh;
            };
            let radius = cmd.extras[0];
            let verts = (cmd.extras[1] as i64).max(POLY_MIN_VERTICES as i64) as u32;
            let rot = cmd.extras[2].to_radians();
            let (sx, sy) = (cmd.extras[3], cmd.extras[4]);
            if radius.abs() <= POINT_EPSILON {
                return mesh;
            }
            let c: Vec<[f32; 2]> = (0..verts)
                .map(|i| {
                    let a = rot + std::f32::consts::TAU * (i as f32) / (verts as f32);
                    [
                        center[0] + a.cos() * radius * sx,
                        center[1] + a.sin() * radius * sy,
                    ]
                })
                .collect();
            emit_closed(&mut mesh, &c, outline, thickness, feather);
        }
        // ── リング（円環セクタ）──
        PrimitiveKind::Ring => {
            let Some(&center) = src.first() else {
                return mesh;
            };
            let (inner, outer) = (cmd.extras[0].min(cmd.extras[1]), cmd.extras[0].max(cmd.extras[1]));
            if outer <= POINT_EPSILON {
                return mesh;
            }
            let c = ring_contour(center, inner, outer, cmd.extras[2], cmd.extras[3]);
            emit_closed(&mut mesh, &c, outline, thickness, feather);
        }
        // ── 円弧 ──
        PrimitiveKind::Arc => {
            let Some(&center) = src.first() else {
                return mesh;
            };
            let (radius, start, end) = (cmd.extras[0], cmd.extras[1], cmd.extras[2]);
            if radius.abs() <= POINT_EPSILON {
                return mesh;
            }
            let segs = arc_segments_for(radius, end - start);
            if outline {
                // 輪郭モード: 半径 radius の弧を太さ thickness の線で描く
                let pl = arc_points(center, radius, radius, start, end, segs, false);
                stroke_polyline(&mut mesh, &pl, false, thickness, feather);
            } else {
                // 塗りモード: 太さ thickness のリング（radius を帯の中心とする）
                let half = thickness.max(MIN_THICKNESS) * 0.5;
                let c = ring_contour(center, (radius - half).max(0.0), radius + half, start, end);
                fill_contour(&mut mesh, &c, feather);
            }
        }
        // ── 3 次ベジエ（常に線）──
        PrimitiveKind::Bezier => {
            if src.len() < 4 {
                return mesh;
            }
            let pl = bezier_points(src[0], src[1], src[2], src[3], cmd.extras[0] as u32);
            stroke_polyline(&mut mesh, &pl, false, thickness, feather);
        }
    }
    mesh
}

/// 閉輪郭を Fill / Outline のどちらかで出力する共通処理。
fn emit_closed(
    mesh: &mut Mesh2d,
    contour: &[[f32; 2]],
    outline: bool,
    thickness: f32,
    feather: f32,
) {
    if contour.len() < 3 {
        return;
    }
    if outline {
        stroke_polyline(mesh, contour, true, thickness, feather);
    } else {
        fill_contour(mesh, contour, feather);
    }
}

// ============================================================
//  ユニットテスト（GPU 不要の純粋幾何）
// ============================================================

#[cfg(test)]
mod tests {
    use super::super::queue::{Transform2d, PRIM_EXTRA_FLOATS};
    use super::*;

    /// テスト用コマンドの雛形。
    fn cmd(kind: PrimitiveKind) -> PrimitiveCommand {
        PrimitiveCommand {
            kind,
            space: None,
            color: [1.0, 1.0, 1.0, 1.0],
            mode: PrimitiveDrawMode::Fill,
            thickness: 2.0,
            layer: 0,
            srt: Transform2d::IDENTITY,
            extras: [0.0; PRIM_EXTRA_FLOATS],
            points: Vec::new(),
        }
    }

    /// 円の分割数は半径に応じて増え、上下限でクランプされる。
    #[test]
    fn primitive_circle_segment_count_scales_with_radius() {
        // 極小半径 → 最小分割数
        assert_eq!(arc_segments_for(0.1, 360.0), ARC_MIN_SEGMENTS);
        // 巨大半径 → 最大分割数
        assert_eq!(arc_segments_for(100000.0, 360.0), ARC_MAX_SEGMENTS);
        // 中間: 半径 100 の全周（弧長 628.3px）を 4px 刻み → ceil(157.08) = 158
        assert_eq!(arc_segments_for(100.0, 360.0), 158);
        // 分割数は単調非減少
        assert!(arc_segments_for(50.0, 360.0) <= arc_segments_for(200.0, 360.0));
    }

    /// 円（Fill）の三角形数は「分割数 - 2（本体）+ 帯」で分割数に比例する。
    #[test]
    fn primitive_circle_tessellation_is_non_empty() {
        let mut c = cmd(PrimitiveKind::Circle);
        c.points = vec![[0.0, 0.0]];
        c.extras[0] = 50.0; // 半径
        c.extras[1] = 1.0; // scale x
        c.extras[2] = 1.0; // scale y
        let mesh = tessellate(&c, 1.0);
        let segs = arc_segments_for(50.0, 360.0) as usize;
        // 本体 = (頂点数 - 2) 枚。頂点数は segs（末尾の重複点を落とす）。
        assert!(mesh.triangle_count() >= segs - 2);
        // フェザー帯（辺ごとに 2 枚）も乗るので本体だけより多い
        assert!(mesh.triangle_count() > segs);
    }

    /// 線（2 点の Polyline）はフェザー無しなら矩形 = 三角形 2 枚になる。
    #[test]
    fn primitive_line_quad_geometry() {
        let mut c = cmd(PrimitiveKind::Polyline);
        c.points = vec![[0.0, 0.0], [10.0, 0.0]];
        c.thickness = 4.0;
        let mesh = tessellate(&c, 0.0);
        assert_eq!(mesh.triangle_count(), 2, "矩形は三角形 2 枚");
        // 太さ 4 の水平線 → y は ±2 の範囲に収まる
        let max_y = mesh.verts.iter().fold(f32::MIN, |m, v| m.max(v.pos[1]));
        let min_y = mesh.verts.iter().fold(f32::MAX, |m, v| m.min(v.pos[1]));
        assert!((max_y - 2.0).abs() < 1e-4, "max_y={max_y}");
        assert!((min_y + 2.0).abs() < 1e-4, "min_y={min_y}");
        // x は 0..10
        let max_x = mesh.verts.iter().fold(f32::MIN, |m, v| m.max(v.pos[0]));
        assert!((max_x - 10.0).abs() < 1e-4, "max_x={max_x}");
    }

    /// 円弧の点列は開始角・終了角の範囲に収まり、端点が指定角に一致する。
    #[test]
    fn primitive_arc_angle_range() {
        let pts = arc_points([0.0, 0.0], 10.0, 10.0, 0.0, 90.0, 8, false);
        assert_eq!(pts.len(), 9, "分割数 + 1 点");
        // 始点 = 角度 0 → (10, 0)
        assert!((pts[0][0] - 10.0).abs() < 1e-3 && pts[0][1].abs() < 1e-3);
        // 終点 = 角度 90 → (0, 10)（画面座標系は Y 下向きなので下方向）
        let last = *pts.last().unwrap();
        assert!(last[0].abs() < 1e-3 && (last[1] - 10.0).abs() < 1e-3);
        // 全点が半径 10 の円周上にある
        for p in &pts {
            let r = (p[0] * p[0] + p[1] * p[1]).sqrt();
            assert!((r - 10.0).abs() < 1e-3, "r={r}");
        }
        // 全点が第 1 象限（0..90°）に収まる
        assert!(pts.iter().all(|p| p[0] >= -1e-3 && p[1] >= -1e-3));
    }

    /// 凸多角形（正方形）の三角形分割は 2 枚。
    #[test]
    fn primitive_polygon_triangulation_convex() {
        let square = [[0.0, 0.0], [10.0, 0.0], [10.0, 10.0], [0.0, 10.0]];
        let tris = triangulate_ear_clip(&square);
        assert_eq!(tris.len(), 2, "n 角形は n-2 枚");
    }

    /// 凹多角形（L 字 = 6 頂点）の三角形分割は 4 枚で、面積が保存される。
    #[test]
    fn primitive_polygon_triangulation_concave() {
        // L 字（面積 = 10*10 - 5*5 = 75）
        let l = [
            [0.0, 0.0],
            [10.0, 0.0],
            [10.0, 5.0],
            [5.0, 5.0],
            [5.0, 10.0],
            [0.0, 10.0],
        ];
        let tris = triangulate_ear_clip(&l);
        assert_eq!(tris.len(), 4, "6 角形は 4 枚");
        // 三角形面積の総和が元の多角形面積と一致する（凹でも欠けない）
        let total: f32 = tris
            .iter()
            .map(|t| {
                let (a, b, c) = (l[t[0] as usize], l[t[1] as usize], l[t[2] as usize]);
                (cross(sub(b, a), sub(c, a)) * 0.5).abs()
            })
            .sum();
        assert!((total - 75.0).abs() < 1e-3, "total={total}");
    }

    /// 角丸は元の頂点数より多い輪郭を返し、元の外接範囲を超えない。
    #[test]
    fn primitive_rounded_rect_stays_in_bounds() {
        let rect = [[0.0, 0.0], [100.0, 0.0], [100.0, 50.0], [0.0, 50.0]];
        let rounded = round_corners(&rect, 10.0);
        assert!(rounded.len() > rect.len());
        for p in &rounded {
            assert!(p[0] >= -1e-3 && p[0] <= 100.001, "x={}", p[0]);
            assert!(p[1] >= -1e-3 && p[1] <= 50.001, "y={}", p[1]);
        }
    }

    /// ベジエは始点・終点が制御点 p0 / p3 に一致する。
    #[test]
    fn primitive_bezier_endpoints() {
        let pts = bezier_points([0.0, 0.0], [0.0, 10.0], [10.0, 10.0], [10.0, 0.0], 16);
        assert_eq!(pts.len(), 17);
        assert!(pts[0][0].abs() < 1e-5 && pts[0][1].abs() < 1e-5);
        let last = *pts.last().unwrap();
        assert!((last[0] - 10.0).abs() < 1e-5 && last[1].abs() < 1e-5);
    }

    /// 点が足りない指定は空メッシュ（描画されない）。
    #[test]
    fn primitive_degenerate_inputs_are_empty() {
        // 2 点しかない多角形
        let mut c = cmd(PrimitiveKind::Polygon);
        c.points = vec![[0.0, 0.0], [1.0, 1.0]];
        assert!(tessellate(&c, 1.0).is_empty());
        // 半径 0 の円
        let mut c2 = cmd(PrimitiveKind::Circle);
        c2.points = vec![[0.0, 0.0]];
        c2.extras = [0.0, 1.0, 1.0, 0.0, 0.0];
        assert!(tessellate(&c2, 1.0).is_empty());
        // 点列が空の折れ線
        let c3 = cmd(PrimitiveKind::Polyline);
        assert!(tessellate(&c3, 1.0).is_empty());
    }

    /// SRT が点列へ適用される（平行移動）。
    #[test]
    fn primitive_srt_is_applied_to_points() {
        let mut c = cmd(PrimitiveKind::Polygon);
        c.points = vec![[0.0, 0.0], [10.0, 0.0], [0.0, 10.0]];
        c.srt = Transform2d {
            position: [100.0, 200.0],
            rotation_deg: 0.0,
            scale: [1.0, 1.0],
        };
        let mesh = tessellate(&c, 0.0);
        assert!(mesh.verts.iter().all(|v| v.pos[0] >= 99.9 && v.pos[1] >= 199.9));
    }
}
