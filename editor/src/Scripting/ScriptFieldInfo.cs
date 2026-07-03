using System.Collections.Generic;
using System.Reflection;

namespace SEEDEditor.Scripting;

/// <summary>
/// インスペクタに表示する [SerializeField] フィールド 1 件の情報。
///
/// - Children が非 null の場合、このフィールドは [Serializable] なネストクラスであり、
///   Children にその内部フィールド群を保持する（折りたたみ表示・再帰編集用）。
/// - RangeMin/Max が指定されている場合、[Range] によるスライダー表示を行う。
/// - Header が指定されている場合、このフィールドの直前に見出しを表示する。
/// </summary>
public record ScriptFieldInfo(
    FieldInfo Field,
    string    Label,
    string?   Tooltip,
    object?   DefaultValue
)
{
    /// <summary>フィールド直前に表示する見出し（[Header]）。無ければ null。</summary>
    public string? Header { get; init; }

    /// <summary>[Range] の最小値。無ければ null。</summary>
    public float? RangeMin { get; init; }

    /// <summary>[Range] の最大値。無ければ null。</summary>
    public float? RangeMax { get; init; }

    /// <summary>[Serializable] ネストクラスの子フィールド。ネストでなければ null。</summary>
    public IReadOnlyList<ScriptFieldInfo>? Children { get; init; }
}
