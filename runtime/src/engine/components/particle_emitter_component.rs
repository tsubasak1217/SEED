// ============================================================
//  particle_emitter_component.rs — パーティクルエミッタコンポーネント
//
//  Actor に GPU パーティクルの放出源（エミッタ）を持たせる ECS スロット
//  コンポーネント。本コンポーネントは「エミッタのパラメータ（放出レート・
//  寿命・初速・重力・サイズ・色・ブレンド・シミュレーション空間など）」の
//  データのみを保持する（ECS 理念：データとロジックの分離）。
//
//  位置・向きは Actor の Transform から解決する（本コンポーネント自身は
//  位置・回転を持たない）。実際のパーティクルのシミュレーション（GPU
//  compute）と描画はレンダラ側が別管理する。個々のパーティクルの生存状態や
//  GPU バッファといった揮発状態は本コンポーネントには一切持たせない。
//
//  【放出方向の慣例】
//  `direction_local`（ローカル放出方向、既定 +Y）は Actor の Transform で
//  回転され、その周囲 `spread_angle_deg`（円錐の半頂角）の範囲へ放出される。
//
//  【シリアライズ】
//  全フィールドに #[serde(default)]（非ゼロ既定は default fn）を付け、
//  旧 .scene（フィールド欠落）でも読み込みが失敗しないようにする。
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

// ─── デフォルト値関数 ─────────────────────────────────────────
// マジックナンバー禁止のため、非ゼロ既定値はすべて関数に切り出す。

/// max_particles の既定値（GPU パーティクルプールの初期サイズ）。
fn default_max_particles() -> u32 { 1024 }
/// emit_rate の既定値（1 秒あたりの放出個数）。
fn default_emit_rate() -> f32 { 100.0 }
/// lifetime（寿命 min/max・秒）の既定値。
fn default_lifetime() -> [f32; 2] { [1.0, 2.0] }
/// initial_speed（初速 min/max）の既定値。
fn default_initial_speed() -> [f32; 2] { [1.0, 3.0] }
/// spread_angle_deg（放出円錐の半頂角・度）の既定値。
fn default_spread_angle_deg() -> f32 { 30.0 }
/// direction_local（ローカル放出方向）の既定値（+Y 上向き）。
fn default_direction_local() -> [f32; 3] { [0.0, 1.0, 0.0] }
/// gravity（重力加速度）の既定値（-Y 方向、地球重力相当）。
fn default_gravity() -> [f32; 3] { [0.0, -9.8, 0.0] }
/// start_size（開始サイズ min/max・ワールド単位）の既定値。
fn default_start_size() -> [f32; 2] { [0.1, 0.2] }
/// start_color（開始色 RGBA）の既定値（不透明な白）。
fn default_start_color() -> [f32; 4] { [1.0, 1.0, 1.0, 1.0] }
/// end_color（終了色 RGBA）の既定値（白→透明のフェードアウト）。
fn default_end_color() -> [f32; 4] { [1.0, 1.0, 1.0, 0.0] }
/// playing（Play 開始時に放出を開始するか）の既定値（true）。
fn default_playing() -> bool { true }
/// loop_emit（寿命ループ放出するか）の既定値（true）。
fn default_loop_emit() -> bool { true }

// ─── ParticleBlend ────────────────────────────────────────────

/// パーティクルの合成（ブレンド）モード。
///
/// - `Additive`: 加算合成（炎・光・魔法エフェクト向け。既定）。
/// - `Alpha`   : 通常のアルファブレンド（煙・破片など不透明寄りの表現向け）。
///
/// serde は snake_case でシリアライズする（`"additive"` / `"alpha"`）。
/// 旧シーン（blend 欠落）は `#[serde(default)]` により Additive になる。
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
    ///
    /// 消費者はレンダラ／compute 側（本クレート外の別担当）で、現時点では
    /// Rust 側に呼び出しが無いため dead_code を明示的に許可する。
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
///
/// serde は snake_case でシリアライズする（`"world"` / `"local"`）。
/// 旧シーン（sim_space 欠落）は `#[serde(default)]` により World になる。
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
    ///
    /// 消費者はレンダラ／compute 側（本クレート外の別担当）で、現時点では
    /// Rust 側に呼び出しが無いため dead_code を明示的に許可する。
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

// ─── ParticleEmitterComponentData（シリアライズ用）───────────

/// ParticleEmitterComponent のシリアライズ用データ（.scene 保存・Undo スナップショット）。
#[derive(Clone, Serialize, Deserialize, Debug)]
pub struct ParticleEmitterComponentData {
    /// パーティクル最大数（GPU プールサイズ）。上限は
    /// `MAX_PARTICLES_PER_EMITTER`（利用側で clamp）。既定 1024。
    #[serde(default = "default_max_particles")]
    pub max_particles: u32,
    /// 放出レート（1 秒あたりの放出個数）。既定 100.0。
    #[serde(default = "default_emit_rate")]
    pub emit_rate: f32,
    /// Play 開始時に一括放出する個数（バースト）。既定 0。
    #[serde(default)]
    pub burst: u32,
    /// 寿命 [min, max]（秒）。既定 [1.0, 2.0]。
    #[serde(default = "default_lifetime")]
    pub lifetime: [f32; 2],
    /// 初速 [min, max]。既定 [1.0, 3.0]。
    #[serde(default = "default_initial_speed")]
    pub initial_speed: [f32; 2],
    /// 放出円錐の半頂角（度）。既定 30.0。
    #[serde(default = "default_spread_angle_deg")]
    pub spread_angle_deg: f32,
    /// ローカル放出方向。既定 [0, 1, 0]（+Y 上向き）。
    #[serde(default = "default_direction_local")]
    pub direction_local: [f32; 3],
    /// 重力加速度。既定 [0, -9.8, 0]。
    #[serde(default = "default_gravity")]
    pub gravity: [f32; 3],
    /// 空気抵抗係数。既定 0.0。
    #[serde(default)]
    pub drag: f32,
    /// 開始サイズ [min, max]（ワールド単位）。既定 [0.1, 0.2]。
    #[serde(default = "default_start_size")]
    pub start_size: [f32; 2],
    /// 寿命末尾でのサイズ倍率（開始サイズに対する比）。既定 0.0（消えるように縮小）。
    #[serde(default)]
    pub end_size_scale: f32,
    /// 開始色（RGBA）。既定 [1, 1, 1, 1]。
    #[serde(default = "default_start_color")]
    pub start_color: [f32; 4],
    /// 終了色（RGBA）。既定 [1, 1, 1, 0]（白→透明）。
    #[serde(default = "default_end_color")]
    pub end_color: [f32; 4],
    /// テクスチャパス（assets:// 仮想パス）。空文字はシェーダ内プロシージャル白円。既定 ""。
    #[serde(default)]
    pub texture_path: String,
    /// 合成モード（additive / alpha）。既定 additive。
    #[serde(default)]
    pub blend: ParticleBlend,
    /// シミュレーション空間（world / local）。既定 world。
    #[serde(default)]
    pub sim_space: ParticleSimSpace,
    /// 放出中フラグ（Play 開始時に放出を開始するか）。既定 true。
    #[serde(default = "default_playing")]
    pub playing: bool,
    /// 寿命ループ放出フラグ（false なら一巡で停止）。既定 true。
    #[serde(default = "default_loop_emit")]
    pub loop_emit: bool,
}

impl Default for ParticleEmitterComponentData {
    fn default() -> Self {
        Self {
            max_particles:    default_max_particles(),
            emit_rate:        default_emit_rate(),
            burst:            0,
            lifetime:         default_lifetime(),
            initial_speed:    default_initial_speed(),
            spread_angle_deg: default_spread_angle_deg(),
            direction_local:  default_direction_local(),
            gravity:          default_gravity(),
            drag:             0.0,
            start_size:       default_start_size(),
            end_size_scale:   0.0,
            start_color:      default_start_color(),
            end_color:        default_end_color(),
            texture_path:     String::new(),
            blend:            ParticleBlend::default(),
            sim_space:        ParticleSimSpace::default(),
            playing:          default_playing(),
            loop_emit:        default_loop_emit(),
        }
    }
}

// ─── ParticleEmitterComponent（ECS 実体）─────────────────────

/// パーティクルエミッタコンポーネント（ECS 実体）。
///
/// 保持するのはエミッタのパラメータのみ。位置・向きは Actor の Transform から
/// レンダラが解決し、個々のパーティクルの生存状態や GPU バッファはレンダラが
/// 別管理する。揮発状態は持たない（Data と同一構成）。
#[derive(Clone, Debug)]
pub struct ParticleEmitterComponent {
    pub max_particles:    u32,
    pub emit_rate:        f32,
    pub burst:            u32,
    pub lifetime:         [f32; 2],
    pub initial_speed:    [f32; 2],
    pub spread_angle_deg: f32,
    pub direction_local:  [f32; 3],
    pub gravity:          [f32; 3],
    pub drag:             f32,
    pub start_size:       [f32; 2],
    pub end_size_scale:   f32,
    pub start_color:      [f32; 4],
    pub end_color:        [f32; 4],
    pub texture_path:     String,
    pub blend:            ParticleBlend,
    pub sim_space:        ParticleSimSpace,
    pub playing:          bool,
    pub loop_emit:        bool,
    /// スクリプト Burst(n) が積む「即時放出リクエスト数」（ランタイム専用・非シリアライズ）。
    ///
    /// スクリプト API（host_api）が加算し、GPU パーティクルシステムが毎フレーム
    /// 消費してゼロに戻す。シーン保存（to_data）には含めない。
    pub pending_burst:    u32,
}

impl ParticleEmitterComponent {
    /// シリアライズ用データからコンポーネントを構築する。
    pub fn from_data(data: ParticleEmitterComponentData) -> Self {
        Self {
            max_particles:    data.max_particles,
            emit_rate:        data.emit_rate,
            burst:            data.burst,
            lifetime:         data.lifetime,
            initial_speed:    data.initial_speed,
            spread_angle_deg: data.spread_angle_deg,
            direction_local:  data.direction_local,
            gravity:          data.gravity,
            drag:             data.drag,
            start_size:       data.start_size,
            end_size_scale:   data.end_size_scale,
            start_color:      data.start_color,
            end_color:        data.end_color,
            texture_path:     data.texture_path,
            blend:            data.blend,
            sim_space:        data.sim_space,
            playing:          data.playing,
            loop_emit:        data.loop_emit,
            // ランタイム専用の即時放出リクエストは常に 0 から開始する（非シリアライズ）。
            pending_burst:    0,
        }
    }

    /// シリアライズ用データへ変換する。
    pub fn to_data(&self) -> ParticleEmitterComponentData {
        ParticleEmitterComponentData {
            max_particles:    self.max_particles,
            emit_rate:        self.emit_rate,
            burst:            self.burst,
            lifetime:         self.lifetime,
            initial_speed:    self.initial_speed,
            spread_angle_deg: self.spread_angle_deg,
            direction_local:  self.direction_local,
            gravity:          self.gravity,
            drag:             self.drag,
            start_size:       self.start_size,
            end_size_scale:   self.end_size_scale,
            start_color:      self.start_color,
            end_color:        self.end_color,
            texture_path:     self.texture_path.clone(),
            blend:            self.blend,
            sim_space:        self.sim_space,
            playing:          self.playing,
            loop_emit:        self.loop_emit,
        }
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
    /// （旧 .scene のフィールド欠落に対する後方互換の担保）
    #[test]
    fn deserialize_empty_json_yields_defaults() {
        let data: ParticleEmitterComponentData =
            serde_json::from_str("{}").expect("空 JSON のデシリアライズに失敗");
        let def = ParticleEmitterComponentData::default();

        assert_eq!(data.max_particles,    def.max_particles);
        assert_eq!(data.emit_rate,        def.emit_rate);
        assert_eq!(data.burst,            def.burst);
        assert_eq!(data.lifetime,         def.lifetime);
        assert_eq!(data.initial_speed,    def.initial_speed);
        assert_eq!(data.spread_angle_deg, def.spread_angle_deg);
        assert_eq!(data.direction_local,  def.direction_local);
        assert_eq!(data.gravity,          def.gravity);
        assert_eq!(data.drag,             def.drag);
        assert_eq!(data.start_size,       def.start_size);
        assert_eq!(data.end_size_scale,   def.end_size_scale);
        assert_eq!(data.start_color,      def.start_color);
        assert_eq!(data.end_color,        def.end_color);
        assert_eq!(data.texture_path,     def.texture_path);
        assert_eq!(data.blend,            def.blend);
        assert_eq!(data.sim_space,        def.sim_space);
        assert_eq!(data.playing,          def.playing);
        assert_eq!(data.loop_emit,        def.loop_emit);
    }

    /// from_data → serialize → deserialize → to_data のラウンドトリップで
    /// 値が一致すること（非既定値を織り交ぜて検証）。
    #[test]
    fn roundtrip_preserves_values() {
        // 全フィールドに既定と異なる値を設定する。
        let original = ParticleEmitterComponentData {
            max_particles:    4096,
            emit_rate:        250.0,
            burst:            32,
            lifetime:         [0.5, 4.0],
            initial_speed:    [2.0, 5.0],
            spread_angle_deg: 45.0,
            direction_local:  [1.0, 0.0, 0.0],
            gravity:          [0.0, -1.0, 0.0],
            drag:             0.25,
            start_size:       [0.05, 0.5],
            end_size_scale:   2.0,
            start_color:      [0.2, 0.4, 0.6, 0.8],
            end_color:        [0.1, 0.2, 0.3, 0.0],
            texture_path:     "assets://fx/spark.png".to_string(),
            blend:            ParticleBlend::Alpha,
            sim_space:        ParticleSimSpace::Local,
            playing:          false,
            loop_emit:        false,
        };

        // from_data で ECS 実体を作り、to_data で戻す。
        let component = ParticleEmitterComponent::from_data(original.clone());
        let back = component.to_data();

        // to_data → serialize → deserialize → 再構築で JSON 経路も検証する。
        let json = serde_json::to_string(&back).expect("シリアライズに失敗");
        let restored: ParticleEmitterComponentData =
            serde_json::from_str(&json).expect("デシリアライズに失敗");

        assert_eq!(restored.max_particles,    original.max_particles);
        assert_eq!(restored.emit_rate,        original.emit_rate);
        assert_eq!(restored.burst,            original.burst);
        assert_eq!(restored.lifetime,         original.lifetime);
        assert_eq!(restored.initial_speed,    original.initial_speed);
        assert_eq!(restored.spread_angle_deg, original.spread_angle_deg);
        assert_eq!(restored.direction_local,  original.direction_local);
        assert_eq!(restored.gravity,          original.gravity);
        assert_eq!(restored.drag,             original.drag);
        assert_eq!(restored.start_size,       original.start_size);
        assert_eq!(restored.end_size_scale,   original.end_size_scale);
        assert_eq!(restored.start_color,      original.start_color);
        assert_eq!(restored.end_color,        original.end_color);
        assert_eq!(restored.texture_path,     original.texture_path);
        assert_eq!(restored.blend,            original.blend);
        assert_eq!(restored.sim_space,        original.sim_space);
        assert_eq!(restored.playing,          original.playing);
        assert_eq!(restored.loop_emit,        original.loop_emit);
    }
}
