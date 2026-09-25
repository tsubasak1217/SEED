// ============================================================
//  snapshot_view_ops.rs — エディタの編集用ランタイムで「端末の一時停止の写し」を閲覧専用に出し、編集中のシーンへ戻す
//                         （SNAPSHOT_VIEW_BEGIN / SNAPSHOT_VIEW_END。docs/android.md §20.17）
//
//  【出す（begin_snapshot_view）】Edit のときだけ
//    1. 写しのファイルを読む（Scene::load。ここまでは何も変えない＝読めなければ FAILED:load で元のまま）
//    2. 最初の表示なら、編集中のシーンをメモリへ退避する（play_snapshot.rs の capture_edit_stash ＝ Play 開始時と同じ形:
//       シーン JSON・アクター編集タブ・デバッグカメラ ＋ 地形の実データを move ＋ Undo 履歴 ＋ ツール）。
//       退避できなければ FAILED:stash（元のまま）。ただし file_ok が付いていて読み込み中のシーンのパスがあれば、
//       戻すときに保存済みのファイルを読み直すことにして続ける（エディタは未保存の変更が無いときだけ付ける）。
//       既に出していれば退避は取り直さない（いまのシーンは前の写し）。
//    3. 写しを据え付ける（LOAD_SCENE と同じ。アクター編集タブは残す）。読み込み中のシーンのパスは写しのパスにする
//       （保存は下の拒否で止めるが、万一すり抜けても保存の突き合わせ〈scene_save_ops.rs〉が元のシーンへの上書きを弾く）。
//       ツールは選択に固定する（ギズモで動かせないように）。
//  【戻す（end_snapshot_view）】退避した状態から restore_from_play_start で組み直す（ファイルを読まない＝未保存の変更・
//    スカルプトも戻る）→ Undo 履歴とツールを戻す。組み直せなければ保存済みのファイルを読み直す（未保存の変更は失われる）。
//  【閲覧専用（refuse_in_snapshot_view）】出している間に届いた保存・編集・シーンの切り替えの命令は捨て、
//    SNAPSHOT_VIEW_REFUSED:<名前> を返す（保存は SAVE_ERROR:snapshot_view 等も返して相手の待ち合わせを解く）。
//    エディタの UI も無効表示にしているので、これは二重の守り（AI ツール・キーの転送もここで止まる）。
//  応答の書式は scene_snapshot/wire.rs。
// ============================================================

use std::path::Path;
use std::time::Instant;

use crate::engine::core::app_base::ipc::{IpcCommand, ToolMode};
use crate::engine::core::app_base::scene::Scene;
use crate::engine::core::app_base::scene_snapshot::{wire, RestoreKind, ViewFailStage};
use crate::engine::core::app_base::undo::UndoHistory;

use super::app_init::SceneInstallOptions;
use super::field_edit::{field_edit_target, FieldEditTarget};
use super::{App, PlayStartState, RuntimeMode};

/// ログの印（PC の編集用ランタイムの標準エラー）。
const VIEW_LOG_PREFIX: &str = "[SEED SNAPSHOT VIEW]";

/// 写しの表示中に保存・読み込みを拒んだ理由（エディタの保存・読み込みの失敗の表示にそのまま出る）。
const VIEW_REFUSED_REASON: &str = "端末の一時停止の写し（閲覧専用）を表示中のため受け付けません";

/// 写しの表示中の状態（出している間だけ App::snapshot_view にある）。
pub struct SnapshotViewState {
    /// メモリへ退避した編集中のシーン（None ならファイルの読み直しで戻す）。
    stash: Option<PlayStartState>,
    /// 写しを出す前に読み込んでいたシーンのパス（ファイルの読み直しに使う・戻した後に読み込み中へ戻す）。
    restore_path: Option<String>,
    /// 写しを出す前の Undo 履歴（写しの間は空。メモリから戻したときだけ書き戻す）。
    undo_history: UndoHistory,
    /// 写しを出す前のツール（写しの間は選択に固定）。
    tool_mode: ToolMode,
}

/// 写しの表示中に捨てる命令の扱い。
#[derive(Debug, Clone, PartialEq, Eq)]
pub(super) struct ViewRefusal {
    /// 知らせに載せる命令の名前。
    pub label: &'static str,
    /// 相手の待ち合わせを解くために返す応答（無ければ None）。
    pub reply: Option<String>,
}

impl App {
    // ── 出す ─────────────────────────────────────────────

    /// SNAPSHOT_VIEW_BEGIN: 編集中のシーンを退避して写しを閲覧専用で読み込み、応答する。
    pub(super) fn begin_snapshot_view(&mut self, path: &str, allow_file_restore: bool) {
        let started = Instant::now();
        let reply = match self.try_begin_snapshot_view(path, allow_file_restore) {
            Ok(actors) => {
                let elapsed_ms = started.elapsed().as_secs_f64() * 1000.0;
                eprintln!("{VIEW_LOG_PREFIX} 写しを閲覧専用で出しました: {path}（アクタ {actors}・{elapsed_ms:.1} ms）");
                wire::format_view_ready(path, actors, elapsed_ms)
            }
            Err((stage, reason)) => {
                eprintln!("{VIEW_LOG_PREFIX} 写しを出せませんでした（{}）: {path} — {reason}", stage.wire_name());
                wire::format_view_failed(path, stage, &reason)
            }
        };
        if let Some(ipc) = &self.ipc {
            ipc.send(&reply);
        }
    }

    /// 写しを出す本体。成功なら読み込んだアクタ数（子を含む）、失敗なら段階と理由（失敗のときは何も変えていない）。
    fn try_begin_snapshot_view(&mut self, path: &str, allow_file_restore: bool) -> Result<usize, (ViewFailStage, String)> {
        if self.mode != RuntimeMode::Edit {
            return Err((ViewFailStage::Mode, "編集用ランタイムが Edit ではありません（Play 中は写しを出せません）".to_string()));
        }

        // ── 1. 写しを読む（ここまでは何も変えない）──
        let host = self.scripting_host.clone();
        let loaded = {
            let Some(ctx) = self.draw_ctx.as_ref() else {
                return Err((ViewFailStage::Load, "描画の準備ができていません".to_string()));
            };
            Scene::load(Path::new(path), ctx, host.as_ref())
        };
        let (new_scene, camera) = loaded.map_err(|e| (ViewFailStage::Load, format!("写しを読めません: {e}")))?;
        let actors = count_actors(&new_scene);

        // ── 2. 最初の表示なら、編集中のシーンを退避する ──
        if self.snapshot_view.is_none() {
            // 進行中の配置・モーダルの変形は写しへ持ち込まない（LOAD_SCENE と同じく先に取り消す）
            self.cancel_placement();
            if self.modal_transform_active() {
                self.cancel_modal_transform();
            }
            let mut stash = match self.capture_edit_stash() {
                Ok(state) => Some(state),
                Err(reason) if allow_file_restore && self.loaded_scene_path.is_some() => {
                    eprintln!(
                        "{VIEW_LOG_PREFIX} 編集中のシーンをメモリへ退避できないため、戻すときは保存済みのファイルを読み直します: {reason}"
                    );
                    None
                }
                Err(reason) => return Err((ViewFailStage::Stash, reason)),
            };
            // 地形の実データを差し替えの直前に move で退避する（GPU の資源を含むので複製できない。
            // ブラシの形は道具の設定なので写しの側へ引き継ぐ。play_snapshot.rs の stash_terrain_before_scene_swap と同じ扱い）
            if let Some(state) = stash.as_mut() {
                let terrain = std::mem::take(&mut self.terrain);
                self.terrain.brush_mask_path = terrain.brush_mask_path.clone();
                state.terrain = Some(Box::new(terrain));
            }
            self.snapshot_view = Some(SnapshotViewState {
                stash,
                restore_path: self.loaded_scene_path.clone(),
                undo_history: std::mem::replace(&mut self.undo_history, UndoHistory::new()),
                tool_mode: self.tool_mode,
            });
        } else {
            // 写しの差し替え: 退避は取り直さない。写しの間に積んだ履歴は捨てる
            self.undo_history = UndoHistory::new();
        }

        // ── 3. 写しを据え付ける（LOAD_SCENE と同じ。アクター編集タブは残す）──
        self.install_scene_for_snapshot_view(new_scene, camera);
        self.set_loaded_scene_path(path);
        self.set_tool_mode_and_notify(ToolMode::Select);
        Ok(actors)
    }

    /// 写し・戻したシーンを据え付けた後の共通の後始末（選択・物理・ヒエラルキー・カメラの知らせ）。
    fn after_snapshot_view_swap(&mut self) {
        // 選択は旧シーンの Entity を指しているので捨てる
        self.selected_instances.clear();
        self.selected_actor_dfs_ids.clear();
        self.actor_virtual_selected_idx = None;
        self.actor_virtual_selected_slot_idx = 0;
        // 旧 Entity をキーに持つタブ物理・速度キャッシュを捨て、編集時物理を初期状態で動かし直す（exit_play と同じ手順）
        self.tab_physics.clear();
        self.current_vel_cache_3d.clear();
        self.current_vel_cache_2d.clear();
        self.pending_restore_vel_3d = None;
        self.pending_restore_vel_2d = None;
        self.reset_physics_timeline();
        if self.edit_physics_enabled {
            self.stop_physics();
            self.start_physics();
        }
        if self.edit_physics_2d_enabled {
            self.stop_physics_2d();
            self.start_physics_2d();
        }
        if self.is_edit_physics_pushback_mode() {
            self.enter_edit_physics_pushback();
        } else if self.edit_physics_enabled || self.edit_physics_2d_enabled {
            self.init_physics_timeline();
        }
        self.send_selected();
        // ツリーが丸ごと入れ替わったので、差分ではなく全再構築を知らせる
        self.send_hierarchy_reset();
        if let Some(ipc) = &self.ipc {
            let (pos, euler_x, euler_y, euler_z, fov, far, spd) = self.cam_state_tuple();
            ipc.send(&format!("CAM_STATE:{pos},{euler_x},{euler_y},{euler_z},{fov},{far},{spd}"));
        }
    }

    /// 写しを据え付ける（LOAD_SCENE の Edit の読み込みと同じ据え付け。シーン世界線を表示する）。
    fn install_scene_for_snapshot_view(
        &mut self,
        new_scene: Scene,
        camera: Option<crate::engine::core::app_base::scene::DebugCameraData>,
    ) {
        self.active_world_line = 0;
        self.install_loaded_scene(
            new_scene,
            camera,
            SceneInstallOptions {
                // アクター編集タブ（world_line > 0）は旧シーンから引き継ぐ（戻すときは退避から作り直す）
                keep_actor_edit_tabs: true,
                reset_physics_caches: true,
                ..Default::default()
            },
        );
        self.after_snapshot_view_swap();
    }

    // ── 戻す ─────────────────────────────────────────────

    /// SNAPSHOT_VIEW_END: 退避した編集中のシーンへ戻して応答する（出していなければ none）。
    pub(super) fn end_snapshot_view(&mut self) {
        let started = Instant::now();
        let Some(view) = self.snapshot_view.take() else {
            if let Some(ipc) = &self.ipc {
                ipc.send(&wire::format_view_ended(RestoreKind::None, "", 0.0));
            }
            return;
        };

        let (kind, detail) = self.restore_after_snapshot_view(view);
        self.after_snapshot_view_swap();
        let elapsed_ms = started.elapsed().as_secs_f64() * 1000.0;
        eprintln!("{VIEW_LOG_PREFIX} 写しの表示をやめました（{}）: {detail}（{elapsed_ms:.1} ms）", kind.wire_name());
        if let Some(ipc) = &self.ipc {
            ipc.send(&wire::format_view_ended(kind, &detail, elapsed_ms));
        }
    }

    /// 退避した状態から戻す（メモリ → だめならファイル）。戻し方と、戻したシーンのパスか理由を返す。
    fn restore_after_snapshot_view(&mut self, view: SnapshotViewState) -> (RestoreKind, String) {
        self.set_tool_mode_and_notify(view.tool_mode);
        let restore_path = view.restore_path.clone().unwrap_or_default();

        // ① メモリから組み直す（ファイルを読まない＝未保存の変更・スカルプトも戻る）
        if let Some(stash) = view.stash {
            if self.restore_from_play_start(stash) {
                self.undo_history = view.undo_history;
                if view.restore_path.is_none() {
                    // 一度も保存していないシーン: 読み込み中のパスは無いまま（写しのパスを残さない）
                    self.loaded_scene_path = None;
                }
                return (RestoreKind::Memory, restore_path);
            }
            eprintln!("{VIEW_LOG_PREFIX} メモリへ退避した編集中のシーンを組み直せませんでした。保存済みのファイルを読み直します");
        }

        // ② 保存済みのファイルを読み直す（未保存の変更は失われる。履歴も前提が崩れるので空のまま）
        match view.restore_path {
            Some(path) if self.reload_scene_file_after_play(&path) => (RestoreKind::File, path),
            Some(path) => (RestoreKind::Failed, format!("編集中のシーンを組み直せず、{path} の読み直しにも失敗しました")),
            None => (RestoreKind::Failed, "編集中のシーンを組み直せず、読み直せる保存済みのシーンもありません".to_string()),
        }
    }

    /// ツールを変えてエディタのツールバーへ知らせる（TOOL_MODE:）。
    fn set_tool_mode_and_notify(&mut self, tool: ToolMode) {
        self.tool_mode = tool;
        if let Some(ipc) = &self.ipc {
            ipc.send(&format!("TOOL_MODE:{}", tool_mode_wire_name(tool)));
        }
    }

    // ── 閲覧専用 ───────────────────────────────────────────

    /// 端末の写しを閲覧専用で出しているか（ランタイムのウィンドウのキー入力〈ツールのホットキー〉も止めるのに使う）。
    pub(super) fn is_snapshot_view_active(&self) -> bool {
        self.snapshot_view.is_some()
    }

    /// 写しの表示中なら、保存・編集・シーンの切り替えの命令を捨てる（ipc_handler.rs の命令ごとの最初に呼ぶ）。
    ///
    /// # 戻り値
    /// 捨てたら true（呼び出し側はその命令を処理しない）。
    pub(super) fn refuse_in_snapshot_view(&self, command: &IpcCommand) -> bool {
        if self.snapshot_view.is_none() {
            return false;
        }
        let Some(refusal) = snapshot_view_refusal(command) else { return false };
        eprintln!("{VIEW_LOG_PREFIX} 閲覧専用のため捨てました: {}", refusal.label);
        if let Some(ipc) = &self.ipc {
            if let Some(reply) = &refusal.reply {
                ipc.send(reply);
            }
            ipc.send(&wire::format_view_refused(refusal.label));
        }
        true
    }
}

/// 写しの表示中に捨てる命令か（純粋な処理）。捨てるなら名前と返す応答。
///
/// 捨てるのは「写し（や、退避した元のシーン・プロジェクトのファイル）を変えるもの」:
///   保存・書き出し（元のシーンを写しの内容で上書きしない）／シーンの切り替え・Play・アクター編集タブ／
///   ツール（選択に固定）・モーダルの変形・配置・ドロップ／ツリーの編集・Undo/Redo・貼り付け／地形の編集／
///   インスペクタの値の編集（field_edit.rs の分類。Undo に載る SET_* 系）／AI ツールの編集。
/// 見るための命令（カメラ・選択・問い合わせ・表示の設定・撮影）は通す。
pub(super) fn snapshot_view_refusal(command: &IpcCommand) -> Option<ViewRefusal> {
    use IpcCommand as C;
    let with_reply = |label: &'static str, reply: String| Some(ViewRefusal { label, reply: Some(reply) });
    let plain = |label: &'static str| Some(ViewRefusal { label, reply: None });
    match command {
        // ── 保存・書き出し（待っている相手に失敗を返す）──
        C::SaveScene(_) => with_reply("SAVE_SCENE", wire::SAVE_ERROR_SNAPSHOT_VIEW.to_string()),
        C::SaveSceneAs(_) => with_reply("SAVE_SCENE_AS", wire::SAVE_ERROR_SNAPSHOT_VIEW.to_string()),
        C::SaveSceneCopy(_) => with_reply("SAVE_SCENE_COPY", wire::SAVE_ERROR_SNAPSHOT_VIEW.to_string()),
        C::SaveActor(_) => with_reply("SAVE_ACTOR", wire::SAVE_ERROR_SNAPSHOT_VIEW.to_string()),
        C::TerrainSave => with_reply("TERRAIN_SAVE", format!("TERRAIN_SAVE_ERROR:{VIEW_REFUSED_REASON}")),
        C::TerrainSaveAs { .. } => with_reply("TERRAIN_SAVE_AS", format!("TERRAIN_SAVE_AS_ERROR:{VIEW_REFUSED_REASON}")),
        C::ExportActor { .. } => with_reply("EXPORT_ACTOR", format!("EXPORT_ACTOR_ERR:{VIEW_REFUSED_REASON}")),

        // ── シーンの切り替え・Play・アクター編集タブ ──
        C::LoadScene(_) => with_reply("LOAD_SCENE", format!("LOAD_ERROR:{VIEW_REFUSED_REASON}")),
        // Play の開始を待っているエディタを Edit へ戻す（PLAY_ENTERED は来ない）
        C::EnterPlay => with_reply("ENTER_PLAY", "PLAY_EXITED".to_string()),
        C::OpenActor { .. } => plain("OPEN_ACTOR"),
        C::EditCanvasBegin { .. } => plain("EDIT_CANVAS_BEGIN"),
        C::SetActiveWorldLine(_) => plain("SET_ACTIVE_WORLD_LINE"),
        C::RemoveWorldLine(_) => plain("REMOVE_WORLD_LINE"),

        // ── ツール・モーダルの変形・配置・ドロップ（ギズモで動かさない）──
        // 選択ツールは見るための道具なので通す（固定しているのと同じ）。それ以外は選択へ戻して知らせ直す
        C::SetToolMode(tool) | C::SetToolModeFromHotkey(tool) if *tool != ToolMode::Select => {
            with_reply("SET_TOOL_MODE", "TOOL_MODE:SELECT".to_string())
        }
        C::ModalBegin(_) => with_reply("MODAL_BEGIN", "MODAL_STATE:0".to_string()),
        C::LogicPlaceBegin { .. } | C::LogicPlace { .. } => plain("LOGIC_PLACE"),
        C::DropActor { .. } => plain("DROP_ACTOR"),
        C::BeginTransformDrag { .. } => plain("BEGIN_TRANSFORM_DRAG"),

        // ── ツリーの編集・履歴・貼り付け ──
        C::Undo => plain("UNDO"),
        C::Redo => plain("REDO"),
        C::Paste => plain("PASTE"),
        C::Delete(_) | C::DeleteRecursive(_) | C::RemoveActor(_) => plain("DELETE"),
        C::Reparent { .. } => plain("REPARENT"),
        C::Rename { .. } | C::RenameActor { .. } => plain("RENAME"),
        C::CreateGroup { .. } | C::CreateGroupWithChildren { .. } => plain("CREATE_GROUP"),
        C::AddActor { .. } | C::AddActor2D { .. } | C::AddActorChild { .. } | C::AddActor2dChild { .. } | C::WrapActor { .. } => {
            plain("ADD_ACTOR")
        }
        C::AddComponent { .. } | C::RemoveComponentSlot { .. } | C::RenameComponentSlot { .. } | C::DuplicateComponent { .. } => {
            plain("EDIT_COMPONENT")
        }
        C::SetTransform { .. } | C::SetActorTransform { .. } | C::SetCanvasTransform { .. } => plain("SET_TRANSFORM"),
        C::SetActorActive { .. } | C::SetActorVisible { .. } | C::SetSlotEnabled { .. } => plain("SET_ACTIVE"),
        C::SetModelPath { .. } | C::SetControlPoints { .. } | C::SetControlPointPos { .. } | C::AddControlPointAtScreen { .. } => {
            plain("EDIT_COMPONENT")
        }
        C::CreateSpriteBoneActors { .. } => plain("CREATE_SPRITE_BONES"),
        C::UnlinkPrefab { .. } | C::ReapplyPrefab { .. } | C::ReapplyPrefabPath { .. } | C::ReapplyAllPrefabs => plain("PREFAB"),
        C::AiAddActor { .. } | C::AiRemoveActor { .. } | C::AiMoveActor { .. } | C::AiAddComponent { .. } | C::AiSetValue { .. } => {
            plain("AI_EDIT")
        }

        // ── 地形の編集（写しの地形を変えても意味が無く、保存も拒むので最初から受け付けない）──
        C::TerrainInit { .. }
        | C::TerrainAddChunks { .. }
        | C::TerrainBrush { .. }
        | C::TerrainPaint { .. }
        | C::TerrainSharpness { .. }
        | C::TerrainCollisionToggle { .. }
        | C::TerrainDecimate { .. }
        | C::TerrainUndo
        | C::TerrainRedo
        | C::TerrainCoverBrush { .. }
        | C::TerrainScatterBrush { .. }
        | C::TerrainScatterRules { .. }
        | C::TerrainHeightmap { .. }
        | C::TerrainCoverStep { .. }
        | C::TerrainCoverClear
        | C::TerrainCoverSimStart => plain("TERRAIN_EDIT"),

        // ── インスペクタの値の編集（Undo に載る SET_* 系。分類の正典は field_edit.rs）──
        other if !matches!(field_edit_target(other), FieldEditTarget::None) => plain("FIELD_EDIT"),
        _ => None,
    }
}

/// ツールの IPC の名前（event_handler.rs の TOOL_MODE: と同じ）。
fn tool_mode_wire_name(tool: ToolMode) -> &'static str {
    match tool {
        ToolMode::Select => "SELECT",
        ToolMode::Move => "MOVE",
        ToolMode::Rotate => "ROTATE",
        ToolMode::Scale => "SCALE",
    }
}

/// シーンのアクタの数（子を含む）。
fn count_actors(scene: &Scene) -> usize {
    fn count(actor: &crate::engine::structs::objects::Actor) -> usize {
        1 + actor.children().iter().map(count).sum::<usize>()
    }
    scene.actors.iter().map(count).sum()
}

// ============================================================
//  テスト
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;

    /// 保存・シーンの切り替えは捨て、相手の待ち合わせを解く応答を返すこと。
    #[test]
    fn saves_and_scene_switches_are_refused_with_replies() {
        let save = snapshot_view_refusal(&IpcCommand::SaveScene("C:/a.scene".into())).expect("捨てる");
        assert_eq!(save.label, "SAVE_SCENE");
        assert_eq!(save.reply.as_deref(), Some(wire::SAVE_ERROR_SNAPSHOT_VIEW));
        assert!(snapshot_view_refusal(&IpcCommand::SaveSceneAs("C:/b.scene".into())).is_some());
        assert!(snapshot_view_refusal(&IpcCommand::SaveSceneCopy("C:/c.scene".into())).is_some());
        assert!(snapshot_view_refusal(&IpcCommand::TerrainSave).unwrap().reply.unwrap().starts_with("TERRAIN_SAVE_ERROR:"));
        assert!(snapshot_view_refusal(&IpcCommand::LoadScene("C:/d.scene".into())).unwrap().reply.unwrap().starts_with("LOAD_ERROR:"));
        assert_eq!(snapshot_view_refusal(&IpcCommand::EnterPlay).unwrap().reply.as_deref(), Some("PLAY_EXITED"));
    }

    /// 編集（ツリー・履歴・ツール・値）は捨てること。ツールは選択に固定したまま知らせ直す。
    #[test]
    fn edits_are_refused() {
        assert!(snapshot_view_refusal(&IpcCommand::Undo).is_some());
        assert!(snapshot_view_refusal(&IpcCommand::Delete(vec![1])).is_some());
        assert_eq!(
            snapshot_view_refusal(&IpcCommand::SetToolMode(ToolMode::Move)).unwrap().reply.as_deref(),
            Some("TOOL_MODE:SELECT")
        );
        let field = IpcCommand::SetLightField { actor_dfs_id: 1, slot_idx: 0, key: "intensity".into(), value: "2".into() };
        assert_eq!(snapshot_view_refusal(&field).unwrap().label, "FIELD_EDIT");
    }

    /// 見るための命令（選択・カメラ・問い合わせ・表示の設定・終わる命令）は通すこと。
    #[test]
    fn viewing_commands_pass() {
        assert!(snapshot_view_refusal(&IpcCommand::Select(3)).is_none());
        assert!(snapshot_view_refusal(&IpcCommand::GetActorComponents(3)).is_none());
        assert!(snapshot_view_refusal(&IpcCommand::GetCamState).is_none());
        assert!(snapshot_view_refusal(&IpcCommand::CamKeyDown("W".into())).is_none());
        assert!(snapshot_view_refusal(&IpcCommand::SetShowGrid(true)).is_none());
        assert!(snapshot_view_refusal(&IpcCommand::Stop).is_none());
        assert!(snapshot_view_refusal(&IpcCommand::SetToolMode(ToolMode::Select)).is_none(), "選択ツールは見るための道具");
        assert!(snapshot_view_refusal(&IpcCommand::SetCameraTransform {
            px: 0.0, py: 1.0, pz: 2.0, euler_x: 0.0, euler_y: 0.0, euler_z: 0.0
        })
        .is_none());
    }
}
