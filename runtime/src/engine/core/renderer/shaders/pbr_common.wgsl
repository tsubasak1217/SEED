// ============================================================
// pbr_common.wgsl  —  Cook-Torrance PBR ヘルパー関数（バインディングなし共有定義）
//
// ## 役割（単一責任）
// distribution_ggx / geometry_smith / fresnel_schlick 等、BRDF 計算に使う純関数
// （入出力ともにバインディングを一切参照しない）だけを持つ。
// 旧 shader_common.wgsl から移設した（Phase D3: G-Buffer デファード化 Phase A）。
//
// なぜ分離するか:
//   evaluate_lighting（lighting_eval.wgsl）はこれらのヘルパーに依存するが、デファードの
//   ライティングパス（deferred_lighting.wgsl）は shader_common.wgsl を連結しない
//   （group2 のマテリアルバインディングを巻き込まないため。light_common.wgsl 分離と同じ理由）。
//   そのため PBR ヘルパーも「バインディングを持たない共有定義ファイル」として独立させ、
//   shader_common.wgsl 経由のフォワード系パイプラインとデファードのライティングパスの
//   両方から連結できるようにする（cluster_common.wgsl と同じ設計パターン）。
//
// ## 連結順序
// バインディング・他の型に依存しない純関数のみのため、連結順は他の共有定義に対して
// 依存しない（先頭付近であればどこでもよい）。既存 TOML では shader_common.wgsl の
// 直前に置く（["cluster_common.wgsl", "pbr_common.wgsl", "shader_common.wgsl", ...]）。
// ============================================================

const PI: f32 = 3.14159265359;

/// GGX（Trowbridge-Reitz）法線分布関数。ラフネスに応じたハイライトの広がりを決める。
fn distribution_ggx(N: vec3<f32>, H: vec3<f32>, roughness: f32) -> f32 {
    let a  = roughness * roughness;
    let a2 = a * a;
    let ndh = max(dot(N, H), 0.0);
    let d   = ndh * ndh * (a2 - 1.0) + 1.0;
    return a2 / (PI * d * d);
}

/// Schlick-GGX 幾何減衰項（単一方向ぶん）。geometry_smith から V・L それぞれに適用される。
fn geometry_schlick_ggx(ndv: f32, roughness: f32) -> f32 {
    let r = roughness + 1.0;
    let k = (r * r) / 8.0;
    return ndv / (ndv * (1.0 - k) + k);
}

/// Smith 法による幾何減衰項（視線方向・光源方向の両方の自己遮蔽を考慮）。
fn geometry_smith(N: vec3<f32>, V: vec3<f32>, L: vec3<f32>, roughness: f32) -> f32 {
    let ndv = max(dot(N, V), 0.0);
    let ndl = max(dot(N, L), 0.0);
    return geometry_schlick_ggx(ndv, roughness) * geometry_schlick_ggx(ndl, roughness);
}

/// Fresnel-Schlick 近似（視線角に応じた反射率の変化）。
fn fresnel_schlick(cos_theta: f32, F0: vec3<f32>) -> vec3<f32> {
    return F0 + (1.0 - F0) * pow(clamp(1.0 - cos_theta, 0.0, 1.0), 5.0);
}
