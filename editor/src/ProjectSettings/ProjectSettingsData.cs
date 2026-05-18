using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SEEDEditor.ProjectSettings;

/// <summary>
/// プロジェクト全体の設定データ。
/// {assetsPath}/project_settings.json に JSON 形式で永続化される。
/// 今後カテゴリが増えるたびにプロパティを追加していく。
/// </summary>
public class ProjectSettingsData
{
    // ── 必須設定 ─────────────────────────────────────────────

    /// <summary>ゲームの名前（パッケージフォルダ名・ウィンドウタイトルなどに使用）。</summary>
    [JsonPropertyName("game_name")]
    public string GameName { get; set; } = "MyGame";

    /// <summary>ゲーム起動時に最初にロードするシーンの仮想パス（assets://...）。</summary>
    [JsonPropertyName("start_scene")]
    public string StartScene { get; set; } = string.Empty;

    // ── グラフィックス設定（将来実装） ─────────────────────────
    // ── オーディオ設定（将来実装） ──────────────────────────────
    // ── 物理設定（将来実装） ────────────────────────────────────
    // ── 入力設定（将来実装） ────────────────────────────────────
    // ── ビルド設定（将来実装） ──────────────────────────────────
    // ── タグ＆レイヤー設定（将来実装） ──────────────────────────

    // ── 永続化 ──────────────────────────────────────────────

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>
    /// 指定パスから設定ファイルをロードする。
    /// ファイルが存在しない場合や JSON パースに失敗した場合はデフォルト値を返す。
    /// </summary>
    public static ProjectSettingsData LoadFrom(string path)
    {
        if (!File.Exists(path)) return new ProjectSettingsData();
        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<ProjectSettingsData>(json) ?? new ProjectSettingsData();
        }
        catch
        {
            // ファイルが破損していた場合はデフォルト値で継続する
            return new ProjectSettingsData();
        }
    }

    /// <summary>現在の設定を指定パスに JSON として保存する。</summary>
    public void SaveTo(string path)
    {
        var json = JsonSerializer.Serialize(this, JsonOptions);
        File.WriteAllText(path, json);
    }
}
