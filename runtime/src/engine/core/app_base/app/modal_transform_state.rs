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
//  2D キャンバス空間とスクリーン座標の相互変換（純関数）
// ============================================================
//
//  2D 編集のギズモ・ピック・モーダルはすべて `screen_to_ray_ortho` が作る
//  「キャンバス px 空間」（+X = 画面右 / +Y = 画面下）で計算する。
//  モーダルの回転角・拡縮距離はピボットの**スクリーン座標**を中心に測るため、
//  逆向き（キャンバス px → スクリーン）の写像がここに要る。
//
//  両者は `half_w = half_h * (vp_w / vp_h)` という関係から
//  **X と Y で同じ正の倍率**の相似変換になる（反転も歪みもない）。
//  そのためスクリーン上で測った角度・距離比は、キャンバス px 空間で
//  測ったものと完全に一致する。

/// スクリーン座標（クライアント px）→ キャンバス px 空間。
///
/// `screen_to_ray_ortho` のレイ原点 XY と同一の式。
/// `pan` は 2D カメラのパン、`half` は `[half_w, half_h]`、`vp` はビューポートサイズ。
pub fn screen_to_canvas_px(
    screen: (f32, f32),
    pan: [f32; 2],
    half: [f32; 2],
    vp: [f32; 2],
) -> [f32; 2] {
    let ndc_x = 2.0 * screen.0 / vp[0] - 1.0;
    let ndc_y = 2.0 * screen.1 / vp[1] - 1.0;
    [pan[0] + ndc_x * half[0], pan[1] + ndc_y * half[1]]
}

/// キャンバス px 空間 → スクリーン座標（クライアント px）。`screen_to_canvas_px` の逆写像。
pub fn canvas_px_to_screen(
    canvas: [f32; 2],
    pan: [f32; 2],
    half: [f32; 2],
    vp: [f32; 2],
) -> (f32, f32) {
    // half が 0（縮退したカメラ）でも NaN を返さないよう 1.0 とみなす
    let hx = if half[0].abs() > 1.0e-6 { half[0] } else { 1.0 };
    let hy = if half[1].abs() > 1.0e-6 { half[1] } else { 1.0 };
    let ndc_x = (canvas[0] - pan[0]) / hx;
    let ndc_y = (canvas[1] - pan[1]) / hy;
    ((ndc_x + 1.0) * 0.5 * vp[0], (ndc_y + 1.0) * 0.5 * vp[1])
}

/// 2D モーダル回転の符号。
///
/// キャンバス px 空間は +Y = 画面下で、`rotation_matrix_about` に軸 +Z（画面奥）と
/// 角度 +θ を渡すと点 (1, 0) は (cosθ, sinθ) へ動く ＝ **画面上では時計回り**。
/// 一方 `screen_angle` も Y-down の atan2 なので、マウスを時計回りに回すと増加する。
/// したがって両者は既に一致しており、反転は不要（＝ +1）。
///
/// これは `CanvasTransform::to_mat4_sized`（col0 = [cos, sin]）の規約、
/// すなわち「正の `rotation` = 画面上で時計回り」とも一致する
/// （canvas_gizmo_basis.rs の座標系メモを参照）。
pub const ROTATION_SIGN_2D: f32 = 1.0;

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
//  数値入力（Blender の「G/R/S のあと数字を打つ」相当）
// ============================================================

/// 数値入力バッファに積める数字の最大個数（符号・小数点は数えない）。
///
/// f32 の有効桁数を大きく超える入力は意味を持たないうえ、
/// 押しっぱなしのキーリピートでバッファが際限なく伸びるのを防ぐ。
pub const NUMERIC_MAX_DIGITS: usize = 12;

/// 数値入力で受け付ける小数点文字。
pub const NUMERIC_DOT: char = '.';

/// 数値入力で受け付ける符号反転文字。
pub const NUMERIC_SIGN: char = '-';

/// モーダルトランスフォーム中の数値入力バッファ。
///
/// 【Blender の挙動に合わせた仕様】
/// - 数字 `0-9` を打つと末尾に追加される
/// - 小数点は 1 個まで。2 個目以降は無視する
/// - `-` はカーソル位置に関係なく **符号のトグル**（先頭でなくてもよい）
/// - Backspace は 1 文字削除。空になったらマウス駆動へ戻る
///
/// 【符号を本体文字列に含めない理由】
/// Blender の `-` は「文字の挿入」ではなく「符号の反転」なので、
/// 本体（数字と小数点の並び）とは独立したフラグとして持つ。
/// こうすると Backspace が符号を巻き込んで消すこともない。
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct ModalNumericInput {
    /// 入力済みの数字と小数点の並び（符号は含まない）。
    body: String,
    /// 符号が負か。
    negative: bool,
}

impl ModalNumericInput {
    /// 空の入力バッファを作る。
    pub fn new() -> Self {
        Self::default()
    }

    /// 数値入力モードが有効か（＝1 文字でも本体が入っているか）。
    ///
    /// `-` だけを押した状態は「まだ数値が無い」ため false。
    /// この判定が false の間はマウス駆動のままになる。
    pub fn is_active(&self) -> bool {
        !self.body.is_empty()
    }

    /// バッファを空に戻す（符号も解除する）。
    pub fn clear(&mut self) {
        self.body.clear();
        self.negative = false;
    }

    /// 1 文字を入力として適用する。バッファが変化したら `true`。
    ///
    /// 受け付けるのは数字 `0-9` / 小数点 `.` / 符号 `-` のみ。
    /// それ以外の文字、桁数上限超過、2 個目の小数点は無視して `false` を返す。
    pub fn apply_char(&mut self, c: char) -> bool {
        match c {
            NUMERIC_SIGN => {
                // Blender と同じく、どの位置で押しても符号のトグル
                self.negative = !self.negative;
                true
            }
            NUMERIC_DOT => {
                // 小数点は 1 個まで（2 個目は無視）
                if self.body.contains(NUMERIC_DOT) {
                    return false;
                }
                self.body.push(NUMERIC_DOT);
                true
            }
            d if d.is_ascii_digit() => {
                // 桁数上限（小数点は桁数に数えない）
                if self.body.chars().filter(|ch| ch.is_ascii_digit()).count() >= NUMERIC_MAX_DIGITS
                {
                    return false;
                }
                self.body.push(d);
                true
            }
            _ => false,
        }
    }

    /// 末尾の 1 文字を削除する。削除できたら `true`。
    ///
    /// 本体が空になったら符号も解除し、完全に初期状態へ戻す
    /// （次にマウス駆動へ復帰したあと、また数値入力を始めても
    ///   前回の符号を引きずらないようにするため）。
    pub fn backspace(&mut self) -> bool {
        if self.body.pop().is_none() {
            // 本体が空でも符号だけ立っている状態は解除できる
            if self.negative {
                self.negative = false;
                return true;
            }
            return false;
        }
        if self.body.is_empty() {
            self.negative = false;
        }
        true
    }

    /// 現在の入力値。
    ///
    /// `"."` のように数値として解釈できない中間状態や、
    /// 桁あふれで無限大になる入力は 0.0 として扱う
    /// （行列生成へ NaN / inf を絶対に流さないため）。
    pub fn value(&self) -> f32 {
        let v = self.body.parse::<f32>().unwrap_or(0.0);
        let v = if v.is_finite() { v } else { 0.0 };
        if self.negative { -v } else { v }
    }

    /// 表示用の文字列（`-` + 本体）。エディタ／オーバーレイ表示に使う。
    pub fn display(&self) -> String {
        if self.negative {
            format!("{NUMERIC_SIGN}{}", self.body)
        } else {
            self.body.clone()
        }
    }
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
    /// 2D キャンバス編集のモーダルか。
    ///
    /// true のとき、`pivot` / `view_forward` / `local_axes` はすべて
    /// **キャンバス px 空間**（+X = 画面右 / +Y = 画面下 / +Z = 画面奥）の値であり、
    /// 数学そのものは 3D とまったく同じものが使える（2D ortho カメラのレイが
    /// この空間の座標をそのまま返すため）。3D と挙動を変えるのは次の 3 点だけ:
    ///   - 軸拘束は X / Y のみ（Z はキャンバス法線なので意味を持たない）
    ///   - 回転は常にキャンバス法線まわり（R 中の軸拘束は受け付けない）
    ///   - 画面上の回り方と回転符号の対応（`App` 側で符号を決める）
    pub is_2d: bool,

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

    // ── 数値入力 ──────────────────────────────────────────────
    /// 数値入力バッファ（Blender の「G/R/S のあと数字を打つ」相当）。
    ///
    /// 1 文字でも入ると駆動源がマウスから数値へ切り替わる
    /// （`numeric_active()` が true の間、マウス移動は累積されない）。
    pub numeric: ModalNumericInput,
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
            is_2d: false,
            accum_translation: [0.0; 3],
            accum_angle: 0.0,
            accum_scale: 1.0,
            prev_plane_point: None,
            prev_axis_t: None,
            prev_angle: None,
            prev_dist: None,
            external_cursor: false,
            numeric: ModalNumericInput::new(),
        }
    }

    /// 2D キャンバス編集用のモーダルとしてマークする（ビルダー）。
    pub fn into_2d(mut self) -> Self {
        self.is_2d = true;
        self
    }

    /// この軸キーを受け付けるか。
    ///
    /// 3D は常に true。2D は次の 2 つを弾く:
    /// - `Z`: キャンバス法線方向。`CanvasTransform` は XY しか持たないため
    ///   移動も拡縮も表現できない。
    /// - 回転（R）中の全軸: 2D の回転軸はキャンバス法線に固定であり、
    ///   X / Y まわりの回転は `CanvasTransform.rotation`（Z 回転のみ）に
    ///   書き戻せない。
    pub fn axis_allowed(&self, axis: ModalAxis) -> bool {
        if !self.is_2d {
            return true;
        }
        axis != ModalAxis::Z && self.kind != ModalKind::Rotate
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
    ///
    /// 2D で意味を持たない軸（`axis_allowed` が false）は**完全に無視**する。
    /// 拘束状態も累積量も変えないため、押しても現在の変形が乱れない。
    pub fn press_axis(&mut self, axis: ModalAxis) {
        if !self.axis_allowed(axis) {
            return;
        }
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

    // ── 数値入力 ──────────────────────────────────────────────

    /// 数値入力を受け付けられる状態か。
    ///
    /// 【軸未指定の移動（G だけ）を除外する理由】
    /// Blender は軸未指定の G でも数値を打てるが、その場合は X/Y/Z の
    /// 3 フィールドを Tab で渡り歩く別 UI になる。本エディタにはその
    /// 入力欄表示が無く、単一の数値をどの方向へ適用するかを利用者が
    /// 判断できないため、**移動は軸拘束済みのときだけ**数値入力を許す。
    /// 回転（軸未指定＝ビュー方向軸）と拡縮（軸未指定＝全軸均一）は
    /// 単一の数値で意味が定まるので、軸未指定でも受け付ける。
    pub fn numeric_enabled(&self) -> bool {
        !(self.kind == ModalKind::Move && self.constraint.is_none())
    }

    /// 現在マウスではなく数値入力で駆動されているか。
    pub fn numeric_active(&self) -> bool {
        self.numeric_enabled() && self.numeric.is_active()
    }

    /// 数値入力の 1 文字を適用する。バッファが変化したら `true`。
    ///
    /// 受け付けない状態（軸未指定の移動）では何もせず `false`。
    pub fn apply_numeric_char(&mut self, c: char) -> bool {
        if !self.numeric_enabled() {
            return false;
        }
        self.numeric.apply_char(c)
    }

    /// 数値入力を 1 文字削除する。バッファが変化したら `true`。
    pub fn numeric_backspace(&mut self) -> bool {
        if !self.numeric_enabled() {
            return false;
        }
        self.numeric.backspace()
    }

    /// 数値入力から直接デルタ行列を組み立てる（`numeric_active()` 時のみ使う）。
    ///
    /// 単位は Blender と同じく G=メートル / R=度 / S=倍率。
    fn numeric_delta_matrix(&self) -> [[f32; 4]; 4] {
        let v = self.numeric.value();
        match self.kind {
            // 移動: 拘束軸方向へ v[m]。numeric_enabled() が拘束の存在を保証する。
            ModalKind::Move => {
                let dir = self.constraint_dir().unwrap_or([0.0, 0.0, 0.0]);
                translation_matrix([dir[0] * v, dir[1] * v, dir[2] * v])
            }
            // 回転: 軸まわりに v[度]（右手系。R X 90 で Blender と同じ向き）
            ModalKind::Rotate => {
                rotation_matrix_about(self.pivot, self.rotation_axis(), v.to_radians())
            }
            // 拡縮: 倍率 v。0 や負値は既存のスケール処理と同じく
            // MIN_SCALE_FACTOR で下限クランプされる（行列が潰れないため）。
            ModalKind::Scale => match self.constraint_dir() {
                Some(dir) => scale_matrix_axis(self.pivot, dir, v),
                None => scale_matrix_uniform(self.pivot, v),
            },
        }
    }

    /// 累積量から現在のデルタ行列（ピボット基準）を組み立てる。
    ///
    /// 累積がゼロなら単位行列を返す（＝取消時にスナップショットへ完全復元できる）。
    /// 数値入力中はマウス累積を無視し、打ち込まれた値をそのまま使う。
    pub fn delta_matrix(&self) -> [[f32; 4]; 4] {
        if self.numeric_active() {
            return self.numeric_delta_matrix();
        }
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

    // ── 数値入力バッファ（純関数）─────────────────────────────

    /// 文字列を 1 文字ずつ流し込んだ入力バッファを作る。
    fn typed(s: &str) -> ModalNumericInput {
        let mut n = ModalNumericInput::new();
        for c in s.chars() {
            n.apply_char(c);
        }
        n
    }

    #[test]
    fn numeric_empty_is_inactive_and_zero() {
        let n = ModalNumericInput::new();
        assert!(!n.is_active(), "空バッファは数値入力モードに入らない");
        assert_eq!(n.value(), 0.0);
        assert_eq!(n.display(), "");
    }

    #[test]
    fn numeric_accepts_digits() {
        let n = typed("123");
        assert!(n.is_active());
        assert_eq!(n.value(), 123.0);
        assert_eq!(n.display(), "123");
    }

    #[test]
    fn numeric_zero_is_active_and_zero_valued() {
        // "0" は「値ゼロ」であって「未入力」ではない（S 0 は 0 倍指定）
        let n = typed("0");
        assert!(n.is_active());
        assert_eq!(n.value(), 0.0);
    }

    #[test]
    fn numeric_accepts_single_decimal_point_and_ignores_second() {
        let mut n = ModalNumericInput::new();
        assert!(n.apply_char('1'));
        assert!(n.apply_char('.'), "1 個目の小数点は受理される");
        assert!(n.apply_char('5'));
        assert!(!n.apply_char('.'), "2 個目の小数点は無視される");
        assert!(n.apply_char('2'));
        assert_eq!(n.display(), "1.52");
        assert!((n.value() - 1.52).abs() < 1.0e-6);
    }

    #[test]
    fn numeric_leading_decimal_point_parses() {
        // "." だけでは数値にならないので 0 扱い。数字が続けば通常どおり。
        let dot_only = typed(".");
        assert!(dot_only.is_active(), "小数点だけでも入力は始まっている");
        assert_eq!(dot_only.value(), 0.0);

        let n = typed(".5");
        assert!((n.value() - 0.5).abs() < 1.0e-6);
    }

    #[test]
    fn numeric_trailing_decimal_point_parses() {
        let n = typed("7.");
        assert!((n.value() - 7.0).abs() < 1.0e-6);
    }

    #[test]
    fn numeric_minus_toggles_sign_anywhere() {
        // 先頭で押しても途中で押しても符号反転（Blender 準拠）
        let head = typed("-25");
        assert_eq!(head.value(), -25.0);
        assert_eq!(head.display(), "-25");

        let mid = typed("2-5");
        assert_eq!(mid.value(), -25.0, "途中の - も符号トグルであり挿入ではない");

        let twice = typed("2-5-");
        assert_eq!(twice.value(), 25.0, "2 回押すと元の符号へ戻る");
    }

    #[test]
    fn numeric_minus_alone_does_not_activate() {
        // 符号だけでは「値が無い」ので、マウス駆動のまま
        let n = typed("-");
        assert!(!n.is_active());
        assert_eq!(n.value(), 0.0);
    }

    #[test]
    fn numeric_backspace_removes_one_char() {
        let mut n = typed("1.25");
        assert!(n.backspace());
        assert_eq!(n.display(), "1.2");
        assert!(n.backspace());
        assert_eq!(n.display(), "1.");
        assert!(n.backspace());
        assert_eq!(n.display(), "1");
        assert!(n.is_active());
    }

    #[test]
    fn numeric_backspace_to_empty_returns_to_mouse_and_clears_sign() {
        let mut n = typed("-3");
        assert!(n.backspace());
        assert!(!n.is_active(), "全部消えたらマウス駆動へ戻る");
        assert_eq!(n.display(), "", "符号も一緒に解除される");
        assert_eq!(n.value(), 0.0);
        assert!(!n.backspace(), "空バッファへの Backspace は何も変えない");
    }

    #[test]
    fn numeric_backspace_clears_sign_only_state() {
        let mut n = typed("-");
        assert!(n.backspace(), "符号だけの状態も Backspace で解除できる");
        assert_eq!(n.display(), "");
        assert!(!n.backspace());
    }

    #[test]
    fn numeric_rejects_non_numeric_chars() {
        let mut n = ModalNumericInput::new();
        assert!(!n.apply_char('a'));
        assert!(!n.apply_char('+'));
        assert!(!n.apply_char(' '));
        assert!(!n.is_active());
    }

    #[test]
    fn numeric_digit_count_is_capped() {
        let long = "9".repeat(NUMERIC_MAX_DIGITS + 5);
        let n = typed(&long);
        let digits = n.display().chars().filter(|c| c.is_ascii_digit()).count();
        assert_eq!(digits, NUMERIC_MAX_DIGITS, "桁数上限で頭打ちになる");
        assert!(n.value().is_finite(), "上限内なら必ず有限値");
    }

    // ── 数値入力とモーダル状態の結合 ──────────────────────────

    #[test]
    fn numeric_disabled_for_unconstrained_move() {
        // 軸未指定の G は数値入力を受け付けない（採用仕様）
        let mut m = test_modal(ModalKind::Move);
        assert!(!m.numeric_enabled());
        assert!(!m.apply_numeric_char('5'));
        assert!(!m.numeric_active());

        // 軸を指定すると受け付けるようになる
        m.press_axis(ModalAxis::X);
        assert!(m.numeric_enabled());
        assert!(m.apply_numeric_char('5'));
        assert!(m.numeric_active());
    }

    #[test]
    fn numeric_enabled_without_axis_for_rotate_and_scale() {
        // R はビュー方向軸、S は全軸均一なので単一の数値で意味が定まる
        assert!(test_modal(ModalKind::Rotate).numeric_enabled());
        assert!(test_modal(ModalKind::Scale).numeric_enabled());
    }

    #[test]
    fn numeric_move_translates_along_constrained_axis() {
        let mut m = test_modal(ModalKind::Move);
        m.press_axis(ModalAxis::X);
        for c in "2.5".chars() {
            m.apply_numeric_char(c);
        }
        assert_mat_near(
            m.delta_matrix(),
            translation_matrix([2.5, 0.0, 0.0]),
            1.0e-5,
        );
    }

    #[test]
    fn numeric_overrides_mouse_accumulation() {
        // マウスで動かしたあとに数字を打つと、数値がそのまま結果になる
        let mut m = test_modal(ModalKind::Move);
        m.press_axis(ModalAxis::X);
        m.accumulate_move_axis(0.0, [1.0, 0.0, 0.0], 1.0);
        m.accumulate_move_axis(9.0, [1.0, 0.0, 0.0], 1.0);
        assert!((m.accum_translation[0] - 9.0).abs() < 1.0e-5);

        m.apply_numeric_char('3');
        assert_mat_near(m.delta_matrix(), translation_matrix([3.0, 0.0, 0.0]), 1.0e-5);

        // Backspace で空にすればマウスの累積量へ戻る
        m.numeric_backspace();
        assert!(!m.numeric_active());
        assert_mat_near(m.delta_matrix(), translation_matrix([9.0, 0.0, 0.0]), 1.0e-5);
    }

    #[test]
    fn numeric_rotate_uses_degrees() {
        let mut m = test_modal(ModalKind::Rotate);
        m.press_axis(ModalAxis::Z);
        for c in "90".chars() {
            m.apply_numeric_char(c);
        }
        let d = m.delta_matrix();
        let expect = rotation_matrix_about(
            [0.0, 0.0, 0.0],
            [0.0, 0.0, 1.0],
            std::f32::consts::FRAC_PI_2,
        );
        assert_mat_near(d, expect, 1.0e-5);
    }

    #[test]
    fn numeric_scale_zero_is_clamped_to_min_factor() {
        // 0 倍・負値でも行列が潰れない（既存のスケール処理と同じ下限クランプ）
        let mut zero = test_modal(ModalKind::Scale);
        zero.apply_numeric_char('0');
        assert_mat_near(
            zero.delta_matrix(),
            scale_matrix_uniform([0.0, 0.0, 0.0], MIN_SCALE_FACTOR),
            1.0e-6,
        );

        let mut neg = test_modal(ModalKind::Scale);
        for c in "-2".chars() {
            neg.apply_numeric_char(c);
        }
        assert_mat_near(
            neg.delta_matrix(),
            scale_matrix_uniform([0.0, 0.0, 0.0], MIN_SCALE_FACTOR),
            1.0e-6,
        );
    }

    #[test]
    fn numeric_survives_axis_change() {
        // Blender と同じく、軸を切り替えても打ち込んだ数値は保持される
        let mut m = test_modal(ModalKind::Scale);
        m.apply_numeric_char('3');
        m.press_axis(ModalAxis::Y);
        assert!(m.numeric_active());
        assert_mat_near(
            m.delta_matrix(),
            scale_matrix_axis([0.0, 0.0, 0.0], [0.0, 1.0, 0.0], 3.0),
            1.0e-6,
        );
    }
    // ========================================================
    //  2D キャンバス編集モーダルのテスト
    // ========================================================

    /// テスト用 2D ビュー: WYSIWYG（1 キャンバス px = 1 画面 px）。
    /// `ortho_half_h = vp_h / 2` はエディタが 2D シーンビュー初期化時に入れる値と同じ。
    const T2D_VP: [f32; 2] = [1280.0, 720.0];
    const T2D_HALF: [f32; 2] = [640.0, 360.0];
    const T2D_PAN: [f32; 2] = [0.0, 0.0];

    /// 2D 用のテストモーダル（ピボット = キャンバス px の原点、ビュー法線 = +Z）。
    fn test_modal_2d(kind: ModalKind, local_rot_deg: f32) -> ModalTransform {
        let (sin, cos) = local_rot_deg.to_radians().sin_cos();
        ModalTransform::new(
            kind,
            [0.0, 0.0, 0.0],
            canvas_px_to_screen([0.0, 0.0], T2D_PAN, T2D_HALF, T2D_VP),
            [0.0, 0.0, 1.0],
            // canvas_gizmo_axes_from_rot と同じ基底（ローカル X / Y / キャンバス法線）
            [[cos, sin, 0.0], [-sin, cos, 0.0], [0.0, 0.0, 1.0]],
        )
        .into_2d()
    }

    /// 設計スケール（1 キャンバス px = 1 画面 px）ではスクリーンデルタが
    /// そのままキャンバスデルタになる。往復変換も一致する。
    #[test]
    fn screen_delta_maps_one_to_one_at_design_scale() {
        let a = screen_to_canvas_px((100.0, 200.0), T2D_PAN, T2D_HALF, T2D_VP);
        let b = screen_to_canvas_px((130.0, 180.0), T2D_PAN, T2D_HALF, T2D_VP);
        assert!((b[0] - a[0] - 30.0).abs() < 1.0e-4, "X デルタは画面と等倍");
        assert!((b[1] - a[1] + 20.0).abs() < 1.0e-4, "Y デルタも画面と等倍（Y は下向き）");
        // 逆写像で元のスクリーン座標へ戻る
        let back = canvas_px_to_screen(a, T2D_PAN, T2D_HALF, T2D_VP);
        assert!((back.0 - 100.0).abs() < 1.0e-3 && (back.1 - 200.0).abs() < 1.0e-3);
    }

    /// ズーム 2 倍（ortho 半高が半分）なら、同じ画面移動量でキャンバスデルタは半分になる。
    #[test]
    fn screen_delta_scales_with_zoom() {
        let half = [T2D_HALF[0] * 0.5, T2D_HALF[1] * 0.5];
        let a = screen_to_canvas_px((100.0, 100.0), T2D_PAN, half, T2D_VP);
        let b = screen_to_canvas_px((140.0, 100.0), T2D_PAN, half, T2D_VP);
        assert!((b[0] - a[0] - 20.0).abs() < 1.0e-4, "画面 40px → キャンバス 20px");
    }

    /// 回転の符号: 画面上で時計回りに動かすと正の角度が溜まり、
    /// その回転行列はキャンバスの +X を +Y（画面下）側へ送る＝画面上でも時計回り。
    #[test]
    fn rotation_sign_2d_is_clockwise_positive() {
        let mut m = test_modal_2d(ModalKind::Rotate, 0.0);
        let pivot = m.pivot_screen;
        // ピボットの右（角度 0）→ 真下（角度 +90°）= 画面上で時計回り
        m.accumulate_rotation(screen_angle(pivot, (pivot.0 + 100.0, pivot.1)), ROTATION_SIGN_2D, 1.0);
        m.accumulate_rotation(screen_angle(pivot, (pivot.0, pivot.1 + 100.0)), ROTATION_SIGN_2D, 1.0);
        assert!(
            (m.accum_angle - std::f32::consts::FRAC_PI_2).abs() < 1.0e-4,
            "時計回り 90° = +90°（accum={}）",
            m.accum_angle.to_degrees()
        );
        // 回転行列がキャンバス +X を +Y（画面下）へ送ることを確認する
        let d = m.delta_matrix();
        let x = [d[0][0], d[1][0]];
        assert!(x[0].abs() < 1.0e-4 && (x[1] - 1.0).abs() < 1.0e-4, "+X → +Y: {x:?}");
    }

    /// 拡縮: ピボットからのスクリーン距離比がそのまま倍率になる。
    #[test]
    fn scale_ratio_follows_screen_distance() {
        let mut m = test_modal_2d(ModalKind::Scale, 0.0);
        let pivot = m.pivot_screen;
        m.accumulate_scale(screen_distance(pivot, (pivot.0 + 100.0, pivot.1)), 1.0);
        m.accumulate_scale(screen_distance(pivot, (pivot.0 + 250.0, pivot.1)), 1.0);
        assert!(
            (m.accum_scale - 2.5).abs() < 1.0e-4,
            "100px → 250px で 2.5 倍（accum={}）",
            m.accum_scale
        );
        // 拘束なしは均一拡縮（X も Y も同じ倍率）
        let d = m.delta_matrix();
        assert!((d[0][0] - 2.5).abs() < 1.0e-4 && (d[1][1] - 2.5).abs() < 1.0e-4);
    }

    /// Local 拘束（アクタが 90° 回転）: X 拘束はキャンバスの +Y 方向へ写る。
    #[test]
    fn local_axis_constraint_follows_actor_rotation_90() {
        let mut m = test_modal_2d(ModalKind::Move, 90.0);
        m.press_axis(ModalAxis::X); // 1 回目 = World
        let w = m.constraint_dir().expect("World 拘束");
        assert!((w[0] - 1.0).abs() < 1.0e-5 && w[1].abs() < 1.0e-5, "World X = キャンバス +X");
        m.press_axis(ModalAxis::X); // 2 回目 = Local
        let l = m.constraint_dir().expect("Local 拘束");
        assert!(
            l[0].abs() < 1.0e-5 && (l[1] - 1.0).abs() < 1.0e-5,
            "回転 90° の Local X はキャンバス +Y（画面下）: {l:?}"
        );
        // 拡縮でも同じ基底が使われる（軸方向のみ倍率が乗る）
        let mut sm = test_modal_2d(ModalKind::Scale, 90.0);
        sm.press_axis(ModalAxis::X);
        sm.press_axis(ModalAxis::X);
        sm.apply_numeric_char('2');
        let d = sm.delta_matrix();
        // Local X（= キャンバス +Y）方向だけ 2 倍、直交方向は等倍
        assert!((d[1][1] - 2.0).abs() < 1.0e-4, "キャンバス Y 成分が 2 倍: {}", d[1][1]);
        assert!((d[0][0] - 1.0).abs() < 1.0e-4, "キャンバス X 成分は等倍: {}", d[0][0]);
    }

    /// 2D では Z 拘束を受け付けない（CanvasTransform は XY しか持たないため）。
    #[test]
    fn z_axis_is_ignored_in_2d() {
        let mut m = test_modal_2d(ModalKind::Move, 0.0);
        m.press_axis(ModalAxis::Z);
        assert!(m.constraint.is_none(), "Z は拘束にならない");
        // 3D では従来どおり受け付ける
        let mut m3 = test_modal(ModalKind::Move);
        m3.press_axis(ModalAxis::Z);
        assert!(m3.constraint.is_some());
    }

    /// 2D の回転（R）は軸拘束を受け付けない（回転軸はキャンバス法線に固定）。
    #[test]
    fn rotate_ignores_axis_constraints_in_2d() {
        let mut m = test_modal_2d(ModalKind::Rotate, 30.0);
        m.press_axis(ModalAxis::X);
        m.press_axis(ModalAxis::Y);
        assert!(m.constraint.is_none(), "R 中の X/Y は無視される");
        // 回転軸は常にキャンバス法線（ビュー法線 +Z）
        let axis = m.rotation_axis();
        assert!(axis[2] > 0.999, "回転軸は +Z: {axis:?}");
    }

    /// 2D 拘束移動の数値入力: 拘束軸方向へ指定 px ぶん動く。
    #[test]
    fn numeric_move_along_2d_axis() {
        let mut m = test_modal_2d(ModalKind::Move, 0.0);
        m.press_axis(ModalAxis::Y); // World Y = キャンバス +Y（画面下）
        assert!(m.apply_numeric_char('4'));
        assert!(m.apply_numeric_char('0'));
        let d = m.delta_matrix();
        assert!(d[0][3].abs() < 1.0e-4);
        assert!((d[1][3] - 40.0).abs() < 1.0e-4, "キャンバス Y に +40px: {}", d[1][3]);
    }
}
