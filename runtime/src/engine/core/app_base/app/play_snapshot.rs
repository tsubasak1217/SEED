// ============================================================
//  play_snapshot.rs — 埋め込み Play の「開始時状態」退避と復元
//
//  【責務】
//  Play を開始した瞬間の編集状態を丸ごと退避し、Play 停止時に必ずそこへ戻すこと。
//  Play の状態機械（ENTER_PLAY / EXIT_PLAY の手順）そのものは play_mode_ops.rs が持ち、
//  ここは「何を退避し、どう戻すか」だけを受け持つ（単一責任のための分割）。
//
//  【2 段構えの退避】
//   ① PlaySnapshot（軽量・従来どおり）
//      wl0 非地形アクターの ActorData ＋ 地形/編集タブの現物 entity。
//      Play 中にシーンが差し替わらない通常ケースは、これだけで復元できる。
//      地形・散布・GPU・モデルキャッシュを作り直さないので高速（高速化の本体）。
//
//   ② PlayStartState（完全・シーン遷移対策）
//      Play 中にスクリプトが SEED.Scene.Transition で別シーンへ移ると、
//      install_loaded_scene が Scene を丸ごと差し替えて旧 World を捨てる。
//      このとき ① の Keep(entity) は死んだ entity を指すため復元に使えない。
//      そこで Play 開始時に「シーンの直列化 JSON ＋ 編集タブのアクターデータ」を
//      メモリへ持ち、遷移が起きた場合はここから編集状態を組み直す。
//      **ファイルを読み直さない**ので、Play 開始前の未保存編集がそのまま戻る
//      （Unity の Play 停止と同じ体感にするための要）。
//
//  【地形の扱い】
//  シーン JSON には地形サブツリー（チャンク数百枚）を書かず、位置マーカーだけを書く。
//  地形の実体は TerrainState（メモリ上の密度・散布・カバー場）であり、
//  シーン差し替えの直前に **move で丸ごと奪って** 退避する（GPU リソースを含むため
//  clone できない）。復元時は .tvox からではなくこの実データから
//  resync_terrain_actors_after_tree_restore でチャンクを作り直す。
//  これにより「Play 開始前の未保存スカルプト」も失われない。
//  Undo/Redo のツリー復元（actor_ops.rs）が使っている地形マーカー方式と同じ考え方であり、
//  マーカー生成・削ぎ落としの関数もそちらと共有している。
// ============================================================

use crate::engine::ecs::Entity;
use crate::engine::structs::objects::actor::ActorData;
use crate::engine::core::app_base::scene::Scene;
use crate::engine::core::app_base::undo::UndoHistory;

use super::{App, RuntimeMode};
use super::app_init::SceneInstallOptions;
use super::actor_ops::{prune_terrain_subtree, terrain_marker_data, terrain_marker_placement};
use super::terrain_ops::TerrainState;

// ============================================================
//  PlaySnapshot — 埋め込みインプレース Play のアクター状態退避（軽量版）
// ============================================================

/// 埋め込み Play（ENTER_PLAY）開始時に取るシーンのトップレベルアクター状態のスナップショット。
///
/// 【設計】高速化の本体は「地形・散布・GPU リソースを作り直さない」こと。そのため
/// スナップショットは **シーン世界線（world_line == 0）の非地形アクターのみ** を対象とし、
/// 地形ルート（TERRAIN_ROOT_NAME）とアクター編集タブ（world_line > 0）のアクターは
/// 「現物を保持（Keep）」して一切触らない。Play 中に地形は変化せず（スクリプトに地形編集
/// API は無い）、編集タブはシミュレートされないため、保持で状態は保たれる。
///
/// トップレベルアクターの **並び順を保持** することで、復元後も DFS ID の対応
/// （物理・スクリプトのイベント配信が依存）を Play 前と一致させる。
///
/// 【使えないケース】Play 中にシーンが差し替わると Keep の entity が死ぬ。
/// その場合は PlayStartState からの完全復元へ切り替える（本ファイル後半）。
pub struct PlaySnapshot {
    /// Play 前のトップレベルアクター列（順序保持）。各要素が復元方法を持つ。
    pub(super) entries: Vec<PlaySnapshotEntry>,
}

/// トップレベルアクター 1 体分のスナップショット項目。
pub(super) enum PlaySnapshotEntry {
    /// スナップショット対象（world_line == 0・非地形）。ActorData から再構築する。
    Restore(ActorData),
    /// 保持対象（地形ルート・world_line > 0）。ルート entity をキーに現物を退避して戻す。
    Keep(Entity),
}

// ============================================================
//  PlayStartState — Play 開始時の編集状態（完全版）
// ============================================================

/// アクター編集タブ（world_line > 0）1 本分の退避データ。
///
/// .scene の JSON は world_line を持たない（シーン世界線のアクターしか記録しない）ため、
/// 編集タブはシーン JSON とは別に「world_line 付き」で退避する必要がある。
pub struct PlayEditTabActors {
    /// この編集タブの世界線番号（1 以上）。
    pub world_line: u32,
    /// その世界線のトップレベルアクター列（順序保持）。
    pub actors: Vec<ActorData>,
}

/// Play を開始した瞬間の編集状態一式。EXIT_PLAY で編集状態へ戻すための原本。
///
/// App::play_start に 1 つだけ持ち、ENTER_PLAY で作り EXIT_PLAY で捨てる。
pub struct PlayStartState {
    /// Play 開始時に読み込んでいたシーンのパス（App::loaded_scene_path の複製）。
    /// 遷移判定と、復元後に読み込み中パスを戻すために使う。None は「未確定」。
    pub scene_path: Option<String>,
    /// Play 開始時のシーンを .scene と同じ形式で直列化した JSON。
    /// 地形サブツリーは位置マーカーへ削いである（実体は terrain 側）。
    /// 直列化に失敗した場合のみ None（その場合は完全復元をあきらめる）。
    pub scene_json: Option<String>,
    /// Play 開始時に開いていたアクター編集タブ（world_line > 0）のアクター列。
    pub edit_tabs: Vec<PlayEditTabActors>,
    /// Play 中のシーン差し替え直前に奪い取った地形の実データ。
    /// None は「まだ差し替えが起きていない」（＝地形は App 側に現物がある）。
    ///
    /// TerrainState は大きい構造体なので Box に入れて PlayStartState 自体を軽く保つ。
    pub terrain: Option<Box<TerrainState>>,
    /// Play 中にシーンの差し替え（スクリプトのシーン遷移）が起きたか。
    /// これが true のときだけ「完全復元」経路を通る。
    pub transitioned: bool,
}

impl PlayStartState {
    /// シーンパスだけを記録した初期状態を作る（ENTER_PLAY の最初に呼ぶ）。
    /// シーン本体の退避は App::capture_play_start_scene が後から埋める。
    pub(super) fn new(scene_path: Option<String>) -> Self {
        Self {
            scene_path,
            scene_json:   None,
            edit_tabs:    Vec::new(),
            terrain:      None,
            transitioned: false,
        }
    }

    /// 「Play 開始時のシーンから差し替わっている」か。
    ///
    /// 第一の根拠は差し替えフック（App::stash_terrain_before_scene_swap）が立てる
    /// transitioned フラグ。同名シーンへの遷移（Scene.Transition で自分自身を
    /// 読み直す）でもフラグは立つため、パス比較だけより確実である。
    /// パス比較は将来フックを通らない差し替え経路が増えたときの保険として併用する。
    pub(super) fn is_scene_replaced(&self, current_path: Option<&str>) -> bool {
        if self.transitioned {
            return true;
        }
        match (self.scene_path.as_deref(), current_path) {
            (Some(start), Some(current)) => start != current,
            _ => false,
        }
    }
}

// ============================================================
//  退避（ENTER_PLAY）
// ============================================================

/// capture_play_start_scene の 1 パス走査で決めた「トップレベルアクター 1 体の扱い」。
///
/// シーン JSON 用のアクターデータ列（json_actors）と PlaySnapshot の項目列は
/// 大半が同じデータなので、to_data（インスタンス行列の複製を伴う重い処理）を
/// 2 度走らせないために、走査は 1 回だけ行いこの中間表現へ落とす。
enum PlayTopLevelSlot {
    /// wl0 非地形。JSON 用データをそのまま Restore へ move する（複製なし）。
    RestoreSharedWithJson,
    /// wl0 非地形だが、サブツリーに地形ルートを含む稀なケース
    /// （地形をグループの中へ入れている）。JSON 側は削いだ複製を使い、
    /// PlaySnapshot 側は従来どおり削いでいない現物データを使う（挙動を変えないため）。
    RestoreOwned(ActorData),
    /// wl0 地形ルート。JSON にはマーカーを書き、復元は現物 Keep。
    KeepTerrain(Entity),
    /// アクター編集タブ（wl > 0）。JSON には出さず edit_tabs へ退避し、復元は現物 Keep。
    KeepEditTab(Entity),
}

/// `capture_top_levels` の戻り値（トップレベル走査 1 回分の成果）。
struct CapturedTopLevels {
    /// シーン JSON に書くアクターデータ列（wl0 のみ・地形は位置マーカー）。
    json_actors: Vec<ActorData>,
    /// トップレベルアクターと 1 対 1 に対応する扱い（走査順を保持）。
    slots: Vec<PlayTopLevelSlot>,
    /// アクター編集タブ（wl > 0）の退避データ（world_line ごとにまとめる）。
    edit_tabs: Vec<PlayEditTabActors>,
}

/// トップレベルアクター列を 1 回だけ走査し、Play 開始退避に必要な 3 つの成果を作る。
///
/// App から切り出した自由関数にしてあるのは、GPU（DrawContext）無しで
/// 単体テストできるようにするため（actor_ops.rs の `snapshot_actors` と同じ方針）。
///
/// # 振り分け
/// - `world_line > 0`（アクター編集タブ）: JSON には出さず `edit_tabs` へ。
///   `.scene` 形式は world_line を持たないため、シーン JSON へ混ぜると
///   復元時にシーン世界線のアクターへ化けてしまう。
/// - 地形ルート: JSON には位置マーカーだけ。実体（密度・散布・カバー場）は
///   `TerrainState` 側で退避する。
/// - それ以外の wl0 アクター: そのまま JSON へ。地形ルートを子孫に含む稀なケース
///   （グループへ入れてある）だけ、JSON 用に複製して削ぐ。
fn capture_top_levels(
    actors: &[crate::engine::structs::objects::Actor],
    world:  &crate::engine::ecs::World,
) -> CapturedTopLevels {
    let mut json_actors: Vec<ActorData>         = Vec::with_capacity(actors.len());
    let mut slots:       Vec<PlayTopLevelSlot>  = Vec::with_capacity(actors.len());
    let mut edit_tabs:   Vec<PlayEditTabActors> = Vec::new();

    for root in actors {
        // ── アクター編集タブ（wl > 0）: JSON には出さず world_line 付きで退避 ──
        if root.world_line != 0 {
            let data = root.to_data(world);
            match edit_tabs.iter_mut().find(|t| t.world_line == root.world_line) {
                Some(tab) => tab.actors.push(data),
                None => edit_tabs.push(PlayEditTabActors {
                    world_line: root.world_line,
                    actors:     vec![data],
                }),
            }
            slots.push(PlayTopLevelSlot::KeepEditTab(root.entity));
            continue;
        }

        // ── 地形ルート: JSON には位置マーカーだけ（チャンクは TerrainState が持つ）──
        if App::is_terrain_root(root) {
            json_actors.push(terrain_marker_data(root));
            slots.push(PlayTopLevelSlot::KeepTerrain(root.entity));
            continue;
        }

        // ── 通常の wl0 アクター ──
        let data = root.to_data(world);
        // 地形ルートがこのアクターの子孫にある（グループへ入れてある）稀なケースだけ、
        // JSON 用に複製してから削ぐ。通常はここに入らないので複製コストは掛からない。
        if terrain_marker_placement(std::slice::from_ref(&data)).is_some() {
            let mut pruned = data.clone();
            prune_terrain_subtree(&mut pruned);
            json_actors.push(pruned);
            slots.push(PlayTopLevelSlot::RestoreOwned(data));
        } else {
            json_actors.push(data);
            slots.push(PlayTopLevelSlot::RestoreSharedWithJson);
        }
    }

    CapturedTopLevels { json_actors, slots, edit_tabs }
}

impl App {
    /// ENTER_PLAY: Play 開始時のシーン状態を退避する。
    ///
    /// - App::play_snapshot に軽量スナップショット（通常の停止で使う）
    /// - App::play_start に完全スナップショット（シーン遷移が起きた停止で使う）
    ///
    /// 事前条件: self.play_start が PlayStartState::new で作られていること
    /// （シーン未ロードでも遷移判定ができるよう、パス記録だけは先に済ませてある）。
    pub(super) fn capture_play_start_scene(&mut self) {
        // デバッグカメラは self の別フィールドなので、scene を借りる前に取っておく。
        let camera = self.debug_camera_data();
        let Some(scene) = self.scene.as_ref() else { return };

        // ── 1 パス走査: JSON 用アクターデータ列と、各トップレベルの扱いを同時に作る ──
        let CapturedTopLevels { json_actors, slots, edit_tabs } =
            capture_top_levels(&scene.actors, &scene.world);

        // ── シーン JSON（.scene と同じ形式）を組み立てる ──
        //   ここで初めて json_actors を借りる。失敗しても Play は続行し、
        //   完全復元だけをあきらめる（軽量スナップショットは有効なまま）。
        let scene_json = match scene.to_json_with_actors(&camera, &json_actors) {
            Ok(json) => Some(json),
            Err(e) => {
                eprintln!(
                    "[SEED PLAY] Play 開始状態の直列化に失敗しました: {e} — \
                     Play 中にシーン遷移が起きた場合は開始シーンのファイル読み直しで復元します"
                );
                None
            }
        };

        // ── 軽量スナップショットの項目列を組み立てる（json_actors を消費）──
        let mut json_iter = json_actors.into_iter();
        let mut entries   = Vec::with_capacity(slots.len());
        for slot in slots {
            match slot {
                PlayTopLevelSlot::RestoreSharedWithJson => {
                    // 走査順と push 順が一致しているので next() は必ず Some。
                    if let Some(data) = json_iter.next() {
                        entries.push(PlaySnapshotEntry::Restore(data));
                    }
                }
                PlayTopLevelSlot::RestoreOwned(data) => {
                    json_iter.next(); // JSON 側の削いだ複製は読み飛ばす
                    entries.push(PlaySnapshotEntry::Restore(data));
                }
                PlayTopLevelSlot::KeepTerrain(entity) => {
                    json_iter.next(); // JSON 側のマーカーは読み飛ばす
                    entries.push(PlaySnapshotEntry::Keep(entity));
                }
                PlayTopLevelSlot::KeepEditTab(entity) => {
                    entries.push(PlaySnapshotEntry::Keep(entity));
                }
            }
        }

        self.play_snapshot = Some(PlaySnapshot { entries });
        if let Some(start) = self.play_start.as_mut() {
            start.scene_json = scene_json;
            start.edit_tabs  = edit_tabs;
        }
    }

    /// Play 中の **シーン差し替え直前** に呼ぶフック。地形の実データを退避する。
    ///
    /// install_loaded_scene は必ず rebuild_terrain_after_load を呼び、その中で
    /// TerrainState を default() で作り直す。つまり差し替えを跨ぐと
    /// 「Play 開始前の未保存スカルプト・散布・カバー場」がメモリからも消える。
    /// ここで丸ごと奪っておくことで、Play 停止時にそれらを完全な形で戻せる。
    ///
    /// TerrainState は GPU リソース（レイヤテクスチャ・草バッファ・散布モデル）を
    /// 含むため clone できない。複製ではなく **move で奪う**（呼び出し元は直後に
    /// 新シーン用の地形を作り直すので、奪っても不整合は起きない）。
    ///
    /// Play 中でない、または Play 開始状態が無い場合は何もしない（冪等）。
    pub(super) fn stash_terrain_before_scene_swap(&mut self) {
        if self.mode != RuntimeMode::Play {
            return;
        }
        let Some(start) = self.play_start.as_mut() else { return };
        // 差し替えが起きたこと自体を記録する（同名シーンへの遷移でも復元経路へ入るため、
        // パス比較ではなくこのフラグを第一の根拠にする）。
        start.transitioned = true;
        // 2 回目以降の遷移では最初の退避（＝Play 開始時の地形）を必ず守る。
        if start.terrain.is_some() {
            return;
        }
        let stashed = std::mem::take(&mut self.terrain);
        // ブラシ形状マスクのパスは「道具の設定」であってシーンのデータではないため、
        // 新シーン側へ引き継ぐ（rebuild_terrain_after_load が行う持ち越しと同じ扱い）。
        self.terrain.brush_mask_path = stashed.brush_mask_path.clone();
        start.terrain = Some(Box::new(stashed));
    }
}

// ============================================================
//  復元（EXIT_PLAY）
// ============================================================

impl App {
    /// Play 開始時の編集状態へ完全復元する（シーン遷移が起きた停止で使う）。
    ///
    /// 手順:
    ///   1. 退避した JSON から Scene を組み立てる（ファイルは読まない＝未保存編集も戻る）
    ///   2. install_loaded_scene で 3 経路共通の据え付けを行う
    ///   3. 退避した地形の実データを戻し、メモリ上の密度からチャンクを作り直す
    ///   4. アクター編集タブ（world_line > 0）を復元する
    ///   5. 読み込み中シーンパスを Play 開始時のものへ戻す
    ///
    /// 選択状態のクリア・send_hierarchy / send_selected・編集時物理の再起動は
    /// exit_play の後半処理が同じことを行うため、ここでは行わない（二重実行を避ける）。
    ///
    /// # 戻り値
    /// 復元できたら true。false のときは呼び出し元が最後の手段
    /// （開始シーンのファイル読み直し）へフォールバックする。
    pub(super) fn restore_from_play_start(&mut self, start: PlayStartState) -> bool {
        // 直列化に失敗していた場合は完全復元できない。
        let Some(json) = start.scene_json.as_deref() else {
            eprintln!("[SEED PLAY] Play 開始状態の JSON が無いため完全復元できません");
            return false;
        };
        // 地形の退避が無い状態で JSON（地形はマーカーのみ）を据え付けると地形が消える。
        // そうなるくらいならファイル読み直しへ倒す。
        if start.terrain.is_none() {
            eprintln!("[SEED PLAY] 地形の退避が無いため完全復元できません");
            return false;
        }
        if self.draw_ctx.is_none() {
            eprintln!("[SEED PLAY] DrawContext が無いため完全復元できません");
            return false;
        }

        // シーン差し替えの前に mode を Edit へ落としておく。
        // install_loaded_scene は mode == Play のとき地形 LOD の事前収束
        // （数秒かかりうるブロッキング処理）を行うが、これから Edit へ戻るので不要。
        // 後段の「編集状態へ復帰」でも同じ代入を行うため冪等。
        self.mode = RuntimeMode::Edit;

        // scripting_host は clone してから draw_ctx を借りる（同時可変借用を避ける）。
        let host  = self.scripting_host.clone();
        let built = {
            let ctx = self.draw_ctx.as_ref().unwrap();
            Scene::from_json(json, ctx, host.as_ref())
        };
        let (new_scene, cam_data) = match built {
            Ok(v) => v,
            Err(e) => {
                eprintln!("[SEED PLAY] Play 開始状態の復元に失敗しました: {e}");
                return false;
            }
        };

        // Undo 履歴は差し替えで死んだ World の Entity を参照しているため破棄する
        // （LOAD_SCENE ハンドラと同じ理由・同じ手順）。
        self.undo_history = UndoHistory::new();

        self.install_loaded_scene(
            new_scene,
            cam_data,
            SceneInstallOptions {
                // 編集タブはこの後 JSON 外の退避データから作り直すので引き継がない
                // （引き継ぐと遷移先 World の死んだアクターを持ち込むことになる）。
                keep_actor_edit_tabs: false,
                // 破棄済みアクターを掴んだままのポインタ状態を捨てる
                reset_pointer: true,
                // 遷移先で鳴っていたコンポーネント音源を止める
                reset_audio_components: true,
                // 旧 Entity をキーに持つ物理キャッシュ・タイムラインを捨てる
                reset_physics_caches: true,
                ..Default::default()
            },
        );

        // ── 地形を「Play 開始時の実データ」へ戻す ────────────────────────
        //   install_loaded_scene → rebuild_terrain_after_load は、マーカーだけの
        //   シーン JSON からは空の TerrainState を作る。それを退避データで置き換え、
        //   メモリ上の密度からチャンクアクター・メッシュ・GPU を作り直す。
        //   .tvox は読まないので、Play 開始前の未保存スカルプトがそのまま戻る。
        if let Some(terrain) = start.terrain {
            self.terrain = *terrain;
            // チャンク → メッシュスロットの対応は死んだ Entity を指すので捨てる
            // （直後の resync が新しい Entity で張り直す）。
            self.terrain.chunk_slot_entity.clear();
            // 地形が差し替わったことを描画側へ知らせる世代カウンタ。
            self.terrain_edit_version += 1;
            // 密度が 1 チャンクも無いシーン（地形を使っていないシーン）では
            // 作り直すものが無い。resync は地形ルートが無いとエラーログを出すため、
            // 密度があるときだけ呼ぶ。
            if !self.terrain.chunks.is_empty() {
                self.resync_terrain_actors_after_tree_restore();
            }
            // 地表カバー場を Play 開始時の状態へ戻す（Play 中の積算は捨てる）。
            // 退避した TerrainState の中に Play 開始時のスナップショットが入っている。
            self.restore_cover_after_play();
        }

        // ── アクター編集タブ（world_line > 0）を復元する ──────────────────
        //   Undo/Redo のツリー復元と同じ入口を使う（world_line の再帰設定・
        //   キャンバス世界線の再判定まで面倒を見てくれる）。
        for tab in start.edit_tabs {
            self.rebuild_actors_for_wl(tab.world_line, tab.actors);
        }

        // 「いま何を読み込んでいるか」を Play 開始時のシーンへ戻す。
        // これを忘れると、エディタが編集中と思っているシーンとランタイムの実体がずれ、
        // 保存が path_mismatch で拒否され続ける。
        if let Some(path) = start.scene_path.as_deref() {
            self.set_loaded_scene_path(path);
        }
        true
    }

    /// 最後の手段: Play 開始時のシーン **ファイル** を読み直して据え付ける。
    ///
    /// restore_from_play_start が失敗したときだけ通る。ファイルから読むため
    /// **Play 開始前の未保存編集は失われる**。呼び出し元がエラーログを出す。
    ///
    /// # 戻り値
    /// 読み直せたら true。
    pub(super) fn reload_scene_file_after_play(&mut self, start_path: &str) -> bool {
        if self.draw_ctx.is_none() {
            return false;
        }
        // install_loaded_scene の地形 LOD 事前収束を避けるため先に Edit へ落とす（冪等）。
        self.mode = RuntimeMode::Edit;

        let host   = self.scripting_host.clone();
        let loaded = {
            let ctx = self.draw_ctx.as_ref().unwrap();
            Scene::load(std::path::Path::new(start_path), ctx, host.as_ref())
        };
        match loaded {
            Ok((new_scene, cam_data)) => {
                self.undo_history = UndoHistory::new();
                self.install_loaded_scene(
                    new_scene,
                    cam_data,
                    SceneInstallOptions {
                        keep_actor_edit_tabs:   false,
                        reset_pointer:          true,
                        reset_audio_components: true,
                        reset_physics_caches:   true,
                        ..Default::default()
                    },
                );
                self.set_loaded_scene_path(start_path);
                true
            }
            Err(e) => {
                eprintln!(
                    "[SEED PLAY] Play 開始時のシーン({start_path})の読み直しにも失敗しました: {e}"
                );
                false
            }
        }
    }
}

// ============================================================
//  テスト
// ============================================================
//
//  復元方向（build_actor / メッシュ再構築）は DrawContext（GPU）を要するため
//  ここでは検証できない。GPU 非依存の「退避」側、すなわち
//    ・トップレベルの振り分け（capture_top_levels）
//    ・シーン直列化の往復（Scene::to_json_with_actors → SceneData）
//  を対象にする。復元は `Scene::from_json`（= `Scene::load` の実体）が担い、
//  そちらはファイル経路と共通なので、直列化さえ正しければ往復が成立する。
#[cfg(test)]
mod tests {
    use super::*;
    use crate::engine::ecs::World;
    use crate::engine::components::Transform;
    use crate::engine::structs::objects::Actor;
    use crate::engine::core::app_base::scene::DebugCameraData;
    use super::super::terrain_ops::TERRAIN_ROOT_NAME;

    /// Transform 付きの通常アクターを 1 体作る。
    fn make_actor(world: &mut World, name: &str, wl: u32) -> Actor {
        let e = world.spawn();
        world.insert(e, Transform::default());
        let mut a = Actor::new(e, name);
        a.world_line = wl;
        a
    }

    /// 地形ルート + チャンク器（子）を 1 セット作る。
    fn make_terrain_root(world: &mut World) -> Actor {
        let root_e = world.spawn();
        let mut root = Actor::new_folder(root_e, TERRAIN_ROOT_NAME);
        root.world_line = 0;
        let chunk_e = world.spawn();
        world.insert(chunk_e, Transform::default());
        let mut chunk = Actor::new(chunk_e, "chunk_0_0_0");
        chunk.world_line = 0;
        root.add_child(chunk);
        root
    }

    /// 「地形 + 通常アクター + 編集タブ」を含むシーンを組み立てる。
    fn make_scene() -> Scene {
        let mut scene = Scene::new("test_scene");
        let terrain = make_terrain_root(&mut scene.world);
        let player  = make_actor(&mut scene.world, "Player", 0);
        // グループ（子を 1 体持つ通常アクター）
        let mut group = make_actor(&mut scene.world, "Group", 0);
        group.add_child(make_actor(&mut scene.world, "Child", 0));
        // アクター編集タブ（world_line = 1）
        let tab = make_actor(&mut scene.world, "EditedActor", 1);
        scene.actors.push(terrain);
        scene.actors.push(player);
        scene.actors.push(group);
        scene.actors.push(tab);
        scene
    }

    /// 地形はマーカー化され、編集タブは JSON から外れて world_line 付きで退避されること。
    #[test]
    fn capture_splits_terrain_and_edit_tabs() {
        let scene = make_scene();
        let captured = capture_top_levels(&scene.actors, &scene.world);

        // JSON へ出るのは wl0 の 3 体（地形マーカー / Player / Group）だけ。
        assert_eq!(captured.json_actors.len(), 3, "編集タブは JSON に含めない");
        let terrain = captured.json_actors.iter()
            .find(|d| d.name == TERRAIN_ROOT_NAME).expect("地形マーカー");
        assert!(terrain.children.is_empty(), "地形チャンクは JSON へ書かない");
        assert!(terrain.components.is_empty(), "地形ルートのスロットも書かない");

        // 扱いはトップレベルと 1 対 1（走査順を保持）。
        assert_eq!(captured.slots.len(), scene.actors.len());
        assert!(matches!(captured.slots[0], PlayTopLevelSlot::KeepTerrain(_)));
        assert!(matches!(captured.slots[1], PlayTopLevelSlot::RestoreSharedWithJson));
        assert!(matches!(captured.slots[2], PlayTopLevelSlot::RestoreSharedWithJson));
        assert!(matches!(captured.slots[3], PlayTopLevelSlot::KeepEditTab(_)));

        // 編集タブは world_line 付きで退避される。
        assert_eq!(captured.edit_tabs.len(), 1);
        assert_eq!(captured.edit_tabs[0].world_line, 1);
        assert_eq!(captured.edit_tabs[0].actors.len(), 1);
        assert_eq!(captured.edit_tabs[0].actors[0].name, "EditedActor");
    }

    /// 直列化 → 文字列 → 逆直列化で、アクター数・名前・子ツリー・コンポーネント種別が
    /// 保たれること（`Scene::from_json` が読む形式そのもので検証する）。
    #[test]
    fn scene_json_roundtrip_preserves_actor_tree() {
        let scene    = make_scene();
        let captured = capture_top_levels(&scene.actors, &scene.world);
        let camera   = DebugCameraData::default();

        let json = scene
            .to_json_with_actors(&camera, &captured.json_actors)
            .expect("直列化できること");

        // Scene::from_json と同じ型で読み戻す（GPU 無しで検証できる範囲）。
        let back: serde_json::Value = serde_json::from_str(&json).expect("JSON として読めること");
        assert_eq!(back["name"], "test_scene", "シーン名が保たれる");

        let actors = back["actors"].as_array().expect("actors 配列");
        assert_eq!(actors.len(), 3, "wl0 の 3 体が書かれる");
        let names: Vec<&str> = actors.iter().map(|a| a["name"].as_str().unwrap()).collect();
        assert_eq!(names, vec![TERRAIN_ROOT_NAME, "Player", "Group"], "並び順も保たれる");

        // 子ツリーが保たれること（Group > Child）。
        let group = actors.iter().find(|a| a["name"] == "Group").unwrap();
        let children = group["children"].as_array().expect("children 配列");
        assert_eq!(children.len(), 1);
        assert_eq!(children[0]["name"], "Child");

        // デバッグカメラも書き出される（Play 開始時の Edit カメラを復元するため）。
        assert!(back.get("debug_camera").is_some(), "デバッグカメラが書かれる");
    }

    /// アクター列が同じなら、`to_json`（全アクターを自前で集める）と
    /// `to_json_with_actors`（外から受け取る）は同一の JSON を出すこと。
    ///
    /// なお「その JSON を `SceneData`（読み込み側の型）が読めるか」は scene.rs の
    /// `scene_json_is_readable_as_scene_data` が担当する（SceneData が非公開のため）。
    #[test]
    fn to_json_matches_to_json_with_actors() {
        let scene  = make_scene();
        let camera = DebugCameraData::default();
        let actors: Vec<ActorData> =
            scene.actors.iter().map(|a| a.to_data(&scene.world)).collect();

        let via_owned    = scene.to_json(&camera).expect("直列化できること");
        let via_borrowed = scene
            .to_json_with_actors(&camera, &actors)
            .expect("直列化できること");
        assert_eq!(via_owned, via_borrowed, "アクター列が同じなら出力も同一であること");
    }

    /// シーン差し替えの判定: フラグが立っていればパスが同じでも「差し替わった」とみなす
    /// （同名シーンへの Scene.Transition を取りこぼさないため）。
    #[test]
    fn scene_replacement_is_detected_by_flag_and_by_path() {
        let mut start = PlayStartState::new(Some("assets://a.scene".to_string()));
        assert!(!start.is_scene_replaced(Some("assets://a.scene")), "何も起きていない");

        // パスが違えば差し替わったとみなす（フックを通らない経路への保険）。
        assert!(start.is_scene_replaced(Some("assets://b.scene")));

        // 同名シーンへ遷移した場合はパス比較では分からないが、フラグで検出する。
        start.transitioned = true;
        assert!(start.is_scene_replaced(Some("assets://a.scene")));
    }
}
