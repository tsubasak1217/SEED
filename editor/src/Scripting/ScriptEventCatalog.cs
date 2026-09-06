using System;
using System.Collections.Generic;
using System.Reflection;
using SEEDEditor.Controls;

namespace SEEDEditor.Scripting;

/// <summary>
/// ScriptEvent の結線先として選べる「メソッド 1 件」。
///
/// バインディング（<see cref="SEED.ScriptEventBinding"/>）が保存するのはメソッド名と
/// 引数種別の 2 つだけなので、候補もこの 2 つ組で表す。
/// 引数種別はメソッドのシグネチャから一意に決まる（ユーザーには選ばせない）ため、
/// 同名でも引数違いのオーバーロードは別候補として並ぶ。
/// </summary>
/// <param name="Name">メソッド名（<c>MethodInfo.Name</c>）。</param>
/// <param name="ArgKind">
/// このメソッドを呼ぶときの固定引数の種別。0 引数メソッドなら
/// <see cref="SEED.ScriptEventArgKind.None"/>。
/// </param>
public readonly record struct ScriptEventMethod(string Name, SEED.ScriptEventArgKind ArgKind)
{
    /// <summary>コンボボックスに出す表示文字列（例 <c>Begin(string)</c> / <c>Fire()</c>）。</summary>
    public string DisplayText => ArgKind == SEED.ScriptEventArgKind.None
        ? $"{Name}()"
        : $"{Name}({SEED.ScriptEvent.ArgKindToJson(ArgKind)})";
}

/// <summary>
/// ScriptEvent の結線先候補（アクタが持つスクリプト型／その呼び出せるメソッド）を列挙する。
///
/// 【責務】
/// WPF に一切依存しない「候補の算出」だけを持つ。IPC（GET_ACTOR_COMPONENTS）や
/// UI スレッドの都合は <see cref="IScriptEventCatalogProvider"/> の実装側
/// （InspectorPanel）が受け持ち、UI の組み立ては <see cref="ScriptEventFieldBuilder"/> が持つ。
/// この分離により、除外規則だけを単体テストから検証できる。
///
/// 【判定表を二重に持たないこと】
/// 「どの引数型が渡せるか」「どのメソッドが呼べるか」の判定は
/// <see cref="SEED.ScriptEvent.IsSupportedArgType"/> /
/// <see cref="SEED.ScriptEvent.MethodMatches(MethodInfo, SEED.ScriptEventArgKind)"/> が正典。
/// ランタイム（<see cref="SEED.ScriptEventBinding.Invoke"/>）と食い違うと
/// 「インスペクタで選べるのに実行時に呼ばれない」という無言の故障になるため、
/// エディタ側で同じ表をミラーしてはならない。
/// </summary>
internal static class ScriptEventCatalog
{
    /// <summary>ScriptComponent スロットを表す ACTOR_COMPONENTS の "type" 文字列。</summary>
    private const string ScriptComponentTypeId = ReferenceKindCatalog.ScriptComponentTypeId;

    /// <summary>候補メソッドの探索に使うリフレクションフラグ（public インスタンス・宣言型のみ）。</summary>
    private const BindingFlags MethodFlags =
        BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly;

    /// <summary>
    /// アクタが持つスクリプト型名の一覧を返す（宣言順・重複なし）。
    ///
    /// ScriptComponent スロットの .cs パスを <paramref name="compile"/> で型へ解決し、
    /// <c>Type.Name</c>（名前空間なし）を集める。これは
    /// <see cref="SEED.ScriptEventBinding.Script"/> に保存される値であり、
    /// ランタイムが実インスタンスの型名と突き合わせる文字列と同じ形である。
    /// </summary>
    /// <param name="snapshot">対象アクタの ACTOR_COMPONENTS スナップショット。</param>
    /// <param name="compile">
    /// .cs パス（<c>assets://</c> 仮想パスのこともある）→ コンパイル済み型 の解決関数。
    /// 仮想パスの絶対パス化（VirtualPath.ToAbsolute）とコンパイル結果のキャッシュは
    /// この関数の責務（エディタ側は InspectorPanel.GetOrCompileScript が担当する）。
    /// 解決できないスクリプトは候補から落とす（＝コンパイルエラー中のスクリプトは出さない）。
    /// </param>
    public static IReadOnlyList<string> ScriptTypesOnActor(
        ActorComponentSnapshot snapshot, Func<string, Type?> compile)
    {
        var result = new List<string>();
        if (snapshot is null) return result;

        foreach (var comp in snapshot.Components)
        {
            if (comp.TypeId != ScriptComponentTypeId) continue;
            if (string.IsNullOrEmpty(comp.ScriptPath)) continue;

            Type? type;
            // ユーザースクリプトのコンパイルは失敗し得る（構文エラー中など）。
            // 1 スロットの失敗で一覧全体を落とさない。
            try { type = compile(comp.ScriptPath); }
            catch (Exception) { type = null; }
            if (type is null) continue;

            // 同じスクリプトが複数スロットに付いていても候補は 1 つでよい
            // （バインディングは型名までしか保存しないため区別できない）。
            if (!result.Contains(type.Name)) result.Add(type.Name);
        }
        return result;
    }

    /// <summary>
    /// スクリプト型から「ScriptEvent の結線先にできるメソッド」を列挙する（宣言順・重複なし）。
    ///
    /// 【除外規則】
    /// - public インスタンスメソッド以外（static / private / protected）
    /// - <c>IsSpecialName</c>（プロパティ・イベント・演算子のアクセサ）
    /// - 基底クラス <see cref="SEEDScript"/> と <see cref="object"/> 由来のメソッド
    ///   （Start / Update / ToString などライフサイクル・BCL のメソッドを候補に出さない）
    /// - ジェネリックメソッド定義（型引数を決められない）
    /// - 引数が 2 個以上、または 1 個でも <see cref="SEED.ScriptEvent.IsSupportedArgType"/> が
    ///   非対応（double / 列挙型 / 構造体など）／ref・out のもの
    ///
    /// 中間のユーザー基底クラス（<c>class Boss : Enemy</c> の <c>Enemy</c>）で宣言された
    /// メソッドは候補に含める。ランタイム側の解決は継承メソッドも呼べるためで、
    /// ここで落とすと「呼べるのに一覧に出ない」ズレになる。
    /// </summary>
    public static IReadOnlyList<ScriptEventMethod> MethodsOf(Type? scriptType)
    {
        var result = new List<ScriptEventMethod>();
        if (scriptType is null) return result;

        // 派生クラス → 基底クラスの順に、SEEDScript / object の手前まで辿る。
        // 各段で DeclaredOnly を使うことで「どの型が宣言したか」を明示的に制御する。
        for (var t = scriptType; t is not null && !IsExcludedDeclaringType(t); t = t.BaseType)
        {
            foreach (var m in t.GetMethods(MethodFlags))
            {
                if (m.IsSpecialName) continue;                     // プロパティ／イベントのアクセサ
                if (m.IsGenericMethodDefinition) continue;          // 型引数を決められない
                if (IsLifecycleOverride(m)) continue;               // OnStart / Update などの override

                if (!TryGetArgKind(m, out var argKind)) continue;   // 引数の形が非対応

                var candidate = new ScriptEventMethod(m.Name, argKind);
                // 派生側で同じ形のメソッドを隠蔽（new）している場合、先に見た派生側を優先する
                if (!result.Contains(candidate)) result.Add(candidate);
            }
        }
        return result;
    }

    /// <summary>
    /// 候補から除外する「宣言型」か。ここに到達したら祖先の探索を打ち切る。
    /// SEEDScript より上（object を含む）はエンジン／BCL のメソッドしか無い。
    /// </summary>
    private static bool IsExcludedDeclaringType(Type t)
        => t == typeof(SEEDScript) || t == typeof(object);

    /// <summary>
    /// エンジンが呼ぶライフサイクルメソッドの override かを判定する。
    ///
    /// <c>public override void OnStart()</c> や
    /// <c>public override void OnCollisionEnter(GameObject other)</c> は
    /// 宣言型がユーザースクリプトになるため DeclaredOnly では除外できない。
    /// 仮想メソッドの「宣言の起点」（<see cref="MethodInfo.GetBaseDefinition"/>）が
    /// <see cref="SEEDScript"/> / <see cref="object"/> にあるものを弾くことで、
    /// ユーザーが新たに定義したメソッドだけを候補に残す。
    /// </summary>
    private static bool IsLifecycleOverride(MethodInfo method)
    {
        var baseDecl = method.GetBaseDefinition().DeclaringType;
        return baseDecl is not null
            && !ReferenceEquals(baseDecl, method.DeclaringType)
            && IsExcludedDeclaringType(baseDecl);
    }

    /// <summary>
    /// メソッドのシグネチャから固定引数の種別を決める（非対応なら false）。
    ///
    /// 種別はユーザーに選ばせず、必ずここでシグネチャから自動決定する。
    /// 判定の最終確認は <see cref="SEED.ScriptEvent.MethodMatches(MethodInfo, SEED.ScriptEventArgKind)"/>
    /// に委ね、ランタイムの呼び出し可否と一致することを保証する。
    /// </summary>
    private static bool TryGetArgKind(MethodInfo method, out SEED.ScriptEventArgKind argKind)
    {
        argKind = SEED.ScriptEventArgKind.None;

        var ps = method.GetParameters();
        if (ps.Length > 1) return false;

        if (ps.Length == 1)
        {
            var p = ps[0];
            if (p.ParameterType.IsByRef || p.IsOut) return false;
            var kind = SEED.ScriptEvent.IsSupportedArgType(p.ParameterType);
            if (kind is null) return false;
            argKind = kind.Value;
        }

        // 正典の判定でも呼べる形であることを確認する（表の二重管理を避ける最終関門）
        return SEED.ScriptEvent.MethodMatches(method, argKind);
    }
}
