// ============================================================
//  ByteSizeText.cs — バイト数の人間向け表記
//
//  【役割】
//  「1234567 バイト」を「1.2 MB」と書くだけの小さな整形処理。
//  テンプレートのインポート画面が、エントリのサイズ・合計サイズ・
//  コピー量を同じ書式で出すために使う。
//
//  【なぜデータ表にするか】
//  単位の刻みをテーブルで持つことで、単位を 1 段増やしたいときに
//  1 行足すだけで済み、桁ごとの分岐（if の連鎖）が増えない。
// ============================================================

using System;
using System.Globalization;

namespace SEEDEditor.Templates;

/// <summary>バイト数を表示用の文字列へ整形する。状態を持たない静的ユーティリティ。</summary>
public static class ByteSizeText
{
    /// <summary>単位 1 段の倍率（1 KB = 1024 B）。</summary>
    private const double UnitStep = 1024.0;

    /// <summary>小さい順に並べた単位記号。先頭はバイト。</summary>
    private static readonly string[] UnitSuffixes = ["B", "KB", "MB", "GB", "TB"];

    /// <summary>バイト単位のときに小数を付けないための境界インデックス。</summary>
    private const int ByteUnitIndex = 0;

    /// <summary>小数点以下の桁数（KB 以上で使う）。</summary>
    private const int FractionDigits = 1;

    /// <summary>
    /// バイト数を「1.2 MB」形式の文字列にする。
    /// </summary>
    /// <param name="bytes">バイト数（負数は 0 として扱う）。</param>
    /// <returns>表示用の文字列。</returns>
    public static string Format(long bytes)
    {
        if (bytes <= 0) return "0 " + UnitSuffixes[ByteUnitIndex];

        double value = bytes;
        int    unit  = ByteUnitIndex;

        // 表の最後の単位に達するまで、1 段の倍率を下回るまで割っていく
        while (value >= UnitStep && unit < UnitSuffixes.Length - 1)
        {
            value /= UnitStep;
            unit++;
        }

        // バイトは整数、それ以上は小数 1 桁で見せる
        var text = unit == ByteUnitIndex
            ? value.ToString("F0", CultureInfo.InvariantCulture)
            : value.ToString("F" + FractionDigits.ToString(CultureInfo.InvariantCulture),
                             CultureInfo.InvariantCulture);

        return text + " " + UnitSuffixes[unit];
    }
}
