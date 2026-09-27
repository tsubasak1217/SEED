// ============================================================
//  canvas_layout/frame.rs — レイアウトの文脈（親 → 子へ渡すもの・走査全体の入力）
//
//  旧実装の各走査が引数で持ち回っていた値を 2 つに束ねる:
//    - CanvasParentFrame … 親から子へ渡す文脈（parent_canvas_size / parent_world_rs /
//                           parent_cumul_scale / parent_zone）。ノードごとに変わる
//    - CanvasLayoutEnv   … 走査全体で変わらない入力（viewport_size / canvas_viewport_overrides /
//                           root_auto_sizes / design_space と、自動スケールの割り算の扱い）
// ============================================================

use std::collections::HashMap;

use crate::engine::components::CanvasDrawZone;
use crate::engine::ecs::Entity;

use super::units::CanvasScreenEnv;

/// 単位行列（行優先・列優先どちらでも同じ）。最上位ノードの親のワールド行列。
pub const IDENTITY_MAT4: [[f32; 4]; 4] = [
    [1.0, 0.0, 0.0, 0.0],
    [0.0, 1.0, 0.0, 0.0],
    [0.0, 0.0, 1.0, 0.0],
    [0.0, 0.0, 0.0, 1.0],
];

/// 最上位ノードの親の累積スケール（倍率なし）。
pub const UNIT_CUMUL_SCALE: [f32; 2] = [1.0, 1.0];

/// 親から子へ渡すレイアウトの文脈。
///
/// フォルダノード（レイアウト透明）は受け取った文脈をそのまま子へ渡し、
/// CanvasTransform を持つノードは `placement::resolve` が作る `child_frame` を渡す。
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct CanvasParentFrame {
    /// アンカーの基準サイズ。`None` = 最上位（ビューポートが仮想親）。
    /// 子へ渡す値は必ず `anchor::child_anchor_basis` で作る（`None` は最上位専用）。
    pub anchor_basis: Option<[f32; 2]>,
    /// 親のワールド行列（行優先。回転＋平行移動のみでスケールは累積スケール側で持つ）。
    pub world_rs: [[f32; 4]; 4],
    /// 親までの累積スケール（スケールモードに応じて子の位置・サイズへ掛かる）。
    pub cumul_scale: [f32; 2],
    /// 描画ゾーン（ルートキャンバスの draw_zone をサブツリーへ継承する）。
    pub zone: CanvasDrawZone,
}

impl CanvasParentFrame {
    /// 2D キャンバスの最上位（ビューポートが仮想親）の文脈。
    ///
    /// # 引数
    /// * `zone` - ルートキャンバスが draw_zone を持たないときの既定ゾーン（呼び出し側は Foreground）
    pub const fn viewport_root(zone: CanvasDrawZone) -> Self {
        Self {
            anchor_basis: None,
            world_rs: IDENTITY_MAT4,
            cumul_scale: UNIT_CUMUL_SCALE,
            zone,
        }
    }

    /// 任意の親の文脈（旧 API の引数をそのまま束ねる。3D ワールドキャンバスの子・テスト用）。
    ///
    /// # 引数
    /// * `anchor_basis` - 親のアンカー基準サイズ（最上位なら None）
    /// * `world_rs`     - 親のワールド行列（行優先）
    /// * `cumul_scale`  - 親までの累積スケール
    /// * `zone`         - 継承する描画ゾーン
    pub const fn new(
        anchor_basis: Option<[f32; 2]>,
        world_rs: [[f32; 4]; 4],
        cumul_scale: [f32; 2],
        zone: CanvasDrawZone,
    ) -> Self {
        Self { anchor_basis, world_rs, cumul_scale, zone }
    }

    /// 最上位（親が居ない）か。ルートキャンバスだけが持つ規則（自動解像度・自動スケール・
    /// Camera 参照のビューポート・自身の draw_zone）の分岐に使う。
    #[inline]
    pub fn is_root(&self) -> bool {
        self.anchor_basis.is_none()
    }
}

/// ルートキャンバスの自動スケール（ビューポート ÷ キャンバスの大きさ）の割り算の扱い。
///
/// 旧実装は読み手によって違っていた（W2-1a の同値の検査で確かめた。docs/backlog.md）:
///   - 描画・キャンバス枠・ID 描画 … そのまま割る（大きさ 0 なら無限大）
///   - 当たり判定・2D 物理       … 分母を `f32::EPSILON` 以上に切り上げる
/// 大きさ 0 のキャンバスという退化した入力でしか差は出ないが、読み手ごとの結果を
/// 変えないため、表を作るときに読み手が選ぶ。
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum AutoScaleDivisor {
    /// そのまま割る（描画・キャンバス枠・ID 描画）。
    Raw,
    /// 分母を `f32::EPSILON` 以上にする（当たり判定・2D 物理）。
    GuardEpsilon,
}

/// 走査全体で変わらないレイアウトの入力。
#[derive(Clone, Copy)]
pub struct CanvasLayoutEnv<'m> {
    /// 最上位ノードの基準ビューポートサイズ（シーンのスクリーンスペース表示のとき Some）。
    pub viewport_size: Option<[f32; 2]>,
    /// ルートキャンバスごとの実効ビューポートの上書き（Camera 参照。actor.entity → [w, h]）。
    pub viewport_overrides: &'m HashMap<Entity, [f32; 2]>,
    /// ビューポート・ルートキャンバスの自動解像度（actor.entity → [w, h]。登録済みルートは
    /// 大きさをこの値へ置き換え、CanvasTransform を恒等として扱う）。
    pub root_auto_sizes: &'m HashMap<Entity, [f32; 2]>,
    /// ビューポートタブの設計空間表示中か（true のときルートキャンバス左上をワールド原点にする）。
    pub design_space: bool,
    /// 自動スケールの割り算の扱い（読み手ごとの旧実装の違いを保つ）。
    pub auto_scale_divisor: AutoScaleDivisor,
    /// 画面の情報（dp の倍率・安全領域。W2-1b）。使わない文脈は `CanvasScreenEnv::NONE`。
    /// 描画・当たり判定・2D 物理の表が同じ値を使うこと（App::canvas_screen_env）。
    pub screen: CanvasScreenEnv,
}

impl<'m> CanvasLayoutEnv<'m> {
    /// ビューポート基準も自動解像度も無い文脈（アクター編集タブ・3D ワールドキャンバスの子・テスト）。
    ///
    /// # 引数
    /// * `empty` - 空の表（呼び出し側が持つ。上書き・自動解像度の両方に使う）
    /// * `auto_scale_divisor` - 自動スケールの割り算の扱い
    pub fn without_viewport(
        empty: &'m HashMap<Entity, [f32; 2]>,
        auto_scale_divisor: AutoScaleDivisor,
    ) -> Self {
        Self {
            viewport_size: None,
            viewport_overrides: empty,
            root_auto_sizes: empty,
            design_space: false,
            auto_scale_divisor,
            screen: CanvasScreenEnv::NONE,
        }
    }
}
