using System.Text.Json;
using SEEDEditor.Preview;
using SpriteRigTests;

namespace ScreenPreviewTests;

/// <summary>
/// 最近プレビューしたプレハブの一覧（PreviewRecentList の規則と PreviewRecentStore の保存）。
/// </summary>
public static class RecentTests
{
    /// <summary>テストを登録する。</summary>
    public static void Register(TestHarness h)
    {
        h.Add("最近: 使ったものを先頭へ移す", PushMovesToFront);
        h.Add("最近: 重複は大小文字を無視して 1 つ（新しい方を先頭に残す）", PushDedupesIgnoringCase);
        h.Add("最近: 上限 8 件・空白は足さない", PushCapsAndIgnoresBlank);
        h.Add("保存: プロジェクトごとに往復する（キーは大小文字・末尾の区切りを見ない）", StoreRoundTrip);
        h.Add("保存: 無いファイルは空、壊れたファイルは空で続けて次の保存で直る", StoreBrokenFile);
    }

    private static void PushMovesToFront()
    {
        var list = PreviewRecentList.Push(["assets://a.actor", "assets://b.actor", "assets://c.actor"], "assets://c.actor");
        Check.Equal("assets://c.actor,assets://a.actor,assets://b.actor", string.Join(",", list), "先頭へ移す");
        var added = PreviewRecentList.Push(list, "assets://d.actor");
        Check.Equal("assets://d.actor,assets://c.actor,assets://a.actor,assets://b.actor", string.Join(",", added), "新しいものは先頭");
        Check.Equal(1, PreviewRecentList.Push(null, "assets://x.actor").Count, "空の一覧へ足す");
    }

    private static void PushDedupesIgnoringCase()
    {
        var list = PreviewRecentList.Push(["assets://UI/A.actor", "assets://b.actor"], "assets://ui/a.actor");
        Check.Equal("assets://ui/a.actor,assets://b.actor", string.Join(",", list), "大小文字違いは同じもの");
        var normalized = PreviewRecentList.Normalize([" assets://x.actor ", "ASSETS://X.ACTOR", null, "", "assets://y.actor"]);
        Check.Equal("assets://x.actor,assets://y.actor", string.Join(",", normalized), "整える（空白・null・重複）");
    }

    private static void PushCapsAndIgnoresBlank()
    {
        IReadOnlyList<string> list = Array.Empty<string>();
        for (int i = 0; i < PreviewRecentList.MaxCount + 3; i++)
            list = PreviewRecentList.Push(list, $"assets://p{i}.actor");
        Check.Equal(PreviewRecentList.MaxCount, list.Count, "上限");
        Check.Equal(8, PreviewRecentList.MaxCount, "上限は 8 件");
        Check.Equal($"assets://p{PreviewRecentList.MaxCount + 2}.actor", list[0], "最新が先頭");
        Check.Equal("assets://p3.actor", list[^1], "古いものから落ちる");
        var unchanged = PreviewRecentList.Push(list, "   ");
        Check.Equal(string.Join(",", list), string.Join(",", unchanged), "空白は足さない");
    }

    private static void StoreRoundTrip()
    {
        using var temp = new TempDir();
        var store = new PreviewRecentStore(Path.Combine(temp.Path, "settings", PreviewRecentStore.FileName));
        var rootA = Path.Combine(temp.Path, "ProjA", "assets");
        var rootB = Path.Combine(temp.Path, "ProjB", "assets");

        store.Push(rootA, "assets://ui/one.actor");
        store.Push(rootA, "assets://ui/画面.actor");
        store.Push(rootB, "assets://b.actor");
        Check.True(store.LastError is null, $"保存の失敗: {store.LastError}");

        // 別の窓口（＝エディタを開き直した）から読む
        var reopened = new PreviewRecentStore(store.FilePath);
        Check.Equal("assets://ui/画面.actor,assets://ui/one.actor", string.Join(",", reopened.Get(rootA)), "A の一覧（最近の順）");
        Check.Equal("assets://b.actor", string.Join(",", reopened.Get(rootB)), "B の一覧");
        Check.Equal(2, reopened.Get(rootA.ToUpperInvariant() + Path.DirectorySeparatorChar).Count, "大小文字・末尾の区切りを見ない");
        Check.Equal(0, reopened.Get(Path.Combine(temp.Path, "ProjC")).Count, "無いプロジェクトは空");
        Check.Equal(0, reopened.Get(null).Count, "アセットルートなしは空");

        // ファイルの形（format_version と projects）
        using var doc = JsonDocument.Parse(File.ReadAllText(store.FilePath));
        Check.Equal(PreviewRecentStore.SupportedFormatVersion, doc.RootElement.GetProperty("format_version").GetInt32(), "format_version");
        Check.Equal(2, doc.RootElement.GetProperty("projects").EnumerateObject().Count(), "プロジェクトの数");
        Check.True(doc.RootElement.GetProperty("projects").EnumerateObject().All(p => p.Name == p.Name.ToLowerInvariant() && !p.Name.Contains('\\')),
                   "キーは '/' 区切りの小文字");
        Check.True(!File.Exists(store.FilePath + ".tmp"), "一時ファイルを残さない");
    }

    private static void StoreBrokenFile()
    {
        using var temp = new TempDir();
        var root = Path.Combine(temp.Path, "assets");

        var missing = new PreviewRecentStore(Path.Combine(temp.Path, "none", PreviewRecentStore.FileName));
        Check.Equal(0, missing.Get(root).Count, "無いファイルは空");
        Check.True(missing.LastError is null, "無いファイルは問題ではない");

        var brokenPath = temp.WriteFile(PreviewRecentStore.FileName, "{ \"format_version\": 1, \"projects\": { ");
        var broken = new PreviewRecentStore(brokenPath);
        Check.Equal(0, broken.Get(root).Count, "壊れたファイルは空");
        Check.True(broken.LastError is not null, "壊れたことは知らせる");

        var after = broken.Push(root, "assets://a.actor");
        Check.Equal(1, after.Count, "壊れていても足せる");
        Check.Equal("assets://a.actor", string.Join(",", new PreviewRecentStore(brokenPath).Get(root)), "次の保存で正しい形に戻る");
    }
}
