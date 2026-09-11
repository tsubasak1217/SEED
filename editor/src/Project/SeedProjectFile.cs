// ============================================================
//  SeedProjectFile.cs — .seedproj（プロジェクトファイル）の JSON モデル
//
//  【役割】
//  Visual Studio の .sln に相当する「ゲーム 1 本ぶんの入口ファイル」を読み書きする。
//  エディタはこのファイルを起点に assets / plugins / cache / save / logs / build の
//  位置を決める（位置の導出そのものは ProjectPaths が担当する）。
//
//  【フォルダ構成】
//    <ProjectRoot>/
//      <Name>.seedproj   ← このファイル
//      assets/           ← ゲームのアセット（assets:// のルート）
//      plugins/          ← ネイティブプラグイン DLL
//      cache/ save/ logs/ build/   ← 実行時・ビルド時に自動生成される
//
//  【ランタイムとの契約】
//  ランタイムは「アセットルートの親フォルダ」から cache / save / plugins を導出する
//  （runtime/src/engine/core/loader/asset_cache.rs, save/path.rs, app/app_init.rs）。
//  したがって assets_dir はプロジェクトルート直下の 1 階層でなければならず、
//  plugins_dir はランタイム側で "plugins" 固定である点に注意（docs/project_system.md）。
//
//  WPF に一切依存しない（単体テストからそのままリンクして使える）。
// ============================================================

using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SEEDEditor.Project;

/// <summary>
/// .seedproj の読み書きに失敗したことを表す例外。
/// 「開けなかった理由」を利用者へそのまま見せられる日本語メッセージを持つ。
/// </summary>
public sealed class SeedProjectFileException : Exception
{
    /// <summary>理由を指定して生成する。</summary>
    /// <param name="message">利用者へ表示する日本語メッセージ。</param>
    /// <param name="inner">元の例外（あれば）。</param>
    public SeedProjectFileException(string message, Exception? inner = null)
        : base(message, inner) { }
}

/// <summary>
/// プロジェクトファイル（<c>&lt;Name&gt;.seedproj</c>）の内容。
///
/// <para>
/// JSON のキー名は <see cref="JsonPropertyNameAttribute"/> で固定する。
/// 将来キーを増やすときは <see cref="FormatVersion"/> を上げず、
/// 既定値を持つ任意キーとして足すこと（古いエディタでも読めるようにするため）。
/// 読めなくなる変更をするときだけ <see cref="CURRENT_FORMAT_VERSION"/> を上げる。
/// </para>
/// </summary>
public sealed class SeedProjectFile
{
    // ── 定数 ────────────────────────────────────────────────

    /// <summary>プロジェクトファイルの拡張子（ドット付き）。</summary>
    public const string EXTENSION = ".seedproj";

    /// <summary>このエディタが書き出す形式バージョン。</summary>
    public const int CURRENT_FORMAT_VERSION = 1;

    /// <summary>assets_dir の既定値（プロジェクトルートからの相対）。</summary>
    public const string DEFAULT_ASSETS_DIR = "assets";

    /// <summary>plugins_dir の既定値（プロジェクトルートからの相対）。</summary>
    public const string DEFAULT_PLUGINS_DIR = "plugins";

    /// <summary>created_at に使う書式（ISO 8601 / ラウンドトリップ可能）。</summary>
    private const string CREATED_AT_FORMAT = "o";

    /// <summary>JSON の書き出し設定（人が読める・差分が取れる整形）。</summary>
    private static readonly JsonSerializerOptions SerializeOptions = new()
    {
        WriteIndented = true,
        // 日本語のプロジェクト名がそのまま読める形で保存されるようにする
        // （既定のエスケープだと "テスト" になり、手で開いたとき読めない）。
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    // ── JSON フィールド ──────────────────────────────────────

    /// <summary>形式バージョン。読み込み時に将来版を弾くために使う。</summary>
    [JsonPropertyName("format_version")]
    public int FormatVersion { get; set; } = CURRENT_FORMAT_VERSION;

    /// <summary>
    /// プロジェクト識別名。既定ではファイル名（拡張子なし）と一致する。
    /// フォルダ名・ファイル名に使える文字だけで構成される。
    /// </summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// 画面に出す表示名。空なら <see cref="Name"/> を使う
    /// （<see cref="EffectiveDisplayName"/> がその判断を 1 か所に閉じている）。
    /// </summary>
    [JsonPropertyName("display_name")]
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>このプロジェクトを作成したエディタのバージョン（記録用）。</summary>
    [JsonPropertyName("engine_version")]
    public string EngineVersion { get; set; } = string.Empty;

    /// <summary>作成日時（ISO 8601）。記録専用で、動作には影響しない。</summary>
    [JsonPropertyName("created_at")]
    public string CreatedAt { get; set; } = string.Empty;

    /// <summary>アセットフォルダ名（プロジェクトルートからの相対）。</summary>
    [JsonPropertyName("assets_dir")]
    public string AssetsDir { get; set; } = DEFAULT_ASSETS_DIR;

    /// <summary>プラグインフォルダ名（プロジェクトルートからの相対）。</summary>
    [JsonPropertyName("plugins_dir")]
    public string PluginsDir { get; set; } = DEFAULT_PLUGINS_DIR;

    /// <summary>
    /// このクラスがまだモデル化していないキーを保持するバケット。
    /// 新しいエディタが足したキーを、古いエディタが保存で消してしまうのを防ぐ。
    /// </summary>
    [JsonExtensionData]
    public System.Collections.Generic.Dictionary<string, JsonElement> ExtraData { get; set; } = new();

    // ── 派生プロパティ ───────────────────────────────────────

    /// <summary>画面に出す名前。display_name が空なら name で代用する。</summary>
    [JsonIgnore]
    public string EffectiveDisplayName
        => string.IsNullOrWhiteSpace(DisplayName) ? Name : DisplayName;

    // ── 生成 ────────────────────────────────────────────────

    /// <summary>
    /// 新規プロジェクト用の内容を組み立てる（ファイルへは書かない）。
    /// </summary>
    /// <param name="name">プロジェクト識別名（= ファイル名の stem）。</param>
    /// <param name="displayName">表示名。null / 空なら <paramref name="name"/> を使う。</param>
    /// <param name="engineVersion">記録するエンジン版。null なら実行中のエディタ版。</param>
    /// <param name="createdAt">作成日時。null なら現在時刻（UTC）。</param>
    public static SeedProjectFile Create(
        string           name,
        string?          displayName   = null,
        string?          engineVersion = null,
        DateTimeOffset?  createdAt     = null)
        => new()
        {
            FormatVersion = CURRENT_FORMAT_VERSION,
            Name          = name,
            DisplayName   = string.IsNullOrWhiteSpace(displayName) ? name : displayName!,
            EngineVersion = engineVersion ?? EditorVersion.Current,
            CreatedAt     = (createdAt ?? DateTimeOffset.UtcNow).ToString(
                                CREATED_AT_FORMAT, System.Globalization.CultureInfo.InvariantCulture),
            AssetsDir     = DEFAULT_ASSETS_DIR,
            PluginsDir    = DEFAULT_PLUGINS_DIR,
        };

    // ── 永続化 ──────────────────────────────────────────────

    /// <summary>
    /// .seedproj を読み込む。
    /// </summary>
    /// <param name="path">.seedproj の絶対パス。</param>
    /// <returns>読み込んだ内容（欠けたフィールドは既定値で補完済み）。</returns>
    /// <exception cref="SeedProjectFileException">
    /// ファイルが無い／JSON として壊れている／このエディタより新しい形式のとき。
    /// </exception>
    public static SeedProjectFile Load(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new SeedProjectFileException("プロジェクトファイルのパスが指定されていません。");

        if (!File.Exists(path))
            throw new SeedProjectFileException($"プロジェクトファイルが見つかりません: {path}");

        SeedProjectFile? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<SeedProjectFile>(File.ReadAllText(path));
        }
        catch (Exception ex)
        {
            throw new SeedProjectFileException(
                $"プロジェクトファイルを読み込めませんでした: {path}\n{ex.Message}", ex);
        }

        if (parsed is null)
            throw new SeedProjectFileException($"プロジェクトファイルが空です: {path}");

        if (parsed.FormatVersion > CURRENT_FORMAT_VERSION)
        {
            throw new SeedProjectFileException(
                $"このプロジェクトは新しい形式（format_version={parsed.FormatVersion}）です。"
              + $"エディタを更新してください（対応版 {CURRENT_FORMAT_VERSION}）。");
        }

        parsed.NormalizeWithFileName(path);
        return parsed;
    }

    /// <summary>
    /// 例外を投げない読み込み。スタート画面のように「開けなかった理由を出して続行」
    /// したい場面で使う。
    /// </summary>
    /// <param name="path">.seedproj の絶対パス。</param>
    /// <param name="file">読み込んだ内容。失敗時は null。</param>
    /// <param name="error">失敗理由（日本語）。成功時は null。</param>
    /// <returns>成功したら true。</returns>
    public static bool TryLoad(string path, out SeedProjectFile? file, out string? error)
    {
        try
        {
            file  = Load(path);
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            file  = null;
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// .seedproj を書き出す（親フォルダが無ければ作る）。
    /// </summary>
    /// <param name="path">.seedproj の絶対パス。</param>
    public void Save(string path)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, JsonSerializer.Serialize(this, SerializeOptions));
    }

    // ── 補完・探索 ──────────────────────────────────────────

    /// <summary>
    /// 欠けたフィールドを既定値で補う。
    ///
    /// 手で書いた .seedproj や、将来キーを減らした版で読んでも
    /// 「assets_dir が空でアセットが見つからない」といった壊れ方をしないようにする。
    /// </summary>
    /// <param name="projectFilePath">読み込んだファイルのパス（name の補完に使う）。</param>
    public void NormalizeWithFileName(string projectFilePath)
    {
        if (string.IsNullOrWhiteSpace(Name))
            Name = Path.GetFileNameWithoutExtension(projectFilePath);

        if (string.IsNullOrWhiteSpace(AssetsDir))  AssetsDir  = DEFAULT_ASSETS_DIR;
        if (string.IsNullOrWhiteSpace(PluginsDir)) PluginsDir = DEFAULT_PLUGINS_DIR;
        if (FormatVersion <= 0)                    FormatVersion = CURRENT_FORMAT_VERSION;
    }

    /// <summary>
    /// フォルダの中から .seedproj を 1 つ探す。
    ///
    /// 複数ある場合は「フォルダ名と同じ stem のもの」を優先し、
    /// それも無ければ名前順の先頭を返す（毎回同じ結果になるようにするため）。
    /// </summary>
    /// <param name="directory">探索するフォルダ。</param>
    /// <returns>見つかった .seedproj の絶対パス。無ければ null。</returns>
    public static string? FindInDirectory(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return null;

        string[] candidates;
        try
        {
            candidates = Directory.GetFiles(directory, "*" + EXTENSION, SearchOption.TopDirectoryOnly);
        }
        catch
        {
            // 権限が無い・列挙中に消えた等。見つからなかった扱いにする。
            return null;
        }
        if (candidates.Length == 0) return null;

        var folderName = new DirectoryInfo(directory).Name;
        var preferred  = candidates.FirstOrDefault(
            p => string.Equals(Path.GetFileNameWithoutExtension(p), folderName,
                               StringComparison.OrdinalIgnoreCase));

        return preferred ?? candidates.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).First();
    }
}
