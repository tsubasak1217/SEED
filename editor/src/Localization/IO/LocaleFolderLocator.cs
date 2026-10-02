// ============================================================
//  LocaleFolderLocator.cs — 多言語の置き場（フォルダ）の場所（純粋な計算）
//
//  既定の置き場は SEED.Localization の LocalePaths.DefaultRoot（"assets://locale"）。エディタではそれを
//  プロジェクトのアセットのフォルダ（<プロジェクト>/assets）からの相対で解いて <assets>/locale にする。
//  L10n.Configure で別の置き場（"assets://story/locale" など）を使うゲームもあるので、パネルは
//  プロジェクトパネルでダブルクリックした表のフォルダも開ける（そのときは表示に assets:// の形を出す）。
// ============================================================

using System;
using System.IO;
using SEED.Localization;
using SEEDEditor.Assets;

namespace SEEDEditor.Localization.IO;

/// <summary>多言語の置き場（フォルダ）の場所。</summary>
public static class LocaleFolderLocator
{
    /// <summary>
    /// 既定の置き場（assets://locale）の絶対パス。
    /// </summary>
    /// <param name="assetsRoot">プロジェクトのアセットのフォルダ（絶対パス）。</param>
    /// <returns>置き場の絶対パス（アセットのフォルダが空なら null）。</returns>
    public static string? DefaultFolder(string? assetsRoot)
    {
        string root = LocalePaths.NormalizeRoot(null);
        string relative = root.StartsWith(AssetUriPath.Scheme, StringComparison.Ordinal)
            ? root.Substring(AssetUriPath.Scheme.Length)
            : root;
        return AssetUriPath.ToAbsolute(assetsRoot, relative);
    }

    /// <summary>
    /// 置き場の表示名（アセットのフォルダの中なら "assets://locale"、外なら絶対パス）。
    /// </summary>
    /// <param name="assetsRoot">プロジェクトのアセットのフォルダ。</param>
    /// <param name="folder">置き場の絶対パス。</param>
    /// <returns>表示名。</returns>
    public static string DisplayName(string? assetsRoot, string folder)
    {
        string? relative = AssetUriPath.ToRelative(assetsRoot, folder);
        return relative is null ? folder : AssetUriPath.Scheme + relative;
    }

    /// <summary>置き場の言語の一覧（index.json）の絶対パス。</summary>
    /// <param name="folder">置き場の絶対パス。</param>
    /// <returns>index.json の絶対パス。</returns>
    public static string IndexPath(string folder) => Path.Combine(folder, LocalePaths.IndexFileName);

    /// <summary>置き場の言語の表の絶対パス。</summary>
    /// <param name="folder">置き場の絶対パス。</param>
    /// <param name="code">言語のコード。</param>
    /// <returns>&lt;code&gt;.json の絶対パス。</returns>
    public static string TablePath(string folder, string code) => Path.Combine(folder, code + LocalePaths.TableExtension);
}
