// ============================================================
//  terrain_cover_ops.rs — 地表カバー場のエンジン統合層（Phase I3.1）
//
//  【責務】
//    純粋データ層（`engine::terrain::cover`）と ECS / GPU / ファイル IO を橋渡しする。
//      ① cover_materials.json の読み込み（データドリブンの入口）
//      ② `CoverEmitterComponent` → `CoverEmitSpec` のワールド解決（マスク画像のロード込み）
//      ③ 積算の駆動（Edit のシミュレートボタン / Play 中の毎フレーム）
//      ④ カバー場 → 地形メッシュ頂点への焼き込み（描画への反映）
//      ⑤ .tcover の保存・読み込み（.tvox / .tscatter と同じディレクトリ・同じ流儀）
//      ⑥ Play 中の積算を揮発させるスナップショット / 復元
//
//  【CPU で積算する理由（GPU コンピュートを採らなかった判断）】
//    1 チャンクのカバー場は 32×32 = 1024 テクセルしか無い。既定の地面 48 チャンクでも
//    5 万テクセル弱で、1 テクセルあたりの計算は数個の乗算と比較だけである。
//    一方 GPU 化すると、保存（.tcover）のたびにテクスチャの読み戻し（非同期マップ）が
//    要り、「シミュレートボタンを押して N 秒ぶん即時計算する」も GPU の往復回数分の
//    フレームを跨ぐことになる。積算・保存・テストを 1 本の同期コードで完結させる
//    ほうが、この規模では明確に有利と判断した。
//    実測は `SEED_PERF_LOG=1` の `[PERF f=...] cover=...ms` で監視できる。
//
//  【描画への反映が「頂点の焼き直し」である理由】
//    docs/cover_field.md と terrain_mesh_build.rs::rebuild_terrain_model_with_cover の
//    コメントを参照。要約すると、変位を頂点位置へ焼くことで
//    シャドウ・深度・ID・RT のすべてが自動的に一致するためである。
//    頂点の焼き直しは 32×32 の積算より重いので、`COVER_APPLY_INTERVAL_SEC` で間引く。
// ============================================================

use std::collections::{HashMap, HashSet};
use std::sync::Arc;
use std::time::Instant;

use crate::engine::components::cover_emitter_component::{
    CoverEmitterComponent, CoverEmitterRangeKind,
};
use crate::engine::components::{ComponentKind, ModelComponent, Transform};
use crate::engine::core::loader::model::Model;
use crate::engine::structs::objects::Actor;
use crate::engine::terrain::chunk_coord::ChunkCoord;
use crate::engine::terrain::cover::{
    accumulate_chunk, read_cover_chunk, write_cover_chunk, CoverEmitRange, CoverEmitSpec,
    CoverField, CoverMask, CoverMaterialSet, CoverSurface,
};

use super::terrain_mesh_build::rebuild_terrain_model_with_cover;
use super::App;

// ─── アセットの所在（props.json / layers.json と同じ流儀）─────────────────────

/// カバー素材定義アセットの既定パス。
const COVER_MATERIALS_ASSET: &str = "assets://terrain/cover_materials.json";

/// カバー素材定義の差し替え用環境変数（開発時の実験用）。
const COVER_MATERIALS_PATH_ENV: &str = "SEED_TERRAIN_COVER_MATERIALS";

// ─── 調整用定数（マジックナンバー禁止）───────────────────────────────────────

/// カバー場を頂点へ焼き直す最短間隔（秒）。
///
/// 積算そのもの（32×32 の加算）は毎フレームやっても無視できるが、頂点の焼き直しは
/// 「チャンクの全頂点を作り直して GPU へ再アップロード」であり、可視チャンク数に
/// 比例して重い。雪が積もる速さに対して 10Hz で十分に滑らかに見えるため、
/// ここで間引く（1 フレームぶんの積み増しは肉眼では判別できない）。
const COVER_APPLY_INTERVAL_SEC: f32 = 0.1;

/// Edit のシミュレートボタンで「秒数指定」したときの標準の 1 ステップ時間（秒）。
///
/// 指定秒数を一気に 1 ステップで計算すると、素材置き換え規則
/// （古い素材を削ってから新素材が乗る）が 1 回しか適用されず、
/// 「落ち葉の上に雪」の中間状態が飛ぶ。Play と同じ 60Hz 相当で刻む。
const COVER_SIMULATE_STEP_SEC: f32 = 1.0 / 60.0;

/// 秒数指定シミュレートの 1 回あたりのステップ数上限。
///
/// 【なぜ上限を切るのか】1 ステップは「全チャンク × 32×32 テクセル」を舐める。
/// 既定の 48 チャンクで約 5 万テクセルなので、60Hz 刻みのまま 1 時間ぶんを
/// 指定されると 21 万ステップ＝100 億テクセル演算となり、エディタが数分固まる。
///
/// 【刻みを粗くしても壊れない理由】自然減衰が無く、積算は
/// `量 += 被覆率 × 強度 × dt × 傾斜` の単調加算なので、刻み幅を変えても
/// **飽和前の合計量は厳密に同じ**である。刻み数が効くのは異素材の
/// 置き換え（削り → 乗せ）の途中経過だけで、そこには 600 ステップあれば十分。
const COVER_SIMULATE_MAX_STEPS: u32 = 600;

/// 秒数指定シミュレートで受け付ける秒数の上限。
///
/// 自然減衰が無いため、どんな強度でもこれだけ回せば必ず飽和している
/// （既定強度 0.2/秒 なら 5 秒で満量）。非有限値・負値の防波堤も兼ねる。
const COVER_SIMULATE_MAX_SECONDS: f32 = 3600.0;

/// シミュレートの秒数入力がこの値以下なら「連続シミュレート（再生形式）」とみなす。
const COVER_SIMULATE_CONTINUOUS_THRESHOLD: f32 = 0.0;

/// 連続シミュレート中の 1 フレームあたりの時間刻み上限（秒）。
///
/// フレーム落ちした瞬間に何秒ぶんも一気に積もると、押した時間と結果が対応しなくなる。
const COVER_SIMULATE_MAX_DT: f32 = 1.0 / 20.0;

/// ミリ秒換算（計測ログ用）。
const MILLIS_PER_SEC: f64 = 1000.0;

// ============================================================
//  カバー適用前のメッシュ基準値（変位の累積を防ぐキャッシュ）
// ============================================================

/// カバーを適用する前のチャンクメッシュの基準値。
///
/// 【なぜ必要か】
///   変位は「基準位置 ＋ 法線 × 量 × 高さ」で作る。もし現在の（既に変位済みの）
///   位置へ足し込むと、適用のたびに雪が伸び続けて地形が破裂する。
///   毎回この基準へ戻してから足し直すことで、適用回数に依らず結果が一意になる。
///
///   平均アルベドも同じ理由で「カバー無しの値」を覚えておく必要がある
///   （カバー色へ寄せた値を基準にすると、適用のたびに白へ漸近してしまう）。
pub struct CoverBaseMesh {
    /// カバー適用前の頂点位置（チャンクローカル座標。頂点列と同順・同長）。
    positions: Arc<Vec<[f32; 3]>>,
    /// カバー適用前のチャンク平均アルベド（リニア RGB）。
    avg_albedo: [f32; 3],
}

// ============================================================
//  App — カバー場のエンジン統合
// ============================================================

impl App {
    // ─── ① 素材定義の読み込み ───────────────────────────────────────────────

    /// cover_materials.json を読み込んで `terrain.cover_materials` へ格納する。
    ///
    /// 読み込み元は環境変数 `SEED_TERRAIN_COVER_MATERIALS` >
    /// `assets://terrain/cover_materials.json` の順で解決する
    /// （`ensure_terrain_props` / `ensure_terrain_layers` と完全に同じ流儀）。
    /// 読めなければ `CoverMaterialSet::default()`（雪・落ち葉・濡れの 3 種）へ
    /// フォールバックし、警告は 1 回だけ出す。
    pub(super) fn ensure_cover_materials(&mut self) {
        let source = std::env::var(COVER_MATERIALS_PATH_ENV)
            .ok()
            .filter(|p| !p.is_empty())
            .unwrap_or_else(|| COVER_MATERIALS_ASSET.to_string());

        let set = match crate::engine::asset_fs::read_string(&source) {
            Ok(text) => match CoverMaterialSet::from_json_str(&text) {
                Ok(set) => set,
                Err(e) => {
                    self.warn_cover_materials_once(&format!("parse failed ({source}): {e}"));
                    CoverMaterialSet::default()
                }
            },
            Err(_) => {
                self.warn_cover_materials_once(&format!("not found: {source}（既定セットを使用）"));
                CoverMaterialSet::default()
            }
        };
        self.terrain.cover_materials = set;
    }

    /// カバー素材定義の警告を 1 回だけ出す（ログを埋めないため）。
    fn warn_cover_materials_once(&mut self, message: &str) {
        if self.terrain.cover_materials_warned {
            return;
        }
        self.terrain.cover_materials_warned = true;
        eprintln!("[SEED terrain] cover_materials.json {message}");
    }

    // ─── ② エミッタのワールド解決 ───────────────────────────────────────────

    /// シーンの全アクタを走査し、有効なカバーエミッタをワールド解決して集める。
    ///
    /// スキップ規則は他の収集処理（`collect_interaction_sources` / `collect_water_volumes`）と
    /// 完全に揃える:
    ///   ・world_line が一致しないルートは対象外
    ///   ・active=false のアクターはサブツリーごとスキップ（祖先の非アクティブも伝播）
    ///   ・enabled=false のスロットはスキップ
    ///   ・コンポーネント側の enabled=false もスキップ
    ///   ・強度 0 以下・未定義の素材 ID はスキップ（積算で何も起きないため）
    pub(super) fn collect_cover_emitters(&mut self) -> Vec<CoverEmitSpec> {
        // ─── マスク画像の要求パスを先に洗い出して読み込む ───
        //   `&mut self`（マスクキャッシュ）と `&self.scene`（走査）を同時に借りられないため、
        //   「必要なパスの収集 → ロード → 仕様の組み立て」の 3 段に分ける。
        let raw = {
            let Some(scene) = self.scene.as_ref() else { return Vec::new() };
            let wl = self.active_world_line;
            let mut out: Vec<RawCoverEmitter> = Vec::new();
            for root in scene.actors.iter().filter(|a| a.world_line == wl) {
                collect_cover_in_actor(root, &scene.world, &mut out, true);
            }
            out
        };
        if raw.is_empty() {
            return Vec::new();
        }

        // ─── マスク画像をキャッシュへ確保する ───
        for r in &raw {
            if r.range_kind == CoverEmitterRangeKind::TextureMask && !r.mask_path.is_empty() {
                self.ensure_cover_mask(&r.mask_path);
            }
        }

        // ─── 素材 ID を添字へ解決し、仕様を組み立てる ───
        let mut out = Vec::with_capacity(raw.len());
        for r in raw {
            let Some(material_index) = self.terrain.cover_materials.index_of(&r.material_id)
            else {
                // 未定義の素材 ID。何も積もらないので落とす（警告は出さない＝
                // アセット編集中に ID を打ち替えている最中にログが埋まるのを避ける）。
                continue;
            };
            let range = match r.range_kind {
                CoverEmitterRangeKind::Global => CoverEmitRange::Global,
                CoverEmitterRangeKind::Region => CoverEmitRange::Region {
                    center: r.world_pos,
                    half_extents: r.extents,
                    fade: r.fade,
                },
                CoverEmitterRangeKind::TextureMask => {
                    let mask = self
                        .terrain
                        .cover_mask_cache
                        .get(&r.mask_path)
                        .cloned()
                        .unwrap_or_else(CoverMask::empty);
                    // マスクが無効（未設定・読み込み失敗）ならこのエミッタは何も降らせない。
                    if !mask.is_valid() {
                        continue;
                    }
                    CoverEmitRange::TextureMask {
                        center: r.world_pos,
                        size_xz: r.mask_size,
                        mask,
                    }
                }
            };
            out.push(CoverEmitSpec {
                range,
                material_index: material_index as u8,
                rate: r.strength,
            });
        }
        out
    }

    /// マスク画像をデコードしてキャッシュへ入れる（既にあれば何もしない）。
    ///
    /// 読み込みに失敗した場合も「無効なマスク」をキャッシュへ入れる。
    /// こうしないと毎フレーム同じ壊れたパスを読み直してしまう
    /// （`scatter_model_failed` と同じ考え方）。
    fn ensure_cover_mask(&mut self, path: &str) {
        if self.terrain.cover_mask_cache.contains_key(path) {
            return;
        }
        let mask = match crate::engine::asset_fs::read_image_result(path) {
            Ok(img) => {
                let (w, h) = (img.width() as usize, img.height() as usize);
                // RGBA から輝度を取らず **R チャンネル**をそのまま使う。
                // グレースケール画像では R=G=B であり、カラー画像を入れられた場合も
                // 「赤成分をマスクとして使う」という単純で予測可能な規則になる。
                let pixels: Vec<u8> = img.pixels().map(|p| p.0[0]).collect();
                CoverMask { width: w, height: h, pixels }
            }
            Err(e) => {
                eprintln!("[SEED terrain] cover mask load failed: {path} err={e}");
                CoverMask::empty()
            }
        };
        self.terrain.cover_mask_cache.insert(path.to_string(), mask);
    }

    // ─── ③ 積算の駆動 ───────────────────────────────────────────────────────

    /// カバー場を `dt` 秒ぶん進める（毎フレーム呼ばれる）。
    ///
    /// - `play_running`: Play かつ非ポーズか。true なら Play の毎フレーム積算を行う。
    ///
    /// Edit 中は `terrain.cover_sim_running`（シミュレートボタン）が立っているあいだだけ進む。
    /// **どちらも false のフレームは 1 命令も走らない**（エミッタ収集すら行わない）。
    ///
    /// 戻り値は積算に要した時間（ミリ秒。計測ログ用）。
    pub(super) fn tick_terrain_cover(&mut self, dt: f32, play_running: bool) -> f64 {
        let running = play_running || self.terrain.cover_sim_running;
        if !running || self.terrain.chunks.is_empty() {
            return 0.0;
        }
        let t = Instant::now();
        // フレーム落ち時に何秒ぶんも一気に積もらないよう刻みを制限する。
        let step = dt.clamp(0.0, COVER_SIMULATE_MAX_DT);
        if step > 0.0 {
            let emitters = self.collect_cover_emitters();
            self.accumulate_cover(&emitters, step);
        }
        t.elapsed().as_secs_f64() * MILLIS_PER_SEC
    }

    /// 全チャンクのカバー場を `dt` 秒ぶん進める（純粋関数 `accumulate_chunk` の駆動）。
    ///
    /// 変化したチャンクだけを「未保存」と「頂点焼き直し待ち」にマークする。
    fn accumulate_cover(&mut self, emitters: &[CoverEmitSpec], dt: f32) {
        if emitters.is_empty() {
            return;
        }
        let settings = self.terrain.settings.clone();
        let extent = settings.chunk_extent();

        // 地表情報（密度場の派生キャッシュ）を必要なぶんだけ用意する。
        let coords: Vec<ChunkCoord> = self.terrain.chunks.keys().copied().collect();
        for coord in &coords {
            self.ensure_cover_surface(*coord);
        }

        let mut changed: Vec<ChunkCoord> = Vec::new();
        for coord in coords {
            let Some(surface) = self.terrain.cover_surface.get(&coord) else { continue };
            let origin = coord.world_origin(&settings);
            let field = self.terrain.cover.entry(coord).or_default();
            if accumulate_chunk(field, surface, origin, extent, emitters, dt) {
                changed.push(coord);
            }
        }
        for coord in changed {
            self.terrain.cover_dirty.insert(coord);
            self.terrain.cover_pending_apply.insert(coord);
        }
    }

    /// 指定チャンクの地表情報（密度場からの派生）をキャッシュへ用意する。
    fn ensure_cover_surface(&mut self, coord: ChunkCoord) {
        if self.terrain.cover_surface.contains_key(&coord) {
            return;
        }
        let Some(chunk) = self.terrain.chunks.get(&coord) else { return };
        let origin_y = coord.world_origin(&self.terrain.settings)[1];
        let surface = CoverSurface::from_chunk(chunk, &self.terrain.settings, origin_y);
        self.terrain.cover_surface.insert(coord, surface);
    }

    /// 地形が編集された（再メッシュされた）チャンクのカバー派生データを捨てる。
    ///
    /// `remesh_chunks` の入口から呼ばれる。捨てるのは
    ///   ・地表情報（密度場が変わったので法線・高さが変わる）
    ///   ・メッシュ基準値（頂点が作り直されるので古い基準は無意味）
    /// であり、**カバー場そのもの（積もった量）は保持する**
    /// （地形を少し掘っただけで積雪が消えるのは直感に反するため）。
    pub(super) fn invalidate_cover_for_remesh(&mut self, coord: ChunkCoord) {
        self.terrain.cover_surface.remove(&coord);
        self.terrain.cover_base_mesh.remove(&coord);
        // カバーが乗っているチャンクは、新しいメッシュへ焼き直す必要がある。
        if self.terrain.cover.get(&coord).is_some_and(|f| !f.is_empty()) {
            self.terrain.cover_pending_apply.insert(coord);
        }
    }

    // ─── ④ 頂点への焼き込み ─────────────────────────────────────────────────

    /// 焼き直し待ちのチャンクへカバー場を反映する（毎フレーム呼ばれる）。
    ///
    /// `COVER_APPLY_INTERVAL_SEC` で間引く。待ちが空のフレームは即座に返る。
    ///
    /// 戻り値は焼き直しに要した時間（ミリ秒。計測ログ用）。
    pub(super) fn apply_pending_cover(&mut self, dt: f32) -> f64 {
        if self.terrain.cover_pending_apply.is_empty() {
            // 待ちが無いあいだはタイマーを進めない（次に積もった瞬間へ即反映するため）。
            self.terrain.cover_apply_timer = COVER_APPLY_INTERVAL_SEC;
            return 0.0;
        }
        self.terrain.cover_apply_timer += dt.max(0.0);
        if self.terrain.cover_apply_timer < COVER_APPLY_INTERVAL_SEC {
            return 0.0;
        }
        self.terrain.cover_apply_timer = 0.0;
        if self.draw_ctx.is_none() {
            return 0.0;
        }

        let t_total = Instant::now();
        // 決定性のため座標順に処理する（ログ・GPU コマンド列を再現可能にする）。
        let mut coords: Vec<ChunkCoord> =
            std::mem::take(&mut self.terrain.cover_pending_apply).into_iter().collect();
        coords.sort_by_key(|c| (c.x, c.y, c.z));

        let extent = self.terrain.settings.chunk_extent();
        let materials = self.terrain.cover_materials.clone();
        let mut applied = 0usize;
        for coord in coords {
            if self.apply_cover_to_chunk(coord, extent, &materials) {
                applied += 1;
            }
        }

        let total_ms = t_total.elapsed().as_secs_f64() * MILLIS_PER_SEC;
        if *super::terrain_ops::PERF_TERRAIN_LOG_ENABLED && applied > 0 {
            eprintln!("[PERF terrain] cover apply chunks={applied} total={total_ms:.2}ms");
        }
        total_ms
    }

    /// 1 チャンクぶんのカバー場を頂点へ焼き込み、GPU 頂点バッファを書き換える。
    ///
    /// 【`apply_terrain_paint_colors` と同じ理由で `invalidate_geometry_caches` を呼ばない】
    ///   あれは BLAS 再構築と統合バッチ再構築を誘発する。カバーの変位は数センチであり、
    ///   RT 影・RT 反射が「カバー無しの地形」を辿ることによる差は肉眼で識別できない。
    ///   毎フレーム BLAS を作り直すコストのほうが桁違いに大きいので呼ばない
    ///   （docs/cover_field.md に既知の制限として明記）。
    ///   ラスタ経路（G-Buffer・シャドウマップ・深度・ID）は同じ頂点バッファを読むため
    ///   すべて自動的に一致する。
    ///
    /// 戻り値は実際に書き換えたか。
    fn apply_cover_to_chunk(
        &mut self,
        coord: ChunkCoord,
        extent: f32,
        materials: &CoverMaterialSet,
    ) -> bool {
        let Some(field) = self.terrain.cover.get(&coord).cloned() else { return false };
        let Some(&slot_entity) = self.terrain.chunk_slot_entity.get(&coord) else { return false };

        // ─── 基準メッシュ（カバー適用前の頂点位置・平均アルベド）を用意する ───
        //   無ければ現在の CPU モデルから作る。`invalidate_cover_for_remesh` が
        //   再メッシュのたびに捨てるので、ここで拾うのは常に「カバー未適用のメッシュ」である。
        let new_model: Option<Model> = {
            let Some(scene) = self.scene.as_ref() else { return false };
            let Some(mc) = scene.world.get::<ModelComponent>(slot_entity) else { return false };
            // 空メッシュチャンク（全 AIR / 全 SOLID）は書き換える頂点が無い。
            if mc.gpu_model.is_none() {
                return false;
            }
            let Some(model) = mc.model.as_ref() else { return false };
            let (Some(mesh), Some(material)) = (model.meshes.first(), model.materials.first())
            else {
                return false;
            };
            let Some(prim) = mesh.primitives.first() else { return false };

            let base = match self.terrain.cover_base_mesh.get(&coord) {
                Some(b) if b.positions.len() == prim.vertices.len() => b,
                _ => {
                    let positions: Vec<[f32; 3]> =
                        prim.vertices.iter().map(|v| v.position).collect();
                    let avg = material.avg_albedo;
                    self.terrain.cover_base_mesh.insert(
                        coord,
                        CoverBaseMesh {
                            positions: Arc::new(positions),
                            avg_albedo: [avg[0], avg[1], avg[2]],
                        },
                    );
                    // 借用の都合で入れ直してから引き直す（挿入直後なので必ず存在する）。
                    self.terrain.cover_base_mesh.get(&coord).unwrap()
                }
            };

            rebuild_terrain_model_with_cover(
                &prim.vertices,
                &prim.indices,
                &model.name,
                material.terrain_palette,
                &base.positions,
                base.avg_albedo,
                &field,
                materials,
                extent,
            )
        };
        let Some(new_model) = new_model else { return false };

        // ─── CPU モデル差し替え ＋ GPU 頂点バッファの丸ごと書き換え ───
        //   `apply_terrain_paint_colors` の ⑧ と同一の手順（1 回の write_buffer で全頂点）。
        let ctx = self.draw_ctx.as_ref().unwrap();
        let Some(scene) = self.scene.as_mut() else { return false };
        let Some(mc) = scene.world.get_mut::<ModelComponent>(slot_entity) else { return false };
        mc.model = Some(Arc::new(new_model));

        // 平均アルベドを GPU 側へも反映する（RT 反射・水面反射・DDGI が読む値）。
        if let Some(avg) = mc
            .model
            .as_ref()
            .and_then(|m| m.materials.first())
            .map(|mat| mat.avg_albedo)
        {
            if let Some(gpu) = mc.gpu_model.as_mut() {
                if let Some(slot) = gpu.avg_albedos.first_mut() {
                    *slot = avg;
                }
            }
        }

        if let (Some(model), Some(gpu)) = (mc.model.as_ref(), mc.gpu_model.as_ref()) {
            if let (Some(prim), Some(gpu_prim)) = (
                model.meshes.first().and_then(|m| m.primitives.first()),
                gpu.meshes.first().and_then(|m| m.primitives.first()),
            ) {
                ctx.queue.write_buffer(
                    &gpu_prim.vertex_buffer,
                    0,
                    bytemuck::cast_slice(&prim.vertices),
                );
            }
        }
        true
    }

    // ─── IPC ハンドラ（シミュレート / 停止 / 全消去）─────────────────────────

    /// カバー場の Edit シミュレート（`TERRAIN_COVER_SIMULATE:{seconds}`）。
    ///
    /// - `seconds > 0`: その秒数ぶんを **即時**（このフレーム内）計算して停止する
    /// - `seconds <= 0`: 停止コマンドが来るまで毎フレーム積算する（再生形式）
    ///
    /// 結果は編集データとしてカバー場へ書かれ、`TERRAIN_SAVE` の保存対象になる。
    ///
    /// 【素材定義をここで読み直さない理由】
    ///   カバー素材の色・粗さは GPU uniform（group3）に載っており、その作り直しは
    ///   `ensure_terrain_layers` 経由でしか起きない。ここで CPU 側だけ読み直すと
    ///   「変位は新しい定義・色は古い定義」というちぐはぐが生じ、素材を増減させた
    ///   場合は添字までずれる。cover_materials.json の反映は layers.json と同じ
    ///   `TERRAIN_RELOAD_LAYERS`（地形設定ウィンドウの保存）に一本化する。
    pub(super) fn handle_terrain_cover_simulate(&mut self, seconds: f32) {
        // ─── 連続シミュレート（秒数入力なし）───
        if !(seconds > COVER_SIMULATE_CONTINUOUS_THRESHOLD) {
            self.terrain.cover_sim_running = true;
            if let Some(ipc) = &self.ipc {
                ipc.send("TERRAIN_COVER_SIMULATE_OK:running");
            }
            return;
        }

        // ─── 秒数指定: 有限ステップ数で一気に回す ───
        //   刻みは「60Hz 相当」を基本とし、指定秒数が長い場合だけ
        //   ステップ数を上限で頭打ちにして刻み幅のほうを伸ばす
        //   （合計量は刻み幅に依らないので結果は変わらない。定数コメント参照）。
        let t = Instant::now();
        let emitters = self.collect_cover_emitters();
        let total = seconds.min(COVER_SIMULATE_MAX_SECONDS);
        let steps = ((total / COVER_SIMULATE_STEP_SEC).ceil() as u32)
            .clamp(1, COVER_SIMULATE_MAX_STEPS);
        let step_dt = total / steps as f32;
        for _ in 0..steps {
            self.accumulate_cover(&emitters, step_dt);
        }
        // 秒数指定は「押したら計算して止まる」なので、連続シミュレートは開始しない。
        self.terrain.cover_sim_running = false;
        // 即時計算なので焼き直しの間引きを待たずに反映する。
        self.terrain.cover_apply_timer = COVER_APPLY_INTERVAL_SEC;

        let ms = t.elapsed().as_secs_f64() * MILLIS_PER_SEC;
        eprintln!(
            "[SEED terrain] cover simulate: {seconds:.2}s in {steps} steps, \
             emitters={} chunks={} ({ms:.1}ms)",
            emitters.len(),
            self.terrain.cover.len()
        );
        if let Some(ipc) = &self.ipc {
            ipc.send(&format!("TERRAIN_COVER_SIMULATE_OK:{steps}"));
            ipc.send("SCENE_MODIFIED");
        }
    }

    /// 連続シミュレートを停止する（`TERRAIN_COVER_SIMULATE_STOP`）。
    pub(super) fn handle_terrain_cover_simulate_stop(&mut self) {
        self.terrain.cover_sim_running = false;
        if let Some(ipc) = &self.ipc {
            ipc.send("TERRAIN_COVER_SIMULATE_STOPPED");
            ipc.send("SCENE_MODIFIED");
        }
    }

    /// 全チャンクのカバー場を消去する（`TERRAIN_COVER_CLEAR`）。
    ///
    /// 消したチャンクは「未保存」になるので、`TERRAIN_SAVE` で
    /// ディスク上の .tcover も削除される（消したはずの雪が復活しない）。
    pub(super) fn handle_terrain_cover_clear(&mut self) {
        self.terrain.cover_sim_running = false;
        let coords: Vec<ChunkCoord> = self.terrain.cover.keys().copied().collect();
        for coord in coords {
            if let Some(field) = self.terrain.cover.get_mut(&coord) {
                if field.is_empty() {
                    continue;
                }
                field.clear();
            }
            self.terrain.cover_dirty.insert(coord);
            self.terrain.cover_pending_apply.insert(coord);
        }
        // 消去は待たせる意味が無いので、次フレームで即座に焼き直す。
        self.terrain.cover_apply_timer = COVER_APPLY_INTERVAL_SEC;
        if let Some(ipc) = &self.ipc {
            ipc.send("TERRAIN_COVER_CLEARED");
            ipc.send("SCENE_MODIFIED");
        }
    }

    // ─── ⑤ 永続化 ───────────────────────────────────────────────────────────

    /// 全チャンクのカバー場を .tcover として保存する。
    ///
    /// 【空チャンクのファイルを消す理由】
    ///   量 0 になったチャンクのファイルを残すと、次回ロード時に消したはずの雪が
    ///   復活する。`save_terrain_scatter` と同じく「空 = ファイルを消す」を
    ///   保存の不変条件とする。
    ///
    /// 戻り値は (書き出したファイル数, 削除したファイル数)。
    pub(super) fn save_terrain_cover(&mut self, dir: &std::path::Path) -> (u32, u32) {
        let mut written = 0u32;
        let mut removed = 0u32;

        for (&coord, field) in &self.terrain.cover {
            let path = dir.join(tcover_file_name(coord));
            if field.is_empty() {
                match std::fs::remove_file(&path) {
                    Ok(()) => removed += 1,
                    Err(e) if e.kind() == std::io::ErrorKind::NotFound => {}
                    Err(e) => eprintln!("[SEED terrain] tcover remove failed: {path:?} err={e}"),
                }
                continue;
            }
            let bytes = write_cover_chunk(field, coord);
            match std::fs::write(&path, &bytes) {
                Ok(()) => written += 1,
                Err(e) => eprintln!("[SEED terrain] tcover save failed: {path:?} err={e}"),
            }
        }

        self.terrain.cover_dirty.clear();
        (written, removed)
    }

    /// .tvox の隣にある .tcover を読み込んで `terrain.cover` を埋める。
    ///
    /// 【ファイルが無いのはエラーではない】
    ///   カバー機能より前に保存されたシーンには .tcover が存在しない。
    ///   欠落を「カバー無し」として扱うことで旧シーンもそのまま開ける。
    pub(super) fn load_terrain_cover(&mut self, chunk_paths: &[(ChunkCoord, String)]) {
        for (coord, tvox_path) in chunk_paths {
            let path = tcover_path_from_tvox(tvox_path);
            let Ok(bytes) = crate::engine::asset_fs::read_bytes(&path) else {
                // 未保存／旧シーン。カバー無しとして扱う（エラーではない）。
                continue;
            };
            match read_cover_chunk(&bytes) {
                Ok((field, _stored_coord)) => {
                    let has_cover = !field.is_empty();
                    self.terrain.cover.insert(*coord, field);
                    // 読み込んだカバーを頂点へ焼き直す（描画へ反映する）。
                    if has_cover {
                        self.terrain.cover_pending_apply.insert(*coord);
                    }
                }
                Err(e) => {
                    eprintln!("[SEED terrain] tcover decode failed, skip: {path} err={e:?}");
                }
            }
        }
        // ロード直後は間引きを待たずに反映する（開いた瞬間に雪が乗っている状態にする）。
        self.terrain.cover_apply_timer = COVER_APPLY_INTERVAL_SEC;
    }

    // ─── ⑥ Play 中の積算を揮発させる ────────────────────────────────────────

    /// Play 開始時に Edit のカバー場を退避する。
    ///
    /// Play 中の積算はゲーム状態であって編集データではない（水位 `sim_level_y` と
    /// まったく同じ考え方）。Stop したら Edit 時の保存状態へ戻す必要がある。
    ///
    /// メモリは 1 チャンク 2KB 強（32×32×2 バイト）なので、丸ごと複製して構わない。
    pub(super) fn snapshot_cover_for_play(&mut self) {
        self.terrain.cover_play_snapshot = Some(self.terrain.cover.clone());
        // Play 中に Edit のシミュレートが動き続けると二重に積もるので必ず止める。
        self.terrain.cover_sim_running = false;
    }

    /// Play 終了時に Edit のカバー場へ戻す。
    ///
    /// Play 中に積もったぶんは捨てられ、未保存フラグも立たない
    /// （Play しただけでシーンが変更済みになる、という挙動を避ける）。
    pub(super) fn restore_cover_after_play(&mut self) {
        let Some(snapshot) = self.terrain.cover_play_snapshot.take() else { return };
        // Play 中に触れた（または Play 前から乗っていた）チャンクは全部焼き直す。
        let mut touched: HashSet<ChunkCoord> =
            self.terrain.cover.keys().copied().collect();
        touched.extend(snapshot.keys().copied());
        self.terrain.cover = snapshot;
        self.terrain.cover_pending_apply.extend(touched);
        self.terrain.cover_apply_timer = COVER_APPLY_INTERVAL_SEC;
    }
}

// ============================================================
//  ファイル名・パスの規則（.tvox / .tscatter と同じ命名）
// ============================================================

/// チャンクの .tcover ファイル名（`chunk_X_Y_Z.tcover`）を返す。
pub(super) fn tcover_file_name(coord: ChunkCoord) -> String {
    format!("chunk_{}_{}_{}.tcover", coord.x, coord.y, coord.z)
}

/// .tvox の仮想パスから、隣に置かれた .tcover の仮想パスを導く。
///
/// ロード時は `TerrainChunkComponent::tvox_path` しか手掛かりが無い。
/// 拡張子だけを差し替えることで tvox 側のパス規則に自動で追従する
/// （`tscatter_path_from_tvox` と同じ設計）。
pub(super) fn tcover_path_from_tvox(tvox_path: &str) -> String {
    match tvox_path.strip_suffix(".tvox") {
        Some(stem) => format!("{stem}.tcover"),
        None => format!("{tvox_path}.tcover"),
    }
}

// ============================================================
//  エミッタ収集（ECS 走査。ワールド解決前の生データ）
// ============================================================

/// アクタ走査で拾ったカバーエミッタの生データ（素材 ID はまだ添字に解決していない）。
struct RawCoverEmitter {
    /// アクターのワールド位置（Region / TextureMask の中心）。
    world_pos: [f32; 3],
    range_kind: CoverEmitterRangeKind,
    extents: [f32; 3],
    fade: f32,
    mask_path: String,
    mask_size: [f32; 2],
    material_id: String,
    strength: f32,
}

/// Transform を持たないアクター（フォルダノード等）の既定位置。
const FALLBACK_ACTOR_POSITION: [f32; 3] = [0.0, 0.0, 0.0];

/// `collect_cover_emitters` の再帰実装。
///
/// `parent_active` は祖先のアクティブ状態。自身または祖先が active=false の
/// アクターからは収集しない。
fn collect_cover_in_actor(
    actor: &Actor,
    world: &crate::engine::ecs::World,
    out: &mut Vec<RawCoverEmitter>,
    parent_active: bool,
) {
    let active = parent_active && actor.active;

    if active {
        // エミッタのワールド位置（Transform はワールド空間）。
        let pos = world
            .get::<Transform>(actor.entity)
            .map(|t| t.position)
            .unwrap_or(FALLBACK_ACTOR_POSITION);

        for slot in actor.slots().iter() {
            if slot.kind != ComponentKind::CoverEmitter || !slot.enabled {
                continue;
            }
            let Some(e) = world.get::<CoverEmitterComponent>(slot.entity) else { continue };
            // コンポーネント側の有効フラグ（ゲームロジックからの一時停止）。
            if !e.enabled {
                continue;
            }
            // 強度 0 以下は何も積もらせないので、ここで落とす
            // （積算ループにも入れない＝完全に無コスト）。
            if !(e.strength > 0.0) {
                continue;
            }
            out.push(RawCoverEmitter {
                world_pos: pos,
                range_kind: e.range_kind,
                extents: e.extents,
                fade: e.fade,
                mask_path: e.mask_path.clone(),
                mask_size: e.mask_size,
                material_id: e.material_id.clone(),
                strength: e.strength,
            });
        }
    }

    for child in actor.children() {
        collect_cover_in_actor(child, world, out, active);
    }
}

// ============================================================
//  TerrainState のカバー関連フィールド（型エイリアス）
// ============================================================

/// チャンク → カバー場のマップ（`.tcover` の実体）。
pub type CoverFieldMap = HashMap<ChunkCoord, CoverField>;

// ─── ユニットテスト ──────────────────────────────────────────────────────────

#[cfg(test)]
mod tests {
    use super::*;

    /// .tcover のファイル名が .tvox / .tscatter と同じ命名規則であること。
    #[test]
    fn tcover_file_name_follows_convention() {
        assert_eq!(
            tcover_file_name(ChunkCoord::new(-1, 2, 3)),
            "chunk_-1_2_3.tcover"
        );
    }

    /// .tvox パスから .tcover パスが導けること（拡張子差し替え）。
    #[test]
    fn tcover_path_derives_from_tvox_path() {
        assert_eq!(
            tcover_path_from_tvox("assets://terrain/Scene1/chunk_0_0_0.tvox"),
            "assets://terrain/Scene1/chunk_0_0_0.tcover"
        );
        // 拡張子が想定外でも壊れない（読み込みに失敗して「カバー無し」になるだけ）。
        assert_eq!(tcover_path_from_tvox("weird"), "weird.tcover");
    }

    /// 秒数指定シミュレートのステップ数と刻み幅の決め方を固定する。
    ///
    /// ・短い指定は 60Hz 刻みそのまま（置き換えの途中経過が潰れない）
    /// ・長い指定でもステップ数は上限で頭打ち（エディタが固まらない）
    /// ・どちらの場合も `steps × step_dt` は指定秒数と一致する（合計量が変わらない）
    #[test]
    fn simulate_step_plan_is_bounded_and_conserves_total_time() {
        fn plan(seconds: f32) -> (u32, f32) {
            let total = seconds.min(COVER_SIMULATE_MAX_SECONDS);
            let steps = ((total / COVER_SIMULATE_STEP_SEC).ceil() as u32)
                .clamp(1, COVER_SIMULATE_MAX_STEPS);
            (steps, total / steps as f32)
        }

        // 5 秒 → 300 ステップ（= 60Hz 刻み）。
        let (steps, dt) = plan(5.0);
        assert_eq!(steps, 300);
        assert!((dt - COVER_SIMULATE_STEP_SEC).abs() < 1.0e-6);

        // 上限を超える指定でもステップ数は頭打ちになり、合計時間は保たれる。
        for seconds in [60.0f32, 3600.0, 1.0e9] {
            let (steps, dt) = plan(seconds);
            assert!(steps <= COVER_SIMULATE_MAX_STEPS, "ステップ数は上限以下");
            let total = seconds.min(COVER_SIMULATE_MAX_SECONDS);
            assert!(
                (steps as f32 * dt - total).abs() < 1.0e-2,
                "steps × dt が指定秒数と一致すること (seconds={seconds})"
            );
        }

        // 0 に近い指定でも 0 除算せず 1 ステップになる。
        let (steps, _) = plan(1.0e-6);
        assert_eq!(steps, 1);
    }
}
