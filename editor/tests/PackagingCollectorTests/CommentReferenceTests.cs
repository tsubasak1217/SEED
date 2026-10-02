// ============================================================
//  CommentReferenceTests.cs — 説明のコメントの中の例のパスを「参照先が見つからないパス」の警告に数えない
//  （docs/backlog.md「W3-2 で見つかったエンジンの制限」(7)。Wake or Pay で 2 件の誤検出）
//
//  【起きていたこと】
//  Wake or Pay の GameData.cs の文書コメント（/// … "assets://common/data/xxx.json" …）と SoundDef.cs の
//  （"assets://common/audio/&lt;id&gt;.wav"）が、SeedPak の「❌ 参照先が見つからないパス」に出ていた。
//  コメントは実行されないので、これはパッケージ版の読み込みの失敗にならない（誤検出）。
//
//  【方針（収録は保守的に）】
//  コメントの中の参照も、実在するファイル・フォルダなら従来どおり収録する（コメントに頼って入っていたものを落とさない）。
//  変えるのは警告だけ: C# のコメント（// ・ /// ・ /* */）の中にだけ書かれ、実体の無い参照は、欠落の一覧から外して
//  別の一覧（IgnoredCommentReferences）に残す。同じ文字列がコードの中にも書かれていれば従来どおり警告する。
//
//  【例外: コメントの中にだけある末尾 '/' のフォルダ参照は展開しない（2026-10-03 の 2 回目のレビュー #26）】
//  末尾 '/' のフォルダ参照を拾う直し（95723ec7。NormalizeRelative の TrimEnd）の前は、末尾 '/' の参照は照合に外れて
//  何も入らなかった。直しの後は、`/// 原画は <c>assets://art/</c> に置く` のような説明の文だけで、art/ の配下が丸ごと
//  （除外ルールに当たる .psd・Thumbs.db や .cs も）pak に入るようになっていた。「従来どおり」を守るため、
//  コメントの中にだけある末尾 '/' の参照はフォルダを展開しない（コードにも書かれていれば展開する）。
//
//  【検証範囲】
//   - CSharpCommentSpans: // ・ /// ・ /* */ を拾い、文字列（普通・逐語的・補間・生）と文字のリテラルの中の // を数えない
//   - AssetReferenceScanner: コメントの中にだけある assets:// に印を付け、コードの中にもあれば付けない
//   - AssetCollector / AssetPakBuilder: 警告から外し、収録は変えない（末尾 '/' のフォルダ参照だけは修正前と同じく展開しない）
// ============================================================

using System;
using System.Collections.Generic;
using System.Linq;
using SEEDEditor.Packaging.Collect;
using SEEDEditor.Packaging.Pak;
using SpriteRigTests;

namespace SEEDEditor.Tests.PackagingCollector;

/// <summary>コメントの中の例のパスのテスト。</summary>
public static class CommentReferenceTests
{
    /// <summary>テスト用の架空のアセットルート（走査だけのテスト用）。</summary>
    private const string FakeRoot = @"C:\proj\assets";

    /// <summary>テストを登録する。</summary>
    /// <param name="h">テストランナー。</param>
    public static void Register(TestHarness h)
    {
        h.Add("コメント: // ・ /// ・ /* */ の範囲を拾い、文字列・文字のリテラルの中の // は数えない", FindsCommentSpans);
        h.Add("コメント: 補間文字列の穴の中の文字列・逐語的文字列の \"\"・生の文字列でも文字列の中の // を数えない", SkipsTrickyLiterals);
        h.Add("コメント: 走査はコメントの中にだけある assets:// に印を付け、コードの中にもあれば付けない", ScannerMarksCommentOnlyReferences);
        h.Add("コメント: Wake or Pay と同じ形の文書コメントの例は「参照先が見つからない」に出ず、別の一覧に残る", CollectorIgnoresCommentOnlyMissing);
        h.Add("コメント: コメントの中の参照でも実在するファイル・フォルダは従来どおり収録する（収録は保守的）", CommentReferencesStillIncluded);
        h.Add("コメント: コメントの中にだけある末尾 / のフォルダ参照は展開しない（修正前と同じ。コードにもあれば展開する）", CommentOnlyTrailingSlashFolderNotExpanded);
        h.Add("コメント: 同じ欠落の参照がコードにもあれば警告する・.cs 以外（JSON）は従来どおり", CodeReferenceStillWarns);
        h.Add("コメント: 共通の報告は警告の行に出さず、対象外の件数だけを 1 行で知らせる", ReportShowsIgnoredCountOnly);
    }

    /// <summary>コメントの中か（範囲の判定の短縮）。</summary>
    private static bool InComment(string text, string needle, int occurrence = 0)
    {
        int at = -1;
        for (int n = 0; n <= occurrence; n++) at = text.IndexOf(needle, at + 1, StringComparison.Ordinal);
        if (at < 0) throw new AssertionException($"テキストに {needle} が無い");
        return CSharpCommentSpans.Contains(CSharpCommentSpans.Find(text), at);
    }

    /// <summary>コメントの範囲の基本。</summary>
    private static void FindsCommentSpans()
    {
        var text = string.Join("\n",
            "/// <summary>例 \"assets://doc/example.json\"</summary>",
            "public static class A",
            "{",
            "    public const string P = \"assets://code/real.json\"; // 行末のコメント assets://tail/note.png",
            "    /* ブロックの",
            "       assets://block/inside.png */ public const string Q = \"assets://code/after_block.png\";",
            "    public const string Url = \"http://example.com//double\";",
            "    public const char Slash = '/'; public const char Quote = '\"'; // assets://after/char.png",
            "}");

        Check.True(InComment(text, "assets://doc/example.json"), "/// の文書コメントの中");
        Check.True(!InComment(text, "assets://code/real.json"), "文字列の中（コード）をコメントにした");
        Check.True(InComment(text, "assets://tail/note.png"), "行末の // のコメントの中");
        Check.True(InComment(text, "assets://block/inside.png"), "/* */ の中");
        Check.True(!InComment(text, "assets://code/after_block.png"), "*/ の後のコードをコメントにした");
        Check.True(!InComment(text, "//double"), "文字列の中の // をコメントの始まりにした");
        Check.True(InComment(text, "assets://after/char.png"), "文字のリテラル '\"' の後の // を見落とした");

        var spans = CSharpCommentSpans.Find(text);
        Check.True(spans.Zip(spans.Skip(1)).All(p => p.First.End <= p.Second.Start), "範囲が昇順で重ならない");
        Check.Equal(0, CSharpCommentSpans.Find("").Count, "空のソース");
        Check.True(InComment("// 閉じない\n", "閉じない"), "最終行の // ");
        Check.True(InComment("/* 閉じないブロック assets://x.png", "assets://x.png"), "閉じない /* は最後まで");
    }

    /// <summary>補間・逐語的・生の文字列。</summary>
    private static void SkipsTrickyLiterals()
    {
        var text = string.Join("\n",
            "var a = $\"{(flag ? \"x//y\" : \"z\")} assets://interp/in_string.png\";",
            "var b = @\"C:\\dir\\\"\"quoted\"\" // assets://verbatim/in_string.png\";",
            "var c = \"\"\"",
            "  raw // assets://raw/in_string.png \"\" still raw",
            "  \"\"\";",
            "var d = $@\"{x} // assets://interp_verbatim/in_string.png\";",
            "var e = $\"{{ // assets://escaped_brace/in_string.png }}\";",
            "var f = $\"{ /* 穴の中のコメント assets://hole/comment.png */ x }\";",
            "// assets://after/all.png");

        Check.True(!InComment(text, "assets://interp/in_string.png"), "補間文字列の穴の中の文字列で同期を失った");
        Check.True(!InComment(text, "assets://verbatim/in_string.png"), "逐語的文字列の \"\" で同期を失った");
        Check.True(!InComment(text, "assets://raw/in_string.png"), "生の文字列の中の // をコメントにした");
        Check.True(!InComment(text, "assets://interp_verbatim/in_string.png"), "$@ の文字列の中の // をコメントにした");
        Check.True(!InComment(text, "assets://escaped_brace/in_string.png"), "{{ }} のエスケープで同期を失った");
        Check.True(InComment(text, "assets://hole/comment.png"), "補間の穴の中の /* */ を見落とした");
        Check.True(InComment(text, "assets://after/all.png"), "最後の行のコメントを見落とした（どこかで同期を失った）");
    }

    /// <summary>走査の印。</summary>
    private static void ScannerMarksCommentOnlyReferences()
    {
        var text = string.Join("\n",
            "/// <param name=\"readAssetText\">アセットのパス（\"assets://common/data/xxx.json\"）→ 中身。</param>",
            "public static class GameData",
            "{",
            "    // 例: assets://shared/both.json もここで読む",
            "    public const string Both = \"assets://shared/both.json\";",
            "    public const string Code = \"assets://code/only.json\";",
            "}");
        var refs = AssetReferenceScanner.Scan(text, "scripts/GameData.cs", FakeRoot);

        AssetReferenceCandidate Find(string rel) =>
            refs.FirstOrDefault(r => r.IsExplicit && r.Candidates[0] == rel);
        Check.True(Find("common/data/xxx.json").OnlyInComments, "文書コメントの中だけの参照に印が無い");
        Check.True(!Find("shared/both.json").OnlyInComments, "コメントとコードの両方にある参照に印を付けた（コードの方が後でも）");
        Check.True(!Find("code/only.json").OnlyInComments, "コードの参照に印を付けた");

        // .cs 以外はコメントを見ない（JSON にコメントの文法は無い）
        var json = AssetReferenceScanner.Scan("{ \"note\": \"// assets://json/value.png\" }", "data/a.json", FakeRoot);
        Check.True(json.Where(r => r.IsExplicit).All(r => !r.OnlyInComments), "JSON の値に印を付けた");
    }

    /// <summary>Wake or Pay と同じ形の誤検出が警告から消える。</summary>
    private static void CollectorIgnoresCommentOnlyMissing()
    {
        using var fx = new AssetFixture();
        // Wake or Pay の GameData.cs:92 と SoundDef.cs:15 と同じ形（どちらもどのシーンからも辿られないスクリプト）
        fx.WriteText("scripts/Domain/GameData.cs", """
        public static class GameData
        {
            /// <param name="readAssetText">アセットのパス（"assets://common/data/xxx.json"）→ 中身。SEED なら <c>path =&gt; Assets.ReadText(path)</c>。</param>
            public static void Load(System.Func<string, string> readAssetText) { }
        }
        """);
        fx.WriteText("scripts/Domain/SoundDef.cs", """
        /// <param name="AssetPath">SEED のアセットのパス（"assets://common/audio/&lt;id&gt;.wav"）。</param>
        public sealed record SoundDef(string AssetPath);
        """);

        var result = new AssetCollector(fx.Root, new AssetPackagingSettings()).Collect();
        var warned = result.MissingReferences.Where(m => m.SourceRelPath.StartsWith("scripts/Domain/", StringComparison.Ordinal)).ToList();
        Check.Equal(0, warned.Count, "コメントの中の例が警告に出た: " + string.Join(", ", warned.Select(m => m.ReferencePath)));

        var ignored = result.IgnoredCommentReferences.Select(m => m.ReferencePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Check.True(ignored.Contains("common/data/xxx.json"), "対象外の一覧に GameData.cs の例が無い");
        Check.True(ignored.Any(p => p.StartsWith("common/audio/", StringComparison.OrdinalIgnoreCase)), "対象外の一覧に SoundDef.cs の例が無い");

        // フィクスチャの本物の欠落（シーンの参照）は従来どおり警告する
        Check.True(result.MissingReferences.Any(m => m.ReferencePath == "scenes/no_such_texture.png"), "本物の欠落まで消えた");
    }

    /// <summary>コメントの中でも実在すれば入る（末尾 '/' の無いフォルダ参照も従来どおり）。</summary>
    private static void CommentReferencesStillIncluded()
    {
        using var fx = new AssetFixture();
        fx.WriteText("scripts/Docs.cs", """
        /// <summary>テーマは assets://themes/doc_theme.json と同じ形で書く（コメントにだけ書いたファイル）。</summary>
        public static class Docs
        {
            /* 置き場のフォルダ: "assets://doc_folder" （コメントにだけ書いた、末尾 / の無いフォルダ） */
        }
        """);
        fx.WriteText("themes/doc_theme.json", "{ }");
        fx.WriteText("doc_folder/a.json", "{ }");

        var included = new AssetCollector(fx.Root, new AssetPackagingSettings()).Collect()
            .Included.Select(a => a.RelPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Check.True(included.Contains("themes/doc_theme.json"), "コメントの中の実在するファイルを収録から外した（保守的でない）");
        Check.True(included.Contains("doc_folder/a.json"), "コメントの中の末尾 / の無いフォルダ参照を収録から外した（95723ec7 より前から入っていた）");
        // 既存のフィクスチャのコメント中の参照（"assets://notes/memo.png）から読む。"）も従来どおり入る
        Check.True(included.Contains("notes/memo.png"), "既存のコメント中の参照が入らなくなった");
    }

    /// <summary>
    /// コメントの中にだけある末尾 '/' のフォルダ参照（説明の文の「置き場」）は、修正前（95723ec7 より前）と同じくフォルダを展開しない。
    /// 2 回目のレビュー #26: 展開すると除外ルールに当たる .psd・Thumbs.db や .cs まで丸ごと pak に入り、
    /// テンプレートのインポート（CollectFrom）ではライブラリのフォルダが丸ごとプロジェクトへコピーされる。
    /// </summary>
    private static void CommentOnlyTrailingSlashFolderNotExpanded()
    {
        using var fx = new AssetFixture();
        // 説明の文だけに書いた置き場（assets:// と、アセットルートの絶対パス \ 区切り の 2 系統）
        fx.WriteText("scripts/ArtDoc.cs", $$"""
        /// <summary>原画は <c>assets://art/</c> に置く。</summary>
        public static class ArtDoc
        {
            // 下書きの置き場: {{fx.Root}}\sketch\
        }
        """);
        fx.WriteBinary("art/hero.png", 8);
        fx.WriteBinary("art/hero.psd", 8);
        fx.WriteBinary("art/Thumbs.db", 8);
        fx.WriteText("art/Tool.cs", "public static class Tool { }");
        fx.WriteBinary("sketch/rough.png", 8);

        // コメントにもコードにも書いた末尾 / のフォルダ（コードの参照なので 95723ec7 の直しどおり展開する）
        fx.WriteText("scripts/Both.cs", """
        public static class Both
        {
            // 例: assets://both_folder/ の中を全部読む
            public const string Folder = "assets://both_folder/";
        }
        """);
        fx.WriteText("both_folder/a.json", "{ }");

        var result = new AssetCollector(fx.Root, new AssetPackagingSettings()).Collect();
        var included = result.Included.Select(a => a.RelPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var leaked = included.Where(p => p.StartsWith("art/", StringComparison.OrdinalIgnoreCase) ||
                                         p.StartsWith("sketch/", StringComparison.OrdinalIgnoreCase)).ToList();
        Check.Equal(0, leaked.Count, "コメントにだけ書いた末尾 / のフォルダを展開した: " + string.Join(", ", leaked));
        Check.True(included.Contains("both_folder/a.json"), "コードにも書いた末尾 / のフォルダを展開しなかった（95723ec7 の直しが戻った）");
        Check.True(!result.MissingReferences.Any(m => m.ReferencePath.StartsWith("art", StringComparison.OrdinalIgnoreCase) ||
                                                      m.ReferencePath.StartsWith("sketch", StringComparison.OrdinalIgnoreCase)),
            "展開しないフォルダ参照を欠落として警告した: " + string.Join(", ", result.MissingReferences.Select(m => m.ReferencePath)));

        // テンプレートのインポートの入口（CollectFrom）でも同じ（スクリプトを起点にしてもフォルダを持ち込まない）
        var fromScript = new AssetCollector(fx.Root, new AssetPackagingSettings()).CollectFrom(["scripts/ArtDoc.cs"]);
        var imported = fromScript.Included.Select(a => a.RelPath).ToList();
        Check.True(!imported.Any(p => p.StartsWith("art/", StringComparison.OrdinalIgnoreCase) ||
                                      p.StartsWith("sketch/", StringComparison.OrdinalIgnoreCase)),
            "CollectFrom でコメントの中のフォルダを持ち込んだ: " + string.Join(", ", imported));
    }

    /// <summary>コードにもあれば警告する・.cs 以外は従来どおり。</summary>
    private static void CodeReferenceStillWarns()
    {
        using var fx = new AssetFixture();
        fx.WriteText("scripts/Mixed.cs", """
        public static class Mixed
        {
            // 例: assets://gone/missing_in_both.png
            public const string Icon = "assets://gone/missing_in_both.png";
            // コメントにだけ: "assets://gone/comment_only.png"
        }
        """);
        fx.WriteText("data/table.json", "{ \"icon\": \"assets://gone/from_json.png\" }");
        fx.WriteText("scenes/main.scene", fx.ReadText("scenes/main.scene").Replace(
            "\"broken\":", "\"table\": \"assets://data/table.json\",\n  \"broken\":", StringComparison.Ordinal));

        var result = new AssetCollector(fx.Root, new AssetPackagingSettings()).Collect();
        var warned = result.MissingReferences.Select(m => m.ReferencePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Check.True(warned.Contains("gone/missing_in_both.png"), "コードにもある欠落を警告しなかった");
        Check.True(!warned.Contains("gone/comment_only.png"), "コメントにだけある欠落を警告した");
        Check.True(warned.Contains("gone/from_json.png"), "JSON の欠落を警告しなかった（.cs 以外は従来どおり）");
    }

    /// <summary>共通の報告の書式。</summary>
    private static void ReportShowsIgnoredCountOnly()
    {
        var result = new AssetCollectionResult
        {
            MissingReferences = [new MissingReference("scenes/real_missing.png", "assets://scenes/real_missing.png", "scenes/main.scene")],
            IgnoredCommentReferences =
            [
                new MissingReference("common/data/xxx.json", "assets://common/data/xxx.json", "scripts/GameData.cs"),
                new MissingReference("common/audio/x.wav", "assets://common/audio/x.wav", "scripts/SoundDef.cs"),
            ],
        };
        var lines = new List<string>();
        AssetPakBuilder.ReportCollection(result, lines.Add);

        Check.True(lines.Any(l => l.StartsWith("❌ 参照先が見つからないパス: 1 件", StringComparison.Ordinal)), "警告の件数がコメントの分を含んでいる");
        Check.True(!lines.Any(l => l.Contains("common/data/xxx.json", StringComparison.Ordinal)), "対象外の例を一覧に出した");
        Check.Equal(1, lines.Count(l => l.StartsWith(AssetPakBuilder.IgnoredCommentReferencesPrefix + "2 件", StringComparison.Ordinal)),
            "対象外の件数の 1 行: " + string.Join(" / ", lines));

        var none = new List<string>();
        AssetPakBuilder.ReportCollection(new AssetCollectionResult(), none.Add);
        Check.True(!none.Any(l => l.StartsWith(AssetPakBuilder.IgnoredCommentReferencesPrefix, StringComparison.Ordinal)), "対象外が 0 件なら何も出さない");
    }
}
