// ============================================================
//  random_model.rs — 同値テスト用の、決定的な乱数で作るスキンモデルと再生指定（テスト専用）
//
//  【何を散らすか】旧い作り（1 スレッド = 1 インスタンス）と新しい作り（1 レーン = 1 ノード）で
//  計算の分け方が変わる所を狙う。
//    - ノードの木: 根が複数・親の番号が子より大きい（BFS 順と番号の順が違う）・根から辿れないノード（孤立）
//    - チャンネル: T / R / S × 補間 3 種（LINEAR / STEP / CUBICSPLINE）・キー 1 個・同じ時刻のキー・
//      **同じノードと属性へのチャンネルの重複**（後が勝つ順序）・チャンネルの並びの入れ替え・空のアニメ
//    - ジョイント: 同じノードを指す重複・番号の入れ替え・枠の上限（MAX_JOINTS）いっぱい
//    - 再生指定: 範囲外のアニメ番号・端の時刻（0・長さ・キーの時刻ちょうど・範囲外）・ブレンド率
//      （1・0・0.5・乱数・NaN）・Animator 非駆動（None）
// ============================================================

use crate::engine::core::loader::model::{
    Animation, AnimationChannel, AnimationOutputs, AnimationSampler, Interpolation, Model, ModelNode, Skin, SkinJoint,
};
use crate::engine::core::renderer::skin_system::SkinAnimPose;

// ─── 散らし方の割合・範囲（データ。マジックナンバーにしない）──────────────

/// ノードが（先に作ったノードの子ではなく）根になる割合。
const ROOT_CHANCE: f32 = 0.08;
/// 根のうち 1 つを root_nodes に載せない（孤立ノードにする）割合（モデルごと）。
const ORPHAN_CHANCE: f32 = 0.5;
/// ノードがアニメのチャンネルを持つ割合（属性ごと）。
const CHANNEL_CHANCE: f32 = 0.55;
/// 同じノード・同じ属性のチャンネルを重ねて足す割合。
const DUPLICATE_CHANNEL_CHANCE: f32 = 0.06;
/// アニメが空（チャンネル 0 本）になる割合。
const EMPTY_ANIM_CHANCE: f32 = 0.08;
/// 次のキーを前のキーと同じ時刻にする割合（区間の長さ 0 の扱い）。
const SAME_TIME_KEY_CHANCE: f32 = 0.05;
/// 1 チャンネルのキーの最大数。
const MAX_KEYS: usize = 10;
/// アニメの長さ（秒）の範囲。
const DURATION_RANGE: (f32, f32) = (0.2, 3.0);
/// 平行移動の成分の範囲。
const TRANSLATION_RANGE: (f32, f32) = (-2.0, 2.0);
/// 拡縮の成分の範囲。
const SCALE_RANGE: (f32, f32) = (0.4, 1.6);
/// 再生指定で、アニメの範囲の外の時刻を作るときのはみ出し（秒）。
const TIME_OVERSHOOT: f32 = 0.3;
/// 再生指定が Animator 非駆動（None ＝静止ポーズ）になる割合。
const UNDRIVEN_CHANCE: f32 = 0.06;
/// 正規化に使う四元数の長さの下限（これより短い乱数の組は引き直す）。
const MIN_QUAT_LENGTH: f32 = 0.1;
/// 成分をちょうど +0 / -0 にする割合（実際の glTF は軸に沿った回転・行列の 0 の行を持ち、0 の符号の扱いの差が出やすい）。
const SIGNED_ZERO_CHANCE: f32 = 0.2;
/// 回転を軸に沿った回転（成分のうち 2 つがちょうど ±0）にする割合。
const AXIS_ROTATION_CHANCE: f32 = 0.25;

/// 決定的な乱数（SplitMix64）。種が同じなら同じ並びになる（失敗を再現できる）。
pub(super) struct Rng(u64);

impl Rng {
    /// 種から作る。
    pub fn new(seed: u64) -> Self {
        Self(seed)
    }

    /// 次の 64 ビット。
    pub fn next_u64(&mut self) -> u64 {
        self.0 = self.0.wrapping_add(0x9E37_79B9_7F4A_7C15);
        let mut z = self.0;
        z = (z ^ (z >> 30)).wrapping_mul(0xBF58_476D_1CE4_E5B9);
        z = (z ^ (z >> 27)).wrapping_mul(0x94D0_49BB_1331_11EB);
        z ^ (z >> 31)
    }

    /// [0, 1) の一様な実数（24 ビットの精度）。
    pub fn unit(&mut self) -> f32 {
        const MANTISSA_BITS: u32 = 24;
        (self.next_u64() >> (64 - MANTISSA_BITS)) as f32 / (1u64 << MANTISSA_BITS) as f32
    }

    /// [lo, hi) の一様な実数。
    pub fn range(&mut self, (lo, hi): (f32, f32)) -> f32 {
        lo + (hi - lo) * self.unit()
    }

    /// [0, n) の整数（n > 0）。
    pub fn below(&mut self, n: usize) -> usize {
        (self.next_u64() % n as u64) as usize
    }

    /// 割合 p で true。
    pub fn chance(&mut self, p: f32) -> bool {
        self.unit() < p
    }

    /// 単位四元数 [x, y, z, w]。
    pub fn quat(&mut self) -> [f32; 4] {
        loop {
            let q = [self.range((-1.0, 1.0)), self.range((-1.0, 1.0)), self.range((-1.0, 1.0)), self.range((-1.0, 1.0))];
            let len = q.iter().map(|c| c * c).sum::<f32>().sqrt();
            if len > MIN_QUAT_LENGTH {
                return q.map(|c| c / len);
            }
        }
    }

    /// 3 成分のベクトル（成分を時々ちょうど ±0 にする）。
    pub fn vec3(&mut self, range: (f32, f32)) -> [f32; 3] {
        [self.maybe_signed_zero(range), self.maybe_signed_zero(range), self.maybe_signed_zero(range)]
    }

    /// 範囲の実数か、割合 SIGNED_ZERO_CHANCE でちょうど +0 / -0。
    pub fn maybe_signed_zero(&mut self, range: (f32, f32)) -> f32 {
        if self.chance(SIGNED_ZERO_CHANCE) {
            self.signed_zero()
        } else {
            self.range(range)
        }
    }

    /// +0 か -0。
    pub fn signed_zero(&mut self) -> f32 {
        if self.chance(0.5) { -0.0 } else { 0.0 }
    }

    /// 回転: 単位四元数か、時々 1 軸まわりの回転（ほかの 2 成分がちょうど ±0）。
    pub fn rotation(&mut self) -> [f32; 4] {
        if !self.chance(AXIS_ROTATION_CHANCE) {
            return self.quat();
        }
        let angle = self.range((-std::f32::consts::PI, std::f32::consts::PI));
        let (sin, cos) = (angle * 0.5).sin_cos();
        let mut q = [self.signed_zero(), self.signed_zero(), self.signed_zero(), cos];
        q[self.below(3)] = sin;
        q
    }

    /// 並びをかき混ぜる（Fisher–Yates）。
    pub fn shuffle<T>(&mut self, items: &mut [T]) {
        for i in (1..items.len()).rev() {
            let j = self.below(i + 1);
            items.swap(i, j);
        }
    }
}

/// 作るモデルの大きさ。
#[derive(Clone, Copy, Debug)]
pub(super) struct ModelShape {
    /// ノード数。
    pub nodes: usize,
    /// ジョイント数（ノード数より多ければ同じノードを指す重複で埋める）。
    pub joints: usize,
    /// アニメの本数。
    pub anims: usize,
}

/// 行優先の 4x4（CPU 側の規約。SkinJoint::inverse_bind_matrix と同じ）の、回転・拡縮・平行移動の行列。
fn random_affine_row_major(rng: &mut Rng) -> [[f32; 4]; 4] {
    let [x, y, z, w] = rng.rotation();
    let s = rng.vec3(SCALE_RANGE);
    let t = rng.vec3(TRANSLATION_RANGE);
    // 回転行列（行優先・列ベクトル規約）× 拡縮、最後の列に平行移動。
    let r = [
        [1.0 - 2.0 * (y * y + z * z), 2.0 * (x * y - w * z), 2.0 * (x * z + w * y)],
        [2.0 * (x * y + w * z), 1.0 - 2.0 * (x * x + z * z), 2.0 * (y * z - w * x)],
        [2.0 * (x * z - w * y), 2.0 * (y * z + w * x), 1.0 - 2.0 * (x * x + y * y)],
    ];
    [
        [r[0][0] * s[0], r[0][1] * s[1], r[0][2] * s[2], t[0]],
        [r[1][0] * s[0], r[1][1] * s[1], r[1][2] * s[2], t[1]],
        [r[2][0] * s[0], r[2][1] * s[1], r[2][2] * s[2], t[2]],
        [rng.signed_zero(), rng.signed_zero(), rng.signed_zero(), 1.0],
    ]
}

/// 補間の種類を選ぶ（LINEAR を多めに）。
fn random_interpolation(rng: &mut Rng) -> Interpolation {
    match rng.below(4) {
        0 | 1 => Interpolation::Linear,
        2 => Interpolation::Step,
        _ => Interpolation::CubicSpline,
    }
}

/// 昇順のキーの時刻（同じ時刻のキーを時々混ぜる）。
fn random_timestamps(rng: &mut Rng, duration: f32) -> Vec<f32> {
    let count = 1 + rng.below(MAX_KEYS);
    let mut times: Vec<f32> = (0..count).map(|_| rng.range((0.0, duration))).collect();
    times.sort_by(f32::total_cmp);
    for i in 1..times.len() {
        if rng.chance(SAME_TIME_KEY_CHANCE) {
            times[i] = times[i - 1];
        }
    }
    times
}

/// 1 チャンネル（属性 `prop`: 0=T, 1=R, 2=S）を作る。CUBICSPLINE は値をキーごとに 3 個（入りの接線・値・出の接線）持つ。
fn random_channel(rng: &mut Rng, node: usize, prop: usize, duration: f32) -> AnimationChannel {
    let interpolation = random_interpolation(rng);
    let timestamps = random_timestamps(rng, duration);
    let values_per_key = if interpolation == Interpolation::CubicSpline { 3 } else { 1 };
    let count = timestamps.len() * values_per_key;
    let outputs = match prop {
        0 => AnimationOutputs::Translations((0..count).map(|_| rng.vec3(TRANSLATION_RANGE)).collect()),
        1 => AnimationOutputs::Rotations((0..count).map(|_| rng.rotation()).collect()),
        _ => AnimationOutputs::Scales((0..count).map(|_| rng.vec3(SCALE_RANGE)).collect()),
    };
    AnimationChannel {
        target_node_index: node,
        sampler: AnimationSampler { interpolation, timestamps, outputs },
    }
}

/// アニメ 1 本（チャンネルの並びはかき混ぜ、同じノード・属性への重複を時々足す）。
fn random_animation(rng: &mut Rng, index: usize, nodes: usize) -> Animation {
    let duration = rng.range(DURATION_RANGE);
    let mut channels = Vec::new();
    if !rng.chance(EMPTY_ANIM_CHANCE) {
        for node in 0..nodes {
            for prop in 0..3 {
                if rng.chance(CHANNEL_CHANCE) {
                    channels.push(random_channel(rng, node, prop, duration));
                }
                if rng.chance(DUPLICATE_CHANNEL_CHANCE) {
                    channels.push(random_channel(rng, node, prop, duration));
                }
            }
        }
        rng.shuffle(&mut channels);
    }
    Animation { name: format!("anim{index}"), duration, channels }
}

/// 乱数のスキンモデルを作る（ノードの木・バインドポーズ・ジョイント・アニメ）。
pub(super) fn random_model(rng: &mut Rng, name: &str, shape: ModelShape) -> Model {
    let n = shape.nodes;
    // 木の順（番号とは別の並び）で親を決める: 先に並んだノードの中から親を選ぶので輪にならない。
    let mut tree_order: Vec<usize> = (0..n).collect();
    rng.shuffle(&mut tree_order);
    let mut parent: Vec<Option<usize>> = vec![None; n];
    let mut roots = Vec::new();
    for (k, &node) in tree_order.iter().enumerate() {
        if k == 0 || rng.chance(ROOT_CHANCE) {
            roots.push(node);
        } else {
            parent[node] = Some(tree_order[rng.below(k)]);
        }
    }
    // 根が 2 つ以上あれば、時々 1 つを root_nodes に載せない（根から辿れない孤立ノード。compute_bfs_order が末尾へ足す）。
    if roots.len() > 1 && rng.chance(ORPHAN_CHANCE) {
        roots.remove(1 + rng.below(roots.len() - 1));
    }

    let nodes: Vec<ModelNode> = (0..n)
        .map(|i| ModelNode {
            name: format!("node{i}"),
            local_matrix: ModelNode::identity_matrix(),
            translation: rng.vec3(TRANSLATION_RANGE),
            rotation: rng.rotation(),
            scale: rng.vec3(SCALE_RANGE),
            mesh_index: None,
            skin_index: None,
            children: (0..n).filter(|&c| parent[c] == Some(i)).collect(),
            parent: parent[i],
        })
        .collect();

    // ジョイント: 全ノードをかき混ぜた並びから取り、足りなければ同じノードを指す重複で埋める。
    let mut joint_nodes: Vec<usize> = (0..n).collect();
    rng.shuffle(&mut joint_nodes);
    joint_nodes.truncate(shape.joints);
    while joint_nodes.len() < shape.joints {
        joint_nodes.push(rng.below(n));
    }
    let joints = joint_nodes
        .iter()
        .enumerate()
        .map(|(j, &node_index)| SkinJoint {
            node_index,
            name: format!("joint{j}"),
            inverse_bind_matrix: random_affine_row_major(rng),
        })
        .collect();

    let animations = (0..shape.anims).map(|a| random_animation(rng, a, n)).collect();
    Model {
        name: name.to_string(),
        nodes,
        root_nodes: roots,
        meshes: vec![],
        materials: vec![],
        textures: vec![],
        animations,
        skins: vec![Skin { name: "skin".into(), joints, root_joint: None }],
    }
}

/// アニメ `anim` の上で再生指定に使う時刻を選ぶ（端・キーの時刻ちょうど・範囲外・乱数）。
fn pick_time(rng: &mut Rng, model: &Model, anim: usize) -> f32 {
    let Some(a) = model.animations.get(anim) else {
        return rng.range((0.0, DURATION_RANGE.1));
    };
    match rng.below(5) {
        0 => 0.0,
        1 => a.duration,
        2 => {
            // あるチャンネルのキーの時刻ちょうど（区間の端の扱い）
            if a.channels.is_empty() {
                0.0
            } else {
                let ts = &a.channels[rng.below(a.channels.len())].sampler.timestamps;
                ts[rng.below(ts.len())]
            }
        }
        3 => rng.range((-TIME_OVERSHOOT, a.duration + TIME_OVERSHOOT)),
        _ => rng.range((0.0, a.duration)),
    }
}

/// ブレンド率を選ぶ（1＝フェード無し・0・0.5・乱数・NaN〈CPU 側で 1 へ倒す〉）。
fn pick_weight(rng: &mut Rng) -> f32 {
    match rng.below(6) {
        0 | 1 => 1.0,
        2 => 0.0,
        3 => 0.5,
        4 => rng.unit(),
        _ => f32::NAN,
    }
}

/// インスタンス `count` 体ぶんの再生指定（範囲外のアニメ番号・Animator 非駆動も混ぜる）。
pub(super) fn random_poses(rng: &mut Rng, model: &Model, count: usize) -> Vec<Option<SkinAnimPose>> {
    // アニメ番号は本数より少し先まで選ぶ（範囲外は CPU 側 normalize_pose が最後のアニメへ丸める）。
    let anim_choices = model.animations.len() + 2;
    (0..count)
        .map(|_| {
            if rng.chance(UNDRIVEN_CHANCE) {
                return None;
            }
            let anim_a = rng.below(anim_choices);
            let anim_b = rng.below(anim_choices);
            Some(SkinAnimPose {
                anim_a: anim_a as u32,
                time_a: pick_time(rng, model, anim_a),
                anim_b: anim_b as u32,
                time_b: pick_time(rng, model, anim_b),
                weight: pick_weight(rng),
            })
        })
        .collect()
}
