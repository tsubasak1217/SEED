// ============================================================
//  render_relevance.rs — 「このフレームの描画に関与するか」の合併判定
//
//  》含む処理「
//  - RelevanceVolume: カメラ視錐台 ∪ 影カスケード範囲 ∪ RT キャスタ半径 の合併領域
//  - contains_aabb:   ワールド空間 AABB がその合併領域に少しでも掛かるかの判定
//
//  【なぜ「視錐台だけ」では駄目なのか】
//  「画面に映っていないインスタンスの更新を省く」を素朴にカメラ視錐台だけで判定すると、
//    ・画面外にいるが影を落とす物体（影が消える）
//    ・画面外にあるがレイトレースの反射・影レイが当たる物体（映り込みが消える）
//  が誤って捨てられる。本エンジンは過去に「オブジェクト単位の視錐台カリング」を
//  この誤棄却（画面端のポッピング・チャンク消え）が理由で撤去した経緯がある
//  （`InstancedModelBatch::update` のコメント参照）。
//  そこで「関与する」を **視錐台 ∪ 影カスケード ∪ RT 半径** の合併として定義し、
//  この 3 つのいずれにも掛からないものだけを非関与とする。
//
//  【判定は必ず保守側（含める側）へ倒す】
//  判定材料が無い（視錐台も影も RT も未設定）ときは「全部関与する」を返す。
//  設定漏れで物が消えるより、最適化が効かない方が安全なため。
// ============================================================

use super::gpu_resources::{aabb_distance_sq, aabb_outside_frustum, extract_frustum_planes};

/// 影カスケードを最大いくつ保持するか。
///
/// `shadow::CSM_CASCADE_COUNT` と一致させる必要はない（それ以下なら先頭から詰める）が、
/// 足りないと保守側へ倒す判定ができなくなるため、実際のカスケード数以上にしておくこと。
pub const MAX_RELEVANCE_CASCADES: usize = 4;

/// 「このフレームの描画に関与する」領域の合併。
///
/// 3 つの構成要素はいずれも省略可能（`None` / 空）で、省略した要素はその条件で
/// 関与判定しないことを意味する。すべて省略した場合は全 AABB が関与扱いになる。
#[derive(Clone, Debug, Default)]
pub struct RelevanceVolume {
    /// カメラ視錐台の 6 平面（view-proj から抽出）。未設定なら視錐台では絞らない。
    camera_frustum: Option<[[f32; 4]; 6]>,
    /// 影カスケードごとの 6 平面（平行光の light view-proj から抽出）。
    /// 実際に描画されるカスケードぶんだけ詰める。
    cascade_frusta: [Option<[[f32; 4]; 6]>; MAX_RELEVANCE_CASCADES],
    /// RT キャスタとして拾う球（カメラ中心）。`(中心, 半径²)`。
    rt_sphere: Option<([f32; 3], f32)>,
}

impl RelevanceVolume {
    /// 何も絞らない（全部関与扱い）合併領域を作る。
    pub fn unbounded() -> Self { Self::default() }

    /// カメラ視錐台を設定する（`view_proj` は行優先のワールド→クリップ行列）。
    pub fn with_camera(mut self, view_proj: &[[f32; 4]; 4]) -> Self {
        self.camera_frustum = Some(extract_frustum_planes(view_proj));
        self
    }

    /// 影カスケードを設定する（各要素は light view-proj）。
    ///
    /// `MAX_RELEVANCE_CASCADES` を超えるぶんは無視する（先頭ほど近距離＝影響が大きい）。
    pub fn with_shadow_cascades(mut self, cascade_vps: &[[[f32; 4]; 4]]) -> Self {
        for (slot, vp) in self.cascade_frusta.iter_mut().zip(cascade_vps.iter()) {
            *slot = Some(extract_frustum_planes(vp));
        }
        self
    }

    /// RT キャスタ半径を設定する（カメラ位置中心の球）。半径 0 以下なら設定しない。
    pub fn with_rt_radius(mut self, camera_pos: [f32; 3], radius: f32) -> Self {
        if radius > 0.0 {
            self.rt_sphere = Some((camera_pos, radius * radius));
        }
        self
    }

    /// 絞り込み条件を 1 つも持たない（＝全部関与扱いになる）か。
    pub fn is_unbounded(&self) -> bool {
        self.camera_frustum.is_none()
            && self.cascade_frusta.iter().all(|c| c.is_none())
            && self.rt_sphere.is_none()
    }

    /// ワールド空間 AABB がこの合併領域に少しでも掛かるか。
    ///
    /// 掛からない（＝どの条件にも当てはまらない）ときだけ `false`。
    /// 条件を 1 つも持たない場合は常に `true`（保守側）。
    pub fn contains_aabb(&self, min: [f32; 3], max: [f32; 3]) -> bool {
        if self.is_unbounded() { return true; }

        // ① カメラ視錐台
        if let Some(planes) = &self.camera_frustum {
            if !aabb_outside_frustum(planes, min, max) { return true; }
        }
        // ② 影カスケード（1 つでも掛かれば関与）
        for planes in self.cascade_frusta.iter().flatten() {
            if !aabb_outside_frustum(planes, min, max) { return true; }
        }
        // ③ RT キャスタ球（AABB と球の最短距離で判定）
        if let Some((center, radius_sq)) = self.rt_sphere {
            if aabb_distance_sq(min, max, center) <= radius_sq { return true; }
        }
        false
    }
}

// ============================================================
//  テスト
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;

    /// `center` 中心・1 辺 2*half の立方体をクリップ空間へ写す単純な正射 view-proj。
    ///
    /// `extract_frustum_planes` は `vp[row][col]` の行優先（`clip = vp * world_pos`）を
    /// 前提にするので、平行移動は**第 4 列**に置く（第 4 行ではない）。
    fn ortho_vp(center: [f32; 3], half: f32) -> [[f32; 4]; 4] {
        let s = 1.0 / half;
        [
            [s, 0.0, 0.0, -center[0] * s],
            [0.0, s, 0.0, -center[1] * s],
            [0.0, 0.0, s, -center[2] * s],
            [0.0, 0.0, 0.0, 1.0],
        ]
    }

    /// 1 辺 2r の立方体 AABB を作る。
    fn cube(center: [f32; 3], r: f32) -> ([f32; 3], [f32; 3]) {
        (
            [center[0] - r, center[1] - r, center[2] - r],
            [center[0] + r, center[1] + r, center[2] + r],
        )
    }

    /// 条件を 1 つも持たない合併領域は全部を関与扱いにする（保守側）。
    #[test]
    fn unbounded_accepts_everything() {
        let v = RelevanceVolume::unbounded();
        let (min, max) = cube([1000.0, 1000.0, 1000.0], 1.0);
        assert!(v.contains_aabb(min, max));
    }

    /// カメラ視錐台の内側は関与、遠く外側は非関与。
    #[test]
    fn camera_frustum_selects_inside_only() {
        let v = RelevanceVolume::default().with_camera(&ortho_vp([0.0, 0.0, 0.0], 10.0));
        let (in_min, in_max) = cube([0.0, 0.0, 0.0], 1.0);
        assert!(v.contains_aabb(in_min, in_max), "視錐台内は関与");
        let (out_min, out_max) = cube([100.0, 0.0, 0.0], 1.0);
        assert!(!v.contains_aabb(out_min, out_max), "視錐台外（他条件なし）は非関与");
    }

    /// カメラ視錐台の外でも、影カスケードに掛かれば関与する。
    /// （これが無いと画面外キャスタの影が消える＝過去に撤去された素朴カリングの再発）
    #[test]
    fn shadow_cascade_keeps_offscreen_caster() {
        let cascades = [ortho_vp([100.0, 0.0, 0.0], 20.0)];
        let v = RelevanceVolume::default()
            .with_camera(&ortho_vp([0.0, 0.0, 0.0], 10.0))
            .with_shadow_cascades(&cascades);
        let (min, max) = cube([100.0, 0.0, 0.0], 1.0);
        assert!(v.contains_aabb(min, max), "視錐台外でも影カスケード内なら関与");
        let (far_min, far_max) = cube([1000.0, 0.0, 0.0], 1.0);
        assert!(!v.contains_aabb(far_min, far_max), "どちらにも掛からなければ非関与");
    }

    /// カメラ視錐台・影の外でも、RT キャスタ半径内なら関与する。
    #[test]
    fn rt_radius_keeps_nearby_caster() {
        let v = RelevanceVolume::default()
            .with_camera(&ortho_vp([0.0, 0.0, 0.0], 1.0))
            .with_rt_radius([0.0, 0.0, 0.0], 50.0);
        // 視錐台（半径 1）の外だが RT 半径 50 の内側。
        let (min, max) = cube([30.0, 0.0, 0.0], 1.0);
        assert!(v.contains_aabb(min, max), "RT 半径内なら関与");
        let (out_min, out_max) = cube([200.0, 0.0, 0.0], 1.0);
        assert!(!v.contains_aabb(out_min, out_max), "RT 半径外なら非関与");
    }

    /// 半径ちょうど境界の AABB は関与側（<=）に入る。境界で点滅しないことの確認。
    #[test]
    fn rt_radius_boundary_is_inclusive() {
        let v = RelevanceVolume::default().with_rt_radius([0.0, 0.0, 0.0], 10.0);
        // AABB の最近点がちょうど半径 10 に接する。
        let (min, max) = ([10.0, -1.0, -1.0], [12.0, 1.0, 1.0]);
        assert!(v.contains_aabb(min, max), "境界上は関与側");
    }

    /// 巨大な AABB は中心が遠くても、カメラ側へ伸びていれば関与する
    /// （原点距離ではなく AABB 距離で見ていることの確認。地形チャンクの誤棄却防止）。
    #[test]
    fn large_aabb_reaching_camera_is_relevant() {
        let v = RelevanceVolume::default().with_rt_radius([0.0, 0.0, 0.0], 10.0);
        // 中心は x=500 と遠いが、AABB は x=-1 まで伸びている。
        let (min, max) = ([-1.0, -1.0, -1.0], [1000.0, 1.0, 1.0]);
        assert!(v.contains_aabb(min, max), "AABB がカメラ近傍へ伸びていれば関与");
    }

}
