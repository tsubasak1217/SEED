using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SEEDEditor.ProjectSettings;

/// <summary>
/// プロジェクト設定のプラグインエントリ。
/// project_settings.json の "plugins" 配列の各要素に対応する。
/// Rust 側の manifest::PluginEntry と同一構造を維持すること。
/// </summary>
public class PluginEntry
{
    /// <summary>プラグイン識別名（plugin.json の name フィールドと一致させること）。</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>プラグインが有効化されているかどうか。</summary>
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;
}

/// <summary>
/// シーンマネージャに登録されたシーンエントリ。
/// project_settings.json の "scenes" 配列の各要素に対応する。
/// スクリプトからは Name を引数に SEED.Scene.Transition("name") で遷移できる。
/// Rust 側（app_init.rs のシーンレジストリ読み込み）と同一構造を維持すること。
/// </summary>
public class SceneEntry
{
    /// <summary>シーン識別名（既定はファイル名の拡張子なし。レジストリ内で一意）。</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>シーンファイルの仮想パス（assets://...）。</summary>
    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;
}

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

    // ── シーンマネージャ ─────────────────────────────────────

    /// <summary>
    /// シーンマネージャに登録されたシーン一覧。
    /// スクリプトの SEED.Scene.Load / Transition が名前で参照する。
    /// </summary>
    [JsonPropertyName("scenes")]
    public List<SceneEntry> Scenes { get; set; } = new();

    // ── プラグイン設定 ────────────────────────────────────────

    /// <summary>
    /// インポート済みプラグインの有効/無効リスト。
    /// リストに存在しないプラグインはデフォルトで有効（Rust 側と同一ルール）。
    /// </summary>
    [JsonPropertyName("plugins")]
    public List<PluginEntry> Plugins { get; set; } = new();

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
