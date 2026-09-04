using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SEEDEditor.Scripting;

/// <summary>
/// ユーザースクリプト（.cs）の XML ドキュメントコメント（<c>/// &lt;summary&gt;</c>）を
/// 「クラス名.フィールド名 → 説明文」の索引として保持する。
///
/// 【なぜ構文木から取るのか】
/// ドキュメントコメントはリフレクション（<see cref="System.Reflection.FieldInfo"/>）からは
/// 一切取得できない。コンパイル時に別ファイル（XML ドキュメント）へ出力される情報であり、
/// エディタはユーザースクリプトをメモリ上へ Emit しているのでその出力も持たない。
/// そのため、コンパイルのために既に構文解析済みの構文木（<see cref="ScriptCompiler"/> が
/// キャッシュしている）を再利用して、フィールド宣言の直前トリビアから直接読み取る。
///
/// 【キャッシュ】
/// 索引は「構文木インスタンス 1 本ごと」に <see cref="ConditionalWeakTable{TKey,TValue}"/> で
/// 保持する。<see cref="ScriptCompiler.CollectProjectSyntaxTrees"/> は
/// 「最終更新時刻とサイズが変わらない限り同一の SyntaxTree インスタンスを返す」ため、
/// これは実質「(パス, 更新時刻) キャッシュ」と同じ効果になり、
/// インスペクタ再表示のたびに全ファイルを走査し直すことを避けられる。
/// 構文木が破棄されれば索引も自動で回収される。
///
/// 【スレッド】
/// 索引の構築はバックグラウンドスレッド（スクリプト型解決タスク）から、
/// 参照は UI スレッドから行われる。公開スナップショットは不変辞書への
/// 参照差し替え（コピーオンライト）でのみ更新するため、ロックなしで安全に読める。
/// </summary>
internal static class ScriptDocComments
{
    // ── 整形の上限（ツールチップが画面を覆い尽くさないための安全弁）─────────

    /// <summary>1 件の説明文として保持する最大行数。超えた分は切り捨てる。</summary>
    private const int MaxSummaryLines = 12;

    /// <summary>1 件の説明文として保持する最大文字数。超えた分は省略記号を付けて切る。</summary>
    private const int MaxSummaryChars = 800;

    /// <summary>文字数上限で切ったときに末尾へ付ける省略記号。</summary>
    private const string TruncationMark = "…";

    /// <summary>クラス名とフィールド名を連結して索引キーにするときの区切り文字。</summary>
    private const char KeySeparator = '.';

    /// <summary>構文木 1 本ぶんの索引（キー: "クラス名.フィールド名"）。</summary>
    private static readonly ConditionalWeakTable<SyntaxTree, Dictionary<string, string>> _perTree = new();

    /// <summary>
    /// 全ファイルをマージした公開スナップショット。
    /// 更新は「新しい辞書を作って差し替える」形のみ（読み手をロックしないため）。
    /// </summary>
    private static volatile Dictionary<string, string> _snapshot = new(StringComparer.Ordinal);

    // ── 索引の構築 ───────────────────────────────────────────

    /// <summary>
    /// プロジェクト全体の構文木からドキュメントコメント索引を作り直す。
    /// 構文木ごとの解析結果はキャッシュされるため、変更のないファイルは走査し直さない。
    /// </summary>
    /// <param name="trees">
    /// <see cref="ScriptCompiler.CollectProjectSyntaxTrees"/> が返した (パス, 構文木) の一覧。
    /// </param>
    public static void Index(IEnumerable<(string path, SyntaxTree tree)> trees)
    {
        var merged = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (_, tree) in trees)
            foreach (var kv in EntriesOf(tree))
                merged[kv.Key] = kv.Value;   // 同名クラスが複数あれば後勝ち（実行時も後勝ちで解決される）

        _snapshot = merged;
    }

    /// <summary>
    /// 単一ファイルの構文木を既存の索引へマージする。
    /// プロジェクト全体コンパイルが失敗して単一ファイルコンパイル
    /// （<see cref="ScriptCompiler.CompileFile"/>）へ落ちた経路でも説明文を出せるようにする。
    /// </summary>
    public static void IndexSingle(SyntaxTree tree)
    {
        var entries = EntriesOf(tree);
        if (entries.Count == 0) return;

        // 読み手を止めないよう、現行スナップショットの複製に足してから差し替える
        var merged = new Dictionary<string, string>(_snapshot, StringComparer.Ordinal);
        foreach (var kv in entries) merged[kv.Key] = kv.Value;
        _snapshot = merged;
    }

    /// <summary>構文木 1 本の索引を取得する（未解析ならここで解析してキャッシュする）。</summary>
    private static Dictionary<string, string> EntriesOf(SyntaxTree tree)
    {
        if (_perTree.TryGetValue(tree, out var cached)) return cached;

        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            foreach (var decl in tree.GetRoot().DescendantNodes().OfType<FieldDeclarationSyntax>())
            {
                // 宣言を囲む型（class / struct / record）名がキーの前半になる。
                // 入れ子型でも「単純名」で引くため、リフレクションの Type.Name と一致する。
                var owner = decl.FirstAncestorOrSelf<TypeDeclarationSyntax>();
                if (owner is null) continue;

                var summary = ExtractSummary(decl);
                if (summary is null) continue;

                // `int a, b;` のように 1 宣言で複数フィールドを宣言できるので全変数へ割り当てる
                foreach (var v in decl.Declaration.Variables)
                    map[MakeKey(owner.Identifier.Text, v.Identifier.Text)] = summary;
            }
        }
        catch { /* 壊れた構文木は説明無しとして扱う（表示機能なので失敗しても致命的でない） */ }

        // 別スレッドが同時に作っていた場合は先着のものを使う（内容は同じ）
        return _perTree.GetValue(tree, _ => map);
    }

    // ── 参照 ─────────────────────────────────────────────────

    /// <summary>索引キーを組み立てる。</summary>
    private static string MakeKey(string typeName, string fieldName) => typeName + KeySeparator + fieldName;

    /// <summary>
    /// フィールドの <c>&lt;summary&gt;</c> 説明文を引く。見つからなければ null。
    /// </summary>
    /// <param name="declaringTypeName">宣言している型の単純名（<c>Type.Name</c>）。</param>
    /// <param name="fieldName">フィールド名。</param>
    public static string? Lookup(string? declaringTypeName, string fieldName)
    {
        if (string.IsNullOrEmpty(declaringTypeName)) return null;
        return _snapshot.TryGetValue(MakeKey(declaringTypeName!, fieldName), out var s) ? s : null;
    }

    // ── XML ドキュメントコメントの解釈 ───────────────────────

    /// <summary>
    /// フィールド宣言の直前トリビアから <c>&lt;summary&gt;</c> の本文を取り出し、
    /// 表示用のプレーンテキストへ整形する。ドキュメントコメントが無ければ null。
    /// </summary>
    private static string? ExtractSummary(SyntaxNode node)
    {
        var doc = node.GetLeadingTrivia()
            .Select(t => t.GetStructure())
            .OfType<DocumentationCommentTriviaSyntax>()
            .FirstOrDefault();
        if (doc is null) return null;

        var summary = doc.Content
            .OfType<XmlElementSyntax>()
            .FirstOrDefault(e => e.StartTag.Name.LocalName.ValueText == "summary");
        if (summary is null) return null;

        var sb = new StringBuilder();
        AppendContent(summary.Content, sb);
        return Normalize(sb.ToString());
    }

    /// <summary>
    /// XML ノード列をプレーンテキストへ落とし込む。
    /// <c>&lt;see cref="X"/&gt;</c> は X、<c>&lt;b&gt;</c> / <c>&lt;c&gt;</c> 等は中身だけを残す。
    /// </summary>
    private static void AppendContent(IEnumerable<XmlNodeSyntax> nodes, StringBuilder sb)
    {
        foreach (var n in nodes)
        {
            switch (n)
            {
                // 素のテキスト（行頭の "///" は字句解析済みで ValueText には含まれない）
                case XmlTextSyntax text:
                    foreach (var tok in text.TextTokens)
                        sb.Append(tok.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.XmlTextLiteralNewLineToken)
                            ? "\n" : tok.ValueText);
                    break;

                // <b>…</b> / <c>…</c> / <para>…</para> などは中身を再帰的に展開する
                case XmlElementSyntax elem:
                    if (elem.StartTag.Name.LocalName.ValueText == "para") sb.Append('\n');
                    AppendContent(elem.Content, sb);
                    if (elem.StartTag.Name.LocalName.ValueText == "para") sb.Append('\n');
                    break;

                // <see cref="X"/> / <paramref name="x"/> は指し先の名前だけを残す
                case XmlEmptyElementSyntax empty:
                    sb.Append(ReferencedName(empty));
                    break;
            }
        }
    }

    /// <summary>空要素（&lt;see/&gt; 等）が指す名前を、表示用に短く取り出す。</summary>
    private static string ReferencedName(XmlEmptyElementSyntax empty)
    {
        foreach (var attr in empty.Attributes)
        {
            switch (attr)
            {
                // cref="SEED.ScriptArray" → "ScriptArray"（名前空間・引数は落とす）
                case XmlCrefAttributeSyntax cref:
                    return ShortName(cref.Cref.ToString());
                // name="value"（paramref / typeparamref）
                case XmlNameAttributeSyntax nameAttr:
                    return nameAttr.Identifier.Identifier.ValueText;
            }
        }
        return "";
    }

    /// <summary>完全修飾名から末尾の識別子だけを取り出す（表示を短くするため）。</summary>
    private static string ShortName(string cref)
    {
        // メソッド参照 "Foo.Bar(int)" の引数部分を落とす
        var paren = cref.IndexOf('(');
        if (paren >= 0) cref = cref[..paren];
        var dot = cref.LastIndexOf('.');
        return dot >= 0 && dot < cref.Length - 1 ? cref[(dot + 1)..] : cref;
    }

    /// <summary>
    /// 抽出した本文を表示用に整える。
    /// 各行の前後空白を落とし、先頭・末尾の空行を除去し、連続する空行は 1 行にまとめ、
    /// 行数・文字数の上限で切り詰める。
    /// </summary>
    private static string? Normalize(string raw)
    {
        var lines = raw.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

        var kept = new List<string>();
        foreach (var line in lines)
        {
            // 念のため行頭に残った "///" を落としてから整形する
            var t = line.TrimStart();
            if (t.StartsWith("///", StringComparison.Ordinal)) t = t[3..];
            t = t.Trim();

            if (t.Length == 0)
            {
                if (kept.Count == 0) continue;              // 先頭の空行は捨てる
                if (kept[^1].Length == 0) continue;         // 連続する空行はまとめる
            }
            kept.Add(t);
            if (kept.Count >= MaxSummaryLines) break;
        }

        // 末尾の空行を落とす
        while (kept.Count > 0 && kept[^1].Length == 0) kept.RemoveAt(kept.Count - 1);
        if (kept.Count == 0) return null;

        var text = string.Join("\n", kept);
        return text.Length > MaxSummaryChars ? text[..MaxSummaryChars] + TruncationMark : text;
    }
}
