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
//  【視認性 — 影 1 枚では足りなかった】
//  以前は暗色を右下へ 1.5px ずらした「影」を 1 枚敷くだけだった。これは
//  暗い背景では十分だが、**明るい背景（空・雪原・白い床・選択ハイライト）では
//  文字の左上側に影が無く、白文字が背景へ溶けて読めない**。
//  そこで視認性を 2 段構えにする:
//    ① 全方向アウトライン … 同じ文字列を 8 方向へずらして黒で描き、本体を上へ重ねる。
//       どちらの向きに明部が来ても必ず暗い縁が挟まる。
//    ② 背景プレート       … 文字の外接矩形＋パディングの角丸クアッドを黒半透明で敷く。
//       アウトラインだけでは白背景に「黒縁の白文字」が乗るだけでコントラストが
//       局所的にしか立たないため、面で沈める。
//  ①だけ・②だけでは足りず、両方あって初めて「どんな背景でも読める」になる。
//
//  【なぜ外接矩形を実測するのか】
//  プレートは文字にぴったり付いていないと「板だけ大きい」不格好になる。
//  字幅を文字数×係数で概算すると、日本語（全角）と英数字（半角）の混在で
//  簡単に 2 倍ずれる。グリフを組めば正確な外接矩形が分かるので、
//  **プレートのサイズも画面端でのはみ出し防止も実測値で行う**。
// ============================================================

use super::hint_plate::{GpuHintPlateBatch, HintPlate, HintPlateRect};
use super::{FontConfig, FontSystem, GpuTextBatch, TextBatch};

// ─── 定数（マジックナンバー禁止）─────────────────────────────

/// ガイド文字の大きさ [px]。
const HINT_FONT_SIZE: f32 = 14.0;

/// 行送り（フォントサイズの何倍か）。
const HINT_LINE_HEIGHT_RATIO: f32 = 1.35;

/// 本文の色（やや暖かい白。シーンの白飛びと区別が付く）。
const HINT_COLOR: [f32; 4] = [1.0, 0.98, 0.90, 1.0];

/// アウトラインの太さ [px]（本体からずらす距離）。
///
/// 1px 未満では明るい背景に負け、2.5px を超えると細い字が潰れる。
const HINT_OUTLINE_WIDTH: f32 = 1.7;

/// アウトラインの色（黒・ほぼ不透明）。
const HINT_OUTLINE_COLOR: [f32; 4] = [0.0, 0.0, 0.0, 0.95];

/// 斜め方向の単位成分（1/√2）。縦横と斜めで縁の太さが揃うようにする。
const HINT_OUTLINE_DIAG: f32 = std::f32::consts::FRAC_1_SQRT_2;

/// アウトラインを描く 8 方向（上下左右＋4 斜め）の単位ベクトル。
const HINT_OUTLINE_DIRS: [(f32, f32); 8] = [
    (-1.0, 0.0),
    (1.0, 0.0),
    (0.0, -1.0),
    (0.0, 1.0),
    (-HINT_OUTLINE_DIAG, -HINT_OUTLINE_DIAG),
    (HINT_OUTLINE_DIAG, -HINT_OUTLINE_DIAG),
    (-HINT_OUTLINE_DIAG, HINT_OUTLINE_DIAG),
    (HINT_OUTLINE_DIAG, HINT_OUTLINE_DIAG),
];

/// 背景プレートの色（黒・半透明）。
///
/// 濃すぎるとシーンが見えず、薄すぎると白背景で効かない。
const HINT_PLATE_COLOR: [f32; 4] = [0.0, 0.0, 0.0, 0.52];

/// 背景プレートの左右パディング [px]（文字の外接矩形からの余白）。
const HINT_PLATE_PAD_X: f32 = 7.0;

/// 背景プレートの上下パディング [px]。
const HINT_PLATE_PAD_Y: f32 = 5.0;

/// 背景プレートの角丸半径 [px]。
const HINT_PLATE_CORNER_RADIUS: f32 = 5.0;

/// カーソルからガイド左上までのオフセット X [px]（カーソル自身に重ねない）。
pub const HINT_CURSOR_OFFSET_X: f32 = 18.0;

/// カーソルからガイド左上までのオフセット Y [px]（＝カーソルの右下に出す）。
pub const HINT_CURSOR_OFFSET_Y: f32 = 20.0;

/// ガイド（プレート込み）を画面端から出さないための余白 [px]。
const HINT_SCREEN_MARGIN: f32 = 8.0;

// ── TextExtent ───────────────────────────────────────────────

/// 組んだ文字列群の外接矩形（アンカーからの相対オフセット [px]）。
///
/// アンカーは「1 行目のペン基点」であり、グリフはそこから上下左右へ広がる
/// （ペン基点はベースライン上にあるので `min_y` は通常 負になる）。
#[derive(Copy, Clone, Debug, PartialEq)]
pub struct TextExtent {
    /// アンカーからの左端オフセット [px]。
    pub min_x: f32,
    /// アンカーからの上端オフセット [px]。
    pub min_y: f32,
    /// アンカーからの右端オフセット [px]。
    pub max_x: f32,
    /// アンカーからの下端オフセット [px]。
    pub max_y: f32,
}

impl TextExtent {
    /// 幅 [px]。
    pub fn width(&self) -> f32 {
        self.max_x - self.min_x
    }
    /// 高さ [px]。
    pub fn height(&self) -> f32 {
        self.max_y - self.min_y
    }
}

// ── GpuHintBatch ─────────────────────────────────────────────

/// 1 フレームぶんのガイド描画データ（背景プレート＋文字）。
///
/// プレートと文字はパイプラインが別なので 2 本持つ。描画順（プレート → 文字）を
/// 呼び出し側に委ねると取り違えが起きるため、`draw` の中で固定する。
pub struct GpuHintBatch {
    /// 背景プレート（構築に失敗した場合のみ None）。
    plate: Option<GpuHintPlateBatch>,
    /// 文字（アウトライン＋本体を 1 バッチにまとめてある）。
    text: GpuTextBatch,
}

// ── ScreenHintOverlay ────────────────────────────────────────

/// カーソル脇の操作ガイドを描くレンダラー。
pub struct ScreenHintOverlay {
    font_system: FontSystem,
    plate: HintPlate,
}

impl ScreenHintOverlay {
    /// 既定フォントで初期化する。
    pub fn new(
        device: &wgpu::Device,
        surface_format: wgpu::TextureFormat,
        depth_format: wgpu::TextureFormat,
    ) -> Option<Self> {
        let font_system =
            FontSystem::new(device, surface_format, depth_format, FontConfig::default()).ok()?;
        let plate = HintPlate::new(device, surface_format, depth_format);
        Some(Self { font_system, plate })
    }

    /// ガイドの描画データを構築する（行が無ければ None）。
    ///
    /// `anchor_x`, `anchor_y` は希望位置（1 行目のペン基点）のスクリーン座標 [px]。
    /// プレート込みで画面外へはみ出す場合は内側へ寄せる。
    pub fn build(
        &mut self,
        lines: &[String],
        anchor_x: f32,
        anchor_y: f32,
        screen_w: f32,
        screen_h: f32,
        device: &wgpu::Device,
        queue: &wgpu::Queue,
    ) -> Option<GpuHintBatch> {
        if lines.is_empty() || screen_w <= 0.0 || screen_h <= 0.0 {
            return None;
        }

        let line_h = HINT_FONT_SIZE * HINT_LINE_HEIGHT_RATIO;

        // ── ① 外接矩形を実測してから位置を決める ──
        // 実測にはグリフのラスタライズが要るが、直後に同じ文字列を組むので
        // アトラスへ入ったグリフはそのまま再利用される（二度手間にならない）。
        let extent = self.measure(lines, line_h)?;
        let (x, y) = clamp_anchor(&extent, anchor_x, anchor_y, screen_w, screen_h);

        // ── ② 背景プレート（文字の下に敷く）──
        let plate_rect = HintPlateRect {
            x: x + extent.min_x - HINT_PLATE_PAD_X,
            y: y + extent.min_y - HINT_PLATE_PAD_Y,
            w: extent.width() + HINT_PLATE_PAD_X * 2.0,
            h: extent.height() + HINT_PLATE_PAD_Y * 2.0,
            radius: HINT_PLATE_CORNER_RADIUS,
            color: HINT_PLATE_COLOR,
        };
        let plate = HintPlate::build(&[plate_rect], screen_w, screen_h, device);

        // ── ③ 文字（全方向アウトライン → 本体 の順に積む）──
        let mut batch = TextBatch::new();
        for (i, line) in lines.iter().enumerate() {
            let pen_y = y + line_h * i as f32;
            for (dx, dy) in HINT_OUTLINE_DIRS {
                self.font_system.queue_text(
                    &mut batch,
                    line,
                    x + dx * HINT_OUTLINE_WIDTH,
                    pen_y + dy * HINT_OUTLINE_WIDTH,
                    HINT_FONT_SIZE,
                    HINT_OUTLINE_COLOR,
                    screen_w,
                    screen_h,
                );
            }
            self.font_system.queue_text(
                &mut batch, line, x, pen_y,
                HINT_FONT_SIZE, HINT_COLOR, screen_w, screen_h,
            );
        }
        self.font_system.flush(queue);
        let text = self.font_system.build_gpu_batch(&batch, device)?;

        Some(GpuHintBatch { plate, text })
    }

    /// 行群の外接矩形を実測する（描けるグリフが 1 つも無ければ None）。
    ///
    /// `add_text_screen` とまったく同じ式（ペン基点 + bearing、advance で送る）で
    /// 計算するので、プレートは必ず実際の描画結果へ一致する。
    fn measure(&mut self, lines: &[String], line_h: f32) -> Option<TextExtent> {
        let (mut min_x, mut min_y) = (f32::MAX, f32::MAX);
        let (mut max_x, mut max_y) = (f32::MIN, f32::MIN);
        let mut any = false;

        for (i, line) in lines.iter().enumerate() {
            let pen_y = line_h * i as f32;
            let mut pen_x = 0.0f32;
            for (_, info) in self.font_system.prepare_glyphs(line, HINT_FONT_SIZE) {
                let x0 = pen_x + info.bearing[0];
                let y0 = pen_y + info.bearing[1];
                min_x = min_x.min(x0);
                min_y = min_y.min(y0);
                max_x = max_x.max(x0 + info.size[0]);
                max_y = max_y.max(y0 + info.size[1]);
                pen_x += info.advance;
                any = true;
            }
        }
        if !any {
            return None; // 空白だけの行しか無い（描くものが無い）
        }
        // アウトラインは本体より外側へ出るので、その分だけ矩形を広げる。
        Some(TextExtent {
            min_x: min_x - HINT_OUTLINE_WIDTH,
            min_y: min_y - HINT_OUTLINE_WIDTH,
            max_x: max_x + HINT_OUTLINE_WIDTH,
            max_y: max_y + HINT_OUTLINE_WIDTH,
        })
    }

    /// メインレンダーパスへ描画する（深度テストなし、UI オーバーレイ）。
    ///
    /// 背景プレート → 文字 の順で、必ずプレートが下になる。
    pub fn draw<'pass>(
        &'pass self,
        batch: &'pass GpuHintBatch,
        pass: &mut wgpu::RenderPass<'pass>,
    ) {
        if let Some(plate) = &batch.plate {
            self.plate.draw(plate, pass);
        }
        self.font_system.draw_text_batch(&batch.text, pass);
    }
}

// ── 位置決め（純関数＝テスト可能）────────────────────────────

/// ガイドの位置（1 行目のペン基点）を、**プレート込みで**画面内へ収める。
///
/// 右端・下端ではみ出す場合は内側へ戻す。判定に使うのは実測した外接矩形なので、
/// 「日本語だと板が画面外へ出る」といった字幅の見積もり違いが起きない。
pub fn clamp_anchor(
    extent: &TextExtent,
    anchor_x: f32,
    anchor_y: f32,
    screen_w: f32,
    screen_h: f32,
) -> (f32, f32) {
    // アンカーから見た「プレートの外周」までのオフセット。
    let left = extent.min_x - HINT_PLATE_PAD_X;
    let top = extent.min_y - HINT_PLATE_PAD_Y;
    let right = extent.max_x + HINT_PLATE_PAD_X;
    let bottom = extent.max_y + HINT_PLATE_PAD_Y;

    // 上限（右下側）を先に当て、下限（左上側）を後に当てる。
    // こうすると画面よりガイドが大きい場合でも左上が必ず見える。
    let x = anchor_x
        .min(screen_w - HINT_SCREEN_MARGIN - right)
        .max(HINT_SCREEN_MARGIN - left);
    let y = anchor_y
        .min(screen_h - HINT_SCREEN_MARGIN - bottom)
        .max(HINT_SCREEN_MARGIN - top);
    (x, y)
}

// ============================================================
//  テスト
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;

    /// テスト用の外接矩形（1 行ぶん・幅 200px・ベースラインの上 11px / 下 3px）。
    fn extent() -> TextExtent {
        TextExtent { min_x: 0.0, min_y: -11.0, max_x: 200.0, max_y: 3.0 }
    }

    /// 画面中央付近ならカーソル脇の位置がそのまま使われること。
    #[test]
    fn anchor_is_kept_when_it_fits() {
        let (x, y) = clamp_anchor(&extent(), 400.0, 300.0, 1920.0, 1080.0);
        assert_eq!((x, y), (400.0, 300.0));
    }

    /// 右下端では画面内へ戻されること（プレートごと見切れない）。
    #[test]
    fn anchor_is_pulled_back_at_the_screen_edge() {
        let e = extent();
        let (x, y) = clamp_anchor(&e, 1900.0, 1070.0, 1920.0, 1080.0);
        assert!(x < 1900.0, "右端では左へ戻ること: {x}");
        assert!(y < 1070.0, "下端では上へ戻ること: {y}");
        // プレートの右端・下端が余白の内側に入っていること。
        assert!(x + e.max_x + HINT_PLATE_PAD_X <= 1920.0 - HINT_SCREEN_MARGIN + 1.0e-3);
        assert!(y + e.max_y + HINT_PLATE_PAD_Y <= 1080.0 - HINT_SCREEN_MARGIN + 1.0e-3);
    }

    /// 左上でもプレートが画面外へ出ないこと（負の座標を作らない）。
    #[test]
    fn plate_never_leaves_the_top_left_margin() {
        let e = extent();
        let (x, y) = clamp_anchor(&e, 0.0, 0.0, 1920.0, 1080.0);
        assert!(x + e.min_x - HINT_PLATE_PAD_X >= HINT_SCREEN_MARGIN - 1.0e-3, "左端: {x}");
        assert!(y + e.min_y - HINT_PLATE_PAD_Y >= HINT_SCREEN_MARGIN - 1.0e-3, "上端: {y}");
    }

    /// 画面よりガイドが大きい場合でも左上側が優先されること
    /// （右下を優先すると文字の頭が切れて何も読めなくなる）。
    #[test]
    fn tiny_screen_keeps_the_top_left_visible() {
        let e = TextExtent { min_x: 0.0, min_y: -11.0, max_x: 500.0, max_y: 3.0 };
        let (x, y) = clamp_anchor(&e, 5.0, 5.0, 100.0, 60.0);
        assert_eq!(x, HINT_SCREEN_MARGIN - e.min_x + HINT_PLATE_PAD_X);
        assert_eq!(y, HINT_SCREEN_MARGIN - e.min_y + HINT_PLATE_PAD_Y);
    }

    /// 外接矩形の幅・高さが min/max から求まること。
    #[test]
    fn extent_reports_width_and_height() {
        let e = extent();
        assert_eq!(e.width(), 200.0);
        assert_eq!(e.height(), 14.0);
    }

    /// アウトラインが 8 方向すべてを覆い、斜めも縦横と同じ長さであること
    /// （どこか 1 方向でも欠けると、その向きの明るい背景で文字が溶ける）。
    #[test]
    fn outline_covers_all_eight_directions_with_equal_length() {
        assert_eq!(HINT_OUTLINE_DIRS.len(), 8);
        for (dx, dy) in HINT_OUTLINE_DIRS {
            let len = (dx * dx + dy * dy).sqrt();
            assert!((len - 1.0).abs() < 1.0e-5, "単位ベクトルであること: ({dx}, {dy})");
        }
        // 相反する向きが必ず対で入っていること（片側だけの「影」に退化しない）。
        for (dx, dy) in HINT_OUTLINE_DIRS {
            assert!(
                HINT_OUTLINE_DIRS.iter().any(|(ox, oy)| (ox + dx).abs() < 1.0e-5 && (oy + dy).abs() < 1.0e-5),
                "({dx}, {dy}) の逆向きが無い"
            );
        }
    }
}
