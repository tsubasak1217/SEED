using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;

namespace SEEDEditor.Panels.Inspector;

/// <summary>
/// ロックしたアクタと、ロックした番号にいま居るアクタを照合した結果（<see cref="InspectorLockIdentity.Compare"/>）。
/// </summary>
public enum InspectorLockVerdict
{
    /// <summary>名前も中身（構成）も同じ。ロックを続け、届いた値で描き直す。</summary>
    Same,

    /// <summary>名前か中身（構成）が違う。番号へ別のアクタがずれ込んだので、ロックを外して知らせる。</summary>
    Different,

    /// <summary>どちらかが解析できない（壊れた応答・目印が無い）。判断しない（ロックを壊さない）。</summary>
    Unknown,
}

/// <summary>
/// インスペクタのロックが「同じアクタ」を指し続けているかを確かめるための目印（WPF 非依存の純粋なロジック）。
///
/// <para>
/// 【なぜ要るか（2026-10-03 の 2 回目のレビュー #13）】
/// ロックは DFS 番号で対象を持つ。番号はツリーの位置なので、手前のアクタの増減（削除の Undo など）で同じ番号に別のアクタが入る。
/// 従来の確かめ（InspectorPanel.Lock.cs）は「同じ番号に同じ名前」だけで、同じ名前の兄弟（リストの行など）がずれ込むと続いてしまい、
/// 表示はロックしたアクタの値のまま、書き先は別のアクタになっていた。ヒエラルキーが変わるたびにロックした番号の
/// ACTOR_COMPONENTS を取り直し、この目印で照合する。
/// </para>
/// <para>
/// 【目印に入れるもの】名前・ルートの種類（3D の Transform / 2D の CanvasTransform / どちらも無い）・フォルダか・プレハブの参照・
/// コンポーネントの構成（スロットごとの種類・番号・スロット名・スクリプトのパス）。値（位置・色など）は入れない
/// （ロック中も値の変化は表示へ反映し続けるのがロックの約束なので、値で照合するとロックが外れ続ける）。
/// 名前も構成も同じ兄弟（同じプレハブの行など）は見分けられない（ACTOR_COMPONENTS に個体を表す ID が無い）。
/// その場合も取り直した値で描き直すので、表示と書き先は同じアクタになる（食い違いは起きない）。
/// </para>
/// </summary>
/// <param name="Name">アクタの名前（ACTOR_COMPONENTS の "name"）。</param>
/// <param name="Composition">名前以外の中身の要約（ルートの種類・フォルダ・プレハブの参照・コンポーネントの構成を並べた文字列）。</param>
public sealed record InspectorLockIdentity(string Name, string Composition)
{
    // ── ACTOR_COMPONENTS の JSON のキー（runtime の component_ops.rs send_actor_components と一致必須）──

    /// <summary>アクタ名のキー。</summary>
    private const string NameKey = "name";

    /// <summary>3D のルート Transform のキー（あれば 3D のアクタ）。</summary>
    private const string TransformKey = "transform";

    /// <summary>2D のルート CanvasTransform のキー（あれば 2D のアクタ）。</summary>
    private const string CanvasTransformKey = "canvas_transform";

    /// <summary>フォルダのノードかのキー。</summary>
    private const string IsFolderKey = "is_folder";

    /// <summary>プレハブの参照パスのキー（非プレハブは null）。</summary>
    private const string PrefabSourceKey = "prefab_source";

    /// <summary>コンポーネントの配列のキー。</summary>
    private const string ComponentsKey = "components";

    /// <summary>コンポーネントの種類のキー。</summary>
    private const string ComponentTypeKey = "type";

    /// <summary>コンポーネントのスロット番号のキー。</summary>
    private const string ComponentSlotKey = "slot";

    /// <summary>スロット名のキー。</summary>
    private const string ComponentNameKey = "name";

    /// <summary>スクリプトのパスのキー（ScriptComponent だけが持つ）。</summary>
    private const string ComponentScriptPathKey = "model_path";

    /// <summary>要約の区切り（項目の間）。名前やパスに入りにくい制御文字を使う。</summary>
    private const char FieldSeparator = '\u001F';

    /// <summary>要約の区切り（コンポーネントの間）。</summary>
    private const char ComponentSeparator = '\u001E';

    /// <summary>
    /// ACTOR_COMPONENTS の JSON から目印を作る。解析できなければ null。
    /// </summary>
    /// <param name="actorComponentsJson">ACTOR_COMPONENTS の応答 JSON。</param>
    public static InspectorLockIdentity? TryParse(string actorComponentsJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(actorComponentsJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            var name = root.TryGetProperty(NameKey, out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() ?? "" : "";
            var sb = new StringBuilder();

            // ルートの種類（3D / 2D / どちらも無い）・フォルダ・プレハブの参照
            sb.Append(root.TryGetProperty(TransformKey, out _) ? "3D" : root.TryGetProperty(CanvasTransformKey, out _) ? "2D" : "-");
            sb.Append(FieldSeparator);
            sb.Append(root.TryGetProperty(IsFolderKey, out var f) && f.ValueKind == JsonValueKind.True ? "folder" : "actor");
            sb.Append(FieldSeparator);
            sb.Append(root.TryGetProperty(PrefabSourceKey, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : "");

            // コンポーネントの構成（届いた順。スロットの並びも中身のうち）
            if (root.TryGetProperty(ComponentsKey, out var comps) && comps.ValueKind == JsonValueKind.Array)
            {
                foreach (var comp in comps.EnumerateArray())
                {
                    sb.Append(ComponentSeparator);
                    sb.Append(ReadString(comp, ComponentTypeKey)).Append(FieldSeparator);
                    sb.Append(comp.TryGetProperty(ComponentSlotKey, out var s) && s.TryGetInt32(out var slot) ? slot : -1).Append(FieldSeparator);
                    sb.Append(ReadString(comp, ComponentNameKey)).Append(FieldSeparator);
                    sb.Append(ReadString(comp, ComponentScriptPathKey));
                }
            }
            return new InspectorLockIdentity(name, sb.ToString());
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// ロックしたアクタの目印と、ロックした番号にいま居るアクタの目印を照合する。
    /// </summary>
    /// <param name="locked">ロックしたアクタの目印（ロックした時点・その後に描いた最新の構成）。</param>
    /// <param name="incoming">取り直した ACTOR_COMPONENTS の目印。</param>
    public static InspectorLockVerdict Compare(InspectorLockIdentity? locked, InspectorLockIdentity? incoming)
    {
        if (locked is null || incoming is null) return InspectorLockVerdict.Unknown;
        return string.Equals(locked.Name, incoming.Name, StringComparison.Ordinal)
            && string.Equals(locked.Composition, incoming.Composition, StringComparison.Ordinal)
            ? InspectorLockVerdict.Same
            : InspectorLockVerdict.Different;
    }

    /// <summary>オブジェクトの文字列の値を読む（無い・文字列でなければ空）。</summary>
    private static string ReadString(JsonElement obj, string key)
        => obj.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
}
