// ============================================================
//  text_ops.rs — TextComponent のエディタ操作（インスペクタからのフィールド更新）
//
//  インスペクタが送る `SET_TEXT_FIELD:{actor},{slot},{key},{value}` を受け、
//  対象スロットの `TextComponent` へ反映する。
//  Undo は `field_edit.rs` の共通機構が担当するので、ここでは記録しない。
// ============================================================

use crate::engine::components::{
    ComponentKind, TextAlign, TextComponent, TextVerticalAlign, MAX_BOX_SIZE, MAX_OUTLINE_WIDTH,
    MAX_SHADOW_OFFSET, MAX_SHADOW_SOFTNESS, MAX_TEXT_WEIGHT, MIN_BOX_SIZE, MIN_OUTLINE_WIDTH,
    MIN_SHADOW_SOFTNESS,
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
    /// `line_spacing` / `layer` / `font_path` / `icon_set` / `outline_width` / `outline_color` /
    /// `box_width` / `box_height` / `wrap` / `weight` /
    /// `shadow_offset_x` / `shadow_offset_y` / `shadow_color` / `shadow_softness`。
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
            // 使用フォントのアセットパス。パスに改行は入らないので
            // content のようなエスケープ解除は行わず、そのまま格納する。
            // 空文字 = 組み込みフォントへ戻す、という意味を持つ。
            "font_path" => tc.font_path = value.to_string(),
            // アイコンセット（.icons）のアセットパス。font_path と同じく
            // 改行を含まないためエスケープ解除は行わない。
            // 空文字 = アイコンセット未使用（[icon:] は未解決になる）。
            "icon_set" => tc.icon_set = value.to_string(),
            // ── 色（RGBA。"r,g,b,a" 形式）──────────────────────
            "color" => {
                if let Some(rgba) = parse_rgba(value) {
                    tc.color = rgba;
                }
            }
            "outline_color" => {
                if let Some(rgba) = parse_rgba(value) {
                    tc.outline_color = rgba;
                }
            }
            "shadow_color" => {
                if let Some(rgba) = parse_rgba(value) {
                    tc.shadow_color = rgba;
                }
            }
            // ── 列挙（小文字キー文字列。未知の値は既存値を保つ）──
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
            // ── 真偽値（インスペクタのチェックボックスは 0/1 の数値で届く）──
            "wrap" => {
                if let Ok(v) = value.parse::<f32>() {
                    tc.wrap = v != 0.0;
                }
            }
            // ── 整数 ─────────────────────────────────────────
            "layer" => {
                if let Ok(v) = value.parse::<i32>() {
                    tc.layer = v;
                }
            }
            // ── 数値（値域は clamp_numeric_field が唯一の定義）──
            _ => {
                if let Some(v) = clamp_numeric_field(key, value) {
                    match key {
                        "font_size" => tc.font_size = v,
                        "line_spacing" => tc.line_spacing = v,
                        "outline_width" => tc.outline_width = v,
                        // 枠（幅 0 = 枠なし。高さは「最小高さ」で内容により伸びる）
                        "box_width" => tc.box_width = v,
                        "box_height" => tc.box_height = v,
                        // 文字の太さ（負で細く・正で太く）
                        "weight" => tc.weight = v,
                        // ドロップシャドウ
                        "shadow_offset_x" => tc.shadow_offset_x = v,
                        "shadow_offset_y" => tc.shadow_offset_y = v,
                        "shadow_softness" => tc.shadow_softness = v,
                        // clamp_numeric_field が Some を返すキーはここで必ず捌く。
                        _ => {}
                    }
                }
            }
        }
    }
}

/// 数値フィールドの入力値を許容範囲へ丸める（**キーごとの値域の唯一の定義**）。
///
/// 数値にできない値・数値フィールドでないキーは `None` を返す
/// （呼び出し側は既存値を保つ＝不正入力で表示が壊れない）。
fn clamp_numeric_field(key: &str, value: &str) -> Option<f32> {
    let v = value.parse::<f32>().ok()?;
    Some(match key {
        "font_size" => v.clamp(MIN_FONT_SIZE, MAX_FONT_SIZE),
        "line_spacing" => v.clamp(MIN_LINE_SPACING, MAX_LINE_SPACING),
        "outline_width" => v.clamp(MIN_OUTLINE_WIDTH, MAX_OUTLINE_WIDTH),
        // 枠サイズ（幅・高さ共通。0 = 枠なし／高さ 0 = 内容なりに伸びる）
        "box_width" | "box_height" => v.clamp(MIN_BOX_SIZE, MAX_BOX_SIZE),
        // 太さは符号つき（負で細く・正で太く）
        "weight" => v.clamp(-MAX_TEXT_WEIGHT, MAX_TEXT_WEIGHT),
        // 影のオフセットも符号つき
        "shadow_offset_x" | "shadow_offset_y" => v.clamp(-MAX_SHADOW_OFFSET, MAX_SHADOW_OFFSET),
        "shadow_softness" => v.clamp(MIN_SHADOW_SOFTNESS, MAX_SHADOW_SOFTNESS),
        _ => return None,
    })
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
    use super::*;

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

    /// 数値フィールドは各キーの値域へ丸められる（下限・上限の両方）。
    #[test]
    fn numeric_fields_are_clamped_per_key() {
        // 枠サイズ: 負値は 0（枠なし）へ、巨大値は上限へ。
        assert_eq!(clamp_numeric_field("box_width", "-10"), Some(MIN_BOX_SIZE));
        assert_eq!(clamp_numeric_field("box_height", "999999"), Some(MAX_BOX_SIZE));
        // 太さは符号つきで対称にクランプされる。
        assert_eq!(clamp_numeric_field("weight", "-999"), Some(-MAX_TEXT_WEIGHT));
        assert_eq!(clamp_numeric_field("weight", "999"), Some(MAX_TEXT_WEIGHT));
        assert_eq!(clamp_numeric_field("weight", "-2.5"), Some(-2.5));
        // 影のオフセットも符号つき。
        assert_eq!(
            clamp_numeric_field("shadow_offset_x", "-99999"),
            Some(-MAX_SHADOW_OFFSET)
        );
        assert_eq!(
            clamp_numeric_field("shadow_offset_y", "99999"),
            Some(MAX_SHADOW_OFFSET)
        );
        // ぼかしは 0 以上。
        assert_eq!(
            clamp_numeric_field("shadow_softness", "-1"),
            Some(MIN_SHADOW_SOFTNESS)
        );
        // 既存フィールドの値域も同じ入口で守られている。
        assert_eq!(clamp_numeric_field("font_size", "0"), Some(MIN_FONT_SIZE));
        assert_eq!(
            clamp_numeric_field("outline_width", "-1"),
            Some(MIN_OUTLINE_WIDTH)
        );
    }

    /// 数値でない値・数値フィールドでないキーは None（既存値を保つ）。
    #[test]
    fn non_numeric_input_is_rejected() {
        assert_eq!(clamp_numeric_field("box_width", "abc"), None);
        assert_eq!(clamp_numeric_field("content", "12"), None);
        assert_eq!(clamp_numeric_field("unknown_key", "12"), None);
    }
}
