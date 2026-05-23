// ============================================================
//  AIPanelSettings.cs — AI アシスタントパネルの永続化設定
//
//  プロバイダー・モデル・API キーなどをエディタ再起動後も
//  保持するための設定クラス。
//  %APPDATA%/SEED/ai_settings.json に JSON 形式で保存される。
// ============================================================

namespace SEEDEditor.AI.Models;

/// <summary>
/// AI アシスタントパネルの永続化設定。
/// エディタ起動時に読み込まれ、設定変更時に自動保存される。
/// </summary>
public class AIPanelSettings
{
    /// <summary>
    /// 選択中のプロバイダーインデックス。
    /// 0: ローカル AI / 1: OpenAI 互換 / 2: Anthropic / 3: Gemini
    /// </summary>
    public int ProviderIndex { get; set; } = 0;

    /// <summary>選択中のモデル名</summary>
    public string Model { get; set; } = "qwen2.5-coder";

    /// <summary>API キー（Anthropic / OpenAI / Gemini 向け）</summary>
    public string ApiKey { get; set; } = "";

    /// <summary>カスタムエンドポイント URL（OpenAI 互換プロバイダー向け）</summary>
    public string Endpoint { get; set; } = "";

    /// <summary>ツール実行ログをチャットに表示するか</summary>
    public bool ShowToolLog { get; set; } = true;
}
