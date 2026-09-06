// ============================================================
//  font/registry.rs — フォント実体のレジストリ（パス → フォント ID）
//
//  【役割】
//  「テキストごとにフォントを選べる」ようにするための、フォント実体の一元管理。
//  アセットパスを渡すと ID（u16）を返し、同じパスは 2 度読まない。
//  グリフアトラスのキーはこの ID を持つので、同じ文字でもフォントが違えば
//  別グリフとして正しくキャッシュされる。
//
//  【GPU 非依存】
//  wgpu へ一切触れないので、そのままユニットテストできる。
//
//  【失敗時の方針】
//  読み込み・パースに失敗しても描画は止めない。組み込みフォントへ黙って
//  フォールバックし、警告は **1 度だけ** 出す（font_id は毎フレーム
//  呼ばれるため、失敗を記録せずに再試行するとログとディスク I/O が溢れる）。
// ============================================================

use std::collections::HashMap;

use ab_glyph::{FontArc, InvalidFont};

use crate::engine::asset_fs;

// ─── 定数 ─────────────────────────────────────────────────────

/// 組み込みフォントの ID（パス "" もこれに解決される）。
pub const BUILTIN_FONT_ID: u16 = 0;

// ─── FontRegistry ─────────────────────────────────────────────

/// アセットパスからフォント実体を引くレジストリ。
///
/// `fonts[0]` は必ず組み込みフォント。以降は登場順に追加される。
pub struct FontRegistry {
    /// ID 順のフォント実体（index 0 = 組み込み）。
    fonts: Vec<FontArc>,
    /// アセットパス → フォント ID。
    /// 読み込み失敗も `BUILTIN_FONT_ID` を入れて記録し、再試行しない。
    by_path: HashMap<String, u16>,
}

impl FontRegistry {
    /// 組み込みフォントのバイト列で初期化する。
    ///
    /// 組み込みフォント自体のパースに失敗した場合のみエラー（＝ビルド不良）。
    pub fn new(builtin_bytes: &'static [u8]) -> Result<Self, InvalidFont> {
        let builtin = FontArc::try_from_slice(builtin_bytes)?;
        Ok(Self {
            fonts: vec![builtin],
            by_path: HashMap::new(),
        })
    }

    /// パスからフォント ID を引く（未ロードなら `asset_fs::read_bytes` で読み込む）。
    ///
    /// 空文字・読み込み失敗・パース失敗はすべて `BUILTIN_FONT_ID` へフォールバックする。
    /// 失敗したパスも表へ記録するので、警告は最初の 1 回しか出ない。
    pub fn font_id(&mut self, path: &str) -> u16 {
        // 空文字 = 「組み込みフォントを使う」の明示。表にも入れない。
        if path.is_empty() {
            return BUILTIN_FONT_ID;
        }
        // 既知のパス（成功・失敗どちらも）はここで返る。
        if let Some(id) = self.by_path.get(path) {
            return *id;
        }

        // ── 未知のパス: 実際に読み込む ──
        // 相対パスはアセットルート基準へ寄せる（read_bytes の CWD 依存を避ける）。
        let resolved = asset_fs::normalize_asset_path(path);
        let id = match asset_fs::read_bytes(&resolved) {
            Ok(bytes) => match FontArc::try_from_vec(bytes) {
                Ok(font) => {
                    let new_id = self.fonts.len() as u16;
                    self.fonts.push(font);
                    new_id
                }
                Err(e) => {
                    eprintln!("[SEED FONT] フォントの解析に失敗しました（組み込みで代用）: {path} ({e:?})");
                    BUILTIN_FONT_ID
                }
            },
            Err(e) => {
                eprintln!("[SEED FONT] フォントの読み込みに失敗しました（組み込みで代用）: {path} ({e})");
                BUILTIN_FONT_ID
            }
        };
        // 失敗も記録する（毎フレームの再試行を防ぐのが目的）。
        self.by_path.insert(path.to_string(), id);
        id
    }

    /// ID からフォント実体を返す。範囲外 ID は組み込みフォントを返す。
    pub fn font(&self, id: u16) -> &FontArc {
        self.fonts
            .get(id as usize)
            .unwrap_or(&self.fonts[BUILTIN_FONT_ID as usize])
    }

    /// テスト用: 任意のバイト列を登録する（`asset_fs` を通さない）。
    #[cfg(test)]
    pub fn register_bytes_for_test(&mut self, path: &str, bytes: Vec<u8>) -> u16 {
        let id = match FontArc::try_from_vec(bytes) {
            Ok(font) => {
                let new_id = self.fonts.len() as u16;
                self.fonts.push(font);
                new_id
            }
            Err(_) => BUILTIN_FONT_ID,
        };
        self.by_path.insert(path.to_string(), id);
        id
    }

    /// 登録済みフォント数（テスト・診断用）。
    pub fn len(&self) -> usize {
        self.fonts.len()
    }

    /// 常に組み込みフォントが 1 つあるので空にはならない（clippy 対策の相棒）。
    pub fn is_empty(&self) -> bool {
        self.fonts.is_empty()
    }
}

// ============================================================
//  ユニットテスト（GPU 不要）
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;

    /// テスト用レジストリ（組み込みフォントのみ）。
    fn reg() -> FontRegistry {
        FontRegistry::new(super::super::DEFAULT_FONT_BYTES).expect("組み込みフォントは必ず読める")
    }

    /// 空パスは組み込みフォント ID。
    #[test]
    fn empty_path_is_builtin() {
        let mut r = reg();
        assert_eq!(r.font_id(""), BUILTIN_FONT_ID);
        assert_eq!(r.len(), 1, "空パスでフォントは増えない");
    }

    /// 解決できないパスも組み込みへフォールバックする。
    #[test]
    fn unresolvable_path_falls_back_to_builtin() {
        let mut r = reg();
        assert_eq!(
            r.font_id("assets://__no_such_font__.ttf"),
            BUILTIN_FONT_ID
        );
        assert_eq!(r.len(), 1, "失敗時にフォントは増えない");
    }

    /// 同じパスを 2 回引いても ID は同じで、フォント配列は増えない（キャッシュ）。
    #[test]
    fn same_path_is_cached() {
        let mut r = reg();
        let path = "assets://__no_such_font__.ttf";
        let a = r.font_id(path);
        let before = r.len();
        let b = r.font_id(path);
        assert_eq!(a, b);
        assert_eq!(r.len(), before, "2 回目でフォントが増えてはいけない");
    }

    /// 範囲外 ID は組み込みフォントを返す（落ちない）。
    #[test]
    fn out_of_range_id_returns_builtin() {
        let r = reg();
        let _ = r.font(9999);
    }

    /// 明示登録したフォントは新しい ID を得る。
    #[test]
    fn registered_font_gets_new_id() {
        let mut r = reg();
        let id = r.register_bytes_for_test(
            "assets://dummy.ttf",
            super::super::DEFAULT_FONT_BYTES.to_vec(),
        );
        assert_ne!(id, BUILTIN_FONT_ID);
        assert_eq!(r.font_id("assets://dummy.ttf"), id);
        assert_eq!(r.len(), 2);
    }
}
