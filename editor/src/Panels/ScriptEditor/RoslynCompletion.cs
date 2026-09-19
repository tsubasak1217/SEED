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
    /// <remarks>
    /// <c>parameterTypes</c> はメソッド・コンストラクタ・インデクサの引数の型の表記
    /// （オーバーロードのうち、どの宣言へ飛ぶかを決めるために使う）。それ以外は null。
    /// </remarks>
    public static async Task<(string assembly, string typeName, string? memberName,
                              System.Collections.Generic.IReadOnlyList<string>? parameterTypes)?>
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

            // オーバーロードの絞り込み用に、引数の型の表記を取り出す。
            // 構築済みジェネリック（Foo<int>）や拡張メソッドの簡約形では、宣言どおりの表記に
            // ならないので、必ず元の定義（OriginalDefinition / ReducedFrom）から取る。
            System.Collections.Generic.IReadOnlyList<string>? parameterTypes = symbol switch
            {
                IMethodSymbol method => (method.ReducedFrom ?? method).OriginalDefinition.Parameters
                    .Select(p => p.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat))
                    .ToList(),
                IPropertySymbol { IsIndexer: true } indexer => indexer.OriginalDefinition.Parameters
                    .Select(p => p.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat))
                    .ToList(),
                _ => null,
            };

            return (assembly!, typeSymbol.Name, memberName, parameterTypes);
        }
        catch
        {
            return null;
        }
    }
}
