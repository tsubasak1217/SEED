using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.CodeCompletion;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Editing;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SEEDEditor.Panels.ScriptEditor;

/// <summary>
/// Roslyn の意味解析（SemanticModel.LookupSymbols）を用いた自作 IntelliSense 補完。
///
/// Roslyn 標準の CompletionService は演算子・アクセサ・BCL 全体まで含み
/// ノイズが多いため、こちらではスコープ内で「名前で参照できる実シンボル」だけを
/// 取得する。これにより次を実現する:
///   - ファイル内のローカル変数・引数・フィールド・メソッド
///   - アクセス可能なクラス／構造体などの型名
///   - "." の後は対象式の型のメンバーだけ（文脈依存）
/// 候補は種類でランク付けし、ユーザーコードのシンボルを上位に出す。
/// </summary>
public static class CustomCompletion
{
    /// <summary>補完候補 1 件（表示・挿入用）。</summary>
    public sealed record Entry(string Name, SymbolKind Kind, int Rank, string Detail, bool InSource);

    /// <summary>
    /// 指定位置・入力済みプレフィックスに対する補完候補を取得する。
    /// prefixLen は position 直前の識別子の長さ（"." 判定に使う）。
    /// </summary>
    public static async Task<IReadOnlyList<Entry>> GetEntriesAsync(Document document, int position, int prefixLen)
    {
        try
        {
            var model = await document.GetSemanticModelAsync();
            var root  = await document.GetSyntaxRootAsync();
            if (model is null || root is null) return Array.Empty<Entry>();

            var prefix = prefixLen > 0
                ? (await document.GetTextAsync()).ToString(new Microsoft.CodeAnalysis.Text.TextSpan(position - prefixLen, prefixLen))
                : "";

            // "." メンバーアクセスなら対象式の型のメンバーだけを列挙する
            ImmutableArray<ISymbol> symbols;
            var container = ResolveMemberAccessType(model, root, position, prefixLen);
            symbols = container is not null
                ? model.LookupSymbols(position, container: container)
                : model.LookupSymbols(position);

            // 名前で参照できる有用なシンボルのみ、名前で集約（オーバーロードを 1 件に）
            var entries = symbols
                .Where(s => s.CanBeReferencedByName && !string.IsNullOrEmpty(s.Name))
                .Where(IsUsefulKind)
                .Where(s => Matches(s.Name, prefix))
                .GroupBy(s => s.Name, StringComparer.Ordinal)
                .Select(g => ToEntry(g.First()))
                .OrderBy(e => e.Rank)
                .ThenByDescending(e => StartsWithCI(e.Name, prefix))
                .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                .Take(80)
                .ToList();

            return entries;
        }
        catch
        {
            return Array.Empty<Entry>();
        }
    }

    /// <summary>"." の直後なら、その対象式の型を返す（メンバー補完用）。そうでなければ null。</summary>
    private static ITypeSymbol? ResolveMemberAccessType(SemanticModel model, SyntaxNode root, int position, int prefixLen)
    {
        int dotPos = position - prefixLen - 1;
        if (dotPos < 0) return null;

        var token = root.FindToken(dotPos);
        if (!token.IsKind(SyntaxKind.DotToken)) return null;

        if (token.Parent is MemberAccessExpressionSyntax ma)
        {
            // 式が型名（静的アクセス）ならその型自身、そうでなければ式の型
            if (model.GetSymbolInfo(ma.Expression).Symbol is ITypeSymbol typeSym) return typeSym;
            return model.GetTypeInfo(ma.Expression).Type;
        }
        return null;
    }

    /// <summary>補完に出す価値のあるシンボル種別か。</summary>
    private static bool IsUsefulKind(ISymbol s) => s.Kind switch
    {
        SymbolKind.Local or SymbolKind.Parameter or SymbolKind.RangeVariable => true,
        SymbolKind.Field or SymbolKind.Property or SymbolKind.Event          => true,
        SymbolKind.Method                                                    => true,
        SymbolKind.NamedType or SymbolKind.Namespace                         => true,
        _                                                                    => false,
    };

    /// <summary>プレフィックスに一致するか（前方一致 or キャメルヒント部分列）。空なら常に一致。</summary>
    private static bool Matches(string name, string prefix)
    {
        if (prefix.Length == 0) return true;
        if (StartsWithCI(name, prefix)) return true;
        return SubsequenceCI(name, prefix);
    }

    private static bool StartsWithCI(string name, string prefix)
        => prefix.Length == 0 || name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>prefix の各文字が name 中に順序どおり現れるか（緩いキャメルマッチ）。</summary>
    private static bool SubsequenceCI(string name, string prefix)
    {
        int i = 0;
        foreach (var ch in name)
        {
            if (i < prefix.Length && char.ToLowerInvariant(ch) == char.ToLowerInvariant(prefix[i])) i++;
            if (i == prefix.Length) return true;
        }
        return i == prefix.Length;
    }

    /// <summary>シンボルを表示用 Entry に変換する（種別でランク付け）。</summary>
    private static Entry ToEntry(ISymbol s)
    {
        bool inSource = s.Locations.Any(l => l.IsInSource);
        int rank = s.Kind switch
        {
            SymbolKind.Local or SymbolKind.Parameter or SymbolKind.RangeVariable => 0,
            SymbolKind.Field or SymbolKind.Property or SymbolKind.Event          => 1,
            SymbolKind.Method                                                    => 2,
            SymbolKind.NamedType                                                 => inSource ? 3 : 4,
            SymbolKind.Namespace                                                 => 5,
            _                                                                    => 6,
        };
        return new Entry(s.Name, s.Kind, rank, BuildDetail(s), inSource);
    }

    /// <summary>候補の右側に出す簡易シグネチャ・型情報を作る。</summary>
    private static string BuildDetail(ISymbol s) => s switch
    {
        IMethodSymbol m   => $"{m.ReturnType.Name} {m.Name}({string.Join(", ", m.Parameters.Select(p => p.Type.Name))})",
        IPropertySymbol p => p.Type.Name,
        IFieldSymbol f    => f.Type.Name,
        ILocalSymbol l    => l.Type.Name,
        IParameterSymbol pa => pa.Type.Name,
        INamedTypeSymbol t => t.TypeKind.ToString().ToLowerInvariant(),
        INamespaceSymbol  => "namespace",
        _                 => s.Kind.ToString(),
    };
}

/// <summary>
/// CustomCompletion.Entry を AvalonEdit の補完項目に橋渡しするデータ。
/// </summary>
public sealed class SymbolCompletionData : ICompletionData
{
    private readonly CustomCompletion.Entry _e;

    public SymbolCompletionData(CustomCompletion.Entry e) => _e = e;

    public ImageSource? Image => null;

    /// <summary>フィルタ・挿入に使う文字列（シンボル名）。</summary>
    public string Text => _e.Name;

    /// <summary>リスト表示（種別記号 + 名前）。</summary>
    public object Content => $"{Glyph(_e.Kind)} {_e.Name}";

    /// <summary>ツールチップに出す詳細（型・シグネチャ）。</summary>
    public object Description => _e.Detail;

    public double Priority => 0;

    /// <summary>確定時にシンボル名を挿入する。</summary>
    public void Complete(TextArea textArea, ISegment completionSegment, EventArgs insertionRequestEventArgs)
        => textArea.Document.Replace(completionSegment, _e.Name);

    private static string Glyph(SymbolKind kind) => kind switch
    {
        SymbolKind.Method                        => "◆",
        SymbolKind.Property                      => "◇",
        SymbolKind.Field                         => "▪",
        SymbolKind.Event                         => "🄴",
        SymbolKind.Local or SymbolKind.Parameter => "▫",
        SymbolKind.NamedType                     => "🅒",
        SymbolKind.Namespace                     => "🄝",
        _                                        => "•",
    };
}
