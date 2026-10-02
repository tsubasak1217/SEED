// ============================================================
//  LocaleRowKind.cs — 文字列表の行の種類（値だけの型）
// ============================================================

namespace SEEDEditor.Localization.Model;

/// <summary>文字列表の行の種類。</summary>
public enum LocaleRowKind
{
    /// <summary>普通のキー。</summary>
    Plain,

    /// <summary>複数形のまとまりの形（coins.one・coins.other など）。</summary>
    PluralForm,

    /// <summary>ほかの言語では複数形のまとまりになっているキーの 1 文（ja の "coins": "コイン {n} 枚"）。</summary>
    PluralPlain,
}
