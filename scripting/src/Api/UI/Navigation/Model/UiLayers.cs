using System;

namespace SEED.UI;

// ============================================================
//  UiLayers.cs — 重なる画面のレイヤーの底上げ（CanvasLayoutItem.LayerBias）の値（W2-7。純粋な計算）
//
//  SEED の 2D の描画は「ゾーン → レイヤー → 種別（スプライト → 図形 → パーティクル → テキスト）」の順なので、
//  同じレイヤーの画面を重ねると下の画面の文字が上の画面の板より手前に出る。重なる面ごとに底上げを分けて前後を決める:
//
//  | 面                         | 底上げ                                   | テーマのトークン |
//  |----------------------------|------------------------------------------|------------------|
//  | 画面のスタックの段 i        | i × 段の値（ScreenStack.LayerStep）      | layer.stack_step |
//  | 上からの覆いの帯・中の j 番 | 帯 + j × modal_step                      | layer.overlay    |
//  | 下からのシートの帯          | 同上                                     | layer.sheet      |
//  | ダイアログの帯              | 同上                                     | layer.dialog     |
//  | トーストの帯                | 帯（トーストの中は並びの順）             | layer.toast      |
//  底上げは祖先から足し合わさる（入れ子のスタック＝タブの中のスタックは、外の段の中で自分の段の値〈小さめ〉を使う）。
//  画面の中の表示のレイヤーは段の値より小さく保つこと（既定 10,000。docs/ui_navigation.md §6）。
// ============================================================

/// <summary>重なる面の種類（覆い・シート・ダイアログ）。</summary>
public enum ModalKind
{
    /// <summary>上からの覆い（プロフィール・オプション）。</summary>
    Overlay = 0,
    /// <summary>下からのシート。</summary>
    Sheet = 1,
    /// <summary>ダイアログ。</summary>
    Dialog = 2,
}

/// <summary>重なりのレイヤーの底上げの値。</summary>
public static class UiLayers
{
    /// <summary>テーマに無いときの画面のスタックの 1 段。</summary>
    public const int DefaultStackStep = 10_000;
    /// <summary>テーマに無いときの帯の中の 1 つ。</summary>
    public const int DefaultModalStep = 10_000;
    /// <summary>テーマに無いときの覆いの帯。</summary>
    public const int DefaultOverlayBand = 1_000_000;
    /// <summary>テーマに無いときのシートの帯。</summary>
    public const int DefaultSheetBand = 2_000_000;
    /// <summary>テーマに無いときのダイアログの帯。</summary>
    public const int DefaultDialogBand = 3_000_000;
    /// <summary>テーマに無いときのトーストの帯。</summary>
    public const int DefaultToastBand = 4_000_000;
    /// <summary>テーマに無いときのフォーカスの枠（方向キー・パッドの移動。2026-10-03。トーストより上＝いちばん手前）。</summary>
    public const int DefaultFocusRingBand = 5_000_000;
    /// <summary>底上げの上限（CanvasLayoutItem.LayerBias が受ける範囲 = f32 で正確な整数）。</summary>
    public const int MaxBias = 16_777_216;

    /// <summary>画面のスタックの 1 段（指定が正ならそれ、無ければテーマ）。</summary>
    public static int StackStep(UiThemeData theme, int overrideStep)
        => overrideStep > 0 ? overrideStep : ToInt(theme.Number(NavTokens.LayerStackStep, DefaultStackStep), DefaultStackStep);

    /// <summary>段 index の画面の底上げ（index × 段。範囲の外は上限へ収める）。</summary>
    public static int ScreenBias(int index, int step) => Clamp((long)Math.Max(0, index) * Math.Max(0, step));

    /// <summary>帯の中の 1 つ。</summary>
    public static int ModalStep(UiThemeData theme) => ToInt(theme.Number(NavTokens.LayerModalStep, DefaultModalStep), DefaultModalStep);

    /// <summary>面の帯。</summary>
    public static int Band(UiThemeData theme, ModalKind kind) => kind switch
    {
        ModalKind.Overlay => ToInt(theme.Number(NavTokens.LayerOverlay, DefaultOverlayBand), DefaultOverlayBand),
        ModalKind.Sheet => ToInt(theme.Number(NavTokens.LayerSheet, DefaultSheetBand), DefaultSheetBand),
        _ => ToInt(theme.Number(NavTokens.LayerDialog, DefaultDialogBand), DefaultDialogBand),
    };

    /// <summary>トーストの帯。</summary>
    public static int ToastBand(UiThemeData theme) => ToInt(theme.Number(NavTokens.LayerToast, DefaultToastBand), DefaultToastBand);

    /// <summary>フォーカスの枠の底上げ（2026-10-03。layer.focus_ring）。</summary>
    public static int FocusRingBand(UiThemeData theme)
        => ToInt(theme.Number(NavTokens.LayerFocusRing, DefaultFocusRingBand), DefaultFocusRingBand);

    /// <summary>帯の中の j 番の底上げ（帯のノードからの相対。帯そのものは帯のノードが持つ）。</summary>
    public static int ModalEntryBias(int index, int step) => ScreenBias(index, step);

    /// <summary>数のトークン → 整数（有限でなければ既定）。</summary>
    private static int ToInt(float value, int fallback) => float.IsFinite(value) ? Clamp((long)MathF.Round(value)) : fallback;

    /// <summary>底上げの範囲へ収める。</summary>
    private static int Clamp(long value) => (int)Math.Clamp(value, -MaxBias, MaxBias);
}
