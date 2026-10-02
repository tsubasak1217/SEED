using SEEDEditor.DevicePresets;
using SpriteRigTests;

namespace AndroidRunUiTests;

/// <summary>
/// 端末プリセットの一覧（editor/config/device_presets.json の読み込み・検証・組み込みの 1 件へのフォールバック。
/// docs/editor_device_presets.md §2）。
/// </summary>
public static class DevicePresetCatalogTests
{
    /// <summary>同梱の JSON のリポジトリの根からの場所。</summary>
    private const string ShippedPresetsPath = "editor/config/device_presets.json";

    /// <summary>リポジトリの根を探すときに上へたどる階層の上限（テストは bin の下で動く）。</summary>
    private const int RepoProbeDepth = 12;

    /// <summary>テストを登録する。</summary>
    /// <param name="harness">テストランナー。</param>
    public static void Register(TestHarness harness)
    {
        harness.Add("端末プリセット: 同梱の device_presets.json は警告なしで 5 件（Pixel 6a 実寸・半分・dp 等倍・小さい電話・タブレットの順）・値が仕様どおり", ShippedFileMatchesSpec);
        harness.Add("端末プリセット: 組み込みの 1 件（Pixel 6a 半分）は同梱の pixel6a-half と同じ値", BuiltInMatchesShipped);
        harness.Add("端末プリセット: ファイルが無い・JSON が壊れている・presets が無い・使える端末が 0 件・構成フォルダが無いなら組み込みの 1 件と警告", FallsBackToBuiltIn);
        harness.Add("端末プリセット: 欠けたキーは既定（name=id・倍率 1・安全領域 0・キーボード 0・品質の指定なし・説明は空）", MissingKeysUseDefaults);
        harness.Add("端末プリセット: 間違った 1 件だけを捨てて理由を警告し、ほかの端末は残す", RejectsOnlyBrokenEntries);
        harness.Add("端末プリセット: id の重複は後のものを捨てる（大文字小文字を問わない）・新しすぎる版は警告して読む・コメントと末尾のカンマを許す・Find", DuplicatesVersionAndLookup);
    }

    // ── 同梱の JSON ─────────────────────────────────────────

    /// <summary>同梱の JSON の値（仕様の 5 件）。</summary>
    private static void ShippedFileMatchesSpec()
    {
        var catalog = DevicePresetCatalog.Load(FindRepoFile(ShippedPresetsPath));
        Check.Equal(0, catalog.Warnings.Count, $"警告（{string.Join(" / ", catalog.Warnings)}）");
        Check.True(catalog.SourcePath is not null, "同梱のファイルを読んだ（組み込みへ落ちていない）");
        Check.Equal("pixel6a-full,pixel6a-half,pixel6a-dp,phone-small,tablet",
            string.Join(",", catalog.Presets.Select(preset => preset.Id)), "並び（実行先セレクタの行の順）");

        var full = catalog.Presets[0];
        Check.Equal("Pixel 6a 実寸", full.Name, "実寸の名前");
        Check.Equal((1080, 2400), (full.WidthPx, full.HeightPx), "実寸の窓");
        Check.Equal(2.625, full.ScaleFactor, "実寸の倍率");
        Check.Equal(new DeviceSafeArea(0, 132, 0, 63), full.SafeArea, "実寸の安全領域");
        Check.Equal(979, full.KeyboardHeightPx, "実寸のキーボード（実機の実測）");

        var half = catalog.Presets[1];
        Check.Equal((540, 1200, 1.3125), (half.WidthPx, half.HeightPx, half.ScaleFactor), "半分の窓と倍率");
        Check.Equal(new DeviceSafeArea(0, 66, 0, 32), half.SafeArea, "半分の安全領域");
        Check.Equal((full.WidthDp, full.HeightDp), (half.WidthDp, half.HeightDp), "半分の dp の数は実寸と同じ（411×914）");

        var dp = catalog.Presets[2];
        Check.Equal((411, 914, 1.0), (dp.WidthPx, dp.HeightPx, dp.ScaleFactor), "dp 等倍の窓と倍率");
        Check.Equal(new DeviceSafeArea(0, 50, 0, 24), dp.SafeArea, "dp 等倍の安全領域");
        Check.Equal((411, 914), (dp.WidthDp, dp.HeightDp), "dp 等倍の dp の数");

        var phone = catalog.Presets[3];
        Check.Equal((360, 800, 2.0), (phone.WidthDp, phone.HeightDp, phone.ScaleFactor), "小さい電話は 360×800 dp @2");
        var tablet = catalog.Presets[4];
        Check.Equal((800, 1280, 1.5), (tablet.WidthDp, tablet.HeightDp, tablet.ScaleFactor), "タブレットは 800×1280 dp @1.5");

        Check.True(catalog.Presets.All(preset => preset.RenderQuality == "mobile"), "どれも描画の品質は mobile（Android の既定と同じ）");
        Check.True(catalog.Presets.All(preset => !string.IsNullOrWhiteSpace(preset.Description)), "どれも説明がある");
    }

    /// <summary>組み込みの 1 件と同梱の JSON の突き合わせ（片方だけ直すずれを捕まえる）。</summary>
    private static void BuiltInMatchesShipped()
    {
        var shipped = DevicePresetCatalog.Load(FindRepoFile(ShippedPresetsPath)).Find(DevicePresetCatalog.BuiltInPreset.Id);
        Check.True(shipped is not null, "同梱の JSON に pixel6a-half がある");
        Check.Equal(DevicePresetCatalog.BuiltInPreset, shipped, "組み込みの値（名前・説明まで）が同梱と同じ");
    }

    // ── フォールバック ─────────────────────────────────────

    /// <summary>読めないときは組み込みの 1 件。</summary>
    private static void FallsBackToBuiltIn()
    {
        using var temp = new AndroidPipelineTests.TempDir();

        void ExpectBuiltIn(DevicePresetCatalog catalog, string warningPart, string what)
        {
            Check.Equal(1, catalog.Presets.Count, $"{what}: 1 件");
            Check.Equal(DevicePresetCatalog.BuiltInPreset, catalog.Presets[0], $"{what}: 組み込みの Pixel 6a 半分");
            Check.True(catalog.SourcePath is null, $"{what}: 読んだファイルは無し");
            Check.True(catalog.Warnings.Any(warning => warning.Contains(warningPart)),
                $"{what}: 警告に「{warningPart}」（{string.Join(" / ", catalog.Warnings)}）");
        }

        ExpectBuiltIn(DevicePresetCatalog.Load(temp.Combine("none.json")), "見つからない", "ファイルが無い");
        ExpectBuiltIn(DevicePresetCatalog.Load(temp.WriteFile("broken.json", "{ \"presets\": [ { \"id\": ")), "JSON として読めない", "壊れた JSON");
        ExpectBuiltIn(DevicePresetCatalog.Load(temp.WriteFile("array.json", "[ { \"id\": \"a\" } ]")), "\"presets\" の配列が無い", "根が配列");
        ExpectBuiltIn(DevicePresetCatalog.Load(temp.WriteFile("nopresets.json", "{ \"format_version\": 1 }")), "\"presets\" の配列が無い", "presets が無い");
        ExpectBuiltIn(DevicePresetCatalog.Load(temp.WriteFile("notarray.json", "{ \"presets\": { \"id\": \"a\" } }")), "\"presets\" の配列が無い", "presets が配列でない");
        ExpectBuiltIn(DevicePresetCatalog.Load(temp.WriteFile("empty.json", "{ \"presets\": [] }")), "1 件も無い", "0 件");
        ExpectBuiltIn(DevicePresetCatalog.Load(temp.WriteFile("allbad.json", "{ \"presets\": [ { \"id\": \"a\" } ] }")), "1 件も無い", "使える端末が 0 件");
        ExpectBuiltIn(DevicePresetCatalog.LoadFromDir(null), "構成フォルダ", "構成フォルダが無い");

        // 構成フォルダにファイルがあれば読む（ファイル名は device_presets.json）
        temp.WriteFile(DevicePresetCatalog.FileName, "{ \"presets\": [ { \"id\": \"x\", \"width_px\": 100, \"height_px\": 200 } ] }");
        var fromDir = DevicePresetCatalog.LoadFromDir(temp.Path);
        Check.Equal("x", fromDir.Presets.Single().Id, "構成フォルダの device_presets.json を読む");
        Check.Equal(0, fromDir.Warnings.Count, "警告なし");
    }

    // ── 1 件の検証 ─────────────────────────────────────────

    /// <summary>欠けたキーの既定。</summary>
    private static void MissingKeysUseDefaults()
    {
        using var temp = new AndroidPipelineTests.TempDir();
        var catalog = DevicePresetCatalog.Load(temp.WriteFile("minimal.json",
            "{ \"presets\": [ { \"id\": \"  bare  \", \"width_px\": 360, \"height_px\": 640, \"render_quality\": \"  \" } ] }"));
        Check.Equal(0, catalog.Warnings.Count, $"警告なし（{string.Join(" / ", catalog.Warnings)}）");
        var preset = catalog.Presets.Single();
        Check.Equal("bare", preset.Id, "id は前後の空白を落とす");
        Check.Equal("bare", preset.Name, "name が無ければ id");
        Check.Equal(DevicePresetJson.DefaultScaleFactor, preset.ScaleFactor, "倍率の既定 1");
        Check.Equal(DeviceSafeArea.None, preset.SafeArea, "安全領域の既定は 0（画面全体）");
        Check.Equal(DevicePresetJson.DefaultKeyboardHeightPx, preset.KeyboardHeightPx, "キーボードの既定 0（模擬しない）");
        Check.True(!preset.SimulatesKeyboard, "キーボードを模擬しない");
        Check.True(preset.RenderQuality is null, "空白だけの品質は指定なし");
        Check.Equal(string.Empty, preset.Description, "説明の既定は空");
        Check.Equal((360, 640), (preset.WidthDp, preset.HeightDp), "倍率 1 なら dp = 画素");
    }

    /// <summary>間違った 1 件だけを捨てる。</summary>
    private static void RejectsOnlyBrokenEntries()
    {
        using var temp = new AndroidPipelineTests.TempDir();
        // 1 件目は正しい。2 件目以降はそれぞれ 1 か所だけ間違えてある（理由の文言の一部と組で確かめる）
        var cases = new (string Json, string Reason)[]
        {
            ("{ \"name\": \"no id\", \"width_px\": 100, \"height_px\": 100 }", "id がありません"),
            ("{ \"id\": \"no-width\", \"height_px\": 100 }", "width_px"),
            ("{ \"id\": \"zero-height\", \"width_px\": 100, \"height_px\": 0 }", "height_px"),
            ("{ \"id\": \"huge\", \"width_px\": 100, \"height_px\": 100000 }", "height_px"),
            ("{ \"id\": \"zero-scale\", \"width_px\": 100, \"height_px\": 100, \"scale_factor\": 0 }", "scale_factor"),
            ("{ \"id\": \"three-edges\", \"width_px\": 100, \"height_px\": 100, \"safe_area_px\": [1, 2, 3] }", "4 つの整数"),
            ("{ \"id\": \"negative-edge\", \"width_px\": 100, \"height_px\": 100, \"safe_area_px\": [0, -1, 0, 0] }", "負の値"),
            ("{ \"id\": \"covering\", \"width_px\": 100, \"height_px\": 100, \"safe_area_px\": [0, 60, 0, 40] }", "窓の大きさ以上"),
            ("{ \"id\": \"tall-keyboard\", \"width_px\": 100, \"height_px\": 100, \"keyboard_height_px\": 100 }", "keyboard_height_px"),
            ("{ \"id\": \"negative-keyboard\", \"width_px\": 100, \"height_px\": 100, \"keyboard_height_px\": -5 }", "keyboard_height_px"),
            ("{ \"id\": \"spaced-quality\", \"width_px\": 100, \"height_px\": 100, \"render_quality\": \"mobile --x\" }", "英数字"),
            ("{ \"id\": \"quoted-quality\", \"width_px\": 100, \"height_px\": 100, \"render_quality\": \"a\\\"b\" }", "英数字"),
            ("{ \"id\": \"string-width\", \"width_px\": \"540\", \"height_px\": 100 }", "string-width"),
            ("{ \"id\": \"fraction-width\", \"width_px\": 540.5, \"height_px\": 100 }", "fraction-width"),
            ("42", "オブジェクト"),
        };
        var good = "{ \"id\": \"good\", \"width_px\": 300, \"height_px\": 600, \"scale_factor\": 1.5 }";
        var json = "{ \"presets\": [ " + good + ", " + string.Join(", ", cases.Select(c => c.Json)) + " ] }";
        var catalog = DevicePresetCatalog.Load(temp.WriteFile("mixed.json", json));

        Check.Equal("good", string.Join(",", catalog.Presets.Select(preset => preset.Id)), "正しい 1 件だけが残る");
        Check.True(catalog.SourcePath is not null, "組み込みへは落ちない（使える端末がある）");
        Check.Equal(cases.Length, catalog.Warnings.Count, $"捨てた件数だけ警告（{string.Join(" / ", catalog.Warnings)}）");
        for (var i = 0; i < cases.Length; i++)
        {
            var warning = catalog.Warnings[i];
            // 何件目か（正しい 1 件目の次から数える）と理由
            Check.True(warning.Contains($"{i + 2} 件目"), $"{i + 2} 件目の警告に番号: {warning}");
            Check.True(warning.Contains(cases[i].Reason), $"{i + 2} 件目の理由に「{cases[i].Reason}」: {warning}");
        }
    }

    /// <summary>id の重複・書式の版・コメント・Find。</summary>
    private static void DuplicatesVersionAndLookup()
    {
        using var temp = new AndroidPipelineTests.TempDir();
        const string json = """
            {
              // 人が手で書くファイルなのでコメントと末尾のカンマを許す
              "format_version": 99,
              "presets": [
                { "id": "Phone", "name": "前", "width_px": 100, "height_px": 200 },
                { "id": "phone", "name": "後", "width_px": 300, "height_px": 400 },
                { "id": "other", "width_px": 50, /* 要素の中のコメント */ "height_px": 60, },
              ],
            }
            """;
        var catalog = DevicePresetCatalog.Load(temp.WriteFile("dup.json", json));
        Check.Equal("Phone,other", string.Join(",", catalog.Presets.Select(preset => preset.Id)), "重複の後のものを捨てる");
        Check.Equal("前", catalog.Presets[0].Name, "前のものが残る");
        Check.True(catalog.Warnings.Any(warning => warning.Contains("重複")), $"重複の警告（{string.Join(" / ", catalog.Warnings)}）");
        Check.True(catalog.Warnings.Any(warning => warning.Contains("format_version=99")), "新しすぎる版の警告");
        Check.True(catalog.Find("PHONE") is { Name: "前" }, "Find は大文字小文字を区別しない");
        Check.True(catalog.Find("nothing") is null && catalog.Find(null) is null && catalog.Find(" ") is null, "無ければ null");
    }

    // ── 補助 ─────────────────────────────────────────────

    /// <summary>
    /// リポジトリの根からの相対パスでファイルを探す（テストは bin の下で動くので上へたどる）。
    /// </summary>
    /// <param name="relativePath">リポジトリの根からの相対パス（'/' 区切り）。</param>
    /// <returns>絶対パス。</returns>
    /// <exception cref="AssertionException">見つからないとき（同梱のファイルの確かめを黙って飛ばさない）。</exception>
    private static string FindRepoFile(string relativePath)
    {
        var relative = relativePath.Replace('/', Path.DirectorySeparatorChar);
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < RepoProbeDepth && dir is not null; i++)
        {
            var candidate = Path.Combine(dir, relative);
            if (File.Exists(candidate)) return candidate;
            dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
        }
        throw new AssertionException($"リポジトリのファイルが見つかりません: {relativePath}（起点: {AppContext.BaseDirectory}）");
    }
}
