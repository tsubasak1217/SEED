// @water_shading_contract 1
// ============================================================
//  poison.wgsl — 毒沼の水面シェーディングアセット
//
//  ## 使い方
//  WaterVolume の「水面シェーダ」にこのファイルを指定するだけ。
//  形状（波・流れ・波紋）はインスペクタの通常の水パラメータで作り、
//  **色だけ**をこのアセットが決める。
//
//  ## 見た目の作り
//    ・毒々しい緑             … 浅場は黄緑、深場は濁った暗緑。強い吸収で底がすぐ見えなくなる。
//    ・粘つく艶               … 粘度（viscosity）に応じて、法線に沿った鋭いハイライトを乗せる。
//                               標準の水より狭く強い＝「ぬめっている」印象になる。
//    ・澱みの模様             … 低周波の fBm を薄く重ね、表面が均一でなくむらになる。
//    ・泡 → 泡立ちの白緑      … 標準の泡量を「毒の泡」に読み替え、白ではなく白緑にして
//                               さらに**膨らみ（明滅する粒）**を足す。
//
//  ## 推奨するシーン側の設定（物性はアセットの担当外）
//    粘度 viscosity          = 0.6   （艶の鋭さがこの値で変わる）
//    波紋減衰 ripple_damping = 0.75
//    波振幅 wave_amplitude   = 0.06  （粘って波立ちにくい）
//    波速度 wave_speed       = 0.35
//    吸収距離 absorption_distance = 0.5（強い吸収＝底が見えない）
//    深場色 deep_color       = 暗い緑 (0.02, 0.09, 0.03)
//    浅場色 shallow_color    = 黄緑   (0.25, 0.55, 0.12)
//    泡色 foam_color         = 白緑   (0.7, 0.95, 0.6)
//    反射強度 reflection_intensity = 0.5
//    反射粗さ reflection_roughness = 0.25
//
//  ## インスペクタから触れるパラメータ（Phase W8.2）
//  下の `override` 宣言が、この水域の「水面シェーダ」欄の直下に UI 行として出る。
//  値は水域ごとに持てるので、同じ poison.wgsl で「浅い毒だまり」と
//  「濃い毒沼」を作り分けられる。ここに書いた値はその**既定値**である
//  （`@reset` を付けた行には「デフォルトに戻す」ボタンが出る）。
//
//  `glow_color` はスクリプトからも動かせる（例: ボス HP に応じて蛍光を強める）。
//    waterVolume.SetShaderParam("glow_color", new Vector3(0.2f, 0.6f, 0.1f));
//
//  ## `@ref` — シーンの値をそのまま流し込む（Phase W8.3）
//  `@ref` を付けた宣言は、値を直接入力する代わりに「シーン内のどのアクタの
//  どの変数から貰うか」を指定する参照バインド行になる（値の入力欄自体は出ない）。
//  書式は他の属性と同じで、`@color` / `@range` / `@reset` と併用できる:
//
//    @ref
//    override player_speed: f32 = 0.0;   // 未バインド時は保存値、無ければこの既定値
//
//  下の `glow_boost` がその実例で、`[SerializeField, Bindable] float` を持つ
//  スクリプト（例: ボスの怒り度）をインスペクタでドロップして繋ぐと、
//  その実行中の値が毎フレーム蛍光の強さへ流れ込む。詳細は
//  `docs/water_shading_asset.md` の「3.6 `@ref` — シーンから値を流し込む」を参照。
// ============================================================

// ─── インスペクタへ出すパラメータ ────────────────────────────
// 宣言した名前は、この下のコードで**そのまま値として**読める。

@color @reset
override glow_color: vec3<f32> = vec3(0.10, 0.32, 0.05);  // 毒の蛍光色
@range(0.0, 4.0) @reset
override gloss_strength: f32 = 1.6;                       // 艶の強さ（ぬめり感）
@range(0.0, 1.0) @reset
override scum_strength: f32 = 0.35;                       // 澱みの濃さ
@ref @range(0.0, 4.0) @reset
override glow_boost: f32 = 1.0;                           // 蛍光の強さ倍率（外部バインド可）

// ─── 吸収・色のパラメータ ────────────────────────────────────

/// 吸収距離に掛ける倍率。1 未満で「標準の水より濃い」＝底がすぐ見えなくなる。
const POISON_ABSORPTION_SCALE: f32 = 0.55;
/// 深場での最大不透明度に掛ける倍率。1 に近いほど背景を完全に隠す。
const POISON_OPACITY_SCALE: f32 = 1.15;
// 表面に薄く乗る蛍光の色（毒々しさの正体。加算で乗る）はパラメータ `glow_color`。
/// 蛍光が最大になる厚み [m]。浅い所は素直な緑、深い所ほど不気味に光る。
const POISON_GLOW_DEPTH: f32 = 2.0;

// ─── 澱み（表面のむら）のパラメータ ──────────────────────────

/// 澱みノイズの空間周波数 [1/m]。低くして「大きなむら」にする。
const POISON_SCUM_SCALE: f32 = 0.12;
// 澱みが色をどれだけ濁らせるか（0..1）はパラメータ `scum_strength`。
/// 澱みの色（黄土がかった緑＝濁り）。
const POISON_SCUM_COLOR: vec3<f32> = vec3<f32>(0.18, 0.22, 0.06);
/// 流れが無い水域でも澱みが動くようにする基準スクロール速度 [m/s]。
/// 溶岩よりさらに遅い（ほとんど動かないが、完全な静止でもない）。
const POISON_DRIFT_SPEED: f32 = 0.02;

// ─── 粘つく艶（スペキュラ）のパラメータ ──────────────────────

/// 艶の鋭さの下限（粘度 0 のとき）。
const POISON_GLOSS_POWER_MIN: f32 = 24.0;
/// 艶の鋭さの上限（粘度 1 のとき）。粘るほどハイライトが狭く硬くなる。
const POISON_GLOSS_POWER_MAX: f32 = 180.0;
// 艶の強さ（HDR なので 1 を超えてよい）はパラメータ `gloss_strength`。
/// 艶の色（わずかに緑を帯びた白）。
const POISON_GLOSS_COLOR: vec3<f32> = vec3<f32>(0.85, 1.0, 0.8);
/// 艶を作るための仮想光源の向き（真上からやや手前。正規化して使う）。
///
/// 水面パスはライト配列を持たない（バインドグループが上限に達している）ため、
/// **アセット固有の固定光**で艶を作る。シーンのライトとは連動しない点に注意。
const POISON_GLOSS_LIGHT_DIR: vec3<f32> = vec3<f32>(0.35, 1.0, 0.25);

// ─── 泡立ちのパラメータ ──────────────────────────────────────

/// 泡の色を白緑へ寄せる混合率（0 = シーン設定の泡色そのまま）。
const POISON_BUBBLE_TINT: f32 = 0.6;
/// 泡立ちの白緑。
const POISON_BUBBLE_COLOR: vec3<f32> = vec3<f32>(0.65, 1.0, 0.55);
/// 泡の粒（膨らみ）の空間周波数 [1/m]。
const POISON_BUBBLE_SCALE: f32 = 3.5;
/// 泡の粒とみなすノイズ値のしきい値。
const POISON_BUBBLE_THRESHOLD: f32 = 0.7;
/// 泡が膨らんで弾ける周期の速さ [rad/s]。
const POISON_BUBBLE_SPEED: f32 = 2.2;
/// 泡の粒が既存の泡量へ上乗せする最大量。
const POISON_BUBBLE_STRENGTH: f32 = 0.45;

// ============================================================
//  実装
// ============================================================

/// 澱み（表面のむら）の量 0..1 を返す。低周波ノイズをゆっくり流したもの。
fn poison_scum(input: WaterShadeInput) -> f32 {
    // 流速がある水域（川）は流速で、無い水域（沼）は一定方向の微速で流す。
    let drift = input.flow + vec2<f32>(POISON_DRIFT_SPEED, POISON_DRIFT_SPEED * 0.6);
    let uv    = (input.world_pos.xz - drift * input.time) * POISON_SCUM_SCALE;
    return water_shade_saturate(water_shade_fbm(uv));
}

/// 粘つく艶（狭く強いスペキュラ）の強度を返す。
///
/// 粘度が高いほど指数が大きくなり、ハイライトが小さく硬くなる
/// ＝「水」ではなく「ぬめった液体」に見える。
fn poison_gloss(input: WaterShadeInput) -> f32 {
    let light = normalize(POISON_GLOSS_LIGHT_DIR);
    // Blinn-Phong のハーフベクトル。水面パスは実ライトを持たないので固定光で評価する。
    let h     = normalize(light + input.view_dir);
    let ndh   = water_shade_saturate(dot(input.normal, h));
    let power = mix(POISON_GLOSS_POWER_MIN, POISON_GLOSS_POWER_MAX,
                    water_shade_saturate(input.viscosity));
    return pow(ndh, power) * gloss_strength;
}

/// 泡の粒（膨らんで弾ける泡立ち）の量を返す。泡のある所にだけ出す。
fn poison_bubbles(input: WaterShadeInput, foam_total: f32) -> f32 {
    if (foam_total <= 0.0) {
        return 0.0;
    }
    let uv = (input.world_pos.xz - input.flow * input.time) * POISON_BUBBLE_SCALE;
    let n  = water_shade_value_noise(uv);
    let grain = smoothstep(POISON_BUBBLE_THRESHOLD, 1.0, n);
    // 粒ごとに位相をずらし、膨らむ → 弾ける を繰り返させる。
    let phase = n * 6.2831853;
    let swell = 0.5 + 0.5 * sin(input.time * POISON_BUBBLE_SPEED + phase);
    return grain * swell * water_shade_saturate(foam_total) * POISON_BUBBLE_STRENGTH;
}

/// 水面 1 ピクセルの最終色を決める（契約関数）。
fn water_shade(input: WaterShadeInput) -> vec4<f32> {
    // ── ① 強い吸収 ────────────────────────────────────────
    //    標準の吸収式を、吸収距離を縮めて（＝濃くして）使う。
    //    契約の `water_shade_absorption` を流用するので、
    //    「標準の水と同じ理屈で、濃さだけが違う」ことが保証される。
    let absorb = water_shade_absorption(
        input.thickness, input.absorption_distance * POISON_ABSORPTION_SCALE);
    var tint = mix(input.deep_color, input.shallow_color, absorb);

    // ── ② 澱みで色を濁らせる ──────────────────────────────
    //    表面が均一な緑だと「塗った板」に見えるので、低周波のむらを入れる。
    tint = mix(tint, POISON_SCUM_COLOR, poison_scum(input) * scum_strength);

    // ── ③ 背景合成 ────────────────────────────────────────
    //    毒沼はほぼ不透明にしたいので、シーン設定の不透明度を少し押し上げる。
    let opacity = clamp(input.surface_opacity * POISON_OPACITY_SCALE, 0.0, 1.0)
                * (1.0 - absorb);
    var color = mix(input.refraction_color, tint, opacity);

    // ── ④ 蛍光（深い所ほど不気味に光る）──────────────────
    //    `glow_boost` はゲーム内変数（例: ボスの怒り度）を @ref バインドで
    //    流し込める倍率。未バインドなら既定値 1.0 のまま。
    let glow_t = water_shade_saturate(input.thickness / POISON_GLOW_DEPTH);
    color = color + glow_color * glow_t * glow_boost;

    // ── ⑤ 泡 → 泡立ちの白緑 ──────────────────────────────
    //    シーン設定の泡色を白緑へ寄せてから、粒（膨らみ）を上乗せする。
    let foam_total  = input.foam + input.ripple_foam + input.shore_foam;
    let bubble_col  = mix(input.foam_color, POISON_BUBBLE_COLOR, POISON_BUBBLE_TINT);
    let bubble_amt  = foam_total + poison_bubbles(input, foam_total);
    color = color + bubble_col * bubble_amt;

    // ── ⑥ 反射 ────────────────────────────────────────────
    //    標準と同じフレネルで環境を映す（毒沼は溶岩と違って表面が濡れている）。
    let fresnel = water_shade_fresnel(
        input.normal, input.view_dir, input.fresnel_power, input.fresnel_strength);
    color = mix(color, input.reflection_color, fresnel * input.reflection_strength);

    // ── ⑦ 粘つく艶 ────────────────────────────────────────
    //    反射の**後**に加算するのが要点。反射像に埋もれず、常に表面のぬめりとして見える。
    //    水中から見上げているときは表面の艶が見えないので出さない。
    if (!input.underwater) {
        color = color + POISON_GLOSS_COLOR * poison_gloss(input);
    }

    return vec4<f32>(color, 1.0);
}
