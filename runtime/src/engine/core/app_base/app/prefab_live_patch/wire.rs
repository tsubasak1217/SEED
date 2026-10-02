// ============================================================
//  prefab_live_patch/wire.rs — 当て直し・書き戻しの応答の文字列（エディタとの約束を 1 か所に置く）
//
//  ・PREFAB_LIVE_PATCH_DONE:{件数},{仮想パス}   … 当て直した（0 件でも返す）
//  ・PREFAB_LIVE_PATCH_ERROR:{理由}            … 当て直せなかった（Play でない・読めない 等）
//  ・PREFAB_WRITE_BACK_DONE:{件数},{仮想パス}   … 書き戻した（件数は続けて当て直した Play 中のインスタンス数）
//  ・PREFAB_WRITE_BACK_ERROR:{理由}            … 書き戻せなかった
//  仮想パスにはカンマが入り得るので、エディタは最初のカンマだけで区切る（PREFAB_REAPPLY_DONE と同じ）。
//  エディタ側の受け口: editor/src/Runtime/RuntimeManager.cs（PREFAB_LIVE_PATCH_* / PREFAB_WRITE_BACK_*）。
// ============================================================

/// 当て直しの完了の接頭辞。
pub(crate) const LIVE_PATCH_DONE_PREFIX: &str = "PREFAB_LIVE_PATCH_DONE:";
/// 当て直しの失敗の接頭辞。
pub(crate) const LIVE_PATCH_ERROR_PREFIX: &str = "PREFAB_LIVE_PATCH_ERROR:";
/// 書き戻しの完了の接頭辞。
pub(crate) const WRITE_BACK_DONE_PREFIX: &str = "PREFAB_WRITE_BACK_DONE:";
/// 書き戻しの失敗の接頭辞。
pub(crate) const WRITE_BACK_ERROR_PREFIX: &str = "PREFAB_WRITE_BACK_ERROR:";

/// Play 中でないときの理由（Edit のワールドは丸ごとの再展開 PREFAB_REAPPLY_PATH で更新する）。
pub(crate) const REASON_NOT_PLAYING: &str = "Play 中だけ使えます（Edit では PREFAB_REAPPLY_PATH を使ってください）";
/// シーン・描画の準備ができていないときの理由。
pub(crate) const REASON_NO_SCENE: &str = "シーンが読み込まれていません";

/// 当て直しの完了の応答。
pub(crate) fn live_patch_done(count: usize, vpath: &str) -> String {
    format!("{LIVE_PATCH_DONE_PREFIX}{count},{vpath}")
}

/// 当て直しの失敗の応答（改行は 1 行の約束を壊すので空白にする）。
pub(crate) fn live_patch_error(reason: &str) -> String {
    format!("{LIVE_PATCH_ERROR_PREFIX}{}", one_line(reason))
}

/// 書き戻しの完了の応答。
pub(crate) fn write_back_done(count: usize, vpath: &str) -> String {
    format!("{WRITE_BACK_DONE_PREFIX}{count},{vpath}")
}

/// 書き戻しの失敗の応答。
pub(crate) fn write_back_error(reason: &str) -> String {
    format!("{WRITE_BACK_ERROR_PREFIX}{}", one_line(reason))
}

/// IPC は 1 行 1 メッセージなので、理由の改行を空白へ寄せる。
fn one_line(text: &str) -> String {
    text.replace(['\r', '\n'], " ")
}

#[cfg(test)]
mod tests {
    use super::*;

    /// 応答の形（エディタの RuntimeManager が読む形）を固定する。
    #[test]
    fn reply_formats_are_stable() {
        assert_eq!(live_patch_done(2, "assets://ui/A,B.actor"), "PREFAB_LIVE_PATCH_DONE:2,assets://ui/A,B.actor");
        assert_eq!(live_patch_error("x\ny"), "PREFAB_LIVE_PATCH_ERROR:x y");
        assert_eq!(write_back_done(1, "assets://ui/A.actor"), "PREFAB_WRITE_BACK_DONE:1,assets://ui/A.actor");
        assert_eq!(write_back_error("bad"), "PREFAB_WRITE_BACK_ERROR:bad");
    }
}
