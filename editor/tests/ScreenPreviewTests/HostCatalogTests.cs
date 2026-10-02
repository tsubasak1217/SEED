using System.Globalization;
using System.Text.RegularExpressions;
using SEEDEditor.Preview;
using SpriteRigTests;

namespace ScreenPreviewTests;

/// <summary>
/// 差し込み先の案内の表（PreviewHostCatalog と editor/config/screen_preview_hosts.json）。
/// </summary>
public static class HostCatalogTests
{
    /// <summary>同梱の表（リポジトリの根からの相対パス）。</summary>
    private const string ShippedTablePath = "editor/config/screen_preview_hosts.json";

    /// <summary>ScreenStack のソース。</summary>
    private const string ScreenStackSource = "scripting/src/Api/UI/Navigation/ScreenStack.cs";

    /// <summary>ModalHost のソース。</summary>
    private const string ModalHostSource = "scripting/src/Api/UI/Navigation/ModalHost.cs";

    /// <summary>UiLayers のソース。</summary>
    private const string UiLayersSource = "scripting/src/Api/UI/Navigation/Model/UiLayers.cs";

    /// <summary>テストを登録する。</summary>
    public static void Register(TestHarness h)
    {
        h.Add("表: 同梱の JSON が警告なしで読め、組み込みの表と同じ", ShippedTableEqualsBuiltIn);
        h.Add("表: 2026-10-02 の登録（ScreenStack・ModalHost・PopupPlane）", BuiltInRegistrations);
        h.Add("表: FindHost は完全名・ファイルのパス・大小文字を見分けず、当たらない名前は null", FindHostVariants);
        h.Add("表: ScreenStack の根の画面（欄の値・RootSafeArea=false で枠の直下・LayerStep の上書き）", ResolveRootScreenWithFields);
        h.Add("表: ScreenStack の根の画面（欄なしは既定・中身なし）", ResolveRootScreenDefaults);
        h.Add("表: RootSafeArea の読み方（大小文字無視・読めなければ既定）", ResolveSafeAreaParsing);
        h.Add("表: LayerStep が 0・負・読めないときは既定", ResolveLayerStepFallback);
        h.Add("表: 差し込む子・枠の欄の上書き", ResolveUnderAndFrameOverride);
        h.Add("表: ModalHost の帯（既定の面・固定の底上げ・選ぶ...）", ResolveModalHost);
        h.Add("表: PopupPlane の中身（Card/Content・底上げなし・選ぶ...）", ResolvePopupPlane);
        h.Add("表: 無い・壊れた・未来の版・構成フォルダなしは警告して組み込みの表", LoadFallbacks);
        h.Add("表: コメント・末尾カンマを許し、書き損じた項目だけ飛ばす", LoadLenientAndSkipsBadHosts);
        h.Add("表: 既定値がスクリプト（ScreenStack.cs・ModalHost.cs・UiLayers.cs）とずれていない", DefaultsMatchScriptSources);
    }

    // ============================================================
    //  同梱の表 = 組み込みの表
    // ============================================================

    private static void ShippedTableEqualsBuiltIn()
    {
        var warnings = new List<string>();
        var shipped  = PreviewHostCatalog.LoadFile(RepoFiles.Find(ShippedTablePath), warnings.Add);
        Check.Equal(0, warnings.Count, $"同梱の表の警告（{string.Join(" / ", warnings)}）");
        Check.True(shipped.SourcePath is not null, "同梱の表を読んだ（組み込みへ落ちていない）");
        AssertSameCatalog(PreviewHostCatalog.BuiltIn(), shipped);
    }

    private static void BuiltInRegistrations()
    {
        var catalog = PreviewHostCatalog.BuiltIn();
        Check.Equal(PreviewHostCatalog.BuiltInDefaultLayerBias, catalog.DefaultLayerBias, "default_layer_bias");
        Check.Equal(10_000, catalog.DefaultLayerBias, "default_layer_bias の値");
        Check.Equal("ScreenStack,ModalHost,PopupPlane", string.Join(",", catalog.Hosts.Select(h => h.ShortName)), "登録の並び");
        Check.Equal("根の画面,画面", Labels(catalog, "ScreenStack"), "ScreenStack の行");
        Check.Equal("ダイアログ,シート,覆い", Labels(catalog, "ModalHost"), "ModalHost の行");
        Check.Equal("中身", Labels(catalog, "PopupPlane"), "PopupPlane の行");
        Check.Equal("画面のスタック", catalog.FindHost("ScreenStack")!.Label, "ScreenStack の見出し");
        Check.Equal("重ねる面の受け皿", catalog.FindHost("ModalHost")!.Label, "ModalHost の見出し");
        Check.Equal("中央のポップアップ（Wake or Pay）", catalog.FindHost("PopupPlane")!.Label, "PopupPlane の見出し");
    }

    private static void FindHostVariants()
    {
        var catalog = PreviewHostCatalog.BuiltIn();
        Check.Equal("ScreenStack", catalog.FindHost("SEED.UI.ScreenStack")?.ShortName, "完全名");
        Check.Equal("PopupPlane", catalog.FindHost("assets://scripts/App/Screens/Common/PopupPlane.cs")?.ShortName, "assets:// のパス");
        Check.Equal("ModalHost", catalog.FindHost("seed.ui.modalhost")?.ShortName, "小文字の完全名");
        Check.Equal("ModalHost", catalog.FindHost("C:\\proj\\assets\\scripts\\ModalHost.CS")?.ShortName, "絶対パス・大文字の拡張子");
        Check.Equal("PopupPlane", catalog.FindHost("PopupPlane.cs")?.ShortName, "ファイル名だけ");
        Check.True(catalog.FindHost("SEED.UI.Button") is null, "表に無いスクリプト");
        Check.True(catalog.FindHost("SEED.UI.ScreenStackX") is null, "前方一致は当てない");
        Check.True(catalog.FindHost("") is null, "空");
        Check.True(catalog.FindHost(null) is null, "null");
        Check.Equal("ScreenStack", PreviewHostCatalog.ShortClassName("  SEED.UI.ScreenStack  "), "前後の空白");
    }

    // ============================================================
    //  欄の値の当てはめ
    // ============================================================

    private static void ResolveRootScreenWithFields()
    {
        // Wake or Pay の App.scene の根のスタック（RootSafeArea=false・タブの中は LayerStep=1000）と同じ値
        var resolved = Resolve("ScreenStack", "根の画面", new()
        {
            ["RootPrefab"]   = "assets://app/prefabs/shell.actor",
            ["RootSafeArea"] = "false",
            ["LayerStep"]    = "1000",
        });
        Check.Equal("Screens", resolved.Under, "差し込む子（既定）");
        Check.Equal("assets://app/prefabs/shell.actor", resolved.Prefab, "中身は RootPrefab の値");
        Check.Equal("assets://ui/prefabs/screen_frame.actor", resolved.Frame, "枠（既定）");
        Check.Equal("", resolved.FrameBody, "RootSafeArea=false は枠の直下");
        Check.Equal(1000, resolved.LayerBias, "LayerStep の上書き");
        Check.True(!resolved.CanPick, "根の画面は選ばない");
    }

    private static void ResolveRootScreenDefaults()
    {
        var resolved = Resolve("ScreenStack", "根の画面", new());
        Check.Equal("Screens", resolved.Under, "差し込む子");
        Check.True(resolved.Prefab is null, "RootPrefab が無ければ既定の中身は無い（ボタンを出さない）");
        Check.Equal("Body", resolved.FrameBody, "RootSafeArea の既定 true は枠の Body");
        Check.Equal(10_000, resolved.LayerBias, "LayerStep の既定");

        var screen = Resolve("ScreenStack", "画面", new());
        Check.True(screen.Prefab is null, "画面は中身を選ぶ");
        Check.True(screen.CanPick, "画面は選ぶ...を出す");
        Check.Equal("Body", screen.FrameBody, "画面は枠の Body（ScreenOptions.SafeArea の既定）");
        Check.Equal("assets://ui/prefabs/screen_frame.actor", screen.Frame, "画面の枠");
        // 画面の行は RootSafeArea を見ない（Push の既定 = 安全領域の中）
        var screenIgnoresRoot = Resolve("ScreenStack", "画面", new() { ["RootSafeArea"] = "false" });
        Check.Equal("Body", screenIgnoresRoot.FrameBody, "画面の行は RootSafeArea に左右されない");
    }

    private static void ResolveSafeAreaParsing()
    {
        Check.Equal("", Resolve("ScreenStack", "根の画面", new() { ["RootSafeArea"] = "FALSE" }).FrameBody, "FALSE");
        Check.Equal("Body", Resolve("ScreenStack", "根の画面", new() { ["RootSafeArea"] = "True" }).FrameBody, "True");
        Check.Equal("Body", Resolve("ScreenStack", "根の画面", new() { ["RootSafeArea"] = "yes" }).FrameBody, "読めない値は既定（true）");
        Check.Equal("Body", Resolve("ScreenStack", "根の画面", new() { ["RootSafeArea"] = "" }).FrameBody, "空は既定（true）");
    }

    private static void ResolveLayerStepFallback()
    {
        Check.Equal(10_000, Resolve("ScreenStack", "画面", new() { ["LayerStep"] = "0" }).LayerBias, "0 はテーマの値＝既定");
        Check.Equal(10_000, Resolve("ScreenStack", "画面", new() { ["LayerStep"] = "-5" }).LayerBias, "負は既定");
        Check.Equal(10_000, Resolve("ScreenStack", "画面", new() { ["LayerStep"] = "abc" }).LayerBias, "読めなければ既定");
        Check.Equal(2500, Resolve("ScreenStack", "画面", new() { ["LayerStep"] = " 2500 " }).LayerBias, "前後の空白は許す");
    }

    private static void ResolveUnderAndFrameOverride()
    {
        var resolved = Resolve("ScreenStack", "画面", new()
        {
            ["ScreensChild"] = "Stack/Screens",
            ["FramePrefab"]  = "assets://app/prefabs/my_frame.actor",
        });
        Check.Equal("Stack/Screens", resolved.Under, "ScreensChild の上書き");
        Check.Equal("assets://app/prefabs/my_frame.actor", resolved.Frame, "FramePrefab の上書き");
        Check.Equal("Screens", Resolve("ScreenStack", "画面", new() { ["ScreensChild"] = "  " }).Under, "空白だけの値は既定");
    }

    private static void ResolveModalHost()
    {
        var dialog = Resolve("ModalHost", "ダイアログ", new());
        Check.Equal("Dialogs", dialog.Under, "ダイアログの帯");
        Check.Equal("assets://ui/prefabs/dialog.actor", dialog.Prefab, "ダイアログの既定の面");
        Check.True(dialog.Frame is null, "枠なし");
        Check.Equal("", dialog.FrameBody, "枠なしは枠の中の差し込み先も空");
        Check.Equal(3_000_000, dialog.LayerBias, "ダイアログの帯の底上げ");
        Check.True(dialog.CanPick, "選ぶ...");

        var custom = Resolve("ModalHost", "ダイアログ", new() { ["DialogPrefab"] = "assets://app/prefabs/my_dialog.actor", ["DialogsChild"] = "Top" });
        Check.Equal("assets://app/prefabs/my_dialog.actor", custom.Prefab, "DialogPrefab の上書き");
        Check.Equal("Top", custom.Under, "DialogsChild の上書き");

        var sheet = Resolve("ModalHost", "シート", new());
        Check.Equal("Sheets", sheet.Under, "シートの帯");
        Check.Equal("assets://ui/prefabs/bottom_sheet.actor", sheet.Prefab, "シートの既定の面");
        Check.Equal(2_000_000, sheet.LayerBias, "シートの帯の底上げ");

        var overlay = Resolve("ModalHost", "覆い", new());
        Check.Equal("Overlays", overlay.Under, "覆いの帯");
        Check.Equal("assets://ui/prefabs/top_sheet.actor", overlay.Prefab, "覆いの既定の面");
        Check.Equal(1_000_000, overlay.LayerBias, "覆いの帯の底上げ");
    }

    private static void ResolvePopupPlane()
    {
        var content = Resolve("PopupPlane", "中身", null);
        Check.Equal("Card/Content", content.Under, "中身の置き場");
        Check.True(content.Prefab is null, "中身は選ぶ");
        Check.True(content.Frame is null, "枠なし");
        Check.Equal(0, content.LayerBias, "底上げなし");
        Check.True(content.CanPick, "選ぶ...");
    }

    // ============================================================
    //  読み込みの失敗
    // ============================================================

    private static void LoadFallbacks()
    {
        using var temp = new TempDir();

        AssertFallback(Path.Combine(temp.Path, "missing.json"), "ファイルが無い");
        AssertFallback(temp.WriteFile("broken.json", "{ \"format_version\": 1, \"hosts\": [ "), "壊れた JSON");
        AssertFallback(temp.WriteFile("future.json", "{ \"format_version\": 2, \"hosts\": [] }"), "未来の版");
        AssertFallback(temp.WriteFile("no_version.json", "{ \"hosts\": [] }"), "版が無い");
        AssertFallback(temp.WriteFile("no_hosts.json", "{ \"format_version\": 1 }"), "hosts が無い");

        var warnings = new List<string>();
        var noDir = PreviewHostCatalog.Load(null, warnings.Add);
        Check.True(noDir.SourcePath is null && warnings.Count == 1, "構成フォルダなしは組み込みの表");
        Check.Equal(3, noDir.Hosts.Count, "組み込みの表の件数");

        // 構成フォルダの下のファイル名で読む
        temp.WriteFile(PreviewHostCatalog.FileName, "{ \"format_version\": 1, \"hosts\": [ { \"script\": \"Foo\", \"slots\": [] } ] }");
        var fromDir = PreviewHostCatalog.Load(temp.Path);
        Check.Equal("Foo", string.Join(",", fromDir.Hosts.Select(h => h.ShortName)), "構成フォルダの下の表を読む");
        Check.Equal(PreviewHostCatalog.BuiltInDefaultLayerBias, fromDir.DefaultLayerBias, "default_layer_bias が無ければ組み込みの既定");
    }

    private static void LoadLenientAndSkipsBadHosts()
    {
        using var temp = new TempDir();
        var path = temp.WriteFile("lenient.json", """
            // 先頭のコメント
            {
              "format_version": 1,
              "default_layer_bias": 500,
              "hosts": [
                { "script": "SEED.UI.Thing", "label": " 物 ", "slots": [
                    { "label": "行", "under_default": " A/B ", "prefab_default": "  ", "pick": true, }, // 末尾カンマ
                ], },
                { "label": "script が無い", "slots": [] },
                { "script": "thing", "label": "重なり", "slots": [] },
              ],
            }
            """);
        var warnings = new List<string>();
        var catalog  = PreviewHostCatalog.LoadFile(path, warnings.Add);
        Check.True(catalog.SourcePath is not null, "組み込みへ落ちない");
        Check.Equal(500, catalog.DefaultLayerBias, "default_layer_bias");
        Check.Equal("Thing", string.Join(",", catalog.Hosts.Select(h => h.ShortName)), "script の無い・重なった項目は飛ばす");
        Check.Equal(2, warnings.Count, "飛ばした件の警告");
        var host = catalog.FindHost("Thing")!;
        Check.Equal("物", host.Label, "見出しの前後の空白を落とす");
        var resolved = PreviewHostCatalog.ResolveSlot(host.Slots[0], null);
        Check.Equal("A/B", resolved.Under, "差し込む子の前後の空白を落とす");
        Check.True(resolved.Prefab is null, "空白だけの既定の中身は無し");
        Check.True(resolved.CanPick, "pick");
    }

    // ============================================================
    //  スクリプトとのずれ検知
    // ============================================================

    private static void DefaultsMatchScriptSources()
    {
        var screenStack = File.ReadAllText(RepoFiles.Find(ScreenStackSource));
        var modalHost   = File.ReadAllText(RepoFiles.Find(ModalHostSource));
        var uiLayers    = File.ReadAllText(RepoFiles.Find(UiLayersSource));
        var catalog     = PreviewHostCatalog.BuiltIn();

        // ── ScreenStack ──
        var stack = catalog.FindHost("ScreenStack")!;
        var rootSlot   = Slot(stack, "根の画面");
        var screenSlot = Slot(stack, "画面");
        foreach (var slot in new[] { rootSlot, screenSlot })
        {
            Check.Equal("ScreensChild", slot.UnderField, $"{slot.Label}: 差し込む子の欄");
            Check.Equal(ConstString(screenStack, "ScreensChild"), slot.UnderDefault, $"{slot.Label}: ScreensChild の既定");
            Check.Equal("FramePrefab", slot.FrameField, $"{slot.Label}: 枠の欄");
            Check.Equal(ConstString(screenStack, "DefaultFramePrefab"), slot.FrameDefault, $"{slot.Label}: DefaultFramePrefab");
            Check.Equal(ConstString(screenStack, "BodyChild"), slot.FrameBody, $"{slot.Label}: 枠の Body");
            Check.Equal("LayerStep", slot.LayerBiasField, $"{slot.Label}: 底上げの欄");
            Check.Equal(ConstInt(uiLayers, "DefaultStackStep"), slot.LayerBiasDefault, $"{slot.Label}: DefaultStackStep");
        }
        Check.Equal("RootPrefab", rootSlot.PrefabField, "根の画面の中身の欄");
        Check.Equal("RootSafeArea", rootSlot.SafeAreaField, "根の画面の安全領域の欄");
        Check.Equal(ConstBool(screenStack, "RootSafeArea"), rootSlot.SafeAreaDefault, "RootSafeArea の既定");
        foreach (var field in new[] { "RootPrefab", "RootSafeArea", "FramePrefab", "LayerStep", "ScreensChild" })
            Check.True(HasPublicField(screenStack, field), $"ScreenStack に [SerializeField] {field} がある");

        // ── ModalHost ──
        var modal = catalog.FindHost("ModalHost")!;
        AssertBand(modalHost, uiLayers, Slot(modal, "ダイアログ"), "DialogsChild", "DialogPrefab", "DefaultDialogPrefab", "DefaultDialogBand");
        AssertBand(modalHost, uiLayers, Slot(modal, "シート"),     "SheetsChild",  "SheetPrefab",  "DefaultSheetPrefab",  "DefaultSheetBand");
        AssertBand(modalHost, uiLayers, Slot(modal, "覆い"),       "OverlaysChild","OverlayPrefab","DefaultOverlayPrefab","DefaultOverlayBand");

        // ── ヒエラルキーの右クリックの底上げ = 画面のスタック 1 段 ──
        Check.Equal(ConstInt(uiLayers, "DefaultStackStep"), catalog.DefaultLayerBias, "default_layer_bias = DefaultStackStep");
    }

    /// <summary>ModalHost の帯 1 つ分の既定がスクリプトと同じか。</summary>
    private static void AssertBand(string modalHost, string uiLayers, PreviewHostSlot slot,
                                   string childField, string prefabField, string prefabConst, string bandConst)
    {
        Check.Equal(childField, slot.UnderField, $"{slot.Label}: 帯の欄");
        Check.Equal(ConstString(modalHost, childField), slot.UnderDefault, $"{slot.Label}: {childField} の既定");
        Check.Equal(prefabField, slot.PrefabField, $"{slot.Label}: 面の欄");
        Check.Equal(ConstString(modalHost, prefabConst), slot.PrefabDefault, $"{slot.Label}: {prefabConst}");
        Check.True(HasPublicField(modalHost, prefabField), $"ModalHost に [SerializeField] {prefabField} がある");
        Check.True(HasPublicField(modalHost, childField), $"ModalHost に [SerializeField] {childField} がある");
        Check.Equal(ConstInt(uiLayers, bandConst), slot.LayerBias, $"{slot.Label}: {bandConst}");
    }

    // ============================================================
    //  小道具
    // ============================================================

    /// <summary>案内の行を引いて欄の値を当てる。</summary>
    private static ResolvedPreviewSlot Resolve(string host, string label, Dictionary<string, string>? values) =>
        PreviewHostCatalog.ResolveSlot(Slot(PreviewHostCatalog.BuiltIn().FindHost(host)!, label), values);

    /// <summary>見出しで行を引く。</summary>
    private static PreviewHostSlot Slot(PreviewHost host, string label) =>
        host.Slots.FirstOrDefault(s => s.Label == label)
        ?? throw new AssertionException($"{host.ShortName} に行「{label}」が無い");

    /// <summary>行の見出しを並べる。</summary>
    private static string Labels(PreviewHostCatalog catalog, string host) =>
        string.Join(",", catalog.FindHost(host)!.Slots.Select(s => s.Label));

    /// <summary>2 つの表が同じか（版・底上げ・案内・行の全部の欄）。</summary>
    private static void AssertSameCatalog(PreviewHostCatalog expected, PreviewHostCatalog actual)
    {
        Check.Equal(expected.DefaultLayerBias, actual.DefaultLayerBias, "default_layer_bias");
        Check.Equal(expected.Hosts.Count, actual.Hosts.Count, "案内の数");
        for (int i = 0; i < expected.Hosts.Count; i++)
        {
            var e = expected.Hosts[i];
            var a = actual.Hosts[i];
            Check.Equal(e.Script, a.Script, $"案内 {i} の script");
            Check.Equal(e.ShortName, a.ShortName, $"案内 {i} の短い名前");
            Check.Equal(e.Label, a.Label, $"案内 {i} の見出し");
            Check.Equal(e.Slots.Count, a.Slots.Count, $"{e.ShortName} の行の数");
            for (int j = 0; j < e.Slots.Count; j++)
                Check.Equal(e.Slots[j], a.Slots[j], $"{e.ShortName} の行 {j}（{e.Slots[j].Label}）");
        }
    }

    /// <summary>組み込みの表へ落ちたこと（警告つき）を確かめる。</summary>
    private static void AssertFallback(string path, string what)
    {
        var warnings = new List<string>();
        var catalog  = PreviewHostCatalog.LoadFile(path, warnings.Add);
        Check.True(catalog.SourcePath is null, $"{what}: 組み込みの表へ落ちる");
        Check.Equal(1, warnings.Count, $"{what}: 警告");
        Check.Equal(1, catalog.Warnings.Count, $"{what}: Warnings にも残る");
        AssertSameCatalog(PreviewHostCatalog.BuiltIn(), catalog);
    }

    /// <summary>ソースの「名前 = "値"」の値（const・フィールドの初期値）を取り出す。</summary>
    private static string ConstString(string source, string name)
    {
        var match = Regex.Match(source, $@"\b{Regex.Escape(name)}\s*=\s*""([^""]*)""\s*;");
        Check.True(match.Success, $"ソースに {name} = \"...\"; が無い");
        return match.Groups[1].Value;
    }

    /// <summary>ソースの「名前 = 数;」の値（桁区切りの '_' を許す）を取り出す。</summary>
    private static int ConstInt(string source, string name)
    {
        var match = Regex.Match(source, $@"\b{Regex.Escape(name)}\s*=\s*([0-9_]+)\s*;");
        Check.True(match.Success, $"ソースに {name} = 数; が無い");
        return int.Parse(match.Groups[1].Value.Replace("_", ""), CultureInfo.InvariantCulture);
    }

    /// <summary>ソースの「名前 = true/false;」の値を取り出す。</summary>
    private static bool ConstBool(string source, string name)
    {
        var match = Regex.Match(source, $@"\b{Regex.Escape(name)}\s*=\s*(true|false)\s*;");
        Check.True(match.Success, $"ソースに {name} = true/false; が無い");
        return match.Groups[1].Value == "true";
    }

    /// <summary>ソースに「public 型 名前」のフィールドがあるか（[SerializeField] の欄名のずれ検知）。</summary>
    private static bool HasPublicField(string source, string name) =>
        Regex.IsMatch(source, $@"public\s+[\w<>\[\]?]+\s+{Regex.Escape(name)}\b");
}
