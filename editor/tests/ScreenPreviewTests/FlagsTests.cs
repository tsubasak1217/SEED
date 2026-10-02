using System.Text.Json;
using SEEDEditor.Preview;
using SpriteRigTests;

namespace ScreenPreviewTests;

/// <summary>
/// ヒエラルキーの印（HierarchyPreviewFlags）とインスペクタの editor_preview（InspectorPreviewInfo）の読み取り。
/// </summary>
public static class FlagsTests
{
    /// <summary>テストを登録する。</summary>
    public static void Register(TestHarness h)
    {
        h.Add("ヒエラルキー: 旧 JSON（欄なし）はプレビューの外", HierarchyOldJson);
        h.Add("ヒエラルキー: 根・中・外の印", HierarchyRootInnerOuter);
        h.Add("ヒエラルキー: 型の違う欄は外として読み、根は必ず部分木の中", HierarchyDefensive);
        h.Add("インスペクタ: 欄なし・null はプレビューの外", InspectorOutside);
        h.Add("インスペクタ: 根・中（枠あり / 枠なし）", InspectorRootAndInner);
        h.Add("インスペクタ: 根の番号が読めなければ外として扱う", InspectorBadRootDfs);
    }

    /// <summary>JSON の 1 オブジェクトを読む（呼び出し側で Dispose する文書ごと返す）。</summary>
    private static JsonDocument Parse(string json) => JsonDocument.Parse(json);

    private static void HierarchyOldJson()
    {
        using var doc = Parse("""{"id":3,"name":"A","parent":null,"is_group":false}""");
        Check.Equal(HierarchyPreviewFlags.None, HierarchyPreviewFlags.Read(doc.RootElement), "旧 JSON");
    }

    private static void HierarchyRootInnerOuter()
    {
        using var root = Parse("""{"id":5,"preview":true,"preview_root":true,"preview_source":"assets://alarms/prefabs/alarm_edit.actor"}""");
        Check.Equal(new HierarchyPreviewFlags(true, true, "assets://alarms/prefabs/alarm_edit.actor"),
                    HierarchyPreviewFlags.Read(root.RootElement), "根");

        using var inner = Parse("""{"id":6,"preview":true,"preview_root":false,"preview_source":null}""");
        Check.Equal(new HierarchyPreviewFlags(true, false, null), HierarchyPreviewFlags.Read(inner.RootElement), "中");

        using var outer = Parse("""{"id":1,"preview":false,"preview_root":false,"preview_source":null}""");
        Check.Equal(HierarchyPreviewFlags.None, HierarchyPreviewFlags.Read(outer.RootElement), "外");
    }

    private static void HierarchyDefensive()
    {
        using var strings = Parse("""{"preview":"yes","preview_root":1,"preview_source":5}""");
        Check.Equal(HierarchyPreviewFlags.None, HierarchyPreviewFlags.Read(strings.RootElement), "型の違う欄は外");

        using var rootOnly = Parse("""{"preview":false,"preview_root":true,"preview_source":"assets://a.actor"}""");
        var flags = HierarchyPreviewFlags.Read(rootOnly.RootElement);
        Check.True(flags.IsPreview && flags.IsPreviewRoot, "根なら部分木の中として扱う");

        using var innerWithSource = Parse("""{"preview":true,"preview_root":false,"preview_source":"assets://a.actor"}""");
        Check.True(HierarchyPreviewFlags.Read(innerWithSource.RootElement).PreviewSource is null, "根以外の preview_source は使わない");

        using var array = Parse("[]");
        Check.Equal(HierarchyPreviewFlags.None, HierarchyPreviewFlags.Read(array.RootElement), "オブジェクトでない");
    }

    private static void InspectorOutside()
    {
        using var noField = Parse("""{"id":3,"name":"A","components":[]}""");
        Check.True(InspectorPreviewInfo.Read(noField.RootElement) is null, "欄なし（旧ランタイム）");
        using var nullField = Parse("""{"id":3,"editor_preview":null}""");
        Check.True(InspectorPreviewInfo.Read(nullField.RootElement) is null, "null（プレビューの外）");
        using var stringField = Parse("""{"id":3,"editor_preview":"x"}""");
        Check.True(InspectorPreviewInfo.Read(stringField.RootElement) is null, "オブジェクトでない");
    }

    private static void InspectorRootAndInner()
    {
        using var root = Parse("""{"id":12,"editor_preview":{"root_dfs":12,"is_root":true,"prefab":"assets://ui/prefabs/dialog.actor","frame":null}}""");
        Check.Equal(new InspectorPreviewInfo(12, true, "assets://ui/prefabs/dialog.actor", null),
                    InspectorPreviewInfo.Read(root.RootElement), "根（枠なし）");

        using var inner = Parse("""{"id":14,"editor_preview":{"root_dfs":12,"is_root":false,"prefab":"assets://a.actor","frame":"assets://ui/prefabs/screen_frame.actor"}}""");
        Check.Equal(new InspectorPreviewInfo(12, false, "assets://a.actor", "assets://ui/prefabs/screen_frame.actor"),
                    InspectorPreviewInfo.Read(inner.RootElement), "中（枠あり）");

        using var noFrameField = Parse("""{"editor_preview":{"root_dfs":0,"prefab":"C:\\p\\a.actor"}}""");
        Check.Equal(new InspectorPreviewInfo(0, false, "C:\\p\\a.actor", null),
                    InspectorPreviewInfo.Read(noFrameField.RootElement), "is_root・frame の欄なし");
    }

    private static void InspectorBadRootDfs()
    {
        using var stringDfs = Parse("""{"editor_preview":{"root_dfs":"12","is_root":true,"prefab":"a"}}""");
        Check.True(InspectorPreviewInfo.Read(stringDfs.RootElement) is null, "文字列の番号");
        using var negative = Parse("""{"editor_preview":{"root_dfs":-1,"is_root":true,"prefab":"a"}}""");
        Check.True(InspectorPreviewInfo.Read(negative.RootElement) is null, "負の番号");
        using var missing = Parse("""{"editor_preview":{"is_root":true,"prefab":"a"}}""");
        Check.True(InspectorPreviewInfo.Read(missing.RootElement) is null, "番号なし");
    }
}
