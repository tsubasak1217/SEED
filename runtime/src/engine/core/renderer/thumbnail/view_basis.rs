// ============================================================
//  view_basis.rs — 任意方向からの構図決定（カメラ自動フィット）
// ------------------------------------------------------------
//  役割:
//    「被写体の AABB」と「見る向き」から、全体がちょうど収まる正射カメラを求める。
//    GPU にも ECS にも触れない純粋な幾何計算なので、そのまま単体テストできる。
//
//  なぜ actor_thumbnail から切り出したのか:
//    図鑑用のアクタサムネイルは真横・正面・真上の 3 方向しか使わないため、
//    元の実装は「画面横に写るのは size[2]」のように軸決め打ちで書かれていた。
//    モデルサムネイルは全体の形が分かる**斜め上から**の視点が要るので、
//    軸に縛られない一般形が必要になる。
//
//    ここではその一般形だけを持ち、`actor_thumbnail::compute_framing` は
//    自分のビューから基底を作ってここへ委譲する。計算の本体を 1 つに保つことで、
//    「図鑑だけ直してモデルが直っていない」というずれが起きないようにする。
//
//  軸に縛られない幅の求め方:
//    軸平行な箱（サイズ sx, sy, sz）を単位ベクトル a の方向へ射影した幅は
//      |a.x|·sx + |a.y|·sy + |a.z|·sz
//    になる（箱の 8 頂点のうち a と最も同じ向きの頂点が端になるため）。
//    a が座標軸そのもののときは対応する辺の長さに一致するので、
//    元の軸決め打ちの式と**ビット単位で同じ値**を返す（0 倍と 0 加算しか増えない）。
// ============================================================

use crate::engine::core::renderer::actor_thumbnail::{
    center_square_crop, Framing, EYE_PULLBACK_FACTOR, EYE_PULLBACK_MIN, FAR_SLACK, ORTHO_HALF_H_MIN,
    ORTHO_NEAR,
};

// ─── 定数（マジックナンバー禁止）─────────────────────────────

/// モデルサムネイルを撮る方位角（度）。0 = 真正面（+Z 側）、正で右手側へ回る。
///
/// 真正面だと箱や板が単なる矩形に見えて形が伝わらないので、少しだけ回して
/// 側面も見えるようにする。35° は「正面がまだ主役のまま、奥行きが分かる」境目。
const MODEL_VIEW_AZIMUTH_DEG: f32 = 35.0;

/// モデルサムネイルを撮る仰角（度）。正で上から見下ろす。
///
/// 上面が見えると平たいもの（皿・地面タイル）と立体の区別がつく。
/// 深すぎると立ち姿のもの（木・人）が潰れるので浅めに取る。
const MODEL_VIEW_ELEVATION_DEG: f32 = 25.0;

/// ワールドの上方向。画面の上をここへ合わせる（首をかしげた絵にしないため）。
const WORLD_UP: [f32; 3] = [0.0, 1.0, 0.0];

/// 正規化でゼロ除算に落ちないための下限長さ。
const NORMALIZE_EPSILON: f32 = 1e-6;

// ============================================================
//  ViewBasis — 「どちらを向いて、どちらが画面の右か」
// ============================================================

/// 撮影カメラの向きを表す正規直交基底＋オイラー角。
///
/// SEED のデバッグカメラは yaw / pitch で姿勢を持つため、
/// ベクトルだけでなく対応する角度も一緒に運ぶ。
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct ViewBasis {
    /// 視線方向（カメラ → 被写体の単位ベクトル）。
    pub forward: [f32; 3],
    /// 画面上方向（ワールド）。
    pub up: [f32; 3],
    /// 画面右方向（ワールド）。左手座標系なので `cross(up, forward)`。
    pub right: [f32; 3],
    /// カメラの yaw（Y 軸回転・ラジアン）。
    pub yaw: f32,
    /// カメラの pitch（X 軸回転・ラジアン。正 = 下向き）。
    pub pitch: f32,
}

impl ViewBasis {
    /// 各要素を直接指定して作る。
    ///
    /// 既存の [`crate::engine::core::renderer::actor_thumbnail::ThumbnailView`] は
    /// 厳密な定数（0 / ±1 / ±π/2）で自分の基底と角度を持っているため、
    /// 計算で作り直さずそのまま渡してもらう。こうすれば図鑑サムネイルの構図は
    /// 1 ビットも変わらない。
    pub fn from_parts(
        forward: [f32; 3],
        up: [f32; 3],
        right: [f32; 3],
        yaw: f32,
        pitch: f32,
    ) -> Self {
        Self { forward, up, right, yaw, pitch }
    }

    /// 視線方向だけから基底を組み立てる（画面の上はワールド上方向に合わせる）。
    ///
    /// 真上・真下を向いた場合は `cross(WORLD_UP, forward)` が退化するので、
    /// そのときだけ +Z を仮の上として使う（[`ThumbnailView::Top`] と同じ扱い）。
    pub fn from_forward(forward: [f32; 3]) -> Self {
        let forward = normalize(forward, [0.0, 0.0, 1.0]);

        // 左手座標系: right = cross(up, forward)
        let right = normalize(cross(WORLD_UP, forward), [1.0, 0.0, 0.0]);
        // 視線と直交する「本当の画面上方向」を取り直す（forward が斜めでも傾かない）
        let up = normalize(cross(forward, right), WORLD_UP);

        // SEED の左手座標系では
        //   forward = (sin(yaw)·cos(pitch), −sin(pitch), cos(yaw)·cos(pitch))
        // なので、逆に解くと下記になる。
        let pitch = (-forward[1]).clamp(-1.0, 1.0).asin();
        let yaw = forward[0].atan2(forward[2]);

        Self { forward, up, right, yaw, pitch }
    }

    /// モデルサムネイル用の既定視点（斜め前上から）。
    ///
    /// カメラを方位角 [`MODEL_VIEW_AZIMUTH_DEG`]・仰角 [`MODEL_VIEW_ELEVATION_DEG`] の
    /// 位置に置き、被写体の中心を向かせる。
    pub fn model_default() -> Self {
        let azimuth = MODEL_VIEW_AZIMUTH_DEG.to_radians();
        let elevation = MODEL_VIEW_ELEVATION_DEG.to_radians();

        // 被写体 → カメラの方向（方位角 0 は +Z 側＝ThumbnailView::Front と同じ向き）
        let to_eye = [
            azimuth.sin() * elevation.cos(),
            elevation.sin(),
            azimuth.cos() * elevation.cos(),
        ];
        // 視線はその逆向き
        Self::from_forward([-to_eye[0], -to_eye[1], -to_eye[2]])
    }
}

// ============================================================
//  構図計算
// ============================================================

/// AABB とビューポートから、被写体が中央の正方形にちょうど収まる構図を求める。
///
/// # 正射半高の決め方
/// 正射投影では「1 ピクセルあたりのワールド長さ」が縦横で等しく、
/// `2·half_h / viewport_h` になる。中央の正方形（一辺 `side` px）が覆うワールド長さは
/// `side · 2·half_h / viewport_h` なので、その半分（＝正方形の半径）は
/// `side · half_h / viewport_h`。
/// これが被写体の必要半径 `needed` 以上であればよいので、
/// `half_h ≥ needed · viewport_h / side` を満たす最小値を採る。
///
/// # 引数
/// * `aabb_min` / `aabb_max` — 被写体のワールド AABB。
/// * `basis` — どの向きから撮るか。
/// * `viewport_w` / `viewport_h` — 実際に描画するフレームバッファの大きさ（ピクセル）。
/// * `margin_ratio` — 余白の比率（1.0 = AABB ちょうど）。
pub fn compute_framing_for_basis(
    aabb_min: [f32; 3],
    aabb_max: [f32; 3],
    basis: ViewBasis,
    viewport_w: u32,
    viewport_h: u32,
    margin_ratio: f32,
) -> Framing {
    // AABB の中心とサイズ（負のサイズにならないよう max(0) で守る）
    let center = [
        (aabb_min[0] + aabb_max[0]) * 0.5,
        (aabb_min[1] + aabb_max[1]) * 0.5,
        (aabb_min[2] + aabb_max[2]) * 0.5,
    ];
    let size = [
        (aabb_max[0] - aabb_min[0]).max(0.0),
        (aabb_max[1] - aabb_min[1]).max(0.0),
        (aabb_max[2] - aabb_min[2]).max(0.0),
    ];

    // 画面内に収めるべき半径（縦横のうち大きいほう）に余白を掛ける
    let screen_width = extent_along(basis.right, size);
    let screen_height = extent_along(basis.up, size);
    let needed_half = 0.5 * screen_width.max(screen_height) * margin_ratio;

    // 中央正方形へ収まるよう、ビューポート高さ／正方形の一辺の比で割り増しする
    let crop = center_square_crop(viewport_w.max(1), viewport_h.max(1));
    let side = crop.side.max(1) as f32;
    let ortho_half_h = (needed_half * (viewport_h.max(1) as f32) / side).max(ORTHO_HALF_H_MIN);

    // 視点は AABB の外側へ引く。奥行きの半分 × 係数（最低 EYE_PULLBACK_MIN）。
    let half_depth = 0.5 * extent_along(basis.forward, size);
    let pullback = (half_depth * EYE_PULLBACK_FACTOR).max(EYE_PULLBACK_MIN);
    let eye = [
        center[0] - basis.forward[0] * pullback,
        center[1] - basis.forward[1] * pullback,
        center[2] - basis.forward[2] * pullback,
    ];

    // ファーは「視点から AABB の裏側まで」＋余裕
    let far = pullback + half_depth + FAR_SLACK;

    Framing {
        eye,
        target: center,
        up: basis.up,
        ortho_half_h,
        near: ORTHO_NEAR,
        far,
        yaw: basis.yaw,
        pitch: basis.pitch,
    }
}

/// 軸平行な箱（サイズ `size`）を単位ベクトル `axis` の方向へ射影した幅を返す。
///
/// 箱の 8 頂点のうち `axis` と最も同じ向き／逆向きの頂点が両端になるので、
/// 幅は各辺の長さに軸成分の絶対値を掛けた和になる。
///
/// 公開しているのは、図鑑側（`actor_thumbnail`）のテストが
/// 「軸決め打ちだった旧式と同じ値になること」を突き合わせるため。
pub fn extent_along(axis: [f32; 3], size: [f32; 3]) -> f32 {
    axis[0].abs() * size[0] + axis[1].abs() * size[1] + axis[2].abs() * size[2]
}

/// 左手座標系の外積。
fn cross(a: [f32; 3], b: [f32; 3]) -> [f32; 3] {
    [
        a[1] * b[2] - a[2] * b[1],
        a[2] * b[0] - a[0] * b[2],
        a[0] * b[1] - a[1] * b[0],
    ]
}

/// ベクトルを正規化する。長さがほぼ 0 のときは `fallback` を返す
/// （真上を向いたときの right など、退化を安全に逃がすため）。
fn normalize(v: [f32; 3], fallback: [f32; 3]) -> [f32; 3] {
    let length = (v[0] * v[0] + v[1] * v[1] + v[2] * v[2]).sqrt();
    if length < NORMALIZE_EPSILON {
        return fallback;
    }
    [v[0] / length, v[1] / length, v[2] / length]
}

// ============================================================
//  単体テスト
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;

    /// 浮動小数の近似比較。
    fn close(a: f32, b: f32) -> bool {
        (a - b).abs() < 1e-4
    }

    /// 軸に沿った射影幅が、対応する辺の長さと厳密に一致すること。
    ///
    /// ここが崩れると図鑑サムネイル（軸決め打ちだった旧実装）の構図が変わる。
    #[test]
    fn extent_along_axis_equals_the_matching_edge() {
        let size = [2.0, 3.0, 5.0];
        assert_eq!(extent_along([1.0, 0.0, 0.0], size), 2.0);
        assert_eq!(extent_along([0.0, -1.0, 0.0], size), 3.0);
        assert_eq!(extent_along([0.0, 0.0, 1.0], size), 5.0);
    }

    /// 斜め方向では、関わる辺の寄与が足し合わされること。
    #[test]
    fn extent_along_diagonal_sums_contributions() {
        let size = [2.0, 0.0, 2.0];
        let diagonal = normalize([1.0, 0.0, 1.0], [1.0, 0.0, 0.0]);
        // 正方形を対角方向へ射影すると 2√2 ≈ 2.828 の幅になる
        assert!(close(extent_along(diagonal, size), 2.0 * std::f32::consts::SQRT_2));
    }

    /// 基底が正規直交（互いに直交・長さ 1）であること。
    #[test]
    fn model_default_basis_is_orthonormal() {
        let basis = ViewBasis::model_default();
        for v in [basis.forward, basis.up, basis.right] {
            let length = (v[0] * v[0] + v[1] * v[1] + v[2] * v[2]).sqrt();
            assert!(close(length, 1.0), "長さが 1 でない: {v:?}");
        }
        assert!(close(dot(basis.forward, basis.up), 0.0), "forward と up が直交していない");
        assert!(close(dot(basis.forward, basis.right), 0.0), "forward と right が直交していない");
        assert!(close(dot(basis.up, basis.right), 0.0), "up と right が直交していない");
    }

    /// モデル用の既定視点が「斜め前・上から見下ろす」向きであること。
    #[test]
    fn model_default_looks_down_from_the_front_side() {
        let basis = ViewBasis::model_default();
        // 見下ろす = 視線の Y 成分が負
        assert!(basis.forward[1] < 0.0, "見下ろしていない: {:?}", basis.forward);
        // 正面寄り = 視線の Z 成分が負（カメラが +Z 側にいる）
        assert!(basis.forward[2] < 0.0, "正面側から見ていない: {:?}", basis.forward);
        // 真正面ではなく振ってある = X 成分が 0 でない
        assert!(basis.forward[0].abs() > 0.1, "斜めに振れていない: {:?}", basis.forward);
        // 画面の上がワールドの上を向いている（絵が逆さ／横倒しにならない）
        assert!(basis.up[1] > 0.0, "画面上がワールド上を向いていない: {:?}", basis.up);
        // pitch は見下ろし（正）で、指定した仰角に一致する
        assert!(close(basis.pitch, MODEL_VIEW_ELEVATION_DEG.to_radians()));
    }

    /// forward から復元した yaw / pitch が、その forward を再現すること。
    ///
    /// カメラへ入れるのは yaw / pitch なので、ここがずれると
    /// 「構図は正しいのにカメラがよそを向く」という食い違いになる。
    #[test]
    fn yaw_pitch_round_trip_reproduces_forward() {
        for forward in [
            [-1.0, 0.0, 0.0],
            [0.0, 0.0, -1.0],
            [0.0, -1.0, 0.0],
            [-0.5198, -0.4226, -0.7424],
        ] {
            let basis = ViewBasis::from_forward(forward);
            // SEED の左手座標系での姿勢 → 前方ベクトルの定義式
            let rebuilt = [
                basis.yaw.sin() * basis.pitch.cos(),
                -basis.pitch.sin(),
                basis.yaw.cos() * basis.pitch.cos(),
            ];
            for axis in 0..3 {
                assert!(
                    close(rebuilt[axis], basis.forward[axis]),
                    "yaw/pitch から forward を再現できない: {:?} → {rebuilt:?}",
                    basis.forward
                );
            }
        }
    }

    /// 被写体が大きくなれば、それに比例して正射半高も大きくなること。
    #[test]
    fn framing_scales_with_the_subject() {
        let basis = ViewBasis::model_default();
        let small = compute_framing_for_basis([-1.0; 3], [1.0; 3], basis, 100, 100, 1.0);
        let large = compute_framing_for_basis([-2.0; 3], [2.0; 3], basis, 100, 100, 1.0);
        assert!(close(large.ortho_half_h, small.ortho_half_h * 2.0));
    }

    /// 視点が AABB の外側にあり、near より必ず遠いこと（near クリップでの欠けを防ぐ）。
    #[test]
    fn eye_sits_outside_the_subject() {
        let basis = ViewBasis::model_default();
        let framing = compute_framing_for_basis([-1.0; 3], [1.0; 3], basis, 64, 64, 1.1);
        let distance = ((framing.eye[0] - framing.target[0]).powi(2)
            + (framing.eye[1] - framing.target[1]).powi(2)
            + (framing.eye[2] - framing.target[2]).powi(2))
        .sqrt();
        // 被写体の外接球半径 √3 より遠い
        assert!(distance > 3.0f32.sqrt(), "視点が被写体の中に入っている: {distance}");
        assert!(framing.near < distance, "視点が near より手前にある");
        assert!(framing.far > distance, "far が視点より手前にある");
    }

    /// 退化した AABB（点）でもゼロ除算・ゼロ半高にならないこと。
    #[test]
    fn degenerate_aabb_stays_renderable() {
        let basis = ViewBasis::model_default();
        let framing = compute_framing_for_basis([0.0; 3], [0.0; 3], basis, 64, 64, 1.1);
        assert!(framing.ortho_half_h >= ORTHO_HALF_H_MIN);
        assert!(framing.far > framing.near);
        assert!(framing.ortho_half_h.is_finite());
        assert!(framing.eye.iter().all(|v| v.is_finite()));
    }

    /// 真下を向いた視線でも基底が退化しないこと（`cross` がゼロになる特異点）。
    #[test]
    fn straight_down_view_does_not_degenerate() {
        let basis = ViewBasis::from_forward([0.0, -1.0, 0.0]);
        for v in [basis.up, basis.right] {
            let length = (v[0] * v[0] + v[1] * v[1] + v[2] * v[2]).sqrt();
            assert!(close(length, 1.0), "退化した基底: {v:?}");
        }
        assert!(basis.pitch.is_finite() && basis.yaw.is_finite());
    }

    /// 内積（テスト用ヘルパ）。
    fn dot(a: [f32; 3], b: [f32; 3]) -> f32 {
        a[0] * b[0] + a[1] * b[1] + a[2] * b[2]
    }
}
