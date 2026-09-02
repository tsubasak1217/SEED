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
