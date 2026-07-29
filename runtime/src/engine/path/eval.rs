// ============================================================
//  path/eval.rs — コントロールポイント列の評価（PathEval）
//
//  `ControlPointComponent` の点列（アクタ相対）を **ワールド空間へ 1 度だけ解決**し、
//  「時刻での位置」「進捗での位置」「折れ線化」の 3 つの問い合わせに答える中間表現。
//
//  ## なぜ中間表現を挟むのか
//  川の描画・キャラの巡回・カメラのフライスルーが、それぞれ独自にワールド変換と
//  補間を書くと、同じ点列なのに **見た目の線と実際に通る線がズレる**（Phase W4 で
//  「見えているのに流されない」として実際に問題になった構図）。
//  評価をこの型 1 つに集約し、描画も消費側も同じ `PathEval` を見るようにする。
//
//  ## 座標系
//  構築時にアクタの Transform 行列を掛け、以降はすべて**ワールド空間**で答える。
//  回転も「アクタ回転 × 点の回転」を行列で合成してから YXZ オイラー角へ戻すので、
//  アクタを回してもパス上の姿勢が正しく追従する。
//
//  ## 時刻パラメータの扱い
//  `time` の単位・原点は消費側に委ねる（`ControlPointComponent` 冒頭コメント参照）。
//  本型は「点列に沿う 1 次元パラメータ」としてのみ扱い、
//  区間外は**両端でクランプ**する（外挿しない ＝ パスの外へ飛び出さない）。
//
//  ## 川（Phase W4）との接続を意識した設計
//  `sample_polyline` はワールド空間の折れ線を返す。川はこれをそのまま
//  `RiverPath::build` の制御点として渡せる（XZ 折れ線化はリボン生成側の仕事）。
//  次フェーズで「同一アクタの ControlPointComponent を川が参照する」統合を行う際に、
//  本 API 以外を触らずに済むようにしてある。
// ============================================================

use crate::engine::components::control_point_component::{ControlPoint, ControlPointInterp};
use crate::engine::components::Transform;
use crate::engine::methods::gizmo_interact::mat4x4_mul;

use super::interp::{catmull_rom, distance3, lerp3, PATH_EPSILON};

// ─── 定数（マジックナンバー禁止）─────────────────────────────

/// `sample_polyline` の分割 1 つぶんの既定目標長（m）。
///
/// 川スプライン（W4）の分割密度と同じ値を既定にしてあり、
/// 「汎用パスとして折れ線化したもの」と「川として折れ線化したもの」が
/// 既定設定では一致する。
pub const PATH_DEFAULT_STEP_M: f32 = 2.0;

/// `sample_polyline` が返す折れ線の最大分割数。
///
/// 上限が無いと、制御点を密に置いた長いパスで頂点数が青天井になる
/// （＝ビューポートのライン描画コストが予測不能になる）。
pub const PATH_MAX_POLYLINE_SEGMENTS: usize = 1024;

/// 分割長の下限（m）。0 以下や極端に小さい値で分割数が発散するのを防ぐ。
pub const PATH_MIN_STEP_M: f32 = 0.01;

/// パスが「区間を持つ」ために必要な最小の点数。
pub const PATH_MIN_POINTS_FOR_SEGMENT: usize = 2;

// ─── ResolvedControlPoint ─────────────────────────────────────

/// ワールド空間へ解決済みの制御点 1 個。
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct ResolvedControlPoint {
    /// ワールド座標。
    pub position: [f32; 3],
    /// ワールド姿勢（度・YXZ オイラー角。アクタ回転と合成済み）。
    pub rotation: [f32; 3],
    /// ワールド拡縮（アクタスケール × 点スケール）。
    ///
    /// 川は位置しか使わないためこの値の影響を受けないが、
    /// ビューポートのキューブ表示や、将来のカメラ／巡回の消費側が使う。
    pub scale: [f32; 3],
    /// 時刻パラメータ（アクタ変換の影響を受けない）。
    pub time: f32,
    /// **この点から次の点へ**向かう区間の補間方法。
    pub interp: ControlPointInterp,
}

// ─── PathSample ───────────────────────────────────────────────

/// パス上の 1 点の評価結果。
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct PathSample {
    /// ワールド座標。
    pub position: [f32; 3],
    /// ワールド姿勢（度・YXZ オイラー角）。
    pub rotation: [f32; 3],
    /// ワールド拡縮（軸ごと）。区間の補間方法に従って補間される。
    pub scale: [f32; 3],
}

// ─── PathEval ─────────────────────────────────────────────────

/// ワールド空間へ解決済みのパス。位置・姿勢・折れ線の問い合わせに答える。
#[derive(Clone, Debug, Default, PartialEq)]
pub struct PathEval {
    /// 解決済みの制御点列（順序 = パスの順序）。
    points: Vec<ResolvedControlPoint>,
}

impl PathEval {
    /// アクタ相対の制御点列と、そのアクタの Transform からワールド空間のパスを構築する。
    ///
    /// 点が 0 個でも構築は成功する（問い合わせが一律 `None` を返すだけ）。
    /// 「点が足りない」ことを呼び出し側でいちいち分岐させないための設計。
    pub fn from_points(points: &[ControlPoint], actor: &Transform) -> Self {
        let actor_mat = actor.to_mat4();
        let resolved = points.iter().map(|p| {
            // 点のローカル行列（位置・回転・**拡縮**）を作り、アクタ行列と合成してから
            // ワールドの位置・姿勢・拡縮へ分解する。
            //
            // ※ スケールは点の**自分の原点まわり**に掛かるので、合成しても
            //    点のワールド位置は変わらない（＝位置しか使わない消費側＝川は無影響）。
            let local = Transform {
                position: p.position,
                rotation: p.rotation,
                scale:    p.scale,
            }.to_mat4();
            let world = Transform::from_mat4(&mat4x4_mul(actor_mat, local));
            ResolvedControlPoint {
                position: world.position,
                rotation: world.rotation,
                scale:    world.scale,
                time:     p.time,
                interp:   p.interp,
            }
        }).collect();
        Self { points: resolved }
    }

    /// 解決済み制御点列への参照（描画・ピッキングが点そのものを必要とする）。
    pub fn points(&self) -> &[ResolvedControlPoint] { &self.points }

    /// 制御点の数。
    pub fn len(&self) -> usize { self.points.len() }

    /// 制御点が 1 つも無いか。
    pub fn is_empty(&self) -> bool { self.points.is_empty() }

    /// 時刻の範囲（最初の点の time, 最後の点の time）。点が無ければ None。
    ///
    /// 点列の `time` が単調でない場合でも「先頭と末尾」をそのまま返す
    /// （並べ替えはしない ＝ 添字の順序がパスの順序であるという契約を守る）。
    pub fn time_range(&self) -> Option<(f32, f32)> {
        let first = self.points.first()?;
        let last  = self.points.last()?;
        Some((first.time, last.time))
    }

    /// パス全体の所要時間（末尾 time − 先頭 time）。点が無ければ None。
    pub fn duration(&self) -> Option<f32> {
        self.time_range().map(|(a, b)| b - a)
    }

    /// 指定時刻におけるパス上の位置・姿勢を返す。
    ///
    /// 区間外はクランプする（先頭より前は先頭、末尾より後は末尾）。
    /// 点が 0 個のときのみ None。点が 1 個ならその点を常に返す。
    pub fn sample_at_time(&self, t: f32) -> Option<PathSample> {
        let (index, u) = self.locate_time(t)?;
        Some(self.eval_segment(index, u))
    }

    /// 指定時刻におけるパス上の位置。
    pub fn position_at_time(&self, t: f32) -> Option<[f32; 3]> {
        self.sample_at_time(t).map(|s| s.position)
    }

    /// 進捗 0..1 におけるパス上の位置・姿勢を返す。
    ///
    /// 進捗は**時刻の範囲**へ写す（0 = 先頭の time、1 = 末尾の time）。
    /// 所要時間が 0（全点が同時刻）の場合のみ、**区間の添字空間**へ写す
    /// （0 = 先頭の点、1 = 末尾の点）。時刻を設定していない点列でも
    /// 「等間隔にパスを辿る」用途が壊れないようにするための退避規則。
    pub fn sample_at_progress(&self, progress: f32) -> Option<PathSample> {
        let n = self.points.len();
        if n == 0 { return None; }
        let p = progress.clamp(0.0, 1.0);
        let (t0, t1) = self.time_range()?;
        if (t1 - t0).abs() > PATH_EPSILON {
            return self.sample_at_time(t0 + (t1 - t0) * p);
        }
        // 時刻が全点同一 → 区間の添字空間で等分する。
        if n < PATH_MIN_POINTS_FOR_SEGMENT {
            return Some(self.eval_segment(0, 0.0));
        }
        let span_count = n - 1;
        let scaled = p * span_count as f32;
        let index  = (scaled.floor() as usize).min(span_count - 1);
        let u      = (scaled - index as f32).clamp(0.0, 1.0);
        Some(self.eval_segment(index, u))
    }

    /// 進捗 0..1 におけるパス上の位置。
    pub fn position_at_progress(&self, progress: f32) -> Option<[f32; 3]> {
        self.sample_at_progress(progress).map(|s| s.position)
    }

    /// パスをワールド空間の折れ線へ変換する（描画・川のリボン生成用）。
    ///
    /// - `step_m`: 分割 1 つぶんの目標長（m）。小さいほど曲線が滑らかになる。
    ///   `PATH_MIN_STEP_M` で下限を切り、総分割数は `PATH_MAX_POLYLINE_SEGMENTS` で頭打ちにする。
    ///
    /// 補間方法は**区間ごとに尊重する**:
    ///   - `Linear` / `Step` … 分割しない（直線 1 本で表せるため）。
    ///     ただし `Step` は「次の点へ瞬間移動する」ので、返る線分は
    ///     **実際に通る経路ではなく飛び先を示す弦**である
    ///     （ビューポートでは L 字で描き分け、経路でないことを示す）。
    ///   - `CatmullRom` … 弦長 / `step_m` の数だけ分割する。
    ///
    /// 点が 0 個なら空、1 個ならその 1 点だけを返す。
    pub fn sample_polyline(&self, step_m: f32) -> Vec<[f32; 3]> {
        let n = self.points.len();
        if n == 0 { return Vec::new(); }
        if n < PATH_MIN_POINTS_FOR_SEGMENT {
            return vec![self.points[0].position];
        }
        let step = step_m.max(PATH_MIN_STEP_M);

        // ── ① 区間ごとの分割数を決める（Catmull-Rom のみ分割する）──
        let span_count = n - 1;
        let mut divisions: Vec<usize> = Vec::with_capacity(span_count);
        for i in 0..span_count {
            let div = match self.points[i].interp {
                ControlPointInterp::CatmullRom => {
                    let chord = distance3(self.points[i].position, self.points[i + 1].position);
                    ((chord / step).ceil() as usize).max(1)
                }
                // 直線・階段は分割しても頂点が増えるだけで形は変わらない。
                ControlPointInterp::Linear | ControlPointInterp::Step => 1,
            };
            divisions.push(div);
        }
        // 総分割数が上限を超えるなら比例配分で縮める（各区間は最低 1 分割を保つ）。
        let total: usize = divisions.iter().sum();
        if total > PATH_MAX_POLYLINE_SEGMENTS {
            let scale = PATH_MAX_POLYLINE_SEGMENTS as f32 / total as f32;
            for d in divisions.iter_mut() {
                *d = ((*d as f32 * scale).floor() as usize).max(1);
            }
        }

        // ── ② 区間内を [0,1) で刻み、最後に終端の点を 1 つだけ足す ──
        //     （区間境界で頂点が重複しないようにするため）。
        let mut out: Vec<[f32; 3]> = Vec::with_capacity(divisions.iter().sum::<usize>() + 1);
        for (i, &div) in divisions.iter().enumerate() {
            for s in 0..div {
                let u = s as f32 / div as f32;
                out.push(self.eval_segment(i, u).position);
            }
        }
        out.push(self.points[n - 1].position);
        out
    }

    /// **1 区間だけ**を折れ線化する（区間 `index` → `index + 1`）。
    ///
    /// 返る頂点は `points[index]` から `points[index + 1]` までで、**両端を含む**
    /// （`sample_polyline` は区間境界の重複を避けるため終端を含めないので、用途が異なる）。
    /// 区間ごとに色や線種を変えて描くビューポート表示のために用意している。
    ///
    /// 区間が存在しない添字なら空を返す。
    pub fn segment_polyline(&self, index: usize, step_m: f32) -> Vec<[f32; 3]> {
        let n = self.points.len();
        if n < PATH_MIN_POINTS_FOR_SEGMENT || index + 1 >= n { return Vec::new(); }
        let step = step_m.max(PATH_MIN_STEP_M);

        let div = match self.points[index].interp {
            ControlPointInterp::CatmullRom => {
                let chord = distance3(self.points[index].position, self.points[index + 1].position);
                ((chord / step).ceil() as usize)
                    .max(1)
                    .min(PATH_MAX_POLYLINE_SEGMENTS)
            }
            // 直線・階段は分割しても形が変わらない。
            ControlPointInterp::Linear | ControlPointInterp::Step => 1,
        };

        let mut out = Vec::with_capacity(div + 1);
        for s in 0..div {
            out.push(self.eval_segment(index, s as f32 / div as f32).position);
        }
        out.push(self.points[index + 1].position);
        out
    }

    // ─── 内部実装 ────────────────────────────────────────────

    /// 時刻 `t` が属する区間の添字と、その区間内パラメータ u ∈ [0,1] を求める。
    ///
    /// 点が 1 個なら (0, 0.0)。区間外はクランプ。
    /// `time` が単調増加でない点列では、先頭から走査して**最初に該当した区間**を採る
    /// （それでも見つからなければ末尾へクランプする）。
    fn locate_time(&self, t: f32) -> Option<(usize, f32)> {
        let n = self.points.len();
        if n == 0 { return None; }
        if n < PATH_MIN_POINTS_FOR_SEGMENT { return Some((0, 0.0)); }

        let last_span = n - PATH_MIN_POINTS_FOR_SEGMENT; // = n - 2（最後の区間の添字）
        if t <= self.points[0].time { return Some((0, 0.0)); }
        if t >= self.points[n - 1].time { return Some((last_span, 1.0)); }

        for i in 0..n - 1 {
            let a = self.points[i].time;
            let b = self.points[i + 1].time;
            if t >= a && t <= b {
                let span = b - a;
                let u = if span.abs() > PATH_EPSILON { (t - a) / span } else { 0.0 };
                return Some((i, u.clamp(0.0, 1.0)));
            }
        }
        // 時刻が単調でない点列で、どの区間にも入らなかった場合の退避。
        Some((last_span, 1.0))
    }

    /// 区間 `index`（`index` → `index + 1`）の内部パラメータ `u` で評価する。
    ///
    /// 点が 1 個のときは `index = 0` / `u` 無視でその点を返す。
    fn eval_segment(&self, index: usize, u: f32) -> PathSample {
        let n = self.points.len();
        if n < PATH_MIN_POINTS_FOR_SEGMENT {
            let p = self.points[0];
            return PathSample { position: p.position, rotation: p.rotation, scale: p.scale };
        }
        let i  = index.min(n - PATH_MIN_POINTS_FOR_SEGMENT);
        let a  = self.points[i];
        let b  = self.points[i + 1];
        let ut = u.clamp(0.0, 1.0);

        let position = match a.interp {
            // 階段保持: 次の点に**着くまで**手前の点の位置を保ち、着いた瞬間に次の点になる。
            // u == 1 を手前の点のままにすると、パス末尾の区間が Step のとき
            // 「最後の点に永久に到達しない」パスになってしまう（実際に踏んだ不具合）。
            ControlPointInterp::Step       => if ut >= 1.0 { b.position } else { a.position },
            ControlPointInterp::Linear     => lerp3(a.position, b.position, ut),
            ControlPointInterp::CatmullRom => {
                // 端点は 1 つ外側の点を折り返して複製する（標準的な端点処理）。
                let p0 = self.points[i.saturating_sub(1)].position;
                let p3 = self.points[(i + 2).min(n - 1)].position;
                catmull_rom(p0, a.position, b.position, p3, ut)
            }
        };

        // 姿勢は「Step は保持、それ以外は軸ごとの線形補間」で扱う。
        // 角度の巻き戻り（例 350° → 10°）は補正しない。巻き戻りを最短経路で
        // 補間したい用途（カメラの追従）は、消費側でクォータニオンへ移して扱うこと。
        let rotation = match a.interp {
            ControlPointInterp::Step => if ut >= 1.0 { b.rotation } else { a.rotation },
            _                        => lerp3(a.rotation, b.rotation, ut),
        };

        // 拡縮は姿勢と同じ規則（Step は保持、それ以外は軸ごとの線形補間）で扱う。
        // 対数補間（指数的なスケール変化）にしないのは、姿勢と挙動を揃えた方が
        // 「点を 2 つ置いて間を見る」ときの予測が立つため。
        let scale = match a.interp {
            ControlPointInterp::Step => if ut >= 1.0 { b.scale } else { a.scale },
            _                        => lerp3(a.scale, b.scale, ut),
        };

        PathSample { position, rotation, scale }
    }
}

// ─── ユニットテスト ──────────────────────────────────────────

#[cfg(test)]
mod tests {
    use super::*;

    /// テスト用: 位置と時刻と補間方法だけを指定して制御点を作る。
    fn cp(pos: [f32; 3], time: f32, interp: ControlPointInterp) -> ControlPoint {
        ControlPoint { position: pos, rotation: [0.0; 3], scale: [1.0; 3], time, interp }
    }

    /// 単位変換（無回転・原点）のアクタ Transform。
    fn identity_actor() -> Transform { Transform::identity() }

    /// 点が 0 個: すべての問い合わせが None / 空を返すこと（呼び出し側で分岐不要）。
    #[test]
    fn empty_path_answers_nothing() {
        let p = PathEval::from_points(&[], &identity_actor());
        assert!(p.is_empty());
        assert_eq!(p.len(), 0);
        assert_eq!(p.time_range(), None);
        assert_eq!(p.duration(), None);
        assert_eq!(p.position_at_time(0.0), None);
        assert_eq!(p.position_at_progress(0.5), None);
        assert!(p.sample_polyline(PATH_DEFAULT_STEP_M).is_empty());
    }

    /// 点が 1 個: どの時刻・進捗を聞いてもその点を返し、折れ線はその 1 点のみ。
    #[test]
    fn single_point_path_is_constant() {
        let pts = [cp([5.0, 1.0, -2.0], 3.0, ControlPointInterp::CatmullRom)];
        let p = PathEval::from_points(&pts, &identity_actor());
        assert_eq!(p.position_at_time(-100.0), Some([5.0, 1.0, -2.0]));
        assert_eq!(p.position_at_time(100.0),  Some([5.0, 1.0, -2.0]));
        assert_eq!(p.position_at_progress(0.0), Some([5.0, 1.0, -2.0]));
        assert_eq!(p.position_at_progress(1.0), Some([5.0, 1.0, -2.0]));
        assert_eq!(p.duration(), Some(0.0));
        assert_eq!(p.sample_polyline(PATH_DEFAULT_STEP_M), vec![[5.0, 1.0, -2.0]]);
    }

    /// Linear 区間: 中間時刻でちょうど中点を返すこと。
    #[test]
    fn linear_interpolates_midpoint() {
        let pts = [
            cp([0.0, 0.0, 0.0], 0.0, ControlPointInterp::Linear),
            cp([10.0, 0.0, 0.0], 2.0, ControlPointInterp::Linear),
        ];
        let p = PathEval::from_points(&pts, &identity_actor());
        let mid = p.position_at_time(1.0).unwrap();
        assert!((mid[0] - 5.0).abs() < 1.0e-5, "中間時刻で中点になること: {mid:?}");
    }

    /// Step 区間: 次の点に到達するまで手前の点を保ち、到達時刻でだけ次の点になること。
    #[test]
    fn step_holds_previous_point() {
        let pts = [
            cp([0.0, 0.0, 0.0], 0.0, ControlPointInterp::Step),
            cp([10.0, 0.0, 0.0], 2.0, ControlPointInterp::Step),
        ];
        let p = PathEval::from_points(&pts, &identity_actor());
        assert_eq!(p.position_at_time(0.0).unwrap()[0], 0.0);
        assert_eq!(p.position_at_time(1.99).unwrap()[0], 0.0, "区間内は手前の点を保持");
        assert_eq!(p.position_at_time(2.0).unwrap()[0], 10.0, "末尾時刻では最終点");
    }

    /// CatmullRom 区間: 制御点を必ず通ること（時刻ちょうどで一致）。
    #[test]
    fn catmull_rom_passes_through_each_control_point() {
        let pts = [
            cp([0.0, 0.0, 0.0],  0.0, ControlPointInterp::CatmullRom),
            cp([1.0, 2.0, 0.0],  1.0, ControlPointInterp::CatmullRom),
            cp([3.0, 0.0, 1.0],  2.0, ControlPointInterp::CatmullRom),
            cp([5.0, 1.0, 3.0],  3.0, ControlPointInterp::CatmullRom),
        ];
        let p = PathEval::from_points(&pts, &identity_actor());
        for (i, want) in pts.iter().enumerate() {
            let got = p.position_at_time(i as f32).unwrap();
            for a in 0..3 {
                assert!((got[a] - want.position[a]).abs() < 1.0e-4,
                    "点 {i} を通ること: got={got:?} want={:?}", want.position);
            }
        }
    }

    /// 区間ごとに補間方法が切り替わること（前半 Linear・後半 Step）。
    #[test]
    fn interp_is_per_segment() {
        let pts = [
            cp([0.0, 0.0, 0.0],  0.0, ControlPointInterp::Linear),
            cp([10.0, 0.0, 0.0], 1.0, ControlPointInterp::Step),
            cp([20.0, 0.0, 0.0], 2.0, ControlPointInterp::Linear),
        ];
        let p = PathEval::from_points(&pts, &identity_actor());
        assert!((p.position_at_time(0.5).unwrap()[0] - 5.0).abs() < 1.0e-5, "前半は線形");
        assert_eq!(p.position_at_time(1.5).unwrap()[0], 10.0, "後半は階段保持");
    }

    /// 時刻クランプ: 範囲外は外挿せず両端で止まること。
    #[test]
    fn time_query_clamps_outside_range() {
        let pts = [
            cp([0.0, 0.0, 0.0],  10.0, ControlPointInterp::Linear),
            cp([10.0, 0.0, 0.0], 20.0, ControlPointInterp::Linear),
        ];
        let p = PathEval::from_points(&pts, &identity_actor());
        assert_eq!(p.position_at_time(-999.0).unwrap()[0], 0.0);
        assert_eq!(p.position_at_time(999.0).unwrap()[0], 10.0);
        assert_eq!(p.time_range(), Some((10.0, 20.0)));
        assert_eq!(p.duration(), Some(10.0));
    }

    /// 進捗 0..1 が時刻範囲へ写ること（時刻の原点が 0 でなくても正しく効く）。
    #[test]
    fn progress_maps_onto_time_range() {
        let pts = [
            cp([0.0, 0.0, 0.0],  100.0, ControlPointInterp::Linear),
            cp([10.0, 0.0, 0.0], 102.0, ControlPointInterp::Linear),
        ];
        let p = PathEval::from_points(&pts, &identity_actor());
        assert!((p.position_at_progress(0.5).unwrap()[0] - 5.0).abs() < 1.0e-5);
        assert_eq!(p.position_at_progress(0.0).unwrap()[0], 0.0);
        assert_eq!(p.position_at_progress(1.0).unwrap()[0], 10.0);
        // 範囲外の進捗はクランプされること
        assert_eq!(p.position_at_progress(-5.0).unwrap()[0], 0.0);
        assert_eq!(p.position_at_progress(5.0).unwrap()[0], 10.0);
    }

    /// 全点が同時刻（＝時刻未設定）でも、進捗が区間の添字空間へ写って等分に辿れること。
    #[test]
    fn progress_falls_back_to_index_space_when_duration_is_zero() {
        let pts = [
            cp([0.0, 0.0, 0.0],  0.0, ControlPointInterp::Linear),
            cp([10.0, 0.0, 0.0], 0.0, ControlPointInterp::Linear),
            cp([20.0, 0.0, 0.0], 0.0, ControlPointInterp::Linear),
        ];
        let p = PathEval::from_points(&pts, &identity_actor());
        assert!((p.position_at_progress(0.25).unwrap()[0] - 5.0).abs() < 1.0e-5);
        assert!((p.position_at_progress(0.75).unwrap()[0] - 15.0).abs() < 1.0e-5);
        assert_eq!(p.position_at_progress(1.0).unwrap()[0], 20.0);
    }

    /// アクタ Transform（平行移動・回転・スケール）がワールド解決に効くこと。
    #[test]
    fn actor_transform_resolves_points_to_world() {
        let actor = Transform {
            position: [100.0, 0.0, 0.0],
            rotation: [0.0, 90.0, 0.0], // Y 軸 +90°: ローカル +X → ワールド -Z
            scale:    [2.0, 2.0, 2.0],
        };
        let pts = [cp([1.0, 0.0, 0.0], 0.0, ControlPointInterp::Linear)];
        let p = PathEval::from_points(&pts, &actor);
        let w = p.position_at_time(0.0).unwrap();
        assert!((w[0] - 100.0).abs() < 1.0e-3, "X はアクタ位置のまま: {w:?}");
        assert!((w[1]).abs()        < 1.0e-3);
        assert!((w[2] + 2.0).abs()  < 1.0e-3, "スケール 2 × 回転 90° で -Z 方向へ 2m: {w:?}");
    }

    /// 折れ線化: Linear/Step の区間は分割されず、点数が「区間数 + 1」になること。
    #[test]
    fn polyline_does_not_subdivide_linear_or_step() {
        let pts = [
            cp([0.0, 0.0, 0.0],   0.0, ControlPointInterp::Linear),
            cp([100.0, 0.0, 0.0], 1.0, ControlPointInterp::Step),
            cp([200.0, 0.0, 0.0], 2.0, ControlPointInterp::Linear),
        ];
        let p  = PathEval::from_points(&pts, &identity_actor());
        let pl = p.sample_polyline(PATH_DEFAULT_STEP_M);
        assert_eq!(pl.len(), 3, "2 区間 + 終端 = 3 頂点: {pl:?}");
        assert_eq!(pl.first().copied(), Some([0.0, 0.0, 0.0]));
        assert_eq!(pl.last().copied(),  Some([200.0, 0.0, 0.0]));
    }

    /// 折れ線化: CatmullRom の区間は step_m に応じて分割されること。
    #[test]
    fn polyline_subdivides_catmull_rom_by_step() {
        let pts = [
            cp([0.0, 0.0, 0.0],  0.0, ControlPointInterp::CatmullRom),
            cp([10.0, 0.0, 0.0], 1.0, ControlPointInterp::CatmullRom),
        ];
        let p = PathEval::from_points(&pts, &identity_actor());
        // 弦長 10m を 2m 刻み → 5 分割 + 終端 = 6 頂点
        assert_eq!(p.sample_polyline(2.0).len(), 6);
        // 刻みを細かくすれば頂点が増えること
        assert!(p.sample_polyline(0.5).len() > p.sample_polyline(2.0).len());
    }

    /// 折れ線化: 総分割数が上限で頭打ちになること（描画コストの上限保証）。
    #[test]
    fn polyline_respects_segment_cap() {
        let pts = [
            cp([0.0, 0.0, 0.0],       0.0, ControlPointInterp::CatmullRom),
            cp([1_000_000.0, 0.0, 0.0], 1.0, ControlPointInterp::CatmullRom),
        ];
        let p  = PathEval::from_points(&pts, &identity_actor());
        let pl = p.sample_polyline(PATH_MIN_STEP_M);
        assert!(pl.len() <= PATH_MAX_POLYLINE_SEGMENTS + 1, "上限 + 終端を超えないこと: {}", pl.len());
    }

    /// 折れ線化: step_m に 0 や負値を渡しても発散せず、下限で切られること。
    #[test]
    fn polyline_clamps_non_positive_step() {
        let pts = [
            cp([0.0, 0.0, 0.0],  0.0, ControlPointInterp::CatmullRom),
            cp([10.0, 0.0, 0.0], 1.0, ControlPointInterp::CatmullRom),
        ];
        let p = PathEval::from_points(&pts, &identity_actor());
        let pl = p.sample_polyline(0.0);
        assert!(!pl.is_empty());
        assert!(pl.len() <= PATH_MAX_POLYLINE_SEGMENTS + 1);
    }

    /// 1 区間の折れ線化は両端を含み、区間外の添字では空を返すこと。
    #[test]
    fn segment_polyline_includes_both_ends() {
        let pts = [
            cp([0.0, 0.0, 0.0],  0.0, ControlPointInterp::Linear),
            cp([10.0, 0.0, 0.0], 1.0, ControlPointInterp::CatmullRom),
            cp([20.0, 0.0, 0.0], 2.0, ControlPointInterp::Linear),
        ];
        let p = PathEval::from_points(&pts, &identity_actor());
        // Linear 区間: 分割されず両端 2 頂点だけ
        let s0 = p.segment_polyline(0, PATH_DEFAULT_STEP_M);
        assert_eq!(s0.len(), 2);
        assert_eq!(s0.first().copied(), Some([0.0, 0.0, 0.0]));
        assert_eq!(s0.last().copied(),  Some([10.0, 0.0, 0.0]));
        // CatmullRom 区間: 分割される（弦長 10m / 2m = 5 分割 + 終端）
        assert_eq!(p.segment_polyline(1, 2.0).len(), 6);
        // 存在しない区間
        assert!(p.segment_polyline(2, PATH_DEFAULT_STEP_M).is_empty());
        assert!(p.segment_polyline(99, PATH_DEFAULT_STEP_M).is_empty());
    }

    /// 拡縮: 点の scale がアクタスケールと合成されてワールド拡縮になること。
    /// **かつ、点のスケールは点の位置を動かさないこと**（川など位置のみ使う消費側への無影響の保証）。
    #[test]
    fn point_scale_composes_with_actor_and_does_not_move_position() {
        let actor = Transform {
            position: [10.0, 0.0, 0.0],
            rotation: [0.0, 0.0, 0.0],
            scale:    [2.0, 2.0, 2.0],
        };
        let mut p = cp([3.0, 0.0, 0.0], 0.0, ControlPointInterp::Linear);
        p.scale = [4.0, 1.0, 0.5];

        // 比較用: 同じ点でスケールだけ等倍のもの
        let plain = cp([3.0, 0.0, 0.0], 0.0, ControlPointInterp::Linear);

        let scaled_eval = PathEval::from_points(&[p], &actor);
        let plain_eval  = PathEval::from_points(&[plain], &actor);
        let scaled = scaled_eval.points()[0];
        let plain_r = plain_eval.points()[0];

        // 位置はスケールの有無で変わらない（点は自分の原点まわりに拡縮するため）
        for a in 0..3 {
            assert!((scaled.position[a] - plain_r.position[a]).abs() < 1.0e-4,
                "点スケールが位置を動かしてはいけない: {:?} vs {:?}", scaled.position, plain_r.position);
        }
        // ワールド拡縮 = アクタスケール × 点スケール
        assert!((scaled.scale[0] - 8.0).abs() < 1.0e-3, "{:?}", scaled.scale);
        assert!((scaled.scale[1] - 2.0).abs() < 1.0e-3, "{:?}", scaled.scale);
        assert!((scaled.scale[2] - 1.0).abs() < 1.0e-3, "{:?}", scaled.scale);
    }

    /// 拡縮: Linear は軸ごとに線形補間され、Step は保持されること（姿勢と同じ規則）。
    #[test]
    fn scale_interpolates_per_segment_mode() {
        let mut a = cp([0.0; 3], 0.0, ControlPointInterp::Linear);
        a.scale = [1.0, 1.0, 1.0];
        let mut b = cp([1.0, 0.0, 0.0], 1.0, ControlPointInterp::Linear);
        b.scale = [3.0, 3.0, 3.0];
        let p = PathEval::from_points(&[a, b], &identity_actor());
        let s = p.sample_at_time(0.5).unwrap();
        assert!((s.scale[0] - 2.0).abs() < 1.0e-3, "中間で 2 倍になること: {:?}", s.scale);

        let mut a2 = a; a2.interp = ControlPointInterp::Step;
        let p2 = PathEval::from_points(&[a2, b], &identity_actor());
        assert!((p2.sample_at_time(0.5).unwrap().scale[0] - 1.0).abs() < 1.0e-3, "Step は保持");
    }

    /// 姿勢: Linear は軸ごとに線形補間され、Step は保持されること。
    #[test]
    fn rotation_interpolates_per_segment_mode() {
        let mut a = cp([0.0; 3], 0.0, ControlPointInterp::Linear);
        a.rotation = [0.0, 0.0, 0.0];
        let mut b = cp([1.0, 0.0, 0.0], 1.0, ControlPointInterp::Linear);
        b.rotation = [0.0, 40.0, 0.0];
        let p = PathEval::from_points(&[a, b], &identity_actor());
        let s = p.sample_at_time(0.5).unwrap();
        assert!((s.rotation[1] - 20.0).abs() < 1.0e-3, "Y 角が中間になること: {:?}", s.rotation);

        let mut a2 = a; a2.interp = ControlPointInterp::Step;
        let p2 = PathEval::from_points(&[a2, b], &identity_actor());
        assert!((p2.sample_at_time(0.5).unwrap().rotation[1]).abs() < 1.0e-3, "Step は保持");
    }
}
