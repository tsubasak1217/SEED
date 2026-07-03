using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;

namespace SEEDEditor.Panels.ScriptEditor;

/// <summary>
/// Roslyn を用いた F12 定義ジャンプのロジック。
/// （IntelliSense 補完は <see cref="CustomCompletion"/> が担う。）
/// </summary>
public static class RoslynCompletion
{
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
