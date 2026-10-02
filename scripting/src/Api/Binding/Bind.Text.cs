using System;
using SEED.Localization;
using SEEDEditor.Scripting;

namespace SEED.Binding;

// ============================================================
//  Bind.Text.cs — Text コンポーネントの文字への結び付け（実行中だけの部分）
//
//      Bind.Text(this, title, _title);                                  // 文字の観測値をそのまま
//      Bind.Text(this, count, _count, n => $"{n} 回");                   // 値 → 書式
//      Bind.Text(this, money, _money, "hud.money", "amount");           // 値 → L10n.Get("hud.money", ("amount", 値))。言語の切り替えでも入れ直す
//  作った時点の値ですぐ入れ、変わるたびに入れ直す。Text が消えたら（Text.IsValid が false）自分を外す。
//  多言語の固定の文字（キーだけ）は LocalizedText / LocalizedLabel を使う（SEED.Localization）。
// ============================================================

public static partial class Bind
{
    /// <summary>文字の観測値を Text へ結ぶ。</summary>
    /// <param name="text">当てる Text。</param>
    /// <param name="source">文字の観測値。</param>
    /// <returns>外す口。</returns>
    public static IDisposable Text(Text text, IReadOnlyObservable<string> source)
        => OneWay(new TextTarget(text), source);

    /// <summary>文字の観測値を Text へ結ぶ（owner の破棄で自動で外れる）。</summary>
    /// <param name="owner">寿命を合わせるスクリプト（ふつうは this）。</param>
    /// <param name="text">当てる Text。</param>
    /// <param name="source">文字の観測値。</param>
    /// <returns>外す口。</returns>
    public static IDisposable Text(SEEDScript owner, Text text, IReadOnlyObservable<string> source)
        => Own(owner, Text(text, source));

    /// <summary>観測値を書式で文字にして Text へ結ぶ。</summary>
    /// <typeparam name="T">値の型。</typeparam>
    /// <param name="text">当てる Text。</param>
    /// <param name="source">観測値。</param>
    /// <param name="format">値 → 文字。</param>
    /// <returns>外す口。</returns>
    public static IDisposable Text<T>(Text text, IReadOnlyObservable<T> source, Func<T, string> format)
        => OneWay(new TextTarget(text), source, format);

    /// <summary>観測値を書式で文字にして Text へ結ぶ（owner の破棄で自動で外れる）。</summary>
    /// <typeparam name="T">値の型。</typeparam>
    /// <param name="owner">寿命を合わせるスクリプト（ふつうは this）。</param>
    /// <param name="text">当てる Text。</param>
    /// <param name="source">観測値。</param>
    /// <param name="format">値 → 文字。</param>
    /// <returns>外す口。</returns>
    public static IDisposable Text<T>(SEEDScript owner, Text text, IReadOnlyObservable<T> source, Func<T, string> format)
        => Own(owner, Text(text, source, format));

    /// <summary>
    /// 観測値を言語の表の文へ差し込んで Text へ結ぶ（値が変わるたびに <c>L10n.Get(l10nKey, (argName, 値))</c> を入れ、
    /// 言語の切り替え・表の読み直し〈L10n.Changed〉でも入れ直す）。数・日付は今の言語の文化で書かれる（表の "{amount:N0}" など）。
    /// </summary>
    /// <typeparam name="T">値の型。</typeparam>
    /// <param name="text">当てる Text。</param>
    /// <param name="source">観測値。</param>
    /// <param name="l10nKey">言語の表のキー。</param>
    /// <param name="argName">文の差し込みの名前（{argName}）。</param>
    /// <returns>外す口。</returns>
    public static IDisposable Text<T>(Text text, IReadOnlyObservable<T> source, string l10nKey, string argName)
    {
        ArgumentNullException.ThrowIfNull(l10nKey);
        ArgumentNullException.ThrowIfNull(argName);
        var binding = CreateOneWay(new TextTarget(text), source, value => L10n.Get(l10nKey, (argName, (object?)value)));

        // 言語が替わったら同じ値で文を引き直す（L10n.Changed は今の言語のコードの string で発火する。結び付けと一緒に外す）
        binding.Own(Events.Subscribe(L10n.Changed, (string _) => binding.Reapply()));
        return binding;
    }

    /// <summary>
    /// 観測値を言語の表の文へ差し込んで Text へ結ぶ（owner の破棄で自動で外れる）。
    /// </summary>
    /// <typeparam name="T">値の型。</typeparam>
    /// <param name="owner">寿命を合わせるスクリプト（ふつうは this）。</param>
    /// <param name="text">当てる Text。</param>
    /// <param name="source">観測値。</param>
    /// <param name="l10nKey">言語の表のキー。</param>
    /// <param name="argName">文の差し込みの名前（{argName}）。</param>
    /// <returns>外す口。</returns>
    public static IDisposable Text<T>(SEEDScript owner, Text text, IReadOnlyObservable<T> source, string l10nKey, string argName)
        => Own(owner, Text(text, source, l10nKey, argName));
}
