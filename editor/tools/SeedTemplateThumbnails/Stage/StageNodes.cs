// ============================================================
//  StageNodes.cs — 舞台のシーンに置くノード・コンポーネントの JSON の組み立て
//
//  【役割】
//  シーン（.scene）のアクタの JSON（ランタイムの ActorData の直列化と同じ形）を、欄名を 1 か所に集めて作る。
//  形の出どころは templates/ui/scenes/ui_text_input.scene・ui_navigation.scene（dp のルート・背景・縦の Stack）と
//  templates/scenes/cameraModeTest.scene（主カメラと平行光）。ランタイムの形が変わったらここだけ直す。
// ============================================================

using System.Text.Json.Nodes;

namespace SEEDEditor.Tools.SeedTemplateThumbnails.Stage;

/// <summary>舞台のノード・コンポーネントの JSON を作る。状態を持たない。</summary>
public static class StageNodes
{
    // ── 欄名・値（ランタイムの直列化名）─────────────────────

    private const string NameKey = "name";
    private const string ActorKindKey = "actor_kind";
    private const string ActorKind2D = "Actor2D";
    private const string ComponentsKey = "components";
    private const string ChildrenKey = "children";
    private const string ComponentKey = "component";
    private const string TypeKey = "type";
    private const string DataKey = "data";
    private const string TransformKey = "transform";
    private const string ScriptTypeNameKey = "type_name";
    private const string ScriptFieldsKey = "fields";

    /// <summary>不透明の α。</summary>
    private const double OpaqueAlpha = 1.0;

    // ── カメラ・光の欄のうち、舞台では効かない値（形をそろえるために書く。cameraModeTest.scene と同じ値）──

    /// <summary>平行投影のときの高さ（透視なので使われない）。</summary>
    private const double CameraOrthoHeight = 10.0;

    /// <summary>光の届く距離（平行光なので使われない）。</summary>
    private const double LightRange = 10.0;

    /// <summary>スポットの内・外の角度（度。平行光なので使われない）。</summary>
    private const double LightInnerAngleDeg = 25.0, LightOuterAngleDeg = 35.0;

    /// <summary>面光源の幅・高さ（平行光なので使われない）。</summary>
    private const double LightRectSize = 1.0;

    /// <summary>影の縁のぼかし（影を落とさないので使われない）。</summary>
    private const double LightSoftRadius = 0.25;

    /// <summary>照り返しの強さ（舞台では照り返さない）。</summary>
    private const double LightBounceIntensity = 0.0;

    /// <summary>光の色（白）。</summary>
    private static readonly double[] WhiteLight = [1, 1, 1];

    /// <summary>「大きさは親が決める」キャンバス・スプライトに置く仮の大きさ（見本のシーンと同じ 1）。</summary>
    private const double PlaceholderSize = 1.0;

    /// <summary>3D の拡大率（等倍）。</summary>
    private static readonly double[] UnitScale = [1, 1, 1];

    /// <summary>sRGB の 1 成分の最大値（0〜255）。</summary>
    private const double SrgbMax = 255.0;

    /// <summary>sRGB → 線形の変換の境（IEC 61966-2-1）。</summary>
    private const double SrgbLinearThreshold = 0.04045;

    /// <summary>sRGB → 線形の変換の直線部の傾きの逆数。</summary>
    private const double SrgbLinearSlope = 12.92;

    /// <summary>sRGB → 線形の変換の曲線部の定数。</summary>
    private const double SrgbOffset = 0.055, SrgbScale = 1.055, SrgbGamma = 2.4;

    // ============================================================
    //  ノード
    // ============================================================

    /// <summary>2D のノード（actor_kind = Actor2D）。</summary>
    public static JsonObject Node2D(string name, IEnumerable<JsonObject> components, IEnumerable<JsonObject>? children = null) => new()
    {
        [NameKey] = name,
        [ActorKindKey] = ActorKind2D,
        [ComponentsKey] = new JsonArray(components.Select(c => (JsonNode)c).ToArray()),
        [ChildrenKey] = new JsonArray((children ?? []).Select(c => (JsonNode)c).ToArray()),
    };

    /// <summary>3D のノード（位置・向き〈度〉・等倍）。</summary>
    public static JsonObject Node3D(string name, double[] position, double[] rotation, IEnumerable<JsonObject> components) => new()
    {
        [NameKey] = name,
        [TransformKey] = new JsonObject
        {
            ["position"] = Numbers(position),
            ["rotation"] = Numbers(rotation),
            ["scale"] = Numbers(UnitScale),
        },
        [ComponentsKey] = new JsonArray(components.Select(c => (JsonNode)c).ToArray()),
        [ChildrenKey] = new JsonArray(),
    };

    /// <summary>ノードの子の配列（無ければ作る）。</summary>
    public static JsonArray ChildrenOf(JsonObject node)
    {
        if (node[ChildrenKey] is JsonArray children) return children;
        var created = new JsonArray();
        node[ChildrenKey] = created;
        return created;
    }

    // ============================================================
    //  コンポーネント
    // ============================================================

    /// <summary>コンポーネント 1 つ（components の 1 要素）。</summary>
    public static JsonObject Component(string name, string type, JsonObject data) => new()
    {
        [NameKey] = name,
        [ComponentKey] = new JsonObject { [TypeKey] = type, [DataKey] = data },
    };

    /// <summary>
    /// キャンバス。<paramref name="dpUnit"/> が真ならルートの単位を dp にする（子のキャンバスはルートの単位に従うので付けない）。
    /// </summary>
    public static JsonObject Canvas(double width, double height, bool dpUnit)
    {
        var data = new JsonObject
        {
            ["width"] = width,
            ["height"] = height,
            ["auto_scale"] = false,
            ["viewport_ref"] = new JsonObject { ["type"] = "window" },
            ["gravity_mode"] = "world_down",
            ["draw_zone"] = "foreground",
            ["pivot"] = Numbers([0, 0]),
        };
        if (dpUnit) data["unit"] = "dp";
        return Component("Canvas", "CanvasComponent", data);
    }

    /// <summary>親いっぱいに広がる単色のスプライト（背景。色は sRGB 0〜255 で渡し、線形で保存する）。</summary>
    public static JsonObject FillSprite(byte[] srgb) => Component("Sprite", "SpriteComponent", new JsonObject
    {
        ["texture_path"] = "",
        ["color"] = LinearColor(srgb),
        ["width"] = PlaceholderSize,
        ["height"] = PlaceholderSize,
        ["layer"] = 0,
    });

    /// <summary>親の大きさに合わせる（縦横とも）。</summary>
    public static JsonObject LayoutFill() => Component("LayoutItem", "CanvasLayoutItemComponent", new JsonObject
    {
        ["fill_width"] = true,
        ["fill_height"] = true,
    });

    /// <summary>子を縦に並べて中央へ寄せる入れ物（余白・間隔は dp）。</summary>
    public static JsonObject CenterStack(double padding, double spacing) => Component("Stack", "CanvasStackComponent", new JsonObject
    {
        ["enabled"] = true,
        ["direction"] = "vertical",
        ["spacing"] = spacing,
        ["padding"] = new JsonObject { ["left"] = padding, ["top"] = padding, ["right"] = padding, ["bottom"] = padding },
        ["main_align"] = "center",
        ["cross_align"] = "center",
        ["reverse"] = false,
        ["fit_width"] = false,
        ["fit_height"] = false,
        ["hidden_children"] = "collapse",
    });

    /// <summary>スクリプト（型名と欄。欄の値は文字列）。</summary>
    public static JsonObject Script(string componentName, string typeName, IReadOnlyDictionary<string, string> fields)
    {
        var fieldObj = new JsonObject();
        foreach (var (key, value) in fields) fieldObj[key] = value;
        return Component(componentName, "ScriptComponent", new JsonObject
        {
            [ScriptTypeNameKey] = typeName,
            [ScriptFieldsKey] = fieldObj,
        });
    }

    /// <summary>主カメラ（透視・消去の色は sRGB 0〜255 で渡す）。</summary>
    public static JsonObject MainCamera(double fovDeg, int targetWidth, int targetHeight, byte[] clearSrgb) =>
        Component("Camera", "CameraComponent", new JsonObject
        {
            ["fov_y_deg"] = fovDeg,
            ["near"] = ThumbnailStageDefaults.CameraNear,
            ["far"] = ThumbnailStageDefaults.CameraFar,
            ["is_main"] = true,
            ["clear_color"] = LinearColor(clearSrgb),
            ["scaling_mode"] = "vert_minus",
            ["target_width"] = targetWidth,
            ["target_height"] = targetHeight,
            ["bar_color"] = LinearColor(clearSrgb),
            ["projection"] = "perspective",
            ["ortho_height"] = CameraOrthoHeight,
        });

    /// <summary>白い平行光（影は落とさない＝床の無い舞台で影の計算をしない）。</summary>
    public static JsonObject DirectionalLight(double intensity) => Component("Light", "LightComponent", new JsonObject
    {
        ["kind"] = "directional",
        ["color"] = Numbers(WhiteLight),
        ["intensity"] = intensity,
        ["range"] = LightRange,
        ["inner_angle_deg"] = LightInnerAngleDeg,
        ["outer_angle_deg"] = LightOuterAngleDeg,
        ["rect_width"] = LightRectSize,
        ["rect_height"] = LightRectSize,
        ["cast_shadows"] = false,
        ["soft_radius"] = LightSoftRadius,
        ["bounce_intensity"] = LightBounceIntensity,
    });

    /// <summary>
    /// ノードのコンポーネントのうち、型（例 "SpriteComponent"）が一致する最初のものの data を引く。
    /// </summary>
    /// <param name="node">ノード。</param>
    /// <param name="type">コンポーネントの型名。</param>
    /// <returns>data。無ければ null。</returns>
    public static JsonObject? FindComponentDataByType(JsonObject node, string type) =>
        (node[ComponentsKey] as JsonArray)?.OfType<JsonObject>()
            .Select(c => c[ComponentKey] as JsonObject)
            .FirstOrDefault(c => c?[TypeKey] is JsonValue t && t.TryGetValue<string>(out var s) && s == type)?[DataKey] as JsonObject;

    // ============================================================
    //  値
    // ============================================================

    /// <summary>数の配列。</summary>
    public static JsonArray Numbers(double[] values) => new(values.Select(v => (JsonNode)JsonValue.Create(v)).ToArray());

    /// <summary>sRGB（0〜255）を線形の RGBA（0〜1。α = 1）にする（シーンの色は線形で保存されている）。</summary>
    public static JsonArray LinearColor(byte[] srgb) =>
        Numbers([ToLinear(srgb[0]), ToLinear(srgb[1]), ToLinear(srgb[2]), OpaqueAlpha]);

    /// <summary>sRGB の 1 成分（0〜255）を線形（0〜1）にする。</summary>
    private static double ToLinear(byte value)
    {
        double c = value / SrgbMax;
        return c <= SrgbLinearThreshold ? c / SrgbLinearSlope : Math.Pow((c + SrgbOffset) / SrgbScale, SrgbGamma);
    }
}
