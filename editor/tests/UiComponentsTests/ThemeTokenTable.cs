using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using SEED.UI;

namespace UiComponentsTests;

/// <summary>
/// docs/ui_theme.md §8 のトークンの表（Markdown）を、トークンの表（UiTokenCatalog）と既定のテーマ（default_theme.json の暗い方・明るい方）から作る（W2-9）。
/// テストは docs の表がこれと一致するかを確かめ、<c>--update-docs &lt;path&gt;</c> で docs の印の間を書き直す。
/// </summary>
public static class ThemeTokenTable
{
    /// <summary>docs の表の始まりの印。</summary>
    public const string BeginMarker = "<!-- token-table:begin（editor/tests/UiComponentsTests の --update-docs が作る。手で直さない） -->";
    /// <summary>docs の表の終わりの印。</summary>
    public const string EndMarker = "<!-- token-table:end -->";
    /// <summary>明るい方が暗い方と同じ値のときの書き方。</summary>
    private const string SameMark = "〃";
    /// <summary>空の文字列の書き方。</summary>
    private const string EmptyText = "（空＝組み込み）";
    /// <summary>数の書き方（小数 3 桁まで）。</summary>
    private const string NumberFormat = "0.###";

    /// <summary>表の Markdown（見出しの行から最後の行まで。行末は \n）。</summary>
    public static string Build(UiThemeData dark, UiThemeData light)
    {
        var sb = new StringBuilder();
        sb.Append("| トークン | 型 | 既定（暗い方） | 既定（明るい方） | 使う部品 |\n");
        sb.Append("|---|---|---|---|---|\n");
        foreach (var info in UiTokenCatalog.All)
        {
            string d = Value(dark, info), l = Value(light, info);
            sb.Append($"| `{info.Name}` | {KindName(info.Kind)} | {d} | {(l == d ? SameMark : l)} | {info.UsedBy} |\n");
        }
        return sb.ToString();
    }

    /// <summary>docs の印の間の表を取り出す（印が無ければ null）。</summary>
    public static string? Extract(string markdown)
    {
        string text = markdown.Replace("\r\n", "\n");
        int begin = text.IndexOf(BeginMarker, StringComparison.Ordinal);
        int end = text.IndexOf(EndMarker, StringComparison.Ordinal);
        if (begin < 0 || end < begin) return null;
        int start = begin + BeginMarker.Length;
        return text.Substring(start, end - start).Trim('\n') + "\n";
    }

    /// <summary>docs の印の間を表で置き直す（印が無ければ false）。</summary>
    public static bool Update(string path, string table)
    {
        string text = File.ReadAllText(path).Replace("\r\n", "\n");
        int begin = text.IndexOf(BeginMarker, StringComparison.Ordinal);
        int end = text.IndexOf(EndMarker, StringComparison.Ordinal);
        if (begin < 0 || end < begin) return false;
        int start = begin + BeginMarker.Length;
        string updated = text.Substring(0, start) + "\n" + table + text.Substring(end);
        File.WriteAllText(path, updated, new UTF8Encoding(false));
        return true;
    }

    /// <summary>型の名前（docs の列）。</summary>
    private static string KindName(UiTokenKind kind) => kind switch
    {
        UiTokenKind.Color => "色",
        UiTokenKind.Curve => "曲線",
        UiTokenKind.Text => "文字列",
        _ => "数",
    };

    /// <summary>既定の値の書き方（色は sRGB の 16 進・曲線は 4 つの数・文字列は空の書き方）。</summary>
    private static string Value(UiThemeData theme, UiTokenInfo info)
    {
        switch (info.Kind)
        {
            case UiTokenKind.Color:
                return theme.TryColor(info.Name, out var c) ? UiColorMath.ToHex(c) : "（なし）";
            case UiTokenKind.Text:
                return theme.TryText(info.Name, out var t) ? (t.Length == 0 ? EmptyText : $"`{t}`") : "（なし）";
            case UiTokenKind.Curve:
                return string.Join(", ", UiTokenCatalog.LeafTokens(info).Select(leaf =>
                    theme.TryNumber(leaf, out var n) ? n.ToString(NumberFormat, CultureInfo.InvariantCulture) : "?"));
            default:
                return theme.TryNumber(info.Name, out var v) ? v.ToString(NumberFormat, CultureInfo.InvariantCulture) : "（なし）";
        }
    }
}
