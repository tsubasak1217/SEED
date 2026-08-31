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

use std::collections::HashMap;
use std::sync::atomic::{AtomicU64, Ordering};

use crate::engine::components::{
    AnimatorComponent, AudioComponent, CameraComponent, CanvasTransform, InputMapComponent,
    ParticleEmitterComponent, SkinnedSpriteComponent, SpriteComponent, Transform,
    WaterLinkComponent, WaterVolumeComponent,
};
use crate::engine::core::input::action_map::{ActionMap, ActionRuntime};
use crate::engine::core::input::{Input, InputState};
use crate::engine::ecs::{Entity, World};
use crate::engine::structs::objects::actor::ComponentSlot;
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

    /// OnDestroy 通知の実行中だけ true になる再入ガード。
    ///
    /// OnDestroy はインスタンス破棄の直前（＝アクターやシーンが解体されている最中）に
    /// 呼ばれるため、そこから発行される Instantiate / Destroy は適用先が存在しない、
    /// あるいは破棄処理と競合する。仕様として「OnDestroy 内の生成・破棄は無視する」と
    /// 定め、このフラグが立っている間は該当 FFI を受理しない（戻り値 0 = 失敗）。
    static IN_ON_DESTROY: Cell<bool> = const { Cell::new(false) };

    /// スクリプトが `Transform.Teleport` で発行したキャラクター瞬間移動のキュー。
    /// 要素 = (DFS 順 entity_id, 移動先ワールド位置 [x,y,z], 回転 [x,y,z,w])。
    /// キャラクター衝突ミラー（`CharacterWorld`）はメインスレッドの App が保持するため即時反映
    /// できない。App がキャラ解決ステップ冒頭でこのキューを消費し、ミラー内キャラ位置を移動先へ
    /// 直接セット（解決の基準 char_last_pos をリセット）する。さもないと motion が巨大になり
    /// KCC が壁で堰き止めてテレポートが失敗する。ECS 位置設定は ffi_teleport が既に行う。
    static CHARACTER_TELEPORTS: RefCell<Vec<(u64, [f32; 3], [f32; 4])>> = const { RefCell::new(Vec::new()) };

    /// ゲームロジック実行中だけ設定される Input への読み取り専用ポインタ。
    /// スクリプトの Input API（キー・マウス判定）が参照する。
    /// 入力イベントの処理はフレームロジック外（イベントハンドラ）で行われるため、
    /// 公開中に Input が変更されることはない。
    static INPUT_PTR: Cell<*const Input> = const { Cell::new(std::ptr::null()) };

    /// ゲームロジック実行中だけ設定される物理スレッドへのコマンド送信チャンネル。
    /// スクリプトの Physics.Raycast（同期問い合わせ）が使用する。
    static PHYSICS_TX: RefCell<Option<crossbeam_channel::Sender<crate::engine::physics::PhysicsCommand>>>
        = const { RefCell::new(None) };

    /// スクリプトが発行したオーディオコマンドのキュー。
    /// AudioManager は App が所有するため即時実行せず、App がフレーム末尾に適用する
    ///（SE の再生遅延は数 ms 以内で知覚できない）。
    static AUDIO_COMMANDS: RefCell<Vec<ScriptAudioCommand>> = const { RefCell::new(Vec::new()) };

    /// 2D アクターのスクリーン座標マップ（アクターエンティティ → 左上原点ピクセル）。
    /// frame_renderer が描画と同一の座標変換チェーンでフレームごとに計算・公開し、
    /// スクリプトの CanvasTransform.ScreenPosition が参照する。
    static SCREEN_POSITIONS: RefCell<Vec<(Entity, [f32; 2])>> = const { RefCell::new(Vec::new()) };

    /// カーソルのキャンバス座標（スクリーンスペースキャンバスの ortho 空間。
    /// 画面中央が原点・Y 下向き・1 単位 = 1px）。
    /// ポインタイベント処理（pointer_events.rs）がヒットテストに使った値をそのまま
    /// 毎フレーム公開し、スクリプトの `Input.MousePositionCanvas` が参照する。
    /// 未公開フレーム（Play 以外・キャンバス世界線以外）は [0, 0] のまま。
    static CANVAS_MOUSE_POS: Cell<[f32; 2]> = const { Cell::new([0.0, 0.0]) };
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

/// OnDestroy 通知を再入ガード下で実行する。
///
/// ガード中は ffi_instantiate / ffi_destroy が受理されない（＝OnDestroy 内の
/// GameObject.Instantiate / Destroy は無視される）。ネストしても正しく復元できるよう、
/// 直前の値を保存して戻す。
pub fn with_on_destroy_guard<R>(f: impl FnOnce() -> R) -> R {
    let prev = IN_ON_DESTROY.with(|g| g.replace(true));
    let result = f();
    IN_ON_DESTROY.with(|g| g.set(prev));
    result
}

/// OnDestroy 通知の実行中かどうか（シーン操作 FFI の受理判定に使う）。
fn is_in_on_destroy() -> bool {
    IN_ON_DESTROY.with(|g| g.get())
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

/// カーソルのキャンバス座標（ortho 空間）を公開する。
///
/// ポインタイベント処理がヒットテストに使ったのと同じ値を渡すことで、
/// スクリプトの `Input.MousePositionCanvas` と `OnPointer*` の座標系が必ず一致する。
pub fn publish_canvas_mouse_position(pos: [f32; 2]) {
    CANVAS_MOUSE_POS.with(|c| c.set(pos));
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
    /// シーンを事前読み込みする（遷移はしない）。
    /// Transition 前に呼んでおくことで、遷移時のロード時間をなくせる。
    /// name_or_path はシーンマネージャ登録名または assets:// パス。
    PreloadScene { name_or_path: String },
    /// シーン全体を切り替える（シーン遷移）。事前読み込み済みなら即座に切り替わり、
    /// 未読み込みなら内部で自動的に読み込んでから切り替わる。
    /// このコマンドが存在するフレームでは他の全コマンドが破棄される
    /// （旧ワールドのエンティティ参照が新ワールドで別実体を指す危険を防ぐため）。
    TransitionScene { name_or_path: String },
}

/// 積まれたシーン操作コマンドを取り出す（キューは空になる）。
/// App がフレームのゲームロジック後に呼び、順番に適用する。
pub fn take_scene_commands() -> Vec<ScriptSceneCommand> {
    SCENE_COMMANDS.with(|q| std::mem::take(&mut *q.borrow_mut()))
}

/// 積まれたキャラクター瞬間移動を取り出す（キューは空になる）。
/// App がキャラ解決ステップ冒頭で呼び、ミラー内キャラ位置を移動先へ直接セットする。
pub fn take_character_teleports() -> Vec<(u64, [f32; 3], [f32; 4])> {
    CHARACTER_TELEPORTS.with(|q| std::mem::take(&mut *q.borrow_mut()))
}

// ─── オーディオコマンド（遅延適用）──────────────────────────

/// スクリプトが発行するオーディオ操作コマンド。
/// App::apply_script_audio_commands がフレーム末尾にまとめて適用する。
pub enum ScriptAudioCommand {
    /// 効果音を再生する（多重再生可）
    PlaySe { path: String, volume: f32 },
    /// BGM を再生する（既存 BGM は置き換え）
    PlayBgm { path: String, volume: f32, looped: bool },
    /// BGM を停止する
    StopBgm,
    /// BGM の音量を変更する
    SetBgmVolume { volume: f32 },
    /// AudioComponent の音源を再生する（entity = スロットエンティティ）
    PlayComponent { entity: Entity },
    /// AudioComponent の音源を停止する（entity = スロットエンティティ）
    StopComponent { entity: Entity },
}

/// 積まれたオーディオコマンドを取り出す（キューは空になる）。
pub fn take_audio_commands() -> Vec<ScriptAudioCommand> {
    AUDIO_COMMANDS.with(|q| std::mem::take(&mut *q.borrow_mut()))
}

// ─── float 配列の定数 ────────────────────────────────────────

/// float フィールドの最大要素数（RGBA カラーの 4 要素が最大）。
pub const MAX_FLOAT_FIELD_LEN: usize = 4;

// ─── スロットエンティティ解決 ─────────────────────────────────

/// コンポーネント T の実体があるエンティティを解決する。
///
/// Transform / CanvasTransform はアクターのルートエンティティに直接あるが、
/// Sprite / Camera / Audio 等の追加コンポーネントは「スロット専用エンティティ」に
/// 格納される。スクリプトは gameObject（ルートエンティティ）しか知らないため、
/// ルートに無い場合は Actor ツリーからスロットを探して解決する。
///
/// 探索順: 1. 指定エンティティ自身 → 2. 指定エンティティをルートに持つアクターの
/// スロット群（最初に T を持つもの）。見つからなければ None。
fn locate<T: crate::engine::ecs::Component>(world: &World, entity: Entity) -> Option<Entity> {
    // 1. 指定エンティティ自身が持っている場合（Transform / CanvasTransform 等）
    if world.get::<T>(entity).is_some() {
        return Some(entity);
    }
    // 2. Actor ツリーからルートエンティティ一致のアクターを探し、スロットを走査する
    let actors_ptr = ACTORS_PTR.with(|p| p.get());
    if actors_ptr.is_null() { return None; }

    /// ルートエンティティが一致するアクターを再帰検索するローカル関数
    fn find_actor(actors: &[Actor], e: Entity) -> Option<&Actor> {
        for a in actors {
            if a.entity == e { return Some(a); }
            if let Some(found) = find_actor(a.children(), e) { return Some(found); }
        }
        None
    }

    let actors = unsafe { &*actors_ptr };
    let actor  = find_actor(actors, entity)?;
    actor.slots().iter()
        .map(|s| s.entity)
        .find(|&se| world.get::<T>(se).is_some())
}

/// ルートエンティティが `entity` と一致する Actor を Actor ツリーから探す。
///
/// Transform の書き込みで「子孫へ伝播する」ために Actor ツリー（親子関係）が必要になる。
/// ACTORS_PTR はスクリプトフェーズ実行中のみ有効な読み取り専用ポインタで、
/// フェーズ中にツリーの構造変更は起きない（Instantiate/Destroy は遅延コマンド）。
///
/// 戻り値の参照はフェーズ内でのみ有効。呼び出し側は即座に使い切ること。
/// ツリーが未公開（ポインタが null）の場合は None。
fn actor_of_entity<'a>(entity: Entity) -> Option<&'a Actor> {
    let actors_ptr = ACTORS_PTR.with(|p| p.get());
    if actors_ptr.is_null() { return None; }

    /// ルートエンティティが一致するアクターを再帰検索するローカル関数
    fn find_actor(actors: &[Actor], e: Entity) -> Option<&Actor> {
        for a in actors {
            if a.entity == e { return Some(a); }
            if let Some(found) = find_actor(a.children(), e) { return Some(found); }
        }
        None
    }

    // SAFETY: ACTORS_PTR はフェーズ実行中のみ非 null で、その間ツリーは不変。
    // world（&mut World）とは別オブジェクトなのでエイリアス違反にはならない。
    let actors = unsafe { &*actors_ptr };
    find_actor(actors, entity)
}

// ─── コンポーネント種別文字列（GetComponent<T> の解決キー）────────
//  C# ラッパーの ComponentKindName（= 従来の Comp 定数）と完全一致させること。
//  Transform / CanvasTransform はアクターのルート entity 直付け、それ以外はスロット格納型。
const KIND_TRANSFORM: &str = "Transform";
const KIND_CANVAS_TRANSFORM: &str = "CanvasTransform";
const KIND_SPRITE: &str = "Sprite";
const KIND_SKINNED_SPRITE: &str = "SkinnedSprite";
const KIND_CAMERA: &str = "Camera";
const KIND_AUDIO: &str = "Audio";
const KIND_ANIMATOR: &str = "Animator";
const KIND_PARTICLE: &str = "ParticleEmitter";
const KIND_INPUT_MAP: &str = "InputMap";

// ─── 水面シェーダパラメータの動的フィールド名（Phase W8.2）────────
//  `WaterVolume` の `shader_params` は**アセットが決める名前**のマップなので、
//  他のフィールドのように固定の match キーにできない。そこで
//  「接頭辞 ＋ アセット内の識別子」というフィールド名で受け渡す。
//  接頭辞で成分数（1 or 3）を決めるため、C# 側の `SetShaderParam` の
//  オーバーロードと 1 対 1 に対応する。**C# の定数と完全一致させること。**

/// スカラー（f32）パラメータのフィールド名接頭辞。
const SHADER_PARAM_FLOAT_PREFIX: &str = "shader_param_f:";
/// 色（vec3<f32>）パラメータのフィールド名接頭辞。
const SHADER_PARAM_VEC3_PREFIX: &str = "shader_param_v3:";

/// スロットが指定コンポーネント種別（kind 文字列）の実体を持つかを判定する。
///
/// スロット専用 entity に対して各コンポーネント型を直引きして種別を確定する
/// （`ComponentSlot::kind` に頼らず world 上の実データで判定するため、
/// locate と同じ「実体があるか」の基準で一貫する）。
fn slot_is_kind(world: &World, slot: &ComponentSlot, kind: &str) -> bool {
    match kind {
        KIND_SPRITE => world.get::<SpriteComponent>(slot.entity).is_some(),
        KIND_SKINNED_SPRITE => world.get::<SkinnedSpriteComponent>(slot.entity).is_some(),
        KIND_CAMERA => world.get::<CameraComponent>(slot.entity).is_some(),
        KIND_AUDIO => world.get::<AudioComponent>(slot.entity).is_some(),
        KIND_ANIMATOR => world.get::<AnimatorComponent>(slot.entity).is_some(),
        KIND_PARTICLE => world.get::<ParticleEmitterComponent>(slot.entity).is_some(),
        KIND_INPUT_MAP => world.get::<InputMapComponent>(slot.entity).is_some(),
        _ => false,
    }
}

/// GetComponent<T> のスロット解決本体。
///
/// - Transform / CanvasTransform: アクタールート entity 直付け（0 番目のみ存在）。
///   index / name は無視し、ルート entity 自身が該当コンポーネントを持つときだけ返す。
/// - スロット格納型: ルート entity 一致のアクターを Actor ツリーから引き、
///   kind でフィルタしたスロット群から、name 指定があれば名前一致、なければ index 番目
///   （index<0 は 0 番目）を選び、そのスロット entity を返す。
///
/// 見つからなければ None。
fn resolve_component_slot(
    world: &World, root: Entity, kind: &str, name: Option<&str>, index: i32,
) -> Option<Entity> {
    // ── ルート直付け型（Transform / CanvasTransform）──
    match kind {
        KIND_TRANSFORM => return world.get::<Transform>(root).map(|_| root),
        KIND_CANVAS_TRANSFORM => return world.get::<CanvasTransform>(root).map(|_| root),
        _ => {}
    }

    // ── スロット格納型: Actor ツリーからルート一致アクターのスロットを走査 ──
    let actor = actor_of_entity(root)?;
    match name {
        // 名前一致（かつ kind 一致）の最初のスロット
        Some(n) => actor
            .slots()
            .iter()
            .find(|s| s.name == n && slot_is_kind(world, s, kind))
            .map(|s| s.entity),
        // index 番目（kind 一致のみでフィルタ。index<0 は 0 番目）
        None => {
            let i = if index < 0 { 0 } else { index as usize };
            actor
                .slots()
                .iter()
                .filter(|s| slot_is_kind(world, s, kind))
                .nth(i)
                .map(|s| s.entity)
        }
    }
}

// ─── InputMap 評価キャッシュ ──────────────────────────────────

thread_local! {
    /// asset_path をキーにした .inputmap のパース結果キャッシュ。
    ///
    /// スクリプトは CLR メインスレッド専用（scripting/mod.rs）なので thread_local で足りる。
    /// 初回アクセス時に asset_fs でファイルを読み込み・パースし、以降は再パースしない
    /// （毎フレーム読むのは禁止方針）。asset_path が変わると別キーとして新規パースされる。
    static INPUT_MAP_CACHE: RefCell<HashMap<String, CachedActionMap>> =
        RefCell::new(HashMap::new());

    /// asset_path をキーにしたアクション状態のフレーム履歴（Start/End・条件エッジ用）。
    ///
    /// ActionMap 自体はステートレスなので、条件（Trigger/Release）と Start/End の
    /// 立ち上がり/立ち下がり検出に必要な「前フレーム状態」をここに持つ。
    /// 同一 asset_path 内はアクション名ごとに独立して履歴を保持する。
    static ACTION_RUNTIME_CACHE: RefCell<HashMap<String, ActionRuntime>> =
        RefCell::new(HashMap::new());
}

/// グローバルフレームカウンタ。
///
/// アクション状態キャッシュが「新しいフレームか（履歴をシフトすべきか）」を判定するために使う。
/// 同一フレーム内の複数クエリで一貫した結果を返すための単調増加スタンプ。
/// frame_renderer が毎フレーム 1 回 `advance_script_frame()` を呼んで加算する。
static SCRIPT_FRAME: AtomicU64 = AtomicU64::new(0);

/// フレームカウンタを 1 進める（frame_renderer がフレーム先頭で 1 回呼ぶ）。
pub fn advance_script_frame() {
    SCRIPT_FRAME.fetch_add(1, Ordering::Relaxed);
}

/// 現在のフレーム番号を返す。
fn current_script_frame() -> u64 {
    SCRIPT_FRAME.load(Ordering::Relaxed)
}

/// キャッシュ済み ActionMap ＋ ホットリロード用のメタ情報。
///
/// 【なぜ mtime 監視が要るか（実バグの再発防止）】
/// かつては「初回パース後は二度と再読込しない」キャッシュだったため、
/// エディタで .inputmap を編集しても**ランタイムを再起動するまで反映されず**、
/// 「矢印キーを削除したのに効き続ける」「RightStickX を追加したのに効かない」
/// という報告バグの原因になった。シェーディングアセットと同じ mtime ポーリング
/// （`INPUT_MAP_POLL_INTERVAL_SECS` 間引き）で編集へ自動追従する。
struct CachedActionMap {
    map: ActionMap,
    /// 最後に読み込んだときのファイル mtime（asset_fs::mtime）。
    mtime: u64,
    /// 最後に mtime を確認した時刻（ポーリング間引き用）。
    last_check: std::time::Instant,
}

/// .inputmap の mtime をポーリングする最短間隔 [秒]。
/// シェーディングアセットのホットリロード（SHADING_ASSET_POLL_INTERVAL_SECS）と同じ方針。
const INPUT_MAP_POLL_INTERVAL_SECS: f64 = 1.0;

/// キャッシュエントリを「最新のファイル内容」に保証する。
///
/// - 未キャッシュ → 読み込んでパース
/// - キャッシュ済み → 前回確認から `INPUT_MAP_POLL_INTERVAL_SECS` 経過していれば
///   mtime を再取得し、変わっていたら再パース。**あわせて対応する ActionRuntime
///   （フレーム履歴）も破棄する** — 旧マップのエッジ検出履歴を新マップへ持ち越すと
///   「削除したアクションの Release が発火する」類の混線が起きるため。
fn ensure_action_map_fresh(cache: &mut HashMap<String, CachedActionMap>, key: &str) {
    let now = std::time::Instant::now();
    if let Some(entry) = cache.get_mut(key) {
        if now.duration_since(entry.last_check).as_secs_f64() < INPUT_MAP_POLL_INTERVAL_SECS {
            return;
        }
        entry.last_check = now;
        let normalized = crate::engine::asset_fs::normalize_asset_path(key);
        let mtime = crate::engine::asset_fs::mtime(&normalized);
        if mtime == entry.mtime {
            return;
        }
        entry.mtime = mtime;
        entry.map = load_action_map(key);
        ACTION_RUNTIME_CACHE.with(|rc| {
            rc.borrow_mut().remove(key);
        });
        return;
    }
    let normalized = crate::engine::asset_fs::normalize_asset_path(key);
    cache.insert(key.to_string(), CachedActionMap {
        map: load_action_map(key),
        mtime: crate::engine::asset_fs::mtime(&normalized),
        last_check: now,
    });
}

/// asset_path から ActionMap をロードする（失敗時は空マップ＋警告）。
fn load_action_map(asset_path: &str) -> ActionMap {
    let normalized = crate::engine::asset_fs::normalize_asset_path(asset_path);
    match crate::engine::asset_fs::read_string(&normalized) {
        Ok(json) => ActionMap::parse(&json),
        Err(e) => {
            eprintln!("[SEED script] InputMap 読み込み失敗 '{asset_path}': {e}");
            ActionMap::empty()
        }
    }
}

/// スロット entity の InputMapComponent を解決し、キャッシュ済み ActionMap で
/// クロージャ f を実行する。InputMap 未アタッチ・asset_path 未設定なら None。
fn with_action_map<R>(world: &World, slot: Entity, f: impl FnOnce(&ActionMap) -> R) -> Option<R> {
    let ic = world.get::<InputMapComponent>(slot)?;
    if ic.asset_path.is_empty() {
        return None;
    }
    let key = ic.asset_path.clone();
    INPUT_MAP_CACHE.with(|c| {
        let mut cache = c.borrow_mut();
        // 未キャッシュなら読み込み、キャッシュ済みでも mtime 変化で再読込（ホットリロード）
        ensure_action_map_fresh(&mut cache, &key);
        // 直前に必ず挿入済みなので unwrap は安全
        Some(f(&cache.get(&key).unwrap().map))
    })
}

/// スロット entity の InputMapComponent を解決し、キャッシュ済み ActionMap と
/// asset_path 単位のフレーム履歴（ActionRuntime）を渡してクロージャ f を実行する。
///
/// 条件（Trigger/Release）や Start/End のエッジ検出には前フレーム状態が必要なため、
/// map（不変）と runtime（可変・履歴）の両方を借用する。
fn with_action_map_and_runtime<R>(
    world: &World,
    slot: Entity,
    f: impl FnOnce(&ActionMap, &mut ActionRuntime) -> R,
) -> Option<R> {
    let ic = world.get::<InputMapComponent>(slot)?;
    if ic.asset_path.is_empty() {
        return None;
    }
    let key = ic.asset_path.clone();
    INPUT_MAP_CACHE.with(|c| {
        let mut cache = c.borrow_mut();
        // 未キャッシュなら読み込み、キャッシュ済みでも mtime 変化で再読込（ホットリロード）。
        // 再読込時は対応する ActionRuntime も ensure 側が破棄する。
        ensure_action_map_fresh(&mut cache, &key);
        let map = &cache.get(&key).unwrap().map;
        // 別 RefCell なので同時借用しても衝突しない。
        ACTION_RUNTIME_CACHE.with(|rc| {
            let mut runtimes = rc.borrow_mut();
            let rt = runtimes.entry(key.clone()).or_default();
            Some(f(map, rt))
        })
    })
}

// ─── コンポーネントレジストリ ─────────────────────────────────
//  新しいコンポーネントをスクリプトへ公開するときは、read_floats / write_floats
//  （文字列フィールドがあれば read_string / write_string も）と has_component に
//  1 分岐ずつ追加する（docs/scripting_api.md・.claude/CLAUDE.md の手順を参照）。
//  スロット格納型のコンポーネントは locate::<T> でエンティティを解決すること。

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
                // ── 方向ベクトル（read のみ・単位長）──
                // 回転規約（YXZ オイラー・前方向 +Z）の正典である Transform::rotation_basis()
                // をそのまま通す。C# 側で再計算せず、ここで得た基底を返すことで二重管理を防ぐ。
                // Back / Left / Down は C# 側で符号反転して求める。
                "forward"  => put(out, &t.forward()),
                "up"       => put(out, &t.up()),
                "right"    => put(out, &t.right()),
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
        // ── 2D スプライト（スロット格納型: locate で解決）──
        "Sprite" => {
            let e = locate::<SpriteComponent>(world, entity)?;
            let s = world.get::<SpriteComponent>(e)?;
            match field {
                "color"  => put(out, &s.color),
                "width"  => put(out, &[s.width]),
                "height" => put(out, &[s.height]),
                // 描画優先度レイヤー（i32 → f32 変換して返す。Camera の target_width と同様）
                "layer"  => put(out, &[s.layer as f32]),
                // ポインタイベントのヒットテスト対象か（bool = 0/1）
                "raycast_target" => put(out, &[if s.raycast_target { 1.0 } else { 0.0 }]),
                _        => None,
            }
        }
        // ── メッシュ変形スキニング 2D スプライト（スロット格納型: locate で解決）──
        "SkinnedSprite" => {
            let e = locate::<SkinnedSpriteComponent>(world, entity)?;
            let s = world.get::<SkinnedSpriteComponent>(e)?;
            match field {
                "color" => put(out, &s.color),
                // 描画優先度レイヤー（i32 → f32 変換して返す。Sprite の layer と同様）
                "layer" => put(out, &[s.layer as f32]),
                // ポインタイベントのヒットテスト対象か（bool = 0/1）
                "raycast_target" => put(out, &[if s.raycast_target { 1.0 } else { 0.0 }]),
                _       => None,
            }
        }
        // ── オーディオソース（スロット格納型: locate で解決）──
        "Audio" => {
            let e = locate::<AudioComponent>(world, entity)?;
            let a = world.get::<AudioComponent>(e)?;
            match field {
                "volume"        => put(out, &[a.volume]),
                "pan"           => put(out, &[a.pan]),
                "loop"          => put(out, &[if a.looped { 1.0 } else { 0.0 }]),
                "play_on_start" => put(out, &[if a.play_on_start { 1.0 } else { 0.0 }]),
                "spatial"       => put(out, &[if a.spatial { 1.0 } else { 0.0 }]),
                "min_distance"  => put(out, &[a.min_distance]),
                "max_distance"  => put(out, &[a.max_distance]),
                _               => None,
            }
        }
        // ── 3D カメラ（スロット格納型: locate で解決）──
        "Camera" => {
            let e = locate::<CameraComponent>(world, entity)?;
            let c = world.get::<CameraComponent>(e)?;
            match field {
                "fov_y_deg"     => put(out, &[c.fov_y_deg]),
                "near"          => put(out, &[c.near]),
                "far"           => put(out, &[c.far]),
                "is_main"       => put(out, &[if c.is_main { 1.0 } else { 0.0 }]),
                "clear_color"   => put(out, &c.clear_color),
                "target_width"  => put(out, &[c.target_width as f32]),
                "target_height" => put(out, &[c.target_height as f32]),
                "bar_color"     => put(out, &c.bar_color),
                "ortho_height"  => put(out, &[c.ortho_height]),
                _               => None,
            }
        }
        // ── アニメーター（スロット格納型: locate で解決）──
        // Play/Stop/Pause/Resume は文字列引数を伴うため専用 FFI（ffi_animator_component）で扱う。
        // ここでは単純な数値フィールド（再生位置・速度・再生中フラグ）のみを公開する。
        "Animator" => {
            let e = locate::<AnimatorComponent>(world, entity)?;
            let a = world.get::<AnimatorComponent>(e)?;
            match field {
                "time"    => put(out, &[a.time]),
                "speed"   => put(out, &[a.speed]),
                "playing" => put(out, &[if a.playing { 1.0 } else { 0.0 }]),
                _         => None,
            }
        }
        // ── パーティクルエミッタ（スロット格納型: locate で解決）──
        // Play/Stop/Burst は専用 FFI（ffi_particle_component）で扱う。
        // ここでは単純な数値／bool フィールドの読み書きのみ公開する。
        "ParticleEmitter" => {
            let e = locate::<ParticleEmitterComponent>(world, entity)?;
            let p = world.get::<ParticleEmitterComponent>(e)?;
            // スクリプト API の互換レイヤ: フィールド名は従来どおり（emit_rate /
            // spread_angle_deg / loop_emit）に保ち、新スキーマ（emit_interval +
            // particles_per_emit / direction_randomness / emit_mode）へ換算して読む。
            // C# ラッパー・docs/scripting_api.md（正典）の API 名は変えない。
            match field {
                // 実効放出レート（個/秒）= particles_per_emit / emit_interval。
                "emit_rate"        => {
                    let rate = if p.emit_interval > 0.0 {
                        p.particles_per_emit as f32 / p.emit_interval
                    } else { 0.0 };
                    put(out, &[rate])
                }
                "drag"             => put(out, &[p.drag]),
                // 円錐半頂角（度）= direction_randomness * 180。
                "spread_angle_deg" => put(out, &[
                    p.direction_randomness
                        * crate::engine::components::DIRECTION_RANDOMNESS_MAX_HALF_ANGLE_DEG,
                ]),
                "playing"          => put(out, &[if p.playing { 1.0 } else { 0.0 }]),
                // loop_emit = emit_mode が Loop かどうか。
                "loop_emit"        => put(out, &[
                    if p.emit_mode == crate::engine::components::EmitMode::Loop { 1.0 } else { 0.0 },
                ]),
                _                  => None,
            }
        }
        // ── 水位グラフの開口（Phase W2.5。スロット格納型: locate で解決）──
        // バルブ開閉（openness）をスクリプトから制御するための公開。
        // 接続先アクタ名（volume_a / volume_b）は**公開しない**
        //（実行中に付け替えると水位グラフの同一性が壊れるため。壁の破壊は
        //  スロットの有効・無効やコンポーネント追加で表現する）。
        "WaterLink" => {
            let e = locate::<WaterLinkComponent>(world, entity)?;
            let l = world.get::<WaterLinkComponent>(e)?;
            match field {
                "openness"         => put(out, &[l.openness]),
                "opening_width"    => put(out, &[l.opening_width]),
                "opening_height"   => put(out, &[l.opening_height]),
                "opening_bottom"   => put(out, &[l.opening_bottom]),
                "flow_coefficient" => put(out, &[l.flow_coefficient]),
                _                  => None,
            }
        }
        // ── 水ボリューム（Phase W2.5。スロット格納型: locate で解決）──
        // 水位グラフの読み書きに必要な最小限だけを公開する。
        // 見た目パラメータ（色・波・泡）は演出設定であってゲームロジックが
        // 触るものではないため、公開しない（インスペクタで編集する）。
        "WaterVolume" => {
            let e = locate::<WaterVolumeComponent>(world, entity)?;
            let w = world.get::<WaterVolumeComponent>(e)?;
            match field {
                // 設定水位（Ocean = ワールド絶対 / Region = アクタ相対）。
                "surface_height" => put(out, &[w.surface_height]),
                // 水位グラフの対象フラグ。
                "simulate_level" => put(out, &[if w.simulate_level { 1.0 } else { 0.0 }]),
                // **現在の水面 Y（常にワールド絶対値）。読み取り専用。**
                // 「水位が閾値を超えたら扉を閉める」といった判定に使う。
                //
                // 水位グラフが動いていればその現在水位をそのまま返す。
                // 動いていない（静止水面の）ときは種別ごとの規約に合わせて
                // ワールド Y へ直す ＝ Ocean は絶対値のまま / Region・Spline は
                // アクタ Y を足す（`ResolvedWaterVolume` と同じ換算。
                // ここで揃えておかないと「Play 前後で値の意味が変わる」ことになる）。
                "water_level"    => {
                    let level = w.sim_level_y.unwrap_or_else(|| {
                        let base = if w.kind == crate::engine::components::WaterVolumeKind::Ocean {
                            0.0
                        } else {
                            // アクタのルート entity（= 引数 entity）の Transform を見る。
                            world.get::<Transform>(entity).map(|t| t.position[1]).unwrap_or(0.0)
                        };
                        base + w.surface_height
                    });
                    put(out, &[level])
                }
                // ── 水面シェーディングアセットのパラメータ（W8.2）──
                // フィールド名は `shader_param_f:<名前>` / `shader_param_v3:<名前>` の
                // **接頭辞つき動的キー**である（アセットごとに名前が違うので固定 match にできない）。
                // 保存値が無ければアセットの宣言（`override ... = ...`）の既定値へ落ちる。
                // 宣言そのものが無い（アセット未指定・名前の綴り違い）ときは 0 を返す。
                other => {
                    let (name, is_vec3) = match other.strip_prefix(SHADER_PARAM_VEC3_PREFIX) {
                        Some(n) => (n, true),
                        None    => (other.strip_prefix(SHADER_PARAM_FLOAT_PREFIX)?, false),
                    };
                    let value = w.shader_params.get(name).copied().or_else(|| {
                        crate::engine::core::renderer::water::shading_asset::declaration_default(
                            &w.surface_shader, name)
                    }).unwrap_or([0.0; 4]);
                    if is_vec3 { put(out, &value[..3]) } else { put(out, &value[..1]) }
                }
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
        // Transform はワールド空間で保持され、描画実体は ModelComponent.instance_mats、
        // 親子関係は Actor ツリーが持つ。そのためコンポーネントの数値を書くだけでは
        // 「メッシュが動かない」「子アクタ（カメラ等）が追従しない」状態になる。
        // 必ず集約関数 transform_sync::set_actor_world_transform を経由させること。
        "Transform" => {
            // 現在値を読み、指定フィールドだけ差し替えた新しいワールド Transform を作る
            let Some(cur) = world.get::<Transform>(entity).cloned() else { return false };
            let mut next = cur;
            let field_ok = match field {
                "position" => take::<3>(v).map(|a| next.position = a).is_some(),
                "rotation" => take::<3>(v).map(|a| next.rotation = a).is_some(),
                "scale"    => take::<3>(v).map(|a| next.scale    = a).is_some(),
                _          => false,
            };
            if !field_ok { return false; }

            match actor_of_entity(entity) {
                // Actor ツリーが公開されている通常ケース: instance_mats・全子孫へ伝播する。
                // child_dfs_start は Undo 記録に使う採番の起点で、スクリプト経路では
                // Undo を記録しないため 0 でよい。
                Some(actor) => {
                    crate::engine::core::transform_sync::set_actor_world_transform(
                        actor, world, next, 0,
                    );
                    true
                }
                // Actor ツリー未公開（フェーズ外呼び出し等）のフォールバック。
                // 伝播はできないが、少なくともコンポーネント値は反映する。
                None => {
                    match world.get_mut::<Transform>(entity) {
                        Some(t) => { *t = next; true }
                        None    => false,
                    }
                }
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
        // ── 2D スプライト（スロット格納型: locate で解決）──
        "Sprite" => {
            let Some(e) = locate::<SpriteComponent>(world, entity) else { return false };
            let Some(s) = world.get_mut::<SpriteComponent>(e) else { return false };
            match field {
                "color"  => take(v).map(|a| s.color = a).is_some(),
                "width"  => take::<1>(v).map(|a| s.width  = a[0]).is_some(),
                "height" => take::<1>(v).map(|a| s.height = a[0]).is_some(),
                // 描画優先度レイヤー（f32 → i32 変換して格納。Camera の target_width と同様）
                "layer"  => take::<1>(v).map(|a| s.layer = a[0] as i32).is_some(),
                // ポインタイベントのヒットテスト対象か（0/1 → bool）
                "raycast_target" => {
                    take::<1>(v).map(|a| s.raycast_target = a[0] != 0.0).is_some()
                }
                _        => false,
            }
        }
        // ── メッシュ変形スキニング 2D スプライト（スロット格納型: locate で解決）──
        "SkinnedSprite" => {
            let Some(e) = locate::<SkinnedSpriteComponent>(world, entity) else { return false };
            let Some(s) = world.get_mut::<SkinnedSpriteComponent>(e) else { return false };
            match field {
                "color" => take(v).map(|a| s.color = a).is_some(),
                // 描画優先度レイヤー（f32 → i32 変換して格納。Sprite の layer と同様）
                "layer" => take::<1>(v).map(|a| s.layer = a[0] as i32).is_some(),
                // ポインタイベントのヒットテスト対象か（0/1 → bool）
                "raycast_target" => {
                    take::<1>(v).map(|a| s.raycast_target = a[0] != 0.0).is_some()
                }
                _       => false,
            }
        }
        // ── オーディオソース（スロット格納型: locate で解決）──
        "Audio" => {
            let Some(e) = locate::<AudioComponent>(world, entity) else { return false };
            let Some(a) = world.get_mut::<AudioComponent>(e) else { return false };
            match field {
                "volume"        => take::<1>(v).map(|x| a.volume = x[0].max(0.0)).is_some(),
                "pan"           => take::<1>(v).map(|x| a.pan = x[0].clamp(-1.0, 1.0)).is_some(),
                "loop"          => take::<1>(v).map(|x| a.looped = x[0] != 0.0).is_some(),
                "play_on_start" => take::<1>(v).map(|x| a.play_on_start = x[0] != 0.0).is_some(),
                "spatial"       => take::<1>(v).map(|x| a.spatial = x[0] != 0.0).is_some(),
                "min_distance"  => take::<1>(v).map(|x| a.min_distance = x[0].max(0.0)).is_some(),
                "max_distance"  => take::<1>(v).map(|x| a.max_distance = x[0].max(0.0)).is_some(),
                _               => false,
            }
        }
        // ── 3D カメラ（スロット格納型: locate で解決）──
        "Camera" => {
            let Some(e) = locate::<CameraComponent>(world, entity) else { return false };
            let Some(c) = world.get_mut::<CameraComponent>(e) else { return false };
            match field {
                "fov_y_deg"     => take::<1>(v).map(|a| c.fov_y_deg = a[0]).is_some(),
                "near"          => take::<1>(v).map(|a| c.near = a[0]).is_some(),
                "far"           => take::<1>(v).map(|a| c.far  = a[0]).is_some(),
                "is_main"       => take::<1>(v).map(|a| c.is_main = a[0] != 0.0).is_some(),
                "clear_color"   => take(v).map(|a| c.clear_color = a).is_some(),
                "target_width"  => take::<1>(v).map(|a| c.target_width  = a[0].max(0.0) as u32).is_some(),
                "target_height" => take::<1>(v).map(|a| c.target_height = a[0].max(0.0) as u32).is_some(),
                "bar_color"     => take(v).map(|a| c.bar_color = a).is_some(),
                "ortho_height"  => take::<1>(v).map(|a| c.ortho_height = a[0].max(0.01)).is_some(),
                _               => false,
            }
        }
        // ── アニメーター（スロット格納型: locate で解決）──
        // playing は Play/Stop/Pause/Resume（ffi_animator_component）経由でのみ変更させる
        // （直接書き込みを許すと current_clip との整合が崩れるため、ここでは公開しない）。
        "Animator" => {
            let Some(e) = locate::<AnimatorComponent>(world, entity) else { return false };
            let Some(a) = world.get_mut::<AnimatorComponent>(e) else { return false };
            match field {
                "time"  => take::<1>(v).map(|x| a.time = x[0].max(0.0)).is_some(),
                "speed" => take::<1>(v).map(|x| a.speed = x[0]).is_some(),
                _       => false,
            }
        }
        // ── パーティクルエミッタ（スロット格納型: locate で解決）──
        // 負値・範囲外は light_ops と同方針でクランプする（放出レート/抵抗は非負、
        // spread は円錐半頂角なので 0..=180 度に収める）。
        "ParticleEmitter" => {
            let Some(e) = locate::<ParticleEmitterComponent>(world, entity) else { return false };
            let Some(p) = world.get_mut::<ParticleEmitterComponent>(e) else { return false };
            // スクリプト API の互換レイヤ（read_floats と対）: 従来のフィールド名を
            // 新スキーマへ換算して書き込む。C# ラッパー・docs の API 名は変えない。
            match field {
                // emit_rate（個/秒）→ emit_interval = particles_per_emit / rate。
                // rate<=0 は「放出停止」の意図として emit_interval を 0 にせず
                // 十分大きな間隔（f32::MAX）にする（interval=0 は毎フレーム放出の意味のため）。
                "emit_rate"        => take::<1>(v).map(|x| {
                    let rate = x[0].max(0.0);
                    p.emit_interval = if rate > 0.0 {
                        p.particles_per_emit.max(1) as f32 / rate
                    } else { f32::MAX };
                }).is_some(),
                "drag"             => take::<1>(v).map(|x| p.drag = x[0].max(0.0)).is_some(),
                // spread_angle_deg（0..180）→ direction_randomness = deg / 180。
                "spread_angle_deg" => take::<1>(v).map(|x| {
                    p.direction_randomness = x[0].clamp(0.0, 180.0)
                        / crate::engine::components::DIRECTION_RANDOMNESS_MAX_HALF_ANGLE_DEG;
                }).is_some(),
                "playing"          => take::<1>(v).map(|x| p.playing = x[0] != 0.0).is_some(),
                // loop_emit: true → Loop / false → Once（Count 指定はエディタ側のみ）。
                "loop_emit"        => take::<1>(v).map(|x| {
                    p.emit_mode = if x[0] != 0.0 {
                        crate::engine::components::EmitMode::Loop
                    } else {
                        crate::engine::components::EmitMode::Once
                    };
                }).is_some(),
                _                  => false,
            }
        }
        // ── 水位グラフの開口（Phase W2.5。スロット格納型: locate で解決）──
        // **バルブ開閉のための書き込み口**。openness を 0 にすれば水が止まり、
        // 1 にすれば流れ出す（「レベルスクリプトがそのまま水の挙動になる」）。
        // クランプ規則はインスペクタ（water_link_ops.rs）と同一にする
        //（経路によって許される値が違うと、スクリプトで設定した値が
        //  インスペクタを触った瞬間に変わる、というずれが起きる）。
        "WaterLink" => {
            let Some(e) = locate::<WaterLinkComponent>(world, entity) else { return false };
            let Some(l) = world.get_mut::<WaterLinkComponent>(e) else { return false };
            match field {
                "openness"         => take::<1>(v)
                    .map(|a| l.openness = a[0].clamp(0.0, 1.0)).is_some(),
                "opening_width"    => take::<1>(v)
                    .map(|a| l.opening_width = a[0].max(0.0)).is_some(),
                "opening_height"   => take::<1>(v)
                    .map(|a| l.opening_height = a[0].max(0.0)).is_some(),
                "opening_bottom"   => take::<1>(v)
                    .map(|a| l.opening_bottom = a[0]).is_some(),
                "flow_coefficient" => take::<1>(v)
                    .map(|a| l.flow_coefficient = a[0].max(0.0)).is_some(),
                _                  => false,
            }
        }
        // ── 水ボリューム（Phase W2.5。スロット格納型: locate で解決）──
        // `water_level`（現在水位）は**読み取り専用**なのでここに分岐を書かない。
        // 水位を直接書けてしまうと体積保存が破れ、水位グラフの前提が壊れる
        //（水を足したい／抜きたい場合は surface_height を動かすのではなく、
        //  リンクの開閉で表現するのが本方式の設計である）。
        "WaterVolume" => {
            let Some(e) = locate::<WaterVolumeComponent>(world, entity) else { return false };
            let Some(w) = world.get_mut::<WaterVolumeComponent>(e) else { return false };
            match field {
                "surface_height" => take::<1>(v).map(|a| w.surface_height = a[0]).is_some(),
                "simulate_level" => take::<1>(v).map(|a| {
                    let on = a[0] != 0.0;
                    w.simulate_level = on;
                    // OFF にしたら揮発水位も消す（water_ops.rs と同一の規則）。
                    if !on { w.sim_level_y = None; }
                }).is_some(),
                // ── 水面シェーディングアセットのパラメータ（W8.2）──
                // 「ボス HP で毒の蛍光を変える」のような**毎フレームの流し込み**が用途。
                // インスペクタ経路（SET_WATER_SHADER_PARAM）と同じく
                // `shader_params` へ 4 成分をそのまま入れる（描画側の解釈も同一）。
                //
                // **Play 中の書き込みはシーンへ焼き付かない**: Play 終了時に
                // `play_mode_ops::restore_actors_from_snapshot` が Play 開始時点の
                // ActorData からアクタを作り直すため、ここでの変更は自動的に巻き戻る
                //（sim_level_y と同じ「Play 中だけの値」になる）。SCENE_MODIFIED も
                // Undo 記録も行わない（スクリプトの実行はシーンの編集ではない）。
                other => {
                    let (name, is_vec3) = match other.strip_prefix(SHADER_PARAM_VEC3_PREFIX) {
                        Some(n) => (n, true),
                        None    => match other.strip_prefix(SHADER_PARAM_FLOAT_PREFIX) {
                            Some(n) => (n, false),
                            None    => return false,
                        },
                    };
                    if name.is_empty() { return false; }
                    // 型ごとに要素数を厳密に検査する（C# 側の実装ミスを丸めない）。
                    let value = if is_vec3 {
                        match take::<3>(v) { Some(a) => [a[0], a[1], a[2], 0.0], None => return false }
                    } else {
                        match take::<1>(v) { Some(a) => [a[0], 0.0, 0.0, 0.0], None => return false }
                    };
                    w.shader_params.insert(name.to_string(), value);
                    true
                }
            }
        }
        _ => false,
    }
}

/// コンポーネントの文字列フィールドを読む。未対応は None。
fn read_string(world: &World, entity: Entity, component: &str, field: &str) -> Option<String> {
    match component {
        "Sprite" => {
            let e = locate::<SpriteComponent>(world, entity)?;
            let s = world.get::<SpriteComponent>(e)?;
            match field {
                "texture_path" => Some(s.texture_path.clone()),
                _              => None,
            }
        }
        "SkinnedSprite" => {
            let e = locate::<SkinnedSpriteComponent>(world, entity)?;
            let s = world.get::<SkinnedSpriteComponent>(e)?;
            match field {
                "texture_path" => Some(s.texture_path.clone()),
                "mesh_path"    => Some(s.mesh_path.clone()),
                _              => None,
            }
        }
        "Audio" => {
            let e = locate::<AudioComponent>(world, entity)?;
            let a = world.get::<AudioComponent>(e)?;
            match field {
                "audio_path" => Some(a.audio_path.clone()),
                _            => None,
            }
        }
        "Camera" => {
            let e = locate::<CameraComponent>(world, entity)?;
            let c = world.get::<CameraComponent>(e)?;
            match field {
                "projection" => Some(c.projection.as_str().to_string()),
                _            => None,
            }
        }
        // ── アニメーター（スロット格納型: locate で解決）──
        // current_clip は書き込み不可（Play アクション経由でのみ変更させる）のため read のみ。
        "Animator" => {
            let e = locate::<AnimatorComponent>(world, entity)?;
            let a = world.get::<AnimatorComponent>(e)?;
            match field {
                // 未再生（None）は空文字を返す（C# 側 CurrentClip の既定値と一致させる）
                "current_clip" => Some(a.current_clip.clone().unwrap_or_default()),
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
            let Some(e) = locate::<SpriteComponent>(world, entity) else { return false };
            let Some(s) = world.get_mut::<SpriteComponent>(e) else { return false };
            match field {
                "texture_path" => { s.texture_path = value.to_string(); true }
                _              => false,
            }
        }
        "SkinnedSprite" => {
            let Some(e) = locate::<SkinnedSpriteComponent>(world, entity) else { return false };
            let Some(s) = world.get_mut::<SkinnedSpriteComponent>(e) else { return false };
            match field {
                "texture_path" => { s.texture_path = value.to_string(); true }
                "mesh_path"    => { s.mesh_path    = value.to_string(); true }
                _              => false,
            }
        }
        "Audio" => {
            let Some(e) = locate::<AudioComponent>(world, entity) else { return false };
            let Some(a) = world.get_mut::<AudioComponent>(e) else { return false };
            match field {
                "audio_path" => { a.audio_path = value.to_string(); true }
                _            => false,
            }
        }
        "Camera" => {
            let Some(e) = locate::<CameraComponent>(world, entity) else { return false };
            let Some(c) = world.get_mut::<CameraComponent>(e) else { return false };
            match field {
                "projection" => {
                    c.projection = crate::engine::components::CameraProjection::from_str(value);
                    true
                }
                _ => false,
            }
        }
        _ => false,
    }
}

/// エンティティ（またはそのアクターのスロット）が指定コンポーネントを持つか。
fn has_component(world: &World, entity: Entity, component: &str) -> bool {
    match component {
        "Transform"       => world.get::<Transform>(entity).is_some(),
        "CanvasTransform" => world.get::<CanvasTransform>(entity).is_some(),
        "Sprite"          => locate::<SpriteComponent>(world, entity).is_some(),
        "SkinnedSprite"   => locate::<SkinnedSpriteComponent>(world, entity).is_some(),
        "Camera"          => locate::<CameraComponent>(world, entity).is_some(),
        "Audio"           => locate::<AudioComponent>(world, entity).is_some(),
        "Animator"        => locate::<AnimatorComponent>(world, entity).is_some(),
        "ParticleEmitter" => locate::<ParticleEmitterComponent>(world, entity).is_some(),
        "InputMap"        => locate::<InputMapComponent>(world, entity).is_some(),
        // 水位グラフ（Phase W2.5）
        "WaterLink"       => locate::<WaterLinkComponent>(world, entity).is_some(),
        "WaterVolume"     => locate::<WaterVolumeComponent>(world, entity).is_some(),
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
    // OnDestroy 内からの生成は仕様として無視する（適用先のアクター/シーンが解体中のため）
    if is_in_on_destroy() { return 0; }
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
    // OnDestroy 内からの破棄要求は仕様として無視する（二重破棄・破棄処理との競合を防ぐ）
    if is_in_on_destroy() { return 0; }
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

// ─── シーンコマンド種別（C# 側 Scene クラスの定数と一致させる）───
const SCENE_CMD_PRELOAD: i32 = 0;    // 事前読み込み（遷移しない）
const SCENE_CMD_TRANSITION: i32 = 1; // 遷移（未読み込みなら自動 Load）

/// シーンの事前読み込み・遷移コマンドを発行する。受理=1 / 失敗=0。
///
/// kind: 上記 SCENE_CMD_*。name_or_path はシーンマネージャ登録名
/// （project_settings.json の scenes）または assets:// パス。
/// 実際の処理はフレームのゲームロジック後に遅延適用される。
/// Transition があるフレームでは他のシーン操作コマンドはすべて破棄される。
unsafe extern "system" fn ffi_scene(kind: i32, name: *const u8, name_len: i32) -> i32 {
    let name_str = str_from(name, name_len);
    if name_str.is_empty() { return 0; }
    let cmd = match kind {
        SCENE_CMD_PRELOAD    => ScriptSceneCommand::PreloadScene    { name_or_path: name_str.to_string() },
        SCENE_CMD_TRANSITION => ScriptSceneCommand::TransitionScene { name_or_path: name_str.to_string() },
        _ => return 0,
    };
    SCENE_COMMANDS.with(|q| q.borrow_mut().push(cmd));
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
/// kind: 0=スクリーン座標(2要素) / 1=相対移動量(Raw Input・2要素) / 2=ホイール量(1要素) /
///       3=キャンバス座標(2要素) / 4=カーソル座標差分(2要素)。
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
        // カーソル座標差分（CursorMoved 由来。Raw Input が届かない埋め込み Play でも動く）
        input_bridge::MOUSE_STATE_POSITION_DELTA => {
            let v = input.mouse_position_delta();
            *out        = v.x;
            *out.add(1) = v.y;
            2
        }
        // キャンバス座標は Input ではなくポインタイベント処理が公開する値を返す
        //（ウィンドウサイズとキャンバスのレイアウト規約が必要なため）。
        input_bridge::MOUSE_STATE_CANVAS_POSITION => {
            let v = CANVAS_MOUSE_POS.with(|c| c.get());
            *out        = v[0];
            *out.add(1) = v[1];
            2
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

/// アクターのルートエンティティから DFS 順 ID を逆引きする（entity_by_dfs_id の逆）。
///
/// Transform.Teleport が、対象アクターの物理エントリ（前回位置・コライダー）を物理
/// スレッド側で特定できるよう、ECS Entity → DFS 順 ID を求める。
/// 走査順は entity_by_dfs_id / collect_physics_objects と同一
/// （world_line 一致のルートから先行順 DFS、ID は 1 始まり・全アクターがカウント対象）。
fn dfs_id_by_entity(actors: &[Actor], target: Entity) -> Option<u64> {
    fn walk(actors: &[Actor], counter: &mut u64, target: Entity) -> Option<u64> {
        for a in actors {
            *counter += 1;
            if a.entity == target { return Some(*counter); }
            if let Some(id) = walk(a.children(), counter, target) { return Some(id); }
        }
        None
    }
    let roots: Vec<&Actor> = actors.iter().filter(|a| a.world_line == PLAY_WORLD_LINE).collect();
    let mut counter = 0u64;
    for root in roots {
        counter += 1;
        if root.entity == target { return Some(counter); }
        if let Some(id) = walk(root.children(), &mut counter, target) { return Some(id); }
    }
    None
}

/// YXZ オイラー角（度）を SEED のクォータニオン [x, y, z, w] へ変換する。
///
/// Transform.rotation はオイラー角（度）で保持されるが、物理スレッドのコライダー姿勢は
/// クォータニオンを要するため、Transform.Teleport の送信直前にここで変換する。
fn euler_deg_to_quat_arr(euler_deg: [f32; 3]) -> [f32; 4] {
    use crate::engine::structs::tensor::Vector3 as SeedVec3;
    use crate::engine::structs::transforms::Quaternion as SeedQuat;
    let euler_rad = SeedVec3::new(
        euler_deg[0].to_radians(),
        euler_deg[1].to_radians(),
        euler_deg[2].to_radians(),
    );
    let q = SeedQuat::from_euler(euler_rad);
    [q.x, q.y, q.z, q.w]
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

/// キャラクターコントローラーを瞬間移動させる（衝突無視）。成功=1 / 失敗=0。
///
/// C# `Transform.Teleport(pos)` 用。idx/gen は対象アクターのルートエンティティ、
/// pos は 3 要素のワールド位置。次を行う:
///   1. ECS Transform 位置を pos に設定する（集約関数経由で子アクタも追従）。
///   2. キャラクター衝突ミラー（`CharacterWorld`）の解決基準を pos へリセットするため、
///      (entity_id, pos, rot) をテレポートキューへ積む。App がキャラ解決ステップ冒頭で消費し、
///      ミラー内キャラ位置を移動先へ直接セットする。これにより次の解決で motion≈0 となり、
///      移動先で押し戻されない（さもないと巨大 motion が壁で堰き止められテレポートが失敗する）。
///
/// is_character_controller でないアクターではミラーが無視するため、実質位置設定のみになる。
unsafe extern "system" fn ffi_teleport(
    idx: u32, generation: u32,
    pos: *const f32,
) -> i32 {
    if pos.is_null() { return 0; }
    let wptr = WORLD_PTR.with(|p| p.get());
    if wptr.is_null() { return 0; }
    let world  = &mut *wptr;
    let entity = Entity::from_raw(idx, generation);

    // 現在の Transform（回転・スケールを維持するため）を読む。無ければ対象外。
    let Some(cur) = world.get::<Transform>(entity).cloned() else { return 0 };
    let new_pos  = [*pos, *pos.add(1), *pos.add(2)];
    let rotation = euler_deg_to_quat_arr(cur.rotation);

    // 1. ECS Transform 位置を新位置へ設定（集約関数経由で instance_mats・全子孫へ伝播）
    let mut next = cur;
    next.position = new_pos;
    match actor_of_entity(entity) {
        Some(actor) => {
            crate::engine::core::transform_sync::set_actor_world_transform(actor, world, next, 0);
        }
        None => {
            if let Some(t) = world.get_mut::<Transform>(entity) { t.position = new_pos; }
        }
    }

    // 2. キャラクター衝突ミラーの解決基準リセットをテレポートキューへ積む。
    //    App がキャラ解決ステップ冒頭で消費する（キャラでなければミラーが無視する）。
    //    Actor ツリー未公開・DFS 逆引き失敗時は ECS 設定のみで完了扱い。
    let actors_ptr = ACTORS_PTR.with(|p| p.get());
    if !actors_ptr.is_null() {
        if let Some(entity_id) = dfs_id_by_entity(&*actors_ptr, entity) {
            CHARACTER_TELEPORTS.with(|q| q.borrow_mut().push((entity_id, new_pos, rotation)));
        }
    }
    1
}

/// キャラクターコントローラーの接地状態を返す。接地=1 / 非接地・未登録=0。
///
/// C# `Physics.IsGrounded(actor)` 用。メインスレッド常駐の CharacterWorld が KCC 解決時に
/// 求めた grounded を、フレームごとに ECS Entity をキーに公開（publish_grounded_states）した
/// スナップショットを参照する。
unsafe extern "system" fn ffi_is_grounded(idx: u32, generation: u32) -> i32 {
    let entity = Entity::from_raw(idx, generation);
    let grounded = GROUNDED_STATES.with(|p| {
        p.borrow().iter().find(|(e, _)| *e == entity).map(|(_, g)| *g).unwrap_or(false)
    });
    if grounded { 1 } else { 0 }
}

// ─── オーディオ FFI ──────────────────────────────────────────

/// オーディオコマンド種別（C# 側 Audio クラスの定数と一致させる）。
const AUDIO_CMD_PLAY_SE: i32 = 0;        // SE 再生（path, volume）
const AUDIO_CMD_PLAY_BGM: i32 = 1;       // BGM 再生（path, volume, flag=ループ 0/1）
const AUDIO_CMD_STOP_BGM: i32 = 2;       // BGM 停止
const AUDIO_CMD_SET_BGM_VOLUME: i32 = 3; // BGM 音量変更（volume）

/// オーディオコマンドを発行する。受理=1 / 失敗=0。
///
/// kind: 上記 AUDIO_CMD_*。path は SE/BGM 再生時のみ使用（それ以外は無視）。
/// volume は 1.0 = 等倍。flag は BGM 再生時のループ指定（0/1）。
unsafe extern "system" fn ffi_audio(
    kind: i32,
    path: *const u8, path_len: i32,
    volume: f32, flag: i32,
) -> i32 {
    let cmd = match kind {
        AUDIO_CMD_PLAY_SE => {
            let p = str_from(path, path_len);
            if p.is_empty() { return 0; }
            ScriptAudioCommand::PlaySe { path: p.to_string(), volume }
        }
        AUDIO_CMD_PLAY_BGM => {
            let p = str_from(path, path_len);
            if p.is_empty() { return 0; }
            ScriptAudioCommand::PlayBgm { path: p.to_string(), volume, looped: flag != 0 }
        }
        AUDIO_CMD_STOP_BGM       => ScriptAudioCommand::StopBgm,
        AUDIO_CMD_SET_BGM_VOLUME => ScriptAudioCommand::SetBgmVolume { volume },
        _ => return 0,
    };
    AUDIO_COMMANDS.with(|q| q.borrow_mut().push(cmd));
    1
}

/// AudioComponent 操作の種別（C# 側 AudioSource の定数と一致させる）。
const AUDIO_COMPONENT_PLAY: i32 = 0;
const AUDIO_COMPONENT_STOP: i32 = 1;
const AUDIO_COMPONENT_IS_PLAYING: i32 = 2;

thread_local! {
    /// AudioComponent の「再生中スロット」判定関数。
    /// AudioManager は App が所有するため、frame_renderer がフレームごとに
    /// 再生中スロットのスナップショットを公開する。
    static PLAYING_AUDIO_SLOTS: RefCell<Vec<Entity>> = const { RefCell::new(Vec::new()) };

    /// キャラクターコントローラーの接地状態スナップショット（アクターエンティティ → grounded）。
    /// sync_character_controllers ③（描画直前）がキャラ補正結果を反映するたびに更新・公開し、
    /// スクリプトの Physics.IsGrounded がこの値を参照する。
    /// 新しい補正結果が無いフレームでは前回値が維持される。
    static GROUNDED_STATES: RefCell<Vec<(Entity, bool)>> = const { RefCell::new(Vec::new()) };
}

/// キャラクターコントローラーの接地状態スナップショットを公開する。
/// sync_character_controllers ③ が描画直前に呼ぶ（DFS ID は ECS Entity へ変換済み）。
pub fn publish_grounded_states(states: Vec<(Entity, bool)>) {
    GROUNDED_STATES.with(|p| *p.borrow_mut() = states);
}

/// 再生中の AudioComponent スロット一覧を公開する（フレームごとに更新）。
pub fn publish_playing_audio_slots(slots: Vec<Entity>) {
    PLAYING_AUDIO_SLOTS.with(|p| *p.borrow_mut() = slots);
}

/// 2D アクターのスクリーン座標マップを公開する（フレームごとに更新）。
pub fn publish_screen_positions(positions: Vec<(Entity, [f32; 2])>) {
    SCREEN_POSITIONS.with(|p| *p.borrow_mut() = positions);
}

/// 2D アクターのスクリーン座標（ウィンドウ左上原点・ピクセル）を取得する。
/// 見つかった=1 / 2D アクターでない・未公開=0（out は 2 要素）。
unsafe extern "system" fn ffi_screen_position(idx: u32, generation: u32, out: *mut f32) -> i32 {
    if out.is_null() { return 0; }
    let entity = Entity::from_raw(idx, generation);
    let found = SCREEN_POSITIONS.with(|p| {
        p.borrow().iter().find(|(e, _)| *e == entity).map(|(_, pos)| *pos)
    });
    match found {
        Some(pos) => {
            *out        = pos[0];
            *out.add(1) = pos[1];
            1
        }
        None => 0,
    }
}

/// AudioComponent を操作する。成功（IsPlaying の場合は再生中）=1 / 失敗=0。
///
/// idx/gen は gameObject のルートエンティティ。AudioComponent のスロットを
/// locate で解決し、Play/Stop はコマンドキューへ積む（フレーム末尾に適用）。
unsafe extern "system" fn ffi_audio_component(action: i32, idx: u32, generation: u32) -> i32 {
    let ptr = WORLD_PTR.with(|p| p.get());
    if ptr.is_null() { return 0; }
    let world = &*ptr;
    let entity = Entity::from_raw(idx, generation);
    // ルートエンティティから AudioComponent のスロットエンティティを解決する
    let Some(slot) = locate::<AudioComponent>(world, entity) else { return 0 };

    match action {
        AUDIO_COMPONENT_PLAY => {
            AUDIO_COMMANDS.with(|q| q.borrow_mut().push(ScriptAudioCommand::PlayComponent { entity: slot }));
            1
        }
        AUDIO_COMPONENT_STOP => {
            AUDIO_COMMANDS.with(|q| q.borrow_mut().push(ScriptAudioCommand::StopComponent { entity: slot }));
            1
        }
        AUDIO_COMPONENT_IS_PLAYING => {
            let playing = PLAYING_AUDIO_SLOTS.with(|p| p.borrow().contains(&slot));
            if playing { 1 } else { 0 }
        }
        _ => 0,
    }
}

/// AnimatorComponent 操作の種別（C# 側 Animator の定数と一致させる）。
const ANIMATOR_COMPONENT_PLAY: i32   = 0; // clip 名 + speed（NaN = 変更なし）を指定して先頭から再生
const ANIMATOR_COMPONENT_STOP: i32   = 1; // 停止して time=0
const ANIMATOR_COMPONENT_PAUSE: i32  = 2; // playing=false のまま time は維持
const ANIMATOR_COMPONENT_RESUME: i32 = 3; // current_clip があれば playing=true に戻す

/// AnimatorComponent を操作する。成功=1 / 失敗=0。
///
/// idx/gen は gameObject のルートエンティティ。AnimatorComponent のスロットを locate で解決する。
/// Play/Stop/Pause/Resume は文字列（clip 名）や状態遷移を伴うため、汎用の
/// read_floats/write_floats（time・speed・playing の単純な数値フィールドのみ）とは別に
/// この専用 FFI で扱う。
///
/// clip_name/clip_name_len: Play 時のクリップ名（それ以外の action では無視）。
/// speed: Play 時の再生速度指定（NaN なら既存の speed を変更しない）。それ以外の action では無視。
unsafe extern "system" fn ffi_animator_component(
    action: i32, idx: u32, generation: u32,
    clip_name: *const u8, clip_name_len: i32,
    speed: f32,
) -> i32 {
    let ptr = WORLD_PTR.with(|p| p.get());
    if ptr.is_null() { return 0; }
    let world = &mut *ptr;
    let entity = Entity::from_raw(idx, generation);
    // ルートエンティティから AnimatorComponent のスロットエンティティを解決する
    let Some(slot) = locate::<AnimatorComponent>(world, entity) else { return 0 };
    let Some(a) = world.get_mut::<AnimatorComponent>(slot) else { return 0 };

    match action {
        ANIMATOR_COMPONENT_PLAY => {
            let name = str_from(clip_name, clip_name_len);
            // ロード済みキャッシュ（init_animators がフレーム先頭で clips を全ロード済み）に無ければ失敗
            if !a.cache.contains_key(name) {
                eprintln!("[SEED script] Animator.Play: クリップ '{name}' が見つかりません（clips 未登録 or 未ロード）");
                return 0;
            }
            a.current_clip = Some(name.to_string());
            a.time = 0.0;
            a.playing = true;
            if !speed.is_nan() { a.speed = speed; }
            1
        }
        ANIMATOR_COMPONENT_STOP => {
            a.playing = false;
            a.time = 0.0;
            1
        }
        ANIMATOR_COMPONENT_PAUSE => {
            a.playing = false;
            1
        }
        ANIMATOR_COMPONENT_RESUME => {
            // 再生対象クリップが無いまま resume しても無害（システム側が current_clip 未設定を無視する）
            if a.current_clip.is_some() { a.playing = true; }
            1
        }
        _ => 0,
    }
}

/// ParticleEmitterComponent 操作の種別（C# 側 ParticleEmitter の定数と一致させる）。
const PARTICLE_COMPONENT_PLAY: i32  = 0; // playing = true（放出開始）
const PARTICLE_COMPONENT_STOP: i32  = 1; // playing = false（放出停止）
const PARTICLE_COMPONENT_BURST: i32 = 2; // pending_burst に count を加算（即時一括放出）

/// ParticleEmitterComponent を操作する。成功=1 / 失敗=0。
///
/// idx/gen は gameObject のルートエンティティ。ParticleEmitterComponent のスロットを
/// locate で解決する。Play/Stop は playing フラグを直接切り替え、Burst は
/// ランタイム専用の pending_burst（非シリアライズ）へ count を積む
/// （GPU パーティクルシステムが毎フレーム消費してゼロに戻す契約）。
///
/// count: Burst 時のみ使用する放出個数（0 以下は 0 として無視。他 action では未使用）。
unsafe extern "system" fn ffi_particle_component(
    action: i32, idx: u32, generation: u32, count: i32,
) -> i32 {
    let ptr = WORLD_PTR.with(|p| p.get());
    if ptr.is_null() { return 0; }
    let world = &mut *ptr;
    let entity = Entity::from_raw(idx, generation);
    // ルートエンティティから ParticleEmitterComponent のスロットエンティティを解決する
    let Some(slot) = locate::<ParticleEmitterComponent>(world, entity) else { return 0 };
    let Some(p) = world.get_mut::<ParticleEmitterComponent>(slot) else { return 0 };

    match action {
        PARTICLE_COMPONENT_PLAY => { p.playing = true;  1 }
        PARTICLE_COMPONENT_STOP => { p.playing = false; 1 }
        PARTICLE_COMPONENT_BURST => {
            // 負値は 0 として扱い、飽和加算でオーバーフローを防ぐ。
            p.pending_burst = p.pending_burst.saturating_add(count.max(0) as u32);
            1
        }
        _ => 0,
    }
}

// ─── GetComponent<T> スロット解決 FFI ────────────────────────

/// GetComponent<T> のスロット entity を解決する。見つかった=1 / なし=0。
///
/// actor_idx/gen はスクリプトの gameObject（アクタールート entity）。kind は
/// C# ラッパーの ComponentKindName（"Transform"/"Camera"/"InputMap" …）。
/// name_len>0 のときはスロット名一致、そうでなければ index 番目（index<0 は 0）を選ぶ。
/// out_entity（[index, generation] の 2 要素）へ解決したスロット entity を書き込む。
///
/// Transform / CanvasTransform はアクタールート直付けのため、その entity 自身を返す
/// （index / name は無視。0 番目のみ存在）。
unsafe extern "system" fn ffi_resolve_component_slot(
    actor_idx: u32, actor_gen: u32,
    kind: *const u8, kind_len: i32,
    name: *const u8, name_len: i32,
    index: i32,
    out_entity: *mut u32,
) -> i32 {
    let ptr = WORLD_PTR.with(|p| p.get());
    if ptr.is_null() || out_entity.is_null() {
        return 0;
    }
    let world = &*ptr;
    let root = Entity::from_raw(actor_idx, actor_gen);
    let kind_s = str_from(kind, kind_len);
    // name_len<=0 は「名前指定なし（index で選ぶ）」を意味する
    let name_opt = if name_len > 0 { Some(str_from(name, name_len)) } else { None };

    match resolve_component_slot(world, root, kind_s, name_opt, index) {
        Some(e) => {
            *out_entity = e.index();
            *out_entity.add(1) = e.generation();
            1
        }
        None => 0,
    }
}

// ─── InputMap 評価 FFI ───────────────────────────────────────

// InputMap アクション評価の kind（C# 側 InputMap.cs の定数と一致必須）。
// 条件（Trigger/Press/Release）適用後のアクション状態に対する問い合わせ種別。
const INPUT_ACTION_KIND_ACTION: i32 = 0; // GetAction: 条件適用後の bool
const INPUT_ACTION_KIND_START: i32 = 1; // GetActionStart: アクション成立の瞬間
const INPUT_ACTION_KIND_END: i32 = 2; // GetActionEnd: アクション終了の瞬間

/// InputMap のアクションを評価する。真=1 / 偽・失敗=0。
///
/// slot_idx/gen は InputMapComponent のスロット entity（GetComponent<InputMap> で解決済み）。
/// kind: 0=action（条件適用後）/ 1=start（成立の瞬間）/ 2=end（終了の瞬間）。
/// name/len はアクション名。
///
/// 条件（Trigger/Press/Release）と Start/End のエッジ検出にはフレーム履歴が要るため、
/// asset_path 単位の ActionRuntime とグローバルフレーム番号を使う。
unsafe extern "system" fn ffi_input_action(
    slot_idx: u32, slot_gen: u32,
    kind: i32,
    name: *const u8, len: i32,
) -> i32 {
    let wptr = WORLD_PTR.with(|p| p.get());
    let iptr = INPUT_PTR.with(|p| p.get());
    if wptr.is_null() || iptr.is_null() {
        return 0;
    }
    let world = &*wptr;
    let input = &*iptr;
    let slot = Entity::from_raw(slot_idx, slot_gen);
    let name_s = str_from(name, len);
    let frame = current_script_frame();

    // input は KeyQuery と PadQuery の両方を実装する（キー＋パッド）。
    let result = with_action_map_and_runtime(world, slot, |m, rt| {
        m.eval_action(rt, input, input, name_s, frame)
    });

    let hit = match result {
        Some(r) => match kind {
            INPUT_ACTION_KIND_ACTION => r.action,
            INPUT_ACTION_KIND_START => r.start,
            INPUT_ACTION_KIND_END => r.end,
            _ => false,
        },
        None => false,
    };
    if hit { 1 } else { 0 }
}

/// InputMap の軸アクション（Axis1D / Vector2）を評価する。書き込んだ要素数を返す（失敗=0）。
///
/// slot_idx/gen は InputMapComponent のスロット entity。name/len はアクション名。
/// out_len>=2 のときは Vector2（[x, y] の 2 要素）、それ以外は Axis1D（[x] の 1 要素）を書き込む。
/// out は out_len 要素以上の容量を C# 側が保証する。
unsafe extern "system" fn ffi_input_action_axis(
    slot_idx: u32, slot_gen: u32,
    name: *const u8, len: i32,
    out: *mut f32, out_len: i32,
) -> i32 {
    if out.is_null() || out_len <= 0 {
        return 0;
    }
    let wptr = WORLD_PTR.with(|p| p.get());
    let iptr = INPUT_PTR.with(|p| p.get());
    if wptr.is_null() || iptr.is_null() {
        return 0;
    }
    let world = &*wptr;
    let input = &*iptr;
    let slot = Entity::from_raw(slot_idx, slot_gen);
    let name_s = str_from(name, len);

    // out_len で Axis2D / Axis1D を分岐（C# の GetVector2 / GetAxis の呼び分けに対応）。
    // input は KeyQuery と PadQuery の両方を実装する（キー＋パッド）。
    let written = with_action_map(world, slot, |m| {
        if out_len >= 2 {
            let v = m.eval_axis2d(input, input, name_s);
            *out = v[0];
            *out.add(1) = v[1];
            2
        } else {
            *out = m.eval_axis1d(input, input, name_s);
            1
        }
    });
    written.unwrap_or(0)
}

// ─── プロファイラ（SEED.Profiler.Begin / End）────────────────────
//
// スクリプトから任意区間を計測するための FFI。エンジンのフレームプロファイラ
// （engine/core/profiling）へ手動スコープとして積む。
//
// kind: 0 = Begin（name を使う）/ 1 = End（name は無視）
// 戻り値: 1 = 計測された / 0 = 計測されなかった
//         （プロファイラパネル非表示・名前の種類が上限超過・Begin と対応しない End 等）。
//
// 【安全性】スクリプトが Begin と End を対応させ損ねても、エンジン側のスコープは
// 壊れない（End はスクリプトが開いたスコープしか閉じず、閉じ忘れは親スコープの
// 終了時にまとめて閉じられる）。
unsafe extern "system" fn ffi_profiler(kind: i32, name: *const u8, name_len: i32) -> i32 {
    use crate::engine::core::profiling;

    /// `kind` の値: 計測開始。
    const PROFILER_KIND_BEGIN: i32 = 0;
    /// `kind` の値: 計測終了。
    const PROFILER_KIND_END: i32 = 1;

    match kind {
        PROFILER_KIND_BEGIN => {
            // 計測が無効なら文字列変換もインターンも行わない（無効時のコストを最小化）。
            if !profiling::is_enabled() {
                return 0;
            }
            let name_str = str_from(name, name_len);
            if name_str.is_empty() {
                return 0;
            }
            match profiling::intern_name(name_str) {
                Some(interned) => profiling::script_scope_begin(interned) as i32,
                None => 0,
            }
        }
        PROFILER_KIND_END => profiling::script_scope_end() as i32,
        _ => 0,
    }
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
    scene:             unsafe extern "system" fn(i32, *const u8, i32) -> i32,
    audio:             unsafe extern "system" fn(i32, *const u8, i32, f32, i32) -> i32,
    audio_component:   unsafe extern "system" fn(i32, u32, u32) -> i32,
    screen_position:   unsafe extern "system" fn(u32, u32, *mut f32) -> i32,
    animator_component: unsafe extern "system" fn(i32, u32, u32, *const u8, i32, f32) -> i32,
    particle_component: unsafe extern "system" fn(i32, u32, u32, i32) -> i32,
    // キャラクターコントローラー系（Transform.Teleport / Physics.IsGrounded）。
    // 新カテゴリ API のため構造体末尾に追加した（C# ScriptHost.cs も末尾に同順で追加）。
    teleport:           unsafe extern "system" fn(u32, u32, *const f32) -> i32,
    is_grounded:        unsafe extern "system" fn(u32, u32) -> i32,
    // GetComponent<T> / InputMap 系（新カテゴリ API のため構造体末尾に追加。
    // C# ScriptHost.cs も末尾に同順で追加すること）。
    resolve_component_slot: unsafe extern "system" fn(u32, u32, *const u8, i32, *const u8, i32, i32, *mut u32) -> i32,
    input_action:           unsafe extern "system" fn(u32, u32, i32, *const u8, i32) -> i32,
    input_action_axis:      unsafe extern "system" fn(u32, u32, *const u8, i32, *mut f32, i32) -> i32,
    // プロファイラ系（SEED.Profiler.Begin / End）。
    // 新カテゴリ API のため構造体末尾に追加した（C# ScriptHost.cs も末尾に同順で追加）。
    profiler:               unsafe extern "system" fn(i32, *const u8, i32) -> i32,
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
    scene:             ffi_scene,
    audio:             ffi_audio,
    audio_component:   ffi_audio_component,
    screen_position:   ffi_screen_position,
    animator_component: ffi_animator_component,
    particle_component: ffi_particle_component,
    teleport:           ffi_teleport,
    is_grounded:        ffi_is_grounded,
    resolve_component_slot: ffi_resolve_component_slot,
    input_action:           ffi_input_action,
    input_action_axis:      ffi_input_action_axis,
    profiler:               ffi_profiler,
};

/// C# へ渡す関数ポインタ表へのポインタを返す（RegisterHostApi 用）。
pub fn host_api_ptr() -> *const ScriptHostApi {
    &HOST_API
}

// ============================================================
//  ユニットテスト（GetComponent<T> のスロット解決）
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;
    use crate::engine::components::ComponentKind;

    /// スロット解決を検証するためのアクター＋World を組み立てる。
    ///
    /// ルート（root）に Transform を直付けし、Camera スロットを 2 つ
    /// （"CamA" / "CamB"）と Sprite スロットを 1 つ追加する。
    fn setup() -> (World, Actor) {
        let mut world = World::new();

        // ルート entity（Transform 直付け）
        let root = world.spawn();
        world.insert(root, Transform::default());
        let mut actor = Actor::new(root, "Player");

        // Camera スロット × 2（名前 CamA / CamB）
        let cam_a = world.spawn();
        world.insert(cam_a, CameraComponent::default());
        actor.add_slot_typed::<CameraComponent>("CamA", ComponentKind::Camera, cam_a);

        let cam_b = world.spawn();
        world.insert(cam_b, CameraComponent::default());
        actor.add_slot_typed::<CameraComponent>("CamB", ComponentKind::Camera, cam_b);

        // Sprite スロット × 1
        let spr = world.spawn();
        world.insert(spr, SpriteComponent::default());
        actor.add_slot_typed::<SpriteComponent>("Body", ComponentKind::Sprite, spr);

        (world, actor)
    }

    /// Transform はルート直付け（entity 自身を返し、index/name は無視）。
    #[test]
    fn resolve_transform_returns_root() {
        let (world, actor) = setup();
        let root = actor.entity;
        with_actors(&std::vec![actor], || {
            // 0 番目・index 指定・name 指定いずれもルートを返す
            assert_eq!(resolve_component_slot(&world, root, "Transform", None, 0), Some(root));
            assert_eq!(resolve_component_slot(&world, root, "Transform", None, 5), Some(root));
            assert_eq!(resolve_component_slot(&world, root, "Transform", Some("x"), -1), Some(root));
        });
    }

    /// CanvasTransform を持たないアクターでは None（is {} パターンが null になる）。
    #[test]
    fn resolve_missing_canvas_transform_is_none() {
        let (world, actor) = setup();
        let root = actor.entity;
        with_actors(&std::vec![actor], || {
            assert_eq!(resolve_component_slot(&world, root, "CanvasTransform", None, 0), None);
        });
    }

    /// index による Camera スロット選択（0 番目 = CamA, 1 番目 = CamB）。
    #[test]
    fn resolve_camera_by_index() {
        let (world, actor) = setup();
        let root = actor.entity;
        let cam_a = actor.slots()[0].entity;
        let cam_b = actor.slots()[1].entity;
        with_actors(&std::vec![actor], || {
            assert_eq!(resolve_component_slot(&world, root, "Camera", None, 0), Some(cam_a));
            assert_eq!(resolve_component_slot(&world, root, "Camera", None, 1), Some(cam_b));
            // index<0 は 0 番目扱い
            assert_eq!(resolve_component_slot(&world, root, "Camera", None, -1), Some(cam_a));
            // 範囲外は None
            assert_eq!(resolve_component_slot(&world, root, "Camera", None, 2), None);
        });
    }

    /// name による Camera スロット選択。
    #[test]
    fn resolve_camera_by_name() {
        let (world, actor) = setup();
        let root = actor.entity;
        let cam_b = actor.slots()[1].entity;
        with_actors(&std::vec![actor], || {
            assert_eq!(resolve_component_slot(&world, root, "Camera", Some("CamB"), -1), Some(cam_b));
            // 存在しない名前は None
            assert_eq!(resolve_component_slot(&world, root, "Camera", Some("Nope"), -1), None);
            // kind 不一致（Sprite スロット名 "Body" を Camera で引いても None）
            assert_eq!(resolve_component_slot(&world, root, "Camera", Some("Body"), -1), None);
        });
    }

    /// 別種スロット（Sprite）の 0 番目解決。
    #[test]
    fn resolve_sprite_slot() {
        let (world, actor) = setup();
        let root = actor.entity;
        let spr = actor.slots()[2].entity;
        with_actors(&std::vec![actor], || {
            assert_eq!(resolve_component_slot(&world, root, "Sprite", None, 0), Some(spr));
        });
    }
}
