using System;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;

namespace SEED;

/// <summary>
/// <see cref="ScriptEvent"/> に登録された「呼び出し先 1 件」。
///
/// Unity の UnityEvent の 1 行（オブジェクト＋コンポーネント＋メソッド＋引数）に相当する。
/// 参照はすべて**名前**で保持し、実体（Entity / CLR インスタンス / MethodInfo）は
/// 呼び出しのたびに解決する。名前で持つ理由は次の 2 つ。
/// - シーン／プレハブの保存形式が「フィールドパス → 1 本の文字列」で、実体を書けないため。
/// - スクリプトのホットリロードで CLR インスタンスが作り直されるため、
///   実体を握り込むと古いインスタンスを呼んでしまうため。
///
/// 【解決の順序】<see cref="Invoke"/> を参照。
///
/// 【キャッシュ方針】
/// 解決結果のうちキャッシュするのは <see cref="MethodInfo"/> と、その持ち主の <see cref="Type"/>
/// だけで、しかも**このバインディングのインスタンス内**に閉じる。
/// ユーザースクリプト型はアンロード可能な AssemblyLoadContext にロードされるため、
/// 静的辞書へ入れるとホットリロードで ALC がアンロードできなくなる
/// （ScriptReference.cs の同趣旨のコメントを参照）。
/// 対象インスタンスは毎回解決する（生成・破棄・リロードで入れ替わるため）。
/// </summary>
public sealed class ScriptEventBinding
{
    // ─── シリアライズされる内容（すべて名前ベース）─────────────

    /// <summary>呼び出し先アクターの名前。空なら「未設定」として黙って無視する。</summary>
    public string Actor { get; set; } = "";

    /// <summary>呼び出し先スクリプトの型名（<c>Type.Name</c>／名前空間なし）。</summary>
    public string Script { get; set; } = "";

    /// <summary>呼び出すメソッド名（public インスタンスメソッド）。</summary>
    public string Method { get; set; } = "";

    /// <summary>固定引数の種別。<see cref="ScriptEventArgKind.None"/> なら 0 引数メソッドを呼ぶ。</summary>
    public ScriptEventArgKind ArgKind { get; set; } = ScriptEventArgKind.None;

    /// <summary>固定引数の値（文字列表現）。<see cref="ArgKind"/> に従って実行時に変換する。</summary>
    public string Arg { get; set; } = "";

    // ─── 実行時のみのキャッシュ（シリアライズ対象外）───────────

    /// <summary>キャッシュ済み MethodInfo の持ち主の型（参照同一性で有効判定する）。</summary>
    private Type? _cachedTargetType;

    /// <summary>解決済みメソッド。<see cref="_cachedTargetType"/> と対で有効。</summary>
    private MethodInfo? _cachedMethod;

    /// <summary>解決失敗の警告を既に 1 回出したか（毎フレームのログ洪水を防ぐ）。</summary>
    private bool _warned;

    /// <summary>メソッド探索に使うリフレクションフラグ（public インスタンスメソッドのみ）。</summary>
    private const BindingFlags MethodFlags = BindingFlags.Public | BindingFlags.Instance;

    // ─── 構築 ─────────────────────────────────────────────────

    /// <summary>既定コンストラクタ（デコード・エディタからの生成に使う）。</summary>
    public ScriptEventBinding() { }

    /// <summary>全項目を指定して生成する。</summary>
    public ScriptEventBinding(string actor, string script, string method,
                              ScriptEventArgKind argKind, string arg)
    {
        Actor   = actor  ?? "";
        Script  = script ?? "";
        Method  = method ?? "";
        ArgKind = argKind;
        Arg     = arg    ?? "";
    }

    // ─── 呼び出し ─────────────────────────────────────────────

    /// <summary>
    /// このバインディングを 1 回実行する。
    ///
    /// 【解決手順】
    /// 1. <see cref="Actor"/> が空 → 未設定なので黙って何もしない。
    /// 2. ScriptHost.TryFindActor でアクター Entity を引く。
    /// 3. ScriptHost.TryResolveScriptInstance で生きている CLR インスタンスの GCHandle を得る。
    /// 4. GCHandle.FromIntPtr(...).Target で実体を取り出す。
    /// 5. 実体の Type.Name が <see cref="Script"/> と一致するか再確認する
    ///    （Rust 側の照合と二重化して、別スクリプトを呼ぶ事故を防ぐ）。
    /// 6. メソッドをリフレクションで解決する（キャッシュ有効ならそれを使う）。
    /// 7. 引数を組み立てて呼ぶ。
    ///
    /// 【失敗時の扱い】
    /// 2〜6 の失敗はこのバインディングにつき 1 回だけ警告を出す
    /// （アクターがまだ生成されていない・破棄済みといった一時的な状態でも
    ///   毎フレーム警告が出ると本当の問題が埋もれるため）。
    /// 7 のユーザーメソッド内で起きた例外は catch してエラーログにする
    /// （FFI 境界へ例外を投げると CLR ホストごと落ちるため）。
    /// </summary>
    public void Invoke()
    {
        // 1. 未設定行（インスペクタで行を足しただけの状態）は何もしない
        if (string.IsNullOrEmpty(Actor)) return;

        // 2. アクター名 → Entity
        if (!ScriptHost.TryFindActor(Actor, out var entity))
        {
            Warn($"アクター '{Actor}' が見つかりません（{Describe()}）");
            return;
        }

        // 3. Entity + 型名 → 生きている CLR インスタンスの GCHandle
        if (string.IsNullOrEmpty(Script) ||
            !ScriptHost.TryResolveScriptInstance(entity, Script, null, out var handle) || handle == 0)
        {
            Warn($"アクター '{Actor}' にスクリプト '{Script}' が見つかりません（{Describe()}）");
            return;
        }

        // 4. GCHandle → 実体（無効ハンドルは例外になり得るので握り潰す）
        object? instance;
        try { instance = GCHandle.FromIntPtr(handle).Target; }
        catch (Exception) { instance = null; }
        if (instance is null)
        {
            Warn($"スクリプト '{Script}' のインスタンスが既に破棄されています（{Describe()}）");
            return;
        }

        // 5. 型名の再確認（Rust 側の照合と二重化した安全弁）
        var targetType = instance.GetType();
        if (!string.Equals(targetType.Name, Script, StringComparison.Ordinal))
        {
            Warn($"解決したインスタンスの型 '{targetType.Name}' が指定 '{Script}' と一致しません（{Describe()}）");
            return;
        }

        // 6. メソッド解決
        var method = ResolveMethod(targetType);
        if (method is null)
        {
            Warn($"'{Script}' に呼び出せる public メソッド '{Method}' "
               + $"（引数 0 個 または {ArgKind} 1 個）がありません（{Describe()}）");
            return;
        }

        // 7. 引数を組み立てて呼ぶ
        object?[] args;
        try { args = BuildArguments(method); }
        catch (Exception ex)
        {
            Warn($"引数の変換に失敗しました: {ex.Message}（{Describe()}）");
            return;
        }

        try
        {
            method.Invoke(instance, args);
        }
        catch (TargetInvocationException tie)
        {
            // ユーザーメソッド内で起きた例外。FFI 境界へ出さずにログへ落とす。
            var inner = tie.InnerException ?? (Exception)tie;
            Debug.LogError($"ScriptEvent の呼び出し先で例外: {inner}（{Describe()}）");
        }
        catch (Exception ex)
        {
            Debug.LogError($"ScriptEvent の呼び出しに失敗: {ex}（{Describe()}）");
        }
    }

    // ─── 内部ヘルパー ─────────────────────────────────────────

    /// <summary>
    /// 呼び出し先メソッドを解決する（見つからなければ null）。
    ///
    /// 候補は「名前一致 かつ public インスタンス かつ 非ジェネリック」で、
    /// - <see cref="ArgKind"/> が None なら 0 引数メソッド
    /// - それ以外なら「対応する型の 1 引数メソッド」を優先し、無ければ 0 引数メソッドへ落とす
    ///   （引数を設定していても、受け手が引数を必要としない設計はあり得るため）。
    /// 解決できた場合だけ、持ち主の型と対でキャッシュする。
    /// </summary>
    private MethodInfo? ResolveMethod(Type targetType)
    {
        // キャッシュは「同一の型オブジェクト」に対してのみ有効。
        // ホットリロードで型が作り直されると参照が変わるので自動的に無効化される。
        if (ReferenceEquals(_cachedTargetType, targetType) && _cachedMethod is not null)
            return _cachedMethod;

        if (string.IsNullOrEmpty(Method)) return null;

        MethodInfo? zeroArg  = null;   // 0 引数の候補
        MethodInfo? argMatch = null;   // ArgKind に一致する 1 引数の候補

        foreach (var m in targetType.GetMethods(MethodFlags))
        {
            if (!ScriptEvent.MethodMatches(m, ArgKind, Method)) continue;
            if (m.GetParameters().Length == 0) zeroArg ??= m;
            else                               argMatch ??= m;
        }

        // 引数指定ありのときは 1 引数版を優先し、無ければ 0 引数版へ落とす
        var chosen = argMatch ?? zeroArg;
        if (chosen is null) return null;

        _cachedTargetType = targetType;
        _cachedMethod     = chosen;
        return chosen;
    }

    /// <summary>
    /// 解決済みメソッドへ渡す実引数を組み立てる。
    /// 0 引数メソッドなら空配列、1 引数なら <see cref="ArgKind"/> に応じた値 1 個。
    /// 数値の解釈に失敗した場合は 0（bool は false）に落とす。
    /// </summary>
    private object?[] BuildArguments(MethodInfo method)
    {
        if (method.GetParameters().Length == 0) return Array.Empty<object?>();

        var inv = CultureInfo.InvariantCulture;
        object? value = ArgKind switch
        {
            ScriptEventArgKind.String     => Arg,
            ScriptEventArgKind.Float      => float.TryParse(Arg, NumberStyles.Float, inv, out var f) ? f : 0f,
            ScriptEventArgKind.Int        => int.TryParse(Arg, NumberStyles.Integer, inv, out var i) ? i : 0,
            ScriptEventArgKind.Bool       => Arg == ScriptEvent.TrueText,
            ScriptEventArgKind.GameObject => GameObject.Find(Arg),
            _                             => null,
        };
        return new[] { value };
    }

    /// <summary>このバインディングにつき 1 回だけ警告を出す。</summary>
    private void Warn(string message)
    {
        if (_warned) return;
        _warned = true;
        Debug.LogWarning($"ScriptEvent: {message}");
    }

    /// <summary>ログ用の 1 行表現。</summary>
    public string Describe() => $"{Actor}/{Script}.{Method}({ArgKind}:{Arg})";
}
