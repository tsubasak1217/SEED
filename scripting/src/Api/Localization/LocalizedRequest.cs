using System;
using System.Collections.Generic;

namespace SEED.Localization;

// ============================================================
//  LocalizedRequest.cs — 文字を当てる先（ILocalizedTarget）へ渡す「どのキーをどう引くか」（キー・差し込み・数）
//
//  当てる先は Resolve（自分の文字 1 つ）か ResolveChild（項目ごとの文字。"キー.番号"）で今の言語の文を引く。
//  数（Count）があれば複数形（L10n.Plural）、無ければ L10n.Get で引く。
// ============================================================

/// <summary>文字を当てる先へ渡す引き方（キー・差し込み・数）。</summary>
public readonly struct LocalizedRequest
{
    /// <summary>差し込みが無いときの並び。</summary>
    private static readonly (string name, object? value)[] NoArgs = Array.Empty<(string name, object? value)>();

    /// <summary>差し込み（名前と値。先のものが勝つ）。</summary>
    private readonly (string name, object? value)[]? _args;

    /// <summary>キー（"menu.start"）。</summary>
    public string Key { get; }

    /// <summary>複数形の数（null なら複数形にしない）。</summary>
    public long? Count { get; }

    /// <summary>差し込み（名前と値）。</summary>
    public IReadOnlyList<(string name, object? value)> Args => _args ?? NoArgs;

    /// <summary>引き方を作る。</summary>
    /// <param name="key">キー。</param>
    /// <param name="args">差し込み（null なら無し）。</param>
    /// <param name="count">複数形の数（null なら複数形にしない）。</param>
    public LocalizedRequest(string key, (string name, object? value)[]? args = null, long? count = null)
    {
        Key = key ?? string.Empty;
        _args = args;
        Count = count;
    }

    /// <summary>キーの今の言語の文（数があれば複数形）。</summary>
    /// <returns>文。</returns>
    public string Resolve() => ResolveKey(Key);

    /// <summary>
    /// 項目ごとの文（"キー.項目"。例 キー "settings.theme" と項目 "0" → "settings.theme.0"。表では配列でも書ける）。
    /// </summary>
    /// <param name="item">項目の名前・番号。</param>
    /// <returns>文。</returns>
    public string ResolveChild(string item) => ResolveKey(LocaleJson.Join(Key, item));

    /// <summary>キーを今の言語で引く（数があれば複数形）。</summary>
    private string ResolveKey(string key)
    {
        var args = _args ?? NoArgs;
        return Count is long n ? L10n.Plural(key, n, args) : L10n.Get(key, args);
    }
}
