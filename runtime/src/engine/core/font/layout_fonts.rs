// ============================================================
//  font/layout_fonts.rs — 描画器を持たない層のためのフォントキャッシュ
//
//  【なぜ必要か】
//  テキストのレイアウト（送り幅・行分割・境界矩形）は GPU に触らない純計算だが、
//  フォント実体（`FontArc`）だけは必要になる。
//  ところが `FontSystem`（＝ `CanvasTextRenderer`）はグリフアトラスを持つため
//  wgpu デバイスに紐づき、可変借用が要る。
//  スプライト収集（`canvas_collect`）のように「シーンを不変借用したまま走る」
//  経路からは描画器を借りられないため、**GPU 非依存のフォント実体だけ**を
//  引ける入口をここに置く。
//
//  【一貫性】
//  実体は `registry::FontRegistry` そのもの（読み込み・フォールバック規則を
//  描画側と共有する）。したがって「レイアウトに使ったフォント」と
//  「描画に使うフォント」が食い違うことはない。
//
//  【コスト】
//  組み込みフォントは `&'static [u8]` から参照で構築されるため複製されない。
//  外部フォント（.ttf）は描画側レジストリとは別に 1 部だけ常駐する。
//  レイアウト用に読むフォントは Text コンポーネントが指定したものだけなので、
//  実用上は数個で頭打ちになる。
// ============================================================

use std::sync::{Mutex, OnceLock};

use ab_glyph::FontArc;

use super::DEFAULT_FONT_BYTES;
use super::registry::FontRegistry;

/// レイアウト専用フォントレジストリ（プロセス内で 1 つ）。
static REGISTRY: OnceLock<Mutex<FontRegistry>> = OnceLock::new();

/// レジストリへの排他アクセスを得る。
///
/// 組み込みフォントのパースに失敗するのはビルド不良のときだけなので、
/// ここで失敗したら復旧の余地がない（`expect` で早期に気づけるようにする）。
fn registry() -> &'static Mutex<FontRegistry> {
    REGISTRY.get_or_init(|| {
        Mutex::new(
            FontRegistry::new(DEFAULT_FONT_BYTES).expect("組み込みフォントは必ず読める"),
        )
    })
}

/// アセットパスからフォント実体を取得する（空文字 = 組み込みフォント）。
///
/// `FontArc` は内部が `Arc` なので clone は参照カウントの増加だけで済む。
/// 読み込み失敗時は組み込みフォントへフォールバックし、警告は 1 度だけ出る
/// （フォールバック規則は `FontRegistry` と共有）。
pub fn font_for(path: &str) -> FontArc {
    let mut reg = match registry().lock() {
        Ok(r) => r,
        // 他スレッドが panic した場合でも描画を止めない（毒された鍵を無視して復旧）。
        Err(poisoned) => poisoned.into_inner(),
    };
    let id = reg.font_id(path);
    reg.font(id).clone()
}

// ============================================================
//  単体テスト（GPU 不要）
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;
    use crate::engine::core::font::text_layout::advance_em;

    /// 空パス（組み込みフォント）で送り幅が引ける。
    #[test]
    fn builtin_font_is_available() {
        let f = font_for("");
        assert!(advance_em(&f, 'A') > 0.0, "組み込みフォントで送り幅が引ける");
    }

    /// 解決できないパスは組み込みフォントへフォールバックする（落ちない）。
    #[test]
    fn unresolvable_path_falls_back() {
        let a = font_for("");
        let b = font_for("assets://__no_such_font_for_layout__.ttf");
        assert_eq!(advance_em(&a, 'A'), advance_em(&b, 'A'));
    }
}
