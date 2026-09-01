// ============================================================
//  modal_transform_state.rs — Blender 風モーダルトランスフォームの状態と純関数
//
//  【責務】
//  - モーダル種別（移動 / 回転 / 拡縮）と軸拘束の状態機械
//  - マウス入力からトランスフォームのデルタ量を求める純粋な数学関数
//  - 累積量からデルタ行列（ピボット基準）を組み立てる
//
//  【なぜ App から分離するか】
//  ここに置く処理は「ウィンドウ・シーン・GPU に一切触れない」ため、
//  単体テストで数値的に検証できる。App 側（modal_transform.rs）は
//  入力イベントの受け取りとシーンへの書き戻しだけを担当する。
// ============================================================

/// モーダルトランスフォームの種別（Blender の G / R / S に対応）。
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ModalKind {
    /// G: 移動
    Move,
    /// R: 回転
    Rotate,
    /// S: 拡縮
    Scale,
}

/// 軸拘束の対象軸。
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ModalAxis {
    X,
    Y,
    Z,
}

impl ModalAxis {
    /// ワールド軸の単位ベクトルを返す。
    pub fn world_dir(self) -> [f32; 3] {
        match self {
            ModalAxis::X => [1.0, 0.0, 0.0],
            ModalAxis::Y => [0.0, 1.0, 0.0],
            ModalAxis::Z => [0.0, 0.0, 1.0],
        }
    }

    /// 軸表示用の色（X=赤 / Y=緑 / Z=青、RGBA）。
    pub fn line_color(self) -> [f32; 4] {
        match self {
            ModalAxis::X => [1.0, 0.25, 0.25, 1.0],
            ModalAxis::Y => [0.25, 1.0, 0.25, 1.0],
            ModalAxis::Z => [0.35, 0.55, 1.0, 1.0],
        }
    }

    /// `local_axes` 配列（[X, Y, Z] の順）のインデックスを返す。
    pub fn index(self) -> usize {
        match self {
            ModalAxis::X => 0,
            ModalAxis::Y => 1,
            ModalAxis::Z => 2,
        }
    }
}

/// 軸拘束の座標系。
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ModalAxisSpace {
    /// ワールド軸（X/Y/Z を 1 度押した状態）。
    World,
    /// 選択アクタのローカル軸（同じキーを 2 度押した状態）。
    Local,
}

/// 現在の軸拘束（None = 拘束なし）。
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct ModalConstraint {
    pub axis: ModalAxis,
    pub space: ModalAxisSpace,
}

/// 軸キー（X/Y/Z）を押したときの拘束状態遷移。
///
/// Blender と同じく **同じ軸キーを押すたびに ワールド → ローカル → 解除** と巡回する。
/// 別の軸キーを押した場合は、その軸のワールド拘束へ切り替える。
pub fn cycle_constraint(
    current: Option<ModalConstraint>,
    pressed: ModalAxis,
) -> Option<ModalConstraint> {
    match current {
        // 同じ軸のワールド拘束中 → ローカル拘束へ
        Some(ModalConstraint { axis, space: ModalAxisSpace::World }) if axis == pressed => {
            Some(ModalConstraint { axis: pressed, space: ModalAxisSpace::Local })
        }
        // 同じ軸のローカル拘束中 → 拘束解除
        Some(ModalConstraint { axis, space: ModalAxisSpace::Local }) if axis == pressed => None,
        // 拘束なし、または別軸 → 押した軸のワールド拘束
        _ => Some(ModalConstraint { axis: pressed, space: ModalAxisSpace::World }),
    }
}

/// Shift 押下中の感度倍率（微調整）。
pub const FINE_SENSITIVITY: f32 = 0.1;

/// スケール倍率の下限（0 や負のスケールで行列が潰れるのを防ぐ）。
pub const MIN_SCALE_FACTOR: f32 = 1.0e-3;

/// 単位行列（row-major、平行移動は m[i][3]）。
pub const IDENTITY: [[f32; 4]; 4] = [
    [1.0, 0.0, 0.0, 0.0],
    [0.0, 1.0, 0.0, 0.0],
    [0.0, 0.0, 1.0, 0.0],
    [0.0, 0.0, 0.0, 1.0],
];

// ============================================================
//  ベクトル小物
// ============================================================

/// 内積。
pub fn dot3(a: [f32; 3], b: [f32; 3]) -> f32 {
    a[0] * b[0] + a[1] * b[1] + a[2] * b[2]
}

/// 正規化（長さ 0 の場合は元のベクトルをそのまま返す）。
pub fn normalize3(v: [f32; 3]) -> [f32; 3] {
    let len = dot3(v, v).sqrt();
    if len < 1.0e-8 {
        v
    } else {
        [v[0] / len, v[1] / len, v[2] / len]
    }
}

// ============================================================
//  マウス入力 → スカラー量（純関数）
// ============================================================

/// レイと平面の交点を求める。
///
/// 拘束なしの移動（G）で使う。平面はピボットを通り、カメラ前方向を法線とする
/// 「カメラに正対する平面」であり、ピボットの深度でマウス移動量をワールド量に変換する。
///
/// レイが平面と平行な場合は `None`。
pub fn ray_plane_intersect(
    ray_o: [f32; 3],
    ray_d: [f32; 3],
    plane_p: [f32; 3],
    plane_n: [f32; 3],
) -> Option<[f32; 3]> {
    let denom = dot3(ray_d, plane_n);
    if denom.abs() < 1.0e-8 {
        return None;
    }
    let diff = [
        plane_p[0] - ray_o[0],
        plane_p[1] - ray_o[1],
        plane_p[2] - ray_o[2],
    ];
    let t = dot3(diff, plane_n) / denom;
    Some([
        ray_o[0] + ray_d[0] * t,
        ray_o[1] + ray_d[1] * t,
        ray_o[2] + ray_d[2] * t,
    ])
}

/// マウスレイと拘束軸直線の最近接点を求め、**軸直線上のパラメータ t** を返す。
///
/// 軸拘束ありの移動（G + X/Y/Z）で使う。返り値 t は
/// `line_p + line_d * t` が最近接点になるスカラーで、`line_d` が単位ベクトルなら
/// ピボットからのワールド距離そのものになる。
///
/// レイと軸が平行な場合は `None`（スライド量を決められない）。
pub fn ray_line_closest_t(
    ray_o: [f32; 3],
    ray_d: [f32; 3],
    line_p: [f32; 3],
    line_d: [f32; 3],
) -> Option<f32> {
    // 2 直線の最近接点の標準解法。
    //   L1: ray_o  + s * ray_d
    //   L2: line_p + t * line_d
    let w0 = [
        ray_o[0] - line_p[0],
        ray_o[1] - line_p[1],
        ray_o[2] - line_p[2],
    ];
    let a = dot3(ray_d, ray_d);
    let b = dot3(ray_d, line_d);
    let c = dot3(line_d, line_d);
    let d = dot3(ray_d, w0);
    let e = dot3(line_d, w0);
    let denom = a * c - b * b;
    if denom.abs() < 1.0e-8 {
        // レイと軸が平行 → 最近接点が一意に決まらない
        return None;
    }
    Some((a * e - b * d) / denom)
}

/// 角度差を (-π, π] に折り返す。
///
/// 回転（R）でマウスがピボットの周りを一周した際、atan2 の
/// ±π 境界をまたいでも角度差が跳ねないようにするために使う。
pub fn wrap_angle(a: f32) -> f32 {
    let two_pi = std::f32::consts::TAU;
    let mut x = a % two_pi;
    if x > std::f32::consts::PI {
        x -= two_pi;
    } else if x <= -std::f32::consts::PI {
        x += two_pi;
    }
    x
}

/// ピボットのスクリーン座標を中心としたマウスの角度（ラジアン、スクリーン Y-down 系）。
pub fn screen_angle(pivot_screen: (f32, f32), mouse: (f32, f32)) -> f32 {
    (mouse.1 - pivot_screen.1).atan2(mouse.0 - pivot_screen.0)
}

/// ピボットのスクリーン座標からのマウス距離。
pub fn screen_distance(pivot_screen: (f32, f32), mouse: (f32, f32)) -> f32 {
    let dx = mouse.0 - pivot_screen.0;
    let dy = mouse.1 - pivot_screen.1;
    (dx * dx + dy * dy).sqrt()
}

/// 開始時距離を 1.0 とする拡縮倍率。
///
/// 距離が 0 に潰れた場合は `MIN_SCALE_FACTOR` で下限を張る。
pub fn scale_factor(start_dist: f32, cur_dist: f32) -> f32 {
    if start_dist <= 1.0e-4 {
        return 1.0;
    }
    (cur_dist / start_dist).max(MIN_SCALE_FACTOR)
}

// ============================================================
//  デルタ行列の生成（純関数）
// ============================================================

/// 線形部 `l`（3x3、row-major）をピボット基準の 4x4 デルタ行列にする。
///
/// D = T(pivot) * L * T(-pivot) なので、平行移動成分は `pivot - L * pivot`。
fn linear_about_pivot(l: [[f32; 3]; 3], pivot: [f32; 3]) -> [[f32; 4]; 4] {
    let mut m = IDENTITY;
    for i in 0..3 {
        for j in 0..3 {
            m[i][j] = l[i][j];
        }
        m[i][3] = pivot[i] - (l[i][0] * pivot[0] + l[i][1] * pivot[1] + l[i][2] * pivot[2]);
    }
    m
}

/// 平行移動のデルタ行列。
pub fn translation_matrix(t: [f32; 3]) -> [[f32; 4]; 4] {
    let mut m = IDENTITY;
    m[0][3] = t[0];
    m[1][3] = t[1];
    m[2][3] = t[2];
    m
}

/// 任意軸まわりの回転デルタ行列（ロドリゲスの回転公式、ピボット基準）。
///
/// 軸 `n` の先端から原点を見て**反時計回り**が正（右手系の回転）。
/// 画面上での回転方向との対応は `App::modal_rotation_sign` 側で決める。
pub fn rotation_matrix_about(pivot: [f32; 3], axis: [f32; 3], angle: f32) -> [[f32; 4]; 4] {
    let n = normalize3(axis);
    let (s, c) = angle.sin_cos();
    let ic = 1.0 - c;
    let l = [
        [
            c + n[0] * n[0] * ic,
            n[0] * n[1] * ic - n[2] * s,
            n[0] * n[2] * ic + n[1] * s,
        ],
        [
            n[1] * n[0] * ic + n[2] * s,
            c + n[1] * n[1] * ic,
            n[1] * n[2] * ic - n[0] * s,
        ],
        [
            n[2] * n[0] * ic - n[1] * s,
            n[2] * n[1] * ic + n[0] * s,
            c + n[2] * n[2] * ic,
        ],
    ];
    linear_about_pivot(l, pivot)
}

/// 均一拡縮のデルタ行列（ピボット基準）。
pub fn scale_matrix_uniform(pivot: [f32; 3], factor: f32) -> [[f32; 4]; 4] {
    let f = factor.max(MIN_SCALE_FACTOR);
    let l = [[f, 0.0, 0.0], [0.0, f, 0.0], [0.0, 0.0, f]];
    linear_about_pivot(l, pivot)
}

/// 単一軸方向のみの拡縮デルタ行列（ピボット基準）。
///
/// L = I + (f - 1) * n nᵀ。`n` が任意方向の単位ベクトルでよいため、
/// ワールド軸拘束にもローカル軸拘束にも同じ式が使える。
pub fn scale_matrix_axis(pivot: [f32; 3], axis: [f32; 3], factor: f32) -> [[f32; 4]; 4] {
    let n = normalize3(axis);
    let f = factor.max(MIN_SCALE_FACTOR) - 1.0;
    let mut l = [[0.0f32; 3]; 3];
    for i in 0..3 {
        for j in 0..3 {
            l[i][j] = if i == j { 1.0 } else { 0.0 } + f * n[i] * n[j];
        }
    }
    linear_about_pivot(l, pivot)
}

// ============================================================
//  ModalTransform — モーダル 1 回分の状態
// ============================================================

/// モーダルトランスフォーム 1 回分の状態。
///
/// 【累積方式である理由】
/// マウス位置から「開始時からの絶対量」を毎回引き直すのではなく、
/// 前回イベントからの差分を累積する。こうすると Shift（微調整）の
/// 感度をイベント単位で掛けられ、モーダル中に軸拘束を切り替えても
/// 破綻しない（軸切替時は累積をリセットして元の姿勢から取り直す）。
pub struct ModalTransform {
    /// モーダル種別。
    pub kind: ModalKind,
    /// 現在の軸拘束（None = 拘束なし）。
    pub constraint: Option<ModalConstraint>,
    /// 変換の基準点（全選択アクタの重心）。
    pub pivot: [f32; 3],
    /// ピボットのスクリーン座標（回転角・拡縮距離の中心）。
    pub pivot_screen: (f32, f32),
    /// モーダル開始時のカメラ前方向（拘束なし時の移動平面法線 / 回転軸）。
    pub view_forward: [f32; 3],
    /// 選択アクタのローカル軸（[X, Y, Z]）。取得できない場合はワールド軸。
    pub local_axes: [[f32; 3]; 3],
    /// ギズモドラッグ機構へ渡す開始行列（ピボットを平行移動成分に持つ単位行列）。
    pub start_mat: [[f32; 4]; 4],

    // ── 累積量 ────────────────────────────────────────────────
    /// 累積平行移動量（ワールド）。
    pub accum_translation: [f32; 3],
    /// 累積回転角（ラジアン）。
    pub accum_angle: f32,
    /// 累積拡縮倍率。
    pub accum_scale: f32,

    // ── 前回イベントの参照値（差分計算用）────────────────────
    /// 前回のカメラ平面上の交点（拘束なし移動用）。
    pub prev_plane_point: Option<[f32; 3]>,
    /// 前回の軸上パラメータ（軸拘束移動用）。
    pub prev_axis_t: Option<f32>,
    /// 前回のスクリーン角度（回転用）。
    pub prev_angle: Option<f32>,
    /// 前回のピボットからのスクリーン距離（拡縮用）。
    pub prev_dist: Option<f32>,

    // ── カーソル座標の供給源 ──────────────────────────────────
    /// エディタから外部カーソル座標（`MODAL:CURSOR:x,y`）を受け取ったか。
    ///
    /// 【なぜ必要か】
    /// OS はマウスイベントをカーソル直下のウィンドウにしか配送しないため、
    /// カーソルがランタイム子ウィンドウの外（エディタの他パネル上など）へ
    /// 出ると、ランタイムには `CursorMoved` が一切届かず更新が止まる。
    /// そこでエディタ側がモーダル中だけグローバルなカーソル位置を追跡し、
    /// IPC で送ってくる。一度でも外部座標が来たら**そちらを正**とし、
    /// 自前の `CursorMoved` は無視する（同じ移動を二重に積まないため）。
    pub external_cursor: bool,
}

impl ModalTransform {
    /// 新しいモーダル状態を作る（累積量はゼロ＝開始時の姿勢）。
    pub fn new(
        kind: ModalKind,
        pivot: [f32; 3],
        pivot_screen: (f32, f32),
        view_forward: [f32; 3],
        local_axes: [[f32; 3]; 3],
    ) -> Self {
        let mut start_mat = IDENTITY;
        start_mat[0][3] = pivot[0];
        start_mat[1][3] = pivot[1];
        start_mat[2][3] = pivot[2];
        Self {
            kind,
            constraint: None,
            pivot,
            pivot_screen,
            view_forward: normalize3(view_forward),
            local_axes,
            start_mat,
            accum_translation: [0.0; 3],
            accum_angle: 0.0,
            accum_scale: 1.0,
            prev_plane_point: None,
            prev_axis_t: None,
            prev_angle: None,
            prev_dist: None,
            external_cursor: false,
        }
    }

    /// 外部カーソル座標（エディタ由来）を受け取ったことを記録する。
    ///
    /// これ以降、このモーダルが終わるまでウィンドウ自前のカーソルイベントは
    /// 採用しない（`accepts_window_cursor()` が false を返す）。
    pub fn mark_external_cursor(&mut self) {
        self.external_cursor = true;
    }

    /// ウィンドウ自前の `CursorMoved` を採用してよいか。
    ///
    /// 外部座標源へ切り替わっていない間だけ true。
    pub fn accepts_window_cursor(&self) -> bool {
        !self.external_cursor
    }

    /// 累積量と前回参照値をリセットする（＝開始時の姿勢に戻す）。
    ///
    /// 軸拘束を切り替えたときに呼ぶ。Blender と同じく、拘束を変えると
    /// それまでの移動量は捨てられ、新しい拘束で取り直しになる。
    pub fn reset_accumulation(&mut self) {
        self.accum_translation = [0.0; 3];
        self.accum_angle = 0.0;
        self.accum_scale = 1.0;
        self.prev_plane_point = None;
        self.prev_axis_t = None;
        self.prev_angle = None;
        self.prev_dist = None;
    }

    /// 軸キー押下を適用する（拘束状態を巡回させ、累積をリセットする）。
    pub fn press_axis(&mut self, axis: ModalAxis) {
        self.constraint = cycle_constraint(self.constraint, axis);
        self.reset_accumulation();
    }

    /// 現在の拘束軸のワールド空間方向を返す（拘束なしなら None）。
    pub fn constraint_dir(&self) -> Option<[f32; 3]> {
        self.constraint.map(|c| match c.space {
            ModalAxisSpace::World => c.axis.world_dir(),
            ModalAxisSpace::Local => normalize3(self.local_axes[c.axis.index()]),
        })
    }

    /// 回転に使う軸を返す。
    ///
    /// 拘束なしのときは**ビュー方向（カメラ前方向）**を軸とする。
    pub fn rotation_axis(&self) -> [f32; 3] {
        self.constraint_dir().unwrap_or(self.view_forward)
    }

    /// 累積量から現在のデルタ行列（ピボット基準）を組み立てる。
    ///
    /// 累積がゼロなら単位行列を返す（＝取消時にスナップショットへ完全復元できる）。
    pub fn delta_matrix(&self) -> [[f32; 4]; 4] {
        match self.kind {
            ModalKind::Move => translation_matrix(self.accum_translation),
            ModalKind::Rotate => {
                rotation_matrix_about(self.pivot, self.rotation_axis(), self.accum_angle)
            }
            ModalKind::Scale => match self.constraint_dir() {
                // 軸拘束あり: その軸方向のみ拡縮
                Some(dir) => scale_matrix_axis(self.pivot, dir, self.accum_scale),
                // 拘束なし: 全軸均一拡縮
                None => scale_matrix_uniform(self.pivot, self.accum_scale),
            },
        }
    }

    /// 拘束なし移動: カメラ平面上の交点差分を累積する。
    pub fn accumulate_move_plane(&mut self, point: [f32; 3], sensitivity: f32) {
        if let Some(prev) = self.prev_plane_point {
            for i in 0..3 {
                self.accum_translation[i] += (point[i] - prev[i]) * sensitivity;
            }
        }
        self.prev_plane_point = Some(point);
    }

    /// 軸拘束移動: 軸上パラメータの差分を軸方向へ累積する。
    pub fn accumulate_move_axis(&mut self, t: f32, dir: [f32; 3], sensitivity: f32) {
        if let Some(prev) = self.prev_axis_t {
            let d = (t - prev) * sensitivity;
            for i in 0..3 {
                self.accum_translation[i] += dir[i] * d;
            }
        }
        self.prev_axis_t = Some(t);
    }

    /// 回転: スクリーン角度の差分（ラップ済み）を符号付きで累積する。
    ///
    /// `sign` は「軸が画面奥を向くか手前を向くか」で決まる回転方向の符号。
    pub fn accumulate_rotation(&mut self, angle: f32, sign: f32, sensitivity: f32) {
        if let Some(prev) = self.prev_angle {
            self.accum_angle += wrap_angle(angle - prev) * sign * sensitivity;
        }
        self.prev_angle = Some(angle);
    }

    /// 拡縮: スクリーン距離比を対数空間で累積する。
    ///
    /// 対数空間で足し込むことで、Shift による感度 1/10 が
    /// 「倍率の指数を 1/10 にする」という自然な意味になる。
    pub fn accumulate_scale(&mut self, dist: f32, sensitivity: f32) {
        if let Some(prev) = self.prev_dist {
            if prev > 1.0e-4 && dist > 1.0e-4 {
                let ratio = (dist / prev).powf(sensitivity);
                self.accum_scale = (self.accum_scale * ratio).max(MIN_SCALE_FACTOR);
            }
        }
        self.prev_dist = Some(dist);
    }
}

// ============================================================
//  テスト
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;

    /// 行列の各要素がほぼ等しいことを検証する。
    fn assert_mat_near(a: [[f32; 4]; 4], b: [[f32; 4]; 4], eps: f32) {
        for i in 0..4 {
            for j in 0..4 {
                assert!(
                    (a[i][j] - b[i][j]).abs() < eps,
                    "mat[{i}][{j}]: {} != {}",
                    a[i][j],
                    b[i][j]
                );
            }
        }
    }

    // ── 軸拘束の状態遷移 ─────────────────────────────────────

    #[test]
    fn axis_constraint_cycles_none_world_local_none() {
        let mut c = None;
        c = cycle_constraint(c, ModalAxis::X);
        assert_eq!(
            c,
            Some(ModalConstraint { axis: ModalAxis::X, space: ModalAxisSpace::World })
        );
        c = cycle_constraint(c, ModalAxis::X);
        assert_eq!(
            c,
            Some(ModalConstraint { axis: ModalAxis::X, space: ModalAxisSpace::Local })
        );
        c = cycle_constraint(c, ModalAxis::X);
        assert_eq!(c, None, "3 度目の同一キーで拘束解除");
        c = cycle_constraint(c, ModalAxis::X);
        assert_eq!(
            c,
            Some(ModalConstraint { axis: ModalAxis::X, space: ModalAxisSpace::World }),
            "解除後はまたワールドから始まる"
        );
    }

    #[test]
    fn other_axis_key_switches_to_world_constraint() {
        let local_x = Some(ModalConstraint { axis: ModalAxis::X, space: ModalAxisSpace::Local });
        assert_eq!(
            cycle_constraint(local_x, ModalAxis::Y),
            Some(ModalConstraint { axis: ModalAxis::Y, space: ModalAxisSpace::World })
        );
    }

    #[test]
    fn axis_key_press_resets_accumulation() {
        let mut m = ModalTransform::new(
            ModalKind::Move,
            [0.0, 0.0, 0.0],
            (100.0, 100.0),
            [0.0, 0.0, 1.0],
            [[1.0, 0.0, 0.0], [0.0, 1.0, 0.0], [0.0, 0.0, 1.0]],
        );
        m.accum_translation = [5.0, 5.0, 5.0];
        m.prev_plane_point = Some([1.0, 2.0, 3.0]);
        m.press_axis(ModalAxis::Z);
        assert_eq!(m.accum_translation, [0.0, 0.0, 0.0]);
        assert!(m.prev_plane_point.is_none());
    }

    // ── G: レイ-軸最近接点 / カメラ平面交点 ──────────────────

    #[test]
    fn ray_line_closest_t_basic() {
        // 軸: 原点を通る X 軸。レイ: (3, 5, 0) から -Y 方向。
        // 最近接点は X = 3 の点なので t = 3。
        let t = ray_line_closest_t(
            [3.0, 5.0, 0.0],
            [0.0, -1.0, 0.0],
            [0.0, 0.0, 0.0],
            [1.0, 0.0, 0.0],
        )
        .expect("レイと軸は平行ではない");
        assert!((t - 3.0).abs() < 1.0e-5, "t = {t}");
    }

    #[test]
    fn ray_line_closest_t_skew() {
        // 軸: X 軸。レイ: (2, 0, 4) から Z 方向（軸と交わらない）。
        // 軸上の最近接点は X = 2。
        let t = ray_line_closest_t(
            [2.0, 0.0, 4.0],
            [0.0, 0.0, 1.0],
            [0.0, 0.0, 0.0],
            [1.0, 0.0, 0.0],
        )
        .unwrap();
        assert!((t - 2.0).abs() < 1.0e-5, "t = {t}");
    }

    #[test]
    fn ray_line_closest_t_parallel_is_none() {
        assert!(
            ray_line_closest_t(
                [0.0, 1.0, 0.0],
                [1.0, 0.0, 0.0],
                [0.0, 0.0, 0.0],
                [1.0, 0.0, 0.0]
            )
            .is_none()
        );
    }

    #[test]
    fn unconstrained_move_uses_camera_plane() {
        // カメラ前方向 = +Z、ピボット深度 z = 10 の平面。
        let pivot = [0.0, 0.0, 10.0];
        let mut m = ModalTransform::new(
            ModalKind::Move,
            pivot,
            (100.0, 100.0),
            [0.0, 0.0, 1.0],
            [[1.0, 0.0, 0.0], [0.0, 1.0, 0.0], [0.0, 0.0, 1.0]],
        );
        assert!(m.constraint_dir().is_none(), "拘束なしで開始する");

        // カメラ原点から 2 本のレイを飛ばし、平面交点の差分を累積する
        let p0 = ray_plane_intersect([0.0, 0.0, 0.0], [0.0, 0.0, 1.0], pivot, [0.0, 0.0, 1.0])
            .unwrap();
        assert_eq!(p0, [0.0, 0.0, 10.0]);
        let p1 = ray_plane_intersect(
            [0.0, 0.0, 0.0],
            normalize3([0.1, 0.2, 1.0]),
            pivot,
            [0.0, 0.0, 1.0],
        )
        .unwrap();
        m.accumulate_move_plane(p0, 1.0);
        m.accumulate_move_plane(p1, 1.0);

        // 平面上の移動なので Z 成分は増えず、XY が動く
        assert!((m.accum_translation[2]).abs() < 1.0e-4);
        assert!((m.accum_translation[0] - 1.0).abs() < 1.0e-4);
        assert!((m.accum_translation[1] - 2.0).abs() < 1.0e-4);
        // デルタ行列は純粋な平行移動
        let d = m.delta_matrix();
        assert!((d[0][3] - 1.0).abs() < 1.0e-4);
        assert!((d[1][3] - 2.0).abs() < 1.0e-4);
        assert!((d[0][0] - 1.0).abs() < 1.0e-6, "回転成分は入らない");
    }

    #[test]
    fn axis_constrained_move_only_along_axis() {
        let mut m = ModalTransform::new(
            ModalKind::Move,
            [0.0, 0.0, 0.0],
            (0.0, 0.0),
            [0.0, 0.0, 1.0],
            [[1.0, 0.0, 0.0], [0.0, 1.0, 0.0], [0.0, 0.0, 1.0]],
        );
        m.press_axis(ModalAxis::X);
        let dir = m.constraint_dir().unwrap();
        m.accumulate_move_axis(0.0, dir, 1.0);
        m.accumulate_move_axis(4.0, dir, 1.0);
        assert_eq!(m.accum_translation, [4.0, 0.0, 0.0]);
    }

    #[test]
    fn shift_fine_sensitivity_scales_move() {
        let mut m = ModalTransform::new(
            ModalKind::Move,
            [0.0, 0.0, 0.0],
            (0.0, 0.0),
            [0.0, 0.0, 1.0],
            [[1.0, 0.0, 0.0], [0.0, 1.0, 0.0], [0.0, 0.0, 1.0]],
        );
        m.press_axis(ModalAxis::Y);
        let dir = m.constraint_dir().unwrap();
        m.accumulate_move_axis(0.0, dir, FINE_SENSITIVITY);
        m.accumulate_move_axis(10.0, dir, FINE_SENSITIVITY);
        assert!((m.accum_translation[1] - 1.0).abs() < 1.0e-5);
    }

    // ── R: 角度差分のラップ ─────────────────────────────────

    #[test]
    fn wrap_angle_folds_into_pi_range() {
        use std::f32::consts::PI;
        assert!((wrap_angle(0.5) - 0.5).abs() < 1.0e-6);
        // 179° → -179° の移動は +358° ではなく -2°
        let d = wrap_angle((-179.0f32).to_radians() - (179.0f32).to_radians());
        assert!((d - (2.0f32).to_radians()).abs() < 1.0e-5, "d = {d}");
        assert!(wrap_angle(3.0 * PI).abs() - PI < 1.0e-5);
    }

    #[test]
    fn rotation_accumulates_with_wrap() {
        let mut m = ModalTransform::new(
            ModalKind::Rotate,
            [0.0, 0.0, 0.0],
            (0.0, 0.0),
            [0.0, 0.0, 1.0],
            [[1.0, 0.0, 0.0], [0.0, 1.0, 0.0], [0.0, 0.0, 1.0]],
        );
        let a0 = (179.0f32).to_radians();
        let a1 = (-179.0f32).to_radians();
        m.accumulate_rotation(a0, 1.0, 1.0);
        m.accumulate_rotation(a1, 1.0, 1.0);
        assert!(
            (m.accum_angle - (2.0f32).to_radians()).abs() < 1.0e-5,
            "境界跨ぎで角度が跳ねない: {}",
            m.accum_angle
        );
    }

    #[test]
    fn unconstrained_rotation_axis_is_view_forward() {
        let view_forward = normalize3([0.3, -0.5, 1.0]);
        let m = ModalTransform::new(
            ModalKind::Rotate,
            [1.0, 2.0, 3.0],
            (0.0, 0.0),
            view_forward,
            [[1.0, 0.0, 0.0], [0.0, 1.0, 0.0], [0.0, 0.0, 1.0]],
        );
        assert!(m.constraint_dir().is_none());
        let axis = m.rotation_axis();
        for i in 0..3 {
            assert!((axis[i] - view_forward[i]).abs() < 1.0e-6);
        }
    }

    #[test]
    fn rotation_delta_keeps_pivot_fixed() {
        let pivot = [1.0, 2.0, 3.0];
        let d = rotation_matrix_about(pivot, [0.0, 1.0, 0.0], 0.7);
        let p = [
            d[0][0] * pivot[0] + d[0][1] * pivot[1] + d[0][2] * pivot[2] + d[0][3],
            d[1][0] * pivot[0] + d[1][1] * pivot[1] + d[1][2] * pivot[2] + d[1][3],
            d[2][0] * pivot[0] + d[2][1] * pivot[1] + d[2][2] * pivot[2] + d[2][3],
        ];
        for i in 0..3 {
            assert!((p[i] - pivot[i]).abs() < 1.0e-5);
        }
    }

    #[test]
    fn local_constraint_uses_local_axis() {
        // Y 軸まわりに 90° 回ったアクタのローカル X 軸は -Z 方向。
        let local = [[0.0, 0.0, -1.0], [0.0, 1.0, 0.0], [1.0, 0.0, 0.0]];
        let mut m = ModalTransform::new(
            ModalKind::Rotate,
            [0.0, 0.0, 0.0],
            (0.0, 0.0),
            [0.0, 0.0, 1.0],
            local,
        );
        m.press_axis(ModalAxis::X); // ワールド X
        assert_eq!(m.constraint_dir().unwrap(), [1.0, 0.0, 0.0]);
        m.press_axis(ModalAxis::X); // ローカル X
        let d = m.constraint_dir().unwrap();
        assert!((d[2] - (-1.0)).abs() < 1.0e-6, "ローカル X = -Z: {d:?}");
    }

    // ── S: 倍率 ─────────────────────────────────────────────

    #[test]
    fn scale_factor_is_ratio_to_start_distance() {
        assert!((scale_factor(100.0, 200.0) - 2.0).abs() < 1.0e-6);
        assert!((scale_factor(100.0, 50.0) - 0.5).abs() < 1.0e-6);
        assert!((scale_factor(100.0, 100.0) - 1.0).abs() < 1.0e-6);
        // 開始時距離が 0 に潰れている場合は 1.0（拡縮しない）
        assert!((scale_factor(0.0, 50.0) - 1.0).abs() < 1.0e-6);
    }

    #[test]
    fn unconstrained_scale_is_uniform() {
        let mut m = ModalTransform::new(
            ModalKind::Scale,
            [0.0, 0.0, 0.0],
            (100.0, 100.0),
            [0.0, 0.0, 1.0],
            [[1.0, 0.0, 0.0], [0.0, 1.0, 0.0], [0.0, 0.0, 1.0]],
        );
        assert!(m.constraint_dir().is_none(), "拘束なしで開始する");
        m.accumulate_scale(screen_distance((100.0, 100.0), (200.0, 100.0)), 1.0);
        m.accumulate_scale(screen_distance((100.0, 100.0), (300.0, 100.0)), 1.0);
        assert!((m.accum_scale - 2.0).abs() < 1.0e-5);
        let d = m.delta_matrix();
        assert_mat_near(
            d,
            [
                [2.0, 0.0, 0.0, 0.0],
                [0.0, 2.0, 0.0, 0.0],
                [0.0, 0.0, 2.0, 0.0],
                [0.0, 0.0, 0.0, 1.0],
            ],
            1.0e-5,
        );
    }

    #[test]
    fn axis_constrained_scale_only_that_axis() {
        let mut m = ModalTransform::new(
            ModalKind::Scale,
            [0.0, 0.0, 0.0],
            (100.0, 100.0),
            [0.0, 0.0, 1.0],
            [[1.0, 0.0, 0.0], [0.0, 1.0, 0.0], [0.0, 0.0, 1.0]],
        );
        m.press_axis(ModalAxis::Y);
        m.accum_scale = 3.0;
        let d = m.delta_matrix();
        assert_mat_near(
            d,
            [
                [1.0, 0.0, 0.0, 0.0],
                [0.0, 3.0, 0.0, 0.0],
                [0.0, 0.0, 1.0, 0.0],
                [0.0, 0.0, 0.0, 1.0],
            ],
            1.0e-5,
        );
    }

    #[test]
    fn scale_delta_is_pivot_relative() {
        let pivot = [5.0, 0.0, 0.0];
        let d = scale_matrix_uniform(pivot, 2.0);
        // ピボットは不動
        let px = d[0][0] * pivot[0] + d[0][3];
        assert!((px - 5.0).abs() < 1.0e-5);
        // ピボットから 1 離れた点は 2 離れる
        let qx = d[0][0] * 6.0 + d[0][3];
        assert!((qx - 7.0).abs() < 1.0e-5);
    }

    // ── 取消: 累積リセットでデルタが単位行列に戻る ──────────

    #[test]
    fn cancel_reset_makes_delta_identity() {
        for kind in [ModalKind::Move, ModalKind::Rotate, ModalKind::Scale] {
            let mut m = ModalTransform::new(
                kind,
                [1.0, 2.0, 3.0],
                (10.0, 20.0),
                [0.0, 0.0, 1.0],
                [[1.0, 0.0, 0.0], [0.0, 1.0, 0.0], [0.0, 0.0, 1.0]],
            );
            // 適当に動かしてから
            m.accum_translation = [3.0, -2.0, 1.0];
            m.accum_angle = 0.9;
            m.accum_scale = 4.0;
            assert!(
                m.delta_matrix() != IDENTITY,
                "{kind:?}: 動かした後は単位行列ではない"
            );
            // 取消（＝スナップショット復元）は累積ゼロのデルタ適用と等価
            m.reset_accumulation();
            assert_mat_near(m.delta_matrix(), IDENTITY, 1.0e-6);
        }
    }

    #[test]
    fn identity_delta_restores_start_matrix() {
        // 取消時にスナップショットへ完全復元されることの数値的な保証。
        let start = [
            [0.5, 0.1, 0.0, 7.0],
            [0.0, 2.0, 0.3, -1.0],
            [0.2, 0.0, 1.5, 4.0],
            [0.0, 0.0, 0.0, 1.0],
        ];
        let m = ModalTransform::new(
            ModalKind::Move,
            [1.0, 2.0, 3.0],
            (0.0, 0.0),
            [0.0, 0.0, 1.0],
            [[1.0, 0.0, 0.0], [0.0, 1.0, 0.0], [0.0, 0.0, 1.0]],
        );
        let restored =
            crate::engine::methods::gizmo_interact::mat4x4_mul(m.delta_matrix(), start);
        assert_mat_near(restored, start, 1.0e-6);
    }

    // ============================================================
    //  ウィンドウ外カーソル（負値・幅/高さ超え）での継続性
    //
    //  エディタがグローバル追跡したカーソルは、ビューポートのクライアント
    //  座標へ変換した結果が負値や幅超えになる。これらの座標でも
    //  G / R / S の計算が破綻せず滑らかに続くことを検証する。
    // ============================================================

    /// テスト用のビューポートサイズ。
    const TEST_VP_W: f32 = 800.0;
    const TEST_VP_H: f32 = 600.0;

    /// テスト用のモーダル状態（ピボットは原点、カメラは -Z から +Z を向く）。
    fn test_modal(kind: ModalKind) -> ModalTransform {
        ModalTransform::new(
            kind,
            [0.0, 0.0, 0.0],
            (TEST_VP_W * 0.5, TEST_VP_H * 0.5),
            [0.0, 0.0, 1.0],
            [[1.0, 0.0, 0.0], [0.0, 1.0, 0.0], [0.0, 0.0, 1.0]],
        )
    }

    #[test]
    fn rotate_continues_outside_viewport() {
        // ピボット中心に反時計回りで 4 点を回る。2 点目以降は
        // すべてビューポート外（負値 / 幅超え / 高さ超え）にある。
        let mut m = test_modal(ModalKind::Rotate);
        let cx = TEST_VP_W * 0.5;
        let cy = TEST_VP_H * 0.5;
        let r = 900.0_f32; // 画面をはみ出す半径
        let mut prev_accum = 0.0_f32;
        for step in 0..8 {
            let th = std::f32::consts::TAU * (step as f32) / 8.0;
            let px = cx + r * th.cos();
            let py = cy + r * th.sin();
            // 2 点目以降は必ずビューポート外であること（テスト前提の確認）
            if step > 0 {
                assert!(
                    px < 0.0 || py < 0.0 || px > TEST_VP_W || py > TEST_VP_H,
                    "step {step}: ({px}, {py}) はビューポート外であるべき"
                );
            }
            let angle = screen_angle(m.pivot_screen, (px, py));
            m.accumulate_rotation(angle, 1.0, 1.0);
            if step > 1 {
                // 1 ステップ = 45 度ずつ、跳ねずに単調増加すること
                let d = m.accum_angle - prev_accum;
                assert!(
                    (d - std::f32::consts::FRAC_PI_4).abs() < 1.0e-4,
                    "step {step}: 角度差 {d} が 45 度ではない"
                );
            }
            prev_accum = m.accum_angle;
        }
        // 7 ステップ分 = 315 度
        assert!((m.accum_angle - std::f32::consts::FRAC_PI_4 * 7.0).abs() < 1.0e-3);
    }

    #[test]
    fn scale_continues_outside_viewport() {
        // ピボットから遠ざかる（画面外へ出る）に従って倍率が単調増加すること。
        let mut m = test_modal(ModalKind::Scale);
        let cy = TEST_VP_H * 0.5;
        // 画面内 → 右端超え → さらに遠く
        let xs = [TEST_VP_W * 0.5 + 100.0, TEST_VP_W + 400.0, TEST_VP_W + 900.0];
        let mut prev = 1.0_f32;
        for (i, x) in xs.iter().enumerate() {
            let dist = screen_distance(m.pivot_screen, (*x, cy));
            m.accumulate_scale(dist, 1.0);
            if i > 0 {
                assert!(
                    m.accum_scale > prev,
                    "i={i}: 画面外でも倍率が増え続けるべき ({} <= {prev})",
                    m.accum_scale
                );
            }
            prev = m.accum_scale;
        }
        // 開始距離 100 に対し最終距離は 900+400=... 実測比で 10 倍超
        assert!(m.accum_scale > 5.0, "accum_scale={}", m.accum_scale);
    }

    #[test]
    fn scale_continues_with_negative_coords() {
        // 負値（ウィンドウ左上を越えた側）でも距離が正しく求まり、破綻しないこと。
        let mut m = test_modal(ModalKind::Scale);
        m.accumulate_scale(screen_distance(m.pivot_screen, (-100.0, -100.0)), 1.0);
        let d0 = m.prev_dist.unwrap();
        m.accumulate_scale(screen_distance(m.pivot_screen, (-500.0, -500.0)), 1.0);
        assert!(d0 > 0.0 && m.accum_scale > 1.0, "accum_scale={}", m.accum_scale);
        assert!(m.accum_scale.is_finite());
    }

    #[test]
    fn axis_move_continues_outside_viewport() {
        // 軸拘束移動（G→X）で、カーソルがビューポート右外へ出ても
        // 軸上パラメータが増え続けること。
        // レイはギズモと同じ screen_to_ray で作る（クランプが無いことの確認も兼ねる）。
        use crate::engine::methods::gizmo_interact::screen_to_ray;

        // カメラは (0, 0, -10) から +Z を向く（row-major の view / proj を直接組む）。
        let view = [
            [1.0, 0.0, 0.0, 0.0],
            [0.0, 1.0, 0.0, 0.0],
            [0.0, 0.0, 1.0, 10.0],
            [0.0, 0.0, 0.0, 1.0],
        ];
        // f = cot(fov/2) = 1.0（fov_y = 90 度）、aspect = 4/3
        let proj = [
            [1.0 / (TEST_VP_W / TEST_VP_H), 0.0, 0.0, 0.0],
            [0.0, 1.0, 0.0, 0.0],
            [0.0, 0.0, 1.0, 0.0],
            [0.0, 0.0, 1.0, 0.0],
        ];
        let cam_pos = [0.0, 0.0, -10.0];

        let mut m = test_modal(ModalKind::Move);
        m.press_axis(ModalAxis::X);
        let dir = m.constraint_dir().expect("X 軸拘束");

        let cy = TEST_VP_H * 0.5;
        // 画面中央 → 右端 → 右端の外 → さらに外
        let xs = [
            TEST_VP_W * 0.5,
            TEST_VP_W,
            TEST_VP_W + 500.0,
            TEST_VP_W + 1500.0,
        ];
        let mut prev_t = f32::NEG_INFINITY;
        for (i, x) in xs.iter().enumerate() {
            let (ro, rd) = screen_to_ray(*x, cy, TEST_VP_W, TEST_VP_H, &view, &proj, cam_pos);
            let t = ray_line_closest_t(ro, rd, m.pivot, dir).expect("軸と平行ではない");
            assert!(t.is_finite(), "i={i}: t が有限でない");
            assert!(t > prev_t, "i={i}: t={t} が単調増加していない (prev={prev_t})");
            prev_t = t;
            m.accumulate_move_axis(t, dir, 1.0);
        }
        // 累積は X 方向のみ、かつ画面外まで動かした分だけ大きい
        assert!(m.accum_translation[0] > 0.0);
        assert!(m.accum_translation[1].abs() < 1.0e-5);
        assert!(m.accum_translation[2].abs() < 1.0e-5);
    }

    // ============================================================
    //  外部カーソル座標源への切替
    // ============================================================

    #[test]
    fn external_cursor_source_switch() {
        let mut m = test_modal(ModalKind::Move);
        // 既定ではウィンドウ自前の CursorMoved を採用する
        assert!(m.accepts_window_cursor());
        assert!(!m.external_cursor);

        // エディタからの座標を一度受け取ったら、以降は自前を採用しない
        m.mark_external_cursor();
        assert!(!m.accepts_window_cursor());

        // 軸拘束の切り替え（累積リセット）でも座標源は元に戻らない
        // （戻ると子ウィンドウ上で二重適用が復活してしまう）
        m.press_axis(ModalAxis::Z);
        assert!(!m.accepts_window_cursor());
        m.reset_accumulation();
        assert!(!m.accepts_window_cursor());

        // 新しいモーダルでは自前の座標源から始まる
        let fresh = test_modal(ModalKind::Move);
        assert!(fresh.accepts_window_cursor());
    }
}
