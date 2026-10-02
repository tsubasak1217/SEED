using System;
using System.Linq;
using System.Text;
using SEEDEditor.Panels.ScriptEditor.InlineCompletion.Reference;
using SpriteRigTests;

namespace InlineCompletionTests;

/// <summary>
/// 編集中のファイルの文脈の語（<see cref="CompletionContextExtractor"/>）のテスト。
/// </summary>
public static class ContextTests
{
    /// <summary>近くの行数を確かめるときの行数。</summary>
    private const int WindowLines = 2;

    /// <summary>テストを登録する。</summary>
    /// <param name="harness">テストランナー。</param>
    public static void Register(TestHarness harness)
    {
        harness.Add("文脈: using の名前空間の語（static・別名も）", UsingNamespaces);
        harness.Add("文脈: SEED. に続く語（SEED 自身は捨てる語）", EngineQualifiedWords);
        harness.Add("文脈: キーワード・1 文字の語・捨てる語は入らず、Get のような API 名は残る", FiltersWords);
        harness.Add("文脈: カーソルの前後 N 行の語だけが「近く」", NearCursorWindow);
        harness.Add("文脈: 書きかけの語（Bind| と Bind.|）", TypingWord);
        harness.Add("文脈: 文字列・文字のリテラルの中身は数えず、コメントは数える", SkipsLiteralContents);
        harness.Add("文脈: 重みは出方の重みの和", WeightsAreSums);
        harness.Add("文脈: カーソルが範囲外・空のファイルでも落ちない", RobustToEdges);
    }

    /// <summary>既定の設定（近くの行数だけ小さくする）。</summary>
    private static ApiReferenceSettings Settings(int window = WindowLines) => ApiReferenceSettings.Create(cursorWindowLines: window);

    /// <summary>using 指令。</summary>
    private static void UsingNamespaces()
    {
        const string text = "using SEED.Localization;\nusing static SEED.Mathf;\nusing UiAlias = SEED.UI;\n  using var scope = Make();\n";
        var context = CompletionContextExtractor.Extract(text, text.Length, Settings());
        Check.True(context.Kinds["Localization"].HasFlag(ContextWordKinds.Using), "using SEED.Localization の Localization");
        Check.True(context.Kinds["Mathf"].HasFlag(ContextWordKinds.Using), "using static の Mathf");
        Check.True(context.Kinds["UI"].HasFlag(ContextWordKinds.Using), "別名つきの using の名前空間 UI");
        Check.True(!context.Kinds.ContainsKey("UiAlias"), "別名そのものは数えない");
        Check.True(!context.Kinds.TryGetValue("scope", out var scope) || !scope.HasFlag(ContextWordKinds.Using),
            "using 宣言（using var）は名前空間ではない");
        Check.True(context.Kinds.ContainsKey("Make"), "using 宣言の右辺の語は普通の語");
        Check.True(!context.Kinds.ContainsKey("SEED"), "SEED は捨てる語");
    }

    /// <summary>SEED. の修飾。</summary>
    private static void EngineQualifiedWords()
    {
        const string text = "var s = SEED.UI.UiWidget.Of<SEED.UI.ScreenStack>(root);\nvar x = MySEED.Value; var y = SEEDEditor.Scripting.Thing;\n";
        var context = CompletionContextExtractor.Extract(text, 0, Settings());
        foreach (var word in new[] { "UI", "UiWidget", "Of", "ScreenStack" })
            Check.True(context.Kinds[word].HasFlag(ContextWordKinds.EngineQualified), $"{word} は SEED. に続く語");
        Check.True(!context.Kinds["MySEED"].HasFlag(ContextWordKinds.EngineQualified), "MySEED は SEED. の修飾ではない");
        Check.True(!context.Kinds["Thing"].HasFlag(ContextWordKinds.EngineQualified), "SEEDEditor. は SEED. の修飾ではない");
        Check.True(context.Kinds.ContainsKey("root"), "修飾の外の語も拾う");
    }

    /// <summary>捨てる語。</summary>
    private static void FiltersWords()
    {
        const string text = "public override void Update(ref NativeFrameContext ctx) { float x = L10n.Get(\"k\"); var v = value; System.Console.WriteLine(x); }";
        var context = CompletionContextExtractor.Extract(text, 0, Settings());
        foreach (var keyword in new[] { "public", "override", "void", "ref", "float", "var", "value" })
            Check.True(!context.Kinds.ContainsKey(keyword), $"キーワード {keyword} は入らない");
        Check.True(!context.Kinds.ContainsKey("x"), "1 文字の語は入らない");
        Check.True(!context.Kinds.ContainsKey("System"), "設定の捨てる語（System）は入らない");
        foreach (var word in new[] { "Update", "NativeFrameContext", "ctx", "L10n", "Get", "Console", "WriteLine" })
            Check.True(context.Kinds.ContainsKey(word), $"{word} は入る");
    }

    /// <summary>近くの範囲。</summary>
    private static void NearCursorWindow()
    {
        var sb = new StringBuilder();
        for (int i = 0; i < 10; i++) sb.Append($"var Word{i} = {i};\n");
        string text = sb.ToString();
        // 5 行目（Word5）の行頭にカーソル。前後 2 行 = Word3〜Word7 が近く
        int caret = text.IndexOf("var Word5", StringComparison.Ordinal);
        var context = CompletionContextExtractor.Extract(text, caret, Settings());
        for (int i = 0; i < 10; i++)
        {
            bool near = context.Kinds[$"Word{i}"].HasFlag(ContextWordKinds.NearCursor);
            Check.Equal(i >= 3 && i <= 7, near, $"Word{i} が近くか");
        }
        var none = CompletionContextExtractor.Extract(text, caret, Settings(window: 0));
        Check.True(none.Kinds["Word5"].HasFlag(ContextWordKinds.NearCursor) && !none.Kinds["Word4"].HasFlag(ContextWordKinds.NearCursor),
            "行数 0 ならカーソルの行だけが近く");
    }

    /// <summary>書きかけの語。</summary>
    private static void TypingWord()
    {
        var (bind, bindCaret) = Fixture.Split(Fixture.BindTypingFile);
        var context = CompletionContextExtractor.Extract(bind, bindCaret, Settings());
        Check.Equal("Bind", context.TypingWord, "Bind| の書きかけ");
        Check.True(context.Kinds["Bind"].HasFlag(ContextWordKinds.Typing), "書きかけの出方");

        Check.Equal("Bind", CompletionContextExtractor.FindTypingWord("x = Bind.", "x = Bind.".Length), "Bind.| は . の前の語");
        Check.Equal("Ge", CompletionContextExtractor.FindTypingWord("L10n.Ge", "L10n.Ge".Length), "メンバの書きかけ");
        Check.Equal<string?>(null, CompletionContextExtractor.FindTypingWord("foo( ", "foo( ".Length), "直前が空白なら無し");
        Check.Equal("abc", CompletionContextExtractor.FindTypingWord("12abc", "12abc".Length), "数字で始まる並びは数字を飛ばす");

        var keyword = CompletionContextExtractor.Extract("return", "return".Length, Settings());
        Check.Equal<string?>(null, keyword.TypingWord, "キーワードは書きかけの語にしない");
    }

    /// <summary>リテラルとコメント。</summary>
    private static void SkipsLiteralContents()
    {
        // 1 行目: 普通の文字列 / 2 行目: 逐語的文字列（"" は引用符）/ 3 行目: エスケープした引用符 /
        // 4 行目: 文字の '"' とコメント
        const string text = """
            var h = stack.Push("assets://alarm/prefabs/edit.actor");
            var p = @"C:\Verbatim ""Quoted"" Path";
            var e = "Escaped \" StillInside" + AfterString;
            if (c == '"') Quote(); // ScreenStack に積む
            """;
        var context = CompletionContextExtractor.Extract(text, 0, Settings());
        foreach (var inside in new[] { "assets", "alarm", "prefabs", "edit", "actor", "Verbatim", "Quoted", "Path", "Escaped", "StillInside" })
            Check.True(!context.Kinds.ContainsKey(inside), $"リテラルの中の {inside} は数えない");
        foreach (var outside in new[] { "stack", "Push", "AfterString", "Quote", "ScreenStack" })
            Check.True(context.Kinds.ContainsKey(outside), $"リテラルの外・コメントの {outside} は数える");
    }

    /// <summary>重みの和。</summary>
    private static void WeightsAreSums()
    {
        var weights = ApiReferenceSettings.DefaultWeights;
        const string text = "using SEED.Localization;\nvar a = SEED.UI.ScreenStack.Top;\nFar.Away();\n";
        int caret = text.IndexOf("var a", StringComparison.Ordinal);
        var context = CompletionContextExtractor.Extract(text, caret, Settings(window: 0));
        Check.Close(weights.File + weights.Using, context.Weights["Localization"], 1e-9, "using の語（遠い）");
        Check.Close(weights.File + weights.NearCursor + weights.EngineQualified, context.Weights["ScreenStack"], 1e-9, "近くの SEED. の語");
        Check.Close(weights.File, context.Weights["Away"], 1e-9, "遠くの普通の語");
        Check.Close(weights.File + weights.NearCursor + weights.Typing + weights.EngineQualified,
            CompletionContextExtractor.Extract("SEED.UI.Screen", "SEED.UI.Screen".Length, Settings()).Weights["Screen"], 1e-9,
            "書きかけの SEED. の語は全部の和");
    }

    /// <summary>端の場合。</summary>
    private static void RobustToEdges()
    {
        var empty = CompletionContextExtractor.Extract(string.Empty, 0, Settings());
        Check.Equal(0, empty.Kinds.Count, "空のファイルは語なし");
        var outOfRange = CompletionContextExtractor.Extract("var Thing = 1;", 999, Settings());
        Check.True(outOfRange.Kinds.ContainsKey("Thing"), "範囲外のカーソルは丸める");
        var negative = CompletionContextExtractor.Extract("var Thing = 1;", -5, Settings());
        Check.True(negative.Kinds["Thing"].HasFlag(ContextWordKinds.NearCursor), "負のカーソルは先頭へ丸める");
        var crlf = CompletionContextExtractor.Extract("using SEED.UI;\r\nvar Near = 1;\r\n", 0, Settings(window: 0));
        Check.True(crlf.Kinds["UI"].HasFlag(ContextWordKinds.Using), "CRLF のファイルでも using を拾う");
        Check.True(!crlf.Kinds["Near"].HasFlag(ContextWordKinds.NearCursor), "CRLF でも行の数え方は同じ");
        Check.True(empty.Weights.Values.All(w => w >= 0), "重みは 0 以上");
    }
}
