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
//      対象コンポーネントを引き、レジストリ（read_floats / write_floats /
//      read_string / write_string）で読み書きする。
//
//  データ表現:
//   - 数値フィールドはすべて「float 配列（1〜4 要素）」で統一する。
//     f32 → 1 要素 / Vector2 → 2 / Vector3 → 3 / RGBA カラー → 4。
//     bool は 0.0 / 1.0、整数（解像度など）は f32 に変換して受け渡す
//     （u32 は 2^24 まで正確に表現できるため解像度用途では損失なし）。
//   - 文字列フィールド（テクスチャパスなど）は UTF-8 バイト列で受け渡す。
//
//  安全性: CLR は単一プロセス・メインスレッド専用（scripting/mod.rs 参照）。
//  World ポインタは with_world のスコープ内でのみ有効で、その間 Rust 側は World への
//  参照を保持しない（ScriptSystem は事前にハンドルを収集してから呼ぶ）ため、
//  可変アクセスが他の参照と衝突しない。
// ============================================================

use std::cell::Cell;

use crate::engine::components::{
    CameraComponent, CanvasTransform, SpriteComponent, Transform,
};
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

// ─── float 配列の定数 ────────────────────────────────────────

/// float フィールドの最大要素数（RGBA カラーの 4 要素が最大）。
pub const MAX_FLOAT_FIELD_LEN: usize = 4;

// ─── コンポーネントレジストリ ─────────────────────────────────
//  新しいコンポーネントをスクリプトへ公開するときは、read_floats / write_floats
//  （文字列フィールドがあれば read_string / write_string も）と has_component に
//  1 分岐ずつ追加する（docs/scripting_api.md・.claude/CLAUDE.md の手順を参照）。

/// コンポーネントの数値フィールドを読み、out に書いた要素数を返す。未対応は None。
///
/// out は MAX_FLOAT_FIELD_LEN 要素以上あること（呼び出し側 FFI が保証する）。
fn read_floats(
    world: &World, entity: Entity, component: &str, field: &str, out: &mut [f32],
) -> Option<usize> {
    // スライスを out へコピーして要素数を返すローカルヘルパ
    fn put(out: &mut [f32], v: &[f32]) -> Option<usize> {
        out[..v.len()].copy_from_slice(v);
        Some(v.len())
    }
    match component {
        // ── 3D トランスフォーム ──
        "Transform" => {
            let t = world.get::<Transform>(entity)?;
            match field {
                "position" => put(out, &t.position),
                "rotation" => put(out, &t.rotation),
                "scale"    => put(out, &t.scale),
                _          => None,
            }
        }
        // ── 2D キャンバストランスフォーム ──
        "CanvasTransform" => {
            let t = world.get::<CanvasTransform>(entity)?;
            match field {
                "position" => put(out, &t.position),
                "rotation" => put(out, &[t.rotation]),
                "scale"    => put(out, &t.scale),
                "pivot"    => put(out, &t.pivot),
                "anchor"   => put(out, &t.anchor),
                _          => None,
            }
        }
        // ── 2D スプライト ──
        "Sprite" => {
            let s = world.get::<SpriteComponent>(entity)?;
            match field {
                "color"  => put(out, &s.color),
                "width"  => put(out, &[s.width]),
                "height" => put(out, &[s.height]),
                _        => None,
            }
        }
        // ── 3D カメラ ──
        "Camera" => {
            let c = world.get::<CameraComponent>(entity)?;
            match field {
                "fov_y_deg"     => put(out, &[c.fov_y_deg]),
                "near"          => put(out, &[c.near]),
                "far"           => put(out, &[c.far]),
                "is_main"       => put(out, &[if c.is_main { 1.0 } else { 0.0 }]),
                "clear_color"   => put(out, &c.clear_color),
                "target_width"  => put(out, &[c.target_width as f32]),
                "target_height" => put(out, &[c.target_height as f32]),
                "bar_color"     => put(out, &c.bar_color),
                _               => None,
            }
        }
        _ => None,
    }
}

/// コンポーネントの数値フィールドへ書き込む。成功なら true。
///
/// v の要素数がフィールドの要素数と一致しない場合は失敗させる
/// （C# ラッパーの実装ミスを暗黙に丸めず検出するため）。
fn write_floats(
    world: &mut World, entity: Entity, component: &str, field: &str, v: &[f32],
) -> bool {
    // 要素数が一致する場合のみ固定長配列へコピーするローカルヘルパ
    fn take<const N: usize>(v: &[f32]) -> Option<[f32; N]> {
        v.try_into().ok()
    }
    match component {
        // ── 3D トランスフォーム ──
        "Transform" => {
            let Some(t) = world.get_mut::<Transform>(entity) else { return false };
            match field {
                "position" => take(v).map(|a| t.position = a).is_some(),
                "rotation" => take(v).map(|a| t.rotation = a).is_some(),
                "scale"    => take(v).map(|a| t.scale    = a).is_some(),
                _          => false,
            }
        }
        // ── 2D キャンバストランスフォーム ──
        "CanvasTransform" => {
            let Some(t) = world.get_mut::<CanvasTransform>(entity) else { return false };
            match field {
                "position" => take(v).map(|a| t.position = a).is_some(),
                "rotation" => take::<1>(v).map(|a| t.rotation = a[0]).is_some(),
                "scale"    => take(v).map(|a| t.scale  = a).is_some(),
                "pivot"    => take(v).map(|a| t.pivot  = a).is_some(),
                "anchor"   => take(v).map(|a| t.anchor = a).is_some(),
                _          => false,
            }
        }
        // ── 2D スプライト ──
        "Sprite" => {
            let Some(s) = world.get_mut::<SpriteComponent>(entity) else { return false };
            match field {
                "color"  => take(v).map(|a| s.color = a).is_some(),
                "width"  => take::<1>(v).map(|a| s.width  = a[0]).is_some(),
                "height" => take::<1>(v).map(|a| s.height = a[0]).is_some(),
                _        => false,
            }
        }
        // ── 3D カメラ ──
        "Camera" => {
            let Some(c) = world.get_mut::<CameraComponent>(entity) else { return false };
            match field {
                "fov_y_deg"     => take::<1>(v).map(|a| c.fov_y_deg = a[0]).is_some(),
                "near"          => take::<1>(v).map(|a| c.near = a[0]).is_some(),
                "far"           => take::<1>(v).map(|a| c.far  = a[0]).is_some(),
                "is_main"       => take::<1>(v).map(|a| c.is_main = a[0] != 0.0).is_some(),
                "clear_color"   => take(v).map(|a| c.clear_color = a).is_some(),
                "target_width"  => take::<1>(v).map(|a| c.target_width  = a[0].max(0.0) as u32).is_some(),
                "target_height" => take::<1>(v).map(|a| c.target_height = a[0].max(0.0) as u32).is_some(),
                "bar_color"     => take(v).map(|a| c.bar_color = a).is_some(),
                _               => false,
            }
        }
        _ => false,
    }
}

/// コンポーネントの文字列フィールドを読む。未対応は None。
fn read_string(world: &World, entity: Entity, component: &str, field: &str) -> Option<String> {
    match component {
        "Sprite" => {
            let s = world.get::<SpriteComponent>(entity)?;
            match field {
                "texture_path" => Some(s.texture_path.clone()),
                _              => None,
            }
        }
        _ => None,
    }
}

/// コンポーネントの文字列フィールドへ書き込む。成功なら true。
fn write_string(
    world: &mut World, entity: Entity, component: &str, field: &str, value: &str,
) -> bool {
    match component {
        "Sprite" => {
            let Some(s) = world.get_mut::<SpriteComponent>(entity) else { return false };
            match field {
                "texture_path" => { s.texture_path = value.to_string(); true }
                _              => false,
            }
        }
        _ => false,
    }
}

/// エンティティが指定コンポーネントを持つか。
fn has_component(world: &World, entity: Entity, component: &str) -> bool {
    match component {
        "Transform"       => world.get::<Transform>(entity).is_some(),
        "CanvasTransform" => world.get::<CanvasTransform>(entity).is_some(),
        "Sprite"          => world.get::<SpriteComponent>(entity).is_some(),
        "Camera"          => world.get::<CameraComponent>(entity).is_some(),
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

/// 数値フィールドを読む。out に書き込んだ要素数（1〜4）を返す。失敗=0。
/// cap は out の容量（要素数）。フィールドの要素数が cap を超える場合は失敗。
unsafe extern "system" fn ffi_get_floats(
    idx: u32, generation: u32,
    comp: *const u8, comp_len: i32,
    field: *const u8, field_len: i32,
    out: *mut f32, cap: i32,
) -> i32 {
    let ptr = WORLD_PTR.with(|p| p.get());
    if ptr.is_null() || out.is_null() || cap <= 0 { return 0; }
    let world = &*ptr;
    let entity = Entity::from_raw(idx, generation);
    let mut buf = [0.0f32; MAX_FLOAT_FIELD_LEN];
    match read_floats(world, entity, str_from(comp, comp_len), str_from(field, field_len), &mut buf) {
        Some(n) if n <= cap as usize => {
            std::ptr::copy_nonoverlapping(buf.as_ptr(), out, n);
            n as i32
        }
        _ => 0,
    }
}

/// 数値フィールドへ書き込む。成功=1 / 失敗=0（inp は count 要素）。
unsafe extern "system" fn ffi_set_floats(
    idx: u32, generation: u32,
    comp: *const u8, comp_len: i32,
    field: *const u8, field_len: i32,
    inp: *const f32, count: i32,
) -> i32 {
    let ptr = WORLD_PTR.with(|p| p.get());
    if ptr.is_null() || inp.is_null() || count <= 0 || count as usize > MAX_FLOAT_FIELD_LEN {
        return 0;
    }
    let world = &mut *ptr;
    let entity = Entity::from_raw(idx, generation);
    let v = std::slice::from_raw_parts(inp, count as usize);
    if write_floats(world, entity, str_from(comp, comp_len), str_from(field, field_len), v) { 1 } else { 0 }
}

/// 文字列フィールドを読む。UTF-8 バイト列の必要長を返す（失敗=-1）。
/// 必要長 <= cap のときだけ out へ書き込む（C# 側は不足時にバッファを広げて再呼び出しする）。
unsafe extern "system" fn ffi_get_string(
    idx: u32, generation: u32,
    comp: *const u8, comp_len: i32,
    field: *const u8, field_len: i32,
    out: *mut u8, cap: i32,
) -> i32 {
    let ptr = WORLD_PTR.with(|p| p.get());
    if ptr.is_null() { return -1; }
    let world = &*ptr;
    let entity = Entity::from_raw(idx, generation);
    match read_string(world, entity, str_from(comp, comp_len), str_from(field, field_len)) {
        Some(s) => {
            let bytes = s.as_bytes();
            if !out.is_null() && bytes.len() <= cap as usize {
                std::ptr::copy_nonoverlapping(bytes.as_ptr(), out, bytes.len());
            }
            bytes.len() as i32
        }
        None => -1,
    }
}

/// 文字列フィールドへ書き込む。成功=1 / 失敗=0（value は UTF-8 バイト列）。
unsafe extern "system" fn ffi_set_string(
    idx: u32, generation: u32,
    comp: *const u8, comp_len: i32,
    field: *const u8, field_len: i32,
    value: *const u8, value_len: i32,
) -> i32 {
    let ptr = WORLD_PTR.with(|p| p.get());
    if ptr.is_null() { return 0; }
    let world = &mut *ptr;
    let entity = Entity::from_raw(idx, generation);
    // value_len=0 は「空文字列を書く」として許容する（str_from が空文字を返す）
    let s = str_from(value, value_len);
    if write_string(world, entity, str_from(comp, comp_len), str_from(field, field_len), s) { 1 } else { 0 }
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
    get_floats:    unsafe extern "system" fn(u32, u32, *const u8, i32, *const u8, i32, *mut f32, i32) -> i32,
    set_floats:    unsafe extern "system" fn(u32, u32, *const u8, i32, *const u8, i32, *const f32, i32) -> i32,
    get_string:    unsafe extern "system" fn(u32, u32, *const u8, i32, *const u8, i32, *mut u8, i32) -> i32,
    set_string:    unsafe extern "system" fn(u32, u32, *const u8, i32, *const u8, i32, *const u8, i32) -> i32,
    has_component: unsafe extern "system" fn(u32, u32, *const u8, i32) -> i32,
}

// 関数ポインタは Sync。プロセス全体で 1 つの静的表を共有する。
static HOST_API: ScriptHostApi = ScriptHostApi {
    get_floats:    ffi_get_floats,
    set_floats:    ffi_set_floats,
    get_string:    ffi_get_string,
    set_string:    ffi_set_string,
    has_component: ffi_has_component,
};

/// C# へ渡す関数ポインタ表へのポインタを返す（RegisterHostApi 用）。
pub fn host_api_ptr() -> *const ScriptHostApi {
    &HOST_API
}
