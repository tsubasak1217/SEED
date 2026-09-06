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
}
