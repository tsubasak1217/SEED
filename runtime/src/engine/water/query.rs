// ============================================================
//  water/query.rs — 水の問い合わせ API（正式窓口）
//
//  遊泳・浮力・水中ポストエフェクトなど「水に触れる側」は、
//  必ずこの WaterQuery 経由で問い合わせる。
//  WaterVolumeComponent やレンダラの内部表現を直接読まないこと
//  （読むと描画都合の変更がゲームロジックを壊す）。
//
//  【判定規則】
//    Ocean  … XZ 無限。y <= surface_y なら水中。水面高さは常に取得できる。
//    Region … center ± half_extents の軸平行 AABB。
//             水中判定は「XZ が AABB 内」かつ「y <= surface_y」かつ
//             「y >= AABB 下端」。水面高さは XZ が AABB 内なら Y を問わず返す
//             （水面の上に居ても「その真下に水がある」ことは分かるべきなため）。
//    Spline … 川（W4）。折れ線リボン（`RiverPath`）までの **XZ 最短距離が半幅以内**で、
//             かつ Y が「その地点の補間水面 Y」から下へ深さぶんの帯に入っていれば水中。
//             水面高さは幅内なら Y を問わず「補間水面 Y」を返す（Region と同じ規約）。
//
//  複数の水が重なる場合、水面高さは最も高いものを採用する
//  （プールの上に大洋がある等でも「最初にぶつかる水面」が返る）。
//
//  【流速（W4）】
//  流速を持つのは川だけである。川の帯の中では「その地点の接線方向 × flow_speed」を返す。
//  複数の川が重なる場合は、**中心線に最も近い川**の流速を採る（最も強く影響する川が勝つ）。
// ============================================================

use super::resolved::ResolvedWaterVolume;
use crate::engine::components::water_volume_component::WaterVolumeKind;

// ─── 定数 ────────────────────────────────────────────────────

/// 水（川）が無い場所の流速。
const ZERO_FLOW: [f32; 3] = [0.0, 0.0, 0.0];

/// 流速が効く水面より上のマージン（m）。
///
/// 水面ちょうどに浮いている物体（浮力で水面に張り付いた船・プレイヤー）は、
/// 数値誤差で水面のわずか上に居ることがある。そこで流されなくなると
/// 「水面に浮くと急に止まる」という致命的な違和感になるため、
/// 水面から上へこの分だけは流速を効かせる。
const RIVER_FLOW_ABOVE_SURFACE_M: f32 = 0.25;

// ─── WaterQuery ──────────────────────────────────────────────

/// 解決済み水ボリューム集合に対する問い合わせ。描画とは独立。
///
/// 借用ビューであり状態を持たない。毎フレーム
/// `collect_water_volumes` の結果を包んで使い捨てる想定。
pub struct WaterQuery<'a> {
    /// 問い合わせ対象のワールド空間水ボリューム群
    volumes: &'a [ResolvedWaterVolume],
}

impl<'a> WaterQuery<'a> {
    /// 解決済み水ボリューム列を包んで問い合わせビューを作る。
    pub fn new(volumes: &'a [ResolvedWaterVolume]) -> Self {
        Self { volumes }
    }

    /// この点は水中か。
    ///
    /// 1 つでも水中判定になるボリュームがあれば true。
    pub fn is_underwater(&self, point: [f32; 3]) -> bool {
        self.volumes.iter().any(|v| volume_contains(v, point))
    }

    /// この点(の XZ)における水面高さ。水が無ければ None。
    /// 複数重なる場合は最も高い水面を返す。
    pub fn surface_height_at(&self, point: [f32; 3]) -> Option<f32> {
        self.topmost_volume_at_xz(point).map(|(_, y)| y)
    }

    /// この点(の XZ)で**最も高い水面を提供している水域**とその水面高さ。
    ///
    /// `surface_height_at` はこの関数の高さ成分そのものである
    /// （＝「高さを返した水域」と「物性を返す水域」が必ず一致する。
    /// 別々に走査すると、重なった水域で「水面は大洋・粘度は池」という
    /// ちぐはぐな組み合わせが起こりうる）。
    ///
    /// 水域の物性（粘度・波紋の減衰率）を引くための正式窓口でもある。
    pub fn topmost_volume_at_xz(
        &self,
        point: [f32; 3],
    ) -> Option<(&'a ResolvedWaterVolume, f32)> {
        let mut best: Option<(&'a ResolvedWaterVolume, f32)> = None;
        for v in self.volumes {
            // XZ が範囲内のボリュームだけが水面高さを提供する
            let Some(y) = volume_surface_at_xz(v, point) else { continue };
            // 最も高い水面を採用する（f32 の比較は NaN を除外したいので max ではなく明示比較）
            best = match best {
                Some((pv, py)) if py >= y => Some((pv, py)),
                _ => Some((v, y)),
            };
        }
        best
    }

    /// この点の流速（m/s。ワールド空間）。
    ///
    /// 流速を持つのは川（`WaterVolumeKind::Spline`）だけで、Ocean / Region は常にゼロ。
    /// 川の帯（幅内・水面から下へ深さぶん＋水面上のわずかなマージン）に入っていれば、
    /// その地点の**下流方向（3D 単位ベクトル）× flow_speed** を返す。
    ///
    /// 川が複数重なる場合は、**中心線に最も近い川**の流速を採る
    /// （合成すると打ち消し合って「合流点で止まる」不自然が出るため、最寄りが勝つ）。
    pub fn flow_at(&self, point: [f32; 3]) -> [f32; 3] {
        let mut best_distance = f32::INFINITY;
        let mut best_flow     = ZERO_FLOW;
        for v in self.volumes {
            let Some(river) = v.river.as_ref() else { continue };
            let s = river.nearest(point);
            // 幅の外は流されない。
            if s.distance_xz > river.half_width { continue; }
            // 深さ方向の帯: 水面 + マージン 〜 水面 − 深さ。
            if point[1] > s.surface_y + RIVER_FLOW_ABOVE_SURFACE_M { continue; }
            if point[1] < s.surface_y - river.depth { continue; }
            if s.distance_xz < best_distance {
                best_distance = s.distance_xz;
                best_flow = [
                    s.direction[0] * river.flow_speed,
                    s.direction[1] * river.flow_speed,
                    s.direction[2] * river.flow_speed,
                ];
            }
        }
        best_flow
    }
}

// ─── 判定ヘルパー ────────────────────────────────────────────

/// 点の XZ がボリュームの水平範囲に入っているか。
///
/// Ocean は XZ 無限なので常に true。川は折れ線までの距離で判定する。
fn volume_contains_xz(v: &ResolvedWaterVolume, point: [f32; 3]) -> bool {
    match v.kind {
        WaterVolumeKind::Ocean => true,
        WaterVolumeKind::Region => {
            let dx = point[0] - v.center[0];
            let dz = point[2] - v.center[2];
            dx.abs() <= v.half_extents[0] && dz.abs() <= v.half_extents[2]
        }
        // 川（W4）: 折れ線までの XZ 最短距離が半幅以内なら内側。
        // 折れ線が無い（制御点不足の）川は水域として存在しない。
        WaterVolumeKind::Spline => match v.river.as_ref() {
            Some(river) => river.nearest(point).distance_xz <= river.half_width,
            None => false,
        },
    }
}

/// 点がこのボリュームの「水の中」に入っているか（境界値は水中として含む）。
fn volume_contains(v: &ResolvedWaterVolume, point: [f32; 3]) -> bool {
    match v.kind {
        // Ocean: XZ 無限のため水面より下なら常に水中
        WaterVolumeKind::Ocean => point[1] <= v.surface_y,
        WaterVolumeKind::Region => {
            if !volume_contains_xz(v, point) { return false; }
            // 水面より下、かつ AABB の下端より上であること
            let bottom_y = v.center[1] - v.half_extents[1];
            point[1] <= v.surface_y && point[1] >= bottom_y
        }
        // 川（W4）: 幅内、かつ「その地点の補間水面 Y」から下へ深さぶんの帯に入っていること。
        // 水面 Y が地点ごとに違う（川は下る）ため、Region と違って公称 surface_y は使えない。
        WaterVolumeKind::Spline => {
            let Some(river) = v.river.as_ref() else { return false };
            let s = river.nearest(point);
            s.distance_xz <= river.half_width
                && point[1] <= s.surface_y
                && point[1] >= s.surface_y - river.depth
        }
    }
}

/// 点の XZ における、このボリュームの水面高さ。範囲外なら None。
/// Y 座標は問わない（水面の上に居ても真下の水面は返る）。
fn volume_surface_at_xz(v: &ResolvedWaterVolume, point: [f32; 3]) -> Option<f32> {
    // 川は地点ごとに水面 Y が違うので、折れ線から補間した値を返す。
    if v.kind == WaterVolumeKind::Spline {
        let river = v.river.as_ref()?;
        let s = river.nearest(point);
        return if s.distance_xz <= river.half_width { Some(s.surface_y) } else { None };
    }
    if volume_contains_xz(v, point) { Some(v.surface_y) } else { None }
}

// ─── ユニットテスト ──────────────────────────────────────────

#[cfg(test)]
mod tests {
    use super::*;
    use crate::engine::water::resolved::WaterVisualParams;

    /// テスト用の見た目パラメータ（値は判定に関与しないためゼロで良い）。
    fn dummy_visual() -> WaterVisualParams {
        WaterVisualParams {
            shallow_color: [0.0; 3],
            deep_color: [0.0; 3],
            absorption_distance: 0.0,
            surface_opacity: 0.0,
            foam_color: [0.0; 3],
            foam_width: 0.0,
            foam_intensity: 0.0,
            wave_amplitude: 0.0,
            wave_scale: 0.0,
            wave_speed: 0.0,
            wave_direction_deg: 0.0,
            // 波形ランダマイズ（Phase W6.4）。既定相当の値を入れておく。
            wave_noise_strength: 0.35, wave_noise_scale: 1.0,
            fresnel_power: 0.0,
            fresnel_strength: 0.0,
            // 反射（Phase W5.2）。問い合わせ用ダミーなので強度 0（＝反射なし）。
            reflection_intensity: 0.0, reflection_roughness: 0.0,
            refraction_distortion: 0.0,
            ripple_strength: 0.0,
            ripple_foam_threshold: 0.0,
            // 水域ごとの物性（Phase I2.1）。既定相当（＝現行の水）の値を入れておく。
            viscosity: 0.0, ripple_damping: 1.0 / 1.5,
            // 水中コースティクス（Phase W5.3）。既定相当の値を入れておく。
            caustics_intensity: 0.6, caustics_scale: 1.0, caustics_depth_fade: 6.0,
            // 影の屈折ゆらぎもテスト用ダミーでは 0（＝影をずらさない）。
            shadow_refraction_strength: 0.0,
            // 岸波（W1.5）はテスト用のダミーなので全て 0（＝岸波を出さない）。
            shore_wave_strength: 0.0, shore_wave_length: 0.0,
            shore_wave_period: 0.0, shore_wave_foam: 0.0,
        }
    }

    /// 水面 Y = surface_y の Ocean を作る。
    fn ocean(surface_y: f32) -> ResolvedWaterVolume {
        ResolvedWaterVolume {
            kind: WaterVolumeKind::Ocean,
            surface_y,
            center: [0.0; 3],
            half_extents: [0.0; 3],
            ocean_extent: 1.0,
            visual: dummy_visual(),
            // 問い合わせ（水中判定）はピッキング ID を使わないのでダミー
            actor_dfs_id: 0,
            // Ocean / Region は川を持たない
            river: None,
        }
    }

    /// center 中心・half 半径・水面 = center.y + surf_rel の Region を作る。
    fn region(center: [f32; 3], half: [f32; 3], surf_rel: f32) -> ResolvedWaterVolume {
        ResolvedWaterVolume {
            kind: WaterVolumeKind::Region,
            surface_y: center[1] + surf_rel,
            center,
            half_extents: half,
            ocean_extent: 0.0,
            visual: dummy_visual(),
            // 問い合わせ（水中判定）はピッキング ID を使わないのでダミー
            actor_dfs_id: 0,
            // Ocean / Region は川を持たない
            river: None,
        }
    }

    /// 折れ線を持たない Spline（制御点不足の川）を作る。無視されることの確認用。
    fn spline_without_path() -> ResolvedWaterVolume {
        ResolvedWaterVolume {
            kind: WaterVolumeKind::Spline,
            surface_y: 100.0,
            center: [0.0; 3],
            half_extents: [1000.0; 3],
            ocean_extent: 0.0,
            visual: dummy_visual(),
            // 問い合わせ（水中判定）はピッキング ID を使わないのでダミー
            actor_dfs_id: 0,
            river: None,
        }
    }

    /// 川（W4）を作る。制御点はワールド座標で与える。
    fn river(
        points:     &[[f32; 3]],
        width:      f32,
        flow_speed: f32,
        depth:      f32,
    ) -> ResolvedWaterVolume {
        ResolvedWaterVolume {
            kind: WaterVolumeKind::Spline,
            // 公称水面 Y（川では使われない。実際の水面は折れ線から補間される）
            surface_y: points[0][1],
            center: [0.0; 3],
            half_extents: [0.0, depth, 0.0],
            ocean_extent: 0.0,
            visual: dummy_visual(),
            actor_dfs_id: 0,
            // 分割長は既定（2m 刻み）。問い合わせの検証に分割密度は本質的でない。
            river: crate::engine::water::RiverPath::build(
                points, width, flow_speed, depth,
                crate::engine::water::spline::RIVER_SAMPLE_STEP_M),
        }
    }

    /// テスト用の川の既定値（幅 4m・流速 2m/s・深さ 3m）。
    const RIVER_W: f32 = 4.0;
    const RIVER_S: f32 = 2.0;
    const RIVER_D: f32 = 3.0;

    /// X 軸方向にまっすぐ流れる水平な川（原点 → +X 20m、水面 Y = 0）。
    fn straight_river() -> ResolvedWaterVolume {
        river(&[[0.0, 0.0, 0.0], [20.0, 0.0, 0.0]], RIVER_W, RIVER_S, RIVER_D)
    }

    // ── Ocean ────────────────────────────────────────────────

    /// Ocean は水面より下が水中、上は水中でない（XZ は無関係）。
    #[test]
    fn ocean_underwater_above_and_below() {
        let vols = [ocean(5.0)];
        let q = WaterQuery::new(&vols);
        assert!(q.is_underwater([9999.0, 4.9, -9999.0]), "水面下は水中");
        assert!(!q.is_underwater([0.0, 5.1, 0.0]), "水面上は水中でない");
    }

    /// 境界値ちょうど（point.y == surface_y）は水中扱い。
    #[test]
    fn ocean_exact_surface_is_underwater() {
        let vols = [ocean(5.0)];
        let q = WaterQuery::new(&vols);
        assert!(q.is_underwater([0.0, 5.0, 0.0]));
    }

    /// Ocean の水面高さはどの XZ でも取得できる。
    #[test]
    fn ocean_surface_height_is_always_some() {
        let vols = [ocean(5.0)];
        let q = WaterQuery::new(&vols);
        assert_eq!(q.surface_height_at([1e6, -1e6, 1e6]), Some(5.0));
    }

    // ── Region ───────────────────────────────────────────────

    /// Region 内部の点は水中、XZ 範囲外は水中でない。
    #[test]
    fn region_inside_and_outside_xz() {
        // 中心 [0,0,0]・半径 [10,5,10]・水面 = +2 → 水面 Y=2, 下端 Y=-5
        let vols = [region([0.0, 0.0, 0.0], [10.0, 5.0, 10.0], 2.0)];
        let q = WaterQuery::new(&vols);
        assert!(q.is_underwater([0.0, 0.0, 0.0]), "中心は水中");
        assert!(!q.is_underwater([10.1, 0.0, 0.0]), "XZ 範囲外は水中でない");
        assert!(!q.is_underwater([0.0, 0.0, -10.1]), "XZ 範囲外(-Z)は水中でない");
    }

    /// XZ 境界ちょうど（|dx| == half_x）は範囲内として扱う。
    #[test]
    fn region_xz_boundary_is_inside() {
        let vols = [region([0.0, 0.0, 0.0], [10.0, 5.0, 10.0], 2.0)];
        let q = WaterQuery::new(&vols);
        assert!(q.is_underwater([10.0, 0.0, 10.0]), "XZ 境界ちょうどは内側");
        assert_eq!(q.surface_height_at([10.0, 0.0, 10.0]), Some(2.0));
    }

    /// 下端 Y 境界ちょうどは水中、それより下は水中でない（AABB を抜けたため）。
    #[test]
    fn region_bottom_y_boundary() {
        let vols = [region([0.0, 0.0, 0.0], [10.0, 5.0, 10.0], 2.0)];
        let q = WaterQuery::new(&vols);
        assert!(q.is_underwater([0.0, -5.0, 0.0]), "下端ちょうどは水中");
        assert!(!q.is_underwater([0.0, -5.1, 0.0]), "下端より下は水中でない");
    }

    /// 水面ちょうど（point.y == surface_y）は水中、それより上は水中でない。
    #[test]
    fn region_exact_surface_is_underwater() {
        let vols = [region([0.0, 0.0, 0.0], [10.0, 5.0, 10.0], 2.0)];
        let q = WaterQuery::new(&vols);
        assert!(q.is_underwater([0.0, 2.0, 0.0]), "水面ちょうどは水中");
        assert!(!q.is_underwater([0.0, 2.01, 0.0]), "水面より上は水中でない");
    }

    /// surface_height_at は Y を問わない（水面より高い点でも真下の水面を返す）。
    #[test]
    fn region_surface_height_ignores_y() {
        let vols = [region([0.0, 0.0, 0.0], [10.0, 5.0, 10.0], 2.0)];
        let q = WaterQuery::new(&vols);
        assert_eq!(q.surface_height_at([0.0, 1000.0, 0.0]), Some(2.0));
        assert_eq!(q.surface_height_at([0.0, -1000.0, 0.0]), Some(2.0));
        assert_eq!(q.surface_height_at([50.0, 0.0, 0.0]), None, "XZ 範囲外は None");
    }

    /// Region の水面 Y は「中心 Y + surface_height（相対）」で決まる。
    #[test]
    fn region_surface_is_relative_to_center() {
        let vols = [region([0.0, 20.0, 0.0], [10.0, 5.0, 10.0], 2.0)];
        let q = WaterQuery::new(&vols);
        assert_eq!(q.surface_height_at([0.0, 0.0, 0.0]), Some(22.0));
    }

    // ── 重なり・空・Spline ───────────────────────────────────

    /// Ocean と Region が重なる場合、水面高さは最大値を返す。
    #[test]
    fn overlapping_returns_highest_surface() {
        let vols = [
            ocean(1.0),
            region([0.0, 0.0, 0.0], [10.0, 5.0, 10.0], 4.0), // 水面 Y = 4
        ];
        let q = WaterQuery::new(&vols);
        assert_eq!(q.surface_height_at([0.0, 0.0, 0.0]), Some(4.0));

        // 順序を入れ替えても結果は同じ（最大値であって「最後の要素」ではない）
        let vols_rev = [
            region([0.0, 0.0, 0.0], [10.0, 5.0, 10.0], 4.0),
            ocean(1.0),
        ];
        let q_rev = WaterQuery::new(&vols_rev);
        assert_eq!(q_rev.surface_height_at([0.0, 0.0, 0.0]), Some(4.0));
    }

    /// Region の外へ出れば、重なっていた Ocean の水面が返る。
    #[test]
    fn overlapping_outside_region_falls_back_to_ocean() {
        let vols = [
            ocean(1.0),
            region([0.0, 0.0, 0.0], [10.0, 5.0, 10.0], 4.0),
        ];
        let q = WaterQuery::new(&vols);
        assert_eq!(q.surface_height_at([100.0, 0.0, 0.0]), Some(1.0));
    }

    /// 水が 1 つも無ければ surface_height_at は None、is_underwater は false。
    #[test]
    fn empty_volumes_report_no_water() {
        let vols: [ResolvedWaterVolume; 0] = [];
        let q = WaterQuery::new(&vols);
        assert_eq!(q.surface_height_at([0.0, 0.0, 0.0]), None);
        assert!(!q.is_underwater([0.0, -100.0, 0.0]));
    }

    /// 折れ線を持たない Spline（制御点不足）は問い合わせで常に無視される。
    #[test]
    fn spline_without_path_is_ignored() {
        let vols = [spline_without_path()];
        let q = WaterQuery::new(&vols);
        assert!(!q.is_underwater([0.0, 0.0, 0.0]));
        assert_eq!(q.surface_height_at([0.0, 0.0, 0.0]), None);
        assert_eq!(q.flow_at([0.0, 0.0, 0.0]), [0.0, 0.0, 0.0]);
    }

    // ── 川（Phase W4）────────────────────────────────────────

    /// 川の帯の中は水中、幅の外は水中でない。
    #[test]
    fn river_inside_and_outside_width() {
        let vols = [straight_river()];
        let q = WaterQuery::new(&vols);
        assert!(q.is_underwater([10.0, -0.5, 0.0]), "中心線の水面下は水中");
        assert!(!q.is_underwater([10.0, -0.5, 5.0]), "幅の外は水中でない");
    }

    /// 幅境界ちょうど（距離 == 半幅）は内側として扱う（Region の XZ 境界と同じ規約）。
    #[test]
    fn river_width_boundary_is_inside() {
        let vols = [straight_river()];
        let q = WaterQuery::new(&vols);
        let half = RIVER_W * 0.5;
        assert!(q.is_underwater([10.0, 0.0, half]), "半幅ちょうどは内側");
        assert!(!q.is_underwater([10.0, 0.0, half + 0.01]), "半幅を超えたら外側");
    }

    /// 川の水面より上は水中でなく、深さより下（＝川底より下）も水中でない。
    #[test]
    fn river_depth_band() {
        let vols = [straight_river()];
        let q = WaterQuery::new(&vols);
        assert!(q.is_underwater([10.0, 0.0, 0.0]), "水面ちょうどは水中");
        assert!(!q.is_underwater([10.0, 0.01, 0.0]), "水面より上は水中でない");
        assert!(q.is_underwater([10.0, -RIVER_D, 0.0]), "深さちょうどは水中");
        assert!(!q.is_underwater([10.0, -RIVER_D - 0.01, 0.0]), "川底より下は水中でない");
    }

    /// 川の水面高さは制御点の Y の補間（下る川の途中では中間の高さ）。
    #[test]
    fn river_surface_height_interpolates_and_ignores_y() {
        let vols = [river(&[[0.0, 10.0, 0.0], [20.0, 0.0, 0.0]], RIVER_W, RIVER_S, RIVER_D)];
        let q = WaterQuery::new(&vols);
        let mid = q.surface_height_at([10.0, 1000.0, 0.0]).expect("幅内なら Y を問わず返る");
        assert!((mid - 5.0).abs() < 0.3, "中間地点の水面はおよそ 5（実際 {mid}）");
        assert_eq!(q.surface_height_at([10.0, 0.0, 50.0]), None, "幅の外は None");
    }

    /// 川の中では下流方向へ流速が返る。幅の外・空の水域ではゼロ。
    #[test]
    fn river_flow_points_downstream() {
        let vols = [straight_river()];
        let q = WaterQuery::new(&vols);
        let f = q.flow_at([10.0, -1.0, 0.0]);
        assert!((f[0] - RIVER_S).abs() < 1e-3, "+X へ流速 {RIVER_S}（実際 {}）", f[0]);
        assert!(f[1].abs() < 1e-3 && f[2].abs() < 1e-3, "水平な直線川では XZ 以外は 0");

        assert_eq!(q.flow_at([10.0, -1.0, 5.0]), ZERO_FLOW, "幅の外は流れない");
        assert_eq!(q.flow_at([10.0, -100.0, 0.0]), ZERO_FLOW, "川底より下は流れない");
    }

    /// 水面のわずか上（浮いている物体）は流される — 数値誤差で急停止しないこと。
    #[test]
    fn river_flow_applies_slightly_above_surface() {
        let vols = [straight_river()];
        let q = WaterQuery::new(&vols);
        let just_above = q.flow_at([10.0, RIVER_FLOW_ABOVE_SURFACE_M * 0.5, 0.0]);
        assert!(just_above[0] > 0.0, "水面すぐ上は流される");
        let far_above = q.flow_at([10.0, RIVER_FLOW_ABOVE_SURFACE_M + 1.0, 0.0]);
        assert_eq!(far_above, ZERO_FLOW, "水面から離れた空中は流されない");
    }

    /// 下る川の流速は 3D（Y 成分を持つ）で、大きさが flow_speed になる。
    #[test]
    fn river_flow_is_three_dimensional_and_normalized() {
        let vols = [river(&[[0.0, 10.0, 0.0], [20.0, 0.0, 0.0]], RIVER_W, RIVER_S, RIVER_D)];
        let q = WaterQuery::new(&vols);
        // 中間地点の水面あたり（水面 Y ≒ 5）
        let f = q.flow_at([10.0, 4.0, 0.0]);
        let len = (f[0] * f[0] + f[1] * f[1] + f[2] * f[2]).sqrt();
        assert!((len - RIVER_S).abs() < 1e-2, "流速の大きさは flow_speed（実際 {len}）");
        assert!(f[1] < 0.0, "下る川なので Y は負");
    }

    /// Ocean / Region は流速を持たない（川だけが流れる）。
    #[test]
    fn non_river_volumes_have_no_flow() {
        let vols = [ocean(5.0), region([0.0, 0.0, 0.0], [10.0, 5.0, 10.0], 2.0)];
        let q = WaterQuery::new(&vols);
        assert_eq!(q.flow_at([0.0, 0.0, 0.0]), ZERO_FLOW);
        assert_eq!(q.flow_at([123.0, -45.0, 6.0]), ZERO_FLOW);

        let empty: [ResolvedWaterVolume; 0] = [];
        assert_eq!(WaterQuery::new(&empty).flow_at([0.0, 0.0, 0.0]), ZERO_FLOW);
    }

    /// 川が 2 本重なる場合は、中心線に近い方の流速が採られる（打ち消し合わない）。
    #[test]
    fn overlapping_rivers_use_nearest_centerline() {
        // +X へ流れる川（中心 z=0）と、+Z へ流れる川（中心 x=8）。点 (8, 0, 1) は
        // 後者の中心線から 0m、前者の中心線から 1m。
        let vols = [
            straight_river(),
            river(&[[8.0, 0.0, -10.0], [8.0, 0.0, 10.0]], RIVER_W, RIVER_S, RIVER_D),
        ];
        let q = WaterQuery::new(&vols);
        let f = q.flow_at([8.0, -0.5, 1.0]);
        assert!(f[2] > 0.0 && f[0].abs() < 1e-3, "近い方（+Z の川）の流速が採られる");
    }
}
