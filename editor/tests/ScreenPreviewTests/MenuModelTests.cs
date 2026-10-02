using SEEDEditor.Preview;
using SpriteRigTests;

namespace ScreenPreviewTests;

/// <summary>
/// ヒエラルキーの右クリックの項目（PreviewMenuModel）。
/// </summary>
public static class MenuModelTests
{
    /// <summary>テストを登録する。</summary>
    public static void Register(TestHarness h)
    {
        h.Add("右クリック: 普通のノード（最近なし・プレビューなし）は「プレハブをプレビュー」→「プロジェクトから選ぶ...」だけ", NormalNodeMinimal);
        h.Add("右クリック: 普通のノード（最近あり・プレビューあり）は最近 → 区切り → 選ぶ...、と「すべてのプレビューを消す」", NormalNodeWithRecentAndPreview);
        h.Add("右クリック: プレビューの根はメニューを丸ごとプレビューの項目にする", PreviewRootReplacesMenu);
        h.Add("右クリック: プレビューの中（元のプレハブが分からない）は「元のプレハブを開く」を押せない", PreviewInnerWithoutSource);
        h.Add("右クリック: 空白はプレビューがあるときだけ「すべてのプレビューを消す」", BlankArea);
        h.Add("右クリック: Play 中はプレビューの項目をすべて押せない", NotEditDisablesAll);
        h.Add("右クリック: 見出しのファイル名", DisplayNames);
    }

    private static void NormalNodeMinimal()
    {
        var menu = PreviewMenuModel.Build(new PreviewMenuInput { HasNode = true });
        Check.Equal(PreviewMenuPlacement.Append, menu.Placement, "既存のメニューへ足す");
        Check.Equal(1, menu.Entries.Count, "項目の数");
        var sub = menu.Entries[0];
        Check.Equal(PreviewMenuEntryKind.SubMenu, sub.Kind, "サブメニュー");
        Check.Equal(PreviewMenuModel.PreviewPrefabHeader, sub.Header, "見出し");
        Check.Equal(1, sub.Children.Count, "子は選ぶ...だけ（区切りを出さない）");
        Check.Equal(PreviewMenuCommand.PickPrefab, sub.Children[0].Command, "選ぶ...");
        Check.Equal(PreviewMenuModel.PickPrefabHeader, sub.Children[0].Header, "選ぶ...の見出し");
        Check.True(AllEnabled(menu.Entries), "Edit ではすべて押せる");
    }

    private static void NormalNodeWithRecentAndPreview()
    {
        var menu = PreviewMenuModel.Build(new PreviewMenuInput
        {
            HasNode        = true,
            TreeHasPreview = true,
            RecentPrefabs  = ["assets://alarms/prefabs/alarm_edit.actor", "  ", "assets://ui/prefabs/dialog.actor2d"],
        });
        Check.Equal(PreviewMenuPlacement.Append, menu.Placement, "既存のメニューへ足す");
        Check.Equal("SubMenu,Item", Kinds(menu.Entries), "サブメニュー＋すべてのプレビューを消す");
        var children = menu.Entries[0].Children;
        Check.Equal("Item,Item,Separator,Item", Kinds(children), "最近 2 件（空白は飛ばす）→ 区切り → 選ぶ...");
        Check.Equal("alarm_edit.actor", children[0].Header, "見出しはファイル名");
        Check.Equal("assets://alarms/prefabs/alarm_edit.actor", children[0].ToolTip, "ツールチップは仮想パス");
        Check.Equal(PreviewMenuCommand.PreviewRecent, children[0].Command, "最近のものをプレビュー");
        Check.Equal("assets://alarms/prefabs/alarm_edit.actor", children[0].Argument, "引数は仮想パス");
        Check.Equal("dialog.actor2d", children[1].Header, "2 件目");
        Check.Equal(PreviewMenuCommand.PickPrefab, children[3].Command, "最後は選ぶ...");
        Check.Equal(PreviewMenuCommand.ClearAll, menu.Entries[1].Command, "すべてのプレビューを消す");
    }

    private static void PreviewRootReplacesMenu()
    {
        var menu = PreviewMenuModel.Build(new PreviewMenuInput
        {
            HasNode           = true,
            NodeInPreview     = true,
            NodeIsPreviewRoot = true,
            NearestRootSource = "assets://alarms/prefabs/alarm_edit.actor",
            TreeHasPreview    = true,
            RecentPrefabs     = ["assets://alarms/prefabs/alarm_edit_body.actor"],
        });
        Check.Equal(PreviewMenuPlacement.Replace, menu.Placement, "メニューを丸ごと置き換える");
        Check.Equal("SubMenu,Separator,Item,Item,Item", Kinds(menu.Entries), "並び");
        Check.Equal(PreviewMenuModel.PreviewPrefabHeader, menu.Entries[0].Header, "入れ子のプレビューは可");
        Check.Equal(PreviewMenuCommand.OpenSource, menu.Entries[2].Command, "元のプレハブを開く");
        Check.Equal("assets://alarms/prefabs/alarm_edit.actor", menu.Entries[2].Argument, "開くのは根の中身");
        Check.True(menu.Entries[2].IsEnabled, "中身が分かるので押せる");
        Check.Equal(PreviewMenuCommand.ClearThis, menu.Entries[3].Command, "プレビューを消す");
        Check.Equal(PreviewMenuCommand.ClearAll, menu.Entries[4].Command, "すべてのプレビューを消す");

        // 既存の追加・コピー・削除などに当たるコマンドは無い（コマンドの種類はプレビューのものだけ）
        var commands = Flatten(menu.Entries).Select(e => e.Command).Distinct().OrderBy(c => c).ToList();
        Check.Equal("None,PickPrefab,PreviewRecent,ClearThis,ClearAll,OpenSource", string.Join(",", commands), "コマンドの種類");
    }

    private static void PreviewInnerWithoutSource()
    {
        var menu = PreviewMenuModel.Build(new PreviewMenuInput
        {
            HasNode        = true,
            NodeInPreview  = true,
            TreeHasPreview = true,
        });
        Check.Equal(PreviewMenuPlacement.Replace, menu.Placement, "中のノードも置き換える");
        var open = menu.Entries.Single(e => e.Command == PreviewMenuCommand.OpenSource);
        Check.True(!open.IsEnabled, "元のプレハブが分からなければ押せない");
        Check.Equal(PreviewMenuModel.NoSourceToolTip, open.ToolTip, "理由");
        Check.True(open.Argument is null, "引数なし");
        Check.True(menu.Entries.Single(e => e.Command == PreviewMenuCommand.ClearThis).IsEnabled, "プレビューを消すは押せる");
    }

    private static void BlankArea()
    {
        var with = PreviewMenuModel.Build(new PreviewMenuInput { HasNode = false, TreeHasPreview = true });
        Check.Equal(PreviewMenuPlacement.Append, with.Placement, "プレビューがあれば足す");
        Check.Equal(1, with.Entries.Count, "1 項目");
        Check.Equal(PreviewMenuCommand.ClearAll, with.Entries[0].Command, "すべてのプレビューを消す");

        var without = PreviewMenuModel.Build(new PreviewMenuInput { HasNode = false, TreeHasPreview = false, RecentPrefabs = ["assets://a.actor"] });
        Check.Equal(PreviewMenuPlacement.None, without.Placement, "プレビューが無ければ何も足さない");
        Check.Equal(0, without.Entries.Count, "空");
    }

    private static void NotEditDisablesAll()
    {
        var inputs = new[]
        {
            new PreviewMenuInput { HasNode = true, TreeHasPreview = true, IsEditMode = false, RecentPrefabs = ["assets://a.actor"] },
            new PreviewMenuInput { HasNode = true, NodeInPreview = true, NodeIsPreviewRoot = true, NearestRootSource = "assets://a.actor",
                                   TreeHasPreview = true, IsEditMode = false },
            new PreviewMenuInput { HasNode = false, TreeHasPreview = true, IsEditMode = false },
        };
        foreach (var input in inputs)
        {
            var menu = PreviewMenuModel.Build(input);
            foreach (var entry in Flatten(menu.Entries).Where(e => e.Kind != PreviewMenuEntryKind.Separator))
            {
                Check.True(!entry.IsEnabled, $"Play 中は押せない: {entry.Header}");
                Check.Equal(PreviewMenuModel.NotEditToolTip, entry.ToolTip, $"理由: {entry.Header}");
            }
        }
    }

    private static void DisplayNames()
    {
        Check.Equal("a.actor", PreviewMenuModel.DisplayName("assets://ui/a.actor"), "仮想パス");
        Check.Equal("b.actor2d", PreviewMenuModel.DisplayName("C:\\p\\b.actor2d"), "絶対パス");
        Check.Equal("c.actor", PreviewMenuModel.DisplayName("c.actor"), "ファイル名だけ");
    }

    /// <summary>項目の種類を並べる。</summary>
    private static string Kinds(IEnumerable<PreviewMenuEntry> entries) => string.Join(",", entries.Select(e => e.Kind));

    /// <summary>子を含めて平らにする。</summary>
    private static IEnumerable<PreviewMenuEntry> Flatten(IEnumerable<PreviewMenuEntry> entries) =>
        entries.SelectMany(e => new[] { e }.Concat(Flatten(e.Children)));

    /// <summary>区切り以外がすべて押せるか。</summary>
    private static bool AllEnabled(IEnumerable<PreviewMenuEntry> entries) =>
        Flatten(entries).Where(e => e.Kind != PreviewMenuEntryKind.Separator).All(e => e.IsEnabled);
}
