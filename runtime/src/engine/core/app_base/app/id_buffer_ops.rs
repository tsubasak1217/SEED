// ============================================================
//  id_buffer_ops.rs — ピッキングの ID バッファ（画面と同じ大きさの Rgba32Float）をいつ持つか
//
//  【担当】
//  - ID バッファの持ち方（起動時から持つ／ID パスを描くときに初めて作る／作らない）の判断（純関数＋App の窓口）。
//  - 「描くときに作る」場合の作成（図鑑のサムネイルの撮影＝thumbnail_ops.rs が ID パスを描く前に呼ぶ）。
//    作った後の大きさの追従は on_resize（event_handler.rs）が受け持つ。
//
//  【なぜ単体の Play では起動時に作らないか】ID パスを描くのは、エディタの操作（Edit のピック・
//  エディタの Play の一時停止中のピック・D&D の着地点）と、Play 中も ID パスを描く指定（SEED_ID_PASS_IN_PLAY）、
//  図鑑のサムネイルの撮影だけ。エディタに接続していない Play（--mode=play の単体起動・パッケージ実行・Android）は
//  ふだん ID パスを描かないのに、1080x2400 で 39.6 MiB の ID バッファを持ち、窓の大きさの知らせのたびに
//  作り直していた（docs/rendering_profiles.md）。そこで単体の Play では起動時に作らず、ID パスを描くときに
//  初めて作る（描画の構成の picking=false〈UI だけの構成〉ではそれもしない＝従来どおり）。
// ============================================================

use super::{App, RuntimeMode};
use crate::engine::methods::drawer::IdBuffer;

/// ピッキングの ID バッファの持ち方。
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub(super) enum IdBufferPolicy {
    /// 起動時から持ち、窓の大きさの知らせのたびに作り直す（Edit・エディタの Play・Play 中も ID パスを描く指定）。
    Always,
    /// 起動時には作らず、ID パスを描くとき（図鑑のサムネイルの撮影）に初めて作る（単体の Play で picking=true）。
    OnDemand,
    /// 作らない（単体の Play で picking=false＝UI だけの構成）。
    Never,
}

/// 持ち方を決める材料（App の状態から集める。純関数で決めるため）。
#[derive(Debug, Clone, Copy, Default)]
pub(super) struct IdBufferInputs {
    /// Edit モード（シーンビューのピック）。
    pub edit_mode: bool,
    /// エディタのビューポートへ埋め込まれている（Edit のまま Play へ入る「その場の Play」を含む）。
    pub embedded: bool,
    /// エディタの名前付きパイプでつながった Play（エディタが別のプロセスで起動した Play。一時停止でピックする）。
    pub editor_play: bool,
    /// Play 中も ID パスを毎フレーム描く指定（SEED_ID_PASS_IN_PLAY。合成の第 3 層のマスク）。
    pub id_pass_in_play: bool,
    /// 描画の構成の picking の旗（単体の Play で、ID パスを描くときに作ってよいか）。
    pub picking_flag: bool,
}

/// ID バッファの持ち方を決める（純関数）。
///
/// エディタにつながっている（Edit・埋め込み・エディタの Play）か Play 中も ID パスを描く指定なら起動時から持つ。
/// それ以外（エディタに接続していない Play）は、構成の picking が true なら描くときに作り、false なら作らない。
pub(super) fn decide_id_buffer_policy(inputs: &IdBufferInputs) -> IdBufferPolicy {
    if inputs.edit_mode || inputs.embedded || inputs.editor_play || inputs.id_pass_in_play {
        IdBufferPolicy::Always
    } else if inputs.picking_flag {
        IdBufferPolicy::OnDemand
    } else {
        IdBufferPolicy::Never
    }
}

/// ID バッファを作らない構成（picking=false）で撮影を頼まれたときの文言（レビュー #12）。
pub(super) const ID_BUFFER_DISABLED_BY_PROFILE: &str =
    "この描画の構成（picking=false）では撮影できません（背景を抜く ID バッファを作らない構成です）";

/// 作ってよい構成なのに ID バッファを用意できなかったときの文言（窓の大きさが未確定・描画の準備前など）。
pub(super) const ID_BUFFER_NOT_READY: &str =
    "ID バッファを用意できなかったため撮影できません（窓の大きさが決まっていない・描画の準備ができていない可能性）";

/// ID パスを描く準備（`ensure_id_buffer` の後）ができていないときの理由（純関数）。できていれば None。
///
/// 以前は ID バッファが無いまま撮影を続け、読み戻しも予約されないので 30 秒の期限切れまで待ってから
/// 「フレームが回っていない可能性」という誤った理由で失敗していた（thumbnail_ops.rs。レビュー #12）。
pub(super) fn id_buffer_unavailable_reason(has_buffer: bool, policy: IdBufferPolicy) -> Option<&'static str> {
    match (has_buffer, policy) {
        (true, _) => None,
        (false, IdBufferPolicy::Never) => Some(ID_BUFFER_DISABLED_BY_PROFILE),
        (false, IdBufferPolicy::Always | IdBufferPolicy::OnDemand) => Some(ID_BUFFER_NOT_READY),
    }
}

impl App {
    /// 今の状態での ID バッファの持ち方。
    fn id_buffer_policy(&self) -> IdBufferPolicy {
        decide_id_buffer_policy(&IdBufferInputs {
            edit_mode: self.mode == RuntimeMode::Edit,
            embedded: self.is_embedded(),
            editor_play: crate::engine::app_env::is_editor_play(),
            id_pass_in_play: crate::engine::methods::drawer::id_pass::id_pass_enabled_in_play(),
            picking_flag: self.render_profile.flags.picking,
        })
    }

    /// ピッキングの ID バッファを起動時から持つか（app_init が作るか・on_resize が作り直すかの判断）。
    pub(super) fn id_buffer_wanted(&self) -> bool {
        self.id_buffer_policy() == IdBufferPolicy::Always
    }

    /// ID パスを描く前に、ID バッファが無ければ作る（単体の Play で図鑑のサムネイルを撮るとき。構成が許すときだけ）。
    ///
    /// 大きさは on_resize・app_init と同じ「描画解像度」（内部解像度固定ならその大きさ、既定は窓の大きさ）。
    /// 作った後は on_resize が窓の大きさの知らせのたびに合わせ直す（ID パスは共有の深度と同じパスに入るので、
    /// 大きさが食い違うと wgpu の検証で止まる）。
    pub(super) fn ensure_id_buffer(&mut self) {
        if self.id_buffer.is_some() || self.id_buffer_policy() == IdBufferPolicy::Never {
            return;
        }
        let window_size = self
            .get_parent_client_size()
            .or_else(|| self.window.as_ref().map(|window| window.inner_size()));
        let Some(window_size) = window_size else {
            return;
        };
        let (width, height) = self
            .fixed_render_resolution()
            .unwrap_or((window_size.width, window_size.height));
        let Some(draw_ctx) = self.draw_ctx.as_ref() else {
            return;
        };
        if width == 0 || height == 0 {
            return;
        }
        eprintln!("[SEED PICKING] ID バッファを作りました（{width}x{height}。ID パスを描くため＝図鑑のサムネイルの撮影など）");
        self.id_buffer = Some(IdBuffer::new(&draw_ctx.device, width, height));
    }

    /// 撮影（ID パスを描く）の前に ID バッファを用意し、用意できなければ理由を返す（thumbnail_ops.rs が使う）。
    pub(super) fn prepare_id_buffer_for_capture(&mut self) -> Result<(), &'static str> {
        self.ensure_id_buffer();
        match id_buffer_unavailable_reason(self.id_buffer.is_some(), self.id_buffer_policy()) {
            Some(reason) => Err(reason),
            None => Ok(()),
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    /// エディタにつながっている・Play 中も ID パスを描く指定なら、構成によらず起動時から持つ（従来どおり）。
    #[test]
    fn editor_connected_runs_always_keep_the_buffer() {
        for picking_flag in [true, false] {
            for inputs in [
                IdBufferInputs { edit_mode: true, picking_flag, ..Default::default() },
                IdBufferInputs { embedded: true, picking_flag, ..Default::default() },
                IdBufferInputs { editor_play: true, picking_flag, ..Default::default() },
                IdBufferInputs { id_pass_in_play: true, picking_flag, ..Default::default() },
            ] {
                assert_eq!(decide_id_buffer_policy(&inputs), IdBufferPolicy::Always, "{inputs:?}");
            }
        }
    }

    /// エディタに接続していない Play: full（picking=true）は描くときに作る、ui（picking=false）は作らない。
    #[test]
    fn standalone_play_creates_on_demand_or_never() {
        let full = IdBufferInputs { picking_flag: true, ..Default::default() };
        assert_eq!(decide_id_buffer_policy(&full), IdBufferPolicy::OnDemand);
        let ui = IdBufferInputs { picking_flag: false, ..Default::default() };
        assert_eq!(decide_id_buffer_policy(&ui), IdBufferPolicy::Never);
    }

    /// レビュー #12: ID バッファが無いまま撮影を進めない。作らない構成なら構成の理由、作ってよいのに無ければ準備の理由。
    #[test]
    fn capture_is_refused_without_id_buffer_with_the_right_reason() {
        for policy in [IdBufferPolicy::Always, IdBufferPolicy::OnDemand, IdBufferPolicy::Never] {
            assert_eq!(id_buffer_unavailable_reason(true, policy), None, "あれば撮れる（{policy:?}）");
        }
        assert_eq!(id_buffer_unavailable_reason(false, IdBufferPolicy::Never), Some(ID_BUFFER_DISABLED_BY_PROFILE));
        assert_eq!(id_buffer_unavailable_reason(false, IdBufferPolicy::OnDemand), Some(ID_BUFFER_NOT_READY));
        assert_eq!(id_buffer_unavailable_reason(false, IdBufferPolicy::Always), Some(ID_BUFFER_NOT_READY));
    }
}
