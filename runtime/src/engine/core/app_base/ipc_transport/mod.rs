// ============================================================
//  ipc_transport/ — エディタとランタイムの IPC の「通信路」（段階D-1・2026-09-25）
//
//  【役割】
//  IPC の中身（1 行 1 コマンドの文字列プロトコル。書式の正典は ipc.rs の read_loop と IpcCommand）は変えずに、
//  行を運ぶ通信路だけを差し替えられるようにする。読み書きの本体（ipc.rs の read_loop / write_loop）は
//  `Read` / `Write` を満たすものなら何でも受け、通信路ごとの違いはこのフォルダの中に閉じ込める。
//
//    名前付きパイプ（pipe.rs） … PC のエディタ（Edit の埋め込み・PC の Play）。起動引数 --pipe=<名前>。従来どおり
//    TCP（tcp.rs）            … Android（エディタ／SeedAndroid が adb forward 越しにつなぐ）。起動オプション ipc_port
//                               （am start の extra seed.ipc_port → platform::launch_options → LaunchArgs.ipc_port）。
//                               PC でも --ipc-port=<ポート> で同じ通信路を試せる（検証用）
//
//  【通信路ごとの違い】
//                     名前付きパイプ                    TCP
//    つなぐ向き       ランタイム → エディタ（起動時に 1 回）  エディタ → ランタイム（127.0.0.1 で listen し 1 本ずつ accept）
//    つながる前       —（つながらなければ IPC 無し）    IPC 無しの Play と同じ（送る行は捨てる）
//    つながったとき   —                                 ランタイムが最初に挨拶の 1 行（READY:0）を書く
//    切れたとき       何もしない（従来どおり）           EditorDisconnected を App へ積み、次の接続を待つ
//                                                       → 一時停止中なら再開（session_policy.rs。DETACH の後の切断は据え置き）
//
//  どの通信路を使うかは起動引数だけで決める（endpoint.rs。純粋な処理）。
// ============================================================

/// 起動引数から通信路を選ぶ（純粋な処理）と、選んだ通信路を開く。
pub mod endpoint;
/// 名前付きパイプ（Windows。PC のエディタ）。
pub(crate) mod pipe;
/// 切断・切り離しのときに一時停止をどう扱うか（純粋な処理）。
pub mod session_policy;
/// TCP（127.0.0.1 で待ち受ける。Android の adb forward 越しの接続）。
pub(crate) mod tcp;

pub use endpoint::{choose_endpoint, open_endpoint, IpcEndpoint};
pub use session_policy::IpcSessionPolicy;

/// 開いた IPC の通信路の種類（ログの出し分け・実行環境フラグの判定に使う）。
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum IpcTransportKind {
    /// 名前付きパイプ（PC のエディタとつながっている）。
    NamedPipe,
    /// TCP で待ち受けている（Android。エディタがつないでいるとは限らない）。
    TcpListener,
}
