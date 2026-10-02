// ============================================================
//  ThumbnailWorkFolderTests.cs — 作業の置き場を借りる・前の中身を消す・返す（一時フォルダの実物で確かめる）
//  （docs/reviews/2026-10-02_code_review.md #2）
//
//  【確かめること】
//   - プロジェクト（assets/project_settings.json・*.seedproj）と印の無い中身のあるフォルダは断り、何も消さない・何も書かない
//   - 無い・空の置き場は作って印を書く。印のある置き場は前の assets/・shots/・runtime*.log だけを消す（cache/・save/ は残す）
//   - 同じ置き場を同時に借りる 2 つ目の実行は断る（錠）。返した後はまた借りられる
//   - 既定の置き場（実行ごと）は成功したら消し、失敗したら残す。--work で指定した置き場は消さない
// ============================================================

using SEEDEditor.Tools.SeedTemplateThumbnails.Work;
using SpriteRigTests;

namespace SEEDEditor.Tests.TemplateThumbnails;

/// <summary>ThumbnailWorkFolder の単体テスト（一時フォルダの実物を使う）。</summary>
public static class ThumbnailWorkFolderTests
{
    /// <summary>テストを登録する。</summary>
    /// <param name="h">テストランナー。</param>
    public static void Register(TestHarness h)
    {
        h.Add("置き場: 実プロジェクト（assets/project_settings.json）は断り、assets を消さない・印も書かない", RefusesProjectWithoutTouching);
        h.Add("置き場: .seedproj のあるフォルダ・印の無い中身のあるフォルダ・ファイルは断る", RefusesForeignAndFiles);
        h.Add("置き場: 無い・空のフォルダは作って印を書き、印のある置き場は前の assets/・shots/・ログだけを消す", CleansOnlyOwnedLeftovers);
        h.Add("置き場: 同じ置き場を同時に借りる 2 つ目は断り、返した後はまた借りられる", LockRefusesSecondRun);
        h.Add("置き場: 既定（実行ごと）は成功したら消して失敗したら残す・--work 指定は消さない", ReleaseRemovesOnlyTemporaryOnSuccess);
    }

    /// <summary>実プロジェクトは断る（何も消さない・何も書かない）。</summary>
    private static void RefusesProjectWithoutTouching()
    {
        using var temp = new TempFolder();
        var settings = temp.WriteFile("Game/assets/project_settings.json", "{ \"game_name\": \"Game\" }");
        var texture = temp.WriteFile("Game/assets/textures/a.png", "png");
        var root = temp.Combine("Game");

        var work = ThumbnailWorkFolder.Acquire(root, isTemporary: false, out var error);
        Check.True(work is null, "実プロジェクトを借りてしまった");
        Check.True(error.Contains(root, StringComparison.Ordinal), $"理由に置き場のパスが無い: {error}");
        Check.True(File.Exists(settings) && File.Exists(texture), "断ったのにプロジェクトの assets が消えた");
        Check.True(!File.Exists(Path.Combine(root, WorkFolderPolicy.MarkerFileName)), "断ったのに印を書いた");
        Check.True(!File.Exists(Path.Combine(root, WorkFolderPolicy.LockFileName)), "断ったのに錠を残した");
        Check.Equal(WorkFolderVerdict.RefuseProject, WorkFolderPolicy.Judge(ThumbnailWorkFolder.Inspect(root)), "判定");
    }

    /// <summary>.seedproj・印の無い中身・ファイルは断る。</summary>
    private static void RefusesForeignAndFiles()
    {
        using var temp = new TempFolder();

        // .seedproj がある（印があってもプロジェクト）
        temp.WriteFile("Proj/Proj.seedproj", "{}");
        temp.WriteFile("Proj/" + WorkFolderPolicy.MarkerFileName, "x");
        temp.WriteFile("Proj/assets/keep.txt", "keep");
        Check.True(ThumbnailWorkFolder.Acquire(temp.Combine("Proj"), false, out _) is null, ".seedproj のあるフォルダを借りた");
        Check.True(File.Exists(temp.Combine("Proj/assets/keep.txt")), ".seedproj のあるフォルダの assets が消えた");

        // 印の無い中身のあるフォルダ（利用者のフォルダかもしれない）
        temp.WriteFile("Docs/assets/notes.txt", "keep");
        temp.WriteFile("Docs/runtime_notes.log", "keep");
        var foreign = ThumbnailWorkFolder.Acquire(temp.Combine("Docs"), false, out var foreignError);
        Check.True(foreign is null, "印の無い中身のあるフォルダを借りた");
        Check.True(foreignError.Contains(WorkFolderPolicy.MarkerFileName, StringComparison.Ordinal), $"理由に印の名前が無い: {foreignError}");
        Check.True(File.Exists(temp.Combine("Docs/assets/notes.txt")) && File.Exists(temp.Combine("Docs/runtime_notes.log")),
            "印の無いフォルダの中身が消えた");

        // ファイル
        var file = temp.WriteFile("plain.txt", "file");
        Check.True(ThumbnailWorkFolder.Acquire(file, false, out _) is null, "ファイルを置き場として借りた");
        Check.True(File.Exists(file), "ファイルが消えた");
    }

    /// <summary>無い・空は作って印を書き、印のある置き場は決まったものだけ消す。</summary>
    private static void CleansOnlyOwnedLeftovers()
    {
        using var temp = new TempFolder();

        // ── 無い置き場: 作って印を書く ──
        var fresh = temp.Combine("fresh/work");
        using (var work = ThumbnailWorkFolder.Acquire(fresh, false, out var error) ?? throw new AssertionException("無い置き場を借りられない: " + error))
        {
            Check.Equal(WorkFolderVerdict.CreateNew, work.Verdict, "判定");
            Check.True(File.Exists(Path.Combine(fresh, WorkFolderPolicy.MarkerFileName)), "印が無い");
            Check.True(File.Exists(Path.Combine(fresh, WorkFolderPolicy.LockFileName)), "借りている間は錠のファイルがある");
            work.CleanForRun();
            Check.True(Directory.Exists(work.AssetsRoot) && Directory.Exists(work.ShotsRoot), "CleanForRun は空の assets/・shots/ を作る");
        }
        Check.True(!File.Exists(Path.Combine(fresh, WorkFolderPolicy.LockFileName)), "返したら錠のファイルは消える");

        // ── 空の置き場: 印を書いて使う ──
        var empty = temp.Combine("empty");
        Directory.CreateDirectory(empty);
        using (var work = ThumbnailWorkFolder.Acquire(empty, false, out var error) ?? throw new AssertionException("空の置き場を借りられない: " + error))
            Check.Equal(WorkFolderVerdict.AdoptEmpty, work.Verdict, "判定");

        // ── 印のある置き場（前の実行の残り）: assets/・shots/・runtime*.log だけ消す ──
        var owned = temp.Combine("owned");
        temp.WriteFile("owned/" + WorkFolderPolicy.MarkerFileName, "marker");
        temp.WriteFile("owned/assets/project_settings.json", "{}");          // 道具が前に書いた一時のプロジェクト
        temp.WriteFile("owned/assets/__thumbnails/_boot.scene", "{}");
        temp.WriteFile("owned/shots/ui_button.png", "png");
        temp.WriteFile("owned/runtime_540x540_1.log", "log");
        temp.WriteFile("owned/cache/model.smdl", "cache");                    // 残す（次の読み込みを速くする）
        temp.WriteFile("owned/save/save.json", "{}");                         // 残す
        temp.WriteFile("owned/notes.log", "keep");                            // runtime*.log に当たらないものは残す
        using (var work = ThumbnailWorkFolder.Acquire(owned, false, out var error) ?? throw new AssertionException("印のある置き場を借りられない: " + error))
        {
            Check.Equal(WorkFolderVerdict.ReuseOwned, work.Verdict, "判定");
            work.CleanForRun();
            Check.True(!File.Exists(temp.Combine("owned/assets/project_settings.json")), "前の一時のプロジェクトが残っている");
            Check.True(!File.Exists(temp.Combine("owned/assets/__thumbnails/_boot.scene")), "前の舞台が残っている");
            Check.True(!File.Exists(temp.Combine("owned/shots/ui_button.png")), "前の撮った画像が残っている");
            Check.True(!File.Exists(temp.Combine("owned/runtime_540x540_1.log")), "前のランタイムのログが残っている");
            Check.True(File.Exists(temp.Combine("owned/cache/model.smdl")), "モデルのキャッシュを消した");
            Check.True(File.Exists(temp.Combine("owned/save/save.json")), "セーブを消した");
            Check.True(File.Exists(temp.Combine("owned/notes.log")), "runtime*.log に当たらないファイルを消した");
            Check.True(File.Exists(temp.Combine("owned/" + WorkFolderPolicy.MarkerFileName)), "印を消した");
        }

        // ── 返した後の置き場は消せない（錠の無い置き場は消さない）──
        var released = ThumbnailWorkFolder.Acquire(owned, false, out _)!;
        released.Dispose();
        bool threw = false;
        try { released.CleanForRun(); }
        catch (ObjectDisposedException) { threw = true; }
        Check.True(threw, "返した後の CleanForRun が中身を消せてしまう");
    }

    /// <summary>同時に借りる 2 つ目は断る。</summary>
    private static void LockRefusesSecondRun()
    {
        using var temp = new TempFolder();
        var root = temp.Combine("shared");
        var first = ThumbnailWorkFolder.Acquire(root, false, out var firstError) ?? throw new AssertionException("1 つ目を借りられない: " + firstError);
        temp.WriteFile("shared/assets/in_use.scene", "{}");   // 1 つ目の実行が使っている中身

        var second = ThumbnailWorkFolder.Acquire(root, false, out var secondError);
        Check.True(second is null, "使用中の置き場を 2 つ目の実行が借りた");
        Check.True(secondError.Contains("別の実行", StringComparison.Ordinal), $"理由が使用中を伝えない: {secondError}");
        Check.True(File.Exists(temp.Combine("shared/assets/in_use.scene")), "2 つ目の実行が 1 つ目の中身を消した");

        first.Release(succeeded: true);
        using var third = ThumbnailWorkFolder.Acquire(root, false, out var thirdError);
        Check.True(third is not null, "返した後に借りられない: " + thirdError);
    }

    /// <summary>既定の置き場だけ、成功したら消す。</summary>
    private static void ReleaseRemovesOnlyTemporaryOnSuccess()
    {
        using var temp = new TempFolder();

        // 既定（実行ごと）・成功 → 消す
        var okRoot = temp.Combine("runs/run_ok");
        var ok = ThumbnailWorkFolder.Acquire(okRoot, isTemporary: true, out _)!;
        ok.CleanForRun();
        File.WriteAllText(Path.Combine(okRoot, "runtime_540x540_1.log"), "log");
        Check.True(ok.Release(succeeded: true) is null, "成功したのに残したと返した");
        Check.True(!Directory.Exists(okRoot), "成功した既定の置き場が残っている");

        // 既定（実行ごと）・失敗 → 残す（ログを見られる）
        var failRoot = temp.Combine("runs/run_fail");
        var fail = ThumbnailWorkFolder.Acquire(failRoot, isTemporary: true, out _)!;
        fail.CleanForRun();
        var log = Path.Combine(failRoot, "runtime_540x540_1.log");
        File.WriteAllText(log, "log");
        Check.Equal(failRoot, fail.Release(succeeded: false), "失敗したら残した置き場を返す");
        Check.True(File.Exists(log), "失敗した既定の置き場のログが消えた");
        Check.True(!File.Exists(Path.Combine(failRoot, WorkFolderPolicy.LockFileName)), "返したのに錠が残っている");

        // --work 指定・成功 → 消さない
        var explicitRoot = temp.Combine("explicit");
        var given = ThumbnailWorkFolder.Acquire(explicitRoot, isTemporary: false, out _)!;
        given.CleanForRun();
        Check.Equal(explicitRoot, given.Release(succeeded: true), "--work 指定は残す");
        Check.True(Directory.Exists(explicitRoot), "--work 指定の置き場を消した");
    }
}
