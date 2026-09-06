// ============================================================
//  primitive2d.wgsl — スクリプト 2D プリミティブ描画シェーダー
//
//  バインドグループなし（テクスチャもカメラ uniform も使わない）。
//  頂点は CPU 側（primitive2d/pass.rs）で NDC まで変換済みなので、
//  頂点シェーダーはそのまま clip space へ流すだけ。
//
//  アンチエイリアスは「輪郭の外側へ張った 1px のフェザー帯」で行う。
//  頂点属性 `edge` が図形内部で 1.0・帯の外縁で 0.0 になっており、
//  ラスタライザの線形補間がそのままカバレッジ（被覆率）の近似になる。
//  これにより図形種別ごとの解析 SDF を書かずに全図形を 1 本の
//  パイプラインで滑らかに描ける。
// ============================================================

// ── 定数（マジックナンバー禁止）────────────────────────────────

/// これ未満のアルファは描かない（ブレンド段のコストと帯の残像を避ける）。
const PRIM_ALPHA_EPSILON: f32 = 0.002;

// ── 頂点入出力 ────────────────────────────────────────────────

struct VertIn {
    /// NDC 座標（CPU で model × view_proj 済み）。
    @location(0) position : vec3<f32>,
    /// RGBA カラー（0..1・ストレートアルファ）。
    @location(1) color    : vec4<f32>,
    /// フェザー係数（1 = 図形内部 / 0 = 帯の外縁）。
    @location(2) edge     : f32,
}

struct VertOut {
    @builtin(position) clip_pos : vec4<f32>,
    @location(0)       color    : vec4<f32>,
    @location(1)       edge     : f32,
}

// ── 頂点シェーダー ────────────────────────────────────────────

@vertex
fn vs_main(in: VertIn) -> VertOut {
    var out : VertOut;
    // 入力は NDC（z も CPU で射影済み）。
    // 3D ワールドキャンバス上の図形は depth_compare=LessEqual のパイプラインで
    // 描かれるため、この z が 3D シーンとの前後関係を決める。
    // スクリーンスペース／2D キャンバス用のパイプラインは depth_compare=Always なので
    // z は無視される。
    out.clip_pos = vec4<f32>(in.position, 1.0);
    out.color    = in.color;
    out.edge     = in.edge;
    return out;
}

// ── フラグメントシェーダー ────────────────────────────────────

@fragment
fn fs_main(in: VertOut) -> @location(0) vec4<f32> {
    // 補間された被覆率をアルファへ掛ける。
    let coverage = clamp(in.edge, 0.0, 1.0);
    let a = in.color.a * coverage;
    if a < PRIM_ALPHA_EPSILON { discard; }
    return vec4<f32>(in.color.rgb, a);
}
