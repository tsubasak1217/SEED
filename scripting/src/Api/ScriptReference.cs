using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

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

        // 参照ハンドルは必ずこのアセンブリの型。ユーザー型・BCL 型はここで弾く
        // （キャッシュにユーザー型を残さないための必須ガードでもある）。
        if (core.Assembly != HandleAssembly) return false;

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

        var entity = ResolveEntity(kind, value);

        // 解決できなかった: Nullable は null（未設定）、非 Nullable は無効ハンドル
        if (!entity.IsValid && kind.IsNullable) return null;

        return MakeHandle(kind.HandleType, entity);
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
