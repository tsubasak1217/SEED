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
//    3. **1 スキンメッシュノードインスタンス（＝キャラ 1 体の 1 メッシュノード）につき
//       BLAS を 1 個**作り、そのノードに属する全プリミティブを
//       `BlasGeometries::TriangleGeometries` の **ジオメトリ配列**として登録する。
//       BLAS を毎フレーム再構築する。
//    4. TLAS インスタンス変換にはノードのワールド行列を渡す。
//       → 描画結果 `u_model.model * (skin * pos)` と RT の
//         `TLAS変換 * BLAS頂点` が厳密に一致する。
//
//  【なぜ「プリミティブ単位」ではなく「インスタンス単位」の BLAS なのか】
//    以前は (batch_key, mesh, prim, inst) を 1 エントリ＝1 BLAS としていた。
//    glTF のスキンモデルはマテリアル分割で 1 メッシュが数十プリミティブに割れるのが普通で
//    （BrainStem は 1 メッシュ 59 プリミティブ）、20 体並べると 1180 エントリになり、
//    上限 `MAX_RT_SKIN_BLAS` を「体の途中で」使い切る。結果、
//    **先頭の 1 体だけが TLAS に載り、その 1 体にだけ RTAO と RT 影の自己遮蔽が乗って
//    暗く見える**（他の体は RT から完全に消える）という不整合が起きていた。
//    1 インスタンス = 1 BLAS に統合すると、
//      - エントリ消費が「1 体 = 1 件」になり、上限が体数の意味を持つ
//      - 上限到達時に「体の途中で切れる」部分受理が構造的に起きない
//      - BLAS 本数が 59×N → N に減り、VRAM とビルド時間の両方が改善する
//    という 3 点が同時に得られる。
//
//  【グルーピングの単位は「メッシュノード」であって「メッシュ」ではない】
//    1 つの BLAS は TLAS インスタンス変換を 1 個しか持てない。同じ mesh_idx を
//    参照する **別ノード**（＝別ワールド変換）を 1 つの BLAS に混ぜると、片方の変換で
//    もう片方まで動いてしまう。よってキーは `(batch_key, node_idx, inst_idx)` とする。
//
//  【単一責任】
//    本モジュールは「変形出力バッファ・BLAS・BindGroup の生成／キャッシュ／解放と
//    変形 compute の記録」だけを担う。TLAS への詰め込み・マスク決定・
//    bindless レコードの生成は rt_shadow.rs の責務（非スキンと共通）。
// ============================================================

use std::collections::hash_map::DefaultHasher;
use std::collections::{HashMap, HashSet};
use std::hash::{Hash, Hasher};

use wgpu::{
    AccelerationStructureFlags, AccelerationStructureGeometryFlags,
    AccelerationStructureUpdateMode, BlasGeometrySizeDescriptors,
    BlasTriangleGeometrySizeDescriptor, CreateBlasDescriptor,
};

use super::gpu_resources::GpuPrimitive;
use super::pipeline::SkinDeformPipeline;
use super::skin_system::{SkinComputeSystem, MAX_JOINTS};

// ─── 定数 ────────────────────────────────────────────────────

/// RT へ載せられる「スキンメッシュノード × インスタンス」の総数上限。
///
/// **単位は「1 体（1 メッシュノードインスタンス）= 1 件」**である（プリミティブ数には依存しない）。
/// 1 エントリにつき「そのノードの全プリミティブぶんの変形後頂点バッファ（頂点数 × 16B）＋
/// 専用 BLAS 1 個」が VRAM に常駐し、さらに毎フレーム BLAS を再構築する
/// （＝GPU 時間も比例して増える）。無制限に許すと群衆シーンで VRAM とフレーム時間の
/// 両方が破綻するため、明示的な天井を設ける。
/// 64 は「主要キャラ 64 体」を想定した値。超過分は登録せず 1 回だけ警告する。
///
/// 【部分受理は起きない】判定は 1 体を丸ごと受け入れるか丸ごと弾くかの二択であり、
/// 「体の途中でプリミティブが切れる」ことは構造的に起こらない
/// （テスト `capacity_rejects_whole_instances_only` が固定する）。
pub const MAX_RT_SKIN_BLAS: usize = 64;

/// 1 プリミティブが持てる頂点数の上限。
///
/// 変形出力は 16B/頂点なので 20 万頂点 = 3.2MB/プリミティブ。これを超えるプリミティブは
/// 毎フレームの BLAS 再構築コストが現実的でない（TDR リスク）ため登録しない。
pub const MAX_RT_SKIN_VERTICES: u32 = 200_000;

/// 1 エントリ（＝1 BLAS）が持てるジオメトリ数の上限。
///
/// wgpu / 各バックエンドは 1 BLAS のジオメトリ数に上限を持つ（Vulkan の
/// `maxGeometryCount` は実装依存だが 2^24 程度と十分大きい）。ここでの上限は
/// ハード制約ではなく「1 体あたりのビルドコストとレコード消費の天井」であり、
/// 想定外のモデル（数千プリミティブ）が 1 フレームのビルドを爆発させるのを防ぐ。
/// 256 は「マテリアル分割された実用キャラ（BrainStem = 59）」に十分な余裕を持つ値。
pub const MAX_RT_SKIN_GEOMETRIES: usize = 256;

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
///
/// 粒度は **メッシュノード**（プリミティブ単位ではない）。1 ノードの全プリミティブが
/// 1 つの BLAS へジオメトリ配列として入る。ノード単位にする理由はファイル冒頭の
/// 「グルーピングの単位は……」を参照（1 BLAS = 1 ワールド変換）。
#[derive(Clone, PartialEq, Eq, Hash, Debug)]
pub struct SkinBlasKey {
    /// キャスターキー（frame_renderer が渡す統合バッチキー ＝ `BlasKey::source_path` と同一体系）。
    pub batch_key: String,
    /// メッシュノード番号（`NodePrimDraw::node_idx`）。ワールド変換の単位。
    pub node_idx:  usize,
    /// バッチ内のインスタンス番号。
    pub inst_idx:  usize,
    /// 呼び出し側が定める「同一 BLAS へまとめてよい分類」。
    ///
    /// 本モジュールは値の意味を解釈しない（単一責任）。rt_shadow.rs は
    /// **TLAS インスタンスマスク**（不透明 0x01 / 非不透明 0x02）を渡す。
    /// マスクは TLAS インスタンス単位の属性なので、異なるマスクのプリミティブを
    /// 1 つの BLAS へ混ぜると「半透明部位が不透明の影を落とす」等の退行になる。
    /// 分類が違えば別 BLAS（別 TLAS インスタンス）に分ける、という規約をキーで表す。
    pub class_id:  u32,
}

impl SkinBlasKey {
    pub fn new(batch_key: &str, node_idx: usize, inst_idx: usize, class_id: u32) -> Self {
        Self { batch_key: batch_key.to_string(), node_idx, inst_idx, class_id }
    }
}

// ─── 入力（呼び出し側が渡すプリミティブ列）──────────────────────

/// `ensure_entry` へ渡す「このメッシュノードを構成するプリミティブ 1 個」。
///
/// 呼び出し側（rt_shadow.rs）が `InstancedModelBatch::rt_enumerate_skinned` の
/// グループ列挙から組み立てる。順序がそのまま **BLAS のジオメトリ順** になり、
/// さらに bindless レコードの並び順にもなるため、呼び出しごとに順序を変えてはならない。
pub struct SkinPrimInput<'a> {
    pub mesh_idx: usize,
    pub prim_idx: usize,
    pub prim:     &'a GpuPrimitive,
}

// ─── エントリ ────────────────────────────────────────────────

/// 1 プリミティブ（＝BLAS 内の 1 ジオメトリ）分の GPU リソース。
pub struct SkinPrimSlot {
    /// このスロットが由来するメッシュ番号（呼び出し側がマテリアルを引くのに使う）。
    pub mesh_idx:     usize,
    /// このスロットが由来するプリミティブ番号（同上）。
    pub prim_idx:     usize,
    /// 変形後ローカル頂点位置（`vec4<f32>` 配列）。BLAS の頂点入力かつ compute の出力。
    pub out_buffer:   wgpu::Buffer,
    /// 頂点数（ディスパッチ数とサイズ記述子に使う）。
    pub vertex_count: u32,
    /// インデックス数（サイズ記述子に使う）。
    pub index_count:  u32,
    /// skin_deform.wgsl の uniform（毎フレーム joint_base を書き換える）。
    params_buffer:    wgpu::Buffer,
    /// group0 BindGroup（params / 元頂点 / スキン属性 / 出力）。プリミティブが変わらない限り不変。
    entry_bg:         wgpu::BindGroup,
    /// 直近に書き込んだ params（同値なら `queue.write_buffer` を省く）。
    last_params:      SkinDeformParams,
}

/// 1 エントリ（スキンメッシュノード × インスタンス）分の GPU リソース。
struct SkinBlasEntry {
    /// このエントリ専用の BLAS（毎フレーム再構築）。`slots` と同順のジオメトリ配列を持つ。
    blas:  wgpu::Blas,
    /// ジオメトリ順のプリミティブスロット。**この順序が BLAS の geometry_index と一致する**。
    slots: Vec<SkinPrimSlot>,
    /// このエントリを作ったときの「構成シグネチャ」。
    ///
    /// 同一キー（batch_key+node+inst）のままモデル実体が差し替わると、各スロットの
    /// `entry_bg` が掴む頂点バッファも `vertex_count` / `index_count` も古いままになる。
    /// 頂点数・インデックス数が減る差し替えでは BLAS ビルドが wgpu の検証で落ち、
    /// プリミティブ数が変わればジオメトリ配列の形そのものが変わる。
    /// 構成（プリミティブ列＋各実体世代＋頂点/インデックス数）をハッシュで畳み込み、
    /// 不一致を検出したらエントリごと作り直す。
    layout_sig: u64,
    /// **最後に BLAS を構築できたときの**ポーズ署名。`None` = 一度も構築していない。
    /// これが今フレームの署名と一致するなら、変形 compute も BLAS 再構築も省ける。
    pose_sig_built: Option<SkinPoseSignature>,
    /// 今フレームの `ensure_entry` が算出したポーズ署名（`mark_built` で `pose_sig_built` へ確定）。
    pose_sig_current: SkinPoseSignature,
}

// ─── ポーズ署名（per-actor の BLAS 再構築スキップ）──────────────

/// 1 スキンエントリの「変形後頂点の中身」を一意に決める入力一式。
///
/// スキン変形の出力は
///   （元頂点＋スキン属性）×（ジョイント行列 `jmats[joint_base..]`）
/// で決まり、ジョイント行列は再生時刻から毎フレーム同じ計算で作られる。
/// `SkinComputeSystem` はモデルの**全アニメ**を焼き込んでおり、どのアニメをどの時刻で
/// どれだけ混ぜるかは `pose_bits`（再生指定そのもの）が完全に表す。
/// したがってここに挙げた 5 つが一致すれば、変形後頂点は 1 ビット違わず同一になる。
///
/// - `layout_sig`: エントリを構成する **全プリミティブ**（頂点・スキン属性・インデックス）の
///   実体世代と形状を畳み込んだハッシュ。BLAS 統合により 1 エントリが複数プリミティブを
///   持つため、単一の `prim_generation` ではなく結合ハッシュを署名要素にする。
/// - `skin_generation`: ジョイント行列バッファを持つスキンシステムの実体世代。
/// - `lod` / `compact_idx`: どのジョイント行列スロットを読むか（`joint_base` の決定要素）。
/// - `pose_bits`: 再生指定（フェード元アニメ index/時刻・現在アニメ index/時刻・ブレンド率）の
///   ビット表現（`SkinAnimPose::sig_bits`。Animator 非駆動は番兵値＝静止）。
///   **ブレンド状態まで含めるのが要点**で、これが無いとクロスフェード中に
///   「行列もマテリアルも変わらないのにポーズだけ変わる」フレームで署名が固定され、
///   RT 上のキャラだけが混合途中のポーズで止まる。
#[derive(Clone, Copy, PartialEq, Eq, Debug, Default)]
pub struct SkinPoseSignature {
    pub layout_sig:      u64,
    pub skin_generation: u64,
    pub lod:             usize,
    pub compact_idx:     u32,
    pub pose_bits:       [u32; 5],
}

/// このエントリの変形 compute ＋ BLAS 再構築が必要かを判定する純関数。
///
/// `built` は「最後に BLAS を構築できたときの署名」（未構築は `None`）。
/// 未構築なら必ず再構築する（構築していない BLAS を TLAS へ登録すると wgpu の検証で落ちる）。
fn pose_needs_rebuild(built: Option<SkinPoseSignature>, current: SkinPoseSignature) -> bool {
    built != Some(current)
}

/// `ensure_entry` の結果。TLAS へ載せてよいか／再構築が要るかを分けて返す。
#[derive(Clone, Copy, PartialEq, Eq, Debug)]
pub enum SkinEntryStatus {
    /// 対象外（頂点数制約・用途不足・件数上限）。TLAS へ載せてはいけない。
    Rejected,
    /// 登録済み。ポーズが前回構築時と完全一致するため、変形 compute も BLAS 再構築も省く。
    /// 前フレームの変形後頂点バッファと BLAS がそのまま正しい（両者は必ず同時に据え置かれる）。
    Unchanged,
    /// 登録済み。初回、またはポーズ／実体が変わったので変形＋BLAS 再構築が要る。
    NeedsRebuild,
}

impl SkinEntryStatus {
    /// TLAS へ載せてよいか（`Rejected` 以外）。
    pub fn accepted(self) -> bool { self != SkinEntryStatus::Rejected }
    /// このフレームに BLAS を構築し直す必要があるか。
    pub fn needs_rebuild(self) -> bool { self == SkinEntryStatus::NeedsRebuild }
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
    key:       SkinBlasKey,
    /// ジョイント行列 BindGroup のキャッシュキー（バッチキー＋LOD）。
    joint_key: (String, usize),
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
    /// 頂点数上限超過の警告を出したプリミティブ（同一プリミティブは 1 回だけ警告）。
    warned_vertices:  HashSet<(String, usize, usize)>,
    /// バッファ用途不足の警告を出したプリミティブ（同一プリミティブは 1 回だけ警告）。
    warned_usage:     HashSet<(String, usize, usize)>,
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

    /// 「1 体ぶん（＝`groups` 個のエントリ）をまとめて受け入れられるか」を事前に問う。
    ///
    /// 【なぜ事前予約が要るか】1 体は分類（インスタンスマスク）ごとに複数エントリへ
    /// 割れることがある。1 件ずつ上限判定すると「不透明部位だけ通って半透明部位が
    /// 弾かれる」＝**体の途中で切れる部分受理**が起きうる。呼び出し側は 1 体を
    /// 登録する前に本メソッドで枠を確認し、足りなければその体を丸ごと見送る。
    /// これにより「体の一部だけが RT に映る」不整合が構造的に起こらない。
    pub fn can_accept_group(&self, groups: usize) -> bool {
        capacity_allows_group(self.live_keys.len(), groups)
    }

    /// 解放されたバッチキーに紐づくエントリ／BindGroup を追従解放する。
    /// `RtShadowResources::prune_source_paths` から呼ばれ、非スキン BLAS と同じ寿命規則に揃える。
    pub fn prune_batch_keys(&mut self, freed_keys: &[String]) -> usize {
        if freed_keys.is_empty() { return 0; }
        let before = self.entries.len();
        self.entries.retain(|k, _| !freed_keys.iter().any(|f| k.batch_key == *f));
        self.joint_bgs.retain(|(bk, _), _| !freed_keys.iter().any(|f| bk == f));
        self.warned_vertices.retain(|(bk, _, _)| !freed_keys.iter().any(|f| bk == f));
        self.warned_usage.retain(|(bk, _, _)| !freed_keys.iter().any(|f| bk == f));
        before - self.entries.len()
    }

    /// 1 エントリ（＝スキンメッシュノード 1 インスタンス）を当該フレームに登録し、
    /// 必要なら GPU リソースを生成する。
    ///
    /// - `batch_key` / `node_idx` / `inst_idx`: エントリの同定に使う。
    /// - `prims`: このノードを構成するプリミティブ列。**この順序が BLAS のジオメトリ順**になる。
    /// - `skin`: このバッチのスキンシステム（ジョイント行列バッファの供給元）。
    /// - `lod` / `compact_idx`: ジョイント行列の位置。`joint_base = compact_idx * MAX_JOINTS`。
    /// - `pose_bits`: 再生指定（アニメ index・時刻・ブレンド率）のビット表現
    ///   （ポーズ署名の要素。呼び出し元が `skin_system::pose_sig_bits` で算出する）。
    ///
    /// 返り値は `SkinEntryStatus`。`Rejected` 以外なら TLAS へ載せてよく、
    /// `NeedsRebuild` のときだけ変形 compute を積み BLAS を構築し直す必要がある。
    ///
    /// 【部分受理をしない】制約を満たさないプリミティブは個別に除外されるが、
    /// **件数上限による拒否は 1 体まるごと**である（枠が足りなければ 1 件も登録しない）。
    /// これにより「体の途中で切れて一部だけ RT に映る」不整合が起こらない。
    #[allow(clippy::too_many_arguments)]
    pub fn ensure_entry(
        &mut self,
        device:      &wgpu::Device,
        queue:       &wgpu::Queue,
        pipeline:    &SkinDeformPipeline,
        batch_key:   &str,
        node_idx:    usize,
        inst_idx:    usize,
        class_id:    u32,
        prims:       &[SkinPrimInput<'_>],
        skin:        &SkinComputeSystem,
        lod:         usize,
        compact_idx: u32,
        pose_bits:   [u32; 5],
    ) -> SkinEntryStatus {
        let key = SkinBlasKey::new(batch_key, node_idx, inst_idx, class_id);

        // ── 1. プリミティブごとの適格性判定（BLAS へ入れられるものだけ残す）──
        // ここで弾かれるのは「そもそも BLAS を作れない／作る意味が無い」プリミティブだけで、
        // 残ったプリミティブは 1 つの BLAS のジオメトリ配列としてまとめて扱う。
        let mut accepted: Vec<&SkinPrimInput<'_>> = Vec::with_capacity(prims.len());
        for p in prims {
            if self.prim_is_eligible(batch_key, p) {
                accepted.push(p);
            }
        }
        if accepted.is_empty() { return SkinEntryStatus::Rejected; }
        // ジオメトリ数の天井（1 体あたりのビルドコストとレコード消費の暴走防止）。
        // 切り捨ては「その体の一部が RT に映らない」という目に見える縮退なので、
        // 黙って落とさず 1 回だけ警告する（同一 (batch, node) につき 1 回）。
        if accepted.len() > MAX_RT_SKIN_GEOMETRIES {
            // prim_idx に番兵 usize::MAX を使い、プリミティブ単位の警告キーと衝突させない
            // （実在の prim_idx が usize::MAX になることはない）。
            let wk = (batch_key.to_string(), node_idx, usize::MAX);
            if self.warned_vertices.insert(wk) {
                eprintln!(
                    "[SEED RT] 警告: {batch_key} node#{node_idx} のスキンプリミティブ数 {} が\
                     1 BLAS あたりの上限 {MAX_RT_SKIN_GEOMETRIES} を超えています。\
                     超過分は RT（影/反射/GI/水面反射）に映りません",
                    accepted.len()
                );
            }
            accepted.truncate(MAX_RT_SKIN_GEOMETRIES);
        }

        // ── 2. 総数上限（**今フレームに受理した件数** に対して判定する）──────────
        // 【なぜ「マップの総件数」ではないのか】`entries` には前フレームまでのエントリが
        // 残っており、解放は後段の `prune_unused` で行われる。総件数で判定すると、
        // エントリ集合が総入れ替えになるフレーム（アクタ差し替え・シーン切替）で
        // 「まだ解放されていない旧エントリ」が枠を食い、新規が丸ごと弾かれて 1 フレーム
        // 点滅する。今フレームの受理数（`live_keys`）を基準にすれば旧エントリの残存に
        // 左右されず、「1 フレームに載せる最大数」という本来の意味どおりに効く。
        //
        // 【単位】1 体 = 1 件。プリミティブ数に依存しないので、上限に達しても
        // 「体の途中で切れる」ことはない。
        if !capacity_allows(self.live_keys.len(), self.live_keys.contains(&key)) {
            if !self.warned_capacity {
                self.warned_capacity = true;
                eprintln!(
                    "[SEED RT] 警告: RT スキン BLAS のエントリ数が上限 {MAX_RT_SKIN_BLAS} 体に達しました。\
                     これ以上のスキンメッシュ（1 体 = 1 エントリ）は RT（影/反射/GI/水面反射）に映りません\
                     （MAX_RT_SKIN_BLAS を増やすか対象を減らしてください）"
                );
            }
            return SkinEntryStatus::Rejected;
        }

        // ── 3. エントリの生成（初回、または構成が変わったとき）──────────
        // 構成シグネチャ不一致 = 同じキーのままモデル実体が差し替わった／プリミティブ構成が
        // 変わった。古い頂点バッファを掴んだ BindGroup と古い形状のまま BLAS を組むと、
        // 最悪 wgpu の検証パニックになる。
        let layout_sig = compute_layout_sig(&accepted);
        let need_new_entry = cache_needs_rebuild(
            self.entries.get(&key).map(|e| e.layout_sig), layout_sig,
        );
        if need_new_entry {
            let entry = create_entry(device, pipeline, &accepted, layout_sig, &key);
            self.entries.insert(key.clone(), entry);
        }

        // ── 4. ジョイント行列 BindGroup（(バッチ, LOD) 単位でキャッシュ）─────
        // こちらも世代で追従する。バッチ再生成では `GpuPrimitive` は据え置きのまま
        // `SkinComputeSystem` だけが新しくなる（＝2 つの世代は独立に動く）ため、
        // エントリ側とジョイント側でそれぞれ別に突き合わせる必要がある。
        let joint_key = (batch_key.to_string(), lod);
        let need_new_joint_bg = cache_needs_rebuild(
            self.joint_bgs.get(&joint_key).map(|j| j.skin_generation), skin.generation,
        );
        if need_new_joint_bg {
            let Some(jbuf) = skin.jmat_buffer(lod) else { return SkinEntryStatus::Rejected };
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

        // ── 5. params の更新（joint_base はポーズ／LOD で毎フレーム変わりうる）──
        let entry = self.entries.get_mut(&key).expect("直前に挿入済み");
        let joint_base = compact_idx * MAX_JOINTS as u32;
        for slot in entry.slots.iter_mut() {
            let params = SkinDeformParams {
                vertex_count:        slot.vertex_count,
                joint_base,
                vertex_stride_words: vertex_stride_words(),
                skin_stride_words:   skin_stride_words(),
            };
            if slot.last_params != params {
                queue.write_buffer(&slot.params_buffer, 0, bytemuck::bytes_of(&params));
                slot.last_params = params;
            }
        }

        // ── 6. ポーズ署名の突き合わせ（per-actor の再構築スキップ）─────────
        // 変形後頂点を決める入力が前回構築時と 1 ビット違わなければ、変形 compute も
        // BLAS も前フレームのものがそのまま正しい。アニメ停止中・ポーズ静止中の
        // アクタはここで毎フレームの変形＋BLAS 再構築を丸ごと省ける。
        // 【重要】変形出力バッファと BLAS は**必ず同時に**据え置かれる（両方スキップ、
        // または両方更新）。片方だけ更新すると RT 上でメッシュが壊れる。
        let sig = SkinPoseSignature {
            layout_sig,
            skin_generation: skin.generation,
            lod,
            compact_idx,
            pose_bits,
        };
        entry.pose_sig_current = sig;
        let needs_rebuild = pose_needs_rebuild(entry.pose_sig_built, sig);

        // ── 7. 当該フレームの生存へ登録（スキップしても生存扱い＝prune させない）──
        self.live_keys.insert(key.clone());
        if !needs_rebuild {
            return SkinEntryStatus::Unchanged;
        }
        self.pending.push(PendingDispatch { key, joint_key });
        SkinEntryStatus::NeedsRebuild
    }

    /// プリミティブ 1 個が BLAS ジオメトリとして適格かを判定する（不適格なら 1 回だけ警告）。
    ///
    /// 判定内容は 2 つ:
    ///   - 頂点数／インデックス数の制約（VRAM とビルド時間の暴走防止）
    ///   - 変形 compute と BLAS が要求するバッファ用途（STORAGE / BLAS_INPUT）
    /// 用途が足りないまま BindGroup を作ると wgpu の検証エラーでアプリが落ちるため、
    /// 非スキン側の BLAS_INPUT 検証（rt_shadow.rs）と同じ流儀でここも防御する。
    fn prim_is_eligible(&mut self, batch_key: &str, p: &SkinPrimInput<'_>) -> bool {
        let prim = p.prim;
        // スキン属性バッファが無ければ変形できない。
        let Some(skin_vb) = prim.skin_vertex_buffer.as_ref() else { return false };

        // インデックス 0 のプリミティブは三角形が 1 枚も無く BLAS を作る意味が無い
        // （index_count=0 のサイズ記述子の扱いはバックエンド依存なので手前で弾く）。
        if prim.vertex_count == 0
            || prim.index_count == 0
            || prim.vertex_count > MAX_RT_SKIN_VERTICES
        {
            let wk = (batch_key.to_string(), p.mesh_idx, p.prim_idx);
            if self.warned_vertices.insert(wk) {
                eprintln!(
                    "[SEED RT] 警告: {batch_key} mesh#{} prim#{} の頂点数 {} / インデックス数 {} が\
                     RT スキン BLAS の制約（頂点 1〜{MAX_RT_SKIN_VERTICES}・インデックス 1 以上）を\
                     満たしません。このプリミティブは RT（影/反射/GI/水面反射）に映りません",
                    p.mesh_idx, p.prim_idx, prim.vertex_count, prim.index_count
                );
            }
            return false;
        }

        let vb_ok  = prim.vertex_buffer.usage().contains(wgpu::BufferUsages::STORAGE);
        let svb_ok = skin_vb.usage().contains(wgpu::BufferUsages::STORAGE);
        let ib_ok  = prim.index_buffer.usage().contains(wgpu::BufferUsages::BLAS_INPUT);
        if !vb_ok || !svb_ok || !ib_ok {
            let wk = (batch_key.to_string(), p.mesh_idx, p.prim_idx);
            if self.warned_usage.insert(wk) {
                eprintln!(
                    "[SEED RT] 警告: {batch_key} mesh#{} prim#{} のバッファ用途が不足しています\
                     （vertex STORAGE={vb_ok}, skin STORAGE={svb_ok}, index BLAS_INPUT={ib_ok}）。\
                     このスキンプリミティブは RT に映りません（gpu_resources.rs の生成経路を確認）",
                    p.mesh_idx, p.prim_idx
                );
            }
            return false;
        }
        true
    }

    /// BLAS を実際に構築したエントリの署名を「構築済み」として確定させる。
    ///
    /// `ensure_entry` が `NeedsRebuild` を返しても、呼び出し側の最終防御
    /// （インデックスバッファの用途検証など）でビルド対象から外れることがある。
    /// その場合に署名を確定させてしまうと、次フレームで「構築していない BLAS」を
    /// 静止扱いで TLAS へ登録し wgpu の検証で落ちる。そこで確定は
    /// **実際にビルドへ積んだキー**を渡してもらう本メソッドで行う。
    pub fn mark_built(&mut self, keys: &[SkinBlasKey]) {
        for k in keys {
            if let Some(e) = self.entries.get_mut(k) {
                e.pose_sig_built = Some(e.pose_sig_current);
            }
        }
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
            pass.set_bind_group(1, &jbg.bg, &[]);
            // 1 エントリ（体）の全プリミティブを続けてディスパッチする。
            // ワークグループ数は「頂点数 ÷ workgroup_size」の切り上げ。
            for slot in &entry.slots {
                pass.set_bind_group(0, &slot.entry_bg, &[]);
                pass.dispatch_workgroups(slot.vertex_count.div_ceil(DEFORM_WORKGROUP_SIZE), 1, 1);
            }
        }
    }

    /// BLAS ビルドに必要な (BLAS, ジオメトリ順のプリミティブスロット) を返す。
    ///
    /// スロットの並びがそのまま BLAS のジオメトリ順（＝シェーダが読む `geometry_index`）であり、
    /// 呼び出し側はこの順序で bindless レコードを連続配置しなければならない。
    pub fn blas_geometries(&self, key: &SkinBlasKey)
        -> Option<(&wgpu::Blas, &[SkinPrimSlot])>
    {
        self.entries.get(key).map(|e| (&e.blas, e.slots.as_slice()))
    }
}

// ─── 内部ヘルパー ────────────────────────────────────────────

/// キャッシュしている GPU リソースを作り直すべきかを判定する（単一の判断箇所）。
///
/// - `cached`: そのキャッシュエントリを作ったときの生成世代／構成シグネチャ。`None` = 未キャッシュ。
/// - `current`: 現在の実体の生成世代／構成シグネチャ。
///
/// キーが一致していても実体が作り直されていれば値が変わる。この関数はその
/// 「キー一致だけでは足りない」という判断を 1 箇所に集約し、エントリ側とジョイント側の
/// 両方から同じ規則で使う（片方だけ判定を忘れる、という退行を防ぐ）。
fn cache_needs_rebuild(cached: Option<u64>, current: u64) -> bool {
    match cached {
        None => true,             // 未キャッシュ: 作る
        Some(g) => g != current,  // 実体が差し替わった: 作り直す
    }
}

/// 件数上限を新規エントリに対して許可するかを判定する純関数（単一の判断箇所）。
///
/// - `live_len`: 今フレームにすでに受理したエントリ数（＝体数）。
/// - `already_live`: このキーが今フレームすでに受理済みか（再登録は枠を消費しない）。
///
/// 【この関数を切り出す理由】上限判定は「1 体 = 1 件」という本修正の核心であり、
/// GPU デバイス無しで検証できる形にしておかないと、退行（プリミティブ単位への逆戻り）を
/// テストで縛れない。`ensure_entry` は 1 体につき 1 回だけ本関数を呼ぶので、
/// プリミティブ数がいくつでも消費は 1 件であり、部分受理は構造的に起こらない。
fn capacity_allows(live_len: usize, already_live: bool) -> bool {
    already_live || capacity_allows_group(live_len, 1)
}

/// 「まとめて `groups` 件のエントリを受け入れられるか」を判定する純関数。
/// 1 体が分類（マスク）ごとに複数エントリへ割れるときの **原子的な予約**に使う。
fn capacity_allows_group(live_len: usize, groups: usize) -> bool {
    live_len + groups <= MAX_RT_SKIN_BLAS
}

/// エントリの構成シグネチャを計算する。
///
/// 畳み込む要素は「プリミティブ列の順序・同定（mesh/prim）・実体世代・形状（頂点/インデックス数）」。
/// これが変われば BindGroup も BLAS のジオメトリ配列も作り直す必要がある。
/// ポーズ署名の `layout_sig` としても使い、実体差し替えで必ず BLAS を再構築させる。
fn compute_layout_sig(accepted: &[&SkinPrimInput<'_>]) -> u64 {
    let mut h = DefaultHasher::new();
    // 件数を先に混ぜ、連結の曖昧さ（[a],[b] と [a,b] の衝突）を消す。
    accepted.len().hash(&mut h);
    for p in accepted {
        p.mesh_idx.hash(&mut h);
        p.prim_idx.hash(&mut h);
        p.prim.generation.hash(&mut h);
        p.prim.vertex_count.hash(&mut h);
        p.prim.index_count.hash(&mut h);
    }
    h.finish()
}

/// `Vertex` 1 個のワード数（u32 単位）。skin_deform.wgsl が生バイト列を u32 配列として読むため。
fn vertex_stride_words() -> u32 {
    (std::mem::size_of::<crate::engine::core::loader::model::Vertex>() / BYTES_PER_WORD) as u32
}

/// `SkinVertex` 1 個のワード数（u32 単位）。
fn skin_stride_words() -> u32 {
    (std::mem::size_of::<crate::engine::core::loader::model::SkinVertex>() / BYTES_PER_WORD) as u32
}

/// プリミティブ 1 個分のスロット（出力バッファ・uniform・BindGroup）を生成する。
fn create_slot(
    device:   &wgpu::Device,
    pipeline: &SkinDeformPipeline,
    p:        &SkinPrimInput<'_>,
) -> SkinPrimSlot {
    let prim = p.prim;
    let skin_vb = prim.skin_vertex_buffer.as_ref()
        .expect("prim_is_eligible がスキン属性バッファの存在を保証している");
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

    SkinPrimSlot {
        mesh_idx: p.mesh_idx,
        prim_idx: p.prim_idx,
        out_buffer,
        vertex_count,
        index_count,
        params_buffer,
        entry_bg,
        // 番兵: 実際の params と必ず異なる値を入れ、初回は必ず書き込ませる。
        last_params: SkinDeformParams {
            vertex_count:        u32::MAX,
            joint_base:          u32::MAX,
            vertex_stride_words: u32::MAX,
            skin_stride_words:   u32::MAX,
        },
    }
}

/// エントリ 1 件分（＝1 体 1 メッシュノード）の GPU リソースを生成する。
///
/// BLAS は **プリミティブ数ぶんのジオメトリ配列**を持つ 1 個だけ作る。
fn create_entry(
    device:     &wgpu::Device,
    pipeline:   &SkinDeformPipeline,
    accepted:   &[&SkinPrimInput<'_>],
    layout_sig: u64,
    key:        &SkinBlasKey,
) -> SkinBlasEntry {
    let slots: Vec<SkinPrimSlot> = accepted.iter()
        .map(|p| create_slot(device, pipeline, p))
        .collect();

    // 【ビルドフラグの根拠】スキン BLAS は毎フレーム内容が変わるため、
    // トレース性能より **ビルド速度** を優先する（PREFER_FAST_BUILD）。
    // update_mode は Build（フル再構築）とした。wgpu 25 には
    // `AccelerationStructureUpdateMode::PreferUpdate`（refit）もあるが、
    //   - refit はトポロジ不変を前提に BVH を「歪める」ため、大きくポーズが変わると
    //     トレース品質が急速に劣化し、リセット（フル再構築）のタイミング管理が要る。
    //   - wgpu 25 の Blas は「同じジオメトリ記述で再ビルドすると更新になる」保証を
    //     API レベルで明示しておらず、バックエンドによる差異を検証できていない。
    // 実測で問題が出るまではフル再構築で正しさを優先する（docs に既知の制限として記載）。
    let descriptors: Vec<BlasTriangleGeometrySizeDescriptor> = slots.iter()
        .map(|s| skin_blas_size_desc(s.vertex_count, s.index_count))
        .collect();
    let blas = device.create_blas(
        &CreateBlasDescriptor {
            label:       Some("RT Skin BLAS"),
            flags:       AccelerationStructureFlags::PREFER_FAST_BUILD,
            update_mode: AccelerationStructureUpdateMode::Build,
        },
        BlasGeometrySizeDescriptors::Triangles { descriptors },
    );

    let total_verts: u64 = slots.iter().map(|s| s.vertex_count as u64).sum();
    eprintln!(
        "[SEED RT] スキン BLAS 生成: {} node#{} inst#{}（ジオメトリ {} 個 / 合計頂点 {}）",
        key.batch_key, key.node_idx, key.inst_idx, slots.len(), total_verts
    );

    SkinBlasEntry {
        blas,
        slots,
        layout_sig,
        // 新規エントリの BLAS は未構築。初回は必ず変形＋構築を通す。
        pose_sig_built:   None,
        pose_sig_current: SkinPoseSignature::default(),
    }
}

/// スキン BLAS のジオメトリ 1 個ぶんのサイズ記述子。
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

    /// 構成シグネチャによるキャッシュ無効化の規則を固定する。
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
        // 値が同じなら再利用する（毎フレーム作り直すと確保コストが跳ねる）。
        assert!(!cache_needs_rebuild(Some(7), 7), "一致なら再利用するべき");
        // 値が変わっていれば必ず作り直す（実体が差し替わっている）。
        assert!(cache_needs_rebuild(Some(7), 8), "不一致なら作り直すべき");
        // 世代が「戻る」ことは採番の性質上ないが、不一致は一律で作り直す（安全側）。
        assert!(cache_needs_rebuild(Some(8), 7), "不一致は方向に関わらず作り直すべき");
    }

    /// ポーズ署名による per-actor スキップ判定（`pose_needs_rebuild`）の両方向。
    ///
    /// 「入力不変 → スキップ」「入力変化 → 再構築」がどちらも成立しないと、
    /// 前者を落とせば最適化が効かず、後者を落とせば RT 上でポーズが固まる。
    #[test]
    fn pose_skip_follows_all_inputs() {
        let base = SkinPoseSignature {
            layout_sig:      10,
            skin_generation: 20,
            lod:             1,
            compact_idx:     3,
            pose_bits:       [0, 0.5f32.to_bits(), 1, 0.25f32.to_bits(), 1.0f32.to_bits()],
        };
        // 未構築（初回）は必ず構築する。構築していない BLAS を TLAS へ載せると落ちるため。
        assert!(pose_needs_rebuild(None, base), "未構築なら必ず再構築");
        // 完全一致 ⇒ 変形も BLAS 再構築も省く。
        assert!(!pose_needs_rebuild(Some(base), base), "入力不変ならスキップできる");

        // 各要素を単独で変えたら、必ず再構築へ倒れること。
        let mut anim = base;   anim.pose_bits[3] = 0.75f32.to_bits();   // 再生時刻が進んだ
        let mut blend = base;  blend.pose_bits[4] = 0.5f32.to_bits();    // クロスフェードの weight が進んだ
        let mut src = base;    src.pose_bits[0] = 7;                     // フェード元アニメが変わった
        let mut prim = base;   prim.layout_sig += 1;                    // モデル実体／構成の差し替え
        let mut skin = base;   skin.skin_generation += 1;               // スキンシステム再生成
        let mut lod  = base;   lod.lod += 1;                            // LOD が切り替わった
        let mut idx  = base;   idx.compact_idx += 1;                    // ジョイント行列スロット移動
        for changed in [anim, blend, src, prim, skin, lod, idx] {
            assert!(
                pose_needs_rebuild(Some(base), changed),
                "入力が変われば再構築が必要: {changed:?}"
            );
        }
    }

    /// `SkinEntryStatus` の意味付け（TLAS へ載せてよいか／再構築が要るか）。
    #[test]
    fn skin_entry_status_semantics() {
        assert!(!SkinEntryStatus::Rejected.accepted(),     "非対象は TLAS へ載せない");
        assert!(!SkinEntryStatus::Rejected.needs_rebuild(), "非対象はビルドもしない");
        assert!(SkinEntryStatus::Unchanged.accepted(),      "静止エントリも TLAS へは載せる");
        assert!(!SkinEntryStatus::Unchanged.needs_rebuild(), "静止エントリはビルドを省く");
        assert!(SkinEntryStatus::NeedsRebuild.accepted(),    "更新エントリは TLAS へ載せる");
        assert!(SkinEntryStatus::NeedsRebuild.needs_rebuild(), "更新エントリはビルドする");
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

    /// キーの粒度が「メッシュノード × インスタンス」であること（プリミティブを含まない）。
    ///
    /// これが崩れる（prim_idx が復活する）と、1 体が 59 エントリを消費して
    /// 上限 64 を体の途中で使い切る退行（オリジナル 1 体だけが暗くなる症状）が再発する。
    #[test]
    fn key_granularity_is_node_instance() {
        const CLASS_OPAQUE: u32 = 0x01;
        const CLASS_BLEND:  u32 = 0x02;
        let a = SkinBlasKey::new("batch", 3, 7, CLASS_OPAQUE);
        let b = SkinBlasKey::new("batch", 3, 7, CLASS_OPAQUE);
        assert_eq!(a, b, "同じ (batch, node, inst, class) は同一キーであること");
        // ノードが違えば別キー（別ワールド変換 = 別 BLAS でなければならない）。
        assert_ne!(a, SkinBlasKey::new("batch", 4, 7, CLASS_OPAQUE), "ノードが違えば別エントリ");
        // インスタンスが違えば別キー（ポーズが違う = 頂点が違う）。
        assert_ne!(a, SkinBlasKey::new("batch", 3, 8, CLASS_OPAQUE), "インスタンスが違えば別エントリ");
        // 分類（インスタンスマスク）が違えば別キー（マスクは TLAS インスタンス単位の属性）。
        assert_ne!(a, SkinBlasKey::new("batch", 3, 7, CLASS_BLEND), "分類が違えば別エントリ");
    }

    /// 1 体が複数分類（不透明＋半透明）へ割れるときの **原子的な予約**。
    ///
    /// 1 件ずつ上限判定すると「不透明部位だけ載って半透明部位が弾かれる」部分受理が
    /// 起きうる。呼び出し側は体ごとに `can_accept_group(分類数)` で予約するため、
    /// 枠が足りない体は 1 件も登録されない。
    #[test]
    fn group_reservation_is_atomic() {
        // 残り 1 枠しかないとき、2 分類の体は丸ごと見送られる。
        assert!(!capacity_allows_group(MAX_RT_SKIN_BLAS - 1, 2),
            "残り 1 枠で 2 分類の体を部分受理してはならない");
        // ちょうど収まるなら受け入れる。
        assert!(capacity_allows_group(MAX_RT_SKIN_BLAS - 2, 2), "ちょうど収まる体は受け入れる");
        assert!(capacity_allows_group(0, MAX_RT_SKIN_BLAS), "空なら上限ちょうどまで入る");
        assert!(!capacity_allows_group(0, MAX_RT_SKIN_BLAS + 1), "上限を 1 件でも超えたら拒否");
    }

    /// 構成シグネチャが「プリミティブ列の順序と件数」を区別すること。
    ///
    /// ジオメトリ順は bindless レコードの並び順そのものなので、順序が変わったのに
    /// エントリを作り直さないと `geometry_index` とレコードの対応が入れ替わり、
    /// 別マテリアルのテクスチャで影・反射が着色される。
    #[test]
    fn layout_sig_distinguishes_order_and_count() {
        // GpuPrimitive の実体を作らずにシグネチャだけ検証するため、ハッシュ入力を直接組む
        // ヘルパーと同じ規則をここで再現する（compute_layout_sig の入力と 1:1）。
        fn sig(items: &[(usize, usize, u64, u32, u32)]) -> u64 {
            let mut h = DefaultHasher::new();
            items.len().hash(&mut h);
            for (m, p, g, vc, ic) in items {
                m.hash(&mut h); p.hash(&mut h); g.hash(&mut h);
                vc.hash(&mut h); ic.hash(&mut h);
            }
            h.finish()
        }
        let a = sig(&[(0, 0, 1, 10, 30), (0, 1, 1, 20, 60)]);
        let b = sig(&[(0, 1, 1, 20, 60), (0, 0, 1, 10, 30)]); // 順序違い
        let c = sig(&[(0, 0, 1, 10, 30)]);                    // 件数違い
        let d = sig(&[(0, 0, 2, 10, 30), (0, 1, 1, 20, 60)]); // 世代違い
        assert_ne!(a, b, "ジオメトリ順が変われば構成シグネチャも変わること");
        assert_ne!(a, c, "プリミティブ数が変われば構成シグネチャも変わること");
        assert_ne!(a, d, "実体世代が変われば構成シグネチャも変わること");
        assert_eq!(a, sig(&[(0, 0, 1, 10, 30), (0, 1, 1, 20, 60)]), "同一入力は同一シグネチャ");
    }

    /// 【本修正の中核】20 体 × 59 プリミティブ（BrainStem 相当）を登録したとき、
    /// **全 20 体が受理される**こと。
    ///
    /// 旧実装はエントリ単位が (batch, mesh, prim, inst) だったため、20×59 = 1180 件が
    /// 上限 64 を「体の途中で」使い切り、inst0 だけが全 59 件受理・inst1 は 5 件だけ・
    /// inst2 以降は全滅した。結果 TLAS にはオリジナル 1 体しか載らず、その 1 体にだけ
    /// RTAO と RT 影の自己遮蔽が乗って暗く見えた。ここでは実 GPU 無しで判定規則だけを
    /// 再現し（`ensure_entry` は 1 体につき 1 回だけ `capacity_allows` を呼ぶ）、
    /// 「1 体 = 1 件」であることを固定する。
    #[test]
    fn twenty_actors_with_many_primitives_are_all_accepted() {
        const ACTORS:     usize = 20;
        const PRIMS:      usize = 59; // BrainStem の 1 メッシュあたりプリミティブ数
        let mut live: Vec<SkinBlasKey> = Vec::new();
        let mut accepted = 0usize;

        // 列挙はインスタンス major（gpu_resources::rt_enumerate_skinned と同じ順序）。
        const CLASS_OPAQUE: u32 = 0x01; // 全プリミティブが不透明な体（BrainStem 相当）
        for inst in 0..ACTORS {
            let key = SkinBlasKey::new("batch", 0, inst, CLASS_OPAQUE);
            let already = live.contains(&key);
            if !capacity_allows(live.len(), already) { continue; }
            // 1 体に何プリミティブあっても、消費するのは 1 件だけ。
            let _ = PRIMS;
            if !already { live.push(key); }
            accepted += 1;
        }

        assert_eq!(
            accepted, ACTORS,
            "20 体 × {PRIMS} プリミティブでも全 {ACTORS} 体が受理されること\
             （プリミティブ単位に戻ると 1 体しか載らない退行が再発する）"
        );
        assert_eq!(live.len(), ACTORS, "エントリ消費は 1 体 = 1 件であること");
        assert!(
            ACTORS * PRIMS > MAX_RT_SKIN_BLAS,
            "このテストは「旧単位なら必ず上限を超える」規模で行う意味がある（{} > {MAX_RT_SKIN_BLAS}）",
            ACTORS * PRIMS
        );
    }

    /// 上限に達したときの拒否が「体まるごと」であり、体の途中で切れないこと。
    ///
    /// 上限までは全て受理され、上限を超えた体は 1 件も登録されない（部分受理が無い）。
    /// 判定が 1 体につき 1 回しか走らない構造なので、これは規則として保証される。
    #[test]
    fn capacity_rejects_whole_instances_only() {
        const OVER: usize = 10; // 上限を超えて要求する体数
        let mut live = 0usize;
        let mut results = Vec::new();
        for _ in 0..(MAX_RT_SKIN_BLAS + OVER) {
            let ok = capacity_allows(live, false);
            if ok { live += 1; }
            results.push(ok);
        }
        assert_eq!(live, MAX_RT_SKIN_BLAS, "受理数は上限ちょうどで止まること");
        assert!(results[..MAX_RT_SKIN_BLAS].iter().all(|&b| b), "上限までは全て受理");
        assert!(results[MAX_RT_SKIN_BLAS..].iter().all(|&b| !b), "超過分は全て拒否");
        // 同一キーの再登録は枠を消費しない（既に生存しているエントリは常に通す）。
        assert!(capacity_allows(MAX_RT_SKIN_BLAS, true), "受理済みキーの再登録は常に許可");
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

        // ── ダミー: 三角形 1 枚（3 頂点）＋ジョイント 1 本 を 2 ジオメトリぶん ────
        // 統合 BLAS（複数ジオメトリ）の経路を実デバイスで通すため、意図的に 2 個作る。
        const TRI_VERTS:   u32 = 3;
        const TRI_INDICES: u32 = 3;
        const GEOM_COUNT:  usize = 2;
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

        // ── 出力／uniform／BindGroup（ジオメトリごと）───────────────
        let params = SkinDeformParams {
            vertex_count: TRI_VERTS, joint_base: 0,
            vertex_stride_words: vertex_stride_words(),
            skin_stride_words:   skin_stride_words(),
        };
        let mut out_bufs = Vec::new();
        let mut entry_bgs = Vec::new();
        for g in 0..GEOM_COUNT {
            let out_buf = device.create_buffer(&wgpu::BufferDescriptor {
                label: Some("test out"),
                size: TRI_VERTS as u64 * SKIN_DEFORM_VERTEX_STRIDE,
                usage: wgpu::BufferUsages::STORAGE | wgpu::BufferUsages::BLAS_INPUT,
                mapped_at_creation: false,
            });
            let pbuf = device.create_buffer_init(&wgpu::util::BufferInitDescriptor {
                label: Some("test params"), contents: bytemuck::bytes_of(&params),
                usage: wgpu::BufferUsages::UNIFORM | wgpu::BufferUsages::COPY_DST,
            });
            let bg = device.create_bind_group(&wgpu::BindGroupDescriptor {
                label: Some("test entry bg"), layout: &pipeline.entry_bgl,
                entries: &[
                    wgpu::BindGroupEntry { binding: 0, resource: pbuf.as_entire_binding() },
                    wgpu::BindGroupEntry { binding: 1, resource: vb.as_entire_binding() },
                    wgpu::BindGroupEntry { binding: 2, resource: svb.as_entire_binding() },
                    wgpu::BindGroupEntry { binding: 3, resource: out_buf.as_entire_binding() },
                ],
            });
            let _ = g;
            out_bufs.push(out_buf);
            entry_bgs.push(bg);
        }
        let joint_bg = device.create_bind_group(&wgpu::BindGroupDescriptor {
            label: Some("test joint bg"), layout: &pipeline.joint_bgl,
            entries: &[wgpu::BindGroupEntry { binding: 0, resource: jbuf.as_entire_binding() }],
        });

        // ── BLAS（2 ジオメトリ）/ TLAS ──────────────────────────────
        let size_descs: Vec<_> = (0..GEOM_COUNT)
            .map(|_| skin_blas_size_desc(TRI_VERTS, TRI_INDICES))
            .collect();
        let blas = device.create_blas(
            &CreateBlasDescriptor {
                label: Some("test skin blas"),
                flags: AccelerationStructureFlags::PREFER_FAST_BUILD,
                update_mode: AccelerationStructureUpdateMode::Build,
            },
            BlasGeometrySizeDescriptors::Triangles { descriptors: size_descs.clone() },
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
            pass.set_bind_group(1, &joint_bg, &[]);
            for bg in &entry_bgs {
                pass.set_bind_group(0, bg, &[]);
                pass.dispatch_workgroups(TRI_VERTS.div_ceil(DEFORM_WORKGROUP_SIZE), 1, 1);
            }
        }
        // 恒等変換（3x4 行優先）。
        let xf: [f32; 12] = [1.0, 0.0, 0.0, 0.0,
                             0.0, 1.0, 0.0, 0.0,
                             0.0, 0.0, 1.0, 0.0];
        package.get_mut_slice(0..1).expect("TLAS スライス")[0] =
            Some(wgpu::TlasInstance::new(&blas, xf, 0, 0xFF));

        let geometries: Vec<wgpu::BlasTriangleGeometry> = (0..GEOM_COUNT)
            .map(|g| wgpu::BlasTriangleGeometry {
                size:                    &size_descs[g],
                vertex_buffer:           &out_bufs[g],
                first_vertex:            0,
                vertex_stride:           SKIN_DEFORM_VERTEX_STRIDE,
                index_buffer:            Some(&ib),
                first_index:             Some(0),
                transform_buffer:        None,
                transform_buffer_offset: None,
            })
            .collect();
        encoder.build_acceleration_structures(
            std::iter::once(&wgpu::BlasBuildEntry {
                blas: &blas,
                geometry: wgpu::BlasGeometries::TriangleGeometries(geometries),
            }),
            std::iter::once(&package),
        );
        queue.submit(std::iter::once(encoder.finish()));
        let _ = device.poll(wgpu::PollType::Wait);
    }
}
