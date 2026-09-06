// ============================================================
//  audio_ops.rs — スクリプト発行のオーディオコマンド適用と
//                 AudioComponent の毎フレーム更新
//
//  C# スクリプトの SEED.Audio.Play / PlayBgm / StopBgm、および
//  AudioComponent の Play/Stop は host_api のコマンドキューに積まれる。
//  apply_script_audio_commands がフレーム末尾にそれらを適用する。
//
//  update_component_audio は AudioComponent の play_on_start 発火と、
//  spatial（3D 距離減衰・方向パン）/ pan（手動パン）の毎フレーム反映を行う。
// ============================================================

use crate::engine::components::{AudioComponent, CameraComponent, ComponentKind, Transform};
use crate::engine::core::audio::AudioManager;
use crate::engine::core::scripting::{ScriptAudioCommand, take_audio_commands};
use crate::engine::ecs::{Entity, World};
use crate::engine::structs::objects::Actor;

use super::{App, RuntimeMode};

// ─── 減衰・パンの定数 ─────────────────────────────────────────

/// 非 spatial 音源のパンをエミッタ方向へ変換するときの正面成分の最小値。
/// pan=±1 でも完全に真横にせず、わずかに正面成分を残して不自然な消音を防ぐ。
const PAN_FORWARD_MIN: f32 = 0.05;

impl App {
    /// スクリプトが積んだオーディオコマンドを適用し、再生済み SE を回収する。
    ///
    /// frame_renderer のゲームロジックブロック直後（シーンコマンド適用の隣）に呼ばれる。
    /// AudioManager は初回コマンド時に遅延初期化する
    /// （オーディオデバイスが無い環境では初期化に失敗し、全コマンドが無音で無視される）。
    pub(super) fn apply_script_audio_commands(&mut self) {
        let commands = take_audio_commands();

        // コマンドが無くても再生終了 SE の回収だけは行う
        if commands.is_empty() {
            if let Some(audio) = &mut self.audio {
                audio.cleanup();
            }
            return;
        }

        // 遅延初期化（デバイスなしでは None のまま = 無音動作）
        self.ensure_audio_manager();

        for cmd in commands {
            match cmd {
                ScriptAudioCommand::PlaySe { path, volume } => {
                    if let Some(audio) = &mut self.audio {
                        audio.play_se(&path, volume);
                    }
                }
                ScriptAudioCommand::PlayBgm {
                    path,
                    volume,
                    looped,
                } => {
                    if let Some(audio) = &mut self.audio {
                        audio.play_bgm(&path, volume, looped);
                    }
                }
                ScriptAudioCommand::StopBgm => {
                    if let Some(audio) = &mut self.audio {
                        audio.stop_bgm();
                    }
                }
                ScriptAudioCommand::SetBgmVolume { volume } => {
                    if let Some(audio) = &mut self.audio {
                        audio.set_bgm_volume(volume);
                    }
                }
                ScriptAudioCommand::SetBgmSpeed { speed } => {
                    if let Some(audio) = &mut self.audio {
                        audio.set_bgm_speed(speed);
                    }
                }
                ScriptAudioCommand::PlayComponent { entity } => {
                    // コンポーネントデータを読み取ってから再生する
                    //（scene と audio の借用を分離するため 2 段階で行う）
                    let params = self
                        .scene
                        .as_ref()
                        .and_then(|s| s.world.get::<AudioComponent>(entity))
                        .map(|a| (a.audio_path.clone(), a.volume, a.looped));
                    if let (Some((path, volume, looped)), Some(audio)) =
                        (params, self.audio.as_mut())
                    {
                        if !path.is_empty() {
                            audio.play_component(entity, &path, volume, looped);
                        }
                    }
                }
                ScriptAudioCommand::StopComponent { entity } => {
                    if let Some(audio) = &mut self.audio {
                        audio.stop_component(entity);
                    }
                }
            }
        }
        if let Some(audio) = &mut self.audio {
            audio.cleanup();
        }
    }

    /// AudioManager を必要に応じて初期化する（既に試行済みなら何もしない）。
    fn ensure_audio_manager(&mut self) {
        if self.audio.is_none() {
            self.audio = AudioManager::new();
            if self.audio.is_none() {
                eprintln!("[Script] Audio: オーディオデバイスの初期化に失敗しました（無音で継続）");
            }
        }
    }

    /// AudioComponent の毎フレーム更新（Play モードのみ）。
    ///
    /// 1. play_on_start が未発火のコンポーネントを自動再生する
    /// 2. 再生中の音源に距離減衰（spatial）または手動パンを反映する
    ///
    /// リスナーはメインカメラ（is_main な CameraComponent を持つアクター）。
    /// メインカメラが無い場合は減衰なし・中央で再生される。
    pub(super) fn update_component_audio(&mut self) {
        if self.mode != RuntimeMode::Play {
            return;
        }
        let Some(scene) = &self.scene else { return };

        // AudioComponent スロットを収集する: (スロット, データ, アクター位置)
        let mut sources: Vec<(Entity, AudioComponent, [f32; 3])> = Vec::new();
        collect_audio_sources(&scene.actors, &scene.world, &mut sources);
        if sources.is_empty() {
            return;
        }

        // 自動再生が必要なものがあれば AudioManager を初期化する
        let needs_autostart = sources
            .iter()
            .any(|(_, a, _)| a.play_on_start && !a.audio_path.is_empty());
        if needs_autostart {
            self.ensure_audio_manager();
        }
        let listener = find_listener_pose(
            &self.scene.as_ref().unwrap().actors,
            &self.scene.as_ref().unwrap().world,
        );
        let Some(audio) = &mut self.audio else { return };

        for (slot, comp, pos) in sources {
            // 1. play_on_start の自動再生（一度だけ発火する）
            if comp.play_on_start
                && !comp.audio_path.is_empty()
                && audio.component_needs_autostart(slot)
            {
                audio.play_component(slot, &comp.audio_path, comp.volume, comp.looped);
            }
            if !audio.is_component_playing(slot) {
                continue;
            }

            // 2. 減衰・パンの反映
            if comp.spatial {
                // ── 3D 空間再生: メインカメラとの距離で線形減衰 + 方向パン ──
                let (direction, attenuation) = match listener {
                    Some((lpos, yaw)) => {
                        let rel = [pos[0] - lpos[0], pos[1] - lpos[1], pos[2] - lpos[2]];
                        let dist = (rel[0] * rel[0] + rel[1] * rel[1] + rel[2] * rel[2]).sqrt();
                        // 線形減衰: min 以内=100% / max 以遠=0% / 間は線形
                        let range = (comp.max_distance - comp.min_distance).max(f32::EPSILON);
                        let att = (1.0 - (dist - comp.min_distance) / range).clamp(0.0, 1.0);
                        // リスナーローカル空間の方向（ヨーのみ考慮。x=右, y=上, z=前）
                        // カメラの前方 = +Z をヨーで回した向きと仮定する（Transform は YXZ オイラー・度）
                        let dir = if dist > f32::EPSILON {
                            let n = [rel[0] / dist, rel[1] / dist, rel[2] / dist];
                            let (sin_y, cos_y) = yaw.sin_cos();
                            // 右 = (cos, 0, -sin), 前 = (sin, 0, cos) への射影
                            [
                                n[0] * cos_y - n[2] * sin_y, // ローカル右成分
                                n[1],                        // 上成分
                                n[0] * sin_y + n[2] * cos_y, // ローカル前成分
                            ]
                        } else {
                            [0.0, 0.0, 1.0] // リスナーと同位置なら正面扱い
                        };
                        (dir, att)
                    }
                    // メインカメラが無い場合: 減衰なし・正面
                    None => ([0.0, 0.0, 1.0], 1.0),
                };
                audio.update_component_voice(slot, direction, comp.volume * attenuation);
            } else {
                // ── 2D 再生: pan（-1..1）でエミッタ方向を左右に振る ──
                let pan = comp.pan.clamp(-1.0, 1.0);
                let forward = (1.0 - pan * pan).sqrt().max(PAN_FORWARD_MIN);
                audio.update_component_voice(slot, [pan, 0.0, forward], comp.volume);
            }
        }
    }

    /// インスペクターからの AudioComponent フィールド更新（SET_AUDIO_FIELD IPC）。
    ///
    /// key: path / volume / loop / play_on_start / spatial / min_distance / max_distance / pan。
    /// 不正な key・value は無視される。更新後はインスペクターへ再送信する。
    pub(super) fn handle_set_audio_field(
        &mut self,
        actor_dfs_id: u32,
        slot_idx: u32,
        key: &str,
        value: &str,
    ) {
        use super::find_actor_by_dfs;

        let wl = self.active_world_line;
        // 対象スロットのエンティティを解決する
        let slot_entity = {
            let Some(scene) = &self.scene else { return };
            let mut c = 0u32;
            find_actor_by_dfs(&scene.actors, wl, actor_dfs_id, &mut c)
                .and_then(|a| a.slots().get(slot_idx as usize))
                .filter(|s| s.kind == ComponentKind::Audio)
                .map(|s| s.entity)
        };
        let Some(entity) = slot_entity else { return };
        let Some(scene) = &mut self.scene else { return };
        let Some(a) = scene.world.get_mut::<AudioComponent>(entity) else {
            return;
        };

        // key ごとに値を解釈して反映する（数値・真偽値のパース失敗は無視）
        match key {
            "path" => a.audio_path = value.to_string(),
            "volume" => {
                if let Ok(v) = value.parse::<f32>() {
                    a.volume = v.max(0.0);
                }
            }
            "loop" => a.looped = value == "1" || value == "true",
            "play_on_start" => a.play_on_start = value == "1" || value == "true",
            "spatial" => a.spatial = value == "1" || value == "true",
            "min_distance" => {
                if let Ok(v) = value.parse::<f32>() {
                    a.min_distance = v.max(0.0);
                }
            }
            "max_distance" => {
                if let Ok(v) = value.parse::<f32>() {
                    a.max_distance = v.max(0.0);
                }
            }
            "pan" => {
                if let Ok(v) = value.parse::<f32>() {
                    a.pan = v.clamp(-1.0, 1.0);
                }
            }
            _ => return,
        }

        self.send_actor_components(actor_dfs_id, self.actor_virtual_selected_slot_idx);
        if let Some(ipc) = &self.ipc {
            ipc.send("SCENE_MODIFIED");
        }
    }

    /// 再生中の AudioComponent スロット一覧を返す（IsPlaying 判定の公開用）。
    pub(super) fn playing_audio_slots(&self) -> Vec<Entity> {
        match &self.audio {
            Some(audio) => {
                let mut slots = Vec::new();
                let scene = self.scene.as_ref();
                if let Some(scene) = scene {
                    let mut sources = Vec::new();
                    collect_audio_sources(&scene.actors, &scene.world, &mut sources);
                    for (slot, _, _) in sources {
                        if audio.is_component_playing(slot) {
                            slots.push(slot);
                        }
                    }
                }
                slots
            }
            None => Vec::new(),
        }
    }
}

// ─── 収集ヘルパー ────────────────────────────────────────────

/// Actor ツリーを走査し、AudioComponent スロットを
/// (スロットエンティティ, コンポーネントのコピー, アクターの 3D 位置) で収集する。
/// 2D アクター（Transform なし）は位置 [0,0,0] として扱う（spatial は実質無効）。
fn collect_audio_sources(
    actors: &[Actor],
    world: &World,
    out: &mut Vec<(Entity, AudioComponent, [f32; 3])>,
) {
    for actor in actors {
        let pos = world
            .get::<Transform>(actor.entity)
            .map(|t| t.position)
            .unwrap_or([0.0, 0.0, 0.0]);
        for slot in actor.slots() {
            if slot.kind == ComponentKind::Audio {
                if let Some(a) = world.get::<AudioComponent>(slot.entity) {
                    out.push((slot.entity, a.clone(), pos));
                }
            }
        }
        collect_audio_sources(actor.children(), world, out);
    }
}

/// リスナー（メインカメラ）の位置とヨー角（ラジアン）を取得する。
/// is_main な CameraComponent を持つ最初のアクターを DFS で探す。見つからなければ None。
fn find_listener_pose(actors: &[Actor], world: &World) -> Option<([f32; 3], f32)> {
    for actor in actors {
        for slot in actor.slots() {
            if slot.kind == ComponentKind::Camera {
                if let Some(cam) = world.get::<CameraComponent>(slot.entity) {
                    if cam.is_main {
                        let tf = world.get::<Transform>(actor.entity)?;
                        // Transform.rotation は YXZ オイラー角（度）。ヨー = Y 成分。
                        return Some((tf.position, tf.rotation[1].to_radians()));
                    }
                }
            }
        }
        if let Some(found) = find_listener_pose(actor.children(), world) {
            return Some(found);
        }
    }
    None
}
