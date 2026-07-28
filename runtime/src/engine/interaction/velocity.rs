// ============================================================
//  interaction/velocity.rs — インタラクションソースの速度算出（Phase I1）
//
//  `InteractionSourceComponent` は速度を宣言しない（＝アクタを動かすだけで場が反応する）。
//  そのため「今フレームのワールド位置」と「前フレームのワールド位置」の差分から
//  毎フレーム速度を求める必要がある。その状態保持と算出だけを担うのが本モジュール。
//
//  ## なぜ Transform に速度を持たせないのか
//  Transform はスクリプト・物理・アニメーション・ギズモが自由に書き換える共有データであり、
//  「誰がいつ動かしたか」を Transform 側で一貫して速度化することはできない。
//  一方インタラクションフィールドが欲しいのは「見た目上どれだけ動いたか」だけなので、
//  **消費側（本モジュール）が位置履歴を持つ**のが最も破綻が少ない。
//  この方式は駆動元（物理／スクリプト／アニメ／手動ドラッグ）を問わず等しく機能する。
// ============================================================

use std::collections::HashMap;

use super::resolved::ResolvedInteractionSource;

/// 速度が確定したインタラクションソース（GPU へ送る直前の CPU 表現）。
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct MovingInteractionSource {
    /// ワールド座標。
    pub world_pos: [f32; 3],
    /// ワールド XZ 速度（m/s）。場へ焼かれる値そのもの。
    ///
    /// Y 速度は瞬発場に含めない。場は俯瞰（XZ 平面）テクスチャであり、消費側
    /// （草の曲げ・水の波紋）が必要とするのは水平方向の押し出しだけのため。
    pub velocity_xz: [f32; 2],
    /// 影響半径（m）。
    pub radius: f32,
    /// 書き込みの強さ（0..1）。
    pub strength: f32,
}

/// フレーム間の位置差分からインタラクションソースの速度を求めるトラッカー。
///
/// キー（`ResolvedInteractionSource::key`）ごとに前フレームのワールド位置を保持する。
/// 今フレームに現れなかったキーは自動的に忘れる（マップが無限に育たない）。
#[derive(Default)]
pub struct InteractionSourceVelocityTracker {
    /// 前フレームのワールド位置（キー → 位置）。
    prev_positions: HashMap<u64, [f32; 3]>,
    /// `update` の作業用バッファ（毎フレームの HashMap 再確保を避けるため使い回す）。
    next_positions: HashMap<u64, [f32; 3]>,
}

/// 速度算出で使う最小 dt（秒）。
///
/// dt が 0（同一フレームで 2 回呼ぶ・計測の粒度落ち）だと 0 除算で無限大になる。
/// 1000fps 相当を下限として飽和させる。
const MIN_DELTA_SECS: f32 = 0.001;

/// 速度の上限（m/s）。
///
/// テレポート（スクリプトによる瞬間移動・シーン読み込み直後の初期配置）で
/// 巨大な速度が出ると、場に極端な値が焼かれて草が一斉に地面へ潜る。
/// 人間の全力疾走〜車両の常識的な速度に収まる値で飽和させる。
/// これを超える移動は「移動」ではなく「転送」とみなし、場には上限値だけを書く。
pub const INTERACTION_MAX_SPEED: f32 = 20.0;

impl InteractionSourceVelocityTracker {
    /// トラッカーを作る。
    pub fn new() -> Self {
        Self::default()
    }

    /// 今フレームのソース列に速度を付けて返す。
    ///
    /// - `sources`: `collect_interaction_sources` の結果（ワールド解決済み）。
    /// - `delta_secs`: 前フレームからの経過時間（壁時計。Edit 中も進む値を渡すこと）。
    ///
    /// 初出のソース（前フレーム位置が無い）は速度 0 とする。出現した瞬間に
    /// 「原点から今の位置へ飛んできた」巨大な速度が出るのを防ぐため。
    pub fn update(
        &mut self,
        sources:    &[ResolvedInteractionSource],
        delta_secs: f32,
    ) -> Vec<MovingInteractionSource> {
        let dt = delta_secs.max(MIN_DELTA_SECS);
        let inv_dt = 1.0 / dt;

        self.next_positions.clear();
        let mut out = Vec::with_capacity(sources.len());

        for s in sources {
            // 前フレーム位置が無い（初出）＝速度 0。
            let velocity_xz = match self.prev_positions.get(&s.key) {
                Some(prev) => {
                    let vx = (s.world_pos[0] - prev[0]) * inv_dt;
                    let vz = (s.world_pos[2] - prev[2]) * inv_dt;
                    clamp_speed([vx, vz], INTERACTION_MAX_SPEED)
                }
                None => [0.0, 0.0],
            };
            self.next_positions.insert(s.key, s.world_pos);
            out.push(MovingInteractionSource {
                world_pos: s.world_pos,
                velocity_xz,
                radius:    s.radius,
                strength:  s.strength,
            });
        }

        // 今フレームに現れなかったキーは忘れる（消えたアクタの位置を持ち続けない）。
        std::mem::swap(&mut self.prev_positions, &mut self.next_positions);
        out
    }

    /// 位置履歴を全消去する（シーン読み込み・Play 開始/終了などの不連続点で呼ぶ）。
    ///
    /// これを呼ばないと、シーンが入れ替わった直後に「旧シーンの位置 → 新シーンの位置」の
    /// 巨大な速度が 1 フレームだけ焼かれる。
    pub fn clear(&mut self) {
        self.prev_positions.clear();
        self.next_positions.clear();
    }
}

/// XZ 速度ベクトルの長さを `max` で飽和させる（向きは保つ）。
fn clamp_speed(v: [f32; 2], max: f32) -> [f32; 2] {
    let len_sq = v[0] * v[0] + v[1] * v[1];
    if len_sq <= max * max || len_sq <= 0.0 {
        return v;
    }
    let scale = max / len_sq.sqrt();
    [v[0] * scale, v[1] * scale]
}

// ─── ユニットテスト ──────────────────────────────────────────

#[cfg(test)]
mod tests {
    use super::*;

    /// テスト用のソースを 1 個作る。
    fn src(key: u64, pos: [f32; 3]) -> ResolvedInteractionSource {
        ResolvedInteractionSource { key, world_pos: pos, radius: 1.0, strength: 1.0 }
    }

    /// 初出フレームの速度は 0（出現時の巨大速度を防ぐ）。
    #[test]
    fn first_frame_velocity_is_zero() {
        let mut t = InteractionSourceVelocityTracker::new();
        let out = t.update(&[src(1, [10.0, 0.0, 20.0])], 1.0 / 60.0);
        assert_eq!(out[0].velocity_xz, [0.0, 0.0]);
    }

    /// 2 フレーム目は「差分 / dt」が速度になる（Y は無視される）。
    #[test]
    fn second_frame_velocity_is_delta_over_dt() {
        let mut t = InteractionSourceVelocityTracker::new();
        t.update(&[src(1, [0.0, 0.0, 0.0])], 0.5);
        let out = t.update(&[src(1, [1.0, 99.0, 2.0])], 0.5);
        assert_eq!(out[0].velocity_xz, [2.0, 4.0], "dt=0.5 なので差分の 2 倍");
    }

    /// dt=0 でも無限大にならない（下限で飽和する）。
    #[test]
    fn zero_dt_does_not_produce_infinity() {
        let mut t = InteractionSourceVelocityTracker::new();
        t.update(&[src(1, [0.0, 0.0, 0.0])], 0.0);
        let out = t.update(&[src(1, [1.0, 0.0, 0.0])], 0.0);
        assert!(out[0].velocity_xz[0].is_finite());
        // 下限 dt=0.001 → 1m/0.001s = 1000m/s だが上限 20 で飽和する。
        assert_eq!(out[0].velocity_xz[0], INTERACTION_MAX_SPEED);
    }

    /// テレポートは上限速度で飽和し、**向きは保たれる**。
    #[test]
    fn teleport_is_clamped_but_keeps_direction() {
        let mut t = InteractionSourceVelocityTracker::new();
        t.update(&[src(1, [0.0, 0.0, 0.0])], 1.0);
        let out = t.update(&[src(1, [300.0, 0.0, 400.0])], 1.0);
        let v = out[0].velocity_xz;
        let len = (v[0] * v[0] + v[1] * v[1]).sqrt();
        assert!((len - INTERACTION_MAX_SPEED).abs() < 1e-3, "長さは上限に飽和する");
        // 元ベクトル (300,400) と同じ向き（3:4）であること。
        assert!((v[0] / v[1] - 0.75).abs() < 1e-5);
    }

    /// 消えたソースのキーは忘れる（再出現時は初出扱い＝速度 0）。
    #[test]
    fn forgets_disappeared_sources() {
        let mut t = InteractionSourceVelocityTracker::new();
        t.update(&[src(1, [0.0, 0.0, 0.0])], 1.0);
        t.update(&[], 1.0); // 1 フレーム消える
        let out = t.update(&[src(1, [5.0, 0.0, 0.0])], 1.0);
        assert_eq!(out[0].velocity_xz, [0.0, 0.0], "再出現は初出扱い");
    }

    /// キーが違うソース同士は混ざらない（別アクタの速度を拾わない）。
    #[test]
    fn tracks_sources_independently() {
        let mut t = InteractionSourceVelocityTracker::new();
        t.update(&[src(1, [0.0, 0.0, 0.0]), src(2, [10.0, 0.0, 0.0])], 1.0);
        let out = t.update(&[src(1, [1.0, 0.0, 0.0]), src(2, [10.0, 0.0, 0.0])], 1.0);
        assert_eq!(out[0].velocity_xz, [1.0, 0.0]);
        assert_eq!(out[1].velocity_xz, [0.0, 0.0], "動いていないソースは速度 0");
    }

    /// clear 後は全ソースが初出扱いになる（シーン切替の不連続対策）。
    #[test]
    fn clear_resets_history() {
        let mut t = InteractionSourceVelocityTracker::new();
        t.update(&[src(1, [0.0, 0.0, 0.0])], 1.0);
        t.clear();
        let out = t.update(&[src(1, [5.0, 0.0, 0.0])], 1.0);
        assert_eq!(out[0].velocity_xz, [0.0, 0.0]);
    }

    /// 半径・強さはそのまま素通しされる（トラッカーは速度以外を触らない）。
    #[test]
    fn passes_through_radius_and_strength() {
        let mut t = InteractionSourceVelocityTracker::new();
        let s = ResolvedInteractionSource {
            key: 1, world_pos: [0.0; 3], radius: 3.5, strength: 0.25,
        };
        let out = t.update(&[s], 1.0);
        assert_eq!(out[0].radius, 3.5);
        assert_eq!(out[0].strength, 0.25);
    }
}
