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
use super::spline::{RiverPath, RIVER_MIN_CONTROL_POINTS};

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
    /// 解析波の進行方位角（度。0 = +Z、正で +X 側へ回る。Phase W6.3）
    pub wave_direction_deg: f32,
    /// 波形ランダマイズの強さ（0 で完全無効。Phase W6.4）
    pub wave_noise_strength: f32,
    /// 波形ランダマイズのノイズ空間周波数倍率（Phase W6.4）
    pub wave_noise_scale: f32,
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
    /// 水中コースティクスの強さ（0 で完全無効。Phase W5.3）
    pub caustics_intensity: f32,
    /// コースティクスの細かさ倍率（Phase W5.3）
    pub caustics_scale: f32,
    /// コースティクスが消える水深（m。Phase W5.3）
    pub caustics_depth_fade: f32,
    /// 水中の影を水面の屈折でずらす誇張倍率（1.0=物理どおり／0 で無効。Phase W5.3）
    pub shadow_refraction_strength: f32,
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
            wave_direction_deg:    c.wave_direction_deg,
            wave_noise_strength:   c.wave_noise_strength,
            wave_noise_scale:      c.wave_noise_scale,
            fresnel_power:         c.fresnel_power,
            fresnel_strength:      c.fresnel_strength,
            reflection_color:      c.reflection_color,
            refraction_distortion: c.refraction_distortion,
            ripple_strength:       c.ripple_strength,
            ripple_foam_threshold: c.ripple_foam_threshold,
            caustics_intensity:    c.caustics_intensity,
            caustics_scale:        c.caustics_scale,
            caustics_depth_fade:   c.caustics_depth_fade,
            shadow_refraction_strength: c.shadow_refraction_strength,
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
///
/// **`Copy` ではない**（W4 の川が可変長の折れ線 `RiverPath` を持つため）。
/// 毎フレーム 1 度だけ作られて配列で持ち回るだけなので、複製コストは問題にならない。
#[derive(Clone, Debug, PartialEq)]
pub struct ResolvedWaterVolume {
    /// 水ボリュームの種別
    pub kind: WaterVolumeKind,
    /// ワールド空間の水面 Y。
    ///
    /// **Spline（川）では公称値**（アクタ原点 + surface_height）にすぎない。
    /// 川の実際の水面 Y は地点ごとに変わるため、`river` の折れ線から求めること
    /// （問い合わせ `WaterQuery` はそうしている）。
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
    /// 川の折れ線リボン（**kind = Spline のときだけ Some**。Phase W4）。
    ///
    /// 制御点が 2 点未満の Spline は `None` になり、描画も問い合わせも行われない
    /// （＝「川として成立していない水」は存在しないのと同じ扱い）。
    pub river: Option<RiverPath>,
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
        Self::from_component_with_path(c, actor_pos, actor_dfs_id, None)
    }

    /// `from_component` に「外から差し込むワールド空間の折れ線」を足した版。
    ///
    /// `control_polyline` は **`control_point_ref` で指定されたアクタの
    /// `ControlPointComponent` を評価した結果**（`PathEval::sample_polyline`）を想定する。
    /// `Some` かつ 2 点以上のときは `spline_points` を**完全に無視**して、こちらで川を組む。
    /// 参照が明示されているなら、それがユーザーの指定した唯一の真実であり、
    /// 2 つの点列を混ぜると「見えている線と流れる線が違う」状態を再び作ってしまうため。
    ///
    /// **どちらの点列を使うかの判断は呼び出し側（`collect`）が行う**。
    /// 本関数は「差し込まれた折れ線があるならそれを使う」だけで、
    /// 参照名の解決やアクタ探索は一切知らない（描画層に検索を持ち込まないため）。
    ///
    /// ## 座標系の注意（二重加算の禁止）
    /// `control_polyline` は PathEval がアクタ Transform（位置・回転・スケール）を
    /// 適用済みの**完全なワールド座標**である。したがって `spline_points` 経路のように
    /// `actor_pos` を足してはならない（足すとアクタ位置が二重に効いて川が飛ぶ）。
    /// 一方 `surface_height`（川全体の水位オフセット）は
    /// インスペクタの「水面高さ」が川に効き続けるべきなので、**Y にだけ上乗せする**。
    ///
    /// `control_polyline` が `None` または 2 点未満のときは、従来どおり
    /// `spline_points` から川を組む（フォールバック）。
    pub fn from_component_with_path(
        c:                &WaterVolumeComponent,
        actor_pos:        [f32; 3],
        actor_dfs_id:     u32,
        control_polyline: Option<&[[f32; 3]]>,
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
                river:        None,
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
                // Spline（川。W4）のときだけ、制御点をワールド空間へ移して
                // 折れ線リボンを構築する。制御点は **アクタ相対**なので
                // 「アクタ位置 + 制御点」で世界へ出し、Y には Region と同じく
                // surface_height（相対水位）を上乗せする
                //（＝アクタを動かせば川ごと動き、surface_height で川全体の水位を上下できる）。
                let river = if c.kind == WaterVolumeKind::Spline {
                    // 折れ線の出どころは 2 通り。ControlPointComponent があればそちらが優先。
                    let world: Vec<[f32; 3]> = match control_polyline {
                        // ── ① ControlPoint 経路（優先）──
                        // すでにワールド座標なのでアクタ位置は足さない（二重加算の禁止）。
                        // surface_height だけは川全体の水位オフセットとして Y に上乗せする。
                        Some(poly) if poly.len() >= RIVER_MIN_CONTROL_POINTS => poly.iter()
                            .map(|p| [p[0], p[1] + c.surface_height, p[2]])
                            .collect(),
                        // ── ② spline_points 経路（フォールバック）──
                        // こちらは**アクタ相対**なので、アクタ位置を足してワールドへ出す。
                        _ => c.spline_points.iter()
                            .map(|p| [
                                actor_pos[0] + p[0],
                                actor_pos[1] + p[1] + c.surface_height,
                                actor_pos[2] + p[2],
                            ])
                            .collect(),
                    };
                    // 水中とみなす厚みは川専用の river_depth（負値は 0 に丸める）。
                    // 分割密度はボリュームごとの river_segment_length（W4.1）。
                    RiverPath::build(
                        &world, c.river_width, c.flow_speed, c.river_depth.abs(),
                        c.river_segment_length)
                } else {
                    None
                };
                Self {
                    kind:         c.kind,
                    // 水面 Y は「設定水位（アクタ Y + surface_height）」が基本だが、
                    // 水位グラフ（W2.5）が動いている間だけ **その現在水位で差し替える**。
                    // ここ 1 か所を差し替えるだけで、描画・WaterQuery・岸波ベイクの
                    // すべてが自動的に現在水位を見る（下流に個別の分岐が要らない）。
                    // Spline（川）は水位グラフのノードになれないので常に None ＝
                    // 従来どおり公称値になる（`river` の折れ線が実際の水面を持つ）。
                    surface_y:    c.sim_level_y.unwrap_or(actor_pos[1] + c.surface_height),
                    center:       actor_pos,
                    half_extents: half,
                    ocean_extent: c.ocean_extent,
                    visual:       WaterVisualParams::from_component(c),
                    actor_dfs_id,
                    river,
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

    /// 水位グラフ（W2.5）が動いている間は、その現在水位が `surface_y` を上書きすること。
    /// これが崩れると「水は流れているのに水面が動かない」状態になる。
    #[test]
    fn simulated_level_overrides_authored_surface_height() {
        let mut c = WaterVolumeComponent::default();
        c.kind           = WaterVolumeKind::Region;
        c.surface_height = 2.0;              // 設定水位（アクタ Y=10 なら 12）
        c.sim_level_y    = Some(7.25);       // 水位グラフの現在水位（ワールド絶対 Y）
        let r = ResolvedWaterVolume::from_component(&c, [0.0, 10.0, 0.0], 0);
        assert_eq!(r.surface_y, 7.25, "現在水位が使われること");
        // 揮発水位が消えれば設定水位へ戻る（Play 停止時の挙動）。
        c.sim_level_y = None;
        let r = ResolvedWaterVolume::from_component(&c, [0.0, 10.0, 0.0], 0);
        assert_eq!(r.surface_y, 12.0, "None なら設定水位（アクタ Y + surface_height）");
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

    /// Spline は制御点をワールド空間（アクタ位置 + 相対 + surface_height）へ移して
    /// 折れ線リボンを構築する。
    #[test]
    fn spline_builds_river_in_world_space() {
        let mut c = WaterVolumeComponent::default();
        c.kind = WaterVolumeKind::Spline;
        c.surface_height = 1.0;
        c.spline_points = vec![[0.0, 0.0, 0.0], [10.0, -2.0, 0.0]];
        let r = ResolvedWaterVolume::from_component(&c, [100.0, 50.0, -20.0], 0);
        let river = r.river.as_ref().expect("制御点 2 点で川が成立する");
        // 始点 = アクタ位置 + 制御点 + surface_height（Y のみ）
        assert_eq!(river.nodes[0].pos, [100.0, 51.0, -20.0]);
        let last = river.nodes.last().unwrap().pos;
        assert!((last[0] - 110.0).abs() < 1e-4);
        assert!((last[1] - 49.0).abs() < 1e-4, "下流は 2m 下る");
    }

    /// 制御点が 2 点未満の Spline は川を持たない（描画・問い合わせとも無効）。
    #[test]
    fn spline_without_enough_points_has_no_river() {
        let mut c = WaterVolumeComponent::default();
        c.kind = WaterVolumeKind::Spline;
        c.spline_points = vec![[0.0, 0.0, 0.0]];
        let r = ResolvedWaterVolume::from_component(&c, [0.0, 0.0, 0.0], 0);
        assert!(r.river.is_none());
    }

    /// Ocean / Region は川を持たない。
    #[test]
    fn non_spline_kinds_have_no_river() {
        for kind in [WaterVolumeKind::Ocean, WaterVolumeKind::Region] {
            let mut c = WaterVolumeComponent::default();
            c.kind = kind;
            // 制御点があっても Spline 以外では無視される
            c.spline_points = vec![[0.0, 0.0, 0.0], [10.0, 0.0, 0.0]];
            let r = ResolvedWaterVolume::from_component(&c, [0.0, 0.0, 0.0], 0);
            assert!(r.river.is_none(), "{kind:?} は川を持たない");
        }
    }

    /// ControlPoint 由来の折れ線があれば spline_points を完全に無視すること（優先度）。
    #[test]
    fn control_polyline_overrides_spline_points() {
        let mut c = WaterVolumeComponent::default();
        c.kind = WaterVolumeKind::Spline;
        // spline_points 側は Z = 999（採用されたらすぐ分かる値）
        c.spline_points = vec![[0.0, 0.0, 999.0], [10.0, 0.0, 999.0]];
        let poly = [[1.0, 2.0, 3.0], [11.0, 2.0, 3.0]];
        let r = ResolvedWaterVolume::from_component_with_path(&c, [0.0, 0.0, 0.0], 0, Some(&poly));
        let river = r.river.as_ref().expect("制御点 2 点で川が成立する");
        assert_eq!(river.nodes[0].pos, [1.0, 2.0, 3.0], "ControlPoint の折れ線が使われること");
    }

    /// ControlPoint 経路でも surface_height が Y に上乗せされること
    ///（インスペクタの「水面高さ」が川に効き続ける契約）。
    #[test]
    fn control_polyline_still_applies_surface_height() {
        let mut c = WaterVolumeComponent::default();
        c.kind = WaterVolumeKind::Spline;
        c.surface_height = 5.0;
        let poly = [[0.0, 1.0, 0.0], [10.0, 1.0, 0.0]];
        let r = ResolvedWaterVolume::from_component_with_path(&c, [0.0, 0.0, 0.0], 0, Some(&poly));
        let river = r.river.as_ref().unwrap();
        assert_eq!(river.nodes[0].pos[1], 6.0, "折れ線の Y + surface_height");
    }

    /// ControlPoint 経路ではアクタ位置を足さないこと（PathEval が適用済みのため二重加算禁止）。
    #[test]
    fn control_polyline_does_not_add_actor_position_twice() {
        let mut c = WaterVolumeComponent::default();
        c.kind = WaterVolumeKind::Spline;
        // 折れ線はすでにワールド座標（アクタ位置 100 を含んでいる）
        let poly = [[100.0, 0.0, 0.0], [110.0, 0.0, 0.0]];
        let r = ResolvedWaterVolume::from_component_with_path(&c, [100.0, 0.0, 0.0], 0, Some(&poly));
        let river = r.river.as_ref().unwrap();
        assert_eq!(river.nodes[0].pos[0], 100.0, "200 になっていたらアクタ位置の二重加算");
    }

    /// 折れ線が 2 点未満なら spline_points へフォールバックすること。
    #[test]
    fn short_control_polyline_falls_back_to_spline_points() {
        let mut c = WaterVolumeComponent::default();
        c.kind = WaterVolumeKind::Spline;
        c.spline_points = vec![[0.0, 0.0, 7.0], [10.0, 0.0, 7.0]];
        let poly = [[1.0, 2.0, 3.0]]; // 1 点だけ ＝ 川として成立しない
        let r = ResolvedWaterVolume::from_component_with_path(&c, [0.0, 0.0, 0.0], 0, Some(&poly));
        let river = r.river.as_ref().expect("spline_points 側で川が成立する");
        assert_eq!(river.nodes[0].pos, [0.0, 0.0, 7.0], "spline_points が使われること");
    }

    /// `from_component` は折れ線なし版へ委譲する薄いラッパであること（既存挙動の不変）。
    #[test]
    fn from_component_matches_with_path_none() {
        let mut c = WaterVolumeComponent::default();
        c.kind = WaterVolumeKind::Spline;
        c.spline_points = vec![[0.0, 0.0, 0.0], [10.0, 0.0, 0.0]];
        let a = ResolvedWaterVolume::from_component(&c, [1.0, 2.0, 3.0], 4);
        let b = ResolvedWaterVolume::from_component_with_path(&c, [1.0, 2.0, 3.0], 4, None);
        assert_eq!(a, b);
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
