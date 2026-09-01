// ============================================================
//  font/screen_hint.rs — カーソル脇に出すスクリーンスペース操作ガイド
//
//  【責務】
//  「いま何ができるか」を数行のテキストで、指定したスクリーン座標へ描く。
//  ロジック配置モードの「左クリック: 配置 / 右クリック: 取消」「ドラッグ: 半径 3.20 m」
//  のような、**操作中だけ出したい短い案内**が対象。
//
//  【なぜ専用モジュールなのか】
//  同じ FontSystem は軸ギズモ（`axis_gizmo.rs`）も持っているが、あちらは
//  「ビューポート隅の軸ラベル」という別の責務で、表示 ON/OFF も別設定である。
//  ガイドをそこへ相乗りさせると「軸ギズモを消すとガイドも消える」ことになる。
//  単一責任の原則に従い、ガイドはガイド専用のフォントシステムを持つ。
//
//  【視認性】
//  シーンの明暗に関わらず読めるように、**同じ文字列を 2 回描く**
//  （暗色を 1px ずらして影 → 明色を本体）。背景クアッドを足すより
//  パイプラインが増えず、テキストの上下左右どこが背景でも均一に効く。
// ============================================================

use super::{FontConfig, FontSystem, GpuTextBatch, TextBatch};

// ─── 定数（マジックナンバー禁止）─────────────────────────────

/// ガイド文字の大きさ [px]。
const HINT_FONT_SIZE: f32 = 14.0;

/// 行送り（フォントサイズの何倍か）。
const HINT_LINE_HEIGHT_RATIO: f32 = 1.35;

/// 影のずらし量 [px]（右下方向へ）。
const HINT_SHADOW_OFFSET: f32 = 1.5;

/// 本文の色（やや暖かい白。シーンの白飛びと区別が付く）。
const HINT_COLOR: [f32; 4] = [1.0, 0.98, 0.90, 1.0];

/// 影の色（黒・やや透過）。
const HINT_SHADOW_COLOR: [f32; 4] = [0.0, 0.0, 0.0, 0.85];

/// カーソルからガイド左上までのオフセット X [px]（カーソル自身に重ねない）。
pub const HINT_CURSOR_OFFSET_X: f32 = 18.0;

/// カーソルからガイド左上までのオフセット Y [px]（＝カーソルの右下に出す）。
pub const HINT_CURSOR_OFFSET_Y: f32 = 20.0;

/// ガイドを画面端から出さないための余白 [px]。
const HINT_SCREEN_MARGIN: f32 = 8.0;

/// 折り返さない前提での 1 文字あたりの概算幅（フォントサイズの何倍か）。
///
/// 画面右端・下端でガイドがはみ出さないよう位置を戻すためだけに使う概算値。
/// 正確な字幅はグリフを組むまで分からないが、はみ出し防止には概算で足りる。
const HINT_CHAR_WIDTH_RATIO: f32 = 0.95;

// ── ScreenHintOverlay ────────────────────────────────────────

/// カーソル脇の操作ガイドを描くレンダラー。
pub struct ScreenHintOverlay {
    font_system: FontSystem,
}

impl ScreenHintOverlay {
    /// 既定フォントで初期化する。
    pub fn new(
        device: &wgpu::Device,
        surface_format: wgpu::TextureFormat,
        depth_format: wgpu::TextureFormat,
    ) -> Option<Self> {
        FontSystem::new(device, surface_format, depth_format, FontConfig::default())
            .ok()
            .map(|font_system| Self { font_system })
    }

    /// ガイドのテキストバッチを構築する（行が無ければ None）。
    ///
    /// `anchor_x`, `anchor_y` はガイドの左上のスクリーン座標 [px]。
    /// 画面外へはみ出す場合は内側へ寄せる。
    pub fn build(
        &mut self,
        lines: &[String],
        anchor_x: f32,
        anchor_y: f32,
        screen_w: f32,
        screen_h: f32,
        device: &wgpu::Device,
        queue: &wgpu::Queue,
    ) -> Option<GpuTextBatch> {
        if lines.is_empty() || screen_w <= 0.0 || screen_h <= 0.0 {
            return None;
        }

        let line_h = HINT_FONT_SIZE * HINT_LINE_HEIGHT_RATIO;
        let (x, y) = clamp_anchor(lines, anchor_x, anchor_y, screen_w, screen_h, line_h);

        let mut batch = TextBatch::new();
        for (i, line) in lines.iter().enumerate() {
            let pen_y = y + line_h * i as f32;
            // 影 → 本体 の順に積む（後に積んだほうが上に乗る）。
            self.font_system.queue_text(
                &mut batch, line,
                x + HINT_SHADOW_OFFSET, pen_y + HINT_SHADOW_OFFSET,
                HINT_FONT_SIZE, HINT_SHADOW_COLOR, screen_w, screen_h,
            );
            self.font_system.queue_text(
                &mut batch, line, x, pen_y,
                HINT_FONT_SIZE, HINT_COLOR, screen_w, screen_h,
            );
        }
        self.font_system.flush(queue);
        self.font_system.build_gpu_batch(&batch, device)
    }

    /// メインレンダーパスへ描画する（深度テストなし、UI オーバーレイ）。
    pub fn draw<'pass>(
        &'pass self,
        batch: &'pass GpuTextBatch,
        pass: &mut wgpu::RenderPass<'pass>,
    ) {
        self.font_system.draw_text_batch(batch, pass);
    }
}

// ── 位置決め（純関数＝テスト可能）────────────────────────────

/// ガイドの左上位置を画面内へ収める。
///
/// 右端・下端ではガイドがはみ出すので内側へ戻す。字幅は概算（`HINT_CHAR_WIDTH_RATIO`）
/// で見積もる。多少ずれても「読めない位置に出ない」ことが目的なので概算で足りる。
pub fn clamp_anchor(
    lines:    &[String],
    anchor_x: f32,
    anchor_y: f32,
    screen_w: f32,
    screen_h: f32,
    line_h:   f32,
) -> (f32, f32) {
    let max_chars = lines.iter().map(|l| l.chars().count()).max().unwrap_or(0) as f32;
    let est_w = max_chars * HINT_FONT_SIZE * HINT_CHAR_WIDTH_RATIO;
    let est_h = line_h * lines.len() as f32;

    let x = anchor_x
        .min(screen_w - est_w - HINT_SCREEN_MARGIN)
        .max(HINT_SCREEN_MARGIN);
    let y = anchor_y
        .min(screen_h - est_h - HINT_SCREEN_MARGIN)
        .max(HINT_SCREEN_MARGIN);
    (x, y)
}

// ============================================================
//  テスト
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;

    /// 画面中央付近ならカーソル脇の位置がそのまま使われること。
    #[test]
    fn anchor_is_kept_when_it_fits() {
        let lines = vec!["左クリック: 配置".to_string()];
        let (x, y) = clamp_anchor(&lines, 400.0, 300.0, 1920.0, 1080.0, 20.0);
        assert_eq!((x, y), (400.0, 300.0));
    }

    /// 右下端では画面内へ戻されること（ガイドが見切れない）。
    #[test]
    fn anchor_is_pulled_back_at_the_screen_edge() {
        let lines = vec!["左クリック: 配置 / 右クリック: 取消".to_string()];
        let (x, y) = clamp_anchor(&lines, 1900.0, 1070.0, 1920.0, 1080.0, 20.0);
        assert!(x < 1900.0, "右端では左へ戻ること: {x}");
        assert!(y < 1070.0, "下端では上へ戻ること: {y}");
        assert!(x >= HINT_SCREEN_MARGIN && y >= HINT_SCREEN_MARGIN, "余白より内側に入らないこと");
    }

    /// 極端に狭い画面でも左上余白より外へは出ないこと（負の座標を作らない）。
    #[test]
    fn anchor_never_goes_negative() {
        let lines = vec!["とても長いガイド文字列がここに入ります".to_string()];
        let (x, y) = clamp_anchor(&lines, 5.0, 5.0, 100.0, 60.0, 20.0);
        assert_eq!(x, HINT_SCREEN_MARGIN);
        assert_eq!(y, HINT_SCREEN_MARGIN);
    }
}
