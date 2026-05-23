// ============================================================
//  IAIProvider.cs — AI プロバイダー抽象インターフェース
//
//  OpenAI 互換・Anthropic などの差異をこのインターフェースで吸収する。
//  新しいプロバイダーを追加する場合はこのインターフェースを実装するだけでよい。
// ============================================================

using SEEDEditor.AI.Models;

namespace SEEDEditor.AI.Providers;

/// <summary>
/// AI プロバイダーの共通インターフェース。
/// チャット履歴とツール定義を受け取り、AI の応答を返す。
/// </summary>
public interface IAIProvider
{
    /// <summary>プロバイダーの表示名</summary>
    string Name { get; }

    /// <summary>設定が完了しているかどうか（API キー等）</summary>
    bool IsConfigured { get; }

    /// <summary>
    /// AI にメッセージを送信し、応答を受け取る。
    ///
    /// ツール呼び出しが含まれる場合は AIResponse.ToolCalls に格納される。
    /// 呼び出し側はツールを実行して結果を messages に追加し、再度呼び出す（ループ）。
    /// </summary>
    /// <param name="messages">チャット履歴（システムメッセージを含む）</param>
    /// <param name="tools">AI に公開するツール定義の一覧</param>
    /// <returns>AI の応答</returns>
    Task<AIResponse> ChatAsync(List<ChatMessage> messages, List<ToolDefinition> tools);
}
