// ============================================================
//  actor_thumbnail.rs — アクタ・サムネイル（図鑑画像）生成の純粋ロジック
// ------------------------------------------------------------
//  役割:
//    `RENDER_ACTOR_THUMBNAIL:`（thumbnail_ops.rs）から使う「GPU に依存しない計算」を
//    ここへ集約する。GPU/ECS/IPC に触るコードと分けてあるので、そのまま単体テストできる。
//
//  ここに置くもの:
//    - 構図（AABB → 正射カメラの視点・半高・near/far・切り出し矩形）
//    - ID バッファのアルファ抜き（ID != 0 のピクセルだけ残す）
//    - 正方形の切り出しと縮小、PNG 書き出し
//    - 応答文字列の書式（エディタ側パーサとの契約）
//
//  ここに置かないもの:
//    - 描画そのもの、ワールド線の出し入れ、カメラ状態の退避／復帰
//      （すべて app/thumbnail_ops.rs 側。App への可変参照が要るため）
//
//  「なぜ ID バッファでアルファを作るのか」:
//    通常のカラーバッファは、背景（スカイボックス or クリア色）が焼き込まれていて
//    「どこまでが被写体か」を色から復元できない（魚の体色と背景色が偶然一致しうる）。
//    ID パスは「そのピクセルにどのインスタンスが描かれたか」を書く専用パスで、
//    背景は必ず 0 になる。つまり ID != 0 が被写体マスクそのものであり、
//    色に一切依存せずに完全な切り抜きが得られる。
// ============================================================

use std::path::{Path, PathBuf};

use super::texture::alpha_bleed::bleed_alpha_edges_default;
use super::thumbnail::view_basis::ViewBasis;

// ─── 定数（マジックナンバー禁止）─────────────────────────────

/// 被写体の周囲に空ける余白の比率（1.0 = AABB ちょうど、1.1 = 10% 余白）。
///
/// 図鑑の枠にぴったり接するとトリミングされたように見えるので、少しだけ空ける。
pub const FRAMING_MARGIN_RATIO: f32 = 1.10;

/// 正射投影のニアクリップ面（ワールド単位）。
///
/// 正射投影では near を小さくしても精度が落ちない（深度が線形）ため、
/// 「AABB より手前に置いた視点」から確実に写るだけの小さな値でよい。
pub const ORTHO_NEAR: f32 = 0.01;

/// 視点を AABB の外側へ引く距離の、AABB 奥行き半分に対する倍率。
///
/// 1.0 だと視点が AABB の面上に載って near クリップで欠けるので、余裕を持たせる。
pub const EYE_PULLBACK_FACTOR: f32 = 2.0;

/// 視点を引く距離の下限（ワールド単位）。極端に薄い AABB でも near より必ず遠くする。
pub const EYE_PULLBACK_MIN: f32 = 1.0;

/// ファークリップ面を「視点からの距離 + AABB 奥行き」より更に伸ばす余裕（ワールド単位）。
pub const FAR_SLACK: f32 = 1.0;

/// 正射半高の下限（ワールド単位）。AABB が退化（点）でもゼロ除算にしない。
pub const ORTHO_HALF_H_MIN: f32 = 0.001;

/// 生成できるサムネイル一辺の最小ピクセル数。
pub const THUMBNAIL_SIZE_MIN: u32 = 16;

/// 生成できるサムネイル一辺の最大ピクセル数。
pub const THUMBNAIL_SIZE_MAX: u32 = 4096;

/// 完全不透明を表すアルファ値。
const ALPHA_OPAQUE: u8 = 255;

/// 完全透明を表すアルファ値。
const ALPHA_TRANSPARENT: u8 = 0;

/// RGBA8 の 1 ピクセルあたりのバイト数。
const RGBA_BYTES_PER_PIXEL: usize = 4;

/// RGBA8 のうちアルファ成分のバイト位置。
const RGBA_ALPHA_OFFSET: usize = 3;

/// 応答（成功）の接頭辞。エディタ側 RuntimeManager のパーサと対になる。
pub const REPLY_DONE_PREFIX: &str = "RENDER_ACTOR_THUMBNAIL_DONE:";

/// 応答（失敗）の接頭辞。
pub const REPLY_ERROR_PREFIX: &str = "RENDER_ACTOR_THUMBNAIL_ERROR:";

// ============================================================
//  ThumbnailView — どの向きから撮るか
// ============================================================

/// サムネイルの視点方向。
///
/// いずれも「その軸のプラス側に視点を置き、原点方向（マイナス方向）を向く」。
/// 図鑑は魚を横から見せたいので既定は [`ThumbnailView::Side`]。
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ThumbnailView {
    /// +X 側から −X 方向を見る（真横）。画面右＝ワールド +Z、画面上＝ワールド +Y。
    Side,
    /// +Z 側から −Z 方向を見る（正面）。画面右＝ワールド −X、画面上＝ワールド +Y。
    Front,
    /// +Y 側から −Y 方向を見る（真上）。画面右＝ワールド +X、画面上＝ワールド +Z。
    Top,
}

impl ThumbnailView {
    /// IPC 引数の文字列（"side" / "front" / "top"）から解釈する。大文字小文字は無視。
    pub fn parse(text: &str) -> Option<Self> {
        match text.trim().to_ascii_lowercase().as_str() {
            "side" => Some(Self::Side),
            "front" => Some(Self::Front),
            "top" => Some(Self::Top),
            _ => None,
        }
    }

    /// 視線方向（視点 → 被写体の単位ベクトル）。
    fn forward(self) -> [f32; 3] {
        match self {
            Self::Side => [-1.0, 0.0, 0.0],
            Self::Front => [0.0, 0.0, -1.0],
            Self::Top => [0.0, -1.0, 0.0],
        }
    }

    /// 画面上方向にあたるワールド軸。
    ///
    /// 真上から見る Top のときだけ、視線と上方向が平行になってしまうので +Z を上に取る
    /// （こうすると「上が魚の頭、下が尾」ではなく「奥行き方向が画面の上下」になる）。
    fn up(self) -> [f32; 3] {
        match self {
            Self::Side | Self::Front => [0.0, 1.0, 0.0],
            Self::Top => [0.0, 0.0, 1.0],
        }
    }

    /// 画面右方向にあたるワールドベクトル。
    ///
    /// 左手座標系なので right = cross(up, forward)。
    /// 追い込み（`refine_framing`）が返すパン量をワールドへ戻すのに使う。
    pub fn right(self) -> [f32; 3] {
        let u = self.up();
        let f = self.forward();
        [
            u[1] * f[2] - u[2] * f[1],
            u[2] * f[0] - u[0] * f[2],
            u[0] * f[1] - u[1] * f[0],
        ]
    }

    // ─── 軸決め打ちの旧・射影式（テスト専用） ─────────────────
    //
    //  構図計算を `thumbnail::view_basis` の一般形へ委譲する前は、
    //  「Side なら画面横は size[2]」のようにビューごとの決め打ちで幅を求めていた。
    //  一般形（射影幅 = |a.x|·sx + |a.y|·sy + |a.z|·sz）はこれと同じ値を返すはずだが、
    //  それは**証明されるべき前提**であってコメントで済ませてよいことではない。
    //  そこで旧式をここに残し、`general_extents_match_the_legacy_axis_formulas`
    //  で 1 つずつ突き合わせている。委譲によって図鑑の構図が動いていないことの番人。

    /// AABB のサイズ [x, y, z] から「画面横方向」に写るワールド長さを取り出す（旧式）。
    #[cfg(test)]
    fn screen_width_of(self, size: [f32; 3]) -> f32 {
        match self {
            Self::Side => size[2],  // 画面右 = ±Z
            Self::Front => size[0], // 画面右 = ∓X
            Self::Top => size[0],   // 画面右 = ±X
        }
    }

    /// AABB のサイズ [x, y, z] から「画面縦方向」に写るワールド長さを取り出す（旧式）。
    #[cfg(test)]
    fn screen_height_of(self, size: [f32; 3]) -> f32 {
        match self {
            Self::Side | Self::Front => size[1], // 画面上 = ±Y
            Self::Top => size[2],                // 画面上 = ±Z
        }
    }

    /// AABB のサイズ [x, y, z] から「視線方向（奥行き）」のワールド長さを取り出す（旧式）。
    #[cfg(test)]
    fn depth_of(self, size: [f32; 3]) -> f32 {
        match self {
            Self::Side => size[0],
            Self::Front => size[2],
            Self::Top => size[1],
        }
    }

    /// このビューにおけるカメラの yaw（Y 軸回転・ラジアン）。
    ///
    /// 左手座標系で forward = (sin(yaw)·cos(pitch), −sin(pitch), cos(yaw)·cos(pitch))。
    pub fn yaw(self) -> f32 {
        match self {
            // forward = (−1, 0, 0) → sin(yaw) = −1
            Self::Side => -std::f32::consts::FRAC_PI_2,
            // forward = (0, 0, −1) → cos(yaw) = −1
            Self::Front => std::f32::consts::PI,
            // 真下向きは yaw が不定。パンの基準として 0 を使う。
            Self::Top => 0.0,
        }
    }

    /// このビューにおけるカメラの pitch（X 軸回転・ラジアン。正 = 下向き）。
    pub fn pitch(self) -> f32 {
        match self {
            Self::Side | Self::Front => 0.0,
            // forward = (0, −1, 0) → sin(pitch) = 1
            Self::Top => std::f32::consts::FRAC_PI_2,
        }
    }

    /// このビューを、方向に依存しない一般形の基底（[`ViewBasis`]）へ変換する。
    ///
    /// 構図計算の本体はモデルサムネイルと共有する（`thumbnail::view_basis`）。
    /// その際、基底を計算で作り直すと丸め誤差で図鑑の絵が変わりかねないので、
    /// ここが持っている**厳密な定数（0 / ±1 / ±π/2）をそのまま**渡す。
    pub fn to_basis(self) -> ViewBasis {
        ViewBasis::from_parts(
            self.forward(),
            self.up(),
            self.right(),
            self.yaw(),
            self.pitch(),
        )
    }
}

// ============================================================
//  Framing — AABB から決まるカメラ配置と切り出し矩形
// ============================================================

/// サムネイル 1 枚ぶんの構図。
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct Framing {
    /// カメラ位置（ワールド）。
    pub eye: [f32; 3],
    /// 注視点（＝ AABB の中心。ワールド）。
    pub target: [f32; 3],
    /// 画面上方向（ワールド）。
    pub up: [f32; 3],
    /// 正射投影の縦方向の描画範囲（半分の高さ・ワールド単位）。
    pub ortho_half_h: f32,
    /// ニアクリップ面。
    pub near: f32,
    /// ファークリップ面。
    pub far: f32,
    /// カメラの yaw（ラジアン）。
    pub yaw: f32,
    /// カメラの pitch（ラジアン）。
    pub pitch: f32,
}

/// フレームバッファから切り出す正方形の領域（ピクセル）。
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct CropRect {
    /// 左端の X（ピクセル）。
    pub x: u32,
    /// 上端の Y（ピクセル）。
    pub y: u32,
    /// 一辺の長さ（ピクセル）。
    pub side: u32,
}

/// ビューポートの中央から取れる最大の正方形を求める。
///
/// サムネイルは正方形だが、実際に描くのはウィンドウ全面（＝縦横比が 1 ではない）なので、
/// 「中央の正方形だけを切り出して縮小する」ことで正方形の絵にする。
pub fn center_square_crop(viewport_w: u32, viewport_h: u32) -> CropRect {
    let side = viewport_w.min(viewport_h);
    CropRect {
        x: (viewport_w - side) / 2,
        y: (viewport_h - side) / 2,
        side,
    }
}

// ============================================================
//  AABB の合成
// ============================================================

/// ローカル AABB を行列で変換し、変換後の 8 頂点を包む軸平行 AABB を返す。
///
/// 行列は**行優先**（平行移動が `m[0][3]`, `m[1][3]`, `m[2][3]`）。
/// SEED の `instance_mats` / `ModelComponent::render_matrix` と同じ並び。
///
/// 回転が入ると「変換後の AABB」は元の AABB より必ず大きくなるが、
/// サムネイルの構図決めには十分（被写体が枠から出ないことのほうが重要）。
pub fn transform_aabb(
    local_min: [f32; 3],
    local_max: [f32; 3],
    matrix: &[[f32; 4]; 4],
) -> ([f32; 3], [f32; 3]) {
    let mut min = [f32::INFINITY; 3];
    let mut max = [f32::NEG_INFINITY; 3];

    // ローカル AABB の 8 頂点をすべて変換して包み直す
    for corner in 0..8 {
        let point = [
            if corner & 1 == 0 { local_min[0] } else { local_max[0] },
            if corner & 2 == 0 { local_min[1] } else { local_max[1] },
            if corner & 4 == 0 { local_min[2] } else { local_max[2] },
        ];
        for axis in 0..3 {
            let value = matrix[axis][0] * point[0]
                + matrix[axis][1] * point[1]
                + matrix[axis][2] * point[2]
                + matrix[axis][3];
            min[axis] = min[axis].min(value);
            max[axis] = max[axis].max(value);
        }
    }
    (min, max)
}

/// 2 つの AABB を包む AABB を返す。
pub fn merge_aabb(
    a: ([f32; 3], [f32; 3]),
    b: ([f32; 3], [f32; 3]),
) -> ([f32; 3], [f32; 3]) {
    let mut min = [0.0f32; 3];
    let mut max = [0.0f32; 3];
    for axis in 0..3 {
        min[axis] = a.0[axis].min(b.0[axis]);
        max[axis] = a.1[axis].max(b.1[axis]);
    }
    (min, max)
}

// ============================================================
//  ID マスク → アルファ
// ============================================================

/// ID バッファのアルファ成分（`bitcast<f32>(instance_id)`）を使って RGBA を切り抜く。
///
/// ID パスは背景に 0 を書く（id_pass.rs のヘッダ参照）ので、
/// **ビットパターンが 0 のピクセルだけが背景**である。
/// 色の一致に頼らないため、魚の体色が背景色と同じでも誤爆しない。
///
/// # 引数
/// * `rgba` — フレームから読み戻した RGBA8 バッファ（`width · height · 4` バイト）。**破壊的に更新**。
/// * `id_alpha` — 同じ並びの ID アルファ成分（`width · height` 要素）。
///
/// # 戻り値
/// 不透明にしたピクセル数。0 なら「何も写っていない」＝失敗として扱える。
///
/// # パニック
/// バッファ長が食い違う場合はパニックせず、短いほうに合わせて処理する
/// （読み戻しの行パディング処理を先に済ませてある前提だが、防御的に扱う）。
pub fn apply_id_mask_alpha(rgba: &mut [u8], id_alpha: &[f32]) -> u32 {
    let pixel_count = (rgba.len() / RGBA_BYTES_PER_PIXEL).min(id_alpha.len());
    let mut opaque = 0u32;
    for index in 0..pixel_count {
        // -0.0 もビットパターンは 0 ではないため to_bits で厳密に判定する
        let is_background = id_alpha[index].to_bits() == 0;
        let alpha_byte = index * RGBA_BYTES_PER_PIXEL + RGBA_ALPHA_OFFSET;
        if is_background {
            rgba[alpha_byte] = ALPHA_TRANSPARENT;
        } else {
            rgba[alpha_byte] = ALPHA_OPAQUE;
            opaque += 1;
        }
    }
    opaque
}

// ============================================================
//  構図の追い込み（実際に描けた範囲から測り直す）
// ============================================================
//
//  なぜ AABB だけでは足りないか:
//    モデルのローカル AABB（`Model::local_aabb`）は glTF の**全メッシュの生頂点**を
//    包む箱で、実際に描かれる範囲と一致するとは限らない。
//    実測でも、伊勢海老・竜宮の使い・タツノオトシゴのように
//    「AABB が見えている本体の数倍ある」prefab が存在した
//    （描画されない補助メッシュや、スキンのバインドポーズのずれが原因）。
//    その結果、被写体が隅に小さく寄って写る。
//
//  そこで「一度撮ってみて、ID マスクの実際の広がりから測り直す」。
//  正射投影ではピクセル ↔ ワールドが線形なので、測った矩形から
//  必要な半高とカメラのパン量を厳密に逆算できる（近似ではない）。

/// ID マスク上で被写体が占める矩形（両端を含む・ピクセル）。
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct MaskBounds {
    pub min_x: u32,
    pub min_y: u32,
    pub max_x: u32,
    pub max_y: u32,
}

impl MaskBounds {
    /// 幅（ピクセル、両端を含むので +1）。
    pub fn width(&self) -> u32 { self.max_x - self.min_x + 1 }
    /// 高さ（ピクセル、両端を含むので +1）。
    pub fn height(&self) -> u32 { self.max_y - self.min_y + 1 }
}

/// ID マスク（0 = 背景）から被写体の矩形を測る。1 ピクセルも無ければ None。
pub fn measure_mask_bounds(id_alpha: &[f32], width: u32, height: u32) -> Option<MaskBounds> {
    let mut min_x = u32::MAX;
    let mut min_y = u32::MAX;
    let mut max_x = 0u32;
    let mut max_y = 0u32;
    let mut found = false;

    for y in 0..height {
        let row = (y as usize) * (width as usize);
        for x in 0..width {
            let Some(value) = id_alpha.get(row + x as usize) else { continue };
            if value.to_bits() == 0 {
                continue;
            }
            found = true;
            min_x = min_x.min(x);
            min_y = min_y.min(y);
            max_x = max_x.max(x);
            max_y = max_y.max(y);
        }
    }

    found.then_some(MaskBounds { min_x, min_y, max_x, max_y })
}

/// 追い込みの判定結果。
#[derive(Debug, Clone, Copy, PartialEq)]
pub enum RefineDecision {
    /// 構図は十分。このまま合成してよい。
    Accept,
    /// カメラを直して撮り直す。
    Retry {
        /// 新しい正射半高。
        ortho_half_h: f32,
        /// カメラを画面右方向へ動かす量（ワールド単位。負なら左）。
        pan_right: f32,
        /// カメラを画面上方向へ動かす量（ワールド単位。負なら下）。
        pan_up: f32,
    },
}

/// 中心ずれの許容量（切り出し正方形の一辺に対する比率）。
///
/// これ以内なら「中央に写っている」とみなして撮り直さない。
pub const REFINE_CENTER_TOLERANCE: f32 = 0.01;

/// 大きさのずれの許容量（半高の比率）。
///
/// 目標の半高と現在の半高がこの比率以内なら撮り直さない。
pub const REFINE_SIZE_TOLERANCE: f32 = 0.05;

/// 被写体が画面端に接していた（＝見切れている疑いがある）ときに、
/// 最低でもこの倍率までしか寄せない（ズームインで見切れを悪化させないための歯止め）。
pub const REFINE_CLIPPED_MIN_ZOOM: f32 = 1.0;

/// 実測した矩形から、構図を受理するか撮り直すかを決める。
///
/// # 引数
/// * `bounds` — ID マスクで測った被写体の矩形。
/// * `crop` — 最終的に切り出す中央の正方形。
/// * `viewport_w` / `viewport_h` — フレームバッファの大きさ。
/// * `current_half_h` — 今回の撮影に使った正射半高。
/// * `margin_ratio` — 余白の比率。
pub fn refine_framing(
    bounds: MaskBounds,
    crop: CropRect,
    viewport_w: u32,
    viewport_h: u32,
    current_half_h: f32,
    margin_ratio: f32,
) -> RefineDecision {
    let viewport_h_f = viewport_h.max(1) as f32;
    let side = crop.side.max(1) as f32;

    // 正射投影では 1 ピクセルが表すワールド長さは縦横同じ
    let world_per_pixel = 2.0 * current_half_h / viewport_h_f;

    // 被写体の中心が画面中心からどれだけずれているか（ピクセル）
    let center_x = (bounds.min_x + bounds.max_x + 1) as f32 * 0.5;
    let center_y = (bounds.min_y + bounds.max_y + 1) as f32 * 0.5;
    let offset_x = center_x - viewport_w.max(1) as f32 * 0.5;
    let offset_y = center_y - viewport_h_f * 0.5;

    // 中央正方形へ収めるのに必要な半高
    let needed_half_px = bounds.width().max(bounds.height()) as f32 * 0.5 * margin_ratio;
    let mut target_half_h = needed_half_px * world_per_pixel * viewport_h_f / side;

    // 画面端に接している＝見切れている可能性がある。測った大きさは下限でしかないので、
    // このパスでは決してズームインしない（＝さらに見切れることを防ぐ）。
    let clipped = bounds.min_x == 0
        || bounds.min_y == 0
        || bounds.max_x + 1 >= viewport_w
        || bounds.max_y + 1 >= viewport_h;
    if clipped {
        target_half_h = target_half_h.max(current_half_h * REFINE_CLIPPED_MIN_ZOOM);
    }
    target_half_h = target_half_h.max(ORTHO_HALF_H_MIN);

    // 受理判定: 中心ずれも大きさのずれも許容範囲内なら撮り直さない
    let center_ok = offset_x.abs() <= side * REFINE_CENTER_TOLERANCE
        && offset_y.abs() <= side * REFINE_CENTER_TOLERANCE;
    let size_ok = (target_half_h / current_half_h.max(f32::MIN_POSITIVE) - 1.0).abs()
        <= REFINE_SIZE_TOLERANCE;
    if center_ok && size_ok && !clipped {
        return RefineDecision::Accept;
    }

    RefineDecision::Retry {
        ortho_half_h: target_half_h,
        // 被写体が右にずれているならカメラも右へ動かす（画面上では左へ戻る）
        pan_right: offset_x * world_per_pixel,
        // 画面の Y は下向きなので符号を反転する
        pan_up: -offset_y * world_per_pixel,
    }
}

// ============================================================
//  切り出し・縮小・書き出し
// ============================================================

/// RGBA8 バッファから矩形を切り出す。範囲外を指定された場合はクランプする。
pub fn crop_rgba(
    src: &[u8],
    src_w: u32,
    src_h: u32,
    rect: CropRect,
) -> (Vec<u8>, u32, u32) {
    let x0 = rect.x.min(src_w);
    let y0 = rect.y.min(src_h);
    let w = rect.side.min(src_w.saturating_sub(x0));
    let h = rect.side.min(src_h.saturating_sub(y0));
    let mut out = Vec::with_capacity((w as usize) * (h as usize) * RGBA_BYTES_PER_PIXEL);
    for row in 0..h {
        let start = (((y0 + row) as usize) * (src_w as usize) + x0 as usize) * RGBA_BYTES_PER_PIXEL;
        let end = start + (w as usize) * RGBA_BYTES_PER_PIXEL;
        out.extend_from_slice(&src[start..end]);
    }
    (out, w, h)
}

/// アルファ付き画像を安全に縮小して PNG として書き出す。
///
/// # 縮小前にアルファブリードを掛ける理由
/// 透明ピクセルの RGB は「背景の色」が残っている。そのまま縮小すると、境界の
/// 半透明ピクセルに背景色が混ざって輪郭が縁取られて見える（フリンジ）。
/// [`bleed_alpha_edges_default`] で透明側の RGB を隣の不透明色で塗り潰しておけば、
/// 何が混ざっても被写体の色になるのでフリンジが出ない。
///
/// # 引数
/// * `rgba` — 切り出し済みの正方形 RGBA8。**アルファブリードのため可変**。
/// * `src_size` — `rgba` の一辺（正方形前提）。
/// * `out_size` — 出力する一辺（ピクセル）。
/// * `out_path` — 出力先。親ディレクトリが無ければ作る。
pub fn write_thumbnail_png(
    rgba: &mut Vec<u8>,
    src_size: u32,
    out_size: u32,
    out_path: &Path,
) -> Result<(), String> {
    // 1. 縁のにじみ対策（縮小・拡大の前に必ず掛ける）
    bleed_alpha_edges_default(src_size, src_size, rgba);

    // 2. image クレートへ載せる
    let image = image::RgbaImage::from_raw(src_size, src_size, std::mem::take(rgba))
        .ok_or_else(|| format!("画素数が一致しません（一辺 {src_size}px）"))?;

    // 3. 目的の大きさへリサンプル（同じ大きさなら何もしない）
    let resized = if src_size == out_size {
        image
    } else {
        image::imageops::resize(&image, out_size, out_size, image::imageops::FilterType::Lanczos3)
    };

    // 4. 親ディレクトリを作ってから保存
    if let Some(parent) = out_path.parent() {
        std::fs::create_dir_all(parent)
            .map_err(|e| format!("出力先ディレクトリを作成できません（{}）: {e}", parent.display()))?;
    }
    resized
        .save(out_path)
        .map_err(|e| format!("PNG を書き出せません（{}）: {e}", out_path.display()))?;
    Ok(())
}

// ============================================================
//  引数・パスの取り扱い
// ============================================================

/// `RENDER_ACTOR_THUMBNAIL:` の引数一式。
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ThumbnailRequest {
    /// 読み込む .actor（`assets://` 仮想パスまたは絶対パス）。
    pub actor_path: String,
    /// 出力する PNG の絶対パス。
    pub out_png: String,
    /// 出力する一辺のピクセル数。
    pub size_px: u32,
    /// 視点方向。
    pub view: ThumbnailView,
}

/// `{actor},{out_png},{size},{view}` を解釈する。
///
/// # パスとカンマ
/// 引数はカンマ区切りだが、パスにカンマが含まれると復元不可能になる。
/// Windows のパスにカンマは使えるものの、アセットの命名規約として禁止するほうが
/// プロトコルを単純に保てるので、**カンマを含むパスは明示的に拒否**する
/// （黙って壊れるより、はっきり失敗させる）。
pub fn parse_request_args(args: &str) -> Result<ThumbnailRequest, String> {
    let parts: Vec<&str> = args.split(',').map(str::trim).collect();
    if parts.len() != 4 {
        return Err(format!(
            "引数は「アクタパス,出力PNG,一辺px,ビュー」の 4 つです（{} 個でした。パスにカンマは使えません）",
            parts.len()
        ));
    }

    let actor_path = parts[0].to_string();
    let out_png = parts[1].to_string();
    if actor_path.is_empty() {
        return Err("アクタパスが空です".to_string());
    }
    if out_png.is_empty() {
        return Err("出力 PNG のパスが空です".to_string());
    }

    let size_px: u32 = parts[2]
        .parse()
        .map_err(|_| format!("一辺のピクセル数を数値として読めません: '{}'", parts[2]))?;
    if !(THUMBNAIL_SIZE_MIN..=THUMBNAIL_SIZE_MAX).contains(&size_px) {
        return Err(format!(
            "一辺のピクセル数は {THUMBNAIL_SIZE_MIN}〜{THUMBNAIL_SIZE_MAX} の範囲です（{size_px} が指定されました）"
        ));
    }

    let view = ThumbnailView::parse(parts[3])
        .ok_or_else(|| format!("ビューは side / front / top のいずれかです: '{}'", parts[3]))?;

    Ok(ThumbnailRequest { actor_path, out_png, size_px, view })
}

/// `assets://` 仮想パスにも絶対パスにも対応してファイルパスへ解決する。
pub fn resolve_actor_path(path: &str) -> PathBuf {
    crate::engine::asset_fs::resolve(path)
}

/// 成功応答を組み立てる。
pub fn format_done(out_png: &str) -> String {
    format!("{REPLY_DONE_PREFIX}{out_png}")
}

/// 失敗応答を組み立てる。
pub fn format_error(message: &str) -> String {
    format!("{REPLY_ERROR_PREFIX}{message}")
}

// ============================================================
//  単体テスト
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;
    use super::super::thumbnail::view_basis::{compute_framing_for_basis, extent_along};

    /// テスト用: 1 辺 1.0 の立方体 AABB。
    const UNIT_MIN: [f32; 3] = [-0.5, -0.5, -0.5];
    const UNIT_MAX: [f32; 3] = [0.5, 0.5, 0.5];

    /// 浮動小数の近似比較。
    fn close(a: f32, b: f32) -> bool {
        (a - b).abs() < 1e-4
    }

    /// テスト用: ビューを指定して構図を求める。
    ///
    /// 本番の撮影経路（`app/thumbnail_ops.rs` の `ThumbnailPlan`）は最初から基底を
    /// 持っているので、ビューを受け取る入口は存在しない。ここだけ `to_basis()` を
    /// 挟んで、従来どおり「ビューを指定した構図」を検査できるようにする。
    fn framing_for(
        aabb_min: [f32; 3],
        aabb_max: [f32; 3],
        view: ThumbnailView,
        viewport_w: u32,
        viewport_h: u32,
        margin_ratio: f32,
    ) -> Framing {
        compute_framing_for_basis(
            aabb_min,
            aabb_max,
            view.to_basis(),
            viewport_w,
            viewport_h,
            margin_ratio,
        )
    }

    // ─── 応答書式（エディタとの契約）─────────────────────────

    #[test]
    fn reply_formats_match_protocol() {
        assert_eq!(
            format_done(r"C:\out\iwashi.png"),
            r"RENDER_ACTOR_THUMBNAIL_DONE:C:\out\iwashi.png"
        );
        assert_eq!(
            format_error("アクタを読み込めません"),
            "RENDER_ACTOR_THUMBNAIL_ERROR:アクタを読み込めません"
        );
    }

    // ─── 引数パース ───────────────────────────────────────────

    #[test]
    fn parse_request_accepts_valid_args() {
        let req = parse_request_args(
            r"assets://mainGame/actors/Fish/Lv1/iwashi.actor,C:\out\iwashi.png,512,side",
        )
        .expect("解釈できるはず");
        assert_eq!(req.actor_path, "assets://mainGame/actors/Fish/Lv1/iwashi.actor");
        assert_eq!(req.out_png, r"C:\out\iwashi.png");
        assert_eq!(req.size_px, 512);
        assert_eq!(req.view, ThumbnailView::Side);
    }

    #[test]
    fn parse_request_trims_whitespace_and_is_case_insensitive() {
        let req = parse_request_args(" a.actor , b.png , 64 , TOP ").expect("解釈できるはず");
        assert_eq!(req.actor_path, "a.actor");
        assert_eq!(req.out_png, "b.png");
        assert_eq!(req.view, ThumbnailView::Top);
    }

    #[test]
    fn parse_request_rejects_malformed() {
        // 引数の数違い（パスにカンマが混ざった場合もここで落ちる）
        assert!(parse_request_args("a.actor,b.png,512").is_err());
        assert!(parse_request_args("a,b,c,512,side").is_err());
        // 空パス
        assert!(parse_request_args(",b.png,512,side").is_err());
        assert!(parse_request_args("a.actor,,512,side").is_err());
        // 数値でない・範囲外
        assert!(parse_request_args("a.actor,b.png,xx,side").is_err());
        assert!(parse_request_args("a.actor,b.png,0,side").is_err());
        assert!(parse_request_args("a.actor,b.png,99999,side").is_err());
        // 未知のビュー
        assert!(parse_request_args("a.actor,b.png,512,diagonal").is_err());
    }

    // ─── 切り出し矩形 ─────────────────────────────────────────

    #[test]
    fn center_square_crop_takes_the_shorter_side() {
        // 横長: 高さが一辺になり、左右が均等に切られる
        assert_eq!(center_square_crop(1920, 1080), CropRect { x: 420, y: 0, side: 1080 });
        // 縦長: 幅が一辺になり、上下が均等に切られる
        assert_eq!(center_square_crop(600, 800), CropRect { x: 0, y: 100, side: 600 });
        // 正方形: そのまま
        assert_eq!(center_square_crop(512, 512), CropRect { x: 0, y: 0, side: 512 });
    }

    // ─── 構図 ─────────────────────────────────────────────────

    #[test]
    fn framing_places_eye_on_the_positive_axis_of_the_view() {
        // Side: +X 側から見る
        let f = framing_for(UNIT_MIN, UNIT_MAX, ThumbnailView::Side, 1024, 1024, 1.0);
        assert!(f.eye[0] > f.target[0], "視点は +X 側に無ければならない");
        assert!(close(f.eye[1], f.target[1]) && close(f.eye[2], f.target[2]));

        // Front: +Z 側から見る
        let f = framing_for(UNIT_MIN, UNIT_MAX, ThumbnailView::Front, 1024, 1024, 1.0);
        assert!(f.eye[2] > f.target[2], "視点は +Z 側に無ければならない");

        // Top: +Y 側から見る
        let f = framing_for(UNIT_MIN, UNIT_MAX, ThumbnailView::Top, 1024, 1024, 1.0);
        assert!(f.eye[1] > f.target[1], "視点は +Y 側に無ければならない");
    }

    /// 一般形の射影幅が、委譲前の軸決め打ちの式と**厳密に**一致すること。
    ///
    /// `compute_framing` は `thumbnail::view_basis` の一般形へ委譲している。
    /// ここが 1 つでもずれると図鑑サムネイルの構図（寄り・引き）が静かに変わるため、
    /// 3 ビュー × 3 方向（画面横・画面縦・奥行き）をすべて突き合わせる。
    #[test]
    fn general_extents_match_the_legacy_axis_formulas() {
        // 辺の長さが全部違う箱（取り違えを見逃さないため）
        let size = [2.0, 3.0, 5.0];
        for view in [ThumbnailView::Side, ThumbnailView::Front, ThumbnailView::Top] {
            let basis = view.to_basis();
            assert_eq!(
                extent_along(basis.right, size),
                view.screen_width_of(size),
                "{view:?}: 画面横の射影幅が旧式と違う"
            );
            assert_eq!(
                extent_along(basis.up, size),
                view.screen_height_of(size),
                "{view:?}: 画面縦の射影幅が旧式と違う"
            );
            assert_eq!(
                extent_along(basis.forward, size),
                view.depth_of(size),
                "{view:?}: 奥行きの射影幅が旧式と違う"
            );
        }
    }

    #[test]
    fn framing_targets_the_aabb_center() {
        // 原点からずれた AABB でも中心を向く
        let f = framing_for([1.0, 2.0, 3.0], [3.0, 6.0, 9.0], ThumbnailView::Side, 512, 512, 1.0);
        assert!(close(f.target[0], 2.0));
        assert!(close(f.target[1], 4.0));
        assert!(close(f.target[2], 6.0));
    }

    #[test]
    fn framing_half_height_covers_the_larger_screen_axis() {
        // Side ビューでは画面横 = Z、画面縦 = Y。Z のほうが長い細長い魚を想定。
        // 正方形ビューポートなので half_h = max(Z, Y)/2 × 余白比
        let min = [-0.1, -1.0, -5.0];
        let max = [0.1, 1.0, 5.0];
        let f = framing_for(min, max, ThumbnailView::Side, 800, 800, 1.0);
        assert!(close(f.ortho_half_h, 5.0), "half_h={}", f.ortho_half_h);

        // 余白 10% を掛けると 10% 大きくなる
        let f = framing_for(min, max, ThumbnailView::Side, 800, 800, 1.1);
        assert!(close(f.ortho_half_h, 5.5), "half_h={}", f.ortho_half_h);
    }

    #[test]
    fn framing_compensates_for_a_non_square_viewport() {
        // 横長ビューポート（高さが短辺）: 中央正方形の一辺 = 高さ なので補正なし
        let f = framing_for(UNIT_MIN, UNIT_MAX, ThumbnailView::Side, 1920, 1080, 1.0);
        assert!(close(f.ortho_half_h, 0.5), "half_h={}", f.ortho_half_h);

        // 縦長ビューポート（幅が短辺）: 中央正方形は幅ぶんしかないので
        // half_h を viewport_h / side = 800/600 倍に広げる必要がある
        let f = framing_for(UNIT_MIN, UNIT_MAX, ThumbnailView::Side, 600, 800, 1.0);
        assert!(close(f.ortho_half_h, 0.5 * 800.0 / 600.0), "half_h={}", f.ortho_half_h);
    }

    #[test]
    fn framing_clip_planes_enclose_the_subject() {
        let f = framing_for([-2.0, -2.0, -2.0], [2.0, 2.0, 2.0], ThumbnailView::Side, 512, 512, 1.1);
        // 視点から被写体の最も手前の面までの距離
        let nearest = f.eye[0] - 2.0;
        let farthest = f.eye[0] - (-2.0);
        assert!(f.near < nearest, "near({}) は被写体手前({nearest}) より小さいこと", f.near);
        assert!(f.far > farthest, "far({}) は被写体奥({farthest}) より大きいこと", f.far);
    }

    #[test]
    fn framing_survives_a_degenerate_aabb() {
        // 大きさゼロ（点）でもゼロ除算・ゼロ半高にならない
        let f = framing_for([1.0; 3], [1.0; 3], ThumbnailView::Side, 512, 512, 1.1);
        assert!(f.ortho_half_h >= ORTHO_HALF_H_MIN);
        assert!(f.far > f.near);
        assert!(f.eye[0] > f.target[0]);
    }

    #[test]
    fn framing_view_angles_point_the_camera_the_right_way() {
        // 左手座標系 forward = (sin(yaw)cos(pitch), -sin(pitch), cos(yaw)cos(pitch))
        for view in [ThumbnailView::Side, ThumbnailView::Front, ThumbnailView::Top] {
            let (yaw, pitch) = (view.yaw(), view.pitch());
            let forward = [
                yaw.sin() * pitch.cos(),
                -pitch.sin(),
                yaw.cos() * pitch.cos(),
            ];
            let expected = view.forward();
            for axis in 0..3 {
                assert!(
                    close(forward[axis], expected[axis]),
                    "{view:?} の軸{axis}: yaw/pitch から {} だが期待は {}",
                    forward[axis],
                    expected[axis]
                );
            }
        }
    }

    // ─── AABB の変換・合成 ───────────────────────────────────

    #[test]
    fn transform_aabb_applies_translation_from_the_last_column() {
        // 行優先。平行移動は m[i][3]。
        let mut m = [[0.0f32; 4]; 4];
        for i in 0..4 { m[i][i] = 1.0; }
        m[0][3] = 10.0;
        m[1][3] = -5.0;
        m[2][3] = 2.0;

        let (min, max) = transform_aabb(UNIT_MIN, UNIT_MAX, &m);
        assert!(close(min[0], 9.5) && close(max[0], 10.5));
        assert!(close(min[1], -5.5) && close(max[1], -4.5));
        assert!(close(min[2], 1.5) && close(max[2], 2.5));
    }

    #[test]
    fn transform_aabb_grows_the_box_under_rotation() {
        // Y 軸まわり 45°。XZ 平面の対角がそのまま幅になるので √2 倍へ広がる。
        let (c, s) = (std::f32::consts::FRAC_PI_4.cos(), std::f32::consts::FRAC_PI_4.sin());
        let m = [
            [c, 0.0, s, 0.0],
            [0.0, 1.0, 0.0, 0.0],
            [-s, 0.0, c, 0.0],
            [0.0, 0.0, 0.0, 1.0],
        ];
        let (min, max) = transform_aabb(UNIT_MIN, UNIT_MAX, &m);
        let half_diagonal = 0.5 * std::f32::consts::SQRT_2;
        assert!(close(max[0], half_diagonal), "max.x={}", max[0]);
        assert!(close(min[0], -half_diagonal));
        // Y は回転軸なので変わらない
        assert!(close(max[1], 0.5) && close(min[1], -0.5));
    }

    #[test]
    fn transform_aabb_scales() {
        let mut m = [[0.0f32; 4]; 4];
        m[0][0] = 2.0; m[1][1] = 3.0; m[2][2] = 4.0; m[3][3] = 1.0;
        let (min, max) = transform_aabb(UNIT_MIN, UNIT_MAX, &m);
        assert!(close(max[0], 1.0) && close(max[1], 1.5) && close(max[2], 2.0));
        assert!(close(min[0], -1.0) && close(min[1], -1.5) && close(min[2], -2.0));
    }

    #[test]
    fn merge_aabb_takes_the_outer_bounds() {
        let a = ([0.0, 0.0, 0.0], [1.0, 1.0, 1.0]);
        let b = ([-2.0, 0.5, -0.5], [0.5, 3.0, 0.5]);
        let (min, max) = merge_aabb(a, b);
        assert_eq!(min, [-2.0, 0.0, -0.5]);
        assert_eq!(max, [1.0, 3.0, 1.0]);
    }

    // ─── ID マスク → アルファ ────────────────────────────────

    #[test]
    fn id_mask_makes_background_transparent_and_subject_opaque() {
        // 4 ピクセル: 背景, ID=1, 背景, ID=42
        let mut rgba = vec![
            10, 20, 30, 99, //
            40, 50, 60, 99, //
            70, 80, 90, 99, //
            11, 22, 33, 99,
        ];
        let id_alpha = [
            f32::from_bits(0),
            f32::from_bits(1),
            f32::from_bits(0),
            f32::from_bits(42),
        ];
        let opaque = apply_id_mask_alpha(&mut rgba, &id_alpha);
        assert_eq!(opaque, 2);
        assert_eq!(rgba[3], 0, "背景は透明");
        assert_eq!(rgba[7], 255, "被写体は不透明");
        assert_eq!(rgba[11], 0, "背景は透明");
        assert_eq!(rgba[15], 255, "被写体は不透明");
        // RGB は触らない
        assert_eq!(&rgba[0..3], &[10, 20, 30]);
        assert_eq!(&rgba[4..7], &[40, 50, 60]);
    }

    #[test]
    fn id_mask_treats_negative_zero_as_a_subject() {
        // -0.0 は数値としては 0 だがビットパターンは 0 ではない。
        // ID として 0x80000000 が割り当てられたインスタンスを背景と誤判定しないこと。
        let mut rgba = vec![1, 2, 3, 4];
        let opaque = apply_id_mask_alpha(&mut rgba, &[-0.0f32]);
        assert_eq!(opaque, 1);
        assert_eq!(rgba[3], 255);
    }

    #[test]
    fn id_mask_reports_zero_when_nothing_was_drawn() {
        let mut rgba = vec![0; 4 * 4];
        let opaque = apply_id_mask_alpha(&mut rgba, &[f32::from_bits(0); 4]);
        assert_eq!(opaque, 0, "何も写っていなければ 0 を返して呼び出し側が失敗にできる");
    }

    #[test]
    fn id_mask_handles_mismatched_lengths_without_panicking() {
        // ID 側が短い
        let mut rgba = vec![0u8; 4 * 4];
        assert_eq!(apply_id_mask_alpha(&mut rgba, &[f32::from_bits(7)]), 1);
        // RGBA 側が短い
        let mut rgba = vec![0u8; 4];
        assert_eq!(apply_id_mask_alpha(&mut rgba, &[f32::from_bits(7); 8]), 1);
    }

    // ─── 切り出し ─────────────────────────────────────────────

    #[test]
    fn crop_extracts_the_requested_rectangle() {
        // 4x2 の画像。各ピクセルの R に通し番号を入れる。
        let mut src = Vec::new();
        for i in 0..8u8 {
            src.extend_from_slice(&[i, 0, 0, 255]);
        }
        // 右上 2x2（x=2, y=0）
        let (out, w, h) = crop_rgba(&src, 4, 2, CropRect { x: 2, y: 0, side: 2 });
        assert_eq!((w, h), (2, 2));
        assert_eq!(out[0], 2);
        assert_eq!(out[4], 3);
        assert_eq!(out[8], 6);
        assert_eq!(out[12], 7);
    }

    #[test]
    fn crop_clamps_out_of_range_rectangles() {
        let src = vec![0u8; 4 * 4 * 4];
        let (out, w, h) = crop_rgba(&src, 4, 4, CropRect { x: 3, y: 3, side: 10 });
        assert_eq!((w, h), (1, 1));
        assert_eq!(out.len(), 4);
    }

    // ─── 画面右／上ベクトル ──────────────────────────────────

    #[test]
    fn view_right_and_up_match_the_documented_screen_axes() {
        // ドキュメントの「画面右＝…」と実際の外積が食い違わないこと。
        // ここがずれると、追い込みのパンが逆方向へ効いて発散する。
        assert_eq!(ThumbnailView::Side.right(), [0.0, 0.0, 1.0]);
        assert_eq!(ThumbnailView::Side.up(), [0.0, 1.0, 0.0]);

        assert_eq!(ThumbnailView::Front.right(), [-1.0, 0.0, 0.0]);
        assert_eq!(ThumbnailView::Front.up(), [0.0, 1.0, 0.0]);

        assert_eq!(ThumbnailView::Top.right(), [1.0, 0.0, 0.0]);
        assert_eq!(ThumbnailView::Top.up(), [0.0, 0.0, 1.0]);
    }

    // ─── マスクの実測 ────────────────────────────────────────

    /// テスト用: 幅 w・高さ h のマスクを作り、矩形内だけ ID を立てる。
    fn mask_with_rect(w: u32, h: u32, x0: u32, y0: u32, x1: u32, y1: u32) -> Vec<f32> {
        let mut mask = vec![f32::from_bits(0); (w * h) as usize];
        for y in y0..=y1 {
            for x in x0..=x1 {
                mask[(y * w + x) as usize] = f32::from_bits(7);
            }
        }
        mask
    }

    #[test]
    fn measure_mask_bounds_finds_the_rectangle() {
        let mask = mask_with_rect(10, 8, 2, 3, 5, 6);
        let b = measure_mask_bounds(&mask, 10, 8).expect("矩形が見つかるはず");
        assert_eq!(b, MaskBounds { min_x: 2, min_y: 3, max_x: 5, max_y: 6 });
        assert_eq!(b.width(), 4);
        assert_eq!(b.height(), 4);
    }

    #[test]
    fn measure_mask_bounds_returns_none_for_an_empty_mask() {
        let mask = vec![f32::from_bits(0); 4 * 4];
        assert!(measure_mask_bounds(&mask, 4, 4).is_none());
    }

    #[test]
    fn measure_mask_bounds_finds_single_pixels_at_the_corners() {
        let mask = mask_with_rect(4, 4, 3, 3, 3, 3);
        let b = measure_mask_bounds(&mask, 4, 4).expect("1 ピクセルでも見つかるはず");
        assert_eq!(b, MaskBounds { min_x: 3, min_y: 3, max_x: 3, max_y: 3 });
        assert_eq!((b.width(), b.height()), (1, 1));
    }

    // ─── 構図の追い込み ──────────────────────────────────────

    #[test]
    fn refine_accepts_a_well_framed_subject() {
        // 正方形ビューポート 100x100、被写体が中央で余白 10% ぶんほぼぴったり。
        // 被写体の半径 45px に対し、半高が 45×1.1 = 49.5px 相当なら適正。
        let viewport = 100u32;
        let crop = center_square_crop(viewport, viewport);
        let half_h = 0.495; // 1px = 0.0099 ワールド → 半高 49.5px 相当
        let bounds = MaskBounds { min_x: 5, min_y: 5, max_x: 94, max_y: 94 };
        match refine_framing(bounds, crop, viewport, viewport, half_h, 1.1) {
            RefineDecision::Accept => {}
            other => panic!("受理されるはず: {other:?}"),
        }
    }

    #[test]
    fn refine_zooms_in_on_a_subject_that_is_far_too_small() {
        // 被写体が中央にあるが 10px しかない → 大きく寄る指示が出るはず
        let viewport = 100u32;
        let crop = center_square_crop(viewport, viewport);
        let half_h = 1.0;
        let bounds = MaskBounds { min_x: 45, min_y: 45, max_x: 54, max_y: 54 };
        match refine_framing(bounds, crop, viewport, viewport, half_h, 1.1) {
            RefineDecision::Retry { ortho_half_h, pan_right, pan_up } => {
                assert!(ortho_half_h < half_h, "寄る方向のはず: {ortho_half_h}");
                assert!(pan_right.abs() < 1e-4 && pan_up.abs() < 1e-4, "中央なのでパンは 0");
            }
            other => panic!("撮り直しになるはず: {other:?}"),
        }
    }

    #[test]
    fn refine_pans_towards_an_off_center_subject() {
        // 被写体が画面の右下に寄っている → カメラを右・下へ動かす指示
        let viewport = 100u32;
        let crop = center_square_crop(viewport, viewport);
        let bounds = MaskBounds { min_x: 70, min_y: 70, max_x: 79, max_y: 79 };
        match refine_framing(bounds, crop, viewport, viewport, 1.0, 1.1) {
            RefineDecision::Retry { pan_right, pan_up, .. } => {
                assert!(pan_right > 0.0, "右へ寄っているのでカメラも右へ: {pan_right}");
                assert!(pan_up < 0.0, "下へ寄っているのでカメラも下へ: {pan_up}");
            }
            other => panic!("撮り直しになるはず: {other:?}"),
        }
    }

    #[test]
    fn refine_never_zooms_in_while_the_subject_is_clipped() {
        // 被写体が左端に接している＝見切れている疑い。
        // 測った幅は下限でしかないので、ここで寄るとさらに見切れる。
        let viewport = 100u32;
        let crop = center_square_crop(viewport, viewport);
        let half_h = 1.0;
        let bounds = MaskBounds { min_x: 0, min_y: 45, max_x: 20, max_y: 54 };
        match refine_framing(bounds, crop, viewport, viewport, half_h, 1.1) {
            RefineDecision::Retry { ortho_half_h, pan_right, .. } => {
                assert!(
                    ortho_half_h >= half_h,
                    "見切れ中は寄ってはいけない: {ortho_half_h} < {half_h}"
                );
                assert!(pan_right < 0.0, "左に寄っているのでカメラも左へ: {pan_right}");
            }
            other => panic!("見切れているので必ず撮り直し: {other:?}"),
        }
    }

    #[test]
    fn refine_never_accepts_a_clipped_subject() {
        // 端に接していれば、中央・適正サイズに見えても受理しない
        let viewport = 100u32;
        let crop = center_square_crop(viewport, viewport);
        let bounds = MaskBounds { min_x: 0, min_y: 0, max_x: 99, max_y: 99 };
        assert!(matches!(
            refine_framing(bounds, crop, viewport, viewport, 0.55, 1.1),
            RefineDecision::Retry { .. }
        ));
    }

    #[test]
    fn refine_converges_for_a_badly_over_estimated_aabb() {
        // 伊勢海老のケースを模す: AABB が実体より大幅に大きく、被写体が隅に小さく写る。
        // 追い込みを数回まわすと「中央・適正サイズ」へ収束すること。
        let viewport = 512u32;
        let crop = center_square_crop(viewport, viewport);
        let margin = 1.1;

        // 真の被写体（カメラ初期位置から見た相対位置と半径・ワールド単位）
        let true_center = (0.30f32, -0.10f32);
        let true_half = 0.50f32;

        let mut half_h = 2.0f32; // 過大な AABB 由来の初期値
        let mut cam = (0.0f32, 0.0f32); // カメラの (right, up) オフセット

        for pass in 0..8 {
            // 現在の構図で被写体がどこに写るかを求める
            let world_per_pixel = 2.0 * half_h / viewport as f32;
            let center_px = viewport as f32 * 0.5;
            let cx = center_px + (true_center.0 - cam.0) / world_per_pixel;
            let cy = center_px - (true_center.1 - cam.1) / world_per_pixel;
            let half_px = true_half / world_per_pixel;

            let min_x = (cx - half_px).round().clamp(0.0, viewport as f32 - 1.0) as u32;
            let max_x = (cx + half_px).round().clamp(0.0, viewport as f32 - 1.0) as u32;
            let min_y = (cy - half_px).round().clamp(0.0, viewport as f32 - 1.0) as u32;
            let max_y = (cy + half_px).round().clamp(0.0, viewport as f32 - 1.0) as u32;
            let bounds = MaskBounds { min_x, min_y, max_x, max_y };

            match refine_framing(bounds, crop, viewport, viewport, half_h, margin) {
                RefineDecision::Accept => {
                    // 収束した。被写体が枠の 8 割以上を占めていること。
                    let fill = bounds.width() as f32 / crop.side as f32;
                    assert!(fill > 0.8, "収束時の占有率が低い: {fill} (pass {pass})");
                    return;
                }
                RefineDecision::Retry { ortho_half_h, pan_right, pan_up } => {
                    half_h = ortho_half_h;
                    cam = (cam.0 + pan_right, cam.1 + pan_up);
                }
            }
        }
        panic!("8 回の追い込みで収束しなかった（half_h={half_h}, cam={cam:?}）");
    }

    // ─── PNG 書き出し（往復）─────────────────────────────────

    #[test]
    fn write_thumbnail_png_roundtrips_with_alpha_preserved() {
        // 中央 2x2 だけ不透明な 4x4 を 4x4 のまま書き出して読み直す
        let size = 4u32;
        let mut rgba = vec![0u8; (size * size * 4) as usize];
        for y in 1..3u32 {
            for x in 1..3u32 {
                let base = ((y * size + x) * 4) as usize;
                rgba[base] = 200;
                rgba[base + 1] = 30;
                rgba[base + 2] = 40;
                rgba[base + 3] = 255;
            }
        }

        let dir = std::env::temp_dir().join("seed_thumb_test");
        let path = dir.join("roundtrip.png");
        let _ = std::fs::remove_file(&path);

        write_thumbnail_png(&mut rgba, size, size, &path).expect("書き出せるはず");

        let loaded = image::open(&path).expect("読み直せるはず").to_rgba8();
        assert_eq!(loaded.dimensions(), (size, size));
        // 四隅は透明のまま
        assert_eq!(loaded.get_pixel(0, 0)[3], 0);
        assert_eq!(loaded.get_pixel(size - 1, size - 1)[3], 0);
        // 中央は不透明で色が保たれている
        let center = loaded.get_pixel(1, 1);
        assert_eq!(center[3], 255);
        assert_eq!(center[0], 200);

        let _ = std::fs::remove_file(&path);
    }

    #[test]
    fn write_thumbnail_png_downscales_and_creates_directories() {
        let src_size = 8u32;
        let out_size = 4u32;
        let mut rgba = vec![0u8; (src_size * src_size * 4) as usize];
        // 左半分を不透明にする
        for y in 0..src_size {
            for x in 0..(src_size / 2) {
                let base = ((y * src_size + x) * 4) as usize;
                rgba[base + 3] = 255;
            }
        }

        // 存在しない入れ子ディレクトリへ書けること
        let dir = std::env::temp_dir().join("seed_thumb_test").join("nested").join("deep");
        let _ = std::fs::remove_dir_all(&dir);
        let path = dir.join("scaled.png");

        write_thumbnail_png(&mut rgba, src_size, out_size, &path).expect("書き出せるはず");
        let loaded = image::open(&path).expect("読み直せるはず").to_rgba8();
        assert_eq!(loaded.dimensions(), (out_size, out_size));

        let _ = std::fs::remove_dir_all(std::env::temp_dir().join("seed_thumb_test"));
    }
}
