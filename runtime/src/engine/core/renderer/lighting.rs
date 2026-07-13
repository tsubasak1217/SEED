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
//  デバイスの max_bind_groups=5（group 0〜4）環境に対応するため、
//  Phase R2 のシャドウ資源も group 4 へ同居させた複合レイアウトになっている。
//    binding 0: array<GpuLight>（storage, read）
//    binding 1: LightMeta（uniform, ライト数）
//    binding 2: CSM 深度配列（texture_depth_2d_array, ShadowResources 所有）
//    binding 3: スポット深度配列（texture_depth_2d_array, ShadowResources 所有）
//    binding 4: 比較サンプラー（LessEqual, ShadowResources 所有）
//    binding 5: ShadowMatrices UBO（ShadowResources 所有）
//    binding 6: TLAS（RT 影バリアントのみ。RtShadowResources 所有）
//    binding 7: array<ClusterCell>（storage, read。ClusterResources 所有・Phase C1）
//    binding 8: array<u32> クラスタライトインデックス（storage, read。同上）
//    binding 9: ClusterParams（uniform。同上。**BG ごとに差し替わる**）
//  シェーダの宣言（cluster_common.wgsl / shader_common.wgsl / shadow.wgsl の group 4）と
//  厳密に一致させること。
// ============================================================

use wgpu::util::DeviceExt;

use super::shadow::ShadowResources;

/// GPU に送れるライトの最大数。
///
/// storage buffer を固定容量で確保し、毎フレーム有効なライトのみ書き込む。
///
/// Phase C1（Clustered Lighting）で 64 → 1024 へ引き上げた。フラグメントは
/// 全ライトではなく「自分のクラスタのライトリスト ＋ 全平行光」しか走査しないため、
/// 灯数を増やしてもフラグメント負荷は増えない（増えるのはクラスタ構築 compute の
/// コストと storage buffer の容量 = 1024 × 96B ≒ 96KB だけ）。
/// 上限を更に上げる場合は renderer/clustered.rs の MAX_LIGHTS_PER_CLUSTER
/// （1 クラスタが保持できる灯数）も併せて検討すること。
pub const MAX_LIGHTS: usize = 1024;

// ─── ライト種別コード ─────────────────────────────────────────
// LightKind::to_code() およびシェーダの LIGHT_KIND_* 定数と一致させること。

/// 環境光（アンビエント）の既定色（白）。従来のハードコード値と同一の見た目を維持する。
pub const DEFAULT_AMBIENT_COLOR: [f32; 3] = [1.0, 1.0, 1.0];
/// 環境光（アンビエント）の既定強度。旧シェーダの `vec3(0.05)` と一致（0 で完全な暗闇）。
pub const DEFAULT_AMBIENT_INTENSITY: f32 = 0.05;

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
/// |  92    | soft_radius      |   4  |
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
    /// ソフト影の見込み半径（Phase R8 ソフトシャドウ）。0 = ハードシャドウ。
    /// directional: tan(角径) の無次元スロープ（collect_gpu_lights で度→tan 変換済み）。
    /// point/spot/rect: 光源のワールド半径（シェーダで radius/距離＝見込み角に換算）。
    /// 旧レイアウトの _pad1（offset 92）を再利用するため 96 バイトは不変。
    pub soft_radius:      f32,
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
            soft_radius:      0.0,
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
            soft_radius:      0.0,
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
            soft_radius:      0.0,
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
            soft_radius:      0.0,
        }
    }
}

/// ベクトルを正規化する（長さ 0 は [0,0,1] にフォールバック）。
fn normalize(v: [f32; 3]) -> [f32; 3] {
    let len = (v[0] * v[0] + v[1] * v[1] + v[2] * v[2]).sqrt();
    if len < 1e-6 { [0.0, 0.0, 1.0] } else { [v[0] / len, v[1] / len, v[2] / len] }
}

// ─── LightMeta ───────────────────────────────────────────────

/// ライト配列のメタ情報 uniform（32 bytes）。
///
/// count / rt_shadows / ambient_* が意味を持ち、_pad は 16 バイト境界のためのパディング。
/// WGSL 側 LightMeta（shader_common.wgsl）と厳密に一致させること:
///   先頭スカラー 4 つ（16B）→ ambient_color: vec3（16B 境界, offset 16）→ ambient_intensity（offset 28）。
///
/// | offset | field             | size |
/// |--------|-------------------|------|
/// |   0    | count             |   4  |
/// |   4    | rt_shadows        |   4  |
/// |   8    | _pad[0]           |   4  |
/// |  12    | _pad[1]           |   4  |
/// |  16    | ambient_color     |  12  |
/// |  28    | ambient_intensity |   4  |
/// 合計 32
#[repr(C)]
#[derive(Debug, Clone, Copy, bytemuck::Pod, bytemuck::Zeroable)]
pub struct LightMeta {
    /// 有効なライト数（シェーダはこの数だけループする）。
    pub count:      u32,
    /// インラインレイトレ影の有効フラグ（Phase R8, 0=無効/1=有効）。
    /// RT 対応 GPU でのみ RT パイプラインが読む。有効時は全ライト種で遮蔽レイを飛ばし、
    /// 無効時（および RT 非対応パイプライン）は従来のシャドウマップ経路を使う。
    pub rt_shadows: u32,
    /// アライメント用パディング（ambient_color を 16 バイト境界へ揃える）。
    pub _pad:       [u32; 2],
    /// 環境光の色（リニア RGB, Phase R1.5）。既定は白。
    pub ambient_color: [f32; 3],
    /// 環境光の強度（Phase R1.5）。既定 0.05（従来のハードコード値）。0 で完全な暗闇。
    pub ambient_intensity: f32,
}

// ─── LightBuffer ─────────────────────────────────────────────

/// ライト用 GPU バッファ一式（storage 配列 + メタ uniform + bind group）。
///
/// 容量 MAX_LIGHTS の storage buffer を確保し、毎フレーム `update()` で
/// 有効ライトのみを書き込み、メタにライト数を書く。
pub struct LightBuffer {
    /// array<GpuLight>（storage, read）。容量 MAX_LIGHTS 固定。
    /// クラスタ構築 compute（clustered.rs）も同じバッファを group 0 で読む。
    lights_buffer: wgpu::Buffer,
    /// LightMeta（uniform）。
    meta_buffer:   wgpu::Buffer,
    /// group 4 の複合 bind group（binding 0 = lights, 1 = meta,
    /// 2 = CSM 深度配列, 3 = スポット深度配列, 4 = 比較サンプラー, 5 = シャドウ行列 UBO,
    /// 7 = クラスタグリッド, 8 = クラスタライトインデックス, 9 = クラスタパラメータ）。
    /// ライトバッファもシャドウ資源もクラスタバッファも生成後不変のため、
    /// 起動時 1 回だけ生成して使い回す（中身の更新は queue.write_buffer /
    /// 深度パス描画 / compute で行われ、BG 再生成は不要）。
    ///
    /// **こちらは「クラスタ有効」側**（binding 9 = メインカメラ用 ClusterParams）。
    /// メインカメラで描く全パス（不透明・距離ソート透明・WBOIT・ギズモアイコン）で使う。
    pub bind_group: wgpu::BindGroup,
    /// **クラスタ無効**側の group 4 複合 bind group（Phase C1）。
    ///
    /// binding 9 に enabled=0 固定の ClusterParams を差した以外は `bind_group` と同一
    /// （ライト・シャドウ・クラスタバッファはすべて共有する）。
    /// これを bind したパスのフラグメントは、クラスタを一切参照せず従来どおり
    /// 全ライトを線形走査する。
    ///
    /// クラスタは**カメラごとに固有**（near/far/fov/ビューポート依存）であり、
    /// メインカメラ基準で構築したクラスタをカメラプレビューのパスで使うと
    /// プレビューのライティングが壊れる（ライトが飛ぶ／暗くなる）。
    /// カメラプレビューのパスは必ずこちらを bind すること（frame_renderer.rs）。
    pub bind_group_unclustered: wgpu::BindGroup,
}

impl LightBuffer {
    /// ライトバッファ一式と group 4 複合 bind group（クラスタ有効／無効の 2 本）を生成する。
    ///
    /// - `bgl`:      mesh パイプラインの group 4 レイアウト（ライト＋シャドウ＋クラスタ複合）。
    /// - `shadow`:   シャドウ資源（binding 2〜5 のビュー・サンプラー・UBO を供給）。
    /// - `clusters`: クラスタ資源（binding 7〜9 のグリッド・インデックス・パラメータを供給）。
    pub fn new(
        device:   &wgpu::Device,
        bgl:      &wgpu::BindGroupLayout,
        shadow:   &ShadowResources,
        clusters: &super::clustered::ClusterResources,
    ) -> Self {
        // storage 配列は最初から MAX_LIGHTS 分ゼロ確保する（実行時サイズ変更を避ける）。
        let init_lights = vec![GpuLight::zeroed(); MAX_LIGHTS];
        let lights_buffer = device.create_buffer_init(&wgpu::util::BufferInitDescriptor {
            label:    Some("Lights Storage Buffer"),
            contents: bytemuck::cast_slice(&init_lights),
            usage:    wgpu::BufferUsages::STORAGE | wgpu::BufferUsages::COPY_DST,
        });

        let init_meta = LightMeta {
            count:             0,
            rt_shadows:        0,
            _pad:              [0; 2],
            ambient_color:     DEFAULT_AMBIENT_COLOR,
            ambient_intensity: DEFAULT_AMBIENT_INTENSITY,
        };
        let meta_buffer = device.create_buffer_init(&wgpu::util::BufferInitDescriptor {
            label:    Some("Light Meta Uniform"),
            contents: bytemuck::bytes_of(&init_meta),
            usage:    wgpu::BufferUsages::UNIFORM | wgpu::BufferUsages::COPY_DST,
        });

        // group 4 複合 BG（ライト binding 0/1 ＋ シャドウ binding 2〜5 ＋ クラスタ binding 7〜9）。
        // shadow.wgsl / shader_common.wgsl の group 4 宣言と一致させること。
        // クラスタ有効／無効は binding 9（ClusterParams）だけが異なる 2 本を作る。
        let make_bg = |label: &str, params: &wgpu::Buffer| {
            device.create_bind_group(&wgpu::BindGroupDescriptor {
                label:   Some(label),
                layout:  bgl,
                entries: &[
                    wgpu::BindGroupEntry { binding: 0, resource: lights_buffer.as_entire_binding() },
                    wgpu::BindGroupEntry { binding: 1, resource: meta_buffer.as_entire_binding() },
                    wgpu::BindGroupEntry { binding: 2, resource: wgpu::BindingResource::TextureView(&shadow.dir_array_view) },
                    wgpu::BindGroupEntry { binding: 3, resource: wgpu::BindingResource::TextureView(&shadow.spot_array_view) },
                    wgpu::BindGroupEntry { binding: 4, resource: wgpu::BindingResource::Sampler(&shadow.sampler) },
                    wgpu::BindGroupEntry { binding: 5, resource: shadow.ubo.as_entire_binding() },
                    wgpu::BindGroupEntry { binding: 7, resource: clusters.grid_buffer().as_entire_binding() },
                    wgpu::BindGroupEntry { binding: 8, resource: clusters.indices_buffer().as_entire_binding() },
                    wgpu::BindGroupEntry { binding: 9, resource: params.as_entire_binding() },
                ],
            })
        };
        let bind_group = make_bg("Lights+Shadow+Cluster BG (group 4)", clusters.params_buffer());
        let bind_group_unclustered = make_bg(
            "Lights+Shadow+Cluster BG (group 4, cluster disabled)",
            clusters.params_disabled_buffer(),
        );

        Self { lights_buffer, meta_buffer, bind_group, bind_group_unclustered }
    }

    /// ライト storage buffer への参照（クラスタ構築 compute の BindGroup 生成に使う）。
    pub fn lights_buffer(&self) -> &wgpu::Buffer { &self.lights_buffer }

    /// 有効ライト配列を GPU へアップロードする（MAX_LIGHTS を超える分は切り捨て）。
    ///
    /// storage 配列の未使用スロットは前フレームの値が残るが、meta.count で
    /// ループ範囲を制限するためシェーディングには影響しない。
    ///
    /// - `rt_shadows`: このフレームでインラインレイトレ影を使うか（Phase R8）。
    ///   RT パイプラインのフラグメントはこの値でシャドウマップ/RT を実行時分岐する。
    /// - `ambient_color` / `ambient_intensity`: 環境光（Phase R1.5）。
    ///   フラグメントの `ambient = ambient_color * ambient_intensity * albedo * ao`。
    pub fn update(
        &self,
        queue:             &wgpu::Queue,
        lights:            &[GpuLight],
        rt_shadows:        bool,
        ambient_color:     [f32; 3],
        ambient_intensity: f32,
    ) {
        let count = lights.len().min(MAX_LIGHTS);
        if count > 0 {
            queue.write_buffer(&self.lights_buffer, 0, bytemuck::cast_slice(&lights[..count]));
        }
        let meta = LightMeta {
            count:      count as u32,
            rt_shadows: if rt_shadows { 1 } else { 0 },
            _pad:       [0; 2],
            ambient_color,
            ambient_intensity,
        };
        queue.write_buffer(&self.meta_buffer, 0, bytemuck::bytes_of(&meta));
    }

    /// RT 影用の group 4 複合 BindGroup を生成する（Phase R8, RT 対応時のみ）。
    ///
    /// 通常の bind group（binding 0〜5 ＋ クラスタ 7〜9）に TLAS（binding 6）を加えたもの。
    /// ライトバッファ・シャドウ資源・クラスタ資源は通常 BG と同一のものを共有し、TLAS のみ追加する。
    /// `rt_lights_bgl` は mesh_rt パイプライン由来（acceleration_structure を含む group 4）。
    ///
    /// RT パイプラインを使うのはメインカメラのパスだけなので、クラスタは常に有効側
    /// （params_buffer）を差す（カメラプレビューは RT を使わない＝R8 からの方針）。
    pub fn create_rt_bind_group(
        &self,
        device:        &wgpu::Device,
        rt_lights_bgl: &wgpu::BindGroupLayout,
        shadow:        &ShadowResources,
        clusters:      &super::clustered::ClusterResources,
        tlas:          &wgpu::Tlas,
    ) -> wgpu::BindGroup {
        device.create_bind_group(&wgpu::BindGroupDescriptor {
            label:   Some("Lights+Shadow+Cluster+TLAS BG (group 4, RT)"),
            layout:  rt_lights_bgl,
            entries: &[
                wgpu::BindGroupEntry { binding: 0, resource: self.lights_buffer.as_entire_binding() },
                wgpu::BindGroupEntry { binding: 1, resource: self.meta_buffer.as_entire_binding() },
                wgpu::BindGroupEntry { binding: 2, resource: wgpu::BindingResource::TextureView(&shadow.dir_array_view) },
                wgpu::BindGroupEntry { binding: 3, resource: wgpu::BindingResource::TextureView(&shadow.spot_array_view) },
                wgpu::BindGroupEntry { binding: 4, resource: wgpu::BindingResource::Sampler(&shadow.sampler) },
                wgpu::BindGroupEntry { binding: 5, resource: shadow.ubo.as_entire_binding() },
                wgpu::BindGroupEntry { binding: 6, resource: wgpu::BindingResource::AccelerationStructure(tlas) },
                wgpu::BindGroupEntry { binding: 7, resource: clusters.grid_buffer().as_entire_binding() },
                wgpu::BindGroupEntry { binding: 8, resource: clusters.indices_buffer().as_entire_binding() },
                wgpu::BindGroupEntry { binding: 9, resource: clusters.params_buffer().as_entire_binding() },
            ],
        })
    }
}

// ─── レイアウト検証テスト ──────────────────────────────────────
//
// GpuLight / LightMeta の repr(C) レイアウトは shader_common.wgsl の WGSL 構造体と
// バイト単位で一致していなければならない（不一致は静かに描画バグを生む）。
// 変更時に気づけるよう、サイズ・オフセットを固定値で検証する。
#[cfg(test)]
mod layout_tests {
    use super::*;
    use std::mem::{size_of, offset_of};

    /// GpuLight は 96 バイト（array stride も 96）。soft_radius は旧 _pad1 の offset 92 を再利用。
    #[test]
    fn gpu_light_layout() {
        assert_eq!(size_of::<GpuLight>(), 96, "GpuLight は 96 バイト（WGSL stride と一致）");
        assert_eq!(offset_of!(GpuLight, shadow_index), 76);
        assert_eq!(offset_of!(GpuLight, rect_up),      80);
        assert_eq!(offset_of!(GpuLight, soft_radius),  92);
    }

    /// LightMeta は 32 バイト。ambient_color は 16 バイト境界（offset 16）、ambient_intensity は offset 28。
    #[test]
    fn light_meta_layout() {
        assert_eq!(size_of::<LightMeta>(), 32, "LightMeta は 32 バイト（WGSL uniform と一致）");
        assert_eq!(offset_of!(LightMeta, count),             0);
        assert_eq!(offset_of!(LightMeta, rt_shadows),        4);
        assert_eq!(offset_of!(LightMeta, ambient_color),     16);
        assert_eq!(offset_of!(LightMeta, ambient_intensity), 28);
    }
}


