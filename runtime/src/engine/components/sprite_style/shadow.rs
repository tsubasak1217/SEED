// ============================================================
//  sprite_style/shadow.rs — スプライトのぼかしの影（W2-4・任意の機能）
//
//  形（角丸・楕円・弧）と同じ SDF を `offset` だけずらし、`blur` の幅でぼかして、形の**後ろ**に描く
//  （CSS の box-shadow と同じ見た目の簡易版。ぼかしはガウス関数の近似で、σ = blur ÷ 2）。
//  色はスプライトの color（塗りの色）とは独立（塗りを透明にした形にも影が付く。フェードするときは影の色も変える）。
//  描き方の正典は engine/core/renderer/ui_shape/sdf.rs の `shadow_coverage`。
// ============================================================

use serde::{Deserialize, Serialize};

/// 影の色の既定（35% の黒）。
const DEFAULT_COLOR: [f32; 4] = [0.0, 0.0, 0.0, 0.35];
/// 影のずれの既定（右へ 0・下へ 4。キャンバスの単位）。
const DEFAULT_OFFSET: [f32; 2] = [0.0, 4.0];
/// 影のぼかしの既定（キャンバスの単位）。
const DEFAULT_BLUR: f32 = 8.0;

/// serde の既定値: 影の色。
fn default_color() -> [f32; 4] {
    DEFAULT_COLOR
}

/// serde の既定値: 影のずれ。
fn default_offset() -> [f32; 2] {
    DEFAULT_OFFSET
}

/// serde の既定値: 影のぼかし。
fn default_blur() -> f32 {
    DEFAULT_BLUR
}

/// スプライトのぼかしの影。
#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
pub struct SpriteShadow {
    /// 影を描くか。
    #[serde(default)]
    pub enabled: bool,
    /// 影の色（RGBA 0..1）。
    #[serde(default = "default_color")]
    pub color: [f32; 4],
    /// 影のずれ（キャンバスの単位。X 右・Y 下）。
    #[serde(default = "default_offset")]
    pub offset: [f32; 2],
    /// ぼかしの幅（キャンバスの単位。0 = くっきり）。
    #[serde(default = "default_blur")]
    pub blur: f32,
}

impl Default for SpriteShadow {
    fn default() -> Self {
        Self { enabled: false, color: DEFAULT_COLOR, offset: DEFAULT_OFFSET, blur: DEFAULT_BLUR }
    }
}

impl SpriteShadow {
    /// すべての欄が既定か（保存を省く判定。`skip_serializing_if`）。
    pub fn is_default(&self) -> bool {
        *self == Self::default()
    }

    /// 影が見えるか（有効で、色が透明でない）。
    pub fn is_visible(&self) -> bool {
        self.enabled && self.color[3] > 0.0
    }
}

// ============================================================
//  単体テスト
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;

    /// 欄の無い旧データ（`{}`）は既定（描かない）で読める。
    #[test]
    fn empty_json_is_disabled_default() {
        let s: SpriteShadow = serde_json::from_str("{}").unwrap();
        assert!(s.is_default());
        assert!(!s.is_visible());
    }

    /// 有効でも透明な影は見えない。
    #[test]
    fn transparent_shadow_is_not_visible() {
        let s = SpriteShadow { enabled: true, color: [0.0, 0.0, 0.0, 0.0], ..SpriteShadow::default() };
        assert!(!s.is_visible());
        let s = SpriteShadow { enabled: true, ..SpriteShadow::default() };
        assert!(s.is_visible());
    }
}
