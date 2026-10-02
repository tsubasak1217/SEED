// ============================================================
//  ThumbnailSample.cs — カタログのエントリの thumbnail_sample 欄（見本の撮り方のデータ）
//
//  【役割】
//  テンプレートごとの「どう見せて撮るか」をデータで持つ。ツールのコードはテンプレートの名前を知らない
//  （ボタンの文字・一覧の 3 行・ホイールの値・グラフの点・開くダイアログの文言は、すべてこの欄に書く）。
//  エディタのカタログの読み込み（TemplateActorCatalog）は知らない欄を読み飛ばすので、この欄を足しても一覧は変わらない。
//
//  【書き方】（docs/template_library.md §9.10 の表と同じ。すべて省略できる）
//    "thumbnail_sample": {
//      "skip": "理由",                    … 撮らない（空でなければ。一覧に理由を出す）
//      "stage_kind": "ui",                … 舞台の種類 ui / model / root_canvas（既定: 2D なら ui、3D なら model）
//      "stand_in": "prefabs/x.actor",     … 代わりに置くテンプレート（カタログのフォルダ基準）。自分だけでは見えない面を
//                                           受け皿に開かせるときに使う（例 ダイアログの面 → ModalHost を置いて script で開く）
//      "placement": "center",             … ui: center（既定。枠に入れて中央へ）/ fill（舞台いっぱい。受け皿・画面）
//      "stage": [360, 360],               … ui の舞台の大きさ（dp）
//      "size": [120, 48],                 … center の枠の大きさ（dp。既定はテンプレートの根の Sprite か Canvas の大きさ）
//      "frame": 200,                      … 切り出す正方形の一辺（dp。既定は舞台の短い辺）
//      "focus": [0.5, 0.5],               … 切り出しの中心（舞台に対する割合）
//      "fit": "crop",                     … crop（正方形を切り出す。ui の既定）/ contain（全体を正方形に収める。他の既定）
//      "settle": 0.8,                     … 見本の操作ができてから撮るまでの秒
//      "patch": { "<ノードのパス>": { "<コンポーネント名>": { …data へ深くマージ… } } },
//                                         … ノードのパスは根 = ""、子は "Label"・"Pages/Tab0"（name を '/' でつなぐ）
//      "instances": [ {patch}, {patch} ],  … 同じテンプレートを並べる（それぞれに patch を重ねる。center のとき）
//      "spacing": 12,                     … 並べる間隔（dp）
//      "script": { "Action": "dialog", "Title": "…" },  … 舞台のスクリプト ThumbnailStage.cs の欄
//      "camera": { "position": [0,1,3.6], "rotation": [0,0,0], "fov": 35 },  … model の舞台のカメラ
//      "light": { "rotation": [50,-30,0], "intensity": 2.4 },              … model の舞台の平行光
//      "window": [960, 540]               … root_canvas の舞台の窓（画素）
//    }
// ============================================================

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SEEDEditor.Tools.SeedTemplateThumbnails.Stage;

/// <summary>舞台の種類。</summary>
public enum StageKind
{
    /// <summary>dp のキャンバスに UI の部品を置く（2D のテンプレートの既定）。</summary>
    Ui,
    /// <summary>カメラと光の前に 3D のテンプレートを置く（3D のテンプレートの既定）。</summary>
    Model,
    /// <summary>ルートのキャンバスを持つ 2D のテンプレートを、そのままルートとして置く（画素のキャンバスの見本）。</summary>
    RootCanvas,
}

/// <summary>UI の舞台での置き方。</summary>
public enum StagePlacement
{
    /// <summary>大きさの決まった枠に入れて、舞台の中央に並べる。</summary>
    Center,
    /// <summary>舞台の根の直下に置く（親に合わせて広がる受け皿・画面）。</summary>
    Fill,
}

/// <summary>撮った画像の正方形への収め方。</summary>
public enum ThumbnailFit
{
    /// <summary>正方形を切り出す（frame・focus）。</summary>
    Crop,
    /// <summary>全体を正方形に収め、余りを背景の色で埋める。</summary>
    Contain,
}

/// <summary>
/// 1 件のテンプレートの見本の撮り方（カタログの thumbnail_sample を解釈した値）。
/// </summary>
public sealed class ThumbnailSample
{
    // ============================================================
    //  欄名
    // ============================================================

    /// <summary>カタログのエントリの欄名。</summary>
    public const string Key = "thumbnail_sample";

    private const string SkipKey = "skip";
    private const string StageKindKey = "stage_kind";
    private const string StandInKey = "stand_in";
    private const string PlacementKey = "placement";
    private const string StageKey = "stage";
    private const string SizeKey = "size";
    private const string FrameKey = "frame";
    private const string FocusKey = "focus";
    private const string FitKey = "fit";
    private const string SettleKey = "settle";
    private const string PatchKey = "patch";
    private const string InstancesKey = "instances";
    private const string SpacingKey = "spacing";
    private const string ScriptKey = "script";
    private const string CameraKey = "camera";
    private const string LightKey = "light";
    private const string WindowKey = "window";
    private const string PositionKey = "position";
    private const string RotationKey = "rotation";
    private const string FovKey = "fov";
    private const string IntensityKey = "intensity";

    /// <summary>2 つの数の組（大きさ・割合）の要素数。</summary>
    private const int PairLength = 2;

    /// <summary>3 つの数の組（位置・向き）の要素数。</summary>
    private const int TripleLength = 3;

    /// <summary>割合の下限・上限（focus）。</summary>
    private const double FractionMin = 0, FractionMax = 1;

    /// <summary>真ん中の割合（focus の既定）。</summary>
    private const double CenterFraction = 0.5;

    /// <summary>舞台の値の名前の表（データの文字列 → 列挙）。</summary>
    private static readonly Dictionary<string, StageKind> KindNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ui"] = StageKind.Ui, ["model"] = StageKind.Model, ["root_canvas"] = StageKind.RootCanvas,
    };

    /// <summary>置き方の名前の表。</summary>
    private static readonly Dictionary<string, StagePlacement> PlacementNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["center"] = StagePlacement.Center, ["fill"] = StagePlacement.Fill,
    };

    /// <summary>収め方の名前の表。</summary>
    private static readonly Dictionary<string, ThumbnailFit> FitNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["crop"] = ThumbnailFit.Crop, ["contain"] = ThumbnailFit.Contain,
    };

    // ============================================================
    //  値
    // ============================================================

    /// <summary>撮らない理由（空なら撮る）。</summary>
    public string SkipReason { get; private set; } = "";

    /// <summary>舞台の種類（null ならテンプレートの 2D / 3D から決める）。</summary>
    public StageKind? Kind { get; private set; }

    /// <summary>代わりに置くテンプレート（カタログのフォルダ基準の相対パス。空なら自分を置く）。</summary>
    public string StandIn { get; private set; } = "";

    /// <summary>UI の舞台での置き方。</summary>
    public StagePlacement Placement { get; private set; } = StagePlacement.Center;

    /// <summary>UI の舞台の幅（dp）。</summary>
    public double StageWidthDp { get; private set; } = ThumbnailStageDefaults.StageWidthDp;

    /// <summary>UI の舞台の高さ（dp）。</summary>
    public double StageHeightDp { get; private set; } = ThumbnailStageDefaults.StageHeightDp;

    /// <summary>中央に置く枠の幅（dp。null ならテンプレートから決める）。</summary>
    public double? HolderWidthDp { get; private set; }

    /// <summary>中央に置く枠の高さ（dp。null ならテンプレートから決める）。</summary>
    public double? HolderHeightDp { get; private set; }

    /// <summary>切り出す正方形の一辺（dp。null なら舞台の短い辺）。</summary>
    public double? FrameDp { get; private set; }

    /// <summary>切り出しの中心の横の割合（0..1）。</summary>
    public double FocusX { get; private set; } = CenterFraction;

    /// <summary>切り出しの中心の縦の割合（0..1）。</summary>
    public double FocusY { get; private set; } = CenterFraction;

    /// <summary>正方形への収め方（null なら舞台の種類から決める）。</summary>
    public ThumbnailFit? Fit { get; private set; }

    /// <summary>見本の操作ができてから撮るまでの秒。</summary>
    public double SettleSeconds { get; private set; } = ThumbnailStageDefaults.SettleSeconds;

    /// <summary>全部の置き場に重ねる上書き（ノードのパス → コンポーネント名 → data へ深くマージ）。</summary>
    public JsonObject Patch { get; private set; } = new();

    /// <summary>並べる置き場ごとの上書き（空なら 1 つだけ置く）。</summary>
    public IReadOnlyList<JsonObject> Instances { get; private set; } = [];

    /// <summary>並べる間隔（dp）。</summary>
    public double SpacingDp { get; private set; } = ThumbnailStageDefaults.InstanceSpacingDp;

    /// <summary>舞台のスクリプト（ThumbnailStage.cs）の欄（値は文字列にそろえる）。</summary>
    public IReadOnlyDictionary<string, string> ScriptFields { get; private set; } = new Dictionary<string, string>();

    /// <summary>3D の舞台のカメラの位置（m）。</summary>
    public double[] CameraPosition { get; private set; } = ThumbnailStageDefaults.CameraPosition;

    /// <summary>3D の舞台のカメラの向き（度）。</summary>
    public double[] CameraRotation { get; private set; } = ThumbnailStageDefaults.CameraRotation;

    /// <summary>3D の舞台のカメラの縦の画角（度）。</summary>
    public double CameraFovDeg { get; private set; } = ThumbnailStageDefaults.CameraFovDeg;

    /// <summary>3D の舞台の平行光の向き（度）。</summary>
    public double[] LightRotation { get; private set; } = ThumbnailStageDefaults.LightRotation;

    /// <summary>3D の舞台の平行光の強さ。</summary>
    public double LightIntensity { get; private set; } = ThumbnailStageDefaults.LightIntensity;

    /// <summary>ルートのキャンバスの舞台の窓の幅（画素）。</summary>
    public int WindowWidthPx { get; private set; } = ThumbnailStageDefaults.RootCanvasWindowWidthPx;

    /// <summary>ルートのキャンバスの舞台の窓の高さ（画素）。</summary>
    public int WindowHeightPx { get; private set; } = ThumbnailStageDefaults.RootCanvasWindowHeightPx;

    // ============================================================
    //  解釈
    // ============================================================

    /// <summary>
    /// thumbnail_sample の欄を解釈する。欄が無ければ既定の撮り方。
    /// </summary>
    /// <param name="node">欄の値（無ければ null）。</param>
    /// <param name="errors">書き損じの積み先（1 つでもあればその件は撮らない）。</param>
    /// <returns>撮り方。</returns>
    public static ThumbnailSample Parse(JsonNode? node, List<string> errors)
    {
        var sample = new ThumbnailSample();
        if (node is null) return sample;
        if (node is not JsonObject obj)
        {
            errors.Add($"{Key} がオブジェクトではありません");
            return sample;
        }

        sample.SkipReason = GetString(obj, SkipKey);
        sample.StandIn = GetString(obj, StandInKey);
        sample.SettleSeconds = GetNumber(obj, SettleKey, errors) ?? sample.SettleSeconds;
        sample.SpacingDp = GetNumber(obj, SpacingKey, errors) ?? sample.SpacingDp;
        sample.FrameDp = GetNumber(obj, FrameKey, errors);

        // ── 名前で選ぶ値 ──
        if (GetString(obj, StageKindKey) is { Length: > 0 } kind)
        {
            if (KindNames.TryGetValue(kind, out var k)) sample.Kind = k;
            else errors.Add($"{StageKindKey} が分かりません: {kind}（ui / model / root_canvas）");
        }
        if (GetString(obj, PlacementKey) is { Length: > 0 } placement)
        {
            if (PlacementNames.TryGetValue(placement, out var p)) sample.Placement = p;
            else errors.Add($"{PlacementKey} が分かりません: {placement}（center / fill）");
        }
        if (GetString(obj, FitKey) is { Length: > 0 } fit)
        {
            if (FitNames.TryGetValue(fit, out var f)) sample.Fit = f;
            else errors.Add($"{FitKey} が分かりません: {fit}（crop / contain）");
        }

        // ── 数の組 ──
        if (GetNumbers(obj, StageKey, PairLength, errors) is { } stage)
        {
            sample.StageWidthDp = stage[0];
            sample.StageHeightDp = stage[1];
        }
        if (GetNumbers(obj, SizeKey, PairLength, errors) is { } size)
        {
            sample.HolderWidthDp = size[0];
            sample.HolderHeightDp = size[1];
        }
        if (GetNumbers(obj, FocusKey, PairLength, errors) is { } focus)
        {
            sample.FocusX = Math.Clamp(focus[0], FractionMin, FractionMax);
            sample.FocusY = Math.Clamp(focus[1], FractionMin, FractionMax);
        }
        if (GetNumbers(obj, WindowKey, PairLength, errors) is { } window)
        {
            sample.WindowWidthPx = (int)Math.Round(window[0]);
            sample.WindowHeightPx = (int)Math.Round(window[1]);
        }

        // ── 上書き（patch・instances）──
        if (obj[PatchKey] is { } patch)
        {
            if (patch is JsonObject patchObj) sample.Patch = (JsonObject)patchObj.DeepClone();
            else errors.Add($"{PatchKey} がオブジェクトではありません");
        }
        if (obj[InstancesKey] is { } instances)
        {
            if (instances is JsonArray array && array.All(i => i is JsonObject))
                sample.Instances = array.Select(i => (JsonObject)i!.DeepClone()).ToList();
            else errors.Add($"{InstancesKey} がオブジェクトの配列ではありません");
        }

        // ── 舞台のスクリプトの欄（ScriptComponent の欄は文字列で持つ）──
        if (obj[ScriptKey] is { } script)
        {
            if (script is JsonObject scriptObj)
            {
                var fields = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var (name, value) in scriptObj) fields[name] = ToFieldText(value);
                sample.ScriptFields = fields;
            }
            else errors.Add($"{ScriptKey} がオブジェクトではありません");
        }

        // ── 3D の舞台 ──
        if (obj[CameraKey] is JsonObject camera)
        {
            sample.CameraPosition = GetNumbers(camera, PositionKey, TripleLength, errors) ?? sample.CameraPosition;
            sample.CameraRotation = GetNumbers(camera, RotationKey, TripleLength, errors) ?? sample.CameraRotation;
            sample.CameraFovDeg = GetNumber(camera, FovKey, errors) ?? sample.CameraFovDeg;
        }
        if (obj[LightKey] is JsonObject light)
        {
            sample.LightRotation = GetNumbers(light, RotationKey, TripleLength, errors) ?? sample.LightRotation;
            sample.LightIntensity = GetNumber(light, IntensityKey, errors) ?? sample.LightIntensity;
        }
        return sample;
    }

    // ============================================================
    //  読み取りの小道具
    // ============================================================

    /// <summary>文字列の欄（無い・文字列でなければ空）。</summary>
    private static string GetString(JsonObject obj, string key) =>
        obj[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s.Trim() : "";

    /// <summary>数の欄（無ければ null。数でなければ誤り）。</summary>
    private static double? GetNumber(JsonObject obj, string key, List<string> errors)
    {
        if (obj[key] is not { } node) return null;
        if (node is JsonValue v && v.GetValueKind() == JsonValueKind.Number) return v.GetValue<double>();
        errors.Add($"{key} が数ではありません: {node.ToJsonString()}");
        return null;
    }

    /// <summary>決まった数の要素の数の配列の欄（無ければ null。形が違えば誤り）。</summary>
    private static double[]? GetNumbers(JsonObject obj, string key, int length, List<string> errors)
    {
        if (obj[key] is not { } node) return null;
        if (node is JsonArray array && array.Count == length
            && array.All(e => e is JsonValue v && v.GetValueKind() == JsonValueKind.Number))
            return array.Select(e => e!.GetValue<double>()).ToArray();
        errors.Add($"{key} は {length} つの数の配列で書いてください: {node.ToJsonString()}");
        return null;
    }

    /// <summary>スクリプトの欄の値を文字列にそろえる（ScriptComponent の fields は文字列で保存される）。</summary>
    private static string ToFieldText(JsonNode? value) => value switch
    {
        null => "",
        JsonValue v when v.GetValueKind() == JsonValueKind.String => v.GetValue<string>(),
        JsonValue v when v.GetValueKind() == JsonValueKind.Number =>
            v.GetValue<double>().ToString("R", CultureInfo.InvariantCulture),
        JsonValue v when v.GetValueKind() == JsonValueKind.True => "true",
        JsonValue v when v.GetValueKind() == JsonValueKind.False => "false",
        _ => value.ToJsonString(),
    };
}
