// ============================================================
//  ResultOrderTests.cs — 結果をカタログの順へ並べる（鍵はライブラリ相対パス）
//  （docs/reviews/2026-10-02_code_review.md #19: 同じファイル名のテンプレートが 2 つのカタログにあると落ちた）
// ============================================================

using SEEDEditor.Tools.SeedTemplateThumbnails;
using SpriteRigTests;

namespace SEEDEditor.Tests.TemplateThumbnails;

/// <summary>ThumbnailResultOrder の単体テスト。</summary>
public static class ResultOrderTests
{
    /// <summary>並べる結果の代わり（ライブラリ相対パスと、来た順の印）。</summary>
    /// <param name="RelPath">テンプレートのライブラリ相対パス。</param>
    /// <param name="Label">見分けの印。</param>
    private sealed record Item(string RelPath, string Label);

    /// <summary>テストを登録する。</summary>
    /// <param name="h">テストランナー。</param>
    public static void Register(TestHarness h)
    {
        h.Add("結果の順: 別のカタログに同じファイル名（button.actor）があっても落ちずにカタログの順へ並ぶ", SameFileNameInTwoCatalogs);
        h.Add("結果の順: 大文字小文字の違いは同じ鍵・カタログに無い結果は最後・同じ鍵が 2 度あっても落ちない", EdgeCases);
    }

    /// <summary>同じファイル名が 2 つのカタログにある。</summary>
    private static void SameFileNameInTwoCatalogs()
    {
        // カタログの順: actors/button → ui/prefabs/button → ui/prefabs/toggle
        string[] entries = ["actors/button.actor", "ui/prefabs/button.actor", "ui/prefabs/toggle.actor"];
        // 撮った順は窓の大きさのまとまりごとなのでカタログの順と違う
        Item[] results =
        [
            new("ui/prefabs/toggle.actor", "toggle"),
            new("ui/prefabs/button.actor", "ui-button"),
            new("actors/button.actor", "actors-button"),
        ];

        var sorted = ThumbnailResultOrder.Sort(entries, results, r => r.RelPath);
        Check.Equal("actors-button,ui-button,toggle", string.Join(",", sorted.Select(r => r.Label)), "カタログの順");
    }

    /// <summary>大文字小文字・カタログに無い鍵・重なった鍵。</summary>
    private static void EdgeCases()
    {
        string[] entries = ["ui/A.actor", "ui/b.actor", "ui/A.actor"];   // 同じ鍵が 2 度（落ちずに最初の位置を採る）
        Item[] results =
        [
            new("ui/unknown.actor", "unknown-1"),
            new("UI/B.ACTOR", "b"),
            new("ui/a.actor", "a"),
            new("ui/other.actor", "unknown-2"),
        ];

        var sorted = ThumbnailResultOrder.Sort(entries, results, r => r.RelPath);
        Check.Equal("a,b,unknown-1,unknown-2", string.Join(",", sorted.Select(r => r.Label)),
            "大文字小文字を見ない・カタログに無いものは最後（来た順のまま）");
        Check.Equal(0, ThumbnailResultOrder.Sort(entries, Array.Empty<Item>(), r => r.RelPath).Count, "結果が無ければ空");
    }
}
