// ============================================================
//  app/drag_state.rs — LMB ドラッグ状態の集約構造体
//
//  App 構造体から LMB ドラッグ関連フィールドを分離し、
//  単一責任原則に基づいてドラッグライフサイクル全体の状態を管理する。
//
//  【含まれる状態】
//    - ギズモドラッグ進行状態 (gizmo_drag)
//    - ドラッグ開始行列スナップショット群
//    - LMB 押下フラグ・座標
//    - 矩形選択状態と Undo 用事前スナップショット
// ============================================================

use crate::engine::components::{CanvasTransform, Transform as ActorTransform};
use crate::engine::methods::gizmo_interact::GizmoDrag;

/// LMB ドラッグに関連する全状態を集約する。
///
/// App から分離することで App 構造体の責任範囲を減らし、
/// ドラッグ関連ロジックの読み書きコストを下げる。
pub(super) struct DragState {
    /// 進行中のギズモドラッグ状態（None = ドラッグなし）。
    pub gizmo_drag: Option<GizmoDrag>,

    // ── ドラッグ開始スナップショット ────────────────────────────
    /// ドラッグ開始時の「ルート選択インスタンス」初期行列（親子フィルタ済み）。
    pub drag_root_starts: Vec<(u32, [[f32; 4]; 4])>,
    /// ドラッグ開始時の子孫インスタンス初期行列（ルート以外の追従対象）。
    pub drag_child_starts: Vec<(u32, [[f32; 4]; 4])>,
    /// アクター編集モードのギズモドラッグ開始時の子アクター MC 行列 (child_dfs_id, mat)。
    pub actor_child_drag_starts: Vec<(u32, [[f32; 4]; 4])>,
    /// アクタートランスフォームをギズモでドラッグ中に保持する開始状態 (dfs_id, old_transform)。
    pub actor_transform_drag_start: Option<(u32, ActorTransform)>,
    /// 2D アクターの CanvasTransform をギズモでドラッグ中に保持する開始状態 (dfs_id, old_canvas_transform)。
    pub canvas_transform_drag_start: Option<(u32, CanvasTransform)>,
    /// ギズモドラッグ開始時の追加 MC スロット開始行列。
    /// タプル: (slot_i, 全インスタンス開始行列 Vec)
    /// 選択スロット以外の MC を選択スロットと一緒に動かすために使う。
    pub actor_extra_mc_drag_starts: Vec<(usize, Vec<[[f32; 4]; 4]>)>,
    /// マルチ選択ギズモドラッグ時の非プライマリ選択アクター開始行列（dfs_id, start_mat）。
    pub multi_actor_drag_starts: Vec<(u32, [[f32; 4]; 4])>,

    // ── LMB 入力状態 ──────────────────────────────────────────
    /// LMB 押下中フラグ。
    pub lmb_held: bool,
    /// LMB 押下時のビューポート座標。
    pub lmb_press_pos: Option<(f32, f32)>,
    /// LMB 押下時の Ctrl 状態（ピック結果でトグル判定に使用）。
    pub ctrl_at_press: bool,

    // ── 矩形選択状態 ──────────────────────────────────────────
    /// 矩形選択ドラッグ中フラグ。
    pub rect_selecting: bool,
    /// 矩形選択開始時のインスタンス選択状態（Undo 記録用）。
    pub selection_before_rect: Vec<u32>,
    /// 矩形選択開始時のアクター DFS 選択状態（Undo 記録用）。
    pub selection_before_rect_dfs: Vec<usize>,
    /// 矩形選択開始時のプライマリ選択アクター（Undo 記録用）。
    pub selection_before_rect_primary: Option<usize>,
}

impl DragState {
    /// すべてのフィールドをデフォルト値（ドラッグなし状態）で初期化する。
    pub fn new() -> Self {
        Self {
            gizmo_drag: None,
            drag_root_starts: Vec::new(),
            drag_child_starts: Vec::new(),
            actor_child_drag_starts: Vec::new(),
            actor_transform_drag_start: None,
            canvas_transform_drag_start: None,
            actor_extra_mc_drag_starts: Vec::new(),
            multi_actor_drag_starts: Vec::new(),
            lmb_held: false,
            lmb_press_pos: None,
            ctrl_at_press: false,
            rect_selecting: false,
            selection_before_rect: Vec::new(),
            selection_before_rect_dfs: Vec::new(),
            selection_before_rect_primary: None,
        }
    }
}
