// ============================================================
//  tests_skin_playback.rs — GPU スキニング再生経路の CPU 写しによる回帰テスト
//
//  【なぜ CPU 写しなのか】
//  「アニメが一覧には出るのに再生されない」系の不具合は、
//    ① モデル → pack_animations（全アニメ連結・オフセット表）
//    ② SkinAnimPose → GpuAnimSample（アニメ index の解決・時刻クランプ）
//    ③ skin_compute.wgsl（チャンネル適用 → BFS ワールド行列 → ジョイント行列）
//  のどこで落ちても同じ「バインドポーズのまま」に見える。GPU 実機テスト
//  （skin_system.rs の `gpu_multi_anim_...`）は実行環境に GPU を要求するため、
//  ここでは ③ を CPU で 1:1 に写し、GPU 非依存で ①〜③ を通しで検証する。
//
//  【この写しが守る不変条件】skin_compute.wgsl の `eval_pose` / `cs_main` と
//  同じ順序・同じ規約（列優先行列・BFS 順・joint = world[node] * ibm）で計算する。
//  シェーダ側の式を変えたらこちらも必ず追随させること。
// ============================================================

use crate::engine::core::loader::model::{
    Animation, AnimationChannel, AnimationOutputs, AnimationSampler, Interpolation, Model,
    ModelNode, Skin, SkinJoint,
};
use crate::engine::core::renderer::skin_system::{
    animation_is_motionless, compute_bfs_order, pack_animations, PackedAnimations, SkinAnimPose,
};

// ============================================================
//  skin_compute.wgsl の CPU 写し
// ============================================================

/// 4x4 列優先行列（WGSL の `mat4x4<f32>` と同じ規約: `m[col][row]`）。
type Mat4 = [[f32; 4]; 4];

/// 列優先の行列積（WGSL の `a * b` と同じ）。
fn mul(a: &Mat4, b: &Mat4) -> Mat4 {
    let mut o = [[0.0f32; 4]; 4];
    for c in 0..4 {
        for r in 0..4 {
            o[c][r] = (0..4).map(|k| a[k][r] * b[c][k]).sum();
        }
    }
    o
}

/// TRS → 列優先行列（skin_compute.wgsl の `trs_to_mat` と同一式）。
fn trs_to_mat(t: [f32; 3], r: [f32; 4], s: [f32; 3]) -> Mat4 {
    let (x, y, z, w) = (r[0], r[1], r[2], r[3]);
    let (x2, y2, z2) = (x + x, y + y, z + z);
    let (xx, yy, zz) = (x * x2, y * y2, z * z2);
    let (xy, xz, yz) = (x * y2, x * z2, y * z2);
    let (wx, wy, wz) = (w * x2, w * y2, w * z2);
    [
        [(1.0 - yy - zz) * s[0], (xy + wz) * s[0], (xz - wy) * s[0], 0.0],
        [(xy - wz) * s[1], (1.0 - xx - zz) * s[1], (yz + wx) * s[1], 0.0],
        [(xz + wy) * s[2], (yz - wx) * s[2], (1.0 - xx - yy) * s[2], 0.0],
        [t[0], t[1], t[2], 1.0],
    ]
}

/// タイムスタンプ列から補間区間を求める（`find_interval` と同一）。
fn find_interval(ts: &[f32], t: f32) -> (usize, usize) {
    if ts.is_empty() {
        return (0, 0);
    }
    let last = ts.len() - 1;
    if t <= ts[0] {
        return (0, 0);
    }
    if t >= ts[last] {
        return (last, last);
    }
    let (mut lo, mut hi) = (0usize, last);
    while hi - lo > 1 {
        let mid = (lo + hi) / 2;
        if ts[mid] <= t {
            lo = mid;
        } else {
            hi = mid;
        }
    }
    (lo, hi)
}

/// 補間係数（`interp_alpha` と同一）。
fn interp_alpha(ts: &[f32], i0: usize, i1: usize, t: f32) -> f32 {
    let dt = ts[i1] - ts[i0];
    if dt < 1e-7 {
        return 0.0;
    }
    ((t - ts[i0]) / dt).clamp(0.0, 1.0)
}

/// 1 アニメ 1 時刻のポーズ（ノードごとの TRS）。`eval_pose` の CPU 写し。
///
/// バインドポーズで初期化し、そのアニメが持つチャンネルだけを上書きする。
fn eval_pose(
    packed: &PackedAnimations,
    anim_idx: usize,
    t: f32,
    bind: &(Vec<[f32; 3]>, Vec<[f32; 4]>, Vec<[f32; 3]>),
) -> (Vec<[f32; 3]>, Vec<[f32; 4]>, Vec<[f32; 3]>) {
    let (mut nt, mut nr, mut ns) = (bind.0.clone(), bind.1.clone(), bind.2.clone());
    let Some(info) = packed.anims.get(anim_idx) else {
        return (nt, nr, ns);
    };
    let begin = info.chan_offset as usize;
    let end = (begin + info.chan_count as usize).min(packed.channels.len());

    for ch in &packed.channels[begin..end] {
        let ts = &packed.timestamps
            [ch.ts_offset as usize..ch.ts_offset as usize + ch.ts_count as usize];
        let (i0, i1) = find_interval(ts, t);
        let a = interp_alpha(ts, i0, i1, t);
        let is_cubic = ch.interp == 2;
        let vi = |i: usize| {
            if is_cubic {
                ch.val_offset as usize + i * 3 + 1
            } else {
                ch.val_offset as usize + i
            }
        };
        let (c0, c1) = (vi(i0), vi(i1));
        let node = ch.target_node as usize;
        match ch.prop_type {
            0 => {
                let v0 = packed.trans_vals[c0];
                let v1 = packed.trans_vals[c1];
                nt[node] = if ch.interp == 1 {
                    [v0[0], v0[1], v0[2]]
                } else {
                    [
                        v0[0] + (v1[0] - v0[0]) * a,
                        v0[1] + (v1[1] - v0[1]) * a,
                        v0[2] + (v1[2] - v0[2]) * a,
                    ]
                };
            }
            1 => {
                let v0 = packed.rot_vals[c0];
                let v1 = packed.rot_vals[c1];
                nr[node] = if ch.interp == 1 { v0 } else { nlerp(v0, v1, a) };
            }
            2 => {
                let v0 = packed.scale_vals[c0];
                let v1 = packed.scale_vals[c1];
                ns[node] = if ch.interp == 1 {
                    [v0[0], v0[1], v0[2]]
                } else {
                    [
                        v0[0] + (v1[0] - v0[0]) * a,
                        v0[1] + (v1[1] - v0[1]) * a,
                        v0[2] + (v1[2] - v0[2]) * a,
                    ]
                };
            }
            _ => {}
        }
    }
    (nt, nr, ns)
}

/// 符号合わせ付き正規化線形補間（`nlerp` と同一）。
fn nlerp(a: [f32; 4], b: [f32; 4], alpha: f32) -> [f32; 4] {
    let dot: f32 = (0..4).map(|i| a[i] * b[i]).sum();
    let bb = if dot < 0.0 { [-b[0], -b[1], -b[2], -b[3]] } else { b };
    let mut o = [0.0f32; 4];
    for i in 0..4 {
        o[i] = a[i] + (bb[i] - a[i]) * alpha;
    }
    let len = (o.iter().map(|v| v * v).sum::<f32>()).sqrt().max(1e-20);
    [o[0] / len, o[1] / len, o[2] / len, o[3] / len]
}

/// `cs_main` の CPU 写し: 再生指定 1 件からジョイント行列列を作る。
///
/// クロスフェード（weight < 1）は per-node TRS の混合として写す。
fn joint_matrices_for(model: &Model, packed: &PackedAnimations, pose: SkinAnimPose) -> Vec<Mat4> {
    let bind: (Vec<[f32; 3]>, Vec<[f32; 4]>, Vec<[f32; 3]>) = (
        model.nodes.iter().map(|n| n.translation).collect(),
        model.nodes.iter().map(|n| n.rotation).collect(),
        model.nodes.iter().map(|n| n.scale).collect(),
    );
    let (mut nt, mut nr, mut ns) =
        eval_pose(packed, pose.anim_b as usize, pose.time_b, &bind);
    let w = pose.weight.clamp(0.0, 1.0);
    if w < 1.0 {
        let (at, ar, as_) = eval_pose(packed, pose.anim_a as usize, pose.time_a, &bind);
        for i in 0..nt.len() {
            for k in 0..3 {
                nt[i][k] = at[i][k] + (nt[i][k] - at[i][k]) * w;
                ns[i][k] = as_[i][k] + (ns[i][k] - as_[i][k]) * w;
            }
            nr[i] = nlerp(ar[i], nr[i], w);
        }
    }

    // BFS 順でワールド行列を積む（skin_compute.wgsl ③ と同一）。
    let (bfs, parents) = compute_bfs_order(model);
    let mut world = vec![trs_to_mat([0.0; 3], [0.0, 0.0, 0.0, 1.0], [1.0; 3]); model.nodes.len()];
    for &bi in &bfs {
        let ni = bi as usize;
        let lm = trs_to_mat(nt[ni], nr[ni], ns[ni]);
        world[ni] = if parents[ni] < 0 {
            lm
        } else {
            mul(&world[parents[ni] as usize], &lm)
        };
    }

    // ジョイント行列 = world[node] * ibm（IBM は行優先 → 列優先へ転置）。
    model.skins[0]
        .joints
        .iter()
        .map(|j| {
            let mut ibm = [[0.0f32; 4]; 4];
            for c in 0..4 {
                for r in 0..4 {
                    ibm[c][r] = j.inverse_bind_matrix[r][c];
                }
            }
            mul(&world[j.node_index], &ibm)
        })
        .collect()
}

/// 2 つのジョイント行列列が実質同一か。
fn matrices_equal(a: &[Mat4], b: &[Mat4]) -> bool {
    a.len() == b.len()
        && a.iter().zip(b).all(|(x, y)| {
            x.iter()
                .flatten()
                .zip(y.iter().flatten())
                .all(|(p, q)| (p - q).abs() < 1e-5)
        })
}

// ============================================================
//  テスト用モデル生成
// ============================================================

/// ノード 0（ルート）→ ノード 1（子）の 2 段スケルトン。ジョイントは 2 本。
fn two_bone_model(animations: Vec<Animation>) -> Model {
    let node = |name: &str, children: Vec<usize>, parent: Option<usize>| ModelNode {
        name: name.into(),
        local_matrix: ModelNode::identity_matrix(),
        translation: [0.0, 0.0, 0.0],
        rotation: [0.0, 0.0, 0.0, 1.0],
        scale: [1.0, 1.0, 1.0],
        mesh_index: None,
        skin_index: None,
        children,
        parent,
    };
    Model {
        name: "two_bone".into(),
        nodes: vec![node("root", vec![1], None), node("child", vec![], Some(0))],
        root_nodes: vec![0],
        meshes: vec![],
        materials: vec![],
        textures: vec![],
        animations,
        skins: vec![Skin {
            name: "skin".into(),
            joints: vec![
                SkinJoint {
                    node_index: 0,
                    name: "root".into(),
                    inverse_bind_matrix: ModelNode::identity_matrix(),
                },
                SkinJoint {
                    node_index: 1,
                    name: "child".into(),
                    inverse_bind_matrix: ModelNode::identity_matrix(),
                },
            ],
            root_joint: Some(0),
        }],
    }
}

/// 平行移動チャンネル 1 本だけを持つアニメを作る。
///
/// `keys` は `(時刻, 平行移動)` の並び。`interp` を STEP にすると
/// 「2 キー・始点＝終点」＝動きなしアニメの再現に使える。
fn translate_anim(
    name: &str,
    target_node: usize,
    interp: Interpolation,
    keys: &[(f32, [f32; 3])],
) -> Animation {
    Animation {
        name: name.into(),
        duration: keys.last().map(|k| k.0).unwrap_or(0.0),
        channels: vec![AnimationChannel {
            target_node_index: target_node,
            sampler: AnimationSampler {
                interpolation: interp,
                timestamps: keys.iter().map(|k| k.0).collect(),
                outputs: AnimationOutputs::Translations(keys.iter().map(|k| k.1).collect()),
            },
        }],
    }
}

// ============================================================
//  テスト本体
// ============================================================

/// 【多アニメ再生の通し検証】7 本パッキングしたモデルで、index 0 以外のアニメを
/// 指定しても正しくそのアニメのポーズが出る（＝アニメテーブルのオフセットと
/// `anim_b` の解決が全 index で機能する）。
///
/// sakanadori（アニメ 7 本・1 プリム・ジョイント多数）と同じ構成条件を、
/// **中身は動くアニメ**にして再現している。ここが通れば「7 本パッキング／
/// index 解決／STEP 補間」は不具合の原因ではない。
#[test]
fn plays_non_zero_animation_index_from_seven_packed_clips() {
    // 7 本すべて別の平行移動先を持つ（index が正しく効いているか区別できる）。
    let anims: Vec<Animation> = (0..7)
        .map(|i| {
            translate_anim(
                &format!("Clip{i}"),
                1,
                Interpolation::Step,
                &[(0.0, [0.0, 0.0, 0.0]), (2.0, [(i + 1) as f32, 0.0, 0.0])],
            )
        })
        .collect();
    let model = two_bone_model(anims);
    let packed = pack_animations(&model.animations, &model.name);
    assert_eq!(packed.anim_count(), 7, "7 本すべてパッキングされる");
    assert!(packed.motionless.is_empty(), "どのクリップも動きを持つ");

    // 各アニメを「末尾キー時刻」で評価すると、子ジョイントが index に応じた位置へ動く。
    for i in 0..7u32 {
        let mats = joint_matrices_for(&model, &packed, SkinAnimPose::single(i, 2.0));
        let tx = mats[1][3][0]; // 子ジョイントの平行移動 x（列優先: col3）
        assert!(
            (tx - (i + 1) as f32).abs() < 1e-4,
            "アニメ index {i} のポーズが出る（期待 {}, 実際 {tx}）",
            i + 1
        );
    }
}

/// 【sakanadori の症状の再現】全チャンネルが「2 キー・始点＝終点」の STEP で
/// 書き出されたモデルは、どの時刻・どのアニメを再生してもバインドポーズと
/// 一致する。エンジンは正しく再生しており、動かないのはデータ側である。
///
/// あわせて、`pack_animations` がこのアセットを「動きなし」として検出することを検証する
/// （検出できなければ、同じ症状がまた無言で埋もれる）。
#[test]
fn constant_key_animation_yields_bind_pose_and_is_reported() {
    const CONST_POS: [f32; 3] = [0.25, 0.0, 0.0];
    // 始点＝終点の 2 キー（sakanadori.glb の全 7 アニメと同じ形）。
    let anims: Vec<Animation> = ["Idle", "Walk", "Cast"]
        .iter()
        .map(|n| {
            translate_anim(
                n,
                1,
                Interpolation::Step,
                &[(0.0, CONST_POS), (2.0, CONST_POS)],
            )
        })
        .collect();
    for a in &anims {
        assert!(animation_is_motionless(a), "定数キーは動きなしと判定される");
    }

    let model = two_bone_model(anims);
    let packed = pack_animations(&model.animations, &model.name);
    assert_eq!(
        packed.motionless.len(),
        3,
        "動きなしアニメ 3 本すべてが診断リストへ載る: {:?}",
        packed.motionless
    );

    // 時刻を変えてもポーズが一切変化しない（＝再生されていないように見える）。
    let t0 = joint_matrices_for(&model, &packed, SkinAnimPose::single(0, 0.0));
    for &t in &[0.5f32, 1.0, 1.9, 2.0] {
        let tn = joint_matrices_for(&model, &packed, SkinAnimPose::single(0, t));
        assert!(matrices_equal(&t0, &tn), "t={t} でもポーズが変わらない");
    }
    // アニメを切り替えても同じ（3 本とも同一の定数ポーズ）。
    let other = joint_matrices_for(&model, &packed, SkinAnimPose::single(2, 1.0));
    assert!(matrices_equal(&t0, &other), "別クリップでもポーズは同一");
}

/// 動きのあるアニメは「動きなし」と誤検出されない（診断の偽陽性防止）。
#[test]
fn moving_animation_is_not_reported_as_motionless() {
    let a = translate_anim(
        "Move",
        1,
        Interpolation::Linear,
        &[(0.0, [0.0, 0.0, 0.0]), (1.0, [1.0, 0.0, 0.0])],
    );
    assert!(!animation_is_motionless(&a));
    let packed = pack_animations(std::slice::from_ref(&a), "test");
    assert!(packed.motionless.is_empty());
}
