// ============================================================
//  TerrainLayersDocument.cs — layers.json の読み書き（差分更新モデル）
//
//  【責務】
//    地形マテリアルレイヤ定義ファイル（assets/terrain/layers.json）を
//    エディタで編集するためのドキュメントモデル。
//
//  【なぜ POCO への deserialize ではなく JsonNode 差分更新なのか】
//    layers.json はランタイム（Rust）が正典であり、エディタが知らないフィールドが
//    今後増えうる（例: 現ファイル冒頭の "_comment"、将来のブラシ設定など）。
//    C# の型へ deserialize → serialize すると、型に無いフィールドは
//    黙って消える。これは「後方互換を壊さない」という要件に真っ向から反する。
//
//    そこで本クラスは元 JSON を JsonNode ツリーとして保持し、
//      - ルート直下の未知キー（"_comment" 等）はそのまま残す
//      - 各レイヤオブジェクトも元のノードを保持し、編集したキーだけを上書きする
//    という差分更新を行う。エディタが知らないレイヤ内フィールドも失われない。
//
//  【既定値と同じ値は書き出さない】
//    Rust 側は全フィールドが #[serde(default)] のため、省略＝既定値である。
//    「元ファイルに無く、かつ既定値のまま」のフィールドは書き出さないことで、
//    エディタで開いて保存しただけのファイルが肥大化しないようにする。
//    逆に「元ファイルにあった」フィールドは値が既定値でも書き続ける（保存性優先）。
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
//  TerrainLayerEdit — 1 レイヤの編集状態
// ============================================================

/// <summary>
/// layers.json の 1 レイヤぶんの編集状態。
/// <para>
/// <see cref="Source"/> に元の JSON オブジェクト（親から切り離した複製）を持ち、
/// <see cref="WriteBack"/> で編集値だけを書き戻す。これによりエディタが
/// 解釈しないフィールドが保存で失われない。
/// </para>
/// </summary>
internal sealed class TerrainLayerEdit
{
    // ── JSON キー名（マジック文字列を 1 箇所へ集約）────────────

    private const string KeyName             = "name";
    private const string KeyBaseColor        = "base_color";
    private const string KeyRoughness        = "roughness";
    private const string KeyMetallic         = "metallic";
    private const string KeyUvScale          = "uv_scale";
    private const string KeyBaseColorTexture = "base_color_texture";
    private const string KeyNormalTexture    = "normal_texture";
    private const string KeyRoughnessTexture = "roughness_texture";
    private const string KeyDetile           = "detile";
    private const string KeyDetileStrength   = "detile_strength";
    private const string KeyRule             = "rule";
    private const string KeySlopeMinDeg      = "slope_min_deg";
    private const string KeySlopeMaxDeg      = "slope_max_deg";
    private const string KeySlopeFadeDeg     = "slope_fade_deg";
    private const string KeyHeightMin        = "height_min";
    private const string KeyHeightMax        = "height_max";
    private const string KeyHeightFade       = "height_fade";
    private const string KeyPriority         = "priority";

    /// <summary>ベースカラーの成分数（RGB）。</summary>
    private const int ColorComponentCount = 3;

    /// <summary>
    /// 元 JSON オブジェクト（親から切り離した複製）。
    /// エディタが知らないキーはここに残り続け、保存時にそのまま出力される。
    /// </summary>
    private readonly JsonObject _source;

    // ── 編集対象フィールド ────────────────────────────────────

    /// <summary>レイヤ名（レイヤ選択 UI の表示名）。Rust 側に既定値が無いため常に出力する。</summary>
    public string Name { get; set; } = "";

    /// <summary>ベースカラー係数（リニア RGB・長さ 3）。</summary>
    public double[] BaseColor { get; set; } = (double[])TerrainLayerDefaults.BaseColor.Clone();

    /// <summary>ラフネス（0=鏡面, 1=完全拡散）。</summary>
    public double Roughness { get; set; } = TerrainLayerDefaults.Roughness;

    /// <summary>メタリック（0=非金属, 1=金属）。</summary>
    public double Metallic { get; set; } = TerrainLayerDefaults.Metallic;

    /// <summary>triplanar UV スケール。</summary>
    public double UvScale { get; set; } = TerrainLayerDefaults.UvScale;

    /// <summary>ベースカラーテクスチャのアセット相対パス（null = 単色レイヤ）。</summary>
    public string? BaseColorTexture { get; set; }

    /// <summary>法線マップのアセット相対パス（null = 法線マップ無し）。</summary>
    public string? NormalTexture { get; set; }

    /// <summary>ラフネスマップのアセット相対パス（null = スカラ Roughness を使う）。</summary>
    public string? RoughnessTexture { get; set; }

    /// <summary>タイリング解消モード（"none" / "stochastic" / "macro"）。</summary>
    public string Detile { get; set; } = TerrainLayerDefaults.Detile;

    /// <summary>タイリング解消の強度（0..1）。</summary>
    public double DetileStrength { get; set; } = TerrainLayerDefaults.DetileStrength;

    /// <summary>斜度ウィンドウ下限（度）。</summary>
    public double SlopeMinDeg { get; set; } = TerrainLayerDefaults.SlopeMinDeg;

    /// <summary>斜度ウィンドウ上限（度）。</summary>
    public double SlopeMaxDeg { get; set; } = TerrainLayerDefaults.SlopeMaxDeg;

    /// <summary>斜度ウィンドウのフェード幅（度）。</summary>
    public double SlopeFadeDeg { get; set; } = TerrainLayerDefaults.SlopeFadeDeg;

    /// <summary>高度ウィンドウ下限（ワールド Y・メートル）。</summary>
    public double HeightMin { get; set; } = TerrainLayerDefaults.HeightMin;

    /// <summary>高度ウィンドウ上限（ワールド Y・メートル）。</summary>
    public double HeightMax { get; set; } = TerrainLayerDefaults.HeightMax;

    /// <summary>高度ウィンドウのフェード幅（メートル）。</summary>
    public double HeightFade { get; set; } = TerrainLayerDefaults.HeightFade;

    /// <summary>ルールの基礎重み（優先度）。</summary>
    public double Priority { get; set; } = TerrainLayerDefaults.Priority;

    // ── 生成 ──────────────────────────────────────────────────

    /// <summary>
    /// 既存の JSON オブジェクトから編集状態を復元する。
    /// </summary>
    /// <param name="source">layers[] の 1 要素（親から切り離された複製であること）。</param>
    private TerrainLayerEdit(JsonObject source)
    {
        _source = source;

        // ─── レイヤ本体 ───
        Name             = ReadString(source, KeyName) ?? "";
        BaseColor        = ReadColor(source, KeyBaseColor);
        Roughness        = ReadDouble(source, KeyRoughness,      TerrainLayerDefaults.Roughness);
        Metallic         = ReadDouble(source, KeyMetallic,       TerrainLayerDefaults.Metallic);
        UvScale          = ReadDouble(source, KeyUvScale,        TerrainLayerDefaults.UvScale);
        BaseColorTexture = ReadPath(source, KeyBaseColorTexture);
        NormalTexture    = ReadPath(source, KeyNormalTexture);
        RoughnessTexture = ReadPath(source, KeyRoughnessTexture);
        Detile           = ReadString(source, KeyDetile) ?? TerrainLayerDefaults.Detile;
        DetileStrength   = ReadDouble(source, KeyDetileStrength, TerrainLayerDefaults.DetileStrength);

        // ─── ルール（rule サブオブジェクト。存在しなければ全て既定値）───
        if (source[KeyRule] is JsonObject rule)
        {
            SlopeMinDeg  = ReadDouble(rule, KeySlopeMinDeg,  TerrainLayerDefaults.SlopeMinDeg);
            SlopeMaxDeg  = ReadDouble(rule, KeySlopeMaxDeg,  TerrainLayerDefaults.SlopeMaxDeg);
            SlopeFadeDeg = ReadDouble(rule, KeySlopeFadeDeg, TerrainLayerDefaults.SlopeFadeDeg);
            HeightMin    = ReadDouble(rule, KeyHeightMin,    TerrainLayerDefaults.HeightMin);
            HeightMax    = ReadDouble(rule, KeyHeightMax,    TerrainLayerDefaults.HeightMax);
            HeightFade   = ReadDouble(rule, KeyHeightFade,   TerrainLayerDefaults.HeightFade);
            Priority     = ReadDouble(rule, KeyPriority,     TerrainLayerDefaults.Priority);
        }
    }

    /// <summary>
    /// 既存 JSON オブジェクトから編集状態を作る。
    /// 元ノードは複製して保持するため、呼び出し側のツリーへ影響しない。
    /// </summary>
    public static TerrainLayerEdit FromJson(JsonObject node)
        => new((JsonObject)node.DeepClone());

    /// <summary>
    /// 新規レイヤ（未知フィールド無し）を作る。
    /// </summary>
    /// <remarks>
    /// 優先度だけは既定値（1.0）ではなく <see cref="TerrainLayerDefaults.NewLayerPriority"/>（0）で作る。
    /// 既定ルールは地形全域で成立するため、優先度 1 のまま追加すると
    /// 「追加した瞬間に地形全体の自動ペイントへ無地グレーが混ざる」からである
    /// （詳細な理由は <see cref="TerrainLayerDefaults.NewLayerPriority"/> のコメント）。
    /// 0 は既定値と異なるので <see cref="WriteBack"/> が rule.priority を必ず出力する。
    /// </remarks>
    /// <param name="name">レイヤ名。</param>
    public static TerrainLayerEdit CreateNew(string name)
        => new(new JsonObject())
        {
            Name = name,
            Priority = TerrainLayerDefaults.NewLayerPriority,
        };

    /// <summary>
    /// このレイヤの複製を作る（未知フィールドも含めて複製される）。
    /// </summary>
    /// <param name="name">複製後のレイヤ名。</param>
    public TerrainLayerEdit Duplicate(string name)
    {
        // 一旦現在の編集値を JSON へ書き戻してから複製することで、
        // 「未保存の編集 ＋ 未知フィールド」の両方を引き継いだ複製になる。
        var snapshot = (JsonObject)WriteBack().DeepClone();
        var copy = FromJson(snapshot);
        copy.Name = name;
        return copy;
    }

    // ── 書き戻し ──────────────────────────────────────────────

    /// <summary>
    /// 編集値を元 JSON オブジェクトへ書き戻し、そのオブジェクトを返す。
    /// エディタが解釈しないキーは触らないため保持される。
    /// </summary>
    public JsonObject WriteBack()
    {
        // name は Rust 側に serde default が無い必須フィールドなので常に出力する。
        _source[KeyName] = JsonValue.Create(Name);

        SetColorOrOmit(_source, KeyBaseColor, BaseColor, TerrainLayerDefaults.BaseColor);
        SetOrOmit(_source, KeyRoughness,      Roughness,      TerrainLayerDefaults.Roughness);
        SetOrOmit(_source, KeyMetallic,       Metallic,       TerrainLayerDefaults.Metallic);
        SetOrOmit(_source, KeyUvScale,        UvScale,        TerrainLayerDefaults.UvScale);
        SetStringOrOmit(_source, KeyBaseColorTexture, BaseColorTexture);
        SetStringOrOmit(_source, KeyNormalTexture,    NormalTexture);
        SetStringOrOmit(_source, KeyRoughnessTexture, RoughnessTexture);
        SetStringValueOrOmit(_source, KeyDetile, Detile, TerrainLayerDefaults.Detile);
        SetOrOmit(_source, KeyDetileStrength, DetileStrength, TerrainLayerDefaults.DetileStrength);

        // ─── ルール ───
        //   既存の rule オブジェクトがあればそれを使い（未知キー保持）、
        //   無ければ一時オブジェクトへ書いてから「中身があるときだけ」ぶら下げる。
        bool ruleExisted = _source[KeyRule] is JsonObject;
        var rule = _source[KeyRule] as JsonObject ?? new JsonObject();

        SetOrOmit(rule, KeySlopeMinDeg,  SlopeMinDeg,  TerrainLayerDefaults.SlopeMinDeg);
        SetOrOmit(rule, KeySlopeMaxDeg,  SlopeMaxDeg,  TerrainLayerDefaults.SlopeMaxDeg);
        SetOrOmit(rule, KeySlopeFadeDeg, SlopeFadeDeg, TerrainLayerDefaults.SlopeFadeDeg);
        SetOrOmit(rule, KeyHeightMin,    HeightMin,    TerrainLayerDefaults.HeightMin);
        SetOrOmit(rule, KeyHeightMax,    HeightMax,    TerrainLayerDefaults.HeightMax);
        SetOrOmit(rule, KeyHeightFade,   HeightFade,   TerrainLayerDefaults.HeightFade);
        SetOrOmit(rule, KeyPriority,     Priority,     TerrainLayerDefaults.Priority);

        // 元々 rule が無く、かつ全フィールドが既定値のままなら rule 自体を書かない。
        if (!ruleExisted && rule.Count > 0)
            _source[KeyRule] = rule;

        return _source;
    }

    // ── JSON 読み出しヘルパ ───────────────────────────────────

    /// <summary>数値キーを読む。存在しない／数値でない場合は既定値を返す。</summary>
    private static double ReadDouble(JsonObject obj, string key, double fallback)
        => obj[key] is JsonValue v && v.TryGetValue<double>(out var d) ? d : fallback;

    /// <summary>文字列キーを読む。存在しない／null の場合は null を返す。</summary>
    private static string? ReadString(JsonObject obj, string key)
        => obj[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    /// <summary>
    /// 任意（未設定可）のパス文字列キーを読む。
    /// キーなし・null・空文字列（空白のみを含む）はすべて「未設定」として null へ正規化する。
    /// これにより、過去に空文字列で保存された layers.json も UI 上「未設定」として扱われる。
    /// </summary>
    private static string? ReadPath(JsonObject obj, string key)
    {
        var raw = ReadString(obj, key);
        return string.IsNullOrWhiteSpace(raw) ? null : raw;
    }

    /// <summary>
    /// RGB 配列キーを読む。要素数が足りない／数値でない成分は既定値で埋める。
    /// </summary>
    private static double[] ReadColor(JsonObject obj, string key)
    {
        var result = (double[])TerrainLayerDefaults.BaseColor.Clone();
        if (obj[key] is not JsonArray arr) return result;
        for (int i = 0; i < ColorComponentCount && i < arr.Count; i++)
        {
            if (arr[i] is JsonValue v && v.TryGetValue<double>(out var d))
                result[i] = d;
        }
        return result;
    }

    // ── JSON 書き込みヘルパ（既定値なら省略）──────────────────

    /// <summary>
    /// 数値を書き込む。元から存在したキー、または既定値と異なる値のときだけ出力する。
    /// </summary>
    private static void SetOrOmit(JsonObject obj, string key, double value, double defaultValue)
    {
        if (obj.ContainsKey(key) || Math.Abs(value - defaultValue) > TerrainLayerDefaults.Epsilon)
            obj[key] = JsonValue.Create(value);
    }

    /// <summary>
    /// 文字列列挙値（detile 等）を書き込む。既定値かつ元から無ければ省略する。
    /// </summary>
    private static void SetStringValueOrOmit(JsonObject obj, string key, string value, string defaultValue)
    {
        if (obj.ContainsKey(key) || !string.Equals(value, defaultValue, StringComparison.Ordinal))
            obj[key] = JsonValue.Create(value);
    }

    /// <summary>
    /// 任意（null 可）のパス文字列を書き込む。
    /// null かつ元から無ければ省略、null だが元にあった場合は明示的な null を維持する
    /// （元ファイルの "base_color_texture": null という書き方を壊さないため）。
    /// </summary>
    private static void SetStringOrOmit(JsonObject obj, string key, string? value)
    {
        if (!string.IsNullOrEmpty(value))
            obj[key] = JsonValue.Create(value);
        else if (obj.ContainsKey(key))
            obj[key] = null;
    }

    /// <summary>
    /// RGB 配列を書き込む。全成分が既定値と一致し、かつ元から無ければ省略する。
    /// </summary>
    private static void SetColorOrOmit(JsonObject obj, string key, double[] value, double[] defaultValue)
    {
        bool differs = false;
        for (int i = 0; i < ColorComponentCount; i++)
        {
            if (Math.Abs(value[i] - defaultValue[i]) > TerrainLayerDefaults.Epsilon) { differs = true; break; }
        }
        if (!obj.ContainsKey(key) && !differs) return;

        var arr = new JsonArray();
        for (int i = 0; i < ColorComponentCount; i++) arr.Add(JsonValue.Create(value[i]));
        obj[key] = arr;
    }
}

// ============================================================
//  TerrainLayersDocument — layers.json 全体
// ============================================================

/// <summary>
/// layers.json ドキュメント。ルート直下の未知キー（"_comment" 等）を保持したまま
/// "layers" 配列だけを差し替える差分保存を行う。
/// </summary>
internal sealed class TerrainLayersDocument
{
    /// <summary>レイヤ配列の JSON キー。</summary>
    private const string KeyLayers = "layers";

    /// <summary>ファイルが存在しない／壊れている場合に用意する初期レイヤ名。</summary>
    private const string FallbackLayerName = "layer0";

    /// <summary>assets ルートから見た layers.json の相対パス。</summary>
    public const string RelativePath = @"terrain\layers.json";

    /// <summary>ルート JSON オブジェクト（"layers" 以外のキーはそのまま保持される）。</summary>
    private readonly JsonObject _root;

    /// <summary>編集中のレイヤ一覧。並び順がそのまま layers.json の並び（＝レイヤ番号）になる。</summary>
    public List<TerrainLayerEdit> Layers { get; } = new();

    /// <summary>読み込み時に既存ファイルが無かった／壊れていたか（UI での注意表示に使う）。</summary>
    public bool WasMissingOrInvalid { get; private init; }

    private TerrainLayersDocument(JsonObject root)
    {
        _root = root;
    }

    // ── 読み込み ──────────────────────────────────────────────

    /// <summary>
    /// assets ルート配下の layers.json を読み込む。
    /// ファイルが無い／壊れている場合は、単色レイヤ 1 枚だけの新規ドキュメントを返す
    /// （ランタイム側フォールバック定義をここへ複製すると二重管理になるため、あえて最小構成にする）。
    /// </summary>
    /// <param name="assetsRoot">assets ディレクトリの絶対パス。</param>
    public static TerrainLayersDocument Load(string assetsRoot)
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
            var doc = new TerrainLayersDocument(new JsonObject()) { WasMissingOrInvalid = true };
            doc.Layers.Add(TerrainLayerEdit.CreateNew(FallbackLayerName));
            return doc;
        }

        var result = new TerrainLayersDocument(root);
        if (root[KeyLayers] is JsonArray arr)
        {
            foreach (var node in arr)
            {
                if (node is JsonObject o)
                    result.Layers.Add(TerrainLayerEdit.FromJson(o));
            }
        }
        // レイヤが 1 枚も無いファイルは編集不能なので、最低 1 枚は用意する。
        if (result.Layers.Count == 0)
            result.Layers.Add(TerrainLayerEdit.CreateNew(FallbackLayerName));
        return result;
    }

    // ── 保存 ──────────────────────────────────────────────────

    /// <summary>
    /// 編集内容を layers.json へ書き出す。
    /// ルート直下の未知キーと、各レイヤの未知キーは保持される。
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
        // 各レイヤの編集値を元ノードへ書き戻し、新しい配列として組み直す。
        // JsonNode は 1 つの親にしか属せないため、書き戻し済みノードを複製して詰める。
        var arr = new JsonArray();
        foreach (var layer in Layers)
            arr.Add(layer.WriteBack().DeepClone());
        _root[KeyLayers] = arr;

        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            // 日本語のレイヤ名や "_comment" が \uXXXX へ潰れないようにする。
            Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
        };
        return _root.ToJsonString(options);
    }

    /// <summary>assets ルートから layers.json の絶対パスを求める。</summary>
    public static string ResolvePath(string assetsRoot)
        => Path.Combine(assetsRoot, RelativePath);
}
