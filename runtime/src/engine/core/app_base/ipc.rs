use std::fs::OpenOptions;
use std::io::{BufRead, BufReader, Write};
use std::sync::mpsc;
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

/// エディタの左ツールバーで選択中のギズモ操作種別（選択/移動/回転/拡縮）。
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
//  GizmoSpace — 移動/回転/スケールギズモの座標系モード
// ============================================================

/// ギズモが従う座標系。
/// - World: ワールド軸（X/Y/Z）に整列した従来どおりのギズモ（デフォルト）。
/// - Local: 選択中アクターのローカル回転軸に整列したギズモ。
///   オブジェクトが回転していても、その場での「前後左右」に沿った直感的な操作ができる。
#[derive(Clone, Copy, PartialEq, Eq, Debug)]
pub enum GizmoSpace {
    World,
    Local,
}

impl Default for GizmoSpace {
    fn default() -> Self { GizmoSpace::World }
}

// ============================================================
//  TerrainChunkConfig — 地形チャンク構成のワイヤ表現
// ============================================================

/// エディタから受け取る地形チャンク構成（`TERRAIN_INIT` / `TERRAIN_HEIGHTMAP` の引数）。
///
/// ここでは**値を検証しない**（パース層の責務は「文字列 → 型」まで）。
/// 上下限のクランプは `TerrainSettings::apply_chunk_config` が一手に担う。
#[derive(Clone, Copy, PartialEq, Debug)]
pub struct TerrainChunkConfig {
    /// 初期地面の X 方向チャンク数。
    pub chunks_x: u32,
    /// 初期地面の Z 方向チャンク数。
    pub chunks_z: u32,
    /// チャンク 1 軸あたりのセル数（ボクセル分割数）。
    pub chunk_cells: u32,
    /// 1 ボクセル辺のサイズ（メートル）。
    pub voxel_size: f32,
}

// ============================================================
//  IpcCommand — エディタから受け取るコマンド
// ============================================================

/// エディタ（Named Pipe サーバー）から受信するコマンド 1 件分。
/// IPC 受信スレッドがテキストプロトコルをパースしてこの型に変換し、メインループへ渡す。
pub enum IpcCommand {
    Pause,
    Resume,
    Stop,
    /// エディタから転送されたカメラキー押下（キー名: "W","A","S","D","Q","E","SHIFT"）
    CamKeyDown(String),
    /// エディタから転送されたカメラキー離し
    CamKeyUp(String),
    /// 全カメラキーの強制リセット。
    /// Play 切替などでキー UP がこのランタイムへ届かず状態がスタックするのを防ぐ
    /// （エディタが Edit 復帰・再同期時に送信する）。
    CamKeysClear,
    /// Play 時カーソルクランプの有効/無効
    PlayClamp(bool),
    /// ツールモード切り替え
    SetToolMode(ToolMode),
    /// ギズモ座標系モード切り替え（World / Local）
    /// フォーマット: GIZMO_SPACE:WORLD / GIZMO_SPACE:LOCAL
    SetGizmoSpace(GizmoSpace),
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
    /// 親子付け変更（new_parent=None はルートへ）。
    ///
    /// anchor_sibling: 挿入位置の基準となる兄弟アクターの DFS id（None = 末尾へ追加）。
    /// place_before: true ならアンカーの直前、false ならアンカーの直後へ挿入する。
    /// 旧 2 フィールド形式（IPC 上）との後方互換のため anchor_sibling/place_before は
    /// 未指定時 None/false 扱いになる（＝従来どおり末尾追加）。
    Reparent {
        child:          u32,
        new_parent:     Option<u32>,
        anchor_sibling: Option<u32>,
        place_before:   bool,
    },
    /// インスタンス名変更
    Rename { idx: u32, name: String },
    /// シーンを指定パスへ保存
    SaveScene(String),
    /// ボクセル地形を初期化する（地形ツリー生成＋初期地面）。
    /// ワイヤ形式:
    ///   - `TERRAIN_INIT`（引数なし・旧形式。現在の TerrainSettings で初期化する）
    ///   - `TERRAIN_INIT:{chunks_x},{chunks_z},{chunk_cells},{voxel_size}`（新形式）
    /// `config` が `Some` のときだけチャンク構成を上書きしてから初期化する。
    TerrainInit { config: Option<TerrainChunkConfig> },
    /// 編集中の地形へチャンクを追加する（既存チャンクは温存する）。
    /// ワイヤ形式: `TERRAIN_ADD_CHUNKS:{min_x},{min_z},{max_x},{max_z}`（i32×4・両端含む）
    /// 縦方向（Y）の範囲は現在の TerrainSettings（ground_chunk_y_min/max）に従う。
    TerrainAddChunks { min_x: i32, min_z: i32, max_x: i32, max_z: i32 },
    /// ボクセル地形をブラシ編集する（スクリーン座標からレイマーチで着弾点を求める）。
    /// ワイヤ形式: `TERRAIN_BRUSH:{op},{screen_x},{screen_y},{radius},{strength}`
    ///   op: 0=Add / 1=Subtract / 2=Smooth / 3=Flatten
    TerrainBrush { op: u32, screen_x: f32, screen_y: f32, radius: f32, strength: f32 },
    /// ボクセル地形にレイヤペイントブラシを適用する（Terrain T2）。
    /// ワイヤ形式: `TERRAIN_PAINT:{layer},{screen_x},{screen_y},{radius},{strength}`
    ///   layer: 塗る対象レイヤ番号（0 起点。layers.json の並び順に対応）
    /// 密度は変えず、レイヤ重み（スプラット）だけを押し上げる。
    TerrainPaint { layer: u32, screen_x: f32, screen_y: f32, radius: f32, strength: f32 },
    /// 地形ペイント系ブラシの形状マスク画像を設定・解除する。
    ///
    /// ワイヤ形式: `TERRAIN_BRUSH_MASK:{path}`（`path` が空文字なら解除）
    ///   path: グレースケール画像のパス（`assets://` 仮想パス・絶対パスのどちらも可）。
    ///
    /// **パス専用の状態設定コマンド**である。既存の `TERRAIN_PAINT` /
    /// `TERRAIN_COVER_BRUSH` はカンマ区切りであり、カンマを含みうる
    /// Windows のファイルパスを混ぜられないため、別コマンドに分けてある。
    /// コロン以降はすべて path（カンマも含む）として扱う。
    TerrainBrushMask { path: String },
    /// ボクセル地形の全チャンクを .tvox としてアセット配下へ保存する。
    /// ワイヤ形式: `TERRAIN_SAVE`（引数なし）
    TerrainSave,
    /// ブラシ範囲プレビュー（Edit モードのホバー位置にワイヤスフィアを描く）を更新する。
    /// ワイヤ形式: `TERRAIN_BRUSH_PREVIEW:{screen_x},{screen_y},{radius},{strength}`
    /// strength はプレビュー球の色（低強度=水色〜高強度=オレンジ）に反映される。
    TerrainBrushPreview { screen_x: f32, screen_y: f32, radius: f32, strength: f32 },
    /// ブラシ範囲プレビューを非表示にする（terrain モード離脱時）。
    /// ワイヤ形式: `TERRAIN_BRUSH_PREVIEW_OFF`（引数なし）
    TerrainBrushPreviewOff,
    /// terrain 専用 undo（Ctrl+Z 相当。シーン全体の Undo/Redo とは別スタック）。
    /// ワイヤ形式: `TERRAIN_UNDO`（引数なし）
    TerrainUndo,
    /// terrain 専用 redo（Ctrl+Y 相当）。
    /// ワイヤ形式: `TERRAIN_REDO`（引数なし）
    TerrainRedo,
    /// 現在進行中のブラシストロークを 1 つの undo エントリとして確定する。
    /// エディタはドラッグ終了（マウスアップ）時に送る。
    /// ワイヤ形式: `TERRAIN_STROKE_END`（引数なし）
    TerrainStrokeEnd,
    /// カバー場（I3.1）のリアルタイム連続シミュレートを開始する。
    ///
    /// 開始〜停止までが 1 つの undo 単位（terrain 専用スタックの 1 エントリ）になる。
    /// ワイヤ形式: `TERRAIN_COVER_SIM_START`（引数なし）
    TerrainCoverSimStart,
    /// カバー場の連続シミュレートを停止する。
    /// ワイヤ形式: `TERRAIN_COVER_SIM_STOP`（引数なし）
    TerrainCoverSimStop,
    /// カバー場を指定秒数ぶん **即時**（このフレーム内）計算して停止する。
    ///
    /// ワイヤ形式: `TERRAIN_COVER_STEP:{seconds}`
    TerrainCoverStep { seconds: f32 },
    /// 全チャンクのカバー場を消去する。
    /// ワイヤ形式: `TERRAIN_COVER_CLEAR`（引数なし）
    TerrainCoverClear,
    /// カバー場を球ブラシで手編集する（地形編集モードの「カバー」ツール）。
    ///
    /// ワイヤ形式:
    /// `TERRAIN_COVER_BRUSH:{material_id},{screen_x},{screen_y},{radius},{strength},{target_amount},{erase}`
    ///   material_id  : cover_materials.json の素材 ID（消去時は無視される）。
    ///   target_amount: 塗りの目標量（0..1。消去時は無視される）。
    ///   erase        : 0=塗る / 1=消す。
    /// 着弾点は密度ブラシと同じレイマーチで求める（プレビュー球と必ず一致させるため）。
    /// Undo は密度ブラシと同じ terrain 専用スタック（`TERRAIN_STROKE_END` で確定）。
    TerrainCoverBrush {
        material_id: String,
        screen_x: f32,
        screen_y: f32,
        radius: f32,
        strength: f32,
        target_amount: f32,
        erase: bool,
    },
    /// `CoverEmitterComponent`（I3.1）のフィールドを更新する（cover_emitter_ops.rs が処理）。
    ///
    /// key: range_kind / extents_x / extents_y / extents_z / fade / mask_path /
    ///      mask_size_x / mask_size_z / material_id / strength / enabled。
    /// value は数値・文字列・"true"/"false" のいずれか（key ごとに解釈）。
    /// ワイヤ形式: `SET_COVER_FIELD:{actor_dfs_id},{slot_idx},{key},{value}`
    SetCoverField { actor_dfs_id: u32, slot_idx: u32, key: String, value: String },
    /// レイヤ定義（layers.json）を再読込し、レイヤテクスチャ配列と全チャンクを作り直す。
    /// エディタの地形設定ウィンドウがレイヤを保存した直後に送る（シーンビューへの即時反映）。
    /// ワイヤ形式: `TERRAIN_RELOAD_LAYERS`（引数なし）
    TerrainReloadLayers,
    /// ハイトマップ画像から地形を敷き直す。
    /// ワイヤ形式（2 形式を受け付ける）:
    ///   - 旧: `TERRAIN_HEIGHTMAP:{path},{height_scale}`
    ///         path は Windows パスがカンマを含みうるため、右端のカンマより前を path とする。
    ///   - 新: `TERRAIN_HEIGHTMAP:{chunks_x},{chunks_z},{chunk_cells},{voxel_size},{height_scale},{path}`
    ///         path を**末尾**へ置くことで、path 中のカンマに影響されずに前 5 個の数値
    ///         フィールドを固定個数（splitn(6, ',')）で切り出せる。
    ///   height_scale: 輝度 1.0（白）が対応する高さ（メートル）。
    /// `config` が `Some` のときだけチャンク構成を上書きしてから敷き直す。
    TerrainHeightmap { path: String, height_scale: f32, config: Option<TerrainChunkConfig> },
    /// 散布プロップをルールで全チャンクへ自動散布し直す（Terrain T3）。
    /// ワイヤ形式: `TERRAIN_SCATTER_RULES:{prop_id},{seed}`
    ///   prop_id: props.json のプロップ ID。**空文字なら全プロップ**が対象。
    ///   seed   : ルール散布の大域シード（同じ値なら必ず同じ草原が再現される）。
    /// prop_id は末尾の seed 以外すべてとして切り出す（ID は先頭・seed は末尾固定）。
    TerrainScatterRules { prop_id: String, seed: u64 },
    /// 散布プロップを球ブラシで手描き追加／消去する（Terrain T3）。
    /// ワイヤ形式: `TERRAIN_SCATTER_BRUSH:{prop_id},{screen_x},{screen_y},{radius},{density},{erase}`
    ///   prop_id: props.json のプロップ ID（消去時は無視される＝半径内の全種が消える）。
    ///   density: 1 m² あたりの目標本数。erase: 0=追加 / 1=消去。
    /// 着弾点は密度ブラシと同じレイマーチで求める（プレビュー球と必ず一致させるため）。
    TerrainScatterBrush {
        prop_id: String,
        screen_x: f32,
        screen_y: f32,
        radius: f32,
        density: f32,
        erase: bool,
    },
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
    /// インラインレイトレ影オンオフ（RT対応GPUのみ効果あり）
    SetRtShadows(bool),
    /// ブルーム／FXAA のポストエフェクト設定（Phase R4）。
    /// フィールド: (bloom有効, fxaa有効, bloom強度)。しきい値／ニーは定数既定を使う。
    /// Phase R5: 透明描画方式（距離ソート / WBOIT）も同 IPC で切り替える。
    SetPostFx {
        bloom: bool,
        fxaa: bool,
        bloom_intensity: f32,
        transparency: crate::engine::core::renderer::TransparencyMode,
        /// Deferred（G-Buffer）レンダリング有効フラグ（Phase D3 Deferred Phase B）。欠落時は true。
        /// OFF で従来のフォワード経路（deferred=false）にフォールバックする（A/B パリティ検証用）。
        deferred: bool,
        /// RT屈折の逐次グラブ有効フラグ。欠落時は false（既定 OFF）。
        /// ON でガラス 1 個描画ごとに背景ミップチェーンを再グラブし、ガラス越しガラスの多重屈折を表現する
        /// （重い。距離ソート透明経路＋RT translucency 有効時のみ意味を持つ）。
        refract_sequential_grab: bool,
        /// エディタのシーンビュー表示モード（Lit / Unlit / Wireframe）。欠落時は Lit。
        /// SET_POST_FX に相乗りしているのは、エディタが全ビューポート描画設定を 1 本の
        /// SendPostFx()／1 個の SET_POST_FX ハンドラへ集約しているため（追加配線を増やさない）。
        view_mode: crate::engine::core::renderer::SceneViewMode,
        /// DDGI（レイトレGI）の数値設定（Phase RT-GI）。有効/無効は features.gi へ移行済み。
        gi: crate::engine::core::renderer::GiSettings,
        /// 反射（SSR / RT）の強度（Phase D6）。欠落時は既定 1.0。有効/無効・方式は features.reflection。
        reflection_intensity: f32,
        /// AO（SSAO / RT-AO）の強度（Phase D4）。欠落時は既定 1.0。有効/無効・方式は features.ao。
        ao_intensity: f32,
        /// レンダリング機能マトリクス（新キー "features"）。新エディタは常に Some を送る。
        /// None（旧エディタ）のときは影など他機能を据え置き、legacy_gi_enabled のみ反映する。
        features: Option<crate::engine::core::renderer::RenderFeatures>,
        /// 旧キー "gi_enabled"（後方互換）。features==None のときだけ GI モードへ反映する。
        legacy_gi_enabled: Option<bool>,
    },
    /// 環境光（アンビエント）の色・強度（Phase R1.5）。
    /// フォーマット: `SET_AMBIENT:{r},{g},{b},{intensity}`（色はリニア RGB）。
    /// intensity=0 で完全な暗闇。既定は白×0.05（従来のハードコード値）。
    SetAmbient { color: [f32; 3], intensity: f32 },
    /// 軸ギズモ表示オンオフ
    SetShowAxisGizmo(bool),
    /// スキンスプライトのボーン可視化 ON/OFF（エディタ・セッション限り）。
    /// フォーマット: SHOW_SPRITE_BONES:0|1
    SetShowSpriteBones(bool),
    /// アクターを指定パスへ保存（アクター編集モードのアクティブ世界線）
    SaveActor(String),
    /// インスペクターフィールドドラッグ開始（Undo 単一化のため事前状態を保存）
    BeginTransformDrag { is_actor: bool, target_id: u32 },
    /// インスペクターフィールドドラッグ終了（1 undo コマンドとして記録）
    EndTransformDrag,
    /// シーンファイルのロード
    LoadScene(String),
    /// 埋め込みインプレース Play 開始（フェーズ2）。
    /// Edit ランタイムを再ロードせず、構築済みの地形・散布・GPU リソースを保持したまま
    /// mode を Play へ切り替える。開始前に現アクター状態を ActorData へスナップショットし、
    /// EXIT_PLAY で復元できるようにする。応答: `PLAY_ENTERED`。
    EnterPlay,
    /// 埋め込みインプレース Play 停止（フェーズ2）。
    /// ENTER_PLAY で保持したスナップショットから非地形アクターを再構築し、mode を Edit へ戻す。
    /// 地形・散布・GPU リソースには触れない。応答: `PLAY_EXITED`。
    ExitPlay,
    /// デバッグカメラ状態要求
    GetCamState,
    /// デバッグカメラ位置・Euler XYZ 回転設定（度、YXZ 合成順）
    /// フォーマット: CAM_TRANSFORM:{px},{py},{pz},{euler_x},{euler_y},{euler_z}
    SetCameraTransform { px: f32, py: f32, pz: f32, euler_x: f32, euler_y: f32, euler_z: f32 },
    /// デバッグカメラ移動速度設定
    SetCameraSpeed(f32),
    /// アクターファイルを指定世界線で開く（world_line,path の順でカンマ区切り）
    OpenActor { path: String, world_line: u32 },
    /// シーン内キャンバスアクターの隔離編集を開始する（キャンバス編集タブ）。
    /// シーン世界線（0）の対象アクターサブツリーを world_line へ移動し、
    /// 2D キャンバス編集世界線として登録してアクティブ化する。
    /// フォーマット: EDIT_CANVAS_BEGIN:{world_line},{actor_dfs_id}
    /// 応答: CANVAS_EDIT_WL:{world_line},{root_is_2d:0|1},{actor_name}
    EditCanvasBegin { world_line: u32, actor_dfs_id: u32 },
    /// キャンバス編集タブを終了し、アクターサブツリーをシーン世界線（0）の
    /// 元の位置へ戻す。フォーマット: EDIT_CANVAS_END:{world_line}
    EditCanvasEnd(u32),
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
    /// 指定アクターを新規アクターで「ラップ」する。
    /// 新規アクターを child_dfs の現在位置（同じ親・同じ index）に挿入し、
    /// child_dfs をその新規アクターの子へ移動する（右クリック「親として追加（ラップ）」用）。
    /// フォーマット: WRAP_ACTOR:{child_dfs},{is_2d(0|1)}
    WrapActor { child_dfs: u32, is_2d: bool },
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
    /// ModelComponent のフィールドを更新する（key: cast_shadows / render_tag。
    /// LightComponent の SetLightField と同流儀）
    /// フォーマット: SET_MODEL_FIELD:{actor_dfs_id},{slot_idx},{key},{value}
    SetModelField { actor_dfs_id: u32, slot_idx: u32, key: String, value: String },
    /// マテリアルスロットのオーバーライドを設定/解除する（Phase R7: .mat マテリアル＋
    /// マルチマテリアル編集）。json が空 or `{"kind":"embedded"}` で解除（埋込に戻す）、
    /// それ以外は `MaterialOverrideKind` の JSON（`{"kind":"mat_asset","path":".."}` /
    /// `{"kind":"inline",...}`）。
    /// フォーマット: SET_MATERIAL_OVERRIDE:{actor_dfs_id},{slot_idx},{mat_slot},{json}
    SetMaterialOverride { actor_dfs_id: u32, slot_idx: u32, mat_slot: u32, json: String },
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
    /// スプライトのポストエフェクト（.postfx）参照パスを設定する（空文字列で無効化）。
    SetSpritePostfx { actor_dfs_id: u32, slot_idx: u32, path: String },
    /// SpriteComponent の RGBA カラーを設定する（正規化値 0.0〜1.0）
    /// フォーマット: SET_SPRITE_COLOR:{actor_dfs_id},{slot_idx},{r},{g},{b},{a}
    SetSpriteColor { actor_dfs_id: u32, slot_idx: u32, r: f32, g: f32, b: f32, a: f32 },
    /// SpriteComponent の幅・高さをキャンバスユニットで設定する
    /// フォーマット: SET_SPRITE_SIZE:{actor_dfs_id},{slot_idx},{width},{height}
    SetSpriteSize { actor_dfs_id: u32, slot_idx: u32, width: f32, height: f32 },
    /// SpriteComponent の描画優先度レイヤーを設定する（大きいほど手前）
    /// フォーマット: SET_SPRITE_LAYER:{actor_dfs_id},{slot_idx},{layer}
    SetSpriteLayer { actor_dfs_id: u32, slot_idx: u32, layer: i32 },
    /// SpriteComponent の汎用フィールドを更新する（key: raycast_target）。
    /// 専用コマンド（PATH/COLOR/SIZE/LAYER）が無い単純フィールドはこちらを使う。
    /// フォーマット: SET_SPRITE_FIELD:{actor_dfs_id},{slot_idx},{key},{value}
    SetSpriteField { actor_dfs_id: u32, slot_idx: u32, key: String, value: String },
    /// AudioComponent のフィールドを更新する（key: path/volume/loop/play_on_start/spatial/min_distance/max_distance/pan）
    SetAudioField { actor_dfs_id: u32, slot_idx: u32, key: String, value: String },
    /// LineRendererComponent のフィールドを更新する
    /// （key: width / color / local_space / depth_test / visible）。
    /// points はスクリプト駆動が前提でインスペクタからは編集しない。
    /// フォーマット: SET_LINE_RENDERER_FIELD:{actor_dfs_id},{slot_idx},{key},{value}
    SetLineRendererField { actor_dfs_id: u32, slot_idx: u32, key: String, value: String },
    /// SkinnedSpriteComponent のフィールドを更新する
    /// （key: mesh_path / texture_path / color / layer。Phase A1）
    SetSkinnedSpriteField { actor_dfs_id: u32, slot_idx: u32, key: String, value: String },
    /// SkinnedSpriteComponent の `bone_overrides`（ボーン名 → アクター相対パス）を
    /// JSON オブジェクトで一括置換する（Phase A2 のボーン対応表 UI が使う）。
    /// フォーマット: SET_SKINNED_SPRITE_BONE_OVERRIDES:{actor_dfs_id},{slot_idx},{json}
    SetSkinnedSpriteBoneOverrides { actor_dfs_id: u32, slot_idx: u32, json: String },
    /// `.sprite_mesh` のボーン宣言から 2D 子アクター階層を一括生成する（Phase A2）。
    /// フォーマット: CREATE_SPRITE_BONE_ACTORS:{actor_dfs_id},{slot_idx}
    CreateSpriteBoneActors { actor_dfs_id: u32, slot_idx: u32 },
    /// LightComponent のフィールドを更新する
    /// （key: kind/color/intensity/range/inner_angle/outer_angle/rect_width/rect_height/cast_shadows）
    SetLightField { actor_dfs_id: u32, slot_idx: u32, key: String, value: String },
    /// WaterVolumeComponent のフィールドを更新する（water_ops.rs が処理）。
    /// key: kind / surface_height / region_half_extents / ocean_extent / shallow_color /
    ///      deep_color / absorption_distance / surface_opacity / foam_color / foam_width /
    ///      foam_intensity / wave_amplitude / wave_scale / wave_speed /
    ///      ripple_strength / ripple_foam_threshold /
    ///      viscosity / ripple_damping（I2.1 水域ごとの物性）/ fresnel_power /
    ///      fresnel_strength / reflection_intensity / reflection_roughness（W5.2）/
    ///      refraction_distortion /
    ///      shore_wave_strength / shore_wave_length / shore_wave_period / shore_wave_foam（W1.5 岸波）。
    /// ベクタ系（region_half_extents / *_color）の value は "x,y,z" 形式。
    SetWaterField { actor_dfs_id: u32, slot_idx: u32, key: String, value: String },
    /// 水面シェーディングアセットが宣言したパラメータ 1 個の値を更新する
    /// （Phase W8.2。water_ops.rs が処理）。
    ///
    /// フォーマット: SET_WATER_SHADER_PARAM:{actor_dfs_id},{slot_idx},{name},{x},{y},{z},{w}
    /// `name` はアセット内の識別子、value は常に 4 成分（color は xyz、
    /// スカラーは x のみ意味を持ち、残りは 0 を送る）。
    /// **`WaterVolumeComponent::shader_params` にはこの 4 成分がそのまま入る**。
    SetWaterShaderParam { actor_dfs_id: u32, slot_idx: u32, name: String, value: String },
    /// 水面シェーディングアセットのパラメータ 1 個を**アセットの既定値へ戻す**
    /// （`@reset` 属性を持つ行の「デフォルトに戻す」ボタン。water_ops.rs が処理）。
    ///
    /// フォーマット: RESET_WATER_SHADER_PARAM:{actor_dfs_id},{slot_idx},{name}
    ///
    /// 実装は「シーン側の上書き値を**消す**」であり、既定値を書き込むのではない。
    /// こうしておくと、後からアセットの既定値を書き換えたときに
    /// 「戻したはずの水域」が新しい既定値へ追随する（＝アセットが正典であり続ける）。
    ResetWaterShaderParam { actor_dfs_id: u32, slot_idx: u32, name: String },
    /// **任意のコンポーネント**のフィールド 1 個を既定値へ戻す
    /// （インスペクタ各行の「⟲ デフォルトに戻す」ボタン。component_reset_ops.rs が処理）。
    ///
    /// フォーマット: RESET_COMPONENT_FIELD:{actor_dfs_id},{slot_idx},{field_path}
    ///
    /// `field_path` は **コンポーネントの JSON 表現上のパス**（`/` 区切り、残り全部）。
    /// 例: `intensity` / `wave_amplitude` / `material_overrides/0/kind/roughness`。
    /// 種別ごとの分岐を持たない汎用コマンドなので、コンポーネントが増えても
    /// このコマンドを増やす必要はない（既定値の正典は Rust の `Default`）。
    ResetComponentField { actor_dfs_id: u32, slot_idx: u32, field: String },
    /// 水面シェーディングアセットの `@ref` パラメータ 1 個のバインド先を設定・解除する
    /// （Phase W8.3。water_ops.rs が処理）。
    ///
    /// フォーマット: SET_WATER_SHADER_BINDING:{actor_dfs_id},{slot_idx},{name},{binding}
    /// `name` はアセット内の識別子、`binding` は `"アクタ名|スロット名|変数名"`。
    /// **`binding` が空文字列ならバインド解除**（保存値／アセット既定値へ戻る）。
    ///
    /// 値そのもの（`shader_params`）は一切触らない。バインドを外したときに
    /// 「バインド前の値」へ戻るのはそのためである。
    SetWaterShaderBinding { actor_dfs_id: u32, slot_idx: u32, name: String, binding: String },
    /// 指定アクタが供給できるバインド元（`@ref` の接続先候補）を問い合わせる
    /// （Phase W8.3。water_ops.rs が処理し、`BINDABLE_SOURCES:` で返す）。
    ///
    /// フォーマット: GET_BINDABLE_SOURCES:{actor_dfs_id},{value_type}
    /// `value_type` は `f32` / `vec3`（WGSL 型と厳密一致するものだけを返す）。
    ///
    /// 応答は `BINDABLE_SOURCES:{json}` で、json は
    /// `[{"slot":"スロット名","label":"表示名","variables":[{"name":"…","label":"…"}]}]`。
    /// **候補の正典は Rust 側**（`engine::binding::catalog` とスクリプトの `[Bindable]`）であり、
    /// エディタはミラー表を持たない。
    GetBindableSources { actor_dfs_id: u32, value_type: String },
    /// WaterLinkComponent（水位グラフの開口。W2.5）のフィールド更新（water_link_ops.rs が処理）。
    /// フォーマット: SET_WATER_LINK_FIELD:{actor_dfs_id},{slot_idx},{key},{value}
    /// key: volume_a / volume_b / opening_bottom / opening_height /
    ///      opening_width / openness / flow_coefficient。
    SetWaterLinkField { actor_dfs_id: u32, slot_idx: u32, key: String, value: String },
    /// ControlPointComponent の点列を **JSON でまるごと置き換える**（control_point_ops.rs が処理）。
    /// フォーマット: SET_CONTROL_POINTS:{actor_dfs_id},{slot_idx},{json}
    /// json は `[{"position":[x,y,z],"rotation":[x,y,z],"time":t,"interp":"CatmullRom"},...]`。
    /// 点の追加・削除・並べ替え・属性編集のいずれもこの 1 コマンドで来る
    ///（水の spline_points と同じ「リスト全置換」流儀。差分プロトコルを作らない）。
    SetControlPoints { actor_dfs_id: u32, slot_idx: u32, json: String },
    /// ControlPointComponent の **1 点の位置だけ**を更新する（control_point_ops.rs が処理）。
    /// フォーマット: SET_CONTROL_POINT_POS:{actor_dfs_id},{slot_idx},{index},{x},{y},{z}
    /// ギズモのドラッグ中に毎フレーム飛ぶため、JSON 全置換より軽い専用経路を用意する。
    SetControlPointPos { actor_dfs_id: u32, slot_idx: u32, index: u32, x: f32, y: f32, z: f32 },
    /// インスペクタの点リスト行クリックで、**ビューポート側の点選択を合わせる**
    /// （control_point_ops.rs が処理）。
    /// フォーマット: SELECT_CONTROL_POINT:{actor_dfs_id},{slot_idx},{index}
    /// ランタイム → エディタの `CONTROL_POINT_SELECTED` と対になる逆方向の通知で、
    /// 「リストで選ぶ」「ビューポートで選ぶ」のどちらからでも同じ点が選ばれる状態にする。
    SelectControlPoint { actor_dfs_id: u32, slot_idx: u32, index: u32 },
    /// エディタの「制御点を追加」ボタンをビューポートへ D&D したときの着弾点に点を足す
    /// （control_point_ops.rs が処理）。
    /// フォーマット: ADD_CONTROL_POINT_AT_SCREEN:{actor_dfs_id},{slot_idx},{screen_x},{screen_y}
    ///
    /// **なぜワールド座標ではなく画面座標を送るのか**:
    /// 着弾点を求めるにはシーン形状（メッシュの ID バッファ・地形のボクセル密度場）が要り、
    /// それらは**ランタイムにしか存在しない**。したがって C# 側でワールド座標を求めることは
    /// 原理的に不可能で、画面座標だけを送り、ランタイムが
    /// 「レイ解決 → アクタ相対へ変換 → 点を追加」まで一括で行う。
    /// C# は一切ワールド座標を扱わない（座標系の二重管理を持ち込まないための設計）。
    AddControlPointAtScreen { actor_dfs_id: u32, slot_idx: u32, screen_x: u32, screen_y: u32 },
    /// 「制御点を追加」ボタンのドラッグ中に、**カーソル位置の着弾予定点**を問い合わせる
    /// （control_point_ops.rs / frame_renderer.rs が処理）。
    /// フォーマット: CONTROL_POINT_DRAG_HOVER:{screen_x},{screen_y}
    ///
    /// 着弾解決はドロップ（`AddControlPointAtScreen`）と**同じ関数**を使い、
    /// 結果を「配置予定マーカー」として描くだけで、点そのものは追加しない。
    /// ヒットが無ければマーカーを消す（＝「ここには置けない」が見た目で分かる）。
    /// エディタ側は 30Hz 程度に間引いて送る（毎フレームの ID 読み戻しを浪費しないため）。
    ControlPointDragHover { screen_x: u32, screen_y: u32 },
    /// 「制御点を追加」ボタンのドラッグが終わった / ビューポート外へ出たことの通知。
    /// フォーマット: CONTROL_POINT_DRAG_END
    /// 配置予定マーカーと未解決のホバー要求を破棄する。
    ControlPointDragEnd,
    /// InteractionSourceComponent のフィールドを更新する（interaction_ops.rs が処理）。
    /// key: radius / strength / enabled。value は数値または "true"/"false"。
    SetInteractionField { actor_dfs_id: u32, slot_idx: u32, key: String, value: String },
    /// JointAttachComponent のフィールドを更新する
    /// （key: joint_name / offset_pos / offset_rot / offset_scale。offset_* は "x,y,z" 形式）
    SetJointAttachField { actor_dfs_id: u32, slot_idx: u32, key: String, value: String },
    /// SkyboxComponent のフィールドを更新する（key: texture_path/mode/intensity/tint。skybox_ops.rs が処理）
    SetSkyboxField { actor_dfs_id: u32, slot_idx: u32, key: String, value: String },
    /// ParticleEmitterComponent のフィールドを更新する
    /// （key: max_particles/shape/spawn_volume/emit_mode/lifetime_min/... 等。particle_ops.rs が処理）
    SetParticleField { actor_dfs_id: u32, slot_idx: u32, key: String, value: String },
    /// ParticleEmitterComponent のカーブを差し替える（curve_id: speed/rot_speed/color/scale/
    /// random_colors。json は ParamCurve のシリアライズ。random_colors のみ Vec<ParamCurve> の
    /// JSON 配列）。particle_ops.rs の handle_set_particle_curve が処理する。
    /// フォーマット: SET_PARTICLE_CURVE:{actor_dfs_id},{slot_idx},{curve_id},{json}
    SetParticleCurve { actor_dfs_id: u32, slot_idx: u32, curve_id: String, json: String },
    /// CanvasTransform の anchor を設定する（正規化値 0.0〜1.0）
    /// フォーマット: SET_CANVAS_ANCHOR:{actor_dfs_id},{anchor_x},{anchor_y}
    SetCanvasAnchor { actor_dfs_id: u32, ax: f32, ay: f32 },
    /// CanvasTransform のスケールモード（scale_transform / scale_size / keep_aspect_ratio /
    /// aspect_ratio_axis）を設定する。スケールモードは各ノードの CanvasTransform が保持するため、
    /// スロット指定は不要（アクター DFS ID のルート CanvasTransform を更新する）。
    /// フォーマット: SET_CANVAS_TRANSFORM_SCALE_MODE:{actor_dfs_id},{scale_transform},{scale_size},{keep_aspect},{axis}
    /// scale_transform / scale_size / keep_aspect は "0" または "1"、axis は 0=Width / 1=Height。
    SetCanvasTransformScaleMode { actor_dfs_id: u32, scale_transform: bool, scale_size: bool, keep_aspect: bool, axis: u8 },
    /// キャンバスをスクリーンスペースオーバーレイで表示するかを切り替える
    /// false（デフォルト）= ワールドスペース、true = スクリーンスペースオーバーレイ
    /// フォーマット: CANVAS_SS_OVERLAY:0/1
    SetCanvasScreenSpaceOverlay(bool),
    /// Edit モードのビューポート表示モード（3Dシーン / 2Dシーンタブ）を切り替える
    /// is_2d = true: 2D シーンビュー（スクリーンスペース重ね表示 + 2D パン・ズーム）
    /// is_2d = false: 3D シーンビュー（スクリーンスペースキャンバス非表示）
    /// Play モード中は無視される（Edit モード限定機能）
    /// フォーマット: EDIT_VIEW:2d / EDIT_VIEW:3d
    SetEditViewMode { is_2d: bool },
    /// エディタのデバッグカメラを正射投影/透視投影に切り替える（2D トグル）。
    /// 視点は維持したまま 0.3 秒かけて投影方式を補間する。
    /// フォーマット: EDITOR_CAM_ORTHO:1 / EDITOR_CAM_ORTHO:0
    SetEditorCameraOrtho(bool),
    /// ルートキャンバスの画面サイズ自動スケールを設定する
    /// フォーマット: SET_CANVAS_AUTO_SCALE:{actor_dfs_id},{slot_idx},{value}
    SetCanvasAutoScale { actor_dfs_id: u32, slot_idx: u32, auto_scale: bool },
    /// CanvasComponent の重力方向モードを設定する
    /// フォーマット: SET_CANVAS_GRAVITY_MODE:{actor_dfs_id},{slot_idx},{mode:0|1}
    /// mode: 0=WorldDown, 1=CanvasDown
    SetCanvasGravityMode { actor_dfs_id: u32, slot_idx: u32, mode: u8 },
    /// CanvasComponent の描画ゾーンを設定する（ビューポート・ルートキャンバス用）
    /// フォーマット: SET_CANVAS_DRAW_ZONE:{actor_dfs_id},{slot_idx},{zone}
    /// zone: "foreground"（3D ワールドの手前・デフォルト）| "background"（3D ワールドの奥）
    SetCanvasDrawZone { actor_dfs_id: u32, slot_idx: u32, zone: String },
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
    /// キャンバスのビューポート参照をメインカメラ（is_main = true）に設定する
    /// フォーマット: SET_CANVAS_VIEWPORT_REF_MAIN_CAMERA:{actor_dfs_id},{slot_idx}
    /// メインカメラが存在しない場合は実行時にウィンドウ基準へフォールバックする
    SetCanvasViewportRefMainCamera { actor_dfs_id: u32, slot_idx: u32 },
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
    /// CameraComponent の投影方式を設定する（"perspective" / "orthographic"）
    /// フォーマット: SET_CAMERA_PROJECTION:{actor_dfs_id},{slot_idx},{mode}
    SetCameraComponentProjection { actor_dfs_id: u32, slot_idx: u32, mode: String },
    /// CameraComponent の正射投影の縦描画範囲（ワールド単位・全高）を設定する
    /// フォーマット: SET_CAMERA_ORTHO_HEIGHT:{actor_dfs_id},{slot_idx},{value}
    SetCameraComponentOrthoHeight { actor_dfs_id: u32, slot_idx: u32, value: f32 },
    /// CameraComponent のシェーディングアセット（WGSL ファイル）のパスを設定する
    /// フォーマット: SET_CAMERA_SHADING_ASSET:{actor_dfs_id},{slot_idx},{path}
    /// path は assets:// 仮想パスまたは絶対パス。空文字は未設定（None）を意味する。
    SetCameraComponentShadingAsset { actor_dfs_id: u32, slot_idx: u32, path: String },
    /// CameraComponent のシェーディングアセットが宣言したパラメータ 1 個を更新する
    /// （shading_param_ops.rs が処理。水面の SET_WATER_SHADER_PARAM と同じ流儀）。
    ///
    /// フォーマット: SET_CAMERA_SHADING_PARAM:{actor_dfs_id},{slot_idx},{name},{x},{y},{z},{w}
    /// `name` はアセット内の識別子、値は常に 4 成分（color は xyz、スカラーは x のみ）。
    SetCameraShadingParam { actor_dfs_id: u32, slot_idx: u32, name: String, value: String },
    /// CameraComponent のシェーディングパラメータ 1 個を**アセットの既定値へ戻す**
    /// （`@reset` 属性を持つ行のボタン）。
    ///
    /// フォーマット: RESET_CAMERA_SHADING_PARAM:{actor_dfs_id},{slot_idx},{name}
    /// 実装は「上書き値を消す」であり、既定値を焼き込むのではない
    /// （後からアセットの既定値を変えたときに追随するため）。
    ResetCameraShadingParam { actor_dfs_id: u32, slot_idx: u32, name: String },
    /// CameraComponent の `@ref` パラメータ 1 個のバインド先を設定・解除する。
    ///
    /// フォーマット: SET_CAMERA_SHADING_BINDING:{actor_dfs_id},{slot_idx},{name},{binding}
    /// `binding` が空文字列ならバインド解除（保存値／アセット既定値へ戻る）。
    SetCameraShadingBinding { actor_dfs_id: u32, slot_idx: u32, name: String, binding: String },
    /// シーン既定のシェーディングアセット（WGSL ファイル）のパスを設定する
    /// フォーマット: SET_SCENE_SHADING_ASSET:{path}
    /// path は assets:// 仮想パスまたは絶対パス。空文字は未設定（None）を意味する。
    SetSceneShadingAsset { path: String },
    /// シーン既定のシェーディングアセットが宣言したパラメータ 1 個を更新する
    /// （shading_param_ops.rs が処理）。
    ///
    /// フォーマット: SET_SCENE_SHADING_PARAM:{name},{x},{y},{z},{w}
    SetSceneShadingParam { name: String, value: String },
    /// シーン既定のシェーディングパラメータ 1 個をアセットの既定値へ戻す。
    /// フォーマット: RESET_SCENE_SHADING_PARAM:{name}
    ResetSceneShadingParam { name: String },
    /// シーン既定の `@ref` パラメータ 1 個のバインド先を設定・解除する。
    /// フォーマット: SET_SCENE_SHADING_BINDING:{name},{binding}
    /// `binding` が空文字列ならバインド解除。
    SetSceneShadingBinding { name: String, binding: String },
    /// シーン既定のシェーディングパラメータ一覧（宣言＋現在値）を問い合わせる。
    ///
    /// フォーマット: GET_SCENE_SHADING_PARAMS
    /// 応答: `SCENE_SHADING_PARAMS:{json}`（`shade_params::params_json` と同一のワイヤ表現）。
    ///
    /// シーン設定ウィンドウは `.scene` を直接読んで表示を組み立てるが、
    /// **宣言の解析は Rust 側だけが行う**ため、行の作り方はこの問い合わせで取る。
    GetSceneShadingParams,
    /// シェーディングアセットの WGSL ソースを、保存せずにインメモリ検証して診断を返す
    /// フォーマット: VALIDATE_WGSL:{request_id},{json_source}
    /// - request_id  : 10 進 u64。レスポンスと対応付けるための識別子（カンマを含まない）
    /// - json_source : WGSL ソースの JSON 文字列リテラル（前後の `"` 込み・改行は `\n`）。
    ///                 カンマ・改行を含むソースを 1 行に載せるための表現。
    /// レスポンス: `WGSL_DIAG:{request_id},{json_array}`（診断オブジェクトの配列。成功時 `[]`）
    ValidateWgsl { request_id: u64, source: String },
    /// シーン単位のビューポート／レンダリング設定（`.scene` の settings 節）を設定する
    /// フォーマット: SET_SCENE_SETTINGS:{json}
    /// json は `scene_settings::SceneSettingsData` の JSON 全体（カンマを含む）。
    SetSceneSettings { json: String },
    /// アクターのアクティブ切替（Unity の SetActive 相当）。
    /// フォーマット: SET_ACTOR_ACTIVE:{dfs_id},{0|1}
    SetActorActive { dfs_id: u32, active: bool },
    /// コンポーネントスロットの有効切替（Unity の enabled 相当）。
    /// フォーマット: SET_SLOT_ENABLED:{actor_dfs_id},{slot_idx},{0|1}
    SetSlotEnabled { actor_dfs_id: u32, slot_idx: u32, enabled: bool },
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

    /// プレハブ参照リンクを解除する（アクターの prefab_source を None にする）。
    /// 以後このアクターは「プレハブから更新」・.actor 保存時ライブ反映の対象外となり、
    /// 独立したアクターツリーとしてシーンに保存・維持される。
    /// フォーマット: UNLINK_PREFAB:{actor_dfs}
    UnlinkPrefab { actor_dfs: u32 },

    /// プレハブインスタンスを参照先 .actor の内容で再展開する（ユーザーの明示操作）。
    /// 指定アクタ自身がプレハブならそれを、そうでなければ配下のプレハブインスタンスを対象とする。
    /// シーン上でインスタンスへ加えた変更は破棄されるため、エディタ側で確認ダイアログ必須。
    /// フォーマット: PREFAB_REAPPLY:{actor_dfs}
    ReapplyPrefab { actor_dfs: u32 },

    /// シーン内（world_line=0）の全プレハブインスタンスを、参照先 .actor の内容で
    /// 一括再展開する（ユーザーの明示操作）。
    /// プレハブ本体を編集したあと、その内容をシーン全体へ反映するための入口。
    /// シーン上でインスタンスへ加えた変更は全て破棄されるため、
    /// エディタ側で確認ダイアログ必須。Undo で 1 操作として戻せる。
    /// フォーマット: PREFAB_REAPPLY_ALL（引数なし）
    ReapplyAllPrefabs,

    /// 編集時の物理シミュレーション設定。
    /// enabled=true かつ with_rigidbody=false : 重力なし・全ボディを kinematic として衝突検出のみ
    /// enabled=true かつ with_rigidbody=true  : 重力・ダイナミクスも有効な完全シミュレーション
    /// フォーマット: SET_EDIT_PHYSICS:{enabled},{with_rigidbody}  (0=off, 1=on)
    SetEditPhysics { enabled: bool, with_rigidbody: bool },

    /// 編集時の物理シミュレーション設定（2D/3D 統合）。
    /// エディタの単一チェックボックスから届き、3D・2D を常に同値で設定する。
    /// タイムラインは 3D・2D 共通の 1 本として扱う（タブごと状態保持と併用）。
    /// フォーマット: SET_EDIT_PHYSICS_ALL:{enabled},{with_rigidbody}  (0=off, 1=on)
    SetEditPhysicsAll { enabled: bool, with_rigidbody: bool },

    /// 実行時コライダー描画設定。
    /// Play モードでもコライダーワイヤーフレームを描画する。
    /// フォーマット: SET_PLAY_COLLIDER_DRAW:{0|1}
    SetPlayColliderDraw(bool),

    /// Play 中のシェーディングアセット（.wgsl）ホットリロード設定。
    /// true のとき Play 実行中でも `.wgsl` の mtime ポーリングとパイプライン
    /// 再コンパイルを許可する（保存した瞬間だけヒッチする代わりに、
    /// 再生を止めずに画作りを詰められる）。既定は ON。
    /// フォーマット: SET_PLAY_SHADER_HOT_RELOAD:{0|1}
    SetPlayShaderHotReload(bool),

    /// プロファイラ計測の購読設定。
    /// エディタの「プロファイラ」パネルが表示されている間だけ true を送る。
    /// true の間だけランタイムがフレーム内セクション時間を計測し、
    /// 集計窓ごとに `PROFILER:{json}` を返す（非表示時は計測自体を止めてオーバーヘッドを消す）。
    /// フォーマット: SET_PROFILER:{0|1}
    SetProfilerEnabled(bool),

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

    // ─── アニメーション（AnimatorComponent 編集 / Edit プレビュー）────────────
    /// AnimatorComponent の clips / default_clip / play_on_start / speed をまとめて設定する。
    /// フォーマット: SET_ANIMATOR_CLIPS:{actor_dfs_id},{slot_idx},{json}
    /// json は AnimatorComponentData の serde_json シリアライズ結果（カンマ含む）。
    /// slot_idx はマルチ Animator 対応のため付与（parse2u_tail 流用）。
    SetAnimatorClips { actor_dfs_id: u32, slot_idx: u32, json: String },
    /// Edit モード限定：指定クリップの指定時刻を対象アクターへプレビュー適用する。
    /// フォーマット: ANIM_PREVIEW:{actor_dfs_id},{clip_path},{time}
    AnimPreview { actor_dfs_id: u32, clip_path: String, time: f32 },
    /// アニメーションプレビューを終了し、退避しておいた元値へ復元する。
    /// フォーマット: ANIM_PREVIEW_STOP:{actor_dfs_id}
    AnimPreviewStop { actor_dfs_id: u32 },
    /// 指定クリップのロード済みキャッシュを破棄する（.anim 保存後の再読込用）。
    /// フォーマット: ANIM_RELOAD:{clip_path}
    AnimReload { clip_path: String },
}

// ============================================================
//  IpcClient
// ============================================================

/// Named Pipe クライアント。
/// エディタ（サーバー）への接続、コマンド受信、イベント送信を行う。
pub struct IpcClient {
    commands: mpsc::Receiver<IpcCommand>,
    /// 送信メッセージを書き込み専用スレッドへ渡すチャンネル。
    /// 呼び出し元は投入するだけで即座に返り、パイプ書き込みでブロックしない。
    write_tx: mpsc::Sender<String>,
}

impl IpcClient {
    /// パイプ名（`\\.\pipe\<name>` のうち `<name>` 部分）を指定して接続する。
    pub fn connect(pipe_name: &str) -> std::io::Result<Self> {
        let pipe_path = format!(r"\\.\pipe\{}", pipe_name);
        let file = try_open(&pipe_path)?;
        let write_file = file.try_clone()?;

        // 書き込み専用スレッドを起動する。パイプが詰まってもこのスレッドが待つだけで、
        // 送信元（レンダースレッド等）は影響を受けない。
        let (write_tx, write_rx) = mpsc::channel::<String>();
        thread::spawn(move || write_loop(write_file, write_rx));

        let (tx, rx) = mpsc::channel();
        thread::spawn(move || read_loop(file, tx));

        Ok(Self { commands: rx, write_tx })
    }

    /// エディタにメッセージを 1 行送信する（非ブロッキング）。
    ///
    /// 【重要】名前付きパイプへの同期 writeln! はエディタ（読み手）が遅いと
    /// パイプバッファ満杯で呼び出し元スレッドをブロックする。update_physics などから
    /// 衝突イベントごとに毎フレーム呼ばれるため、これがレンダースレッドを数百 ms
    /// ブロックしていた（[PERF] の physics rest スパイクの実体）。実際の WriteFile は
    /// 書き込み専用スレッドに委譲し、ここではチャンネルへ投入して即座に返す。
    /// 全メッセージが単一チャンネル・単一スレッド経由のため送信順序（FIFO）は保たれる。
    pub fn send(&self, msg: &str) {
        // 書き込みスレッドが生存する限り失敗しない。切断時（スレッド終了後）は捨てる。
        let _ = self.write_tx.send(msg.to_string());
    }

    /// コマンドキューから 1 件取り出す（ブロックしない）。
    pub fn try_recv(&self) -> Option<IpcCommand> {
        self.commands.try_recv().ok()
    }
}

// ============================================================
//  地形コマンドのパース（TERRAIN_*）
//
//  read_loop の巨大な match から切り出した純粋関数。
//  「文字列 in / IpcCommand out」で副作用を持たないため、名前付きパイプ無しで
//  ユニットテストできる（read_loop 内に埋めたままだとテスト不能だった）。
// ============================================================

/// 地形コマンドの共通プレフィックス。read_loop の振り分けと本関数で共有する。
const TERRAIN_COMMAND_PREFIX: &str = "TERRAIN_";

/// 地形チャンク構成の 4 フィールド `"chunks_x,chunks_z,chunk_cells,voxel_size"` をパースする。
///
/// `TERRAIN_INIT:` と `TERRAIN_HEIGHTMAP:` の新形式で共有する（DRY）。
/// フィールド数が 4 でない・数値として読めない場合は `None`。
fn parse_terrain_chunk_config(s: &str) -> Option<TerrainChunkConfig> {
    let parts: Vec<&str> = s.split(',').collect();
    if parts.len() != TERRAIN_CHUNK_CONFIG_FIELDS {
        return None;
    }
    Some(TerrainChunkConfig {
        chunks_x:    parts[0].trim().parse::<u32>().ok()?,
        chunks_z:    parts[1].trim().parse::<u32>().ok()?,
        chunk_cells: parts[2].trim().parse::<u32>().ok()?,
        voxel_size:  parts[3].trim().parse::<f32>().ok()?,
    })
}

/// チャンク構成 4 フィールドの個数。`TERRAIN_INIT:` の引数個数でもある。
const TERRAIN_CHUNK_CONFIG_FIELDS: usize = 4;
/// ハイトマップ新形式の総フィールド数（構成 4 + height_scale 1 + path 1）。
const TERRAIN_HEIGHTMAP_FIELDS: usize = TERRAIN_CHUNK_CONFIG_FIELDS + 2;

/// `TERRAIN_SCATTER_BRUSH:` の引数個数
/// （prop_id, screen_x, screen_y, radius, density, erase）。
const TERRAIN_SCATTER_BRUSH_FIELDS: usize = 6;

/// `TERRAIN_COVER_BRUSH:` の引数個数
/// （material_id, screen_x, screen_y, radius, strength, target_amount, erase）。
const TERRAIN_COVER_BRUSH_FIELDS: usize = 7;

/// ハイトマップ**新形式**をパースする。
/// `"chunks_x,chunks_z,chunk_cells,voxel_size,height_scale,path"`（path は末尾・カンマ可）。
///
/// `splitn(6, ',')` で先頭 5 個の数値フィールドと残り全部（= path）に切り分ける。
/// 前 5 個のいずれかが数値として読めなければ「新形式ではない」と判断して `None` を返し、
/// 呼び出し側が旧形式パースへフォールバックする。
fn parse_heightmap_with_config(rest: &str) -> Option<IpcCommand> {
    let parts: Vec<&str> = rest.splitn(TERRAIN_HEIGHTMAP_FIELDS, ',').collect();
    if parts.len() != TERRAIN_HEIGHTMAP_FIELDS {
        return None;
    }
    let config = TerrainChunkConfig {
        chunks_x:    parts[0].trim().parse::<u32>().ok()?,
        chunks_z:    parts[1].trim().parse::<u32>().ok()?,
        chunk_cells: parts[2].trim().parse::<u32>().ok()?,
        voxel_size:  parts[3].trim().parse::<f32>().ok()?,
    };
    let height_scale = parts[4].trim().parse::<f32>().ok()?;
    // path は空であってはならない（空だと画像読込が必ず失敗するため、旧形式へ落とす）。
    let path = parts[5];
    if path.is_empty() {
        return None;
    }
    Some(IpcCommand::TerrainHeightmap {
        path: path.to_string(),
        height_scale,
        config: Some(config),
    })
}

/// ハイトマップ**旧形式** `"path,height_scale"` をパースする（下位互換）。
///
/// path に Windows パスのカンマが含まれても壊れないよう、右端のカンマで分割する。
fn parse_heightmap_legacy(rest: &str) -> Option<IpcCommand> {
    let idx = rest.rfind(',')?;
    let path = &rest[..idx];
    let height_scale = rest[idx + 1..].trim().parse::<f32>().ok()?;
    Some(IpcCommand::TerrainHeightmap {
        path: path.to_string(),
        height_scale,
        config: None,
    })
}

/// 地形関連の IPC 1 行を `IpcCommand` へ変換する。未知のコマンド／引数不正は `None`。
///
/// 【判定順の注意】
///   引数なしの完全一致（例 `TERRAIN_BRUSH_PREVIEW_OFF`）は、同じ語で始まる
///   前方一致アーム（`TERRAIN_BRUSH_PREVIEW:`）より **先に** 置くこと。
///   逆にすると OFF が引数付きとして解釈され、パース失敗で握り潰される。
fn parse_terrain_command(s: &str) -> Option<IpcCommand> {
    match s {
        // ── 引数なしコマンド ──
        // 地形初期化（旧形式・引数なし）。現在の設定をそのまま使う。
        "TERRAIN_INIT" => Some(IpcCommand::TerrainInit { config: None }),
        // 地形保存。
        "TERRAIN_SAVE" => Some(IpcCommand::TerrainSave),
        // レイヤ定義（layers.json）の再読込＋全チャンク再メッシュ。
        "TERRAIN_RELOAD_LAYERS" => Some(IpcCommand::TerrainReloadLayers),
        // ブラシプレビュー非表示。TERRAIN_BRUSH_PREVIEW: より先に判定する。
        "TERRAIN_BRUSH_PREVIEW_OFF" => Some(IpcCommand::TerrainBrushPreviewOff),
        // terrain 専用 undo/redo・ストローク確定。
        "TERRAIN_UNDO" => Some(IpcCommand::TerrainUndo),
        "TERRAIN_REDO" => Some(IpcCommand::TerrainRedo),
        "TERRAIN_STROKE_END" => Some(IpcCommand::TerrainStrokeEnd),
        // カバー場（I3.1）の連続シミュレート開始／停止・全消去。
        // START と STOP は語頭（`TERRAIN_COVER_SIM_ST`）が共通なので、
        // 前方一致ではなく **完全一致アーム** で判定する（取り違えが起きない）。
        // 秒数指定は別コマンド `TERRAIN_COVER_STEP:` に分離済みで、
        // これらとは語頭も異なるため判定順に依存しない。
        "TERRAIN_COVER_SIM_START" => Some(IpcCommand::TerrainCoverSimStart),
        "TERRAIN_COVER_SIM_STOP" => Some(IpcCommand::TerrainCoverSimStop),
        "TERRAIN_COVER_CLEAR" => Some(IpcCommand::TerrainCoverClear),

        // ── 引数付きコマンド ──
        // 地形初期化（新形式）: "chunks_x,chunks_z,chunk_cells,voxel_size"。
        // 前 3 個が u32、末尾が f32。値域の検証はここでは行わない
        //（TerrainSettings::apply_chunk_config が一手にクランプする）。
        s if s.starts_with("TERRAIN_INIT:") => {
            parse_terrain_chunk_config(&s["TERRAIN_INIT:".len()..])
                .map(|config| IpcCommand::TerrainInit { config: Some(config) })
        }
        // チャンク追加: "min_x,min_z,max_x,max_z"（i32×4・両端含む）。
        s if s.starts_with("TERRAIN_ADD_CHUNKS:") => {
            let parts: Vec<&str> = s["TERRAIN_ADD_CHUNKS:".len()..].split(',').collect();
            if parts.len() != 4 {
                return None;
            }
            let mut v = [0i32; 4];
            for (i, p) in parts.iter().enumerate() {
                v[i] = p.trim().parse::<i32>().ok()?;
            }
            Some(IpcCommand::TerrainAddChunks { min_x: v[0], min_z: v[1], max_x: v[2], max_z: v[3] })
        }
        // ブラシプレビュー更新: "screen_x,screen_y,radius,strength"（f32×4）。
        s if s.starts_with("TERRAIN_BRUSH_PREVIEW:") => {
            parse_nf::<4>(&s["TERRAIN_BRUSH_PREVIEW:".len()..]).map(|fs| {
                IpcCommand::TerrainBrushPreview {
                    screen_x: fs[0],
                    screen_y: fs[1],
                    radius:   fs[2],
                    strength: fs[3],
                }
            })
        }
        // ハイトマップ読込。新旧 2 形式を受け付ける（下位互換）。
        //   新: "chunks_x,chunks_z,chunk_cells,voxel_size,height_scale,path"
        //       path が末尾なので、前 5 フィールドを splitn(6) で確実に切り出せる。
        //   旧: "path,height_scale"
        //       path 側に Windows パスのカンマが含まれても壊れないよう右端のカンマで分割する。
        // 新形式の判定は「6 フィールド以上あり、前 5 個がすべて数値として読める」こと。
        // 旧形式の path（例 "C:\a\map.png"）は先頭フィールドが数値にならないため衝突しない。
        s if s.starts_with("TERRAIN_HEIGHTMAP:") => {
            let rest = &s["TERRAIN_HEIGHTMAP:".len()..];
            parse_heightmap_with_config(rest).or_else(|| parse_heightmap_legacy(rest))
        }
        // 地形ブラシ: "op,screen_x,screen_y,radius,strength"。
        // 先頭 op は u32、残り 4 つは f32。parse1u_nf::<4> で (op, [sx,sy,r,st]) を得る。
        s if s.starts_with("TERRAIN_BRUSH:") => {
            parse1u_nf::<4>(&s["TERRAIN_BRUSH:".len()..]).map(|(op, fs)| {
                IpcCommand::TerrainBrush {
                    op,
                    screen_x: fs[0],
                    screen_y: fs[1],
                    radius:   fs[2],
                    strength: fs[3],
                }
            })
        }
        // 散布ルール実行: "prop_id,seed"。
        // prop_id は空文字（= 全プロップ）を許すため、`rsplit_once(',')` で
        // **末尾の seed** を切り離し、残り全部を prop_id とする
        // （先頭から split すると空 prop_id と引数不足が区別できない）。
        s if s.starts_with("TERRAIN_SCATTER_RULES:") => {
            let rest = &s["TERRAIN_SCATTER_RULES:".len()..];
            let (prop_id, seed_s) = rest.rsplit_once(',')?;
            let seed = seed_s.trim().parse::<u64>().ok()?;
            Some(IpcCommand::TerrainScatterRules { prop_id: prop_id.to_string(), seed })
        }
        // 散布ブラシ: "prop_id,screen_x,screen_y,radius,density,erase"。
        // 先頭が文字列 ID なので数値ヘルパは使えず、固定 6 フィールドで切り出す。
        s if s.starts_with("TERRAIN_SCATTER_BRUSH:") => {
            let rest = &s["TERRAIN_SCATTER_BRUSH:".len()..];
            let parts: Vec<&str> = rest.split(',').collect();
            if parts.len() != TERRAIN_SCATTER_BRUSH_FIELDS {
                return None;
            }
            Some(IpcCommand::TerrainScatterBrush {
                prop_id:  parts[0].to_string(),
                screen_x: parts[1].trim().parse::<f32>().ok()?,
                screen_y: parts[2].trim().parse::<f32>().ok()?,
                radius:   parts[3].trim().parse::<f32>().ok()?,
                density:  parts[4].trim().parse::<f32>().ok()?,
                // erase は 0/1。0 以外はすべて「消去」として扱う（寛容側）。
                erase:    parts[5].trim().parse::<u32>().ok()? != 0,
            })
        }
        // カバーブラシ: "material_id,screen_x,screen_y,radius,strength,target_amount,erase"。
        // 先頭が文字列 ID なので数値ヘルパは使えず、固定 7 フィールドで切り出す
        // （散布ブラシとまったく同じ流儀。ID にカンマは使えない規約）。
        s if s.starts_with("TERRAIN_COVER_BRUSH:") => {
            let rest = &s["TERRAIN_COVER_BRUSH:".len()..];
            let parts: Vec<&str> = rest.split(',').collect();
            if parts.len() != TERRAIN_COVER_BRUSH_FIELDS {
                return None;
            }
            Some(IpcCommand::TerrainCoverBrush {
                material_id:   parts[0].to_string(),
                screen_x:      parts[1].trim().parse::<f32>().ok()?,
                screen_y:      parts[2].trim().parse::<f32>().ok()?,
                radius:        parts[3].trim().parse::<f32>().ok()?,
                strength:      parts[4].trim().parse::<f32>().ok()?,
                target_amount: parts[5].trim().parse::<f32>().ok()?,
                // erase は 0/1。0 以外はすべて「消去」として扱う（寛容側）。
                erase:         parts[6].trim().parse::<u32>().ok()? != 0,
            })
        }
        // カバー場の即時ステップ: "seconds"（f32×1）。
        // 指定秒数ぶんをこのフレーム内で計算して停止する（連続シミュレートは
        // 引数なしの TERRAIN_COVER_SIM_START / _STOP が担当する）。
        s if s.starts_with("TERRAIN_COVER_STEP:") => {
            parse_nf::<1>(&s["TERRAIN_COVER_STEP:".len()..])
                .map(|fs| IpcCommand::TerrainCoverStep { seconds: fs[0] })
        }
        // ブラシ形状マスク: "path"（コロン以降すべて）。
        // **数値ヘルパを通さない**のが要点である。Windows のパスはカンマを含みうるので、
        // 分割せずそのまま渡す。空文字は「解除」を意味する正当な値なので弾かない。
        s if s.starts_with("TERRAIN_BRUSH_MASK:") => {
            Some(IpcCommand::TerrainBrushMask {
                path: s["TERRAIN_BRUSH_MASK:".len()..].to_string(),
            })
        }
        // 地形レイヤペイント: "layer,screen_x,screen_y,radius,strength"。
        // 先頭 layer は u32、残り 4 つは f32（TERRAIN_BRUSH と同じ並び）。
        s if s.starts_with("TERRAIN_PAINT:") => {
            parse1u_nf::<4>(&s["TERRAIN_PAINT:".len()..]).map(|(layer, fs)| {
                IpcCommand::TerrainPaint {
                    layer,
                    screen_x: fs[0],
                    screen_y: fs[1],
                    radius:   fs[2],
                    strength: fs[3],
                }
            })
        }
        _ => None,
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

/// `rest` から `u32, u32, u32` をカンマ区切りでパースして (a, b, c) を返す。
///
/// `parse3u_tail` は 4 フィールド目（tail）を必須とするため、
/// 「u32 がちょうど 3 個で終わる」コマンドには使えない。こちらを使う。
/// 個数が足りない・数値でない場合は None（半端なコマンドを実行しない）。
#[inline]
fn parse3u(rest: &str) -> Option<(u32, u32, u32)> {
    let mut it = rest.split(',');
    Some((
        it.next()?.trim().parse().ok()?,
        it.next()?.trim().parse().ok()?,
        it.next()?.trim().parse().ok()?,
    ))
}

/// `rest` から `u32, u32, u32, u32` をカンマ区切りでパースして (a, b, c, d) を返す。
/// 個数が足りない・数値でない場合は None。
#[inline]
fn parse4u(rest: &str) -> Option<(u32, u32, u32, u32)> {
    let mut it = rest.split(',');
    Some((
        it.next()?.trim().parse().ok()?,
        it.next()?.trim().parse().ok()?,
        it.next()?.trim().parse().ok()?,
        it.next()?.trim().parse().ok()?,
    ))
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

/// `rest` から `u32, u32, u32, <tail>` をカンマ区切りでパースして (a, b, c, tail) を返す。
/// tail にカンマが含まれてもよい（JSON 文字列等）。json の中身にカンマがあるため
/// `splitn(4, ',')` で先頭 3 フィールドのみを厳密に切り出し、残り全部を tail とする
/// （`split_once` を連鎖させると json 内カンマで壊れるため使わない）。
#[inline]
fn parse3u_tail(rest: &str) -> Option<(u32, u32, u32, &str)> {
    let mut it = rest.splitn(4, ',');
    Some((
        it.next()?.trim().parse().ok()?,
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

/// IPC 書き込み専用スレッド。
///
/// チャンネルで受け取ったメッセージを名前付きパイプへ 1 行ずつ書き込む。
/// パイプバッファ満杯で writeln! がブロックしても、待つのはこのスレッドだけで、
/// 送信元スレッド（レンダースレッド等）は send() でチャンネルへ投入済みのため影響しない。
/// 送信元がすべて Drop されて write_tx が閉じると recv() が Err を返しループを抜ける。
fn write_loop(mut file: std::fs::File, rx: mpsc::Receiver<String>) {
    use std::io::Write;
    while let Ok(msg) = rx.recv() {
        // 元の send() と同じく 1 メッセージ = 1 行（改行区切り）で書き込む。
        // 書き込みエラー（パイプ切断等）でスレッドを終了する。
        if writeln!(file, "{}", msg).is_err() {
            break;
        }
    }
}

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
                        "CTRL_DOWN"      => Some(IpcCommand::CtrlDown),
                        "CTRL_UP"        => Some(IpcCommand::CtrlUp),
                        "CAM_KEYS_CLEAR" => Some(IpcCommand::CamKeysClear),
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
                        "GIZMO_SPACE:WORLD" => Some(IpcCommand::SetGizmoSpace(GizmoSpace::World)),
                        "GIZMO_SPACE:LOCAL" => Some(IpcCommand::SetGizmoSpace(GizmoSpace::Local)),
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
                        // 地形コマンド（TERRAIN_*）は 1 箇所へ集約して parse_terrain_command へ委譲する。
                        // 他に "TERRAIN_" で始まるコマンドは存在しないため、この 1 アームで
                        // 従来の個別アーム群と同じ判定結果になる（コマンド追加時もここは触らずに済む）。
                        s if s.starts_with(TERRAIN_COMMAND_PREFIX) => parse_terrain_command(s),
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
                            // フォーマット: REPARENT:{child},{parent|-1}[,{anchorSiblingId|-1},{placeBefore(0|1)}]
                            // anchorSiblingId: 挿入位置の基準となる兄弟アクターの DFS id（-1 = 末尾追加）
                            // placeBefore: 1 = アンカーの前に挿入 / 0 = アンカーの後に挿入
                            // 後方互換: 旧 2 フィールド形式（parent のみ）も受理し、
                            // anchor=-1, placeBefore=0（末尾追加）扱いにする
                            parse1u_tail(&s["REPARENT:".len()..]).and_then(|(child, tail)| {
                                let fields: Vec<&str> = tail.split(',').collect();
                                let p = fields.first()?.trim();
                                let new_parent = if p == "-1" { None } else { Some(p.parse::<u32>().ok()?) };
                                let (anchor_sibling, place_before) = if fields.len() >= 3 {
                                    let a: i64 = fields[1].trim().parse().ok()?;
                                    let anchor = if a < 0 { None } else { Some(a as u32) };
                                    let pb = fields[2].trim().parse::<u32>().ok()? == 1;
                                    (anchor, pb)
                                } else {
                                    (None, false)
                                };
                                Some(IpcCommand::Reparent { child, new_parent, anchor_sibling, place_before })
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
                        "RT_SHADOWS:1"          => Some(IpcCommand::SetRtShadows(true)),
                        "RT_SHADOWS:0"          => Some(IpcCommand::SetRtShadows(false)),
                        // ポストエフェクト設定（Phase R4）。JSON: {"bloom":bool,"fxaa":bool,"bloom_intensity":float}。
                        // パース不能・キー欠落時は安全側（false / 既定強度）へフォールバックする。
                        s if s.starts_with("SET_POST_FX:") => {
                            let json = &s["SET_POST_FX:".len()..];
                            let v = serde_json::from_str::<serde_json::Value>(json).ok();
                            let bloom = v.as_ref().and_then(|v| v["bloom"].as_bool()).unwrap_or(false);
                            let fxaa  = v.as_ref().and_then(|v| v["fxaa"].as_bool()).unwrap_or(false);
                            let bloom_intensity = v.as_ref()
                                .and_then(|v| v["bloom_intensity"].as_f64())
                                .unwrap_or(0.6) as f32;
                            // 透明描画方式（欠落時は "sort" = 距離ソート）。
                            let transparency = crate::engine::core::renderer::TransparencyMode::from_str(
                                v.as_ref()
                                    .and_then(|v| v["transparency"].as_str())
                                    .unwrap_or("sort"),
                            );
                            // メッシュレットカリングは常時有効化したため "meshlet_cull" は解釈しない
                            //（旧エディタが送ってきても無視する＝ワイヤ互換維持）。
                            // Deferred レンダリング（欠落時は true = 有効。Phase D3 Deferred Phase B）。
                            let deferred = v.as_ref()
                                .and_then(|v| v["deferred"].as_bool())
                                .unwrap_or(true);
                            // RT屈折の逐次グラブ（欠落時は false = 無効。既定 OFF の重量オプション）。
                            let refract_sequential_grab = v.as_ref()
                                .and_then(|v| v["refract_sequential_grab"].as_bool())
                                .unwrap_or(false);
                            // ビューモード（欠落・未知時は "lit" = ライティング ON）。
                            let view_mode = crate::engine::core::renderer::SceneViewMode::from_str(
                                v.as_ref()
                                    .and_then(|v| v["view_mode"].as_str())
                                    .unwrap_or("lit"),
                            );
                            // DDGI の数値設定（Phase RT-GI）。既定値から出発し、存在するキーだけ上書きする。
                            let mut gi = crate::engine::core::renderer::GiSettings::default();
                            // 旧キー "gi_enabled"（後方互換）。features 無しの旧エディタからの GI 有効/無効。
                            let legacy_gi_enabled = v.as_ref().and_then(|vv| vv["gi_enabled"].as_bool());
                            if let Some(vv) = v.as_ref() {
                                if let Some(x) = vv["gi_intensity"].as_f64()         { gi.intensity = x as f32; }
                                if let Some(x) = vv["gi_probes_per_frame"].as_u64()  { gi.probes_per_frame = x as u32; }
                                if let Some(x) = vv["gi_rays_per_probe"].as_u64()    { gi.rays_per_probe = x as u32; }
                                if let Some(x) = vv["gi_hysteresis"].as_f64()        { gi.hysteresis = x as f32; }
                                if let Some(x) = vv["gi_recursive_weight"].as_f64()  { gi.recursive_weight = x as f32; }
                            }
                            // 反射強度（Phase D6）。欠落時は既定 1.0。
                            let reflection_intensity = v.as_ref()
                                .and_then(|vv| vv["reflection_intensity"].as_f64())
                                .unwrap_or(crate::engine::core::renderer::DEFAULT_REFLECTION_INTENSITY as f64) as f32;
                            // AO 強度（Phase D4）。欠落時は既定 1.0。
                            let ao_intensity = v.as_ref()
                                .and_then(|vv| vv["ao_intensity"].as_f64())
                                .unwrap_or(crate::engine::core::renderer::DEFAULT_AO_INTENSITY as f64) as f32;
                            // 新キー "features"（機能マトリクス）。欠落キーは serde default で埋まる。
                            let features = v.as_ref()
                                .and_then(|vv| vv.get("features"))
                                .and_then(|fv| serde_json::from_value::<crate::engine::core::renderer::RenderFeatures>(fv.clone()).ok());
                            Some(IpcCommand::SetPostFx { bloom, fxaa, bloom_intensity, transparency, deferred, refract_sequential_grab, view_mode, gi, reflection_intensity, ao_intensity, features, legacy_gi_enabled })
                        }
                        // 環境光（Phase R1.5）。"SET_AMBIENT:{r},{g},{b},{intensity}"。
                        // 4 要素に満たない／パース不能な場合は無視する（None）。
                        s if s.starts_with("SET_AMBIENT:") => {
                            let body = &s["SET_AMBIENT:".len()..];
                            let parts: Vec<&str> = body.split(',').collect();
                            if parts.len() == 4 {
                                if let (Ok(r), Ok(g), Ok(b), Ok(i)) = (
                                    parts[0].parse::<f32>(),
                                    parts[1].parse::<f32>(),
                                    parts[2].parse::<f32>(),
                                    parts[3].parse::<f32>(),
                                ) {
                                    Some(IpcCommand::SetAmbient {
                                        color:     [r.max(0.0), g.max(0.0), b.max(0.0)],
                                        intensity: i.max(0.0),
                                    })
                                } else { None }
                            } else { None }
                        }
                        "SHOW_AXIS_GIZMO:1"     => Some(IpcCommand::SetShowAxisGizmo(true)),
                        "SHOW_AXIS_GIZMO:0"     => Some(IpcCommand::SetShowAxisGizmo(false)),
                        "SHOW_SPRITE_BONES:1"   => Some(IpcCommand::SetShowSpriteBones(true)),
                        "SHOW_SPRITE_BONES:0"   => Some(IpcCommand::SetShowSpriteBones(false)),
                        "CANVAS_SS_OVERLAY:1"   => Some(IpcCommand::SetCanvasScreenSpaceOverlay(true)),
                        "CANVAS_SS_OVERLAY:0"   => Some(IpcCommand::SetCanvasScreenSpaceOverlay(false)),
                        // Edit ビューモード切替（エディタの 3Dシーン / 2Dシーンタブ）
                        "EDIT_VIEW:2d"          => Some(IpcCommand::SetEditViewMode { is_2d: true  }),
                        "EDIT_VIEW:3d"          => Some(IpcCommand::SetEditViewMode { is_2d: false }),
                        "EDITOR_CAM_ORTHO:1"    => Some(IpcCommand::SetEditorCameraOrtho(true)),
                        "EDITOR_CAM_ORTHO:0"    => Some(IpcCommand::SetEditorCameraOrtho(false)),
                        s if s.starts_with("LOAD_SCENE:") => {
                            Some(IpcCommand::LoadScene(s["LOAD_SCENE:".len()..].to_string()))
                        }
                        // 埋め込みインプレース Play 開始/停止（フェーズ2、引数なし）
                        "ENTER_PLAY"    => Some(IpcCommand::EnterPlay),
                        "EXIT_PLAY"     => Some(IpcCommand::ExitPlay),
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
                        s if s.starts_with("EDIT_CANVAS_BEGIN:") => {
                            // フォーマット: "EDIT_CANVAS_BEGIN:{world_line},{actor_dfs_id}"
                            let rest = &s["EDIT_CANVAS_BEGIN:".len()..];
                            let mut it = rest.splitn(2, ',');
                            if let (Some(wl_s), Some(dfs_s)) = (it.next(), it.next()) {
                                match (wl_s.parse::<u32>(), dfs_s.parse::<u32>()) {
                                    (Ok(wl), Ok(dfs)) => Some(IpcCommand::EditCanvasBegin {
                                        world_line: wl, actor_dfs_id: dfs,
                                    }),
                                    _ => None,
                                }
                            } else { None }
                        }
                        s if s.starts_with("EDIT_CANVAS_END:") => {
                            s["EDIT_CANVAS_END:".len()..].parse::<u32>().ok()
                                .map(IpcCommand::EditCanvasEnd)
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
                        s if s.starts_with("WRAP_ACTOR:") => {
                            // WRAP_ACTOR:{child_dfs},{is_2d(0|1)}
                            let rest = &s["WRAP_ACTOR:".len()..];
                            let mut it = rest.splitn(2, ',');
                            if let (Some(c_s), Some(k_s)) = (it.next(), it.next()) {
                                c_s.trim().parse::<u32>().ok().map(|child_dfs| IpcCommand::WrapActor {
                                    child_dfs,
                                    is_2d: k_s.trim() == "1",
                                })
                            } else { None }
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
                        s if s.starts_with("SET_ACTOR_ACTIVE:") => {
                            // フォーマット: SET_ACTOR_ACTIVE:{dfs_id},{0|1}
                            parse2u(&s["SET_ACTOR_ACTIVE:".len()..])
                                .map(|(dfs, v)| IpcCommand::SetActorActive { dfs_id: dfs, active: v != 0 })
                        }
                        s if s.starts_with("SET_SLOT_ENABLED:") => {
                            // フォーマット: SET_SLOT_ENABLED:{actor_dfs_id},{slot_idx},{0|1}
                            parse2u1b(&s["SET_SLOT_ENABLED:".len()..])
                                .map(|(a, sl, v)| IpcCommand::SetSlotEnabled {
                                    actor_dfs_id: a, slot_idx: sl, enabled: v,
                                })
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
                        s if s.starts_with("SET_MODEL_FIELD:") => {
                            // フォーマット: SET_MODEL_FIELD:{actor_dfs_id},{slot_idx},{key},{value}
                            parse2u_tail(&s["SET_MODEL_FIELD:".len()..]).and_then(|(a, sl, tail)| {
                                let (key, value) = tail.split_once(',')?;
                                Some(IpcCommand::SetModelField {
                                    actor_dfs_id: a, slot_idx: sl,
                                    key: key.to_string(), value: value.to_string(),
                                })
                            })
                        }
                        s if s.starts_with("SET_MATERIAL_OVERRIDE:") => {
                            // フォーマット: SET_MATERIAL_OVERRIDE:{actor_dfs_id},{slot_idx},{mat_slot},{json}
                            parse3u_tail(&s["SET_MATERIAL_OVERRIDE:".len()..])
                                .map(|(a, sl, ms, json)| IpcCommand::SetMaterialOverride {
                                    actor_dfs_id: a, slot_idx: sl, mat_slot: ms, json: json.to_string(),
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
                        s if s.starts_with("SET_SPRITE_POSTFX:") => {
                            // フォーマット: SET_SPRITE_POSTFX:{actor_dfs_id},{slot_idx},{path}
                            parse2u_tail(&s["SET_SPRITE_POSTFX:".len()..])
                                .map(|(a, sl, path)| IpcCommand::SetSpritePostfx {
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
                        s if s.starts_with("SET_SPRITE_LAYER:") => {
                            // フォーマット: SET_SPRITE_LAYER:{actor_dfs_id},{slot_idx},{layer}
                            parse2u_tail(&s["SET_SPRITE_LAYER:".len()..]).and_then(|(a, sl, tail)| {
                                let layer: i32 = tail.trim().parse().ok()?;
                                Some(IpcCommand::SetSpriteLayer {
                                    actor_dfs_id: a, slot_idx: sl, layer,
                                })
                            })
                        }
                        s if s.starts_with("SET_SPRITE_FIELD:") => {
                            // フォーマット: SET_SPRITE_FIELD:{actor_dfs_id},{slot_idx},{key},{value}
                            parse2u_tail(&s["SET_SPRITE_FIELD:".len()..]).and_then(|(a, sl, tail)| {
                                let (key, value) = tail.split_once(',')?;
                                Some(IpcCommand::SetSpriteField {
                                    actor_dfs_id: a, slot_idx: sl,
                                    key: key.to_string(), value: value.to_string(),
                                })
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
                        s if s.starts_with("SET_LINE_RENDERER_FIELD:") => {
                            // フォーマット: SET_LINE_RENDERER_FIELD:{actor_dfs_id},{slot_idx},{key},{value}
                            parse2u_tail(&s["SET_LINE_RENDERER_FIELD:".len()..]).and_then(|(a, sl, tail)| {
                                let (key, value) = tail.split_once(',')?;
                                Some(IpcCommand::SetLineRendererField {
                                    actor_dfs_id: a, slot_idx: sl,
                                    key: key.to_string(), value: value.to_string(),
                                })
                            })
                        }
                        s if s.starts_with("SET_SKINNED_SPRITE_BONE_OVERRIDES:") => {
                            // フォーマット: SET_SKINNED_SPRITE_BONE_OVERRIDES:{actor_dfs_id},{slot_idx},{json}
                            // json は "," を含むため tail をそのまま渡す（分割しない）。
                            parse2u_tail(&s["SET_SKINNED_SPRITE_BONE_OVERRIDES:".len()..])
                                .map(|(a, sl, tail)| IpcCommand::SetSkinnedSpriteBoneOverrides {
                                    actor_dfs_id: a, slot_idx: sl, json: tail.to_string(),
                                })
                        }
                        s if s.starts_with("CREATE_SPRITE_BONE_ACTORS:") => {
                            // フォーマット: CREATE_SPRITE_BONE_ACTORS:{actor_dfs_id},{slot_idx}
                            let rest = &s["CREATE_SPRITE_BONE_ACTORS:".len()..];
                            rest.split_once(',').and_then(|(a, sl)| {
                                Some(IpcCommand::CreateSpriteBoneActors {
                                    actor_dfs_id: a.trim().parse::<u32>().ok()?,
                                    slot_idx:     sl.trim().parse::<u32>().ok()?,
                                })
                            })
                        }
                        s if s.starts_with("SET_SKINNED_SPRITE_FIELD:") => {
                            // フォーマット: SET_SKINNED_SPRITE_FIELD:{actor_dfs_id},{slot_idx},{key},{value}
                            // value に "," を含む color を扱うため tail をそのまま value にする。
                            parse2u_tail(&s["SET_SKINNED_SPRITE_FIELD:".len()..]).and_then(|(a, sl, tail)| {
                                let (key, value) = tail.split_once(',')?;
                                Some(IpcCommand::SetSkinnedSpriteField {
                                    actor_dfs_id: a, slot_idx: sl,
                                    key: key.to_string(), value: value.to_string(),
                                })
                            })
                        }
                        s if s.starts_with("SET_LIGHT_FIELD:") => {
                            // フォーマット: SET_LIGHT_FIELD:{actor_dfs_id},{slot_idx},{key},{value}
                            // value に "," を含む可能性がある color を扱うため tail をそのまま value にする。
                            parse2u_tail(&s["SET_LIGHT_FIELD:".len()..]).and_then(|(a, sl, tail)| {
                                let (key, value) = tail.split_once(',')?;
                                Some(IpcCommand::SetLightField {
                                    actor_dfs_id: a, slot_idx: sl,
                                    key: key.to_string(), value: value.to_string(),
                                })
                            })
                        }
                        s if s.starts_with("SET_WATER_FIELD:") => {
                            // フォーマット: SET_WATER_FIELD:{actor_dfs_id},{slot_idx},{key},{value}
                            // value に "," を含む（region_half_extents / *_color は "x,y,z"）ため
                            // 最初の "," までを key とし、tail 全体を value にする。
                            parse2u_tail(&s["SET_WATER_FIELD:".len()..]).and_then(|(a, sl, tail)| {
                                let (key, value) = tail.split_once(',')?;
                                Some(IpcCommand::SetWaterField {
                                    actor_dfs_id: a, slot_idx: sl,
                                    key: key.to_string(), value: value.to_string(),
                                })
                            })
                        }
                        s if s.starts_with("SET_CAMERA_SHADING_PARAM:") => {
                            // フォーマット: SET_CAMERA_SHADING_PARAM:{actor},{slot},{name},{x},{y},{z},{w}
                            // value（"x,y,z,w"）が "," を含むので、最初の "," までを name にする。
                            parse2u_tail(&s["SET_CAMERA_SHADING_PARAM:".len()..]).and_then(|(a, sl, tail)| {
                                let (name, value) = tail.split_once(',')?;
                                Some(IpcCommand::SetCameraShadingParam {
                                    actor_dfs_id: a, slot_idx: sl,
                                    name: name.to_string(), value: value.to_string(),
                                })
                            })
                        }
                        s if s.starts_with("RESET_CAMERA_SHADING_PARAM:") => {
                            // フォーマット: RESET_CAMERA_SHADING_PARAM:{actor},{slot},{name}
                            // name に "," は入らない（WGSL 識別子）ので tail をそのまま使う。
                            parse2u_tail(&s["RESET_CAMERA_SHADING_PARAM:".len()..])
                                .map(|(a, sl, tail)| IpcCommand::ResetCameraShadingParam {
                                    actor_dfs_id: a, slot_idx: sl, name: tail.to_string(),
                                })
                        }
                        s if s.starts_with("SET_CAMERA_SHADING_BINDING:") => {
                            // フォーマット: SET_CAMERA_SHADING_BINDING:{actor},{slot},{name},{binding}
                            // binding はアクタ名を含み "," を含みうるので tail 全体を binding にする。
                            // **空文字列＝解除**なので、右辺が空になるのも正常系である。
                            parse2u_tail(&s["SET_CAMERA_SHADING_BINDING:".len()..]).and_then(|(a, sl, tail)| {
                                let (name, binding) = tail.split_once(',')?;
                                Some(IpcCommand::SetCameraShadingBinding {
                                    actor_dfs_id: a, slot_idx: sl,
                                    name: name.to_string(), binding: binding.to_string(),
                                })
                            })
                        }
                        s if s.starts_with("SET_SCENE_SHADING_PARAM:") => {
                            // フォーマット: SET_SCENE_SHADING_PARAM:{name},{x},{y},{z},{w}
                            s["SET_SCENE_SHADING_PARAM:".len()..].split_once(',')
                                .map(|(name, value)| IpcCommand::SetSceneShadingParam {
                                    name: name.trim().to_string(), value: value.to_string(),
                                })
                        }
                        s if s.starts_with("RESET_SCENE_SHADING_PARAM:") => {
                            // フォーマット: RESET_SCENE_SHADING_PARAM:{name}
                            Some(IpcCommand::ResetSceneShadingParam {
                                name: s["RESET_SCENE_SHADING_PARAM:".len()..].trim().to_string(),
                            })
                        }
                        s if s.starts_with("SET_SCENE_SHADING_BINDING:") => {
                            // フォーマット: SET_SCENE_SHADING_BINDING:{name},{binding}
                            // binding は空文字列でもよい（＝解除）。
                            s["SET_SCENE_SHADING_BINDING:".len()..].split_once(',')
                                .map(|(name, binding)| IpcCommand::SetSceneShadingBinding {
                                    name: name.trim().to_string(), binding: binding.trim().to_string(),
                                })
                        }
                        s if s.starts_with("SET_WATER_SHADER_PARAM:") => {
                            // フォーマット: SET_WATER_SHADER_PARAM:{actor},{slot},{name},{x},{y},{z},{w}
                            // value（"x,y,z,w"）に "," を含むので、最初の "," までを name とし
                            // 残り全部を value にする（SET_WATER_FIELD と同じ流儀）。
                            // **プレフィックスが SET_WATER_FIELD: と衝突しない**ことに注意。
                            parse2u_tail(&s["SET_WATER_SHADER_PARAM:".len()..]).and_then(|(a, sl, tail)| {
                                let (name, value) = tail.split_once(',')?;
                                Some(IpcCommand::SetWaterShaderParam {
                                    actor_dfs_id: a, slot_idx: sl,
                                    name: name.to_string(), value: value.to_string(),
                                })
                            })
                        }
                        s if s.starts_with("RESET_WATER_SHADER_PARAM:") => {
                            // フォーマット: RESET_WATER_SHADER_PARAM:{actor},{slot},{name}
                            // name に "," は入らない（WGSL 識別子）ので tail をそのまま使う。
                            parse2u_tail(&s["RESET_WATER_SHADER_PARAM:".len()..])
                                .map(|(a, sl, tail)| IpcCommand::ResetWaterShaderParam {
                                    actor_dfs_id: a, slot_idx: sl, name: tail.to_string(),
                                })
                        }
                        s if s.starts_with("RESET_COMPONENT_FIELD:") => {
                            // フォーマット: RESET_COMPONENT_FIELD:{actor},{slot},{field_path}
                            // field_path は "/" 区切りなので "," を含まないが、
                            // 万一含まれても壊れないよう tail（残り全部）をそのまま使う。
                            // プレフィックスは他のどのコマンドとも衝突しない
                            // （既存の RESET_* は WATER / CAMERA / SCENE の 3 種のみ）。
                            parse2u_tail(&s["RESET_COMPONENT_FIELD:".len()..])
                                .map(|(a, sl, tail)| IpcCommand::ResetComponentField {
                                    actor_dfs_id: a, slot_idx: sl, field: tail.to_string(),
                                })
                        }
                        s if s.starts_with("SET_WATER_SHADER_BINDING:") => {
                            // フォーマット: SET_WATER_SHADER_BINDING:{actor},{slot},{name},{binding}
                            // binding（"アクタ名|スロット名|変数名"）にはアクタ名が入り "," を
                            // 含みうるので、最初の "," までを name とし tail 全体を binding にする。
                            // **binding は空文字列でもよい**（＝バインド解除）ので、
                            // split_once の右辺が空になる場合も正常系である。
                            parse2u_tail(&s["SET_WATER_SHADER_BINDING:".len()..]).and_then(|(a, sl, tail)| {
                                let (name, binding) = tail.split_once(',')?;
                                Some(IpcCommand::SetWaterShaderBinding {
                                    actor_dfs_id: a, slot_idx: sl,
                                    name: name.to_string(), binding: binding.to_string(),
                                })
                            })
                        }
                        s if s.starts_with("GET_BINDABLE_SOURCES:") => {
                            // フォーマット: GET_BINDABLE_SOURCES:{actor_dfs_id},{value_type}
                            let tail = &s["GET_BINDABLE_SOURCES:".len()..];
                            tail.split_once(',').and_then(|(a, t)| {
                                Some(IpcCommand::GetBindableSources {
                                    actor_dfs_id: a.trim().parse().ok()?,
                                    value_type:   t.trim().to_string(),
                                })
                            })
                        }
                        s if s.starts_with("SET_WATER_LINK_FIELD:") => {
                            // フォーマット: SET_WATER_LINK_FIELD:{actor_dfs_id},{slot_idx},{key},{value}
                            // value（アクタ名）に "," が入りうるので、SET_WATER_FIELD と同じく
                            // 最初の "," までを key とし、tail 全体を value にする。
                            // **プレフィックスが SET_WATER_FIELD: と衝突しない**ことに注意
                            //（"SET_WATER_LINK_FIELD:" は "SET_WATER_FIELD:" で始まらない）。
                            parse2u_tail(&s["SET_WATER_LINK_FIELD:".len()..]).and_then(|(a, sl, tail)| {
                                let (key, value) = tail.split_once(',')?;
                                Some(IpcCommand::SetWaterLinkField {
                                    actor_dfs_id: a, slot_idx: sl,
                                    key: key.to_string(), value: value.to_string(),
                                })
                            })
                        }
                        s if s.starts_with("SET_CONTROL_POINTS:") => {
                            // フォーマット: SET_CONTROL_POINTS:{actor_dfs_id},{slot_idx},{json}
                            // json 内にカンマが多数あるため、先頭 2 フィールドだけを厳密に
                            // 切り出して残り全部を json とする（parse2u_tail が splitn(3) で行う）。
                            parse2u_tail(&s["SET_CONTROL_POINTS:".len()..]).map(|(a, sl, tail)| {
                                IpcCommand::SetControlPoints {
                                    actor_dfs_id: a, slot_idx: sl, json: tail.to_string(),
                                }
                            })
                        }
                        s if s.starts_with("SET_CONTROL_POINT_POS:") => {
                            // フォーマット: SET_CONTROL_POINT_POS:{actor_dfs_id},{slot_idx},{index},{x},{y},{z}
                            // 座標 3 つは tail をカンマで分割して取る。1 つでも欠けたら破棄する
                            //（半端な座標で点を飛ばさない）。
                            parse3u_tail(&s["SET_CONTROL_POINT_POS:".len()..]).and_then(|(a, sl, idx, tail)| {
                                let mut it = tail.split(',');
                                let x = it.next()?.trim().parse::<f32>().ok()?;
                                let y = it.next()?.trim().parse::<f32>().ok()?;
                                let z = it.next()?.trim().parse::<f32>().ok()?;
                                Some(IpcCommand::SetControlPointPos {
                                    actor_dfs_id: a, slot_idx: sl, index: idx, x, y, z,
                                })
                            })
                        }
                        s if s.starts_with("SELECT_CONTROL_POINT:") => {
                            // フォーマット: SELECT_CONTROL_POINT:{actor_dfs_id},{slot_idx},{index}
                            // 3 個の u32 で終わる（tail が無い）ので parse3u を使う。
                            parse3u(&s["SELECT_CONTROL_POINT:".len()..]).map(|(a, sl, idx)| {
                                IpcCommand::SelectControlPoint {
                                    actor_dfs_id: a, slot_idx: sl, index: idx,
                                }
                            })
                        }
                        s if s.starts_with("ADD_CONTROL_POINT_AT_SCREEN:") => {
                            // フォーマット: ADD_CONTROL_POINT_AT_SCREEN:{actor_dfs_id},{slot_idx},{screen_x},{screen_y}
                            // 座標は**ビューポート内のピクセル座標**（ワールド座標ではない。
                            // 着弾点の解決はランタイム側の責務。IpcCommand の定義コメント参照）。
                            parse4u(&s["ADD_CONTROL_POINT_AT_SCREEN:".len()..]).map(|(a, sl, sx, sy)| {
                                IpcCommand::AddControlPointAtScreen {
                                    actor_dfs_id: a, slot_idx: sl, screen_x: sx, screen_y: sy,
                                }
                            })
                        }
                        s if s.starts_with("CONTROL_POINT_DRAG_HOVER:") => {
                            // フォーマット: CONTROL_POINT_DRAG_HOVER:{screen_x},{screen_y}
                            // ドラッグ中の配置予定マーカー用。座標はビューポート内ピクセル。
                            parse2u(&s["CONTROL_POINT_DRAG_HOVER:".len()..]).map(|(sx, sy)| {
                                IpcCommand::ControlPointDragHover { screen_x: sx, screen_y: sy }
                            })
                        }
                        s if s == "CONTROL_POINT_DRAG_END" => Some(IpcCommand::ControlPointDragEnd),
                        s if s.starts_with("SET_INTERACTION_FIELD:") => {
                            // フォーマット: SET_INTERACTION_FIELD:{actor_dfs_id},{slot_idx},{key},{value}
                            // value に "," は含まれない（スカラーと bool のみ）が、
                            // 将来のベクタ系フィールド追加に備えて水と同じ tail 方式で切る。
                            parse2u_tail(&s["SET_INTERACTION_FIELD:".len()..]).and_then(|(a, sl, tail)| {
                                let (key, value) = tail.split_once(',')?;
                                Some(IpcCommand::SetInteractionField {
                                    actor_dfs_id: a, slot_idx: sl,
                                    key: key.to_string(), value: value.to_string(),
                                })
                            })
                        }
                        s if s.starts_with("SET_COVER_FIELD:") => {
                            // フォーマット: SET_COVER_FIELD:{actor_dfs_id},{slot_idx},{key},{value}
                            // value に "," を含みうる（mask_path が Windows パス・
                            // material_id が任意文字）ため、最初のカンマまでを key とし
                            // 残り全部を value にする（JointAttach と同じ tail 方式）。
                            parse2u_tail(&s["SET_COVER_FIELD:".len()..]).and_then(|(a, sl, tail)| {
                                let (key, value) = tail.split_once(',')?;
                                Some(IpcCommand::SetCoverField {
                                    actor_dfs_id: a, slot_idx: sl,
                                    key: key.to_string(), value: value.to_string(),
                                })
                            })
                        }
                        s if s.starts_with("SET_JOINTATTACH_FIELD:") => {
                            // フォーマット: SET_JOINTATTACH_FIELD:{actor_dfs_id},{slot_idx},{key},{value}
                            // value に "," を含む（offset_* は "x,y,z"、joint_name も任意文字）ため tail をそのまま value にする。
                            parse2u_tail(&s["SET_JOINTATTACH_FIELD:".len()..]).and_then(|(a, sl, tail)| {
                                let (key, value) = tail.split_once(',')?;
                                Some(IpcCommand::SetJointAttachField {
                                    actor_dfs_id: a, slot_idx: sl,
                                    key: key.to_string(), value: value.to_string(),
                                })
                            })
                        }
                        s if s.starts_with("SET_SKYBOX_FIELD:") => {
                            // フォーマット: SET_SKYBOX_FIELD:{actor_dfs_id},{slot_idx},{key},{value}
                            // value に "," を含む可能性がある（tint / texture_path）ため tail をそのまま value にする。
                            parse2u_tail(&s["SET_SKYBOX_FIELD:".len()..]).and_then(|(a, sl, tail)| {
                                let (key, value) = tail.split_once(',')?;
                                Some(IpcCommand::SetSkyboxField {
                                    actor_dfs_id: a, slot_idx: sl,
                                    key: key.to_string(), value: value.to_string(),
                                })
                            })
                        }
                        s if s.starts_with("SET_PARTICLE_FIELD:") => {
                            // フォーマット: SET_PARTICLE_FIELD:{actor_dfs_id},{slot_idx},{key},{value}
                            // value に "," を含む可能性を考慮し tail をそのまま value にする。
                            parse2u_tail(&s["SET_PARTICLE_FIELD:".len()..]).and_then(|(a, sl, tail)| {
                                let (key, value) = tail.split_once(',')?;
                                Some(IpcCommand::SetParticleField {
                                    actor_dfs_id: a, slot_idx: sl,
                                    key: key.to_string(), value: value.to_string(),
                                })
                            })
                        }
                        s if s.starts_with("SET_PARTICLE_CURVE:") => {
                            // フォーマット: SET_PARTICLE_CURVE:{actor_dfs_id},{slot_idx},{curve_id},{json}
                            // json に "," を含むため（配列・複数キー）、curve_id だけを
                            // 先頭で split_once して残り全部を json として扱う。
                            parse2u_tail(&s["SET_PARTICLE_CURVE:".len()..]).and_then(|(a, sl, tail)| {
                                let (curve_id, json) = tail.split_once(',')?;
                                Some(IpcCommand::SetParticleCurve {
                                    actor_dfs_id: a, slot_idx: sl,
                                    curve_id: curve_id.to_string(), json: json.to_string(),
                                })
                            })
                        }
                        s if s.starts_with("SET_CANVAS_ANCHOR:") => {
                            // フォーマット: SET_CANVAS_ANCHOR:{actor_dfs_id},{anchor_x},{anchor_y}
                            parse1u_nf::<2>(&s["SET_CANVAS_ANCHOR:".len()..])
                                .map(|(id, fs)| IpcCommand::SetCanvasAnchor { actor_dfs_id: id, ax: fs[0], ay: fs[1] })
                        }
                        s if s.starts_with("SET_CANVAS_TRANSFORM_SCALE_MODE:") => {
                            // フォーマット: SET_CANVAS_TRANSFORM_SCALE_MODE:{actor_dfs_id},{scale_transform},{scale_size},{keep_aspect},{axis}
                            // bool は "0"/"1"、axis は 0=Width / 1=Height。
                            let rest = &s["SET_CANVAS_TRANSFORM_SCALE_MODE:".len()..];
                            let mut it = rest.splitn(5, ',');
                            (|| -> Option<IpcCommand> {
                                let id:   u32  = it.next()?.trim().parse().ok()?;
                                let st:   bool = it.next()?.trim() == "1";
                                let ss:   bool = it.next()?.trim() == "1";
                                let keep: bool = it.next()?.trim() == "1";
                                let axis: u8   = it.next()?.trim().parse().ok()?;
                                Some(IpcCommand::SetCanvasTransformScaleMode {
                                    actor_dfs_id: id,
                                    scale_transform: st, scale_size: ss,
                                    keep_aspect: keep, axis,
                                })
                            })()
                        }
                        s if s.starts_with("SET_CANVAS_AUTO_SCALE:") => {
                            // フォーマット: SET_CANVAS_AUTO_SCALE:{actor_dfs_id},{slot_idx},{0|1}
                            parse2u1b(&s["SET_CANVAS_AUTO_SCALE:".len()..])
                                .map(|(id, sl, v)| IpcCommand::SetCanvasAutoScale {
                                    actor_dfs_id: id, slot_idx: sl, auto_scale: v,
                                })
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
                        s if s.starts_with("SET_CANVAS_DRAW_ZONE:") => {
                            // フォーマット: SET_CANVAS_DRAW_ZONE:{actor_dfs_id},{slot_idx},{zone}
                            // zone: "foreground" | "background"
                            parse2u_tail(&s["SET_CANVAS_DRAW_ZONE:".len()..])
                                .map(|(a, sl, zone)| IpcCommand::SetCanvasDrawZone {
                                    actor_dfs_id: a, slot_idx: sl, zone: zone.trim().to_string(),
                                })
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
                        s if s.starts_with("SET_CANVAS_VIEWPORT_REF_MAIN_CAMERA:") => {
                            // フォーマット: SET_CANVAS_VIEWPORT_REF_MAIN_CAMERA:{actor_dfs_id},{slot_idx}
                            parse2u(&s["SET_CANVAS_VIEWPORT_REF_MAIN_CAMERA:".len()..])
                                .map(|(id, sl)| IpcCommand::SetCanvasViewportRefMainCamera {
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
                        s if s.starts_with("SET_CAMERA_PROJECTION:") => {
                            // フォーマット: SET_CAMERA_PROJECTION:{actor_dfs_id},{slot_idx},{mode}
                            parse2u_tail(&s["SET_CAMERA_PROJECTION:".len()..])
                                .map(|(a, sl, mode)| IpcCommand::SetCameraComponentProjection {
                                    actor_dfs_id: a, slot_idx: sl, mode: mode.trim().to_string(),
                                })
                        }
                        s if s.starts_with("SET_CAMERA_ORTHO_HEIGHT:") => {
                            // フォーマット: SET_CAMERA_ORTHO_HEIGHT:{actor_dfs_id},{slot_idx},{value}
                            parse2u1f(&s["SET_CAMERA_ORTHO_HEIGHT:".len()..])
                                .map(|(a, sl, v)| IpcCommand::SetCameraComponentOrthoHeight {
                                    actor_dfs_id: a, slot_idx: sl, value: v,
                                })
                        }
                        s if s.starts_with("SET_CAMERA_SHADING_ASSET:") => {
                            // フォーマット: SET_CAMERA_SHADING_ASSET:{actor_dfs_id},{slot_idx},{path}
                            // path はカンマを含まない前提（SET_INPUTMAP_PATH と同流儀）
                            parse2u_tail(&s["SET_CAMERA_SHADING_ASSET:".len()..])
                                .map(|(a, sl, path)| IpcCommand::SetCameraComponentShadingAsset {
                                    actor_dfs_id: a, slot_idx: sl, path: path.trim().to_string(),
                                })
                        }
                        s if s.starts_with("SET_SCENE_SHADING_ASSET:") => {
                            // フォーマット: SET_SCENE_SHADING_ASSET:{path}
                            // 空文字は未設定（None）を意味する
                            Some(IpcCommand::SetSceneShadingAsset {
                                path: s["SET_SCENE_SHADING_ASSET:".len()..].trim().to_string(),
                            })
                        }
                        s if s.starts_with("VALIDATE_WGSL:") => {
                            // フォーマット: VALIDATE_WGSL:{request_id},{json_source}
                            // json_source は JSON 文字列リテラル（カンマ・改行エスケープ済み）。
                            // 内部のカンマは splitn(2) で後半に丸ごと残るため、最初のカンマだけで分ければよい。
                            let rest = &s["VALIDATE_WGSL:".len()..];
                            let mut it = rest.splitn(2, ',');
                            match (it.next(), it.next()) {
                                (Some(id_s), Some(json_src)) => {
                                    // id が数値でない／ソースが JSON 文字列として壊れている場合は
                                    // 応答先も内容も確定できないため、コマンドごと捨てる。
                                    match (id_s.trim().parse::<u64>(),
                                           serde_json::from_str::<String>(json_src)) {
                                        (Ok(request_id), Ok(source)) =>
                                            Some(IpcCommand::ValidateWgsl { request_id, source }),
                                        _ => None,
                                    }
                                }
                                _ => None,
                            }
                        }
                        s if s.starts_with("SET_SCENE_SETTINGS:") => {
                            // フォーマット: SET_SCENE_SETTINGS:{json}
                            // json はカンマを含む JSON 全体のため、プレフィクス以降をそのまま渡す
                            Some(IpcCommand::SetSceneSettings {
                                json: s["SET_SCENE_SETTINGS:".len()..].to_string(),
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
                        "GET_SCENE_SHADING_PARAMS" => Some(IpcCommand::GetSceneShadingParams),
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

                        s if s.starts_with("UNLINK_PREFAB:") => {
                            // フォーマット: UNLINK_PREFAB:{actor_dfs}
                            s["UNLINK_PREFAB:".len()..].trim().parse::<u32>().ok()
                                .map(|actor_dfs| IpcCommand::UnlinkPrefab { actor_dfs })
                        }

                        s if s.starts_with("PREFAB_REAPPLY:") => {
                            // フォーマット: PREFAB_REAPPLY:{actor_dfs}
                            s["PREFAB_REAPPLY:".len()..].trim().parse::<u32>().ok()
                                .map(|actor_dfs| IpcCommand::ReapplyPrefab { actor_dfs })
                        }

                        // シーン内全プレハブの一括更新（引数なし）。
                        // "PREFAB_REAPPLY:" 判定とは接頭辞が異なるため衝突しない。
                        "PREFAB_REAPPLY_ALL" => Some(IpcCommand::ReapplyAllPrefabs),

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
                        s if s.starts_with("SET_EDIT_PHYSICS_ALL:") => {
                            // フォーマット: SET_EDIT_PHYSICS_ALL:{enabled},{with_rigidbody}  (0/1)
                            let rest = &s["SET_EDIT_PHYSICS_ALL:".len()..];
                            let mut it = rest.split(',');
                            match (it.next(), it.next()) {
                                (Some(e), Some(rb)) => Some(IpcCommand::SetEditPhysicsAll {
                                    enabled:         e.trim() == "1",
                                    with_rigidbody:  rb.trim() == "1",
                                }),
                                _ => None,
                            }
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

                        // Play 中のシェーディングアセット・ホットリロードの ON/OFF。
                        // エディタ設定（editor_preferences.json の play_shader_hot_reload）と 1 対 1。
                        "SET_PLAY_SHADER_HOT_RELOAD:1" => Some(IpcCommand::SetPlayShaderHotReload(true)),
                        "SET_PLAY_SHADER_HOT_RELOAD:0" => Some(IpcCommand::SetPlayShaderHotReload(false)),

                        // プロファイラ計測の購読 ON/OFF（プロファイラパネルの表示状態と 1 対 1）。
                        "SET_PROFILER:1" => Some(IpcCommand::SetProfilerEnabled(true)),
                        "SET_PROFILER:0" => Some(IpcCommand::SetProfilerEnabled(false)),

                        s if s.starts_with("SET_ANIMATOR_CLIPS:") => {
                            // フォーマット: SET_ANIMATOR_CLIPS:{actor_dfs_id},{slot_idx},{json}
                            // json は AnimatorComponentData の serde_json シリアライズ結果（カンマ含む）。
                            parse2u_tail(&s["SET_ANIMATOR_CLIPS:".len()..])
                                .map(|(a, sl, json)| IpcCommand::SetAnimatorClips {
                                    actor_dfs_id: a, slot_idx: sl, json: json.to_string(),
                                })
                        }
                        s if s.starts_with("ANIM_PREVIEW_STOP:") => {
                            // フォーマット: ANIM_PREVIEW_STOP:{actor_dfs_id}
                            s["ANIM_PREVIEW_STOP:".len()..].trim().parse::<u32>().ok()
                                .map(|actor_dfs_id| IpcCommand::AnimPreviewStop { actor_dfs_id })
                        }
                        s if s.starts_with("ANIM_PREVIEW:") => {
                            // フォーマット: ANIM_PREVIEW:{actor_dfs_id},{clip_path},{time}
                            // clip_path は "assets://..." 仮想パス（'/' 区切りでカンマを含まない）。
                            let rest = &s["ANIM_PREVIEW:".len()..];
                            let mut it = rest.splitn(3, ',');
                            (|| -> Option<IpcCommand> {
                                let actor_dfs_id: u32 = it.next()?.trim().parse().ok()?;
                                let clip_path = it.next()?.to_string();
                                let time: f32 = it.next()?.trim().parse().ok()?;
                                Some(IpcCommand::AnimPreview { actor_dfs_id, clip_path, time })
                            })()
                        }
                        s if s.starts_with("ANIM_RELOAD:") => {
                            // フォーマット: ANIM_RELOAD:{clip_path}
                            let clip_path = s["ANIM_RELOAD:".len()..].to_string();
                            Some(IpcCommand::AnimReload { clip_path })
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
    for _ in 0..PIPE_CONNECT_RETRIES {
        match OpenOptions::new().read(true).write(true).open(path) {
            Ok(f)  => return Ok(f),
            Err(_) => thread::sleep(Duration::from_millis(PIPE_CONNECT_RETRY_MS)),
        }
    }
    OpenOptions::new().read(true).write(true).open(path)
}

// ============================================================
//  テスト — 地形コマンドのパース
//
//  read_loop はパイプ読み込みと一体で自動テストできないため、そこから
//  切り出した純粋関数 parse_terrain_command を直接検証する。
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;

    /// レイヤ再読込コマンドが専用 variant へパースされること（本機能の追加分）。
    #[test]
    fn parses_terrain_reload_layers() {
        assert!(matches!(
            parse_terrain_command("TERRAIN_RELOAD_LAYERS"),
            Some(IpcCommand::TerrainReloadLayers)
        ));
    }

    /// 引数なし地形コマンドが取りこぼされないこと（アーム統合時の退行検出）。
    #[test]
    fn parses_argument_less_terrain_commands() {
        assert!(matches!(parse_terrain_command("TERRAIN_INIT"),        Some(IpcCommand::TerrainInit { config: None })));
        assert!(matches!(parse_terrain_command("TERRAIN_SAVE"),        Some(IpcCommand::TerrainSave)));
        assert!(matches!(parse_terrain_command("TERRAIN_UNDO"),        Some(IpcCommand::TerrainUndo)));
        assert!(matches!(parse_terrain_command("TERRAIN_REDO"),        Some(IpcCommand::TerrainRedo)));
        assert!(matches!(parse_terrain_command("TERRAIN_STROKE_END"),  Some(IpcCommand::TerrainStrokeEnd)));
        assert!(matches!(parse_terrain_command("TERRAIN_COVER_CLEAR"), Some(IpcCommand::TerrainCoverClear)));
    }

    /// カバー場（I3.1）のシミュレート系 3 コマンドが正しく解釈されること。
    ///
    /// 語頭衝突の回帰テストも兼ねる: `TERRAIN_COVER_SIM_START` と
    /// `TERRAIN_COVER_SIM_STOP` は `TERRAIN_COVER_SIM_ST` まで共通なので、
    /// 前方一致で判定すると取り違えが起きる（両方とも完全一致アームで判定する）。
    #[test]
    fn parses_cover_simulate_commands() {
        assert!(matches!(
            parse_terrain_command("TERRAIN_COVER_SIM_START"),
            Some(IpcCommand::TerrainCoverSimStart)
        ));
        assert!(matches!(
            parse_terrain_command("TERRAIN_COVER_SIM_STOP"),
            Some(IpcCommand::TerrainCoverSimStop)
        ));
        match parse_terrain_command("TERRAIN_COVER_STEP:2.5") {
            Some(IpcCommand::TerrainCoverStep { seconds }) => assert_eq!(seconds, 2.5),
            _ => panic!("秒数付きステップが解釈できない"),
        }
        // 引数の形式が不正なものは受け付けない（黙って 0 秒扱いにしない）。
        assert!(parse_terrain_command("TERRAIN_COVER_STEP:abc").is_none());
    }

    /// プレビュー OFF が「引数付きプレビュー」より先に判定されること（判定順の回帰テスト）。
    #[test]
    fn preview_off_is_matched_before_preview_with_args() {
        assert!(matches!(
            parse_terrain_command("TERRAIN_BRUSH_PREVIEW_OFF"),
            Some(IpcCommand::TerrainBrushPreviewOff)
        ));
        let cmd = parse_terrain_command("TERRAIN_BRUSH_PREVIEW:10,20,3,0.5");
        match cmd {
            Some(IpcCommand::TerrainBrushPreview { screen_x, screen_y, radius, strength }) => {
                assert_eq!(screen_x, 10.0);
                assert_eq!(screen_y, 20.0);
                assert_eq!(radius,    3.0);
                assert_eq!(strength,  0.5);
            }
            _ => panic!("TerrainBrushPreview を期待した"),
        }
    }

    /// ブラシ／ペイントの引数並びが従来どおりであること。
    #[test]
    fn parses_brush_and_paint_arguments() {
        match parse_terrain_command("TERRAIN_BRUSH:1,100,200,4.5,0.25") {
            Some(IpcCommand::TerrainBrush { op, screen_x, screen_y, radius, strength }) => {
                assert_eq!(op, 1);
                assert_eq!(screen_x, 100.0);
                assert_eq!(screen_y, 200.0);
                assert_eq!(radius,   4.5);
                assert_eq!(strength, 0.25);
            }
            _ => panic!("TerrainBrush を期待した"),
        }
        match parse_terrain_command("TERRAIN_PAINT:3,10,20,2,1") {
            Some(IpcCommand::TerrainPaint { layer, .. }) => assert_eq!(layer, 3),
            _ => panic!("TerrainPaint を期待した"),
        }
    }

    /// ブラシ形状マスクの設定コマンドが、**パスを一切分割せずに**受け取れること。
    ///
    /// 本コマンドを別立てにした理由そのものを固定するテストである。
    /// Windows のパスはカンマ・コロン・空白を含みうるので、
    /// カンマ区切りのブラシコマンドへ相乗りさせるとここが壊れる。
    #[test]
    fn brush_mask_parses_path_verbatim() {
        // ─── カンマ・コロンを含む絶対パス（分割してはいけない）───
        let raw = r"C:\assets\brush,01: test.png";
        match parse_terrain_command(&format!("TERRAIN_BRUSH_MASK:{raw}")) {
            Some(IpcCommand::TerrainBrushMask { path }) => assert_eq!(path, raw),
            _ => panic!("TerrainBrushMask を期待した"),
        }
        // ─── 仮想パス ───
        match parse_terrain_command("TERRAIN_BRUSH_MASK:assets://terrain/brush.png") {
            Some(IpcCommand::TerrainBrushMask { path }) => {
                assert_eq!(path, "assets://terrain/brush.png");
            }
            _ => panic!("TerrainBrushMask を期待した"),
        }
        // ─── 空文字は「解除」を意味する正当な値（None にしない）───
        match parse_terrain_command("TERRAIN_BRUSH_MASK:") {
            Some(IpcCommand::TerrainBrushMask { path }) => assert!(path.is_empty()),
            _ => panic!("解除コマンドも TerrainBrushMask として受け取ること"),
        }
    }

    /// 散布ルール実行コマンドが prop_id と seed へ正しく分解されること。
    ///
    /// 空 prop_id（= 全プロップ対象）が「引数不足」と混同されないことが要点。
    #[test]
    fn scatter_rules_parses() {
        // ─── 通常形（ID 指定）───
        match parse_terrain_command("TERRAIN_SCATTER_RULES:grass_field,12345") {
            Some(IpcCommand::TerrainScatterRules { prop_id, seed }) => {
                assert_eq!(prop_id, "grass_field");
                assert_eq!(seed, 12345);
            }
            _ => panic!("TerrainScatterRules を期待した"),
        }

        // ─── 空 prop_id = 全プロップ対象（先頭 split だと壊れる境界）───
        match parse_terrain_command("TERRAIN_SCATTER_RULES:,7") {
            Some(IpcCommand::TerrainScatterRules { prop_id, seed }) => {
                assert_eq!(prop_id, "", "空 prop_id は「全プロップ」を意味する");
                assert_eq!(seed, 7);
            }
            _ => panic!("空 prop_id でも TerrainScatterRules を期待した"),
        }

        // ─── 境界: seed=0 と u64 上限 ───
        match parse_terrain_command("TERRAIN_SCATTER_RULES:g,0") {
            Some(IpcCommand::TerrainScatterRules { seed, .. }) => assert_eq!(seed, 0),
            _ => panic!("seed=0 を期待した"),
        }
        let max = u64::MAX;
        match parse_terrain_command(&format!("TERRAIN_SCATTER_RULES:g,{max}")) {
            Some(IpcCommand::TerrainScatterRules { seed, .. }) => assert_eq!(seed, max),
            _ => panic!("seed=u64::MAX を期待した"),
        }

        // ─── 不正形: seed 欠落／seed が数値でない／u64 溢れ ───
        assert!(parse_terrain_command("TERRAIN_SCATTER_RULES:grass_field").is_none());
        assert!(parse_terrain_command("TERRAIN_SCATTER_RULES:grass_field,abc").is_none());
        assert!(parse_terrain_command("TERRAIN_SCATTER_RULES:g,-1").is_none());
    }

    /// 散布ブラシコマンドが 6 フィールドへ正しく分解されること。
    #[test]
    fn scatter_brush_parses() {
        match parse_terrain_command("TERRAIN_SCATTER_BRUSH:grass_field,100,200,3.5,8,0") {
            Some(IpcCommand::TerrainScatterBrush {
                prop_id, screen_x, screen_y, radius, density, erase,
            }) => {
                assert_eq!(prop_id, "grass_field");
                assert_eq!(screen_x, 100.0);
                assert_eq!(screen_y, 200.0);
                assert_eq!(radius, 3.5);
                assert_eq!(density, 8.0);
                assert!(!erase, "erase=0 は追加");
            }
            _ => panic!("TerrainScatterBrush を期待した"),
        }

        // ─── erase=1 は消去 ───
        match parse_terrain_command("TERRAIN_SCATTER_BRUSH:g,1,2,3,4,1") {
            Some(IpcCommand::TerrainScatterBrush { erase, .. }) => assert!(erase),
            _ => panic!("erase=1 を期待した"),
        }

        // ─── 境界: 半径・密度 0（パースは通る。妥当性は実行側の責務）───
        match parse_terrain_command("TERRAIN_SCATTER_BRUSH:g,0,0,0,0,0") {
            Some(IpcCommand::TerrainScatterBrush { radius, density, .. }) => {
                assert_eq!(radius, 0.0);
                assert_eq!(density, 0.0);
            }
            _ => panic!("0 値でもパースは通ること"),
        }

        // ─── 不正形: フィールド不足／過多／数値でない ───
        assert!(parse_terrain_command("TERRAIN_SCATTER_BRUSH:g,1,2,3,4").is_none());
        assert!(parse_terrain_command("TERRAIN_SCATTER_BRUSH:g,1,2,3,4,0,9").is_none());
        assert!(parse_terrain_command("TERRAIN_SCATTER_BRUSH:g,x,2,3,4,0").is_none());
        assert!(parse_terrain_command("TERRAIN_SCATTER_BRUSH:g,1,2,3,4,x").is_none());
    }

    /// カバーブラシコマンドが 7 フィールドへ正しく分解されること。
    #[test]
    fn cover_brush_parses() {
        match parse_terrain_command("TERRAIN_COVER_BRUSH:snow,100,200,3.5,0.5,0.8,0") {
            Some(IpcCommand::TerrainCoverBrush {
                material_id, screen_x, screen_y, radius, strength, target_amount, erase,
            }) => {
                assert_eq!(material_id, "snow");
                assert_eq!(screen_x, 100.0);
                assert_eq!(screen_y, 200.0);
                assert_eq!(radius, 3.5);
                assert_eq!(strength, 0.5);
                assert_eq!(target_amount, 0.8);
                assert!(!erase, "erase=0 は塗り");
            }
            _ => panic!("TerrainCoverBrush を期待した"),
        }

        // ─── erase=1 は消しゴム（素材 ID は無視されるので空文字も通る）───
        match parse_terrain_command("TERRAIN_COVER_BRUSH:,1,2,3,0.5,0,1") {
            Some(IpcCommand::TerrainCoverBrush { material_id, erase, .. }) => {
                assert_eq!(material_id, "");
                assert!(erase);
            }
            _ => panic!("erase=1 を期待した"),
        }

        // ─── 不正形: フィールド不足／過多／数値でない ───
        assert!(parse_terrain_command("TERRAIN_COVER_BRUSH:snow,1,2,3,0.5,0").is_none());
        assert!(parse_terrain_command("TERRAIN_COVER_BRUSH:snow,1,2,3,0.5,0,0,9").is_none());
        assert!(parse_terrain_command("TERRAIN_COVER_BRUSH:snow,x,2,3,0.5,0,0").is_none());
        assert!(parse_terrain_command("TERRAIN_COVER_BRUSH:snow,1,2,3,0.5,0,x").is_none());
    }

    /// 旧形式のハイトマップはカンマを含むパスでも右端のカンマで分割されること。
    #[test]
    fn heightmap_splits_on_last_comma() {
        match parse_terrain_command(r"TERRAIN_HEIGHTMAP:C:\a,b\map.png,12.5") {
            Some(IpcCommand::TerrainHeightmap { path, height_scale, config }) => {
                assert_eq!(path, r"C:\a,b\map.png");
                assert_eq!(height_scale, 12.5);
                assert_eq!(config, None, "旧形式では構成指定が付かない");
            }
            _ => panic!("TerrainHeightmap を期待した"),
        }
    }

    /// 未知コマンド・引数不正は None（握り潰し）になること。
    #[test]
    fn rejects_unknown_and_malformed_terrain_commands() {
        assert!(parse_terrain_command("TERRAIN_NOPE").is_none());
        // 引数の個数不足（f32×4 が必要なのに 3 個）。
        assert!(parse_terrain_command("TERRAIN_BRUSH_PREVIEW:1,2,3").is_none());
        // height_scale が数値でない。
        assert!(parse_terrain_command("TERRAIN_HEIGHTMAP:map.png,abc").is_none());
    }

    // ── チャンク構成の設定（本機能の追加分）─────────────────────────────

    /// 構成引数つき TERRAIN_INIT が 4 フィールドを正しく取り出すこと。
    #[test]
    fn parses_terrain_init_with_chunk_config() {
        match parse_terrain_command("TERRAIN_INIT:6,8,16,0.25") {
            Some(IpcCommand::TerrainInit { config: Some(c) }) => {
                assert_eq!(c.chunks_x, 6);
                assert_eq!(c.chunks_z, 8);
                assert_eq!(c.chunk_cells, 16);
                assert_eq!(c.voxel_size, 0.25);
            }
            _ => panic!("構成付き TerrainInit を期待した"),
        }
    }

    /// 構成引数の個数不足・非数値は握り潰されること（旧形式へ誤って落ちない）。
    #[test]
    fn rejects_malformed_terrain_init_config() {
        assert!(parse_terrain_command("TERRAIN_INIT:6,8,16").is_none());
        assert!(parse_terrain_command("TERRAIN_INIT:6,8,16,0.25,99").is_none());
        assert!(parse_terrain_command("TERRAIN_INIT:a,b,c,d").is_none());
    }

    /// チャンク追加が i32×4 として読めること（負のチャンク座標も許す）。
    #[test]
    fn parses_terrain_add_chunks() {
        match parse_terrain_command("TERRAIN_ADD_CHUNKS:-2,-3,4,5") {
            Some(IpcCommand::TerrainAddChunks { min_x, min_z, max_x, max_z }) => {
                assert_eq!((min_x, min_z, max_x, max_z), (-2, -3, 4, 5));
            }
            _ => panic!("TerrainAddChunks を期待した"),
        }
        assert!(parse_terrain_command("TERRAIN_ADD_CHUNKS:1,2,3").is_none());
        assert!(parse_terrain_command("TERRAIN_ADD_CHUNKS:1,2,3,x").is_none());
    }

    /// 新形式のハイトマップ（構成 4 + height_scale + path）が読めること。
    /// path は末尾なので、カンマを含むパスでも前 5 フィールドが壊れない。
    #[test]
    fn parses_heightmap_with_chunk_config() {
        match parse_terrain_command(r"TERRAIN_HEIGHTMAP:3,5,16,0.25,20,C:\a,b\map.png") {
            Some(IpcCommand::TerrainHeightmap { path, height_scale, config: Some(c) }) => {
                assert_eq!(path, r"C:\a,b\map.png", "path は最後のフィールド全部（カンマ込み）");
                assert_eq!(height_scale, 20.0);
                assert_eq!(c.chunks_x, 3);
                assert_eq!(c.chunks_z, 5);
                assert_eq!(c.chunk_cells, 16);
                assert_eq!(c.voxel_size, 0.25);
            }
            _ => panic!("構成付き TerrainHeightmap を期待した"),
        }
    }

    /// 新形式として読めない入力は旧形式へフォールバックすること（後方互換の要）。
    /// Windows の絶対パスは先頭フィールドが数値にならないため、必ず旧形式で解釈される。
    #[test]
    fn heightmap_falls_back_to_legacy_form() {
        match parse_terrain_command(r"TERRAIN_HEIGHTMAP:C:\maps\a,b,c,d,e\hm.png,10") {
            Some(IpcCommand::TerrainHeightmap { path, height_scale, config: None }) => {
                assert_eq!(path, r"C:\maps\a,b,c,d,e\hm.png");
                assert_eq!(height_scale, 10.0);
            }
            _ => panic!("旧形式の TerrainHeightmap を期待した"),
        }
    }
}
