// ============================================================
//  session_policy.rs — TCP の通信路が切れたときに一時停止をどう扱うか（純粋な処理）
//
//  【決まり】
//    - 相手（エディタ）が黙って切れた（エディタを閉じた・落ちた・USB が外れた）→ 一時停止中なら再開する。
//      端末のゲームが誰にも解けない一時停止のまま残らないようにする（Play は続ける）。
//    - 相手が切る前に DETACH（意図した切り離し）を送っていた → 一時停止のまま据え置く。
//      SeedAndroid の pause / resume は「つないで 1 命令送って DETACH して切る」ので、pause の効果を切断で消さない。
//  DETACH の印は切断を 1 回受けたら消す（次の接続へ持ち越さない）。
//
//  App（app/ipc_handler.rs）が IpcCommand::Detach と IpcCommand::EditorDisconnected を受けたときに呼ぶ。
//  名前付きパイプ（PC）では EditorDisconnected は積まれないので、PC の Play の振る舞いは変わらない。
//
//  【一時停止中の見た目（pause_keeps_game_view）】
//  PC（名前付きパイプ）の PAUSE は、ランタイムの paused を立てて描画をエディタの見た目（デバッグカメラ・グリッド・ギズモ）に
//  切り替える（エディタは一時停止中のランタイムのウィンドウをビューポートへ埋め込み、デバッグカメラで見回す）。
//  TCP（Android の端末）ではデバッグカメラを動かす手段が無いので、ゲームの時間・物理・スクリプトだけを止め、
//  画面はゲームのカメラのまま止める（App の remote_paused。app/ipc_handler.rs）。
// ============================================================

use super::IpcTransportKind;

/// 切断・切り離しの扱い（App が 1 つ持つ）。
#[derive(Debug, Clone, Copy, Default, PartialEq, Eq)]
pub struct IpcSessionPolicy {
    /// いまの接続で DETACH を受けたか（次の切断で一時停止を据え置く）。
    detach_requested: bool,
}

/// 切断を受けたときにすること。
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum DisconnectAction {
    /// 何もしない（一時停止していない）。
    Nothing,
    /// 一時停止を解いて Play を続ける（黙って切れた）。
    Resume,
    /// 一時停止のまま据え置く（DETACH の後の切断）。
    KeepPaused,
}

/// PAUSE を受けたときに、画面をゲームのカメラのまま止めるか【純関数】。
///
/// # 引数
/// * `transport` - PAUSE が届いた通信路の種類
///
/// # 戻り値
/// true ならゲームの画面のまま止める（TCP＝Android の端末）。false なら従来どおりエディタの見た目に切り替える（名前付きパイプ＝PC）。
pub fn pause_keeps_game_view(transport: IpcTransportKind) -> bool {
    match transport {
        IpcTransportKind::NamedPipe => false,
        IpcTransportKind::TcpListener => true,
    }
}

impl IpcSessionPolicy {
    /// DETACH（相手が「このまま切り離す」と言った）を受けた。
    pub fn on_detach(&mut self) {
        self.detach_requested = true;
    }

    /// 通信路が切れた。一時停止をどうするかを返し、DETACH の印を消す。
    ///
    /// # 引数
    /// * `paused` - いま一時停止しているか（App の paused）
    pub fn on_disconnected(&mut self, paused: bool) -> DisconnectAction {
        let detached = std::mem::take(&mut self.detach_requested);
        match (paused, detached) {
            (false, _) => DisconnectAction::Nothing,
            (true, false) => DisconnectAction::Resume,
            (true, true) => DisconnectAction::KeepPaused,
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    /// 黙って切れたら一時停止を解く。一時停止していなければ何もしない。
    #[test]
    fn silent_disconnect_resumes_paused_game() {
        let mut policy = IpcSessionPolicy::default();
        assert_eq!(policy.on_disconnected(true), DisconnectAction::Resume);
        assert_eq!(policy.on_disconnected(false), DisconnectAction::Nothing);
    }

    /// PC（名前付きパイプ）の一時停止は従来どおりエディタの見た目、TCP（端末）はゲームの画面のまま。
    #[test]
    fn only_tcp_pause_keeps_game_view() {
        assert!(!pause_keeps_game_view(IpcTransportKind::NamedPipe), "PC の PAUSE の見た目は変えない");
        assert!(pause_keeps_game_view(IpcTransportKind::TcpListener), "端末ではデバッグカメラへ切り替えない");
    }

    /// DETACH の後の切断は一時停止のまま。印は 1 回の切断で消え、次の接続へ持ち越さない。
    #[test]
    fn detach_keeps_pause_only_for_that_connection() {
        let mut policy = IpcSessionPolicy::default();
        policy.on_detach();
        assert_eq!(policy.on_disconnected(true), DisconnectAction::KeepPaused);
        assert_eq!(policy.on_disconnected(true), DisconnectAction::Resume, "次の接続が黙って切れたら再開する");

        // 一時停止していないときの DETACH も印は消える
        policy.on_detach();
        assert_eq!(policy.on_disconnected(false), DisconnectAction::Nothing);
        assert_eq!(policy.on_disconnected(true), DisconnectAction::Resume);
    }
}
