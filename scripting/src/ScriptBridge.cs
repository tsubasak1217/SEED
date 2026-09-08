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
            // ScriptEvent フィールドは「未設定でも null にならない」契約なので、
            // 値の注入前にここで実体を用意しておく（後述 EnsureScriptEventInstances 参照）。
            EnsureScriptEventInstances(instance, type, 0);
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
        // 未解決のまま残った参照フィールドの保留エントリも掃除する
        ForgetPendingReferences(handlePtr);
        // this.On で張った名前付きイベント購読を解除する。
        // OnDestroy が呼ばれない破棄経路（OnStart 前の破棄など）でも、
        // 静的な購読テーブルへ死んだインスタンスのデリゲートが残らないようにする。
        if (Get(handlePtr) is SEEDScript script) script.UnsubscribeAllEvents();
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
            try
            {
                ss.OnDestroy();
            }
            finally
            {
                // this.On で張った名前付きイベント購読をここで自動解除する
                // （ユーザーの OnDestroy が例外で落ちても必ず解除されるよう finally に置く）。
                ss.UnsubscribeAllEvents();
            }
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
    {
        DispatchDebugCommandsOncePerFrame(ctx);
        InvokePhase(h, ctx, ScriptCallback.BeginFrame);
    }

    /// <summary>
    /// このフレームでまだ配っていなければ、外部から届いたデバッグコマンド
    /// （<c>SCRIPT_DEBUG</c> IPC）を <see cref="SEED.Debug"/> のハンドラへ配る
    /// 【外部指示がゲームへ入る唯一のタイミング】。
    ///
    /// <c>BeginFrame</c> は<b>スクリプトの数だけ</b>呼ばれるので、
    /// フレームの実時間（<c>UnscaledElapsedTime</c>）が前回と変わったときだけ動かし、
    /// 1 フレームにつき 1 回に絞る。
    ///
    /// <b>注意</b>: 同じフレームの <c>OnStart</c> で登録したハンドラは、
    /// 登録がこの呼び出しより後になると 1 件取りこぼすことがある
    /// （ランタイムは Play 開始時に待ち行列を空にするので、通常の
    /// 「OnStart で登録 → あとからコマンドを送る」使い方では起きない）。
    /// </summary>
    private static void DispatchDebugCommandsOncePerFrame(NativeFrameContext* ctx)
    {
        float now = ctx->UnscaledElapsedTime;
        if (now == _lastDebugDispatchTime) { return; }
        _lastDebugDispatchTime = now;

        try { SEED.Debug.DispatchPendingCommands(); }
        catch (Exception ex)
        {
            // FFI 境界を例外が越えると CLR がプロセスを落とすため、必ずここで握り潰す。
            Console.Error.WriteLine($"[SEEDScripting] デバッグコマンドの配信で例外: {ex}");
        }
    }

    /// <summary>デバッグコマンドを最後に配ったフレームの実時間（同一フレームの二重配信よけ）。</summary>
    private static float _lastDebugDispatchTime = float.NaN;

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
        SEED.Time.Sync(ctx->DeltaTime, ctx->AnimTime, ctx->UnscaledDeltaTime, ctx->UnscaledElapsedTime);
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

    // ポインタイベント種別（Rust 側 scripting/mod.rs の POINTER_EVENT_* 定数と一致させる）。
    // 物理イベントと同じ FFI 経路に相乗りしており、other は常に Entity.None。
    private const int PointerEventEnter = 6;
    private const int PointerEventExit  = 7;
    private const int PointerEventDown  = 8;
    private const int PointerEventUp    = 9;
    private const int PointerEventClick = 10;

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
                // ── キャンバス UI のポインタイベント（相手アクターの概念は無い）──
                case PointerEventEnter:          ss.OnPointerEnter();        break;
                case PointerEventExit:           ss.OnPointerExit();         break;
                case PointerEventDown:           ss.OnPointerDown();         break;
                case PointerEventUp:             ss.OnPointerUp();           break;
                case PointerEventClick:          ss.OnPointerClick();        break;
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
            // 名前付きイベント（SEED.Events）の購読も全消去する。
            // 購読テーブルは静的なので、旧アセンブリのメソッドを指すデリゲートを
            // 握ったままだとアンロード可能な ALC が解放されず、ホットリロードが破綻する。
            SEED.Events.ClearAll();
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
    ///
    /// 参照フィールド（GameObject / Transform / Camera … とその Nullable 版）は
    /// 解決に World と Actor ツリーが必要で、この呼び出し時点（シーンロード中・
    /// IPC 処理中）ではまだ公開されていない。そのため値をそのまま解決せず
    /// 保留キューへ積み、<see cref="ResolveReferenceFields"/> が
    /// スクリプトの OnStart 直前（World 公開中）にまとめて解決・注入する。
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

            // 参照フィールド（単体・配列とも）は即時解決できないので保留キューへ積む
            if (NeedsDeferredReferenceResolution(target, name))
            {
                QueuePendingReference(h, name, value);
                return;
            }

            SetFieldByPath(target, name, value);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[SEEDScripting] SetFieldValue failed: {ex.Message}");
        }
    }

    // ─── 参照フィールドの遅延解決 ─────────────────────────────
    //
    // 【なぜ遅延するか】
    // 参照フィールドはシーンに「アクター名（＋スロット名）」の文字列で保存される。
    // これを実体（entity ハンドル）へ解決するには World と Actor ツリーが必要だが、
    // それらはスクリプトのライフサイクルフェーズ実行中しか公開されない。
    // ScriptComponent の生成（シーンロード / Instantiate / ホットリロード）や
    // エディタからのフィールド編集はフェーズ外で起きるため、その場では解決できない。
    //
    // 【いつ解決するか】
    // Rust の ScriptSystem が BeginFrame フェーズで、そのスクリプトの OnStart を
    // 呼ぶ直前に ResolveReferenceFields を発行する（World / Actor ツリー公開中）。
    // これにより「OnStart の時点では参照が既に注入されている」ことが保証される。

    /// <summary>
    /// 未解決の参照フィールド値（ハンドル → フィールドパス → シリアライズ値）。
    ///
    /// 同じパスへの再設定は上書きする（エディタでの張り替えに追従するため）。
    /// スクリプトは CLR メインスレッド専用だが、辞書操作は破壊的なので lock で守る。
    /// </summary>
    private static readonly System.Collections.Generic.Dictionary<
        nint, System.Collections.Generic.Dictionary<string, string>> PendingReferences = new();

    /// <summary>PendingReferences の排他ロック。</summary>
    private static readonly object PendingReferencesLock = new();

    /// <summary>参照フィールド値を保留キューへ積む（同一パスは上書き）。</summary>
    private static void QueuePendingReference(nint h, string path, string value)
    {
        lock (PendingReferencesLock)
        {
            if (!PendingReferences.TryGetValue(h, out var map))
            {
                map = new System.Collections.Generic.Dictionary<string, string>();
                PendingReferences[h] = map;
            }
            map[path] = value;
        }
    }

    /// <summary>指定ハンドルの保留エントリを破棄する（インスタンス破棄時）。</summary>
    private static void ForgetPendingReferences(nint h)
    {
        lock (PendingReferencesLock) PendingReferences.Remove(h);
    }

    /// <summary>
    /// 保留中の参照フィールドを解決してスクリプトインスタンスへ注入する。
    ///
    /// Rust の ScriptSystem が、World / Actor ツリーを公開したフェーズ内で
    /// OnStart より前に呼ぶ。解決は一度きり（適用後は保留エントリを破棄する）で、
    /// 実行中のアクター名変更には追従しない。
    ///
    /// 解決できなかった参照は、Nullable 宣言なら null（＝未設定）、
    /// 非 Nullable 宣言なら無効ハンドル（IsValid == false）になる。
    /// </summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static void ResolveReferenceFields(nint h, uint entityIndex, uint entityGeneration)
    {
        try
        {
            var target = Get(h);
            if (target is null) return;

            // 参照文字列のパス解決基準（"./Child" の相対指定・素の名前のサブツリー優先）
            // に使う所有エンティティ。Rust 側が未束縛を uint.MaxValue で伝えてくる。
            var owner = entityIndex == uint.MaxValue
                ? SEED.Entity.None
                : new SEED.Entity(entityIndex, entityGeneration);

            // 保留エントリを取り出して即座に辞書から外す（解決は一度きり）
            System.Collections.Generic.Dictionary<string, string>? map;
            lock (PendingReferencesLock)
            {
                if (!PendingReferences.TryGetValue(h, out map)) return;
                PendingReferences.Remove(h);
            }

            foreach (var (path, value) in map)
            {
                // ネスト途中のオブジェクトは必要なら生成してから末端へ書き込む
                if (!TryResolveLeafField(target, path, createMissing: true, out var fieldOwner, out var leaf))
                    continue;
                ApplyResolvedReference(fieldOwner, leaf, value, owner);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[SEEDScripting] ResolveReferenceFields failed: {ex.Message}");
        }
    }

    /// <summary>
    /// 指定パスの末端フィールドが参照フィールド型かを Rust 側から問い合わせる FFI。
    ///
    /// アクタのリネーム時、ランタイムは「値が旧アクタ名に一致するフィールド」を
    /// 新名へ書き換える候補にするが、プレーンな文字列フィールドがたまたま
    /// アクタ名と同じ値を持つ場合に誤って書き換えてはならない。
    /// この FFI で「本当に参照フィールドか」を型情報（リフレクション）で確定させる。
    /// World / Actor ツリーへはアクセスしないため、フェーズ外で呼んでも安全。
    ///
    /// 戻り値: 参照フィールドなら 1、それ以外（不明・エラー含む）は 0。
    /// </summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static int IsReferenceField(nint h, byte* namePtr, int nameLen)
    {
        try
        {
            var target = Get(h);
            if (target is null) return 0;
            var name = Encoding.UTF8.GetString(namePtr, nameLen);
            return IsReferenceFieldPath(target, name) ? 1 : 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[SEEDScripting] IsReferenceField failed: {ex.Message}");
            return 0;
        }
    }

    // ─── [Bindable] フィールドのライブ読み取り（Phase W8.3）──────
    //
    // 【なぜ FFI で読むのか】
    // Rust 側の ScriptComponent.fields は「編集時のシリアライズ値」であり、
    // Play 中にスクリプトが書き換えた値は反映されない。シェーダの @ref バインドは
    // 「今この瞬間の値」を流すのが目的なので、正典は常に CLR 側の実インスタンスである。
    //
    // 【なぜ [Bindable] の検証をここで行うのか】
    // エディタでバインドを張った後にスクリプトから属性が外れる／フィールドが消える
    // ことがある。読み取りのたびに検証しておけば、属性を外した瞬間からバインドは
    // 解決失敗になり、インスペクタに ⚠ が出る（＝設定時だけの検証では追随できない）。

    /// <summary>
    /// バインド元として読める最大成分数（<c>Vector3</c> の 3）。
    /// Rust 側が渡すバッファ長（vec4 = 4）とは別で、こちらは「型の上限」である。
    /// </summary>
    private const int BindableMaxComponents = 3;

    /// <summary>
    /// 指定パスの <c>[SerializeField, Bindable]</c> フィールドの**実行中の値**を
    /// float 配列として読み出す FFI（水面シェーダの <c>@ref</c> バインド。Phase W8.3）。
    ///
    /// リフレクションでインスタンスのフィールドを読むだけで World / Actor ツリーへは
    /// 触れないため、スクリプトフェーズ外（描画準備中・インスペクタ更新中）でも安全。
    ///
    /// 戻り値: 書き込んだ成分数（<c>float</c> なら 1、<c>Vector3</c> なら 3）。
    /// フィールドが無い・<c>[Bindable]</c> が無い・<c>[SerializeField]</c> が無い・
    /// 型が非対応・バッファ不足・例外のいずれでも **0**（＝解決失敗）を返す。
    /// </summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static int ReadFieldFloats(nint h, byte* namePtr, int nameLen, float* outBuf, int capacity)
    {
        try
        {
            if (outBuf is null || capacity <= 0) return 0;
            var target = Get(h);
            if (target is null) return 0;

            var path = Encoding.UTF8.GetString(namePtr, nameLen);
            if (!TryResolveLeafField(target, path, createMissing: false, out var owner, out var leaf))
                return 0;

            // [SerializeField] と [Bindable] の両方が要る。
            // 属性は Roslyn コンパイルと ALC コンパイルでアセンブリ ID が異なりうるので、
            // 型一致ではなく**属性名**で照合する（ScriptCompiler と同じ流儀）。
            if (!HasAttributeNamed(leaf, nameof(SerializeFieldAttribute))) return 0;
            if (!HasAttributeNamed(leaf, nameof(BindableAttribute)))       return 0;

            var value = leaf.GetValue(owner);
            if (value is null) return 0;

            // 対応型は WGSL 側と 1 対 1 の 2 種類だけ。成分の部分取り出しはしない。
            switch (value)
            {
                case float f when capacity >= 1:
                    outBuf[0] = f;
                    return 1;
                case SEED.Vector3 v when capacity >= BindableMaxComponents:
                    outBuf[0] = v.x;
                    outBuf[1] = v.y;
                    outBuf[2] = v.z;
                    return BindableMaxComponents;
                default:
                    return 0;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[SEEDScripting] ReadFieldFloats failed: {ex.Message}");
            return 0;
        }
    }

    // ─── バインド値の汎用読み取り（Text の {num} / {string}・新経路）───
    //
    // 【なぜ ReadFieldFloats と別に用意するのか】
    // ReadFieldFloats は「水面シェーダの @ref」専用に設計されており、
    //   ・[SerializeField] + [Bindable] の**フィールド**だけ
    //   ・float / Vector3 だけ（float 配列で運ぶ）
    // という契約を持つ。Text のプレースホルダ（{string}）は文字列を運ぶ必要があり、
    // また「計算結果を出したい」用途ではプロパティやメソッドを公開したい。
    // 既存経路の契約を広げると水面側の互換が壊れるため、**新経路を並走**させる。

    /// <summary>数値（float / int）を要求する種別コード（Rust 側 BindableValueType::F32 に対応）。</summary>
    private const int BindableKindNum = 0;

    /// <summary>文字列を要求する種別コード（Rust 側 BindableValueType::Str に対応）。</summary>
    private const int BindableKindStr = 1;

    /// <summary>解決に失敗したことを表す戻り値（見つからない・型不一致・例外）。</summary>
    private const int BindableFailure = -1;

    /// <summary>
    /// バッファ不足を表す戻り値の基準値。
    /// 実際の戻り値は <c>-(必要バイト数) + BindableShortBufferBase</c>（= -必要バイト数 - 1）で、
    /// 失敗コード <see cref="BindableFailure"/>（-1）と必ず区別できる。
    /// </summary>
    private const int BindableShortBufferBase = -1;

    /// <summary>数値バインドが書き込むバイト数（float 1 個 = リトルエンディアン 4 バイト）。</summary>
    private const int BindableNumBytes = 4;

    /// <summary>
    /// <c>[Bindable]</c> メンバの**実行中の値**を、要求種別に合わせて読み出す FFI。
    ///
    /// <para>対象になるメンバ（<see cref="BindableAttribute"/> の契約）:
    /// <c>[SerializeField, Bindable]</c> のフィールド／<c>[Bindable]</c> の get 付きプロパティ／
    /// <c>[Bindable]</c> の引数なしメソッド。型は <c>float</c> / <c>int</c>（→ <c>{num}</c>）と
    /// <c>string</c>（→ <c>{string}</c>）。</para>
    ///
    /// <para><b>メソッドは毎フレーム呼び出される。</b> 副作用を持たせてはならない。</para>
    ///
    /// 引数:
    ///   <paramref name="wantKind"/> … 0 = 数値 / 1 = 文字列（それ以外は失敗）
    ///   <paramref name="outBuf"/>, <paramref name="capacity"/> … 値の書き込み先（バイト列）
    ///
    /// <para><b>ワイヤ表現（Rust 側と一致必須）</b>: 数値は <c>float</c> を
    /// <b>リトルエンディアンの 4 バイト</b>で書く（Rust は <c>f32::from_le_bytes</c> で読む）。
    /// 文字列は UTF-8 バイト列をそのまま書く。
    /// バッファを 1 本にしているのは、FFI の引数が 6 個までという制約
    /// （netcorehost の <c>FnPtr</c> 実装が既定で 6 引数まで）に収めるためである。</para>
    ///
    /// 戻り値:
    ///   <c>&gt;= 0</c> … 成功。書き込んだバイト数（数値は必ず 4、
    ///                    文字列は UTF-8 のバイト数。空文字列は 0 で、これも成功）。
    ///   <c>-1</c>      … 解決失敗（メンバが無い・属性が無い・型不一致・例外）。
    ///   <c>&lt;= -2</c> … バッファ不足。必要バイト数 = <c>-(戻り値) - 1</c>。
    /// </summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static int ReadBindableValue(
        nint h, byte* namePtr, int nameLen, int wantKind, byte* outBuf, int capacity)
    {
        string path = string.Empty;
        try
        {
            var target = Get(h);
            if (target is null) return BindableFailure;

            path = Encoding.UTF8.GetString(namePtr, nameLen);
            if (!TryReadBindableMember(target, path, out var value)) return BindableFailure;

            switch (wantKind)
            {
                // ── 数値（float / int を LE 4 バイトで運ぶ）──
                case BindableKindNum:
                {
                    if (!TryToBindableFloat(value, out var f)) return BindableFailure;
                    if (outBuf is null || capacity < BindableNumBytes)
                        return -BindableNumBytes + BindableShortBufferBase;
                    System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(
                        new Span<byte>(outBuf, BindableNumBytes), f);
                    return BindableNumBytes;
                }
                // ── 文字列（UTF-8 バイト列で運ぶ）──
                case BindableKindStr:
                {
                    if (value is not string s) return BindableFailure;
                    var bytes = Encoding.UTF8.GetBytes(s);
                    if (outBuf is null || capacity < bytes.Length)
                        return -bytes.Length + BindableShortBufferBase;
                    for (int i = 0; i < bytes.Length; i++) outBuf[i] = bytes[i];
                    return bytes.Length;
                }
                // ── 未知の種別コード（Rust 側の型追加漏れ）──
                default:
                    return BindableFailure;
            }
        }
        catch (Exception ex)
        {
            // 毎フレーム呼ばれる経路なので、同じ (ハンドル, メンバ) につき 1 回だけ通知する。
            LogBindableErrorOnce(h, path, ex);
            return BindableFailure;
        }
    }

    /// <summary>
    /// このスクリプトが公開している <c>[Bindable]</c> メンバの一覧を JSON 配列で書き出す FFI。
    ///
    /// <para>形式: <c>[{"name":"Speed","type":"f32"},{"name":"Title","type":"str"}]</c>
    ///  - name … <see cref="ReadBindableValue"/> に渡すメンバ名
    ///  - type … <c>f32</c>（float / int）/ <c>str</c>（string）/ <c>vec3</c>（Vector3 フィールド）</para>
    ///
    /// <para><c>vec3</c> は水面シェーダの <c>@ref</c> 専用で、既存経路
    /// （<see cref="ReadFieldFloats"/>）が読める <c>[SerializeField, Bindable]</c> の
    /// <c>Vector3</c> <b>フィールドだけ</b>を挙げる
    /// （プロパティ／メソッドの <c>Vector3</c> は既存経路が読めないため候補にしない）。</para>
    ///
    /// <para>候補列挙はインスペクタのバインド先選択でのみ使う（毎フレームでは呼ばれない）。
    /// リフレクションのみで World へは触れないため、スクリプトフェーズ外でも安全。</para>
    ///
    /// 戻り値: 書き込んだバイト数（空配列でも "[]" の 2 バイト）。
    ///         バッファ不足なら**必要バイト数の負値**。ハンドル無効・例外時は 0。
    /// </summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static int DescribeBindableMembers(nint h, byte* outBuf, int capacity)
    {
        try
        {
            var target = Get(h);
            if (target is null) return 0;

            var sb    = new StringBuilder("[");
            var seen  = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
            var first = true;
            foreach (var (name, typeTag) in EnumerateBindableMembers(target.GetType()))
            {
                // 同名メンバ（フィールドを隠すプロパティ等）は最初の 1 件だけ載せる。
                if (!seen.Add(name)) continue;
                if (!first) sb.Append(',');
                first = false;
                sb.Append("{\"name\":").Append(JsonString(name))
                  .Append(",\"type\":").Append(JsonString(typeTag)).Append('}');
            }
            sb.Append(']');

            var bytes = Encoding.UTF8.GetBytes(sb.ToString());
            if (outBuf is null || capacity < bytes.Length) return -bytes.Length;
            for (int i = 0; i < bytes.Length; i++) outBuf[i] = bytes[i];
            return bytes.Length;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[SEEDScripting] DescribeBindableMembers failed: {ex.Message}");
            return 0;
        }
    }

    // ─── [Bindable] メンバの走査・型判定（上記 2 つの FFI が共有）───

    /// <summary>プロパティ／メソッドの走査に使う BindingFlags（<see cref="FieldFlags"/> と対）。</summary>
    private const System.Reflection.BindingFlags BindableMemberFlags =
        System.Reflection.BindingFlags.Public |
        System.Reflection.BindingFlags.NonPublic |
        System.Reflection.BindingFlags.Instance;

    /// <summary>数値バインドのワイヤ型名（Rust の BindableValueType::as_str と一致必須）。</summary>
    private const string BindableTypeF32 = "f32";
    /// <summary>文字列バインドのワイヤ型名（同上）。</summary>
    private const string BindableTypeStr = "str";
    /// <summary>3 成分バインドのワイヤ型名（同上。水面シェーダ専用）。</summary>
    private const string BindableTypeVec3 = "vec3";

    /// <summary>
    /// 型が公開している <c>[Bindable]</c> メンバを (メンバ名, ワイヤ型名) の列として返す。
    ///
    /// 並び順は「フィールド → プロパティ → メソッド」で、その中は宣言順
    /// （＝インスペクタの候補一覧の並びになる）。
    /// </summary>
    private static System.Collections.Generic.IEnumerable<(string Name, string TypeTag)>
        EnumerateBindableMembers(Type type)
    {
        // ── フィールド（[SerializeField] 併用が必須）──
        foreach (var f in type.GetFields(FieldFlags))
        {
            if (!HasAttributeNamed(f, nameof(BindableAttribute)))       continue;
            if (!HasAttributeNamed(f, nameof(SerializeFieldAttribute))) continue;
            // Vector3 はフィールドのときだけ候補にする（既存の @ref 経路が読めるのがフィールドだけのため）。
            if (f.FieldType == typeof(SEED.Vector3)) { yield return (f.Name, BindableTypeVec3); continue; }
            if (TryGetBindableTypeTag(f.FieldType, out var tag)) yield return (f.Name, tag);
        }
        // ── プロパティ（get があること。[SerializeField] は不要）──
        foreach (var p in type.GetProperties(BindableMemberFlags))
        {
            if (!HasAttributeNamed(p, nameof(BindableAttribute))) continue;
            if (p.GetMethod is null || p.GetIndexParameters().Length > 0) continue;
            if (TryGetBindableTypeTag(p.PropertyType, out var tag)) yield return (p.Name, tag);
        }
        // ── メソッド（引数なし・戻り値あり。[SerializeField] は不要）──
        foreach (var m in type.GetMethods(BindableMemberFlags))
        {
            if (!HasAttributeNamed(m, nameof(BindableAttribute))) continue;
            if (m.GetParameters().Length > 0 || m.IsGenericMethodDefinition) continue;
            if (TryGetBindableTypeTag(m.ReturnType, out var tag)) yield return (m.Name, tag);
        }
    }

    /// <summary>
    /// バインドで運べる型かを判定し、ワイヤ型名を返す。
    /// <c>float</c> / <c>int</c> は <c>f32</c>、<c>string</c> は <c>str</c>。それ以外は false。
    /// </summary>
    private static bool TryGetBindableTypeTag(Type t, out string tag)
    {
        if (t == typeof(float) || t == typeof(int)) { tag = BindableTypeF32; return true; }
        if (t == typeof(string))                    { tag = BindableTypeStr; return true; }
        tag = string.Empty;
        return false;
    }

    /// <summary>
    /// 名前で <c>[Bindable]</c> メンバを引き当て、その**実行中の値**を取り出す。
    ///
    /// 探索順は宣言と同じ「フィールド → プロパティ → メソッド」。
    /// 属性・アクセサ・引数の条件は <see cref="EnumerateBindableMembers"/> と完全に同じで、
    /// 「候補には出るが読めない」「読めるのに候補に出ない」が起きないようにしてある。
    /// </summary>
    private static bool TryReadBindableMember(object target, string name, out object? value)
    {
        value = null;
        var type = target.GetType();

        // ── フィールド（[SerializeField] + [Bindable]）──
        var field = type.GetField(name, FieldFlags);
        if (field is not null
            && HasAttributeNamed(field, nameof(BindableAttribute))
            && HasAttributeNamed(field, nameof(SerializeFieldAttribute)))
        {
            value = field.GetValue(target);
            return true;
        }

        // ── プロパティ（[Bindable] + get あり・添字なし）──
        var prop = type.GetProperty(name, BindableMemberFlags);
        if (prop is not null
            && HasAttributeNamed(prop, nameof(BindableAttribute))
            && prop.GetMethod is not null
            && prop.GetIndexParameters().Length == 0)
        {
            value = prop.GetValue(target);
            return true;
        }

        // ── メソッド（[Bindable] + 引数なし）──
        var method = type.GetMethod(
            name, BindableMemberFlags, binder: null, types: Type.EmptyTypes, modifiers: null);
        if (method is not null
            && HasAttributeNamed(method, nameof(BindableAttribute))
            && !method.IsGenericMethodDefinition)
        {
            value = method.Invoke(target, null);
            return true;
        }
        return false;
    }

    /// <summary>
    /// 数値バインドの値を float へ変換する（<c>int</c> は float へ広げる）。
    /// それ以外の型（<c>string</c> / <c>Vector3</c> / null 等）は false。
    /// </summary>
    private static bool TryToBindableFloat(object? value, out float result)
    {
        switch (value)
        {
            case float f: result = f;  return true;
            case int   i: result = i;  return true;
            default:      result = 0f; return false;
        }
    }

    /// <summary>
    /// バインド読み取りで起きた例外を、(ハンドル, メンバ名) につき 1 回だけ通知する。
    ///
    /// この経路は**毎フレーム**走るため、素直にログを出すと 1 例外で
    /// コンソールが埋まって他のエラーが読めなくなる。
    /// </summary>
    private static void LogBindableErrorOnce(nint h, string member, Exception ex)
    {
        try
        {
            lock (ScriptErrorLock)
            {
                if (!BindableErrorLogged.Add((h, member))) return;
            }
            Console.Error.WriteLine(
                $"[SEEDScripting] ReadBindableValue failed: {SafeTypeName(h)}.{member}: {ex.Message}"
                + "（以降このメンバの失敗は通知しません）");
        }
        catch
        {
            // ログ出力自体の失敗（stderr 切断等）でプロセスを落とさない。
        }
    }

    /// <summary>
    /// 通知済みのバインド読み取り例外（(ハンドル, メンバ名) の集合）。
    /// <see cref="ScriptErrorLock"/> で保護し、ハンドル破棄・ホットリロードで掃除する。
    /// </summary>
    private static readonly System.Collections.Generic.HashSet<(nint Handle, string Member)>
        BindableErrorLogged = new();

    // ─── [SerializeField] 定義のスナップショット（ホットリロードの値引き継ぎ）───
    //
    // 【なぜ必要か】
    // ホットリロード直後、Rust 側は「旧アセンブリ時代に保存した値（名前 → 文字列）」だけを
    // 持っている。しかし新アセンブリではフィールドが増減・改名・型変更されている可能性がある。
    // 型情報を持たない Rust だけでは「引き継いでよい値」を判定できない。
    // そこで、生成直後（＝宣言時初期値のまま）のインスタンスから
    // 「フィールドパス・型タグ・既定値」を吸い出して渡し、
    // 引き継ぎ判定そのものは Rust の純粋関数（carry_over_script_fields）に任せる。

    /// <summary>ネスト展開の再帰上限（循環参照・過度な深さの安全弁。ScriptCompiler と同値）。</summary>
    private const int DescribeMaxNestDepth = 8;

    /// <summary>
    /// スクリプトインスタンスの [SerializeField] フィールド定義を JSON 配列として書き出す FFI。
    ///
    /// 形式: <c>[{"name":"stats.hp","type":"int","default":"10"}, ...]</c>
    ///  - name    : ドット区切りのフィールドパス（<see cref="SetFieldValue"/> と同じ表記）
    ///  - type    : float / double / int / long / short / bool / string / reference / unsupported、
    ///              配列フィールドは array:&lt;要素型タグ&gt;（構造体配列は array:struct:&lt;構造体名&gt;）
    ///  - default : 宣言時初期値の文字列化（reference / unsupported は空文字）
    ///  - members : 構造体配列のときだけ付く、要素構造体のメンバ定義配列
    ///              （[{"name":..,"label":..,"type":..,"default":..}, ...]）
    ///
    /// **生成直後のインスタンスに対して呼ぶこと。** 値を書き込んだ後に呼ぶと、
    /// 「既定値」ではなく「書き込み済みの値」が返ってしまう。
    ///
    /// リフレクションのみで World / Actor ツリーへは触れないため、
    /// スクリプトフェーズ外（IPC 処理中・インスペクタ更新中）でも安全に呼べる。
    ///
    /// 戻り値: 書き込んだバイト数（空配列でも "[]" の 2 バイト）。
    ///         バッファ不足なら **必要バイト数の負値**（呼び出し側は拡張して再試行する）。
    ///         ハンドル無効・例外時は 0。
    /// </summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static int DescribeSerializeFields(nint h, byte* outBuf, int capacity)
    {
        try
        {
            var target = Get(h);
            if (target is null) return 0;

            var sb = new StringBuilder("[");
            AppendFieldDescriptions(sb, target, target.GetType(), prefix: "", depth: 0);
            sb.Append(']');

            var bytes = Encoding.UTF8.GetBytes(sb.ToString());
            if (outBuf is null || capacity < bytes.Length) return -bytes.Length;
            for (int i = 0; i < bytes.Length; i++) outBuf[i] = bytes[i];
            return bytes.Length;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[SEEDScripting] DescribeSerializeFields failed: {ex.Message}");
            return 0;
        }
    }

    /// <summary>
    /// [SerializeField] フィールドを再帰的に走査し、JSON 要素を sb へ追記する。
    ///
    /// instance が null（ネストオブジェクトが未生成）の場合は、型から一時インスタンスを
    /// 生成して宣言時初期値を読む（生成できなければ既定値は空文字になる）。
    /// </summary>
    private static void AppendFieldDescriptions(
        StringBuilder sb, object? instance, Type type, string prefix, int depth)
    {
        if (instance is null)
        {
            // 既定値読み取り用の一時インスタンス（生成失敗は致命ではないので握り潰す）
            try { instance = Activator.CreateInstance(type); } catch { instance = null; }
        }

        foreach (var f in type.GetFields(FieldFlags))
        {
            if (!HasAttributeNamed(f, nameof(SerializeFieldAttribute))) continue;

            var path  = prefix + f.Name;
            var value = instance is null ? null : f.GetValue(instance);
            var isRef = SEED.ScriptReference.TryGetKind(f.FieldType, out _);

            // 配列フィールド（T[] / List<T>）は 1 本の JSON 配列文字列として扱う葉である。
            // List<T> は BCL で [Serializable] が付いているため、ネスト判定より先に
            // 配列判定を行わないと List の内部フィールドへ降りてしまう。
            var isArray = SEED.ScriptArray.TryGetElementType(f.FieldType, out _, out _);

            // ScriptEvent フィールドも「1 本の JSON 配列文字列」で表す葉である。
            // 内部の List<ScriptEventBinding> へ降りてしまわないよう、ネスト判定より先に弾く。
            var isScriptEvent = SEED.ScriptEvent.IsScriptEventType(f.FieldType);

            // [Serializable] ネストクラスは葉ではないので子へ降りる
            //（参照ハンドル・ScriptEvent は内部構造を晒さないため展開しない。ScriptCompiler と同じ規則）
            if (!isRef && !isArray && !isScriptEvent &&
                depth < DescribeMaxNestDepth && IsNestedSerializableType(f.FieldType))
            {
                AppendFieldDescriptions(sb, value, f.FieldType, path + ".", depth + 1);
                continue;
            }

            if (sb.Length > 1) sb.Append(',');   // "[" だけのときは区切り不要
            sb.Append("{\"name\":").Append(JsonString(path))
              .Append(",\"type\":").Append(JsonString(TypeTagOf(f.FieldType, isRef)))
              .Append(",\"default\":").Append(JsonString(DefaultValueString(f.FieldType, isRef, value)));

            // 構造体配列（array:struct:Xxx）はメンバのレイアウトも渡す。
            // Rust 側はこれを見て「メンバ名＋型が合う要素か」を確かめ、引き継ぎ可否を決める。
            if (!isRef && SEED.ScriptArray.TryGetElementType(f.FieldType, out var structElem, out _) &&
                SEED.ScriptStructArray.TryGetLayout(structElem, out var structMembers))
            {
                sb.Append(",\"members\":").Append(SEED.ScriptStructArray.MembersMetadataJson(structMembers));
            }

            sb.Append('}');
        }
    }

    /// <summary>
    /// [Serializable] が付いた、パスを掘り下げるべきネストクラス型かを判定する。
    /// 判定規則はエディタ側 ScriptCompiler.IsNestedSerializable と一致させること。
    /// </summary>
    private static bool IsNestedSerializableType(Type t)
    {
        if (t.IsPrimitive || t.IsEnum || t == typeof(string) || t.IsArray) return false;
        // List<T> は BCL で [Serializable] が付いているが、内部フィールドを展開する対象ではない
        // （配列フィールドとして 1 本の JSON 文字列に畳む）。ScriptCompiler と同じ規則。
        if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(System.Collections.Generic.List<>))
            return false;
        if (!t.IsClass && !(t.IsValueType && !t.IsPrimitive)) return false;
        // アセンブリ ID 差異を吸収するため属性名で照合する
        return t.GetCustomAttributesData().Any(a => a.AttributeType.Name == "SerializableAttribute");
    }

    /// <summary>
    /// Rust へ渡す型タグ。Rust 側 carry_over_script_fields の型一致判定に使う。
    /// 対応型は <see cref="ConvertValue"/> と 1 対 1 で対応させること。
    /// </summary>
    private static string TypeTagOf(Type t, bool isReference)
    {
        if (isReference)         return "reference";

        // ScriptEvent（UnityEvent 相当）は 1 本の JSON 配列文字列として保存する葉。
        // 配列判定より先に見る（内部実装が List<T> でも配列フィールド扱いにしないため）。
        if (SEED.ScriptEvent.IsScriptEventType(t)) return SEED.ScriptEvent.TypeTag;

        // 列挙型（[Flags] を除く）はメンバ名の文字列 1 本として保存する葉。
        // 判定・書式の正典は SEED.ScriptEnumField（エディタ／Rust と共有）。
        if (SEED.ScriptEnumField.IsEnumFieldType(t)) return SEED.ScriptEnumField.TypeTag;

        // 配列フィールド（T[] / List<T>）は "array:要素型タグ" で表す。
        // 要素型がインスペクタ非対応なら配列全体を unsupported とする。
        if (SEED.ScriptArray.TryGetElementType(t, out var elementType, out _))
        {
            var elementTag = SEED.ScriptArray.ElementTypeTag(elementType);
            return elementTag is null ? "unsupported" : SEED.ScriptArray.TypeTagPrefix + elementTag;
        }

        if (t == typeof(float))  return "float";
        if (t == typeof(double)) return "double";
        if (t == typeof(int))    return "int";
        if (t == typeof(long))   return "long";
        if (t == typeof(short))  return "short";
        if (t == typeof(bool))   return "bool";
        if (t == typeof(string)) return "string";
        return "unsupported";
    }

    /// <summary>
    /// 宣言時初期値の文字列化（<see cref="ConvertValue"/> が解釈できる表記に合わせる）。
    /// 参照フィールドはアクター名の文字列として保存されるので、既定は「未設定＝空文字」。
    /// </summary>
    private static string DefaultValueString(Type type, bool isReference, object? value)
    {
        // ScriptEvent の結線はインスペクタで作るものなので、宣言時初期値は常に「結線なし」。
        // （スクリプト側の初期化子で結線を書く用途は想定しない＝データ側の一元管理を保つ）
        if (!isReference && SEED.ScriptEvent.IsScriptEventType(type))
            return SEED.ScriptEvent.EmptyJson;

        // 列挙型は宣言時初期値の「メンバ名」を返す（数値ではなく名前で保存する規約）。
        // 宣言に無い値（未定義値のキャスト）は名前を持たないので空文字になる。
        if (!isReference && SEED.ScriptEnumField.IsEnumFieldType(type))
            return SEED.ScriptEnumField.ToText(type, value);

        // 配列フィールドは実配列を JSON 配列文字列へ書き出す（未初期化なら "[]"）
        if (!isReference && SEED.ScriptArray.TryGetElementType(type, out var elementType, out _))
            return SEED.ScriptArray.EncodeValue(value, elementType);

        if (isReference || value is null) return "";
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        return value switch
        {
            bool b   => b ? "true" : "false",
            float f  => f.ToString("R", inv),
            double d => d.ToString("R", inv),
            string s => s,
            _        => Convert.ToString(value, inv) ?? "",
        };
    }

    /// <summary>文字列を JSON 文字列リテラル（引用符込み）へエスケープする。</summary>
    private static string JsonString(string s)
    {
        var sb = new StringBuilder(s.Length + 2);
        sb.Append('"');
        foreach (var c in s)
        {
            switch (c)
            {
                case '"':  sb.Append("\\\""); break;
                case '\\': sb.Append(@"\\"); break;
                case '\n': sb.Append(@"\n");  break;
                case '\r': sb.Append(@"\r");  break;
                case '\t': sb.Append(@"\t");  break;
                default:
                    if (c < 0x20) sb.Append(@"\u").Append(((int)c).ToString("x4"));
                    else          sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
        return sb.ToString();
    }

    /// <summary>
    /// フィールドに指定名の属性が付いているかを**属性名で**判定する。
    ///
    /// エディタの Roslyn コンパイルとランタイムの ALC コンパイルでは属性の
    /// アセンブリ ID が異なる場合があるため、<c>GetCustomAttribute</c> の型一致ではなく
    /// 名前で照合する（ScriptCompiler の <c>HasSerializeField</c> と同じ理由）。
    /// </summary>
    private static bool HasAttributeNamed(System.Reflection.FieldInfo field, string attributeTypeName)
        => field.GetCustomAttributesData().Any(a => a.AttributeType.Name == attributeTypeName);

    /// <summary>
    /// フィールド以外のメンバ（プロパティ・メソッド）に指定名の属性が付いているかを
    /// **属性名で**判定する（フィールド版と同じ理由で型一致を使わない）。
    /// </summary>
    private static bool HasAttributeNamed(System.Reflection.MemberInfo member, string attributeTypeName)
        => member.GetCustomAttributesData().Any(a => a.AttributeType.Name == attributeTypeName);

    /// <summary>
    /// 指定パスの末端フィールドが参照フィールド型かを判定する。
    /// 途中のネストオブジェクトを生成せずに型だけを辿るため、判定に副作用がない。
    /// </summary>
    private static bool IsReferenceFieldPath(object root, string path)
        => TryResolveLeafFieldType(root.GetType(), path, out var leafType)
        && SEED.ScriptReference.TryGetKind(leafType, out _);

    /// <summary>
    /// 指定パスの末端フィールドが「World 公開後でないと解決できない」フィールドかを判定する。
    /// 参照フィールド単体に加えて、要素が参照型の配列フィールド（<c>Transform[]</c> など）も含む。
    /// </summary>
    private static bool NeedsDeferredReferenceResolution(object root, string path)
    {
        if (!TryResolveLeafFieldType(root.GetType(), path, out var leafType)) return false;
        if (SEED.ScriptReference.TryGetKind(leafType, out _)) return true;
        if (!SEED.ScriptArray.TryGetElementType(leafType, out var elementType, out _)) return false;
        if (SEED.ScriptReference.TryGetKind(elementType, out _)) return true;

        // 構造体配列は「参照メンバ（単体・配列とも）を 1 つでも含む」ときだけ遅延させる。
        // 参照を含まない構造体配列はその場で組み立てられるので即時適用にする
        //（遅延キューは OnStart 直前に 1 度しか掃かれないため、不要な遅延は避ける）。
        return SEED.ScriptStructArray.TryGetLayout(elementType, out var members)
            && members.Any(m => m.NeedsWorld);
    }

    /// <summary>
    /// 保留中の値をフィールドへ書き込む（参照単体 / 参照配列の両対応）。
    ///
    /// 参照配列は JSON 配列文字列を要素ごとに解決してから実配列を組み立てる。
    /// 解決できない要素は、Nullable 宣言なら null、非 Nullable なら無効ハンドルになる
    /// （単体の参照フィールドとまったく同じ規則）。
    /// </summary>
    private static void ApplyResolvedReference(
        object owner, System.Reflection.FieldInfo leaf, string value, SEED.Entity ownerEntity)
    {
        var leafType = leaf.FieldType;

        // 構造体配列（参照メンバを含むもの）: メンバ単位で解決して作り直す。
        // 参照メンバは Resolve、それ以外は通常の ConvertValue と同じ規則で変換する。
        if (SEED.ScriptArray.TryGetElementType(leafType, out var structElem, out var structIsList) &&
            SEED.ScriptStructArray.TryGetLayout(structElem, out var structMembers))
        {
            var built = SEED.ScriptStructArray.BuildInstance(
                structElem, structIsList, value, structMembers,
                (memberType, memberValue) => ResolveOrConvert(memberType, memberValue, ownerEntity));
            leaf.SetValue(owner, built);
            return;
        }

        // 参照配列: 要素ごとに解決して T[] / List<T> を作り直す
        if (!SEED.ScriptReference.TryGetKind(leafType, out _) &&
            SEED.ScriptArray.TryGetElementType(leafType, out var elementType, out var isList) &&
            SEED.ScriptReference.TryGetKind(elementType, out _))
        {
            var resolved = SEED.ScriptArray.BuildInstance(
                elementType, isList, value,
                (elemType, elemValue) => SEED.ScriptReference.Resolve(elemType, elemValue, ownerEntity));
            leaf.SetValue(owner, resolved);
            return;
        }

        leaf.SetValue(owner, SEED.ScriptReference.Resolve(leafType, value, ownerEntity));
    }

    /// <summary>
    /// 参照型なら実体へ解決し、それ以外は通常の型変換を行う（構造体配列のメンバ用）。
    /// World 公開中（<see cref="ResolveReferenceFields"/> 実行中）にのみ呼ぶこと。
    /// </summary>
    private static object? ResolveOrConvert(Type type, string value, SEED.Entity ownerEntity)
        => SEED.ScriptReference.TryGetKind(type, out _)
            ? SEED.ScriptReference.Resolve(type, value, ownerEntity)
            : ConvertValue(type, value);

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
            // バインド読み取りの通知済みフラグも同じハンドル単位で捨てる
            //（同じ GCHandle 値が再利用されたときに、初回の通知が握り潰されないようにする）。
            BindableErrorLogged.RemoveWhere(k => k.Handle == h);
        }
    }

    /// <summary>例外抑制状態を全消去する（ホットリロード時）。</summary>
    private static void ClearAllErrorState()
    {
        lock (ScriptErrorLock)
        {
            ScriptErrorCounts.Clear();
            BindableErrorLogged.Clear();
        }
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
        if (!TryResolveLeafField(root, path, createMissing: true, out var owner, out var leaf)) return;

        // [TextArea] を付けた string フィールドは、IPC が 1 行 1 コマンドである都合で
        // 改行・タブをエスケープした表記で届く（シーン JSON にも同じ表記で保存されている）。
        // 実体へ入れる直前に生の文字列へ戻す。エスケープ規約の正典は SEED.ScriptTextArea。
        //
        // 対象を [TextArea] 付きの string に限定しているのは、無条件に戻すと
        // バックスラッシュを含む既存の文字列（Windows のパスなど）を壊すため。
        // 構造体リストの中の string は JSON 文字列として運ばれるのでこの経路を通らない。
        if (leaf.FieldType == typeof(string) &&
            HasAttributeNamed(leaf, SEED.ScriptTextArea.AttributeName))
            value = SEED.ScriptTextArea.Unescape(value);

        var converted = ConvertValue(leaf.FieldType, value);
        if (converted is null)
        {
            // 列挙型は「未知のメンバ名・空文字 → 現在値（宣言時初期値）を維持」が仕様なので、
            // 変換失敗は異常ではない。列挙子を削除／改名した後の古い保存値で毎回警告が出るのを避ける。
            if (!SEED.ScriptEnumField.IsEnumFieldType(leaf.FieldType))
                Console.Error.WriteLine($"[SEEDScripting] unsupported field type: {leaf.FieldType.Name} ({leaf.Name})");
            return;
        }
        leaf.SetValue(owner, converted);
    }

    /// <summary>
    /// ドット区切りパスをたどり、末端フィールドとその所有オブジェクトを取得する。
    ///
    /// createMissing = true のとき、途中のネストオブジェクトが null なら生成して親へ設定する
    /// （値を書き込む用途）。false のときは生成せず、null に当たった時点で失敗を返す。
    /// </summary>
    private static bool TryResolveLeafField(
        object root, string path, bool createMissing,
        out object owner, out System.Reflection.FieldInfo leaf)
    {
        owner = root;
        leaf  = null!;

        var segments = path.Split('.');
        object current = root;

        // 末端の 1 つ手前までネストオブジェクトをたどる
        for (int i = 0; i < segments.Length - 1; i++)
        {
            var f = current.GetType().GetField(segments[i], FieldFlags);
            if (f is null)
            {
                Console.Error.WriteLine($"[SEEDScripting] nested field not found: {current.GetType().Name}.{segments[i]}");
                return false;
            }
            var child = f.GetValue(current);
            if (child is null)
            {
                if (!createMissing) return false;
                // ネストオブジェクトが未生成なら生成して親へ設定する
                child = Activator.CreateInstance(f.FieldType);
                if (child is null)
                {
                    Console.Error.WriteLine($"[SEEDScripting] cannot instantiate nested type: {f.FieldType.Name}");
                    return false;
                }
                // 生成したネストオブジェクトにも ScriptEvent の非 null 保証を効かせる
                EnsureScriptEventInstances(child, f.FieldType, 0);
                f.SetValue(current, child);
            }
            current = child;
        }

        var leafName = segments[^1];
        var found = current.GetType().GetField(leafName, FieldFlags);
        if (found is null)
        {
            Console.Error.WriteLine($"[SEEDScripting] field not found: {current.GetType().Name}.{leafName}");
            return false;
        }

        owner = current;
        leaf  = found;
        return true;
    }

    /// <summary>
    /// ドット区切りパスの末端フィールド「型」だけを、インスタンスを介さずに辿る。
    /// 参照フィールド判定（<see cref="IsReferenceFieldPath"/>）用で、副作用がない。
    /// </summary>
    private static bool TryResolveLeafFieldType(Type rootType, string path, out Type leafType)
    {
        leafType = null!;
        var segments = path.Split('.');
        var current  = rootType;

        for (int i = 0; i < segments.Length - 1; i++)
        {
            var f = current.GetField(segments[i], FieldFlags);
            if (f is null) return false;
            current = f.FieldType;
        }

        var leaf = current.GetField(segments[^1], FieldFlags);
        if (leaf is null) return false;
        leafType = leaf.FieldType;
        return true;
    }

    /// <summary>
    /// <c>ScriptEvent</c> 型のフィールドを、まだ null なら空の実インスタンスで埋める。
    ///
    /// 【なぜ必要か】
    /// フィールド値マップ（Rust 側 <c>ScriptComponent.fields</c>）には
    /// 「インスペクタで明示的に設定した値」しか入らない規約なので、
    /// 一度も結線していない <c>ScriptEvent</c> フィールドには <c>SetFieldValue</c> が飛んでこない。
    /// そのままだとスクリプト側の <c>onStart.Invoke()</c> が NullReferenceException になる。
    /// 「ScriptEvent は非 null」を守るため、インスタンス生成直後にここで実体を作る。
    /// フィールド初期化子（<c>= new ScriptEvent()</c>）を書いてある場合は何もしない。
    ///
    /// ネストした [Serializable] クラスも <see cref="DescribeMaxNestDepth"/> まで同じ規則で辿る
    /// （降下の判定は <see cref="AppendFieldDescriptions"/> と揃えてある）。
    /// </summary>
    private static void EnsureScriptEventInstances(object instance, Type type, int depth)
    {
        foreach (var f in type.GetFields(FieldFlags))
        {
            if (!HasAttributeNamed(f, nameof(SerializeFieldAttribute))) continue;

            // ScriptEvent フィールド: null なら結線 0 件の実体を入れる
            if (SEED.ScriptEvent.IsScriptEventType(f.FieldType))
            {
                if (f.GetValue(instance) is null) f.SetValue(instance, new SEED.ScriptEvent());
                continue;
            }

            // ネストクラスは「既に実体があるもの」だけ辿る
            //（null のネストは値が来た時点で生成され、その際にも本メソッドが呼ばれる）
            if (depth >= DescribeMaxNestDepth) continue;
            if (SEED.ScriptReference.TryGetKind(f.FieldType, out _)) continue;
            if (SEED.ScriptArray.TryGetElementType(f.FieldType, out _, out _)) continue;
            if (!IsNestedSerializableType(f.FieldType)) continue;

            if (f.GetValue(instance) is { } child)
                EnsureScriptEventInstances(child, f.FieldType, depth + 1);
        }
    }

    /// <summary>
    /// 文字列値を対象フィールド型へ変換する（未対応型は null）。
    ///
    /// 配列フィールド（T[] / List&lt;T&gt;）は JSON 配列文字列を受け取り、
    /// 要素ごとに同じ変換規則を適用して実配列を組み立てる。
    /// 参照要素はここでは解決できない（World が必要）ため対象外
    /// （<see cref="ResolveReferenceFields"/> が担当する）。
    /// </summary>
    private static object? ConvertValue(Type type, string value)
    {
        // ScriptEvent フィールド: JSON 配列文字列 → ScriptEvent 実インスタンス。
        // World を必要としない即時経路（結線は実行時に名前で解決するため）なので、
        // NeedsDeferredReferenceResolution の対象にはしない。
        if (SEED.ScriptEvent.IsScriptEventType(type))
            return SEED.ScriptEvent.BuildInstance(value);

        // 列挙型フィールド: メンバ名の文字列 → 列挙型の値（大文字小文字は区別しない）。
        // 未知の名前・空文字は「変換失敗（null）」とし、呼び出し側が値の書き込みを
        // 見送ることで宣言時の初期値が保たれる（SetFieldByPath / BuildInstance を参照）。
        if (SEED.ScriptEnumField.IsEnumFieldType(type))
            return SEED.ScriptEnumField.TryParse(type, value, out var enumValue) ? enumValue : null;

        // 構造体配列フィールド: JSON オブジェクト配列文字列 → List<構造体> / 構造体[]
        // 参照メンバを含む構造体は World が必要なのでここでは扱わない
        //（NeedsDeferredReferenceResolution が真になり ResolveReferenceFields が担当する）。
        if (SEED.ScriptArray.TryGetElementType(type, out var structElem, out var structIsList) &&
            SEED.ScriptStructArray.TryGetLayout(structElem, out var structMembers))
        {
            return structMembers.Any(m => m.NeedsWorld)
                ? null
                : SEED.ScriptStructArray.BuildInstance(
                      structElem, structIsList, value, structMembers, ConvertValue);
        }

        // 配列フィールド: JSON 配列文字列 → T[] / List<T>
        if (SEED.ScriptArray.TryGetElementType(type, out var elementType, out var isList) &&
            SEED.ScriptArray.TryGetElementKind(elementType, out _, out var isRefElement) &&
            !isRefElement)
        {
            return SEED.ScriptArray.BuildInstance(elementType, isList, value, ConvertValue);
        }

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
