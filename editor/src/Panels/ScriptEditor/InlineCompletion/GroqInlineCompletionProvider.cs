using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace SEEDEditor.Panels.ScriptEditor.InlineCompletion;

/// <summary>
/// Groq のクラウド推論（OpenAI 互換 Chat Completions API）を使ったインライン補完。
///
/// Groq は FIM 専用エンドポイントを持たないため、カーソル前後のコードを与えて
/// 「カーソル位置に挿入するコードだけを出力せよ」と指示するチャット方式で補完する。
/// ストリーミングで受信し、新しい入力が来たら接続を切って生成を止める（無駄な消費を防ぐ）。
/// </summary>
public sealed class GroqInlineCompletionProvider : IInlineCompletionProvider
{
    /// <summary>Groq の OpenAI 互換エンドポイント（チャット補完）。</summary>
    private const string EndpointUrl = "https://api.groq.com/openai/v1/chat/completions";

    /// <summary>Groq の利用可能モデル一覧エンドポイント。</summary>
    private const string ModelsUrl = "https://api.groq.com/openai/v1/models";

    // ── 生成・文脈パラメータ ────────────────────────────────
    private const int    MaxTokens      = 160;
    private const int    MaxPrefixChars = 2000;
    private const int    MaxSuffixChars = 800;
    private const double Temperature    = 0.2;
    private const int    RequestTimeoutSec = 20;

    /// <summary>補完エンジンとしての役割を固定するシステムプロンプト（基本方針）。</summary>
    private const string SystemPromptBase =
        "You are an inline code completion engine for the C# scripting of a custom game engine called SEED (NOT Unity). " +
        "Given the code before and after the cursor, output ONLY the raw code to insert at the cursor to continue it. " +
        "Do not repeat the existing code. Do not use the UnityEngine namespace or MonoBehaviour. " +
        "Use ONLY the SEED engine API described in the reference below, plus the .NET base class library. " +
        "Do not add explanations or markdown code fences.";

    /// <summary>
    /// 実際に使うシステムプロンプト。基本方針に加え、スクリプト API リファレンス
    /// （docs/scripting_api.md）を「正典」として注入し、モデルに SEED 独自 API を教える。
    /// リファレンスは初回のみ読み込まれキャッシュされる。
    /// </summary>
    private static string BuildSystemPrompt()
    {
        var reference = ScriptApiReference.Load();
        if (string.IsNullOrEmpty(reference)) return SystemPromptBase;
        return SystemPromptBase
             + "\n\n=== SEED Script API reference (authoritative; the only engine API you may use) ===\n"
             + reference;
    }

    // APIキー・モデル名は設定から都度読む（設定変更が即反映される）
    private readonly Func<string> _apiKey;
    private readonly Func<string> _model;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(RequestTimeoutSec) };

    /// <summary>
    /// レート制限（429）を受けたら、この時刻まではリクエストを送らない（クールダウン）。
    /// retry-after を尊重することで、上限中に無駄打ちして 429 を量産するのを防ぐ。
    /// </summary>
    private DateTime _cooldownUntil = DateTime.MinValue;

    /// <summary>クールダウン中スキップのログを 1 回だけ出すためのフラグ（毎回出すとうるさいため）。</summary>
    private bool _cooldownLogged;

    /// <summary>retry-after が取れなかった 429 に対する既定クールダウン秒数。</summary>
    private const int DefaultCooldownSec = 20;

    public GroqInlineCompletionProvider(Func<string> apiKey, Func<string> model)
    {
        _apiKey = apiKey;
        _model  = model;
    }

    /// <summary>API キーが設定されていれば利用可能とみなす。</summary>
    public bool IsAvailable => !string.IsNullOrWhiteSpace(_apiKey());

    public async Task<string?> GetCompletionAsync(string prefix, string suffix, CancellationToken ct)
    {
        var apiKey = _apiKey();
        if (string.IsNullOrWhiteSpace(apiKey)) return null;

        // レート制限クールダウン中は API を叩かない（無駄打ちで 429 を量産しないため）。
        // 「理由なく止まって見える」のを防ぐため、クールダウン中である旨を 1 回だけログする。
        if (DateTime.UtcNow < _cooldownUntil)
        {
            if (!_cooldownLogged)
            {
                _cooldownLogged = true;
                int remain = (int)Math.Ceiling((_cooldownUntil - DateTime.UtcNow).TotalSeconds);
                SEEDEditor.EditorLog.Write($"[インライン補完] レート制限クールダウン中（あと約{remain}s）のためスキップ");
            }
            return null;
        }

        // 文脈はカーソル近傍だけに絞る
        string p = prefix.Length > MaxPrefixChars ? prefix[^MaxPrefixChars..] : prefix;
        string s = suffix.Length > MaxSuffixChars ? suffix[..MaxSuffixChars] : suffix;

        string userMessage =
            "Code before the cursor:\n```csharp\n" + p + "\n```\n" +
            "Code after the cursor:\n```csharp\n" + s + "\n```\n" +
            "Output only the code to insert at the cursor.";

        var request = new ChatRequest
        {
            Model    = _model(),
            MaxTokens = MaxTokens,
            Temperature = Temperature,
            Stream   = true,
            Messages = new[]
            {
                new ChatMessage { Role = "system", Content = BuildSystemPrompt() },
                new ChatMessage { Role = "user",   Content = userMessage },
            },
        };

        try
        {
            var json = JsonSerializer.Serialize(request);
            using var req = new HttpRequestMessage(HttpMethod.Post, EndpointUrl)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

            using var res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!res.IsSuccessStatusCode)
            {
                // 429（レート制限）などの原因を判別できるよう、本文とレート制限ヘッダも記録する。
                // Groq のヘッダ: x-ratelimit-remaining-requests / -tokens（どちらが 0 かで RPM/TPM を判別）、
                // retry-after（再試行までの秒数）。本文にも "Rate limit reached ... try again in Xs" が入る。
                string Header(string name) =>
                    res.Headers.TryGetValues(name, out var v) ? string.Join(",", v) : "-";
                string body = "";
                try { body = await res.Content.ReadAsStringAsync(ct); } catch { /* ignore */ }
                if (body.Length > 300) body = body[..300];
                body = body.Replace('\n', ' ').Replace('\r', ' ');

                var retryAfter = Header("retry-after");
                SEEDEditor.EditorLog.Write(
                    $"[インライン補完] Groq HTTP {(int)res.StatusCode} {res.StatusCode} " +
                    $"retry-after={retryAfter} " +
                    $"remaining-req={Header("x-ratelimit-remaining-requests")} " +
                    $"remaining-tok={Header("x-ratelimit-remaining-tokens")} " +
                    $"body={body}");

                // 429（レート制限）は retry-after ぶんクールダウンし、その間は送信を止める。
                if ((int)res.StatusCode == 429)
                {
                    int sec = int.TryParse(retryAfter, out var ra) && ra > 0 ? ra : DefaultCooldownSec;
                    _cooldownUntil = DateTime.UtcNow.AddSeconds(sec + 1);   // +1s は境界の余裕
                    _cooldownLogged = false;                                // 次のクールダウンで再度 1 回ログする
                    SEEDEditor.EditorLog.Write($"[インライン補完] レート制限のため {sec}s クールダウンします");
                }
                return null;
            }

            await using var stream = await res.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(stream);

            // OpenAI 互換 SSE: 各行 "data: {json}"、末尾は "data: [DONE]"
            var sb = new StringBuilder();
            while (await reader.ReadLineAsync(ct) is { } line)
            {
                if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
                var payload = line[5..].Trim();
                if (payload.Length == 0) continue;
                if (payload == "[DONE]") break;

                ChatChunk? chunk;
                try { chunk = JsonSerializer.Deserialize<ChatChunk>(payload); }
                catch { continue; }

                var delta = chunk?.Choices is { Length: > 0 } c ? c[0].Delta?.Content : null;
                if (!string.IsNullOrEmpty(delta)) sb.Append(delta);
            }

            // 推論モデル（qwen3 等）は思考過程を <think>…</think> で吐くため取り除く。
            var text = StripCodeFences(StripReasoning(sb.ToString()));
            return string.IsNullOrEmpty(text) ? null : text;
        }
        catch (OperationCanceledException) { return null; } // 新しい入力で破棄
        catch (Exception ex)
        {
            SEEDEditor.EditorLog.Write($"[インライン補完] Groq リクエスト失敗: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 指定 API キーで利用可能なモデル ID 一覧を取得する（ソート済み）。
    /// キー未設定・エラー時は空リストを返す。設定画面のモデル選択用。
    /// </summary>
    public static async Task<IReadOnlyList<string>> FetchModelsAsync(string apiKey, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey)) return Array.Empty<string>();
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            using var req = new HttpRequestMessage(HttpMethod.Get, ModelsUrl);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

            using var res = await http.SendAsync(req, ct);
            if (!res.IsSuccessStatusCode)
            {
                SEEDEditor.EditorLog.Write($"[インライン補完] Groq モデル一覧 HTTP {(int)res.StatusCode} {res.StatusCode}");
                return Array.Empty<string>();
            }

            var body   = await res.Content.ReadAsStringAsync(ct);
            var parsed = JsonSerializer.Deserialize<ModelList>(body);
            return parsed?.Data?
                .Where(m => !string.IsNullOrEmpty(m.Id))
                .Select(m => m.Id!)
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToList() ?? (IReadOnlyList<string>)Array.Empty<string>();
        }
        catch (Exception ex)
        {
            SEEDEditor.EditorLog.Write($"[インライン補完] Groq モデル一覧の取得失敗: {ex.Message}");
            return Array.Empty<string>();
        }
    }

    /// <summary>
    /// 推論モデル（qwen3 / deepseek-r1 等）が吐く思考ブロック &lt;think&gt;…&lt;/think&gt; を除去する。
    /// 閉じタグがある場合はそのブロックを丸ごと削り、思考だけで出力が終わった
    /// （閉じられていない）場合はコードが無いとみなして空文字を返す。
    /// </summary>
    private static string StripReasoning(string s)
    {
        const string open  = "<think>";
        const string close = "</think>";

        int start = s.IndexOf(open, StringComparison.OrdinalIgnoreCase);
        while (start >= 0)
        {
            int end = s.IndexOf(close, start, StringComparison.OrdinalIgnoreCase);
            if (end < 0) return "";                       // 思考のみで打ち切り＝補完なし
            s = s.Remove(start, end - start + close.Length);
            start = s.IndexOf(open, StringComparison.OrdinalIgnoreCase);
        }
        return s.Trim();
    }

    /// <summary>チャットモデルが付けがちな ```csharp フェンスを取り除く。</summary>
    private static string StripCodeFences(string s)
    {
        s = s.Trim();
        if (s.StartsWith("```", StringComparison.Ordinal))
        {
            int nl = s.IndexOf('\n');
            if (nl >= 0) s = s[(nl + 1)..];        // 先頭の ```lang 行を除去
            int fence = s.LastIndexOf("```", StringComparison.Ordinal);
            if (fence >= 0) s = s[..fence];         // 末尾の ``` を除去
        }
        return s;
    }

    // ── OpenAI 互換 Chat Completions の入出力 DTO ───────────────

    private sealed class ChatRequest
    {
        [JsonPropertyName("model")]       public string        Model       { get; set; } = "";
        [JsonPropertyName("messages")]    public ChatMessage[] Messages    { get; set; } = Array.Empty<ChatMessage>();
        [JsonPropertyName("max_tokens")]  public int           MaxTokens   { get; set; }
        [JsonPropertyName("temperature")] public double        Temperature { get; set; }
        [JsonPropertyName("stream")]      public bool          Stream      { get; set; }
    }

    private sealed class ChatMessage
    {
        [JsonPropertyName("role")]    public string Role    { get; set; } = "";
        [JsonPropertyName("content")] public string Content { get; set; } = "";
    }

    private sealed class ChatChunk
    {
        [JsonPropertyName("choices")] public Choice[]? Choices { get; set; }
    }

    private sealed class Choice
    {
        [JsonPropertyName("delta")] public Delta? Delta { get; set; }
    }

    private sealed class Delta
    {
        [JsonPropertyName("content")] public string? Content { get; set; }
    }

    // ── モデル一覧 DTO（/v1/models）───────────────────────────

    private sealed class ModelList
    {
        [JsonPropertyName("data")] public ModelInfo[]? Data { get; set; }
    }

    private sealed class ModelInfo
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
    }
}
