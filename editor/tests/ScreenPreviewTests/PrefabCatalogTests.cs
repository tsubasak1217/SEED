using SEEDEditor.Preview;
using SpriteRigTests;

namespace ScreenPreviewTests;

/// <summary>
/// プレハブの一覧・検索・並び（PrefabPreviewCatalog）。
/// </summary>
public static class PrefabCatalogTests
{
    /// <summary>テストを登録する。</summary>
    public static void Register(TestHarness h)
    {
        h.Add("一覧: .actor / .actor2d だけを列挙し、'.' で始まるフォルダは飛ばす", ScanListsPrefabsOnly);
        h.Add("一覧: 仮想パス（'/' 区切り）・表示名・フォルダ", ScanItemShape);
        h.Add("一覧: アセットルートが無ければ空", ScanMissingRoot);
        h.Add("検索: 大小文字を区別しない部分一致・空白区切りは全部を含む", SearchTerms);
        h.Add("並び: 最近使ったもの（最近の順）→ 残り（仮想パスの順）。重複なし・無いものは出さない", OrderRecentFirst);
    }

    /// <summary>試しのアセットルートを作る。</summary>
    private static TempDir MakeAssets()
    {
        var temp = new TempDir();
        temp.WriteFile("a.actor");
        temp.WriteFile("sub/b.actor2d");
        temp.WriteFile("sub/deep/C.ACTOR");
        temp.WriteFile("画面/ホーム.actor");
        temp.WriteFile(".backup/old.actor");          // 世代バックアップ（飛ばす）
        temp.WriteFile("sub/.hidden/z.actor");        // '.' で始まるフォルダ（飛ばす）
        temp.WriteFile("x.scene");                    // ほかの拡張子
        temp.WriteFile("y.actor.bak");                // 拡張子は .bak
        temp.WriteFile("ui/readme.txt");
        return temp;
    }

    private static void ScanListsPrefabsOnly()
    {
        using var temp = MakeAssets();
        var items = PrefabPreviewCatalog.Scan(temp.Path);
        Check.Equal("assets://a.actor,assets://sub/b.actor2d,assets://sub/deep/C.ACTOR,assets://画面/ホーム.actor",
                    string.Join(",", items.Select(i => i.VirtualPath)), "列挙（仮想パスの順）");
    }

    private static void ScanItemShape()
    {
        using var temp = MakeAssets();
        var items = PrefabPreviewCatalog.Scan(temp.Path + Path.DirectorySeparatorChar);   // 末尾の区切りがあってもよい
        var deep = items.Single(i => i.DisplayName == "C.ACTOR");
        Check.Equal("assets://sub/deep/C.ACTOR", deep.VirtualPath, "仮想パス");
        Check.Equal("sub/deep", deep.Folder, "フォルダ");
        Check.True(File.Exists(deep.AbsolutePath), "絶対パス");
        var top = items.Single(i => i.DisplayName == "a.actor");
        Check.Equal("", top.Folder, "直下はフォルダ空");
        var japanese = items.Single(i => i.DisplayName == "ホーム.actor");
        Check.Equal("画面", japanese.Folder, "日本語のフォルダ");
    }

    private static void ScanMissingRoot()
    {
        Check.Equal(0, PrefabPreviewCatalog.Scan(Path.Combine(Path.GetTempPath(), "seed_no_such_dir_" + Guid.NewGuid().ToString("N"))).Count, "無いフォルダ");
        Check.Equal(0, PrefabPreviewCatalog.Scan(null).Count, "null");
        Check.Equal(0, PrefabPreviewCatalog.Scan("  ").Count, "空白");
    }

    private static void SearchTerms()
    {
        using var temp = MakeAssets();
        var items = PrefabPreviewCatalog.Scan(temp.Path);
        Check.Equal(4, PrefabPreviewCatalog.Filter(items, "").Count, "空はすべて");
        Check.Equal(4, PrefabPreviewCatalog.Filter(items, "   ").Count, "空白だけもすべて");
        Check.Equal(2, PrefabPreviewCatalog.Filter(items, "SUB").Count, "大小文字を区別しない");
        Check.Equal(1, PrefabPreviewCatalog.Filter(items, "sub deep").Count, "空白区切りは全部を含む");
        Check.Equal(1, PrefabPreviewCatalog.Filter(items, "sub　deep").Count, "全角の空白でも区切る");
        Check.Equal(1, PrefabPreviewCatalog.Filter(items, "b.actor2d").Count, "ファイル名");
        Check.Equal(1, PrefabPreviewCatalog.Filter(items, "ホーム").Count, "日本語");
        Check.Equal(0, PrefabPreviewCatalog.Filter(items, "sub zzz").Count, "1 語でも当たらなければ外れる");
    }

    private static void OrderRecentFirst()
    {
        using var temp = MakeAssets();
        var items = PrefabPreviewCatalog.Scan(temp.Path);
        var order = PrefabPreviewCatalog.Order(items, [
            "assets://sub/deep/c.actor",     // 大小文字が違っても同じもの
            "assets://missing.actor",        // 一覧に無い（消えた）ものは出さない
            "assets://a.actor",
            "ASSETS://A.ACTOR",              // 重複
        ]);
        Check.Equal("C.ACTOR,a.actor", string.Join(",", order.Recent.Select(i => i.DisplayName)), "最近（最近の順）");
        Check.Equal("assets://sub/b.actor2d,assets://画面/ホーム.actor",
                    string.Join(",", order.Others.Select(i => i.VirtualPath)), "残り（仮想パスの順）");
        Check.Equal(4, order.Count, "全件数");

        // 絞り込んだ後の並び（最近のものが絞り込みで外れたら出さない）
        var filtered = PrefabPreviewCatalog.Order(PrefabPreviewCatalog.Filter(items, "sub"), ["assets://a.actor"]);
        Check.Equal(0, filtered.Recent.Count, "絞り込みで外れた最近のものは出さない");
        Check.Equal(2, filtered.Others.Count, "残り");

        var noRecent = PrefabPreviewCatalog.Order(items, null);
        Check.Equal(0, noRecent.Recent.Count, "最近なし");
        Check.Equal(4, noRecent.Others.Count, "すべて残り");
    }
}
