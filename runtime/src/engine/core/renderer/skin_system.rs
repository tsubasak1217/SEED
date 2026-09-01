// ============================================================
//  skin_system.rs — GPU スキニング システム
//
//  SkinComputeSystem:
//    - アニメーションデータを GPU に 1 回だけアップロード
//    - フレームごとに compact_anim_times のみ更新
//    - dispatch_lod() でコンピュートシェーダを実行してジョイント行列を計算
//    - 頂点シェーダが読む joint VS BG（group 3）を per-LOD で提供
// ============================================================

use super::gpu_resources::{next_gpu_generation, NUM_LODS};
use super::pipeline::SkinComputePipeline;
use crate::engine::core::loader::model::{Animation, AnimationOutputs, Interpolation, Model};
use wgpu::util::DeviceExt;

// ============================================================
//  GPU 側データ構造
// ============================================================

/// GPU スキニング計算に渡す、1アニメーションチャンネル分の情報。
/// 対象ノード・プロパティ種別（T/R/S）・タイムスタンプ/値バッファ内のオフセットと補間方式を保持する。
///
/// 【複数アニメ対応】チャンネル列はモデル内の**全アニメーション**を連結して 1 本に詰める。
/// どの範囲がどのアニメかは `GpuAnimInfo`（アニメテーブル）が持つ。
#[repr(C)]
#[derive(Clone, Copy, Debug, PartialEq, bytemuck::Pod, bytemuck::Zeroable)]
pub struct GpuChannelInfo {
    pub target_node: u32,
    pub prop_type: u32, // 0=T, 1=R, 2=S
    pub ts_offset: u32,
    pub ts_count: u32,
    pub val_offset: u32,
    pub interp: u32, // 0=LINEAR, 1=STEP, 2=CUBICSPLINE
    pub _pad: [u32; 2],
}

/// アニメーション 1 本ぶんのテーブルエントリ（GPU 側 `anims[]`）。
///
/// 連結済みチャンネル列のどこからどれだけが自分のチャンネルかを示す。
/// `duration` はシェーダ側では使わない（時刻の正規化は CPU 側の権威時刻で完了している）が、
/// CPU 側のクランプとデバッグのために同じテーブルへ持たせる。
#[repr(C)]
#[derive(Clone, Copy, Debug, PartialEq, bytemuck::Pod, bytemuck::Zeroable)]
pub struct GpuAnimInfo {
    /// 連結チャンネル列内の開始位置
    pub chan_offset: u32,
    /// このアニメのチャンネル数
    pub chan_count: u32,
    /// クリップ長（秒）
    pub duration: f32,
    pub _pad: u32,
}

/// インスタンス 1 体ぶんの再生指定（GPU 側 `anim_samples[]`）。
///
/// クロスフェード用に 2 スロットを持つ:
///   - A = フェード元クリップ（`anim_a` / `time_a`）
///   - B = 現在クリップ      （`anim_b` / `time_b`）
///
/// 最終ポーズは `mix(pose_A, pose_B, weight)`。`weight = 1` でフェード無し（B のみ）
/// ＝従来（単一アニメ再生）と同一の結果になる。
#[repr(C)]
#[derive(Clone, Copy, Debug, PartialEq, bytemuck::Pod, bytemuck::Zeroable)]
pub struct GpuAnimSample {
    pub anim_a: u32,
    pub anim_b: u32,
    pub time_a: f32,
    pub time_b: f32,
    pub weight: f32,
    pub _pad: [u32; 3],
}

/// スキニング用コンピュートシェーダに渡す per-LOD パラメータ uniform。
/// ノード数・ジョイント数・チャンネル数・可視インスタンス数・アニメ本数を保持する。
#[repr(C)]
#[derive(Clone, Copy, bytemuck::Pod, bytemuck::Zeroable)]
pub struct GpuSkinParams {
    pub n_nodes: u32,
    pub n_joints: u32,
    /// 連結チャンネル列の総数（範囲外アクセス防止のガード用）
    pub n_channels: u32,
    pub n_visible: u32,
    /// パッキング済みアニメ本数（`anims[]` の有効長）
    pub n_anims: u32,
    pub _pad: [u32; 3],
}

// ─── 上限（超過分は警告してフォールバック）─────────────────────

/// 1 モデルあたりのジョイント数上限（超過分は切り捨て）。
pub const MAX_JOINTS: usize = 128;
/// 1 モデルあたりの GPU パッキング対象アニメ数上限。超過分は登録しない（＝再生できない）。
pub const MAX_ANIMS: usize = 64;
/// 全アニメ合計のチャンネル数上限。超えた時点で以降のアニメを打ち切る。
pub const MAX_TOTAL_CHANNELS: usize = 16384;
/// 全アニメ合計のキーフレーム（タイムスタンプ）数上限。超えた時点で以降のアニメを打ち切る。
pub const MAX_TOTAL_KEYS: usize = 1 << 21;

/// Animator 非駆動インスタンスの静止時刻（animations[0] の先頭フレームで凍結）。
pub const FROZEN_POSE_TIME: f32 = 0.0;

/// Animator 非駆動（静止）インスタンスの再生指定。
/// アニメ 0 の先頭フレームをブレンド無しで固定する。
pub const FROZEN_POSE: SkinAnimPose = SkinAnimPose {
    anim_a: 0,
    time_a: FROZEN_POSE_TIME,
    anim_b: 0,
    time_b: FROZEN_POSE_TIME,
    weight: 1.0,
};

// ============================================================
//  SkinAnimPose — インスタンスごとの再生指定（CPU 側の受け渡し型）
// ============================================================

/// インスタンス 1 体の再生指定（CPU 側）。
///
/// `ModelComponent::anim_drive` 由来の値をレンダラ経路（統合バッチ → スキンシステム →
/// RT スキン BLAS）へ運ぶ唯一の型。**ポーズを一意に決める値の全体**であることが重要で、
/// ダーティゲート（`merge_batch_gate`）と RT のポーズ署名（`rt_skin_blas`）は
/// この型の内容が変わったかどうかだけを見てスキップ判定する。
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct SkinAnimPose {
    /// フェード元アニメの index（`Model::animations`）
    pub anim_a: u32,
    /// フェード元アニメの再生時刻（秒・正規化済み）
    pub time_a: f32,
    /// 現在アニメの index（`Model::animations`）
    pub anim_b: u32,
    /// 現在アニメの再生時刻（秒・正規化済み）
    pub time_b: f32,
    /// ブレンド率（0=A のみ / 1=B のみ）。フェードしていないときは 1.0。
    pub weight: f32,
}

impl SkinAnimPose {
    /// フェード無し（単一クリップ）の再生指定を作る。
    pub fn single(anim_idx: u32, time: f32) -> Self {
        Self { anim_a: anim_idx, time_a: time, anim_b: anim_idx, time_b: time, weight: 1.0 }
    }

    /// ポーズ署名用のビット列（RT スキン BLAS / 静止スキップ判定に混ぜる）。
    ///
    /// **ブレンド状態まで含める**のが要点。ここに weight や A 側を入れないと、
    /// 「行列もマテリアルも変わらないのにフェード進行でポーズだけが変わる」フレームで
    /// 署名が不変になり、RT 上のキャラだけが古いポーズで止まる。
    pub fn sig_bits(&self) -> [u32; 5] {
        [
            self.anim_a,
            self.time_a.to_bits(),
            self.anim_b,
            self.time_b.to_bits(),
            self.weight.to_bits(),
        ]
    }
}

/// `Option<SkinAnimPose>` のポーズ署名。`None`（Animator 非駆動＝静止）は
/// 実値と衝突しない番兵（NaN ビット）を使う。
pub fn pose_sig_bits(pose: Option<SkinAnimPose>) -> [u32; 5] {
    /// f32 の NaN ビットパターン。有限の再生時刻・weight と衝突しない。
    const NONE_BITS: u32 = 0x7fc0_0000;
    match pose {
        Some(p) => p.sig_bits(),
        None => [NONE_BITS; 5],
    }
}

// ============================================================
//  アニメーションのパッキング（GPU 非依存の純ロジック）
// ============================================================

/// 全アニメーションを GPU バッファ用に連結したもの。
///
/// GPU リソースを作らないため、上限判定・オフセット計算だけを単体テストできる。
#[derive(Debug, Default, PartialEq)]
pub struct PackedAnimations {
    /// アニメテーブル（index = `Model::animations` の index。打ち切られた分は含まれない）
    pub anims: Vec<GpuAnimInfo>,
    /// 全アニメ連結済みチャンネル列
    pub channels: Vec<GpuChannelInfo>,
    pub timestamps: Vec<f32>,
    pub trans_vals: Vec<[f32; 4]>,
    pub rot_vals: Vec<[f32; 4]>,
    pub scale_vals: Vec<[f32; 4]>,
    /// 上限超過で 1 本でも打ち切ったか（診断用）
    pub truncated: bool,
}

impl PackedAnimations {
    /// パッキング済みアニメ本数（`Model::animations` の先頭からこの本数までが再生可能）。
    pub fn anim_count(&self) -> usize {
        self.anims.len()
    }

    /// 指定アニメのクリップ長（秒）。範囲外は先頭アニメ長へフォールバックする。
    pub fn duration_of(&self, idx: usize) -> f32 {
        self.anims
            .get(idx)
            .or_else(|| self.anims.first())
            .map(|a| a.duration)
            .unwrap_or(0.0)
            .max(1e-4)
    }
}

/// モデルの全アニメーションを GPU バッファ用に連結する。
///
/// 【上限とフォールバック】`MAX_ANIMS` / `MAX_TOTAL_CHANNELS` / `MAX_TOTAL_KEYS` の
/// いずれかを超えるアニメは**登録せず打ち切る**（既に詰めたアニメはそのまま再生できる）。
/// 打ち切られた index を指す再生指定は GPU 側で index 0 へクランプされる
/// （＝先頭アニメで代替再生。ポーズが消えて破綻するより安全）。打ち切り時は警告を出す。
pub fn pack_animations(animations: &[Animation], model_label: &str) -> PackedAnimations {
    let mut out = PackedAnimations::default();

    let n_src = animations.len();
    let n_take = n_src.min(MAX_ANIMS);
    if n_src > MAX_ANIMS {
        out.truncated = true;
        eprintln!(
            "[SEED skin] {model_label}: アニメ数 {n_src} が上限 {MAX_ANIMS} を超えるため、先頭 {MAX_ANIMS} 本のみ GPU へ登録します。"
        );
    }

    for anim in animations.iter().take(n_take) {
        // このアニメを入れると上限を超えるなら、ここで打ち切る（半端な部分登録はしない）。
        let add_channels = anim.channels.len();
        let add_keys: usize = anim.channels.iter().map(|c| c.sampler.timestamps.len()).sum();
        if out.channels.len() + add_channels > MAX_TOTAL_CHANNELS
            || out.timestamps.len() + add_keys > MAX_TOTAL_KEYS
        {
            out.truncated = true;
            eprintln!(
                "[SEED skin] {model_label}: チャンネル/キー総数が上限（{MAX_TOTAL_CHANNELS} ch / {MAX_TOTAL_KEYS} key）に達したため、アニメ '{}' 以降を GPU へ登録しません（先頭 {} 本のみ再生可能）。",
                anim.name,
                out.anims.len()
            );
            break;
        }

        let chan_offset = out.channels.len() as u32;
        for ch in &anim.channels {
            let s = &ch.sampler;
            let ts_offset = out.timestamps.len() as u32;
            let ts_count = s.timestamps.len() as u32;

            let interp = match s.interpolation {
                Interpolation::Linear => 0u32,
                Interpolation::Step => 1u32,
                Interpolation::CubicSpline => 2u32,
            };

            let (prop_type, val_offset) = match &s.outputs {
                AnimationOutputs::Translations(v) => {
                    let off = out.trans_vals.len() as u32;
                    for t in v {
                        out.trans_vals.push([t[0], t[1], t[2], 0.0]);
                    }
                    (0u32, off)
                }
                AnimationOutputs::Rotations(v) => {
                    let off = out.rot_vals.len() as u32;
                    for r in v {
                        out.rot_vals.push(*r);
                    }
                    (1u32, off)
                }
                AnimationOutputs::Scales(v) => {
                    let off = out.scale_vals.len() as u32;
                    for sc in v {
                        out.scale_vals.push([sc[0], sc[1], sc[2], 1.0]);
                    }
                    (2u32, off)
                }
                // モーフターゲットは GPU スキニング対象外（チャンネルごと読み飛ばす）
                AnimationOutputs::MorphWeights(_) => continue,
            };

            // タイムスタンプは「チャンネルを採用したときだけ」詰める
            // （読み飛ばしたチャンネルのぶんの穴を作らないため、必ず採用確定後に行う）。
            out.timestamps.extend_from_slice(&s.timestamps);
            out.channels.push(GpuChannelInfo {
                target_node: ch.target_node_index as u32,
                prop_type,
                ts_offset,
                ts_count,
                val_offset,
                interp,
                _pad: [0; 2],
            });
        }

        out.anims.push(GpuAnimInfo {
            chan_offset,
            chan_count: out.channels.len() as u32 - chan_offset,
            duration: anim.duration.max(1e-4),
            _pad: 0,
        });
    }

    // 空バッファ防止（wgpu は 0 バイトのバッファを作れない）。
    // ダミーは末尾に置くだけなので、上で計算したオフセットには影響しない。
    if out.timestamps.is_empty() {
        out.timestamps.push(0.0);
    }
    if out.trans_vals.is_empty() {
        out.trans_vals.push([0.0; 4]);
    }
    if out.rot_vals.is_empty() {
        out.rot_vals.push([0.0, 0.0, 0.0, 1.0]);
    }
    if out.scale_vals.is_empty() {
        out.scale_vals.push([1.0, 1.0, 1.0, 1.0]);
    }
    if out.channels.is_empty() {
        out.channels.push(bytemuck::Zeroable::zeroed());
    }
    if out.anims.is_empty() {
        out.anims.push(GpuAnimInfo { chan_offset: 0, chan_count: 0, duration: 1e-4, _pad: 0 });
    }

    out
}

// ============================================================
//  SkinComputeSystem
// ============================================================

/// GPU スキニング コンピュートシステム。
///
/// モデルにスキンとアニメーションが存在する場合にのみ生成される。
pub struct SkinComputeSystem {
    // ── 静的 BG（ロード時 1 回）─────────────────────────────
    pub static_bg: wgpu::BindGroup,

    // ── LOD ごとのバッファ & BG ──────────────────────────────
    /// コンパクト anim_times (storage) ＋ SkinParams (uniform) → group 0
    pub lod_per_frame_bgs: Vec<wgpu::BindGroup>,
    lod_anim_times_bufs: Vec<wgpu::Buffer>,
    lod_params_bufs: Vec<wgpu::Buffer>,

    /// 出力ジョイント行列バッファ → compute group 2
    lod_output_bgs: Vec<wgpu::BindGroup>,

    /// 頂点シェーダ用 BG → vertex group 3
    pub lod_joint_vs_bgs: Vec<wgpu::BindGroup>,

    /// LOD ごとのジョイント行列バッファ（`sk_jmats_lodN`）そのもの。
    ///
    /// 【なぜバッファ自体を保持するのか（RT スキン BLAS）】
    /// 従来は生成した `jm_buf` を出力 BG／VS BG へ move するだけで保持していなかった。
    /// Phase RT-Skin では、同じジョイント行列を **compute 可視の別 BindGroup**
    /// （`SkinDeformPipeline::joint_bgl`）からも読む必要がある。既存 BG の
    /// レイアウトは visibility=COMPUTE(出力)/VERTEX(VS) で用途が固定されているため
    /// 流用できず、バッファ実体から新しい BindGroup を作る。
    /// バッファは共有なので VRAM 増加はゼロ（Vec の参照ぶんのみ）。
    lod_jmat_bufs: Vec<wgpu::Buffer>,

    // ── メタデータ ────────────────────────────────────────────
    pub n_nodes: u32,
    pub n_joints: u32,
    /// 全アニメ連結後のチャンネル総数
    pub n_channels: u32,
    /// GPU へ登録できたアニメ本数（この本数未満の index だけが再生できる）
    pub n_anims: u32,
    /// アニメごとのクリップ長（秒）。`upload_lod_poses` の時刻クランプに使う。
    pub anim_durations: Vec<f32>,
    /// 先頭アニメのクリップ長（秒）。旧来 API 互換のショートカット。
    pub anim_duration: f32,
    /// バッチの割り当て済みインスタンス上限（バッファサイズ算出に使用した値）
    pub max_instances: u32,

    /// このスキンシステムの GPU リソース生成世代（`next_gpu_generation()` で採番）。
    ///
    /// 【なぜ必要か】統合バッチは容量不足時に **同じ batch_key のまま作り直される**
    /// （frame_renderer の統合バッチ再生成／terrain_scatter_ops の容量拡張）。
    /// そのとき `SkinComputeSystem` ごと新しくなり、`sk_jmats_lodN` も別バッファになる。
    /// RT スキン BLAS（rt_skin_blas.rs）はジョイント行列 BindGroup を (batch_key, LOD) で
    /// キャッシュするため、この世代を突き合わせないと **旧バッファを掴んだ BindGroup を
    /// 使い続け、RT 上のキャラだけがそのフレームのポーズで永久停止する**。
    pub generation: u64,
}

impl SkinComputeSystem {
    /// スキンとアニメーションを持つモデルからシステムを生成する。
    /// スキン・アニメーションがない場合は `None` を返す。
    pub fn new(
        device: &wgpu::Device,
        model: &Model,
        num_instances: u32,
        skin_pipeline: &SkinComputePipeline,
        joint_bgl: &wgpu::BindGroupLayout,
    ) -> Option<Self> {
        if model.skins.is_empty() || model.animations.is_empty() {
            return None;
        }

        let skin = &model.skins[0];
        let n_nodes = model.nodes.len() as u32;
        let n_joints = (skin.joints.len() as u32).min(MAX_JOINTS as u32);

        // ── バインドポーズ TRS ────────────────────────────────
        let bind_t: Vec<[f32; 4]> = model
            .nodes
            .iter()
            .map(|n| [n.translation[0], n.translation[1], n.translation[2], 0.0])
            .collect();
        let bind_r: Vec<[f32; 4]> = model.nodes.iter().map(|n| n.rotation).collect();
        let bind_s: Vec<[f32; 4]> = model
            .nodes
            .iter()
            .map(|n| [n.scale[0], n.scale[1], n.scale[2], 1.0])
            .collect();

        // ── アニメーションデータのパッキング（全アニメを 1 本に連結）─────
        // 上限超過の打ち切り・オフセット計算は純ロジック（pack_animations）に委ねる。
        let packed = pack_animations(&model.animations, &model.name);
        let n_channels = packed.channels.len() as u32;
        let n_anims = packed.anims.len() as u32;
        let anim_durations: Vec<f32> = packed.anims.iter().map(|a| a.duration).collect();
        let anim_duration = anim_durations.first().copied().unwrap_or(1e-4);

        // BFS 順序 + 親インデックス
        let (bfs_order, parent_indices) = compute_bfs_order(model);

        // ジョイントノード + インバースバインド行列（CPU 行優先 → GPU 列優先）
        let joint_nodes: Vec<u32> = skin
            .joints
            .iter()
            .take(MAX_JOINTS)
            .map(|j| j.node_index as u32)
            .collect();
        let ibm_data: Vec<[[f32; 4]; 4]> = skin
            .joints
            .iter()
            .take(MAX_JOINTS)
            .map(|j| transpose4x4(&j.inverse_bind_matrix))
            .collect();

        // ── GPU バッファ + 静的 BG の作成 ─────────────────────
        let mk = |label: &str, data: &[u8]| -> wgpu::Buffer {
            device.create_buffer_init(&wgpu::util::BufferInitDescriptor {
                label: Some(label),
                contents: data,
                usage: wgpu::BufferUsages::STORAGE | wgpu::BufferUsages::COPY_DST,
            })
        };

        let bind_t_buf = mk("sk_bind_t", bytemuck::cast_slice(&bind_t));
        let bind_r_buf = mk("sk_bind_r", bytemuck::cast_slice(&bind_r));
        let bind_s_buf = mk("sk_bind_s", bytemuck::cast_slice(&bind_s));
        let chan_buf = mk("sk_channels", bytemuck::cast_slice(&packed.channels));
        let ts_buf = mk("sk_ts", bytemuck::cast_slice(&packed.timestamps));
        let trans_buf = mk("sk_trans", bytemuck::cast_slice(&packed.trans_vals));
        let rot_buf = mk("sk_rot", bytemuck::cast_slice(&packed.rot_vals));
        let scale_buf = mk("sk_scale", bytemuck::cast_slice(&packed.scale_vals));
        // アニメテーブル（どの範囲のチャンネルがどのアニメか）。
        // uniform 配列は固定長でなければならないため MAX_ANIMS 件へパディングする
        //（16B × 64 = 1KB。有効長は SkinParams.n_anims が示す）。
        let mut anim_table = packed.anims.clone();
        anim_table.resize(MAX_ANIMS, GpuAnimInfo { chan_offset: 0, chan_count: 0, duration: 1e-4, _pad: 0 });
        let anim_tbl_buf = device.create_buffer_init(&wgpu::util::BufferInitDescriptor {
            label: Some("sk_anims"),
            contents: bytemuck::cast_slice(&anim_table),
            usage: wgpu::BufferUsages::UNIFORM | wgpu::BufferUsages::COPY_DST,
        });
        let bfs_buf = mk("sk_bfs", bytemuck::cast_slice(&bfs_order));
        let par_buf = mk("sk_parents", bytemuck::cast_slice(&parent_indices));
        let jnode_buf = mk("sk_jnodes", bytemuck::cast_slice(&joint_nodes));
        let ibm_buf = mk("sk_ibm", bytemuck::cast_slice(&ibm_data));

        let static_bg = device.create_bind_group(&wgpu::BindGroupDescriptor {
            label: Some("Skin Static BG"),
            layout: &skin_pipeline.static_bgl,
            entries: &[
                wgpu::BindGroupEntry {
                    binding: 0,
                    resource: bind_t_buf.as_entire_binding(),
                },
                wgpu::BindGroupEntry {
                    binding: 1,
                    resource: bind_r_buf.as_entire_binding(),
                },
                wgpu::BindGroupEntry {
                    binding: 2,
                    resource: bind_s_buf.as_entire_binding(),
                },
                wgpu::BindGroupEntry {
                    binding: 3,
                    resource: chan_buf.as_entire_binding(),
                },
                wgpu::BindGroupEntry {
                    binding: 4,
                    resource: ts_buf.as_entire_binding(),
                },
                wgpu::BindGroupEntry {
                    binding: 5,
                    resource: trans_buf.as_entire_binding(),
                },
                wgpu::BindGroupEntry {
                    binding: 6,
                    resource: rot_buf.as_entire_binding(),
                },
                wgpu::BindGroupEntry {
                    binding: 7,
                    resource: scale_buf.as_entire_binding(),
                },
                wgpu::BindGroupEntry {
                    binding: 8,
                    resource: bfs_buf.as_entire_binding(),
                },
                wgpu::BindGroupEntry {
                    binding: 9,
                    resource: par_buf.as_entire_binding(),
                },
                wgpu::BindGroupEntry {
                    binding: 10,
                    resource: jnode_buf.as_entire_binding(),
                },
                wgpu::BindGroupEntry {
                    binding: 11,
                    resource: ibm_buf.as_entire_binding(),
                },
                wgpu::BindGroupEntry {
                    binding: 12,
                    resource: anim_tbl_buf.as_entire_binding(),
                },
            ],
        });

        // ── LOD ごとのバッファ & BG ──────────────────────────
        let max_inst = num_instances.max(1) as usize;
        let jmat_size = (max_inst * MAX_JOINTS * std::mem::size_of::<[[f32; 4]; 4]>()) as u64;
        // per-instance の再生指定（アニメ index ×2・時刻 ×2・weight）を storage で渡す
        let atime_size =
            (max_inst * std::mem::size_of::<GpuAnimSample>()).max(std::mem::size_of::<GpuAnimSample>())
                as u64;
        let params_size = std::mem::size_of::<GpuSkinParams>() as u64;

        let mut lod_anim_times_bufs = Vec::with_capacity(NUM_LODS);
        let mut lod_params_bufs = Vec::with_capacity(NUM_LODS);
        let mut lod_per_frame_bgs = Vec::with_capacity(NUM_LODS);
        let mut lod_output_bgs = Vec::with_capacity(NUM_LODS);
        let mut lod_joint_vs_bgs = Vec::with_capacity(NUM_LODS);
        // RT スキン BLAS の変形 compute が同じバッファを読むため、実体を保持する。
        let mut lod_jmat_bufs = Vec::with_capacity(NUM_LODS);

        for lod in 0..NUM_LODS {
            let at_buf = device.create_buffer(&wgpu::BufferDescriptor {
                label: Some(&format!("sk_atime_lod{lod}")),
                size: atime_size,
                usage: wgpu::BufferUsages::STORAGE | wgpu::BufferUsages::COPY_DST,
                mapped_at_creation: false,
            });
            let pm_buf = device.create_buffer(&wgpu::BufferDescriptor {
                label: Some(&format!("sk_params_lod{lod}")),
                size: params_size,
                usage: wgpu::BufferUsages::UNIFORM | wgpu::BufferUsages::COPY_DST,
                mapped_at_creation: false,
            });
            let pf_bg = device.create_bind_group(&wgpu::BindGroupDescriptor {
                label: Some(&format!("Skin PF BG lod{lod}")),
                layout: &skin_pipeline.per_frame_bgl,
                entries: &[
                    wgpu::BindGroupEntry {
                        binding: 0,
                        resource: at_buf.as_entire_binding(),
                    },
                    wgpu::BindGroupEntry {
                        binding: 1,
                        resource: pm_buf.as_entire_binding(),
                    },
                ],
            });

            let jm_buf = device.create_buffer(&wgpu::BufferDescriptor {
                label: Some(&format!("sk_jmats_lod{lod}")),
                size: jmat_size,
                // COPY_SRC は実 GPU テストのリードバック用（描画経路は読み出さない）。
                usage: wgpu::BufferUsages::STORAGE | wgpu::BufferUsages::COPY_SRC,
                mapped_at_creation: false,
            });
            let out_bg = device.create_bind_group(&wgpu::BindGroupDescriptor {
                label: Some(&format!("Skin Out BG lod{lod}")),
                layout: &skin_pipeline.output_bgl,
                entries: &[wgpu::BindGroupEntry {
                    binding: 0,
                    resource: jm_buf.as_entire_binding(),
                }],
            });
            let vs_bg = device.create_bind_group(&wgpu::BindGroupDescriptor {
                label: Some(&format!("Skin VS BG lod{lod}")),
                layout: joint_bgl,
                entries: &[wgpu::BindGroupEntry {
                    binding: 0,
                    resource: jm_buf.as_entire_binding(),
                }],
            });

            lod_anim_times_bufs.push(at_buf);
            lod_params_bufs.push(pm_buf);
            lod_per_frame_bgs.push(pf_bg);
            lod_output_bgs.push(out_bg);
            lod_joint_vs_bgs.push(vs_bg);
            // BG へ move せず実体を保持（RT スキン BLAS の変形 compute が別 BG から読む）。
            lod_jmat_bufs.push(jm_buf);
        }

        Some(Self {
            static_bg,
            lod_per_frame_bgs,
            lod_anim_times_bufs,
            lod_params_bufs,
            lod_output_bgs,
            lod_joint_vs_bgs,
            lod_jmat_bufs,
            n_nodes,
            n_joints,
            n_channels,
            n_anims,
            anim_durations,
            anim_duration,
            max_instances: num_instances,
            // バッチ再生成のたびに新しい世代を採番する（RT 側の BindGroup キャッシュ追従用）。
            generation: next_gpu_generation(),
        })
    }

    /// 指定 LOD のジョイント行列バッファ（`sk_jmats_lodN`）を返す。
    ///
    /// RT スキン BLAS（`rt_skin_blas.rs`）の変形 compute が、compute 可視の
    /// 専用 BindGroup を作るために参照する。頂点シェーダが読むのとまったく同じ
    /// バッファなので、変形結果は描画結果と厳密に一致する。
    pub fn jmat_buffer(&self, lod: usize) -> Option<&wgpu::Buffer> {
        self.lod_jmat_bufs.get(lod)
    }

    /// 再生指定（アニメ index・時刻・ブレンド率）を GPU 用の POD へ正規化する。
    ///
    /// - アニメ index は登録済み本数へクランプ（打ち切られた index は先頭アニメで代替）。
    /// - 時刻はそのアニメのクリップ長へクランプ（呼び出し側で正規化済みだが安全側）。
    /// - weight は 0..=1 へクランプ（NaN は 1.0 = フェード無しへ倒す）。
    fn normalize_pose(&self, pose: SkinAnimPose) -> GpuAnimSample {
        let last = self.n_anims.saturating_sub(1);
        let ia = pose.anim_a.min(last);
        let ib = pose.anim_b.min(last);
        let dur = |i: u32| self.anim_durations.get(i as usize).copied().unwrap_or(self.anim_duration);
        let w = if pose.weight.is_nan() { 1.0 } else { pose.weight.clamp(0.0, 1.0) };
        GpuAnimSample {
            anim_a: ia,
            anim_b: ib,
            time_a: pose.time_a.clamp(0.0, dur(ia)),
            time_b: pose.time_b.clamp(0.0, dur(ib)),
            weight: w,
            _pad: [0; 3],
        }
    }

    /// LOD ごとのコンパクト再生指定（アニメ index・時刻・ブレンド率）を GPU にアップロードする。
    ///
    /// `compact_inst_indices`: このLODで可視なインスタンスの元インデックス一覧。
    /// `pose_overrides`: インスタンスごとの Animator 駆動再生指定（元インデックス順）。
    ///   `Some(pose)` のインスタンスはその指定で再生する（複数アニメ選択＋クロスフェード）。
    ///   空 or `None` のインスタンスは **静止**（`FROZEN_POSE` ＝アニメ 0 の先頭フレーム）。
    ///
    /// 【静止ポーズの選択】Animator 非駆動時は animations[0] の t=0 姿勢で凍結する。
    /// バインドポーズ（単位ジョイント行列）にするにはコンピュートシェーダ側に
    /// 「チャンネル評価をスキップする」分岐の追加が必要になるため、
    /// 既存パイプラインのまま時刻を固定するだけで済む t=0 凍結を採用した。
    pub fn upload_lod_poses(
        &self,
        queue: &wgpu::Queue,
        lod: usize,
        compact_inst_indices: &[usize],
        pose_overrides: &[Option<SkinAnimPose>],
    ) {
        let visible = compact_inst_indices.len() as u32;
        if visible == 0 {
            return;
        }

        let samples: Vec<GpuAnimSample> = compact_inst_indices
            .iter()
            .map(|&orig| {
                let pose = match pose_overrides.get(orig) {
                    Some(Some(p)) => *p,
                    // 非駆動インスタンスは先頭アニメの先頭フレームで静止させる
                    _ => FROZEN_POSE,
                };
                self.normalize_pose(pose)
            })
            .collect();

        queue.write_buffer(
            &self.lod_anim_times_bufs[lod],
            0,
            bytemuck::cast_slice(&samples),
        );
        queue.write_buffer(
            &self.lod_params_bufs[lod],
            0,
            bytemuck::bytes_of(&GpuSkinParams {
                n_nodes: self.n_nodes,
                n_joints: self.n_joints,
                n_channels: self.n_channels,
                n_visible: visible,
                n_anims: self.n_anims,
                _pad: [0; 3],
            }),
        );
    }

    /// 指定 LOD の GPU スキニング計算を ComputePass に積む。
    ///
    /// 呼び出し元が 1 つの ComputePass を共有して複数の LOD / アクター分を
    /// まとめて記録することで、begin/end pass のオーバーヘッドを削減する。
    pub fn dispatch_lod(
        &self,
        pass: &mut wgpu::ComputePass<'_>,
        pipeline: &SkinComputePipeline,
        lod: usize,
        visible_count: u32,
    ) {
        if visible_count == 0 {
            return;
        }

        let wg_count = (visible_count + 63) / 64;
        pass.set_pipeline(&pipeline.pipeline);
        pass.set_bind_group(0, &self.lod_per_frame_bgs[lod], &[]);
        pass.set_bind_group(1, &self.static_bg, &[]);
        pass.set_bind_group(2, &self.lod_output_bgs[lod], &[]);
        pass.dispatch_workgroups(wg_count, 1, 1);
    }
}

// ============================================================
//  内部ヘルパー
// ============================================================

/// シーングラフの BFS 走査順と各ノードの親インデックスを返す。
pub fn compute_bfs_order(model: &Model) -> (Vec<u32>, Vec<i32>) {
    let n = model.nodes.len();
    let mut bfs = Vec::with_capacity(n);
    let mut parents = vec![-1i32; n];
    let mut queue: std::collections::VecDeque<usize> = model.root_nodes.iter().copied().collect();

    while let Some(ni) = queue.pop_front() {
        bfs.push(ni as u32);
        for &child in &model.nodes[ni].children {
            parents[child] = ni as i32;
            queue.push_back(child);
        }
    }

    // 孤立ノード対策
    let visited: std::collections::HashSet<u32> = bfs.iter().copied().collect();
    for i in 0..n {
        if !visited.contains(&(i as u32)) {
            bfs.push(i as u32);
        }
    }

    (bfs, parents)
}

/// 行優先行列（CPU）→ 列優先行列（GPU）への転置
fn transpose4x4(m: &[[f32; 4]; 4]) -> [[f32; 4]; 4] {
    let mut out = [[0.0f32; 4]; 4];
    for i in 0..4 {
        for j in 0..4 {
            out[i][j] = m[j][i];
        }
    }
    out
}

// ============================================================
//  テスト
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;
    use crate::engine::core::loader::model::{AnimationChannel, AnimationSampler};

    /// テスト用アニメを作る。`(target_node, keys)` のチャンネルを translation で並べる。
    fn anim(name: &str, duration: f32, channels: &[(usize, usize)]) -> Animation {
        Animation {
            name: name.to_string(),
            duration,
            channels: channels
                .iter()
                .map(|&(node, keys)| AnimationChannel {
                    target_node_index: node,
                    sampler: AnimationSampler {
                        interpolation: Interpolation::Linear,
                        timestamps: (0..keys).map(|i| i as f32).collect(),
                        outputs: AnimationOutputs::Translations(
                            (0..keys).map(|i| [i as f32, 0.0, 0.0]).collect(),
                        ),
                    },
                })
                .collect(),
        }
    }

    /// 複数アニメのチャンネル/キーオフセットが「連結順に隙間なく」計算される。
    #[test]
    fn packs_multiple_animations_with_contiguous_offsets() {
        let anims = vec![
            anim("Idle", 1.0, &[(0, 2), (1, 3)]),
            anim("Walk", 2.0, &[(2, 4)]),
        ];
        let p = pack_animations(&anims, "test");

        assert_eq!(p.anim_count(), 2);
        // アニメ 0: チャンネル 0..2 / アニメ 1: チャンネル 2..3
        assert_eq!((p.anims[0].chan_offset, p.anims[0].chan_count), (0, 2));
        assert_eq!((p.anims[1].chan_offset, p.anims[1].chan_count), (2, 1));
        assert_eq!(p.anims[0].duration, 1.0);
        assert_eq!(p.anims[1].duration, 2.0);
        assert_eq!(p.duration_of(1), 2.0);
        assert_eq!(p.duration_of(99), 1.0, "範囲外は先頭アニメ長へフォールバック");

        // タイムスタンプは 2 + 3 + 4 = 9 個が連結順に並ぶ
        assert_eq!(p.timestamps.len(), 9);
        assert_eq!(p.channels[0].ts_offset, 0);
        assert_eq!(p.channels[1].ts_offset, 2);
        assert_eq!(p.channels[2].ts_offset, 5);
        assert_eq!(p.channels[2].ts_count, 4);
        // 値バッファのオフセットも連結順（translation は 3 チャンネル合計 9 キー）
        assert_eq!(p.trans_vals.len(), 9);
        assert_eq!(p.channels[2].val_offset, 5);
        assert!(!p.truncated);
    }

    /// アニメ数が MAX_ANIMS を超えたら先頭 MAX_ANIMS 本だけを登録する。
    #[test]
    fn truncates_animations_over_max_anims() {
        let anims: Vec<Animation> = (0..(MAX_ANIMS + 5))
            .map(|i| anim(&format!("A{i}"), 1.0, &[(0, 2)]))
            .collect();
        let p = pack_animations(&anims, "test");
        assert_eq!(p.anim_count(), MAX_ANIMS);
        assert!(p.truncated);
        assert_eq!(p.channels.len(), MAX_ANIMS);
    }

    /// チャンネル総数が上限に達したら、そこから先のアニメは登録しない（部分登録もしない）。
    #[test]
    fn truncates_animations_over_channel_budget() {
        // 1 本で上限ちょうどを使い切るアニメ + もう 1 本
        let big = anim(
            "Big",
            1.0,
            &(0..MAX_TOTAL_CHANNELS).map(|i| (i % 64, 1)).collect::<Vec<_>>(),
        );
        let anims = vec![big, anim("Extra", 1.0, &[(0, 2)])];
        let p = pack_animations(&anims, "test");
        assert_eq!(p.anim_count(), 1, "2 本目は丸ごと打ち切られる");
        assert_eq!(p.channels.len(), MAX_TOTAL_CHANNELS);
        assert!(p.truncated);
    }

    /// アニメ 0 本でもダミーで埋めて空バッファを作らない（wgpu は 0 バイト不可）。
    #[test]
    fn empty_animations_produce_non_empty_buffers() {
        let p = pack_animations(&[], "test");
        assert_eq!(p.anim_count(), 1);
        assert_eq!(p.anims[0].chan_count, 0);
        assert!(!p.timestamps.is_empty());
        assert!(!p.channels.is_empty());
        assert!(!p.trans_vals.is_empty() && !p.rot_vals.is_empty() && !p.scale_vals.is_empty());
    }

    /// モーフターゲットチャンネルは読み飛ばされ、タイムスタンプ列に穴を作らない。
    #[test]
    fn morph_channels_are_skipped_without_leaving_gaps() {
        let mut a = anim("M", 1.0, &[(0, 2)]);
        a.channels.push(AnimationChannel {
            target_node_index: 1,
            sampler: AnimationSampler {
                interpolation: Interpolation::Linear,
                timestamps: vec![0.0, 1.0, 2.0],
                outputs: AnimationOutputs::MorphWeights(vec![0.0, 1.0, 0.0]),
            },
        });
        a.channels.push(AnimationChannel {
            target_node_index: 2,
            sampler: AnimationSampler {
                interpolation: Interpolation::Linear,
                timestamps: vec![0.0, 1.0],
                outputs: AnimationOutputs::Scales(vec![[1.0; 3], [2.0; 3]]),
            },
        });
        let p = pack_animations(&[a], "test");
        assert_eq!(p.channels.len(), 2, "モーフチャンネルは登録されない");
        assert_eq!(p.timestamps.len(), 4, "読み飛ばしたキーは詰めない");
        assert_eq!(p.channels[1].ts_offset, 2, "後続チャンネルのオフセットに穴が空かない");
    }

    /// 静止（None）と実ポーズのポーズ署名は必ず異なり、weight だけの差も検出される。
    #[test]
    fn pose_signature_reflects_blend_state() {
        let base = SkinAnimPose { anim_a: 0, time_a: 0.25, anim_b: 1, time_b: 0.5, weight: 0.3 };
        let mut only_weight = base;
        only_weight.weight = 0.4;
        let mut only_src = base;
        only_src.anim_a = 2;

        assert_ne!(pose_sig_bits(Some(base)), pose_sig_bits(Some(only_weight)));
        assert_ne!(pose_sig_bits(Some(base)), pose_sig_bits(Some(only_src)));
        assert_ne!(pose_sig_bits(Some(base)), pose_sig_bits(None));
        assert_eq!(pose_sig_bits(Some(base)), pose_sig_bits(Some(base)));
        // フェード無しの単一クリップは A/B が同一で weight=1
        let single = SkinAnimPose::single(3, 1.5);
        assert_eq!(single.weight, 1.0);
        assert_eq!(single.anim_a, single.anim_b);
    }

    /// skin_compute.wgsl が naga で parse / validate できる（複数アニメ＋ブレンド版）。
    #[test]
    fn skin_compute_wgsl_is_valid() {
        let src = include_str!("shaders/skin_compute.wgsl");
        let module = naga::front::wgsl::parse_str(src)
            .unwrap_or_else(|e| panic!("[skin_compute] WGSL parse 失敗: {e:?}"));
        let mut validator = naga::valid::Validator::new(
            naga::valid::ValidationFlags::all(),
            naga::valid::Capabilities::empty(),
        );
        validator
            .validate(&module)
            .unwrap_or_else(|e| panic!("[skin_compute] WGSL validate 失敗: {e:?}"));

        // アニメテーブルは固定長 uniform。WGSL の要素数と Rust の MAX_ANIMS がずれると、
        // 61 本目以降のアニメが GPU 側で読めない／範囲外になる（BGL 生成は通ってしまう）。
        assert!(
            src.contains(&format!("array<AnimInfo, {MAX_ANIMS}>")),
            "WGSL のアニメテーブル長が MAX_ANIMS({MAX_ANIMS}) と一致していない"
        );
    }

    /// **実 GPU**: 複数アニメのパッキング＋クロスフェード compute を 1 回走らせ、
    /// ジョイント行列が「アニメ選択」と「ブレンド」の両方を反映していることを確認する。
    ///
    /// 3 インスタンスを 1 ディスパッチで処理する:
    ///   - inst0: アニメ 0 のみ（平行移動 +10 X）
    ///   - inst1: アニメ 1 のみ（平行移動 +10 Y）… **同じバッチ内で別アニメ**
    ///   - inst2: アニメ 0 → 1 へ weight 0.5 でブレンド（(5,5,0) になるはず）
    ///
    /// 実行: `cargo test skin_system::tests::multi_anim_blend_runs_on_gpu -- --ignored --nocapture`
    #[test]
    #[ignore = "実 GPU が必要。--ignored で実行する"]
    fn multi_anim_blend_runs_on_gpu() {
        use crate::engine::core::loader::model::{ModelNode, Skin, SkinJoint};
        use crate::engine::core::renderer::pipeline::SkinComputePipeline;

        // ── 1 ノード 1 ジョイント、アニメ 2 本（X 移動 / Y 移動）のモデル ──
        let translate_anim = |name: &str, v: [f32; 3]| Animation {
            name: name.to_string(),
            duration: 1.0,
            channels: vec![AnimationChannel {
                target_node_index: 0,
                sampler: AnimationSampler {
                    interpolation: Interpolation::Linear,
                    timestamps: vec![0.0, 1.0],
                    outputs: AnimationOutputs::Translations(vec![v, v]),
                },
            }],
        };
        let model = Model {
            name: "gpu_blend_test".into(),
            nodes: vec![ModelNode {
                name: "root".into(),
                local_matrix: ModelNode::identity_matrix(),
                translation: [0.0; 3],
                rotation: [0.0, 0.0, 0.0, 1.0],
                scale: [1.0; 3],
                mesh_index: None,
                skin_index: Some(0),
                children: vec![],
                parent: None,
            }],
            root_nodes: vec![0],
            meshes: vec![],
            materials: vec![],
            textures: vec![],
            animations: vec![
                translate_anim("MoveX", [10.0, 0.0, 0.0]),
                translate_anim("MoveY", [0.0, 10.0, 0.0]),
            ],
            skins: vec![Skin {
                name: "skin".into(),
                joints: vec![SkinJoint {
                    node_index: 0,
                    name: "root".into(),
                    inverse_bind_matrix: ModelNode::identity_matrix(),
                }],
                root_joint: Some(0),
            }],
        };

        // ── デバイス生成（無ければスキップ）──
        let instance = wgpu::Instance::new(&wgpu::InstanceDescriptor::default());
        let Ok(adapter) =
            pollster::block_on(instance.request_adapter(&wgpu::RequestAdapterOptions::default()))
        else {
            eprintln!("[skin_system] GPU アダプタが見つからないため検証をスキップ");
            return;
        };
        // スキン静的 BG は storage を 12 本使う（既定リミットの 8 では足りない）。
        // 本番の初期化（renderer::mod.rs）と同じ値を要求する。
        let Ok((device, queue)) = pollster::block_on(adapter.request_device(&wgpu::DeviceDescriptor {
            required_limits: wgpu::Limits {
                max_storage_buffers_per_shader_stage: 12,
                ..wgpu::Limits::default()
            },
            ..Default::default()
        })) else {
            eprintln!("[skin_system] デバイス生成に失敗したため検証をスキップ");
            return;
        };

        // ── スキンシステム生成（全アニメがパッキングされる）──
        let pipeline = SkinComputePipeline::new(&device, None);
        // 頂点シェーダ用 BGL は本テストでは使わないが、生成には必要なので同等のものを作る。
        let joint_bgl = device.create_bind_group_layout(&wgpu::BindGroupLayoutDescriptor {
            label: Some("test joint vs bgl"),
            entries: &[wgpu::BindGroupLayoutEntry {
                binding: 0,
                visibility: wgpu::ShaderStages::VERTEX,
                ty: wgpu::BindingType::Buffer {
                    ty: wgpu::BufferBindingType::Storage { read_only: true },
                    has_dynamic_offset: false,
                    min_binding_size: None,
                },
                count: None,
            }],
        });
        const N_INST: usize = 3;
        let skin = SkinComputeSystem::new(&device, &model, N_INST as u32, &pipeline, &joint_bgl)
            .expect("スキン＋アニメありのモデルからは生成できる");
        assert_eq!(skin.n_anims, 2, "2 本ともパッキングされる");

        // ── 再生指定をアップロードして 1 回ディスパッチ ──
        let poses = vec![
            Some(SkinAnimPose::single(0, 0.5)),
            Some(SkinAnimPose::single(1, 0.5)),
            Some(SkinAnimPose { anim_a: 0, time_a: 0.5, anim_b: 1, time_b: 0.5, weight: 0.5 }),
        ];
        let compact: Vec<usize> = (0..N_INST).collect();
        skin.upload_lod_poses(&queue, 0, &compact, &poses);

        let readback = device.create_buffer(&wgpu::BufferDescriptor {
            label: Some("test readback"),
            size: (N_INST * MAX_JOINTS * std::mem::size_of::<[[f32; 4]; 4]>()) as u64,
            usage: wgpu::BufferUsages::COPY_DST | wgpu::BufferUsages::MAP_READ,
            mapped_at_creation: false,
        });
        let mut enc = device.create_command_encoder(&wgpu::CommandEncoderDescriptor::default());
        {
            let mut pass = enc.begin_compute_pass(&wgpu::ComputePassDescriptor::default());
            skin.dispatch_lod(&mut pass, &pipeline, 0, N_INST as u32);
        }
        enc.copy_buffer_to_buffer(
            skin.jmat_buffer(0).expect("LOD0 のジョイント行列バッファ"),
            0,
            &readback,
            0,
            readback.size(),
        );
        queue.submit([enc.finish()]);

        // ── 読み戻して各インスタンスのジョイント行列（列優先）を検証 ──
        let slice = readback.slice(..);
        slice.map_async(wgpu::MapMode::Read, |_| {});
        device.poll(wgpu::PollType::Wait).expect("GPU 完了待ち");
        let data = slice.get_mapped_range();
        let mats: &[[[f32; 4]; 4]] = bytemuck::cast_slice(&data);

        // 列優先なので平行移動は col3（= mats[i][3]）に入る
        let tr = |inst: usize| mats[inst * MAX_JOINTS][3];
        let approx = |a: [f32; 4], b: [f32; 3]| {
            (a[0] - b[0]).abs() < 1e-3 && (a[1] - b[1]).abs() < 1e-3 && (a[2] - b[2]).abs() < 1e-3
        };
        assert!(approx(tr(0), [10.0, 0.0, 0.0]), "inst0 = アニメ0: {:?}", tr(0));
        assert!(approx(tr(1), [0.0, 10.0, 0.0]), "inst1 = アニメ1: {:?}", tr(1));
        assert!(approx(tr(2), [5.0, 5.0, 0.0]), "inst2 = 0→1 の中間: {:?}", tr(2));

        drop(data);
        readback.unmap();
    }

    /// Rust 側 POD と WGSL 側 struct のサイズ規約が一致している
    /// （AnimSample = 32B / AnimInfo = 16B / SkinParams = 32B / ChannelInfo = 32B）。
    #[test]
    fn gpu_struct_sizes_match_wgsl_layout() {
        assert_eq!(std::mem::size_of::<GpuAnimSample>(), 32);
        assert_eq!(std::mem::size_of::<GpuAnimInfo>(), 16);
        assert_eq!(std::mem::size_of::<GpuSkinParams>(), 32);
        assert_eq!(std::mem::size_of::<GpuChannelInfo>(), 32);
    }
}
