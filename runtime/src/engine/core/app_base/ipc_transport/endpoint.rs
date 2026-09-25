// ============================================================
//  endpoint.rs — 起動引数から IPC の通信路を選び、開く
//
//  【決め方】（choose_endpoint。純粋な処理・単体テスト付き）
//    1. パイプ名がある（PC のエディタが --pipe=<名前> を渡した）→ 名前付きパイプ（従来どおり。ポートより優先）
//    2. ポートがある（Android の起動オプション ipc_port・PC の --ipc-port）→ 127.0.0.1:<ポート> で待ち受ける
//    3. どちらも無い（配布版・ランチャーからの起動）→ IPC 無し（従来どおり）
//
//  【開けなかったとき】
//  パイプは従来どおり黙って IPC 無しにする（エディタ側が接続の時間切れで気付く）。
//  TCP は理由を 1 行だけ標準エラー（Android は logcat）へ出して IPC 無しで続ける
//  （ポートの使用中・INTERNET 権限の無い APK 等。ゲームは止めない）。
// ============================================================

use crate::engine::core::app_base::ipc::IpcClient;

/// ログの印（logcat の SEED タグで探しやすくする）。
const LOG_PREFIX: &str = "[SEED IPC]";

/// 使う通信路。
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum IpcEndpoint {
    /// 名前付きパイプへつなぐ（`\\.\pipe\<名前>` の `<名前>`）。
    NamedPipe(String),
    /// 127.0.0.1:<ポート> で待ち受ける（0 なら OS が空きポートを選ぶ。単体テスト用）。
    TcpListen(u16),
    /// IPC 無し。
    Disabled,
}

/// 起動引数から通信路を選ぶ【純関数】。
///
/// # 引数
/// * `pipe_name` - `LaunchArgs.pipe_name`（PC のエディタの --pipe=）
/// * `ipc_port`  - `LaunchArgs.ipc_port`（Android の起動オプション・PC の --ipc-port=）
pub fn choose_endpoint(pipe_name: Option<&str>, ipc_port: Option<u16>) -> IpcEndpoint {
    // パイプ名は中身を問わず従来どおりそのまま使う（空なら接続に失敗して IPC 無しになるのも従来どおり）
    if let Some(name) = pipe_name {
        return IpcEndpoint::NamedPipe(name.to_string());
    }
    match ipc_port {
        Some(port) => IpcEndpoint::TcpListen(port),
        None => IpcEndpoint::Disabled,
    }
}

/// 選んだ通信路を開く（開けなければ None ＝ IPC 無しの Play）。
///
/// # 引数
/// * `endpoint` - `choose_endpoint` の結果
pub fn open_endpoint(endpoint: &IpcEndpoint) -> Option<IpcClient> {
    match endpoint {
        IpcEndpoint::NamedPipe(name) => IpcClient::connect(name).ok(),
        IpcEndpoint::TcpListen(port) => match IpcClient::listen_tcp(*port) {
            Ok(client) => {
                if let Some(address) = client.tcp_local_addr() {
                    eprintln!(
                        "{LOG_PREFIX} エディタとの通信路: {address} で待ち受けます（adb forward 越しの接続を 1 本ずつ受け付けます）"
                    );
                }
                Some(client)
            }
            Err(err) => {
                eprintln!(
                    "{LOG_PREFIX} エディタとの通信路（127.0.0.1:{port}）を開けません。一時停止などのエディタからの操作は使えません\
                     （ポートの使用中・APK に INTERNET 権限が無い等）: {err}"
                );
                None
            }
        },
        IpcEndpoint::Disabled => None,
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    /// パイプ名が最優先、次にポート、どちらも無ければ IPC 無し。
    #[test]
    fn pipe_wins_then_port_then_disabled() {
        assert_eq!(choose_endpoint(Some("SEED_x"), Some(52735)), IpcEndpoint::NamedPipe("SEED_x".to_string()));
        assert_eq!(choose_endpoint(Some("SEED_x"), None), IpcEndpoint::NamedPipe("SEED_x".to_string()));
        assert_eq!(choose_endpoint(None, Some(52735)), IpcEndpoint::TcpListen(52735));
        assert_eq!(choose_endpoint(None, None), IpcEndpoint::Disabled, "パイプも TCP も無ければ従来どおり IPC 無し");
    }

    /// IPC 無しは何も開かない（従来の「--pipe が無い Play」と同じ）。
    #[test]
    fn disabled_opens_nothing() {
        assert!(open_endpoint(&IpcEndpoint::Disabled).is_none());
    }
}
