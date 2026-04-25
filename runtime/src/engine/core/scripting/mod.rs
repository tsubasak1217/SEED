use std::any::Any;
use std::path::{Path, PathBuf};
use std::sync::Arc;

use netcorehost::{nethost, pdcstr, pdcstring::PdCString};
use serde::{Deserialize, Serialize};

use crate::engine::core::clock::FrameContext;
use crate::engine::structs::components::{Component, ComponentData};

// ============================================================
//  FFI 型定義
// ============================================================

/// Rust 側の FrameContext と同じメモリレイアウト。
/// C# の NativeFrameContext と一致させること。
#[repr(C)]
struct RawFrameContext {
    delta_time: f32,
    anim_time:  f32,
}

impl From<&FrameContext> for RawFrameContext {
    fn from(ctx: &FrameContext) -> Self {
        Self { delta_time: ctx.delta_time, anim_time: ctx.anim_time }
    }
}

// Windows x64 では "system" == "C" (cdecl) — C# の CallConvCdecl と一致する。
type CreateFn    = unsafe extern "system" fn(*const u8, i32) -> isize;
type DestroyFn   = unsafe extern "system" fn(isize);
type LifecycleFn = unsafe extern "system" fn(isize, *const RawFrameContext);

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
        }))
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

// ============================================================
//  ScriptComponentData — シリアライズ用
// ============================================================

#[derive(Serialize, Deserialize, Clone)]
pub struct ScriptComponentData {
    pub type_name: String,
}

// ============================================================
//  PlaceholderScriptSlot — エディタ専用・CLR 不使用のスクリプトスロット
// ============================================================

pub struct PlaceholderScriptSlot {
    pub script_path: String,
}

impl Component for PlaceholderScriptSlot {
    fn as_any(&self)         -> &dyn Any     { self }
    fn as_any_mut(&mut self) -> &mut dyn Any { self }

    fn to_data(&self) -> ComponentData {
        ComponentData::ScriptComponent(ScriptComponentData {
            type_name: self.script_path.clone(),
        })
    }
}

// ============================================================
//  ScriptComponent — Rust Component トレイト実装
// ============================================================

pub struct ScriptComponent {
    host:      Arc<ScriptingHost>,
    handle:    isize,
    type_name: String,
}

impl ScriptComponent {
    pub fn new(host: Arc<ScriptingHost>, type_name: impl Into<String>) -> Option<Self> {
        let type_name = type_name.into();
        let bytes     = type_name.as_bytes();
        let handle = unsafe {
            (host.create_fn)(bytes.as_ptr(), bytes.len() as i32)
        };
        if handle == 0 { return None; }
        Some(Self { host, handle, type_name })
    }
}

impl Drop for ScriptComponent {
    fn drop(&mut self) {
        unsafe { (self.host.destroy_fn)(self.handle); }
    }
}

impl Component for ScriptComponent {
    fn as_any(&self)         -> &dyn Any     { self }
    fn as_any_mut(&mut self) -> &mut dyn Any { self }

    fn to_data(&self) -> ComponentData {
        ComponentData::ScriptComponent(ScriptComponentData {
            type_name: self.type_name.clone(),
        })
    }

    fn begin_frame(&mut self, ctx: &FrameContext) {
        let raw = RawFrameContext::from(ctx);
        unsafe { (self.host.begin_frame_fn)(self.handle, &raw); }
    }
    fn early_update(&mut self, ctx: &FrameContext) {
        let raw = RawFrameContext::from(ctx);
        unsafe { (self.host.early_update_fn)(self.handle, &raw); }
    }
    fn update(&mut self, ctx: &FrameContext) {
        let raw = RawFrameContext::from(ctx);
        unsafe { (self.host.update_fn)(self.handle, &raw); }
    }
    fn constant_update(&mut self, ctx: &FrameContext) {
        let raw = RawFrameContext::from(ctx);
        unsafe { (self.host.constant_update_fn)(self.handle, &raw); }
    }
    fn late_update(&mut self, ctx: &FrameContext) {
        let raw = RawFrameContext::from(ctx);
        unsafe { (self.host.late_update_fn)(self.handle, &raw); }
    }
    fn render(&mut self, ctx: &FrameContext) {
        let raw = RawFrameContext::from(ctx);
        unsafe { (self.host.render_fn)(self.handle, &raw); }
    }
    fn end_frame(&mut self, ctx: &FrameContext) {
        let raw = RawFrameContext::from(ctx);
        unsafe { (self.host.end_frame_fn)(self.handle, &raw); }
    }
}
