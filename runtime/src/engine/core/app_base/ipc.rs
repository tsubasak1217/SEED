use std::fs::OpenOptions;
use std::io::{BufRead, BufReader, Write};
use std::sync::{mpsc, Arc, Mutex};
use std::thread;
use std::time::Duration;

// ============================================================
//  ToolMode — エディタの左ツールバー選択状態
// ============================================================

#[derive(Clone, Copy, PartialEq, Eq, Debug)]
pub enum ToolMode {
    Select,
    Move,
    Rotate,
    Scale,
}

impl Default for ToolMode {
    fn default() -> Self { ToolMode::Select }
}

// ============================================================
//  IpcCommand — エディタから受け取るコマンド
// ============================================================

pub enum IpcCommand {
    Pause,
    Resume,
    Stop,
    /// エディタから転送されたカメラキー押下（キー名: "W","A","S","D","Q","E","SHIFT"）
    CamKeyDown(String),
    /// エディタから転送されたカメラキー離し
    CamKeyUp(String),
    /// Play 時カーソルクランプの有効/無効
    PlayClamp(bool),
    /// ツールモード切り替え
    SetToolMode(ToolMode),
}

// ============================================================
//  IpcClient
// ============================================================

/// Named Pipe クライアント。
/// エディタ（サーバー）への接続、コマンド受信、イベント送信を行う。
pub struct IpcClient {
    commands: mpsc::Receiver<IpcCommand>,
    writer:   Arc<Mutex<std::fs::File>>,
}

impl IpcClient {
    /// パイプ名（`\\.\pipe\<name>` のうち `<name>` 部分）を指定して接続する。
    pub fn connect(pipe_name: &str) -> std::io::Result<Self> {
        let pipe_path = format!(r"\\.\pipe\{}", pipe_name);
        let file = try_open(&pipe_path)?;
        let write_file = file.try_clone()?;
        let writer = Arc::new(Mutex::new(write_file));

        let (tx, rx) = mpsc::channel();
        thread::spawn(move || read_loop(file, tx));

        Ok(Self { commands: rx, writer })
    }

    /// エディタにメッセージを 1 行送信する。
    pub fn send(&self, msg: &str) {
        if let Ok(mut w) = self.writer.lock() {
            let _ = writeln!(w, "{}", msg);
        }
    }

    /// コマンドキューから 1 件取り出す（ブロックしない）。
    pub fn try_recv(&self) -> Option<IpcCommand> {
        self.commands.try_recv().ok()
    }
}

// ─── 内部ヘルパー ──────────────────────────────────────────

/// PeekNamedPipe でデータ確認後のみ ReadFile する。
///
/// try_clone() した複製ハンドルでブロッキング ReadFile を使うと、
/// メインスレッドの WriteFile がカーネルロックで待たされるため、
/// PeekNamedPipe でノンブロッキング確認してから ReadFile する方式を維持する。
fn read_loop(file: std::fs::File, tx: mpsc::Sender<IpcCommand>) {
    use std::os::windows::io::AsRawHandle;

    let mut reader   = BufReader::new(file);
    let mut line_buf = String::new();

    loop {
        let avail = peek_pipe(reader.get_ref().as_raw_handle());
        if avail == 0 {
            thread::sleep(Duration::from_millis(1));
            continue;
        }

        line_buf.clear();
        match reader.read_line(&mut line_buf) {
            Ok(0) | Err(_) => break,
            Ok(_) => {
                let trimmed = line_buf.trim();
                let cmd = if let Some(key) = trimmed.strip_prefix("CAM_KEY_DOWN:") {
                    Some(IpcCommand::CamKeyDown(key.to_string()))
                } else if let Some(key) = trimmed.strip_prefix("CAM_KEY_UP:") {
                    Some(IpcCommand::CamKeyUp(key.to_string()))
                } else {
                    match trimmed {
                        "PAUSE"        => Some(IpcCommand::Pause),
                        "RESUME"       => Some(IpcCommand::Resume),
                        "STOP"         => Some(IpcCommand::Stop),
                        "PLAY_CLAMP:1" => Some(IpcCommand::PlayClamp(true)),
                        "PLAY_CLAMP:0" => Some(IpcCommand::PlayClamp(false)),
                        "TOOL:SELECT"  => Some(IpcCommand::SetToolMode(ToolMode::Select)),
                        "TOOL:MOVE"    => Some(IpcCommand::SetToolMode(ToolMode::Move)),
                        "TOOL:ROTATE"  => Some(IpcCommand::SetToolMode(ToolMode::Rotate)),
                        "TOOL:SCALE"   => Some(IpcCommand::SetToolMode(ToolMode::Scale)),
                        _              => None,
                    }
                };
                if let Some(cmd) = cmd {
                    if tx.send(cmd).is_err() { break; }
                }
            }
        }
    }
}

#[cfg(windows)]
fn peek_pipe(handle: std::os::windows::raw::HANDLE) -> u32 {
    let mut available: u32 = 0;
    unsafe {
        windows_sys::Win32::System::Pipes::PeekNamedPipe(
            handle as _,
            std::ptr::null_mut(),
            0,
            std::ptr::null_mut(),
            &mut available,
            std::ptr::null_mut(),
        );
    }
    available
}

fn try_open(path: &str) -> std::io::Result<std::fs::File> {
    for _ in 0..20 {
        match OpenOptions::new().read(true).write(true).open(path) {
            Ok(f)  => return Ok(f),
            Err(_) => thread::sleep(Duration::from_millis(100)),
        }
    }
    OpenOptions::new().read(true).write(true).open(path)
}
