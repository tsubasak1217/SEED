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
// スクリプト入力 API の ID ⇔ winit 型対応表
pub mod input_bridge;
// ControlPoint パス評価（時刻 → ワールド位置／進行方向）の純関数層
pub mod path_query;
pub use host_api::{
    with_world, with_actors, take_scene_commands, take_audio_commands,
    publish_input, publish_physics_sender, publish_canvas_mouse_position,
    advance_script_frame, with_on_destroy_guard,
    ScriptSceneCommand, ScriptAudioCommand,
};

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

// ─── 物理イベント種別（C# 側 ScriptBridge の定数と一致させる）───
pub const PHYSICS_EVENT_COLLISION_ENTER: i32 = 0;
pub const PHYSICS_EVENT_COLLISION_STAY:  i32 = 1;
pub const PHYSICS_EVENT_COLLISION_EXIT:  i32 = 2;
pub const PHYSICS_EVENT_TRIGGER_ENTER:   i32 = 3;
pub const PHYSICS_EVENT_TRIGGER_EXIT:    i32 = 4;
pub const PHYSICS_EVENT_TRIGGER_STAY:    i32 = 5;

// ─── ポインタイベント種別（キャンバス UI のマウス操作）───────────
//
// 物理イベントと同じ FFI 経路（RawPhysicsEvent / run_physics_event_raw）に相乗りする。
// 「自分 = イベント対象アクター」「相手 = 未使用（Entity.None）」で送る。
// C# 側 ScriptBridge の PointerEvent* 定数と一致させること。
/// カーソルがこのアクターへ入った最初のフレーム。
pub const POINTER_EVENT_ENTER: i32 = 6;
/// カーソルがこのアクターから出た最初のフレーム。
pub const POINTER_EVENT_EXIT:  i32 = 7;
/// このアクターの上で左ボタンが押された瞬間。
pub const POINTER_EVENT_DOWN:  i32 = 8;
/// このアクターの上で左ボタンが離された瞬間。
pub const POINTER_EVENT_UP:    i32 = 9;
/// 押下と解放が同一アクター上で完結した瞬間（Up の直後に送る）。
pub const POINTER_EVENT_CLICK: i32 = 10;

/// C# 側 NativePhysicsEvent と同じメモリレイアウト（#[repr(C)]）。
/// 物理イベント（衝突・トリガー）をスクリプトへ通知するときに渡す。
///
/// kind: 0=CollisionEnter / 1=CollisionStay / 2=CollisionExit /
///       3=TriggerEnter / 4=TriggerExit / 5=TriggerStay（C# 側 ScriptBridge と一致させる）
#[repr(C)]
pub(crate) struct RawPhysicsEvent {
    /// イベント種別（上記コメント参照）
    pub kind:             i32,
    /// 通知先スクリプトが乗るアクターのエンティティ（gameObject の束縛用）
    pub self_index:       u32,
    pub self_generation:  u32,
    /// 衝突相手アクターのエンティティ（u32::MAX = 不明）
    pub other_index:      u32,
    pub other_generation: u32,
}

// Windows x64 では "system" == "C" (cdecl) — C# の CallConvCdecl と一致する。
type CreateFn    = unsafe extern "system" fn(*const u8, i32) -> isize;
type DestroyFn   = unsafe extern "system" fn(isize);
type LifecycleFn = unsafe extern "system" fn(isize, *const RawFrameContext);
/// フレームコンテキストを持たない 1 回限りのライフサイクル通知（OnStart / OnDestroy）。
/// 引数は (ハンドル, 所有エンティティ index, 同 generation)。所有者未束縛時は index = u32::MAX。
/// ユーザー側メソッドは引数を取らないが、gameObject / transform を束縛するために
/// エンティティだけは C# へ渡す。
type InstanceEventFn = unsafe extern "system" fn(isize, u32, u32);
/// 物理イベント（衝突・トリガー）をスクリプトへ通知する。
type PhysicsEventFn = unsafe extern "system" fn(isize, *const RawPhysicsEvent);
/// アセットルート内の .cs を CLR 側でコンパイルする。戻り値はコンパイルされた型数（負値はエラー）。
type CompileFn   = unsafe extern "system" fn(*const u8, i32) -> i32;
/// スクリプトインスタンスの [SerializeField] フィールドに文字列値を設定する。
type SetFieldFn  = unsafe extern "system" fn(isize, *const u8, i32, *const u8, i32);
/// 保留中の [SerializeField] 参照フィールド（アクター名／スロット名の文字列）を
/// 実体ハンドルへ解決してスクリプトインスタンスへ注入する。
///
/// 解決には World と Actor ツリーが必要なため、**必ずスクリプトフェーズ実行中**
/// （`with_world` / `with_actors` でポインタが公開されている間）に呼ぶこと。
type ResolveRefsFn = unsafe extern "system" fn(isize);
/// 指定パスの [SerializeField] フィールドが参照フィールド型かを判定する。
/// リフレクションのみで World へアクセスしないため、フェーズ外でも呼べる。
/// 戻り値: 参照フィールドなら 1、それ以外は 0。
type IsRefFieldFn = unsafe extern "system" fn(isize, *const u8, i32) -> i32;
/// 指定パスの `[SerializeField, Bindable]` フィールドの**実行中の値**を float 配列で読む
/// （シェーダパラメータの `@ref` バインド。Phase W8.3）。
///
/// 引数: (ハンドル, フィールド名 UTF-8 ポインタ, その長さ, 書き込み先バッファ, バッファ容量)
/// 戻り値: 書き込んだ成分数（`float` なら 1、`Vector3` なら 3）。
///         フィールドが無い・`[Bindable]` が付いていない・型が非対応・
///         バッファが足りないときは **0**（＝解決失敗）。
///
/// ## フェーズ外から呼んでよい理由
/// リフレクションでインスタンスのフィールドを読むだけで、World・Actor ツリーへは
/// 一切触れない（`IsReferenceField` と同じ制約）。したがって描画準備中でも
/// エディタのインスペクタ更新中でも安全に呼べる。
type ReadFieldFloatsFn =
    unsafe extern "system" fn(isize, *const u8, i32, *mut f32, i32) -> i32;
/// 生成直後のスクリプトインスタンスから `[SerializeField]` フィールド定義
/// （パス・型タグ・既定値）を JSON 配列として書き出す。
///
/// 引数: (ハンドル, 書き込み先バッファ, バッファ容量バイト数)
/// 戻り値: 書き込んだバイト数。バッファ不足なら **必要バイト数の負値**、
///         ハンドル無効・例外時は 0。
///
/// ホットリロード時の「保存済みフィールド値の引き継ぎ」判定に使う。
/// リフレクションのみで World へ触れないため、フェーズ外でも安全に呼べる。
type DescribeFieldsFn = unsafe extern "system" fn(isize, *mut u8, i32) -> i32;
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
    /// 初回ライフサイクル（BeginFrame）の直前に 1 回だけ呼ぶ OnStart 通知
    pub on_start_fn:        InstanceEventFn,
    /// インスタンス破棄の直前に 1 回だけ呼ぶ OnDestroy 通知
    pub on_destroy_fn:      InstanceEventFn,
    pub begin_frame_fn:     LifecycleFn,
    pub early_update_fn:    LifecycleFn,
    pub update_fn:          LifecycleFn,
    pub constant_update_fn: LifecycleFn,
    pub late_update_fn:     LifecycleFn,
    pub render_fn:          LifecycleFn,
    pub end_frame_fn:       LifecycleFn,
    /// 物理イベント（衝突・トリガー）通知
    pub physics_event_fn:   PhysicsEventFn,
    pub(crate) compile_fn:   CompileFn,
    pub(crate) set_field_fn: SetFieldFn,
    /// 保留中の参照フィールドを解決・注入する（OnStart 直前にフェーズ内で呼ぶ）
    pub(crate) resolve_refs_fn: ResolveRefsFn,
    /// フィールドが参照フィールド型かの判定（アクタリネーム時の参照追従で使用）
    pub(crate) is_ref_field_fn: IsRefFieldFn,
    /// `[Bindable]` フィールドの実行中の値の読み取り（`@ref` バインドの解決で使用）
    pub(crate) read_field_floats_fn: ReadFieldFloatsFn,
    /// `[SerializeField]` フィールド定義のスナップショット取得
    /// （ホットリロード時の値引き継ぎで使用）
    pub(crate) describe_fields_fn: DescribeFieldsFn,
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
            on_start_fn:       get_fn!(fn(isize, u32, u32),                    pdcstr!("OnStart")),
            on_destroy_fn:     get_fn!(fn(isize, u32, u32),                    pdcstr!("OnDestroy")),
            begin_frame_fn:    get_fn!(fn(isize, *const RawFrameContext),      pdcstr!("BeginFrame")),
            early_update_fn:   get_fn!(fn(isize, *const RawFrameContext),      pdcstr!("EarlyUpdate")),
            update_fn:         get_fn!(fn(isize, *const RawFrameContext),      pdcstr!("Update")),
            constant_update_fn:get_fn!(fn(isize, *const RawFrameContext),      pdcstr!("ConstantUpdate")),
            late_update_fn:    get_fn!(fn(isize, *const RawFrameContext),      pdcstr!("LateUpdate")),
            render_fn:         get_fn!(fn(isize, *const RawFrameContext),      pdcstr!("Render")),
            end_frame_fn:      get_fn!(fn(isize, *const RawFrameContext),      pdcstr!("EndFrame")),
            physics_event_fn:  get_fn!(fn(isize, *const RawPhysicsEvent),      pdcstr!("OnPhysicsEvent")),
            compile_fn:        get_fn!(fn(*const u8, i32) -> i32,              pdcstr!("CompileScripts")),
            set_field_fn:      get_fn!(fn(isize, *const u8, i32, *const u8, i32), pdcstr!("SetFieldValue")),
            resolve_refs_fn:   get_fn!(fn(isize),                              pdcstr!("ResolveReferenceFields")),
            is_ref_field_fn:   get_fn!(fn(isize, *const u8, i32) -> i32,       pdcstr!("IsReferenceField")),
            read_field_floats_fn: get_fn!(fn(isize, *const u8, i32, *mut f32, i32) -> i32,
                                                                              pdcstr!("ReadFieldFloats")),
            describe_fields_fn: get_fn!(fn(isize, *mut u8, i32) -> i32,
                                                                              pdcstr!("DescribeSerializeFields")),
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
