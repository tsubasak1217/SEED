// ============================================================
//  broadphase.rs — AABB ブロードフェーズ（Sort-and-Sweep）
//
//  【Sort-and-Sweep アルゴリズム】
//    1. 全オブジェクトの AABB を X 軸の min でソート
//    2. 先頭からスイープし「X 区間が重なるアクティブセット」を維持
//    3. X が重なるペアについて Y・Z も確認し、3D AABB 重なりを検証
//    4. 重なるペアを候補として返す
//
//  計算量: O(n log n + k)  k = 候補ペア数
// ============================================================

use crate::math::Aabb;

/// AABB ペアをソートアンドスイープで絞り込み、衝突候補ペアを返す。
///
/// `objects` : (entity_id, world_aabb) のスライス。
/// 戻り値    : 重なりが検出された (entity_id_a, entity_id_b) のリスト（id_a < id_b に正規化）。
pub fn find_candidate_pairs(objects: &[(u64, Aabb)]) -> Vec<(u64, u64)> {
    if objects.len() < 2 {
        return Vec::new();
    }

    // X 軸の min でソート
    let mut sorted = objects.to_vec();
    sorted.sort_unstable_by(|a, b| {
        a.1.min.x.partial_cmp(&b.1.min.x).unwrap_or(std::cmp::Ordering::Equal)
    });

    let n = sorted.len();
    let mut pairs = Vec::new();

    for i in 0..n {
        let (id_a, aabb_a) = sorted[i];
        for j in (i + 1)..n {
            let (id_b, aabb_b) = sorted[j];
            // X 軸で離れていれば以降全スキップ
            if aabb_b.min.x > aabb_a.max.x {
                break;
            }
            // Y・Z 軸も重なるか確認
            if aabb_a.intersects(aabb_b) {
                let pair = if id_a < id_b { (id_a, id_b) } else { (id_b, id_a) };
                pairs.push(pair);
            }
        }
    }
    pairs
}
