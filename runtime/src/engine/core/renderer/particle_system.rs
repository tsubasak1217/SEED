// ============================================================
//  particle_system.rs — GPU パーティクルのシミュレーション＋描画システム
//
//  ECS の ParticleEmitterComponent（データのみ）を入力に、GPU 上で
//  パーティクルをシミュレート（compute）して描画（vertex pulling ビルボード）する。
//
//  【ライフサイクル（1 フレーム）】
//    1. collect_and_consume()  … CPU 側。シーンからエミッタを収集し、放出個数を
//       決定してリングカーソルを進め、pending_burst（スクリプトの Burst 要求）を
//       消費する。デバイス不要（World への &mut のみ）。
//    2. sync_gpu()             … GPU 側。エミッタごとの GPU バッファ／バインドグループを
//       確保・更新し、パラメータ uniform を書き込む（テクスチャ差し替え検知含む）。
//    3. dispatch()             … compute pass 内で全エミッタをディスパッチ。
//    4. draw()                 … render pass 内で全エミッタをブレンド別に描画。
//
//  【スポーン方式：リングカーソル（atomic なし）】
//    CPU が spawn_cursor（= ring_start）と emit_count を uniform で渡す。compute の
//    各スレッド（slot index i）はリング区間 [ring_start, ring_start+emit_count) に
//    自分が入っていれば無条件で再スポーンする（過剰放出時は生存粒子を上書き＝標準的な
//    リング挙動）。詳細は particle_sim.wgsl を参照。
//
//  【空間シム】
//    World: スポーン位置＝エミッタ行列の平行移動、方向＝行列で回した円錐。放出後は
//           ワールド固定（エミッタ移動に追従しない）。
//    Local: 原点発生・ローカルでシムし、描画時にワールド行列で変換（エミッタに追従）。
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

use crate::engine::components::{ComponentKind, Transform};
use crate::engine::components::particle_emitter_component::{
    ParticleBlend, ParticleEmitterComponent, ParticleSimSpace, MAX_PARTICLES_PER_EMITTER,
};
use crate::engine::ecs::{Entity, World};
use crate::engine::structs::objects::Actor;

use super::pipeline::{ParticleComputePipeline, ParticlePipelines};

// ─── 定数（マジックナンバー禁止）──────────────────────────────

/// compute のワークグループサイズ（particle_sim.wgsl の @workgroup_size と一致）。
const WORKGROUP_SIZE: u32 = 64;

/// 1 パーティクルのバイトストライド（std430・16 の倍数）。WGSL の Particle と一致。
///   pos(12)+age(4) / vel(12)+lifetime(4) / seed(4)+pad(12) = 48。
pub const PARTICLE_STRIDE: u32 = 48;

/// max_particles の下限（0 だと dispatch/バッファが不正になるため 1 にクランプ）。
const MIN_PARTICLES: u32 = 1;

// ─── GpuParticle（storage 要素・std430, 48 バイト）────────────

/// GPU パーティクル 1 個（compute が read_write、描画が read）。
///
/// zeroed = 全 dead（lifetime=0）。生成時ゼロ初期化で初期状態は全 dead になる。
/// 全パディングを明示し bytemuck Pod の要件（隙間なし）を満たす。
#[repr(C)]
#[derive(Copy, Clone, Pod, Zeroable)]
pub struct GpuParticle {
    /// 位置（World or Local, sim_space による）。offset 0
    pub pos:      [f32; 3],
    /// 経過秒。offset 12
    pub age:      f32,
    /// 速度。offset 16
    pub vel:      [f32; 3],
    /// 寿命秒（<=0 は dead）。offset 28
    pub lifetime: f32,
    /// スポーン時の乱数シード（描画がサイズ乱数の再現に使う）。offset 32
    pub seed:     u32,
    /// 16 バイト境界パディング。offset 36
    pub _pad:     [u32; 3],
}

// ─── GpuEmitterParams（uniform, 192 バイト）───────────────────

/// エミッタパラメータ uniform（compute と描画で共有）。
///
/// WGSL の std140 uniform レイアウトに一致させるため、vec3 の直後にスカラーを
/// 詰めて 4 番目の要素スロットを埋めている（各 vec3/vec4 は 16 バイト境界）。
/// repr(C) の自然オフセットが std140 のオフセットと一致する（layout_tests で固定）。
#[repr(C)]
#[derive(Copy, Clone, Pod, Zeroable)]
pub struct GpuEmitterParams {
    /// エミッタのワールド行列（列優先＝CPU 行優先を転置済み）。offset 0
    pub world_mat:       [[f32; 4]; 4],
    /// このステップの経過秒。offset 64
    pub dt:              f32,
    /// 今フレームの放出個数。offset 68
    pub emit_count:      u32,
    /// リングカーソル開始スロット。offset 72
    pub ring_start:      u32,
    /// プール容量。offset 76
    pub max_particles:   u32,
    /// フレーム固有ノンス（乱数系列の変化用）。offset 80
    pub frame_nonce:     u32,
    /// 空気抵抗係数。offset 84
    pub drag:            f32,
    /// 放出円錐の半頂角（ラジアン）。offset 88
    pub spread_rad:      f32,
    /// 寿命末のサイズ倍率（描画用）。offset 92
    pub end_size_scale:  f32,
    /// ローカル放出方向。offset 96（vec3・16 バイト境界）
    pub direction_local: [f32; 3],
    /// 初速 min。offset 108（direction_local の 4 番目スロットに同居）
    pub speed_min:       f32,
    /// 初速 max。offset 112
    pub speed_max:       f32,
    /// 寿命 min。offset 116
    pub lifetime_min:    f32,
    /// 寿命 max。offset 120
    pub lifetime_max:    f32,
    /// 開始サイズ min。offset 124
    pub size_min:        f32,
    /// 重力加速度。offset 128（vec3・16 バイト境界）
    pub gravity:         [f32; 3],
    /// 開始サイズ max。offset 140（gravity の 4 番目スロットに同居）
    pub size_max:        f32,
    /// 開始色 RGBA。offset 144（vec4・16 バイト境界）
    pub start_color:     [f32; 4],
    /// 終了色 RGBA。offset 160
    pub end_color:       [f32; 4],
    /// シミュレーション空間（0=World / 1=Local）。offset 176
    pub sim_space:       u32,
    /// テクスチャ使用フラグ（1=テクスチャ / 0=プロシージャル円）。offset 180
    pub use_texture:     u32,
    /// パディング。offset 184
    pub _pad0:           u32,
    pub _pad1:           u32,
}

// ─── CPU 側の永続状態（エミッタごと・デバイス不要）────────────

/// エミッタ 1 個の CPU 側永続状態（放出アキュムレータ・カーソル等）。
struct EmitterCpuState {
    /// 放出アキュムレータ（emit_rate*dt を積み、整数個放出したら小数を残す）。
    emit_accum:    f32,
    /// 次の放出開始スロット（リングカーソル）。常に < max。
    spawn_cursor:  u32,
    /// 前フレームの playing（立ち上がりエッジでのバースト検出用。初期 false）。
    prev_playing:  bool,
    /// これまでの累計放出数（loop_emit=false の一巡停止判定用）。
    emitted_total: u64,
}

impl EmitterCpuState {
    fn new() -> Self {
        Self { emit_accum: 0.0, spawn_cursor: 0, prev_playing: false, emitted_total: 0 }
    }
}

// ─── GPU 側の状態（エミッタごと・バッファ／バインドグループ）──

/// エミッタ 1 個の GPU 側状態（バッファ・バインドグループ・テクスチャ）。
struct EmitterGpuState {
    /// パーティクルプール（STORAGE|COPY_DST, max*PARTICLE_STRIDE, 生成時ゼロ＝全 dead）。
    ///
    /// compute_bg / draw_bg が参照し続けるため保持のみで直接は読み出さない
    /// （バインドグループのバッキングとして生存させる必要がある）。
    #[allow(dead_code)]
    particle_buf: wgpu::Buffer,
    /// エミッタパラメータ uniform（UNIFORM|COPY_DST, 192 バイト）。
    params_buf:   wgpu::Buffer,
    /// compute 用バインドグループ（group0: particles rw + params）。
    compute_bg:   wgpu::BindGroup,
    /// 描画用バインドグループ（group1: particles ro + params）。
    draw_bg:      wgpu::BindGroup,
    /// テクスチャバインドグループ（group2）。未ロード時 None（既定白を使う）。
    texture_bg:   Option<wgpu::BindGroup>,
    /// 現在ロード済みのテクスチャパス（差し替え検知用）。
    texture_path: Option<String>,
    /// particle_buf の確保容量（max_particles 変更時の再確保判定）。
    buf_capacity: u32,
}

impl EmitterGpuState {
    /// 指定容量でバッファ・バインドグループ一式を新規生成する（particle_buf はゼロ初期化）。
    fn create(
        device:      &wgpu::Device,
        compute_pl:  &ParticleComputePipeline,
        draw_pl:     &ParticlePipelines,
        capacity:    u32,
    ) -> Self {
        // パーティクルプールを 0 埋めして確保（全 dead 初期状態を保証する）。
        let byte_size = capacity as usize * PARTICLE_STRIDE as usize;
        let zeros = vec![0u8; byte_size];
        let particle_buf = device.create_buffer_init(&wgpu::util::BufferInitDescriptor {
            label:    Some("Particle Pool Buffer"),
            contents: &zeros,
            usage:    wgpu::BufferUsages::STORAGE | wgpu::BufferUsages::COPY_DST,
        });

        // エミッタパラメータ uniform（毎フレーム write_buffer で更新）。
        let params_buf = device.create_buffer(&wgpu::BufferDescriptor {
            label:              Some("Particle Emitter Params"),
            size:               std::mem::size_of::<GpuEmitterParams>() as u64,
            usage:              wgpu::BufferUsages::UNIFORM | wgpu::BufferUsages::COPY_DST,
            mapped_at_creation: false,
        });

        // compute BG（particles read_write + params）。
        let compute_bg = device.create_bind_group(&wgpu::BindGroupDescriptor {
            label:   Some("Particle Compute BG"),
            layout:  &compute_pl.bgl,
            entries: &[
                wgpu::BindGroupEntry { binding: 0, resource: particle_buf.as_entire_binding() },
                wgpu::BindGroupEntry { binding: 1, resource: params_buf.as_entire_binding() },
            ],
        });

        // 描画 BG（particles read + params）。group1。
        let draw_bg = device.create_bind_group(&wgpu::BindGroupDescriptor {
            label:   Some("Particle Draw BG (group1)"),
            layout:  &draw_pl.particle_bgl,
            entries: &[
                wgpu::BindGroupEntry { binding: 0, resource: particle_buf.as_entire_binding() },
                wgpu::BindGroupEntry { binding: 1, resource: params_buf.as_entire_binding() },
            ],
        });

        Self {
            particle_buf,
            params_buf,
            compute_bg,
            draw_bg,
            texture_bg:   None,
            texture_path: None,
            buf_capacity: capacity,
        }
    }
}

// ─── フレームごとの描画対象記述 ───────────────────────────────

/// このフレームに dispatch / draw する 1 エミッタの記述。
struct EmitterFrameDesc {
    /// エミッタスロットの entity（gpu マップのキー）。
    entity:        Entity,
    /// 合成モード（描画パイプライン選択）。
    blend:         ParticleBlend,
    /// プール容量（dispatch のワークグループ数・draw のインスタンス数）。
    max_particles: u32,
    /// テクスチャパス（空＝プロシージャル）。sync_gpu が差し替え検知に使う。
    texture_path:  String,
    /// GPU へ書き込むパラメータ（use_texture は sync_gpu でロード結果に応じて確定）。
    params:        GpuEmitterParams,
}

// ─── ParticleSystem 本体 ──────────────────────────────────────

/// GPU パーティクルシステム（App が 1 つ保持）。
///
/// エミッタごとの CPU/GPU 状態を HashMap で保持し、シーンに存在しなくなった
/// エミッタは毎フレーム retain で破棄する。パイプライン等の静的リソースは
/// DrawPipelines（draw_ctx.pipelines）側が持つ（本体はバッファと状態のみ）。
pub struct ParticleSystem {
    /// エミッタ entity → CPU 永続状態。
    cpu:   HashMap<Entity, EmitterCpuState>,
    /// エミッタ entity → GPU 状態（バッファ・BG）。
    gpu:   HashMap<Entity, EmitterGpuState>,
    /// このフレームの描画対象（collect_and_consume が毎フレーム再構築する）。
    frame: Vec<EmitterFrameDesc>,
    /// フレームカウンタ（frame_nonce の生成に使う。乱数系列をフレームで変える）。
    frame_counter: u64,
}

/// 収集時にシーンから抜き出すエミッタのスナップショット（World の借用を跨がないため）。
struct RawEmitter {
    entity:           Entity,
    world_mat:        [[f32; 4]; 4], // 行優先（CPU）
    max_particles:    u32,
    emit_rate:        f32,
    burst:            u32,
    pending_burst:    u32,
    lifetime:         [f32; 2],
    initial_speed:    [f32; 2],
    spread_angle_deg: f32,
    direction_local:  [f32; 3],
    gravity:          [f32; 3],
    drag:             f32,
    start_size:       [f32; 2],
    end_size_scale:   f32,
    start_color:      [f32; 4],
    end_color:        [f32; 4],
    texture_path:     String,
    blend:            ParticleBlend,
    sim_space:        ParticleSimSpace,
    playing:          bool,
    loop_emit:        bool,
}

impl Default for ParticleSystem {
    fn default() -> Self { Self::new() }
}

impl ParticleSystem {
    /// 空のシステムを生成する（デバイス不要。rt_pool と同じく eager 構築可能）。
    pub fn new() -> Self {
        Self {
            cpu:           HashMap::new(),
            gpu:           HashMap::new(),
            frame:         Vec::new(),
            frame_counter: 0,
        }
    }

    /// このフレームに描画すべきエミッタが 1 つでもあるか（追加コストゼロ判定）。
    pub fn has_emitters(&self) -> bool { !self.frame.is_empty() }

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

        // ③ シーンから消えたエミッタの CPU/GPU 状態を破棄する（メモリ・GPU 解放）。
        self.cpu.retain(|e, _| present.contains(e));
        self.gpu.retain(|e, _| present.contains(e));
    }

    /// 1 エミッタの放出個数・リングカーソル・パラメータを決定して frame へ積む。
    fn process_emitter(&mut self, raw: RawEmitter, dt: f32) {
        let max = raw.max_particles.clamp(MIN_PARTICLES, MAX_PARTICLES_PER_EMITTER);
        let counter = self.frame_counter;
        let cpu = self.cpu.entry(raw.entity).or_insert_with(EmitterCpuState::new);

        let mut count: u32 = 0;

        // 連続放出（playing 時のみ）: emit_accum に emit_rate*dt を積み、整数分を放出。
        if raw.playing {
            cpu.emit_accum += raw.emit_rate.max(0.0) * dt.max(0.0);
            let whole = cpu.emit_accum.floor();
            count += whole.max(0.0) as u32;
            cpu.emit_accum -= whole;
        }

        // スクリプトの Burst(n)（pending_burst）は playing に関わらず消費・放出する。
        count += raw.pending_burst;

        // playing 立ち上がりエッジ（初回生成時 playing=true 含む。prev_playing 初期 false）
        // でバースト放出する。
        if raw.playing && !cpu.prev_playing {
            count += raw.burst;
        }
        cpu.prev_playing = raw.playing;

        // loop_emit=false: 累計放出が容量に達したら放出停止（一巡で止める）。
        if !raw.loop_emit {
            if cpu.emitted_total >= max as u64 {
                count = 0;
            } else {
                let remaining = (max as u64 - cpu.emitted_total) as u32;
                count = count.min(remaining);
            }
        }

        // 1 フレームでプール容量を超えて放出しても意味がない（上書きになる）ためクランプ。
        count = count.min(max);

        let ring_start = cpu.spawn_cursor % max;
        cpu.spawn_cursor = (cpu.spawn_cursor + count) % max;
        cpu.emitted_total = cpu.emitted_total.saturating_add(count as u64);

        // frame_nonce: フレームカウンタとエミッタ index を混ぜて系列を分離・変化させる。
        let frame_nonce = (counter as u32) ^ raw.entity.index().wrapping_mul(0x9E3779B9);

        let params = GpuEmitterParams {
            world_mat:       transpose4x4(&raw.world_mat), // 行優先 → 列優先
            dt,
            emit_count:      count,
            ring_start,
            max_particles:   max,
            frame_nonce,
            drag:            raw.drag,
            spread_rad:      raw.spread_angle_deg.to_radians(),
            end_size_scale:  raw.end_size_scale,
            direction_local: raw.direction_local,
            speed_min:       raw.initial_speed[0],
            speed_max:       raw.initial_speed[1],
            lifetime_min:    raw.lifetime[0],
            lifetime_max:    raw.lifetime[1],
            size_min:        raw.start_size[0],
            gravity:         raw.gravity,
            size_max:        raw.start_size[1],
            start_color:     raw.start_color,
            end_color:       raw.end_color,
            sim_space:       raw.sim_space.to_code(),
            // 仮の use_texture（テクスチャパス有無）。sync_gpu がロード結果で確定する。
            use_texture:     if raw.texture_path.is_empty() { 0 } else { 1 },
            _pad0:           0,
            _pad1:           0,
        };

        self.frame.push(EmitterFrameDesc {
            entity:        raw.entity,
            blend:         raw.blend,
            max_particles: max,
            texture_path:  raw.texture_path,
            params,
        });
    }

    // ── フェーズ 2: GPU バッファ／BG 確保・パラメータ書き込み ──

    /// エミッタごとの GPU 状態を確保・更新し、パラメータ uniform を書き込む。
    ///
    /// - 容量（max_particles）変更時はプールを作り直す（ゼロ初期化＝全 dead）。
    /// - テクスチャパス変更時は texture_bg を再ロードする。
    /// - use_texture はロード成否に応じて確定し、params を書き込む。
    ///
    /// エミッタ 0 個なら即 return（バッファ確保なし＝追加コストゼロ）。
    pub fn sync_gpu(
        &mut self,
        device:     &wgpu::Device,
        queue:      &wgpu::Queue,
        compute_pl: &ParticleComputePipeline,
        draw_pl:    &ParticlePipelines,
    ) {
        if self.frame.is_empty() { return; }

        for i in 0..self.frame.len() {
            // frame[i] から必要な値をコピー／複製して以降の &mut self.gpu と競合させない。
            let entity     = self.frame[i].entity;
            let capacity   = self.frame[i].max_particles;
            let tex_path   = self.frame[i].texture_path.clone();
            let mut params = self.frame[i].params;

            // ① 容量に合わせて GPU 状態を確保（新規 or 容量変更時に再生成）。
            let need_new = match self.gpu.get(&entity) {
                Some(g) => g.buf_capacity != capacity,
                None    => true,
            };
            if need_new {
                let g = EmitterGpuState::create(device, compute_pl, draw_pl, capacity);
                self.gpu.insert(entity, g);
            }

            // ② テクスチャの差し替え検知＆ロード、use_texture の確定。
            let mut use_texture = 0u32;
            if !tex_path.is_empty() {
                let g = self.gpu.get_mut(&entity).expect("gpu state must exist");
                let changed = g.texture_path.as_deref() != Some(tex_path.as_str())
                    || g.texture_bg.is_none();
                if changed {
                    g.texture_bg = load_particle_texture(
                        device, queue, &tex_path, &draw_pl.tex_bgl, &draw_pl.sampler,
                    );
                    g.texture_path = Some(tex_path.clone());
                }
                if g.texture_bg.is_some() { use_texture = 1; }
            }

            // ③ 確定した use_texture を params と frame[i]（draw が参照）へ反映して書き込む。
            params.use_texture = use_texture;
            self.frame[i].params.use_texture = use_texture;
            let g = self.gpu.get(&entity).expect("gpu state must exist");
            queue.write_buffer(&g.params_buf, 0, bytemuck::bytes_of(&params));
        }
    }

    // ── フェーズ 3: compute ディスパッチ ──────────────────────

    /// 全エミッタのシミュレーションを compute pass 内でディスパッチする。
    ///
    /// エミッタ 0 個なら即 return（パイプライン設定もしない）。
    /// ※ playing=false のエミッタも「既存粒子が自然消滅するまで」常時ディスパッチする
    ///   （放出は collect 側で止めており、更新は回す）。全粒子 dead 判定は省略。
    ///   TODO: 全粒子 dead を検出して dispatch を打ち切れば更に省ける（エミッタ少数前提で未実装）。
    pub fn dispatch(&self, pass: &mut wgpu::ComputePass<'_>, compute_pl: &ParticleComputePipeline) {
        if self.frame.is_empty() { return; }
        pass.set_pipeline(&compute_pl.pipeline);
        for desc in &self.frame {
            let Some(g) = self.gpu.get(&desc.entity) else { continue; };
            pass.set_bind_group(0, &g.compute_bg, &[]);
            let groups = (desc.max_particles + WORKGROUP_SIZE - 1) / WORKGROUP_SIZE;
            pass.dispatch_workgroups(groups, 1, 1);
        }
    }

    // ── フェーズ 4: 描画 ──────────────────────────────────────

    /// 全エミッタをブレンド別パイプラインで描画する（vertex pulling・6 頂点クアッド）。
    ///
    /// group0=camera（呼び出し側の共有カメラ BG）, group1=particles+params,
    /// group2=texture（未使用時は既定白）。エミッタ 0 個なら即 return。
    ///
    /// TODO: Alpha ブレンドのエミッタ単位粗ソート（現状は登録順で描画）。
    /// TODO: indirect draw count（生存数に応じた可変インスタンス数）で無駄頂点を削減。
    pub fn draw<'p>(
        &'p self,
        pass:      &mut wgpu::RenderPass<'p>,
        draw_pl:   &'p ParticlePipelines,
        camera_bg: &'p wgpu::BindGroup,
    ) {
        if self.frame.is_empty() { return; }
        // group0（camera）は additive/alpha でレイアウト共通のため 1 度だけセットする。
        pass.set_bind_group(0, camera_bg, &[]);
        for desc in &self.frame {
            let Some(g) = self.gpu.get(&desc.entity) else { continue; };
            let pipe = match desc.blend {
                ParticleBlend::Additive => &draw_pl.additive,
                ParticleBlend::Alpha    => &draw_pl.alpha,
            };
            pass.set_pipeline(pipe);
            pass.set_bind_group(1, &g.draw_bg, &[]);
            // テクスチャ使用時は emitter の texture_bg、未使用時は既定白 1x1。
            let tex_bg = if desc.params.use_texture == 1 {
                g.texture_bg.as_ref().unwrap_or(&draw_pl.default_white_bg)
            } else {
                &draw_pl.default_white_bg
            };
            pass.set_bind_group(2, tex_bg, &[]);
            // 6 頂点（2 三角形）× max_particles インスタンス（dead はシェーダで縮退）。
            pass.draw(0..6, 0..desc.max_particles);
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
                    if slot.kind != ComponentKind::ParticleEmitter { continue; }
                    // pending_burst を消費するため &mut で取得する。
                    if let Some(c) = world.get_mut::<ParticleEmitterComponent>(slot.entity) {
                        let pending = c.pending_burst;
                        c.pending_burst = 0; // 契約: 本システムが毎フレーム消費してゼロに戻す。
                        out.push(RawEmitter {
                            entity:           slot.entity,
                            world_mat:        mat,
                            max_particles:    c.max_particles,
                            emit_rate:        c.emit_rate,
                            burst:            c.burst,
                            pending_burst:    pending,
                            lifetime:         c.lifetime,
                            initial_speed:    c.initial_speed,
                            spread_angle_deg: c.spread_angle_deg,
                            direction_local:  c.direction_local,
                            gravity:          c.gravity,
                            drag:             c.drag,
                            start_size:       c.start_size,
                            end_size_scale:   c.end_size_scale,
                            start_color:      c.start_color,
                            end_color:        c.end_color,
                            texture_path:     c.texture_path.clone(),
                            blend:            c.blend,
                            sim_space:        c.sim_space,
                            playing:          c.playing,
                            loop_emit:        c.loop_emit,
                        });
                    }
                }
            }
        }
        // 子アクターを再帰走査する（world の &mut は上の借用が解放済み）。
        gather_emitters(world, actor.children(), wl, out);
    }
}

/// テクスチャファイルを読み込んで group2（テクスチャ＋サンプラー）BindGroup を作る。
///
/// asset_fs::read_image を使うため assets:// / PAK に対応する（失敗時はマゼンタ 1x1）。
fn load_particle_texture(
    device:  &wgpu::Device,
    queue:   &wgpu::Queue,
    path:    &str,
    tex_bgl: &wgpu::BindGroupLayout,
    sampler: &wgpu::Sampler,
) -> Option<wgpu::BindGroup> {
    let rgba = crate::engine::asset_fs::read_image(path);
    let (w, h) = rgba.dimensions();
    let texture = device.create_texture(&wgpu::TextureDescriptor {
        label:           Some("Particle Texture"),
        size:            wgpu::Extent3d { width: w, height: h, depth_or_array_layers: 1 },
        mip_level_count: 1,
        sample_count:    1,
        dimension:       wgpu::TextureDimension::D2,
        format:          wgpu::TextureFormat::Rgba8UnormSrgb,
        usage:           wgpu::TextureUsages::TEXTURE_BINDING | wgpu::TextureUsages::COPY_DST,
        view_formats:    &[],
    });
    queue.write_texture(
        texture.as_image_copy(),
        &rgba,
        // wgpu 25 の新名称（旧 ImageDataLayout は deprecated）。
        wgpu::TexelCopyBufferLayout {
            offset:         0,
            bytes_per_row:  Some(4 * w),
            rows_per_image: Some(h),
        },
        wgpu::Extent3d { width: w, height: h, depth_or_array_layers: 1 },
    );
    let view = texture.create_view(&Default::default());
    let bg = device.create_bind_group(&wgpu::BindGroupDescriptor {
        label:   Some("Particle Texture BG"),
        layout:  tex_bgl,
        entries: &[
            wgpu::BindGroupEntry { binding: 0, resource: wgpu::BindingResource::TextureView(&view) },
            wgpu::BindGroupEntry { binding: 1, resource: wgpu::BindingResource::Sampler(sampler) },
        ],
    });
    Some(bg)
}

/// 行優先行列（CPU）→ 列優先行列（GPU）への転置（skin_system と同一）。
fn transpose4x4(m: &[[f32; 4]; 4]) -> [[f32; 4]; 4] {
    let mut out = [[0.0f32; 4]; 4];
    for i in 0..4 { for j in 0..4 { out[i][j] = m[j][i]; } }
    out
}

// ─── レイアウト検証テスト ──────────────────────────────────────
//
// GpuParticle / GpuEmitterParams の repr(C) レイアウトは WGSL 構造体と
// バイト単位で一致していなければならない（不一致は静かに描画バグを生む）。
#[cfg(test)]
mod layout_tests {
    use super::*;
    use std::mem::{size_of, offset_of};

    /// GpuParticle は 48 バイト（PARTICLE_STRIDE と一致・array stride）。
    #[test]
    fn gpu_particle_layout() {
        assert_eq!(size_of::<GpuParticle>(), 48, "GpuParticle は 48 バイト");
        assert_eq!(PARTICLE_STRIDE, 48, "PARTICLE_STRIDE は 48 固定");
        assert_eq!(offset_of!(GpuParticle, pos),      0);
        assert_eq!(offset_of!(GpuParticle, age),      12);
        assert_eq!(offset_of!(GpuParticle, vel),      16);
        assert_eq!(offset_of!(GpuParticle, lifetime), 28);
        assert_eq!(offset_of!(GpuParticle, seed),     32);
    }

    /// GpuEmitterParams は 192 バイト。vec3/vec4 は 16 バイト境界に整列。
    #[test]
    fn gpu_emitter_params_layout() {
        assert_eq!(size_of::<GpuEmitterParams>(), 192, "GpuEmitterParams は 192 バイト");
        assert_eq!(offset_of!(GpuEmitterParams, world_mat),       0);
        assert_eq!(offset_of!(GpuEmitterParams, dt),              64);
        assert_eq!(offset_of!(GpuEmitterParams, emit_count),      68);
        assert_eq!(offset_of!(GpuEmitterParams, ring_start),      72);
        assert_eq!(offset_of!(GpuEmitterParams, max_particles),   76);
        assert_eq!(offset_of!(GpuEmitterParams, frame_nonce),     80);
        assert_eq!(offset_of!(GpuEmitterParams, drag),            84);
        assert_eq!(offset_of!(GpuEmitterParams, spread_rad),      88);
        assert_eq!(offset_of!(GpuEmitterParams, end_size_scale),  92);
        assert_eq!(offset_of!(GpuEmitterParams, direction_local), 96);
        assert_eq!(offset_of!(GpuEmitterParams, speed_min),       108);
        assert_eq!(offset_of!(GpuEmitterParams, speed_max),       112);
        assert_eq!(offset_of!(GpuEmitterParams, lifetime_min),    116);
        assert_eq!(offset_of!(GpuEmitterParams, lifetime_max),    120);
        assert_eq!(offset_of!(GpuEmitterParams, size_min),        124);
        assert_eq!(offset_of!(GpuEmitterParams, gravity),         128);
        assert_eq!(offset_of!(GpuEmitterParams, size_max),        140);
        assert_eq!(offset_of!(GpuEmitterParams, start_color),     144);
        assert_eq!(offset_of!(GpuEmitterParams, end_color),       160);
        assert_eq!(offset_of!(GpuEmitterParams, sim_space),       176);
        assert_eq!(offset_of!(GpuEmitterParams, use_texture),     180);
    }
}

// ─── WGSL 静的検証（naga parse + validate）─────────────────────
#[cfg(test)]
mod shader_tests {
    /// particle_sim.wgsl / particle_draw.wgsl を naga で parse + validate する。
    /// どちらも自己完結（外部連結不要）でパース可能な構成である。
    #[test]
    fn particle_shaders_parse_and_validate() {
        let variants: [(&str, &str); 2] = [
            ("particle_sim",  include_str!("shaders/particle_sim.wgsl")),
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
