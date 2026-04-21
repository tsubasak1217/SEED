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
    /// Ctrl キー押下（エディタから転送）
    CtrlDown,
    /// Ctrl キー離し（エディタから転送）
    CtrlUp,
    /// Ctrl+Z 相当（エディタから転送）
    Undo,
    /// Ctrl+Y 相当（エディタから転送）
    Redo,
    /// ヒエラルキーからの選択（インスタンスインデックス）
    Select(u32),
    /// 指定インスタンスのみ削除（子は切り離してルートへ）
    Delete(Vec<u32>),
    /// 指定インスタンスとその全子孫を削除
    DeleteRecursive(Vec<u32>),
    /// 親子付け変更（new_parent=None はルートへ）
    Reparent { child: u32, new_parent: Option<u32> },
    /// インスタンス名変更
    Rename { idx: u32, name: String },
    /// シーンを指定パスへ保存
    SaveScene(String),
    /// グループフォルダ作成（parent=None はルート）
    CreateGroup { name: String, parent: Option<u32> },
    /// グループフォルダ作成 + 子を一括移動
    CreateGroupWithChildren { name: String, parent: Option<u32>, children: Vec<u32> },
    /// 複数インスタンスの一括選択（インスタンスインデックスのリスト）
    SelectMulti(Vec<u32>),
    /// 選択インスタンスをクリップボードへコピー
    Copy,
    /// クリップボードの内容をペースト
    Paste,
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
                        "CTRL_DOWN"    => Some(IpcCommand::CtrlDown),
                        "CTRL_UP"      => Some(IpcCommand::CtrlUp),
                        "PAUSE"        => Some(IpcCommand::Pause),
                        "RESUME"       => Some(IpcCommand::Resume),
                        "STOP"         => Some(IpcCommand::Stop),
                        "PLAY_CLAMP:1" => Some(IpcCommand::PlayClamp(true)),
                        "PLAY_CLAMP:0" => Some(IpcCommand::PlayClamp(false)),
                        "TOOL:SELECT"  => Some(IpcCommand::SetToolMode(ToolMode::Select)),
                        "TOOL:MOVE"    => Some(IpcCommand::SetToolMode(ToolMode::Move)),
                        "TOOL:ROTATE"  => Some(IpcCommand::SetToolMode(ToolMode::Rotate)),
                        "TOOL:SCALE"   => Some(IpcCommand::SetToolMode(ToolMode::Scale)),
                        "UNDO"         => Some(IpcCommand::Undo),
                        "REDO"         => Some(IpcCommand::Redo),
                        s if s.starts_with("SELECT:") => {
                            s["SELECT:".len()..].parse::<u32>().ok()
                                .map(IpcCommand::Select)
                        }
                        s if s.starts_with("DELETE_RECURSIVE:") => {
                            let ids: Vec<u32> = s["DELETE_RECURSIVE:".len()..]
                                .split(',').filter_map(|x| x.parse::<u32>().ok()).collect();
                            if !ids.is_empty() { Some(IpcCommand::DeleteRecursive(ids)) } else { None }
                        }
                        s if s.starts_with("DELETE:") => {
                            let ids: Vec<u32> = s["DELETE:".len()..]
                                .split(',').filter_map(|x| x.parse::<u32>().ok()).collect();
                            if !ids.is_empty() { Some(IpcCommand::Delete(ids)) } else { None }
                        }
                        s if s.starts_with("RENAME:") => {
                            let rest = &s["RENAME:".len()..];
                            // "id,name" — name 中にカンマを含む可能性があるため splitn(2)
                            let mut it = rest.splitn(2, ',');
                            if let (Some(idx_s), Some(name)) = (it.next(), it.next()) {
                                if let Some(idx) = idx_s.parse::<u32>().ok() {
                                    Some(IpcCommand::Rename { idx, name: name.to_string() })
                                } else { None }
                            } else { None }
                        }
                        s if s.starts_with("SAVE_SCENE:") => {
                            Some(IpcCommand::SaveScene(s["SAVE_SCENE:".len()..].to_string()))
                        }
                        s if s.starts_with("CREATE_GROUP:") => {
                            let rest = &s["CREATE_GROUP:".len()..];
                            let mut it = rest.splitn(2, ',');
                            if let (Some(p), Some(name)) = (it.next(), it.next()) {
                                let parent = if p == "-1" { None } else { p.parse::<u32>().ok() };
                                Some(IpcCommand::CreateGroup { name: name.to_string(), parent })
                            } else { None }
                        }
                        // フォーマット: "CREATE_GROUP_WITH_CHILDREN:{parentId}|{name}|{childId1},{childId2},..."
                        s if s.starts_with("CREATE_GROUP_WITH_CHILDREN:") => {
                            let rest = &s["CREATE_GROUP_WITH_CHILDREN:".len()..];
                            let mut it = rest.splitn(3, '|');
                            if let (Some(p), Some(name), Some(kids)) = (it.next(), it.next(), it.next()) {
                                let parent = if p == "-1" { None } else { p.parse::<u32>().ok() };
                                let children: Vec<u32> = kids.split(',')
                                    .filter_map(|x| x.parse::<u32>().ok())
                                    .collect();
                                Some(IpcCommand::CreateGroupWithChildren {
                                    name: name.to_string(), parent, children,
                                })
                            } else { None }
                        }
                        s if s.starts_with("SELECT_MULTI:") => {
                            let ids: Vec<u32> = s["SELECT_MULTI:".len()..]
                                .split(',')
                                .filter_map(|x| x.parse::<u32>().ok())
                                .collect();
                            if !ids.is_empty() { Some(IpcCommand::SelectMulti(ids)) } else { None }
                        }
                        "COPY"  => Some(IpcCommand::Copy),
                        "PASTE" => Some(IpcCommand::Paste),
                        s if s.starts_with("REPARENT:") => {
                            let rest = &s["REPARENT:".len()..];
                            let mut it = rest.splitn(2, ',');
                            if let (Some(c), Some(p)) = (it.next(), it.next()) {
                                if let Some(child) = c.parse::<u32>().ok() {
                                    let new_parent = if p == "-1" { None }
                                        else { p.parse::<u32>().ok() };
                                    Some(IpcCommand::Reparent { child, new_parent })
                                } else { None }
                            } else { None }
                        }
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
