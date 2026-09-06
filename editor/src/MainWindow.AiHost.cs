// ============================================================
//  MainWindow.AiHost.cs — MainWindow による IEditorAiHost 実装
//
//  MCP / HTTP ブリッジ（SeedAIBridge → EditorCommandExecutor）から呼ばれる
//  「エディタ本体の状態取得・UI 操作」を提供する。
//  AI 側の都合をパネルやランタイム管理へ漏らさないよう、窓口をここ 1 箇所に集約する。
//
//  【設計方針】
//   ・既存の操作経路を再利用する（Play/Stop はプレイバーのハンドラ、保存は Ctrl+S と同じ
//     DoQuickSave、選択はヒエラルキーと同じ SELECT: IPC）。AI 専用の別経路を作らない。
//   ・非同期完了はランタイムからの応答イベント（ACTOR_COMPONENTS / SaveCompleted）か、
//     状態のポーリングで待つ。いずれもタイムアウト付きで UI スレッドを固めない。
// ============================================================

using System;

using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using SEEDEditor.AI.Tools;
using SEEDEditor.Runtime;

namespace SEEDEditor;

public partial class MainWindow : IEditorAiHost
{
    // ── 定数 ─────────────────────────────────────────────────────

    /// <summary>ヒエラルキーの仮想アクター ID のオフセット。SELECT: IPC で DFS ID に加算する。</summary>
    private const uint AiVirtualActorIdBase = 999_000_000u;

    /// <summary>再生状態の遷移待ちのポーリング間隔（ミリ秒）。</summary>
    private const int AiStatePollIntervalMs = 50;

    /// <summary>
    /// Play 開始の遷移待ちタイムアウト（ミリ秒）。
    /// スクリプト再コンパイルや一時シーン保存を挟むため他より長いが、
    /// HTTP 応答を無限に待たせないよう上限を設ける（超過時はエラーとして状態を返す）。
    /// </summary>
    private const int AiPlayStateTimeoutMs = 60_000;

    /// <summary>Stop / Pause / Resume の遷移待ちのタイムアウト（ミリ秒）。</summary>
    private const int AiSimpleStateTimeoutMs = 15_000;

    /// <summary>選択なしを表す DFS ID。</summary>
    private const int AiNoSelection = -1;

    // ── 状態キャッシュ ───────────────────────────────────────────

    /// <summary>
    /// ランタイムから最後に届いたヒエラルキー JSON。
    /// ランタイムは変化時にのみ push するため、AI からの問い合わせにはこのキャッシュで応える。
    /// </summary>
    private string _aiHierarchyJson = "[]";

    /// <summary>最後に選択されたアクターの DFS ID（未選択は -1）。</summary>
    private int _aiSelectedActorDfsId = AiNoSelection;

    // ── 初期化 ───────────────────────────────────────────────────

    /// <summary>
    /// AI ツール向けの状態キャッシュ購読を開始する。MainWindow のコンストラクタから呼ぶ。
    /// ランタイムが無い（未起動）場合は何もしない。
    /// </summary>
    private void InitAiHost()
    {
        if (_runtimeManager is null) return;

        // ヒエラルキーは変化時 push のみ。問い合わせに答えられるよう最新版を保持する。
        _runtimeManager.HierarchyUpdated += json => _aiHierarchyJson = json;

        // 選択 ID は仮想 ID（999_000_000 + DFS ID）で届くことがあるため DFS ID へ正規化する。
        _runtimeManager.SelectionChanged += idx => _aiSelectedActorDfsId = NormalizeActorId(idx);
    }

    /// <summary>仮想アクター ID（999_000_000 以上）を DFS ID へ正規化する。</summary>
    private static int NormalizeActorId(int idx)
    {
        if (idx < 0) return AiNoSelection;
        return idx >= (int)AiVirtualActorIdBase ? idx - (int)AiVirtualActorIdBase : idx;
    }

    // ── IEditorAiHost: 状態の取得 ────────────────────────────────

    /// <inheritdoc/>
    EditorState IEditorAiHost.RuntimeState => _runtimeManager?.State ?? EditorState.Idle;

    /// <inheritdoc/>
    bool IEditorAiHost.RuntimeConnected => _runtimeManager?.IsPipeConnected ?? false;

    /// <inheritdoc/>
    string? IEditorAiHost.CurrentScenePath => _currentScenePath;

    /// <inheritdoc/>
    int IEditorAiHost.SelectedActorDfsId => _aiSelectedActorDfsId;

    /// <inheritdoc/>
    string IEditorAiHost.HierarchyJson => _aiHierarchyJson;

    // ── IEditorAiHost: ウィンドウハンドル ────────────────────────

    /// <inheritdoc/>
    nint IEditorAiHost.EditorWindowHandle => new WindowInteropHelper(this).Handle;

    /// <inheritdoc/>
    nint IEditorAiHost.RuntimeWindowHandle => _runtimeManager?.RuntimeHwnd ?? nint.Zero;

    // ── IEditorAiHost: 操作 ──────────────────────────────────────

    /// <inheritdoc/>
    void IEditorAiHost.SendIpc(string command) => _runtimeManager?.SendToRuntime(command);

    /// <inheritdoc/>
    async Task<string?> IEditorAiHost.SelectActorAsync(int dfsId, int timeoutMs)
    {
        if (_runtimeManager is null) return null;

        // 応答を取りこぼさないよう、送信より先に購読する。
        // ランタイム側のイベントはパイプ受信スレッドで発火するため、
        // 継続を非同期実行にして UI スレッドの再入を避ける。
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnComponents(string json) => tcs.TrySetResult(json);
        _runtimeManager.ActorComponentsReceived += OnComponents;

        try
        {
            // ヒエラルキーのクリックと同じ経路。SELECT: で選択状態そのものを動かし、
            // GET_ACTOR_COMPONENTS: で確実に ACTOR_COMPONENTS を 1 通返させる。
            _runtimeManager.SendToRuntime($"SELECT:{AiVirtualActorIdBase + (uint)dfsId}");
            _runtimeManager.SendToRuntime($"GET_ACTOR_COMPONENTS:{dfsId}");
            _aiSelectedActorDfsId = dfsId;

            var completed = await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs));
            return completed == tcs.Task ? await tcs.Task : null;
        }
        finally
        {
            _runtimeManager.ActorComponentsReceived -= OnComponents;
        }
    }

    /// <inheritdoc/>
    async Task<string?> IEditorAiHost.ControlPlayAsync(string action)
    {
        if (_runtimeManager is null) return "ランタイムが初期化されていません。";

        var state = _runtimeManager.State;
        var empty = new RoutedEventArgs();

        switch (action)
        {
            case "play":
                if (state != EditorState.Edit)
                    return $"play は Edit 状態でのみ実行できます（現在: {state}）。";
                // プレイバーのボタンと同じハンドラを通す（スクリプト検証・一時保存を含む）
                OnPlayPause(this, empty);
                return await WaitForStateAsync(EditorState.Play, AiPlayStateTimeoutMs);

            case "pause":
                if (state != EditorState.Play)
                    return $"pause は Play 状態でのみ実行できます（現在: {state}）。";
                OnPlayPause(this, empty);
                return await WaitForStateAsync(EditorState.Pause, AiSimpleStateTimeoutMs);

            case "resume":
                if (state != EditorState.Pause)
                    return $"resume は Pause 状態でのみ実行できます（現在: {state}）。";
                OnPlayPause(this, empty);
                return await WaitForStateAsync(EditorState.Play, AiSimpleStateTimeoutMs);

            case "stop":
                if (state is not (EditorState.Play or EditorState.Pause))
                    return $"stop は Play / Pause 状態でのみ実行できます（現在: {state}）。";
                OnStop(this, empty);
                return await WaitForStateAsync(EditorState.Edit, AiSimpleStateTimeoutMs);

            default:
                return $"不明な action '{action}'（play / pause / resume / stop のいずれか）。";
        }
    }

    /// <inheritdoc/>
    async Task<string?> IEditorAiHost.SaveSceneAsync(int timeoutMs)
    {
        if (_runtimeManager is null) return "ランタイムが初期化されていません。";
        if (_runtimeManager.State != EditorState.Edit)
            return $"シーン保存は Edit 状態でのみ実行できます（現在: {_runtimeManager.State}）。";
        if (_currentScenePath is null && _activeActorPath is null)
            return "保存先が未確定です（新規シーン）。エディタで一度「名前を付けて保存」してください。";

        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnSaved(bool ok, string err) => tcs.TrySetResult(ok ? null : err);
        _runtimeManager.SaveCompleted += OnSaved;

        try
        {
            // Ctrl+S と同じ経路（アクタータブが開いていればアクター保存になる）
            DoQuickSave();

            var completed = await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs));
            if (completed != tcs.Task)
                return $"保存完了通知が {timeoutMs} ms 以内に返りませんでした。";
            return await tcs.Task;
        }
        finally
        {
            _runtimeManager.SaveCompleted -= OnSaved;
        }
    }

    // ── 内部ヘルパー ─────────────────────────────────────────────

    /// <summary>
    /// ランタイムが目的の状態になるまでポーリングで待つ。
    /// await Task.Delay でディスパッチャへ制御を返すため UI スレッドを固めない。
    /// </summary>
    /// <returns>到達したら null、タイムアウトしたらその理由。</returns>
    private async Task<string?> WaitForStateAsync(EditorState target, int timeoutMs)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (_runtimeManager?.State == target) return null;
            await Task.Delay(AiStatePollIntervalMs);
        }
        return $"{timeoutMs} ms 以内に {target} 状態へ遷移しませんでした"
             + $"（現在: {_runtimeManager?.State.ToString() ?? "不明"}）。";
    }
}
