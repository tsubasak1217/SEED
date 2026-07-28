// ============================================================
//  water/params.rs — 水面描画の GPU 側パラメータ
//
//  `ResolvedWaterVolume`（エンジン層の中間表現）を、シェーダが読む
//  ストレージバッファ要素へ詰め替えるだけの層。
//  「エンジンの水表現」と「描画都合のレイアウト」を型で分離しておくことで、
//  シェーダのレイアウト変更がエンジン層へ波及しない。
// ============================================================

use crate::engine::water::ResolvedWaterVolume;
use crate::engine::components::water_volume_component::WaterVolumeKind;

/// 1 ドローで描ける水ボリュームの最大数。
/// これを超えた分は切り捨てる（描画順の先頭から採用）。
/// ストレージバッファはこの容量まで自動で伸びる。
pub const WATER_MAX_VOLUMES: usize = 64;

/// 水ボリューム 1 個ぶんの GPU パラメータ。
///
/// **全フィールドを vec4 相当（`[f32; 4]`）で構成している**。
/// std430 のアラインメント規則では vec3 が 16 バイト境界へ寄せられて暗黙のパディングが
/// 生じるため、そもそも vec3 を持たせないことでレイアウト事故を構造的に排除する。
/// WGSL 側 `water_surface.wgsl` の `struct WaterParams` とフィールド順を厳密一致させること。
#[repr(C)]
#[derive(Debug, Clone, Copy, bytemuck::Pod, bytemuck::Zeroable)]
pub struct WaterParams {
    /// xyz = 水面クアッド中心のワールド座標（y = 水面 Y）／w = 未使用
    pub center: [f32; 4],
    /// x,z = クアッドの片側半径（m）／y,w = 未使用
    pub half_extent: [f32; 4],
    /// rgb = 浅場の色／a = 吸収距離（m）
    pub shallow_color: [f32; 4],
    /// rgb = 深場の色／a = 深場での最大不透明度
    pub deep_color: [f32; 4],
    /// rgb = 岸フォームの色／a = フォーム幅（m）
    pub foam_color: [f32; 4],
    /// rgb = 簡易反射色／a = フォーム強度
    pub reflection_color: [f32; 4],
    /// x = 波振幅／y = 波の空間周波数／z = 波速度／w = 屈折歪み
    pub wave: [f32; 4],
    /// x = フレネル指数／y = フレネル寄与率／z,w = 未使用
    pub fresnel: [f32; 4],
}

impl WaterParams {
    /// `ResolvedWaterVolume` から GPU パラメータを作る。
    ///
    /// `camera_pos` は Ocean のカメラ追従に使う（Ocean は XZ 無限の想定なので、
    /// カメラ位置を中心とした `ocean_extent` 半径のクアッドを毎フレーム置き直す）。
    /// Region は AABB の中心 XZ・半径 XZ をそのまま使い、Y は解決済みの水面 Y。
    pub fn from_resolved(v: &ResolvedWaterVolume, camera_pos: [f32; 3]) -> Self {
        // Ocean は「カメラ追従の巨大クアッド」、Region は「AABB 上面の矩形」。
        let (cx, cz, hx, hz) = match v.kind {
            WaterVolumeKind::Ocean => (
                camera_pos[0], camera_pos[2],
                v.ocean_extent, v.ocean_extent,
            ),
            _ => (
                v.center[0], v.center[2],
                v.half_extents[0], v.half_extents[2],
            ),
        };
        let vis = &v.visual;
        Self {
            center:      [cx, v.surface_y, cz, 0.0],
            half_extent: [hx, 0.0, hz, 0.0],
            shallow_color: [
                vis.shallow_color[0], vis.shallow_color[1], vis.shallow_color[2],
                vis.absorption_distance,
            ],
            deep_color: [
                vis.deep_color[0], vis.deep_color[1], vis.deep_color[2],
                vis.surface_opacity,
            ],
            foam_color: [
                vis.foam_color[0], vis.foam_color[1], vis.foam_color[2],
                vis.foam_width,
            ],
            reflection_color: [
                vis.reflection_color[0], vis.reflection_color[1], vis.reflection_color[2],
                vis.foam_intensity,
            ],
            wave: [
                vis.wave_amplitude, vis.wave_scale, vis.wave_speed, vis.refraction_distortion,
            ],
            fresnel: [vis.fresnel_power, vis.fresnel_strength, 0.0, 0.0],
        }
    }
}

// ============================================================
//  テスト
// ============================================================
#[cfg(test)]
mod tests {
    use super::*;

    /// std430 のストレージバッファ配列要素として安全なサイズ・アラインメントであること。
    /// vec4 のみで構成しているので 16 の倍数・16 アラインになるはず（暗黙パディング無し）。
    #[test]
    fn water_params_layout_is_std430_safe() {
        assert_eq!(std::mem::size_of::<WaterParams>() % 16, 0,
            "WaterParams のサイズは 16 の倍数であること（std430 の配列ストライド）");
        assert_eq!(std::mem::size_of::<WaterParams>(), 8 * 16,
            "WaterParams は vec4 8 本ぶん（128 バイト）であること。\
             WGSL 側 struct WaterParams と同期すること");
        assert_eq!(std::mem::align_of::<WaterParams>(), 4,
            "repr(C) の [f32;4] 配列なので Rust 側アラインは 4（バイト列は 16 の倍数長で連続する）");
    }

    /// Ocean はカメラ XZ 追従、Region は AABB 中心を使うこと。
    #[test]
    fn ocean_follows_camera_region_uses_center() {
        use crate::engine::water::WaterVisualParams;
        let visual = WaterVisualParams {
            shallow_color: [0.0; 3], deep_color: [0.0; 3], absorption_distance: 1.0,
            surface_opacity: 1.0, foam_color: [0.0; 3], foam_width: 1.0, foam_intensity: 1.0,
            wave_amplitude: 1.0, wave_scale: 1.0, wave_speed: 1.0, fresnel_power: 1.0,
            fresnel_strength: 1.0, reflection_color: [0.0; 3], refraction_distortion: 0.0,
        };
        let ocean = ResolvedWaterVolume {
            kind: WaterVolumeKind::Ocean, surface_y: 3.0,
            center: [0.0; 3], half_extents: [0.0; 3], ocean_extent: 500.0, visual,
        };
        let p = WaterParams::from_resolved(&ocean, [10.0, 5.0, -20.0]);
        assert_eq!(p.center, [10.0, 3.0, -20.0, 0.0]);
        assert_eq!(p.half_extent, [500.0, 0.0, 500.0, 0.0]);

        let region = ResolvedWaterVolume {
            kind: WaterVolumeKind::Region, surface_y: 2.0,
            center: [1.0, 0.0, 2.0], half_extents: [4.0, 1.0, 6.0], ocean_extent: 500.0, visual,
        };
        let q = WaterParams::from_resolved(&region, [10.0, 5.0, -20.0]);
        assert_eq!(q.center, [1.0, 2.0, 2.0, 0.0]);
        assert_eq!(q.half_extent, [4.0, 0.0, 6.0, 0.0]);
    }
}
