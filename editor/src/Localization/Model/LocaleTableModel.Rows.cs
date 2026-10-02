// ============================================================
//  LocaleTableModel.Rows.cs — 文字列表の行の組み立て（キーの並び・複数形・未訳の数え方）
//
//  【キーの並び】言語の一覧の順に表を見て、キーが最初に出てきた順。2 つ目以降の言語にしか無いキーは、
//  その言語の表で直前にあるキーの後ろへ差し込む（en にだけある coins.one は en の並びの位置に入る）。
//  新しく足したキーはどの表でも末尾に入るので、行も末尾に並ぶ。
//
//  【複数形】どれかの言語の表で複数形のまとまり（LocaleTable.PluralGroups）になっている名前 G について:
//    - G.<形> の行は PluralForm。その言語に G の 1 文があれば「要らない」。無ければ、その言語の複数形の規則が
//      使う形（LocalePluralRequirements）だけが「書くべき」で、ほかの形は「要らない」
//    - G の 1 文の行（ja の "coins"）は PluralPlain。その言語に G の形の文が 1 つでもあれば「要らない」
//  （実行中の引き方 docs/localization.md §5 の ①〜④ と同じ考え方。どちらか一方で引ける）
// ============================================================

using System;
using System.Collections.Generic;
using System.Linq;
using SEED.Localization;
using SEEDEditor.Localization.Json;

namespace SEEDEditor.Localization.Model;

public sealed partial class LocaleTableModel
{
    /// <summary>「表に無い」升目の説明。</summary>
    private const string NoteAbsent = "この言語の表にありません（実行中は次の言語から引きます）";

    /// <summary>null の升目の説明。</summary>
    private const string NoteUntranslated = "null（訳していません。実行中は次の言語から引きます）";

    /// <summary>空の文字列の升目の説明。</summary>
    private const string NoteEmpty = "空の文字列（実行中はそのまま空になります）";

    /// <summary>
    /// 行を組み立てる（キーの並び・升目の状態・説明）。
    /// </summary>
    /// <returns>行（キーの並び）。</returns>
    public IReadOnlyList<LocaleRow> BuildRows()
    {
        var keys = OrderedKeys();
        var groups = PluralGroups();
        var rows = new List<LocaleRow>(keys.Count);
        foreach (var key in keys) rows.Add(BuildRow(key, groups));
        return rows;
    }

    /// <summary>未訳の升目の数（全部の行）。</summary>
    /// <returns>未訳の数。</returns>
    public int CountMissing() => BuildRows().Sum(r => r.MissingCount);

    /// <summary>
    /// キーの並び（言語の一覧の順に表を見て最初に出てきた順。後の言語にしか無いキーは直前のキーの後ろへ）。
    /// </summary>
    /// <returns>平たいキーの並び。</returns>
    public IReadOnlyList<string> OrderedKeys()
    {
        // 途中へ差し込むので連結リストで並べ、キー → 節の表で位置を引く（差し込みも引きも定数時間）
        var order = new LinkedList<string>();
        var nodes = new Dictionary<string, LinkedListNode<string>>(StringComparer.Ordinal);
        foreach (var column in _columns)
        {
            var document = column.Document;
            LinkedListNode<string>? cursor = null;   // この表で直前に見たキーの節
            for (int i = 0; i < document.Count; i++)
            {
                if (!document.IsKeyEntry(i)) continue;
                string key = document.FlatKeyAt(i);
                if (nodes.TryGetValue(key, out var known))
                {
                    cursor = known;
                    continue;
                }
                // 初めてのキー: 直前のキーの後ろへ（この表でまだ何も見ていなければ末尾）
                cursor = cursor is null ? order.AddLast(key) : order.AddAfter(cursor, key);
                nodes[key] = cursor;
            }
        }
        return order.ToList();
    }

    /// <summary>どれかの言語の表で複数形のまとまりになっている名前（LocaleTable の判定）。</summary>
    /// <returns>まとまりの名前。</returns>
    public IReadOnlySet<string> PluralGroups()
    {
        var groups = new HashSet<string>(StringComparer.Ordinal);
        foreach (var column in _columns) groups.UnionWith(column.Table.PluralGroups);
        return groups;
    }

    /// <summary>行を 1 つ作る（種類を決め、言語ごとの升目と説明を集める）。</summary>
    private LocaleRow BuildRow(string key, IReadOnlySet<string> groups)
    {
        // ── 種類 ──
        LocaleRowKind kind = LocaleRowKind.Plain;
        string? group = null;
        PluralCategory? category = null;
        if (LocaleKeyRules.TrySplitPluralForm(key, out var formGroup, out var formCategory) && groups.Contains(formGroup))
        {
            kind = LocaleRowKind.PluralForm;
            group = formGroup;
            category = formCategory;
        }
        else if (groups.Contains(key))
        {
            kind = LocaleRowKind.PluralPlain;
            group = key;
        }

        // ── 升目 ──
        var cells = new List<LocaleCell>(_columns.Count);
        foreach (var column in _columns) cells.Add(BuildCell(column, key, kind, group, category));

        return new LocaleRow(key, kind, group, category, cells);
    }

    /// <summary>升目を 1 つ作る（表の値 → 未訳か → 要らないか）。</summary>
    private static LocaleCell BuildCell(LocaleLanguageColumn column, string key, LocaleRowKind kind, string? group, PluralCategory? category)
    {
        var (text, state) = ReadCell(column, key);
        if (state == LocaleCellState.Translated) return new LocaleCell(column.Code, text, state, string.Empty);

        // ── 文が無い: 書くべきかを決める ──
        string? notNeededReason = kind switch
        {
            LocaleRowKind.PluralForm when IsTranslated(column, group!) =>
                $"「{group}」の 1 文で足りています（複数形の形は要りません）",
            LocaleRowKind.PluralForm when !LocalePluralRequirements.Uses(column.Code, category!.Value) =>
                $"{column.Code} の複数形の規則では使わない形です（書いても構いません）",
            LocaleRowKind.PluralPlain when HasAnyTranslatedForm(column, key) =>
                "複数形の形で書いてあるので 1 文は要りません",
            _ => null,
        };
        if (notNeededReason is not null) return new LocaleCell(column.Code, text, LocaleCellState.NotNeeded, notNeededReason);

        string note = state switch
        {
            LocaleCellState.Empty => NoteEmpty,
            LocaleCellState.Untranslated => NoteUntranslated,
            _ => NoteAbsent,
        };
        return new LocaleCell(column.Code, text, state, note);
    }

    /// <summary>表の値を読む（無い・null・空・文あり）。</summary>
    private static (string? Text, LocaleCellState State) ReadCell(LocaleLanguageColumn column, string key)
    {
        int index = column.Document.FindKey(key);
        if (index < 0) return (null, LocaleCellState.Absent);

        var entry = column.Document.Entries[index];
        if (entry.Kind == LocaleJsonWriter.EntryKind.Null) return (null, LocaleCellState.Untranslated);
        return entry.Value.Length == 0
            ? (entry.Value, LocaleCellState.Empty)
            : (entry.Value, LocaleCellState.Translated);
    }

    /// <summary>その言語にキーの文があるか（空・null・欠けでない）。</summary>
    private static bool IsTranslated(LocaleLanguageColumn column, string key) =>
        ReadCell(column, key).State == LocaleCellState.Translated;

    /// <summary>その言語にまとまりの形の文が 1 つでもあるか。</summary>
    private static bool HasAnyTranslatedForm(LocaleLanguageColumn column, string group)
    {
        foreach (var category in Enum.GetValues<PluralCategory>())
            if (IsTranslated(column, LocaleKeyRules.FormKey(group, category))) return true;
        return false;
    }

    /// <summary>
    /// キーの説明を集める（キーを包むオブジェクトの "_" で始まる鍵。近い順）。
    /// 既定の言語の表を先に見て、無ければ一覧の順に最初に説明のある表のものを返す。
    /// </summary>
    /// <param name="key">平たいキー。</param>
    /// <returns>説明（無ければ空）。</returns>
    public IReadOnlyList<LocaleComment> CommentsFor(string key)
    {
        foreach (var column in ColumnsDefaultFirst())
        {
            var document = column.Document;
            int index = document.FindKey(key);
            var path = index >= 0 ? document.Entries[index].Path : document.PathForNewKey(key);
            var comments = document.CommentsFor(path);
            if (comments.Count > 0) return comments;
        }
        return Array.Empty<LocaleComment>();
    }

    /// <summary>既定の言語の列を先頭にした列の並び。</summary>
    private IEnumerable<LocaleLanguageColumn> ColumnsDefaultFirst()
    {
        var first = FindColumn(_index.DefaultCode);
        if (first is not null) yield return first;
        foreach (var column in _columns)
            if (!ReferenceEquals(column, first)) yield return column;
    }
}
