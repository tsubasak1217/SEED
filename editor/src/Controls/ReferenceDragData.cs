using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Windows;

namespace SEEDEditor.Controls;

/// <summary>
/// 参照ピッカーが受け付けるドラッグデータの形式名。
///
/// ドラッグ元が別々のパネル（Hierarchy / シーンビュー / インスペクタ）に分かれているため、
/// 形式名の文字列をここに集約して重複定義を無くす。
/// </summary>
internal static class ReferenceDragFormats
{
    /// <summary>Hierarchy パネルが単一アクタドラッグ時に付ける DFS ID（int）。</summary>
    public const string HierarchyActorDfsId = "HierarchyActorDfsId";

    /// <summary>シーンビューからのアクタドラッグに付く DFS ID（int）。</summary>
    public const string SceneViewActorDfsId = "SceneViewActorDfsId";

    /// <summary>
    /// インスペクタのコンポーネントヘッダから開始したドラッグに付くペイロード（JSON 文字列）。
    /// 実体は <see cref="ComponentDragPayload"/>。
    /// </summary>
    public const string InspectorComponent = "SEEDInspectorComponentRef";
}

/// <summary>
/// インスペクタのコンポーネントヘッダをドラッグしたときに運ぶ情報。
///
/// 「ドロップ対象そのもの（このコンポーネント）」と「その所有者（このアクタ）」の
/// どちらとしても解釈できるよう、ドラッグしたスロット自身に加えて
/// 所有アクタの全スロット構成も同梱する。これにより参照ピッカーは
/// IPC 往復なしでドラッグ中に可否を判定できる。
///
/// DataObject へは JSON 文字列として載せる（任意のオブジェクトを直接載せると
/// シリアライズ可否がプロセス境界で問題になるため）。
/// </summary>
internal sealed class ComponentDragPayload
{
    /// <summary>所有アクタの DFS ID。</summary>
    public int ActorDfsId { get; set; } = -1;

    /// <summary>所有アクタ名。</summary>
    public string ActorName { get; set; } = "";

    /// <summary>ドラッグしたコンポーネントのスロット添字。</summary>
    public int SlotIdx { get; set; } = -1;

    /// <summary>ドラッグしたコンポーネントのスロット名（表示名）。</summary>
    public string SlotName { get; set; } = "";

    /// <summary>ドラッグしたコンポーネントの種別（ACTOR_COMPONENTS の "type"）。</summary>
    public string TypeId { get; set; } = "";

    /// <summary>
    /// ドラッグしたコンポーネントが ScriptComponent のときの .cs パス。
    /// スクリプト参照フィールド（"Script:PlayerMove"）の型判定に必要。
    /// </summary>
    public string ScriptPath { get; set; } = "";

    /// <summary>所有アクタが 3D の Transform を持つか。</summary>
    public bool HasTransform { get; set; }

    /// <summary>所有アクタが 2D の CanvasTransform を持つか。</summary>
    public bool HasCanvasTransform { get; set; }

    /// <summary>所有アクタの全スロット構成（所有者としての解釈に使う）。</summary>
    public List<ActorComponentEntry> OwnerComponents { get; set; } = new();

    /// <summary>ドラッグしたコンポーネント自身を <see cref="ActorComponentEntry"/> の形に組み直す。</summary>
    public ActorComponentEntry ToDraggedEntry() => new(TypeId, SlotIdx, SlotName, ScriptPath);

    /// <summary>所有アクタ側の情報を <see cref="ActorComponentSnapshot"/> の形に組み直す。</summary>
    public ActorComponentSnapshot ToOwnerSnapshot() => new()
    {
        DfsId              = ActorDfsId,
        ActorName          = ActorName,
        HasTransform       = HasTransform,
        HasCanvasTransform = HasCanvasTransform,
        Components         = OwnerComponents,
    };

    /// <summary>DataObject へ載せる JSON 文字列へ変換する。</summary>
    public string ToJson() => JsonSerializer.Serialize(this);

    /// <summary>JSON 文字列から復元する。壊れていれば null。</summary>
    public static ComponentDragPayload? FromJson(string json)
    {
        try { return JsonSerializer.Deserialize<ComponentDragPayload>(json); }
        catch { return null; }
    }

    /// <summary>
    /// ドラッグデータからコンポーネントペイロードを取り出す（無ければ null）。
    /// </summary>
    public static ComponentDragPayload? FromDragData(IDataObject data)
    {
        if (!data.GetDataPresent(ReferenceDragFormats.InspectorComponent)) return null;
        return data.GetData(ReferenceDragFormats.InspectorComponent) is string json
            ? FromJson(json)
            : null;
    }
}

/// <summary>
/// 参照ピッカーへ落とされたドラッグデータの読み取りヘルパ。
/// アクタ行ドラッグ（Hierarchy / シーンビュー）とコンポーネントヘッダドラッグの
/// 両方を、同じ「対象アクタ DFS ID（＋任意でドラッグされたコンポーネント）」へ正規化する。
/// </summary>
internal static class ReferenceDragData
{
    /// <summary>
    /// ドラッグデータから対象アクタの DFS ID を取り出す（該当が無ければ null）。
    /// コンポーネントドラッグの場合は「その所有アクタ」の DFS ID を返す。
    /// </summary>
    public static int? TryGetActorDfsId(IDataObject data)
    {
        if (ComponentDragPayload.FromDragData(data) is { ActorDfsId: >= 0 } payload)
            return payload.ActorDfsId;
        if (data.GetDataPresent(ReferenceDragFormats.HierarchyActorDfsId))
            return data.GetData(ReferenceDragFormats.HierarchyActorDfsId) as int?;
        if (data.GetDataPresent(ReferenceDragFormats.SceneViewActorDfsId))
            return data.GetData(ReferenceDragFormats.SceneViewActorDfsId) as int?;
        return null;
    }

    /// <summary>ドラッグデータが参照ピッカーの入力になり得るか（形式だけの判定）。</summary>
    public static bool HasReferenceSource(IDataObject data) => TryGetActorDfsId(data) is not null;
}
