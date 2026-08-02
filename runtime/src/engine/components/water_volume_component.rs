// ============================================================
//  water_volume_component.rs — 水ボリュームコンポーネント
//
//  Actor に「水」を持たせる ECS スロットコンポーネント。
//  本コンポーネントは水の形状定義（種別・水面高さ・範囲）と見た目パラメータ
//  （色・フォーム・波・フレネル・屈折）の *データのみ* を保持する
//  （ECS 理念：データとロジックの分離）。
//
//  【位置の解決】
//  水面のワールド位置はアクターの Transform から解決する（本コンポーネント
//  自身は位置を持たない）。ワールド空間への解決は engine::water::collect が
//  行い、描画・問い合わせの双方はその中間表現 ResolvedWaterVolume だけを見る。
//
//  【種別ごとの意味の差】
//    Ocean  … XZ 無限の大洋。surface_height は「ワールド Y 絶対値」。
//    Region … 直方体の水塊。surface_height は「アクタ原点からの相対 Y」。
//    Spline … 川（W4）。`spline_points`（アクタ相対の制御点列）を Catmull-Rom で
//             補間した曲線に沿って、幅 `river_width` のリボン水面を張る。
//             水面 Y は制御点の Y を補間したもの（＝川は下る）。
//             **制御点が 2 点未満なら描画・問い合わせとも無効。**
//
//  【流速について】
//  流速を持つのは Spline（川）だけである（`flow_speed`）。
//  `WaterQuery::flow_at` は川の接線方向 × `flow_speed` を返し、
//  Ocean / Region では常にゼロを返す。
//
//  【シリアライズ】
//  全フィールドに #[serde(default)]（非ゼロ既定は default fn）を付け、
//  旧 .scene（フィールド欠落）でも読み込みが失敗しないようにする。
// ============================================================

use serde::{Deserialize, Serialize};

use crate::engine::ecs::Component;

// ─── デフォルト値関数 ─────────────────────────────────────────
// マジックナンバー禁止のため、非ゼロ既定値はすべて関数に切り出す。

/// region_half_extents の既定値（ローカル AABB 半径：横 10m・縦 5m・奥 10m）。
fn default_region_half_extents() -> [f32; 3] { [10.0, 5.0, 10.0] }
/// ocean_extent の既定値（大洋クアッドの片側半径 2km）。
fn default_ocean_extent() -> f32 { 2000.0 }
/// shallow_color の既定値（浅場の緑がかった水色・リニア）。
fn default_shallow_color() -> [f32; 3] { [0.10, 0.45, 0.42] }
/// deep_color の既定値（深場の濃紺・リニア）。
fn default_deep_color() -> [f32; 3] { [0.01, 0.06, 0.12] }
/// absorption_distance の既定値（8m 進むと深場の色へほぼ収束する）。
fn default_absorption_distance() -> f32 { 8.0 }
/// surface_opacity の既定値（深場での最大不透明度）。
fn default_surface_opacity() -> f32 { 0.92 }
/// foam_color の既定値（白）。
fn default_foam_color() -> [f32; 3] { [1.0, 1.0, 1.0] }
/// foam_width の既定値（この水深より浅い所にフォームが出る）。
fn default_foam_width() -> f32 { 0.35 }
/// foam_intensity の既定値（フォームの濃さ）。
fn default_foam_intensity() -> f32 { 0.8 }
/// wave_amplitude の既定値（法線摂動の強さ。形状は変えず陰影だけ揺らす）。
fn default_wave_amplitude() -> f32 { 0.06 }
/// wave_scale の既定値（波の空間周波数 1/m）。
fn default_wave_scale() -> f32 { 0.12 }
/// wave_speed の既定値（波のスクロール速度）。
fn default_wave_speed() -> f32 { 0.6 }
// wave_direction_deg の既定値は 0.0（＝+Z 方向へ進む）なので、
// serde の型既定（f32 = 0.0）で足りる。専用の default 関数は置かない。
/// fresnel_power の既定値（Schlick 近似の指数）。
fn default_fresnel_power() -> f32 { 5.0 }
/// fresnel_strength の既定値（フレネル反射の寄与率）。
fn default_fresnel_strength() -> f32 { 1.0 }
/// reflection_color の既定値（浅い角度で映る空の簡易色・リニア）。
fn default_reflection_color() -> [f32; 3] { [0.35, 0.50, 0.62] }
/// refraction_distortion の既定値（屈折 UV の最大歪み。画面比）。
fn default_refraction_distortion() -> f32 { 0.03 }
/// ripple_strength の既定値（波紋の法線摂動スケール。1.0 = 標準）。
///
/// インタラクションフィールドの波高勾配を水面法線へ足す際の倍率。
/// 0 にすると波紋・航跡の表示だけを切れる（場の計算自体は他の消費者と共有のため止まらない）。
fn default_ripple_strength() -> f32 { 1.0 }
/// ripple_foam_threshold の既定値（この波高（m 相当）を超えた所に航跡の泡が出る）。
///
/// 歩行が立てる波（振幅 0.03 前後）では泡が出ず、走り・飛び込みで出る値。
fn default_ripple_foam_threshold() -> f32 { 0.05 }

// ─── 水中コースティクス（Phase W5.3）の既定値 ─────────────────
//
// コースティクス（集光模様）は「水面の高さ場のラプラシアン × 水深」から作る
// 描画専用パラメータで、挙動（浮力・流れ・問い合わせ）には一切影響しない。
// 既定は「水域を置けばそのまま水底に網目が走る」値にしてある。
// **描画専用なのでインスペクタ UI・スクリプト API へは公開しない**
//（見た目の微調整はシーン JSON を直接触るか、将来 UI を足すときにまとめて行う）。

/// caustics_intensity の既定値（集光模様の強さ）。**0 で完全無効**。
///
/// 直達光に対する最大増幅率は `強度 × CAUSTICS_OUTPUT_GAIN`（caustics.wgsl）で、
/// 0.6 は「水底が明らかに揺らいで見えるが白飛びしない」ところ。
fn default_caustics_intensity() -> f32 { 0.6 }
/// caustics_scale の既定値（模様の細かさ倍率。1.0 = 標準）。
///
/// 大きいほど差分ステップが小さくなり、高周波成分＝細かい網目を拾う。
fn default_caustics_scale() -> f32 { 1.0 }
/// caustics_depth_fade の既定値（水面からこの距離 m で模様がほぼ消える）。
///
/// 実際の水中でも集光は数 m で拡散して消えるため、深い水域の底まで
/// 網目が届き続けないようにする指数フェードの距離定数。
fn default_caustics_depth_fade() -> f32 { 6.0 }

// ─── 岸波（ショアフィールド。Phase W1.5）の既定値 ────────────
//
// 既定は「Ocean に付ければそのまま浜へ寄せる波が出る」値にしてある。
// 岸波は地形の水深から作られるため、地形の無いシーン・水深が深いだけの水域では
// これらの値でも一切現れない（＝既定が有効でも見た目が壊れることはない）。

/// shore_wave_strength の既定値（1.0 = 標準）。**0 で完全無効（W1 と同一出力）**。
fn default_shore_wave_strength() -> f32 { 1.0 }
/// shore_wave_length の既定値（うねりの波長 m）。
///
/// 浜へ寄せるうねりとして自然に見える長さ。短くすると細かいさざ波、
/// 長くすると外洋の大きなうねりになる。
fn default_shore_wave_length() -> f32 { 12.0 }
/// shore_wave_period の既定値（うねりが 1 波長進む周期 秒）。
///
/// 波長 12m / 周期 4s ＝ 位相速度 3m/s。実際の浜のうねりに近い速さ。
fn default_shore_wave_period() -> f32 { 4.0 }
/// shore_wave_foam の既定値（砕け波・打ち上げの泡量 0..1）。
fn default_shore_wave_foam() -> f32 { 0.8 }

// ─── 川（スプライン。Phase W4）の既定値 ──────────────────────
//
// 川は kind = Spline のときだけ使われる。制御点が 2 点未満なら
// 描画・問い合わせとも完全に無効（既定は空なので、Spline に切り替えた
// 直後は「まだ何も描かれない」＝壊れた見た目にならない）。

/// river_width の既定値（川幅 m。リボンの全幅であって半幅ではない）。
fn default_river_width() -> f32 { 4.0 }
/// flow_speed の既定値（流速 m/s。ゆるやかな小川程度の速さ）。
fn default_flow_speed() -> f32 { 1.5 }
/// river_depth の既定値（川の深さ m。水面からこの深さまでが「川の中」）。
fn default_river_depth() -> f32 { 2.0 }
/// river_segment_length の既定値（曲線長 何 m ごとに 1 分割するか）。
///
/// 従来の固定値（`RIVER_SAMPLE_STEP_M`）と同じ 2.0 にしてあるので、
/// 旧 .scene を読み込んでも川の形は 1 頂点も変わらない。
fn default_river_segment_length() -> f32 { 2.0 }

// ─── WaterVolumeKind ─────────────────────────────────────────

/// 水ボリュームの種別。
///
/// - `Ocean`  : XZ 方向に無限の大洋。水面は `surface_height`（ワールド Y 絶対値）に張る。
///              描画クアッドはカメラに追従し、片側半径 `ocean_extent` で作られる。
/// - `Region` : 直方体（軸平行 AABB）で区切った水塊。既定。
///              水面 Y は「アクタ原点 + `surface_height`」で決まる。
/// - `Spline` : 川（スプライン水路。W4）。制御点列 `spline_points` を Catmull-Rom で
///              補間した曲線に沿って、幅 `river_width` のリボン水面を張る。
///              **制御点が 2 点未満なら描画・問い合わせとも無効。**
///
/// serde は文字列（`"Ocean"` / `"Region"` / `"Spline"`）でシリアライズする。
/// 旧シーン（kind 欠落）は `#[serde(default)]` により Region になる。
#[derive(Clone, Copy, PartialEq, Eq, Serialize, Deserialize, Debug)]
pub enum WaterVolumeKind {
    /// XZ 無限の大洋（水面 Y はワールド絶対値）
    Ocean,
    /// 直方体で区切った水塊（既定）
    Region,
    /// 川スプライン（W4）。制御点列に沿ったリボン水面。流速を持つ唯一の種別。
    Spline,
}

impl Default for WaterVolumeKind {
    /// kind 省略時の既定は Region（局所的な水たまり／プールが最も一般的なため）。
    fn default() -> Self { WaterVolumeKind::Region }
}

impl WaterVolumeKind {
    /// インスペクタへ送る種別文字列（C# 側ドロップダウンの Tag と一致させる）。
    pub fn as_str(self) -> &'static str {
        match self {
            WaterVolumeKind::Ocean  => "Ocean",
            WaterVolumeKind::Region => "Region",
            WaterVolumeKind::Spline => "Spline",
        }
    }

    /// IPC 文字列から種別へ変換する。未知の文字列は None（呼び出し側で無視する）。
    pub fn from_str_opt(s: &str) -> Option<Self> {
        match s {
            "Ocean"  => Some(WaterVolumeKind::Ocean),
            "Region" => Some(WaterVolumeKind::Region),
            "Spline" => Some(WaterVolumeKind::Spline),
            _        => None,
        }
    }
}

// ─── WaterVolumeComponentData（シリアライズ用）───────────────

/// WaterVolumeComponent のシリアライズ用データ（.scene 保存・Undo スナップショット）。
#[derive(Clone, Serialize, Deserialize, Debug)]
pub struct WaterVolumeComponentData {
    /// 水ボリュームの種別（既定 Region）
    #[serde(default)]
    pub kind: WaterVolumeKind,
    /// 水面高さ。**Ocean = ワールド Y 絶対値 / Region = アクタ原点からの相対 Y**。
    #[serde(default)]
    pub surface_height: f32,
    /// Region のローカル AABB 半径（アクタ位置が中心）。既定 [10, 5, 10]。
    #[serde(default = "default_region_half_extents")]
    pub region_half_extents: [f32; 3],
    /// Ocean 水面クアッドの片側半径（m）。カメラに追従して張られる。既定 2000。
    #[serde(default = "default_ocean_extent")]
    pub ocean_extent: f32,
    /// 浅場の色（リニア RGB）
    #[serde(default = "default_shallow_color")]
    pub shallow_color: [f32; 3],
    /// 深場の色（リニア RGB）
    #[serde(default = "default_deep_color")]
    pub deep_color: [f32; 3],
    /// 深色へ収束するまでの水中距離（m）。小さいほど濁って見える。
    #[serde(default = "default_absorption_distance")]
    pub absorption_distance: f32,
    /// 深場での最大不透明度（0..1）
    #[serde(default = "default_surface_opacity")]
    pub surface_opacity: f32,
    /// 岸フォームの色（リニア RGB）
    #[serde(default = "default_foam_color")]
    pub foam_color: [f32; 3],
    /// フォームが出る水深（m）。この深さより浅い所に白波が乗る。
    #[serde(default = "default_foam_width")]
    pub foam_width: f32,
    /// フォームの強度（0..1）
    #[serde(default = "default_foam_intensity")]
    pub foam_intensity: f32,
    /// 法線摂動の強さ（形状は変えず陰影のみ揺らす）
    #[serde(default = "default_wave_amplitude")]
    pub wave_amplitude: f32,
    /// 波の空間周波数（1/m）。大きいほど細かい波になる。
    #[serde(default = "default_wave_scale")]
    pub wave_scale: f32,
    /// 波のスクロール速度
    #[serde(default = "default_wave_speed")]
    pub wave_speed: f32,
    /// 解析波（通常の波）の進行方位角（度。Phase W6.3）。
    ///
    /// ## 規約
    /// ワールド XZ 平面上の方位角。**0 = +Z 方向へ進む**／
    /// **正の角度で +X 側へ回る**（＝上から見て時計回り。90° で +X、180° で −Z）。
    /// 内部の 6 層は**この角度でまとめて剛体回転**するだけで、
    /// 層ごとの方向の散らばり（タイル感対策）はそのまま保たれる。
    ///
    /// ## 川との関係
    /// 川（Spline）では水面模様が別途「流れ」で下流へ移流されるが、
    /// 移流はサンプル座標の平行移動、この角度は波の伝播方向であり**互いに独立**である。
    /// 両者は衝突しない（流れに直交する波は流されても位相が変わらない、
    /// という物理的に妥当な見え方になる）。
    ///
    /// 既定 0.0。範囲の制限は設けない（360 を超える値も剰余として自然に働く）。
    #[serde(default)]
    pub wave_direction_deg: f32,
    /// フレネル指数（Schlick 近似の累乗。大きいほど正面が透ける）
    #[serde(default = "default_fresnel_power")]
    pub fresnel_power: f32,
    /// フレネル反射の寄与率（0..1）
    #[serde(default = "default_fresnel_strength")]
    pub fresnel_strength: f32,
    /// 浅い角度で映る簡易反射色（リニア RGB）
    #[serde(default = "default_reflection_color")]
    pub reflection_color: [f32; 3],
    /// 屈折 UV の最大歪み（画面比）
    #[serde(default = "default_refraction_distortion")]
    pub refraction_distortion: f32,
    /// 波紋・航跡（インタラクションフィールド）の法線摂動スケール（Phase I2）
    #[serde(default = "default_ripple_strength")]
    pub ripple_strength: f32,
    /// 波紋フォームが出る波高しきい値（m 相当。Phase I2）
    #[serde(default = "default_ripple_foam_threshold")]
    pub ripple_foam_threshold: f32,
    /// 水中コースティクス（集光模様）の強さ。**0 で完全無効**（Phase W5.3）
    #[serde(default = "default_caustics_intensity")]
    pub caustics_intensity: f32,
    /// コースティクスの細かさ倍率（大きいほど細かい網目。Phase W5.3）
    #[serde(default = "default_caustics_scale")]
    pub caustics_scale: f32,
    /// 水面からこの距離（m）進むとコースティクスがほぼ消える（Phase W5.3）
    #[serde(default = "default_caustics_depth_fade")]
    pub caustics_depth_fade: f32,
    /// 岸波（ショアフィールド）の強さ。**0 で完全無効**（Phase W1.5）
    #[serde(default = "default_shore_wave_strength")]
    pub shore_wave_strength: f32,
    /// 岸へ寄せるうねりの波長（m。Phase W1.5）
    #[serde(default = "default_shore_wave_length")]
    pub shore_wave_length: f32,
    /// 岸へ寄せるうねりの周期（秒。Phase W1.5）
    #[serde(default = "default_shore_wave_period")]
    pub shore_wave_period: f32,
    /// 砕け波・打ち上げの泡量（0..1。Phase W1.5）
    #[serde(default = "default_shore_wave_foam")]
    pub shore_wave_foam: f32,
    /// 川の制御点列（**アクタ相対**のローカル座標。Phase W4）。
    ///
    /// kind = Spline のときだけ使う。Catmull-Rom で滑らかに補間され、
    /// その周りに幅 `river_width` のリボン（川面）が張られる。
    /// **2 点未満なら川は成立しない**ものとして描画・問い合わせとも無効になる。
    #[serde(default)]
    pub spline_points: Vec<[f32; 3]>,
    /// 川幅（m。Phase W4）。スプラインに沿って一定。
    #[serde(default = "default_river_width")]
    pub river_width: f32,
    /// 流速（m/s。Phase W4）。`WaterQuery::flow_at` が返す速さであり、
    /// 同時に水面模様が下流へ流れる速さでもある（見た目と挙動が同じ値を見る）。
    #[serde(default = "default_flow_speed")]
    pub flow_speed: f32,
    /// 川の深さ（m。Phase W4）。水面からこの深さまでが「川の中」＝水中判定になる。
    ///
    /// Region の `region_half_extents.y` に相当するが、川は AABB を持たないため
    /// 独立したフィールドにしてある（AABB 半径を流用すると、Spline では
    /// インスペクタに出ない値が挙動を決める“隠れた結合”になる）。
    #[serde(default = "default_river_depth")]
    pub river_depth: f32,
    /// 川の折れ線 1 分割ぶんの目標長（m。Phase W4.1）。小さいほど川が滑らかになる。
    ///
    /// 分割数は「曲線長 / この値」で決まる。下限は
    /// `RIVER_SEGMENT_LENGTH_MIN`（0 や極小値で分割数が発散するのを防ぐ）。
    /// 総分割数の上限 `RIVER_MAX_SEGMENTS` は据え置きなので、
    /// **長い川ではこの値を小さくしても上限で頭打ちになり、自動的に粗くなる**。
    #[serde(default = "default_river_segment_length")]
    pub river_segment_length: f32,
    /// 川の制御点を借りてくる**参照先アクタ名**（Phase W4.1）。
    ///
    /// 空文字列 = 参照なし。このとき川は従来どおり `spline_points` から組まれる。
    /// 非空なら、その名前のアクタが持つ **0 番目の `ControlPointComponent`** を
    /// 川の点列として使い、`spline_points` は完全に無視する
    /// （点列の出どころが 2 つ同時に効くと「見えている線と流れる線が違う」ため）。
    ///
    /// ## なぜ「同一アクタの自動優先」をやめたのか
    /// 以前は同じアクタに ControlPoint があれば黙って優先していたが、
    /// 「どちらが効いているのか」がユーザーから見えず、コンポーネントを付けただけで
    /// 川の形が変わる挙動になっていた。参照を明示させれば結線が UI に出る。
    ///
    /// ## 参照先は別アクタでもよい
    /// 別アクタを指した場合、点列のワールド解決には**参照先アクタの Transform**を使う
    /// （＝制御点を持つアクタを動かすと川が動く。水アクタを動かしても川は動かない）。
    ///
    /// ## 同名アクタ・複数スロット
    /// 同名アクタが複数ある場合は DFS で最初に見つかったもの、
    /// 1 アクタに ControlPoint スロットが複数ある場合は 0 番目を使う。
    #[serde(default)]
    pub control_point_ref: String,
}

impl Default for WaterVolumeComponentData {
    fn default() -> Self {
        Self {
            kind:                  WaterVolumeKind::default(),
            surface_height:        0.0,
            region_half_extents:   default_region_half_extents(),
            ocean_extent:          default_ocean_extent(),
            shallow_color:         default_shallow_color(),
            deep_color:            default_deep_color(),
            absorption_distance:   default_absorption_distance(),
            surface_opacity:       default_surface_opacity(),
            foam_color:            default_foam_color(),
            foam_width:            default_foam_width(),
            foam_intensity:        default_foam_intensity(),
            wave_amplitude:        default_wave_amplitude(),
            wave_scale:            default_wave_scale(),
            wave_speed:            default_wave_speed(),
            // 方位角の既定は 0 度（＝+Z 方向へ進む）。
            wave_direction_deg:    0.0,
            fresnel_power:         default_fresnel_power(),
            fresnel_strength:      default_fresnel_strength(),
            reflection_color:      default_reflection_color(),
            refraction_distortion: default_refraction_distortion(),
            ripple_strength:       default_ripple_strength(),
            ripple_foam_threshold: default_ripple_foam_threshold(),
            // 水中コースティクス（Phase W5.3。描画専用パラメータ）。
            caustics_intensity:    default_caustics_intensity(),
            caustics_scale:        default_caustics_scale(),
            caustics_depth_fade:   default_caustics_depth_fade(),
            shore_wave_strength:   default_shore_wave_strength(),
            shore_wave_length:     default_shore_wave_length(),
            shore_wave_period:     default_shore_wave_period(),
            shore_wave_foam:       default_shore_wave_foam(),
            // 川の制御点は既定で空（＝Spline に切り替えただけでは何も描かれない）。
            spline_points:         Vec::new(),
            river_width:           default_river_width(),
            flow_speed:            default_flow_speed(),
            river_depth:           default_river_depth(),
            river_segment_length:  default_river_segment_length(),
            // 参照は既定で空（＝spline_points 経路。既存シーンの挙動そのまま）。
            control_point_ref:     String::new(),
        }
    }
}

// ─── WaterVolumeComponent（ECS 実体）─────────────────────────

/// 水ボリュームコンポーネント（ECS 実体）。
///
/// 保持するのは水の形状定義と見た目パラメータのみ。ワールド位置は Actor の
/// Transform から engine::water::collect が毎フレーム解決する。
/// 揮発状態は持たない（Data と同一構成）。
#[derive(Clone, Debug)]
pub struct WaterVolumeComponent {
    /// 水ボリュームの種別
    pub kind: WaterVolumeKind,
    /// 水面高さ（Ocean = ワールド Y 絶対値 / Region = アクタ原点からの相対 Y）
    pub surface_height: f32,
    /// Region のローカル AABB 半径（アクタ位置が中心）
    pub region_half_extents: [f32; 3],
    /// Ocean 水面クアッドの片側半径（m）
    pub ocean_extent: f32,
    /// 浅場の色（リニア RGB）
    pub shallow_color: [f32; 3],
    /// 深場の色（リニア RGB）
    pub deep_color: [f32; 3],
    /// 深色へ収束するまでの水中距離（m）
    pub absorption_distance: f32,
    /// 深場での最大不透明度（0..1）
    pub surface_opacity: f32,
    /// 岸フォームの色（リニア RGB）
    pub foam_color: [f32; 3],
    /// フォームが出る水深（m）
    pub foam_width: f32,
    /// フォームの強度（0..1）
    pub foam_intensity: f32,
    /// 法線摂動の強さ
    pub wave_amplitude: f32,
    /// 波の空間周波数（1/m）
    pub wave_scale: f32,
    /// 波のスクロール速度
    pub wave_speed: f32,
    /// 解析波の進行方位角（度。0 = +Z、正で +X 側へ回る。Phase W6.3）
    pub wave_direction_deg: f32,
    /// フレネル指数
    pub fresnel_power: f32,
    /// フレネル反射の寄与率（0..1）
    pub fresnel_strength: f32,
    /// 浅い角度での簡易反射色（リニア RGB）
    pub reflection_color: [f32; 3],
    /// 屈折 UV の最大歪み（画面比）
    pub refraction_distortion: f32,
    /// 波紋・航跡の法線摂動スケール（Phase I2）
    pub ripple_strength: f32,
    /// 波紋フォームが出る波高しきい値（m 相当。Phase I2）
    pub ripple_foam_threshold: f32,
    /// 水中コースティクスの強さ（0 で完全無効。Phase W5.3）
    pub caustics_intensity: f32,
    /// コースティクスの細かさ倍率（Phase W5.3）
    pub caustics_scale: f32,
    /// コースティクスが消える水深（m。Phase W5.3）
    pub caustics_depth_fade: f32,
    /// 岸波の強さ（0 で完全無効。Phase W1.5）
    pub shore_wave_strength: f32,
    /// 岸へ寄せるうねりの波長（m。Phase W1.5）
    pub shore_wave_length: f32,
    /// 岸へ寄せるうねりの周期（秒。Phase W1.5）
    pub shore_wave_period: f32,
    /// 砕け波・打ち上げの泡量（0..1。Phase W1.5）
    pub shore_wave_foam: f32,
    /// 川の制御点列（アクタ相対のローカル座標。2 点未満は無効。Phase W4）
    pub spline_points: Vec<[f32; 3]>,
    /// 川幅（m。Phase W4）
    pub river_width: f32,
    /// 流速（m/s。Phase W4）
    pub flow_speed: f32,
    /// 川の深さ（m。Phase W4）
    pub river_depth: f32,
    /// 川の折れ線 1 分割ぶんの目標長（m。Phase W4.1）
    pub river_segment_length: f32,
    /// 川の制御点を借りる参照先アクタ名（空 = spline_points を使う。Phase W4.1）
    pub control_point_ref: String,
}

impl WaterVolumeComponent {
    /// シリアライズ用データからコンポーネントを構築する。
    pub fn from_data(data: WaterVolumeComponentData) -> Self {
        Self {
            kind:                  data.kind,
            surface_height:        data.surface_height,
            region_half_extents:   data.region_half_extents,
            ocean_extent:          data.ocean_extent,
            shallow_color:         data.shallow_color,
            deep_color:            data.deep_color,
            absorption_distance:   data.absorption_distance,
            surface_opacity:       data.surface_opacity,
            foam_color:            data.foam_color,
            foam_width:            data.foam_width,
            foam_intensity:        data.foam_intensity,
            wave_amplitude:        data.wave_amplitude,
            wave_scale:            data.wave_scale,
            wave_speed:            data.wave_speed,
            wave_direction_deg:    data.wave_direction_deg,
            fresnel_power:         data.fresnel_power,
            fresnel_strength:      data.fresnel_strength,
            reflection_color:      data.reflection_color,
            refraction_distortion: data.refraction_distortion,
            ripple_strength:       data.ripple_strength,
            ripple_foam_threshold: data.ripple_foam_threshold,
            caustics_intensity:    data.caustics_intensity,
            caustics_scale:        data.caustics_scale,
            caustics_depth_fade:   data.caustics_depth_fade,
            shore_wave_strength:   data.shore_wave_strength,
            shore_wave_length:     data.shore_wave_length,
            shore_wave_period:     data.shore_wave_period,
            shore_wave_foam:       data.shore_wave_foam,
            spline_points:         data.spline_points,
            river_width:           data.river_width,
            flow_speed:            data.flow_speed,
            river_depth:           data.river_depth,
            river_segment_length:  data.river_segment_length,
            control_point_ref:     data.control_point_ref,
        }
    }

    /// シリアライズ用データへ変換する。
    pub fn to_data(&self) -> WaterVolumeComponentData {
        WaterVolumeComponentData {
            kind:                  self.kind,
            surface_height:        self.surface_height,
            region_half_extents:   self.region_half_extents,
            ocean_extent:          self.ocean_extent,
            shallow_color:         self.shallow_color,
            deep_color:            self.deep_color,
            absorption_distance:   self.absorption_distance,
            surface_opacity:       self.surface_opacity,
            foam_color:            self.foam_color,
            foam_width:            self.foam_width,
            foam_intensity:        self.foam_intensity,
            wave_amplitude:        self.wave_amplitude,
            wave_scale:            self.wave_scale,
            wave_speed:            self.wave_speed,
            wave_direction_deg:    self.wave_direction_deg,
            fresnel_power:         self.fresnel_power,
            fresnel_strength:      self.fresnel_strength,
            reflection_color:      self.reflection_color,
            refraction_distortion: self.refraction_distortion,
            ripple_strength:       self.ripple_strength,
            ripple_foam_threshold: self.ripple_foam_threshold,
            caustics_intensity:    self.caustics_intensity,
            caustics_scale:        self.caustics_scale,
            caustics_depth_fade:   self.caustics_depth_fade,
            shore_wave_strength:   self.shore_wave_strength,
            shore_wave_length:     self.shore_wave_length,
            shore_wave_period:     self.shore_wave_period,
            shore_wave_foam:       self.shore_wave_foam,
            // 制御点列だけは所有権を渡せない（&self 受け）ため複製する。
            spline_points:         self.spline_points.clone(),
            river_width:           self.river_width,
            flow_speed:            self.flow_speed,
            river_depth:           self.river_depth,
            river_segment_length:  self.river_segment_length,
            // 参照名も所有権を渡せない（&self 受け）ため複製する。
            control_point_ref:     self.control_point_ref.clone(),
        }
    }
}

impl Default for WaterVolumeComponent {
    fn default() -> Self { Self::from_data(WaterVolumeComponentData::default()) }
}

impl Component for WaterVolumeComponent {}

// ─── ユニットテスト ──────────────────────────────────────────

#[cfg(test)]
mod tests {
    use super::*;

    /// kind の文字列表現が IPC 仕様（"Ocean"/"Region"/"Spline"）と一致すること。
    #[test]
    fn kind_str_roundtrip() {
        for k in [WaterVolumeKind::Ocean, WaterVolumeKind::Region, WaterVolumeKind::Spline] {
            assert_eq!(WaterVolumeKind::from_str_opt(k.as_str()), Some(k));
        }
        // 未知文字列は None（呼び出し側で無視される）
        assert_eq!(WaterVolumeKind::from_str_opt("river"), None);
    }

    /// kind 省略時の既定は Region であること。
    #[test]
    fn kind_default_is_region() {
        assert_eq!(WaterVolumeKind::default(), WaterVolumeKind::Region);
    }

    /// 空 JSON（全フィールド欠落＝旧 .scene 相当）でもデシリアライズでき、
    /// 既定値が入ること（serde(default) の付け漏れ検出）。
    #[test]
    fn deserializes_from_empty_json_with_defaults() {
        let d: WaterVolumeComponentData = serde_json::from_str("{}").expect("空 JSON を読めること");
        let def = WaterVolumeComponentData::default();
        assert_eq!(d.kind, def.kind);
        assert_eq!(d.surface_height, def.surface_height);
        assert_eq!(d.region_half_extents, def.region_half_extents);
        assert_eq!(d.ocean_extent, def.ocean_extent);
        assert_eq!(d.shallow_color, def.shallow_color);
        assert_eq!(d.deep_color, def.deep_color);
        assert_eq!(d.absorption_distance, def.absorption_distance);
        assert_eq!(d.surface_opacity, def.surface_opacity);
        assert_eq!(d.foam_color, def.foam_color);
        assert_eq!(d.foam_width, def.foam_width);
        assert_eq!(d.foam_intensity, def.foam_intensity);
        assert_eq!(d.wave_amplitude, def.wave_amplitude);
        assert_eq!(d.wave_scale, def.wave_scale);
        assert_eq!(d.wave_speed, def.wave_speed);
        // 方位角の既定は 0 度（旧 .scene にフィールドが無くても見た目が変わらない）。
        assert_eq!(d.wave_direction_deg, 0.0);
        assert_eq!(d.fresnel_power, def.fresnel_power);
        assert_eq!(d.fresnel_strength, def.fresnel_strength);
        assert_eq!(d.reflection_color, def.reflection_color);
        assert_eq!(d.refraction_distortion, def.refraction_distortion);
        assert_eq!(d.ripple_strength, def.ripple_strength);
        assert_eq!(d.ripple_foam_threshold, def.ripple_foam_threshold);
        // 水中コースティクス（Phase W5.3）。旧 .scene にフィールドが無くても
        // 既定値（0.6 / 1.0 / 6.0）が入り、読み込みが失敗しないこと。
        assert_eq!(d.caustics_intensity, def.caustics_intensity);
        assert_eq!(d.caustics_scale, def.caustics_scale);
        assert_eq!(d.caustics_depth_fade, def.caustics_depth_fade);
        assert_eq!(d.caustics_intensity, 0.6);
        assert_eq!(d.caustics_scale, 1.0);
        assert_eq!(d.caustics_depth_fade, 6.0);
        assert_eq!(d.shore_wave_strength, def.shore_wave_strength);
        assert_eq!(d.shore_wave_length, def.shore_wave_length);
        assert_eq!(d.shore_wave_period, def.shore_wave_period);
        assert_eq!(d.shore_wave_foam, def.shore_wave_foam);
        // 川（W4）: 制御点は空・幅と流速は既定値
        assert_eq!(d.spline_points, def.spline_points);
        assert_eq!(d.river_width, def.river_width);
        assert_eq!(d.flow_speed, def.flow_speed);
        assert_eq!(d.river_depth, def.river_depth);
        // W4.1: 分割長は従来固定値と同じ 2.0、参照は空（＝旧 .scene の見た目が変わらない）
        assert_eq!(d.river_segment_length, def.river_segment_length);
        assert_eq!(d.river_segment_length, 2.0, "旧シーンの川の形が変わらない既定値であること");
        assert_eq!(d.control_point_ref, def.control_point_ref);
        assert!(d.control_point_ref.is_empty(), "参照は既定で未設定");
    }

    /// kind は文字列としてシリアライズされること（C# 側の期待に合わせる）。
    #[test]
    fn kind_serializes_as_string() {
        let json = serde_json::to_string(&WaterVolumeKind::Ocean).unwrap();
        assert_eq!(json, "\"Ocean\"");
    }

    /// from_data / to_data が全フィールドを往復すること（写し漏れ検出）。
    #[test]
    fn data_roundtrip_preserves_all_fields() {
        let src = WaterVolumeComponentData {
            kind: WaterVolumeKind::Ocean,
            surface_height: 12.5,
            region_half_extents: [1.0, 2.0, 3.0],
            ocean_extent: 111.0,
            shallow_color: [0.1, 0.2, 0.3],
            deep_color: [0.4, 0.5, 0.6],
            absorption_distance: 4.5,
            surface_opacity: 0.5,
            foam_color: [0.7, 0.8, 0.9],
            foam_width: 1.25,
            foam_intensity: 0.25,
            wave_amplitude: 0.75,
            wave_scale: 0.33,
            wave_speed: 2.5,
            wave_direction_deg: 37.5,
            fresnel_power: 3.5,
            fresnel_strength: 0.6,
            reflection_color: [0.11, 0.22, 0.33],
            refraction_distortion: 0.07,
            ripple_strength: 1.5,
            ripple_foam_threshold: 0.2,
            caustics_intensity: 0.35,
            caustics_scale: 2.5,
            caustics_depth_fade: 9.5,
            shore_wave_strength: 0.75,
            shore_wave_length: 9.5,
            shore_wave_period: 3.25,
            shore_wave_foam: 0.4,
            spline_points: vec![[0.0, 0.0, 0.0], [1.0, -0.5, 4.0]],
            river_width: 6.5,
            flow_speed: 2.25,
            river_depth: 1.75,
            river_segment_length: 0.75,
            control_point_ref: "RiverPathActor".to_string(),
        };
        let back = WaterVolumeComponent::from_data(src.clone()).to_data();
        assert_eq!(back.kind, src.kind);
        assert_eq!(back.surface_height, src.surface_height);
        assert_eq!(back.region_half_extents, src.region_half_extents);
        assert_eq!(back.ocean_extent, src.ocean_extent);
        assert_eq!(back.shallow_color, src.shallow_color);
        assert_eq!(back.deep_color, src.deep_color);
        assert_eq!(back.absorption_distance, src.absorption_distance);
        assert_eq!(back.surface_opacity, src.surface_opacity);
        assert_eq!(back.foam_color, src.foam_color);
        assert_eq!(back.foam_width, src.foam_width);
        assert_eq!(back.foam_intensity, src.foam_intensity);
        assert_eq!(back.wave_amplitude, src.wave_amplitude);
        assert_eq!(back.wave_scale, src.wave_scale);
        assert_eq!(back.wave_speed, src.wave_speed);
        assert_eq!(back.wave_direction_deg, src.wave_direction_deg);
        assert_eq!(back.fresnel_power, src.fresnel_power);
        assert_eq!(back.fresnel_strength, src.fresnel_strength);
        assert_eq!(back.reflection_color, src.reflection_color);
        assert_eq!(back.refraction_distortion, src.refraction_distortion);
        assert_eq!(back.ripple_strength, src.ripple_strength);
        assert_eq!(back.ripple_foam_threshold, src.ripple_foam_threshold);
        assert_eq!(back.caustics_intensity, src.caustics_intensity);
        assert_eq!(back.caustics_scale, src.caustics_scale);
        assert_eq!(back.caustics_depth_fade, src.caustics_depth_fade);
        assert_eq!(back.shore_wave_strength, src.shore_wave_strength);
        assert_eq!(back.shore_wave_length, src.shore_wave_length);
        assert_eq!(back.shore_wave_period, src.shore_wave_period);
        assert_eq!(back.shore_wave_foam, src.shore_wave_foam);
        assert_eq!(back.spline_points, src.spline_points);
        assert_eq!(back.river_width, src.river_width);
        assert_eq!(back.flow_speed, src.flow_speed);
        assert_eq!(back.river_depth, src.river_depth);
        assert_eq!(back.river_segment_length, src.river_segment_length);
        assert_eq!(back.control_point_ref, src.control_point_ref);
    }
}
