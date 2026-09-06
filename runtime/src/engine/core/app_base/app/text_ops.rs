// ============================================================
//  text_ops.rs — TextComponent のエディタ操作（インスペクタからのフィールド更新）
//
//  インスペクタが送る `SET_TEXT_FIELD:{actor},{slot},{key},{value}` を受け、
//  対象スロットの `TextComponent` へ反映する。
//  Undo は `field_edit.rs` の共通機構が担当するので、ここでは記録しない。
// ============================================================

use crate::engine::components::{
    ComponentKind, TextAlign, TextComponent, TextVerticalAlign, MAX_OUTLINE_WIDTH,
    MIN_OUTLINE_WIDTH,
};

use super::App;

// ─── 入力値の範囲（マジックナンバーをここへ集約する）───────────────

/// フォントサイズの下限（0 以下は描画できないため）。
const MIN_FONT_SIZE: f32 = 1.0;
/// フォントサイズの上限（アトラス 1 枚に収まる現実的な上限）。
const MAX_FONT_SIZE: f32 = 512.0;
/// 行送り倍率の下限（行が逆順に重なるのを防ぐ）。
const MIN_LINE_SPACING: f32 = 0.1;
/// 行送り倍率の上限。
const MAX_LINE_SPACING: f32 = 10.0;
/// 色成分の要素数（RGBA）。
const COLOR_COMPONENTS: usize = 4;
/// 色成分の下限・上限。
const COLOR_MIN: f32 = 0.0;
const COLOR_MAX: f32 = 1.0;

impl App {
    /// インスペクタから届いた TextComponent のフィールド更新を反映する。
    ///
    /// `key` は
    /// `content` / `font_size` / `color` / `align` / `vertical_align` /
    /// `line_spacing` / `layer` / `font_path` / `outline_width` / `outline_color`。
    /// パースできない値は無視する（不正入力で既存値を壊さない）。
    pub(super) fn handle_set_text_field(
        &mut self,
        actor_dfs_id: u32,
        slot_idx: u32,
        key: &str,
        value: &str,
    ) {
        use super::find_actor_by_dfs;

        let wl = self.active_world_line;
        // 対象スロットのエンティティを解決する（handle_set_line_renderer_field と同流儀）。
        let slot_entity = {
            let Some(scene) = &self.scene else { return };
            let mut c = 0u32;
            find_actor_by_dfs(&scene.actors, wl, actor_dfs_id, &mut c)
                .and_then(|a| a.slots().get(slot_idx as usize))
                .filter(|s| s.kind == ComponentKind::Text)
                .map(|s| s.entity)
        };
        let Some(entity) = slot_entity else { return };
        let Some(scene) = &mut self.scene else { return };
        let Some(tc) = scene.world.get_mut::<TextComponent>(entity) else {
            return;
        };

        match key {
            // 表示文字列。改行はインスペクタ側で "\n" のリテラル 2 文字に
            // エスケープして送られる（IPC は 1 行 1 コマンドのため生の改行を送れない）。
            "content" => tc.content = unescape_content(value),
            "font_size" => {
                if let Ok(v) = value.parse::<f32>() {
                    tc.font_size = v.clamp(MIN_FONT_SIZE, MAX_FONT_SIZE);
                }
            }
            "color" => {
                if let Some(rgba) = parse_rgba(value) {
                    tc.color = rgba;
                }
            }
            // 使用フォントのアセットパス。パスに改行は入らないので
            // content のようなエスケープ解除は行わず、そのまま格納する。
            // 空文字 = 組み込みフォントへ戻す、という意味を持つ。
            "font_path" => tc.font_path = value.to_string(),
            "outline_width" => {
                if let Ok(v) = value.parse::<f32>() {
                    tc.outline_width = v.clamp(MIN_OUTLINE_WIDTH, MAX_OUTLINE_WIDTH);
                }
            }
            "outline_color" => {
                if let Some(rgba) = parse_rgba(value) {
                    tc.outline_color = rgba;
                }
            }
            "align" => {
                if let Some(a) = TextAlign::from_key(value) {
                    tc.align = a;
                }
            }
            "vertical_align" => {
                if let Some(a) = TextVerticalAlign::from_key(value) {
                    tc.vertical_align = a;
                }
            }
            "line_spacing" => {
                if let Ok(v) = value.parse::<f32>() {
                    tc.line_spacing = v.clamp(MIN_LINE_SPACING, MAX_LINE_SPACING);
                }
            }
            "layer" => {
                if let Ok(v) = value.parse::<i32>() {
                    tc.layer = v;
                }
            }
            _ => {}
        }
    }
}

/// "r,g,b,a" 形式の色文字列を RGBA 配列へパースする。
///
/// 要素数が違う・数値にできない要素がある場合は `None`（呼び出し側は既存値を保つ）。
/// 各成分は 0..1 へクランプする。文字色と縁取り色で同じ規則を使う。
fn parse_rgba(value: &str) -> Option<[f32; COLOR_COMPONENTS]> {
    let parts: Vec<&str> = value.split(',').collect();
    if parts.len() != COLOR_COMPONENTS {
        return None;
    }
    let mut rgba = [0.0f32; COLOR_COMPONENTS];
    for (dst, src) in rgba.iter_mut().zip(parts.iter()) {
        *dst = src.trim().parse::<f32>().ok()?.clamp(COLOR_MIN, COLOR_MAX);
    }
    Some(rgba)
}

/// インスペクタから届く文字列のエスケープを解除する。
///
/// IPC は 1 コマンド 1 行のテキストプロトコルなので、改行は `\n`
/// （バックスラッシュ + n）の 2 文字に置換されて届く。
/// バックスラッシュ自体は `\\` で送られる。
fn unescape_content(raw: &str) -> String {
    let mut out = String::with_capacity(raw.len());
    let mut chars = raw.chars();
    while let Some(c) = chars.next() {
        if c != '\\' {
            out.push(c);
            continue;
        }
        // エスケープシーケンス。未知の記号はバックスラッシュごと残す
        // （情報を落とさないほうがユーザーの意図に近い）。
        match chars.next() {
            Some('n') => out.push('\n'),
            Some('t') => out.push('\t'),
            Some('\\') => out.push('\\'),
            Some(other) => {
                out.push('\\');
                out.push(other);
            }
            None => out.push('\\'),
        }
    }
    out
}

// ============================================================
//  ユニットテスト
// ============================================================

#[cfg(test)]
mod tests {
    use super::{parse_rgba, unescape_content};

    /// `\n` は改行、`\t` はタブ、`\\` はバックスラッシュ 1 文字になる。
    #[test]
    fn unescapes_known_sequences() {
        assert_eq!(unescape_content(r"a\nb"), "a\nb");
        assert_eq!(unescape_content(r"a\tb"), "a\tb");
        assert_eq!(unescape_content(r"a\\b"), r"a\b");
    }

    /// 未知のエスケープはバックスラッシュごとそのまま残す。
    #[test]
    fn keeps_unknown_escapes_verbatim() {
        assert_eq!(unescape_content(r"a\qb"), r"a\qb");
    }

    /// 末尾の孤立したバックスラッシュでも panic しない。
    #[test]
    fn trailing_backslash_is_safe() {
        assert_eq!(unescape_content("a\\"), "a\\");
    }

    /// エスケープが無い文字列（日本語含む）はそのまま通る。
    #[test]
    fn plain_text_passes_through() {
        assert_eq!(unescape_content("所持金: 1200 円"), "所持金: 1200 円");
    }

    /// 正常な "r,g,b,a" はそのままパースされ、範囲外はクランプされる。
    #[test]
    fn parses_and_clamps_rgba() {
        assert_eq!(parse_rgba("1,0.5,0,1"), Some([1.0, 0.5, 0.0, 1.0]));
        assert_eq!(parse_rgba(" 2 , -1 , 0.25 , 1 "), Some([1.0, 0.0, 0.25, 1.0]));
    }

    /// 要素数不足・非数値は None（既存値を壊さない）。
    #[test]
    fn rejects_malformed_rgba() {
        assert_eq!(parse_rgba("1,0,0"), None);
        assert_eq!(parse_rgba("1,0,0,x"), None);
    }
}
