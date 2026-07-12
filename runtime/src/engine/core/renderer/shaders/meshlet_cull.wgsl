// ============================================================
// meshlet_cull.wgsl — GPU メッシュレットカリング コンピュートシェーダ（第1弾）
//
// 1 プリミティブ（LOD0）の「可視インスタンス × メッシュレット」を走査し、
// 各メッシュレットの境界球をインスタンス行列でワールド空間へ変換して
//   1) 視錐台 6 平面テスト（保守的マージン付き）
//   2) 法線コーン背面棄却（保守側 cutoff）
// を行い、生存したものだけ atomicAdd でコンパクトに DrawIndexedIndirect を書き出す。
// 書き出した件数は draw_count（atomic）に入り、multi_draw_indexed_indirect_count の
// カウントバッファとして使われる。
//
// 【パリティ設計】
// カリングは保守側（棄却しすぎない）に倒す。境界球半径にマージンを足し、コーン棄却は
// cutoff にマージンを足して発火しにくくする。これにより「本来見えるメッシュレットを
// 誤って捨てる」ことを防ぎ、トグル OFF（従来全描画）との見た目一致を担保する。
//
// Group 0:
//   0 = instances   (array<InstanceData>, storage read)  … ノード LOD0 の可視インスタンス行列
//   1 = meshlets    (array<Meshlet>,      storage read)  … プリミティブのメッシュレット記述子
//   2 = draw_cmds   (array<DrawIndexedIndirect>, storage rw) … 生存分を先頭から詰める
//   3 = draw_count  (atomic<u32>,         storage rw)     … 生存件数（indirect count）
//   4 = params      (CullParams,          uniform)
// ============================================================

// インスタンス行列（gpu_resources::ModelUniform と一致: model + normal_matrix, 128B）
struct InstanceData {
    model:         mat4x4<f32>,
    normal_matrix: mat4x4<f32>,
}

// メッシュレット記述子（gpu_resources::GpuMeshlet と一致, stride 48）
struct Meshlet {
    index_offset: u32,
    index_count:  u32,
    _pad0:        u32,
    _pad1:        u32,
    center:       vec3<f32>,
    radius:       f32,
    cone_axis:    vec3<f32>,
    cone_cutoff:  f32,
}

// draw_indexed_indirect 引数（wgpu 仕様）
struct DrawIndexedIndirect {
    index_count:    u32,
    instance_count: u32,
    first_index:    u32,
    base_vertex:    i32,
    first_instance: u32,
}

struct CullParams {
    planes:            array<vec4<f32>, 6>, // 視錐台平面（未正規化。extract_frustum_planes と同一）
    camera_pos:        vec4<f32>,           // xyz = カメラ位置
    instance_count:    u32,                 // 可視インスタンス数（LOD0）
    meshlet_count:     u32,                 // このプリミティブのメッシュレット数
    // 法線コーン背面棄却を行うか（CONE_CULL_ENABLED = 行う / それ以外 = 行わない）。
    // 背面カリング（CullFace::Back）マテリアルでのみ 1 が入る。両面描画（None）・
    // 前面カリング（Front）では背面が可視になるため、コーン棄却は必ず無効化する。
    cone_cull_enabled: u32,
    _pad0:             u32,
}

@group(0) @binding(0) var<storage, read>       instances:  array<InstanceData>;
@group(0) @binding(1) var<storage, read>       meshlets:   array<Meshlet>;
@group(0) @binding(2) var<storage, read_write> draw_cmds:  array<DrawIndexedIndirect>;
@group(0) @binding(3) var<storage, read_write> draw_count: atomic<u32>;
@group(0) @binding(4) var<uniform>             params:     CullParams;

// 境界球半径に足す保守マージン（棄却しすぎ防止）。
const SPHERE_MARGIN: f32 = 0.01;
// コーン棄却の保守マージン（cutoff に足して発火を弱める）。
const CONE_MARGIN:   f32 = 0.02;
// params.cone_cull_enabled がこの値のときのみ法線コーン背面棄却を行う
// （gpu_resources::CONE_CULL_ON と一致させること）。
const CONE_CULL_ENABLED: u32 = 1u;

@compute @workgroup_size(64)
fn cs_main(@builtin(global_invocation_id) gid: vec3<u32>) {
    let n_inst = params.instance_count;
    let n_ml   = params.meshlet_count;
    let total  = n_inst * n_ml;
    let t      = gid.x;
    if t >= total { return; }

    // スレッド t → (インスタンス, メッシュレット)
    let inst_idx = t / n_ml;
    let ml_idx   = t % n_ml;

    let inst = instances[inst_idx];
    let ml   = meshlets[ml_idx];

    // ── 境界球をワールド空間へ ────────────────────────────────
    // 中心はモデル行列で変換（頂点シェーダと同一の M*v 規約）。
    let center_w = (inst.model * vec4<f32>(ml.center, 1.0)).xyz;
    // 半径は基底ベクトル長の最大（非一様スケール/シアーに対して保守的な過大評価）。
    let bx = (inst.model * vec4<f32>(1.0, 0.0, 0.0, 0.0)).xyz;
    let by = (inst.model * vec4<f32>(0.0, 1.0, 0.0, 0.0)).xyz;
    let bz = (inst.model * vec4<f32>(0.0, 0.0, 1.0, 0.0)).xyz;
    let max_scale = max(length(bx), max(length(by), length(bz)));
    let radius_w  = ml.radius * max_scale + SPHERE_MARGIN;

    // ── 視錐台テスト（保守的: 球が完全に外側のときのみ棄却）────
    var visible = true;
    for (var i = 0u; i < 6u; i++) {
        let pl = params.planes[i];
        let nl = length(pl.xyz);
        // 未正規化平面の符号付き距離。nl==0 の異常時はスキップ（棄却しない＝保守側）。
        if nl > 1e-6 {
            let sd = (dot(pl.xyz, center_w) + pl.w) / nl;
            if sd < -radius_w {
                visible = false;
                break;
            }
        }
    }

    // ── 法線コーン背面棄却（保守側）──────────────────────────
    // 【前提】この棄却は「背面が描画されない（背面カリング有効）」ことに依存する最適化。
    // 両面描画（CullFace::None）・前面カリング（Front）のマテリアルでは背面も可視なので、
    // cone_cull_enabled = 0 が渡され、ここは丸ごとスキップされる（視錐台テストのみ効く）。
    // cone_cutoff が有効（<1）なメッシュレットのみ対象。法線は normal_matrix で変換。
    if visible && params.cone_cull_enabled == CONE_CULL_ENABLED && ml.cone_cutoff < 0.999 {
        let axis_w = normalize((inst.normal_matrix * vec4<f32>(ml.cone_axis, 0.0)).xyz);
        let to_c   = center_w - params.camera_pos.xyz;
        let dist   = length(to_c);
        if dist > 1e-4 {
            // meshopt の球ベース背面式（apex 不要）:
            //   dot(center-cam, axis) >= cutoff*dist + radius  → 完全背面 → 棄却可
            // 保守化のため RHS に CONE_MARGIN を足して発火を弱める。
            let lhs = dot(to_c, axis_w);
            let rhs = ml.cone_cutoff * dist + radius_w + CONE_MARGIN;
            if lhs >= rhs {
                visible = false;
            }
        }
    }

    if !visible { return; }

    // ── 生存: DrawIndexedIndirect を先頭から詰める ──────────────
    let slot = atomicAdd(&draw_count, 1u);
    draw_cmds[slot] = DrawIndexedIndirect(
        ml.index_count,   // index_count
        1u,               // instance_count
        ml.index_offset,  // first_index（meshlet_index_buffer 内オフセット）
        0i,               // base_vertex
        inst_idx,         // first_instance → @builtin(instance_index)
    );
}
