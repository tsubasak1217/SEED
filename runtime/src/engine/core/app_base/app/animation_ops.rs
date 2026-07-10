// ============================================================
//  animation_ops.rs — AnimationSystem の App 統合（毎フレーム評価ループ）
//
//  【役割】
//  Play モード中に AnimatorComponent を評価し、各トラックを対象アクターの
//  コンポーネントへ書き込む。アクター木（scene.actors）とコンポーネント実体
//  （scene.world）を跨ぐ処理のため、純ロジック（engine::animation::system）を
//  App 側の本モジュールから駆動する。
//
//  【毎フレームの流れ】
//    1. init_animators: 未初期化アニメーターのクリップをロードし、
//       play_on_start なら default_clip を再生開始にする。
//    2. update_animations: 各アニメーターの time を進め、loop_mode で正規化し、
//       トラックを評価してレジストリ経由で書き込む。
//
//  Edit モードでは動かさない（P2 でプレビュー対応予定）。
// ============================================================

use std::sync::Arc;

use crate::engine::animation::{self, AnimationClip};
use crate::engine::components::{AnimatorComponent, AnimClipRef, ComponentKind};
use crate::engine::ecs::Entity;
use crate::engine::structs::objects::Actor;

use super::{App, RuntimeMode};

impl App {
    /// AnimationSystem の毎フレームエントリポイント（Play・非ポーズ時のみ）。
    ///
    /// frame_renderer のゲームロジックブロック先頭（スクリプト更新より前）で呼ばれる。
    /// dt はゲーム時間の delta（Time API と同源の ctx.delta_time）。
    pub(super) fn update_animations(&mut self, dt: f32) {
        if self.mode != RuntimeMode::Play || self.paused { return; }

        // 1. 未初期化アニメーターのクリップロード & play_on_start 発火
        self.init_animators();

        // 2. 評価 & 書き込み
        let Some(scene) = self.scene.as_mut() else { return };
        // scene.actors（不変）と scene.world（可変）は別フィールドのため同時借用できる
        let actors = &scene.actors;
        let world  = &mut scene.world;

        // world_line 0 のアクター木を DFS して (保持アクタ, スロット entity) を収集する
        let mut jobs: Vec<(&Actor, Entity)> = Vec::new();
        for root in actors.iter().filter(|a| a.world_line == 0) {
            collect_animator_jobs(root, &mut jobs);
        }

        for (owner, slot_entity) in jobs {
            // ── アニメーターの現在状態と再生対象クリップ（Arc 複製）を読み出す ──
            let (mut time, playing, speed, clip): (f32, bool, f32, Arc<AnimationClip>) =
                match world.get::<AnimatorComponent>(slot_entity) {
                    Some(a) => {
                        // 現在クリップがキャッシュに無ければスキップ
                        let clip = match a.current_clip.as_ref().and_then(|n| a.cache.get(n).cloned()) {
                            Some(c) => c,
                            None => continue,
                        };
                        (a.time, a.playing, a.speed, clip)
                    }
                    None => continue,
                };

            // ── 時刻を進めて loop_mode で正規化する ──
            if playing {
                time += dt * speed;
            }
            let (sample_time, still_playing) =
                animation::normalize_time(clip.loop_mode, time, clip.duration);

            // ── 各トラックを評価して対象アクターへ書き込む ──
            for track in &clip.tracks {
                // (component, property) をレジストリで束縛に解決する
                let Some(binding) =
                    animation::resolve_binding(&track.target.component, &track.target.property)
                else { continue }; // 未知プロパティは無視（ロード時想定・ここでは静かにスキップ）
                // 束縛が期待する値型とトラックの値型が一致しなければスキップ
                if binding.expected_value_type() != track.value_type { continue; }
                // actor_path を保持アクタ基準で解決する
                let Some(target_actor) =
                    animation::resolve_actor_path(owner, &track.target.actor_path)
                else { continue };
                // サンプルして書き込む
                if let Some(value) = animation::sample_track(track, sample_time) {
                    animation::apply_write(world, target_actor, binding, &value);
                }
            }

            // ── 再生状態を書き戻す ──
            if let Some(a) = world.get_mut::<AnimatorComponent>(slot_entity) {
                a.time = time;
                // Once の末尾到達で playing=false になる。すでに停止中なら停止のまま。
                a.playing = playing && still_playing;
            }
        }
    }

    /// 未初期化の AnimatorComponent についてクリップをロードし、
    /// play_on_start なら default_clip を再生開始状態にする。
    ///
    /// ロード失敗は LOAD_ERROR 通知を送り、該当クリップのみ無効化する（他は継続）。
    fn init_animators(&mut self) {
        // 対象スロット entity を先に集める（借用競合回避）
        let entities: Vec<Entity> = {
            let Some(scene) = self.scene.as_ref() else { return };
            let mut out = Vec::new();
            for root in scene.actors.iter().filter(|a| a.world_line == 0) {
                collect_animator_entities(root, &mut out);
            }
            out
        };
        if entities.is_empty() { return; }

        let mut load_errors: Vec<String> = Vec::new();

        for slot_entity in entities {
            // 未初期化かどうかとクリップ参照一覧を取得する
            let clips: Vec<AnimClipRef> = {
                let Some(scene) = self.scene.as_ref() else { return };
                match scene.world.get::<AnimatorComponent>(slot_entity) {
                    Some(a) if !a.initialized => a.clips.clone(),
                    _ => continue, // 既に初期化済み or 実体なし
                }
            };

            // 各クリップをロードする（ディスク I/O。ここでは World を触らない）
            let mut loaded: Vec<(String, Arc<AnimationClip>)> = Vec::new();
            for cref in &clips {
                if cref.path.is_empty() { continue; }
                match AnimationClip::load(&cref.path) {
                    Ok(clip) => {
                        // ロード時にトラックの (component, property) をレジストリで検証し、
                        // 未知プロパティや値型不一致を警告する（該当トラックは評価時に無視される）。
                        for (ti, track) in clip.tracks.iter().enumerate() {
                            match animation::resolve_binding(&track.target.component, &track.target.property) {
                                Some(binding) if binding.expected_value_type() != track.value_type => {
                                    eprintln!("[SEED anim] {}: track {ti} ({}/{}) の値型が不一致のため無視します",
                                              cref.path, track.target.component, track.target.property);
                                }
                                None => {
                                    eprintln!("[SEED anim] {}: track {ti} の未対応プロパティ {}/{} を無視します",
                                              cref.path, track.target.component, track.target.property);
                                }
                                _ => {}
                            }
                        }
                        // キャッシュキー: AnimClipRef.name（空なら .anim のクリップ名を採用）
                        let key = if cref.name.is_empty() { clip.name.clone() } else { cref.name.clone() };
                        loaded.push((key, Arc::new(clip)));
                    }
                    Err(err) => load_errors.push(format!("LOAD_ERROR:{err}")),
                }
            }

            // キャッシュ登録・初期化フラグ・play_on_start 発火を反映する
            let Some(scene) = self.scene.as_mut() else { return };
            if let Some(a) = scene.world.get_mut::<AnimatorComponent>(slot_entity) {
                for (k, v) in loaded { a.cache.insert(k, v); }
                a.initialized = true;
                if a.play_on_start && !a.default_clip.is_empty() && a.cache.contains_key(&a.default_clip) {
                    a.current_clip = Some(a.default_clip.clone());
                    a.time = 0.0;
                    a.playing = true;
                }
            }
        }

        // ロードエラーをエディタへ通知する
        if let Some(ipc) = &self.ipc {
            for e in load_errors { ipc.send(&e); }
        }
    }
}

// ─── 収集ヘルパー ────────────────────────────────────────────

/// アクター木を DFS し、有効な Animator スロットについて
/// (保持アクタ参照, スロット entity) を収集する。
fn collect_animator_jobs<'a>(actor: &'a Actor, out: &mut Vec<(&'a Actor, Entity)>) {
    // 非アクティブなアクター配下はアニメーションさせない
    if !actor.active { return; }
    for slot in actor.slots() {
        if slot.kind == ComponentKind::Animator && slot.enabled {
            out.push((actor, slot.entity));
        }
    }
    for child in actor.children() {
        collect_animator_jobs(child, out);
    }
}

/// アクター木を DFS し、Animator スロットの entity のみを収集する
/// （初期化パス用。保持アクタ参照は不要）。
fn collect_animator_entities(actor: &Actor, out: &mut Vec<Entity>) {
    for slot in actor.slots() {
        if slot.kind == ComponentKind::Animator {
            out.push(slot.entity);
        }
    }
    for child in actor.children() {
        collect_animator_entities(child, out);
    }
}
