// ============================================================
//  ChatMessage.cs — AI チャットのメッセージモデル
//
//  役割 (role) と内容 (content) を保持する。
//  ツール呼び出し結果の格納にも使用する。
// ============================================================

namespace SEEDEditor.AI.Models;

/// <summary>
/// AI とのやり取りで使用するメッセージ。
/// role: "system" / "user" / "assistant" / "tool"
/// </summary>
public class ChatMessage
{
    /// <summary>発話者の役割</summary>
    public string Role    { get; set; } = "user";

    /// <summary>メッセージ本文（テキスト）</summary>
    public string Content { get; set; } = string.Empty;

    /// <summary>
    /// ツール呼び出し結果を示す場合のツール呼び出し ID。
    /// Anthropic では tool_use_id、OpenAI では tool_call_id に相当する。
    /// </summary>
    public string? ToolCallId { get; set; }

    /// <summary>
    /// AI がツールを呼び出した際の呼び出し情報。
    /// Role = "assistant" のメッセージに含まれる場合がある。
    /// </summary>
    public List<ToolCall>? ToolCalls { get; set; }

    /// <summary>
    /// Gemini プロバイダー専用: モデル応答の生 parts JSON 配列文字列。
    /// thought_signature や thought パーツ等、Gemini 固有フィールドを
    /// 会話履歴に完全に再現するために保存する。
    /// null の場合は通常のパーツ構築にフォールバックする。
    /// </summary>
    public string? RawGeminiPartsJson { get; set; }
}

/// <summary>
/// AI が要求した個別のツール呼び出し情報。
/// </summary>
public class ToolCall
{
    /// <summary>ツール呼び出しの一意 ID</summary>
    public string Id           { get; set; } = string.Empty;

    /// <summary>呼び出すツール名</summary>
    public string FunctionName { get; set; } = string.Empty;

    /// <summary>引数（JSON 文字列）</summary>
    public string ArgumentsJson { get; set; } = "{}";
}
