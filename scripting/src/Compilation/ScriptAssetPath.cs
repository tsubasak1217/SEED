// ============================================================
//  ScriptAssetPath.cs — スクリプトパスの正規化（照合キーの唯一の定義）
//
//  【役割】
//  「.scene に書かれたスクリプト参照文字列」と「ディスク上の .cs の場所」を
//  同じ土俵で比較するための **正規キー** を作る。
//
//  【なぜ 1 か所に集めるか】
//  参照文字列は歴史的に 3 形式が混在している。
//    ① 仮想パス   : assets://mainGame/scripts/Foo.cs   （PAK 化で書き換えたもの）
//    ② 絶対パス   : C:\...\runtime\assets\title\scripts\Foo.cs（エディタ保存のもの）
//    ③ 素の型名   : Foo                                （旧形式）
//  正規化規則を各所へ散らすと「エディタでは解決できるがパッケージ版だけ
//  Script type not found になる」形でズレが表面化するため、規則はここだけに置く。
// ============================================================

using System;
using System.IO;

namespace SEEDEditor.Scripting.Compilation;

/// <summary>
/// スクリプト参照文字列とファイルパスを、照合用の正規キーへ揃えるユーティリティ。
/// キーは「アセットルート相対・'/' 区切り・小文字」の 1 形式に統一する。
/// </summary>
public static class ScriptAssetPath
{
    /// <summary>PAK 化後のアセット参照に付く仮想パスの接頭辞。</summary>
    public const string VirtualPathPrefix = "assets://";

    /// <summary>スクリプトソースの拡張子（小文字）。</summary>
    public const string ScriptExtension = ".cs";

    /// <summary>正規キーで使うパス区切り文字。</summary>
    private const char CanonicalSeparator = '/';

    /// <summary>Windows のパス区切り文字（入力に混ざりうる）。</summary>
    private const char WindowsSeparator = '\\';

    /// <summary>カレントディレクトリを表す相対指定の接頭辞（除去対象）。</summary>
    private const string CurrentDirectoryPrefix = "./";

    /// <summary>
    /// アセットルート相対のパスを正規キーへ揃える。
    /// </summary>
    /// <param name="relativePath">アセットルート相対のパス（区切り・大小文字は問わない）。</param>
    /// <returns>正規キー（'/' 区切り・小文字・先頭の区切りと "./" を除去済み）。</returns>
    public static string ToKey(string relativePath)
    {
        if (string.IsNullOrEmpty(relativePath)) return "";

        var s = relativePath.Trim().Replace(WindowsSeparator, CanonicalSeparator);

        // "./foo/bar.cs" と "/foo/bar.cs" を "foo/bar.cs" へ揃える
        while (s.StartsWith(CurrentDirectoryPrefix, StringComparison.Ordinal))
            s = s[CurrentDirectoryPrefix.Length..];
        s = s.TrimStart(CanonicalSeparator);

        return s.ToLowerInvariant();
    }

    /// <summary>
    /// アセットルート配下の絶対パスから正規キーを作る。
    /// </summary>
    /// <param name="assetsRoot">アセットルートの絶対パス。</param>
    /// <param name="absolutePath">.cs の絶対パス。</param>
    /// <returns>正規キー。ルート配下でない場合はファイル名だけのキー。</returns>
    public static string KeyFromAbsolute(string assetsRoot, string absolutePath)
    {
        try
        {
            var root = Path.GetFullPath(assetsRoot);
            var full = Path.GetFullPath(absolutePath);

            // ルート配下かどうかを、末尾区切りを補ってから判定する
            // （"assets" と "assets_backup" を取り違えないため）。
            var rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar)
                ? root
                : root + Path.DirectorySeparatorChar;

            if (full.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
                return ToKey(full[rootWithSeparator.Length..]);

            // ルート外（想定外だが落とさない）はファイル名だけを返す
            return ToKey(Path.GetFileName(full));
        }
        catch
        {
            // パスとして解釈できない入力でもキー生成は失敗させない
            return ToKey(Path.GetFileName(absolutePath));
        }
    }

    /// <summary>
    /// シーン等に保存された参照文字列から正規キーを作る。
    /// </summary>
    /// <param name="nameOrPath">参照文字列（assets:// 形式 / 絶対パス / 相対パス）。</param>
    /// <returns>
    /// 正規キー。assets:// 形式なら接頭辞を除いたアセット相対キーになる。
    /// 絶対パスの場合はそのまま正規化した文字列（相対キーとは一致しない）。
    /// </returns>
    public static string KeyFromReference(string nameOrPath)
    {
        if (string.IsNullOrEmpty(nameOrPath)) return "";

        var s = nameOrPath.Trim();
        if (s.StartsWith(VirtualPathPrefix, StringComparison.OrdinalIgnoreCase))
            s = s[VirtualPathPrefix.Length..];

        return ToKey(s);
    }

    /// <summary>
    /// 参照文字列が .cs ファイル指定かを判定する。
    /// </summary>
    /// <param name="nameOrPath">参照文字列。</param>
    /// <returns>.cs で終わるなら true。</returns>
    public static bool IsScriptFileReference(string nameOrPath) =>
        nameOrPath.EndsWith(ScriptExtension, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 正規キーからファイル名部分（例 "foo.cs"）を取り出す。
    /// </summary>
    /// <param name="key">正規キー。</param>
    /// <returns>ファイル名（小文字）。</returns>
    public static string FileNameOfKey(string key)
    {
        var i = key.LastIndexOf(CanonicalSeparator);
        return i < 0 ? key : key[(i + 1)..];
    }
}
