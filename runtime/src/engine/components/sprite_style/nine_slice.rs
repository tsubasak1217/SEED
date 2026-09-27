// ============================================================
//  sprite_style/nine_slice.rs — 画像の 9 スライス（W2-4）
//
//  テクスチャを枠の 4 辺の幅（`border`。テクスチャの画素）で 3×3 に切り、四隅は大きさを保ち、
//  辺と中央は伸ばす（Stretch）か繰り返す（Repeat）。ボタン・吹き出し・ネームプレートの枠を
//  どんな大きさでも崩さずに描くため。
//    - 描く枠の幅 = `border` × `scale`（キャンバスの単位。2 倍の解像度の画像なら scale 0.5）
//    - 向かい合う 2 辺の和がスプライトの幅（高さ）を超えるときは、2 辺を同じ割合で縮める（角が重ならない）
//    - Repeat は「丸めた回数」で並べる（CSS の border-image-repeat: round。端で切れたタイルを作らない）
//    - `fill_center` = false なら中央を描かない（枠だけ）
//  写像の正典は engine/core/renderer/ui_shape/nine_slice.rs。
// ============================================================

use serde::{Deserialize, Serialize};

/// 描く枠の倍率の既定（テクスチャの 1 画素 = キャンバスの 1 単位）。
const DEFAULT_SCALE: f32 = 1.0;

/// serde の既定値: 描く枠の倍率。
fn default_scale() -> f32 {
    DEFAULT_SCALE
}

/// serde の既定値: 中央を描く。
fn default_fill_center() -> bool {
    true
}

/// 辺・中央の埋め方。
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum NineSliceMode {
    /// 伸ばす（既定）。
    #[default]
    Stretch,
    /// 繰り返す（回数を丸めて、端で切れたタイルを作らない）。
    Repeat,
}

impl NineSliceMode {
    /// 保存・IPC・スクリプトで使う名前（serde の名前と同じ）。
    pub fn key(self) -> &'static str {
        match self {
            Self::Stretch => "stretch",
            Self::Repeat => "repeat",
        }
    }

    /// 名前から埋め方を引く（知らない名前は None）。
    pub fn from_key(key: &str) -> Option<Self> {
        match key.trim() {
            "stretch" => Some(Self::Stretch),
            "repeat" => Some(Self::Repeat),
            _ => None,
        }
    }
}

/// 画像の 9 スライス。
#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
pub struct SpriteNineSlice {
    /// 9 スライスで描くか（false なら画像を矩形いっぱいに伸ばす＝従来どおり）。
    #[serde(default)]
    pub enabled: bool,
    /// 枠の 4 辺の幅（左・上・右・下。テクスチャの画素）。
    #[serde(default)]
    pub border: [f32; 4],
    /// 描く枠の倍率（キャンバスの単位 ÷ テクスチャの画素）。
    #[serde(default = "default_scale")]
    pub scale: f32,
    /// 辺（上下左右の帯）の埋め方。
    #[serde(default)]
    pub edge_mode: NineSliceMode,
    /// 中央の埋め方。
    #[serde(default)]
    pub center_mode: NineSliceMode,
    /// 中央を描くか（false = 枠だけ）。
    #[serde(default = "default_fill_center")]
    pub fill_center: bool,
}

impl Default for SpriteNineSlice {
    fn default() -> Self {
        Self {
            enabled: false,
            border: [0.0; 4],
            scale: DEFAULT_SCALE,
            edge_mode: NineSliceMode::Stretch,
            center_mode: NineSliceMode::Stretch,
            fill_center: true,
        }
    }
}

impl SpriteNineSlice {
    /// すべての欄が既定か（保存を省く判定。`skip_serializing_if`）。
    pub fn is_default(&self) -> bool {
        *self == Self::default()
    }
}

// ============================================================
//  単体テスト
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;

    /// 欄の無い旧データ（`{}`）は既定（使わない・中央を描く・倍率 1）で読める。
    #[test]
    fn empty_json_is_disabled_default() {
        let n: SpriteNineSlice = serde_json::from_str("{}").unwrap();
        assert!(n.is_default());
        assert!(!n.enabled);
        assert!(n.fill_center);
        assert_eq!(n.scale, DEFAULT_SCALE);
    }

    /// 埋め方の名前の往復。
    #[test]
    fn mode_keys_round_trip() {
        for mode in [NineSliceMode::Stretch, NineSliceMode::Repeat] {
            assert_eq!(NineSliceMode::from_key(mode.key()), Some(mode));
            assert_eq!(serde_json::to_string(&mode).unwrap(), format!("\"{}\"", mode.key()));
        }
    }
}
