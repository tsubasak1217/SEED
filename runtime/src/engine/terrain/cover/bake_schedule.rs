// ============================================================
//  terrain/cover/bake_schedule.rs — カバー頂点焼き直しのフレーム分散スケジューラ（純粋層）
//
//  【解く問題】
//    カバー場（雪・落ち葉）が変化したチャンクは `cover_pending_apply` へ積まれ、
//    `COVER_APPLY_INTERVAL_SEC` ごとの発火フレームで**全件まとめて**頂点へ焼き直されていた。
//    走行中の轍や広域の積雪では 1 発火に数十チャンクが集中し、そのフレームだけ
//    数十ミリ秒のスパイクになる（発火しないフレームは 0ms なので、平均ではなく
//    「たまに 1 フレームだけ跳ねる」という最も体感の悪い形で出る）。
//
//    そこで 1 フレームに焼くチャンク数へ予算を設け、超過分を次の発火フレームへ繰り越す。
//
//  【境界整合という制約（この設計の核心）】
//    チャンク境界の複製頂点は、隣接チャンクのカバー場も読んで変位を決める
//    （`CoverNeighborhood`／26 近傍）。焼き込み値はワールド位置の純関数なので、
//    **両側が同じ世代のカバー場を読んでいる限り**どちらのメッシュから焼いても一致する。
//    逆に言えば、隣り合う 2 チャンクを別フレームに焼くと、片方だけが新しい世代を読んだ
//    状態が生まれ、境界に段差（隙間）が出る。
//
//    したがって予算は「26 近傍で連結した待ちチャンクの塊（コンポーネント）」を
//    **決して分割してはならない**。本モジュールは待ち集合を 26 近傍連結成分へ分解し、
//    成分単位で採否を決めることで、フレーム分散と境界整合を両立させる。
//
//  【優先順位】
//    ① 轍スタンプ由来（`immediate`）… 接地への応答性が最優先。予算に関係なく必ず今フレーム焼く。
//    ② カメラ近傍            … 見えている場所から先に直す。
//    ③ その他                … 遠方。繰り越されても気付かれにくい。
//
//  【繰り越しの安全性】
//    採用されなかった成分は呼び出し側の待ち集合（`HashSet`）へ戻される。待ち集合は
//    集合なので、繰り越し中に同じチャンクへ追加変更が来ても重複して積まれず
//    （二重焼き防止）、逆に落ちることもない（取りこぼし防止）。
// ============================================================

use std::collections::{HashMap, HashSet};

use crate::engine::terrain::chunk_coord::ChunkCoord;

/// 1 フレームに焼き直すチャンク数の予算（コンポーネント単位で数える）。
///
/// 【値の根拠】1 チャンクの焼き直しは実測でおおむね 1〜2ms（頂点走査＋GPU 頂点バッファ書き換え）。
/// 60fps の 1 フレーム 16.7ms に対し、カバー以外の処理を圧迫しない上限として 4 チャンク
/// （≒ 4〜8ms）を採る。轍のような即時成分はこの予算の外で必ず焼かれるため、
/// 「操作への反応が遅れる」側には効かない。
pub const COVER_BAKE_CHUNK_BUDGET_PER_FRAME: usize = 4;

/// チャンクを「カメラ近傍」とみなす距離（メートル）。
///
/// 【値の根拠】既定チャンク一辺 32m に対しておよそ 3 チャンクぶん。この範囲は
/// 画面中央を占めて段差が目立つため、遠方より先に焼く。
pub const COVER_BAKE_NEAR_CAMERA_DISTANCE_M: f32 = 96.0;

/// 焼き直し成分の優先度（小さいほど先に焼く）。
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
#[derive(Debug, Default, PartialEq, Eq)]
pub struct CoverBakePlan {
    /// 今フレームに焼くチャンク（決定性のため座標順にソート済み）。
    pub bake: Vec<ChunkCoord>,
    /// 次フレーム以降へ繰り越すチャンク。
    pub deferred: Vec<ChunkCoord>,
}

/// 待ちチャンク集合を「今フレーム焼く分」と「繰り越す分」へ分ける純粋関数。
///
/// - `pending`      … 焼き直し待ちチャンク集合。
/// - `immediate`    … 轍スタンプ由来のチャンク集合（`pending` の部分集合でなくてよい）。
/// - `chunk_center` … チャンク中心のワールド座標を返す関数（カメラ距離の算出用）。
/// - `camera_pos`   … カメラのワールド座標。
/// - `budget`       … 1 フレームに焼くチャンク数の目安。
///
/// 【予算の意味（ソフト予算）】
///   - `Immediate` 成分は予算に数えるが、予算超過でも必ず `bake` へ入れる（応答性優先）。
///   - 非 `Immediate` 成分は、予算に余裕があるあいだだけ採用する。ただし
///     **1 フレームに 1 成分も焼けない状態を作らない**ため、まだ 1 件も焼いていなければ
///     予算を超える成分でも 1 つだけは採用する（進捗保証。さもないと予算より大きい成分が
///     永久に焼かれない）。
///   - 成分は決して分割しない（境界整合の担保。モジュール冒頭の解説を参照）。
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

    // ── ① 26 近傍で連結した成分へ分解する ──
    let components = split_into_neighbor_components(pending);

    // ── ② 各成分の優先度・カメラ距離を求める ──
    //   成分の優先度は「メンバーの中で最も高い（＝値が小さい）優先度」。
    //   一部でも轍が絡む成分は、境界整合のため丸ごと即時に焼く必要があるためである。
    struct Ranked {
        priority: CoverBakePriority,
        /// 成分内の最小カメラ距離の二乗（近い成分ほど先に焼く）。
        near_sq:  f32,
        /// 決定性のためのタイブレーク（成分内の最小座標）。
        tiebreak: (i32, i32, i32),
        members:  Vec<ChunkCoord>,
    }

    let near_sq_limit = COVER_BAKE_NEAR_CAMERA_DISTANCE_M * COVER_BAKE_NEAR_CAMERA_DISTANCE_M;
    let mut ranked: Vec<Ranked> = components
        .into_iter()
        .map(|members| {
            let mut has_immediate = false;
            let mut near_sq = f32::MAX;
            let mut tiebreak = (i32::MAX, i32::MAX, i32::MAX);
            for &c in &members {
                if immediate.contains(&c) {
                    has_immediate = true;
                }
                let center = chunk_center(c);
                let d = [
                    center[0] - camera_pos[0],
                    center[1] - camera_pos[1],
                    center[2] - camera_pos[2],
                ];
                let sq = d[0] * d[0] + d[1] * d[1] + d[2] * d[2];
                if sq < near_sq { near_sq = sq; }
                let key = (c.x, c.y, c.z);
                if key < tiebreak { tiebreak = key; }
            }
            let priority = if has_immediate {
                CoverBakePriority::Immediate
            } else if near_sq <= near_sq_limit {
                CoverBakePriority::NearCamera
            } else {
                CoverBakePriority::Far
            };
            Ranked { priority, near_sq, tiebreak, members }
        })
        .collect();

    // 優先度 → カメラ距離 → 座標 の順で決定的に並べる。
    ranked.sort_by(|a, b| {
        a.priority
            .cmp(&b.priority)
            .then(a.near_sq.total_cmp(&b.near_sq))
            .then(a.tiebreak.cmp(&b.tiebreak))
    });

    // ── ③ 予算に従って採否を決める（成分は分割しない）──
    let mut plan  = CoverBakePlan::default();
    let mut baked = 0usize;
    for r in ranked {
        let take = match r.priority {
            // 轍は予算を無視して必ず焼く。
            CoverBakePriority::Immediate => true,
            // 予算に余裕がある、または「まだ 1 件も焼いていない」なら焼く（進捗保証）。
            _ => baked < budget || baked == 0,
        };
        if take {
            baked += r.members.len();
            plan.bake.extend(r.members);
        } else {
            plan.deferred.extend(r.members);
        }
    }

    plan.bake.sort_by_key(|c| (c.x, c.y, c.z));
    plan.deferred.sort_by_key(|c| (c.x, c.y, c.z));
    plan
}

/// 待ちチャンク集合を 26 近傍連結成分へ分解する（純粋関数）。
///
/// 26 近傍＝XZ の 8 近傍 × 上下 3 段（角を含む）。境界の複製頂点が読み合う範囲と一致させる。
/// 走査順は座標ソート順に固定してあるため、同じ入力からは常に同じ分解が得られる。
fn split_into_neighbor_components(pending: &HashSet<ChunkCoord>) -> Vec<Vec<ChunkCoord>> {
    // 決定的な走査順にするため、いったんソートした配列を作る。
    let mut sorted: Vec<ChunkCoord> = pending.iter().copied().collect();
    sorted.sort_by_key(|c| (c.x, c.y, c.z));

    // coord → 成分番号。未割り当ては未登録。
    let mut assigned: HashMap<ChunkCoord, usize> = HashMap::new();
    let mut components: Vec<Vec<ChunkCoord>> = Vec::new();

    for &seed in &sorted {
        if assigned.contains_key(&seed) {
            continue;
        }
        let id = components.len();
        let mut members = Vec::new();
        // 幅優先で 26 近傍を辿る。
        let mut stack = vec![seed];
        assigned.insert(seed, id);
        while let Some(cur) = stack.pop() {
            members.push(cur);
            for dy in -1..=1i32 {
                for dz in -1..=1i32 {
                    for dx in -1..=1i32 {
                        if dx == 0 && dy == 0 && dz == 0 {
                            continue;
                        }
                        let n = ChunkCoord::new(cur.x + dx, cur.y + dy, cur.z + dz);
                        if pending.contains(&n) && !assigned.contains_key(&n) {
                            assigned.insert(n, id);
                            stack.push(n);
                        }
                    }
                }
            }
        }
        members.sort_by_key(|c| (c.x, c.y, c.z));
        components.push(members);
    }
    components
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
            &HashSet::new(), &HashSet::new(), center, [0.0; 3],
            COVER_BAKE_CHUNK_BUDGET_PER_FRAME,
        );
        assert!(plan.bake.is_empty() && plan.deferred.is_empty());
    }

    /// 26 近傍で隣り合うチャンクは、予算より大きくても必ず同一フレームへまとまること
    /// （境界整合の担保。これが崩れると境界に隙間が出る）。
    #[test]
    fn adjacent_chunks_are_never_split_across_frames() {
        // 角接触（斜め）と上下段も含めた 5 チャンクの塊。予算 1 でも分割してはいけない。
        let pending = set(&[(0, 0, 0), (1, 0, 0), (1, 1, 1), (2, 1, 1), (2, 2, 2)]);
        let plan = plan_cover_bake(&pending, &HashSet::new(), center, [0.0; 3], 1);
        assert_eq!(plan.bake.len(), 5, "連結した塊は分割されない: {:?}", plan);
        assert!(plan.deferred.is_empty());
    }

    /// 離れた塊は予算を超えたぶんが繰り越されること。
    #[test]
    fn disconnected_components_are_deferred_beyond_budget() {
        // 3 つの孤立チャンク（互いに 26 近傍でない = 2 以上離す）。
        let pending = set(&[(0, 0, 0), (10, 0, 0), (20, 0, 0)]);
        // 予算 1: 最初の 1 成分だけ焼き、残り 2 成分は繰り越す。
        let plan = plan_cover_bake(&pending, &HashSet::new(), center, [0.0; 3], 1);
        assert_eq!(plan.bake.len(), 1, "予算 1 なら 1 成分だけ焼く: {:?}", plan);
        assert_eq!(plan.deferred.len(), 2);
        // 焼いた分と繰り越し分の和は入力と一致する（取りこぼし・重複なし）。
        let mut all: Vec<ChunkCoord> = plan.bake.iter().chain(plan.deferred.iter()).copied().collect();
        all.sort_by_key(|c| (c.x, c.y, c.z));
        let mut expect: Vec<ChunkCoord> = pending.iter().copied().collect();
        expect.sort_by_key(|c| (c.x, c.y, c.z));
        assert_eq!(all, expect, "焼き分＋繰り越し分は待ち集合と一致する");
    }

    /// 轍スタンプ由来の成分は予算を使い切っていても必ず今フレームに焼かれること。
    #[test]
    fn immediate_component_bypasses_budget() {
        // 遠方の孤立チャンク 5 個 ＋ 轍が付いた遠方チャンク 1 個。
        let pending = set(&[
            (0, 0, 0), (10, 0, 0), (20, 0, 0), (30, 0, 0), (40, 0, 0), (50, 0, 0),
        ]);
        let immediate = set(&[(50, 0, 0)]);
        let plan = plan_cover_bake(&pending, &immediate, center, [0.0; 3], 1);
        assert!(
            plan.bake.contains(&ChunkCoord::new(50, 0, 0)),
            "轍由来は予算に関係なく焼かれる: {:?}", plan,
        );
        // 轍だけで予算（1）を使い切るので、残りの遠方成分は繰り越される。
        // 「進捗保証」は轍で既に果たされているため、ここで追加採用はしない。
        assert_eq!(plan.bake.len(), 1, "予算ぶんを超えて焼かない: {:?}", plan);
        assert_eq!(plan.deferred.len(), 5, "残りは繰り越す: {:?}", plan);
    }

    /// 轍成分が複数あれば、予算を超えても全て今フレームで焼かれること。
    #[test]
    fn all_immediate_components_bake_regardless_of_budget() {
        let pending  = set(&[(0, 0, 0), (10, 0, 0), (20, 0, 0), (30, 0, 0)]);
        let immediate = set(&[(0, 0, 0), (10, 0, 0), (20, 0, 0)]);
        let plan = plan_cover_bake(&pending, &immediate, center, [1000.0, 0.0, 0.0], 1);
        assert_eq!(plan.bake.len(), 3, "轍 3 成分すべてが焼かれる: {:?}", plan);
        assert_eq!(plan.deferred, vec![ChunkCoord::new(30, 0, 0)]);
    }

    /// カメラ近傍の成分が遠方より先に焼かれること。
    #[test]
    fn near_camera_components_go_first() {
        // (0,0,0) はカメラ直近、(50,0,0) は遠方。予算 1 で近い方だけが焼かれる。
        let pending = set(&[(0, 0, 0), (50, 0, 0)]);
        let plan = plan_cover_bake(&pending, &HashSet::new(), center, [16.0, 16.0, 16.0], 1);
        assert_eq!(plan.bake, vec![ChunkCoord::new(0, 0, 0)], "近い成分が先: {:?}", plan);
        assert_eq!(plan.deferred, vec![ChunkCoord::new(50, 0, 0)]);
    }

    /// 予算より大きい単独成分しか無くても必ず進捗すること（永久繰り越しの防止）。
    #[test]
    fn progress_is_guaranteed_when_component_exceeds_budget() {
        let pending = set(&[(0, 0, 0), (1, 0, 0), (2, 0, 0), (3, 0, 0), (4, 0, 0)]);
        let plan = plan_cover_bake(&pending, &HashSet::new(), center, [1000.0, 0.0, 0.0], 1);
        assert_eq!(plan.bake.len(), 5, "予算超過でも 1 成分は必ず焼く: {:?}", plan);
        assert!(plan.deferred.is_empty());
    }

    /// 同じ入力からは常に同じ計画が出る（決定性）。
    #[test]
    fn plan_is_deterministic() {
        let pending = set(&[(0, 0, 0), (10, 0, 0), (20, 0, 0), (21, 0, 0)]);
        let a = plan_cover_bake(&pending, &HashSet::new(), center, [0.0; 3], 2);
        let b = plan_cover_bake(&pending, &HashSet::new(), center, [0.0; 3], 2);
        assert_eq!(a, b);
    }

    /// 繰り越し中に同じチャンクへ追加変更が来ても、集合なので二重に焼かれないこと。
    /// （呼び出し側の「繰り越しを待ち集合へ戻す」運用を模した検証。）
    #[test]
    fn carry_over_merges_without_duplicates() {
        let pending = set(&[(0, 0, 0), (10, 0, 0), (20, 0, 0)]);
        let plan = plan_cover_bake(&pending, &HashSet::new(), center, [0.0; 3], 1);
        assert_eq!(plan.bake.len(), 1);

        // 呼び出し側と同じ手順: 繰り越しを待ち集合へ戻し、そこへ再度同じチャンクを積む。
        let mut next: HashSet<ChunkCoord> = plan.deferred.iter().copied().collect();
        for &c in &plan.deferred {
            next.insert(c); // 追加変更で再度積まれた想定
        }
        next.insert(ChunkCoord::new(10, 0, 0));
        assert_eq!(next.len(), 2, "集合なので重複しない: {next:?}");

        let plan2 = plan_cover_bake(&next, &HashSet::new(), center, [0.0; 3], 8);
        assert_eq!(plan2.bake.len(), 2, "残り 2 件が焼かれる");
        let mut sorted = plan2.bake.clone();
        sorted.dedup();
        assert_eq!(sorted.len(), 2, "同一チャンクが二重に並ばない");
    }
}
