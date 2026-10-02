// ============================================================
//  prefab_live_patch/ops.rs — Play 中のプレハブの当て直し（impl App）
//
//  ・handle_prefab_live_patch_path … IPC PREFAB_LIVE_PATCH_PATH:{path} の入口（応答を返す）
//  ・live_patch_prefab_path        … 本体（書き戻し write_back.rs からも呼ぶ）
//  ・capture_prefab_live_bases     … Play 開始で、シーンのインスタンスの元の版を控える（base_cache.rs）
//  ・remember_instantiated_prefab  … スクリプトの Instantiate が組み立てた版を控える
//
//  Edit では使わない（Edit は丸ごとの再展開 PREFAB_REAPPLY_PATH。Undo 1 操作・SCENE_MODIFIED あり）。
//  Play のワールドは停止で Play 前の写しへ戻るので、ここでは Undo を積まず SCENE_MODIFIED も送らない。
// ============================================================

use std::collections::{BTreeMap, BTreeSet};
use std::time::Instant;

use crate::engine::components::Transform;
use crate::engine::core::app_base::prefab_hash::content_hash;
use crate::engine::core::redraw::RedrawReason;
use crate::engine::ecs::World;
use crate::engine::methods::gizmo_interact::{mat4x4_inv, mat4x4_mul};
use crate::engine::structs::objects::Actor;
use crate::engine::structs::objects::actor::ActorData;

use super::super::prefab_ops::{load_actor_data_with_hash, SINGULAR_SCALE_EPS};
use super::super::text_expand::invalidate_text_expand_cache;
use super::super::{App, RuntimeMode};
use super::apply::{apply_node, PatchContext, PatchStats};
use super::plan::plan_instance;
use super::summary::summarize_instance;
use super::wire;

/// 当て直しの対象の世界線（Play のシーン）。アクタ編集タブ（1 以上）はファイルそのものなので触らない。
const PLAY_WORLD_LINE: u32 = 0;

/// ミリ秒への換算（ログ用）。
const MILLIS_PER_SECOND: f64 = 1000.0;

impl App {
    /// IPC `PREFAB_LIVE_PATCH_PATH:{path}` を処理して応答を返す。
    ///
    /// 応答: `PREFAB_LIVE_PATCH_DONE:{件数},{仮想パス}`（0 件でも返す）／`PREFAB_LIVE_PATCH_ERROR:{理由}`。
    pub(crate) fn handle_prefab_live_patch_path(&mut self, path: &str) {
        let vpath = crate::engine::asset_fs::to_virtual(path);
        let reply = match self.live_patch_prefab_path(&vpath) {
            Ok(count) => wire::live_patch_done(count, &vpath),
            Err(reason) => wire::live_patch_error(&reason),
        };
        if let Some(ipc) = &self.ipc {
            ipc.send(&reply);
        }
    }

    /// Play 中のシーン（世界線 0）で `prefab_source == vpath` のインスタンス全部へ、ファイルの今の中身を
    /// 「状態を保ったまま」当て直す。戻り値は当て直したインスタンス数。
    ///
    /// スクリプトの Instantiate で Play 中に作られたインスタンス（ScreenStack が積んだ画面など）も含む。
    /// インスタンスの中に入れ子になった同じパスのインスタンスへは降りない（自分の中の自分は外側だけ）。
    pub(crate) fn live_patch_prefab_path(&mut self, vpath: &str) -> Result<usize, String> {
        if self.mode != RuntimeMode::Play {
            return Err(wire::REASON_NOT_PLAYING.to_string());
        }
        if self.scene.is_none() || self.draw_ctx.is_none() {
            return Err(wire::REASON_NO_SCENE.to_string());
        }
        let started = Instant::now();

        // ── 新しい版を読む（ハッシュは生テキストから。読み込みの入口は actor_file）──
        let (new_data, new_hash) = load_actor_data_with_hash(vpath).map_err(|e| format!("{vpath} を読めません: {e}"))?;
        // 自己参照（ファイルの根が自分を指す）は展開すると自分を含み得るので当てない（再展開と同じガード）
        if new_data.prefab_source.as_deref() == Some(vpath) {
            return Err(format!("{vpath} は自分自身を参照しています"));
        }

        // ── 対象のインスタンスを集める（子の添字の列で持つ。対象どうしは入れ子にならない）──
        let targets = {
            let scene = self.scene.as_ref().ok_or(wire::REASON_NO_SCENE)?;
            collect_instance_paths(&scene.actors, vpath)
        };

        // ── 元の版を使える状態にする（解析は可変参照が要るので当て直しの前に済ませる）──
        let hashes: BTreeSet<String> = {
            let scene = self.scene.as_ref().ok_or(wire::REASON_NO_SCENE)?;
            targets
                .iter()
                .filter_map(|path| actor_at_path(&scene.actors, path))
                .filter_map(|actor| actor.prefab_hash.clone())
                .collect()
        };
        for hash in &hashes {
            self.prefab_live_bases.resolve(vpath, hash);
        }

        // ── 当てる（scene を取り出して draw_ctx との同時借用を避ける。再展開と同じ作法）──
        // 冒頭で scene / draw_ctx の存在は確かめてあるので、取り出した scene は必ず下で戻る。
        let host = self.scripting_host.clone();
        let mut stats = PatchStats::default();
        let mut count = 0usize;
        let mut three_way = 0usize;
        let (Some(ctx), Some(mut scene)) = (self.draw_ctx.as_ref(), self.scene.take()) else {
            return Err(wire::REASON_NO_SCENE.to_string());
        };
        {
            for path in &targets {
                let Some(actor) = actor_at_path_mut(&mut scene.actors, path) else { continue };
                let summary = summarize_instance(actor, &scene.world);
                let base = actor.prefab_hash.as_deref().and_then(|h| self.prefab_live_bases.peek(vpath, h));
                if base.is_some() {
                    three_way += 1;
                }
                let plan = plan_instance(&summary, base, &new_data);
                let delta_3d = delta_for_new_3d_nodes(actor, &scene.world, &new_data);
                let mut pc = PatchContext {
                    world: &mut scene.world,
                    ctx,
                    host: host.as_ref(),
                    world_line: PLAY_WORLD_LINE,
                    delta_3d,
                };
                apply_node(actor, plan, base, &new_data, true, &mut pc, &mut stats);
                // 当て直した版を記録する（次の当て直しはこの版を元の版として 3 方向で行う）
                actor.prefab_hash = Some(new_hash.clone());
                count += 1;
            }
        }
        self.scene = Some(scene);

        // 新しい版を控える（このあと積まれるインスタンス・次の当て直しの元の版になる）
        self.prefab_live_bases.remember_data(vpath, &new_hash, &new_data);

        eprintln!(
            "[PrefabLivePatch] {vpath}: インスタンス {count} 個（3 方向 {three_way} / 2 方向 {}）に当て直しました（{:.1} ms）{stats:?}",
            count - three_way,
            started.elapsed().as_secs_f64() * MILLIS_PER_SECOND,
        );
        if count == 0 {
            return Ok(0);
        }

        // ── 当て直しの後始末（レイアウト・テキストの測り直し・描画・エディタの表示）──
        // レイアウト（Stack / Wrap / Grid 等）は毎フレームの木から組み直されるので、描く理由を立てれば次のフレームで測り直す。
        // テキストの展開は内容のハッシュで引き直されるが、スロットを作り直した分の取り残しを捨てておく。
        invalidate_text_expand_cache();
        self.mark_audio_dictionary_dirty();
        self.update_canvas_wl_state_for(PLAY_WORLD_LINE);
        self.declare_redraw_reason(RedrawReason::HotReload);
        self.send_hierarchy();
        if let Some(dfs) = self.actor_virtual_selected_idx {
            self.send_actor_components(dfs as u32, self.actor_virtual_selected_slot_idx);
        }
        Ok(count)
    }

    /// Play の開始で、シーンのインスタンスが作られた元の版をファイルから控える（3 方向の当て直しの材料）。
    ///
    /// 参照パス 1 本につき 1 回だけ生テキストを読み（解析は使うときまで遅らせる）、インスタンスの
    /// `prefab_hash` と一致したものだけ控える（一致しない＝シーンのインスタンスが古い版。元の版は分からない）。
    /// エディタとつながっていない（IPC が無い）ときは当て直しが来ないので読まない。
    pub(crate) fn capture_prefab_live_bases(&mut self) {
        self.prefab_live_bases.clear();
        if self.ipc.is_none() {
            return;
        }
        let Some(scene) = &self.scene else { return };
        let started = Instant::now();
        let mut wanted: BTreeMap<String, BTreeSet<String>> = BTreeMap::new();
        for actor in scene.actors.iter().filter(|a| a.world_line == PLAY_WORLD_LINE) {
            collect_instance_versions(actor, &mut wanted);
        }
        let mut remembered = 0usize;
        for (source, hashes) in wanted {
            let Ok(raw) = crate::engine::asset_fs::read_string(&source) else { continue };
            let hash = content_hash(&raw);
            if hashes.contains(&hash) {
                self.prefab_live_bases.remember_raw(&source, &hash, raw);
                remembered += 1;
            }
        }
        if remembered > 0 {
            eprintln!(
                "[PrefabLivePatch] Play 開始: 元の版を {remembered} 本控えました（{:.1} ms）",
                started.elapsed().as_secs_f64() * MILLIS_PER_SECOND,
            );
        }
    }

    /// スクリプトの Instantiate が組み立てた版を控える（版ごとに最初の 1 回だけ複製する）。
    ///
    /// エディタとつながっていない（IPC が無い）ときは当て直しが来ないので控えない（製品のメモリを使わない）。
    pub(crate) fn remember_instantiated_prefab(&mut self, vpath: &str, hash: &str, data: &ActorData) {
        if self.ipc.is_none() || self.prefab_live_bases.contains(vpath, hash) {
            return;
        }
        self.prefab_live_bases.remember_data(vpath, hash, data);
    }

    /// 書き戻しで上書きする前のファイルを控える（Play 中のほかのインスタンスを 3 方向で当て直せるように）。
    pub(crate) fn remember_prefab_before_overwrite(&mut self, vpath: &str) {
        if let Ok(raw) = crate::engine::asset_fs::read_string(vpath) {
            let hash = content_hash(&raw);
            self.prefab_live_bases.remember_raw(vpath, &hash, raw);
        }
    }
}

// ── 木の走査（App に依存しない）─────────────────────────────────

/// `prefab_source == vpath` のインスタンスの根を、世界線 0 のトップレベルからの子の添字の列で集める。
///
/// 対象を見つけたらその中へは降りない（入れ子の同じパスは外側だけ）。エディタのプレビューの中は見ない。
fn collect_instance_paths(actors: &[Actor], vpath: &str) -> Vec<Vec<usize>> {
    /// 1 ノードを見て、対象なら記録し、そうでなければ子へ降りる。
    fn walk(actor: &Actor, vpath: &str, path: &mut Vec<usize>, out: &mut Vec<Vec<usize>>) {
        if actor.editor_preview.is_some() {
            return;
        }
        if actor.prefab_source.as_deref() == Some(vpath) {
            out.push(path.clone());
            return;
        }
        for (i, child) in actor.children().iter().enumerate() {
            path.push(i);
            walk(child, vpath, path, out);
            path.pop();
        }
    }
    let mut out = Vec::new();
    for (i, actor) in actors.iter().enumerate() {
        if actor.world_line != PLAY_WORLD_LINE {
            continue;
        }
        let mut path = vec![i];
        walk(actor, vpath, &mut path, &mut out);
    }
    out
}

/// 子の添字の列でアクタを引く（共有参照）。
fn actor_at_path<'a>(actors: &'a [Actor], path: &[usize]) -> Option<&'a Actor> {
    let (first, rest) = path.split_first()?;
    let mut node = actors.get(*first)?;
    for &i in rest {
        node = node.children().get(i)?;
    }
    Some(node)
}

/// 子の添字の列でアクタを引く（可変参照）。
fn actor_at_path_mut<'a>(actors: &'a mut [Actor], path: &[usize]) -> Option<&'a mut Actor> {
    let (first, rest) = path.split_first()?;
    let mut node = actors.get_mut(*first)?;
    for &i in rest {
        node = node.children_mut().get_mut(i)?;
    }
    Some(node)
}

/// インスタンスの根の（参照パス → 版）を集める（入れ子のインスタンスも拾う。プレビューの中は見ない）。
fn collect_instance_versions(actor: &Actor, out: &mut BTreeMap<String, BTreeSet<String>>) {
    if actor.editor_preview.is_some() {
        return;
    }
    if let (Some(source), Some(hash)) = (&actor.prefab_source, &actor.prefab_hash) {
        out.entry(source.clone()).or_default().insert(hash.clone());
    }
    for child in actor.children() {
        collect_instance_versions(child, out);
    }
}

/// 新しく作る 3D ノードを「ファイルの根＝原点」基準から根の配置へ移す行列（2D・特異なら None）。
///
/// 再展開（prefab_ops.rs の reinstantiate_single）と同じ delta = M_live_root × M_file_root⁻¹。
fn delta_for_new_3d_nodes(root: &Actor, world: &World, new: &ActorData) -> Option<[[f32; 4]; 4]> {
    if root.is_2d() {
        return None;
    }
    let live = world.get::<Transform>(root.entity)?;
    let file = new.transform.clone().unwrap_or_default();
    if file.scale.iter().any(|s| s.abs() < SINGULAR_SCALE_EPS) {
        return None;
    }
    Some(mat4x4_mul(live.to_mat4(), mat4x4_inv(file.to_mat4())))
}

// ============================================================
//  テスト
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;

    /// 対象の集め方: 普通のアクタの下へは降り、対象のインスタンスの中へは降りない。プレビューの中は見ない。
    #[test]
    fn collects_instances_without_descending_into_them() {
        let mut world = World::new();
        let target = "assets://ui/Row.actor";
        let mut root = Actor::new(world.spawn(), "Root");
        let mut inst = Actor::new(world.spawn(), "Row0");
        inst.prefab_source = Some(target.into());
        // インスタンスの中の同じパス（外側だけを当てる）
        let mut nested = Actor::new(world.spawn(), "Inner");
        nested.prefab_source = Some(target.into());
        inst.children.push(nested);
        root.children.push(inst);
        // 別のプレハブのインスタンスの中にある対象（拾う）
        let mut other = Actor::new(world.spawn(), "Screen");
        other.prefab_source = Some("assets://ui/Screen.actor".into());
        let mut spawned = Actor::new(world.spawn(), "Row1");
        spawned.prefab_source = Some(target.into());
        spawned.spawned_by_script = true;
        other.children.push(spawned);
        root.children.push(other);
        // プレビューの中（拾わない）
        let mut preview = Actor::new(world.spawn(), "Preview");
        preview.editor_preview = Some(serde_json::from_str(r#"{"prefab":"assets://ui/Row.actor"}"#).unwrap());
        let mut in_preview = Actor::new(world.spawn(), "Row2");
        in_preview.prefab_source = Some(target.into());
        preview.children.push(in_preview);
        root.children.push(preview);
        // 世界線 1（アクタ編集タブ）は見ない
        let mut tab = Actor::new(world.spawn(), "Tab");
        tab.world_line = 1;
        tab.prefab_source = Some(target.into());

        let actors = vec![root, tab];
        let paths = collect_instance_paths(&actors, target);
        assert_eq!(paths, vec![vec![0, 0], vec![0, 1, 0]]);
        assert_eq!(actor_at_path(&actors, &paths[1]).map(|a| a.name.as_str()), Some("Row1"));
    }
}
