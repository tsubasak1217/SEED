using System;
using System.Collections.Generic;

namespace SEED.UI;

// ============================================================
//  UiThemePaths.cs — テーマの基（extends）の書き方 → 読むファイル（W2-9。docs/ui_theme.md §3。純粋な計算）
//
//    "default"・"builtin:default"   … 組み込みの既定のテーマ（SEEDScripting に埋め込んだ default_theme.json）
//    "assets://ui/themes/base.json" … そのパス
//    "base.json"・"../common/x.json" … 書いたファイルのフォルダから数えたパス（. と .. を解く）
// ============================================================

/// <summary>テーマの基の書き方 → 読むファイル。</summary>
public static class UiThemePaths
{
    /// <summary>組み込みの既定のテーマの名前（extends に書く）。</summary>
    public const string BuiltInName = "default";
    /// <summary>組み込みの既定のテーマの出どころ（警告・Origin）。</summary>
    public const string BuiltInOrigin = "builtin:default";
    /// <summary>アセットのパスの頭。</summary>
    public const string AssetsScheme = "assets://";
    /// <summary>パスの区切り。</summary>
    private const char PathSeparator = '/';
    /// <summary>今のフォルダ。</summary>
    private const string CurrentDir = ".";
    /// <summary>1 つ上のフォルダ。</summary>
    private const string ParentDir = "..";

    /// <summary>組み込みの既定のテーマを指す書き方か。</summary>
    public static bool IsBuiltIn(string extends) => extends == BuiltInName || extends == BuiltInOrigin;

    /// <summary>
    /// 基の書き方を読むファイルのパスにする（組み込みなら <see cref="BuiltInOrigin"/>）。
    /// </summary>
    /// <param name="extends">JSON の extends の値。</param>
    /// <param name="origin">書いたファイルのパス（assets://…。アセットでなければ相対パスはそのまま返す）。</param>
    public static string Resolve(string extends, string origin)
    {
        if (IsBuiltIn(extends)) return BuiltInOrigin;
        if (extends.StartsWith(AssetsScheme, StringComparison.Ordinal)) return AssetsScheme + Normalize(extends.Substring(AssetsScheme.Length));
        if (!origin.StartsWith(AssetsScheme, StringComparison.Ordinal)) return extends;
        string dir = origin.Substring(AssetsScheme.Length);
        int slash = dir.LastIndexOf(PathSeparator);
        dir = slash < 0 ? string.Empty : dir.Substring(0, slash + 1);
        return AssetsScheme + Normalize(dir + extends);
    }

    /// <summary>. と .. を解き、空の区切りを詰める（根より上へは出ない）。</summary>
    private static string Normalize(string path)
    {
        var parts = new List<string>();
        foreach (var part in path.Split(PathSeparator))
        {
            if (part.Length == 0 || part == CurrentDir) continue;
            if (part == ParentDir)
            {
                if (parts.Count > 0) parts.RemoveAt(parts.Count - 1);
                continue;
            }
            parts.Add(part);
        }
        return string.Join(PathSeparator, parts);
    }
}
