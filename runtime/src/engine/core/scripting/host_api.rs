// ============================================================
//  host_api.rs — C# スクリプト → Rust ECS のアクセスブリッジ
//
//  C# スクリプトが transform.Position などでコンポーネントを読み書きする際、
//  ここで定義した extern 関数（ScriptHostApi 表）が呼ばれる。
//
//  仕組み:
//   1. 起動時に ScriptingHost::install_host_api が C# の RegisterHostApi を呼び、
//      HOST_API（関数ポインタ表）を C# へ渡す。
//   2. スクリプト実行の直前、ScriptSystem が with_world で現在の World ポインタを
//      スレッドローカルに設定する（実行中だけ有効）。
//   3. C# からアクセサ関数が呼ばれると、スレッドローカルの World と (index,gen) から
//      対象コンポーネントを引き、レジストリ（read_vec3 / write_vec3）で読み書きする。
//
//  安全性: CLR は単一プロセス・メインスレッド専用（scripting/mod.rs 参照）。
//  World ポインタは with_world のスコープ内でのみ有効で、その間 Rust 側は World への
//  参照を保持しない（ScriptSystem は事前にハンドルを収集してから呼ぶ）ため、
//  可変アクセスが他の参照と衝突しない。
// ============================================================

use std::cell::Cell;

use crate::engine::components::Transform;
use crate::engine::ecs::{Entity, World};

// ─── スレッドローカル World ポインタ ──────────────────────────

thread_local! {
    /// スクリプト実行中だけ設定される現在の World への生ポインタ。
    static WORLD_PTR: Cell<*mut World> = const { Cell::new(std::ptr::null_mut()) };
}

/// World ポインタを設定してクロージャを実行し、終了後に元へ戻す。
///
/// ScriptSystem が「収集済みハンドルへのスクリプト呼び出し」をこの中で行うことで、
/// C# → Rust のアクセサから安全に World へ触れるようにする。
pub fn with_world<R>(world: &mut World, f: impl FnOnce() -> R) -> R {
    let prev = WORLD_PTR.with(|p| p.replace(world as *mut World));
    let result = f();
    WORLD_PTR.with(|p| p.set(prev));
    result
}

// ─── コンポーネントレジストリ ─────────────────────────────────
//  新しいコンポーネントをスクリプトへ公開するときは、ここに 1 分岐追加する
//  （docs/scripting_api.md・.claude/CLAUDE.md の手順を参照）。

/// コンポーネントの Vector3（[f32;3]）フィールドを読む。未対応は None。
fn read_vec3(world: &World, entity: Entity, component: &str, field: &str) -> Option<[f32; 3]> {
    match component {
        "Transform" => {
            let t = world.get::<Transform>(entity)?;
            match field {
                "position" => Some(t.position),
                "rotation" => Some(t.rotation),
                "scale"    => Some(t.scale),
                _          => None,
            }
        }
        _ => None,
    }
}

/// コンポーネントの Vector3（[f32;3]）フィールドへ書き込む。成功なら true。
fn write_vec3(world: &mut World, entity: Entity, component: &str, field: &str, v: [f32; 3]) -> bool {
    match component {
        "Transform" => {
            let Some(t) = world.get_mut::<Transform>(entity) else { return false };
            match field {
                "position" => { t.position = v; true }
                "rotation" => { t.rotation = v; true }
                "scale"    => { t.scale    = v; true }
                _          => false,
            }
        }
        _ => false,
    }
}

/// エンティティが指定コンポーネントを持つか。
fn has_component(world: &World, entity: Entity, component: &str) -> bool {
    match component {
        "Transform" => world.get::<Transform>(entity).is_some(),
        _ => false,
    }
}

// ─── FFI アクセサ（C# から呼ばれる）──────────────────────────

/// ポインタ＋長さから &str を復元する（不正 UTF-8 は空文字）。
unsafe fn str_from<'a>(ptr: *const u8, len: i32) -> &'a str {
    if ptr.is_null() || len <= 0 { return ""; }
    let bytes = std::slice::from_raw_parts(ptr, len as usize);
    std::str::from_utf8(bytes).unwrap_or("")
}

/// Vector3 フィールドを読む。成功=1 / 失敗=0（out に 3 要素を書き込む）。
unsafe extern "system" fn ffi_get_vec3(
    idx: u32, generation: u32,
    comp: *const u8, comp_len: i32,
    field: *const u8, field_len: i32,
    out: *mut f32,
) -> i32 {
    let ptr = WORLD_PTR.with(|p| p.get());
    if ptr.is_null() || out.is_null() { return 0; }
    let world = &*ptr;
    let entity = Entity::from_raw(idx, generation);
    match read_vec3(world, entity, str_from(comp, comp_len), str_from(field, field_len)) {
        Some(v) => { std::ptr::copy_nonoverlapping(v.as_ptr(), out, 3); 1 }
        None => 0,
    }
}

/// Vector3 フィールドへ書き込む。成功=1 / 失敗=0（inp は 3 要素）。
unsafe extern "system" fn ffi_set_vec3(
    idx: u32, generation: u32,
    comp: *const u8, comp_len: i32,
    field: *const u8, field_len: i32,
    inp: *const f32,
) -> i32 {
    let ptr = WORLD_PTR.with(|p| p.get());
    if ptr.is_null() || inp.is_null() { return 0; }
    let world = &mut *ptr;
    let entity = Entity::from_raw(idx, generation);
    let v = [*inp, *inp.add(1), *inp.add(2)];
    if write_vec3(world, entity, str_from(comp, comp_len), str_from(field, field_len), v) { 1 } else { 0 }
}

/// コンポーネント保持判定。持つ=1 / 持たない=0。
unsafe extern "system" fn ffi_has_component(
    idx: u32, generation: u32,
    comp: *const u8, comp_len: i32,
) -> i32 {
    let ptr = WORLD_PTR.with(|p| p.get());
    if ptr.is_null() { return 0; }
    let world = &*ptr;
    let entity = Entity::from_raw(idx, generation);
    if has_component(world, entity, str_from(comp, comp_len)) { 1 } else { 0 }
}

// ─── C# へ渡す関数ポインタ表 ─────────────────────────────────

/// C# の #[StructLayout(Sequential)] ScriptHostApi と同一レイアウト。
/// フィールド順・シグネチャを C# 側 ScriptHost.cs と必ず一致させること。
/// フィールドは Rust からは読まず C# へ渡す関数ポインタ表なので dead_code を許可する。
#[repr(C)]
#[allow(dead_code)]
pub struct ScriptHostApi {
    get_vec3: unsafe extern "system" fn(u32, u32, *const u8, i32, *const u8, i32, *mut f32) -> i32,
    set_vec3: unsafe extern "system" fn(u32, u32, *const u8, i32, *const u8, i32, *const f32) -> i32,
    has_component: unsafe extern "system" fn(u32, u32, *const u8, i32) -> i32,
}

// 関数ポインタは Sync。プロセス全体で 1 つの静的表を共有する。
static HOST_API: ScriptHostApi = ScriptHostApi {
    get_vec3:      ffi_get_vec3,
    set_vec3:      ffi_set_vec3,
    has_component: ffi_has_component,
};

/// C# へ渡す関数ポインタ表へのポインタを返す（RegisterHostApi 用）。
pub fn host_api_ptr() -> *const ScriptHostApi {
    &HOST_API
}
