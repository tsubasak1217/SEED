// ============================================================
//  water/resolved.rs — ワールド空間へ解決済みの水ボリューム
//
//  WaterVolumeComponent（ローカル定義）＋ Actor の Transform を
//  1 個のワールド空間表現へ畳み込んだ中間データ。
//  描画・問い合わせの双方がこの型だけを見る（境界の単一化）。
//
//  この型は毎フレーム作り直される揮発データであり、シリアライズしない
//  （永続化するのは WaterVolumeComponent 側だけ）。
// ============================================================

use crate::engine::components::water_volume_component::{
    WaterVolumeComponent, WaterVolumeKind,
};

// ─── WaterVisualParams ───────────────────────────────────────

/// 水の見た目パラメータ一式（色・フォーム・波・フレネル・屈折）。
///
/// WaterVolumeComponent の見た目系フィールドをそのままコピーしただけの
/// 素の struct。形状情報（種別・範囲）と見た目を型で分離しておくことで、
/// レンダラは「見た目だけ」を受け取ってユニフォームへ詰められる。
/// 永続化しないため serde は不要。
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct WaterVisualParams {
    /// 浅場の色（リニア RGB）
    pub shallow_color: [f32; 3],
    /// 深場の色（リニア RGB）
    pub deep_color: [f32; 3],
    /// 深色へ収束するまでの水中距離（m）
    pub absorption_distance: f32,
    /// 深場での最大不透明度（0..1）
    pub surface_opacity: f32,
    /// 岸フォームの色（リニア RGB）
    pub foam_color: [f32; 3],
    /// フォームが出る水深（m）
    pub foam_width: f32,
    /// フォームの強度（0..1）
    pub foam_intensity: f32,
    /// 法線摂動の強さ
    pub wave_amplitude: f32,
    /// 波の空間周波数（1/m）
    pub wave_scale: f32,
    /// 波のスクロール速度
    pub wave_speed: f32,
    /// フレネル指数
    pub fresnel_power: f32,
    /// フレネル反射の寄与率（0..1）
    pub fresnel_strength: f32,
    /// 浅い角度での簡易反射色（リニア RGB）
    pub reflection_color: [f32; 3],
    /// 屈折 UV の最大歪み（画面比）
    pub refraction_distortion: f32,
    /// 波紋・航跡（インタラクションフィールド）の法線摂動スケール（Phase I2）
    pub ripple_strength: f32,
    /// 波紋フォームが出る波高しきい値（m 相当。Phase I2）
    pub ripple_foam_threshold: f32,
    /// 岸波の強さ（0 で完全無効。Phase W1.5）
    pub shore_wave_strength: f32,
    /// 岸へ寄せるうねりの波長（m。Phase W1.5）
    pub shore_wave_length: f32,
    /// 岸へ寄せるうねりの周期（秒。Phase W1.5）
    pub shore_wave_period: f32,
    /// 砕け波・打ち上げの泡量（0..1。Phase W1.5）
    pub shore_wave_foam: f32,
}

impl WaterVisualParams {
    /// WaterVolumeComponent の見た目系フィールドを抜き出して構築する。
    pub fn from_component(c: &WaterVolumeComponent) -> Self {
        Self {
            shallow_color:         c.shallow_color,
            deep_color:            c.deep_color,
            absorption_distance:   c.absorption_distance,
            surface_opacity:       c.surface_opacity,
            foam_color:            c.foam_color,
            foam_width:            c.foam_width,
            foam_intensity:        c.foam_intensity,
            wave_amplitude:        c.wave_amplitude,
            wave_scale:            c.wave_scale,
            wave_speed:            c.wave_speed,
            fresnel_power:         c.fresnel_power,
            fresnel_strength:      c.fresnel_strength,
            reflection_color:      c.reflection_color,
            refraction_distortion: c.refraction_distortion,
            ripple_strength:       c.ripple_strength,
            ripple_foam_threshold: c.ripple_foam_threshold,
            shore_wave_strength:   c.shore_wave_strength,
            shore_wave_length:     c.shore_wave_length,
            shore_wave_period:     c.shore_wave_period,
            shore_wave_foam:       c.shore_wave_foam,
        }
    }
}

// ─── ResolvedWaterVolume ─────────────────────────────────────

/// ワールド空間に解決済みの水ボリューム 1 個。
/// アクタの Transform と WaterVolumeComponent から作られ、描画・問い合わせの双方が
/// この単一の中間表現だけを見る（描画都合の実装にしないための境界）。
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct ResolvedWaterVolume {
    /// 水ボリュームの種別（Spline は W4 実装のため、収集時点で除外される）
    pub kind: WaterVolumeKind,
    /// ワールド空間の水面 Y
    pub surface_y: f32,
    /// Region: AABB 中心のワールド座標 / Ocean: 未使用(0)
    pub center: [f32; 3],
    /// Region: AABB 半径 / Ocean: 未使用
    pub half_extents: [f32; 3],
    /// Ocean 水面クアッドの片側半径
    pub ocean_extent: f32,
    /// 見た目パラメータ（そのままコピー）
    pub visual: WaterVisualParams,
    /// この水ボリュームを持つアクタの DFS インデックス（0 始まり）。
    ///
    /// エディタのピッキング（ID パス）で「クリックされた水面 → アクタ」を引くために持つ。
    /// 採番規則は `collect_mcs_in_world_line` / キャンバスピックと**完全に同一**でなければ
    /// ならない（世界線のルート群を DFS し、非アクティブなアクタも数える）。
    pub actor_dfs_id: u32,
}

impl ResolvedWaterVolume {
    /// アクタのワールド位置と WaterVolumeComponent からワールド空間表現を作る。
    ///
    /// `actor_pos` はアクタの Transform.position（Transform はワールド空間）。
    /// `actor_dfs_id` はそのアクタの DFS インデックス（0 始まり。ピッキング用）。
    ///
    /// 【W1 の制限】アクタの回転は無視する（＝Region は常に軸平行 AABB）。
    /// 回転した水塊は W4 以降で対応する。
    pub fn from_component(
        c:            &WaterVolumeComponent,
        actor_pos:    [f32; 3],
        actor_dfs_id: u32,
    ) -> Self {
        match c.kind {
            // Ocean: XZ 無限。水面 Y は surface_height をワールド絶対値として使う
            //（アクタ位置に依存しない ＝ アクタをどこへ置いても水面は動かない）。
            WaterVolumeKind::Ocean => Self {
                kind:         c.kind,
                surface_y:    c.surface_height,
                center:       [0.0, 0.0, 0.0],
                half_extents: [0.0, 0.0, 0.0],
                ocean_extent: c.ocean_extent,
                visual:       WaterVisualParams::from_component(c),
                actor_dfs_id,
            },
            // Region / Spline: アクタ位置を AABB 中心とし、水面 Y は
            // 「中心 Y + surface_height（相対）」で決まる。
            // 半径は負値を許さない（絶対値を取る）。
            _ => {
                let half = [
                    c.region_half_extents[0].abs(),
                    c.region_half_extents[1].abs(),
                    c.region_half_extents[2].abs(),
                ];
                Self {
                    kind:         c.kind,
                    surface_y:    actor_pos[1] + c.surface_height,
                    center:       actor_pos,
                    half_extents: half,
                    ocean_extent: c.ocean_extent,
                    visual:       WaterVisualParams::from_component(c),
                    actor_dfs_id,
                }
            }
        }
    }
}

// ─── ユニットテスト ──────────────────────────────────────────

#[cfg(test)]
mod tests {
    use super::*;

    /// Ocean は surface_height をワールド Y 絶対値として扱い、アクタ位置に依存しない。
    #[test]
    fn ocean_surface_is_absolute_world_y() {
        let mut c = WaterVolumeComponent::default();
        c.kind = WaterVolumeKind::Ocean;
        c.surface_height = 3.0;
        let r = ResolvedWaterVolume::from_component(&c, [100.0, 50.0, -20.0], 0);
        assert_eq!(r.surface_y, 3.0, "Ocean の水面 Y はアクタ Y に影響されない");
    }

    /// Region は surface_height をアクタ原点からの相対 Y として扱う。
    #[test]
    fn region_surface_is_relative_to_actor() {
        let mut c = WaterVolumeComponent::default();
        c.kind = WaterVolumeKind::Region;
        c.surface_height = 2.0;
        let r = ResolvedWaterVolume::from_component(&c, [1.0, 10.0, 2.0], 0);
        assert_eq!(r.surface_y, 12.0, "Region の水面 Y = アクタ Y + surface_height");
        assert_eq!(r.center, [1.0, 10.0, 2.0], "AABB 中心はアクタ位置");
    }

    /// Region の AABB 半径は負値を与えても絶対値になる（反転 AABB を作らない）。
    #[test]
    fn region_half_extents_are_absolute() {
        let mut c = WaterVolumeComponent::default();
        c.kind = WaterVolumeKind::Region;
        c.region_half_extents = [-4.0, 2.0, -6.0];
        let r = ResolvedWaterVolume::from_component(&c, [0.0, 0.0, 0.0], 0);
        assert_eq!(r.half_extents, [4.0, 2.0, 6.0]);
    }

    /// 見た目パラメータはそのままコピーされる。
    #[test]
    fn visual_params_are_copied_verbatim() {
        let mut c = WaterVolumeComponent::default();
        c.shallow_color = [0.1, 0.2, 0.3];
        c.foam_intensity = 0.42;
        let r = ResolvedWaterVolume::from_component(&c, [0.0, 0.0, 0.0], 0);
        assert_eq!(r.visual.shallow_color, [0.1, 0.2, 0.3]);
        assert_eq!(r.visual.foam_intensity, 0.42);
    }
}
