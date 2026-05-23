// ============================================================
//  GeminiProvider.cs — Google Gemini API プロバイダー
//
//  Gemini API（/v1beta/models/{model}:generateContent）に対応する実装。
//  OpenAI / Anthropic と異なる点:
//    - 認証: URL クエリパラメータ ?key={apiKey}
//    - メッセージ形式: contents[].parts[] 配列
//    - ロール名: "user" / "model"（"assistant" ではない）
//    - システムプロンプト: system_instruction トップレベルフィールド
//    - ツール定義: tools[].function_declarations[]
//    - ツール呼び出し: parts[].functionCall
//    - ツール結果: role="user" + parts[].functionResponse
// ============================================================

using System.Net.Http;
using System.Text;
using System.Text.Json;
using SEEDEditor.AI.Models;

namespace SEEDEditor.AI.Providers;

/// <summary>
/// Google Gemini API へリクエストを送信するプロバイダー。
/// gemini-2.0-flash / gemini-1.5-pro / gemini-1.5-flash 等に対応する。
/// </summary>
public class GeminiProvider : IAIProvider
{
    // ── 定数 ────────────────────────────────────────────────
    private const string BASE_URL    = "https://generativelanguage.googleapis.com/v1beta/models";
    private const int    MAX_TOKENS  = 8192;

    // ── フィールド ───────────────────────────────────────────
    private readonly HttpClient _http;
    private readonly AISettings _settings;

    // ── コンストラクタ ──────────────────────────────────────

    public GeminiProvider(AISettings settings)
    {
        _settings = settings;
        _http     = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
    }

    // ── IAIProvider ─────────────────────────────────────────

    public string Name         => "Google Gemini";
    public bool   IsConfigured => !string.IsNullOrWhiteSpace(_settings.ApiKey);

    /// <summary>
    /// Gemini の generateContent エンドポイントへリクエストを送信し応答を返す。
    /// ツール呼び出しが含まれる場合は AIResponse.ToolCalls に格納する。
    /// </summary>
    public async Task<AIResponse> ChatAsync(List<ChatMessage> messages, List<ToolDefinition> tools)
    {
        // エンドポイント URL を組み立てる
        // POST https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent?key={key}
        var model    = _settings.Model;
        var endpoint = $"{BASE_URL}/{model}:generateContent?key={_settings.ApiKey}";

        // システムメッセージを抽出する（Gemini は system_instruction に分離）
        var systemText = messages.FirstOrDefault(m => m.Role == "system")?.Content ?? "";
        var nonSystem  = messages.Where(m => m.Role != "system").ToList();

        // リクエスト本体を組み立てる
        var requestBody = new Dictionary<string, object>
        {
            ["contents"]          = BuildContents(nonSystem),
            ["generationConfig"]  = new { maxOutputTokens = MAX_TOKENS },
        };

        if (!string.IsNullOrEmpty(systemText))
            requestBody["system_instruction"] = new
            {
                parts = new[] { new { text = systemText } }
            };

        if (tools.Count > 0)
            requestBody["tools"] = new[] { new { function_declarations = BuildFunctionDeclarations(tools) } };

        var json    = JsonSerializer.Serialize(requestBody);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        using var response = await _http.PostAsync(endpoint, content);
        var responseText   = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(BuildErrorMessage((int)response.StatusCode, responseText));

        return ParseResponse(responseText);
    }

    // ── プライベートヘルパー ─────────────────────────────────

    /// <summary>
    /// ChatMessage リストを Gemini の contents 配列に変換する。
    ///
    /// - role "assistant" → "model"
    /// - tool 結果メッセージ → role "user" + functionResponse
    /// - アシスタントのツール呼び出し → role "model" + functionCall
    ///
    /// 思考モデル（gemini-2.5 系）が返す functionCall には thoughtSignature が含まれる。
    /// RawGeminiPartsJson が保存されている場合はそれをそのまま使用して
    /// thought_signature / thought パーツを完全に保持する。
    /// </summary>
    private static List<object> BuildContents(List<ChatMessage> messages)
    {
        var result = new List<object>();
        foreach (var m in messages)
        {
            if (m.Role == "tool")
            {
                // ツール実行結果: role="user" + functionResponse
                result.Add(new
                {
                    role  = "user",
                    parts = new[]
                    {
                        new
                        {
                            functionResponse = new
                            {
                                // ToolCallId に関数名を格納しているため name として使用する
                                name     = m.ToolCallId ?? "unknown",
                                response = new { result = m.Content },
                            }
                        }
                    },
                });
            }
            else if (m.ToolCalls != null && m.ToolCalls.Count > 0)
            {
                // アシスタントのツール呼び出しメッセージ
                if (!string.IsNullOrEmpty(m.RawGeminiPartsJson))
                {
                    // 保存済みの生 parts JSON を使用して thought_signature 等を完全に保持する
                    var rawParts = JsonSerializer.Deserialize<JsonElement>(m.RawGeminiPartsJson);
                    result.Add(new { role = "model", parts = rawParts });
                }
                else
                {
                    // フォールバック: 生 JSON がない場合は通常構築（thoughtSignature なし）
                    var parts = new List<object>();
                    if (!string.IsNullOrEmpty(m.Content))
                        parts.Add(new { text = m.Content });

                    foreach (var tc in m.ToolCalls)
                    {
                        var args = JsonSerializer.Deserialize<object>(tc.ArgumentsJson) ?? new { };
                        parts.Add(new { functionCall = new { name = tc.FunctionName, args } });
                    }

                    result.Add(new { role = "model", parts });
                }
            }
            else
            {
                // 通常のテキストメッセージ（assistant → model に変換）
                var role = m.Role == "assistant" ? "model" : m.Role;
                result.Add(new
                {
                    role  = role,
                    parts = new[] { new { text = m.Content } },
                });
            }
        }
        return result;
    }

    /// <summary>ToolDefinition リストを Gemini の function_declarations 配列に変換する。</summary>
    private static List<object> BuildFunctionDeclarations(List<ToolDefinition> tools)
    {
        return tools.Select(t => (object)new
        {
            name        = t.Name,
            description = t.Description,
            parameters  = JsonSerializer.Deserialize<object>(t.ParametersJson),
        }).ToList();
    }

    /// <summary>
    /// HTTP エラーレスポンスを人間が読みやすいメッセージに変換する。
    /// 429 の場合は retryDelay を抽出してリトライ秒数を案内する。
    /// </summary>
    private static string BuildErrorMessage(int statusCode, string responseText)
    {
        try
        {
            using var doc  = JsonDocument.Parse(responseText);
            var errorObj   = doc.RootElement.GetProperty("error");
            var status     = errorObj.TryGetProperty("status",  out var s) ? s.GetString() : null;
            var message    = errorObj.TryGetProperty("message", out var m) ? m.GetString() : responseText;

            if (statusCode == 429)
            {
                // RetryInfo から再試行までの秒数を取得する
                var retryDelay = ExtractRetryDelay(errorObj);
                var retryMsg   = retryDelay > 0
                    ? $"{retryDelay} 秒後に再試行してください。"
                    : "しばらく待ってから再試行してください。";

                return $"クォータ制限に達しました（{status ?? "RESOURCE_EXHAUSTED"}）。\n{retryMsg}\n\n" +
                       "無料枠の上限に達している場合は、別のモデル（gemini-1.5-flash-latest 等）を試すか、" +
                       "Google AI Studio でプランを確認してください。";
            }

            if (statusCode == 404)
            {
                return $"モデルが見つかりません。\n" +
                       $"モデル名が正しいか確認してください。\n" +
                       $"利用可能なモデルの例: gemini-2.0-flash, gemini-1.5-flash-latest, gemini-1.5-pro-latest";
            }

            return $"Gemini API エラー {statusCode} ({status}): {message}";
        }
        catch
        {
            return $"Gemini API エラー {statusCode}: {responseText}";
        }
    }

    /// <summary>
    /// error.details[] から RetryInfo の retryDelay（秒数）を取り出す。
    /// 見つからない場合は 0 を返す。
    /// </summary>
    private static int ExtractRetryDelay(JsonElement errorObj)
    {
        if (!errorObj.TryGetProperty("details", out var details)) return 0;

        foreach (var detail in details.EnumerateArray())
        {
            if (!detail.TryGetProperty("@type", out var typeEl)) continue;
            if (typeEl.GetString() != "type.googleapis.com/google.rpc.RetryInfo") continue;

            if (detail.TryGetProperty("retryDelay", out var delayEl))
            {
                // "30s" 形式から数値を取り出す
                var raw = delayEl.GetString() ?? "";
                var num = new string(raw.TakeWhile(c => char.IsDigit(c) || c == '.').ToArray());
                if (double.TryParse(num, System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture, out var sec))
                    return (int)Math.Ceiling(sec);
            }
        }
        return 0;
    }

    /// <summary>
    /// Gemini レスポンス JSON を AIResponse に変換する。
    ///
    /// 思考モデル（gemini-2.5 系）の場合、parts に thought パーツや
    /// functionCall の thoughtSignature が含まれる。
    /// これらを履歴再構築時に保持するため RawGeminiPartsJson に
    /// parts の生 JSON 文字列を保存する。
    /// </summary>
    private static AIResponse ParseResponse(string json)
    {
        using var doc  = JsonDocument.Parse(json);
        var root       = doc.RootElement;
        var response   = new AIResponse();

        // candidates[0].content.parts を走査する
        if (!root.TryGetProperty("candidates", out var candidates)) return response;
        if (candidates.GetArrayLength() == 0) return response;

        var first       = candidates[0];
        var finishReason = first.TryGetProperty("finishReason", out var fr) ? fr.GetString() ?? "STOP" : "STOP";
        response.StopReason = finishReason;

        if (!first.TryGetProperty("content", out var contentObj)) return response;
        if (!contentObj.TryGetProperty("parts", out var parts))   return response;

        // thought_signature 等を保持するため parts の生 JSON を保存する
        // JsonElement は doc の生存期間に依存するため GetRawText() で文字列化する
        response.RawGeminiPartsJson = parts.GetRawText();

        foreach (var part in parts.EnumerateArray())
        {
            if (part.TryGetProperty("text", out var textEl))
            {
                // thought パーツ（thought=true）はテキストに含めない
                // テキストバブルに思考内容が混入するのを防ぐ
                var isThought = part.TryGetProperty("thought", out var thoughtEl) && thoughtEl.GetBoolean();
                if (!isThought)
                    response.TextContent = (response.TextContent ?? "") + textEl.GetString();
            }
            else if (part.TryGetProperty("functionCall", out var fc))
            {
                // ツール呼び出しブロック
                var funcName = fc.TryGetProperty("name", out var nm) ? nm.GetString() ?? "" : "";
                var argsJson = fc.TryGetProperty("args", out var ag) ? ag.GetRawText() : "{}";

                // Gemini はツール呼び出し ID を持たないため関数名を ID として使用する
                response.ToolCalls.Add(new ToolCall
                {
                    Id            = funcName,
                    FunctionName  = funcName,
                    ArgumentsJson = argsJson,
                });
            }
        }

        return response;
    }
}
