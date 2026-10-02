using SEED.Localization;

namespace LocalizationTests;

// ============================================================
//  Fakes.cs — テスト用の読み込み元・保存先・警告の受け手（エンジンの Assets・SaveData・Debug の代わり）
// ============================================================

/// <summary>辞書から読む読み込み元（パス → 本文と更新の印。書き換えで印を進める）。</summary>
internal sealed class MemoryLocaleSource : ILocaleSource
{
    /// <summary>パス → 本文。</summary>
    private readonly Dictionary<string, string> _files = new(StringComparer.Ordinal);
    /// <summary>パス → 更新の印。</summary>
    private readonly Dictionary<string, long> _stamps = new(StringComparer.Ordinal);
    /// <summary>次に付ける更新の印。</summary>
    private long _nextStamp = 1;

    /// <summary>読まれた回数（パス → 回数）。</summary>
    public Dictionary<string, int> ReadCounts { get; } = new(StringComparer.Ordinal);

    /// <summary>更新の印を取れないファイルにするか（PAK 同梱の代わり。true なら 0 を返す）。</summary>
    public bool StampsUnknown { get; set; }

    /// <summary>ファイルを置く（置き直すと更新の印が進む）。</summary>
    public MemoryLocaleSource Put(string path, string json)
    {
        _files[path] = json;
        _stamps[path] = _nextStamp++;
        return this;
    }

    /// <summary>ファイルを消す（印も消える＝0）。</summary>
    public void Remove(string path)
    {
        _files.Remove(path);
        _stamps.Remove(path);
    }

    /// <inheritdoc />
    public bool TryReadText(string path, out string text)
    {
        ReadCounts[path] = ReadCounts.TryGetValue(path, out int n) ? n + 1 : 1;
        if (_files.TryGetValue(path, out var found))
        {
            text = found;
            return true;
        }
        text = string.Empty;
        return false;
    }

    /// <inheritdoc />
    public long GetModifiedTime(string path)
    {
        if (StampsUnknown) return 0;
        return _stamps.TryGetValue(path, out long stamp) ? stamp : 0;
    }
}

/// <summary>辞書へ保存する保存先（SaveData の代わり。書いた回数も数える）。</summary>
internal sealed class MemoryLocaleStore : ILocaleStore
{
    /// <summary>キー → 値。</summary>
    public Dictionary<string, string> Values { get; } = new(StringComparer.Ordinal);

    /// <summary>SetString の回数。</summary>
    public int WriteCount { get; private set; }

    /// <summary>Delete の回数。</summary>
    public int DeleteCount { get; private set; }

    /// <inheritdoc />
    public string? GetString(string key) => Values.TryGetValue(key, out var value) ? value : null;

    /// <inheritdoc />
    public void SetString(string key, string value)
    {
        Values[key] = value;
        WriteCount++;
    }

    /// <inheritdoc />
    public void Delete(string key)
    {
        Values.Remove(key);
        DeleteCount++;
    }
}

/// <summary>
/// フォルダから読む読み込み元（"assets://" を 1 つのフォルダへ当てる。テンプレートを取り込んだ先を読む）。
/// 更新の印はファイルの最終更新（UNIX 秒）。
/// </summary>
internal sealed class FolderLocaleSource : ILocaleSource
{
    /// <summary>assets:// の頭。</summary>
    private const string Scheme = "assets://";

    /// <summary>assets:// を当てるフォルダ。</summary>
    private readonly string _assetsRoot;

    /// <summary>読み込み元を作る。</summary>
    /// <param name="assetsRoot">assets:// を当てるフォルダ。</param>
    public FolderLocaleSource(string assetsRoot)
    {
        _assetsRoot = assetsRoot;
    }

    /// <summary>assets:// のパスをフォルダのパスへ。</summary>
    private string ToFile(string path) =>
        Path.Combine(_assetsRoot, path.StartsWith(Scheme, StringComparison.Ordinal) ? path.Substring(Scheme.Length) : path);

    /// <inheritdoc />
    public bool TryReadText(string path, out string text)
    {
        string file = ToFile(path);
        if (!File.Exists(file))
        {
            text = string.Empty;
            return false;
        }
        text = File.ReadAllText(file);
        return true;
    }

    /// <inheritdoc />
    public long GetModifiedTime(string path)
    {
        string file = ToFile(path);
        return File.Exists(file) ? new DateTimeOffset(File.GetLastWriteTimeUtc(file)).ToUnixTimeSeconds() : 0;
    }
}

/// <summary>警告を集める受け手。</summary>
internal sealed class WarningLog
{
    /// <summary>集めた警告。</summary>
    public List<string> Messages { get; } = new();

    /// <summary>LocaleCatalog へ渡す口。</summary>
    public void Add(string message) => Messages.Add(message);

    /// <summary>文を含む警告の数。</summary>
    public int CountContaining(string fragment) => Messages.Count(m => m.Contains(fragment, StringComparison.Ordinal));
}

/// <summary>リポジトリの場所（テンプレート・docs を読むため）。</summary>
internal static class RepoPaths
{
    /// <summary>リポジトリの根の目印（この 2 つのフォルダがある所）。</summary>
    private static readonly string[] Markers = { "templates", "scripting" };

    /// <summary>リポジトリの根（実行ファイルの場所から上へ探す）。</summary>
    public static string Root
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null)
            {
                if (Markers.All(m => Directory.Exists(Path.Combine(dir.FullName, m)))) return dir.FullName;
                dir = dir.Parent;
            }
            throw new InvalidOperationException("リポジトリの根（templates と scripting のあるフォルダ）が見つかりません");
        }
    }

    /// <summary>テンプレートのライブラリ（templates/）。</summary>
    public static string Templates => Path.Combine(Root, "templates");
}
