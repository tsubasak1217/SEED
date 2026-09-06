using System;
using System.Collections.Generic;
using System.Reflection;

namespace SEEDEditor.Scripting;

/// <summary>
/// 配列フィールド（<c>T[]</c> / <c>List&lt;T&gt;</c>）1 件分の型情報。
///
/// 値は JSON 配列文字列 1 本としてシーンへ保存される（正典は <see cref="SEED.ScriptArray"/>）。
/// インスペクタは要素の追加・削除のたびに JSON を組み直し、
/// 非配列フィールドとまったく同じ経路（SET_SCRIPT_FIELD）で書き戻す。
/// </summary>
/// <param name="ElementType">要素型（例 <c>float</c> / <c>SEED.Transform</c>）。</param>
/// <param name="IsList"><c>List&lt;T&gt;</c> なら true、<c>T[]</c> なら false（UI 上の違いは無い）。</param>
/// <param name="ElementKind">要素の文字列表現の種類（JSON へ書くときの引用符の有無を決める）。</param>
/// <param name="ElementReference">
/// 要素が参照型（GameObject / コンポーネントハンドル）の場合の種別情報。参照でなければ null。
/// </param>
public readonly record struct ScriptArrayFieldInfo(
    Type                                ElementType,
    bool                                IsList,
    SEED.ScriptArrayElementKind         ElementKind,
    SEED.ScriptReference.ReferenceKind? ElementReference
)
{
    /// <summary>
    /// 要素が <c>[System.Serializable]</c> 構造体の場合の、そのメンバ一覧。
    /// スカラ要素・参照要素の配列では null。
    ///
    /// 要素 1 個は JSON オブジェクト（<c>{"spawnDistance":10.0,...}</c>）として保存され、
    /// インスペクタはこのメンバ一覧から「要素の折りたたみ内に並べる行」を組む。
    /// メンバ行の構築には通常のフィールド行ビルダーをそのまま再利用できるよう、
    /// メンバも <see cref="ScriptFieldInfo"/> として保持する（正典は <see cref="SEED.ScriptStructArray"/>）。
    /// </summary>
    public IReadOnlyList<ScriptFieldInfo>? StructMembers { get; init; }
}

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

    /// <summary>
    /// ユーザースクリプト上のこのフィールドに書かれた <c>/// &lt;summary&gt;</c> の説明文。
    /// 無ければ null。属性で明示する <see cref="Tooltip"/> とは独立で、
    /// ラベルのツールチップに「補足説明」として併記される
    /// （抽出元は <see cref="ScriptDocComments"/>。リフレクションでは取得できない情報）。
    /// </summary>
    public string? Summary { get; init; }

    /// <summary>[Range] の最小値。無ければ null。</summary>
    public float? RangeMin { get; init; }

    /// <summary>[Range] の最大値。無ければ null。</summary>
    public float? RangeMax { get; init; }

    /// <summary>[Serializable] ネストクラスの子フィールド。ネストでなければ null。</summary>
    public IReadOnlyList<ScriptFieldInfo>? Children { get; init; }

    /// <summary>
    /// [ResetButton] が付いているか。true の行だけ右端に「デフォルトに戻す」ボタンを出す。
    /// 戻り先の値は <see cref="DefaultValue"/>（宣言時の初期化子。無ければ言語既定値）。
    /// </summary>
    public bool ShowResetButton { get; init; }

    /// <summary>
    /// 参照フィールド（GameObject / Transform / Camera … へのハンドル）の種別情報。
    /// 参照フィールドでなければ null。判定は SEED.ScriptReference が正典。
    /// </summary>
    public SEED.ScriptReference.ReferenceKind? Reference { get; init; }

    /// <summary>
    /// 配列フィールド（<c>T[]</c> / <c>List&lt;T&gt;</c>）の要素型情報。配列でなければ null。
    /// 判定は <see cref="SEED.ScriptArray"/> が正典（ランタイム側と共有）。
    /// </summary>
    public ScriptArrayFieldInfo? Array { get; init; }

    /// <summary>
    /// <c>SEED.ScriptEvent</c>（UnityEvent 相当）フィールドか。
    ///
    /// 値は「呼び出し先の JSON 配列文字列」1 本として保存される葉であり、
    /// 参照でも配列でもネストクラスでもない独立した分類として扱う。
    /// 判定は <see cref="SEED.ScriptEvent.IsScriptEventType"/> が正典
    /// （ランタイム側の値注入と同じ実装を共有する）。
    /// </summary>
    public bool IsScriptEvent { get; init; }
}
