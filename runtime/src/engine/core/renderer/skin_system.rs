// ============================================================
//  skin_system.rs — GPU スキニング システム
//
//  SkinComputeSystem:
//    - アニメーションデータを GPU に 1 回だけアップロード
//    - フレームごとに compact_anim_times のみ更新
//    - dispatch_lod() でコンピュートシェーダを実行してジョイント行列を計算
//    - 頂点シェーダが読む joint VS BG（group 3）を per-LOD で提供
// ============================================================

use super::gpu_resources::NUM_LODS;
use super::pipeline::SkinComputePipeline;
use crate::engine::core::loader::model::{AnimationOutputs, Interpolation, Model};
use wgpu::util::DeviceExt;

// ============================================================
//  GPU 側データ構造
// ============================================================

#[repr(C)]
#[derive(Clone, Copy, bytemuck::Pod, bytemuck::Zeroable)]
struct GpuChannelInfo {
    target_node: u32,
    prop_type: u32, // 0=T, 1=R, 2=S
    ts_offset: u32,
    ts_count: u32,
    val_offset: u32,
    interp: u32, // 0=LINEAR, 1=STEP, 2=CUBICSPLINE
    _pad: [u32; 2],
}

#[repr(C)]
#[derive(Clone, Copy, bytemuck::Pod, bytemuck::Zeroable)]
pub struct GpuSkinParams {
    pub n_nodes: u32,
    pub n_joints: u32,
    pub n_channels: u32,
    pub n_visible: u32,
}

pub const MAX_JOINTS: usize = 128;

/// Animator 非駆動インスタンスの静止時刻（animations[0] の先頭フレームで凍結）。
pub const FROZEN_POSE_TIME: f32 = 0.0;

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

    // ── メタデータ ────────────────────────────────────────────
    pub n_nodes: u32,
    pub n_joints: u32,
    pub n_channels: u32,
    pub anim_duration: f32,
    /// バッチの割り当て済みインスタンス上限（バッファサイズ算出に使用した値）
    pub max_instances: u32,
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
        let anim = &model.animations[0];
        let n_nodes = model.nodes.len() as u32;
        let n_joints = (skin.joints.len() as u32).min(MAX_JOINTS as u32);
        let anim_duration = anim.duration.max(1e-4);

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

        // ── アニメーションデータのパッキング ─────────────────
        let mut gpu_channels = Vec::<GpuChannelInfo>::new();
        let mut timestamps = Vec::<f32>::new();
        let mut trans_vals = Vec::<[f32; 4]>::new();
        let mut rot_vals = Vec::<[f32; 4]>::new();
        let mut scale_vals = Vec::<[f32; 4]>::new();

        for ch in &anim.channels {
            let s = &ch.sampler;
            let ts_offset = timestamps.len() as u32;
            let ts_count = s.timestamps.len() as u32;
            timestamps.extend_from_slice(&s.timestamps);

            let interp = match s.interpolation {
                Interpolation::Linear => 0u32,
                Interpolation::Step => 1u32,
                Interpolation::CubicSpline => 2u32,
            };

            let (prop_type, val_offset) = match &s.outputs {
                AnimationOutputs::Translations(v) => {
                    let off = trans_vals.len() as u32;
                    for t in v {
                        trans_vals.push([t[0], t[1], t[2], 0.0]);
                    }
                    (0u32, off)
                }
                AnimationOutputs::Rotations(v) => {
                    let off = rot_vals.len() as u32;
                    for r in v {
                        rot_vals.push(*r);
                    }
                    (1u32, off)
                }
                AnimationOutputs::Scales(v) => {
                    let off = scale_vals.len() as u32;
                    for sc in v {
                        scale_vals.push([sc[0], sc[1], sc[2], 1.0]);
                    }
                    (2u32, off)
                }
                AnimationOutputs::MorphWeights(_) => continue,
            };

            gpu_channels.push(GpuChannelInfo {
                target_node: ch.target_node_index as u32,
                prop_type,
                ts_offset,
                ts_count,
                val_offset,
                interp,
                _pad: [0; 2],
            });
        }

        let n_channels = gpu_channels.len() as u32;

        // 空バッファ防止（wgpu は 0 バイト不可）
        if timestamps.is_empty() {
            timestamps.push(0.0);
        }
        if trans_vals.is_empty() {
            trans_vals.push([0.0; 4]);
        }
        if rot_vals.is_empty() {
            rot_vals.push([0.0, 0.0, 0.0, 1.0]);
        }
        if scale_vals.is_empty() {
            scale_vals.push([1.0, 1.0, 1.0, 1.0]);
        }
        if gpu_channels.is_empty() {
            gpu_channels.push(bytemuck::Zeroable::zeroed());
        }

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
        let chan_buf = mk("sk_channels", bytemuck::cast_slice(&gpu_channels));
        let ts_buf = mk("sk_ts", bytemuck::cast_slice(&timestamps));
        let trans_buf = mk("sk_trans", bytemuck::cast_slice(&trans_vals));
        let rot_buf = mk("sk_rot", bytemuck::cast_slice(&rot_vals));
        let scale_buf = mk("sk_scale", bytemuck::cast_slice(&scale_vals));
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
            ],
        });

        // ── LOD ごとのバッファ & BG ──────────────────────────
        let max_inst = num_instances.max(1) as usize;
        let jmat_size = (max_inst * MAX_JOINTS * std::mem::size_of::<[[f32; 4]; 4]>()) as u64;
        let atime_size = (max_inst * std::mem::size_of::<f32>()).max(16) as u64;
        let params_size = std::mem::size_of::<GpuSkinParams>() as u64;

        let mut lod_anim_times_bufs = Vec::with_capacity(NUM_LODS);
        let mut lod_params_bufs = Vec::with_capacity(NUM_LODS);
        let mut lod_per_frame_bgs = Vec::with_capacity(NUM_LODS);
        let mut lod_output_bgs = Vec::with_capacity(NUM_LODS);
        let mut lod_joint_vs_bgs = Vec::with_capacity(NUM_LODS);

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
                usage: wgpu::BufferUsages::STORAGE,
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
        }

        Some(Self {
            static_bg,
            lod_per_frame_bgs,
            lod_anim_times_bufs,
            lod_params_bufs,
            lod_output_bgs,
            lod_joint_vs_bgs,
            n_nodes,
            n_joints,
            n_channels,
            anim_duration,
            max_instances: num_instances,
        })
    }

    /// LOD ごとのコンパクト anim_times を GPU にアップロードする。
    ///
    /// `compact_inst_indices`: このLODで可視なインスタンスの元インデックス一覧。
    /// `time_overrides`: インスタンスごとの Animator 駆動権威時刻（元インデックス順）。
    ///   `Some(t)` のインスタンスは `t` を再生時刻に使う。
    ///   空 or `None` のインスタンスは **静止**（`FROZEN_POSE_TIME` で凍結）。
    ///
    /// 【静止ポーズの選択】Animator 非駆動時は animations[0] の t=0 姿勢で凍結する。
    /// バインドポーズ（単位ジョイント行列）にするにはコンピュートシェーダ側に
    /// 「チャンネル評価をスキップする」分岐の追加が必要になるため、
    /// 既存パイプラインのまま時刻を固定するだけで済む t=0 凍結を採用した。
    /// （旧仕様のグローバルクロック＋位相シードによる群衆デモ再生は廃止済み）
    pub fn upload_lod_times(
        &self,
        queue: &wgpu::Queue,
        lod: usize,
        compact_inst_indices: &[usize],
        time_overrides: &[Option<f32>],
    ) {
        let visible = compact_inst_indices.len() as u32;
        if visible == 0 {
            return;
        }

        let times: Vec<f32> = compact_inst_indices
            .iter()
            .map(|&orig| {
                // Animator 駆動の権威時刻があればそれを使う。
                // 呼び出し側でループ/クランプ正規化済みだが、安全のため [0, duration] にクランプする。
                match time_overrides.get(orig) {
                    Some(Some(t)) => t.clamp(0.0, self.anim_duration),
                    // 非駆動インスタンスは先頭フレームで静止させる
                    _ => FROZEN_POSE_TIME,
                }
            })
            .collect();

        queue.write_buffer(
            &self.lod_anim_times_bufs[lod],
            0,
            bytemuck::cast_slice(&times),
        );
        queue.write_buffer(
            &self.lod_params_bufs[lod],
            0,
            bytemuck::bytes_of(&GpuSkinParams {
                n_nodes: self.n_nodes,
                n_joints: self.n_joints,
                n_channels: self.n_channels,
                n_visible: visible,
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
