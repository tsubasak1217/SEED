// @shading_contract 1
// ============================================================
// toon.wgsl — 3 階調セルシェーディング + リムライト（全体の既定シェーディング）
//
// このファイルをカメラまたはシーンの「シェーディングアセット」に差すだけで、
// シェーディングモデルを設定していない全表面（地形・草・全マテリアル）が
// トゥーンで描かれる。マテリアル側の設定は不要。
//
// 例外的に「このモデルだけは標準 PBR のままにしたい」場合だけ、そのマテリアルの
// シェーディングモデルを 1 にして shade_model_1（下部）で上書きする。
//
// ── パラメータ ──────────────────────────────────────────────
// 下の `override` 宣言はインスペクタの行になる（カメラの「シェーディングアセット」行の
// 直下、またはシーン設定ウィンドウの「シェーダ」行の直下）。値はシーンに保存され、
// アセット側の既定値より優先される。既定値は**このファイル単体で見た目が完成する**よう、
// パラメータを一切触っていない状態の絵と一致させてある。
// ============================================================

/// 拡散光の階調数（3 階調＝影・中間・ハイライト）。
/// 大きくするほど滑らかに、小さくするほど強いセル画調になる。
@range(1.0, 8.0) @reset
override toon_steps: f32 = 3.0;                                  // 階調数

/// 影側に乗せる色かぶり。白（既定）なら色かぶり無し＝素の陰影。
/// 青寄りにすると空の反射を拾ったような、暖色にすると間接光の回り込みのような影になる。
@color @reset
override toon_shadow_tint: vec3<f32> = vec3(1.0, 1.0, 1.0);      // 影の色

/// 最も暗い階調でも完全な黒にしないための下限（環境光的な底上げ）。
@range(0.0, 1.0) @reset
override toon_shadow_floor: f32 = 0.15;                          // 影の明るさの下限

/// リムライト（輪郭光）の強さ。0 で無効。
@range(0.0, 2.0) @reset
override toon_rim_strength: f32 = 0.35;                          // リムライトの強さ

/// 全体の明るさ倍率。
/// `@ref` が付いているので、インスペクタでは値の入力欄ではなく**参照バインド行**になり、
/// シーン内のコンポーネントの変数（例: ライトの強度、スクリプトの [Bindable] フィールド）を
/// 毎フレーム流し込める。バインドしなければこの既定値が使われる。
@ref @reset
override toon_light_boost: f32 = 1.0;                            // 明るさ倍率

// ── パラメータにしない定数（画作りの骨格であり、触る必要が薄いもの）──
/// スペキュラを「乗るか乗らないか」の 2 値にするしきい値。
const TOON_SPECULAR_THRESHOLD: f32 = 0.6;
/// 2 値スペキュラの強さ。
const TOON_SPECULAR_STRENGTH: f32 = 0.7;
/// スペキュラの鋭さ（大きいほど小さく硬いハイライト）。
const TOON_SPECULAR_POWER: f32 = 48.0;
/// リムライトの立ち上がり（大きいほど輪郭が細くなる）。
const TOON_RIM_POWER: f32 = 3.0;

/// 全体の既定シェーディング: トゥーン。
/// シェーディングモデルを設定していない全表面（ID 0）がこれで描かれる。
fn shade_default(sf: ShadingSurface, li: LightSample) -> vec3<f32> {
    let N = sf.normal;
    let V = sf.view_dir;
    let L = li.direction;

    // ── 拡散: N·L を階調化する ─────────────────────────────
    let ndl  = shading_saturate(dot(N, L));
    // floor(x * steps) / steps は「段の下端」を返すため、段の中心へ寄せて明るさの目減りを防ぐ。
    let band = shading_posterize(ndl, toon_steps) + 0.5 / toon_steps;
    let diffuse_term = max(shading_saturate(band), toon_shadow_floor);

    // 影側ほど `toon_shadow_tint` を強く混ぜる（既定の白では恒等＝色かぶり無し）。
    let tint = mix(toon_shadow_tint, vec3<f32>(1.0), diffuse_term);

    // ── スペキュラ: Blinn-Phong を 2 値化する ───────────────
    let H    = normalize(V + L);
    let ndh  = shading_saturate(dot(N, H));
    let spec = pow(ndh, TOON_SPECULAR_POWER);
    let spec_term = select(0.0, TOON_SPECULAR_STRENGTH, spec > TOON_SPECULAR_THRESHOLD);

    // ── リムライト: 視線に対して立っている面ほど明るく ──────
    // 影の付いていない側に出ると不自然なので、ライト方向の寄与で抑える。
    let rim  = pow(1.0 - shading_saturate(dot(N, V)), TOON_RIM_POWER);
    let rim_term = rim * toon_rim_strength * ndl;

    // li.color は減衰・スポット円錐・影まで織り込み済み（再計算してはならない）。
    let lit = sf.base_color * diffuse_term * tint
            + vec3<f32>(spec_term)
            + vec3<f32>(rim_term);
    return lit * li.color * toon_light_boost;
}

/// シェーディングモデル 1: 例外オブジェクト用の上書き。
///
/// 「世界はトゥーンだが、このプロップだけは写実的に見せたい」というときのための枠。
/// マテリアルのシェーディングモデルを 1 にしたものだけがここへ来る。
/// エンジン標準 PBR は shade_model_0 として呼べるので、そのまま素通しする。
fn shade_model_1(sf: ShadingSurface, li: LightSample) -> vec3<f32> {
    return shade_model_0(sf, li);
}
