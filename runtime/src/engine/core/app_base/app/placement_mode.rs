// ============================================================
//  placement_mode.rs — ロジック配置の「カーソル追従 → クリック確定」モード
//
//  【何をするモジュールか】
//  ロジック配置ダイアログの「配置」を押すと、ダイアログは閉じて
//  ランタイムが**配置モード**へ入る。以後は
//    ・開始時: 生成予定のアクタ群を**実際にシーンへ仮スポーン**する
//    ・毎フレーム: カーソルのレイが当たった表面を基準点として仮スポーン群を移動する
//    ・左クリック: その位置で採用（Undo 1 件）＋生成物を全選択してモード終了
//    ・右クリック / Esc: 仮スポーンを削除してモード終了（Undo 履歴は汚さない）
//  という状態機械で動く。
//
//  【なぜ「仮スポーン」方式なのか】
//  以前は線マーカーだけのプレビューだったが、
//  「置いてみないと本当の見た目が分からない」（メッシュ・スケール・接地の高さ）
//  という不満が大きかった。**確定時とまったく同じ生成経路**（`spawn_placement_actors`）
//  で先に置いてしまえば、プレビュー＝結果になり、見た目の齟齬が原理的に消える。
//
//  取消のコストは「入れたものを消す」だけであり、
//  開始直前に取ったアクタツリーのスナップショットを `before` として持っておけば、
//    ・確定 … `before` → 現在 の差分を `ActorTreeSnapshotCommand` 1 件で記録
//    ・取消 … 仮スポーンしたグループを削除（＝スナップショットの状態へ戻る）
//  の 2 つで済む。**Undo 1 回で全部消える**という約束もこれで守られる。
//
//  【取消でツリー全体を再構築しない理由】
//  スナップショットからの完全再構築（`rebuild_actors_for_wl`）は、取消のたびに
//  その世界線の全アクタを作り直す。取消は最も頻度の高い操作なので重すぎるうえ、
//  プレビュー中にユーザーがヒエラルキーで行った無関係な編集まで巻き戻してしまう。
//  こちらは**自分が足したグループだけ**を正確に取り除くので、両方の問題が無い。
//  （結果としてツリーは開始時のスナップショットと一致する。テストで固定してある。）
//
//  【なぜランタイム側にモードを置くのか】
//  基準点はカーソル下の**メッシュ・地形の表面**であり、その解決には
//  ID バッファの読み戻しと地形の密度場が要る。どちらもランタイムにしか無い。
//  エディタ側に状態を持つと「エディタが思う位置」と「実際に置かれる位置」が
//  ずれうるので、状態も解決も生成もランタイムに寄せて 1 か所にする。
//
//  【カメラ操作との衝突】
//  取消を右クリックに割り当てるため、モード中は**右ドラッグのカメラ回転を止める**。
//  ホイールズームと中ボタンパンも合わせて止め、「モード中は視点を変えない」で統一する。
//
//  【半径ドラッグ（円形／円弧パターン）】
//  円は「半径をいくつにするか」を数値で決めにくい。そこで円形パターンのときだけ
//    左ボタン押下 = 中心を固定 → 押したままドラッグ = 半径をリアルタイム調整
//    → 離した時点で確定
//  とする。押してすぐ離した（＝移動量が閾値未満）ならクリック扱いで、
//  ダイアログの半径値のまま従来どおり即配置する。
//  ドラッグで決めた半径は `PLACEMENT_RADIUS` でエディタへ返し、次回の既定値になる。
// ============================================================

use crate::engine::components::{CanvasTransform, Transform};
use crate::engine::core::transform_sync::set_actor_world_transform;
use crate::engine::core::app_base::scene::Scene;
use crate::engine::core::app_base::undo::ActorTreeSnapshotCommand;
use crate::engine::ecs::Entity;
use crate::engine::methods::drawer::LineBatch;
use crate::engine::placement::{generate_points, PlacementPattern, PlacementPoint};
use crate::engine::structs::objects::{Actor, actor::ActorData};

use super::actor_utils::{
    despawn_actor_recursive, dfs_ids_for_entities, extract_actor_by_entity, find_actor_by_entity_mut,
};
use super::control_point_ops::{nearer_hit, transform_point};
use crate::engine::methods::gizmo_interact::mat4x4_inv;
use super::logic_placement_ops::{ground_positions_with, LogicPlaceRequest, TARGET_CONTROL_POINTS};
use super::terrain_scatter_ops::TerrainScatterField;
use super::{App, RuntimeMode};

// ─── 定数（マジックナンバー禁止）─────────────────────────────

/// 3D マーカーの大きさを「そこに置いたギズモの見かけ半径」の何倍にするか。
const MARKER_RADIUS_RATIO: f32 = 0.07;

/// 3D マーカーの最小半径 [m]（極端に寄ったときに潰れないための下限）。
const MARKER_HALF_MIN: f32 = 0.02;

/// 2D マーカーの大きさを、2D オルソカメラの可視半高の何倍にするか。
const MARKER_2D_HALF_RATIO: f32 = 0.012;

/// 基準点マーカー（カーソルの着弾点そのもの）の色（白）。
const BASE_COLOR: [f32; 4] = [1.0, 1.0, 1.0, 1.0];

/// 基準点マーカーを通常マーカーの何倍の大きさで描くか。
const BASE_MARKER_SCALE: f32 = 1.8;

/// 半径ドラッグ中に描く円の色（水色。「いま決めている半径」の印）。
const RADIUS_CIRCLE_COLOR: [f32; 4] = [0.45, 0.85, 1.0, 0.9];

/// 半径ドラッグ中に描く円の分割数（見た目が円に見える最小限）。
const RADIUS_CIRCLE_SEGMENTS: usize = 64;

/// 仮スポーン中のアクタに出すアイコンの色（水色寄り・半透明＝「まだ仮」）。
pub(super) const PREVIEW_ICON_TINT: [f32; 4] = [0.55, 0.85, 1.0, 0.6];

/// 制御点プレビューのアイコン色（黄橙寄り・半透明）。
///
/// アクタ配置（水色）と**必ず違う色**にする。どちらの配置モードに居るのかは
/// アイコンの色でしか判別できず、取り違えると「アクタを置いたつもりが点だった」
/// という取り消しづらい事故になる。
pub(super) const CONTROL_POINT_PREVIEW_ICON_TINT: [f32; 4] = [1.0, 0.78, 0.30, 0.75];

/// 「押してすぐ離した」をクリックと見なすカーソル移動量の上限 [px]。
///
/// これ以上動いていれば半径ドラッグ、未満ならクリック（ダイアログの半径で即配置）。
/// 手の震えでドラッグ扱いにならない程度に小さく取る。
const RADIUS_DRAG_MIN_PIXELS: f32 = 4.0;

/// 半径ドラッグで許す最小半径 [m]（中心と重なって 0 になるのを防ぐ）。
const RADIUS_DRAG_MIN: f32 = 0.01;

/// 基準点・半径が「変わった」と見なす距離のしきい値 [m]。
///
/// 毎フレームの再配置は物理コライダー・BLAS の更新を伴うので、
/// 実質動いていないフレームでは何もしない。
const PREVIEW_EPSILON: f32 = 1.0e-4;

// ============================================================
//  状態
// ============================================================

/// 半径ドラッグの状態（円形／円弧パターンでのみ発生する）。
pub(super) struct RadiusDrag {
    /// 左ボタンを押した時点で固定した中心（＝配置の基準点）。
    pub center: [f32; 3],
    /// 押下時のカーソル座標 [px]（クリック／ドラッグの判定に使う）。
    pub press_cursor: (f32, f32),
    /// 押下時点の半径 [m]（＝ダイアログの値。クリック扱いのときここへ戻す）。
    pub start_radius: f32,
    /// 閾値を超えて動いたか（true ならドラッグ、false ならクリック）。
    pub dragged: bool,
}

/// 制御点への配置（`target = control_points`）の対象スロット。
///
/// アクタ配置と違い**実体を仮スポーンしない**ので、モードが握るのは
/// 「どのアクタの・どのスロットへ入れるか」の 2 つだけで足りる。
#[derive(Copy, Clone, Debug, PartialEq, Eq)]
pub(super) struct ControlPointPlacement {
    /// 対象アクタの DFS id。
    pub actor_dfs_id: u32,
    /// 対象スロットの添字。
    pub slot_idx: u32,
}

/// 配置モードの状態。
///
/// **点列は開始時に 1 回だけ生成して持ち回る**。毎フレーム引き直すと、
/// ランダム散布・ジッターの乱数がカーソル移動のたびに走り、プレビューが
/// ちらついたうえに「見えている形」と「置かれる形」が一致しなくなる。
/// （半径ドラッグ中だけは半径が変わるので引き直すが、シードは同じなので
///   ジッターの模様は保たれる。）
pub(super) struct PlacementMode {
    /// 配置指定（配置元・親・地形接地・パターン）。基準点だけを外から与える。
    pub req: LogicPlaceRequest,
    /// 生成済みの点列（基準点相対）。開始時に固定する。
    pub points: Vec<PlacementPoint>,
    /// 直近フレームに解決したカーソルの着弾点。
    ///
    /// 座標系は**配置対象で変わる**:
    ///   ・アクタ配置   … ワールド座標（2D は `[x, 0, y]`）
    ///   ・制御点への配置 … **対象アクタのローカル座標**
    /// 制御点はアクタ相対のデータなので、ローカルで持っておけば点列の合成
    /// （`placement_world_positions`）も半径ドラッグもそのまま使い回せる。
    /// ワールドが要るのは描画のときだけで、そこで対象アクタの行列を掛ける。
    ///
    /// `None` は「まだ 1 度も解決できていない」。
    pub base: Option<[f32; 3]>,
    /// モードへ入った時点の世界線。切り替わったら自動で取り消す。
    pub world_line: u32,
    /// モードへ入る**直前**のアクタツリー（仮スポーン前）。確定時の Undo `before`。
    pub before_actors: Vec<ActorData>,
    /// 仮スポーンしたグループフォルダのエンティティ。
    pub preview_group: Option<Entity>,
    /// 仮スポーンした子アクタのエンティティ列（移動・アイコン表示の対象）。
    pub preview_entities: Vec<Entity>,
    /// 仮スポーンのサブツリーが占める DFS id の範囲 `[start, end)`。
    ///
    /// ID パスのピックが**プレビュー自身**に当たったかどうかの判定に使う
    /// （当たった場合は「何にも当たっていない」と扱わないと自己参照ループになる）。
    /// グループを挿入した時点で確定し、モード中はツリーが変わらないので固定でよい。
    pub preview_dfs_range: Option<(u32, u32)>,
    /// 直近に仮スポーン群へ適用した基準点（未適用なら `None`）。
    pub applied_origin: Option<[f32; 3]>,
    /// 半径ドラッグの状態（`None` なら通常のカーソル追従中）。
    pub radius_drag: Option<RadiusDrag>,
    /// 制御点への配置なら対象スロット、新規アクタ配置なら `None`。
    pub control_point: Option<ControlPointPlacement>,
}

impl PlacementMode {
    /// 配置の基準点。半径ドラッグ中は押下時に固定した中心、それ以外はカーソル着弾点。
    pub(super) fn origin(&self) -> Option<[f32; 3]> {
        match &self.radius_drag {
            Some(d) => Some(d.center),
            None => self.base,
        }
    }
}

// ============================================================
//  純関数（App に依存しない＝ユニットテスト可能な中核）
// ============================================================

/// 表面ヒット候補から基準点を 1 点に決める。
///
/// メッシュ・水面（GPU ピック）と地形（CPU レイマーチ）のうち**カメラに近い方**を採り、
/// どちらも無ければ `fallback`（カメラからレイ方向へ一定距離進んだ点）を返す。
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
/// いずれかが起きたら false。呼び出し側は false なら取り消す。
pub(super) fn placement_mode_still_valid(
    in_editor: bool,
    has_scene: bool,
    mode_world_line:   u32,
    active_world_line: u32,
) -> bool {
    in_editor && has_scene && mode_world_line == active_world_line
}

/// 基準点と点列から、配置のワールド位置を組み立てる。
///
/// 先頭 `max` 点までを返す（`usize::MAX` で全点）。接地は呼び出し側が別途行う。
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

/// ワールド座標を、与えられたアクタ行列のローカル空間へ落とす。
///
/// 制御点は**アクタ相対**のデータなので、カーソルのワールド着弾点は必ずここを通す。
/// 親子付け・回転・スケールはすべて `actor_mat`（＝アクタのワールド行列）に
/// 畳み込まれているため、逆行列を 1 回掛けるだけで正しいローカルになる。
pub(super) fn world_to_actor_local(actor_mat: [[f32; 4]; 4], world: [f32; 3]) -> [f32; 3] {
    transform_point(&mat4x4_inv(actor_mat), world)
}

/// アクタローカル座標をワールドへ持ち上げる（プレビュー描画用）。
pub(super) fn actor_local_to_world(actor_mat: &[[f32; 4]; 4], local: [f32; 3]) -> [f32; 3] {
    transform_point(actor_mat, local)
}

/// 制御点配置モードを続けてよいか。
///
/// 一般条件（`placement_mode_still_valid`）に加えて、**入れ先のスロットが
/// 生きていること**を要求する。配置モード中に対象アクタを消された場合、
/// 確定しても行き場が無いので、その場で取り消して知らせるほうが親切である
/// （黙って「クリックしても何も起きない」が最も分かりにくい）。
pub(super) fn control_point_mode_still_valid(general_valid: bool, target_alive: bool) -> bool {
    general_valid && target_alive
}

/// このパターンが「押下で中心固定 → ドラッグで半径調整」に対応するか。
///
/// 円形／円弧だけ。他のパターンは半径という概念そのものが無いので従来のクリック配置。
pub(super) fn pattern_supports_radius_drag(pattern: PlacementPattern) -> bool {
    matches!(pattern, PlacementPattern::Circle)
}

/// 中心とカーソル着弾点から半径を求める。
///
/// パターンは XZ 平面（2D はキャンバス XY を `[x,0,y]` に写したもの）に載るので、
/// 高さ（Y）を無視した水平距離を採る。丘の斜面でも「地図上の半径」で決まる。
pub(super) fn radius_from_drag(center: [f32; 3], cursor: [f32; 3]) -> f32 {
    let dx = cursor[0] - center[0];
    let dz = cursor[2] - center[2];
    (dx * dx + dz * dz).sqrt().max(RADIUS_DRAG_MIN)
}

/// カーソルが「クリック」ではなく「ドラッグ」と見なせるだけ動いたか。
pub(super) fn cursor_moved_enough(press: (f32, f32), now: (f32, f32)) -> bool {
    let dx = now.0 - press.0;
    let dy = now.1 - press.1;
    (dx * dx + dy * dy).sqrt() >= RADIUS_DRAG_MIN_PIXELS
}

/// 2 点が実質同じ位置か（再配置の要否判定）。
fn same_position(a: [f32; 3], b: [f32; 3]) -> bool {
    (0..3).all(|k| (a[k] - b[k]).abs() < PREVIEW_EPSILON)
}

/// アクタとその全子孫のノード数を数える（DFS id の連続範囲の長さ）。
fn count_actor_nodes(actor: &Actor) -> u32 {
    1 + actor.children().iter().map(count_actor_nodes).sum::<u32>()
}

/// ルートエンティティでアクタへの不変参照を得る（DFS 順の最初の一致）。
///
/// `actor_utils` には可変版しか無く、ここは読み取りだけなので局所に持つ。
fn find_actor_by_entity_ref(actors: &[Actor], entity: Entity) -> Option<&Actor> {
    for actor in actors {
        if actor.entity == entity { return Some(actor); }
        if let Some(found) = find_actor_by_entity_ref(actor.children(), entity) {
            return Some(found);
        }
    }
    None
}

/// カーソル脇に出す操作ガイドの行を組み立てる。
///
/// 半径ドラッグ中は「いま何 m か」を数字で出す。円は目分量では決めにくいので、
/// これが無いとドラッグ調整が「だいたい」でしか使えない。
/// 円形パターンでドラッグ前なら、半径ドラッグができること自体を 1 行で知らせる。
pub(super) fn guide_lines_for(
    pattern:  PlacementPattern,
    dragging: bool,
    radius:   f32,
    is_control_point: bool,
) -> Vec<String> {
    // 「何が置かれるのか」を毎行で言い換えず、動詞 1 語だけ差し替える。
    let verb = if is_control_point { "制御点を追加" } else { "配置" };
    if dragging {
        return vec![
            format!("ドラッグ: 半径 {radius:.2} m"),
            format!("離す: {verb}"),
        ];
    }
    let mut lines = vec![format!("左クリック: {verb} / 右クリック: 取消")];
    if pattern_supports_radius_drag(pattern) {
        lines.push("左ドラッグ: 半径を調整".to_string());
    }
    lines
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
    fn send_placement_state(&self, active: bool) {
        if let Some(ipc) = &self.ipc {
            ipc.send(if active { "PLACEMENT_STATE:1" } else { "PLACEMENT_STATE:0" });
        }
    }

    // ============================================================
    //  開始（仮スポーン）
    // ============================================================

    /// `LOGIC_PLACE_BEGIN:{json}` を処理して配置モードへ入る。
    ///
    /// 点列を作り、**生成予定のアクタ群を実際にシーンへ置く**（＝仮スポーン）。
    /// Undo 履歴は積まず、`SCENE_MODIFIED` も送らない（まだ確定していないため）。
    /// ヒエラルキーへは 1 回だけ反映する（ツリーに仮の行が見えるのは許容する）。
    pub(super) fn handle_logic_place_begin(&mut self, json: &str) {
        if self.scene.is_none() { return; }

        let req: LogicPlaceRequest = match serde_json::from_str(json) {
            Ok(r) => r,
            Err(e) => {
                self.notify_placement_error(&format!("ロジック配置の指定を解釈できません: {e}"));
                return;
            }
        };
        let result = generate_points(&req.spec);
        if let Some(w) = &result.warning {
            self.notify_placement_error(w);
        }
        if result.points.is_empty() {
            self.notify_placement_error("配置する点がありません（個数・行列数を確認してください）");
            return;
        }

        // 既に進行中なら**先に取り消して**仮スポーンを片付ける
        //（ダイアログを開き直した場合。捨てるだけでは前回の仮アクタが残る）。
        if self.placement_mode.is_some() {
            self.cancel_placement();
        }

        // 制御点への配置は実体を持たないので、専用の開始処理へ分ける
        //（仮スポーンもツリー・Undo のスナップショットも要らない）。
        if req.target == TARGET_CONTROL_POINTS {
            self.begin_control_point_placement(req, result.points);
            return;
        }

        // 開始直前のツリーを控える（確定時の Undo `before`）。
        let wl = self.active_world_line;
        let before_actors = self.snapshot_actors_for_wl(wl);

        // 初回の基準点はカーソル位置から「GPU ヒット無し」の前提で概算する
        // （ID バッファの読み戻しは非同期で、この時点では結果が無い）。
        // 次のフレームで正しい着弾点へ移動するので、1 フレームだけの近似で足りる。
        let origin = self.initial_placement_origin(&req);

        let Some((group, entities)) = self.spawn_placement_actors(&req, &result.points, origin)
        else {
            self.notify_placement_error("プレビュー用のアクタを生成できませんでした");
            return;
        };

        let preview_dfs_range = self.preview_dfs_range_for(wl, group);

        self.placement_mode = Some(PlacementMode {
            req,
            points: result.points,
            base: Some(origin),
            world_line: wl,
            before_actors,
            preview_group: Some(group),
            preview_entities: entities,
            preview_dfs_range,
            applied_origin: Some(origin),
            radius_drag: None,
            control_point: None,
        });
        // 掴み途中のギズモ・ホバー表示を落として、モードの排他を見た目にも反映する。
        self.hovered_gizmo_part = None;
        // 仮スポーンをツリーへ 1 回だけ反映する（毎フレームは送らない）。
        self.send_hierarchy();
        self.send_placement_state(true);
    }

    /// 制御点への配置モードへ入る（`target = control_points`）。
    ///
    /// アクタ配置との違いは 3 点だけ:
    ///   ・**実アクタを仮スポーンしない**（点は座標データであり実体を持たない。
    ///     仮の実体を置くと、取消し損ねたときにシーンへゴミが残る）
    ///   ・基準点は対象アクタの**ローカル座標**で持つ（点の座標系に合わせる）
    ///   ・プレビューはアイコン＋十字だけで描く
    ///
    /// 対象スロットが見つからない場合はモードへ入らない（先に弾く）。
    fn begin_control_point_placement(&mut self, req: LogicPlaceRequest, points: Vec<PlacementPoint>) {
        let target = ControlPointPlacement {
            actor_dfs_id: req.actor_dfs_id,
            slot_idx:     req.slot_idx,
        };
        if self.control_point_slot_entity(target.actor_dfs_id, target.slot_idx).is_none() {
            self.notify_placement_error("対象の ControlPoint スロットが見つかりません");
            return;
        }

        let wl = self.active_world_line;
        // 初回の基準点はカーソル位置から概算する（アクタ配置と同じ理由）。
        // ワールドで求めてから対象アクタのローカルへ落とす。
        let origin_world = self.initial_placement_origin(&req);
        let origin = self.world_to_placement_local(target.actor_dfs_id, origin_world);

        self.placement_mode = Some(PlacementMode {
            req,
            points,
            base: Some(origin),
            world_line: wl,
            before_actors: Vec::new(), // 仮スポーンしないので Undo の before は要らない
            preview_group: None,
            preview_entities: Vec::new(),
            preview_dfs_range: None,
            applied_origin: None,
            radius_drag: None,
            control_point: Some(target),
        });
        self.hovered_gizmo_part = None;
        self.send_placement_state(true);
    }

    /// ワールド座標を対象アクタのローカル座標へ落とす（制御点配置の座標変換）。
    ///
    /// 対象アクタの行列が引けない場合はワールド座標をそのまま返す
    /// （＝単位行列とみなす）。モードは `tick_placement_mode_guard` が
    /// 次のフレームで畳むので、ここで失敗を握り潰しても点は入らない。
    fn world_to_placement_local(&self, actor_dfs_id: u32, world: [f32; 3]) -> [f32; 3] {
        match self.control_point_actor_matrix(actor_dfs_id) {
            Some(m) => world_to_actor_local(m, world),
            None    => world,
        }
    }

    /// 仮スポーンしたグループのサブツリーが占める DFS id の範囲 `[start, end)` を求める。
    ///
    /// DFS id は「その世界線のアクタを深さ優先で数えた通し番号」なので、
    /// あるアクタのサブツリーは必ず**連続した範囲**を占める。
    /// グループ自身の id と、サブツリーのノード数だけで範囲が決まる。
    fn preview_dfs_range_for(&self, wl: u32, group: Entity) -> Option<(u32, u32)> {
        let scene = self.scene.as_ref()?;
        let start = dfs_ids_for_entities(&scene.actors, wl, &[group]).first().copied().flatten()?;
        let actor = find_actor_by_entity_ref(&scene.actors, group)?;
        Some((start, start + count_actor_nodes(actor)))
    }

    /// ピックした DFS id が仮スポーン（プレビュー）のものか。
    pub(super) fn placement_dfs_is_preview(&self, dfs_id: u32) -> bool {
        self.placement_mode
            .as_ref()
            .and_then(|m| m.preview_dfs_range)
            .is_some_and(|(start, end)| dfs_id >= start && dfs_id < end)
    }

    /// 仮スポーン時に使う暫定の基準点。
    ///
    /// 2D はキャンバス座標を直に求められる。3D は表面ヒットが未解決なので
    /// 「カメラからレイ方向へ一定距離」のフォールバックで置く。
    fn initial_placement_origin(&self, req: &LogicPlaceRequest) -> [f32; 3] {
        let Some((cx, cy)) = self.last_cursor_pos else { return [0.0; 3] };
        if req.is_2d {
            let p = self.window_to_canvas_2d(cx, cy);
            [p[0], 0.0, p[1]]
        } else {
            self.resolve_surface_or_camera_dist(None, cx.max(0.0) as u32, cy.max(0.0) as u32)
        }
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
    /// 制御点配置では、解決したワールド座標を**対象アクタのローカルへ変換して**持つ
    /// （点の座標系に合わせる。詳細は `PlacementMode::base` のコメント）。
    pub(super) fn resolve_placement_hover(&mut self, gpu_hit: Option<[f32; 3]>, sx: u32, sy: u32) {
        if self.placement_mode.is_none() { return; }
        let world = self.resolve_surface_or_camera_dist(gpu_hit, sx, sy);
        let base = self.placement_base_from_world(world);
        if let Some(mode) = self.placement_mode.as_mut() {
            mode.base = Some(base);
        }
    }

    /// カーソルのワールド着弾点を、いまのモードの基準点座標系へ写す。
    ///
    /// アクタ配置はワールドのまま、制御点配置は対象アクタのローカルへ。
    fn placement_base_from_world(&self, world: [f32; 3]) -> [f32; 3] {
        match self.placement_mode.as_ref().and_then(|m| m.control_point) {
            Some(cp) => self.world_to_placement_local(cp.actor_dfs_id, world),
            None     => world,
        }
    }

    /// 2D 配置の基準点をカーソルから直に求めて保持する（GPU 読み戻し不要）。
    pub(super) fn update_placement_hover_2d(&mut self) {
        let Some(mode) = self.placement_mode.as_ref() else { return };
        if !mode.req.is_2d { return; }
        let Some((cx, cy)) = self.last_cursor_pos else { return };
        let p = self.window_to_canvas_2d(cx, cy);
        let base = self.placement_base_from_world([p[0], 0.0, p[1]]);
        if let Some(mode) = self.placement_mode.as_mut() {
            mode.base = Some(base);
        }
    }

    // ============================================================
    //  仮スポーン群の毎フレーム更新
    // ============================================================

    /// 仮スポーンしたアクタ群をカーソル（または半径ドラッグ）に追従させる。
    ///
    /// フレーム先頭で 1 回だけ呼ぶ。実際に位置が変わったフレームだけ動かすので、
    /// カーソルが止まっている間は物理コライダー・BLAS の更新も起きない。
    pub(super) fn tick_placement_preview(&mut self) {
        if self.placement_mode.is_none() { return; }

        // ── ① 半径ドラッグ中なら、中心とカーソル着弾点から半径を引き直す ──
        self.update_placement_drag_radius();

        // ── ② 基準点が動いていれば仮スポーン群を移す ──
        // 制御点配置には仮スポーンが無いので、ここから先はまるごと不要。
        if self.placement_mode.as_ref().is_some_and(|m| m.control_point.is_some()) { return; }
        let Some(mode) = self.placement_mode.as_ref() else { return };
        let Some(origin) = mode.origin() else { return };
        if mode.applied_origin.is_some_and(|a| same_position(a, origin)) { return; }

        let positions = self.placement_world_positions_grounded(origin);
        self.apply_preview_positions(&positions);
        if let Some(mode) = self.placement_mode.as_mut() {
            mode.applied_origin = Some(origin);
        }
    }

    /// 半径ドラッグ中に半径を更新し、変わっていれば点列を作り直す。
    ///
    /// 点の**個数**は半径では変わらないので、仮スポーンし直す必要は無い
    ///（位置だけが変わる → ② の再配置で吸収される）。
    fn update_placement_drag_radius(&mut self) {
        let Some(mode) = self.placement_mode.as_ref() else { return };
        let Some(drag) = mode.radius_drag.as_ref() else { return };
        let Some(cursor) = mode.base else { return };
        let new_radius = radius_from_drag(drag.center, cursor);
        if (new_radius - mode.req.spec.radius).abs() < PREVIEW_EPSILON { return; }

        let Some(mode) = self.placement_mode.as_mut() else { return };
        mode.req.spec.radius = new_radius;
        mode.points = generate_points(&mode.req.spec).points;
        // 点列が変わったので、位置は必ず入れ直す。
        mode.applied_origin = None;
    }

    /// 基準点から全点のワールド位置を求める（3D で接地 ON なら点ごとに接地）。
    ///
    /// 接地は**確定時とまったく同じ関数**を使う。こうしておくと
    /// 「見えている高さに必ず置かれる」が構造的に保証される。
    fn placement_world_positions_grounded(&self, origin: [f32; 3]) -> Vec<[f32; 3]> {
        let Some(mode) = self.placement_mode.as_ref() else { return Vec::new() };
        let mut positions = placement_world_positions(origin, &mode.points, usize::MAX);
        if mode.req.ground && !mode.req.is_2d {
            let field = TerrainScatterField::from_state(&self.terrain);
            let _ = ground_positions_with(&field, &mut positions);
        }
        positions
    }

    /// 仮スポーン済みアクタを与えられた位置へ移す。
    ///
    /// 3D はギズモドラッグ・モーダルトランスフォームと**同じ経路**
    ///（`set_actor_world_transform` = 差分行列のサブツリー適用 + instance_mats 同期）
    /// を通す。アクタツリーのフル再構築は起こさない。
    /// 回転・スケールはスポーン時に適用済みなので、ここでは位置だけを触る
    ///（毎フレーム足し込むと回転・スケールが際限なく累積するため）。
    fn apply_preview_positions(&mut self, positions: &[[f32; 3]]) {
        let Some(mode) = self.placement_mode.as_ref() else { return };
        let is_2d = mode.req.is_2d;
        let entities: Vec<Entity> = mode.preview_entities.clone();
        let Some(scene) = self.scene.as_mut() else { return };
        // actors と world を**別々のフィールドとして**借りる（同時可変借用の回避）。
        let Scene { actors, world, .. } = scene;

        for (entity, position) in entities.iter().zip(positions.iter()) {
            let Some(actor) = find_actor_by_entity_mut(actors, *entity) else { continue };
            if is_2d {
                let mut ct = world.get::<CanvasTransform>(*entity).cloned().unwrap_or_default();
                ct.position = [position[0], position[2]];
                world.insert(*entity, ct);
            } else {
                let mut tf = world.get::<Transform>(*entity).cloned().unwrap_or_default();
                tf.position = *position;
                let _ = set_actor_world_transform(actor, world, tf, 0);
            }
        }
    }

    /// 仮スポーン中アクタのワールド位置（アイコンオーバーレイ用。3D のみ）。
    ///
    /// 「空アクタを選択したときに出る人型アイコン」を、選択状態にせずに
    /// プレビュー対象へも出すための座標源。空アクタは見た目を持たないので、
    /// これが無いと「何個どこに置かれるか」がまったく見えない。
    pub(super) fn placement_preview_icon_positions(&self) -> Vec<[f32; 3]> {
        let Some(mode) = self.placement_mode.as_ref() else { return Vec::new() };
        if mode.req.is_2d { return Vec::new() }

        // ── 制御点配置: 実体が無いので、生成予定の点をその場で組んで返す ──
        // 点はアクタローカルなので、対象アクタの行列でワールドへ持ち上げる。
        if let Some(cp) = mode.control_point {
            let Some(origin) = mode.origin() else { return Vec::new() };
            let Some(m) = self.control_point_actor_matrix(cp.actor_dfs_id) else { return Vec::new() };
            return placement_world_positions(origin, &mode.points, usize::MAX)
                .into_iter()
                .map(|p| actor_local_to_world(&m, p))
                .collect();
        }

        let Some(scene) = self.scene.as_ref() else { return Vec::new() };
        mode.preview_entities
            .iter()
            .filter_map(|e| scene.world.get::<Transform>(*e).map(|t| t.position))
            .collect()
    }

    /// プレビューアイコンの色。配置対象で色を変えて取り違えを防ぐ。
    pub(super) fn placement_preview_icon_tint(&self) -> [f32; 4] {
        match self.placement_mode.as_ref().and_then(|m| m.control_point) {
            Some(_) => CONTROL_POINT_PREVIEW_ICON_TINT,
            None    => PREVIEW_ICON_TINT,
        }
    }

    // ============================================================
    //  半径ドラッグ（円形／円弧）
    // ============================================================

    /// 左ボタン押下。円形パターンなら半径ドラッグを開始し、それ以外は即確定する。
    pub(super) fn on_placement_left_press(&mut self) {
        let Some(mode) = self.placement_mode.as_ref() else { return };
        let draggable = pattern_supports_radius_drag(mode.req.spec.pattern);
        let base = mode.base;
        let cursor = self.last_cursor_pos;

        match (draggable, base, cursor) {
            // 円形＋基準点解決済み＋カーソル既知 → 中心を固定してドラッグへ入る。
            (true, Some(center), Some(press_cursor)) => {
                let start_radius = mode.req.spec.radius;
                if let Some(mode) = self.placement_mode.as_mut() {
                    mode.radius_drag = Some(RadiusDrag {
                        center,
                        press_cursor,
                        start_radius,
                        dragged: false,
                    });
                }
            }
            // それ以外は従来どおり「押した時点で配置」。
            _ => self.confirm_placement(),
        }
    }

    /// 左ボタン解放。半径ドラッグ中なら、その半径（またはクリック扱い）で確定する。
    pub(super) fn on_placement_left_release(&mut self) {
        let Some(mode) = self.placement_mode.as_ref() else { return };
        let Some(drag) = mode.radius_drag.as_ref() else { return }; // ドラッグ中でなければ何もしない
        let dragged = drag.dragged;
        let start_radius = drag.start_radius;

        if !dragged {
            // クリック扱い: ダイアログの半径へ戻してから確定する
            //（ドラッグ判定未満のわずかな移動で半径が変わってしまうのを防ぐ）。
            if let Some(mode) = self.placement_mode.as_mut() {
                mode.req.spec.radius = start_radius;
                mode.points = generate_points(&mode.req.spec).points;
                mode.applied_origin = None;
            }
            let origin = self.placement_mode.as_ref().and_then(|m| m.origin());
            if let Some(origin) = origin {
                let positions = self.placement_world_positions_grounded(origin);
                self.apply_preview_positions(&positions);
            }
        } else {
            // ドラッグで決めた半径をエディタへ返す（次回ダイアログの既定値になる）。
            let radius = self.placement_mode.as_ref().map_or(0.0, |m| m.req.spec.radius);
            self.send_placement_radius(radius);
        }
        self.confirm_placement();
    }

    /// カーソル移動時に「クリックかドラッグか」を判定する。
    ///
    /// 半径そのものは基準点の解決（GPU 読み戻し）を待って
    /// `update_placement_drag_radius` が引き直す。ここでは画面上の移動量だけを見る。
    pub(super) fn update_placement_drag_flag(&mut self, cx: f32, cy: f32) {
        let Some(mode) = self.placement_mode.as_mut() else { return };
        let Some(drag) = mode.radius_drag.as_mut() else { return };
        if drag.dragged { return; }
        if cursor_moved_enough(drag.press_cursor, (cx, cy)) {
            drag.dragged = true;
        }
    }

    /// ドラッグで決めた半径をエディタへ通知する（`PLACEMENT_RADIUS:{値}`）。
    fn send_placement_radius(&self, radius: f32) {
        if let Some(ipc) = &self.ipc {
            ipc.send(&format!("PLACEMENT_RADIUS:{radius}"));
        }
    }

    // ============================================================
    //  確定 / 取消
    // ============================================================

    /// 確定。仮スポーンをそのまま採用し、Undo 1 件として記録する。
    ///
    /// 生成物はもうシーンに居るので、ここで作り直すことはしない
    ///（＝プレビューで見えていた物がそのまま残る）。
    pub(super) fn confirm_placement(&mut self) {
        let Some(mode) = self.placement_mode.take() else { return };
        self.send_placement_state(false);

        // 制御点への配置は既存の追記経路へ流す（Undo 1 件・上限切り詰め警告つき）。
        if mode.control_point.is_some() {
            let origin = mode.origin().unwrap_or([0.0; 3]);
            self.place_control_points(&mode.req, &mode.points, origin);
            return;
        }

        let wl = mode.world_line;
        let entities = mode.preview_entities.clone();

        // ── Undo 1 件（開始前 → 現在。Undo 1 回で仮スポーンごと消える）──
        let after_actors = self.snapshot_actors_for_wl(wl);
        self.undo_history.record(Box::new(ActorTreeSnapshotCommand {
            world_line: wl,
            before_actors: mode.before_actors,
            after_actors,
        }));

        if let Some(ipc) = &self.ipc { ipc.send("SCENE_MODIFIED"); }
        // 生成した全アクタを選択状態にする（置いた直後にギズモで動かせるように）。
        self.select_placed_actors(wl, &entities);
    }

    /// 取消。仮スポーンしたグループを削除してモードを抜ける。
    ///
    /// Undo 履歴は積まず、`SCENE_MODIFIED` も送らない
    ///（開始前と同じツリーへ戻るので、シーンは汚れていない）。
    pub(super) fn cancel_placement(&mut self) {
        let Some(mode) = self.placement_mode.take() else { return };
        self.send_placement_state(false);

        // 制御点への配置は何も置いていないので、状態を捨てるだけで元通り。
        // ここで選択を解除してはいけない（対象アクタの選択が外れると、
        // 既存の点キューブまで消えて「取り消したら点が全部消えた」ように見える）。
        if mode.control_point.is_some() { return; }

        self.despawn_placement_preview(mode.preview_group);

        // 仮スポーンが選択されたまま残らないようにする。
        self.selected_instances.clear();
        self.actor_virtual_selected_idx = None;
        self.selected_actor_dfs_ids.clear();
        if let Some(ipc) = &self.ipc { ipc.send("SELECTED:-1"); }
        self.update_canvas_wl_state_for(mode.world_line);
        // 仮スポーンを外したツリーを 1 回だけ送る（毎フレームは送らない）。
        self.send_hierarchy();
    }

    /// 仮スポーンしたグループをサブツリーごとシーンから取り除く。
    fn despawn_placement_preview(&mut self, group: Option<Entity>) {
        let Some(group) = group else { return };
        let Some(scene) = self.scene.as_mut() else { return };
        let Some(actor) = extract_actor_by_entity(&mut scene.actors, group) else { return };
        despawn_actor_recursive(&actor, &mut scene.world);
    }

    /// 毎フレームの前提条件チェック。
    ///
    /// Play 開始・シーン破棄・世界線（タブ）切り替えが起きたら**取り消す**
    /// （仮スポーンも一緒に片付ける）。
    pub(super) fn tick_placement_mode_guard(&mut self) {
        let Some(mode) = self.placement_mode.as_ref() else { return };
        let still_valid = placement_mode_still_valid(
            self.mode == RuntimeMode::Edit || self.paused,
            self.scene.is_some(),
            mode.world_line,
            self.active_world_line,
        );
        // 制御点配置は「対象スロットが生きていること」も前提に加わる。
        // 配置モード中に対象アクタを消された／コンポーネントを外された場合、
        // 確定しても入れ先が無いので、その場で取り消す
        //（気付かずクリックして「何も起きない」となるほうが分かりにくい）。
        let target_alive = match mode.control_point {
            Some(cp) => self.control_point_slot_entity(cp.actor_dfs_id, cp.slot_idx).is_some(),
            None     => true,
        };
        if control_point_mode_still_valid(still_valid, target_alive) { return; }
        self.cancel_placement();
    }

    // ============================================================
    //  プレビュー描画（線）
    // ============================================================

    /// 配置モードの補助線を組む（描くものが無ければ None）。
    ///
    /// **点ごとのマーカーはもう描かない**。仮スポーンした実物がそこに見えるので、
    /// 線マーカーを重ねると二重像になって却って読みにくい。ここで描くのは
    ///   ・基準点（カーソルの着弾点＝パターンのアンカー）の十字
    ///   ・半径ドラッグ中の円（いま決めている半径）
    /// の 2 つだけに絞る。
    ///
    /// フレームループがレンダラを可変借用する**前**に呼ぶこと。
    pub(super) fn build_placement_preview_line_batch(&self) -> Option<LineBatch> {
        let mode = self.placement_mode.as_ref()?;
        let origin = mode.origin()?;
        let is_2d = mode.req.is_2d;

        // 制御点配置では基準点も点列もアクタローカルなので、描く直前に
        // 対象アクタの行列でワールドへ持ち上げる（アクタ配置では恒等変換）。
        let to_world = self.placement_local_to_world_matrix();
        let lift = |p: [f32; 3]| match &to_world {
            Some(m) => actor_local_to_world(m, p),
            None    => p,
        };

        let mut lb = LineBatch::new();
        // 基準点（カーソルの着弾点）を大きめの十字で。パターンのどこが
        // カーソルに吸い付いているか（＝アンカー）が一目で分かるようにする。
        let origin_world = lift(origin);
        let base_draw = if is_2d { [origin_world[0], origin_world[2], 0.0] } else { origin_world };
        let base_half = self.placement_marker_half(origin_world, is_2d) * BASE_MARKER_SCALE;
        add_marker(&mut lb, base_draw, base_half, is_2d, BASE_COLOR);

        // 半径ドラッグ中は、いま決めている半径の円を描く。
        if mode.radius_drag.is_some() {
            add_radius_circle(&mut lb, origin, mode.req.spec.radius, is_2d, &lift);
        }

        if lb.is_empty() { None } else { Some(lb) }
    }

    /// 基準点・点列の座標系をワールドへ写す行列（アクタ配置なら `None` ＝恒等）。
    fn placement_local_to_world_matrix(&self) -> Option<[[f32; 4]; 4]> {
        let cp = self.placement_mode.as_ref()?.control_point?;
        self.control_point_actor_matrix(cp.actor_dfs_id)
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

    // ============================================================
    //  操作ガイド（カーソル近くのテキスト）
    // ============================================================

    /// カーソル脇に出す操作ガイドの行。配置モードでなければ空。
    pub(super) fn placement_guide_lines(&self) -> Vec<String> {
        let Some(mode) = self.placement_mode.as_ref() else { return Vec::new() };
        let dragging = mode.radius_drag.as_ref().is_some_and(|d| d.dragged);
        guide_lines_for(
            mode.req.spec.pattern,
            dragging,
            mode.req.spec.radius,
            mode.control_point.is_some(),
        )
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

/// 半径ドラッグ中の円を追加する（中心 `center`・半径 `radius`）。
///
/// パターンは XZ 平面に載るので、3D は XZ 平面の円、2D はキャンバス XY の円を描く。
///
/// `lift` はパターン座標系 → ワールドの変換。制御点配置ではパターンが
/// **アクタのローカル空間**に載るため、円もアクタと一緒に回っていないと
/// 「見えている円と実際に入る点」がずれる。アクタ配置では恒等関数を渡す。
fn add_radius_circle(
    lb:     &mut LineBatch,
    center: [f32; 3],
    radius: f32,
    is_2d:  bool,
    lift:   &impl Fn([f32; 3]) -> [f32; 3],
) {
    let step = std::f32::consts::TAU / RADIUS_CIRCLE_SEGMENTS as f32;
    // パターン座標 → ワールド → 描画空間（2D はパターンの Z がキャンバス Y に写る）。
    let to_draw = |p: [f32; 3]| -> [f32; 3] {
        let w = lift(p);
        if is_2d { [w[0], w[2], 0.0] } else { w }
    };
    let point_at = |a: f32| -> [f32; 3] {
        let (s, co) = (a.sin(), a.cos());
        to_draw([center[0] + s * radius, center[1], center[2] + co * radius])
    };
    let mut prev = point_at(0.0);
    for i in 1..=RADIUS_CIRCLE_SEGMENTS {
        let next = point_at(step * i as f32);
        lb.add_line(prev, next, RADIUS_CIRCLE_COLOR);
        prev = next;
    }
    // 中心から円周へ 1 本引いて「これが半径」であることを示す。
    lb.add_line(to_draw(center), point_at(0.0), RADIUS_CIRCLE_COLOR);
}

// ============================================================
//  テスト — App を組まずに配置モードの中核（純関数と状態機械）を検証する
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;
    use crate::engine::placement::PlacementSpec;

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
        let mut req = LogicPlaceRequest::default();
        req.spec = spec;
        PlacementMode {
            req,
            points,
            base,
            world_line: TEST_WL,
            before_actors: Vec::new(),
            preview_group: None,
            preview_entities: Vec::new(),
            preview_dfs_range: None,
            applied_origin: None,
            radius_drag: None,
            control_point: None,
        }
    }

    /// 円形パターンの配置モードを作る（半径ドラッグのテスト用）。
    fn circle_mode(radius: f32) -> PlacementMode {
        let spec = PlacementSpec {
            pattern: PlacementPattern::Circle,
            count: 6,
            radius,
            ..Default::default()
        };
        let points = generate_points(&spec).points;
        let mut req = LogicPlaceRequest::default();
        req.spec = spec;
        PlacementMode {
            req,
            points,
            base: Some([0.0; 3]),
            world_line: TEST_WL,
            before_actors: Vec::new(),
            preview_group: None,
            preview_entities: Vec::new(),
            preview_dfs_range: None,
            applied_origin: None,
            radius_drag: None,
            control_point: None,
        }
    }

    // ─── 状態遷移 ─────────────────────────────────────────

    /// **begin**: 開始直後は点列が固定されること。
    #[test]
    fn begin_fixes_points() {
        let m = mode_with_base(None);
        assert_eq!(m.points.len(), 9, "3×3 の点列が開始時に確定していること");
        assert!(m.base.is_none(), "カーソル解決前は基準点なし");
        assert!(m.applied_origin.is_none(), "まだ仮スポーンへ適用していない");
    }

    /// **hover**: 基準点が解決されると、全点がその位置ぶん平行移動すること。
    #[test]
    fn hover_translates_every_point_by_the_base() {
        let m = mode_with_base(Some([10.0, 3.0, -5.0]));
        let base = m.origin().expect("基準点は解決済み");
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
    #[test]
    fn hover_updates_base_without_regenerating_points() {
        let mut m = mode_with_base(Some([0.0, 0.0, 0.0]));
        let before = m.points.clone();
        m.base = Some([7.0, 0.0, 7.0]);
        assert_eq!(m.points, before, "点列は再生成されないこと");
        assert_eq!(m.origin(), Some([7.0, 0.0, 7.0]));
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

    // ─── 半径ドラッグ ─────────────────────────────────────

    /// 半径ドラッグに対応するのは円形パターンだけであること。
    #[test]
    fn only_circle_supports_radius_drag() {
        assert!(pattern_supports_radius_drag(PlacementPattern::Circle));
        assert!(!pattern_supports_radius_drag(PlacementPattern::Grid));
        assert!(!pattern_supports_radius_drag(PlacementPattern::Line));
        assert!(!pattern_supports_radius_drag(PlacementPattern::Random));
    }

    /// 半径は中心とカーソルの**水平距離**であること（高さは無視する）。
    #[test]
    fn radius_is_the_horizontal_distance_from_the_center() {
        let r = radius_from_drag([1.0, 0.0, 2.0], [4.0, 50.0, 6.0]);
        assert!((r - 5.0).abs() < 1.0e-4, "3-4-5 の水平距離になること: {r}");
    }

    /// 中心とカーソルが重なっても半径 0 にはならないこと（点が全部重なるのを防ぐ）。
    #[test]
    fn radius_never_collapses_to_zero() {
        let r = radius_from_drag([1.0, 2.0, 3.0], [1.0, 2.0, 3.0]);
        assert!(r >= RADIUS_DRAG_MIN, "最小半径で下支えされること: {r}");
    }

    /// 閾値未満の移動はクリック、閾値以上はドラッグと判定されること。
    #[test]
    fn small_moves_are_clicks_and_large_moves_are_drags() {
        let press = (100.0, 100.0);
        assert!(!cursor_moved_enough(press, (101.0, 101.0)), "手の震え程度はクリック");
        assert!(cursor_moved_enough(press, (100.0 + RADIUS_DRAG_MIN_PIXELS, 100.0)),
                "閾値ちょうどはドラッグ");
        assert!(cursor_moved_enough(press, (140.0, 130.0)), "大きく動けばドラッグ");
    }

    /// **ドラッグ中は中心が固定される**こと（カーソルが動いても基準点は動かない）。
    #[test]
    fn drag_freezes_the_origin_at_the_press_position() {
        let mut m = circle_mode(5.0);
        m.radius_drag = Some(RadiusDrag {
            center: [3.0, 0.0, 4.0],
            press_cursor: (10.0, 10.0),
            start_radius: 5.0,
            dragged: false,
        });
        m.base = Some([99.0, 0.0, 99.0]); // カーソルは遠くへ動いた
        assert_eq!(m.origin(), Some([3.0, 0.0, 4.0]), "配置の基準点は押下時の中心のまま");
    }

    /// 半径を変えても**点の個数は変わらない**こと（仮スポーンし直しが不要な根拠）。
    #[test]
    fn changing_the_radius_keeps_the_point_count() {
        let small = circle_mode(1.0);
        let large = circle_mode(20.0);
        assert_eq!(small.points.len(), large.points.len(), "個数は半径に依らない");
        // 半径が大きいほど原点から遠いこと（実際に反映されている確認）。
        let d = |m: &PlacementMode| {
            let p = m.points[0].position;
            (p[0] * p[0] + p[2] * p[2]).sqrt()
        };
        assert!(d(&large) > d(&small), "半径が反映されていること");
    }

    // ─── ガイド文言 ───────────────────────────────────────

    /// 通常時のガイドは「左クリック: 配置 / 右クリック: 取消」を出すこと。
    #[test]
    fn guide_shows_click_and_cancel_by_default() {
        let lines = guide_lines_for(PlacementPattern::Grid, false, 5.0, false);
        assert_eq!(lines.len(), 1, "半径ドラッグ非対応パターンは 1 行だけ");
        assert!(lines[0].contains("左クリック") && lines[0].contains("右クリック"));
    }

    /// 円形パターンでは、ドラッグ前でも半径ドラッグができることを知らせること。
    #[test]
    fn guide_advertises_radius_drag_for_circles() {
        let lines = guide_lines_for(PlacementPattern::Circle, false, 5.0, false);
        assert_eq!(lines.len(), 2);
        assert!(lines[1].contains("半径"), "半径ドラッグの案内が出ること");
    }

    /// 半径ドラッグ中は、いまの半径の数値と「離す: 配置」を出すこと。
    #[test]
    fn guide_shows_the_live_radius_while_dragging() {
        let lines = guide_lines_for(PlacementPattern::Circle, true, 12.345, false);
        assert_eq!(lines.len(), 2);
        assert!(lines[0].contains("12.35"), "小数 2 桁で現在値が出ること: {}", lines[0]);
        assert!(lines[1].contains("離す"), "離せば配置される旨が出ること");
    }

    // ─── 取消（仮スポーンの撤去）─────────────────────────

    /// **取消**: 仮スポーンしたグループだけが消え、開始前のツリーと一致すること。
    ///
    /// 「アクタ数が戻る」だけでなく「元から居たアクタが誰も巻き添えにならない」
    /// ことまで確かめる（取消はプレビュー中の他の編集を壊してはいけない）。
    #[test]
    fn cancel_removes_exactly_the_preview_group() {
        use crate::engine::ecs::World;
        use crate::engine::structs::objects::Actor;

        let mut world = World::new();
        let mut actors: Vec<Actor> = Vec::new();

        // 開始前のツリー（既存アクタ 2 体）。
        for name in ["既存A", "既存B"] {
            let e = world.spawn();
            actors.push(Actor::new_folder(e, name.to_string()));
        }
        let before: Vec<String> = actors.iter().map(|a| a.name.clone()).collect();

        // 仮スポーン（グループ 1 + 子 3）。
        let group_entity = world.spawn();
        let mut group = Actor::new_folder(group_entity, "円形配置".to_string());
        let mut child_entities = Vec::new();
        for i in 0..3 {
            let e = world.spawn();
            child_entities.push(e);
            group.children_mut().push(Actor::new_folder(e, format!("円形配置_{i}")));
        }
        actors.push(group);
        assert_eq!(actors.len(), 3, "仮スポーン後はグループが 1 本増えること");

        // 取消（`cancel_placement` が行うのと同じ 2 操作）。
        let removed = extract_actor_by_entity(&mut actors, group_entity)
            .expect("仮スポーンしたグループが見つかること");
        despawn_actor_recursive(&removed, &mut world);

        let after: Vec<String> = actors.iter().map(|a| a.name.clone()).collect();
        assert_eq!(after.len(), before.len(), "アクタ数が開始前と一致すること");
        assert_eq!(after, before, "元から居たアクタが順序ごと保たれること");
        for e in child_entities {
            assert!(world.get::<Transform>(e).is_none(), "子のエンティティも破棄されること");
        }
    }

    // ─── 自己ヒットの除外（DFS 範囲）─────────────────────

    /// 仮スポーンのサブツリーが占める DFS 範囲の長さが、
    /// **グループ＋全子孫のノード数**になること。
    ///
    /// この長さがずれると、プレビュー自身へのピックを取りこぼして
    /// 「カーソルへ這い寄る」自己参照ループが復活する。
    #[test]
    fn preview_dfs_range_covers_the_whole_subtree() {
        use crate::engine::ecs::World;
        use crate::engine::structs::objects::Actor;

        let mut world = World::new();
        let mut group = Actor::new_folder(world.spawn(), "円形配置".to_string());
        for i in 0..3 {
            // 各子はさらに 2 段のサブツリーを持つ（アクタファイル由来を模す）。
            let mut child = Actor::new_folder(world.spawn(), format!("円形配置_{i}"));
            child.children_mut().push(Actor::new_folder(world.spawn(), "mesh".to_string()));
            child.children_mut().push(Actor::new_folder(world.spawn(), "collider".to_string()));
            group.children_mut().push(child);
        }
        // グループ 1 + 子 3 + 孫 6 = 10
        assert_eq!(count_actor_nodes(&group), 10);
    }

    /// 単独のアクタは 1 ノードと数えること（境界）。
    #[test]
    fn a_leaf_actor_counts_as_one_node() {
        use crate::engine::ecs::World;
        use crate::engine::structs::objects::Actor;
        let mut world = World::new();
        assert_eq!(count_actor_nodes(&Actor::new_folder(world.spawn(), "x".to_string())), 1);
    }

    // ─── 再配置の要否判定 ─────────────────────────────────

    /// 実質同じ位置なら再配置しないこと（毎フレームの無駄な更新を避ける根拠）。
    #[test]
    fn identical_positions_skip_reapply() {
        assert!(same_position([1.0, 2.0, 3.0], [1.0, 2.0, 3.0]));
        assert!(same_position([1.0, 2.0, 3.0], [1.0 + PREVIEW_EPSILON * 0.5, 2.0, 3.0]));
        assert!(!same_position([1.0, 2.0, 3.0], [1.1, 2.0, 3.0]));
    }

    // ════════════════════════════════════════════════════════
    //  制御点への配置モード
    // ════════════════════════════════════════════════════════

    /// テスト用の対象スロット。
    const TEST_CP: ControlPointPlacement = ControlPointPlacement { actor_dfs_id: 3, slot_idx: 1 };

    /// 3×3 グリッドの**制御点配置**モードを作る（基準点はアクタローカル）。
    fn cp_mode(base: Option<[f32; 3]>) -> PlacementMode {
        let mut m = mode_with_base(base);
        m.req.target = TARGET_CONTROL_POINTS.to_string();
        m.req.actor_dfs_id = TEST_CP.actor_dfs_id;
        m.req.slot_idx = TEST_CP.slot_idx;
        m.control_point = Some(TEST_CP);
        m
    }

    /// 位置・回転（度）・スケールからアクタのワールド行列を作る。
    fn actor_mat(position: [f32; 3], rotation: [f32; 3], scale: [f32; 3]) -> [[f32; 4]; 4] {
        Transform { position, rotation, scale, ..Default::default() }.to_mat4()
    }

    /// 2 点がほぼ一致すること（浮動小数の比較）。
    fn assert_close(a: [f32; 3], b: [f32; 3], what: &str) {
        for k in 0..3 {
            assert!((a[k] - b[k]).abs() < 1.0e-3, "{what}: {a:?} != {b:?}");
        }
    }

    // ─── 状態遷移（begin → hover → confirm / cancel）───────

    /// **begin**: 実体を仮スポーンせず、対象スロットだけを握ること。
    #[test]
    fn cp_begin_spawns_nothing_and_remembers_the_slot() {
        let m = cp_mode(None);
        assert_eq!(m.control_point, Some(TEST_CP), "対象スロットを保持すること");
        assert!(m.preview_group.is_none(), "仮スポーンのグループを作らないこと");
        assert!(m.preview_entities.is_empty(), "実アクタを 1 体も作らないこと");
        assert!(m.preview_dfs_range.is_none(), "自己ヒット除外の範囲も不要");
        assert!(m.before_actors.is_empty(), "ツリーのスナップショットを取らないこと");
        assert_eq!(m.points.len(), 9, "点列は開始時に確定していること");
    }

    /// **hover**: 基準点（ローカル）が入れば、全点がそこから展開されること。
    #[test]
    fn cp_hover_expands_points_from_the_local_base() {
        let m = cp_mode(Some([2.0, 1.0, -3.0]));
        let base = m.origin().expect("基準点は解決済み");
        let local = placement_world_positions(base, &m.points, usize::MAX);
        assert_eq!(local.len(), 9);
        // アンカー 0.5/0.5 の 3×3 なので、基準点そのものに乗る点が 1 つある。
        assert!(local.iter().any(|p| (p[0] - 2.0).abs() < 1.0e-4 && (p[2] + 3.0).abs() < 1.0e-4));
    }

    /// **confirm/cancel の分岐条件**: 制御点モードかどうかで確定・取消の経路が変わること。
    ///
    /// 実際の確定は `App` を要するのでここでは呼べないが、分岐の根拠になる
    /// フラグ（`control_point`）が両モードで排他であることは固定しておく。
    #[test]
    fn cp_and_actor_modes_are_mutually_exclusive() {
        assert!(cp_mode(None).control_point.is_some(), "制御点モード");
        assert!(mode_with_base(None).control_point.is_none(), "アクタ配置モード");
    }

    /// **対象アクタ消失で自動取消**: 一般条件が満たされていても、
    /// スロットが消えていればモードを続けないこと。
    #[test]
    fn cp_mode_is_cancelled_when_the_target_disappears() {
        assert!(control_point_mode_still_valid(true, true), "対象が生きていれば継続");
        assert!(!control_point_mode_still_valid(true, false), "対象が消えたら取消");
        assert!(!control_point_mode_still_valid(false, true), "Play 開始等でも取消");
    }

    // ─── ワールド → アクタローカル変換 ───────────────────

    /// 平行移動だけのアクタでは、ローカル＝ワールド − アクタ位置になること。
    #[test]
    fn world_to_local_subtracts_the_actor_translation() {
        let m = actor_mat([10.0, 5.0, -2.0], [0.0; 3], [1.0; 3]);
        assert_close(world_to_actor_local(m, [12.0, 5.0, 0.0]), [2.0, 0.0, 2.0], "平行移動");
    }

    /// **回転ありのアクタ**: ワールドの +X はアクタ Y 軸 90° 回転でローカル -Z になること。
    ///
    /// SEED の前方向は +Z・左手系で、Y 回転 90° は +Z を +X へ向ける。
    /// よって逆変換ではワールド +X がローカル +Z … ではなく、
    /// 「行って戻る」ことだけを検証して回転規約への二重管理を避ける。
    #[test]
    fn world_to_local_round_trips_through_rotation() {
        let m = actor_mat([3.0, -1.0, 4.0], [0.0, 90.0, 0.0], [1.0; 3]);
        let world = [7.0, 2.0, -5.0];
        let local = world_to_actor_local(m, world);
        assert_ne!(local, world, "回転していれば座標は変わること");
        assert_close(actor_local_to_world(&m, local), world, "往復で元へ戻ること");
    }

    /// **親あり・回転・スケール込み**でも往復すること。
    ///
    /// 親子付けはアクタのワールド行列に畳み込まれているので、
    /// 「親の分だけ別に足す」処理は不要である（それが必要になっていたら回帰）。
    #[test]
    fn world_to_local_round_trips_with_rotation_and_scale() {
        let m = actor_mat([-8.0, 12.0, 0.5], [15.0, -40.0, 25.0], [2.0, 0.5, 3.0]);
        for world in [[0.0; 3], [1.0, 2.0, 3.0], [-30.0, 7.5, 100.0]] {
            let local = world_to_actor_local(m, world);
            assert_close(actor_local_to_world(&m, local), world, "往復");
        }
    }

    /// スケール付きアクタでは、ローカルの 1 単位がワールドのスケール倍になること
    /// （＝パターンの間隔がアクタのスケールに追従する）。
    #[test]
    fn local_units_follow_the_actor_scale() {
        let m = actor_mat([0.0; 3], [0.0; 3], [2.0, 1.0, 1.0]);
        assert_close(actor_local_to_world(&m, [1.0, 0.0, 0.0]), [2.0, 0.0, 0.0], "X 2 倍");
        assert_close(world_to_actor_local(m, [2.0, 0.0, 0.0]), [1.0, 0.0, 0.0], "逆も 1/2");
    }

    // ─── 半径ドラッグ（制御点モードでも同一挙動）───────────

    /// 制御点モードでも、押下時の中心が固定されること。
    #[test]
    fn cp_radius_drag_freezes_the_center_like_actor_placement() {
        let mut m = cp_mode(Some([0.0; 3]));
        m.req.spec.pattern = PlacementPattern::Circle;
        m.radius_drag = Some(RadiusDrag {
            center: [1.0, 0.0, 2.0],
            press_cursor: (50.0, 50.0),
            start_radius: 3.0,
            dragged: true,
        });
        m.base = Some([40.0, 0.0, 40.0]); // カーソルは遠くへ
        assert_eq!(m.origin(), Some([1.0, 0.0, 2.0]), "基準点は押下時の中心のまま");
        // 半径はローカル空間の水平距離で決まる（アクタ配置と同じ関数）。
        let r = radius_from_drag([1.0, 0.0, 2.0], [4.0, 0.0, 6.0]);
        assert!((r - 5.0).abs() < 1.0e-4, "3-4-5: {r}");
    }

    // ─── ガイド文言 ───────────────────────────────────────

    /// 制御点モードのガイドは「配置」ではなく「制御点を追加」と言うこと。
    #[test]
    fn cp_guide_says_it_adds_control_points() {
        let lines = guide_lines_for(PlacementPattern::Grid, false, 5.0, true);
        assert!(lines[0].contains("制御点を追加"), "何が起きるか明示すること: {}", lines[0]);
        assert!(lines[0].contains("右クリック"), "取消の案内も残ること");
        let dragging = guide_lines_for(PlacementPattern::Circle, true, 2.0, true);
        assert!(dragging[1].contains("制御点を追加"), "離したときの結果も制御点であること");
    }

    /// アクタ配置のガイド文言は変わっていないこと（回帰）。
    #[test]
    fn actor_guide_text_is_unchanged() {
        let lines = guide_lines_for(PlacementPattern::Grid, false, 5.0, false);
        assert_eq!(lines[0], "左クリック: 配置 / 右クリック: 取消");
    }

    // ─── プレビューの色分け ───────────────────────────────

    /// 2 つのプレビュー色が実際に違うこと（取り違え防止の担保）。
    #[test]
    fn preview_tints_differ_between_targets() {
        assert_ne!(PREVIEW_ICON_TINT, CONTROL_POINT_PREVIEW_ICON_TINT);
        // どちらも半透明（＝「まだ仮」）であること。
        assert!(PREVIEW_ICON_TINT[3] < 1.0 && CONTROL_POINT_PREVIEW_ICON_TINT[3] < 1.0);
    }
}
