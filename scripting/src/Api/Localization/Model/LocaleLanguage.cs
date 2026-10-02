namespace SEED.Localization;

// ============================================================
//  LocaleLanguage.cs — index.json の言語 1 つ（値だけの型。純粋）
//
//  index.json の "languages" の 1 要素:
//    { "code": "en", "name": "English", "fallback": "ja", "culture": "en-US" }
//  code     … 言語のコード（表のファイル名 <code>.json と、保存・切り替えに使う。"pt-BR" のような地域つきも可）
//  name     … 言語の名前（その言語で書く。言語を選ぶ画面にそのまま出す。省略 = code）
//  fallback … その言語の表に無いキーを次に探す言語（省略・null = 既定の言語へ直接落ちる）
//  culture  … 数・日付の書式に使う文化の名前（省略 = code。Invariant の環境〈Android〉では使われず不変文化になる）
// ============================================================

/// <summary>index.json の言語 1 つ。</summary>
public sealed class LocaleLanguage
{
    /// <summary>言語のコード（index.json に書いたまま。表のファイル名にもなる）。</summary>
    public string Code { get; }

    /// <summary>言語の名前（その言語での呼び名。言語を選ぶ画面に出す）。</summary>
    public string Name { get; }

    /// <summary>表に無いキーを次に探す言語のコード（無ければ null ＝ 既定の言語へ）。</summary>
    public string? Fallback { get; }

    /// <summary>数・日付の書式に使う文化の名前（index.json の culture。書いていなければ <see cref="Code"/>）。</summary>
    public string CultureName { get; }

    /// <summary>言語 1 つを作る。</summary>
    /// <param name="code">言語のコード。</param>
    /// <param name="name">言語の名前（空なら code）。</param>
    /// <param name="fallback">次に探す言語（無ければ null）。</param>
    /// <param name="cultureName">書式の文化の名前（空・null なら code）。</param>
    public LocaleLanguage(string code, string? name, string? fallback, string? cultureName)
    {
        Code = code;
        Name = string.IsNullOrEmpty(name) ? code : name;
        Fallback = string.IsNullOrEmpty(fallback) ? null : fallback;
        CultureName = string.IsNullOrEmpty(cultureName) ? code : cultureName;
    }

    /// <inheritdoc />
    public override string ToString() => $"{Code}（{Name}）";
}
