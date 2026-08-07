// ============================================================
//  terrain_brush_mask_ops.rs — 地形ブラシの形状マスク（状態設定とキャッシュ解決）
//
//  【責務】
//    「今このブラシで使う形状マスク画像はどれか」という **ツールの状態** を持ち、
//    ブラシ適用時にデコード済みの `CoverMask` を引けるようにする。
//      ① IPC ハンドラ `TERRAIN_BRUSH_MASK:{path}`（空文字で解除）
//      ② キャッシュ（`TerrainState::mask_cache`）からの解決ヘルパ
//
//  【なぜブラシコマンドへ引数を足さず、別コマンドで状態を持つのか】
//    既存の `TERRAIN_PAINT` / `TERRAIN_COVER_BRUSH` は **カンマ区切り**である。
//    Windows のファイルパスはカンマを含みうるため（`C:\tex\brush,01.png` は合法）、
//    行末にパスを足す設計はパースを壊す。加えてブラシはドラッグ中 40ms 間隔で
//    飛ぶので、1 発ごとに数百バイトのパスを載せるのは無駄でもある。
//    半径・強度と同じ「滅多に変わらないツール設定」として状態化するのが素直である。
//
//  【デコードを遅延させる理由】
//    設定コマンド受信時ではなく **最初にブラシが当たったとき**に
//    `ensure_terrain_mask` を通す。地形の作り直し（`TerrainState::default()`）で
//    キャッシュは空になるが、マスクのパスは持ち越す設計にしてあるため、
//    「設定時に 1 回だけ読む」方式だと再初期化後に無言でマスクが外れてしまう。
//    遅延解決なら、キャッシュが消えても次のストロークで必ず復活する。
// ============================================================

use std::collections::HashMap;

use crate::engine::terrain::cover::CoverMask;

use super::App;

// ─── 定数（マジックナンバー禁止）─────────────────────────────────────────────

/// 「マスク未指定」を表すパス文字列。
///
/// エディタは解除操作を `TERRAIN_BRUSH_MASK:`（引数なし＝空文字）で送る。
/// `Option<String>` にしないのは、IPC の値が常に文字列であり、
/// 「空文字＝未指定」という規約が散布 prop_id・カバー素材 ID と共通だからである。
pub(super) const TERRAIN_BRUSH_MASK_NONE: &str = "";

/// マスクキャッシュから、現在のブラシ形状マスクを引く。
///
/// - `cache`: `TerrainState::mask_cache`
/// - `path`:  `TerrainState::brush_mask_path`
///
/// 戻り値が `None` のとき、ブラシは従来どおりの円形フォールオフで動く
/// （`terrain::brush_mask` の縮退規約）。次の 3 つを同じ `None` に畳む:
///   ・パスが未指定（空文字）
///   ・まだデコードしていない（キャッシュに無い）
///   ・デコードに失敗した（`CoverMask::empty()` が入っている）
///
/// 【`TerrainState` 全体ではなくフィールド 2 つを受け取る理由】
///   呼び出し側は同じ `&mut TerrainState` から `chunks` / `cover` を **可変で**
///   借りながらマスクを読む。`&TerrainState` を受け取る形にすると構造体全体の
///   不変借用になって競合するが、フィールド単位なら借用が分割されて両立する。
pub(super) fn resolve_brush_mask<'a>(
    cache: &'a HashMap<String, CoverMask>,
    path: &str,
) -> Option<&'a CoverMask> {
    if path == TERRAIN_BRUSH_MASK_NONE {
        return None;
    }
    cache.get(path).filter(|m| m.is_valid())
}

impl App {
    /// 地形ブラシの形状マスクを設定・解除する（`TERRAIN_BRUSH_MASK:{path}`）。
    ///
    /// `path` が空文字なら解除（＝従来どおりの円形フォールオフへ戻す）。
    /// 同じパスが再指定された場合は **キャッシュを捨てて読み直す**。
    /// 画像を描き替えて指定し直す、という使い方を「同じパスだから何もしない」で
    /// 潰さないためである（レイヤテクスチャの再読込と同じ考え方）。
    pub(super) fn handle_terrain_brush_mask(&mut self, path: String) {
        let path = path.trim().to_string();

        // ─── 解除 ───
        if path == TERRAIN_BRUSH_MASK_NONE {
            self.terrain.brush_mask_path.clear();
            if let Some(ipc) = &self.ipc {
                ipc.send("TERRAIN_BRUSH_MASK_OK:");
            }
            return;
        }

        // ─── 設定（再指定なら読み直す）───
        self.terrain.mask_cache.remove(&path);
        self.ensure_terrain_mask(&path);
        let valid = self
            .terrain
            .mask_cache
            .get(&path)
            .is_some_and(|m| m.is_valid());
        self.terrain.brush_mask_path = path.clone();

        if let Some(ipc) = &self.ipc {
            if valid {
                ipc.send(&format!("TERRAIN_BRUSH_MASK_OK:{path}"));
            } else {
                // 読めなかったことは必ず知らせる。ブラシ自体は円形へ縮退して
                // 動き続けるので、通知が無いと「なぜか形が付かない」で終わってしまう。
                ipc.send(&format!("TERRAIN_BRUSH_MASK_ERROR:{path}"));
            }
        }
    }

    /// 現在のブラシ形状マスクを、必要ならデコードしてキャッシュへ載せる。
    ///
    /// ブラシ適用の入口（レイヤペイント／カバー）から毎回呼ぶ。
    /// 既にキャッシュにあれば `HashMap` の参照 1 回で戻るので、
    /// ドラッグ中（40ms 間隔）に呼び続けても実質コストは無い。
    pub(super) fn ensure_terrain_brush_mask(&mut self) {
        if self.terrain.brush_mask_path == TERRAIN_BRUSH_MASK_NONE {
            return;
        }
        // 借用を切るためにパスを複製する（`ensure_terrain_mask` が `&mut self` を取るため）。
        // 数十バイトの String 複製であり、1 ストローク 25 回でも無視できる。
        let path = self.terrain.brush_mask_path.clone();
        self.ensure_terrain_mask(&path);
    }
}
