using System;
using System.Collections.Generic;
using System.IO;
using ProjectSystemTests;                         // TempDir（一時フォルダは共用）
using SEEDEditor.Panels.ScriptEditor.Session;
using SpriteRigTests;                             // TestHarness / Check（テストランナーは共用）

namespace TextEditorLogicTests;

/// <summary>
/// スクリプトエディタの「開いていたタブのセッション復元」の単体テスト
/// （docs/editor_script_panel.md の「タブのセッション復元」節）。
///
/// 守りたい性質:
///   1. 保存 → 読み直しで同じ状態に戻る（往復できる）
///   2. プロジェクトを移動しても壊れない（ルート内は相対・ルート外は絶対で保存する）
///   3. 壊れた JSON・空ファイル・ファイル無しで**例外を出さず**「復元なし」に落ちる
///      ＝ この機能の失敗でエディタが起動しないことが無い
///   4. 異常値（0 以下・NaN・巨大値・範囲外の添字）が必ず正規化される
///   5. ルート外へ出る相対パス（`..`）は捨てる
///      ＝ 壊れた・細工された JSON で無関係なファイルを勝手に開かない
///   6. 上限枚数で頭打ちになる（肥大した JSON で起動が重くならない）
/// </summary>
public static class SessionStoreTests
{
    // ── テストで使う固定値（マジックナンバー回避）────────────────

    /// <summary>テスト用のプロジェクトルート配下のスクリプト（相対で保存されるはず）。</summary>
    private const string InsideRelative = @"assets\Scripts\PlayerMove.cs";

    /// <summary>保存後に期待する相対表記（スラッシュ区切りへ正規化される）。</summary>
    private const string InsideStored = "assets/Scripts/PlayerMove.cs";

    /// <summary>テストで使うキャレット位置とスクロール位置。</summary>
    private const int    SampleLine   = 42;
    private const int    SampleColumn = 7;
    private const double SampleScroll = 1234.5;

    /// <summary>浮動小数の比較許容誤差（JSON の往復で桁落ちしない想定の確認用）。</summary>
    private const double ScrollTolerance = 0.001;

    /// <summary>このファイルのテストをランナーへ登録する。</summary>
    /// <param name="harness">登録先のテストランナー。</param>
    public static void Register(TestHarness harness)
    {
        // ── 往復 ────────────────────────────────────────────
        harness.Add("保存 → 読み直しで同じ内容に戻る",                     RoundTrip);
        harness.Add("置き場は <ルート>/cache/editor/script_editor_session.json", FilePathLayout);
        harness.Add("JSON のキー名と format_version が仕様どおり",         JsonShape);

        // ── パスの相対化・絶対化 ────────────────────────────
        harness.Add("ルート内は相対・ルート外は絶対で保存する",            RelativeInsideAbsoluteOutside);
        harness.Add("読み込みで相対が絶対へ戻る",                          RelativeBecomesAbsolute);
        harness.Add("`..` でルート外へ出る相対パスは捨てる",               EscapingRelativeIsDropped);
        harness.Add("プロジェクトルートが空なら復元しない",                NoProjectRootNoRestore);

        // ── 壊れた入力 ──────────────────────────────────────
        harness.Add("存在しないファイルは復元なし（例外なし）",            MissingFileIsNoRestore);
        harness.Add("空ファイルは復元なし（例外なし）",                    EmptyFileIsNoRestore);
        harness.Add("壊れた JSON は復元なし＋理由が残る（例外なし）",      BrokenJsonIsNoRestore);
        harness.Add("未知の format_version でも読める",                    UnknownFormatVersionStillReads);

        // ── 値の正規化 ──────────────────────────────────────
        harness.Add("行・桁の 0 以下と巨大値が正規化される",               CaretIsSanitized);
        harness.Add("スクロールの NaN・無限大・負値が 0 になる",           ScrollIsSanitized);

        // ── 間引きと添字 ────────────────────────────────────
        harness.Add("タブ数が上限で頭打ちになる",                          TabsAreCappedOnRestore);
        harness.Add("保存側では上限で切らない",                            BuildDoesNotCapTabs);
        harness.Add("アクティブ添字が範囲外なら先頭に倒れる",              ActiveIndexFallsBackToFirst);
        harness.Add("タブが 1 枚も無ければ保存内容を作らない",             EmptyTabsBuildsNothing);
        harness.Add("保存できないタブを飛ばしてもアクティブが追従する",    ActiveFollowsDroppedTabs);
    }

    // ── 補助 ────────────────────────────────────────────────────

    /// <summary>テスト用のスナップショットを作る。</summary>
    /// <param name="absolutePath">ファイルの絶対パス。</param>
    /// <param name="line">キャレットの行。</param>
    /// <param name="column">キャレットの桁。</param>
    /// <param name="scroll">縦スクロール位置。</param>
    /// <param name="readOnly">読み取り専用タブか。</param>
    private static ScriptEditorSessionTabSnapshot Tab(
        string absolutePath,
        int line       = SampleLine,
        int column     = SampleColumn,
        double scroll  = SampleScroll,
        bool readOnly  = false)
        => new(absolutePath, line, column, scroll, readOnly);

    /// <summary>一時フォルダ内のパスを絶対パスとして組み立てる（実ファイルは作らない）。</summary>
    /// <param name="temp">一時フォルダ。</param>
    /// <param name="relative">ルートからの相対パス。</param>
    private static string Abs(TempDir temp, string relative)
        => Path.GetFullPath(Path.Combine(temp.Path, relative));

    /// <summary>組み立て → 保存 → 読み込み → 復元指示、までを一気に通す。</summary>
    /// <param name="root">プロジェクトルート。</param>
    /// <param name="tabs">保存するタブ。</param>
    /// <param name="activeIndex">アクティブタブの添字。</param>
    /// <returns>復元指示（復元するものが無ければ null）。</returns>
    private static ScriptEditorSessionState? SaveAndReload(
        string root, IReadOnlyList<ScriptEditorSessionTabSnapshot> tabs, int activeIndex)
    {
        var filePath = ScriptEditorSessionStore.FilePathFor(root);
        Check.True(filePath != null, "置き場が決まる");

        var document = ScriptEditorSessionStore.Build(root, tabs, activeIndex);
        Check.True(document != null, "保存内容が作れる");
        Check.True(ScriptEditorSessionStore.Save(filePath, document, out var saveError),
                   $"保存できる（{saveError}）");

        var loaded = ScriptEditorSessionStore.LoadFile(filePath, out _);
        return ScriptEditorSessionStore.Resolve(loaded, root);
    }

    // ── 往復 ────────────────────────────────────────────────────

    private static void RoundTrip()
    {
        using var temp = new TempDir();
        var first  = Abs(temp, InsideRelative);
        var second = Abs(temp, @"assets\Scripts\EnemyAI.cs");

        var state = SaveAndReload(temp.Path, new[]
        {
            Tab(first),
            Tab(second, line: 3, column: 1, scroll: 0.0, readOnly: true),
        }, activeIndex: 1);

        Check.True(state != null, "復元できる");
        Check.Equal(2, state!.Tabs.Count,      "タブ数");
        Check.Equal(1, state.ActiveTabIndex,   "アクティブ添字");

        // 並び順（＝開いた順）が保たれること。並べ替え機能が無いので、この順がそのまま表示順。
        Check.Equal(first,  state.Tabs[0].FilePath, "1 枚目のパス");
        Check.Equal(second, state.Tabs[1].FilePath, "2 枚目のパス");

        Check.Equal(SampleLine,   state.Tabs[0].CaretLine,   "1 枚目の行");
        Check.Equal(SampleColumn, state.Tabs[0].CaretColumn, "1 枚目の桁");
        Check.Close(SampleScroll, state.Tabs[0].ScrollOffset, ScrollTolerance, "1 枚目のスクロール");
        Check.True(!state.Tabs[0].IsReadOnly, "1 枚目は編集可");
        Check.True(state.Tabs[1].IsReadOnly,  "2 枚目は読み取り専用");
    }

    private static void FilePathLayout()
    {
        using var temp = new TempDir();
        var filePath = ScriptEditorSessionStore.FilePathFor(temp.Path);

        var expected = Path.Combine(
            Path.GetFullPath(temp.Path),
            ScriptEditorSessionStore.CACHE_DIR_NAME,
            ScriptEditorSessionStore.EDITOR_DIR_NAME,
            ScriptEditorSessionStore.FileName);
        Check.Equal(expected, filePath, "保存先");
        Check.Equal("script_editor_session.json", ScriptEditorSessionStore.FileName, "ファイル名");

        // プロジェクト未確定（空文字）なら置き場が無い＝保存も復元もしない。
        Check.True(ScriptEditorSessionStore.FilePathFor("")   is null, "空文字なら置き場なし");
        Check.True(ScriptEditorSessionStore.FilePathFor(null) is null, "null なら置き場なし");
    }

    /// <summary>
    /// JSON のキー名を実ファイルで確かめる。
    /// キー名を変えると、旧エディタで保存したセッションが読めなくなる（気付きにくい）ので、
    /// ここで固定しておく。
    /// </summary>
    private static void JsonShape()
    {
        using var temp = new TempDir();
        var filePath = ScriptEditorSessionStore.FilePathFor(temp.Path)!;
        var document = ScriptEditorSessionStore.Build(
            temp.Path, new[] { Tab(Abs(temp, InsideRelative)) }, activeTabIndex: 0);
        Check.True(ScriptEditorSessionStore.Save(filePath, document, out _), "保存できる");

        var json = File.ReadAllText(filePath);
        foreach (var key in new[] { "format_version", "active_tab", "tabs", "path", "line", "column", "scroll", "read_only" })
            Check.True(json.Contains($"\"{key}\""), $"キー {key} が書かれている");

        Check.Equal(1, ScriptEditorSessionStore.SupportedFormatVersion, "書式バージョン");
        Check.True(json.Contains("\"format_version\": 1"), "format_version が 1 で書かれている");

        // 一時ファイル（原子的置換に使う .tmp）を残していないこと。
        Check.True(!File.Exists(filePath + ".tmp"), "一時ファイルを残さない");
    }

    // ── パスの相対化・絶対化 ────────────────────────────────────

    private static void RelativeInsideAbsoluteOutside()
    {
        using var temp = new TempDir();
        // ルート外＝別フォルダ（F12 で開くエンジン API のソースに相当）
        var outside = Path.GetFullPath(Path.Combine(temp.Path, "..", "SEED_outside_ScriptBridge.cs"));

        var document = ScriptEditorSessionStore.Build(temp.Path, new[]
        {
            Tab(Abs(temp, InsideRelative)),
            Tab(outside, readOnly: true),
        }, activeTabIndex: 0);

        Check.True(document != null, "保存内容が作れる");
        Check.Equal(InsideStored, document!.Tabs[0].Path, "ルート内は相対（スラッシュ区切り）");
        Check.True(!Path.IsPathRooted(document.Tabs[0].Path), "ルート内は絶対パスにしない");
        Check.True(Path.IsPathRooted(document.Tabs[1].Path),  "ルート外は絶対パスのまま");

        // ルート外のタブも、読み直せば元の絶対パスへ戻ること。
        var state = ScriptEditorSessionStore.Resolve(document, temp.Path);
        Check.True(state != null, "復元できる");
        Check.Equal(outside, state!.Tabs[1].FilePath, "ルート外のパスが往復する");
    }

    private static void RelativeBecomesAbsolute()
    {
        using var temp = new TempDir();
        var document = new ScriptEditorSessionDocument
        {
            FormatVersion = ScriptEditorSessionStore.SupportedFormatVersion,
            ActiveTab     = 0,
            Tabs          = new List<ScriptEditorSessionTabFile>
            {
                new() { Path = InsideStored, Line = 1, Column = 1 },
            },
        };

        var state = ScriptEditorSessionStore.Resolve(document, temp.Path);
        Check.True(state != null, "復元できる");
        Check.True(Path.IsPathRooted(state!.Tabs[0].FilePath), "絶対パスへ戻る");
        Check.Equal(Abs(temp, InsideRelative), state.Tabs[0].FilePath, "ルートと結合した結果");
    }

    private static void EscapingRelativeIsDropped()
    {
        using var temp = new TempDir();
        var document = new ScriptEditorSessionDocument
        {
            ActiveTab = 0,
            Tabs      = new List<ScriptEditorSessionTabFile>
            {
                // ルートの外へ出る相対パス（壊れた／細工された JSON）
                new() { Path = "../outside.cs",                 Line = 1, Column = 1 },
                new() { Path = "assets/../../elsewhere/x.cs",   Line = 1, Column = 1 },
                // ルート内で折り返す相対パスは正当なので残る
                new() { Path = "assets/sub/../Scripts/Ok.cs",   Line = 1, Column = 1 },
            },
        };

        var state = ScriptEditorSessionStore.Resolve(document, temp.Path);
        Check.True(state != null, "残ったタブがあるので復元できる");
        Check.Equal(1, state!.Tabs.Count, "ルート外へ出る 2 枚は捨てられる");
        Check.Equal(Abs(temp, @"assets\Scripts\Ok.cs"), state.Tabs[0].FilePath, "ルート内で折り返す相対は残る");
    }

    private static void NoProjectRootNoRestore()
    {
        var document = new ScriptEditorSessionDocument
        {
            ActiveTab = 0,
            Tabs      = new List<ScriptEditorSessionTabFile> { new() { Path = InsideStored } },
        };

        // ルートが分からなければ相対パスは絶対化できない＝そのタブは開けない。
        Check.True(ScriptEditorSessionStore.Resolve(document, "")   is null, "空文字のルート");
        Check.True(ScriptEditorSessionStore.Resolve(document, null) is null, "null のルート");
    }

    // ── 壊れた入力 ──────────────────────────────────────────────

    private static void MissingFileIsNoRestore()
    {
        using var temp = new TempDir();
        var filePath = ScriptEditorSessionStore.FilePathFor(temp.Path)!;

        // 初回起動そのもの。ファイルが無いのは正常なので警告も出さない。
        var loaded = ScriptEditorSessionStore.LoadFile(filePath, out var warning);
        Check.True(loaded is null,  "復元なし");
        Check.True(warning is null, "初回起動は警告にしない");
        Check.True(ScriptEditorSessionStore.Resolve(loaded, temp.Path) is null, "復元指示も無い");
    }

    private static void EmptyFileIsNoRestore()
    {
        using var temp = new TempDir();
        var filePath = ScriptEditorSessionStore.FilePathFor(temp.Path)!;
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        File.WriteAllText(filePath, "");

        var loaded = ScriptEditorSessionStore.LoadFile(filePath, out _);
        Check.True(loaded is null, "空ファイルは復元なし（例外を出さない）");

        // 中身が `null` とだけ書かれている場合も同じ。
        File.WriteAllText(filePath, "null");
        Check.True(ScriptEditorSessionStore.LoadFile(filePath, out _) is null, "null 本文も復元なし");
    }

    private static void BrokenJsonIsNoRestore()
    {
        using var temp = new TempDir();
        var filePath = ScriptEditorSessionStore.FilePathFor(temp.Path)!;
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        File.WriteAllText(filePath, "{ これは JSON ではない");

        var loaded = ScriptEditorSessionStore.LoadFile(filePath, out var warning);
        Check.True(loaded is null,      "復元なし");
        Check.True(warning is not null, "理由が残る");

        // 型が違う（tabs が配列でない）場合も例外で落ちない。
        File.WriteAllText(filePath, """{ "format_version": 1, "tabs": 5 }""");
        Check.True(ScriptEditorSessionStore.LoadFile(filePath, out _) is null, "型違いも復元なし");

        // "tabs": null を手書きされても落ちない（null 安全化が効く）。
        File.WriteAllText(filePath, """{ "format_version": 1, "tabs": null }""");
        var nulled = ScriptEditorSessionStore.LoadFile(filePath, out _);
        Check.True(nulled is not null, "読めること自体は成功する");
        Check.Equal(0, nulled!.Tabs.Count, "タブは空になる");
        Check.True(ScriptEditorSessionStore.Resolve(nulled, temp.Path) is null, "復元するものは無い");
    }

    private static void UnknownFormatVersionStillReads()
    {
        using var temp = new TempDir();
        var filePath = ScriptEditorSessionStore.FilePathFor(temp.Path)!;
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        File.WriteAllText(filePath, $$"""
        {
          "format_version": 999,
          "active_tab": 0,
          "tabs": [
            { "path": "{{InsideStored}}", "line": 5, "column": 2, "scroll": 10.0, "read_only": false,
              "future_field": "未来のエディタが足した項目" }
          ]
        }
        """);

        var loaded = ScriptEditorSessionStore.LoadFile(filePath, out var warning);
        Check.True(loaded is not null, "未来のバージョンでも読める（捨てない）");
        Check.True(warning is not null, "未知バージョンである旨は残す");

        var state = ScriptEditorSessionStore.Resolve(loaded, temp.Path);
        Check.True(state != null, "復元できる");
        Check.Equal(5, state!.Tabs[0].CaretLine, "既知の項目はそのまま読める");
    }

    // ── 値の正規化 ──────────────────────────────────────────────

    private static void CaretIsSanitized()
    {
        using var temp = new TempDir();
        var document = new ScriptEditorSessionDocument
        {
            ActiveTab = 0,
            Tabs      = new List<ScriptEditorSessionTabFile>
            {
                new() { Path = InsideStored,               Line = 0,             Column = -5 },
                new() { Path = "assets/Scripts/Other.cs",  Line = int.MaxValue,  Column = int.MinValue },
            },
        };

        var state = ScriptEditorSessionStore.Resolve(document, temp.Path);
        Check.True(state != null, "復元できる");

        Check.Equal(ScriptEditorSessionStore.MinCaretLine,   state!.Tabs[0].CaretLine,   "0 行 → 1 行");
        Check.Equal(ScriptEditorSessionStore.MinCaretColumn, state.Tabs[0].CaretColumn,  "負の桁 → 1 桁");
        Check.Equal(ScriptEditorSessionStore.MaxCaretPosition, state.Tabs[1].CaretLine,  "巨大な行は上限で頭打ち");
        Check.Equal(ScriptEditorSessionStore.MinCaretColumn, state.Tabs[1].CaretColumn,  "最小値の桁 → 1 桁");

        // 保存側（Build）でも同じ正規化が効くこと。壊れた値をファイルへ書き残さない。
        var built = ScriptEditorSessionStore.Build(
            temp.Path, new[] { Tab(Abs(temp, InsideRelative), line: -1, column: 0) }, activeTabIndex: 0);
        Check.Equal(ScriptEditorSessionStore.MinCaretLine,   built!.Tabs[0].Line,   "保存時も行を丸める");
        Check.Equal(ScriptEditorSessionStore.MinCaretColumn, built.Tabs[0].Column,  "保存時も桁を丸める");
    }

    private static void ScrollIsSanitized()
    {
        using var temp = new TempDir();
        var document = new ScriptEditorSessionDocument
        {
            ActiveTab = 0,
            Tabs      = new List<ScriptEditorSessionTabFile>
            {
                new() { Path = InsideStored,              Line = 1, Column = 1, Scroll = double.NaN },
                new() { Path = "assets/Scripts/B.cs",     Line = 1, Column = 1, Scroll = double.PositiveInfinity },
                new() { Path = "assets/Scripts/C.cs",     Line = 1, Column = 1, Scroll = -100.0 },
                new() { Path = "assets/Scripts/D.cs",     Line = 1, Column = 1, Scroll = 1e300 },
            },
        };

        var state = ScriptEditorSessionStore.Resolve(document, temp.Path);
        Check.True(state != null, "復元できる");

        Check.Close(ScriptEditorSessionStore.MinScrollOffset, state!.Tabs[0].ScrollOffset, ScrollTolerance, "NaN → 0");
        Check.Close(ScriptEditorSessionStore.MinScrollOffset, state.Tabs[1].ScrollOffset,  ScrollTolerance, "無限大 → 0");
        Check.Close(ScriptEditorSessionStore.MinScrollOffset, state.Tabs[2].ScrollOffset,  ScrollTolerance, "負値 → 0");
        Check.Close(ScriptEditorSessionStore.MaxScrollOffset, state.Tabs[3].ScrollOffset,  ScrollTolerance, "巨大値 → 上限");
    }

    // ── 間引きと添字 ────────────────────────────────────────────

    private static void TabsAreCappedOnRestore()
    {
        using var temp = new TempDir();
        int over = ScriptEditorSessionStore.MaxRestoredTabs + 10;

        var document = new ScriptEditorSessionDocument { ActiveTab = 0 };
        for (int i = 0; i < over; i++)
            document.Tabs.Add(new ScriptEditorSessionTabFile
            {
                Path = $"assets/Scripts/File{i}.cs", Line = 1, Column = 1,
            });

        var state = ScriptEditorSessionStore.Resolve(document, temp.Path);
        Check.True(state != null, "復元できる");
        Check.Equal(ScriptEditorSessionStore.MaxRestoredTabs, state!.Tabs.Count, "上限で頭打ち");
        // 先頭から採るので、最初に開いていたタブが残る。
        Check.Equal(Abs(temp, @"assets\Scripts\File0.cs"), state.Tabs[0].FilePath, "先頭から採る");
    }

    private static void BuildDoesNotCapTabs()
    {
        using var temp = new TempDir();
        int over = ScriptEditorSessionStore.MaxRestoredTabs + 10;

        var tabs = new List<ScriptEditorSessionTabSnapshot>();
        for (int i = 0; i < over; i++) tabs.Add(Tab(Abs(temp, $@"assets\Scripts\File{i}.cs")));

        // 保存側で切ると、利用者が実際に開いていたタブを失う。間引きは復元側だけの仕事。
        var document = ScriptEditorSessionStore.Build(temp.Path, tabs, activeTabIndex: 0);
        Check.Equal(over, document!.Tabs.Count, "保存側は全部書く");
    }

    private static void ActiveIndexFallsBackToFirst()
    {
        using var temp = new TempDir();
        var tabs = new List<ScriptEditorSessionTabFile>
        {
            new() { Path = InsideStored,            Line = 1, Column = 1 },
            new() { Path = "assets/Scripts/B.cs",   Line = 1, Column = 1 },
        };

        foreach (var broken in new[] { -1, 2, int.MaxValue })
        {
            var state = ScriptEditorSessionStore.Resolve(
                new ScriptEditorSessionDocument { ActiveTab = broken, Tabs = tabs }, temp.Path);
            Check.True(state != null, $"復元できる（active_tab={broken}）");
            Check.Equal(0, state!.ActiveTabIndex, $"範囲外の active_tab={broken} は先頭へ倒れる");
        }
    }

    private static void EmptyTabsBuildsNothing()
    {
        using var temp = new TempDir();

        Check.True(ScriptEditorSessionStore.Build(temp.Path, Array.Empty<ScriptEditorSessionTabSnapshot>(), 0) is null,
                   "タブ 0 枚なら保存内容を作らない");
        Check.True(ScriptEditorSessionStore.Build(temp.Path, null, 0) is null,
                   "null でも保存内容を作らない");

        // 保存内容が無いときに Save を呼んでも失敗として返るだけ（例外にしない）。
        var filePath = ScriptEditorSessionStore.FilePathFor(temp.Path)!;
        Check.True(!ScriptEditorSessionStore.Save(filePath, null, out var error), "書くものが無ければ失敗を返す");
        Check.True(error is not null, "理由が返る");
        Check.True(!File.Exists(filePath), "ファイルは作られない");

        // 元からファイルが無くても Delete は成功扱い（全タブを閉じて終了する経路）。
        Check.True(ScriptEditorSessionStore.Delete(filePath, out _), "無いファイルの削除は成功扱い");
    }

    private static void ActiveFollowsDroppedTabs()
    {
        using var temp = new TempDir();
        var document = new ScriptEditorSessionDocument
        {
            // 2 枚目（添字 1）がアクティブ。1 枚目はルート外へ出る相対なので落ちる。
            ActiveTab = 1,
            Tabs      = new List<ScriptEditorSessionTabFile>
            {
                new() { Path = "../dropped.cs",       Line = 1, Column = 1 },
                new() { Path = InsideStored,          Line = 1, Column = 1 },
                new() { Path = "assets/Scripts/C.cs", Line = 1, Column = 1 },
            },
        };

        var state = ScriptEditorSessionStore.Resolve(document, temp.Path);
        Check.True(state != null, "復元できる");
        Check.Equal(2, state!.Tabs.Count, "落ちた 1 枚を除いた枚数");
        Check.Equal(0, state.ActiveTabIndex, "アクティブが詰め直された位置を指す");
        Check.Equal(Abs(temp, InsideRelative), state.Tabs[state.ActiveTabIndex].FilePath,
                    "アクティブは元と同じファイル");
    }
}
