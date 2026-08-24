// ============================================================
//  terrain/cover/bake_wave.rs — カバー頂点焼き直しの「波（スナップショット付き作業単位）」（純粋層）
//
//  【解く問題】
//    カバー場の焼き込みは「ワールド位置とカバー場の純関数」である。チャンク境界の
//    複製頂点は隣接チャンクのカバー場も読む（`CoverNeighborhood` ／ 26 近傍）ため、
//    **両側のメッシュが同じ世代のカバー場を読んでいる**あいだだけ境界が一致する。
//
//    旧実装はこの制約を「26 近傍で連結した待ちチャンクの塊を分割しない」という
//    スケジューリング規則で守っていた。しかし全域降雪では待ちチャンクが 1 つの
//    巨大な連結成分になるため、1 フレームあたりのチャンク予算がまったく効かず、
//    積算ティックのたびに全チャンクを 1 フレームで焼く羽目になっていた。
//
//  【この設計】
//    「同じ世代を読ませる」ための手段を、スケジューリング規則から**データの凍結**へ
//    移す。焼き直しを始めるときに、対象チャンクとその 26 近傍のカバー場を丸ごと
//    クローンして凍結（スナップショット）し、その波に属する全チャンクは何フレームに
//    分かれて焼かれても必ずこの凍結データだけを読む。
//
//    こうすると「隣り合うチャンクを別フレームに焼くと段差が出る」という制約自体が
//    消えるので、予算は素直にチャンク単位で効く（＝全域降雪でもフレーム負荷が
//    予算で頭打ちになる）。連結成分の分解は不要になり、`bake_schedule.rs` は
//    優先度つきの単純な件数予算へ簡約された。
//
//  【波をまたいだ境界整合】
//    波 A で焼いたチャンク Z と、次の波 B で焼く隣接チャンク Y のあいだも一致する。
//    呼び出し側（`queue_cover_apply`）が「変化したチャンクとその 26 近傍」を必ず
//    セットで待ち集合へ積むためである。境界頂点が読むカバー場のどれか W が波 A→B の
//    あいだに変化したなら、W の 26 近傍である Z も B の対象に入るので、両側とも
//    同じ凍結データ（B）で焼き直される。
//
//  【スナップショットの費用】
//    カバー場 1 チャンクは 32×32 テクセル ×（素材 1B ＋ 量 1B ＋ 踏み固め 1B ＋ 基準 Y 4B）
//    ＝ 約 7KB。既定の地面 48 チャンクを全部凍結しても 350KB 程度であり、
//    積算間隔（既定 0.25 秒）ごとの memcpy として無視できる。
//
//  【波の中断】
//    ・轍スタンプが来た      … 応答性最優先なので波を捨てて張り直す（呼び出し側の判断）。
//    ・地形が再メッシュされた … そのチャンクと 26 近傍を波から追い出して待ちへ戻す
//                              （基準メッシュが作り直されたため凍結データの前提が崩れる）。
//    どちらも「追い出したチャンクを待ち集合へ戻す」ことで取りこぼしを防ぐ。
// ============================================================

use std::collections::{HashMap, HashSet};

use crate::engine::terrain::chunk_coord::ChunkCoord;
use crate::engine::terrain::cover::field::CoverField;

/// 焼き直し 1 波ぶんの作業単位（凍結カバー場 ＋ 未処理チャンク）。
///
/// 空（`is_active() == false`）のときは何も保持しない＝メモリを占めない。
#[derive(Debug, Default)]
pub struct CoverBakeWave {
    /// 凍結したカバー場。キーは「対象チャンク ∪ その 26 近傍」。
    ///
    /// 焼き込みはここだけを読む。実データ（`TerrainState::cover`）は
    /// 波の進行中も積算で書き換わり続けるが、それは次の波が拾う。
    snapshot: HashMap<ChunkCoord, CoverField>,
    /// この波でまだ焼いていないチャンク。
    remaining: HashSet<ChunkCoord>,
    /// そのうち轍スタンプ由来（予算を無視して先に焼く）チャンク。
    immediate: HashSet<ChunkCoord>,
}

impl CoverBakeWave {
    /// 進行中の波があるか（＝まだ焼くチャンクが残っているか）。
    pub fn is_active(&self) -> bool {
        !self.remaining.is_empty()
    }

    /// 新しい波を張る（既存の波は破棄される。呼び出し側が先に `clear` で回収すること）。
    ///
    /// - `targets`   … この波で焼くチャンク集合。
    /// - `immediate` … そのうち轍スタンプ由来のもの（`targets` 外の要素は無視される）。
    /// - `snapshot`  … 凍結したカバー場。**`targets` の 26 近傍まで含めること**
    ///                 （境界頂点が隣のカバー場を読むため。欠けていると「量 0」に
    ///                 縮退して段差が出る）。
    pub fn start(
        &mut self,
        targets: HashSet<ChunkCoord>,
        immediate: HashSet<ChunkCoord>,
        snapshot: HashMap<ChunkCoord, CoverField>,
    ) {
        self.immediate = immediate.into_iter().filter(|c| targets.contains(c)).collect();
        self.remaining = targets;
        self.snapshot = snapshot;
    }

    /// この波でまだ焼いていないチャンク集合。
    pub fn remaining(&self) -> &HashSet<ChunkCoord> {
        &self.remaining
    }

    /// この波の轍スタンプ由来チャンク集合。
    pub fn immediate(&self) -> &HashSet<ChunkCoord> {
        &self.immediate
    }

    /// 凍結カバー場の参照（焼き込みはここだけを読む）。
    pub fn field(&self, coord: ChunkCoord) -> Option<&CoverField> {
        self.snapshot.get(&coord)
    }

    /// 凍結カバー場にこのチャンクの器があるか（「焼く価値があるか」判定に使う）。
    pub fn has_field(&self, coord: ChunkCoord) -> bool {
        self.snapshot.contains_key(&coord)
    }

    /// 1 チャンクを焼き終えたことを記録する。最後の 1 件なら凍結データを解放する。
    pub fn mark_baked(&mut self, coord: ChunkCoord) {
        self.remaining.remove(&coord);
        self.immediate.remove(&coord);
        if self.remaining.is_empty() {
            self.release();
        }
    }

    /// 波を丸ごと捨て、`(未処理チャンク, そのうち轍由来)` を返す。
    ///
    /// 呼び出し側はどちらも待ち集合／即時集合へ戻すこと（取りこぼしと優先度喪失の防止）。
    pub fn clear(&mut self) -> (Vec<ChunkCoord>, Vec<ChunkCoord>) {
        let rest: Vec<ChunkCoord> = self.remaining.drain().collect();
        let urgent: Vec<ChunkCoord> = self.immediate.drain().collect();
        self.release();
        (rest, urgent)
    }

    /// 指定チャンクとその 26 近傍を波から追い出し、追い出した未処理チャンクを返す。
    ///
    /// 地形が再メッシュされたチャンク向け。基準メッシュ（`cover_base_mesh`）が
    /// 作り直されると、凍結カバー場の基準 Y（面のワールド高さ）が実メッシュと
    /// 食い違いうる。当該チャンクだけでなく 26 近傍まで追い出すのは、
    /// 境界頂点がそのカバー場を読むためである。
    pub fn evict_neighborhood(&mut self, coord: ChunkCoord) -> Vec<ChunkCoord> {
        let mut evicted = Vec::new();
        for dy in -1..=1i32 {
            for dz in -1..=1i32 {
                for dx in -1..=1i32 {
                    let n = ChunkCoord::new(coord.x + dx, coord.y + dy, coord.z + dz);
                    if self.remaining.remove(&n) {
                        self.immediate.remove(&n);
                        evicted.push(n);
                    }
                    self.snapshot.remove(&n);
                }
            }
        }
        if self.remaining.is_empty() {
            self.release();
        }
        evicted
    }

    /// 凍結データを解放して波を空へ戻す（確保済み容量も返す）。
    fn release(&mut self) {
        self.snapshot.clear();
        self.snapshot.shrink_to_fit();
        self.immediate.clear();
        self.remaining.clear();
    }
}

// ─── テスト ──────────────────────────────────────────────────────────────────

#[cfg(test)]
mod tests {
    use super::*;

    fn coords(list: &[(i32, i32, i32)]) -> HashSet<ChunkCoord> {
        list.iter().map(|&(x, y, z)| ChunkCoord::new(x, y, z)).collect()
    }

    /// 凍結したカバー場は、あとから実データを書き換えても変わらないこと
    /// （境界整合の根拠そのもの）。
    #[test]
    fn snapshot_is_independent_from_live_field() {
        let c = ChunkCoord::new(0, 0, 0);
        let mut live = CoverField::new();
        live.deposit(0, 0, 1, 0.5);
        let mut wave = CoverBakeWave::default();
        let mut snap = HashMap::new();
        snap.insert(c, live.clone());
        wave.start(coords(&[(0, 0, 0)]), HashSet::new(), snap);

        // 実データをさらに積む（次の波が拾うぶん）。
        live.deposit(0, 0, 1, 0.5);

        let frozen = wave.field(c).expect("凍結データがある");
        assert!(
            (frozen.amount_at(0, 0) - 0.5).abs() <= 2.0 / 255.0,
            "凍結後の実データ変更は波へ波及しない（frozen={}）",
            frozen.amount_at(0, 0),
        );
        assert!(live.amount_at(0, 0) > 0.9, "実データ側だけが増える");
    }

    /// 焼き終えた最後の 1 件で凍結データが解放されること（メモリを持ち越さない）。
    #[test]
    fn snapshot_is_released_when_wave_completes() {
        let mut wave = CoverBakeWave::default();
        let mut snap = HashMap::new();
        snap.insert(ChunkCoord::new(0, 0, 0), CoverField::new());
        wave.start(coords(&[(0, 0, 0)]), HashSet::new(), snap);
        assert!(wave.is_active());
        wave.mark_baked(ChunkCoord::new(0, 0, 0));
        assert!(!wave.is_active());
        assert!(
            wave.field(ChunkCoord::new(0, 0, 0)).is_none(),
            "凍結データは解放される",
        );
    }

    /// 波を捨てたら未処理チャンクと轍マークが返り、取りこぼしが無いこと。
    #[test]
    fn clear_returns_unbaked_chunks() {
        let mut wave = CoverBakeWave::default();
        wave.start(
            coords(&[(0, 0, 0), (1, 0, 0), (2, 0, 0)]),
            coords(&[(1, 0, 0)]),
            HashMap::new(),
        );
        wave.mark_baked(ChunkCoord::new(0, 0, 0));
        let (mut rest, urgent) = wave.clear();
        rest.sort_by_key(|c| (c.x, c.y, c.z));
        assert_eq!(rest, vec![ChunkCoord::new(1, 0, 0), ChunkCoord::new(2, 0, 0)]);
        assert_eq!(urgent, vec![ChunkCoord::new(1, 0, 0)]);
        assert!(!wave.is_active());
    }

    /// `targets` に含まれない即時マークは無視されること（不整合な入力への防御）。
    #[test]
    fn immediate_outside_targets_is_ignored() {
        let mut wave = CoverBakeWave::default();
        wave.start(coords(&[(0, 0, 0)]), coords(&[(9, 9, 9)]), HashMap::new());
        assert!(wave.immediate().is_empty());
    }

    /// 再メッシュされたチャンクは 26 近傍ごと波から追い出されること。
    #[test]
    fn evict_neighborhood_removes_adjacent_chunks() {
        let mut wave = CoverBakeWave::default();
        wave.start(
            // (1,1,1) は (0,0,0) の 26 近傍。(5,0,0) は無関係。
            coords(&[(0, 0, 0), (1, 1, 1), (5, 0, 0)]),
            HashSet::new(),
            HashMap::new(),
        );
        let mut evicted = wave.evict_neighborhood(ChunkCoord::new(0, 0, 0));
        evicted.sort_by_key(|c| (c.x, c.y, c.z));
        assert_eq!(evicted, vec![ChunkCoord::new(0, 0, 0), ChunkCoord::new(1, 1, 1)]);
        assert_eq!(wave.remaining().len(), 1, "無関係なチャンクは残る");
        assert!(wave.remaining().contains(&ChunkCoord::new(5, 0, 0)));
    }
}
