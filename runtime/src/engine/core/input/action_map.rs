// ============================================================
//  action_map.rs — .inputmap（入力アクションマップ）のパース＋評価
//
//  役割（単一責任）:
//    - .inputmap（JSON）を serde でパースし、PC プラットフォームのバインディングを
//      ランタイムで評価可能な形（KeyCode / WASD 合成軸）へ解決する。
//    - アクション名を指定した評価（Bool / Axis1D / Vector2）を提供する。
//
//  データドリブン方針:
//    - キー名（"Space" 等）→ winit KeyCode の対応表はエディタ側の選択肢
//      （editor/src/InputMap/InputMapEditorWindow.xaml.cs の [("PC","Key")] 一覧）と
//      1:1 で一致させる。ここが正典。
//    - ゲームパッド系（GamepadButton / GamepadAxis）や PC 以外のプラットフォームは
//      現状ランタイム基盤が無いため無視する（docs に明記）。
//
//  キャッシュは呼び出し側（host_api.rs）が asset_path をキーに保持する。
//  このモジュールはステートレス（パース結果 = ActionMap のみ）に保つ。
// ============================================================

use serde::Deserialize;
use winit::keyboard::KeyCode;

// ─── 定数（マジックナンバー禁止方針）─────────────────────────

/// 評価対象のプラットフォーム（現状 PC のみ対応）。
const PLATFORM_PC: &str = "PC";
/// 入力種別: 物理キー。
const INPUT_TYPE_KEY: &str = "Key";
/// 入力種別: WASD 合成軸（値に Horizontal / Vertical を取る）。
const INPUT_TYPE_WASD: &str = "WASD";
/// WASD 軸の値: 水平（A/← = -1, D/→ = +1）。
const WASD_HORIZONTAL: &str = "Horizontal";
/// WASD 軸の値: 垂直（S/↓ = -1, W/↑ = +1）。
const WASD_VERTICAL: &str = "Vertical";

/// アクション値の型（エディタ ActionValueType と数値一致）。
const VALUE_TYPE_BOOL: i32 = 0;
const VALUE_TYPE_AXIS1D: i32 = 1;
const VALUE_TYPE_VECTOR2: i32 = 2;

/// Bool アクションの判定種別（C# 側 input_bridge / InputMap ラッパーと一致させる）。
pub const ACTION_KIND_PRESS: i32 = 0; // 押している間
pub const ACTION_KIND_DOWN: i32 = 1; // 押した瞬間
pub const ACTION_KIND_UP: i32 = 2; // 離した瞬間

// ─── キー状態問い合わせの抽象化 ──────────────────────────────

/// キー状態の問い合わせインターフェース。
///
/// 評価ロジックを実ランタイム入力（`Input`）から切り離してユニットテスト可能にするための
/// 抽象。テストではキー押下集合を注入したダミー実装を渡す。
pub trait KeyQuery {
    /// キー `key` が `kind`（PRESS / DOWN / UP）の状態かを返す。
    fn is_key(&self, key: KeyCode, kind: i32) -> bool;
}

/// 実ランタイム入力（`Input`）を `KeyQuery` として使えるようにする。
impl KeyQuery for crate::engine::core::input::Input {
    fn is_key(&self, key: KeyCode, kind: i32) -> bool {
        match kind {
            ACTION_KIND_PRESS => self.is_press_key(key),
            ACTION_KIND_DOWN => self.is_trigger_key(key),
            ACTION_KIND_UP => self.is_release_key(key),
            _ => false,
        }
    }
}

// ─── serde パース用の生データ型 ──────────────────────────────
//  未知フィールドは serde の既定（無視）で許容する。全フィールドに #[serde(default)] を
//  付け、欠落フィールドがあっても丸ごと失敗しないようにする。

#[derive(Deserialize)]
struct RawFile {
    #[serde(default)]
    actions: Vec<RawAction>,
}

#[derive(Deserialize)]
struct RawAction {
    #[serde(default)]
    name: String,
    #[serde(default)]
    value_type: i32,
    #[serde(default)]
    bindings: Vec<RawBinding>,
}

#[derive(Deserialize)]
struct RawBinding {
    #[serde(default)]
    platform: String,
    #[serde(default)]
    input_type: String,
    #[serde(default)]
    value: String,
}

// ─── 解決済みデータ型（評価に使う）──────────────────────────

/// アクション値の型（解決済み）。
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ValueType {
    Bool,
    Axis1D,
    Vector2,
}

impl ValueType {
    /// エディタの数値（0/1/2）から変換する。未知値は Bool 扱い。
    fn from_i32(v: i32) -> Self {
        match v {
            VALUE_TYPE_AXIS1D => ValueType::Axis1D,
            VALUE_TYPE_VECTOR2 => ValueType::Vector2,
            _ => ValueType::Bool, // VALUE_TYPE_BOOL および未知値
        }
    }
}

/// 解決済みの 1 バインディング（PC・サポート対象のみ）。
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum ResolvedBinding {
    /// 物理キー。
    Key(KeyCode),
    /// WASD 水平合成軸。
    WasdHorizontal,
    /// WASD 垂直合成軸。
    WasdVertical,
}

/// 解決済みの 1 アクション。
#[derive(Debug, Clone)]
struct Action {
    name: String,
    #[allow(dead_code)] // value_type は将来のバリデーション用に保持（評価は out 要素数で分岐）
    value_type: ValueType,
    bindings: Vec<ResolvedBinding>,
}

/// パース＋解決済みのアクションマップ。評価はこの構造体のメソッドで行う。
#[derive(Debug, Clone, Default)]
pub struct ActionMap {
    actions: Vec<Action>,
}

impl ActionMap {
    /// 空のマップ（パース失敗時のフォールバック）。
    pub fn empty() -> Self {
        Self { actions: Vec::new() }
    }

    /// JSON 文字列をパースして ActionMap を構築する。
    ///
    /// - PC プラットフォーム以外のバインディングは無視する。
    /// - サポート外の input_type（GamepadButton など）や解決不能なキー名は
    ///   落とす（キー名解決失敗は 1 回だけ警告ログを出す）。
    /// - JSON 自体が壊れている場合は空マップを返す（警告ログ）。
    pub fn parse(json: &str) -> Self {
        let raw: RawFile = match serde_json::from_str(json) {
            Ok(r) => r,
            Err(e) => {
                eprintln!("[SEED script] InputMap パース失敗: {e}");
                return Self::empty();
            }
        };

        let actions = raw
            .actions
            .into_iter()
            .map(|a| Action {
                name: a.name,
                value_type: ValueType::from_i32(a.value_type),
                bindings: a
                    .bindings
                    .into_iter()
                    .filter(|b| b.platform == PLATFORM_PC) // PC のみ
                    .filter_map(resolve_binding)
                    .collect(),
            })
            .collect();

        Self { actions }
    }

    /// 名前一致のアクションを引く。
    fn find(&self, name: &str) -> Option<&Action> {
        self.actions.iter().find(|a| a.name == name)
    }

    /// Bool アクションを評価する。いずれかの Key バインディングが `kind` 状態なら true。
    ///
    /// WASD バインディングは方向軸のため Bool では意味を持たず、無視する。
    pub fn eval_bool(&self, keys: &impl KeyQuery, name: &str, kind: i32) -> bool {
        let Some(action) = self.find(name) else { return false };
        action.bindings.iter().any(|b| match b {
            ResolvedBinding::Key(k) => keys.is_key(*k, kind),
            ResolvedBinding::WasdHorizontal | ResolvedBinding::WasdVertical => false,
        })
    }

    /// Axis1D アクションを評価する（[-1, 1] にクランプ）。
    ///
    /// - WASD 水平/垂直バインディングは合成軸値を加算する。
    /// - Key バインディングは押下中（PRESS）で +1.0 を加算する。
    pub fn eval_axis1d(&self, keys: &impl KeyQuery, name: &str) -> f32 {
        let Some(action) = self.find(name) else { return 0.0 };
        let mut v = 0.0;
        for b in &action.bindings {
            match b {
                ResolvedBinding::WasdHorizontal => v += horizontal_axis(keys),
                ResolvedBinding::WasdVertical => v += vertical_axis(keys),
                ResolvedBinding::Key(k) => {
                    if keys.is_key(*k, ACTION_KIND_PRESS) {
                        v += 1.0;
                    }
                }
            }
        }
        v.clamp(-1.0, 1.0)
    }

    /// Vector2 アクションを評価する（各成分 [-1, 1] にクランプ）。
    ///
    /// Horizontal → x, Vertical → y に合成する。Key バインディングは方向を持たないため無視。
    pub fn eval_vector2(&self, keys: &impl KeyQuery, name: &str) -> [f32; 2] {
        let Some(action) = self.find(name) else { return [0.0, 0.0] };
        let mut x = 0.0;
        let mut y = 0.0;
        for b in &action.bindings {
            match b {
                ResolvedBinding::WasdHorizontal => x += horizontal_axis(keys),
                ResolvedBinding::WasdVertical => y += vertical_axis(keys),
                ResolvedBinding::Key(_) => {} // キーは 2D 方向を定義しない
            }
        }
        [x.clamp(-1.0, 1.0), y.clamp(-1.0, 1.0)]
    }
}

// ─── バインディング解決 ──────────────────────────────────────

/// 生バインディング（PC 前提）を解決する。サポート外は None（落とす）。
fn resolve_binding(b: RawBinding) -> Option<ResolvedBinding> {
    match b.input_type.as_str() {
        INPUT_TYPE_KEY => match key_from_name(&b.value) {
            Some(k) => Some(ResolvedBinding::Key(k)),
            None => {
                // マッピング不能なキー名は無反応。パースは 1 アセット 1 回なので
                // ここでの警告は「初回ロード時 1 回だけ」に相当する。
                eprintln!(
                    "[SEED script] InputMap: 未対応のキー名 '{}'（無反応）",
                    b.value
                );
                None
            }
        },
        INPUT_TYPE_WASD => match b.value.as_str() {
            WASD_HORIZONTAL => Some(ResolvedBinding::WasdHorizontal),
            WASD_VERTICAL => Some(ResolvedBinding::WasdVertical),
            _ => None,
        },
        // ゲームパッド系・仮想入力などランタイム基盤が無いものは無視する
        _ => None,
    }
}

/// WASD 水平合成軸（D/→ = +1, A/← = -1）。矢印キーも同時に有効
/// （既存 `Input.MoveAxis()` と同じ挙動に合わせる）。
fn horizontal_axis(keys: &impl KeyQuery) -> f32 {
    let mut v = 0.0;
    if keys.is_key(KeyCode::KeyD, ACTION_KIND_PRESS)
        || keys.is_key(KeyCode::ArrowRight, ACTION_KIND_PRESS)
    {
        v += 1.0;
    }
    if keys.is_key(KeyCode::KeyA, ACTION_KIND_PRESS)
        || keys.is_key(KeyCode::ArrowLeft, ACTION_KIND_PRESS)
    {
        v -= 1.0;
    }
    v
}

/// WASD 垂直合成軸（W/↑ = +1, S/↓ = -1）。矢印キーも同時に有効。
fn vertical_axis(keys: &impl KeyQuery) -> f32 {
    let mut v = 0.0;
    if keys.is_key(KeyCode::KeyW, ACTION_KIND_PRESS)
        || keys.is_key(KeyCode::ArrowUp, ACTION_KIND_PRESS)
    {
        v += 1.0;
    }
    if keys.is_key(KeyCode::KeyS, ACTION_KIND_PRESS)
        || keys.is_key(KeyCode::ArrowDown, ACTION_KIND_PRESS)
    {
        v -= 1.0;
    }
    v
}

/// エディタのキー名（"Space" / "LeftShift" / "Q" / "Alpha0" / "Keypad0" / "UpArrow" …）を
/// winit KeyCode へ対応させる。
///
/// 正典はエディタ側 InputMapEditorWindow.xaml.cs の [("PC","Key")] 一覧。
/// 一覧に無い名前は None（＝無反応）。
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

    /// テスト用のキー押下集合を注入するダミー KeyQuery。
    /// PRESS / DOWN / UP を種別ごとに別集合で保持し、正確な状態注入を可能にする。
    struct FakeKeys {
        press: HashSet<u32>,
        down: HashSet<u32>,
        up: HashSet<u32>,
    }

    impl FakeKeys {
        fn new() -> Self {
            Self { press: HashSet::new(), down: HashSet::new(), up: HashSet::new() }
        }
        /// KeyCode を安定した u32 キーへ（テスト内比較用。Debug 文字列で代用）。
        fn id(key: KeyCode) -> u32 {
            // KeyCode は Hash 実装があるが、集合の要素型を単純化するため
            // discriminant 相当の安定 ID を作る（Debug 文字列のハッシュ）。
            use std::hash::{Hash, Hasher};
            let mut h = std::collections::hash_map::DefaultHasher::new();
            format!("{key:?}").hash(&mut h);
            h.finish() as u32
        }
        fn with_press(mut self, key: KeyCode) -> Self {
            self.press.insert(Self::id(key));
            self
        }
        fn with_down(mut self, key: KeyCode) -> Self {
            self.down.insert(Self::id(key));
            self
        }
    }

    impl KeyQuery for FakeKeys {
        fn is_key(&self, key: KeyCode, kind: i32) -> bool {
            let id = Self::id(key);
            match kind {
                ACTION_KIND_PRESS => self.press.contains(&id),
                ACTION_KIND_DOWN => self.down.contains(&id),
                ACTION_KIND_UP => self.up.contains(&id),
                _ => false,
            }
        }
    }

    /// 実ファイル形式（playerInput.inputmap 相当）をパースできること。
    #[test]
    fn parse_real_format_jump_action() {
        let json = r#"{
            "actions": [
                { "name": "Jump", "value_type": 0,
                  "bindings": [ { "platform": "PC", "input_type": "Key", "value": "Space" } ] }
            ]
        }"#;
        let map = ActionMap::parse(json);
        // Space 押下で Jump が true、非押下で false
        let pressed = FakeKeys::new().with_press(KeyCode::Space);
        assert!(map.eval_bool(&pressed, "Jump", ACTION_KIND_PRESS));
        let idle = FakeKeys::new();
        assert!(!map.eval_bool(&idle, "Jump", ACTION_KIND_PRESS));
    }

    /// Bool の DOWN（押した瞬間）判定が種別で分離されること。
    #[test]
    fn eval_bool_kind_down() {
        let json = r#"{"actions":[{"name":"Fire","value_type":0,
            "bindings":[{"platform":"PC","input_type":"Key","value":"Q"}]}]}"#;
        let map = ActionMap::parse(json);
        // DOWN 集合にのみ Q → DOWN は真・PRESS は偽
        let keys = FakeKeys::new().with_down(KeyCode::KeyQ);
        assert!(map.eval_bool(&keys, "Fire", ACTION_KIND_DOWN));
        assert!(!map.eval_bool(&keys, "Fire", ACTION_KIND_PRESS));
    }

    /// WASD 合成軸（Horizontal）が D=+1 / A=-1 になること。矢印キーも有効。
    #[test]
    fn eval_axis1d_wasd_horizontal() {
        let json = r#"{"actions":[{"name":"MoveX","value_type":1,
            "bindings":[{"platform":"PC","input_type":"WASD","value":"Horizontal"}]}]}"#;
        let map = ActionMap::parse(json);

        let right = FakeKeys::new().with_press(KeyCode::KeyD);
        assert_eq!(map.eval_axis1d(&right, "MoveX"), 1.0);

        let left = FakeKeys::new().with_press(KeyCode::KeyA);
        assert_eq!(map.eval_axis1d(&left, "MoveX"), -1.0);

        // 矢印右も +1
        let arrow = FakeKeys::new().with_press(KeyCode::ArrowRight);
        assert_eq!(map.eval_axis1d(&arrow, "MoveX"), 1.0);

        // 左右同時押しは相殺して 0
        let both = FakeKeys::new().with_press(KeyCode::KeyD).with_press(KeyCode::KeyA);
        assert_eq!(map.eval_axis1d(&both, "MoveX"), 0.0);
    }

    /// Vector2（Horizontal→x, Vertical→y）合成。
    #[test]
    fn eval_vector2_move() {
        let json = r#"{"actions":[{"name":"Move","value_type":2,
            "bindings":[
                {"platform":"PC","input_type":"WASD","value":"Horizontal"},
                {"platform":"PC","input_type":"WASD","value":"Vertical"}
            ]}]}"#;
        let map = ActionMap::parse(json);
        // D + W → (+1, +1)
        let keys = FakeKeys::new().with_press(KeyCode::KeyD).with_press(KeyCode::KeyW);
        assert_eq!(map.eval_vector2(&keys, "Move"), [1.0, 1.0]);
        // S → (0, -1)
        let down = FakeKeys::new().with_press(KeyCode::KeyS);
        assert_eq!(map.eval_vector2(&down, "Move"), [0.0, -1.0]);
    }

    /// PC 以外のプラットフォーム／未対応 input_type は無視されること。
    #[test]
    fn ignores_non_pc_and_unsupported() {
        let json = r#"{"actions":[{"name":"Jump","value_type":0,
            "bindings":[
                {"platform":"PS5","input_type":"GamepadButton","value":"Cross"},
                {"platform":"PC","input_type":"GamepadButton","value":"South"}
            ]}]}"#;
        let map = ActionMap::parse(json);
        // どのキーを押しても true にならない（Key バインディングが無い）
        let keys = FakeKeys::new().with_press(KeyCode::Space);
        assert!(!map.eval_bool(&keys, "Jump", ACTION_KIND_PRESS));
    }

    /// 未対応のキー名は落ちる（無反応）。
    #[test]
    fn unknown_key_name_dropped() {
        let json = r#"{"actions":[{"name":"X","value_type":0,
            "bindings":[{"platform":"PC","input_type":"Key","value":"NoSuchKey"}]}]}"#;
        let map = ActionMap::parse(json);
        let keys = FakeKeys::new();
        assert!(!map.eval_bool(&keys, "X", ACTION_KIND_PRESS));
    }

    /// 壊れた JSON は空マップになる（パニックしない）。
    #[test]
    fn broken_json_is_empty() {
        let map = ActionMap::parse("{ this is not json ");
        let keys = FakeKeys::new();
        assert!(!map.eval_bool(&keys, "Anything", ACTION_KIND_PRESS));
    }

    /// 未知フィールドを含む JSON も許容してパースできること。
    #[test]
    fn unknown_fields_allowed() {
        let json = r#"{"version":3,"actions":[{"name":"Jump","value_type":0,"extra":true,
            "bindings":[{"platform":"PC","input_type":"Key","value":"Space","note":"x"}]}]}"#;
        let map = ActionMap::parse(json);
        let keys = FakeKeys::new().with_press(KeyCode::Space);
        assert!(map.eval_bool(&keys, "Jump", ACTION_KIND_PRESS));
    }
}
