// ============================================================
//  platform/bridge/mod.rs — アプリのプラットフォーム機能（SEED.Platform）のエンジン側の窓口（W1-1 橋渡し）
//
//  【流れ】
//    C# SEED.Platform → host_api の新カテゴリ（core/scripting/platform_bridge.rs の ffi_platform_*）
//      → ここの invoke / poll_events → 登録された PlatformBridge
//         ├ Android: runtime/android/native/src/platform_bridge/（JNI。Java の SeedPlatform.invoke → :seed_platform）
//         └ デスクトップ: desktop_sim.rs（プロセスの中の模擬）
//    Java から届いたイベント → 各 PlatformBridge の箱（event_queue.rs）→ エンジンがフレームの頭で poll_events で取り出し、
//      スクリプトへ見せる箱へ移す（core/scripting/platform_bridge.rs）→ C# の PlatformEvents.Poll が 1 件ずつ配る
//
//  【実装の選び方】（cfg を散らさず、登録とプラットフォームの特性表で決める）
//    1. OS の糊が起動時に register_bridge で登録したもの（Android の android_main が App を作る前に登録する）
//    2. 登録が無ければ platform::CURRENT.platform_bridge_fallback に従う:
//       デスクトップ = 模擬（DesktopSimBridge）／ Android = 無し（Java の側が居ないと何もできない）
//
//  【同期の約束】
//  invoke は呼び出し元のスレッドで同期に動く（スクリプトのフレームの中）。Android では JNI → Binder の往復 1 回
//  （温まっていれば 1 ms 未満）。:seed_platform がまだ起きていない最初の呼び出しは、Java 側が背面で接続を始めて
//  すぐ {"ok":false,"error":"connecting"} を返す（プロセスの起動の約 120 ms を描画のスレッドで待たない）。
//  つながったら platform.connected のイベントが届く（docs/android.md §25）。
//
//  JSON の形とエラーの理由の名前は wire.rs。目覚まし（W1-3）の共通部品（引数の検査・音源の書き出し）は alarm/、
//  通知（W1-5）の引数の検査は notification/、権限（W1-5）の種類の語彙は permission/、アプリ（W1-6）の URL の規則は app/、
//  触感（W1-6）の引数の検査は haptics/、センサー（W1-8）の引数の検査と標本の大きさは sensor/。
//
//  【Play の区切り】エディタの Play の開始・停止で reset_session を呼び、実装ごとの「前の回の状態」を捨てる
//  （デスクトップの模擬は予約表とイベント。Android の実機の予約は Play と関係ないので触らない）。
// ============================================================

/// 目覚ましの共通部品（引数の検査・音源の書き出し。W1-3）。
pub mod alarm;
/// アプリの共通部品（app.open_url の URL の規則。W1-6）。
pub mod app;
/// デスクトップの模擬（W1-P7）。
pub mod desktop_sim;
/// イベントの待ち行列（上限つき）。
pub mod event_queue;
/// 触感の共通部品（haptics.vibrate の引数の検査。W1-6）。
pub mod haptics;
/// 通知の共通部品（引数の検査。W1-5）。
pub mod notification;
/// 権限の共通部品（種類の語彙と引数の読み取り。W1-5）。
pub mod permission;
/// センサーの共通部品（種類・頻度・模擬の標本の読み取りと標本の大きさ。W1-8）。
pub mod sensor;
/// JSON の約束（名前・形・エラーの理由）。
pub mod wire;

use std::panic::{self, AssertUnwindSafe};
use std::sync::OnceLock;

use crate::engine::platform;

pub use desktop_sim::{set_desktop_launch_uri, DesktopSimBridge};
pub use event_queue::{FrontTake, PlatformEventQueue, PushOutcome, DEFAULT_EVENT_QUEUE_CAPACITY};

/// ログの印（`[SEED PLATFORM]`。Android の logcat でもこの印で探す）。
pub const LOG_PREFIX: &str = "[SEED PLATFORM]";

/// PlatformBridge の種類（スクリプトの `Platform.IsSimulated` の源）。
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum PlatformBridgeKind {
    /// 実機の OS の機能へつながっている（Android）。
    Device,
    /// プロセスの中の模擬（デスクトップ）。
    Simulated,
}

/// 登録が無いときにどうするか（プラットフォームの特性表 `PlatformTraits::platform_bridge_fallback`）。
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum PlatformBridgeFallback {
    /// デスクトップの模擬を使う。
    DesktopSim,
    /// 何も使わない（スクリプトからは `IsSupported == false`、呼び出しは `platform_unavailable`）。
    Unavailable,
}

/// 今の基盤の状態（スクリプトの `Platform.IsSupported` / `IsSimulated` の源。FFI の状態の問い合わせで返す）。
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum PlatformBridgeStatus {
    /// 使えない（登録が無い・Android で Java 側の登録がまだ）。
    Unavailable,
    /// 実機につながっている。
    Device,
    /// 模擬。
    Simulated,
}

/// OS ごとのプラットフォーム機能の実装。
///
/// `invoke` と `poll_events` はエンジンのスレッド（スクリプトのフレーム）から呼ばれる。`Send + Sync` なのは
/// プロセスで 1 つを static に持つため（Android の実装はイベントを JNI のスレッドから受け取る）。
pub trait PlatformBridge: Send + Sync {
    /// この実装の種類。
    fn kind(&self) -> PlatformBridgeKind;

    /// 今呼べるか（Android は Java 側の SeedPlatform の登録が済んでいるか）。
    fn is_available(&self) -> bool;

    /// 命令を 1 つ同期で送り、返答の JSON を返す。
    ///
    /// # 戻り値
    /// 届いたら Ok(返答の JSON。失敗も {"ok":false,"error":…} の形でここに入る)。
    /// 届けられなかったら Err(理由の名前。呼び出し側が {"ok":false,"error":理由} にする)
    fn invoke(&self, module: &str, method: &str, json: &str) -> Result<String, String>;

    /// 届いているイベント（JSON）を届いた順にすべて取り出す（エンジンがフレームの頭で 1 回呼ぶ）。
    fn poll_events(&self) -> Vec<String>;

    /// エディタの Play の区切り（開始・停止）で、前の回の状態を捨てる（既定は何もしない）。
    ///
    /// デスクトップの模擬は目覚ましの予約表と積んだイベントを空にする（Play を止めれば予約は消える）。
    /// Android は実機の予約を Play と関係なく持つので何もしない。
    fn reset_session(&self) {}
}

/// OS の糊が登録した実装（プロセスで 1 つ。最初の登録だけが有効）。
static REGISTERED: OnceLock<Box<dyn PlatformBridge>> = OnceLock::new();

/// 登録が無いデスクトップで使う模擬（最初に使うときに作る）。
static DESKTOP_SIM: OnceLock<DesktopSimBridge> = OnceLock::new();

/// OS の糊が実装を登録する（App を作る前に 1 回だけ呼ぶ）。
///
/// # 戻り値
/// 登録できたら true。既に登録があれば false（最初の登録のまま。2 回目の実装は捨てる）
pub fn register_bridge(bridge: Box<dyn PlatformBridge>) -> bool {
    REGISTERED.set(bridge).is_ok()
}

/// 今使う実装（登録 → 特性表の既定の順）。無ければ None。
fn current_bridge() -> Option<&'static dyn PlatformBridge> {
    if let Some(bridge) = REGISTERED.get() {
        return Some(bridge.as_ref());
    }
    match platform::CURRENT.platform_bridge_fallback {
        PlatformBridgeFallback::DesktopSim => Some(DESKTOP_SIM.get_or_init(|| {
            eprintln!("{LOG_PREFIX} デスクトップの模擬（DesktopSimBridge）で SEED.Platform を動かします");
            DesktopSimBridge::new()
        })),
        PlatformBridgeFallback::Unavailable => None,
    }
}

/// 今の基盤の状態（IPC も JNI も通らない。毎フレーム呼んでよい）。
pub fn status() -> PlatformBridgeStatus {
    match current_bridge() {
        Some(bridge) if bridge.is_available() => match bridge.kind() {
            PlatformBridgeKind::Device => PlatformBridgeStatus::Device,
            PlatformBridgeKind::Simulated => PlatformBridgeStatus::Simulated,
        },
        _ => PlatformBridgeStatus::Unavailable,
    }
}

/// 命令を 1 つ同期で送る（スクリプトの SEED.Platform の入口）。
///
/// # 引数
/// * `module` / `method` - 名前（wire::is_valid_name の約束。合わなければ送らない）
/// * `json`              - 引数の JSON（空なら {} とみなすのは受け手の仕事）
///
/// # 戻り値
/// 届いたら Ok(返答の JSON)、届けられなかったら Err(理由の名前。wire::ERROR_* か実装の理由)。
/// 実装の中の panic は受け止めて Err(internal_panic) にする（FFI の境界を越えさせない）
pub fn invoke(module: &str, method: &str, json: &str) -> Result<String, String> {
    if !wire::is_valid_name(module) || !wire::is_valid_name(method) {
        return Err(wire::ERROR_INVALID_NAME.to_string());
    }
    let Some(bridge) = current_bridge() else {
        return Err(wire::ERROR_UNAVAILABLE.to_string());
    };
    if !bridge.is_available() {
        return Err(wire::ERROR_UNAVAILABLE.to_string());
    }
    panic::catch_unwind(AssertUnwindSafe(|| bridge.invoke(module, method, json))).unwrap_or_else(|_| {
        eprintln!("{LOG_PREFIX} {module}.{method} の呼び出し中に panic しました");
        Err(wire::ERROR_INTERNAL_PANIC.to_string())
    })
}

/// Play の区切りで、今の実装の前の回の状態を捨てる（基盤が無ければ何もしない。panic は受け止めてログだけ）。
pub fn reset_session() {
    let Some(bridge) = current_bridge() else {
        return;
    };
    if panic::catch_unwind(AssertUnwindSafe(|| bridge.reset_session())).is_err() {
        eprintln!("{LOG_PREFIX} Play の区切りの片付けの途中で panic しました");
    }
}

/// 届いているイベントを取り出す（エンジンがフレームの頭で 1 回呼ぶ）。基盤が無ければ空。
pub fn poll_events() -> Vec<String> {
    let Some(bridge) = current_bridge() else {
        return Vec::new();
    };
    panic::catch_unwind(AssertUnwindSafe(|| bridge.poll_events())).unwrap_or_else(|_| {
        eprintln!("{LOG_PREFIX} イベントの取り出し中に panic しました（このフレームの分は捨てます）");
        Vec::new()
    })
}

// ============================================================
//  ユニットテスト（プロセスで共有する実装は、副作用の無い命令だけで確かめる）
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;

    /// 名前の約束に合わない命令は、実装へ届く前に invalid_name で断る。
    #[test]
    fn invalid_names_are_rejected_before_dispatch() {
        assert_eq!(invoke("", wire::METHOD_PING, "{}"), Err(wire::ERROR_INVALID_NAME.to_string()));
        assert_eq!(invoke(wire::MODULE_PLATFORM, "Ping", "{}"), Err(wire::ERROR_INVALID_NAME.to_string()));
        assert_eq!(invoke("platform.x", wire::METHOD_PING, "{}"), Err(wire::ERROR_INVALID_NAME.to_string()));
    }

    /// デスクトップ（テストを走らせるホスト）は登録が無くても模擬で動き、状態は Simulated。
    #[cfg(not(target_os = "android"))]
    #[test]
    fn desktop_falls_back_to_simulator() {
        assert_eq!(platform::CURRENT.platform_bridge_fallback, PlatformBridgeFallback::DesktopSim);
        assert_eq!(status(), PlatformBridgeStatus::Simulated);
        let reply = invoke(wire::MODULE_PLATFORM, wire::METHOD_PING, r#"{"nonce":"n"}"#).expect("模擬へ届かない");
        let value: serde_json::Value = serde_json::from_str(&reply).unwrap();
        assert_eq!(value[wire::KEY_OK], serde_json::Value::Bool(true));
        assert_eq!(value["echo"]["nonce"], serde_json::Value::from("n"));
    }

    /// Android は登録が無ければ「使えない」（Java 側が居ないのに成功を装わない）。
    #[test]
    fn android_has_no_fallback() {
        assert_eq!(platform::ANDROID.platform_bridge_fallback, PlatformBridgeFallback::Unavailable);
        assert_eq!(platform::DESKTOP.platform_bridge_fallback, PlatformBridgeFallback::DesktopSim);
    }
}
