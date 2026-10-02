namespace SEED.Localization;

// ============================================================
//  PluralRuleKind.cs — 複数形の規則の型（CLDR の整数の規則を簡略にしたもの。純粋）
//
//  言語ごとの規則は PluralRules の表（言語のコード → この型）で引く。整数（小数の無い数）だけを扱う。
//  CLDR の「百万の many」（フランス語・スペイン語などの 1 000 000）と小数の形は扱わない（docs/localization.md §5）。
// ============================================================

/// <summary>複数形の規則の型。</summary>
public enum PluralRuleKind
{
    /// <summary>other だけ（日本語・中国語・韓国語・タイ語・ベトナム語・インドネシア語など）。</summary>
    OtherOnly,
    /// <summary>1 は one・ほかは other（英語・ドイツ語・オランダ語・スペイン語・イタリア語など。表に無い言語の既定）。</summary>
    OneOther,
    /// <summary>0 と 1 は one・ほかは other（フランス語・ブラジルのポルトガル語・ヒンディー語など）。</summary>
    ZeroOneAsOne,
    /// <summary>1 は one・2 は two・ほかは other（ヘブライ語）。</summary>
    OneTwoOther,
    /// <summary>末尾の 1 は one・2〜4 は few・ほかは many（11〜14 は many。ロシア語・ウクライナ語・ベラルーシ語）。</summary>
    EastSlavic,
    /// <summary>1 だけ one・末尾の 2〜4 は few（12〜14 を除く）・ほかは many（ポーランド語）。</summary>
    Polish,
    /// <summary>1 は one・2〜4 は few・ほかは other（チェコ語・スロバキア語）。</summary>
    CzechSlovak,
    /// <summary>0 は zero・1 は one・2 は two・下 2 桁 3〜10 は few・11〜99 は many・ほかは other（アラビア語）。</summary>
    Arabic,
}
