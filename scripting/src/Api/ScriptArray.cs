using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace SEED;

/// <summary>
/// 配列要素の「文字列表現の種類」。
/// JSON 配列へ書き出すときの引用符の有無と、値の正規化方法を決める。
/// </summary>
public enum ScriptArrayElementKind
{
    /// <summary>数値（float / double / int / long / short）。JSON では引用符なしで書く。</summary>
    Number,

    /// <summary>真偽値。JSON では <c>true</c> / <c>false</c> をそのまま書く。</summary>
    Bool,

    /// <summary>文字列および参照（アクタ名 "Player" / "Player|Slot"）。JSON では引用符付きで書く。</summary>
    Text,
}

/// <summary>
/// <c>[SerializeField]</c> の「配列フィールド」（<c>T[]</c> / <c>List&lt;T&gt;</c>）に関する
/// 型判定・シリアライズ書式（JSON 配列文字列）・要素値の相互変換をまとめた共通ヘルパー。
///
/// エディタ（インスペクタ UI の生成）とランタイム（スクリプトインスタンスへの注入）の
/// 双方から使われる唯一の正典であり、<see cref="ScriptReference"/> と同じ位置づけである。
///
/// 【なぜ JSON 配列文字列なのか】
/// シーンの保存形式は「フィールドパス → 1 本の文字列」で固定されている
/// （Rust 側 <c>ScriptComponentData.fields</c>）。配列のためにフォーマットを拡張すると
/// 既存シーン・IPC・Undo のすべてに波及するため、配列は「1 本の文字列の中身」として
/// JSON 配列で表現し、外側の仕組みには一切手を入れない設計とした。
///
/// 【書式】
/// - 数値   : <c>[1.5,2,-3]</c>
/// - 真偽値 : <c>[true,false]</c>
/// - 文字列 : <c>["a","b"]</c>（エスケープは JSON 準拠）
/// - 参照   : <c>["Player","Enemy|MainCamera"]</c>（未設定要素は <c>""</c>）
/// - 空配列 : <c>[]</c>
///
/// 【要素の文字列表現】
/// 要素 1 個の文字列表現は、非配列フィールドの表現（<c>ScriptBridge.ConvertValue</c> が
/// 解釈する表記）とまったく同じにしてある。そのため型別の行エディタをそのまま
/// 配列要素にも再利用できる。
/// </summary>
public static class ScriptArray
{
    // ─── 型タグ ───────────────────────────────────────────────

    /// <summary>
    /// 配列フィールドの型タグ接頭辞。型タグは <c>"array:" + 要素型タグ</c>
    /// （例 <c>array:float</c> / <c>array:reference</c>）。
    /// Rust 側 <c>value_matches_type</c> の判定と 1 対 1 で対応させること。
    /// </summary>
    public const string TypeTagPrefix = "array:";

    /// <summary>空配列を表すシリアライズ値。</summary>
    public const string EmptyJson = "[]";

    // ─── 型判定 ───────────────────────────────────────────────

    /// <summary>
    /// フィールド型が配列フィールド（<c>T[]</c> または <c>List&lt;T&gt;</c>）かを判定し、
    /// そうなら要素型と「List かどうか」を返す。
    ///
    /// 多次元配列・ジャグ配列・その他のコレクション（IEnumerable 全般）は対象外。
    /// インスペクタで安全に増減できる形（1 次元・要素型が単純）だけを受け付ける。
    /// </summary>
    public static bool TryGetElementType(Type fieldType, out Type elementType, out bool isList)
    {
        elementType = null!;
        isList      = false;

        // T[]（1 次元のみ）
        if (fieldType.IsArray)
        {
            if (fieldType.GetArrayRank() != 1) return false;
            var elem = fieldType.GetElementType();
            if (elem is null || elem.IsArray) return false;   // ジャグ配列は非対応
            elementType = elem;
            return true;
        }

        // List<T>
        if (fieldType.IsGenericType && fieldType.GetGenericTypeDefinition() == typeof(List<>))
        {
            var elem = fieldType.GetGenericArguments()[0];
            if (elem.IsArray) return false;                    // List<T[]> は非対応
            elementType = elem;
            isList      = true;
            return true;
        }

        return false;
    }

    /// <summary>
    /// 要素型がインスペクタで編集可能かを判定し、可能ならその要素種別を返す。
    /// 対応: float / double / int / long / short / bool / string と参照型
    /// （<see cref="ScriptReference.TryGetKind"/> が真になる型）。
    /// </summary>
    /// <param name="elementType">要素型。</param>
    /// <param name="kind">要素の文字列表現の種類。</param>
    /// <param name="isReference">参照型（アクタ／コンポーネントハンドル）なら true。</param>
    public static bool TryGetElementKind(Type elementType, out ScriptArrayElementKind kind, out bool isReference)
    {
        kind        = ScriptArrayElementKind.Text;
        isReference = false;

        if (ScriptReference.TryGetKind(elementType, out _))
        {
            isReference = true;
            return true;   // 参照はアクタ名の文字列として保存する
        }

        if (elementType == typeof(float) || elementType == typeof(double) ||
            elementType == typeof(int)   || elementType == typeof(long)   ||
            elementType == typeof(short))
        {
            kind = ScriptArrayElementKind.Number;
            return true;
        }
        if (elementType == typeof(bool))
        {
            kind = ScriptArrayElementKind.Bool;
            return true;
        }
        if (elementType == typeof(string))
        {
            kind = ScriptArrayElementKind.Text;
            return true;
        }
        return false;
    }

    /// <summary>
    /// 要素型 1 個分の型タグ（<c>float</c> / <c>int</c> / <c>bool</c> / <c>string</c> /
    /// <c>reference</c> …）を返す。未対応型なら null。
    /// </summary>
    public static string? ElementTypeTag(Type elementType)
    {
        if (ScriptReference.TryGetKind(elementType, out _)) return "reference";
        if (elementType == typeof(float))  return "float";
        if (elementType == typeof(double)) return "double";
        if (elementType == typeof(int))    return "int";
        if (elementType == typeof(long))   return "long";
        if (elementType == typeof(short))  return "short";
        if (elementType == typeof(bool))   return "bool";
        if (elementType == typeof(string)) return "string";
        return null;
    }

    /// <summary>
    /// 要素型に対応する「新規要素の既定値」文字列（[+] ボタンで追加される値）。
    /// 数値は 0、真偽値は false、文字列・参照は空文字。
    /// </summary>
    public static string DefaultElementValue(Type elementType)
    {
        if (!TryGetElementKind(elementType, out var kind, out var isRef)) return "";
        if (isRef) return ScriptReference.UnsetValue;
        return kind switch
        {
            ScriptArrayElementKind.Number => "0",
            ScriptArrayElementKind.Bool   => "false",
            _                             => "",
        };
    }

    // ─── デコード（JSON 配列文字列 → 要素文字列の並び）───────

    /// <summary>
    /// JSON 配列文字列を「要素 1 個ずつの文字列表現」へ分解する。
    ///
    /// 受け付ける要素は文字列・数値・真偽値・null（null は空文字として扱う）。
    /// 入れ子の配列・オブジェクトが現れた時点で解析を中止し、それまでの結果を返す
    /// （壊れた値でクラッシュさせず、可能な範囲だけ復元する方針）。
    /// null・空文字・"[]" は空リストになる。
    /// </summary>
    public static IReadOnlyList<string> Decode(string? json)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(json)) return result;

        var s = json!;
        int i = SkipWhitespace(s, 0);
        if (i >= s.Length || s[i] != '[') return result;   // 配列でなければ空扱い
        i++;

        while (true)
        {
            i = SkipWhitespace(s, i);
            if (i >= s.Length) break;
            if (s[i] == ']') break;
            if (s[i] == ',') { i++; continue; }

            if (s[i] == '"')
            {
                if (!TryReadJsonString(s, ref i, out var text)) break;
                result.Add(text);
            }
            else if (s[i] == '[' || s[i] == '{')
            {
                // 入れ子構造は非対応。ここで解析を打ち切る。
                break;
            }
            else
            {
                // 数値 / true / false / null をリテラルとしてそのまま読む
                int start = i;
                while (i < s.Length && s[i] != ',' && s[i] != ']') i++;
                var token = s[start..i].Trim();
                result.Add(token == "null" ? "" : token);
            }
        }
        return result;
    }

    /// <summary>空白文字を読み飛ばした位置を返す。</summary>
    private static int SkipWhitespace(string s, int i)
    {
        while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
        return i;
    }

    /// <summary>
    /// JSON 文字列リテラル（<c>"..."</c>）を 1 個読み取り、エスケープを解いた本文を返す。
    /// 読み取り位置 <paramref name="i"/> は閉じ引用符の次へ進む。書式不正なら false。
    /// </summary>
    private static bool TryReadJsonString(string s, ref int i, out string text)
    {
        text = "";
        if (i >= s.Length || s[i] != '"') return false;
        i++;   // 開き引用符

        var sb = new StringBuilder();
        while (i < s.Length)
        {
            var c = s[i++];
            if (c == '"') { text = sb.ToString(); return true; }
            if (c != '\\') { sb.Append(c); continue; }

            if (i >= s.Length) return false;
            var esc = s[i++];
            switch (esc)
            {
                case '"':  sb.Append('"');  break;
                case '\\': sb.Append('\\'); break;
                case '/':  sb.Append('/');  break;
                case 'b':  sb.Append('\b'); break;
                case 'f':  sb.Append('\f'); break;
                case 'n':  sb.Append('\n'); break;
                case 'r':  sb.Append('\r'); break;
                case 't':  sb.Append('\t'); break;
                case 'u':
                    if (i + 4 > s.Length) return false;
                    if (!ushort.TryParse(s.Substring(i, 4), NumberStyles.HexNumber,
                                         CultureInfo.InvariantCulture, out var code)) return false;
                    sb.Append((char)code);
                    i += 4;
                    break;
                default:
                    return false;   // 未知のエスケープは書式不正
            }
        }
        return false;   // 閉じ引用符が無い
    }

    // ─── エンコード（要素文字列の並び → JSON 配列文字列）─────

    /// <summary>
    /// 要素の文字列表現の並びを JSON 配列文字列へ書き出す。
    ///
    /// 数値要素が数値として解釈できない場合は 0 に落とす（壊れた JSON を出さないため）。
    /// 真偽値は "true" 以外をすべて false とみなす（非配列フィールドと同じ規則）。
    /// </summary>
    public static string Encode(IReadOnlyList<string> elements, ScriptArrayElementKind kind)
    {
        var inv = CultureInfo.InvariantCulture;
        var sb  = new StringBuilder("[");
        for (int i = 0; i < elements.Count; i++)
        {
            if (i > 0) sb.Append(',');
            var raw = elements[i] ?? "";
            switch (kind)
            {
                case ScriptArrayElementKind.Number:
                    sb.Append(double.TryParse(raw.Trim(), NumberStyles.Float, inv, out var d)
                        ? d.ToString("R", inv)
                        : "0");
                    break;
                case ScriptArrayElementKind.Bool:
                    sb.Append(raw == "true" ? "true" : "false");
                    break;
                default:
                    sb.Append(Quote(raw));
                    break;
            }
        }
        sb.Append(']');
        return sb.ToString();
    }

    /// <summary>
    /// 実配列オブジェクト（<c>T[]</c> / <c>List&lt;T&gt;</c>）を JSON 配列文字列へ書き出す。
    /// 宣言時初期値（既定値）のシリアライズに使う。
    ///
    /// 参照型の要素は実体を文字列化できない（アクタ名はシーン側の情報）ので、
    /// 要素数だけを保ったうえで全要素を「未設定」にする。
    /// </summary>
    public static string EncodeValue(object? arrayOrList, Type elementType)
    {
        if (arrayOrList is not IEnumerable seq) return EmptyJson;
        if (!TryGetElementKind(elementType, out var kind, out var isReference)) return EmptyJson;

        var inv   = CultureInfo.InvariantCulture;
        var items = new List<string>();
        foreach (var item in seq)
        {
            if (isReference) { items.Add(ScriptReference.UnsetValue); continue; }
            items.Add(item switch
            {
                null     => "",
                bool b   => b ? "true" : "false",
                float f  => f.ToString("R", inv),
                double v => v.ToString("R", inv),
                string s => s,
                _        => Convert.ToString(item, inv) ?? "",
            });
        }
        return Encode(items, kind);
    }

    /// <summary>文字列を JSON 文字列リテラル（引用符込み）へエスケープする。</summary>
    public static string Quote(string s)
    {
        var sb = new StringBuilder(s.Length + 2);
        sb.Append('"');
        foreach (var c in s)
        {
            switch (c)
            {
                case '"':  sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n");  break;
                case '\r': sb.Append("\\r");  break;
                case '\t': sb.Append("\\t");  break;
                default:
                    if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                    else          sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
        return sb.ToString();
    }

    // ─── 実体化（要素文字列の並び → T[] / List<T>）───────────

    /// <summary>
    /// JSON 配列文字列から <c>T[]</c> または <c>List&lt;T&gt;</c> の実インスタンスを生成する。
    ///
    /// 要素の変換は <paramref name="convertElement"/> に委譲する
    /// （非配列フィールドとまったく同じ変換規則を使い回すため）。
    /// 変換できない要素は要素型の既定値（数値 0 / false / null）で埋め、
    /// 要素数だけは JSON どおりに保つ。
    ///
    /// <c>List&lt;T&gt;</c> は要素の増減で参照が変わり得るので、常に新しいインスタンスを作る。
    /// </summary>
    public static object BuildInstance(
        Type elementType, bool isList, string? json, Func<Type, string, object?> convertElement)
    {
        var texts  = Decode(json);
        var array  = Array.CreateInstance(elementType, texts.Count);
        for (int i = 0; i < texts.Count; i++)
        {
            object? converted = null;
            try { converted = convertElement(elementType, texts[i]); }
            catch (Exception e) when (e is FormatException or OverflowException or InvalidCastException)
            {
                // 壊れた要素は既定値のまま（例外で配列全体を落とさない）
            }
            if (converted is not null) array.SetValue(converted, i);
        }
        if (!isList) return array;

        // List<T> はコンストラクタ List<T>(IEnumerable<T>) で作り直す
        var listType = typeof(List<>).MakeGenericType(elementType);
        return Activator.CreateInstance(listType, array)!;
    }
}
