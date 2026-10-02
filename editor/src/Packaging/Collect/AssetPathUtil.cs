// ============================================================
//  AssetPathUtil.cs — アセットパスの正規化ユーティリティ
//
//  【役割】
//  収録判定と PAK 書き出しが共通で使う「アセットルート相対パス」の作法を
//  1 か所に閉じる。区切りは '/'、比較は大文字小文字を無視（Windows に合わせる）。
//
//  【なぜ必要か】
//  参照文字列は 4 系統（assets:// / 絶対パス / 参照元からの相対 / ルート相対）あり、
//  ここで 1 つの正規形（ルート相対・'/' 区切り）へ寄せてから集合演算をしないと、
//  同じファイルを別物として二重に数えたり取りこぼしたりする。
// ============================================================

using System;
using System.Collections.Generic;
using System.IO;

namespace SEEDEditor.Packaging.Collect;

/// <summary>アセットルート相対パスの正規化と比較を担うユーティリティ。</summary>
public static class AssetPathUtil
{
    /// <summary>仮想パスのスキーム（ランタイム asset_fs.rs の ASSETS_SCHEME と同一）。</summary>
    public const string AssetsScheme = "assets://";

    /// <summary>アセットルート相対パスの比較子（Windows に合わせて大文字小文字を無視する）。</summary>
    public static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;

    /// <summary>
    /// 相対パスを正規形（'/' 区切り・先頭の "./" と '/' を除去・末尾の '/' を除去）へ整える。
    ///
    /// <para>
    /// 末尾の '/' を落とすのは、フォルダの参照（スクリプトの定数 <c>"assets://common/data/"</c> など）を
    /// フォルダの索引（<see cref="AssetCollector"/> は末尾 '/' なしで登録する）と同じ形にするため。
    /// 2026-10-03 まで落としていなかったので、末尾 '/' のフォルダ参照は照合に外れて黙って収録から漏れていた
    /// （Wake or Pay の APK にデータの JSON が入らなかった。docs/backlog.md W3）。
    /// ファイルの参照は末尾が '/' にならないので、この変更の影響を受けない。
    /// </para>
    /// </summary>
    /// <param name="relative">整えたい相対パス。</param>
    /// <returns>正規化された相対パス。</returns>
    public static string NormalizeRelative(string relative)
    {
        var s = relative.Replace('\\', '/').Trim();
        while (s.StartsWith("./", StringComparison.Ordinal)) s = s[2..];
        s = s.TrimStart('/');
        return CollapseDotSegments(s).TrimEnd('/');
    }

    /// <summary>
    /// 生の参照文字列（正規化する前。<see cref="AssetReferenceCandidate.Raw"/>）が区切り（'/' か '\'）で終わるかを返す。
    ///
    /// <para>
    /// 末尾の区切りは <see cref="NormalizeRelative"/> が落とすので、正規化した後の候補からは「フォルダを指して書かれたか」が分からない。
    /// C# のコメントの中にだけ書かれた末尾 '/' のフォルダ参照（説明の文の「置き場」）を展開しないために使う
    /// （<see cref="AssetCollector"/>。2026-10-03 の 2 回目のレビュー #26）。JSON エスケープした絶対パス（<c>…\/art\/</c>・<c>…\\art\\</c>）も
    /// 最後の文字が '/' か '\' なので同じく当たる。
    /// </para>
    /// </summary>
    /// <param name="raw">元のテキストに書かれていた参照文字列。</param>
    /// <returns>最後の文字が '/' か '\' なら true（空文字は false）。</returns>
    public static bool EndsWithSeparator(string raw)
        => raw.Length > 0 && (raw[^1] == '/' || raw[^1] == '\\');

    /// <summary>
    /// パス中の ".." / "." セグメントを解決して短い形に畳む。
    /// glTF の uri（"../textures/a.png" など）を扱うために必要。
    /// </summary>
    /// <param name="path">'/' 区切りの相対パス。</param>
    /// <returns>畳んだ相対パス。ルートより上へ出る場合は空文字を返す。</returns>
    public static string CollapseDotSegments(string path)
    {
        // ".." も "./" も無いなら分解する必要がない（大半はこちらを通る）
        if (!path.Contains("..", StringComparison.Ordinal) &&
            !path.Contains("./", StringComparison.Ordinal))
            return path;

        var stack = new List<string>();
        foreach (var seg in path.Split('/'))
        {
            if (seg.Length == 0 || seg == ".") continue;
            if (seg == "..")
            {
                // ルートより上へ出る参照はアセット外なので無効とする
                if (stack.Count == 0) return "";
                stack.RemoveAt(stack.Count - 1);
                continue;
            }
            stack.Add(seg);
        }
        return string.Join('/', stack);
    }

    /// <summary>
    /// 絶対パスをアセットルート相対パスへ変換する。ルート外なら null。
    /// </summary>
    /// <param name="assetsRoot">アセットルートの絶対パス。</param>
    /// <param name="absolutePath">変換したい絶対パス。</param>
    /// <returns>ルート相対パス（'/' 区切り）。ルート外なら null。</returns>
    public static string? ToRelative(string assetsRoot, string absolutePath)
    {
        var root = assetsRoot.Replace('\\', '/').TrimEnd('/') + "/";
        var abs  = absolutePath.Replace('\\', '/');
        if (!abs.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return null;
        return NormalizeRelative(abs[root.Length..]);
    }

    /// <summary>アセットルート相対パスを絶対パスへ戻す。</summary>
    /// <param name="assetsRoot">アセットルートの絶対パス。</param>
    /// <param name="relative">ルート相対パス。</param>
    /// <returns>OS の区切り文字による絶対パス。</returns>
    public static string ToAbsolute(string assetsRoot, string relative) =>
        Path.Combine(assetsRoot, relative.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>相対パスの親フォルダ部分を返す（無ければ空文字）。</summary>
    /// <param name="relative">ルート相対パス。</param>
    /// <returns>親フォルダのルート相対パス。</returns>
    public static string GetDirectory(string relative)
    {
        int i = relative.LastIndexOf('/');
        return i < 0 ? "" : relative[..i];
    }

    /// <summary>拡張子を小文字・ドット付きで返す（無ければ空文字）。</summary>
    /// <param name="path">任意のパス。</param>
    /// <returns>".png" のような小文字の拡張子。</returns>
    public static string GetExtensionLower(string path)
    {
        int slash = Math.Max(path.LastIndexOf('/'), path.LastIndexOf('\\'));
        int dot   = path.LastIndexOf('.');
        // ドットが無い / 区切りより前 / 先頭ドット（".DS_Store" のような隠しファイル）は拡張子なし扱い
        if (dot <= slash + 1) return "";
        return path[dot..].ToLowerInvariant();
    }

    /// <summary>パス末尾のファイル名部分を返す。</summary>
    /// <param name="path">任意のパス。</param>
    /// <returns>ファイル名。</returns>
    public static string GetFileName(string path)
    {
        int slash = Math.Max(path.LastIndexOf('/'), path.LastIndexOf('\\'));
        return slash < 0 ? path : path[(slash + 1)..];
    }

    /// <summary>
    /// 「ファイル拡張子らしい」文字列かを判定する（ドット + 英数字 1 文字以上）。
    ///
    /// ドキュメントコメントの "assets://..." のように、拡張子ではないドットを
    /// ファイル参照と誤認して欠落報告するのを防ぐために使う。
    /// </summary>
    /// <param name="extension">GetExtensionLower の戻り値。</param>
    /// <returns>拡張子らしければ true。</returns>
    public static bool IsLikelyExtension(string extension)
    {
        if (extension.Length < 2 || extension[0] != '.') return false;
        for (int i = 1; i < extension.Length; i++)
            if (!char.IsLetterOrDigit(extension[i])) return false;
        return true;
    }
}
