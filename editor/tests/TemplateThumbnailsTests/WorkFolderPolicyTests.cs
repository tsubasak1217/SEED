// ============================================================
//  WorkFolderPolicyTests.cs — 作業の置き場を使ってよいかの判定（純粋な処理。ファイルシステムに触らない）
//  （docs/reviews/2026-10-02_code_review.md #2）
// ============================================================

using SEEDEditor.Tools.SeedTemplateThumbnails.Work;
using SpriteRigTests;

namespace SEEDEditor.Tests.TemplateThumbnails;

/// <summary>WorkFolderPolicy の単体テスト。</summary>
public static class WorkFolderPolicyTests
{
    /// <summary>既定の置き場のテストに使うプロセス ID（実際の値でなくてよい）。</summary>
    private const int SampleProcessId = 4242;

    /// <summary>テストを登録する。</summary>
    /// <param name="h">テストランナー。</param>
    public static void Register(TestHarness h)
    {
        h.Add("置き場の判定: 無い・空・印あり は使う／プロジェクト・印の無い中身・ファイル は断る", JudgesEveryCase);
        h.Add("置き場の判定: 印があっても .seedproj があればプロジェクト（道具は .seedproj を作らない）", SeedProjectWinsOverMarker);
        h.Add("置き場の判定: 断る理由の文に置き場のパスと直し方が入る", RefusalMessages);
        h.Add("既定の置き場: %TEMP%\\seed_template_thumbnails の下の実行ごとの下位フォルダ（プロセス ID と乱数）", DefaultRunFolderShape);
    }

    /// <summary>調べた結果を組み立てる（既定は「ある・空でない・何も無い」）。</summary>
    private static WorkFolderSnapshot Snapshot(
        bool exists = true, bool isFile = false, bool marker = false, bool projectSettings = false,
        bool seedProject = false, bool empty = false) =>
        new(exists, isFile, marker, projectSettings, seedProject, empty);

    /// <summary>全分岐の判定。</summary>
    private static void JudgesEveryCase()
    {
        Check.Equal(WorkFolderVerdict.CreateNew, WorkFolderPolicy.Judge(Snapshot(exists: false, empty: true)), "無い → 作る");
        Check.Equal(WorkFolderVerdict.AdoptEmpty, WorkFolderPolicy.Judge(Snapshot(empty: true)), "空 → 印を書いて使う");
        Check.Equal(WorkFolderVerdict.ReuseOwned, WorkFolderPolicy.Judge(Snapshot(marker: true)), "印あり → 使い直す");
        Check.Equal(WorkFolderVerdict.ReuseOwned, WorkFolderPolicy.Judge(Snapshot(marker: true, projectSettings: true)),
            "印あり＋assets/project_settings.json（道具が書いた一時のプロジェクト）→ 使い直す");
        Check.Equal(WorkFolderVerdict.RefuseProject, WorkFolderPolicy.Judge(Snapshot(projectSettings: true)),
            "印なし＋assets/project_settings.json（実プロジェクト）→ 断る");
        Check.Equal(WorkFolderVerdict.RefuseForeign, WorkFolderPolicy.Judge(Snapshot()),
            "印なし・中身あり（利用者のフォルダかもしれない）→ 断る");
        Check.Equal(WorkFolderVerdict.RefuseNotDirectory, WorkFolderPolicy.Judge(Snapshot(exists: false, isFile: true)),
            "ファイル → 断る");

        // 使ってよい判定は 3 つだけ
        foreach (var verdict in Enum.GetValues<WorkFolderVerdict>())
        {
            bool expected = verdict is WorkFolderVerdict.CreateNew or WorkFolderVerdict.AdoptEmpty or WorkFolderVerdict.ReuseOwned;
            Check.Equal(expected, WorkFolderPolicy.MayUse(verdict), $"MayUse({verdict})");
        }
    }

    /// <summary>.seedproj は印より強い。</summary>
    private static void SeedProjectWinsOverMarker()
    {
        Check.Equal(WorkFolderVerdict.RefuseProject, WorkFolderPolicy.Judge(Snapshot(seedProject: true)), ".seedproj だけ → 断る");
        Check.Equal(WorkFolderVerdict.RefuseProject, WorkFolderPolicy.Judge(Snapshot(marker: true, seedProject: true)),
            "印＋.seedproj → 断る（プロジェクトに印が紛れた）");
    }

    /// <summary>断る理由の文。</summary>
    private static void RefusalMessages()
    {
        const string root = @"D:\SEED_projects\Sample";
        foreach (var verdict in new[] { WorkFolderVerdict.RefuseProject, WorkFolderVerdict.RefuseForeign, WorkFolderVerdict.RefuseNotDirectory })
        {
            var message = WorkFolderPolicy.DescribeRefusal(verdict, root);
            Check.True(message.Contains(root, StringComparison.Ordinal), $"{verdict} の文に置き場のパスが無い: {message}");
            Check.True(message.Contains("--work", StringComparison.Ordinal), $"{verdict} の文にどの引数の話かが無い: {message}");
        }
        Check.True(WorkFolderPolicy.DescribeRefusal(WorkFolderVerdict.RefuseForeign, root).Contains(WorkFolderPolicy.MarkerFileName, StringComparison.Ordinal),
            "印の無いフォルダの文に印のファイル名が無い");
        Check.Equal("", WorkFolderPolicy.DescribeRefusal(WorkFolderVerdict.ReuseOwned, root), "使ってよい判定の文は空");
    }

    /// <summary>既定の置き場の形。</summary>
    private static void DefaultRunFolderShape()
    {
        var temp = Path.GetTempPath();
        var a = WorkFolderPolicy.DefaultRunFolder(temp, SampleProcessId, "0a1b2c3d");
        var b = WorkFolderPolicy.DefaultRunFolder(temp, SampleProcessId, "deadbeef");
        Check.True(!string.Equals(a, b, StringComparison.OrdinalIgnoreCase), "乱数が違えば置き場も違う（同じプロセス ID が使い回されても重ならない）");

        var parent = Path.GetFullPath(Path.Combine(temp, WorkFolderPolicy.DefaultParentFolderName));
        Check.Equal(parent, Path.GetDirectoryName(a), "親は %TEMP%\\seed_template_thumbnails");
        Check.Equal("run_4242_0a1b2c3d", Path.GetFileName(a), "名前は run_<プロセス ID>_<乱数>");

        // 道具が実際に使う既定の置き場（乱数入り）も、2 回作れば違うパスになる
        var first = ThumbnailWorkFolder.NewDefaultRunFolder();
        var second = ThumbnailWorkFolder.NewDefaultRunFolder();
        Check.True(!string.Equals(first, second, StringComparison.OrdinalIgnoreCase), $"既定の置き場が重なった: {first}");
        Check.True(Path.GetFileName(first).StartsWith($"run_{Environment.ProcessId}_", StringComparison.Ordinal),
            $"既定の置き場の名前にこのプロセスの ID が入る: {first}");
    }
}
