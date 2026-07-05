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

    /// <summary>
    /// 指定位置のシンボルが「参照アセンブリ（メタデータ）で定義された型・メンバ」の場合に、
    /// その定義元アセンブリ名・型名・メンバ名を返す。ソース定義を持つシンボルは対象外（null）。
    ///
    /// エンジン API（SEEDScripting.dll 内の Mathf/Vector3/Transform/SEEDScript 等）は
    /// メタデータ参照経由のため <see cref="ResolveDefinitionAsync"/> では解決できない。
    /// これで得た型名から、呼び出し側がエンジンのソースファイルを引き当てて開く。
    /// </summary>
    public static async Task<(string assembly, string typeName, string? memberName)?>
        ResolveMetadataDefinitionAsync(Document document, int position)
    {
        try
        {
            var symbol = await SymbolFinder.FindSymbolAtPositionAsync(document, position);
            if (symbol is null) return null;

            // ソース上に定義があるならメタデータ扱いしない（ユーザースクリプト側で解決済み）
            if (symbol.Locations.Any(l => l.IsInSource)) return null;

            var assembly = symbol.ContainingAssembly?.Name;
            if (string.IsNullOrEmpty(assembly)) return null;

            // 型そのもの、またはメンバの含有型を対象にする
            var typeSymbol = symbol as INamedTypeSymbol ?? symbol.ContainingType;
            if (typeSymbol is null) return null;

            // メンバ（メソッド・プロパティ・フィールド）ならその名前も返し、キャレット位置決めに使う
            string? memberName = symbol is INamedTypeSymbol ? null : symbol.Name;
            return (assembly!, typeSymbol.Name, memberName);
        }
        catch
        {
            return null;
        }
    }
}
