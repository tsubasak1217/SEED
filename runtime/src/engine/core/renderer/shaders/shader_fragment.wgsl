// ============================================================
// shader_fragment.wgsl  —  PBR フラグメントエントリ（薄いラッパ）
//
// ## 構成（Phase: Deferred + Clustered Lighting への準備分割）
// 旧 shade_pbr は「マテリアル採取」と「ライト評価」を 1 関数で行っていた。
// 将来「不透明 = Deferred（G-Buffer）＋ Clustered Lighting / 半透明 = Forward」へ
// 移行するとライト評価が 2 箇所（G-Buffer ライティングパスとフォワード半透明パス）に
// 二重化し、BRDF・ライト種別・影方式を変えるたび同期が必要になる。それを避けるため、
// 責務を 3 段へ分割した:
//
//   1. surface.wgsl        : Surface 構造体（面の情報。G-Buffer に焼く内容とほぼ一致）
//   2. surface_gather.wgsl : gather_surface(VertexOutput, front_facing) -> Surface
//                            （マテリアル採取。Mask の discard もここ。dpdx/dpdy 使用＝
//                              フラグメント専用）
//   3. lighting_eval.wgsl  : evaluate_lighting(Surface) -> vec3<f32>
//                            （アンビエント＋ライトループ＋影＋PBR。VertexOutput 非依存）
//
// 本ファイルはその 3 段を束ねるエントリポイント層のみを持つ。
// shade_pbr は gather_surface → evaluate_lighting を呼ぶだけのラッパであり、
// シグネチャは従来どおり（shader_wboit.wgsl の fs_wboit がそのまま呼ぶ）。
// ============================================================

/// PBR ライティングを評価して HDR カラー（rgb）＋アルファ（a）を返すラッパ。
///
/// fs_main（不透明・Mask パス）と shader_wboit.wgsl の fs_wboit（透明 WBOIT パス）が
/// 同一のライティングを共有するために本体を切り出してある（Phase R5）。
/// Mask モードのアルファテスト（discard）は gather_surface 内で行うため、
/// 両パスでカットオフ挙動が一致する。alpha_cutoff は非 Mask マテリアルでは 0.0 のため
/// Opaque/Blend は影響を受けない（GpuMaterial::upload 参照）。
///
/// `front_facing` は `@builtin(front_facing)`（各エントリポイントが受け取る）。
/// 裏面での法線反転（N・Ng とも）は gather_surface が行う。
///
/// 出力はリニア HDR 色（トーンマップ・ガンマ補正なし）。HDR オフスクリーン
/// （Rgba16Float, renderer::HDR_FORMAT）へ書き、トーンマップはフルスクリーンの
/// post_tonemap.wgsl が一元的に行う（Phase R3）。
fn shade_pbr(in: VertexOutput, front_facing: bool) -> vec4<f32> {
    // 1) マテリアル採取（テクスチャ＋補間属性 → Surface）。Mask の discard もここで発火。
    let surface = gather_surface(in, front_facing);
    // 2) ライト評価（Surface のみを入力。将来の G-Buffer ライティングパスと共有する本体）。
    let hdr_color = evaluate_lighting(surface);
    return vec4<f32>(hdr_color, surface.alpha);
}

// ── 不透明／Mask パスのフラグメントエントリ ──────────────────
// ライティング本体は shade_pbr（→ gather_surface / evaluate_lighting）に集約済み。
// 単一カラーターゲット（@location(0)）へ HDR 色＋アルファを出力する。
//
// `@builtin(front_facing)` は「このフラグメントが三角形の表面（CCW 面）由来か」を示す。
// 両面描画（cull_mode = None）・前面カリング（Front）のパイプラインでのみ false が来る。
// 頂点シェーダ出力（VertexOutput）の変更は不要（builtin はフラグメント入力に直接足せる）。
@fragment
fn fs_main(in: VertexOutput, @builtin(front_facing) front_facing: bool) -> @location(0) vec4<f32> {
    return shade_pbr(in, front_facing);
}
