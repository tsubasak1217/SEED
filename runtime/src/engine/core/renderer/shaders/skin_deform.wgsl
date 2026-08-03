// ============================================================
//  skin_deform.wgsl — RT 用「変形後ローカル頂点位置」書き出し compute
//
//  【このシェーダが存在する理由】
//    本エンジンの GPU スキニングは skin_compute.wgsl が **ジョイント行列だけ** を
//    出力し、頂点の実変形は頂点シェーダ（shader_skinned_vertex.wgsl）が毎描画で
//    行う。つまり「変形後の頂点位置」はどこにも実体として存在しない。
//    レイトレーシングの BLAS は実体としての頂点バッファを要求するため、
//    このシェーダが VS とまったく同じ式で変形後位置を storage へ書き出す。
//
//  【空間の約束】
//    書き出すのは **ローカル空間**（= skin * vec4(position, 1)）である。
//    VS は続けて `u_model.model` を掛けてワールドへ移すが、RT 側は同じ行列を
//    TLAS インスタンス変換として与えるので、BLAS はローカルのままでよい。
//    これにより「同一モデルの複数インスタンス」でも BLAS の中身は
//    インスタンスごとに独立（ポーズが違うため）だが、変換の重複計算は避けられる。
//
//  【ディスパッチ単位】
//    1 ディスパッチ = 1 プリミティブ × 1 インスタンス。joint_base で
//    そのインスタンスのジョイント行列先頭（compact_idx * MAX_JOINTS）を指す。
// ============================================================

/// 1 インスタンスあたりのジョイント行列本数。
/// Rust 側 `skin_system::MAX_JOINTS` および shader_skinned_vertex.wgsl の同名定数と
/// 必ず一致させること（rt_skin_blas.rs のユニットテストが一致を検証する）。
const MAX_JOINTS: u32 = 128u;

/// `SkinVertex` の joints（[u16; 4]）が占めるワード数（u16 ×4 = u32 ×2）。
const JOINT_PACKED_WORDS: u32 = 2u;

/// u16 アンパック用の下位 16bit マスク。
const U16_MASK: u32 = 0xFFFFu;

/// u16 アンパック用の上位 16bit シフト量。
const U16_SHIFT: u32 = 16u;

/// ワークグループサイズ（1 スレッド = 1 頂点）。
const WORKGROUP_SIZE: u32 = 64u;

/// 重み総和がこの値未満なら「スキン情報なし」とみなし、バインドポーズへフォールバックする。
/// 総和 0 のまま合成すると零行列が掛かって頂点が原点へ潰れる（メッシュが消える）ため。
const MIN_WEIGHT_SUM: f32 = 1e-6;

/// 変形 compute のパラメータ（1 プリミティブ × 1 インスタンス分）。
struct SkinDeformParams {
    /// このプリミティブの頂点数（= out_positions の要素数）。
    vertex_count:       u32,
    /// ジョイント行列配列内でのこのインスタンスの先頭要素番号（compact_idx * MAX_JOINTS）。
    joint_base:         u32,
    /// `Vertex` 1 個のワード数（size_of::<Vertex>() / 4）。Rust 側から与える。
    vertex_stride_words: u32,
    /// `SkinVertex` 1 個のワード数（size_of::<SkinVertex>() / 4）。Rust 側から与える。
    skin_stride_words:  u32,
}

@group(0) @binding(0) var<uniform> params: SkinDeformParams;
/// 元頂点バッファ（`Vertex` の生バイト列を u32 配列として読む）。
@group(0) @binding(1) var<storage, read> src_vertices: array<u32>;
/// スキン頂点バッファ（`SkinVertex` の生バイト列を u32 配列として読む）。
@group(0) @binding(2) var<storage, read> src_skin: array<u32>;
/// 出力: 変形後ローカル位置（xyz、w=1.0）。BLAS の頂点バッファとして直接使う。
@group(0) @binding(3) var<storage, read_write> out_positions: array<vec4<f32>>;

/// ジョイント行列（列優先）。skin_compute.wgsl が書いた sk_jmats_lodN をそのまま読む。
@group(1) @binding(0) var<storage, read> joint_matrices: array<mat4x4<f32>>;

@compute @workgroup_size(WORKGROUP_SIZE)
fn cs_main(@builtin(global_invocation_id) gid: vec3<u32>) {
    let vi = gid.x;
    if (vi >= params.vertex_count) {
        return;
    }

    // ── 元位置の取り出し（Vertex の offset 0 に vec3<f32>）─────────
    let vbase = vi * params.vertex_stride_words;
    let pos = vec3<f32>(
        bitcast<f32>(src_vertices[vbase + 0u]),
        bitcast<f32>(src_vertices[vbase + 1u]),
        bitcast<f32>(src_vertices[vbase + 2u]),
    );

    // ── スキン属性の取り出し ─────────────────────────────────
    // SkinVertex = { joints: [u16;4]（2 ワードに packed）, weights: [f32;4] }
    let sbase = vi * params.skin_stride_words;
    let jw0 = src_skin[sbase + 0u];
    let jw1 = src_skin[sbase + 1u];
    // u16 → u32 アンパック（リトルエンディアン: 下位ワードが先の要素）。
    // MAX_JOINTS-1 でクランプするのは既存 VS（shader_skinned_vertex.wgsl）と同じ防御。
    let j = vec4<u32>(
        min(jw0 & U16_MASK,           MAX_JOINTS - 1u),
        min(jw0 >> U16_SHIFT,         MAX_JOINTS - 1u),
        min(jw1 & U16_MASK,           MAX_JOINTS - 1u),
        min(jw1 >> U16_SHIFT,         MAX_JOINTS - 1u),
    );
    let wbase = sbase + JOINT_PACKED_WORDS;
    let w = vec4<f32>(
        bitcast<f32>(src_skin[wbase + 0u]),
        bitcast<f32>(src_skin[wbase + 1u]),
        bitcast<f32>(src_skin[wbase + 2u]),
        bitcast<f32>(src_skin[wbase + 3u]),
    );

    // ── 重み総和 0 のフォールバック ───────────────────────────
    // スキン情報が欠けた頂点（インポート不備・非スキンノードの混在）で
    // 零行列を掛けると頂点が原点へ潰れ、BLAS が巨大な退化三角形の塊になる。
    // その場合はバインドポーズ（元位置）をそのまま書き出す。
    let wsum = w.x + w.y + w.z + w.w;
    if (wsum < MIN_WEIGHT_SUM) {
        out_positions[vi] = vec4<f32>(pos, 1.0);
        return;
    }

    // ── ブレンドスキニング（VS とまったく同じ式）────────────────
    let base = params.joint_base;
    let skin =
        w.x * joint_matrices[base + j.x] +
        w.y * joint_matrices[base + j.y] +
        w.z * joint_matrices[base + j.z] +
        w.w * joint_matrices[base + j.w];

    let local = skin * vec4<f32>(pos, 1.0);
    // BLAS は先頭 12 バイト（Float32x3）しか読まないが、ストライド 16B を保つため
    // w も明示的に 1.0 で埋める（未初期化バイトを残さない）。
    out_positions[vi] = vec4<f32>(local.xyz, 1.0);
}
