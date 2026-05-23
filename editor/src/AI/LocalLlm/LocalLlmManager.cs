// ============================================================
//  LocalLlmManager.cs — ローカル LLM サーバーのライフサイクル管理
//
//  llama-server.exe（llama.cpp）をエディタ内蔵サーバーとして起動し、
//  OpenAI 互換 API エンドポイントを提供する。
//
//  モデルは初回起動時に %APPDATA%/SEED/models/ へダウンロードして永続キャッシュする。
//  エンジン同梱の llama-server.exe が存在しない場合は使用不可を通知する。
//
//  使用モデル: Qwen2.5-Coder-7B-Instruct Q4_K_M (Apache 2.0, 商用利用可)
//  ソース: https://huggingface.co/bartowski/Qwen2.5-Coder-7B-Instruct-GGUF
// ============================================================

using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace SEEDEditor.AI.LocalLlm;

/// <summary>
/// llama-server.exe のプロセス起動・停止とモデルの初回ダウンロードを管理する。
///
/// ライフサイクル:
///   EnsureServerRunningAsync() → モデル未取得なら自動ダウンロード → llama-server.exe 起動
///   StopServer() / Dispose()   → プロセス終了
/// </summary>
public sealed class LocalLlmManager : IDisposable
{
    // ── 定数 ────────────────────────────────────────────────

    /// <summary>llama-server が待ち受けるポート番号</summary>
    private const int SERVER_PORT = 8480;

    /// <summary>デフォルトモデルのファイル名（Qwen2.5-Coder 7B Q4_K_M 量子化）</summary>
    private const string DEFAULT_MODEL_FILENAME = "Qwen2.5-Coder-7B-Instruct-Q4_K_M.gguf";

    /// <summary>
    /// モデルのダウンロード URL。
    /// Bartowski 氏の HuggingFace リポジトリから Q4_K_M 量子化版を取得する。
    /// ライセンス: Apache 2.0（商用利用可・制限なし）
    /// サイズ: 約 4.4GB
    /// </summary>
    private const string DEFAULT_MODEL_DOWNLOAD_URL =
        "https://huggingface.co/bartowski/Qwen2.5-Coder-7B-Instruct-GGUF/resolve/main/Qwen2.5-Coder-7B-Instruct-Q4_K_M.gguf";

    /// <summary>
    /// サーバー起動後の接続待機タイムアウト（秒）。
    /// Qwen2.5-Coder 7B のモデルロードは VRAM 状況次第で 90 秒以上かかる場合がある。
    /// </summary>
    private const int SERVER_START_TIMEOUT_SEC = 180;

    /// <summary>接続確認のポーリング間隔（ミリ秒）</summary>
    private const int SERVER_POLL_INTERVAL_MS = 500;

    /// <summary>ファイルが有効なモデルと見なす最低サイズ（バイト）</summary>
    private const long MIN_MODEL_SIZE_BYTES = 1_000_000L;

    // ── フィールド ──────────────────────────────────────────

    /// <summary>llama-server.exe のフルパス</summary>
    private readonly string _serverExePath;

    /// <summary>モデルキャッシュファイルのフルパス</summary>
    private readonly string _modelPath;

    /// <summary>実行中のサーバープロセス</summary>
    private Process? _serverProcess;

    // ── 公開プロパティ ──────────────────────────────────────

    /// <summary>
    /// llama-server のベース URL。
    /// OpenAICompatibleProvider が /v1/chat/completions を付加するため /v1 は含めない。
    /// </summary>
    public string Endpoint => $"http://localhost:{SERVER_PORT}";

    /// <summary>llama-server.exe がエディタに同梱されているか</summary>
    public bool IsServerExeAvailable => File.Exists(_serverExePath);

    /// <summary>モデルファイルがキャッシュ済みで有効な状態か</summary>
    public bool IsModelDownloaded =>
        File.Exists(_modelPath) && new FileInfo(_modelPath).Length > MIN_MODEL_SIZE_BYTES;

    /// <summary>サーバーが現在起動中か</summary>
    public bool IsServerRunning => _serverProcess is not null && !_serverProcess.HasExited;

    // ── コンストラクタ ──────────────────────────────────────

    /// <summary>
    /// ローカル LLM マネージャーを初期化する。
    /// </summary>
    /// <param name="editorDir">エディタ実行ファイルのディレクトリ（llama-server.exe の配置場所）</param>
    public LocalLlmManager(string editorDir)
    {
        _serverExePath = Path.Combine(editorDir, "llama-server.exe");

        // モデルキャッシュディレクトリを作成する（存在する場合は何もしない）
        var modelsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SEED", "models");
        Directory.CreateDirectory(modelsDir);

        _modelPath = Path.Combine(modelsDir, DEFAULT_MODEL_FILENAME);
    }

    // ── 公開メソッド ─────────────────────────────────────────

    /// <summary>
    /// モデルが未ダウンロードの場合はダウンロードし、
    /// サーバーが未起動の場合は起動する。
    /// 既にサーバーが動いていれば即座に true を返す。
    /// </summary>
    /// <param name="onProgress">進捗メッセージのコールバック</param>
    /// <param name="ct">キャンセルトークン</param>
    /// <returns>サーバーが使用可能になった場合 true</returns>
    public async Task<bool> EnsureServerRunningAsync(
        Action<string>? onProgress = null,
        CancellationToken ct = default)
    {
        // llama-server.exe が存在しない場合は使用不可
        if (!IsServerExeAvailable)
        {
            onProgress?.Invoke("llama-server.exe が見つかりません。エディタを再インストールしてください。");
            EditorLog.Write($"LocalLlmManager — llama-server.exe が見つかりません: {_serverExePath}");
            return false;
        }

        // モデルが未ダウンロードの場合はダウンロードする
        if (!IsModelDownloaded)
        {
            onProgress?.Invoke($"モデルを初回ダウンロード中... ({DEFAULT_MODEL_FILENAME}, 約4.4GB)");
            var downloadOk = await DownloadModelAsync(onProgress, ct);
            if (!downloadOk) return false;
        }

        // 既にサーバーが起動中の場合はそのまま使用する
        if (IsServerRunning)
        {
            EditorLog.Write("LocalLlmManager — サーバーは既に起動中");
            return true;
        }

        // サーバーを起動する
        return await StartServerAsync(onProgress, ct);
    }

    /// <summary>サーバープロセスを停止する。</summary>
    public void StopServer()
    {
        if (_serverProcess is null || _serverProcess.HasExited) return;

        EditorLog.Write("LocalLlmManager — llama-server を停止します");
        try
        {
            _serverProcess.Kill();
            _serverProcess.WaitForExit(3000);
        }
        catch (Exception ex)
        {
            EditorLog.Write($"LocalLlmManager — サーバー停止失敗: {ex.Message}");
        }
        finally
        {
            _serverProcess.Dispose();
            _serverProcess = null;
        }
    }

    // ── プライベートメソッド — モデルダウンロード ───────────────

    /// <summary>
    /// モデルファイルを HuggingFace からダウンロードする。
    /// 1% ごとに進捗を報告し、一時ファイルに保存してから正規パスへ移動する。
    /// ダウンロード中断時は一時ファイルを削除して不完全なファイルが残らないようにする。
    /// </summary>
    private async Task<bool> DownloadModelAsync(
        Action<string>? onProgress,
        CancellationToken ct)
    {
        EditorLog.Write($"LocalLlmManager — ダウンロード開始: {DEFAULT_MODEL_DOWNLOAD_URL}");

        // ダウンロード中断時に不完全なファイルが残らないよう一時ファイルに保存する
        var tempPath = _modelPath + ".part";
        try
        {
            using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            using var response = await http.GetAsync(
                DEFAULT_MODEL_DOWNLOAD_URL,
                HttpCompletionOption.ResponseHeadersRead,
                ct);

            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength ?? -1L;
            await using var responseStream = await response.Content.ReadAsStreamAsync(ct);
            await using var fileStream     = File.Create(tempPath);

            // 64KB バッファで読み込みながら進捗を報告する
            var buffer       = new byte[65536];
            long downloaded  = 0;
            int  lastPercent = -1;
            int  bytesRead;

            while ((bytesRead = await responseStream.ReadAsync(buffer, ct)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
                downloaded += bytesRead;

                if (totalBytes > 0)
                {
                    var percent = (int)(downloaded * 100L / totalBytes);
                    // 1% 変化ごとに進捗を通知する（過剰な通知を避ける）
                    if (percent != lastPercent)
                    {
                        lastPercent = percent;
                        var downloadedMb = downloaded / (1024.0 * 1024.0);
                        var totalMb      = totalBytes  / (1024.0 * 1024.0);
                        onProgress?.Invoke(
                            $"ダウンロード中... {percent}% ({downloadedMb:F0} MB / {totalMb:F0} MB)");
                    }
                }
            }

            // 完了後に正規パスへリネームする
            fileStream.Close();
            File.Move(tempPath, _modelPath, overwrite: true);
            EditorLog.Write($"LocalLlmManager — ダウンロード完了: {_modelPath}");
            onProgress?.Invoke("モデルのダウンロード完了！");
            return true;
        }
        catch (OperationCanceledException)
        {
            onProgress?.Invoke("ダウンロードがキャンセルされました。");
            TryDeleteFile(tempPath);
            return false;
        }
        catch (Exception ex)
        {
            EditorLog.Write($"LocalLlmManager — ダウンロード失敗: {ex.Message}");
            onProgress?.Invoke($"ダウンロードに失敗しました: {ex.Message}");
            TryDeleteFile(tempPath);
            return false;
        }
    }

    // ── プライベートメソッド — サーバー起動 ───────────────────

    /// <summary>
    /// llama-server.exe を起動し、HTTP エンドポイントが応答するまで待機する。
    /// タイムアウト（60 秒）を超えた場合は失敗とする。
    /// </summary>
    private async Task<bool> StartServerAsync(
        Action<string>? onProgress,
        CancellationToken ct)
    {
        EditorLog.Write($"LocalLlmManager — llama-server 起動: {_serverExePath}");
        onProgress?.Invoke("ローカル AI サーバーを起動中...");

        try
        {
            _serverProcess = Process.Start(new ProcessStartInfo
            {
                FileName  = _serverExePath,
                // コンテキスト長 8192（シーン情報 JSON が大きくなるため余裕を持たせる）
                // GPU レイヤー最大適用（VRAM が足りなければ自動で CPU フォールバック）
                // --jinja: モデル組み込みの Jinja2 チャットテンプレートを有効化する。
                // Qwen2.5-Coder はこのテンプレートでツール呼び出し形式を定義しており、
                // これなしだとモデルがツール呼び出しをテキストとして出力してしまい実行されない。
                Arguments = $"--model \"{_modelPath}\" --port {SERVER_PORT} --ctx-size 8192 --n-gpu-layers 99 --parallel 1 --jinja",
                UseShellExecute        = false,
                CreateNoWindow         = true,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
            }) ?? throw new InvalidOperationException("llama-server.exe の起動に失敗しました。");

            _serverProcess.OutputDataReceived += (_, e) =>
            {
                if (e.Data is not null) EditorLog.Write($"[llama-server] {e.Data}");
            };
            _serverProcess.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is not null) EditorLog.Write($"[llama-server:err] {e.Data}");
            };
            _serverProcess.BeginOutputReadLine();
            _serverProcess.BeginErrorReadLine();
        }
        catch (Exception ex)
        {
            EditorLog.Write($"LocalLlmManager — サーバー起動失敗: {ex.Message}");
            onProgress?.Invoke($"サーバーの起動に失敗しました: {ex.Message}");
            return false;
        }

        // サーバーが HTTP 接続を受け付けるまでポーリングして待つ
        var deadline = DateTime.UtcNow.AddSeconds(SERVER_START_TIMEOUT_SEC);
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };

        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();

            // サーバーが予期せず終了した場合は即失敗とする
            if (_serverProcess.HasExited)
            {
                onProgress?.Invoke("サーバーが予期せず終了しました。ログを確認してください。");
                EditorLog.Write("LocalLlmManager — サーバーが起動直後に終了");
                return false;
            }

            try
            {
                // OpenAI 互換の /v1/models で疎通確認する
                var res = await http.GetAsync($"http://localhost:{SERVER_PORT}/v1/models", ct);
                if (res.IsSuccessStatusCode)
                {
                    EditorLog.Write("LocalLlmManager — サーバー接続確認 OK");
                    onProgress?.Invoke("ローカル AI サーバー起動完了！");
                    return true;
                }
            }
            catch { /* まだ起動中のため無視 */ }

            await Task.Delay(SERVER_POLL_INTERVAL_MS, ct);
        }

        onProgress?.Invoke($"サーバーが {SERVER_START_TIMEOUT_SEC} 秒以内に起動しませんでした。");
        EditorLog.Write("LocalLlmManager — サーバー起動タイムアウト");
        return false;
    }

    // ── プライベートメソッド — ユーティリティ ─────────────────

    /// <summary>ファイルが存在する場合のみ削除する（例外は無視）。</summary>
    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* 無視 */ }
    }

    public void Dispose() => StopServer();
}
