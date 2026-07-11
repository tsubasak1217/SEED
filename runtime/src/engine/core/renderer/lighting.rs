// ============================================================
//  lighting.rs — ライト GPU バッファとライト構造体
//
//  シーンの LightComponent を毎フレーム収集して GPU の storage buffer
//  （ライト配列）と uniform（ライト数メタ）へアップロードするための
//  型・バッファ管理を提供する。
//
//  実際のシェーディングは shader_fragment.wgsl 内のライトループが行う。
//  本モジュールは「CPU 側のライトデータ ↔ GPU バッファ」の橋渡しに徹する。
//
//  【バインドグループ】group 4（mesh / skinned_mesh パイプライン共通）
//    binding 0: array<GpuLight>（storage, read）
//    binding 1: LightMeta（uniform, ライト数）
//  シェーダの宣言（shader_common.wgsl の group 4）と厳密に一致させること。
// ============================================================

use wgpu::util::DeviceExt;

/// GPU に送れるライトの最大数。
///
/// storage buffer を固定容量で確保し、毎フレーム有効なライトのみ書き込む。
/// フォワードのper-fragmentループのため、多すぎるとフラグメント負荷が増える。
pub const MAX_LIGHTS: usize = 64;

// ─── ライト種別コード ─────────────────────────────────────────
// LightKind::to_code() およびシェーダの LIGHT_KIND_* 定数と一致させること。

/// 平行光
pub const LIGHT_KIND_DIRECTIONAL: u32 = 0;
/// 点光源
pub const LIGHT_KIND_POINT: u32 = 1;
/// スポット光
pub const LIGHT_KIND_SPOT: u32 = 2;
/// 矩形エリアライト
pub const LIGHT_KIND_RECT: u32 = 3;

// ─── GpuLight ────────────────────────────────────────────────

/// GPU の storage buffer に格納する 1 ライト分のデータ（96 bytes）。
///
/// WGSL storage の std430 相当アライメント（vec3 は 16 バイト境界）に合わせ、
/// 明示的なパディングフィールドで詰める。bytemuck::Pod のため隙間バイトを残さない。
///
/// | offset | field            | size |
/// |--------|------------------|------|
/// |   0    | color            |  12  |
/// |  12    | intensity        |   4  |
/// |  16    | position         |  12  |
/// |  28    | range            |   4  |
/// |  32    | direction        |  12  |
/// |  44    | kind (u32)       |   4  |
/// |  48    | inner_cos        |   4  |
/// |  52    | outer_cos        |   4  |
/// |  56    | rect_half_width  |   4  |
/// |  60    | rect_half_height |   4  |
/// |  64    | rect_right       |  12  |
/// |  76    | shadow_index     |   4  |
/// |  80    | rect_up          |  12  |
/// |  92    | _pad1            |   4  |
/// 合計 96（16 の倍数 → array stride も 96）
#[repr(C)]
#[derive(Debug, Clone, Copy, bytemuck::Pod, bytemuck::Zeroable)]
pub struct GpuLight {
    /// 光の色（リニア RGB）。強度は color とは別に intensity で持つ。
    pub color:            [f32; 3],
    /// 光の強度（color に乗算する係数）。
    pub intensity:        f32,
    /// ワールド位置（point/spot/rect で使用。directional では未使用）。
    pub position:         [f32; 3],
    /// 減衰距離（point/spot。この距離付近で消灯）。
    pub range:            f32,
    /// 照射方向（光が進む向き＝Transform::forward()）。
    /// シェーダは L = -direction（面から光源への向き）として使う。
    pub direction:        [f32; 3],
    /// 種別コード（LIGHT_KIND_*）。
    pub kind:             u32,
    /// スポット内側コーンの cos（この cos より大きい＝内側は全光量）。
    pub inner_cos:        f32,
    /// スポット外側コーンの cos（この cos で 0 まで減衰）。
    pub outer_cos:        f32,
    /// rect の半幅（rect_right 方向）。
    pub rect_half_width:  f32,
    /// rect の半高（rect_up 方向）。
    pub rect_half_height: f32,
    /// rect の右方向ベクトル（面の横軸、正規化）。
    pub rect_right:       [f32; 3],
    /// 影スロット（Phase R2）。-1 = 影なし。
    /// 方向光: 0 = CSM 有効（影付き方向光は最大 1 灯）。
    /// スポット: 0..MAX_SHADOW_SPOTS-1 = スポットシャドウ配列のレイヤ番号。
    /// シェーダ（shadow.wgsl / shader_fragment.wgsl）が f32→i32 で判定する。
    pub shadow_index:     f32,
    /// rect の上方向ベクトル（面の縦軸、正規化）。
    pub rect_up:          [f32; 3],
    /// アライメント用パディング。
    pub _pad1:            f32,
}

impl GpuLight {
    /// ゼロ値（未使用スロットの埋め草）。
    pub fn zeroed() -> Self { bytemuck::Zeroable::zeroed() }

    /// 平行光を構築する。
    ///
    /// `direction` は光が進む向き（正規化済みを想定）。
    pub fn directional(direction: [f32; 3], color: [f32; 3], intensity: f32) -> Self {
        Self {
            color,
            intensity,
            position:         [0.0; 3],
            range:            0.0,
            direction:        normalize(direction),
            kind:             LIGHT_KIND_DIRECTIONAL,
            inner_cos:        0.0,
            outer_cos:        0.0,
            rect_half_width:  0.0,
            rect_half_height: 0.0,
            rect_right:       [0.0; 3],
            shadow_index:     -1.0,
            rect_up:          [0.0; 3],
            _pad1:            0.0,
        }
    }

    /// 点光源を構築する。
    pub fn point(position: [f32; 3], color: [f32; 3], intensity: f32, range: f32) -> Self {
        Self {
            color,
            intensity,
            position,
            range:            range.max(1e-3),
            direction:        [0.0, 0.0, 1.0],
            kind:             LIGHT_KIND_POINT,
            inner_cos:        0.0,
            outer_cos:        0.0,
            rect_half_width:  0.0,
            rect_half_height: 0.0,
            rect_right:       [0.0; 3],
            shadow_index:     -1.0,
            rect_up:          [0.0; 3],
            _pad1:            0.0,
        }
    }

    /// スポット光を構築する。
    ///
    /// `inner_deg`/`outer_deg` は半角（コーン中心軸からの角度）で、
    /// inner ≤ outer を保証して cos に変換する。
    pub fn spot(
        position:  [f32; 3],
        direction: [f32; 3],
        color:     [f32; 3],
        intensity: f32,
        range:     f32,
        inner_deg: f32,
        outer_deg: f32,
    ) -> Self {
        // 内側は外側以下に丸める（逆転していると減衰式が破綻するため）。
        let outer = outer_deg.max(0.0);
        let inner = inner_deg.clamp(0.0, outer);
        Self {
            color,
            intensity,
            position,
            range:            range.max(1e-3),
            direction:        normalize(direction),
            kind:             LIGHT_KIND_SPOT,
            inner_cos:        inner.to_radians().cos(),
            outer_cos:        outer.to_radians().cos(),
            rect_half_width:  0.0,
            rect_half_height: 0.0,
            rect_right:       [0.0; 3],
            shadow_index:     -1.0,
            rect_up:          [0.0; 3],
            _pad1:            0.0,
        }
    }

    /// 矩形エリアライトを構築する。
    ///
    /// `direction` は面の法線（光が進む向き）、`right`/`up` は面の横・縦軸。
    pub fn rect(
        position:  [f32; 3],
        direction: [f32; 3],
        right:     [f32; 3],
        up:        [f32; 3],
        color:     [f32; 3],
        intensity: f32,
        range:     f32,
        width:     f32,
        height:    f32,
    ) -> Self {
        Self {
            color,
            intensity,
            position,
            range:            range.max(1e-3),
            direction:        normalize(direction),
            kind:             LIGHT_KIND_RECT,
            inner_cos:        0.0,
            outer_cos:        0.0,
            rect_half_width:  (width * 0.5).max(1e-4),
            rect_half_height: (height * 0.5).max(1e-4),
            rect_right:       normalize(right),
            shadow_index:     -1.0,
            rect_up:          normalize(up),
            _pad1:            0.0,
        }
    }
}

/// ベクトルを正規化する（長さ 0 は [0,0,1] にフォールバック）。
fn normalize(v: [f32; 3]) -> [f32; 3] {
    let len = (v[0] * v[0] + v[1] * v[1] + v[2] * v[2]).sqrt();
    if len < 1e-6 { [0.0, 0.0, 1.0] } else { [v[0] / len, v[1] / len, v[2] / len] }
}

// ─── LightMeta ───────────────────────────────────────────────

/// ライト配列のメタ情報 uniform（16 bytes）。
///
/// count のみ意味を持ち、残りは 16 バイト境界のためのパディング。
#[repr(C)]
#[derive(Debug, Clone, Copy, bytemuck::Pod, bytemuck::Zeroable)]
pub struct LightMeta {
    /// 有効なライト数（シェーダはこの数だけループする）。
    pub count: u32,
    /// アライメント用パディング。
    pub _pad:  [u32; 3],
}

// ─── LightBuffer ─────────────────────────────────────────────

/// ライト用 GPU バッファ一式（storage 配列 + メタ uniform + bind group）。
///
/// 容量 MAX_LIGHTS の storage buffer を確保し、毎フレーム `update()` で
/// 有効ライトのみを書き込み、メタにライト数を書く。
pub struct LightBuffer {
    /// array<GpuLight>（storage, read）。容量 MAX_LIGHTS 固定。
    lights_buffer: wgpu::Buffer,
    /// LightMeta（uniform）。
    meta_buffer:   wgpu::Buffer,
    /// group 4 の bind group（binding 0 = lights, 1 = meta）。
    pub bind_group: wgpu::BindGroup,
}

impl LightBuffer {
    /// ライトバッファ一式を生成する。`bgl` は mesh パイプラインの group 4 レイアウト。
    pub fn new(device: &wgpu::Device, bgl: &wgpu::BindGroupLayout) -> Self {
        // storage 配列は最初から MAX_LIGHTS 分ゼロ確保する（実行時サイズ変更を避ける）。
        let init_lights = vec![GpuLight::zeroed(); MAX_LIGHTS];
        let lights_buffer = device.create_buffer_init(&wgpu::util::BufferInitDescriptor {
            label:    Some("Lights Storage Buffer"),
            contents: bytemuck::cast_slice(&init_lights),
            usage:    wgpu::BufferUsages::STORAGE | wgpu::BufferUsages::COPY_DST,
        });

        let init_meta = LightMeta { count: 0, _pad: [0; 3] };
        let meta_buffer = device.create_buffer_init(&wgpu::util::BufferInitDescriptor {
            label:    Some("Light Meta Uniform"),
            contents: bytemuck::bytes_of(&init_meta),
            usage:    wgpu::BufferUsages::UNIFORM | wgpu::BufferUsages::COPY_DST,
        });

        let bind_group = device.create_bind_group(&wgpu::BindGroupDescriptor {
            label:   Some("Lights BG"),
            layout:  bgl,
            entries: &[
                wgpu::BindGroupEntry { binding: 0, resource: lights_buffer.as_entire_binding() },
                wgpu::BindGroupEntry { binding: 1, resource: meta_buffer.as_entire_binding() },
            ],
        });

        Self { lights_buffer, meta_buffer, bind_group }
    }

    /// 有効ライト配列を GPU へアップロードする（MAX_LIGHTS を超える分は切り捨て）。
    ///
    /// storage 配列の未使用スロットは前フレームの値が残るが、meta.count で
    /// ループ範囲を制限するためシェーディングには影響しない。
    pub fn update(&self, queue: &wgpu::Queue, lights: &[GpuLight]) {
        let count = lights.len().min(MAX_LIGHTS);
        if count > 0 {
            queue.write_buffer(&self.lights_buffer, 0, bytemuck::cast_slice(&lights[..count]));
        }
        let meta = LightMeta { count: count as u32, _pad: [0; 3] };
        queue.write_buffer(&self.meta_buffer, 0, bytemuck::bytes_of(&meta));
    }
}


