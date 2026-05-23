// ============================================================
//  OpenAICompatibleProvider.cs — OpenAI API 互換プロバイダー
//
//  OpenAI API 仕様（/v1/chat/completions）に準拠した実装。
//  公式 OpenAI API のほか Ollama など互換サービスでも動作する。
//
//  ツール呼び出しは tool_choice: "auto" で AI に委ねる。
// ============================================================

using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SEEDEditor.AI.Models;

namespace SEEDEditor.AI.Providers;

/// <summary>
/// OpenAI API 互換エンドポイントへリクエストを送信するプロバイダー。
/// GPT-4o / GPT-4o-mini / Ollama（llama3 等）に対応する。
/// </summary>
public class OpenAICompatibleProvider : IAIProvider
{
    // ── 定数 ────────────────────────────────────────────────
    private const string DEFAULT_ENDPOINT = "https://api.openai.com/v1/chat/completions";

    // ── フィールド ───────────────────────────────────────────
    private readonly HttpClient  _http;
    private readonly AISettings  _settings;

    // ── コンストラクタ ──────────────────────────────────────

    /// <summary>設定を受け取って HTTP クライアントを初期化する。</summary>
    public OpenAICompatibleProvider(AISettings settings)
    {
        _settings = settings;
        _http     = new HttpClient { Timeout = TimeSpan.FromSeconds(settings.TimeoutSeconds) };
    }

    // ── IAIProvider ─────────────────────────────────────────

    public string Name         => "OpenAI 互換";
    public bool   IsConfigured => !string.IsNullOrWhiteSpace(_settings.ApiKey)
                               || !string.IsNullOrWhiteSpace(_settings.Endpoint);

    /// <summary>
    /// /v1/chat/completions へリクエストを送信し応答を返す。
    /// ツール呼び出しが含まれる場合は AIResponse.ToolCalls に格納する。
    /// </summary>
    public async Task<AIResponse> ChatAsync(List<ChatMessage> messages, List<ToolDefinition> tools)
    {
        // エンドポイントを決定する（空なら公式 OpenAI）
        var endpoint = string.IsNullOrWhiteSpace(_settings.Endpoint)
            ? DEFAULT_ENDPOINT
            : _settings.Endpoint.TrimEnd('/') + "/v1/chat/completions";

        // メッセージを OpenAI フォーマットに変換する
        var msgArray = BuildMessages(messages);

        // リクエスト JSON を組み立てる
        var requestBody = new Dictionary<string, object>
        {
            ["model"]    = _settings.Model,
            ["messages"] = msgArray,
        };

        if (tools.Count > 0)
        {
            requestBody["tools"]       = BuildTools(tools);
            requestBody["tool_choice"] = "auto";
        }

        var json    = JsonSerializer.Serialize(requestBody);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        // Authorization ヘッダーを付与する
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _settings.ApiKey);

        // リクエストを送信する
        using var response = await _http.SendAsync(request);
        var responseText   = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"OpenAI API エラー {(int)response.StatusCode}: {responseText}");

        return ParseResponse(responseText);
    }

    // ── プライベートヘルパー ─────────────────────────────────

    /// <summary>ChatMessage リストを OpenAI メッセージ配列に変換する。</summary>
    private static List<object> BuildMessages(List<ChatMessage> messages)
    {
        var result = new List<object>();
        foreach (var m in messages)
        {
            if (m.Role == "tool")
            {
                // ツール実行結果メッセージ
                result.Add(new
                {
                    role         = "tool",
                    tool_call_id = m.ToolCallId ?? "",
                    content      = m.Content,
                });
            }
            else if (m.ToolCalls != null && m.ToolCalls.Count > 0)
            {
                // アシスタントのツール呼び出しメッセージ
                result.Add(new
                {
                    role       = m.Role,
                    content    = m.Content.Length > 0 ? (object)m.Content : null!,
                    tool_calls = m.ToolCalls.Select(tc => new
                    {
                        id       = tc.Id,
                        type     = "function",
                        function = new { name = tc.FunctionName, arguments = tc.ArgumentsJson },
                    }).ToList(),
                });
            }
            else
            {
                result.Add(new { role = m.Role, content = m.Content });
            }
        }
        return result;
    }

    /// <summary>ToolDefinition リストを OpenAI ツール配列に変換する。</summary>
    private static List<object> BuildTools(List<ToolDefinition> tools)
    {
        return tools.Select(t => (object)new
        {
            type     = "function",
            function = new
            {
                name        = t.Name,
                description = t.Description,
                parameters  = JsonSerializer.Deserialize<object>(t.ParametersJson),
            },
        }).ToList();
    }

    /// <summary>OpenAI レスポンス JSON を AIResponse に変換する。</summary>
    private static AIResponse ParseResponse(string json)
    {
        using var doc  = JsonDocument.Parse(json);
        var choices    = doc.RootElement.GetProperty("choices");
        var first      = choices[0];
        var message    = first.GetProperty("message");
        var finishReason = first.TryGetProperty("finish_reason", out var fr) ? fr.GetString() ?? "stop" : "stop";

        var response = new AIResponse { StopReason = finishReason };

        // テキスト内容を取得する
        if (message.TryGetProperty("content", out var contentEl) && contentEl.ValueKind == JsonValueKind.String)
            response.TextContent = contentEl.GetString();

        // ツール呼び出しを取得する（API 標準形式）
        if (message.TryGetProperty("tool_calls", out var tcEl) && tcEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var tc in tcEl.EnumerateArray())
            {
                var func = tc.GetProperty("function");
                response.ToolCalls.Add(new ToolCall
                {
                    Id            = tc.TryGetProperty("id",   out var id)   ? id.GetString()   ?? "" : "",
                    FunctionName  = func.TryGetProperty("name", out var nm)  ? nm.GetString()   ?? "" : "",
                    ArgumentsJson = func.TryGetProperty("arguments", out var ag) ? ag.GetString() ?? "{}" : "{}",
                });
            }
        }

        // フォールバック: ローカル LLM がツール呼び出しをテキストとして出力した場合に抽出する。
        // Qwen 等の小規模モデルは tool_calls API に対応していても
        // テキスト内に JSON を埋め込む形で出力することがある。
        if (response.ToolCalls.Count == 0 && !string.IsNullOrEmpty(response.TextContent))
        {
            var extracted = ExtractTextToolCalls(response.TextContent);
            if (extracted.Count > 0)
            {
                response.ToolCalls.AddRange(extracted);
                // ツールログとして表示させないようテキスト部分はクリアする
                response.TextContent = null;
            }
        }

        return response;
    }

    // ── テキスト内ツール呼び出し抽出 ─────────────────────────────

    /// <summary>
    /// ローカル LLM がテキストとして出力したツール呼び出しを抽出する。
    ///
    /// 以下の形式に順番に対応する:
    ///   1. Qwen Jinja テンプレート形式: &lt;tool_call&gt;{...}&lt;/tool_call&gt;
    ///   2. Markdown コードブロック形式: ```json\n{...}\n```
    ///   3. 裸の JSON オブジェクト: {"name":"...","arguments":{...}}
    /// </summary>
    private static List<ToolCall> ExtractTextToolCalls(string text)
    {
        var result = new List<ToolCall>();

        // パターン 1: <tool_call> タグ（Qwen の --jinja 起動時に使われる形式）
        foreach (Match m in Regex.Matches(text, @"<tool_call>\s*([\s\S]*?)\s*</tool_call>"))
        {
            var tc = TryParseToolCallJson(m.Groups[1].Value);
            if (tc != null) result.Add(tc);
        }
        if (result.Count > 0) return result;

        // パターン 2: ```json ... ``` コードブロック
        foreach (Match m in Regex.Matches(text, @"```(?:json)?\s*([\s\S]*?)\s*```"))
        {
            var tc = TryParseToolCallJson(m.Groups[1].Value);
            if (tc != null) result.Add(tc);
        }
        if (result.Count > 0) return result;

        // パターン 3: テキスト内の JSON オブジェクトをすべてスキャンして試す
        foreach (var candidate in ScanJsonObjects(text))
        {
            var tc = TryParseToolCallJson(candidate);
            if (tc != null) result.Add(tc);
        }

        return result;
    }

    /// <summary>
    /// テキスト中の完全な JSON オブジェクト（{ ... }）をすべて列挙する。
    /// ブレースの深さを追って入れ子構造も正しく抽出する。
    /// </summary>
    private static IEnumerable<string> ScanJsonObjects(string text)
    {
        int i = 0;
        while (i < text.Length)
        {
            if (text[i] != '{') { i++; continue; }

            int depth    = 0;
            bool inStr   = false;
            bool escaped = false;
            int  start   = i;

            for (int j = i; j < text.Length; j++)
            {
                char c = text[j];

                if (escaped)          { escaped = false; continue; }
                if (c == '\\' && inStr) { escaped = true;  continue; }
                if (c == '"')         { inStr = !inStr;    continue; }
                if (inStr)            { continue; }

                if (c == '{') depth++;
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0)
                    {
                        yield return text.Substring(start, j - start + 1);
                        i = j + 1;
                        goto nextChar;
                    }
                }
            }
            // 閉じブレースが見つからなかった（不完全な JSON）
            break;

            nextChar:;
        }
    }

    /// <summary>
    /// JSON 文字列を ToolCall にパースする。
    /// {"name":"...","arguments":{...}} 形式（arguments は文字列・オブジェクト両方可）に対応。
    /// パース失敗または必須フィールド不足の場合は null を返す。
    /// </summary>
    private static ToolCall? TryParseToolCallJson(string json)
    {
        try
        {
            using var doc  = JsonDocument.Parse(json.Trim());
            var root = doc.RootElement;

            if (!root.TryGetProperty("name", out var nameProp)) return null;
            var name = nameProp.GetString();
            if (string.IsNullOrWhiteSpace(name))                return null;

            // arguments は文字列（シリアライズ済み）またはオブジェクトの両方を許容する
            var argsJson = "{}";
            if (root.TryGetProperty("arguments", out var argsProp))
            {
                argsJson = argsProp.ValueKind == JsonValueKind.String
                    ? argsProp.GetString() ?? "{}"
                    : argsProp.GetRawText();
            }

            return new ToolCall
            {
                Id            = Guid.NewGuid().ToString("N")[..8],
                FunctionName  = name,
                ArgumentsJson = argsJson,
            };
        }
        catch
        {
            return null;
        }
    }
}
