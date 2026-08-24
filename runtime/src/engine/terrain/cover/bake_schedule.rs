// ============================================================
//  terrain/cover/bake_schedule.rs — カバー頂点焼き直しのフレーム分散スケジューラ（純粋層）
//
//  【解く問題】
//    カバー場（雪・落ち葉）が変化したチャンクは頂点へ焼き直される。走行中の轍や
//    広域の積雪では 1 回の積算ティックで数十チャンクが同時に変化するため、
//    まとめて焼くとそのフレームだけ数十ミリ秒のスパイクになる（発火しないフレームは
//    0ms なので、平均ではなく「たまに 1 フレームだけ跳ねる」という最も体感の悪い形で出る）。
//    そこで 1 フレームに焼くチャンク数へ予算を設け、超過分を次フレームへ繰り越す。
//
//  【なぜ単純な件数予算でよいのか（旧実装との差分）】
//    旧実装は「26 近傍で連結した待ちチャンクの塊は分割しない」という規則を持っていた。
//    隣り合う 2 チャンクを別フレームに焼くと、片方だけが新しい世代のカバー場を読んだ
//    状態が生まれ、境界の複製頂点どうしで変位が食い違って段差（隙間）が出るためである。
//    だがこの規則は全域降雪で全チャンクが 1 成分になり、予算がまったく効かなかった。
//
//    現在は `bake_wave.rs` が焼き直し開始時にカバー場を**凍結**する。1 つの波に属する
//    チャンクは何フレームに分かれて焼かれても同じ凍結データだけを読むので、
//    「別フレームに焼くと世代がずれる」という前提そのものが消えた。したがって
//    連結成分の分解は不要になり、本モジュールは優先度つきの件数予算だけを担う。
//
//  【優先順位】
//    ① 轍スタンプ由来（`Immediate`）… 接地への応答性が最優先。予算に関係なく必ず今フレーム焼く。
//    ② カメラ近傍                    … 見えている場所から先に直す。
//    ③ その他                        … 遠方。繰り越されても気付かれにくい。
// ============================================================

use std::collections::HashSet;

use crate::engine::terrain::chunk_coord::ChunkCoord;

/// 1 フレームに焼き直すチャンク数の予算。
///
/// 【値の根拠】1 チャンクの焼き直しは実測でおおむね 1〜2ms（頂点走査＋GPU 頂点バッファ書き換え）。
/// 60fps の 1 フレーム 16.7ms に対し、カバー以外の処理を圧迫しない上限として 4 チャンク
/// （≒ 4〜8ms）を採る。轍のような即時チャンクはこの予算の外で必ず焼かれるため、
/// 「操作への反応が遅れる」側には効かない。
pub const COVER_BAKE_CHUNK_BUDGET_PER_FRAME: usize = 4;

/// チャンクを「カメラ近傍」とみなす距離（メートル）。
///
/// 【値の根拠】既定チャンク一辺 32m に対しておよそ 3 チャンクぶん。この範囲は
/// 画面中央を占めて段差が目立つため、遠方より先に焼く。
pub const COVER_BAKE_NEAR_CAMERA_DISTANCE_M: f32 = 96.0;

/// 焼き直しチャンクの優先度（小さいほど先に焼く）。
#[derive(Debug, Clone, Copy, PartialEq, Eq, PartialOrd, Ord)]
pub enum CoverBakePriority {
    /// ① 轍スタンプ由来。予算を無視して必ず今フレーム焼く。
    Immediate,
    /// ② カメラ近傍。
    NearCamera,
    /// ③ その他（遠方）。
    Far,
}

/// スケジューリング結果。
///
/// 繰り越し分は返さない。未処理チャンクの保持は波（`CoverBakeWave`）の責務であり、
/// 呼び出し側は焼いたチャンクを `mark_baked` で消していくだけでよい（単一責任）。
#[derive(Debug, Default, PartialEq, Eq)]
pub struct CoverBakePlan {
    /// 今フレームに焼くチャンク（決定性のため座標順にソート済み）。
    pub bake: Vec<ChunkCoord>,
}

/// 未処理チャンク集合から「今フレーム焼く分」を選ぶ純粋関数。
///
/// - `pending`      … 焼き直し待ちチャンク集合（現在の波の未処理分）。
/// - `immediate`    … 轍スタンプ由来のチャンク集合（`pending` の部分集合でなくてよい）。
/// - `chunk_center` … チャンク中心のワールド座標を返す関数（カメラ距離の算出用）。
/// - `camera_pos`   … カメラのワールド座標。
/// - `budget`       … 1 フレームに焼くチャンク数の目安。
///
/// 【予算の意味（ソフト予算）】
///   - `Immediate` は予算に数えるが、予算超過でも必ず `bake` へ入れる（応答性優先）。
///   - それ以外は予算に余裕があるあいだだけ採用する。ただし **1 フレームに 1 件も
///     焼けない状態を作らない**ため、まだ 1 件も焼いていなければ 1 つだけは採用する
///     （進捗保証。`budget == 0` で永久に止まるのを防ぐ）。
pub fn plan_cover_bake(
    pending: &HashSet<ChunkCoord>,
    immediate: &HashSet<ChunkCoord>,
    chunk_center: impl Fn(ChunkCoord) -> [f32; 3],
    camera_pos: [f32; 3],
    budget: usize,
) -> CoverBakePlan {
    if pending.is_empty() {
        return CoverBakePlan::default();
    }

    // ── ① 各チャンクの優先度・カメラ距離を求める ──
    let near_sq_limit = COVER_BAKE_NEAR_CAMERA_DISTANCE_M * COVER_BAKE_NEAR_CAMERA_DISTANCE_M;
    let mut ranked: Vec<(CoverBakePriority, f32, (i32, i32, i32), ChunkCoord)> = pending
        .iter()
        .map(|&c| {
            let center = chunk_center(c);
            let d = [
                center[0] - camera_pos[0],
                center[1] - camera_pos[1],
                center[2] - camera_pos[2],
            ];
            let near_sq = d[0] * d[0] + d[1] * d[1] + d[2] * d[2];
            let priority = if immediate.contains(&c) {
                CoverBakePriority::Immediate
            } else if near_sq <= near_sq_limit {
                CoverBakePriority::NearCamera
            } else {
                CoverBakePriority::Far
            };
            (priority, near_sq, (c.x, c.y, c.z), c)
        })
        .collect();

    // 優先度 → カメラ距離 → 座標 の順で決定的に並べる。
    ranked.sort_by(|a, b| {
        a.0.cmp(&b.0)
            .then(a.1.total_cmp(&b.1))
            .then(a.2.cmp(&b.2))
    });

    // ── ② 予算に従って採否を決める ──
    let mut plan = CoverBakePlan::default();
    for (priority, _, _, coord) in ranked {
        let take = match priority {
            // 轍は予算を無視して必ず焼く。
            CoverBakePriority::Immediate => true,
            // 予算に余裕がある、または「まだ 1 件も焼いていない」なら焼く（進捗保証）。
            _ => plan.bake.len() < budget || plan.bake.is_empty(),
        };
        if take {
            plan.bake.push(coord);
        }
    }

    plan.bake.sort_by_key(|c| (c.x, c.y, c.z));
    plan
}

// ─── テスト ──────────────────────────────────────────────────────────────────

#[cfg(test)]
mod tests {
    use super::*;

    /// チャンク一辺（テスト用の仮想設定）。
    const EXTENT: f32 = 32.0;

    /// テスト用のチャンク中心算出（一辺 EXTENT の格子）。
    fn center(c: ChunkCoord) -> [f32; 3] {
        [
            c.x as f32 * EXTENT + EXTENT * 0.5,
            c.y as f32 * EXTENT + EXTENT * 0.5,
            c.z as f32 * EXTENT + EXTENT * 0.5,
        ]
    }

    fn set(coords: &[(i32, i32, i32)]) -> HashSet<ChunkCoord> {
        coords.iter().map(|&(x, y, z)| ChunkCoord::new(x, y, z)).collect()
    }

    /// 待ちが空なら何も焼かない。
    #[test]
    fn empty_pending_yields_empty_plan() {
        let plan = plan_cover_bake(
            &HashSet::new(),
            &HashSet::new(),
            center,
            [0.0; 3],
            COVER_BAKE_CHUNK_BUDGET_PER_FRAME,
        );
        assert!(plan.bake.is_empty());
    }

    /// **予算が実効であること**（本改修の核心）。
    /// 全域降雪を模した「26 近傍で全部つながった 64 チャンク」でも、
    /// 1 フレームに焼くのは予算ぶんだけであること（旧実装はここで 64 件全部焼いていた）。
    #[test]
    fn budget_caps_even_for_fully_connected_pending() {
        let mut coords = Vec::new();
        for x in 0..8 {
            for z in 0..8 {
                coords.push((x, 0, z));
            }
        }
        let pending = set(&coords);
        let plan = plan_cover_bake(
            &pending,
            &HashSet::new(),
            center,
            [0.0; 3],
            COVER_BAKE_CHUNK_BUDGET_PER_FRAME,
        );
        assert_eq!(
            plan.bake.len(),
            COVER_BAKE_CHUNK_BUDGET_PER_FRAME,
            "連結していても予算で頭打ちになる: {plan:?}",
        );
    }

    /// 轍スタンプ由来は予算を使い切っていても必ず今フレームに焼かれること。
    #[test]
    fn immediate_bypasses_budget() {
        let pending = set(&[(0, 0, 0), (1, 0, 0), (2, 0, 0), (3, 0, 0), (4, 0, 0), (5, 0, 0)]);
        let immediate = set(&[(5, 0, 0)]);
        let plan = plan_cover_bake(&pending, &immediate, center, [0.0; 3], 1);
        assert!(
            plan.bake.contains(&ChunkCoord::new(5, 0, 0)),
            "轍由来は予算に関係なく焼かれる: {plan:?}",
        );
        // 轍だけで予算（1）を使い切るので、残りは繰り越される（進捗保証は轍で果たされている）。
        assert_eq!(plan.bake.len(), 1, "予算ぶんを超えて焼かない: {plan:?}");
    }

    /// 轍が複数あれば、予算を超えても全て今フレームで焼かれること。
    #[test]
    fn all_immediate_chunks_bake_regardless_of_budget() {
        let pending = set(&[(0, 0, 0), (10, 0, 0), (20, 0, 0), (30, 0, 0)]);
        let immediate = set(&[(0, 0, 0), (10, 0, 0), (20, 0, 0)]);
        let plan = plan_cover_bake(&pending, &immediate, center, [1000.0, 0.0, 0.0], 1);
        assert_eq!(plan.bake.len(), 3, "轍 3 件すべてが焼かれる: {plan:?}");
        assert!(!plan.bake.contains(&ChunkCoord::new(30, 0, 0)));
    }

    /// カメラ近傍のチャンクが遠方より先に焼かれること。
    #[test]
    fn near_camera_chunks_go_first() {
        let pending = set(&[(0, 0, 0), (50, 0, 0)]);
        let plan = plan_cover_bake(&pending, &HashSet::new(), center, [16.0, 16.0, 16.0], 1);
        assert_eq!(plan.bake, vec![ChunkCoord::new(0, 0, 0)], "近い方が先: {plan:?}");
    }

    /// 予算 0 でも必ず 1 件は進むこと（永久停止の防止）。
    #[test]
    fn progress_is_guaranteed_with_zero_budget() {
        let pending = set(&[(0, 0, 0), (1, 0, 0)]);
        let plan = plan_cover_bake(&pending, &HashSet::new(), center, [0.0; 3], 0);
        assert_eq!(plan.bake.len(), 1, "予算 0 でも 1 件は焼く: {plan:?}");
    }

    /// 同じ入力からは常に同じ計画が出る（決定性）。
    #[test]
    fn plan_is_deterministic() {
        let pending = set(&[(0, 0, 0), (10, 0, 0), (20, 0, 0), (21, 0, 0)]);
        let a = plan_cover_bake(&pending, &HashSet::new(), center, [0.0; 3], 2);
        let b = plan_cover_bake(&pending, &HashSet::new(), center, [0.0; 3], 2);
        assert_eq!(a, b);
    }

    /// 焼いた分を取り除きながら回せば、必ず全件が有限フレームで消化されること。
    #[test]
    fn repeated_planning_drains_all_chunks() {
        let mut pending = set(&[(0, 0, 0), (1, 0, 0), (2, 0, 0), (3, 0, 0), (4, 0, 0)]);
        let mut frames = 0;
        while !pending.is_empty() {
            let plan = plan_cover_bake(&pending, &HashSet::new(), center, [0.0; 3], 2);
            assert!(!plan.bake.is_empty(), "毎フレーム必ず進捗する");
            for c in &plan.bake {
                pending.remove(c);
            }
            frames += 1;
            assert!(frames < 10, "有限フレームで消化される");
        }
        assert_eq!(frames, 3, "5 件を予算 2 で消化 = 3 フレーム");
    }
}
