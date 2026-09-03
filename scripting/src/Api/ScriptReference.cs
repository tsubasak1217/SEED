using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;

namespace SEED;

/// <summary>
/// [SerializeField] の「参照フィールド」（他アクター／他コンポーネントへのハンドル）に関する
/// 型判定・シリアライズ書式・解決処理をまとめた共通ヘルパー。
///
/// エディタ（インスペクタ UI の生成）とランタイム（スクリプトインスタンスへの注入）の
/// 両方から使われる唯一の正典であり、両者で判定がずれないようにここへ集約している。
///
/// 【参照フィールドとみなす型】
/// - <see cref="GameObject"/>（アクター本体への参照）
/// - <see cref="IComponentHandle{TSelf}"/> を実装する全ハンドル型
///   （Transform / CanvasTransform / Sprite / Camera / AudioSource /
///     Animator / ParticleEmitter / InputMap …。新しいハンドル型を追加すれば自動的に対象になる）
/// - 上記の Nullable 版（<c>Transform?</c> など）
///
/// 【シリアライズ書式】<see cref="Format"/> / <see cref="TryParse"/> を参照。
/// </summary>
public static class ScriptReference
{
    /// <summary>アクター名とスロット名を区切る文字（"Player|MainCamera"）。</summary>
    public const char SlotSeparator = '|';

    /// <summary><see cref="GameObject"/> 参照を表す種別名（コンポーネント種別名と衝突しない予約語）。</summary>
    public const string GameObjectKind = "GameObject";

    /// <summary>未設定を表すシリアライズ値（空文字列）。</summary>
    public const string UnsetValue = "";

    /// <summary>
    /// ユーザースクリプト参照（<c>[SerializeField] PlayerMove player;</c>）の種別名プレフィクス。
    ///
    /// 種別名は <c>"Script:" + 型名</c>（例 <c>"Script:PlayerMove"</c>）。
    /// コンポーネント種別名（"Transform" / "Camera" …）と衝突しないよう、
    /// C# の識別子に使えない ':' を区切りに用いている。
    /// エディタ（ReferenceKindCatalog）とランタイム（本ファイル）の双方がこの規約を使う。
    /// </summary>
    public const string ScriptKindPrefix = "Script:";

    /// <summary>種別名がユーザースクリプト参照か。</summary>
    public static bool IsScriptKind(string kind) => kind.StartsWith(ScriptKindPrefix, StringComparison.Ordinal);

    /// <summary>スクリプト参照の種別名から型名を取り出す（スクリプト参照でなければ null）。</summary>
    public static string? ScriptTypeNameOf(string kind)
        => IsScriptKind(kind) ? kind[ScriptKindPrefix.Length..] : null;

    // ─── 型判定 ───────────────────────────────────────────────

    /// <summary>
    /// 参照フィールド 1 件分の型情報。
    /// </summary>
    /// <param name="HandleType">Nullable を外した素のハンドル型（例 <c>SEED.Camera</c>）。</param>
    /// <param name="IsNullable">宣言が <c>T?</c>（Nullable&lt;T&gt;）だったか。null = 未設定を表す。</param>
    /// <param name="Kind">
    /// 種別名。GameObject 参照なら <see cref="GameObjectKind"/>、
    /// コンポーネント参照なら Rust 側解決キー（"Transform" / "Camera" …）。
    /// </param>
    public readonly record struct ReferenceKind(Type HandleType, bool IsNullable, string Kind)
    {
        /// <summary>アクター本体（GameObject）への参照か。</summary>
        public bool IsGameObject => Kind == GameObjectKind;
    }

    // ハンドル型（Nullable を外した素の型）→ 種別名のキャッシュ。
    //
    // 【重要】キーには「このアセンブリ（SEEDScripting）で定義された型」しか入れない。
    // ユーザースクリプトはアンロード可能な AssemblyLoadContext にロードされるため、
    // ユーザー型を静的辞書に保持するとホットリロード時に ALC がアンロードできなくなる。
    // TryGetKind の入口でアセンブリを判定し、他アセンブリの型はキャッシュせず即 false を返す。
    private static readonly Dictionary<Type, string?> KindNameByHandleType = new();

    // ハンドル型 → ハンドル生成デリゲートのキャッシュ（キーは同上の理由で SEEDScripting 型のみ）
    private static readonly Dictionary<Type, Func<Entity, object>> FactoryCache = new();

    /// <summary>参照フィールドになり得る型が定義されているアセンブリ（＝このアセンブリ）。</summary>
    private static readonly Assembly HandleAssembly = typeof(ScriptReference).Assembly;

    /// <summary>
    /// フィールド型が参照フィールドかを判定し、そうなら種別情報を返す。
    /// Nullable&lt;T&gt; は中身の T で判定し <see cref="ReferenceKind.IsNullable"/> を立てる。
    /// </summary>
    public static bool TryGetKind(Type fieldType, out ReferenceKind kind)
    {
        kind = default;

        // Nullable<T> なら中身の T で判定する（T? = 未設定を null で表せる宣言）
        var underlying = Nullable.GetUnderlyingType(fieldType);
        var isNullable = underlying is not null;
        var core       = underlying ?? fieldType;

        // 他アセンブリの型は「ユーザースクリプト型」だけを受け付ける。
        //
        // 【重要】ユーザー型は絶対にキャッシュへ入れない。アンロード可能な
        // AssemblyLoadContext にロードされるため、静的辞書に握るとホットリロードで
        // ALC がアンロードできなくなる（＝リーク）。判定は毎回リフレクションで行う。
        if (core.Assembly != HandleAssembly)
        {
            if (!IsUserScriptType(core)) return false;
            // 種別名は "Script:型名"。Type.Name（名前空間なし）は Rust 側が
            // .cs パスのファイル名語幹から作る型名と一致する。
            kind = new ReferenceKind(core, isNullable, ScriptKindPrefix + core.Name);
            return true;
        }

        string? kindName;
        lock (KindNameByHandleType)
        {
            if (!KindNameByHandleType.TryGetValue(core, out kindName))
            {
                kindName = ComputeKindName(core);
                KindNameByHandleType[core] = kindName;
            }
        }
        if (kindName is null) return false;

        kind = new ReferenceKind(core, isNullable, kindName);
        return true;
    }

    /// <summary>
    /// フィールド情報から参照種別を判定する（<see cref="TryGetKind(Type)"/> の高精度版）。
    ///
    /// 参照型（class）の <c>T?</c> は <see cref="Nullable{T}"/> にならず、
    /// 型情報だけでは Nullable かどうかを判別できない。そのため
    /// <see cref="NullabilityInfoContext"/> でフィールドの null 許容注釈を読み、
    /// <see cref="ReferenceKind.IsNullable"/> を補正する。
    ///
    /// null 許容コンテキストが無効なアセンブリでは注釈が出力されない
    /// （<see cref="NullabilityState.Unknown"/>）ため、その場合は
    /// 「未設定を null で表せる」= true として扱う
    /// （スクリプト参照は解決失敗時に必ず null になるため、これが実態と一致する）。
    /// </summary>
    public static bool TryGetKind(FieldInfo field, out ReferenceKind kind)
    {
        if (!TryGetKind(field.FieldType, out kind)) return false;

        // 値型ハンドル（Transform? など Nullable<T>）は型情報だけで確定しているので触らない
        if (kind.HandleType.IsValueType) return true;

        bool nullable;
        try
        {
            var info = new NullabilityInfoContext().Create(field);
            // Unknown（null 許容コンテキスト無効）は「null になり得る」側へ倒す
            nullable = info.ReadState != NullabilityState.NotNull;
        }
        catch
        {
            // 注釈を読めない環境では安全側（null になり得る）へ倒す
            nullable = true;
        }

        kind = kind with { IsNullable = nullable };
        return true;
    }

    /// <summary>
    /// 参照フィールドにできるユーザースクリプト型か。
    ///
    /// 条件は「<see cref="SEEDEditor.Scripting.IScriptComponent"/> を実装する、インスタンス化可能な class」。
    /// SEEDScript を継承したユーザースクリプトがすべて該当する。
    /// </summary>
    private static bool IsUserScriptType(Type core)
        => core.IsClass && !core.IsAbstract && !core.IsGenericTypeDefinition
           && typeof(SEEDEditor.Scripting.IScriptComponent).IsAssignableFrom(core);

    /// <summary>
    /// 素のハンドル型から種別名を算出する（キャッシュミス時のみ）。参照型でなければ null。
    /// </summary>
    private static string? ComputeKindName(Type core)
    {
        // GameObject 参照
        if (core == typeof(GameObject)) return GameObjectKind;

        // IComponentHandle<TSelf> を自分自身で実装するハンドル型
        var handleIface = core.GetInterfaces().FirstOrDefault(i =>
            i.IsGenericType &&
            i.GetGenericTypeDefinition() == typeof(IComponentHandle<>) &&
            i.GetGenericArguments()[0] == core);
        if (handleIface is null) return null;

        return InvokeStaticGeneric<string>(nameof(GetComponentKindName), core);
    }

    // ─── シリアライズ書式 ─────────────────────────────────────

    /// <summary>
    /// 参照値をシリアライズ文字列へ整形する。
    /// - アクター名のみ         → "Player"（GameObject / Transform / スロット 0 番目）
    /// - アクター名 + スロット名 → "Player|MainCamera"
    /// - 未設定                 → 空文字列
    /// </summary>
    public static string Format(string? actorName, string? slotName)
    {
        if (string.IsNullOrEmpty(actorName)) return UnsetValue;
        return string.IsNullOrEmpty(slotName)
            ? actorName!
            : actorName + SlotSeparator + slotName;
    }

    /// <summary>
    /// シリアライズ文字列をアクター名とスロット名へ分解する。未設定（空文字列）なら false。
    ///
    /// 区切りは<b>最初の</b> <see cref="SlotSeparator"/>。したがってアクター名に
    /// '|' を含めることはできない（スロット名側には含められる）。
    /// </summary>
    public static bool TryParse(string? value, out string actorName, out string? slotName)
    {
        actorName = "";
        slotName  = null;
        if (string.IsNullOrEmpty(value)) return false;

        var sep = value!.IndexOf(SlotSeparator);
        if (sep < 0)
        {
            actorName = value;
        }
        else
        {
            actorName = value[..sep];
            var s     = value[(sep + 1)..];
            slotName  = string.IsNullOrEmpty(s) ? null : s;
        }
        return !string.IsNullOrEmpty(actorName);
    }

    /// <summary>
    /// シリアライズ文字列をインスペクタ表示用の文字列へ整形する
    /// （"Player" / "Player / MainCamera"）。未設定なら null。
    /// </summary>
    public static string? FormatDisplay(string? value)
    {
        if (!TryParse(value, out var actor, out var slot)) return null;
        return slot is null ? actor : $"{actor} / {slot}";
    }

    // ─── 解決（シリアライズ値 → ハンドル）─────────────────────

    /// <summary>
    /// シリアライズ値を実際のハンドル（boxed）へ解決する。
    ///
    /// アクター名でアクターを検索し、コンポーネント参照ならさらにスロットを解決して
    /// ハンドルを生成する。<b>World / Actor ツリーが公開されている間</b>
    /// （スクリプトのライフサイクルフェーズ中）にのみ成功する。
    ///
    /// 解決できなかった場合の戻り値:
    /// - <c>T?</c>（Nullable）宣言 … null（＝未設定）
    /// - <c>T</c> 宣言            … 無効ハンドル（<c>IsValid == false</c>）
    ///
    /// 対象が参照フィールドでない場合は null を返す（呼び出し側で判定済みの想定）。
    /// </summary>
    public static object? Resolve(Type fieldType, string? value)
    {
        if (!TryGetKind(fieldType, out var kind)) return null;

        // ── ユーザースクリプト参照: 実インスタンス（class）をそのまま返す ──
        // ハンドル構造体と違い「無効な値」を表現できないため、解決できなければ
        // T / T? のどちらの宣言でも null になる（利用側は必ず null チェックする）。
        if (IsScriptKind(kind.Kind)) return ResolveScriptInstance(kind, value);

        var entity = ResolveEntity(kind, value);

        // 解決できなかった: Nullable は null（未設定）、非 Nullable は無効ハンドル
        if (!entity.IsValid && kind.IsNullable) return null;

        return MakeHandle(kind.HandleType, entity);
    }

    /// <summary>
    /// スクリプト参照のシリアライズ値から、参照先アクターに載っている
    /// スクリプトの<b>生きた CLR インスタンス</b>を解決する。
    ///
    /// Rust ランタイムが ScriptComponent の GCHandle 値を返し、それを
    /// <see cref="GCHandle.FromIntPtr"/> で実体へ戻す（＝ホットリロード後も
    /// 常に「今の」インスタンスが得られる。呼び出し側でキャッシュしてはならない）。
    ///
    /// 解決できない（アクター不在・スクリプト未アタッチ・World 非公開）場合は null。
    /// </summary>
    private static object? ResolveScriptInstance(ReferenceKind kind, string? value)
    {
        var typeName = ScriptTypeNameOf(kind.Kind);
        if (typeName is null) return null;
        if (!TryParse(value, out var actorName, out var slotName)) return null;
        if (!ScriptHost.TryFindActor(actorName, out var actor)) return null;
        if (!ScriptHost.TryResolveScriptInstance(actor, typeName, slotName, out var handle)) return null;

        object? instance;
        try { instance = GCHandle.FromIntPtr(handle).Target; }
        catch { return null; }

        // 同名クラスが別名前空間に存在する場合など、型が食い違ったら未解決扱いにする
        // （フィールドへ代入すると ArgumentException になるため、ここで弾く）。
        return kind.HandleType.IsInstanceOfType(instance) ? instance : null;
    }

    /// <summary>
    /// シリアライズ値から、ハンドルが指すべき entity を解決する。
    /// GameObject / ルート直付け型（Transform 等）はアクターのルート entity、
    /// スロット格納型はスロット entity。解決失敗時は <see cref="Entity.None"/>。
    /// </summary>
    private static Entity ResolveEntity(ReferenceKind kind, string? value)
    {
        if (!TryParse(value, out var actorName, out var slotName)) return Entity.None;
        if (!ScriptHost.TryFindActor(actorName, out var actor))    return Entity.None;

        // GameObject 参照はアクターのルート entity をそのまま使う
        if (kind.IsGameObject) return actor;

        // コンポーネント参照はスロットを解決する
        //  - スロット名指定あり … 名前一致
        //  - 指定なし           … その種別の 0 番目
        //  （Transform / CanvasTransform はルート直付けのため index / name は無視される）
        return ScriptHost.TryResolveComponentSlot(
                   actor, kind.Kind, slotName, slotName is null ? 0 : -1, out var slot)
            ? slot
            : Entity.None;
    }

    /// <summary>指定ハンドル型のインスタンスを entity から生成する（boxed）。</summary>
    private static object MakeHandle(Type handleType, Entity entity)
    {
        if (handleType == typeof(GameObject)) return new GameObject(entity);

        Func<Entity, object>? factory;
        lock (FactoryCache)
        {
            if (!FactoryCache.TryGetValue(handleType, out factory))
            {
                factory = BuildFactory(handleType);
                FactoryCache[handleType] = factory;
            }
        }
        return factory(entity);
    }

    /// <summary>
    /// ハンドル型 T の <c>T.FromEntity</c>（static abstract）を呼ぶデリゲートを構築する。
    /// static abstract インターフェースメンバは型引数経由でしか呼べないため、
    /// ジェネリックヘルパーを <see cref="MethodInfo.MakeGenericMethod"/> で具体化する。
    /// </summary>
    private static Func<Entity, object> BuildFactory(Type handleType)
    {
        var method = typeof(ScriptReference)
            .GetMethod(nameof(CreateHandle), BindingFlags.NonPublic | BindingFlags.Static)!
            .MakeGenericMethod(handleType);
        return (Func<Entity, object>)Delegate.CreateDelegate(typeof(Func<Entity, object>), method);
    }

    /// <summary>ハンドル型 T を entity から生成する（BuildFactory から具体化して使う）。</summary>
    private static object CreateHandle<T>(Entity entity) where T : struct, IComponentHandle<T>
        => T.FromEntity(entity);

    /// <summary>ハンドル型 T のコンポーネント種別名を返す（ComputeKind から具体化して使う）。</summary>
    private static string GetComponentKindName<T>() where T : struct, IComponentHandle<T>
        => T.ComponentKindName;

    /// <summary>
    /// 引数なしのジェネリック静的ヘルパーを型引数 <paramref name="typeArg"/> で具体化して呼ぶ。
    /// static abstract メンバの読み出し（ComponentKindName）に使う。
    /// </summary>
    private static TResult? InvokeStaticGeneric<TResult>(string methodName, Type typeArg)
        where TResult : class
    {
        try
        {
            var method = typeof(ScriptReference)
                .GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Static)!
                .MakeGenericMethod(typeArg);
            return method.Invoke(null, null) as TResult;
        }
        catch
        {
            // 型引数制約を満たさない等で具体化できない場合は「参照フィールドでない」扱いにする
            return null;
        }
    }
}
