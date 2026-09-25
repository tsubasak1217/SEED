// ============================================================
//  endpoint.rs — 起動引数から IPC の通信路を選び、開く
//
//  【決め方】（choose_endpoint。純粋な処理・単体テスト付き）
//    1. パイプ名がある（PC のエディタが --pipe=<名前> を渡した）→ 名前付きパイプ（従来どおり。ポートより優先。トークンは使わない）
//    2. ポートとトークンがある（Android の起動オプション ipc_port・ipc_token／PC の --ipc-port・--ipc-token）
//       → 127.0.0.1:<ポート> で待ち受け、最初の行のトークンが一致した接続だけを受け付ける（auth.rs）
//    3. ポートだけあってトークンが無い → 待ち受けない（トークン無しでは他のアプリからの接続を見分けられないため。理由を 1 行出す）
//    4. どちらも無い（配布版・ランチャーからの起動）→ IPC 無し（従来どおり）
//
//  【開けなかったとき】
//  パイプは従来どおり黙って IPC 無しにする（エディタ側が接続の時間切れで気付く）。
//  TCP は理由を 1 行だけ標準エラー（Android は logcat）へ出して IPC 無しで続ける
//  （ポートの使用中・INTERNET 権限の無い APK・トークンが無い等。ゲームは止めない）。
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
    TcpListen {
        /// 待ち受けるポート。
        port: u16,
        /// 接続トークン（最初の行の HELLO:<トークン> と照合する）。
        token: String,
    },
    /// ポートの指定はあるがトークンが無い（待ち受けない。古いエディタ・指定の誤り）。
    TcpMissingToken(u16),
    /// IPC 無し。
    Disabled,
}

/// 起動引数から通信路を選ぶ【純関数】。
///
/// # 引数
/// * `pipe_name` - `LaunchArgs.pipe_name`（PC のエディタの --pipe=）
/// * `ipc_port`  - `LaunchArgs.ipc_port`（Android の起動オプション・PC の --ipc-port=）
/// * `ipc_token` - `LaunchArgs.ipc_token`（Android の起動オプション・PC の --ipc-token=）
pub fn choose_endpoint(pipe_name: Option<&str>, ipc_port: Option<u16>, ipc_token: Option<&str>) -> IpcEndpoint {
    // パイプ名は中身を問わず従来どおりそのまま使う（空なら接続に失敗して IPC 無しになるのも従来どおり）
    if let Some(name) = pipe_name {
        return IpcEndpoint::NamedPipe(name.to_string());
    }
    match (ipc_port, ipc_token) {
        (Some(port), Some(token)) => IpcEndpoint::TcpListen { port, token: token.to_string() },
        (Some(port), None) => IpcEndpoint::TcpMissingToken(port),
        (None, _) => IpcEndpoint::Disabled,
    }
}

/// 選んだ通信路を開く（開けなければ None ＝ IPC 無しの Play）。
///
/// # 引数
/// * `endpoint` - `choose_endpoint` の結果
pub fn open_endpoint(endpoint: &IpcEndpoint) -> Option<IpcClient> {
    match endpoint {
        IpcEndpoint::NamedPipe(name) => IpcClient::connect(name).ok(),
        IpcEndpoint::TcpListen { port, token } => match IpcClient::listen_tcp(*port, token.clone()) {
            Ok(client) => {
                if let Some(address) = client.tcp_local_addr() {
                    eprintln!(
                        "{LOG_PREFIX} エディタとの通信路: {address} で待ち受けます（adb forward 越しの接続を 1 本ずつ、接続トークンを照合して受け付けます）"
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
        IpcEndpoint::TcpMissingToken(port) => {
            eprintln!(
                "{LOG_PREFIX} IPC のポート {port} の指定はありますが接続トークン（ipc_token）が無いため、待ち受けません\
                 （トークン無しでは他のアプリからの接続を見分けられません。エディタ／SeedAndroid の run で起動し直してください）"
            );
            None
        }
        IpcEndpoint::Disabled => None,
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    const TOKEN: &str = "0123456789abcdef0123456789abcdef";

    /// パイプ名が最優先（トークンは使わない）、次にポート＋トークン、どちらも無ければ IPC 無し。
    #[test]
    fn pipe_wins_then_port_with_token_then_disabled() {
        assert_eq!(choose_endpoint(Some("SEED_x"), Some(52735), Some(TOKEN)), IpcEndpoint::NamedPipe("SEED_x".to_string()));
        assert_eq!(choose_endpoint(Some("SEED_x"), None, None), IpcEndpoint::NamedPipe("SEED_x".to_string()), "パイプはトークン不要");
        assert_eq!(
            choose_endpoint(None, Some(52735), Some(TOKEN)),
            IpcEndpoint::TcpListen { port: 52735, token: TOKEN.to_string() }
        );
        assert_eq!(choose_endpoint(None, None, None), IpcEndpoint::Disabled, "パイプも TCP も無ければ従来どおり IPC 無し");
        assert_eq!(choose_endpoint(None, None, Some(TOKEN)), IpcEndpoint::Disabled, "トークンだけでは待ち受けない");
    }

    /// ポートだけでトークンが無ければ待ち受けない（開いても何も返さない）。
    #[test]
    fn port_without_token_does_not_listen() {
        let endpoint = choose_endpoint(None, Some(52735), None);
        assert_eq!(endpoint, IpcEndpoint::TcpMissingToken(52735));
        assert!(open_endpoint(&endpoint).is_none());
    }

    /// IPC 無しは何も開かない（従来の「--pipe が無い Play」と同じ）。
    #[test]
    fn disabled_opens_nothing() {
        assert!(open_endpoint(&IpcEndpoint::Disabled).is_none());
    }
}
