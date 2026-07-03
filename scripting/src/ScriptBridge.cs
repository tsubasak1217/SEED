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
