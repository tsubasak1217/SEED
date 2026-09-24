// ============================================================
//  clr_host/entry_points.rs — ScriptBridge（C#）のエントリポイントを取り出して ScriptingHost を組み立てる
//
//  【共通化の範囲】
//  CLR の起動（どの hostfxr を使い、SEEDScripting.dll をどう読み込むか）はプラットフォームで違うが、
//  「関数ポインタの取り出し口（loader）」を得た後は同じ。ここがその共通部分で、
//    デスクトップ … AssemblyDelegateLoader（SEEDScripting.dll をパスで専用の ALC へ読む。clr_host/desktop.rs）
//    同梱 .NET    … DelegateLoader（バイト列で Default ALC へ読んだ SEEDScripting から取る。clr_host/embedded.rs）
//  の両方から同じマクロで ScriptingHost を組み立てる。両方の loader は同じ名前・同じ形の
//  `get_function_with_unmanaged_callers_only::<F>(型名, メソッド名)` を持つ。
//
//  【マクロにしている理由】
//  get_function_with_unmanaged_callers_only の型引数の制約（fn_ptr::WithAbi）は netcorehost から
//  再公開されておらず、2 つの loader を 1 つのジェネリック関数で受けられないため
//  （依存を増やさないため fn_ptr クレートは直接使わない）。
//
//  【C# 側との契約】
//  メソッド名・引数の並びは scripting/src/ScriptBridge.cs の [UnmanagedCallersOnly] と一致させる。
//  ずれると取り出しに失敗して CLR の起動ごと失敗する（省略可能なものは bridge_fn_optional で取り、無ければ None）。
// ============================================================

use netcorehost::{pdcstr, pdcstring::PdCStr};

/// ScriptBridge（C#）の型のアセンブリ修飾名（`scripting/src/ScriptBridge.cs` の名前空間とアセンブリ名）。
///
/// `pdcstr!` は const の文脈で使えないため関数にしている。
pub(crate) fn bridge_type_name() -> &'static PdCStr {
    pdcstr!("SEEDEditor.Scripting.ScriptBridge, SEEDScripting")
}

/// 関数ポインタの取り出し口（loader）から全エントリポイントを取り出し、ScriptingHost を組み立てる。
///
/// 取り出せない関数があれば、呼び出し元の関数から `?` でエラーを返す
/// （呼び出し元の戻り値は `Result<_, Box<dyn std::error::Error>>`）。
///
/// # 引数
/// * `$context` - 初期化済みの CLR コンテキスト（ScriptingHost が保持して寿命を延ばす）
/// * `$loader`  - `get_function_with_unmanaged_callers_only` を持つ取り出し口（2 種類のどちらでもよい）
macro_rules! assemble_scripting_host {
    ($context:expr, $loader:expr) => {{
        use $crate::engine::core::scripting::clr_host::entry_points::bridge_type_name;
        use $crate::engine::core::scripting::{host_api::ScriptHostApi, RawFrameContext, RawPhysicsEvent};
        use netcorehost::pdcstr;

        let loader = &$loader;

        // 必須のエントリポイント（無ければ CLR の起動を失敗にする）。
        macro_rules! bridge_fn {
            ($ty:ty, $method:literal) => {
                *loader.get_function_with_unmanaged_callers_only::<$ty>(bridge_type_name(), pdcstr!($method))?
            };
        }
        // 省略可能なエントリポイント（古い SEEDScripting.dll に無いもの。無ければ None）。
        // Option の中では安全な fn → unsafe fn の暗黙の型変換が効かないので、`as _` で明示的に変換する。
        macro_rules! bridge_fn_optional {
            ($ty:ty, $method:literal) => {
                loader
                    .get_function_with_unmanaged_callers_only::<$ty>(bridge_type_name(), pdcstr!($method))
                    .ok()
                    .map(|function| *function as _)
            };
        }

        $crate::engine::core::scripting::ScriptingHost {
            _context:           $context,
            create_fn:          bridge_fn!(fn(*const u8, i32) -> isize,            "CreateComponent"),
            destroy_fn:         bridge_fn!(fn(isize),                              "DestroyComponent"),
            on_start_fn:        bridge_fn!(fn(isize, u32, u32),                    "OnStart"),
            on_destroy_fn:      bridge_fn!(fn(isize, u32, u32),                    "OnDestroy"),
            begin_frame_fn:     bridge_fn!(fn(isize, *const RawFrameContext),      "BeginFrame"),
            early_update_fn:    bridge_fn!(fn(isize, *const RawFrameContext),      "EarlyUpdate"),
            update_fn:          bridge_fn!(fn(isize, *const RawFrameContext),      "Update"),
            constant_update_fn: bridge_fn!(fn(isize, *const RawFrameContext),      "ConstantUpdate"),
            late_update_fn:     bridge_fn!(fn(isize, *const RawFrameContext),      "LateUpdate"),
            render_fn:          bridge_fn!(fn(isize, *const RawFrameContext),      "Render"),
            end_frame_fn:       bridge_fn!(fn(isize, *const RawFrameContext),      "EndFrame"),
            physics_event_fn:   bridge_fn!(fn(isize, *const RawPhysicsEvent),      "OnPhysicsEvent"),
            compile_fn:         bridge_fn!(fn(*const u8, i32) -> i32,              "CompileScripts"),
            load_precompiled_fn: bridge_fn!(fn(*const u8, i32) -> i32,             "LoadPrecompiledScripts"),
            load_precompiled_bytes_fn:
                bridge_fn_optional!(fn(*const u8, i32, *const u8, i32) -> i32,     "LoadPrecompiledScriptsFromBytes"),
            set_field_fn:       bridge_fn!(fn(isize, *const u8, i32, *const u8, i32), "SetFieldValue"),
            resolve_refs_fn:    bridge_fn!(fn(isize, u32, u32),                    "ResolveReferenceFields"),
            is_ref_field_fn:    bridge_fn!(fn(isize, *const u8, i32) -> i32,       "IsReferenceField"),
            read_field_floats_fn:
                bridge_fn!(fn(isize, *const u8, i32, *mut f32, i32) -> i32,        "ReadFieldFloats"),
            describe_fields_fn: bridge_fn!(fn(isize, *mut u8, i32) -> i32,         "DescribeSerializeFields"),
            read_bindable_value_fn:
                bridge_fn!(fn(isize, *const u8, i32, i32, *mut u8, i32) -> i32,    "ReadBindableValue"),
            describe_bindable_members_fn:
                bridge_fn!(fn(isize, *mut u8, i32) -> i32,                         "DescribeBindableMembers"),
            register_host_api_fn: bridge_fn!(fn(*const ScriptHostApi),             "RegisterHostApi"),
        }
    }};
}

pub(crate) use assemble_scripting_host;
