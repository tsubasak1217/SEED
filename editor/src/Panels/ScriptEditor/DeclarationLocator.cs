// ============================================================
//  DeclarationLocator.cs — ソーステキストの中から「宣言そのもの」の位置を探す
//
//  【なぜ必要か】
//  エンジン API（SEEDScripting.dll）はメタデータ参照なので、F12 の飛び先は
//  「型名・メンバ名からエンジンのソースファイルを引き当てて、その中の宣言を探す」
//  という二段構えになっている。以前は宣言を **名前の正規表現（\bName\b）の最初の一致** で
//  探していたため、宣言より前にある XML ドキュメントコメントや別メンバの本文に
//  同じ単語が出てくると、そこへ飛んでしまっていた。
//  実例（2026-09-19 の報告）: GameObject.Instantiate へ F12 → 宣言（116 行目）ではなく、
//  別メンバの summary にある <c>Instantiate</c>（89 行目）へ飛ぶ。
//
//  【やり方】
//  Roslyn の構文木で **宣言ノードの識別子** を探す。コメントや文字列、呼び出し側の
//  式には絶対に一致しない。オーバーロードは引数の個数 → 型の表記の順で絞る。
//
//  【依存】
//  Microsoft.CodeAnalysis.CSharp のみ（WPF・ワークスペース非依存。単体テストへリンクできる）。
// ============================================================

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SEEDEditor.Panels.ScriptEditor;

/// <summary>宣言を探した結果、何に一致したか。</summary>
public enum DeclarationMatchKind
{
    /// <summary>型もメンバも見つからなかった（オフセットは 0）。</summary>
    None,

    /// <summary>型の宣言に一致した（メンバ指定なし、またはメンバがこのファイルに無い）。</summary>
    Type,

    /// <summary>メンバの宣言に一致した。</summary>
    Member,
}

/// <summary>
/// 宣言の位置（不変）。
/// </summary>
/// <param name="Offset">宣言の識別子の先頭オフセット。</param>
/// <param name="Kind">何に一致したか。</param>
public readonly record struct DeclarationLocation(int Offset, DeclarationMatchKind Kind);

/// <summary>
/// ソーステキストから型・メンバの宣言位置を探す純粋ロジック。
/// </summary>
public static class DeclarationLocator
{
    /// <summary>インスタンスコンストラクタのシンボル名（Roslyn / メタデータの表記）。</summary>
    public const string CONSTRUCTOR_NAME = ".ctor";

    /// <summary>静的コンストラクタのシンボル名。</summary>
    public const string STATIC_CONSTRUCTOR_NAME = ".cctor";

    /// <summary>インデクサのシンボル名。</summary>
    public const string INDEXER_NAME = "this[]";

    /// <summary>
    /// 型（と、指定があればそのメンバ）の宣言位置を探す。
    /// </summary>
    /// <param name="text">ソーステキスト。</param>
    /// <param name="typeName">型の単純名（名前空間なし）。</param>
    /// <param name="memberName">
    /// メンバのシンボル名。型そのものへ飛ぶときは null／空。
    /// コンストラクタは <see cref="CONSTRUCTOR_NAME"/>、インデクサは <see cref="INDEXER_NAME"/>。
    /// </param>
    /// <param name="parameterTypes">
    /// メソッド・コンストラクタ・インデクサの引数の型の表記（オーバーロードの絞り込み用）。
    /// 分からなければ null（その場合は最初に見つかった宣言を返す）。
    /// </param>
    public static DeclarationLocation Find(
        string? text, string? typeName, string? memberName,
        IReadOnlyList<string>? parameterTypes = null)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(typeName))
            return new DeclarationLocation(0, DeclarationMatchKind.None);

        SyntaxNode root;
        try { root = CSharpSyntaxTree.ParseText(text).GetRoot(); }
        catch (Exception) { return new DeclarationLocation(0, DeclarationMatchKind.None); }

        // 同じ名前の型宣言（partial で同じファイルに複数あることもある）をすべて拾う。
        var types = root.DescendantNodes()
                        .OfType<BaseTypeDeclarationSyntax>()
                        .Where(t => t.Identifier.ValueText == typeName)
                        .ToList();

        // 型としての delegate（delegate void Foo();）は BaseTypeDeclaration ではない。
        var delegateType = root.DescendantNodes()
                               .OfType<DelegateDeclarationSyntax>()
                               .FirstOrDefault(d => d.Identifier.ValueText == typeName);

        if (types.Count == 0 && delegateType is null)
            return new DeclarationLocation(0, DeclarationMatchKind.None);

        if (!string.IsNullOrEmpty(memberName))
        {
            var member = FindMember(types, memberName!, parameterTypes);
            if (member is { } token)
                return new DeclarationLocation(token.SpanStart, DeclarationMatchKind.Member);
        }

        int typeOffset = types.Count > 0
            ? types[0].Identifier.SpanStart
            : delegateType!.Identifier.SpanStart;
        return new DeclarationLocation(typeOffset, DeclarationMatchKind.Type);
    }

    /// <summary>
    /// 型宣言の中からメンバの宣言を探し、その識別子のトークンを返す。
    /// </summary>
    /// <param name="types">対象の型宣言（partial ぶんをまとめて渡す）。</param>
    /// <param name="memberName">メンバのシンボル名。</param>
    /// <param name="parameterTypes">引数の型の表記（無ければ null）。</param>
    private static SyntaxToken? FindMember(
        IReadOnlyList<BaseTypeDeclarationSyntax> types, string memberName,
        IReadOnlyList<string>? parameterTypes)
    {
        // 引数を持つ宣言（オーバーロードがあり得るもの）と、持たない宣言を分けて集める。
        var withParameters    = new List<(SyntaxToken Token, ParameterListShape Shape)>();
        var withoutParameters = new List<SyntaxToken>();

        foreach (var type in types)
        {
            // 列挙子（enum のメンバ）
            if (type is EnumDeclarationSyntax enumType)
            {
                withoutParameters.AddRange(enumType.Members
                    .Where(m => m.Identifier.ValueText == memberName)
                    .Select(m => m.Identifier));
                continue;
            }

            if (type is not TypeDeclarationSyntax typeDecl) continue;

            // ★入れ子の型のメンバを巻き込まないよう、直下のメンバだけを見る。
            foreach (var member in typeDecl.Members)
            {
                switch (member)
                {
                    case MethodDeclarationSyntax method when method.Identifier.ValueText == memberName:
                        withParameters.Add((method.Identifier, ShapeOf(method.ParameterList)));
                        break;

                    case ConstructorDeclarationSyntax ctor when IsConstructorNamed(ctor, memberName):
                        withParameters.Add((ctor.Identifier, ShapeOf(ctor.ParameterList)));
                        break;

                    case IndexerDeclarationSyntax indexer when memberName == INDEXER_NAME:
                        withParameters.Add((indexer.ThisKeyword, ShapeOf(indexer.ParameterList)));
                        break;

                    case PropertyDeclarationSyntax property when property.Identifier.ValueText == memberName:
                        withoutParameters.Add(property.Identifier);
                        break;

                    case EventDeclarationSyntax evt when evt.Identifier.ValueText == memberName:
                        withoutParameters.Add(evt.Identifier);
                        break;

                    // フィールド / フィールド形式のイベントは 1 宣言に複数の変数を持てる
                    case BaseFieldDeclarationSyntax field:
                        withoutParameters.AddRange(field.Declaration.Variables
                            .Where(v => v.Identifier.ValueText == memberName)
                            .Select(v => v.Identifier));
                        break;

                    // 入れ子の delegate 型をメンバ名で引かれた場合
                    case DelegateDeclarationSyntax del when del.Identifier.ValueText == memberName:
                        withoutParameters.Add(del.Identifier);
                        break;
                }
            }
        }

        if (withParameters.Count > 0) return ChooseOverload(withParameters, parameterTypes);
        if (withoutParameters.Count > 0) return withoutParameters[0];
        return null;
    }

    /// <summary>コンストラクタがシンボル名（.ctor / .cctor）に合うか。</summary>
    /// <param name="ctor">コンストラクタの宣言。</param>
    /// <param name="memberName">メンバのシンボル名。</param>
    private static bool IsConstructorNamed(ConstructorDeclarationSyntax ctor, string memberName)
    {
        bool isStatic = ctor.Modifiers.Any(SyntaxKind.StaticKeyword);
        return memberName == STATIC_CONSTRUCTOR_NAME ? isStatic
             : memberName == CONSTRUCTOR_NAME        ? !isStatic
             : false;
    }

    /// <summary>
    /// オーバーロードの中から、引数の形が最も合う宣言を選ぶ。
    /// 個数と型の表記がすべて一致 → 個数だけ一致 → 最初の宣言、の順に妥協する。
    /// </summary>
    /// <param name="candidates">候補（宣言順）。</param>
    /// <param name="parameterTypes">求める引数の型の表記（無ければ null）。</param>
    private static SyntaxToken ChooseOverload(
        IReadOnlyList<(SyntaxToken Token, ParameterListShape Shape)> candidates,
        IReadOnlyList<string>? parameterTypes)
    {
        if (parameterTypes is null || candidates.Count == 1) return candidates[0].Token;

        var wanted = parameterTypes.Select(NormalizeTypeText).ToList();

        foreach (var candidate in candidates)
        {
            if (candidate.Shape.Types.SequenceEqual(wanted, StringComparer.Ordinal))
                return candidate.Token;
        }

        foreach (var candidate in candidates)
        {
            if (candidate.Shape.Types.Count == wanted.Count) return candidate.Token;
        }

        return candidates[0].Token;
    }

    /// <summary>引数リストの形（正規化した型の表記の列）。</summary>
    /// <param name="Types">引数の型の表記（正規化済み）。</param>
    private readonly record struct ParameterListShape(IReadOnlyList<string> Types);

    /// <summary>引数リストから形を取り出す。</summary>
    /// <param name="parameters">引数リスト（通常の () とインデクサの [] の両方）。</param>
    private static ParameterListShape ShapeOf(BaseParameterListSyntax parameters)
        => new(parameters.Parameters
                         .Select(p => NormalizeTypeText(p.Type?.ToString() ?? string.Empty))
                         .ToList());

    /// <summary>
    /// 型の表記を比較用に正規化する。空白を除き、名前空間の修飾（最後の '.' より前）を
    /// 落とす。ジェネリクスの中身までは分解しない（<c>List&lt;int&gt;</c> 同士は素直に一致する）。
    /// </summary>
    /// <param name="typeText">型の表記（シンボル側・構文側のどちらでもよい）。</param>
    public static string NormalizeTypeText(string typeText)
    {
        var compact = new string((typeText ?? string.Empty).Where(c => !char.IsWhiteSpace(c)).ToArray());
        if (compact.Length == 0) return compact;

        // ジェネリクスの外側にある最後の '.' を探す（System.Collections.Generic.List<int> → List<int>）。
        int depth = 0, lastDot = -1;
        for (int i = 0; i < compact.Length; i++)
        {
            char c = compact[i];
            if (c == '<') depth++;
            else if (c == '>') depth--;
            else if (c == '.' && depth == 0) lastDot = i;
        }

        var simple = lastDot >= 0 ? compact[(lastDot + 1)..] : compact;
        // "global::Foo" のような別名修飾も落とす。
        int alias = simple.LastIndexOf("::", StringComparison.Ordinal);
        return alias >= 0 ? simple[(alias + 2)..] : simple;
    }
}
