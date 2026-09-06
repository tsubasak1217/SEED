using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace SEED;

/// <summary>
/// Unity の <c>UnityEvent</c> 相当の「インスペクタで結線できるイベント」。
///
/// スクリプト側は次のように宣言し、好きなタイミングで <see cref="Invoke"/> を呼ぶだけでよい。
/// <code>
/// [SerializeField] public ScriptEvent onStart;   // フィールド初期化子は不要
/// ...
/// onStart.Invoke();                              // 結線された全メソッドを順に呼ぶ
/// </code>
///
/// 【非 null 保証】
/// 値は必ずエンジン側（<c>ScriptBridge.ConvertValue</c> → <see cref="BuildInstance"/>）が
/// 生成して注入するため、フィールド初期化子が無くても null にはならない。
/// そのため既定コンストラクタを必ず持たせ、<c>Activator.CreateInstance</c> で作れる状態を保つこと。
///
/// 【シリアライズ書式】
/// 1 フィールド = JSON 配列文字列 1 本。キーは常に 5 個すべて出力し、未設定は
/// <see cref="EmptyJson"/>（<c>[]</c>）にする。
/// <code>
/// [{"actor":"DialogueManager","script":"QuestFlow","method":"Begin","argKind":"string","arg":"intro"}]
/// </code>
/// デコードは寛容にしてある（未知キーは無視、欠損キーは既定値）。
/// 将来 <c>"slot"</c>（同型スクリプトの複数スロット指定）などのキーを足しても、
/// 古い保存データがそのまま読める。
///
/// 【非対応】
/// <c>List&lt;ScriptEvent&gt;</c> のような「ScriptEvent の配列」は現状非対応
/// （<see cref="ScriptArray"/> は要素をスカラ／参照に限っている）。
/// </summary>
public sealed class ScriptEvent
{
    // ─── 型タグ・書式定数（エディタ／Rust と共有する正典）───────

    /// <summary>
    /// ScriptEvent フィールドの型タグ。
    /// Rust 側 <c>script_component.rs</c> の <c>value_matches_type</c> と一致させること。
    /// </summary>
    public const string TypeTag = "scriptevent";

    /// <summary>未設定（バインディング 0 件）を表す JSON 文字列。</summary>
    public const string EmptyJson = "[]";

    /// <summary>bool 値の真を表す文字列表現（他のフィールド型と同じ表記）。</summary>
    public const string TrueText = "true";

    /// <summary>bool 値の偽を表す文字列表現。</summary>
    public const string FalseText = "false";

    // JSON のキー名（5 キー固定）。エディタ側もこの定数を参照して食い違いを防ぐ。

    /// <summary>呼び出し先アクター名のキー。</summary>
    public const string KeyActor = "actor";

    /// <summary>呼び出し先スクリプト型名のキー。</summary>
    public const string KeyScript = "script";

    /// <summary>呼び出すメソッド名のキー。</summary>
    public const string KeyMethod = "method";

    /// <summary>固定引数の種別のキー（値は小文字文字列）。</summary>
    public const string KeyArgKind = "argKind";

    /// <summary>固定引数の値のキー。</summary>
    public const string KeyArg = "arg";

    // ─── インスタンス側（スクリプトから使う面）─────────────────

    /// <summary>結線された呼び出し先の並び（インスペクタでの並び順どおりに呼ぶ）。</summary>
    private readonly List<ScriptEventBinding> _bindings = new();

    /// <summary>
    /// 既定コンストラクタ。
    /// <c>Activator.CreateInstance</c> から生成できる必要があるため、必ず残すこと。
    /// </summary>
    public ScriptEvent() { }

    /// <summary>バインディングを指定して生成する（<see cref="BuildInstance"/> 用）。</summary>
    public ScriptEvent(IEnumerable<ScriptEventBinding> bindings)
    {
        if (bindings is not null) _bindings.AddRange(bindings);
    }

    /// <summary>結線されている呼び出し先（読み取り専用）。</summary>
    public IReadOnlyList<ScriptEventBinding> Bindings => _bindings;

    /// <summary>結線数。0 なら <see cref="Invoke"/> は何もしない。</summary>
    public int Count => _bindings.Count;

    /// <summary>
    /// 結線された呼び出し先を先頭から順に 1 回ずつ呼ぶ。
    ///
    /// 1 件が失敗しても以降の呼び出しは続ける（1 本の結線ミスで
    /// 他の結線まで死ぬのを防ぐ）。個々の失敗の扱いは
    /// <see cref="ScriptEventBinding.Invoke"/> を参照。
    /// </summary>
    public void Invoke()
    {
        // 呼び出し先が自分自身の結線を書き換える可能性を考え、添字で走査する
        // （List の列挙中変更による例外を避ける）。
        for (int i = 0; i < _bindings.Count; i++) _bindings[i]?.Invoke();
    }

    /// <summary>結線を 1 件追加する（スクリプトからの動的追加・エディタからの編集用）。</summary>
    public void Add(ScriptEventBinding binding)
    {
        if (binding is not null) _bindings.Add(binding);
    }

    /// <summary>結線をすべて外す。</summary>
    public void Clear() => _bindings.Clear();

    /// <summary>このイベントを JSON 配列文字列へ書き出す。</summary>
    public string ToJson() => Encode(_bindings);

    // ─── 型判定 ───────────────────────────────────────────────

    /// <summary>
    /// フィールド／メンバの型が ScriptEvent（またはその派生）かを判定する。
    ///
    /// エディタ（インスペクタ UI）とランタイム（値の注入）が同じ判定を使うための正典。
    /// ScriptEvent は SEEDScripting アセンブリで定義される単一の型なので、
    /// ユーザースクリプト型をキャッシュする問題（ALC のアンロード阻害）は起きない。
    /// </summary>
    public static bool IsScriptEventType(Type? type)
        => type is not null && typeof(ScriptEvent).IsAssignableFrom(type);

    /// <summary>
    /// C# の型が固定引数として渡せる型かを判定し、対応する種別を返す（非対応なら null）。
    ///
    /// <see cref="ScriptEventArgKind"/> と 1 対 1 の表であり、
    /// ここに無い型（double / long / 列挙型 / 構造体など）は 1 引数メソッドの
    /// 候補にならない（0 引数メソッドとしてなら結線できる）。
    /// </summary>
    public static ScriptEventArgKind? IsSupportedArgType(Type? type)
    {
        if (type is null) return null;
        if (type == typeof(string))     return ScriptEventArgKind.String;
        if (type == typeof(float))      return ScriptEventArgKind.Float;
        if (type == typeof(int))        return ScriptEventArgKind.Int;
        if (type == typeof(bool))       return ScriptEventArgKind.Bool;
        if (type == typeof(GameObject)) return ScriptEventArgKind.GameObject;
        return null;
    }

    /// <summary>
    /// メソッドが指定の引数種別で呼び出せる形かを判定する（名前は見ない）。
    ///
    /// 【条件】
    /// - ジェネリックメソッド定義でないこと（型引数を決められないため）
    /// - 引数 0 個、または
    ///   <paramref name="argKind"/> が None 以外で、対応する型の引数がちょうど 1 個
    /// - ref / out 引数を含まないこと
    /// 戻り値の型は問わない（値は捨てる）。
    /// </summary>
    public static bool MethodMatches(MethodInfo? method, ScriptEventArgKind argKind)
    {
        if (method is null || method.IsGenericMethodDefinition) return false;

        var ps = method.GetParameters();
        if (ps.Length == 0) return true;                          // 0 引数はどの種別でも呼べる
        if (ps.Length != 1) return false;                         // 2 引数以上は非対応
        if (argKind == ScriptEventArgKind.None) return false;     // 引数なし指定に 1 引数は当てない

        var p = ps[0];
        if (p.ParameterType.IsByRef || p.IsOut) return false;
        return IsSupportedArgType(p.ParameterType) == argKind;
    }

    /// <summary>
    /// メソッド名まで含めて呼び出し候補かを判定する
    /// （<see cref="MethodMatches(MethodInfo, ScriptEventArgKind)"/> に名前一致を足したもの）。
    /// </summary>
    public static bool MethodMatches(MethodInfo? method, ScriptEventArgKind argKind, string methodName)
        => method is not null
        && !string.IsNullOrEmpty(methodName)
        && string.Equals(method.Name, methodName, StringComparison.Ordinal)
        && MethodMatches(method, argKind);

    // ─── 引数種別 ⇔ JSON 表記 ─────────────────────────────────

    /// <summary>引数種別を JSON 表記（小文字文字列）へ変換する。</summary>
    public static string ArgKindToJson(ScriptEventArgKind kind) => kind switch
    {
        ScriptEventArgKind.String     => "string",
        ScriptEventArgKind.Float      => "float",
        ScriptEventArgKind.Int        => "int",
        ScriptEventArgKind.Bool       => "bool",
        ScriptEventArgKind.GameObject => "gameobject",
        _                             => "none",
    };

    /// <summary>
    /// JSON 表記から引数種別へ変換する。
    /// 未知の表記・null は <see cref="ScriptEventArgKind.None"/> に落とす
    /// （新しい種別を知らない古いランタイムでも読み込みが壊れないようにするため）。
    /// 大文字小文字は無視する。
    /// </summary>
    public static ScriptEventArgKind ArgKindFromJson(string? text) => (text ?? "").ToLowerInvariant() switch
    {
        "string"     => ScriptEventArgKind.String,
        "float"      => ScriptEventArgKind.Float,
        "int"        => ScriptEventArgKind.Int,
        "bool"       => ScriptEventArgKind.Bool,
        "gameobject" => ScriptEventArgKind.GameObject,
        _            => ScriptEventArgKind.None,
    };

    // ─── デコード／エンコード ─────────────────────────────────

    /// <summary>
    /// JSON 配列文字列をバインディングの並びへ分解する。
    ///
    /// 【寛容デコードの規則】
    /// - 値が配列でない・壊れている → 空リスト（例外は投げない）
    /// - 配列内のオブジェクト以外の要素 → 読み飛ばす
    /// - 未知のキー → 無視（将来 "slot" などを足しても古い版で読める）
    /// - 欠損キー・型違いのキー → 既定値（空文字 / None）
    /// </summary>
    public static IReadOnlyList<ScriptEventBinding> Decode(string? json)
    {
        var result = new List<ScriptEventBinding>();
        if (string.IsNullOrWhiteSpace(json)) return result;

        try
        {
            using var doc = JsonDocument.Parse(json!);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return result;

            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;   // 壊れ要素はスキップ
                result.Add(new ScriptEventBinding
                {
                    Actor   = ReadText(item, KeyActor),
                    Script  = ReadText(item, KeyScript),
                    Method  = ReadText(item, KeyMethod),
                    ArgKind = ArgKindFromJson(ReadText(item, KeyArgKind)),
                    Arg     = ReadText(item, KeyArg),
                });
            }
        }
        catch (JsonException)
        {
            // 壊れた JSON は「結線なし」として扱う（実行を落とさない）
            result.Clear();
        }
        return result;
    }

    /// <summary>
    /// JSON オブジェクトから文字列プロパティを読む（欠損・型違い・null は空文字）。
    /// </summary>
    private static string ReadText(JsonElement obj, string key)
        => obj.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
            ? (v.GetString() ?? "")
            : "";

    /// <summary>
    /// バインディングの並びを JSON 配列文字列へ書き出す。
    /// キーは常に 5 個すべて出す（欠損キーの有無で差分が揺れないようにするため）。
    /// </summary>
    public static string Encode(IReadOnlyList<ScriptEventBinding>? bindings)
    {
        if (bindings is null || bindings.Count == 0) return EmptyJson;

        var sb = new StringBuilder("[");
        for (int i = 0; i < bindings.Count; i++)
        {
            if (i > 0) sb.Append(',');
            var b = bindings[i] ?? new ScriptEventBinding();
            sb.Append('{')
              .Append(ScriptArray.Quote(KeyActor)).Append(':').Append(ScriptArray.Quote(b.Actor  ?? "")).Append(',')
              .Append(ScriptArray.Quote(KeyScript)).Append(':').Append(ScriptArray.Quote(b.Script ?? "")).Append(',')
              .Append(ScriptArray.Quote(KeyMethod)).Append(':').Append(ScriptArray.Quote(b.Method ?? "")).Append(',')
              .Append(ScriptArray.Quote(KeyArgKind)).Append(':').Append(ScriptArray.Quote(ArgKindToJson(b.ArgKind))).Append(',')
              .Append(ScriptArray.Quote(KeyArg)).Append(':').Append(ScriptArray.Quote(b.Arg ?? ""))
              .Append('}');
        }
        sb.Append(']');
        return sb.ToString();
    }

    /// <summary>
    /// 任意の保存文字列を「正規形の JSON 配列文字列」へ整える。
    ///
    /// 壊れた JSON・配列でない値・空文字はすべて <see cref="EmptyJson"/> になる。
    /// 生の文字列をそのまま JSON へ埋め込む箇所（構造体配列のメンバなど）では、
    /// 必ずこれを通してから埋め込むこと。
    /// </summary>
    public static string Normalize(string? json) => Encode(Decode(json));

    /// <summary>
    /// JSON 配列文字列から <see cref="ScriptEvent"/> の実インスタンスを生成する。
    /// 値が壊れていても必ず非 null のインスタンスを返す（結線 0 件になるだけ）。
    /// </summary>
    public static ScriptEvent BuildInstance(string? json) => new ScriptEvent(Decode(json));
}
