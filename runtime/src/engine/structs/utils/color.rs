// ============================================================
//  color.rs — RGBA カラー型
// ============================================================

/// RGBA カラー（各チャンネル 0.0〜1.0）。
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct Color {
    pub r: f32,
    pub g: f32,
    pub b: f32,
    pub a: f32,
}

impl Color {
    pub const fn new(r: f32, g: f32, b: f32, a: f32) -> Self {
        Self { r, g, b, a }
    }

    pub const fn rgb(r: f32, g: f32, b: f32) -> Self {
        Self { r, g, b, a: 1.0 }
    }

    pub fn with_alpha(self, a: f32) -> Self {
        Self { a, ..self }
    }

    /// `[r, g, b, a]` 配列に変換する（GPU バッファ・描画関数向け）。
    pub const fn to_array(self) -> [f32; 4] {
        [self.r, self.g, self.b, self.a]
    }

    // ── 基本色 ────────────────────────────────────────────────

    pub const BLACK: Self = Self::rgb(0.0, 0.0, 0.0);
    pub const WHITE: Self = Self::rgb(1.0, 1.0, 1.0);
    pub const RED: Self = Self::rgb(1.0, 0.0, 0.0);
    pub const GREEN: Self = Self::rgb(0.0, 1.0, 0.0);
    pub const BLUE: Self = Self::rgb(0.0, 0.0, 1.0);
    pub const YELLOW: Self = Self::rgb(1.0, 1.0, 0.0);
    pub const CYAN: Self = Self::rgb(0.0, 1.0, 1.0);
    pub const MAGENTA: Self = Self::rgb(1.0, 0.0, 1.0);
    pub const TRANSPARENT: Self = Self::new(0.0, 0.0, 0.0, 0.0);

    // ── ギズモ軸色（高彩度）──────────────────────────────────

    pub const GIZMO_X: Self = Self::rgb(1.0, 0.1, 0.1);
    pub const GIZMO_Y: Self = Self::rgb(0.1, 1.0, 0.1);
    pub const GIZMO_Z: Self = Self::rgb(0.1, 0.1, 1.0);
}

impl From<Color> for [f32; 4] {
    fn from(c: Color) -> Self {
        c.to_array()
    }
}

impl From<[f32; 4]> for Color {
    fn from(a: [f32; 4]) -> Self {
        Self::new(a[0], a[1], a[2], a[3])
    }
}
