using System.Text.Json;
using SEEDEditor.Preview;
using SpriteRigTests;

namespace ScreenPreviewTests;

/// <summary>
/// IPC の組み立てと応答の解釈（ScreenPreviewIpc）。ランタイムの ipc.rs / editor_preview/wire.rs と同じ書式であること。
/// </summary>
public static class IpcTests
{
    /// <summary>テストを登録する。</summary>
    public static void Register(TestHarness h)
    {
        h.Add("IPC: PREVIEW_PREFAB は世界線・親・json の順で、欄名はランタイムと同じ", BuildPreviewPrefabFull);
        h.Add("IPC: 枠なしは frame を出さず、底上げ 0 は layer_bias を出さない", BuildPreviewPrefabOmitsDefaults);
        h.Add("IPC: カンマ・日本語を含むパスも json の中で正しく往復し、行は ASCII の 1 行", BuildPreviewPrefabCommaAndJapanese);
        h.Add("IPC: パスの '\\' と '\"' は JSON としてエスケープされる", BuildPreviewPrefabEscapes);
        h.Add("IPC: 負の親・空の中身は組み立てない", BuildPreviewPrefabRejectsBadInput);
        h.Add("IPC: PREVIEW_CLEAR / CLEAR_ALL / REFRESH_PATH の形", BuildClearAndRefresh);
        h.Add("IPC: REFRESH_PATH は空・改行入りを組み立てない", BuildRefreshRejectsBadPath);
        h.Add("IPC: PREVIEW_CLEAR_ALL は PREVIEW_CLEAR の頭に当たらない", ClearAllDoesNotMatchClear);
        h.Add("IPC: PREVIEW_ADDED の解釈（正しい形・欄の数・数でない・負）", ParseAdded);
        h.Add("IPC: PREVIEW_CLEARED の解釈", ParseCleared);
        h.Add("IPC: PREVIEW_REFRESHED はカンマ入りのパスを最初のカンマで区切る", ParseRefreshed);
        h.Add("IPC: PREVIEW_ERROR の解釈", ParseError);
        h.Add("IPC: HIERARCHY_QUIET は単独の行だけ", HierarchyQuiet);
        h.Add("IPC: 差し込み先の見出し", InsertTargetLabel);
    }

    /// <summary>ランタイムと同じく先頭から 2 つだけ区切って json を取り出す（parse2u_tail の写し）。</summary>
    private static (string WorldLine, string ParentDfs, string Json) SplitLikeRuntime(string line)
    {
        Check.True(line.StartsWith(ScreenPreviewIpc.PreviewPrefabPrefix, StringComparison.Ordinal), "頭が PREVIEW_PREFAB:");
        var parts = line[ScreenPreviewIpc.PreviewPrefabPrefix.Length..].Split(',', 3);
        Check.Equal(3, parts.Length, "欄の数");
        return (parts[0], parts[1], parts[2]);
    }

    private static void BuildPreviewPrefabFull()
    {
        var line = ScreenPreviewIpc.BuildPreviewPrefab(2, 17, new ScreenPreviewRequest(
            "assets://alarms/prefabs/alarm_edit.actor", "Screens",
            "assets://ui/prefabs/screen_frame.actor", "Body", 10_000));
        var (wl, parent, json) = SplitLikeRuntime(line);
        Check.Equal("2", wl, "世界線");
        Check.Equal("17", parent, "親");

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Check.Equal("assets://alarms/prefabs/alarm_edit.actor", root.GetProperty("prefab").GetString(), "prefab");
        Check.Equal("Screens", root.GetProperty("under").GetString(), "under");
        Check.Equal("assets://ui/prefabs/screen_frame.actor", root.GetProperty("frame").GetString(), "frame");
        Check.Equal("Body", root.GetProperty("frame_body").GetString(), "frame_body");
        Check.Equal(10_000, root.GetProperty("layer_bias").GetInt32(), "layer_bias");
    }

    private static void BuildPreviewPrefabOmitsDefaults()
    {
        var line = ScreenPreviewIpc.BuildPreviewPrefab(0, 3, new ScreenPreviewRequest("assets://a.actor"));
        var (_, _, json) = SplitLikeRuntime(line);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Check.True(!root.TryGetProperty("frame", out _), "枠なしは frame を出さない");
        Check.True(!root.TryGetProperty("layer_bias", out _), "底上げ 0 は layer_bias を出さない");
        Check.Equal("", root.GetProperty("under").GetString(), "under は空（親の直下）");
        Check.Equal("", root.GetProperty("frame_body").GetString(), "frame_body は空");

        // 空白だけの枠も枠なし（ランタイムも空白だけの frame は None にそろえる）
        var blankFrame = ScreenPreviewIpc.BuildRequestJson(new ScreenPreviewRequest("assets://a.actor", Frame: "  "));
        using var doc2 = JsonDocument.Parse(blankFrame);
        Check.True(!doc2.RootElement.TryGetProperty("frame", out _), "空白だけの枠は出さない");

        // 負の底上げは出す（0 だけが「付けない」）
        var negative = ScreenPreviewIpc.BuildRequestJson(new ScreenPreviewRequest("assets://a.actor", LayerBias: -5));
        using var doc3 = JsonDocument.Parse(negative);
        Check.Equal(-5, doc3.RootElement.GetProperty("layer_bias").GetInt32(), "負の底上げ");
    }

    private static void BuildPreviewPrefabCommaAndJapanese()
    {
        const string prefab = "assets://画面/ホーム,1.actor";
        const string under  = "Card/中身,2";
        var line = ScreenPreviewIpc.BuildPreviewPrefab(0, 5, new ScreenPreviewRequest(prefab, under, "assets://枠,a.actor"));
        Check.True(line.IndexOfAny(['\r', '\n']) < 0, "改行を含まない（1 行 = 1 通）");
        Check.True(line.All(c => c < 0x80), "行は ASCII だけ（日本語は \\uXXXX。serde_json はそのまま読む）");

        var (wl, parent, json) = SplitLikeRuntime(line);
        Check.Equal("0", wl, "世界線（json のカンマで崩れない）");
        Check.Equal("5", parent, "親（json のカンマで崩れない）");
        using var doc = JsonDocument.Parse(json);
        Check.Equal(prefab, doc.RootElement.GetProperty("prefab").GetString(), "prefab の往復");
        Check.Equal(under, doc.RootElement.GetProperty("under").GetString(), "under の往復");
        Check.Equal("assets://枠,a.actor", doc.RootElement.GetProperty("frame").GetString(), "frame の往復");
    }

    private static void BuildPreviewPrefabEscapes()
    {
        const string prefab = "C:\\p\\\"q\"\\a.actor";
        var json = ScreenPreviewIpc.BuildRequestJson(new ScreenPreviewRequest(prefab));
        using var doc = JsonDocument.Parse(json);
        Check.Equal(prefab, doc.RootElement.GetProperty("prefab").GetString(), "'\\' と '\"' の往復");
    }

    private static void BuildPreviewPrefabRejectsBadInput()
    {
        Check.True(Throws(() => ScreenPreviewIpc.BuildPreviewPrefab(0, -1, new ScreenPreviewRequest("assets://a.actor"))),
                   "負の親は組み立てない");
        Check.True(Throws(() => ScreenPreviewIpc.BuildPreviewPrefab(0, 0, new ScreenPreviewRequest("   "))),
                   "空白だけの中身は組み立てない");
        Check.True(Throws(() => ScreenPreviewIpc.BuildClear(0, -3)), "負の番号は消さない");
    }

    private static void BuildClearAndRefresh()
    {
        Check.Equal("PREVIEW_CLEAR:1,23", ScreenPreviewIpc.BuildClear(1, 23), "PREVIEW_CLEAR");
        Check.Equal("PREVIEW_CLEAR_ALL:4", ScreenPreviewIpc.BuildClearAll(4), "PREVIEW_CLEAR_ALL");
        Check.Equal("PREVIEW_REFRESH_PATH:C:\\proj\\assets\\ui,1\\a.actor",
                    ScreenPreviewIpc.BuildRefreshPath("C:\\proj\\assets\\ui,1\\a.actor"), "PREVIEW_REFRESH_PATH（パスはそのまま）");
        Check.Equal("PREVIEW_REFRESH_PATH:assets://ui/画面.actor",
                    ScreenPreviewIpc.BuildRefreshPath("assets://ui/画面.actor"), "assets:// もそのまま");
    }

    private static void BuildRefreshRejectsBadPath()
    {
        Check.True(Throws(() => ScreenPreviewIpc.BuildRefreshPath("")), "空");
        Check.True(Throws(() => ScreenPreviewIpc.BuildRefreshPath("a\nb.actor")), "改行入り");
        Check.True(Throws(() => ScreenPreviewIpc.BuildRefreshPath("a\rb.actor")), "復帰入り");
    }

    private static void ClearAllDoesNotMatchClear()
    {
        var clearAll = ScreenPreviewIpc.BuildClearAll(0);
        Check.True(!clearAll.StartsWith(ScreenPreviewIpc.ClearPrefix, StringComparison.Ordinal),
                   "PREVIEW_CLEAR_ALL: は PREVIEW_CLEAR: の頭に当たらない（ランタイムの振り分けと同じ前提）");
    }

    private static void ParseAdded()
    {
        Check.True(ScreenPreviewIpc.TryParseAdded("PREVIEW_ADDED:3,45", out var wl, out var dfs), "正しい形");
        Check.Equal(3u, wl, "世界線");
        Check.Equal(45, dfs, "根");
        Check.True(!ScreenPreviewIpc.TryParseAdded("PREVIEW_ADDED:3", out _, out _), "欄が足りない");
        Check.True(!ScreenPreviewIpc.TryParseAdded("PREVIEW_ADDED:3,4,5", out _, out _), "欄が多い");
        Check.True(!ScreenPreviewIpc.TryParseAdded("PREVIEW_ADDED:x,1", out _, out _), "数でない");
        Check.True(!ScreenPreviewIpc.TryParseAdded("PREVIEW_ADDED:-1,2", out _, out _), "負の世界線");
        Check.True(!ScreenPreviewIpc.TryParseAdded("PREVIEW_ADDED:1,-2", out _, out _), "負の根");
        Check.True(!ScreenPreviewIpc.TryParseAdded("PREVIEW_CLEARED:1", out _, out _), "ほかの応答");
    }

    private static void ParseCleared()
    {
        Check.True(ScreenPreviewIpc.TryParseCleared("PREVIEW_CLEARED:0", out var zero), "0 件");
        Check.Equal(0, zero, "0 件の数");
        Check.True(ScreenPreviewIpc.TryParseCleared("PREVIEW_CLEARED:3", out var three), "3 件");
        Check.Equal(3, three, "3 件の数");
        Check.True(!ScreenPreviewIpc.TryParseCleared("PREVIEW_CLEARED:", out _), "空");
        Check.True(!ScreenPreviewIpc.TryParseCleared("PREVIEW_CLEARED:a", out _), "数でない");
        Check.True(!ScreenPreviewIpc.TryParseCleared("PREVIEW_REFRESHED:1,a", out _), "ほかの応答");
    }

    private static void ParseRefreshed()
    {
        Check.True(ScreenPreviewIpc.TryParseRefreshed("PREVIEW_REFRESHED:2,assets://ui/screens/Home,1.actor",
                                                      out var count, out var path), "カンマ入りのパス");
        Check.Equal(2, count, "数");
        Check.Equal("assets://ui/screens/Home,1.actor", path, "パスは受け取ったまま");
        Check.True(ScreenPreviewIpc.TryParseRefreshed("PREVIEW_REFRESHED:0,C:\\p\\a.actor", out var zero, out var abs), "0 件");
        Check.Equal(0, zero, "0 件の数");
        Check.Equal("C:\\p\\a.actor", abs, "絶対パス");
        Check.True(!ScreenPreviewIpc.TryParseRefreshed("PREVIEW_REFRESHED:2", out _, out _), "パスの欄が無い");
        Check.True(!ScreenPreviewIpc.TryParseRefreshed("PREVIEW_REFRESHED:x,a", out _, out _), "数でない");
    }

    private static void ParseError()
    {
        Check.True(ScreenPreviewIpc.TryParseError("PREVIEW_ERROR:プレビューの中へは動かせません", out var message), "エラー");
        Check.Equal("プレビューの中へは動かせません", message, "文言");
        Check.True(ScreenPreviewIpc.TryParseError("PREVIEW_ERROR:", out var empty), "空の文言");
        Check.Equal("", empty, "空の文言の中身");
        Check.True(!ScreenPreviewIpc.TryParseError("PREVIEW_CLEARED:1", out _), "ほかの応答");
    }

    private static void HierarchyQuiet()
    {
        Check.True(ScreenPreviewIpc.IsHierarchyQuiet("HIERARCHY_QUIET"), "単独の行");
        Check.True(!ScreenPreviewIpc.IsHierarchyQuiet("HIERARCHY_QUIET:1"), "後ろに何か付いている");
        Check.True(!ScreenPreviewIpc.IsHierarchyQuiet("HIERARCHY:[]"), "HIERARCHY");
        Check.True(!ScreenPreviewIpc.IsHierarchyQuiet("HIERARCHY_RESET"), "HIERARCHY_RESET");
    }

    private static void InsertTargetLabel()
    {
        Check.Equal("差し込み先: RootStack / Screens（枠つき）",
                    PreviewInsertTarget.DescribeLabel("RootStack", "Screens", framed: true), "枠つき");
        Check.Equal("差し込み先: Canvas", PreviewInsertTarget.DescribeLabel("Canvas", "", framed: false), "親の直下");
        var target = new PreviewInsertTarget { Under = "Dialogs", LayerBias = 3_000_000 };
        var request = target.ToRequest("assets://ui/prefabs/dialog.actor");
        Check.Equal(new ScreenPreviewRequest("assets://ui/prefabs/dialog.actor", "Dialogs", null, "", 3_000_000), request,
                    "差し込み先 → 命令の中身");
    }

    /// <summary>例外（ArgumentException）を投げるか。</summary>
    private static bool Throws(Action action)
    {
        try { action(); return false; }
        catch (ArgumentException) { return true; }
    }
}
