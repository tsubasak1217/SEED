// ============================================================
//  text_ops.rs — TextComponent のエディタ操作（インスペクタからのフィールド更新）
//
//  インスペクタが送る `SET_TEXT_FIELD:{actor},{slot},{key},{value}` を受け、
//  対象スロットの `TextComponent` へ反映する。
//  Undo は `field_edit.rs` の共通機構が担当するので、ここでは記録しない。
// ============================================================

use crate::engine::components::{ComponentKind, TextAlign, TextComponent, TextVerticalAlign};

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
    /// `line_spacing` / `layer`。
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
                // "r,g,b,a" をパースする。要素数が違えば無視。
                let parts: Vec<&str> = value.split(',').collect();
                if parts.len() == COLOR_COMPONENTS {
                    let mut rgba = [0.0f32; COLOR_COMPONENTS];
                    let mut ok = true;
                    for (dst, src) in rgba.iter_mut().zip(parts.iter()) {
                        match src.trim().parse::<f32>() {
                            Ok(v) => *dst = v.clamp(COLOR_MIN, COLOR_MAX),
                            Err(_) => ok = false,
                        }
                    }
                    if ok {
                        tc.color = rgba;
                    }
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
    use super::unescape_content;

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
}
