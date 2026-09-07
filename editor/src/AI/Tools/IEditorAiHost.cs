// ============================================================
//  IEditorAiHost.cs — AI ツールがエディタ本体へ要求する操作の契約
//
//  MCP / HTTP ブリッジから呼ばれる「見た目の確認」「再生制御」「保存」などは、
//  MainWindow が持つ状態（現在のシーンパス・選択中アクター・ウィンドウハンドル）や
//  UI 操作（Play / Pause / Stop / Ctrl+S）が必要になる。
//  EditorCommandExecutor から MainWindow を直接参照すると依存が逆流するため、
//  「AI ツールが必要とする最小限の操作」だけをこのインターフェイスに切り出し、
//  MainWindow 側（MainWindow.AiHost.cs）で実装する。
//
//  【スレッド前提】
//  すべてのメンバーは WPF UI スレッドから呼ばれる（SeedAIBridge が Dispatcher へ
//  マーシャルしてから EditorCommandExecutor を呼ぶ）。実装側でのスレッド切替は不要。
// ============================================================

using System.Threading.Tasks;
using SEEDEditor.Runtime;

namespace SEEDEditor.AI.Tools;

/// <summary>
/// AI ツール（MCP / HTTP ブリッジ）がエディタ本体へ要求する操作の契約。
/// 実装は MainWindow（partial: MainWindow.AiHost.cs）。
/// </summary>
public interface IEditorAiHost
{
    // ── 状態の取得 ───────────────────────────────────────────────

    /// <summary>ランタイムの現在状態（Idle / Building / Launching / Edit / Play / Pause）。</summary>
    EditorState RuntimeState { get; }

    /// <summary>ランタイムとの名前付きパイプが接続済みか。</summary>
    bool RuntimeConnected { get; }

    /// <summary>現在開いているシーンファイルの絶対パス（未保存の新規シーンなら null）。</summary>
    string? CurrentScenePath { get; }

    /// <summary>ヒエラルキーで選択中のアクターの DFS ID。未選択なら -1。</summary>
    int SelectedActorDfsId { get; }

    /// <summary>
    /// ランタイムから最後に届いたヒエラルキー JSON（HIERARCHY: の本体）。
    /// 未受信なら空配列 "[]"。ランタイムは変化時にのみ push するためキャッシュを返す。
    /// </summary>
    string HierarchyJson { get; }

    // ── ウィンドウハンドル（スクリーンショット用）────────────────

    /// <summary>エディタのメインウィンドウ HWND。</summary>
    nint EditorWindowHandle { get; }

    /// <summary>
    /// ランタイム（シーンビュー／埋め込み Play ゲーム画面）のウィンドウ HWND。
    /// 未起動なら 0。埋め込み Play 中はシーンビューと同一ウィンドウになる。
    /// </summary>
    nint RuntimeWindowHandle { get; }

    // ── 操作 ─────────────────────────────────────────────────────

    /// <summary>ランタイムへ生の IPC コマンド文字列を送る（低レベルの逃げ道）。</summary>
    void SendIpc(string command);

    /// <summary>
    /// 指定 DFS ID のアクターを選択し、ランタイムから ACTOR_COMPONENTS が返るまで待つ。
    /// ヒエラルキーパネルのクリックと同じ IPC 経路（SELECT: と GET_ACTOR_COMPONENTS:）を通る。
    /// </summary>
    /// <param name="dfsId">選択するアクターの DFS ID。</param>
    /// <param name="timeoutMs">応答待ちのタイムアウト（ミリ秒）。</param>
    /// <returns>ACTOR_COMPONENTS の JSON。タイムアウト時は null。</returns>
    Task<string?> SelectActorAsync(int dfsId, int timeoutMs);

    /// <summary>
    /// プロファイラの一発計測（PROFILE_DUMP）を実行し、ランタイムが書き出した
    /// ダンプ JSON の中身を返す。計測中はランタイムが自動でプロファイラを有効化する
    /// （プロファイラパネルを開いている必要はない）。
    /// </summary>
    /// <param name="seconds">計測する実時間（秒）。</param>
    /// <param name="timeoutMs">応答待ちのタイムアウト（ミリ秒）。計測秒数より十分長くすること。</param>
    /// <returns>ダンプ JSON 文字列。タイムアウト・失敗時は null。</returns>
    Task<string?> ProfileDumpAsync(double seconds, int timeoutMs);

    /// <summary>
    /// ゲーム入力注入コマンド（<c>INPUT_*</c>）をランタイムへ送り、1 行応答を待つ。
    ///
    /// <para>
    /// ランタイムは受理で <c>INPUT_OK</c>、拒否で <c>INPUT_ERROR:{reason}</c> を返す。
    /// <c>INPUT_SEQUENCE</c> だけは受理応答のあとに、全イベントを撃ち終えた時点で
    /// <c>INPUT_SEQUENCE_DONE</c> が非同期で届く（詳細は docs/editor_mcp.md 9 章）。
    /// </para>
    /// </summary>
    /// <param name="command">送信する IPC 文字列（例: <c>INPUT_KEY:W,down</c>）。改行を含めないこと。</param>
    /// <param name="timeoutMs">応答待ちのタイムアウト（ミリ秒）。</param>
    /// <param name="waitSequenceDone">
    /// true なら <c>INPUT_OK</c> のあとさらに <c>INPUT_SEQUENCE_DONE</c> まで待つ
    /// （<c>INPUT_SEQUENCE</c> 用）。false なら最初の応答で返る。
    /// </param>
    /// <returns>
    /// 受け取った応答行（<c>INPUT_OK</c> / <c>INPUT_ERROR:...</c> / <c>INPUT_SEQUENCE_DONE</c>）。
    /// タイムアウト・ランタイム未初期化のときは null。
    /// </returns>
    Task<string?> InjectGameInputAsync(string command, int timeoutMs, bool waitSequenceDone);

    /// <summary>
    /// 再生制御（play / pause / resume / stop）を実行する。
    /// エディタのプレイバーのボタンと同じ経路を通る。
    /// </summary>
    /// <param name="action">"play" / "pause" / "resume" / "stop"。</param>
    /// <returns>実行できなかった場合の理由。実行できたら null。</returns>
    Task<string?> ControlPlayAsync(string action);

    /// <summary>
    /// 現在のシーンを保存する（Ctrl+S と同じ経路）。
    /// </summary>
    /// <param name="timeoutMs">保存完了通知を待つタイムアウト（ミリ秒）。</param>
    /// <returns>成功なら null、失敗ならその理由。</returns>
    Task<string?> SaveSceneAsync(int timeoutMs);

    /// <summary>
    /// ランタイムに GPU 読み戻しスクリーンショットを撮らせる（IPC <c>SCREENSHOT:</c>）。
    ///
    /// <para>
    /// 画面 DC からの BitBlt と違い、ウィンドウが隠れていても・画面外にあっても撮れる。
    /// ヘッドレス運用ではこちらが既定の撮影手段になる。
    /// </para>
    /// </summary>
    /// <param name="target">"game" / "viewport"（どちらも提示中のカラーターゲット）。</param>
    /// <param name="path">書き出し先の絶対パス（.png）。</param>
    /// <param name="timeoutMs">応答待ちのタイムアウト（ミリ秒）。</param>
    /// <returns>
    /// 成功なら <c>(true, パス, 幅, 高さ)</c>、失敗なら <c>(false, エラーメッセージ, 0, 0)</c>。
    /// </returns>
    Task<(bool Ok, string Message, int Width, int Height)> CaptureRuntimeScreenshotAsync(
        string target, string path, int timeoutMs);

    /// <summary>
    /// ランタイムに .actor 単体を読み込ませ、透過 PNG のサムネイルを描かせる
    /// （IPC <c>RENDER_ACTOR_THUMBNAIL:</c>）。図鑑（魚カタログ）画像の生成に使う。
    ///
    /// <para>
    /// ランタイムは現在のシーンを壊さずオフスクリーンで 1 枚描き、
    /// <c>RENDER_ACTOR_THUMBNAIL_DONE:{path}</c> か
    /// <c>RENDER_ACTOR_THUMBNAIL_ERROR:{message}</c> を 1 行だけ返す。
    /// 応答は 1 往復ごとに 1 通しか来ないため、呼び出し側は必ず逐次で使うこと
    /// （並行して撃つと、どの応答がどの依頼のものか区別できない）。
    /// </para>
    /// </summary>
    /// <param name="actorPath">描く .actor のパス（<c>assets://</c> URI または絶対パス）。カンマ不可。</param>
    /// <param name="outPngPath">書き出し先 PNG の絶対パス。カンマ不可。</param>
    /// <param name="sizePx">出力画像の一辺のピクセル数（正方形）。</param>
    /// <param name="view">視点。"side" / "front" / "top" のいずれか。</param>
    /// <param name="timeoutMs">応答待ちのタイムアウト（ミリ秒）。</param>
    /// <returns>
    /// 成功なら <c>(true, 書き出された PNG のパス)</c>、
    /// 失敗・タイムアウトなら <c>(false, 理由メッセージ)</c>。
    /// </returns>
    Task<(bool Ok, string Message)> RenderActorThumbnailAsync(
        string actorPath, string outPngPath, int sizePx, string view, int timeoutMs);

    /// <summary>
    /// エディタを正常終了させる（ヘッドレス運用の後始末）。
    /// ランタイム子プロセスの停止を含め、通常のウィンドウクローズと同じ経路を通す。
    /// 呼び出しは即座に返り、実際の終了は次のディスパッチャ周回で行われる
    /// （HTTP 応答を返す前にプロセスが消えると、呼び出し側がエラーになるため）。
    /// </summary>
    void RequestShutdown();
}
