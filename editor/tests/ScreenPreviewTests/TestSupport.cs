using System.Text;
using SpriteRigTests;

namespace ScreenPreviewTests;

/// <summary>
/// テスト用の一時フォルダ（Dispose で消す）。
/// </summary>
public sealed class TempDir : IDisposable
{
    /// <summary>一時フォルダの名前の頭。</summary>
    private const string NamePrefix = "seed_screen_preview_tests_";

    /// <summary>一時フォルダの絶対パス。</summary>
    public string Path { get; }

    /// <summary>一時フォルダを作る。</summary>
    public TempDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), NamePrefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    /// <summary>
    /// 中にファイルを作る（途中のフォルダも作る）。
    /// </summary>
    /// <param name="relativePath">一時フォルダからの相対パス（'/' 区切り可）。</param>
    /// <param name="content">中身。</param>
    /// <returns>作ったファイルの絶対パス。</returns>
    public string WriteFile(string relativePath, string content = "{}")
    {
        var full = System.IO.Path.Combine(Path, relativePath.Replace('/', System.IO.Path.DirectorySeparatorChar));
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content, new UTF8Encoding(false));
        return full;
    }

    /// <summary>一時フォルダを消す（消せなくてもテストは止めない）。</summary>
    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); }
        catch (IOException) { /* 他のプロセスが掴んでいても結果には関係ない */ }
        catch (UnauthorizedAccessException) { }
    }
}

/// <summary>
/// リポジトリの中のファイルを探す（テストの実行場所は bin の下なので上へたどる）。
/// </summary>
public static class RepoFiles
{
    /// <summary>上へたどる階層の上限。</summary>
    private const int ProbeDepth = 12;

    /// <summary>
    /// リポジトリの根からの相対パスでファイルを探す。
    /// </summary>
    /// <param name="relativePath">リポジトリの根からの相対パス（'/' 区切り）。</param>
    /// <returns>見つかった絶対パス。</returns>
    /// <exception cref="AssertionException">見つからないとき（ずれ検知を黙って飛ばさない）。</exception>
    public static string Find(string relativePath)
    {
        var relative = relativePath.Replace('/', Path.DirectorySeparatorChar);
        var dir      = AppContext.BaseDirectory;
        for (int i = 0; i < ProbeDepth && dir != null; i++)
        {
            var candidate = Path.Combine(dir, relative);
            if (File.Exists(candidate)) return candidate;
            dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
        }
        throw new AssertionException($"リポジトリのファイルが見つかりません: {relativePath}（起点: {AppContext.BaseDirectory}）");
    }
}
