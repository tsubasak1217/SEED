// ============================================================
//  AnthropicProvider.cs — Anthropic Claude API プロバイダー
//
//  Anthropic の Messages API（/v1/messages）に対応する実装。
//  OpenAI と異なる点:
//    - ヘッダー: x-api-key + anthropic-version
//    - ツール定義: tools[].input_schema（parameters ではなく）
//    - ツール結果: role="user" + content[].type="tool_result"
//    - システムメッセージ: messages から分離してトップレベルに
// ============================================================

using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using SEEDEditor.AI.Models;

namespace SEEDEditor.AI.Providers;

/// <summary>
/// Anthropic Claude API へリクエストを送信するプロバイダー。
/// claude-opus-4-7 / claude-sonnet-4-6 / claude-haiku-4-5 等に対応する。
/// </summary>
public class AnthropicProvider : IAIProvider
{
    // ── 定数 ────────────────────────────────────────────────
    private const string API_ENDPOINT    = "https://api.anthropic.com/v1/messages";
    private const string ANTHROPIC_VER   = "2023-06-01";
    private const int    MAX_TOKENS      = 4096;

    // ── フィールド ───────────────────────────────────────────
    private readonly HttpClient _http;
    private readonly AISettings _settings;

    // ── コンストラクタ ──────────────────────────────────────

    /// <summary>設定を受け取って HTTP クライアントを初期化する。</summary>
    public AnthropicProvider(AISettings settings)
    {
        _settings = settings;
        _http     = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
    }

    // ── IAIProvider ─────────────────────────────────────────

    public string Name         => "Anthropic Claude";
    public bool   IsConfigured => !string.IsNullOrWhiteSpace(_settings.ApiKey);

    /// <summary>
    /// /v1/messages へリクエストを送信し応答を返す。
    /// Anthropic のツール呼び出し形式を AIResponse に変換して返す。
    /// </summary>
    public async Task<AIResponse> ChatAsync(List<ChatMessage> messages, List<ToolDefinition> tools)
    {
        // システムメッセージを抽出する（Anthropic はトップレベル）
        var systemMsg = messages.FirstOrDefault(m => m.Role == "system")?.Content ?? "";
        var nonSystem = messages.Where(m => m.Role != "system").ToList();

        // メッセージを Anthropic フォーマットに変換する
        var msgArray = BuildMessages(nonSystem);

        // リクエスト JSON を組み立てる
        var requestBody = new Dictionary<string, object>
        {
            ["model"]      = _settings.Model,
            ["max_tokens"] = MAX_TOKENS,
            ["messages"]   = msgArray,
        };
        if (!string.IsNullOrEmpty(systemMsg))
            requestBody["system"] = systemMsg;

        if (tools.Count > 0)
            requestBody["tools"] = BuildTools(tools);

        var json    = JsonSerializer.Serialize(requestBody);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        // Anthropic 専用ヘッダーを付与する
        using var request = new HttpRequestMessage(HttpMethod.Post, API_ENDPOINT) { Content = content };
        request.Headers.Add("x-api-key",         _settings.ApiKey);
        request.Headers.Add("anthropic-version",  ANTHROPIC_VER);

        // リクエストを送信する
        using var response = await _http.SendAsync(request);
        var responseText   = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Anthropic API エラー {(int)response.StatusCode}: {responseText}");

        return ParseResponse(responseText);
    }

    // ── プライベートヘルパー ─────────────────────────────────

    /// <summary>ChatMessage リストを Anthropic メッセージ配列に変換する。</summary>
    private static List<object> BuildMessages(List<ChatMessage> messages)
    {
        var result = new List<object>();
        foreach (var m in messages)
        {
            if (m.Role == "tool")
            {
                // ツール実行結果: role="user" + content[].type="tool_result"
                result.Add(new
                {
                    role    = "user",
                    content = new[]
                    {
                        new
                        {
                            type        = "tool_result",
                            tool_use_id = m.ToolCallId ?? "",
                            content     = m.Content,
                        }
                    },
                });
            }
            else if (m.ToolCalls != null && m.ToolCalls.Count > 0)
            {
                // アシスタントのツール呼び出しメッセージ
                var contentBlocks = new List<object>();
                if (!string.IsNullOrEmpty(m.Content))
                    contentBlocks.Add(new { type = "text", text = m.Content });

                foreach (var tc in m.ToolCalls)
                {
                    contentBlocks.Add(new
                    {
                        type  = "tool_use",
                        id    = tc.Id,
                        name  = tc.FunctionName,
                        input = JsonSerializer.Deserialize<object>(tc.ArgumentsJson),
                    });
                }

                result.Add(new { role = m.Role, content = contentBlocks });
            }
            else
            {
                result.Add(new { role = m.Role, content = m.Content });
            }
        }
        return result;
    }

    /// <summary>ToolDefinition リストを Anthropic ツール配列に変換する。</summary>
    private static List<object> BuildTools(List<ToolDefinition> tools)
    {
        return tools.Select(t => (object)new
        {
            name         = t.Name,
            description  = t.Description,
            input_schema = JsonSerializer.Deserialize<object>(t.ParametersJson),
        }).ToList();
    }

    /// <summary>Anthropic レスポンス JSON を AIResponse に変換する。</summary>
    private static AIResponse ParseResponse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root      = doc.RootElement;
        var stopReason = root.TryGetProperty("stop_reason", out var sr) ? sr.GetString() ?? "end_turn" : "end_turn";

        var response = new AIResponse { StopReason = stopReason };

        if (!root.TryGetProperty("content", out var contentArr)) return response;

        foreach (var block in contentArr.EnumerateArray())
        {
            var blockType = block.TryGetProperty("type", out var bt) ? bt.GetString() : "";
            switch (blockType)
            {
                case "text":
                    // テキストブロック
                    if (block.TryGetProperty("text", out var txt))
                        response.TextContent = (response.TextContent ?? "") + txt.GetString();
                    break;

                case "tool_use":
                    // ツール呼び出しブロック
                    var inputJson = block.TryGetProperty("input", out var inp)
                        ? inp.GetRawText()
                        : "{}";
                    response.ToolCalls.Add(new ToolCall
                    {
                        Id            = block.TryGetProperty("id",   out var id)   ? id.GetString()   ?? "" : "",
                        FunctionName  = block.TryGetProperty("name", out var nm)   ? nm.GetString()   ?? "" : "",
                        ArgumentsJson = inputJson,
                    });
                    break;
            }
        }

        return response;
    }
}
