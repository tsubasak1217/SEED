// ============================================================
//  ActorPatch.cs — アクタの木（.actor の JSON）への見本の上書き
//
//  【書き方】（ThumbnailSample の patch・instances の 1 要素）
//    { "<ノードのパス>": { "<コンポーネント名>": { …コンポーネントの data へ深くマージする値… } } }
//    - ノードのパス: 根 = ""、子は name を '/' でつなぐ（例 "Label"・"Pages/Tab0"・"Front/Title"）。同じ名前の子が並ぶときは最初の 1 つ
//    - コンポーネント名: components の各要素の name（例 "Text"・"Button"・"Sprite"）。
//      特別な名前 "$node" はノードそのもの（canvas_transform・active・visible など）へマージする
//    - 深いマージ: 両方がオブジェクトなら中へ入って重ね、それ以外は置き換える（配列も置き換え）
//  例: ボタンの文字 { "Label": { "Text": { "content": "保存する" } } }
//      トグルをオン   { "": { "Toggle": { "fields": { "IsOn": "true" } } } }
//
//  見つからないノード・コンポーネントは誤り（テンプレートの作りが変わったのにデータが古い合図）として返す。
// ============================================================

using System.Text.Json.Nodes;

namespace SEEDEditor.Tools.SeedTemplateThumbnails.Stage;

/// <summary>アクタの木への見本の上書き。状態を持たない。</summary>
public static class ActorPatch
{
    /// <summary>ノードのパスの区切り。</summary>
    private const char PathSeparator = '/';

    /// <summary>ノードそのものへマージする特別なコンポーネント名。</summary>
    public const string NodeTarget = "$node";

    /// <summary>ノードの名前の欄名。</summary>
    private const string NameKey = "name";

    /// <summary>子の配列の欄名。</summary>
    private const string ChildrenKey = "children";

    /// <summary>コンポーネントの配列の欄名。</summary>
    private const string ComponentsKey = "components";

    /// <summary>コンポーネントの中身の欄名（components[i].component）。</summary>
    private const string ComponentKey = "component";

    /// <summary>コンポーネントの値の欄名（components[i].component.data）。</summary>
    private const string DataKey = "data";

    /// <summary>
    /// 上書きを木へ当てる（木を直接書き換える）。
    /// </summary>
    /// <param name="root">アクタの木の根。</param>
    /// <param name="patch">上書き（ノードのパス → コンポーネント名 → 値）。</param>
    /// <param name="errors">当てられなかった理由の積み先。</param>
    public static void Apply(JsonObject root, JsonObject patch, List<string> errors)
    {
        foreach (var (path, components) in patch)
        {
            if (FindNode(root, path) is not { } node)
            {
                errors.Add($"patch: ノードが見つかりません: \"{path}\"");
                continue;
            }
            if (components is not JsonObject byComponent)
            {
                errors.Add($"patch: \"{path}\" の値がオブジェクトではありません");
                continue;
            }
            foreach (var (componentName, value) in byComponent)
            {
                if (value is not JsonObject values)
                {
                    errors.Add($"patch: \"{path}\" の \"{componentName}\" の値がオブジェクトではありません");
                    continue;
                }
                var target = componentName == NodeTarget ? node : FindComponentData(node, componentName);
                if (target is null)
                {
                    errors.Add($"patch: \"{path}\" にコンポーネント \"{componentName}\" がありません");
                    continue;
                }
                DeepMerge(target, values);
            }
        }
    }

    /// <summary>
    /// ノードのパス（根 = ""・子の name を '/' でつなぐ）でノードを引く。
    /// </summary>
    /// <param name="root">木の根。</param>
    /// <param name="path">ノードのパス。</param>
    /// <returns>ノード。無ければ null。</returns>
    public static JsonObject? FindNode(JsonObject root, string path)
    {
        var node = root;
        foreach (var name in path.Split(PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var children = node[ChildrenKey] as JsonArray;
            var next = children?.OfType<JsonObject>()
                .FirstOrDefault(c => c[NameKey] is JsonValue v && v.TryGetValue<string>(out var n) && n == name);
            if (next is null) return null;
            node = next;
        }
        return node;
    }

    /// <summary>
    /// ノードのコンポーネント（name が一致する最初の 1 つ）の data を引く。
    /// </summary>
    /// <param name="node">ノード。</param>
    /// <param name="componentName">コンポーネント名。</param>
    /// <returns>data のオブジェクト。無ければ null。</returns>
    public static JsonObject? FindComponentData(JsonObject node, string componentName)
    {
        var entry = (node[ComponentsKey] as JsonArray)?.OfType<JsonObject>()
            .FirstOrDefault(c => c[NameKey] is JsonValue v && v.TryGetValue<string>(out var n) && n == componentName);
        return entry?[ComponentKey]?[DataKey] as JsonObject;
    }

    /// <summary>
    /// 深いマージ（両方がオブジェクトなら中へ入って重ね、それ以外は複製で置き換える）。
    /// </summary>
    /// <param name="target">書き換える先。</param>
    /// <param name="values">重ねる値。</param>
    public static void DeepMerge(JsonObject target, JsonObject values)
    {
        foreach (var (key, value) in values)
        {
            if (value is JsonObject inner && target[key] is JsonObject existing)
            {
                DeepMerge(existing, inner);
                continue;
            }
            target[key] = value?.DeepClone();
        }
    }
}
