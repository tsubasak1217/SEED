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
/// 全周の角度（度）。
const FULL_CIRCLE_DEG: f32 = 360.0;
/// 「全周とみなす」角度の許容誤差（度）。359.999° 等を全周として扱う。
const FULL_CIRCLE_EPS_DEG: f32 = 1e-3;

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

    push_contour_feather(mesh, &pts, feather);
}

/// 閉輪郭の外側へ幅 `feather` のアンチエイリアス帯（alpha 1 → 0）と、凸の角の隙間を塞ぐ三角形を張る
/// （`fill_contour` と W2-8 の `fill_convex` が共有する。`pts` は重複点を除いた 3 点以上）。
fn push_contour_feather(mesh: &mut Mesh2d, pts: &[[f32; 2]], feather: f32) {
    // ── フェザー帯（外向き法線へ feather だけ押し出し、alpha 1 → 0）──
    // 巻き方向によって「外側」が反転するため面積の符号で補正する。
    let flip = if signed_area(pts) >= 0.0 { 1.0 } else { -1.0 };
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

// ─── 軽い三角形分割（W2-8。見た目の拡張つきの図形だけ）──────────────

/// つなぎの扇の弦と円のずれの許容量（フェザーの幅＝画面の 1 画素に対する割合）。
const LEAN_JOIN_TOLERANCE: f32 = 0.25;
/// つなぎの扇の最大の分割数（太い線の 180° の折り返しで 8 枚）。
const LEAN_JOIN_MAX_SEGMENTS: u32 = 8;
/// 曲がっていないとみなす外積の大きさ（単位ベクトルどうし）。
const LEAN_STRAIGHT_EPSILON: f32 = 1e-4;

/// 2D の内積。
#[inline]
fn dot(a: [f32; 2], b: [f32; 2]) -> f32 {
    a[0] * b[0] + a[1] * b[1]
}

/// 点 + ベクトル × k。
#[inline]
fn offset(p: [f32; 2], v: [f32; 2], k: f32) -> [f32; 2] {
    [p[0] + v[0] * k, p[1] + v[1] * k]
}

/// つなぎの扇の分割数【純関数】: 弦と円のずれが許容量（フェザーの幅 × LEAN_JOIN_TOLERANCE。フェザーが無ければ 1 単位 × 同じ割合）
/// 以下になる最小の数（1〜LEAN_JOIN_MAX_SEGMENTS）。1 枚の扇の角度 θ の弦のずれは r(1 − cos(θ/2))。
/// 例: 太さ 2 dp（半径 1）・フェザー 1 画素（PC の 1 dp = 1 画素）で直角は 2 枚、太さ 8 の半径 4 で直角は 4 枚。
pub fn lean_join_segments(sweep: f32, radius: f32, feather: f32) -> u32 {
    let tolerance = LEAN_JOIN_TOLERANCE * if feather > 0.0 { feather } else { 1.0 };
    if !(radius > tolerance) || !(sweep > 0.0) {
        return 1;
    }
    let step = 2.0 * (1.0 - tolerance / radius).acos();
    ((sweep / step).ceil() as u32).clamp(1, LEAN_JOIN_MAX_SEGMENTS)
}

/// 輪郭が凸か（連続する辺の外積の符号がそろう。0 は問わない）。
pub fn is_convex(pts: &[[f32; 2]]) -> bool {
    let n = pts.len();
    if n < 3 {
        return false;
    }
    let mut sign = 0.0f32;
    for i in 0..n {
        let c = cross(sub(pts[(i + 1) % n], pts[i]), sub(pts[(i + 2) % n], pts[(i + 1) % n]));
        if c.abs() <= POINT_EPSILON {
            continue;
        }
        if sign == 0.0 {
            sign = c.signum();
        } else if c.signum() != sign {
            return false;
        }
    }
    sign != 0.0
}

/// 凸の輪郭を扇で塗る（耳刈りを使わない）。凸でなければ従来の `fill_contour`（耳刈り）。フェザーは `fill_contour` と同じ。
pub fn fill_convex(mesh: &mut Mesh2d, contour: &[[f32; 2]], feather: f32) {
    let pts = dedup_points(contour, true);
    if pts.len() < 3 {
        return;
    }
    if !is_convex(&pts) {
        fill_contour(mesh, &pts, feather);
        return;
    }
    // ── 本体（alpha = 1）: 頂点 0 からの扇 ──
    for i in 1..pts.len() - 1 {
        mesh.push_tri((pts[0], 1.0), (pts[i], 1.0), (pts[i + 1], 1.0));
    }
    if feather > 0.0 {
        push_contour_feather(mesh, &pts, feather);
    }
}

/// 帯の 1 枚（4 点。a→b が内側 alpha_ab、c→d が外側 alpha_cd）を三角形 2 枚で積む。
#[inline]
fn push_band(mesh: &mut Mesh2d, a: [f32; 2], b: [f32; 2], c: [f32; 2], d: [f32; 2], alpha_ab: f32, alpha_cd: f32) {
    mesh.push_tri((a, alpha_ab), (b, alpha_ab), (c, alpha_cd));
    mesh.push_tri((a, alpha_ab), (c, alpha_cd), (d, alpha_cd));
}

/// 折れ線を 1 本の帯として描く（見た目の拡張つきの折れ線。W2-8）。
///
/// - 線分ごと: 本体（太さ）の四角形 2 枚 + 両側のフェザーの帯 4 枚
/// - つなぎ: 曲がる**外側**だけに、太さの半分の半径の扇（角度 22.5° ごとに 1 枚・最大 8 枚）とそのフェザー。内側は隣の線分の本体が重なる
///   （不透明な線では見えない。半透明の太い線ではつなぎの内側がわずかに濃くなる＝従来の丸いつなぎと同じ性質）
/// - 開いた線の両端: 端から外へフェザーの帯（切りっぱなしの端）
/// 従来の `stroke_polyline`（線分ごとの四角形の耳刈り・つなぎごとの 8 角形の円）より三角形が少なく、点ごとの配列の確保が無い。
pub fn stroke_polyline_lean(mesh: &mut Mesh2d, points: &[[f32; 2]], closed: bool, thickness: f32, feather: f32) {
    let pts = dedup_points(points, closed);
    if pts.len() < 2 {
        return;
    }
    let half = thickness.max(MIN_THICKNESS) * 0.5;
    let n = pts.len();
    let seg_count = if closed { n } else { n - 1 };
    // ── 線分ごとの帯 ──
    for i in 0..seg_count {
        let (a, b) = (pts[i], pts[(i + 1) % n]);
        let Some(d) = normalize(sub(b, a)) else {
            continue;
        };
        let nrm = [-d[1], d[0]];
        let (al, bl, ar, br) = (offset(a, nrm, half), offset(b, nrm, half), offset(a, nrm, -half), offset(b, nrm, -half));
        push_band(mesh, al, bl, br, ar, 1.0, 1.0);
        if feather > 0.0 {
            push_band(mesh, al, bl, offset(bl, nrm, feather), offset(al, nrm, feather), 1.0, 0.0);
            push_band(mesh, ar, br, offset(br, nrm, -feather), offset(ar, nrm, -feather), 1.0, 0.0);
        }
    }
    // ── 曲がる外側の丸いつなぎ ──
    let joints = if closed { 0..n } else { 1..n.saturating_sub(1) };
    for i in joints {
        let p = pts[i];
        let (Some(d0), Some(d1)) = (normalize(sub(p, pts[(i + n - 1) % n])), normalize(sub(pts[(i + 1) % n], p))) else {
            continue;
        };
        let turn = cross(d0, d1);
        if turn.abs() <= LEAN_STRAIGHT_EPSILON && dot(d0, d1) > 0.0 {
            continue; // まっすぐ続く
        }
        // 外側: 曲がる向きの反対（法線 n = d を +90° 回したもの。turn > 0 なら +n の側へ曲がる → 外は −n）
        let side = if turn > 0.0 { -1.0 } else { 1.0 };
        let from = [-d0[1] * side, d0[0] * side];
        let to = [-d1[1] * side, d1[0] * side];
        let a0 = from[1].atan2(from[0]);
        let mut sweep = to[1].atan2(to[0]) - a0;
        // 外側を回る短い方の角度（折り返しは半周）
        while sweep > std::f32::consts::PI {
            sweep -= std::f32::consts::TAU;
        }
        while sweep < -std::f32::consts::PI {
            sweep += std::f32::consts::TAU;
        }
        let segs = lean_join_segments(sweep.abs(), half, feather);
        let mut prev_dir = from;
        for k in 1..=segs {
            let ang = a0 + sweep * k as f32 / segs as f32;
            let dir = [ang.cos(), ang.sin()];
            let (e0, e1) = (offset(p, prev_dir, half), offset(p, dir, half));
            mesh.push_tri((p, 1.0), (e0, 1.0), (e1, 1.0));
            if feather > 0.0 {
                push_band(mesh, e0, e1, offset(e1, dir, feather), offset(e0, prev_dir, feather), 1.0, 0.0);
            }
            prev_dir = dir;
        }
    }
    // ── 開いた線の両端のフェザー（端の外へ。角の隙間は三角形で塞ぐ）──
    if !closed && feather > 0.0 {
        for (p, q, outward) in [(pts[0], pts[1], -1.0f32), (pts[n - 1], pts[n - 2], -1.0f32)] {
            // q → p の向きが端の外（端の点 p から、隣の点 q と反対へ）
            let Some(d) = normalize(sub(q, p)) else {
                continue;
            };
            let out = [d[0] * outward, d[1] * outward];
            let nrm = [-d[1], d[0]];
            let (l, r) = (offset(p, nrm, half), offset(p, nrm, -half));
            let (lo, ro) = (offset(l, out, feather), offset(r, out, feather));
            push_band(mesh, l, r, ro, lo, 1.0, 0.0);
            mesh.push_tri((l, 1.0), (lo, 0.0), (offset(l, nrm, feather), 0.0));
            mesh.push_tri((r, 1.0), (ro, 0.0), (offset(r, nrm, -feather), 0.0));
        }
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

// ─── 線の下の塗り（W2-8。グラフの面）─────────────────────────

/// 2 点の和（a + b × k）。
#[inline]
fn add_scaled(a: [f32; 2], b: [f32; 2], k: f32) -> [f32; 2] {
    [a[0] + b[0] * k, a[1] + b[1] * k]
}

/// 線の下の塗り（`PrimitiveKind::Area`）: 折れ線 `points`（左 → 右）と基準線 y = `base_y` の間を塗る。
///
/// - 本体: 線分ごとに縦の台形（線分の 2 点と、その真下〈基準線の上〉の 2 点）を三角形 2 枚で張る。線分が基準線を
///   またぐときは交点で切り、基準線の上側と下側をそれぞれ三角形で塗る。耳刈り（`triangulate_ear_clip`）を使わないので、
///   点の数に比例する手間で済む（グラフの 365 点でも軽い）。
/// - アンチエイリアスの帯: 上の縁（基準線から遠い側へ押し出す）だけに張る。縁の折れ目で帯が開く側（次の線分が外側から
///   離れる向き＝山の頂）は三角形で塞ぐ。基準線と左右の端には帯を張らない（グラフでは軸と切り抜きの縁に重なる）。
pub fn fill_area(mesh: &mut Mesh2d, points: &[[f32; 2]], base_y: f32, feather: f32) {
    let pts = dedup_points(points, false);
    if pts.len() < 2 {
        return;
    }
    // 上の縁を「基準線の同じ側にある切れ端」に分ける（またぐ線分は交点で 2 つに切る）
    let mut pieces: Vec<([f32; 2], [f32; 2])> = Vec::with_capacity(pts.len());
    for w in pts.windows(2) {
        let (a, b) = (w[0], w[1]);
        let (da, db) = (a[1] - base_y, b[1] - base_y);
        if da * db < 0.0 {
            let t = da / (da - db);
            let c = [a[0] + (b[0] - a[0]) * t, base_y];
            pieces.push((a, c));
            pieces.push((c, b));
        } else {
            pieces.push((a, b));
        }
    }
    // ── 本体（alpha = 1）: 切れ端ごとに縦の台形（交点で切れた切れ端は三角形に潰れる）──
    for &(a, b) in &pieces {
        let (a0, b0) = ([a[0], base_y], [b[0], base_y]);
        mesh.push_tri((a, 1.0), (b, 1.0), (b0, 1.0));
        mesh.push_tri((a, 1.0), (b0, 1.0), (a0, 1.0));
    }
    if feather <= 0.0 {
        return;
    }
    // ── 上の縁のフェザー帯（alpha 1 → 0。基準線から遠い側へ押し出す）──
    // 前の切れ端の (終点, 外向きの法線, 基準線の上側か)
    let mut prev: Option<([f32; 2], [f32; 2], bool)> = None;
    for &(a, b) in &pieces {
        let Some(d) = normalize(sub(b, a)) else {
            continue;
        };
        // 画面は Y 下向き: 基準線より上（y が小さい）の切れ端は上向き（n.y < 0）が外、下の切れ端は下向きが外
        let above = (a[1] + b[1]) * 0.5 <= base_y;
        let mut n = [d[1], -d[0]];
        if (above && n[1] > 0.0) || (!above && n[1] < 0.0) {
            n = [-n[0], -n[1]];
        }
        let (ao, bo) = (add_scaled(a, n, feather), add_scaled(b, n, feather));
        mesh.push_tri((a, 1.0), (b, 1.0), (bo, 0.0));
        mesh.push_tri((a, 1.0), (bo, 0.0), (ao, 0.0));
        // 折れ目の隙間: 同じ側で続き、この切れ端が前の切れ端の外側から離れる向き（山の頂）なら三角形で塞ぐ
        if let Some((prev_end, prev_n, prev_above)) = prev {
            let same_point = (prev_end[0] - a[0]).abs() < POINT_EPSILON && (prev_end[1] - a[1]).abs() < POINT_EPSILON;
            if same_point && prev_above == above && d[0] * prev_n[0] + d[1] * prev_n[1] < 0.0 {
                mesh.push_tri((a, 1.0), (add_scaled(a, prev_n, feather), 0.0), (ao, 0.0));
            }
        }
        prev = Some((b, n, above));
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

/// 円環（リング）セクタの閉輪郭を返す。**部分リング（sweep < 360°）専用**。
///
/// 外周を start→end、内周を end→start と辿って 1 本の単純多角形にする。
///
/// 【全周で使ってはいけない理由】
/// sweep が 360° の場合、外周の終点と始点、内周の終点と始点がそれぞれ同一座標に
/// なるため、「外周リング＋幅ゼロのスリット＋内周リング」という**縮退した自己接触
/// 多角形**になる。これを耳刈り（`triangulate_ear_clip`）へ渡すと穴が認識されず、
/// 円盤全体が塗り潰される（＝リングが単色の円になる不具合の直接原因）。
/// 全周リングの塗りは `fill_ring_band()`（三角形ストリップ）を使うこと。
///
/// 安全弁として、全周が渡された場合も重複端点だけは落として返す
/// （それでもスリットは残るため、塗りには使わない）。
pub fn ring_contour(
    center: [f32; 2],
    inner_r: f32,
    outer_r: f32,
    start_deg: f32,
    end_deg: f32,
) -> Vec<[f32; 2]> {
    let sweep = end_deg - start_deg;
    let full = is_full_circle(sweep);
    let segs = arc_segments_for(outer_r.max(inner_r), sweep);
    let mut out = arc_points(center, outer_r, outer_r, start_deg, end_deg, segs, false);
    if full {
        out.pop(); // 全周では最終点が始点と重なるので落とす
    }
    // 内半径が 0 なら扇形（中心 1 点で閉じる）
    if inner_r <= POINT_EPSILON {
        if !full {
            out.push(center);
        }
    } else {
        let mut inner = arc_points(center, inner_r, inner_r, end_deg, start_deg, segs, false);
        if full {
            inner.pop();
        }
        out.extend(inner);
    }
    out
}

// ─── 円環（リング／円弧帯）の塗り ───────────────────────────

/// sweep（度）が全周とみなせるか。
///
/// 浮動小数の誤差で 359.9999 になった場合も全周として扱う。
#[inline]
fn is_full_circle(sweep_deg: f32) -> bool {
    sweep_deg.abs() >= FULL_CIRCLE_DEG - FULL_CIRCLE_EPS_DEG
}

/// 扇形（内半径 0 のリング）を塗る。
///
/// 全周なら「中心点を含まない閉じた円輪郭」、部分なら「中心点で閉じた扇形」を
/// `fill_contour` へ渡す（どちらも単純多角形なので耳刈りが正しく通る）。
fn fill_sector(
    mesh: &mut Mesh2d,
    center: [f32; 2],
    radius: f32,
    start_deg: f32,
    end_deg: f32,
    feather: f32,
) {
    let sweep = end_deg - start_deg;
    let segs = arc_segments_for(radius, sweep);
    if is_full_circle(sweep) {
        // 全周: 円の輪郭そのもの（終点は始点と重なるので落とす）
        let mut c = arc_points(center, radius, radius, start_deg, end_deg, segs, false);
        c.pop();
        fill_contour(mesh, &c, feather);
    } else {
        // 部分: 中心 → 弧 → （閉じる）で扇形
        let c = arc_points(center, radius, radius, start_deg, end_deg, segs, true);
        fill_contour(mesh, &c, feather);
    }
}

/// 円環の帯（inner_r < outer_r）を **三角形ストリップ** で塗る。
///
/// 耳刈りは「穴のある形状」を扱えないため、リングは内周／外周の対応点どうしを
/// 直接つないで四角形（＝三角形 2 枚）を並べる。これなら全周でも縮退しない。
///
/// アンチエイリアスのフェザー帯は **外周・内周の両方** に張る。押し出し方向は
/// 中心からの半径方向（外周は外向き、内周は内向き）なので、頂点ごとの法線が
/// 連続し、角の隙間を埋める処理が要らない。
/// 部分リング（sweep < 360°）では半径方向の 2 本の切り口（キャップ）にも
/// 接線方向のフェザー帯と角の三角形を張る。
fn fill_ring_band(
    mesh: &mut Mesh2d,
    center: [f32; 2],
    inner_r: f32,
    outer_r: f32,
    start_deg: f32,
    end_deg: f32,
    feather: f32,
) {
    let sweep = end_deg - start_deg;
    if sweep.abs() <= POINT_EPSILON || outer_r - inner_r <= POINT_EPSILON {
        return;
    }
    let full = is_full_circle(sweep);
    let segs = arc_segments_for(outer_r, sweep);
    // 各分割点の角度・半径方向（外向き単位ベクトル）を先に作る。
    let start = start_deg.to_radians();
    let end = end_deg.to_radians();
    let dirs: Vec<[f32; 2]> = (0..=segs)
        .map(|i| {
            let t = i as f32 / segs as f32;
            let a = start + (end - start) * t;
            [a.cos(), a.sin()]
        })
        .collect();
    // 半径 r・方向 d の座標。
    let at = |d: [f32; 2], r: f32| [center[0] + d[0] * r, center[1] + d[1] * r];

    // ── 本体（alpha = 1）: 区間ごとに四角形を 2 三角形で張る ──
    for i in 0..segs as usize {
        let (d0, d1) = (dirs[i], dirs[i + 1]);
        let (o0, o1) = (at(d0, outer_r), at(d1, outer_r));
        let (i0, i1) = (at(d0, inner_r), at(d1, inner_r));
        mesh.push_tri((o0, 1.0), (o1, 1.0), (i1, 1.0));
        mesh.push_tri((o0, 1.0), (i1, 1.0), (i0, 1.0));
    }
    if feather <= 0.0 {
        return;
    }
    // 内側のフェザーは内半径を超えて中心を跨がないよう制限する。
    let feather_in = feather.min(inner_r);

    // ── 外周・内周のフェザー帯（alpha 1 → 0）──
    for i in 0..segs as usize {
        let (d0, d1) = (dirs[i], dirs[i + 1]);
        // 外周: 半径方向の外向きへ押し出す
        let (o0, o1) = (at(d0, outer_r), at(d1, outer_r));
        let (o0f, o1f) = (at(d0, outer_r + feather), at(d1, outer_r + feather));
        mesh.push_tri((o0, 1.0), (o1, 1.0), (o1f, 0.0));
        mesh.push_tri((o0, 1.0), (o1f, 0.0), (o0f, 0.0));
        // 内周: 半径方向の内向きへ押し出す
        let (i0, i1) = (at(d0, inner_r), at(d1, inner_r));
        let (i0f, i1f) = (at(d0, inner_r - feather_in), at(d1, inner_r - feather_in));
        mesh.push_tri((i0, 1.0), (i1, 1.0), (i1f, 0.0));
        mesh.push_tri((i0, 1.0), (i1f, 0.0), (i0f, 0.0));
    }

    // ── 部分リングの切り口（キャップ）──
    if full {
        return;
    }
    // 掃引の向き（正 = 角度が増える方向）。キャップの外向きは接線の逆／順。
    let dir_sign = if sweep >= 0.0 { 1.0 } else { -1.0 };
    // (端点の半径方向, その端でのキャップ外向き) の 2 組
    let caps = [
        // 始端: 掃引の手前側 = 接線の逆向き
        (dirs[0], -dir_sign),
        // 終端: 掃引の先側 = 接線の順向き
        (dirs[segs as usize], dir_sign),
    ];
    for (d, tan_sign) in caps {
        // 接線（角度が増える向き）は半径方向を +90° 回したもの
        let out_n = [-d[1] * tan_sign, d[0] * tan_sign];
        let (o, ip) = (at(d, outer_r), at(d, inner_r));
        let of = [o[0] + out_n[0] * feather, o[1] + out_n[1] * feather];
        let ifp = [ip[0] + out_n[0] * feather, ip[1] + out_n[1] * feather];
        mesh.push_tri((o, 1.0), (ip, 1.0), (ifp, 0.0));
        mesh.push_tri((o, 1.0), (ifp, 0.0), (of, 0.0));
        // 角（キャップ帯と外周／内周帯の間）を三角形で塞ぐ
        let o_rad = at(d, outer_r + feather);
        mesh.push_tri((o, 1.0), (of, 0.0), (o_rad, 0.0));
        let i_rad = at(d, inner_r - feather_in);
        mesh.push_tri((ip, 1.0), (i_rad, 0.0), (ifp, 0.0));
    }
}

/// 円環を輪郭線（Outline）で描く。
///
/// 全周なら外周・内周をそれぞれ独立した閉じた折れ線として描く
/// （1 本の輪郭にすると半径方向に余計な線が 1 本入るため）。
/// 部分リングなら外周＋内周＋半径方向 2 辺を 1 本の閉輪郭として描く。
fn stroke_ring(
    mesh: &mut Mesh2d,
    center: [f32; 2],
    inner_r: f32,
    outer_r: f32,
    start_deg: f32,
    end_deg: f32,
    thickness: f32,
    feather: f32,
) {
    let sweep = end_deg - start_deg;
    if !is_full_circle(sweep) {
        let c = ring_contour(center, inner_r, outer_r, start_deg, end_deg);
        if c.len() >= 3 {
            stroke_polyline(mesh, &c, true, thickness, feather);
        }
        return;
    }
    // 全周: 外周と内周を別々の閉じた円として描く
    let segs = arc_segments_for(outer_r.max(inner_r), sweep);
    for r in [outer_r, inner_r] {
        if r <= POINT_EPSILON {
            continue;
        }
        let mut c = arc_points(center, r, r, start_deg, end_deg, segs, false);
        c.pop(); // 終点＝始点の重複を落としてから閉じる
        stroke_polyline(mesh, &c, true, thickness, feather);
    }
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
    // 見た目の拡張つき（W2-8）は軽い三角形分割。従来の図形は今までと同じ分割（頂点・見た目を変えない）
    let lean = cmd.style.extended;

    // 図形ごとの「閉輪郭」または「折れ線」を作って共通処理へ渡す。
    match cmd.kind {
        // ── 任意多角形（Rect / Triangle / Polygon）──
        PrimitiveKind::Polygon => {
            emit_closed(&mut mesh, &src, outline, thickness, feather, lean);
        }
        // ── 角丸多角形 ──
        PrimitiveKind::RoundedRect => {
            let rounded = round_corners(&dedup_points(&src, true), cmd.extras[0]);
            emit_closed(&mut mesh, &rounded, outline, thickness, feather, lean);
        }
        // ── 折れ線（Line 含む）──
        PrimitiveKind::Polyline => {
            let closed = cmd.extras[0] >= 0.5;
            if lean {
                stroke_polyline_lean(&mut mesh, &src, closed, thickness, feather);
            } else {
                stroke_polyline(&mut mesh, &src, closed, thickness, feather);
            }
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
            emit_closed(&mut mesh, &c, outline, thickness, feather, lean);
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
            emit_closed(&mut mesh, &c, outline, thickness, feather, lean);
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
            let (start, end) = (cmd.extras[2], cmd.extras[3]);
            if outline {
                stroke_ring(&mut mesh, center, inner, outer, start, end, thickness, feather);
            } else if inner <= POINT_EPSILON {
                // 内半径 0 → 扇形（従来どおり中心を含む輪郭を耳刈り）
                fill_sector(&mut mesh, center, outer, start, end, feather);
            } else {
                // 円環 → 耳刈りではなく内外周のストリップで塗る（全周でも縮退しない）
                fill_ring_band(&mut mesh, center, inner, outer, start, end, feather);
            }
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
                // 塗りモード: 太さ thickness のリング帯（radius を帯の中心とする）
                let half = thickness.max(MIN_THICKNESS) * 0.5;
                let inner = (radius - half).max(0.0);
                let outer = radius + half;
                if inner <= POINT_EPSILON {
                    // 帯が中心まで届く → 扇形として塗る
                    fill_sector(&mut mesh, center, outer, start, end, feather);
                } else {
                    fill_ring_band(&mut mesh, center, inner, outer, start, end, feather);
                }
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
        // ── 線の下の塗り（W2-8。基準線の y は SRT を掛けた描画空間の値として扱う＝グラフは SRT を使わない）──
        PrimitiveKind::Area => {
            let base_y = cmd.srt.apply([0.0, cmd.extras[0]])[1];
            fill_area(&mut mesh, &src, base_y, feather);
        }
    }
    mesh
}

/// 閉輪郭を Fill / Outline のどちらかで出力する共通処理。
///
/// `lean`（見た目の拡張つきの図形。W2-8）なら軽い三角形分割（輪郭の線は 1 本の帯、凸の塗りは扇）。
fn emit_closed(
    mesh: &mut Mesh2d,
    contour: &[[f32; 2]],
    outline: bool,
    thickness: f32,
    feather: f32,
    lean: bool,
) {
    if contour.len() < 3 {
        return;
    }
    match (outline, lean) {
        (true, false) => stroke_polyline(mesh, contour, true, thickness, feather),
        (true, true) => stroke_polyline_lean(mesh, contour, true, thickness, feather),
        (false, false) => fill_contour(mesh, contour, feather),
        (false, true) => fill_convex(mesh, contour, feather),
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
            style: Default::default(),
        }
    }

    /// 線の下の塗り（W2-8）: 本体の面積は折れ線と基準線の間の面積（台形の和）。帯は上の縁の外側だけ（本体を覆わない）。
    #[test]
    fn area_fill_covers_trapezoids_between_line_and_baseline() {
        let mut c = cmd(PrimitiveKind::Area);
        // 基準線 y = 100。山（y が小さいほど高い）: (0,60) → (10,20) → (20,60)
        c.points = vec![[0.0, 60.0], [10.0, 20.0], [20.0, 60.0]];
        c.extras[0] = 100.0;
        let mesh = tessellate(&c, 1.0);
        // 台形 2 つ: (40 + 80) / 2 × 10 × 2 = 1200
        assert!((solid_area(&mesh) - 1200.0).abs() < 1e-3, "面積 {}", solid_area(&mesh));
        // 帯の外縁（alpha 0）の点はすべて縁より上（基準線から遠い側）
        for v in mesh.verts.iter().filter(|v| v.alpha == 0.0) {
            assert!(v.pos[1] < 60.5, "帯は上の縁の外側だけ: {:?}", v.pos);
        }
        // 山の頂の隙間を塞ぐ三角形がある（帯 2 本 × 2 枚 + 頂 1 枚 = 5 枚の帯の三角形）
        let feather_tris = mesh.idx.chunks_exact(3).filter(|t| t.iter().any(|&i| mesh.verts[i as usize].alpha == 0.0)).count();
        assert_eq!(feather_tris, 5);
    }

    /// 基準線をまたぐ線分は交点で切り、上側と下側の両方を塗る（面積は三角形 2 つ）。
    #[test]
    fn area_fill_splits_segments_crossing_the_baseline() {
        let mut c = cmd(PrimitiveKind::Area);
        c.points = vec![[0.0, 80.0], [20.0, 120.0]];
        c.extras[0] = 100.0;
        let mesh = tessellate(&c, 0.0);
        // 交点 x = 10。上側: 底 10 × 高さ 20 / 2 = 100、下側も 100
        assert!((solid_area(&mesh) - 200.0).abs() < 1e-3, "面積 {}", solid_area(&mesh));
        assert!(mesh.verts.iter().all(|v| v.alpha == 1.0), "フェザー 0 なら帯は無い");
    }

    /// 軽い三角形分割（W2-8）: 本体の面積は従来と同じ（線分の四角形 + 外側のつなぎの扇）で、三角形はずっと少ない。
    #[test]
    fn lean_stroke_matches_area_with_fewer_triangles() {
        let mut legacy = cmd(PrimitiveKind::Polyline);
        legacy.points = (0..365).map(|i| [i as f32 * 1.25, 100.0 + ((i * 37) % 23) as f32]).collect();
        let mut lean = legacy.clone();
        lean.style = crate::engine::core::renderer::primitive2d::PrimitiveStyle { extended: true, ..Default::default() };
        let (a, b) = (tessellate(&legacy, 1.0), tessellate(&lean, 1.0));
        eprintln!("365 点の折れ線の三角形: 従来 {} / 軽い {}", a.triangle_count(), b.triangle_count());
        assert!(b.triangle_count() * 2 < a.triangle_count(), "三角形は半分未満: 従来 {} / 軽い {}", a.triangle_count(), b.triangle_count());
        // 本体（alpha 1）は線分の四角形の和 + つなぎの扇。直線の区間だけで比べる: まっすぐな 2 線分の本体の面積 = 長さ × 太さ
        let mut straight = cmd(PrimitiveKind::Polyline);
        straight.points = vec![[0.0, 0.0], [10.0, 0.0], [20.0, 0.0]];
        straight.style = lean.style;
        let m = tessellate(&straight, 0.0);
        assert!((solid_area(&m) - 20.0 * 2.0).abs() < 1e-3, "面積 {}", solid_area(&m));
    }

    /// 軽い三角形分割（W2-8）: 直角に曲がる外側は太さの半分の半径の四分円で塞がる（面積 = 2 線分 + 四分円の近似）。
    #[test]
    fn lean_stroke_fills_outer_corner_with_round_join() {
        let mut c = cmd(PrimitiveKind::Polyline);
        c.points = vec![[0.0, 0.0], [10.0, 0.0], [10.0, 10.0]];
        c.thickness = 4.0;
        c.style = crate::engine::core::renderer::primitive2d::PrimitiveStyle { extended: true, ..Default::default() };
        let m = tessellate(&c, 0.0);
        // 2 線分の四角形（10×4 × 2 = 80）+ 外側の四分円（π × 2² / 4 ≈ 3.14 を扇で近似）。内側の重なり（2×2）も面積に数える
        let segs = lean_join_segments(std::f32::consts::FRAC_PI_2, 2.0, 0.0);
        let fan = segs as f32 * 0.5 * 4.0 * (std::f32::consts::FRAC_PI_2 / segs as f32).sin();
        assert!((solid_area(&m) - (80.0 + fan)).abs() < 1e-3, "面積 {} / 期待 {}", solid_area(&m), 80.0 + fan);
        assert!(fan > 2.8 && fan < std::f32::consts::PI, "扇は四分円の内側で近い: {fan}");
        // 点ごとに 1 本の帯: 三角形は 2 線分 × 2 + 扇の枚数
        assert_eq!(m.triangle_count(), 4 + segs as usize);
    }

    /// つなぎの扇の分割数: 細い線は少なく、太い線・大きな角度ほど多い（上限 8）。
    #[test]
    fn lean_join_segments_follow_chord_error() {
        use std::f32::consts::{FRAC_PI_2, PI};
        assert_eq!(lean_join_segments(FRAC_PI_2, 1.0, 1.0), 2, "太さ 2・1 画素のフェザーで直角は 2 枚");
        assert_eq!(lean_join_segments(PI, 1.0, 1.0), 3, "折り返しは 3 枚");
        assert_eq!(lean_join_segments(FRAC_PI_2, 0.2, 1.0), 1, "半径が許容量以下なら 1 枚");
        assert_eq!(lean_join_segments(PI, 100.0, 1.0), 8, "上限 8");
        assert!(lean_join_segments(FRAC_PI_2, 4.0, 1.0) > lean_join_segments(FRAC_PI_2, 1.0, 1.0), "太いほど多い");
    }

    /// 凸の塗り（W2-8）は扇、凹は従来の耳刈り。面積は同じ。
    #[test]
    fn lean_fill_uses_fan_for_convex_and_ear_clip_otherwise() {
        let square = [[0.0, 0.0], [10.0, 0.0], [10.0, 10.0], [0.0, 10.0]];
        assert!(is_convex(&square));
        let concave = [[0.0, 0.0], [10.0, 0.0], [10.0, 10.0], [5.0, 3.0], [0.0, 10.0]];
        assert!(!is_convex(&concave));
        for shape in [&square[..], &concave[..]] {
            let (mut a, mut b) = (Mesh2d::new(), Mesh2d::new());
            fill_contour(&mut a, shape, 1.0);
            fill_convex(&mut b, shape, 1.0);
            assert!((solid_area(&a) - solid_area(&b)).abs() < 1e-3);
            assert_eq!(a.triangle_count(), b.triangle_count(), "同じ数（凸は扇、凹は耳刈りへ）");
        }
    }

    /// 1,000 点の塗りも点の数に比例する三角形の数（耳刈りを使わない）。
    #[test]
    fn area_fill_triangle_count_is_linear() {
        let mut c = cmd(PrimitiveKind::Area);
        c.points = (0..1000).map(|i| [i as f32, 50.0 + (i % 7) as f32]).collect();
        c.extras[0] = 100.0;
        let mesh = tessellate(&c, 1.0);
        // 本体 2 枚 + 帯 2 枚（+ 頂の塞ぎ最大 1 枚）/ 線分
        assert!(mesh.triangle_count() <= 999 * 5, "{}", mesh.triangle_count());
        assert!(mesh.triangle_count() >= 999 * 4);
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

    // ── リング（円環）のテスト用ヘルパ ──────────────────────

    /// alpha = 1 の三角形（＝塗りの本体。フェザー帯は除く）だけを取り出す。
    fn solid_triangles(mesh: &Mesh2d) -> Vec<([f32; 2], [f32; 2], [f32; 2])> {
        mesh.idx
            .chunks_exact(3)
            .filter_map(|t| {
                let (a, b, c) = (
                    mesh.verts[t[0] as usize],
                    mesh.verts[t[1] as usize],
                    mesh.verts[t[2] as usize],
                );
                // 3 頂点すべてが不透明なものだけが「本体」
                (a.alpha >= 1.0 && b.alpha >= 1.0 && c.alpha >= 1.0)
                    .then_some((a.pos, b.pos, c.pos))
            })
            .collect()
    }

    /// 本体三角形の面積合計。
    fn solid_area(mesh: &Mesh2d) -> f32 {
        solid_triangles(mesh)
            .iter()
            .map(|(a, b, c)| (cross(sub(*b, *a), sub(*c, *a)) * 0.5).abs())
            .sum()
    }

    /// リング用コマンドを作る。
    fn ring_cmd(inner: f32, outer: f32, start: f32, end: f32) -> PrimitiveCommand {
        let mut c = cmd(PrimitiveKind::Ring);
        c.points = vec![[0.0, 0.0]];
        c.extras[0] = inner;
        c.extras[1] = outer;
        c.extras[2] = start;
        c.extras[3] = end;
        c
    }

    /// 【回帰】全周リング（内 88 / 外 90）の塗りが中心を覆わないこと。
    ///
    /// 以前は「外周＋幅ゼロのスリット＋内周」という縮退多角形を耳刈りへ渡しており、
    /// 穴が認識されず円盤全体が塗られていた（レーダーが真っ黄色になる不具合）。
    #[test]
    fn ring_full_circle_fill_does_not_cover_center() {
        let mesh = tessellate(&ring_cmd(88.0, 90.0, 0.0, 360.0), 1.0);
        assert!(!mesh.is_empty(), "リングが空メッシュになっている");
        // どの三角形も中心 (0,0) を含まない
        for (a, b, c) in solid_triangles(&mesh) {
            assert!(
                !point_in_triangle([0.0, 0.0], a, b, c),
                "中心を覆う三角形がある: {a:?} {b:?} {c:?}"
            );
        }
        // 塗り面積 ≒ π(90² - 88²)
        let expect = std::f32::consts::PI * (90.0 * 90.0 - 88.0 * 88.0);
        let got = solid_area(&mesh);
        assert!(
            (got - expect).abs() / expect < 0.05,
            "面積が想定外: got={got} expect={expect}"
        );
    }

    /// リングセクタ（部分リング）の塗り面積が扇形帯の面積と一致する。
    #[test]
    fn ring_sector_fill_area() {
        // 90° ぶんのリング（内 40 / 外 50）
        let mesh = tessellate(&ring_cmd(40.0, 50.0, 0.0, 90.0), 1.0);
        let expect = std::f32::consts::PI * (50.0 * 50.0 - 40.0 * 40.0) * (90.0 / 360.0);
        let got = solid_area(&mesh);
        assert!(
            (got - expect).abs() / expect < 0.05,
            "面積が想定外: got={got} expect={expect}"
        );
        // 中心は塗られない
        for (a, b, c) in solid_triangles(&mesh) {
            assert!(!point_in_triangle([0.0, 0.0], a, b, c));
        }
    }

    /// 内半径 0 のリング（＝扇形）は中心を含み、面積が扇形と一致する。
    #[test]
    fn ring_zero_inner_is_sector() {
        let mesh = tessellate(&ring_cmd(0.0, 50.0, 0.0, 90.0), 1.0);
        let expect = std::f32::consts::PI * 50.0 * 50.0 * (90.0 / 360.0);
        let got = solid_area(&mesh);
        assert!((got - expect).abs() / expect < 0.05, "got={got}");
        // 扇形なので中心は覆われている
        assert!(solid_triangles(&mesh)
            .iter()
            .any(|(a, b, c)| point_in_triangle([0.0, 0.0], *a, *b, *c)));
    }

    /// Arc の Fill は「太さ thickness の帯」になり、中心を塗らない。
    #[test]
    fn arc_band_fill_area() {
        let mut c = cmd(PrimitiveKind::Arc);
        c.points = vec![[0.0, 0.0]];
        c.extras[0] = 60.0; // 半径（帯の中心）
        c.extras[1] = 0.0; // 開始角
        c.extras[2] = 360.0; // 終了角（全周）
        c.thickness = 4.0; // → 内 58 / 外 62
        let mesh = tessellate(&c, 1.0);
        let expect = std::f32::consts::PI * (62.0 * 62.0 - 58.0 * 58.0);
        let got = solid_area(&mesh);
        assert!(
            (got - expect).abs() / expect < 0.05,
            "面積が想定外: got={got} expect={expect}"
        );
        for (a, b, c3) in solid_triangles(&mesh) {
            assert!(!point_in_triangle([0.0, 0.0], a, b, c3), "中心が塗られた");
        }
    }

    /// 全周リングの Outline は外周・内周の 2 本を描き、半径方向の線を出さない。
    #[test]
    fn ring_full_circle_outline_has_no_radial_spoke() {
        let mut c = ring_cmd(40.0, 50.0, 0.0, 360.0);
        c.mode = PrimitiveDrawMode::Outline;
        c.thickness = 2.0;
        let mesh = tessellate(&c, 0.0);
        assert!(!mesh.is_empty());
        // 半径方向のスポークがあれば、内周(40)と外周(50)の中間半径(45 付近)に
        // 頂点が現れる。線の太さ 2（半径 ±1）を考慮しても 43..47 は空のはず。
        let mid = mesh.verts.iter().any(|v| {
            let r = (v.pos[0] * v.pos[0] + v.pos[1] * v.pos[1]).sqrt();
            (43.0..47.0).contains(&r)
        });
        assert!(!mid, "全周リングの輪郭に半径方向の線が入っている");
    }

    /// 円（Circle）の塗り輪郭には終点の重複が無い（面積が正しく出る）。
    #[test]
    fn circle_fill_contour_has_no_duplicate_endpoint() {
        let mut c = cmd(PrimitiveKind::Circle);
        c.points = vec![[0.0, 0.0]];
        c.extras = [50.0, 1.0, 1.0, 0.0, 0.0];
        let mesh = tessellate(&c, 1.0);
        let expect = std::f32::consts::PI * 50.0 * 50.0;
        let got = solid_area(&mesh);
        assert!((got - expect).abs() / expect < 0.05, "got={got}");
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
