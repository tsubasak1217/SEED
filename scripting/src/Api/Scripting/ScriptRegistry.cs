using System;
using System.Runtime.InteropServices;
using SEEDEditor.Scripting;

namespace SEED.Scripting;

// ============================================================
//  ScriptRegistry.cs — スクリプトのインスタンスの登録簿のエンジン側の窓口（2026-10-03。docs/scripting_api.md §7「スクリプトを引く」）
//
//  本体（ScriptInstanceRegistry。Model/）を IScriptComponent で 1 つだけ持ち、
//    - ScriptBridge からの登録・解除・全消去（FFI の入口から呼ばれる）
//    - GameObject / SEEDScript の GetScript 系・Instances 系からの問い合わせ
//  の両方を受ける。ランタイム（Rust）は変えず、ScriptBridge の既存の入口で次の時点を拾う:
//    CreateComponent          … NoteCreated（インスタンスができた。生成の番号＝スロットの順の基）
//    ResolveReferenceFields   … Bind（持ち主のアクタが最初に分かる時点。BeginFrame のフェーズで OnStart の直前）
//    OnStart                  … Bind（同じ持ち主なら何もしない。参照の解決が走らない経路の保険）
//    OnDestroy の後            … Remove（OnDestroy の中ではまだ引ける）
//    DestroyComponent         … Remove（OnStart 前の破棄＝ OnDestroy が来ない経路もここで必ず外れる）
//    CompileScripts / LoadPrecompiledScripts* … ResetForReload（全消去）
//
//  まだ OnStart を迎えていないスクリプトをアクタから引くときは、[SerializeField] のスクリプト参照と同じ
//  ScriptHost.TryResolveScriptInstance（スロットを先頭から見て型の名前が一致する最初のもの）で問い合わせる。
// ============================================================

/// <summary>スクリプトのインスタンスの登録簿のエンジン側の窓口。</summary>
internal static class ScriptRegistry
{
    /// <summary>登録簿の本体（プロセスに 1 つ。CLR のメインスレッド専用）。</summary>
    private static readonly ScriptInstanceRegistry<IScriptComponent> Registry = new();

    /// <summary>まだ OnStart を迎えていないスクリプトをランタイムのスロットへ問い合わせる口。</summary>
    private static readonly ScriptSlotProbe<IScriptComponent> RuntimeSlotProbe = ProbeRuntimeSlot;

    // ── ScriptBridge から（登録・解除・全消去）────────────────────

    /// <summary>
    /// インスタンスができたことを記録する（ScriptBridge.CreateComponent。持ち主はまだ分からない）。
    /// </summary>
    /// <param name="instance">できたインスタンス。</param>
    internal static void NoteCreated(IScriptComponent instance) => Registry.NoteCreated(instance);

    /// <summary>
    /// インスタンスの持ち主のアクタを決める（ScriptBridge.ResolveReferenceFields / OnStart。OnStart の直前）。
    /// 持ち主が未束縛（ランタイムが u32::MAX で伝える＝ Entity.None）なら何もしない。
    /// </summary>
    /// <param name="instance">インスタンス（null なら何もしない）。</param>
    /// <param name="entityIndex">持ち主のアクタのルートのエンティティの index。</param>
    /// <param name="entityGeneration">同 generation。</param>
    internal static void Bind(IScriptComponent? instance, uint entityIndex, uint entityGeneration)
    {
        if (instance is null) return;
        var owner = new Entity(entityIndex, entityGeneration);
        if (!owner.IsValid) return;
        Registry.Bind(instance, KeyOf(owner));
    }

    /// <summary>
    /// インスタンスを登録簿から外す（ScriptBridge.OnDestroy の後・DestroyComponent。二重に呼んでも無害）。
    ///
    /// DestroyComponent は例外の受け止め（try/catch）を持たない FFI の入口なので、ここで受け止める
    /// （FFI の境界を例外が越えると CLR がプロセスを落とす）。
    /// </summary>
    /// <param name="instance">インスタンス（null なら何もしない）。</param>
    internal static void Remove(IScriptComponent? instance)
    {
        if (instance is null) return;
        try
        {
            Registry.Remove(instance);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[SEEDScripting] スクリプトの登録簿から外す処理で例外: {ex}");
        }
    }

    /// <summary>
    /// 全部を外す（スクリプトの読み直し。旧アセンブリのインスタンスと型を握ったままだとアンロードできない）。
    /// </summary>
    internal static void ResetForReload() => Registry.Clear();

    // ── GameObject / SEEDScript から（問い合わせ）────────────────────

    /// <summary>アクタのスクリプトのうち T に当たる最初のもの（スロットの順。OnStart 前のものも引く）。</summary>
    /// <typeparam name="T">探す型（派生型も当たる）。</typeparam>
    /// <param name="owner">アクタ（無効なら null）。</param>
    internal static T? FirstOn<T>(Entity owner) where T : class
        => owner.IsValid ? Registry.FirstOnOwner<T>(KeyOf(owner), RuntimeSlotProbe) : null;

    /// <summary>アクタのスクリプトのうち T に当たるもの全部（スロットの順。その時点の写し）。</summary>
    /// <typeparam name="T">探す型（派生型も当たる）。</typeparam>
    /// <param name="owner">アクタ（無効なら空の配列）。</param>
    internal static T[] AllOn<T>(Entity owner) where T : class
        => owner.IsValid ? Registry.AllOnOwner<T>(KeyOf(owner), RuntimeSlotProbe) : Array.Empty<T>();

    /// <summary>OnStart を迎えたインスタンスのうち T に当たる最初のもの（生成順）。</summary>
    /// <typeparam name="T">探す型（派生型も当たる）。</typeparam>
    internal static T? FirstInstance<T>() where T : class => Registry.FirstInstance<T>();

    /// <summary>OnStart を迎えたインスタンスのうち T に当たるもの全部（生成順。その時点の写し）。</summary>
    /// <typeparam name="T">探す型（派生型も当たる）。</typeparam>
    internal static T[] AllInstances<T>() where T : class => Registry.AllInstances<T>();

    // ── 内部用 ──────────────────────────────────────────────

    /// <summary>エンティティを登録簿の持ち主の鍵へ詰め替える。</summary>
    private static ScriptOwnerKey KeyOf(Entity owner) => new(owner.Index, owner.Generation);

    /// <summary>
    /// アクタのスロットに、指定した型の名前のスクリプトが居ればそのインスタンスを返す
    /// （[SerializeField] のスクリプト参照の解決と同じ引き方。World が公開されているフェーズの中だけ成功する）。
    /// </summary>
    /// <param name="owner">アクタ。</param>
    /// <param name="scriptType">探すスクリプトの型（ランタイムは型の名前＝ .cs のファイル名の語幹で照合する）。</param>
    private static IScriptComponent? ProbeRuntimeSlot(ScriptOwnerKey owner, Type scriptType)
    {
        var actor = new Entity(owner.Index, owner.Generation);
        if (!ScriptHost.TryResolveScriptInstance(actor, scriptType.Name, null, out var handle)) return null;
        try
        {
            return GCHandle.FromIntPtr(handle).Target as IScriptComponent;
        }
        catch (InvalidOperationException)
        {
            // 解放済み・不正なハンドル（ランタイムが 0 以外を返す限り起きない想定）
            return null;
        }
    }
}
