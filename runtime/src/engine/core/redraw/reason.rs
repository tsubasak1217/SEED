// ============================================================
//  redraw/reason.rs — 「描く理由」の種類と、その集合（W2-10a。正典は docs/redraw_policy.md）
//
//  【役割】
//  次のフレームを描くかを決める材料を、種類（RedrawReason）とビットの集合（RedrawReasons）で表す。
//  集合は u32 の 1 語なので、他のスレッド（IPC の読み取り・JNI）からも AtomicU32 の fetch_or 1 回で積める（wake.rs）。
//
//  【理由の出どころ】（どこで積むかの一覧は docs/redraw_policy.md §3）
//    - イベントループのスレッド: 入力の WindowEvent・画面の変化・フレームの末尾に集める「動いている」の申告など
//    - 他のスレッド: IPC の命令・SEED.Platform のイベント（JNI）・文字入力（JNI）・音声フォーカス（JNI）
//  どの理由も「次のフレームを描く」の意味しか持たない（種類はログと計測・試験のため）。
//  例外は Reschedule だけで、これは「眠っている間に予定の時刻が変わった」の知らせ（描かずに起きる時刻を決め直す）。
// ============================================================

use std::fmt;

/// 次のフレームを描く理由の種類。
///
/// 値（`bit_index`）は集合のビットの位置。並びを変えても保存しない（プロセスの中だけで使う）ので互換は要らない。
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum RedrawReason {
    /// 入力の WindowEvent（キー・マウスのボタン・カーソル・ホイール・タッチ・PC の IME）。
    Input,
    /// 押している（キー・マウスのボタン・指）。押している間は描き続ける（押しっぱなしを毎フレーム読むスクリプトのため）。
    InputHeld,
    /// 入力の注入（IPC の INPUT_SEQUENCE）が再生中。予定の時刻の操作を落とさないために描き続ける。
    InjectedInput,
    /// ジェスチャーの進行中（アリーナに参加している指がある。docs/input_gestures.md §9）。
    Gesture,
    /// 文字入力（Android の IME の知らせ。JNI の受け口が起こす。W2-6a で本文を受け取る）。
    TextInput,
    /// 動いているもの（アニメーション・パーティクル・読み込み中のモデル・物理など）の申告。
    Motion,
    /// スクリプトの要求（`SEED.Redraw.Request()`、または `RequestAfter` の時刻が来た）。
    ScriptRequest,
    /// スクリプトの `SEED.Redraw.KeepAlive(秒)` の期限の内。
    ScriptKeepAlive,
    /// スクリプトの `SEED.Redraw.SetContinuous(true)`（常に描く）。
    ScriptContinuous,
    /// IPC の命令が届いた（エディタ・MCP・SeedAndroid）。
    Ipc,
    /// `SEED.Platform` のイベント（目覚まし・通知・権限・接続）が届いた。
    PlatformEvent,
    /// 画面の大きさ・向き・安全領域・倍率・フォーカス・遮蔽・前面と背面の変化。
    Screen,
    /// 音声フォーカスなど、イベントループの 1 周で反映するアプリの状態の変化（Android の JNI）。
    SystemEvent,
    /// ホットリロード（シーン・アセット・スクリプト・シェーダーの差し替え）の適用。
    HotReload,
    /// 撮影・サムネイルの生成・プロファイラの一発計測が進行中（フレームが要る）。
    Capture,
    /// 起動・Play の開始・シーンの切り替え・前面へ戻った直後。
    Lifecycle,
    /// 予定の時刻が来た（WaitUntil で起きた。ジェスチャーの長押し・模擬の目覚まし・`RequestAfter` など）。
    Timer,
    /// 眠っている間に予定の時刻が変わった（他のスレッドからの `RequestAfter` など）。描かずに起きる時刻を決め直す。
    Reschedule,
}

/// Java（Android の MainActivity など）から起こすときの理由の番号: 文字入力（IME の本文・アクション・キーボードの表示と高さ）。
/// Java の `com.seedengine.runtime.redraw.RedrawWaker.REASON_*` と一致させる。
pub const EXTERNAL_REASON_TEXT_INPUT: i32 = 0;

/// Java から起こすときの理由の番号: 画面の変化。
pub const EXTERNAL_REASON_SCREEN: i32 = 1;

/// Java から起こすときの理由の番号: その他のアプリの状態の変化。
pub const EXTERNAL_REASON_SYSTEM_EVENT: i32 = 2;

/// すべての理由（ログの並び・試験の網羅のため。宣言の順）。
pub const ALL_REDRAW_REASONS: [RedrawReason; 18] = [
    RedrawReason::Input,
    RedrawReason::InputHeld,
    RedrawReason::InjectedInput,
    RedrawReason::Gesture,
    RedrawReason::TextInput,
    RedrawReason::Motion,
    RedrawReason::ScriptRequest,
    RedrawReason::ScriptKeepAlive,
    RedrawReason::ScriptContinuous,
    RedrawReason::Ipc,
    RedrawReason::PlatformEvent,
    RedrawReason::Screen,
    RedrawReason::SystemEvent,
    RedrawReason::HotReload,
    RedrawReason::Capture,
    RedrawReason::Lifecycle,
    RedrawReason::Timer,
    RedrawReason::Reschedule,
];

impl RedrawReason {
    /// 集合の中のビットの位置（宣言の順）。
    pub const fn bit_index(self) -> u32 {
        self as u32
    }

    /// 集合の中のビット。
    pub const fn bit(self) -> u32 {
        1 << self.bit_index()
    }

    /// ログの名前（短い英字。grep しやすいように固定する）。
    pub const fn name(self) -> &'static str {
        match self {
            RedrawReason::Input => "input",
            RedrawReason::InputHeld => "input_held",
            RedrawReason::InjectedInput => "injected_input",
            RedrawReason::Gesture => "gesture",
            RedrawReason::TextInput => "text_input",
            RedrawReason::Motion => "motion",
            RedrawReason::ScriptRequest => "script_request",
            RedrawReason::ScriptKeepAlive => "script_keep_alive",
            RedrawReason::ScriptContinuous => "script_continuous",
            RedrawReason::Ipc => "ipc",
            RedrawReason::PlatformEvent => "platform_event",
            RedrawReason::Screen => "screen",
            RedrawReason::SystemEvent => "system_event",
            RedrawReason::HotReload => "hot_reload",
            RedrawReason::Capture => "capture",
            RedrawReason::Lifecycle => "lifecycle",
            RedrawReason::Timer => "timer",
            RedrawReason::Reschedule => "reschedule",
        }
    }

    /// 次のフレームを描く理由か（`Reschedule` だけは描かずに起きる時刻を決め直す知らせ）。
    pub const fn draws_frame(self) -> bool {
        !matches!(self, RedrawReason::Reschedule)
    }

    /// Java から起こすときの理由の番号（`EXTERNAL_REASON_*`）から変換する。知らない番号は None。
    pub const fn from_external_code(code: i32) -> Option<Self> {
        match code {
            EXTERNAL_REASON_TEXT_INPUT => Some(RedrawReason::TextInput),
            EXTERNAL_REASON_SCREEN => Some(RedrawReason::Screen),
            EXTERNAL_REASON_SYSTEM_EVENT => Some(RedrawReason::SystemEvent),
            _ => None,
        }
    }
}

/// 理由の集合（u32 のビット。空 = 描く理由が無い）。
#[derive(Clone, Copy, PartialEq, Eq, Hash, Default)]
pub struct RedrawReasons(u32);

impl RedrawReasons {
    /// 空の集合。
    pub const EMPTY: Self = Self(0);

    /// ビットの並びから作る（wake.rs の AtomicU32 から取り出すとき）。知らないビットは捨てる。
    pub const fn from_bits(bits: u32) -> Self {
        Self(bits & Self::known_bits())
    }

    /// 理由 1 つだけの集合。
    pub const fn only(reason: RedrawReason) -> Self {
        Self(reason.bit())
    }

    /// ビットの並び（wake.rs の AtomicU32 へ積むとき）。
    pub const fn bits(self) -> u32 {
        self.0
    }

    /// 知っている理由のビットをすべて立てた値。
    const fn known_bits() -> u32 {
        let mut bits = 0;
        let mut i = 0;
        while i < ALL_REDRAW_REASONS.len() {
            bits |= ALL_REDRAW_REASONS[i].bit();
            i += 1;
        }
        bits
    }

    /// 空か。
    pub const fn is_empty(self) -> bool {
        self.0 == 0
    }

    /// 理由を含むか。
    pub const fn contains(self, reason: RedrawReason) -> bool {
        self.0 & reason.bit() != 0
    }

    /// 理由を足す。
    pub fn insert(&mut self, reason: RedrawReason) {
        self.0 |= reason.bit();
    }

    /// 条件が真のときだけ理由を足す（フレームの末尾の集計を 1 行ずつ書くため）。
    pub fn insert_if(&mut self, condition: bool, reason: RedrawReason) {
        if condition {
            self.insert(reason);
        }
    }

    /// 和集合。
    pub const fn union(self, other: Self) -> Self {
        Self(self.0 | other.0)
    }

    /// 次のフレームを描く理由だけの集合（`Reschedule` を除く）。
    pub fn frame_reasons(self) -> Self {
        Self(self.0 & !RedrawReason::Reschedule.bit())
    }

    /// 次のフレームを描く理由を 1 つでも含むか。
    pub fn draws_frame(self) -> bool {
        !self.frame_reasons().is_empty()
    }

    /// 含む理由を宣言の順に並べる。
    pub fn iter(self) -> impl Iterator<Item = RedrawReason> {
        ALL_REDRAW_REASONS.into_iter().filter(move |reason| self.contains(*reason))
    }
}

impl fmt::Debug for RedrawReasons {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(f, "RedrawReasons({self})")
    }
}

/// ログの形（`input+ipc`。空なら `none`）。
impl fmt::Display for RedrawReasons {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        if self.is_empty() {
            return f.write_str("none");
        }
        for (i, reason) in self.iter().enumerate() {
            if i > 0 {
                f.write_str("+")?;
            }
            f.write_str(reason.name())?;
        }
        Ok(())
    }
}

impl From<RedrawReason> for RedrawReasons {
    fn from(reason: RedrawReason) -> Self {
        Self::only(reason)
    }
}

// ============================================================
//  テスト
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;

    /// すべての理由が別のビットを持ち、1 語（u32）に収まり、名前も重ならない。
    #[test]
    fn every_reason_has_its_own_bit_and_name() {
        let mut seen_bits = 0u32;
        let mut names = std::collections::HashSet::new();
        for reason in ALL_REDRAW_REASONS {
            assert!(reason.bit_index() < u32::BITS, "{reason:?} が u32 に収まらない");
            assert_eq!(seen_bits & reason.bit(), 0, "{reason:?} のビットが重なる");
            seen_bits |= reason.bit();
            assert!(names.insert(reason.name()), "{reason:?} の名前が重なる");
        }
        // 宣言の順と ALL の並びが一致する（iter の順が宣言の順になる前提）
        for (i, reason) in ALL_REDRAW_REASONS.into_iter().enumerate() {
            assert_eq!(reason.bit_index() as usize, i, "{reason:?} の位置");
        }
    }

    /// 足す・含む・和・空、と表示の形。
    #[test]
    fn set_operations_and_display() {
        let mut reasons = RedrawReasons::EMPTY;
        assert!(reasons.is_empty());
        assert_eq!(reasons.to_string(), "none");
        reasons.insert(RedrawReason::Ipc);
        reasons.insert_if(false, RedrawReason::Motion);
        reasons.insert_if(true, RedrawReason::Input);
        assert!(reasons.contains(RedrawReason::Ipc));
        assert!(reasons.contains(RedrawReason::Input));
        assert!(!reasons.contains(RedrawReason::Motion));
        assert_eq!(reasons.to_string(), "input+ipc", "宣言の順に並ぶ");
        let merged = reasons.union(RedrawReason::Timer.into());
        assert_eq!(merged.iter().count(), 3);
    }

    /// Reschedule は描く理由に数えない（起きる時刻を決め直すだけ）。
    #[test]
    fn reschedule_does_not_draw() {
        let only_reschedule = RedrawReasons::only(RedrawReason::Reschedule);
        assert!(!only_reschedule.is_empty());
        assert!(!only_reschedule.draws_frame());
        assert!(only_reschedule.frame_reasons().is_empty());
        let mixed = only_reschedule.union(RedrawReason::Ipc.into());
        assert!(mixed.draws_frame());
        assert_eq!(mixed.frame_reasons(), RedrawReasons::only(RedrawReason::Ipc));
        for reason in ALL_REDRAW_REASONS {
            assert_eq!(reason.draws_frame(), reason != RedrawReason::Reschedule, "{reason:?}");
        }
    }

    /// Java から起こすときの番号の対応（知らない番号は None）。
    #[test]
    fn external_codes_map_to_reasons() {
        assert_eq!(RedrawReason::from_external_code(EXTERNAL_REASON_TEXT_INPUT), Some(RedrawReason::TextInput));
        assert_eq!(RedrawReason::from_external_code(EXTERNAL_REASON_SCREEN), Some(RedrawReason::Screen));
        assert_eq!(RedrawReason::from_external_code(EXTERNAL_REASON_SYSTEM_EVENT), Some(RedrawReason::SystemEvent));
        assert_eq!(RedrawReason::from_external_code(-1), None);
        assert_eq!(RedrawReason::from_external_code(3), None);
    }

    /// ビットの並びとの往復（知らないビットは捨てる）。
    #[test]
    fn bits_round_trip_and_unknown_bits_are_dropped() {
        let reasons = RedrawReasons::only(RedrawReason::PlatformEvent).union(RedrawReason::TextInput.into());
        assert_eq!(RedrawReasons::from_bits(reasons.bits()), reasons);
        let unknown = 1u32 << (u32::BITS - 1);
        assert_eq!(RedrawReasons::from_bits(unknown | reasons.bits()), reasons);
    }
}
