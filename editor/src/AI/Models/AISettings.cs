// ============================================================
//  AISettings.cs — AI プロバイダー設定モデル
//
//  プロバイダーの種別・API キー・エンドポイント・モデル名を保持する。
//  設定はプロジェクトとは切り離してエディタ設定として永続化する。
// ============================================================

namespace SEEDEditor.AI.Models;

/// <summary>
/// 対応 AI プロバイダーの列挙。
/// </summary>
public enum AIProviderKind
{
    /// <summary>OpenAI API 互換（GPT-4o・Ollama 等）</summary>
    OpenAICompatible,
    /// <summary>Anthropic Claude API</summary>
    Anthropic,
}

/// <summary>
/// AI アシスタントの設定を保持する。
/// </summary>
public class AISettings
{
    /// <summary>使用するプロバイダー種別</summary>
    public AIProviderKind Provider { get; set; } = AIProviderKind.OpenAICompatible;

    /// <summary>API キー</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// エンドポイント URL。
    /// OpenAI 互換の場合は空文字列で公式 API（https://api.openai.com）を使用。
    /// Ollama などローカルモデルの場合は http://localhost:11434/v1 等を指定する。
    /// </summary>
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>モデル名（例: "gpt-4o", "claude-opus-4-7", "llama3"）</summary>
    public string Model { get; set; } = "gpt-4o";

    /// <summary>
    /// HTTP リクエストのタイムアウト（秒）。
    /// ローカル LLM はモデルロードと推論に時間がかかるため大きめに設定する。
    /// デフォルト 120 秒（クラウド API 向け）。ローカル AI は 600 秒を推奨。
    /// </summary>
    public int TimeoutSeconds { get; set; } = 120;

    /// <summary>設定が使用可能な状態かどうか</summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(ApiKey) || string.IsNullOrWhiteSpace(Endpoint) == false;
}
