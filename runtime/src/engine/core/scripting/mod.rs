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

use crate::engine::ecs::Entity;

// C# → Rust のコンポーネントアクセスブリッジ
pub mod host_api;
pub use host_api::{with_world, with_actors, take_scene_commands, ScriptSceneCommand};

// ScriptComponent 等は engine::components から re-export する
pub use crate::engine::components::{
    ScriptComponent, PlaceholderScriptSlot, ScriptComponentData,
};

// ============================================================
//  FFI 型定義
// ============================================================

/// Rust 側の FrameContext と同じメモリレイアウト。
/// C# の NativeFrameContext と一致させること。
///
/// entity_index / entity_generation は、このスクリプトが乗る GameObject
/// （所有 Entity）を C# 側へ伝えるためのもの。所有者が未束縛のときは
/// entity_index = u32::MAX（C# の Entity.None に対応）。
#[repr(C)]
pub(crate) struct RawFrameContext {
    pub delta_time:        f32,
    pub anim_time:         f32,
    pub entity_index:      u32,
    pub entity_generation: u32,
}

impl RawFrameContext {
    /// フレーム時間と所有エンティティから生成する。
    pub fn new(ctx: &crate::engine::core::clock::FrameContext, owner: Option<Entity>) -> Self {
        let (entity_index, entity_generation) = match owner {
            Some(e) => (e.index(), e.generation()),
            None    => (u32::MAX, 0),
        };
        Self {
            delta_time: ctx.delta_time,
            anim_time:  ctx.anim_time,
            entity_index,
            entity_generation,
        }
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
/// コンポーネントアクセス用の関数ポインタ表（HOST_API）を C# へ登録する。
type RegisterHostApiFn = unsafe extern "system" fn(*const host_api::ScriptHostApi);

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
    register_host_api_fn:    RegisterHostApiFn,
}

// CLR は単一プロセスに紐付き、常にメインスレッドからのみアクセスする。
unsafe impl Send for ScriptingHost {}
unsafe impl Sync for ScriptingHost {}

impl ScriptingHost {
    /// DLL パスから CLR を初期化して ScriptingHost を構築する。
    ///
    /// ビルド出力の DLL を直接ロードするとプロセス実行中ずっとファイルが
    /// ロックされ、エディタ/VS からの再ビルドが「別プロセスが使用中」で
    /// 失敗する。これを避けるため、DLL 一式をプロセス専用のテンポラリ
    /// ディレクトリへシャドウコピーし、そのコピーをロードする。
    /// これによりビルド出力側は一切ロックされず、実行中でも再ビルドできる。
    pub fn load(dll_path: &Path) -> Result<Arc<Self>, Box<dyn std::error::Error>> {
        // シャドウコピー（失敗時は元のパスにフォールバック）
        let load_dll = Self::shadow_copy(dll_path).unwrap_or_else(|_| dll_path.to_path_buf());
        let config_path = load_dll.with_extension("runtimeconfig.json");

        let hostfxr = nethost::load_hostfxr()?;
        let context = hostfxr.initialize_for_runtime_config(
            PdCString::from_os_str(config_path.as_os_str())?,
        )?;

        let loader = context.get_delegate_loader_for_assembly(
            PdCString::from_os_str(load_dll.as_os_str())?,
        )?;

        macro_rules! get_fn {
            ($ty:ty, $method:expr) => {{
                *loader.get_function_with_unmanaged_callers_only::<$ty>(
                    pdcstr!("SEEDEditor.Scripting.ScriptBridge, SEEDScripting"),
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
            register_host_api_fn: get_fn!(fn(*const host_api::ScriptHostApi),  pdcstr!("RegisterHostApi")),
        }))
    }

    /// コンポーネントアクセス用の関数ポインタ表（HOST_API）を C# へ登録する。
    /// CLR ロード後に一度だけ呼ぶ。これ以降 transform.Position などのアクセスが有効になる。
    pub fn install_host_api(&self) {
        unsafe { (self.register_host_api_fn)(host_api::host_api_ptr()); }
    }

    /// DLL とその関連ファイル一式を、プロセス専用のテンポラリディレクトリへ
    /// コピーし、コピー後の DLL パスを返す。
    ///
    /// ビルド出力ディレクトリをロックしないためのシャドウコピー。
    /// hostfxr は runtimeconfig.json / deps.json / 依存 DLL を DLL と同じ
    /// フォルダから解決するため、ディレクトリ内の全ファイルをコピーする。
    fn shadow_copy(dll_path: &Path) -> std::io::Result<PathBuf> {
        use std::fs;

        let src_dir = dll_path.parent().ok_or_else(|| {
            std::io::Error::new(std::io::ErrorKind::NotFound, "DLL の親ディレクトリが取得できません")
        })?;
        let file_name = dll_path.file_name().ok_or_else(|| {
            std::io::Error::new(std::io::ErrorKind::NotFound, "DLL ファイル名が取得できません")
        })?;

        // プロセス ID 単位のシャドウディレクトリ（多重起動でも衝突しない）
        let shadow_dir = std::env::temp_dir()
            .join("SEED_scripting_shadow")
            .join(std::process::id().to_string());

        // 既存の残骸を掃除してから作り直す
        let _ = fs::remove_dir_all(&shadow_dir);
        fs::create_dir_all(&shadow_dir)?;

        // ソースディレクトリ直下の全ファイルをコピーする
        for entry in fs::read_dir(src_dir)? {
            let entry = entry?;
            if !entry.file_type()?.is_file() { continue; }
            let dst = shadow_dir.join(entry.file_name());
            fs::copy(entry.path(), dst)?;
        }

        Ok(shadow_dir.join(file_name))
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
