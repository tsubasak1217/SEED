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

    // ── グラフィックス設定 ────────────────────────────────────

    /// <summary>ゲームウィンドウの初期解像度（横・物理ピクセル）。Play・パッケージ版で使用。</summary>
    [JsonPropertyName("window_width")]
    public int WindowWidth { get; set; } = 1920;

    /// <summary>ゲームウィンドウの初期解像度（縦・物理ピクセル）。Play・パッケージ版で使用。</summary>
    [JsonPropertyName("window_height")]
    public int WindowHeight { get; set; } = 1080;

    /// <summary>
    /// ゲーム画面の描画解像度の決め方。
    /// "window"（既定）= ウィンドウの実ピクセルで描画する（従来動作）。
    /// "fixed"        = 常に window_width × window_height の内部解像度で描画し、
    ///                  最終出力でウィンドウへアスペクト比を保ったまま拡大縮小する（余白は黒帯）。
    /// Rust 側 `runtime/src/engine/core/app_base/app/render_resolution.rs` の
    /// RenderResolutionMode と文字列表現を一致させること。
    /// </summary>
    [JsonPropertyName("render_resolution_mode")]
    public string RenderResolutionMode { get; set; } = "window";

    /// <summary>
    /// 目標フレームレート（フレーム／秒）。0 = 無制限。
    ///
    /// <para>
    /// Play・パッケージ版では、フォーカスの有無にかかわらずフレーム間隔を
    /// 1/target_fps 以上に保つ（＝上限を掛けて CPU・GPU の空回りを止める）。
    /// エディタ埋め込みのシーンビューには影響しない。
    /// </para>
    /// <para>
    /// Rust 側 <c>runtime/src/engine/core/app_base/app/frame_pacing.rs</c> の
    /// DEFAULT_TARGET_FPS / TARGET_FPS_MIN / TARGET_FPS_MAX と値域を一致させること。
    /// </para>
    /// </summary>
    [JsonPropertyName("target_fps")]
    public int TargetFps { get; set; } = 60;

    /// <summary>
    /// 垂直同期（VSync）の切り替え。
    /// "auto"（既定）= エディタ埋め込みなら VSync なし（DWM に任せる）、
    ///                 単体ウィンドウ（パッケージ版・別ウィンドウ Play）なら VSync あり。
    /// "on"          = 常に VSync あり（Fifo）。
    /// "off"         = 常に VSync なし（Mailbox / Immediate）。
    /// Rust 側 <c>runtime/src/engine/core/renderer/present_mode.rs</c> の
    /// VsyncMode と文字列表現を一致させること。
    /// </summary>
    [JsonPropertyName("vsync")]
    public string Vsync { get; set; } = "auto";

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

    // ── グラフィックス設定 ────────────────────────────────────

    /// <summary>
    /// インラインレイトレ影の有効フラグ（RT対応GPUのみ効果あり）。
    /// ランタイム起動時に App.rt_shadows へ反映され、エディタからは IPC の
    /// RT_SHADOWS:1 / RT_SHADOWS:0 でライブ切替できる。
    /// </summary>
    [JsonPropertyName("rt_shadows")]
    public bool RtShadows { get; set; } = false;

    /// <summary>
    /// このクラスがモデル化していない未知キーを保持するバケット。
    /// ビューポートツールバーが書き込むグラフィックスキー（features / bloom / gi_intensity など）や
    /// ランタイム専用キー（ambient_* / post_vignette 等）は ProjectSettingsData に無いため、
    /// これが無いと ProjectSettingsWindow の保存（強い型付きシリアライズ）でそれらが消えてしまう。
    /// JsonExtensionData で LoadFrom→SaveTo の往復に未知キーを保全する。
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement> ExtraData { get; set; } = new();

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
