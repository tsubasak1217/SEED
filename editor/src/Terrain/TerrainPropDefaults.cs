// ============================================================
//  TerrainPropDefaults.cs — 地形プロップ定義の既定値（Rust 側の単一の写し）
//
//  【責務】
//    props.json の各フィールドについて「省略時にランタイムが採用する値」を
//    エディタ側でも一元管理する。マジックナンバーを UI コードへ散らさず、
//    かつ「既定値と一致する未記述フィールドは書き出さない」差分保存
//    （TerrainPropsDocument）の判定基準としても使う。
//
//  【同期義務】
//    ここの値は runtime/src/engine/terrain/scatter/props.rs の DEFAULT_* 定数と
//    一致させること。片方だけ変えると、エディタが「既定値だから省略」と
//    判断したフィールドをランタイムが別の値で解釈し、見た目が変わる。
//
//  【なぜ TerrainLayerDefaults と分けるのか】
//    レイヤ（見た目のマテリアル）とプロップ（散布物）は別ファイル・別ライフサイクルの
//    データであり、同期先の Rust ファイルも layers.rs / scatter/props.rs と別である。
//    片方の定数表に相乗りさせると「どちらの正典と揃えるべきか」が曖昧になるため分離する。
// ============================================================

namespace SEEDEditor.Terrain;

/// <summary>
/// props.json の既定値テーブル。
/// 対応する Rust 定数: runtime/src/engine/terrain/scatter/props.rs の DEFAULT_GRASS_* /
/// DEFAULT_WIND_* / DEFAULT_SCATTER_* / DEFAULT_RULE_* / DEFAULT_LAYER_CONDITION_*。
/// </summary>
internal static class TerrainPropDefaults
{
    // ── プロップ種別（PropKind）────────────────────────────────

    /// <summary>プロップ種別 "grass"（手続き生成の草）。Rust PropKind::Grass の serde 表現。</summary>
    public const string KindGrass = "grass";

    /// <summary>プロップ種別 "model"（外部モデルアセット）。Rust PropKind::Model の serde 表現。</summary>
    public const string KindModel = "model";

    /// <summary>プロップ種別 "actor"（プレハブから実アクタを生成）。Rust PropKind::Actor の serde 表現。</summary>
    public const string KindActor = "actor";

    /// <summary>プロップ種別の既定値（Rust の <c>#[default] Grass</c> と一致）。</summary>
    public const string Kind = KindGrass;

    /// <summary>プロップ種別の選択肢（順序は UI の表示順。値は JSON へ書く小文字表記）。</summary>
    public static readonly string[] Kinds = { KindGrass, KindModel, KindActor };

    /// <summary>プロップ種別の日本語表示名（<see cref="Kinds"/> と同じ並び）。</summary>
    public static readonly string[] KindDisplayNames = { "草", "モデル", "アクタ（プレハブ）" };

    // ── 草パラメータ（GrassParams）────────────────────────────

    /// <summary>草の根元の葉幅の既定値（メートル）。</summary>
    public const double GrassWidth = 0.055;

    /// <summary>草の高さの既定値（メートル）。</summary>
    public const double GrassHeight = 0.38;

    /// <summary>草の高さのランダム変動率の既定値（0=全て同じ高さ）。</summary>
    public const double GrassHeightVariance = 0.35;

    /// <summary>草 1 本の縦分割数の既定値。</summary>
    public const int GrassSegments = 4;

    /// <summary>十字 2 枚構成にするかの既定値。</summary>
    public const bool GrassCrossPlanes = true;

    /// <summary>静止時の自然な垂れの既定値（0=直立）。</summary>
    public const double GrassBend = 0.25;

    /// <summary>草の根元色の既定値（リニア RGB）。</summary>
    public static readonly double[] GrassColorBottom = { 0.08, 0.20, 0.05 };

    /// <summary>草の先端色の既定値（リニア RGB）。</summary>
    public static readonly double[] GrassColorTop = { 0.34, 0.58, 0.16 };

    /// <summary>草のラフネスの既定値。</summary>
    public const double GrassRoughness = 0.85;

    /// <summary>先端アルファカットアウト閾値の既定値（0=無効）。</summary>
    public const double GrassTipAlphaCutoff = 0.0;

    /// <summary>
    /// 法線を地表法線へ寄せる割合の既定値。
    /// 0 = 真の幾何法線、1 = 完全に地表法線。板ポリの草が真っ黒に見えるのを防ぐ（normal flattening）。
    /// </summary>
    public const double GrassNormalUpBlend = 0.7;

    // ── 風パラメータ（WindParams）─────────────────────────────

    /// <summary>風による横揺れ幅の既定値（草の高さに対する比率）。</summary>
    public const double WindStrength = 0.16;

    /// <summary>風の基本振動速度の既定値。</summary>
    public const double WindSpeed = 1.4;

    /// <summary>風の空間周波数の既定値（ワールド 1m あたりの位相進み）。</summary>
    public const double WindFrequency = 0.35;

    /// <summary>突風成分の強さの既定値。</summary>
    public const double WindGustStrength = 0.35;

    /// <summary>突風成分の進行速度の既定値。</summary>
    public const double WindGustSpeed = 0.25;

    // ── 散布パラメータ（ScatterParams）────────────────────────

    /// <summary>1 m² あたりの候補点数の既定値。</summary>
    public const double ScatterDensity = 4.0;

    /// <summary>スケール下限の既定値。</summary>
    public const double ScatterScaleMin = 0.85;

    /// <summary>スケール上限の既定値。</summary>
    public const double ScatterScaleMax = 1.25;

    /// <summary>地表法線に沿わせるかの既定値。</summary>
    public const bool ScatterAlignToNormal = true;

    /// <summary>ランダム傾き上限の既定値（度）。</summary>
    public const double ScatterTiltMaxDeg = 8.0;

    /// <summary>Y 軸まわりのランダム回転を掛けるかの既定値。</summary>
    public const bool ScatterRandomYaw = true;

    // ── 散布ルール（ScatterRule）──────────────────────────────

    /// <summary>斜度ウィンドウ下限の既定値（度）。</summary>
    public const double RuleSlopeMinDeg = 0.0;

    /// <summary>斜度ウィンドウ上限の既定値（度）。</summary>
    public const double RuleSlopeMaxDeg = 90.0;

    /// <summary>斜度ウィンドウのフェード幅の既定値（度）。</summary>
    public const double RuleSlopeFadeDeg = 8.0;

    /// <summary>高度ウィンドウ下限の既定値（メートル）。実質「制限なし」。</summary>
    public const double RuleHeightMin = -1.0e6;

    /// <summary>高度ウィンドウ上限の既定値（メートル）。実質「制限なし」。</summary>
    public const double RuleHeightMax = 1.0e6;

    /// <summary>高度ウィンドウのフェード幅の既定値（メートル）。</summary>
    public const double RuleHeightFade = 2.0;

    /// <summary>最終確率の足切り閾値の既定値。</summary>
    public const double RuleThreshold = 0.02;

    /// <summary>レイヤ条件の既定要求重み。</summary>
    public const double LayerConditionMinWeight = 0.5;

    // ── UI・検証用のレンジ定数 ────────────────────────────────

    /// <summary>
    /// props.json に定義できるプロップ数の上限。
    /// 対応する Rust 定数: props.rs の TERRAIN_MAX_PROPS。
    /// これを超える定義は読み込み時に切り詰められるため、エディタ側で追加を止める。
    /// </summary>
    public const int MaxProps = 64;

    /// <summary>
    /// 草 1 本の縦分割数の上限。
    /// 対応する Rust 定数: props.rs の GRASS_MAX_SEGMENTS。
    /// </summary>
    public const int GrassMaxSegments = 8;

    /// <summary>草の縦分割数の下限（0 は不正。Rust の clamped_segments() と同じ下限）。</summary>
    public const int GrassMinSegments = 1;

    /// <summary>斜度の物理的な下限（度）。UI 入力のクランプに使う。</summary>
    public const double SlopeRangeMin = 0.0;

    /// <summary>斜度の物理的な上限（度）。</summary>
    public const double SlopeRangeMax = 90.0;

    /// <summary>0..1 に収まるべきパラメータ（重み・閾値・ラフネス等）の下限。</summary>
    public const double UnitRangeMin = 0.0;

    /// <summary>0..1 に収まるべきパラメータの上限。</summary>
    public const double UnitRangeMax = 1.0;

    /// <summary>負値を許さないパラメータ（幅・高さ・密度・スケール等）の下限。</summary>
    public const double NonNegativeMin = 0.0;

    /// <summary>ランダム傾き上限として許す最大値（度）。真横に倒れる 90 度を上限とする。</summary>
    public const double TiltRangeMax = 90.0;

    /// <summary>
    /// 浮動小数の「既定値と同じか」判定に使う許容誤差。
    /// JSON 経由で double へ往復するため厳密比較は使えない。
    /// </summary>
    public const double Epsilon = 1.0e-9;

    /// <summary>色（リニア RGB）の成分数。</summary>
    public const int ColorComponentCount = 3;
}
