// ============================================================
//  scripting/mod.rs — CLR スクリプティングホスト
//
//  ScriptingHost は .NET CLR の生存期間を管理し、
//  C# スクリプトのライフサイクル関数ポインタを保持する。
//
//  ScriptComponent / PlaceholderScriptSlot / ScriptComponentData は
//  engine::components::script_component に移動済み。
// ============================================================

use std::path::{Path, PathBuf};
use std::sync::Arc;

use netcorehost::{nethost, pdcstr, pdcstring::PdCString};

// ScriptComponent 等は engine::components から re-export する
pub use crate::engine::components::{
    ScriptComponent, PlaceholderScriptSlot, ScriptComponentData,
};

// ============================================================
//  FFI 型定義
// ============================================================

/// Rust 側の FrameContext と同じメモリレイアウト。
/// C# の NativeFrameContext と一致させること。
#[repr(C)]
pub(crate) struct RawFrameContext {
    pub delta_time: f32,
    pub anim_time:  f32,
}

impl From<&crate::engine::core::clock::FrameContext> for RawFrameContext {
    fn from(ctx: &crate::engine::core::clock::FrameContext) -> Self {
        Self { delta_time: ctx.delta_time, anim_time: ctx.anim_time }
    }
}

// Windows x64 では "system" == "C" (cdecl) — C# の CallConvCdecl と一致する。
type CreateFn    = unsafe extern "system" fn(*const u8, i32) -> isize;
type DestroyFn   = unsafe extern "system" fn(isize);
type LifecycleFn = unsafe extern "system" fn(isize, *const RawFrameContext);
/// アセットルート内の .cs を CLR 側でコンパイルする。戻り値はコンパイルされた型数（負値はエラー）。
type CompileFn   = unsafe extern "system" fn(*const u8, i32) -> i32;
/// スクリプトインスタンスの [SerializeField] フィールドに文字列値を設定する。
type SetFieldFn  = unsafe extern "system" fn(isize, *const u8, i32, *const u8, i32);

// ============================================================
//  ScriptingHost — CLR ライフタイムと関数ポインタを保持
// ============================================================

pub struct ScriptingHost {
    // CLR が Drop されると全マネージドオブジェクトが無効になるため保持。
    _context: netcorehost::hostfxr::HostfxrContext<
        netcorehost::hostfxr::InitializedForRuntimeConfig,
    >,

    pub create_fn:          CreateFn,
    pub destroy_fn:         DestroyFn,
    pub begin_frame_fn:     LifecycleFn,
    pub early_update_fn:    LifecycleFn,
    pub update_fn:          LifecycleFn,
    pub constant_update_fn: LifecycleFn,
    pub late_update_fn:     LifecycleFn,
    pub render_fn:          LifecycleFn,
    pub end_frame_fn:       LifecycleFn,
    pub(crate) compile_fn:   CompileFn,
    pub(crate) set_field_fn: SetFieldFn,
}

// CLR は単一プロセスに紐付き、常にメインスレッドからのみアクセスする。
unsafe impl Send for ScriptingHost {}
unsafe impl Sync for ScriptingHost {}

impl ScriptingHost {
    /// DLL パスから CLR を初期化して ScriptingHost を構築する。
    pub fn load(dll_path: &Path) -> Result<Arc<Self>, Box<dyn std::error::Error>> {
        let config_path = dll_path.with_extension("runtimeconfig.json");

        let hostfxr = nethost::load_hostfxr()?;
        let context = hostfxr.initialize_for_runtime_config(
            PdCString::from_os_str(config_path.as_os_str())?,
        )?;

        let loader = context.get_delegate_loader_for_assembly(
            PdCString::from_os_str(dll_path.as_os_str())?,
        )?;

        macro_rules! get_fn {
            ($ty:ty, $method:expr) => {{
                *loader.get_function_with_unmanaged_callers_only::<$ty>(
                    pdcstr!("SEED.Scripting.ScriptBridge, SEEDScripting"),
                    $method,
                )?
            }};
        }

        Ok(Arc::new(Self {
            _context:          context,
            create_fn:         get_fn!(fn(*const u8, i32) -> isize,           pdcstr!("CreateComponent")),
            destroy_fn:        get_fn!(fn(isize),                              pdcstr!("DestroyComponent")),
            begin_frame_fn:    get_fn!(fn(isize, *const RawFrameContext),      pdcstr!("BeginFrame")),
            early_update_fn:   get_fn!(fn(isize, *const RawFrameContext),      pdcstr!("EarlyUpdate")),
            update_fn:         get_fn!(fn(isize, *const RawFrameContext),      pdcstr!("Update")),
            constant_update_fn:get_fn!(fn(isize, *const RawFrameContext),      pdcstr!("ConstantUpdate")),
            late_update_fn:    get_fn!(fn(isize, *const RawFrameContext),      pdcstr!("LateUpdate")),
            render_fn:         get_fn!(fn(isize, *const RawFrameContext),      pdcstr!("Render")),
            end_frame_fn:      get_fn!(fn(isize, *const RawFrameContext),      pdcstr!("EndFrame")),
            compile_fn:        get_fn!(fn(*const u8, i32) -> i32,              pdcstr!("CompileScripts")),
            set_field_fn:      get_fn!(fn(isize, *const u8, i32, *const u8, i32), pdcstr!("SetFieldValue")),
        }))
    }

    /// アセットルート配下の全 .cs スクリプトを CLR 側でコンパイル（再コンパイル）する。
    ///
    /// C# 側は collectible AssemblyLoadContext に読み込むため、
    /// 再呼び出しで旧アセンブリはアンロードされる（＝ホットリロード）。
    /// 呼び出し前に既存の ScriptComponent をすべて破棄しておくこと。
    /// 戻り値はコンパイルされたスクリプト型の数（負値はコンパイル失敗）。
    pub fn compile_scripts(&self, assets_root: &str) -> i32 {
        let bytes = assets_root.as_bytes();
        unsafe { (self.compile_fn)(bytes.as_ptr(), bytes.len() as i32) }
    }

    /// ワーキングディレクトリを基準に DLL を探す。
    pub fn resolve_dll_path() -> PathBuf {
        let base = std::env::current_dir().unwrap_or_default();
        let dev = base.join("../scripting/bin/Debug/net9.0/SEEDScripting.dll");
        if dev.exists() {
            return dev.canonicalize().unwrap_or(dev);
        }
        base.join("SEEDScripting.dll")
    }
}
