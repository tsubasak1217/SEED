// ============================================================
//  TemplateImporter.cs — テンプレートのインポート（計画作成と実行）
//
//  【役割】
//  「選んだテンプレートを、依存ごとプロジェクトのアセットへ持ってくる」処理。
//   1. CreatePlan : 選択エントリ → 依存の閉包 → コピー計画（衝突判定込み）
//   2. Execute    : 計画 + 衝突方針 → 実際のコピー → 結果
//
//  【依存の閉包をどう求めるか】
//  ライブラリのフォルダ構成は**アセットルートと同じ形**である。
//    templates/scenes/physicsTest.scene が assets://shaders/toon.wgsl を参照する
//      ＝ ライブラリルートを基準にすれば templates/shaders/toon.wgsl に解決できる
//  したがって「ライブラリのルートをアセットルートとみなして」
//  パッケージ化と同じ <see cref="AssetCollector"/> を走らせれば、
//  参照の閉包・同伴ファイル（.tvox → .tscatter / terrain_meta.json）・欠落検出まで
//  すべて既存の実装をそのまま使える。収集規則を 2 つ持たずに済むのが利点。
//
//  ただし起点だけは違う（project_settings.json も登録シーンも無い）ので、
//  「渡した起点からだけ辿る」入口 <see cref="AssetCollector.CollectFrom"/> を使う。
//
//  【コピー先】
//  ライブラリ相対パスがそのままアセットルート相対パスになる。
//    templates/shaders/toon.wgsl  →  <ProjectAssets>/shaders/toon.wgsl
//  "templates/" という中間フォルダは作らない。
// ============================================================

using System;
using System.Collections.Generic;
using System.IO;
using SEEDEditor.Packaging.Collect;

namespace SEEDEditor.Templates;

/// <summary>
/// テンプレートライブラリからプロジェクトのアセットへコピーする処理。
/// 状態を持たない静的ユーティリティ（計画オブジェクトが状態を運ぶ）。
/// </summary>
public static class TemplateImporter
{
    // ============================================================
    //  1. 計画の作成
    // ============================================================

    /// <summary>
    /// 選択されたエントリから、依存を含むコピー計画を作る。ファイルには一切触れない。
    /// </summary>
    /// <param name="libraryRoot">テンプレートライブラリのルート（絶対パス）。</param>
    /// <param name="assetsRoot">コピー先のプロジェクトアセットルート（絶対パス）。</param>
    /// <param name="entryRelPaths">
    /// 選択されたエントリのライブラリ相対パス。フォルダエントリならその配下すべてが対象になる。
    /// </param>
    /// <param name="log">進行状況の出力先（省略可）。</param>
    /// <returns>コピー計画。</returns>
    public static TemplateImportPlan CreatePlan(
        string libraryRoot,
        string assetsRoot,
        IEnumerable<string> entryRelPaths,
        Action<string>? log = null)
    {
        var fullLibraryRoot = Path.GetFullPath(libraryRoot);
        var fullAssetsRoot  = Path.GetFullPath(assetsRoot);
        var selected        = Normalize(entryRelPaths);

        // ── 依存の閉包を取る ───────────────────────────────────
        //   収録ルール設定（AssetPackagingSettings）は CollectFrom では
        //   除外統計の算出にしか使われないため、既定値のままで構わない
        //   （除外ルールは収録を止めない。AssetCollector のクラスコメント参照）。
        var collector = new AssetCollector(
            fullLibraryRoot, new AssetPackagingSettings(), runtimeSourceRoot: null, log);
        var collected = collector.CollectFrom(selected);

        // ── コピー計画へ変換する（衝突判定を添える） ────────────
        var items = new List<TemplateCopyItem>(collected.Included.Count);
        foreach (var asset in collected.Included)
        {
            var destination = AssetPathUtil.ToAbsolute(fullAssetsRoot, asset.RelPath);
            items.Add(new TemplateCopyItem(
                asset.RelPath,
                asset.SizeBytes,
                ConflictsWithExisting: File.Exists(destination)));
        }

        return new TemplateImportPlan
        {
            LibraryRoot        = fullLibraryRoot,
            AssetsRoot         = fullAssetsRoot,
            SelectedEntryPaths = selected,
            Items              = items,
            MissingReferences  = collected.MissingReferences,
        };
    }

    /// <summary>
    /// 指定されたエントリ相対パスを正規形（'/' 区切り・重複なし）へ揃える。
    /// </summary>
    /// <param name="entryRelPaths">選択されたエントリの相対パス。</param>
    /// <returns>正規化した相対パス（入力順を保つ）。</returns>
    private static List<string> Normalize(IEnumerable<string> entryRelPaths)
    {
        var list = new List<string>();
        var seen = new HashSet<string>(AssetPathUtil.PathComparer);
        foreach (var raw in entryRelPaths)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var rel = AssetPathUtil.NormalizeRelative(raw);
            if (rel.Length == 0 || !seen.Add(rel)) continue;
            list.Add(rel);
        }
        return list;
    }

    // ============================================================
    //  2. 実行
    // ============================================================

    /// <summary>
    /// 計画に従ってファイルをコピーする。
    ///
    /// <para>
    /// 衝突（コピー先に同名ファイルがある）の判定は、計画時の値ではなく
    /// <b>実行時にもう一度</b> 行う。計画を見せてからユーザーが決めるまでの間に
    /// ファイルが増減しうるため、上書き可否は必ず最新の状態で決める。
    /// </para>
    /// </summary>
    /// <param name="plan">コピー計画。</param>
    /// <param name="policy">衝突したときの扱い。</param>
    /// <param name="log">進行状況の出力先（省略可）。</param>
    /// <returns>コピー結果。</returns>
    public static TemplateImportResult Execute(
        TemplateImportPlan plan,
        TemplateImportConflictPolicy policy,
        Action<string>? log = null)
    {
        // コピー元と先が同じ場所なら何もしない（自分自身への上書き事故を防ぐ）
        if (AssetPathUtil.PathComparer.Equals(
                plan.LibraryRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                plan.AssetsRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)))
        {
            log?.Invoke("⚠ コピー元とコピー先が同じフォルダです（何もしませんでした）");
            return new TemplateImportResult { MissingReferences = plan.MissingReferences };
        }

        int  copied      = 0;
        int  overwritten = 0;
        int  skipped     = 0;
        long copiedBytes = 0;
        var  failures    = new List<TemplateCopyFailure>();

        foreach (var item in plan.Items)
        {
            var source      = AssetPathUtil.ToAbsolute(plan.LibraryRoot, item.RelPath);
            var destination = AssetPathUtil.ToAbsolute(plan.AssetsRoot,  item.RelPath);

            // ── 衝突の扱い ─────────────────────────────────────
            bool exists = File.Exists(destination);
            if (exists && policy == TemplateImportConflictPolicy.Skip)
            {
                skipped++;
                continue;
            }

            // ── コピー ─────────────────────────────────────────
            try
            {
                var destinationDir = Path.GetDirectoryName(destination);
                if (!string.IsNullOrEmpty(destinationDir)) Directory.CreateDirectory(destinationDir);

                File.Copy(source, destination, overwrite: true);

                copied++;
                copiedBytes += item.SizeBytes;
                if (exists) overwritten++;
            }
            catch (Exception ex)
            {
                failures.Add(new TemplateCopyFailure(item.RelPath, ex.Message));
                log?.Invoke($"⚠ コピー失敗: {item.RelPath} — {ex.Message}");
            }
        }

        log?.Invoke($"インポート完了: コピー {copied} 件（上書き {overwritten} 件） / " +
                    $"スキップ {skipped} 件 / 失敗 {failures.Count} 件");

        return new TemplateImportResult
        {
            CopiedCount       = copied,
            OverwrittenCount  = overwritten,
            SkippedCount      = skipped,
            CopiedBytes       = copiedBytes,
            Failures          = failures,
            MissingReferences = plan.MissingReferences,
        };
    }
}
