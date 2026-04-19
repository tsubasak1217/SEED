using System;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace SEED.Scripting;

/// <summary>
/// Rust から呼ばれるアンマネージドエントリポイント群。
/// すべて cdecl 呼び出し規約・静的メソッド。
/// GCHandle を isize として返すことでマネージドオブジェクトの寿命を管理する。
/// </summary>
public static unsafe class ScriptBridge
{
    // ─── インスタンス生成・破棄 ────────────────────────────────

    /// <summary>
    /// 型名（UTF-8）でスクリプトコンポーネントを生成し GCHandle を返す。
    /// 型名例: "SEED.Scripting.MyScript" または単純に "MyScript"
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
        GCHandle.FromIntPtr(handlePtr).Free();
    }

    // ─── ライフサイクル ───────────────────────────────────────

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static void BeginFrame(nint h, NativeFrameContext* ctx)
        => Get(h)?.BeginFrame(ref *ctx);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static void EarlyUpdate(nint h, NativeFrameContext* ctx)
        => Get(h)?.EarlyUpdate(ref *ctx);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static void Update(nint h, NativeFrameContext* ctx)
        => Get(h)?.Update(ref *ctx);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static void ConstantUpdate(nint h, NativeFrameContext* ctx)
        => Get(h)?.ConstantUpdate(ref *ctx);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static void LateUpdate(nint h, NativeFrameContext* ctx)
        => Get(h)?.LateUpdate(ref *ctx);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static void Render(nint h, NativeFrameContext* ctx)
        => Get(h)?.Render(ref *ctx);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static void EndFrame(nint h, NativeFrameContext* ctx)
        => Get(h)?.EndFrame(ref *ctx);

    // ─── 内部ヘルパー ─────────────────────────────────────────

    private static IScriptComponent? Get(nint h)
        => h == 0 ? null : (IScriptComponent?)GCHandle.FromIntPtr(h).Target;

    /// <summary>
    /// 全ロード済みアセンブリから型名で検索する。
    /// 完全修飾名 or 短縮名（クラス名のみ）の両方に対応。
    /// </summary>
    private static Type? FindType(string name)
        => AppDomain.CurrentDomain
            .GetAssemblies()
            .SelectMany(a => { try { return a.GetTypes(); } catch { return []; } })
            .FirstOrDefault(t =>
                t.FullName == name ||
                t.Name     == name);
}
