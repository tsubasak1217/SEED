// ============================================================
//  app/hot_reload_ops.rs — 実行中の差し替え（RELOAD_SCENE / RELOAD_ASSET）の適用（docs/android.md §23）
//
//  【流れ】（1 フレームに 1 回。ipc_handler.rs の process_ipc の最後＝フレームの境界・ゲームロジックの前）
//    1. このフレームに届いた要求を計画にする（hot_reload/batch.rs。純粋な処理）
//    2. 差し替えたアセットのキャッシュを捨てる（forget_asset_caches）
//         描画・再生のたびに引くキャッシュ（スプライトの画像・シェーダ・音声 等）は、これだけで次に使うときに新しい中身になる
//    3. 要ればシーンを 1 回だけ読み直す（rebuild_current_scene。Play 中のシーンの入れ替えはスクリプトの遷移と同じ経路）
//         読み直しはディスク（Android は上書き層 files/assets → APK の pak の順。asset_fs）から行うので、送ったものが入る
//    4. 要求ごとに応答を返す（RELOAD_DONE / RELOAD_SKIPPED / RELOAD_FAILED。書式は hot_reload/wire.rs）
//  ゲームの一時停止（PAUSE）は変えない（一時停止中に差し替えれば、一時停止のまま新しいシーンになる）。
//  スクリプトの差し替え（RELOAD_SCRIPTS）は従来どおり script_ops.rs（すぐ適用し、SCRIPTS_RELOADED で応答）。
//
//  【ログ】（標準エラー。Android は logcat のタグ SEED）
//    [SEED RELOAD] asset models/Fish.glb: キャッシュ 3 件を捨てました（scene）
//    [SEED RELOAD] シーンを読み直しました: assets://scenes/Main.scene（123.4 ms）
//    [SEED RELOAD] RELOAD_DONE:asset:models/Fish.glb|123.4|scene:assets://scenes/Main.scene
// ============================================================

use std::path::{Path, PathBuf};
use std::time::Instant;

use crate::engine::core::app_base::app::RuntimeMode;
use crate::engine::core::app_base::hot_reload::asset_key::key_refers_to;
use crate::engine::core::app_base::hot_reload::{PlanContext, PlannedAsset};

use super::App;

/// ログの印（logcat の SEED タグで探しやすくする）。
const LOG_PREFIX: &str = "[SEED RELOAD]";

/// 秒 → ミリ秒（所要時間の応答・ログ用）。
const MILLIS_PER_SECOND: f64 = 1000.0;

impl App {
    /// このフレームに届いた差し替えの要求をまとめて適用し、応答を返す（process_ipc の最後から呼ぶ）。
    ///
    /// 要求が無ければ何もしない（毎フレーム呼ばれるので、空のときは計画も作らない）。
    pub(super) fn apply_pending_hot_reload(&mut self) {
        if self.hot_reload_batch.is_empty() {
            return;
        }
        let started = Instant::now();
        let assets_root = self.assets_root.as_deref().map(PathBuf::from);
        let current_scene = self.loaded_scene_path.clone();
        let plan = self.hot_reload_batch.take_plan(PlanContext {
            current_scene: current_scene.as_deref(),
            assets_root: assets_root.as_deref(),
            // Edit（PC のエディタの埋め込みランタイム）はエディタが自分の LOAD_SCENE で読み直すので、ここでは読み直さない
            scene_rebuild_allowed: self.mode == RuntimeMode::Play,
        });

        // ── 2. キャッシュを捨てる ──
        for asset in &plan.assets {
            let removed = self.forget_asset_caches(asset, assets_root.as_deref());
            eprintln!(
                "{LOG_PREFIX} asset {}: キャッシュ {removed} 件を捨てました（{}）",
                asset.relative,
                asset.refresh.label()
            );
        }

        // ── 3. シーンを 1 回だけ読み直す ──
        let rebuild = plan.rebuild_scene.then(|| self.rebuild_current_scene());
        let elapsed_ms = started.elapsed().as_secs_f64() * MILLIS_PER_SECOND;
        match &rebuild {
            Some(Ok(scene)) => eprintln!("{LOG_PREFIX} シーンを読み直しました: {scene}（{elapsed_ms:.1} ms）"),
            Some(Err(reason)) => eprintln!("{LOG_PREFIX} シーンを読み直せませんでした（今のシーンのまま続けます）: {reason}"),
            None => {}
        }

        // ── 4. 応答 ──
        for line in plan.reply_lines(elapsed_ms, rebuild.as_ref()) {
            eprintln!("{LOG_PREFIX} {line}");
            if let Some(ipc) = &self.ipc {
                ipc.send(&line);
            }
        }
    }

    /// 差し替えたアセットを指すキャッシュをすべて捨てる。
    ///
    /// キャッシュはどれもパスの文字列をキーにしているので、キーが差し替え対象を指すものだけを捨てる
    /// （照合は hot_reload/asset_key.rs。assets:// ／ 絶対パス ／ 相対パスの表記揺れを吸収する）。
    /// 小さく読み直しが安いプロセス全体のキャッシュ（マテリアル・.postfx の定義・インライン画像）は丸ごと捨てる。
    ///
    /// # 引数
    /// * `asset`       - 差し替えたアセット
    /// * `assets_root` - アセットルート（絶対パスのキーの照合に使う）
    ///
    /// # 戻り値
    /// 捨てたキャッシュの件数（ログ用。丸ごと捨てたものは数えない）。
    fn forget_asset_caches(&mut self, asset: &PlannedAsset, assets_root: Option<&Path>) -> usize {
        let relative = asset.relative.as_str();
        let refers = |key: &str| key_refers_to(key, relative, assets_root);
        let mut removed = 0usize;

        // ── 描画のキャッシュ（DrawContext）──
        if let Some(ctx) = &self.draw_ctx {
            // 解析済み CPU モデル（シーンを組むときに引く。次の読み直しでディスクから読み直させる）
            removed += retain_unmatched(&mut ctx.model_cache.borrow_mut(), &refers);
            // スプライト・UI の画像（描画のたびに引く。捨てれば次のフレームで読み直す）
            removed += retain_unmatched(&mut ctx.sprite_tex_cache.borrow_mut(), &refers);
            // スプライトのポストエフェクトの焼き込み（元の画像か .postfx が変わった）
            removed += ctx.sprite_postfx_cache.invalidate_matching(&refers);
            // スキンスプライトのメッシュと、それを使う変形資源
            removed += ctx.sprite_skin.forget_matching(&refers);
            // シェーディングアセット・水面シェーダ（次の解決で必ず読み直す）
            removed += ctx.shading_asset_cache.forget_matching(&refers);
            removed += ctx.water_shading_asset_cache.forget_matching(&refers);
        }

        // ── 非同期モデルロードの完成品・失敗の記憶 ──
        if let Some(streamer) = crate::engine::core::loader::async_loader::get() {
            removed += streamer.forget_matching(&refers);
        }

        // ── 音声（再生のたびに引く）──
        if let Some(audio) = self.audio.as_mut() {
            removed += audio.forget_matching(&refers);
        }

        // ── モデルの統合バッチと RT の BLAS（キーはモデルのパス＋署名）──
        let freed: Vec<String> =
            self.shared_model_batches.keys().filter(|key| refers(key.as_str())).cloned().collect();
        for key in &freed {
            self.shared_model_batches.remove(key);
            self.batch_absent_frames.remove(key);
        }
        if !freed.is_empty() {
            if let Some(rt_cell) = self.draw_ctx.as_ref().and_then(|ctx| ctx.rt_shadow.as_ref()) {
                rt_cell.borrow_mut().prune_source_paths(&freed);
            }
            removed += freed.len();
        }

        // ── 小さく読み直しが安いプロセス全体のキャッシュは丸ごと捨てる ──
        crate::engine::core::renderer::material_asset::clear_cache();
        crate::engine::core::renderer::postfx::asset::clear_cache();
        crate::engine::core::font::inline::invalidate_caches();

        // スクリプトの Scene.Preload で読んでおいたシーンは古い中身を持つので捨てる（遷移のときに読み直す）
        self.preloaded_scene = None;
        removed
    }

    /// 今のシーンをディスクから読み直す（Play 中。スクリプトのシーン遷移と同じ手順・同じ後始末）。
    ///
    /// # 戻り値
    /// 読み直したシーンのパス。読めなければ理由（今のシーンのまま）。
    fn rebuild_current_scene(&mut self) -> Result<String, String> {
        let Some(path) = self.loaded_scene_path.clone() else {
            return Err("読み込み中のシーンがありません".to_string());
        };
        // スクリプトの遷移（script_scene_ops.rs の apply_script_scene_commands）が遷移の前に行うのと同じ後始末:
        // アクタ構成が変わるので音声辞書の索引を作り直す・カーソルロックを解く・JointAttach のキャッシュを捨てる・時間スケールを戻す
        self.mark_audio_dictionary_dirty();
        self.release_script_cursor_lock();
        self.joint_attach_child_locals.clear();
        self.reset_time_scale_for_play();
        // 事前読み込みは古い中身なので使わない（apply_script_transition_scene は同じパスの事前読み込みを消費するため）
        self.preloaded_scene = None;
        let result = self.apply_script_transition_scene(&path);
        self.send_hierarchy();
        result
    }
}

/// HashMap からキーが条件に合う要素を取り除き、取り除いた件数を返す。
///
/// # 引数
/// * `map`    - 対象（パス → 値）
/// * `refers` - キーが差し替え対象を指すか
fn retain_unmatched<V>(map: &mut std::collections::HashMap<String, V>, refers: &dyn Fn(&str) -> bool) -> usize {
    let before = map.len();
    map.retain(|key, _| !refers(key));
    before - map.len()
}
