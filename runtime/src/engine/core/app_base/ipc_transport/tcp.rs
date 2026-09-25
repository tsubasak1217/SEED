// ============================================================
//  tcp.rs — TCP の通信路（Android。エディタ／SeedAndroid が adb forward 越しにつなぐ）
//
//  【流れ】
//    listen: 127.0.0.1:<ポート> に bind する（ループバックだけ。端末の外からは届かず、PC からは adb forward だけが届く）
//    受け付けスレッド（seed-ipc-tcp-accept）:
//      accept（1 本だけ。つながっている間は次を accept しない＝後から来た接続は OS の待ち行列で待つ）
//        → 最初の 1 行 HELLO:<トークン> を照合する（auth.rs。一致しなければ IPC_DENIED:<理由> を書いて閉じ、次の accept へ）
//        → 挨拶の 1 行（READY:0）を書く → 書き込み口を「いまの接続」に据える
//        → ipc.rs の read_loop で 1 行ずつコマンドにして App へ（切断まで戻らない）
//        → 書き込み口を外して閉じる → App へ EditorDisconnected を積む（一時停止中なら再開。session_policy.rs）
//        → 次の accept へ
//    書き込みスレッド（seed-ipc-tcp-write）:
//      App の IpcClient::send が積んだ行を、いまの接続へ 1 行ずつ書く。つながっていなければ捨てる
//      （IPC 無しの Play と同じ。つながる前の行を後の接続へ溜めて送らない）。書けなければ接続を閉じる
//      （読み取り側が切断に気付いて次の接続へ進む）。
//    「つながっているか」の印（ConnectionFlag）:
//      IpcClient::send が見て、つながっていなければ積まずに捨てる（エディタの居ない端末で FPS 等の行を
//      書き込みスレッドへ回さない。つながる前に送った行が後から届くこともない）。
//
//  【挨拶を書く理由】
//  adb forward は、端末で誰も待ち受けていなくても PC 側の接続をいったん受け付ける（その後 adb が閉じる）。
//  そのためエディタは「TCP でつながった」だけではランタイムとつながったか分からない。最初の 1 行（READY:）が
//  届いたらつながった、届かずに閉じられたらまだ待ち受けていない（起動の途中）、と見分ける。
//  READY:{ウィンドウハンドル} は PC のエディタが Play の準備完了に使う行と同じ書式（Android にハンドルは無いので 0）。
//
//  書き込みは Rust の標準ライブラリの TcpStream（Linux / Android では send に MSG_NOSIGNAL を付ける）なので、
//  相手が閉じた後に書いても SIGPIPE でプロセスが落ちない（エラーとして返る）。
// ============================================================

use std::io::{self, BufRead, BufReader, Read, Write};
use std::net::{Ipv4Addr, Shutdown, SocketAddr, TcpListener, TcpStream};
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{mpsc, Arc, Mutex, MutexGuard, PoisonError};
use std::thread;
use std::time::Duration;

use super::auth::{check_hello, denied_line, HelloRejection, TcpAuth, MAX_HELLO_LINE_BYTES};
use crate::engine::core::app_base::ipc::{read_loop, IpcCommand, ReadLoopEnd};

/// つながったら最初に書く挨拶の行（READY:{ウィンドウハンドル}。Android にウィンドウハンドルは無いので 0）。
pub const GREETING: &str = "READY:0";

/// accept が失敗したときに次を試すまでの間隔（ミリ秒）。失敗が続いても空回りしないように。
const ACCEPT_RETRY_INTERVAL_MS: u64 = 200;

/// 1 行を書くときの時間切れ（秒）。相手が読まなくなった接続で書き込みスレッドが止まり続けないように
/// （時間切れは書けなかったとみなして接続を閉じる）。
const WRITE_TIMEOUT_SECS: u64 = 10;

/// 受け付けスレッドの名前（logcat の tid 表示・デバッガでの識別用）。
const ACCEPT_THREAD_NAME: &str = "seed-ipc-tcp-accept";

/// 書き込みスレッドの名前。
const WRITE_THREAD_NAME: &str = "seed-ipc-tcp-write";

/// ログの印（logcat の SEED タグで探しやすくする）。
const LOG_PREFIX: &str = "[SEED IPC]";

/// 行の区切り（プロトコルは 1 行 1 コマンド）。
const LINE_TERMINATOR: char = '\n';

/// いまの接続の書き込み口（つながっていなければ None）。受け付けスレッドと書き込みスレッドで共有する。
type CurrentWriter = Arc<Mutex<Option<TcpStream>>>;

/// エディタとつながっているかの印（IpcClient::send が見る。挨拶を書く前に書き込み口のロックの中で立て、外したら下ろす）。
pub(crate) type ConnectionFlag = Arc<AtomicBool>;

/// 待ち受けを始めた結果。
pub(crate) struct Listening {
    /// 実際に待ち受けたアドレス（ポート 0 を指定したときに、選ばれたポートを知るのに使う）。
    pub local: SocketAddr,
    /// つながっているかの印。
    pub connected: ConnectionFlag,
}

/// 受け付けスレッドと書き込みスレッドが共有するもの。
#[derive(Clone)]
struct Shared {
    /// いまの接続の書き込み口。
    current: CurrentWriter,
    /// つながっているかの印。
    connected: ConnectionFlag,
}

impl Shared {
    /// 挨拶の 1 行を書いてから書き込み口を据え、「つながっている」にする。
    ///
    /// 書き込み口のロックを握ったまま書くので、書き込みスレッドの行より挨拶が必ず先に届く。
    /// 印は挨拶より先に立てる（相手が挨拶を読んで直ちに App の行を待っても、send がその行を捨てずに積むように）。
    fn attach_with_greeting(&self, mut writer: TcpStream) -> io::Result<()> {
        let mut slot = lock(&self.current);
        self.connected.store(true, Ordering::Release);
        if let Err(err) = write_line(&mut writer, GREETING) {
            self.connected.store(false, Ordering::Release);
            return Err(err);
        }
        *slot = Some(writer);
        Ok(())
    }

    /// 書き込み口を外して閉じ、「つながっていない」にする（既に外れていれば何もしない）。
    fn detach(&self) {
        self.connected.store(false, Ordering::Release);
        if let Some(stream) = lock(&self.current).take() {
            let _ = stream.shutdown(Shutdown::Both);
        }
    }
}

/// 1 本の接続を終えた後にすること。
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum AfterConnection {
    /// 次の接続を待つ。
    AcceptNext,
    /// App が居なくなった（受け手のチャンネルが閉じた）ので受け付けをやめる。
    Stop,
}

/// 127.0.0.1:<ポート> で待ち受け、受け付けスレッドと書き込みスレッドを起動する。
///
/// # 引数
/// * `port`     - 待ち受けるポート（0 なら OS が空きポートを選ぶ）
/// * `auth`     - 接続トークンの照合の設定（auth.rs。最初の行の HELLO が一致した接続だけを受け付ける）
/// * `commands` - 受け取ったコマンドを App へ渡す送り口
/// * `outgoing` - App が送る行（IpcClient::send）の受け口
///
/// # 戻り値
/// 実際に待ち受けたアドレスと、つながっているかの印。
pub(crate) fn listen(
    port: u16,
    auth: TcpAuth,
    commands: mpsc::Sender<IpcCommand>,
    outgoing: mpsc::Receiver<String>,
) -> io::Result<Listening> {
    let listener = TcpListener::bind((Ipv4Addr::LOCALHOST, port))?;
    let local = listener.local_addr()?;
    let shared = Shared {
        current: Arc::new(Mutex::new(None)),
        connected: Arc::new(AtomicBool::new(false)),
    };
    let connected = Arc::clone(&shared.connected);

    let writer_shared = shared.clone();
    thread::Builder::new()
        .name(WRITE_THREAD_NAME.to_string())
        .spawn(move || write_to_current(&writer_shared, &outgoing))?;
    thread::Builder::new()
        .name(ACCEPT_THREAD_NAME.to_string())
        .spawn(move || accept_loop(&listener, &shared, &auth, &commands))?;
    Ok(Listening { local, connected })
}

/// 受け付けスレッドの本体（App が居なくなるまで、1 本ずつ接続を受け付けて読む）。
fn accept_loop(listener: &TcpListener, shared: &Shared, auth: &TcpAuth, commands: &mpsc::Sender<IpcCommand>) {
    loop {
        match listener.accept() {
            Ok((stream, peer)) => {
                if serve_connection(stream, peer, shared, auth, commands) == AfterConnection::Stop {
                    return;
                }
            }
            Err(err) => {
                eprintln!("{LOG_PREFIX} 接続を受け付けられませんでした（次の接続を待ちます）: {err}");
                thread::sleep(Duration::from_millis(ACCEPT_RETRY_INTERVAL_MS));
            }
        }
    }
}

/// 1 本の接続を扱う（照合 → 挨拶 → 読み取り → 後始末）。切断まで戻らない。
///
/// # 引数
/// * `stream`   - 受け付けた接続
/// * `peer`     - 相手のアドレス（ログ用。adb forward なら 127.0.0.1 の adbd）
/// * `shared`   - いまの接続の書き込み口と、つながっているかの印
/// * `auth`     - 接続トークンの照合の設定
/// * `commands` - App への送り口
fn serve_connection(
    stream: TcpStream,
    peer: SocketAddr,
    shared: &Shared,
    auth: &TcpAuth,
    commands: &mpsc::Sender<IpcCommand>,
) -> AfterConnection {
    // 命令は 1 行ずつ即座に届けたい（Nagle で溜めない）。書き込みには時間切れを付ける。
    let _ = stream.set_nodelay(true);
    let _ = stream.set_write_timeout(Some(Duration::from_secs(WRITE_TIMEOUT_SECS)));

    // ── 照合: 最初の 1 行が HELLO:<トークン> で一致したときだけ受け付ける（auth.rs）──
    // 読み取りは BufReader 越し。最初の行と一緒に届いた後続の行は、この BufReader に残ったまま read_loop へ渡す。
    let mut reader = BufReader::new(&stream);
    if let Err(rejection) = authenticate(&stream, &mut reader, auth) {
        eprintln!(
            "{LOG_PREFIX} 接続を断りました（{peer}・理由 {}）。起動オプションのトークンを知っている接続（エディタ／SeedAndroid）だけを受け付けます",
            rejection.reason()
        );
        // 断った理由の行の後に送り側だけ閉じる（FIN）。読み残しは照合の BufReader が持っているので、閉じても RST になりにくい
        let _ = write_line(&mut &stream, &denied_line(rejection));
        let _ = stream.shutdown(Shutdown::Write);
        return AfterConnection::AcceptNext;
    }

    let writer = match stream.try_clone() {
        Ok(writer) => writer,
        Err(err) => {
            eprintln!("{LOG_PREFIX} 接続の書き込み口を作れません（この接続は閉じます）: {err}");
            let _ = stream.shutdown(Shutdown::Both);
            return AfterConnection::AcceptNext;
        }
    };
    // 挨拶を書いてから、いまの接続に据える（App の行より必ず先に届く）
    if let Err(err) = shared.attach_with_greeting(writer) {
        eprintln!("{LOG_PREFIX} 挨拶を書けません（この接続は閉じます）: {err}");
        let _ = stream.shutdown(Shutdown::Both);
        return AfterConnection::AcceptNext;
    }
    eprintln!("{LOG_PREFIX} エディタとつながりました（{peer}）");

    // 切断まで 1 行ずつコマンドにして App へ渡す（書式は ipc.rs の read_loop が正典）
    let end = read_loop(reader, commands.clone());

    // 後始末: 書き込み口を外して閉じる（書き込みスレッドが先に閉じていれば何もしない）
    shared.detach();
    let _ = stream.shutdown(Shutdown::Both);
    if end == ReadLoopEnd::ReceiverGone {
        return AfterConnection::Stop;
    }
    eprintln!("{LOG_PREFIX} エディタとの接続が切れました（次の接続を待ちます）");
    if commands.send(IpcCommand::EditorDisconnected).is_err() {
        return AfterConnection::Stop;
    }
    AfterConnection::AcceptNext
}

/// 最初の 1 行（`HELLO:<トークン>`）を時間切れ付きで読み、照合する。
///
/// 時間内に来ない・読めない・長すぎる（改行が無い）行は HELLO でないとみなす。照合の後は読み取りの時間切れを外す
/// （以降は切断まで待つ）。
///
/// # 引数
/// * `stream` - 受け付けた接続（読み取りの時間切れを付け外しする）
/// * `reader` - 接続の読み取り口（読み残しは read_loop が続けて読む）
/// * `auth`   - 照合の設定
fn authenticate(stream: &TcpStream, reader: &mut BufReader<&TcpStream>, auth: &TcpAuth) -> Result<(), HelloRejection> {
    if stream.set_read_timeout(Some(auth.hello_timeout)).is_err() {
        return Err(HelloRejection::NotHello);
    }
    let mut line = String::new();
    let read = reader.by_ref().take(MAX_HELLO_LINE_BYTES).read_line(&mut line);
    let _ = stream.set_read_timeout(None);
    match read {
        Ok(0) | Err(_) => Err(HelloRejection::NotHello),
        Ok(_) => check_hello(&line, &auth.token),
    }
}

/// 書き込みスレッドの本体（App が送った行を、いまの接続へ書く。つながっていなければ捨てる）。
fn write_to_current(shared: &Shared, outgoing: &mpsc::Receiver<String>) {
    // App が IpcClient を捨てる（送り口が閉じる）とループを抜ける
    while let Ok(message) = outgoing.recv() {
        let mut slot = lock(&shared.current);
        let Some(stream) = slot.as_mut() else {
            continue;
        };
        if let Err(err) = write_line(stream, &message) {
            eprintln!("{LOG_PREFIX} エディタへ書けません（接続を閉じます）: {err}");
            shared.connected.store(false, Ordering::Release);
            if let Some(stream) = slot.take() {
                // 読み取り側の read_line が終わり、受け付けスレッドが切断として後始末する
                let _ = stream.shutdown(Shutdown::Both);
            }
        }
    }
}

/// 1 行を書く（行末の改行を足し、1 回の書き込みにまとめる）。
fn write_line(stream: &mut impl Write, line: &str) -> io::Result<()> {
    let mut bytes = String::with_capacity(line.len() + LINE_TERMINATOR.len_utf8());
    bytes.push_str(line);
    bytes.push(LINE_TERMINATOR);
    stream.write_all(bytes.as_bytes())?;
    stream.flush()
}

/// 書き込み口のロックを取る（他のスレッドが panic してロックが毒されても、中身はそのまま使う）。
fn lock(current: &CurrentWriter) -> MutexGuard<'_, Option<TcpStream>> {
    current.lock().unwrap_or_else(PoisonError::into_inner)
}

// ============================================================
//  テスト — ループバックの TCP で、エディタ役のクライアントとつないで確かめる（端末・adb は使わない）
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;
    use crate::engine::core::app_base::ipc::IpcClient;
    use crate::engine::core::app_base::ipc_transport::session_policy::{DisconnectAction, IpcSessionPolicy};
    use crate::engine::core::app_base::ipc_transport::IpcTransportKind;
    use std::time::Instant;

    /// 待ち合わせの上限（遅い PC でも通るよう長めにとる。通常は数ミリ秒で満たされる）。
    const WAIT: Duration = Duration::from_secs(10);

    /// コマンドが届くのを確かめる間隔。
    const POLL: Duration = Duration::from_millis(5);

    /// 「挨拶が来ない」ことを確かめる待ち時間（1 本目がつながっている間の 2 本目）。
    const NO_GREETING_WAIT: Duration = Duration::from_millis(300);

    /// テストの接続トークン（起動オプションの ipc_token にあたる）。
    const TOKEN: &str = "0123456789abcdef0123456789abcdef";

    /// テストの HELLO の待ち時間（黙った接続を素早く断るのを確かめる）。
    const TEST_HELLO_TIMEOUT: Duration = Duration::from_millis(300);

    /// ポート 0（OS が空きポートを選ぶ）で、テストのトークンで待ち受け、選ばれたアドレスを返す。
    fn listen_any() -> (IpcClient, SocketAddr) {
        let auth = TcpAuth { token: TOKEN.to_string(), hello_timeout: TEST_HELLO_TIMEOUT };
        let client = IpcClient::listen_tcp_with_auth(0, auth).expect("127.0.0.1 で待ち受けられる");
        let address = client.tcp_local_addr().expect("TCP なら待ち受けたアドレスがある");
        (client, address)
    }

    /// つなぐだけ（HELLO は送らない。読み取りの時間切れ付き）。
    fn connect_raw(address: SocketAddr, read_timeout: Duration) -> (TcpStream, BufReader<TcpStream>) {
        let stream = TcpStream::connect(address).expect("つながる");
        stream.set_read_timeout(Some(read_timeout)).unwrap();
        let reader = BufReader::new(stream.try_clone().unwrap());
        (stream, reader)
    }

    /// エディタ役としてつなぎ、最初の行で正しいトークンの HELLO を送る。
    fn connect(address: SocketAddr, read_timeout: Duration) -> (TcpStream, BufReader<TcpStream>) {
        let (mut stream, reader) = connect_raw(address, read_timeout);
        stream.write_all(format!("HELLO:{TOKEN}\n").as_bytes()).unwrap();
        (stream, reader)
    }

    /// 閉じられるまで読み、閉じられたことを確かめる（断られた接続）。
    fn assert_closed(reader: &mut BufReader<TcpStream>) {
        let mut rest = String::new();
        let closed = match reader.read_line(&mut rest) {
            Ok(0) => true,
            Ok(_) => false,
            // 相手が閉じた後の読み取りは、OS によって「リセットされた」の失敗になる
            Err(err) => err.kind() != io::ErrorKind::WouldBlock && err.kind() != io::ErrorKind::TimedOut,
        };
        assert!(closed, "断った後は閉じる（残り: {rest:?}）");
    }

    /// 1 行読む（行末の改行を落とす）。
    fn read_line(reader: &mut BufReader<TcpStream>) -> io::Result<String> {
        let mut line = String::new();
        if reader.read_line(&mut line)? == 0 {
            return Err(io::Error::new(io::ErrorKind::UnexpectedEof, "閉じられた"));
        }
        Ok(line.trim_end().to_string())
    }

    /// App 役: コマンドが 1 件届くまで待つ。
    fn next_command(client: &IpcClient) -> IpcCommand {
        let started = Instant::now();
        loop {
            if let Some(command) = client.try_recv() {
                return command;
            }
            assert!(started.elapsed() < WAIT, "{WAIT:?} 以内にコマンドが届きません");
            thread::sleep(POLL);
        }
    }

    /// 127.0.0.1 だけで待ち受け、つながったら最初に挨拶の 1 行を書く。
    #[test]
    fn binds_loopback_only_and_greets_first() {
        let (client, address) = listen_any();
        assert!(address.ip().is_loopback(), "端末の外から届かないよう 127.0.0.1 だけ: {address}");
        assert_eq!(client.transport(), IpcTransportKind::TcpListener);
        let (_stream, mut reader) = connect(address, WAIT);
        assert_eq!(read_line(&mut reader).unwrap(), GREETING);
    }

    /// PAUSE・RESUME がそのままコマンドになり、切断で EditorDisconnected が届く。
    /// 一時停止中に黙って切れたら、再開の判断になる（session_policy）。
    #[test]
    fn pause_resume_arrive_and_silent_disconnect_resumes() {
        let (client, address) = listen_any();
        let (mut stream, mut reader) = connect(address, WAIT);
        assert_eq!(read_line(&mut reader).unwrap(), GREETING);

        stream.write_all(b"PAUSE\n").unwrap();
        assert!(matches!(next_command(&client), IpcCommand::Pause));
        stream.write_all(b"RESUME\r\nPAUSE\n").unwrap();
        assert!(matches!(next_command(&client), IpcCommand::Resume));
        assert!(matches!(next_command(&client), IpcCommand::Pause), "\\r\\n の行末でも読める");

        // 黙って切る（エディタを閉じた・落ちた）。App は最後の PAUSE で一時停止している
        drop(reader);
        drop(stream);
        assert!(matches!(next_command(&client), IpcCommand::EditorDisconnected));
        let mut policy = IpcSessionPolicy::default();
        assert_eq!(policy.on_disconnected(true), DisconnectAction::Resume, "一時停止中の切断は再開");
    }

    /// DETACH を送ってから切れたら、一時停止のまま（SeedAndroid の pause）。
    #[test]
    fn detach_then_disconnect_keeps_pause() {
        let (client, address) = listen_any();
        let (mut stream, mut reader) = connect(address, WAIT);
        assert_eq!(read_line(&mut reader).unwrap(), GREETING);
        stream.write_all(b"PAUSE\nDETACH\n").unwrap();
        drop(reader);
        drop(stream);

        let mut policy = IpcSessionPolicy::default();
        assert!(matches!(next_command(&client), IpcCommand::Pause));
        assert!(matches!(next_command(&client), IpcCommand::Detach));
        policy.on_detach();
        assert!(matches!(next_command(&client), IpcCommand::EditorDisconnected), "DETACH の後に切断が届く（順番は保たれる）");
        assert_eq!(policy.on_disconnected(true), DisconnectAction::KeepPaused);
    }

    /// 切れたら次の接続を受け付ける（エディタを開き直した・SeedAndroid の 2 回目）。
    #[test]
    fn accepts_again_after_disconnect() {
        let (client, address) = listen_any();
        {
            let (_stream, mut reader) = connect(address, WAIT);
            assert_eq!(read_line(&mut reader).unwrap(), GREETING);
        }
        assert!(matches!(next_command(&client), IpcCommand::EditorDisconnected));

        let (mut stream, mut reader) = connect(address, WAIT);
        assert_eq!(read_line(&mut reader).unwrap(), GREETING, "2 本目も挨拶から");
        stream.write_all(b"PAUSE\n").unwrap();
        assert!(matches!(next_command(&client), IpcCommand::Pause));
    }

    /// 1 本だけ受け付ける: 1 本目がつながっている間、2 本目には挨拶が来ない（OS の待ち行列で待つ）。
    /// 1 本目が切れたら 2 本目を受け付ける。
    #[test]
    fn serves_one_connection_at_a_time() {
        let (client, address) = listen_any();
        let (first, mut first_reader) = connect(address, WAIT);
        assert_eq!(read_line(&mut first_reader).unwrap(), GREETING);

        // 2 本目は TCP としてはつながる（OS の待ち行列）が、受け付けられないので何も届かない。
        // 時間切れ付きの読み取りは Windows では失敗後の接続の状態が不定になるため、ノンブロッキングの peek で確かめる
        let (second, mut second_reader) = connect(address, WAIT);
        thread::sleep(NO_GREETING_WAIT);
        second.set_nonblocking(true).unwrap();
        let mut probe = [0u8; 1];
        let peeked = second.peek(&mut probe);
        assert!(
            matches!(peeked, Err(ref err) if err.kind() == io::ErrorKind::WouldBlock),
            "1 本目がつながっている間は 2 本目に挨拶しない: {peeked:?}"
        );
        second.set_nonblocking(false).unwrap();

        drop(first_reader);
        drop(first);
        assert!(matches!(next_command(&client), IpcCommand::EditorDisconnected));
        assert_eq!(read_line(&mut second_reader).unwrap(), GREETING, "1 本目が切れたら 2 本目を受け付ける");
    }

    /// App が送った行は、いまの接続にだけ届く（つながる前に送った行は捨てる＝IPC 無しの Play と同じ）。
    #[test]
    fn app_messages_reach_only_the_current_connection() {
        let (client, address) = listen_any();
        client.send("SENT_BEFORE_CONNECT");
        let (_stream, mut reader) = connect(address, WAIT);
        assert_eq!(read_line(&mut reader).unwrap(), GREETING, "挨拶が必ず最初");
        client.send("FPS:59.9");
        assert_eq!(read_line(&mut reader).unwrap(), "FPS:59.9", "つながる前の行は届かない");
    }

    // ── 接続トークンの照合（auth.rs）──────────────────────────────

    /// HELLO と同じ書き込みで続けて送った命令も落とさない（照合の読み取りの読み残しを read_loop が引き継ぐ）。
    #[test]
    fn commands_sent_right_after_hello_are_kept() {
        let (client, address) = listen_any();
        let (mut stream, mut reader) = connect_raw(address, WAIT);
        stream.write_all(format!("HELLO:{TOKEN}\nPAUSE\nRESUME\n").as_bytes()).unwrap();
        assert_eq!(read_line(&mut reader).unwrap(), GREETING);
        assert!(matches!(next_command(&client), IpcCommand::Pause));
        assert!(matches!(next_command(&client), IpcCommand::Resume));
    }

    /// トークンが違えば IPC_DENIED:token を書いて閉じ、その接続の命令は App へ 1 行も渡さない。
    /// 断った接続は「つながった」に数えない（切断も積まない）。その後の正しい接続は受け付ける。
    #[test]
    fn rejects_wrong_token_without_delivering_commands() {
        let (client, address) = listen_any();
        let (mut stream, mut reader) = connect_raw(address, WAIT);
        stream.write_all(b"HELLO:ffffffffffffffffffffffffffffffff\nPAUSE\nSTOP\n").unwrap();
        assert_eq!(read_line(&mut reader).unwrap(), "IPC_DENIED:token", "理由だけを返す");
        assert_closed(&mut reader);
        thread::sleep(NO_GREETING_WAIT);
        assert!(client.try_recv().is_none(), "断った接続の PAUSE・STOP は App へ届かない（切断も積まない）");

        let (mut good, mut good_reader) = connect(address, WAIT);
        assert_eq!(read_line(&mut good_reader).unwrap(), GREETING, "正しいトークンの接続は受け付ける");
        good.write_all(b"PAUSE\n").unwrap();
        assert!(matches!(next_command(&client), IpcCommand::Pause));
    }

    /// 最初の行が HELLO でない（いきなり命令を送った）接続は IPC_DENIED:hello で断る。
    #[test]
    fn rejects_commands_before_hello() {
        let (client, address) = listen_any();
        let (mut stream, mut reader) = connect_raw(address, WAIT);
        stream.write_all(format!("PAUSE\nHELLO:{TOKEN}\n").as_bytes()).unwrap();
        assert_eq!(read_line(&mut reader).unwrap(), "IPC_DENIED:hello");
        assert_closed(&mut reader);
        thread::sleep(NO_GREETING_WAIT);
        assert!(client.try_recv().is_none(), "HELLO より前の命令は受け付けない");
    }

    /// 何も送らない接続は、HELLO の待ち時間が過ぎたら断って閉じる（1 本だけの受け付けを塞ぎ続けない）。
    #[test]
    fn rejects_silent_connection_after_timeout() {
        let (client, address) = listen_any();
        let (_silent, mut silent_reader) = connect_raw(address, WAIT);
        assert_eq!(read_line(&mut silent_reader).unwrap(), "IPC_DENIED:hello", "待ち時間の後に断る");
        assert_closed(&mut silent_reader);

        let (_stream, mut reader) = connect(address, WAIT);
        assert_eq!(read_line(&mut reader).unwrap(), GREETING, "次の接続は受け付ける");
        assert!(client.try_recv().is_none());
    }
}
