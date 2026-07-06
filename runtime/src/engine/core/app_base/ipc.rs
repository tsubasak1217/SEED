use std::fs::OpenOptions;
use std::io::{BufRead, BufReader, Write};
use std::sync::{mpsc, Arc, Mutex};
use std::thread;
use std::time::Duration;

// ============================================================
//  モジュール定数
// ============================================================

/// パイプ接続を試みる最大リトライ回数。
const PIPE_CONNECT_RETRIES: u32 = 20;

/// リトライ間隔 (ミリ秒)。エディタ起動後のパイプ準備待ち時間に相当する。
const PIPE_CONNECT_RETRY_MS: u64 = 100;

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
    /// デバッグカメラ位置・Euler XYZ 回転設定（度、YXZ 合成順）
    /// フォーマット: CAM_TRANSFORM:{px},{py},{pz},{euler_x},{euler_y},{euler_z}
    SetCameraTransform { px: f32, py: f32, pz: f32, euler_x: f32, euler_y: f32, euler_z: f32 },
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
    /// 指定アクターの子として 3D アクターを追加する（world_line は親から自動取得）
    /// フォーマット: ADD_ACTOR_CHILD:{parent_dfs_id}
    AddActorChild { parent_dfs_id: u32 },
    /// 指定アクターの子として 2D アクターを追加する（world_line は親から自動取得）
    /// フォーマット: ADD_ACTOR_2D_CHILD:{parent_dfs_id}
    AddActor2dChild { parent_dfs_id: u32 },
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
    /// ScriptComponent の [SerializeField] フィールド値を設定する
    /// フォーマット: SET_SCRIPT_FIELD:{actor_dfs_id},{slot_idx},{field_name},{value}
    SetScriptField { actor_dfs_id: u32, slot_idx: u32, field: String, value: String },
    /// ユーザースクリプトを再コンパイルし、全 ScriptComponent を再生成する（ホットリロード）
    /// フォーマット: RELOAD_SCRIPTS
    ReloadScripts,
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
    /// AudioComponent のフィールドを更新する（key: path/volume/loop/play_on_start/spatial/min_distance/max_distance/pan）
    SetAudioField { actor_dfs_id: u32, slot_idx: u32, key: String, value: String },
    /// CanvasTransform の anchor を設定する（正規化値 0.0〜1.0）
    /// フォーマット: SET_CANVAS_ANCHOR:{actor_dfs_id},{anchor_x},{anchor_y}
    SetCanvasAnchor { actor_dfs_id: u32, ax: f32, ay: f32 },
    /// CanvasComponent のスケールモードを設定する
    /// フォーマット: SET_CANVAS_SCALE_MODE:{actor_dfs_id},{slot_idx},{scale_transform},{scale_size}
    /// scale_transform / scale_size は "0" または "1"
    SetCanvasScaleMode { actor_dfs_id: u32, slot_idx: u32, scale_transform: bool, scale_size: bool },
    /// キャンバスをスクリーンスペースオーバーレイで表示するかを切り替える
    /// false（デフォルト）= ワールドスペース、true = スクリーンスペースオーバーレイ
    /// フォーマット: CANVAS_SS_OVERLAY:0/1
    SetCanvasScreenSpaceOverlay(bool),
    /// ルートキャンバスの画面サイズ自動スケールを設定する
    /// フォーマット: SET_CANVAS_AUTO_SCALE:{actor_dfs_id},{slot_idx},{value}
    SetCanvasAutoScale { actor_dfs_id: u32, slot_idx: u32, auto_scale: bool },
    /// CanvasComponent のアスペクト比維持設定を更新する
    /// フォーマット: SET_CANVAS_ASPECT_RATIO:{actor_dfs_id},{slot_idx},{keep:0|1},{axis:width|height}
    SetCanvasAspectRatio { actor_dfs_id: u32, slot_idx: u32, keep: bool, axis: String },
    /// CanvasComponent の重力方向モードを設定する
    /// フォーマット: SET_CANVAS_GRAVITY_MODE:{actor_dfs_id},{slot_idx},{mode:0|1}
    /// mode: 0=ScreenDown, 1=CanvasDown
    SetCanvasGravityMode { actor_dfs_id: u32, slot_idx: u32, mode: u8 },
    /// 3D キャンバスのピボットを設定する（Actor3D アタッチ時のみ有効）
    /// フォーマット: SET_CANVAS_3D_PIVOT:{actor_dfs_id},{slot_idx},{pivot_x},{pivot_y}
    /// pivot_x / pivot_y は正規化値 [0,1]。(0,0)=左上, (0.5,0.5)=中央, (1,1)=右下。
    SetCanvas3dPivot { actor_dfs_id: u32, slot_idx: u32, pivot_x: f32, pivot_y: f32 },
    /// Collider2dComponent のアスペクト比維持設定を更新する
    /// フォーマット: SET_COLLIDER2D_ASPECT_RATIO:{actor_dfs_id},{slot_idx},{keep:0|1},{axis:width|height}
    SetCollider2dAspectRatio { actor_dfs_id: u32, slot_idx: u32, keep: bool, axis: String },
    /// キャンバスのビューポート参照をウィンドウに設定する（Camera 参照を解除）
    /// フォーマット: SET_CANVAS_VIEWPORT_REF_WINDOW:{actor_dfs_id},{slot_idx}
    SetCanvasViewportRefWindow { actor_dfs_id: u32, slot_idx: u32 },
    /// キャンバスのビューポート参照をカメラに設定する
    /// フォーマット: SET_CANVAS_VIEWPORT_REF_CAMERA:{actor_dfs_id},{slot_idx},{actor_name},{slot_name}
    /// actor_name / slot_name はカンマを含まない前提
    SetCanvasViewportRefCamera { actor_dfs_id: u32, slot_idx: u32, actor_name: String, slot_name: String },
    /// InputMapComponent のアセットパスを設定する
    /// フォーマット: SET_INPUTMAP_PATH:{actor_dfs_id},{slot_idx},{path}
    SetInputMapPath { actor_dfs_id: u32, slot_idx: u32, path: String },
    /// CameraComponent の FOV（視野角・度）を設定する
    /// フォーマット: SET_CAMERA_FOV:{actor_dfs_id},{slot_idx},{value}
    SetCameraComponentFov { actor_dfs_id: u32, slot_idx: u32, value: f32 },
    /// CameraComponent の near clip を設定する
    /// フォーマット: SET_CAMERA_NEAR:{actor_dfs_id},{slot_idx},{value}
    SetCameraComponentNear { actor_dfs_id: u32, slot_idx: u32, value: f32 },
    /// CameraComponent の far clip を設定する
    /// フォーマット: SET_CAMERA_FAR:{actor_dfs_id},{slot_idx},{value}
    SetCameraComponentFar { actor_dfs_id: u32, slot_idx: u32, value: f32 },
    /// CameraComponent の is_main フラグを設定する
    /// フォーマット: SET_CAMERA_MAIN:{actor_dfs_id},{slot_idx},{0|1}
    SetCameraComponentMain { actor_dfs_id: u32, slot_idx: u32, is_main: bool },
    /// CameraComponent のクリアカラーを設定する（正規化値 0.0〜1.0）
    /// フォーマット: SET_CAMERA_CLEAR_COLOR:{actor_dfs_id},{slot_idx},{r},{g},{b},{a}
    SetCameraComponentClearColor { actor_dfs_id: u32, slot_idx: u32, r: f32, g: f32, b: f32, a: f32 },
    /// CameraComponent のスケーリングモードを設定する
    /// フォーマット: SET_CAMERA_SCALING_MODE:{actor_dfs_id},{slot_idx},{mode}
    SetCameraComponentScalingMode { actor_dfs_id: u32, slot_idx: u32, mode: String },
    /// CameraComponent のターゲット解像度を設定する
    /// フォーマット: SET_CAMERA_TARGET_SIZE:{actor_dfs_id},{slot_idx},{width},{height}
    SetCameraComponentTargetSize { actor_dfs_id: u32, slot_idx: u32, width: u32, height: u32 },
    /// CameraComponent の帯カラーを設定する（LetterBox / PillarBox 時の帯色、正規化値 0.0〜1.0）
    /// フォーマット: SET_CAMERA_BAR_COLOR:{actor_dfs_id},{slot_idx},{r},{g},{b},{a}
    SetCameraBarColor { actor_dfs_id: u32, slot_idx: u32, r: f32, g: f32, b: f32, a: f32 },
    /// ColliderComponent のデータ全体（リジッドボディ設定を含む）を JSON で設定する
    /// フォーマット: SET_COLLIDER_DATA:{actor_dfs_id},{slot_idx},{json}
    /// json は ColliderComponentData の serde_json シリアライズ結果（カンマ含む）
    SetColliderData { actor_dfs_id: u32, slot_idx: u32, json: String },
    /// Collider2dComponent のデータ全体（リジッドボディ設定を含む）を JSON で設定する
    /// フォーマット: SET_COLLIDER2D_DATA:{actor_dfs_id},{slot_idx},{json}
    /// json は Collider2dComponentData の serde_json シリアライズ結果（カンマ含む）
    SetCollider2dData { actor_dfs_id: u32, slot_idx: u32, json: String },
    /// 編集時の 2D 物理シミュレーション設定。
    /// enabled=true かつ with_rigidbody=false : 重力なし・全ボディを kinematic として衝突検出のみ
    /// enabled=true かつ with_rigidbody=true  : 重力・ダイナミクスも有効な完全シミュレーション
    /// フォーマット: SET_EDIT_PHYSICS_2D:{enabled},{with_rigidbody}  (0=off, 1=on)
    SetEditPhysics2d { enabled: bool, with_rigidbody: bool },
    /// プラグインコンポーネントのフィールド値を設定する
    /// フォーマット: SET_PLUGIN_FIELD:{actor_dfs_id},{slot_idx},{key},{value}
    /// ※ key と value はカンマを含む可能性があるため最後の区切りのみ使用
    SetPluginField { actor_dfs_id: u32, slot_idx: u32, key: String, value: String },
    /// ロード済みプラグイン一覧の要求
    /// フォーマット: GET_PLUGIN_LIST
    GetPluginList,

    // ── AI アシスタント用コマンド ─────────────────────────────────────────────

    /// シーン全体の情報を JSON で要求する
    /// フォーマット: GET_SCENE_INFO
    GetSceneInfo,
    /// ローカル AI 実行中にレンダリングを一時停止して GPU リソースを解放する
    /// フォーマット: PAUSE_RENDER
    PauseRender,
    /// ローカル AI 応答完了後にレンダリングを再開する
    /// フォーマット: RESUME_RENDER
    ResumeRender,
    /// AI が新しいアクターを追加する
    /// フォーマット: AI_ADD_ACTOR:{name},{x},{y},{z}
    AiAddActor { name: String, x: f32, y: f32, z: f32 },
    /// AI がアクターを削除する（DFS ID 指定）
    /// フォーマット: AI_REMOVE_ACTOR:{actor_dfs_id}
    AiRemoveActor { actor_dfs_id: u32 },
    /// AI がアクターを移動する（DFS ID + 位置）
    /// フォーマット: AI_MOVE_ACTOR:{actor_dfs_id},{x},{y},{z}
    AiMoveActor { actor_dfs_id: u32, x: f32, y: f32, z: f32 },
    /// AI がコンポーネントを追加する
    /// フォーマット: AI_ADD_COMPONENT:{actor_dfs_id},{component_type},{params_json}
    AiAddComponent { actor_dfs_id: u32, component_type: String, params_json: String },
    /// AI がコンポーネントのフィールド値を設定する
    /// フォーマット: AI_SET_VALUE:{actor_dfs_id},{slot_idx},{key},{value}
    AiSetValue { actor_dfs_id: u32, slot_idx: u32, key: String, value: String },

    /// シーン内のアクターをファイルへ書き出す。
    /// transform はルートのみ 0 にリセット、子は相対位置を維持。
    /// フォーマット: EXPORT_ACTOR:{dfs_id},{path}
    /// path はエディタの SaveFileDialog で選択された絶対ファイルパス
    ExportActor { dfs_id: u32, path: String },

    /// 編集時の物理シミュレーション設定。
    /// enabled=true かつ with_rigidbody=false : 重力なし・全ボディを kinematic として衝突検出のみ
    /// enabled=true かつ with_rigidbody=true  : 重力・ダイナミクスも有効な完全シミュレーション
    /// フォーマット: SET_EDIT_PHYSICS:{enabled},{with_rigidbody}  (0=off, 1=on)
    SetEditPhysics { enabled: bool, with_rigidbody: bool },

    /// 実行時コライダー描画設定。
    /// Play モードでもコライダーワイヤーフレームを描画する。
    /// フォーマット: SET_PLAY_COLLIDER_DRAW:{0|1}
    SetPlayColliderDraw(bool),

    // ─── 編集時物理タイムライン ─────────────────────────────────────────────
    /// 再生/停止トグル。
    /// フォーマット: EDIT_PHYSICS_PLAY_PAUSE
    EditPhysicsPlayPause,
    /// フレームを N ステップ進む（step>0）または戻す（step<0）。
    /// フォーマット: EDIT_PHYSICS_STEP:{step}  (例: +1, -1, +5)
    EditPhysicsStep { step: i32 },
    /// 指定フレームへシーク。
    /// フォーマット: EDIT_PHYSICS_SEEK:{frame_idx}
    EditPhysicsSeek { frame: usize },
    /// 現在フレームの状態をフレーム 0 として適用（履歴削除）。
    /// フォーマット: EDIT_PHYSICS_APPLY_FRAME
    EditPhysicsApplyFrame,

    /// 内蔵デバッガのアタッチ/デタッチに合わせてブレークポイント停止ガードを切り替える。
    /// アタッチ中（true）は、ブレークポイントで長時間停止した復帰フレームの
    /// 巨大 delta を丸めて ConstantUpdate の追いつき暴走を防ぐ。
    /// フォーマット: DBG_GUARD:{0|1}
    SetDebugGuard(bool),
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

// ============================================================
//  IPC パース共通ヘルパー
//
//  IPC コマンドのペイロードは「カンマ区切りの数値列」という共通フォーマットを持つ。
//  以下のヘルパーで重複パターンを集約し、各コマンド解析を 1〜2 行に簡略化する。
// ============================================================

/// `rest` から f32×N をカンマ区切りでパースして [f32; N] を返す（先頭 u32 なし）。
#[inline]
fn parse_nf<const N: usize>(rest: &str) -> Option<[f32; N]> {
    let parts: Vec<&str> = rest.split(',').collect();
    if parts.len() != N { return None; }
    let mut fs = [0.0f32; N];
    for (i, p) in parts.iter().enumerate() {
        fs[i] = p.trim().parse().ok()?;
    }
    Some(fs)
}

/// `rest` から `u32, f32×N` をカンマ区切りでパースして (id, [f32; N]) を返す。
#[inline]
fn parse1u_nf<const N: usize>(rest: &str) -> Option<(u32, [f32; N])> {
    let parts: Vec<&str> = rest.split(',').collect();
    if parts.len() != N + 1 { return None; }
    let id: u32 = parts[0].trim().parse().ok()?;
    let mut fs = [0.0f32; N];
    for (i, p) in parts[1..].iter().enumerate() {
        fs[i] = p.trim().parse().ok()?;
    }
    Some((id, fs))
}

/// `rest` から `u32, <tail>` をカンマ区切りでパースして (id, tail) を返す。
/// tail にカンマが含まれてもよい（ファイルパス等）。
#[inline]
fn parse1u_tail(rest: &str) -> Option<(u32, &str)> {
    let mut it = rest.splitn(2, ',');
    Some((it.next()?.trim().parse().ok()?, it.next()?))
}

/// `rest` から `u32, u32` をカンマ区切りでパースして (a, b) を返す。
#[inline]
fn parse2u(rest: &str) -> Option<(u32, u32)> {
    let mut it = rest.splitn(2, ',');
    Some((it.next()?.trim().parse().ok()?, it.next()?.trim().parse().ok()?))
}

/// `rest` から `u32, u32, <tail>` をカンマ区切りでパースして (a, b, tail) を返す。
/// tail にカンマが含まれてもよい（ファイルパス等）。
#[inline]
fn parse2u_tail(rest: &str) -> Option<(u32, u32, &str)> {
    let mut it = rest.splitn(3, ',');
    Some((
        it.next()?.trim().parse().ok()?,
        it.next()?.trim().parse().ok()?,
        it.next()?,
    ))
}

/// `rest` から `u32, u32, f32` をカンマ区切りでパースして (a, b, v) を返す。
#[inline]
fn parse2u1f(rest: &str) -> Option<(u32, u32, f32)> {
    let mut it = rest.split(',');
    Some((
        it.next()?.trim().parse().ok()?,
        it.next()?.trim().parse().ok()?,
        it.next()?.trim().parse().ok()?,
    ))
}

/// `rest` から `u32, u32, {0|1}` をカンマ区切りでパースして (a, b, bool) を返す。
#[inline]
fn parse2u1b(rest: &str) -> Option<(u32, u32, bool)> {
    let mut it = rest.split(',');
    Some((
        it.next()?.trim().parse().ok()?,
        it.next()?.trim().parse().ok()?,
        it.next()?.trim() == "1",
    ))
}

/// `rest` から `u32, u32, {0|1}, {0|1}` をカンマ区切りでパースして (a, b, bool, bool) を返す。
#[inline]
fn parse2u2b(rest: &str) -> Option<(u32, u32, bool, bool)> {
    let mut it = rest.split(',');
    Some((
        it.next()?.trim().parse().ok()?,
        it.next()?.trim().parse().ok()?,
        it.next()?.trim() == "1",
        it.next()?.trim() == "1",
    ))
}

/// `rest` から `u32, u32, f32×N` をカンマ区切りでパースして (a, b, [f32; N]) を返す。
#[inline]
fn parse2u_nf<const N: usize>(rest: &str) -> Option<(u32, u32, [f32; N])> {
    let parts: Vec<&str> = rest.split(',').collect();
    if parts.len() != N + 2 { return None; }
    let a: u32 = parts[0].trim().parse().ok()?;
    let b: u32 = parts[1].trim().parse().ok()?;
    let mut fs = [0.0f32; N];
    for (i, p) in parts[2..].iter().enumerate() {
        fs[i] = p.trim().parse().ok()?;
    }
    Some((a, b, fs))
}

// ============================================================
//  内部ヘルパー
// ============================================================

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
                        "DBG_GUARD:1"  => Some(IpcCommand::SetDebugGuard(true)),
                        "DBG_GUARD:0"  => Some(IpcCommand::SetDebugGuard(false)),
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
                            // "id,name" — name 中にカンマを含む可能性があるため parse1u_tail を使用
                            parse1u_tail(&s["RENAME:".len()..])
                                .map(|(idx, name)| IpcCommand::Rename { idx, name: name.to_string() })
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
                            // フォーマット: SET_TRANSFORM:{id},{px},{py},{pz},{ex},{ey},{ez},{sx},{sy},{sz}
                            parse1u_nf::<9>(&s["SET_TRANSFORM:".len()..]).map(|(id, fs)| IpcCommand::SetTransform {
                                id,
                                px: fs[0], py: fs[1], pz: fs[2],
                                ex: fs[3], ey: fs[4], ez: fs[5],
                                sx: fs[6], sy: fs[7], sz: fs[8],
                            })
                        }
                        s if s.starts_with("REPARENT:") => {
                            // フォーマット: REPARENT:{child},{parent|-1}
                            parse1u_tail(&s["REPARENT:".len()..]).and_then(|(child, p)| {
                                let new_parent = if p == "-1" { None } else { p.parse::<u32>().ok() };
                                Some(IpcCommand::Reparent { child, new_parent })
                            })
                        }
                        s if s.starts_with("VIEWPORT_FOV:") => {
                            s["VIEWPORT_FOV:".len()..].parse::<f32>().ok()
                                .map(IpcCommand::SetCameraFov)
                        }
                        s if s.starts_with("VIEWPORT_FAR:") => {
                            s["VIEWPORT_FAR:".len()..].parse::<f32>().ok()
                                .map(IpcCommand::SetCameraFar)
                        }
                        "SHOW_GRID:1"           => Some(IpcCommand::SetShowGrid(true)),
                        "SHOW_GRID:0"           => Some(IpcCommand::SetShowGrid(false)),
                        "SHOW_AXIS_GIZMO:1"     => Some(IpcCommand::SetShowAxisGizmo(true)),
                        "SHOW_AXIS_GIZMO:0"     => Some(IpcCommand::SetShowAxisGizmo(false)),
                        "CANVAS_SS_OVERLAY:1"   => Some(IpcCommand::SetCanvasScreenSpaceOverlay(true)),
                        "CANVAS_SS_OVERLAY:0"   => Some(IpcCommand::SetCanvasScreenSpaceOverlay(false)),
                        s if s.starts_with("LOAD_SCENE:") => {
                            Some(IpcCommand::LoadScene(s["LOAD_SCENE:".len()..].to_string()))
                        }
                        "GET_CAM_STATE" => Some(IpcCommand::GetCamState),
                        s if s.starts_with("CAM_TRANSFORM:") => {
                            // フォーマット: CAM_TRANSFORM:{px},{py},{pz},{euler_x},{euler_y},{euler_z}
                            parse_nf::<6>(&s["CAM_TRANSFORM:".len()..]).map(|fs| IpcCommand::SetCameraTransform {
                                px: fs[0], py: fs[1], pz: fs[2],
                                euler_x: fs[3], euler_y: fs[4], euler_z: fs[5],
                            })
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
                        s if s.starts_with("ADD_ACTOR_CHILD:") => {
                            // ADD_ACTOR_CHILD:{parent_dfs_id}
                            s["ADD_ACTOR_CHILD:".len()..].trim().parse::<u32>().ok()
                                .map(|id| IpcCommand::AddActorChild { parent_dfs_id: id })
                        }
                        s if s.starts_with("ADD_ACTOR_2D_CHILD:") => {
                            // ADD_ACTOR_2D_CHILD:{parent_dfs_id}
                            s["ADD_ACTOR_2D_CHILD:".len()..].trim().parse::<u32>().ok()
                                .map(|id| IpcCommand::AddActor2dChild { parent_dfs_id: id })
                        }
                        s if s.starts_with("REMOVE_ACTOR:") => {
                            s["REMOVE_ACTOR:".len()..].parse::<u32>().ok()
                                .map(IpcCommand::RemoveActor)
                        }
                        s if s.starts_with("RENAME_ACTOR:") => {
                            // フォーマット: RENAME_ACTOR:{dfs_id},{name}
                            parse1u_tail(&s["RENAME_ACTOR:".len()..])
                                .map(|(dfs_id, name)| IpcCommand::RenameActor { dfs_id, name: name.to_string() })
                        }
                        s if s.starts_with("REMOVE_COMPONENT:") => {
                            // フォーマット: REMOVE_COMPONENT:{actor_dfs_id},{slot_idx}
                            parse2u(&s["REMOVE_COMPONENT:".len()..])
                                .map(|(a, sl)| IpcCommand::RemoveComponentSlot { actor_dfs_id: a, slot_idx: sl })
                        }
                        s if s.starts_with("RENAME_COMPONENT:") => {
                            // フォーマット: RENAME_COMPONENT:{actor_dfs_id},{slot_idx},{name}
                            parse2u_tail(&s["RENAME_COMPONENT:".len()..])
                                .map(|(a, sl, name)| IpcCommand::RenameComponentSlot {
                                    actor_dfs_id: a, slot_idx: sl, name: name.to_string(),
                                })
                        }
                        s if s.starts_with("SET_ACTOR_TRANSFORM:") => {
                            // フォーマット: SET_ACTOR_TRANSFORM:{dfs_id},{px},{py},{pz},{ex},{ey},{ez},{sx},{sy},{sz}
                            parse1u_nf::<9>(&s["SET_ACTOR_TRANSFORM:".len()..]).map(|(dfs_id, fs)| IpcCommand::SetActorTransform {
                                dfs_id,
                                px: fs[0], py: fs[1], pz: fs[2],
                                ex: fs[3], ey: fs[4], ez: fs[5],
                                sx: fs[6], sy: fs[7], sz: fs[8],
                            })
                        }
                        s if s.starts_with("SET_CANVAS_TRANSFORM:") => {
                            // フォーマット: SET_CANVAS_TRANSFORM:{dfs_id},{px},{py},{rotation},{sx},{sy},{pivot_x},{pivot_y}
                            parse1u_nf::<7>(&s["SET_CANVAS_TRANSFORM:".len()..]).map(|(dfs_id, fs)| IpcCommand::SetCanvasTransform {
                                dfs_id,
                                px: fs[0], py: fs[1],
                                rotation: fs[2],
                                sx: fs[3], sy: fs[4],
                                pivot_x: fs[5], pivot_y: fs[6],
                            })
                        }
                        s if s.starts_with("SET_CANVAS_SIZE:") => {
                            // フォーマット: SET_CANVAS_SIZE:{actor_dfs_id},{slot_idx},{width},{height}
                            parse2u_nf::<2>(&s["SET_CANVAS_SIZE:".len()..])
                                .map(|(a, sl, fs)| IpcCommand::SetCanvasSize {
                                    actor_dfs_id: a, slot_idx: sl,
                                    width: fs[0], height: fs[1],
                                })
                        }
                        s if s.starts_with("SET_MODEL_PATH:") => {
                            // フォーマット: SET_MODEL_PATH:{actor_dfs_id},{slot_idx},{path}
                            parse2u_tail(&s["SET_MODEL_PATH:".len()..])
                                .map(|(a, sl, path)| IpcCommand::SetModelPath {
                                    actor_dfs_id: a, slot_idx: sl, path: path.to_string(),
                                })
                        }
                        s if s.starts_with("SET_SCRIPT_FIELD:") => {
                            // フォーマット: SET_SCRIPT_FIELD:{actor_dfs_id},{slot_idx},{field_name},{value}
                            // value にはカンマが含まれてもよい（文字列フィールド用）
                            parse2u_tail(&s["SET_SCRIPT_FIELD:".len()..])
                                .and_then(|(a, sl, tail)| {
                                    let (field, value) = tail.split_once(',')?;
                                    Some(IpcCommand::SetScriptField {
                                        actor_dfs_id: a, slot_idx: sl,
                                        field: field.to_string(), value: value.to_string(),
                                    })
                                })
                        }
                        "RELOAD_SCRIPTS" => Some(IpcCommand::ReloadScripts),
                        s if s.starts_with("DUPLICATE_COMPONENT:") => {
                            // フォーマット: DUPLICATE_COMPONENT:{actor_dfs_id},{slot_idx}
                            parse2u(&s["DUPLICATE_COMPONENT:".len()..])
                                .map(|(a, sl)| IpcCommand::DuplicateComponent { actor_dfs_id: a, slot_idx: sl })
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
                            parse2u_tail(&s["SET_SPRITE_PATH:".len()..])
                                .map(|(a, sl, path)| IpcCommand::SetSpritePath {
                                    actor_dfs_id: a, slot_idx: sl, path: path.to_string(),
                                })
                        }
                        s if s.starts_with("SET_SPRITE_COLOR:") => {
                            // フォーマット: SET_SPRITE_COLOR:{actor_dfs_id},{slot_idx},{r},{g},{b},{a}
                            parse2u_nf::<4>(&s["SET_SPRITE_COLOR:".len()..])
                                .map(|(a, sl, fs)| IpcCommand::SetSpriteColor {
                                    actor_dfs_id: a, slot_idx: sl,
                                    r: fs[0], g: fs[1], b: fs[2], a: fs[3],
                                })
                        }
                        s if s.starts_with("SET_SPRITE_SIZE:") => {
                            // フォーマット: SET_SPRITE_SIZE:{actor_dfs_id},{slot_idx},{width},{height}
                            parse2u_nf::<2>(&s["SET_SPRITE_SIZE:".len()..])
                                .map(|(a, sl, fs)| IpcCommand::SetSpriteSize {
                                    actor_dfs_id: a, slot_idx: sl,
                                    width: fs[0], height: fs[1],
                                })
                        }
                        s if s.starts_with("SET_AUDIO_FIELD:") => {
                            // フォーマット: SET_AUDIO_FIELD:{actor_dfs_id},{slot_idx},{key},{value}
                            parse2u_tail(&s["SET_AUDIO_FIELD:".len()..]).and_then(|(a, sl, tail)| {
                                let (key, value) = tail.split_once(',')?;
                                Some(IpcCommand::SetAudioField {
                                    actor_dfs_id: a, slot_idx: sl,
                                    key: key.to_string(), value: value.to_string(),
                                })
                            })
                        }
                        s if s.starts_with("SET_CANVAS_ANCHOR:") => {
                            // フォーマット: SET_CANVAS_ANCHOR:{actor_dfs_id},{anchor_x},{anchor_y}
                            parse1u_nf::<2>(&s["SET_CANVAS_ANCHOR:".len()..])
                                .map(|(id, fs)| IpcCommand::SetCanvasAnchor { actor_dfs_id: id, ax: fs[0], ay: fs[1] })
                        }
                        s if s.starts_with("SET_CANVAS_SCALE_MODE:") => {
                            // フォーマット: SET_CANVAS_SCALE_MODE:{actor_dfs_id},{slot_idx},{scale_transform},{scale_size}
                            parse2u2b(&s["SET_CANVAS_SCALE_MODE:".len()..])
                                .map(|(id, sl, st, ss)| IpcCommand::SetCanvasScaleMode {
                                    actor_dfs_id: id, slot_idx: sl,
                                    scale_transform: st, scale_size: ss,
                                })
                        }
                        s if s.starts_with("SET_CANVAS_AUTO_SCALE:") => {
                            // フォーマット: SET_CANVAS_AUTO_SCALE:{actor_dfs_id},{slot_idx},{0|1}
                            parse2u1b(&s["SET_CANVAS_AUTO_SCALE:".len()..])
                                .map(|(id, sl, v)| IpcCommand::SetCanvasAutoScale {
                                    actor_dfs_id: id, slot_idx: sl, auto_scale: v,
                                })
                        }
                        s if s.starts_with("SET_CANVAS_ASPECT_RATIO:") => {
                            // フォーマット: SET_CANVAS_ASPECT_RATIO:{id},{slot},{0|1},{axis}
                            let rest = &s["SET_CANVAS_ASPECT_RATIO:".len()..];
                            let mut it = rest.splitn(4, ',');
                            (|| -> Option<IpcCommand> {
                                let id:   u32  = it.next()?.trim().parse().ok()?;
                                let sl:   u32  = it.next()?.trim().parse().ok()?;
                                let keep: bool = it.next()?.trim() == "1";
                                let axis       = it.next()?.trim().to_string();
                                Some(IpcCommand::SetCanvasAspectRatio { actor_dfs_id: id, slot_idx: sl, keep, axis })
                            })()
                        }
                        s if s.starts_with("SET_CANVAS_GRAVITY_MODE:") => {
                            // フォーマット: SET_CANVAS_GRAVITY_MODE:{actor_dfs_id},{slot_idx},{mode}
                            let rest = &s["SET_CANVAS_GRAVITY_MODE:".len()..];
                            let mut it = rest.splitn(3, ',');
                            (|| -> Option<IpcCommand> {
                                let id:   u32 = it.next()?.trim().parse().ok()?;
                                let sl:   u32 = it.next()?.trim().parse().ok()?;
                                let mode: u8  = it.next()?.trim().parse().ok()?;
                                Some(IpcCommand::SetCanvasGravityMode { actor_dfs_id: id, slot_idx: sl, mode })
                            })()
                        }
                        s if s.starts_with("SET_COLLIDER2D_ASPECT_RATIO:") => {
                            // フォーマット: SET_COLLIDER2D_ASPECT_RATIO:{id},{slot},{0|1},{axis}
                            let rest = &s["SET_COLLIDER2D_ASPECT_RATIO:".len()..];
                            let mut it = rest.splitn(4, ',');
                            (|| -> Option<IpcCommand> {
                                let id:   u32  = it.next()?.trim().parse().ok()?;
                                let sl:   u32  = it.next()?.trim().parse().ok()?;
                                let keep: bool = it.next()?.trim() == "1";
                                let axis       = it.next()?.trim().to_string();
                                Some(IpcCommand::SetCollider2dAspectRatio { actor_dfs_id: id, slot_idx: sl, keep, axis })
                            })()
                        }
                        s if s.starts_with("SET_CANVAS_VIEWPORT_REF_WINDOW:") => {
                            // フォーマット: SET_CANVAS_VIEWPORT_REF_WINDOW:{actor_dfs_id},{slot_idx}
                            parse2u(&s["SET_CANVAS_VIEWPORT_REF_WINDOW:".len()..])
                                .map(|(id, sl)| IpcCommand::SetCanvasViewportRefWindow {
                                    actor_dfs_id: id, slot_idx: sl,
                                })
                        }
                        s if s.starts_with("SET_CANVAS_VIEWPORT_REF_CAMERA:") => {
                            // フォーマット: SET_CANVAS_VIEWPORT_REF_CAMERA:{actor_dfs_id},{slot_idx},{actor_name},{slot_name}
                            // actor_name / slot_name にカンマが含まれない前提で4分割する
                            let rest = &s["SET_CANVAS_VIEWPORT_REF_CAMERA:".len()..];
                            let mut it = rest.splitn(4, ',');
                            let parsed = (|| -> Option<IpcCommand> {
                                let id:        u32    = it.next()?.trim().parse().ok()?;
                                let sl:        u32    = it.next()?.trim().parse().ok()?;
                                let aname             = it.next()?.to_string();
                                let sname             = it.next()?.to_string();
                                Some(IpcCommand::SetCanvasViewportRefCamera {
                                    actor_dfs_id: id, slot_idx: sl,
                                    actor_name: aname, slot_name: sname,
                                })
                            })();
                            parsed
                        }
                        s if s.starts_with("SET_CANVAS_3D_PIVOT:") => {
                            // フォーマット: SET_CANVAS_3D_PIVOT:{actor_dfs_id},{slot_idx},{pivot_x},{pivot_y}
                            parse2u_nf::<2>(&s["SET_CANVAS_3D_PIVOT:".len()..])
                                .map(|(a, sl, fs)| IpcCommand::SetCanvas3dPivot {
                                    actor_dfs_id: a, slot_idx: sl,
                                    pivot_x: fs[0].clamp(0.0, 1.0),
                                    pivot_y: fs[1].clamp(0.0, 1.0),
                                })
                        }
                        s if s.starts_with("SET_INPUTMAP_PATH:") => {
                            // フォーマット: SET_INPUTMAP_PATH:{actor_dfs_id},{slot_idx},{path}
                            parse2u_tail(&s["SET_INPUTMAP_PATH:".len()..])
                                .map(|(a, sl, path)| IpcCommand::SetInputMapPath {
                                    actor_dfs_id: a, slot_idx: sl, path: path.to_string(),
                                })
                        }
                        s if s.starts_with("SET_CAMERA_FOV:") => {
                            // フォーマット: SET_CAMERA_FOV:{actor_dfs_id},{slot_idx},{value}
                            parse2u1f(&s["SET_CAMERA_FOV:".len()..])
                                .map(|(a, sl, v)| IpcCommand::SetCameraComponentFov { actor_dfs_id: a, slot_idx: sl, value: v })
                        }
                        s if s.starts_with("SET_CAMERA_NEAR:") => {
                            // フォーマット: SET_CAMERA_NEAR:{actor_dfs_id},{slot_idx},{value}
                            parse2u1f(&s["SET_CAMERA_NEAR:".len()..])
                                .map(|(a, sl, v)| IpcCommand::SetCameraComponentNear { actor_dfs_id: a, slot_idx: sl, value: v })
                        }
                        s if s.starts_with("SET_CAMERA_FAR:") => {
                            // フォーマット: SET_CAMERA_FAR:{actor_dfs_id},{slot_idx},{value}
                            parse2u1f(&s["SET_CAMERA_FAR:".len()..])
                                .map(|(a, sl, v)| IpcCommand::SetCameraComponentFar { actor_dfs_id: a, slot_idx: sl, value: v })
                        }
                        s if s.starts_with("SET_CAMERA_MAIN:") => {
                            // フォーマット: SET_CAMERA_MAIN:{actor_dfs_id},{slot_idx},{0|1}
                            parse2u1b(&s["SET_CAMERA_MAIN:".len()..])
                                .map(|(a, sl, v)| IpcCommand::SetCameraComponentMain { actor_dfs_id: a, slot_idx: sl, is_main: v })
                        }
                        s if s.starts_with("SET_CAMERA_CLEAR_COLOR:") => {
                            // フォーマット: SET_CAMERA_CLEAR_COLOR:{actor_dfs_id},{slot_idx},{r},{g},{b},{a}
                            parse2u_nf::<4>(&s["SET_CAMERA_CLEAR_COLOR:".len()..])
                                .map(|(a, sl, fs)| IpcCommand::SetCameraComponentClearColor {
                                    actor_dfs_id: a, slot_idx: sl,
                                    r: fs[0], g: fs[1], b: fs[2], a: fs[3],
                                })
                        }
                        s if s.starts_with("SET_CAMERA_SCALING_MODE:") => {
                            // フォーマット: SET_CAMERA_SCALING_MODE:{actor_dfs_id},{slot_idx},{mode}
                            parse2u_tail(&s["SET_CAMERA_SCALING_MODE:".len()..])
                                .map(|(a, sl, mode)| IpcCommand::SetCameraComponentScalingMode {
                                    actor_dfs_id: a, slot_idx: sl, mode: mode.trim().to_string(),
                                })
                        }
                        s if s.starts_with("SET_CAMERA_TARGET_SIZE:") => {
                            // フォーマット: SET_CAMERA_TARGET_SIZE:{actor_dfs_id},{slot_idx},{width},{height}
                            let rest = &s["SET_CAMERA_TARGET_SIZE:".len()..];
                            let mut it = rest.split(',');
                            if let (Some(a_s), Some(sl_s), Some(w_s), Some(h_s)) =
                                (it.next(), it.next(), it.next(), it.next())
                            {
                                if let (Ok(a), Ok(sl), Ok(w), Ok(h)) = (
                                    a_s.trim().parse::<u32>(),
                                    sl_s.trim().parse::<u32>(),
                                    w_s.trim().parse::<u32>(),
                                    h_s.trim().parse::<u32>(),
                                ) {
                                    Some(IpcCommand::SetCameraComponentTargetSize {
                                        actor_dfs_id: a, slot_idx: sl, width: w, height: h,
                                    })
                                } else { None }
                            } else { None }
                        }
                        s if s.starts_with("SET_CAMERA_BAR_COLOR:") => {
                            // フォーマット: SET_CAMERA_BAR_COLOR:{actor_dfs_id},{slot_idx},{r},{g},{b},{a}
                            parse2u_nf::<4>(&s["SET_CAMERA_BAR_COLOR:".len()..])
                                .map(|(a, sl, fs)| IpcCommand::SetCameraBarColor {
                                    actor_dfs_id: a, slot_idx: sl,
                                    r: fs[0], g: fs[1], b: fs[2], a: fs[3],
                                })
                        }
                        s if s.starts_with("SET_COLLIDER_DATA:") => {
                            // フォーマット: SET_COLLIDER_DATA:{actor_dfs_id},{slot_idx},{json}
                            // json は ColliderComponentData の JSON（カンマ含む）のため splitn(3) を使用
                            let rest = &s["SET_COLLIDER_DATA:".len()..];
                            let mut it = rest.splitn(3, ',');
                            if let (Some(a_s), Some(sl_s), Some(json)) =
                                (it.next(), it.next(), it.next())
                            {
                                if let (Ok(a), Ok(sl)) =
                                    (a_s.trim().parse::<u32>(), sl_s.trim().parse::<u32>())
                                {
                                    Some(IpcCommand::SetColliderData {
                                        actor_dfs_id: a,
                                        slot_idx: sl,
                                        json: json.to_string(),
                                    })
                                } else { None }
                            } else { None }
                        }
                        s if s.starts_with("SET_COLLIDER2D_DATA:") => {
                            // フォーマット: SET_COLLIDER2D_DATA:{actor_dfs_id},{slot_idx},{json}
                            // json は Collider2dComponentData の JSON（カンマ含む）のため splitn(3) を使用
                            let rest = &s["SET_COLLIDER2D_DATA:".len()..];
                            let mut it = rest.splitn(3, ',');
                            if let (Some(a_s), Some(sl_s), Some(json)) =
                                (it.next(), it.next(), it.next())
                            {
                                if let (Ok(a), Ok(sl)) =
                                    (a_s.trim().parse::<u32>(), sl_s.trim().parse::<u32>())
                                {
                                    Some(IpcCommand::SetCollider2dData {
                                        actor_dfs_id: a,
                                        slot_idx: sl,
                                        json: json.to_string(),
                                    })
                                } else { None }
                            } else { None }
                        }
                        s if s.starts_with("SET_EDIT_PHYSICS_2D:") => {
                            // フォーマット: SET_EDIT_PHYSICS_2D:{enabled},{with_rigidbody}  (0/1)
                            let rest = &s["SET_EDIT_PHYSICS_2D:".len()..];
                            let mut it = rest.split(',');
                            match (it.next(), it.next()) {
                                (Some(e), Some(rb)) => Some(IpcCommand::SetEditPhysics2d {
                                    enabled:        e.trim() == "1",
                                    with_rigidbody: rb.trim() == "1",
                                }),
                                _ => None,
                            }
                        }
                        s if s.starts_with("SET_PLUGIN_FIELD:") => {
                            // フォーマット: SET_PLUGIN_FIELD:{actor_dfs_id},{slot_idx},{key},{value}
                            // key と value はカンマを含む可能性があるため 4 分割して最後をまとめる
                            let rest = &s["SET_PLUGIN_FIELD:".len()..];
                            let mut it = rest.splitn(4, ',');
                            if let (Some(a_s), Some(sl_s), Some(key), Some(value)) =
                                (it.next(), it.next(), it.next(), it.next())
                            {
                                if let (Ok(a), Ok(sl)) =
                                    (a_s.trim().parse::<u32>(), sl_s.trim().parse::<u32>())
                                {
                                    Some(IpcCommand::SetPluginField {
                                        actor_dfs_id: a,
                                        slot_idx: sl,
                                        key:   key.to_string(),
                                        value: value.to_string(),
                                    })
                                } else { None }
                            } else { None }
                        }
                        "GET_PLUGIN_LIST"  => Some(IpcCommand::GetPluginList),
                        "GET_SCENE_INFO"   => Some(IpcCommand::GetSceneInfo),
                        "PAUSE_RENDER"     => Some(IpcCommand::PauseRender),
                        "RESUME_RENDER"    => Some(IpcCommand::ResumeRender),

                        // ── AI アシスタント用コマンド ──────────────────────────────
                        s if s.starts_with("AI_ADD_ACTOR:") => {
                            // フォーマット: AI_ADD_ACTOR:{name},{x},{y},{z}
                            let rest = &s["AI_ADD_ACTOR:".len()..];
                            // 名前にカンマが含まれる可能性があるため、末尾 3 要素を数値として取り出す
                            let parts: Vec<&str> = rest.rsplitn(4, ',').collect();
                            if parts.len() == 4 {
                                if let (Ok(z), Ok(y), Ok(x)) = (
                                    parts[0].trim().parse::<f32>(),
                                    parts[1].trim().parse::<f32>(),
                                    parts[2].trim().parse::<f32>(),
                                ) {
                                    let name = parts[3].to_string();
                                    Some(IpcCommand::AiAddActor { name, x, y, z })
                                } else { None }
                            } else { None }
                        }
                        s if s.starts_with("AI_REMOVE_ACTOR:") => {
                            // フォーマット: AI_REMOVE_ACTOR:{actor_dfs_id}
                            s["AI_REMOVE_ACTOR:".len()..].trim().parse::<u32>().ok()
                                .map(|id| IpcCommand::AiRemoveActor { actor_dfs_id: id })
                        }
                        s if s.starts_with("AI_MOVE_ACTOR:") => {
                            // フォーマット: AI_MOVE_ACTOR:{actor_dfs_id},{x},{y},{z}
                            parse1u_nf::<3>(&s["AI_MOVE_ACTOR:".len()..])
                                .map(|(id, fs)| IpcCommand::AiMoveActor {
                                    actor_dfs_id: id, x: fs[0], y: fs[1], z: fs[2],
                                })
                        }
                        s if s.starts_with("AI_ADD_COMPONENT:") => {
                            // フォーマット: AI_ADD_COMPONENT:{actor_dfs_id},{component_type},{params_json}
                            let rest = &s["AI_ADD_COMPONENT:".len()..];
                            let mut it = rest.splitn(3, ',');
                            if let (Some(id_s), Some(comp_type), Some(params)) =
                                (it.next(), it.next(), it.next())
                            {
                                id_s.trim().parse::<u32>().ok().map(|id| IpcCommand::AiAddComponent {
                                    actor_dfs_id: id,
                                    component_type: comp_type.to_string(),
                                    params_json:    params.to_string(),
                                })
                            } else { None }
                        }
                        s if s.starts_with("AI_SET_VALUE:") => {
                            // フォーマット: AI_SET_VALUE:{actor_dfs_id},{slot_idx},{key},{value}
                            let rest = &s["AI_SET_VALUE:".len()..];
                            let mut it = rest.splitn(4, ',');
                            if let (Some(a_s), Some(sl_s), Some(key), Some(value)) =
                                (it.next(), it.next(), it.next(), it.next())
                            {
                                if let (Ok(a), Ok(sl)) =
                                    (a_s.trim().parse::<u32>(), sl_s.trim().parse::<u32>())
                                {
                                    Some(IpcCommand::AiSetValue {
                                        actor_dfs_id: a,
                                        slot_idx:     sl,
                                        key:          key.to_string(),
                                        value:        value.to_string(),
                                    })
                                } else { None }
                            } else { None }
                        }

                        s if s.starts_with("EXPORT_ACTOR:") => {
                            // フォーマット: EXPORT_ACTOR:{dfs_id},{path}
                            // path は絶対ファイルパス（カンマを含まない前提）
                            parse1u_tail(&s["EXPORT_ACTOR:".len()..])
                                .map(|(dfs_id, path)| IpcCommand::ExportActor {
                                    dfs_id,
                                    path: path.to_string(),
                                })
                        }

                        "EDIT_PHYSICS_PLAY_PAUSE" => {
                            Some(IpcCommand::EditPhysicsPlayPause)
                        }
                        s if s.starts_with("EDIT_PHYSICS_STEP:") => {
                            let rest = &s["EDIT_PHYSICS_STEP:".len()..];
                            rest.trim().parse::<i32>().ok()
                                .map(|step| IpcCommand::EditPhysicsStep { step })
                        }
                        "EDIT_PHYSICS_APPLY_FRAME" => {
                            Some(IpcCommand::EditPhysicsApplyFrame)
                        }
                        s if s.starts_with("EDIT_PHYSICS_SEEK:") => {
                            let rest = &s["EDIT_PHYSICS_SEEK:".len()..];
                            rest.trim().parse::<usize>().ok()
                                .map(|frame| IpcCommand::EditPhysicsSeek { frame })
                        }
                        s if s.starts_with("SET_EDIT_PHYSICS:") => {
                            // フォーマット: SET_EDIT_PHYSICS:{enabled},{with_rigidbody}  (0/1)
                            let rest = &s["SET_EDIT_PHYSICS:".len()..];
                            let mut it = rest.split(',');
                            match (it.next(), it.next()) {
                                (Some(e), Some(rb)) => Some(IpcCommand::SetEditPhysics {
                                    enabled:         e.trim() == "1",
                                    with_rigidbody:  rb.trim() == "1",
                                }),
                                _ => None,
                            }
                        }
                        "SET_PLAY_COLLIDER_DRAW:1" => Some(IpcCommand::SetPlayColliderDraw(true)),
                        "SET_PLAY_COLLIDER_DRAW:0" => Some(IpcCommand::SetPlayColliderDraw(false)),

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
    for _ in 0..PIPE_CONNECT_RETRIES {
        match OpenOptions::new().read(true).write(true).open(path) {
            Ok(f)  => return Ok(f),
            Err(_) => thread::sleep(Duration::from_millis(PIPE_CONNECT_RETRY_MS)),
        }
    }
    OpenOptions::new().read(true).write(true).open(path)
}
