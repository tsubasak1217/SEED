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
use crate::engine::components::{
    AnimatorComponent, AnimatorComponentData, AnimClipRef, AnimClipKind, AnimClipLoop,
    ComponentKind, ModelComponent, ModelAnimDrive,
};
use crate::engine::ecs::Entity;
use crate::engine::structs::objects::Actor;

use super::{App, RuntimeMode, find_actor_by_dfs};

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

        // ── ① 全 Model スロットの anim_drive をクリア（デモ再生へ一旦戻す）──
        // この後のジョブループで「現在 Model クリップを再生中」の MC だけ再設定する。
        // これにより停止・クリップ切替・キーフレームへの切替時に anim_drive が残らない。
        let mut model_entities: Vec<Entity> = Vec::new();
        for root in actors.iter().filter(|a| a.world_line == 0) {
            collect_model_slot_entities(root, &mut model_entities);
        }
        for me in &model_entities {
            if let Some(mc) = world.get_mut::<ModelComponent>(*me) {
                mc.anim_drive = None;
            }
        }

        // world_line 0 のアクター木を DFS して (保持アクタ, スロット entity) を収集する
        let mut jobs: Vec<(&Actor, Entity)> = Vec::new();
        for root in actors.iter().filter(|a| a.world_line == 0) {
            collect_animator_jobs(root, &mut jobs);
        }

        for (owner, slot_entity) in jobs {
            // ── 現在クリップの参照情報（名前・状態）を読み出す ──
            let (current_name, mut time, playing, speed) =
                match world.get::<AnimatorComponent>(slot_entity) {
                    Some(a) => match a.current_clip.as_ref() {
                        Some(n) => (n.clone(), a.time, a.playing, a.speed),
                        None    => continue, // 未再生
                    },
                    None => continue,
                };

            // clips から現在クリップの定義を探して種別を判定する
            let cref: AnimClipRef = match world.get::<AnimatorComponent>(slot_entity)
                .and_then(|a| a.clips.iter().find(|c| c.name == current_name).cloned())
            {
                Some(c) => c,
                None    => continue, // クリップ定義なし（不整合）: スキップ
            };

            match cref.kind {
                // ── (a) Keyframe クリップ: 従来どおりトラックを評価してレジストリ書込 ──
                AnimClipKind::Keyframe => {
                    // 現在クリップがキャッシュに無ければスキップ
                    let clip: Arc<AnimationClip> = match world.get::<AnimatorComponent>(slot_entity)
                        .and_then(|a| a.cache.get(&current_name).cloned())
                    {
                        Some(c) => c,
                        None    => continue,
                    };

                    // 時刻を進めて loop_mode で正規化する
                    if playing { time += dt * speed; }
                    let (sample_time, still_playing) =
                        animation::normalize_time(clip.loop_mode, time, clip.duration);

                    // 各トラックを評価して対象アクターへ書き込む
                    for track in &clip.tracks {
                        let Some(binding) =
                            animation::resolve_binding(&track.target.component, &track.target.property)
                        else { continue };
                        if binding.expected_value_type() != track.value_type { continue; }
                        let Some(target_actor) =
                            animation::resolve_actor_path(owner, &track.target.actor_path)
                        else { continue };
                        if let Some(value) = animation::sample_track(track, sample_time) {
                            animation::apply_write(world, target_actor, binding, &value);
                        }
                    }

                    // 再生状態を書き戻す
                    if let Some(a) = world.get_mut::<AnimatorComponent>(slot_entity) {
                        a.time = time;
                        a.playing = playing && still_playing;
                    }
                }

                // ── (b) Model クリップ: glTF 内蔵アニメを anim_drive 経由で駆動 ──
                AnimClipKind::Model => {
                    // 同アクターの最初の有効 Model スロットを探す
                    let model_entity = owner.slots().iter()
                        .find(|s| s.kind == ComponentKind::Model && s.enabled)
                        .map(|s| s.entity);

                    // glTF 内蔵アニメ名 → インデックス + duration を解決する
                    let resolved: Option<(Entity, usize, f32)> = model_entity.and_then(|me| {
                        world.get::<ModelComponent>(me).and_then(|mc| {
                            mc.model.as_ref().and_then(|model| {
                                model.animations.iter()
                                    .position(|a| a.name == cref.anim)
                                    .map(|idx| (me, idx, model.animations[idx].duration))
                            })
                        })
                    });

                    let Some((me, anim_idx, duration)) = resolved else {
                        // モデル未ロード or アニメ名が見つからない: 警告してデモ再生に委ねる
                        eprintln!(
                            "[SEED anim] Model クリップ '{}' のアニメ '{}' を解決できません（Model スロット/内蔵アニメ名を確認）。デモ再生にフォールバックします。",
                            current_name, cref.anim
                        );
                        continue;
                    };

                    // 時刻を進めてループ/ワンショットで正規化する
                    if playing { time += dt * speed; }
                    let (sample_time, still_playing) = match cref.loop_mode {
                        // ループ: rem_euclid で負時刻も巻き戻す（常に再生継続）
                        AnimClipLoop::Loop => {
                            let d = duration.max(1e-4);
                            (time.rem_euclid(d), true)
                        }
                        // ワンショット: 末尾で停止して最終ポーズを保持する
                        AnimClipLoop::Once => {
                            if time >= duration { (duration, false) } else { (time.max(0.0), true) }
                        }
                    };

                    // GPU スキニングは Model::animations[0] のみ再生可能なため、
                    // anim_idx != 0 は権威駆動できない（警告してデモ再生にフォールバック）。
                    if anim_idx == 0 {
                        if let Some(mc) = world.get_mut::<ModelComponent>(me) {
                            mc.anim_drive = Some(ModelAnimDrive {
                                anim_idx,
                                time: sample_time,
                                playing,
                            });
                        }
                    } else {
                        eprintln!(
                            "[SEED anim] Model クリップ '{}' は内蔵アニメ index {}（'{}'）を指すが、現状の GPU スキニングは index 0 のみ再生可能。デモ再生にフォールバックします（複数アニメ選択は今後の対応）。",
                            current_name, anim_idx, cref.anim
                        );
                    }

                    // 再生状態を書き戻す
                    if let Some(a) = world.get_mut::<AnimatorComponent>(slot_entity) {
                        a.time = time;
                        a.playing = playing && still_playing;
                    }
                }
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
                // play_on_start 発火判定:
                //   - Keyframe クリップ: ロード済み（cache に存在）が条件。
                //   - Model クリップ    : cache は持たないため、clips に kind=Model の
                //     同名クリップが存在すれば再生可能とみなす。
                let default_playable = !a.default_clip.is_empty() && a.clips.iter().any(|c| {
                    c.name == a.default_clip
                        && (matches!(c.kind, AnimClipKind::Model) || a.cache.contains_key(&a.default_clip))
                });
                if a.play_on_start && default_playable {
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

    // ── インスペクターからの AnimatorComponent 編集（SET_ANIMATOR_CLIPS IPC）──

    /// インスペクターの Animator 編集 UI からのクリップ一覧一括更新（SET_ANIMATOR_CLIPS IPC）。
    ///
    /// json は AnimatorComponentData と serde 互換（clips / default_clip / play_on_start / speed）。
    /// 揮発状態（current_clip / time / playing / initialized / cache）は AnimatorComponent::from_data
    /// で作り直すことでリセットする（クリップ差し替え後の状態不整合を避けるため、
    /// 安全側で常に再初期化する）。
    pub(super) fn handle_set_animator_clips(&mut self, actor_dfs_id: u32, slot_idx: u32, json: &str) {
        // JSON を AnimatorComponentData にデコードする（失敗時は無視）
        let data: AnimatorComponentData = match serde_json::from_str(json) {
            Ok(d) => d,
            Err(err) => {
                eprintln!("[SEED anim] SET_ANIMATOR_CLIPS: JSON パース失敗: {err}");
                return;
            }
        };

        let wl = self.active_world_line;
        // 対象スロットのエンティティを解決する（handle_set_audio_field と同じ流儀）
        let slot_entity = {
            let Some(scene) = &self.scene else { return };
            let mut c = 0u32;
            find_actor_by_dfs(&scene.actors, wl, actor_dfs_id, &mut c)
                .and_then(|a| a.slots().get(slot_idx as usize))
                .filter(|s| s.kind == ComponentKind::Animator)
                .map(|s| s.entity)
        };
        let Some(entity) = slot_entity else { return };
        let Some(scene) = &mut self.scene else { return };
        if let Some(a) = scene.world.get_mut::<AnimatorComponent>(entity) {
            // 保存対象データを置換し、揮発状態は from_data 相当で作り直す
            *a = AnimatorComponent::from_data(data);
        }

        self.send_actor_components(actor_dfs_id, self.actor_virtual_selected_slot_idx);
        if let Some(ipc) = &self.ipc { ipc.send("SCENE_MODIFIED"); }
    }

    // ── Edit モードのアニメーションプレビュー（ANIM_PREVIEW / ANIM_PREVIEW_STOP / ANIM_RELOAD）──

    /// Edit モード限定：指定クリップの指定時刻を対象アクターへプレビュー適用する（ANIM_PREVIEW IPC）。
    ///
    /// 初回プレビュー時（そのアクターの元値未退避時）は、このクリップの全トラックが
    /// 触れるプロパティの現在値を anim_preview_saved へ退避する。
    /// 以降の呼び出し（タイムラインのスクラブ等）では退避を行わず、単に評価値を書き込む。
    pub(super) fn handle_anim_preview(&mut self, actor_dfs_id: u32, clip_path: &str, time: f32) {
        // Edit モード専用（Play モード中は AnimationSystem が別途評価するため無視する）
        if self.mode != RuntimeMode::Edit {
            eprintln!("[SEED anim] ANIM_PREVIEW は Edit モード専用（無視）");
            return;
        }

        // クリップをキャッシュから取得、無ければロードしてキャッシュする
        let clip: Arc<AnimationClip> = match self.anim_preview_cache.get(clip_path) {
            Some(c) => c.clone(),
            None => match AnimationClip::load(clip_path) {
                Ok(c) => {
                    let arc = Arc::new(c);
                    self.anim_preview_cache.insert(clip_path.to_string(), arc.clone());
                    arc
                }
                Err(err) => {
                    eprintln!("[SEED anim] ANIM_PREVIEW: クリップロード失敗 {clip_path}: {err}");
                    return;
                }
            },
        };

        let wl = self.active_world_line;
        let Some(scene) = self.scene.as_mut() else { return };
        // scene.actors（不変）と scene.world（可変）は別フィールドのため同時借用できる
        let actors = &scene.actors;
        let world  = &mut scene.world;

        let mut counter = 0u32;
        let Some(owner) = find_actor_by_dfs(actors, wl, actor_dfs_id, &mut counter) else { return };

        // 初回プレビューなら、このクリップの全トラックが触れるプロパティの現在値を退避する
        if !self.anim_preview_saved.contains_key(&actor_dfs_id) {
            let mut saved = Vec::new();
            for track in &clip.tracks {
                let Some(binding) =
                    animation::resolve_binding(&track.target.component, &track.target.property)
                else { continue };
                if binding.expected_value_type() != track.value_type { continue; }
                let Some(target_actor) =
                    animation::resolve_actor_path(owner, &track.target.actor_path)
                else { continue };
                if let Some(value) = animation::read_binding(world, target_actor, binding) {
                    saved.push((track.target.actor_path.clone(), binding, value));
                }
            }
            self.anim_preview_saved.insert(actor_dfs_id, saved);
        }

        // サンプル時刻を求め、各トラックを評価して書き込む
        let (sample_time, _still_playing) =
            animation::normalize_time(clip.loop_mode, time, clip.duration);
        for track in &clip.tracks {
            let Some(binding) =
                animation::resolve_binding(&track.target.component, &track.target.property)
            else { continue };
            if binding.expected_value_type() != track.value_type { continue; }
            let Some(target_actor) =
                animation::resolve_actor_path(owner, &track.target.actor_path)
            else { continue };
            if let Some(value) = animation::sample_track(track, sample_time) {
                animation::apply_write(world, target_actor, binding, &value);
            }
        }
    }

    /// アニメーションプレビューを終了し、退避しておいた元値へ復元する（ANIM_PREVIEW_STOP IPC）。
    /// 退避エントリが無ければ何もしない（プレビュー未実行 or 既に停止済み）。
    pub(super) fn handle_anim_preview_stop(&mut self, actor_dfs_id: u32) {
        let Some(saved) = self.anim_preview_saved.remove(&actor_dfs_id) else { return };
        if saved.is_empty() { return; }

        let wl = self.active_world_line;
        let Some(scene) = self.scene.as_mut() else { return };
        let actors = &scene.actors;
        let world  = &mut scene.world;

        let mut counter = 0u32;
        let Some(owner) = find_actor_by_dfs(actors, wl, actor_dfs_id, &mut counter) else { return };

        for (actor_path, binding, value) in saved {
            if let Some(target_actor) = animation::resolve_actor_path(owner, &actor_path) {
                animation::apply_write(world, target_actor, binding, &value);
            }
        }
    }

    /// 指定クリップのロード済みキャッシュを破棄する（ANIM_RELOAD IPC）。
    /// エディタが .anim 保存後に送る。次回の ANIM_PREVIEW で再ロードされる。
    pub(super) fn handle_anim_reload(&mut self, clip_path: &str) {
        self.anim_preview_cache.remove(clip_path);
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

/// アクター木を DFS し、Model スロットの entity を収集する
/// （毎フレームの anim_drive クリア用。非アクティブ配下も含めてクリアしたいので
/// active 判定は行わない）。
fn collect_model_slot_entities(actor: &Actor, out: &mut Vec<Entity>) {
    for slot in actor.slots() {
        if slot.kind == ComponentKind::Model {
            out.push(slot.entity);
        }
    }
    for child in actor.children() {
        collect_model_slot_entities(child, out);
    }
}
