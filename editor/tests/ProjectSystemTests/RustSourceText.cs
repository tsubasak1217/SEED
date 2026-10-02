using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using SpriteRigTests;

namespace ProjectSystemTests;

/// <summary>
/// ランタイム（Rust）のソースから、エディタが写している定数・綴りを読み取る（突き合わせ用）。
///
/// <para>
/// エディタの RenderProfileFlagCatalog / FontFieldCatalog はランタイムのキー名・値の<b>写し</b>であり、写しである以上は
/// 必ずずれる。ここで正典（flags.rs など）を機械的に読み、突き合わせることで「片方だけ直した」をテストの失敗として表に出す
/// （MigrationTests の RustFormatTable と同じ考え方）。Rust を構文解析するわけではないので、書き方を変えたらここも直すこと
/// ——ただし<b>黙って通る</b>ことは無い（読み取れた件数が 0 ならテストが落ちる）。
/// </para>
/// </summary>
public static class RustSourceText
{
    /// <summary>リポジトリルートの目印（ランタイムの Cargo.toml）。</summary>
    private static readonly string RepoRootMarker = Path.Combine("runtime", "Cargo.toml");

    /// <summary><c>pub const NAME: &amp;str = "value";</c> を拾う。</summary>
    private static readonly Regex StrConstant = new(
        @"const\s+(?<name>\w+)\s*:\s*&str\s*=\s*""(?<value>[^""]*)""\s*;", RegexOptions.Compiled);

    /// <summary><c>pub const NAME: u64 = 1;</c> のような整数の定数を拾う。</summary>
    private static readonly Regex IntConstant = new(
        @"const\s+(?<name>\w+)\s*:\s*(?:u8|u16|u32|u64|usize|i32|i64)\s*=\s*(?<value>\d+)\s*;", RegexOptions.Compiled);

    /// <summary>リポジトリルートを探す（テストの実行場所から上へ辿る。見つからなければ表明の失敗）。</summary>
    /// <returns>リポジトリルートの絶対パス。</returns>
    public static string RepoRoot()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
            {
                if (File.Exists(Path.Combine(dir.FullName, RepoRootMarker))) return dir.FullName;
            }
        }
        throw new AssertionException($"リポジトリルート（{RepoRootMarker} のあるフォルダ）が見つかりません");
    }

    /// <summary>リポジトリルートからの相対パスのファイルを読む（無ければ表明の失敗）。</summary>
    /// <param name="relativePath">相対パス（区切りは / でよい）。</param>
    /// <returns>中身。</returns>
    public static string Read(string relativePath)
    {
        var path = Path.Combine(RepoRoot(), relativePath.Replace('/', Path.DirectorySeparatorChar));
        Check.True(File.Exists(path), $"正典のソースが見つかりません: {path}");
        return File.ReadAllText(path);
    }

    /// <summary>文字列の定数（名前 → 値）を拾う。</summary>
    /// <param name="source">ソースの中身。</param>
    /// <returns>定数の表。</returns>
    public static IReadOnlyDictionary<string, string> StrConstants(string source) =>
        StrConstant.Matches(source)
            .GroupBy(m => m.Groups["name"].Value)
            .ToDictionary(g => g.Key, g => g.First().Groups["value"].Value);

    /// <summary>整数の定数（名前 → 値）を拾う。</summary>
    /// <param name="source">ソースの中身。</param>
    /// <returns>定数の表。</returns>
    public static IReadOnlyDictionary<string, int> IntConstants(string source) =>
        IntConstant.Matches(source)
            .GroupBy(m => m.Groups["name"].Value)
            .ToDictionary(g => g.Key, g => int.Parse(g.First().Groups["value"].Value));

    /// <summary>文字列の定数の値を返す（無ければ表明の失敗）。</summary>
    /// <param name="constants">定数の表。</param>
    /// <param name="name">定数名。</param>
    /// <param name="file">出どころ（失敗の文言用）。</param>
    /// <returns>値。</returns>
    public static string Require(IReadOnlyDictionary<string, string> constants, string name, string file)
    {
        Check.True(constants.TryGetValue(name, out var value), $"{file} に定数 {name} が見つかりません（書き方が変わった？）");
        return value!;
    }

    /// <summary>
    /// enum の <c>as_str</c> の腕（<c>Self::Variant =&gt; "text"</c>）を拾う（列挙子 → 綴り）。
    /// </summary>
    /// <param name="source">ソースの中身。</param>
    /// <returns>列挙子 → 綴り。</returns>
    public static IReadOnlyDictionary<string, string> AsStrArms(string source) =>
        Regex.Matches(source, @"Self::(?<variant>\w+)\s*=>\s*""(?<text>[^""]+)""")
            .GroupBy(m => m.Groups["variant"].Value)
            .ToDictionary(g => g.Key, g => g.First().Groups["text"].Value);

    /// <summary>
    /// enum の <c>parse</c> の腕（<c>"a" | "b" =&gt; Some(Self::Variant)</c>）を拾う（受け付ける綴り → 列挙子）。
    /// </summary>
    /// <param name="source">ソースの中身。</param>
    /// <returns>綴り → 列挙子。</returns>
    public static IReadOnlyDictionary<string, string> ParseArms(string source)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match arm in Regex.Matches(source, @"(?<words>""[^""]+""(?:\s*\|\s*""[^""]+"")*)\s*=>\s*Some\(Self::(?<variant>\w+)\)"))
        {
            foreach (Match word in Regex.Matches(arm.Groups["words"].Value, @"""(?<w>[^""]+)"""))
            {
                result[word.Groups["w"].Value] = arm.Groups["variant"].Value;
            }
        }
        return result;
    }

    /// <summary><c>#[default]</c> の付いた列挙子の名前を返す（無ければ表明の失敗）。</summary>
    /// <param name="source">ソースの中身。</param>
    /// <param name="file">出どころ（失敗の文言用）。</param>
    /// <returns>列挙子の名前。</returns>
    public static string DefaultVariant(string source, string file)
    {
        var match = Regex.Match(source, @"#\[default\]\s*(?:///[^\n]*\n\s*)*(?<variant>\w+)\s*,");
        Check.True(match.Success, $"{file} に #[default] の列挙子が見つかりません");
        return match.Groups["variant"].Value;
    }
}
