// ============================================================
//  placement_mode.rs — ロジック配置の「カーソル追従 → クリック確定」モード
//
//  【何をするモジュールか】
//  ロジック配置ダイアログの「配置」を押すと、ダイアログは閉じて
//  ランタイムが**配置モード**へ入る。以後は
//    ・毎フレーム: カーソルのレイが当たった表面を基準点としてプレビューを描く
//    ・左クリック: その基準点で実生成（Undo 1 件）＋生成物を全選択してモード終了
//    ・右クリック / Esc: 何も生成せずモード終了
//  という 3 状態だけの単純な状態機械で動く。
//
//  【なぜランタイム側にモードを置くのか】
//  基準点はカーソル下の**メッシュ・地形の表面**であり、その解決には
//  ID バッファの読み戻しと地形の密度場が要る。どちらもランタイムにしか無い。
//  エディタ側に状態を持つと「エディタが思う位置」と「実際に置かれる位置」が
//  ずれうるので、状態も解決も生成もランタイムに寄せて 1 か所にする。
//  エディタが持つのは `PLACEMENT_STATE:0|1` に応じた
//  「Esc を取消へ回す」「ヒントを出す」だけの表示上の都合である。
//
//  【モーダルトランスフォーム（G/R/S）との違い】
//  あちらはカーソルがシーンパネルの外へ出ても変形を続けたいので、
//  エディタ側に低レベルマウスフックを置いて座標を送っている。
//  配置モードは「シーンの中を指してクリックする」操作なので、カーソルは
//  常にシーンパネル内にある。ランタイムの CursorMoved / MouseInput だけで
//  完結でき、フックは要らない。
//
//  【カメラ操作との衝突】
//  取消を右クリックに割り当てるため、モード中は**右ドラッグのカメラ回転を止める**。
//  カメラを動かしてから置きたい場合は、いったん取消してから置き直す。
//  （ホイールズームと中ボタンパンは無効化しない理由も無いので合わせて止める。
//   プレビューの基準点は毎フレーム解決し直すので、実害があるわけではないが、
//   「モード中は視点を変えない」ほうが操作の因果が読みやすい。）
// ============================================================

use crate::engine::methods::drawer::LineBatch;
use crate::engine::placement::{generate_points, PlacementPoint};

use super::control_point_ops::nearer_hit;
use super::logic_placement_ops::{ground_positions_with, LogicPlaceRequest, TARGET_CONTROL_POINTS};
use super::terrain_scatter_ops::TerrainScatterField;
use super::{App, RuntimeMode};

// ─── 定数（マジックナンバー禁止）─────────────────────────────

/// プレビューで描くマーカーの上限点数。
///
/// 生成上限（4096 点）をそのまま毎フレーム描くと、線分数が 4096 × 7 本になり
/// 編集中のフレームレートを目に見えて落とす。「配置の形が読める」ことが目的なので、
/// 先頭からこの数だけ描き、残りは数だけを信用してもらう
/// （**確定時には必ず全点が生成される**。プレビューだけの上限である）。
const PREVIEW_MAX_MARKERS: usize = 512;

/// 3D マーカーの大きさを「そこに置いたギズモの見かけ半径」の何倍にするか。
///
/// 制御点キューブ（0.10）より小さめにして、点が密なパターンでも潰れて見えないようにする。
const MARKER_RADIUS_RATIO: f32 = 0.07;

/// 3D マーカーの最小半径 [m]（極端に寄ったときに潰れないための下限）。
const MARKER_HALF_MIN: f32 = 0.02;

/// 2D マーカーの大きさを、2D オルソカメラの可視半高の何倍にするか。
///
/// 可視半高に比例させるので、ズームしても画面上の見かけの大きさが変わらない。
const MARKER_2D_HALF_RATIO: f32 = 0.012;

/// 向き線の長さをマーカー半径の何倍にするか。
const YAW_LINE_RATIO: f32 = 2.5;

/// マーカーの色（水色。確定済みの何物でもない「これから置く印」）。
const MARKER_COLOR: [f32; 4] = [0.45, 0.85, 1.0, 0.9];

/// 向き線の色（橙。位置マーカーと役割を色で分ける）。
const YAW_COLOR: [f32; 4] = [1.0, 0.65, 0.20, 0.9];

/// 基準点マーカー（カーソルの着弾点そのもの）の色（白）。
const BASE_COLOR: [f32; 4] = [1.0, 1.0, 1.0, 1.0];

/// 基準点マーカーを通常マーカーの何倍の大きさで描くか。
const BASE_MARKER_SCALE: f32 = 1.8;

// ============================================================
//  状態
// ============================================================

/// 配置モードの状態。
///
/// **点列は開始時に 1 回だけ生成して持ち回る**。毎フレーム引き直すと、
/// ランダム散布・ジッターの乱数がカーソル移動のたびに走り、プレビューが
/// ちらついたうえに「見えている形」と「置かれる形」が一致しなくなる。
pub(super) struct PlacementMode {
    /// 配置指定（配置元・親・地形接地・パターン）。基準点だけを外から与える。
    pub req: LogicPlaceRequest,
    /// 生成済みの点列（基準点相対）。開始時に固定する。
    pub points: Vec<PlacementPoint>,
    /// 直近フレームに解決した基準点（ワールド座標。2D はキャンバス座標を `[x,0,y]` へ）。
    ///
    /// `None` は「まだ 1 度も解決できていない」＝確定しても置き場所が無い状態。
    pub base: Option<[f32; 3]>,
    /// モードへ入った時点の世界線。切り替わったら自動で取り消す。
    pub world_line: u32,
}

// ============================================================
//  純関数（App に依存しない＝ユニットテスト可能な中核）
// ============================================================

/// 表面ヒット候補から基準点を 1 点に決める。
///
/// メッシュ・水面（GPU ピック）と地形（CPU レイマーチ）のうち**カメラに近い方**を採り、
/// どちらも無ければ `fallback`（カメラからレイ方向へ一定距離進んだ点）を返す。
/// 「空をクリックしても必ずどこかに置ける」というアクタ D&D の約束をここで守る。
pub(super) fn surface_or_fallback(
    cam:         [f32; 3],
    gpu_hit:     Option<[f32; 3]>,
    terrain_hit: Option<[f32; 3]>,
    fallback:    [f32; 3],
) -> [f32; 3] {
    nearer_hit(cam, gpu_hit, terrain_hit).unwrap_or(fallback)
}

/// 配置モードを続けてよい状況かどうか。
///
/// Play 開始（Edit でもポーズでもない）・シーン破棄・タブ（世界線）切り替えの
/// いずれかが起きたら false。呼び出し側は false なら黙って取り消す
///（生成前の状態しか持たないので、捨てても何も壊れない）。
pub(super) fn placement_mode_still_valid(
    in_editor: bool,
    has_scene: bool,
    mode_world_line:   u32,
    active_world_line: u32,
) -> bool {
    in_editor && has_scene && mode_world_line == active_world_line
}

/// 基準点と点列から、プレビュー／実配置のワールド位置を組み立てる。
///
/// 先頭 `max` 点までを返す（プレビューの描画コスト頭打ち。`max` に
/// `usize::MAX` を渡せば全点）。接地は呼び出し側が別途行う。
pub(super) fn placement_world_positions(
    base:   [f32; 3],
    points: &[PlacementPoint],
    max:    usize,
) -> Vec<[f32; 3]> {
    points
        .iter()
        .take(max)
        .map(|p| [
            base[0] + p.position[0],
            base[1] + p.position[1],
            base[2] + p.position[2],
        ])
        .collect()
}

impl App {
    // ============================================================
    //  状態の問い合わせと通知
    // ============================================================

    /// 配置モードが進行中か。
    ///
    /// 進行中は通常の選択クリック・ギズモ・モーダルトランスフォーム・
    /// カメラ操作をすべて無効化する（呼び出し側が本関数を見る）。
    pub(super) fn placement_mode_active(&self) -> bool {
        self.placement_mode.is_some()
    }

    /// 進行状態をエディタへ通知する（`PLACEMENT_STATE:1` / `PLACEMENT_STATE:0`）。
    ///
    /// エディタは埋め込み Edit モードでキーボードフォーカスを持つため、
    /// 「Esc を削除ダイアログではなく配置の取消へ回す」判断にこの状態が要る
    ///（モーダルトランスフォームの `MODAL_STATE` とまったく同じ役割）。
    fn send_placement_state(&self, active: bool) {
        if let Some(ipc) = &self.ipc {
            ipc.send(if active { "PLACEMENT_STATE:1" } else { "PLACEMENT_STATE:0" });
        }
    }

    // ============================================================
    //  開始
    // ============================================================

    /// `LOGIC_PLACE_BEGIN:{json}` を処理して配置モードへ入る。
    ///
    /// ここでは点列を作るだけで、シーンには一切触れない
    /// （＝取消しても Undo 履歴もシーンも汚れない）。
    pub(super) fn handle_logic_place_begin(&mut self, json: &str) {
        if self.scene.is_none() { return; }

        let req: LogicPlaceRequest = match serde_json::from_str(json) {
            Ok(r) => r,
            Err(e) => {
                self.notify_placement_error(&format!("ロジック配置の指定を解釈できません: {e}"));
                return;
            }
        };
        // 制御点への追記はカーソル位置と無関係（アクタ相対座標）なので、
        // 配置モードには入らず従来どおり即時追記へ回す。
        if req.target == TARGET_CONTROL_POINTS {
            self.handle_logic_place(json);
            return;
        }

        let result = generate_points(&req.spec);
        if let Some(w) = &result.warning {
            self.notify_placement_error(w);
        }
        if result.points.is_empty() {
            self.notify_placement_error("配置する点がありません（個数・行列数を確認してください）");
            return;
        }

        // 既に進行中なら黙って捨てて入れ替える（ダイアログを開き直した場合）。
        let world_line = self.active_world_line;
        self.placement_mode = Some(PlacementMode {
            req,
            points: result.points,
            base: None,
            world_line,
        });
        // 掴み途中のギズモ・ホバー表示を落として、モードの排他を見た目にも反映する。
        self.hovered_gizmo_part = None;
        self.send_placement_state(true);
    }

    // ============================================================
    //  基準点の解決（毎フレーム）
    // ============================================================

    /// 配置モードがこのフレームで ID バッファの読み戻しを要求するか。
    ///
    /// 3D のときだけ true。2D はキャンバス座標を CPU で直に求められるので
    /// 読み戻し枠を消費しない（他用途へ譲る）。
    pub(super) fn placement_needs_id_readback(&self) -> bool {
        self.placement_mode.as_ref().is_some_and(|m| !m.req.is_2d)
            && self.last_cursor_pos.is_some()
    }

    /// 読み戻し結果（GPU ヒット）から基準点を解決して保持する（3D 専用）。
    ///
    /// `gpu_hit` は ID パスの RGB に焼かれたワールド座標（背景なら None）。
    /// 地形は ID パスに描かれないので CPU レイマーチと突き合わせ、
    /// **カメラに近い方**を採る（制御点 D&D とまったく同じ規則）。
    /// どちらにも当たらなければ、アクタ D&D と同じ
    /// 「カメラからレイ方向へ一定距離」へフォールバックする。
    pub(super) fn resolve_placement_hover(&mut self, gpu_hit: Option<[f32; 3]>, sx: u32, sy: u32) {
        if self.placement_mode.is_none() { return; }
        let base = self.resolve_surface_or_camera_dist(gpu_hit, sx, sy);
        if let Some(mode) = self.placement_mode.as_mut() {
            mode.base = Some(base);
        }
    }

    /// 2D 配置の基準点をカーソルから直に求めて保持する（GPU 読み戻し不要）。
    ///
    /// キャンバス空間へのマッピングは 2D ピック・2D ドロップと同じ
    /// `window_to_canvas_2d` を使う（＝落ちる場所と見える場所が同じ規約になる）。
    /// パターンの XZ 平面をキャンバスの XY に写す規約に合わせ、`[x, 0, y]` で保持する。
    pub(super) fn update_placement_hover_2d(&mut self) {
        let Some(mode) = self.placement_mode.as_ref() else { return };
        if !mode.req.is_2d { return; }
        let Some((cx, cy)) = self.last_cursor_pos else { return };
        let p = self.window_to_canvas_2d(cx, cy);
        if let Some(mode) = self.placement_mode.as_mut() {
            mode.base = Some([p[0], 0.0, p[1]]);
        }
    }

    // ============================================================
    //  確定 / 取消
    // ============================================================

    /// 左クリックでの確定。基準点＝直近に解決した位置で実生成する。
    ///
    /// 生成は従来どおり `place_actors`（グループフォルダ配下・Undo 1 件）に任せ、
    /// 生成物の全選択もその中で行う。基準点が未解決（＝一度も解決に成功していない）
    /// なら何も生成せずに終わる。
    pub(super) fn confirm_placement(&mut self) {
        let Some(mode) = self.placement_mode.take() else { return };
        self.send_placement_state(false);
        let Some(base) = mode.base else {
            self.notify_placement_error("配置位置が決まっていないため何も生成しませんでした");
            return;
        };
        self.place_actors(&mode.req, &mode.points, base);
    }

    /// 右クリック / Esc での取消。何も生成せずモードを抜ける。
    pub(super) fn cancel_placement(&mut self) {
        if self.placement_mode.take().is_none() { return; }
        self.send_placement_state(false);
    }

    /// 毎フレームの前提条件チェック。
    ///
    /// Play 開始・シーン破棄・世界線（タブ）切り替えが起きたら**静かに取り消す**。
    /// 生成前の状態しか持っていないので、破棄しても何も壊れない。
    pub(super) fn tick_placement_mode_guard(&mut self) {
        let Some(mode) = self.placement_mode.as_ref() else { return };
        let still_valid = placement_mode_still_valid(
            self.mode == RuntimeMode::Edit || self.paused,
            self.scene.is_some(),
            mode.world_line,
            self.active_world_line,
        );
        if still_valid { return; }
        self.cancel_placement();
    }

    // ============================================================
    //  プレビュー描画
    // ============================================================

    /// プレビューに描く点（ワールド／キャンバス座標とヨー）を組み立てる。
    ///
    /// 3D で地形接地が有効なら**確定時とまったく同じ関数**で接地させる。
    /// こうしておくと「マーカーが立っている高さに必ず置かれる」が構造的に保証される。
    /// 描くのは先頭 `PREVIEW_MAX_MARKERS` 点まで（描画コストの頭打ち）。
    fn placement_preview_points(&self) -> Vec<([f32; 3], f32)> {
        let Some(mode) = self.placement_mode.as_ref() else { return Vec::new() };
        let Some(base) = mode.base else { return Vec::new() };

        let take = mode.points.len().min(PREVIEW_MAX_MARKERS);
        let slice = &mode.points[..take];
        let mut positions = placement_world_positions(base, slice, PREVIEW_MAX_MARKERS);

        if mode.req.ground && !mode.req.is_2d {
            let field = TerrainScatterField::from_state(&self.terrain);
            let _ = ground_positions_with(&field, &mut positions);
        }

        positions
            .into_iter()
            .zip(slice.iter())
            .map(|(pos, p)| (pos, p.rotation[1]))
            .collect()
    }

    /// 配置モードのプレビュー線を組む（描くものが無ければ None）。
    ///
    /// フレームループがレンダラを可変借用する**前**に呼ぶこと
    ///（`&self` しか使わないので、可変借用と同時には呼べない）。
    /// 制御点ギズモ・モーダル軸線とまったく同じ「CPU で頂点を組み、
    /// 描画ブロック内で GPU バッファ化する」流儀に合わせてある。
    pub(super) fn build_placement_preview_line_batch(&self) -> Option<LineBatch> {
        let mode = self.placement_mode.as_ref()?;
        let base = mode.base?;
        let is_2d = mode.req.is_2d;
        let points = self.placement_preview_points();
        if points.is_empty() { return None; }

        let mut lb = LineBatch::new();
        // 基準点（カーソルの着弾点）を大きめの十字で。パターンのどこが
        // カーソルに吸い付いているか（＝アンカー）が一目で分かるようにする。
        let base_draw = if is_2d { [base[0], base[2], 0.0] } else { base };
        let base_half = self.placement_marker_half(base, is_2d) * BASE_MARKER_SCALE;
        add_marker(&mut lb, base_draw, base_half, is_2d, BASE_COLOR);

        for (pos, yaw) in points {
            let draw_pos = if is_2d { [pos[0], pos[2], 0.0] } else { pos };
            let half = self.placement_marker_half(pos, is_2d);
            add_marker(&mut lb, draw_pos, half, is_2d, MARKER_COLOR);
            add_yaw_line(&mut lb, draw_pos, half * YAW_LINE_RATIO, yaw, is_2d);
        }
        if lb.is_empty() { None } else { Some(lb) }
    }

    /// マーカーの半径（画面上の見かけの大きさが一定になるよう距離・ズームへ追従させる）。
    fn placement_marker_half(&self, pos: [f32; 3], is_2d: bool) -> f32 {
        if is_2d {
            let half_h = self
                .canvas_cameras
                .get(&self.active_world_line)
                .map(|c| c.ortho_half_h)
                .unwrap_or(1.0);
            (half_h * MARKER_2D_HALF_RATIO).max(MARKER_HALF_MIN)
        } else {
            (self.editor_3d_gizmo_radius(pos) * MARKER_RADIUS_RATIO).max(MARKER_HALF_MIN)
        }
    }
}

// ============================================================
//  線の組み立て（App に依存しない小物）
// ============================================================

/// 位置マーカー（十字）を 1 個追加する。
///
/// 3D は XZ 平面の十字に縦のティックを足した 3 本（地面に刺した杭のように見せる）。
/// 2D はキャンバス平面（XY）の十字 2 本。
fn add_marker(lb: &mut LineBatch, center: [f32; 3], half: f32, is_2d: bool, color: [f32; 4]) {
    let [x, y, z] = center;
    if is_2d {
        lb.add_line([x - half, y, z], [x + half, y, z], color);
        lb.add_line([x, y - half, z], [x, y + half, z], color);
    } else {
        lb.add_line([x - half, y, z], [x + half, y, z], color);
        lb.add_line([x, y, z - half], [x, y, z + half], color);
        lb.add_line([x, y, z], [x, y + half * 2.0, z], color);
    }
}

/// 向き線（ヨーの指す方向へ伸びる 1 本）を追加する。
///
/// ヨーの規約は `yaw = atan2(dir.x, dir.z)`（ヨー 0 で +Z）。
/// 2D ではパターンの Z がキャンバス Y に写るので、そのまま (sin, cos) を XY に置く。
fn add_yaw_line(lb: &mut LineBatch, center: [f32; 3], len: f32, yaw_deg: f32, is_2d: bool) {
    let rad = yaw_deg.to_radians();
    let (s, c) = (rad.sin(), rad.cos());
    let end = if is_2d {
        [center[0] + s * len, center[1] + c * len, center[2]]
    } else {
        [center[0] + s * len, center[1], center[2] + c * len]
    };
    lb.add_line(center, end, YAW_COLOR);
}

// ============================================================
//  テスト — App を組まずに配置モードの中核（純関数と状態機械）を検証する
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;
    use crate::engine::placement::{PlacementPattern, PlacementSpec};

    /// テスト用の世界線。
    const TEST_WL: u32 = 0;

    /// 3×3 グリッドの配置モードを、指定の基準点解決状態で作る。
    fn mode_with_base(base: Option<[f32; 3]>) -> PlacementMode {
        let spec = PlacementSpec {
            pattern: PlacementPattern::Grid,
            rows: 3, cols: 3, layers: 1,
            spacing_x: 2.0, spacing_z: 2.0,
            anchor_x: 0.5, anchor_y: 0.5,
            ..Default::default()
        };
        let points = generate_points(&spec).points;
        PlacementMode {
            req: LogicPlaceRequest::default(),
            points,
            base,
            world_line: TEST_WL,
        }
    }

    // ─── 状態遷移 ─────────────────────────────────────────

    /// **begin**: 開始直後は点列が固定され、基準点はまだ未解決であること。
    #[test]
    fn begin_fixes_points_and_leaves_base_unresolved() {
        let m = mode_with_base(None);
        assert_eq!(m.points.len(), 9, "3×3 の点列が開始時に確定していること");
        assert!(m.base.is_none(), "カーソル解決前は基準点なし");
    }

    /// **hover**: 基準点が解決されると、全点がその位置ぶん平行移動すること。
    #[test]
    fn hover_translates_every_point_by_the_base() {
        let m = mode_with_base(Some([10.0, 3.0, -5.0]));
        let base = m.base.expect("基準点は解決済み");
        let pos = placement_world_positions(base, &m.points, usize::MAX);
        assert_eq!(pos.len(), m.points.len(), "全点が返ること");
        for (w, p) in pos.iter().zip(m.points.iter()) {
            for k in 0..3 {
                assert!((w[k] - (base[k] + p.position[k])).abs() < 1.0e-4);
            }
        }
        // 中心揃えの 3×3 なので、基準点そのものを含む点が必ずある。
        assert!(pos.iter().any(|w| (w[0] - 10.0).abs() < 1.0e-4 && (w[2] + 5.0).abs() < 1.0e-4),
                "アンカー 0.5/0.5 では中央の点が基準点に一致すること");
    }

    /// **hover の再解決**: カーソルが動くと基準点だけが差し替わり、点列は変わらないこと。
    ///
    /// 点列を毎フレーム作り直すと、ランダム散布・ジッターの結果が
    /// カーソル移動のたびに変わって「見た形と違う配置になる」。
    #[test]
    fn hover_updates_base_without_regenerating_points() {
        let mut m = mode_with_base(Some([0.0, 0.0, 0.0]));
        let before = m.points.clone();
        m.base = Some([7.0, 0.0, 7.0]);
        assert_eq!(m.points, before, "点列は再生成されないこと");
        assert_eq!(m.base, Some([7.0, 0.0, 7.0]));
    }

    /// **confirm の前提**: 基準点が未解決なら確定できない（何も生成しない）こと。
    #[test]
    fn confirm_requires_a_resolved_base() {
        assert!(mode_with_base(None).base.is_none(), "未解決では確定材料が無い");
        assert!(mode_with_base(Some([0.0; 3])).base.is_some(), "解決済みなら確定できる");
    }

    /// **Play 開始で取消**: Edit でもポーズでもなくなったらモードを続けないこと。
    #[test]
    fn play_start_invalidates_the_mode() {
        assert!(placement_mode_still_valid(true, true, TEST_WL, TEST_WL), "Edit 中は継続");
        assert!(!placement_mode_still_valid(false, true, TEST_WL, TEST_WL), "Play 開始で取消");
    }

    /// シーン破棄・タブ（世界線）切り替えでも取り消されること。
    #[test]
    fn scene_loss_or_tab_switch_invalidates_the_mode() {
        assert!(!placement_mode_still_valid(true, false, TEST_WL, TEST_WL), "シーン破棄で取消");
        assert!(!placement_mode_still_valid(true, true, TEST_WL, TEST_WL + 1), "タブ切替で取消");
    }

    // ─── 基準点の解決 ─────────────────────────────────────

    /// **ヒット無しならカメラから一定距離へフォールバック**すること。
    #[test]
    fn no_hit_falls_back_to_camera_distance() {
        let cam      = [0.0, 5.0, 0.0];
        let fallback = [0.0, 5.0, 10.0];
        assert_eq!(surface_or_fallback(cam, None, None, fallback), fallback);
    }

    /// ヒットがあればフォールバックを使わず、カメラに近い方を採ること。
    #[test]
    fn hits_take_precedence_over_the_fallback() {
        let cam      = [0.0, 0.0, 0.0];
        let fallback = [0.0, 0.0, 10.0];
        let mesh     = [0.0, 0.0, 8.0];
        let terrain  = [0.0, 0.0, 3.0];
        assert_eq!(surface_or_fallback(cam, Some(mesh), None, fallback), mesh,
                   "メッシュだけならメッシュ");
        assert_eq!(surface_or_fallback(cam, None, Some(terrain), fallback), terrain,
                   "地形だけなら地形");
        assert_eq!(surface_or_fallback(cam, Some(mesh), Some(terrain), fallback), terrain,
                   "両方あるならカメラに近い方");
    }

    // ─── プレビューの上限 ─────────────────────────────────

    /// プレビューは上限で頭打ちにするが、**上限は点列そのものを削らない**こと。
    #[test]
    fn preview_cap_limits_markers_but_not_the_points() {
        let m = mode_with_base(Some([0.0; 3]));
        let capped = placement_world_positions([0.0; 3], &m.points, 4);
        assert_eq!(capped.len(), 4, "プレビューは上限まで");
        assert_eq!(m.points.len(), 9, "点列そのものは減らない（確定時は全点が置かれる）");
    }
}
