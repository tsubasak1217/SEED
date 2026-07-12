// ============================================================
//  particle_emitter_component.rs — パーティクルエミッタコンポーネント
//
//  Actor に GPU パーティクルの放出源（エミッタ）を持たせる ECS スロット
//  コンポーネント。本コンポーネントは「エミッタのパラメータ（形状・出現範囲・
//  emit 制御・寿命・初速・重力・回転・サイズ・色カーブ・ブレンド・シミュレーション
//  空間など）」のデータのみを保持する（ECS 理念：データとロジックの分離）。
//
//  位置・向きは Actor の Transform から解決する（本コンポーネント自身は
//  位置・回転を持たない）。実際のパーティクルのシミュレーション（GPU
//  compute）と描画はレンダラ側が別管理する。個々のパーティクルの生存状態や
//  GPU バッファといった揮発状態は本コンポーネントには一切持たせない。
//
//  【放出方向の慣例】
//  `direction_local`（ローカル放出方向、既定 +Y）は Actor の Transform で
//  回転され、`direction_randomness`（0..1）でばらつく。
//  half_angle_deg = direction_randomness * 180（0=直進 / 0.5=半頂角90° /
//  1.0=半頂角180°＝全球ランダム）の線形マップ。
//
//  【カーブ（寿命正規化 t=0..1 で評価）】
//  速度・回転速度・色(HSVA)・スケール(xyz) を `ParamCurve`（キーの線形補間）で
//  与える。GPU では CPU で `CURVE_LUT_SAMPLES` 個へ焼いた LUT を storage buffer
//  で渡して評価する（レンダラ側実装）。色は HSVA のまま LUT に格納し、シェーダで
//  hsv→rgb 変換する（色相の正しい補間のため）。
//
//  【シリアライズと後方互換】
//  シリアライズ形は新スキーマ（`ParticleEmitterComponentData`）。旧 `.scene`
//  （emit_rate / burst / spread_angle_deg / start_size / end_size_scale /
//  start_color / end_color / loop_emit などの旧フィールド）は
//  `ParticleEmitterComponentRaw` で `#[serde(default)]` として受け、
//  `From<Raw>` で新スキーマへ変換して読み込む（`#[serde(from = ...)]`）。
// ============================================================

use serde::{Deserialize, Serialize};

use crate::engine::ecs::Component;

// ─── 上限定数 ─────────────────────────────────────────────────
// マジックナンバー禁止のため、上限値は名前付き定数として公開する。

/// 1 エミッタあたりのパーティクル最大数（GPU バッファ確保の上限）。
///
/// `max_particles` はこの値を超えないよう「値の利用側（レンダラ／IPC 更新）」で
/// clamp する方針。本コンポーネントの deserialize / from_data では clamp しない
/// （データはそのまま保持し、消費側で上限を適用する）。
pub const MAX_PARTICLES_PER_EMITTER: u32 = 65536;

/// Model 形状の頂点数上限。これを超えるモデルは警告して Point へフォールバックする
/// （利用側＝レンダラで適用）。
pub const MAX_PARTICLE_MODEL_VERTS: usize = 2048;

/// カーブを GPU LUT へ焼くときのサンプル数（t=0..1 を等間隔サンプル）。
/// 各サンプルは vec4（HSVA / xyz など各チャンネル）。
pub const CURVE_LUT_SAMPLES: usize = 64;

/// direction_randomness=1.0 に対応する放出半頂角（度）。全球ランダム。
/// half_angle_deg = direction_randomness * DIRECTION_RANDOMNESS_MAX_HALF_ANGLE_DEG。
pub const DIRECTION_RANDOMNESS_MAX_HALF_ANGLE_DEG: f32 = 180.0;

// ─── デフォルト値関数 ─────────────────────────────────────────
// マジックナンバー禁止のため、非ゼロ既定値はすべて関数に切り出す。

/// max_particles の既定値（GPU パーティクルプールの初期サイズ）。
fn default_max_particles() -> u32 { 1024 }
/// emit_interval（放出間隔・秒）の既定値（0.05 秒＝20 回/秒）。
fn default_emit_interval() -> f32 { 0.05 }
/// particles_per_emit（1 回の emit で放出する個数）の既定値。
fn default_particles_per_emit() -> u32 { 1 }
/// lifetime（寿命 min/max・秒）の既定値。
fn default_lifetime() -> [f32; 2] { [1.0, 2.0] }
/// initial_speed（初速 min/max）の既定値。
fn default_initial_speed() -> [f32; 2] { [1.0, 3.0] }
/// direction_local（ローカル放出方向）の既定値（+Y 上向き）。
fn default_direction_local() -> [f32; 3] { [0.0, 1.0, 0.0] }
/// gravity（重力加速度）の既定値（-Y 方向、地球重力相当）。
fn default_gravity() -> [f32; 3] { [0.0, -9.8, 0.0] }
/// size_range（全体サイズ倍率 min/max）の既定値。
fn default_size_range() -> [f32; 2] { [1.0, 1.0] }
/// playing（Play 開始時に放出を開始するか）の既定値（true）。
fn default_playing() -> bool { true }

// ─── ParticleBlend ────────────────────────────────────────────

/// パーティクルの合成（ブレンド）モード。
///
/// - `Additive`: 加算合成（炎・光・魔法エフェクト向け。既定）。
/// - `Alpha`   : 通常のアルファブレンド（煙・破片など不透明寄りの表現向け）。
#[derive(Clone, Copy, PartialEq, Eq, Serialize, Deserialize, Debug)]
#[serde(rename_all = "snake_case")]
pub enum ParticleBlend {
    /// 加算合成（既定）
    Additive,
    /// アルファブレンド
    Alpha,
}

impl Default for ParticleBlend {
    /// blend 省略時の既定は additive。
    fn default() -> Self { ParticleBlend::Additive }
}

impl ParticleBlend {
    /// GPU / シェーダのブレンド分岐へ渡す種別コード（描画側の定数と一致させる）。
    #[allow(dead_code)]
    pub fn to_code(self) -> u32 {
        match self {
            ParticleBlend::Additive => 0,
            ParticleBlend::Alpha    => 1,
        }
    }

    /// IPC 文字列（インスペクタのドロップダウン Tag）との相互変換。
    pub fn from_str_opt(s: &str) -> Option<Self> {
        match s {
            "additive" => Some(ParticleBlend::Additive),
            "alpha"    => Some(ParticleBlend::Alpha),
            _          => None,
        }
    }

    /// インスペクタへ送る種別文字列。
    pub fn as_str(self) -> &'static str {
        match self {
            ParticleBlend::Additive => "additive",
            ParticleBlend::Alpha    => "alpha",
        }
    }
}

// ─── ParticleSimSpace ─────────────────────────────────────────

/// パーティクルのシミュレーション空間。
///
/// - `World`: ワールド空間で更新（放出後はエミッタの移動に追従しない。既定）。
/// - `Local`: エミッタのローカル空間で更新（エミッタと一緒に動く）。
#[derive(Clone, Copy, PartialEq, Eq, Serialize, Deserialize, Debug)]
#[serde(rename_all = "snake_case")]
pub enum ParticleSimSpace {
    /// ワールド空間シミュレーション（既定）
    World,
    /// ローカル空間シミュレーション
    Local,
}

impl Default for ParticleSimSpace {
    /// sim_space 省略時の既定は world。
    fn default() -> Self { ParticleSimSpace::World }
}

impl ParticleSimSpace {
    /// GPU / compute の空間分岐へ渡す種別コード（シミュレーション側の定数と一致させる）。
    #[allow(dead_code)]
    pub fn to_code(self) -> u32 {
        match self {
            ParticleSimSpace::World => 0,
            ParticleSimSpace::Local => 1,
        }
    }

    /// IPC 文字列（インスペクタのドロップダウン Tag）との相互変換。
    pub fn from_str_opt(s: &str) -> Option<Self> {
        match s {
            "world" => Some(ParticleSimSpace::World),
            "local" => Some(ParticleSimSpace::Local),
            _       => None,
        }
    }

    /// インスペクタへ送る種別文字列。
    pub fn as_str(self) -> &'static str {
        match self {
            ParticleSimSpace::World => "world",
            ParticleSimSpace::Local => "local",
        }
    }
}

// ─── ParticleShape ────────────────────────────────────────────

/// パーティクル 1 粒の見た目形状。
///
/// - `Point` : 現行のカメラ向きビルボード（既定）。
/// - `Sphere`: 組込み低ポリ icosphere（単位球）。
/// - `Box`   : 組込み単位立方体。
/// - `Plane` : 組込み両面クアッド（単位平面）。
/// - `Model` : 既存ロード機構で読んだモデルの先頭プリミティブを流用。
///             頂点数が `MAX_PARTICLE_MODEL_VERTS` を超えたら警告して Point へ
///             フォールバックする（利用側＝レンダラで適用）。
///
/// serde は externally tagged（`"point"` / `{"model":{"path":"..."}}`）。
#[derive(Clone, PartialEq, Serialize, Deserialize, Debug)]
#[serde(rename_all = "snake_case")]
pub enum ParticleShape {
    /// カメラ向きビルボード（既定）
    Point,
    /// 組込み単位球（低ポリ icosphere）
    Sphere,
    /// 組込み単位立方体
    Box,
    /// 組込み両面クアッド
    Plane,
    /// モデルの先頭プリミティブ（頂点数上限あり）
    Model {
        /// モデルの仮想パス（assets:// など）。
        path: String,
    },
}

impl Default for ParticleShape {
    fn default() -> Self { ParticleShape::Point }
}

impl ParticleShape {
    /// GPU / レンダラの形状分岐コード（描画側の定数と一致させる）。
    /// Model は組込みメッシュではないため実行時のロード結果で扱う（ここでは 4）。
    pub fn to_code(&self) -> u32 {
        match self {
            ParticleShape::Point  => 0,
            ParticleShape::Sphere => 1,
            ParticleShape::Box    => 2,
            ParticleShape::Plane  => 3,
            ParticleShape::Model { .. } => 4,
        }
    }

    /// IPC のドロップダウン Tag 文字列。
    pub fn as_str(&self) -> &'static str {
        match self {
            ParticleShape::Point  => "point",
            ParticleShape::Sphere => "sphere",
            ParticleShape::Box    => "box",
            ParticleShape::Plane  => "plane",
            ParticleShape::Model { .. } => "model",
        }
    }

    /// IPC の Tag 文字列から形状を作る（Model のパスは現状を引き継ぐ。呼び出し側で補完）。
    /// `keep_path` は tag=="model" のときに使う既存パス（Point 等へ変えた場合は無視）。
    pub fn from_tag(tag: &str, keep_path: &str) -> Option<Self> {
        match tag {
            "point"  => Some(ParticleShape::Point),
            "sphere" => Some(ParticleShape::Sphere),
            "box"    => Some(ParticleShape::Box),
            "plane"  => Some(ParticleShape::Plane),
            "model"  => Some(ParticleShape::Model { path: keep_path.to_string() }),
            _        => None,
        }
    }

    /// Model のパス（Model 以外は空）。
    pub fn model_path(&self) -> &str {
        match self {
            ParticleShape::Model { path } => path.as_str(),
            _ => "",
        }
    }
}

// ─── SpawnVolume ──────────────────────────────────────────────

/// パーティクルのスポーン位置（出現範囲）。compute 内で体積内一様サンプルする。
///
/// - `Point` : エミッタ原点から放出（既定）。
/// - `Box`   : ローカル軸並行ボックス内一様。
/// - `Sphere`: 半径内一様（体積一様）。
///
/// serde は externally tagged（`"point"` / `{"box":{"half_extents":[..]}}`）。
#[derive(Clone, Copy, PartialEq, Serialize, Deserialize, Debug)]
#[serde(rename_all = "snake_case")]
pub enum SpawnVolume {
    /// 点発生（既定）
    Point,
    /// ボックス体積内一様
    Box {
        /// ローカル各軸の半径（half extents）。
        half_extents: [f32; 3],
    },
    /// 球体積内一様
    Sphere {
        /// 半径。
        radius: f32,
    },
}

impl Default for SpawnVolume {
    fn default() -> Self { SpawnVolume::Point }
}

impl SpawnVolume {
    /// GPU compute のスポーン体積分岐コード（sim シェーダの定数と一致させる）。
    pub fn to_code(&self) -> u32 {
        match self {
            SpawnVolume::Point     => 0,
            SpawnVolume::Box { .. } => 1,
            SpawnVolume::Sphere { .. } => 2,
        }
    }

    /// IPC のドロップダウン Tag 文字列。
    pub fn as_str(&self) -> &'static str {
        match self {
            SpawnVolume::Point     => "point",
            SpawnVolume::Box { .. } => "box",
            SpawnVolume::Sphere { .. } => "sphere",
        }
    }

    /// IPC の Tag 文字列から体積を作る（パラメータは既存値を引き継ぐ）。
    pub fn from_tag(tag: &str, keep_half: [f32; 3], keep_radius: f32) -> Option<Self> {
        match tag {
            "point"  => Some(SpawnVolume::Point),
            "box"    => Some(SpawnVolume::Box { half_extents: keep_half }),
            "sphere" => Some(SpawnVolume::Sphere { radius: keep_radius }),
            _        => None,
        }
    }

    /// ボックス半径（Box 以外は 0）。IPC 送信・編集の値保持に使う。
    pub fn box_half_extents(&self) -> [f32; 3] {
        match self {
            SpawnVolume::Box { half_extents } => *half_extents,
            _ => [0.0; 3],
        }
    }

    /// 球半径（Sphere 以外は 0）。
    pub fn sphere_radius(&self) -> f32 {
        match self {
            SpawnVolume::Sphere { radius } => *radius,
            _ => 0.0,
        }
    }
}

// ─── EmitMode ─────────────────────────────────────────────────

/// 放出モード。
///
/// - `Loop`  : 常時ループ放出（既定）。
/// - `Once`  : プール一巡分だけ放出して停止。
/// - `Count` : 累計 `total` 個だけ放出して停止。
///
/// serde は externally tagged（`"loop"` / `{"count":{"total":100}}`）。
#[derive(Clone, Copy, PartialEq, Serialize, Deserialize, Debug)]
#[serde(rename_all = "snake_case")]
pub enum EmitMode {
    /// 常時ループ放出（既定）
    Loop,
    /// 一巡で停止
    Once,
    /// 累計 total 個で停止
    Count {
        /// 放出する累計個数。
        total: u32,
    },
}

impl Default for EmitMode {
    fn default() -> Self { EmitMode::Loop }
}

impl EmitMode {
    /// IPC のドロップダウン Tag 文字列。
    pub fn as_str(&self) -> &'static str {
        match self {
            EmitMode::Loop      => "loop",
            EmitMode::Once      => "once",
            EmitMode::Count { .. } => "count",
        }
    }

    /// IPC の Tag 文字列から作る（Count の total は既存値を引き継ぐ）。
    pub fn from_tag(tag: &str, keep_total: u32) -> Option<Self> {
        match tag {
            "loop"  => Some(EmitMode::Loop),
            "once"  => Some(EmitMode::Once),
            "count" => Some(EmitMode::Count { total: keep_total }),
            _       => None,
        }
    }

    /// Count の total（Count 以外は 0）。IPC 送信・編集の値保持に使う。
    pub fn count_total(&self) -> u32 {
        match self {
            EmitMode::Count { total } => *total,
            _ => 0,
        }
    }
}

// ─── カーブデータモデル（C# カーブエディタと共有する契約）──────

/// カーブの 1 キー（t=正規化寿命 0..1、v=値）。v1 は線形補間。
#[derive(Clone, Copy, Serialize, Deserialize, Debug, PartialEq)]
pub struct CurveKey {
    /// 正規化寿命（0..1）。チャンネル内で昇順であること。
    pub t: f32,
    /// 値。
    pub v: f32,
}

/// カーブの 1 チャンネル（1 成分の時系列。キーは t 昇順）。
#[derive(Clone, Serialize, Deserialize, Debug, PartialEq)]
pub struct CurveChannel {
    /// キー列（t 昇順）。
    pub keys: Vec<CurveKey>,
}

impl CurveChannel {
    /// 定数チャンネル（両端 [0,v]..[1,v]）を作る。
    pub fn constant(v: f32) -> Self {
        Self { keys: vec![CurveKey { t: 0.0, v }, CurveKey { t: 1.0, v }] }
    }

    /// 2 キー（t=0 が a、t=1 が b）の線形チャンネルを作る。
    pub fn linear(a: f32, b: f32) -> Self {
        Self { keys: vec![CurveKey { t: 0.0, v: a }, CurveKey { t: 1.0, v: b }] }
    }

    /// 正規化寿命 t での値を線形補間で評価する。
    ///
    /// - キー無し → 0.0。
    /// - t は端点でクランプ（最初のキー未満は最初の値、最後のキー超は最後の値）。
    pub fn eval(&self, t: f32) -> f32 {
        let keys = &self.keys;
        if keys.is_empty() { return 0.0; }
        if t <= keys[0].t { return keys[0].v; }
        let last = keys.len() - 1;
        if t >= keys[last].t { return keys[last].v; }
        // t を含む区間 [k, k+1] を線形探索（キー数は少数前提）。
        for w in 0..last {
            let k0 = keys[w];
            let k1 = keys[w + 1];
            if t >= k0.t && t <= k1.t {
                let span = k1.t - k0.t;
                if span <= f32::EPSILON { return k0.v; }
                let f = (t - k0.t) / span;
                return k0.v + (k1.v - k0.v) * f;
            }
        }
        keys[last].v
    }
}

/// 複数チャンネル（1..4 本）のパラメータカーブ。
///
/// - 速度 / 回転速度: 1 チャンネル（x のみ使用）。
/// - 色: 4 チャンネル（HSVA）。
/// - スケール: 3 チャンネル（xyz）。
#[derive(Clone, Serialize, Deserialize, Debug, PartialEq, Default)]
pub struct ParamCurve {
    /// チャンネル列（1..4 本）。
    pub channels: Vec<CurveChannel>,
}

impl ParamCurve {
    /// 全チャンネル定数のカーブ（1 チャンネル）。速度/回転の既定に使う。
    pub fn constant1(v: f32) -> Self {
        Self { channels: vec![CurveChannel::constant(v)] }
    }

    /// t での各成分値を vec4 で返す（存在しないチャンネルは 0.0）。
    ///
    /// 消費側（LUT 焼き / シェーダ）は使う成分だけを読む（速度は x、色は xyzw 等）。
    pub fn sample(&self, t: f32) -> [f32; 4] {
        let mut out = [0.0f32; 4];
        for (c, ch) in self.channels.iter().take(4).enumerate() {
            out[c] = ch.eval(t);
        }
        out
    }

    /// カーブを `samples` 個の vec4 LUT へ焼く（t を 0..1 で等間隔サンプル）。
    ///
    /// GPU の storage buffer へ連結する用途。samples>=2 を想定（1 のときは t=0 のみ）。
    pub fn bake_lut(&self, samples: usize) -> Vec<[f32; 4]> {
        let n = samples.max(1);
        let mut out = Vec::with_capacity(n);
        for i in 0..n {
            let t = if n == 1 { 0.0 } else { i as f32 / (n - 1) as f32 };
            out.push(self.sample(t));
        }
        out
    }
}

// ─── 色空間ヘルパー（RGB→HSV。旧色の互換変換に使う）──────────

/// RGB(0..1)→HSV(H:0..1, S:0..1, V:0..1) 変換。
///
/// 色相 H は 0..1 正規化（度ではない）。彩度 0 のときは H=0 とする。
/// 互換変換（旧 start_color/end_color の HSVA カーブ化）にのみ使う CPU 関数。
fn rgb_to_hsv(r: f32, g: f32, b: f32) -> (f32, f32, f32) {
    let max = r.max(g).max(b);
    let min = r.min(g).min(b);
    let delta = max - min;
    let v = max;
    let s = if max <= f32::EPSILON { 0.0 } else { delta / max };
    let h = if delta <= f32::EPSILON {
        0.0
    } else if max == r {
        (((g - b) / delta) % 6.0) / 6.0
    } else if max == g {
        (((b - r) / delta) + 2.0) / 6.0
    } else {
        (((r - g) / delta) + 4.0) / 6.0
    };
    // H を [0,1) に正規化（負の剰余を補正）。
    let h = if h < 0.0 { h + 1.0 } else { h };
    (h, s, v)
}

/// RGBA(0..1) → HSVA 2 キーカーブ（旧 start_color/end_color を色カーブへ変換）。
fn rgba_pair_to_hsva_curve(start: [f32; 4], end: [f32; 4]) -> ParamCurve {
    let (h0, s0, v0) = rgb_to_hsv(start[0], start[1], start[2]);
    let (h1, s1, v1) = rgb_to_hsv(end[0], end[1], end[2]);
    ParamCurve {
        channels: vec![
            CurveChannel::linear(h0, h1),         // H
            CurveChannel::linear(s0, s1),         // S
            CurveChannel::linear(v0, v1),         // V
            CurveChannel::linear(start[3], end[3]), // A
        ],
    }
}

// ─── 既定カーブ ───────────────────────────────────────────────

/// speed_curve の既定（定数 1.0）。
fn default_speed_curve() -> ParamCurve { ParamCurve::constant1(1.0) }
/// rot_speed_curve の既定（定数 1.0）。
fn default_rot_speed_curve() -> ParamCurve { ParamCurve::constant1(1.0) }
/// color_curve の既定（HSVA：白 V=1, S=0, H=0、A は 1→0 でフェードアウト）。
/// 旧既定（start=白不透明 / end=白透明）と同等の見た目。
fn default_color_curve() -> ParamCurve {
    ParamCurve {
        channels: vec![
            CurveChannel::constant(0.0), // H
            CurveChannel::constant(0.0), // S
            CurveChannel::constant(1.0), // V
            CurveChannel::linear(1.0, 0.0), // A（フェードアウト）
        ],
    }
}
/// scale_curve の既定（xyz とも 1→0 で縮小消滅。旧 end_size_scale=0 と同等）。
fn default_scale_curve() -> ParamCurve {
    ParamCurve {
        channels: vec![
            CurveChannel::linear(1.0, 0.0), // x
            CurveChannel::linear(1.0, 0.0), // y
            CurveChannel::linear(1.0, 0.0), // z
        ],
    }
}

// ─── ParticleEmitterComponentData（シリアライズ用・新スキーマ）──

/// ParticleEmitterComponent のシリアライズ用データ（.scene 保存・Undo スナップショット）。
///
/// デシリアライズは `ParticleEmitterComponentRaw` を経由し（`#[serde(from)]`）、
/// 旧フィールドを互換変換する。シリアライズは本構造体を直接使う（常に新形式）。
#[derive(Clone, Serialize, Deserialize, Debug)]
#[serde(from = "ParticleEmitterComponentRaw")]
pub struct ParticleEmitterComponentData {
    /// パーティクル最大数（GPU プールサイズ）。既定 1024。上限は利用側で clamp。
    pub max_particles: u32,

    // ── 形状・出現範囲 ──
    /// パーティクル 1 粒の見た目形状。既定 Point。
    pub shape: ParticleShape,
    /// スポーン位置（出現範囲）。既定 Point。
    pub spawn_volume: SpawnVolume,

    // ── emit 制御 ──
    /// 放出モード。既定 Loop。
    pub emit_mode: EmitMode,
    /// 開始までの待機秒（初回のみ）。既定 0。
    pub initial_delay: f32,
    /// 開始時に経過済みにする時間（プリウォーム）。既定 0。上限は利用側で clamp。
    pub prewarm_time: f32,
    /// 放出間隔（秒）。既定 0.05。
    pub emit_interval: f32,
    /// 1 回の emit で放出する個数。既定 1。
    pub particles_per_emit: u32,

    // ── 初速・方向 ──
    /// 寿命 [min, max]（秒）。既定 [1.0, 2.0]。
    pub lifetime: [f32; 2],
    /// 初速 [min, max]。既定 [1.0, 3.0]。
    pub initial_speed: [f32; 2],
    /// ローカル放出方向。既定 [0, 1, 0]（+Y）。
    pub direction_local: [f32; 3],
    /// 方向ランダム度（0..1）。half_angle_deg = randomness*180。既定 0。
    pub direction_randomness: f32,

    // ── 力学・回転・サイズ ──
    /// 重力加速度。既定 [0, -9.8, 0]。
    pub gravity: [f32; 3],
    /// 空気抵抗係数。既定 0.0。
    pub drag: f32,
    /// 回転速度 [min, max]（度/秒）。既定 [0, 0]。
    pub rot_speed_range: [f32; 2],
    /// 全体サイズ倍率 [min, max]。既定 [1, 1]。
    pub size_range: [f32; 2],

    // ── カーブ（t=0..1 で評価）──
    /// 射出速度倍率カーブ（x）。既定定数 1。
    pub speed_curve: ParamCurve,
    /// 回転速度倍率カーブ（x）。既定定数 1。
    pub rot_speed_curve: ParamCurve,
    /// 色カーブ（xyzw=HSVA）。既定 白→フェードアウト。
    pub color_curve: ParamCurve,
    /// スケールカーブ（xyz）。既定 1→0 縮小。
    pub scale_curve: ParamCurve,
    /// ランダム色カーブ（HSVA のリスト）。非空ならパーティクルごとに 1 本を選ぶ。既定空。
    pub random_color_curves: Vec<ParamCurve>,

    // ── 見た目・その他 ──
    /// テクスチャパス（assets:// 仮想パス）。空文字はプロシージャル白円。既定 ""。
    pub texture_path: String,
    /// 合成モード（additive / alpha）。既定 additive。
    pub blend: ParticleBlend,
    /// シミュレーション空間（world / local）。既定 world。
    pub sim_space: ParticleSimSpace,
    /// 放出中フラグ（Play 開始時に放出を開始するか）。既定 true。
    pub playing: bool,
}

impl Default for ParticleEmitterComponentData {
    fn default() -> Self {
        Self {
            max_particles:       default_max_particles(),
            shape:               ParticleShape::default(),
            spawn_volume:        SpawnVolume::default(),
            emit_mode:           EmitMode::default(),
            initial_delay:       0.0,
            prewarm_time:        0.0,
            emit_interval:       default_emit_interval(),
            particles_per_emit:  default_particles_per_emit(),
            lifetime:            default_lifetime(),
            initial_speed:       default_initial_speed(),
            direction_local:     default_direction_local(),
            direction_randomness:0.0,
            gravity:             default_gravity(),
            drag:                0.0,
            rot_speed_range:     [0.0, 0.0],
            size_range:          default_size_range(),
            speed_curve:         default_speed_curve(),
            rot_speed_curve:     default_rot_speed_curve(),
            color_curve:         default_color_curve(),
            scale_curve:         default_scale_curve(),
            random_color_curves: Vec::new(),
            texture_path:        String::new(),
            blend:               ParticleBlend::default(),
            sim_space:           ParticleSimSpace::default(),
            playing:             default_playing(),
        }
    }
}

// ─── ParticleEmitterComponentRaw（互換デシリアライズ用）───────

/// デシリアライズの中間表現。新フィールドと旧フィールドを全て `Option`（`#[serde(default)]`）
/// で受け、`From<Raw>` で新スキーマへ変換する。
///
/// 新フィールドが在ればそれを、無ければ旧フィールドから互換変換、どちらも無ければ既定を使う。
#[derive(Deserialize)]
struct ParticleEmitterComponentRaw {
    // 共通・維持フィールド
    #[serde(default)] max_particles:   Option<u32>,
    #[serde(default)] lifetime:        Option<[f32; 2]>,
    #[serde(default)] initial_speed:   Option<[f32; 2]>,
    #[serde(default)] direction_local: Option<[f32; 3]>,
    #[serde(default)] gravity:         Option<[f32; 3]>,
    #[serde(default)] drag:            Option<f32>,
    #[serde(default)] texture_path:    Option<String>,
    #[serde(default)] blend:           Option<ParticleBlend>,
    #[serde(default)] sim_space:       Option<ParticleSimSpace>,
    #[serde(default)] playing:         Option<bool>,

    // 新フィールド
    #[serde(default)] shape:               Option<ParticleShape>,
    #[serde(default)] spawn_volume:        Option<SpawnVolume>,
    #[serde(default)] emit_mode:           Option<EmitMode>,
    #[serde(default)] initial_delay:       Option<f32>,
    #[serde(default)] prewarm_time:        Option<f32>,
    #[serde(default)] emit_interval:       Option<f32>,
    #[serde(default)] particles_per_emit:  Option<u32>,
    #[serde(default)] direction_randomness:Option<f32>,
    #[serde(default)] rot_speed_range:     Option<[f32; 2]>,
    #[serde(default)] size_range:          Option<[f32; 2]>,
    #[serde(default)] speed_curve:         Option<ParamCurve>,
    #[serde(default)] rot_speed_curve:     Option<ParamCurve>,
    #[serde(default)] color_curve:         Option<ParamCurve>,
    #[serde(default)] scale_curve:         Option<ParamCurve>,
    #[serde(default)] random_color_curves: Option<Vec<ParamCurve>>,

    // 旧フィールド（互換変換用）
    #[serde(default)] emit_rate:        Option<f32>,
    #[serde(default)] spread_angle_deg: Option<f32>,
    #[serde(default)] start_size:       Option<[f32; 2]>,
    #[serde(default)] end_size_scale:   Option<f32>,
    #[serde(default)] start_color:      Option<[f32; 4]>,
    #[serde(default)] end_color:        Option<[f32; 4]>,
    #[serde(default)] loop_emit:        Option<bool>,
    // burst（旧・Play 開始時一括放出数）は明確に対応する新概念が無いため変換で破棄する。
    #[serde(default)] #[allow(dead_code)] burst: Option<u32>,
}

impl From<ParticleEmitterComponentRaw> for ParticleEmitterComponentData {
    /// 新フィールド優先、無ければ旧フィールドから互換変換、無ければ既定。
    fn from(r: ParticleEmitterComponentRaw) -> Self {
        let def = ParticleEmitterComponentData::default();

        // emit_interval / particles_per_emit: 新優先。無ければ旧 emit_rate から
        // interval=1/rate（rate>0）, per_emit=1 へ変換。
        let emit_interval = r.emit_interval.unwrap_or_else(|| {
            match r.emit_rate {
                Some(rate) if rate > 0.0 => 1.0 / rate,
                _ => def.emit_interval,
            }
        });
        let particles_per_emit = r.particles_per_emit.unwrap_or_else(|| {
            if r.emit_rate.is_some() { 1 } else { def.particles_per_emit }
        });

        // emit_mode: 新優先。無ければ旧 loop_emit（true→Loop / false→Once）。
        let emit_mode = r.emit_mode.unwrap_or_else(|| {
            match r.loop_emit {
                Some(true)  => EmitMode::Loop,
                Some(false) => EmitMode::Once,
                None        => def.emit_mode,
            }
        });

        // direction_randomness: 新優先。無ければ旧 spread_angle_deg/180。
        let direction_randomness = r.direction_randomness.unwrap_or_else(|| {
            match r.spread_angle_deg {
                Some(s) => (s / DIRECTION_RANDOMNESS_MAX_HALF_ANGLE_DEG).clamp(0.0, 1.0),
                None => def.direction_randomness,
            }
        });

        // size_range: 新優先。無ければ旧 start_size（乱数開始サイズを全体倍率へ転用）。
        let size_range = r.size_range.unwrap_or_else(|| {
            r.start_size.unwrap_or(def.size_range)
        });

        // color_curve: 新優先。無ければ旧 start_color/end_color を HSVA 2 キーカーブへ。
        let color_curve = r.color_curve.unwrap_or_else(|| {
            match (r.start_color, r.end_color) {
                (Some(s), Some(e)) => rgba_pair_to_hsva_curve(s, e),
                (Some(s), None)    => rgba_pair_to_hsva_curve(s, s),
                (None, Some(e))    => rgba_pair_to_hsva_curve(e, e),
                (None, None)       => def.color_curve.clone(),
            }
        });

        // scale_curve: 新優先。無ければ旧 end_size_scale から 1→end_size_scale（xyz）。
        // （旧 start_size は size_range に移したので、ここは寿命内の倍率変化のみ扱う）
        let scale_curve = r.scale_curve.unwrap_or_else(|| {
            match r.end_size_scale {
                Some(es) => ParamCurve {
                    channels: vec![
                        CurveChannel::linear(1.0, es),
                        CurveChannel::linear(1.0, es),
                        CurveChannel::linear(1.0, es),
                    ],
                },
                None => def.scale_curve.clone(),
            }
        });

        Self {
            max_particles:       r.max_particles.unwrap_or(def.max_particles),
            shape:               r.shape.unwrap_or(def.shape),
            spawn_volume:        r.spawn_volume.unwrap_or(def.spawn_volume),
            emit_mode,
            initial_delay:       r.initial_delay.unwrap_or(def.initial_delay),
            prewarm_time:        r.prewarm_time.unwrap_or(def.prewarm_time),
            emit_interval,
            particles_per_emit,
            lifetime:            r.lifetime.unwrap_or(def.lifetime),
            initial_speed:       r.initial_speed.unwrap_or(def.initial_speed),
            direction_local:     r.direction_local.unwrap_or(def.direction_local),
            direction_randomness,
            gravity:             r.gravity.unwrap_or(def.gravity),
            drag:                r.drag.unwrap_or(def.drag),
            rot_speed_range:     r.rot_speed_range.unwrap_or(def.rot_speed_range),
            size_range,
            speed_curve:         r.speed_curve.unwrap_or(def.speed_curve),
            rot_speed_curve:     r.rot_speed_curve.unwrap_or(def.rot_speed_curve),
            color_curve,
            scale_curve,
            random_color_curves: r.random_color_curves.unwrap_or(def.random_color_curves),
            texture_path:        r.texture_path.unwrap_or(def.texture_path),
            blend:               r.blend.unwrap_or(def.blend),
            sim_space:           r.sim_space.unwrap_or(def.sim_space),
            playing:             r.playing.unwrap_or(def.playing),
        }
    }
}

// ─── ParticleEmitterComponent（ECS 実体）─────────────────────

/// パーティクルエミッタコンポーネント（ECS 実体）。
///
/// 保持するのはエミッタのパラメータのみ。位置・向きは Actor の Transform から
/// レンダラが解決し、個々のパーティクルの生存状態や GPU バッファはレンダラが
/// 別管理する。揮発状態（pending_burst）以外は Data と同一構成。
#[derive(Clone, Debug)]
pub struct ParticleEmitterComponent {
    pub max_particles:        u32,
    pub shape:                ParticleShape,
    pub spawn_volume:         SpawnVolume,
    pub emit_mode:            EmitMode,
    pub initial_delay:        f32,
    pub prewarm_time:         f32,
    pub emit_interval:        f32,
    pub particles_per_emit:   u32,
    pub lifetime:             [f32; 2],
    pub initial_speed:        [f32; 2],
    pub direction_local:      [f32; 3],
    pub direction_randomness: f32,
    pub gravity:              [f32; 3],
    pub drag:                 f32,
    pub rot_speed_range:      [f32; 2],
    pub size_range:           [f32; 2],
    pub speed_curve:          ParamCurve,
    pub rot_speed_curve:      ParamCurve,
    pub color_curve:          ParamCurve,
    pub scale_curve:          ParamCurve,
    pub random_color_curves:  Vec<ParamCurve>,
    pub texture_path:         String,
    pub blend:                ParticleBlend,
    pub sim_space:            ParticleSimSpace,
    pub playing:              bool,
    /// スクリプト Burst(n) が積む「即時放出リクエスト数」（ランタイム専用・非シリアライズ）。
    ///
    /// スクリプト API（host_api）が加算し、GPU パーティクルシステムが毎フレーム
    /// 消費してゼロに戻す。シーン保存（to_data）には含めない。
    pub pending_burst:        u32,
    /// カーブ差し替え世代カウンタ（ランタイム専用・非シリアライズ）。
    ///
    /// IPC でカーブを差し替えた際にインクリメントし、レンダラが前回焼いた世代と
    /// 比較して LUT 再焼きを判定する（カーブ変更時のみ再焼き）。
    pub curve_generation:     u64,
}

impl ParticleEmitterComponent {
    /// シリアライズ用データからコンポーネントを構築する。
    pub fn from_data(data: ParticleEmitterComponentData) -> Self {
        Self {
            max_particles:        data.max_particles,
            shape:                data.shape,
            spawn_volume:         data.spawn_volume,
            emit_mode:            data.emit_mode,
            initial_delay:        data.initial_delay,
            prewarm_time:         data.prewarm_time,
            emit_interval:        data.emit_interval,
            particles_per_emit:   data.particles_per_emit,
            lifetime:             data.lifetime,
            initial_speed:        data.initial_speed,
            direction_local:      data.direction_local,
            direction_randomness: data.direction_randomness,
            gravity:              data.gravity,
            drag:                 data.drag,
            rot_speed_range:      data.rot_speed_range,
            size_range:           data.size_range,
            speed_curve:          data.speed_curve,
            rot_speed_curve:      data.rot_speed_curve,
            color_curve:          data.color_curve,
            scale_curve:          data.scale_curve,
            random_color_curves:  data.random_color_curves,
            texture_path:         data.texture_path,
            blend:                data.blend,
            sim_space:            data.sim_space,
            playing:              data.playing,
            // ランタイム専用（非シリアライズ）は常に初期値から始める。
            pending_burst:        0,
            curve_generation:     0,
        }
    }

    /// シリアライズ用データへ変換する。
    pub fn to_data(&self) -> ParticleEmitterComponentData {
        ParticleEmitterComponentData {
            max_particles:        self.max_particles,
            shape:                self.shape.clone(),
            spawn_volume:         self.spawn_volume,
            emit_mode:            self.emit_mode,
            initial_delay:        self.initial_delay,
            prewarm_time:         self.prewarm_time,
            emit_interval:        self.emit_interval,
            particles_per_emit:   self.particles_per_emit,
            lifetime:             self.lifetime,
            initial_speed:        self.initial_speed,
            direction_local:      self.direction_local,
            direction_randomness: self.direction_randomness,
            gravity:              self.gravity,
            drag:                 self.drag,
            rot_speed_range:      self.rot_speed_range,
            size_range:           self.size_range,
            speed_curve:          self.speed_curve.clone(),
            rot_speed_curve:      self.rot_speed_curve.clone(),
            color_curve:          self.color_curve.clone(),
            scale_curve:          self.scale_curve.clone(),
            random_color_curves:  self.random_color_curves.clone(),
            texture_path:         self.texture_path.clone(),
            blend:                self.blend,
            sim_space:            self.sim_space,
            playing:              self.playing,
        }
    }

    /// カーブ差し替えを記録する（IPC の SET_PARTICLE_CURVE から呼ぶ）。
    /// レンダラが世代比較で LUT 再焼きを判定するためのカウンタを進める。
    pub fn bump_curve_generation(&mut self) {
        self.curve_generation = self.curve_generation.wrapping_add(1);
    }
}

impl Default for ParticleEmitterComponent {
    fn default() -> Self { Self::from_data(ParticleEmitterComponentData::default()) }
}

impl Component for ParticleEmitterComponent {}

// ─── テスト ───────────────────────────────────────────────────

#[cfg(test)]
mod tests {
    use super::*;

    /// 空 JSON `{}` からのデシリアライズで全フィールドが既定値になること。
    #[test]
    fn deserialize_empty_json_yields_defaults() {
        let data: ParticleEmitterComponentData =
            serde_json::from_str("{}").expect("空 JSON のデシリアライズに失敗");
        let def = ParticleEmitterComponentData::default();

        assert_eq!(data.max_particles,       def.max_particles);
        assert_eq!(data.shape,               def.shape);
        assert_eq!(data.spawn_volume,        def.spawn_volume);
        assert_eq!(data.emit_mode,           def.emit_mode);
        assert_eq!(data.emit_interval,       def.emit_interval);
        assert_eq!(data.particles_per_emit,  def.particles_per_emit);
        assert_eq!(data.lifetime,            def.lifetime);
        assert_eq!(data.direction_randomness,def.direction_randomness);
        assert_eq!(data.size_range,          def.size_range);
        assert_eq!(data.speed_curve,         def.speed_curve);
        assert_eq!(data.color_curve,         def.color_curve);
        assert_eq!(data.scale_curve,         def.scale_curve);
        assert!(data.random_color_curves.is_empty());
        assert_eq!(data.blend,               def.blend);
        assert_eq!(data.playing,             def.playing);
    }

    /// 新スキーマの from_data → serialize → deserialize → to_data ラウンドトリップ。
    #[test]
    fn roundtrip_preserves_values() {
        let original = ParticleEmitterComponentData {
            max_particles:       4096,
            shape:               ParticleShape::Model { path: "assets://fx/star.glb".into() },
            spawn_volume:        SpawnVolume::Box { half_extents: [1.0, 2.0, 3.0] },
            emit_mode:           EmitMode::Count { total: 500 },
            initial_delay:       0.5,
            prewarm_time:        2.0,
            emit_interval:       0.02,
            particles_per_emit:  4,
            lifetime:            [0.5, 4.0],
            initial_speed:       [2.0, 5.0],
            direction_local:     [1.0, 0.0, 0.0],
            direction_randomness:0.25,
            gravity:             [0.0, -1.0, 0.0],
            drag:                0.25,
            rot_speed_range:     [-90.0, 90.0],
            size_range:          [0.5, 2.0],
            speed_curve:         ParamCurve::constant1(1.5),
            rot_speed_curve:     ParamCurve { channels: vec![CurveChannel::linear(0.0, 2.0)] },
            color_curve:         default_color_curve(),
            scale_curve:         default_scale_curve(),
            random_color_curves: vec![ParamCurve::constant1(0.3), default_color_curve()],
            texture_path:        "assets://fx/spark.png".to_string(),
            blend:               ParticleBlend::Alpha,
            sim_space:           ParticleSimSpace::Local,
            playing:             false,
        };

        let component = ParticleEmitterComponent::from_data(original.clone());
        let back = component.to_data();
        let json = serde_json::to_string(&back).expect("シリアライズに失敗");
        let restored: ParticleEmitterComponentData =
            serde_json::from_str(&json).expect("デシリアライズに失敗");

        assert_eq!(restored.max_particles,       original.max_particles);
        assert_eq!(restored.shape,               original.shape);
        assert_eq!(restored.spawn_volume,        original.spawn_volume);
        assert_eq!(restored.emit_mode,           original.emit_mode);
        assert_eq!(restored.initial_delay,       original.initial_delay);
        assert_eq!(restored.prewarm_time,        original.prewarm_time);
        assert_eq!(restored.emit_interval,       original.emit_interval);
        assert_eq!(restored.particles_per_emit,  original.particles_per_emit);
        assert_eq!(restored.lifetime,            original.lifetime);
        assert_eq!(restored.initial_speed,       original.initial_speed);
        assert_eq!(restored.direction_local,     original.direction_local);
        assert_eq!(restored.direction_randomness,original.direction_randomness);
        assert_eq!(restored.gravity,             original.gravity);
        assert_eq!(restored.drag,                original.drag);
        assert_eq!(restored.rot_speed_range,     original.rot_speed_range);
        assert_eq!(restored.size_range,          original.size_range);
        assert_eq!(restored.speed_curve,         original.speed_curve);
        assert_eq!(restored.rot_speed_curve,     original.rot_speed_curve);
        assert_eq!(restored.color_curve,         original.color_curve);
        assert_eq!(restored.scale_curve,         original.scale_curve);
        assert_eq!(restored.random_color_curves, original.random_color_curves);
        assert_eq!(restored.texture_path,        original.texture_path);
        assert_eq!(restored.blend,               original.blend);
        assert_eq!(restored.sim_space,           original.sim_space);
        assert_eq!(restored.playing,             original.playing);
    }

    /// 旧スキーマ（emit_rate / spread_angle_deg / start_size / end_size_scale /
    /// start_color / end_color / loop_emit / burst）が互換変換で読めること。
    #[test]
    fn legacy_scene_converts() {
        // 旧 .scene のエミッタ JSON（旧フィールドのみ）。
        let legacy = r#"{
            "max_particles": 2048,
            "emit_rate": 200.0,
            "burst": 10,
            "lifetime": [0.5, 1.5],
            "initial_speed": [1.0, 2.0],
            "spread_angle_deg": 90.0,
            "direction_local": [0.0, 1.0, 0.0],
            "gravity": [0.0, -5.0, 0.0],
            "drag": 0.1,
            "start_size": [0.2, 0.4],
            "end_size_scale": 0.5,
            "start_color": [1.0, 0.0, 0.0, 1.0],
            "end_color": [0.0, 0.0, 1.0, 0.0],
            "texture_path": "assets://fx/old.png",
            "blend": "alpha",
            "sim_space": "local",
            "playing": true,
            "loop_emit": false
        }"#;

        let data: ParticleEmitterComponentData =
            serde_json::from_str(legacy).expect("旧 .scene の互換読込に失敗");

        // emit_rate 200 → interval 1/200 = 0.005, per_emit 1。
        assert!((data.emit_interval - 0.005).abs() < 1e-6, "emit_interval 変換");
        assert_eq!(data.particles_per_emit, 1);
        // loop_emit=false → Once。
        assert_eq!(data.emit_mode, EmitMode::Once);
        // spread 90 → randomness 0.5。
        assert!((data.direction_randomness - 0.5).abs() < 1e-6, "randomness 変換");
        // start_size → size_range。
        assert_eq!(data.size_range, [0.2, 0.4]);
        // 維持フィールド。
        assert_eq!(data.max_particles, 2048);
        assert_eq!(data.lifetime, [0.5, 1.5]);
        assert_eq!(data.gravity, [0.0, -5.0, 0.0]);
        assert_eq!(data.blend, ParticleBlend::Alpha);
        assert_eq!(data.sim_space, ParticleSimSpace::Local);
        // 色カーブは 4 チャンネル(HSVA)。赤→青、A 1→0。
        assert_eq!(data.color_curve.channels.len(), 4);
        let a0 = data.color_curve.channels[3].eval(0.0);
        let a1 = data.color_curve.channels[3].eval(1.0);
        assert!((a0 - 1.0).abs() < 1e-6 && a1.abs() < 1e-6, "アルファのフェード");
        // 赤(H≈0)→青(H≈0.667) の色相補間。
        let h0 = data.color_curve.channels[0].eval(0.0);
        let h1 = data.color_curve.channels[0].eval(1.0);
        assert!(h0.abs() < 1e-4, "start H は赤(0)");
        assert!((h1 - 2.0/3.0).abs() < 1e-3, "end H は青(0.667)");
        // scale_curve は 1→0.5（end_size_scale）。
        assert_eq!(data.scale_curve.channels.len(), 3);
        assert!((data.scale_curve.channels[0].eval(1.0) - 0.5).abs() < 1e-6);
    }

    /// ParamCurve::eval の端点クランプと線形補間。
    #[test]
    fn curve_eval_clamps_and_lerps() {
        let ch = CurveChannel { keys: vec![
            CurveKey { t: 0.2, v: 1.0 },
            CurveKey { t: 0.8, v: 3.0 },
        ]};
        assert_eq!(ch.eval(0.0), 1.0);  // 端点クランプ（左）
        assert_eq!(ch.eval(1.0), 3.0);  // 端点クランプ（右）
        assert_eq!(ch.eval(0.5), 2.0);  // 中点線形
        assert!(ch.eval(0.2) == 1.0 && ch.eval(0.8) == 3.0);
    }

    /// bake_lut のサイズと端点。
    #[test]
    fn bake_lut_size_and_endpoints() {
        let c = ParamCurve { channels: vec![CurveChannel::linear(0.0, 1.0)] };
        let lut = c.bake_lut(CURVE_LUT_SAMPLES);
        assert_eq!(lut.len(), CURVE_LUT_SAMPLES);
        assert!((lut[0][0] - 0.0).abs() < 1e-6);
        assert!((lut[CURVE_LUT_SAMPLES - 1][0] - 1.0).abs() < 1e-6);
        // 未使用チャンネル（y,z,w）は 0。
        assert_eq!(lut[10][1], 0.0);
    }
}
