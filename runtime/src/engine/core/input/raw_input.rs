/// Windows の `GetAsyncKeyState` を使ったポーリング入力。
///
/// `WindowEvent::KeyboardInput` はウィンドウフォーカスに依存するため、
/// WPF に埋め込まれた Edit モードでは届かない。
/// `GetAsyncKeyState` はプロセス全体のキー状態を直接参照するのでフォーカス不問。
///
/// マウスデルタとスクロールはイベント由来（`DeviceEvent::MouseMotion` / `WindowEvent::MouseWheel`）
/// のまま累積する。これらは既にフォーカスなしで動作している。
pub struct RawInput {
    mouse_delta: (f32, f32),
    scroll:      f32,
}

impl RawInput {
    pub fn new() -> Self {
        Self { mouse_delta: (0.0, 0.0), scroll: 0.0 }
    }

    // ─── イベント受取（App 側から毎フレーム呼ぶ）──────────────

    /// `DeviceEvent::MouseMotion` の生デルタを累積する。
    pub fn push_mouse_delta(&mut self, dx: f64, dy: f64) {
        self.mouse_delta.0 += dx as f32;
        self.mouse_delta.1 += dy as f32;
    }

    /// `WindowEvent::MouseWheel` のスクロール量を累積する（ライン数）。
    pub fn push_scroll(&mut self, lines: f32) {
        self.scroll += lines;
    }

    /// フレーム末に呼ぶ。累積値をリセットする。
    pub fn end_frame(&mut self) {
        self.mouse_delta = (0.0, 0.0);
        self.scroll      = 0.0;
    }

    // ─── ポーリング API ───────────────────────────────────────

    /// 仮想キーが押されているか（`GetAsyncKeyState` でフォーカス不問にポーリング）。
    #[inline]
    fn vk_down(vk: i32) -> bool {
        #[cfg(windows)]
        {
            use windows_sys::Win32::UI::Input::KeyboardAndMouse::GetAsyncKeyState;
            return unsafe { (GetAsyncKeyState(vk) as u16 & 0x8000) != 0 };
        }
        #[cfg(not(windows))]
        { let _ = vk; false }
    }

    // マウスボタン
    pub fn is_right_mouse(&self) -> bool { Self::vk_down(0x02) } // VK_RBUTTON

    // キーボード
    pub fn is_w(&self) -> bool     { Self::vk_down(0x57) }
    pub fn is_a(&self) -> bool     { Self::vk_down(0x41) }
    pub fn is_s(&self) -> bool     { Self::vk_down(0x53) }
    pub fn is_d(&self) -> bool     { Self::vk_down(0x44) }
    pub fn is_q(&self) -> bool     { Self::vk_down(0x51) }
    pub fn is_e(&self) -> bool     { Self::vk_down(0x45) }
    pub fn is_shift(&self) -> bool {
        Self::vk_down(0xA0) || Self::vk_down(0xA1) // VK_LSHIFT / VK_RSHIFT
    }

    // マウス状態
    /// 今フレームのマウス移動デルタ (dx, dy)。
    pub fn mouse_delta(&self) -> (f32, f32) { self.mouse_delta }
    /// 今フレームのスクロール量（ライン数）。
    pub fn scroll(&self) -> f32 { self.scroll }
}
