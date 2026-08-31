// ============================================================
//  terrain/simplify.rs — チャンクメッシュのその場デシメート（頂点数削減）
//
//  【責務】
//    マーチングキューブスが出した 1 チャンクぶんの `TerrainMesh` を、
//    見た目をできるだけ保ったまま頂点数の少ないメッシュへ簡略化する。
//    エンジン非依存の純粋アルゴリズム層（GPU も ECS も密度場も触らない）。
//
//  【方式 — QEM（Quadric Error Metric）による "ハーフエッジ" コラプス】
//    Garland–Heckbert の二次誤差計量でエッジの潰しやすさを順位づけし、
//    誤差の小さいものから潰す。ただし **潰し先は必ず既存頂点のどちらか**にする
//    （最適点を新しく作る "フルエッジ" コラプスは採らない）。
//
//    ハーフエッジに限定する理由は 3 つあり、どれもこのエンジン固有の制約である。
//      1. 生き残る頂点がすべて **元の MC 頂点そのもの** になるため、
//         `TerrainVertexEdge`（頂点の由来辺）をそのまま引き継げる。
//         由来辺はレイヤペイント高速パスの唯一の手掛かりであり、
//         新しい位置の頂点を作ると由来辺が定義できず高速パスが死ぬ。
//      2. 同じ理由で法線・スプラット（paint / paint_amount）・法線シャープネスも
//         引き継げる。
//         とくに法線は密度勾配から作った高品位なもので、面から作り直すと
//         **隣接チャンクと境界頂点の法線が食い違い、陰影の継ぎ目が出る**。
//         元の値を持ち回ればその心配が原理的に無い。
//      3. 頂点位置が動かないので、境界頂点を「潰し先」に固定するだけで
//         継ぎ目の位置ずれがゼロになる（下記）。
//
//  【継ぎ目の保証（最重要）】
//    チャンク境界面（x=0 / x=extent / y=0 / y=extent / z=0 / z=extent）に載る頂点は
//    **ロック**して、潰される側（消える側）には決してしない。潰し先にはなれる。
//    MC の境界頂点は隣接チャンクと同じ密度サンプルから同じ位置に生まれるため、
//    「境界頂点集合が変わらない」＝「継ぎ目が開かない」がそのまま成り立つ。
//    ロック頂点どうしのエッジも潰さない（境界線上の頂点が減ると隣と食い違うため）。
//
//  【非多様体を作らないための 3 つのガード】
//    - リンク条件（頂点版）: a と b の 1 近傍が共有する頂点の数が、辺 (a,b) を含む三角形数と
//      一致するときだけ潰す。これを満たさない潰しは非多様体辺／穴を生む。
//    - 面重複ガード（リンク条件の辺版）: 潰した結果、既存の面と 3 頂点が完全に一致する面が
//      できるなら潰さない。頂点版のリンク条件は「共有されるのが辺」のケース
//      （四面体構成 {a, b, x, y}）を素通ししてしまい、そこを潰すと面が 2 枚重なる。
//      重なった面は組み直しで 1 枚に落とされるため、結果として**三角形 1 枚ぶんの穴**が開く。
//    - 法線反転ガード: 潰したあと、a に接していた三角形の法線が 90 度以上倒れるなら潰さない。
//
//  【この関数がしないこと】
//    密度場（SDF）は 1 ビットも触らない。デシメートはメッシュだけの操作であり、
//    再メッシュすれば元の密度から作り直される（＝非破壊）。
// ============================================================

use std::collections::{BinaryHeap, HashSet};

use super::marching_cubes::{TerrainMesh, orient_to_winding_convention};

/// 「頂点がチャンク境界面に載っている」と判定する許容誤差（extent に対する割合）。
///
/// 境界面上の MC 頂点は面に垂直な座標が厳密に 0.0 か extent になる（格子線上のため）。
/// f32 の積の誤差だけを吸収できればよいので、ごく小さな相対値で足りる。
const BOUNDARY_EPS_FRACTION: f32 = 1.0e-4;

/// 法線反転ガードのしきい値（潰す前後の面法線の内積の下限）。
///
/// 0.0 = 90 度。これを下回るほど倒れる潰しは、薄い三角形の裏返り（＝ちらつく黒面）を
/// 生むので却下する。1.0 に近づけるほど保守的（＝削減率が落ちる）。
const FLIP_GUARD_MIN_DOT: f32 = 0.0;

/// 縮退三角形とみなす面積の下限（m²）。これ未満の面は法線が定義できない。
const DEGENERATE_AREA_EPS: f32 = 1.0e-12;

/// 強度スライダー（0〜1）の上限で、除去対象頂点のうち何割まで潰しにいくか。
///
/// 1.0 にすると「潰せるものは全部潰す」になり、平坦チャンクが 1 枚のポリゴンまで
/// 落ちて陰影が破綻する。実用上の上限として 0.9 に留める（残り 1 割は形状の芯）。
const MAX_REMOVE_FRACTION: f32 = 0.9;

/// デシメート結果の要約（UI のステータス表示・テスト用）。
#[derive(Clone, Copy, Debug, PartialEq, Eq, Default)]
pub struct SimplifyStats {
    /// 入力の頂点数。
    pub vertices_before: usize,
    /// 出力の頂点数。
    pub vertices_after: usize,
    /// 入力の三角形数。
    pub triangles_before: usize,
    /// 出力の三角形数。
    pub triangles_after: usize,
}

impl SimplifyStats {
    /// 頂点の削減率（0.0〜1.0）。入力が空なら 0。
    pub fn vertex_reduction(&self) -> f32 {
        if self.vertices_before == 0 {
            return 0.0;
        }
        let removed = self.vertices_before.saturating_sub(self.vertices_after);
        removed as f32 / self.vertices_before as f32
    }
}

// ============================================================
//  二次誤差計量（Quadric）
// ============================================================

/// 対称 4x4 二次形式を上三角 10 要素で持つ（Garland–Heckbert の Q）。
///
/// 平面 (a,b,c,d)（a²+b²+c²=1）に対する Q = p·pᵀ。点 v の誤差は vᵀQv。
/// f64 で持つのは、面を数十枚足し込むと f32 では桁落ちして順位が崩れるため。
#[derive(Clone, Copy, Default)]
struct Quadric {
    /// [a², ab, ac, ad, b², bc, bd, c², cd, d²]
    m: [f64; 10],
}

impl Quadric {
    /// 平面 (a,b,c,d) から二次形式を作り、`weight`（面積）で重み付けする。
    fn from_plane(a: f64, b: f64, c: f64, d: f64, weight: f64) -> Self {
        let w = weight;
        Self {
            m: [
                a * a * w,
                a * b * w,
                a * c * w,
                a * d * w,
                b * b * w,
                b * c * w,
                b * d * w,
                c * c * w,
                c * d * w,
                d * d * w,
            ],
        }
    }

    /// 別の二次形式を足し込む（面の寄与を頂点へ集約する）。
    fn add(&mut self, other: &Quadric) {
        for i in 0..10 {
            self.m[i] += other.m[i];
        }
    }

    /// 点 v における二次誤差 vᵀQv を返す（常に非負のはず。数値誤差で負になったら 0 に丸める）。
    fn error_at(&self, v: [f32; 3]) -> f64 {
        let (x, y, z) = (v[0] as f64, v[1] as f64, v[2] as f64);
        let m = &self.m;
        let e = m[0] * x * x
            + 2.0 * m[1] * x * y
            + 2.0 * m[2] * x * z
            + 2.0 * m[3] * x
            + m[4] * y * y
            + 2.0 * m[5] * y * z
            + 2.0 * m[6] * y
            + m[7] * z * z
            + 2.0 * m[8] * z
            + m[9];
        e.max(0.0)
    }
}

// ============================================================
//  優先度つきコラプス候補
// ============================================================

/// 「頂点 `from` を頂点 `to` へ潰す」候補。誤差の小さい順に取り出す。
///
/// `stamp_*` は取り出したときに「その頂点が候補作成後に潰されていないか」を検査する
/// ための世代印。遅延削除（lazy deletion）方式なので、無効になった候補は
/// キューに残したまま取り出し時に捨てる。
#[derive(Clone, Copy, PartialEq)]
struct Candidate {
    cost: f32,
    from: u32,
    to: u32,
    stamp_from: u32,
    stamp_to: u32,
}

impl Eq for Candidate {}

impl Ord for Candidate {
    /// `BinaryHeap` は最大ヒープなので、**コストの小さいほうを「大きい」**と定義して
    /// 最小ヒープとして使う。NaN は現れない（誤差は非負有限）が、
    /// 万一に備えて `partial_cmp` の失敗は Equal に倒す（順序が壊れてもパニックさせない）。
    fn cmp(&self, other: &Self) -> std::cmp::Ordering {
        other
            .cost
            .partial_cmp(&self.cost)
            .unwrap_or(std::cmp::Ordering::Equal)
            // 同コストの決定性のため、頂点番号で最終順序を決める。
            .then_with(|| other.from.cmp(&self.from))
            .then_with(|| other.to.cmp(&self.to))
    }
}

impl PartialOrd for Candidate {
    fn partial_cmp(&self, other: &Self) -> Option<std::cmp::Ordering> {
        Some(self.cmp(other))
    }
}

// ============================================================
//  公開 API
// ============================================================

/// 頂点が「チャンク境界面に載っている」か（＝隣接チャンクと共有されうるか）を判定する。
///
/// `extent` はチャンク 1 軸の広がり（m）。X/Y/Z いずれかの座標が 0 か extent に一致すれば真。
/// 判定を公開しているのは、テストと呼び出し側が「境界頂点集合」を同じ定義で取れるようにするため。
pub fn is_boundary_vertex(pos: [f32; 3], extent: f32) -> bool {
    let eps = extent * BOUNDARY_EPS_FRACTION;
    (0..3).any(|k| pos[k].abs() <= eps || (pos[k] - extent).abs() <= eps)
}

/// チャンクメッシュを簡略化する（**入力は変更せず新しいメッシュを返す**）。
///
/// - `mesh`: 簡略化するチャンクメッシュ（チャンクローカル座標）。
/// - `extent`: チャンク 1 軸の広がり（m）。境界頂点のロック判定に使う。
/// - `strength`: 0.0〜1.0 の強度。0 以下なら**入力の複製をそのまま返す**（無操作）。
///
/// 戻り値は `(簡略化したメッシュ, 統計)`。
///
/// 【保証すること】
///   - `extent` の境界面に載る頂点は 1 つも消えず、位置も動かない。
///   - 出力インデックスは必ず出力頂点数の範囲内（整合）。
///   - 縮退三角形（同一頂点を 2 つ以上含む面）を出力しない。
///   - `positions` / `normals` / `paint` / `paint_amount` / `sharpness` / `edges` の長さは
///     常に一致する。
pub fn simplify_mesh(mesh: &TerrainMesh, extent: f32, strength: f32) -> (TerrainMesh, SimplifyStats) {
    let mut stats = SimplifyStats {
        vertices_before: mesh.positions.len(),
        vertices_after: mesh.positions.len(),
        triangles_before: mesh.triangle_count(),
        triangles_after: mesh.triangle_count(),
    };

    // ── 無操作で帰る条件（強度 0・空メッシュ・extent 不正）──
    if strength <= 0.0 || mesh.positions.is_empty() || mesh.indices.len() < 3 || !(extent > 0.0) {
        return (clone_mesh(mesh), stats);
    }

    let vcount = mesh.positions.len();
    let tcount = mesh.indices.len() / 3;

    // ── ① 三角形配列と頂点→三角形の隣接を作る ──
    let mut tris: Vec<[u32; 3]> = Vec::with_capacity(tcount);
    for t in mesh.indices.chunks_exact(3) {
        // 入力に範囲外インデックスが混ざっていたら簡略化せず素通しする（防御的）。
        if t[0] as usize >= vcount || t[1] as usize >= vcount || t[2] as usize >= vcount {
            return (clone_mesh(mesh), stats);
        }
        tris.push([t[0], t[1], t[2]]);
    }
    // 生存フラグ（潰しで縮退した三角形は false になる）。
    let mut tri_alive: Vec<bool> = vec![true; tris.len()];
    let mut vert_tris: Vec<Vec<u32>> = vec![Vec::new(); vcount];
    for (ti, t) in tris.iter().enumerate() {
        for &v in t {
            vert_tris[v as usize].push(ti as u32);
        }
    }

    // ── ② 境界ロック（チャンク境界面の頂点）と、開いた縁のロック ──
    //   開いた縁（そのエッジを 1 枚の三角形しか共有しない）は穴の輪郭であり、
    //   潰すと穴が広がる。地形チャンクでは基本的に現れないが、
    //   LOD スカート等が混ざったメッシュでも壊れないように保険をかける。
    let mut locked: Vec<bool> = mesh
        .positions
        .iter()
        .map(|p| is_boundary_vertex(*p, extent))
        .collect();
    {
        // 各無向辺が何枚の三角形に共有されるかを数える。
        let mut edge_count: std::collections::HashMap<(u32, u32), u32> =
            std::collections::HashMap::with_capacity(tris.len() * 3);
        for t in &tris {
            for k in 0..3 {
                let (a, b) = (t[k], t[(k + 1) % 3]);
                *edge_count.entry(ordered_pair(a, b)).or_insert(0) += 1;
            }
        }
        for (&(a, b), &n) in &edge_count {
            if n < 2 {
                locked[a as usize] = true;
                locked[b as usize] = true;
            }
        }
    }

    // ── ③ 頂点ごとの二次誤差（接する面の平面二次形式を面積重みで合算）──
    let mut quadrics: Vec<Quadric> = vec![Quadric::default(); vcount];
    for t in &tris {
        let Some((n, area)) = face_normal_area(&mesh.positions, *t) else {
            continue;
        };
        let p0 = mesh.positions[t[0] as usize];
        // 平面の d = -n·p0。
        let d = -(n[0] as f64 * p0[0] as f64 + n[1] as f64 * p0[1] as f64 + n[2] as f64 * p0[2] as f64);
        let q = Quadric::from_plane(n[0] as f64, n[1] as f64, n[2] as f64, d, area as f64);
        for &v in t {
            quadrics[v as usize].add(&q);
        }
    }

    // ── ④ 候補キューを作る（各無向辺につき、許される向きの潰しを積む）──
    let mut stamps: Vec<u32> = vec![0; vcount];
    let mut heap: BinaryHeap<Candidate> = BinaryHeap::new();
    {
        let mut seen: HashSet<(u32, u32)> = HashSet::with_capacity(tris.len() * 3);
        for t in &tris {
            for k in 0..3 {
                let pair = ordered_pair(t[k], t[(k + 1) % 3]);
                if seen.insert(pair) {
                    push_directed_candidates(
                        &mut heap, &quadrics, &locked, &stamps, &mesh.positions, pair.0, pair.1,
                    );
                }
            }
        }
    }

    // ── ⑤ 目標削減数を決める ──
    //   潰せるのはロックされていない頂点だけ。強度 1.0 でもその 9 割までに留める。
    let removable = locked.iter().filter(|l| !**l).count();
    let target_removed =
        ((removable as f32) * strength.clamp(0.0, 1.0) * MAX_REMOVE_FRACTION).floor() as usize;
    if target_removed == 0 {
        return (clone_mesh(mesh), stats);
    }

    // ── ⑥ 誤差の小さい順に潰す ──
    //   `collapsed_to[v]` が v 自身でなければ、v は潰されて別の頂点に成り代わっている。
    let mut collapsed_to: Vec<u32> = (0..vcount as u32).collect();
    let mut removed = 0usize;
    while removed < target_removed {
        let Some(cand) = heap.pop() else { break };
        let (from, to) = (cand.from as usize, cand.to as usize);
        // 世代印が古い候補（どちらかが既に潰された／近傍が変わった）は捨てる。
        if stamps[from] != cand.stamp_from || stamps[to] != cand.stamp_to {
            continue;
        }
        // 潰す側がロック済み／既に潰されているなら無効。
        if locked[from] || collapsed_to[from] != from as u32 || collapsed_to[to] != to as u32 {
            continue;
        }
        if !can_collapse(&tris, &tri_alive, &vert_tris, &mesh.positions, from as u32, to as u32) {
            continue;
        }

        // ── 潰しを実行する ──
        collapsed_to[from] = to as u32;
        let q_from = quadrics[from];
        quadrics[to].add(&q_from);
        removed += 1;

        // from に接していた三角形を to へ付け替え、縮退したものは殺す。
        let moved: Vec<u32> = std::mem::take(&mut vert_tris[from]);
        for ti in moved {
            let t = &mut tris[ti as usize];
            for slot in t.iter_mut() {
                if *slot == from as u32 {
                    *slot = to as u32;
                }
            }
            let t = tris[ti as usize];
            if t[0] == t[1] || t[1] == t[2] || t[2] == t[0] {
                // 辺 (from,to) を共有していた面が潰れて縮退した。
                tri_alive[ti as usize] = false;
            } else if !vert_tris[to].contains(&ti) {
                vert_tris[to].push(ti);
            }
        }
        // 死んだ三角形の参照を残る頂点側からも掃除する（近傍走査の精度を保つ）。
        vert_tris[to].retain(|&ti| tri_alive[ti as usize]);
        let ring: Vec<u32> = one_ring_vertices(&tris, &tri_alive, &vert_tris, to as u32);
        for &v in &ring {
            vert_tris[v as usize].retain(|&ti| tri_alive[ti as usize]);
        }

        // ── 世代印は from と to だけ進める ──
        //   潰しで二次誤差が変わるのはこの 2 頂点だけ（to へ Q が合算され、from は消える）。
        //   近傍頂点まで印を進めると、その頂点が絡む**まだ有効な候補**まで一斉に無効化され、
        //   作り直さない辺（近傍どうしの辺）の候補が永久に失われて削減が早期に止まる。
        stamps[from] = stamps[from].wrapping_add(1);
        stamps[to] = stamps[to].wrapping_add(1);
        // to まわりの辺だけ、新しい印とコストで積み直す。
        for &v in &ring {
            push_directed_candidates(
                &mut heap, &quadrics, &locked, &stamps, &mesh.positions, to as u32, v,
            );
        }
    }

    // ── ⑦ 生き残った三角形から新しいメッシュを組み直す ──
    let out = rebuild(mesh, &tris, &tri_alive);
    stats.vertices_after = out.positions.len();
    stats.triangles_after = out.triangle_count();
    (out, stats)
}

// ============================================================
//  内部ヘルパ
// ============================================================

/// 無向辺を (小さい方, 大きい方) の正準形にする。
#[inline]
fn ordered_pair(a: u32, b: u32) -> (u32, u32) {
    if a <= b { (a, b) } else { (b, a) }
}

/// 三角形の面法線（単位）と面積を返す。縮退面は `None`。
fn face_normal_area(positions: &[[f32; 3]], t: [u32; 3]) -> Option<([f32; 3], f32)> {
    let a = positions[t[0] as usize];
    let b = positions[t[1] as usize];
    let c = positions[t[2] as usize];
    let ab = [b[0] - a[0], b[1] - a[1], b[2] - a[2]];
    let ac = [c[0] - a[0], c[1] - a[1], c[2] - a[2]];
    let cr = [
        ab[1] * ac[2] - ab[2] * ac[1],
        ab[2] * ac[0] - ab[0] * ac[2],
        ab[0] * ac[1] - ab[1] * ac[0],
    ];
    let len2 = cr[0] * cr[0] + cr[1] * cr[1] + cr[2] * cr[2];
    if len2 <= DEGENERATE_AREA_EPS {
        return None;
    }
    let len = len2.sqrt();
    Some(([cr[0] / len, cr[1] / len, cr[2] / len], 0.5 * len))
}

/// 頂点 v に接する**生存**三角形の頂点集合から、v 自身を除いたもの（1 近傍）。
///
/// `vert_tris` には潰しで死んだ三角形の番号が残りうるので、必ず `tri_alive` で濾す。
/// 濾さないと消えた面の頂点まで近傍に数えてしまい、リンク条件の判定が狂う。
fn one_ring_vertices(
    tris: &[[u32; 3]],
    tri_alive: &[bool],
    vert_tris: &[Vec<u32>],
    v: u32,
) -> Vec<u32> {
    let mut out: Vec<u32> = Vec::new();
    for &ti in &vert_tris[v as usize] {
        if !tri_alive[ti as usize] {
            continue;
        }
        for &w in &tris[ti as usize] {
            if w != v && !out.contains(&w) {
                out.push(w);
            }
        }
    }
    out
}

/// 辺 (a,b) について、許される向きの潰し候補をキューへ積む。
///
/// - 両端ロック → 積まない（境界線上の頂点を減らさない）。
/// - 片方だけロック → 「ロックされていない側 → ロック側」の 1 方向だけ。
/// - どちらも自由 → 両方向を積み、コストの小さいほうが先に取り出される。
fn push_directed_candidates(
    heap: &mut BinaryHeap<Candidate>,
    quadrics: &[Quadric],
    locked: &[bool],
    stamps: &[u32],
    positions: &[[f32; 3]],
    a: u32,
    b: u32,
) {
    if a == b {
        return;
    }
    let (ai, bi) = (a as usize, b as usize);
    // 潰し a→b のコスト = (Qa + Qb) を b の位置で評価した値（Garland–Heckbert）。
    let mut push = |from: u32, to: u32| {
        if locked[from as usize] {
            return;
        }
        let mut q = quadrics[from as usize];
        q.add(&quadrics[to as usize]);
        let cost = q.error_at(positions[to as usize]) as f32;
        if !cost.is_finite() {
            return;
        }
        heap.push(Candidate {
            cost,
            from,
            to,
            stamp_from: stamps[from as usize],
            stamp_to: stamps[to as usize],
        });
    };
    if locked[ai] && locked[bi] {
        return;
    }
    push(a, b);
    push(b, a);
}

/// 頂点 `from` を `to` へ潰してよいか（非多様体化・面重複・法線反転を防ぐ 3 つのガード）。
fn can_collapse(
    tris: &[[u32; 3]],
    tri_alive: &[bool],
    vert_tris: &[Vec<u32>],
    positions: &[[f32; 3]],
    from: u32,
    to: u32,
) -> bool {
    // ── ガード 1: リンク条件 ──
    //   a と b の 1 近傍が共有する頂点数と、辺 (a,b) を共有する三角形の枚数が一致すること。
    //   一致しないなら、潰した結果 2 枚以上の面が同じ辺を挟む非多様体形状ができる。
    // 1 近傍は頂点 6〜12 個程度の小さな集合なので、`HashSet` を組むより
    // `Vec` の線形探索のほうが速い（ハッシュ計算と確保のほうが支配的になる）。
    // この関数は候補を取り出すたびに呼ばれる最内ループなので、この差がそのまま効く。
    let ring_from = one_ring_vertices(tris, tri_alive, vert_tris, from);
    let ring_to = one_ring_vertices(tris, tri_alive, vert_tris, to);
    let shared = ring_from
        .iter()
        .filter(|&&v| v != from && v != to && ring_to.contains(&v))
        .count();
    let mut edge_faces = 0usize;
    for &ti in &vert_tris[from as usize] {
        if !tri_alive[ti as usize] {
            continue;
        }
        if tris[ti as usize].contains(&to) {
            edge_faces += 1;
        }
    }
    // 辺が既に消えている（潰しで面が死んだ）候補は、離れた 2 頂点を溶接してしまうので却下する。
    // shared == edge_faces == 0 はリンク条件を素通りしてしまうため、明示的に弾く。
    if edge_faces == 0 || shared != edge_faces {
        return false;
    }

    // ── ガード 2: 面の重複（折り返し）──
    //   【これが穴の直接原因だった】
    //   リンク条件の「共有頂点数」版は、共有されるのが頂点だけでなく **辺** である場合を
    //   見逃す。典型が四面体構成 {from, to, x, y}（面 (from,to,x) (from,to,y) (from,x,y)
    //   (to,x,y) の 4 枚）で、共有頂点は {x, y} の 2 個・辺 (from,to) の面も 2 枚なので
    //   ガード 1 を素通りしてしまう。しかし from→to と潰すと (from,x,y) が (to,x,y) に
    //   化けて、まったく同じ 3 頂点の面が 2 枚重なる（折り返し）。
    //   `rebuild` はその重複を 1 枚に落とすので、結果として **三角形 1 枚ぶんの穴**が開く。
    //
    //   正しい判定は「lk(from) ∩ lk(to) == lk(辺 from-to)」（辺も含めた link condition）で、
    //   これは「潰した後に既存の面と 3 頂点が完全一致する面ができないこと」と等価である。
    //   ここでは後者を直接検査する（近傍が小さいので総当たりで十分速い）。
    //
    //   まず to に接する生存面から「to を除いた 2 頂点」の組を集める
    //   （辺 (from,to) を含む面は潰しで消えるため対象外）。
    let mut to_opposite_pairs: Vec<(u32, u32)> = Vec::new();
    for &ti in &vert_tris[to as usize] {
        if !tri_alive[ti as usize] {
            continue;
        }
        let t = tris[ti as usize];
        if t.contains(&from) {
            continue;
        }
        let mut others = t.iter().copied().filter(|&v| v != to);
        let (Some(a), Some(b)) = (others.next(), others.next()) else {
            continue;
        };
        to_opposite_pairs.push(ordered_pair(a, b));
    }

    // ── ガード 3: 法線反転 ──
    //   from を to へ動かしたとき、from に接する（辺 (from,to) を含まない）面の
    //   向きが 90 度以上倒れるなら潰さない。
    //   ガード 2 の重複検査も、同じ面集合を舐めるこのループの中で済ませる。
    for &ti in &vert_tris[from as usize] {
        if !tri_alive[ti as usize] {
            continue;
        }
        let t = tris[ti as usize];
        if t.contains(&to) {
            // この面は潰しで消えるので判定対象外。
            continue;
        }
        // 【ガード 2】この面は潰しで (to, x, y) になる。同じ (x, y) を持つ面が
        // 既に to にぶら下がっていれば、潰すと面が 2 枚重なる → 却下。
        {
            let mut others = t.iter().copied().filter(|&v| v != from);
            if let (Some(x), Some(y)) = (others.next(), others.next()) {
                if to_opposite_pairs.contains(&ordered_pair(x, y)) {
                    return false;
                }
            }
        }
        // 潰す前から縮退している面は、比較すべき法線が定義できない。
        //   以前はここを読み飛ばしていたが、それでは「向きの情報を持たない面」が
        //   潰しで面積を得たときに、向きが何の検査も受けずに決まってしまう。
        //   縮退面は数がごく少ない（乱数地形 80 ケースで 24 回）ので、
        //   読み飛ばさず潰しを却下するほうが安全で、削減率もほぼ変わらない。
        let Some((n_before, _)) = face_normal_area(positions, t) else {
            return false;
        };
        let moved = [
            if t[0] == from { to } else { t[0] },
            if t[1] == from { to } else { t[1] },
            if t[2] == from { to } else { t[2] },
        ];
        // 潰した後に縮退するなら（面積 0）却下する。
        let Some((n_after, _)) = face_normal_area(positions, moved) else {
            return false;
        };
        let dot = n_before[0] * n_after[0] + n_before[1] * n_after[1] + n_before[2] * n_after[2];
        if dot <= FLIP_GUARD_MIN_DOT {
            return false;
        }
    }
    true
}

/// 生存三角形だけを集め、参照されている頂点だけを詰め直した新しいメッシュを返す。
///
/// 頂点属性（法線・スプラット・由来辺）は **元の頂点のものをそのまま**運ぶ
/// （ハーフエッジコラプスなので生き残る頂点は必ず元の頂点である）。
///
/// 【巻き順の作り直し】
///   潰しは生き残った三角形の頂点を差し替えるので、その三角形の幾何法線も
///   平均頂点法線も変わる。元の巻き順のまま出すとエンジンの巻き順規約
///   （`orient_to_winding_convention`）を破る面が生まれ、**背面カリングで抜けて見える**
///   （表からは穴に見え、下から覗くとその面だけが見える）。
///   そこでマーチングキューブス出力と同じ規約をここで掛け直す。
///   MC と同一の関数を通すので、両者の巻き順の定義は原理的に食い違わない。
fn rebuild(src: &TerrainMesh, tris: &[[u32; 3]], tri_alive: &[bool]) -> TerrainMesh {
    let vcount = src.positions.len();
    // 旧頂点番号 → 新頂点番号（未使用は u32::MAX）。
    let mut remap: Vec<u32> = vec![u32::MAX; vcount];
    let mut out = TerrainMesh::default();
    // 重複三角形（同じ 3 頂点の面が 2 枚）を弾くための集合。
    let mut seen_faces: HashSet<[u32; 3]> = HashSet::new();

    // 由来辺は「あるならすべての頂点ぶんある」（LOD>0 では空）。長さが揃っているときだけ運ぶ。
    let has_edges = src.edges.len() == vcount;
    let has_paint = src.paint.len() == vcount;
    let has_amount = src.paint_amount.len() == vcount;
    // 法線シャープネスも生存頂点の値をそのまま継承する（ハーフエッジコラプスなので
    // 残る頂点は必ず元の MC 頂点そのもの＝値を作り直す必要が無い）。
    let has_sharpness = src.sharpness.len() == vcount;
    let has_normals = src.normals.len() == vcount;

    for (ti, t) in tris.iter().enumerate() {
        if !tri_alive[ti] {
            continue;
        }
        // 念のための縮退チェック（ここに来る面は生存なので通常は起きない）。
        if t[0] == t[1] || t[1] == t[2] || t[2] == t[0] {
            continue;
        }
        // 同一の面が 2 枚残るのを防ぐ（頂点番号を昇順に並べた形で照合する）。
        let mut key = *t;
        key.sort_unstable();
        if !seen_faces.insert(key) {
            continue;
        }
        // 潰しで頂点が入れ替わった面は幾何法線・平均頂点法線がどちらも変わっているので、
        // MC 出力と同じ規約で巻き順を張り直す（ここが唯一の是正点）。
        // 旧頂点番号のまま判定できるよう、詰め直し（remap）の前に掛ける。
        let oriented = orient_to_winding_convention(&src.positions, &src.normals, *t);
        let mut new_tri = [0u32; 3];
        for (k, &v) in oriented.iter().enumerate() {
            let vi = v as usize;
            if remap[vi] == u32::MAX {
                remap[vi] = out.positions.len() as u32;
                out.positions.push(src.positions[vi]);
                if has_normals {
                    out.normals.push(src.normals[vi]);
                }
                if has_paint {
                    out.paint.push(src.paint[vi]);
                }
                if has_amount {
                    out.paint_amount.push(src.paint_amount[vi]);
                }
                if has_sharpness {
                    out.sharpness.push(src.sharpness[vi]);
                }
                if has_edges {
                    out.edges.push(src.edges[vi]);
                }
            }
            new_tri[k] = remap[vi];
        }
        out.indices.extend_from_slice(&new_tri);
    }
    out
}

/// メッシュを丸ごと複製する（無操作パス用。`TerrainMesh` は Clone を導出していない）。
fn clone_mesh(src: &TerrainMesh) -> TerrainMesh {
    TerrainMesh {
        positions: src.positions.clone(),
        normals: src.normals.clone(),
        indices: src.indices.clone(),
        paint: src.paint.clone(),
        paint_amount: src.paint_amount.clone(),
        sharpness: src.sharpness.clone(),
        edges: src.edges.clone(),
    }
}
