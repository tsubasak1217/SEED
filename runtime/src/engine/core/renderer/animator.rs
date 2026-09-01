// ============================================================
//  animator.rs — CPU サイド アニメーション評価
//
//  sample_joint_matrices():
//    1. アニメーションチャンネルから各ノードの TRS を補間
//    2. シーングラフを走査してワールド行列を計算
//    3. ジョイント行列 = world_mat * inverse_bind_matrix を返す
// ============================================================

use crate::engine::core::loader::model::{AnimationOutputs, Interpolation, Model};

/// GPU バッファの stride（インスタンスごとに MAX_JOINTS 分を確保）
pub const MAX_JOINTS: usize = 128;

/// アニメーション時刻 `time`（秒）でスキン `skin_idx` のジョイント行列を計算する。
///
/// 戻り値: `MAX_JOINTS` 個の `[[f32;4];4]`（不足分は単位行列でパディング）。
pub fn sample_joint_matrices(
    model: &Model,
    anim_idx: usize,
    skin_idx: usize,
    time: f32,
) -> Vec<[[f32; 4]; 4]> {
    let id = identity();

    if anim_idx >= model.animations.len() || skin_idx >= model.skins.len() {
        return vec![id; MAX_JOINTS];
    }

    let skin = &model.skins[skin_idx];

    // ── ①〜③ ノードのワールド行列（モデル空間）を計算 ──────────
    let world_mats = compute_node_world_matrices(model, anim_idx, time);

    // ── ④ ジョイント行列 = world[joint.node] * ibm ──────────
    let mut result = vec![id; MAX_JOINTS];
    for (ji, joint) in skin.joints.iter().enumerate() {
        if ji >= MAX_JOINTS {
            break;
        }
        result[ji] = mat4_mul(&world_mats[joint.node_index], &joint.inverse_bind_matrix);
    }
    result
}

/// アニメーション時刻 `time`（秒）で全ノードのワールド行列（モデル空間・行優先）を計算する。
///
/// `sample_joint_matrices` の①〜③（TRS 補間→ローカル行列→シーングラフ走査）を切り出した
/// 純関数。ジョイントアタッチ（ソケット）機構が「ジョイントノードのワールド行列」を得るために
/// 使う（インバースバインド行列は掛けない＝スキニング行列ではなくノードの姿勢そのもの）。
///
/// - `anim_idx` が範囲外（例: `usize::MAX`）または `model.animations` が空の場合は、
///   アニメーションを適用せずバインドポーズ（各ノードの `local_matrix`）で解決する
///   （＝「静止＝t0」相当の姿勢）。
/// - 戻り値: `model.nodes.len()` 個の 4×4 行列（インデックスは `model.nodes` と一致）。
///
/// 出力はモデル空間（モデルルートを単位行列とした階層合成）。ワールド空間へ変換するには
/// 呼び出し側でモデルアクターのワールド行列を左から掛ける。
pub fn compute_node_world_matrices(
    model: &Model,
    anim_idx: usize,
    time: f32,
) -> Vec<[[f32; 4]; 4]> {
    let n_nodes = model.nodes.len();

    // ── ① アニメーション補間後の TRS を収集（有効な anim_idx のときのみ）──
    let node_trs = sample_node_trs(model, anim_idx, time);

    // ── ② ノードローカル行列（TRS 無し＝バインドポーズの local_matrix）──
    let local_mats: Vec<[[f32; 4]; 4]> = (0..n_nodes)
        .map(|i| {
            node_trs[i]
                .as_ref()
                .map(trs_to_matrix)
                .unwrap_or(model.nodes[i].local_matrix)
        })
        .collect();

    // ── ③ ワールド行列（シーングラフ走査。ルートは単位行列）──
    world_from_locals(model, &local_mats)
}

/// クロスフェード（2 クリップ混合）版のノードワールド行列。
///
/// GPU スキニング（skin_compute.wgsl）と**同じ規則**でポーズを混ぜる:
///   - 各ノードの TRS を A・B それぞれで評価する（未アニメのノードはバインドポーズ）
///   - 平行移動/スケールは線形補間、回転は符号合わせ付きの正規化線形補間
///   - `weight = 1` なら B のみ（＝`compute_node_world_matrices` と完全に同じ結果）
///
/// ジョイントアタッチ（ソケット）が、フェード中も描画と同じ姿勢へ追従するために使う。
pub fn compute_node_world_matrices_blend(
    model: &Model,
    anim_a: usize,
    time_a: f32,
    anim_b: usize,
    time_b: f32,
    weight: f32,
) -> Vec<[[f32; 4]; 4]> {
    let w = weight.clamp(0.0, 1.0);
    // フェードしていない（B のみ）ときは単一クリップ経路へ委ねる（結果はビット単位で同一）。
    if w >= 1.0 {
        return compute_node_world_matrices(model, anim_b, time_b);
    }

    let n_nodes = model.nodes.len();
    let trs_a = sample_node_trs(model, anim_a, time_a);
    let trs_b = sample_node_trs(model, anim_b, time_b);

    let local_mats: Vec<[[f32; 4]; 4]> = (0..n_nodes)
        .map(|i| match (&trs_a[i], &trs_b[i]) {
            // どちらのクリップも触らないノードはバインドポーズのまま
            (None, None) => model.nodes[i].local_matrix,
            _ => {
                // 片側だけがアニメするノードは、もう片方をバインドポーズとして混ぜる
                //（そうしないと「A だけが動かす腕」がフェード後も戻らない）。
                let bind = bind_trs(model, i);
                let a = trs_a[i].as_ref().unwrap_or(&bind);
                let b = trs_b[i].as_ref().unwrap_or(&bind);
                trs_to_matrix(&blend_trs(a, b, w))
            }
        })
        .collect();

    world_from_locals(model, &local_mats)
}

/// ノードローカル行列列からワールド行列列（モデル空間）を求める。
fn world_from_locals(model: &Model, local_mats: &[[[f32; 4]; 4]]) -> Vec<[[f32; 4]; 4]> {
    let id = identity();
    let mut world_mats = vec![id; model.nodes.len()];
    for &root in &model.root_nodes {
        compute_world(root, &id, local_mats, &model.nodes, &mut world_mats);
    }
    world_mats
}

/// アニメ `anim_idx` を時刻 `time` で評価し、ノードごとの TRS オーバーライドを返す。
/// `None` の要素は「このアニメが触らないノード」＝バインドポーズを意味する。
/// `anim_idx` が範囲外（例: `usize::MAX`）なら全要素 `None`（＝バインドポーズ）。
fn sample_node_trs(model: &Model, anim_idx: usize, time: f32) -> Vec<Option<Trs>> {
    let n_nodes = model.nodes.len();
    let mut node_trs: Vec<Option<Trs>> = (0..n_nodes).map(|_| None).collect();
    if anim_idx >= model.animations.len() {
        return node_trs;
    }
    let anim = &model.animations[anim_idx];
    for ch in &anim.channels {
        let ni = ch.target_node_index;
        if ni >= n_nodes {
            continue;
        }
        // 初期値はバインドポーズ（このチャンネルが触らない成分を単位値で潰さないため）
        let bind = bind_trs(model, ni);
        let trs = node_trs[ni].get_or_insert(bind);
        let s = &ch.sampler;
        let t = time.clamp(
            s.timestamps.first().copied().unwrap_or(0.0),
            s.timestamps.last().copied().unwrap_or(0.0),
        );
        match &s.outputs {
            AnimationOutputs::Translations(v) => {
                trs.translation = sample_vec3(s.interpolation, &s.timestamps, v, t);
            }
            AnimationOutputs::Rotations(v) => {
                trs.rotation = sample_quat(s.interpolation, &s.timestamps, v, t);
            }
            AnimationOutputs::Scales(v) => {
                trs.scale = sample_vec3(s.interpolation, &s.timestamps, v, t);
            }
            _ => {}
        }
    }
    node_trs
}

/// ノードのバインドポーズ TRS（glTF の node.translation/rotation/scale）。
fn bind_trs(model: &Model, node_idx: usize) -> Trs {
    let n = &model.nodes[node_idx];
    Trs {
        translation: n.translation,
        rotation: n.rotation,
        scale: n.scale,
    }
}

/// 2 つの TRS をブレンドする純関数（GPU 側 skin_compute.wgsl と同一規則）。
///
/// - 平行移動 / スケール: 線形補間
/// - 回転: 符号合わせ（内積が負なら b を反転）付きの正規化線形補間＝最短経路
/// - `w = 0` で a、`w = 1` で b と厳密に一致する
pub fn blend_trs(a: &Trs, b: &Trs, w: f32) -> Trs {
    Trs {
        translation: lerp3(a.translation, b.translation, w),
        rotation: nlerp(a.rotation, b.rotation, w),
        scale: lerp3(a.scale, b.scale, w),
    }
}

fn compute_world(
    node_idx: usize,
    parent_mat: &[[f32; 4]; 4],
    local_mats: &[[[f32; 4]; 4]],
    nodes: &[crate::engine::core::loader::model::ModelNode],
    world_mats: &mut Vec<[[f32; 4]; 4]>,
) {
    let world = mat4_mul(parent_mat, &local_mats[node_idx]);
    world_mats[node_idx] = world;
    for &child in &nodes[node_idx].children {
        compute_world(child, &world, local_mats, nodes, world_mats);
    }
}

// ============================================================
//  TRS 補間
// ============================================================

/// アニメーション補間の中間表現（平行移動・回転・スケール）。
/// チャンネルごとの補間結果を集約し、`trs_to_matrix` でローカル行列へ変換するために使う。
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct Trs {
    pub translation: [f32; 3],
    pub rotation: [f32; 4], // [x, y, z, w]
    pub scale: [f32; 3],
}

impl Trs {
    /// 恒等 TRS（平行移動 0・無回転・等倍）。
    pub fn identity() -> Self {
        Self {
            translation: [0.0, 0.0, 0.0],
            rotation: [0.0, 0.0, 0.0, 1.0],
            scale: [1.0, 1.0, 1.0],
        }
    }
}

fn sample_vec3(interp: Interpolation, timestamps: &[f32], values: &[[f32; 3]], t: f32) -> [f32; 3] {
    if values.is_empty() {
        return [0.0; 3];
    }
    if values.len() == 1 {
        return values[0];
    }
    let (i0, i1, alpha) = find_interval(timestamps, t);
    match interp {
        Interpolation::Step => values[i0],
        Interpolation::Linear => lerp3(values[i0], values[i1], alpha),
        Interpolation::CubicSpline => {
            // 出力レイアウト: [in_tangent, value, out_tangent] の三つ組
            let n = timestamps.len();
            if values.len() == n * 3 {
                lerp3(values[i0 * 3 + 1], values[i1 * 3 + 1], alpha)
            } else {
                lerp3(values[i0], values[i1], alpha)
            }
        }
    }
}

fn sample_quat(interp: Interpolation, timestamps: &[f32], values: &[[f32; 4]], t: f32) -> [f32; 4] {
    if values.is_empty() {
        return [0.0, 0.0, 0.0, 1.0];
    }
    if values.len() == 1 {
        return values[0];
    }
    let (i0, i1, alpha) = find_interval(timestamps, t);
    match interp {
        Interpolation::Step => values[i0],
        Interpolation::Linear => nlerp(values[i0], values[i1], alpha),
        Interpolation::CubicSpline => {
            let n = timestamps.len();
            if values.len() == n * 3 {
                nlerp(values[i0 * 3 + 1], values[i1 * 3 + 1], alpha)
            } else {
                nlerp(values[i0], values[i1], alpha)
            }
        }
    }
}

/// タイムスタンプ列から補間区間 (i0, i1, alpha) を返す。
fn find_interval(timestamps: &[f32], t: f32) -> (usize, usize, f32) {
    let n = timestamps.len();
    if n == 0 {
        return (0, 0, 0.0);
    }
    if t <= timestamps[0] {
        return (0, 0, 0.0);
    }
    if t >= timestamps[n - 1] {
        return (n - 1, n - 1, 0.0);
    }
    let i1 = timestamps.partition_point(|&ts| ts <= t).min(n - 1);
    let i0 = i1.saturating_sub(1);
    let dt = timestamps[i1] - timestamps[i0];
    let alpha = if dt > 1e-6 {
        (t - timestamps[i0]) / dt
    } else {
        0.0
    };
    (i0, i1, alpha)
}

#[inline]
fn lerp3(a: [f32; 3], b: [f32; 3], t: f32) -> [f32; 3] {
    [
        a[0] + (b[0] - a[0]) * t,
        a[1] + (b[1] - a[1]) * t,
        a[2] + (b[2] - a[2]) * t,
    ]
}

/// 正規化線形補間（クォータニオン用）
fn nlerp(a: [f32; 4], b: [f32; 4], t: f32) -> [f32; 4] {
    let dot = a[0] * b[0] + a[1] * b[1] + a[2] * b[2] + a[3] * b[3];
    let b = if dot < 0.0 {
        [-b[0], -b[1], -b[2], -b[3]]
    } else {
        b
    };
    let r = [
        a[0] + (b[0] - a[0]) * t,
        a[1] + (b[1] - a[1]) * t,
        a[2] + (b[2] - a[2]) * t,
        a[3] + (b[3] - a[3]) * t,
    ];
    let len = (r[0] * r[0] + r[1] * r[1] + r[2] * r[2] + r[3] * r[3]).sqrt();
    if len < 1e-8 {
        return [0.0, 0.0, 0.0, 1.0];
    }
    [r[0] / len, r[1] / len, r[2] / len, r[3] / len]
}

// ============================================================
//  TRS → 行優先 4×4 行列
// ============================================================

/// TRS から行優先 4×4 変換行列を生成する。
///
/// 行優先規約: `M * v` で列ベクトル `v` を変換。
fn trs_to_matrix(trs: &Trs) -> [[f32; 4]; 4] {
    let [tx, ty, tz] = trs.translation;
    let [sx, sy, sz] = trs.scale;
    let [qx, qy, qz, qw] = trs.rotation;

    let x2 = qx + qx;
    let y2 = qy + qy;
    let z2 = qz + qz;
    let xx = qx * x2;
    let yy = qy * y2;
    let zz = qz * z2;
    let xy = qx * y2;
    let xz = qx * z2;
    let yz = qy * z2;
    let wx = qw * x2;
    let wy = qw * y2;
    let wz = qw * z2;

    [
        [(1.0 - yy - zz) * sx, (xy - wz) * sy, (xz + wy) * sz, tx],
        [(xy + wz) * sx, (1.0 - xx - zz) * sy, (yz - wx) * sz, ty],
        [(xz - wy) * sx, (yz + wx) * sy, (1.0 - xx - yy) * sz, tz],
        [0.0, 0.0, 0.0, 1.0],
    ]
}

// ============================================================
//  行列演算
// ============================================================

pub fn identity() -> [[f32; 4]; 4] {
    [
        [1.0, 0.0, 0.0, 0.0],
        [0.0, 1.0, 0.0, 0.0],
        [0.0, 0.0, 1.0, 0.0],
        [0.0, 0.0, 0.0, 1.0],
    ]
}

pub fn mat4_mul(a: &[[f32; 4]; 4], b: &[[f32; 4]; 4]) -> [[f32; 4]; 4] {
    let mut out = [[0.0f32; 4]; 4];
    for i in 0..4 {
        for j in 0..4 {
            for k in 0..4 {
                out[i][j] += a[i][k] * b[k][j];
            }
        }
    }
    out
}

// ============================================================
//  テスト
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;
    use crate::engine::core::loader::model::{Model, ModelNode};

    /// 平行移動のみの行優先行列を作る（[row][3] に平行移動）。
    fn translate_mat(t: [f32; 3]) -> [[f32; 4]; 4] {
        let mut m = identity();
        m[0][3] = t[0];
        m[1][3] = t[1];
        m[2][3] = t[2];
        m
    }

    /// 平行移動のみのノードを作る。
    fn node(name: &str, t: [f32; 3], children: Vec<usize>) -> ModelNode {
        ModelNode {
            name: name.to_string(),
            local_matrix: translate_mat(t),
            translation: t,
            rotation: [0.0, 0.0, 0.0, 1.0],
            scale: [1.0, 1.0, 1.0],
            mesh_index: None,
            skin_index: None,
            children,
            parent: None,
        }
    }

    /// アニメ無し（バインドポーズ）で親子ノードのワールド行列が階層合成されることを検証する。
    ///
    /// 親を (1,0,0)、子を親相対 (0,2,0) に置くと、子のワールド平行移動は (1,2,0) になる。
    /// ジョイントアタッチはこの「ノードのワールド行列」を用いてソケット位置を決めるため、
    /// 階層合成が正しいことが追従の前提となる。
    #[test]
    fn node_world_matrices_compose_hierarchy_bind_pose() {
        let model = Model {
            name: "t".into(),
            nodes: vec![
                node("root", [1.0, 0.0, 0.0], vec![1]),
                node("child", [0.0, 2.0, 0.0], vec![]),
            ],
            root_nodes: vec![0],
            meshes: vec![],
            materials: vec![],
            textures: vec![],
            animations: vec![],
            skins: vec![],
        };

        // anim_idx を無効値にするとアニメ非適用（バインドポーズ＝各ノードの local_matrix）で解決される。
        let world = compute_node_world_matrices(&model, usize::MAX, 0.0);
        assert_eq!(world.len(), 2);

        // 親のワールド平行移動 = (1,0,0)
        assert!((world[0][0][3] - 1.0).abs() < 1e-5);
        assert!((world[0][1][3] - 0.0).abs() < 1e-5);
        // 子のワールド平行移動 = 親 (1,0,0) + 子相対 (0,2,0) = (1,2,0)
        assert!(
            (world[1][0][3] - 1.0).abs() < 1e-5,
            "child x = {}",
            world[1][0][3]
        );
        assert!(
            (world[1][1][3] - 2.0).abs() < 1e-5,
            "child y = {}",
            world[1][1][3]
        );
        assert!((world[1][2][3] - 0.0).abs() < 1e-5);
    }

    // ── TRS ブレンド（クロスフェード）の純関数テスト ────────────

    /// weight の端点（0 / 1）でブレンド結果が入力そのものと一致する。
    /// ここが崩れると「フェード開始直後」「完了直後」に姿勢が飛ぶ。
    #[test]
    fn blend_trs_endpoints_match_inputs() {
        let a = Trs {
            translation: [1.0, 2.0, 3.0],
            rotation: [0.0, 0.0, 0.0, 1.0],
            scale: [1.0, 1.0, 1.0],
        };
        // 90° 回転（Z 軸）
        let s = std::f32::consts::FRAC_1_SQRT_2;
        let b = Trs {
            translation: [-1.0, 0.0, 5.0],
            rotation: [0.0, 0.0, s, s],
            scale: [2.0, 2.0, 2.0],
        };

        let at0 = blend_trs(&a, &b, 0.0);
        assert_eq!(at0.translation, a.translation);
        assert_eq!(at0.scale, a.scale);
        for i in 0..4 {
            assert!((at0.rotation[i] - a.rotation[i]).abs() < 1e-6);
        }

        let at1 = blend_trs(&a, &b, 1.0);
        assert_eq!(at1.translation, b.translation);
        assert_eq!(at1.scale, b.scale);
        for i in 0..4 {
            assert!((at1.rotation[i] - b.rotation[i]).abs() < 1e-6);
        }

        // 中点は各成分の中間（回転は正規化されている）
        let mid = blend_trs(&a, &b, 0.5);
        assert!((mid.translation[0] - 0.0).abs() < 1e-6);
        assert!((mid.scale[0] - 1.5).abs() < 1e-6);
        let len: f32 = mid.rotation.iter().map(|v| v * v).sum::<f32>().sqrt();
        assert!((len - 1.0).abs() < 1e-5, "回転は常に正規化される: {len}");
    }

    /// クォータニオンの符号合わせ（最短経路補間）。
    ///
    /// q と -q は同じ姿勢なので、内積が負の組み合わせでは b を反転してから補間しないと
    /// 「遠回り」して 1 フレームで回転が暴れる。
    #[test]
    fn blend_trs_takes_shortest_rotation_path() {
        let ident = Trs::identity();
        let s = std::f32::consts::FRAC_1_SQRT_2;
        let q = [0.0, 0.0, s, s]; // Z 90°
        let a = Trs { rotation: q, ..ident };
        // 同じ姿勢を表す符号反転クォータニオン
        let b = Trs { rotation: [-q[0], -q[1], -q[2], -q[3]], ..ident };

        // 符号合わせが効いていれば、同一姿勢どうしの補間は姿勢を変えない
        let mid = blend_trs(&a, &b, 0.5);
        for i in 0..4 {
            assert!(
                (mid.rotation[i] - q[i]).abs() < 1e-5,
                "符号合わせが無いと中点で姿勢が壊れる: {:?}",
                mid.rotation
            );
        }
    }

    /// weight = 1 のブレンド解決は単一クリップ解決とビット単位で一致する
    /// （＝クロスフェード導入で従来の見た目が 1 ビットも変わらない）。
    #[test]
    fn blend_with_full_weight_matches_single_clip_path() {
        let model = Model {
            name: "t".into(),
            nodes: vec![
                node("root", [1.0, 0.0, 0.0], vec![1]),
                node("child", [0.0, 2.0, 0.0], vec![]),
            ],
            root_nodes: vec![0],
            meshes: vec![],
            materials: vec![],
            textures: vec![],
            animations: vec![],
            skins: vec![],
        };
        let single = compute_node_world_matrices(&model, usize::MAX, 0.0);
        let blended =
            compute_node_world_matrices_blend(&model, usize::MAX, 0.0, usize::MAX, 0.0, 1.0);
        assert_eq!(single, blended);
    }
}
