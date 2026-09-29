namespace SEED.UI;

// ============================================================
//  BuiltInFontAdvance.cs — 組み込みの書体の 1 文字の送り幅（W2 の手直し P2-1。純粋な計算）
//
//  文字の寸法を測る API（Text.Measure）は W2-6c なので、ダイアログの本文・ボタン・グラフの吹き出しの大きさは見積もる（DialogLayout）。
//  その見積もりが使う「1 文字の送り幅（em = 文字の大きさに対する割合）」の表。エンジンの送り幅
//  （runtime/src/engine/core/font/text_layout.rs の advance_em）と同じ値になるように、書体の表から取った:
//    SEED の文字の大きさは ab_glyph の PxScale（＝ hhea.ascent − hhea.descent の高さ）なので、
//    送り幅（em）= hmtx の advanceWidth ÷ (hhea.ascent − hhea.descent)。字の 1 em は大きさ × unitsPerEm ÷ (ascent − descent)。
//  書体: runtime/src/engine/engine_resources/fonts/M_PLUS_Rounded_1c/MPLUSRounded1c-Regular.ttf
//  （テーマの font.family が空＝組み込みの書体のとき。ほかの書体を当てたテーマでは見積もりがずれる。docs/ui_navigation.md §13）。
//  値は 2026-09-29 に fontTools で測った: unitsPerEm 1000・hhea.ascent 1075・hhea.descent −320（高さ 1395）。OS/2 の fsSelection は
//  0x40（USE_TYPO_METRICS が立っていない＝ttf-parser は hhea の値を使う）。
//    - ASCII の印字可能文字（U+0020〜U+007E）: 文字ごとの表（AsciiAdvanceUnits。例 空白 270・数字 620・W 864）
//    - 半角カナ（U+FF61〜U+FF9F）: すべて 500
//    - 制御文字（U+0000〜U+001F。タブなど）: グリフが無いので .notdef の 364（エンジンも .notdef の送り幅で進める）
//    - それ以外: 1000。かな・漢字・全角の記号（U+3000〜U+30FF・U+4E00〜U+9FFF・U+FF01〜U+FF60）は、グリフのある文字は
//      「゠」（U+30A0 の 500）を除いてすべて 1000。この書体のほかの文字（ラテン文字の拡張・記号）とグリフの無い文字（.notdef の 364。絵文字など）の
//      多くは 1000 より狭いので大きめの見積もりになる（行が足りなくなるより 1 行余る方がよい）。
//      1000 より広いのは ‰（1040）・№（1025）・℃（1011）・一部のキリル文字とローマ数字（最大 1422）だけ（小さく見積もる。制限）
// ============================================================

/// <summary>組み込みの書体（M PLUS Rounded 1c Regular）の 1 文字の送り幅（em）。</summary>
internal static class BuiltInFontAdvance
{
    /// <summary>書体の高さ（hhea.ascent 1075 − hhea.descent −320。単位は書体の単位）。SEED の文字の大きさ 1 はこの高さ。</summary>
    public const float HeightUnits = 1395f;

    /// <summary>全角の文字（かな・漢字・全角の記号）の送り幅（書体の単位。hmtx）。</summary>
    public const int WideUnits = 1000;

    /// <summary>半角カナの送り幅（書体の単位。hmtx の U+FF61〜U+FF9F はすべて 500）。</summary>
    public const int HalfWidthKanaUnits = 500;

    /// <summary>グリフの無い文字（制御文字）の送り幅（書体の単位。hmtx の .notdef）。</summary>
    public const int NotdefUnits = 364;

    /// <summary>表にある ASCII の最初の符号位置（空白）。</summary>
    private const int AsciiFirst = 0x20;

    /// <summary>表にある ASCII の最後の符号位置（チルダ）。</summary>
    private const int AsciiLast = 0x7E;

    /// <summary>制御文字の最後の符号位置（U+001F）。</summary>
    private const int ControlLast = 0x1F;

    /// <summary>半角カナの範囲の最初（U+FF61 半角の句点）。</summary>
    private const int HalfWidthKanaFirst = 0xFF61;

    /// <summary>半角カナの範囲の終わり（U+FF9F 半角の半濁点）。</summary>
    private const int HalfWidthKanaLast = 0xFF9F;

    /// <summary>
    /// ASCII の印字可能文字（U+0020〜U+007E）の送り幅（書体の単位。hmtx の advanceWidth。2026-09-29 に fontTools で読んだ値）。
    /// 並びは符号位置の順（添字 = 符号位置 − 0x20）。
    /// </summary>
    private static readonly short[] AsciiAdvanceUnits =
    {
        270, 322, 396, 678, 565, 820, 689, 234, 363, 363, 511, 734, 278, 466, 284, 479, // U+0020〜U+002F  !"#$%&'()*+,-./
        620, 620, 620, 620, 620, 620, 620, 620, 620, 620, 364, 362, 667, 754, 667, 623, // U+0030〜U+003F 0123456789:;<=>?
        824, 639, 598, 613, 658, 587, 572, 687, 676, 316, 529, 586, 582, 816, 686, 714, // U+0040〜U+004F @ABCDEFGHIJKLMNO
        600, 722, 606, 553, 634, 656, 639, 864, 610, 610, 616, 470, 479, 470, 602, 562, // U+0050〜U+005F PQRSTUVWXYZ[\]^_
        238, 534, 578, 493, 578, 524, 508, 563, 570, 310, 355, 523, 310, 785, 565, 556, // U+0060〜U+006F `abcdefghijklmno
        574, 574, 450, 497, 502, 560, 523, 770, 504, 522, 536, 514, 344, 514, 674,      // U+0070〜U+007E pqrstuvwxyz{|}~
    };

    /// <summary>
    /// 1 文字（Unicode の符号位置。エンジンの char と同じ単位）の送り幅（書体の単位）。
    /// </summary>
    /// <param name="codePoint">符号位置。</param>
    public static int Units(int codePoint)
    {
        if (codePoint >= AsciiFirst && codePoint <= AsciiLast) return AsciiAdvanceUnits[codePoint - AsciiFirst];
        if (codePoint >= 0 && codePoint <= ControlLast) return NotdefUnits;
        if (codePoint >= HalfWidthKanaFirst && codePoint <= HalfWidthKanaLast) return HalfWidthKanaUnits;
        return WideUnits;
    }

    /// <summary>1 文字の送り幅（em。文字の大きさに対する割合＝書体の単位 ÷ 書体の高さ）。</summary>
    /// <param name="codePoint">符号位置。</param>
    public static float Em(int codePoint) => Units(codePoint) / HeightUnits;
}
