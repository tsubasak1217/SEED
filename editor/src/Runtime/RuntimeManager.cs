using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using SEEDEditor.Ipc;

namespace SEEDEditor.Runtime;

// ============================================================
//  EditorState
// ============================================================

// Launching: Play ボタン押下後、Play ランタイムの起動シーケンス（プロセス起動〜
//            ウィンドウ／パイプ準備）が進行中の過渡状態。この間に Stop を押せるよう
//            にする（起動キャンセル用）とともに、Play ボタンの再入をブロックする。
public enum EditorState { Idle, Building, Launching, Edit, Play, Pause }

// ============================================================
//  RuntimeManager
// ============================================================

/// <summary>
/// Runtime プロセスのライフサイクル・IPC・状態遷移を管理する。
///
/// 状態遷移:
///   Idle  ──[StartEdit]──▶ Edit
///   Edit  ──[Play]────────▶ Play   ← Edit ランタイムは非表示にして保持（GPU コンテキスト維持）
///   Play  ──[最小化検知]──▶ Pause  ← WinEventHook
///   Pause ──[Resume]──────▶ Play
///   Play/Pause ──[Stop]───▶ Edit   ← Play を非表示保持（Kill しない）→ 保存 Edit を ShowWindow で即復元
///   Play/Pause ──[閉じる]─▶ Edit   ← Play 自己終了（Kill 済み）→ 保存 Edit を ShowWindow で即復元
///
/// Edit ランタイムを Play 中も生かしておく理由:
///   デバッグビルドでは Play 終了→Edit 再起動時に GPU コンテキストの再初期化に
///   約 22 秒かかる（wgpu/DX12）。非表示にするだけなら GPU は維持されたまま
///   ShowWindow で即座に表示でき、ユーザーへの待機をゼロにできる。
///
/// Play ランタイムを Stop 後も生かしておく理由（常駐 Play）:
///   新規 Play プロセスは毎回 GPU 初期化＋モデル再パースのコールドスタートで数十秒かかる。
///   Stop 時に Kill せず PAUSE_RENDER で描画・シミュレーションを止めて非表示保持し、
///   2 回目以降の Play では保持プロセスへ LOAD_SCENE を送ってシーンだけ差し替える
///   （GPU・モデルキャッシュが温かいため数秒で再生できる）。保持プロセスがクラッシュ／
///   終了していれば従来どおり新規起動へフォールバックする。初回 Play はコールドのまま。
/// </summary>
public sealed class RuntimeManager : IDisposable
{
    // ── Win32 デリゲート ───────────────────────────────────────
    private delegate void WinEventProc(
        IntPtr hHook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint thread, uint time);

    // ── 定数 ──────────────────────────────────────────────────
    private const uint EVENT_SYSTEM_MOVESIZESTART = 0x000A;
    private const uint EVENT_SYSTEM_MOVESIZEEND   = 0x000B;
    private const uint EVENT_SYSTEM_MINIMIZESTART = 0x0016;
    private const uint WINEVENT_OUTOFCONTEXT      = 0x0000;
    private const int  GWL_STYLE                  = -16;
    private const int  GWL_EXSTYLE                = -20;
    private const int  WS_CHILD                   = 0x40000000;
    private const int  WS_POPUP                   = unchecked((int)0x80000000);
    private const int  WS_CAPTION                 = 0x00C00000;
    private const int  WS_THICKFRAME              = 0x00040000;
    private const int  WS_OVERLAPPEDWINDOW        = 0x00CF0000;
    private const int  WS_VISIBLE                 = 0x10000000;
    private const int  WS_EX_APPWINDOW            = 0x00040000;
    private const int  SW_HIDE                    = 0;
    private const int  SW_SHOW                    = 5;
    private const int  SW_RESTORE                 = 9;
    private const int  SW_SHOWDEFAULT             = 10;
    private const uint SWP_NOMOVE                 = 0x0002;
    private const uint SWP_NOSIZE                 = 0x0001;
    private const uint SWP_NOZORDER               = 0x0004;
    private const uint SWP_FRAMECHANGED           = 0x0020;

    // ── フィールド ─────────────────────────────────────────────
    private readonly string               _runtimeExePath;
    /// <summary>Playモードで --assets-root として渡すアセットルートパス。</summary>
    public string? AssetsPath { get; set; }

    /// <summary>エディタリソースディレクトリのパス（--editor-resources として渡す）。</summary>
    public string? EditorResourcesPath { get; set; }

    /// <summary>
    /// Play 時にロードするシーンパス。
    /// null の場合はランタイムが project_settings.json の start_scene を使う。
    /// </summary>
    public string? PlayScenePath { get; set; }

    /// <summary>
    /// Play 起動時に --play-collider-draw=1 フラグを渡すかどうか。
    /// true の場合、Play ランタイムはコライダーワイヤーフレームを最初から描画する
    /// （SyncViewportSettings の到着遅延を回避するための先行フラグ）。
    /// </summary>
    public bool PlayColliderDraw { get; set; }

    /// <summary>
    /// プロファイラ計測の購読状態（プロファイラパネルが表示中のときのみ true）。
    /// ランタイム側は SET_PROFILER で明示的に ON にしない限り計測を止めたままにするため、
    /// このフラグは「次に接続し直したランタイムへ再送すべき値」としても使う
    /// （<see cref="SetProfilerEnabled"/> 参照）。
    /// </summary>
    public bool ProfilerEnabled { get; private set; }

    private readonly RuntimeSourceWatcher? _sourceWatcher;
    private Process?                      _process;
    private PipeServer?                   _pipe;
    private IntPtr                        _runtimeHwnd;
    private IntPtr                        _viewportContainerHwnd;
    private EditorState                   _state = EditorState.Idle;
    private Win32.RECT                    _runtimeRectBeforeEmbed;

    // WinEventHook（GC 対策で delegate を保持）
    private WinEventProc? _winEventDelegate;
    private IntPtr        _winEventHook;

    // Play 中に保存しておく Edit ランタイムのフィールド（GPU 再初期化を避けるための保持）
    private Process?    _savedEditProcess;
    private PipeServer? _savedEditPipe;
    private IntPtr      _savedEditHwnd;

    // Stop 後も Kill せず常駐保持する Play ランタイムのフィールド。
    // 2 回目以降の Play では、この保持プロセスへ LOAD_SCENE を送ってシーンだけ差し替える
    // ことで、GPU 初期化（約 22 秒）とモデル再パースのコールドスタートを回避する。
    // 初回 Play・保持プロセスがクラッシュ/終了している場合は従来どおり新規起動する。
    private Process?    _persistentPlayProcess;
    private PipeServer? _persistentPlayPipe;
    private IntPtr      _persistentPlayHwnd;

    /// <summary>
    /// 埋め込みインプレース Play（フェーズ2）を使うかどうか。MainWindow が Play 開始前に設定する。
    /// true のとき、PlayAsync は別プロセスを起動せず現 Edit ランタイムへ ENTER_PLAY を送る。
    /// </summary>
    public bool EmbeddedPlay { get; set; }

    /// <summary>
    /// 現在、埋め込みインプレース Play 中か（ENTER_PLAY 済みで EXIT_PLAY 前）。
    /// Stop / OnRuntimeExited の分岐と、二重遷移防止に使う。
    /// </summary>
    private bool _inEmbeddedPlay;

    /// <summary>
    /// Play 起動シーケンス（プロセス起動〜Play 遷移、または ENTER_PLAY〜PLAY_ENTERED）が
    /// 進行中かどうか。次の 2 つの不具合対策に使う:
    ///   - 不具合2（プロセス単一性）: PlayAsync 入口の再入ガード。起動処理中に再度呼ばれても
    ///     早期 return して 2 つ目のランタイムプロセスを起動しない（実行ボタン連打対策）。
    ///   - 不具合1（起動中 Stop）: Stop が起動中かどうかを判定し、起動をキャンセルするために使う。
    /// UI スレッドからのみ読み書きするため lock 不要（PlayAsync/Stop はともに UI スレッド）。
    /// </summary>
    private bool _isLaunching;

    /// <summary>
    /// ウィンドウ Play の起動シーケンス（LaunchAsync）をキャンセルするためのトークンソース。
    /// 起動中に Stop が押されたら Cancel し、LaunchAsync 内の await（クラッシュ検知待機・
    /// パイプ接続待機）を中断させて Play への遷移を止め、起動途中のプロセスを終了させる。
    /// </summary>
    private CancellationTokenSource? _launchCts;

    /// <summary>
    /// ウィンドウ Play の「ウィンドウ準備完了（READY:hwnd 受信）」を待つための完了ソース（方針A）。
    ///
    /// ウィンドウ Play のランタイムは GPU 初期化＋シーンロード（数十秒かかりうる）を終えてから
    /// ウィンドウを set_visible し、その直後に READY:{hwnd} を送ってくる。一方パイプ接続は
    /// その前（約 500ms）に成立するため、従来はパイプ接続時点で Play へ遷移していた。すると
    /// _runtimeHwnd 未確定のまま Play になり、ロード中に Stop すると常駐保持経路で SW_HIDE が
    /// 空振りし、ロード完了後にランタイムが自前でウィンドウを表示して「応答なし白画面」が残った。
    ///
    /// これを避けるため Play 遷移を READY 受信まで遅延し、その間は Launching を維持する。
    /// LaunchAsync がこの TCS を await し、OnPipeMessage の READY 受信で TrySetResult(true)、
    /// READY 前にプロセスが終了した場合は OnRuntimeExited が TrySetResult(false) で解く。
    /// ウィンドウ Play の起動シーケンス中のみ非 null。パイプ受信スレッド／UI スレッド双方から
    /// TrySetResult されるため TCS の Try 系のみ使用する。
    /// </summary>
    private TaskCompletionSource<bool>? _playWindowReadyTcs;

    /// <summary>
    /// 埋め込みインプレース Play セッション中かどうか（ENTER_PLAY 送信〜PLAY_EXITED/プロセス終了まで）。
    /// UI 側の状態遷移処理（ApplyUiState 等）が「ビューポートホストを隠してよいか」の判定に使う。
    /// チェックボックス値（EmbeddedPlay）ではなくセッション実態を返す点に注意
    /// （Play 中にチェックを切り替えられても表示制御が破綻しないようにするため）。
    /// </summary>
    public bool InEmbeddedPlay => _inEmbeddedPlay;

    // ── 公開プロパティ・イベント ────────────────────────────────

    public EditorState State => _state;

    /// <summary>
    /// 実行中ランタイムが内蔵デバッガのブレークポイントで停止（メインスレッド凍結）中か。
    /// MainWindow がデバッグセッションの停止/継続に合わせて設定する。
    ///
    /// 凍結中はランタイムのメッセージポンプが止まっているため、そのウィンドウへの
    /// 同期的な Win32 操作（SetParent / SetWindowPos 等）は呼び出し側スレッドごと
    /// ブロックしてデッドロックする。これを避けるためウィンドウ埋め込み等を抑止する。
    /// </summary>
    public bool DebuggerSuspended { get; set; }

    /// <summary>
    /// 現在動作中のランタイムプロセス ID（VS デバッガアタッチ用）。
    /// Play 中は Play ランタイム、Edit 中は Edit ランタイムの PID。
    /// 未起動・終了済みなら null。
    /// </summary>
    public int? CurrentProcessId
        => _process is { HasExited: false } p ? p.Id : null;

    /// <summary>ゲームウィンドウ HWND（READY 受信後に確定）。</summary>
    public nint RuntimeHwnd => (nint)_runtimeHwnd;

    /// <summary>状態変化時に発火する。UI スレッドから呼ばれる。</summary>
    public event Action<EditorState>? StateChanged;

    /// <summary>ゲームウィンドウの HWND が確定したときに発火する。</summary>
    public event Action<nint>? RuntimeHwndAvailable;

    /// <summary>ゲームウィンドウのドラッグ / リサイズ開始時に発火する。</summary>
    public event Action? RuntimeMoveStart;

    /// <summary>ゲームウィンドウのドラッグ / リサイズ終了時に発火する。</summary>
    public event Action? RuntimeMoveEnd;

    /// <summary>ヒエラルキーが更新されたときに発火する（JSON 文字列）。</summary>
    public event Action<string>? HierarchyUpdated;

    /// <summary>選択インスタンスが変化したときに発火する（-1 = 選択なし）。</summary>
    public event Action<int>? SelectionChanged;

    /// <summary>ビューポートで複数選択されたときに発火する（インスタンスインデックスのリスト）。</summary>
    public event Action<IReadOnlyList<int>>? SelectionMultiChanged;

    /// <summary>
    /// ユーザースクリプトのホットリロード（再コンパイル＋全 ScriptComponent 再生成）が
    /// **成功**したときに発火する（引数 = コンパイルされたスクリプト型数）。
    ///
    /// 受け手はスクリプト型キャッシュ（[SerializeField] 定義の抽出結果）を捨てる。
    /// 保存した .cs 以外（基底クラスや [Serializable] ネスト型を共有する別スクリプト）の
    /// 定義も同時に変わりうるため、破棄は**全件**が正しい。
    /// 失敗時は発火しない（旧定義のまま表示を保つ）。
    /// </summary>
    public event Action<int>? ScriptsReloaded;

    /// <summary>シーン保存完了時に発火する（ok=true: 成功, ok=false: 失敗メッセージ付き）。</summary>
    public event Action<bool, string>? SaveCompleted;

    /// <summary>ビューポート上で短押し右クリックされたときに発火する。</summary>
    public event Action? ViewportContextMenuRequested;

    /// <summary>
    /// ランタイムが最初のフレームを実際に描画したときに発火する。
    /// デバッグビルドのランタイムのみ送信する（FIRST_FRAME メッセージ）。
    /// </summary>
    public event Action? FirstFrameReady;

    /// <summary>アクターデータが返ってきたときに発火する（JSON 文字列）。</summary>
    public event Action<string>? ActorDataReceived;

    /// <summary>アクター編集モードでコンポーネント一覧が返ってきたときに発火する（JSON 文字列）。</summary>
    public event Action<string>? ActorComponentsReceived;

    /// <summary>
    /// 水面シェーダの <c>@ref</c> パラメータに繋げられるバインド元候補が返ってきたときに発火する
    /// （GET_BINDABLE_SOURCES への応答。引数は JSON 文字列）。
    ///
    /// JSON は <c>[{"slot":"…","label":"…","variables":[{"name":"…","label":"…"}]}]</c>。
    /// **候補の正典はランタイム側**なので、エディタは受け取った配列を並べるだけでよい。
    /// </summary>
    public event Action<string>? BindableSourcesReceived;

    /// <summary>
    /// シーン既定シェーディングアセットのパラメータ一覧が返ってきたときに発火する
    /// （GET_SCENE_SHADING_PARAMS への応答、および値変更・Undo 後の自動再送）。
    ///
    /// JSON はインスペクタの水面パラメータ行と**同一のワイヤ表現**
    /// （<c>[{"name","type","label","min","max","reset","ref","binding","binding_ok","value"}]</c>）。
    /// </summary>
    public event Action<string>? SceneShadingParamsReceived;

    /// <summary>デバッグカメラ状態が返ってきたときに発火する（CAM_STATE メッセージ本体）。</summary>
    public event Action<string>? CameraStateReceived;

    /// <summary>
    /// 編集時物理タイムライン状態が更新されたときに発火する。
    /// 引数: "paused,at_latest,current_frame,total_frames,time_sec"
    /// </summary>
    public event Action<string>? EditPhysicsStateReceived;

    /// <summary>ランタイム側でシーンが変更されたときに発火する（ギズモドラッグ完了など）。</summary>
    public event Action? SceneModified;

    /// <summary>
    /// ランタイム側のホットキー（Q/W/E/T）でツールモードが変わったときに発火する。
    /// 引数: "SELECT" / "MOVE" / "ROTATE" / "SCALE"。ツールバーの表示同期に使う。
    /// </summary>
    public event Action<string>? ToolModeChanged;

    /// <summary>
    /// モーダルトランスフォーム（Blender 風 G/R/S）の進行状態が変わったときに発火する。
    /// 引数: true = 進行中 / false = 終了（確定・取消・開始拒否）。
    /// エディタのキーフックが「X/Y/Z・Enter・Esc をモーダルへ回すか」の判断に使う。
    /// </summary>
    public event Action<bool>? ModalTransformStateChanged;

    /// <summary>
    /// ロジック配置の<b>配置モード</b>（カーソル追従プレビュー → クリック確定）の
    /// 進行状態が変わったときに発火する。
    /// 引数: true = 進行中 / false = 終了（確定・取消・自動取消）。
    ///
    /// エディタは「Esc を削除ダイアログではなく配置の取消へ回す」判断と、
    /// 操作ヒントの表示に使う。マウス操作はランタイムの子ウィンドウが直接受け取るので、
    /// モーダルトランスフォームのようなグローバルマウスフックは要らない。
    /// </summary>
    public event Action<bool>? PlacementStateChanged;

    /// <summary>
    /// ロジック配置の配置モードで、半径ドラッグにより確定した半径 [m]。
    ///
    /// 円形パターンをビューポート上でドラッグして決めた値をダイアログの
    /// 前回値へ書き戻し、次に開いたときの初期値にするために使う。
    /// </summary>
    public event Action<float>? PlacementRadiusChanged;

    /// <summary>アクター編集モードに切り替わったときに発火する。</summary>
    public event Action? ActorEditStarted;

    /// <summary>アクター編集モードが終了して通常シーンに戻ったときに発火する。</summary>
    public event Action? ActorEditEnded;

    /// <summary>
    /// キャンバス編集タブがランタイム側で開始されたときに発火する
    /// （EDIT_CANVAS_BEGIN への応答）。引数は (世界線, ルートが2Dアクタか, アクター名)。
    /// </summary>
    public event Action<uint, bool, string>? CanvasEditStarted;

    /// <summary>世界線切り替え情報が返ってきたときに発火する（デバッグログ用）。</summary>
    public event Action<string>? WorldLineInfoReceived;

    /// <summary>ランタイム側のFPSが更新されたときに発火する（0.5秒ごと）。</summary>
    public event Action<float>? FpsReceived;

    /// <summary>
    /// ランタイム側のプロファイラ計測レポートを受信したときに発火する（0.5秒ごと、JSON 文字列）。
    /// SET_PROFILER で購読を ON にしている間のみランタイムから送られてくる。
    /// </summary>
    public event Action<string>? ProfilerReportReceived;

    /// <summary>
    /// ロード済みプラグイン一覧が返ってきたときに発火する（JSON 文字列）。
    /// フォーマット: [{"name":"...","version":"...","description":"..."},...]
    /// </summary>
    public event Action<string>? PluginListReceived;

    /// <summary>
    /// シーン情報が返ってきたときに発火する（JSON 文字列）。
    /// GET_SCENE_INFO コマンドへの応答として受信する。
    /// フォーマット: ActorData[] のシリアライズ JSON
    /// </summary>
    public event Action<string>? SceneInfoReceived;

    /// <summary>アクターファイル書き出し完了通知。true=成功（保存パス）/ false=失敗（エラーメッセージ）。</summary>
    public event Action<bool, string>? ExportActorCompleted;

    /// <summary>地形の初期化完了通知（TERRAIN_INIT_OK）。</summary>
    public event Action? TerrainInitCompleted;

    /// <summary>地形の保存完了通知（TERRAIN_SAVE_OK:count / TERRAIN_SAVE_ERROR:msg）。true=成功（引数=保存チャンク数）/ false=失敗（引数=エラーメッセージ）。</summary>
    public event Action<bool, string>? TerrainSaveCompleted;

    /// <summary>
    /// 地形の別名保存完了通知（TERRAIN_SAVE_AS_OK:dir,count / TERRAIN_SAVE_AS_ERROR:msg）。
    /// true=成功（第2引数=保存先の地形フォルダ参照・第3引数=保存チャンク数）/
    /// false=失敗（第2引数=エラーメッセージ・第3引数は空）。
    /// </summary>
    public event Action<bool, string, string>? TerrainSaveAsCompleted;

    /// <summary>
    /// 現在の地形フォルダ参照の通知（TERRAIN_DIR:dir）。
    /// アセットルート相対のパス（例 `terrain/Scene1`）。地形の初期化・シーンロード・
    /// 別名保存のたびにランタイムから push される。
    /// </summary>
    public event Action<string>? TerrainDirChanged;

    /// <summary>地形ブラシ結果通知。true=命中（引数="hx,hy,hz"）/ false=非命中（引数="")。</summary>
    public event Action<bool, string>? TerrainBrushResult;

    /// <summary>
    /// チャンク当たり判定トグルの結果通知
    /// （TERRAIN_COLLISION_OK:x,y,z,enabled / TERRAIN_COLLISION_MISS）。
    /// true=命中（引数="x,y,z,0|1"。末尾 1 = 当たり判定あり）/ false=非命中（引数=""）。
    /// </summary>
    public event Action<bool, string>? TerrainCollisionResult;

    /// <summary>
    /// その場デシメートの完了通知
    /// （TERRAIN_DECIMATE_OK:strength,before,after / TERRAIN_DECIMATE_ERROR:msg）。
    /// true=成功（引数="強度,適用前頂点数,適用後頂点数"）/ false=失敗（引数=エラーメッセージ）。
    /// </summary>
    public event Action<bool, string>? TerrainDecimateCompleted;

    /// <summary>ハイトマップ反映完了通知（TERRAIN_HEIGHTMAP_OK:ms / TERRAIN_HEIGHTMAP_ERROR:msg）。true=成功（引数=処理時間ms）/ false=失敗（引数=エラーメッセージ）。</summary>
    public event Action<bool, string>? TerrainHeightmapCompleted;

    /// <summary>
    /// チャンク追加完了通知（TERRAIN_ADD_CHUNKS_OK:追加数,再メッシュ数 / TERRAIN_ADD_CHUNKS_ERROR:msg）。
    /// true=成功（引数="追加チャンク数,再メッシュしたチャンク数"）/ false=失敗（引数=エラーメッセージ）。
    /// </summary>
    public event Action<bool, string>? TerrainAddChunksCompleted;

    /// <summary>
    /// 散布完了通知（TERRAIN_SCATTER_OK:総インスタンス数 / TERRAIN_SCATTER_ERROR:msg）。
    /// true=成功（引数=散布された総インスタンス数）/ false=失敗（引数=エラーメッセージ）。
    /// ルールによる再散布（TERRAIN_SCATTER_RULES）への応答として受信する。
    /// </summary>
    public event Action<bool, string>? TerrainScatterCompleted;

    /// <summary>
    /// WGSL シェーディングアセットの検証結果通知（WGSL_DIAG:request_id,json_array）。
    /// 第 1 引数 = 依頼時の request_id、第 2 引数 = 診断の JSON 配列文字列（成功時は "[]"）。
    /// 相関・解釈は WgslValidationService が行う（本クラスは配送のみ）。
    /// </summary>
    public event Action<long, string>? WgslDiagnosticsReceived;

    /// <summary>
    /// 地表カバー場のリアルタイム連続シミュレートの稼働状態が変わった通知
    /// （TERRAIN_COVER_SIM_STARTED = true / TERRAIN_COVER_SIM_STOPPED = false）。
    ///
    /// 停止通知はユーザーの停止操作以外（全消去・Play 開始）でも飛ぶ。
    /// インスペクタの再生/停止トグルはこの通知だけを真実として表示を合わせること
    /// （送信直後に自前でトグルを反転させるとランタイム側の実状態とズレる）。
    /// </summary>
    public event Action<bool>? TerrainCoverSimRunningChanged;

    /// <summary>
    /// ビューポート上で ControlPoint の点が選択された通知
    /// （CONTROL_POINT_SELECTED:{actorDfsId},{slotIdx},{index}）。
    /// 引数は (actorDfsId, slotIdx, index)。インスペクタのリスト行ハイライトに使う。
    /// </summary>
    public event Action<int, int, int>? ControlPointSelected;

    /// <summary>ControlPoint の点選択が解除された通知（CONTROL_POINT_DESELECTED）。</summary>
    public event Action? ControlPointDeselected;

    // ── コンストラクタ ─────────────────────────────────────────

    public RuntimeManager(string runtimeExePath)
    {
        _runtimeExePath = runtimeExePath;

        var sourceDir = ResolveRuntimeSourceDir(runtimeExePath);
        if (sourceDir is not null)
        {
            _sourceWatcher = new RuntimeSourceWatcher(sourceDir, runtimeExePath);
            EditorLog.Write($"RuntimeSourceWatcher 開始 — dir={sourceDir}  isDirty={_sourceWatcher.IsDirty}");
        }
        else
        {
            EditorLog.Write($"RuntimeSourceWatcher: Cargo.toml が見つからないためスキップ (exe={runtimeExePath})");
        }
    }

    // ── 公開: 状態遷移 ─────────────────────────────────────────

    /// <summary>Edit モードで Runtime を起動する（エディタ起動時に呼ぶ）。</summary>
    public async Task StartEditAsync(IntPtr viewportContainerHwnd)
    {
        _viewportContainerHwnd = viewportContainerHwnd;

        // 開発時のみ: ソースに変更がある場合だけビルドする
        if (_sourceWatcher is not null && _sourceWatcher.IsDirty)
        {
            // ビルド前に旧プロセスを確実に終了（EXE ファイルのロック解除）
            KillStaleRuntimeProcesses();

            ChangeState(EditorState.Building);
            var sourceDir = ResolveRuntimeSourceDir(_runtimeExePath)!;
            var ok = await BuildAsync(sourceDir);
            if (!ok)
            {
                ChangeState(EditorState.Idle);
                throw new InvalidOperationException(
                    "cargo build failed. ログを確認してください:\n" +
                    Path.GetFullPath("logs/SEEDEditor.log"));
            }
            _sourceWatcher.MarkClean();
        }
        else if (_sourceWatcher is not null)
        {
            EditorLog.Write("StartEditAsync — ソース変更なし、ビルドをスキップ");
        }

        await LaunchAsync(editMode: true);
    }

    /// <summary>
    /// Play ボタン: Edit ランタイムをウィンドウ非表示にして保持し、Play ランタイムを開始する。
    /// プロセスを終了しないことで GPU コンテキストを維持し、Play 終了後に即復元できるようにする。
    ///
    /// Play ランタイムの起動は次の 2 通り:
    ///   - 常駐 Play プロセスが生きていれば再利用（LOAD_SCENE でシーン差し替え・数秒）。
    ///   - 初回 or 常駐消滅時は新規起動（コールドスタート・数十秒）。
    /// </summary>
    public async Task PlayAsync()
    {
        // ── 再入防止ガード（不具合2: Play ランタイムプロセスの単一性保証）──────────
        // 実行ボタンの連打・イベントの多重発火で PlayAsync が重ねて呼ばれても、
        // 2 つ目のランタイムプロセスを起動しないように早期 return する。
        //   ・_isLaunching: 起動シーケンス進行中（ウィンドウ Play / 埋め込み Play 共通）
        //   ・_state Play/Pause: 既に Play プロセスが生存しており再生中
        // 本質的なガードは（UI 側の連打対策に依存せず）ここ RuntimeManager 側に置く。
        if (_isLaunching)
        {
            EditorLog.Write("PlayAsync — 既に起動処理中のため無視（多重起動防止）");
            return;
        }
        if (_state == EditorState.Play || _state == EditorState.Pause)
        {
            EditorLog.Write($"PlayAsync — 既に {_state} 中のため無視（多重起動防止）");
            return;
        }

        // ── 埋め込みインプレース Play（フェーズ2）─────────────────────────────
        // 現 Edit ランタイムが生きていれば、別プロセスを起動せず ENTER_PLAY で
        // その場で Play 化する。地形・散布・GPU リソースを作り直さないため即座に再生できる。
        // EnterPlayEmbedded が _isLaunching を立て、PLAY_ENTERED 受信時に降ろす。
        if (EmbeddedPlay && _process is { HasExited: false } && _state == EditorState.Edit)
        {
            EnterPlayEmbedded();
            return;
        }

        // ── ここから先はウィンドウ Play（別プロセス起動 or 常駐再利用）─────────────
        // 起動シーケンス開始を宣言する。以降 Stop されたらこのフラグ / トークンで検知する。
        _isLaunching = true;
        _launchCts?.Dispose();
        _launchCts = new CancellationTokenSource();
        var launchToken = _launchCts.Token;

        try
        {
            // Edit ランタイムをフィールドに保存（プロセスは生かしたまま）
            _savedEditProcess = _process;
            _savedEditPipe    = _pipe;
            _savedEditHwnd    = _runtimeHwnd;

            // Play 中に Edit プロセスが終了しても OnRuntimeExited が誤発火しないよう購読解除
            if (_savedEditProcess is not null)
                _savedEditProcess.Exited -= OnRuntimeExited;

            // Edit パイプのメッセージも一時停止
            if (_savedEditPipe is not null)
                _savedEditPipe.MessageReceived -= OnPipeMessage;

            // Edit ウィンドウを非表示（プロセスは維持）
            if (_savedEditHwnd != IntPtr.Zero)
                Win32.ShowWindow(_savedEditHwnd, SW_HIDE);

            // 現フィールドをリセット（再利用 / 新規起動が Play 用に設定する）
            _process     = null;
            _pipe        = null;
            _runtimeHwnd = IntPtr.Zero;

            // ── 常駐 Play プロセスの再利用判定 ─────────────────────────────
            // 保持プロセスが生きていて、かつ再ロードすべきシーンパスが確定している場合のみ
            // 再利用する。PlayScenePath が null（「開始シーンからプレイ」時はエディタ側が
            // 開始シーンの実パスを持たない）のときは LOAD_SCENE を送れないため新規起動する。
            var canReuse = _persistentPlayProcess is { HasExited: false }
                        && !string.IsNullOrEmpty(PlayScenePath);
            if (canReuse)
            {
                EditorLog.Write("PlayAsync — 常駐 Play プロセスを再利用（LOAD_SCENE で高速再生）");
                ReusePersistentPlayRuntime();
                return;
            }

            // 保持プロセスが消滅していた（クラッシュ/終了）場合は破棄してから新規起動する
            if (_persistentPlayProcess is not null)
            {
                EditorLog.Write("PlayAsync — 常駐 Play プロセスが消滅、または再ロード先シーン未確定。新規起動へフォールバック");
                DisposePersistentPlayRuntime(killProcess: true);
            }
            else
            {
                EditorLog.Write("PlayAsync — 常駐 Play なし。新規起動（コールドスタート）");
            }

            await LaunchAsync(editMode: false, launchToken);
        }
        finally
        {
            // 起動シーケンス終了（成功・キャンセル・失敗いずれも）でフラグを必ず降ろす。
            // これによりクラッシュ即時終了で例外が飛んでもガードが解除され、次の Play が可能になる。
            // 埋め込み Play はこの経路を通らない（別ライフサイクル: PLAY_ENTERED で降ろす）。
            _isLaunching = false;
        }
    }

    /// <summary>
    /// 埋め込みインプレース Play を開始する（フェーズ2）。
    /// 現 Edit ランタイム（シーンパネルに埋め込み済み）へ ENTER_PLAY を送り、その場で
    /// Play 化する。ウィンドウの付け替え・別プロセス起動・シーン再ロードは一切行わない。
    /// 状態遷移（Edit→Play）は ENTER_PLAY への応答 PLAY_ENTERED を OnPipeMessage が受けて行う。
    /// UI スレッドから呼ぶこと。
    /// </summary>
    private void EnterPlayEmbedded()
    {
        EditorLog.Write("EnterPlayEmbedded — ENTER_PLAY 送信（地形・散布・GPU を保持したまま Play 化）");
        // ENTER_PLAY の二重送信を防ぐ（不具合2）。PLAY_ENTERED 受信まで起動中とみなし、
        // PlayAsync 入口の _isLaunching ガードで再入をブロックする。
        _isLaunching    = true;
        _inEmbeddedPlay = true;
        // クラッシュ再起動カウンタはセッション単位でリセットしておく。
        _restartCount = 0;
        _pipe?.Send("ENTER_PLAY");
        // コライダー描画フラグを同期する（ウィンドウ Play の --play-collider-draw=1 に相当）。
        _pipe?.Send($"SET_PLAY_COLLIDER_DRAW:{(PlayColliderDraw ? 1 : 0)}");
        // プロファイラ購読状態を同期する（新しいランタイムは常に OFF スタートのため、
        // パネルが開いたままなら再送しないと Play 後に計測データが止まる）。
        _pipe?.Send($"SET_PROFILER:{(ProfilerEnabled ? 1 : 0)}");
        // 状態遷移とフォーカスは PLAY_ENTERED 受信時に MainWindow 側で行う。
    }

    /// <summary>
    /// 埋め込みインプレース Play を停止して Edit へ戻す（フェーズ2）。
    /// ランタイムへ EXIT_PLAY を送るだけで、ウィンドウ操作・プロセス破棄は行わない。
    /// 状態遷移（Play→Edit）は応答 PLAY_EXITED を OnPipeMessage が受けて行う。
    /// </summary>
    private void StopEmbeddedPlay()
    {
        EditorLog.Write("StopEmbeddedPlay — EXIT_PLAY 送信（アクター状態を復元して Edit 復帰）");
        _pipe?.Send("EXIT_PLAY");
        // _inEmbeddedPlay=false / ChangeState(Edit) は PLAY_EXITED 受信時に行う。
    }

    /// <summary>
    /// 常駐保持していた Play ランタイムを再利用して再生を再開する。
    /// RESUME_RENDER で描画・シミュレーションを再開し、LOAD_SCENE でシーンを差し替える
    /// （ランタイム側 LOAD_SCENE の Play モード処理が物理再構築・パーティクル解放・
    ///   スクリプト再生成・ゲーム内時間リセットを行う）。UI スレッドから呼ぶこと。
    /// </summary>
    private void ReusePersistentPlayRuntime()
    {
        EditorLog.Write($"ReusePersistentPlayRuntime — hwnd=0x{_persistentPlayHwnd:X}  scene={PlayScenePath}");

        // フィールドを保持 Play から復元する
        _process     = _persistentPlayProcess;
        _pipe        = _persistentPlayPipe;
        _runtimeHwnd = _persistentPlayHwnd;

        _persistentPlayProcess = null;
        _persistentPlayPipe    = null;
        _persistentPlayHwnd    = IntPtr.Zero;

        // イベントを再購読する（保持中は誤発火防止のため解除していた）
        if (_process is not null)
            _process.Exited += OnRuntimeExited;
        if (_pipe is not null)
            _pipe.MessageReceived += OnPipeMessage;

        // 描画・シミュレーションを再開し、シーンを差し替える。
        // RESUME_RENDER と LOAD_SCENE は同一フレームの process_ipc でまとめて処理される。
        _pipe?.Send("RESUME_RENDER");
        _pipe?.Send($"LOAD_SCENE:{PlayScenePath}");
        // コライダー描画フラグを同期する（新規起動時の --play-collider-draw=1 に相当）
        _pipe?.Send($"SET_PLAY_COLLIDER_DRAW:{(PlayColliderDraw ? 1 : 0)}");
        // プロファイラ購読状態を同期する（保持 Play ランタイムは非表示中に OFF 化されている
        // 可能性があるため、パネルが開いたままなら明示的に再送する）。
        _pipe?.Send($"SET_PROFILER:{(ProfilerEnabled ? 1 : 0)}");

        // ウィンドウを再表示して前面化する
        if (_runtimeHwnd != IntPtr.Zero)
        {
            Win32.ShowWindow(_runtimeHwnd, SW_SHOW);
            Win32.SetForegroundWindow(_runtimeHwnd);
        }

        // クラッシュカウントをリセットする
        _restartCount    = 0;
        _intentionalStop = false;

        // Play 状態へ遷移する（UI 更新・自動デバッガアタッチのトリガ）。
        // 保持プロセスは同一 PID のため、デバッガは同 PID へ再アタッチされる。
        ChangeState(EditorState.Play);
        EditorLog.Write("ReusePersistentPlayRuntime — State changed to Play（常駐再利用）");

        // 最小化検知フックを再設置する（新規 Play 起動時と同じ）
        InstallMinimizeHook();

        // HWND 依存の後処理（SyncViewportSettings・カーソルクランプ等）を再実行する
        RuntimeHwndAvailable?.Invoke((nint)_runtimeHwnd);
    }

    /// <summary>最小化検知 or Pause ボタン: デバッグカメラに切替えて Viewport に埋め込む。</summary>
    public void Pause()
    {
        EditorLog.Write($"Pause — state={_state}  hwnd=0x{_runtimeHwnd:X}  dbgSuspended={DebuggerSuspended}");
        if (_state != EditorState.Play) return;

        // ブレークポイント停止中はランタイムのメインスレッドが凍結しており、
        // EmbedRuntimeWindow 内の SetParent/SetWindowPos が凍結ウィンドウへの
        // 同期 Win32 呼び出しでデッドロックする。停止中は埋め込みを行わない
        // （停止フレームの表示は DWM サムネイルで別途行う）。
        if (DebuggerSuspended)
        {
            EditorLog.Write("Pause — デバッガ停止中のためウィンドウ埋め込みを抑止");
            return;
        }

        _pipe?.Send("PAUSE");
        EmbedRuntimeWindow();
        ChangeState(EditorState.Pause);
    }

    /// <summary>Resume ボタン: 通常モードに戻し独立ウィンドウに戻す。</summary>
    public void Resume()
    {
        EditorLog.Write($"Resume — state={_state}  hwnd=0x{_runtimeHwnd:X}");
        if (_state != EditorState.Pause) return;
        DetachRuntimeWindow();
        _pipe?.Send("RESUME");
        ChangeState(EditorState.Play);
    }

    /// <summary>Runtime に任意のメッセージを送信する（IPC 経由）。</summary>
    public void SendToRuntime(string message)
    {
        // 高頻度で送信されるカメラキー・修飾キーのログは除外する
        var noisy = message.StartsWith("CAM_KEY_")
                 || message == "CTRL_DOWN"
                 || message == "CTRL_UP";
        if (!noisy)
            EditorLog.Write($"[Editor→Runtime] {message[..Math.Min(80, message.Length)]}");
        _pipe?.Send(message);
    }

    /// <summary>
    /// ランタイムへプロファイラ計測の ON/OFF を送る（プロファイラパネルの表示状態と連動）。
    /// ランタイム側はデフォルト OFF で、パネルを閉じている間は計測コスト自体をゼロにするため、
    /// パネルの表示/非表示のたびに必ず呼び出すこと。
    /// </summary>
    public void SetProfilerEnabled(bool enabled)
    {
        ProfilerEnabled = enabled;
        _pipe?.Send($"SET_PROFILER:{(enabled ? 1 : 0)}");
    }

    /// <summary>
    /// WGSL シェーディングアセットの検証をランタイムへ依頼する。
    ///
    /// 送信書式（Rust 側と合意済み・変更禁止）:
    ///   <c>VALIDATE_WGSL:{request_id},{json_source}</c>
    ///   json_source は JSON 文字列リテラル（前後のダブルクォート込み・改行は \n エスケープ）。
    ///
    /// 【送信しない条件】
    /// - パイプ未接続（ランタイム未起動・起動前・再起動中）。
    /// - Play 中: 検証（naga）はランタイムのメインスレッドで走るため、
    ///   再生中のフレーム時間を削ってしまう。Pause 中や起動シーケンス中は
    ///   フレームを回していない／落としても影響が無いので許可する
    ///   （以前は「Edit ちょうど」に限定していたため、Pause 中や
    ///   Edit 遷移直前の検証まで捨てていた）。
    /// 送らなかった場合は「エラー」ではなく「検証不可」であり、呼び出し側は診断を出さない。
    ///
    /// 【ログ】送信は入力デバウンス毎（最短 500ms 間隔）なので、本文は載せず
    /// 「id とソース長」だけの 1 行に留める。送れなかった場合は理由が変わったときだけ
    /// 記録する（未接続のまま編集し続けると毎回出てログが埋まるため）。
    /// </summary>
    /// <param name="requestId">応答の相関に使う 10 進整数 ID。</param>
    /// <param name="source">検証対象の WGSL ソース全文。</param>
    /// <returns>送信できたら true、送信条件を満たさず送らなかったら false。</returns>
    public bool SendValidateWgsl(long requestId, string source)
    {
        if (_pipe is null || !_pipe.IsConnected)
        {
            // 「ランタイムが起動していないので検証できない」ことを必ず追跡できるようにする。
            // 実際、ビューポートタブが一度も前面に来ないまま起動するとランタイムが
            // 立ち上がらず、ここで黙って捨てられていた（WGSL の赤下線が出ない原因）。
            LogWgslValidationSkipped($"パイプ未接続（state={_state}）");
            return false;
        }
        // Play 中は既定では検証しない（naga 検証がランタイムのメインスレッドで走り、
        // 再生中のフレーム時間を削るため）。ただしエディタ設定
        // 「Play中もシェーダをホットリロード」がオンなら、Play 中も編集→保存で
        // 即反映させる運用なので、赤下線が出ないと保存してから壊れたと気づくことになる。
        // ホットリロードを許した以上、検証のコストも許容する。
        if (_state == EditorState.Play && !EditorPreferences.Instance.PlayShaderHotReload)
        {
            LogWgslValidationSkipped("Play 中（再生フレームを削らないため検証しない）");
            return false;
        }

        _lastWgslSkipReason = null;
        // ソースは 1 行の IPC に載せるため JSON 文字列リテラル化する（改行・引用符を安全に運ぶ）。
        var jsonSource = System.Text.Json.JsonSerializer.Serialize(source);
        EditorLog.Write($"[Editor→Runtime] VALIDATE_WGSL id={requestId} src={source.Length}文字 wire={jsonSource.Length}文字");
        _pipe.Send($"VALIDATE_WGSL:{requestId},{jsonSource}");
        return true;
    }

    /// <summary>直近に記録した「WGSL 検証を送れなかった理由」。同じ理由の連投を抑えるために保持する。</summary>
    private string? _lastWgslSkipReason;

    /// <summary>
    /// WGSL 検証を送信できなかったことを記録する（同一理由が続く間は 1 回だけ）。
    /// 入力のたびに呼ばれるため、無条件に書くとログが検証スキップで埋まってしまう。
    /// </summary>
    /// <param name="reason">送信できなかった理由（状態を含む短い説明）。</param>
    private void LogWgslValidationSkipped(string reason)
    {
        if (_lastWgslSkipReason == reason) return;
        _lastWgslSkipReason = reason;
        EditorLog.Write($"[Editor→Runtime] VALIDATE_WGSL 送信せず — {reason}");
    }

    /// <summary>
    /// 現在の Edit ランタイムのシーン状態をシステム TEMP フォルダの一時ファイルへ保存し、
    /// そのパスを返す。Play 実行前に未保存シーンの状態をキャプチャするために使用する。
    ///
    /// 既存の SAVE_SCENE / SAVE_OK IPC を再利用して保存する。
    /// タイムアウト（10 秒）または保存失敗時は null を返す。
    /// </summary>
    public async Task<string?> SaveCurrentSceneToTempAsync()
    {
        // Edit モード以外では使用不可
        if (_pipe is null || _state != EditorState.Edit)
        {
            EditorLog.Write("SaveCurrentSceneToTempAsync — Edit モードでないためスキップ");
            return null;
        }

        // 一時保存先パスを作成する（毎回同じパスで上書き）
        var tempDir  = Path.Combine(Path.GetTempPath(), "SEED");
        Directory.CreateDirectory(tempDir);
        var tempPath = Path.Combine(tempDir, "_play_temp.scene");

        // SAVE_OK / SAVE_ERROR を一回だけ受け取るための TCS を用意する。
        // SaveCompleted イベントは他の保存操作でも発火するため、
        // 購読してすぐ解除するパターンで競合を最小化する。
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnSave(bool ok, string _err)
        {
            tcs.TrySetResult(ok);
        }

        SaveCompleted += OnSave;
        try
        {
            _pipe.Send($"SAVE_SCENE:{tempPath}");
            EditorLog.Write($"SaveCurrentSceneToTempAsync — SAVE_SCENE:{tempPath}");

            // 最大 10 秒待機する（ディスク書き込みに時間がかかる場合に備える）
            var timeoutTask = Task.Delay(TimeSpan.FromSeconds(10));
            var completed   = await Task.WhenAny(tcs.Task, timeoutTask);

            if (completed == timeoutTask)
            {
                EditorLog.Write("SaveCurrentSceneToTempAsync — タイムアウト");
                return null;
            }

            var ok = await tcs.Task;
            if (!ok)
            {
                EditorLog.Write("SaveCurrentSceneToTempAsync — 保存失敗");
                return null;
            }

            EditorLog.Write($"SaveCurrentSceneToTempAsync — 保存完了: {tempPath}");
            return tempPath;
        }
        finally
        {
            // 必ず購読を解除する
            SaveCompleted -= OnSave;
        }
    }

    /// <summary>
    /// AI 実行中にレンダリングを一時停止して GPU リソースを解放する。
    /// ローカル LLM がレンダリングと GPU を奪い合わないようにするために使用する。
    /// Edit モードでのみ有効。Play/Pause 中は何もしない。
    /// </summary>
    public void PauseRendering()
    {
        if (_state != EditorState.Edit) return;
        EditorLog.Write("[Editor→Runtime] PAUSE_RENDER");
        _pipe?.Send("PAUSE_RENDER");
    }

    /// <summary>
    /// AI 応答完了後にレンダリングを再開する。
    /// PauseRendering() とペアで使用する。
    /// </summary>
    public void ResumeRendering()
    {
        if (_state != EditorState.Edit) return;
        EditorLog.Write("[Editor→Runtime] RESUME_RENDER");
        _pipe?.Send("RESUME_RENDER");
    }

    /// <summary>
    /// Runtime ウィンドウをコンテナの現在のクライアントサイズに合わせてリサイズする。
    /// 最大化起動などで初期サイズがズレた場合の補正用。
    /// </summary>
    public void ResizeRuntimeToContainer()
    {
        if (_runtimeHwnd == IntPtr.Zero || _viewportContainerHwnd == IntPtr.Zero) return;
        Win32.GetClientRect(_viewportContainerHwnd, out var rect);
        int w = rect.Right - rect.Left;
        int h = rect.Bottom - rect.Top;
        if (w > 0 && h > 0)
            Win32.MoveWindow(_runtimeHwnd, 0, 0, w, h, repaint: true);
        EditorLog.Write($"ResizeRuntimeToContainer — {w}x{h}");
    }

    /// <summary>
    /// Stop ボタン: Play ランタイムを Kill せず非表示保持（常駐）し、Edit に戻る。
    /// 2 回目以降の Play で保持プロセスを再利用してコールドスタートを回避する。
    /// </summary>
    public void Stop()
    {
        // 埋め込みインプレース Play 中は EXIT_PLAY を送るだけで Edit へ戻る
        // （ウィンドウ操作・プロセス保持は不要）。ENTER_PLAY 直後（PLAY_ENTERED 前）でも
        // ランタイムは IPC を順に処理するため、EXIT_PLAY 送信で Edit へ確実に戻る。
        if (_inEmbeddedPlay)
        {
            StopEmbeddedPlay();
            return;
        }

        // ── 起動シーケンス中の Stop（不具合1）───────────────────────────────
        // ウィンドウ Play の起動途中（プロセス起動〜Play 遷移前）に Stop が押された場合、
        // 起動をキャンセルする。実際の後始末（起動途中プロセスの終了・Edit 復帰）は、
        // キャンセルにより中断した LaunchAsync のキャンセルハンドラ（AbortLaunchAndRestoreEdit）
        // が UI スレッド上で行う。ここではキャンセル通知だけ行い二重処理を避ける。
        if (_isLaunching)
        {
            EditorLog.Write("Stop — 起動シーケンス中に Stop。起動をキャンセルして Edit へ戻す");
            _launchCts?.Cancel();
            return;
        }

        if (_state == EditorState.Pause) DetachRuntimeWindow();

        // Play ランタイムを Kill せず、描画停止＋非表示で常駐保持へ退避する
        HidePlayRuntime();

        // 保存した Edit ランタイムが生きていれば即復元（GPU 再初期化なし）
        if (_savedEditProcess is not null && !_savedEditProcess.HasExited)
            RestoreEditRuntime();
        else
            _ = StartEditAsync(_viewportContainerHwnd);
    }

    // ── プライベート: Play ランタイム常駐保持 ─────────────────────

    /// <summary>
    /// 現在の Play ランタイムを Kill せず、レンダリング停止（PAUSE_RENDER）＋ウィンドウ非表示で
    /// 常駐フィールドへ退避する。2 回目以降の Play で ReusePersistentPlayRuntime が再利用する。
    ///
    /// PAUSE_RENDER はランタイムの handle_redraw_requested を IPC 処理後に早期 return させ、
    /// 描画・物理・スクリプト・時間更新をまとめて止める（GPU/CPU を解放）。IPC は処理され続けるため、
    /// 再利用時の RESUME_RENDER / LOAD_SCENE を受け取れる。UI スレッドから呼ぶこと。
    /// </summary>
    private void HidePlayRuntime()
    {
        if (_process is null)
        {
            EditorLog.Write("HidePlayRuntime — Play プロセスが無いためスキップ");
            return;
        }

        // ── 不変条件ガード（方針B の安全網）─────────────────────────────
        // 常駐保持は「ウィンドウが出現済み（_runtimeHwnd 確定）」を前提とする。SW_HIDE で
        // ウィンドウを隠せてはじめて安全に非表示保持できるからである。方針A により通常は
        // Play 状態＝READY 受信済み＝hwnd 確定のため本分岐には入らないが、万一 hwnd 未確定で
        // ここへ来た場合、非表示化できないまま保持するとロード完了後にランタイムが自前で
        // ウィンドウを表示して「応答なし白画面」が残る。よって保持せず即 Kill してリークを防ぐ。
        if (_runtimeHwnd == IntPtr.Zero)
        {
            EditorLog.Write("HidePlayRuntime — hwnd 未確定のため常駐保持せず即 Kill（白画面ゾンビ防止）");
            KillRuntime(sendStop: false);
            return;
        }

        // 既に別の常駐 Play を保持していれば（通常は再利用時に消費済みで発生しないが保険）Kill する
        if (_persistentPlayProcess is not null)
        {
            EditorLog.Write("HidePlayRuntime — 既存の常駐 Play が残存していたため破棄");
            DisposePersistentPlayRuntime(killProcess: true);
        }

        UninstallMinimizeHook();

        // 描画・シミュレーションを停止して GPU/CPU を解放する（AI 実行時と同じ PAUSE_RENDER 経路）
        _pipe?.Send("PAUSE_RENDER");

        // 保持中に終了・メッセージで誤動作しないようイベント購読を解除する
        if (_process is not null)
            _process.Exited -= OnRuntimeExited;
        if (_pipe is not null)
            _pipe.MessageReceived -= OnPipeMessage;

        // ウィンドウを非表示にする（プロセスは維持）
        if (_runtimeHwnd != IntPtr.Zero)
            Win32.ShowWindow(_runtimeHwnd, SW_HIDE);

        // 常駐フィールドへ退避する
        _persistentPlayProcess = _process;
        _persistentPlayPipe    = _pipe;
        _persistentPlayHwnd    = _runtimeHwnd;

        // 現フィールドをクリアする（RestoreEditRuntime が Edit を設定する）
        _process     = null;
        _pipe        = null;
        _runtimeHwnd = IntPtr.Zero;

        EditorLog.Write($"HidePlayRuntime — Play を常駐保持  hwnd=0x{_persistentPlayHwnd:X}  PID={_persistentPlayProcess?.Id}");
    }

    /// <summary>
    /// 常駐保持している Play ランタイムを破棄する。
    /// killProcess=true のときはプロセスも Kill する（フォールバック新規起動・エディタ終了時）。
    /// </summary>
    private void DisposePersistentPlayRuntime(bool killProcess)
    {
        if (_persistentPlayProcess is null) return;

        if (killProcess && !_persistentPlayProcess.HasExited)
        {
            _persistentPlayPipe?.Send("STOP");
            try
            {
                _persistentPlayProcess.Kill();
                _persistentPlayProcess.WaitForExit(500);
            }
            catch (Exception ex)
            {
                EditorLog.Write($"DisposePersistentPlayRuntime — kill failed: {ex.Message}");
            }
        }
        _persistentPlayPipe?.Dispose();
        _persistentPlayProcess.Dispose();
        _persistentPlayProcess = null;
        _persistentPlayPipe    = null;
        _persistentPlayHwnd    = IntPtr.Zero;
    }

    // ── プライベート: 残存プロセス終了 ──────────────────────────

    /// <summary>
    /// 同名の Runtime プロセスが残っていれば強制終了し、
    /// EXE ファイルのロックを解放する。
    /// </summary>
    private void KillStaleRuntimeProcesses()
    {
        var exeName = Path.GetFileNameWithoutExtension(_runtimeExePath); // "SEED"
        foreach (var proc in Process.GetProcessesByName(exeName))
        {
            // 自分が管理しているプロセスは KillRuntime で既に終了済みのはず
            if (_process is not null && proc.Id == _process.Id)
            {
                proc.Dispose();
                continue;
            }
            try
            {
                EditorLog.Write($"KillStaleRuntimeProcesses — killing PID={proc.Id}");
                proc.Kill();
                proc.WaitForExit(1000);
            }
            catch (Exception ex)
            {
                EditorLog.Write($"KillStaleRuntimeProcesses — kill failed: {ex.Message}");
            }
            finally
            {
                proc.Dispose();
            }
        }
    }

    // ── プライベート: ビルド ───────────────────────────────────

    /// <summary>
    /// exe パスから runtime/ ソースディレクトリを解決する。
    /// target/debug または target/release の 2 階層上が runtime/。
    /// Cargo.toml が存在しない（配布時 etc）場合は null を返す。
    /// </summary>
    private static string? ResolveRuntimeSourceDir(string exePath)
    {
        // runtime/target/debug/SEED.exe
        //          ↑ ↑ ↑ 3 つ上
        var exeDir     = Path.GetDirectoryName(exePath) ?? "";        // target/debug
        var targetDir  = Path.GetDirectoryName(exeDir)  ?? "";        // target
        var runtimeDir = Path.GetDirectoryName(targetDir) ?? "";      // runtime
        var cargoToml  = Path.Combine(runtimeDir, "Cargo.toml");
        return File.Exists(cargoToml) ? runtimeDir : null;
    }

    /// <summary>
    /// 指定ディレクトリで <c>cargo build</c> を実行し、完了を待つ。
    /// 標準エラー出力（cargo の進捗表示）をログに書き出す。
    /// </summary>
    private static async Task<bool> BuildAsync(string workingDir)
    {
        EditorLog.Write($"BuildAsync — cargo build  dir={workingDir}");

        var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

        var proc = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName               = "cargo",
                Arguments              = "build",
                WorkingDirectory       = workingDir,
                UseShellExecute        = false,
                CreateNoWindow         = true,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
            },
            EnableRaisingEvents = true,
        };

        // cargo はほぼすべての出力を stderr に書く
        proc.OutputDataReceived += (_, e) => { if (e.Data is not null) EditorLog.Write($"[cargo] {e.Data}"); };
        proc.ErrorDataReceived  += (_, e) => { if (e.Data is not null) EditorLog.Write($"[cargo] {e.Data}"); };
        proc.Exited             += (_, _) => tcs.TrySetResult(proc.ExitCode);

        try
        {
            proc.Start();
        }
        catch (Exception ex)
        {
            EditorLog.Write($"BuildAsync — cargo の起動に失敗: {ex.Message}  (cargo が PATH に無い?)");
            proc.Dispose();
            return false;
        }

        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        var exitCode = await tcs.Task;
        proc.Dispose();

        EditorLog.Write($"BuildAsync — cargo build 終了  exitCode={exitCode}");
        return exitCode == 0;
    }

    // ── プライベート: 起動 ─────────────────────────────────────

    /// <param name="editMode">true=Edit ランタイム起動 / false=Play ランタイム起動。</param>
    /// <param name="launchToken">
    /// Play 起動シーケンスのキャンセルトークン（不具合1）。起動中に Stop が押されると Cancel され、
    /// クラッシュ検知待機・パイプ接続待機の await を中断させて Play 遷移を止める。
    /// Edit 起動（editMode=true）では default（キャンセル不可）で呼ばれる。
    /// </param>
    private async Task LaunchAsync(bool editMode, CancellationToken launchToken = default)
    {
        EditorLog.Write($"LaunchAsync start — editMode={editMode}");

        _pipe = new PipeServer();
        EditorLog.Write($"PipeServer created — name={_pipe.PipeName}");

        // Playモードではアセットルートパスを引数として渡し、
        // ランタイムが project_settings.json から開始シーンを自動ロードできるようにする
        var assetsRootArg = !string.IsNullOrEmpty(AssetsPath)
            ? $" --assets-root={AssetsPath}"
            : "";
        var editorResourcesArg = !string.IsNullOrEmpty(EditorResourcesPath)
            ? $" --editor-resources={EditorResourcesPath}"
            : "";
        // PlayScenePath が設定されている場合は --scene= で渡す（null = start_scene 使用）
        var sceneArg = !string.IsNullOrEmpty(PlayScenePath)
            ? $" --scene={PlayScenePath}"
            : "";
        // コライダー描画フラグ: SyncViewportSettings の到着前から有効にするために先行フラグとして渡す
        var playColliderDrawArg = PlayColliderDraw ? " --play-collider-draw=1" : "";
        // エディタの PID を渡す: SEED.exe はエディタが終了したら自分自身も終了する
        var parentPidArg = $" --parent-pid={System.Diagnostics.Process.GetCurrentProcess().Id}";
        // Edit / Play 両モードで --assets-root を渡す。
        // Edit モードでも sprite テクスチャ等のバーチャルパス（assets://...）解決に必要。
        var args = editMode
            ? $"--mode=edit --pipe={_pipe.PipeName} --parent-hwnd={_viewportContainerHwnd}{assetsRootArg}{editorResourcesArg}{parentPidArg}"
            : $"--mode=play --pipe={_pipe.PipeName}{assetsRootArg}{sceneArg}{editorResourcesArg}{playColliderDrawArg}{parentPidArg}";

        var workDir = ResolveWorkingDirectory(_runtimeExePath);
        EditorLog.Write($"Process.Start — exe={_runtimeExePath}  args={args}  workDir={workDir}");

        var stderr = new System.Text.StringBuilder();

        _process = Process.Start(new ProcessStartInfo
        {
            FileName               = _runtimeExePath,
            Arguments              = args,
            UseShellExecute        = false,
            WorkingDirectory       = workDir,
            CreateNoWindow         = true,
            RedirectStandardError  = true,
            RedirectStandardOutput = true,
        }) ?? throw new InvalidOperationException("Failed to start runtime.");

        EditorLog.Write($"Process started — PID={_process.Id}");

        // Play 起動時は Launching 状態へ遷移する（不具合1・不具合2）。
        // これにより UI は起動中を表示し、Stop ボタンを押せる（起動キャンセル可能）状態にする。
        // Edit 起動時は従来どおり最後にまとめて Edit へ遷移するため、ここでは変更しない。
        if (!editMode) ChangeState(EditorState.Launching);

        _process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data != null)
            {
                stderr.AppendLine(e.Data);
                EditorLog.Write($"[STDERR] {e.Data}");
            }
        };
        _process.OutputDataReceived += (_, e) =>
        {
            if (e.Data != null) EditorLog.Write($"[STDOUT] {e.Data}");
        };
        _process.BeginErrorReadLine();
        _process.BeginOutputReadLine();

        _process.EnableRaisingEvents = true;
        _process.Exited += OnRuntimeExited;

        _pipe.MessageReceived += OnPipeMessage;

        // ── 起動シーケンスの待機（クラッシュ検知 → パイプ接続）───────────────
        // launchToken が Cancel されると（起動中に Stop）、以下の await が
        // OperationCanceledException で中断し、下の catch で Edit へ復帰する（不具合1）。
        try
        {
            // 起動直後クラッシュを検知
            EditorLog.Write("Waiting 500ms for crash detection...");
            await Task.Delay(500, launchToken);
            if (_process.HasExited)
            {
                var msg = stderr.Length > 0 ? stderr.ToString() : "(no stderr output)";
                throw new InvalidOperationException($"Runtime crashed immediately (exit code {_process.ExitCode}):\n{msg}");
            }
            // 正常起動できたのでクラッシュカウントをリセット
            _restartCount    = 0;
            _intentionalStop = false;
            EditorLog.Write("Process still alive — waiting for pipe connection (10s timeout)...");

            // launchToken（Stop によるキャンセル）と 10 秒タイムアウトを結合したトークンで待機する。
            bool pipeConnected = false;
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(launchToken);
            cts.CancelAfter(TimeSpan.FromSeconds(10));
            try
            {
                await _pipe.WaitForConnectionAsync(cts.Token);
                pipeConnected = true;
                EditorLog.Write("Pipe connected");
                // プロファイラ購読状態を新しいランタイムへ同期する。
                // ランタイムは既定で計測 OFF のため、パネルを開いたままエディタを起動した
                // （＝接続前に SetProfilerEnabled が空振りした）ケースをここで拾わないと、
                // パネルを一度閉じて開き直すまでデータが届かない。
                _pipe?.Send($"SET_PROFILER:{(ProfilerEnabled ? 1 : 0)}");
            }
            catch (Exception ex) when (!launchToken.IsCancellationRequested)
            {
                // タイムアウト等（Stop 由来のキャンセルは除く）。HWND なしで続行する従来動作。
                EditorLog.Write($"Pipe connection timeout/error: {ex.Message}  (continuing without HWND)");
            }

            // パイプ接続待ちの間に Stop されていた場合はここで確実に中断する。
            launchToken.ThrowIfCancellationRequested();

            // ── 方針A: Play はウィンドウ準備完了（READY:hwnd）まで遅延する ─────────────
            // ウィンドウ Play のランタイムはロード完了後にウィンドウを表示して READY:{hwnd} を
            // 送る。パイプ接続（約 500ms）ではなく READY を待って Play へ遷移することで、
            // _runtimeHwnd を必ず確定させてから Play にする。これにより、ロード中の Stop は
            // Play ではなく Launching 中の Stop（_isLaunching=true）として扱われ、キャンセル機構
            // （AbortLaunchAndRestoreEdit → Kill）が起動途中プロセスを確実に終了させる。
            // → 「Stop 後にロード完了したランタイムが自前でウィンドウを表示して応答なし白画面」を根絶する。
            //
            // パイプが接続できなかった場合（10 秒タイムアウト）は READY も来ないため待たずに続行する
            // （従来どおり HWND なしで Play へ遷移する。この分岐は異常系のフォールバック）。
            if (!editMode && pipeConnected)
            {
                var readyTcs = new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                _playWindowReadyTcs = readyTcs;
                // Stop（launchToken）でこの待機をキャンセルする。プロセスが READY 前に終了した
                // 場合は OnRuntimeExited が TrySetResult(false) で解く。
                using var readyReg = launchToken.Register(
                    () => readyTcs.TrySetCanceled(launchToken));
                try
                {
                    EditorLog.Write("LaunchAsync — パイプ接続完了。READY(ウィンドウ準備完了)を待機して Play へ遷移する");
                    var windowReady = await readyTcs.Task;
                    if (!windowReady)
                    {
                        // READY を待っている間にプロセスが終了した（クラッシュ等）。
                        // 後始末（Edit 復元 or 再起動）は OnRuntimeExited が実施済みのため、
                        // ここでは Play へ遷移せず終了する。
                        EditorLog.Write("LaunchAsync — READY 前にプロセスが終了。Play 遷移を中止");
                        return;
                    }
                    EditorLog.Write("LaunchAsync — READY 受信。Play へ遷移する");
                }
                finally
                {
                    _playWindowReadyTcs = null;
                }
            }
        }
        catch (OperationCanceledException) when (!editMode && launchToken.IsCancellationRequested)
        {
            // 起動中に Stop が押された（不具合1）。起動途中プロセスを終了し Edit へ復帰する。
            EditorLog.Write("LaunchAsync — 起動シーケンスがキャンセルされた（起動中 Stop）。プロセス終了・Edit 復帰");
            AbortLaunchAndRestoreEdit();
            return;
        }

        ChangeState(editMode ? EditorState.Edit : EditorState.Play);
        EditorLog.Write($"State changed to {(editMode ? "Edit" : "Play")}");

        if (!editMode) InstallMinimizeHook();
    }

    /// <summary>
    /// 起動シーケンス中に Stop が押されたときの後始末（不具合1）。UI スレッドから呼ぶこと。
    /// 起動途中の Play プロセスは READY 前で HWND 未確定のため常駐保持（非表示化）できない。
    /// 確実に Kill し、保存しておいた Edit ランタイムを即復元する（無ければ通常再起動）。
    /// </summary>
    private void AbortLaunchAndRestoreEdit()
    {
        // 起動途中プロセスを終了する（KillRuntime が _process.Exited 購読解除・pipe 破棄・Kill を実施）。
        // sendStop=false: READY 前でランタイムの IPC 受信が未確立の可能性があるため STOP は送らない。
        KillRuntime(sendStop: false);

        // 保存した Edit ランタイムが生きていれば即復元（GPU 再初期化なし）、無ければ通常再起動。
        if (_savedEditProcess is not null && !_savedEditProcess.HasExited)
            RestoreEditRuntime();
        else
            _ = StartEditAsync(_viewportContainerHwnd);
    }

    // ── プライベート: IPC メッセージ処理 ──────────────────────

    private void OnPipeMessage(string msg)
    {
        if (msg.StartsWith("READY:", StringComparison.Ordinal) &&
            long.TryParse(msg["READY:".Length..], out var hwnd))
        {
            _runtimeHwnd = (IntPtr)hwnd;
            EditorLog.Write($"[Runtime→Editor] READY  hwnd=0x{hwnd:X}");
            RuntimeHwndAvailable?.Invoke((nint)hwnd);
            // 方針A: ウィンドウ Play の起動シーケンスが READY を待っている場合、ここで解禁して
            // LaunchAsync に Play 遷移を再開させる（_runtimeHwnd 確定後に Play へ遷移させる）。
            // Play 起動中以外（Edit 起動・常駐再利用・Edit 復元経由の READY）では null のため無害。
            _playWindowReadyTcs?.TrySetResult(true);
        }
        else if (msg == "FIRST_FRAME")
        {
            EditorLog.Write("[Runtime→Editor] FIRST_FRAME — 最初の実フレーム描画完了");
            FirstFrameReady?.Invoke();
        }
        else if (msg == "PLAY_ENTERED")
        {
            // 埋め込みインプレース Play 開始の応答。Edit→Play へ遷移する。
            // 本コールバックはパイプ受信スレッドのため、ChangeState 経由の StateChanged
            // ハンドラ（MainWindow）が Dispatcher で UI スレッドへマーシャルする。
            EditorLog.Write("[Runtime→Editor] PLAY_ENTERED — 埋め込み Play 開始");
            // 埋め込み Play の起動シーケンス完了。再入ガードを解除する（不具合2）。
            _isLaunching = false;
            ChangeState(EditorState.Play);
        }
        else if (msg == "PLAY_EXITED")
        {
            // 埋め込みインプレース Play 停止の応答。Play→Edit へ遷移する。
            EditorLog.Write("[Runtime→Editor] PLAY_EXITED — 埋め込み Play 停止、Edit 復帰");
            // PLAY_ENTERED が届く前に Stop された場合の保険としてここでも解除する。
            _isLaunching    = false;
            _inEmbeddedPlay = false;
            ChangeState(EditorState.Edit);
        }
        else if (msg.StartsWith("WGSL_DIAG:", StringComparison.Ordinal))
        {
            // 書式: WGSL_DIAG:{request_id},{json_array}
            // request_id はカンマを含まない 10 進整数なので、最初のカンマまでで分割する
            // （json_array 側にはカンマが含まれ得るため Split は使わない）。
            var payload  = msg["WGSL_DIAG:".Length..];
            int commaPos = payload.IndexOf(',');
            if (commaPos > 0 && long.TryParse(payload[..commaPos], out var reqId))
            {
                var json = payload[(commaPos + 1)..];
                // 受信の事実は必ず残す（診断本文は長いので載せない）。件数は JSON を解釈する
                // WgslValidationService 側で記録する。
                EditorLog.Write($"[Runtime→Editor] WGSL_DIAG id={reqId} json={json.Length}文字");
                WgslDiagnosticsReceived?.Invoke(reqId, json);
            }
            else
            {
                // 書式違反（相関できない応答）は握り潰さず記録する。
                EditorLog.Write($"[Runtime→Editor] WGSL_DIAG 書式不正: {payload[..Math.Min(80, payload.Length)]}");
            }
        }
        else if (msg.StartsWith("HIERARCHY:", StringComparison.Ordinal))
        {
            var json = msg["HIERARCHY:".Length..];
            EditorLog.Write($"[Runtime→Editor] HIERARCHY ({json.Length} chars)");
            HierarchyUpdated?.Invoke(json);
        }
        else if (msg.StartsWith("SELECTED:", StringComparison.Ordinal) &&
                 int.TryParse(msg["SELECTED:".Length..], out var selIdx))
        {
            EditorLog.Write($"[Runtime→Editor] SELECTED idx={selIdx}");
            SelectionChanged?.Invoke(selIdx);
        }
        else if (msg.StartsWith("SELECTED_MULTI:", StringComparison.Ordinal))
        {
            var ids = msg["SELECTED_MULTI:".Length..]
                .Split(',')
                .Select(s => int.TryParse(s, out var n) ? (int?)n : null)
                .Where(n => n.HasValue)
                .Select(n => n!.Value)
                .ToList();
            EditorLog.Write($"[Runtime→Editor] SELECTED_MULTI count={ids.Count}");
            if (ids.Count > 0) SelectionMultiChanged?.Invoke(ids);
        }
        else if (msg.StartsWith("SCRIPTS_RELOADED:", StringComparison.Ordinal))
        {
            // ランタイム側の全スクリプト一括コンパイル・再生成の結果通知。
            // フォーマット: "count,restored"（count = コンパイル型数、-1 = 失敗）。
            // 失敗時はスクリプトが Placeholder のまま実行されない（サイレント故障）ため、
            // Output パネルに目立つ形でエラーを表示する。
            var payload = msg["SCRIPTS_RELOADED:".Length..];
            var countStr = payload.Split(',').FirstOrDefault() ?? "";
            if (int.TryParse(countStr, out var compiledCount) && compiledCount >= 0)
            {
                EditorLog.Write($"[Runtime→Editor] SCRIPTS_RELOADED — {compiledCount} 型をコンパイル・再生成完了");
                // 型キャッシュ破棄の合図。この直後にランタイムから届く ACTOR_COMPONENTS で
                // インスペクタが新しいフィールド構成に組み直される（再アタッチ不要）。
                ScriptsReloaded?.Invoke(compiledCount);
            }
            else
            {
                EditorLog.Write("════════════════════════════════════════════════");
                EditorLog.Write("[スクリプトエラー] スクリプトのリロードに失敗しました。");
                EditorLog.Write("  全 .cs の一括コンパイルでエラーが発生しています（型名の重複など、");
                EditorLog.Write("  ファイル単体では検出できないエラーの可能性があります）。");
                EditorLog.Write("  上記の [ScriptCompileError] 行を確認してください。");
                EditorLog.Write("  ※ 修正するまでスクリプトは実行されません。");
                EditorLog.Write("════════════════════════════════════════════════");
            }
        }
        else if (msg == "CONTEXT_MENU")
        {
            EditorLog.Write("[Runtime→Editor] CONTEXT_MENU");
            ViewportContextMenuRequested?.Invoke();
        }
        else if (msg == "SAVE_OK")
        {
            EditorLog.Write("[Runtime→Editor] SAVE_OK");
            SaveCompleted?.Invoke(true, "");
        }
        else if (msg.StartsWith("SAVE_ERROR:", StringComparison.Ordinal))
        {
            EditorLog.Write($"[Runtime→Editor] SAVE_ERROR: {msg["SAVE_ERROR:".Length..]}");
            SaveCompleted?.Invoke(false, msg["SAVE_ERROR:".Length..]);
        }
        else if (msg == "TERRAIN_INIT_OK")
        {
            EditorLog.Write("[Runtime→Editor] TERRAIN_INIT_OK");
            TerrainInitCompleted?.Invoke();
        }
        else if (msg.StartsWith("TERRAIN_SAVE_OK:", StringComparison.Ordinal))
        {
            var count = msg["TERRAIN_SAVE_OK:".Length..];
            EditorLog.Write($"[Runtime→Editor] TERRAIN_SAVE_OK count={count}");
            TerrainSaveCompleted?.Invoke(true, count);
        }
        else if (msg.StartsWith("TERRAIN_SAVE_ERROR:", StringComparison.Ordinal))
        {
            var err = msg["TERRAIN_SAVE_ERROR:".Length..];
            EditorLog.Write($"[Runtime→Editor] TERRAIN_SAVE_ERROR {err}");
            TerrainSaveCompleted?.Invoke(false, err);
        }
        else if (msg.StartsWith("TERRAIN_SAVE_AS_OK:", StringComparison.Ordinal))
        {
            // 引数は "フォルダ参照,チャンク数"。フォルダ名にカンマが入りうるので
            // **最後のカンマ**で分割する（ランタイム側の組み立てと対）。
            var body  = msg["TERRAIN_SAVE_AS_OK:".Length..];
            int comma = body.LastIndexOf(',');
            var dir   = comma < 0 ? body : body[..comma];
            var count = comma < 0 ? ""   : body[(comma + 1)..];
            EditorLog.Write($"[Runtime→Editor] TERRAIN_SAVE_AS_OK dir={dir} count={count}");
            TerrainSaveAsCompleted?.Invoke(true, dir, count);
        }
        else if (msg.StartsWith("TERRAIN_SAVE_AS_ERROR:", StringComparison.Ordinal))
        {
            var err = msg["TERRAIN_SAVE_AS_ERROR:".Length..];
            EditorLog.Write($"[Runtime→Editor] TERRAIN_SAVE_AS_ERROR {err}");
            TerrainSaveAsCompleted?.Invoke(false, err, "");
        }
        else if (msg.StartsWith("TERRAIN_DIR:", StringComparison.Ordinal))
        {
            var dir = msg["TERRAIN_DIR:".Length..];
            EditorLog.Write($"[Runtime→Editor] TERRAIN_DIR {dir}");
            TerrainDirChanged?.Invoke(dir);
        }
        else if (msg.StartsWith("TERRAIN_BRUSH_OK:", StringComparison.Ordinal))
        {
            TerrainBrushResult?.Invoke(true, msg["TERRAIN_BRUSH_OK:".Length..]);
        }
        else if (msg == "TERRAIN_BRUSH_MISS")
        {
            TerrainBrushResult?.Invoke(false, "");
        }
        // チャンク当たり判定トグル（コリジョンツールのクリック 1 回ごと）。
        else if (msg.StartsWith("TERRAIN_COLLISION_OK:", StringComparison.Ordinal))
        {
            var body = msg["TERRAIN_COLLISION_OK:".Length..];
            EditorLog.Write($"[Runtime→Editor] TERRAIN_COLLISION_OK {body}");
            TerrainCollisionResult?.Invoke(true, body);
        }
        else if (msg == "TERRAIN_COLLISION_MISS")
        {
            TerrainCollisionResult?.Invoke(false, "");
        }
        // その場デシメート（全チャンク一括）の完了通知。
        else if (msg.StartsWith("TERRAIN_DECIMATE_OK:", StringComparison.Ordinal))
        {
            var body = msg["TERRAIN_DECIMATE_OK:".Length..];
            EditorLog.Write($"[Runtime→Editor] TERRAIN_DECIMATE_OK {body}");
            TerrainDecimateCompleted?.Invoke(true, body);
        }
        else if (msg.StartsWith("TERRAIN_DECIMATE_ERROR:", StringComparison.Ordinal))
        {
            var err = msg["TERRAIN_DECIMATE_ERROR:".Length..];
            EditorLog.Write($"[Runtime→Editor] TERRAIN_DECIMATE_ERROR {err}");
            TerrainDecimateCompleted?.Invoke(false, err);
        }
        else if (msg.StartsWith("TERRAIN_HEIGHTMAP_OK:", StringComparison.Ordinal))
        {
            var ms = msg["TERRAIN_HEIGHTMAP_OK:".Length..];
            EditorLog.Write($"[Runtime→Editor] TERRAIN_HEIGHTMAP_OK ms={ms}");
            TerrainHeightmapCompleted?.Invoke(true, ms);
        }
        else if (msg.StartsWith("TERRAIN_HEIGHTMAP_ERROR:", StringComparison.Ordinal))
        {
            var err = msg["TERRAIN_HEIGHTMAP_ERROR:".Length..];
            EditorLog.Write($"[Runtime→Editor] TERRAIN_HEIGHTMAP_ERROR {err}");
            TerrainHeightmapCompleted?.Invoke(false, err);
        }
        else if (msg.StartsWith("TERRAIN_ADD_CHUNKS_OK:", StringComparison.Ordinal))
        {
            var counts = msg["TERRAIN_ADD_CHUNKS_OK:".Length..];
            EditorLog.Write($"[Runtime→Editor] TERRAIN_ADD_CHUNKS_OK counts={counts}");
            TerrainAddChunksCompleted?.Invoke(true, counts);
        }
        else if (msg.StartsWith("TERRAIN_ADD_CHUNKS_ERROR:", StringComparison.Ordinal))
        {
            var err = msg["TERRAIN_ADD_CHUNKS_ERROR:".Length..];
            EditorLog.Write($"[Runtime→Editor] TERRAIN_ADD_CHUNKS_ERROR {err}");
            TerrainAddChunksCompleted?.Invoke(false, err);
        }
        // 散布ブラシの結果は、ドラッグ中に 40ms 間隔で届く高頻度メッセージ。
        // 密度ブラシ（TERRAIN_BRUSH_OK / _MISS）と同じく TerrainBrushResult へ流し、
        // UI 更新は行わない。ここで拾わないと未知メッセージとして毎回ログへ書かれ、
        // 1 ストロークでエディタログが埋まってしまう。
        else if (msg.StartsWith("TERRAIN_SCATTER_BRUSH_OK:", StringComparison.Ordinal))
        {
            TerrainBrushResult?.Invoke(true, msg["TERRAIN_SCATTER_BRUSH_OK:".Length..]);
        }
        else if (msg == "TERRAIN_SCATTER_BRUSH_MISS")
        {
            TerrainBrushResult?.Invoke(false, "");
        }
        else if (msg.StartsWith("TERRAIN_SCATTER_OK:", StringComparison.Ordinal))
        {
            var total = msg["TERRAIN_SCATTER_OK:".Length..];
            EditorLog.Write($"[Runtime→Editor] TERRAIN_SCATTER_OK total={total}");
            TerrainScatterCompleted?.Invoke(true, total);
        }
        else if (msg.StartsWith("TERRAIN_SCATTER_ERROR:", StringComparison.Ordinal))
        {
            var err = msg["TERRAIN_SCATTER_ERROR:".Length..];
            EditorLog.Write($"[Runtime→Editor] TERRAIN_SCATTER_ERROR {err}");
            TerrainScatterCompleted?.Invoke(false, err);
        }
        // ── 地表カバー場（CoverEmitterComponent）のシミュレート通知 ──
        //   稼働状態が変わる 2 種だけをイベント化し、単発完了の 2 種はログのみに留める
        //   （拾わないと未知メッセージとして毎回ログに警告が出る）。
        else if (msg == "TERRAIN_COVER_SIM_STARTED")
        {
            EditorLog.Write("[Runtime→Editor] TERRAIN_COVER_SIM_STARTED");
            TerrainCoverSimRunningChanged?.Invoke(true);
        }
        else if (msg == "TERRAIN_COVER_SIM_STOPPED")
        {
            EditorLog.Write("[Runtime→Editor] TERRAIN_COVER_SIM_STOPPED");
            TerrainCoverSimRunningChanged?.Invoke(false);
        }
        else if (msg.StartsWith("TERRAIN_COVER_STEP_OK:", StringComparison.Ordinal))
        {
            var steps = msg["TERRAIN_COVER_STEP_OK:".Length..];
            EditorLog.Write($"[Runtime→Editor] TERRAIN_COVER_STEP_OK steps={steps}");
        }
        else if (msg == "TERRAIN_COVER_CLEARED")
        {
            EditorLog.Write("[Runtime→Editor] TERRAIN_COVER_CLEARED");
        }
        // カバーブラシの結果は散布ブラシとまったく同じ扱い（高頻度なのでログに書かない）。
        else if (msg.StartsWith("TERRAIN_COVER_BRUSH_OK:", StringComparison.Ordinal))
        {
            TerrainBrushResult?.Invoke(true, msg["TERRAIN_COVER_BRUSH_OK:".Length..]);
        }
        else if (msg == "TERRAIN_COVER_BRUSH_MISS")
        {
            TerrainBrushResult?.Invoke(false, "");
        }
        // 未定義の素材 ID を塗ろうとした等の設定ミスは、ドラッグ中に連呼されうるが
        // 原因が分からないと直せないのでログには残す（ステータス表示までは行わない）。
        else if (msg.StartsWith("TERRAIN_COVER_BRUSH_ERROR:", StringComparison.Ordinal))
        {
            EditorLog.Write($"[Runtime→Editor] {msg}");
        }
        // ブラシ形状マスクの設定結果。設定・解除のたびに 1 回だけ来る低頻度メッセージなので
        // 成否とも残す（読み込み失敗時はブラシが円形へ縮退するだけで動き続けるため、
        // ログが唯一の手掛かりになる）。
        else if (msg.StartsWith("TERRAIN_BRUSH_MASK_OK:", StringComparison.Ordinal)
              || msg.StartsWith("TERRAIN_BRUSH_MASK_ERROR:", StringComparison.Ordinal))
        {
            EditorLog.Write($"[Runtime→Editor] {msg}");
        }
        else if (msg.StartsWith("ACTOR_DATA:", StringComparison.Ordinal))
        {
            var json = msg["ACTOR_DATA:".Length..];
            EditorLog.Write($"[Runtime→Editor] ACTOR_DATA ({json.Length} chars)");
            ActorDataReceived?.Invoke(json);
        }
        else if (msg.StartsWith("ACTOR_COMPONENTS:", StringComparison.Ordinal))
        {
            var json = msg["ACTOR_COMPONENTS:".Length..];
            EditorLog.Write($"[Runtime→Editor] ACTOR_COMPONENTS ({json.Length} chars)");
            ActorComponentsReceived?.Invoke(json);
        }
        else if (msg.StartsWith("BINDABLE_SOURCES:", StringComparison.Ordinal))
        {
            // 水面シェーダ @ref 行のドロップ解決に使うバインド元候補（GET_BINDABLE_SOURCES の応答）。
            // 中身の解釈はインスペクタ側に任せ、ここは JSON をそのまま流すだけにする。
            var json = msg["BINDABLE_SOURCES:".Length..];
            EditorLog.Write($"[Runtime→Editor] BINDABLE_SOURCES ({json.Length} chars)");
            BindableSourcesReceived?.Invoke(json);
        }
        else if (msg.StartsWith("SCENE_SHADING_PARAMS:", StringComparison.Ordinal))
        {
            // シーン設定ウィンドウの「シェーダ」行の直下に出すパラメータ行の元データ。
            // 解釈は受け手（シーン設定ウィンドウ）に任せ、ここは JSON をそのまま流す。
            var json = msg["SCENE_SHADING_PARAMS:".Length..];
            EditorLog.Write($"[Runtime→Editor] SCENE_SHADING_PARAMS ({json.Length} chars)");
            SceneShadingParamsReceived?.Invoke(json);
        }
        else if (msg.StartsWith("CONTROL_POINT_SELECTED:", StringComparison.Ordinal))
        {
            // フォーマット: CONTROL_POINT_SELECTED:{actorDfsId},{slotIdx},{index}
            // 3 要素すべてが int パースできた場合のみ通知する（壊れた行はログのみで無視）。
            var payload = msg["CONTROL_POINT_SELECTED:".Length..];
            var parts   = payload.Split(',');
            if (parts.Length == 3
                && int.TryParse(parts[0], out var cpActor)
                && int.TryParse(parts[1], out var cpSlot)
                && int.TryParse(parts[2], out var cpIndex))
            {
                ControlPointSelected?.Invoke(cpActor, cpSlot, cpIndex);
            }
            else
            {
                EditorLog.Write($"[Runtime→Editor] CONTROL_POINT_SELECTED 解析失敗: {payload}");
            }
        }
        else if (msg == "CONTROL_POINT_DESELECTED")
        {
            ControlPointDeselected?.Invoke();
        }
        else if (msg == "STOPPED")
        {
            // ゲームウィンドウが閉じられた通知。
            // Rust 側の CloseRequested で SetForegroundWindow(editor_hwnd) を直接呼んでいるため、
            // このメッセージが届く前にエディタはすでにフォアグラウンドを取得済み。
            // Activate() は追加の安全策として残す。
            EditorLog.Write("[Runtime→Editor] STOPPED — ゲームウィンドウ終了、フォアグラウンド復帰");
            Application.Current.Dispatcher.InvokeAsync(() =>
                Application.Current.MainWindow?.Activate());
        }
        else if (msg == "SCENE_LOADED")
        {
            EditorLog.Write("[Runtime→Editor] SCENE_LOADED");
        }
        else if (msg.StartsWith("CAM_STATE:", StringComparison.Ordinal))
        {
            var payload = msg["CAM_STATE:".Length..];
            CameraStateReceived?.Invoke(payload);
        }
        else if (msg == "SCENE_MODIFIED")
        {
            SceneModified?.Invoke();
        }
        else if (msg.StartsWith("MODAL_STATE:", StringComparison.Ordinal))
        {
            // モーダルトランスフォームの進行状態通知（1 = 開始 / 0 = 終了）。
            ModalTransformStateChanged?.Invoke(msg["MODAL_STATE:".Length..] == "1");
        }
        else if (msg.StartsWith("PLACEMENT_STATE:", StringComparison.Ordinal))
        {
            // ロジック配置の配置モードの進行状態通知（1 = 開始 / 0 = 終了）。
            PlacementStateChanged?.Invoke(msg["PLACEMENT_STATE:".Length..] == "1");
        }
        else if (msg.StartsWith("PLACEMENT_RADIUS:", StringComparison.Ordinal))
        {
            // 半径ドラッグで確定した半径 [m]。Rust 側は不変文化圏の書式で送る。
            var payload = msg["PLACEMENT_RADIUS:".Length..];
            if (float.TryParse(payload, System.Globalization.NumberStyles.Float,
                               System.Globalization.CultureInfo.InvariantCulture, out var radius))
            {
                PlacementRadiusChanged?.Invoke(radius);
            }
        }
        else if (msg.StartsWith("TOOL_MODE:", StringComparison.Ordinal))
        {
            // ランタイム側のツールホットキー（Q/W/E/T）による切り替え通知。
            var payload = msg["TOOL_MODE:".Length..];
            ToolModeChanged?.Invoke(payload);
        }
        else if (msg.StartsWith("EDIT_PHYSICS_STATE:", StringComparison.Ordinal))
        {
            var payload = msg["EDIT_PHYSICS_STATE:".Length..];
            EditPhysicsStateReceived?.Invoke(payload);
        }
        else if (msg == "ACTOR_EDIT_STARTED")
        {
            EditorLog.Write("[Runtime→Editor] ACTOR_EDIT_STARTED");
            ActorEditStarted?.Invoke();
        }
        else if (msg == "ACTOR_EDIT_ENDED")
        {
            EditorLog.Write("[Runtime→Editor] ACTOR_EDIT_ENDED");
            ActorEditEnded?.Invoke();
        }
        else if (msg.StartsWith("CANVAS_EDIT_WL:", StringComparison.Ordinal))
        {
            // キャンバス編集タブ開始応答。
            // フォーマット: CANVAS_EDIT_WL:{world_line},{root_is_2d:0|1},{actor_name}
            var payload = msg["CANVAS_EDIT_WL:".Length..];
            var parts   = payload.Split(',', 3);
            if (parts.Length == 3 && uint.TryParse(parts[0], out var canvasWl))
            {
                bool rootIs2D = parts[1] == "1";
                EditorLog.Write($"[Runtime→Editor] CANVAS_EDIT_WL wl={canvasWl} rootIs2D={rootIs2D} name={parts[2]}");
                CanvasEditStarted?.Invoke(canvasWl, rootIs2D, parts[2]);
            }
            else
            {
                EditorLog.Write($"[Runtime→Editor] CANVAS_EDIT_WL 解析失敗: {payload}");
            }
        }
        else if (msg.StartsWith("WORLD_LINE_INFO:", StringComparison.Ordinal))
        {
            var info = msg["WORLD_LINE_INFO:".Length..];
            EditorLog.Write($"[WorldLine] {info}");
            WorldLineInfoReceived?.Invoke(info);
        }
        else if (msg.StartsWith("LOAD_ERROR:", StringComparison.Ordinal))
        {
            var err = msg["LOAD_ERROR:".Length..];
            EditorLog.Write($"[Runtime→Editor] LOAD_ERROR: {err}");
            Application.Current.Dispatcher.InvokeAsync(() =>
                MessageBox.Show($"シーンの読み込みに失敗しました:\n{err}", "SEED Editor",
                    MessageBoxButton.OK, MessageBoxImage.Error));
        }
        else if (msg.StartsWith("FPS:", StringComparison.Ordinal) &&
                 float.TryParse(msg["FPS:".Length..],
                     System.Globalization.NumberStyles.Float,
                     System.Globalization.CultureInfo.InvariantCulture,
                     out var fps))
        {
            FpsReceived?.Invoke(fps);
        }
        else if (msg.StartsWith("PROFILER:", StringComparison.Ordinal))
        {
            // プロファイラ計測レポート（0.5秒ごと、JSON）。SET_PROFILER で購読 ON の間だけ届く。
            ProfilerReportReceived?.Invoke(msg["PROFILER:".Length..]);
        }
        else if (msg.StartsWith("PLUGIN_LIST:", StringComparison.Ordinal))
        {
            // ロード済みプラグイン一覧を受信する。
            // フォーマット: PLUGIN_LIST:[{"name":"...","version":"...","description":"..."},...]
            var json = msg["PLUGIN_LIST:".Length..];
            EditorLog.Write($"[Runtime→Editor] PLUGIN_LIST ({json.Length} chars)");
            PluginListReceived?.Invoke(json);
        }
        else if (msg.StartsWith("SCENE_INFO:", StringComparison.Ordinal))
        {
            // シーン情報を受信する（AI アシスタントの GET_SCENE_INFO 応答）。
            // フォーマット: SCENE_INFO:[{ActorData...}, ...]
            var json = msg["SCENE_INFO:".Length..];
            EditorLog.Write($"[Runtime→Editor] SCENE_INFO ({json.Length} chars)");
            SceneInfoReceived?.Invoke(json);
        }
        else if (msg.StartsWith("EXPORT_ACTOR_OK:", StringComparison.Ordinal))
        {
            var path = msg["EXPORT_ACTOR_OK:".Length..];
            EditorLog.Write($"[Runtime→Editor] EXPORT_ACTOR_OK: {path}");
            ExportActorCompleted?.Invoke(true, path);
            Application.Current.Dispatcher.InvokeAsync(() =>
                MessageBox.Show($"アクタファイルを保存しました:\n{path}", "SEED Editor",
                    MessageBoxButton.OK, MessageBoxImage.Information));
        }
        else if (msg.StartsWith("EXPORT_ACTOR_ERR:", StringComparison.Ordinal))
        {
            var err = msg["EXPORT_ACTOR_ERR:".Length..];
            EditorLog.Write($"[Runtime→Editor] EXPORT_ACTOR_ERR: {err}");
            ExportActorCompleted?.Invoke(false, err);
            Application.Current.Dispatcher.InvokeAsync(() =>
                MessageBox.Show($"アクタファイルの保存に失敗しました:\n{err}", "SEED Editor",
                    MessageBoxButton.OK, MessageBoxImage.Error));
        }
        else
        {
            EditorLog.Write($"[Runtime→Editor] unknown: {msg[..Math.Min(80, msg.Length)]}");
        }
    }

    // ── プライベート: Runtime 自己終了 ────────────────────────

    private int            _restartCount  = 0;
    private const int      MaxRestarts    = 3;
    // KillRuntime が呼ばれた後に Exited が遅延発火しても
    // クラッシュと誤判定しないためのフラグ。
    private volatile bool  _intentionalStop = false;

    private void OnRuntimeExited(object? sender, EventArgs e)
    {
        var exitCode = _process?.ExitCode ?? -1;
        EditorLog.Write($"OnRuntimeExited — exitCode={exitCode}  intentional={_intentionalStop}  restartCount={_restartCount}  embedded={_inEmbeddedPlay}");

        // 埋め込み Play 中のクラッシュでは、Edit と Play が同一プロセスのため編集セッションも
        // 道連れになる。フラグを畳んで通常のクラッシュ再起動フロー（下）へ委ねる。
        // 再起動後は _play_temp.scene 保険で最新シーンが復元される（MainWindow が再ロード）。
        _inEmbeddedPlay = false;
        // プロセスが落ちた以上、起動シーケンスは継続不能。再入ガードを解除する（不具合2の保険）。
        _isLaunching = false;
        // 方針A: READY(ウィンドウ準備完了)待機中にプロセスが終了した場合は、待機を false で解いて
        // LaunchAsync の Play 遷移を中止させる（後始末は本メソッドが継続する）。
        _playWindowReadyTcs?.TrySetResult(false);

        // KillRuntime / Stop による意図的な終了は再起動もクラッシュ計上もしない
        if (_intentionalStop)
        {
            _intentionalStop = false;
            Application.Current.Dispatcher.InvokeAsync(() =>
            {
                UninstallMinimizeHook();
                _pipe?.Dispose();
                _pipe        = null;
                _runtimeHwnd = IntPtr.Zero;
                ChangeState(EditorState.Idle);
            });
            return;
        }

        Application.Current.Dispatcher.InvokeAsync(async () =>
        {
            UninstallMinimizeHook();
            _pipe?.Dispose();
            _pipe        = null;
            _runtimeHwnd = IntPtr.Zero;

            // Play ランタイムが終了し、保存した Edit ランタイムが生きていれば即復元
            // （Idle を経由せず直接 Edit へ移行することで UI のちらつきを防ぐ）
            if (_savedEditProcess is not null && !_savedEditProcess.HasExited)
            {
                EditorLog.Write("OnRuntimeExited — Play 終了、保存した Edit ランタイムを即復元");
                RestoreEditRuntime();
                return;
            }

            // 保存 Edit なし: 通常のクラッシュ再起動フロー
            ChangeState(EditorState.Idle);

            if (_restartCount >= MaxRestarts)
            {
                EditorLog.Write("OnRuntimeExited — restart limit reached, stopping");
                MessageBox.Show(
                    $"Runtime が {MaxRestarts} 回クラッシュしたため再起動を中断しました。\nログを確認してください:\n{System.IO.Path.GetFullPath("logs/SEEDEditor.log")}",
                    "SEED Editor", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            _restartCount++;
            EditorLog.Write($"OnRuntimeExited — restarting (attempt {_restartCount})");
            try
            {
                await StartEditAsync(_viewportContainerHwnd);
            }
            catch (Exception ex)
            {
                EditorLog.Write($"OnRuntimeExited — StartEditAsync failed: {ex}");
                MessageBox.Show($"Runtime 再起動失敗:\n{ex}", "SEED Editor",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        });
    }

    // ── プライベート: Edit ランタイム復元 ─────────────────────

    /// <summary>
    /// Play 中に保存しておいた Edit ランタイムを復元する。
    /// ShowWindow で再表示するだけなので GPU 再初期化が発生せず即座に表示できる。
    /// UI スレッドから呼ぶこと。
    /// </summary>
    private void RestoreEditRuntime()
    {
        // プロセスが既に終了していた場合は通常再起動にフォールバック
        if (_savedEditProcess is null || _savedEditProcess.HasExited)
        {
            EditorLog.Write("RestoreEditRuntime — 保存した Edit プロセスが消滅、StartEditAsync にフォールバック");
            _savedEditProcess?.Dispose();
            _savedEditProcess = null;
            _savedEditPipe    = null;
            _savedEditHwnd    = IntPtr.Zero;
            _ = StartEditAsync(_viewportContainerHwnd);
            return;
        }

        EditorLog.Write($"RestoreEditRuntime — 保存 Edit ランタイムを復元  hwnd=0x{_savedEditHwnd:X}");

        // フィールドを復元
        _process     = _savedEditProcess;
        _pipe        = _savedEditPipe;
        _runtimeHwnd = _savedEditHwnd;

        _savedEditProcess = null;
        _savedEditPipe    = null;
        _savedEditHwnd    = IntPtr.Zero;

        // イベントを再購読
        if (_process is not null)
            _process.Exited += OnRuntimeExited;
        if (_pipe is not null)
            _pipe.MessageReceived += OnPipeMessage;
        // 退避していた Edit ランタイムへ戻るので、プロファイラ購読状態を同期し直す
        // （Play 中にパネルを開閉していた場合の取りこぼしを防ぐ）。
        _pipe?.Send($"SET_PROFILER:{(ProfilerEnabled ? 1 : 0)}");

        // Edit ウィンドウを再表示してコンテナサイズに合わせる
        if (_runtimeHwnd != IntPtr.Zero)
        {
            Win32.ShowWindow(_runtimeHwnd, SW_SHOW);
            ResizeRuntimeToContainer();
        }

        // クラッシュカウントをリセット
        _restartCount    = 0;
        _intentionalStop = false;

        // Edit 状態へ直接移行
        ChangeState(EditorState.Edit);

        // RuntimeHwndAvailable → MainWindow が SyncViewportSettings など後処理を実施
        // FirstFrameReady → ローディングオーバーレイを即座に非表示にする
        RuntimeHwndAvailable?.Invoke((nint)_runtimeHwnd);
        FirstFrameReady?.Invoke();
    }

    // ── プライベート: WinEvent フック（最小化検知）────────────

    private void InstallMinimizeHook()
    {
        _winEventDelegate = OnWinEvent;
        // MOVESIZESTART(0x000A) 〜 MINIMIZESTART(0x0016) を一括フック
        _winEventHook = Win32.SetWinEventHook(
            EVENT_SYSTEM_MOVESIZESTART, EVENT_SYSTEM_MINIMIZESTART,
            IntPtr.Zero, _winEventDelegate,
            _process is not null ? (uint)_process.Id : 0,
            0, WINEVENT_OUTOFCONTEXT);
    }

    private void UninstallMinimizeHook()
    {
        if (_winEventHook == IntPtr.Zero) return;
        Win32.UnhookWinEvent(_winEventHook);
        _winEventHook     = IntPtr.Zero;
        _winEventDelegate = null;
    }

    private void OnWinEvent(IntPtr hook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint thread, uint time)
    {
        // hwnd が対象の Play ランタイムでない場合もログに残す（想定外イベント検出）
        var evtName = eventType switch
        {
            EVENT_SYSTEM_MOVESIZESTART => "MOVESIZESTART",
            EVENT_SYSTEM_MOVESIZEEND   => "MOVESIZEEND",
            EVENT_SYSTEM_MINIMIZESTART => "MINIMIZESTART",
            _                          => $"0x{eventType:X4}",
        };
        var tid  = System.Threading.Thread.CurrentThread.ManagedThreadId;
        var isUi = Application.Current?.Dispatcher.CheckAccess() ?? false;
        EditorLog.Write($"OnWinEvent — {evtName}  hwnd=0x{hwnd:X}  runtimeHwnd=0x{_runtimeHwnd:X}  match={hwnd == _runtimeHwnd}  tid={tid}  ui={isUi}  state={_state}");
        if (hwnd != _runtimeHwnd) return;

        if (eventType == EVENT_SYSTEM_MINIMIZESTART)
            Application.Current.Dispatcher.InvokeAsync(Pause);
        else if (eventType == EVENT_SYSTEM_MOVESIZESTART)
            RuntimeMoveStart?.Invoke();
        else if (eventType == EVENT_SYSTEM_MOVESIZEEND)
            RuntimeMoveEnd?.Invoke();
    }

    // ── プライベート: ウィンドウ埋め込み ───────────────────────

    private void EmbedRuntimeWindow()
    {
        EditorLog.Write($"EmbedRuntimeWindow — hwnd=0x{_runtimeHwnd:X}  container=0x{_viewportContainerHwnd:X}");
        if (_runtimeHwnd == IntPtr.Zero || _viewportContainerHwnd == IntPtr.Zero) return;

        // ShowWindow(SW_RESTORE) の後に保存することで、最小化中の座標（-32000,-32000）
        // ではなく復元後の正確な位置・サイズが _runtimeRectBeforeEmbed に入る。
        // DetachRuntimeWindow で MoveWindow に使用するため正確な値が必要。
        Win32.ShowWindow(_runtimeHwnd, SW_RESTORE);

        // Resume 時に元のサイズ・位置へ戻すために保存（SW_RESTORE の後）
        Win32.GetWindowRect(_runtimeHwnd, out _runtimeRectBeforeEmbed);

        // コンテナサイズを取得する
        Win32.GetClientRect(_viewportContainerHwnd, out var rect);
        int cw = rect.Right  - rect.Left;
        int ch = rect.Bottom - rect.Top;

        // ① SetParent を最初に行う（他の操作より前に実施する）。
        //
        //    Rust 側の SurfaceError::Outdated は SetParent 後に発生する。
        //    Outdated ハンドラが GetParent(hwnd) を呼んだとき、SetParent が
        //    完了済みであれば正確なコンテナ HWND が返り、GetClientRect でコンテナの
        //    実サイズ（Vulkan の currentExtent と一致）が取得できる。
        //
        //    以前の実装では SetWindowLong(WS_CHILD) を先に呼んでいたため、
        //    WS_CHILD スタイルだが親なしの状態で GetParent がデスクトップ（1920x1080）を
        //    返し、renderer.resize(1920x1080) → depth/color サイズ不一致でクラッシュした。
        Win32.SetParent(_runtimeHwnd, _viewportContainerHwnd);

        // ② スタイルを WS_CHILD に変更（タイトルバー / リサイズ枠 / タスクバーエントリ除去）
        var style = Win32.GetWindowLong(_runtimeHwnd, GWL_STYLE);
        style = (style & ~WS_POPUP & ~WS_CAPTION & ~WS_THICKFRAME) | WS_CHILD;
        Win32.SetWindowLong(_runtimeHwnd, GWL_STYLE, style);

        var exStyle = Win32.GetWindowLong(_runtimeHwnd, GWL_EXSTYLE);
        Win32.SetWindowLong(_runtimeHwnd, GWL_EXSTYLE, exStyle & ~WS_EX_APPWINDOW);

        // ③ SWP_FRAMECHANGED で NCCALCSIZE を発火しつつ (0,0) にコンテナサイズ配置。
        //    WS_CHILD はデコレーションなしなので client = outer = cw x ch
        //    → WM_SIZE(cw, ch) のみ発生。
        if (cw > 0 && ch > 0)
            Win32.SetWindowPos(_runtimeHwnd, IntPtr.Zero, 0, 0, cw, ch,
                SWP_NOZORDER | SWP_FRAMECHANGED);
    }

    private void DetachRuntimeWindow()
    {
        EditorLog.Write($"DetachRuntimeWindow — hwnd=0x{_runtimeHwnd:X}");
        if (_runtimeHwnd == IntPtr.Zero) return;

        Win32.SetParent(_runtimeHwnd, IntPtr.Zero);

        var style = Win32.GetWindowLong(_runtimeHwnd, GWL_STYLE);
        style = (style & ~WS_CHILD) | WS_OVERLAPPEDWINDOW | WS_VISIBLE;
        Win32.SetWindowLong(_runtimeHwnd, GWL_STYLE, style);

        var exStyle = Win32.GetWindowLong(_runtimeHwnd, GWL_EXSTYLE);
        Win32.SetWindowLong(_runtimeHwnd, GWL_EXSTYLE, exStyle | WS_EX_APPWINDOW);

        Win32.SetWindowPos(_runtimeHwnd, IntPtr.Zero, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_FRAMECHANGED);

        // Pause 前のサイズ・位置を復元
        var r = _runtimeRectBeforeEmbed;
        int w = r.Right  - r.Left;
        int h = r.Bottom - r.Top;
        if (w > 0 && h > 0)
            Win32.MoveWindow(_runtimeHwnd, r.Left, r.Top, w, h, repaint: true);

        Win32.ShowWindow(_runtimeHwnd, SW_SHOWDEFAULT);
        EditorLog.Write($"DetachRuntimeWindow — 完了");
    }

    // ── プライベート: プロセス終了 ─────────────────────────────

    private void KillRuntime(bool sendStop = false)
    {
        // Exited が Kill より先に発火する競合があるため、先にフラグを立てる
        _intentionalStop = true;
        UninstallMinimizeHook();
        if (sendStop) _pipe?.Send("STOP");
        _pipe?.Dispose();
        _pipe = null;
        if (_process is not null)
        {
            // Kill 前に購読解除 — Kill 後に Exited が発火して OnRuntimeExited が
            // 新 _pipe を Dispose するレースコンディションを防ぐ。
            _process.Exited -= OnRuntimeExited;
            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill();
                    _process.WaitForExit(500);
                }
            }
            catch (Exception ex)
            {
                // Kill が失敗（アクセス拒否・既終了レース等）してもプロセス参照は破棄まで進める。
                // 例外を握り潰さないと Dispose の後続（保存 Edit／常駐 Play の Kill）が
                // スキップされてプロセスがリークする（エディタ終了後の SEED.exe 残留の原因）。
                EditorLog.Write($"KillRuntime — kill failed: {ex.Message}");
            }
            _process.Dispose();
        }
        _process     = null;
        _runtimeHwnd = IntPtr.Zero;
    }

    /// <summary>
    /// Runtime の作業ディレクトリを解決する。
    /// Cargo ビルド出力（target/debug, target/release）の場合は 2 階層上に戻し、
    /// assets/ などが存在するプロジェクトルートを返す。
    /// 配布時（exe 隣に assets/ がある構成）はそのまま exe のディレクトリを返す。
    /// </summary>
    private static string ResolveWorkingDirectory(string exePath)
    {
        var exeDir     = Path.GetDirectoryName(exePath)!;
        var buildType  = Path.GetFileName(exeDir);                    // "debug" / "release"
        var targetDir  = Path.GetFileName(Path.GetDirectoryName(exeDir)!); // "target"

        if ((buildType is "debug" or "release") && targetDir == "target")
            return Path.GetFullPath(Path.Combine(exeDir, @"..\.."));

        return exeDir;
    }

    private void ChangeState(EditorState next)
    {
        _state = next;
        StateChanged?.Invoke(next);
    }

    public void Dispose()
    {
        // エディタ終了時は、管理下の全ランタイムプロセス（通常 _process・保存 Edit・常駐 Play）を
        // 確実に終了させ、エディタを閉じた後に SEED.exe が残留しないようにする。
        // 各終了処理は独立した try/catch で囲み、いずれかが例外を投げても残りのプロセス終了が
        // スキップされない（＝リークしない）ようにする。

        // ① 現在アクティブなランタイム（Play 中なら Play、Edit 中なら Edit）を終了する。
        try { KillRuntime(); }
        catch (Exception ex) { EditorLog.Write($"Dispose — KillRuntime failed: {ex.Message}"); }

        // ② Play 中に非表示保持していた Edit ランタイムを終了する（Play 中にウィンドウを閉じた場合など）。
        try
        {
            if (_savedEditProcess is not null)
            {
                if (!_savedEditProcess.HasExited)
                {
                    _savedEditPipe?.Send("STOP");
                    _savedEditPipe?.Dispose();
                    try { _savedEditProcess.Kill(); _savedEditProcess.WaitForExit(500); }
                    catch (Exception ex) { EditorLog.Write($"Dispose — saved Edit kill failed: {ex.Message}"); }
                }
                _savedEditProcess.Dispose();
                _savedEditProcess = null;
                _savedEditPipe    = null;
                _savedEditHwnd    = IntPtr.Zero;
            }
        }
        catch (Exception ex) { EditorLog.Write($"Dispose — saved Edit cleanup failed: {ex.Message}"); }

        // ③ Stop 後も Kill せず常駐保持していた Play ランタイムを確実に終了する（リーク防止）。
        //    ①②③ を独立 try/catch にしたことで、どれか 1 つの Kill が失敗しても他は必ず実行され、
        //    管理下の全プロセスが終了する（本来は各ランタイムの --parent-pid 監視も保険になる）。
        try { DisposePersistentPlayRuntime(killProcess: true); }
        catch (Exception ex) { EditorLog.Write($"Dispose — persistent Play cleanup failed: {ex.Message}"); }

        // 起動シーケンスのキャンセルソースを破棄する
        _launchCts?.Dispose();
        _launchCts = null;

        _sourceWatcher?.Dispose();
    }

    // ============================================================
    //  Win32 P/Invoke（ネストクラス）
    // ============================================================

    private static class Win32
    {
        [DllImport("user32.dll")]
        internal static extern IntPtr SetWinEventHook(
            uint eventMin, uint eventMax,
            IntPtr hmodWinEventProc,
            WinEventProc lpfnWinEventProc,
            uint idProcess, uint idThread, uint dwFlags);

        [DllImport("user32.dll")]
        internal static extern bool UnhookWinEvent(IntPtr hWinEventHook);

        [DllImport("user32.dll")]
        internal static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        internal static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll")]
        internal static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);

        [DllImport("user32.dll")]
        internal static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        internal static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        internal static extern bool MoveWindow(
            IntPtr hWnd, int x, int y, int w, int h, bool repaint);

        [DllImport("user32.dll")]
        internal static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        internal static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        internal static extern bool SetWindowPos(
            IntPtr hWnd, IntPtr hWndInsertAfter,
            int x, int y, int cx, int cy, uint uFlags);

        /// <summary>Win32 矩形構造体。</summary>
        [StructLayout(LayoutKind.Sequential)]
        internal struct RECT { public int Left, Top, Right, Bottom; }
    }
}
