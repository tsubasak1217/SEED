// ============================================================
//  interaction/resolved.rs — インタラクションソースのワールド解決表現（Phase I1）
//
//  `InteractionSourceComponent`（データ）＋ アクタの `Transform`（位置）を
//  1 つに畳んだ、**描画側だけが見る中間表現**。
//  コンポーネント／ECS の型を描画層へ持ち込まないための境界であり、
//  水系の `water::ResolvedWaterVolume` と同じ役割・同じ立ち位置にある。
// ============================================================

/// ワールド空間へ解決済みのインタラクションソース 1 個。
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct ResolvedInteractionSource {
    /// ソースの同一性を表す安定キー（前フレーム位置の対応付けに使う）。
    ///
    /// 速度は「前フレームのワールド位置との差分」から求めるため、フレームを跨いで
    /// 「同じソース」を突き合わせる必要がある。キーは `source_key()` が
    /// アクタ DFS 連番とスロット添字から合成する。
    ///
    /// 【既知の限界】ヒエラルキーを編集してアクタの DFS 連番がずれると、
    /// そのフレームだけ別ソースの前フレーム位置と突き合わされ、瞬間的に
    /// 大きな速度が出る可能性がある。編集操作の 1 フレームだけの現象であり、
    /// 場は数秒で減衰するため実害はない（ゲーム実行中に DFS は変わらない）。
    pub key: u64,
    /// ソースのワールド座標（アクタ Transform の位置）。
    pub world_pos: [f32; 3],
    /// 影響半径（m）。
    pub radius: f32,
    /// 書き込みの強さ（0..1）。
    pub strength: f32,
}

/// アクタ DFS 連番とスロット添字から、フレームを跨いで安定なソースキーを合成する。
///
/// 1 アクタが複数の `InteractionSourceComponent` を持てる（同型コンポーネントの
/// 複数持ちは本エンジンの仕様）ため、スロット添字まで含めないと衝突する。
pub fn source_key(actor_dfs_id: u32, slot_index: u32) -> u64 {
    ((actor_dfs_id as u64) << u32::BITS) | (slot_index as u64)
}

#[cfg(test)]
mod tests {
    use super::*;

    /// キーは (アクタ, スロット) の組ごとに一意であること。
    #[test]
    fn source_key_is_unique_per_actor_and_slot() {
        assert_ne!(source_key(0, 0), source_key(0, 1));
        assert_ne!(source_key(0, 1), source_key(1, 0));
        assert_eq!(source_key(3, 7), source_key(3, 7));
    }

    /// 上位 32bit にアクタ DFS・下位 32bit にスロット添字が入ること
    /// （どちらかが欠けると別アクタ同士のキーが衝突する）。
    #[test]
    fn source_key_packs_both_fields() {
        let k = source_key(0x1234_5678, 0x9abc_def0);
        assert_eq!((k >> 32) as u32, 0x1234_5678);
        assert_eq!(k as u32, 0x9abc_def0);
    }
}
