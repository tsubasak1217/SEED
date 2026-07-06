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

use std::cell::{Cell, RefCell};

use crate::engine::components::{
    CameraComponent, CanvasTransform, SpriteComponent, Transform,
};
use crate::engine::core::input::{Input, InputState};
use crate::engine::ecs::{Entity, World};
use crate::engine::structs::objects::Actor;

use super::input_bridge;

// ─── スレッドローカル World ポインタ ──────────────────────────

thread_local! {
    /// スクリプト実行中だけ設定される現在の World への生ポインタ。
    static WORLD_PTR: Cell<*mut World> = const { Cell::new(std::ptr::null_mut()) };

    /// フェーズ実行中だけ設定される Actor ツリー（ルート一覧）への生ポインタ。
    /// Find（名前検索）が Actor 名を参照するために使う読み取り専用ポインタ。
    /// フェーズ中に Actor ツリーは構造変更されない（変更はフェーズ後のコマンド適用で行う）。
    static ACTORS_PTR: Cell<*const Vec<Actor>> = const { Cell::new(std::ptr::null()) };

    /// スクリプトが発行したシーン操作コマンドのキュー。
    /// Instantiate / Destroy は Actor ツリーと DrawContext を要するため即時実行せず、
    /// ここへ積んで App がフレームのゲームロジック後に適用する（Unity の遅延 Destroy と同様）。
    static SCENE_COMMANDS: RefCell<Vec<ScriptSceneCommand>> = const { RefCell::new(Vec::new()) };

    /// ゲームロジック実行中だけ設定される Input への読み取り専用ポインタ。
    /// スクリプトの Input API（キー・マウス判定）が参照する。
    /// 入力イベントの処理はフレームロジック外（イベントハンドラ）で行われるため、
    /// 公開中に Input が変更されることはない。
    static INPUT_PTR: Cell<*const Input> = const { Cell::new(std::ptr::null()) };

    /// ゲームロジック実行中だけ設定される物理スレッドへのコマンド送信チャンネル。
    /// スクリプトの Physics.Raycast（同期問い合わせ）が使用する。
    static PHYSICS_TX: RefCell<Option<crossbeam_channel::Sender<crate::engine::physics::PhysicsCommand>>>
        = const { RefCell::new(None) };
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

/// Actor ツリーへの読み取り専用ポインタを設定してクロージャを実行し、終了後に元へ戻す。
///
/// Scene::run_phase がフェーズ実行を包むことで、C# の Find（名前検索）から
/// Actor 名を参照できるようにする。World とは別フィールドのため借用は競合しない。
pub fn with_actors<R>(actors: &Vec<Actor>, f: impl FnOnce() -> R) -> R {
    let prev = ACTORS_PTR.with(|p| p.replace(actors as *const Vec<Actor>));
    let result = f();
    ACTORS_PTR.with(|p| p.set(prev));
    result
}

/// ゲームロジック開始前に Input への読み取り専用ポインタを公開する（None で解除）。
///
/// frame_renderer がフェーズ群の実行前に Some、実行後に None を渡す。
/// スクリプトの Input API はこのポインタ経由でキー・マウス状態を参照する。
pub fn publish_input(input: Option<&Input>) {
    INPUT_PTR.with(|p| p.set(match input {
        Some(i) => i as *const Input,
        None    => std::ptr::null(),
    }));
}

/// ゲームロジック開始前に物理スレッドへのコマンド送信チャンネルを公開する（None で解除）。
///
/// frame_renderer がフェーズ群の実行前に Some、実行後に None を渡す。
/// スクリプトの Physics.Raycast はこのチャンネル経由で物理スレッドへ同期問い合わせする。
pub fn publish_physics_sender(
    tx: Option<crossbeam_channel::Sender<crate::engine::physics::PhysicsCommand>>,
) {
    PHYSICS_TX.with(|p| *p.borrow_mut() = tx);
}

// ─── シーン操作コマンド（遅延適用）──────────────────────────

/// スクリプトが発行するシーン構造の変更コマンド。
///
/// フェーズ実行中は Actor ツリーを直接変更できないため、
/// App::apply_script_scene_commands がゲームロジック後にまとめて適用する。
pub enum ScriptSceneCommand {
    /// .actor ファイルをシーンへ生成する。
    /// entity は ffi_instantiate が予約済みのルートエンティティ
    /// （デフォルト Transform 挿入済みで、スクリプトは即座に Position を設定できる）。
    Instantiate { path: String, entity: Entity },
    /// 指定ルートエンティティの Actor をシーンから破棄する。
    Destroy { entity: Entity },
    /// .scene ファイルへシーン全体を切り替える（シーン遷移）。
    /// このコマンドが存在するフレームでは他の全コマンドが破棄される
    /// （旧ワールドのエンティティ参照が新ワールドで別実体を指す危険を防ぐため）。
    LoadScene { path: String },
}

/// 積まれたシーン操作コマンドを取り出す（キューは空になる）。
/// App がフレームのゲームロジック後に呼び、順番に適用する。
pub fn take_scene_commands() -> Vec<ScriptSceneCommand> {
    SCENE_COMMANDS.with(|q| std::mem::take(&mut *q.borrow_mut()))
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

// ─── シーン操作 FFI（Instantiate / Destroy / Find）───────────

/// .actor ファイルからアクターを生成する。成功=1 / 失敗=0。
///
/// ルートエンティティを即座に予約してデフォルト Transform を挿入し、
/// out（[index, generation] の 2 要素）へ返す。これによりスクリプトは
/// 戻り値の GameObject に対して同フレーム中に Position 等を設定できる。
/// アクター本体（モデル・スプライト・子など）の構築はフレームのゲームロジック後に
/// 遅延適用される（読み込み失敗時は予約エンティティごと破棄される）。
///
/// 注意: 2D アクター（Actor2D）の場合、遅延適用時に仮の Transform は
/// CanvasTransform へ差し替えられるため、生成直後の 3D Position 設定は反映されない。
unsafe extern "system" fn ffi_instantiate(
    path: *const u8, path_len: i32,
    out: *mut u32,
) -> i32 {
    let ptr = WORLD_PTR.with(|p| p.get());
    if ptr.is_null() || out.is_null() { return 0; }
    let path_str = str_from(path, path_len);
    if path_str.is_empty() { return 0; }

    // ルートエンティティを予約し、スクリプトが即座に位置設定できるよう
    // デフォルト Transform を挿入する（2D の場合は適用時に差し替える）
    let world = &mut *ptr;
    let entity = world.spawn();
    world.insert(entity, Transform::default());

    SCENE_COMMANDS.with(|q| q.borrow_mut().push(ScriptSceneCommand::Instantiate {
        path:   path_str.to_string(),
        entity,
    }));

    *out         = entity.index();
    *out.add(1)  = entity.generation();
    1
}

/// 指定ルートエンティティのアクターを破棄する。受理=1 / 失敗=0。
///
/// 実際の破棄はフレームのゲームロジック後に遅延適用される
/// （実行中スクリプトの巻き添えを防ぐため。Unity の Destroy と同じ考え方）。
unsafe extern "system" fn ffi_destroy(idx: u32, generation: u32) -> i32 {
    let ptr = WORLD_PTR.with(|p| p.get());
    if ptr.is_null() { return 0; }
    let world = &*ptr;
    let entity = Entity::from_raw(idx, generation);
    // 生存確認: 予約済み Transform か CanvasTransform を持つものだけ受理する
    if world.get::<Transform>(entity).is_none()
        && world.get::<CanvasTransform>(entity).is_none()
    {
        return 0;
    }
    SCENE_COMMANDS.with(|q| q.borrow_mut().push(ScriptSceneCommand::Destroy { entity }));
    1
}

/// シーン全体を .scene ファイルへ切り替える（シーン遷移）。受理=1 / 失敗=0。
///
/// 実際の切り替えはフレームのゲームロジック後に遅延適用される。
/// 同フレームに積まれた他のシーン操作コマンドはすべて破棄される。
unsafe extern "system" fn ffi_load_scene(path: *const u8, path_len: i32) -> i32 {
    let path_str = str_from(path, path_len);
    if path_str.is_empty() { return 0; }
    SCENE_COMMANDS.with(|q| q.borrow_mut().push(ScriptSceneCommand::LoadScene {
        path: path_str.to_string(),
    }));
    1
}

/// アクターを名前で検索する（DFS 順の最初の一致）。見つかった=1 / なし=0。
/// out（[index, generation] の 2 要素）へルートエンティティを返す。
unsafe extern "system" fn ffi_find_actor(
    name: *const u8, name_len: i32,
    out: *mut u32,
) -> i32 {
    let actors_ptr = ACTORS_PTR.with(|p| p.get());
    if actors_ptr.is_null() || out.is_null() { return 0; }
    let name_str = str_from(name, name_len);
    if name_str.is_empty() { return 0; }

    /// Actor ツリーを DFS で走査し、名前一致の最初のエンティティを返すローカル関数
    fn walk(actors: &[Actor], name: &str) -> Option<Entity> {
        for a in actors {
            if a.name == name { return Some(a.entity); }
            if let Some(e) = walk(a.children(), name) { return Some(e); }
        }
        None
    }

    let actors = &*actors_ptr;
    match walk(actors, name_str) {
        Some(e) => {
            *out        = e.index();
            *out.add(1) = e.generation();
            1
        }
        None => 0,
    }
}

// ─── 入力 FFI（キー・マウス）─────────────────────────────────

/// キー入力判定。押されている(kind に応じた状態)=1 / それ以外・失敗=0。
///
/// kind: 0=押下中(press) / 1=押した瞬間(trigger) / 2=離した瞬間(release)。
/// key_id は C# 側 SEED.KeyCode の数値（input_bridge の対応表で変換）。
unsafe extern "system" fn ffi_input_key(kind: i32, key_id: u32) -> i32 {
    let ptr = INPUT_PTR.with(|p| p.get());
    if ptr.is_null() { return 0; }
    let input = &*ptr;
    let Some(key) = input_bridge::keycode_from_id(key_id) else { return 0 };
    let hit = match kind {
        input_bridge::INPUT_KIND_PRESS   => input.is_press_key(key),
        input_bridge::INPUT_KIND_TRIGGER => input.is_trigger_key(key),
        input_bridge::INPUT_KIND_RELEASE => input.is_release_key(key),
        _ => false,
    };
    if hit { 1 } else { 0 }
}

/// マウスボタン入力判定。判定は ffi_input_key と同じ kind 体系。
/// button_id は C# 側 SEED.MouseButton の数値（0=左 / 1=右 / 2=中）。
unsafe extern "system" fn ffi_input_mouse_button(kind: i32, button_id: u32) -> i32 {
    let ptr = INPUT_PTR.with(|p| p.get());
    if ptr.is_null() { return 0; }
    let input = &*ptr;
    let Some(button) = input_bridge::mouse_button_from_id(button_id) else { return 0 };
    let hit = match kind {
        input_bridge::INPUT_KIND_PRESS   => input.is_press_mouse(button),
        input_bridge::INPUT_KIND_TRIGGER => input.is_trigger_mouse(button),
        input_bridge::INPUT_KIND_RELEASE => input.is_release_mouse(button),
        _ => false,
    };
    if hit { 1 } else { 0 }
}

/// マウス状態（座標・移動量・ホイール）を取得する。out へ書いた要素数を返す（失敗=0）。
///
/// kind: 0=スクリーン座標(2要素) / 1=相対移動量(2要素) / 2=ホイール量(1要素)。
/// out は 2 要素以上の容量を C# 側が保証する。
unsafe extern "system" fn ffi_input_mouse_state(kind: i32, out: *mut f32) -> i32 {
    let ptr = INPUT_PTR.with(|p| p.get());
    if ptr.is_null() || out.is_null() { return 0; }
    let input = &*ptr;
    match kind {
        input_bridge::MOUSE_STATE_POSITION => {
            let v = input.mouse_position(InputState::Current);
            *out        = v.x;
            *out.add(1) = v.y;
            2
        }
        input_bridge::MOUSE_STATE_DELTA => {
            let v = input.mouse_vector(InputState::Current);
            *out        = v.x;
            *out.add(1) = v.y;
            2
        }
        input_bridge::MOUSE_STATE_SCROLL => {
            *out = input.mouse_scroll(InputState::Current);
            1
        }
        _ => 0,
    }
}

// ─── 物理 FFI（Raycast）──────────────────────────────────────

/// レイキャストのタイムアウト（ミリ秒）。物理スレッドのコマンドドレインは
/// 約 1ms 間隔なので通常は数 ms で応答する。応答がない場合はミス扱い。
const RAYCAST_TIMEOUT_MS: u64 = 20;

/// Play シーンの world_line（物理の DFS 順 ID はこの世界線の走査で振られる。
/// スクリプトは Play モードでのみ実行されるため常に 0）。
const PLAY_WORLD_LINE: u32 = 0;

/// DFS 順 ID からアクターのルートエンティティを逆引きする。
///
/// 走査順は physics_ops::collect_physics_objects と同一
/// （world_line 一致のルートから先行順 DFS、ID は 1 始まり・全アクターがカウント対象）。
fn entity_by_dfs_id(actors: &[Actor], target: u64) -> Option<Entity> {
    fn walk(actors: &[Actor], counter: &mut u64, target: u64) -> Option<Entity> {
        for a in actors {
            *counter += 1;
            if *counter == target { return Some(a.entity); }
            if let Some(e) = walk(a.children(), counter, target) { return Some(e); }
        }
        None
    }
    let roots: Vec<&Actor> = actors.iter().filter(|a| a.world_line == PLAY_WORLD_LINE).collect();
    let mut counter = 0u64;
    for root in roots {
        counter += 1;
        if counter == target { return Some(root.entity); }
        if let Some(e) = walk(root.children(), &mut counter, target) { return Some(e); }
    }
    None
}

/// レイキャストを実行する。ヒット=1 / ミス・失敗=0。
///
/// origin / direction は各 3 要素。ヒット時は out_hit へ
/// [point.x, point.y, point.z, normal.x, normal.y, normal.z, distance] の 7 要素、
/// out_entity へ [index, generation] の 2 要素を書き込む
/// （ヒットしたアクターが逆引きできない場合は u32::MAX, 0）。
unsafe extern "system" fn ffi_raycast(
    origin: *const f32, direction: *const f32, max_distance: f32,
    out_hit: *mut f32, out_entity: *mut u32,
) -> i32 {
    use crate::engine::physics::PhysicsCommand;

    if origin.is_null() || direction.is_null() || out_hit.is_null() || out_entity.is_null() {
        return 0;
    }
    // 物理スレッドが起動していなければミス扱い
    let Some(tx) = PHYSICS_TX.with(|p| p.borrow().clone()) else { return 0 };

    // 同期問い合わせ: 応答用の 1 要素チャンネルを添えてコマンドを送る
    let (reply_tx, reply_rx) = crossbeam_channel::bounded(1);
    let cmd = PhysicsCommand::Raycast {
        origin:       [*origin, *origin.add(1), *origin.add(2)],
        direction:    [*direction, *direction.add(1), *direction.add(2)],
        max_distance,
        reply:        reply_tx,
    };
    if tx.send(cmd).is_err() { return 0; }

    let hit = match reply_rx.recv_timeout(std::time::Duration::from_millis(RAYCAST_TIMEOUT_MS)) {
        Ok(Some(hit)) => hit,
        _             => return 0,
    };

    // ヒット情報を書き込む
    *out_hit        = hit.point[0];
    *out_hit.add(1) = hit.point[1];
    *out_hit.add(2) = hit.point[2];
    *out_hit.add(3) = hit.normal[0];
    *out_hit.add(4) = hit.normal[1];
    *out_hit.add(5) = hit.normal[2];
    *out_hit.add(6) = hit.distance;

    // DFS 順 ID → エンティティの逆引き（Actor ツリーが公開中の場合のみ）
    let entity = {
        let actors_ptr = ACTORS_PTR.with(|p| p.get());
        if actors_ptr.is_null() { None } else { entity_by_dfs_id(&*actors_ptr, hit.entity_id) }
    };
    match entity {
        Some(e) => { *out_entity = e.index(); *out_entity.add(1) = e.generation(); }
        None    => { *out_entity = u32::MAX;  *out_entity.add(1) = 0; }
    }
    1
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
    instantiate:   unsafe extern "system" fn(*const u8, i32, *mut u32) -> i32,
    destroy:       unsafe extern "system" fn(u32, u32) -> i32,
    find_actor:    unsafe extern "system" fn(*const u8, i32, *mut u32) -> i32,
    input_key:         unsafe extern "system" fn(i32, u32) -> i32,
    input_mouse:       unsafe extern "system" fn(i32, u32) -> i32,
    input_mouse_state: unsafe extern "system" fn(i32, *mut f32) -> i32,
    raycast:           unsafe extern "system" fn(*const f32, *const f32, f32, *mut f32, *mut u32) -> i32,
    load_scene:        unsafe extern "system" fn(*const u8, i32) -> i32,
}

// 関数ポインタは Sync。プロセス全体で 1 つの静的表を共有する。
static HOST_API: ScriptHostApi = ScriptHostApi {
    get_floats:    ffi_get_floats,
    set_floats:    ffi_set_floats,
    get_string:    ffi_get_string,
    set_string:    ffi_set_string,
    has_component: ffi_has_component,
    instantiate:   ffi_instantiate,
    destroy:       ffi_destroy,
    find_actor:    ffi_find_actor,
    input_key:         ffi_input_key,
    input_mouse:       ffi_input_mouse_button,
    input_mouse_state: ffi_input_mouse_state,
    raycast:           ffi_raycast,
    load_scene:        ffi_load_scene,
};

/// C# へ渡す関数ポインタ表へのポインタを返す（RegisterHostApi 用）。
pub fn host_api_ptr() -> *const ScriptHostApi {
    &HOST_API
}
