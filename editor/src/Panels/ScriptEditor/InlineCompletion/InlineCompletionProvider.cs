using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using SEEDEditor.AI.LocalLlm;

namespace SEEDEditor.Panels.ScriptEditor.InlineCompletion;

/// <summary>
/// GitHub Copilot 風のインライン補完（ゴーストテキスト）を、内蔵ローカル LLM
/// （llama-server 上の Qwen2.5-Coder）を使って生成するプロバイダ。
///
/// カーソル前後のコードを FIM（Fill-In-the-Middle）プロンプトに組み立て、
/// llama-server の /completion エンドポイントへ投げて「間に入るコード」を得る。
/// サーバーが起動していないときは何も返さない（起動はエディタ側が明示的に行う）。
/// </summary>
public sealed class InlineCompletionProvider
{
    // ── FIM 制御トークン（Qwen2.5-Coder のフォーマット）──────────
    private const string FimPrefixToken = "<|fim_prefix|>";
    private const string FimSuffixToken = "<|fim_suffix|>";
    private const string FimMiddleToken = "<|fim_middle|>";

    // ── 生成・文脈のパラメータ（マジックナンバーを避け定数化）────
    /// <summary>1 回の補完で生成する最大トークン数（1 行想定なので控えめ＝高速）。</summary>
    private const int MaxPredictTokens = 64;
    // プロンプトキャッシュ無効時は毎回プロンプト全体を処理するため、
    // 文脈を短めにして 1 回あたりの計算量（＝重さ）を抑える。
    /// <summary>プロンプトに含めるカーソル前テキストの最大文字数。</summary>
    private const int MaxPrefixChars = 800;
    /// <summary>プロンプトに含めるカーソル後テキストの最大文字数。</summary>
    private const int MaxSuffixChars = 400;
    /// <summary>サンプリング温度（低め＝確定的でノイズの少ない補完）。</summary>
    private const double Temperature = 0.2;
    /// <summary>核サンプリング（上位確率のみ＝安定した候補）。</summary>
    private const double TopP = 0.95;
    /// <summary>1 リクエストのタイムアウト（秒）。</summary>
    private const int RequestTimeoutSec = 12;

    /// <summary>
    /// 生成停止トークン。FIM 制御・特殊トークンに加え、改行でも停止する。
    /// 表示は 1 行のみなので、改行以降の生成を止めることでレイテンシを大きく削る。
    /// </summary>
    private static readonly string[] StopTokens =
    {
        "\n",
        "<|endoftext|>", FimPrefixToken, FimSuffixToken, FimMiddleToken,
        "<|fim_pad|>", "<|repo_name|>", "<|file_sep|>",
    };

    private readonly LocalLlmManager _llm;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(RequestTimeoutSec) };

    public InlineCompletionProvider(LocalLlmManager llm) => _llm = llm;

    /// <summary>サーバーが起動済みで補完リクエストを送れる状態か。</summary>
    public bool IsAvailable => _llm.IsServerRunning;

    /// <summary>
    /// カーソル前後のテキストから補完候補を取得する。
    /// サーバー未起動・エラー・空応答のときは null を返す。
    /// </summary>
    /// <param name="prefix">カーソルより前の全テキスト</param>
    /// <param name="suffix">カーソルより後の全テキスト</param>
    /// <param name="ct">キャンセルトークン（新しい入力が来たら破棄する）</param>
    public async Task<string?> GetCompletionAsync(string prefix, string suffix, CancellationToken ct)
    {
        if (!_llm.IsServerRunning) return null;

        // 文脈が長すぎるとレイテンシが増すため、カーソル近傍だけに絞る
        string p = prefix.Length > MaxPrefixChars ? prefix[^MaxPrefixChars..] : prefix;
        string s = suffix.Length > MaxSuffixChars ? suffix[..MaxSuffixChars] : suffix;

        // FIM プロンプト: <|fim_prefix|>前<|fim_suffix|>後<|fim_middle|>
        string fimPrompt = FimPrefixToken + p + FimSuffixToken + s + FimMiddleToken;

        var request = new CompletionRequest
        {
            Prompt      = fimPrompt,
            NPredict    = MaxPredictTokens,
            Temperature = Temperature,
            TopP        = TopP,
            Stop        = StopTokens,
            // ストリーミングで受信する。新しい入力が来て ct がキャンセルされると
            // 接続が切れ、llama-server 側も生成を打ち切る。これにより、タイプ中に
            // 7B 推論がキューへ溜まってマシン全体が固まる問題を防ぐ（最重要）。
            Stream      = true,
            // プロンプトキャッシュは使わない（サーバー側 --cache-ram 0 と対）。
            // 空き RAM が少ない環境ではキャッシュ保持が逆にディスクスワップを招くため。
            CachePrompt = false,
        };

        // localhost は環境により IPv6(::1) を先に試して接続失敗することがあるため、
        // サーバーがバインドしている IPv4(127.0.0.1) を明示して確実に繋ぐ。
        string url = _llm.Endpoint.Replace("localhost", "127.0.0.1") + "/completion";

        try
        {
            var json = JsonSerializer.Serialize(request);
            using var req = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
            // ヘッダー受信時点で返してもらい、本文はストリームで逐次読む
            using var res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!res.IsSuccessStatusCode)
            {
                SEEDEditor.EditorLog.Write($"[インライン補完] HTTP {(int)res.StatusCode} {res.StatusCode}");
                return null;
            }

            await using var stream = await res.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(stream);

            var sb = new StringBuilder();
            // SSE: 各行が "data: {json}"。content を積み上げ、改行・stop で打ち切る。
            while (await reader.ReadLineAsync(ct) is { } line)
            {
                if (line.Length == 0 || !line.StartsWith("data:", StringComparison.Ordinal)) continue;

                var payload = line[5..].Trim();
                CompletionResponse? chunk;
                try { chunk = JsonSerializer.Deserialize<CompletionResponse>(payload); }
                catch { continue; }

                if (!string.IsNullOrEmpty(chunk?.Content)) sb.Append(chunk!.Content);

                // 1 行分そろった（改行を含む）か、サーバーが停止したら十分。
                // 早期に return するとレスポンスが破棄され、接続切断で生成も止まる。
                if (chunk?.Stop == true) break;
                if (sb.Length > 0 && sb.ToString().IndexOf('\n') >= 0) break;
            }

            return sb.Length == 0 ? null : sb.ToString();
        }
        catch (OperationCanceledException) { return null; } // 新しい入力で破棄
        catch (Exception ex)
        {
            SEEDEditor.EditorLog.Write($"[インライン補完] リクエスト失敗: {ex.GetType().Name}: {ex.Message}");
            return null; // 通信・解析エラーは黙って無効
        }
    }

    // ── llama-server /completion の入出力 DTO ───────────────────

    /// <summary>/completion リクエストボディ。</summary>
    private sealed class CompletionRequest
    {
        [JsonPropertyName("prompt")]       public string   Prompt      { get; set; } = "";
        [JsonPropertyName("n_predict")]    public int      NPredict    { get; set; }
        [JsonPropertyName("temperature")]  public double   Temperature { get; set; }
        [JsonPropertyName("top_p")]        public double   TopP        { get; set; }
        [JsonPropertyName("stop")]         public string[] Stop        { get; set; } = Array.Empty<string>();
        [JsonPropertyName("stream")]       public bool     Stream      { get; set; }
        [JsonPropertyName("cache_prompt")] public bool     CachePrompt { get; set; }
    }

    /// <summary>/completion レスポンス（ストリーミング 1 チャンク／最終まとめ共通）。</summary>
    private sealed class CompletionResponse
    {
        [JsonPropertyName("content")] public string? Content { get; set; }
        [JsonPropertyName("stop")]    public bool    Stop    { get; set; }
    }
}
