// ============================================================
//  camera_orbit.rs — デバッグカメラの Blender 風オービット回転
//
//  【何をするモジュールか】
//  ビューポートで**ホイール（中）ボタンと右ボタンを同時押し**したとき、
//  カーソル下の表面（メッシュ／水面 = GPU ID ヒット、地形 = CPU レイマーチ）を
//  **ピボット**として、カメラをその点まわりの球面上で回す。
//  ピボットは画面上でほぼ固定に見え、ピボットとの距離は保たれる。
//
//  【なぜ状態機械が要るのか】
//  「同時押し」は 2 つの独立したボタンイベントで成立するので、
//    ・どちらが先でも成立させる（順不同）
//    ・ピボットが決まるまでの数フレームは既存操作で動かす
//    ・当たらなかったらオービットに入らず既存操作へ倒す
//    ・片方を離したら残った方の通常操作へ**シームレスに**戻す
//  という 4 つを同時に満たす必要がある。素朴なフラグでは表現できないので、
//  下の `OrbitPhase` を唯一の真実として持つ。
//
//  【なぜ「後から押されたボタンの操作」へ倒すのか】
//  ユーザーが最後に取った行動が、その時点での意図に一番近いからである。
//  中→右 の順に押したなら「回したい」、右→中 なら「動かしたい」。
//  ヒットが無い（空を掴んだ）ときはオービットの中心が定義できないので、
//  この意図をそのまま既存操作として実行する。
//
//  【ピボット解決の非同期性】
//  地形は CPU レイマーチなので**同時押しの瞬間に同期で**解ける（即オービット開始）。
//  メッシュ／水面は ID バッファの GPU 読み戻しが要るので 1 フレーム遅れる。
//  その間は `Pending` として後押しボタンの操作を続け、結果が届いた時点で
//  `Active`（ヒットあり）か `Fallback`（ヒット無し）へ確定する。
//  `Pending` → `Active` の切替でカメラは**一切動かない**（ピボットは以降の
//  回転にしか使われず、カメラの位置・向きを書き換えないため）ので跳ねない。
//
//  【既存操作との排他】
//  Play 中の埋め込みビュー・モーダルトランスフォーム中・ロジック配置モード中・
//  2D ビュー（正射カメラ）では発動しない。既存の視点回転が 2D 正射で無効なので、
//  オービットも同じ条件で止めて操作規則を 1 本に保つ。
// ============================================================

use winit::event::MouseButton;

use crate::engine::structs::objects::camera::debug_camera::DEBUG_CAMERA_PITCH_LIMIT;
use crate::engine::structs::tensor::Vector3;
use crate::engine::structs::transforms::Quaternion;

use super::{App, RuntimeMode};

// ─── 状態 ─────────────────────────────────────────────────────

/// 同時押しが成立したときに「後から押されたボタン」が持つ通常操作。
///
/// ピボットが取れなかった場合・取れるまでの間は、この操作を適用する。
#[derive(Copy, Clone, Debug, PartialEq, Eq)]
pub(super) enum OrbitFallback {
    /// 中ボタンが後 → 平行移動（パン）。
    Pan,
    /// 右ボタンが後 → 視点回転（フライのルック）。
    Look,
}

impl OrbitFallback {
    /// 押されたボタンから、そのボタンが本来担う操作を決める。
    #[inline]
    pub(super) fn from_button(button: MouseButton) -> Option<Self> {
        match button {
            MouseButton::Middle => Some(Self::Pan),
            MouseButton::Right => Some(Self::Look),
            _ => None,
        }
    }
}

/// オービットの状態。App が 1 つだけ持つ。
#[derive(Copy, Clone, Debug, PartialEq)]
pub(super) enum OrbitPhase {
    /// 同時押しが成立していない（＝既存操作がそのまま効く）。
    Idle,
    /// 同時押しは成立したが、ピボットがまだ決まっていない。
    ///
    /// `screen` は**成立した瞬間の**カーソル座標で、以降カーソルが動いても変えない
    /// （動かすと読み戻しのたびにピボットが飛び、オービットの中心が定まらない）。
    Pending {
        screen: (u32, u32),
        fallback: OrbitFallback,
    },
    /// オービット中。`pivot` はワールド座標。
    Active { pivot: [f32; 3] },
    /// ピボットが取れなかった。どちらかのボタンを離すまで `fallback` を適用する。
    Fallback { fallback: OrbitFallback },
}

impl Default for OrbitPhase {
    fn default() -> Self {
        Self::Idle
    }
}

impl OrbitPhase {
    /// オービット中ならピボットを返す。
    #[inline]
    pub(super) fn active_pivot(self) -> Option<[f32; 3]> {
        match self {
            Self::Active { pivot } => Some(pivot),
            _ => None,
        }
    }

    /// 後押しボタンの操作を適用すべき状態なら、その操作を返す。
    #[inline]
    pub(super) fn pending_fallback(self) -> Option<OrbitFallback> {
        match self {
            Self::Pending { fallback, .. } | Self::Fallback { fallback } => Some(fallback),
            _ => None,
        }
    }
}

// ─── 純関数（状態遷移）───────────────────────────────────────

/// マウスボタンの押下／解放を受けて次の状態を決める。
///
/// # 引数
/// - `phase`:     現在の状態
/// - `button`:    今回イベントが起きたボタン（中／右以外は無視）
/// - `pressed`:   押下 = true / 解放 = false
/// - `both_held`: **このイベントを反映した後**に中・右の両方が押されているか
/// - `cursor`:    現在のカーソル座標（ビューポートローカル・ピクセル）
///
/// # 規則
/// - 解放は常に終了（`Idle`）。残った方のボタンの通常操作は、毎フレームの
///   `cam_input` を見る既存経路がそのまま引き継ぐのでシームレスに移行する。
/// - 押下で両方が揃ったら `Pending`（＝ピボット解決待ち）。押されたばかりの
///   ボタンの操作を fallback に持つ。カーソル座標が無い（ウィンドウ外など）
///   ときはピボットを求めようがないので即 `Fallback` にする。
/// - それ以外は状態を変えない。
pub(super) fn orbit_on_button(
    phase: OrbitPhase,
    button: MouseButton,
    pressed: bool,
    both_held: bool,
    cursor: Option<(u32, u32)>,
) -> OrbitPhase {
    let Some(fallback) = OrbitFallback::from_button(button) else {
        return phase;
    };
    if !pressed {
        // どちらかを離した時点でオービットは終わる。
        return OrbitPhase::Idle;
    }
    if !both_held {
        // まだ片方だけ。通常操作のまま。
        return OrbitPhase::Idle;
    }
    match cursor {
        Some(screen) => OrbitPhase::Pending { screen, fallback },
        None => OrbitPhase::Fallback { fallback },
    }
}

/// ピボット解決の結果（GPU ヒット or 地形ヒット）を受けて次の状態を決める。
///
/// `Pending` 以外では何もしない（解決の取りこぼしが `Active` を壊さないため）。
pub(super) fn orbit_on_hit(phase: OrbitPhase, hit: Option<[f32; 3]>) -> OrbitPhase {
    let OrbitPhase::Pending { fallback, .. } = phase else {
        return phase;
    };
    match hit {
        Some(pivot) => OrbitPhase::Active { pivot },
        None => OrbitPhase::Fallback { fallback },
    }
}

// ─── 純関数（回転の数式）─────────────────────────────────────

/// yaw / pitch からデバッグカメラの姿勢クォータニオンを作る。
///
/// `DebugCamera::update_rotation` と**同じ合成順** `R = Ry(yaw) * Rx(pitch)` を使う。
/// ここがずれるとオービット後に視点回転へ戻った瞬間にカメラが跳ねる。
#[inline]
pub(super) fn debug_camera_rotation(yaw: f32, pitch: f32) -> Quaternion {
    let yaw_q = Quaternion::from_axis_angle(Vector3::new(0.0, 1.0, 0.0), yaw);
    let pitch_q = Quaternion::from_axis_angle(Vector3::new(1.0, 0.0, 0.0), pitch);
    yaw_q * pitch_q
}

/// 視点回転（フライのルック）と同じ yaw / pitch の更新。
///
/// オービットの fallback（`Look`）と、オービット本体の角度計算の両方で使う。
/// ピッチは既存と同じ範囲へクランプする。
#[inline]
pub(super) fn apply_look_delta(
    yaw: f32,
    pitch: f32,
    dx: f32,
    dy: f32,
    sensitivity: f32,
) -> (f32, f32) {
    let new_yaw = yaw + dx * sensitivity;
    let new_pitch =
        (pitch + dy * sensitivity).clamp(-DEBUG_CAMERA_PITCH_LIMIT, DEBUG_CAMERA_PITCH_LIMIT);
    (new_yaw, new_pitch)
}

/// ピボットまわりにカメラを回す。
///
/// # 数式（適用順）
/// 1. マウスデルタから新しい yaw / pitch を作る（`apply_look_delta`。ピッチはクランプ）。
/// 2. 旧姿勢 `R_old = Ry(yaw) Rx(pitch)`、新姿勢 `R_new = Ry(yaw') Rx(pitch')` を作る。
/// 3. **差分回転** `ΔR = R_new * R_old⁻¹` を求める。
/// 4. カメラ位置を `pivot + ΔR * (position − pivot)` へ移す。
///
/// ビュー方向（＝ R）とピボットからのオフセットに**同一の ΔR** を掛けるので、
/// - ΔR は回転（直交変換）なので **|position − pivot| は不変**（距離維持）
/// - ピボットの視線に対する相対方向も不変 → **画面上でほぼ固定**に見える
/// が同時に成り立つ。ピッチがクランプに当たったフレームは ΔR が小さくなるだけで、
/// 位置と向きは常に同じ ΔR で動くため、ずれ（ピボット漂流）は起きない。
///
/// # 戻り値
/// `(新しい位置, 新しい yaw, 新しい pitch)`
pub(super) fn orbit_rotate(
    pivot: [f32; 3],
    position: [f32; 3],
    yaw: f32,
    pitch: f32,
    dx: f32,
    dy: f32,
    sensitivity: f32,
) -> ([f32; 3], f32, f32) {
    let (new_yaw, new_pitch) = apply_look_delta(yaw, pitch, dx, dy, sensitivity);
    let r_old = debug_camera_rotation(yaw, pitch);
    let r_new = debug_camera_rotation(new_yaw, new_pitch);
    let delta = r_new * r_old.inverse();

    let offset = Vector3::new(
        position[0] - pivot[0],
        position[1] - pivot[1],
        position[2] - pivot[2],
    );
    let rotated = delta.rotate(offset);
    (
        [
            pivot[0] + rotated.x,
            pivot[1] + rotated.y,
            pivot[2] + rotated.z,
        ],
        new_yaw,
        new_pitch,
    )
}

// ─── App への統合 ─────────────────────────────────────────────

impl App {
    /// いまオービットを許してよい状況か。
    ///
    /// 既存の排他規則に合わせる:
    /// - Edit／ポーズ中のみ（Play 中の埋め込みビューでは発動しない）
    /// - ロジック配置モード中・モーダルトランスフォーム中は不可
    /// - 2D ビュー（アクター編集タブ／2D シーンビュー）と正射カメラでは不可
    ///   （既存の視点回転が正射で無効なので、それに揃える）
    pub(super) fn orbit_allowed(&self) -> bool {
        (self.mode == RuntimeMode::Edit || self.paused)
            && !self.placement_mode_active()
            && !self.modal_transform_active()
            && !self.edit_view_is_2d()
            && !self.actor_edit_canvas_wls.contains(&self.active_world_line)
            && !self.camera.is_ortho()
    }

    /// 中／右ボタンの押下・解放をオービット状態機械へ流す。
    ///
    /// `cam_input.mmb` / `cam_input.rmb` を**更新した後**に呼ぶこと
    /// （`both_held` の判定にそのままの値を使うため）。
    ///
    /// 同時押しが成立したら、その場で**地形の CPU レイマーチだけ**同期で試す。
    /// 当たればフレームを待たずにオービットへ入れる（メッシュは GPU 読み戻しが
    /// 要るので `Pending` のまま次フレームへ持ち越す）。
    pub(super) fn update_orbit_on_button(&mut self, button: MouseButton, pressed: bool) {
        if !self.orbit_allowed() {
            self.orbit = OrbitPhase::Idle;
            return;
        }
        let both_held = self.cam_input.mmb && self.cam_input.rmb;
        let cursor = self
            .last_cursor_pos
            .map(|(x, y)| (x.max(0.0) as u32, y.max(0.0) as u32));
        self.orbit = orbit_on_button(self.orbit, button, pressed, both_held, cursor);

        // 地形は同期に解けるので、ここで当たれば即オービット開始。
        if let OrbitPhase::Pending { screen, .. } = self.orbit {
            if let Some(hit) = self.terrain_raymarch_hit(screen.0 as f32, screen.1 as f32) {
                self.orbit = OrbitPhase::Active { pivot: hit };
            }
        }
    }

    /// このフレームで ID バッファの読み戻しを要求する座標（＝ピボット解決待ちのとき）。
    #[inline]
    pub(super) fn orbit_needs_id_readback(&self) -> Option<(u32, u32)> {
        match self.orbit {
            OrbitPhase::Pending { screen, .. } => Some(screen),
            _ => None,
        }
    }

    /// GPU 読み戻しの結果を受け取ってピボットを確定する。
    ///
    /// メッシュ／水面（GPU ID ヒット）と地形（CPU レイマーチ）の**カメラに近い方**を
    /// 採る。地形は ID パスに描かれないため GPU ヒットだけでは取りこぼす一方、
    /// 地形の手前にメッシュがあれば地形ではなくメッシュを掴みたいので、
    /// 配置モード（`resolve_surface_or_camera_dist`）と同じ合成規則にする。
    ///
    /// 配置モードと違い「カメラから一定距離」のフォールバックは**使わない**。
    /// 何にも当たっていないのに空中の点を中心に回すと、掴んだ場所と回転中心が
    /// 一致せず操作が予測できなくなるため、その場合はオービットに入らない。
    pub(super) fn resolve_orbit_hit(&mut self, gpu_hit: Option<[f32; 3]>) {
        let OrbitPhase::Pending { screen, .. } = self.orbit else {
            return;
        };
        let terrain_hit = self.terrain_raymarch_hit(screen.0 as f32, screen.1 as f32);
        let cam = self.camera.position();
        let hit = super::control_point_ops::nearer_hit(
            [cam.x, cam.y, cam.z],
            gpu_hit,
            terrain_hit,
        );
        self.orbit = orbit_on_hit(self.orbit, hit);
    }

    /// 毎フレーム、デバッグカメラ更新の**直前**に呼ぶ。
    ///
    /// オービット中は自前でカメラを回し、消費したマウスデルタを 0 に落とす。
    /// こうすると後段の `DebugCamera::update` の視点回転・MMB パンが
    /// 「動いていないフレーム」として素通りするので、二重適用が起きない。
    /// WASDQE 移動とホイールのドリーはそのまま効く（既存挙動を殺さない）。
    pub(super) fn tick_camera_orbit(&mut self) {
        if !self.orbit_allowed() {
            self.orbit = OrbitPhase::Idle;
            return;
        }
        // 安全網: 両ボタンが押されていなければ何があっても終了させる。
        // ボタンの解放イベントは `on_mouse_button` が拾って `Idle` にするが、
        // Alt+Tab でのフォーカス喪失・2D ビューへの切替中の解放など、
        // そのイベントがこのウィンドウへ届かない経路が存在する。
        // 取りこぼすとボタンを離した後もカメラが回り続けてしまう。
        if !(self.cam_input.mmb && self.cam_input.rmb) {
            self.orbit = OrbitPhase::Idle;
            return;
        }

        if let Some(pivot) = self.orbit.active_pivot() {
            let (dx, dy) = (self.cam_input.mouse_dx, self.cam_input.mouse_dy);
            if dx != 0.0 || dy != 0.0 {
                let p = self.camera.base.transform.position;
                let (pos, yaw, pitch) = orbit_rotate(
                    pivot,
                    [p.x, p.y, p.z],
                    self.camera.yaw,
                    self.camera.pitch,
                    dx,
                    dy,
                    self.camera.mouse_sensitivity,
                );
                self.camera.yaw = yaw;
                self.camera.pitch = pitch;
                self.camera.base.transform.position = Vector3::new(pos[0], pos[1], pos[2]);
                self.camera.base.transform.rotation = debug_camera_rotation(yaw, pitch);
            }
            // オービットが食べたデルタは後段へ渡さない。
            self.cam_input.mouse_dx = 0.0;
            self.cam_input.mouse_dy = 0.0;
            return;
        }

        // ピボット待ち／ヒット無し: 後から押されたボタンの操作を適用する。
        // Pan は `DebugCamera::update_mmb_pan` が MMB 押下でそのまま担うので、
        // ここで何もしないのが正しい（横取りすると換算式が二重になる）。
        if self.orbit.pending_fallback() == Some(OrbitFallback::Look) {
            let (dx, dy) = (self.cam_input.mouse_dx, self.cam_input.mouse_dy);
            if dx != 0.0 || dy != 0.0 {
                let (yaw, pitch) = apply_look_delta(
                    self.camera.yaw,
                    self.camera.pitch,
                    dx,
                    dy,
                    self.camera.mouse_sensitivity,
                );
                self.camera.yaw = yaw;
                self.camera.pitch = pitch;
                self.camera.base.transform.rotation = debug_camera_rotation(yaw, pitch);
            }
            // MMB も押されているのでパンが走ってしまう。デルタを食べて止める。
            self.cam_input.mouse_dx = 0.0;
            self.cam_input.mouse_dy = 0.0;
        }
    }
}

// ─── テスト ───────────────────────────────────────────────────

#[cfg(test)]
mod tests {
    use super::*;

    /// 位置比較の許容誤差（クォータニオン合成の f32 誤差を吸収する）。
    const EPS: f32 = 1e-4;
    /// 既定のマウス感度（`DebugCamera::default` と同じ値）。
    const SENS: f32 = 0.002;

    fn dist(a: [f32; 3], b: [f32; 3]) -> f32 {
        let d = [a[0] - b[0], a[1] - b[1], a[2] - b[2]];
        (d[0] * d[0] + d[1] * d[1] + d[2] * d[2]).sqrt()
    }

    // ── 回転の数式 ────────────────────────────────────────────

    /// ピボットとの距離が保たれる（ΔR が直交変換であることの帰結）。
    #[test]
    fn orbit_preserves_distance_to_pivot() {
        let pivot = [1.0, 2.0, 3.0];
        let pos = [1.0, 2.0, -7.0]; // ピボットの手前 10m
        let before = dist(pos, pivot);
        for (dx, dy) in [(120.0, 0.0), (0.0, 90.0), (-300.0, -40.0), (30.0, 30.0)] {
            let (np, _, _) = orbit_rotate(pivot, pos, 0.3, -0.2, dx, dy, SENS);
            assert!(
                (dist(np, pivot) - before).abs() < EPS,
                "距離が変わった dx={dx} dy={dy}: {} != {before}",
                dist(np, pivot)
            );
        }
    }

    /// ピボットがカメラのビュー空間で動かない（＝画面上で固定に見える）。
    ///
    /// ビュー空間座標 = R⁻¹ (pivot − position)。回転前後でこれが一致することを見る。
    #[test]
    fn orbit_keeps_pivot_fixed_in_view_space() {
        let pivot = [4.0, 1.0, 0.5];
        let pos = [-2.0, 3.0, -6.0];
        let (yaw, pitch) = (0.8_f32, 0.25_f32);

        let view_space = |p: [f32; 3], pos: [f32; 3], yaw: f32, pitch: f32| {
            let r = debug_camera_rotation(yaw, pitch);
            r.inverse()
                .rotate(Vector3::new(p[0] - pos[0], p[1] - pos[1], p[2] - pos[2]))
        };
        let before = view_space(pivot, pos, yaw, pitch);

        for (dx, dy) in [(200.0, 0.0), (0.0, -150.0), (-80.0, 60.0)] {
            let (np, ny, npi) = orbit_rotate(pivot, pos, yaw, pitch, dx, dy, SENS);
            let after = view_space(pivot, np, ny, npi);
            assert!(
                (after.x - before.x).abs() < EPS
                    && (after.y - before.y).abs() < EPS
                    && (after.z - before.z).abs() < EPS,
                "ピボットがビュー空間で動いた dx={dx} dy={dy}: {after:?} != {before:?}"
            );
        }
    }

    /// マウス X はヨー、マウス Y はピッチへ、既存の視点回転と同じ符号・感度で入る。
    #[test]
    fn orbit_maps_x_to_yaw_and_y_to_pitch() {
        let (_, yaw, pitch) =
            orbit_rotate([0.0; 3], [0.0, 0.0, -5.0], 0.0, 0.0, 100.0, 50.0, SENS);
        assert!((yaw - 100.0 * SENS).abs() < 1e-6, "ヨー: {yaw}");
        assert!((pitch - 50.0 * SENS).abs() < 1e-6, "ピッチ: {pitch}");
    }

    /// ピッチは既存の視点回転と同じ値でクランプされる（真上／真下の手前で止まる）。
    #[test]
    fn orbit_clamps_pitch_like_the_look_rotation() {
        // 上方向・下方向のどちらへ振り切ってもクランプ値で止まる
        let (_, _, up) =
            orbit_rotate([0.0; 3], [0.0, 0.0, -5.0], 0.0, 0.0, 0.0, 100_000.0, SENS);
        let (_, _, down) =
            orbit_rotate([0.0; 3], [0.0, 0.0, -5.0], 0.0, 0.0, 0.0, -100_000.0, SENS);
        assert_eq!(up, DEBUG_CAMERA_PITCH_LIMIT);
        assert_eq!(down, -DEBUG_CAMERA_PITCH_LIMIT);
        // クランプに当たっても距離は保たれる（ΔR が小さくなるだけ）
        let (np, _, _) =
            orbit_rotate([0.0; 3], [0.0, 0.0, -5.0], 0.0, 0.0, 0.0, 100_000.0, SENS);
        assert!((dist(np, [0.0; 3]) - 5.0).abs() < EPS);
    }

    /// デルタ 0 では位置も角度も動かない（跳ねの原因になる無駄な更新をしない）。
    #[test]
    fn orbit_with_zero_delta_is_identity() {
        let pos = [3.0, -1.0, 2.0];
        let (np, ny, npi) = orbit_rotate([0.0; 3], pos, 0.5, -0.1, 0.0, 0.0, SENS);
        assert!(dist(np, pos) < EPS, "位置が動いた: {np:?}");
        assert_eq!((ny, npi), (0.5, -0.1));
    }

    /// ピボットにカメラが重なっていても破綻しない（オフセット 0 は回っても 0）。
    #[test]
    fn orbit_at_zero_offset_stays_put() {
        let pivot = [1.0, 1.0, 1.0];
        let (np, _, _) = orbit_rotate(pivot, pivot, 0.0, 0.0, 500.0, 200.0, SENS);
        assert!(dist(np, pivot) < EPS, "ピボット上で位置がずれた: {np:?}");
    }

    // ── 状態機械 ──────────────────────────────────────────────

    const CUR: Option<(u32, u32)> = Some((100, 50));

    /// 中 → 右 の順でも、右 → 中 の順でも同時押しは成立する（順不同）。
    #[test]
    fn simultaneous_press_is_order_independent() {
        // 中 → 右: 後押しは右なので fallback = Look
        let p = orbit_on_button(OrbitPhase::Idle, MouseButton::Middle, true, false, CUR);
        assert_eq!(p, OrbitPhase::Idle, "片方だけでは成立しない");
        let p = orbit_on_button(p, MouseButton::Right, true, true, CUR);
        assert_eq!(
            p,
            OrbitPhase::Pending {
                screen: (100, 50),
                fallback: OrbitFallback::Look
            }
        );

        // 右 → 中: 後押しは中なので fallback = Pan
        let p = orbit_on_button(OrbitPhase::Idle, MouseButton::Right, true, false, CUR);
        assert_eq!(p, OrbitPhase::Idle);
        let p = orbit_on_button(p, MouseButton::Middle, true, true, CUR);
        assert_eq!(
            p,
            OrbitPhase::Pending {
                screen: (100, 50),
                fallback: OrbitFallback::Pan
            }
        );
    }

    /// 非同期ヒットが届いた時点でオービットへ切り替わる。
    #[test]
    fn late_gpu_hit_switches_to_orbit() {
        let p = OrbitPhase::Pending {
            screen: (10, 10),
            fallback: OrbitFallback::Look,
        };
        assert_eq!(
            orbit_on_hit(p, Some([1.0, 2.0, 3.0])),
            OrbitPhase::Active {
                pivot: [1.0, 2.0, 3.0]
            }
        );
    }

    /// ヒットが無ければオービットに入らず、後押しボタンの操作へ倒れる。
    #[test]
    fn missing_hit_falls_back_to_the_later_button() {
        for fb in [OrbitFallback::Pan, OrbitFallback::Look] {
            let p = OrbitPhase::Pending {
                screen: (10, 10),
                fallback: fb,
            };
            assert_eq!(orbit_on_hit(p, None), OrbitPhase::Fallback { fallback: fb });
            assert_eq!(
                OrbitPhase::Fallback { fallback: fb }.pending_fallback(),
                Some(fb)
            );
        }
    }

    /// カーソル座標が無ければピボットを求めようがないので即 fallback。
    #[test]
    fn press_without_cursor_goes_straight_to_fallback() {
        let p = orbit_on_button(OrbitPhase::Idle, MouseButton::Right, true, true, None);
        assert_eq!(
            p,
            OrbitPhase::Fallback {
                fallback: OrbitFallback::Look
            }
        );
    }

    /// どちらかを離せばオービットは終了し、残った方の通常操作へ戻る。
    #[test]
    fn releasing_either_button_ends_the_orbit() {
        let active = OrbitPhase::Active {
            pivot: [0.0, 0.0, 0.0],
        };
        // 右を離す（中は押したまま）→ Idle。以降は MMB パンが素通しで効く。
        assert_eq!(
            orbit_on_button(active, MouseButton::Right, false, false, CUR),
            OrbitPhase::Idle
        );
        // 中を離す（右は押したまま）→ Idle。以降は RMB の視点回転が効く。
        assert_eq!(
            orbit_on_button(active, MouseButton::Middle, false, false, CUR),
            OrbitPhase::Idle
        );
        // Pending / Fallback からも同様に抜ける
        let pending = OrbitPhase::Pending {
            screen: (1, 1),
            fallback: OrbitFallback::Pan,
        };
        assert_eq!(
            orbit_on_button(pending, MouseButton::Middle, false, false, CUR),
            OrbitPhase::Idle
        );
    }

    /// 一度離してもう一度同時押しすれば、また成立する（再入可能）。
    #[test]
    fn orbit_can_be_re_triggered_after_release() {
        let p = OrbitPhase::Active { pivot: [0.0; 3] };
        let p = orbit_on_button(p, MouseButton::Right, false, false, CUR); // 右離し
        assert_eq!(p, OrbitPhase::Idle);
        let p = orbit_on_button(p, MouseButton::Right, true, true, CUR); // 右を押し直す
        assert_eq!(
            p,
            OrbitPhase::Pending {
                screen: (100, 50),
                fallback: OrbitFallback::Look
            }
        );
    }

    /// 左ボタンなど関係の無いボタンは状態を一切変えない。
    #[test]
    fn unrelated_buttons_do_not_touch_the_state() {
        let active = OrbitPhase::Active {
            pivot: [1.0, 0.0, 0.0],
        };
        assert_eq!(
            orbit_on_button(active, MouseButton::Left, true, true, CUR),
            active
        );
        assert_eq!(
            orbit_on_button(active, MouseButton::Left, false, false, CUR),
            active
        );
    }

    /// 解決の取りこぼし（Pending 以外へ届いた結果）で Active が壊れない。
    #[test]
    fn hit_resolution_only_applies_to_pending() {
        let active = OrbitPhase::Active {
            pivot: [1.0, 0.0, 0.0],
        };
        assert_eq!(orbit_on_hit(active, Some([9.0; 3])), active);
        assert_eq!(
            orbit_on_hit(OrbitPhase::Idle, Some([9.0; 3])),
            OrbitPhase::Idle
        );
    }

    /// `Pending` → `Active` の切替でカメラの位置・向きは変わらない（跳ねない）。
    ///
    /// 切替は状態の書き換えだけで、カメラを動かす計算を伴わないことを
    /// 「デルタ 0 のオービットが恒等である」ことで担保する。
    #[test]
    fn switching_to_orbit_does_not_jerk_the_camera() {
        let pos = [2.0, 5.0, -3.0];
        let (yaw, pitch) = (0.9_f32, -0.4_f32);
        let p = orbit_on_hit(
            OrbitPhase::Pending {
                screen: (0, 0),
                fallback: OrbitFallback::Look,
            },
            Some([0.0, 0.0, 0.0]),
        );
        let pivot = p.active_pivot().expect("Active になること");
        // 切替直後（まだマウスが動いていない）フレームの適用結果
        let (np, ny, npi) = orbit_rotate(pivot, pos, yaw, pitch, 0.0, 0.0, SENS);
        assert!(dist(np, pos) < EPS, "位置が跳ねた: {np:?}");
        assert_eq!((ny, npi), (yaw, pitch), "向きが跳ねた");
    }
}
