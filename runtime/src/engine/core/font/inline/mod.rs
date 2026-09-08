// ============================================================
//  font/inline/mod.rs — 本文中のインライン画像（記法・解決・配置）
//
//  【この層の責務】
//  「文字の並びの中に画像を 1 文字として混ぜる」機能を、レイアウト本体
//  （`text_wrap` / `text_layout`）から切り離して持つ。
//
//    markup.rs     : 記法 `[icon:...]` / `[img:...]` のトークン化（純関数）
//    icon_set.rs   : アイコンセット `.icons`（JSON）の定義・ローダ・キャッシュ
//    image_meta.rs : 画像の縦横比の取得とキャッシュ（幅の決定に必要）
//    doc.rs        : トークン列 → 「代替文字へ潰した本文 + 画像位置表」
//    placement.rs  : 解決済みレイアウト → 画像を描く矩形（キャンバス px）
//
//  【レイアウトとの接続点】
//  画像は 1 つにつき代替文字 1 文字（U+FFFC）として本文へ埋め込まれ、
//  折り返し・整列・枠・pivot・影といった既存規則がそのまま適用される。
//  レイアウト側が知るのは「その位置の送り幅と高さ」だけである。
//
//  【描画経路】
//  画像は SDF テキストパイプライン（R8 の距離場アトラス）では描けないため、
//  **既存のスプライト描画経路**へ「Text 由来のスプライト矩形」として流す
//  （テクスチャ解決も SpriteComponent と同じキャッシュを使う）。
//
//  【アセットのホットリロード】
//  `.icons` と画像寸法はプロセス内キャッシュに載る。差し替えを反映するには
//  `invalidate_caches` を呼ぶこと（キャッシュを持たないと毎フレーム
//  ディスクを叩き、未解決時は警告ログが溢れる）。
// ============================================================

/// 本文の色付き区間表（`{color}` の解決結果）。
pub mod color_runs;
/// 記法つき本文の解決結果（レイアウトへ渡す中間表現）。
pub mod doc;
/// アイコンセット `.icons`（JSON）の定義とローダ。
pub mod icon_set;
/// インライン画像の縦横比キャッシュ。
pub mod image_meta;
/// 記法パーサ（純関数）。
pub mod markup;
/// 解決済みレイアウトから画像の配置矩形を求める。
pub mod placement;
/// スロット値の書式（数値の丸め・注入文字列の上限）。
pub mod slot_format;
/// プレースホルダ記法（波括弧）のパーサ（純関数）。
pub mod slot_markup;

// 再エクスポートは「レイアウト側が実際に使うもの」だけに絞る。
// `InlineDoc` / `InlineImage` 型そのものが要る場合は `doc::` から直接参照する。
pub use color_runs::ColorRuns;
pub use doc::{IMAGE_PLACEHOLDER, InlineDoc, InlineImages, build_doc, build_doc_with_slots};
pub use icon_set::ICON_SET_EXTENSION;
pub use placement::{InlineImageRect, collect_image_rects};

/// インライン画像まわりのプロセス内キャッシュをすべて捨てる。
///
/// アセットの差し替え・プロジェクト切り替えの際に呼ぶこと。
/// 警告の重複抑止もリセットするので、直した内容が次のフレームで再評価される。
pub fn invalidate_caches() {
    icon_set::invalidate_all();
    image_meta::invalidate_all();
    doc::reset_warnings();
}
