using System;

namespace SEED.UI;

// ============================================================
//  WheelLookParams.cs — ホイール（時刻ホイールの列など）の「曲面の見た目」を決める値の組（W2-5。純粋なデータ）
//
//  行は平らな一覧として縦に並び（行の高さ = 1 行ぶんのスクロールの長さ）、見た目だけを円柱に巻いたように映す。
//  円柱の形・行の詰め具合・中央の拡大・中央の外の暗さを 1 つにまとめ、WheelLook（純関数）へ渡す。
//
//  【出典】既定の値は Flutter の master のソース（2026-09-28 に取得して確かめた）から取った:
//    - packages/flutter/lib/src/cupertino/picker.dart
//        _kDefaultDiameterRatio = 1.07・_kDefaultPerspective = 0.003・_kSqueeze = 1.45・_kOverAndUnderCenterOpacity = 0.447
//    - packages/flutter/lib/src/cupertino/date_picker.dart（CupertinoDatePicker。時刻ホイールの手本）
//        _kSqueeze = 1.25・_kMagnification = 2.35 / 2.1（中央の帯の中を拡大する倍率）
//  Flutter は中央の帯の中と外を切り抜いて 2 度描く（帯の中は拡大・不透明、外は 0.447 の濃さ）。SEED は行ごとに 1 度だけ描くので、
//  「帯の中にある度合い」（行の中心と窓の中心の距離が 0 → 1、1 行ぶん離れると 0）で拡大と濃さを連続に補間する（近似）。
//  EdgeShade は Flutter に無い SEED の足し分: 円柱の面が視線から傾くほど暗くする（面の向きの余弦＝ランバートの近似。
//  実機の iOS の上下の薄れの代わり。0 で無効）。
// ============================================================

/// <summary>ホイールの曲面の見た目を決める値の組（<see cref="WheelLook"/> へ渡す）。</summary>
public readonly struct WheelLookParams
{
    /// <summary>円柱の直径 ÷ 窓の高さ（Flutter の CupertinoPicker の既定 <c>_kDefaultDiameterRatio</c>）。</summary>
    public const float CupertinoDiameterRatio = 1.07f;

    /// <summary>遠近の強さ（Flutter の <c>_kDefaultPerspective</c>。RenderListWheelViewport の上限は 0.01）。</summary>
    public const float CupertinoPerspective = 0.003f;

    /// <summary>行の詰め具合（CupertinoPicker の既定 <c>_kSqueeze</c>。大きいほど多くの行が見える）。</summary>
    public const float CupertinoPickerSqueeze = 1.45f;

    /// <summary>行の詰め具合（CupertinoDatePicker の <c>_kSqueeze</c>。時刻ホイールの既定）。</summary>
    public const float DatePickerSqueeze = 1.25f;

    /// <summary>中央の帯の中の拡大の倍率（CupertinoDatePicker の <c>_kMagnification</c> = 2.35 / 2.1）。</summary>
    public const float DatePickerMagnification = 2.35f / 2.1f;

    /// <summary>中央の帯の外の濃さ（Flutter の <c>_kOverAndUnderCenterOpacity</c>）。テーマの opacity.wheel_dim の既定と同じ。</summary>
    public const float CupertinoDimOpacity = 0.447f;

    /// <summary>面の傾きで暗くする強さの既定（1 = 面の向きの余弦をそのまま掛ける）。</summary>
    public const float DefaultEdgeShade = 1f;

    /// <summary>直径の比の下限（0 以下は円柱にならない。Flutter も 0 より大きいことを求める）。</summary>
    private const float MinDiameterRatio = 0.01f;

    /// <summary>詰め具合の下限（0 以下は割り算にならない）。</summary>
    private const float MinSqueeze = 0.01f;

    /// <summary>遠近の上限（Flutter の RenderListWheelViewport の perspective ≤ 0.01）。</summary>
    private const float MaxPerspective = 0.01f;

    /// <summary>拡大の下限（1 未満は中央が縮む＝意図しない値として 1 に揃える）。</summary>
    private const float MinMagnification = 1f;

    /// <summary>円柱の直径 ÷ 窓の高さ（1 以上なら見える角度が asin(1 / 比) まで、1 未満なら ±90° まで）。</summary>
    public float DiameterRatio { get; init; }

    /// <summary>遠近の強さ（0 = 平行投影）。</summary>
    public float Perspective { get; init; }

    /// <summary>行の詰め具合（1 = 窓の高さに平らに並べたときと同じ数、2 = 2 倍の数が見える）。</summary>
    public float Squeeze { get; init; }

    /// <summary>中央の行の拡大の倍率（1 = 拡大しない）。</summary>
    public float Magnification { get; init; }

    /// <summary>中央の帯の外の濃さ（0..1）。</summary>
    public float DimOpacity { get; init; }

    /// <summary>面の傾きで暗くする強さ（0..1）。</summary>
    public float EdgeShade { get; init; }

    /// <summary>時刻ホイールの既定（CupertinoDatePicker と同じ曲面と拡大）。</summary>
    public static WheelLookParams DatePicker => new()
    {
        DiameterRatio = CupertinoDiameterRatio,
        Perspective = CupertinoPerspective,
        Squeeze = DatePickerSqueeze,
        Magnification = DatePickerMagnification,
        DimOpacity = CupertinoDimOpacity,
        EdgeShade = DefaultEdgeShade,
    };

    /// <summary>
    /// 範囲の外・壊れた値（NaN・負）を使える値へ直した写し（見た目の計算が 0 除算・NaN を出さないように）。
    /// </summary>
    public WheelLookParams Sanitized() => new()
    {
        DiameterRatio = Math.Max(MinDiameterRatio, Finite(DiameterRatio, CupertinoDiameterRatio)),
        Perspective = Math.Clamp(Finite(Perspective, CupertinoPerspective), 0f, MaxPerspective),
        Squeeze = Math.Max(MinSqueeze, Finite(Squeeze, DatePickerSqueeze)),
        Magnification = Math.Max(MinMagnification, Finite(Magnification, DatePickerMagnification)),
        DimOpacity = Math.Clamp(Finite(DimOpacity, CupertinoDimOpacity), 0f, 1f),
        EdgeShade = Math.Clamp(Finite(EdgeShade, DefaultEdgeShade), 0f, 1f),
    };

    /// <summary>有限の値ならそのまま、NaN・無限なら代わりの値。</summary>
    private static float Finite(float value, float fallback) => float.IsFinite(value) ? value : fallback;
}
