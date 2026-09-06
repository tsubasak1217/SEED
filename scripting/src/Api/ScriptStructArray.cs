using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace SEED;

/// <summary>
/// 構造体配列のメンバ 1 件分のレイアウト情報。
///
/// <c>[System.Serializable]</c> な構造体／クラスを要素にもつ配列フィールド
/// （<c>List&lt;FishLevelEntry&gt;</c> など）について、
/// 「JSON オブジェクトのどのキーが、どの型のメンバか」を表す。
/// エディタはこれを見てメンバ行の UI を組み、ランタイムはこれを見て値を流し込む。
/// </summary>
public sealed class ScriptStructMemberInfo
{
    /// <summary>対応するフィールド（値の読み書きに使う）。</summary>
    public required FieldInfo Field { get; init; }

    /// <summary>JSON オブジェクトのキー（＝フィールド名）。</summary>
    public string Name => Field.Name;

    /// <summary>インスペクタに出す表示名（[SerializeField(Label=...)]、無ければフィールド名）。</summary>
    public required string Label { get; init; }

    /// <summary>
    /// 型タグ。スカラは <c>float</c> / <c>int</c> / <c>bool</c> / <c>string</c> / <c>reference</c>、
    /// 入れ子の配列メンバは <c>array:&lt;要素型タグ&gt;</c>。
    /// Rust 側 <c>value_matches_type</c> の判定と 1 対 1 で対応させること。
    /// </summary>
    public required string TypeTag { get; init; }

    /// <summary>宣言時初期値の文字列表現（配列メンバなら JSON 配列文字列）。</summary>
    public required string DefaultValue { get; init; }

    /// <summary>このメンバ自身が配列（<c>T[]</c> / <c>List&lt;T&gt;</c>）か。</summary>
    public bool IsArray { get; init; }

    /// <summary>配列メンバの要素型（配列でなければ null）。</summary>
    public Type? ElementType { get; init; }

    /// <summary>配列メンバが <c>List&lt;T&gt;</c> なら true（<c>T[]</c> なら false）。</summary>
    public bool IsList { get; init; }

    /// <summary>配列メンバの要素種別（配列でなければ既定値）。</summary>
    public ScriptArrayElementKind ElementKind { get; init; }

    /// <summary>メンバ自身が参照型（GameObject / コンポーネントハンドル）か。</summary>
    public bool IsReference { get; init; }

    /// <summary>
    /// メンバ自身が <see cref="ScriptEvent"/> か。
    /// 値は「JSON 配列」を生のまま埋め込む（文字列としてクォートしない）ので、
    /// エンコード／デコードの分岐に使う。
    /// </summary>
    public bool IsScriptEvent { get; init; }

    /// <summary>配列メンバの要素が参照型か。</summary>
    public bool IsReferenceElement { get; init; }

    /// <summary>このメンバが World 公開後でないと値を作れない（参照を含む）か。</summary>
    public bool NeedsWorld => IsReference || IsReferenceElement;
}

/// <summary>
/// <c>[System.Serializable]</c> な構造体／クラスを要素にもつ配列フィールド
/// （<c>List&lt;T&gt;</c> / <c>T[]</c>）の型判定・シリアライズ書式・相互変換をまとめた共通ヘルパー。
///
/// スカラ要素の配列を扱う <see cref="ScriptArray"/> の姉妹であり、
/// エディタ（インスペクタ UI）とランタイム（値の注入）の双方から使われる唯一の正典である。
///
/// 【書式】
/// 値は「JSON オブジェクトの配列」1 本の文字列として保存する。
/// <code>
/// [{"spawnDistance":10.0,"fishPrefabs":["a.actor","b.actor"]},{"spawnDistance":25.0,"fishPrefabs":[]}]
/// </code>
/// シーンの保存形式（フィールドパス → 1 本の文字列）も IPC も変更しない。
///
/// 【対応メンバ】
/// メンバは <c>[SerializeField]</c> が付いた public/private フィールドで、型は
/// - スカラ（float / double / int / long / short / bool / string）
/// - 参照型（GameObject / Transform / Camera … のハンドル）
/// - 上記の <c>List&lt;&gt;</c> / 配列（入れ子は 1 段まで）
/// のいずれか。1 つでも非対応メンバがあれば**配列フィールド全体を非対応**として扱う
/// （半端に一部だけ編集できる UI は、保存されないメンバを見落とす事故のもとになるため）。
///
/// 【デコードの方針】
/// 保存値は「メンバ名で照合して読む」寛容デコードにしてある。
/// - JSON に無いメンバ → 宣言時初期値のまま
/// - JSON にあるが宣言に無いキー → 無視
/// - 値の型が合わない／壊れている → そのメンバだけ既定値（例外で配列全体を落とさない）
/// これにより、構造体のメンバを増減してもホットリロードで値が生き残る。
/// </summary>
public static class ScriptStructArray
{
    // ─── 型タグ ───────────────────────────────────────────────

    /// <summary>
    /// 構造体要素の型タグ接頭辞。要素型タグは <c>"struct:" + 構造体名</c>、
    /// 配列フィールド全体の型タグは <c>"array:struct:FishLevelEntry"</c> になる。
    /// </summary>
    public const string StructTypeTagPrefix = "struct:";

    /// <summary>空の JSON オブジェクト（メンバが 1 つも無い構造体の既定値）。</summary>
    private const string EmptyObjectJson = "{}";

    /// <summary>要素型 1 個分の型タグ（<c>struct:構造体名</c>）を返す。</summary>
    public static string ElementTypeTag(Type elementType)
        => StructTypeTagPrefix + elementType.Name;

    // ─── レイアウト判定 ───────────────────────────────────────

    /// <summary>
    /// 要素型が「インスペクタで編集できる <c>[Serializable]</c> 構造体／クラス」かを判定し、
    /// そうならメンバのレイアウトを返す。
    ///
    /// 非対応の条件（いずれか 1 つでも該当すれば false）:
    /// - <c>[Serializable]</c> が付いていない
    /// - プリミティブ・列挙型・string・配列・List・参照ハンドル型
    /// - 引数なしで生成できない（クラスで既定コンストラクタが無い）
    /// - <c>[SerializeField]</c> メンバに非対応型（入れ子の構造体など）が含まれる
    /// </summary>
    public static bool TryGetLayout(Type elementType, out IReadOnlyList<ScriptStructMemberInfo> members)
    {
        members = Array.Empty<ScriptStructMemberInfo>();
        if (!IsSerializableStructType(elementType)) return false;

        // 宣言時初期値を読むための一時インスタンス（クラスの初期化子を反映させる）
        object? sample;
        try { sample = Activator.CreateInstance(elementType); }
        catch { return false; }
        if (sample is null) return false;

        var list = new List<ScriptStructMemberInfo>();
        foreach (var f in elementType.GetFields(MemberFlags))
        {
            if (!HasAttributeNamed(f, "SerializeFieldAttribute")) continue;

            var info = BuildMemberInfo(f, sample);
            if (info is null) return false;   // 非対応メンバが 1 つでもあれば配列全体を非対応にする
            list.Add(info);
        }
        members = list;
        return true;
    }

    /// <summary>メンバ探索に使うリフレクションフラグ（public / private の両方を見る）。</summary>
    private const BindingFlags MemberFlags =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

    /// <summary>
    /// <c>[Serializable]</c> が付いたユーザー定義の構造体／クラス型かを判定する。
    /// 参照ハンドル型は「1 個の値」として扱うのでここでは除外する。
    /// </summary>
    private static bool IsSerializableStructType(Type t)
    {
        if (t.IsPrimitive || t.IsEnum || t == typeof(string) || t.IsArray) return false;
        if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(List<>)) return false;
        if (ScriptReference.TryGetKind(t, out _)) return false;
        // ScriptEvent は「1 個の値（JSON 配列文字列）」として扱うので、
        // 内部を展開する構造体要素としては扱わない。
        if (ScriptEvent.IsScriptEventType(t)) return false;
        if (!t.IsClass && !t.IsValueType) return false;
        // Nullable<T> は「構造体そのもの」ではないので除外する
        if (Nullable.GetUnderlyingType(t) is not null) return false;
        // アセンブリ ID 差異を吸収するため属性名で照合する（ScriptBridge / ScriptCompiler と同じ理由）
        return t.GetCustomAttributesData().Any(a => a.AttributeType.Name == "SerializableAttribute");
    }

    /// <summary>フィールドに指定名の属性が付いているかを属性名で判定する。</summary>
    private static bool HasAttributeNamed(FieldInfo f, string attributeTypeName)
        => f.GetCustomAttributesData().Any(a => a.AttributeType.Name == attributeTypeName);

    /// <summary>
    /// メンバ 1 件のレイアウト情報を組み立てる。非対応型なら null を返す。
    /// </summary>
    private static ScriptStructMemberInfo? BuildMemberInfo(FieldInfo f, object sample)
    {
        var label = ReadLabel(f) ?? f.Name;
        object? value;
        try { value = f.GetValue(sample); } catch { value = null; }

        // 参照メンバ（単体）
        if (ScriptReference.TryGetKind(f.FieldType, out _))
        {
            return new ScriptStructMemberInfo
            {
                Field        = f,
                Label        = label,
                TypeTag      = "reference",
                DefaultValue = ScriptReference.UnsetValue,
                IsReference  = true,
            };
        }

        // ScriptEvent メンバ（結線は実行時に名前で解決するので World は不要＝NeedsWorld=false）
        if (ScriptEvent.IsScriptEventType(f.FieldType))
        {
            return new ScriptStructMemberInfo
            {
                Field         = f,
                Label         = label,
                TypeTag       = ScriptEvent.TypeTag,
                DefaultValue  = ScriptEvent.EmptyJson,
                IsScriptEvent = true,
            };
        }

        // 配列メンバ（入れ子は 1 段まで＝要素はスカラか参照のみ）
        if (ScriptArray.TryGetElementType(f.FieldType, out var elemType, out var isList))
        {
            if (!ScriptArray.TryGetElementKind(elemType, out var elemKind, out var elemIsRef)) return null;
            return new ScriptStructMemberInfo
            {
                Field              = f,
                Label              = label,
                TypeTag            = ScriptArray.TypeTagPrefix + ScriptArray.ElementTypeTag(elemType),
                DefaultValue       = ScriptArray.EncodeValue(value, elemType),
                IsArray            = true,
                ElementType        = elemType,
                IsList             = isList,
                ElementKind        = elemKind,
                IsReferenceElement = elemIsRef,
            };
        }

        // スカラメンバ
        var tag = ScalarTypeTag(f.FieldType);
        if (tag is null) return null;   // 入れ子の構造体・列挙型などは非対応
        return new ScriptStructMemberInfo
        {
            Field        = f,
            Label        = label,
            TypeTag      = tag,
            DefaultValue = ScalarValueString(value),
        };
    }

    /// <summary>[SerializeField(Label = "...")] の表示名を読み取る（無ければ null）。</summary>
    private static string? ReadLabel(FieldInfo f)
    {
        var data = f.GetCustomAttributesData()
            .FirstOrDefault(a => a.AttributeType.Name == "SerializeFieldAttribute");
        if (data is null) return null;
        foreach (var na in data.NamedArguments)
            if (na.MemberName == "Label") return na.TypedValue.Value as string;
        return null;
    }

    /// <summary>スカラ型の型タグ（非対応なら null）。<see cref="ScriptArray.ElementTypeTag"/> と同じ表。</summary>
    private static string? ScalarTypeTag(Type t)
    {
        if (t == typeof(float))  return "float";
        if (t == typeof(double)) return "double";
        if (t == typeof(int))    return "int";
        if (t == typeof(long))   return "long";
        if (t == typeof(short))  return "short";
        if (t == typeof(bool))   return "bool";
        if (t == typeof(string)) return "string";
        return null;
    }

    /// <summary>スカラ値を「非配列フィールドと同じ文字列表現」へ変換する。</summary>
    private static string ScalarValueString(object? value)
    {
        if (value is null) return "";
        var inv = CultureInfo.InvariantCulture;
        return value switch
        {
            bool b   => b ? "true" : "false",
            float f  => f.ToString("R", inv),
            double d => d.ToString("R", inv),
            string s => s,
            _        => Convert.ToString(value, inv) ?? "",
        };
    }

    // ─── メタデータ（Rust への受け渡し）───────────────────────

    /// <summary>
    /// メンバのレイアウトを JSON 配列として書き出す。
    /// <c>DescribeSerializeFields</c> のフィールド要素へ <c>"members"</c> として埋め込み、
    /// Rust 側のホットリロード引き継ぎ判定（メンバ名＋型の照合）に使う。
    ///
    /// 形式: <c>[{"name":"spawnDistance","label":"出現距離","type":"float","default":"0"}, ...]</c>
    /// </summary>
    public static string MembersMetadataJson(IReadOnlyList<ScriptStructMemberInfo> members)
    {
        var sb = new StringBuilder("[");
        for (int i = 0; i < members.Count; i++)
        {
            if (i > 0) sb.Append(',');
            var m = members[i];
            sb.Append("{\"name\":").Append(ScriptArray.Quote(m.Name))
              .Append(",\"label\":").Append(ScriptArray.Quote(m.Label))
              .Append(",\"type\":").Append(ScriptArray.Quote(m.TypeTag))
              .Append(",\"default\":").Append(ScriptArray.Quote(m.DefaultValue))
              .Append('}');
        }
        sb.Append(']');
        return sb.ToString();
    }

    // ─── 要素（JSON オブジェクト）の分解・組み立て ────────────

    /// <summary>
    /// JSON オブジェクト配列文字列を「要素 1 個ずつの JSON オブジェクト文字列」へ分解する。
    ///
    /// 配列でない・壊れている場合は空リストを返す（例外は投げない）。
    /// 配列内のオブジェクト以外の要素（数値・文字列など）は読み飛ばす。
    /// </summary>
    public static IReadOnlyList<string> DecodeObjects(string? json)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(json)) return result;

        try
        {
            using var doc = JsonDocument.Parse(json!);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return result;
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;   // 壊れ要素はスキップ
                result.Add(item.GetRawText());
            }
        }
        catch (JsonException)
        {
            // 壊れた JSON は「要素なし」として扱う（インスペクタを落とさない）
        }
        return result;
    }

    /// <summary>
    /// 要素（JSON オブジェクト文字列）の並びを 1 本の JSON 配列文字列へ束ねる。
    /// 空文字・オブジェクトでない要素は空オブジェクト <c>{}</c> に落とす。
    /// </summary>
    public static string EncodeObjects(IReadOnlyList<string> objectJsons)
    {
        var sb = new StringBuilder("[");
        for (int i = 0; i < objectJsons.Count; i++)
        {
            if (i > 0) sb.Append(',');
            var raw = (objectJsons[i] ?? "").Trim();
            sb.Append(raw.StartsWith('{') && raw.EndsWith('}') ? raw : EmptyObjectJson);
        }
        sb.Append(']');
        return sb.ToString();
    }

    /// <summary>要素 1 個分の既定値（[+] で追加される JSON オブジェクト）を作る。</summary>
    public static string DefaultObjectJson(IReadOnlyList<ScriptStructMemberInfo> members)
    {
        var values = new Dictionary<string, string>(members.Count);
        foreach (var m in members) values[m.Name] = m.DefaultValue;
        return EncodeMembers(values, members);
    }

    // ─── メンバ値（文字列表現）の相互変換 ─────────────────────

    /// <summary>
    /// 要素の JSON オブジェクトを「メンバ名 → 文字列表現」へ分解する。
    ///
    /// 文字列表現はスカラなら非配列フィールドと同じ表記、配列メンバなら
    /// JSON 配列文字列（<see cref="ScriptArray"/> の書式）で、
    /// そのまま既存の型別エディタへ渡せる。
    /// JSON に無いメンバ・型が合わないメンバは宣言時初期値で埋める。
    /// </summary>
    public static IReadOnlyDictionary<string, string> DecodeMembers(
        string? objectJson, IReadOnlyList<ScriptStructMemberInfo> members)
    {
        var values = new Dictionary<string, string>(members.Count);
        foreach (var m in members) values[m.Name] = m.DefaultValue;   // まず既定値で埋める
        if (string.IsNullOrWhiteSpace(objectJson)) return values;

        try
        {
            using var doc = JsonDocument.Parse(objectJson!);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return values;

            foreach (var m in members)
            {
                if (!doc.RootElement.TryGetProperty(m.Name, out var prop)) continue;   // 欠損は既定値
                var text = MemberTextFromJson(prop, m);
                if (text is not null) values[m.Name] = text;                            // 型不一致も既定値
            }
        }
        catch (JsonException)
        {
            // 壊れたオブジェクトは全メンバ既定値のまま返す
        }
        return values;
    }

    /// <summary>
    /// JSON 値をメンバの文字列表現へ変換する。型が食い違う場合は null（＝既定値を使う）。
    /// </summary>
    private static string? MemberTextFromJson(JsonElement value, ScriptStructMemberInfo m)
    {
        // ScriptEvent は「JSON 配列そのもの」が値。生テキストを取り出して次段へ渡す。
        if (m.IsScriptEvent)
            return value.ValueKind == JsonValueKind.Array ? value.GetRawText() : null;

        if (m.IsArray)
            return value.ValueKind == JsonValueKind.Array ? value.GetRawText() : null;

        return m.TypeTag switch
        {
            "float" or "double" or "int" or "long" or "short"
                => value.ValueKind == JsonValueKind.Number ? value.GetRawText() : null,
            "bool"
                => value.ValueKind switch
                {
                    JsonValueKind.True  => "true",
                    JsonValueKind.False => "false",
                    _                   => null,
                },
            // string / reference は JSON 文字列。null は「未設定（空文字）」として受ける。
            _   => value.ValueKind switch
                {
                    JsonValueKind.String => value.GetString() ?? "",
                    JsonValueKind.Null   => "",
                    _                    => null,
                },
        };
    }

    /// <summary>
    /// 「メンバ名 → 文字列表現」を要素 1 個分の JSON オブジェクト文字列へ組み立てる。
    /// 値が欠けているメンバは既定値で書く（キーは常に全メンバぶん出す）。
    /// </summary>
    public static string EncodeMembers(
        IReadOnlyDictionary<string, string> values, IReadOnlyList<ScriptStructMemberInfo> members)
    {
        var inv = CultureInfo.InvariantCulture;
        var sb  = new StringBuilder("{");
        for (int i = 0; i < members.Count; i++)
        {
            var m = members[i];
            if (i > 0) sb.Append(',');
            sb.Append(ScriptArray.Quote(m.Name)).Append(':');

            var raw = values.TryGetValue(m.Name, out var v) ? (v ?? "") : m.DefaultValue;

            if (m.IsScriptEvent)
            {
                // ScriptEvent は JSON 配列を生のまま埋め込む（クォートすると二重エスケープになる）。
                // 壊れた文字列を埋め込まないよう必ず Normalize を通す。
                sb.Append(ScriptEvent.Normalize(raw));
                continue;
            }

            if (m.IsArray)
            {
                // 入れ子配列は必ず ScriptArray で組み直す（壊れた文字列をそのまま埋め込まないため）
                sb.Append(ScriptArray.Encode(ScriptArray.Decode(raw), m.ElementKind));
                continue;
            }

            switch (m.TypeTag)
            {
                case "float" or "double" or "int" or "long" or "short":
                    sb.Append(double.TryParse(raw.Trim(), NumberStyles.Float, inv, out var d)
                        ? d.ToString("R", inv) : "0");
                    break;
                case "bool":
                    sb.Append(raw == "true" ? "true" : "false");
                    break;
                default:
                    sb.Append(ScriptArray.Quote(raw));
                    break;
            }
        }
        sb.Append('}');
        return sb.ToString();
    }

    // ─── 実体化（JSON → List&lt;T&gt; / T[]）────────────────────

    /// <summary>
    /// JSON オブジェクト配列文字列から <c>List&lt;T&gt;</c> / <c>T[]</c> の実インスタンスを生成する。
    ///
    /// 要素はメンバ名で照合して埋め、欠損メンバは宣言時初期値のまま、
    /// 壊れた値はそのメンバだけ既定値になる（例外で配列全体を落とさない）。
    /// スカラ・参照の実際の変換は <paramref name="convertLeaf"/> に委譲する
    /// （即時適用では ConvertValue、参照解決フェーズでは Resolve を渡す）。
    /// </summary>
    public static object BuildInstance(
        Type                                  elementType,
        bool                                  isList,
        string?                               json,
        IReadOnlyList<ScriptStructMemberInfo> members,
        Func<Type, string, object?>           convertLeaf)
    {
        var objects = DecodeObjects(json);
        var array   = Array.CreateInstance(elementType, objects.Count);

        for (int i = 0; i < objects.Count; i++)
        {
            object? boxed;
            try { boxed = Activator.CreateInstance(elementType); }
            catch { continue; }                 // 生成できない要素は既定値のまま残す
            if (boxed is null) continue;

            var values = DecodeMembers(objects[i], members);
            foreach (var m in members)
            {
                if (!values.TryGetValue(m.Name, out var text)) continue;
                try
                {
                    object? converted = m.IsArray
                        ? ScriptArray.BuildInstance(m.ElementType!, m.IsList, text, convertLeaf)
                        : convertLeaf(m.Field.FieldType, text);
                    if (converted is not null) m.Field.SetValue(boxed, converted);
                }
                catch (Exception e) when (e is FormatException or OverflowException or InvalidCastException
                                            or ArgumentException)
                {
                    // 壊れたメンバは宣言時初期値のまま（他メンバ・他要素の巻き添えを防ぐ）
                }
            }
            array.SetValue(boxed, i);
        }

        if (!isList) return array;
        var listType = typeof(List<>).MakeGenericType(elementType);
        return Activator.CreateInstance(listType, array)!;
    }

    /// <summary>
    /// 実インスタンス（<c>List&lt;T&gt;</c> / <c>T[]</c>）を JSON オブジェクト配列文字列へ書き出す。
    /// 宣言時初期値のシリアライズに使う。
    ///
    /// 参照メンバは実体を文字列化できない（アクタ名はシーン側の情報）ので未設定として書く。
    /// </summary>
    public static string EncodeValue(
        object? arrayOrList, IReadOnlyList<ScriptStructMemberInfo> members)
    {
        if (arrayOrList is not IEnumerable seq) return ScriptArray.EmptyJson;

        var objects = new List<string>();
        foreach (var item in seq)
        {
            if (item is null) { objects.Add(DefaultObjectJson(members)); continue; }

            var values = new Dictionary<string, string>(members.Count);
            foreach (var m in members)
            {
                object? v = null;
                try { v = m.Field.GetValue(item); } catch { /* 読めないメンバは既定値 */ }
                values[m.Name] = m switch
                {
                    { IsReference: true }   => ScriptReference.UnsetValue,
                    // 結線はインスペクタで作るものなので宣言時初期値は常に「結線なし」
                    { IsScriptEvent: true } => ScriptEvent.EmptyJson,
                    { IsArray: true }       => ScriptArray.EncodeValue(v, m.ElementType!),
                    _                       => ScalarValueString(v),
                };
            }
            objects.Add(EncodeMembers(values, members));
        }
        return EncodeObjects(objects);
    }
}
