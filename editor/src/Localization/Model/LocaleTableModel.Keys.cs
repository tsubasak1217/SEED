// ============================================================
//  LocaleTableModel.Keys.cs — キーの編集（升目・追加・名前の変更・削除・複数形の形の追加）
//
//  【約束】（docs/localization.md §15）
//    - 升目を空にすると null（訳していない＝実行中は次の言語から引く）として書く。表に無いキーの升目を空のままにしても何も足さない
//    - キーの追加は、全部の言語の表の末尾に null で足す（行も末尾に並ぶ）
//    - 名前の変更は、表の中の位置を変えずに鍵の道筋だけを替える。複数形のまとまりは形ごと替える
//    - 削除は全部の言語の表から消す（同じキーが 2 か所にあれば両方）
//    - 複数形の形の追加は、まとまりがある言語にはその形を null で足し（1 文で足りている言語は触らない）、
//      どの言語でもまだまとまりでなければ、1 文を other の形へ移して全言語をまとまりにする
//  どれも書く前に全部の表で確かめ、1 つでもぶつかれば何も変えずに理由を返す（途中まで変わった状態を作らない）。
//  読めなかった表（読み取り専用の列）には触らない。
// ============================================================

using System;
using System.Collections.Generic;
using System.Linq;
using SEED.Localization;
using SEEDEditor.Localization.Json;

namespace SEEDEditor.Localization.Model;

public sealed partial class LocaleTableModel
{
    /// <summary>
    /// 升目の文を書き換える（空・null にすると「訳していない」= null）。
    /// </summary>
    /// <param name="key">平たいキー。</param>
    /// <param name="code">言語のコード。</param>
    /// <param name="text">新しい文（空・null は訳していない）。</param>
    /// <returns>結果。</returns>
    public LocaleEditResult SetCell(string key, string code, string? text)
    {
        if (IsReadOnly) return LocaleEditResult.Fail(IndexLoadError);
        var column = FindColumn(code);
        if (column is null) return LocaleEditResult.Fail($"言語「{code}」は一覧にありません");
        if (column.IsReadOnly) return LocaleEditResult.Fail(column.LoadError);

        var document = column.Document;
        string? value = string.IsNullOrEmpty(text) ? null : text;
        int index = document.FindKey(key);

        if (index >= 0)
        {
            // ── 表にあるキー: 同じ値なら何もしない（数・真偽値の字句も文字として同じなら書き換えない）──
            var entry = document.Entries[index];
            bool same = value is null
                ? entry.Kind == LocaleJsonWriter.EntryKind.Null
                : entry.Kind != LocaleJsonWriter.EntryKind.Null && string.Equals(entry.Value, value, StringComparison.Ordinal);
            if (same) return LocaleEditResult.Unchanged;

            document.Replace(index, value is null
                ? LocaleJsonWriter.Entry.Untranslated(entry.Path)
                : LocaleJsonWriter.Entry.Text(entry.Path, value));
        }
        else
        {
            // ── 表に無いキー: 空のままなら足さない。文があれば、ほかの言語の並びに合わせた位置へ足す ──
            if (value is null) return LocaleEditResult.Unchanged;
            var path = document.PathForNewKey(key);
            if (!document.CanPlaceLeaf(path, null, out var reason)) return LocaleEditResult.Fail($"{column.FileName}: {reason}");
            InsertNewEntry(column, key, LocaleJsonWriter.Entry.Text(path, value));
        }

        column.Refresh();
        return LocaleEditResult.Done;
    }

    /// <summary>
    /// キーを足す（全部の言語の表の末尾に null で）。
    /// </summary>
    /// <param name="key">平たいキー（前後の空白は落とす）。</param>
    /// <returns>結果。</returns>
    public LocaleEditResult AddKey(string key)
    {
        if (IsReadOnly) return LocaleEditResult.Fail(IndexLoadError);
        if (!LocaleKeyRules.TryValidate(key, out var normalized, out var reason)) return LocaleEditResult.Fail(reason);
        if (!EditableColumns.Any()) return LocaleEditResult.Fail("言語がありません（先に言語を足してください）");
        if (OrderedKeys().Contains(normalized, StringComparer.Ordinal)) return LocaleEditResult.Fail($"「{normalized}」は既にあります");

        // ── 全部の表で置けるかを先に確かめる ──
        foreach (var column in EditableColumns)
        {
            if (!column.Document.CanPlaceLeaf(column.Document.PathForNewKey(normalized), null, out var conflict))
                return LocaleEditResult.Fail($"{column.FileName}: {conflict}");
        }

        // ── 末尾に null で足す ──
        foreach (var column in EditableColumns)
        {
            var document = column.Document;
            var path = document.PathForNewKey(normalized);
            document.RemovePlaceholdersAlong(path);
            document.Append(LocaleJsonWriter.Entry.Untranslated(path));
            column.Refresh();
        }
        return LocaleEditResult.Done;
    }

    /// <summary>
    /// キーの名前を変える（表の中の位置はそのまま）。<paramref name="key"/> が複数形のまとまりの名前なら、
    /// その形（key.one など）もいっしょに替える。
    /// </summary>
    /// <param name="key">今のキー（複数形はまとまりの名前を渡す）。</param>
    /// <param name="newKey">新しいキー。</param>
    /// <returns>結果。</returns>
    public LocaleEditResult RenameKey(string key, string newKey)
    {
        if (IsReadOnly) return LocaleEditResult.Fail(IndexLoadError);
        if (!LocaleKeyRules.TryValidate(newKey, out var target, out var reason)) return LocaleEditResult.Fail(reason);
        if (string.Equals(key, target, StringComparison.Ordinal)) return LocaleEditResult.Unchanged;

        // ── 動かすキー（古い → 新しい）: キー自身と、複数形のまとまりの名前ならその形 ──
        var moves = new Dictionary<string, string>(StringComparer.Ordinal) { [key] = target };
        if (PluralGroups().Contains(key))
        {
            foreach (var category in Enum.GetValues<PluralCategory>())
                moves[LocaleKeyRules.FormKey(key, category)] = LocaleKeyRules.FormKey(target, category);
        }

        var existingKeys = OrderedKeys();
        if (!existingKeys.Contains(key, StringComparer.Ordinal) && !moves.Keys.Any(k => existingKeys.Contains(k, StringComparer.Ordinal)))
            return LocaleEditResult.Fail($"「{key}」はありません");
        foreach (var (from, to) in moves)
        {
            if (!existingKeys.Contains(from, StringComparer.Ordinal)) continue;
            if (existingKeys.Contains(to, StringComparer.Ordinal) && !moves.ContainsKey(to))
                return LocaleEditResult.Fail($"「{to}」は既にあります");
        }

        // ── 全部の表で確かめてから替える ──
        var plans = new List<(LocaleLanguageColumn Column, List<(int Index, IReadOnlyList<string> Path)> Moves)>();
        foreach (var column in EditableColumns)
        {
            var document = column.Document;
            var columnMoves = new List<(int, IReadOnlyList<string>)>();
            foreach (var (from, to) in moves)
            {
                int index = document.FindKey(from);
                if (index < 0) continue;
                columnMoves.Add((index, RenamedPath(document, document.Entries[index].Path, to)));
            }
            var ignore = new HashSet<int>(columnMoves.Select(m => m.Item1));
            foreach (var (_, path) in columnMoves)
            {
                if (!document.CanPlaceLeaf(path, ignore, out var conflict)) return LocaleEditResult.Fail($"{column.FileName}: {conflict}");
            }
            if (!AllDistinct(columnMoves.Select(m => m.Item2)))
                return LocaleEditResult.Fail($"{column.FileName}: 名前を変えた後のキーが重なります");
            plans.Add((column, columnMoves));
        }

        foreach (var (column, columnMoves) in plans)
        {
            var document = column.Document;
            foreach (var (index, path) in columnMoves) document.Replace(index, document.Entries[index].WithPath(path));
            foreach (var (_, path) in columnMoves) document.RemovePlaceholdersAlong(path);
            column.Refresh();
        }
        return LocaleEditResult.Done;
    }

    /// <summary>
    /// キーを消す（全部の言語の表から。同じキーが 2 か所にあれば両方）。
    /// </summary>
    /// <param name="keys">消すキー。</param>
    /// <returns>結果。</returns>
    public LocaleEditResult DeleteKeys(IEnumerable<string> keys)
    {
        if (IsReadOnly) return LocaleEditResult.Fail(IndexLoadError);
        var targets = keys.ToList();
        bool changed = false;
        foreach (var column in EditableColumns)
        {
            var document = column.Document;
            bool columnChanged = false;
            foreach (var key in targets)
            {
                for (int index = document.FindKey(key); index >= 0; index = document.FindKey(key))
                {
                    document.RemoveAt(index);
                    columnChanged = true;
                }
            }
            if (!columnChanged) continue;
            column.Refresh();
            changed = true;
        }
        return changed ? LocaleEditResult.Done : LocaleEditResult.Unchanged;
    }

    /// <summary>
    /// 複数形の形を足す（docs/localization.md §5）。
    /// <list type="bullet">
    ///   <item>どれかの言語で <paramref name="group"/> がまとまりなら: 1 文を持たない言語に、その形を null で足す</item>
    ///   <item>どの言語でもまだまとまりでなければ: 1 文を other の形へ移し、全言語をまとまりにしてその形を null で足す</item>
    /// </list>
    /// </summary>
    /// <param name="group">まとまりの名前（形の行なら名前の部分を渡す）。</param>
    /// <param name="category">足す形。</param>
    /// <returns>結果。</returns>
    public LocaleEditResult AddPluralForm(string group, PluralCategory category)
    {
        if (IsReadOnly) return LocaleEditResult.Fail(IndexLoadError);
        if (!LocaleKeyRules.TryValidate(group, out var name, out var reason)) return LocaleEditResult.Fail(reason);
        if (!EditableColumns.Any()) return LocaleEditResult.Fail("言語がありません（先に言語を足してください）");

        bool isGroup = PluralGroups().Contains(name);
        string formKey = LocaleKeyRules.FormKey(name, category);
        string otherKey = LocaleKeyRules.FormKey(name, PluralCategory.Other);

        // ── 言語ごとの書き換えを先に決めて確かめる ──
        var plans = new List<Action>();
        foreach (var column in EditableColumns)
        {
            var document = column.Document;
            if (isGroup)
            {
                // まとまりがある: 1 文で足りている言語・既にその形がある言語は触らない
                if (document.FindKey(name) >= 0 || document.FindKey(formKey) >= 0) continue;
                var path = FormPath(document, name, category);
                if (!document.CanPlaceLeaf(path, null, out var conflict)) return LocaleEditResult.Fail($"{column.FileName}: {conflict}");
                plans.Add(() =>
                {
                    document.RemovePlaceholdersAlong(path);
                    int last = document.LastIndexUnder(path.Take(path.Count - 1).ToArray());
                    if (last >= 0) document.Insert(last + 1, LocaleJsonWriter.Entry.Untranslated(path));
                    else InsertNewEntry(column, formKey, LocaleJsonWriter.Entry.Untranslated(path));
                    column.Refresh();
                });
                continue;
            }

            // まだまとまりでない: 1 文を other の形へ移す（無ければ other も null で作る）
            int plain = document.FindKey(name);
            if (plain >= 0)
            {
                var entry = document.Entries[plain];
                var otherPath = Append(entry.Path, PluralCategoryNames.ToKey(PluralCategory.Other));
                var formPath = Append(entry.Path, PluralCategoryNames.ToKey(category));
                var ignore = new HashSet<int> { plain };
                if (!document.CanPlaceLeaf(otherPath, ignore, out var conflict)) return LocaleEditResult.Fail($"{column.FileName}: {conflict}");
                plans.Add(() =>
                {
                    document.Replace(plain, entry.WithPath(otherPath));
                    if (category != PluralCategory.Other) document.Insert(plain + 1, LocaleJsonWriter.Entry.Untranslated(formPath));
                    column.Refresh();
                });
            }
            else
            {
                var otherPath = document.PathForNewKey(otherKey);
                var formPath = document.PathForNewKey(formKey);
                if (!document.CanPlaceLeaf(otherPath, null, out var conflict)) return LocaleEditResult.Fail($"{column.FileName}: {conflict}");
                plans.Add(() =>
                {
                    document.RemovePlaceholdersAlong(otherPath);
                    InsertNewEntry(column, otherKey, LocaleJsonWriter.Entry.Untranslated(otherPath));
                    if (category != PluralCategory.Other)
                    {
                        int at = document.FindPath(otherPath);
                        document.Insert(at + 1, LocaleJsonWriter.Entry.Untranslated(formPath));
                    }
                    column.Refresh();
                });
            }
        }

        if (plans.Count == 0) return LocaleEditResult.Unchanged;
        foreach (var apply in plans) apply();
        return LocaleEditResult.Done;
    }

    // ============================================================
    //  内部
    // ============================================================

    /// <summary>
    /// 表に無いキーの項目を、行の並びで直前にあり、この表にもあるキーの後ろへ差し込む（無ければ末尾）。
    /// ほかの言語と同じ並びを保つため（en に足りなかった ui.dialog.later は close の後ろへ入る）。
    /// </summary>
    private void InsertNewEntry(LocaleLanguageColumn column, string key, LocaleJsonWriter.Entry entry)
    {
        var document = column.Document;
        int removedAt = document.RemovePlaceholdersAlong(entry.Path);
        var order = OrderedKeys();
        int position = -1;
        for (int i = 0; i < order.Count; i++)
        {
            if (string.Equals(order[i], key, StringComparison.Ordinal)) position = i;
        }

        // 行の並びで直前のキーから遡り、この表にあるものの後ろへ
        for (int i = position - 1; i >= 0; i--)
        {
            int anchor = document.FindKey(order[i]);
            if (anchor < 0) continue;
            document.Insert(anchor + 1, entry);
            return;
        }
        document.Insert(removedAt >= 0 ? removedAt : document.Count, entry);
    }

    /// <summary>
    /// 名前の変更後の道筋。"." を含む鍵 1 つで書かれた項目（{"menu.start": …}）は平たいまま、
    /// 根の直下の鍵 1 つの項目は表の書き方（LocaleJsonDocument.PathForNewKey）、入れ子の項目は "." で分ける。
    /// </summary>
    private static IReadOnlyList<string> RenamedPath(LocaleJsonDocument document, IReadOnlyList<string> path, string to)
    {
        if (path.Count > 1) return LocaleJsonWriter.SplitKey(to);
        return path[0].IndexOf(LocaleJson.KeySeparator) >= 0 ? new[] { to } : document.PathForNewKey(to);
    }

    /// <summary>複数形の形の道筋（既にある形と同じ書き方。無ければ表の書き方で）。</summary>
    private static IReadOnlyList<string> FormPath(LocaleJsonDocument document, string group, PluralCategory category)
    {
        string categoryKey = PluralCategoryNames.ToKey(category);
        foreach (var existing in Enum.GetValues<PluralCategory>())
        {
            int index = document.FindKey(LocaleKeyRules.FormKey(group, existing));
            if (index < 0) continue;
            var path = document.Entries[index].Path;
            // まとまりのオブジェクトの子として書かれていれば同じオブジェクトへ、平たい鍵なら平たいまま
            return path.Count > 1 ? Append(path.Take(path.Count - 1).ToArray(), categoryKey) : new[] { LocaleKeyRules.FormKey(group, category) };
        }
        return document.PathForNewKey(LocaleKeyRules.FormKey(group, category));
    }

    /// <summary>道筋の後ろに鍵を 1 つ足した道筋。</summary>
    private static IReadOnlyList<string> Append(IReadOnlyList<string> path, string segment)
    {
        var result = new string[path.Count + 1];
        for (int i = 0; i < path.Count; i++) result[i] = path[i];
        result[path.Count] = segment;
        return result;
    }

    /// <summary>道筋がすべて違うか。</summary>
    private static bool AllDistinct(IEnumerable<IReadOnlyList<string>> paths)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in paths)
        {
            // 鍵の中に区切りの文字があっても取り違えないよう、長さ付きでつなぐ
            string joined = string.Join("|", path.Select(s => s.Length + ":" + s));
            if (!seen.Add(joined)) return false;
        }
        return true;
    }
}
