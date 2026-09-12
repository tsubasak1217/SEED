using System;
using System.IO;
using ProjectSystemTests;            // TempDir（一時フォルダは ProjectSystemTests のものを共用）
using SEEDEditor.Panels.ScriptEditor;
using SpriteRigTests;                // TestHarness / Check（テストランナーも共用）

namespace TextEditorLogicTests;

/// <summary>
/// スクリプトパネル（内蔵テキストエディタ）の純ロジック単体テスト
/// （docs/editor_script_panel.md）。
///
/// 検証の柱:
///   1. 拡張子 → 言語種別の対応（<see cref="TextEditableCatalog"/>）
///      ＝ ダブルクリックで開ける形式と、その着色・機能の切り替わり方
///   2. 開かせてはいけない形式（.scene / .actor / .actor2d）を必ず拒むこと
///   3. サイズ上限（巨大ファイルを読み取り専用にする境界）
///   4. JSON が壊れていても組み込み既定で動き続けること
///   5. 言語ごとの機能の有無（<see cref="EditorLanguages"/>）
/// </summary>
public static class Program
{
    /// <summary>エントリポイント。全テストを実行し、失敗があれば終了コード 1 を返す。</summary>
    public static int Main()
    {
        var harness = new TestHarness();

        // ── 拡張子 → 言語種別 ───────────────────────────────
        harness.Add("コード系（.cs / .wgsl）は従来どおりの言語になる",     LanguageCode);
        harness.Add("テキスト系（.json/.txt/.csv/.md）が開ける",           LanguageText);
        harness.Add("JSON 系の独自拡張子は JSON として扱う",               LanguageJsonFamily);
        harness.Add("拡張子の大文字小文字は区別しない",                    LanguageCaseInsensitive);
        harness.Add("ドット無しで問い合わせても引ける",                    LanguageWithoutDot);
        harness.Add("未知の拡張子は開けない（既定は C# 扱い）",            LanguageUnknown);

        // ── 開かせてはいけない形式 ──────────────────────────
        harness.Add("シーン・アクタはテキスト編集対象にしない",            ForbiddenSceneAndActor);
        harness.Add("JSON に書かれていてもシーン・アクタは拒否する",       ForbiddenEvenFromJson);
        harness.Add("地形のバイナリ中間データは編集対象にしない",          ForbiddenTerrainBinaries);

        // ── サイズ上限 ──────────────────────────────────────
        harness.Add("上限ちょうどは編集可・1 バイト超で読み取り専用",      SizeLimitBoundary);
        harness.Add("上限は JSON で変えられる",                            SizeLimitFromJson);
        harness.Add("0 以下の上限は既定へ丸める",                          SizeLimitInvalid);

        // ── カタログの読み込み ──────────────────────────────
        harness.Add("JSON で対応拡張子を差し替えられる",                   CatalogFromJson);
        harness.Add("未知の言語はプレーンテキストとして読む",              CatalogUnknownLanguage);
        harness.Add("壊れた JSON は組み込み既定へフォールバックする",      CatalogBrokenJson);
        harness.Add("ファイルが無ければ組み込み既定へフォールバックする",  CatalogMissingFile);
        harness.Add("空の一覧は組み込み既定へフォールバックする",          CatalogEmptyList);
        harness.Add("同梱の text_editable_extensions.json が既定と一致する", CatalogShippedFile);

        // ── 言語ごとの機能の有無 ────────────────────────────
        harness.Add("Roslyn 機能は C# だけ",                               CapabilityRoslyn);
        harness.Add("補完を持つのは C# と WGSL だけ",                      CapabilityCompletion);

        // ── 共有カタログの差し替え（静的状態を変えるので最後）────
        harness.Add("UseCatalog で全体の判定が切り替わる",                 EditorLanguagesUseCatalog);

        return harness.Run();
    }

    // ── 拡張子 → 言語種別 ───────────────────────────────────────

    /// <summary>組み込み既定のカタログ（JSON を読まない）。</summary>
    private static TextEditableCatalog Catalog() => TextEditableCatalog.BuiltIn();

    /// <summary>拡張子が指定の言語として引けることを表明する。</summary>
    private static void AssertLanguage(string extension, EditorLanguage expected)
    {
        var catalog = Catalog();
        Check.True(catalog.IsEditableExtension(extension), $"開けるはず: {extension}");
        Check.True(catalog.TryGetLanguage(extension, out var actual), $"言語が引けるはず: {extension}");
        Check.Equal(expected, actual, $"{extension} の言語");
    }

    private static void LanguageCode()
    {
        AssertLanguage(".cs",   EditorLanguage.CSharp);
        AssertLanguage(".wgsl", EditorLanguage.Wgsl);
    }

    private static void LanguageText()
    {
        AssertLanguage(".json", EditorLanguage.Json);
        AssertLanguage(".txt",  EditorLanguage.PlainText);
        AssertLanguage(".csv",  EditorLanguage.Csv);
        AssertLanguage(".md",   EditorLanguage.Markdown);
    }

    private static void LanguageJsonFamily()
    {
        // 中身が JSON の独自拡張子。JSON として着色し、コード解析系の機能は付けない。
        AssertLanguage(".inputmap", EditorLanguage.Json);
        AssertLanguage(".icons",    EditorLanguage.Json);
        AssertLanguage(".anim",     EditorLanguage.Json);
    }

    private static void LanguageCaseInsensitive()
    {
        AssertLanguage(".JSON", EditorLanguage.Json);
        AssertLanguage(".Cs",   EditorLanguage.CSharp);
    }

    private static void LanguageWithoutDot()
    {
        var catalog = Catalog();
        Check.True(catalog.IsEditableExtension("json"), "ドット無しでも引ける");
        Check.Equal(EditorLanguage.Json, catalog.LanguageFromPath("a/b/props.json"), "パスからも引ける");
    }

    private static void LanguageUnknown()
    {
        var catalog = Catalog();
        Check.True(!catalog.IsEditableExtension(".png"),  "画像は開けない");
        Check.True(!catalog.IsEditableExtension(""),      "拡張子なしは開けない");
        Check.True(!catalog.IsEditableExtension(null),    "null でも落ちない");
        // 明示的に開いた場合の既定は従来どおり C#（F12 の定義ジャンプ等が依存している）
        Check.Equal(EditorLanguage.CSharp, catalog.LanguageFromPath("engine/Api.unknown"), "未知は C# 扱い");
    }

    // ── 開かせてはいけない形式 ──────────────────────────────────

    private static void ForbiddenSceneAndActor()
    {
        var catalog = Catalog();
        // エディタが常時読み込んで編集している実体。テキストで書き換えると保存が取り合いになる。
        Check.True(!catalog.IsEditableExtension(".scene"),   ".scene は開けない");
        Check.True(!catalog.IsEditableExtension(".actor"),   ".actor は開けない");
        Check.True(!catalog.IsEditableExtension(".actor2d"), ".actor2d は開けない");
    }

    private static void ForbiddenEvenFromJson()
    {
        using var temp = new TempDir();
        var path = temp.Combine(TextEditableCatalog.FileName);
        File.WriteAllText(path, """
        {
          "format_version": 1,
          "extensions": [
            { "extension": ".scene", "language": "json" },
            { "extension": ".json",  "language": "json" }
          ]
        }
        """);

        var catalog = TextEditableCatalog.Load(path);
        Check.True(!catalog.IsEditableExtension(".scene"), "JSON に書いても拒否される");
        Check.True(catalog.IsEditableExtension(".json"),   "同じファイルの他の項目は生きる");
        Check.True(catalog.Warnings.Count > 0,             "拒否した理由が警告に残る");
    }

    private static void ForbiddenTerrainBinaries()
    {
        var catalog = Catalog();
        // .tvox / .tscatter / .tcover はエンジンが書く独自バイナリ（magic TVOX / TSCT / TCOV）。
        // テキストとして開くと文字化けし、保存すればファイルが壊れる。
        Check.True(!catalog.IsEditableExtension(".tvox"),     ".tvox は開けない");
        Check.True(!catalog.IsEditableExtension(".tscatter"), ".tscatter は開けない");
        Check.True(!catalog.IsEditableExtension(".tcover"),   ".tcover は開けない");
    }

    // ── サイズ上限 ──────────────────────────────────────────────

    private static void SizeLimitBoundary()
    {
        var catalog = Catalog();
        long limit = catalog.MaxEditableBytes;
        Check.Equal(TextEditableCatalog.BuiltInMaxEditableBytes, limit, "既定の上限");
        Check.True(!catalog.ExceedsEditableSize(limit),     "上限ちょうどは編集できる");
        Check.True(catalog.ExceedsEditableSize(limit + 1),  "1 バイト超は読み取り専用");
        Check.True(!catalog.ExceedsEditableSize(0),         "空ファイルは編集できる");
    }

    private static void SizeLimitFromJson()
    {
        using var temp = new TempDir();
        var path = temp.Combine(TextEditableCatalog.FileName);
        File.WriteAllText(path, """
        {
          "format_version": 1,
          "max_editable_bytes": 100,
          "extensions": [ { "extension": ".txt", "language": "text" } ]
        }
        """);

        var catalog = TextEditableCatalog.Load(path);
        Check.Equal(100L, catalog.MaxEditableBytes,     "JSON の上限が効く");
        Check.True(catalog.ExceedsEditableSize(101),    "101 バイトは超過");
    }

    private static void SizeLimitInvalid()
    {
        using var temp = new TempDir();
        var path = temp.Combine(TextEditableCatalog.FileName);
        File.WriteAllText(path, """
        {
          "format_version": 1,
          "max_editable_bytes": 0,
          "extensions": [ { "extension": ".txt", "language": "text" } ]
        }
        """);

        var catalog = TextEditableCatalog.Load(path);
        Check.Equal(TextEditableCatalog.BuiltInMaxEditableBytes, catalog.MaxEditableBytes,
                    "0 は既定へ丸める（何も編集できなくなるのを防ぐ）");
        Check.True(catalog.Warnings.Count > 0, "理由が警告に残る");
    }

    // ── カタログの読み込み ──────────────────────────────────────

    private static void CatalogFromJson()
    {
        using var temp = new TempDir();
        var path = temp.Combine(TextEditableCatalog.FileName);
        File.WriteAllText(path, """
        {
          "format_version": 1,
          "extensions": [
            { "extension": "log",   "language": "text" },
            { "extension": ".props", "language": "json", "note": "独自形式" }
          ]
        }
        """);

        var catalog = TextEditableCatalog.Load(path);
        Check.Equal(0, catalog.Warnings.Count, "警告なしで読める");
        Check.True(catalog.SourcePath != null, "読み込み元が記録される");
        Check.Equal(EditorLanguage.PlainText, catalog.LanguageFromPath("a.log"),   "ドット無しの指定も効く");
        Check.Equal(EditorLanguage.Json,      catalog.LanguageFromPath("a.props"), "独自形式を JSON にできる");
        Check.True(!catalog.IsEditableExtension(".csv"), "JSON で差し替えたので既定の一覧は効かない");
    }

    private static void CatalogUnknownLanguage()
    {
        using var temp = new TempDir();
        var path = temp.Combine(TextEditableCatalog.FileName);
        File.WriteAllText(path, """
        {
          "format_version": 1,
          "extensions": [ { "extension": ".foo", "language": "klingon" } ]
        }
        """);

        var catalog = TextEditableCatalog.Load(path);
        Check.Equal(EditorLanguage.PlainText, catalog.LanguageFromPath("a.foo"), "未知の言語は無着色で開く");
        Check.True(catalog.Warnings.Count > 0, "理由が警告に残る");
    }

    private static void CatalogBrokenJson()
    {
        using var temp = new TempDir();
        var path = temp.Combine(TextEditableCatalog.FileName);
        File.WriteAllText(path, "{ これは JSON ではない");

        var catalog = TextEditableCatalog.Load(path);
        Check.True(catalog.Warnings.Count > 0,            "理由が警告に残る");
        Check.True(catalog.IsEditableExtension(".cs"),    "組み込み既定で動き続ける");
        Check.True(catalog.IsEditableExtension(".wgsl"),  "シェーダーも従来どおり開ける");
    }

    private static void CatalogMissingFile()
    {
        using var temp = new TempDir();
        var catalog = TextEditableCatalog.LoadFromDir(temp.Path);
        Check.True(catalog.Warnings.Count > 0,         "理由が警告に残る");
        Check.True(catalog.IsEditableExtension(".cs"), "組み込み既定で動き続ける");
    }

    private static void CatalogEmptyList()
    {
        using var temp = new TempDir();
        var path = temp.Combine(TextEditableCatalog.FileName);
        File.WriteAllText(path, """{ "format_version": 1, "extensions": [] }""");

        var catalog = TextEditableCatalog.Load(path);
        Check.True(catalog.IsEditableExtension(".cs"), "何も開けないエディタにはしない");
        Check.True(catalog.Warnings.Count > 0,         "理由が警告に残る");
    }

    /// <summary>
    /// リポジトリに入っている実物（editor/config/text_editable_extensions.json）を読む。
    ///
    /// 組み込み既定は「JSON を消しても動く」ための写しなので、両者がずれると
    /// 「開発環境と配布環境で開ける形式が違う」という気付きにくい差になる。
    /// </summary>
    private static void CatalogShippedFile()
    {
        var configDir = FindEditorConfigDir();
        var catalog   = TextEditableCatalog.LoadFromDir(configDir);

        Check.True(catalog.SourcePath != null, "同梱ファイルを実際に読めている");
        Check.Equal(0, catalog.Warnings.Count, "同梱ファイルは警告なしで読めること");

        var builtIn = TextEditableCatalog.BuiltIn();
        Check.Equal(builtIn.MaxEditableBytes, catalog.MaxEditableBytes, "サイズ上限が既定と一致");
        Check.Equal(builtIn.Extensions.Count, catalog.Extensions.Count, "対応拡張子の件数が既定と一致");

        foreach (var ext in builtIn.Extensions)
        {
            Check.True(builtIn.TryGetLanguage(ext, out var expected), $"既定側で引ける: {ext}");
            Check.True(catalog.TryGetLanguage(ext, out var actual),   $"同梱側でも引ける: {ext}");
            Check.Equal(expected, actual, $"{ext} の言語が一致");
        }
    }

    /// <summary>
    /// editor/config フォルダを探す。テストの出力先（editor/tests/&lt;Proj&gt;/bin/&lt;Cfg&gt;/&lt;tfm&gt;）から
    /// 上へ辿り、SEEDEditor.csproj のあるフォルダ（＝ editor）を見つけて config を返す。
    /// </summary>
    /// <returns>editor/config の絶対パス。</returns>
    private static string FindEditorConfigDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "SEEDEditor.csproj")))
                return Path.Combine(dir.FullName, "config");
            dir = dir.Parent;
        }
        throw new AssertionException(
            $"editor フォルダが見つかりません（起点: {AppContext.BaseDirectory}）");
    }

    // ── 言語ごとの機能の有無 ────────────────────────────────────

    private static void CapabilityRoslyn()
    {
        // Roslyn 機能＝IntelliSense・診断・デバッグ・整形・AI 補完・保存後のコンパイル検証。
        Check.True(EditorLanguages.UsesRoslyn(EditorLanguage.CSharp),     "C# は有効");
        Check.True(!EditorLanguages.UsesRoslyn(EditorLanguage.Wgsl),      "WGSL は無効");
        Check.True(!EditorLanguages.UsesRoslyn(EditorLanguage.Json),      "JSON は無効");
        Check.True(!EditorLanguages.UsesRoslyn(EditorLanguage.Csv),       "CSV は無効");
        Check.True(!EditorLanguages.UsesRoslyn(EditorLanguage.Markdown),  "Markdown は無効");
        Check.True(!EditorLanguages.UsesRoslyn(EditorLanguage.PlainText), "テキストは無効");
    }

    private static void CapabilityCompletion()
    {
        // ここが緩むと、JSON を開いたときに WGSL の候補（静的辞書）が出てしまう。
        Check.True(EditorLanguages.HasCompletion(EditorLanguage.CSharp),     "C#（Roslyn）");
        Check.True(EditorLanguages.HasCompletion(EditorLanguage.Wgsl),       "WGSL（静的辞書）");
        Check.True(!EditorLanguages.HasCompletion(EditorLanguage.Json),      "JSON には候補源が無い");
        Check.True(!EditorLanguages.HasCompletion(EditorLanguage.Csv),       "CSV には候補源が無い");
        Check.True(!EditorLanguages.HasCompletion(EditorLanguage.Markdown),  "Markdown には候補源が無い");
        Check.True(!EditorLanguages.HasCompletion(EditorLanguage.PlainText), "テキストには候補源が無い");
    }

    // ── 共有カタログの差し替え ──────────────────────────────────

    private static void EditorLanguagesUseCatalog()
    {
        // 既定では .csv が開ける
        Check.True(EditorLanguages.IsEditableExtension(".csv"), "差し替え前（組み込み既定）");

        using var temp = new TempDir();
        var path = temp.Combine(TextEditableCatalog.FileName);
        File.WriteAllText(path, """
        {
          "format_version": 1,
          "extensions": [ { "extension": ".cs", "language": "csharp" } ]
        }
        """);

        EditorLanguages.UseCatalog(TextEditableCatalog.Load(path));
        Check.True(!EditorLanguages.IsEditableExtension(".csv"), "差し替え後は新しいカタログで判定する");
        Check.Equal(EditorLanguage.CSharp, EditorLanguages.FromPath("a.cs"), "FromPath も追従する");

        // null を渡しても壊れない（読み込み失敗時に呼ばれても現状維持）
        EditorLanguages.UseCatalog(null);
        Check.True(EditorLanguages.IsEditableExtension(".cs"), "null 差し替えは無視される");
    }
}
