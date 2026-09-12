// ============================================================
//  model_streaming.rs — モデル非同期ロードの App 側グルー
//
//  【役割】
//  `loader::async_loader`（ワーカースレッド・RAM キャッシュ・プリフェッチ）と
//  ECS / GPU をつなぐ層。責務は 3 つだけ。
//
//   1. **非同期スコープ**: 「いま構築しているアクタのモデルは非同期でよい」
//      という印をスレッドローカルに立てる。`scene::build_actor` がこれを見て
//      同期ロードするか要求だけ出すかを決める。
//   2. **保留スロットの台帳**: 非同期にしたモデルスロット（ECS の entity）を
//      控えておき、完成したらそこへ差し込む。
//   3. **毎フレームのポンプ**: 完成品を受け取り、予算の範囲で GPU アップロード
//      して ECS へ反映する。
//
//  【非同期にする範囲（意図的に狭い）】
//  シーン読み込み（`.scene` の一括構築）とサムネイル生成は **従来どおり同期**。
//  エディタは `SCENE_LOADED` を「全モデルが揃った」合図として使っており、
//  サムネイルは読み込み直後に GPU リソースを前提に描画するため、ここを非同期に
//  すると壊れる。非同期にするのは「シーン読み込み後に新しく生成されるアクタ」
//  ＝スクリプトの `GameObject.Instantiate` 経路だけ。
// ============================================================

use std::cell::{Cell, RefCell};
use std::sync::Arc;

use crate::engine::components::ModelComponent;
use crate::engine::core::loader::async_loader::{
    self, JobPriority, RequestState, UploadBudget,
};
use crate::engine::core::loader::model::Model;
use crate::engine::ecs::Entity;

use super::App;

// ============================================================
//  定数
// ============================================================

/// 保留スロットがこの秒数を超えても完成しない場合に警告を出す。
///
/// 「魚が出てこない」が発生したときに、ロード待ちで止まっているのか
/// 別の原因なのかをログで切り分けるための計器。
const PENDING_WARN_SECS: f32 = 10.0;

/// ストリーミング統計を標準エラーへ出す最小間隔（秒）。
/// 常時出すとログが埋まるため、変化があったときだけこの間隔で出す。
const STATS_LOG_INTERVAL_SECS: f64 = 10.0;

// ============================================================
//  非同期スコープ（スレッドローカル）
// ============================================================

thread_local! {
    /// 現在構築中のアクタでモデルの非同期ロードを許可するか。
    /// 既定 false ＝ 従来どおり同期（シーン読み込み・サムネイル・エディタ操作）。
    static ASYNC_SCOPE: Cell<bool> = const { Cell::new(false) };

    /// GPU 反映待ちのモデルスロット台帳。
    static PENDING_SLOTS: RefCell<Vec<PendingModelSlot>> = const { RefCell::new(Vec::new()) };
}

/// GPU 反映待ちのモデルスロット 1 件。
pub(crate) struct PendingModelSlot {
    /// `ModelComponent` を格納しているスロット専用エンティティ。
    pub slot_entity: Entity,
    /// 読み込み中のモデルパス（`assets://` 仮想パスまたは絶対パス）。
    pub path: String,
    /// 要求した時刻（待ち時間の警告用）。
    pub requested_at: std::time::Instant,
    /// 警告済みか（同じスロットで何度も警告しないための記憶）。
    pub warned: bool,
}

/// 非同期ロードを許可する区間を作る RAII ガード。
///
/// `drop` で必ず元の値へ戻すため、途中の `return` / `?` でも印が残らない。
/// ネストしても正しく復元できるよう、直前の値を保持する。
pub(crate) struct AsyncModelScope {
    previous: bool,
}

impl AsyncModelScope {
    /// 非同期ロードを許可した状態へ入る。
    pub(crate) fn enter() -> Self {
        let previous = ASYNC_SCOPE.with(|f| f.replace(true));
        Self { previous }
    }
}

impl Drop for AsyncModelScope {
    fn drop(&mut self) {
        let prev = self.previous;
        ASYNC_SCOPE.with(|f| f.set(prev));
    }
}

/// いま構築中のアクタでモデルの非同期ロードが許可されているか。
///
/// `scene::build_actor` の ModelComponent 分岐だけが見る。
/// ストリーマが未初期化（テスト・ヘッドレス）の場合は常に false（＝従来の同期経路）。
pub(crate) fn async_model_enabled() -> bool {
    ASYNC_SCOPE.with(|f| f.get()) && async_loader::get().is_some()
}

/// モデルの非同期ロードを要求する（`build_actor` から呼ぶ）。
///
/// 戻り値:
/// - `Some(model)` … RAM キャッシュに完成品があった（そのまま同期で使える）
/// - `None`        … ワーカーへ要求を出した（`gpu_model = None` で作り、後から差し込む）
///
/// 読み込み失敗が確定しているパスも `None` を返す（保留台帳には積まない）。
pub(crate) fn request_model_async(slot_entity: Entity, path: &str) -> Option<Arc<Model>> {
    let Some(streamer) = async_loader::get() else {
        return None;
    };
    match streamer.request(path, JobPriority::OnDemand) {
        // 先読み済み: 1 フレームも待たずにそのまま使える。
        RequestState::Ready(m) => Some(m),
        // 読み込み中: 完成したら差し込むスロットとして台帳へ載せる。
        RequestState::Pending => {
            PENDING_SLOTS.with(|p| {
                p.borrow_mut().push(PendingModelSlot {
                    slot_entity,
                    path: path.to_string(),
                    requested_at: std::time::Instant::now(),
                    warned: false,
                })
            });
            None
        }
        // 過去に失敗済み: 何度要求しても同じなので台帳に載せない
        //（ログは最初の失敗時に 1 回だけ出ている）。
        RequestState::Failed => None,
    }
}

/// 保留台帳を空にする（シーン差し替え時に必ず呼ぶ）。
///
/// **なぜ必要か**: 台帳は ECS の `Entity`（インデックス＋世代）を持つ。
/// シーンを差し替えると `World` ごと作り直されるため、同じ (index, generation) が
/// 新しいワールドの **別の実体** を指してしまう。持ち越すと無関係なアクタへ
/// モデルを差し込む事故になるので、シーン据え付けの度に破棄する。
pub(crate) fn clear_pending_slots() {
    PENDING_SLOTS.with(|p| p.borrow_mut().clear());
}

/// 保留中の件数（ログ・診断用）。
pub(crate) fn pending_count() -> usize {
    PENDING_SLOTS.with(|p| p.borrow().len())
}

// ============================================================
//  App 側の処理
// ============================================================

impl App {
    /// ストリーミングを初期化する（起動時に一度だけ）。
    ///
    /// `project_settings.json` のテキストから設定を読み、ワーカースレッドを起こす。
    /// 統合バッチの遅延解放しきい値（秒 → フレーム）もここで確定させる。
    pub(super) fn init_model_streaming(&mut self, settings_json: &str) {
        let cfg = async_loader::parse_streaming_config(settings_json);
        // 秒指定の常駐時間をフレーム数へ換算して控える（毎フレーム再計算しない）。
        // **無効化時も適用する**: 常駐時間は非同期ロードとは独立した設定で、
        // 「ストリーミングを切ったらバッチが 1 秒で消える」という別の挙動差を作らない。
        self.stale_batch_prune_frames = cfg.keep_alive_frames(self.target_fps);
        eprintln!(
            "[SEED INIT] streaming keep_alive={:.0}s -> {} frames (target_fps={})",
            cfg.keep_alive_secs, self.stale_batch_prune_frames, self.target_fps,
        );
        if !cfg.enabled {
            // ストリーマを作らない ＝ `async_model_enabled()` が常に false になり、
            // プリフェッチもポンプも何もしない。本機能導入前と完全に同じ経路へ戻る。
            eprintln!(
                "[SEED INIT] モデル非同期ロードは無効です（project_settings.json の streaming.enabled か環境変数 {} による）。すべて同期ロードで動作します。",
                async_loader::ENV_STREAMING_OVERRIDE,
            );
            return;
        }
        async_loader::init(cfg);
    }

    /// 統合バッチ／BLAS キャッシュの遅延解放しきい値（フレーム数）を返す。
    ///
    /// `streaming.keep_alive_secs` から起動時に換算した値。まだ初期化されていない
    /// （ストリーミング設定を読む前のフレーム）場合は既定 30 秒相当へ落とす。
    /// **フレーム単位のまま**なのは、既存の不在カウンタ（`batch_absent_frames`）が
    /// フレーム単位で数えているため。判定式を変えずにしきい値だけデータ駆動にする。
    pub(super) fn stale_batch_prune_frames(&self) -> u32 {
        if self.stale_batch_prune_frames > 0 {
            return self.stale_batch_prune_frames;
        }
        async_loader::StreamingConfig::default().keep_alive_frames(self.target_fps)
    }

    /// シーン据え付け後にプリフェッチを開始する。
    ///
    /// 2 系統の候補を低優先度で先読みする。どちらもワーカー側で走査するので
    /// メインスレッドはここでディスクに触らない。
    ///
    /// 1. **シーン内のプレハブ参照**（`prefab_source`）… そのプレハブが参照する
    ///    モデルを先読みする。再展開・複製で即座に必要になる可能性が高い。
    /// 2. **アセット配下の `.actor` 走査**（起動中 1 回だけ）… スクリプトが
    ///    `Instantiate` するプレハブ（魚など）はシーン JSON に現れないため、
    ///    「後で生成されるかもしれないプレハブ」をここで拾う。
    ///    走査範囲は `streaming.prefetch_dirs` で絞れる（既定はアセットルート全体）。
    pub(super) fn start_model_prefetch(&mut self) {
        let Some(streamer) = async_loader::get() else {
            return;
        };
        if !streamer.config().prefetch {
            return;
        }

        // ① シーン内のプレハブ参照を集める（アクタツリーの再帰走査。I/O 無し）。
        if let Some(scene) = &self.scene {
            let mut sources: Vec<String> = Vec::new();
            collect_prefab_sources(scene.actors.as_slice(), &mut sources);
            for s in sources {
                streamer.prefetch_prefab(&s);
            }
        }

        // ② アセット配下の `.actor` 走査（プロセスで 1 回だけ実行される）。
        streamer.start_dir_prefetch();
    }

    /// 完成したモデルを ECS へ差し込む（毎フレーム 1 回、描画収集より前に呼ぶ）。
    ///
    /// - ワーカーの完成品をチャネルから吸い出して RAM キャッシュへ入れる（I/O 無し）
    /// - 保留台帳を先頭から見て、完成しているものを予算の範囲で GPU アップロードする
    /// - 対象エンティティが既に破棄されていた保留は捨てる
    ///
    /// 予算（件数・時間）を超えた分は次フレームへ持ち越すため、
    /// 大量同時出現でもここでフレームが飛ぶことはない。
    pub(super) fn pump_model_streaming(&mut self) {
        let Some(streamer) = async_loader::get() else {
            return;
        };
        // ① ワーカーの完成品を取り込む（HashMap への移動だけ。ディスクも GPU も触らない）。
        streamer.pump();

        if self.draw_ctx.is_none() || self.scene.is_none() {
            return;
        }
        // 保留が無ければここで終わり（定常状態では毎フレームここで抜ける）。
        if pending_count() == 0 {
            self.log_streaming_stats_if_due();
            return;
        }

        let cfg = streamer.config();
        let mut budget = UploadBudget::new(cfg.max_uploads_per_frame, cfg.upload_budget_ms);
        let mut uploaded = 0usize;
        let t_pump = std::time::Instant::now();

        // 台帳を丸ごと取り出し、処理できなかったものを書き戻す（借用の重なりを避ける）。
        let slots: Vec<PendingModelSlot> = PENDING_SLOTS.with(|p| std::mem::take(&mut *p.borrow_mut()));
        let mut keep: Vec<PendingModelSlot> = Vec::with_capacity(slots.len());

        for mut slot in slots {
            // 予算切れ: 以降は次フレームへ回す。
            if !budget.allows(uploaded) {
                keep.push(slot);
                continue;
            }
            // 読み込み失敗が確定したパスは諦める（ログはストリーマ側で出済み）。
            if streamer.is_failed(&slot.path) {
                continue;
            }
            // まだ完成していない: 次フレーム以降へ持ち越す。
            let Some(model) = streamer.take_ready(&slot.path) else {
                // 長く待たされている場合だけ 1 度警告する（沈黙のまま出てこない事故の可視化）。
                if !slot.warned && slot.requested_at.elapsed().as_secs_f32() > PENDING_WARN_SECS {
                    slot.warned = true;
                    eprintln!(
                        "[SEED stream] モデルの到着が {:.0} 秒を超えました: {}（待ち行列 {} 件）",
                        PENDING_WARN_SECS,
                        slot.path,
                        streamer.queued(),
                    );
                }
                keep.push(slot);
                continue;
            };

            // 完成品を GPU へ載せて ECS へ差し込む。
            if self.install_streamed_model(&slot, Arc::clone(&model)) {
                uploaded += 1;
                budget.consume();
            } else {
                // 差し込めなかった（出現直後に Destroy された／別経路で差し替わった）。
                // 保留は捨てるが、**読み込み済みモデルは RAM キャッシュへ戻す**。
                // 捨ててしまうと「生成 → 即破棄」を繰り返す演出でディスクから
                // 読み直し続けることになる（非同期にした意味が消える）。
                streamer.put_back(&slot.path, model);
            }
        }

        PENDING_SLOTS.with(|p| {
            let mut v = p.borrow_mut();
            // ポンプ中に新しい要求が積まれている可能性があるので、持ち越し分は前へ入れる。
            keep.append(&mut v);
            *v = keep;
        });

        if uploaded > 0 {
            let ms = t_pump.elapsed().as_secs_f64() * 1000.0;
            let (n, bytes) = streamer.ram_usage();
            eprintln!(
                "[SEED stream] メイン適用: {uploaded} 件 / {ms:.2} ms（保留 {} 件, RAM キャッシュ {} 件 {:.1} MiB）",
                pending_count(),
                n,
                bytes as f64 / (1024.0 * 1024.0),
            );
        }
        self.log_streaming_stats_if_due();
    }

    /// 完成した CPU モデルを GPU へアップロードし、対象スロットの
    /// `ModelComponent` へ差し込む。差し込めたら true。
    ///
    /// 対象エンティティが破棄済み（出現直後に Destroy された等）なら false を返す。
    /// 生成する GPU リソースはシーン読み込み時（`scene::build_actor`）と完全に同じ
    /// （`upload_model_with_overrides` ＋ `create_instanced_batch`）で、
    /// 描画結果は同期ロードのときと 1 ビットも変わらない。
    fn install_streamed_model(&mut self, slot: &PendingModelSlot, model: Arc<Model>) -> bool {
        // draw_ctx（不変）と scene.world（可変）は別フィールドなので分割借用できる。
        let ctx = self.draw_ctx.as_ref().unwrap();
        let scene = self.scene.as_mut().unwrap();

        let Some(mc) = scene.world.get_mut::<ModelComponent>(slot.slot_entity) else {
            // アクタが既に破棄されている（Instantiate 直後に Destroy 等）。
            return false;
        };
        // 別経路（インスペクタでのモデル差し替え等）で既にモデルが入っている／
        // パスが変わっている場合は、このロード結果を上書きに使わない。
        if mc.model.is_some() || mc.source_path != slot.path {
            return false;
        }

        let gpu_model = ctx.upload_model_with_overrides(&*model, &mc.material_overrides);
        let instanced_batch = ctx.create_instanced_batch(&*model, mc.instance_mats.len() as u32);
        mc.model = Some(Arc::clone(&model));
        mc.gpu_model = Some(gpu_model);
        mc.instanced_batch = Some(instanced_batch);

        // プロセス内 CPU キャッシュへも入れる。以降、同じモデルのアクタは
        // 非同期経路を通らずその場で（ディスクに触れず）構築される。
        ctx.model_cache
            .borrow_mut()
            .entry(slot.path.clone())
            .or_insert(model);
        true
    }

    /// ストリーミングの統計を一定間隔でログへ出す（変化があったときだけ）。
    fn log_streaming_stats_if_due(&mut self) {
        let Some(streamer) = async_loader::get() else {
            return;
        };
        let st = streamer.stats();
        let now = std::time::Instant::now();
        let due = self
            .streaming_stats_logged_at
            .is_none_or(|t| now.duration_since(t).as_secs_f64() >= STATS_LOG_INTERVAL_SECS);
        if !due {
            return;
        }
        // 前回から完了件数が増えていないなら黙っている（定常時は無出力）。
        if st.completed + st.failed == self.streaming_stats_last_total {
            return;
        }
        self.streaming_stats_logged_at = Some(now);
        self.streaming_stats_last_total = st.completed + st.failed;
        let (n, bytes) = streamer.ram_usage();
        eprintln!(
            "[SEED stream] 累計 完了 {} 件 / 失敗 {} 件 / ワーカー総時間 {:.0} ms、待ち行列 {} 件、RAM キャッシュ {} 件 {:.1} MiB",
            st.completed,
            st.failed,
            st.worker_ms_total,
            streamer.queued(),
            n,
            bytes as f64 / (1024.0 * 1024.0),
        );
    }
}

/// アクタツリーを再帰的に走査して `prefab_source`（`assets://…​.actor`）を集める。
fn collect_prefab_sources(
    actors: &[crate::engine::structs::objects::Actor],
    out: &mut Vec<String>,
) {
    for a in actors {
        if let Some(src) = &a.prefab_source {
            out.push(src.clone());
        }
        collect_prefab_sources(a.children(), out);
    }
}

// ============================================================
//  テスト
// ============================================================
#[cfg(test)]
mod tests {
    use super::*;

    /// 非同期スコープはネストしても抜けた時点で元へ戻ること
    /// （途中の early return で「非同期のまま」が漏れないことの保証）。
    #[test]
    fn async_scope_restores_previous_value() {
        assert!(!ASYNC_SCOPE.with(|f| f.get()), "既定は同期");
        {
            let _outer = AsyncModelScope::enter();
            assert!(ASYNC_SCOPE.with(|f| f.get()));
            {
                let _inner = AsyncModelScope::enter();
                assert!(ASYNC_SCOPE.with(|f| f.get()));
            }
            assert!(ASYNC_SCOPE.with(|f| f.get()), "内側を抜けても外側は有効");
        }
        assert!(!ASYNC_SCOPE.with(|f| f.get()), "全て抜けたら同期へ戻る");
    }

    /// ストリーマ未初期化なら `async_model_enabled` は常に false
    /// （＝従来の同期経路。ヘッドレス・単体テストで挙動が変わらない）。
    #[test]
    fn async_disabled_without_streamer() {
        let _scope = AsyncModelScope::enter();
        assert!(
            !async_model_enabled(),
            "ストリーマ未初期化なら非同期にはしない"
        );
    }

    /// 保留台帳の破棄が効くこと（シーン差し替えで entity を持ち越さない）。
    #[test]
    fn pending_slots_can_be_cleared() {
        PENDING_SLOTS.with(|p| {
            p.borrow_mut().push(PendingModelSlot {
                slot_entity: Entity::from_raw(0, 0),
                path: "assets://a.glb".to_string(),
                requested_at: std::time::Instant::now(),
                warned: false,
            })
        });
        assert_eq!(pending_count(), 1);
        clear_pending_slots();
        assert_eq!(pending_count(), 0);
    }
}
