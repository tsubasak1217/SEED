// ============================================================
//  harness.rs — 旧い作りと新しい作りの skin compute を同じ入力で走らせ、読み戻して比べる（テスト専用・実 GPU）
//
//  【同じ入力の作り方】同じモデルから SkinComputeSystem を 2 つ作り（入力バッファの中身は同じ・出力バッファは別。
//  片方の書き残しが他方の未書き込みを隠さないよう、出力は 0 で始まる別のバッファにする）、同じ再生指定を
//  upload_lod_poses で書く。新しい作りは本番の `dispatch_lod`（1 ワークグループ = 1 インスタンス）で、
//  旧い作りは写しのパイプライン（同じ BindGroupLayout）を 64 スレッドのワークグループで dispatch する。
// ============================================================

use crate::engine::core::loader::model::Model;
use crate::engine::core::renderer::gpu_mem::GpuMemDeviceExt;
use crate::engine::core::renderer::pipeline::SkinComputePipeline;
use crate::engine::core::renderer::skin_system::{SkinAnimPose, SkinComputeSystem, MAX_JOINTS};

/// 旧い作りのワークグループの大きさ（legacy_skin_compute.wgsl の `@workgroup_size(64)`。1 スレッド = 1 インスタンス）。
const LEGACY_WORKGROUP_SIZE: u32 = 64;

/// 走らせる LOD（どの LOD も同じ作りのバッファなので 0 だけでよい）。
const LOD: usize = 0;

/// スキンの静的 BindGroupLayout が使う storage の本数（本体の renderer/mod.rs が要求する上限と同じ）。
const SKIN_STORAGE_BUFFERS: u32 = 12;

/// アダプタを名前（の一部）で選ぶ環境変数（例: `Intel`・`NVIDIA`）。無ければ本体と同じ選び方（独立 GPU を優先）。
const ADAPTER_ENV: &str = "SEED_SKIN_EQUIV_ADAPTER";

/// アダプタの優先順（本体の renderer/mod.rs の select_adapter と同じ: 独立 GPU > 内蔵 GPU > その他）。
/// 同じ種類なら Vulkan を先にする（本体の PC の既定のバックエンド）。
fn adapter_rank(info: &wgpu::AdapterInfo) -> (u8, u8) {
    let device = match info.device_type {
        wgpu::DeviceType::DiscreteGpu => 3,
        wgpu::DeviceType::IntegratedGpu => 2,
        wgpu::DeviceType::VirtualGpu => 1,
        _ => 0,
    };
    (device, u8::from(info.backend == wgpu::Backend::Vulkan))
}

/// 比べるアダプタを選ぶ（`wanted` があれば名前〈の一部・大文字小文字を問わない〉で、無ければ本体と同じ優先順）。
fn pick_adapter(instance: &wgpu::Instance, wanted: Option<&str>) -> Option<wgpu::Adapter> {
    let mut adapters = instance.enumerate_adapters(wgpu::Backends::all());
    if let Some(wanted) = wanted {
        let wanted = wanted.to_lowercase();
        adapters.retain(|a| a.get_info().name.to_lowercase().contains(&wanted));
    }
    adapters.sort_by_key(|a| std::cmp::Reverse(adapter_rank(&a.get_info())));
    adapters.into_iter().next()
}

/// 列優先の 4x4 行列（WGSL の `mat4x4<f32>` と同じ並び: `m[列][行]`）。
pub(super) type Mat4 = [[f32; 4]; 4];

/// 新旧の出力を比べた結果。
#[derive(Debug, Default)]
pub(super) struct Comparison {
    /// 比べた行列の数（インスタンス × MAX_JOINTS 枠）。
    pub matrices: usize,
    /// 1 要素でもビットが違った行列の数。
    pub mismatched_matrices: usize,
    /// 違った要素の最大の ULP 差（同じなら 0）。
    pub max_ulp: u64,
    /// 両方とも NaN だった要素の数（同じ計算なら同じ所で NaN になる。参考）。
    pub both_nan: usize,
    /// 最初に見つけた違い（場所と値）。
    pub first_mismatch: Option<String>,
}

impl Comparison {
    /// 別の比較結果を足し込む（モデルを跨いだ合計）。
    pub fn absorb(&mut self, other: Comparison) {
        self.matrices += other.matrices;
        self.mismatched_matrices += other.mismatched_matrices;
        self.max_ulp = self.max_ulp.max(other.max_ulp);
        self.both_nan += other.both_nan;
        if self.first_mismatch.is_none() {
            self.first_mismatch = other.first_mismatch;
        }
    }
}

/// 実 GPU のデバイスと、新旧のパイプライン。
pub(super) struct SkinGpu {
    device: wgpu::Device,
    queue: wgpu::Queue,
    /// 新しい作り（本番と同じ生成）。
    pipeline: SkinComputePipeline,
    /// 旧い作りの写し（BindGroupLayout は新しい作りのものを使う＝同じバインドグループをそのまま差せる）。
    legacy: wgpu::ComputePipeline,
    /// 頂点シェーダ用のジョイントの BindGroupLayout（SkinComputeSystem の生成に要る。比べるのには使わない）。
    joint_bgl: wgpu::BindGroupLayout,
    /// 使ったアダプタの名前（ログ用）。
    pub adapter_name: String,
}

impl SkinGpu {
    /// アダプタ・デバイスを用意する（環境変数 SEED_SKIN_EQUIV_ADAPTER があれば名前で選ぶ。無い環境では None＝呼び出し側がスキップする）。
    pub fn new() -> Option<Self> {
        Self::with_adapter(std::env::var(ADAPTER_ENV).ok().as_deref())
    }

    /// 名前（の一部）でアダプタを選んで用意する（None なら本体と同じ優先順）。
    pub fn with_adapter(wanted: Option<&str>) -> Option<Self> {
        let instance = wgpu::Instance::new(&wgpu::InstanceDescriptor::default());
        let adapter = pick_adapter(&instance, wanted)?;
        let info = adapter.get_info();
        let adapter_name = format!("{} ({:?}, {:?}, driver {} {})", info.name, info.backend, info.device_type, info.driver, info.driver_info);
        let (device, queue) = pollster::block_on(adapter.request_device(&wgpu::DeviceDescriptor {
            required_limits: wgpu::Limits {
                max_storage_buffers_per_shader_stage: SKIN_STORAGE_BUFFERS,
                ..wgpu::Limits::default()
            },
            ..Default::default()
        }))
        .ok()?;

        let pipeline = SkinComputePipeline::new(&device, None);
        let legacy_module = device.create_shader_module(wgpu::ShaderModuleDescriptor {
            label: Some("Legacy Skin Compute Shader (test)"),
            source: wgpu::ShaderSource::Wgsl(include_str!("legacy_skin_compute.wgsl").into()),
        });
        let legacy_layout = device.create_pipeline_layout(&wgpu::PipelineLayoutDescriptor {
            label: Some("Legacy Skin Compute Layout (test)"),
            bind_group_layouts: &[&pipeline.per_frame_bgl, &pipeline.static_bgl, &pipeline.output_bgl],
            push_constant_ranges: &[],
        });
        let legacy = device.create_compute_pipeline(&wgpu::ComputePipelineDescriptor {
            label: Some("Legacy Skin Compute Pipeline (test)"),
            layout: Some(&legacy_layout),
            module: &legacy_module,
            entry_point: Some("cs_main"),
            compilation_options: Default::default(),
            cache: None,
        });
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
        Some(Self { device, queue, pipeline, legacy, joint_bgl, adapter_name })
    }

    /// 同じ再生指定で旧い作りと新しい作りを走らせ、(旧, 新) のジョイント行列（インスタンス × MAX_JOINTS 枠）を返す。
    ///
    /// モデルにスキンかアニメが無ければ None（SkinComputeSystem を作れない）。
    pub fn run_both(&self, model: &Model, poses: &[Option<SkinAnimPose>]) -> Option<(Vec<Mat4>, Vec<Mat4>)> {
        Some((self.run(model, poses, Implementation::Legacy)?, self.run(model, poses, Implementation::New)?))
    }

    /// 1 つの作りで走らせて、ジョイント行列（インスタンス × MAX_JOINTS 枠）を読み戻す。
    ///
    /// 呼ぶたびに SkinComputeSystem を作り直す（出力は 0 で始まる別のバッファ。前の結果が未書き込みを隠さない）。
    pub fn run(&self, model: &Model, poses: &[Option<SkinAnimPose>], which: Implementation) -> Option<Vec<Mat4>> {
        self.run_in_one_pass(&[(model, poses)], which)?.pop()
    }

    /// 複数のモデル（それぞれの再生指定）を、本体と同じく **1 つの ComputePass にまとめて** dispatch し、
    /// モデルごとのジョイント行列を読み戻す（本体の frame_renderer.rs は全モデル・全 LOD のスキニングを
    /// 1 つのパスに積むので、パスの中で並んで走る形でも結果が同じかを確かめるため）。
    ///
    /// どれかのモデルにスキンかアニメが無ければ None。
    pub fn run_in_one_pass(
        &self,
        jobs: &[(&Model, &[Option<SkinAnimPose>])],
        which: Implementation,
    ) -> Option<Vec<Vec<Mat4>>> {
        // モデルごとに SkinComputeSystem（入力・出力のバッファ）と再生指定を用意する。
        let mut systems = Vec::with_capacity(jobs.len());
        for (model, poses) in jobs {
            let instances = u32::try_from(poses.len()).expect("インスタンス数は u32 に収まる");
            let system = SkinComputeSystem::new(&self.device, model, instances, &self.pipeline, &self.joint_bgl)?;
            let compact: Vec<usize> = (0..poses.len()).collect();
            system.upload_lod_poses(&self.queue, LOD, &compact, poses);
            systems.push((system, instances));
        }
        // 旧い作りの出力の BindGroup（SkinComputeSystem の出力 BG は非公開なので、同じレイアウトで作り直す）。
        let legacy_out_bgs: Vec<wgpu::BindGroup> = systems
            .iter()
            .map(|(system, _)| {
                self.device.create_bind_group(&wgpu::BindGroupDescriptor {
                    label: Some("Legacy Skin Out BG (test)"),
                    layout: &self.pipeline.output_bgl,
                    entries: &[wgpu::BindGroupEntry {
                        binding: 0,
                        resource: system.jmat_buffer(LOD).expect("LOD0 のジョイント行列バッファ").as_entire_binding(),
                    }],
                })
            })
            .collect();

        let mut encoder = self.device.create_command_encoder(&wgpu::CommandEncoderDescriptor::default());
        {
            let mut pass = encoder.begin_compute_pass(&wgpu::ComputePassDescriptor::default());
            for ((system, instances), out_bg) in systems.iter().zip(&legacy_out_bgs) {
                match which {
                    // 新しい作り: 本番の dispatch（1 ワークグループ = 1 インスタンス）。
                    Implementation::New => system.dispatch_lod(&mut pass, &self.pipeline, LOD, *instances),
                    // 旧い作り: 1 スレッド = 1 インスタンスを 64 スレッドのワークグループで（旧い dispatch_lod と同じ数え方）。
                    Implementation::Legacy => {
                        pass.set_pipeline(&self.legacy);
                        pass.set_bind_group(0, &system.lod_per_frame_bgs[LOD], &[]);
                        pass.set_bind_group(1, &system.static_bg, &[]);
                        pass.set_bind_group(2, out_bg, &[]);
                        pass.dispatch_workgroups(instances.div_ceil(LEGACY_WORKGROUP_SIZE), 1, 1);
                    }
                }
            }
        }
        // モデルごとに読み戻し用のバッファへ写す。
        let readbacks: Vec<wgpu::Buffer> = systems
            .iter()
            .map(|(system, instances)| {
                let bytes = u64::from(*instances) * MAX_JOINTS as u64 * std::mem::size_of::<Mat4>() as u64;
                let readback = self.device.create_buffer_tracked(&wgpu::BufferDescriptor {
                    label: Some("skin equivalence readback"),
                    size: bytes,
                    usage: wgpu::BufferUsages::COPY_DST | wgpu::BufferUsages::MAP_READ,
                    mapped_at_creation: false,
                });
                let jmats = system.jmat_buffer(LOD).expect("LOD0 のジョイント行列バッファ");
                encoder.copy_buffer_to_buffer(jmats, 0, &readback, 0, bytes);
                readback
            })
            .collect();
        self.queue.submit([encoder.finish()]);

        for readback in &readbacks {
            readback.slice(..).map_async(wgpu::MapMode::Read, |_| {});
        }
        self.device.poll(wgpu::PollType::Wait).expect("GPU の完了待ち");
        let outputs = readbacks
            .iter()
            .map(|readback| {
                let data = readback.slice(..).get_mapped_range();
                let mats: Vec<Mat4> = bytemuck::cast_slice::<u8, Mat4>(&data).to_vec();
                drop(data);
                readback.unmap();
                mats
            })
            .collect();
        Some(outputs)
    }
}

/// どちらの作りで走らせるか。
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub(super) enum Implementation {
    /// 旧い作りの写し（legacy_skin_compute.wgsl。1 スレッド = 1 インスタンス）。
    Legacy,
    /// 新しい作り（本番の skin_compute.wgsl。1 ワークグループ = 1 インスタンス）。
    New,
}

/// 浮動小数を「数の並びの順」の整数へ写す（ULP の差を測るため。±0 は同じ値になる）。
fn ordered_bits(x: f32) -> i64 {
    let bits = x.to_bits() as i32;
    if bits < 0 {
        i64::from(i32::MIN) - i64::from(bits)
    } else {
        i64::from(bits)
    }
}

/// 旧い作りと新しい作りの出力をビット単位で比べる（`label` は違いの報告に付ける名前）。
pub(super) fn compare(label: &str, legacy: &[Mat4], new: &[Mat4]) -> Comparison {
    assert_eq!(legacy.len(), new.len(), "{label}: 出力の長さが違う");
    let mut result = Comparison { matrices: legacy.len(), ..Comparison::default() };
    for (index, (old_m, new_m)) in legacy.iter().zip(new).enumerate() {
        let mut differs = false;
        for (old_col, new_col) in old_m.iter().zip(new_m) {
            for (&a, &b) in old_col.iter().zip(new_col) {
                if a.is_nan() && b.is_nan() {
                    result.both_nan += 1;
                    continue;
                }
                if a.to_bits() != b.to_bits() {
                    differs = true;
                    result.max_ulp = result.max_ulp.max((ordered_bits(a) - ordered_bits(b)).unsigned_abs());
                }
            }
        }
        if differs {
            result.mismatched_matrices += 1;
            if result.first_mismatch.is_none() {
                result.first_mismatch = Some(format!(
                    "{label}: インスタンス {} のジョイント {}: 旧 {old_m:?} / 新 {new_m:?}",
                    index / MAX_JOINTS,
                    index % MAX_JOINTS
                ));
            }
        }
    }
    result
}

#[cfg(test)]
mod tests {
    use super::*;

    /// ULP の写像: 隣り合う浮動小数は 1、±0 は 0、符号を跨いでも並びどおりの距離になる。
    #[test]
    fn ordered_bits_measures_ulp_distance() {
        let one = 1.0f32;
        let next = f32::from_bits(one.to_bits() + 1);
        assert_eq!((ordered_bits(next) - ordered_bits(one)).unsigned_abs(), 1);
        assert_eq!(ordered_bits(0.0), ordered_bits(-0.0));
        let tiny = f32::from_bits(1);
        assert_eq!((ordered_bits(tiny) - ordered_bits(-tiny)).unsigned_abs(), 2);
    }

    /// 比べる関数: 同じならすべて 0、1 要素だけ違えばその行列 1 個と ULP の差を数える。
    #[test]
    fn compare_counts_bitwise_differences() {
        let identity: Mat4 = [[1.0, 0.0, 0.0, 0.0], [0.0, 1.0, 0.0, 0.0], [0.0, 0.0, 1.0, 0.0], [0.0, 0.0, 0.0, 1.0]];
        let same = compare("same", &[identity; 3], &[identity; 3]);
        assert_eq!((same.matrices, same.mismatched_matrices, same.max_ulp), (3, 0, 0));
        let mut nudged = identity;
        nudged[3][0] = f32::from_bits(nudged[3][0].to_bits() + 2);
        let diff = compare("diff", &[identity, identity], &[identity, nudged]);
        assert_eq!((diff.mismatched_matrices, diff.max_ulp), (1, 2));
        assert!(diff.first_mismatch.is_some_and(|m| m.contains("インスタンス 0 のジョイント 1")));
    }
}
