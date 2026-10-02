namespace SEED.Localization;

// ============================================================
//  LocalePaths.cs — 多言語のデータファイルの置き場とファイル名（純粋）
//
//  既定の置き場は assets://locale（templates/locale をプロジェクトへ取り込むと、ここに index.json・ja.json・en.json が入る）。
//  置き場は L10n.Configure(root) で変えられる（例 "assets://mygame/locale"）。表のファイル名は「言語のコード + .json」。
// ============================================================

/// <summary>多言語のデータファイルの置き場とファイル名。</summary>
public static class LocalePaths
{
    /// <summary>既定の置き場。</summary>
    public const string DefaultRoot = "assets://locale";
    /// <summary>言語の一覧のファイル名。</summary>
    public const string IndexFileName = "index.json";
    /// <summary>言語の表のファイル名の後ろ（"ja" → "ja.json"）。</summary>
    public const string TableExtension = ".json";
    /// <summary>パスの区切り。</summary>
    private const char Separator = '/';

    /// <summary>置き場を書きそろえる（前後の空白と末尾の "/" を落とす。空なら既定の置き場）。</summary>
    /// <param name="root">置き場。</param>
    /// <returns>書きそろえた置き場。</returns>
    public static string NormalizeRoot(string? root)
    {
        string trimmed = (root ?? string.Empty).Trim().TrimEnd(Separator);
        return trimmed.Length == 0 ? DefaultRoot : trimmed;
    }

    /// <summary>言語の一覧のパス（"assets://locale/index.json"）。</summary>
    /// <param name="root">書きそろえた置き場。</param>
    /// <returns>パス。</returns>
    public static string IndexPath(string root) => root + Separator + IndexFileName;

    /// <summary>言語の表のパス（"assets://locale/en.json"）。</summary>
    /// <param name="root">書きそろえた置き場。</param>
    /// <param name="code">言語のコード（index.json に書いたまま）。</param>
    /// <returns>パス。</returns>
    public static string TablePath(string root, string code) => root + Separator + code + TableExtension;
}
