using System;
using System.Collections.Generic;
using System.Globalization;

namespace SEEDEditor.Panels.Inspector;

/// <summary>
/// スケール連動（鎖トグル ON）のときに、1 チャンネルの編集を
/// 他チャンネルへ同じ比率で波及させる計算。
///
/// <para>
/// WPF に依存しない純粋関数だけを置く（UI・状態管理は
/// <c>InspectorPanel.ScaleLink.cs</c> の担当）。
/// <c>editor/tests/InspectorLogicTests</c> から直接検証する。
/// </para>
/// </summary>
public static class ScaleLinkCalculator
{
    /// <summary>
    /// 「値が変わった」と見なす最小差。
    ///
    /// 比較対象はどちらも同じ書式で描いたテキストを parse した値なので、
    /// 編集していないチャンネルの差はちょうど 0 になる。
    /// それでも浮動小数の丸めを踏まない余裕として小さな閾値を置く。
    /// </summary>
    public const float ChangeEpsilon = 1e-6f;

    /// <summary>小数桁数の下限（整数表示を許すので 0）。</summary>
    public const int MinDecimalPlaces = 0;

    /// <summary>小数桁数の上限（float の有効桁を超える表示をしない）。</summary>
    public const int MaxDecimalPlaces = 6;

    /// <summary>
    /// 基準値と現在値を比べ、「ちょうど 1 チャンネルだけ変化している」場合にその添字を返す。
    ///
    /// 0 チャンネル（変化なし）または 2 チャンネル以上（UI 再構築・外部更新で
    /// 基準値が古くなった等）の場合は null を返す。呼び出し側は基準値を取り直して
    /// 連動を見送る＝ユーザーが触っていない値を勝手に書き換えないための門番。
    /// </summary>
    /// <param name="baseline">編集開始時点の値。</param>
    /// <param name="current">現在の値（<paramref name="baseline"/> と同じ要素数）。</param>
    /// <param name="epsilon">変化と見なす最小差。</param>
    /// <returns>唯一変化したチャンネルの添字。特定できなければ null。</returns>
    public static int? FindSingleChangedChannel(
        IReadOnlyList<float> baseline,
        IReadOnlyList<float> current,
        float                epsilon = ChangeEpsilon)
    {
        if (baseline is null || current is null)          return null;
        if (baseline.Count == 0)                          return null;
        if (baseline.Count != current.Count)              return null;

        int found = -1;
        for (int i = 0; i < baseline.Count; i++)
        {
            if (MathF.Abs(current[i] - baseline[i]) <= epsilon) continue;
            if (found >= 0) return null;   // 2 チャンネル以上変化 → 特定不能
            found = i;
        }
        return found >= 0 ? found : null;
    }

    /// <summary>
    /// 1 チャンネルの新しい値から、連動後の全チャンネル値を求める。
    ///
    /// <para><b>比率</b>: ratio = 編集後の値 ÷ 編集前の値。
    /// 他チャンネルは「編集前の値 × ratio」になる（例: (2,4,1) の X を 2→3 で (3,6,1.5)）。</para>
    ///
    /// <para><b>0 の扱い</b>: 0 に何を掛けても 0 のままで、比率では二度と戻せない。
    /// そこで「編集前の値が 0 のチャンネルは、編集したチャンネルと同じ値にする」。
    /// 編集したチャンネル自身が 0 だった場合は比率そのものが定義できないため、
    /// 同じ方針を全チャンネルへ広げて「全チャンネルを編集後の値で揃える」。</para>
    ///
    /// <para><b>非有限</b>: 編集後の値が NaN / ∞ のときは比率を計算できないので、
    /// 編集したチャンネルだけを書き換えて他は基準値のまま返す。</para>
    /// </summary>
    /// <param name="baseline">編集前の全チャンネル値。</param>
    /// <param name="editedIndex">編集されたチャンネルの添字。</param>
    /// <param name="editedValue">編集後の値。</param>
    /// <returns>連動後の全チャンネル値（<paramref name="baseline"/> と同じ要素数の新しい配列）。</returns>
    public static float[] Compute(IReadOnlyList<float> baseline, int editedIndex, float editedValue)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        if (editedIndex < 0 || editedIndex >= baseline.Count)
            throw new ArgumentOutOfRangeException(nameof(editedIndex));

        var result = new float[baseline.Count];
        for (int i = 0; i < baseline.Count; i++) result[i] = baseline[i];
        result[editedIndex] = editedValue;

        // 非有限値は比率計算に使えない（他チャンネルまで壊さない）
        if (float.IsNaN(editedValue) || float.IsInfinity(editedValue)) return result;

        float before = baseline[editedIndex];

        // 編集したチャンネルが 0 だった → 比率が定義できないので全チャンネルを揃える
        if (MathF.Abs(before) <= ChangeEpsilon)
        {
            for (int i = 0; i < result.Length; i++) result[i] = editedValue;
            return result;
        }

        float ratio = editedValue / before;
        for (int i = 0; i < result.Length; i++)
        {
            if (i == editedIndex) continue;
            // 他チャンネルが 0 → 比率では動かせないので編集値に合わせる
            result[i] = MathF.Abs(baseline[i]) <= ChangeEpsilon
                ? editedValue
                : baseline[i] * ratio;
        }
        return result;
    }

    /// <summary>
    /// 表示テキストの小数桁数を数える（連動先の桁数を編集中のフィールドへ揃えるために使う）。
    ///
    /// 数値ドラッグ中は刻み幅から桁数が決まる（0.01 刻み → 2 桁）ため、
    /// 連動先だけ常に固定桁で描くと「3.00 と 6.000」のように桁が食い違って見える。
    /// </summary>
    /// <param name="text">フィールドの表示テキスト（不変カルチャの数値表記）。</param>
    /// <returns><see cref="MinDecimalPlaces"/>〜<see cref="MaxDecimalPlaces"/> に収めた小数桁数。</returns>
    public static int DecimalPlacesOf(string? text)
    {
        if (string.IsNullOrEmpty(text)) return MinDecimalPlaces;

        // 指数表記（1e-3 等）は桁数を素直に数えられないので下限に倒す
        if (text.IndexOf('e') >= 0 || text.IndexOf('E') >= 0) return MinDecimalPlaces;

        int dot = text.IndexOf('.');
        if (dot < 0) return MinDecimalPlaces;

        int digits = 0;
        for (int i = dot + 1; i < text.Length; i++)
        {
            if (!char.IsDigit(text[i])) break;
            digits++;
        }
        return Math.Clamp(digits, MinDecimalPlaces, MaxDecimalPlaces);
    }

    /// <summary>値を指定桁数の固定小数点表記（不変カルチャ）へ整形する。</summary>
    /// <param name="value">整形する値。</param>
    /// <param name="decimals">小数桁数。範囲外は丸める。</param>
    public static string Format(float value, int decimals)
    {
        int d = Math.Clamp(decimals, MinDecimalPlaces, MaxDecimalPlaces);
        return value.ToString("F" + d.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
    }
}
