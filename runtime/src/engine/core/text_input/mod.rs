// ============================================================
//  text_input/ — 文字入力の受け口（W2-6a。正典は docs/ui_text_input.md）
//
//  【全体】（docs/app_platform_roadmap.md §3.8.4 の E-06 の決定）
//    - エンジンの入力欄の状態を 1 つの形（本文・選択・変換中の区間。添字は UTF-8 のバイト）で持ち、
//      プラットフォームの差は受け口で吸収する
//        Windows … winit の Ime::Preedit / Commit と PC のキー（app/text_input_hooks.rs が hub へ）
//        Android … MainActivity の上書き（stateChanged・onEditorAction・キーボードの表示と高さ）→ JNI → inbox
//                  （runtime/android/native の text_input）。操作は複製した AndroidApp の API（platform.rs の登録）
//        スクリプト … scripting/text_input_bridge.rs（SEED.TextInput）
//        IPC … INPUT_TEXT:（input/inject/text_command.rs。PC で日本語の IME・Android の知らせを模擬する）
//  【構成】
//    indices    … UTF-8・UTF-16・文字・書記素の添字の変換（純関数）
//    config     … 入力欄の設定（種類・アクション・最大の長さ・貼り付けとコピーの許可）
//    edit_state … 状態と編集の操作（純ロジック）
//    filter     … 決まり（数字だけ・最大の長さ・貼り付けの禁止）を当てる（純関数）
//    keys       … PC のキー → 編集の操作（純関数）
//    clipboard  … PC のクリップボード（Windows は OS、ほかはプロセスの中の控え）
//    session    … フォーカスのある入力欄 1 つぶんの場（知らせ・IME との食い違い）
//    hub        … プロセスに 1 つの窓口（場・キーボード・プラットフォームへの命令）
//    platform   … プラットフォームへの命令の形と、Android の実装の登録
//    inbox      … Android の JNI からの知らせの箱（イベントループを起こす）
// ============================================================

pub mod clipboard;
pub mod config;
pub mod edit_state;
pub mod filter;
pub mod hub;
pub mod inbox;
pub mod indices;
pub mod keys;
pub mod platform;
pub mod session;

pub use clipboard::{ClipboardAccess, MemoryClipboard, SystemClipboard};
pub use config::{TextInputAction, TextInputConfig, TextInputKind};
pub use edit_state::{TextEditState, Utf16State};
pub use hub::{with_hub, KeyboardState, TextInputHub, LOG_PREFIX};
pub use inbox::PlatformTextMessage;
pub use keys::{EditKey, EditModifiers};
pub use platform::{register_platform, registered_platform, PlatformCommand, TextInputPlatform};
pub use session::{KeyOutcome, TextInputEvent, TextSession};
