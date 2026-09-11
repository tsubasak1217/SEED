// ============================================================
//  RecentProjectsStore.cs — 最近開いたプロジェクトの一覧
//
//  【役割】
//  スタート画面の左側に並ぶ「最近のプロジェクト」を永続化する。
//  保存先は editor/settings/recent_projects.json（エディタ側の設定なので
//  プロジェクトの外＝エディタ本体の settings フォルダに置く）。
//
//  【旧形式からの移行】
//  同名の recent_projects.json は、以前は「最近開いたシーン（.scene の絶対パスの
//  文字列配列）」だった。中身の形が違うだけで名前が同じなので、初回に一度だけ
//  内容を見て判定し、文字列配列なら recent_scenes.json へ移してから
//  新形式で書き直す（RecentScenesManager 側もこの移行を先に走らせる）。
//
//  WPF に一切依存しない（単体テストからそのままリンクして使える）。
// ============================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SEEDEditor.Project;

/// <summary>最近開いたプロジェクト 1 件ぶんの記録。</summary>
public sealed class RecentProjectEntry
{
    /// <summary>.seedproj の絶対パス。</summary>
    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;

    /// <summary>一覧に出す表示名（記録した時点の display_name）。</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>最後に開いた日時（ISO 8601）。</summary>
    [JsonPropertyName("last_opened")]
    public string LastOpened { get; set; } = string.Empty;

    /// <summary>
    /// <see cref="LastOpened"/> を日時として解釈する。壊れていれば null。
    /// 並べ替えと画面表示の両方で使うため、解釈を 1 か所に閉じている。
    /// </summary>
    [JsonIgnore]
    public DateTimeOffset? LastOpenedAt
        => DateTimeOffset.TryParse(
               LastOpened, System.Globalization.CultureInfo.InvariantCulture,
               System.Globalization.DateTimeStyles.RoundtripKind, out var v)
           ? v : null;
}

/// <summary>recent_projects.json のルート構造。</summary>
public sealed class RecentProjectsDocument
{
    /// <summary>形式バージョン。旧形式（文字列配列）との判別にも使う。</summary>
    [JsonPropertyName("format_version")]
    public int FormatVersion { get; set; } = RecentProjectsStore.CURRENT_FORMAT_VERSION;

    /// <summary>新しい順に並んだ記録。</summary>
    [JsonPropertyName("entries")]
    public List<RecentProjectEntry> Entries { get; set; } = new();
}

/// <summary>
/// 最近開いたプロジェクト一覧の読み書き。
///
/// <para>
/// 設定フォルダをコンストラクタで受け取るのは、単体テストが一時フォルダを
/// 指して回せるようにするため（実行環境の設定を壊さない）。
/// </para>
/// </summary>
public sealed class RecentProjectsStore
{
    // ── 定数 ────────────────────────────────────────────────

    /// <summary>保存ファイル名。</summary>
    public const string FILE_NAME = "recent_projects.json";

    /// <summary>旧「最近開いたシーン」一覧の移行先ファイル名。</summary>
    public const string LEGACY_SCENES_FILE_NAME = "recent_scenes.json";

    /// <summary>このエディタが書き出す形式バージョン。</summary>
    public const int CURRENT_FORMAT_VERSION = 1;

    /// <summary>保持する最大件数（古いものから捨てる）。</summary>
    public const int MAX_ENTRIES = 20;

    /// <summary>last_opened に使う書式（ISO 8601 / ラウンドトリップ可能）。</summary>
    private const string LAST_OPENED_FORMAT = "o";

    /// <summary>JSON の書き出し設定。</summary>
    private static readonly JsonSerializerOptions SerializeOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    // ── 状態 ────────────────────────────────────────────────

    /// <summary>設定フォルダ（editor/settings）。</summary>
    private readonly string _settingsDir;

    /// <summary>recent_projects.json の絶対パス。</summary>
    public string FilePath => System.IO.Path.Combine(_settingsDir, FILE_NAME);

    /// <summary>
    /// 設定フォルダを指定して生成する。
    /// </summary>
    /// <param name="settingsDir">editor/settings 相当のフォルダ。</param>
    public RecentProjectsStore(string settingsDir)
    {
        _settingsDir = settingsDir ?? throw new ArgumentNullException(nameof(settingsDir));
    }

    // ── 読み書き ────────────────────────────────────────────

    /// <summary>
    /// 一覧を読み込む（新しい順）。ファイルが無い・壊れている場合は空を返す。
    /// </summary>
    public List<RecentProjectEntry> Load()
    {
        MigrateLegacySceneList(_settingsDir);

        var path = FilePath;
        if (!File.Exists(path)) return new List<RecentProjectEntry>();

        try
        {
            var doc = JsonSerializer.Deserialize<RecentProjectsDocument>(File.ReadAllText(path));
            if (doc?.Entries is null) return new List<RecentProjectEntry>();

            // path が空の壊れた行は落とす（画面に空行が出るのを防ぐ）。
            return doc.Entries.Where(e => !string.IsNullOrWhiteSpace(e.Path)).ToList();
        }
        catch
        {
            // 壊れていたら空扱い。次の Add で正しい形に書き直される。
            return new List<RecentProjectEntry>();
        }
    }

    /// <summary>
    /// プロジェクトを先頭へ追加する（既にあれば先頭へ引き上げて日時を更新）。
    /// </summary>
    /// <param name="projectFilePath">.seedproj の絶対パス。</param>
    /// <param name="displayName">一覧に出す表示名。</param>
    /// <param name="now">記録する日時。null なら現在時刻（UTC）。</param>
    public void Add(string projectFilePath, string displayName, DateTimeOffset? now = null)
    {
        if (string.IsNullOrWhiteSpace(projectFilePath)) return;

        var full    = SafeFullPath(projectFilePath);
        var entries = Load();

        entries.RemoveAll(e => PathEquals(e.Path, full));
        entries.Insert(0, new RecentProjectEntry
        {
            Path       = full,
            Name       = displayName,
            LastOpened = (now ?? DateTimeOffset.UtcNow).ToString(
                             LAST_OPENED_FORMAT, System.Globalization.CultureInfo.InvariantCulture),
        });

        if (entries.Count > MAX_ENTRIES) entries = entries.Take(MAX_ENTRIES).ToList();
        Save(entries);
    }

    /// <summary>一覧から 1 件外す（ファイル自体は消さない）。</summary>
    /// <param name="projectFilePath">.seedproj の絶対パス。</param>
    public void Remove(string projectFilePath)
    {
        if (string.IsNullOrWhiteSpace(projectFilePath)) return;

        var full    = SafeFullPath(projectFilePath);
        var entries = Load();
        if (entries.RemoveAll(e => PathEquals(e.Path, full)) == 0) return;
        Save(entries);
    }

    /// <summary>一覧をファイルへ書き出す。</summary>
    /// <param name="entries">新しい順に並んだ記録。</param>
    public void Save(List<RecentProjectEntry> entries)
    {
        Directory.CreateDirectory(_settingsDir);
        var doc = new RecentProjectsDocument
        {
            FormatVersion = CURRENT_FORMAT_VERSION,
            Entries       = entries,
        };
        File.WriteAllText(FilePath, JsonSerializer.Serialize(doc, SerializeOptions));
    }

    // ── 旧形式の移行 ────────────────────────────────────────

    /// <summary>移行を試みた設定フォルダ（プロセス内で 1 回だけ走らせるためのガード）。</summary>
    private static readonly HashSet<string> _migratedDirs = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 旧 recent_projects.json（= .scene パスの文字列配列）を
    /// recent_scenes.json へ移す。
    ///
    /// <para>
    /// 判定は「JSON のルートが配列かどうか」で行う。新形式はオブジェクトなので
    /// 誤判定しない。移行先が既にある場合は上書きしない（新しい方が正）。
    /// 移行後、元ファイルは削除する（次の <see cref="Save"/> が新形式で作り直す）。
    /// </para>
    /// </summary>
    /// <param name="settingsDir">editor/settings 相当のフォルダ。</param>
    /// <returns>実際に移行を行ったら true。</returns>
    public static bool MigrateLegacySceneList(string settingsDir)
    {
        if (string.IsNullOrWhiteSpace(settingsDir)) return false;

        lock (_migratedDirs)
        {
            if (!_migratedDirs.Add(System.IO.Path.GetFullPath(settingsDir))) return false;
        }

        var source = System.IO.Path.Combine(settingsDir, FILE_NAME);
        if (!File.Exists(source)) return false;

        try
        {
            var text = File.ReadAllText(source);
            using var doc = JsonDocument.Parse(text);

            // 新形式（オブジェクト）ならそのまま使う。
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return false;

            var destination = System.IO.Path.Combine(settingsDir, LEGACY_SCENES_FILE_NAME);
            if (!File.Exists(destination)) File.WriteAllText(destination, text);

            // 旧ファイルは消す。以後は新形式だけが recent_projects.json を名乗る。
            File.Delete(source);
            return true;
        }
        catch
        {
            // 壊れた JSON・権限エラー。移行できないだけで起動は続行する。
            return false;
        }
    }

    // ── 補助 ────────────────────────────────────────────────

    /// <summary>例外を投げずに絶対パスへ正規化する（不正なら入力をそのまま返す）。</summary>
    private static string SafeFullPath(string path)
    {
        try { return System.IO.Path.GetFullPath(path); }
        catch { return path; }
    }

    /// <summary>パスを大文字小文字無視で比較する（Windows のファイルシステム規約）。</summary>
    private static bool PathEquals(string a, string b)
        => string.Equals(SafeFullPath(a), b, StringComparison.OrdinalIgnoreCase);
}
