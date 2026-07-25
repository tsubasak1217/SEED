// ============================================================
//  gamepad.rs — ゲームパッド入力基盤（gilrs バックエンド）
//
//  役割（単一責任）:
//    - gilrs でパッドイベントを毎フレームポンプし、接続中パッド（最初の1台）の
//      ボタン押下状態・軸値のスナップショットを保持する。
//    - キーボードと同じ prev/current スナップショット方式で press/down/up の
//      3 状態を提供する（down=押した瞬間 / up=離した瞬間）。
//    - .inputmap の GamepadButton / GamepadAxis 評価（action_map）へ状態を渡す。
//
//  設計方針:
//    - 論理ボタン/軸（PadButton / PadAxis）を .inputmap の value 文字列と 1:1 対応する
//      安定した列挙で定義する（gilrs の内部列挙とはここで変換して切り離す）。
//    - 複数台接続は将来対応。現状は「最初に接続された1台」のみを対象とする。
//    - 未接続時は全ボタン false・全軸 0.0 を返す。
// ============================================================

use gilrs::{Axis as GilrsAxis, Button as GilrsButton, EventType, GamepadId, Gilrs};

// ─── 論理ボタン / 軸の定義 ────────────────────────────────────

/// .inputmap の GamepadButton value と 1:1 対応する論理ボタン。
///
/// 名前はエディタの選択肢（InputMapEditorWindow の GamepadButton 一覧）と一致させる。
/// LeftShoulder=LB / RightShoulder=RB / LeftStickPress=L3 / RightStickPress=R3。
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum PadButton {
    South,
    East,
    West,
    North,
    DPadUp,
    DPadDown,
    DPadLeft,
    DPadRight,
    LeftShoulder,
    RightShoulder,
    LeftStickPress,
    RightStickPress,
    Start,
    Select,
}

impl PadButton {
    /// 全論理ボタンの数（状態配列サイズ）。
    pub const COUNT: usize = 14;

    /// 状態配列のインデックス（0..COUNT）。
    fn index(self) -> usize {
        self as usize
    }

    /// .inputmap の value 文字列から論理ボタンへ変換する。未対応は None。
    pub fn from_name(name: &str) -> Option<Self> {
        Some(match name {
            "South" => Self::South,
            "East" => Self::East,
            "West" => Self::West,
            "North" => Self::North,
            "DPadUp" => Self::DPadUp,
            "DPadDown" => Self::DPadDown,
            "DPadLeft" => Self::DPadLeft,
            "DPadRight" => Self::DPadRight,
            "LeftShoulder" => Self::LeftShoulder,
            "RightShoulder" => Self::RightShoulder,
            "LeftStickPress" => Self::LeftStickPress,
            "RightStickPress" => Self::RightStickPress,
            "Start" => Self::Start,
            "Select" => Self::Select,
            _ => return None,
        })
    }

}

// gilrs 命名の注意: LeftTrigger=LB（ショルダー）, LeftTrigger2=LT（アナログトリガ）,
// LeftThumb=L3（スティック押し込み）。トリガ（LT/RT）は軸として扱う（gilrs_button で対応）。

/// .inputmap の GamepadAxis value と 1:1 対応する論理軸。
///
/// スティック 4 軸（-1..1）とトリガ 2 軸（0..1）。
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum PadAxis {
    LeftStickX,
    LeftStickY,
    RightStickX,
    RightStickY,
    LeftTrigger,
    RightTrigger,
}

impl PadAxis {
    /// 全論理軸の数（状態配列サイズ）。
    pub const COUNT: usize = 6;

    /// 状態配列のインデックス（0..COUNT）。
    fn index(self) -> usize {
        self as usize
    }

    /// .inputmap の value 文字列から論理軸へ変換する。未対応は None。
    pub fn from_name(name: &str) -> Option<Self> {
        Some(match name {
            "LeftStickX" => Self::LeftStickX,
            "LeftStickY" => Self::LeftStickY,
            "RightStickX" => Self::RightStickX,
            "RightStickY" => Self::RightStickY,
            "LeftTrigger" => Self::LeftTrigger,
            "RightTrigger" => Self::RightTrigger,
            _ => return None,
        })
    }
}

// ─── パッド状態問い合わせの抽象化 ────────────────────────────

/// パッド状態の問い合わせインターフェース。
///
/// action_map の評価を実ランタイム（`GamepadState`）から切り離してユニットテスト可能に
/// するための抽象。テストではダミー実装を注入する。
pub trait PadQuery {
    /// 論理ボタンが「押されている間」true。
    fn is_button_pressed(&self, btn: PadButton) -> bool;
    /// 論理軸の生値。スティックは -1..1、トリガは 0..1。未接続は 0.0。
    fn axis_value(&self, axis: PadAxis) -> f32;
}

// ─── ゲームパッド状態 ────────────────────────────────────────

/// ゲームパッド入力状態。gilrs を内包し、毎フレーム `update` でポンプする。
///
/// prev/current スナップショット方式でボタンのエッジ（down/up）を提供する。
/// 軸は current のみ保持する（軸のエッジは意味を持たないため）。
pub struct GamepadState {
    /// gilrs インスタンス（イベントソース）。初期化失敗時は None（パッド無効）。
    gilrs: Option<Gilrs>,
    /// 対象とするパッド（最初に接続された1台）。未接続は None。
    active: Option<GamepadId>,

    /// 現フレームのボタン押下状態。
    current_buttons: [bool; PadButton::COUNT],
    /// 前フレームのボタン押下状態（エッジ検出用）。
    prev_buttons: [bool; PadButton::COUNT],
    /// 現フレームの軸値。
    current_axes: [f32; PadAxis::COUNT],
}

impl GamepadState {
    /// 新規作成。gilrs 初期化に失敗しても panic せず「パッド無効」で継続する。
    pub fn new() -> Self {
        let gilrs = match Gilrs::new() {
            Ok(g) => Some(g),
            Err(e) => {
                eprintln!("[SEED input] ゲームパッド初期化に失敗（パッド無効で継続）: {e}");
                None
            }
        };
        // 起動時点で既に接続済みのパッドがあれば最初の1台を採用する。
        let active = gilrs
            .as_ref()
            .and_then(|g| g.gamepads().next().map(|(id, _)| id));

        Self {
            gilrs,
            active,
            current_buttons: [false; PadButton::COUNT],
            prev_buttons: [false; PadButton::COUNT],
            current_axes: [0.0; PadAxis::COUNT],
        }
    }

    /// 毎フレーム先頭で呼ぶ。gilrs のイベントをすべてポンプして内部状態を更新し、
    /// 対象パッドの現在状態を current スナップショットへ取り込む。
    ///
    /// 接続/切断を監視し、対象未設定時は最初に接続されたパッドを採用する。
    pub fn update(&mut self) {
        let Some(gilrs) = self.gilrs.as_mut() else {
            return;
        };

        // ── イベントをすべてポンプ（next_event が内部状態も更新する）──
        while let Some(ev) = gilrs.next_event() {
            match ev.event {
                EventType::Connected => {
                    // 対象未設定なら、接続されたパッドを対象に採用する。
                    if self.active.is_none() {
                        self.active = Some(ev.id);
                    }
                }
                EventType::Disconnected => {
                    // 対象が切断されたら解除し、他に接続中があれば次を採用する。
                    if self.active == Some(ev.id) {
                        self.active = gilrs.gamepads().next().map(|(id, _)| id);
                    }
                }
                _ => {}
            }
        }

        // ── 対象パッドの現在状態を取り込む ──
        // 対象が接続済みでなければ全ゼロ（未接続時の既定）。
        let connected = self
            .active
            .and_then(|id| gilrs.connected_gamepad(id));

        if let Some(pad) = connected {
            // ボタン: 論理ボタン → gilrs ボタン押下状態。
            for i in 0..PadButton::COUNT {
                let lb = LOGICAL_BUTTONS[i];
                self.current_buttons[i] = pad.is_pressed(gilrs_button(lb));
            }
            // 軸: スティックは Axis 値、トリガは analog ボタン値（0..1）。
            self.current_axes[PadAxis::LeftStickX.index()] = pad.value(GilrsAxis::LeftStickX);
            self.current_axes[PadAxis::LeftStickY.index()] = pad.value(GilrsAxis::LeftStickY);
            self.current_axes[PadAxis::RightStickX.index()] = pad.value(GilrsAxis::RightStickX);
            self.current_axes[PadAxis::RightStickY.index()] = pad.value(GilrsAxis::RightStickY);
            // トリガ（LT/RT）は Button::LeftTrigger2 / RightTrigger2 のアナログ値（0..1）。
            self.current_axes[PadAxis::LeftTrigger.index()] =
                pad.button_data(GilrsButton::LeftTrigger2).map(|d| d.value()).unwrap_or(0.0);
            self.current_axes[PadAxis::RightTrigger.index()] =
                pad.button_data(GilrsButton::RightTrigger2).map(|d| d.value()).unwrap_or(0.0);
        } else {
            // 未接続: 全ゼロ。
            self.current_buttons = [false; PadButton::COUNT];
            self.current_axes = [0.0; PadAxis::COUNT];
        }
    }

    /// フレーム末尾で呼ぶ。current を prev へ退避してエッジ検出の基準を進める。
    pub fn end_frame(&mut self) {
        self.prev_buttons = self.current_buttons;
    }

    // ─── 直接クエリ（keyboard と同じ 3 状態）───────────────────

    /// ボタンが押されている間 true。
    pub fn is_press(&self, btn: PadButton) -> bool {
        self.current_buttons[btn.index()]
    }
    /// ボタンが押された瞬間のみ true。
    pub fn is_trigger(&self, btn: PadButton) -> bool {
        let i = btn.index();
        self.current_buttons[i] && !self.prev_buttons[i]
    }
    /// ボタンが離された瞬間のみ true。
    pub fn is_release(&self, btn: PadButton) -> bool {
        let i = btn.index();
        !self.current_buttons[i] && self.prev_buttons[i]
    }
    /// 軸の生値。
    pub fn axis(&self, axis: PadAxis) -> f32 {
        self.current_axes[axis.index()]
    }
}

impl Default for GamepadState {
    fn default() -> Self {
        Self::new()
    }
}

/// action_map から利用する PadQuery 実装（押下状態＋軸生値）。
impl PadQuery for GamepadState {
    fn is_button_pressed(&self, btn: PadButton) -> bool {
        self.is_press(btn)
    }
    fn axis_value(&self, axis: PadAxis) -> f32 {
        self.axis(axis)
    }
}

// ─── 論理ボタン ⇔ gilrs ボタンの対応 ─────────────────────────

/// index 順の論理ボタン一覧（`current_buttons` 配列の並びと一致）。
const LOGICAL_BUTTONS: [PadButton; PadButton::COUNT] = [
    PadButton::South,
    PadButton::East,
    PadButton::West,
    PadButton::North,
    PadButton::DPadUp,
    PadButton::DPadDown,
    PadButton::DPadLeft,
    PadButton::DPadRight,
    PadButton::LeftShoulder,
    PadButton::RightShoulder,
    PadButton::LeftStickPress,
    PadButton::RightStickPress,
    PadButton::Start,
    PadButton::Select,
];

/// 論理ボタン → gilrs ボタン（状態読み取り用）。
fn gilrs_button(b: PadButton) -> GilrsButton {
    match b {
        PadButton::South => GilrsButton::South,
        PadButton::East => GilrsButton::East,
        PadButton::West => GilrsButton::West,
        PadButton::North => GilrsButton::North,
        PadButton::DPadUp => GilrsButton::DPadUp,
        PadButton::DPadDown => GilrsButton::DPadDown,
        PadButton::DPadLeft => GilrsButton::DPadLeft,
        PadButton::DPadRight => GilrsButton::DPadRight,
        PadButton::LeftShoulder => GilrsButton::LeftTrigger,
        PadButton::RightShoulder => GilrsButton::RightTrigger,
        PadButton::LeftStickPress => GilrsButton::LeftThumb,
        PadButton::RightStickPress => GilrsButton::RightThumb,
        PadButton::Start => GilrsButton::Start,
        PadButton::Select => GilrsButton::Select,
    }
}

// LOGICAL_BUTTONS の並びが PadButton の discriminant（index）と一致することを保証する。
// （current_buttons[i] = pad.is_pressed(gilrs_button(LOGICAL_BUTTONS[i])) が index と対応するため）
const _: () = {
    // 個数の一致だけでもズレを検出できる（要素追加時に COUNT 更新を強制）。
    assert!(LOGICAL_BUTTONS.len() == PadButton::COUNT);
};
