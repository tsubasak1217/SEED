// ============================================================
//  camera_scene_gizmo.rs — カメラアクター視覚化ギズモ
//
//  Edit モードのシーン上でカメラコンポーネントを持つアクターを
//  視覚的に示すための 3 つのギズモを提供する。
//
//  1. カメラアイコン (ローポリソリッドメッシュ)
//     - CameraComponent を持つ全アクターの位置にカメラ形状を描画する。
//     - GizmoBatch の gizmo_tri_pipeline で常に最前面に描画される。
//
//  2. フラスタム可視化 (選択時ライン描画)
//     - 選択中のカメラアクターの投影錐台を黄色いラインで描画する。
//     - 遠クリップを MAX_FRUSTUM_FAR でクランプして描画範囲を制限する。
//
//  3. カメラプレビュー描画（呼び出し元 render.rs が担当）
//     - ここではカメラ行列の構築のみを提供する。
// ============================================================

use std::f32::consts::PI;

use crate::engine::ecs::World;
use crate::engine::structs::objects::Actor;
use crate::engine::structs::tensor::{Vector3, Mat4x4};
use crate::engine::components::{
    ComponentKind, CameraComponent, CameraProjection,
    Transform as ActorTransform,
};
use crate::engine::methods::drawer::{GizmoBatch, LineBatch, CameraUniform, extract_frustum_planes};
use crate::engine::structs::utils::Color;

// ── カメラアイコン寸法定数（ローカル座標）────────────────────────────────────
// 単位はローカル空間（1.0 = 1 ユニット、CAMERA_ICON_SCALE で実スケールに変換）

/// アイコンのワールドスケール係数（アクターのスケールとは独立した固定サイズ）。
const CAMERA_ICON_SCALE: f32 = 0.45;

/// ボディボックスの X 方向半幅。
const BODY_HW: f32 = 0.30;
/// ボディボックスの Y 方向半高さ。
const BODY_HH: f32 = 0.18;
/// ボディボックスの Z 方向半奥行き（カメラ奥から前面まで）。
const BODY_HD: f32 = 0.22;

/// レンズ円柱の外半径。
const LENS_R: f32 = 0.13;
/// レンズ円柱の開始 Z（ボディ前面）。
const LENS_Z0: f32 = 0.22;
/// レンズ円柱の終了 Z（先端）。
const LENS_Z1: f32 = 0.45;
/// レンズ多角形の分割数。
const LENS_SEGS: usize = 8;

/// ビューファインダー（上部バンプ）の X 方向半幅。
const VF_HW: f32 = 0.09;
/// ビューファインダーの底部 Y（ボディ上面と同じ）。
const VF_Y0: f32 = 0.18;
/// ビューファインダーの頂部 Y。
const VF_Y1: f32 = 0.30;
/// ビューファインダーの Z 開始（ボディ後方側）。
const VF_Z0: f32 = -0.14;
/// ビューファインダーの Z 終了（ボディ前方側）。
const VF_Z1: f32 = 0.06;

// ── カメラアイコン色 ─────────────────────────────────────────────────────────
/// ボディ・ビューファインダーの色（薄グレー）。
const CAM_BODY_COLOR: Color = Color::new(0.62, 0.62, 0.65, 1.0);
/// レンズの色（濃グレー）。
const CAM_LENS_COLOR: Color = Color::new(0.20, 0.20, 0.22, 1.0);
/// レンズの前面ガラス色（暗い青みのある濃色）。
const CAM_GLASS_COLOR: Color = Color::new(0.10, 0.15, 0.22, 0.90);

// ── フラスタム定数 ──────────────────────────────────────────────────────────
/// フラスタム描画の遠クリップ最大値（m 単位）。カメラ設定が大きくてもこれでクランプ。
const MAX_FRUSTUM_FAR: f32 = 30.0;

/// フラスタムラインの色（黄色）。
const FRUSTUM_COLOR: [f32; 4] = [1.0, 0.82, 0.10, 0.90];

// ============================================================
//  ヘルパー関数
// ============================================================

/// ローカル空間の点をワールド空間に変換する。
///
/// 行列形式は `Transform::to_mat4()` の行優先形式（m[row][col]）。
/// 平行移動成分は m[row][3] に格納されている。
#[inline]
fn transform_pt(m: &[[f32; 4]; 4], p: [f32; 3]) -> [f32; 3] {
    [
        m[0][0] * p[0] + m[0][1] * p[1] + m[0][2] * p[2] + m[0][3],
        m[1][0] * p[0] + m[1][1] * p[1] + m[1][2] * p[2] + m[1][3],
        m[2][0] * p[0] + m[2][1] * p[1] + m[2][2] * p[2] + m[2][3],
    ]
}

/// ベクトルの加算。
#[inline]
fn add3(a: [f32; 3], b: [f32; 3]) -> [f32; 3] {
    [a[0] + b[0], a[1] + b[1], a[2] + b[2]]
}

/// ベクトルのスカラー乗算。
#[inline]
fn scale3(v: [f32; 3], s: f32) -> [f32; 3] {
    [v[0] * s, v[1] * s, v[2] * s]
}

/// ベクトルを正規化する。長さがほぼ 0 の場合は [0,0,0] を返す。
#[inline]
fn normalize3(v: [f32; 3]) -> [f32; 3] {
    let len = (v[0] * v[0] + v[1] * v[1] + v[2] * v[2]).sqrt();
    if len < 1e-6 { return [0.0; 3]; }
    [v[0] / len, v[1] / len, v[2] / len]
}

/// アクター Transform からスケールを除いた回転+平行移動のみの 4x4 行列を返す。
/// カメラアイコンは常に CAMERA_ICON_SCALE で固定描画するためスケールは無視する。
fn icon_matrix(tf: &ActorTransform) -> [[f32; 4]; 4] {
    ActorTransform {
        position: tf.position,
        rotation: tf.rotation,
        scale:    [CAMERA_ICON_SCALE, CAMERA_ICON_SCALE, CAMERA_ICON_SCALE],
    }.to_mat4()
}

/// ローカル空間の AABB ボックスを変換してソリッド三角形を GizmoBatch に追加する。
/// `cull_mode = "None"` のため面の向きは問わない。
fn add_box_world(
    batch: &mut GizmoBatch,
    mat:   &[[f32; 4]; 4],
    min:   [f32; 3],
    max:   [f32; 3],
    color: Color,
) {
    let [x0, y0, z0] = min;
    let [x1, y1, z1] = max;

    // 8 コーナー（ローカル空間→ワールド空間に変換）
    let lv = [
        [x0, y0, z0], [x1, y0, z0], [x1, y1, z0], [x0, y1, z0],  // 0-3: -Z 面
        [x0, y0, z1], [x1, y0, z1], [x1, y1, z1], [x0, y1, z1],  // 4-7: +Z 面
    ];
    let v: [_; 8] = std::array::from_fn(|i| transform_pt(mat, lv[i]));

    // 6 面を CCW 三角形 2 枚で描画
    let faces: [(usize, usize, usize, usize); 6] = [
        (4, 5, 6, 7),  // +Z 面（前面）
        (1, 0, 3, 2),  // -Z 面（背面）
        (5, 1, 2, 6),  // +X 面（右）
        (0, 4, 7, 3),  // -X 面（左）
        (3, 7, 6, 2),  // +Y 面（上）
        (4, 0, 1, 5),  // -Y 面（下）
    ];
    for (a, b, c, d) in faces {
        batch.add_solid_tri(v[a], v[b], v[c], color);
        batch.add_solid_tri(v[a], v[c], v[d], color);
    }
}

/// ローカル空間の円柱（多角形プリズム）を変換してソリッド三角形を GizmoBatch に追加する。
///
/// - `z0`: 後端 Z、`z1`: 前端 Z
/// - `radius`: 円半径
/// - `segs`: 多角形の辺数（推奨 8）
fn add_cylinder_world(
    batch:  &mut GizmoBatch,
    mat:    &[[f32; 4]; 4],
    z0:     f32,
    z1:     f32,
    radius: f32,
    segs:   usize,
    color:  Color,
) {
    let n = segs.max(3);
    for i in 0..n {
        let t0 = 2.0 * PI * i as f32 / n as f32;
        let t1 = 2.0 * PI * (i + 1) as f32 / n as f32;
        let (s0, c0) = t0.sin_cos();
        let (s1, c1) = t1.sin_cos();
        // ローカル空間コーナー
        let b0 = transform_pt(mat, [radius * c0, radius * s0, z0]);
        let b1 = transform_pt(mat, [radius * c1, radius * s1, z0]);
        let f0 = transform_pt(mat, [radius * c0, radius * s0, z1]);
        let f1 = transform_pt(mat, [radius * c1, radius * s1, z1]);
        let fc = transform_pt(mat, [0.0, 0.0, z1]);  // 前面中心

        // 側面 quad（2 三角形）
        batch.add_solid_tri(b0, b1, f1, color);
        batch.add_solid_tri(b0, f1, f0, color);

        // 前面ファン三角形
        batch.add_solid_tri(fc, f0, f1, color);
    }
}

// ============================================================
//  カメラアイコン生成
// ============================================================

/// 指定 Transform のカメラアイコン三角形を GizmoBatch に追加する。
///
/// アイコンはカメラアクターの位置・向きに合わせて描画され、
/// アクターのスケールには依存せず常に CAMERA_ICON_SCALE で固定される。
fn add_camera_icon(batch: &mut GizmoBatch, tf: &ActorTransform) {
    let mat = icon_matrix(tf);

    // ① ボディボックス
    add_box_world(
        batch, &mat,
        [-BODY_HW, -BODY_HH, -BODY_HD],
        [BODY_HW,   BODY_HH,  BODY_HD],
        CAM_BODY_COLOR,
    );

    // ② レンズ円柱（前面に突出）
    add_cylinder_world(
        batch, &mat,
        LENS_Z0, LENS_Z1, LENS_R, LENS_SEGS, CAM_LENS_COLOR,
    );

    // ③ レンズ前面（ガラス面：濃い青みグレー）
    add_cylinder_cap(batch, &mat, LENS_Z1, LENS_R, LENS_SEGS, CAM_GLASS_COLOR);

    // ④ ビューファインダー（上部の小さな突起）
    add_box_world(
        batch, &mat,
        [-VF_HW,  VF_Y0, VF_Z0],
        [ VF_HW,  VF_Y1, VF_Z1],
        CAM_BODY_COLOR,
    );
}

/// レンズ前端の円形キャップ（前面ディスク）を追加する。
fn add_cylinder_cap(
    batch:  &mut GizmoBatch,
    mat:    &[[f32; 4]; 4],
    z:      f32,
    radius: f32,
    segs:   usize,
    color:  Color,
) {
    let n = segs.max(3);
    let center = transform_pt(mat, [0.0, 0.0, z]);
    for i in 0..n {
        let t0 = 2.0 * PI * i as f32 / n as f32;
        let t1 = 2.0 * PI * (i + 1) as f32 / n as f32;
        let (s0, c0) = t0.sin_cos();
        let (s1, c1) = t1.sin_cos();
        let p0 = transform_pt(mat, [radius * c0, radius * s0, z]);
        let p1 = transform_pt(mat, [radius * c1, radius * s1, z]);
        batch.add_solid_tri(center, p0, p1, color);
    }
}

// ============================================================
//  公開 API
// ============================================================

/// CameraComponent を持つ全アクターの (DFS ID, アイコン変換行列) リストを返す。
///
/// アイコン変換行列はアクターの位置・回転を保持しつつ
/// `CAMERA_ICON_SCALE` で固定スケールを適用した 4x4 行列。
/// GLB モデルを InstancedModelBatch で描画する際の `root_transforms` に使用する。
///
/// # 引数
/// - `actors` : ルートアクターのスライス
/// - `world`  : ECS ワールド（コンポーネント参照に使用）
/// - `wl`     : 対象の世界線番号
pub fn collect_camera_actor_matrices(
    actors: &[Actor],
    world:  &World,
    wl:     u32,
) -> Vec<(usize, [[f32; 4]; 4])> {
    let mut result  = Vec::new();
    let mut counter = 0usize;
    collect_camera_matrices_recursive(actors, world, wl, &mut counter, &mut result);
    result
}

/// アクターツリーを走査し、CameraComponent を持つ全アクターのカメラアイコンを
/// GizmoBatch に追加して GPU バッチを返す。
///
/// # 引数
/// - `actors` : ルートアクターのスライス
/// - `world`  : ECS ワールド（コンポーネント参照に使用）
/// - `wl`     : 対象の世界線番号
/// - `device` : GPU デバイス（バッファ構築用）
///
/// バッチが空の場合は `None` を返す。
pub fn build_camera_icon_batch(
    actors: &[Actor],
    world:  &World,
    wl:     u32,
    device: &wgpu::Device,
) -> Option<crate::engine::methods::drawer::GpuGizmoBatch> {
    let mut batch = GizmoBatch::new();
    collect_camera_icons_recursive(actors, world, wl, &mut batch);
    if batch.is_empty() { None } else { Some(batch.build(device)) }
}

/// フラスタム可視化と CameraUniform 構築に必要なカメラパラメータをまとめた型。
pub struct SelectedCameraData {
    /// カメラアクターのワールドトランスフォーム。
    pub transform: ActorTransform,
    /// カメラコンポーネントのパラメータ。
    pub fov_y_deg: f32,
    pub near:      f32,
    pub far:       f32,
    pub clear_color: [f32; 4],
    /// ゲームのターゲット解像度（横）。フラスタムおよびプレビューのアスペクト比算出に使用。
    pub target_width:  u32,
    /// ゲームのターゲット解像度（縦）。フラスタムおよびプレビューのアスペクト比算出に使用。
    pub target_height: u32,
    /// 投影方式（透視 / 正射）。
    pub projection:   CameraProjection,
    /// 正射投影時の縦方向の描画範囲（ワールド単位・全高）。
    pub ortho_height: f32,
}

impl SelectedCameraData {
    /// ターゲット解像度からアスペクト比（width / height）を返す。
    pub fn target_aspect(&self) -> f32 {
        self.target_width.max(1) as f32 / self.target_height.max(1) as f32
    }

    /// 投影方式に応じた射影行列を返す（透視 or 正射）。
    ///
    /// フラスタムカリング・プレビュー・カメラ描画で共通利用し、投影の一貫性を保つ。
    /// 正射は縦 `ortho_height`・横 `ortho_height * aspect` の範囲を中央基準（Y-up）で写す。
    pub fn proj_matrix(&self, aspect: f32) -> Mat4x4<f32> {
        let near = self.near.max(0.01);
        let far  = self.far.max(near + 0.1);
        match self.projection {
            CameraProjection::Perspective => {
                Mat4x4::perspective_lh(self.fov_y_deg.to_radians(), aspect, near, far)
            }
            CameraProjection::Orthographic => {
                let half_h = (self.ortho_height.max(0.01)) * 0.5;
                let half_w = half_h * aspect;
                Mat4x4::orthographic_lh(-half_w, half_w, -half_h, half_h, near, far)
            }
        }
    }
}

/// 選択中のアクターが CameraComponent を持つ場合に SelectedCameraData を返す。
///
/// # 引数
/// - `actors`          : ルートアクターのスライス
/// - `world`           : ECS ワールド
/// - `wl`              : 対象の世界線番号
/// - `selected_dfs_id` : 選択中アクターの DFS 番号（None の場合は None を返す）
pub fn get_selected_camera_data(
    actors:          &[Actor],
    world:           &World,
    wl:              u32,
    selected_dfs_id: Option<usize>,
) -> Option<SelectedCameraData> {
    let dfs_id = selected_dfs_id? as u32;
    let mut counter = 0u32;
    find_camera_actor(actors, world, wl, dfs_id, &mut counter)
}

/// 選択中カメラアクターのフラスタムライン GPU バッチを構築する。
///
/// フラスタム可視化は選択中カメラのみ。遠クリップは MAX_FRUSTUM_FAR でクランプ。
/// アスペクト比は cam_data.target_aspect() から自動導出する（エディタビューポートに依存しない）。
pub fn build_camera_frustum_batch(
    cam_data: &SelectedCameraData,
    device:   &wgpu::Device,
) -> Option<crate::engine::methods::drawer::GpuLineBatch> {
    let mut lb = LineBatch::new();
    add_camera_frustum(&mut lb, cam_data);
    if lb.is_empty() { None } else { Some(lb.build(device)) }
}

/// 選択中カメラの視錐台プレーンを計算する（フラスタムカリング用）。
///
/// `build_camera_uniform` の view_proj はトランスポーズされた GPU 向け形式であるため、
/// `extract_frustum_planes` に渡す行優先 VP 行列はここで別途計算する。
/// アスペクト比は cam_data.target_aspect() から自動導出する。
pub fn compute_frustum_planes(cam_data: &SelectedCameraData) -> [[f32; 4]; 6] {
    let aspect = cam_data.target_aspect();
    let tf = &cam_data.transform;
    let [px, py, pz] = tf.position;
    let [fx, fy, fz] = tf.forward();
    let [ux, uy, uz] = tf.up();
    let pos    = Vector3::new(px, py, pz);
    let target = pos + Vector3::new(fx, fy, fz);
    let up_vec = Vector3::new(ux, uy, uz);
    let view   = Mat4x4::look_at_lh(pos, target, up_vec);
    let proj   = cam_data.proj_matrix(aspect);
    let vp = proj * view;
    extract_frustum_planes(&vp.data)
}

/// 選択中カメラの CameraUniform を構築する（カメラプレビューレンダー用）。
///
/// - `res`: ビューポート解像度 [width, height]（ギズモ太線計算に使用）
pub fn build_camera_uniform(
    cam_data: &SelectedCameraData,
    aspect:   f32,
    res:      [f32; 2],
) -> CameraUniform {
    let tf = &cam_data.transform;
    let [px, py, pz] = tf.position;
    let [fx, fy, fz] = tf.forward();
    let [ux, uy, uz] = tf.up();

    let pos    = Vector3::new(px, py, pz);
    let target = pos + Vector3::new(fx, fy, fz);
    let up_vec = Vector3::new(ux, uy, uz);

    let view = Mat4x4::look_at_lh(pos, target, up_vec);
    let proj = cam_data.proj_matrix(aspect);
    let view_proj = proj * view;

    CameraUniform {
        view_proj:  view_proj.transpose().data,
        view:       view.transpose().data,
        position:   [px, py, pz],
        _pad:       0.0,
        resolution: res,
        _pad2:      [0.0; 2],
    }
}

// ============================================================
//  内部再帰実装
// ============================================================

/// アクターツリーを DFS 走査してカメラアクターの (DFS ID, アイコン行列) を収集する。
///
/// DFS カウンタはすべての world_line 一致アクターを数えるため、
/// CameraComponent 非保持アクターもカウントのみ行う。
fn collect_camera_matrices_recursive(
    actors:  &[Actor],
    world:   &World,
    wl:      u32,
    counter: &mut usize,
    result:  &mut Vec<(usize, [[f32; 4]; 4])>,
) {
    for actor in actors {
        if actor.world_line != wl { continue; }
        let dfs_id = *counter;
        *counter += 1;

        // CameraComponent を持つアクターのみアイコン行列を追加する
        if actor.has_kind(ComponentKind::Camera) {
            if let Some(cam_entity) = actor.first_slot_entity_of_kind(ComponentKind::Camera) {
                if world.get::<CameraComponent>(cam_entity).is_some() {
                    if let Some(tf) = world.get::<ActorTransform>(actor.entity) {
                        result.push((dfs_id, icon_matrix(tf)));
                    }
                }
            }
        }
        // 子アクターを再帰処理
        collect_camera_matrices_recursive(actor.children(), world, wl, counter, result);
    }
}

/// アクターツリーを DFS 走査してカメラアイコンを収集する。
fn collect_camera_icons_recursive(
    actors: &[Actor],
    world:  &World,
    wl:     u32,
    batch:  &mut GizmoBatch,
) {
    for actor in actors {
        if actor.world_line != wl { continue; }
        // CameraComponent の有無を確認する
        if actor.has_kind(ComponentKind::Camera) {
            if let Some(cam_entity) = actor.first_slot_entity_of_kind(ComponentKind::Camera) {
                if world.get::<CameraComponent>(cam_entity).is_some() {
                    // アクターの Transform を取得してアイコンを生成する
                    if let Some(tf) = world.get::<ActorTransform>(actor.entity) {
                        add_camera_icon(batch, tf);
                    }
                }
            }
        }
        // 子アクターを再帰処理
        collect_camera_icons_recursive(actor.children(), world, wl, batch);
    }
}

/// DFS 番号でアクターを特定し、CameraComponent を持つ場合の情報を返す。
fn find_camera_actor(
    actors:  &[Actor],
    world:   &World,
    wl:      u32,
    dfs_id:  u32,
    counter: &mut u32,
) -> Option<SelectedCameraData> {
    for actor in actors {
        if actor.world_line != wl {
            continue;
        }
        let current_id = *counter;
        *counter += 1;

        if current_id == dfs_id {
            // 対象アクターが見つかった。CameraComponent の有無を確認する
            if let Some(cam_entity) = actor.first_slot_entity_of_kind(ComponentKind::Camera) {
                if let Some(cam) = world.get::<CameraComponent>(cam_entity) {
                    if let Some(tf) = world.get::<ActorTransform>(actor.entity) {
                        return Some(SelectedCameraData {
                            transform:    tf.clone(),
                            fov_y_deg:    cam.fov_y_deg,
                            near:         cam.near,
                            far:          cam.far,
                            clear_color:  cam.clear_color,
                            target_width:  cam.target_width,
                            target_height: cam.target_height,
                            projection:   cam.projection,
                            ortho_height: cam.ortho_height,
                        });
                    }
                }
            }
            return None;
        }

        // 子アクターを再帰処理
        if let Some(data) = find_camera_actor(actor.children(), world, wl, dfs_id, counter) {
            return Some(data);
        }
    }
    None
}

/// 選択中カメラのフラスタムラインを LineBatch に追加する。
/// アスペクト比は cam.target_aspect() から自動導出する。
fn add_camera_frustum(lb: &mut LineBatch, cam: &SelectedCameraData) {
    let aspect  = cam.target_aspect();
    let tf      = &cam.transform;
    let pos     = tf.position;
    let fwd     = normalize3(tf.forward());
    let up_raw  = normalize3(tf.up());

    // right ベクトル（外積: fwd × up）
    let right = normalize3([
        fwd[1] * up_raw[2] - fwd[2] * up_raw[1],
        fwd[2] * up_raw[0] - fwd[0] * up_raw[2],
        fwd[0] * up_raw[1] - fwd[1] * up_raw[0],
    ]);

    // up を right と fwd から再計算（直交化）
    let up = normalize3([
        right[1] * fwd[2] - right[2] * fwd[1],
        right[2] * fwd[0] - right[0] * fwd[2],
        right[0] * fwd[1] - right[1] * fwd[0],
    ]);

    let near     = cam.near.max(0.01);
    let far      = cam.far.min(MAX_FRUSTUM_FAR).max(near + 0.1);

    // near / far 平面の半サイズ。
    // 透視: 距離に比例して広がる（角錐）。正射: 距離に依らず一定（直方体）。
    let (nh, nw, fh, fw) = match cam.projection {
        CameraProjection::Perspective => {
            let half_fov = (cam.fov_y_deg * 0.5).to_radians().tan();
            let nh = half_fov * near;
            let fh = half_fov * far;
            (nh, nh * aspect, fh, fh * aspect)
        }
        CameraProjection::Orthographic => {
            let h = cam.ortho_height.max(0.01) * 0.5;
            (h, h * aspect, h, h * aspect)
        }
    };

    let nc = add3(pos, scale3(fwd, near));
    let fc = add3(pos, scale3(fwd, far));

    // フラスタム 8 コーナーを計算する
    // near: TL=0, TR=1, BR=2, BL=3
    // far:  TL=4, TR=5, BR=6, BL=7
    let corners = [
        add3(add3(nc, scale3(up,  nh)), scale3(right, -nw)),  // 0 near TL
        add3(add3(nc, scale3(up,  nh)), scale3(right,  nw)),  // 1 near TR
        add3(add3(nc, scale3(up, -nh)), scale3(right,  nw)),  // 2 near BR
        add3(add3(nc, scale3(up, -nh)), scale3(right, -nw)),  // 3 near BL
        add3(add3(fc, scale3(up,  fh)), scale3(right, -fw)),  // 4 far TL
        add3(add3(fc, scale3(up,  fh)), scale3(right,  fw)),  // 5 far TR
        add3(add3(fc, scale3(up, -fh)), scale3(right,  fw)),  // 6 far BR
        add3(add3(fc, scale3(up, -fh)), scale3(right, -fw)),  // 7 far BL
    ];

    let c = FRUSTUM_COLOR;

    // near 平面の矩形（4 辺）
    lb.add_line(corners[0], corners[1], c);
    lb.add_line(corners[1], corners[2], c);
    lb.add_line(corners[2], corners[3], c);
    lb.add_line(corners[3], corners[0], c);

    // far 平面の矩形（4 辺）
    lb.add_line(corners[4], corners[5], c);
    lb.add_line(corners[5], corners[6], c);
    lb.add_line(corners[6], corners[7], c);
    lb.add_line(corners[7], corners[4], c);

    // near-far を繋ぐ 4 辺（錐台の稜線）
    lb.add_line(corners[0], corners[4], c);
    lb.add_line(corners[1], corners[5], c);
    lb.add_line(corners[2], corners[6], c);
    lb.add_line(corners[3], corners[7], c);
}
