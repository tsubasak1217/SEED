use winit::dpi::LogicalSize;
use winit::event_loop::ActiveEventLoop;
use winit::window::{Window, WindowAttributes};

/// ウィンドウ生成時に使用するパラメータ。
///
/// # フィールド
/// - `title`      : ウィンドウタイトルバーに表示する文字列（`'static` 参照）
/// - `width`      : ウィンドウの論理幅（ポイント単位）
/// - `height`     : ウィンドウの論理高さ（同上）
/// - `visible`    : 生成直後にウィンドウを表示するか
/// - `parent_hwnd`: 親ウィンドウの HWND（Some の場合は子ウィンドウとして生成）
pub struct WindowConfig {
    pub title: &'static str,
    pub width: f64,
    pub height: f64,
    pub visible: bool,
    /// エディタ埋込み時に WPF 側コンテナの HWND を指定する。
    /// `Some` の場合、デコレーション（タイトルバー・ボーダー）なしの子ウィンドウを生成する。
    pub parent_hwnd: Option<isize>,
}

impl Default for WindowConfig {
    fn default() -> Self {
        Self {
            title: "SEED",
            width: 800.0,
            height: 600.0,
            visible: false,
            parent_hwnd: None,
        }
    }
}

/// `WindowConfig` の設定に従ってネイティブウィンドウを生成して返す。
///
/// # 引数
/// - `event_loop`: アクティブなイベントループへの参照
/// - `config`    : ウィンドウの初期設定
///
/// # 子ウィンドウモード（`config.parent_hwnd` が `Some` の場合）
/// Windows プラットフォーム拡張 `WindowAttributesExtWindows::with_parent_window` を使い、
/// 指定された HWND の子ウィンドウとして生成する。
/// デコレーションは自動的に無効化される。
pub fn create_window(event_loop: &ActiveEventLoop, config: &WindowConfig) -> Window {
    let attributes = build_attributes(config);
    event_loop
        .create_window(attributes)
        .expect("Failed to create window")
}

/// `WindowConfig` から `WindowAttributes` を構築する。
/// Windows の親ウィンドウ設定はプラットフォーム固有の分岐で適用する。
fn build_attributes(config: &WindowConfig) -> WindowAttributes {
    let attrs = Window::default_attributes()
        .with_title(config.title)
        .with_inner_size(LogicalSize::new(config.width, config.height))
        .with_visible(config.visible);

    apply_parent_hwnd(attrs, config.parent_hwnd)
}

/// Windows のみ: 親 HWND が指定されている場合に子ウィンドウ属性を付加する。
#[cfg(target_os = "windows")]
fn apply_parent_hwnd(attrs: WindowAttributes, parent_hwnd: Option<isize>) -> WindowAttributes {
    use raw_window_handle::{RawWindowHandle, Win32WindowHandle};
    use std::num::NonZeroIsize;
    use winit::platform::windows::WindowAttributesExtWindows;

    match parent_hwnd {
        Some(hwnd) => {
            let handle = Win32WindowHandle::new(
                NonZeroIsize::new(hwnd).expect("parent HWND must not be zero"),
            );
            // Safety: 呼び出し元（App::resumed）が有効な HWND を保証する。
            // WPF の BuildWindowCore が返したコンテナウィンドウの HWND を渡している。
            //
            // with_drag_and_drop(false): WPF 側から OLE D&D を行う場合、winit が
            // RegisterDragDrop で登録した IDropTarget へのクロスプロセス COM 呼び出しが
            // 発生し、UI スレッドがフリーズする原因となる。埋め込みウィンドウでは OLE
            // ドロップ受付が不要なため無効化する。
            unsafe {
                attrs
                    .with_decorations(false)
                    .with_drag_and_drop(false)
                    .with_parent_window(Some(RawWindowHandle::Win32(handle)))
            }
        }
        None => attrs,
    }
}

/// Windows 以外: 親ウィンドウ指定は無視してそのまま返す。
#[cfg(not(target_os = "windows"))]
fn apply_parent_hwnd(attrs: WindowAttributes, _parent_hwnd: Option<isize>) -> WindowAttributes {
    attrs
}
