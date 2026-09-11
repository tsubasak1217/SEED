// @water_shading_contract 1
// ============================================================
//  magma.wgsl — マグマ（溶岩）の水面シェーディングアセット
//
//  ## 使い方
//  WaterVolume の「水面シェーダ」にこのファイルを指定するだけ。
//  形状（波・流れ・波紋）はインスペクタの通常の水パラメータで作り、
//  **色だけ**をこのアセットが決める。
//
//  ## 見た目の作り
//    ・深いほど発光する         … 厚み（thickness）が大きいほど橙 → 黄白へ。表層は黒い殻。
//    ・亀裂の模様               … fBm ノイズを流速で流し、殻の割れ目から下の熱が覗く。
//    ・泡 → 焦げ ＋ 火の粉      … 標準の泡量（岸フォーム・航跡・岸波）を
//                                 「白い泡」ではなく「黒い焦げ」と「明滅する火の粉」に読み替える。
//    ・反射は弱め               … 溶岩は粗い黒体に近く、鏡のようには映らない。
//
//  ## 推奨するシーン側の設定（物性はアセットの担当外）
//    粘度 viscosity        = 0.85   （波がゆっくり進み、波紋がすぐ減衰する）
//    波紋減衰 ripple_damping = 0.9
//    波振幅 wave_amplitude = 0.15   （うねりは小さく、うねうねと）
//    波速度 wave_speed     = 0.2
//    吸収距離 absorption_distance = 0.6（すぐ深場の色に到達する＝底が見えない）
//    深場色 deep_color     = ほぼ黒 (0.02, 0.005, 0.0)
//    浅場色 shallow_color  = 暗い赤 (0.25, 0.05, 0.01)
//    反射強度 reflection_intensity = 0.15
//    反射粗さ reflection_roughness = 0.8
//
//  ## インスペクタから触れるパラメータ（Phase W8.2）
//  下の `override` 宣言が、この水域の「水面シェーダ」欄の直下に UI 行として出る。
//  値は水域ごと（＝アクタごと）に持てるので、同じ magma.wgsl を差した 2 つの池を
//  別の色・別の流速にできる。ここに書いた値はその**既定値**である
//  （`@reset` を付けた行には「デフォルトに戻す」ボタンが出る）。
// ============================================================

// ─── インスペクタへ出すパラメータ ────────────────────────────
// 宣言した名前は、この下のコードで**そのまま値として**読める
// （エンジンが値の受け渡しコードを生成する。詳細は docs/water_shading_asset.md）。

@color @reset
override emission_color: vec3<f32> = vec3(3.2, 0.75, 0.08);  // 溶岩の発光色
@ref
override glow_boost: f32 = 1.0;                              // 発光の強さ
@range(0.0, 1.0) @reset
override crack_speed: f32 = 0.05;                            // 亀裂の流れる速さ(m/s)
@range(0.0, 1.0) @reset
override crack_open: f32 = 0.85;                             // 亀裂の開き具合

// ─── 発光のパラメータ ────────────────────────────────────────

/// 発光が最大に達する厚み [m]。これより深い所は一様に最も明るい。
const MAGMA_GLOW_DEPTH: f32 = 3.0;
/// 発光カーブの鋭さ。1 で線形、大きいほど「浅い所は黒く、深い所だけ急に光る」。
const MAGMA_GLOW_CURVE: f32 = 2.2;
/// 最も冷えた表層の色（黒い殻）。
const MAGMA_CRUST_COLOR: vec3<f32> = vec3<f32>(0.03, 0.012, 0.008);
// 中温の色（橙）はパラメータ `emission_color`（インスペクタで変えられる）。
/// 最高温の色（黄白）。亀裂の底で覗く色。
const MAGMA_CORE_COLOR: vec3<f32> = vec3<f32>(6.0, 3.0, 0.9);

// ─── 亀裂（殻の割れ目）のパラメータ ──────────────────────────

/// 亀裂ノイズの空間周波数 [1/m]。大きいほど模様が細かい。
const MAGMA_CRACK_SCALE: f32 = 0.35;
/// 亀裂とみなすノイズ値のしきい値（fBm は 0..1 付近に分布する）。
const MAGMA_CRACK_THRESHOLD: f32 = 0.46;
/// しきい値から完全な亀裂になるまでの幅（境界を滑らかにする）。
const MAGMA_CRACK_SOFTNESS: f32 = 0.09;
// 亀裂が殻をどれだけ「開く」か（0..1）はパラメータ `crack_open`。
// 流れが無い水域でも模様を動かす基準スクロール速度 [m/s] はパラメータ `crack_speed`。

// ─── 泡の読み替え（焦げ＋火の粉）────────────────────────────

/// 泡の位置に乗る焦げの色（岸際・航跡が黒く固まる）。
const MAGMA_CHAR_COLOR: vec3<f32> = vec3<f32>(0.015, 0.008, 0.006);
/// 焦げが完全に覆うまでの泡量（これ以上は変わらない）。
const MAGMA_CHAR_FULL: f32 = 0.6;
/// 火の粉の色（明滅する明るい点）。
const MAGMA_EMBER_COLOR: vec3<f32> = vec3<f32>(8.0, 3.4, 0.6);
/// 火の粉の空間周波数 [1/m]。焦げより細かい粒にする。
const MAGMA_EMBER_SCALE: f32 = 2.5;
/// 火の粉とみなすノイズ値のしきい値（高いほど粒がまばら）。
const MAGMA_EMBER_THRESHOLD: f32 = 0.82;
/// 火の粉の明滅の速さ [rad/s]。
const MAGMA_EMBER_FLICKER_SPEED: f32 = 7.0;
/// 火の粉の最大強度。
const MAGMA_EMBER_STRENGTH: f32 = 0.5;

// ─── 反射のパラメータ ────────────────────────────────────────

/// 反射の減衰倍率。溶岩は粗い黒体なので、シーンの反射強度をさらに落とす。
const MAGMA_REFLECTION_SCALE: f32 = 0.25;
/// 反射を評価するフレネル指数（溶岩は縁でしか映らない）。
const MAGMA_FRESNEL_POWER: f32 = 6.0;

// ============================================================
//  実装
// ============================================================

/// 表層の温度分布（0 = 冷えた殻 / 1 = 最高温）を返す。
///
/// 厚みによる基礎温度に、亀裂ノイズによる「殻の割れ」を足したもの。
/// 亀裂は流速方向へゆっくり流れるので、川に指定すれば下流へ流れる溶岩になる。
fn magma_heat(input: WaterShadeInput) -> f32 {
    // ── ① 厚みによる基礎温度 ────────────────────────────────
    //    深い所ほど熱い（＝下から熱が伝わる）。カーブで「浅い縁は黒い」を強調する。
    let depth_t   = water_shade_saturate(input.thickness / MAGMA_GLOW_DEPTH);
    let base_heat = pow(depth_t, MAGMA_GLOW_CURVE);

    // ── ② 亀裂ノイズ ────────────────────────────────────────
    //    流速がある水域（川）は流速で、無い水域（池）は一定方向の微速で流す。
    //    どちらも時間で動くので、静止した溶岩に見えない。
    let drift = input.flow - vec2<f32>(0.0, crack_speed);
    let uv    = (input.world_pos.xz - drift * input.time) * MAGMA_CRACK_SCALE;
    let n     = water_shade_fbm(uv);
    // しきい値を下回った所（＝ノイズの谷）が亀裂。境界は smoothstep で滑らかにする。
    let crack = 1.0 - smoothstep(
        MAGMA_CRACK_THRESHOLD - MAGMA_CRACK_SOFTNESS,
        MAGMA_CRACK_THRESHOLD + MAGMA_CRACK_SOFTNESS,
        n);

    // ── ③ 合成 ──────────────────────────────────────────────
    //    亀裂は「基礎温度と 1.0 の間を開く」形で効かせる。
    //    こうすると浅い縁では亀裂があっても控えめにしか光らず、
    //    深い所ほど亀裂が明確な熱の筋になる（自然な見え方）。
    return water_shade_saturate(mix(base_heat, 1.0, crack * crack_open * depth_t));
}

/// 火の粉（明滅する明るい粒）の強度を返す。泡のある所にだけ出す。
fn magma_ember(input: WaterShadeInput, foam_total: f32) -> f32 {
    if (foam_total <= 0.0) {
        return 0.0;
    }
    // 粒のパターン。焦げより細かく、ゆっくり流れる。
    let uv = (input.world_pos.xz - input.flow * input.time) * MAGMA_EMBER_SCALE;
    let n  = water_shade_value_noise(uv);
    let grain = smoothstep(MAGMA_EMBER_THRESHOLD, 1.0, n);
    // 粒ごとに位相をずらして明滅させる（同じ位置なら同じ位相＝ちらつかない）。
    let phase   = n * 6.2831853;
    let flicker = 0.5 + 0.5 * sin(input.time * MAGMA_EMBER_FLICKER_SPEED + phase);
    return grain * flicker * water_shade_saturate(foam_total) * MAGMA_EMBER_STRENGTH;
}

/// 水面 1 ピクセルの最終色を決める（契約関数）。
fn water_shade(input: WaterShadeInput) -> vec4<f32> {
    // ── ① 温度 → 色 ────────────────────────────────────────
    //    殻 → 橙 → 黄白 の 2 段補間。中間点を 0.5 にせず 0.6 に寄せることで、
    //    「大半は暗い殻で、熱い所だけが局所的に眩しい」という溶岩らしい分布になる。
    //    橙の段（`emission_color`）と全体の明るさ（`glow_boost`）はインスペクタで変えられる。
    let heat = magma_heat(input);
    var color = mix(MAGMA_CRUST_COLOR, emission_color, water_shade_saturate(heat / 0.6));
    color = mix(color, MAGMA_CORE_COLOR,
                water_shade_saturate((heat - 0.6) / 0.4));
    //    発光の強さ。殻（冷えた表層）まで一様に明るくならないよう、
    //    温度で重み付けしてから掛ける（0 にしても黒い殻の質感は残る）。
    color = color * mix(1.0, glow_boost, heat);

    // ── ② 泡 → 焦げ ────────────────────────────────────────
    //    標準の泡量（岸フォーム・航跡・岸波）は「かき混ぜられて表面が冷えた所」と読み替える。
    //    白を足すのではなく、焦げ色へ寄せる（＝暗くする）のが要点。
    let foam_total = input.foam + input.ripple_foam + input.shore_foam;
    let char_amount = water_shade_saturate(foam_total / MAGMA_CHAR_FULL);
    color = mix(color, MAGMA_CHAR_COLOR, char_amount);

    // ── ③ 泡 → 火の粉 ──────────────────────────────────────
    //    冷えた殻の上で、割れた所から火の粉が飛ぶ。加算で乗せる。
    color = color + MAGMA_EMBER_COLOR * magma_ember(input, foam_total);

    // ── ④ 反射（弱め）──────────────────────────────────────
    //    溶岩は粗い黒体なので鏡像はほとんど出ない。それでも縁で環境が薄く乗ると
    //    「表面がある」ことが伝わるので、強く減衰させたうえで残す。
    let fresnel = water_shade_fresnel(
        input.normal, input.view_dir, MAGMA_FRESNEL_POWER, input.fresnel_strength);
    color = mix(color, input.reflection_color,
                fresnel * input.reflection_strength * MAGMA_REFLECTION_SCALE);

    // 溶岩は不透明（背景＝屈折グラブは一切使わない）。
    return vec4<f32>(color, 1.0);
}
