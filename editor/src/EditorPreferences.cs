using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SEEDEditor;

/// <summary>
/// エディタ全体の環境設定（特定パネルに属さない操作系の好み）。
/// editor/settings/editor_preferences.json に永続化される。
///
/// 起動時に MainWindow が <see cref="Init"/> を呼んで読み込み、
/// 以降は <see cref="Instance"/> から参照する。設定 UI からの変更は
/// Instance を書き換えて <see cref="Save"/> を呼ぶと即座に反映・保存される。
/// </summary>
public sealed class EditorPreferences
{
    // ── 設定値の範囲（クランプ用）─────────────────────────────
    /// <summary>タッチパッドスクロール係数の最小値。</summary>
    public const double ScrollScaleMin = 0.05;
    /// <summary>タッチパッドスクロール係数の最大値。</summary>
    public const double ScrollScaleMax = 5.0;

    // ── 設定値 ────────────────────────────────────────────────

    /// <summary>
    /// タッチパッド（精密スクロール）の縦・横スクロール係数（1.0 = 標準）。
    /// 小さくするほどスクロールが弱く（ゆっくりに）なる。物理ホイールには影響しない。
    /// </summary>
    [JsonPropertyName("touchpad_scroll_scale")]
    public double TouchpadScrollScale { get; set; } = 1.0;

    /// <summary>
    /// ヒエラルキーの選択アクターの所属（ワールド/ビューポート）に応じて、
    /// シーンタブ（ワールド/ビューポート）を自動で切り替えるかどうか。既定はオン。
    /// </summary>
    [JsonPropertyName("scene_tab_auto_switch")]
    public bool SceneTabAutoSwitch { get; set; } = true;

    /// <summary>
    /// Play 実行時に別プロセスのウィンドウを出してプレイするかどうか。
    /// 既定は false（＝埋め込みインプレース Play。別プロセスを起動せず、シーンパネルの
    /// Edit ランタイムをその場で Play 化するため即座に再生できる）。
    /// true にすると従来の別ウィンドウ Play（別プロセス）になる。
    /// UI 上の「ウィンドウを出してプレイ」チェックボックスと 1 対 1 に対応する。
    /// </summary>
    [JsonPropertyName("window_play")]
    public bool WindowPlay { get; set; } = false;

    // ── シングルトン・永続化 ──────────────────────────────────

    /// <summary>読み込み済みの環境設定（Init 前は既定値）。</summary>
    public static EditorPreferences Instance { get; private set; } = new();

    /// <summary>設定ファイルの絶対パス（Init で決定）。</summary>
    private static string? _filePath;

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    /// <summary>設定ディレクトリを指定して環境設定を読み込む（起動時に一度呼ぶ）。</summary>
    public static void Init(string settingsDir)
    {
        _filePath = Path.Combine(settingsDir, "editor_preferences.json");
        try
        {
            if (File.Exists(_filePath))
            {
                var loaded = JsonSerializer.Deserialize<EditorPreferences>(File.ReadAllText(_filePath), JsonOpts);
                if (loaded is not null) Instance = loaded;
            }
        }
        catch
        {
            // 壊れたファイルは既定値で継続する（次回保存で上書きされる）
            Instance = new EditorPreferences();
        }
        // 範囲外の値（手編集等）をクランプして正規化する
        Instance.TouchpadScrollScale = Math.Clamp(Instance.TouchpadScrollScale, ScrollScaleMin, ScrollScaleMax);
    }

    /// <summary>現在の設定をファイルへ保存する。</summary>
    public static void Save()
    {
        if (_filePath is null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            File.WriteAllText(_filePath, JsonSerializer.Serialize(Instance, JsonOpts));
        }
        catch (Exception ex)
        {
            EditorLog.Write($"環境設定の保存に失敗: {ex.Message}");
        }
    }
}
