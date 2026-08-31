// ============================================================
// gbuffer_skinned_vertex.wgsl — G-Buffer 専用 スキンメッシュ頂点シェーダ
//
// shader_skinned_vertex.wgsl との違いは `prev_clip` を実値で埋める点のみ
// （分けた理由は gbuffer_static_vertex.wgsl 冒頭のコメントと同じ）。
//
// ## 【重要】スキンメッシュの速度は「剛体ぶんのみ」である（今回のスコープ）
//
// 本シェーダは前フレームのワールド座標を
//     prev_model * (**今フレームの** skin 行列 * local_pos)
// で作る。つまり **アクタ全体の移動・回転（剛体運動）は正しく速度に乗るが、
// ボーンアニメーションによる変形ぶんは乗らない**（走るキャラの胴体の速度は
// 出るが、腕の振りぶんの追加速度は出ない）。
//
// ### なぜ前フレームのボーンパレットを使わないのか（調査結果と判断）
// 素直な案は「Skin Compute の出力バッファ（`sk_jmats_lod{N}`）を 2 枚持って
// 前フレームぶんを残す」だが、**この案はコストの前に正しさで落ちる**:
//
//   - 出力バッファの添字は `compact_instance_index * MAX_JOINTS + joint` であり、
//     compact 添字は「その LOD でその フレームに可視だったインスタンスの並び順」
//     でしかない。距離 LOD のバケット移動が 1 個でも起きると、同じスロット番号が
//     **別のインスタンス**を指す。前フレームバッファをそのまま読むと、無関係な
//     ポーズとの差分＝画面全体に飛び散る巨大な速度になる（要件4 が禁じる爆発）。
//   - 正しくやるには「今フレームの compact スロット → 前フレームの (LOD, スロット)」
//     の再マップ表と、4 つの LOD 出力バッファ全部のバインド、および前フレーム
//     非可視インスタンスの無効フラグが要る。group 3（joints）のレイアウトを
//     G-Buffer 専用に作り直す必要もある。
//   - メモリ増分は素直な二重化で `MAX_JOINTS(128) * 64B * max_instances * NUM_LODS(4)`
//     ＝ **32KB × インスタンス上限**（100 体で 3.2MB／バッチ）。
//     compact ではなく「元インスタンス添字」で 1 枚だけ持つ改良案なら
//     8KB × インスタンス上限（100 体で 0.8MB／バッチ）で済み、スキャッタ用の
//     compute ディスパッチが 1 本増える。
//
// 消費者（TAA / モーションブラー）がまだ存在しない段階で、アニメーション系の
// 中核（Skin Compute の添字規約）を作り替える価値は無いと判断し、今回は
// 剛体ぶんのみで確定させた。将来 TAA を入れる際は上記「元インスタンス添字で
// 1 枚持つ＋スキャッタ compute」案から着手すること。
//
// ## 依存
//   shader_common.wgsl   … CameraUniform / ModelUniform / VertexOutput
//   velocity_common.wgsl … PrevModelUniform / u_prev_instances
// ============================================================

const MAX_JOINTS: u32 = 128u;

/// ジョイント行列（列優先、コンパクト順）
@group(3) @binding(0) var<storage, read> joint_matrices: array<mat4x4<f32>>;

struct VertexInput {
    @location(0) position: vec3<f32>,
    @location(1) normal:   vec3<f32>,
    @location(2) tangent:  vec4<f32>,
    @location(3) uv0:      vec2<f32>,
    @location(4) uv1:      vec2<f32>,
    @location(5) color:    vec4<f32>,
    // スキニング
    @location(6) joints:  vec4<u32>,
    @location(7) weights: vec4<f32>,
}

@vertex
fn vs_main(v: VertexInput, @builtin(instance_index) inst_idx: u32) -> VertexOutput {
    let u_model = u_instances[inst_idx];

    // このインスタンスのジョイント行列基点
    let base = inst_idx * MAX_JOINTS;
    let j = vec4<u32>(
        min(v.joints.x, MAX_JOINTS - 1u),
        min(v.joints.y, MAX_JOINTS - 1u),
        min(v.joints.z, MAX_JOINTS - 1u),
        min(v.joints.w, MAX_JOINTS - 1u),
    );

    // ブレンドスキニング行列（列優先）
    let skin =
        v.weights.x * joint_matrices[base + j.x] +
        v.weights.y * joint_matrices[base + j.y] +
        v.weights.z * joint_matrices[base + j.z] +
        v.weights.w * joint_matrices[base + j.w];

    let skinned_local = skin * vec4<f32>(v.position, 1.0);
    let world_pos4    = u_model.model * skinned_local;
    let nm            = u_model.normal_matrix;
    let wn            = normalize((nm * (skin * vec4<f32>(v.normal,      0.0))).xyz);
    let wt            = normalize((nm * (skin * vec4<f32>(v.tangent.xyz, 0.0))).xyz);
    let wbt           = normalize(cross(wn, wt) * v.tangent.w);

    // ── 前フレームのワールド座標（剛体ぶんのみ。ファイル冒頭の説明を参照）──
    //   スキン行列は今フレームのものを流用する。したがって「前フレームの姿勢」
    //   ではなく「今フレームの姿勢を前フレームの位置・向きで置いたもの」になる。
    let prev_world_pos4 = u_prev_instances[inst_idx].prev_model * skinned_local;

    let clip = u_camera.view_proj * world_pos4;

    var out: VertexOutput;
    out.clip_pos     = clip;
    out.world_pos    = world_pos4.xyz;
    out.world_normal = wn;
    out.world_tan    = wt;
    out.world_bitan  = wbt;
    out.uv0          = v.uv0;
    out.uv1          = v.uv1;
    out.color        = v.color;
    // 法線シャープネス（地形のみ意味を持つ。通常メッシュでは必ず 0 に落ちる）。
    out.sharpness    = decode_vertex_sharpness(v.tangent.w);
    out.render_tag   = instance_render_tag(u_model);
    out.curr_clip    = clip;
    out.prev_clip    = u_camera.prev_view_proj * prev_world_pos4;
    return out;
}
