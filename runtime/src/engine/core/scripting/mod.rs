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

use crate::engine::core::package_layout;
use crate::engine::ecs::Entity;

// C# → Rust のコンポーネントアクセスブリッジ
pub mod host_api;
// アクタ参照文字列（"./Child" / "../Sibling" / 絶対パス / 素の名前）のパス解決
pub mod actor_ref_path;
// スクリプト入力 API の ID ⇔ winit 型対応表
pub mod input_bridge;
// ControlPoint パス評価（時刻 → ワールド位置／進行方向）の純関数層
pub mod path_query;
// カメラのワールド→スクリーン射影（Camera.WorldToScreen / WorldToCanvas）の純関数層
pub mod camera_project;
// GameObject.Visible の set を遅延適用するまでの保留値テーブル
pub mod name_pending;
pub mod visible_pending;
// SCRIPT_DEBUG IPC → SEED.Debug.OnCommand の待ち行列
pub mod debug_command;
pub use host_api::{
    with_world, with_actors, take_scene_commands, take_audio_commands,
    publish_input, publish_physics_sender, publish_canvas_mouse_position,
    advance_script_frame, with_on_destroy_guard,
    take_cursor_lock_request, clear_cursor_lock_request,
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
    /// 時間スケール適用後のフレーム delta（秒）。C# の `Time.DeltaTime`。
    pub delta_time:        f32,
    /// 時間スケール適用後のゲーム内累計時間（秒）。C# の `Time.ElapsedTime`。
    pub anim_time:         f32,
    pub entity_index:      u32,
    pub entity_generation: u32,
    /// 時間スケール**未適用**のフレーム delta（秒）。C# の `Time.UnscaledDeltaTime`。
    ///
    /// 【末尾に追加する理由】既存フィールドの間へ挿入すると C# 側 NativeFrameContext の
    /// オフセットが全部ずれる。末尾追加なら旧フィールドの位置が変わらず、
    /// 万一片側のビルドが古くても既存フィールドは正しく読める（壊れ方が穏やか）。
    pub unscaled_delta_time: f32,
    /// 時間スケール**未適用**のゲーム内累計時間（秒）。C# の `Time.UnscaledElapsedTime`。
    pub unscaled_elapsed_time: f32,
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
            unscaled_delta_time:   ctx.unscaled_delta_time,
            unscaled_elapsed_time: ctx.unscaled_anim_time,
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
/// 事前コンパイル済みユーザースクリプト DLL をロードする（パッケージ版の経路）。
/// 引数は DLL パスの UTF-8 バイト列とその長さ。戻り値は解決可能になった型数（負値はエラー）。
type LoadPrecompiledFn = unsafe extern "system" fn(*const u8, i32) -> i32;
/// スクリプトインスタンスの [SerializeField] フィールドに文字列値を設定する。
type SetFieldFn  = unsafe extern "system" fn(isize, *const u8, i32, *const u8, i32);
/// 保留中の [SerializeField] 参照フィールド（アクタ参照文字列／スロット名）を
/// 実体ハンドルへ解決してスクリプトインスタンスへ注入する。
///
/// 引数は (ハンドル, 所有 entity index, 同 generation)。所有 entity は
/// 「自分のサブツリー優先」「`./Child` 相対指定」の基準として C# 側へ渡す
/// （未束縛のときは u32::MAX = C# の Entity.None）。
///
/// 解決には World と Actor ツリーが必要なため、**必ずスクリプトフェーズ実行中**
/// （`with_world` / `with_actors` でポインタが公開されている間）に呼ぶこと。
type ResolveRefsFn = unsafe extern "system" fn(isize, u32, u32);
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
/// `[Bindable]` メンバの**実行中の値**を、要求種別に合わせて読む（Text のプレースホルダ用）。
///
/// `ReadFieldFloats`（水面シェーダの `@ref` 専用）とは**別経路**である。
/// 違いは 3 点:
///   ① フィールドだけでなく、`[Bindable]` の**プロパティ・引数なしメソッド**も読める
///      （プロパティ／メソッドは `[SerializeField]` の併用が不要）
///   ② `string` を返せる（`{string}` プレースホルダ用）
///   ③ `int` を `f32` へ変換して受け付ける
///
/// 引数: (ハンドル, メンバ名 UTF-8 ポインタ, その長さ, 要求種別, 書き込み先, その容量)
/// 要求種別: 0 = 数値（`BindableValueType::F32`）/ 1 = 文字列（同 `Str`）。
///
/// ## ワイヤ表現（C# 側と一致必須）
/// 書き込み先は**バイト列 1 本**である。
///   ・数値   … `f32` をリトルエンディアン 4 バイト（`f32::from_le_bytes` で読む）
///   ・文字列 … UTF-8 バイト列
/// バッファを 1 本にまとめてあるのは、FFI の引数を **6 個以内**に収めるため
/// （`netcorehost` が使う `fn_ptr` クレートの `FnPtr` 実装が既定で 6 引数までしかない）。
///
/// 戻り値: `>= 0` … 成功（書き込んだバイト数。数値は必ず 4、空文字列は 0）
///         `-1`   … 解決失敗（メンバが無い・属性が無い・型不一致・例外）
///         `<= -2`… バッファ不足。必要バイト数 = `-(戻り値) - 1`
///
/// **メソッドは毎フレーム呼ばれる**（＝C# 側で副作用を書いてはならない契約）。
/// リフレクションのみで World へ触れないため、フェーズ外でも安全に呼べる。
type ReadBindableValueFn =
    unsafe extern "system" fn(isize, *const u8, i32, i32, *mut u8, i32) -> i32;
/// スクリプトが公開している `[Bindable]` メンバの一覧を JSON 配列で書き出す。
///
/// 引数: (ハンドル, 書き込み先バッファ, バッファ容量バイト数)
/// 戻り値: 書き込んだバイト数。バッファ不足なら **必要バイト数の負値**、
///         ハンドル無効・例外時は 0（`DescribeSerializeFields` と同じ規約）。
///
/// 形式: `[{"name":"Speed","type":"f32"},{"name":"Title","type":"str"}]`
/// インスペクタのバインド先候補列挙（GET_BINDABLE_SOURCES）でのみ使う。
type DescribeBindableMembersFn = unsafe extern "system" fn(isize, *mut u8, i32) -> i32;
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
    /// 事前コンパイル済みユーザースクリプト DLL のロード（パッケージ版の起動経路）
    pub(crate) load_precompiled_fn: LoadPrecompiledFn,
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
    /// `[Bindable]` メンバ（フィールド／プロパティ／引数なしメソッド）の値の読み取り
    /// （Text のプレースホルダ `{num}` / `{string}` の解決で使用）
    pub(crate) read_bindable_value_fn: ReadBindableValueFn,
    /// `[Bindable]` メンバ一覧の取得（インスペクタのバインド先候補列挙で使用）
    pub(crate) describe_bindable_members_fn: DescribeBindableMembersFn,
    register_host_api_fn:    RegisterHostApiFn,
}

// CLR は単一プロセスに紐付き、常にメインスレッドからのみアクセスする。
unsafe impl Send for ScriptingHost {}
unsafe impl Sync for ScriptingHost {}

impl ScriptingHost {
    /// 探索結果から CLR を初期化して ScriptingHost を構築する。
    ///
    /// ## シャドウコピーを掛ける／掛けないの判断
    /// 開発ビルド出力（`scripting/bin/...`）の DLL を直接ロードすると
    /// プロセス実行中ずっとファイルがロックされ、エディタ/VS からの再ビルドが
    /// 「別プロセスが使用中」で失敗する。そのため開発時だけ DLL 一式を
    /// プロセス専用のテンポラリへコピーし、そのコピーをロードする。
    ///
    /// 一方パッケージ版では DLL は `{exe のフォルダ}/bin/` にあり、そこには
    /// 同梱 .NET ランタイム（`bin/dotnet/`。実測 75 MB 超）も同居する。
    /// シャドウコピーはフォルダ直下の全ファイルを写すため、そのまま掛けると
    /// 起動のたびに配布物の副次ファイルをテンポラリへ複製することになる。
    /// 配布物は再ビルドされないのでロックしても実害が無く、コピーは不要。
    pub fn load(location: &ScriptingHostLocation) -> Result<Arc<Self>, Box<dyn std::error::Error>> {
        let dll_path = location.dll_path.as_path();

        // 開発ビルド出力のときだけシャドウコピー（失敗時は元のパスにフォールバック）
        let load_dll = if location.is_dev_build_output {
            Self::shadow_copy(dll_path).unwrap_or_else(|_| dll_path.to_path_buf())
        } else {
            dll_path.to_path_buf()
        };
        let config_path = load_dll.with_extension("runtimeconfig.json");

        // ── CLR（hostfxr）の探索先を決める ──
        // 実行ファイルの bin/ に dotnet/ を同梱していればそこを .NET ルートとして使い、
        // 無ければ PC にインストール済みの .NET を使う（従来どおり）。
        // どちらを使ったかは配布先での切り分けに直結するので必ず 1 行残す。
        let exe_dir = std::env::current_exe()
            .ok()
            .and_then(|exe| exe.parent().map(Path::to_path_buf));
        let bundled_root = bundled_dotnet_root(exe_dir.as_deref(), &|path| path.is_dir());

        let hostfxr = match &bundled_root {
            Some(root) => {
                eprintln!("[SEED] dotnet root: bundled {}", root.display());
                nethost::load_hostfxr_with_dotnet_root(PdCString::from_os_str(root.as_os_str())?)?
            }
            None => {
                eprintln!("[SEED] dotnet root: global");
                nethost::load_hostfxr()?
            }
        };

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
            load_precompiled_fn: get_fn!(fn(*const u8, i32) -> i32,            pdcstr!("LoadPrecompiledScripts")),
            set_field_fn:      get_fn!(fn(isize, *const u8, i32, *const u8, i32), pdcstr!("SetFieldValue")),
            resolve_refs_fn:   get_fn!(fn(isize, u32, u32),                    pdcstr!("ResolveReferenceFields")),
            is_ref_field_fn:   get_fn!(fn(isize, *const u8, i32) -> i32,       pdcstr!("IsReferenceField")),
            read_field_floats_fn: get_fn!(fn(isize, *const u8, i32, *mut f32, i32) -> i32,
                                                                              pdcstr!("ReadFieldFloats")),
            describe_fields_fn: get_fn!(fn(isize, *mut u8, i32) -> i32,
                                                                              pdcstr!("DescribeSerializeFields")),
            read_bindable_value_fn: get_fn!(fn(isize, *const u8, i32, i32, *mut u8, i32) -> i32,
                                                                              pdcstr!("ReadBindableValue")),
            describe_bindable_members_fn: get_fn!(fn(isize, *mut u8, i32) -> i32,
                                                                              pdcstr!("DescribeBindableMembers")),
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

    /// 事前コンパイル済みユーザースクリプト DLL をロードする（パッケージ版の起動経路）。
    ///
    /// パッケージ版にはソース（.cs）も Roslyn も同梱しないため、
    /// `compile_scripts` の代わりにこちらを呼ぶ。型解決に必要な
    /// 「ソース相対パス → 型名」の対応表は DLL 内に埋め込まれている。
    ///
    /// 戻り値は解決可能になったスクリプト型の数（負値はロード失敗）。
    pub fn load_precompiled_scripts(&self, dll_path: &Path) -> i32 {
        // C# 側は UTF-8 バイト列として受け取る。パスが UTF-8 で表現できない場合
        // （不正なサロゲート等）は失敗として扱う。
        let path_string = dll_path.to_string_lossy();
        let bytes = path_string.as_bytes();
        unsafe { (self.load_precompiled_fn)(bytes.as_ptr(), bytes.len() as i32) }
    }

    /// スクリプトホスト DLL の探索を行い、最初に見つかった候補を返す。
    ///
    /// 候補が 1 つも実在しない場合は「最後の候補」（＝パッケージ配置）を
    /// そのまま返す。呼び出し側は `dll_path.exists()` で存在を確かめること。
    pub fn resolve_dll_path() -> ScriptingHostLocation {
        let cwd     = std::env::current_dir().unwrap_or_default();
        let exe_dir = std::env::current_exe().ok().and_then(|p| p.parent().map(PathBuf::from));

        let candidates = scripting_host_dll_candidates(&cwd, exe_dir.as_deref());
        for candidate in &candidates {
            if candidate.dll_path.exists() {
                // 正規化できるなら正規化する（相対 ".." を含むパスをそのまま
                // hostfxr へ渡すと、環境によって解決に失敗することがある）
                let dll_path = candidate.dll_path.canonicalize()
                    .unwrap_or_else(|_| candidate.dll_path.clone());
                return ScriptingHostLocation {
                    dll_path,
                    is_dev_build_output: candidate.is_dev_build_output,
                };
            }
        }

        // 実在候補が無いときは最後の候補を返す（存在チェックは呼び出し側の責務）
        candidates.into_iter().next_back().unwrap_or_else(|| ScriptingHostLocation {
            dll_path: PathBuf::from(SCRIPTING_HOST_DLL_NAME),
            is_dev_build_output: false,
        })
    }
}

// ============================================================
//  スクリプトホスト DLL の探索（純関数層）
// ============================================================

/// スクリプトホスト DLL（SEEDScripting.dll）のファイル名。
pub const SCRIPTING_HOST_DLL_NAME: &str = "SEEDScripting.dll";

/// 事前コンパイル済みユーザースクリプト DLL のファイル名。
///
/// C# 側 `PrecompiledScriptArtifact.AssemblyFileName` および
/// エディタの `ScriptPackager` と同じ名前でなければならない。
pub const PRECOMPILED_SCRIPTS_DLL_NAME: &str = "SEEDUserScripts.dll";

/// 開発時のスクリプトホスト DLL の位置（ワーキングディレクトリ `runtime/` から見た相対）。
const DEV_SCRIPTING_HOST_RELATIVE_DIR: &str = "../scripting/bin/Debug/net9.0";

/// スクリプトホストが要求する .NET ランタイムの表示名（利用者向けの案内文で使う）。
///
/// 正典は `scripting/SEEDScripting.csproj` の `TargetFramework`（= `SEEDScripting.runtimeconfig.json`
/// の framework version）。ここは案内文に出す文字列でしかないので、
/// 判定には使わない（判定は hostfxr が runtimeconfig.json を読んで行う）。
pub const REQUIRED_DOTNET_RUNTIME_LABEL: &str = ".NET 9";

// ============================================================
//  同梱 .NET ランタイム（self-contained 配布）の探索
// ============================================================

/// 配布物の `bin/` 直下に置く、同梱 .NET ランタイムのフォルダ名。
///
/// エディタ側の `DotnetRuntimeBundler`（`editor/src/Packaging/Runtime/`）が
/// この名前で書き出す。両者を変えるときは必ず揃えること。
///
/// レイアウトは PC にインストールされる .NET と同じ形にする。
/// ```text
/// {exe のフォルダ}/bin/dotnet/host/fxr/<ver>/hostfxr.dll
/// {exe のフォルダ}/bin/dotnet/shared/Microsoft.NETCore.App/<ver>/*
/// ```
///
/// 手前の `bin/` 1 段は `core::package_layout::BIN_DIR_NAME` が持つ
/// （配布物のフォルダ構成の正典はそちら）。
pub const BUNDLED_DOTNET_ROOT_DIR: &str = "dotnet";

/// 同梱 .NET ルート配下の host フォルダ名（`<root>/host/fxr/<ver>/hostfxr.dll`）。
const BUNDLED_DOTNET_HOST_DIR: &str = "host";

/// 同梱 .NET ルート配下の fxr フォルダ名（hostfxr のバージョン別フォルダの親）。
const BUNDLED_DOTNET_FXR_DIR: &str = "fxr";

/// 同梱 .NET ランタイムのルートを決める【純関数】。
///
/// 判定は「`{exe のフォルダ}/bin/dotnet/host/fxr` が実在するか」の 1 点だけ。
/// `dotnet/` があってもバージョン別フォルダが無ければ hostfxr は見つからないので、
/// フォルダの存在ではなく **hostfxr の置き場**まで見る。
///
/// 実際のバージョン選択（`host/fxr/<ver>` のどれを使うか）は hostfxr 側の
/// 探索に任せる（nethost が同フォルダ内の最新版を選ぶ）。
///
/// # 引数
/// * `exe_dir`    - 実行ファイルのあるフォルダ（取得できなければ None）
/// * `dir_exists` - ディレクトリの存在判定（テストのために注入する）
///
/// # 戻り値
/// 同梱ランタイムを使うなら、その .NET ルートの絶対パス。使わないなら None。
pub(crate) fn bundled_dotnet_root(
    exe_dir:    Option<&Path>,
    dir_exists: &dyn Fn(&Path) -> bool,
) -> Option<PathBuf> {
    // 配布物の副次ファイルは exe 直下ではなく bin/ にまとめる（package_layout の構成）。
    let root = package_layout::bin_dir(exe_dir?).join(BUNDLED_DOTNET_ROOT_DIR);
    let fxr  = root.join(BUNDLED_DOTNET_HOST_DIR).join(BUNDLED_DOTNET_FXR_DIR);
    dir_exists(&fxr).then_some(root)
}

/// スクリプトホスト DLL の探索結果 1 件。
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ScriptingHostLocation {
    /// SEEDScripting.dll の位置。
    pub dll_path: PathBuf,
    /// 開発ビルド出力（エディタ/VS から再ビルドされうる場所）か。
    /// true のときだけロード前にテンポラリへシャドウコピーしてロックを避ける。
    pub is_dev_build_output: bool,
}

/// スクリプトホスト DLL の探索候補を優先順に列挙する【純関数】。
///
/// 1. 開発ビルド出力: `{cwd}/../scripting/bin/Debug/net9.0/SEEDScripting.dll`
///    （`cargo run` を `runtime/` で実行する開発時の配置）
/// 2. パッケージ配置: `{exe のフォルダ}/bin/SEEDScripting.dll`
///    （配布物。cwd はユーザーがどこから起動したかで変わるため exe 基準にする）
///
/// exe のフォルダが取得できない場合は 2 を省く（候補は 1 件だけになる）。
///
/// ## exe 直下を候補にしない理由
/// 配布物は「exe / assets.pak / bin / caches / logs / saved」だけが並ぶ構成に
/// 統一してある（`core::package_layout`）。exe 直下にも DLL を探しに行くと
/// 古い配置の残骸を拾って新旧のホストが混ざるため、候補から外す。
///
/// # 引数
/// * `cwd`     - カレントディレクトリ
/// * `exe_dir` - 実行ファイルのあるフォルダ（取得できなければ None）
pub(crate) fn scripting_host_dll_candidates(
    cwd: &Path,
    exe_dir: Option<&Path>,
) -> Vec<ScriptingHostLocation> {
    let mut candidates = Vec::new();

    candidates.push(ScriptingHostLocation {
        dll_path: cwd.join(DEV_SCRIPTING_HOST_RELATIVE_DIR).join(SCRIPTING_HOST_DLL_NAME),
        is_dev_build_output: true,
    });

    if let Some(dir) = exe_dir {
        candidates.push(ScriptingHostLocation {
            dll_path: package_layout::bin_dir(dir).join(SCRIPTING_HOST_DLL_NAME),
            is_dev_build_output: false,
        });
    }

    candidates
}

#[cfg(test)]
mod scripting_host_path_tests {
    use super::*;

    /// 開発ビルド出力が第 1 候補で、シャドウコピー対象として印が付くこと。
    #[test]
    fn dev_build_output_comes_first() {
        let cwd = Path::new("C:/proj/runtime");
        let exe = Path::new("C:/proj/runtime/target/debug");
        let candidates = scripting_host_dll_candidates(cwd, Some(exe));

        assert_eq!(candidates.len(), 2);
        assert_eq!(
            candidates[0].dll_path,
            Path::new("C:/proj/runtime/../scripting/bin/Debug/net9.0/SEEDScripting.dll")
        );
        assert!(candidates[0].is_dev_build_output, "開発ビルド出力の印が付いていない");
    }

    /// パッケージ配置の候補は cwd ではなく exe のフォルダの `bin/` になること。
    /// （配布物はショートカット等から起動され、cwd が別の場所になり得る）
    #[test]
    fn package_candidate_is_under_exe_bin_dir() {
        let cwd = Path::new("C:/somewhere/else");
        let exe = Path::new("D:/Games/MyGame");
        let candidates = scripting_host_dll_candidates(cwd, Some(exe));

        assert_eq!(candidates[1].dll_path, Path::new("D:/Games/MyGame/bin/SEEDScripting.dll"));
        assert!(
            !candidates[1].is_dev_build_output,
            "パッケージ配置をシャドウコピー対象にしてはいけない（同梱 .NET ごと複製されるため）"
        );
    }

    /// exe 直下は候補に入らないこと（古い配置の残骸を拾わないための回帰防止）。
    #[test]
    fn exe_dir_root_is_not_a_candidate() {
        let exe = Path::new("D:/Games/MyGame");
        let candidates = scripting_host_dll_candidates(Path::new("C:/somewhere/else"), Some(exe));

        assert!(
            !candidates.iter().any(|c| c.dll_path == exe.join(SCRIPTING_HOST_DLL_NAME)),
            "exe 直下が候補に残っている（bin/ へ一元化した意味が無くなる）"
        );
    }

    /// exe のフォルダが取れないときは開発候補だけになること。
    #[test]
    fn without_exe_dir_only_dev_candidate() {
        let candidates = scripting_host_dll_candidates(Path::new("C:/proj/runtime"), None);
        assert_eq!(candidates.len(), 1);
        assert!(candidates[0].is_dev_build_output);
    }

    /// 事前コンパイル DLL の名前が C# 側の規約と一致していること（名前の取り違え防止）。
    #[test]
    fn precompiled_dll_name_matches_contract() {
        assert_eq!(PRECOMPILED_SCRIPTS_DLL_NAME, "SEEDUserScripts.dll");
    }

    // ── 同梱 .NET ランタイムの検出 ──────────────────────────

    /// 存在判定を注入するヘルパ（引数のパスだけを「実在する」とみなす）。
    fn only_existing(expected: &Path) -> impl Fn(&Path) -> bool + '_ {
        move |path: &Path| path == expected
    }

    /// `{exe}/bin/dotnet/host/fxr` があれば、そのひとつ上（`{exe}/bin/dotnet`）を
    /// .NET ルートに選ぶこと。
    #[test]
    fn bundled_root_is_used_when_host_fxr_exists() {
        let exe = Path::new("D:/Games/MyGame");
        let fxr = exe.join("bin").join("dotnet").join("host").join("fxr");

        let root = bundled_dotnet_root(Some(exe), &only_existing(&fxr));
        assert_eq!(root, Some(exe.join("bin").join("dotnet")));
    }

    /// `bin/dotnet/` があっても `host/fxr` が無ければ同梱扱いにしないこと。
    /// （空フォルダや作りかけの配布物で hostfxr が見つからず起動失敗するのを避ける）
    #[test]
    fn bundled_root_is_ignored_without_host_fxr() {
        let exe = Path::new("D:/Games/MyGame");
        // bin/dotnet フォルダだけが実在する状況を作る
        let dotnet_only = exe.join("bin").join("dotnet");

        let root = bundled_dotnet_root(Some(exe), &only_existing(&dotnet_only));
        assert_eq!(root, None, "host/fxr が無いのに同梱ランタイムを使おうとしている");
    }

    /// 旧配置（`{exe}/dotnet/host/fxr`）は同梱扱いにしないこと。
    /// 古い配布物の残骸を新レイアウトの起動が拾わないための回帰防止。
    #[test]
    fn legacy_root_directly_under_exe_is_ignored() {
        let exe = Path::new("D:/Games/MyGame");
        let legacy_fxr = exe.join("dotnet").join("host").join("fxr");

        let root = bundled_dotnet_root(Some(exe), &only_existing(&legacy_fxr));
        assert_eq!(root, None, "exe 直下の旧 dotnet/ を拾っている");
    }

    /// exe のフォルダが取得できないときは同梱ランタイムを使わないこと。
    #[test]
    fn without_exe_dir_no_bundled_root() {
        // どのパスでも「実在する」と答える判定でも、exe が無ければ None
        let root = bundled_dotnet_root(None, &|_| true);
        assert_eq!(root, None);
    }

    /// 同梱フォルダ名がエディタ側（DotnetRuntimeBundler）の規約と一致していること。
    #[test]
    fn bundled_dotnet_dir_name_matches_contract() {
        assert_eq!(BUNDLED_DOTNET_ROOT_DIR, "dotnet");
    }
}
