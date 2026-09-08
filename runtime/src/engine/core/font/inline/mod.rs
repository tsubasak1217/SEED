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

// ============================================================
//  ライブ編集の自動反映（ポーリング）
//
//  【背景】
//  `icon_set` / `image_meta` は「1 度読んだら二度と読まない」プロセス内
//  キャッシュのため、`.icons` や参照先の画像を外部エディタで編集しても
//  エディタ（SEEDEditor）を再起動するまで反映されなかった。
//
//  【方式】
//  各キャッシュのエントリに「実ファイルパス＋読み込み時点の更新時刻」を
//  持たせ（`icon_set::poll_changes` / `image_meta::poll_changes`）、
//  ここ `poll_asset_changes` がフレーム頭（`App::build_text_expand_map`）
//  から毎回呼ばれる前提で、`ICON_SET_POLL_INTERVAL` に 1 回だけ実際の
//  ディスク確認を行う（間引きにより、確認自体のコストは無視できる）。
//
//  【PAK モード】
//  パッケージ実行では `asset_fs` が実ファイルを持たない（PAK 内蔵）ため、
//  ファイルは差し替えようがない。確認そのものを丸ごとスキップする。
// ============================================================

use std::sync::{Mutex, OnceLock};
use std::time::Instant;

use crate::engine::asset_fs;

/// ポーリングの実行結果。
///
/// 「間引きで確認自体を行わなかった」ことと「確認したが変化が無かった」ことを
/// 呼び出し側（テスト）が区別できるよう、確認したかどうかを型で分ける。
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum PollOutcome {
    /// PAK パッケージ実行中、またはポーリング間隔が未経過のため確認しなかった。
    Skipped,
    /// 実際にディスクの更新時刻を確認した（内包する bool = 1 件以上再読込したか）。
    Ran(bool),
}

/// 直前にポーリングを実行した時刻（プロセス内で 1 つ）。
///
/// `None` は「まだ 1 度も実行していない」＝ 初回呼び出しは必ず実行する。
static LAST_POLL: OnceLock<Mutex<Option<Instant>>> = OnceLock::new();

/// `LAST_POLL` への排他アクセスを得る。
fn last_poll() -> &'static Mutex<Option<Instant>> {
    LAST_POLL.get_or_init(|| Mutex::new(None))
}

/// `.icons` と画像寸法のライブ編集を定期的にポーリングし、
/// 実ファイルが更新されていれば該当キャッシュだけ破棄・再読込する。
///
/// - `App::build_text_expand_map`（フレーム頭で 1 回）の先頭から呼ぶこと。
/// - 戻り値が `true` のとき、呼び出し側は展開結果キャッシュ
///   （`text_expand::invalidate_text_expand_cache`）を破棄すること。
///   展開結果のハッシュは `.icons` の中身や画像のアスペクト比を含まないため、
///   ここで明示的に破棄しないと差し替えが永久に反映されない。
pub fn poll_asset_changes() -> bool {
    matches!(poll_asset_changes_at(Instant::now()), PollOutcome::Ran(true))
}

/// `poll_asset_changes` の内部実装。「現在時刻」を引数で受け取れるようにして、
/// テストから間引き（`ICON_SET_POLL_INTERVAL`）の挙動を検証できるようにする。
fn poll_asset_changes_at(now: Instant) -> PollOutcome {
    // PAK パッケージ実行では実ファイルが無いので確認自体が無意味。
    if asset_fs::is_packaged() {
        return PollOutcome::Skipped;
    }
    {
        let Ok(mut last) = last_poll().lock() else { return PollOutcome::Skipped };
        if let Some(prev) = *last {
            if now.duration_since(prev) < icon_set::ICON_SET_POLL_INTERVAL {
                return PollOutcome::Skipped;
            }
        }
        *last = Some(now);
    }

    let icon_changed = icon_set::poll_changes();
    let image_changed = image_meta::poll_changes();
    let changed = icon_changed || image_changed;
    if changed {
        // 未解決だった参照が直った可能性があるので、警告の重複抑止を解除し
        // 次回の展開で改めて評価されるようにする。
        doc::reset_warnings();
    }
    PollOutcome::Ran(changed)
}

#[cfg(test)]
mod poll_tests {
    use super::*;
    use std::time::Duration;

    /// テスト間で `LAST_POLL` を汚さないよう、明示的にリセットするヘルパ。
    fn reset_last_poll() {
        *last_poll().lock().unwrap() = None;
    }

    /// ポーリング間隔が経過していない連続呼び出しは、確認自体を行わない（`Skipped`）。
    #[test]
    fn interval_throttles_repeated_calls() {
        reset_last_poll();
        let t0 = Instant::now();
        // 初回は必ず実行される（Skipped にはならない）。
        assert_ne!(poll_asset_changes_at(t0), PollOutcome::Skipped);
        // 間隔未満での 2 回目は間引かれる。
        let t1 = t0 + Duration::from_millis(500);
        assert_eq!(poll_asset_changes_at(t1), PollOutcome::Skipped);
        // 間隔経過後は再び実行される。
        let t2 = t0 + icon_set::ICON_SET_POLL_INTERVAL + Duration::from_millis(1);
        assert_ne!(poll_asset_changes_at(t2), PollOutcome::Skipped);
        reset_last_poll();
    }
}
