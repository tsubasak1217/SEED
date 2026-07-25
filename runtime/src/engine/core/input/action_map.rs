// ============================================================
//  action_map.rs — .inputmap（入力アクションマップ）のパース＋評価（v2）
//
//  役割（単一責任）:
//    - .inputmap（JSON, v2）を serde でパースし、PC のバインディング
//      （Key / GamepadButton / GamepadAxis）を評価可能な形へ解決する。
//    - v1（version 欠落）を読み込んだ場合は内部で v2 へ移行する（後方互換）。
//    - アクションを Bool / Axis1D / Axis2D で評価する。
//
//  スキーマ v2 の要点:
//    - value_type: 0=Bool / 1=Axis1D / 2=Axis2D。
//    - Bool: condition（Trigger/Press/Release）を持つ。バインドは bindings（フラット）。
//    - Axis1D: positive / negative の 2 グループ。
//    - Axis2D: x{positive,negative} / y{positive,negative} と normalize フラグ。
//    - Binding: { platform, input_type(Key/GamepadButton/GamepadAxis), value, dead_zone }。
//      dead_zone はアナログ（GamepadAxis）のみ意味を持つ。
//
//  評価規則:
//    - 軸値 = clamp(Σ正バインド − Σ負バインド, -1, 1)。
//      デジタル（Key/Button）: 押下=1.0。アナログ（Axis）: dead_zone 適用後の符号付き生値。
//    - Bool 生値: いずれかのバインドがアクティブ（デジタル=押下 / アナログ=|dz適用後|>0）。
//    - Bool アクション: condition を適用（Trigger=立ち上がり / Press=押下中 / Release=立ち下がり）。
//    - Start/End: condition 適用後の値の立ち上がり/立ち下がり（フレーム履歴が必要 → ActionRuntime）。
//
//  状態（フレーム履歴）は ActionRuntime が持ち、呼び出し側（host_api）が
//  asset_path 単位で保持する。ActionMap 自体はステートレス（パース結果のみ）。
// ============================================================

use std::collections::HashMap;

use serde::Deserialize;
use winit::keyboard::KeyCode;

use super::gamepad::{PadAxis, PadButton, PadQuery};

// ─── 定数（マジックナンバー禁止方針）─────────────────────────

/// 評価対象のプラットフォーム（現状 PC のみ対応。パッドも PC プラットフォーム扱い）。
const PLATFORM_PC: &str = "PC";
/// 入力種別: 物理キー。
const INPUT_TYPE_KEY: &str = "Key";
/// 入力種別: ゲームパッドボタン（デジタル）。
const INPUT_TYPE_GAMEPAD_BUTTON: &str = "GamepadButton";
/// 入力種別: ゲームパッド軸（アナログ）。
const INPUT_TYPE_GAMEPAD_AXIS: &str = "GamepadAxis";
/// 入力種別: WASD 合成軸（v1 のみ。v2 移行時に正負バインドへ展開する）。
const INPUT_TYPE_WASD: &str = "WASD";
/// WASD 軸の値: 水平。
const WASD_HORIZONTAL: &str = "Horizontal";
/// WASD 軸の値: 垂直。
const WASD_VERTICAL: &str = "Vertical";

/// アクション値の型（エディタ ActionValueType と数値一致）。
const VALUE_TYPE_BOOL: i32 = 0;
const VALUE_TYPE_AXIS1D: i32 = 1;
const VALUE_TYPE_AXIS2D: i32 = 2;

/// アナログ軸の既定デッドゾーン。
///
/// 一般的なアナログスティックは中立でも ±0.1 程度の揺れ（ドリフト）が出るため、
/// それを無反応にしつつ操作感を損なわない値として 0.2 を採用する
/// （多くのゲームエンジンの既定と同程度）。省略時にこの値を使う。
pub const DEFAULT_DEAD_ZONE: f32 = 0.2;

// ─── 入力問い合わせの抽象化 ──────────────────────────────────

/// キー状態の問い合わせインターフェース（押下中かのみ）。
///
/// エッジ（押した/離した瞬間）はアクションレベルの condition / Start・End で
/// フレーム履歴（ActionRuntime）から算出するため、ここでは押下中判定のみを提供する。
pub trait KeyQuery {
    /// キー `key` が押されている間 true。
    fn is_key_pressed(&self, key: KeyCode) -> bool;
}

/// 実ランタイム入力（`Input`）を `KeyQuery` として使う。
impl KeyQuery for crate::engine::core::input::Input {
    fn is_key_pressed(&self, key: KeyCode) -> bool {
        self.is_press_key(key)
    }
}

/// 実ランタイム入力（`Input`）を `PadQuery` として使う（is_active でゲート）。
impl PadQuery for crate::engine::core::input::Input {
    fn is_button_pressed(&self, btn: PadButton) -> bool {
        self.is_active() && self.gamepad().is_press(btn)
    }
    fn axis_value(&self, axis: PadAxis) -> f32 {
        if self.is_active() {
            self.gamepad().axis(axis)
        } else {
            0.0
        }
    }
}

// ─── serde パース用の生データ型 ──────────────────────────────
//  全フィールドに #[serde(default)] を付け、欠落があっても丸ごと失敗しない。
//  version 欠落＝v1（後方互換）。

#[derive(Deserialize)]
struct RawFile {
    /// スキーマバージョン。欠落（None）＝v1。
    #[serde(default)]
    version: Option<u32>,
    #[serde(default)]
    actions: Vec<RawAction>,
}

#[derive(Deserialize)]
struct RawAction {
    #[serde(default)]
    name: String,
    #[serde(default)]
    value_type: i32,
    /// Bool のみ有効: "Trigger"/"Press"/"Release"。既定 Press。
    #[serde(default)]
    condition: Option<String>,
    /// Axis2D のみ有効: 長さ>1 のとき正規化。
    #[serde(default)]
    normalize: bool,
    /// Bool: フラットなバインドリスト（v1 は全型がここに入る）。
    #[serde(default)]
    bindings: Vec<RawBinding>,
    /// Axis1D: 正バインド。
    #[serde(default)]
    positive: Vec<RawBinding>,
    /// Axis1D: 負バインド。
    #[serde(default)]
    negative: Vec<RawBinding>,
    /// Axis2D: X 軸。
    #[serde(default)]
    x: Option<RawAxis>,
    /// Axis2D: Y 軸。
    #[serde(default)]
    y: Option<RawAxis>,
}

#[derive(Deserialize, Default)]
struct RawAxis {
    #[serde(default)]
    positive: Vec<RawBinding>,
    #[serde(default)]
    negative: Vec<RawBinding>,
}

#[derive(Deserialize)]
struct RawBinding {
    #[serde(default)]
    platform: String,
    #[serde(default)]
    input_type: String,
    #[serde(default)]
    value: String,
    /// アナログ（GamepadAxis）のみ意味を持つデッドゾーン。省略時 DEFAULT_DEAD_ZONE。
    #[serde(default)]
    dead_zone: Option<f32>,
}

// ─── 解決済みデータ型（評価に使う）──────────────────────────

/// アクション値の型（解決済み）。
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ValueType {
    Bool,
    Axis1D,
    Axis2D,
}

impl ValueType {
    /// エディタの数値（0/1/2）から変換する。未知値は Bool 扱い。
    fn from_i32(v: i32) -> Self {
        match v {
            VALUE_TYPE_AXIS1D => ValueType::Axis1D,
            VALUE_TYPE_AXIS2D => ValueType::Axis2D,
            _ => ValueType::Bool,
        }
    }
}

/// Bool アクションの条件（生値からアクション状態への変換規則）。
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Condition {
    /// 成立した瞬間（生値の立ち上がり）。
    Trigger,
    /// 押下中（生値そのまま）。
    Press,
    /// 離した瞬間（生値の立ち下がり）。
    Release,
}

impl Condition {
    /// 文字列から変換する。未知・欠落は既定の Press。
    fn from_opt(s: &Option<String>) -> Self {
        match s.as_deref() {
            Some("Trigger") => Condition::Trigger,
            Some("Release") => Condition::Release,
            _ => Condition::Press,
        }
    }
}

/// 解決済みの入力ソース（1 バインド）。各ソースは符号なしの寄与値を持つ。
#[derive(Debug, Clone, Copy, PartialEq)]
enum Source {
    /// 物理キー（デジタル: 押下=1.0）。
    Key(KeyCode),
    /// ゲームパッドボタン（デジタル: 押下=1.0）。
    Button(PadButton),
    /// ゲームパッド軸（アナログ: dead_zone 適用後の符号付き生値）。
    Axis { axis: PadAxis, dead_zone: f32 },
}

impl Source {
    /// このソースの符号付き寄与値を返す。
    /// デジタル=押下で 1.0 / 非押下 0.0。アナログ=dead_zone 適用後の生値（-1..1 or 0..1）。
    fn value(&self, keys: &impl KeyQuery, pad: &impl PadQuery) -> f32 {
        match self {
            Source::Key(k) => {
                if keys.is_key_pressed(*k) { 1.0 } else { 0.0 }
            }
            Source::Button(b) => {
                if pad.is_button_pressed(*b) { 1.0 } else { 0.0 }
            }
            Source::Axis { axis, dead_zone } => {
                apply_dead_zone(pad.axis_value(*axis), *dead_zone)
            }
        }
    }

    /// このソースがアクティブか（Bool 生値の判定用）。
    /// デジタル=押下 / アナログ=dead_zone 適用後が非ゼロ。
    fn active(&self, keys: &impl KeyQuery, pad: &impl PadQuery) -> bool {
        match self {
            Source::Key(k) => keys.is_key_pressed(*k),
            Source::Button(b) => pad.is_button_pressed(*b),
            Source::Axis { axis, dead_zone } => {
                apply_dead_zone(pad.axis_value(*axis), *dead_zone).abs() > 0.0
            }
        }
    }
}

/// 解決済みアクションの本体（型ごとにバインドグループを保持）。
#[derive(Debug, Clone)]
enum ActionBody {
    /// Bool: フラットなバインド群 + 条件。
    Bool { bindings: Vec<Source>, condition: Condition },
    /// Axis1D: 正/負バインド群。
    Axis1D { positive: Vec<Source>, negative: Vec<Source> },
    /// Axis2D: x/y の正/負バインド群 + 正規化フラグ。
    Axis2D {
        x_pos: Vec<Source>,
        x_neg: Vec<Source>,
        y_pos: Vec<Source>,
        y_neg: Vec<Source>,
        normalize: bool,
    },
}

/// 解決済みの 1 アクション。
#[derive(Debug, Clone)]
struct Action {
    name: String,
    body: ActionBody,
}

impl Action {
    /// Bool の生値（いずれかのバインドがアクティブ）を評価する。
    /// 軸アクションでも「いずれかのソースが非ゼロ」を生値として扱う（GetAction の保険）。
    fn raw_active(&self, keys: &impl KeyQuery, pad: &impl PadQuery) -> bool {
        match &self.body {
            ActionBody::Bool { bindings, .. } => bindings.iter().any(|s| s.active(keys, pad)),
            ActionBody::Axis1D { positive, negative } => {
                positive.iter().chain(negative).any(|s| s.active(keys, pad))
            }
            ActionBody::Axis2D { x_pos, x_neg, y_pos, y_neg, .. } => x_pos
                .iter()
                .chain(x_neg)
                .chain(y_pos)
                .chain(y_neg)
                .any(|s| s.active(keys, pad)),
        }
    }

    /// このアクションの条件（Bool のみ意味を持つ。軸は Press 相当で生値を返す）。
    fn condition(&self) -> Condition {
        match &self.body {
            ActionBody::Bool { condition, .. } => *condition,
            _ => Condition::Press,
        }
    }
}

/// パース＋解決済みのアクションマップ。評価はこの構造体のメソッドで行う。
#[derive(Debug, Clone, Default)]
pub struct ActionMap {
    actions: Vec<Action>,
}

// ─── アクションのフレーム履歴（Start/End・条件エッジ用）───────

/// 1 アクションの前フレーム状態。
#[derive(Debug, Clone, Copy)]
struct FrameState {
    /// 最後に評価したフレーム番号（同一フレーム内の再クエリを一貫させる）。
    last_frame: u64,
    /// 前フレームの生値。
    prev_raw: bool,
    /// 前フレームの条件適用後の値。
    prev_cond: bool,
    /// 現フレームの生値。
    cur_raw: bool,
    /// 現フレームの条件適用後の値。
    cur_cond: bool,
}

/// アクション状態のフレーム履歴キャッシュ。
///
/// (アクション名) → 前フレーム状態。asset_path 単位で host_api が保持する。
/// Trigger/Release 条件と Start/End エッジの算出にフレーム履歴が要るため分離する。
#[derive(Debug, Clone, Default)]
pub struct ActionRuntime {
    states: HashMap<String, FrameState>,
}

/// アクション評価結果（GetAction / GetActionStart / GetActionEnd に対応）。
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct ActionResult {
    /// 条件適用後のアクション状態（GetAction）。
    pub action: bool,
    /// アクション成立の瞬間（GetActionStart）。
    pub start: bool,
    /// アクション終了の瞬間（GetActionEnd）。
    pub end: bool,
}

impl ActionMap {
    /// 空のマップ（パース失敗時のフォールバック）。
    pub fn empty() -> Self {
        Self { actions: Vec::new() }
    }

    /// JSON 文字列をパースして ActionMap を構築する（v2。version 欠落は v1 として移行）。
    pub fn parse(json: &str) -> Self {
        let raw: RawFile = match serde_json::from_str(json) {
            Ok(r) => r,
            Err(e) => {
                eprintln!("[SEED script] InputMap パース失敗: {e}");
                return Self::empty();
            }
        };

        let actions = raw.actions.into_iter().map(resolve_action).collect();
        Self { actions }
    }

    /// 名前一致のアクションを引く。
    fn find(&self, name: &str) -> Option<&Action> {
        self.actions.iter().find(|a| a.name == name)
    }

    /// Bool アクションを評価する（condition + Start/End をフレーム履歴から算出）。
    ///
    /// - `frame` はグローバルフレーム番号（同一フレーム内の複数クエリを一貫させる）。
    /// - アクションが無い場合は全 false。
    pub fn eval_action(
        &self,
        rt: &mut ActionRuntime,
        keys: &impl KeyQuery,
        pad: &impl PadQuery,
        name: &str,
        frame: u64,
    ) -> ActionResult {
        let Some(action) = self.find(name) else {
            return ActionResult { action: false, start: false, end: false };
        };

        let raw = action.raw_active(keys, pad);
        let cond = action.condition();

        let entry = rt.states.entry(name.to_string()).or_insert(FrameState {
            last_frame: u64::MAX, // 未評価マーカー（最初のフレームで必ずシフトさせる）
            prev_raw: false,
            prev_cond: false,
            cur_raw: false,
            cur_cond: false,
        });

        // 新しいフレームなら履歴をシフトして再計算する。同一フレーム内は結果を再利用する。
        if entry.last_frame != frame {
            // 前フレームの current を prev へ退避（初回は既定 false のまま）。
            entry.prev_raw = entry.cur_raw;
            entry.prev_cond = entry.cur_cond;

            // 条件を適用して現フレームの条件適用後値を求める。
            let cur_cond = match cond {
                Condition::Press => raw,
                Condition::Trigger => raw && !entry.prev_raw, // 生値の立ち上がり
                Condition::Release => !raw && entry.prev_raw, // 生値の立ち下がり
            };

            entry.cur_raw = raw;
            entry.cur_cond = cur_cond;
            entry.last_frame = frame;
        }

        // Start/End は条件適用後の値のエッジ。
        let start = entry.cur_cond && !entry.prev_cond;
        let end = !entry.cur_cond && entry.prev_cond;
        ActionResult { action: entry.cur_cond, start, end }
    }

    /// Axis1D アクションを評価する（[-1, 1]）。
    pub fn eval_axis1d(&self, keys: &impl KeyQuery, pad: &impl PadQuery, name: &str) -> f32 {
        let Some(action) = self.find(name) else { return 0.0 };
        match &action.body {
            ActionBody::Axis1D { positive, negative } => eval_group(positive, negative, keys, pad),
            // 型不一致でも合理的に評価する（Bool の生値を 0/1 で返す）。
            ActionBody::Bool { bindings, .. } => {
                if bindings.iter().any(|s| s.active(keys, pad)) { 1.0 } else { 0.0 }
            }
            ActionBody::Axis2D { x_pos, x_neg, .. } => eval_group(x_pos, x_neg, keys, pad),
        }
    }

    /// Axis2D アクションを評価する（各成分 [-1, 1]。normalize なら長さ>1 で正規化）。
    pub fn eval_axis2d(&self, keys: &impl KeyQuery, pad: &impl PadQuery, name: &str) -> [f32; 2] {
        let Some(action) = self.find(name) else { return [0.0, 0.0] };
        match &action.body {
            ActionBody::Axis2D { x_pos, x_neg, y_pos, y_neg, normalize } => {
                let x = eval_group(x_pos, x_neg, keys, pad);
                let y = eval_group(y_pos, y_neg, keys, pad);
                if *normalize {
                    normalize_if_long(x, y)
                } else {
                    [x, y]
                }
            }
            ActionBody::Axis1D { positive, negative } => {
                [eval_group(positive, negative, keys, pad), 0.0]
            }
            ActionBody::Bool { .. } => [0.0, 0.0],
        }
    }
}

// ─── 評価ヘルパー ────────────────────────────────────────────

/// 正/負グループから軸値を求める: clamp(Σpos − Σneg, -1, 1)。
fn eval_group(
    positive: &[Source],
    negative: &[Source],
    keys: &impl KeyQuery,
    pad: &impl PadQuery,
) -> f32 {
    let mut v = 0.0;
    for s in positive {
        v += s.value(keys, pad);
    }
    for s in negative {
        v -= s.value(keys, pad);
    }
    v.clamp(-1.0, 1.0)
}

/// デッドゾーン適用（カクつかない標準式）。
///
/// |v| < dz なら 0。それ以外は (|v| - dz) / (1 - dz) にリスケールして符号を復元する。
/// これにより dz 直上で 0 から滑らかに立ち上がる（段差が出ない）。
fn apply_dead_zone(v: f32, dead_zone: f32) -> f32 {
    // dz は [0, 1) に収める（1 以上だと 0 除算・全域無反応になるため）。
    let dz = dead_zone.clamp(0.0, 0.999);
    let a = v.abs();
    if a < dz {
        0.0
    } else {
        ((a - dz) / (1.0 - dz)) * v.signum()
    }
}

/// 長さが 1 を超える場合のみ (x, y) を単位長へ正規化する。
///
/// これにより斜めキーボード入力（1,1）が長さ √2 → 0.707 に収まる。
fn normalize_if_long(x: f32, y: f32) -> [f32; 2] {
    let len2 = x * x + y * y;
    if len2 > 1.0 {
        let len = len2.sqrt();
        [x / len, y / len]
    } else {
        [x, y]
    }
}

// ─── アクション解決（v2 パース + v1 移行）─────────────────────

/// 生アクションを解決済み Action へ変換する。
///
/// value_type に応じて適切な v2 フィールド（bindings / positive・negative / x・y）を読む。
/// さらに `bindings` に残る v1 バインド（Key / WASD）を型に応じて移行する。
fn resolve_action(a: RawAction) -> Action {
    let value_type = ValueType::from_i32(a.value_type);

    let body = match value_type {
        ValueType::Bool => {
            // Bool: bindings のデジタル/アナログソースを集める（WASD は Bool では無意味）。
            let bindings = a.bindings.iter().filter_map(resolve_source).collect();
            ActionBody::Bool { bindings, condition: Condition::from_opt(&a.condition) }
        }
        ValueType::Axis1D => {
            let mut positive: Vec<Source> = a.positive.iter().filter_map(resolve_source).collect();
            let mut negative: Vec<Source> = a.negative.iter().filter_map(resolve_source).collect();
            // v1 移行: bindings 内の WASD / Key を正負へ展開する。
            migrate_axis1d_bindings(&a.bindings, &mut positive, &mut negative);
            ActionBody::Axis1D { positive, negative }
        }
        ValueType::Axis2D => {
            let x = a.x.unwrap_or_default();
            let y = a.y.unwrap_or_default();
            let mut x_pos: Vec<Source> = x.positive.iter().filter_map(resolve_source).collect();
            let mut x_neg: Vec<Source> = x.negative.iter().filter_map(resolve_source).collect();
            let mut y_pos: Vec<Source> = y.positive.iter().filter_map(resolve_source).collect();
            let mut y_neg: Vec<Source> = y.negative.iter().filter_map(resolve_source).collect();
            // v1 移行: bindings 内の WASD を x/y の正負へ展開する。
            migrate_axis2d_bindings(&a.bindings, &mut x_pos, &mut x_neg, &mut y_pos, &mut y_neg);
            ActionBody::Axis2D { x_pos, x_neg, y_pos, y_neg, normalize: a.normalize }
        }
    };

    Action { name: a.name, body }
}

/// 生バインディング（Key / GamepadButton / GamepadAxis）を Source へ解決する。
/// PC 以外・WASD・未対応は None（WASD は移行ヘルパーが別途処理する）。
fn resolve_source(b: &RawBinding) -> Option<Source> {
    if b.platform != PLATFORM_PC {
        return None;
    }
    match b.input_type.as_str() {
        INPUT_TYPE_KEY => key_from_name(&b.value).map(Source::Key).or_else(|| {
            eprintln!("[SEED script] InputMap: 未対応のキー名 '{}'（無反応）", b.value);
            None
        }),
        INPUT_TYPE_GAMEPAD_BUTTON => PadButton::from_name(&b.value).map(Source::Button).or_else(|| {
            eprintln!("[SEED script] InputMap: 未対応のパッドボタン '{}'（無反応）", b.value);
            None
        }),
        INPUT_TYPE_GAMEPAD_AXIS => PadAxis::from_name(&b.value).map(|axis| Source::Axis {
            axis,
            dead_zone: b.dead_zone.unwrap_or(DEFAULT_DEAD_ZONE),
        }),
        // WASD は移行ヘルパー、その他（VirtualButton 等）は基盤なしのため無視。
        _ => None,
    }
}

/// v1 の Axis1D バインド（bindings）を正負グループへ移行する。
///
/// - WASD Horizontal → positive += [D, →], negative += [A, ←]。
/// - WASD Vertical   → positive += [W, ↑], negative += [S, ↓]。
/// - 素の Key        → positive（v1 の「Key 押下で +1」挙動を維持）。
fn migrate_axis1d_bindings(bindings: &[RawBinding], positive: &mut Vec<Source>, negative: &mut Vec<Source>) {
    for b in bindings {
        if b.platform != PLATFORM_PC {
            continue;
        }
        match b.input_type.as_str() {
            INPUT_TYPE_WASD => expand_wasd(&b.value, positive, negative),
            INPUT_TYPE_KEY => {
                if let Some(k) = key_from_name(&b.value) {
                    positive.push(Source::Key(k));
                }
            }
            _ => {}
        }
    }
}

/// v1 の Axis2D バインド（bindings）を x/y の正負グループへ移行する。
///
/// - WASD Horizontal → x（D/→ 正, A/← 負）。
/// - WASD Vertical   → y（W/↑ 正, S/↓ 負）。
/// - 素の Key は v1 の eval_vector2 が無視していたため移行しない。
fn migrate_axis2d_bindings(
    bindings: &[RawBinding],
    x_pos: &mut Vec<Source>,
    x_neg: &mut Vec<Source>,
    y_pos: &mut Vec<Source>,
    y_neg: &mut Vec<Source>,
) {
    for b in bindings {
        if b.platform != PLATFORM_PC || b.input_type != INPUT_TYPE_WASD {
            continue;
        }
        match b.value.as_str() {
            WASD_HORIZONTAL => expand_wasd(WASD_HORIZONTAL, x_pos, x_neg),
            WASD_VERTICAL => expand_wasd(WASD_VERTICAL, y_pos, y_neg),
            _ => {}
        }
    }
}

/// WASD 合成軸を正負のキーソースへ展開する（矢印キーも同時に有効）。
fn expand_wasd(value: &str, positive: &mut Vec<Source>, negative: &mut Vec<Source>) {
    match value {
        WASD_HORIZONTAL => {
            positive.push(Source::Key(KeyCode::KeyD));
            positive.push(Source::Key(KeyCode::ArrowRight));
            negative.push(Source::Key(KeyCode::KeyA));
            negative.push(Source::Key(KeyCode::ArrowLeft));
        }
        WASD_VERTICAL => {
            positive.push(Source::Key(KeyCode::KeyW));
            positive.push(Source::Key(KeyCode::ArrowUp));
            negative.push(Source::Key(KeyCode::KeyS));
            negative.push(Source::Key(KeyCode::ArrowDown));
        }
        _ => {}
    }
}

/// エディタのキー名（"Space" / "LeftShift" / "Q" / "Alpha0" / "Keypad0" / "UpArrow" …）を
/// winit KeyCode へ対応させる。正典はエディタ側 InputMapEditorWindow の Key 一覧。
fn key_from_name(name: &str) -> Option<KeyCode> {
    Some(match name {
        // ── 特殊キー ──
        "Space" => KeyCode::Space,
        "Enter" => KeyCode::Enter,
        "Tab" => KeyCode::Tab,
        "Backspace" => KeyCode::Backspace,
        "Escape" => KeyCode::Escape,
        "Delete" => KeyCode::Delete,
        // ── アルファベット ──
        "A" => KeyCode::KeyA,
        "B" => KeyCode::KeyB,
        "C" => KeyCode::KeyC,
        "D" => KeyCode::KeyD,
        "E" => KeyCode::KeyE,
        "F" => KeyCode::KeyF,
        "G" => KeyCode::KeyG,
        "H" => KeyCode::KeyH,
        "I" => KeyCode::KeyI,
        "J" => KeyCode::KeyJ,
        "K" => KeyCode::KeyK,
        "L" => KeyCode::KeyL,
        "M" => KeyCode::KeyM,
        "N" => KeyCode::KeyN,
        "O" => KeyCode::KeyO,
        "P" => KeyCode::KeyP,
        "Q" => KeyCode::KeyQ,
        "R" => KeyCode::KeyR,
        "S" => KeyCode::KeyS,
        "T" => KeyCode::KeyT,
        "U" => KeyCode::KeyU,
        "V" => KeyCode::KeyV,
        "W" => KeyCode::KeyW,
        "X" => KeyCode::KeyX,
        "Y" => KeyCode::KeyY,
        "Z" => KeyCode::KeyZ,
        // ── 数字（上段）Alpha0..Alpha9 ──
        "Alpha0" => KeyCode::Digit0,
        "Alpha1" => KeyCode::Digit1,
        "Alpha2" => KeyCode::Digit2,
        "Alpha3" => KeyCode::Digit3,
        "Alpha4" => KeyCode::Digit4,
        "Alpha5" => KeyCode::Digit5,
        "Alpha6" => KeyCode::Digit6,
        "Alpha7" => KeyCode::Digit7,
        "Alpha8" => KeyCode::Digit8,
        "Alpha9" => KeyCode::Digit9,
        // ── ファンクションキー ──
        "F1" => KeyCode::F1,
        "F2" => KeyCode::F2,
        "F3" => KeyCode::F3,
        "F4" => KeyCode::F4,
        "F5" => KeyCode::F5,
        "F6" => KeyCode::F6,
        "F7" => KeyCode::F7,
        "F8" => KeyCode::F8,
        "F9" => KeyCode::F9,
        "F10" => KeyCode::F10,
        "F11" => KeyCode::F11,
        "F12" => KeyCode::F12,
        // ── 矢印キー ──
        "UpArrow" => KeyCode::ArrowUp,
        "DownArrow" => KeyCode::ArrowDown,
        "LeftArrow" => KeyCode::ArrowLeft,
        "RightArrow" => KeyCode::ArrowRight,
        // ── 修飾キー ──
        "LeftShift" => KeyCode::ShiftLeft,
        "RightShift" => KeyCode::ShiftRight,
        "LeftCtrl" => KeyCode::ControlLeft,
        "RightCtrl" => KeyCode::ControlRight,
        "LeftAlt" => KeyCode::AltLeft,
        "RightAlt" => KeyCode::AltRight,
        // ── テンキー Keypad0..Keypad9 ──
        "Keypad0" => KeyCode::Numpad0,
        "Keypad1" => KeyCode::Numpad1,
        "Keypad2" => KeyCode::Numpad2,
        "Keypad3" => KeyCode::Numpad3,
        "Keypad4" => KeyCode::Numpad4,
        "Keypad5" => KeyCode::Numpad5,
        "Keypad6" => KeyCode::Numpad6,
        "Keypad7" => KeyCode::Numpad7,
        "Keypad8" => KeyCode::Numpad8,
        "Keypad9" => KeyCode::Numpad9,
        _ => return None,
    })
}

// ============================================================
//  ユニットテスト
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;
    use std::collections::HashSet;

    /// テスト用のキー押下集合を注入するダミー KeyQuery（押下中のみ）。
    struct FakeKeys {
        pressed: HashSet<u32>,
    }
    impl FakeKeys {
        fn new() -> Self {
            Self { pressed: HashSet::new() }
        }
        fn id(key: KeyCode) -> u32 {
            use std::hash::{Hash, Hasher};
            let mut h = std::collections::hash_map::DefaultHasher::new();
            format!("{key:?}").hash(&mut h);
            h.finish() as u32
        }
        fn with(mut self, key: KeyCode) -> Self {
            self.pressed.insert(Self::id(key));
            self
        }
    }
    impl KeyQuery for FakeKeys {
        fn is_key_pressed(&self, key: KeyCode) -> bool {
            self.pressed.contains(&Self::id(key))
        }
    }

    /// テスト用のパッド状態を注入するダミー PadQuery。
    #[derive(Default)]
    struct FakePad {
        buttons: HashSet<u32>,
        axes: HashMap<u32, f32>,
    }
    impl FakePad {
        fn bid(b: PadButton) -> u32 {
            b as u32
        }
        fn aid(a: PadAxis) -> u32 {
            a as u32
        }
        fn with_button(mut self, b: PadButton) -> Self {
            self.buttons.insert(Self::bid(b));
            self
        }
        fn with_axis(mut self, a: PadAxis, v: f32) -> Self {
            self.axes.insert(Self::aid(a), v);
            self
        }
    }
    impl PadQuery for FakePad {
        fn is_button_pressed(&self, btn: PadButton) -> bool {
            self.buttons.contains(&Self::bid(btn))
        }
        fn axis_value(&self, axis: PadAxis) -> f32 {
            *self.axes.get(&Self::aid(axis)).unwrap_or(&0.0)
        }
    }

    /// 押下なしのダミーパッド。
    fn no_pad() -> FakePad {
        FakePad::default()
    }

    // ─── v1 後方互換 ─────────────────────────────────────────

    /// v1（version 欠落）の Bool/Key をそのまま読めること。
    #[test]
    fn v1_bool_key_migrates() {
        let json = r#"{"actions":[{"name":"Jump","value_type":0,
            "bindings":[{"platform":"PC","input_type":"Key","value":"Space"}]}]}"#;
        let map = ActionMap::parse(json);
        let mut rt = ActionRuntime::default();
        let keys = FakeKeys::new().with(KeyCode::Space);
        let r = map.eval_action(&mut rt, &keys, &no_pad(), "Jump", 1);
        assert!(r.action);
        let idle = FakeKeys::new();
        let r2 = map.eval_action(&mut rt, &idle, &no_pad(), "Jump", 2);
        assert!(!r2.action);
    }

    /// v1 の Vector2 + WASD が Axis2D + x/y 正負へ移行されること（D+W → (+1,+1)）。
    #[test]
    fn v1_vector2_wasd_migrates() {
        let json = r#"{"actions":[{"name":"Move","value_type":2,
            "bindings":[
                {"platform":"PC","input_type":"WASD","value":"Horizontal"},
                {"platform":"PC","input_type":"WASD","value":"Vertical"}
            ]}]}"#;
        let map = ActionMap::parse(json);
        let keys = FakeKeys::new().with(KeyCode::KeyD).with(KeyCode::KeyW);
        assert_eq!(map.eval_axis2d(&keys, &no_pad(), "Move"), [1.0, 1.0]);
        // A+S → (-1, -1)
        let keys2 = FakeKeys::new().with(KeyCode::KeyA).with(KeyCode::KeyS);
        assert_eq!(map.eval_axis2d(&keys2, &no_pad(), "Move"), [-1.0, -1.0]);
    }

    /// v1 の Axis1D + WASD Horizontal が positive/negative へ移行されること。
    #[test]
    fn v1_axis1d_wasd_migrates() {
        let json = r#"{"actions":[{"name":"Steer","value_type":1,
            "bindings":[{"platform":"PC","input_type":"WASD","value":"Horizontal"}]}]}"#;
        let map = ActionMap::parse(json);
        assert_eq!(map.eval_axis1d(&FakeKeys::new().with(KeyCode::KeyD), &no_pad(), "Steer"), 1.0);
        assert_eq!(map.eval_axis1d(&FakeKeys::new().with(KeyCode::KeyA), &no_pad(), "Steer"), -1.0);
        assert_eq!(map.eval_axis1d(&FakeKeys::new().with(KeyCode::ArrowRight), &no_pad(), "Steer"), 1.0);
    }

    // ─── 条件（Trigger/Press/Release）─────────────────────────

    /// Press（既定）: 生値が true の間 true。
    #[test]
    fn condition_press() {
        let json = r#"{"version":2,"actions":[{"name":"Fire","value_type":0,"condition":"Press",
            "bindings":[{"platform":"PC","input_type":"Key","value":"Q"}]}]}"#;
        let map = ActionMap::parse(json);
        let mut rt = ActionRuntime::default();
        let held = FakeKeys::new().with(KeyCode::KeyQ);
        assert!(map.eval_action(&mut rt, &held, &no_pad(), "Fire", 1).action);
        assert!(map.eval_action(&mut rt, &held, &no_pad(), "Fire", 2).action); // 押しっぱなしでも true
    }

    /// Trigger: 生値の立ち上がりフレームのみ true。
    #[test]
    fn condition_trigger() {
        let json = r#"{"version":2,"actions":[{"name":"Fire","value_type":0,"condition":"Trigger",
            "bindings":[{"platform":"PC","input_type":"Key","value":"Q"}]}]}"#;
        let map = ActionMap::parse(json);
        let mut rt = ActionRuntime::default();
        let held = FakeKeys::new().with(KeyCode::KeyQ);
        let idle = FakeKeys::new();
        // frame1: idle（false）
        assert!(!map.eval_action(&mut rt, &idle, &no_pad(), "Fire", 1).action);
        // frame2: 押した瞬間 → true
        assert!(map.eval_action(&mut rt, &held, &no_pad(), "Fire", 2).action);
        // frame3: 押しっぱなし → false（立ち上がりではない）
        assert!(!map.eval_action(&mut rt, &held, &no_pad(), "Fire", 3).action);
    }

    /// Release: 生値の立ち下がりフレームのみ true。
    #[test]
    fn condition_release() {
        let json = r#"{"version":2,"actions":[{"name":"Fire","value_type":0,"condition":"Release",
            "bindings":[{"platform":"PC","input_type":"Key","value":"Q"}]}]}"#;
        let map = ActionMap::parse(json);
        let mut rt = ActionRuntime::default();
        let held = FakeKeys::new().with(KeyCode::KeyQ);
        let idle = FakeKeys::new();
        assert!(!map.eval_action(&mut rt, &held, &no_pad(), "Fire", 1).action); // 押下中
        assert!(map.eval_action(&mut rt, &idle, &no_pad(), "Fire", 2).action); // 離した瞬間 → true
        assert!(!map.eval_action(&mut rt, &idle, &no_pad(), "Fire", 3).action); // 離しっぱなし → false
    }

    // ─── Start / End エッジ ──────────────────────────────────

    /// Start/End が condition 適用後の値のエッジになること（Press 条件）。
    #[test]
    fn start_end_edges() {
        let json = r#"{"version":2,"actions":[{"name":"Hold","value_type":0,"condition":"Press",
            "bindings":[{"platform":"PC","input_type":"Key","value":"Q"}]}]}"#;
        let map = ActionMap::parse(json);
        let mut rt = ActionRuntime::default();
        let held = FakeKeys::new().with(KeyCode::KeyQ);
        let idle = FakeKeys::new();

        let f1 = map.eval_action(&mut rt, &idle, &no_pad(), "Hold", 1);
        assert!(!f1.start && !f1.end);
        let f2 = map.eval_action(&mut rt, &held, &no_pad(), "Hold", 2);
        assert!(f2.start && !f2.end); // 立ち上がり
        let f3 = map.eval_action(&mut rt, &held, &no_pad(), "Hold", 3);
        assert!(!f3.start && !f3.end); // 継続
        let f4 = map.eval_action(&mut rt, &idle, &no_pad(), "Hold", 4);
        assert!(!f4.start && f4.end); // 立ち下がり
    }

    /// 同一フレーム内の複数クエリが一貫すること（履歴を二重に進めない）。
    #[test]
    fn same_frame_consistent() {
        let json = r#"{"version":2,"actions":[{"name":"Fire","value_type":0,"condition":"Trigger",
            "bindings":[{"platform":"PC","input_type":"Key","value":"Q"}]}]}"#;
        let map = ActionMap::parse(json);
        let mut rt = ActionRuntime::default();
        let idle = FakeKeys::new();
        let held = FakeKeys::new().with(KeyCode::KeyQ);
        map.eval_action(&mut rt, &idle, &no_pad(), "Fire", 1);
        // frame2 を同一フレームで 3 回クエリ → すべて true（履歴が進まない）
        assert!(map.eval_action(&mut rt, &held, &no_pad(), "Fire", 2).action);
        assert!(map.eval_action(&mut rt, &held, &no_pad(), "Fire", 2).action);
        assert!(map.eval_action(&mut rt, &held, &no_pad(), "Fire", 2).action);
        // frame3 で継続 → Trigger は false
        assert!(!map.eval_action(&mut rt, &held, &no_pad(), "Fire", 3).action);
    }

    // ─── dead_zone / 正負合成 / Axis2D 正規化 / パッド ───────

    /// dead_zone リスケール: |v|<dz は 0、dz 直上は 0 から立ち上がる。
    #[test]
    fn dead_zone_rescale() {
        // dz=0.2。生値 0.1 → 0、0.2 → 0、0.6 → (0.6-0.2)/0.8 = 0.5
        assert_eq!(apply_dead_zone(0.1, 0.2), 0.0);
        assert_eq!(apply_dead_zone(0.2, 0.2), 0.0);
        assert!((apply_dead_zone(0.6, 0.2) - 0.5).abs() < 1e-6);
        // 符号復元
        assert!((apply_dead_zone(-0.6, 0.2) + 0.5).abs() < 1e-6);
    }

    /// GamepadAxis の符号付き生値が正バインドで全域通ること（PadQuery 注入）。
    #[test]
    fn gamepad_axis_signed_passthrough() {
        let json = r#"{"version":2,"actions":[{"name":"Steer","value_type":1,
            "positive":[{"platform":"PC","input_type":"GamepadAxis","value":"LeftStickX","dead_zone":0.1}]}]}"#;
        let map = ActionMap::parse(json);
        let keys = FakeKeys::new();
        // +0.55 → (0.55-0.1)/0.9 = 0.5
        let pad_pos = no_pad().with_axis(PadAxis::LeftStickX, 0.55);
        assert!((map.eval_axis1d(&keys, &pad_pos, "Steer") - 0.5).abs() < 1e-6);
        // -0.55 → -0.5（符号付きで負方向も通る）
        let pad_neg = no_pad().with_axis(PadAxis::LeftStickX, -0.55);
        assert!((map.eval_axis1d(&keys, &pad_neg, "Steer") + 0.5).abs() < 1e-6);
    }

    /// 正負バインドの合成（正 D + 負 A の相殺）。
    #[test]
    fn positive_negative_compose() {
        let json = r#"{"version":2,"actions":[{"name":"Steer","value_type":1,
            "positive":[{"platform":"PC","input_type":"Key","value":"D"}],
            "negative":[{"platform":"PC","input_type":"Key","value":"A"}]}]}"#;
        let map = ActionMap::parse(json);
        assert_eq!(map.eval_axis1d(&FakeKeys::new().with(KeyCode::KeyD), &no_pad(), "Steer"), 1.0);
        assert_eq!(map.eval_axis1d(&FakeKeys::new().with(KeyCode::KeyA), &no_pad(), "Steer"), -1.0);
        let both = FakeKeys::new().with(KeyCode::KeyD).with(KeyCode::KeyA);
        assert_eq!(map.eval_axis1d(&both, &no_pad(), "Steer"), 0.0);
    }

    /// Axis2D normalize: 斜め (1,1) が正規化で 0.707 になること。
    #[test]
    fn axis2d_normalize() {
        let json = r#"{"version":2,"actions":[{"name":"Move","value_type":2,"normalize":true,
            "x":{"positive":[{"platform":"PC","input_type":"Key","value":"D"}]},
            "y":{"positive":[{"platform":"PC","input_type":"Key","value":"W"}]}}]}"#;
        let map = ActionMap::parse(json);
        let keys = FakeKeys::new().with(KeyCode::KeyD).with(KeyCode::KeyW);
        let v = map.eval_axis2d(&keys, &no_pad(), "Move");
        assert!((v[0] - 0.70710677).abs() < 1e-5);
        assert!((v[1] - 0.70710677).abs() < 1e-5);
        // 軸単独は正規化しない（長さ1以下）
        let only_x = FakeKeys::new().with(KeyCode::KeyD);
        assert_eq!(map.eval_axis2d(&only_x, &no_pad(), "Move"), [1.0, 0.0]);
    }

    /// normalize=false なら斜めが (1,1) のまま（クランプ後）。
    #[test]
    fn axis2d_no_normalize() {
        let json = r#"{"version":2,"actions":[{"name":"Move","value_type":2,"normalize":false,
            "x":{"positive":[{"platform":"PC","input_type":"Key","value":"D"}]},
            "y":{"positive":[{"platform":"PC","input_type":"Key","value":"W"}]}}]}"#;
        let map = ActionMap::parse(json);
        let keys = FakeKeys::new().with(KeyCode::KeyD).with(KeyCode::KeyW);
        assert_eq!(map.eval_axis2d(&keys, &no_pad(), "Move"), [1.0, 1.0]);
    }

    /// GamepadButton の Bool 評価（PadQuery 注入）。
    #[test]
    fn gamepad_button_bool() {
        let json = r#"{"version":2,"actions":[{"name":"Jump","value_type":0,
            "bindings":[{"platform":"PC","input_type":"GamepadButton","value":"South"}]}]}"#;
        let map = ActionMap::parse(json);
        let mut rt = ActionRuntime::default();
        let pad = no_pad().with_button(PadButton::South);
        assert!(map.eval_action(&mut rt, &FakeKeys::new(), &pad, "Jump", 1).action);
        assert!(!map.eval_action(&mut rt, &FakeKeys::new(), &no_pad(), "Jump", 2).action);
    }

    // ─── エラー・未知フィールド ──────────────────────────────

    /// 壊れた JSON は空マップ（パニックしない）。
    #[test]
    fn broken_json_is_empty() {
        let map = ActionMap::parse("{ this is not json ");
        let mut rt = ActionRuntime::default();
        assert!(!map.eval_action(&mut rt, &FakeKeys::new(), &no_pad(), "X", 1).action);
    }

    /// 未知フィールドを含む JSON も許容してパースできること。
    #[test]
    fn unknown_fields_allowed() {
        let json = r#"{"version":2,"actions":[{"name":"Jump","value_type":0,"extra":true,
            "bindings":[{"platform":"PC","input_type":"Key","value":"Space","note":"x"}]}]}"#;
        let map = ActionMap::parse(json);
        let mut rt = ActionRuntime::default();
        let keys = FakeKeys::new().with(KeyCode::Space);
        assert!(map.eval_action(&mut rt, &keys, &no_pad(), "Jump", 1).action);
    }

    /// PC 以外は無視されること。
    #[test]
    fn ignores_non_pc() {
        let json = r#"{"version":2,"actions":[{"name":"Jump","value_type":0,
            "bindings":[{"platform":"PS5","input_type":"GamepadButton","value":"South"}]}]}"#;
        let map = ActionMap::parse(json);
        let mut rt = ActionRuntime::default();
        let pad = no_pad().with_button(PadButton::South);
        assert!(!map.eval_action(&mut rt, &FakeKeys::new(), &pad, "Jump", 1).action);
    }
}
