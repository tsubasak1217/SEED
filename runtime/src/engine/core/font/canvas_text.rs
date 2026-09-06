// ============================================================
//  font/canvas_text.rs — キャンバス上の TextComponent を描画する層
//
//  【役割】
//  `TextComponent` が積んだ描画アイテム（文字列 + キャンバス変換行列）を、
//  既存の `FontSystem`（グリフアトラス + テキストパイプライン）を使って
//  1 本の頂点バッチへ焼き、レンダーパスへ流す。
//
//  【なぜ CPU で NDC まで変換するか】
//  テキストパイプラインの頂点は **NDC 直値**（カメラ uniform を持たない）。
//  そこで「キャンバスローカル(px) → ワールド(GPU 行列) → クリップ(カメラ VP) → NDC」
//  の全変換を CPU で行い、既存パイプラインへ手を入れずに済ませる。
//  文字数はせいぜい数百なので CPU 変換のコストは無視できる（1 文字 = 4 頂点）。
//  結果として **スプライトとまったく同じ変換連鎖**を通るため、
//  アンカー・ピボット・親子スケール・3D キャンバスの遠近が自動的に一致する。
//
//  【レイアウト】
//  ペンは行頭から右へ進み、改行 `\n` で `font_size * line_spacing` だけ下がる。
//  行幅・ブロック高さを先に測ってから `align` / `vertical_align` のオフセットを適用する。
// ============================================================

use super::sdf::outline_px_to_sdf;
use super::{FontSystem, GpuTextBatch, TextBatch};
use crate::engine::components::{CanvasDrawZone, TextAlign, TextVerticalAlign, MAX_TEXT_CHARS};

// ─── CanvasTextItem ───────────────────────────────────────────

/// キャンバス上に描く 1 つのテキスト。
///
/// `model` は `collect_sprite_items` がスプライトと同じ規則で組んだ
/// **GPU 列優先行列**（列 0..2 = 基底、列 3 = 平行移動）。
/// 単位は「キャンバスピクセル → ワールド」。
pub struct CanvasTextItem {
    /// 表示文字列（改行 `\n` で複数行）。
    pub text: String,
    /// フォントサイズ（キャンバスピクセル）。
    pub font_size: f32,
    /// RGBA カラー。
    pub color: [f32; 4],
    /// 水平方向の基準位置。
    pub align: TextAlign,
    /// 垂直方向の基準位置。
    pub vertical_align: TextVerticalAlign,
    /// 行送り倍率（フォントサイズに対する倍率）。
    pub line_spacing: f32,
    /// キャンバスピクセル → ワールドの GPU 列優先行列。
    pub model: [[f32; 4]; 4],
    /// 描画ゾーン（背景／前面）。スプライトと同じ規約で振り分ける。
    pub zone: CanvasDrawZone,
    /// 描画レイヤー（大きいほど手前。呼び出し側が安定ソートに使う）。
    pub layer: i32,
    /// 使用フォントの assets:// 仮想パス。空文字 = 組み込みフォント。
    pub font_path: String,
    /// 縁取りの太さ（キャンバスピクセル）。0 = 縁取りなし。
    pub outline_width: f32,
    /// 縁取りの色（RGBA 0..1）。
    pub outline_color: [f32; 4],
}

// ─── CanvasTextRenderer ───────────────────────────────────────

/// キャンバステキスト描画器。フォントシステム 1 つを保持する。
///
/// 生成時のカラー / 深度フォーマットに紐づくため、描画先パスの
/// アタッチメント構成ごとに 1 インスタンス必要（現状はメインパスと
/// キャンバスオーバーレイパスが同じ HDR + 深度なので 1 つで足りる）。
pub struct CanvasTextRenderer {
    /// グリフのラスタライズ・アトラス・描画パイプライン。
    font: FontSystem,
}

impl CanvasTextRenderer {
    /// 既定フォントで初期化する。フォント読み込みに失敗したら `None`。
    ///
    /// 失敗してもエンジンは止めない（テキストが出ないだけで他は動く）。
    pub fn new(
        device: &wgpu::Device,
        color_format: wgpu::TextureFormat,
        depth_format: wgpu::TextureFormat,
    ) -> Option<Self> {
        // キャンバステキストは日本語で字種が多いので大きめのアトラスを使う。
        match FontSystem::new(
            device,
            color_format,
            depth_format,
            super::FontConfig::canvas(),
        ) {
            Ok(font) => Some(Self { font }),
            Err(e) => {
                eprintln!("[SEED TEXT] フォントの初期化に失敗しました: {e:?}");
                None
            }
        }
    }

    /// テキストアイテム列を 1 本の GPU バッチへ焼く。
    ///
    /// - `view_proj`: カメラのビュー射影行列（**行優先** `data[row][col]`）。
    ///   `Mat4x4::data` をそのまま渡すこと。
    /// - 返り値 `None` = 描く文字が 1 つも無い（呼び出し側は描画をスキップする）。
    pub fn build(
        &mut self,
        device: &wgpu::Device,
        queue: &wgpu::Queue,
        items: &[CanvasTextItem],
        view_proj: &[[f32; 4]; 4],
    ) -> Option<GpuTextBatch> {
        if items.is_empty() {
            return None;
        }
        let mut batch = TextBatch::new();
        for item in items {
            self.append_item(&mut batch, item, view_proj);
        }
        // 新しく増えたグリフをアトラスへアップロードする（毎フレーム必須）。
        self.font.flush(queue);
        self.font.build_gpu_batch(&batch, device)
    }

    /// 焼いたバッチをレンダーパスへ描画する。
    pub fn draw<'pass>(
        &'pass self,
        gpu: &'pass GpuTextBatch,
        pass: &mut wgpu::RenderPass<'pass>,
    ) {
        self.font.draw_text_batch(gpu, pass);
    }

    // ── 内部: 1 アイテム分の頂点生成 ─────────────────────────

    /// 1 つの `CanvasTextItem` をバッチへ追加する。
    fn append_item(
        &mut self,
        batch: &mut TextBatch,
        item: &CanvasTextItem,
        view_proj: &[[f32; 4]; 4],
    ) {
        // 空文字・非表示（完全透明）・サイズ 0 は頂点を作らない。
        if item.text.is_empty() || item.font_size <= 0.0 || item.color[3] <= 0.0 {
            return;
        }

        // 描画上限で切り詰める（暴走した文字列でフレームを潰さないため）。
        let text: String = if item.text.chars().count() > MAX_TEXT_CHARS {
            item.text.chars().take(MAX_TEXT_CHARS).collect()
        } else {
            item.text.clone()
        };

        // 行ごとにグリフを準備し、同時に行幅を測る。
        // `prepare_glyphs` はアウトラインを持たない文字（スペース等）を返さないため、
        // 送り幅はフォントから別途取得して補う（さもないと空白が詰まる）。
        let mut lines: Vec<LineLayout> = Vec::new();
        for raw_line in text.split('\n') {
            lines.push(self.layout_line(raw_line, item.font_size, &item.font_path));
        }
        if lines.is_empty() {
            return;
        }

        // 縁取りの太さ（px）を SDF テクスチャ単位へ 1 度だけ変換する
        // （グリフごとに計算しても同じ値なので外へ括り出す）。
        let outline_dist = outline_px_to_sdf(item.outline_width, item.font_size);

        // ブロック全体の高さ = 行数 × 行送り。
        let line_step = item.font_size * item.line_spacing;
        let block_height = line_step * lines.len() as f32;
        // 垂直方向オフセット（キャンバス Y は下向き）。
        let base_y = match item.vertical_align {
            TextVerticalAlign::Top => 0.0,
            TextVerticalAlign::Middle => -block_height * 0.5,
            TextVerticalAlign::Bottom => -block_height,
        };

        for (row, line) in lines.iter().enumerate() {
            // 水平方向オフセット（行ごとに幅が違うので行単位で計算する）。
            let base_x = match item.align {
                TextAlign::Left => 0.0,
                TextAlign::Center => -line.width * 0.5,
                TextAlign::Right => -line.width,
            };
            // ペン Y はベースラインではなく「行の上端」。GlyphInfo.bearing[1] が
            // 上端からのオフセット（Y 下向き）なので、そのまま足せる。
            let pen_y = base_y + line_step * row as f32;
            let mut pen_x = base_x;

            for placed in &line.glyphs {
                let advance = placed.advance;
                if let Some(info) = placed.info {
                    // キャンバスローカル（px）でのクアッド 4 隅。
                    // メトリクスは em 単位なのでフォントサイズを掛けて px にする。
                    let bearing = info.bearing_px(item.font_size);
                    let size = info.size_px(item.font_size);
                    let x0 = pen_x + bearing[0];
                    let y0 = pen_y + bearing[1];
                    let x1 = x0 + size[0];
                    let y1 = y0 + size[1];

                    // 4 隅を NDC へ変換する。1 頂点でもクリップ外（w<=0）なら
                    // このグリフごと捨てる（カメラ背後の 3D キャンバス対策）。
                    let Some(p00) = project(x0, y0, &item.model, view_proj) else { pen_x += advance; continue };
                    let Some(p10) = project(x1, y0, &item.model, view_proj) else { pen_x += advance; continue };
                    let Some(p11) = project(x1, y1, &item.model, view_proj) else { pen_x += advance; continue };
                    let Some(p01) = project(x0, y1, &item.model, view_proj) else { pen_x += advance; continue };

                    batch.add_quad_ndc(
                        [p00, p10, p11, p01],
                        [info.uv_min[0], info.uv_min[1]],
                        [info.uv_max[0], info.uv_max[1]],
                        item.color,
                        item.outline_color,
                        outline_dist,
                    );
                }
                pen_x += advance;
            }
        }
    }

    /// 1 行分のグリフを準備し、行幅を測る。
    ///
    /// `font_path` は使用フォントのアセットパス（空文字 = 組み込み）。
    fn layout_line(&mut self, line: &str, font_size: f32, font_path: &str) -> LineLayout {
        // アウトラインを持つ文字のグリフ情報（アトラス登録込み）。
        let prepared = self.font.prepare_glyphs(line, font_path);

        let mut glyphs = Vec::with_capacity(line.chars().count());
        let mut width = 0.0f32;
        // prepared は「アウトラインを持つ文字だけ」を入力順に並べたもの。
        // 元の文字列を走査しながら、対応する要素を順に取り出す。
        let mut it = prepared.into_iter().peekable();
        for ch in line.chars() {
            let info = match it.peek() {
                Some((c, _)) if *c == ch => it.next().map(|(_, i)| i),
                _ => None,
            };
            // 送り幅: グリフ情報があればそれを使い、無ければ（スペース等）
            // フォントから直接引く。ここを落とすと空白が消えて字が詰まる。
            let advance = match &info {
                Some(i) => i.advance_px(font_size),
                None => self.font.advance_em(font_path, ch) * font_size,
            };
            width += advance;
            glyphs.push(PlacedGlyph { info, advance });
        }
        LineLayout { glyphs, width }
    }
}

// ─── レイアウト中間表現 ────────────────────────────────────────

/// 1 文字ぶんの配置情報。
struct PlacedGlyph {
    /// アトラス上のグリフ情報。`None` = 描画不要（スペース等）。
    info: Option<super::atlas::GlyphInfo>,
    /// 次の文字までの送り幅（px）。
    advance: f32,
}

/// 1 行ぶんのレイアウト結果。
struct LineLayout {
    /// 行を構成する文字の配置情報。
    glyphs: Vec<PlacedGlyph>,
    /// 行の総幅（px。整列計算に使う）。
    width: f32,
}

// ─── 座標変換 ─────────────────────────────────────────────────

/// キャンバスローカル座標 (x, y) を NDC へ射影する。
///
/// - `model`     : GPU 列優先（`model[col][row]`）のキャンバス → ワールド行列
/// - `view_proj` : 行優先（`vp[row][col]`）のカメラ行列
///
/// 戻り値 `None` = クリップ空間の w が 0 以下（カメラの背後・退化行列）。
/// その場合は割り算が破綻するため呼び出し側でグリフを捨てる。
fn project(
    x: f32,
    y: f32,
    model: &[[f32; 4]; 4],
    view_proj: &[[f32; 4]; 4],
) -> Option<[f32; 3]> {
    // ワールド座標 = model(列優先) * (x, y, 0, 1)
    let mut world = [0.0f32; 4];
    for (row, w) in world.iter_mut().enumerate() {
        *w = model[0][row] * x + model[1][row] * y + model[3][row];
    }

    // クリップ座標 = view_proj(行優先) * world
    let mut clip = [0.0f32; 4];
    for (row, c) in clip.iter_mut().enumerate() {
        let r = &view_proj[row];
        *c = r[0] * world[0] + r[1] * world[1] + r[2] * world[2] + r[3] * world[3];
    }

    /// クリップ w の下限。これ以下は視錐台の外（カメラ背後）とみなす。
    const MIN_CLIP_W: f32 = 1e-6;
    if clip[3] <= MIN_CLIP_W {
        return None;
    }
    let inv_w = 1.0 / clip[3];
    Some([clip[0] * inv_w, clip[1] * inv_w, clip[2] * inv_w])
}

// ============================================================
//  ユニットテスト（座標変換は純関数なので検証できる）
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;

    /// 単位行列 × 単位行列では入力座標がそのまま NDC になる。
    #[test]
    fn identity_projection_is_passthrough() {
        let ident = [
            [1.0, 0.0, 0.0, 0.0],
            [0.0, 1.0, 0.0, 0.0],
            [0.0, 0.0, 1.0, 0.0],
            [0.0, 0.0, 0.0, 1.0],
        ];
        let p = project(0.25, -0.5, &ident, &ident).expect("w=1 なので成功する");
        assert!((p[0] - 0.25).abs() < 1e-6);
        assert!((p[1] + 0.5).abs() < 1e-6);
    }

    /// model の平行移動列（列 3）が効くこと。
    #[test]
    fn model_translation_is_applied() {
        // 列優先: model[3] = 平行移動 (10, 20, 0, 1)
        let model = [
            [1.0, 0.0, 0.0, 0.0],
            [0.0, 1.0, 0.0, 0.0],
            [0.0, 0.0, 1.0, 0.0],
            [10.0, 20.0, 0.0, 1.0],
        ];
        let ident = [
            [1.0, 0.0, 0.0, 0.0],
            [0.0, 1.0, 0.0, 0.0],
            [0.0, 0.0, 1.0, 0.0],
            [0.0, 0.0, 0.0, 1.0],
        ];
        let p = project(1.0, 2.0, &model, &ident).unwrap();
        assert!((p[0] - 11.0).abs() < 1e-6);
        assert!((p[1] - 22.0).abs() < 1e-6);
    }

    /// view_proj の行優先解釈が正しいこと（行 0 が NDC x を作る）。
    #[test]
    fn view_proj_is_row_major() {
        let ident_model = [
            [1.0, 0.0, 0.0, 0.0],
            [0.0, 1.0, 0.0, 0.0],
            [0.0, 0.0, 1.0, 0.0],
            [0.0, 0.0, 0.0, 1.0],
        ];
        // 行優先で x を 2 倍、y に +3 の平行移動を掛ける行列
        let vp = [
            [2.0, 0.0, 0.0, 0.0],
            [0.0, 1.0, 0.0, 3.0],
            [0.0, 0.0, 1.0, 0.0],
            [0.0, 0.0, 0.0, 1.0],
        ];
        let p = project(4.0, 5.0, &ident_model, &vp).unwrap();
        assert!((p[0] - 8.0).abs() < 1e-6);
        assert!((p[1] - 8.0).abs() < 1e-6);
    }

    /// w <= 0（カメラ背後）は None を返す。
    #[test]
    fn behind_camera_is_rejected() {
        let ident_model = [
            [1.0, 0.0, 0.0, 0.0],
            [0.0, 1.0, 0.0, 0.0],
            [0.0, 0.0, 1.0, 0.0],
            [0.0, 0.0, 0.0, 1.0],
        ];
        // 最終行が w = -1 を返す行列
        let vp = [
            [1.0, 0.0, 0.0, 0.0],
            [0.0, 1.0, 0.0, 0.0],
            [0.0, 0.0, 1.0, 0.0],
            [0.0, 0.0, 0.0, -1.0],
        ];
        assert!(project(1.0, 1.0, &ident_model, &vp).is_none());
    }
}
