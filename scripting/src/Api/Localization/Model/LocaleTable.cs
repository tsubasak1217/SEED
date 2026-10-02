using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;

namespace SEED.Localization;

// ============================================================
//  LocaleTable.cs — 言語 1 つの文字列の表（<言語>.json）の読み込みと引き方（純粋な計算）
//
//  【JSON の形】（docs/localization.md §2.2）
//    {
//      "_about": "説明（読まない）",
//      "menu": { "start": "はじめる", "quit": "おわる" },     … 入れ子は "." でつないだキーに平たくする（menu.start）
//      "greeting": "こんにちは、{name} さん",                  … 差し込みは LocaleFormatter
//      "coins": { "one": "{n} coin", "other": "{n} coins" },  … 子の鍵がすべて複数形の種類なら複数形のまとまり
//      "weekdays": ["日", "月", "火"],                        … 配列は番号のキー（weekdays.0）
//      "todo": null                                           … null は「訳していない」（無いのと同じ。次の言語を探す）
//    }
//  数と真偽値は書いたままの文字列（10 → "10"・true → "true"）として読む。
//  【読み方の約束】空の鍵・重なったキーは警告（Warnings）。例外は投げない。壊れた JSON は Error に理由（空の表）。
//  【複数形のまとまり】子の鍵（説明の鍵を除く）がすべて zero|one|two|few|many|other で、値が文字列か null のオブジェクト。
//  子は普通のキー（coins.one）としても引ける。まとまりの名前（coins）を Get で引くと other の形を返す。
// ============================================================

/// <summary>言語 1 つの文字列の表。</summary>
public sealed class LocaleTable
{
    /// <summary>真の値を文字列にしたもの。</summary>
    private const string TrueText = "true";
    /// <summary>偽の値を文字列にしたもの。</summary>
    private const string FalseText = "false";

    /// <summary>空の表（ファイルを読めないとき）。</summary>
    public static LocaleTable Empty { get; } = new(string.Empty);

    /// <summary>キー → 文（差し込み前の型）。</summary>
    private readonly Dictionary<string, string> _entries = new(StringComparer.Ordinal);
    /// <summary>複数形のまとまりの名前（"coins"）。</summary>
    private readonly HashSet<string> _pluralGroups = new(StringComparer.Ordinal);
    /// <summary>警告。</summary>
    private readonly List<string> _warnings = new();

    /// <summary>どこから読んだか（警告の頭）。</summary>
    public string Origin { get; }
    /// <summary>キー → 文（平たくした後。複数形の子も入る）。</summary>
    public IReadOnlyDictionary<string, string> Entries => _entries;
    /// <summary>複数形のまとまりの名前。</summary>
    public IReadOnlyCollection<string> PluralGroups => _pluralGroups;
    /// <summary>警告（空の鍵・重なったキー）。</summary>
    public IReadOnlyList<string> Warnings => _warnings;
    /// <summary>壊れた JSON の理由（読めたら空）。</summary>
    public string Error { get; private set; } = string.Empty;
    /// <summary>JSON として読めたか。</summary>
    public bool IsValid => Error.Length == 0;
    /// <summary>キーの数（複数形の子を含む）。</summary>
    public int Count => _entries.Count;

    private LocaleTable(string origin)
    {
        Origin = origin;
    }

    // ============================================================
    //  読み込み
    // ============================================================

    /// <summary>言語の表の JSON を読む（例外を投げない）。</summary>
    /// <param name="json">表の JSON。</param>
    /// <param name="origin">どこから読んだか（警告の頭）。</param>
    /// <returns>表（壊れていれば空で <see cref="Error"/> に理由）。</returns>
    public static LocaleTable Parse(string? json, string origin)
    {
        var table = new LocaleTable(origin);
        try
        {
            using var doc = JsonDocument.Parse(json ?? string.Empty, LocaleJson.ReadOptions);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                table.Error = "言語の表の最上位がオブジェクトではありません";
                return table;
            }
            table.Flatten(doc.RootElement, string.Empty);
        }
        catch (JsonException ex)
        {
            table.Error = ex.Message;
        }
        return table;
    }

    /// <summary>
    /// キー → 文の組から表を作る（コードで表を組むとき・テスト用）。キーは平たくした形（"menu.start"）で渡す。
    /// 複数形のまとまりは <paramref name="pluralGroups"/> で名前を渡す。
    /// </summary>
    /// <param name="entries">キー → 文。</param>
    /// <param name="pluralGroups">複数形のまとまりの名前（無ければ null）。</param>
    /// <param name="origin">どこから作ったか。</param>
    /// <returns>表。</returns>
    public static LocaleTable FromEntries(
        IEnumerable<KeyValuePair<string, string>> entries,
        IEnumerable<string>? pluralGroups = null,
        string origin = "")
    {
        var table = new LocaleTable(origin);
        foreach (var pair in entries) table.Add(pair.Key, pair.Value);
        if (pluralGroups is not null)
            foreach (var group in pluralGroups) table._pluralGroups.Add(group);
        return table;
    }

    /// <summary>JSON の値を 1 つ平たくする（オブジェクト・配列は子へ、文字列・数・真偽値はキーへ、null は飛ばす）。</summary>
    /// <param name="element">値。</param>
    /// <param name="prefix">親のキー（根は空）。</param>
    private void Flatten(JsonElement element, string prefix)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                if (prefix.Length > 0 && IsPluralObject(element)) _pluralGroups.Add(prefix);
                foreach (var prop in element.EnumerateObject())
                {
                    if (LocaleJson.IsComment(prop.Name)) continue;
                    if (prop.Name.Length == 0)
                    {
                        Warn(prefix, "空の鍵は読みません");
                        continue;
                    }
                    Flatten(prop.Value, LocaleJson.Join(prefix, prop.Name));
                }
                break;

            case JsonValueKind.Array:
                int position = 0;
                foreach (var item in element.EnumerateArray())
                {
                    Flatten(item, LocaleJson.Join(prefix, position.ToString(CultureInfo.InvariantCulture)));
                    position++;
                }
                break;

            case JsonValueKind.String:
                Add(prefix, element.GetString() ?? string.Empty);
                break;

            case JsonValueKind.Number:
                Add(prefix, element.GetRawText());
                break;

            case JsonValueKind.True:
                Add(prefix, TrueText);
                break;

            case JsonValueKind.False:
                Add(prefix, FalseText);
                break;

            // null（訳していない）と未定義は飛ばす（無いキーと同じ扱い＝次の言語を探す）
        }
    }

    /// <summary>
    /// 複数形のまとまりのオブジェクトか（説明の鍵を除く子が 1 つ以上あり、すべて複数形の種類の名前で、値が文字列か null）。
    /// </summary>
    private static bool IsPluralObject(JsonElement element)
    {
        bool any = false;
        foreach (var prop in element.EnumerateObject())
        {
            if (LocaleJson.IsComment(prop.Name)) continue;
            if (!PluralCategoryNames.IsCategoryKey(prop.Name)) return false;
            if (prop.Value.ValueKind != JsonValueKind.String && prop.Value.ValueKind != JsonValueKind.Null) return false;
            any = true;
        }
        return any;
    }

    /// <summary>キーを 1 つ足す（重なったら警告して後のものを使う）。</summary>
    private void Add(string key, string value)
    {
        if (key.Length == 0)
        {
            Warn(key, "空のキーは読みません");
            return;
        }
        if (_entries.ContainsKey(key)) Warn(key, "キーが重なっています（後のものを使います）");
        _entries[key] = value;
    }

    /// <summary>警告を積む（どこのキーかを頭に付ける）。</summary>
    private void Warn(string key, string message) => _warnings.Add($"{Origin}: {key}: {message}");

    // ============================================================
    //  引き方
    // ============================================================

    /// <summary>キーがあるか（普通のキーか複数形のまとまりの名前）。</summary>
    /// <param name="key">キー。</param>
    /// <returns>あれば true。</returns>
    public bool ContainsKey(string key) => _entries.ContainsKey(key) || _pluralGroups.Contains(key);

    /// <summary>複数形のまとまりの名前か。</summary>
    /// <param name="key">キー。</param>
    /// <returns>まとまりなら true。</returns>
    public bool IsPluralGroup(string key) => _pluralGroups.Contains(key);

    /// <summary>
    /// キーの文を引く（普通のキー。複数形のまとまりの名前なら other の形）。
    /// </summary>
    /// <param name="key">キー。</param>
    /// <param name="template">文（差し込み前。無ければ空）。</param>
    /// <returns>あれば true。</returns>
    public bool TryGetText(string key, out string template)
    {
        if (_entries.TryGetValue(key, out var found))
        {
            template = found;
            return true;
        }
        if (_pluralGroups.Contains(key)
            && _entries.TryGetValue(LocaleJson.Join(key, PluralCategoryNames.ToKey(PluralCategory.Other)), out var other))
        {
            template = other;
            return true;
        }
        template = string.Empty;
        return false;
    }

    /// <summary>
    /// 複数形の文を引く: 0 なら zero の子 → 規則の形の子 → other の子 → 普通のキー（形の変わらない言語の 1 文）の順。
    /// </summary>
    /// <param name="key">複数形のまとまりの名前（"coins"）。</param>
    /// <param name="count">数。</param>
    /// <param name="category">この表の言語の規則で決めた形の種類。</param>
    /// <param name="template">文（差し込み前。無ければ空）。</param>
    /// <returns>あれば true。</returns>
    public bool TryGetPlural(string key, long count, PluralCategory category, out string template)
    {
        // ① どの言語でも 0 は zero の子を先に探す（「アイテムはありません」を書けるように。Flutter の intl と同じ）
        if (count == 0 && _entries.TryGetValue(FormKey(key, PluralCategory.Zero), out var zero))
        {
            template = zero;
            return true;
        }
        // ② 言語の規則の形 → ③ other の形
        if (_entries.TryGetValue(FormKey(key, category), out var form)
            || _entries.TryGetValue(FormKey(key, PluralCategory.Other), out form))
        {
            template = form;
            return true;
        }
        // ④ 普通のキー（日本語のように形が変わらない言語は "coins": "{n} 枚" と 1 文で書いてよい）
        if (_entries.TryGetValue(key, out var plain))
        {
            template = plain;
            return true;
        }
        template = string.Empty;
        return false;
    }

    /// <summary>複数形の子のキー（"coins" と One → "coins.one"）。</summary>
    private static string FormKey(string key, PluralCategory category) =>
        LocaleJson.Join(key, PluralCategoryNames.ToKey(category));
}
