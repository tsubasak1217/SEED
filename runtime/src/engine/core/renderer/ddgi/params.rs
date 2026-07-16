// ============================================================
// ddgi/params.rs — GiParams（GPU uniform）と CPU→GPU パラメータ変換
//
// ## 役割（単一責任）
// プローブ格子・アトラス配置・更新ノブ（強度/ヒステリシス/再帰重み/レイ数）を
// 1 個の uniform 構造体へ詰める。描画側（group4 binding10）と compute（group0 binding0）の
// 両方が同じ GiParams を読む。WGSL ミラーは ddgi_common.wgsl。
//
// ## vec3 パディング禁止（align16 の地雷）
// WGSL の vec3<f32> は align 16 でストライドが化けるため使用禁止。origin/spacing は
// スカラー 3 本で詰める（WGSL 側も f32 スカラー 3 本）。全フィールド 4 バイト境界で、
// 合計 80 バイト（16 の倍数）。naga レイアウト照合テストでズレを止める。
// ============================================================

use super::grid::GiGrid;

/// DDGI の GPU パラメータ（uniform, 80 バイト）。
///
/// | offset | field             |
/// |--------|-------------------|
/// |   0    | origin_x/y/z      |
/// |  12    | spacing_x/y/z     |
/// |  24    | dim_x/y/z (u32)   |
/// |  36    | probe_count (u32) |
/// |  40    | atlas_cols (u32)  |
/// |  44    | atlas_rows (u32)  |
/// |  48    | enabled (u32)     |
/// |  52    | rays_per_probe    |
/// |  56    | probe_update_base |
/// |  60    | frame_index (u32) |
/// |  64    | intensity (f32)   |
/// |  68    | hysteresis (f32)  |
/// |  72    | recursive_weight  |
/// |  76    | _pad0 (u32)       |
/// 合計 80（16 の倍数）。
#[repr(C)]
#[derive(Debug, Clone, Copy, bytemuck::Pod, bytemuck::Zeroable)]
pub struct GiParams {
    pub origin_x: f32,
    pub origin_y: f32,
    pub origin_z: f32,
    pub spacing_x: f32,
    pub spacing_y: f32,
    pub spacing_z: f32,
    pub dim_x: u32,
    pub dim_y: u32,
    pub dim_z: u32,
    pub probe_count: u32,
    pub atlas_cols: u32,
    pub atlas_rows: u32,
    /// 0 = 無効（描画側はフラットアンビエントへフォールバック、compute は走らせない）。
    pub enabled: u32,
    pub rays_per_probe: u32,
    /// このフレームで更新するプローブ番号の先頭（ローテーション）。
    pub probe_update_base: u32,
    /// フレーム番号（レイ方向の回転ノイズに使う）。
    pub frame_index: u32,
    pub intensity: f32,
    pub hysteresis: f32,
    pub recursive_weight: f32,
    pub _pad0: u32,
}

impl GiParams {
    /// 無効状態（enabled=0）の GiParams。格子だけは有効値を入れておく
    /// （描画側が万一参照しても NaN/0除算を起こさないため）。
    pub fn disabled(grid: &GiGrid) -> Self {
        Self::new(grid, false, 1, 0, 0, 1.0, 0.97, 0.5)
    }

    /// 格子とノブから GiParams を構築する。
    #[allow(clippy::too_many_arguments)]
    pub fn new(
        grid:              &GiGrid,
        enabled:           bool,
        rays_per_probe:    u32,
        probe_update_base: u32,
        frame_index:       u32,
        intensity:         f32,
        hysteresis:        f32,
        recursive_weight:  f32,
    ) -> Self {
        Self {
            origin_x: grid.origin[0],
            origin_y: grid.origin[1],
            origin_z: grid.origin[2],
            spacing_x: grid.spacing[0],
            spacing_y: grid.spacing[1],
            spacing_z: grid.spacing[2],
            dim_x: grid.dims[0],
            dim_y: grid.dims[1],
            dim_z: grid.dims[2],
            probe_count: grid.probe_count(),
            atlas_cols: grid.atlas_cols(),
            atlas_rows: grid.atlas_rows(),
            enabled: if enabled { 1 } else { 0 },
            rays_per_probe: rays_per_probe.clamp(1, super::GI_MAX_RAYS),
            probe_update_base,
            frame_index,
            intensity,
            hysteresis,
            recursive_weight,
            _pad0: 0,
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::mem::{offset_of, size_of};

    /// GiParams の Rust レイアウト（サイズ・主要オフセット）を固定する。
    #[test]
    fn gi_params_layout() {
        assert_eq!(size_of::<GiParams>(), 80, "GiParams は 80 バイト（16 の倍数）");
        assert_eq!(offset_of!(GiParams, origin_x), 0);
        assert_eq!(offset_of!(GiParams, spacing_x), 12);
        assert_eq!(offset_of!(GiParams, dim_x), 24);
        assert_eq!(offset_of!(GiParams, probe_count), 36);
        assert_eq!(offset_of!(GiParams, enabled), 48);
        assert_eq!(offset_of!(GiParams, intensity), 64);
        assert_eq!(offset_of!(GiParams, recursive_weight), 72);
    }

    /// 【回帰ガード】WGSL 側 GiParams（ddgi_common.wgsl）の naga 計算サイズが Rust と一致すること。
    /// vec3 パディング等の align16 押し出しをこの照合で止める。
    #[test]
    fn wgsl_gi_params_size_matches_rust() {
        let src = include_str!("../shaders/ddgi_common.wgsl");
        let module = naga::front::wgsl::parse_str(src)
            .expect("ddgi_common.wgsl の parse に失敗");
        let (handle, _) = module.types.iter()
            .find(|(_, t)| t.name.as_deref() == Some("GiParams"))
            .expect("WGSL に struct GiParams が見つかりません");
        let mut layouter = naga::proc::Layouter::default();
        layouter.update(module.to_ctx()).expect("naga Layouter の計算に失敗");
        let wgsl_size = layouter[handle].size as usize;
        assert_eq!(
            wgsl_size, size_of::<GiParams>(),
            "WGSL の GiParams サイズ（naga 計算）が Rust と一致しません。\
             vec3 パディング等の align16 押し出しを疑うこと（スカラーで詰める）"
        );
    }
}
