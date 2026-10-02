// ============================================================
//  TempFolder.cs — テスト用の一時フォルダ（%TEMP% の下。Dispose で消す）
// ============================================================

using System.Text;

namespace SEEDEditor.Tests.TemplateThumbnails;

/// <summary>テスト用の一時フォルダ（Dispose で中身ごと消す）。</summary>
public sealed class TempFolder : IDisposable
{
    /// <summary>一時フォルダの名前の頭。</summary>
    private const string NamePrefix = "seed_template_thumbnails_tests_";

    /// <summary>一時フォルダの絶対パス。</summary>
    public string Path { get; }

    /// <summary>一時フォルダを作る。</summary>
    public TempFolder()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), NamePrefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    /// <summary>
    /// 中のパスを作る（'/' 区切りの相対パス → 絶対パス。ファイルもフォルダも作らない）。
    /// </summary>
    /// <param name="relativePath">一時フォルダからの相対パス。</param>
    /// <returns>絶対パス。</returns>
    public string Combine(string relativePath) =>
        System.IO.Path.Combine(Path, relativePath.Replace('/', System.IO.Path.DirectorySeparatorChar));

    /// <summary>
    /// 中にファイルを書く（途中のフォルダも作る）。
    /// </summary>
    /// <param name="relativePath">一時フォルダからの相対パス（'/' 区切り）。</param>
    /// <param name="content">中身。</param>
    /// <returns>書いたファイルの絶対パス。</returns>
    public string WriteFile(string relativePath, string content = "{}")
    {
        var full = Combine(relativePath);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content, new UTF8Encoding(false));
        return full;
    }

    /// <summary>一時フォルダを消す（消せなくてもテストは止めない）。</summary>
    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); }
        catch (IOException) { /* ほかのプロセスが掴んでいても結果には関係ない */ }
        catch (UnauthorizedAccessException) { }
    }
}
