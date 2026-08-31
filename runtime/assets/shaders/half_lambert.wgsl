// @shading_contract 1
// ============================================================
// half_lambert.wgsl — ハーフランバート（全体の既定シェーディング）
//
// このファイルをカメラまたはシーンの「Shading」に差すだけで、シェーディングモデルを
// 設定していない全表面（地形・草・全マテリアル）が柔らかい陰影で描かれる。
// マテリアル側の設定は不要。
//
// N·L を [0,1] へ折り返してから二乗する Valve 由来の古典手法。
// 影側が真っ黒にならず、キャラクタや地形がふんわり見えるのが特徴。
// ============================================================

/// 折り返し量（0.5 = 標準のハーフランバート。小さくするほど通常のランバートへ近づく）。
const HL_WRAP: f32 = 0.5;
/// スペキュラの鋭さ。ラフネスから求める指数の上限。
const HL_SPECULAR_POWER_MAX: f32 = 64.0;
/// スペキュラ強度（金属寄りのマテリアルほど強く乗せる係数の底上げ）。
const HL_SPECULAR_BASE: f32 = 0.08;

/// 全体の既定シェーディング: ハーフランバート。
fn shade_default(sf: ShadingSurface, li: LightSample) -> vec3<f32> {
    let N = sf.normal;
    let V = sf.view_dir;
    let L = li.direction;

    // ── 拡散: N·L を折り返してから二乗（暗部が持ち上がり階調が柔らかくなる）──
    let ndl  = dot(N, L);
    let wrap = shading_saturate(ndl * HL_WRAP + (1.0 - HL_WRAP));
    let diffuse_term = wrap * wrap;

    // ── スペキュラ: Blinn-Phong。鋭さはラフネスから導出 ──────────────────
    // ラフネス 0 → 最も鋭い、1 → ほぼ消える。金属は少し強めに乗せる。
    let H     = normalize(V + L);
    let ndh   = shading_saturate(dot(N, H));
    let power = mix(HL_SPECULAR_POWER_MAX, 1.0, shading_saturate(sf.roughness));
    let spec  = pow(ndh, power) * (HL_SPECULAR_BASE + sf.metallic * (1.0 - HL_SPECULAR_BASE));

    // li.color は減衰・円錐・影まで織り込み済み（再計算禁止）。
    return (sf.base_color * diffuse_term + vec3<f32>(spec)) * li.color;
}

/// シェーディングモデル 1: 例外オブジェクト用（標準 PBR へ戻す枠）。
/// 「世界は柔らかいが、このプロップだけ写実的にしたい」用途。
fn shade_model_1(sf: ShadingSurface, li: LightSample) -> vec3<f32> {
    return shade_model_0(sf, li);
}
