using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SEEDEditor.Assets;

/// <summary>
/// 絶対パスと <c>assets://</c> 仮想パスを相互変換する唯一の変換器。
///
/// SEED ではランタイム／スクリプト／シーンファイルがアセットを
/// <c>assets://mainGame/textures/ui/white.png</c> という仮想パスで参照する。
/// 実体はプロジェクトのアセットルート（<c>&lt;project&gt;/assets</c>）配下のファイルで、
/// 「アセットルートからの相対パスを <c>/</c> 区切りにしたもの」がそのまま仮想パスになる。
///
/// この変換は「プロジェクトパネルのパスコピー」「参照フィールドへの貼り付け」など
/// 複数箇所で必要になるため、規則をここ 1 箇所へ集約する。
/// WPF に依存しない純粋なパス計算だけを置くこと
/// （editor/tests/ProjectPanelLogicTests がこのファイルを直接リンクして検証している。
///   WPF 型を使い始めるとテストのビルドが壊れ、それが設計崩れの検知器になる）。
/// </summary>
public static class AssetUriPath
{
    /// <summary>仮想パスのスキーム接頭辞。</summary>
    public const string Scheme = "assets://";

    /// <summary>仮想パスの区切り文字（OS に依らず常にスラッシュ）。</summary>
    public const char Separator = '/';

    /// <summary>アセットルート自身を指すときの相対パス（＝空文字）。</summary>
    private const string RootRelative = "";

    /// <summary><see cref="Path.GetRelativePath"/> が「同じ場所」を表すときに返す文字列。</summary>
    private const string SamePathMarker = ".";

    /// <summary>親をたどる相対パスの先頭（ルート外を検出するために使う）。</summary>
    private const string ParentPrefix = "../";

    /// <summary>親そのものを指す相対パス。</summary>
    private const string ParentSelf = "..";

    /// <summary>
    /// 絶対パスをアセットルートからの相対パス（<c>/</c> 区切り、スキーム無し）へ変換する。
    /// </summary>
    /// <param name="assetsRoot">アセットルートの絶対パス。末尾の区切り文字は有っても無くてもよい。</param>
    /// <param name="absolutePath">変換したいファイル／フォルダの絶対パス。</param>
    /// <returns>
    /// 相対パス。アセットルート自身なら空文字。
    /// ルート外・空入力・パスとして解釈できない入力なら <c>null</c>
    /// （呼び出し側は「仮想パスを持てないファイル」として扱う）。
    /// </returns>
    public static string? ToRelative(string? assetsRoot, string? absolutePath)
    {
        if (string.IsNullOrWhiteSpace(assetsRoot) || string.IsNullOrWhiteSpace(absolutePath)) return null;

        string root;
        string full;
        try
        {
            // 末尾区切りや "." "..'" を畳んでから比較する（"C:\a\assets\" と "C:\a\assets" を同一視）。
            root = Path.GetFullPath(assetsRoot);
            full = Path.GetFullPath(absolutePath);
        }
        catch
        {
            // 不正文字などでパスとして解釈できない場合は仮想パスを作らない。
            return null;
        }

        string relative;
        try
        {
            relative = Path.GetRelativePath(root, full);
        }
        catch
        {
            return null;
        }

        // 同一パス（アセットルート自身）。
        if (relative == SamePathMarker) return RootRelative;

        // 区切り文字をスラッシュへ揃えてから「ルート外」を判定する。
        // 文字列前方一致で判定すると "assets" と "assetsBackup" を取り違えるため、
        // GetRelativePath の結果（".." で始まる／絶対パスのまま）で判断する。
        var unified = relative.Replace(Path.DirectorySeparatorChar, Separator)
                              .Replace(Path.AltDirectorySeparatorChar, Separator);
        if (unified == ParentSelf || unified.StartsWith(ParentPrefix, StringComparison.Ordinal)) return null;
        if (Path.IsPathRooted(relative)) return null;

        // 末尾のスラッシュ（フォルダを "dir/" 形式で渡された場合）は落とす。
        return unified.TrimEnd(Separator);
    }

    /// <summary>
    /// 絶対パスを <c>assets://</c> 仮想パスへ変換する。
    /// </summary>
    /// <param name="assetsRoot">アセットルートの絶対パス。</param>
    /// <param name="absolutePath">変換したいファイル／フォルダの絶対パス。</param>
    /// <returns>
    /// <c>assets://…</c> 形式の文字列。アセットルート自身なら <c>assets://</c>。
    /// ルート外なら <c>null</c>。
    /// </returns>
    public static string? ToAssetUri(string? assetsRoot, string? absolutePath)
    {
        var relative = ToRelative(assetsRoot, absolutePath);
        return relative == null ? null : Scheme + relative;
    }

    /// <summary>
    /// 複数パスをクリップボード用の 1 つの文字列へまとめる（1 行 1 パス）。
    /// 変換できなかった要素（<c>null</c>）は落とす。
    /// </summary>
    /// <param name="values">行として並べる文字列（<c>null</c> 可）。</param>
    /// <returns>改行区切りの文字列。1 件も無ければ空文字。</returns>
    public static string JoinLines(IEnumerable<string?> values)
    {
        var lines = values.Where(v => !string.IsNullOrEmpty(v)).Select(v => v!);
        return string.Join(Environment.NewLine, lines);
    }
}
