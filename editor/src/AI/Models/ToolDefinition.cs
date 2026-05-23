// ============================================================
//  ToolDefinition.cs — AI ツール定義モデル
//
//  AI プロバイダーへ渡すツール（Function）の定義を表す。
//  プロバイダーごとの変換は各 Provider クラスが担当する。
// ============================================================

namespace SEEDEditor.AI.Models;

/// <summary>
/// AI に公開するツール（Function）の定義。
/// プロバイダー非依存の中間表現。
/// </summary>
public class ToolDefinition
{
    /// <summary>ツール名（スネークケース推奨）</summary>
    public string Name        { get; set; } = string.Empty;

    /// <summary>AI へ提示するツールの説明文</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// JSON Schema 形式の引数定義（JSON 文字列）。
    /// 例: {"type":"object","properties":{"name":{"type":"string"}},"required":["name"]}
    /// </summary>
    public string ParametersJson { get; set; } = """{"type":"object","properties":{}}""";
}

/// <summary>
/// AI からのレスポンス（テキスト or ツール呼び出し）。
/// </summary>
public class AIResponse
{
    /// <summary>テキスト応答（ツール呼び出しがない場合）</summary>
    public string? TextContent { get; set; }

    /// <summary>AI が要求したツール呼び出しの一覧</summary>
    public List<ToolCall> ToolCalls { get; set; } = new();

    /// <summary>ツール呼び出しが含まれているかどうか</summary>
    public bool HasToolCalls => ToolCalls.Count > 0;

    /// <summary>完了理由（"stop", "tool_use", "end_turn" 等）</summary>
    public string StopReason { get; set; } = "stop";

    /// <summary>
    /// Gemini プロバイダー専用: モデル応答の生 parts JSON 配列文字列。
    /// thought_signature / thought パーツを履歴再構築時に保持するために使用する。
    /// </summary>
    public string? RawGeminiPartsJson { get; set; }
}
