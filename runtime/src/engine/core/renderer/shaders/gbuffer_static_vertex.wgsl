// ============================================================
// gbuffer_static_vertex.wgsl — G-Buffer 専用 スタティックメッシュ頂点シェーダ
//
// ## shader_static_vertex.wgsl との違いは 1 点だけ
// 「前フレームのモデル行列（group 4 の `u_prev_instances`）で同じローカル頂点を
//   もう一度ワールド→クリップへ通し、`prev_clip` を実値で埋める」。
// それ以外（ワールド変換・法線/接線・UV・頂点カラー・拡張スロットのタグ）は
// 完全に同一である。
//
// ## なぜファイルを分けたのか（設計判断）
// `u_prev_instances` は group 4 binding 0 を占有する。フォワード系（mesh/skinned/
// transparent）は group 4 をライトで使っているため、共有の頂点シェーダから
// 参照すると全フォワードパイプラインのリフレクションに速度用バインディングが
// 混入して衝突する。頂点シェーダ側を 2 本に分けるのが、バインディング境界を
// 汚さずに済む唯一の素直な分け方。
//
// ## 静的ジオメトリの速度が「二重計算」にならない理由
// 静止インスタンスでは Rust 側が `prev_model == model` を詰めるため、
// 本シェーダの再投影はそのまま「カメラ由来の速度」に縮退する。
// 深度から再投影するフルスクリーンパスを別に持つ必要が無く（＝動的物が
// 書いた画素を後から塗り潰す危険も無く）、静的・動的が 1 本の式で完結する。
//
// ## 依存
//   shader_common.wgsl   … CameraUniform / ModelUniform / VertexOutput
//   velocity_common.wgsl … PrevModelUniform / u_prev_instances
// ============================================================

struct VertexInput {
    @location(0) position: vec3<f32>,
    @location(1) normal:   vec3<f32>,
    @location(2) tangent:  vec4<f32>,
    @location(3) uv0:      vec2<f32>,
    @location(4) uv1:      vec2<f32>,
    @location(5) color:    vec4<f32>,
}

@vertex
fn vs_main(v: VertexInput, @builtin(instance_index) inst_idx: u32) -> VertexOutput {
    // inst_idx は DrawIndexedIndirect.first_instance 経由でコンピュートシェーダが
    // 書き込んだ実インスタンス番号（u_instances / u_prev_instances 共通の直接インデックス）。
    let u_model    = u_instances[inst_idx];
    let world_pos4 = u_model.model * vec4<f32>(v.position, 1.0);
    let nm         = u_model.normal_matrix;
    let wn         = normalize((nm * vec4<f32>(v.normal,      0.0)).xyz);
    let wt         = normalize((nm * vec4<f32>(v.tangent.xyz, 0.0)).xyz);
    let wbt        = normalize(cross(wn, wt) * v.tangent.w);

    // ── 前フレームのワールド座標（同じローカル頂点・前フレームのモデル行列）──
    //   前フレームが無いインスタンス（初回・新規可視・バッチ再生成）では
    //   Rust 側が prev_model = model を詰めているため、ここは自動的に
    //   「今フレームのワールド座標を前フレームのカメラで見た位置」になる。
    let prev_world_pos4 = u_prev_instances[inst_idx].prev_model * vec4<f32>(v.position, 1.0);

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
    out.render_tag   = instance_render_tag(u_model);
    out.curr_clip    = clip;
    out.prev_clip    = u_camera.prev_view_proj * prev_world_pos4;
    return out;
}
