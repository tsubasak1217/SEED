// ============================================================
//  TemplateActorTests.cs — テンプレートアクタ（カタログ・検索・まっさらな木・依存のコピー）の単体テスト
//
//  実行: dotnet run --project editor/tests/TemplateImportTests
//
//  【検証範囲】
//   1. TemplateActorCatalog          : カタログの読み込み（並び・カテゴリ・警告・サムネイルの規約）
//   2. TemplateActorSearch           : 部分一致・大文字小文字/全角半角/かなの区別なし・カテゴリとの組み合わせ
//   3. TemplateActorFlattener        : 入れ子のプレハブの展開と prefab_source / prefab_hash の除去
//   4. TemplateActorDependencyPlanner: 依存ファイルの計画とコピー（テンプレート自身は作らない・既存は触らない）
//   5. TemplateActorInstaller ほか   : 一連の準備・一時ファイル・追加先の規則・IPC の形
//   6. 実ライブラリ（templates/）    : 同梱のカタログがすべて解決でき、まっさらにできること
// ============================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using SEEDEditor.Templates;
using SEEDEditor.Templates.Actors;
using SpriteRigTests;

namespace SEEDEditor.Tests.TemplateImport;

/// <summary>テンプレートアクタのテストの登録。</summary>
public static class TemplateActorTests
{
    /// <summary>実ライブラリを探すとき、テストの exe から何階層まで遡るか。</summary>
    private const int RepositorySearchDepth = 10;

    /// <summary>同梱カタログのエントリ数の下限（2026-10-01 の登録数 = UI 28 ＋ 見本 2。減ったら載せ漏れ）。</summary>
    private const int MinimumShippedEntryCount = 30;

    /// <summary>テストを登録する。</summary>
    /// <param name="h">テストランナー。</param>
    public static void Register(TestHarness h)
    {
        RegisterCatalogTests(h);
        RegisterSearchTests(h);
        RegisterFlattenerTests(h);
        RegisterDependencyTests(h);
        RegisterInstallerTests(h);
        RegisterShippedLibraryTests(h);
        TemplateActorAddFlowTests.Register(h);
    }

    // ============================================================
    //  1. カタログ
    // ============================================================

    private static void RegisterCatalogTests(TestHarness h)
    {
        h.Add("カタログ: sort_order の順に並び、カテゴリは木の順（1 段目 → その子）", () =>
        {
            using var fx = new TemplateActorFixture();
            var catalog  = TemplateActorCatalog.Load(fx.LibraryRoot);

            var paths = catalog.Entries.Select(e => e.TemplateRelPath).ToList();
            Check.Equal(TemplateActorFixture.ValidUiEntryCount + 1, paths.Count, "有効なエントリ数（UI 4 + 3D 1）");
            Check.Equal(TemplateActorFixture.ThingPath, paths[^1], "sort_order 20 の 3D は最後");

            var keys = catalog.Categories.Select(c => c.Key).ToList();
            Check.Equal("UI", keys[0], "先頭の 1 段目");
            Check.Equal("UI/基本", keys[1], "UI の最初の子");
            Check.True(keys.IndexOf("UI/ナビゲーション") < keys.IndexOf("3D"), "UI の子はすべて 3D より前");
            Check.Equal("3D/見本", keys[^1], "最後は 3D の子");
            Check.True(catalog.Categories.All(c => c.Depth is 1 or 2), "カテゴリは 2 段まで");

            // エントリはカテゴリの並びに揃う（「板」は基本なので「ホイール」〈入力〉より前）
            Check.True(paths.IndexOf(TemplateActorFixture.PanelPath) < paths.IndexOf(TemplateActorFixture.WheelPath),
                       "同じカテゴリのエントリはまとまって並ぶ");
        });

        h.Add("カタログ: 載っていない .actor（内部の部品）は出さない", () =>
        {
            using var fx = new TemplateActorFixture();
            var catalog  = TemplateActorCatalog.Load(fx.LibraryRoot);
            var paths    = catalog.Entries.Select(e => e.TemplateRelPath).ToList();
            Check.True(!paths.Contains(TemplateActorFixture.RowPath), "行の部品が一覧に出ている");
            Check.True(!paths.Contains(TemplateActorFixture.FramePath), "枠の部品が一覧に出ている");
        });

        h.Add("カタログ: 壊れた・欠けた・重複のエントリと未来の版のカタログは飛ばして警告にする", () =>
        {
            using var fx = new TemplateActorFixture();
            var catalog  = TemplateActorCatalog.Load(fx.LibraryRoot);
            var all      = string.Join("\n", catalog.Warnings);

            Check.True(catalog.Entries.All(e => e.TemplateRelPath != TemplateActorFixture.BrokenPath), "壊れたテンプレートが載っている");
            Check.True(all.Contains("broken.actor"), "壊れたテンプレートの警告が無い");
            Check.True(all.Contains("nothing.actor"), "欠けたテンプレートの警告が無い");
            Check.True(all.Contains("2 度"), "重複の警告が無い");
            Check.True(all.Contains("future/template_actors.json"), "未来の版のカタログの警告が無い");
            Check.True(catalog.Entries.All(e => e.Name != "未来"), "未来の版のカタログを読んでいる");
            Check.True(catalog.Entries.All(e => e.Name != "重複"), "重複の後の方を採っている");
        });

        h.Add("カタログ: 2D/3D の別はテンプレートの actor_kind から決まる", () =>
        {
            using var fx = new TemplateActorFixture();
            var catalog  = TemplateActorCatalog.Load(fx.LibraryRoot);
            Check.True(catalog.Entries.First(e => e.TemplateRelPath == TemplateActorFixture.ButtonPath).Is2D, "ボタンは 2D");
            Check.True(!catalog.Entries.First(e => e.TemplateRelPath == TemplateActorFixture.ThingPath).Is2D, "actor_kind 無しは 3D");
        });

        h.Add("カタログ: サムネイルは既定で thumbnails/<ファイル名>.png、明示があればそれ", () =>
        {
            using var fx = new TemplateActorFixture();
            var catalog  = TemplateActorCatalog.Load(fx.LibraryRoot);

            var button = catalog.Entries.First(e => e.TemplateRelPath == TemplateActorFixture.ButtonPath);
            Check.Equal(TemplateActorFixture.ButtonThumbnailPath, button.ThumbnailRelPath, "既定のサムネイルの場所");
            Check.True(!button.ThumbnailExplicit, "既定の場所は明示扱いにしない");

            var wheel = catalog.Entries.First(e => e.TemplateRelPath == TemplateActorFixture.WheelPath);
            Check.Equal(TemplateActorFixture.WheelThumbnailPath, wheel.ThumbnailRelPath, "明示したサムネイル");
            Check.True(wheel.ThumbnailExplicit, "明示の印");

            // 規約の組み立てそのもの（無いファイルでも「あるはずの場所」を返す）
            Check.Equal("actors/thumbnails/thing.png",
                        TemplateActorCatalog.DefaultThumbnailRelPath("actors", TemplateActorFixture.ThingPath),
                        "規約の場所");
        });

        h.Add("カタログ: カテゴリは 2 段まで（深い段は畳む・空は「その他」）", () =>
        {
            var warnings = new List<string>();
            Check.Equal("UI|基本", string.Join("|", TemplateActorCatalog.SplitCategory(" UI / 基本 ", "t", warnings)), "前後の空白を落とす");
            Check.Equal(TemplateActorCatalogFormat.UncategorizedName,
                        TemplateActorCatalog.SplitCategory("", "t", warnings)[0], "空は「その他」");
            var deep = TemplateActorCatalog.SplitCategory("A/B/C", "t", warnings);
            Check.Equal(TemplateActorCatalogFormat.MaxCategoryDepth, deep.Count, "段数の上限");
            Check.Equal("B › C", deep[1], "深い段は最後の段へ畳む");
            Check.True(warnings.Count == 1, "深すぎる段の警告");
        });

        h.Add("カタログ: カタログとサムネイルのフォルダはインポートの一覧に出ない", () =>
        {
            using var fx = new TemplateActorFixture();
            var library  = TemplateLibrary.Load(fx.LibraryRoot);
            var ui       = library.Categories.First(c => c.FolderName == "ui");
            Check.True(ui.Entries.All(e => e.DisplayName != TemplateLibraryMetadata.TemplateActorCatalogFileName),
                       "カタログがインポートの一覧に出ている");
            Check.True(ui.Entries.All(e => e.DisplayName != TemplateLibraryMetadata.ThumbnailFolderName),
                       "サムネイルのフォルダがインポートの一覧に出ている");
            Check.True(ui.Entries.Any(e => e.DisplayName == "prefabs"), "普通のフォルダまで消えている");
        });

        h.Add("カタログ: 無いライブラリは空のカタログ（例外にしない）", () =>
        {
            var catalog = TemplateActorCatalog.Load(
                Path.Combine(Path.GetTempPath(), "seed_no_such_library_" + Guid.NewGuid().ToString("N")));
            Check.True(catalog.IsEmpty, "空判定");
            Check.Equal(0, catalog.Categories.Count, "カテゴリ数");
        });
    }

    // ============================================================
    //  2. 検索
    // ============================================================

    private static void RegisterSearchTests(TestHarness h)
    {
        h.Add("検索: 大文字小文字・全角半角・カタカナとひらがなを区別しない", () =>
        {
            Check.Equal(TemplateActorSearch.Normalize("button"), TemplateActorSearch.Normalize("ＢＵＴＴＯＮ"), "全角の英字");
            Check.Equal(TemplateActorSearch.Normalize("ボタン"), TemplateActorSearch.Normalize("ﾎﾞﾀﾝ"), "半角カナ");
            Check.Equal(TemplateActorSearch.Normalize("ボタン"), TemplateActorSearch.Normalize("ぼたん"), "ひらがな");
            Check.Equal(2, TemplateActorSearch.SplitTerms("ぼたん　丸").Count, "全角空白でも語が分かれる");
        });

        h.Add("検索: 名前・説明・検索語・パスの部分一致で絞り込み、語はすべて含むものだけ", () =>
        {
            using var fx = new TemplateActorFixture();
            var entries  = TemplateActorCatalog.Load(fx.LibraryRoot).Entries;

            Check.Equal(entries.Count, TemplateActorSearch.Filter(entries, "", null).Count, "空の検索は全件");
            Check.Equal("ボタン", TemplateActorSearch.Filter(entries, "ﾎﾞﾀﾝ", null).Single().Name, "名前（半角カナ）");
            Check.Equal("ボタン", TemplateActorSearch.Filter(entries, "押す", null).Single().Name, "説明と検索語");
            Check.Equal("ホイール", TemplateActorSearch.Filter(entries, "WHEEL", null).Single().Name, "検索語（大文字）");
            Check.Equal("もの", TemplateActorSearch.Filter(entries, "thing.actor", null).Single().Name, "パス");
            Check.Equal(0, TemplateActorSearch.Filter(entries, "ボタン 回して", null).Count, "語は AND");
        });

        h.Add("検索: カテゴリの選択と組み合わせる（1 段目はその下の段も含む）", () =>
        {
            using var fx = new TemplateActorFixture();
            var entries  = TemplateActorCatalog.Load(fx.LibraryRoot).Entries;

            Check.Equal(TemplateActorFixture.ValidUiEntryCount, TemplateActorSearch.Filter(entries, "", "UI").Count, "1 段目は子を含む");
            Check.Equal(2, TemplateActorSearch.Filter(entries, "", "UI/基本").Count, "2 段目はそのカテゴリだけ");
            Check.Equal(0, TemplateActorSearch.Filter(entries, "wheel", "UI/基本").Count, "カテゴリの外は出さない");
            Check.Equal(0, TemplateActorSearch.Filter(entries, "", "U").Count, "キーの前方一致だけでは入らない");
        });
    }

    // ============================================================
    //  3. まっさらな木
    // ============================================================

    private static void RegisterFlattenerTests(TestHarness h)
    {
        h.Add("まっさら: 全ノードの prefab_source / prefab_hash を取り除き、保存された中身は残す", () =>
        {
            using var fx = new TemplateActorFixture();
            var template = ReadObject(fx.Lib(TemplateActorFixture.PanelPath));
            var result   = TemplateActorFlattener.Flatten(template, s => TemplateActorInstaller.LoadNestedFromLibrary(fx.LibraryRoot, s));

            Check.True(!TemplateActorFlattener.ContainsPrefabLink(result.Root), "印が残っている");
            Check.Equal(4, result.StrippedCount, "印を外したノード数（ルート・Stub・Saved・Deep）");

            var saved = Child(result.Root, "Saved");
            Check.Equal("Kept", ((JsonArray)saved["components"]!)[0]!["name"]!.GetValue<string>(), "保存された中身が差し替わっている");
            Check.Equal("Deep", Child(saved, "Deep")["name"]!.GetValue<string>(), "保存された子が消えている");

            // 入力は変更しない
            Check.True(TemplateActorFlattener.ContainsPrefabLink(template), "入力の木を書き換えている");
        });

        h.Add("まっさら: 参照だけのノードは参照先の中身を差し込み、名前と変換は残す", () =>
        {
            using var fx = new TemplateActorFixture();
            var template = ReadObject(fx.Lib(TemplateActorFixture.PanelPath));
            var result   = TemplateActorFlattener.Flatten(template, s => TemplateActorInstaller.LoadNestedFromLibrary(fx.LibraryRoot, s));

            Check.Equal(1, result.ExpandedCount, "差し込んだノード数");
            var stub = Child(result.Root, "Stub");
            Check.Equal("Label", Child(stub, "Label")["name"]!.GetValue<string>(), "参照先の子が入っていない");
            Check.Equal(5.0, stub["canvas_transform"]!["position"]![0]!.GetValue<double>(), "インスタンスの変換が失われた");
            Check.Equal(0, result.Warnings.Count, "警告が出ている");
        });

        h.Add("まっさら: 見つからない参照・循環・版違いは差し込まずに警告し、印だけ外す", () =>
        {
            var root = JsonNode.Parse("""
                { "name": "R", "format_version": 2, "components": [], "children": [
                  { "name": "Lost", "prefab_source": "assets://none.actor", "components": [], "children": [] },
                  { "name": "Loop", "prefab_source": "assets://loop.actor", "components": [], "children": [] },
                  { "name": "Old",  "prefab_source": "assets://old.actor",  "components": [], "children": [] } ] }
                """)!.AsObject();
            JsonObject? Load(string s) => s switch
            {
                // 循環: 自分自身を参照だけで持つ
                "assets://loop.actor" => JsonNode.Parse("""
                    { "name": "L", "format_version": 2, "components": [], "children": [
                      { "name": "Again", "prefab_source": "assets://loop.actor", "components": [], "children": [] } ] }
                    """)!.AsObject(),
                // 版違い（版の欄なし = 1 版目。テンプレートは 2 版）
                "assets://old.actor" => JsonNode.Parse("""{ "name": "O", "components": [], "children": [] }""")!.AsObject(),
                _ => null,
            };

            var result = TemplateActorFlattener.Flatten(root, Load);
            Check.True(!TemplateActorFlattener.ContainsPrefabLink(result.Root), "印が残っている");
            Check.Equal(1, result.ExpandedCount, "Loop の 1 段目だけ差し込む");
            var text = string.Join("\n", result.Warnings);
            Check.True(text.Contains("見つからない"), "欠けた参照の警告が無い");
            Check.True(text.Contains("循環"), "循環の警告が無い");
            Check.True(text.Contains("版"), "版違いの警告が無い");
        });

        h.Add("まっさら: 2D の判定と名前はルートから取る", () =>
        {
            var root   = JsonNode.Parse("""{ "name": "Btn", "actor_kind": "Actor2D", "components": [], "children": [] }""")!.AsObject();
            var result = TemplateActorFlattener.Flatten(root, _ => null);
            Check.True(result.Is2D, "2D の判定");
            Check.Equal("Btn", result.RootName, "ルートの名前");
        });
    }

    // ============================================================
    //  4. 依存ファイル
    // ============================================================

    private static void RegisterDependencyTests(TestHarness h)
    {
        h.Add("依存: テクスチャとスクリプトが実行中に読む部品を拾い、テンプレート自身と文字列のフォルダ名は拾わない", () =>
        {
            using var fx = new TemplateActorFixture();
            var button = Plan(fx, TemplateActorFixture.ButtonPath, []);
            Check.Equal(TemplateActorFixture.ButtonTexturePath, button.Files.Single().RelPath, "ボタンの依存はテクスチャだけ");

            var wheel = Plan(fx, TemplateActorFixture.WheelPath, []);
            Check.Equal(TemplateActorFixture.RowPath, wheel.Files.Single().RelPath, "ホイールの依存は行の部品");
            Check.True(wheel.Files.All(f => f.RelPath != TemplateActorFixture.WheelPath), "テンプレート自身がコピー対象に入っている");
        });

        h.Add("依存: requires で JSON に現れない部品を足し、展開済みの入れ子は拾わない", () =>
        {
            using var fx = new TemplateActorFixture();
            var stack = Plan(fx, TemplateActorFixture.StackPath, [TemplateActorFixture.FramePath]);
            Check.Equal(TemplateActorFixture.FramePath, stack.Files.Single().RelPath, "requires の部品");

            var panel = Plan(fx, TemplateActorFixture.PanelPath, []);
            Check.Equal(0, panel.Files.Count, "展開・除去した入れ子の .actor をコピーしようとしている");
        });

        h.Add("依存: 既にあるファイルは上書きせず、無いものだけコピーする", () =>
        {
            using var fx = new TemplateActorFixture();
            fx.WriteAssetText(TemplateActorFixture.ButtonTexturePath, "project's own");
            var plan = Plan(fx, TemplateActorFixture.ButtonPath, []);
            Check.True(plan.Files.Single().AlreadyInProject, "既存の判定");
            Check.Equal(0, plan.ToCopy.Count, "既存をコピー対象にしている");

            var result = TemplateActorDependencyPlanner.Execute(plan);
            Check.Equal(0, result.Copied.Count, "コピー数");
            Check.Equal(1, result.SkippedExisting.Count, "触らなかった数");
            Check.Equal("project's own", File.ReadAllText(fx.Asset(TemplateActorFixture.ButtonTexturePath)), "既存ファイルを書き換えた");

            var wheel = TemplateActorDependencyPlanner.Execute(Plan(fx, TemplateActorFixture.WheelPath, []));
            Check.Equal(TemplateActorFixture.RowPath, wheel.Copied.Single(), "無いものはコピーする");
            Check.True(File.Exists(fx.Asset(TemplateActorFixture.RowPath)), "コピー先に無い");
        });

        h.Add("依存: ライブラリにもプロジェクトにも無い参照は欠落として知らせ、プロジェクトにあれば知らせない", () =>
        {
            using var fx = new TemplateActorFixture();
            const string json = """
                { "name": "X", "components": [ { "name": "S", "component": { "type": "SpriteComponent",
                  "data": { "texture_path": "assets://ui/textures/gone.png" } } } ], "children": [] }
                """;
            var plan = TemplateActorDependencyPlanner.Plan(fx.LibraryRoot, fx.AssetsRoot, "ui/prefabs/x.actor", json, ["prefabs_none/y.actor"]);
            Check.Equal(2, plan.Missing.Count, "欠落の数（テクスチャと requires）");

            fx.WriteAssetText("ui/textures/gone.png", "mine");
            var again = TemplateActorDependencyPlanner.Plan(fx.LibraryRoot, fx.AssetsRoot, "ui/prefabs/x.actor", json, []);
            Check.Equal(0, again.Missing.Count, "プロジェクトにある参照を欠落にしている");
        });
    }

    // ============================================================
    //  5. 一連の準備・一時ファイル・追加先・IPC
    // ============================================================

    private static void RegisterInstallerTests(TestHarness h)
    {
        h.Add("準備: 依存をコピーし、まっさらな木を一時ファイルへ書き、プロジェクトに .actor を作らない", () =>
        {
            using var fx = new TemplateActorFixture();
            var entry = TemplateActorCatalog.Load(fx.LibraryRoot).Entries.First(e => e.TemplateRelPath == TemplateActorFixture.PanelPath);
            var result = TemplateActorInstaller.Prepare(fx.LibraryRoot, fx.AssetsRoot, entry, fx.StagingRoot);

            Check.True(result.Success, $"失敗した: {result.Error}");
            Check.True(result.StagedPath.StartsWith(fx.StagingRoot, StringComparison.OrdinalIgnoreCase), "一時ファイルの場所");
            Check.Equal(".actor", Path.GetExtension(result.StagedPath), "拡張子はテンプレートにそろえる");
            var staged = ReadObject(result.StagedPath);
            Check.True(!TemplateActorFlattener.ContainsPrefabLink(staged), "一時ファイルに印が残っている");
            Check.Equal("Panel", result.RootName, "ルートの名前");
            Check.True(result.Is2D, "2D の判定");
            Check.True(!File.Exists(fx.Asset(TemplateActorFixture.PanelPath)), "プロジェクトにテンプレートの .actor を作った");
            Check.Equal(0, Directory.GetFiles(fx.AssetsRoot, "*.actor", SearchOption.AllDirectories).Length,
                        "プロジェクトに .actor ができている");
        });

        h.Add("準備: 3D のテンプレートはモデルを先にコピーする", () =>
        {
            using var fx = new TemplateActorFixture();
            var entry  = TemplateActorCatalog.Load(fx.LibraryRoot).Entries.First(e => e.TemplateRelPath == TemplateActorFixture.ThingPath);
            var result = TemplateActorInstaller.Prepare(fx.LibraryRoot, fx.AssetsRoot, entry, fx.StagingRoot);
            Check.True(result.Success, $"失敗した: {result.Error}");
            Check.True(!result.Is2D, "3D の判定");
            Check.Equal(TemplateActorFixture.ModelPath, result.Copy.Copied.Single(), "モデルのコピー");
        });

        h.Add("準備: プロジェクトのアセットフォルダが無ければ失敗を返す", () =>
        {
            using var fx = new TemplateActorFixture();
            var entry  = TemplateActorCatalog.Load(fx.LibraryRoot).Entries.First();
            var result = TemplateActorInstaller.Prepare(fx.LibraryRoot, Path.Combine(fx.AssetsRoot, "nope"), entry, fx.StagingRoot);
            Check.True(!result.Success, "失敗になっていない");
        });

        h.Add("一時ファイル: 古いものだけ掃除する", () =>
        {
            using var fx = new TemplateActorFixture();
            var fresh = TemplateActorStaging.Stage("{}", "a.actor2d", fx.StagingRoot);
            var old   = TemplateActorStaging.Stage("{}", "b.actor", fx.StagingRoot);
            File.SetLastWriteTimeUtc(old, DateTime.UtcNow - TemplateActorStaging.StaleAge - TimeSpan.FromMinutes(1));

            Check.Equal(".actor2d", Path.GetExtension(fresh), "拡張子");
            Check.Equal(1, TemplateActorStaging.CleanupStale(fx.StagingRoot, DateTime.UtcNow), "消した数");
            Check.True(File.Exists(fresh) && !File.Exists(old), "新しい方を消した／古い方が残った");
        });

        h.Add("追加先: 既存の 2D/3D の追加と同じ規則で弾く", () =>
        {
            var root       = new TemplateActorTarget();
            var canvasEdit = new TemplateActorTarget { IsCanvasEditTab = true };
            var under2D    = new TemplateActorTarget { ParentDfs = 3, ParentIs2D = true };
            var plain3D    = new TemplateActorTarget { ParentDfs = 4 };
            var canvas3D   = new TemplateActorTarget { ParentDfs = 5, ParentHasCanvas = true };

            Check.True(root.RejectReason(templateIs2D: true) is null && root.RejectReason(false) is null, "ルートはどちらも可");
            Check.Equal(TemplateActorTarget.Reject3DAtCanvasEditRoot, canvasEdit.RejectReason(false), "キャンバス編集のルートに 3D");
            Check.True(canvasEdit.RejectReason(true) is null, "キャンバス編集のルートに 2D は可");
            Check.Equal(TemplateActorTarget.Reject3DUnder2D, under2D.RejectReason(false), "2D の子に 3D");
            Check.Equal(TemplateActorTarget.Reject2DUnderPlain3D, plain3D.RejectReason(true), "Canvas 無し 3D の子に 2D");
            Check.True(canvas3D.RejectReason(true) is null && plain3D.RejectReason(false) is null, "許される組み合わせ");
        });

        h.Add("IPC: ADD_TEMPLATE_ACTOR:{wl},{親|-1},{パス}（パスは最後・カンマを含んでよい）", () =>
        {
            var root  = TemplateActorIpc.BuildAddCommand(new TemplateActorTarget { WorldLine = 2 }, @"C:\t\a,b.actor");
            Check.Equal(@"ADD_TEMPLATE_ACTOR:2,-1,C:\t\a,b.actor", root, "ルート");
            var child = TemplateActorIpc.BuildAddCommand(new TemplateActorTarget { ParentDfs = 7 }, @"C:\t\x.actor");
            Check.Equal(@"ADD_TEMPLATE_ACTOR:0,7,C:\t\x.actor", child, "子");

            bool threw = false;
            try { TemplateActorIpc.BuildAddCommand(new TemplateActorTarget(), "a\nb"); }
            catch (ArgumentException) { threw = true; }
            Check.True(threw, "改行を含むパスを受け付けた");
        });
    }

    // ============================================================
    //  6. 同梱のライブラリ（templates/）
    // ============================================================

    private static void RegisterShippedLibraryTests(TestHarness h)
    {
        h.Add("同梱: templates/ のカタログはすべて解決でき、まっさらにでき、部品の依存を拾える", () =>
        {
            var libraryRoot = FindShippedLibrary();
            if (libraryRoot is null)
            {
                Console.WriteLine("         （リポジトリの templates/ が見つからないので省略）");
                return;
            }

            var catalog = TemplateActorCatalog.Load(libraryRoot);
            Check.True(catalog.Warnings.Count == 0, "カタログの警告: " + string.Join(" / ", catalog.Warnings));
            Check.True(catalog.Entries.Count >= MinimumShippedEntryCount,
                       $"エントリ数が少ない（{catalog.Entries.Count} < {MinimumShippedEntryCount}）");
            Check.True(catalog.Entries.All(e => e.CategorySegments.Count is 1 or 2), "カテゴリの段数");
            Check.True(catalog.Entries.All(e => e.Description.Length > 0 && e.Tags.Count > 0), "説明か検索語が空のエントリがある");

            // すべてのテンプレートがまっさらにでき、印が残らない
            foreach (var entry in catalog.Entries)
            {
                var template = ReadObject(Path.Combine(libraryRoot, entry.TemplateRelPath));
                var flat     = TemplateActorFlattener.Flatten(template, s => TemplateActorInstaller.LoadNestedFromLibrary(libraryRoot, s));
                Check.True(!TemplateActorFlattener.ContainsPrefabLink(flat.Root), $"印が残った: {entry.TemplateRelPath}");
                Check.Equal(entry.Is2D, flat.Is2D, $"2D の判定の食い違い: {entry.TemplateRelPath}");
            }

            // 実行中に読む部品（テキストなので git で追跡されている）の拾い方を実データで確かめる
            var emptyAssets = Path.Combine(Path.GetTempPath(), "seed_template_actor_empty_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(emptyAssets);
            try
            {
                AssertDependsOn(catalog, libraryRoot, emptyAssets, "ui/prefabs/wheel_picker.actor", "ui/prefabs/wheel_row.actor");
                AssertDependsOn(catalog, libraryRoot, emptyAssets, "ui/prefabs/line_chart.actor", "ui/prefabs/chart_label.actor");
                AssertDependsOn(catalog, libraryRoot, emptyAssets, "ui/prefabs/screen_stack.actor", "ui/prefabs/screen_frame.actor");
                AssertDependsOn(catalog, libraryRoot, emptyAssets, "ui/prefabs/modal_host.actor", "ui/prefabs/dialog.actor");
                AssertDependsOn(catalog, libraryRoot, emptyAssets, "ui/prefabs/toast_host.actor", "ui/prefabs/toast.actor");
                AssertDependsOn(catalog, libraryRoot, emptyAssets, "ui/prefabs/list_row.actor", "ui/scripts/UiGalleryListRow.cs");

                var button = PlanFor(catalog, libraryRoot, emptyAssets, "ui/prefabs/button.actor");
                Check.Equal(0, button.Files.Count, "ボタンは依存ファイル無しのはず: " + string.Join(",", button.Files.Select(f => f.RelPath)));
            }
            finally
            {
                try { Directory.Delete(emptyAssets, recursive: true); } catch (IOException) { }
            }
        });
    }

    // ============================================================
    //  小道具
    // ============================================================

    /// <summary>JSON ファイルを読んでルートのオブジェクトを返す。</summary>
    private static JsonObject ReadObject(string path) => JsonNode.Parse(File.ReadAllText(path))!.AsObject();

    /// <summary>名前で子のノードを引く。</summary>
    private static JsonObject Child(JsonObject node, string name) =>
        ((JsonArray)node["children"]!).OfType<JsonObject>().First(c => c["name"]!.GetValue<string>() == name);

    /// <summary>フィクスチャのテンプレートをまっさらにして依存の計画を作る。</summary>
    private static TemplateActorDependencyPlan Plan(TemplateActorFixture fx, string templateRel, string[] requires)
    {
        var flat = TemplateActorFlattener.Flatten(
            ReadObject(fx.Lib(templateRel)), s => TemplateActorInstaller.LoadNestedFromLibrary(fx.LibraryRoot, s));
        return TemplateActorDependencyPlanner.Plan(
            fx.LibraryRoot, fx.AssetsRoot, templateRel, flat.Root.ToJsonString(), requires);
    }

    /// <summary>同梱カタログのエントリについて依存の計画を作る。</summary>
    private static TemplateActorDependencyPlan PlanFor(
        TemplateActorCatalog catalog, string libraryRoot, string assetsRoot, string templateRel)
    {
        var entry = catalog.Entries.First(e => e.TemplateRelPath == templateRel);
        var flat  = TemplateActorFlattener.Flatten(
            ReadObject(Path.Combine(libraryRoot, templateRel)), s => TemplateActorInstaller.LoadNestedFromLibrary(libraryRoot, s));
        return TemplateActorDependencyPlanner.Plan(
            libraryRoot, assetsRoot, templateRel, flat.Root.ToJsonString(), entry.RequiredRelPaths);
    }

    /// <summary>同梱カタログのエントリが指定のファイルを依存として拾うことを表明する。</summary>
    private static void AssertDependsOn(
        TemplateActorCatalog catalog, string libraryRoot, string assetsRoot, string templateRel, string dependencyRel)
    {
        var plan = PlanFor(catalog, libraryRoot, assetsRoot, templateRel);
        Check.True(plan.Files.Any(f => string.Equals(f.RelPath, dependencyRel, StringComparison.OrdinalIgnoreCase)),
                   $"{templateRel} の依存に {dependencyRel} が無い（{string.Join(",", plan.Files.Select(f => f.RelPath))}）");
        Check.True(plan.Files.All(f => !string.Equals(f.RelPath, templateRel, StringComparison.OrdinalIgnoreCase)),
                   $"{templateRel} 自身をコピーしようとしている");
    }

    /// <summary>テストの exe から遡って、リポジトリの templates/（カタログ付き）を探す。</summary>
    private static string? FindShippedLibrary()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < RepositorySearchDepth && dir is not null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, TemplateLibraryLocator.LibraryFolderName);
            if (File.Exists(Path.Combine(candidate, "ui", TemplateLibraryMetadata.TemplateActorCatalogFileName)))
                return candidate;
        }
        return null;
    }
}
