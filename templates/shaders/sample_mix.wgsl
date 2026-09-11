// @shading_contract 1
// ============================================================
// sample_mix.wgsl — 既定＋複数シェーディングモデルを使い分けるサンプル
//
// このファイルを差すと、まず全表面（地形・草含む）がハーフランバートになり、
// マテリアルの「シェーディングモデル」の値で例外的に別の見た目へ上書きできる:
//   （未設定/0 = ハーフランバート ← shade_default）
//   1 = シンプルトゥーン（2 階調）
//   2 = エンジン標準 PBR（写実に戻したいオブジェクト用）
//   3 = ポスタライズカラー（色数を落としたアート調）
// ============================================================

// ── 既定: ハーフランバート ────────────────────────────────

/// 全体の既定シェーディング: ハーフランバート（折り返し 0.5 固定）。
fn shade_default(sf: ShadingSurface, li: LightSample) -> vec3<f32> {
    let wrap = shading_saturate(dot(sf.normal, li.direction) * 0.5 + 0.5);
    return sf.base_color * (wrap * wrap) * li.color;
}

// ── モデル 1: シンプルトゥーン ─────────────────────────────

/// 明暗 2 階調のしきい値。
const MIX_TOON_THRESHOLD: f32 = 0.35;
/// 影側の明るさ。
const MIX_TOON_SHADOW: f32 = 0.25;

/// シェーディングモデル 1: 2 階調トゥーン。
fn shade_model_1(sf: ShadingSurface, li: LightSample) -> vec3<f32> {
    let ndl = shading_saturate(dot(sf.normal, li.direction));
    let lit = select(MIX_TOON_SHADOW, 1.0, ndl > MIX_TOON_THRESHOLD);
    return sf.base_color * lit * li.color;
}

// ── モデル 2: エンジン標準 PBR ─────────────────────────────

/// シェーディングモデル 2: 標準 PBR へ戻す（写実で見せたい例外オブジェクト用）。
fn shade_model_2(sf: ShadingSurface, li: LightSample) -> vec3<f32> {
    return shade_model_0(sf, li);
}

// ── モデル 3: ポスタライズカラー ───────────────────────────

/// 拡散の階調数。
const MIX_POSTER_STEPS: f32 = 4.0;

/// シェーディングモデル 3: ライティング結果の明るさを段階化して色数を落とす。
fn shade_model_3(sf: ShadingSurface, li: LightSample) -> vec3<f32> {
    let ndl    = shading_saturate(dot(sf.normal, li.direction));
    let banded = shading_posterize(ndl, MIX_POSTER_STEPS) + 0.5 / MIX_POSTER_STEPS;
    return sf.base_color * shading_saturate(banded) * li.color;
}
