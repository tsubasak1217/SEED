using System;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace SEEDEditor.Scripting;

/// <summary>
/// Rust から呼ばれるアンマネージドエントリポイント群。
/// すべて cdecl 呼び出し規約・静的メソッド。
/// GCHandle を isize として返すことでマネージドオブジェクトの寿命を管理する。
/// </summary>
public static unsafe class ScriptBridge
{
    // ─── ホスト API 登録 ──────────────────────────────────────

    /// <summary>
    /// Rust ランタイムがコンポーネントアクセス用の関数ポインタ表を登録する。
    /// 起動時に一度だけ呼ばれ、以降 Transform 等のアクセスがこの表を通る。
    /// </summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static void RegisterHostApi(SEED.ScriptHostApi* api)
        => SEED.ScriptHost.Register(api);

    // ─── インスタンス生成・破棄 ────────────────────────────────

    /// <summary>
    /// 型名（UTF-8）でスクリプトコンポーネントを生成し GCHandle を返す。
    /// 型名例: "SEEDEditor.Scripting.MyScript" または単純に "MyScript"
    /// </summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static nint CreateComponent(byte* typeNamePtr, int typeNameLen)
    {
        try
        {
            var typeName = Encoding.UTF8.GetString(typeNamePtr, typeNameLen);
            var type     = FindType(typeName)
                ?? throw new TypeLoadException($"Script type not found: '{typeName}'");
            var instance = (IScriptComponent)(Activator.CreateInstance(type)
                ?? throw new InvalidOperationException($"Cannot instantiate: '{typeName}'"));
            return GCHandle.ToIntPtr(GCHandle.Alloc(instance));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[SEEDScripting] CreateComponent failed: {ex}");
            return 0;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static void DestroyComponent(nint handlePtr)
    {
        if (handlePtr == 0) return;
        // 例外抑制テーブルに残った当該ハンドルのエントリを掃除する
        // （インスタンス破棄後もカウンタが残り続けるのを防ぐ）。
        ForgetErrorState(handlePtr);
        GCHandle.FromIntPtr(handlePtr).Free();
    }

    // ─── 生成・破棄コールバック ───────────────────────────────
    // フレームコンテキストを持たない 1 回限りの通知。
    // 引数は (ハンドル, 所有エンティティ index, 同 generation)。
    // Rust 側 InstanceEventFn（scripting/mod.rs）とシグネチャを一致させること。

    /// <summary>
    /// スクリプトの初回ライフサイクル（BeginFrame）直前に 1 回だけ呼ばれる OnStart。
    /// ScriptSystem（Rust）が BeginFrame フェーズでスクリプトごとに発行する。
    /// </summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static void OnStart(nint h, uint entityIndex, uint entityGeneration)
    {
        try
        {
            if (Get(h) is not SEEDScript ss) return;
            ss.BindEntity(entityIndex, entityGeneration);
            ss.OnStart();
        }
        catch (Exception ex)
        {
            // FFI 境界を例外が越えると CLR がプロセスを落とすため、必ずここで握り潰す。
            ReportScriptException(h, ScriptCallback.OnStart, ex);
        }
    }

    /// <summary>
    /// スクリプトインスタンス破棄の直前に 1 回だけ呼ばれる OnDestroy。
    /// Rust 側 ScriptComponent の Drop が（OnStart 済みの場合のみ）発行する。
    /// この呼び出しの直後に DestroyComponent で GCHandle が解放される。
    /// </summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static void OnDestroy(nint h, uint entityIndex, uint entityGeneration)
    {
        try
        {
            if (Get(h) is not SEEDScript ss) return;
            ss.BindEntity(entityIndex, entityGeneration);
            ss.OnDestroy();
        }
        catch (Exception ex)
        {
            // FFI 境界を例外が越えると CLR がプロセスを落とすため、必ずここで握り潰す。
            ReportScriptException(h, ScriptCallback.OnDestroy, ex);
        }
    }

    // ─── ライフサイクル ───────────────────────────────────────
    // 各フェーズの実行直前に、現在フレームの時間（SEED.Time）と所有エンティティ
    // （SEEDScript.gameObject/transform 用）をスクリプトへ束縛する（Prepare）。

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static void BeginFrame(nint h, NativeFrameContext* ctx)
    { InvokePhase(h, ctx, ScriptCallback.BeginFrame); }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static void EarlyUpdate(nint h, NativeFrameContext* ctx)
    { InvokePhase(h, ctx, ScriptCallback.EarlyUpdate); }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static void Update(nint h, NativeFrameContext* ctx)
    { InvokePhase(h, ctx, ScriptCallback.Update); }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static void ConstantUpdate(nint h, NativeFrameContext* ctx)
    { InvokePhase(h, ctx, ScriptCallback.ConstantUpdate); }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static void LateUpdate(nint h, NativeFrameContext* ctx)
    { InvokePhase(h, ctx, ScriptCallback.LateUpdate); }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static void Render(nint h, NativeFrameContext* ctx)
    { InvokePhase(h, ctx, ScriptCallback.Render); }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static void EndFrame(nint h, NativeFrameContext* ctx)
    { InvokePhase(h, ctx, ScriptCallback.EndFrame); }

    /// <summary>
    /// フレームフェーズ呼び出しの共通実装。
    /// Prepare（時間同期・エンティティ束縛）と該当フェーズの実行をまとめて
    /// try/catch で保護する。ユーザースクリプトが例外を投げても FFI 境界を
    /// 越えさせず、ログを出してそのフレームの当該呼び出しだけを中断する。
    /// </summary>
    private static void InvokePhase(nint h, NativeFrameContext* ctx, ScriptCallback phase)
    {
        try
        {
            var s = Prepare(h, ctx);
            if (s is null) return;
            switch (phase)
            {
                case ScriptCallback.BeginFrame:     s.BeginFrame(ref *ctx);     break;
                case ScriptCallback.EarlyUpdate:    s.EarlyUpdate(ref *ctx);    break;
                case ScriptCallback.Update:         s.Update(ref *ctx);         break;
                case ScriptCallback.ConstantUpdate: s.ConstantUpdate(ref *ctx); break;
                case ScriptCallback.LateUpdate:     s.LateUpdate(ref *ctx);     break;
                case ScriptCallback.Render:         s.Render(ref *ctx);         break;
                case ScriptCallback.EndFrame:       s.EndFrame(ref *ctx);       break;
            }
        }
        catch (Exception ex)
        {
            // FFI 境界を例外が越えると CLR がプロセスを落とすため、必ずここで握り潰す。
            ReportScriptException(h, phase, ex);
        }
    }

    /// <summary>
    /// フェーズ実行の直前準備。現在フレームの時間を SEED.Time へ同期し、
    /// SEEDScript には所有エンティティを束縛する。対象インスタンスを返す。
    /// </summary>
    private static IScriptComponent? Prepare(nint h, NativeFrameContext* ctx)
    {
        SEED.Time.Sync(ctx->DeltaTime, ctx->AnimTime);
        var s = Get(h);
        if (s is SEEDScript ss) ss.BindEntity(ctx->EntityIndex, ctx->EntityGeneration);
        return s;
    }

    // ─── 物理イベント ─────────────────────────────────────────

    // イベント種別（Rust 側 scripting/mod.rs の PHYSICS_EVENT_* 定数と一致させる）
    private const int PhysicsEventCollisionEnter = 0;
    private const int PhysicsEventCollisionStay  = 1;
    private const int PhysicsEventCollisionExit  = 2;
    private const int PhysicsEventTriggerEnter   = 3;
    private const int PhysicsEventTriggerExit    = 4;
    private const int PhysicsEventTriggerStay    = 5;

    /// <summary>
    /// 物理イベント（衝突・トリガー）をスクリプトへ通知する。
    /// Rust の update_physics がイベント受信時に呼ぶ。
    /// 自エンティティを束縛してから、種別に応じたコールバックを呼び出す。
    /// </summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static void OnPhysicsEvent(nint h, NativePhysicsEvent* ev)
    {
        try
        {
            if (Get(h) is not SEEDScript ss) return;
            ss.BindEntity(ev->SelfIndex, ev->SelfGeneration);
            var other = new SEED.GameObject(new SEED.Entity(ev->OtherIndex, ev->OtherGeneration));
            switch (ev->Kind)
            {
                case PhysicsEventCollisionEnter: ss.OnCollisionEnter(other); break;
                case PhysicsEventCollisionStay:  ss.OnCollisionStay(other);  break;
                case PhysicsEventCollisionExit:  ss.OnCollisionExit(other);  break;
                case PhysicsEventTriggerEnter:   ss.OnTriggerEnter(other);   break;
                case PhysicsEventTriggerExit:    ss.OnTriggerExit(other);    break;
                case PhysicsEventTriggerStay:    ss.OnTriggerStay(other);    break;
            }
        }
        catch (Exception ex)
        {
            // FFI 境界を例外が越えると CLR がプロセスを落とすため、必ずここで握り潰す。
            ReportScriptException(h, ScriptCallback.OnPhysicsEvent, ex);
        }
    }

    // ─── スクリプトコンパイル ─────────────────────────────────

    /// <summary>
    /// アセットルート配下の全 .cs をコンパイルして collectible ALC にロードする。
    /// 再呼び出しで旧アセンブリはアンロードされる（ホットリロード）。
    /// 呼び出し前に既存インスタンスをすべて DestroyComponent しておくこと。
    /// 戻り値: コンパイルされたスクリプト型数（-1 はコンパイル失敗）。
    /// </summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static int CompileScripts(byte* rootPtr, int rootLen)
    {
        try
        {
            // ホットリロードで全インスタンスが作り直されるため、
            // 旧インスタンスに紐づく例外抑制状態はここで全消去する。
            ClearAllErrorState();
            var root = Encoding.UTF8.GetString(rootPtr, rootLen);
            return ScriptAssemblyManager.CompileAndLoad(root);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[SEEDScripting] CompileScripts failed: {ex}");
            return -1;
        }
    }

    // ─── フィールド設定 ───────────────────────────────────────

    /// <summary>
    /// [SerializeField] フィールドに文字列値を設定する（リフレクション）。
    /// 対応型: float / double / int / long / short / bool / string
    ///
    /// name にドット区切りパス（例 "stats.hp"）を渡すと、[Serializable] な
    /// ネストクラスのフィールドへ再帰的に設定する。途中のネストオブジェクトが
    /// null の場合は自動生成する。
    /// </summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static void SetFieldValue(nint h, byte* namePtr, int nameLen, byte* valPtr, int valLen)
    {
        try
        {
            var target = Get(h);
            if (target is null) return;

            var name  = Encoding.UTF8.GetString(namePtr, nameLen);
            var value = Encoding.UTF8.GetString(valPtr, valLen);

            SetFieldByPath(target, name, value);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[SEEDScripting] SetFieldValue failed: {ex.Message}");
        }
    }

    // ─── スクリプト例外の捕捉とログ抑制 ───────────────────────
    //
    // 【背景】
    // ScriptBridge の各メソッドは [UnmanagedCallersOnly] で Rust から直接呼ばれる
    // FFI エントリポイントである。ここから例外が抜けると CLR は「Unhandled
    // exception」としてプロセス全体を即座に終了させる（exitCode 0xC0000409 等）。
    // ユーザースクリプトの単純なミス（Nullable の .Value など）でランタイムが
    // 落ちるのを防ぐため、すべてのコールバックを try/catch で包み、
    // ログを出したうえでゲームの実行は継続する。
    //
    // 【ログ抑制】
    // 毎フレーム呼ばれるフェーズで例外が出続けると、全文スタックの出力だけで
    // ログ・stderr パイプ・フレーム時間が破綻する。そこで
    // （インスタンス × コールバック種別）ごとに発生回数を数え、
    //   ・初回          … 定型 1 行 + 全文スタックトレース
    //   ・2 回目以降    … ScriptErrorRepeatLogInterval 回ごとに 1 行サマリのみ
    // とする。正常フレームでは一切テーブルに触れないため、
    // 例外が起きていない限り追加コストはゼロである。

    /// <summary>スクリプト呼び出し種別（ログの「メソッド名」として使う）。</summary>
    private enum ScriptCallback
    {
        BeginFrame,
        EarlyUpdate,
        Update,
        ConstantUpdate,
        LateUpdate,
        Render,
        EndFrame,
        OnStart,
        OnDestroy,
        OnPhysicsEvent,
    }

    /// <summary>
    /// 同一インスタンス × 同一コールバックで例外が繰り返された場合に、
    /// 何回に 1 回サマリ行を出すかの間隔。60fps 換算で約 5 秒に 1 行。
    /// </summary>
    private const int ScriptErrorRepeatLogInterval = 300;

    /// <summary>ログ 1 行目の定型プレフィクス（エディタ側の検索キーを兼ねる）。</summary>
    private const string ScriptErrorPrefix = "[SCRIPT ERROR]";

    /// <summary>型名が解決できなかった場合に使う代替表記。</summary>
    private const string UnknownScriptTypeName = "<unknown script>";

    /// <summary>
    /// （GCHandle 値, コールバック種別）ごとの累計例外回数。
    /// インスタンス破棄（DestroyComponent）とホットリロード（CompileScripts）で掃除する。
    /// </summary>
    private static readonly System.Collections.Generic.Dictionary<(nint Handle, ScriptCallback Callback), int>
        ScriptErrorCounts = new();

    /// <summary>ScriptErrorCounts の排他用ロック（例外発生時のみ取得する）。</summary>
    private static readonly object ScriptErrorLock = new();

    /// <summary>掃除時の列挙に使うコールバック種別の一覧（毎回の配列生成を避けるためキャッシュ）。</summary>
    private static readonly ScriptCallback[] AllScriptCallbacks = Enum.GetValues<ScriptCallback>();

    /// <summary>
    /// スクリプト由来の例外を stderr へ報告する（ランタイム経由でエディタログに載る）。
    /// 初回は定型 1 行 + 全文スタック、以降は一定間隔でサマリ 1 行のみを出力する。
    /// この関数自体は決して例外を送出しない（FFI 境界へ抜けさせないため）。
    /// </summary>
    private static void ReportScriptException(nint h, ScriptCallback callback, Exception ex)
    {
        try
        {
            // 累計回数を更新する（例外発生時にしか呼ばれないため、ここでのロックは実害がない）
            int count;
            lock (ScriptErrorLock)
            {
                var key = (h, callback);
                count = ScriptErrorCounts.TryGetValue(key, out var prev) ? prev + 1 : 1;
                ScriptErrorCounts[key] = count;
            }

            var typeName = SafeTypeName(h);

            if (count == 1)
            {
                // 初回のみ全文を出す。1 行目はエディタログから拾いやすい定型フォーマット。
                Console.Error.WriteLine($"{ScriptErrorPrefix} {typeName}.{callback}: {ex.Message}");
                Console.Error.WriteLine(ex.ToString());
                Console.Error.WriteLine(
                    $"{ScriptErrorPrefix} 以降この例外は {ScriptErrorRepeatLogInterval} 回ごとに 1 行のみ通知します。");
            }
            else if (count % ScriptErrorRepeatLogInterval == 0)
            {
                // 繰り返し発生分はサマリ 1 行のみ（スタックは初回ログを参照させる）
                Console.Error.WriteLine(
                    $"{ScriptErrorPrefix} {typeName}.{callback}: 例外が継続中（累計 {count} 回・詳細は初回ログ参照）: {ex.Message}");
            }
        }
        catch
        {
            // ログ出力自体の失敗（stderr 切断等）でプロセスを落とさない。
            // ここで握り潰す以外に安全な選択肢はないため、意図的に無処理とする。
        }
    }

    /// <summary>
    /// GCHandle からスクリプトの型名を安全に取得する。
    /// 解決できない場合（ハンドル無効・解放済み等）は代替表記を返し、例外は投げない。
    /// </summary>
    private static string SafeTypeName(nint h)
    {
        try
        {
            return Get(h)?.GetType().FullName ?? UnknownScriptTypeName;
        }
        catch
        {
            return UnknownScriptTypeName;
        }
    }

    /// <summary>指定ハンドルに紐づく例外抑制状態をすべて破棄する。</summary>
    private static void ForgetErrorState(nint h)
    {
        lock (ScriptErrorLock)
        {
            // 対象ハンドルのエントリのみ列挙して削除する（コールバック種別ごとに最大 1 件）
            foreach (var callback in AllScriptCallbacks)
                ScriptErrorCounts.Remove((h, callback));
        }
    }

    /// <summary>例外抑制状態を全消去する（ホットリロード時）。</summary>
    private static void ClearAllErrorState()
    {
        lock (ScriptErrorLock) ScriptErrorCounts.Clear();
    }

    // ─── 内部ヘルパー ─────────────────────────────────────────

    private const System.Reflection.BindingFlags FieldFlags =
        System.Reflection.BindingFlags.Public |
        System.Reflection.BindingFlags.NonPublic |
        System.Reflection.BindingFlags.Instance;

    /// <summary>
    /// ドット区切りパスをたどってフィールドへ値を設定する。
    /// 末端以外はネストオブジェクトを解決し（null なら生成し）、
    /// 末端フィールドで型変換して値を書き込む。
    /// </summary>
    private static void SetFieldByPath(object root, string path, string value)
    {
        var segments = path.Split('.');
        object current = root;

        // 末端の 1 つ手前までネストオブジェクトをたどる（必要なら生成する）
        for (int i = 0; i < segments.Length - 1; i++)
        {
            var f = current.GetType().GetField(segments[i], FieldFlags);
            if (f is null)
            {
                Console.Error.WriteLine($"[SEEDScripting] nested field not found: {current.GetType().Name}.{segments[i]}");
                return;
            }
            var child = f.GetValue(current);
            if (child is null)
            {
                // ネストオブジェクトが未生成なら生成して親へ設定する
                child = Activator.CreateInstance(f.FieldType);
                if (child is null)
                {
                    Console.Error.WriteLine($"[SEEDScripting] cannot instantiate nested type: {f.FieldType.Name}");
                    return;
                }
                f.SetValue(current, child);
            }
            current = child;
        }

        // 末端フィールドへ変換値を設定する
        var leafName = segments[^1];
        var leaf = current.GetType().GetField(leafName, FieldFlags);
        if (leaf is null)
        {
            Console.Error.WriteLine($"[SEEDScripting] field not found: {current.GetType().Name}.{leafName}");
            return;
        }

        var converted = ConvertValue(leaf.FieldType, value);
        if (converted is null)
        {
            Console.Error.WriteLine($"[SEEDScripting] unsupported field type: {leaf.FieldType.Name} ({leafName})");
            return;
        }
        leaf.SetValue(current, converted);
    }

    /// <summary>文字列値を対象フィールド型へ変換する（未対応型は null）。</summary>
    private static object? ConvertValue(Type type, string value)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        return type switch
        {
            var t when t == typeof(float)  => float.Parse(value, inv),
            var t when t == typeof(double) => double.Parse(value, inv),
            var t when t == typeof(int)    => int.Parse(value, inv),
            var t when t == typeof(long)   => long.Parse(value, inv),
            var t when t == typeof(short)  => short.Parse(value, inv),
            var t when t == typeof(bool)   => value == "true",
            var t when t == typeof(string) => value,
            _ => null,
        };
    }

    private static IScriptComponent? Get(nint h)
        => h == 0 ? null : (IScriptComponent?)GCHandle.FromIntPtr(h).Target;

    /// <summary>
    /// 型名または .cs ファイルパスからスクリプト型を検索する。
    /// ユーザースクリプトアセンブリ（ScriptAssemblyManager）を優先する。
    /// </summary>
    private static Type? FindType(string name)
        => ScriptAssemblyManager.Resolve(name);
}
