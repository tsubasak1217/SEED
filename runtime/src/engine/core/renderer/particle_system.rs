// ============================================================
//  particle_system.rs — GPU パーティクルのシミュレーション＋描画システム（拡張版）
//
//  ECS の ParticleEmitterComponent（データのみ）を入力に、GPU 上で
//  パーティクルをシミュレート（compute）して描画（形状メッシュ×インスタンス）する。
//
//  【ライフサイクル（1 フレーム）】
//    1. collect_and_consume()  … CPU 側。シーンからエミッタを収集し、放出個数を
//       決定してリングカーソルを進め、pending_burst（スクリプトの Burst 要求）を
//       消費する。プリウォーム（初回起動時の一括経過）のステップ列もここで作る。
//       デバイス不要（World への &mut のみ）。
//    2. sync_gpu()             … GPU 側。エミッタごとの GPU バッファ／バインドグループを
//       確保・更新し、カーブ LUT を（世代変化時のみ）焼き直し、パラメータ uniform を
//       書き込む（テクスチャ／モデル形状の差し替え検知含む）。
//    3. dispatch()             … compute pass 内で全エミッタをディスパッチ
//       （プリウォームステップがあれば先に順次ディスパッチする）。
//    4. draw()                 … render pass 内で全エミッタをブレンド別に
//       形状メッシュ＋インスタンスで描画する。
//
//  【スポーン方式：リングカーソル（atomic なし）】
//    CPU が spawn_cursor（= ring_start）と emit_count を uniform で渡す。compute の
//    各スレッド（slot index i）はリング区間 [ring_start, ring_start+emit_count) に
//    自分が入っていれば無条件で再スポーンする（過剰放出時は生存粒子を上書き＝標準的な
//    リング挙動）。詳細は particle_sim.wgsl を参照。
//
//  【放出制御（emit_interval / particles_per_emit / emit_mode / initial_delay）】
//    毎フレーム interval_accum に経過秒を積み、emit_interval を跨ぐたびに
//    particles_per_emit 個を放出する。emit_mode（Loop/Once/Count）は累計放出数で
//    打ち切る。initial_delay は playing 立ち上がり時にカウントダウンして消化する。
//
//  【プリウォーム】
//    playing 立ち上がり時に prewarm_time > 0 なら、固定 dt=1/60 の K ステップ
//    （K は MAX_PREWARM_STEPS でクランプ）を同一フレームで一括 compute 実行する。
//    各ステップは独自の uniform 値が必要なため「ステップごとの一時 uniform バッファ＋
//    バインドグループ」を作って順次ディスパッチする（queue.write_buffer は submit 前に
//    まとめて実行されるため、同一バッファへの多重書き込みでは各ステップの値を
//    参照できない。一時バッファ方式が正しい）。一時リソースは次フレームの sync_gpu で破棄。
//
//  【カーブ LUT】
//    speed / rot_speed / color(HSVA) / scale(xyz) / random_color_0..N-1 の各カーブを
//    CURVE_LUT_SAMPLES 行の vec4 に CPU で焼き、1 本の storage buffer に連結して
//    compute / draw の両方にバインドする。行オフセットはシェーダが lut_samples から
//    計算する（speed=0, rot_speed=S, color=2S, scale=3S, random_color_j=(4+j)S）。
//    再焼きはコンポーネントの curve_generation（IPC のカーブ差し替えで進む）が
//    前回焼いた世代と異なるときのみ行う。
//
//  【空間シム】
//    World: スポーン位置・方向をエミッタ行列で変換して固定（エミッタ移動に追従しない）。
//    Local: ローカルでシムし、描画時にワールド行列で変換（エミッタに追従）。
//
//  【孤児パーティクル（エミッタ消滅後の生存）】
//    ParticleEmitterComponent の削除やアクタ破棄でエミッタが消えても、発射済みの
//    パーティクルはエミッタから独立した存在として寿命が尽きるまで生き続ける。
//    エミッタが collect で見つからなくなったフレームに、その GPU リソース
//    （EmitterGpuState）の所有権を「孤児プール（orphans）」へ移譲する。孤児は
//    emit_count=0（放出停止）のまま compute と描画を継続し、TTL（= 削除時点で
//    存在しうる粒子の最大寿命 + 余裕）が尽きたら解放する。生存数の判定に GPU→CPU
//    readback は一切使わない（毎フレーム同期ゼロ）。
//    孤児は削除時点のワールド行列を凍結して保持するため、元エミッタの Transform には
//    追従しない（Local 空間シムの粒子もワールド空間上のその場に留まる）。
//    シーン切り替え・プレイ停止時は clear_all() で孤児ごと即時解放する。
//
//  【追加コストゼロ（受入条件）】
//    エミッタが 1 つも無いフレームは collect で frame が空になり、sync_gpu / dispatch /
//    draw はすべて即 return する。バッファ確保も一切行わない。
//
//  ※ GPU 構造体（GpuParticle / GpuEmitterParams）のバイトレイアウトは WGSL
//    （particle_sim.wgsl / particle_draw.wgsl）と厳密に一致させること。末尾の
//    layout_tests がサイズ・オフセットを固定値で検証する。
// ============================================================

use std::collections::{HashMap, HashSet};

use bytemuck::{Pod, Zeroable};
use wgpu::util::DeviceExt;

use crate::engine::components::particle_emitter_component::{
    CURVE_LUT_SAMPLES, DIRECTION_RANDOMNESS_MAX_HALF_ANGLE_DEG, EmitMode, MAX_PARTICLE_TEXTURES,
    MAX_PARTICLES_PER_EMITTER, ParamCurve, ParticleBlend, ParticleEmitterComponent, ParticleShape,
    ParticleSimSpace, SpawnVolume,
};
use crate::engine::components::{ComponentKind, Transform};
use crate::engine::ecs::{Entity, World};
use crate::engine::structs::objects::Actor;

use super::particle_shapes::{ShapeMesh, ShapeMeshCache};
use super::pipeline::{ParticleComputePipeline, ParticlePipelines};

// ─── 定数（マジックナンバー禁止）──────────────────────────────

/// compute のワークグループサイズ（particle_sim.wgsl の @workgroup_size と一致）。
const WORKGROUP_SIZE: u32 = 64;

/// 1 パーティクルのバイトストライド（std430・16 の倍数）。WGSL の Particle と一致。
///   pos(12)+age(4) / vel(12)+lifetime(4) / emit_dir(12)+base_speed(4) /
///   seed(4)+rot_angle(4)+pad(8) = 64。
pub const PARTICLE_STRIDE: u32 = 64;

/// max_particles の下限（0 だと dispatch/バッファが不正になるため 1 にクランプ）。
const MIN_PARTICLES: u32 = 1;

/// プリウォームの固定ステップ時間（秒）。60fps 相当の 1/60 固定。
const PREWARM_FIXED_DT: f32 = 1.0 / 60.0;

/// プリウォームの最大ステップ数（prewarm_time が大きくても一括実行はここまで）。
const MAX_PREWARM_STEPS: u32 = 600;

/// 形状モード（GpuEmitterParams.shape_mode）: メッシュ（Sphere / Box / Plane / Model）。
/// Pixel は専用の PointList パイプラインで描くため shape_mode は常に mesh でよい
/// （draw シェーダは現状 shape_mode を参照しないが、レイアウト維持のため保持する）。
const SHAPE_MODE_MESH: u32 = 1;

/// LUT に固定で並ぶカーブ本数（speed / rot_speed / scale）。
/// 色カーブ（最低 1 本）はこの後ろに可変本数で続く。
const LUT_FIXED_CURVES: usize = 3;

/// 孤児パーティクル（エミッタ消滅後も残る粒子群）の TTL に足す余裕秒。
///
/// エミッタが消えた瞬間に放出されていた粒子の残り寿命は最大でも lifetime_max。
/// TTL = lifetime_max + この余裕 とすることで、収集フレームと dispatch フレームの
/// 1 フレームずれや dt の丸めを吸収しつつ、確実に「全滅後」に解放できる。
/// GPU からの readback（毎フレーム GPU→CPU 同期）を一切行わずに解放時刻を決めるための値。
const ORPHAN_TTL_MARGIN_SECS: f32 = 0.25;

// ─── GpuParticle（storage 要素・std430, 64 バイト）────────────

/// GPU パーティクル 1 個（compute が read_write、描画が read）。
///
/// zeroed = 全 dead（lifetime=0）。生成時ゼロ初期化で初期状態は全 dead になる。
/// 全パディングを明示し bytemuck Pod の要件（隙間なし）を満たす。
///
/// 【速度モデル】射出速度（emit_dir×base_speed×speed_curve(t)）と、
/// 重力／空気抵抗を積分する蓄積速度 vel を分離して持つ（particle_sim.wgsl 参照）。
#[repr(C)]
#[derive(Copy, Clone, Pod, Zeroable)]
pub struct GpuParticle {
    /// 位置（World or Local, sim_space による）。offset 0
    pub pos: [f32; 3],
    /// 経過秒。offset 12
    pub age: f32,
    /// 蓄積速度（重力／抵抗のみ。スポーン時 0）。offset 16
    pub vel: [f32; 3],
    /// 寿命秒（<=0 は dead）。offset 28
    pub lifetime: f32,
    /// 正規化射出方向（World or Local）。offset 32
    pub emit_dir: [f32; 3],
    /// 基準初速（速度カーブ変調前）。offset 44
    pub base_speed: f32,
    /// スポーン時の乱数シード（描画がサイズ／回転軸／色選択の再現に使う）。offset 48
    pub seed: u32,
    /// 蓄積回転角（ラジアン）。offset 52
    pub rot_angle: f32,
    /// 16 バイト境界パディング。offset 56
    pub _pad: [u32; 2],
}

// ─── GpuEmitterParams（uniform, 192 バイト）───────────────────

/// エミッタパラメータ uniform（compute と描画で共有）。
///
/// WGSL の std140 uniform レイアウトに一致させるため、vec3 の直後にスカラーを
/// 詰めて 4 番目の要素スロットを埋めている（各 vec3 は 16 バイト境界）。
/// repr(C) の自然オフセットが std140 のオフセットと一致する（layout_tests で固定）。
#[repr(C)]
#[derive(Copy, Clone, Pod, Zeroable)]
pub struct GpuEmitterParams {
    /// エミッタのワールド行列（列優先＝CPU 行優先を転置済み）。offset 0
    pub world_mat: [[f32; 4]; 4],
    /// このステップの経過秒。offset 64
    pub dt: f32,
    /// このステップの放出個数。offset 68
    pub emit_count: u32,
    /// リングカーソル開始スロット。offset 72
    pub ring_start: u32,
    /// プール容量。offset 76
    pub max_particles: u32,
    /// フレーム固有ノンス（乱数系列の変化用）。offset 80
    pub frame_nonce: u32,
    /// 空気抵抗係数。offset 84
    pub drag: f32,
    /// 放出円錐の半頂角（ラジアン）＝ direction_randomness×180° を rad 化。offset 88
    pub spread_rad: f32,
    /// 形状モード（0=billboard / 1=mesh）。offset 92
    pub shape_mode: u32,
    /// ローカル放出方向。offset 96（vec3・16 バイト境界）
    pub direction_local: [f32; 3],
    /// 初速 min。offset 108（direction_local の 4 番目スロットに同居）
    pub speed_min: f32,
    /// 初速 max。offset 112
    pub speed_max: f32,
    /// 寿命 min。offset 116
    pub lifetime_min: f32,
    /// 寿命 max。offset 120
    pub lifetime_max: f32,
    /// 回転速度 min（rad/s。deg/s から CPU で変換）。offset 124
    pub rot_speed_min: f32,
    /// 重力加速度。offset 128（vec3・16 バイト境界）
    pub gravity: [f32; 3],
    /// 回転速度 max（rad/s）。offset 140（gravity の 4 番目スロットに同居）
    pub rot_speed_max: f32,
    /// スポーン Box の half extents。offset 144（vec3・16 バイト境界）
    pub spawn_box: [f32; 3],
    /// スポーン Sphere の半径。offset 156（spawn_box の 4 番目スロットに同居）
    pub spawn_sphere_radius: f32,
    /// 全体サイズ倍率 min（size_range[0]）。offset 160
    pub size_min: f32,
    /// 全体サイズ倍率 max（size_range[1]）。offset 164
    pub size_max: f32,
    /// スポーン体積コード（0=Point / 1=Box / 2=Sphere）。offset 168
    pub spawn_volume: u32,
    /// シミュレーション空間（0=World / 1=Local）。offset 172
    pub sim_space: u32,
    /// テクスチャ使用フラグ（1=テクスチャ / 0=プロシージャル円）。offset 176
    pub use_texture: u32,
    /// カーブ LUT のサンプル数（CURVE_LUT_SAMPLES）。offset 180
    pub lut_samples: u32,
    /// 色カーブ本数（最低 1。パーティクルごとに 1 本を seed で選ぶ）。offset 184
    pub color_count: u32,
    /// 初期回転角 min（ラジアン。度→rad は CPU で変換）。offset 188
    pub initial_rot_min: f32,
    /// 初期回転角 max（ラジアン）。offset 192
    pub initial_rot_max: f32,
    /// テクスチャ配列レイヤ数（0/1=単層。粒子ごとに seed で選ぶ）。offset 196
    pub tex_layer_count: u32,
    /// パディング（16 バイト境界）。offset 200/204
    pub _pad0: u32,
    pub _pad1: u32,
}

// ─── CPU 側の永続状態（エミッタごと・デバイス不要）────────────

/// エミッタ 1 個の CPU 側永続状態（放出アキュムレータ・カーソル等）。
struct EmitterCpuState {
    /// 放出間隔アキュムレータ（経過秒を積み、emit_interval を跨ぐたびに放出）。
    interval_accum: f32,
    /// 次の放出開始スロット（リングカーソル）。常に < max。
    spawn_cursor: u32,
    /// 前フレームの playing（立ち上がりエッジ検出用。初期 false）。
    prev_playing: bool,
    /// これまでの累計放出数（emit_mode の Once / Count の打ち切り判定用）。
    emitted_total: u64,
    /// initial_delay の残り秒（playing 立ち上がりでセットし、消化してから放出開始）。
    delay_left: f32,
}

impl EmitterCpuState {
    fn new() -> Self {
        Self {
            interval_accum: 0.0,
            spawn_cursor: 0,
            prev_playing: false,
            emitted_total: 0,
            delay_left: 0.0,
        }
    }
}

// ─── GPU 側の状態（エミッタごと・バッファ／バインドグループ）──

/// エミッタ 1 個の GPU 側状態（バッファ・バインドグループ・テクスチャ・LUT）。
struct EmitterGpuState {
    /// パーティクルプール（STORAGE|COPY_DST, max*PARTICLE_STRIDE, 生成時ゼロ＝全 dead）。
    ///
    /// compute_bg / draw_bg のバッキングとして生存させる必要があるため保持する
    /// （プリウォーム用の一時 BG 生成でも参照する）。
    particle_buf: wgpu::Buffer,
    /// エミッタパラメータ uniform（UNIFORM|COPY_DST, 192 バイト）。
    params_buf: wgpu::Buffer,
    /// カーブ LUT（STORAGE|COPY_DST, vec4 × (4+N)*CURVE_LUT_SAMPLES）。
    lut_buf: wgpu::Buffer,
    /// LUT の行数（バッファ再確保の判定用）。
    lut_rows: usize,
    /// 前回 LUT を焼いたカーブ世代（None は未焼き）。IPC のカーブ差し替えで
    /// コンポーネントの curve_generation が進むと再焼きする。
    lut_generation: Option<u64>,
    /// compute 用バインドグループ（group0: particles rw + params + lut）。
    compute_bg: wgpu::BindGroup,
    /// 描画用バインドグループ（group1: particles ro + params + lut）。
    draw_bg: wgpu::BindGroup,
    /// テクスチャバインドグループ（group2, texture_2d_array）。未ロード時 None（既定白を使う）。
    texture_bg: Option<wgpu::BindGroup>,
    /// 現在ロード済みのテクスチャパス列（差し替え検知用）。
    texture_paths: Vec<String>,
    /// ロード済みテクスチャ配列のレイヤ数（描画の tex_layer_count 用）。
    tex_layer_count: u32,
    /// particle_buf の確保容量（max_particles 変更時の再確保判定）。
    buf_capacity: u32,
    /// プリウォーム用の一時リソース（ステップごとの uniform バッファ＋compute BG）。
    ///
    /// dispatch() が順次ディスパッチした後、次フレームの sync_gpu 冒頭で破棄する。
    /// 各ステップは独自の uniform 値が必要なため一時バッファを分ける
    /// （同一バッファへの write_buffer 多重書き込みは submit 時に最後の値しか残らない）。
    prewarm: Vec<(wgpu::Buffer, wgpu::BindGroup)>,
}

impl EmitterGpuState {
    /// 指定容量＋LUT データでバッファ・バインドグループ一式を新規生成する
    /// （particle_buf はゼロ初期化＝全 dead）。
    fn create(
        device: &wgpu::Device,
        compute_pl: &ParticleComputePipeline,
        draw_pl: &ParticlePipelines,
        capacity: u32,
        lut_data: &[[f32; 4]],
        generation: u64,
    ) -> Self {
        // パーティクルプールを 0 埋めして確保（全 dead 初期状態を保証する）。
        let byte_size = capacity as usize * PARTICLE_STRIDE as usize;
        let zeros = vec![0u8; byte_size];
        let particle_buf = device.create_buffer_init(&wgpu::util::BufferInitDescriptor {
            label: Some("Particle Pool Buffer"),
            contents: &zeros,
            usage: wgpu::BufferUsages::STORAGE | wgpu::BufferUsages::COPY_DST,
        });

        // エミッタパラメータ uniform（毎フレーム write_buffer で更新）。
        let params_buf = device.create_buffer(&wgpu::BufferDescriptor {
            label: Some("Particle Emitter Params"),
            size: std::mem::size_of::<GpuEmitterParams>() as u64,
            usage: wgpu::BufferUsages::UNIFORM | wgpu::BufferUsages::COPY_DST,
            mapped_at_creation: false,
        });

        // カーブ LUT（初期データ入り。世代変化かつ同サイズなら write_buffer で更新）。
        let lut_buf = device.create_buffer_init(&wgpu::util::BufferInitDescriptor {
            label: Some("Particle Curve LUT"),
            contents: bytemuck::cast_slice(lut_data),
            usage: wgpu::BufferUsages::STORAGE | wgpu::BufferUsages::COPY_DST,
        });

        let (compute_bg, draw_bg) = Self::create_bind_groups(
            device,
            compute_pl,
            draw_pl,
            &particle_buf,
            &params_buf,
            &lut_buf,
        );

        Self {
            particle_buf,
            params_buf,
            lut_buf,
            lut_rows: lut_data.len(),
            lut_generation: Some(generation),
            compute_bg,
            draw_bg,
            texture_bg: None,
            texture_paths: Vec::new(),
            tex_layer_count: 0,
            buf_capacity: capacity,
            prewarm: Vec::new(),
        }
    }

    /// compute / draw のバインドグループを生成する（LUT バッファ再確保時にも呼ぶ）。
    fn create_bind_groups(
        device: &wgpu::Device,
        compute_pl: &ParticleComputePipeline,
        draw_pl: &ParticlePipelines,
        particle_buf: &wgpu::Buffer,
        params_buf: &wgpu::Buffer,
        lut_buf: &wgpu::Buffer,
    ) -> (wgpu::BindGroup, wgpu::BindGroup) {
        // compute BG（particles read_write + params + lut）。group0。
        let compute_bg = device.create_bind_group(&wgpu::BindGroupDescriptor {
            label: Some("Particle Compute BG"),
            layout: &compute_pl.bgl,
            entries: &[
                wgpu::BindGroupEntry {
                    binding: 0,
                    resource: particle_buf.as_entire_binding(),
                },
                wgpu::BindGroupEntry {
                    binding: 1,
                    resource: params_buf.as_entire_binding(),
                },
                wgpu::BindGroupEntry {
                    binding: 2,
                    resource: lut_buf.as_entire_binding(),
                },
            ],
        });
        // 描画 BG（particles read + params + lut）。group1。
        let draw_bg = device.create_bind_group(&wgpu::BindGroupDescriptor {
            label: Some("Particle Draw BG (group1)"),
            layout: &draw_pl.particle_bgl,
            entries: &[
                wgpu::BindGroupEntry {
                    binding: 0,
                    resource: particle_buf.as_entire_binding(),
                },
                wgpu::BindGroupEntry {
                    binding: 1,
                    resource: params_buf.as_entire_binding(),
                },
                wgpu::BindGroupEntry {
                    binding: 2,
                    resource: lut_buf.as_entire_binding(),
                },
            ],
        });
        (compute_bg, draw_bg)
    }
}

// ─── 孤児パーティクル（エミッタ消滅後も生き残る粒子群）──────────

/// 孤児化に必要なエミッタのスナップショット（シミュレーション継続と描画に要る最小情報）。
///
/// エミッタコンポーネントの削除・アクタ破棄の直前フレームの状態を焼き止めたもの。
/// `params.world_mat` もここで凍結されるため、孤児粒子は元エミッタの Transform に
/// 追従しない（World シムはそもそもワールド固定。Local シムの粒子も、削除時点の
/// 行列を保持し続けることでワールド空間上のその場に留まる）。
#[derive(Clone)]
struct OrphanSeed {
    /// 合成モード（描画パイプライン選択）。
    blend: ParticleBlend,
    /// 形状（描画のメッシュ解決に使う。メッシュは ShapeMeshCache に残っている）。
    shape: ParticleShape,
    /// プール容量（dispatch のワークグループ数・draw のインスタンス数）。
    max_particles: u32,
    /// 最後のフレームの GPU パラメータ（world_mat 凍結）。
    /// 孤児は毎フレーム `dt` のみ差し替え、`emit_count` は常に 0（＝新規放出しない）。
    params: GpuEmitterParams,
    /// この時点で存在しうる粒子の最大寿命（秒）。TTL の基準に使う。
    max_lifetime: f32,
}

/// 孤児パーティクル群（エミッタは消えたが寿命が残っている粒子のプール）。
///
/// エミッタが消えた時点で GPU リソース（`EmitterGpuState`）の所有権をここへ移譲する。
/// 放出は止め（emit_count=0）、シミュレーション（compute）と描画は寿命が尽きるまで継続する。
/// TTL が 0 以下になったら `Vec::retain` で drop され、GPU リソースが解放される
/// （生存数の GPU→CPU readback は行わない＝毎フレーム同期ゼロ）。
struct OrphanEmitter {
    /// エミッタから移譲された GPU リソース（バッファ・BindGroup・テクスチャ）。
    gpu: EmitterGpuState,
    /// 凍結したエミッタ状態（描画・パラメータ書き戻しに使う）。
    seed: OrphanSeed,
    /// 残り生存秒。collect_and_consume で dt ずつ減らし、0 以下で解放する。
    ttl: f32,
}

// ─── フレームごとの描画対象記述 ───────────────────────────────

/// このフレームに dispatch / draw する 1 エミッタの記述。
struct EmitterFrameDesc {
    /// エミッタスロットの entity（gpu マップのキー）。
    entity: Entity,
    /// 合成モード（描画パイプライン選択）。
    blend: ParticleBlend,
    /// プール容量（dispatch のワークグループ数・draw のインスタンス数）。
    max_particles: u32,
    /// テクスチャパス列（空＝プロシージャル白）。sync_gpu が差し替え検知に使う。
    texture_paths: Vec<String>,
    /// 形状（sync_gpu がメッシュ解決・Model ロードとフォールバック判定に使う）。
    shape: ParticleShape,
    /// カーブ世代（sync_gpu が LUT 再焼き判定に使う）。
    curve_generation: u64,
    /// LUT 焼き込み用のカーブ（clone。エミッタ数は少数前提で毎フレームの clone を許容）。
    curves: FrameCurves,
    /// プリウォームステップのパラメータ列（playing 立ち上がり時のみ非空）。
    prewarm_params: Vec<GpuEmitterParams>,
    /// GPU へ書き込むパラメータ（use_texture / shape_mode は sync_gpu で確定）。
    params: GpuEmitterParams,
}

/// LUT 焼き込みに必要なカーブ一式（フレーム記述に載せる clone）。
struct FrameCurves {
    speed: ParamCurve,
    rot_speed: ParamCurve,
    scale: ParamCurve,
    /// 色カーブリスト（HSVA、最低 1 本。パーティクルごとに 1 本を選ぶ）。
    colors: Vec<ParamCurve>,
}

impl FrameCurves {
    /// LUT へ焼く（連結順: speed / rot_speed / scale / color_0..M-1）。M=colors.len()。
    fn bake(&self) -> Vec<[f32; 4]> {
        let color_n = self.colors.len().max(1);
        let mut lut = Vec::with_capacity((LUT_FIXED_CURVES + color_n) * CURVE_LUT_SAMPLES);
        lut.extend(self.speed.bake_lut(CURVE_LUT_SAMPLES));
        lut.extend(self.rot_speed.bake_lut(CURVE_LUT_SAMPLES));
        lut.extend(self.scale.bake_lut(CURVE_LUT_SAMPLES));
        // 色カーブ（最低 1 本は保証されている前提。空なら白フェード相当の定数で埋める）。
        if self.colors.is_empty() {
            lut.extend(ParamCurve::default().bake_lut(CURVE_LUT_SAMPLES));
        } else {
            for c in &self.colors {
                lut.extend(c.bake_lut(CURVE_LUT_SAMPLES));
            }
        }
        lut
    }
}

// ─── ParticleSystem 本体 ──────────────────────────────────────

/// GPU パーティクルシステム（App が 1 つ保持）。
///
/// エミッタごとの CPU/GPU 状態を HashMap で保持し、シーンに存在しなくなった
/// エミッタは毎フレーム retain で破棄する。パイプライン等の静的リソースは
/// DrawPipelines（draw_ctx.pipelines）側が持つ（本体はバッファと状態のみ）。
pub struct ParticleSystem {
    /// エミッタ entity → CPU 永続状態。
    cpu: HashMap<Entity, EmitterCpuState>,
    /// エミッタ entity → GPU 状態（バッファ・BG）。
    gpu: HashMap<Entity, EmitterGpuState>,
    /// エミッタ entity → 直近フレームのスナップショット（孤児化の種）。
    /// sync_gpu が毎フレーム更新し、エミッタ消滅時に OrphanEmitter へ移す。
    seeds: HashMap<Entity, OrphanSeed>,
    /// 孤児パーティクル群（エミッタは消えたが寿命が残っている粒子のプール）。
    /// 放出は止め、シミュレーションと描画のみ TTL が尽きるまで継続する。
    orphans: Vec<OrphanEmitter>,
    /// 形状メッシュキャッシュ（組込み 4 種＋Model。デバイス取得後に遅延生成）。
    /// 孤児がメッシュ形状のときも参照するため、エミッタ消滅後も保持し続ける。
    shapes: Option<ShapeMeshCache>,
    /// このフレームの描画対象（collect_and_consume が毎フレーム再構築する）。
    frame: Vec<EmitterFrameDesc>,
    /// フレームカウンタ（frame_nonce の生成に使う。乱数系列をフレームで変える）。
    frame_counter: u64,
}

/// 収集時にシーンから抜き出すエミッタのスナップショット（World の借用を跨がないため）。
struct RawEmitter {
    entity: Entity,
    world_mat: [[f32; 4]; 4], // 行優先（CPU）
    max_particles: u32,
    shape: ParticleShape,
    spawn_volume: SpawnVolume,
    emit_mode: EmitMode,
    initial_delay: f32,
    prewarm_time: f32,
    emit_interval: f32,
    particles_per_emit: u32,
    pending_burst: u32,
    lifetime: [f32; 2],
    initial_speed: [f32; 2],
    direction_local: [f32; 3],
    direction_randomness: f32,
    gravity: [f32; 3],
    drag: f32,
    rot_speed_range: [f32; 2],
    initial_rotation_range: [f32; 2],
    size_range: [f32; 2],
    curves: FrameCurves,
    curve_generation: u64,
    texture_paths: Vec<String>,
    blend: ParticleBlend,
    sim_space: ParticleSimSpace,
    playing: bool,
}

impl Default for ParticleSystem {
    fn default() -> Self {
        Self::new()
    }
}

impl ParticleSystem {
    /// 空のシステムを生成する（デバイス不要。rt_pool と同じく eager 構築可能）。
    pub fn new() -> Self {
        Self {
            cpu: HashMap::new(),
            gpu: HashMap::new(),
            seeds: HashMap::new(),
            orphans: Vec::new(),
            shapes: None,
            frame: Vec::new(),
            frame_counter: 0,
        }
    }

    /// このフレームに sync/dispatch/draw すべき対象（エミッタ or 孤児）があるか
    /// （追加コストゼロ判定）。孤児＝エミッタは消えたが寿命が残っている粒子群。
    pub fn has_emitters(&self) -> bool {
        !self.frame.is_empty() || !self.orphans.is_empty()
    }

    /// 全パーティクル資源（エミッタ・孤児とも）を即時解放する。
    ///
    /// シーン切り替え・プレイ停止など「粒子をシーンを跨いで残してはいけない」場面で呼ぶ。
    /// 孤児プールも破棄するため、旧シーンの粒子が新シーンに残ることはない。
    pub fn clear_all(&mut self) {
        self.frame.clear();
        self.cpu.clear();
        self.gpu.clear();
        self.seeds.clear();
        self.orphans.clear();
    }

    /// プール容量からディスパッチするワークグループ数を求める（エミッタ／孤児で共用）。
    fn workgroup_count(max_particles: u32) -> u32 {
        max_particles.div_ceil(WORKGROUP_SIZE)
    }

    // ── フェーズ 1: CPU 収集＋放出決定＋pending_burst 消費 ──────

    /// シーンからエミッタを収集し、放出個数を決定してリングカーソルを進める。
    ///
    /// - `world`  : ECS ワールド（pending_burst を &mut で 0 に戻すため可変借用）。
    /// - `actors` : ルートアクター列（world_line フィルタ・DFS で走査）。
    /// - `wl`     : 対象ワールドライン。
    /// - `dt`     : このステップの経過秒（Play=可変 / Edit=固定 1/60。呼び出し側が決める）。
    ///
    /// デバイス不要。呼び出し側は本メソッドを描画ブロックの &mut 借用に入る前に呼ぶこと
    /// （world への &mut が必要なため）。
    pub fn collect_and_consume(&mut self, world: &mut World, actors: &[Actor], wl: u32, dt: f32) {
        // フレームリストを毎フレーム作り直す（前フレームの残骸を持ち越さない）。
        self.frame.clear();
        self.frame_counter = self.frame_counter.wrapping_add(1);

        // ① シーンを走査してエミッタのスナップショットを収集し、pending_burst を消費する。
        let mut raws: Vec<RawEmitter> = Vec::new();
        gather_emitters(world, actors, wl, &mut raws);

        // ② 各エミッタの放出個数を決定し、GPU パラメータを組む。
        let mut present: HashSet<Entity> = HashSet::with_capacity(raws.len());
        for raw in raws {
            present.insert(raw.entity);
            self.process_emitter(raw, dt);
        }

        // ③ 既存の孤児プールの時間を進め、寿命が尽きたものを解放する。
        self.age_orphans(dt);

        // ④ シーンから消えたエミッタの GPU リソースを孤児プールへ移譲する。
        //    （即破棄すると発射済みの粒子まで消えてしまうため。放出のみ止める）
        self.orphan_removed_emitters(&present);

        // ⑤ 消えたエミッタの CPU 状態・スナップショットを破棄する。
        //    GPU 状態は ④ で移譲済み（孤児にならなかったものは ④ 内で drop される）。
        self.cpu.retain(|e, _| present.contains(e));
        self.seeds.retain(|e, _| present.contains(e));
    }

    /// 孤児プールの時間を進め、TTL が尽きたものを解放する（GPU リソースを drop）。
    ///
    /// 生存粒子数の判定に GPU→CPU readback は使わない。エミッタ削除時点で存在しうる
    /// 粒子の最大寿命を TTL として持ち、経過したら「必ず全滅している」ことを根拠に解放する。
    fn age_orphans(&mut self, dt: f32) {
        for o in &mut self.orphans {
            o.ttl -= dt;
            // このステップの dt を反映する（シミュレーションは継続）。
            o.seed.params.dt = dt;
            // 孤児は新規放出しない（保険。移譲時にも 0 にしてある）。
            o.seed.params.emit_count = 0;
        }
        // TTL 切れを解放（EmitterGpuState の drop で GPU バッファ・BG・テクスチャが解放される）。
        self.orphans.retain(|o| o.ttl > 0.0);
    }

    /// シーンから消えたエミッタの GPU リソースを孤児プールへ移譲する。
    ///
    /// 「エミッタが消える」＝ ParticleEmitterComponent の削除・アクタ破棄・世界線切り替え。
    /// 発射済みの粒子はエミッタから独立した存在として扱い、寿命が尽きるまで
    /// シミュレーションと描画を継続する（新規の放出だけが止まる）。
    fn orphan_removed_emitters(&mut self, present: &HashSet<Entity>) {
        // 借用の都合で、消えた entity を先に列挙してから remove する。
        let gone: Vec<Entity> = self
            .gpu
            .keys()
            .filter(|e| !present.contains(e))
            .copied()
            .collect();

        for e in gone {
            // GPU 状態を取り出す（ここで self.gpu から外れる）。
            let Some(mut gpu) = self.gpu.remove(&e) else {
                continue;
            };
            // 直近フレームのスナップショットが無い＝一度も sync_gpu を通っていない
            // （＝GPU 上に粒子が 1 つも存在しない）ので、そのまま解放してよい。
            let Some(mut seed) = self.seeds.remove(&e) else {
                continue;
            };

            // プリウォームの一時リソースは孤児には不要（放出しない）。ここで解放する。
            gpu.prewarm.clear();
            // 放出停止（孤児は既存粒子の更新・描画のみ）。
            seed.params.emit_count = 0;

            // TTL = 削除時点で存在しうる粒子の最大寿命 ＋ 余裕。
            let ttl = seed.max_lifetime + ORPHAN_TTL_MARGIN_SECS;
            if seed.max_lifetime <= 0.0 {
                // 寿命 0 以下＝生存しうる粒子が無い。孤児化せず即解放する（gpu を drop）。
                continue;
            }
            self.orphans.push(OrphanEmitter { gpu, seed, ttl });
        }
    }

    /// 1 ステップぶんの放出個数を決定する（interval / delay / emit_mode を消化）。
    ///
    /// playing 中のエミッタに対して呼ぶ。initial_delay の残りがあれば先に消化し、
    /// 消化しきれた余り時間で interval を進める。emit_mode（Once/Count）の上限を
    /// 超えないよう打ち切る。戻り値は「このステップに放出する個数」（max へは
    /// 呼び出し側でクランプ）。
    fn step_emission(
        cpu: &mut EmitterCpuState,
        interval: f32,
        per_emit: u32,
        mode: EmitMode,
        max: u32,
        dt: f32,
    ) -> u32 {
        // initial_delay の消化（残りがステップより長ければ今回は放出なし）。
        let mut dt_eff = dt;
        if cpu.delay_left > 0.0 {
            if cpu.delay_left >= dt {
                cpu.delay_left -= dt;
                return 0;
            }
            dt_eff = dt - cpu.delay_left;
            cpu.delay_left = 0.0;
        }

        // per_emit=0 は何も放出しない設定（アキュムレータの空回りを避けて即 return）。
        if per_emit == 0 {
            return 0;
        }

        // 間隔アキュムレータ: emit_interval を跨ぐたびに particles_per_emit 個。
        let mut emits: u32 = 0;
        if interval > 0.0 {
            cpu.interval_accum += dt_eff.max(0.0);
            while cpu.interval_accum >= interval {
                cpu.interval_accum -= interval;
                emits = emits.saturating_add(per_emit);
                // 1 ステップでプール容量以上を放出しても上書きになるだけなので打ち切る
                // （アキュムレータは剰余で正規化して過剰な繰り越しも防ぐ）。
                if emits >= max {
                    cpu.interval_accum %= interval;
                    break;
                }
            }
        } else {
            // interval<=0 は「毎フレーム particles_per_emit 個」を意味する（0 除算回避）。
            emits = per_emit;
        }

        // emit_mode による累計上限（Loop=無制限 / Once=プール一巡 / Count=指定数）。
        let limit: u64 = match mode {
            EmitMode::Loop => u64::MAX,
            EmitMode::Once => max as u64,
            EmitMode::Count { total } => total as u64,
        };
        if cpu.emitted_total >= limit {
            return 0;
        }
        let remaining = (limit - cpu.emitted_total).min(u32::MAX as u64) as u32;
        emits.min(remaining)
    }

    /// 1 エミッタの放出個数・リングカーソル・パラメータを決定して frame へ積む。
    fn process_emitter(&mut self, raw: RawEmitter, dt: f32) {
        let max = raw
            .max_particles
            .clamp(MIN_PARTICLES, MAX_PARTICLES_PER_EMITTER);
        let counter = self.frame_counter;
        let cpu = self
            .cpu
            .entry(raw.entity)
            .or_insert_with(EmitterCpuState::new);

        // playing 立ち上がりエッジ（初回生成時 playing=true 含む。prev_playing 初期 false）。
        // initial_delay をセットし、Once/Count の累計・アキュムレータをリセットして
        // 「再生し直し」で再度放出できるようにする。
        let rising = raw.playing && !cpu.prev_playing;
        if rising {
            cpu.delay_left = raw.initial_delay.max(0.0);
            cpu.interval_accum = 0.0;
            cpu.emitted_total = 0;
        }
        cpu.prev_playing = raw.playing;

        // frame_nonce: フレームカウンタとエミッタ index を混ぜて系列を分離・変化させる。
        let frame_nonce = (counter as u32) ^ raw.entity.index().wrapping_mul(0x9E3779B9);

        // パラメータ共通部を作るローカルクロージャ（ステップごとに dt/emit/ring/nonce が変わる）。
        let make_params = |dt: f32, emit_count: u32, ring_start: u32, nonce: u32| {
            GpuEmitterParams {
                world_mat: transpose4x4(&raw.world_mat), // 行優先 → 列優先
                dt,
                emit_count,
                ring_start,
                max_particles: max,
                frame_nonce: nonce,
                drag: raw.drag,
                // direction_randomness（0..1）→ 半頂角: randomness×180 度 → ラジアン。
                spread_rad: (raw.direction_randomness.clamp(0.0, 1.0)
                    * DIRECTION_RANDOMNESS_MAX_HALF_ANGLE_DEG)
                    .to_radians(),
                // shape_mode は常に mesh（Pixel は専用 PointList パイプラインで描く）。
                shape_mode: SHAPE_MODE_MESH,
                direction_local: raw.direction_local,
                speed_min: raw.initial_speed[0],
                speed_max: raw.initial_speed[1],
                lifetime_min: raw.lifetime[0],
                lifetime_max: raw.lifetime[1],
                // 回転速度は度/秒 → ラジアン/秒 へ変換して渡す。
                rot_speed_min: raw.rot_speed_range[0].to_radians(),
                gravity: raw.gravity,
                rot_speed_max: raw.rot_speed_range[1].to_radians(),
                spawn_box: raw.spawn_volume.box_half_extents(),
                spawn_sphere_radius: raw.spawn_volume.sphere_radius(),
                size_min: raw.size_range[0],
                size_max: raw.size_range[1],
                spawn_volume: raw.spawn_volume.to_code(),
                sim_space: raw.sim_space.to_code(),
                // 仮の use_texture / tex_layer_count（sync_gpu がロード結果で確定する）。
                use_texture: if raw.texture_paths.is_empty() { 0 } else { 1 },
                lut_samples: CURVE_LUT_SAMPLES as u32,
                // 色カーブ本数（最低 1 を保証）。
                color_count: (raw.curves.colors.len().max(1)) as u32,
                // 初期回転角（度→ラジアン）。
                initial_rot_min: raw.initial_rotation_range[0].to_radians(),
                initial_rot_max: raw.initial_rotation_range[1].to_radians(),
                tex_layer_count: 0,
                _pad0: 0,
                _pad1: 0,
            }
        };

        // ── プリウォーム: playing 立ち上がり時に固定 1/60 の K ステップを一括生成する ──
        // 各ステップは通常フレームと同じ放出ロジック（delay / interval / mode）を回し、
        // リングカーソル・累計も進める（＝実時間で K/60 秒経過した状態を再現する）。
        let mut prewarm_params: Vec<GpuEmitterParams> = Vec::new();
        if rising && raw.prewarm_time > 0.0 {
            let steps =
                ((raw.prewarm_time / PREWARM_FIXED_DT).round() as u32).min(MAX_PREWARM_STEPS);
            prewarm_params.reserve(steps as usize);
            for step in 0..steps {
                let mut c = Self::step_emission(
                    cpu,
                    raw.emit_interval,
                    raw.particles_per_emit,
                    raw.emit_mode,
                    max,
                    PREWARM_FIXED_DT,
                );
                c = c.min(max);
                let ring = cpu.spawn_cursor % max;
                cpu.spawn_cursor = (cpu.spawn_cursor + c) % max;
                cpu.emitted_total = cpu.emitted_total.saturating_add(c as u64);
                // ステップごとに乱数系列を変える（同一 nonce だと同スロットの再抽選が同値になる）。
                let nonce = frame_nonce ^ (step + 1).wrapping_mul(0x85EBCA6B);
                prewarm_params.push(make_params(PREWARM_FIXED_DT, c, ring, nonce));
            }
        }

        // ── 通常ステップの放出個数 ──
        let mut count: u32 = 0;
        if raw.playing {
            count = Self::step_emission(
                cpu,
                raw.emit_interval,
                raw.particles_per_emit,
                raw.emit_mode,
                max,
                dt,
            );
        }

        // スクリプトの Burst(n)（pending_burst）は playing / emit_mode に関わらず
        // 消費・放出する（明示要求のため emit_mode の上限は適用しない。累計には加算する）。
        count = count.saturating_add(raw.pending_burst);

        // 1 フレームでプール容量を超えて放出しても意味がない（上書きになる）ためクランプ。
        count = count.min(max);

        let ring_start = cpu.spawn_cursor % max;
        cpu.spawn_cursor = (cpu.spawn_cursor + count) % max;
        cpu.emitted_total = cpu.emitted_total.saturating_add(count as u64);

        let params = make_params(dt, count, ring_start, frame_nonce);

        self.frame.push(EmitterFrameDesc {
            entity: raw.entity,
            blend: raw.blend,
            max_particles: max,
            texture_paths: raw.texture_paths,
            shape: raw.shape,
            curve_generation: raw.curve_generation,
            curves: raw.curves,
            prewarm_params,
            params,
        });
    }

    // ── フェーズ 2: GPU バッファ／BG 確保・LUT 焼き・パラメータ書き込み ──

    /// エミッタごとの GPU 状態を確保・更新し、パラメータ uniform を書き込む。
    ///
    /// - 容量（max_particles）変更時はプールを作り直す（ゼロ初期化＝全 dead）。
    /// - カーブ世代（curve_generation）変更時のみ LUT を再焼きする。
    /// - テクスチャパス変更時は texture_bg を再ロードする。
    /// - Model 形状はここでロード（失敗／頂点数超過は Point へフォールバック）。
    /// - use_texture / shape_mode はロード結果に応じて確定し、params を書き込む。
    /// - プリウォームステップがあれば一時 uniform＋BG を作る（dispatch が消費）。
    /// - 孤児（エミッタ消滅後の粒子群）の params も dt 更新のため書き戻す。
    ///
    /// エミッタも孤児も 0 個なら即 return（バッファ確保なし＝追加コストゼロ）。
    pub fn sync_gpu(
        &mut self,
        device: &wgpu::Device,
        queue: &wgpu::Queue,
        compute_pl: &ParticleComputePipeline,
        draw_pl: &ParticlePipelines,
    ) {
        if self.frame.is_empty() && self.orphans.is_empty() {
            return;
        }

        // 形状メッシュキャッシュを遅延生成する（初回のみ。以降は全エミッタで共有）。
        if self.shapes.is_none() {
            self.shapes = Some(ShapeMeshCache::new_builtins(device));
        }

        for i in 0..self.frame.len() {
            // frame[i] から必要な値をコピー／複製して以降の &mut self.gpu と競合させない。
            let entity = self.frame[i].entity;
            let capacity = self.frame[i].max_particles;
            let tex_paths = self.frame[i].texture_paths.clone();
            let shape = self.frame[i].shape.clone();
            let generation = self.frame[i].curve_generation;
            let mut params = self.frame[i].params;

            // ① 容量に合わせて GPU 状態を確保（新規 or 容量変更時に再生成）。
            let need_new = match self.gpu.get(&entity) {
                Some(g) => g.buf_capacity != capacity,
                None => true,
            };
            if need_new {
                let lut = self.frame[i].curves.bake();
                let g = EmitterGpuState::create(
                    device, compute_pl, draw_pl, capacity, &lut, generation,
                );
                self.gpu.insert(entity, g);
            }

            // ② カーブ世代が変わっていたら LUT を再焼きする（変更時のみ）。
            {
                let g = self.gpu.get_mut(&entity).expect("gpu state must exist");
                if g.lut_generation != Some(generation) {
                    let lut = self.frame[i].curves.bake();
                    if lut.len() == g.lut_rows {
                        // 同サイズ: バッファへ上書き（BG 再生成不要）。
                        queue.write_buffer(&g.lut_buf, 0, bytemuck::cast_slice(&lut));
                    } else {
                        // サイズ変更（random_color 本数の増減）: バッファ＋BG を作り直す。
                        g.lut_buf = device.create_buffer_init(&wgpu::util::BufferInitDescriptor {
                            label: Some("Particle Curve LUT"),
                            contents: bytemuck::cast_slice(&lut),
                            usage: wgpu::BufferUsages::STORAGE | wgpu::BufferUsages::COPY_DST,
                        });
                        g.lut_rows = lut.len();
                        let (cbg, dbg) = EmitterGpuState::create_bind_groups(
                            device,
                            compute_pl,
                            draw_pl,
                            &g.particle_buf,
                            &g.params_buf,
                            &g.lut_buf,
                        );
                        g.compute_bg = cbg;
                        g.draw_bg = dbg;
                    }
                    g.lut_generation = Some(generation);
                }
            }

            // ③ Model 形状のロードを試みる（成否は draw が model_cached で見て、
            //    未ロードなら Pixel パイプラインへフォールバックする）。
            if let ParticleShape::Model { path } = &shape {
                if !path.is_empty() {
                    let shapes = self.shapes.as_mut().expect("shape cache must exist");
                    let _ = shapes.model(device, path);
                }
            }

            // ④ テクスチャ配列の差し替え検知＆ロード、use_texture / tex_layer_count の確定。
            let mut use_texture = 0u32;
            let mut tex_layers = 0u32;
            if !tex_paths.is_empty() {
                let g = self.gpu.get_mut(&entity).expect("gpu state must exist");
                let changed = g.texture_paths != tex_paths || g.texture_bg.is_none();
                if changed {
                    let (bg, layers) = load_particle_textures(
                        device,
                        queue,
                        &tex_paths,
                        &draw_pl.tex_bgl,
                        &draw_pl.sampler,
                    );
                    g.texture_bg = bg;
                    g.tex_layer_count = layers;
                    g.texture_paths = tex_paths.clone();
                }
                if g.texture_bg.is_some() {
                    use_texture = 1;
                    tex_layers = g.tex_layer_count;
                }
            }

            // ⑤ 確定した use_texture / tex_layer_count を params と frame[i]（draw が参照）へ反映する。
            params.use_texture = use_texture;
            params.tex_layer_count = tex_layers;
            self.frame[i].params.use_texture = use_texture;
            self.frame[i].params.tex_layer_count = tex_layers;

            // ⑥ プリウォームステップの一時 uniform＋compute BG を作る。
            //    前フレームの一時リソースはここで破棄する（dispatch 済み）。
            //    ステップ params にも確定済み use_texture / shape_mode を反映する
            //    （sim は使わないが描画と同一構造体のため一貫させる）。
            {
                let g = self.gpu.get_mut(&entity).expect("gpu state must exist");
                g.prewarm.clear();
                let steps = std::mem::take(&mut self.frame[i].prewarm_params);
                for mut sp in steps {
                    sp.use_texture = use_texture;
                    sp.shape_mode = params.shape_mode;
                    let buf = device.create_buffer_init(&wgpu::util::BufferInitDescriptor {
                        label: Some("Particle Prewarm Params"),
                        contents: bytemuck::bytes_of(&sp),
                        usage: wgpu::BufferUsages::UNIFORM,
                    });
                    let bg = device.create_bind_group(&wgpu::BindGroupDescriptor {
                        label: Some("Particle Prewarm BG"),
                        layout: &compute_pl.bgl,
                        entries: &[
                            wgpu::BindGroupEntry {
                                binding: 0,
                                resource: g.particle_buf.as_entire_binding(),
                            },
                            wgpu::BindGroupEntry {
                                binding: 1,
                                resource: buf.as_entire_binding(),
                            },
                            wgpu::BindGroupEntry {
                                binding: 2,
                                resource: g.lut_buf.as_entire_binding(),
                            },
                        ],
                    });
                    g.prewarm.push((buf, bg));
                }
            }

            let g = self.gpu.get(&entity).expect("gpu state must exist");
            queue.write_buffer(&g.params_buf, 0, bytemuck::bytes_of(&params));

            // ⑦ 孤児化の種（直近フレームのスナップショット）を更新する。
            //    エミッタが次フレームに消えたとき、この内容のまま孤児として生き続ける
            //    （world_mat もここで凍結される＝孤児はエミッタの Transform に追従しない）。
            self.seeds.insert(
                entity,
                OrphanSeed {
                    blend: self.frame[i].blend,
                    shape: shape.clone(),
                    max_particles: capacity,
                    params,
                    // 存在しうる粒子の最大寿命（負値データは 0 に丸める）。
                    max_lifetime: params.lifetime_max.max(0.0),
                },
            );
        }

        // 孤児のパラメータ uniform を書き戻す（dt 更新・emit_count=0）。
        // バッファ／BindGroup は移譲済みのものをそのまま使うため再確保は発生しない。
        for o in &self.orphans {
            queue.write_buffer(&o.gpu.params_buf, 0, bytemuck::bytes_of(&o.seed.params));
        }
    }

    // ── フェーズ 3: compute ディスパッチ ──────────────────────

    /// 全エミッタのシミュレーションを compute pass 内でディスパッチする。
    ///
    /// プリウォームステップ（一時 BG）があれば先に順次ディスパッチし、
    /// 最後に通常ステップをディスパッチする。エミッタ・孤児とも 0 個なら即 return。
    /// ※ playing=false のエミッタも「既存粒子が自然消滅するまで」常時ディスパッチする
    ///   （放出は collect 側で止めており、更新は回す）。全粒子 dead 判定は省略。
    /// ※ 孤児（エミッタ消滅後の粒子群）も emit_count=0 のまま同じ compute を回す。
    ///   TODO: 全粒子 dead を検出して dispatch を打ち切れば更に省ける（エミッタ少数前提で未実装）。
    pub fn dispatch(&self, pass: &mut wgpu::ComputePass<'_>, compute_pl: &ParticleComputePipeline) {
        if self.frame.is_empty() && self.orphans.is_empty() {
            return;
        }
        pass.set_pipeline(&compute_pl.pipeline);
        for desc in &self.frame {
            let Some(g) = self.gpu.get(&desc.entity) else {
                continue;
            };
            let groups = Self::workgroup_count(desc.max_particles);
            // プリウォーム: ステップごとの一時 BG を順にディスパッチ（各自の uniform 値を読む）。
            for (_buf, bg) in &g.prewarm {
                pass.set_bind_group(0, bg, &[]);
                pass.dispatch_workgroups(groups, 1, 1);
            }
            // 通常ステップ。
            pass.set_bind_group(0, &g.compute_bg, &[]);
            pass.dispatch_workgroups(groups, 1, 1);
        }
        // 孤児: 放出は 0 のまま、既存粒子の更新（位置・速度・寿命）のみ回す。
        for o in &self.orphans {
            let groups = Self::workgroup_count(o.seed.max_particles);
            pass.set_bind_group(0, &o.gpu.compute_bg, &[]);
            pass.dispatch_workgroups(groups, 1, 1);
        }
    }

    // ── フェーズ 4: 描画 ──────────────────────────────────────

    /// 全エミッタ＋孤児をブレンド別パイプラインで描画する（形状メッシュ×インスタンス）。
    ///
    /// group0=camera（呼び出し側の共有カメラ BG）, group1=particles+params+lut,
    /// group2=texture（未使用時は既定白）。エミッタ・孤児とも 0 個なら即 return。
    ///
    /// TODO: Alpha ブレンドのエミッタ単位粗ソート（現状は登録順で描画）。
    /// TODO: indirect draw count（生存数に応じた可変インスタンス数）で無駄頂点を削減。
    pub fn draw<'p>(
        &'p self,
        pass: &mut wgpu::RenderPass<'p>,
        draw_pl: &'p ParticlePipelines,
        camera_bg: &'p wgpu::BindGroup,
    ) {
        if self.frame.is_empty() && self.orphans.is_empty() {
            return;
        }
        let Some(shapes) = &self.shapes else {
            return;
        }; // sync_gpu 前は描画しない
        // group0（camera）は全パイプラインでレイアウト共通のため 1 度だけセットする。
        pass.set_bind_group(0, camera_bg, &[]);

        // 生存中のエミッタ。
        for desc in &self.frame {
            let Some(g) = self.gpu.get(&desc.entity) else {
                continue;
            };
            Self::draw_pool(
                pass,
                draw_pl,
                shapes,
                g,
                desc.blend,
                &desc.shape,
                desc.max_particles,
                desc.params.use_texture,
            );
        }
        // 孤児（エミッタは消えたが寿命が残っている粒子群）。凍結した seed で描く。
        for o in &self.orphans {
            Self::draw_pool(
                pass,
                draw_pl,
                shapes,
                &o.gpu,
                o.seed.blend,
                &o.seed.shape,
                o.seed.max_particles,
                o.seed.params.use_texture,
            );
        }
    }

    /// パーティクルプール 1 本を描画する（生存エミッタ／孤児で共用する内部ヘルパ）。
    ///
    /// 形状を解決して、ブレンド別のメッシュ／Pixel パイプラインでインスタンス描画する。
    /// group0（camera）は呼び出し側でセット済みであること。
    fn draw_pool<'p>(
        pass: &mut wgpu::RenderPass<'p>,
        draw_pl: &'p ParticlePipelines,
        shapes: &'p ShapeMeshCache,
        gpu: &'p EmitterGpuState,
        blend: ParticleBlend,
        shape: &ParticleShape,
        max_particles: u32,
        use_texture: u32,
    ) {
        let code = blend.to_code();

        // 形状メッシュを解決する。Pixel、または Model のロード未完（None）は
        // メッシュ無し＝Pixel（PointList）パイプラインの 1px 点で描く。
        let mesh: Option<&ShapeMesh> = match shape {
            ParticleShape::Pixel => None,
            ParticleShape::Sphere => Some(shapes.sphere()),
            ParticleShape::Box => Some(shapes.cube()),
            ParticleShape::Plane => Some(shapes.plane()),
            ParticleShape::Model { path } => shapes.model_cached(path),
        };

        // 共通バインド（group1=particles/params/lut, group2=texture or 既定白）。
        pass.set_bind_group(1, &gpu.draw_bg, &[]);
        let tex_bg = if use_texture == 1 {
            gpu.texture_bg.as_ref().unwrap_or(&draw_pl.default_white_bg)
        } else {
            &draw_pl.default_white_bg
        };
        pass.set_bind_group(2, tex_bg, &[]);

        match mesh {
            // メッシュ形状: ブレンド別 TriangleList パイプライン＋形状メッシュ×インスタンス。
            Some(m) => {
                pass.set_pipeline(&draw_pl.mesh[code]);
                pass.set_vertex_buffer(0, m.vbuf.slice(..));
                pass.set_index_buffer(m.ibuf.slice(..), wgpu::IndexFormat::Uint32);
                pass.draw_indexed(0..m.index_count, 0, 0..max_particles);
            }
            // Pixel: ブレンド別 PointList パイプライン。頂点バッファ無し・1 頂点×インスタンス。
            None => {
                pass.set_pipeline(&draw_pl.pixel[code]);
                pass.draw(0..1, 0..max_particles);
            }
        }
    }
}

// ─── フリー関数 ───────────────────────────────────────────────

/// シーンを DFS 走査してエミッタのスナップショットを収集し、pending_burst を消費する。
///
/// Transform は Actor 本体の entity から、ParticleEmitterComponent は
/// ParticleEmitter スロットの entity から取得する（light_scene_gizmo と同じ慣例）。
/// World の借用を跨がないよう、Transform 行列を先にコピーしてから component を &mut する。
fn gather_emitters(world: &mut World, actors: &[Actor], wl: u32, out: &mut Vec<RawEmitter>) {
    for actor in actors {
        if actor.world_line == wl {
            // 先に Transform 行列をコピーして World の不変借用を解放する。
            let mat = world.get::<Transform>(actor.entity).map(|t| t.to_mat4());
            if let Some(mat) = mat {
                for slot in actor.slots() {
                    if slot.kind != ComponentKind::ParticleEmitter {
                        continue;
                    }
                    // pending_burst を消費するため &mut で取得する。
                    if let Some(c) = world.get_mut::<ParticleEmitterComponent>(slot.entity) {
                        let pending = c.pending_burst;
                        c.pending_burst = 0; // 契約: 本システムが毎フレーム消費してゼロに戻す。
                        out.push(RawEmitter {
                            entity: slot.entity,
                            world_mat: mat,
                            max_particles: c.max_particles,
                            shape: c.shape.clone(),
                            spawn_volume: c.spawn_volume,
                            emit_mode: c.emit_mode,
                            initial_delay: c.initial_delay,
                            prewarm_time: c.prewarm_time,
                            emit_interval: c.emit_interval,
                            particles_per_emit: c.particles_per_emit,
                            pending_burst: pending,
                            lifetime: c.lifetime,
                            initial_speed: c.initial_speed,
                            direction_local: c.direction_local,
                            direction_randomness: c.direction_randomness,
                            gravity: c.gravity,
                            drag: c.drag,
                            rot_speed_range: c.rot_speed_range,
                            initial_rotation_range: c.initial_rotation_range,
                            size_range: c.size_range,
                            // LUT 焼き込み用にカーブを clone する（エミッタ数は少数前提）。
                            curves: FrameCurves {
                                speed: c.speed_curve.clone(),
                                rot_speed: c.rot_speed_curve.clone(),
                                scale: c.scale_curve.clone(),
                                colors: c.color_curves.clone(),
                            },
                            curve_generation: c.curve_generation,
                            texture_paths: c.texture_paths.clone(),
                            blend: c.blend,
                            sim_space: c.sim_space,
                            playing: c.playing,
                        });
                    }
                }
            }
        }
        // 子アクターを再帰走査する（world の &mut は上の借用が解放済み）。
        gather_emitters(world, actor.children(), wl, out);
    }
}

/// テクスチャパス列を読み込み、texture_2d_array（最大 MAX_PARTICLE_TEXTURES レイヤ）の
/// group2 BindGroup を作る。サイズ不一致は先頭テクスチャのサイズへ CPU リサイズする
/// （Triangle フィルタ）。asset_fs::read_image 経由で assets:// / PAK に対応する。
///
/// 戻り値は (BindGroup, レイヤ数)。有効なパスが 1 つも無ければ (None, 0)＝既定白を使う。
fn load_particle_textures(
    device: &wgpu::Device,
    queue: &wgpu::Queue,
    paths: &[String],
    tex_bgl: &wgpu::BindGroupLayout,
    sampler: &wgpu::Sampler,
) -> (Option<wgpu::BindGroup>, u32) {
    // 空でないパスのみを最大 MAX_PARTICLE_TEXTURES 枚まで採用する。
    let sel: Vec<&String> = paths
        .iter()
        .filter(|p| !p.is_empty())
        .take(MAX_PARTICLE_TEXTURES)
        .collect();
    if sel.is_empty() {
        return (None, 0);
    }

    // 先頭画像のサイズを配列全体の基準サイズにする（不一致レイヤはここへリサイズ）。
    let first = crate::engine::asset_fs::read_image(sel[0]);
    let (w, h) = first.dimensions();
    if w == 0 || h == 0 {
        return (None, 0);
    }
    let layers = sel.len() as u32;

    // 各レイヤの RGBA バイト列を基準サイズへ揃えて連結する（レイヤ順に並べる）。
    let mut data: Vec<u8> = Vec::with_capacity((w * h * 4) as usize * layers as usize);
    for (i, p) in sel.iter().enumerate() {
        let img = if i == 0 {
            first.clone()
        } else {
            crate::engine::asset_fs::read_image(p)
        };
        if img.dimensions() == (w, h) {
            data.extend_from_slice(img.as_raw());
        } else {
            let resized =
                image::imageops::resize(&img, w, h, image::imageops::FilterType::Triangle);
            data.extend_from_slice(resized.as_raw());
        }
    }

    // texture_2d_array を確保して全レイヤを一括アップロードする。
    let texture = device.create_texture(&wgpu::TextureDescriptor {
        label: Some("Particle Texture Array"),
        size: wgpu::Extent3d {
            width: w,
            height: h,
            depth_or_array_layers: layers,
        },
        mip_level_count: 1,
        sample_count: 1,
        dimension: wgpu::TextureDimension::D2,
        format: wgpu::TextureFormat::Rgba8UnormSrgb,
        usage: wgpu::TextureUsages::TEXTURE_BINDING | wgpu::TextureUsages::COPY_DST,
        view_formats: &[],
    });
    queue.write_texture(
        texture.as_image_copy(),
        &data,
        wgpu::TexelCopyBufferLayout {
            offset: 0,
            bytes_per_row: Some(4 * w),
            rows_per_image: Some(h),
        },
        wgpu::Extent3d {
            width: w,
            height: h,
            depth_or_array_layers: layers,
        },
    );
    let view = texture.create_view(&wgpu::TextureViewDescriptor {
        label: Some("Particle Texture Array View"),
        dimension: Some(wgpu::TextureViewDimension::D2Array),
        array_layer_count: Some(layers),
        ..Default::default()
    });
    let bg = device.create_bind_group(&wgpu::BindGroupDescriptor {
        label: Some("Particle Texture BG"),
        layout: tex_bgl,
        entries: &[
            wgpu::BindGroupEntry {
                binding: 0,
                resource: wgpu::BindingResource::TextureView(&view),
            },
            wgpu::BindGroupEntry {
                binding: 1,
                resource: wgpu::BindingResource::Sampler(sampler),
            },
        ],
    });
    (Some(bg), layers)
}

/// 行優先行列（CPU）→ 列優先行列（GPU）への転置（skin_system と同一）。
fn transpose4x4(m: &[[f32; 4]; 4]) -> [[f32; 4]; 4] {
    let mut out = [[0.0f32; 4]; 4];
    for i in 0..4 {
        for j in 0..4 {
            out[i][j] = m[j][i];
        }
    }
    out
}

// ─── レイアウト検証テスト ──────────────────────────────────────
//
// GpuParticle / GpuEmitterParams の repr(C) レイアウトは WGSL 構造体と
// バイト単位で一致していなければならない（不一致は静かに描画バグを生む）。
#[cfg(test)]
mod layout_tests {
    use super::*;
    use std::mem::{offset_of, size_of};

    /// GpuParticle は 64 バイト（PARTICLE_STRIDE と一致・array stride）。
    #[test]
    fn gpu_particle_layout() {
        assert_eq!(size_of::<GpuParticle>(), 64, "GpuParticle は 64 バイト");
        assert_eq!(PARTICLE_STRIDE, 64, "PARTICLE_STRIDE は 64 固定");
        assert_eq!(offset_of!(GpuParticle, pos), 0);
        assert_eq!(offset_of!(GpuParticle, age), 12);
        assert_eq!(offset_of!(GpuParticle, vel), 16);
        assert_eq!(offset_of!(GpuParticle, lifetime), 28);
        assert_eq!(offset_of!(GpuParticle, emit_dir), 32);
        assert_eq!(offset_of!(GpuParticle, base_speed), 44);
        assert_eq!(offset_of!(GpuParticle, seed), 48);
        assert_eq!(offset_of!(GpuParticle, rot_angle), 52);
    }

    /// GpuEmitterParams は 208 バイト。vec3 は 16 バイト境界に整列し、
    /// 直後のスカラーが 4 番目のスロットに同居する。
    #[test]
    fn gpu_emitter_params_layout() {
        assert_eq!(
            size_of::<GpuEmitterParams>(),
            208,
            "GpuEmitterParams は 208 バイト"
        );
        assert_eq!(offset_of!(GpuEmitterParams, world_mat), 0);
        assert_eq!(offset_of!(GpuEmitterParams, dt), 64);
        assert_eq!(offset_of!(GpuEmitterParams, emit_count), 68);
        assert_eq!(offset_of!(GpuEmitterParams, ring_start), 72);
        assert_eq!(offset_of!(GpuEmitterParams, max_particles), 76);
        assert_eq!(offset_of!(GpuEmitterParams, frame_nonce), 80);
        assert_eq!(offset_of!(GpuEmitterParams, drag), 84);
        assert_eq!(offset_of!(GpuEmitterParams, spread_rad), 88);
        assert_eq!(offset_of!(GpuEmitterParams, shape_mode), 92);
        assert_eq!(offset_of!(GpuEmitterParams, direction_local), 96);
        assert_eq!(offset_of!(GpuEmitterParams, speed_min), 108);
        assert_eq!(offset_of!(GpuEmitterParams, speed_max), 112);
        assert_eq!(offset_of!(GpuEmitterParams, lifetime_min), 116);
        assert_eq!(offset_of!(GpuEmitterParams, lifetime_max), 120);
        assert_eq!(offset_of!(GpuEmitterParams, rot_speed_min), 124);
        assert_eq!(offset_of!(GpuEmitterParams, gravity), 128);
        assert_eq!(offset_of!(GpuEmitterParams, rot_speed_max), 140);
        assert_eq!(offset_of!(GpuEmitterParams, spawn_box), 144);
        assert_eq!(offset_of!(GpuEmitterParams, spawn_sphere_radius), 156);
        assert_eq!(offset_of!(GpuEmitterParams, size_min), 160);
        assert_eq!(offset_of!(GpuEmitterParams, size_max), 164);
        assert_eq!(offset_of!(GpuEmitterParams, spawn_volume), 168);
        assert_eq!(offset_of!(GpuEmitterParams, sim_space), 172);
        assert_eq!(offset_of!(GpuEmitterParams, use_texture), 176);
        assert_eq!(offset_of!(GpuEmitterParams, lut_samples), 180);
        assert_eq!(offset_of!(GpuEmitterParams, color_count), 184);
        assert_eq!(offset_of!(GpuEmitterParams, initial_rot_min), 188);
        assert_eq!(offset_of!(GpuEmitterParams, initial_rot_max), 192);
        assert_eq!(offset_of!(GpuEmitterParams, tex_layer_count), 196);
    }

    /// LUT の連結順（speed / rot_speed / scale / color_0..M-1）と行数。
    #[test]
    fn lut_bake_layout() {
        use crate::engine::components::particle_emitter_component::CurveChannel;
        let curves = FrameCurves {
            speed: ParamCurve::constant1(2.0),
            rot_speed: ParamCurve::constant1(3.0),
            scale: ParamCurve {
                channels: vec![
                    CurveChannel::constant(1.0),
                    CurveChannel::constant(1.0),
                    CurveChannel::constant(1.0),
                ],
            },
            colors: vec![
                ParamCurve {
                    channels: vec![
                        CurveChannel::constant(0.5),
                        CurveChannel::constant(0.6),
                        CurveChannel::constant(0.7),
                        CurveChannel::constant(0.8),
                    ],
                },
                ParamCurve::constant1(9.0),
            ],
        };
        let lut = curves.bake();
        let s = CURVE_LUT_SAMPLES;
        // 行数 = (固定 3 本 + color 2 本) × S。
        assert_eq!(lut.len(), (LUT_FIXED_CURVES + 2) * s);
        // 各チャンネルの先頭行が正しい位置にあること。
        assert_eq!(lut[0][0], 2.0, "speed は行 0 から");
        assert_eq!(lut[s][0], 3.0, "rot_speed は行 S から");
        assert_eq!(lut[2 * s][0], 1.0, "scale は行 2S から");
        assert_eq!(lut[3 * s][0], 0.5, "color_0 は行 3S から（H）");
        assert_eq!(lut[3 * s][3], 0.8, "color_0 の A チャンネル");
        assert_eq!(lut[4 * s][0], 9.0, "color_1 は行 4S から");
    }
}

// ─── フレームレート非依存性の検証 ──────────────────────────────
//
// particle_sim.wgsl の重力＋空気抵抗は「指数減衰の閉形式」で積分している。
// 下の参照実装はその閉形式を CPU に写したもの（テスト専用。GPU 側ロジックの
// 二重実装ではなく、式そのものが dt の刻み方に依らないことを固定する検証用）。
#[cfg(test)]
mod framerate_independence_tests {
    /// WGSL の DRAG_EPSILON と同じ「抵抗なし」しきい値。
    const DRAG_EPSILON: f32 = 1e-4;

    /// 重力 g・空気抵抗 k のもとで速度 v を dt 秒ぶん進める閉形式（particle_sim.wgsl と同一式）。
    ///
    ///   dv/dt = g - k*v
    ///   v(t+dt) = v∞ + (v - v∞) * exp(-k*dt)          , v∞ = g/k
    ///   Δx      = v∞*dt + (v - v∞) * (1 - exp(-k*dt)) / k
    ///
    /// 戻り値は (新しい速度, このステップの変位)。k≈0 は等加速度運動の厳密解へ分岐する。
    fn integrate(v: f64, g: f64, k: f64, dt: f64) -> (f64, f64) {
        if k > DRAG_EPSILON as f64 {
            let decay = (-k * dt).exp();
            let v_inf = g / k;
            let dv = v - v_inf;
            (v_inf + dv * decay, v_inf * dt + dv * (1.0 - decay) / k)
        } else {
            (v + g * dt, v * dt + 0.5 * g * dt * dt)
        }
    }

    /// 旧実装（線形近似の減衰 v *= 1 - k*dt）: フレームレート依存であることを示す対照。
    fn integrate_legacy(v: f64, g: f64, k: f64, dt: f64) -> (f64, f64) {
        let nv = (v + g * dt) * (1.0 - k * dt).max(0.0);
        (nv, nv * dt) // 旧: pos += v_new * dt（半陰的オイラー）
    }

    /// 閉形式の積分は dt の刻み方に依らない（60fps×2 ステップ == 30fps×1 ステップ）。
    #[test]
    fn closed_form_integration_is_framerate_independent() {
        let g = -9.8_f64;
        // 抵抗あり／なし（k=0）／強い抵抗の 3 ケースで検証する。
        for &k in &[0.0_f64, 0.5, 2.0, 30.0] {
            let (dt_fast, dt_slow) = (1.0 / 60.0, 1.0 / 30.0);

            // 60fps: 2 ステップ。
            let (mut v_fast, mut x_fast) = (3.0_f64, 0.0_f64);
            for _ in 0..2 {
                let (nv, dx) = integrate(v_fast, g, k, dt_fast);
                v_fast = nv;
                x_fast += dx;
            }
            // 30fps: 1 ステップ（同じ 1/30 秒）。
            let (v_slow, x_slow) = integrate(3.0, g, k, dt_slow);

            assert!(
                (v_fast - v_slow).abs() < 1e-9,
                "drag={k}: 速度が dt 依存（60fps×2={v_fast} / 30fps×1={v_slow}）",
            );
            assert!(
                (x_fast - x_slow).abs() < 1e-9,
                "drag={k}: 位置が dt 依存（60fps×2={x_fast} / 30fps×1={x_slow}）",
            );
        }
    }

    /// 対照: 旧実装（v *= 1-k*dt ＋半陰的オイラー）は同条件で明確に食い違う
    /// ＝これが「速度が FPS 依存」だった根本原因であることを固定する。
    #[test]
    fn legacy_linear_drag_was_framerate_dependent() {
        let (g, k) = (-9.8_f64, 2.0_f64);
        let (mut v_fast, mut x_fast) = (3.0_f64, 0.0_f64);
        for _ in 0..2 {
            let (nv, dx) = integrate_legacy(v_fast, g, k, 1.0 / 60.0);
            v_fast = nv;
            x_fast += dx;
        }
        let (v_slow, x_slow) = integrate_legacy(3.0, g, k, 1.0 / 30.0);
        assert!(
            (v_fast - v_slow).abs() > 1e-3,
            "旧実装は FPS 依存だったはず（fast={v_fast} / slow={v_slow}）",
        );
        assert!(
            (x_fast - x_slow).abs() > 1e-3,
            "旧実装は位置も FPS 依存だったはず"
        );
    }
}

// ─── WGSL 静的検証（naga parse + validate）─────────────────────
#[cfg(test)]
mod shader_tests {
    /// particle_sim.wgsl の全ての時間発展項が dt を経由していること（静的検査）。
    ///
    /// 「dt を掛け忘れた項」「フレーム依存の減衰形（v *= 1 - k*dt）」の再混入を
    /// コンパイル時ではなくテストで塞ぐ。WGSL ロジックを CPU に二重実装せずに
    /// フレームレート非依存の構造を担保するためのガード。
    #[test]
    fn particle_sim_time_evolution_uses_dt() {
        let src = include_str!("shaders/particle_sim.wgsl");

        // ① 禁止パターン: 線形近似の減衰（終端速度が dt 依存になり k*dt>=1 で速度が潰れる）。
        assert!(
            !src.contains("1.0 - params.drag"),
            "線形近似の空気抵抗（v *= 1 - drag*dt）が再混入している。exp(-k*dt) を使うこと",
        );

        // ② 必須パターン: 指数減衰の閉形式。
        assert!(
            src.contains("exp(-k * dt)"),
            "空気抵抗が指数減衰の閉形式 exp(-k*dt) になっていない",
        );

        // ③ 粒子の状態（pos / vel / age / rot_angle）を「自分自身に積む」代入は、必ず
        //    dt 由来の項を含むこと（変位変数 *_disp は dt を掛けて作られている）。
        //    空白を除去して比較する（整形の揺れに影響されないようにするため）。
        //    ※ スポーン節の初期化（p.xxx = 定数）は `p.x = p.x +` の形にならず対象外。
        let fields = ["p.pos", "p.vel", "p.age", "p.rot_angle"];
        let mut checked = 0usize;
        for line in src.lines() {
            let squashed: String = line.chars().filter(|c| !c.is_whitespace()).collect();
            for f in fields {
                if squashed.starts_with(&format!("{f}={f}+")) {
                    checked += 1;
                    assert!(
                        squashed.contains("dt") || squashed.contains("_disp"),
                        "時間発展の代入が dt を経由していない: {}",
                        line.trim(),
                    );
                }
            }
        }
        // 検査対象が 0 件＝パターンが変わって検査が空回りしている、を防ぐ。
        assert!(
            checked >= 3,
            "時間発展の代入が検出できていない（検査が空回り）: {checked} 件"
        );
    }

    /// particle_sim.wgsl / particle_draw.wgsl を naga で parse + validate する。
    /// どちらも自己完結（外部連結不要）でパース可能な構成である。
    #[test]
    fn particle_shaders_parse_and_validate() {
        let variants: [(&str, &str); 2] = [
            ("particle_sim", include_str!("shaders/particle_sim.wgsl")),
            ("particle_draw", include_str!("shaders/particle_draw.wgsl")),
        ];
        for (name, src) in variants {
            let module = naga::front::wgsl::parse_str(src)
                .unwrap_or_else(|e| panic!("[{name}] WGSL parse 失敗: {e:?}"));
            let mut validator = naga::valid::Validator::new(
                naga::valid::ValidationFlags::all(),
                naga::valid::Capabilities::empty(),
            );
            validator
                .validate(&module)
                .unwrap_or_else(|e| panic!("[{name}] WGSL validate 失敗: {e:?}"));
        }
    }
}
