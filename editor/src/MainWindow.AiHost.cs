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

using System.IO;
using System.Threading;
using System.Threading.Channels;
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

    /// <summary>ゲーム入力注入（INPUT_*）でランタイムが未接続のときに返す応答（擬似応答）。</summary>
    private const string AiInputNotConnectedReply = "INPUT_ERROR:runtime_not_connected";

    /// <summary>撮影応答（SCREENSHOT_DONE:）のフィールド区切り文字。</summary>
    private const string AiScreenshotFieldSeparator = ",";

    /// <summary>撮影応答の最小フィールド数（パス・幅・高さ）。</summary>
    private const int AiScreenshotFieldCount = 3;

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
    async Task<string?> IEditorAiHost.ProfileDumpAsync(double seconds, int timeoutMs)
    {
        if (_runtimeManager is null) return null;

        // 応答を取りこぼさないよう、送信より先に購読する
        // （SelectActorAsync と同じ理由: イベントはパイプ受信スレッドで発火する）。
        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnReady(string path)  => tcs.TrySetResult(path);
        void OnFailed(string _)    => tcs.TrySetResult(null);
        _runtimeManager.ProfileDumpReady  += OnReady;
        _runtimeManager.ProfileDumpFailed += OnFailed;

        try
        {
            _runtimeManager.SendToRuntime(
                $"PROFILE_DUMP:{seconds.ToString(System.Globalization.CultureInfo.InvariantCulture)}");

            var completed = await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs));
            if (completed != tcs.Task) return null;

            var path = await tcs.Task;
            if (string.IsNullOrEmpty(path)) return null;

            // ランタイムはエディタと同一マシン・同一ユーザーで動くので、
            // 書き出されたファイルをそのまま読める。
            try
            {
                return await File.ReadAllTextAsync(path);
            }
            catch (Exception)
            {
                // 読めない場合（消された・権限など）はタイムアウトと同様に null 扱いにする。
                return null;
            }
        }
        finally
        {
            _runtimeManager.ProfileDumpReady  -= OnReady;
            _runtimeManager.ProfileDumpFailed -= OnFailed;
        }
    }

    /// <inheritdoc/>
    async Task<string?> IEditorAiHost.InjectGameInputAsync(
        string command, int timeoutMs, bool waitSequenceDone)
    {
        if (_runtimeManager is null) return null;
        // 未接続なら送っても誰も応答しない。タイムアウトを待たせず、
        // 呼び出し元が INPUT_ERROR: と同じ扱いで整形できる擬似応答を返す。
        if (!_runtimeManager.IsPipeConnected) return AiInputNotConnectedReply;

        // 応答を取りこぼさないよう、送信より先に購読する
        // （SelectActorAsync と同じ理由: イベントはパイプ受信スレッドで発火する）。
        // INPUT_SEQUENCE は「受理 → 完了」の 2 通が届くため、TaskCompletionSource ではなく
        // キュー（Channel）で受けて 1 通ずつ読み進める。
        var channel = Channel.CreateUnbounded<string>(
            new UnboundedChannelOptions { SingleReader = true });
        void OnReply(string line) => channel.Writer.TryWrite(line);
        _runtimeManager.InputInjectReplyReceived += OnReply;

        try
        {
            _runtimeManager.SendToRuntime(command);

            // タイムアウトは「一連の応答を待ち切る全体の制限時間」として 1 本で管理する。
            using var cts = new CancellationTokenSource(timeoutMs);
            var accepted  = false;   // INPUT_OK を受け取ったか（DONE の取り違え防止に使う）

            while (true)
            {
                string line;
                try
                {
                    line = await channel.Reader.ReadAsync(cts.Token);
                }
                catch (OperationCanceledException)
                {
                    return null;     // タイムアウト
                }

                // 拒否はその場で確定。理由をそのまま呼び出し元へ渡す。
                if (line.StartsWith(RuntimeManager.INPUT_ERROR_PREFIX, StringComparison.Ordinal))
                    return line;

                if (line == RuntimeManager.INPUT_OK_MESSAGE)
                {
                    if (!waitSequenceDone) return line;
                    accepted = true; // 受理された。あとは完了通知を待つ。
                    continue;
                }

                if (line == RuntimeManager.INPUT_SEQUENCE_DONE_MESSAGE)
                {
                    // 受理応答より先に来た DONE は、以前に投げっぱなしにした
                    // シーケンス（wait:false）の残りなので自分のものではない。
                    if (waitSequenceDone && !accepted) continue;
                    return line;
                }

                // 未知の応答（将来の拡張）は無視して待ち続ける。
            }
        }
        finally
        {
            _runtimeManager.InputInjectReplyReceived -= OnReply;
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
        // 別インスタンスがこのシーンを開いている間は保存させない。
        // ここで弾かないと DoQuickSave が黙って何もせず、保存完了通知を待ち続けてしまう。
        if (SceneSaveDenialReason is { } denial) return denial;

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

    /// <inheritdoc/>
    async Task<(bool Ok, string Message, int Width, int Height)>
        IEditorAiHost.CaptureRuntimeScreenshotAsync(string target, string path, int timeoutMs)
    {
        if (_runtimeManager is null)
            return (false, "ランタイムが初期化されていません。", 0, 0);
        if (!_runtimeManager.IsPipeConnected)
            return (false, "ランタイムへ接続されていません（未起動 / 起動中）。", 0, 0);

        // 応答を取りこぼさないよう、送信より先に購読する。
        // ランタイム側のイベントはパイプ受信スレッドで発火するため、
        // 継続を非同期実行にして UI スレッドの再入を避ける。
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnDone(string reply) => tcs.TrySetResult(reply);
        _runtimeManager.ScreenshotCompleted += OnDone;

        try
        {
            _runtimeManager.SendToRuntime($"SCREENSHOT:{target},{path}");

            var completed = await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs));
            if (completed != tcs.Task)
                return (false, $"ランタイムから撮影応答が {timeoutMs} ms 以内に返りませんでした。", 0, 0);

            return ParseScreenshotReply(await tcs.Task);
        }
        finally
        {
            _runtimeManager.ScreenshotCompleted -= OnDone;
        }
    }

    /// <summary>
    /// ランタイムの撮影応答を解釈する。
    ///   成功: <c>SCREENSHOT_DONE:{path},{width},{height}</c>
    ///   失敗: <c>SCREENSHOT_ERROR:{message}</c>
    /// パスにカンマは含まれない前提で、末尾 2 個のカンマで幅・高さを切り出す。
    /// </summary>
    private static (bool Ok, string Message, int Width, int Height) ParseScreenshotReply(string reply)
    {
        if (reply.StartsWith(RuntimeManager.SCREENSHOT_ERROR_PREFIX, StringComparison.Ordinal))
            return (false, reply[RuntimeManager.SCREENSHOT_ERROR_PREFIX.Length..], 0, 0);

        if (!reply.StartsWith(RuntimeManager.SCREENSHOT_DONE_PREFIX, StringComparison.Ordinal))
            return (false, $"想定外の撮影応答です: {reply}", 0, 0);

        var body  = reply[RuntimeManager.SCREENSHOT_DONE_PREFIX.Length..];
        var parts = body.Split(AiScreenshotFieldSeparator);
        if (parts.Length < AiScreenshotFieldCount)
            return (false, $"撮影応答の書式が不正です: {reply}", 0, 0);

        // 幅・高さは末尾 2 要素。残り（先頭側）を連結し直したものがパス。
        var width  = int.TryParse(parts[^2], out var w) ? w : 0;
        var height = int.TryParse(parts[^1], out var h) ? h : 0;
        var path   = string.Join(AiScreenshotFieldSeparator, parts[..^2]);
        return (true, path, width, height);
    }

    /// <inheritdoc/>
    void IEditorAiHost.RequestShutdown()
    {
        EditorLog.Write("[AI ツール] shutdown 要求を受理しました。エディタを終了します。");
        // HTTP 応答を返しきってから落とすため、次のディスパッチャ周回へ回す。
        // Application.Shutdown は通常終了と同じ経路で OnWindowClosing を通し、
        // ランタイム子プロセスの停止・レイアウト保存を行う。
        Dispatcher.BeginInvoke(new Action(() => Application.Current.Shutdown()),
            System.Windows.Threading.DispatcherPriority.ApplicationIdle);
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
