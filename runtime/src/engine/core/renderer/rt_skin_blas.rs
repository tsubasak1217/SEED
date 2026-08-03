// ============================================================
//  rt_skin_blas.rs — スキンメッシュの RT 加速構造（Phase RT-Skin）
//
//  【解く問題】
//    本エンジンの GPU スキニングは「ジョイント行列だけ」を計算し、頂点変形は
//    頂点シェーダが毎描画で行う（skin_system.rs / shader_skinned_vertex.wgsl）。
//    そのため「変形後の頂点」がメモリ上に実体として存在せず、BLAS を作れなかった。
//    ＝スキンキャラは RT 影・RT 反射・DDGI・水面反射のどれにも映らなかった。
//
//  【方式】
//    1. skin_compute.wgsl が LOD ごとのジョイント行列（sk_jmats_lodN）を書く（既存）。
//    2. 本モジュールが「1 プリミティブ × 1 インスタンス」ごとに
//       skin_deform.wgsl を dispatch し、**変形後ローカル頂点位置**（vec4, 16B）を
//       専用 storage バッファへ書き出す。
//    3. そのバッファを頂点入力、元プリミティブのインデックスバッファをインデックス入力
//       として BLAS を毎フレーム再構築する。
//    4. TLAS インスタンス変換にはノードのワールド行列を渡す。
//       → 描画結果 `u_model.model * (skin * pos)` と RT の
//         `TLAS変換 * BLAS頂点` が厳密に一致する。
//
//  【単一責任】
//    本モジュールは「変形出力バッファ・BLAS・BindGroup の生成／キャッシュ／解放と
//    変形 compute の記録」だけを担う。TLAS への詰め込み・マスク決定・
//    bindless レコードの生成は rt_shadow.rs の責務（非スキンと共通）。
// ============================================================

use std::collections::{HashMap, HashSet};

use wgpu::{
    AccelerationStructureFlags, AccelerationStructureGeometryFlags,
    AccelerationStructureUpdateMode, BlasGeometrySizeDescriptors,
    BlasTriangleGeometrySizeDescriptor, CreateBlasDescriptor,
};

use super::gpu_resources::GpuPrimitive;
use super::pipeline::SkinDeformPipeline;
use super::skin_system::{SkinComputeSystem, MAX_JOINTS};

// ─── 定数 ────────────────────────────────────────────────────

/// RT へ載せられる「スキンプリミティブ × インスタンス」の総数上限。
///
/// 1 エントリにつき「変形後頂点バッファ（頂点数 × 16B）＋ 専用 BLAS」が VRAM に常駐し、
/// さらに毎フレーム BLAS を再構築する（＝GPU 時間も比例して増える）。無制限に許すと
/// 群衆シーンで VRAM とフレーム時間の両方が破綻するため、明示的な天井を設ける。
/// 64 は「主要キャラ数体 × 数プリミティブ」を想定した値。超過分は登録せず 1 回だけ警告する。
pub const MAX_RT_SKIN_BLAS: usize = 64;

/// 1 エントリ（プリミティブ×インスタンス）が持てる頂点数の上限。
///
/// 変形出力は 16B/頂点なので 20 万頂点 = 3.2MB/エントリ。これを超えるプリミティブは
/// 毎フレームの BLAS 再構築コストが現実的でない（TDR リスク）ため登録しない。
pub const MAX_RT_SKIN_VERTICES: u32 = 200_000;

/// 変形出力 1 頂点あたりのバイト数（`vec4<f32>` = BLAS の頂点ストライド）。
/// マジックナンバーを避けるため型サイズから導出する。skin_deform.wgsl の
/// `out_positions: array<vec4<f32>>` と対。
pub const SKIN_DEFORM_VERTEX_STRIDE: wgpu::BufferAddress =
    std::mem::size_of::<[f32; 4]>() as wgpu::BufferAddress;

/// skin_deform.wgsl の `@workgroup_size` と一致させること（ディスパッチ数の算出に使う）。
const DEFORM_WORKGROUP_SIZE: u32 = 64;

/// u32 1 ワードのバイト数（生バイト列を u32 配列として読むためのストライド換算に使う）。
const BYTES_PER_WORD: usize = std::mem::size_of::<u32>();

// ─── GPU 側 uniform ─────────────────────────────────────────

/// skin_deform.wgsl の `SkinDeformParams` と 1:1 対応する uniform。
///
/// フィールド順・型は WGSL 側の宣言と必ず一致させること。
#[repr(C)]
#[derive(Clone, Copy, PartialEq, Eq, bytemuck::Pod, bytemuck::Zeroable)]
struct SkinDeformParams {
    /// このプリミティブの頂点数。
    vertex_count:        u32,
    /// ジョイント行列配列内のこのインスタンスの先頭要素番号（compact_idx * MAX_JOINTS）。
    joint_base:          u32,
    /// `Vertex` 1 個のワード数。
    vertex_stride_words: u32,
    /// `SkinVertex` 1 個のワード数。
    skin_stride_words:   u32,
}

// ─── キー ────────────────────────────────────────────────────

/// スキン BLAS エントリを一意に識別するキー。
///
/// 非スキン BLAS（`BlasKey`）と違い **インスタンスまで含む**。同じモデルの別インスタンスは
/// ポーズが異なる＝変形後頂点が異なるため、BLAS を共有できないからである。
#[derive(Clone, PartialEq, Eq, Hash, Debug)]
pub struct SkinBlasKey {
    /// キャスターキー（frame_renderer が渡す統合バッチキー ＝ `BlasKey::source_path` と同一体系）。
    pub batch_key: String,
    pub mesh_idx:  usize,
    pub prim_idx:  usize,
    pub inst_idx:  usize,
}

impl SkinBlasKey {
    pub fn new(batch_key: &str, mesh_idx: usize, prim_idx: usize, inst_idx: usize) -> Self {
        Self { batch_key: batch_key.to_string(), mesh_idx, prim_idx, inst_idx }
    }
}

// ─── エントリ ────────────────────────────────────────────────

/// 1 エントリ（スキンプリミティブ × インスタンス）分の GPU リソース。
struct SkinBlasEntry {
    /// 変形後ローカル頂点位置（`vec4<f32>` 配列）。BLAS の頂点入力かつ compute の出力。
    out_buffer:    wgpu::Buffer,
    /// skin_deform.wgsl の uniform（毎フレーム joint_base を書き換える）。
    params_buffer: wgpu::Buffer,
    /// group0 BindGroup（params / 元頂点 / スキン属性 / 出力）。プリミティブが変わらない限り不変。
    entry_bg:      wgpu::BindGroup,
    /// このエントリ専用の BLAS（毎フレーム再構築）。
    blas:          wgpu::Blas,
    /// 頂点数（ディスパッチ数とサイズ記述子に使う）。
    vertex_count:  u32,
    /// インデックス数（サイズ記述子に使う）。
    index_count:   u32,
    /// 直近に書き込んだ params（同値なら `queue.write_buffer` を省く）。
    last_params:   SkinDeformParams,
    /// このエントリを作ったときの `GpuPrimitive::generation`。
    ///
    /// 同一キー（batch_key+mesh+prim+inst）のままモデル実体が差し替わると、
    /// `entry_bg` が掴む頂点バッファも `vertex_count` / `index_count` も古いままになる。
    /// 頂点数・インデックス数が減る差し替えでは BLAS ビルドが wgpu の検証で落ちるため、
    /// 世代不一致を検出したらエントリごと作り直す。
    prim_generation: u64,
}

/// ジョイント行列 BindGroup のキャッシュ 1 件。
struct JointBgEntry {
    /// group1 の BindGroup（`sk_jmats_lodN` を指す）。
    bg: wgpu::BindGroup,
    /// この BindGroup を作ったときの `SkinComputeSystem::generation`。
    ///
    /// 統合バッチは容量不足で **同じ batch_key のまま作り直される**（frame_renderer の
    /// 統合バッチ再生成 / terrain_scatter_ops の容量拡張）。そのとき `sk_jmats_lodN` は
    /// 別バッファになるので、世代を突き合わせないと旧バッファを読み続け、
    /// **RT 上のキャラだけが再構築時点のポーズで永久停止する**。さらに旧バッファは
    /// 「旧 visible 数を超える compact スロット」がゼロのままなので、増えたインスタンスは
    /// 零行列で変形され原点へ潰れた退化 BLAS になる。
    skin_generation: u64,
}

/// 当該フレームに変形 compute を積むエントリ 1 件分の情報。
struct PendingDispatch {
    key:        SkinBlasKey,
    /// ジョイント行列 BindGroup のキャッシュキー（バッチキー＋LOD）。
    joint_key:  (String, usize),
    /// ディスパッチするワークグループ数。
    wg_count:   u32,
}

// ─── マネージャ ──────────────────────────────────────────────

/// スキン RT 加速構造のマネージャ。`RtShadowResources` が 1 個保持する。
///
/// フレームごとの使い方（rt_shadow.rs の `prepare_and_build` が守る順序）:
///   1. `begin_frame()`
///   2. 登録したいエントリごとに `ensure_entry()`（上限超過・頂点数超過はここで弾かれる）
///   3. `prune_unused()` で当該フレームに現れなかったエントリを解放
///   4. `record_deform(encoder, pipeline)` で変形 compute を **同一 encoder** に記録
///   5. 同じ encoder で `build_acceleration_structures`（BLAS＋TLAS）
pub struct RtSkinBlasManager {
    /// エントリのキャッシュ（キー → GPU リソース）。
    entries:          HashMap<SkinBlasKey, SkinBlasEntry>,
    /// ジョイント行列 BindGroup のキャッシュ（(バッチキー, LOD) → group1 BG）。
    ///
    /// バッファ実体は `SkinComputeSystem` の `sk_jmats_lodN`（バッチ単位）なので、
    /// エントリごとではなく (バッチ, LOD) 単位で 1 個あればよい。
    /// （生成世代を一緒に保持し、バッチ再生成でバッファが入れ替わったら作り直す）
    joint_bgs:        HashMap<(String, usize), JointBgEntry>,
    /// 当該フレームに現れたキー（`prune_unused` の生存判定に使う）。
    live_keys:        HashSet<SkinBlasKey>,
    /// 当該フレームの変形 compute ディスパッチ予定。
    pending:          Vec<PendingDispatch>,
    /// 総数上限超過の警告を出したか（ログ爆発防止）。
    warned_capacity:  bool,
    /// 頂点数上限超過の警告を出したエントリ（同一キーは 1 回だけ警告）。
    warned_vertices:  HashSet<SkinBlasKey>,
    /// バッファ用途不足の警告を出したエントリ（同一キーは 1 回だけ警告）。
    warned_usage:     HashSet<SkinBlasKey>,
}

impl Default for RtSkinBlasManager {
    fn default() -> Self { Self::new() }
}

impl RtSkinBlasManager {
    /// 空のマネージャを作る（GPU リソースは遅延生成）。
    pub fn new() -> Self {
        Self {
            entries:         HashMap::new(),
            joint_bgs:       HashMap::new(),
            live_keys:       HashSet::new(),
            pending:         Vec::new(),
            warned_capacity: false,
            warned_vertices: HashSet::new(),
            warned_usage:    HashSet::new(),
        }
    }

    /// フレーム開始時に呼ぶ（生存キーとディスパッチ予定をリセットする）。
    pub fn begin_frame(&mut self) {
        self.live_keys.clear();
        self.pending.clear();
    }

    /// 解放されたバッチキーに紐づくエントリ／BindGroup を追従解放する。
    /// `RtShadowResources::prune_source_paths` から呼ばれ、非スキン BLAS と同じ寿命規則に揃える。
    pub fn prune_batch_keys(&mut self, freed_keys: &[String]) -> usize {
        if freed_keys.is_empty() { return 0; }
        let before = self.entries.len();
        self.entries.retain(|k, _| !freed_keys.iter().any(|f| k.batch_key == *f));
        self.joint_bgs.retain(|(bk, _), _| !freed_keys.iter().any(|f| bk == f));
        self.warned_vertices.retain(|k| !freed_keys.iter().any(|f| k.batch_key == *f));
        self.warned_usage.retain(|k| !freed_keys.iter().any(|f| k.batch_key == *f));
        before - self.entries.len()
    }

    /// 1 エントリを当該フレームに登録し、必要なら GPU リソースを生成する。
    ///
    /// - `batch_key` / `mesh_idx` / `prim_idx` / `inst_idx`: エントリの同定に使う。
    /// - `prim`: 元プリミティブ（頂点・スキン属性・インデックスの供給元）。
    /// - `skin`: このバッチのスキンシステム（ジョイント行列バッファの供給元）。
    /// - `lod` / `compact_idx`: ジョイント行列の位置。`joint_base = compact_idx * MAX_JOINTS`。
    ///
    /// 返り値 `true` = 登録成功（TLAS へ載せてよい）。`false` = 上限超過・用途不足等で非対象。
    #[allow(clippy::too_many_arguments)]
    pub fn ensure_entry(
        &mut self,
        device:      &wgpu::Device,
        queue:       &wgpu::Queue,
        pipeline:    &SkinDeformPipeline,
        batch_key:   &str,
        mesh_idx:    usize,
        prim_idx:    usize,
        inst_idx:    usize,
        prim:        &GpuPrimitive,
        skin:        &SkinComputeSystem,
        lod:         usize,
        compact_idx: u32,
    ) -> bool {
        let key = SkinBlasKey::new(batch_key, mesh_idx, prim_idx, inst_idx);

        // ── 事前条件: スキン属性バッファが無ければ変形できない ──────────
        let Some(skin_vb) = prim.skin_vertex_buffer.as_ref() else { return false };

        // ── 頂点数／インデックス数の制約（VRAM／ビルド時間の暴走防止）────────
        // インデックス 0 のプリミティブは三角形が 1 枚も無く BLAS を作る意味が無い
        // （index_count=0 のサイズ記述子の扱いはバックエンド依存なので手前で弾く）。
        if prim.vertex_count == 0
            || prim.index_count == 0
            || prim.vertex_count > MAX_RT_SKIN_VERTICES
        {
            if self.warned_vertices.insert(key.clone()) {
                eprintln!(
                    "[SEED RT] 警告: {} mesh#{mesh_idx} prim#{prim_idx} inst#{inst_idx} の頂点数 {} /\
                     インデックス数 {} が RT スキン BLAS の制約（頂点 1〜{MAX_RT_SKIN_VERTICES}・\
                     インデックス 1 以上）を満たしません。このプリミティブは\
                     RT（影/反射/GI/水面反射）に映りません",
                    batch_key, prim.vertex_count, prim.index_count
                );
            }
            return false;
        }

        // ── 用途検証: 変形 compute は元バッファを storage として読む ──────
        // 用途が足りないまま BindGroup を作ると wgpu の検証エラーでアプリが落ちるため、
        // 非スキン側の BLAS_INPUT 検証（rt_shadow.rs）と同じ流儀でここも防御する。
        let vb_ok  = prim.vertex_buffer.usage().contains(wgpu::BufferUsages::STORAGE);
        let svb_ok = skin_vb.usage().contains(wgpu::BufferUsages::STORAGE);
        let ib_ok  = prim.index_buffer.usage().contains(wgpu::BufferUsages::BLAS_INPUT);
        if !vb_ok || !svb_ok || !ib_ok {
            if self.warned_usage.insert(key.clone()) {
                eprintln!(
                    "[SEED RT] 警告: {} mesh#{mesh_idx} prim#{prim_idx} のバッファ用途が不足しています\
                     （vertex STORAGE={vb_ok}, skin STORAGE={svb_ok}, index BLAS_INPUT={ib_ok}）。\
                     このスキンプリミティブは RT に映りません（gpu_resources.rs の生成経路を確認）",
                    batch_key
                );
            }
            return false;
        }

        // ── 総数上限（**今フレームに受理した件数** に対して判定する）──────────
        // 【なぜ「マップの総件数」ではないのか】`entries` には前フレームまでのエントリが
        // 残っており、解放は後段の `prune_unused` で行われる。総件数で判定すると、
        // エントリ集合が総入れ替えになるフレーム（アクタ差し替え・シーン切替）で
        // 「まだ解放されていない旧エントリ」が枠を食い、新規が丸ごと弾かれて 1 フレーム
        // 点滅する。今フレームの受理数（`live_keys`）を基準にすれば旧エントリの残存に
        // 左右されず、「1 フレームに載せる最大数」という本来の意味どおりに効く。
        if self.live_keys.len() >= MAX_RT_SKIN_BLAS {
            if !self.warned_capacity {
                self.warned_capacity = true;
                eprintln!(
                    "[SEED RT] 警告: RT スキン BLAS のエントリ数が上限 {MAX_RT_SKIN_BLAS} に達しました。\
                     これ以上のスキンプリミティブ×インスタンスは RT（影/反射/GI/水面反射）に映りません\
                     （MAX_RT_SKIN_BLAS を増やすか対象を減らしてください）"
                );
            }
            return false;
        }

        // ── エントリの生成（初回、または実体が作り直されたとき）──────────
        // 世代不一致 = 同じキーのままモデル実体が差し替わった。古い頂点バッファを掴んだ
        // BindGroup と古い頂点数のまま BLAS を組むと、最悪 wgpu の検証パニックになる。
        let need_new_entry = cache_needs_rebuild(
            self.entries.get(&key).map(|e| e.prim_generation), prim.generation,
        );
        if need_new_entry {
            let entry = create_entry(device, pipeline, prim, skin_vb, &key);
            self.entries.insert(key.clone(), entry);
        }

        // ── ジョイント行列 BindGroup（(バッチ, LOD) 単位でキャッシュ）─────
        // こちらも世代で追従する。バッチ再生成では `GpuPrimitive` は据え置きのまま
        // `SkinComputeSystem` だけが新しくなる（＝2 つの世代は独立に動く）ため、
        // エントリ側とジョイント側でそれぞれ別に突き合わせる必要がある。
        let joint_key = (batch_key.to_string(), lod);
        let need_new_joint_bg = cache_needs_rebuild(
            self.joint_bgs.get(&joint_key).map(|j| j.skin_generation), skin.generation,
        );
        if need_new_joint_bg {
            let Some(jbuf) = skin.jmat_buffer(lod) else { return false };
            let bg = device.create_bind_group(&wgpu::BindGroupDescriptor {
                label:   Some("Skin Deform Joint BG"),
                layout:  &pipeline.joint_bgl,
                entries: &[wgpu::BindGroupEntry { binding: 0, resource: jbuf.as_entire_binding() }],
            });
            self.joint_bgs.insert(
                joint_key.clone(),
                JointBgEntry { bg, skin_generation: skin.generation },
            );
        }

        // ── params の更新（joint_base はポーズ／LOD で毎フレーム変わりうる）──
        let entry = self.entries.get_mut(&key).expect("直前に挿入済み");
        let params = SkinDeformParams {
            vertex_count:        entry.vertex_count,
            joint_base:          compact_idx * MAX_JOINTS as u32,
            vertex_stride_words: vertex_stride_words(),
            skin_stride_words:   skin_stride_words(),
        };
        if entry.last_params != params {
            queue.write_buffer(&entry.params_buffer, 0, bytemuck::bytes_of(&params));
            entry.last_params = params;
        }

        // ── 当該フレームの生存＋ディスパッチ予定へ登録 ─────────────────
        // ワークグループ数は「頂点数 ÷ workgroup_size」の切り上げ。
        let wg_count = entry.vertex_count.div_ceil(DEFORM_WORKGROUP_SIZE);
        self.live_keys.insert(key.clone());
        self.pending.push(PendingDispatch { key, joint_key, wg_count });
        true
    }

    /// 当該フレームに現れなかったエントリ（＝アクタ削除・LOD 外れ・上限超過）を解放する。
    /// 返り値: 解放したエントリ数。
    pub fn prune_unused(&mut self) -> usize {
        let live = &self.live_keys;
        let before = self.entries.len();
        self.entries.retain(|k, _| live.contains(k));
        // ジョイント BG は「生存エントリのバッチキー」に含まれるものだけ残す。
        let live_batches: HashSet<&String> = live.iter().map(|k| &k.batch_key).collect();
        self.joint_bgs.retain(|(bk, _), _| live_batches.contains(bk));
        before - self.entries.len()
    }

    /// 当該フレームぶんの変形 compute を encoder へ記録する。
    ///
    /// 【依存の根拠】呼び出し側（rt_shadow.rs）は
    ///   「skin compute（ジョイント行列）→ 本 compute（頂点変形）→ build_acceleration_structures」
    /// を **同一 command encoder** に、この順で積む。wgpu/WebGPU のコマンドは 1 本の
    /// キュー上で記録順に実行され、パス間には暗黙のバリアが入るため、BLAS ビルドが読む
    /// 変形出力は必ず書き込み完了後の内容になる（明示的なバリア API は不要）。
    pub fn record_deform(
        &self,
        encoder:  &mut wgpu::CommandEncoder,
        pipeline: &SkinDeformPipeline,
    ) {
        if self.pending.is_empty() { return; }
        let mut pass = encoder.begin_compute_pass(&wgpu::ComputePassDescriptor {
            label:            Some("RT Skin Deform Pass"),
            timestamp_writes: None,
        });
        pass.set_pipeline(&pipeline.pipeline);
        for p in &self.pending {
            let (Some(entry), Some(jbg)) =
                (self.entries.get(&p.key), self.joint_bgs.get(&p.joint_key)) else { continue };
            pass.set_bind_group(0, &entry.entry_bg, &[]);
            pass.set_bind_group(1, &jbg.bg, &[]);
            pass.dispatch_workgroups(p.wg_count, 1, 1);
        }
    }

    /// BLAS ビルドに必要な (BLAS, 変形後頂点バッファ, 頂点数, インデックス数) を返す。
    pub fn blas_inputs(&self, key: &SkinBlasKey)
        -> Option<(&wgpu::Blas, &wgpu::Buffer, u32, u32)>
    {
        self.entries.get(key)
            .map(|e| (&e.blas, &e.out_buffer, e.vertex_count, e.index_count))
    }
}

// ─── 内部ヘルパー ────────────────────────────────────────────

/// キャッシュしている GPU リソースを作り直すべきかを判定する（単一の判断箇所）。
///
/// - `cached`: そのキャッシュエントリを作ったときの生成世代。`None` = 未キャッシュ。
/// - `current`: 現在の実体の生成世代（`GpuPrimitive::generation` / `SkinComputeSystem::generation`）。
///
/// キーが一致していても実体が作り直されていれば世代が変わる。この関数はその
/// 「キー一致だけでは足りない」という判断を 1 箇所に集約し、エントリ側とジョイント側の
/// 両方から同じ規則で使う（片方だけ判定を忘れる、という退行を防ぐ）。
fn cache_needs_rebuild(cached: Option<u64>, current: u64) -> bool {
    match cached {
        None => true,             // 未キャッシュ: 作る
        Some(g) => g != current,  // 実体が差し替わった: 作り直す
    }
}

/// `Vertex` 1 個のワード数（u32 単位）。skin_deform.wgsl が生バイト列を u32 配列として読むため。
fn vertex_stride_words() -> u32 {
    (std::mem::size_of::<crate::engine::core::loader::model::Vertex>() / BYTES_PER_WORD) as u32
}

/// `SkinVertex` 1 個のワード数（u32 単位）。
fn skin_stride_words() -> u32 {
    (std::mem::size_of::<crate::engine::core::loader::model::SkinVertex>() / BYTES_PER_WORD) as u32
}

/// エントリ 1 件分の GPU リソース（出力バッファ・uniform・BindGroup・BLAS）を生成する。
fn create_entry(
    device:   &wgpu::Device,
    pipeline: &SkinDeformPipeline,
    prim:     &GpuPrimitive,
    skin_vb:  &wgpu::Buffer,
    key:      &SkinBlasKey,
) -> SkinBlasEntry {
    let vertex_count = prim.vertex_count;
    let index_count  = prim.index_count;

    // 変形後ローカル位置バッファ。compute の出力（STORAGE）かつ BLAS の頂点入力（BLAS_INPUT）。
    let out_buffer = device.create_buffer(&wgpu::BufferDescriptor {
        label:              Some("RT Skin Deformed Positions"),
        size:               vertex_count as u64 * SKIN_DEFORM_VERTEX_STRIDE,
        usage:              wgpu::BufferUsages::STORAGE | wgpu::BufferUsages::BLAS_INPUT,
        mapped_at_creation: false,
    });

    let params_buffer = device.create_buffer(&wgpu::BufferDescriptor {
        label:              Some("RT Skin Deform Params"),
        size:               std::mem::size_of::<SkinDeformParams>() as u64,
        usage:              wgpu::BufferUsages::UNIFORM | wgpu::BufferUsages::COPY_DST,
        mapped_at_creation: false,
    });

    let entry_bg = device.create_bind_group(&wgpu::BindGroupDescriptor {
        label:   Some("RT Skin Deform Entry BG"),
        layout:  &pipeline.entry_bgl,
        entries: &[
            wgpu::BindGroupEntry { binding: 0, resource: params_buffer.as_entire_binding() },
            wgpu::BindGroupEntry { binding: 1, resource: prim.vertex_buffer.as_entire_binding() },
            wgpu::BindGroupEntry { binding: 2, resource: skin_vb.as_entire_binding() },
            wgpu::BindGroupEntry { binding: 3, resource: out_buffer.as_entire_binding() },
        ],
    });

    // 【ビルドフラグの根拠】スキン BLAS は毎フレーム内容が変わるため、
    // トレース性能より **ビルド速度** を優先する（PREFER_FAST_BUILD）。
    // update_mode は Build（フル再構築）とした。wgpu 25 には
    // `AccelerationStructureUpdateMode::PreferUpdate`（refit）もあるが、
    //   - refit はトポロジ不変を前提に BVH を「歪める」ため、大きくポーズが変わると
    //     トレース品質が急速に劣化し、リセット（フル再構築）のタイミング管理が要る。
    //   - wgpu 25 の Blas は「同じジオメトリ記述で再ビルドすると更新になる」保証を
    //     API レベルで明示しておらず、バックエンドによる差異を検証できていない。
    // 実測で問題が出るまではフル再構築で正しさを優先する（docs に既知の制限として記載）。
    let blas = device.create_blas(
        &CreateBlasDescriptor {
            label:       Some("RT Skin BLAS"),
            flags:       AccelerationStructureFlags::PREFER_FAST_BUILD,
            update_mode: AccelerationStructureUpdateMode::Build,
        },
        BlasGeometrySizeDescriptors::Triangles {
            descriptors: vec![skin_blas_size_desc(vertex_count, index_count)],
        },
    );

    eprintln!(
        "[SEED RT] スキン BLAS 生成: {} mesh#{} prim#{} inst#{}（頂点 {vertex_count} / インデックス {index_count}）",
        key.batch_key, key.mesh_idx, key.prim_idx, key.inst_idx
    );

    SkinBlasEntry {
        out_buffer,
        params_buffer,
        entry_bg,
        blas,
        vertex_count,
        index_count,
        // 番兵: 実際の params と必ず異なる値を入れ、初回は必ず書き込ませる。
        last_params: SkinDeformParams {
            vertex_count:        u32::MAX,
            joint_base:          u32::MAX,
            vertex_stride_words: u32::MAX,
            skin_stride_words:   u32::MAX,
        },
        // 生成元プリミティブの世代を記録し、実体差し替えを検出できるようにする。
        prim_generation: prim.generation,
    }
}

/// スキン BLAS のサイズ記述子。
///
/// 頂点フォーマットは Float32x3（変形出力 vec4 の先頭 12B を読む）、インデックスは Uint32。
/// OPAQUE フラグは非スキン（rt_shadow.rs の `blas_size_desc`）と同じ理由で常に立てる
/// （アルファテストはインスタンスマスクで扱う）。
pub fn skin_blas_size_desc(vertex_count: u32, index_count: u32)
    -> BlasTriangleGeometrySizeDescriptor
{
    BlasTriangleGeometrySizeDescriptor {
        vertex_format: wgpu::VertexFormat::Float32x3,
        vertex_count,
        index_format:  Some(wgpu::IndexFormat::Uint32),
        index_count:   Some(index_count),
        flags:         AccelerationStructureGeometryFlags::OPAQUE,
    }
}

// ============================================================
//  テスト
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;

    /// WGSL ソースから `const NAME: u32 = <値>u;` の右辺を u32 として取り出す。
    /// 定数の値をシェーダ側の 1 箇所に保ち、Rust 側は「読み取って一致を検証する」だけにする。
    /// （rt_shadow.rs / clustered.rs の同名ヘルパーと同じ流儀。テスト専用のため各所で独立に持つ）
    fn wgsl_const_u32(src: &str, name: &str) -> u32 {
        let decl = src
            .lines()
            .map(str::trim)
            .find(|l| l.starts_with(&format!("const {name}")))
            .unwrap_or_else(|| panic!("WGSL に const {name} の宣言が見つかりません"));
        let rhs = decl
            .split('=')
            .nth(1)
            .unwrap_or_else(|| panic!("const {name} の宣言に '=' がありません"))
            .trim()
            .trim_end_matches(';')
            .trim()
            .trim_end_matches('u'); // WGSL の u32 サフィックス
        if let Some(hex) = rhs.strip_prefix("0x").or_else(|| rhs.strip_prefix("0X")) {
            u32::from_str_radix(hex, 16)
                .unwrap_or_else(|_| panic!("const {name} が u32 の 16 進として解釈できません"))
        } else {
            rhs.parse::<u32>()
                .unwrap_or_else(|_| panic!("const {name} が u32 として解釈できません"))
        }
    }

    /// skin_deform.wgsl の `MAX_JOINTS` が Rust の `skin_system::MAX_JOINTS` と一致すること。
    ///
    /// ズレると `joint_base` の刻み幅が食い違い、**別インスタンスのポーズで変形される**
    /// （キャラが破裂する／別人の動きをする）。コンパイルでも実行時エラーでも検出できない
    /// 無言の破綻なので、rt_shadow.rs の `wgsl_cull_mask_matches_rust_mask` と同じ流儀で
    /// WGSL ソースを直接パースして担保する。
    #[test]
    fn wgsl_max_joints_matches_rust() {
        let src = include_str!("shaders/skin_deform.wgsl");
        let v = wgsl_const_u32(src, "MAX_JOINTS");
        assert_eq!(
            v, MAX_JOINTS as u32,
            "skin_deform.wgsl の MAX_JOINTS({v}) と Rust の skin_system::MAX_JOINTS({}) が一致しません",
            MAX_JOINTS
        );

        // 頂点シェーダ側とも一致すること（3 者が同じ値である必要がある）。
        let vs = include_str!("shaders/shader_skinned_vertex.wgsl");
        let vv = wgsl_const_u32(vs, "MAX_JOINTS");
        assert_eq!(vv, v, "shader_skinned_vertex.wgsl と skin_deform.wgsl の MAX_JOINTS が一致しません");
    }

    /// skin_deform.wgsl の `@workgroup_size` の元になる定数が Rust 側のディスパッチ数算出と
    /// 一致すること。ズレると末尾頂点が変形されない（BLAS に bind pose の破片が残る）。
    #[test]
    fn wgsl_workgroup_size_matches_rust() {
        let src = include_str!("shaders/skin_deform.wgsl");
        let v = wgsl_const_u32(src, "WORKGROUP_SIZE");
        assert_eq!(
            v, DEFORM_WORKGROUP_SIZE,
            "skin_deform.wgsl の WORKGROUP_SIZE({v}) と Rust の DEFORM_WORKGROUP_SIZE({DEFORM_WORKGROUP_SIZE}) が一致しません"
        );
        // 実際に `@compute @workgroup_size(WORKGROUP_SIZE)` として使われていること
        // （定数だけ合っていてリテラル直書きに戻されていたら意味が無い）。
        assert!(
            src.contains("@workgroup_size(WORKGROUP_SIZE)"),
            "skin_deform.wgsl が @workgroup_size(WORKGROUP_SIZE) を使っていません"
        );
    }

    /// skin_deform.wgsl が naga で parse + validate を通ること。
    /// RT スキンは RT 対応 GPU の実行時にしかパイプラインが作られないため、
    /// WGSL の構文・型エラーは cargo build では検出できない。ここで静的に担保する。
    #[test]
    fn skin_deform_wgsl_validates() {
        let src = include_str!("shaders/skin_deform.wgsl");
        let module = naga::front::wgsl::parse_str(src)
            .expect("skin_deform.wgsl の parse に失敗しました");
        let mut validator = naga::valid::Validator::new(
            naga::valid::ValidationFlags::all(),
            naga::valid::Capabilities::default(),
        );
        validator.validate(&module)
            .expect("skin_deform.wgsl の validate に失敗しました");
    }

    /// WGSL の u16 アンパック（`w & 0xFFFF` / `w >> 16`）が Rust の `[u16; 4]` の
    /// メモリ配置と一致することを、bytemuck による実バイト列で検証する。
    ///
    /// リトルエンディアン前提で「1 ワード目の下位 = joints[0]、上位 = joints[1]」が成り立つ。
    /// この対応が崩れるとボーン割り当てが入れ替わり、キャラが破裂する。
    #[test]
    fn u16_joint_unpack_matches_layout() {
        use crate::engine::core::loader::model::SkinVertex;
        const U16_MASK:  u32 = 0xFFFF;
        const U16_SHIFT: u32 = 16;

        let sv = SkinVertex { joints: [1, 2, 3, 4], weights: [0.1, 0.2, 0.3, 0.4] };
        let bytes: &[u8] = bytemuck::bytes_of(&sv);
        let words: &[u32] = bytemuck::cast_slice(bytes);

        // WGSL と同じ式で復元する。
        let j = [
            words[0] & U16_MASK,
            words[0] >> U16_SHIFT,
            words[1] & U16_MASK,
            words[1] >> U16_SHIFT,
        ];
        assert_eq!(j, [1, 2, 3, 4], "u16 joints のアンパック結果が元の値と一致しません");

        // weights は joints の直後 2 ワード目以降（WGSL の JOINT_PACKED_WORDS = 2 と対）。
        let w = [
            f32::from_bits(words[2]), f32::from_bits(words[3]),
            f32::from_bits(words[4]), f32::from_bits(words[5]),
        ];
        assert_eq!(w, sv.weights, "weights のワードオフセットが JOINT_PACKED_WORDS=2 と一致しません");

        // ストライド換算（Rust ヘルパー）が実サイズと一致すること。
        assert_eq!(skin_stride_words() as usize, words.len(),
            "skin_stride_words() が SkinVertex の実ワード数と一致しません");
        assert_eq!(
            vertex_stride_words() as usize,
            std::mem::size_of::<crate::engine::core::loader::model::Vertex>() / 4,
            "vertex_stride_words() が Vertex の実ワード数と一致しません"
        );
    }

    /// 生成世代によるキャッシュ無効化の規則を固定する。
    ///
    /// これは「同じ batch_key のままバッチ／モデル実体が作り直される」経路
    /// （frame_renderer の統合バッチ再生成・terrain_scatter_ops の容量拡張・モデル差し替え）
    /// への追従そのものであり、崩れると
    ///   - RT 上のキャラだけが再構築時点のポーズで永久停止する
    ///   - 旧バッファの未初期化スロットを読んで頂点が原点へ潰れる
    ///   - 頂点数が減る差し替えで BLAS ビルドが wgpu の検証パニックになる
    /// という無言の破綻になる。実行時エラーでもコンパイルでも検出できないため、
    /// 判断の規則自体をここで固定する。
    #[test]
    fn cache_invalidation_follows_generation() {
        // 未キャッシュは必ず作る。
        assert!(cache_needs_rebuild(None, 1), "未キャッシュなら生成が必要");
        // 世代が同じなら再利用する（毎フレーム作り直すと確保コストが跳ねる）。
        assert!(!cache_needs_rebuild(Some(7), 7), "世代一致なら再利用するべき");
        // 世代が変わっていれば必ず作り直す（実体が差し替わっている）。
        assert!(cache_needs_rebuild(Some(7), 8), "世代不一致なら作り直すべき");
        // 世代が「戻る」ことは採番の性質上ないが、不一致は一律で作り直す（安全側）。
        assert!(cache_needs_rebuild(Some(8), 7), "不一致は方向に関わらず作り直すべき");
    }

    /// 生成世代の採番が単調増加かつ一意であること。
    /// 同じ値が 2 回出るとキャッシュ無効化が空振りし、上のテストが守る規則ごと無意味になる。
    #[test]
    fn gpu_generation_is_unique_and_monotonic() {
        use crate::engine::core::renderer::gpu_resources::next_gpu_generation;
        const SAMPLES: usize = 64;
        let mut prev = next_gpu_generation();
        // 0 は「未設定」の番兵として空けてある。
        assert_ne!(prev, 0, "生成世代に 0 が使われています（番兵と衝突します）");
        for _ in 0..SAMPLES {
            let cur = next_gpu_generation();
            assert!(cur > prev, "生成世代が単調増加していません: {prev} -> {cur}");
            prev = cur;
        }
    }

    /// 変形出力のストライドが `Float32x3` を読むのに十分な 16B であること
    /// （BLAS の `vertex_stride` と出力バッファのサイズ計算がここに依存する）。
    #[test]
    fn deform_vertex_stride_is_vec4() {
        assert_eq!(SKIN_DEFORM_VERTEX_STRIDE, 16,
            "変形出力のストライドは vec4<f32> = 16B である必要があります");
    }

    /// **実 GPU**: 変形 compute パイプライン生成 → ダミープリミティブ 1 個の変形 →
    /// BLAS＋TLAS のビルドがエラー無く通ること。
    ///
    /// バインドのステージ可視性や BLAS の用途検証は wgpu が実デバイス上でしか行わないため、
    /// ここだけは実 GPU を要する。RT 機能非対応のアダプタではスキップする。
    ///
    /// 実行: `cargo test rt_skin_blas::tests::skin_deform_builds_blas_on_gpu -- --ignored --nocapture`
    #[test]
    #[ignore = "実 GPU が必要。--ignored で実行する"]
    fn skin_deform_builds_blas_on_gpu() {
        use wgpu::util::DeviceExt;
        use crate::engine::core::loader::model::{SkinVertex, Vertex};

        let instance = wgpu::Instance::new(&wgpu::InstanceDescriptor::default());
        let Ok(adapter) = pollster::block_on(instance.request_adapter(
            &wgpu::RequestAdapterOptions::default())) else {
            eprintln!("[rt_skin_blas] GPU アダプタが見つからないため検証をスキップ");
            return;
        };
        // RT 加速構造＋レイクエリの両方が要る。無ければスキップ（非対応 GPU では機能自体が無効）。
        let need = wgpu::Features::EXPERIMENTAL_RAY_QUERY
            | wgpu::Features::EXPERIMENTAL_RAY_TRACING_ACCELERATION_STRUCTURE;
        if !adapter.features().contains(need) {
            eprintln!("[rt_skin_blas] アダプタが RT 機能に非対応のため検証をスキップ");
            return;
        }
        let Ok((device, queue)) = pollster::block_on(adapter.request_device(
            &wgpu::DeviceDescriptor { required_features: need, ..Default::default() })) else {
            eprintln!("[rt_skin_blas] RT 機能付きデバイス生成に失敗したため検証をスキップ");
            return;
        };

        // ── パイプライン生成（ステージ可視性の検証を兼ねる）──────────
        let pipeline = SkinDeformPipeline::new(&device, None);

        // ── ダミー: 三角形 1 枚（3 頂点）＋ジョイント 1 本 ────────────
        const TRI_VERTS:   u32 = 3;
        const TRI_INDICES: u32 = 3;
        let verts = vec![Vertex::default(); TRI_VERTS as usize];
        let skins = vec![
            SkinVertex { joints: [0; 4], weights: [1.0, 0.0, 0.0, 0.0] };
            TRI_VERTS as usize
        ];
        let indices: Vec<u32> = (0..TRI_INDICES).collect();

        let vb = device.create_buffer_init(&wgpu::util::BufferInitDescriptor {
            label: Some("test vb"), contents: bytemuck::cast_slice(&verts),
            usage: wgpu::BufferUsages::VERTEX | wgpu::BufferUsages::STORAGE
                 | wgpu::BufferUsages::BLAS_INPUT | wgpu::BufferUsages::COPY_DST,
        });
        let svb = device.create_buffer_init(&wgpu::util::BufferInitDescriptor {
            label: Some("test svb"), contents: bytemuck::cast_slice(&skins),
            usage: wgpu::BufferUsages::VERTEX | wgpu::BufferUsages::STORAGE,
        });
        let ib = device.create_buffer_init(&wgpu::util::BufferInitDescriptor {
            label: Some("test ib"), contents: bytemuck::cast_slice(&indices),
            usage: wgpu::BufferUsages::INDEX | wgpu::BufferUsages::BLAS_INPUT,
        });
        // 単位行列 1 本ぶんのジョイント行列（MAX_JOINTS 本ぶん確保する）。
        let jmats = vec![[[1.0f32, 0.0, 0.0, 0.0], [0.0, 1.0, 0.0, 0.0],
                          [0.0, 0.0, 1.0, 0.0], [0.0, 0.0, 0.0, 1.0]]; MAX_JOINTS];
        let jbuf = device.create_buffer_init(&wgpu::util::BufferInitDescriptor {
            label: Some("test jmats"), contents: bytemuck::cast_slice(&jmats),
            usage: wgpu::BufferUsages::STORAGE,
        });

        // ── 出力／uniform／BindGroup ───────────────────────────────
        let out_buf = device.create_buffer(&wgpu::BufferDescriptor {
            label: Some("test out"),
            size: TRI_VERTS as u64 * SKIN_DEFORM_VERTEX_STRIDE,
            usage: wgpu::BufferUsages::STORAGE | wgpu::BufferUsages::BLAS_INPUT,
            mapped_at_creation: false,
        });
        let params = SkinDeformParams {
            vertex_count: TRI_VERTS, joint_base: 0,
            vertex_stride_words: vertex_stride_words(),
            skin_stride_words:   skin_stride_words(),
        };
        let pbuf = device.create_buffer_init(&wgpu::util::BufferInitDescriptor {
            label: Some("test params"), contents: bytemuck::bytes_of(&params),
            usage: wgpu::BufferUsages::UNIFORM | wgpu::BufferUsages::COPY_DST,
        });
        let entry_bg = device.create_bind_group(&wgpu::BindGroupDescriptor {
            label: Some("test entry bg"), layout: &pipeline.entry_bgl,
            entries: &[
                wgpu::BindGroupEntry { binding: 0, resource: pbuf.as_entire_binding() },
                wgpu::BindGroupEntry { binding: 1, resource: vb.as_entire_binding() },
                wgpu::BindGroupEntry { binding: 2, resource: svb.as_entire_binding() },
                wgpu::BindGroupEntry { binding: 3, resource: out_buf.as_entire_binding() },
            ],
        });
        let joint_bg = device.create_bind_group(&wgpu::BindGroupDescriptor {
            label: Some("test joint bg"), layout: &pipeline.joint_bgl,
            entries: &[wgpu::BindGroupEntry { binding: 0, resource: jbuf.as_entire_binding() }],
        });

        // ── BLAS / TLAS ────────────────────────────────────────────
        let size_desc = skin_blas_size_desc(TRI_VERTS, TRI_INDICES);
        let blas = device.create_blas(
            &CreateBlasDescriptor {
                label: Some("test skin blas"),
                flags: AccelerationStructureFlags::PREFER_FAST_BUILD,
                update_mode: AccelerationStructureUpdateMode::Build,
            },
            BlasGeometrySizeDescriptors::Triangles { descriptors: vec![size_desc.clone()] },
        );
        let tlas = device.create_tlas(&wgpu::CreateTlasDescriptor {
            label: Some("test skin tlas"), max_instances: 1,
            flags: AccelerationStructureFlags::PREFER_FAST_BUILD,
            update_mode: AccelerationStructureUpdateMode::Build,
        });
        let mut package = wgpu::TlasPackage::new(tlas);

        // ── 記録: 変形 compute → AS ビルド（同一 encoder・この順）──────
        let mut encoder = device.create_command_encoder(&wgpu::CommandEncoderDescriptor {
            label: Some("test encoder"),
        });
        {
            let mut pass = encoder.begin_compute_pass(&wgpu::ComputePassDescriptor {
                label: Some("test deform pass"), timestamp_writes: None,
            });
            pass.set_pipeline(&pipeline.pipeline);
            pass.set_bind_group(0, &entry_bg, &[]);
            pass.set_bind_group(1, &joint_bg, &[]);
            pass.dispatch_workgroups(TRI_VERTS.div_ceil(DEFORM_WORKGROUP_SIZE), 1, 1);
        }
        // 恒等変換（3x4 行優先）。
        let xf: [f32; 12] = [1.0, 0.0, 0.0, 0.0,
                             0.0, 1.0, 0.0, 0.0,
                             0.0, 0.0, 1.0, 0.0];
        package.get_mut_slice(0..1).expect("TLAS スライス")[0] =
            Some(wgpu::TlasInstance::new(&blas, xf, 0, 0xFF));

        encoder.build_acceleration_structures(
            std::iter::once(&wgpu::BlasBuildEntry {
                blas: &blas,
                geometry: wgpu::BlasGeometries::TriangleGeometries(vec![
                    wgpu::BlasTriangleGeometry {
                        size:                    &size_desc,
                        vertex_buffer:           &out_buf,
                        first_vertex:            0,
                        vertex_stride:           SKIN_DEFORM_VERTEX_STRIDE,
                        index_buffer:            Some(&ib),
                        first_index:             Some(0),
                        transform_buffer:        None,
                        transform_buffer_offset: None,
                    },
                ]),
            }),
            std::iter::once(&package),
        );
        queue.submit(std::iter::once(encoder.finish()));
        let _ = device.poll(wgpu::PollType::Wait);
    }
}
