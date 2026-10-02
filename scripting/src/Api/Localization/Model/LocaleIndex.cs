using System;
using System.Collections.Generic;
using System.Text.Json;

namespace SEED.Localization;

// ============================================================
//  LocaleIndex.cs — 言語の一覧（index.json）の読み込みと、言語の引き当て・探す順（純粋な計算）
//
//  【JSON の形】（docs/localization.md §2.1）
//    {
//      "default": "ja",                                   … 既定の言語（どの言語の表にも無いキーの最後の行き先。省略 = 先頭の言語）
//      "languages": [
//        { "code": "ja", "name": "日本語", "fallback": null },
//        { "code": "en", "name": "English", "fallback": "ja", "culture": "en-US" }
//      ]
//    }
//  【読み方の約束】知らない鍵・型の合わない値・知らない言語を指す fallback / default は警告（Warnings）に積んで読まない。
//  例外は投げない。壊れた JSON は Error に理由（言語 0 件として扱う）。
//  【言語の引き当て（Match）】端末の言語（"en-US"）や保存した値を一覧の言語へ当てる:
//    ① そのまま（大文字小文字・"_" と "-" は区別しない）→ ② 後ろの区切りを落としていく（"zh-Hant-TW" → "zh-Hant" → "zh"）
//    → ③ 同じ言語の別の地域（"en-GB" → 一覧の "en-US"。一覧の先のもの）→ 無ければ null。
//  【探す順（BuildChain）】その言語 → fallback → その fallback … → 既定の言語（同じ言語は 2 度入れない＝輪でも止まる）。
// ============================================================

/// <summary>言語の一覧（index.json）。</summary>
public sealed class LocaleIndex
{
    /// <summary>既定の言語の鍵。</summary>
    public const string KeyDefault = "default";
    /// <summary>言語の一覧の鍵。</summary>
    public const string KeyLanguages = "languages";
    /// <summary>言語のコードの鍵。</summary>
    public const string KeyCode = "code";
    /// <summary>言語の名前の鍵。</summary>
    public const string KeyName = "name";
    /// <summary>次に探す言語の鍵。</summary>
    public const string KeyFallback = "fallback";
    /// <summary>書式の文化の鍵。</summary>
    public const string KeyCulture = "culture";

    /// <summary>言語のコードの区切り（BCP 47 の "-"）。</summary>
    private const char SubtagSeparator = '-';
    /// <summary>言語のコードの区切りの書き換え前（"pt_BR" のような書き方を "-" へそろえる）。</summary>
    private const char AltSubtagSeparator = '_';

    /// <summary>言語 0 件の一覧（index.json を読めないとき）。</summary>
    public static LocaleIndex Empty { get; } = new(string.Empty);

    /// <summary>言語（index.json の順）。</summary>
    private readonly List<LocaleLanguage> _languages = new();
    /// <summary>警告。</summary>
    private readonly List<string> _warnings = new();

    /// <summary>どこから読んだか（警告の頭に付ける）。</summary>
    public string Origin { get; }
    /// <summary>言語（index.json の順）。</summary>
    public IReadOnlyList<LocaleLanguage> Languages => _languages;
    /// <summary>既定の言語のコード（言語が 0 件なら空）。</summary>
    public string DefaultCode { get; private set; } = string.Empty;
    /// <summary>警告（知らない鍵・型の誤り・知らない言語を指す fallback / default・重なったコード）。</summary>
    public IReadOnlyList<string> Warnings => _warnings;
    /// <summary>壊れた JSON の理由（読めたら空）。</summary>
    public string Error { get; private set; } = string.Empty;
    /// <summary>JSON として読めたか。</summary>
    public bool IsValid => Error.Length == 0;
    /// <summary>言語が 1 つも無いか。</summary>
    public bool IsEmpty => _languages.Count == 0;

    private LocaleIndex(string origin)
    {
        Origin = origin;
    }

    // ============================================================
    //  読み込み
    // ============================================================

    /// <summary>index.json を読む（例外を投げない）。</summary>
    /// <param name="json">index.json の中身。</param>
    /// <param name="origin">どこから読んだか（警告の頭）。</param>
    /// <returns>言語の一覧（壊れていれば言語 0 件で <see cref="Error"/> に理由）。</returns>
    public static LocaleIndex Parse(string? json, string origin)
    {
        var index = new LocaleIndex(origin);
        try
        {
            using var doc = JsonDocument.Parse(json ?? string.Empty, LocaleJson.ReadOptions);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                index.Error = "index.json の最上位がオブジェクトではありません";
                return index;
            }
            index.Read(doc.RootElement);
        }
        catch (JsonException ex)
        {
            index.Error = ex.Message;
        }
        return index;
    }

    /// <summary>最上位を読む（言語 → fallback の確かめ → 既定の言語の順）。</summary>
    private void Read(JsonElement root)
    {
        string? requestedDefault = null;
        var rawLanguages = new List<(string Code, string? Name, string? Fallback, string? Culture)>();

        foreach (var prop in root.EnumerateObject())
        {
            if (LocaleJson.IsComment(prop.Name)) continue;
            switch (prop.Name)
            {
                case KeyDefault:
                    if (prop.Value.ValueKind == JsonValueKind.String) requestedDefault = prop.Value.GetString();
                    else Warn(KeyDefault, "既定の言語はコードの文字列で書きます");
                    break;
                case KeyLanguages:
                    if (prop.Value.ValueKind == JsonValueKind.Array) ReadLanguages(prop.Value, rawLanguages);
                    else Warn(KeyLanguages, "言語の一覧は配列で書きます");
                    break;
                default:
                    Warn(prop.Name, "知らない鍵です（default・languages だけを読みます）");
                    break;
            }
        }

        // ── fallback が一覧の言語を指しているかを確かめてから言語を作る（知らない言語は既定の言語へ直接落とす）──
        foreach (var (code, name, fallback, culture) in rawLanguages)
        {
            string? resolvedFallback = null;
            if (fallback is not null)
            {
                var target = FindRaw(rawLanguages, fallback);
                if (target is null) Warn($"{code}.{KeyFallback}", $"一覧に無い言語「{fallback}」です（既定の言語へ落とします）");
                else resolvedFallback = target;
            }
            _languages.Add(new LocaleLanguage(code, name, resolvedFallback, culture));
        }

        // ── 既定の言語（省略・知らない言語なら先頭の言語）──
        if (_languages.Count == 0) return;
        DefaultCode = _languages[0].Code;
        if (requestedDefault is null) return;
        var found = Find(requestedDefault);
        if (found is null) Warn(KeyDefault, $"一覧に無い言語「{requestedDefault}」です（先頭の {DefaultCode} を既定にします）");
        else DefaultCode = found.Code;
    }

    /// <summary>"languages" の配列を読む（コードの無い要素・重なったコードは警告して飛ばす）。</summary>
    private void ReadLanguages(JsonElement array, List<(string, string?, string?, string?)> into)
    {
        int position = 0;
        foreach (var item in array.EnumerateArray())
        {
            string where = $"{KeyLanguages}[{position}]";
            position++;
            if (item.ValueKind != JsonValueKind.Object)
            {
                Warn(where, "言語は { \"code\": … } のオブジェクトで書きます");
                continue;
            }

            string? code = null, name = null, fallback = null, culture = null;
            foreach (var prop in item.EnumerateObject())
            {
                if (LocaleJson.IsComment(prop.Name)) continue;
                switch (prop.Name)
                {
                    case KeyCode: code = ReadOptionalString(prop.Value, where, KeyCode); break;
                    case KeyName: name = ReadOptionalString(prop.Value, where, KeyName); break;
                    case KeyFallback: fallback = ReadOptionalString(prop.Value, where, KeyFallback); break;
                    case KeyCulture: culture = ReadOptionalString(prop.Value, where, KeyCulture); break;
                    default: Warn($"{where}.{prop.Name}", "知らない鍵です（code・name・fallback・culture だけを読みます）"); break;
                }
            }

            if (string.IsNullOrWhiteSpace(code))
            {
                Warn(where, "言語のコード（code）がありません");
                continue;
            }
            string normalized = Normalize(code);
            if (FindRaw(into, normalized) is not null)
            {
                Warn(where, $"言語のコード「{normalized}」が重なっています（先のものを使います）");
                continue;
            }
            into.Add((normalized, name, fallback is null ? null : Normalize(fallback), culture));
        }
    }

    /// <summary>文字列か null の値を読む（ほかの型は警告して null）。</summary>
    private string? ReadOptionalString(JsonElement value, string where, string key)
    {
        if (value.ValueKind == JsonValueKind.String) return value.GetString();
        if (value.ValueKind != JsonValueKind.Null) Warn($"{where}.{key}", "文字列か null で書きます");
        return null;
    }

    /// <summary>読みかけの一覧から言語のコードを探す（大文字小文字を区別しない。見つかれば一覧の書き方を返す）。</summary>
    private static string? FindRaw(List<(string Code, string?, string?, string?)> raw, string code)
    {
        foreach (var entry in raw)
            if (string.Equals(entry.Code, code, StringComparison.OrdinalIgnoreCase)) return entry.Code;
        return null;
    }

    /// <summary>警告を積む（どこの鍵かを頭に付ける）。</summary>
    private void Warn(string key, string message) => _warnings.Add($"{Origin}: {key}: {message}");

    // ============================================================
    //  引き当て
    // ============================================================

    /// <summary>
    /// 言語のコードを書きそろえる（前後の空白を落とし、"_" を "-" にする。大文字小文字はそのまま）。
    /// </summary>
    /// <param name="code">言語のコード。</param>
    /// <returns>書きそろえたコード。</returns>
    public static string Normalize(string code) => code.Trim().Replace(AltSubtagSeparator, SubtagSeparator);

    /// <summary>一覧の言語をコードで引く（書きそろえてから大文字小文字を区別せずに比べる。地域を落とすなどはしない）。</summary>
    /// <param name="code">言語のコード。</param>
    /// <returns>言語（無ければ null）。</returns>
    public LocaleLanguage? Find(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        string normalized = Normalize(code);
        foreach (var language in _languages)
            if (string.Equals(language.Code, normalized, StringComparison.OrdinalIgnoreCase)) return language;
        return null;
    }

    /// <summary>
    /// 端末の言語・保存した値を一覧の言語へ当てる（そのまま → 後ろの区切りを落とす → 同じ言語の別の地域）。
    /// </summary>
    /// <param name="requested">当てたい言語のコード（"en-US" など。null・空は当たらない）。</param>
    /// <returns>一覧の言語のコード（当たらなければ null）。</returns>
    public string? Match(string? requested)
    {
        if (string.IsNullOrWhiteSpace(requested)) return null;
        string tag = Normalize(requested);

        // ① そのまま → ② 後ろの区切りを落としていく
        for (string candidate = tag; candidate.Length > 0; candidate = ParentTag(candidate))
        {
            if (Find(candidate) is { } exact) return exact.Code;
        }

        // ③ 同じ言語の別の地域（一覧の先のもの）
        string baseLanguage = BaseLanguage(tag);
        foreach (var language in _languages)
            if (string.Equals(BaseLanguage(language.Code), baseLanguage, StringComparison.OrdinalIgnoreCase)) return language.Code;
        return null;
    }

    /// <summary>
    /// キーを探す言語の順を作る（その言語 → fallback の連鎖 → 既定の言語。同じ言語は 2 度入れない）。
    /// </summary>
    /// <param name="code">今の言語のコード（一覧に無ければ既定の言語だけ）。</param>
    /// <returns>探す順の言語のコード（言語が 0 件なら空）。</returns>
    public IReadOnlyList<string> BuildChain(string code)
    {
        var chain = new List<string>();
        var current = Find(code);
        while (current is not null && !Contains(chain, current.Code))
        {
            chain.Add(current.Code);
            current = current.Fallback is null ? null : Find(current.Fallback);
        }
        if (DefaultCode.Length > 0 && !Contains(chain, DefaultCode)) chain.Add(DefaultCode);
        return chain;
    }

    /// <summary>コードの並びに含まれるか（大文字小文字を区別しない）。</summary>
    private static bool Contains(List<string> codes, string code)
    {
        foreach (var c in codes)
            if (string.Equals(c, code, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>最後の区切りより前（"zh-Hant-TW" → "zh-Hant"。区切りが無ければ空）。</summary>
    private static string ParentTag(string tag)
    {
        int separator = tag.LastIndexOf(SubtagSeparator);
        return separator <= 0 ? string.Empty : tag.Substring(0, separator);
    }

    /// <summary>言語の部分（最初の区切りより前。"en-US" → "en"）。</summary>
    /// <param name="code">言語のコード。</param>
    /// <returns>言語の部分。</returns>
    public static string BaseLanguage(string code)
    {
        int separator = code.IndexOf(SubtagSeparator);
        return separator < 0 ? code : code.Substring(0, separator);
    }
}
