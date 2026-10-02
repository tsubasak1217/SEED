// ============================================================
//  Program.cs — SeedTemplateThumbnails（テンプレートアクタの見本の画像を作る）の入口
//
//  【流れ】
//    1. 引数の解釈（ThumbnailArguments）と入力の確定（ThumbnailInputs: ライブラリ・ランタイム・作業フォルダ）
//    2. カタログの読み込み（TemplateActorCatalog = エディタの「テンプレートアクタを追加」の窓と同じ）と、
//       見本の撮り方（thumbnail_sample。ThumbnailSampleCatalog）の読み込み
//    3. 作業の置き場を借りる（ThumbnailWorkFolder。道具の印のある置き場・無い置き場・空の置き場だけを使い、
//       プロジェクト・印の無い中身のあるフォルダ・別の実行が使っている置き場は断る〈終了コード 4〉）。
//       借りたら前の実行の中身（assets/・shots/・ランタイムのログ）を消す
//    4. 件ごとの舞台の計画（StageSceneBuilder。--only で絞る。skip の件・書き損じの件はここで結果へ）
//    5. 撮影（ThumbnailGenerator: ランタイムを 1 回だけ起動し、LOAD_SCENE で舞台を順に読み込んで撮る）
//    6. 結果の一覧（ThumbnailReport。カタログに現れた順。ThumbnailResultOrder）と、--sheet のときの確認用の画像（ContactSheet）
//    7. 作業の置き場を返す（既定の実行ごとの置き場は、成功したら消す。失敗したらログを見られるよう残す）
//
//  【書くもの】
//  templates/<フォルダ>/thumbnails/<テンプレートのファイル名（拡張子なし）>.png（カタログの thumbnail 欄があればそこ）。
//  ほかは作業フォルダ（--work）の中だけ（一時のプロジェクト・撮った元の画像・ランタイムのログ・セーブ）。
//  詳細は docs/template_library.md §9.10。
// ============================================================

using System.Diagnostics;
using System.Text.Json.Nodes;
using SEEDEditor.Templates.Actors;
using SEEDEditor.Tools.SeedTemplateThumbnails.Imaging;
using SEEDEditor.Tools.SeedTemplateThumbnails.Stage;
using SEEDEditor.Tools.SeedTemplateThumbnails.Work;

namespace SEEDEditor.Tools.SeedTemplateThumbnails;

/// <summary>SeedTemplateThumbnails の入口。</summary>
public static class Program
{
    // ── 終了コード（Usage の説明と一致させる）──────────────

    /// <summary>すべて書けた（飛ばした件を含む）。</summary>
    private const int ExitSuccess = 0;

    /// <summary>引数・入力の誤り。</summary>
    private const int ExitInvalidInput = 1;

    /// <summary>撮れなかった件がある。</summary>
    private const int ExitSomeFailed = 2;

    /// <summary>ランタイムを使えなかった。</summary>
    private const int ExitRuntimeUnavailable = 3;

    /// <summary>作業の置き場を使えない（プロジェクト・道具の印の無い中身のあるフォルダ・別の実行が使用中・前の中身を消せない）。</summary>
    private const int ExitWorkFolderUnusable = 4;

    /// <summary>
    /// 見本の画像を作る。WPF の画像の機能（確認用の画像の文字の描画を含む）のため STA で動かす。
    /// </summary>
    /// <param name="args">コマンドライン引数（ThumbnailArguments.Usage 参照）。</param>
    /// <returns>プロセスの終了コード。</returns>
    [STAThread]
    public static int Main(string[] args)
    {
        var clock = Stopwatch.StartNew();

        // ── 1. 引数・入力 ──
        var parsed = ThumbnailArguments.Parse(args);
        if (parsed.ShowHelp)
        {
            Console.WriteLine(ThumbnailArguments.Usage);
            return ExitSuccess;
        }
        if (parsed.Error is not null || parsed.Options is null)
        {
            Console.Error.WriteLine($"❌ {parsed.Error}");
            Console.Error.WriteLine(ThumbnailArguments.Usage);
            return ExitInvalidInput;
        }
        var options = parsed.Options;
        if (ThumbnailInputs.Resolve(options, out var inputError) is not { } inputs)
        {
            Console.Error.WriteLine($"❌ {inputError}");
            return ExitInvalidInput;
        }
        Console.WriteLine($"ライブラリ: {inputs.LibraryRoot}");
        Console.WriteLine($"ランタイム: {inputs.RuntimeExe}（作業フォルダ {inputs.RuntimeWorkingDirectory}）");

        // ── 2. カタログ ──
        var catalog = TemplateActorCatalog.Load(inputs.LibraryRoot);
        foreach (var warning in catalog.Warnings) Console.WriteLine($"  カタログの警告: {warning}");
        var sampleWarnings = new List<string>();
        var samples = ThumbnailSampleCatalog.Load(inputs.LibraryRoot, sampleWarnings);
        foreach (var warning in sampleWarnings) Console.WriteLine($"  カタログの警告: {warning}");

        var entries = catalog.Entries
            .Where(e => options.Only.Count == 0 || options.Only.Contains(Path.GetFileNameWithoutExtension(e.TemplateRelPath)))
            .ToList();
        var unknown = options.Only.Where(o => !catalog.Entries.Any(e =>
            string.Equals(Path.GetFileNameWithoutExtension(e.TemplateRelPath), o, StringComparison.OrdinalIgnoreCase))).ToList();
        if (unknown.Count > 0)
        {
            Console.Error.WriteLine($"❌ --only の名前がカタログにありません: {string.Join(", ", unknown)}");
            return ExitInvalidInput;
        }
        Console.WriteLine($"カタログ: {catalog.Entries.Count} 件（撮る対象 {entries.Count} 件）");

        // ── 3. 作業の置き場を借りる（中身を消してよいのは道具の印のある置き場だけ。プロジェクトなどは断る）──
        if (ThumbnailWorkFolder.Acquire(inputs.WorkRoot, inputs.WorkIsTemporary, out var workError) is not { } work)
        {
            Console.Error.WriteLine($"❌ {workError}");
            return ExitWorkFolderUnusable;
        }
        // 例外で抜けたときも失敗扱い（既定の置き場もログを見られるよう残す）
        int exitCode = ExitWorkFolderUnusable;
        try
        {
            Console.WriteLine($"作業の置き場: {work.Root}（{work.DescribeVerdict()}）");
            try
            {
                work.CleanForRun();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"❌ 作業の置き場の前の中身（assets/・shots/・ログ）を消せません（ほかのプロセスが使っていないか確かめてください）: {ex.Message}");
                return exitCode;
            }
            exitCode = PlanAndShoot(options, inputs, work, catalog, samples, entries, clock);
            return exitCode;
        }
        finally
        {
            // ── 7. 返す（既定の置き場は成功したら消す）──
            var kept = work.Release(succeeded: exitCode == ExitSuccess);
            if (kept is not null && work.IsTemporary)
                Console.WriteLine($"作業の置き場を残しました（ランタイムのログ・撮った元の画像を見られます。要らなければ消してください）: {kept}");
        }
    }

    /// <summary>
    /// 計画・撮影・結果の一覧（手順 4〜6）。
    /// </summary>
    /// <param name="options">引数。</param>
    /// <param name="inputs">確定した入力。</param>
    /// <param name="work">借りた作業の置き場（前の中身は消してある）。</param>
    /// <param name="catalog">テンプレートのカタログ。</param>
    /// <param name="samples">見本の撮り方（テンプレートのライブラリ相対パス → thumbnail_sample）。</param>
    /// <param name="entries">撮る対象のエントリ（カタログに現れた順）。</param>
    /// <param name="clock">全体の時計。</param>
    /// <returns>終了コード。</returns>
    private static int PlanAndShoot(ThumbnailOptions options, ThumbnailInputs inputs, ThumbnailWorkFolder work,
        TemplateActorCatalog catalog, IReadOnlyDictionary<string, JsonNode> samples,
        IReadOnlyList<TemplateActorEntry> entries, Stopwatch clock)
    {
        // ── 4. 計画 ──
        var builder = new StageSceneBuilder(inputs.LibraryRoot, catalog);
        var results = new List<ThumbnailResult>();
        var plans = new List<StagePlan>();
        for (int i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            string name = Path.GetFileNameWithoutExtension(entry.TemplateRelPath);
            var errors = new List<string>();
            var sample = ThumbnailSample.Parse(samples.GetValueOrDefault(entry.TemplateRelPath), errors);
            if (sample.SkipReason.Length > 0 && errors.Count == 0)
            {
                results.Add(new ThumbnailResult(name, entry.TemplateRelPath, entry.Name, ThumbnailOutcome.Skipped,
                    sample.SkipReason, null, 0, 0, null, [], TimeSpan.Zero));
                continue;
            }
            var plan = errors.Count == 0 ? builder.Build(i, entry, sample, errors) : null;
            if (plan is null)
            {
                results.Add(new ThumbnailResult(name, entry.TemplateRelPath, entry.Name, ThumbnailOutcome.Failed,
                    "舞台を組み立てられません: " + string.Join(" / ", errors), null, 0, 0, null, [], TimeSpan.Zero));
                continue;
            }
            plans.Add(plan);
        }

        // ── 5. 撮影 ──
        var generator = new ThumbnailGenerator(inputs, work, options.Size, options.Port, Console.WriteLine);
        var runtimeError = generator.Run(plans, results);

        // ── 6. 結果（カタログに現れた順。鍵はライブラリ相対パス＝別のカタログの同じファイル名でも重ならない）──
        var sorted = ThumbnailResultOrder.Sort(entries.Select(e => e.TemplateRelPath).ToList(), results, r => r.TemplateRelPath);
        ThumbnailReport.Print(sorted, clock.Elapsed, Console.WriteLine);

        if (options.Sheet is { } sheet)
        {
            var items = sorted.Where(r => r.Outcome == ThumbnailOutcome.Written && r.OutputPath is not null)
                .Select(r => (r.Name, r.OutputPath!)).ToList();
            ContactSheet.Write(Path.GetFullPath(sheet), items, options.Size);
            Console.WriteLine($"確認用の一覧の画像: {Path.GetFullPath(sheet)}（{items.Count} 件）");
        }

        if (runtimeError is not null)
        {
            Console.Error.WriteLine($"❌ {runtimeError}");
            return ExitRuntimeUnavailable;
        }
        return sorted.Any(r => r.Outcome == ThumbnailOutcome.Failed) ? ExitSomeFailed : ExitSuccess;
    }
}
