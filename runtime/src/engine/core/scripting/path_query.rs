// ============================================================
//  path_query.rs — スクリプトへ公開するパス評価（ControlPoint）の純関数層
//
//  C# の `SEED.ControlPointPath`（`gameObject.GetComponent<ControlPointPath>()`）が
//  問い合わせる「時刻 → ワールド位置」「時刻 → 進行方向（接線）」を計算する。
//
//  ## なぜ host_api.rs に直接書かないのか
//  host_api.rs は「World ポインタ・FFI 境界・文字列レジストリ」の置き場であり、
//  評価そのもののロジックが混ざると World を用意しないとテストできなくなる。
//  ここは **PathEval だけを引数に取る純関数**に閉じてあるので、
//  World も FFI も無しでユニットテストできる（＝周回・クランプ・接線の契約を守れる）。
//
//  ## 座標系
//  `PathEval` は構築時にアクタ変換を掛け済みなので、本モジュールの入出力は
//  すべて**ワールド空間**である。アクタ変換の合成は呼び出し側（host_api）の責務。
//
//  ## 時刻の扱い（PathEval と同義であることが契約）
//  閉ループは 1 周ぶんの時刻で剰余を取って**周回**し、開いたパスは両端で**クランプ**する。
//  この規則は `PathEval::sample_at_time` にのみ実装があり、本モジュールは
//  自前で剰余やクランプを書かない（規則を 2 箇所に持たないため）。
// ============================================================

use crate::engine::path::PathEval;

// ─── 定数（マジックナンバー禁止）─────────────────────────────

/// 接線（進行方向）を数値微分で求めるときの時刻の刻み幅（秒）。
///
/// パスの解析的な微分は補間方法（Linear / CatmullRom / Step）ごとに別式になり、
/// 区間の継ぎ目で不連続になる。**中央差分 1 本**に統一すれば、補間方法が増えても
/// この層は無変更で済み、継ぎ目でも左右の平均として滑らかに繋がる。
///
/// 値は「1 ミリ秒ぶん先の位置との差」。制御点の既定間隔（1 点 = 1 秒）に対して
/// 十分小さく、かつ f32 の桁落ちで差がゼロに潰れない大きさとして選んでいる。
pub const PATH_TANGENT_EPSILON: f32 = 1.0e-3;

/// 接線を「向きが定まった」とみなす最小の差分長（ワールド単位）。
///
/// Step 補間の区間内・停留点・全点同一座標のパスでは差分が 0 になる。
/// そこを無理に正規化すると NaN／暴れたベクトルが出るので、
/// この閾値未満は「向き無し（None）」として呼び出し側に判断させる。
pub const PATH_TANGENT_MIN_LEN: f32 = 1.0e-6;

// ─── 位置の問い合わせ ─────────────────────────────────────────

/// 指定時刻におけるパス上のワールド位置。点が 0 個なら None。
///
/// 閉ループは周回、開いたパスは両端クランプ（`PathEval::sample_at_time` の規則そのまま）。
pub fn path_position_at(eval: &PathEval, time: f32) -> Option<[f32; 3]> {
    eval.position_at_time(time)
}

// ─── 接線（進行方向）の問い合わせ ─────────────────────────────

/// 指定時刻におけるパスの進行方向（**単位ベクトル・ワールド空間**）。
///
/// 中央差分 `p(t + ε) − p(t − ε)` を正規化して返す。
/// 向きが定まらない場合（点が 1 個以下／Step 補間の区間内／全点が同一座標）は None。
///
/// ## 端の扱い
/// - 閉ループ: 時刻は周回するのでクランプ不要。継ぎ目でも正しく繋がる。
/// - 開いたパス: 差分の両端が同じ位置へクランプされると差が 0 に潰れるため、
///   評価時刻そのものを `[t0 + ε, t1 − ε]` に寄せてから中央差分を取る。
///   これで始点・終点でも「そこでの進行方向」が返る。
/// - 時刻の幅が 2ε 以下の縮退パス（全点が同時刻など）は、
///   先頭時刻と末尾時刻の 2 点差分で代用する。
pub fn path_tangent_at(eval: &PathEval, time: f32) -> Option<[f32; 3]> {
    let (t0, t1) = eval.time_range()?;

    // 差分を取る 2 つの時刻を決める（規則は上のドキュメントコメントのとおり）
    let (ta, tb) = if eval.is_closed() {
        // 閉ループ: 周回するのでそのまま前後へずらす
        (time - PATH_TANGENT_EPSILON, time + PATH_TANGENT_EPSILON)
    } else if t1 - t0 <= PATH_TANGENT_EPSILON * 2.0 {
        // 縮退（時刻の幅が刻み幅以下）: 端から端への向きで代用する
        (t0, t1)
    } else {
        // 開いたパス: 中央差分が両端クランプで潰れないよう評価時刻を内側へ寄せる
        let center = time.clamp(t0 + PATH_TANGENT_EPSILON, t1 - PATH_TANGENT_EPSILON);
        (center - PATH_TANGENT_EPSILON, center + PATH_TANGENT_EPSILON)
    };

    let a = eval.position_at_time(ta)?;
    let b = eval.position_at_time(tb)?;
    normalize_or_none([b[0] - a[0], b[1] - a[1], b[2] - a[2]])
}

/// ベクトルを正規化する。長さが `PATH_TANGENT_MIN_LEN` 未満なら None（向き無し）。
fn normalize_or_none(v: [f32; 3]) -> Option<[f32; 3]> {
    let len = (v[0] * v[0] + v[1] * v[1] + v[2] * v[2]).sqrt();
    if !len.is_finite() || len < PATH_TANGENT_MIN_LEN {
        return None;
    }
    Some([v[0] / len, v[1] / len, v[2] / len])
}

// ============================================================
//  ユニットテスト（World も FFI も使わない純関数のテスト）
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;
    use crate::engine::components::control_point_component::{
        ControlPoint, ControlPointComponent, ControlPointInterp,
    };
    use crate::engine::components::Transform;

    /// 位置 p・時刻 t・直線補間の制御点を作る（テストの読みやすさのため）。
    fn linear_point(p: [f32; 3], t: f32) -> ControlPoint {
        ControlPoint { position: p, time: t, interp: ControlPointInterp::Linear, ..Default::default() }
    }

    /// X 軸に沿って 0→1→2 と並ぶ、時刻 0/1/2 の直線パス（開いたパス）。
    fn straight_open() -> ControlPointComponent {
        ControlPointComponent {
            points: vec![
                linear_point([0.0, 0.0, 0.0], 0.0),
                linear_point([1.0, 0.0, 0.0], 1.0),
                linear_point([2.0, 0.0, 0.0], 2.0),
            ],
            closed: false,
        }
    }

    /// XZ 平面の正方形ループ（時刻 0/1/2/3、閉ループ）。
    fn square_closed() -> ControlPointComponent {
        ControlPointComponent {
            points: vec![
                linear_point([0.0, 0.0, 0.0], 0.0),
                linear_point([1.0, 0.0, 0.0], 1.0),
                linear_point([1.0, 0.0, 1.0], 2.0),
                linear_point([0.0, 0.0, 1.0], 3.0),
            ],
            closed: true,
        }
    }

    /// 2 つの座標がほぼ等しいこと（f32 の丸めを許容）。
    fn assert_close(a: [f32; 3], b: [f32; 3], tol: f32, msg: &str) {
        for i in 0..3 {
            assert!((a[i] - b[i]).abs() <= tol, "{msg}: {a:?} != {b:?}（成分 {i}）");
        }
    }

    /// **アクタ変換（平行移動＋スケール）がワールド位置へ反映されること。**
    /// スクリプト API は「アクタを動かすとパスごと動く」ことを前提に使われるため、
    /// ここが崩れると経路移動がアクタ位置からずれる。
    #[test]
    fn position_includes_actor_transform() {
        let comp = straight_open();
        let actor = Transform {
            position: [10.0, 5.0, -3.0],
            scale: [2.0, 2.0, 2.0],
            ..Transform::identity()
        };
        let eval = PathEval::from_component(&comp, &actor);

        // ローカル (1,0,0) は スケール2 → (2,0,0)、平行移動で (12,5,-3)
        assert_close(
            path_position_at(&eval, 1.0).expect("時刻 1 の位置が取れること"),
            [12.0, 5.0, -3.0],
            1.0e-4,
            "アクタ変換（平行移動＋スケール）が掛かっていること",
        );
    }

    /// **アクタ回転もワールド位置へ反映されること（Y 90 度）。**
    #[test]
    fn position_includes_actor_rotation() {
        let comp = straight_open();
        // Y 軸 +90 度。+X は -Z へ回る（Transform::rotation_basis の規約）。
        let actor = Transform { rotation: [0.0, 90.0, 0.0], ..Transform::identity() };
        let eval = PathEval::from_component(&comp, &actor);

        let p = path_position_at(&eval, 2.0).expect("時刻 2 の位置が取れること");
        assert!(p[0].abs() < 1.0e-3, "X 成分は 0 付近になるはず（実際: {p:?}）");
        assert!(p[2].abs() > 1.0, "Z 成分へ回り込むはず（実際: {p:?}）");
    }

    /// **開いたパスは区間外でクランプされること**（外挿してパス外へ飛び出さない）。
    #[test]
    fn open_path_clamps_outside_range() {
        let eval = PathEval::from_component(&straight_open(), &Transform::identity());
        assert_close(
            path_position_at(&eval, -100.0).expect("先頭より前でも値が返ること"),
            [0.0, 0.0, 0.0], 1.0e-4, "先頭でクランプ",
        );
        assert_close(
            path_position_at(&eval, 100.0).expect("末尾より後でも値が返ること"),
            [2.0, 0.0, 0.0], 1.0e-4, "末尾でクランプ",
        );
    }

    /// **閉ループは 1 周ぶんの時刻で周回すること**（t と t + duration が同じ位置）。
    /// 経路移動スクリプトが「時刻を増やし続けるだけでぐるぐる回れる」ための契約。
    #[test]
    fn closed_path_wraps_by_duration() {
        let eval = PathEval::from_component(&square_closed(), &Transform::identity());
        let dur = eval.duration().expect("閉ループの 1 周時間が取れること");
        assert!(dur > 0.0, "1 周時間は正（実際: {dur}）");

        for t in [0.0f32, 0.5, 1.7, 3.2] {
            let a = path_position_at(&eval, t).expect("周回前の位置");
            let b = path_position_at(&eval, t + dur).expect("1 周後の位置");
            assert_close(a, b, 1.0e-3, "1 周後は同じ位置に戻ること");
        }
        // 負方向へ回しても同じ（rem_euclid による周回）
        let a = path_position_at(&eval, 0.25).expect("正方向");
        let b = path_position_at(&eval, 0.25 - dur).expect("負方向へ 1 周");
        assert_close(a, b, 1.0e-3, "負の時刻でも周回すること");
    }

    /// **接線が進行方向の単位ベクトルであること**（直線パスの中央）。
    #[test]
    fn tangent_is_unit_forward_direction() {
        let eval = PathEval::from_component(&straight_open(), &Transform::identity());
        let t = path_tangent_at(&eval, 1.0).expect("中央では向きが定まること");
        assert_close(t, [1.0, 0.0, 0.0], 1.0e-3, "+X 方向へ進むこと");
        let len = (t[0] * t[0] + t[1] * t[1] + t[2] * t[2]).sqrt();
        assert!((len - 1.0).abs() < 1.0e-4, "単位長であること（実際: {len}）");
    }

    /// **開いたパスの端でも接線が返ること**（両端クランプで 0 に潰れない）。
    #[test]
    fn tangent_available_at_open_path_ends() {
        let eval = PathEval::from_component(&straight_open(), &Transform::identity());
        for t in [-10.0f32, 0.0, 2.0, 10.0] {
            let d = path_tangent_at(&eval, t)
                .unwrap_or_else(|| panic!("時刻 {t} でも向きが返ること"));
            assert_close(d, [1.0, 0.0, 0.0], 1.0e-3, "端でも +X 方向");
        }
    }

    /// **接線にもアクタ回転が掛かること**（ワールド空間の向きであることの確認）。
    #[test]
    fn tangent_is_in_world_space() {
        let actor = Transform { rotation: [0.0, 90.0, 0.0], ..Transform::identity() };
        let eval = PathEval::from_component(&straight_open(), &actor);
        let d = path_tangent_at(&eval, 1.0).expect("向きが定まること");
        assert!(d[0].abs() < 1.0e-2, "ローカル +X はワールドでは X 成分をほぼ失う（実際: {d:?}）");
        assert!(d[2].abs() > 0.9, "Z 成分へ回ること（実際: {d:?}）");
    }

    /// **閉ループの継ぎ目（最後の点 → 最初の点）でも接線が返ること。**
    #[test]
    fn tangent_available_across_closing_segment() {
        let eval = PathEval::from_component(&square_closed(), &Transform::identity());
        let dur = eval.duration().expect("1 周時間");
        // 継ぎ目のわずかに手前・わずかに後ろ
        for t in [dur - 0.1, dur + 0.1] {
            let d = path_tangent_at(&eval, t).unwrap_or_else(|| panic!("時刻 {t} で向きが返ること"));
            let len = (d[0] * d[0] + d[1] * d[1] + d[2] * d[2]).sqrt();
            assert!((len - 1.0).abs() < 1.0e-3, "単位長（実際: {len}）");
        }
    }

    /// **点が 0 個のパスは位置も接線も None**（呼び出し側が既定値へ落とせる）。
    #[test]
    fn empty_path_returns_none() {
        let eval = PathEval::from_component(&ControlPointComponent::default(), &Transform::identity());
        assert!(path_position_at(&eval, 0.0).is_none(), "点 0 個なら位置は None");
        assert!(path_tangent_at(&eval, 0.0).is_none(), "点 0 個なら接線は None");
    }

    /// **全点が同一座標なら接線は None**（0 ベクトルを正規化して NaN を出さない）。
    #[test]
    fn degenerate_path_has_no_tangent() {
        let comp = ControlPointComponent {
            points: vec![
                linear_point([1.0, 2.0, 3.0], 0.0),
                linear_point([1.0, 2.0, 3.0], 1.0),
            ],
            closed: false,
        };
        let eval = PathEval::from_component(&comp, &Transform::identity());
        assert!(path_tangent_at(&eval, 0.5).is_none(), "同一座標では向きが定まらない");
    }
}

// ============================================================
//  継ぎ目（閉ループの「最後の点 → 最初の点」）の連続性テスト
//
//  経路移動の体感バグの再現テスト。
//    ・「継ぎ目の通過に時間がかかる／そこだけ突進する」 → **速度の不連続**
//    ・「一度クルっと回ってもう一度前を向く」           → **接線の反転（カスプ）**
//
//  ## 調査でわかったこと（2 つの疑いのうち、実際に壊れていたのは 1 つ）
//  ・速度の不連続は**実在した**。閉じる区間の所要時刻が固定 1 秒だったため、
//    弦長が他区間と違うパスで継ぎ目の平均速度が 3〜5 倍ずれていた（下のテストが再現する）。
//  ・接線の反転は**再現しなかった**。閉ループの Catmull-Rom は隣接点を `% n` で
//    正しく周回して取っており、1ms 刻みで 1 周走査しても内積は 0.99 を下回らない
//    （粗く 50ms 刻みで測ると跳んで見えるが、それはサンプリングの粗さであって
//    曲線の不連続ではない）。見えていた「クルっと回る」は速度の跳ねの副作用
//    ＝ 継ぎ目で数倍速く走るぶん進行方向が 1 フレームで大きく変わり、
//    最短回りの補間が逆回りを選ぶ、という筋。接線の連続性テストは
//    「今は壊れていない」ことを固定する退行防止として残す。
//
//  ## 何を基準にするか
//  補間方法そのものが持つ不連続（Linear は制御点で必ず角ばる、CatmullRom は
//  区間ごとに時刻で正規化するので点上で速さが変わる）は仕様であってバグではない。
//  検証したいのは「**継ぎ目だけが他の点より悪くない**」ことなので、
//  継ぎ目での跳ね幅を**内側の制御点での跳ね幅の最大値**と比べる。
//  こうすると「閉じる区間の所要時刻が固定値」というバグだけを狙って落とせる。
// ============================================================

#[cfg(test)]
mod seam_continuity {
    use super::*;
    use crate::engine::components::control_point_component::{
        ControlPoint, ControlPointComponent, ControlPointInterp,
    };
    use crate::engine::components::Transform;

    // ─── 測定パラメータ（マジックナンバー禁止）───────────────

    /// 制御点の直前・直後で速度を測るときの、制御点からの離し幅（秒）。
    /// 中央差分が制御点をまたがない程度に小さく、かつ f32 で差が潰れない大きさ。
    const KNOT_PROBE_OFFSET: f32 = 2.0e-3;

    /// 速度を測る中央差分の半幅（秒）。`KNOT_PROBE_OFFSET` より小さいこと。
    const PROBE_H: f32 = 5.0e-4;

    /// 「継ぎ目が内側の制御点より悪くない」と認める許容倍率。
    /// f32 の丸めと、閉じる区間だけ隣接点の取り方が違うぶんの誤差を吸収する。
    const SEAM_TOLERANCE: f32 = 1.05;

    /// 閉じる区間の平均速度が通常区間の平均速度と「揃っている」と認める比の上限。
    ///
    /// 道のりを折れ線で近似しているぶんの誤差（数 %）を吸収する幅。
    /// 固定 1 秒だった従来実装では、間隔の不揃いなパスでこの比が 4〜5 倍に達する。
    const SEAM_AVERAGE_SPEED_RATIO_MAX: f32 = 1.10;

    /// 曲線を走査する刻み幅（秒）。区間内で一気に進んでも取りこぼさない細かさにする。
    const SCAN_STEP: f32 = 1.0e-3;

    /// 隣り合うサンプル（`SCAN_STEP` = 1ms ぶん）の接線が「連続に繋がっている」と
    /// みなす内積の下限。約 8 度まで許す。
    ///
    /// 制御点 1 つぶんが既定 1 秒なので、1ms は区間の 1/1000。滑らかな曲線なら
    /// どんなに急な曲がりでもこの間に 1 度も回らない。これを割るのは
    /// **カスプ（速度が 0 に潰れて向きが飛ぶ点）がある**ときだけで、
    /// それが「一度クルっと回ってもう一度前を向く」の候補だった症状にあたる。
    const TANGENT_DOT_MIN: f32 = 0.99;

    /// 速度比の判定から外す下限速度（実質停止しているサンプル）。
    const SPEED_MIN: f32 = 1.0e-3;

    // ─── ヘルパ ─────────────────────────────────────────────

    /// 位置の列から「時刻 = 添字」「指定の補間方法」の閉ループを作る。
    fn closed_loop(positions: &[[f32; 3]], interp: ControlPointInterp) -> ControlPointComponent {
        ControlPointComponent {
            points: positions
                .iter()
                .enumerate()
                .map(|(i, p)| ControlPoint {
                    position: *p,
                    time: i as f32,
                    interp,
                    ..Default::default()
                })
                .collect(),
            closed: true,
        }
    }

    /// XZ 平面の正 n 角形（半径 r）。等間隔＝継ぎ目も他区間と同条件になる基準ケース。
    fn regular_polygon(n: usize, r: f32) -> Vec<[f32; 3]> {
        let angles: Vec<f32> = (0..n)
            .map(|i| (i as f32) / (n as f32) * std::f32::consts::TAU)
            .collect();
        ring(&angles, r)
    }

    /// 半径 r の円周上に、指定した角度（度）の順で点を置いた輪。
    ///
    /// 角度の間隔を変えることで「区間の弦長だけが不揃いな、素直な凸ループ」を作れる。
    /// **行って戻るだけのヘアピン経路を検証に使ってはいけない**: 折り返し点では
    /// 進行方向が本当に 180 度反転するので、バグでない反転を検出してしまう。
    fn ring(angles_rad: &[f32], r: f32) -> Vec<[f32; 3]> {
        angles_rad.iter().map(|a| [r * a.cos(), 0.0, r * a.sin()]).collect()
    }

    /// 度 → ラジアン（テストの読みやすさのため）。
    fn deg(d: f32) -> f32 {
        d.to_radians()
    }

    /// 時刻 t における経路上の速さ（|dP/dt|）を中央差分で測る。
    fn speed_at(eval: &PathEval, t: f32) -> f32 {
        let a = eval.position_at_time(t - PROBE_H).expect("位置が取れること");
        let b = eval.position_at_time(t + PROBE_H).expect("位置が取れること");
        let d = [b[0] - a[0], b[1] - a[1], b[2] - a[2]];
        (d[0] * d[0] + d[1] * d[1] + d[2] * d[2]).sqrt() / (2.0 * PROBE_H)
    }

    /// 制御点（時刻 `t`）をまたぐ速さの跳ね幅（`速い側 / 遅い側`、1.0 が完全連続）。
    fn speed_jump_at_knot(eval: &PathEval, t: f32) -> f32 {
        let before = speed_at(eval, t - KNOT_PROBE_OFFSET);
        let after = speed_at(eval, t + KNOT_PROBE_OFFSET);
        if before <= SPEED_MIN || after <= SPEED_MIN {
            return 1.0; // 実質停止している点は比が意味を持たない
        }
        before.max(after) / before.min(after)
    }

    /// **継ぎ目の速度の跳ね幅が、内側の制御点の跳ね幅を超えないこと**を表明する。
    ///
    /// 継ぎ目は 2 箇所ある（最後の点で閉じる区間へ入るところ、
    /// 最初の点で通常区間へ戻るところ）ので両方を見る。
    fn assert_seam_speed_no_worse_than_interior(name: &str, comp: &ControlPointComponent) {
        let eval = PathEval::from_component(comp, &Transform::identity());
        let n = comp.points.len();

        // 内側の制御点（点 1 〜 点 n-2）での跳ね幅の最大値 ＝ このパス固有の「普通の悪さ」。
        let interior = (1..n - 1)
            .map(|i| speed_jump_at_knot(&eval, comp.points[i].time))
            .fold(1.0f32, f32::max);

        // 継ぎ目の 2 箇所。1 周の終わり（= 最初の点へ戻るところ）は duration の位置。
        let last_time = comp.points[n - 1].time;
        let loop_end = comp.points[0].time + eval.duration().expect("1 周時間");
        let seam_in = speed_jump_at_knot(&eval, last_time);
        let seam_out = speed_jump_at_knot(&eval, loop_end);
        let seam = seam_in.max(seam_out);

        let limit = interior * SEAM_TOLERANCE;
        assert!(
            seam <= limit,
            "[{name}] 継ぎ目だけ速度が跳んでいる: 継ぎ目 {seam:.3}（入り {seam_in:.3} / 出口 {seam_out:.3}）\
             > 内側の制御点の最大 {interior:.3} × 許容 {SEAM_TOLERANCE}\
             ＝ 継ぎ目だけ突進する／足踏みする",
        );
    }

    /// **閉じる区間の平均速度が、通常区間の平均速度と一致すること**を表明する。
    ///
    /// 「継ぎ目の通過に時間がかかる／そこだけ突進する」は区間まるごとの体感なので、
    /// 瞬間速度ではなく**区間の平均速度**で見るのが症状に対応した測り方。
    /// 瞬間速度の連続性まで求めると、区間ごとに時刻で正規化する Catmull-Rom では
    /// 原理的に達成できない（区間の弦長で時刻を割り当て直す＝別の設計になる）。
    fn assert_seam_average_speed_matches(name: &str, comp: &ControlPointComponent) {
        let eval = PathEval::from_component(comp, &Transform::identity());
        let n = comp.points.len();
        let t0 = comp.points[0].time;
        let last_time = comp.points[n - 1].time;

        // 区間の道のりを折れ線で測る（時刻で等分してサンプルする）。
        let travelled = |from: f32, to: f32| -> f32 {
            const SAMPLES: usize = 64;
            let mut len = 0.0;
            let mut prev = eval.position_at_time(from).expect("位置");
            for s in 1..=SAMPLES {
                let t = from + (to - from) * (s as f32 / SAMPLES as f32);
                let p = eval.position_at_time(t).expect("位置");
                let d = [p[0] - prev[0], p[1] - prev[1], p[2] - prev[2]];
                len += (d[0] * d[0] + d[1] * d[1] + d[2] * d[2]).sqrt();
                prev = p;
            }
            len
        };

        let interior_speed = travelled(t0, last_time) / (last_time - t0);
        let closing = eval.closing_duration();
        // 継ぎ目の直前・直後をわずかに避けてサンプルする（境界での丸め対策）。
        let closing_speed = travelled(last_time, last_time + closing) / closing;

        let ratio = closing_speed.max(interior_speed) / closing_speed.min(interior_speed);
        assert!(
            ratio <= SEAM_AVERAGE_SPEED_RATIO_MAX,
            "[{name}] 閉じる区間の平均速度が他区間と揃っていない: \
             閉じる区間 {closing_speed:.3} / 通常区間 {interior_speed:.3}（比 {ratio:.3} > {SEAM_AVERAGE_SPEED_RATIO_MAX}）\
             ＝ 継ぎ目だけ突進する／通過に時間がかかる",
        );
    }

    /// **1 周を通して接線が連続であること**を表明する（CatmullRom 専用）。
    ///
    /// Linear は制御点で必ず角ばる（＝接線が跳ぶ）ので、この判定の対象外。
    ///
    /// 継ぎ目を特別扱いせず 1 周まるごと走査するのは、
    /// 「継ぎ目だけ直しても他の点でカスプが残れば同じ症状が出る」ため。
    fn assert_no_cusp(name: &str, comp: &ControlPointComponent) {
        let eval = PathEval::from_component(comp, &Transform::identity());
        let dur = eval.duration().expect("1 周時間");
        let t0 = comp.points[0].time;

        let mut prev: Option<[f32; 3]> = None;
        let mut t = t0;
        while t <= t0 + dur {
            let tan = path_tangent_at(&eval, t).expect("1 周を通して向きが定まること");
            if let Some(p) = prev {
                let dot: f32 = p[0] * tan[0] + p[1] * tan[1] + p[2] * tan[2];
                assert!(
                    dot >= TANGENT_DOT_MIN,
                    "[{name}] 時刻 {t:.3} で接線が跳んだ（内積 {dot:.5} < {TANGENT_DOT_MIN}）: \
                     {p:?} → {tan:?} ＝ 曲線にカスプ／輪ができており、\
                     目標ヨーが大きく回る（「クルっと回る」）",
                );
            }
            prev = Some(tan);
            t += SCAN_STEP;
        }
    }

    // ─── 疑い (a): 閉じる区間の所要時刻が固定値だった問題 ───────

    /// 閉じる区間の所要時刻が**弦長に比例**すること（新しい時刻定義そのものの表明）。
    ///
    /// 等速で並べた 3 点（10m/s）＋ 長さ 20m の閉じる弦 → 閉じる区間は 2 秒。
    #[test]
    fn closing_duration_is_proportional_to_chord_length() {
        let comp = closed_loop(
            &[[0.0, 0.0, 0.0], [10.0, 0.0, 0.0], [20.0, 0.0, 0.0]],
            ControlPointInterp::Linear,
        );
        let eval = PathEval::from_component(&comp, &Transform::identity());
        let closing = eval.closing_duration();
        assert!(
            (closing - 2.0).abs() < 1.0e-3,
            "閉じる区間は平均速度 10m/s で 20m ＝ 2 秒になること（実際: {closing}）",
        );
        assert!(
            (eval.duration().expect("1 周時間") - 4.0).abs() < 1.0e-3,
            "1 周 = 2 秒（通常区間） + 2 秒（閉じる区間）",
        );
    }

    /// **等間隔の輪では従来どおり 1 秒**であること（既定の点列の見え方を変えていない）。
    #[test]
    fn closing_duration_matches_default_step_on_uniform_loop() {
        let comp = closed_loop(&regular_polygon(8, 10.0), ControlPointInterp::Linear);
        let eval = PathEval::from_component(&comp, &Transform::identity());
        assert!(
            (eval.closing_duration() - 1.0).abs() < 1.0e-3,
            "等間隔・等時間なら閉じる区間も 1 秒（実際: {}）",
            eval.closing_duration(),
        );
    }

    /// **最後の点が最初の点とほぼ重なる**輪で、継ぎ目に足踏みが生じないこと。
    ///
    /// 「手で輪を閉じたうえで closed も立てた」よくある作り方。
    /// 固定 1 秒だと、ほぼ 0m の区間に丸 1 秒かかって必ずその場で止まって見える。
    #[test]
    fn duplicated_endpoint_does_not_stall() {
        let comp = closed_loop(
            &[
                [10.0, 0.0, 0.0],
                [0.0, 0.0, 10.0],
                [-10.0, 0.0, 0.0],
                [0.0, 0.0, -10.0],
                [10.0, 0.0, 0.0], // 先頭と同じ位置
            ],
            ControlPointInterp::Linear,
        );
        let eval = PathEval::from_component(&comp, &Transform::identity());
        let closing = eval.closing_duration();
        assert!(
            closing < 0.01,
            "重なった端点の区間は実質ゼロ秒であること（実際: {closing} 秒）",
        );
        assert!(closing > 0.0, "ゼロ除算を避けるため正であること（実際: {closing}）");
    }

    /// **閉じる弦が他区間より長い輪**で、継ぎ目だけ速度が跳ばないこと。
    /// 固定 1 秒だと閉じる区間で数倍に突進する。
    #[test]
    fn long_closing_chord_speed_is_no_worse_than_interior() {
        // 円周上の 0°/40°/80°/120°/160° に点を密に置く。
        // 残りの 160° → 360°（＝閉じる区間）だけが他の 5 倍の弧になる。
        let pts = ring(&[deg(0.0), deg(40.0), deg(80.0), deg(120.0), deg(160.0)], 10.0);
        for interp in [ControlPointInterp::Linear, ControlPointInterp::CatmullRom] {
            assert_seam_average_speed_matches(
                &format!("閉じる弦が長い輪 / {interp:?}"),
                &closed_loop(&pts, interp),
            );
        }
        // 直線補間では瞬間速度まで連続になること（区間内で速さが一定なので厳密に測れる）。
        assert_seam_speed_no_worse_than_interior(
            "閉じる弦が長い輪 / Linear",
            &closed_loop(&pts, ControlPointInterp::Linear),
        );
    }

    /// **閉じる弦が他区間より短い輪**で、継ぎ目だけ速度が落ちないこと
    /// （固定 1 秒だと継ぎ目で足踏みする ＝「通過に時間がかかる」の直接の再現）。
    #[test]
    fn short_closing_chord_speed_is_no_worse_than_interior() {
        // 円周上の 0°/120°/240°/330° に点を置く。最後の 330° → 360° が
        // 閉じる区間で、他の区間（120° ぶん）の 1/4 しかない。
        let pts = ring(&[deg(0.0), deg(120.0), deg(240.0), deg(330.0)], 10.0);
        for interp in [ControlPointInterp::Linear, ControlPointInterp::CatmullRom] {
            assert_seam_average_speed_matches(
                &format!("閉じる弦が短い輪 / {interp:?}"),
                &closed_loop(&pts, interp),
            );
        }
        assert_seam_speed_no_worse_than_interior(
            "閉じる弦が短い輪 / Linear",
            &closed_loop(&pts, ControlPointInterp::Linear),
        );
    }

    /// **等間隔の円**（既定の「1 点 = 1 秒」）で継ぎ目の速度が連続であること（退行防止）。
    #[test]
    fn uniform_circle_seam_speed_is_continuous() {
        for interp in [ControlPointInterp::Linear, ControlPointInterp::CatmullRom] {
            assert_seam_speed_no_worse_than_interior(
                &format!("等間隔の円 / {interp:?}"),
                &closed_loop(&regular_polygon(8, 10.0), interp),
            );
        }
    }

    // ─── 疑い (b): Catmull-Rom のカスプで接線が反転する問題 ─────

    /// **等間隔の円**では 1 周を通して接線が滑らかに回ること（基準ケース）。
    #[test]
    fn uniform_circle_has_no_cusp() {
        assert_no_cusp(
            "等間隔の円 / CatmullRom",
            &closed_loop(&regular_polygon(8, 10.0), ControlPointInterp::CatmullRom),
        );
    }

    /// **間隔が不揃いな輪**を Catmull-Rom で閉じても接線が反転しないこと。
    ///
    /// uniform Catmull-Rom（α = 0）は隣接区間の弦長が大きく違う点の周りで輪を描き、
    /// 進行方向が一瞬逆を向く。中心求心（α = 0.5）版ならカスプも自己交差も生じない。
    #[test]
    fn nonuniform_loop_has_no_cusp() {
        // 前半に点を密集させ、閉じる区間だけ大きく空けた輪。
        // uniform Catmull-Rom はこの「間隔が急変する点」の周りで輪を描く。
        let pts = ring(&[deg(0.0), deg(40.0), deg(80.0), deg(120.0), deg(160.0)], 10.0);
        assert_no_cusp(
            "間隔が不揃いな輪 / CatmullRom",
            &closed_loop(&pts, ControlPointInterp::CatmullRom),
        );
    }

    /// **点が極端に密集した輪**でも接線が連続であること。
    ///
    /// 円周の 0°/5°/10° に 3 点を固めて置き、残りを 180° の 1 点で結ぶ輪。
    /// 隣接区間の弦長が 20 倍以上違う極端な配置で、Catmull-Rom の隣接点の
    /// 取り方（閉ループの `% n` 周回）が壊れると真っ先に破綻する形。
    #[test]
    fn clustered_points_have_no_cusp() {
        let pts = ring(&[deg(0.0), deg(5.0), deg(10.0), deg(180.0)], 10.0);
        assert_no_cusp(
            "密集した点の輪 / CatmullRom",
            &closed_loop(&pts, ControlPointInterp::CatmullRom),
        );
    }

    /// **縦横比の大きい楕円**でも接線が連続であること。
    #[test]
    fn ellipse_has_no_cusp() {
        let pts: Vec<[f32; 3]> = (0..8)
            .map(|i| {
                let a = (i as f32) / 8.0 * std::f32::consts::TAU;
                [20.0 * a.cos(), 0.0, 5.0 * a.sin()]
            })
            .collect();
        assert_no_cusp("楕円 / CatmullRom", &closed_loop(&pts, ControlPointInterp::CatmullRom));
    }
}

