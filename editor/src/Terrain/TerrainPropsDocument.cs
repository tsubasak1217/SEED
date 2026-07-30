// ============================================================
//  TerrainPropsDocument.cs — props.json の読み書き（差分更新モデル）
//
//  【責務】
//    地形プロップ（散布物）定義ファイル（assets/terrain/props.json）を
//    エディタで編集するためのドキュメントモデル。
//
//  【なぜ POCO への deserialize ではなく JsonNode 差分更新なのか】
//    layers.json と同じ理由（TerrainLayersDocument の冒頭コメント参照）。
//    props.json はランタイム（Rust: terrain/scatter/props.rs）が正典であり、
//    エディタが知らないフィールドが今後増えうる（"_comment"、将来の LOD 設定など）。
//    C# の型へ deserialize → serialize すると型に無いフィールドが黙って消えるため、
//    元 JSON を JsonNode ツリーとして保持し、編集したキーだけを上書きする。
//
//  【既定値と同じ値は書き出さない】
//    Rust 側は id/name/layer_conditions[].layer 以外の全フィールドが
//    #[serde(default)] のため、省略＝既定値である。
//    「元ファイルに無く、かつ既定値のまま」のフィールドは書き出さないことで、
//    エディタで開いて保存しただけのファイルが肥大化しないようにする。
//    逆に「元ファイルにあった」フィールドは値が既定値でも書き続ける（保存性優先）。
//
//  【必須フィールド】
//    id / name / layer_conditions[].layer は Rust 側に serde default が無いため、
//    値が空でも必ず出力する（省略すると deserialize が失敗し、ランタイムが
//    既定プロップセットへフォールバックしてしまう）。
// ============================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Unicode;

namespace SEEDEditor.Terrain;

// ============================================================
//  TerrainPropJson — JsonNode 差分更新の共通ヘルパ
// ============================================================

/// <summary>
/// props.json の差分更新で使う JsonObject 読み書きヘルパ。
/// <para>
/// 「既定値かつ元ファイルに無ければ書かない」という省略規則を 1 箇所へ集約し、
/// 各パラメータ構造体（草・風・散布・ルール）が同じ流儀で書き戻せるようにする。
/// </para>
/// </summary>
internal static class TerrainPropJson
{
    // ── 読み出し ──────────────────────────────────────────────

    /// <summary>数値キーを読む。存在しない／数値でない場合は既定値を返す。</summary>
    public static double ReadDouble(JsonObject obj, string key, double fallback)
        => obj[key] is JsonValue v && v.TryGetValue<double>(out var d) ? d : fallback;

    /// <summary>整数キーを読む。存在しない／整数でない場合は既定値を返す。</summary>
    public static int ReadInt(JsonObject obj, string key, int fallback)
        => obj[key] is JsonValue v && v.TryGetValue<int>(out var i) ? i : fallback;

    /// <summary>真偽キーを読む。存在しない／真偽でない場合は既定値を返す。</summary>
    public static bool ReadBool(JsonObject obj, string key, bool fallback)
        => obj[key] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : fallback;

    /// <summary>文字列キーを読む。存在しない／null の場合は null を返す。</summary>
    public static string? ReadString(JsonObject obj, string key)
        => obj[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    /// <summary>
    /// RGB 配列キーを読む。要素数が足りない／数値でない成分は既定値で埋める。
    /// </summary>
    public static double[] ReadColor(JsonObject obj, string key, double[] fallback)
    {
        var result = (double[])fallback.Clone();
        if (obj[key] is not JsonArray arr) return result;
        for (int i = 0; i < TerrainPropDefaults.ColorComponentCount && i < arr.Count; i++)
        {
            if (arr[i] is JsonValue v && v.TryGetValue<double>(out var d))
                result[i] = d;
        }
        return result;
    }

    /// <summary>
    /// 子オブジェクトを取り出す（無ければ空の JsonObject）。
    /// 呼び出し側は戻り値へ書き戻したうえで <see cref="AttachIfMeaningful"/> でぶら下げる。
    /// </summary>
    public static JsonObject Child(JsonObject obj, string key)
        => obj[key] as JsonObject ?? new JsonObject();

    // ── 書き込み（既定値なら省略）─────────────────────────────

    /// <summary>数値を書き込む。元から存在したキー、または既定値と異なる値のときだけ出力する。</summary>
    public static void SetOrOmit(JsonObject obj, string key, double value, double defaultValue)
    {
        if (obj.ContainsKey(key) || Math.Abs(value - defaultValue) > TerrainPropDefaults.Epsilon)
            obj[key] = JsonValue.Create(value);
    }

    /// <summary>整数を書き込む。元から存在したキー、または既定値と異なる値のときだけ出力する。</summary>
    public static void SetIntOrOmit(JsonObject obj, string key, int value, int defaultValue)
    {
        if (obj.ContainsKey(key) || value != defaultValue)
            obj[key] = JsonValue.Create(value);
    }

    /// <summary>真偽値を書き込む。元から存在したキー、または既定値と異なる値のときだけ出力する。</summary>
    public static void SetBoolOrOmit(JsonObject obj, string key, bool value, bool defaultValue)
    {
        if (obj.ContainsKey(key) || value != defaultValue)
            obj[key] = JsonValue.Create(value);
    }

    /// <summary>文字列列挙値（kind 等）を書き込む。既定値かつ元から無ければ省略する。</summary>
    public static void SetEnumOrOmit(JsonObject obj, string key, string value, string defaultValue)
    {
        if (obj.ContainsKey(key) || !string.Equals(value, defaultValue, StringComparison.Ordinal))
            obj[key] = JsonValue.Create(value);
    }

    /// <summary>
    /// 任意（null 可）のパス文字列を書き込む。
    /// 空文字は「未設定」とみなして明示的な JSON null にする
    /// （Rust 側は Option&lt;String&gt; なので "" は「空パスのモデル」という別の意味になってしまう）。
    /// null かつ元から無ければキーごと省略する。
    /// </summary>
    public static void SetOptionalStringOrOmit(JsonObject obj, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            obj[key] = JsonValue.Create(value);
        else if (obj.ContainsKey(key))
            obj[key] = null;
    }

    /// <summary>RGB 配列を書き込む。全成分が既定値と一致し、かつ元から無ければ省略する。</summary>
    public static void SetColorOrOmit(JsonObject obj, string key, double[] value, double[] defaultValue)
    {
        bool differs = false;
        for (int i = 0; i < TerrainPropDefaults.ColorComponentCount; i++)
        {
            if (Math.Abs(value[i] - defaultValue[i]) > TerrainPropDefaults.Epsilon) { differs = true; break; }
        }
        if (!obj.ContainsKey(key) && !differs) return;

        var arr = new JsonArray();
        for (int i = 0; i < TerrainPropDefaults.ColorComponentCount; i++) arr.Add(JsonValue.Create(value[i]));
        obj[key] = arr;
    }

    /// <summary>
    /// 書き戻し済みの子オブジェクトを親へぶら下げる。
    /// 「元から存在した」か「中身が 1 つ以上ある（＝既定値以外が書かれた）」ときのみ出力する。
    /// </summary>
    /// <param name="parent">親オブジェクト。</param>
    /// <param name="key">子オブジェクトのキー。</param>
    /// <param name="child">書き戻し済みの子オブジェクト。</param>
    /// <param name="existed">読み込み時に親へ存在したか。</param>
    public static void AttachIfMeaningful(JsonObject parent, string key, JsonObject child, bool existed)
    {
        if (!existed && child.Count == 0) return;

        // 元から存在した場合、child は parent[key] に既にぶら下がっている「生きたノード」であり、
        // 編集値はその場で書き込み済みである。同じノードを同じスロットへ代入し直すと
        // JsonNode の「1 ノード 1 親」制約に触れうるため、同一参照なら何もしない。
        if (ReferenceEquals(parent[key], child)) return;

        parent[key] = child;
    }
}

// ============================================================
//  GrassParamsEdit — 草 1 本の形状パラメータ
// ============================================================

/// <summary>
/// 手続き生成される草の形状・見た目パラメータ（props.json の "grass"）。
/// <c>kind = grass</c> のときだけ意味を持つ。
/// 対応する Rust 型: props.rs の <c>GrassParams</c>。
/// </summary>
internal sealed class GrassParamsEdit
{
    private const string KeyWidth          = "width";
    private const string KeyHeight         = "height";
    private const string KeyHeightVariance = "height_variance";
    private const string KeySegments       = "segments";
    private const string KeyCrossPlanes    = "cross_planes";
    private const string KeyBend           = "bend";
    private const string KeyColorBottom    = "color_bottom";
    private const string KeyColorTop       = "color_top";
    private const string KeyRoughness      = "roughness";
    private const string KeyTipAlphaCutoff = "tip_alpha_cutoff";
    private const string KeyNormalUpBlend  = "normal_up_blend";

    /// <summary>元 JSON オブジェクト（未知キー保持用）。</summary>
    private readonly JsonObject _source;

    /// <summary>読み込み時に親へ "grass" キーが存在したか（保存時の出力判定に使う）。</summary>
    public bool Existed { get; }

    /// <summary>根元の葉幅（メートル）。</summary>
    public double Width { get; set; } = TerrainPropDefaults.GrassWidth;

    /// <summary>葉の高さ（メートル）。</summary>
    public double Height { get; set; } = TerrainPropDefaults.GrassHeight;

    /// <summary>高さのランダム変動率（0..1）。</summary>
    public double HeightVariance { get; set; } = TerrainPropDefaults.GrassHeightVariance;

    /// <summary>縦分割数（1..GrassMaxSegments）。</summary>
    public int Segments { get; set; } = TerrainPropDefaults.GrassSegments;

    /// <summary>十字 2 枚構成にするか。</summary>
    public bool CrossPlanes { get; set; } = TerrainPropDefaults.GrassCrossPlanes;

    /// <summary>静止時の自然な垂れ（0=直立）。</summary>
    public double Bend { get; set; } = TerrainPropDefaults.GrassBend;

    /// <summary>根元色（リニア RGB）。</summary>
    public double[] ColorBottom { get; set; } = (double[])TerrainPropDefaults.GrassColorBottom.Clone();

    /// <summary>先端色（リニア RGB）。</summary>
    public double[] ColorTop { get; set; } = (double[])TerrainPropDefaults.GrassColorTop.Clone();

    /// <summary>ラフネス（0..1）。</summary>
    public double Roughness { get; set; } = TerrainPropDefaults.GrassRoughness;

    /// <summary>先端アルファカットアウト閾値（0=無効）。</summary>
    public double TipAlphaCutoff { get; set; } = TerrainPropDefaults.GrassTipAlphaCutoff;

    /// <summary>
    /// 法線を地表法線へ寄せる割合（0..1）。
    /// 0 = 真の幾何法線、1 = 完全に地表法線。板ポリの草が真っ黒に見えるのを防ぐ（normal flattening）。
    /// </summary>
    public double NormalUpBlend { get; set; } = TerrainPropDefaults.GrassNormalUpBlend;

    /// <summary>親オブジェクトの "grass" キーから編集状態を復元する。</summary>
    public GrassParamsEdit(JsonObject parent, string key)
    {
        Existed = parent[key] is JsonObject;
        _source = TerrainPropJson.Child(parent, key);

        Width          = TerrainPropJson.ReadDouble(_source, KeyWidth,          TerrainPropDefaults.GrassWidth);
        Height         = TerrainPropJson.ReadDouble(_source, KeyHeight,         TerrainPropDefaults.GrassHeight);
        HeightVariance = TerrainPropJson.ReadDouble(_source, KeyHeightVariance, TerrainPropDefaults.GrassHeightVariance);
        Segments       = TerrainPropJson.ReadInt   (_source, KeySegments,       TerrainPropDefaults.GrassSegments);
        CrossPlanes    = TerrainPropJson.ReadBool  (_source, KeyCrossPlanes,    TerrainPropDefaults.GrassCrossPlanes);
        Bend           = TerrainPropJson.ReadDouble(_source, KeyBend,           TerrainPropDefaults.GrassBend);
        ColorBottom    = TerrainPropJson.ReadColor (_source, KeyColorBottom,    TerrainPropDefaults.GrassColorBottom);
        ColorTop       = TerrainPropJson.ReadColor (_source, KeyColorTop,       TerrainPropDefaults.GrassColorTop);
        Roughness      = TerrainPropJson.ReadDouble(_source, KeyRoughness,      TerrainPropDefaults.GrassRoughness);
        TipAlphaCutoff = TerrainPropJson.ReadDouble(_source, KeyTipAlphaCutoff, TerrainPropDefaults.GrassTipAlphaCutoff);
        NormalUpBlend  = TerrainPropJson.ReadDouble(_source, KeyNormalUpBlend,  TerrainPropDefaults.GrassNormalUpBlend);
    }

    /// <summary>編集値を親オブジェクトへ書き戻す（全て既定値かつ元から無ければ書かない）。</summary>
    public void WriteBack(JsonObject parent, string key)
    {
        TerrainPropJson.SetOrOmit     (_source, KeyWidth,          Width,          TerrainPropDefaults.GrassWidth);
        TerrainPropJson.SetOrOmit     (_source, KeyHeight,         Height,         TerrainPropDefaults.GrassHeight);
        TerrainPropJson.SetOrOmit     (_source, KeyHeightVariance, HeightVariance, TerrainPropDefaults.GrassHeightVariance);
        TerrainPropJson.SetIntOrOmit  (_source, KeySegments,       Segments,       TerrainPropDefaults.GrassSegments);
        TerrainPropJson.SetBoolOrOmit (_source, KeyCrossPlanes,    CrossPlanes,    TerrainPropDefaults.GrassCrossPlanes);
        TerrainPropJson.SetOrOmit     (_source, KeyBend,           Bend,           TerrainPropDefaults.GrassBend);
        TerrainPropJson.SetColorOrOmit(_source, KeyColorBottom,    ColorBottom,    TerrainPropDefaults.GrassColorBottom);
        TerrainPropJson.SetColorOrOmit(_source, KeyColorTop,       ColorTop,       TerrainPropDefaults.GrassColorTop);
        TerrainPropJson.SetOrOmit     (_source, KeyRoughness,      Roughness,      TerrainPropDefaults.GrassRoughness);
        TerrainPropJson.SetOrOmit     (_source, KeyTipAlphaCutoff, TipAlphaCutoff, TerrainPropDefaults.GrassTipAlphaCutoff);
        TerrainPropJson.SetOrOmit     (_source, KeyNormalUpBlend,  NormalUpBlend,  TerrainPropDefaults.GrassNormalUpBlend);

        TerrainPropJson.AttachIfMeaningful(parent, key, _source, Existed);
    }
}

// ============================================================
//  WindParamsEdit — 風による揺れのパラメータ
// ============================================================

/// <summary>
/// 風による揺れのパラメータ（props.json の "wind"）。
/// 対応する Rust 型: props.rs の <c>WindParams</c>。
/// </summary>
internal sealed class WindParamsEdit
{
    private const string KeyStrength     = "strength";
    private const string KeySpeed        = "speed";
    private const string KeyFrequency    = "frequency";
    private const string KeyGustStrength = "gust_strength";
    private const string KeyGustSpeed    = "gust_speed";

    private readonly JsonObject _source;

    /// <summary>読み込み時に親へ "wind" キーが存在したか。</summary>
    public bool Existed { get; }

    /// <summary>横揺れ幅（草の高さに対する比率）。0 で風の影響なし。</summary>
    public double Strength { get; set; } = TerrainPropDefaults.WindStrength;

    /// <summary>基本振動の時間速度。</summary>
    public double Speed { get; set; } = TerrainPropDefaults.WindSpeed;

    /// <summary>基本振動の空間周波数（ワールド 1m あたりの位相進み）。</summary>
    public double Frequency { get; set; } = TerrainPropDefaults.WindFrequency;

    /// <summary>突風成分の強さ。</summary>
    public double GustStrength { get; set; } = TerrainPropDefaults.WindGustStrength;

    /// <summary>突風成分の進行速度。</summary>
    public double GustSpeed { get; set; } = TerrainPropDefaults.WindGustSpeed;

    /// <summary>親オブジェクトの "wind" キーから編集状態を復元する。</summary>
    public WindParamsEdit(JsonObject parent, string key)
    {
        Existed = parent[key] is JsonObject;
        _source = TerrainPropJson.Child(parent, key);

        Strength     = TerrainPropJson.ReadDouble(_source, KeyStrength,     TerrainPropDefaults.WindStrength);
        Speed        = TerrainPropJson.ReadDouble(_source, KeySpeed,        TerrainPropDefaults.WindSpeed);
        Frequency    = TerrainPropJson.ReadDouble(_source, KeyFrequency,    TerrainPropDefaults.WindFrequency);
        GustStrength = TerrainPropJson.ReadDouble(_source, KeyGustStrength, TerrainPropDefaults.WindGustStrength);
        GustSpeed    = TerrainPropJson.ReadDouble(_source, KeyGustSpeed,    TerrainPropDefaults.WindGustSpeed);
    }

    /// <summary>編集値を親オブジェクトへ書き戻す。</summary>
    public void WriteBack(JsonObject parent, string key)
    {
        TerrainPropJson.SetOrOmit(_source, KeyStrength,     Strength,     TerrainPropDefaults.WindStrength);
        TerrainPropJson.SetOrOmit(_source, KeySpeed,        Speed,        TerrainPropDefaults.WindSpeed);
        TerrainPropJson.SetOrOmit(_source, KeyFrequency,    Frequency,    TerrainPropDefaults.WindFrequency);
        TerrainPropJson.SetOrOmit(_source, KeyGustStrength, GustStrength, TerrainPropDefaults.WindGustStrength);
        TerrainPropJson.SetOrOmit(_source, KeyGustSpeed,    GustSpeed,    TerrainPropDefaults.WindGustSpeed);

        TerrainPropJson.AttachIfMeaningful(parent, key, _source, Existed);
    }
}

// ============================================================
//  ScatterParamsEdit — 散布の密度・姿勢パラメータ
// ============================================================

/// <summary>
/// 散布インスタンスの密度と姿勢のばらつき（props.json の "scatter"）。
/// 対応する Rust 型: props.rs の <c>ScatterParams</c>。
/// </summary>
internal sealed class ScatterParamsEdit
{
    private const string KeyDensity       = "density";
    private const string KeyScaleMin      = "scale_min";
    private const string KeyScaleMax      = "scale_max";
    private const string KeyAlignToNormal = "align_to_normal";
    private const string KeyTiltMaxDeg    = "tilt_max_deg";
    private const string KeyRandomYaw     = "random_yaw";

    private readonly JsonObject _source;

    /// <summary>読み込み時に親へ "scatter" キーが存在したか。</summary>
    public bool Existed { get; }

    /// <summary>1 m² あたりの候補点数（確定本数ではない）。</summary>
    public double Density { get; set; } = TerrainPropDefaults.ScatterDensity;

    /// <summary>スケール下限（1.0 = 定義そのままのサイズ）。</summary>
    public double ScaleMin { get; set; } = TerrainPropDefaults.ScatterScaleMin;

    /// <summary>スケール上限。</summary>
    public double ScaleMax { get; set; } = TerrainPropDefaults.ScatterScaleMax;

    /// <summary>地表法線に沿わせるか（false なら常に真上を向く）。</summary>
    public bool AlignToNormal { get; set; } = TerrainPropDefaults.ScatterAlignToNormal;

    /// <summary>ランダム傾き上限（度）。</summary>
    public double TiltMaxDeg { get; set; } = TerrainPropDefaults.ScatterTiltMaxDeg;

    /// <summary>Y 軸まわりのランダム回転を掛けるか。</summary>
    public bool RandomYaw { get; set; } = TerrainPropDefaults.ScatterRandomYaw;

    /// <summary>親オブジェクトの "scatter" キーから編集状態を復元する。</summary>
    public ScatterParamsEdit(JsonObject parent, string key)
    {
        Existed = parent[key] is JsonObject;
        _source = TerrainPropJson.Child(parent, key);

        Density       = TerrainPropJson.ReadDouble(_source, KeyDensity,       TerrainPropDefaults.ScatterDensity);
        ScaleMin      = TerrainPropJson.ReadDouble(_source, KeyScaleMin,      TerrainPropDefaults.ScatterScaleMin);
        ScaleMax      = TerrainPropJson.ReadDouble(_source, KeyScaleMax,      TerrainPropDefaults.ScatterScaleMax);
        AlignToNormal = TerrainPropJson.ReadBool  (_source, KeyAlignToNormal, TerrainPropDefaults.ScatterAlignToNormal);
        TiltMaxDeg    = TerrainPropJson.ReadDouble(_source, KeyTiltMaxDeg,    TerrainPropDefaults.ScatterTiltMaxDeg);
        RandomYaw     = TerrainPropJson.ReadBool  (_source, KeyRandomYaw,     TerrainPropDefaults.ScatterRandomYaw);
    }

    /// <summary>編集値を親オブジェクトへ書き戻す。</summary>
    public void WriteBack(JsonObject parent, string key)
    {
        TerrainPropJson.SetOrOmit    (_source, KeyDensity,       Density,       TerrainPropDefaults.ScatterDensity);
        TerrainPropJson.SetOrOmit    (_source, KeyScaleMin,      ScaleMin,      TerrainPropDefaults.ScatterScaleMin);
        TerrainPropJson.SetOrOmit    (_source, KeyScaleMax,      ScaleMax,      TerrainPropDefaults.ScatterScaleMax);
        TerrainPropJson.SetBoolOrOmit(_source, KeyAlignToNormal, AlignToNormal, TerrainPropDefaults.ScatterAlignToNormal);
        TerrainPropJson.SetOrOmit    (_source, KeyTiltMaxDeg,    TiltMaxDeg,    TerrainPropDefaults.ScatterTiltMaxDeg);
        TerrainPropJson.SetBoolOrOmit(_source, KeyRandomYaw,     RandomYaw,     TerrainPropDefaults.ScatterRandomYaw);

        TerrainPropJson.AttachIfMeaningful(parent, key, _source, Existed);
    }
}

// ============================================================
//  LayerConditionEdit — レイヤ重みによる散布条件 1 件
// ============================================================

/// <summary>
/// 「指定レイヤの重みが一定以上の場所にだけ生える」条件 1 件。
/// 対応する Rust 型: props.rs の <c>LayerCondition</c>。
/// <para>
/// <c>layer</c> は Rust 側に serde default が無い必須フィールドなので、
/// 空文字でも必ず出力する（省略すると props.json 全体の読み込みが失敗する）。
/// </para>
/// </summary>
internal sealed class LayerConditionEdit
{
    private const string KeyLayer     = "layer";
    private const string KeyMinWeight = "min_weight";

    private readonly JsonObject _source;

    /// <summary>対象レイヤ名（layers.json の name と一致させる）。</summary>
    public string Layer { get; set; } = "";

    /// <summary>要求する最小重み（0..1）。</summary>
    public double MinWeight { get; set; } = TerrainPropDefaults.LayerConditionMinWeight;

    private LayerConditionEdit(JsonObject source)
    {
        _source   = source;
        Layer     = TerrainPropJson.ReadString(source, KeyLayer) ?? "";
        MinWeight = TerrainPropJson.ReadDouble(source, KeyMinWeight, TerrainPropDefaults.LayerConditionMinWeight);
    }

    /// <summary>既存 JSON オブジェクトから条件を作る（元ノードは複製して保持する）。</summary>
    public static LayerConditionEdit FromJson(JsonObject node)
        => new((JsonObject)node.DeepClone());

    /// <summary>新規条件を作る。</summary>
    /// <param name="layer">対象レイヤ名。</param>
    public static LayerConditionEdit CreateNew(string layer)
        => new(new JsonObject()) { Layer = layer };

    /// <summary>編集値を元 JSON オブジェクトへ書き戻し、そのオブジェクトを返す。</summary>
    public JsonObject WriteBack()
    {
        _source[KeyLayer] = JsonValue.Create(Layer);
        TerrainPropJson.SetOrOmit(_source, KeyMinWeight, MinWeight, TerrainPropDefaults.LayerConditionMinWeight);
        return _source;
    }
}

// ============================================================
//  ScatterRuleEdit — 斜度／高度／レイヤによる自動散布ルール
// ============================================================

/// <summary>
/// 1 プロップの「どこに自動で生えるか」を表すルール（props.json の "rule"）。
/// 対応する Rust 型: props.rs の <c>ScatterRule</c>。
/// </summary>
internal sealed class ScatterRuleEdit
{
    private const string KeySlopeMinDeg     = "slope_min_deg";
    private const string KeySlopeMaxDeg     = "slope_max_deg";
    private const string KeySlopeFadeDeg    = "slope_fade_deg";
    private const string KeyHeightMin       = "height_min";
    private const string KeyHeightMax       = "height_max";
    private const string KeyHeightFade      = "height_fade";
    private const string KeyLayerConditions = "layer_conditions";
    private const string KeyThreshold       = "threshold";

    private readonly JsonObject _source;

    /// <summary>読み込み時に親へ "rule" キーが存在したか。</summary>
    public bool Existed { get; }

    /// <summary>読み込み時に "layer_conditions" キーが存在したか（空配列化を保存へ反映するために追跡する）。</summary>
    private readonly bool _layerConditionsExisted;

    /// <summary>斜度ウィンドウ下限（度）。</summary>
    public double SlopeMinDeg { get; set; } = TerrainPropDefaults.RuleSlopeMinDeg;

    /// <summary>斜度ウィンドウ上限（度）。</summary>
    public double SlopeMaxDeg { get; set; } = TerrainPropDefaults.RuleSlopeMaxDeg;

    /// <summary>斜度ウィンドウ両端のぼかし幅（度）。</summary>
    public double SlopeFadeDeg { get; set; } = TerrainPropDefaults.RuleSlopeFadeDeg;

    /// <summary>高度ウィンドウ下限（ワールド Y・メートル）。</summary>
    public double HeightMin { get; set; } = TerrainPropDefaults.RuleHeightMin;

    /// <summary>高度ウィンドウ上限（ワールド Y・メートル）。</summary>
    public double HeightMax { get; set; } = TerrainPropDefaults.RuleHeightMax;

    /// <summary>高度ウィンドウ両端のぼかし幅（メートル）。</summary>
    public double HeightFade { get; set; } = TerrainPropDefaults.RuleHeightFade;

    /// <summary>最終確率の足切り閾値（これ未満なら生やさない）。</summary>
    public double Threshold { get; set; } = TerrainPropDefaults.RuleThreshold;

    /// <summary>地形レイヤ重み条件（空なら無条件）。複数指定時は AND 合成。</summary>
    public List<LayerConditionEdit> LayerConditions { get; } = new();

    /// <summary>親オブジェクトの "rule" キーから編集状態を復元する。</summary>
    public ScatterRuleEdit(JsonObject parent, string key)
    {
        Existed = parent[key] is JsonObject;
        _source = TerrainPropJson.Child(parent, key);

        SlopeMinDeg  = TerrainPropJson.ReadDouble(_source, KeySlopeMinDeg,  TerrainPropDefaults.RuleSlopeMinDeg);
        SlopeMaxDeg  = TerrainPropJson.ReadDouble(_source, KeySlopeMaxDeg,  TerrainPropDefaults.RuleSlopeMaxDeg);
        SlopeFadeDeg = TerrainPropJson.ReadDouble(_source, KeySlopeFadeDeg, TerrainPropDefaults.RuleSlopeFadeDeg);
        HeightMin    = TerrainPropJson.ReadDouble(_source, KeyHeightMin,    TerrainPropDefaults.RuleHeightMin);
        HeightMax    = TerrainPropJson.ReadDouble(_source, KeyHeightMax,    TerrainPropDefaults.RuleHeightMax);
        HeightFade   = TerrainPropJson.ReadDouble(_source, KeyHeightFade,   TerrainPropDefaults.RuleHeightFade);
        Threshold    = TerrainPropJson.ReadDouble(_source, KeyThreshold,    TerrainPropDefaults.RuleThreshold);

        _layerConditionsExisted = _source[KeyLayerConditions] is JsonArray;
        if (_source[KeyLayerConditions] is JsonArray arr)
        {
            foreach (var node in arr)
            {
                if (node is JsonObject o) LayerConditions.Add(LayerConditionEdit.FromJson(o));
            }
        }
    }

    /// <summary>編集値を親オブジェクトへ書き戻す。</summary>
    public void WriteBack(JsonObject parent, string key)
    {
        TerrainPropJson.SetOrOmit(_source, KeySlopeMinDeg,  SlopeMinDeg,  TerrainPropDefaults.RuleSlopeMinDeg);
        TerrainPropJson.SetOrOmit(_source, KeySlopeMaxDeg,  SlopeMaxDeg,  TerrainPropDefaults.RuleSlopeMaxDeg);
        TerrainPropJson.SetOrOmit(_source, KeySlopeFadeDeg, SlopeFadeDeg, TerrainPropDefaults.RuleSlopeFadeDeg);
        TerrainPropJson.SetOrOmit(_source, KeyHeightMin,    HeightMin,    TerrainPropDefaults.RuleHeightMin);
        TerrainPropJson.SetOrOmit(_source, KeyHeightMax,    HeightMax,    TerrainPropDefaults.RuleHeightMax);
        TerrainPropJson.SetOrOmit(_source, KeyHeightFade,   HeightFade,   TerrainPropDefaults.RuleHeightFade);
        TerrainPropJson.SetOrOmit(_source, KeyThreshold,    Threshold,    TerrainPropDefaults.RuleThreshold);

        // レイヤ条件は「元からあった」か「1 件以上ある」ときに配列を書く。
        // 元からあって全削除された場合も空配列を書く必要がある
        // （書かないと省略＝既定値扱いになるが、既定値も空配列なので結果は同じ。
        //   ただし元ファイルの形を保つ方が差分が読みやすいため明示的に書く）。
        if (_layerConditionsExisted || LayerConditions.Count > 0)
        {
            var arr = new JsonArray();
            foreach (var cond in LayerConditions)
                arr.Add(cond.WriteBack().DeepClone());
            _source[KeyLayerConditions] = arr;
        }

        TerrainPropJson.AttachIfMeaningful(parent, key, _source, Existed);
    }
}

// ============================================================
//  TerrainPropEdit — 1 プロップの編集状態
// ============================================================

/// <summary>
/// props.json の 1 プロップぶんの編集状態。
/// <para>
/// 元 JSON オブジェクト（親から切り離した複製）を保持し、
/// <see cref="WriteBack"/> で編集値だけを書き戻すため、
/// エディタが解釈しないフィールドが保存で失われない。
/// </para>
/// 対応する Rust 型: props.rs の <c>TerrainProp</c>。
/// </summary>
internal sealed class TerrainPropEdit
{
    // ── JSON キー名（マジック文字列を 1 箇所へ集約）────────────

    private const string KeyId        = "id";
    private const string KeyName      = "name";
    private const string KeyKind       = "kind";
    private const string KeyModelPath  = "model_path";
    private const string KeyPrefabPath = "prefab_path";
    private const string KeyGrass     = "grass";
    private const string KeyWind      = "wind";
    private const string KeyScatter   = "scatter";
    private const string KeyRule      = "rule";

    /// <summary>元 JSON オブジェクト（親から切り離した複製）。未知キーはここに残る。</summary>
    private readonly JsonObject _source;

    /// <summary>一意なプロップ ID（IPC・散布データの安定キー）。Rust 側に既定値が無いため常に出力する。</summary>
    public string Id { get; set; } = "";

    /// <summary>表示名（エディタの一覧に出す）。Rust 側に既定値が無いため常に出力する。</summary>
    public string Name { get; set; } = "";

    /// <summary>プロップ種別（"grass" / "model"）。JSON へは必ず小文字で書く。</summary>
    public string Kind { get; set; } = TerrainPropDefaults.Kind;

    /// <summary>モデルアセットのアセット相対パス（kind=model のときのみ意味を持つ。未設定は null）。</summary>
    public string? ModelPath { get; set; }

    /// <summary>プレハブ（.actor）のアセット相対パス（kind=actor のときのみ意味を持つ。未設定は null）。</summary>
    public string? PrefabPath { get; set; }

    /// <summary>草の形状パラメータ。</summary>
    public GrassParamsEdit Grass { get; }

    /// <summary>風による揺れのパラメータ。</summary>
    public WindParamsEdit Wind { get; }

    /// <summary>散布の密度・姿勢パラメータ。</summary>
    public ScatterParamsEdit Scatter { get; }

    /// <summary>自動散布ルール。</summary>
    public ScatterRuleEdit Rule { get; }

    /// <summary>種別が「草」かどうか（UI の条件付き表示に使う）。</summary>
    public bool IsGrass => string.Equals(Kind, TerrainPropDefaults.KindGrass, StringComparison.Ordinal);

    /// <summary>種別が「モデル」かどうか（UI の条件付き表示に使う）。</summary>
    public bool IsModel => string.Equals(Kind, TerrainPropDefaults.KindModel, StringComparison.Ordinal);

    /// <summary>種別が「アクタ（プレハブ）」かどうか（UI の条件付き表示に使う）。</summary>
    public bool IsActor => string.Equals(Kind, TerrainPropDefaults.KindActor, StringComparison.Ordinal);

    /// <summary>既存の JSON オブジェクトから編集状態を復元する。</summary>
    /// <param name="source">props[] の 1 要素（親から切り離された複製であること）。</param>
    private TerrainPropEdit(JsonObject source)
    {
        _source = source;

        Id        = TerrainPropJson.ReadString(source, KeyId)   ?? "";
        Name      = TerrainPropJson.ReadString(source, KeyName) ?? "";
        Kind       = NormalizeKind(TerrainPropJson.ReadString(source, KeyKind));
        ModelPath  = TerrainPropJson.ReadString(source, KeyModelPath);
        PrefabPath = TerrainPropJson.ReadString(source, KeyPrefabPath);

        Grass   = new GrassParamsEdit  (source, KeyGrass);
        Wind    = new WindParamsEdit   (source, KeyWind);
        Scatter = new ScatterParamsEdit(source, KeyScatter);
        Rule    = new ScatterRuleEdit  (source, KeyRule);
    }

    /// <summary>
    /// JSON から読んだ kind 文字列を既知の値へ正規化する。
    /// 未知の値（将来 Rust 側に追加された種別を含む）は既定の "grass" へ倒す
    /// ——ただし正規化された値をそのまま保存すると未知種別が失われるため、
    /// 元ファイルの値は <see cref="_source"/> に残っており、UI で変更しない限り
    /// <see cref="WriteBack"/> が上書きするのは既知の値だけである点に注意。
    /// </summary>
    private static string NormalizeKind(string? raw)
    {
        if (raw is null) return TerrainPropDefaults.Kind;
        foreach (var k in TerrainPropDefaults.Kinds)
        {
            if (string.Equals(raw, k, StringComparison.OrdinalIgnoreCase)) return k;
        }
        return TerrainPropDefaults.Kind;
    }

    /// <summary>既存 JSON オブジェクトから編集状態を作る（元ノードは複製して保持する）。</summary>
    public static TerrainPropEdit FromJson(JsonObject node)
        => new((JsonObject)node.DeepClone());

    /// <summary>新規プロップ（未知フィールド無し）を作る。</summary>
    /// <param name="id">プロップ ID。</param>
    /// <param name="name">表示名。</param>
    public static TerrainPropEdit CreateNew(string id, string name)
        => new(new JsonObject()) { Id = id, Name = name };

    /// <summary>このプロップの複製を作る（未知フィールドも含めて複製される）。</summary>
    /// <param name="id">複製後のプロップ ID。</param>
    /// <param name="name">複製後の表示名。</param>
    public TerrainPropEdit Duplicate(string id, string name)
    {
        // 一旦現在の編集値を JSON へ書き戻してから複製することで、
        // 「未保存の編集 ＋ 未知フィールド」の両方を引き継いだ複製になる。
        var snapshot = (JsonObject)WriteBack().DeepClone();
        var copy = FromJson(snapshot);
        copy.Id   = id;
        copy.Name = name;
        return copy;
    }

    /// <summary>
    /// 編集値を元 JSON オブジェクトへ書き戻し、そのオブジェクトを返す。
    /// エディタが解釈しないキーは触らないため保持される。
    /// </summary>
    public JsonObject WriteBack()
    {
        // id / name は Rust 側に serde default が無い必須フィールドなので常に出力する。
        _source[KeyId]   = JsonValue.Create(Id);
        _source[KeyName] = JsonValue.Create(Name);

        // kind は PropKind の serde 表現（小文字）で書く。既定（grass）かつ元から無ければ省略。
        TerrainPropJson.SetEnumOrOmit(_source, KeyKind, Kind, TerrainPropDefaults.Kind);
        // model_path / prefab_path は未設定なら "" ではなく JSON null（Rust は Option<String>）。
        TerrainPropJson.SetOptionalStringOrOmit(_source, KeyModelPath, ModelPath);
        TerrainPropJson.SetOptionalStringOrOmit(_source, KeyPrefabPath, PrefabPath);

        Grass  .WriteBack(_source, KeyGrass);
        Wind   .WriteBack(_source, KeyWind);
        Scatter.WriteBack(_source, KeyScatter);
        Rule   .WriteBack(_source, KeyRule);

        return _source;
    }
}

// ============================================================
//  TerrainPropsDocument — props.json 全体
// ============================================================

/// <summary>
/// props.json ドキュメント。ルート直下の未知キー（"_comment" 等）を保持したまま
/// "props" 配列だけを差し替える差分保存を行う。
/// </summary>
internal sealed class TerrainPropsDocument
{
    /// <summary>プロップ配列の JSON キー。</summary>
    private const string KeyProps = "props";

    /// <summary>ファイルが存在しない／壊れている場合に用意する初期プロップの ID。</summary>
    private const string FallbackPropId = "grass_field";

    /// <summary>ファイルが存在しない／壊れている場合に用意する初期プロップの表示名。</summary>
    private const string FallbackPropName = "草";

    /// <summary>assets ルートから見た props.json の相対パス。</summary>
    public const string RelativePath = @"terrain\props.json";

    /// <summary>ルート JSON オブジェクト（"props" 以外のキーはそのまま保持される）。</summary>
    private readonly JsonObject _root;

    /// <summary>編集中のプロップ一覧。並び順がそのまま props.json の並び（＝prop 添字）になる。</summary>
    public List<TerrainPropEdit> Props { get; } = new();

    /// <summary>読み込み時に既存ファイルが無かった／壊れていたか（UI での注意表示に使う）。</summary>
    public bool WasMissingOrInvalid { get; private init; }

    private TerrainPropsDocument(JsonObject root)
    {
        _root = root;
    }

    // ── 読み込み ──────────────────────────────────────────────

    /// <summary>
    /// assets ルート配下の props.json を読み込む。
    /// ファイルが無い／壊れている場合は、草プロップ 1 種だけの新規ドキュメントを返す
    /// （ランタイム側フォールバック定義をここへ複製すると二重管理になるため、あえて最小構成にする）。
    /// </summary>
    /// <param name="assetsRoot">assets ディレクトリの絶対パス。</param>
    public static TerrainPropsDocument Load(string assetsRoot)
    {
        var path = ResolvePath(assetsRoot);
        JsonObject? root = null;
        try
        {
            if (File.Exists(path))
                root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
        }
        catch (Exception)
        {
            // 壊れた JSON はフォールバックへ倒す（ここで例外を投げるとウィンドウが開けない）。
            root = null;
        }

        if (root is null)
        {
            var doc = new TerrainPropsDocument(new JsonObject()) { WasMissingOrInvalid = true };
            doc.Props.Add(TerrainPropEdit.CreateNew(FallbackPropId, FallbackPropName));
            return doc;
        }

        var result = new TerrainPropsDocument(root);
        if (root[KeyProps] is JsonArray arr)
        {
            foreach (var node in arr)
            {
                if (node is JsonObject o)
                    result.Props.Add(TerrainPropEdit.FromJson(o));
            }
        }
        // プロップが 1 つも無いファイルは編集不能なので、最低 1 種は用意する。
        if (result.Props.Count == 0)
            result.Props.Add(TerrainPropEdit.CreateNew(FallbackPropId, FallbackPropName));
        return result;
    }

    // ── 保存 ──────────────────────────────────────────────────

    /// <summary>
    /// 編集内容を props.json へ書き出す。
    /// ルート直下の未知キーと、各プロップの未知キーは保持される。
    /// </summary>
    /// <param name="assetsRoot">assets ディレクトリの絶対パス。</param>
    public void Save(string assetsRoot)
    {
        var path = ResolvePath(assetsRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, ToJsonString());
    }

    /// <summary>
    /// 現在の編集内容を JSON 文字列へ直列化する（保存と往復テストで共有する）。
    /// </summary>
    public string ToJsonString()
    {
        // 各プロップの編集値を元ノードへ書き戻し、新しい配列として組み直す。
        // JsonNode は 1 つの親にしか属せないため、書き戻し済みノードを複製して詰める。
        var arr = new JsonArray();
        foreach (var prop in Props)
            arr.Add(prop.WriteBack().DeepClone());
        _root[KeyProps] = arr;

        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            // 日本語のプロップ名や "_comment" が \uXXXX へ潰れないようにする。
            Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
        };
        return _root.ToJsonString(options);
    }

    /// <summary>assets ルートから props.json の絶対パスを求める。</summary>
    public static string ResolvePath(string assetsRoot)
        => Path.Combine(assetsRoot, RelativePath);
}
