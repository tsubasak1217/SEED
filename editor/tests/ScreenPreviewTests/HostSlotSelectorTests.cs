using SEEDEditor.Preview;
using SpriteRigTests;

namespace ScreenPreviewTests;

/// <summary>
/// AI ツール（MCP の seed_preview の host）が「行の見出し」で差し込み先を選ぶ部分（PreviewHostSlotSelector）。
/// インスペクタの差し込み先の案内と同じ結果（PreviewHostCatalog.ResolveSlot に同じ欄の値を当てたもの）になることを見る。
/// </summary>
public static class HostSlotSelectorTests
{
    /// <summary>ScreenStack の型名（ACTOR_COMPONENTS の model_path に入る形の 1 つ）。</summary>
    private const string ScreenStackType = "SEED.UI.ScreenStack";

    /// <summary>ModalHost のファイルのパス（model_path に入るもう 1 つの形）。</summary>
    private const string ModalHostPath = "assets://scripts/UI/ModalHost.cs";

    /// <summary>テストを登録する。</summary>
    public static void Register(TestHarness h)
    {
        h.Add("行の選択: 見出しで ScreenStack の「画面」を選び、欄の値を当てる（インスペクタと同じ結果）", SelectsScreenRowWithFields);
        h.Add("行の選択: \"スクリプト名/見出し\" で絞れる・英字の大小を見逃す", SelectsByScriptAndLabel);
        h.Add("行の選択: 文字列以外の欄の値（true/false・数）も読める", ReadsNonStringFieldValues);
        h.Add("行の選択: 当たらない見出し・空の指定は理由と選べる行の一覧を返す", ReportsUnknownLabel);
        h.Add("行の選択: 表に載ったスクリプトが無い・壊れた JSON は選べる行なし", NoHostScripts);
    }

    /// <summary>ACTOR_COMPONENTS の最小の形（components の配列）を作る。</summary>
    /// <param name="components">components の要素の JSON（カンマ区切りで並べる）。</param>
    private static string Components(params string[] components) =>
        "{\"id\":3,\"name\":\"Root\",\"components\":[" + string.Join(",", components) + "]}";

    /// <summary>ScriptComponent の要素を作る。</summary>
    /// <param name="modelPath">型名・パス。</param>
    /// <param name="fieldsJson">script_fields の JSON。</param>
    private static string Script(string modelPath, string fieldsJson) =>
        "{\"slot\":1,\"type\":\"ScriptComponent\",\"model_path\":\"" + modelPath + "\",\"script_fields\":" + fieldsJson + "}";

    private static void SelectsScreenRowWithFields()
    {
        var catalog = PreviewHostCatalog.BuiltIn();
        var json = Components(
            "{\"slot\":0,\"type\":\"CanvasComponent\"}",
            Script(ScreenStackType, "{\"ScreensChild\":\"Body/Screens\",\"FramePrefab\":\"assets://x/frame.actor\",\"LayerStep\":\"500\"}"));

        var selection = PreviewHostSlotSelector.Select(catalog, json, "画面");
        Check.True(selection.Error is null, $"選べなかった: {selection.Error}");
        Check.Equal("ScreenStack", selection.HostShortName, "行を持つスクリプト");

        // インスペクタと同じ結果（同じ定義に同じ欄の値を当てたもの）
        var host     = catalog.FindHost(ScreenStackType)!;
        var expected = PreviewHostCatalog.ResolveSlot(host.Slots.First(s => s.Label == "画面"),
            new Dictionary<string, string>
            {
                ["ScreensChild"] = "Body/Screens",
                ["FramePrefab"]  = "assets://x/frame.actor",
                ["LayerStep"]    = "500",
            });
        Check.Equal(expected, selection.Slot, "当てた結果");
        Check.Equal("Body/Screens", selection.Slot!.Under, "差し込む子は欄の値");
        Check.Equal(500, selection.Slot.LayerBias, "底上げは欄の値");
        Check.True(selection.Available.Contains("ScreenStack/根の画面") && selection.Available.Contains("ScreenStack/画面"),
                   "選べる行の一覧");
    }

    private static void SelectsByScriptAndLabel()
    {
        var catalog = PreviewHostCatalog.BuiltIn();
        var json = Components(
            Script(ScreenStackType, "{}"),
            Script(ModalHostPath, "{\"DialogsChild\":\"Layer/Dialogs\"}"));

        var byScript = PreviewHostSlotSelector.Select(catalog, json, "modalhost/ダイアログ");
        Check.True(byScript.Error is null, $"選べなかった: {byScript.Error}");
        Check.Equal("ModalHost", byScript.HostShortName, "スクリプト名で絞った行");
        Check.Equal("Layer/Dialogs", byScript.Slot!.Under, "ModalHost の欄の値");
        Check.Equal("assets://ui/prefabs/dialog.actor", byScript.Slot.Prefab, "既定の面");

        // 別のスクリプトの名前で絞ると、その行は無いので選べない
        var wrong = PreviewHostSlotSelector.Select(catalog, json, "ScreenStack/ダイアログ");
        Check.True(wrong.Slot is null && wrong.Error is not null, "ScreenStack にダイアログの行は無い");
    }

    private static void ReadsNonStringFieldValues()
    {
        var catalog = PreviewHostCatalog.BuiltIn();
        // RootSafeArea=false（真偽値のまま）なら枠の直下（frame_body が空）、LayerStep は数のまま
        var json = Components(Script(ScreenStackType,
            "{\"RootPrefab\":\"assets://a/root.actor\",\"RootSafeArea\":false,\"LayerStep\":250}"));

        var selection = PreviewHostSlotSelector.Select(catalog, json, "根の画面");
        Check.True(selection.Error is null, $"選べなかった: {selection.Error}");
        Check.Equal("assets://a/root.actor", selection.Slot!.Prefab, "中身は欄の値");
        Check.Equal("", selection.Slot.FrameBody, "安全領域の外（枠の直下）");
        Check.Equal(250, selection.Slot.LayerBias, "数のままの底上げ");
        Check.True(!selection.Slot.CanPick, "根の画面は選べない行");
    }

    private static void ReportsUnknownLabel()
    {
        var catalog = PreviewHostCatalog.BuiltIn();
        var json = Components(Script(ScreenStackType, "{}"));

        var unknown = PreviewHostSlotSelector.Select(catalog, json, "ダイアログ");
        Check.True(unknown.Slot is null, "ScreenStack にダイアログの行は無い");
        Check.True(unknown.Error!.Contains("ScreenStack/画面"), $"理由に選べる行が入っている: {unknown.Error}");
        Check.Equal(2, unknown.Available.Count, "選べる行の数（根の画面・画面）");

        var empty = PreviewHostSlotSelector.Select(catalog, json, "  ");
        Check.True(empty.Slot is null && empty.Error is not null, "空の指定は選べない");
    }

    private static void NoHostScripts()
    {
        var catalog = PreviewHostCatalog.BuiltIn();
        var plain = PreviewHostSlotSelector.Select(catalog, Components(Script("SEED.Game.Player", "{}")), "画面");
        Check.True(plain.Slot is null && plain.Available.Count == 0, "表に無いスクリプトだけなら選べる行なし");
        Check.True(plain.Error!.Contains("screen_preview_hosts.json"), $"表の場所を案内する: {plain.Error}");

        var broken = PreviewHostSlotSelector.Select(catalog, "{ これは JSON ではない", "画面");
        Check.True(broken.Slot is null && broken.Available.Count == 0, "壊れた JSON は選べる行なし");
    }
}
