using System;
using System.Runtime.InteropServices;
using System.Text;

namespace SEED;

/// <summary>
/// Rust ランタイムが渡してくる関数ポインタ表（<see cref="ScriptHostApi"/>）を保持し、
/// スクリプト側のコンポーネントアクセス（Transform など）を FFI 経由で仲介する。
///
/// Rust 側は起動時に一度だけ ScriptBridge.RegisterHostApi を呼び、
/// このクラスへアクセサ関数ポインタを登録する。登録前・非対応フィールドは失敗（既定値）扱い。
/// </summary>
public static unsafe class ScriptHost
{
    /// <summary>Rust から登録されたアクセサ関数ポインタ表。</summary>
    private static ScriptHostApi _api;
    /// <summary>ホスト API が登録済みか（未登録なら全アクセスが失敗する）。</summary>
    private static bool _available;

    /// <summary>Rust から関数ポインタ表を登録する（ScriptBridge.RegisterHostApi 経由）。</summary>
    internal static void Register(ScriptHostApi* api)
    {
        if (api == null) return;
        _api = *api;
        _available = true;
    }

    /// <summary>指定コンポーネントの Vector3 フィールドを読む。失敗時は false。</summary>
    public static bool TryGetVec3(Entity e, string component, string field, out Vector3 value)
    {
        value = Vector3.Zero;
        if (!_available || _api.GetVec3 == null || !e.IsValid) return false;

        int cl = Encoding.UTF8.GetByteCount(component);
        int fl = Encoding.UTF8.GetByteCount(field);
        Span<byte> cb = stackalloc byte[cl];
        Span<byte> fb = stackalloc byte[fl];
        Encoding.UTF8.GetBytes(component, cb);
        Encoding.UTF8.GetBytes(field, fb);

        float* v = stackalloc float[3];
        int ok;
        fixed (byte* cp = cb)
        fixed (byte* fp = fb)
            ok = _api.GetVec3(e.Index, e.Generation, cp, cl, fp, fl, v);

        if (ok == 0) return false;
        value = new Vector3(v[0], v[1], v[2]);
        return true;
    }

    /// <summary>指定コンポーネントの Vector3 フィールドへ書き込む。失敗時は false。</summary>
    public static bool TrySetVec3(Entity e, string component, string field, Vector3 value)
    {
        if (!_available || _api.SetVec3 == null || !e.IsValid) return false;

        int cl = Encoding.UTF8.GetByteCount(component);
        int fl = Encoding.UTF8.GetByteCount(field);
        Span<byte> cb = stackalloc byte[cl];
        Span<byte> fb = stackalloc byte[fl];
        Encoding.UTF8.GetBytes(component, cb);
        Encoding.UTF8.GetBytes(field, fb);

        float* v = stackalloc float[3];
        v[0] = value.x; v[1] = value.y; v[2] = value.z;

        int ok;
        fixed (byte* cp = cb)
        fixed (byte* fp = fb)
            ok = _api.SetVec3(e.Index, e.Generation, cp, cl, fp, fl, v);

        return ok != 0;
    }

    /// <summary>エンティティが指定コンポーネントを持つか。</summary>
    public static bool HasComponent(Entity e, string component)
    {
        if (!_available || _api.HasComponent == null || !e.IsValid) return false;

        int cl = Encoding.UTF8.GetByteCount(component);
        Span<byte> cb = stackalloc byte[cl];
        Encoding.UTF8.GetBytes(component, cb);
        fixed (byte* cp = cb)
            return _api.HasComponent(e.Index, e.Generation, cp, cl) != 0;
    }
}

/// <summary>
/// Rust の #[repr(C)] ScriptHostApi と同じレイアウトの関数ポインタ表。
/// フィールド順・シグネチャを Rust 側 host_api.rs と必ず一致させること。
/// すべて cdecl（Win64 では system == C == cdecl）。戻り値 int は成功=1/失敗=0。
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct ScriptHostApi
{
    /// <summary>(idx, gen, comp, compLen, field, fieldLen, out float[3]) → 1/0</summary>
    public delegate* unmanaged[Cdecl]<uint, uint, byte*, int, byte*, int, float*, int> GetVec3;
    /// <summary>(idx, gen, comp, compLen, field, fieldLen, in float[3]) → 1/0</summary>
    public delegate* unmanaged[Cdecl]<uint, uint, byte*, int, byte*, int, float*, int> SetVec3;
    /// <summary>(idx, gen, comp, compLen) → 1/0</summary>
    public delegate* unmanaged[Cdecl]<uint, uint, byte*, int, int> HasComponent;
}
