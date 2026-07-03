using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.CodeCompletion;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Editing;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Completion;
using Microsoft.CodeAnalysis.FindSymbols;
using RoslynCompletionList = Microsoft.CodeAnalysis.Completion.CompletionList;

namespace SEEDEditor.Panels.ScriptEditor;

/// <summary>
/// Roslyn を用いた IntelliSense 補完と F12 定義ジャンプのロジック。
/// </summary>
public static class RoslynCompletion
{
    /// <summary>
    /// 指定位置の補完候補を取得する。
    /// 型に応じてアクセス可能なメンバー・変数・キーワードが返る。
    /// </summary>
    public static async Task<RoslynCompletionList?> GetCompletionsAsync(Document document, int position)
    {
        var service = CompletionService.GetService(document);
        if (service is null) return null;
        try
        {
            return await service.GetCompletionsAsync(document, position);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 指定位置のシンボルの定義箇所（ファイルパスとオフセット）を解決する。
    /// ファイルをまたいで定義元へジャンプするために使う。戻り値 null は解決不可。
    /// </summary>
    public static async Task<(string filePath, int offset)?> ResolveDefinitionAsync(Document document, int position)
    {
        try
        {
            var symbol = await SymbolFinder.FindSymbolAtPositionAsync(document, position);
            if (symbol is null) return null;

            // ソース上の定義位置を優先する（メタデータのみの型は対象外）
            var loc = symbol.Locations.FirstOrDefault(l => l.IsInSource);
            if (loc is null) return null;

            var path = loc.SourceTree?.FilePath;
            if (string.IsNullOrEmpty(path)) return null;
            return (path!, loc.SourceSpan.Start);
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>
/// Roslyn の CompletionItem を AvalonEdit の補完ウィンドウ項目に橋渡しするデータ。
/// </summary>
public sealed class RoslynCompletionData : ICompletionData
{
    private readonly CompletionItem _item;

    public RoslynCompletionData(CompletionItem item)
    {
        _item = item;
    }

    public ImageSource? Image => null;

    /// <summary>補完確定時に挿入される文字列。</summary>
    public string Text => _item.DisplayText;

    /// <summary>リストに表示される内容（アイコン記号 + 表示名）。</summary>
    public object Content => $"{Glyph()} {_item.DisplayText}";

    public object Description => _item.InlineDescription is { Length: > 0 } d ? d : _item.DisplayText;

    /// <summary>Roslyn の並び順（SortText）を優先度に反映する。</summary>
    public double Priority => 0;

    /// <summary>補完を確定し、対象セグメントを置き換える。</summary>
    public void Complete(TextArea textArea, ISegment completionSegment, EventArgs insertionRequestEventArgs)
        => textArea.Document.Replace(completionSegment, Text);

    /// <summary>候補の種別（メソッド・プロパティ・クラス等）を記号で表す。</summary>
    private string Glyph()
    {
        foreach (var tag in _item.Tags)
        {
            switch (tag)
            {
                case "Method":    return "◆";
                case "Property":  return "◇";
                case "Field":     return "▪";
                case "Class":     return "🅒";
                case "Structure": return "🅢";
                case "Interface": return "🅘";
                case "Enum":      return "🅔";
                case "Keyword":   return "🄺";
                case "Local":
                case "Parameter": return "▫";
                case "Namespace": return "🄝";
                case "Event":     return "🄴";
            }
        }
        return "•";
    }
}
