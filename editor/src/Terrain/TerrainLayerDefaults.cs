// ============================================================
//  TerrainLayerDefaults.cs — 地形レイヤ定義の既定値（Rust 側の単一の写し）
//
//  【責務】
//    layers.json の各フィールドについて「省略時にランタイムが採用する値」を
//    エディタ側でも一元管理する。マジックナンバーを UI コードへ散らさず、
//    かつ「既定値と一致する未記述フィールドは書き出さない」差分保存
//    （TerrainLayersDocument）の判定基準としても使う。
//
//  【同期義務】
//    ここの値は runtime/src/engine/terrain/layers.rs の DEFAULT_* 定数と
//    一致させること。片方だけ変えると、エディタが「既定値だから省略」と
//    判断したフィールドをランタイムが別の値で解釈し、見た目が変わる。
// ============================================================

namespace SEEDEditor.Terrain;

/// <summary>
/// layers.json の既定値テーブル。
/// 対応する Rust 定数: runtime/src/engine/terrain/layers.rs の DEFAULT_LAYER_* / DEFAULT_SLOPE_* / DEFAULT_HEIGHT_* / DEFAULT_PRIORITY。
/// </summary>
internal static class TerrainLayerDefaults
{
    // ── レイヤ本体 ────────────────────────────────────────────

    /// <summary>ベースカラー（リニア RGB）の既定値。中間グレー。</summary>
    public static readonly double[] BaseColor = { 0.5, 0.5, 0.5 };

    /// <summary>ラフネスの既定値（自然物は概ね粗い）。</summary>
    public const double Roughness = 0.9;

    /// <summary>メタリックの既定値（自然物は非金属）。</summary>
    public const double Metallic = 0.0;

    /// <summary>triplanar UV スケールの既定値（ワールド 1m あたりの UV 進み量）。</summary>
    public const double UvScale = 0.25;

    /// <summary>タイリング解消モードの既定値（"none" = 単純タイリング）。</summary>
    public const string Detile = "none";

    /// <summary>タイリング解消強度の既定値（1.0 = モードの効果をフル適用）。</summary>
    public const double DetileStrength = 1.0;

    // ── 自動ペイントルール ────────────────────────────────────

    /// <summary>斜度ウィンドウ下限の既定値（度）。0 度 = 完全な平地。</summary>
    public const double SlopeMinDeg = 0.0;

    /// <summary>斜度ウィンドウ上限の既定値（度）。90 度 = 垂直な崖。</summary>
    public const double SlopeMaxDeg = 90.0;

    /// <summary>斜度ウィンドウのフェード幅の既定値（度）。</summary>
    public const double SlopeFadeDeg = 8.0;

    /// <summary>高度ウィンドウ下限の既定値（メートル）。実質「制限なし」。</summary>
    public const double HeightMin = -1.0e6;

    /// <summary>高度ウィンドウ上限の既定値（メートル）。実質「制限なし」。</summary>
    public const double HeightMax = 1.0e6;

    /// <summary>高度ウィンドウのフェード幅の既定値（メートル）。</summary>
    public const double HeightFade = 2.0;

    /// <summary>ルール基礎重み（優先度）の既定値。</summary>
    public const double Priority = 1.0;

    // ── UI・検証用のレンジ定数 ────────────────────────────────

    /// <summary>斜度の物理的な下限（度）。UI 入力のクランプに使う。</summary>
    public const double SlopeRangeMin = 0.0;

    /// <summary>斜度の物理的な上限（度）。法線 Y から acos で求めるため 90 を超えない。</summary>
    public const double SlopeRangeMax = 90.0;

    /// <summary>0..1 に収まるべきパラメータ（roughness/metallic/detile_strength）の下限。</summary>
    public const double UnitRangeMin = 0.0;

    /// <summary>0..1 に収まるべきパラメータの上限。</summary>
    public const double UnitRangeMax = 1.0;

    /// <summary>
    /// layers.json に定義できるレイヤ数の上限。
    /// 対応する Rust 定数: layers.rs の TERRAIN_MAX_LAYERS。
    /// これを超えるとランタイム読み込み時に切り詰められるため、エディタ側で追加を止める。
    /// </summary>
    public const int MaxLayers = 16;

    /// <summary>
    /// 浮動小数の「既定値と同じか」判定に使う許容誤差。
    /// JSON 経由で double へ往復するため厳密比較は使えない。
    /// </summary>
    public const double Epsilon = 1.0e-9;

    /// <summary>タイリング解消モードの選択肢（layers.rs の DetileMode と対応。順序は UI の表示順）。</summary>
    public static readonly string[] DetileModes = { "none", "stochastic", "macro" };
}
