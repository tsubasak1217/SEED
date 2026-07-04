using System;
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
    /// <summary>プロンプトに含めるカーソル前テキストの最大文字数。</summary>
    private const int MaxPrefixChars = 2000;
    /// <summary>プロンプトに含めるカーソル後テキストの最大文字数。</summary>
    private const int MaxSuffixChars = 1000;
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
            Stream      = false,
            // KV キャッシュを再利用する。タイプするたびに伸びる共通プレフィックスの
            // 再計算を省けるため、対話的な補完のレイテンシが大幅に下がる（最大の高速化）。
            CachePrompt = true,
        };

        // localhost は環境により IPv6(::1) を先に試して接続失敗することがあるため、
        // サーバーがバインドしている IPv4(127.0.0.1) を明示して確実に繋ぐ。
        string url = _llm.Endpoint.Replace("localhost", "127.0.0.1") + "/completion";

        try
        {
            var json    = JsonSerializer.Serialize(request);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var res     = await _http.PostAsync(url, content, ct);
            if (!res.IsSuccessStatusCode)
            {
                SEEDEditor.EditorLog.Write($"[インライン補完] HTTP {(int)res.StatusCode} {res.StatusCode}");
                return null;
            }

            var body   = await res.Content.ReadAsStringAsync(ct);
            var parsed = JsonSerializer.Deserialize<CompletionResponse>(body);
            var text   = parsed?.Content;
            return string.IsNullOrEmpty(text) ? null : text;
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

    /// <summary>/completion レスポンスボディ（content のみ利用）。</summary>
    private sealed class CompletionResponse
    {
        [JsonPropertyName("content")] public string? Content { get; set; }
    }
}
