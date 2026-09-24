using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using SEEDEditor.Runtime.BuildConfig;

namespace MigrationTests;

/// <summary>
/// Rust 側の版の表（<c>runtime/src/engine/core/migration/kind.rs</c>）を読み取る。
///
/// <para>
/// C# の <see cref="SEEDEditor.Migration.AssetFormats"/> は kind.rs の**写し**であり、
/// 写しである以上は必ずずれる。ここで正典を機械的に読み、突き合わせることで
/// 「片方だけ直した」をテストの失敗として表に出す。
/// </para>
/// <para>
/// Rust を構文解析するわけではないので、kind.rs の書き方（<c>FormatSpec</c> の
/// const 宣言が並ぶ形）に依存する。書き方を変えたらここも直すこと
/// ——ただし**黙って通る**ことは無い（件数が 0 になればテストが落ちる）。
/// </para>
/// </summary>
public static class RustFormatTable
{
    /// <summary>kind.rs のリポジトリルートからの相対パス。</summary>
    public const string KindRsRelativePath =
        @"runtime\src\engine\core\migration\kind.rs";

    /// <summary>Rust 側の <c>VersionKey::FormatVersion</c> の綴り。</summary>
    private const string RUST_VERSION_KEY_FORMAT_VERSION = "FormatVersion";

    /// <summary>Rust 側の <c>VersionKey::Version</c> の綴り。</summary>
    private const string RUST_VERSION_KEY_VERSION = "Version";

    /// <summary>Rust 側の <c>FormatWriter::Runtime</c> の綴り。</summary>
    private const string RUST_WRITER_RUNTIME = "Runtime";

    /// <summary>Rust 側の <c>FormatWriter::Editor</c> の綴り。</summary>
    private const string RUST_WRITER_EDITOR = "Editor";

    /// <summary>kind.rs から読み取った 1 形式ぶんの仕様。</summary>
    /// <param name="Label">形式名。</param>
    /// <param name="VersionKeyVariant">版の欄名の列挙名（FormatVersion / Version）。</param>
    /// <param name="CurrentVersion">現行版。</param>
    /// <param name="WriterVariant">書き手の列挙名（Runtime / Editor）。</param>
    public sealed record Entry(
        string Label, string VersionKeyVariant, int CurrentVersion, string WriterVariant);

    /// <summary>1 件の <c>FormatSpec</c> const 宣言を丸ごと拾う。</summary>
    private static readonly Regex SpecBlock = new(
        @"const\s+\w+\s*:\s*FormatSpec\s*=\s*FormatSpec\s*\{(?<body>.*?)\};",
        RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex LabelField =
        new(@"label\s*:\s*""(?<value>[^""]+)""", RegexOptions.Compiled);

    private static readonly Regex VersionKeyField =
        new(@"version_key\s*:\s*VersionKey::(?<value>\w+)", RegexOptions.Compiled);

    private static readonly Regex CurrentVersionField =
        new(@"current_version\s*:\s*(?<value>\d+)", RegexOptions.Compiled);

    private static readonly Regex WriterField =
        new(@"writer\s*:\s*FormatWriter::(?<value>\w+)", RegexOptions.Compiled);

    /// <summary>
    /// kind.rs の絶対パスを求める。見つからなければ null。
    /// </summary>
    public static string? FindKindRs()
    {
        // テスト実行ディレクトリ（bin/Debug/net10.0/）から上へ辿って
        // runtime/Cargo.toml を持つフォルダ（＝リポジトリルート）を探す。
        var repoRoot = RuntimeExeLocator.FindRepoRoot(AppContext.BaseDirectory)
                       ?? RuntimeExeLocator.FindRepoRoot(Environment.CurrentDirectory);
        if (repoRoot is null) return null;

        var path = Path.Combine(repoRoot, KindRsRelativePath);
        return File.Exists(path) ? path : null;
    }

    /// <summary>
    /// kind.rs を読み、形式名 → 仕様の対応を返す。
    /// </summary>
    /// <param name="kindRsPath">kind.rs の絶対パス。</param>
    public static IReadOnlyDictionary<string, Entry> Parse(string kindRsPath)
    {
        var source = File.ReadAllText(kindRsPath);

        // テスト用の const（#[cfg(test)] の中）まで拾わないよう、テストモジュール以降は切る。
        var testsAt = source.IndexOf("#[cfg(test)]", StringComparison.Ordinal);
        if (testsAt >= 0) source = source[..testsAt];

        var result = new Dictionary<string, Entry>(StringComparer.Ordinal);
        foreach (Match block in SpecBlock.Matches(source))
        {
            var body = block.Groups["body"].Value;

            var label      = Single(LabelField, body);
            var versionKey = Single(VersionKeyField, body);
            var version    = Single(CurrentVersionField, body);
            var writer     = Single(WriterField, body);
            if (label is null || versionKey is null || version is null || writer is null) continue;

            result[label] = new Entry(
                label, versionKey, int.Parse(version, System.Globalization.CultureInfo.InvariantCulture), writer);
        }
        return result;
    }

    /// <summary>C# 側の列挙を Rust 側の綴りへ直す（突き合わせ用）。</summary>
    /// <param name="key">C# 側の版の欄名。</param>
    public static string ToRustVersionKey(SEEDEditor.Migration.AssetVersionKey key)
        => key == SEEDEditor.Migration.AssetVersionKey.FormatVersion
            ? RUST_VERSION_KEY_FORMAT_VERSION
            : RUST_VERSION_KEY_VERSION;

    /// <summary>C# 側の列挙を Rust 側の綴りへ直す（突き合わせ用）。</summary>
    /// <param name="writer">C# 側の書き手。</param>
    public static string ToRustWriter(SEEDEditor.Migration.AssetFormatWriter writer)
        => writer == SEEDEditor.Migration.AssetFormatWriter.Runtime
            ? RUST_WRITER_RUNTIME
            : RUST_WRITER_EDITOR;

    /// <summary>1 件だけ拾う（見つからなければ null）。</summary>
    /// <param name="regex">拾う正規表現。</param>
    /// <param name="body">対象のテキスト。</param>
    private static string? Single(Regex regex, string body)
    {
        var match = regex.Match(body);
        return match.Success ? match.Groups["value"].Value : null;
    }
}
