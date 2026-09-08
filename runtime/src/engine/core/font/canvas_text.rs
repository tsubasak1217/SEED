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

use super::inline::{IMAGE_PLACEHOLDER, InlineImages, build_doc};
use super::sdf::{outline_px_to_sdf, px_to_sdf};
use super::text_layout::{
    ResolvedLayout, TextLayoutSpec, TextLocalBox, resolve_layout_with_images,
};
use super::{FontSystem, GlyphShading, GpuTextBatch, TextBatch};
use crate::engine::components::{CanvasDrawZone, TextAlign, TextVerticalAlign};

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
    /// アイコンセット（.icons）の assets:// 仮想パス。空文字 = 未使用。
    ///
    /// 本文の `[icon:名前]` 記法をこの表で解決する。
    /// **画像そのものはここでは描かない**（SDF アトラスは色を持てないため、
    /// スプライト経路へ回す。`canvas_collect` を参照）。ここで必要なのは
    /// 「画像がどれだけ場所を取るか」だけで、文字の送り幅に反映される。
    pub icon_set: String,
    /// 縁取りの太さ（キャンバスピクセル）。0 = 縁取りなし。
    pub outline_width: f32,
    /// 縁取りの色（RGBA 0..1）。
    pub outline_color: [f32; 4],
    /// 枠の幅（キャンバスピクセル）。0 = 枠なし（従来どおり原点基準に置く）。
    pub box_width: f32,
    /// 枠の最小高さ（キャンバスピクセル）。実際の高さは内容高さとの大きいほう。
    pub box_height: f32,
    /// 枠幅での自動折り返しを行うか（`box_width > 0` のときのみ有効）。
    pub wrap: bool,
    /// 正規化ピボット（枠サイズに対する 0..1）。
    ///
    /// **枠なしのときは呼び出し側が `[0, 0]` を渡すこと**（従来挙動の維持）。
    /// 枠ありでは `model` を pivot 無しで組み、ここでグリフ座標を平行移動する。
    pub pivot: [f32; 2],
    /// 文字の太さ（キャンバスピクセル。負で細く・正で太く）。
    pub weight: f32,
    /// ドロップシャドウのオフセット（キャンバスピクセル。X 右・Y 下）。
    pub shadow_offset: [f32; 2],
    /// ドロップシャドウの色（RGBA 0..1）。
    pub shadow_color: [f32; 4],
    /// ドロップシャドウのぼかし幅（キャンバスピクセル。0 = シャープ）。
    pub shadow_softness: f32,
}

impl CanvasTextItem {
    /// このアイテムのレイアウト条件を組み立てる。
    ///
    /// 描画（`append_item`）と計測（`CanvasTextRenderer::resolve_bounds`）が
    /// 同じ条件を作れるよう、変換の定義はここ 1 箇所に置く。
    pub fn layout_spec(&self) -> TextLayoutSpec {
        TextLayoutSpec {
            font_size: self.font_size,
            line_spacing: self.line_spacing,
            align: self.align,
            vertical_align: self.vertical_align,
            outline_width: self.outline_width,
            box_width: self.box_width,
            box_height: self.box_height,
            wrap: self.wrap,
        }
    }

    /// ドロップシャドウを描くか（オフセットがゼロ、または完全透明なら描かない）。
    pub fn has_shadow(&self) -> bool {
        (self.shadow_offset[0] != 0.0 || self.shadow_offset[1] != 0.0) && self.shadow_color[3] > 0.0
    }
}

// ─── TextDrawRange ────────────────────────────────────────────

/// 1 本のテキストバッチ内の部分区間（＝ 1 ドローコール分）。
///
/// UI 描画順の統合でテキストを「ラン」単位に分割描画するために使う。
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct TextDrawRange {
    /// バッチ先頭からのインデックス番号。
    pub first_index: u32,
    /// 描くインデックス数（0 = 空区間）。
    pub index_count: u32,
}

impl TextDrawRange {
    /// 空区間（描く文字が無い）か。
    pub fn is_empty(&self) -> bool {
        self.index_count == 0
    }
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

    /// テキストアイテムを**グループ単位に区切って** 1 本の GPU バッチへ焼く。
    ///
    /// UI 描画順の統合（スプライト／プリミティブ／テキストをレイヤー順に 1 列へ並べる）で、
    /// 「テキストのラン 1 本」＝「グループ 1 つ」として分割描画するために使う。
    /// 頂点バッファは従来どおり 1 本しか作らない（グループごとの GPU 確保は発生しない）。
    ///
    /// - `groups`: 描画順に並んだアイテム区間の列。
    /// - 返り値: `(GPU バッチ, グループと 1:1 対応するインデックス区間)`。
    ///   描く文字が 1 つも無ければ `None`（呼び出し側は描画をスキップする）。
    pub fn build_grouped(
        &mut self,
        device: &wgpu::Device,
        queue: &wgpu::Queue,
        groups: &[&[CanvasTextItem]],
        view_proj: &[[f32; 4]; 4],
    ) -> Option<(GpuTextBatch, Vec<TextDrawRange>)> {
        if groups.iter().all(|g| g.is_empty()) {
            return None;
        }
        let mut batch = TextBatch::new();
        let mut ranges: Vec<TextDrawRange> = Vec::with_capacity(groups.len());
        for group in groups {
            // グループ開始時点のインデックス位置を記録し、積み終わりとの差を区間長にする。
            let first_index = batch.index_len();
            for item in group.iter() {
                self.append_item(&mut batch, item, view_proj);
            }
            ranges.push(TextDrawRange {
                first_index,
                index_count: batch.index_len() - first_index,
            });
        }
        // 新しく増えたグリフをアトラスへアップロードする（毎フレーム必須）。
        self.font.flush(queue);
        self.font
            .build_gpu_batch(&batch, device)
            .map(|gpu| (gpu, ranges))
    }

    /// テキストの表示寸法（キャンバスローカル px の境界矩形と pivot 基準サイズ）を測る。
    ///
    /// 描画（`append_item`）と**同一のレイアウト規則**（`text_layout::resolve_layout`）を
    /// 使うため、ピックのヒット矩形・選択アウトラインが必ず見た目と一致する。
    /// フォントは `font_path` から解決する（未ロードならここで読み込まれ、
    /// 以降はレジストリのキャッシュが効く）。空文字・サイズ 0 は `None`。
    ///
    /// 戻り値の 2 要素目は pivot を掛ける基準サイズ（枠なしは `[0, 0]` ＝ pivot 無効）。
    pub fn resolve_bounds(
        &mut self,
        text: &str,
        spec: &TextLayoutSpec,
        font_path: &str,
        icon_set: &str,
    ) -> Option<(TextLocalBox, [f32; 2])> {
        // 記法を解決してから測る（画像はグリフと同じく行幅・境界に効く）。
        let doc = build_doc(text, icon_set);
        let font_id = self.font.registry.font_id(font_path);
        let font = self.font.registry.font(font_id);
        resolve_layout_with_images(font, &doc.text, spec, &doc.images)
            .map(|r| (r.bounds, r.pivot_size))
    }

    /// 焼いたバッチをレンダーパスへ描画する。
    pub fn draw<'pass>(
        &'pass self,
        gpu: &'pass GpuTextBatch,
        pass: &mut wgpu::RenderPass<'pass>,
    ) {
        self.font.draw_text_batch(gpu, pass);
    }

    /// 焼いたバッチの **1 区間だけ**をレンダーパスへ描画する（ラン単位描画）。
    pub fn draw_range<'pass>(
        &'pass self,
        gpu: &'pass GpuTextBatch,
        range: &TextDrawRange,
        pass: &mut wgpu::RenderPass<'pass>,
    ) {
        self.font
            .draw_text_batch_range(gpu, range.first_index, range.index_count, pass);
    }

    // ── 内部: 1 アイテム分の頂点生成 ─────────────────────────

    /// 1 つの `CanvasTextItem` をバッチへ追加する。
    ///
    /// 手順は「レイアウト解決 → 行ごとのグリフ準備 → 影を描く → 本体を描く」。
    /// 影と本体はまったく同じグリフ列を平行移動して描くため、
    /// **アイテム単位で影を全部描いてから本体を描く**（グリフ単位で交互に積むと
    /// 隣の文字の本体の上へ次の文字の影が乗ってしまう）。
    fn append_item(
        &mut self,
        batch: &mut TextBatch,
        item: &CanvasTextItem,
        view_proj: &[[f32; 4]; 4],
    ) {
        // 空文字・サイズ 0 は頂点を作らない。
        // 本体が完全透明でも、影が見えるなら描く必要がある。
        let draw_body = item.color[3] > 0.0;
        let draw_shadow = item.has_shadow();
        if item.text.is_empty() || item.font_size <= 0.0 || (!draw_body && !draw_shadow) {
            return;
        }

        // レイアウト解決。フォント実体は clone（Arc）して借用衝突を避ける
        // （このあと `self.font` を可変借用してグリフをアトラスへ登録するため）。
        let font_id = self.font.registry.font_id(&item.font_path);
        let font = self.font.registry.font(font_id).clone();
        let spec = item.layout_spec();
        // 記法（インライン画像）を解決してからレイアウトする。
        // 画像は 1 文字ぶんの代替文字として本文に埋まり、送り幅だけが効く
        // （画像の絵そのものはスプライト経路が描く）。
        let doc = build_doc(&item.text, &item.icon_set);
        let Some(layout) = resolve_layout_with_images(&font, &doc.text, &spec, &doc.images) else {
            return;
        };

        // 行ごとにグリフを準備する（範囲は resolve_layout が決めた行分割）。
        // `prepare_glyphs` はアウトラインを持たない文字（スペース等）を返さないため、
        // 送り幅はフォントから別途取得して補う（さもないと空白が詰まる）。
        let mut lines: Vec<LineLayout> = Vec::with_capacity(layout.lines.len());
        for wrapped in &layout.lines {
            let text = layout.text[wrapped.range.clone()].to_string();
            lines.push(self.layout_line(
                &text,
                wrapped.range.start,
                item.font_size,
                &item.font_path,
                &layout.images,
            ));
        }

        // px → SDF テクスチャ単位の変換は 1 度だけ行う（グリフごとに同じ値）。
        let outline_dist = outline_px_to_sdf(item.outline_width, item.font_size);
        let weight_dist = px_to_sdf(item.weight, item.font_size);

        // 枠ありのときだけ pivot がローカル平行移動として効く（枠なしは [0,0]）。
        let pivot_offset = layout.pivot_offset(item.pivot);

        // ── 影（本体より先に積む＝下へ回る）──
        if draw_shadow {
            let mut shadow = GlyphShading::plain(item.shadow_color);
            // 影も本体と同じ太さで抜く（太くした文字の影だけ細いと不自然になる）。
            shadow.weight_dist = weight_dist;
            shadow.softness = px_to_sdf(item.shadow_softness, item.font_size);
            emit_glyph_quads(
                batch,
                &layout,
                &lines,
                [
                    pivot_offset[0] + item.shadow_offset[0],
                    pivot_offset[1] + item.shadow_offset[1],
                ],
                item.font_size,
                &shadow,
                &item.model,
                view_proj,
            );
        }

        // ── 本体 ──
        if draw_body {
            let body = GlyphShading {
                color: item.color,
                outline_color: item.outline_color,
                outline_dist,
                weight_dist,
                softness: 0.0,
            };
            emit_glyph_quads(
                batch,
                &layout,
                &lines,
                pivot_offset,
                item.font_size,
                &body,
                &item.model,
                view_proj,
            );
        }
    }

    /// 1 行分のグリフを準備し、行幅を測る。
    ///
    /// - `line`       : 行の文字列（`layout.text` の当該範囲を切り出したもの）
    /// - `line_start` : その行が本文全体で何バイト目から始まるか（画像表の引き当てに使う）
    /// - `font_path`  : 使用フォントのアセットパス（空文字 = 組み込み）
    /// - `images`     : 本文全体に対するインライン画像の位置表
    ///
    /// インライン画像の代替文字は**グリフを作らず**、画像の送り幅だけを進める
    /// （画像の絵はスプライト経路が描く）。ここで代替文字をフォントへ渡すと
    /// 未定義グリフ（豆腐）が本文中へ描かれてしまうため、必ず除外する。
    fn layout_line(
        &mut self,
        line: &str,
        line_start: usize,
        font_size: f32,
        font_path: &str,
        images: &InlineImages,
    ) -> LineLayout {
        // 代替文字はグリフ化しない。含まれる行だけ除去した文字列を作る
        // （含まない大多数の行では確保が起きない）。
        let glyph_src: std::borrow::Cow<str> = if line.contains(IMAGE_PLACEHOLDER) {
            std::borrow::Cow::Owned(line.replace(IMAGE_PLACEHOLDER, ""))
        } else {
            std::borrow::Cow::Borrowed(line)
        };
        // アウトラインを持つ文字のグリフ情報（アトラス登録込み）。
        let prepared = self.font.prepare_glyphs(&glyph_src, font_path);

        let mut glyphs = Vec::with_capacity(line.chars().count());
        let mut width = 0.0f32;
        // prepared は「アウトラインを持つ文字だけ」を入力順に並べたもの。
        // 元の文字列を走査しながら、対応する要素を順に取り出す。
        let mut it = prepared.into_iter().peekable();
        for (rel, ch) in line.char_indices() {
            // ── インライン画像: グリフ無し・送り幅は画像のもの ──
            if ch == IMAGE_PLACEHOLDER {
                if let Some(img) = images.get(line_start + rel) {
                    let advance = img.advance_px(font_size);
                    width += advance;
                    glyphs.push(PlacedGlyph {
                        info: None,
                        advance,
                    });
                    continue;
                }
            }
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

// ─── グリフクアッドの生成（影・本体で共有）─────────────────────

/// 解決済みレイアウトと準備済みグリフから、クアッド列をバッチへ積む。
///
/// 影と本体はまったく同じ形状を「オフセットと陰影だけ変えて」描くため、
/// この 1 本を 2 回呼ぶ形にして式の二重定義を避ける。
///
/// - `offset`  : すべてのグリフへ一様に足すローカル平行移動（px）。
///   pivot ぶんの移動と、影のオフセットがここに合流する。
/// - `shading` : 色・縁取り・太さ・ぼかし（クアッド内で定数）。
#[allow(clippy::too_many_arguments)]
fn emit_glyph_quads(
    batch: &mut TextBatch,
    layout: &ResolvedLayout,
    lines: &[LineLayout],
    offset: [f32; 2],
    font_size: f32,
    shading: &GlyphShading,
    model: &[[f32; 4]; 4],
    view_proj: &[[f32; 4]; 4],
) {
    for (row, line) in lines.iter().enumerate() {
        // 水平方向の開始 X（行ごとに幅が違うので行単位で決まっている）。
        let base_x = layout.base_x[row] + offset[0];
        // ペン Y は当該行の**ベースライン**。GlyphInfo.bearing[1] は
        // ベースラインからクアッド左上へのオフセット（上向きが負）なので
        // そのまま足せる。
        let pen_y = layout.first_baseline_y + layout.line_step * row as f32 + offset[1];
        let mut pen_x = base_x;

        for placed in &line.glyphs {
            let advance = placed.advance;
            if let Some(info) = placed.info {
                // キャンバスローカル（px）でのクアッド 4 隅。
                // メトリクスは em 単位なのでフォントサイズを掛けて px にする。
                let bearing = info.bearing_px(font_size);
                let size = info.size_px(font_size);
                let x0 = pen_x + bearing[0];
                let y0 = pen_y + bearing[1];
                let x1 = x0 + size[0];
                let y1 = y0 + size[1];

                // 4 隅を NDC へ変換する。1 頂点でもクリップ外（w<=0）なら
                // このグリフごと捨てる（カメラ背後の 3D キャンバス対策）。
                let Some(p00) = project(x0, y0, model, view_proj) else { pen_x += advance; continue };
                let Some(p10) = project(x1, y0, model, view_proj) else { pen_x += advance; continue };
                let Some(p11) = project(x1, y1, model, view_proj) else { pen_x += advance; continue };
                let Some(p01) = project(x0, y1, model, view_proj) else { pen_x += advance; continue };

                batch.add_quad_ndc(
                    [p00, p10, p11, p01],
                    [info.uv_min[0], info.uv_min[1]],
                    [info.uv_max[0], info.uv_max[1]],
                    shading,
                );
            }
            pen_x += advance;
        }
    }
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
