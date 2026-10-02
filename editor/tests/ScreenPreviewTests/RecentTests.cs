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
        h.Add("保存: 別のエディタが書きかけの一時ファイルを開いていても保存できる（一時ファイルの名前は書き込みごとに違う）", StoreTempNameIsPerWrite);
        h.Add("保存: 置き換えに失敗したら一時ファイルを残さない（理由は LastError）", StoreFailureLeavesNoTemp);
    }

    /// <summary>保存ファイルと同じフォルダに残った一時ファイル（*.tmp）。</summary>
    private static string[] TempFilesNextTo(string filePath) =>
        Directory.GetFiles(Path.GetDirectoryName(filePath)!, "*.tmp");

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
        Check.Equal(0, TempFilesNextTo(store.FilePath).Length, "一時ファイルを残さない");
    }

    /// <summary>
    /// 別のエディタ（B）が以前の固定の一時ファイル名（screen_preview_recent.json.tmp）へ書いている最中でも、
    /// こちら（A）の保存は B の書きかけに触れずに成功する（2026-10-02 のレビュー #18。以前は名前が固定で、互いの書きかけを
    /// 本体へ移して JSON を壊すか、開けずに保存し損ねた）。
    /// </summary>
    private static void StoreTempNameIsPerWrite()
    {
        using var temp = new TempDir();
        var store = new PreviewRecentStore(Path.Combine(temp.Path, PreviewRecentStore.FileName));
        var root = Path.Combine(temp.Path, "Proj", "assets");
        store.Push(root, "assets://first.actor");

        // B の書きかけ（共有を許して開いたまま。以前の固定の名前）
        var legacyTemp = store.FilePath + ".tmp";
        using (var writerB = new FileStream(legacyTemp, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
        {
            writerB.Write("{ \"format_version\": 1, \"projects\": { "u8);
            writerB.Flush();

            store.Push(root, "assets://second.actor");
            Check.True(store.LastError is null, $"別のエディタの書きかけがあると保存できない: {store.LastError}");
        }

        Check.Equal("assets://second.actor,assets://first.actor", string.Join(",", new PreviewRecentStore(store.FilePath).Get(root)),
            "本体は A の書いた内容（B の書きかけが混ざっていない）");
        File.Delete(legacyTemp);   // B の書きかけはこのテストが作ったもの

        // 書き込みごとの一時ファイルの名前（同じフォルダ・違う名前・.tmp で終わる）
        var a = PreviewRecentStore.MakeTempPath(store.FilePath);
        var b = PreviewRecentStore.MakeTempPath(store.FilePath);
        Check.True(!string.Equals(a, b, StringComparison.OrdinalIgnoreCase), "一時ファイルの名前が書き込みごとに違わない");
        Check.Equal(Path.GetDirectoryName(store.FilePath), Path.GetDirectoryName(a), "一時ファイルは同じフォルダ（置き換えが名前の付け替えで済む）");
        Check.True(Path.GetFileName(a).StartsWith(PreviewRecentStore.FileName, StringComparison.Ordinal) && a.EndsWith(".tmp", StringComparison.Ordinal),
            $"一時ファイルの名前は <保存ファイル名>.<固有>.tmp: {a}");
        Check.Equal(0, TempFilesNextTo(store.FilePath).Length, "一時ファイルを残さない");
    }

    /// <summary>置き換えに失敗したら一時ファイルを消す（名前が毎回違うので、残すと溜まる）。</summary>
    private static void StoreFailureLeavesNoTemp()
    {
        using var temp = new TempDir();
        var store = new PreviewRecentStore(Path.Combine(temp.Path, PreviewRecentStore.FileName));
        var root = Path.Combine(temp.Path, "Proj", "assets");
        store.Push(root, "assets://first.actor");

        // 本体を「消し・置き換えを許さない」共有で開いておく（ほかのプロセスが読んでいる最中の再現）→ 置き換えが失敗する
        using (var reader = new FileStream(store.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            store.Push(root, "assets://second.actor");
            Check.True(store.LastError is not null, "置き換えに失敗したことを知らせない");
            Check.Equal(0, TempFilesNextTo(store.FilePath).Length,
                "置き換えに失敗した一時ファイルが残っている: " + string.Join(", ", TempFilesNextTo(store.FilePath).Select(Path.GetFileName)));
        }

        Check.Equal("assets://first.actor", string.Join(",", new PreviewRecentStore(store.FilePath).Get(root)), "失敗した保存は本体を変えない");
        store.Push(root, "assets://second.actor");
        Check.True(store.LastError is null, $"開放された後は保存できる: {store.LastError}");
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
