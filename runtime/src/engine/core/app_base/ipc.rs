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
    /// アクターデータ要求（インスタンスインデックス）
    GetActorData(u32),
    /// トランスフォーム設定（位置・ZYX オイラー角(度)・スケール）
    SetTransform { id: u32, px: f32, py: f32, pz: f32, ex: f32, ey: f32, ez: f32, sx: f32, sy: f32, sz: f32 },
    /// デバッグカメラ画角設定（度）
    SetCameraFov(f32),
    /// デバッグカメラ描画距離（far clip）設定
    SetCameraFar(f32),
    /// グリッド描画オンオフ
    SetShowGrid(bool),
    /// 軸ギズモ表示オンオフ
    SetShowAxisGizmo(bool),
    /// アクターを指定パスへ保存（アクター編集モードのアクティブ世界線）
    SaveActor(String),
    /// インスペクターフィールドドラッグ開始（Undo 単一化のため事前状態を保存）
    BeginTransformDrag { is_actor: bool, target_id: u32 },
    /// インスペクターフィールドドラッグ終了（1 undo コマンドとして記録）
    EndTransformDrag,
    /// シーンファイルのロード
    LoadScene(String),
    /// デバッグカメラ状態要求
    GetCamState,
    /// デバッグカメラ位置・回転設定（yaw/pitch は度）
    SetCameraTransform { px: f32, py: f32, pz: f32, yaw: f32, pitch: f32 },
    /// デバッグカメラ移動速度設定
    SetCameraSpeed(f32),
    /// アクターファイルを指定世界線で開く（world_line,path の順でカンマ区切り）
    OpenActor { path: String, world_line: u32 },
    /// アクティブ世界線を切り替える（0=通常シーン）
    SetActiveWorldLine(u32),
    /// 指定世界線のアクターをシーンから除去する
    RemoveWorldLine(u32),
    /// アクターにコンポーネントを追加する
    /// フォーマット: ADD_COMPONENT:{actor_dfs_id},{type},{name},{args}
    AddComponent { actor_dfs_id: u32, component_type: String, slot_name: String, args: String },
    /// アクターのコンポーネント一覧を要求する
    GetActorComponents(u32),
    /// 子アクター（3D）を追加する (world_line, parent_dfs_id=None はルート)
    AddActor { world_line: u32, parent_dfs_id: Option<u32> },
    /// 子アクター（2D）を追加する (world_line, parent_dfs_id=None はルート)
    AddActor2D { world_line: u32, parent_dfs_id: Option<u32> },
    /// アクターを削除する
    RemoveActor(u32),
    /// アクターをリネームする
    RenameActor { dfs_id: u32, name: String },
    /// コンポーネントスロットを削除する
    RemoveComponentSlot { actor_dfs_id: u32, slot_idx: u32 },
    /// コンポーネントスロットをリネームする
    RenameComponentSlot { actor_dfs_id: u32, slot_idx: u32, name: String },
    /// 3D アクターのトランスフォームを設定する
    SetActorTransform { dfs_id: u32, px: f32, py: f32, pz: f32, ex: f32, ey: f32, ez: f32, sx: f32, sy: f32, sz: f32 },
    /// 2D アクターの CanvasTransform を設定する
    /// フォーマット: SET_CANVAS_TRANSFORM:{dfs_id},{px},{py},{rotation},{sx},{sy},{pivot_x},{pivot_y}
    SetCanvasTransform { dfs_id: u32, px: f32, py: f32, rotation: f32, sx: f32, sy: f32, pivot_x: f32, pivot_y: f32 },
    /// CanvasComponent のサイズを設定する
    /// フォーマット: SET_CANVAS_SIZE:{actor_dfs_id},{slot_idx},{width},{height}
    SetCanvasSize { actor_dfs_id: u32, slot_idx: u32, width: f32, height: f32 },
    /// ModelComponent のモデルパスを後から設定する
    /// フォーマット: SET_MODEL_PATH:{actor_dfs_id},{slot_idx},{path}
    SetModelPath { actor_dfs_id: u32, slot_idx: u32, path: String },
    /// コンポーネントスロットを複製する
    /// フォーマット: DUPLICATE_COMPONENT:{actor_dfs_id},{slot_idx}
    DuplicateComponent { actor_dfs_id: u32, slot_idx: u32 },
    /// .actor ファイルをビューポートにドラッグ&ドロップした
    /// フォーマット: DROP_ACTOR:{path},{screen_x},{screen_y}
    DropActor { path: String, screen_x: u32, screen_y: u32 },
    /// ドラッグ中カーソル位置ホバー通知（配置プレビュー球体表示用）
    /// フォーマット: DRAG_HOVER:{viewport_x},{viewport_y}
    DragHover { x: u32, y: u32 },
    /// ドラッグ離脱通知（プレビュー球体を消す）
    DragHoverEnd,
    /// SpriteComponent のテクスチャパスを設定する
    /// フォーマット: SET_SPRITE_PATH:{actor_dfs_id},{slot_idx},{path}
    SetSpritePath { actor_dfs_id: u32, slot_idx: u32, path: String },
    /// SpriteComponent の RGBA カラーを設定する（正規化値 0.0〜1.0）
    /// フォーマット: SET_SPRITE_COLOR:{actor_dfs_id},{slot_idx},{r},{g},{b},{a}
    SetSpriteColor { actor_dfs_id: u32, slot_idx: u32, r: f32, g: f32, b: f32, a: f32 },
    /// SpriteComponent の幅・高さをキャンバスユニットで設定する
    /// フォーマット: SET_SPRITE_SIZE:{actor_dfs_id},{slot_idx},{width},{height}
    SetSpriteSize { actor_dfs_id: u32, slot_idx: u32, width: f32, height: f32 },
    /// CanvasTransform の anchor を設定する（正規化値 0.0〜1.0）
    /// フォーマット: SET_CANVAS_ANCHOR:{actor_dfs_id},{anchor_x},{anchor_y}
    SetCanvasAnchor { actor_dfs_id: u32, ax: f32, ay: f32 },
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
                        s if s.starts_with("SAVE_ACTOR:") => {
                            Some(IpcCommand::SaveActor(s["SAVE_ACTOR:".len()..].to_string()))
                        }
                        s if s.starts_with("BEGIN_TRANSFORM_DRAG:") => {
                            let rest = &s["BEGIN_TRANSFORM_DRAG:".len()..];
                            let mut it = rest.splitn(2, ',');
                            if let (Some(t), Some(id_s)) = (it.next(), it.next()) {
                                if let (Ok(type_n), Ok(id)) = (t.parse::<u32>(), id_s.parse::<u32>()) {
                                    Some(IpcCommand::BeginTransformDrag { is_actor: type_n != 0, target_id: id })
                                } else { None }
                            } else { None }
                        }
                        "END_TRANSFORM_DRAG" => Some(IpcCommand::EndTransformDrag),
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
                        s if s.starts_with("GET_ACTOR:") => {
                            s["GET_ACTOR:".len()..].parse::<u32>().ok()
                                .map(IpcCommand::GetActorData)
                        }
                        s if s.starts_with("SET_TRANSFORM:") => {
                            let rest = &s["SET_TRANSFORM:".len()..];
                            let parts: Vec<&str> = rest.split(',').collect();
                            if parts.len() == 10 {
                                if let Ok(id) = parts[0].parse::<u32>() {
                                    let floats: Vec<f32> = parts[1..].iter()
                                        .filter_map(|x| x.parse::<f32>().ok())
                                        .collect();
                                    if floats.len() == 9 {
                                        Some(IpcCommand::SetTransform {
                                            id,
                                            px: floats[0], py: floats[1], pz: floats[2],
                                            ex: floats[3], ey: floats[4], ez: floats[5],
                                            sx: floats[6], sy: floats[7], sz: floats[8],
                                        })
                                    } else { None }
                                } else { None }
                            } else { None }
                        }
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
                        s if s.starts_with("VIEWPORT_FOV:") => {
                            s["VIEWPORT_FOV:".len()..].parse::<f32>().ok()
                                .map(IpcCommand::SetCameraFov)
                        }
                        s if s.starts_with("VIEWPORT_FAR:") => {
                            s["VIEWPORT_FAR:".len()..].parse::<f32>().ok()
                                .map(IpcCommand::SetCameraFar)
                        }
                        "SHOW_GRID:1"        => Some(IpcCommand::SetShowGrid(true)),
                        "SHOW_GRID:0"        => Some(IpcCommand::SetShowGrid(false)),
                        "SHOW_AXIS_GIZMO:1"  => Some(IpcCommand::SetShowAxisGizmo(true)),
                        "SHOW_AXIS_GIZMO:0"  => Some(IpcCommand::SetShowAxisGizmo(false)),
                        s if s.starts_with("LOAD_SCENE:") => {
                            Some(IpcCommand::LoadScene(s["LOAD_SCENE:".len()..].to_string()))
                        }
                        "GET_CAM_STATE" => Some(IpcCommand::GetCamState),
                        s if s.starts_with("CAM_TRANSFORM:") => {
                            let rest = &s["CAM_TRANSFORM:".len()..];
                            let parts: Vec<&str> = rest.split(',').collect();
                            if parts.len() == 5 {
                                let fs: Vec<f32> = parts.iter()
                                    .filter_map(|x| x.parse::<f32>().ok())
                                    .collect();
                                if fs.len() == 5 {
                                    Some(IpcCommand::SetCameraTransform {
                                        px: fs[0], py: fs[1], pz: fs[2],
                                        yaw: fs[3], pitch: fs[4],
                                    })
                                } else { None }
                            } else { None }
                        }
                        s if s.starts_with("CAM_SPEED:") => {
                            s["CAM_SPEED:".len()..].parse::<f32>().ok()
                                .map(IpcCommand::SetCameraSpeed)
                        }
                        s if s.starts_with("OPEN_ACTOR:") => {
                            // フォーマット: "OPEN_ACTOR:<world_line>,<path>"
                            let rest = &s["OPEN_ACTOR:".len()..];
                            let mut it = rest.splitn(2, ',');
                            if let (Some(wl_s), Some(path)) = (it.next(), it.next()) {
                                wl_s.parse::<u32>().ok()
                                    .map(|wl| IpcCommand::OpenActor { path: path.to_string(), world_line: wl })
                            } else { None }
                        }
                        s if s.starts_with("SET_ACTIVE_WORLD_LINE:") => {
                            s["SET_ACTIVE_WORLD_LINE:".len()..].parse::<u32>().ok()
                                .map(IpcCommand::SetActiveWorldLine)
                        }
                        s if s.starts_with("REMOVE_WORLD_LINE:") => {
                            s["REMOVE_WORLD_LINE:".len()..].parse::<u32>().ok()
                                .map(IpcCommand::RemoveWorldLine)
                        }
                        s if s.starts_with("ADD_COMPONENT:") => {
                            // ADD_COMPONENT:{dfs_id},{type},{name},{args}
                            let rest = &s["ADD_COMPONENT:".len()..];
                            let mut it = rest.splitn(4, ',');
                            if let (Some(id_s), Some(comp_type), Some(name), Some(args)) =
                                (it.next(), it.next(), it.next(), it.next())
                            {
                                id_s.parse::<u32>().ok().map(|actor_dfs_id| IpcCommand::AddComponent {
                                    actor_dfs_id,
                                    component_type: comp_type.to_string(),
                                    slot_name:      name.to_string(),
                                    args:           args.to_string(),
                                })
                            } else { None }
                        }
                        s if s.starts_with("GET_ACTOR_COMPONENTS:") => {
                            s["GET_ACTOR_COMPONENTS:".len()..].parse::<u32>().ok()
                                .map(IpcCommand::GetActorComponents)
                        }
                        s if s.starts_with("ADD_ACTOR:") => {
                            // ADD_ACTOR:{world_line},{parent_dfs_id} (-1 = root)
                            let rest = &s["ADD_ACTOR:".len()..];
                            let mut it = rest.splitn(2, ',');
                            if let (Some(wl_s), Some(p_s)) = (it.next(), it.next()) {
                                if let Ok(wl) = wl_s.parse::<u32>() {
                                    let parent = if p_s == "-1" { None }
                                        else { p_s.parse::<u32>().ok() };
                                    Some(IpcCommand::AddActor { world_line: wl, parent_dfs_id: parent })
                                } else { None }
                            } else { None }
                        }
                        s if s.starts_with("ADD_ACTOR_2D:") => {
                            // ADD_ACTOR_2D:{world_line},{parent_dfs_id} (-1 = root)
                            let rest = &s["ADD_ACTOR_2D:".len()..];
                            let mut it = rest.splitn(2, ',');
                            if let (Some(wl_s), Some(p_s)) = (it.next(), it.next()) {
                                if let Ok(wl) = wl_s.parse::<u32>() {
                                    let parent = if p_s == "-1" { None }
                                        else { p_s.parse::<u32>().ok() };
                                    Some(IpcCommand::AddActor2D { world_line: wl, parent_dfs_id: parent })
                                } else { None }
                            } else { None }
                        }
                        s if s.starts_with("REMOVE_ACTOR:") => {
                            s["REMOVE_ACTOR:".len()..].parse::<u32>().ok()
                                .map(IpcCommand::RemoveActor)
                        }
                        s if s.starts_with("RENAME_ACTOR:") => {
                            let rest = &s["RENAME_ACTOR:".len()..];
                            let mut it = rest.splitn(2, ',');
                            if let (Some(id_s), Some(name)) = (it.next(), it.next()) {
                                id_s.parse::<u32>().ok().map(|dfs_id| IpcCommand::RenameActor {
                                    dfs_id, name: name.to_string(),
                                })
                            } else { None }
                        }
                        s if s.starts_with("REMOVE_COMPONENT:") => {
                            let rest = &s["REMOVE_COMPONENT:".len()..];
                            let mut it = rest.splitn(2, ',');
                            if let (Some(id_s), Some(slot_s)) = (it.next(), it.next()) {
                                if let (Ok(a), Ok(sl)) = (id_s.parse::<u32>(), slot_s.parse::<u32>()) {
                                    Some(IpcCommand::RemoveComponentSlot { actor_dfs_id: a, slot_idx: sl })
                                } else { None }
                            } else { None }
                        }
                        s if s.starts_with("RENAME_COMPONENT:") => {
                            let rest = &s["RENAME_COMPONENT:".len()..];
                            let mut it = rest.splitn(3, ',');
                            if let (Some(id_s), Some(sl_s), Some(name)) = (it.next(), it.next(), it.next()) {
                                if let (Ok(a), Ok(sl)) = (id_s.parse::<u32>(), sl_s.parse::<u32>()) {
                                    Some(IpcCommand::RenameComponentSlot {
                                        actor_dfs_id: a, slot_idx: sl, name: name.to_string(),
                                    })
                                } else { None }
                            } else { None }
                        }
                        s if s.starts_with("SET_ACTOR_TRANSFORM:") => {
                            let rest = &s["SET_ACTOR_TRANSFORM:".len()..];
                            let parts: Vec<&str> = rest.split(',').collect();
                            if parts.len() == 10 {
                                if let Ok(dfs_id) = parts[0].parse::<u32>() {
                                    let fs: Vec<f32> = parts[1..].iter()
                                        .filter_map(|x| x.parse::<f32>().ok())
                                        .collect();
                                    if fs.len() == 9 {
                                        Some(IpcCommand::SetActorTransform {
                                            dfs_id,
                                            px: fs[0], py: fs[1], pz: fs[2],
                                            ex: fs[3], ey: fs[4], ez: fs[5],
                                            sx: fs[6], sy: fs[7], sz: fs[8],
                                        })
                                    } else { None }
                                } else { None }
                            } else { None }
                        }
                        s if s.starts_with("SET_CANVAS_TRANSFORM:") => {
                            // フォーマット: SET_CANVAS_TRANSFORM:{dfs_id},{px},{py},{rotation},{sx},{sy},{pivot_x},{pivot_y}
                            let rest = &s["SET_CANVAS_TRANSFORM:".len()..];
                            let parts: Vec<&str> = rest.split(',').collect();
                            if parts.len() == 8 {
                                if let Ok(dfs_id) = parts[0].parse::<u32>() {
                                    let fs: Vec<f32> = parts[1..].iter()
                                        .filter_map(|x| x.parse::<f32>().ok())
                                        .collect();
                                    if fs.len() == 7 {
                                        Some(IpcCommand::SetCanvasTransform {
                                            dfs_id,
                                            px: fs[0], py: fs[1],
                                            rotation: fs[2],
                                            sx: fs[3], sy: fs[4],
                                            pivot_x: fs[5], pivot_y: fs[6],
                                        })
                                    } else { None }
                                } else { None }
                            } else { None }
                        }
                        s if s.starts_with("SET_CANVAS_SIZE:") => {
                            // フォーマット: SET_CANVAS_SIZE:{actor_dfs_id},{slot_idx},{width},{height}
                            let rest = &s["SET_CANVAS_SIZE:".len()..];
                            let parts: Vec<&str> = rest.split(',').collect();
                            if parts.len() == 4 {
                                if let (Ok(a), Ok(sl)) = (parts[0].parse::<u32>(), parts[1].parse::<u32>()) {
                                    let fs: Vec<f32> = parts[2..].iter()
                                        .filter_map(|x| x.parse::<f32>().ok())
                                        .collect();
                                    if fs.len() == 2 {
                                        Some(IpcCommand::SetCanvasSize {
                                            actor_dfs_id: a, slot_idx: sl,
                                            width: fs[0], height: fs[1],
                                        })
                                    } else { None }
                                } else { None }
                            } else { None }
                        }
                        s if s.starts_with("SET_MODEL_PATH:") => {
                            let rest = &s["SET_MODEL_PATH:".len()..];
                            let mut parts = rest.splitn(3, ',');
                            if let (Some(id_s), Some(sl_s), Some(path)) =
                                (parts.next(), parts.next(), parts.next())
                            {
                                if let (Ok(a), Ok(sl)) = (id_s.parse::<u32>(), sl_s.parse::<u32>()) {
                                    Some(IpcCommand::SetModelPath {
                                        actor_dfs_id: a, slot_idx: sl, path: path.to_string(),
                                    })
                                } else { None }
                            } else { None }
                        }
                        s if s.starts_with("DUPLICATE_COMPONENT:") => {
                            let rest = &s["DUPLICATE_COMPONENT:".len()..];
                            let mut it = rest.splitn(2, ',');
                            if let (Some(id_s), Some(sl_s)) = (it.next(), it.next()) {
                                if let (Ok(a), Ok(sl)) = (id_s.parse::<u32>(), sl_s.parse::<u32>()) {
                                    Some(IpcCommand::DuplicateComponent { actor_dfs_id: a, slot_idx: sl })
                                } else { None }
                            } else { None }
                        }
                        s if s.starts_with("DROP_ACTOR:") => {
                            // フォーマット: DROP_ACTOR:{path},{screen_x},{screen_y}
                            // path 中にカンマが含まれる可能性があるため末尾から数値を取り出す
                            let rest = &s["DROP_ACTOR:".len()..];
                            let parts: Vec<&str> = rest.rsplitn(3, ',').collect();
                            if parts.len() == 3 {
                                if let (Ok(sy), Ok(sx)) =
                                    (parts[0].parse::<u32>(), parts[1].parse::<u32>())
                                {
                                    Some(IpcCommand::DropActor {
                                        path: parts[2].to_string(),
                                        screen_x: sx,
                                        screen_y: sy,
                                    })
                                } else { None }
                            } else { None }
                        }
                        s if s.starts_with("DRAG_HOVER:") => {
                            // フォーマット: DRAG_HOVER:{viewport_x},{viewport_y}
                            let rest = &s["DRAG_HOVER:".len()..];
                            let mut parts = rest.split(',');
                            match (parts.next(), parts.next()) {
                                (Some(xs), Some(ys)) => {
                                    if let (Ok(x), Ok(y)) = (xs.parse::<u32>(), ys.parse::<u32>()) {
                                        Some(IpcCommand::DragHover { x, y })
                                    } else { None }
                                }
                                _ => None,
                            }
                        }
                        s if s == "DRAG_HOVER_END" => Some(IpcCommand::DragHoverEnd),
                        s if s.starts_with("SET_SPRITE_PATH:") => {
                            // フォーマット: SET_SPRITE_PATH:{actor_dfs_id},{slot_idx},{path}
                            // path 中にカンマが含まれる可能性があるため splitn(3) を使用
                            let rest = &s["SET_SPRITE_PATH:".len()..];
                            let mut parts = rest.splitn(3, ',');
                            if let (Some(id_s), Some(sl_s), Some(path)) =
                                (parts.next(), parts.next(), parts.next())
                            {
                                if let (Ok(a), Ok(sl)) = (id_s.parse::<u32>(), sl_s.parse::<u32>()) {
                                    Some(IpcCommand::SetSpritePath {
                                        actor_dfs_id: a, slot_idx: sl, path: path.to_string(),
                                    })
                                } else { None }
                            } else { None }
                        }
                        s if s.starts_with("SET_SPRITE_COLOR:") => {
                            // フォーマット: SET_SPRITE_COLOR:{actor_dfs_id},{slot_idx},{r},{g},{b},{a}
                            let rest = &s["SET_SPRITE_COLOR:".len()..];
                            let parts: Vec<&str> = rest.split(',').collect();
                            if parts.len() == 6 {
                                if let (Ok(a), Ok(sl)) = (parts[0].parse::<u32>(), parts[1].parse::<u32>()) {
                                    let fs: Vec<f32> = parts[2..].iter()
                                        .filter_map(|x| x.parse::<f32>().ok())
                                        .collect();
                                    if fs.len() == 4 {
                                        Some(IpcCommand::SetSpriteColor {
                                            actor_dfs_id: a, slot_idx: sl,
                                            r: fs[0], g: fs[1], b: fs[2], a: fs[3],
                                        })
                                    } else { None }
                                } else { None }
                            } else { None }
                        }
                        s if s.starts_with("SET_SPRITE_SIZE:") => {
                            // フォーマット: SET_SPRITE_SIZE:{actor_dfs_id},{slot_idx},{width},{height}
                            let rest = &s["SET_SPRITE_SIZE:".len()..];
                            let parts: Vec<&str> = rest.split(',').collect();
                            if parts.len() == 4 {
                                if let (Ok(a), Ok(sl)) = (parts[0].parse::<u32>(), parts[1].parse::<u32>()) {
                                    let fs: Vec<f32> = parts[2..].iter()
                                        .filter_map(|x| x.parse::<f32>().ok())
                                        .collect();
                                    if fs.len() == 2 {
                                        Some(IpcCommand::SetSpriteSize {
                                            actor_dfs_id: a, slot_idx: sl,
                                            width: fs[0], height: fs[1],
                                        })
                                    } else { None }
                                } else { None }
                            } else { None }
                        }
                        s if s.starts_with("SET_CANVAS_ANCHOR:") => {
                            // フォーマット: SET_CANVAS_ANCHOR:{actor_dfs_id},{anchor_x},{anchor_y}
                            let rest = &s["SET_CANVAS_ANCHOR:".len()..];
                            let parts: Vec<&str> = rest.split(',').collect();
                            if parts.len() == 3 {
                                if let (Ok(id), Ok(ax), Ok(ay)) = (
                                    parts[0].parse::<u32>(),
                                    parts[1].parse::<f32>(),
                                    parts[2].parse::<f32>(),
                                ) {
                                    Some(IpcCommand::SetCanvasAnchor { actor_dfs_id: id, ax, ay })
                                } else { None }
                            } else { None }
                        }
                        _                    => None,
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
