// ============================================================
//  TemplateActorDependencyPlanner.cs — テンプレートアクタを動かすのに要るファイルのコピー
//
//  【役割】
//  テンプレートアクタは「まっさらなアクタ」としてシーンへ入れる（.actor はプロジェクトに作らない）。
//  ただしテンプレートが参照しているファイル（テクスチャ・見本のスクリプト・
//  スクリプトが実行中に読む部品の .actor・モデル…）がプロジェクトに無いと動かないので、
//  それらだけをライブラリから**同じ相対パス**へコピーする。
//   1. Plan    : まっさらにした木の JSON ＋ カタログの requires → 参照の閉包 → コピー計画
//   2. Execute : 計画に従ってコピー（**既にあるファイルは触らない**。上書きはしない）
//
//  【参照の集め方】
//  テンプレートの**元のファイル**ではなく、まっさらにした後の JSON を走査する。
//  入れ子のプレハブの参照（prefab_source）は取り除いた後なので、展開済みの部品の .actor が
//  「参照されている」と誤ってコピーされることがない。
//  走査はパッケージ化・テンプレートのインポートと同じ AssetReferenceScanner / AssetCollector を使い、
//  収集の規則を 2 つ持たない（docs/template_library.md §5）。
//
//  【取り違えの防止】
//  AssetReferenceScanner は引用符で囲まれた一般の文字列（"Label" など）も「パスかもしれない」として
//  返す。ここでは**ライブラリに実在するファイル**に当たったものだけを起点にし、フォルダは起点にしない
//  （"ui" のような文字列がフォルダに当たって、フォルダごとコピーされる事故を防ぐ）。
// ============================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SEEDEditor.Packaging.Collect;

namespace SEEDEditor.Templates.Actors;

/// <summary>
/// テンプレートアクタが必要とするファイル 1 件。
/// </summary>
/// <param name="RelPath">ライブラリ相対パス（＝コピー先のアセットルート相対パス）。</param>
/// <param name="SizeBytes">ライブラリ側のバイトサイズ。</param>
/// <param name="AlreadyInProject">プロジェクトに同じ相対パスのファイルが既にあるなら true（触らない）。</param>
public readonly record struct TemplateActorDependency(
    string RelPath,
    long SizeBytes,
    bool AlreadyInProject);

/// <summary>
/// テンプレートアクタの依存ファイルのコピー計画。
/// </summary>
public sealed class TemplateActorDependencyPlan
{
    /// <summary>コピー元のライブラリルート（絶対パス）。</summary>
    public required string LibraryRoot { get; init; }

    /// <summary>コピー先のプロジェクトのアセットルート（絶対パス）。</summary>
    public required string AssetsRoot { get; init; }

    /// <summary>必要なファイル（相対パスの昇順）。テンプレート自身は含まない。</summary>
    public IReadOnlyList<TemplateActorDependency> Files { get; init; } = [];

    /// <summary>ライブラリにもプロジェクトにも見つからなかった参照（利用者へ知らせる）。</summary>
    public IReadOnlyList<string> Missing { get; init; } = [];

    /// <summary>これからコピーするファイル（プロジェクトに無いもの）。</summary>
    public IReadOnlyList<TemplateActorDependency> ToCopy =>
        Files.Where(f => !f.AlreadyInProject).ToList();

    /// <summary>プロジェクトに既にあるので触らないファイル。</summary>
    public IReadOnlyList<TemplateActorDependency> AlreadyPresent =>
        Files.Where(f => f.AlreadyInProject).ToList();
}

/// <summary>
/// 依存ファイルのコピーの結果。
/// </summary>
public sealed class TemplateActorCopyResult
{
    /// <summary>コピーしたファイルの相対パス。</summary>
    public IReadOnlyList<string> Copied { get; init; } = [];

    /// <summary>プロジェクトに既にあったので触らなかったファイルの相対パス。</summary>
    public IReadOnlyList<string> SkippedExisting { get; init; } = [];

    /// <summary>コピーに失敗したファイルと理由。</summary>
    public IReadOnlyList<TemplateCopyFailure> Failures { get; init; } = [];

    /// <summary>1 件でもコピーしたなら true（プロジェクトパネルの再読み込みの判断に使う）。</summary>
    public bool HasCopied => Copied.Count > 0;
}

/// <summary>
/// テンプレートアクタの依存ファイルの計画と実行。状態を持たない静的ユーティリティ。
/// </summary>
public static class TemplateActorDependencyPlanner
{
    // ============================================================
    //  1. 計画
    // ============================================================

    /// <summary>
    /// まっさらにした木の JSON とカタログの requires から、コピー計画を作る。ファイルには触れない。
    /// </summary>
    /// <param name="libraryRoot">ライブラリルートの絶対パス。</param>
    /// <param name="assetsRoot">プロジェクトのアセットルートの絶対パス。</param>
    /// <param name="templateRelPath">テンプレートのライブラリ相対パス（コピー対象から必ず外す）。</param>
    /// <param name="flattenedJson">まっさらにした木の JSON テキスト。</param>
    /// <param name="requiredRelPaths">カタログの requires（ライブラリ相対パス）。</param>
    /// <param name="log">進行状況の出力先（省略可）。</param>
    /// <returns>コピー計画。</returns>
    public static TemplateActorDependencyPlan Plan(
        string libraryRoot,
        string assetsRoot,
        string templateRelPath,
        string flattenedJson,
        IEnumerable<string> requiredRelPaths,
        Action<string>? log = null)
    {
        var fullLibrary = Path.GetFullPath(libraryRoot);
        var fullAssets  = Path.GetFullPath(assetsRoot);
        var seeds       = new List<string>();
        var seedSet     = new HashSet<string>(AssetPathUtil.PathComparer);
        var missing     = new List<string>();
        var missingSet  = new HashSet<string>(AssetPathUtil.PathComparer);

        void AddSeed(string rel)
        {
            if (seedSet.Add(rel)) seeds.Add(rel);
        }
        void AddMissing(string text)
        {
            if (missingSet.Add(text)) missing.Add(text);
        }

        // ── ① 木の JSON に書かれた参照 ──────────────────────────
        foreach (var candidate in AssetReferenceScanner.Scan(flattenedJson, templateRelPath, fullLibrary))
        {
            var hit = candidate.Candidates.FirstOrDefault(rel => IsFile(fullLibrary, rel));
            if (hit is not null) { AddSeed(hit); continue; }

            // ライブラリに無くても、プロジェクトに既にあれば動く（欠落ではない）
            bool inProject = candidate.Candidates.Any(rel => IsFile(fullAssets, rel));
            if (candidate.IsExplicit && !inProject) AddMissing(candidate.Raw);
        }

        // ── ② カタログが明示した「実行時に読む」ファイル ─────────────
        foreach (var raw in requiredRelPaths)
        {
            var rel = AssetPathUtil.NormalizeRelative(raw);
            if (rel.Length == 0) continue;
            if (IsFile(fullLibrary, rel)) AddSeed(rel);
            else if (!IsFile(fullAssets, rel)) AddMissing(rel);
        }

        if (seeds.Count == 0)
        {
            // 起点が無ければライブラリ全体の索引も作らない（数千ファイルの列挙を省く）
            return new TemplateActorDependencyPlan
            {
                LibraryRoot = fullLibrary, AssetsRoot = fullAssets, Missing = missing,
            };
        }

        // ── ③ 閉包（テクスチャを参照するスクリプト…のような多段の参照も辿る）──
        var collector = new AssetCollector(fullLibrary, new AssetPackagingSettings(), runtimeSourceRoot: null, log);
        var collected = collector.CollectFrom(seeds);

        var normalizedTemplate = AssetPathUtil.NormalizeRelative(templateRelPath);
        var files = new List<TemplateActorDependency>();
        foreach (var asset in collected.Included)
        {
            // テンプレート自身は絶対にコピーしない（プレハブをプロジェクトに作らない約束）
            if (AssetPathUtil.PathComparer.Equals(asset.RelPath, normalizedTemplate)) continue;
            files.Add(new TemplateActorDependency(
                asset.RelPath, asset.SizeBytes, AlreadyInProject: IsFile(fullAssets, asset.RelPath)));
        }

        foreach (var m in collected.MissingReferences)
            if (!IsFile(fullAssets, m.ReferencePath)) AddMissing(m.ReferencePath);

        return new TemplateActorDependencyPlan
        {
            LibraryRoot = fullLibrary,
            AssetsRoot  = fullAssets,
            Files       = files.OrderBy(f => f.RelPath, AssetPathUtil.PathComparer).ToList(),
            Missing     = missing,
        };
    }

    // ============================================================
    //  2. 実行
    // ============================================================

    /// <summary>
    /// 計画に従ってファイルをコピーする。**既にあるファイルは上書きしない**。
    /// 既にあるかどうかは計画時ではなく実行時にもう一度確かめる（間にファイルが増えうるため）。
    /// </summary>
    /// <param name="plan">コピー計画。</param>
    /// <param name="log">進行状況の出力先（省略可）。</param>
    /// <returns>コピーの結果。</returns>
    public static TemplateActorCopyResult Execute(TemplateActorDependencyPlan plan, Action<string>? log = null)
    {
        var copied   = new List<string>();
        var skipped  = new List<string>();
        var failures = new List<TemplateCopyFailure>();

        foreach (var file in plan.Files)
        {
            var source      = AssetPathUtil.ToAbsolute(plan.LibraryRoot, file.RelPath);
            var destination = AssetPathUtil.ToAbsolute(plan.AssetsRoot,  file.RelPath);

            if (File.Exists(destination))
            {
                skipped.Add(file.RelPath);
                continue;
            }

            try
            {
                var dir = Path.GetDirectoryName(destination);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                // overwrite: false … 確認の直後に誰かが置いたファイルも上書きしない
                File.Copy(source, destination, overwrite: false);
                copied.Add(file.RelPath);
            }
            catch (IOException) when (File.Exists(destination))
            {
                // 確認とコピーの間に同じ名前のファイルができた（上書きしない約束どおり触らない）
                skipped.Add(file.RelPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failures.Add(new TemplateCopyFailure(file.RelPath, ex.Message));
                log?.Invoke($"テンプレートアクタの依存ファイルをコピーできませんでした: {file.RelPath} — {ex.Message}");
            }
        }

        return new TemplateActorCopyResult { Copied = copied, SkippedExisting = skipped, Failures = failures };
    }

    /// <summary>ルート相対パスが、そのルートの下に実在する**ファイル**かを判定する（フォルダは false）。</summary>
    private static bool IsFile(string root, string rel) =>
        rel.Length > 0 && File.Exists(AssetPathUtil.ToAbsolute(root, rel));
}
