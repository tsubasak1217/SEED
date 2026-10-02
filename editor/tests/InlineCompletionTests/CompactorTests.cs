using System;
using SEEDEditor.Panels.ScriptEditor.InlineCompletion.Reference;
using SpriteRigTests;

namespace InlineCompletionTests;

/// <summary>
/// Markdown の圧縮（<see cref="ApiReferenceCompactor"/>）のテスト。
/// 以前の ScriptApiReference.Compact() と同じ規則であることを確かめる。
/// </summary>
public static class CompactorTests
{
    /// <summary>テストを登録する。</summary>
    /// <param name="harness">テストランナー。</param>
    public static void Register(TestHarness harness)
    {
        harness.Add("圧縮: 見出し・コード・表・重要注記を残し、散文と重要でない注記を落とす", KeepsSignalDropsProse);
        harness.Add("圧縮: メンテナ向けの見出し以降を丸ごと捨てる", DropsMaintainerSectionAndAfter);
        harness.Add("圧縮: 改行は LF にそろい、CRLF の入力でも同じ結果", NormalizesLineEndings);
        harness.Add("圧縮: コードの中の # 行・空行・表の形の行はコードとして残る", KeepsCodeLinesVerbatim);
    }

    /// <summary>残すもの・落とすもの。</summary>
    private static void KeepsSignalDropsProse()
    {
        var compact = ApiReferenceCompactor.Compact(Fixture.Markdown);
        Check.True(compact.Contains("# SEED スクリプト API リファレンス\n", StringComparison.Ordinal), "題の見出しが残る");
        Check.True(compact.Contains("> **重要（AI 向け）**", StringComparison.Ordinal), "重要の注記が残る");
        Check.True(!compact.Contains("この注記は落ちる", StringComparison.Ordinal), "重要を含まない注記は落ちる");
        Check.True(!compact.Contains("散文", StringComparison.Ordinal), "散文は落ちる");
        Check.True(compact.Contains("public class Mover : SEEDScript", StringComparison.Ordinal), "コードは残る");
        Check.True(compact.Contains("| `[SerializeField]` |", StringComparison.Ordinal), "表の行は残る");
        Check.True(compact.Contains("#### レシピ: 回す", StringComparison.Ordinal), "#### の見出しも残る");
        Check.True(compact.Contains("## 1. スクリプトの基本形\n```csharp\n", StringComparison.Ordinal),
            "コードの外の空行は落ちる（見出しの直後にフェンスが来る）");
        Check.True(compact.Contains("using SEED.UI;\n\nvar stack", StringComparison.Ordinal), "コードの中の空行は残る");
    }

    /// <summary>メンテナ向けの節と、それより後ろの節は入らない。</summary>
    private static void DropsMaintainerSectionAndAfter()
    {
        var compact = ApiReferenceCompactor.Compact(Fixture.Markdown);
        Check.True(!compact.Contains("メンテナ向け", StringComparison.Ordinal), "メンテナ向けの見出しが無い");
        Check.True(!compact.Contains("MaintainerOnlyApi", StringComparison.Ordinal), "メンテナ向けのコードが無い");
        Check.True(!compact.Contains("System.Linq", StringComparison.Ordinal), "その後ろの §9 も無い");
        Check.True(compact.Contains("Bind.Text", StringComparison.Ordinal), "直前の §7.22 は残る");
    }

    /// <summary>LF と CRLF の入力で同じ結果になり、出力に CR が無い。</summary>
    private static void NormalizesLineEndings()
    {
        var lf = ApiReferenceCompactor.Compact(Fixture.Markdown);
        var crlf = ApiReferenceCompactor.Compact(Fixture.Markdown.Replace("\n", "\r\n"));
        Check.Equal(lf, crlf, "LF と CRLF で同じ結果");
        Check.True(!lf.Contains('\r'), "出力に CR が無い");
        Check.True(lf.EndsWith("\n", StringComparison.Ordinal), "各行は LF で終わる");
        Check.Equal(string.Empty, ApiReferenceCompactor.Compact(string.Empty), "空の入力は空");
    }

    /// <summary>フェンスの中は見出し・表・空行の形でもコードとして残る。</summary>
    private static void KeepsCodeLinesVerbatim()
    {
        const string md = "## 1. 節\n散文\n```csharp\n#region 中の見出しの形\n\n| 表の形\n  メンテナ向け の語\n#endregion\n```\n散文\n## 2. 次\n";
        var compact = ApiReferenceCompactor.Compact(md);
        Check.Equal("## 1. 節\n```csharp\n#region 中の見出しの形\n\n| 表の形\n  メンテナ向け の語\n#endregion\n```\n## 2. 次\n",
            compact, "フェンスの中は全部残り、中の「メンテナ向け」では打ち切らない");
    }
}
