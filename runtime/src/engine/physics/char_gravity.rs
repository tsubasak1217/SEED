// ============================================================
//  physics/char_gravity.rs — キネマティックキャラクターへのノーコード重力適用
//
//  【何のためのモジュールか】
//    SEED のキャラクターコントローラー（`ColliderComponent::is_character_controller`）は
//    「スクリプトが `transform.Position` に書いた希望位置を KCC が衝突解決して押し戻す」
//    という設計で、重力は含まれない。そのため「歩く」だけのスクリプトを書くと
//    キャラは空中に浮いたまま水平移動してしまい、落下させるには毎回スクリプトで
//    落下速度を積分する定型コードを書く必要があった。
//
//    本モジュールは、その定型処理をエンジン側へ移し、コンポーネントの
//    「重力を適用」チェック（`ColliderComponent::apply_gravity`）を ON にするだけで
//    落下・接地・着地リセットが効くようにする（ノーコード）。
//
//  【設計方針 — なぜ「Y オフセットの加算」なのか】
//    スクリプトが同一フレームに書いた水平移動を**絶対に上書きしない**ことが最重要要件。
//    そこで本モジュールは速度も位置も自前では持たず、「今フレームに希望位置へ加算すべき
//    下方向オフセット [m]」だけを返す。呼び出し側（physics_ops::sync_character_controllers）は
//
//        desired.y += char_gravity.step(...)
//
//    と**加算合成**してから KCC へ渡す。KCC は水平・垂直をまとめてスイープ解決するため、
//    壁ずり・段差（autostep）・斜面（slope）の扱いは既存の KCC 設定がそのまま適用される。
//    適用順は「スクリプト Update 群 → 本モジュール → KCC 解決」で、スクリプトの
//    上方向移動（ジャンプ）とも自然に合成される（同フレームの上移動が下オフセットを上回れば上へ動く）。
//
//  【接地維持のための微小プローブ】
//    接地中は落下速度を 0 にリセットするが、垂直移動量まで完全に 0 にすると
//    rapier の KCC が「下向きの接触」を検出できず grounded が明滅する（＝落下速度が
//    誤って積分され始める）。そのため接地中は速度 0 のまま、
//    `CHAR_GRAVITY_GROUND_STICK_M` ぶんの微小な下向きオフセットだけを与えて
//    床への吸着と grounded 判定を安定させる。
//
//  【状態のキー】
//    entity_id は物理系と共通の「1-indexed DFS 順カウンタ」。`CharacterWorld` が持つ
//    `char_last_pos` と同じキー体系なので、階層変更時の振る舞いも既存 KCC と一致する。
// ============================================================

use std::collections::{HashMap, HashSet};

// ─── 重力パラメータ定数 ──────────────────────────────────────────────────────
//
//  マジックナンバーを避けるため、挙動を決める値はすべてここに名前付き定数として置く。
//  重力加速度そのものは既存の物理設定（`types::DEFAULT_GRAVITY`）を流用し、ここには持たない。

/// 落下の最大速度（終端速度・m/s、絶対値）。
///
/// これ以上は速くならないようクランプする。無制限に加速すると、1 フレームの移動量が
/// キャラの厚みを超えてトンネリング（床をすり抜ける）を起こすため必ず要る。
/// 55 m/s は実際のスカイダイビング（腹ばい姿勢）の終端速度とほぼ同じで、
/// 60fps なら 1 フレーム約 0.92m ＝ 標準的なキャラの厚みを超えない範囲に収まる。
pub const CHAR_GRAVITY_TERMINAL_VELOCITY_MPS: f32 = 55.0;

/// 接地中に毎フレーム与える微小な下向きオフセット（メートル）。
///
/// grounded 判定と床への吸着を安定させるためのプローブ（モジュール冒頭の解説参照）。
/// KCC のスキン幅（1cm）より大きく、段差判定（0.3m）よりずっと小さい値にする。
pub const CHAR_GRAVITY_GROUND_STICK_M: f32 = 0.02;

/// 重力積分に使うフレーム時間の上限（秒）。
///
/// ロード直後やブレークポイント復帰など、1 フレームが極端に長くなった直後に
/// 巨大な落下量が一度に入って床を突き抜けるのを防ぐ。100ms（＝10fps 相当）で頭打ちにする。
pub const CHAR_GRAVITY_MAX_DT_S: f32 = 0.1;

// ─── 落下状態 ────────────────────────────────────────────────────────────────

/// キャラ 1 体ぶんの落下状態（フレームをまたいで保持する最小限の情報）。
#[derive(Clone, Copy, Debug, Default, PartialEq)]
pub struct CharacterFallState {
    /// 垂直方向の速度 [m/s]。下向きが負（SEED の Y 軸は上が正）。
    pub velocity: f32,
    /// 直前フレームの KCC 解決で接地していたか。
    pub grounded: bool,
}

/// 1 フレームぶんの重力積分結果。
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct GravityStep {
    /// 積分後の垂直速度 [m/s]（次フレームへ持ち越す値）。
    pub velocity: f32,
    /// 今フレームの希望位置 Y へ加算すべきオフセット [m]（通常は負＝下向き）。
    pub delta_y: f32,
}

// ─── 純粋関数（ユニットテスト対象） ──────────────────────────────────────────

/// 落下速度を 1 フレーム積分し、希望位置へ加算する下方向オフセットを求める純粋関数。
///
/// - 接地中: 速度を 0 に保ち、接地維持用の微小プローブだけを返す。
/// - 空中:   `v += gravity_y * dt` を終端速度でクランプし、`delta_y = v * dt` を返す。
///
/// `gravity_y` は重力加速度の Y 成分（通常 `DEFAULT_GRAVITY[1]` = -9.81）。
/// `dt` は実フレーム時間 [秒]（`CHAR_GRAVITY_MAX_DT_S` で上限クランプし、負値は 0 とみなす）。
pub fn integrate_fall(state: CharacterFallState, gravity_y: f32, dt: f32) -> GravityStep {
    // 異常な dt（負値・巨大値・NaN）を安全な範囲へ丸める。
    let dt = if dt.is_finite() { dt.clamp(0.0, CHAR_GRAVITY_MAX_DT_S) } else { 0.0 };

    // 接地中: 落下速度はリセットしたまま、吸着プローブぶんだけ下へ押す。
    if state.grounded {
        return GravityStep { velocity: 0.0, delta_y: -CHAR_GRAVITY_GROUND_STICK_M };
    }

    // 空中: 重力で加速し、終端速度で頭打ちにする（重力が上向き設定でも同じ大きさで制限する）。
    let velocity = (state.velocity + gravity_y * dt)
        .clamp(-CHAR_GRAVITY_TERMINAL_VELOCITY_MPS, CHAR_GRAVITY_TERMINAL_VELOCITY_MPS);

    GravityStep { velocity, delta_y: velocity * dt }
}

/// KCC 解決後の接地結果を落下状態へ反映する純粋関数。
///
/// 着地した（`grounded == true`）フレームで落下速度を 0 にリセットする。
/// 空中のままなら積分後の速度をそのまま次フレームへ持ち越す。
pub fn settle_after_resolve(step: GravityStep, grounded: bool) -> CharacterFallState {
    CharacterFallState {
        velocity: if grounded { 0.0 } else { step.velocity },
        grounded,
    }
}

// ─── キャラ横断の状態保持 ────────────────────────────────────────────────────

/// 「重力を適用」が ON のキャラクター全体の落下状態を保持するコンテナ。
///
/// `App` が物理起動中のみ保持し、毎フレーム `step`（KCC 解決前）→ `settle`（解決後）の
/// 順に呼ぶ。対象から外れたキャラの状態は `retain` で掃除する。
#[derive(Debug, Default)]
pub struct CharacterGravity {
    /// entity_id（1-indexed DFS）→ 落下状態。
    states: HashMap<u64, CharacterFallState>,
}

impl CharacterGravity {
    /// 空の状態で生成する。
    pub fn new() -> Self {
        Self { states: HashMap::new() }
    }

    /// 1 フレームぶん落下を積分し、希望位置 Y へ加算すべきオフセット [m] を返す。
    ///
    /// KCC 解決の**前**に呼ぶこと。解決後は必ず `settle` を呼んで接地結果を反映する。
    pub fn step(&mut self, entity_id: u64, gravity_y: f32, dt: f32) -> f32 {
        let prev = self.states.get(&entity_id).copied().unwrap_or_default();
        let step = integrate_fall(prev, gravity_y, dt);
        // 接地フラグは解決後（settle）に確定するため、ここでは速度だけ進める。
        self.states.insert(entity_id, CharacterFallState {
            velocity: step.velocity,
            grounded: prev.grounded,
        });
        step.delta_y
    }

    /// KCC 解決後の接地結果を反映する（着地で落下速度を 0 にリセット）。
    pub fn settle(&mut self, entity_id: u64, grounded: bool) {
        let entry = self.states.entry(entity_id).or_default();
        entry.grounded = grounded;
        if grounded {
            entry.velocity = 0.0;
        }
    }

    /// 1 体ぶんの落下状態を初期化する（テレポート・リスポーン用）。
    ///
    /// 瞬間移動の直後に落下速度を持ち越すと、移動先で即座に高速落下してしまう。
    /// `Transform.Teleport` で解決基準（`char_last_pos`）を作り直すのと同じタイミングで呼ぶ。
    pub fn reset(&mut self, entity_id: u64) {
        self.states.remove(&entity_id);
    }

    /// 「重力を適用」が ON の現存キャラ以外の状態を破棄する（削除・チェック OFF の後始末）。
    pub fn retain(&mut self, live_ids: &HashSet<u64>) {
        self.states.retain(|id, _| live_ids.contains(id));
    }

    /// 保持中の落下状態を取得する（診断・テスト用）。
    pub fn state_of(&self, entity_id: u64) -> Option<CharacterFallState> {
        self.states.get(&entity_id).copied()
    }

    /// 保持件数（診断・テスト用）。
    pub fn len(&self) -> usize {
        self.states.len()
    }

    /// 状態を保持していないか（診断・テスト用）。
    pub fn is_empty(&self) -> bool {
        self.states.is_empty()
    }
}

// ─── ユニットテスト ──────────────────────────────────────────────────────────

#[cfg(test)]
mod tests {
    use super::*;

    /// テストで使う重力加速度（既存の物理設定と同じ値）。
    const G: f32 = crate::engine::physics::types::DEFAULT_GRAVITY[1];

    /// 60fps 相当のフレーム時間。
    const DT: f32 = 1.0 / 60.0;

    #[test]
    fn accelerates_while_airborne() {
        let s = CharacterFallState { velocity: 0.0, grounded: false };
        let step = integrate_fall(s, G, DT);
        // v = 0 + (-9.81) * (1/60)
        assert!((step.velocity - G * DT).abs() < 1e-6, "velocity={}", step.velocity);
        // delta_y = v * dt（下向き＝負）
        assert!(step.delta_y < 0.0);
        assert!((step.delta_y - G * DT * DT).abs() < 1e-6);
    }

    #[test]
    fn airborne_acceleration_accumulates() {
        let mut s = CharacterFallState::default(); // grounded=false, velocity=0
        for _ in 0..3 {
            let step = integrate_fall(s, G, DT);
            s = settle_after_resolve(step, false);
        }
        // 3 フレームぶん加速している
        assert!((s.velocity - G * DT * 3.0).abs() < 1e-5, "velocity={}", s.velocity);
    }

    #[test]
    fn grounded_keeps_zero_velocity_with_stick_probe() {
        let s = CharacterFallState { velocity: -20.0, grounded: true };
        let step = integrate_fall(s, G, DT);
        assert_eq!(step.velocity, 0.0);
        assert_eq!(step.delta_y, -CHAR_GRAVITY_GROUND_STICK_M);
    }

    #[test]
    fn landing_resets_fall_velocity() {
        let s = CharacterFallState { velocity: -30.0, grounded: false };
        let step = integrate_fall(s, G, DT);
        assert!(step.velocity < -30.0); // まだ空中なので加速している
        let landed = settle_after_resolve(step, true);
        assert_eq!(landed.velocity, 0.0);
        assert!(landed.grounded);
    }

    #[test]
    fn never_exceeds_terminal_velocity() {
        let mut s = CharacterFallState::default();
        // 10 秒ぶん（600 フレーム）落下させても終端速度で頭打ちになる
        for _ in 0..600 {
            let step = integrate_fall(s, G, DT);
            s = settle_after_resolve(step, false);
        }
        assert!(s.velocity >= -CHAR_GRAVITY_TERMINAL_VELOCITY_MPS,
            "terminal velocity を超えた: {}", s.velocity);
        assert!((s.velocity + CHAR_GRAVITY_TERMINAL_VELOCITY_MPS).abs() < 1e-3,
            "終端速度に到達していない: {}", s.velocity);
    }

    #[test]
    fn abnormal_dt_is_clamped() {
        let s = CharacterFallState::default();
        // 巨大 dt（5 秒）でも上限 0.1 秒ぶんまでしか進まない
        let big = integrate_fall(s, G, 5.0);
        let capped = integrate_fall(s, G, CHAR_GRAVITY_MAX_DT_S);
        assert_eq!(big, capped);
        // 負の dt / NaN では移動しない
        assert_eq!(integrate_fall(s, G, -1.0).delta_y, 0.0);
        assert_eq!(integrate_fall(s, G, f32::NAN).delta_y, 0.0);
    }

    #[test]
    fn step_returns_only_vertical_offset() {
        // 「加算合成」の契約: step が返すのは Y オフセット 1 つだけで、
        // 呼び出し側が希望位置へ足すことでスクリプトの水平移動と共存する。
        let mut g = CharacterGravity::new();
        let script_move = [1.5f32, 0.0, -2.5f32]; // スクリプトが同フレームに書いた水平移動
        let mut desired = [10.0f32, 5.0f32, 3.0f32];
        desired[0] += script_move[0];
        desired[2] += script_move[2];

        let dy = g.step(1, G, DT);
        desired[1] += dy;

        // 水平成分はスクリプトの値のまま（重力が上書きしていない）
        assert_eq!(desired[0], 11.5);
        assert_eq!(desired[2], 0.5);
        // 垂直成分だけが下へずれている
        assert!(desired[1] < 5.0);
    }

    #[test]
    fn upward_script_move_composes_over_gravity() {
        // ジャンプ相当: 接地中フレームでスクリプトが +0.3m 上へ動かすと、
        // 重力の接地プローブ（-0.02m）を打ち消して正味は上向きになる。
        let mut g = CharacterGravity::new();
        g.settle(1, true); // 接地状態
        let dy = g.step(1, G, DT);
        assert_eq!(dy, -CHAR_GRAVITY_GROUND_STICK_M);
        let net = 0.3 + dy;
        assert!(net > 0.0, "ジャンプが重力に打ち消された: {net}");
    }

    #[test]
    fn reset_clears_accumulated_fall_velocity() {
        // テレポート想定: 落下速度が溜まった状態で reset すると静止状態に戻る。
        let mut g = CharacterGravity::new();
        for _ in 0..60 { g.step(1, G, DT); }
        assert!(g.state_of(1).unwrap().velocity < -9.0, "落下速度が溜まっていない");

        g.reset(1);
        assert!(g.state_of(1).is_none());
        // reset 後の最初の 1 フレームは初速 0 から積分し直す
        let dy = g.step(1, G, DT);
        assert!((dy - G * DT * DT).abs() < 1e-6, "初速が 0 に戻っていない: {dy}");
    }

    #[test]
    fn per_character_state_is_independent_and_prunable() {
        let mut g = CharacterGravity::new();
        g.step(1, G, DT);
        g.step(2, G, DT);
        g.settle(2, true);
        assert_eq!(g.len(), 2);
        assert_eq!(g.state_of(2).unwrap().velocity, 0.0);
        assert!(g.state_of(1).unwrap().velocity < 0.0);

        // 対象から外れた 1 を掃除する
        let live: HashSet<u64> = [2u64].into_iter().collect();
        g.retain(&live);
        assert_eq!(g.len(), 1);
        assert!(g.state_of(1).is_none());
    }
}
