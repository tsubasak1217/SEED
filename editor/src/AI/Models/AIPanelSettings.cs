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
    /// AI 動作モード。
    /// 0: API (直接 Web API を呼ぶ) / 1: CLI (ローカルの CLI ツールを介す)
    /// </summary>
    public int Mode { get; set; } = 0;

    /// <summary>
    /// 選択中のプロバイダーインデックス。
    /// Mode=0 (API) 時: 0:ローカルAI / 1:OpenAI / 2:Anthropic / 3:Gemini
    /// Mode=1 (CLI) 時: 0:Gemini CLI / 1:Claude Code
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
