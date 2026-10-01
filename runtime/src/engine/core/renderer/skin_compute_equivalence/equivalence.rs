// ============================================================
//  equivalence.rs — 旧い skin compute と新しい skin compute の突き合わせ（テスト専用・実 GPU・--ignored）
//
//    random_models_match_legacy_bitwise … 乱数のモデル（ノード 1〜64・ジョイント〜128・補間 3 種）で、
//                                         すべてのインスタンス × 128 枠のジョイント行列がビット単位で一致する
//    nodes_beyond_limit_are_skipped     … ノード数が上限（64）を超えるモデルの新しい決め事（上限以上のノードは
//                                         評価しない・その子は根・それを指すジョイントは単位行列）を、
//                                         上限内に切り詰めたモデルの旧い作りの結果と突き合わせる
//    real_models_match_legacy_bitwise   … 実際の glb（環境変数 SEED_SKIN_EQUIV_MODELS のフォルダ）の全アニメを、
//                                         時刻を刻んで・ブレンドも混ぜて突き合わせる
//    random_models_in_one_pass_match_single_dispatch / real_models_in_one_pass_match_single_dispatch
//                                       … 本体と同じく複数のモデルを 1 つの ComputePass にまとめて走らせても、
//                                         新しい作りが 1 つずつ走らせた旧い作りとビット単位で同じ
//
//  実行: cargo test skin_compute_equivalence -- --ignored --nocapture
// ============================================================

use std::path::{Path, PathBuf};

use super::harness::{compare, Comparison, Implementation, SkinGpu};
use super::random_model::{random_model, random_poses, ModelShape, Rng};
use crate::engine::core::loader::model::{
    AnimationChannel, AnimationOutputs, AnimationSampler, Interpolation, Model, ModelNode, SkinJoint,
};
use crate::engine::core::renderer::skin_system::{SkinAnimPose, MAX_JOINTS, MAX_NODES};

/// 乱数の種（失敗したらこの値と何番目のモデルかで再現できる）。
const RANDOM_SEED: u64 = 0x5EED_5C1D_2026_1002;
/// 乱数のモデルの形（ノード数・ジョイント数・アニメの本数）。上限ちょうど（64 ノード・128 ジョイント）と小さいものを混ぜる。
const RANDOM_SHAPES: [ModelShape; 10] = [
    ModelShape { nodes: 1, joints: 1, anims: 1 },
    ModelShape { nodes: 2, joints: 3, anims: 2 },
    ModelShape { nodes: 7, joints: 5, anims: 1 },
    ModelShape { nodes: 12, joints: 12, anims: 3 },
    ModelShape { nodes: 20, joints: 18, anims: 6 },
    ModelShape { nodes: 33, joints: 40, anims: 4 },
    ModelShape { nodes: 59, joints: 57, anims: 6 },
    ModelShape { nodes: 63, joints: 63, anims: 5 },
    ModelShape { nodes: MAX_NODES, joints: MAX_NODES, anims: 4 },
    ModelShape { nodes: MAX_NODES, joints: MAX_JOINTS, anims: 8 },
];
/// 形ごとに作るモデルの数（種は形と番号で変える）。
const MODELS_PER_SHAPE: usize = 4;
/// 1 回のディスパッチのインスタンス数（旧い作りの 64 スレッドのワークグループを跨ぐ数）。
const RANDOM_INSTANCES: usize = 131;
/// 1 つのパスにまとめて走らせるときの、モデルごとのインスタンス数（本体のバッチと同じく少数〜数十）。
const ONE_PASS_INSTANCES: usize = 9;

/// ノード数の上限を超えるモデルの、上限より後ろに足すノード数。
const EXTRA_NODES: usize = 6;

/// 実際の glb を置いたフォルダを渡す環境変数。
const MODELS_DIR_ENV: &str = "SEED_SKIN_EQUIV_MODELS";
/// 実際のモデルの各アニメを何等分した時刻で再生するか（0 と長さちょうどを含む）。
const REAL_TIME_STEPS: usize = 32;
/// 実際のモデルに混ぜるブレンドの再生指定の数。
const REAL_BLEND_POSES: usize = 64;

/// 比べた結果を出し、1 つでもビットが違えば失敗にする。
fn assert_bit_identical(what: &str, total: &Comparison) {
    eprintln!(
        "[skin equivalence] {what}: 行列 {} 個を比べた・ビットの違う行列 {} 個（最大 {} ULP）・両方 NaN の要素 {} 個",
        total.matrices, total.mismatched_matrices, total.max_ulp, total.both_nan
    );
    assert_eq!(
        total.mismatched_matrices, 0,
        "{what}: 新しい skin compute の出力が旧い作りとビット単位で一致しない。最初の違い: {}",
        total.first_mismatch.as_deref().unwrap_or("-")
    );
    assert!(total.matrices > 0, "{what}: 比べる行列が無い（テストの作りの誤り）");
}

/// GPU が無ければスキップの理由を出して None。
fn gpu_or_skip() -> Option<SkinGpu> {
    let gpu = SkinGpu::new();
    match &gpu {
        Some(g) => eprintln!("[skin equivalence] アダプタ: {}", g.adapter_name),
        None => eprintln!("[skin equivalence] GPU アダプタ・デバイスが無いため検証をスキップ"),
    }
    gpu
}

/// 乱数のモデル（ノード 1〜64）で、新しい作りが旧い作りとビット単位で同じジョイント行列を出す。
#[test]
#[ignore = "実 GPU が必要。--ignored で実行する"]
fn random_models_match_legacy_bitwise() {
    let _guard = crate::engine::core::renderer::GPU_TEST_LOCK.lock().unwrap_or_else(|e| e.into_inner());
    let Some(gpu) = gpu_or_skip() else { return };
    let mut total = Comparison::default();
    for (shape_index, shape) in RANDOM_SHAPES.iter().enumerate() {
        for k in 0..MODELS_PER_SHAPE {
            let seed = RANDOM_SEED ^ ((shape_index as u64) << 32) ^ k as u64;
            let mut rng = Rng::new(seed);
            let model = random_model(&mut rng, &format!("random_{shape_index}_{k}"), *shape);
            let poses = random_poses(&mut rng, &model, RANDOM_INSTANCES);
            let (legacy, new) = gpu.run_both(&model, &poses).expect("スキン＋アニメありのモデル");
            total.absorb(compare(&format!("形 {shape:?} の {k} 番（種 {seed:#x}）"), &legacy, &new));
        }
    }
    assert_bit_identical("乱数のモデル", &total);
}

/// ノード数が上限を超えるモデル: 上限以上の番号のノードを評価しない新しい決め事を、
/// 「そのノードが無い（上限内に切り詰めた）モデル」の旧い作りの結果と突き合わせる。
///
/// 上限内のモデル M に、次のノードを足したモデル M+ を作る（足したノードの番号はすべて上限以上）:
///   - 上限内のノードの子になる葉 2 つ（ジョイントでない）と、アニメのチャンネルを持つ葉 2 つ
///     → 新しい作りでは評価しない＝M と同じ
///   - 根を 1 つ足し、M の根の 1 つをその子にする → 新しい作りではその子を根として扱う＝M と同じ
///   - ジョイントを 1 つ足して上限以上のノードを指させる → 新しい作りでは単位行列＝M の余りの枠（単位行列）と同じ
#[test]
#[ignore = "実 GPU が必要。--ignored で実行する"]
fn nodes_beyond_limit_are_skipped() {
    let _guard = crate::engine::core::renderer::GPU_TEST_LOCK.lock().unwrap_or_else(|e| e.into_inner());
    let Some(gpu) = gpu_or_skip() else { return };
    let mut rng = Rng::new(RANDOM_SEED ^ 0xB10C);
    let shape = ModelShape { nodes: MAX_NODES, joints: MAX_NODES - 4, anims: 3 };
    let base = random_model(&mut rng, "within_limit", shape);
    let extended = extend_beyond_limit(&mut rng, &base);
    assert!(extended.nodes.len() > MAX_NODES, "上限を超えるモデルになっている");
    let poses = random_poses(&mut rng, &base, RANDOM_INSTANCES);

    let legacy_base = gpu.run(&base, &poses, Implementation::Legacy).expect("上限内のモデル");
    let new_extended = gpu.run(&extended, &poses, Implementation::New).expect("上限を超えるモデル");
    assert_bit_identical(
        "上限を超えるモデル（新しい作り）と上限内に切り詰めたモデル（旧い作り）",
        &compare("beyond_limit", &legacy_base, &new_extended),
    );
}

/// `base`（ノード数 = MAX_NODES）に上限以上の番号のノードを足したモデルを作る（`nodes_beyond_limit_are_skipped` の説明）。
fn extend_beyond_limit(rng: &mut Rng, base: &Model) -> Model {
    let n = base.nodes.len();
    let mut nodes: Vec<ModelNode> = base
        .nodes
        .iter()
        .map(|node| ModelNode {
            name: node.name.clone(),
            local_matrix: node.local_matrix,
            translation: node.translation,
            rotation: node.rotation,
            scale: node.scale,
            mesh_index: node.mesh_index,
            skin_index: node.skin_index,
            children: node.children.clone(),
            parent: node.parent,
        })
        .collect();
    let mut roots = base.root_nodes.clone();
    let new_node = |index: usize, parent: Option<usize>| ModelNode {
        name: format!("extra{index}"),
        local_matrix: ModelNode::identity_matrix(),
        translation: [0.5, -0.25, 1.0],
        rotation: [0.0, 0.0, 0.0, 1.0],
        scale: [1.0, 2.0, 1.0],
        mesh_index: None,
        skin_index: None,
        children: vec![],
        parent,
    };
    // 葉 4 つ（上限内のノードの子）。後ろの 2 つにはアニメのチャンネルを付ける。
    for k in 0..EXTRA_NODES - 2 {
        let index = n + k;
        let parent = rng.below(n);
        nodes[parent].children.push(index);
        nodes.push(new_node(index, Some(parent)));
    }
    // 根を 1 つ足し、元の根の 1 つをその子にする（元の root_nodes の同じ位置に置き換える）。
    let new_root = n + EXTRA_NODES - 2;
    let adopted_slot = rng.below(roots.len());
    let adopted = roots[adopted_slot];
    roots[adopted_slot] = new_root;
    nodes[adopted].parent = Some(new_root);
    let mut root_node = new_node(new_root, None);
    root_node.children.push(adopted);
    nodes.push(root_node);
    // 最後の 1 つ（ジョイントに指させる葉。新しい根の子にする）。
    let pointed = n + EXTRA_NODES - 1;
    nodes[new_root].children.push(pointed);
    nodes.push(new_node(pointed, Some(new_root)));

    // ジョイント: 元のものの後ろに、上限以上のノードを指すものを 1 つ足す。
    let mut joints: Vec<SkinJoint> = base.skins[0]
        .joints
        .iter()
        .map(|j| SkinJoint { node_index: j.node_index, name: j.name.clone(), inverse_bind_matrix: j.inverse_bind_matrix })
        .collect();
    joints.push(SkinJoint { node_index: pointed, name: "beyond".into(), inverse_bind_matrix: ModelNode::identity_matrix() });

    // アニメ: 元のチャンネルの後ろに、足した葉を動かすチャンネルを付ける（新しい作りでは使われない）。
    let animations = base
        .animations
        .iter()
        .map(|anim| {
            let mut channels: Vec<AnimationChannel> = anim.channels.iter().map(clone_channel).collect();
            for target in [n + EXTRA_NODES - 4, n + EXTRA_NODES - 3, new_root] {
                channels.push(AnimationChannel {
                    target_node_index: target,
                    sampler: AnimationSampler {
                        interpolation: Interpolation::Linear,
                        timestamps: vec![0.0, anim.duration],
                        outputs: AnimationOutputs::Translations(vec![[1.0, 2.0, 3.0], [-3.0, 0.5, 2.0]]),
                    },
                });
            }
            crate::engine::core::loader::model::Animation {
                name: anim.name.clone(),
                duration: anim.duration,
                channels,
            }
        })
        .collect();

    Model {
        name: format!("{}_beyond_limit", base.name),
        nodes,
        root_nodes: roots,
        meshes: vec![],
        materials: vec![],
        textures: vec![],
        animations,
        skins: vec![crate::engine::core::loader::model::Skin {
            name: base.skins[0].name.clone(),
            joints,
            root_joint: base.skins[0].root_joint,
        }],
    }
}

/// チャンネルを写す（AnimationChannel は Clone を持たないため）。
fn clone_channel(channel: &AnimationChannel) -> AnimationChannel {
    let s = &channel.sampler;
    let outputs = match &s.outputs {
        AnimationOutputs::Translations(v) => AnimationOutputs::Translations(v.clone()),
        AnimationOutputs::Rotations(v) => AnimationOutputs::Rotations(v.clone()),
        AnimationOutputs::Scales(v) => AnimationOutputs::Scales(v.clone()),
        AnimationOutputs::MorphWeights(v) => AnimationOutputs::MorphWeights(v.clone()),
    };
    AnimationChannel {
        target_node_index: channel.target_node_index,
        sampler: AnimationSampler { interpolation: s.interpolation, timestamps: s.timestamps.clone(), outputs },
    }
}

/// 本体と同じく、複数のモデルのスキニングを **1 つの ComputePass にまとめて** 走らせても、
/// 新しい作りの結果は 1 つずつ走らせた旧い作りの結果とビット単位で同じ（パスの中で並んで走る形の確かめ）。
///
/// 旧い作りを 1 つのパスにまとめた結果と 1 つずつの結果の違いも数えて出す（参考。旧い作りが並んで走ると
/// 崩れるかを見る。崩れていても失敗にはしない＝旧い作りはもう使わない）。
#[test]
#[ignore = "実 GPU が必要。--ignored で実行する"]
fn random_models_in_one_pass_match_single_dispatch() {
    let _guard = crate::engine::core::renderer::GPU_TEST_LOCK.lock().unwrap_or_else(|e| e.into_inner());
    let Some(gpu) = gpu_or_skip() else { return };
    let mut models = Vec::new();
    let mut poses = Vec::new();
    for (shape_index, shape) in RANDOM_SHAPES.iter().enumerate() {
        let mut rng = Rng::new(RANDOM_SEED ^ 0x0E_0A55 ^ shape_index as u64);
        let model = random_model(&mut rng, &format!("one_pass_{shape_index}"), *shape);
        poses.push(random_poses(&mut rng, &model, ONE_PASS_INSTANCES));
        models.push(model);
    }
    compare_one_pass("乱数のモデル", &gpu, &models, &poses);
}

/// 1 つのパスでの新しい作り・旧い作りと、1 つずつ走らせた旧い作り（基準）を比べる（`random_models_in_one_pass_…`・`real_models_…`）。
fn compare_one_pass(what: &str, gpu: &SkinGpu, models: &[Model], poses: &[Vec<Option<SkinAnimPose>>]) {
    let jobs: Vec<(&Model, &[Option<SkinAnimPose>])> =
        models.iter().zip(poses).map(|(m, p)| (m, p.as_slice())).collect();
    let new_one_pass = gpu.run_in_one_pass(&jobs, Implementation::New).expect("スキン＋アニメありのモデル");
    let legacy_one_pass = gpu.run_in_one_pass(&jobs, Implementation::Legacy).expect("スキン＋アニメありのモデル");
    let mut new_total = Comparison::default();
    let mut legacy_total = Comparison::default();
    for (i, (model, model_poses)) in jobs.iter().enumerate() {
        let reference = gpu.run(model, model_poses, Implementation::Legacy).expect("スキン＋アニメありのモデル");
        new_total.absorb(compare(&format!("{} を 1 つのパスで（新）", model.name), &reference, &new_one_pass[i]));
        legacy_total.absorb(compare(&format!("{} を 1 つのパスで（旧）", model.name), &reference, &legacy_one_pass[i]));
    }
    eprintln!(
        "[skin equivalence] {what}を 1 つのパスで（参考・旧い作り）: 行列 {} 個・1 つずつの結果と違う行列 {} 個（最大 {} ULP）{}",
        legacy_total.matrices,
        legacy_total.mismatched_matrices,
        legacy_total.max_ulp,
        legacy_total.first_mismatch.as_deref().map(|m| format!("・最初の違い: {m}")).unwrap_or_default()
    );
    assert_bit_identical(&format!("{what}（{} 個）を 1 つのパスで走らせた新しい作り", jobs.len()), &new_total);
}

/// フォルダの下の .glb / .gltf を集める（名前の順）。
fn collect_model_files(dir: &Path, out: &mut Vec<PathBuf>) {
    let Ok(entries) = std::fs::read_dir(dir) else { return };
    let mut paths: Vec<PathBuf> = entries.filter_map(|e| e.ok().map(|e| e.path())).collect();
    paths.sort();
    for path in paths {
        if path.is_dir() {
            collect_model_files(&path, out);
        } else if path
            .extension()
            .and_then(|e| e.to_str())
            .is_some_and(|e| e.eq_ignore_ascii_case("glb") || e.eq_ignore_ascii_case("gltf"))
        {
            out.push(path);
        }
    }
}

/// 実際のモデルの再生指定: 各アニメを REAL_TIME_STEPS 等分した時刻（端を含む）・ブレンド・Animator 非駆動。
fn real_model_poses(rng: &mut Rng, model: &Model) -> Vec<Option<SkinAnimPose>> {
    let mut poses = vec![None];
    for (index, anim) in model.animations.iter().enumerate() {
        for step in 0..=REAL_TIME_STEPS {
            let time = anim.duration * step as f32 / REAL_TIME_STEPS as f32;
            poses.push(Some(SkinAnimPose::single(index as u32, time)));
        }
    }
    poses.extend(random_poses(rng, model, REAL_BLEND_POSES));
    poses
}

/// 実際の glb（環境変数 SEED_SKIN_EQUIV_MODELS のフォルダ）のスキン＋アニメのモデルすべてで、
/// 新しい作りが旧い作りとビット単位で同じジョイント行列を出す。
#[test]
#[ignore = "実 GPU と実際の glb のフォルダ（環境変数 SEED_SKIN_EQUIV_MODELS）が必要。--ignored で実行する"]
fn real_models_match_legacy_bitwise() {
    let _guard = crate::engine::core::renderer::GPU_TEST_LOCK.lock().unwrap_or_else(|e| e.into_inner());
    let Some((files, models)) = load_real_models() else { return };
    let Some(gpu) = gpu_or_skip() else { return };
    let mut total = Comparison::default();
    for (k, (path, model)) in models.iter().enumerate() {
        let mut rng = Rng::new(RANDOM_SEED ^ k as u64);
        let poses = real_model_poses(&mut rng, model);
        let (legacy, new) = gpu.run_both(model, &poses).expect("スキン＋アニメありのモデル");
        let result = compare(&path.display().to_string(), &legacy, &new);
        eprintln!(
            "[skin equivalence] {}: ノード {}・ジョイント {}・アニメ {}・再生指定 {} → 違う行列 {}",
            path.display(),
            model.nodes.len(),
            model.skins[0].joints.len(),
            model.animations.len(),
            poses.len(),
            result.mismatched_matrices
        );
        total.absorb(result);
    }
    eprintln!("[skin equivalence] 実際のモデル {} 個（ファイル {files} 個のうちスキン＋アニメあり）", models.len());
    assert_bit_identical("実際のモデル", &total);
}

/// 実際の glb を全部（本体と同じく）1 つの ComputePass にまとめて走らせても、新しい作りの結果は
/// 1 つずつ走らせた旧い作りの結果とビット単位で同じ（WarashibeFishing の MainGame は 17〜18 個のスキンを 1 パスに積む）。
#[test]
#[ignore = "実 GPU と実際の glb のフォルダ（環境変数 SEED_SKIN_EQUIV_MODELS）が必要。--ignored で実行する"]
fn real_models_in_one_pass_match_single_dispatch() {
    let _guard = crate::engine::core::renderer::GPU_TEST_LOCK.lock().unwrap_or_else(|e| e.into_inner());
    let Some((_files, loaded)) = load_real_models() else { return };
    let Some(gpu) = gpu_or_skip() else { return };
    let mut models = Vec::new();
    let mut poses = Vec::new();
    for (k, (_path, model)) in loaded.into_iter().enumerate() {
        let mut rng = Rng::new(RANDOM_SEED ^ 0x0E_0A55 ^ k as u64);
        poses.push(random_poses(&mut rng, &model, ONE_PASS_INSTANCES));
        models.push(model);
    }
    compare_one_pass("実際のモデル", &gpu, &models, &poses);
}

/// 環境変数 SEED_SKIN_EQUIV_MODELS のフォルダの下の glb のうち、スキン＋アニメのあるものを読む
/// （環境変数が無ければ理由を出して None。読めないファイルがあれば失敗にする）。返すのは（ファイル数, (パス, モデル) の列）。
fn load_real_models() -> Option<(usize, Vec<(PathBuf, Model)>)> {
    let Some(dir) = std::env::var_os(MODELS_DIR_ENV) else {
        eprintln!("[skin equivalence] 環境変数 {MODELS_DIR_ENV} が無いため実際のモデルの検証をスキップ");
        return None;
    };
    let mut files = Vec::new();
    collect_model_files(Path::new(&dir), &mut files);
    let mut models = Vec::new();
    for path in &files {
        let model = match crate::engine::core::loader::load_model(path) {
            Ok(model) => model,
            Err(e) => panic!("{} を読めない: {e}", path.display()),
        };
        if !model.skins.is_empty() && !model.animations.is_empty() {
            models.push((path.clone(), model));
        }
    }
    assert!(!models.is_empty(), "{MODELS_DIR_ENV} のフォルダにスキン＋アニメのモデルが無い");
    Some((files.len(), models))
}
