// ============================================================
//  water/spline.rs — 川スプラインの幾何（Phase W4）
//
//  ## 役割（単一責任）
//  `WaterVolumeComponent` の制御点列（アクタ相対）を、
//  **ワールド空間の折れ線リボン**（`RiverPath`）へ 1 度だけ変換し、
//  描画（リボンメッシュ）と問い合わせ（流速・水中判定・水面高さ）の
//  **双方がまったく同じ折れ線を見る**ようにする。
//
//  「見た目の川」と「流される判定」が別々の曲線を持つと、岸すれすれで
//  「水に見えるのに流されない」ズレが必ず出る。単一の折れ線に集約するのが
//  この型の存在理由である。
//
//  ## 補間
//  制御点は **Catmull-Rom スプライン**（uniform, τ = 0.5）で補間する。
//  制御点を必ず通り、追加のハンドル（ベジェの制御ハンドル等）を要さないため、
//  「点を置くだけで滑らかな川になる」というエディタ体験に最も素直に合う。
//  端点は「1 つ外側の点を折り返して複製」する標準的な処理で扱う。
//
//  ## 分割密度
//  折れ線の分割数は**曲線長から一定密度で決める**（`build` の `segment_length` ごとに 1 分割。
//  既定は `RIVER_SAMPLE_STEP_M`、下限は `RIVER_SEGMENT_LENGTH_MIN`）。
//  短い川に無駄な頂点を置かず、長い川でも上限 `RIVER_MAX_SEGMENTS` を超えない。
//  **上限は刻み幅の設定に依らず据え置き**なので、長い川では設定を細かくしても
//  比例配分で縮められ、自動的に粗くなる。
//  1 分割 = 描画の 1 インスタンス（クアッド 1 枚）なので、この上限が
//  そのまま「川 1 本あたりの最大ドローインスタンス数」になる。
//
//  ## リボンの幅
//  幅は一定（`river_width`）。曲がり角では隣接 2 区間の法線を平均した
//  「マイター法線」を使い、`1/cos(θ/2)` の補正でリボンが痩せないようにする
//  （急な折り返しで補正が発散しないよう `RIVER_MITER_MAX` で頭打ちにする）。
//
//  ## 問い合わせとの誤差
//  水中判定・流速は「折れ線までの XZ 最短距離 ≤ 半幅」で行う。これは曲がり角の
//  外側で **丸い継ぎ目**を持つ判定であり、マイターで角を張った描画リボンとは
//  角の外側でごくわずかに食い違う（判定の方が内側）。実用上は無視できるうえ、
//  「見えているのに流されない」ではなく「流されないのに水に見える」側の
//  安全な誤差なのでこのままとする。
// ============================================================

// ─── 定数（マジックナンバー禁止）─────────────────────────────

/// 折れ線 1 分割ぶんの目標長（m）の**既定値**。曲線長をこれで割った数が分割数になる。
///
/// 川幅の既定 4m に対して十分細かく、かつ 100m の川でも 50 分割で収まる密度。
/// 実際の値は `WaterVolumeComponent::river_segment_length` でボリュームごとに指定でき、
/// この定数はその既定値（＝W4.1 以前の固定値）として残している。
pub const RIVER_SAMPLE_STEP_M: f32 = 2.0;

/// 分割長の下限（m）。
///
/// 0 や極小値を入れると分割数が発散し、上限 `RIVER_MAX_SEGMENTS` の比例縮小に
/// 毎フレーム丸投げすることになる（無駄な計算をしてから捨てる形）。
/// 川幅の下限が 0.01m であることを踏まえ、実用上これ以上細かくしても
/// 見た目が変わらない値として 0.25m で切る。
pub const RIVER_SEGMENT_LENGTH_MIN: f32 = 0.25;

/// 川 1 本あたりの最大分割数（= 最大描画インスタンス数）。
///
/// 1 分割 = クアッド 1 枚（6 頂点）なので 256 分割でも 1536 頂点にすぎないが、
/// GPU パラメータ配列（1 分割 = 224 バイト）の消費を予測可能にするために上限を設ける。
pub const RIVER_MAX_SEGMENTS: usize = 256;

/// 受け付ける制御点の最大数。
///
/// これを超えた分は捨てる。上限が無いと「制御点 1 点 = 最低 1 分割」の下限により
/// `RIVER_MAX_SEGMENTS` を突破し得るため、両者の整合のために必要
/// （`RIVER_MAX_CONTROL_POINTS - 1 <= RIVER_MAX_SEGMENTS` を満たすこと）。
pub const RIVER_MAX_CONTROL_POINTS: usize = 64;

/// 川が成立する最小の制御点数。これ未満は描画も問い合わせも行わない。
pub const RIVER_MIN_CONTROL_POINTS: usize = 2;

/// マイター補正の上限倍率。急な折り返し（θ→180°）で 1/cos(θ/2) が発散するのを防ぐ。
const RIVER_MITER_MAX: f32 = 2.0;

// Catmull-Rom の実装は汎用パス層（`engine::path::interp`）を正典として共有する。
// 川と ControlPointComponent が別々の曲線式を持つと、同じ点列を渡しても
// 「エディタで見える線」と「実際の川」がズレる。再輸出して既存の呼び出し名を保つ。
pub use crate::engine::path::interp::catmull_rom;

/// 川幅の下限（m）。0 幅のリボンは面積を持たず、判定も常に外れるため無意味。
pub const RIVER_WIDTH_MIN: f32 = 0.01;

/// ゼロ除算・ゼロ長ベクトル判定の下限。
const RIVER_EPSILON: f32 = 1.0e-6;

// ─── RiverNode ───────────────────────────────────────────────

/// 川の折れ線の 1 ノード（＝リボンの 1 断面）。
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct RiverNode {
    /// ノードのワールド座標（y = その地点の水面 Y）。
    pub pos: [f32; 3],
    /// リボン断面の法線（XZ。**マイター補正済みなので長さは 1 とは限らない**）。
    /// リボンの角は `pos ± normal * half_width` で求まる。
    pub normal: [f32; 2],
    /// 進行方向（XZ。単位ベクトル）。流れの向きの表示用。
    pub tangent: [f32; 2],
}

// ─── RiverPath ───────────────────────────────────────────────

/// ワールド空間へ解決済みの川 1 本（折れ線リボン）。
///
/// 描画も問い合わせもこの型だけを見る。`ResolvedWaterVolume` が所有する。
#[derive(Clone, Debug, PartialEq)]
pub struct RiverPath {
    /// 折れ線のノード列（**必ず 2 個以上**。`build` がそれを保証する）。
    pub nodes: Vec<RiverNode>,
    /// リボンの半幅（m）。`river_width / 2`。
    pub half_width: f32,
    /// 流速（m/s）。
    pub flow_speed: f32,
    /// 水中とみなす水面下の厚み（m）。これより深い点は「川の底より下」として水中でない。
    pub depth: f32,
}

/// 川への最近傍問い合わせの結果。
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct RiverSample {
    /// 問い合わせ点から折れ線までの **XZ 平面上の**最短距離（m）。
    pub distance_xz: f32,
    /// その最近傍地点の水面 Y（ノードの Y を線形補間したもの）。
    pub surface_y: f32,
    /// その地点の流れの向き（3D 単位ベクトル。下る川では Y が負になる）。
    pub direction: [f32; 3],
}

impl RiverPath {
    /// 制御点列（**ワールド座標**）からリボン折れ線を構築する。
    ///
    /// - `points_world`: Catmull-Rom の制御点（ワールド空間へ変換済み）
    /// - `width`: 川幅（m）。下限 `RIVER_WIDTH_MIN` で切る
    /// - `flow_speed`: 流速（m/s）
    /// - `depth`: 水中とみなす水面下の厚み（m）
    /// - `segment_length`: 分割 1 つぶんの目標長（m）。下限 `RIVER_SEGMENT_LENGTH_MIN` で切る。
    ///   **総分割数の上限 `RIVER_MAX_SEGMENTS` は据え置き**なので、長い川ではこの値を
    ///   小さくしても上限で頭打ちになり、結果として自動的に粗くなる。
    ///
    /// 制御点が `RIVER_MIN_CONTROL_POINTS` 未満なら **None**（川は成立しない）。
    pub fn build(
        points_world:   &[[f32; 3]],
        width:          f32,
        flow_speed:     f32,
        depth:          f32,
        segment_length: f32,
    ) -> Option<Self> {
        if points_world.len() < RIVER_MIN_CONTROL_POINTS {
            return None;
        }
        // 上限超過の制御点は捨てる（分割数の上限と整合させるため。上の定数コメント参照）。
        let ctrl: &[[f32; 3]] = &points_world[..points_world.len().min(RIVER_MAX_CONTROL_POINTS)];

        // ── ① 各区間の分割数を「弦長 / 目標刻み」で決める ──
        //     弦長は曲線長の下限にすぎないが、制御点間隔が刻みより十分大きい
        //     通常の使い方では誤差は小さく、曲線長の数値積分を持ち込む価値は無い。
        //     刻み幅はボリュームごとの設定値（下限で締める）。
        let step = segment_length.max(RIVER_SEGMENT_LENGTH_MIN);
        let span_count = ctrl.len() - 1;
        let mut divisions: Vec<usize> = Vec::with_capacity(span_count);
        for i in 0..span_count {
            let chord = distance3(ctrl[i], ctrl[i + 1]);
            let want  = (chord / step).ceil() as usize;
            divisions.push(want.max(1));
        }
        // 総分割数が上限を超えるなら、区間ごとの分割数を比例配分で縮める
        //（各区間は最低 1 分割を保つ）。
        let total: usize = divisions.iter().sum();
        if total > RIVER_MAX_SEGMENTS {
            let scale = RIVER_MAX_SEGMENTS as f32 / total as f32;
            for d in divisions.iter_mut() {
                *d = ((*d as f32 * scale).floor() as usize).max(1);
            }
        }

        // ── ② Catmull-Rom で位置をサンプルする ──
        //     区間 i の内部を [0,1) で刻み、最後に終端の制御点を 1 つだけ足す
        //     （区間境界でノードが重複しないようにするため）。
        let mut positions: Vec<[f32; 3]> = Vec::new();
        for (i, &div) in divisions.iter().enumerate() {
            // 端点は 1 つ外側の点を折り返して複製する（標準的な端点処理）。
            let p0 = ctrl[i.saturating_sub(1)];
            let p1 = ctrl[i];
            let p2 = ctrl[i + 1];
            let p3 = ctrl[(i + 2).min(ctrl.len() - 1)];
            for s in 0..div {
                let t = s as f32 / div as f32;
                positions.push(catmull_rom(p0, p1, p2, p3, t));
            }
        }
        positions.push(ctrl[ctrl.len() - 1]);

        // ── ③ 縮退（同一座標が連続する）ノードを畳む ──
        //     制御点を重ねて置かれた場合に、長さ 0 の区間で法線が作れなくなるのを防ぐ。
        positions.dedup_by(|a, b| distance3(*a, *b) < RIVER_EPSILON);
        if positions.len() < RIVER_MIN_CONTROL_POINTS {
            return None;
        }

        // ── ④ ノードごとの接線・マイター法線を作る ──
        let nodes = build_nodes(&positions);

        Some(Self {
            nodes,
            half_width: width.max(RIVER_WIDTH_MIN) * 0.5,
            flow_speed,
            depth:      depth.max(0.0),
        })
    }

    /// 折れ線の分割数（= 描画インスタンス数）。
    pub fn segment_count(&self) -> usize {
        self.nodes.len().saturating_sub(1)
    }

    /// 点 `p` に最も近い川上の地点を求める（XZ 平面での最近傍）。
    ///
    /// 折れ線の全区間を総当たりする。区間数は最大 `RIVER_MAX_SEGMENTS` であり、
    /// 1 区間あたり数演算なので、空間分割構造を持ち込む価値は無い。
    pub fn nearest(&self, p: [f32; 3]) -> RiverSample {
        let mut best_dist_sq = f32::INFINITY;
        let mut best_y       = self.nodes[0].pos[1];
        let mut best_dir     = [0.0f32, 0.0, 0.0];

        for w in self.nodes.windows(2) {
            let a = w[0].pos;
            let b = w[1].pos;
            // XZ 平面へ射影して、線分 ab 上の最近傍パラメータ t を求める。
            let abx = b[0] - a[0];
            let abz = b[2] - a[2];
            let len_sq = abx * abx + abz * abz;
            let t = if len_sq > RIVER_EPSILON {
                (((p[0] - a[0]) * abx + (p[2] - a[2]) * abz) / len_sq).clamp(0.0, 1.0)
            } else {
                0.0
            };
            let cx = a[0] + abx * t;
            let cz = a[2] + abz * t;
            let dx = p[0] - cx;
            let dz = p[2] - cz;
            let d_sq = dx * dx + dz * dz;
            if d_sq < best_dist_sq {
                best_dist_sq = d_sq;
                // 水面 Y は区間の両端 Y を同じ t で補間する（＝川が下る）。
                best_y   = a[1] + (b[1] - a[1]) * t;
                best_dir = normalize3([abx, b[1] - a[1], abz]);
            }
        }

        RiverSample {
            distance_xz: best_dist_sq.sqrt(),
            surface_y:   best_y,
            direction:   best_dir,
        }
    }
}

// ─── 補間・ベクトルヘルパー ──────────────────────────────────

/// サンプル済み位置列から、接線とマイター法線を持つノード列を作る。
///
/// 接線は中央差分（前後のノードの差）で求める。区間ごとの法線を平均した向きに
/// なるため、そのままでは曲がり角でリボンが痩せる。`1/cos(θ/2)` すなわち
/// 「区間法線との内積の逆数」を掛けて幅を保ち、`RIVER_MITER_MAX` で頭打ちにする。
fn build_nodes(positions: &[[f32; 3]]) -> Vec<RiverNode> {
    let n = positions.len();
    let mut nodes = Vec::with_capacity(n);
    for i in 0..n {
        // 中央差分の接線（端は片側差分になる）。
        let prev = positions[i.saturating_sub(1)];
        let next = positions[(i + 1).min(n - 1)];
        let tangent = normalize2([next[0] - prev[0], next[2] - prev[2]]);
        // XZ 平面で接線を 90° 回した法線。
        let normal = [-tangent[1], tangent[0]];

        // マイター補正: この区間（i → i+1、端では i-1 → i）の法線と平均法線の
        // 内積が cos(θ/2) にあたる。その逆数を掛けると幅が保たれる。
        let seg_a = positions[i.min(n - 2)];
        let seg_b = positions[(i + 1).min(n - 1)];
        let seg_t = normalize2([seg_b[0] - seg_a[0], seg_b[2] - seg_a[2]]);
        let seg_n = [-seg_t[1], seg_t[0]];
        let cos_half = normal[0] * seg_n[0] + normal[1] * seg_n[1];
        let miter = if cos_half.abs() > RIVER_EPSILON {
            (1.0 / cos_half.abs()).min(RIVER_MITER_MAX)
        } else {
            RIVER_MITER_MAX
        };

        nodes.push(RiverNode {
            pos:     positions[i],
            normal:  [normal[0] * miter, normal[1] * miter],
            tangent,
        });
    }
    nodes
}

/// 3D 距離。
fn distance3(a: [f32; 3], b: [f32; 3]) -> f32 {
    let d = [b[0] - a[0], b[1] - a[1], b[2] - a[2]];
    (d[0] * d[0] + d[1] * d[1] + d[2] * d[2]).sqrt()
}

/// 3D 正規化（長さ 0 はゼロベクトルを返す）。
fn normalize3(v: [f32; 3]) -> [f32; 3] {
    let len = (v[0] * v[0] + v[1] * v[1] + v[2] * v[2]).sqrt();
    if len > RIVER_EPSILON { [v[0] / len, v[1] / len, v[2] / len] } else { [0.0, 0.0, 0.0] }
}

/// 2D 正規化（長さ 0 は +X を返す。法線を作れないノードを無くすため）。
fn normalize2(v: [f32; 2]) -> [f32; 2] {
    let len = (v[0] * v[0] + v[1] * v[1]).sqrt();
    if len > RIVER_EPSILON { [v[0] / len, v[1] / len] } else { [1.0, 0.0] }
}

// ─── ユニットテスト ──────────────────────────────────────────

#[cfg(test)]
mod tests {
    use super::*;

    /// テスト用の既定値（幅 4m・流速 1.5m/s・深さ 5m）。
    const W: f32 = 4.0;
    const S: f32 = 1.5;
    const D: f32 = 5.0;
    /// テスト用の分割長（既定と同じ 2m 刻み）。
    const SEG: f32 = RIVER_SAMPLE_STEP_M;

    /// 制御点が 2 点未満なら川は成立しない（None）。
    #[test]
    fn build_rejects_fewer_than_two_points() {
        assert!(RiverPath::build(&[], W, S, D, SEG).is_none(), "空の制御点");
        assert!(RiverPath::build(&[[0.0, 0.0, 0.0]], W, S, D, SEG).is_none(), "1 点だけ");
    }

    /// 2 点あれば成立し、半幅は幅の半分になる。
    #[test]
    fn build_accepts_two_points() {
        let path = RiverPath::build(&[[0.0, 0.0, 0.0], [10.0, 0.0, 0.0]], W, S, D, SEG)
            .expect("2 点で川が成立すること");
        assert!(path.nodes.len() >= 2);
        assert_eq!(path.half_width, W * 0.5);
        assert_eq!(path.flow_speed, S);
    }

    /// Catmull-Rom は制御点を必ず通る（t=0 で p1、t=1 で p2）。
    #[test]
    fn catmull_rom_passes_through_control_points() {
        let p0 = [0.0, 0.0, 0.0];
        let p1 = [1.0, 2.0, 3.0];
        let p2 = [4.0, 5.0, 6.0];
        let p3 = [7.0, 8.0, 9.0];
        let a = catmull_rom(p0, p1, p2, p3, 0.0);
        let b = catmull_rom(p0, p1, p2, p3, 1.0);
        for i in 0..3 {
            assert!((a[i] - p1[i]).abs() < 1e-5, "t=0 は p1 を通る");
            assert!((b[i] - p2[i]).abs() < 1e-5, "t=1 は p2 を通る");
        }
    }

    /// 分割密度は曲線長に比例する（長い川ほどノードが多い）。
    #[test]
    fn division_density_scales_with_length() {
        let short = RiverPath::build(&[[0.0, 0.0, 0.0], [4.0, 0.0, 0.0]], W, S, D, SEG).unwrap();
        let long  = RiverPath::build(&[[0.0, 0.0, 0.0], [40.0, 0.0, 0.0]], W, S, D, SEG).unwrap();
        assert!(long.segment_count() > short.segment_count());
        // 目標刻み 2m なので 4m は 2 分割、40m は 20 分割になるはず。
        assert_eq!(short.segment_count(), 2);
        assert_eq!(long.segment_count(), 20);
    }

    /// 分割長を細かくすると分割数が増え、粗くすると減ること（W4.1 の設定値が効く契約）。
    #[test]
    fn segment_length_controls_division_count() {
        let pts = [[0.0, 0.0, 0.0], [40.0, 0.0, 0.0]];
        // 40m を 2m 刻み → 20 分割 / 4m 刻み → 10 分割 / 1m 刻み → 40 分割
        assert_eq!(RiverPath::build(&pts, W, S, D, 2.0).unwrap().segment_count(), 20);
        assert_eq!(RiverPath::build(&pts, W, S, D, 4.0).unwrap().segment_count(), 10);
        assert_eq!(RiverPath::build(&pts, W, S, D, 1.0).unwrap().segment_count(), 40);
    }

    /// 分割長が 0 や負でも下限で切られ、分割数が発散しないこと。
    #[test]
    fn segment_length_is_clamped_to_lower_bound() {
        let pts = [[0.0, 0.0, 0.0], [40.0, 0.0, 0.0]];
        // 下限 0.25m 相当（40 / 0.25 = 160 分割）へ丸められること
        let expected = (40.0 / RIVER_SEGMENT_LENGTH_MIN).ceil() as usize;
        assert_eq!(RiverPath::build(&pts, W, S, D, 0.0).unwrap().segment_count(), expected);
        assert_eq!(RiverPath::build(&pts, W, S, D, -5.0).unwrap().segment_count(), expected);
    }

    /// 分割長を細かくしても、総分割数の上限は据え置きで守られること
    ///（＝長い川では設定を細かくしても自動的に粗くなる）。
    #[test]
    fn segment_length_still_respects_global_cap() {
        let path = RiverPath::build(
            &[[0.0, 0.0, 0.0], [100_000.0, 0.0, 0.0]], W, S, D, RIVER_SEGMENT_LENGTH_MIN).unwrap();
        assert!(path.segment_count() <= RIVER_MAX_SEGMENTS,
            "分割長を最小にしても上限 {} を超えないこと: {}", RIVER_MAX_SEGMENTS, path.segment_count());
    }

    /// 分割数は上限を超えない（極端に長い川でも GPU 側の予算が破綻しない）。
    #[test]
    fn division_count_is_capped() {
        let path = RiverPath::build(
            &[[0.0, 0.0, 0.0], [100_000.0, 0.0, 0.0]], W, S, D, SEG).unwrap();
        assert!(path.segment_count() <= RIVER_MAX_SEGMENTS,
            "分割数 {} が上限 {} を超えた", path.segment_count(), RIVER_MAX_SEGMENTS);
    }

    /// 制御点が上限を超えても、分割数の上限を破らない
    /// （「1 制御点区間 = 最低 1 分割」との整合確認）。
    #[test]
    fn control_points_are_capped() {
        let pts: Vec<[f32; 3]> = (0..500).map(|i| [i as f32 * 50.0, 0.0, 0.0]).collect();
        let path = RiverPath::build(&pts, W, S, D, SEG).unwrap();
        assert!(path.segment_count() <= RIVER_MAX_SEGMENTS);
    }

    /// 重なった制御点だけの川は成立しない（縮退の畳み込み後に 2 点未満になる）。
    #[test]
    fn degenerate_duplicate_points_are_rejected() {
        let p = [3.0, 1.0, -2.0];
        assert!(RiverPath::build(&[p, p], W, S, D, SEG).is_none());
    }

    /// 直線の川では、川の中心線上の点の距離が 0 になる。
    #[test]
    fn nearest_on_centerline_is_zero_distance() {
        let path = RiverPath::build(&[[0.0, 0.0, 0.0], [10.0, 0.0, 0.0]], W, S, D, SEG).unwrap();
        let s = path.nearest([5.0, 0.0, 0.0]);
        assert!(s.distance_xz < 1e-4, "中心線上の距離は 0（実際 {}）", s.distance_xz);
    }

    /// 横方向の距離が正しく求まる（幅境界の判定に直結する）。
    #[test]
    fn nearest_measures_perpendicular_distance() {
        let path = RiverPath::build(&[[0.0, 0.0, 0.0], [10.0, 0.0, 0.0]], W, S, D, SEG).unwrap();
        let s = path.nearest([5.0, 0.0, 3.0]);
        assert!((s.distance_xz - 3.0).abs() < 1e-4, "垂直距離 3m（実際 {}）", s.distance_xz);
    }

    /// 端点より外側の点は、端点までの距離になる（線分の外側へ外挿しない）。
    #[test]
    fn nearest_clamps_beyond_endpoints() {
        let path = RiverPath::build(&[[0.0, 0.0, 0.0], [10.0, 0.0, 0.0]], W, S, D, SEG).unwrap();
        let s = path.nearest([14.0, 0.0, 0.0]);
        assert!((s.distance_xz - 4.0).abs() < 1e-4, "終端から 4m（実際 {}）", s.distance_xz);
    }

    /// 水面 Y は制御点の Y を補間する（＝川が下る）。
    #[test]
    fn surface_y_interpolates_between_points() {
        let path = RiverPath::build(&[[0.0, 10.0, 0.0], [10.0, 0.0, 0.0]], W, S, D, SEG).unwrap();
        let s = path.nearest([5.0, 100.0, 0.0]);
        assert!((s.surface_y - 5.0).abs() < 0.2,
            "中間地点の水面 Y はおよそ 5（実際 {}）", s.surface_y);
        // 上流端・下流端でも Y が一致すること
        assert!((path.nearest([0.0, 0.0, 0.0]).surface_y - 10.0).abs() < 1e-3);
        assert!((path.nearest([10.0, 0.0, 0.0]).surface_y - 0.0).abs() < 1e-3);
    }

    /// 流れの向きは上流→下流の 3D 単位ベクトル（下る川では Y が負）。
    #[test]
    fn direction_points_downstream_in_3d() {
        let path = RiverPath::build(&[[0.0, 10.0, 0.0], [10.0, 0.0, 0.0]], W, S, D, SEG).unwrap();
        let d = path.nearest([5.0, 5.0, 0.0]).direction;
        assert!(d[0] > 0.0, "+X 方向へ流れる");
        assert!(d[1] < 0.0, "下る川なので Y は負");
        let len = (d[0] * d[0] + d[1] * d[1] + d[2] * d[2]).sqrt();
        assert!((len - 1.0).abs() < 1e-4, "単位ベクトル（実際の長さ {len}）");
    }

    /// ノードの法線は接線と直交する（マイター倍率が掛かっても向きは直交のまま）。
    #[test]
    fn node_normal_is_perpendicular_to_tangent() {
        let path = RiverPath::build(
            &[[0.0, 0.0, 0.0], [10.0, 0.0, 0.0], [20.0, 0.0, 10.0]], W, S, D, SEG).unwrap();
        for n in &path.nodes {
            let dot = n.normal[0] * n.tangent[0] + n.normal[1] * n.tangent[1];
            assert!(dot.abs() < 1e-4, "法線と接線が直交していない（内積 {dot}）");
        }
    }

    /// マイター補正は上限で頭打ちになる（急な折り返しでリボンが爆発しない）。
    #[test]
    fn miter_is_clamped() {
        // ほぼ 180° 折り返す制御点列
        let path = RiverPath::build(
            &[[0.0, 0.0, 0.0], [10.0, 0.0, 0.0], [0.1, 0.0, 0.1]], W, S, D, SEG).unwrap();
        for n in &path.nodes {
            let len = (n.normal[0] * n.normal[0] + n.normal[1] * n.normal[1]).sqrt();
            assert!(len <= RIVER_MITER_MAX + 1e-3,
                "マイター倍率が上限を超えた（{len}）");
        }
    }

    /// 幅 0 を渡しても下限で切られる（面積 0 のリボンを作らない）。
    #[test]
    fn width_has_a_lower_bound() {
        let path = RiverPath::build(&[[0.0, 0.0, 0.0], [10.0, 0.0, 0.0]], 0.0, S, D, SEG).unwrap();
        assert!(path.half_width >= RIVER_WIDTH_MIN * 0.5);
    }
}
