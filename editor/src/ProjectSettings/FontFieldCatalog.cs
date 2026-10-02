// ============================================================
//  FontFieldCatalog.cs — 文字の距離場の設定（font.distance_field / font.msdf_coloring）の値の表
//
//  【役割】
//  project_settings.json の "font" 節で選べる値・既定値・読み方と、画面に出す表示名・説明を持つ。
//    distance_field … "mtsdf"（既定。"msdf" も同じ意味で読む）/ "sdf"
//    msdf_coloring  … "ink_trap"（既定。"inktrap"・"ink-trap" も同じ意味で読む）/ "simple"
//  キー名・値・既定・読み方の正典はランタイムの
//    runtime/src/engine/core/font/field_settings.rs（FONT_KEY・DISTANCE_FIELD_KEY・COLORING_KEY）
//    runtime/src/engine/core/font/glyph_field.rs（DistanceFieldKind::parse / as_str / #[default]）
//    runtime/src/engine/core/font/msdf/edge_color.rs（ColoringStrategy::parse / as_str / #[default]）
//  ここはその写しなので、ずれたら editor/tests/ProjectSystemTests（Rust のソースを読んで突き合わせる）が落ちる。
//
//  WPF に依存しない（単体テストからリンクされる）。
// ============================================================

using System;
using System.Collections.Generic;
using System.Linq;

namespace SEEDEditor.ProjectSettings;

/// <summary>文字の距離場の設定の選択肢 1 つ。</summary>
/// <param name="Value">JSON に書く値（ランタイムの as_str と同じ綴り）。</param>
/// <param name="Label">画面に出す表示名。</param>
/// <param name="Description">説明。</param>
public sealed record FontFieldChoice(string Value, string Label, string Description);

/// <summary>文字の距離場の設定の値の表と読み方（ランタイムの写し）。</summary>
public static class FontFieldCatalog
{
    // ── 距離場の種類（glyph_field.rs の DistanceFieldKind）────────────

    /// <summary>距離場の種類: MTSDF（輪郭から作る 3 チャネルの MSDF ＋真の SDF。RGBA8）。</summary>
    public const string DistanceFieldMtsdf = "mtsdf";

    /// <summary>距離場の種類: 1 チャネルの SDF（R8。2026-10-01 までの方式）。</summary>
    public const string DistanceFieldSdf = "sdf";

    /// <summary>距離場の種類の別名（ランタイムは MTSDF として読む）。</summary>
    private const string DistanceFieldMsdfAlias = "msdf";

    /// <summary>距離場の種類の既定（DistanceFieldKind の #[default]）。</summary>
    public const string DefaultDistanceField = DistanceFieldMtsdf;

    // ── 辺の色分け（edge_color.rs の ColoringStrategy）────────────────

    /// <summary>辺の色分け: 小さな角（インクトラップ）を見分けて色を置く。</summary>
    public const string ColoringInkTrap = "ink_trap";

    /// <summary>辺の色分け: 角ごとに色を巡回する。</summary>
    public const string ColoringSimple = "simple";

    /// <summary>辺の色分けの別名（ランタイムは ink trap として読む）。</summary>
    private static readonly string[] ColoringInkTrapAliases = ["inktrap", "ink-trap"];

    /// <summary>辺の色分けの既定（ColoringStrategy の #[default]）。</summary>
    public const string DefaultColoring = ColoringInkTrap;

    // ── 画面の選択肢（既定が先頭）───────────────────────────────

    /// <summary>距離場の種類の選択肢。</summary>
    public static readonly IReadOnlyList<FontFieldChoice> DistanceFieldChoices =
    [
        new(DistanceFieldMtsdf, "MTSDF（既定）",
            "輪郭から作る 3 チャネルの MSDF ＋真の SDF（RGBA8）。大きな文字の角と曲線が綺麗に出る。" +
            "小さな文字は真の SDF で描き、字形ごとの検査に落ちた字は自動で真の SDF に戻す。1 字を焼く時間は SDF の約 6〜15 倍"),
        new(DistanceFieldSdf, "SDF（従来）",
            "1 チャネルの SDF（R8。2026-10-01 までの方式）。大きく拡大すると角が丸まる。見比べ（A/B）と退避のために残している"),
    ];

    /// <summary>辺の色分けの選択肢（MTSDF のときだけ効く）。</summary>
    public static readonly IReadOnlyList<FontFieldChoice> ColoringChoices =
    [
        new(ColoringInkTrap, "ink trap（既定）",
            "角が 4 つ以上の輪郭で、小さな角（インクトラップの切れ込み）を見分けて色を置く（msdfgen の edgeColoringInkTrap）"),
        new(ColoringSimple, "simple",
            "角ごとに色を巡回する（msdfgen の edgeColoringSimple）"),
    ];

    // ── 読み方（ランタイムの parse と同じ規則）──────────────────────

    /// <summary>
    /// 距離場の種類を読む（前後の空白・大文字小文字は区別しない。"msdf" は "mtsdf"。知らなければ null）。
    /// </summary>
    /// <param name="text">入力。</param>
    /// <returns>正規化した値。</returns>
    public static string? ParseDistanceField(string? text) => NormalizeWord(text) switch
    {
        DistanceFieldSdf                               => DistanceFieldSdf,
        DistanceFieldMtsdf or DistanceFieldMsdfAlias   => DistanceFieldMtsdf,
        _                                              => null,
    };

    /// <summary>
    /// 辺の色分けを読む（前後の空白・大文字小文字は区別しない。"inktrap"・"ink-trap" は "ink_trap"。知らなければ null）。
    /// </summary>
    /// <param name="text">入力。</param>
    /// <returns>正規化した値。</returns>
    public static string? ParseColoring(string? text)
    {
        var word = NormalizeWord(text);
        if (word == ColoringSimple) return ColoringSimple;
        if (word == ColoringInkTrap || ColoringInkTrapAliases.Contains(word)) return ColoringInkTrap;
        return null;
    }

    /// <summary>距離場の種類の選択肢を引く（無ければ null）。</summary>
    /// <param name="value">正規化した値。</param>
    /// <returns>選択肢。</returns>
    public static FontFieldChoice? FindDistanceField(string? value) =>
        DistanceFieldChoices.FirstOrDefault(c => string.Equals(c.Value, value, StringComparison.Ordinal));

    /// <summary>辺の色分けの選択肢を引く（無ければ null）。</summary>
    /// <param name="value">正規化した値。</param>
    /// <returns>選択肢。</returns>
    public static FontFieldChoice? FindColoring(string? value) =>
        ColoringChoices.FirstOrDefault(c => string.Equals(c.Value, value, StringComparison.Ordinal));

    /// <summary>前後の空白を落として小文字にする（ランタイムの trim + to_ascii_lowercase と同じ扱い）。</summary>
    /// <param name="text">入力。</param>
    /// <returns>正規化した文字列（null は空）。</returns>
    private static string NormalizeWord(string? text) => (text ?? string.Empty).Trim().ToLowerInvariant();
}
