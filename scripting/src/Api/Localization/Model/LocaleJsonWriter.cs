using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace SEED.Localization;

// ============================================================
//  LocaleJsonWriter.cs — 平たくした項目の並びを入れ子の JSON へ戻す（LocaleTable の平たん化の逆。純粋な計算）
//
//  【使いどころ】エディタの文字列表のパネル（editor/src/Localization/。docs/localization.md §15）が
//  index.json・<言語>.json を書き出すとき。実行中（L10n）は読むだけなので使わない。
//
//  【入力】書き出す項目（Entry）の並び。項目は「鍵の道筋（入れ子の鍵の列）・値の種類・値」:
//    道筋 ["menu", "start"]・文字列 "はじめる" → { "menu": { "start": "はじめる" } }
//    鍵の道筋を項目ごとに持つので、平たいまま書いた鍵（{"menu.start": …}）も入れ子の鍵も書いたとおりに戻せる。
//    平たいキー（"menu.start"）から道筋を作るときは SplitKey（LocaleJson.Join の逆）。
//
//  【並び】オブジェクトの子は「その道筋が最初に出てきた順」に並べる。読んだ順のまま渡せば元の並びに戻り、
//    新しい項目を並びの末尾に足すと、その親のオブジェクトの末尾に入る。
//
//  【配列】子の鍵が 0,1,2,… と抜けなく順に並ぶまとまりは配列で書く（読み込みが配列を番号のキーにする約束
//    〈LocaleTable〉の逆）。途中を消して番号が飛んだら番号の鍵のオブジェクトで書く（残りのキーの番号が変わらない）。
//    根（ファイルの最上位）はいつもオブジェクト。
//
//  【書式】インデント 2（Options で変えられる）・鍵と値の間は ": "・最後に改行 1 つ。
//    値だけの配列は 1 行（["日", "月"]）、配列の中の「値だけのオブジェクト」も 1 行（{ "code": "ja", "name": "日本語" }）。
//    templates/locale の書き方と同じなので、読んでそのまま書き戻すと元のファイルと同じ文字の並びになる。
//
//  【エスケープ】JSON に要るもの（" \ 制御文字・対になっていないサロゲート）だけ。日本語などはそのまま書く。
//
//  【例外】空の道筋・同じ道筋の項目が 2 つ・値の下に子を作る（道筋の途中が値）・まとまりの道筋に値を置く、は
//    ArgumentException。書く側（エディタ）は書く前に確かめてあるので、ここに来るのは作りの誤り。
// ============================================================

/// <summary>平たくした項目の並びを入れ子の JSON の文字列へ戻す。</summary>
public static class LocaleJsonWriter
{
    /// <summary>既定のインデントの幅（空白の数）。</summary>
    public const int DefaultIndentSize = 2;

    /// <summary>既定の改行（新しく作るファイル。既存のファイルは読んだときの改行を Options で渡す）。</summary>
    public const string DefaultNewLine = "\n";

    /// <summary>null の書き方。</summary>
    private const string NullLiteral = "null";

    /// <summary>鍵と値の間。</summary>
    private const string KeyValueSeparator = ": ";

    /// <summary>1 行に並べるときの要素の間。</summary>
    private const string InlineItemSeparator = ", ";

    /// <summary>1 行のオブジェクトの括弧の内側の空白（{ "a": 1 }）。</summary>
    private const char InlinePadding = ' ';

    /// <summary>\uXXXX の 16 進の書式（4 桁・小文字）。</summary>
    private const string UnicodeEscapeFormat = "x4";

    /// <summary>これより小さい文字は制御文字としてエスケープする（JSON の決まり）。</summary>
    private const char FirstPrintableChar = ' ';

    /// <summary>既定の書き方。</summary>
    private static readonly Options DefaultOptions = new();

    // ============================================================
    //  入力の型
    // ============================================================

    /// <summary>項目の値の種類。</summary>
    public enum EntryKind
    {
        /// <summary>文字列（Value をエスケープして "…" で書く）。</summary>
        String,

        /// <summary>null（言語の表では「訳していない」）。Value は使わない。</summary>
        Null,

        /// <summary>JSON の字句をそのまま書く（数・true / false・空のまとまり {} [] など。Value がその字句）。</summary>
        Raw,
    }

    /// <summary>書き出す項目 1 つ（鍵の道筋・値の種類・値）。</summary>
    public readonly struct Entry
    {
        /// <summary>項目を作る。</summary>
        /// <param name="path">鍵の道筋（根から順。1 つ以上）。</param>
        /// <param name="kind">値の種類。</param>
        /// <param name="value">値（String は文字列、Raw は JSON の字句。Null は使わない）。</param>
        public Entry(IReadOnlyList<string> path, EntryKind kind, string? value = null)
        {
            Path = path ?? throw new ArgumentNullException(nameof(path));
            Kind = kind;
            Value = value ?? string.Empty;
        }

        /// <summary>鍵の道筋（根から順）。</summary>
        public IReadOnlyList<string> Path { get; }

        /// <summary>値の種類。</summary>
        public EntryKind Kind { get; }

        /// <summary>値（String は文字列、Raw は JSON の字句、Null は空）。</summary>
        public string Value { get; }

        /// <summary>文字列の項目を作る。</summary>
        /// <param name="path">鍵の道筋。</param>
        /// <param name="text">文字列。</param>
        /// <returns>項目。</returns>
        public static Entry Text(IReadOnlyList<string> path, string text) => new(path, EntryKind.String, text);

        /// <summary>null の項目（訳していない）を作る。</summary>
        /// <param name="path">鍵の道筋。</param>
        /// <returns>項目。</returns>
        public static Entry Untranslated(IReadOnlyList<string> path) => new(path, EntryKind.Null);

        /// <summary>道筋だけを替えた項目を作る（値はそのまま）。</summary>
        /// <param name="path">新しい道筋。</param>
        /// <returns>項目。</returns>
        public Entry WithPath(IReadOnlyList<string> path) => new(path, Kind, Value);
    }

    /// <summary>書き方の設定。</summary>
    public sealed class Options
    {
        /// <summary>インデントの幅（空白の数）。</summary>
        public int IndentSize { get; init; } = DefaultIndentSize;

        /// <summary>改行（"\n" か "\r\n"。元のファイルに合わせる）。</summary>
        public string NewLine { get; init; } = DefaultNewLine;
    }

    // ============================================================
    //  鍵
    // ============================================================

    /// <summary>平たいキーを鍵の道筋に分ける（"menu.start" → ["menu", "start"]。LocaleJson.Join の逆）。</summary>
    /// <param name="key">平たいキー。</param>
    /// <returns>鍵の道筋。</returns>
    public static string[] SplitKey(string key) => key.Split(LocaleJson.KeySeparator);

    // ============================================================
    //  書き出し
    // ============================================================

    /// <summary>
    /// 項目の並びを入れ子の JSON の文字列にする（根はオブジェクト。最後に改行）。
    /// </summary>
    /// <param name="entries">項目（並び順がオブジェクトの子の順になる）。</param>
    /// <param name="options">書き方（null なら既定）。</param>
    /// <returns>JSON の文字列。</returns>
    /// <exception cref="ArgumentException">空の道筋・同じ道筋の重なり・値とまとまりのぶつかり。</exception>
    public static string Write(IEnumerable<Entry> entries, Options? options = null)
    {
        options ??= DefaultOptions;

        // ── 道筋の木を組む（子は最初に出てきた順）──
        var root = new Node();
        foreach (var entry in entries) Insert(root, entry);

        // ── 根はいつもオブジェクトで書く ──
        var builder = new StringBuilder();
        WriteObject(builder, root, 0, options);
        builder.Append(options.NewLine);
        return builder.ToString();
    }

    /// <summary>道筋の木の節（値か、子を持つまとまり）。</summary>
    private sealed class Node
    {
        /// <summary>値の項目（まとまりなら null）。</summary>
        public Entry? Leaf;

        /// <summary>子の鍵（最初に出てきた順）。</summary>
        public readonly List<string> Order = new();

        /// <summary>子（鍵 → 節）。</summary>
        public readonly Dictionary<string, Node> Children = new(StringComparer.Ordinal);

        /// <summary>値の節か。</summary>
        public bool IsLeaf => Leaf.HasValue;
    }

    /// <summary>項目を 1 つ木へ入れる（途中のまとまりは無ければ作る）。</summary>
    private static void Insert(Node root, Entry entry)
    {
        if (entry.Path.Count == 0) throw new ArgumentException("空の道筋の項目は書けません");

        var node = root;
        for (int depth = 0; depth < entry.Path.Count; depth++)
        {
            string segment = entry.Path[depth];
            bool isLast = depth == entry.Path.Count - 1;

            if (node.Children.TryGetValue(segment, out var existing))
            {
                // 最後の鍵が既にある＝同じ道筋の重なり、またはまとまりの道筋に値を置こうとした
                if (isLast)
                {
                    throw new ArgumentException(existing.IsLeaf
                        ? $"同じ道筋の項目が 2 つあります: {Describe(entry.Path, entry.Path.Count)}"
                        : $"子を持つまとまりの道筋に値は置けません: {Describe(entry.Path, entry.Path.Count)}");
                }
                // 途中の鍵が値＝値の下に子を作ろうとした
                if (existing.IsLeaf)
                    throw new ArgumentException($"値の下に子は作れません: {Describe(entry.Path, depth + 1)}");
                node = existing;
                continue;
            }

            var child = new Node();
            if (isLast) child.Leaf = entry;
            node.Children.Add(segment, child);
            node.Order.Add(segment);
            node = child;
        }
    }

    /// <summary>道筋を例外の文言にする（先頭から count 個を "." でつなぐ）。</summary>
    private static string Describe(IReadOnlyList<string> path, int count)
    {
        var parts = new string[count];
        for (int i = 0; i < count; i++) parts[i] = path[i];
        return string.Join(LocaleJson.KeySeparator, parts);
    }

    /// <summary>節を書く（値・配列・オブジェクトのどれか）。</summary>
    private static void WriteNode(StringBuilder builder, Node node, int depth, Options options)
    {
        if (node.Leaf is { } leaf)
        {
            WriteLeaf(builder, leaf);
            return;
        }
        if (IsArray(node)) WriteArray(builder, node, depth, options);
        else WriteObject(builder, node, depth, options);
    }

    /// <summary>子の鍵が 0,1,2,… と抜けなく順に並ぶまとまりか（配列で書く）。</summary>
    private static bool IsArray(Node node)
    {
        if (node.Order.Count == 0) return false;
        for (int i = 0; i < node.Order.Count; i++)
        {
            if (!string.Equals(node.Order[i], i.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal))
                return false;
        }
        return true;
    }

    /// <summary>子がすべて値か（1 行で書ける）。</summary>
    private static bool AllChildrenAreLeaves(Node node)
    {
        foreach (var key in node.Order)
            if (!node.Children[key].IsLeaf) return false;
        return true;
    }

    /// <summary>オブジェクトを複数行で書く（子が無ければ {}）。</summary>
    private static void WriteObject(StringBuilder builder, Node node, int depth, Options options)
    {
        if (node.Order.Count == 0)
        {
            builder.Append('{').Append('}');
            return;
        }

        builder.Append('{').Append(options.NewLine);
        for (int i = 0; i < node.Order.Count; i++)
        {
            string key = node.Order[i];
            AppendIndent(builder, depth + 1, options);
            WriteString(builder, key);
            builder.Append(KeyValueSeparator);
            WriteNode(builder, node.Children[key], depth + 1, options);
            if (i < node.Order.Count - 1) builder.Append(',');
            builder.Append(options.NewLine);
        }
        AppendIndent(builder, depth, options);
        builder.Append('}');
    }

    /// <summary>配列を書く（値だけなら 1 行、まとまりを含めば 1 要素 1 行）。</summary>
    private static void WriteArray(StringBuilder builder, Node node, int depth, Options options)
    {
        // ── 値だけの配列は 1 行（["日", "月", "火"]）──
        if (AllChildrenAreLeaves(node))
        {
            builder.Append('[');
            for (int i = 0; i < node.Order.Count; i++)
            {
                if (i > 0) builder.Append(InlineItemSeparator);
                WriteLeaf(builder, node.Children[node.Order[i]].Leaf!.Value);
            }
            builder.Append(']');
            return;
        }

        // ── まとまりを含む配列は 1 要素 1 行。値だけのオブジェクトの要素は 1 行で書く ──
        builder.Append('[').Append(options.NewLine);
        for (int i = 0; i < node.Order.Count; i++)
        {
            var child = node.Children[node.Order[i]];
            AppendIndent(builder, depth + 1, options);
            if (!child.IsLeaf && !IsArray(child) && AllChildrenAreLeaves(child)) WriteInlineObject(builder, child);
            else WriteNode(builder, child, depth + 1, options);
            if (i < node.Order.Count - 1) builder.Append(',');
            builder.Append(options.NewLine);
        }
        AppendIndent(builder, depth, options);
        builder.Append(']');
    }

    /// <summary>値だけのオブジェクトを 1 行で書く（{ "code": "ja", "name": "日本語" }）。</summary>
    private static void WriteInlineObject(StringBuilder builder, Node node)
    {
        builder.Append('{').Append(InlinePadding);
        for (int i = 0; i < node.Order.Count; i++)
        {
            string key = node.Order[i];
            if (i > 0) builder.Append(InlineItemSeparator);
            WriteString(builder, key);
            builder.Append(KeyValueSeparator);
            WriteLeaf(builder, node.Children[key].Leaf!.Value);
        }
        builder.Append(InlinePadding).Append('}');
    }

    /// <summary>値を書く。</summary>
    private static void WriteLeaf(StringBuilder builder, Entry entry)
    {
        switch (entry.Kind)
        {
            case EntryKind.String: WriteString(builder, entry.Value); break;
            case EntryKind.Null: builder.Append(NullLiteral); break;
            default: builder.Append(entry.Value); break;   // Raw は字句そのまま
        }
    }

    /// <summary>インデントを書く。</summary>
    private static void AppendIndent(StringBuilder builder, int depth, Options options) =>
        builder.Append(' ', depth * options.IndentSize);

    /// <summary>JSON の文字列を書く（要るものだけエスケープする）。</summary>
    private static void WriteString(StringBuilder builder, string text)
    {
        builder.Append('"');
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            switch (c)
            {
                case '"': builder.Append('\\').Append('"'); break;
                case '\\': builder.Append('\\').Append('\\'); break;
                case '\n': builder.Append('\\').Append('n'); break;
                case '\r': builder.Append('\\').Append('r'); break;
                case '\t': builder.Append('\\').Append('t'); break;
                case '\b': builder.Append('\\').Append('b'); break;
                case '\f': builder.Append('\\').Append('f'); break;
                default:
                    if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                    {
                        // 対になったサロゲート（絵文字など）はそのまま書く
                        builder.Append(c).Append(text[i + 1]);
                        i++;
                    }
                    else if (c < FirstPrintableChar || char.IsSurrogate(c))
                    {
                        // 制御文字・対になっていないサロゲートは \uXXXX（UTF-8 で書けないため）
                        builder.Append('\\').Append('u').Append(((int)c).ToString(UnicodeEscapeFormat, CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        builder.Append(c);
                    }
                    break;
            }
        }
        builder.Append('"');
    }
}
