// ============================================================
//  canvas_collect.rs — キャンバスアクター情報収集ユーティリティ
//
//  スプライト描画アイテム・キャンバス矩形アウトライン・ID パスアイテムを
//  アクターツリーから DFS 順に収集するためのフリー関数群。
//
//  フレームループ (frame.rs の on_redraw_requested) から呼び出される。
//  各ノード自身の CanvasTransform のスケールモード (scale_transform / scale_size) に
//  応じた累積スケール伝播を親子間で管理する。
// ============================================================

use std::collections::HashMap;
use std::sync::Arc;

use crate::engine::components::{
    AspectRatioAxis, CameraComponent, CanvasComponent, CanvasDrawZone, CanvasTransform,
    CanvasViewportRef, ComponentKind, ScalingMode, SkinnedSpriteComponent, SpriteComponent,
    TextComponent, Transform as ActorTransform,
};
use crate::engine::core::font::canvas_text::CanvasTextItem;
use crate::engine::core::loader::sprite_mesh::SpriteMesh;
use crate::engine::core::renderer::SpriteDrawItem;
use crate::engine::core::renderer::sprite_skin::{SkinnedSpriteDraw, resolve_bone};
use crate::engine::ecs::{Entity, World};
use crate::engine::methods::drawer::{
    DrawContext, GpuSpriteTexture, LineBatch, load_sprite_texture,
};
use crate::engine::methods::gizmo_interact::mat4x4_mul;
use crate::engine::structs::objects::Actor;

/// キャンバス座標（ピクセル）→ 3D ワールド座標の変換スケール係数。
/// mod.rs の CANVAS_WORLD_SCALE と同値。
const CANVAS_WORLD_SCALE: f32 = 1.0 / 100.0;

/// ルート（トップレベル）キャンバスのアンカーオフセットを計算する共通ヘルパー。
///
/// スプライト描画・キャンバス枠・GPU ピッキング・2D 物理/ドロップ配置のすべてが
/// この関数を共有することで、ルートキャンバスの原点位置を完全に一致させる。
///
/// # 引数
/// - `anchor`: ルートキャンバスの CanvasTransform.anchor（正規化 [0,1]）
/// - `vw` / `vh`: 基準ビューポートサイズ（実効解像度・カメラ参照サイズ等）
/// - `design_space`: ビューポートタブの設計空間表示中か（= edit_view_is_2d）
///
/// # 挙動
/// - `design_space=false`（Play・SS オーバーレイ = 実ゲーム合成）:
///   ortho 原点が画面中央のため、anchor=(0,0) を画面左上へ寄せる目的で `-vp/2` する。
///   `anchor*vp - vp/2` により anchor=0→画面左上・0.5→中央・1→右下となる。
/// - `design_space=true`（ビューポートタブの設計空間編集）:
///   「キャンバスを編集」モードと同様に**キャンバス左上をワールド原点**へ一致させる。
///   センタリング（`-vp/2`）を行わず、anchor=(0,0) のルートキャンバス左上が原点になる。
#[inline]
pub(super) fn root_anchor_offset(
    anchor: [f32; 2],
    vw: f32,
    vh: f32,
    design_space: bool,
) -> [f32; 2] {
    if design_space {
        [vw * anchor[0], vh * anchor[1]]
    } else {
        [vw * anchor[0] - vw / 2.0, vh * anchor[1] - vh / 2.0]
    }
}

/// ヒット・描画対象外サブツリーの DFS 番号を消費する共通ヘルパー。
///
/// DFS 番号付けの正典（`find_actor_by_dfs`）は CanvasTransform の有無に関係なく
/// 全アクターを数えるため、走査をスキップするサブツリーでもカウンタだけは
/// 同じ規則で進める必要がある（番号ズレ = 誤選択・誤ハイライトの原因）。
/// 子アクターは親と同一世界線のため world_line フィルタは行わない
/// （`find_actor_child_by_dfs` と同じ扱い）。
pub(super) fn skip_dfs_subtree(actors: &[Actor], counter: &mut u32) {
    for a in actors {
        *counter += 1;
        skip_dfs_subtree(&a.children, counter);
    }
}

// ─── アウトライン描画（太さ・色）の共通定義 ──────────────────────────────────

/// ワールドスペース表示でのアウトラインのリング間隔（ワールド単位に canvas_scale を
/// 乗じる前のキャンバス単位）。SS 表示では呼び出し側が「画面 1px 相当」を渡す。
pub(super) const OUTLINE_RING_STEP: f32 = 1.6;
/// 非選択キャンバス枠のリング数（一本線）。
const OUTLINE_RINGS_THIN: u32 = 1;
/// 選択枠のリング数（非選択の約 5 倍の太さで強調する）。
const OUTLINE_RINGS_THICK: u32 = 5;
/// ルート（基準）キャンバス枠の基本色（非選択時）。#00ff00ff（緑）。
const ROOT_CANVAS_OUTLINE_COL: [f32; 4] = [0.0, 1.0, 0.0, 1.0];

// ============================================================
//  ボーンオーバーレイ（Phase A2 / エディタのみ）
// ============================================================

/// 末端ボーン（子を持たないボーン）を描くときの既定長（メッシュローカル px）。
/// 子ボーンが無く長さを推定できない場合のフォールバック。
const BONE_TIP_DEFAULT_LEN: f32 = 24.0;
/// 関節の丸の半径（メッシュローカル px 相当）。
const BONE_JOINT_RADIUS: f32 = 4.0;
/// 関節の丸を近似する多角形の分割数。
const BONE_JOINT_SEGMENTS: u32 = 12;
/// 通常のボーン線・関節の色（水色）。
const BONE_COL: [f32; 4] = [0.30, 0.80, 1.00, 1.0];
/// 選択中ボーンの強調色（キャンバス枠・スプライト枠と同じオレンジ）。
const BONE_SELECTED_COL: [f32; 4] = [1.0, 0.5, 0.05, 1.0];
/// 解決できなかったボーンの色（赤。対応アクターが無く無変形で描かれている）。
const BONE_UNRESOLVED_COL: [f32; 4] = [1.0, 0.25, 0.25, 1.0];

/// ボーンオーバーレイ描画に必要な外部依存をまとめた文脈。
///
/// `collect_canvas_rects` の引数をこれ以上増やさないための束ね。
/// `None` を渡せばボーン描画は完全にスキップされる（Play 中・表示 OFF 時）。
pub(super) struct BoneOverlayCtx<'a> {
    /// `.sprite_mesh` を CPU で引くローダ（App の CPU キャッシュを束ねたクロージャ）。
    pub mesh_of: &'a dyn Fn(&str) -> Option<Arc<SpriteMesh>>,
}

/// アクターの子孫数（自分は含まない）。DFS 番号の送り幅計算に使う。
fn subtree_dfs_len(actor: &Actor) -> u32 {
    actor.children().iter().map(|c| 1 + subtree_dfs_len(c)).sum()
}

/// スプライトルート基準の相対パスから、そのアクターの DFS 番号を求める。
///
/// `find_actor_by_dfs` と同じ規則（子孫を world_line 無関係に全カウント）で数えるため、
/// 返り値は `selected_actor_dfs_ids` とそのまま突き合わせられる。
fn dfs_of_relative_path(root: &Actor, root_dfs: u32, path: &str) -> Option<u32> {
    let mut cur = root;
    let mut cur_dfs = root_dfs;
    for seg in path.split('/') {
        if seg.is_empty() {
            continue;
        }
        let mut child_dfs = cur_dfs + 1;
        let mut found = None;
        for child in cur.children() {
            if child.name == seg {
                found = Some((child, child_dfs));
                break;
            }
            child_dfs += 1 + subtree_dfs_len(child);
        }
        let (c, d) = found?;
        cur = c;
        cur_dfs = d;
    }
    Some(cur_dfs)
}

/// 円（多角形近似）を LineBatch へ描く。
fn add_circle(lb: &mut LineBatch, center: [f32; 3], radius: f32, color: [f32; 4]) {
    let seg = BONE_JOINT_SEGMENTS.max(3);
    let mut prev: Option<[f32; 3]> = None;
    for i in 0..=seg {
        let t = (i as f32 / seg as f32) * std::f32::consts::TAU;
        let p = [
            center[0] + radius * t.cos(),
            center[1] + radius * t.sin(),
            center[2],
        ];
        if let Some(q) = prev {
            lb.add_line(q, p, color);
        }
        prev = Some(p);
    }
}

/// スキンスプライト 1 スロットぶんのボーン（線＋関節）を LineBatch へ積む。
///
/// # 座標
/// ボーンアクターの合成行列（`resolve_bone` が返す `current`）は
/// **スプライトルート基準 = メッシュローカルと同一空間**なので、関節位置は
/// その平行移動成分そのもの。あとはスプライト本体と同じ `mesh_world`
/// （= parent_world_rs × to_mesh_mat4）で描画空間へ写す。この一致により、
/// ボーン線は必ず変形後メッシュの上に乗る。
///
/// # 色
/// 未解決（対応アクター無し）= 赤 / 選択中のボーンアクター = オレンジ / それ以外 = 水色。
#[allow(clippy::too_many_arguments)]
fn add_sprite_bone_overlay(
    lb: &mut LineBatch,
    ctx: &BoneOverlayCtx<'_>,
    comp: &SkinnedSpriteComponent,
    actor: &Actor,
    my_dfs: u32,
    world: &World,
    mesh_world: [[f32; 4]; 4],
    canvas_scale: f32,
    y_sign: f32,
    selected_dfs_ids: &[usize],
) {
    let Some(mesh) = (ctx.mesh_of)(&comp.mesh_path) else {
        return;
    };

    // メッシュローカル → 描画空間（スプライト本体とまったく同じ写像）
    let csy = canvas_scale * y_sign;
    let tp = |lx: f32, ly: f32| -> [f32; 3] {
        [
            (mesh_world[0][0] * lx + mesh_world[0][1] * ly + mesh_world[0][3]) * canvas_scale,
            (mesh_world[1][0] * lx + mesh_world[1][1] * ly + mesh_world[1][3]) * csy,
            0.0,
        ]
    };

    /// 1 ボーンぶんの表示情報（関節位置・向き・色・子の有無）。
    struct BoneVis {
        /// メッシュローカルの関節位置
        pos: [f32; 2],
        /// ボーン自身の +X 方向（末端ボーンのスタブ向き）
        dir: [f32; 2],
        /// 描画色
        col: [f32; 4],
        /// 子ボーンを持つか
        has_child: bool,
    }

    // ── 各ボーンの現在姿勢を 1 度だけ解決する ──
    let mut vis: Vec<BoneVis> = Vec::with_capacity(mesh.bone_count());
    for (bi, bone) in mesh.bones.iter().enumerate() {
        let r = resolve_bone(comp, actor, world, &bone.name);
        // 未解決ボーンはバインドポーズで描画されているので、表示もバインドポーズ位置に置く
        let m = if r.path.is_some() {
            r.current
        } else {
            mesh.bind_global[bi]
        };
        let col = if r.path.is_none() {
            BONE_UNRESOLVED_COL
        } else if r
            .path
            .as_deref()
            .and_then(|p| dfs_of_relative_path(actor, my_dfs, p))
            .is_some_and(|d| selected_dfs_ids.contains(&(d as usize)))
        {
            BONE_SELECTED_COL
        } else {
            BONE_COL
        };
        // 2×2 部分の第 1 列がボーンローカル +X の向き（正規化して使う）
        let (dx, dy) = (m[0][0], m[1][0]);
        let len = (dx * dx + dy * dy).sqrt();
        let dir = if len > f32::EPSILON {
            [dx / len, dy / len]
        } else {
            [1.0, 0.0]
        };
        vis.push(BoneVis {
            pos: [m[0][3], m[1][3]],
            dir,
            col,
            has_child: false,
        });
    }
    for bone in &mesh.bones {
        if let Some(pi) = bone.parent {
            if let Some(v) = vis.get_mut(pi) {
                v.has_child = true;
            }
        }
    }

    // ── 親関節 → 子関節の線（ボーンの「長さ」は子ボーンへの距離そのもの）──
    for (bi, bone) in mesh.bones.iter().enumerate() {
        let Some(pi) = bone.parent else { continue };
        let (Some(pv), Some(cv)) = (vis.get(pi), vis.get(bi)) else {
            continue;
        };
        lb.add_line(tp(pv.pos[0], pv.pos[1]), tp(cv.pos[0], cv.pos[1]), cv.col);
    }

    // ── 末端ボーン（子なし）は既定長のスタブで向きを示す ──
    for v in vis.iter().filter(|v| !v.has_child) {
        let tip = [
            v.pos[0] + v.dir[0] * BONE_TIP_DEFAULT_LEN,
            v.pos[1] + v.dir[1] * BONE_TIP_DEFAULT_LEN,
        ];
        lb.add_line(tp(v.pos[0], v.pos[1]), tp(tip[0], tip[1]), v.col);
    }

    // ── 関節の丸 ──
    // メッシュローカルで円を作るとボーン変形で歪むため、描画空間で作って丸さを保つ。
    let radius_screen = BONE_JOINT_RADIUS * canvas_scale;
    for v in &vis {
        add_circle(lb, tp(v.pos[0], v.pos[1]), radius_screen, v.col);
    }
}

/// 太い矩形アウトラインを LineBatch へ描画する共通ヘルパー。
///
/// GPU ラインは常に 1px のため、矩形を外側へ押し出した同心矩形を `rings` 本
/// 重ねて太さを表現する。`step` にはリング間隔（描画空間の単位）を渡す。
/// 呼び出し側が「画面 1px 相当」の step を渡せば、リングが隙間なく密着して
/// ズームに依らず 1 本の太線として見える。
///
/// 押し出しは**各辺の外向き法線方向**に行う（真のポリゴンオフセット）。
/// 中心→コーナー方向の押し出しだと縦横比の大きい矩形で長辺側の間隔が潰れて
/// 太さが不均一になるため、コーナーは隣接 2 辺の法線オフセットの合成で求める。
/// `corners` は tl→tr→br→bl の順（回転した矩形にも対応）。
fn add_thick_rect(
    lb: &mut LineBatch,
    corners: [[f32; 3]; 4],
    color: [f32; 4],
    rings: u32,
    step: f32,
) {
    // 矩形中心（法線の向き判定用）
    let cx = (corners[0][0] + corners[1][0] + corners[2][0] + corners[3][0]) * 0.25;
    let cy = (corners[0][1] + corners[1][1] + corners[2][1] + corners[3][1]) * 0.25;

    // 辺 a→b の外向き単位法線（XY 平面）。中心と反対側を向くよう符号を選ぶ。
    let edge_normal = |a: [f32; 3], b: [f32; 3]| -> [f32; 2] {
        let ex = b[0] - a[0];
        let ey = b[1] - a[1];
        let len = (ex * ex + ey * ey).sqrt().max(f32::EPSILON);
        let (mut nx, mut ny) = (ey / len, -ex / len); // 辺に垂直な単位ベクトル
        // 辺の中点から中心へのベクトルと逆向き（外向き）に揃える
        let mx = (a[0] + b[0]) * 0.5 - cx;
        let my = (a[1] + b[1]) * 0.5 - cy;
        if nx * mx + ny * my < 0.0 {
            nx = -nx;
            ny = -ny;
        }
        [nx, ny]
    };
    // 各コーナーの押し出し方向 = 隣接 2 辺の外向き法線の和
    // （矩形なら対角方向の単位×√2 相当。各辺がちょうど off だけ外へ動く）
    let dirs: [[f32; 2]; 4] = std::array::from_fn(|i| {
        let prev = corners[(i + 3) % 4];
        let cur = corners[i];
        let next = corners[(i + 1) % 4];
        let n1 = edge_normal(prev, cur);
        let n2 = edge_normal(cur, next);
        [n1[0] + n2[0], n1[1] + n2[1]]
    });

    for ring in 0..rings.max(1) {
        let off = ring as f32 * step;
        let rc: [[f32; 3]; 4] = std::array::from_fn(|i| {
            let c = corners[i];
            [c[0] + dirs[i][0] * off, c[1] + dirs[i][1] * off, c[2]]
        });
        for i in 0..4 {
            lb.add_line(rc[i], rc[(i + 1) % 4], color);
        }
    }
}


// ============================================================
//  描画アイテム構築の共通ヘルパー
// ============================================================

/// キャンバス空間の行優先ワールド行列を、GPU（列優先）のモデル行列へ変換する。
///
/// スプライト・スキンメッシュ・ID パスがまったく同じ規則で行列を組むための正典。
///   col0 = x 基底（canvas_scale で単位変換）
///   col1 = y 基底（csy = canvas_scale * y_sign。キャンバス Y 下向き → ワールド Y 上向き）
///   col2 = z 基底（3D キャンバスでは actor_3d_mat 由来、2D では identity）
///   col3 = 平行移動（3D キャンバスの z 成分も含める）
pub(super) fn canvas_mat_to_gpu(
    world_row_major: [[f32; 4]; 4],
    canvas_scale: f32,
    y_sign: f32,
) -> [[f32; 4]; 4] {
    let m = world_row_major;
    let csy = canvas_scale * y_sign;
    [
        [m[0][0] * canvas_scale, m[1][0] * csy, m[2][0], 0.0],
        [m[0][1] * canvas_scale, m[1][1] * csy, m[2][1], 0.0],
        [m[0][2] * canvas_scale, m[1][2] * csy, m[2][2], 0.0],
        [m[0][3] * canvas_scale, m[1][3] * csy, m[2][3], 1.0],
    ]
}

/// テクスチャパスから GPU テクスチャを解決する（SpriteComponent と共通のキャッシュ経路）。
///
/// キャッシュ値: Some(arc)=成功 / None=失敗済み（毎フレームのリトライ・ログ爆発防止）。
pub(super) fn resolve_sprite_texture(
    draw_ctx: &DrawContext,
    path: &str,
) -> Option<Arc<GpuSpriteTexture>> {
    if path.is_empty() {
        return None;
    }
    let mut cache = draw_ctx.sprite_tex_cache.borrow_mut();
    if !cache.contains_key(path) {
        let loaded = load_sprite_texture(
            &draw_ctx.device,
            &draw_ctx.queue,
            path,
            &draw_ctx.pipelines.sprite.tex_bgl,
            &draw_ctx.pipelines.sprite.sampler,
        );
        cache.insert(path.to_string(), loaded);
    }
    cache.get(path).and_then(|e| e.clone())
}

// ============================================================
//  collect_sprite_items
// ============================================================

/// スプライト描画リソースをアクターツリーから DFS 順に収集する。
///
/// CanvasTransform + SpriteComponent を持つアクターを再帰的に走査し、
/// `(GPU行列, カラー, テクスチャArc, 描画ゾーン, レイヤー)` のタプルを `out` に追加する。
/// テクスチャは `draw_ctx.sprite_tex_cache` でキャッシュする（失敗も記録して毎フレームスキップ）。
///
/// # 描画ゾーン・レイヤー（Phase C / D）
/// - 描画ゾーンは**ルートキャンバス**の `draw_zone` をサブツリー全体へ継承する
///   （子キャンバスはルートに従属。ワールドキャンバスは呼び出し側が Foreground 固定で渡す）。
/// - レイヤーは各 SpriteComponent の `layer` をそのまま出力し、呼び出し側で
///   同一ゾーン内の安定ソート（大きいほど手前 = 後に描画）を行う。
///
/// # スケールモード
/// スケールモード (scale_transform / scale_size / keep_aspect_ratio /
/// aspect_ratio_axis) は**各ノード自身の CanvasTransform** から読み取る。
/// - `scale_transform=true`  : このノードの位置に親の累積スケールを乗算する
/// - `scale_size=true`       : このノードのサイズに親の累積スケールを乗算する
/// - `keep_aspect_ratio=true`: scale_size 時にアスペクト比を維持する（軸は aspect_ratio_axis）
/// - 回転は常に追従する
#[allow(clippy::too_many_arguments)]
pub(super) fn collect_sprite_items(
    actors: &[Actor],
    world: &World,
    wl: u32,
    draw_ctx: &DrawContext,
    // 親アクターの CanvasComponent サイズ（anchor 計算用）。None = ルートレベル。
    parent_canvas_size: Option<[f32; 2]>,
    // 親のワールド行列（スケールなし: 回転+平行移動のみ）
    parent_world_rs: [[f32; 4]; 4],
    // 親の累積スケール。スケールモードに応じて子に伝播するかを制御する。
    parent_cumul_scale: [f32; 2],
    // ワールドスペース変換スケール（1.0=スクリーンスペース, CANVAS_WORLD_SCALE=ワールドスペース）
    canvas_scale: f32,
    // Y 軸符号（スクリーンスペース=1.0, ワールドスペース=-1.0 で Y を反転）
    y_sign: f32,
    // シーン SS モード時のビューポートサイズ（ルートアンカー計算用）。None = アクター編集タブまたはワールドスペース。
    viewport_size: Option<[f32; 2]>,
    // ルートキャンバスアクターごとの有効ビューポートサイズ上書き（Camera 参照用）。
    // actor.entity → [w, h]。viewport_size より優先される。
    canvas_viewport_overrides: &HashMap<Entity, [f32; 2]>,
    // ビューポート・ルートキャンバスの自動解像度マップ（build_root_canvas_auto_size_map）。
    // 登録済みルートは width/height をこの値へ置き換え、CanvasTransform を恒等として扱う。
    root_auto_sizes: &HashMap<Entity, [f32; 2]>,
    // 親（ルートキャンバス）から継承する描画ゾーン。ルートレベルでは各ルートの
    // CanvasComponent.draw_zone で上書きされる。呼び出し側は Foreground を渡す。
    parent_zone: CanvasDrawZone,
    // ビューポートタブの設計空間表示中か（= edit_view_is_2d）。
    // true のときルートキャンバス左上をワールド原点に一致させる（センタリングしない）。
    design_space: bool,
    out: &mut Vec<SpriteDrawItem>,
    // テキスト描画アイテムの収集先。スプライトと**同じ走査・同じ変換連鎖**で積むため、
    // 別関数に分けず 1 回の DFS でまとめて収集する（両者の位置ズレを構造的に防ぐ）。
    // テキストは専用パイプライン（フォントアトラス）で描くため出力先だけを分ける。
    text_out: &mut Vec<CanvasTextItem>,
) {
    for actor in actors {
        if actor.world_line != wl {
            continue;
        }
        // 非アクティブアクター: 自身と全子孫のスプライトを描画しない。
        // 子孫は本再帰でしか到達しないため、ここで continue すればサブツリー全体が省かれる
        // （この収集は DFS カウンタを持たないためスキップしても番号ズレは起きない）。
        if !actor.active {
            continue;
        }
        let ct_opt = world.get::<CanvasTransform>(actor.entity).cloned();
        if let Some(ct) = ct_opt {
            // ビューポート・ルートキャンバスの自動解像度上書き（Phase B）。
            // Some のとき: 解像度を自動計算値へ置き換え、Transform を恒等として扱う
            // （保存データは書き換えない）。
            let root_auto = if parent_canvas_size.is_none() {
                root_auto_sizes.get(&actor.entity).copied()
            } else {
                None
            };
            let ct = if root_auto.is_some() {
                CanvasTransform::default()
            } else {
                ct
            };
            // スケールモードはこのノード自身の CanvasTransform から読み取る
            let (sm_transform, sm_size, keep_aspect, is_width_axis) = (
                ct.scale_transform,
                ct.scale_size,
                ct.keep_aspect_ratio,
                matches!(ct.aspect_ratio_axis, AspectRatioAxis::Width),
            );
            // アンカーオフセット計算:
            // ルートレベル（parent_canvas_size=None）かつシーン SS モードでは
            // ビューポートを仮想親として扱い、ortho 原点（画面中央）からのオフセットを計算する。
            // Camera 参照が設定されているルートキャンバスはオーバーライドマップの値を優先する。
            // それ以外は親キャンバスサイズ基準。
            let eff_viewport = if parent_canvas_size.is_none() {
                canvas_viewport_overrides
                    .get(&actor.entity)
                    .copied()
                    .or(viewport_size)
            } else {
                viewport_size
            };
            let (anchor_off_x, anchor_off_y) = if parent_canvas_size.is_none() {
                if let Some([vw, vh]) = eff_viewport {
                    // ルートレベル: design_space に応じて原点位置を切り替える（共通ヘルパー）
                    let [ox, oy] = root_anchor_offset(ct.anchor, vw, vh, design_space);
                    (ox, oy)
                } else {
                    (0.0, 0.0)
                }
            } else {
                (
                    parent_canvas_size
                        .map_or(0.0, |[pw, _]| pw * ct.anchor[0] * parent_cumul_scale[0]),
                    parent_canvas_size
                        .map_or(0.0, |[_, ph]| ph * ct.anchor[1] * parent_cumul_scale[1]),
                )
            };

            // 有効位置（スケールモードに応じて位置にスケールを乗算する）
            let eff_pos = if sm_transform {
                [
                    ct.position[0] * parent_cumul_scale[0] + anchor_off_x,
                    ct.position[1] * parent_cumul_scale[1] + anchor_off_y,
                ]
            } else {
                [ct.position[0] + anchor_off_x, ct.position[1] + anchor_off_y]
            };

            // 有効 CanvasTransform（位置を調整済み・anchor は適用済み）
            let eff_ct = CanvasTransform {
                position: eff_pos,
                rotation: ct.rotation,
                scale: ct.scale,
                pivot: ct.pivot,
                anchor: [0.0, 0.0],
                ..ct.clone()
            };

            // 自アクターの CanvasComponent を取得する
            let my_canvas = actor
                .slots()
                .iter()
                .filter(|s| s.kind == ComponentKind::Canvas)
                .find_map(|s| world.get::<CanvasComponent>(s.entity));
            // 描画ゾーンの決定（Phase C）:
            // ルートレベルのキャンバスは自身の draw_zone、それ以外は親（ルート）から継承する。
            let my_zone = if parent_canvas_size.is_none() {
                my_canvas.map(|cc| cc.draw_zone).unwrap_or(parent_zone)
            } else {
                parent_zone
            };
            // sm_size による拡縮を反映した有効キャンバスサイズ（アスペクト比維持を考慮）
            let size_scale_x = if sm_size {
                if keep_aspect && !is_width_axis {
                    parent_cumul_scale[1]
                } else {
                    parent_cumul_scale[0]
                }
            } else {
                1.0
            };
            let size_scale_y = if sm_size {
                if keep_aspect && is_width_axis {
                    parent_cumul_scale[0]
                } else {
                    parent_cumul_scale[1]
                }
            } else {
                1.0
            };
            // 自動解像度上書きがあればそれを基準サイズとする（なければ保存値）
            let (my_eff_w, my_eff_h) = my_canvas
                .map(|cc| {
                    let [bw, bh] = root_auto.unwrap_or([cc.width, cc.height]);
                    (bw * size_scale_x, bh * size_scale_y)
                })
                .unwrap_or((1.0, 1.0));

            // 自分のワールド行列（スケールなし）を親 world_rs と合成する
            // pivot オフセットを正しく計算するため実際のキャンバスサイズを渡す
            let self_world_rs = mat4x4_mul(
                parent_world_rs,
                CanvasTransform {
                    scale: [1.0, 1.0],
                    ..eff_ct.clone()
                }
                .to_mat4_sized(my_eff_w, my_eff_h),
            );

            // SpriteComponent スロットを走査して GPU 行列とテクスチャを収集する
            // （enabled=false のスロットは非表示）
            for slot in actor.slots() {
                if slot.kind == ComponentKind::Sprite && slot.enabled {
                    if let Some(sc) = world.get::<SpriteComponent>(slot.entity) {
                        // scale_size モードに応じたスプライト有効サイズ（アスペクト比維持を考慮）
                        let eff_w = sc.width * size_scale_x;
                        let eff_h = sc.height * size_scale_y;
                        // スプライト行優先行列を親 world_rs と合成し、GPU 列優先に変換する
                        let sprite_world =
                            mat4x4_mul(parent_world_rs, eff_ct.to_sprite_mat4(eff_w, eff_h));
                        // y_sign でキャンバス Y 軸（下向き）→ ワールド Y 軸（上向き）を反転する
                        let csy = canvas_scale * y_sign;
                        // GPU 行列（列優先）:
                        //   col0 = x 基底（canvas_scale で単位変換、csy で Y 反転）
                        //   col1 = y 基底（同上）
                        //   col2 = z 基底（3D キャンバスの場合は actor_3d_mat 由来の z 列、2D では identity）
                        //   col3 = 平行移動（z 成分を含め 3D ワールド座標を正しく反映する）
                        // sprite_world[2][*] は 2D キャンバス時は常に 0 (x,y基底) / 1 (z基底) / 0 (translation)
                        // のため 2D キャンバスの挙動は変わらない。
                        let gpu_mat = [
                            [
                                sprite_world[0][0] * canvas_scale,
                                sprite_world[1][0] * csy,
                                sprite_world[2][0],
                                0.0,
                            ],
                            [
                                sprite_world[0][1] * canvas_scale,
                                sprite_world[1][1] * csy,
                                sprite_world[2][1],
                                0.0,
                            ],
                            [
                                sprite_world[0][2] * canvas_scale,
                                sprite_world[1][2] * csy,
                                sprite_world[2][2],
                                0.0,
                            ],
                            [
                                sprite_world[0][3] * canvas_scale,
                                sprite_world[1][3] * csy,
                                sprite_world[2][3],
                                1.0,
                            ],
                        ];
                        // テクスチャをキャッシュから取得または新規ロードする
                        // キャッシュ値: Some(arc)=成功 / None=失敗済み（毎フレームのリトライ・ログ爆発防止）
                        let mut tex = if sc.texture_path.is_empty() {
                            None
                        } else {
                            let path_str = sc.texture_path.clone();
                            let mut cache = draw_ctx.sprite_tex_cache.borrow_mut();
                            if !cache.contains_key(&path_str) {
                                // 初回のみロード試行（成否に関わらずキャッシュに記録）
                                let loaded = load_sprite_texture(
                                    &draw_ctx.device,
                                    &draw_ctx.queue,
                                    &path_str,
                                    &draw_ctx.pipelines.sprite.tex_bgl,
                                    &draw_ctx.pipelines.sprite.sampler,
                                );
                                // None（失敗）もキャッシュに入れて次フレームからスキップ
                                cache.insert(path_str.clone(), loaded);
                            }
                            // Some(Some(arc))=成功 / Some(None)=失敗 → flatten で None に統一
                            cache.get(&sc.texture_path).and_then(|e| e.clone())
                        };
                        // ポストエフェクト（.postfx）指定時: 元テクスチャにエフェクトチェーンを
                        // 焼き込んだ専用テクスチャへ差し替える（テクスチャキャッシュ層で差し替えるため
                        // スプライト描画コードは無変更）。焼き込み不能・エフェクト空なら元のまま。
                        if !sc.postfx_path.is_empty() {
                            if let Some(base) = tex.clone() {
                                if let Some(baked) =
                                    crate::engine::core::renderer::postfx::resolve_baked(
                                        draw_ctx,
                                        &base,
                                        &sc.texture_path,
                                        &sc.postfx_path,
                                    )
                                {
                                    tex = Some(baked);
                                }
                            }
                        }
                        // 描画ゾーン（ルートキャンバス継承）とレイヤー（スプライト個別）を添付する
                        out.push(SpriteDrawItem {
                            model: gpu_mat,
                            color: sc.color,
                            tex,
                            mesh: None,
                            zone: my_zone,
                            layer: sc.layer,
                        });
                    }
                }
            }

            // SkinnedSpriteComponent スロットを走査してスキンメッシュ描画アイテムを収集する。
            //
            // 矩形スプライトと**同じ out へ同じ規約**（ゾーン・レイヤー・color・
            // キャンバス Transform）で積むため、両者の前後関係は呼び出し側の
            // 共通ソートだけで正しく解決される。
            //
            // 座標系: `.sprite_mesh` の頂点は既にキャンバスピクセル実寸を持つため、
            // ここで掛けるのは親キャンバス由来の**追加スケール**のみ（to_mesh_mat4）。
            for slot in actor.slots() {
                if slot.kind != ComponentKind::SkinnedSprite || !slot.enabled {
                    continue;
                }
                let Some(ss) = world.get::<SkinnedSpriteComponent>(slot.entity) else {
                    continue;
                };
                // ボーンアクターの現在姿勢を集めて GPU で変形し、描画ハンドルを得る。
                // メッシュ未設定・読み込み失敗時は None（＝ このスロットは描画しない）。
                let Some(mesh_draw): Option<Arc<SkinnedSpriteDraw>> =
                    draw_ctx.sprite_skin.prepare_instance(
                        &draw_ctx.device,
                        &draw_ctx.queue,
                        &draw_ctx.pipelines.sprite_skin,
                        slot.entity,
                        ss,
                        actor,
                        world,
                    )
                else {
                    continue;
                };

                // メッシュローカル → キャンバス → GPU 列優先（スプライトと同一の変換連鎖）
                let mesh_world = mat4x4_mul(
                    parent_world_rs,
                    eff_ct.to_mesh_mat4(size_scale_x, size_scale_y),
                );
                let gpu_mat = canvas_mat_to_gpu(mesh_world, canvas_scale, y_sign);

                // テクスチャは SpriteComponent とまったく同じキャッシュ経路で解決する
                let tex = resolve_sprite_texture(draw_ctx, &ss.texture_path);

                out.push(SpriteDrawItem {
                    model: gpu_mat,
                    color: ss.color,
                    tex,
                    mesh: Some(mesh_draw),
                    zone: my_zone,
                    layer: ss.layer,
                });
            }

            // TextComponent スロットを走査してテキスト描画アイテムを収集する。
            //
            // 座標系: グリフのクアッドは**キャンバスピクセル実寸**で組まれるため、
            // スキンメッシュと同じく「親キャンバス由来の追加スケールのみ」を掛ける
            // （to_mesh_mat4）。スプライト（to_sprite_mat4）はユニットクワッドを
            // 実寸へ引き伸ばす別用途なので使わない。
            for slot in actor.slots() {
                if slot.kind != ComponentKind::Text || !slot.enabled {
                    continue;
                }
                let Some(tc) = world.get::<TextComponent>(slot.entity) else {
                    continue;
                };
                // 空文字は頂点を作らないので収集段階で捨てる（毎フレームの無駄を省く）。
                if tc.content.is_empty() {
                    continue;
                }
                let text_world = mat4x4_mul(
                    parent_world_rs,
                    eff_ct.to_mesh_mat4(size_scale_x, size_scale_y),
                );
                text_out.push(CanvasTextItem {
                    text: tc.content.clone(),
                    // フォントサイズは**素の値**を渡す。キャンバスの拡縮
                    // （size_scale_x/y）は上の to_mesh_mat4 が行列側で効かせるため、
                    // ここで掛けると二重にスケールされる。
                    font_size: tc.font_size,
                    color: tc.color,
                    align: tc.align,
                    vertical_align: tc.vertical_align,
                    line_spacing: tc.line_spacing,
                    model: canvas_mat_to_gpu(text_world, canvas_scale, y_sign),
                    zone: my_zone,
                    layer: tc.layer,
                });
            }

            // 子アクターへの基準 Canvas サイズと auto_scale を構築する
            // （子のアンカー基準サイズにも自動解像度上書きを反映する）。
            // スケールモードは各子が自身の CanvasTransform から読み取るため伝播しない。
            let child_info =
                my_canvas.map(|cc| (root_auto.unwrap_or([cc.width, cc.height]), cc.auto_scale));
            let child_canvas_size = child_info.map(|(sz, _)| sz);
            // ルートキャンバスかつ auto_scale=true のとき、ビューポートサイズ/参照サイズで自動スケールする
            // Camera 参照の場合は eff_viewport がカメラの描画範囲になる
            let auto_scale_factor = if parent_canvas_size.is_none() {
                if let (Some([vw, vh]), Some((_, true))) = (eff_viewport, child_info) {
                    [vw / my_eff_w, vh / my_eff_h]
                } else {
                    [1.0f32, 1.0]
                }
            } else {
                [1.0f32, 1.0]
            };
            // 子への累積スケール（このノード自身の scale_transform に応じて自分のスケールを積む）
            let child_cumul_scale = if sm_transform {
                [
                    parent_cumul_scale[0] * ct.scale[0] * auto_scale_factor[0],
                    parent_cumul_scale[1] * ct.scale[1] * auto_scale_factor[1],
                ]
            } else {
                // スケール伝播なし: auto_scale のみ適用
                [
                    ct.scale[0] * auto_scale_factor[0],
                    ct.scale[1] * auto_scale_factor[1],
                ]
            };
            collect_sprite_items(
                &actor.children,
                world,
                wl,
                draw_ctx,
                child_canvas_size,
                self_world_rs,
                child_cumul_scale,
                canvas_scale,
                y_sign,
                viewport_size,
                canvas_viewport_overrides,
                root_auto_sizes,
                my_zone,
                design_space,
                out,
                text_out,
            );
        }
    }
}

// ============================================================
//  collect_canvas_rects
// ============================================================

/// CanvasComponent / Sprite のアウトライン矩形を LineBatch に追加する。
///
/// - CanvasComponent: キャンバス領域のアウトラインを常に描画する
/// - SpriteComponent: `selected_dfs_ids` に含まれる DFS ID のみ描画する
///
/// DFS カウンタは `collect_sprite_items` と同じ規則で全アクターを数える。
#[allow(clippy::too_many_arguments)]
pub(super) fn collect_canvas_rects(
    actors: &[Actor],
    world: &World,
    wl: u32,
    lb: &mut LineBatch,
    // キャンバスアウトラインの色 [r, g, b, a]
    col: [f32; 4],
    // 現在選択中のアクター DFS ID リスト（Sprite アウトラインの描画判定に使う）
    selected_dfs_ids: &[usize],
    counter: &mut u32,
    parent_canvas_size: Option<[f32; 2]>,
    parent_world_rs: [[f32; 4]; 4],
    parent_cumul_scale: [f32; 2],
    canvas_scale: f32,
    y_sign: f32,
    viewport_size: Option<[f32; 2]>,
    canvas_viewport_overrides: &HashMap<Entity, [f32; 2]>,
    // ビューポート・ルートキャンバスの自動解像度マップ（collect_sprite_items と同じ扱い）
    root_auto_sizes: &HashMap<Entity, [f32; 2]>,
    // ビューポートタブの設計空間表示中か（= edit_view_is_2d。collect_sprite_items と同じ扱い）
    design_space: bool,
    // アウトラインのリング間隔（描画空間の単位）。
    // SS 表示では「画面 1px 相当」を渡すことで、ズームに依らず連続した太線に見える。
    outline_step: f32,
    // スキンスプライトのボーン可視化（Phase A2）。None = 描かない
    // （Play 中・ビューポートオプションで OFF・メッシュローダが無い場合）。
    bone_overlay: Option<&BoneOverlayCtx<'_>>,
) {
    for actor in actors {
        if actor.world_line != wl {
            continue;
        }
        let my_dfs = *counter as usize;
        *counter += 1;

        // 非アクティブアクター: 枠線を描画しない（DFS 番号は子孫分も進める）
        if !actor.active {
            skip_dfs_subtree(&actor.children, counter);
            continue;
        }

        let ct_opt = world.get::<CanvasTransform>(actor.entity).cloned();
        if let Some(ct) = ct_opt {
            // ビューポート・ルートキャンバス: 自動解像度上書き + Transform 恒等化（Phase B）
            let root_auto = if parent_canvas_size.is_none() {
                root_auto_sizes.get(&actor.entity).copied()
            } else {
                None
            };
            let ct = if root_auto.is_some() {
                CanvasTransform::default()
            } else {
                ct
            };
            // スケールモードはこのノード自身の CanvasTransform から読み取る
            let (sm_transform, sm_size, keep_aspect, is_width_axis) = (
                ct.scale_transform,
                ct.scale_size,
                ct.keep_aspect_ratio,
                matches!(ct.aspect_ratio_axis, AspectRatioAxis::Width),
            );
            // アンカーオフセット計算（collect_sprite_items と同じロジック）
            // Camera 参照のルートキャンバスはオーバーライドマップの値を優先する
            let eff_viewport = if parent_canvas_size.is_none() {
                canvas_viewport_overrides
                    .get(&actor.entity)
                    .copied()
                    .or(viewport_size)
            } else {
                viewport_size
            };
            let (anchor_off_x, anchor_off_y) = if parent_canvas_size.is_none() {
                if let Some([vw, vh]) = eff_viewport {
                    // ルートレベル: design_space に応じて原点位置を切り替える（共通ヘルパー）
                    let [ox, oy] = root_anchor_offset(ct.anchor, vw, vh, design_space);
                    (ox, oy)
                } else {
                    (0.0, 0.0)
                }
            } else {
                (
                    parent_canvas_size
                        .map_or(0.0, |[pw, _]| pw * ct.anchor[0] * parent_cumul_scale[0]),
                    parent_canvas_size
                        .map_or(0.0, |[_, ph]| ph * ct.anchor[1] * parent_cumul_scale[1]),
                )
            };

            // 有効位置（スケールモードに応じて）
            let eff_pos = if sm_transform {
                [
                    ct.position[0] * parent_cumul_scale[0] + anchor_off_x,
                    ct.position[1] * parent_cumul_scale[1] + anchor_off_y,
                ]
            } else {
                [ct.position[0] + anchor_off_x, ct.position[1] + anchor_off_y]
            };
            let eff_ct = CanvasTransform {
                position: eff_pos,
                rotation: ct.rotation,
                scale: ct.scale,
                pivot: ct.pivot,
                anchor: [0.0, 0.0],
                ..ct.clone()
            };

            // pivot はノーマライズ値のため実際のキャンバスサイズで補正する
            let my_canvas_r = actor
                .slots()
                .iter()
                .filter(|s| s.kind == ComponentKind::Canvas)
                .find_map(|s| world.get::<CanvasComponent>(s.entity));
            // アスペクト比維持を考慮したスケール係数
            let size_sc_x = if sm_size {
                if keep_aspect && !is_width_axis {
                    parent_cumul_scale[1]
                } else {
                    parent_cumul_scale[0]
                }
            } else {
                1.0
            };
            let size_sc_y = if sm_size {
                if keep_aspect && is_width_axis {
                    parent_cumul_scale[0]
                } else {
                    parent_cumul_scale[1]
                }
            } else {
                1.0
            };
            // 自動解像度上書きがあればそれを基準サイズとする（なければ保存値）
            let (my_eff_w_r, my_eff_h_r) = my_canvas_r
                .map(|cc| {
                    let [bw, bh] = root_auto.unwrap_or([cc.width, cc.height]);
                    (bw * size_sc_x, bh * size_sc_y)
                })
                .unwrap_or((1.0, 1.0));

            let self_world_rs = mat4x4_mul(
                parent_world_rs,
                CanvasTransform {
                    scale: [1.0, 1.0],
                    ..eff_ct.clone()
                }
                .to_mat4_sized(my_eff_w_r, my_eff_h_r),
            );

            for slot in actor.slots() {
                match slot.kind {
                    ComponentKind::Canvas => {
                        // CanvasComponent: キャンバス領域のアウトラインを常に描画する。
                        // 色:   選択中=オレンジ（選択色は全アウトライン共通） /
                        //       ルート(基準)キャンバス=緑 / それ以外=通常色。
                        // 太さ: 非選択=一本線、選択中のみ太線（約 5 倍）で強調する。
                        if let Some(cc) = world.get::<CanvasComponent>(slot.entity) {
                            const SELECTED_COL: [f32; 4] = [1.0, 0.5, 0.05, 1.0];
                            let is_selected = selected_dfs_ids.contains(&my_dfs);
                            let is_root = parent_canvas_size.is_none();
                            let draw_col = if is_selected {
                                SELECTED_COL
                            } else if is_root {
                                ROOT_CANVAS_OUTLINE_COL
                            } else {
                                col
                            };
                            let rings = if is_selected {
                                OUTLINE_RINGS_THICK
                            } else {
                                OUTLINE_RINGS_THIN
                            };
                            // アウトラインも自動解像度上書きを反映する
                            let [base_w, base_h] = root_auto.unwrap_or([cc.width, cc.height]);
                            let eff_w = base_w * size_sc_x;
                            let eff_h = base_h * size_sc_y;
                            let m = mat4x4_mul(parent_world_rs, eff_ct.to_mat4_sized(eff_w, eff_h));
                            let csy = canvas_scale * y_sign;
                            let tp = |lx: f32, ly: f32| -> [f32; 3] {
                                [
                                    (m[0][0] * lx + m[0][1] * ly + m[0][3]) * canvas_scale,
                                    (m[1][0] * lx + m[1][1] * ly + m[1][3]) * csy,
                                    0.0f32,
                                ]
                            };
                            add_thick_rect(
                                lb,
                                [
                                    tp(0.0, 0.0),
                                    tp(eff_w, 0.0),
                                    tp(eff_w, eff_h),
                                    tp(0.0, eff_h),
                                ],
                                draw_col,
                                rings,
                                outline_step,
                            );
                        }
                    }
                    ComponentKind::Sprite => {
                        // SpriteComponent: 選択時のみアウトラインを太線で描画する。
                        // 選択色はキャンバス枠と共通のオレンジに統一する。
                        if selected_dfs_ids.contains(&my_dfs) {
                            if let Some(sc) = world.get::<SpriteComponent>(slot.entity) {
                                let eff_w = sc.width * size_sc_x;
                                let eff_h = sc.height * size_sc_y;
                                const SPRITE_OUTLINE_COL: [f32; 4] = [1.0, 0.5, 0.05, 1.0];
                                let m = mat4x4_mul(
                                    parent_world_rs,
                                    eff_ct.to_sprite_mat4(eff_w, eff_h),
                                );
                                let csy2 = canvas_scale * y_sign;
                                let tp = |lx: f32, ly: f32| -> [f32; 3] {
                                    [
                                        (m[0][0] * lx + m[0][1] * ly + m[0][3]) * canvas_scale,
                                        (m[1][0] * lx + m[1][1] * ly + m[1][3]) * csy2,
                                        0.0f32,
                                    ]
                                };
                                add_thick_rect(
                                    lb,
                                    [tp(0.0, 0.0), tp(1.0, 0.0), tp(1.0, 1.0), tp(0.0, 1.0)],
                                    SPRITE_OUTLINE_COL,
                                    OUTLINE_RINGS_THICK,
                                    outline_step,
                                );
                            }
                        }
                    }
                    ComponentKind::SkinnedSprite => {
                        // スキンスプライト: 「本体または配下のボーンアクターが選択中」の
                        // ときだけボーン（線＋関節）を描く。
                        //
                        // 配下判定は DFS 番号の連続性を使う: このノードの子孫は
                        // my_dfs+1 .. my_dfs+subtree_len に必ず収まる（find_actor_by_dfs
                        // と同じ番号付け規則）。ボーンアクターを選んでギズモで回している
                        // 最中もボーン表示が消えないようにするための判定である。
                        let Some(ctx) = bone_overlay else { continue };
                        let sub = subtree_dfs_len(actor) as usize;
                        let selected_here = selected_dfs_ids
                            .iter()
                            .any(|&d| d >= my_dfs && d <= my_dfs + sub);
                        if !selected_here {
                            continue;
                        }
                        let Some(ss) = world.get::<SkinnedSpriteComponent>(slot.entity) else {
                            continue;
                        };
                        // スプライト本体の描画とまったく同じ変換連鎖（to_mesh_mat4）
                        let mesh_world =
                            mat4x4_mul(parent_world_rs, eff_ct.to_mesh_mat4(size_sc_x, size_sc_y));
                        add_sprite_bone_overlay(
                            lb,
                            ctx,
                            ss,
                            actor,
                            my_dfs as u32,
                            world,
                            mesh_world,
                            canvas_scale,
                            y_sign,
                            selected_dfs_ids,
                        );
                    }
                    _ => {}
                }
            }

            // 子への継承情報を構築する（子のアンカー基準サイズにも自動解像度上書きを反映）。
            // スケールモードは各子が自身の CanvasTransform から読み取るため伝播しない。
            let child_info =
                my_canvas_r.map(|cc| (root_auto.unwrap_or([cc.width, cc.height]), cc.auto_scale));
            let child_canvas_size = child_info.map(|(sz, _)| sz);
            let auto_scale_factor = if parent_canvas_size.is_none() {
                if let (Some([vw, vh]), Some((_, true))) = (eff_viewport, child_info) {
                    [vw / my_eff_w_r, vh / my_eff_h_r]
                } else {
                    [1.0f32, 1.0]
                }
            } else {
                [1.0f32, 1.0]
            };
            let child_cumul_scale = if sm_transform {
                [
                    parent_cumul_scale[0] * ct.scale[0] * auto_scale_factor[0],
                    parent_cumul_scale[1] * ct.scale[1] * auto_scale_factor[1],
                ]
            } else {
                [
                    ct.scale[0] * auto_scale_factor[0],
                    ct.scale[1] * auto_scale_factor[1],
                ]
            };
            collect_canvas_rects(
                &actor.children,
                world,
                wl,
                lb,
                col,
                selected_dfs_ids,
                counter,
                child_canvas_size,
                self_world_rs,
                child_cumul_scale,
                canvas_scale,
                y_sign,
                viewport_size,
                canvas_viewport_overrides,
                root_auto_sizes,
                design_space,
                outline_step,
                bone_overlay,
            );
        } else {
            // CanvasTransform なし（Actor3D 等）: 枠描画対象外だが、DFS 番号は
            // find_actor_by_dfs と同じ規則（子孫も含めて全カウント）で消費する。
            // 消費しないと以降の DFS ID がズレて選択ハイライトが別枠に付く。
            skip_dfs_subtree(&actor.children, counter);
        }
    }
}

// ============================================================
//  CanvasIdItem / collect_canvas_id_items
// ============================================================

/// キャンバス ID パス（GPU ピッキング）へ描く 1 アイテム。
///
/// 矩形スプライトとスキンメッシュの共通表現。`mesh` が `Some` のときは
/// 変形済み頂点＋インデックスで描くので、**ピック形状が実際の見た目と一致する**。
pub(super) struct CanvasIdItem {
    /// ID パスの A チャンネルへ書き込む raw ID。
    pub raw_id: u32,
    /// GPU 列優先モデル行列。
    pub model: [[f32; 4]; 4],
    /// アルファマスク用テクスチャパス（None = 白フォールバックで全面ピック可）。
    pub tex_path: Option<String>,
    /// スキンメッシュ描画のハンドル（None = ユニットクワッド）。
    pub mesh: Option<Arc<SkinnedSpriteDraw>>,
    /// 描画ゾーン（描画順ソート用）。
    pub zone: CanvasDrawZone,
    /// レイヤー（描画順ソート用）。
    pub layer: i32,
}

/// キャンバスアクター ID アイテムを DFS 順に収集する。
///
/// DFS カウンタは `find_actor_by_dfs` と同じ規則で全アクターを数える
/// （`CanvasTransform` がないアクターも子を含めてカウント）。
///
/// # 出力 `out` のタプル要素
/// - `raw_id`:         GPU に書き込む ID 値 (`mc_total + dfs + 1`)
/// - `gpu_mat`:        GPU 変換行列（Sprite のみ出力）
/// - `sprite_tex_path`: `Some(path)` → スプライトありでアルファマスク有効
///                      `None`       → スプライトなしで全面選択可能
/// - `draw_zone`:      ルートキャンバスから継承した描画ゾーン（Phase C）
/// - `layer`:          スプライトのレイヤー値（Phase D）
///
/// 呼び出し側で描画（collect_sprite_items）と同一の順序
/// （背景ゾーン → 前面ゾーン、各ゾーン内はレイヤー昇順の安定ソート）に
/// 並べ替えることで、クリック時に視覚的最前面のスプライトがピックされる。
#[allow(clippy::too_many_arguments)]
pub(super) fn collect_canvas_id_items(
    actors: &[Actor],
    world: &World,
    wl: u32,
    counter: &mut u32,
    parent_canvas_size: Option<[f32; 2]>,
    parent_world_rs: [[f32; 4]; 4],
    parent_cumul_scale: [f32; 2],
    canvas_scale: f32,
    y_sign: f32,
    viewport_size: Option<[f32; 2]>,
    canvas_viewport_overrides: &HashMap<Entity, [f32; 2]>,
    // ビューポート・ルートキャンバスの自動解像度マップ（collect_sprite_items と同じ扱い）
    root_auto_sizes: &HashMap<Entity, [f32; 2]>,
    // 3D MC インスタンスの総数（canvas_id の raw_id オフセット計算に使用）
    mc_total: u32,
    // 親（ルートキャンバス）から継承する描画ゾーン（collect_sprite_items と同じ扱い）
    parent_zone: CanvasDrawZone,
    // スクリーンスペースのサブツリー内かどうか。
    // CanvasTransform を持たないアクター（Actor3D。3D ワールドキャンバス等）を通過した時点で
    // false になり、それ以降の子孫は DFS カウントのみ行い ID quad を出力しない。
    // 3D ワールドキャンバス配下のスプライトは WS 用の collect_3d_canvas_child_id_items が
    // 担当するため、ここで出力すると SS 座標の誤った ID quad が重なり誤選択の原因になる。
    in_ss_subtree: bool,
    // ビューポートタブの設計空間表示中か（= edit_view_is_2d。collect_sprite_items と同じ扱い）
    design_space: bool,
    // スキンスプライトの変形済み頂点を引くための描画コンテキスト。
    // 本フレームのスプライト収集（collect_sprite_items）が既にディスパッチ済みの
    // 変形結果をそのまま使う（ID パス用に再変形はしない）。
    draw_ctx: &DrawContext,
    out: &mut Vec<CanvasIdItem>,
) {
    for actor in actors {
        if actor.world_line != wl {
            continue;
        }
        let my_dfs = *counter;
        *counter += 1;

        // 非アクティブアクター: 描画されないためピック（ID）対象からも外す。
        // DFS 番号は選択系と整合させるため子孫分も含めて進める。
        if !actor.active {
            skip_dfs_subtree(&actor.children, counter);
            continue;
        }

        let ct_opt = world.get::<CanvasTransform>(actor.entity).cloned();
        // CanvasTransform を持たないアクター配下は SS サブツリー外として扱う
        let next_in_ss = in_ss_subtree && ct_opt.is_some();
        let (next_canvas_size, next_cumul_scale, next_world_rs, next_zone) =
            if let (true, Some(ct)) = (in_ss_subtree, ct_opt) {
                // ビューポート・ルートキャンバス: 自動解像度上書き + Transform 恒等化（Phase B）
                let root_auto = if parent_canvas_size.is_none() {
                    root_auto_sizes.get(&actor.entity).copied()
                } else {
                    None
                };
                let ct = if root_auto.is_some() {
                    CanvasTransform::default()
                } else {
                    ct
                };
                // スケールモードはこのノード自身の CanvasTransform から読み取る
                let (sm_transform, sm_size, keep_aspect, is_width_axis) = (
                    ct.scale_transform,
                    ct.scale_size,
                    ct.keep_aspect_ratio,
                    matches!(ct.aspect_ratio_axis, AspectRatioAxis::Width),
                );
                // アンカーオフセット（collect_sprite_items と同じロジック）
                // Camera 参照のルートキャンバスはオーバーライドマップの値を優先する
                let eff_viewport = if parent_canvas_size.is_none() {
                    canvas_viewport_overrides
                        .get(&actor.entity)
                        .copied()
                        .or(viewport_size)
                } else {
                    viewport_size
                };
                let (anchor_off_x, anchor_off_y) = if parent_canvas_size.is_none() {
                    if let Some([vw, vh]) = eff_viewport {
                        // ルートレベル: design_space に応じて原点位置を切り替える（共通ヘルパー）
                        let [ox, oy] = root_anchor_offset(ct.anchor, vw, vh, design_space);
                        (ox, oy)
                    } else {
                        (0.0, 0.0)
                    }
                } else {
                    (
                        parent_canvas_size
                            .map_or(0.0, |[pw, _]| pw * ct.anchor[0] * parent_cumul_scale[0]),
                        parent_canvas_size
                            .map_or(0.0, |[_, ph]| ph * ct.anchor[1] * parent_cumul_scale[1]),
                    )
                };
                let eff_pos = if sm_transform {
                    [
                        ct.position[0] * parent_cumul_scale[0] + anchor_off_x,
                        ct.position[1] * parent_cumul_scale[1] + anchor_off_y,
                    ]
                } else {
                    [ct.position[0] + anchor_off_x, ct.position[1] + anchor_off_y]
                };
                let eff_ct = CanvasTransform {
                    position: eff_pos,
                    rotation: ct.rotation,
                    scale: ct.scale,
                    pivot: ct.pivot,
                    anchor: [0.0, 0.0],
                    ..ct.clone()
                };

                // 自アクターの CanvasComponent
                let my_canvas = actor
                    .slots()
                    .iter()
                    .filter(|s| s.kind == ComponentKind::Canvas)
                    .find_map(|s| world.get::<CanvasComponent>(s.entity));
                // 描画ゾーンの決定（collect_sprite_items と同じロジック）:
                // ルートレベルのキャンバスは自身の draw_zone、それ以外は親から継承する。
                let my_zone = if parent_canvas_size.is_none() {
                    my_canvas.map(|cc| cc.draw_zone).unwrap_or(parent_zone)
                } else {
                    parent_zone
                };
                // アスペクト比維持を考慮したスケール係数
                let id_sc_x = if sm_size {
                    if keep_aspect && !is_width_axis {
                        parent_cumul_scale[1]
                    } else {
                        parent_cumul_scale[0]
                    }
                } else {
                    1.0
                };
                let id_sc_y = if sm_size {
                    if keep_aspect && is_width_axis {
                        parent_cumul_scale[0]
                    } else {
                        parent_cumul_scale[1]
                    }
                } else {
                    1.0
                };
                // 自動解像度上書きがあればそれを基準サイズとする（なければ保存値）
                let (my_eff_w, my_eff_h) = my_canvas
                    .map(|cc| {
                        let [bw, bh] = root_auto.unwrap_or([cc.width, cc.height]);
                        (bw * id_sc_x, bh * id_sc_y)
                    })
                    .unwrap_or((1.0, 1.0));

                // 子への親ワールド RS 行列
                let self_world_rs = mat4x4_mul(
                    parent_world_rs,
                    CanvasTransform {
                        scale: [1.0, 1.0],
                        ..eff_ct.clone()
                    }
                    .to_mat4_sized(my_eff_w, my_eff_h),
                );

                // ID quad 用 GPU 行列の構築
                // SpriteComponent を持つアクターをピッキング対象にする。
                // テクスチャなし（単色）は白テクスチャフォールバックを使用して全面選択可能にする。
                let csy = canvas_scale * y_sign;
                let mut gpu_mat_and_path: Option<(
                    [[f32; 4]; 4],
                    Option<String>,
                    i32,
                    Option<Arc<SkinnedSpriteDraw>>,
                )> = None;
                // スキンスプライト（`.sprite_mesh`）: 変形済み頂点でメッシュ形状のまま
                // ID を書く。矩形スプライトより先に走査するのではなく**後**に見るため、
                // 同一アクターに両方あるときは従来どおり SpriteComponent が優先される。
                for slot in actor.slots() {
                    // enabled=false のスプライトは非表示のためピック対象からも外す
                    if slot.kind == ComponentKind::Sprite && slot.enabled {
                        if let Some(sc) = world.get::<SpriteComponent>(slot.entity) {
                            let ew = sc.width * id_sc_x;
                            let eh = sc.height * id_sc_y;
                            let sw = mat4x4_mul(parent_world_rs, eff_ct.to_sprite_mat4(ew, eh));
                            // テクスチャなしは None → 白フォールバック（全面 alpha=1）
                            let tex_path = if sc.texture_path.is_empty() {
                                None
                            } else {
                                Some(sc.texture_path.clone())
                            };
                            gpu_mat_and_path = Some((
                                [
                                    [sw[0][0] * canvas_scale, sw[1][0] * csy, 0.0, 0.0],
                                    [sw[0][1] * canvas_scale, sw[1][1] * csy, 0.0, 0.0],
                                    [0.0, 0.0, 1.0, 0.0],
                                    [sw[0][3] * canvas_scale, sw[1][3] * csy, 0.0, 1.0],
                                ],
                                tex_path,
                                sc.layer,
                                None,
                            ));
                            break;
                        }
                    }
                }
                // 矩形スプライトが無いアクターのみ、スキンスプライトをピック対象にする。
                if gpu_mat_and_path.is_none() {
                    for slot in actor.slots() {
                        if slot.kind != ComponentKind::SkinnedSprite || !slot.enabled {
                            continue;
                        }
                        let Some(ss) = world.get::<SkinnedSpriteComponent>(slot.entity) else {
                            continue;
                        };
                        // 本フレームの描画収集で変形済みの頂点バッファを引く。
                        // まだ変形されていない（＝描画対象でない）場合はピック対象外。
                        let Some(mesh_draw) = draw_ctx.sprite_skin.draw_handle(slot.entity) else {
                            continue;
                        };
                        let mw = mat4x4_mul(parent_world_rs, eff_ct.to_mesh_mat4(id_sc_x, id_sc_y));
                        let tex_path = if ss.texture_path.is_empty() {
                            None
                        } else {
                            Some(ss.texture_path.clone())
                        };
                        gpu_mat_and_path = Some((
                            [
                                [mw[0][0] * canvas_scale, mw[1][0] * csy, 0.0, 0.0],
                                [mw[0][1] * canvas_scale, mw[1][1] * csy, 0.0, 0.0],
                                [0.0, 0.0, 1.0, 0.0],
                                [mw[0][3] * canvas_scale, mw[1][3] * csy, 0.0, 1.0],
                            ],
                            tex_path,
                            ss.layer,
                            Some(mesh_draw),
                        ));
                        break;
                    }
                }

                if let Some((gpu_mat, tex_path, layer, mesh)) = gpu_mat_and_path {
                    // raw_id = mc_total + my_dfs + 1
                    // （0 = 背景、1..mc_total = 3D MC インスタンス）
                    // 描画ゾーン・レイヤーは呼び出し側の描画順ソートに使用する
                    out.push(CanvasIdItem {
                        raw_id: mc_total + my_dfs + 1,
                        model: gpu_mat,
                        tex_path,
                        mesh,
                        zone: my_zone,
                        layer,
                    });
                }

                // 子への継承情報を計算する（collect_sprite_items と同じロジック。
                // 子のアンカー基準サイズにも自動解像度上書きを反映する）。
                // スケールモードは各子が自身の CanvasTransform から読み取るため伝播しない。
                let child_info =
                    my_canvas.map(|cc| (root_auto.unwrap_or([cc.width, cc.height]), cc.auto_scale));
                let child_canvas_size = child_info.map(|(sz, _)| sz);
                let auto_scale_factor = if parent_canvas_size.is_none() {
                    if let (Some([vw, vh]), Some((_, true))) = (eff_viewport, child_info) {
                        [vw / my_eff_w, vh / my_eff_h]
                    } else {
                        [1.0f32, 1.0]
                    }
                } else {
                    [1.0f32, 1.0]
                };
                let child_cumul_scale = if sm_transform {
                    [
                        parent_cumul_scale[0] * ct.scale[0] * auto_scale_factor[0],
                        parent_cumul_scale[1] * ct.scale[1] * auto_scale_factor[1],
                    ]
                } else {
                    [
                        ct.scale[0] * auto_scale_factor[0],
                        ct.scale[1] * auto_scale_factor[1],
                    ]
                };
                (child_canvas_size, child_cumul_scale, self_world_rs, my_zone)
            } else {
                // CanvasTransform なし・または SS サブツリー外:
                // ID quad は出力せず、子は親の情報をそのまま引き継ぐ（DFS カウントのみ）
                (
                    parent_canvas_size,
                    parent_cumul_scale,
                    parent_world_rs,
                    parent_zone,
                )
            };

        // 常に子に再帰する（DFS カウンタを全アクターで管理するため）
        collect_canvas_id_items(
            &actor.children,
            world,
            wl,
            counter,
            next_canvas_size,
            next_world_rs,
            next_cumul_scale,
            canvas_scale,
            y_sign,
            viewport_size,
            canvas_viewport_overrides,
            root_auto_sizes,
            mc_total,
            next_zone,
            next_in_ss,
            design_space,
            draw_ctx,
            out,
        );
    }
}

// ============================================================
//  collect_3d_canvas_child_id_items
// ============================================================

/// 3D Canvas（Actor3D + CanvasComponent）の子スプライト ID アイテムを DFS 順に収集する。
///
/// WS perspective カメラと組み合わせて GPU ID パスで使用する。
/// DFS カウンタは `find_actor_by_dfs` と同じ規則で全アクターを数える。
///
/// 出力タプル要素:
/// - `raw_id`:   GPU に書き込む ID 値 (`mc_total + dfs + 1`)
/// - `gpu_mat`:  3D ワールド空間の GPU 列優先モデル行列
/// - `tex_path`: テクスチャパス（`Some`=あり、アルファマスク有効）
///
/// # レイヤー順（Phase D）
/// ワールドキャンバスのレイヤーは**そのキャンバス内**での描画順制御のため、
/// キャンバス 1 つ分の走査（walk）ごとに追加分をレイヤー昇順で安定ソートする。
/// これにより ID パスの描画順（後勝ち）がスプライト描画順と一致し、
/// クリック時に視覚的最前面のスプライトがピックされる。
pub(super) fn collect_3d_canvas_child_id_items(
    actors: &[Actor],
    world: &World,
    wl: u32,
    counter: &mut u32,
    mc_total: u32,
    // 変形済みスキンメッシュの描画ハンドルを引くための描画文脈。
    // 本フレームの描画収集（collect_sprite_items）が既に compute を回しているので、
    // ここでは `draw_handle` で結果を参照するだけでよい（再ディスパッチしない）。
    draw_ctx: &DrawContext,
    out: &mut Vec<(u32, [[f32; 4]; 4], Option<String>, Option<Arc<SkinnedSpriteDraw>>)>,
    // 3D キャンバス（Actor3D + CanvasComponent）のパネル面そのものをピック対象にする
    // ための面クワッド出力（raw_id, GPU 列優先モデル行列）。透明でも面全体を選択可能に
    // するため、深度対応の canvas_id パイプライン（白フォールバック alpha=1）で描画する。
    // 子スプライト（out）は Always 深度で後から上書きするため、可視スプライトが優先される。
    panel_out: &mut Vec<(u32, [[f32; 4]; 4])>,
) {
    for actor in actors {
        if actor.world_line != wl {
            continue;
        }
        // このアクターの 0 始まり DFS 番号（find_actor_by_dfs と同一規則）。
        // パネル面 raw_id = mc_total + my_dfs + 1（子スプライト・コライダーと同一 ID 空間）。
        let my_dfs = *counter;
        *counter += 1;

        // 非アクティブアクター: 描画されないためピック（ID）対象からも外す。
        // DFS 番号は選択系と整合させるため子孫分も含めて進める。
        if !actor.active {
            skip_dfs_subtree(&actor.children, counter);
            continue;
        }

        // Actor3D + CanvasComponent: 子を canvas_to_world で走査する
        // （Canvas スロットが enabled=false のときは通常再帰へフォールバック）
        let handled = if !actor.is_2d() {
            let canvas_slot = actor
                .slots()
                .iter()
                .find(|s| s.kind == ComponentKind::Canvas && s.enabled);
            if let Some(cs) = canvas_slot {
                if let (Some(cc), Some(tf)) = (
                    world.get::<CanvasComponent>(cs.entity),
                    world.get::<ActorTransform>(actor.entity),
                ) {
                    // スプライト描画と同じ canvas_to_world を構築する
                    let cws = CANVAS_WORLD_SCALE;
                    let (piv_x, piv_y) = (cc.pivot[0], cc.pivot[1]);
                    let canvas_to_world = mat4x4_mul(
                        tf.to_mat4(),
                        [
                            [cws, 0.0, 0.0, -piv_x * cc.width * cws],
                            [0.0, -cws, 0.0, piv_y * cc.height * cws],
                            [0.0, 0.0, 1.0, 0.0],
                            [0.0, 0.0, 0.0, 1.0],
                        ],
                    );
                    // キャンバスパネル面（キャンバス空間 [0,0]-[width,height]）を面ピック対象にする。
                    // canvas_to_world でパネル 3 隅を 3D ワールドへ写し、ユニットクワッド [0,1]²
                    // を覆うアフィンモデル行列（col0=u 基底, col1=v 基底, col3=原点）を構築する。
                    let cpw = |x: f32, y: f32| -> [f32; 3] {
                        [
                            canvas_to_world[0][0] * x
                                + canvas_to_world[0][1] * y
                                + canvas_to_world[0][3],
                            canvas_to_world[1][0] * x
                                + canvas_to_world[1][1] * y
                                + canvas_to_world[1][3],
                            canvas_to_world[2][0] * x
                                + canvas_to_world[2][1] * y
                                + canvas_to_world[2][3],
                        ]
                    };
                    let c00 = cpw(0.0, 0.0);
                    let c10 = cpw(cc.width, 0.0);
                    let c01 = cpw(0.0, cc.height);
                    let ex = [c10[0] - c00[0], c10[1] - c00[1], c10[2] - c00[2]];
                    let ey = [c01[0] - c00[0], c01[1] - c00[1], c01[2] - c00[2]];
                    let panel_model = [
                        [ex[0], ex[1], ex[2], 0.0],
                        [ey[0], ey[1], ey[2], 0.0],
                        [0.0, 0.0, 1.0, 0.0],
                        [c00[0], c00[1], c00[2], 1.0],
                    ];
                    panel_out.push((mc_total + my_dfs + 1, panel_model));

                    // 子を 3D ワールド行列で再帰走査する。
                    // このキャンバス内の追加分をレイヤー昇順で安定ソートしてから out へ
                    // 追加する（ワールドキャンバスのレイヤーはキャンバス内で完結する）。
                    let mut canvas_items: Vec<(
                        u32,
                        [[f32; 4]; 4],
                        Option<String>,
                        Option<Arc<SkinnedSpriteDraw>>,
                        i32,
                    )> = Vec::new();
                    walk_3d_canvas_children_id(
                        &actor.children,
                        world,
                        wl,
                        counter,
                        Some([cc.width, cc.height]),
                        canvas_to_world,
                        [1.0, 1.0],
                        mc_total,
                        draw_ctx,
                        &mut canvas_items,
                    );
                    // 安定ソート: 同一レイヤーはヒエラルキー DFS 順を維持する
                    canvas_items.sort_by_key(|it| it.4);
                    out.extend(
                        canvas_items
                            .into_iter()
                            .map(|(id, m, p, mesh, _)| (id, m, p, mesh)),
                    );
                    true
                } else {
                    false
                }
            } else {
                false
            }
        } else {
            false
        };

        // 3D Canvas でない場合は通常再帰する（DFS カウンタを全アクターで維持する）
        if !handled {
            collect_3d_canvas_child_id_items(
                &actor.children,
                world,
                wl,
                counter,
                mc_total,
                draw_ctx,
                out,
                panel_out,
            );
        }
    }
}

/// 3D Canvas 子アクターを再帰走査してスプライト ID アイテムを収集するヘルパー。
///
/// `parent_world_rs` は canvas_to_world（3D ワールド行列）で、
/// 各子スプライトのモデル行列を 3D ワールド空間で構築する。
/// 出力タプル末尾の `layer` は呼び出し側のキャンバス内レイヤーソートに使用する。
#[allow(clippy::too_many_arguments)]
fn walk_3d_canvas_children_id(
    actors: &[Actor],
    world: &World,
    wl: u32,
    counter: &mut u32,
    parent_canvas_size: Option<[f32; 2]>,
    parent_world_rs: [[f32; 4]; 4],
    parent_cumul_scale: [f32; 2],
    mc_total: u32,
    draw_ctx: &DrawContext,
    out: &mut Vec<(
        u32,
        [[f32; 4]; 4],
        Option<String>,
        Option<Arc<SkinnedSpriteDraw>>,
        i32,
    )>,
) {
    for actor in actors {
        if actor.world_line != wl {
            continue;
        }
        let my_dfs = *counter;
        *counter += 1;

        // 非アクティブアクター: ピック対象から外す（DFS 番号は子孫分も進める）
        if !actor.active {
            skip_dfs_subtree(&actor.children, counter);
            continue;
        }

        let ct_opt = world.get::<CanvasTransform>(actor.entity).cloned();

        let (next_canvas_size, next_world_rs, next_cumul_scale) = if let Some(ct) = ct_opt {
            // スケールモードはこのノード自身の CanvasTransform から読み取る
            let (sm_transform, sm_size, keep_aspect, is_width_axis) = (
                ct.scale_transform,
                ct.scale_size,
                ct.keep_aspect_ratio,
                matches!(ct.aspect_ratio_axis, AspectRatioAxis::Width),
            );
            // アンカーオフセット（collect_sprite_items の 3D Canvas パスと同じロジック）
            let (anchor_off_x, anchor_off_y) =
                parent_canvas_size.map_or((0.0f32, 0.0f32), |[pw, ph]| {
                    (
                        pw * ct.anchor[0] * parent_cumul_scale[0],
                        ph * ct.anchor[1] * parent_cumul_scale[1],
                    )
                });

            // 有効位置（スケールモードに応じて親累積スケールを適用する）
            let eff_pos = if sm_transform {
                [
                    ct.position[0] * parent_cumul_scale[0] + anchor_off_x,
                    ct.position[1] * parent_cumul_scale[1] + anchor_off_y,
                ]
            } else {
                [ct.position[0] + anchor_off_x, ct.position[1] + anchor_off_y]
            };
            let eff_ct = CanvasTransform {
                position: eff_pos,
                rotation: ct.rotation,
                scale: ct.scale,
                pivot: ct.pivot,
                anchor: [0.0, 0.0],
                ..ct.clone()
            };

            // 自アクターの CanvasComponent
            let my_canvas = actor
                .slots()
                .iter()
                .filter(|s| s.kind == ComponentKind::Canvas)
                .find_map(|s| world.get::<CanvasComponent>(s.entity));

            // sm_size による拡縮スケール（アスペクト比維持を考慮）
            let size_sc_x = if sm_size {
                if keep_aspect && !is_width_axis {
                    parent_cumul_scale[1]
                } else {
                    parent_cumul_scale[0]
                }
            } else {
                1.0
            };
            let size_sc_y = if sm_size {
                if keep_aspect && is_width_axis {
                    parent_cumul_scale[0]
                } else {
                    parent_cumul_scale[1]
                }
            } else {
                1.0
            };
            let (my_eff_w, my_eff_h) = my_canvas
                .map(|cc| (cc.width * size_sc_x, cc.height * size_sc_y))
                .unwrap_or((1.0, 1.0));

            // 自分のワールド RS 行列（CanvasComponent サイズで to_mat4_sized を使う）
            let self_world_rs = mat4x4_mul(
                parent_world_rs,
                CanvasTransform {
                    scale: [1.0, 1.0],
                    ..eff_ct.clone()
                }
                .to_mat4_sized(my_eff_w, my_eff_h),
            );

            // SpriteComponent を持つアクターを ID アイテムとして追加する。
            // テクスチャなし（単色）は白テクスチャフォールバックで全面選択可能にする。
            // （enabled=false のスプライトは非表示のためピック対象外）
            // 3D ワールド行列 → GPU 列優先モデル行列（Z 成分を含めた 3D フルマトリクス）
            let to_gpu_mat = |sw: [[f32; 4]; 4]| {
                [
                    [sw[0][0], sw[1][0], sw[2][0], 0.0],
                    [sw[0][1], sw[1][1], sw[2][1], 0.0],
                    [sw[0][2], sw[1][2], sw[2][2], 0.0],
                    [sw[0][3], sw[1][3], sw[2][3], 1.0],
                ]
            };
            let mut pushed = false;
            for slot in actor.slots() {
                if slot.kind == ComponentKind::Sprite && slot.enabled {
                    if let Some(sc) = world.get::<SpriteComponent>(slot.entity) {
                        let ew = sc.width * size_sc_x;
                        let eh = sc.height * size_sc_y;
                        // 3D ワールド空間のスプライト行列（canvas_to_world 込み）
                        let sw = mat4x4_mul(parent_world_rs, eff_ct.to_sprite_mat4(ew, eh));
                        // テクスチャなしは None → 白フォールバック（全面 alpha=1）
                        let tex_path = if sc.texture_path.is_empty() {
                            None
                        } else {
                            Some(sc.texture_path.clone())
                        };
                        // raw_id = canvas_id_offset + dfs_id（canvas_id_offset = mc_total）
                        // layer は呼び出し側のキャンバス内レイヤーソートに使用する
                        out.push((mc_total + my_dfs + 1, to_gpu_mat(sw), tex_path, None, sc.layer));
                        pushed = true;
                        break;
                    }
                }
            }
            // 矩形スプライトが無いアクターのみ、スキンスプライトをピック対象にする
            // （2D キャンバスの ID 収集 collect_canvas_id_items と同じ優先規約）。
            // 変形済み頂点は本フレームの描画収集が既に作っているので、ここでは
            // ハンドルを引くだけ（まだ作られていない＝描画対象でないならピック対象外）。
            if !pushed {
                for slot in actor.slots() {
                    if slot.kind != ComponentKind::SkinnedSprite || !slot.enabled {
                        continue;
                    }
                    let Some(ss) = world.get::<SkinnedSpriteComponent>(slot.entity) else {
                        continue;
                    };
                    let Some(mesh_draw) = draw_ctx.sprite_skin.draw_handle(slot.entity) else {
                        continue;
                    };
                    // メッシュ頂点は既に実寸を持つので、掛けるのは追加スケールのみ
                    let sw = mat4x4_mul(parent_world_rs, eff_ct.to_mesh_mat4(size_sc_x, size_sc_y));
                    let tex_path = if ss.texture_path.is_empty() {
                        None
                    } else {
                        Some(ss.texture_path.clone())
                    };
                    out.push((
                        mc_total + my_dfs + 1,
                        to_gpu_mat(sw),
                        tex_path,
                        Some(mesh_draw),
                        ss.layer,
                    ));
                    break;
                }
            }

            // 子への基準 Canvas サイズを計算する。
            // スケールモードは各子が自身の CanvasTransform から読み取るため伝播しない。
            let child_canvas_size = my_canvas.map(|cc| [cc.width, cc.height]);
            let child_cumul_scale = if sm_transform {
                [
                    parent_cumul_scale[0] * ct.scale[0],
                    parent_cumul_scale[1] * ct.scale[1],
                ]
            } else {
                [ct.scale[0], ct.scale[1]]
            };
            (child_canvas_size, self_world_rs, child_cumul_scale)
        } else {
            // CanvasTransform なし: 親情報をそのまま引き継ぐ
            (parent_canvas_size, parent_world_rs, parent_cumul_scale)
        };

        // 常に子に再帰する（DFS カウンタを全アクターで維持するため）
        walk_3d_canvas_children_id(
            &actor.children,
            world,
            wl,
            counter,
            next_canvas_size,
            next_world_rs,
            next_cumul_scale,
            mc_total,
            draw_ctx,
            out,
        );
    }
}

// ============================================================
//  collect_3d_canvas_child_outlines
// ============================================================

/// 3D Canvas（Actor3D + CanvasComponent）配下にネストされた子キャンバス
/// （CanvasComponent を持つ Actor2D）のアウトライン矩形コーナーを 3D ワールド空間で収集する。
///
/// # 目的
/// 3D キャンバスの子キャンバス枠は、ルート 3D キャンバス枠パス（frame_renderer）が
/// 子へ再帰しないため描画されていなかった。本関数はスプライトの子走査
/// （`walk_3d_canvas_children_id` / `collect_sprite_items`）と**同一の変換連鎖**で
/// 各ネストキャンバスの矩形を求めるため、枠が子キャンバスの描画スプライトと一致する。
///
/// # 変換の一致
/// スプライトは `mat4x4_mul(parent_world_rs, eff_ct.to_sprite_mat4(W, H))` をユニット
/// クワッドに適用して配置される。本関数の枠は `eff_ct.to_mat4_sized(W, H)` を
/// `[0,W]×[0,H]` のコーナーに適用する。`to_mat4_sized(W,H)` を `(W*u, H*v)` に適用した
/// 結果は `to_sprite_mat4(W,H)` を `(u,v)` に適用した結果と一致するため、枠 == スプライト。
///
/// # DFS カウンタ
/// `find_actor_by_dfs` の子ノード規則（world_line 無関係に子孫を全カウント）で `counter`
/// を進めるため、返却する `dfs_id` は選択ハイライト（`selected_actor_dfs_ids`）と整合する。
///
/// 出力タプル: `([TL, TR, BR, BL] の 3D ワールド座標, そのノードの dfs_id)`
#[allow(clippy::too_many_arguments)]
pub(super) fn collect_3d_canvas_child_outlines(
    actors: &[Actor],
    world: &World,
    wl: u32,
    counter: &mut u32,
    parent_canvas_size: Option<[f32; 2]>,
    parent_world_rs: [[f32; 4]; 4],
    parent_cumul_scale: [f32; 2],
    out: &mut Vec<([[f32; 3]; 4], u32)>,
) {
    for actor in actors {
        // このノードの 0 始まり DFS 番号（find_actor_by_dfs の子規則と同一。
        // 子ノードは world_line で除外せず全カウントする）。
        let my_dfs = *counter;
        *counter += 1;

        // 非アクティブアクター: スプライトが描画されないため枠も描かない。
        // DFS 番号は選択系と整合させるため子孫分も進める。
        if !actor.active {
            skip_dfs_subtree(&actor.children, counter);
            continue;
        }

        let ct_opt = world.get::<CanvasTransform>(actor.entity).cloned();

        let (next_canvas_size, next_world_rs, next_cumul_scale) = if let Some(ct) = ct_opt {
            // スケールモードはこのノード自身の CanvasTransform から読み取る
            let (sm_transform, sm_size, keep_aspect, is_width_axis) = (
                ct.scale_transform,
                ct.scale_size,
                ct.keep_aspect_ratio,
                matches!(ct.aspect_ratio_axis, AspectRatioAxis::Width),
            );
            // アンカーオフセット（walk_3d_canvas_children_id / collect_sprite_items と同じ）
            let (anchor_off_x, anchor_off_y) =
                parent_canvas_size.map_or((0.0f32, 0.0f32), |[pw, ph]| {
                    (
                        pw * ct.anchor[0] * parent_cumul_scale[0],
                        ph * ct.anchor[1] * parent_cumul_scale[1],
                    )
                });
            // 有効位置（スケールモードに応じて親累積スケールを適用する）
            let eff_pos = if sm_transform {
                [
                    ct.position[0] * parent_cumul_scale[0] + anchor_off_x,
                    ct.position[1] * parent_cumul_scale[1] + anchor_off_y,
                ]
            } else {
                [ct.position[0] + anchor_off_x, ct.position[1] + anchor_off_y]
            };
            let eff_ct = CanvasTransform {
                position: eff_pos,
                rotation: ct.rotation,
                scale: ct.scale,
                pivot: ct.pivot,
                anchor: [0.0, 0.0],
                ..ct.clone()
            };

            // 自アクターの CanvasComponent
            let my_canvas = actor
                .slots()
                .iter()
                .filter(|s| s.kind == ComponentKind::Canvas)
                .find_map(|s| world.get::<CanvasComponent>(s.entity));

            // sm_size による拡縮スケール（アスペクト比維持を考慮）
            let size_sc_x = if sm_size {
                if keep_aspect && !is_width_axis {
                    parent_cumul_scale[1]
                } else {
                    parent_cumul_scale[0]
                }
            } else {
                1.0
            };
            let size_sc_y = if sm_size {
                if keep_aspect && is_width_axis {
                    parent_cumul_scale[0]
                } else {
                    parent_cumul_scale[1]
                }
            } else {
                1.0
            };
            let (my_eff_w, my_eff_h) = my_canvas
                .map(|cc| (cc.width * size_sc_x, cc.height * size_sc_y))
                .unwrap_or((1.0, 1.0));

            // 子伝播用のワールド RS 行列（scale=[1,1]。スプライト子走査と同一）
            let self_world_rs = mat4x4_mul(
                parent_world_rs,
                CanvasTransform {
                    scale: [1.0, 1.0],
                    ..eff_ct.clone()
                }
                .to_mat4_sized(my_eff_w, my_eff_h),
            );

            // このノードが CanvasComponent を持つなら、その矩形アウトラインを出力する。
            // 枠行列は eff_ct（scale 込み）× to_mat4_sized（2D collect_canvas_rects の枠と同一形）。
            if my_canvas.is_some() {
                let m = mat4x4_mul(parent_world_rs, eff_ct.to_mat4_sized(my_eff_w, my_eff_h));
                let tp = |cx: f32, cy: f32| -> [f32; 3] {
                    [
                        m[0][0] * cx + m[0][1] * cy + m[0][3],
                        m[1][0] * cx + m[1][1] * cy + m[1][3],
                        m[2][0] * cx + m[2][1] * cy + m[2][3],
                    ]
                };
                out.push((
                    [
                        tp(0.0, 0.0),
                        tp(my_eff_w, 0.0),
                        tp(my_eff_w, my_eff_h),
                        tp(0.0, my_eff_h),
                    ],
                    my_dfs,
                ));
            }

            // 子への基準 Canvas サイズと累積スケール（walk_3d_canvas_children_id と同一）
            let child_canvas_size = my_canvas.map(|cc| [cc.width, cc.height]);
            let child_cumul_scale = if sm_transform {
                [
                    parent_cumul_scale[0] * ct.scale[0],
                    parent_cumul_scale[1] * ct.scale[1],
                ]
            } else {
                [ct.scale[0], ct.scale[1]]
            };
            (child_canvas_size, self_world_rs, child_cumul_scale)
        } else {
            // CanvasTransform なし: 親情報をそのまま引き継ぐ
            (parent_canvas_size, parent_world_rs, parent_cumul_scale)
        };

        // 常に子に再帰する（DFS カウンタを全アクターで維持するため）
        collect_3d_canvas_child_outlines(
            &actor.children,
            world,
            wl,
            counter,
            next_canvas_size,
            next_world_rs,
            next_cumul_scale,
            out,
        );
    }
}

/// 3D Canvas 子スプライトのワールド空間コーナー座標を計算して返す。
///
/// 戻り値: `[TL, TR, BR, BL]` の 3D ワールド座標。
/// `sprite_world` は `mat4x4_mul(canvas_to_world, eff_ct.to_sprite_mat4(w, h))` の行優先行列。
pub(super) fn sprite_world_corners(sprite_world: &[[f32; 4]; 4]) -> [[f32; 3]; 4] {
    // unit quad の各 UV コーナーに sprite_world を適用する
    let corner = |u: f32, v: f32| -> [f32; 3] {
        [
            u * sprite_world[0][0] + v * sprite_world[0][1] + sprite_world[0][3],
            u * sprite_world[1][0] + v * sprite_world[1][1] + sprite_world[1][3],
            u * sprite_world[2][0] + v * sprite_world[2][1] + sprite_world[2][3],
        ]
    };
    [
        corner(0.0, 0.0),
        corner(1.0, 0.0),
        corner(1.0, 1.0),
        corner(0.0, 1.0),
    ]
}

// ============================================================
//  canvas_viewport_utils — CanvasViewportRef::Camera 解決ユーティリティ
//
//  frame_renderer.rs と physics2d_ops.rs の両方から参照されるため、
//  canvas_collect.rs (pub(super)) に配置する。
// ============================================================

/// スケーリングモードに応じたゲームビューポート矩形・アスペクト比・FOV を計算する。
///
/// 戻り値: (vp_x, vp_y, vp_w, vp_h, proj_aspect, fov_y_rad)
pub(super) fn compute_game_viewport(
    scaling_mode: &ScalingMode,
    window_w: f32,
    window_h: f32,
    target_w: u32,
    target_h: u32,
    fov_y_deg: f32,
) -> (f32, f32, f32, f32, f32, f32) {
    let tw = target_w.max(1) as f32;
    let th = target_h.max(1) as f32;
    let target_aspect = tw / th;
    let window_aspect = if window_h > 0.0 {
        window_w / window_h
    } else {
        target_aspect
    };
    let fov_y_rad = fov_y_deg.to_radians();

    match scaling_mode {
        ScalingMode::VertMinus => (0.0, 0.0, window_w, window_h, window_aspect, fov_y_rad),
        ScalingMode::HorPlus => {
            let fov_x = 2.0 * ((fov_y_rad * 0.5).tan() * target_aspect).atan();
            let fov_y_adj = if window_aspect > 0.0 {
                2.0 * ((fov_x * 0.5).tan() / window_aspect).atan()
            } else {
                fov_y_rad
            };
            (0.0, 0.0, window_w, window_h, window_aspect, fov_y_adj)
        }
        ScalingMode::LetterBox => {
            let scale = window_w / tw;
            let vp_h = (th * scale).min(window_h);
            let y_off = ((window_h - vp_h) * 0.5).max(0.0);
            (0.0, y_off, window_w, vp_h, target_aspect, fov_y_rad)
        }
        ScalingMode::PillarBox => {
            let scale = window_h / th;
            let vp_w = (tw * scale).min(window_w);
            let x_off = ((window_w - vp_w) * 0.5).max(0.0);
            (x_off, 0.0, vp_w, window_h, target_aspect, fov_y_rad)
        }
        ScalingMode::LetterPillarBox => {
            if window_aspect >= target_aspect {
                let vp_w = window_h * target_aspect;
                let x_off = ((window_w - vp_w) * 0.5).max(0.0);
                (x_off, 0.0, vp_w, window_h, target_aspect, fov_y_rad)
            } else {
                let vp_h = window_w / target_aspect;
                let y_off = ((window_h - vp_h) * 0.5).max(0.0);
                (0.0, y_off, window_w, vp_h, target_aspect, fov_y_rad)
            }
        }
        ScalingMode::FullScale => (0.0, 0.0, window_w, window_h, target_aspect, fov_y_rad),
    }
}

/// ゲームビューポート矩形を実レンダーターゲットサイズへクランプする（set_viewport 安全化）。
///
/// wgpu の `RenderPass::set_viewport` は、矩形の原点・サイズがレンダーターゲット外、
/// または幅・高さが 0 以下だとバリデーションエラーでパニックする。さらにパニックが
/// 巻き戻る際に未 present の `SurfaceTexture` 破棄で二次パニック（プロセス abort）を
/// 誘発する。埋め込み Play ではリサイズ中に「window.inner_size() 由来で計算した
/// game_viewport」と「スワップチェーンが currentExtent へクランプした実サーフェス
/// サイズ」が 1 フレームだけ食い違い、これが起きる。
///
/// 本関数は set_viewport を呼ぶ全箇所の最終防衛線として、矩形を `[0, target]` の範囲へ
/// クランプする。
///
/// # 引数
/// - `vp`:       クランプ対象のビューポート矩形 `(x, y, w, h)`（ピクセル）。
/// - `target_w`: 実レンダーターゲットの幅（ピクセル）。
/// - `target_h`: 実レンダーターゲットの高さ（ピクセル）。
///
/// # 戻り値
/// - `Some((x, y, w, h))`: クランプ後の有効な矩形（幅・高さともに 1px 以上）。
/// - `None`:               クランプ結果が縮退（幅または高さ 1px 未満）した場合。
///                         呼び出し側はそのフレームのビューポート設定をスキップする。
pub(super) fn clamp_viewport_to_target(
    vp:       (f32, f32, f32, f32),
    target_w: f32,
    target_h: f32,
) -> Option<(f32, f32, f32, f32)> {
    let (x, y, w, h) = vp;
    // 原点は [0, target] に収め、右下端（x+w, y+h）も同様に収める。
    // その差分を新しい幅・高さとすることで、はみ出し分を確実に切り落とす。
    let x0 = x.clamp(0.0, target_w);
    let y0 = y.clamp(0.0, target_h);
    let x1 = (x + w).clamp(0.0, target_w);
    let y1 = (y + h).clamp(0.0, target_h);
    let cw = x1 - x0;
    let ch = y1 - y0;
    // 1px 未満は wgpu が拒否する（かつ描画しても不可視）ため、縮退として弾く。
    if cw < 1.0 || ch < 1.0 {
        return None;
    }
    Some((x0, y0, cw, ch))
}

/// DFS でアクター名と指定スロット名の CameraComponent を持つ最初のアクターを返す。
///
/// `CanvasViewportRef::Camera { actor_name, slot_name }` の参照解決に使用する。
/// component_ops（インスペクタ用自動解像度計算）からも共用するため pub(super)。
pub(super) fn find_camera_component_by_ref<'a>(
    actors: &'a [Actor],
    world: &'a World,
    actor_name: &str,
    slot_name: &str,
) -> Option<&'a CameraComponent> {
    for a in actors {
        if a.name == actor_name {
            if let Some(cam) = a
                .slots()
                .iter()
                .find(|s| s.name == slot_name && s.kind == ComponentKind::Camera)
                .and_then(|s| world.get::<CameraComponent>(s.entity))
            {
                return Some(cam);
            }
        }
        if let Some(found) =
            find_camera_component_by_ref(a.children(), world, actor_name, slot_name)
        {
            return Some(found);
        }
    }
    None
}

/// 指定世界線のアクターツリーから is_main = true の CameraComponent を DFS 順で探す。
///
/// `CanvasViewportRef::MainCamera` の参照解決に使用する。
/// component_ops（インスペクタ用自動解像度計算）からも共用するため pub(super)。
pub(super) fn find_main_camera_in_wl<'a>(
    actors: &'a [Actor],
    world: &'a World,
    wl: u32,
) -> Option<&'a CameraComponent> {
    /// サブツリー内を DFS で探索する内部ヘルパー（world_line チェックはルートのみ）。
    fn find_main_camera<'a>(actors: &'a [Actor], world: &'a World) -> Option<&'a CameraComponent> {
        for a in actors {
            for slot in a.slots() {
                if slot.kind != ComponentKind::Camera {
                    continue;
                }
                if let Some(cam) = world.get::<CameraComponent>(slot.entity) {
                    if cam.is_main {
                        return Some(cam);
                    }
                }
            }
            if let Some(found) = find_main_camera(a.children(), world) {
                return Some(found);
            }
        }
        None
    }
    // トップレベルは world_line でフィルタしてから DFS する
    // （子アクターはルートと同じ世界線に属する前提）
    for root in actors.iter().filter(|a| a.world_line == wl) {
        if let Some(cam) = find_main_camera(std::slice::from_ref(root), world) {
            return Some(cam);
        }
    }
    None
}

/// `CanvasViewportRef::Camera` で参照されるカメラアクターのビューポートサイズを解決する。
///
/// 参照カメラのスケーリングモードを適用し、帯を除いたコンテンツ領域のサイズを返す。
/// 参照先が見つからない場合は `[win_w, win_h]` を返す。
pub(super) fn resolve_camera_viewport_size(
    actors: &[Actor],
    world: &World,
    cam_actor_name: &str,
    cam_slot_name: &str,
    win_w: f32,
    win_h: f32,
) -> [f32; 2] {
    let Some(cam) = find_camera_component_by_ref(actors, world, cam_actor_name, cam_slot_name)
    else {
        return [win_w, win_h];
    };

    let (_, _, rendered_w, rendered_h, _, _) = compute_game_viewport(
        &cam.scaling_mode,
        win_w,
        win_h,
        cam.target_width,
        cam.target_height,
        cam.fov_y_deg,
    );
    [rendered_w, rendered_h]
}

/// `CanvasViewportRef::MainCamera` 用: 同一世界線内のメインカメラの実効表示領域サイズを解決する。
///
/// 指定世界線のアクターツリーを DFS 順に走査し、最初に見つかった `is_main = true` の
/// `CameraComponent` について、明示 Camera 参照と同じ計算（`compute_game_viewport` による
/// スケーリングモード + 設計解像度 + ウィンドウサイズからのレターボックス補正済み矩形）で
/// 帯を除いたゲーム表示領域のサイズを返す。
/// メインカメラが存在しない場合は `None` を返す（= ウィンドウ基準へフォールバック）。
pub(super) fn resolve_main_camera_viewport_size(
    actors: &[Actor],
    world: &World,
    wl: u32,
    win_w: f32,
    win_h: f32,
) -> Option<[f32; 2]> {
    let cam = find_main_camera_in_wl(actors, world, wl)?;
    // 明示 Camera 参照（resolve_camera_viewport_size）と同一の実効矩形計算を再利用する
    let (_, _, rendered_w, rendered_h, _, _) = compute_game_viewport(
        &cam.scaling_mode,
        win_w,
        win_h,
        cam.target_width,
        cam.target_height,
        cam.fov_y_deg,
    );
    Some([rendered_w, rendered_h])
}

// ============================================================
//  ルートキャンバス自動解像度（Phase B: canvas_camera_rework.md 第 2 節）
//
//  ビューポート所属のルートキャンバスは、保存された width / height ではなく
//  「プロジェクト設定の解像度 × 参照カメラのスケーリングモード」から
//  実効解像度を導出する（編集時の事前計算。実行時の動的リサイズ追従は
//  従来どおり auto_scale / スケールモードが担う）。
//  保存データ（width / height）は書き換えず温存する。
// ============================================================

/// プロジェクト設定解像度と参照カメラ設定からルートキャンバスの実効解像度を計算する。
///
/// - カメラ参照あり: プロジェクト設定解像度をウィンドウサイズと見なして
///   カメラのスケーリングモードでフィットさせ、帯を除いた矩形サイズを返す
/// - カメラ参照なし（Window 参照・カメラ不在）: プロジェクト設定解像度をそのまま返す
///
/// 全呼び出し箇所（描画・物理・ピッキング・インスペクタ表示）で
/// 同一計算を共有するための単一の正典関数。
pub(super) fn effective_root_canvas_size(
    project_res: (u32, u32),
    camera: Option<&CameraComponent>,
) -> [f32; 2] {
    let proj_w = project_res.0.max(1) as f32;
    let proj_h = project_res.1.max(1) as f32;
    match camera {
        Some(cam) => {
            // 明示 Camera / MainCamera 参照と同一のスケーリングモード計算を再利用する
            let (_, _, rendered_w, rendered_h, _, _) = compute_game_viewport(
                &cam.scaling_mode,
                proj_w,
                proj_h,
                cam.target_width,
                cam.target_height,
                cam.fov_y_deg,
            );
            [rendered_w, rendered_h]
        }
        None => [proj_w, proj_h],
    }
}

/// ビューポート所属のルートキャンバス（トップレベル Actor2D + CanvasComponent）ごとに
/// 自動解像度（effective_root_canvas_size）を事前解決したマップを構築する。
///
/// このマップに登録されたルートキャンバスは、レイアウト計算時に
/// - width / height をマップの値へ置き換え
/// - CanvasTransform を恒等（position/rotation/pivot/anchor = 0, scale = 1）として扱う
///   （保存データは書き換えない）
/// シーン SS レイアウト時のみ構築し、アクター編集タブ・ワールドキャンバスでは
/// 空マップを渡して従来動作を維持する。
pub(super) fn build_root_canvas_auto_size_map(
    actors: &[Actor],
    world: &World,
    wl: u32,
    project_res: (u32, u32),
) -> HashMap<Entity, [f32; 2]> {
    let mut map = HashMap::new();
    // MainCamera 参照の解決結果はキャンバス間で共通のため 1 回だけ計算してキャッシュする
    let mut main_cam: Option<Option<&CameraComponent>> = None;
    for actor in actors {
        if actor.world_line != wl {
            continue;
        }
        // ビューポート所属ルートキャンバス = トップレベル Actor2D + CanvasTransform + Canvas スロット
        if !actor.is_2d() {
            continue;
        }
        if world.get::<CanvasTransform>(actor.entity).is_none() {
            continue;
        }
        for slot in actor.slots() {
            if slot.kind != ComponentKind::Canvas {
                continue;
            }
            if let Some(cc) = world.get::<CanvasComponent>(slot.entity) {
                // 参照カメラを解決する（Window / カメラ不在は None = プロジェクト解像度をそのまま使用）
                let cam = match &cc.viewport_ref {
                    CanvasViewportRef::Camera {
                        actor_name,
                        slot_name,
                    } => find_camera_component_by_ref(actors, world, actor_name, slot_name),
                    CanvasViewportRef::MainCamera => {
                        *main_cam.get_or_insert_with(|| find_main_camera_in_wl(actors, world, wl))
                    }
                    CanvasViewportRef::Window => None,
                };
                map.insert(actor.entity, effective_root_canvas_size(project_res, cam));
                break;
            }
        }
    }
    map
}

/// シーン SS レイアウト用の 2 つの上書きマップをまとめて構築する共通ヘルパー（フリー関数版）。
///
/// 戻り値: (ビューポート上書きマップ, ルート自動解像度マップ)
/// - ビューポート上書きマップ: `CanvasViewportRef::Camera / MainCamera` の実効表示領域
///   （ルートのアンカー基準・auto_scale の分母に使われる）
/// - ルート自動解像度マップ: ビューポート・ルートキャンバスの実効解像度 + Transform 恒等化キー
///
/// # `design_space`（Edit View2D = ビューポートタブの設計空間表示）
/// true のとき、ルートキャンバスの基準ビューポートを**自動解像度そのもの**へ上書きする。
/// これにより
///   - ルートのアンカーオフセット = `-自動解像度/2` → **キャンバス中心 = ortho 原点**
///   - auto_scale 係数 = 自動解像度/自動解像度 = **1（設計解像度のまま表示）**
/// となり、ライブウィンドウ（エディタパネル）サイズに依存しない安定した
/// WYSIWYG 編集空間になる。Play・SS オーバーレイ表示（false）は従来どおり
/// 実ウィンドウ基準（ゲームの最終合成と同じ）のまま変化しない。
///
/// 描画・GPU ID ピッキング・2D 物理・D&D 配置のすべてがこのヘルパーを共有することで
/// 座標系の一貫性を保証する。
/// frame_renderer（self.renderer 可変借用中）から呼べるようフリー関数として提供し、
/// それ以外の呼び出し元向けに同名の App メソッドを用意する。
#[allow(clippy::too_many_arguments)]
pub(super) fn build_ss_layout_maps_free(
    actors: &[Actor],
    world: &World,
    wl: u32,
    win_w: f32,
    win_h: f32,
    play_gvp: Option<(f32, f32, f32, f32)>,
    project_res: (u32, u32),
    design_space: bool,
) -> (HashMap<Entity, [f32; 2]>, HashMap<Entity, [f32; 2]>) {
    let mut vp_overrides = build_canvas_viewport_map(actors, world, wl, win_w, win_h, play_gvp);
    let auto_sizes = build_root_canvas_auto_size_map(actors, world, wl, project_res);
    // ビューポートタブ: ルートの基準ビューポートを自動解像度へ上書き（設計空間表示）
    if design_space {
        for (entity, size) in &auto_sizes {
            vp_overrides.insert(*entity, *size);
        }
    }
    (vp_overrides, auto_sizes)
}

impl super::App {
    /// build_ss_layout_maps_free の App メソッド版。
    /// プロジェクト解像度キャッシュと Edit View2D 判定を self から補完する。
    pub(super) fn build_ss_layout_maps(
        &self,
        actors: &[Actor],
        world: &World,
        wl: u32,
        win_w: f32,
        win_h: f32,
        play_gvp: Option<(f32, f32, f32, f32)>,
    ) -> (HashMap<Entity, [f32; 2]>, HashMap<Entity, [f32; 2]>) {
        build_ss_layout_maps_free(
            actors,
            world,
            wl,
            win_w,
            win_h,
            play_gvp,
            self.project_resolution,
            self.edit_view_is_2d(),
        )
    }
}

/// シーン SS モード時に各ルートキャンバスアクターの有効ビューポートサイズを事前解決する。
///
/// `CanvasViewportRef::Camera` / `CanvasViewportRef::MainCamera` を持つルートキャンバスのみ
/// マップに追加する。`Window` 参照はデフォルトの `viewport_size` にフォールバックするため不要。
/// `MainCamera` 参照でメインカメラが見つからない場合もマップに追加しない
/// （= `Window` と同じ挙動になる）。
pub(super) fn build_canvas_viewport_map(
    actors: &[Actor],
    world: &World,
    wl: u32,
    win_w: f32,
    win_h: f32,
    _game_viewport: Option<(f32, f32, f32, f32)>,
) -> HashMap<Entity, [f32; 2]> {
    let mut map = HashMap::new();
    // MainCamera 参照の解決結果はキャンバス間で共通のため 1 回だけ計算してキャッシュする
    // （None = 未計算、Some(None) = メインカメラなし、Some(Some(vp)) = 解決済み）
    let mut main_cam_vp: Option<Option<[f32; 2]>> = None;
    for actor in actors {
        if actor.world_line != wl {
            continue;
        }
        if world.get::<CanvasTransform>(actor.entity).is_none() {
            continue;
        }
        for slot in actor.slots() {
            if slot.kind != ComponentKind::Canvas {
                continue;
            }
            if let Some(cc) = world.get::<CanvasComponent>(slot.entity) {
                match &cc.viewport_ref {
                    CanvasViewportRef::Camera {
                        actor_name,
                        slot_name,
                    } => {
                        let vp = resolve_camera_viewport_size(
                            actors, world, actor_name, slot_name, win_w, win_h,
                        );
                        map.insert(actor.entity, vp);
                    }
                    CanvasViewportRef::MainCamera => {
                        // 遅延解決（このフレームで最初に必要になったときのみ計算する）
                        let resolved = *main_cam_vp.get_or_insert_with(|| {
                            resolve_main_camera_viewport_size(actors, world, wl, win_w, win_h)
                        });
                        // メインカメラなし → マップ未登録のままウィンドウ基準にフォールバック
                        if let Some(vp) = resolved {
                            map.insert(actor.entity, vp);
                        }
                    }
                    CanvasViewportRef::Window => {}
                }
                break;
            }
        }
    }
    map
}
