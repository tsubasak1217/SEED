using System;
using System.Globalization;
using SEEDEditor.Panels.Inspector;
using SpriteRigTests;   // TestHarness / Check（テストランナーは SpriteRigTests と共用）

namespace InspectorLogicTests;

/// <summary>
/// インスペクタの計算ロジックの単体テスト（docs/inspector_features.md の仕様を固定する）。
///
/// 検証の柱:
///   1. 画像比率の適用（縦基準 / 横基準・正方形・反転（負値）・不正入力）
///   2. スケール連動の値計算（比率・0 の扱い・チャンネル数 2/3・非有限）
///   3. 編集チャンネルの特定（1 つだけ変化したときのみ連動する門番）
///   4. 表示桁数の追従（ドラッグ中の刻み幅に合わせる）
/// </summary>
public static class Program
{
    /// <summary>浮動小数比較の許容誤差（表示は 3〜4 桁なので十分に細かい）。</summary>
    private const double Tolerance = 1e-4;

    /// <summary>エントリポイント。全テストを実行し、失敗があれば終了コード 1 を返す。</summary>
    public static int Main()
    {
        var harness = new TestHarness();

        // ── 画像比率 ────────────────────────────────────────
        harness.Add("縦基準は高さを保ち幅を比率に合わせる",         AspectKeepHeight);
        harness.Add("横基準は幅を保ち高さを比率に合わせる",         AspectKeepWidth);
        harness.Add("正方形画像では保った辺と同じ値になる",         AspectSquareImage);
        harness.Add("画像寸法が 0 以下なら何も変えない",            AspectRejectsZeroImage);
        harness.Add("保つ辺が 0 なら何も変えない",                  AspectRejectsZeroKeptSide);
        harness.Add("反転（負値）の符号は書き換え先でも保たれる",   AspectKeepsSign);
        harness.Add("非有限な現在値は拒否する",                     AspectRejectsNonFinite);

        // ── スケール連動: 値計算 ────────────────────────────
        harness.Add("同じ比率で他チャンネルが変わる",               LinkScalesOthersByRatio);
        harness.Add("元が 0 のチャンネルは編集値と同じになる",      LinkZeroChannelTakesEditedValue);
        harness.Add("編集チャンネルが 0 なら全チャンネルが揃う",    LinkZeroEditedChannelUnifies);
        harness.Add("2 チャンネル（CanvasTransform）でも動く",      LinkTwoChannels);
        harness.Add("負の値へ編集すると符号も比率どおりになる",     LinkNegativeRatio);
        harness.Add("非有限な編集値は他チャンネルを壊さない",       LinkRejectsNonFinite);
        harness.Add("同じ基準値からの再計算は積み重ならない",       LinkFromFixedBaselineDoesNotDrift);

        // ── スケール連動: 編集チャンネルの特定 ──────────────
        harness.Add("1 つだけ変化していれば添字を返す",             ChangedSingleChannel);
        harness.Add("変化なしなら null",                            ChangedNoneReturnsNull);
        harness.Add("2 つ以上変化していたら null",                  ChangedMultipleReturnsNull);
        harness.Add("要素数が違えば null",                          ChangedLengthMismatchReturnsNull);
        harness.Add("誤差未満の差は変化とみなさない",               ChangedIgnoresEpsilon);

        // ── 表示桁数 ────────────────────────────────────────
        harness.Add("小数桁数はテキストから数える",                 DecimalPlacesFromText);
        harness.Add("整形は不変カルチャの固定小数点",               FormatUsesInvariantCulture);

        return harness.Run();
    }

    // ── 画像比率 ────────────────────────────────────────────

    /// <summary>縦基準（高さを保つ）: 200×100 の画像なら幅 = 高さ × 2。</summary>
    private static void AspectKeepHeight()
    {
        Check.True(
            ImageAspectCalculator.TryApply(200, 100, new SizePair(50f, 80f),
                ImageAspectAxis.KeepHeight, out var r),
            "計算できること");
        Check.Close(160.0, r.Width,  Tolerance, "幅");
        Check.Close(80.0,  r.Height, Tolerance, "高さ（保たれる）");
    }

    /// <summary>横基準（幅を保つ）: 200×100 の画像なら高さ = 幅 ÷ 2。</summary>
    private static void AspectKeepWidth()
    {
        Check.True(
            ImageAspectCalculator.TryApply(200, 100, new SizePair(50f, 80f),
                ImageAspectAxis.KeepWidth, out var r),
            "計算できること");
        Check.Close(50.0, r.Width,  Tolerance, "幅（保たれる）");
        Check.Close(25.0, r.Height, Tolerance, "高さ");
    }

    /// <summary>正方形画像では保った辺と同じ値になる。</summary>
    private static void AspectSquareImage()
    {
        Check.True(
            ImageAspectCalculator.TryApply(512, 512, new SizePair(30f, 70f),
                ImageAspectAxis.KeepHeight, out var r),
            "計算できること");
        Check.Close(70.0, r.Width,  Tolerance, "幅");
        Check.Close(70.0, r.Height, Tolerance, "高さ");
    }

    /// <summary>画像の寸法が取れていない（0 以下）なら比率が定義できないので拒否する。</summary>
    private static void AspectRejectsZeroImage()
    {
        var current = new SizePair(50f, 80f);
        Check.True(
            !ImageAspectCalculator.TryApply(0, 100, current, ImageAspectAxis.KeepHeight, out var r1),
            "幅 0 の画像は拒否");
        Check.Equal(current, r1, "拒否時は現在値のまま");

        Check.True(
            !ImageAspectCalculator.TryApply(100, 0, current, ImageAspectAxis.KeepWidth, out var r2),
            "高さ 0 の画像は拒否");
        Check.Equal(current, r2, "拒否時は現在値のまま");
    }

    /// <summary>保つ側の辺が 0 だと、もう片方も必ず 0 になってしまうので拒否する。</summary>
    private static void AspectRejectsZeroKeptSide()
    {
        Check.True(
            !ImageAspectCalculator.TryApply(200, 100, new SizePair(50f, 0f),
                ImageAspectAxis.KeepHeight, out _),
            "高さ 0 で縦基準は拒否");
        Check.True(
            !ImageAspectCalculator.TryApply(200, 100, new SizePair(0f, 50f),
                ImageAspectAxis.KeepWidth, out _),
            "幅 0 で横基準は拒否");
    }

    /// <summary>
    /// 負値（反転表示）を巻き込まない: 比率は絶対値から求め、
    /// 書き換える辺の符号は元の値の符号を維持する。
    /// </summary>
    private static void AspectKeepsSign()
    {
        // 幅が反転（負）のまま高さを合わせる: 高さは元が正なので正のまま
        Check.True(
            ImageAspectCalculator.TryApply(200, 100, new SizePair(-100f, 20f),
                ImageAspectAxis.KeepWidth, out var r1),
            "計算できること");
        Check.Close(-100.0, r1.Width,  Tolerance, "幅（保たれる・負のまま）");
        Check.Close(50.0,   r1.Height, Tolerance, "高さ（正のまま）");

        // 逆向き: 幅を書き換えるとき、元の幅が負なら負のままにする
        Check.True(
            ImageAspectCalculator.TryApply(200, 100, new SizePair(-100f, 20f),
                ImageAspectAxis.KeepHeight, out var r2),
            "計算できること");
        Check.Close(-40.0, r2.Width,  Tolerance, "幅（負のまま）");
        Check.Close(20.0,  r2.Height, Tolerance, "高さ（保たれる）");
    }

    /// <summary>NaN / ∞ が混じった現在値は計算しない。</summary>
    private static void AspectRejectsNonFinite()
    {
        Check.True(
            !ImageAspectCalculator.TryApply(200, 100, new SizePair(float.NaN, 80f),
                ImageAspectAxis.KeepHeight, out _),
            "NaN は拒否");
        Check.True(
            !ImageAspectCalculator.TryApply(200, 100, new SizePair(50f, float.PositiveInfinity),
                ImageAspectAxis.KeepWidth, out _),
            "∞ は拒否");
    }

    // ── スケール連動: 値計算 ────────────────────────────────

    /// <summary>(2,4,1) の X を 2→3 にしたら (3,6,1.5)。</summary>
    private static void LinkScalesOthersByRatio()
    {
        var r = ScaleLinkCalculator.Compute([2f, 4f, 1f], 0, 3f);
        Check.Close(3.0, r[0], Tolerance, "X");
        Check.Close(6.0, r[1], Tolerance, "Y");
        Check.Close(1.5, r[2], Tolerance, "Z");
    }

    /// <summary>0 のチャンネルは比率では動かせないので編集値に合わせる。</summary>
    private static void LinkZeroChannelTakesEditedValue()
    {
        var r = ScaleLinkCalculator.Compute([2f, 0f, 1f], 0, 3f);
        Check.Close(3.0, r[0], Tolerance, "X");
        Check.Close(3.0, r[1], Tolerance, "Y（0 だったので編集値）");
        Check.Close(1.5, r[2], Tolerance, "Z");
    }

    /// <summary>編集したチャンネルが 0 だと比率そのものが定義できないので全チャンネルを揃える。</summary>
    private static void LinkZeroEditedChannelUnifies()
    {
        var r = ScaleLinkCalculator.Compute([0f, 4f, 1f], 0, 3f);
        Check.Close(3.0, r[0], Tolerance, "X");
        Check.Close(3.0, r[1], Tolerance, "Y");
        Check.Close(3.0, r[2], Tolerance, "Z");
    }

    /// <summary>CanvasTransform は X/Y の 2 チャンネル。</summary>
    private static void LinkTwoChannels()
    {
        var r = ScaleLinkCalculator.Compute([2f, 5f], 1, 10f);
        Check.Equal(2, r.Length, "チャンネル数");
        Check.Close(4.0,  r[0], Tolerance, "X");
        Check.Close(10.0, r[1], Tolerance, "Y");
    }

    /// <summary>負の値へ編集したら比率も負になり、他チャンネルも反転する。</summary>
    private static void LinkNegativeRatio()
    {
        var r = ScaleLinkCalculator.Compute([2f, 4f, 1f], 0, -1f);
        Check.Close(-1.0, r[0], Tolerance, "X");
        Check.Close(-2.0, r[1], Tolerance, "Y");
        Check.Close(-0.5, r[2], Tolerance, "Z");
    }

    /// <summary>非有限な編集値では他チャンネルを書き換えない。</summary>
    private static void LinkRejectsNonFinite()
    {
        var r = ScaleLinkCalculator.Compute([2f, 4f, 1f], 0, float.NaN);
        Check.True(float.IsNaN(r[0]), "X は編集値がそのまま入る");
        Check.Close(4.0, r[1], Tolerance, "Y は基準値のまま");
        Check.Close(1.0, r[2], Tolerance, "Z は基準値のまま");
    }

    /// <summary>
    /// ドラッグ中は同じ基準値から絶対値で計算し直す。
    /// 途中経過を基準にし直した場合（積み重ね）と違い、最終値は 1 回で計算したものと一致する。
    /// </summary>
    private static void LinkFromFixedBaselineDoesNotDrift()
    {
        float[] baseline = [2f, 4f, 1f];

        // ドラッグの途中経過（基準は動かさない）
        var mid = ScaleLinkCalculator.Compute(baseline, 0, 2.1f);
        Check.Close(4.2,  mid[1], Tolerance, "途中の Y");
        Check.Close(1.05, mid[2], Tolerance, "途中の Z");

        // 最終値も同じ基準から計算する
        var end = ScaleLinkCalculator.Compute(baseline, 0, 3f);
        Check.Close(6.0, end[1], Tolerance, "最終の Y");
        Check.Close(1.5, end[2], Tolerance, "最終の Z");
    }

    // ── スケール連動: 編集チャンネルの特定 ──────────────────

    /// <summary>1 チャンネルだけ変化していればその添字を返す。</summary>
    private static void ChangedSingleChannel()
    {
        var idx = ScaleLinkCalculator.FindSingleChangedChannel([2f, 4f, 1f], [2f, 4f, 2f]);
        Check.Equal(2, idx ?? -1, "変化したチャンネル");
    }

    /// <summary>変化が無ければ null（スケール以外の編集で連動を起こさないため）。</summary>
    private static void ChangedNoneReturnsNull()
    {
        var idx = ScaleLinkCalculator.FindSingleChangedChannel([2f, 4f, 1f], [2f, 4f, 1f]);
        Check.True(idx is null, "変化なしは null");
    }

    /// <summary>2 つ以上変化していたら基準値が古い可能性があるので連動しない。</summary>
    private static void ChangedMultipleReturnsNull()
    {
        var idx = ScaleLinkCalculator.FindSingleChangedChannel([2f, 4f, 1f], [3f, 6f, 1f]);
        Check.True(idx is null, "複数変化は null");
    }

    /// <summary>チャンネル数が食い違う（2D ⇔ 3D の切り替わり直後）なら連動しない。</summary>
    private static void ChangedLengthMismatchReturnsNull()
    {
        var idx = ScaleLinkCalculator.FindSingleChangedChannel([2f, 4f], [2f, 4f, 1f]);
        Check.True(idx is null, "要素数違いは null");
    }

    /// <summary>誤差未満のゆらぎは変化とみなさない。</summary>
    private static void ChangedIgnoresEpsilon()
    {
        var idx = ScaleLinkCalculator.FindSingleChangedChannel(
            [2f, 4f, 1f],
            [2f + ScaleLinkCalculator.ChangeEpsilon * 0.5f, 4f, 1f]);
        Check.True(idx is null, "誤差未満は変化なし扱い");
    }

    // ── 表示桁数 ────────────────────────────────────────────

    /// <summary>表示テキストから小数桁数を数える（上限・指数表記・空文字の扱い込み）。</summary>
    private static void DecimalPlacesFromText()
    {
        Check.Equal(2, ScaleLinkCalculator.DecimalPlacesOf("3.00"),      "2 桁");
        Check.Equal(3, ScaleLinkCalculator.DecimalPlacesOf("-1.500"),    "負値でも数える");
        Check.Equal(0, ScaleLinkCalculator.DecimalPlacesOf("3"),         "小数点なしは 0 桁");
        Check.Equal(ScaleLinkCalculator.MaxDecimalPlaces,
                    ScaleLinkCalculator.DecimalPlacesOf("1.23456789"),   "上限で頭打ち");
        Check.Equal(0, ScaleLinkCalculator.DecimalPlacesOf("1e-3"),      "指数表記は 0 桁");
        Check.Equal(0, ScaleLinkCalculator.DecimalPlacesOf(""),          "空文字は 0 桁");
        Check.Equal(0, ScaleLinkCalculator.DecimalPlacesOf(null),        "null は 0 桁");
    }

    /// <summary>
    /// 整形は不変カルチャの固定小数点。
    /// ランタイムへ送るコマンドは不変カルチャ前提なので、
    /// 小数点がカンマになる環境で壊れないことを固定する。
    /// </summary>
    private static void FormatUsesInvariantCulture()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            // 小数点がカンマの文化圏を模す
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            Check.Equal("6.00",  ScaleLinkCalculator.Format(6f, 2),    "2 桁整形");
            Check.Equal("1.500", ScaleLinkCalculator.Format(1.5f, 3),  "3 桁整形");
            Check.Equal("2",     ScaleLinkCalculator.Format(2f, 0),    "0 桁整形");
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }
}
