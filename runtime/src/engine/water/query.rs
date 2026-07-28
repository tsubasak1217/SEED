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
//    Spline … W4 で実装。常に無視する（collect 側でも除外している）。
//
//  複数の水が重なる場合、水面高さは最も高いものを採用する
//  （プールの上に大洋がある等でも「最初にぶつかる水面」が返る）。
// ============================================================

use super::resolved::ResolvedWaterVolume;
use crate::engine::components::water_volume_component::WaterVolumeKind;

// ─── 定数 ────────────────────────────────────────────────────

/// W1 の流速（川スプライン未実装のため常にゼロ）。W4 で実装する。
const ZERO_FLOW: [f32; 3] = [0.0, 0.0, 0.0];

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
        let mut best: Option<f32> = None;
        for v in self.volumes {
            // XZ が範囲内のボリュームだけが水面高さを提供する
            let Some(y) = volume_surface_at_xz(v, point) else { continue };
            // 最も高い水面を採用する（f32 の比較は NaN を除外したいので max ではなく明示比較）
            best = Some(match best {
                Some(prev) if prev >= y => prev,
                _ => y,
            });
        }
        best
    }

    /// この点の流速。W1 では常に [0,0,0]（W4 の川スプラインで実装）。
    ///
    /// 引数 `point` は W4 でスプライン上の最近傍を求めるために使う。
    /// W1 では未使用だが、呼び出し側の API を W4 で変えずに済ませるため既に受け取る。
    pub fn flow_at(&self, _point: [f32; 3]) -> [f32; 3] {
        ZERO_FLOW
    }
}

// ─── 判定ヘルパー ────────────────────────────────────────────

/// 点の XZ がボリュームの水平範囲に入っているか。
///
/// Ocean は XZ 無限なので常に true。Spline は未実装なので常に false。
fn volume_contains_xz(v: &ResolvedWaterVolume, point: [f32; 3]) -> bool {
    match v.kind {
        WaterVolumeKind::Ocean => true,
        WaterVolumeKind::Region => {
            let dx = point[0] - v.center[0];
            let dz = point[2] - v.center[2];
            dx.abs() <= v.half_extents[0] && dz.abs() <= v.half_extents[2]
        }
        // W4 で実装。それまでは問い合わせ対象外。
        WaterVolumeKind::Spline => false,
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
        WaterVolumeKind::Spline => false,
    }
}

/// 点の XZ における、このボリュームの水面高さ。範囲外なら None。
/// Y 座標は問わない（水面の上に居ても真下の水面は返る）。
fn volume_surface_at_xz(v: &ResolvedWaterVolume, point: [f32; 3]) -> Option<f32> {
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
            fresnel_power: 0.0,
            fresnel_strength: 0.0,
            reflection_color: [0.0; 3],
            refraction_distortion: 0.0,
            ripple_strength: 0.0,
            ripple_foam_threshold: 0.0,
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
        }
    }

    /// Spline（W4 未実装）を作る。常に無視されることの確認用。
    fn spline() -> ResolvedWaterVolume {
        ResolvedWaterVolume {
            kind: WaterVolumeKind::Spline,
            surface_y: 100.0,
            center: [0.0; 3],
            half_extents: [1000.0; 3],
            ocean_extent: 0.0,
            visual: dummy_visual(),
            // 問い合わせ（水中判定）はピッキング ID を使わないのでダミー
            actor_dfs_id: 0,
        }
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

    /// Spline は W4 未実装のため、問い合わせでは常に無視される。
    #[test]
    fn spline_is_always_ignored() {
        let vols = [spline()];
        let q = WaterQuery::new(&vols);
        assert!(!q.is_underwater([0.0, 0.0, 0.0]));
        assert_eq!(q.surface_height_at([0.0, 0.0, 0.0]), None);
    }

    /// flow_at は W1 では常にゼロ（水があってもなくても）。
    #[test]
    fn flow_is_always_zero_in_w1() {
        let vols = [ocean(5.0), region([0.0, 0.0, 0.0], [10.0, 5.0, 10.0], 2.0)];
        let q = WaterQuery::new(&vols);
        assert_eq!(q.flow_at([0.0, 0.0, 0.0]), [0.0, 0.0, 0.0]);
        assert_eq!(q.flow_at([123.0, -45.0, 6.0]), [0.0, 0.0, 0.0]);

        let empty: [ResolvedWaterVolume; 0] = [];
        assert_eq!(WaterQuery::new(&empty).flow_at([0.0, 0.0, 0.0]), [0.0, 0.0, 0.0]);
    }
}
