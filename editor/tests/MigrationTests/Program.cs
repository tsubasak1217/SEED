using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SEEDEditor.InputMap;
using SEEDEditor.Migration;
using SEEDEditor.ProjectSettings;
using SEEDEditor.Runtime.BuildConfig;
using SpriteRigTests;   // TestHarness / Check（テストランナーは SpriteRigTests と共用）

namespace MigrationTests;

/// <summary>
/// アセット形式のマイグレーション（docs/asset_migration.md）のエディタ側単体テスト。
///
/// 検証の柱:
///   1. C# の版の表（AssetFormat.cs）が Rust の正典（kind.rs）と一致すること
///   2. 版の覗き読み（欄なし・現行・未来・壊れたファイル・入れ子の同名キー）
///   3. --upgrade-project の出力（JSON Lines）の解釈
///   4. 刻印の往復（保存すると版が先頭に入り、読み戻せる）
///   5. --migrate-json の実行（runtime/target/debug/SEED.exe があるときだけ）
/// </summary>
public static class Program
{
    // ── 固定値（マジックナンバー・マジック文字列の一元化）──────

    /// <summary>ランタイム exe を探すビルド構成の出力フォルダ名（dev プロファイルの出力先）。</summary>
    private const string DEBUG_TARGET_DIR = "debug";

    /// <summary>テスト用の一時フォルダ名の接頭辞。</summary>
    private const string TEMP_PREFIX = "seed_migration_editor_test_";

    /// <summary>v1 の .inputmap（WASD 合成軸を bindings に持つ旧形式）。</summary>
    private const string LEGACY_INPUTMAP = """
        {"actions":[{"name":"Steer","value_type":1,
          "bindings":[{"platform":"PC","input_type":"WASD","value":"Horizontal"}]}]}
        """;

    /// <summary>エントリポイント。全テストを実行し、失敗があれば終了コード 1 を返す。</summary>
    public static int Main()
    {
        var harness = new TestHarness();

        // ── 1. 版の表（Rust の正典との突き合わせ）───────────
        harness.Add("C# の版の表が kind.rs と一致する",            FormatTableMatchesRust);
        harness.Add("版の欄名は 2 種類だけで、綴りが固定されている", VersionKeyNamesAreFixed);
        harness.Add("形式名から形式を逆引きできる",                 FormatLookupByLabel);

        // ── 2. 版の覗き読み ─────────────────────────────────
        harness.Add("版の欄が無いファイルは 1 版とみなす",          PeekMissingFieldIsVersionOne);
        harness.Add("版の欄があればその値を返す",                   PeekReadsExplicitVersion);
        harness.Add("未来版も読み取れる（判断は呼び出し側）",       PeekReadsFutureVersion);
        harness.Add("入れ子の同名キーには反応しない",               PeekIgnoresNestedKeys);
        harness.Add("BOM 付きでも読める",                           PeekAcceptsBom);
        harness.Add("トップレベルがオブジェクトでなければ NotObject", PeekRejectsNonObject);
        harness.Add("壊れた JSON は NotJson",                       PeekRejectsBrokenJson);
        harness.Add("版が非負整数でなければ InvalidVersion",        PeekRejectsInvalidVersion);
        harness.Add(".inputmap は version 欄を見る（format_version ではない）",
                                                                    PeekUsesPerFormatKey);

        // ── 3. --upgrade-project の出力の解釈 ───────────────
        harness.Add("ファイル行・prefab_hash 行・集計行を見分ける", ReportParsesEachLineKind);
        harness.Add("形式ごとの更新件数を数えられる",               ReportCountsByKind);
        harness.Add("error 行と解釈できない行を分けて拾う",         ReportKeepsErrorsAndUnparsed);
        harness.Add("集計に failed / future_version があれば問題ありと判定する",
                                                                    ReportSummaryDetectsProblem);

        // ── 4. 刻印の往復 ───────────────────────────────────
        harness.Add("AssetVersionStamp は版を先頭へ置く",           StampPutsVersionFirst);
        harness.Add("AssetVersionStamp は既存の版を現行版へ置き換える",
                                                                    StampReplacesExistingVersion);
        harness.Add(".inputmap は保存 → 先頭に version → 読み戻せる", InputMapStampRoundTrip);
        harness.Add("project_settings.json は保存 → 先頭に format_version → 読み戻せる",
                                                                    ProjectSettingsStampRoundTrip);
        harness.Add("project_settings.json の未知キーは往復で失われない",
                                                                    ProjectSettingsKeepsUnknownKeys);
        harness.Add("ランタイム exe が無いと古い .inputmap は開かない（既定値で上書きさせない）",
                                                                    GatewayBlocksWhenRuntimeIsMissing);

        // ── 5. --migrate-json の実行（exe があるときだけ）───
        var exePath = FindRuntimeExe();
        if (exePath is not null)
        {
            Console.WriteLine($"  ランタイム exe を使うテストを有効化: {exePath}");
            harness.Add(".inputmap v1 が v2 へ展開される",          () => MigrateExpandsLegacyInputMap(exePath));
            harness.Add("未来版は FutureVersion として返る",        () => MigrateRejectsFutureVersion(exePath));
            harness.Add("未知の形式名は BadUsage として返る",       () => MigrateRejectsUnknownKind(exePath));
            harness.Add("門は古い .inputmap を変換して読ませる",    () => GatewayMigratesLegacyFile(exePath));
            harness.Add("門は未来版のファイルを開かせない",         () => GatewayBlocksFutureVersionFile(exePath));
            harness.Add("一括アップグレードの実出力を解釈できる（dry-run → 実行 → 冪等）",
                                                                    () => UpgradeRunnerRoundTrip(exePath));
        }
        else
        {
            Console.WriteLine(
                "  ランタイム exe が見つからないため --migrate-json のテストは省略します"
                + $"（runtime/target/{DEBUG_TARGET_DIR}/SEED.exe）");
        }

        return harness.Run();
    }

    // ============================================================
    //  1. 版の表
    // ============================================================

    /// <summary>
    /// C# の表と Rust の表（kind.rs）が、形式・欄名・現行版・書き手のすべてで一致すること。
    /// **ここが落ちたら、片方だけ直したということ**。
    /// </summary>
    private static void FormatTableMatchesRust()
    {
        var kindRs = RustFormatTable.FindKindRs();
        Check.True(kindRs is not null,
            $"kind.rs が見つからない（{RustFormatTable.KindRsRelativePath}）。"
            + "リポジトリの外でテストを実行している可能性がある");

        var rust = RustFormatTable.Parse(kindRs!);
        Check.True(rust.Count > 0, "kind.rs から FormatSpec を 1 件も読めなかった（書き方が変わった？）");

        Check.Equal(rust.Count, AssetFormats.All.Count,
            "形式の件数（kind.rs と AssetFormats.All）");

        foreach (var format in AssetFormats.All)
        {
            Check.True(rust.TryGetValue(format.Label, out var entry),
                $"kind.rs に形式 {format.Label} が無い");

            Check.Equal(entry!.CurrentVersion, format.CurrentVersion,
                $"{format.Label} の現行版");
            Check.Equal(entry.VersionKeyVariant, RustFormatTable.ToRustVersionKey(format.VersionKey),
                $"{format.Label} の版の欄名");
            Check.Equal(entry.WriterVariant, RustFormatTable.ToRustWriter(format.Writer),
                $"{format.Label} の書き手");
        }
    }

    /// <summary>版の欄名の綴りが固定されていること（既存ファイルを書き換えないための約束）。</summary>
    private static void VersionKeyNamesAreFixed()
    {
        Check.Equal("format_version", AssetFormats.FORMAT_VERSION_KEY, "新しい形式の版の欄名");
        Check.Equal("version",        AssetFormats.LEGACY_VERSION_KEY, "従来からの版の欄名");
        Check.Equal("format_version", AssetFormats.Anim.VersionKeyName, ".anim の版の欄名");
        Check.Equal("version",        AssetFormats.InputMap.VersionKeyName, ".inputmap の版の欄名");
        Check.Equal("version",        AssetFormats.SpriteMesh.VersionKeyName, ".sprite_mesh の版の欄名");
        Check.Equal(1, AssetFormats.IMPLICIT_FIRST_VERSION, "欄が無いファイルの既定の版");
    }

    /// <summary>形式名からの逆引き（--migrate-json の引数と同じ綴り）。</summary>
    private static void FormatLookupByLabel()
    {
        foreach (var format in AssetFormats.All)
        {
            Check.True(ReferenceEquals(AssetFormats.FromLabel(format.Label), format),
                $"形式名 {format.Label} から逆引きできる");
        }
        Check.True(AssetFormats.FromLabel("unknown_format") is null, "未知の形式名は null");
        Check.True(AssetFormats.FromLabel("") is null, "空の形式名は null");
    }

    // ============================================================
    //  2. 版の覗き読み
    // ============================================================

    /// <summary>版の欄が無いファイルは 1 版（暗黙の既定）として読まれること。</summary>
    private static void PeekMissingFieldIsVersionOne()
    {
        var peek = AssetVersionPeek.Read("""{"name":"Swim","tracks":[]}""", AssetFormats.Anim);
        Check.True(peek.IsOk, "読み取れること");
        Check.Equal(AssetFormats.IMPLICIT_FIRST_VERSION, peek.Version, "欄なしの版");
        Check.True(!peek.HasVersionField, "欄が物理的に無いことを区別できる");
    }

    /// <summary>版の欄があればその値が返ること。</summary>
    private static void PeekReadsExplicitVersion()
    {
        var peek = AssetVersionPeek.Read("""{"format_version":2,"name":"S"}""", AssetFormats.Scene);
        Check.True(peek.IsOk, "読み取れること");
        Check.Equal(2, peek.Version, "明示された版");
        Check.True(peek.HasVersionField, "欄が物理的にあること");
    }

    /// <summary>未来版も「読み取れる」こと（拒否の判断は門の仕事）。</summary>
    private static void PeekReadsFutureVersion()
    {
        var future = AssetFormats.Anim.CurrentVersion + 1;
        var peek = AssetVersionPeek.Read($$"""{"format_version":{{future}}}""", AssetFormats.Anim);
        Check.True(peek.IsOk, "読み取れること");
        Check.Equal(future, peek.Version, "未来版の値");
    }

    /// <summary>
    /// 入れ子の中に同じ名前のキーがあっても、トップレベルの版として拾わないこと。
    /// （拾うと、コンポーネントの中の "version" をファイルの版と誤認する）
    /// </summary>
    private static void PeekIgnoresNestedKeys()
    {
        const string json = """
            {"actions":[{"name":"A","version":99}],"meta":{"version":42}}
            """;
        var peek = AssetVersionPeek.Read(json, AssetFormats.InputMap);
        Check.True(peek.IsOk, "読み取れること");
        Check.Equal(AssetFormats.IMPLICIT_FIRST_VERSION, peek.Version,
            "入れ子の version を拾っていないこと");
        Check.True(!peek.HasVersionField, "トップレベルには欄が無いこと");
    }

    /// <summary>先頭 BOM 付きのファイルも読めること。</summary>
    private static void PeekAcceptsBom()
    {
        var peek = AssetVersionPeek.Read("﻿{\"version\":2}", AssetFormats.InputMap);
        Check.True(peek.IsOk, "BOM 付きでも読めること");
        Check.Equal(2, peek.Version, "版");
    }

    /// <summary>トップレベルが配列などのファイルは形式違いとして弾くこと。</summary>
    private static void PeekRejectsNonObject()
    {
        var peek = AssetVersionPeek.Read("[1,2,3]", AssetFormats.Anim);
        Check.Equal(AssetVersionPeekStatus.NotObject, peek.Status, "配列は NotObject");
    }

    /// <summary>壊れた JSON は NotJson として返ること（例外を投げないこと）。</summary>
    private static void PeekRejectsBrokenJson()
    {
        Check.Equal(AssetVersionPeekStatus.NotJson,
            AssetVersionPeek.Read("{not json", AssetFormats.Anim).Status, "壊れた JSON");
        Check.Equal(AssetVersionPeekStatus.NotJson,
            AssetVersionPeek.Read("", AssetFormats.Anim).Status, "空文字");
        Check.Equal(AssetVersionPeekStatus.NotJson,
            AssetVersionPeek.Read(null, AssetFormats.Anim).Status, "null");
    }

    /// <summary>版の欄が非負整数でなければ形式違いとして弾くこと。</summary>
    private static void PeekRejectsInvalidVersion()
    {
        Check.Equal(AssetVersionPeekStatus.InvalidVersion,
            AssetVersionPeek.Read("""{"format_version":"2"}""", AssetFormats.Anim).Status, "文字列");
        Check.Equal(AssetVersionPeekStatus.InvalidVersion,
            AssetVersionPeek.Read("""{"format_version":1.5}""", AssetFormats.Anim).Status, "小数");
        Check.Equal(AssetVersionPeekStatus.InvalidVersion,
            AssetVersionPeek.Read("""{"format_version":-1}""", AssetFormats.Anim).Status, "負数");
        Check.Equal(AssetVersionPeekStatus.InvalidVersion,
            AssetVersionPeek.Read("""{"format_version":null}""", AssetFormats.Anim).Status, "null");
    }

    /// <summary>
    /// 形式ごとに見る欄名が違うこと。
    /// .inputmap のファイルに format_version が書いてあっても版としては見ない。
    /// </summary>
    private static void PeekUsesPerFormatKey()
    {
        const string json = """{"format_version":9,"version":2,"actions":[]}""";

        var asInputMap = AssetVersionPeek.Read(json, AssetFormats.InputMap);
        Check.Equal(2, asInputMap.Version, ".inputmap は version 欄を見る");

        var asScene = AssetVersionPeek.Read(json, AssetFormats.Scene);
        Check.Equal(9, asScene.Version, ".scene は format_version 欄を見る");
    }

    // ============================================================
    //  3. --upgrade-project の出力の解釈
    // ============================================================

    /// <summary>
    /// 固定のサンプル行（ランタイムの report.rs が出す形）を正しく振り分けること。
    /// </summary>
    private static void ReportParsesEachLineKind()
    {
        var lines = new[]
        {
            """{"kind":"scene","path":"assets/scenes/Main.scene","from":1,"to":2,"status":"upgraded","message":""}""",
            """{"kind":"anim","path":"assets/anim/Swim.anim","from":1,"to":1,"status":"up_to_date","message":""}""",
            """{"kind":"actor","path":"assets/prefabs/Fish.actor","from":3,"to":3,"status":"future_version","message":"新しいバージョンのエンジン"}""",
            """{"kind":"sprite_mesh","path":"assets/Broken.sprite_mesh","from":0,"to":0,"status":"failed","message":"頂点がありません"}""",
            """{"kind":"prefab_hash","path":"assets/scenes/Main.scene","updated":2,"message":""}""",
            """{"kind":"summary","total":4,"upgraded":1,"up_to_date":1,"future_version":1,"failed":1,"prefab_hash_scenes":1,"prefab_hash_updated":2,"dry_run":true}""",
        };

        var report = ProjectUpgradeReport.Parse(lines);

        Check.Equal(4, report.Files.Count, "ファイル行の件数");
        Check.Equal(UpgradeFileStatus.Upgraded,      report.Files[0].Status, "1 件目の状態");
        Check.Equal("assets/scenes/Main.scene",      report.Files[0].Path,   "1 件目のパス");
        Check.Equal(1, report.Files[0].From, "1 件目の変換前の版");
        Check.Equal(2, report.Files[0].To,   "1 件目の変換後の版");
        Check.Equal(UpgradeFileStatus.UpToDate,      report.Files[1].Status, "2 件目の状態");
        Check.Equal(UpgradeFileStatus.FutureVersion, report.Files[2].Status, "3 件目の状態");
        Check.Equal(UpgradeFileStatus.Failed,        report.Files[3].Status, "4 件目の状態");

        Check.Equal(1, report.PrefabHashes.Count, "prefab_hash 行の件数");
        Check.Equal(2, report.PrefabHashes[0].Updated, "貼り直したインスタンス数");

        Check.True(report.Summary is not null, "集計行を拾えていること");
        Check.Equal(4, report.Summary!.Total, "集計: 総数");
        Check.Equal(1, report.Summary.Upgraded, "集計: 更新");
        Check.Equal(2, report.Summary.PrefabHashUpdated, "集計: 貼り直し");
        Check.True(report.Summary.DryRun, "集計: dry-run であること");
        Check.Equal(0, report.UnparsedLines.Count, "解釈できない行が無いこと");
    }

    /// <summary>形式ごとの更新件数が、ランタイムの出力順で数えられること。</summary>
    private static void ReportCountsByKind()
    {
        var lines = new[]
        {
            """{"kind":"anim","path":"a.anim","from":1,"to":1,"status":"upgraded","message":""}""",
            """{"kind":"scene","path":"b.scene","from":1,"to":2,"status":"upgraded","message":""}""",
            """{"kind":"anim","path":"c.anim","from":1,"to":1,"status":"upgraded","message":""}""",
            """{"kind":"anim","path":"d.anim","from":1,"to":1,"status":"up_to_date","message":""}""",
            """{"kind":"summary","total":4,"upgraded":3,"up_to_date":1,"future_version":0,"failed":0,"prefab_hash_scenes":0,"prefab_hash_updated":0,"dry_run":true}""",
        };

        var counts = ProjectUpgradeReport.Parse(lines).CountByKind(UpgradeFileStatus.Upgraded);
        Check.Equal(2, counts.Count, "形式の種類数");
        Check.Equal("anim", counts[0].Key, "出力順で先に出た形式");
        Check.Equal(2, counts[0].Value, "anim の件数");
        Check.Equal("scene", counts[1].Key, "2 番目の形式");
        Check.Equal(1, counts[1].Value, "scene の件数");
    }

    /// <summary>error 行と、解釈できない行を分けて拾うこと（壊れた行で落ちないこと）。</summary>
    private static void ReportKeepsErrorsAndUnparsed()
    {
        var lines = new[]
        {
            """{"kind":"error","message":"プロジェクトが見つかりません"}""",
            "これは JSON ではない",
            """{"kind":"future_thing","foo":1}""",
            "",
        };

        var report = ProjectUpgradeReport.Parse(lines);
        Check.Equal(1, report.Errors.Count, "error 行の件数");
        Check.Equal("プロジェクトが見つかりません", report.Errors[0], "error 行の本文");
        Check.Equal(2, report.UnparsedLines.Count, "解釈できない行の件数（空行は数えない）");
        Check.True(report.Summary is null, "集計行が無いこと");
    }

    /// <summary>集計の「問題あり」判定（終了コードが非 0 になる条件）と一致すること。</summary>
    private static void ReportSummaryDetectsProblem()
    {
        var clean = new UpgradeSummaryLine(3, 1, 2, 0, 0, 0, 0, false);
        Check.True(!clean.HasProblem, "失敗も未来版も無ければ問題なし");

        Check.True(new UpgradeSummaryLine(3, 1, 1, 1, 0, 0, 0, false).HasProblem, "未来版は問題あり");
        Check.True(new UpgradeSummaryLine(3, 1, 1, 0, 1, 0, 0, false).HasProblem, "失敗は問題あり");
    }

    // ============================================================
    //  4. 刻印の往復
    // ============================================================

    /// <summary>版を先頭に置いた新しいオブジェクトが作られること（元は変えないこと）。</summary>
    private static void StampPutsVersionFirst()
    {
        var root = new JsonObject
        {
            ["name"]   = "X",
            ["layers"] = new JsonArray { "a", "b" },
        };

        var stamped = AssetVersionStamp.WithVersionFirst(root, AssetFormats.TerrainLayers);
        var text = stamped.ToJsonString(new JsonSerializerOptions { WriteIndented = true });

        var firstKey = FirstTopLevelKey(text);
        Check.Equal(AssetFormats.TerrainLayers.VersionKeyName, firstKey, "先頭のキー");
        Check.Equal(AssetFormats.TerrainLayers.CurrentVersion,
            (int)stamped[AssetFormats.TerrainLayers.VersionKeyName]!.GetValue<int>(), "刻んだ版");
        Check.Equal("X", stamped["name"]!.GetValue<string>(), "元の欄が残ること");
        Check.Equal(2, stamped["layers"]!.AsArray().Count, "入れ子も複製されること");

        // 元のオブジェクトは変わっていない（同じドキュメントを 2 回書いても結果が変わらない）。
        Check.True(root[AssetFormats.TerrainLayers.VersionKeyName] is null,
            "元のオブジェクトへ版を書き込んでいないこと");
    }

    /// <summary>既に版の欄があるときは、現行版で置き換えて先頭へ移すこと。</summary>
    private static void StampReplacesExistingVersion()
    {
        var root = new JsonObject
        {
            ["name"] = "X",
            [AssetFormats.TerrainProps.VersionKeyName] = 0,
        };

        var stamped = AssetVersionStamp.WithVersionFirst(root, AssetFormats.TerrainProps);
        var text = stamped.ToJsonString();

        Check.Equal(AssetFormats.TerrainProps.VersionKeyName, FirstTopLevelKey(text), "先頭のキー");
        Check.Equal(AssetFormats.TerrainProps.CurrentVersion,
            stamped[AssetFormats.TerrainProps.VersionKeyName]!.GetValue<int>(), "現行版で上書きすること");
        Check.Equal(2, stamped.Count, "欄が二重にならないこと");
    }

    /// <summary>
    /// .inputmap を保存すると version がトップレベルの先頭に入り、そのまま読み戻せること。
    /// （現行版なので、読み戻しでランタイムを起動しないこと＝exe 無しでも通ること）
    /// </summary>
    private static void InputMapStampRoundTrip()
    {
        using var temp = new TempDir();
        var path = Path.Combine(temp.Path, "Game.inputmap");

        var data = new InputMapData();
        data.Actions.Add(new InputAction { Name = "Jump", ValueType = ActionValueType.Bool });
        data.SaveTo(path, temp.Path);

        var text = File.ReadAllText(path);
        Check.Equal(AssetFormats.InputMap.VersionKeyName, FirstTopLevelKey(text),
            $"先頭のキー（実際の JSON: {Head(text)}）");

        var peek = AssetVersionPeek.Read(text, AssetFormats.InputMap);
        Check.True(peek.HasVersionField, "版の欄が物理的に書かれていること");
        Check.Equal(AssetFormats.InputMap.CurrentVersion, peek.Version, "刻まれた版");

        var loaded = InputMapData.LoadFrom(path);
        Check.True(!loaded.IsUnreadable, "読み戻せること");
        Check.Equal(1, loaded.Actions.Count, "アクション数");
        Check.Equal("Jump", loaded.Actions[0].Name, "アクション名");
    }

    /// <summary>
    /// project_settings.json を保存すると format_version がトップレベルの先頭に入り、
    /// そのまま読み戻せること。
    /// </summary>
    private static void ProjectSettingsStampRoundTrip()
    {
        using var temp = new TempDir();
        var path = Path.Combine(temp.Path, "project_settings.json");

        var data = new ProjectSettingsData { GameName = "MyGame", StartScene = "assets://Main.scene" };
        data.SaveTo(path);

        var text = File.ReadAllText(path);
        Check.Equal(AssetFormats.ProjectSettings.VersionKeyName, FirstTopLevelKey(text),
            $"先頭のキー（実際の JSON: {Head(text)}）");

        var peek = AssetVersionPeek.Read(text, AssetFormats.ProjectSettings);
        Check.True(peek.HasVersionField, "版の欄が物理的に書かれていること");
        Check.Equal(AssetFormats.ProjectSettings.CurrentVersion, peek.Version, "刻まれた版");

        var loaded = ProjectSettingsData.LoadFrom(path);
        Check.True(!loaded.IsUnreadable, "読み戻せること");
        Check.Equal("MyGame", loaded.GameName, "ゲーム名");
        Check.Equal("assets://Main.scene", loaded.StartScene, "開始シーン");
    }

    /// <summary>
    /// project_settings.json のモデル化していないキー（ビューポートが書くグラフィックス設定など）が
    /// 往復で失われないこと。版の欄を足したことで壊れていないかの確認でもある。
    /// </summary>
    private static void ProjectSettingsKeepsUnknownKeys()
    {
        using var temp = new TempDir();
        var path = Path.Combine(temp.Path, "project_settings.json");

        File.WriteAllText(path, """
            {
              "game_name": "Old",
              "gi_intensity": 0.75,
              "features": { "shadow": "shadowmap" }
            }
            """, new UTF8Encoding(false));

        var loaded = ProjectSettingsData.LoadFrom(path);
        Check.Equal(AssetFormats.ProjectSettings.CurrentVersion, loaded.FormatVersion,
            "欄なしのファイルは現行版として読まれること");
        loaded.SaveTo(path);

        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;
        Check.True(root.TryGetProperty("gi_intensity", out var gi), "未知キーが残ること");
        Check.Close(0.75, gi.GetDouble(), 1e-9, "未知キーの値");
        Check.True(root.TryGetProperty("features", out _), "未知の入れ子も残ること");
        Check.Equal("Old", root.GetProperty("game_name").GetString(), "既知キーの値");
    }

    /// <summary>
    /// ランタイム exe が無い（未ビルド・配置違い）ときに、古い形式のファイルを
    /// **黙って旧形式として解釈しない**こと。
    ///
    /// <para>
    /// 変換は Rust に一本化してあるので、exe が無ければ v1 の .inputmap は変換できない。
    /// ここで空のマップを返して編集させると、保存した瞬間に利用者のアクション定義が消える。
    /// 「開かない」が正しい振る舞いであることを固定する。
    /// </para>
    /// </summary>
    private static void GatewayBlocksWhenRuntimeIsMissing()
    {
        using var temp = new TempDir();
        var path = Path.Combine(temp.Path, "Legacy.inputmap");
        File.WriteAllText(path, LEGACY_INPUTMAP, new UTF8Encoding(false));

        // exe の場所として「存在しないパス」を差し込む。
        using var gateway = new GatewayScope(Path.Combine(temp.Path, "no_such_SEED.exe"));

        var read = AssetMigrationGateway.ReadFile(path, AssetFormats.InputMap);
        Check.Equal(AssetReadStatus.Blocked, read.Status, "開かせないこと");

        var data = InputMapData.LoadFrom(path);
        Check.True(data.IsUnreadable, "読み手が「保存してはいけない」と分かること");
        Check.Equal(LEGACY_INPUTMAP, File.ReadAllText(path), "ファイルを書き換えないこと");
    }

    // ============================================================
    //  5. --migrate-json の実行（exe があるときだけ）
    // ============================================================

    /// <summary>v1 の .inputmap が v2（正負グループ）へ展開されること。</summary>
    /// <param name="exePath">ランタイム exe の絶対パス。</param>
    private static void MigrateExpandsLegacyInputMap(string exePath)
    {
        var result = MigrateJsonRunner.Run(exePath, AssetFormats.InputMap, LEGACY_INPUTMAP);
        Check.True(result.IsSuccess, $"変換に成功すること: {result.Outcome} {result.Message}");

        using var doc = JsonDocument.Parse(result.Json);
        var root = doc.RootElement;
        Check.Equal(AssetFormats.InputMap.CurrentVersion,
            root.GetProperty(AssetFormats.InputMap.VersionKeyName).GetInt32(), "変換後の版");

        var action = root.GetProperty("actions")[0];
        Check.True(action.TryGetProperty("positive", out var positive), "正グループが作られること");
        Check.Equal("D", positive[0].GetProperty("value").GetString(), "WASD の正側が展開されること");
        Check.True(action.TryGetProperty("negative", out var negative), "負グループが作られること");
        Check.Equal("A", negative[0].GetProperty("value").GetString(), "WASD の負側が展開されること");
    }

    /// <summary>未来版は終了コード 1（FutureVersion）で返ること。</summary>
    /// <param name="exePath">ランタイム exe の絶対パス。</param>
    private static void MigrateRejectsFutureVersion(string exePath)
    {
        var future = AssetFormats.Anim.CurrentVersion + 1;
        var json   = $$"""{"{{AssetFormats.Anim.VersionKeyName}}":{{future}},"name":"Swim","tracks":[]}""";

        var result = MigrateJsonRunner.Run(exePath, AssetFormats.Anim, json);
        Check.Equal(MigrateJsonOutcome.FutureVersion, result.Outcome, "結末");
        Check.True(result.Message.Length > 0, "利用者へ出せる説明が付くこと");
    }

    /// <summary>形式名の綴り違いは終了コード 2（BadUsage）で返ること。</summary>
    /// <param name="exePath">ランタイム exe の絶対パス。</param>
    private static void MigrateRejectsUnknownKind(string exePath)
    {
        // 表に無い形式名を CLI へ渡すため、テスト用の仮の形式は作らず、
        // 既存の形式のラベルを壊した文字列をランタイムへ直接渡す。
        var result = RunWithRawKind(exePath, "not_a_format", "{}");
        Check.Equal(MigrateJsonOutcome.BadUsage, result.Outcome, "結末");
    }

    /// <summary>門（AssetMigrationGateway）が古いファイルを変換して読ませること。</summary>
    /// <param name="exePath">ランタイム exe の絶対パス。</param>
    private static void GatewayMigratesLegacyFile(string exePath)
    {
        using var temp = new TempDir();
        var path = Path.Combine(temp.Path, "Legacy.inputmap");
        File.WriteAllText(path, LEGACY_INPUTMAP, new UTF8Encoding(false));

        using var gateway = new GatewayScope(exePath);

        var read = AssetMigrationGateway.ReadFile(path, AssetFormats.InputMap);
        Check.Equal(AssetReadStatus.Migrated, read.Status, "変換して読んだこと");

        // ファイルは 1 バイトも書き換わっていないこと（読み込みはメモリ上だけ）。
        Check.Equal(LEGACY_INPUTMAP, File.ReadAllText(path), "読み込みでファイルを書き換えないこと");

        // 変換済みテキストがモデルへ載ること。
        var data = InputMapData.LoadFrom(path);
        Check.True(!data.IsUnreadable, "読めること");
        Check.Equal(1, data.Actions.Count, "アクション数");
        Check.True(data.Actions[0].Positive is { Count: > 0 }, "正グループが展開されていること");
    }

    /// <summary>門が未来版のファイルを開かせないこと（既定値で続けさせないこと）。</summary>
    /// <param name="exePath">ランタイム exe の絶対パス。</param>
    private static void GatewayBlocksFutureVersionFile(string exePath)
    {
        using var temp = new TempDir();
        var path = Path.Combine(temp.Path, "Future.inputmap");
        var future = AssetFormats.InputMap.CurrentVersion + 1;
        File.WriteAllText(path,
            $$"""{"{{AssetFormats.InputMap.VersionKeyName}}":{{future}},"actions":[]}""",
            new UTF8Encoding(false));

        using var gateway = new GatewayScope(exePath);

        var read = AssetMigrationGateway.ReadFile(path, AssetFormats.InputMap);
        Check.Equal(AssetReadStatus.Blocked, read.Status, "開かせないこと");
        Check.True(read.Message.Length > 0, "理由が付くこと");

        var data = InputMapData.LoadFrom(path);
        Check.True(data.IsUnreadable, "読み手が「保存してはいけない」と分かること");
    }

    /// <summary>
    /// 一括アップグレードを実際に走らせ、C# 側が出力（JSON Lines）を解釈できることを確かめる。
    ///
    /// <para>
    /// 固定のサンプル行で解釈を固定しているのとは別に、**本物の出力**で
    /// 「行の形が変わったら気づける」状態にしておくための検証。
    /// dry-run は 1 バイトも書かず、実行すると版が入り、2 回目は全件 up_to_date になる。
    /// </para>
    /// </summary>
    /// <param name="exePath">ランタイム exe の絶対パス。</param>
    private static void UpgradeRunnerRoundTrip(string exePath)
    {
        using var temp = new TempDir();

        // <temp>/assets/ を作る（ランタイムはここをアセットルートとして拾う）。
        var assets = Path.Combine(temp.Path, "assets");
        Directory.CreateDirectory(assets);

        var inputMapPath = Path.Combine(assets, "Game.inputmap");
        var animPath     = Path.Combine(assets, "Swim.anim");
        const string ANIM_TEXT = "{\n  \"name\": \"Swim\",\n  \"duration\": 1.0,\n  \"tracks\": []\n}";
        File.WriteAllText(inputMapPath, LEGACY_INPUTMAP, new UTF8Encoding(false));
        File.WriteAllText(animPath,     ANIM_TEXT,       new UTF8Encoding(false));

        // ── 1) dry-run: 何件書き換わるかだけを出し、ファイルは触らない ──
        var dry = ProjectUpgradeRunner.RunAsync(exePath, temp.Path, dryRun: true)
                                      .GetAwaiter().GetResult();
        Check.True(dry.Launched, $"起動できること: {dry.FailureMessage}");
        Check.True(dry.HasReport, "集計行まで読めること");
        Check.Equal(2, dry.Report.Summary!.Total, "対象ファイル総数");
        Check.Equal(2, dry.Report.Summary.Upgraded, "更新する件数");
        Check.True(dry.Report.Summary.DryRun, "dry-run であること");
        Check.Equal(0, dry.Report.UnparsedLines.Count,
            $"解釈できない行が無いこと（{string.Join(" / ", dry.Report.UnparsedLines)}）");
        Check.Equal(0, dry.Report.Errors.Count, "error 行が無いこと");
        Check.Equal(LEGACY_INPUTMAP, File.ReadAllText(inputMapPath), "dry-run で書き換えないこと");
        Check.Equal(ANIM_TEXT,       File.ReadAllText(animPath),     "dry-run で書き換えないこと");

        // 形式ごとの内訳（画面の表示に使う）が数えられること。
        var byKind = dry.Report.CountByKind(UpgradeFileStatus.Upgraded);
        Check.Equal(2, byKind.Count, "形式の種類数（anim と inputmap）");

        // ── 2) 実行: 版が入る ──
        var run = ProjectUpgradeRunner.RunAsync(exePath, temp.Path, dryRun: false)
                                      .GetAwaiter().GetResult();
        Check.True(run.HasReport, "実行の集計行まで読めること");
        Check.Equal(2, run.Report.Summary!.Upgraded, "更新した件数");
        Check.True(!run.Report.Summary.DryRun, "実行であること");
        Check.Equal(ProjectUpgradeRunner.EXIT_OK, run.ExitCode, "終了コード");

        var animPeek = AssetVersionPeek.Read(File.ReadAllText(animPath), AssetFormats.Anim);
        Check.True(animPeek.HasVersionField, ".anim に版が刻まれること");
        Check.Equal(AssetFormats.Anim.CurrentVersion, animPeek.Version, ".anim の版");

        var mapPeek = AssetVersionPeek.Read(File.ReadAllText(inputMapPath), AssetFormats.InputMap);
        Check.Equal(AssetFormats.InputMap.CurrentVersion, mapPeek.Version, ".inputmap の版");

        // ── 3) 冪等: 2 回目は全件 up_to_date ──
        var again = ProjectUpgradeRunner.RunAsync(exePath, temp.Path, dryRun: true)
                                        .GetAwaiter().GetResult();
        Check.Equal(2, again.Report.Summary!.UpToDate, "2 回目は全件が現行版");
        Check.Equal(0, again.Report.Summary.Upgraded, "2 回目は更新なし");
    }

    // ============================================================
    //  補助
    // ============================================================

    /// <summary>
    /// 形式名を文字列のまま渡して <c>--migrate-json</c> を呼ぶ（綴り違いの検証用）。
    /// <see cref="MigrateJsonRunner"/> は表の形式しか受け付けないため、ここだけ直接起動する。
    /// </summary>
    /// <param name="exePath">ランタイム exe の絶対パス。</param>
    /// <param name="kind">渡す形式名。</param>
    /// <param name="json">標準入力へ書く JSON。</param>
    private static MigrateJsonResult RunWithRawKind(string exePath, string kind, string json)
    {
        var info = new System.Diagnostics.ProcessStartInfo
        {
            FileName               = exePath,
            UseShellExecute        = false,
            CreateNoWindow         = true,
            RedirectStandardInput  = true,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
        };
        info.ArgumentList.Add(MigrateJsonRunner.MIGRATE_JSON_FLAG);
        info.ArgumentList.Add(kind);

        using var process = System.Diagnostics.Process.Start(info)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        process.StandardInput.Write(json);
        process.StandardInput.Close();
        process.WaitForExit();

        var outcome = process.ExitCode switch
        {
            MigrateJsonRunner.EXIT_OK             => MigrateJsonOutcome.Success,
            MigrateJsonRunner.EXIT_FUTURE_VERSION => MigrateJsonOutcome.FutureVersion,
            MigrateJsonRunner.EXIT_BAD_USAGE      => MigrateJsonOutcome.BadUsage,
            _                                     => MigrateJsonOutcome.Failed,
        };
        return new MigrateJsonResult(outcome, stdout.Result, string.Empty, stderr.Result);
    }

    /// <summary>
    /// ランタイム exe（<c>runtime/target/debug/SEED.exe</c>）を探す。無ければ null。
    /// </summary>
    private static string? FindRuntimeExe()
    {
        var repoRoot = RuntimeExeLocator.FindRepoRoot(AppContext.BaseDirectory)
                       ?? RuntimeExeLocator.FindRepoRoot(Environment.CurrentDirectory);
        if (repoRoot is null) return null;

        var path = RuntimeExeLocator.TargetExePath(
            repoRoot, new RuntimeBuildConfig { TargetDir = DEBUG_TARGET_DIR });
        return File.Exists(path) ? path : null;
    }

    /// <summary>JSON テキストのトップレベルで最初に現れるキー名を返す（無ければ空文字）。</summary>
    /// <param name="json">対象の JSON テキスト。</param>
    private static string FirstTopLevelKey(string json)
    {
        var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(json));
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return string.Empty;
        if (!reader.Read() || reader.TokenType != JsonTokenType.PropertyName) return string.Empty;
        return reader.GetString() ?? string.Empty;
    }

    /// <summary>失敗メッセージ用に JSON の先頭だけを切り出す。</summary>
    /// <param name="text">対象のテキスト。</param>
    private static string Head(string text)
    {
        const int MAX = 160;
        var flat = text.Replace("\r\n", " ").Replace("\n", " ");
        return flat.Length <= MAX ? flat : flat[..MAX] + "…";
    }

    /// <summary>
    /// 門へランタイム exe を差し込み、テストが終わったら必ず外す。
    /// 差しっぱなしにすると、あとのテストが意図せずランタイムを起動する。
    /// </summary>
    private sealed class GatewayScope : IDisposable
    {
        /// <summary>差し込む前の値（復元用）。</summary>
        private readonly Func<string?>? _previous;

        /// <summary>exe のパスを差し込む。</summary>
        /// <param name="exePath">ランタイム exe の絶対パス。</param>
        public GatewayScope(string exePath)
        {
            _previous = AssetMigrationGateway.RuntimeExePathProvider;
            AssetMigrationGateway.RuntimeExePathProvider = () => exePath;
        }

        /// <summary>差し込みを戻す。</summary>
        public void Dispose() => AssetMigrationGateway.RuntimeExePathProvider = _previous;
    }

    /// <summary>テスト用の一時フォルダ（後始末つき）。</summary>
    private sealed class TempDir : IDisposable
    {
        /// <summary>一時フォルダの絶対パス。</summary>
        public string Path { get; }

        /// <summary>一時フォルダを作る。</summary>
        public TempDir()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), TEMP_PREFIX + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        /// <summary>一時フォルダごと消す（消せなくてもテストは失敗させない）。</summary>
        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch (Exception) { /* 掴まれている場合は次の掃除に任せる */ }
        }
    }
}
