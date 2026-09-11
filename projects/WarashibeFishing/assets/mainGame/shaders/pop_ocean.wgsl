// @water_shading_contract 1
// ============================================================
//  pop_ocean.wgsl — ポップな海（トゥーン／フラット寄り）の水面シェーディングアセット
//
//  ## 用途
//  3D 見下ろし視点のカジュアルな釣りゲーム向け。写実ではなく
//  「彩度が高く、色の切り替わりが明快で、白い泡とキラキラが読み取りやすい海」を作る。
//
//  ## 使い方
//  WaterVolume の「水面シェーダ」にこのファイルを指定するだけ。
//  形状（波・流れ・波紋）はインスペクタの通常の水パラメータで作り、
//  **色だけ**をこのアセットが決める（契約どおり）。
//
//  ## 見た目の作り
//    ・段階的な深度カラー … 水深（thickness）をバンド量子化し、
//                           浅場色（シーン設定）→ 中間色（パラメータ）→ 深場色（シーン設定）を
//                           2〜4 段のベタ塗りで切り替える。バンドの境界はノイズで
//                           わずかに揺らして「手描きの等深線」に見せる。
//    ・スタイライズド泡   … ①岸際の白い縁（浅い帯＋ノイズでギザつかせる）
//                           ②波の山に沿った泡（法線の傾き＋波紋の波高から算出）
//                           どちらも 2 値寄りの硬いエッジで乗せる。
//    ・段階スペキュラ     … 固定光の Blinn-Phong を段数で量子化したトゥーン鏡面。
//                           加えて、時間で瞬く「きらめきの点」を散らす。
//    ・明快な色           … 反射・屈折は契約の入力をそのまま使いつつ、
//                           最後に彩度と明度を持ち上げてポップに寄せる。
//    ・半透明             … 浅い所は背景（屈折グラブ）が透け、深いほど不透明になる。
//                           合成はアセット自身が行う（契約 3.1: アルファはブレンドに使われない）。
//
//  ## 推奨するシーン側の設定（物性はアセットの担当外＝インスペクタで設定する）
//    粘度 viscosity            = 0.0    （さらさらの水。波が素直に進む）
//    波紋減衰 ripple_damping   = 0.35   （ウキの波紋が気持ちよく広がる）
//    波振幅 wave_amplitude     = 0.12   （見下ろしなので大きすぎない）
//    波速度 wave_speed         = 0.8
//    吸収距離 absorption_distance = 4.0 （浅瀬の底が見えるくらい）
//    深場色 deep_color         = (0.02, 0.13, 0.38)  濃い青
//    浅場色 shallow_color      = (0.25, 0.85, 0.80)  ターコイズ
//    泡色 foam_color           = (1.00, 1.00, 1.00)  白
//    反射強度 reflection_intensity = 0.35（映り込みすぎると濁って見える）
//    反射粗さ reflection_roughness = 0.5
//
//  ## インスペクタから触れるパラメータ（Phase W8.2）
//  下の `override` 宣言が「水面シェーダ」欄の直下に UI 行として並ぶ。
//  **宣言は 1 アセット 8 個まで**（`SHADE_PARAM_MAX`）なので、
//  「水域ごとに変えたくなる度合いが高い 8 つ」だけをパラメータにし、
//  残り（全体の色味・きらめきの粒度・不透明度の下限上限など）は下の `const` に置いた。
//  浅場色・深場色・泡色・不透明度の上限は **シーン側の水パラメータがそのまま入力になる**ので、
//  ここで二重に持たない（契約の `WaterShadeInput` から読む）。
// ============================================================

// ─── インスペクタへ出すパラメータ（8 個 = 上限）────────────────
// 宣言した名前は、この下のコードで**そのまま値として**読める
// （エンジンが受け渡しコードを生成する。詳細は docs/water_shading_asset.md）。

@color @reset
override mid_color: vec3<f32> = vec3(0.06, 0.55, 0.78);   // 中間の海色
@range(1.0, 4.0) @reset
override band_count: f32 = 3.0;                           // 深度カラーの段数
@range(0.5, 40.0) @reset
override depth_range: f32 = 6.0;                          // 最深色に達する水深(m)
@range(0.0, 1.0) @reset
override foam_width: f32 = 0.16;                          // 岸の白フチの幅
@range(0.0, 1.0) @reset
override foam_threshold: f32 = 0.45;                      // 波の山に泡が出る高さ
@range(1.0, 6.0) @reset
override specular_steps: f32 = 2.0;                       // ハイライトの段数
@color @reset
override sparkle_color: vec3<f32> = vec3(2.4, 2.8, 3.2);  // きらめきの色
@range(0.0, 2.0) @reset
override saturation_boost: f32 = 0.55;                    // 彩度の持ち上げ

// ─── 深度バンドのパラメータ ──────────────────────────────────

/// バンド境界を揺らすノイズの空間周波数 [1/m]。小さいほど大きくうねる等深線になる。
const POP_BAND_WOBBLE_SCALE: f32 = 0.18;
/// バンド境界の揺れ幅（深度 0..1 に対する量）。0 で完全な同心バンドになる。
const POP_BAND_WOBBLE: f32 = 0.07;
/// バンド境界をどれだけ滑らかに繋ぐか（0 = 完全なベタ塗り／大きいほどグラデ）。
/// 見下ろし視点でジャギらない程度に、ごくわずかだけぼかす。
const POP_BAND_SOFTNESS: f32 = 0.06;
/// 中間色が置かれるバンド位置（0 = 浅場寄り／1 = 深場寄り）。
/// 0.5 より小さくすると「浅い側の見せ場（中間色）が広い」＝釣り場が読みやすい。
const POP_MID_PIVOT: f32 = 0.42;

// ─── 泡のパラメータ ──────────────────────────────────────────

/// 岸フチのギザつきノイズの空間周波数 [1/m]。大きいほど細かいギザギザになる。
const POP_FOAM_EDGE_SCALE: f32 = 1.6;
/// 岸フチのギザつきの深さ（幅に対する比率）。
const POP_FOAM_EDGE_JITTER: f32 = 0.55;
/// 岸フチのノイズが流れる速さ [m/s]（流速ゼロの水域でも泡が動くように）。
const POP_FOAM_EDGE_DRIFT: f32 = 0.25;
/// 泡のエッジの硬さ（0 に近いほど 2 値のベタ塗り）。
const POP_FOAM_SOFTNESS: f32 = 0.12;
/// 「波の山」とみなす法線の傾きの基準（1 - normal_up.y のスケール）。
/// これで割って 0..1 に正規化してから `foam_threshold` と比べる。
const POP_CREST_SLOPE_REF: f32 = 0.30;
/// 波紋（インタラクション）の波高がどれだけで泡になるか [m]。
/// ウキ・ルアーの着水で白い輪が出る量を決める。
const POP_CREST_RIPPLE_HEIGHT: f32 = 0.08;
/// 山の泡に掛かるノイズの空間周波数 [1/m]（泡をベタ帯でなく粒状にする）。
const POP_CREST_NOISE_SCALE: f32 = 2.2;
/// 山の泡のノイズが効く割合（0 = ノイズ無しのベタ帯）。
const POP_CREST_NOISE_MIX: f32 = 0.5;
/// 泡の最大量（加算しすぎて白飛びしないための上限）。
const POP_FOAM_MAX: f32 = 1.0;

// ─── トゥーン鏡面ときらめきのパラメータ ──────────────────────

/// 鏡面を作る仮想光源の向き（真上からやや手前。正規化して使う）。
///
/// 水面パスはライト配列を持たない（バインドグループが上限）ため、
/// **アセット固有の固定光**で評価する（`poison.wgsl` と同じ流儀）。
/// シーンのライトとは連動しない。
const POP_LIGHT_DIR: vec3<f32> = vec3<f32>(0.30, 1.0, 0.45);
/// 鏡面の鋭さ（Blinn-Phong 指数）。ポップに見せるため広めの艶にする。
const POP_SPEC_POWER: f32 = 42.0;
/// 鏡面の強さ（HDR なので 1 を超えてよい）。
const POP_SPEC_STRENGTH: f32 = 1.2;
/// 鏡面の色（ほんのり暖色寄りの白）。
const POP_SPEC_COLOR: vec3<f32> = vec3<f32>(1.0, 0.98, 0.92);
/// きらめき（スパークル点）の空間周波数 [1/m]。大きいほど粒が細かい。
const POP_SPARKLE_SCALE: f32 = 6.0;
/// きらめきとみなすノイズ値のしきい値（高いほど粒がまばら）。
const POP_SPARKLE_THRESHOLD: f32 = 0.86;
/// きらめきが瞬く速さ [rad/s]。
const POP_SPARKLE_SPEED: f32 = 5.0;
/// きらめきの強さ。
const POP_SPARKLE_STRENGTH: f32 = 0.8;
/// きらめきが流れる速さ [m/s]（流速ゼロの水域でも粒が動くように）。
const POP_SPARKLE_DRIFT: f32 = 0.12;

// ─── 全体の色調整・不透明度のパラメータ ──────────────────────

/// 全体の色味（乗算のトーン。1,1,1 で無加工）。
/// わずかに青緑へ寄せて「南国の海」の印象を作る。
const POP_TINT: vec3<f32> = vec3<f32>(0.98, 1.02, 1.04);
/// 明度の持ち上げ倍率（彩度を上げた後に掛ける）。
const POP_BRIGHTNESS: f32 = 1.08;
/// 浅場での不透明度（0 = 底が完全に透ける）。
const POP_OPACITY_SHALLOW: f32 = 0.35;
/// 深場での不透明度（1 = 背景を完全に隠す）。
/// 実際にはシーン設定の `surface_opacity` を上限として掛ける。
const POP_OPACITY_DEEP: f32 = 1.0;

// ============================================================
//  実装
// ============================================================

/// 深度を 0..1 のバンド値へ量子化して返す。
///
/// 返り値は「0 = 最も浅い段」「1 = 最も深い段」で、段数は `band_count`。
/// 境界はノイズでわずかに揺らし、`POP_BAND_SOFTNESS` の幅だけ滑らかに繋ぐ。
fn pop_depth_band(input: WaterShadeInput) -> f32 {
    // ── ① 生の水深比 ───────────────────────────────────────
    //    背景が空（無限遠）のとき thickness は非常に大きい値なので、
    //    飽和させて「最深バンド」に落とす。
    let raw = water_shade_saturate(input.thickness / max(depth_range, WATER_EPSILON));

    // ── ② 境界の揺らぎ ────────────────────────────────────
    //    等深線が数学的に綺麗すぎると CG くさいので、低周波ノイズでうねらせる。
    //    fBm は 0..1 付近に分布するので -0.5 して符号付きの揺れにする。
    let wobble = (water_shade_fbm(input.world_pos.xz * POP_BAND_WOBBLE_SCALE) - 0.5)
               * POP_BAND_WOBBLE;
    let t = water_shade_saturate(raw + wobble);

    // ── ③ 量子化 ──────────────────────────────────────────
    //    段数は 1 以上の整数に丸める（インスペクタは連続値を送ってくる）。
    let bands = max(round(band_count), 1.0);
    //    段の中の位置（0..1）を出し、境界だけを smoothstep で細く繋ぐ。
    //    こうすると「ほぼベタ塗り、境界 1px だけアンチエイリアス」になる。
    //    t = 1.0 のとき floor が bands になって「段が 1 つ多い」ことにならないよう、
    //    直前でわずかに切り下げて最上段を bands - 1 に収める。
    let scaled = min(t * bands, bands - WATER_EPSILON);
    let step_i = floor(scaled);
    let frac   = scaled - step_i;
    let soft   = clamp(POP_BAND_SOFTNESS, WATER_EPSILON, 0.5);
    let eased  = step_i + smoothstep(1.0 - soft, 1.0, frac);
    // 段の代表値を 0..1 へ正規化する。1 段のときは常に 0（＝浅場色一色）。
    return water_shade_saturate(eased / max(bands - 1.0, 1.0));
}

/// バンド値から海の色を引く（浅場色 → 中間色 → 深場色 の 2 段補間）。
///
/// 浅場色・深場色は**シーン側の水パラメータ**をそのまま使う（アセットで重複させない）。
/// 中間色だけがこのアセットのパラメータ `mid_color` である。
fn pop_band_color(input: WaterShadeInput, band: f32) -> vec3<f32> {
    let pivot = clamp(POP_MID_PIVOT, WATER_EPSILON, 1.0 - WATER_EPSILON);
    if (band < pivot) {
        return mix(input.shallow_color, mid_color, band / pivot);
    }
    return mix(mid_color, input.deep_color, (band - pivot) / (1.0 - pivot));
}

/// 岸際の白いフチの量 0..1 を返す。
///
/// 水深が浅い帯（`foam_width`）で立ち上がり、境界をノイズでギザつかせる。
/// エンジンの `input.foam`（シーン設定の岸フォーム）とは別に、
/// **アセット側で幅と形を決められる**ようにするための独自実装である。
fn pop_shore_rim(input: WaterShadeInput) -> f32 {
    if (foam_width <= 0.0) {
        return 0.0;
    }
    // 水深比。`depth_range` ではなく岸フチ用に独立した尺度にすると、
    // 「深度バンドを広げたら白フチも太った」という直感に反する挙動を避けられる。
    let d = water_shade_saturate(input.thickness / max(depth_range, WATER_EPSILON));

    // ギザつき: 岸線に沿ってノイズで境界をずらす。流速（川）＋一定ドリフトで動かす。
    let drift = input.flow + vec2<f32>(POP_FOAM_EDGE_DRIFT, POP_FOAM_EDGE_DRIFT * 0.6);
    let uv    = (input.world_pos.xz - drift * input.time) * POP_FOAM_EDGE_SCALE;
    let jitter = (water_shade_value_noise(uv) - 0.5) * foam_width * POP_FOAM_EDGE_JITTER;
    let edge   = max(foam_width + jitter, WATER_EPSILON);

    // 硬いエッジ（トゥーン）にするため、遷移幅は幅そのものではなく固定比率で決める。
    let soft = max(edge * POP_FOAM_SOFTNESS, WATER_EPSILON);
    return 1.0 - smoothstep(edge - soft, edge + soft, d);
}

/// 波の山に沿った泡の量 0..1 を返す。
///
/// 「山」の判定材料は 2 つ:
///   ・法線の傾き（`normal_up`）… うねりの斜面／頂点で立つ
///   ・波紋の波高（`ripple_height`）… ウキやルアーの着水で立つ
fn pop_crest_foam(input: WaterShadeInput) -> f32 {
    // ── ① 法線の傾きから「荒れ具合」を測る ────────────────
    //    normal_up は反転していない生の法線なので、y が小さいほど斜面が急。
    let slope = water_shade_saturate((1.0 - input.normal_up.y) / POP_CREST_SLOPE_REF);

    // ── ② 波紋の正の波高（盛り上がっている所）を足す ──────
    //    負（凹み）は泡にしないので max で切る。
    let ripple = water_shade_saturate(
        max(input.ripple_height, 0.0) / POP_CREST_RIPPLE_HEIGHT);

    let crest = water_shade_saturate(max(slope, ripple));

    // ── ③ 粒状のノイズを掛けて「泡の塊」にする ────────────
    let uv = (input.world_pos.xz - input.flow * input.time) * POP_CREST_NOISE_SCALE;
    let n  = mix(1.0, water_shade_value_noise(uv), POP_CREST_NOISE_MIX);

    // ── ④ しきい値で 2 値寄りに切る ──────────────────────
    let thr  = clamp(foam_threshold, 0.0, 1.0);
    let soft = max(POP_FOAM_SOFTNESS, WATER_EPSILON);
    return smoothstep(thr, thr + soft, crest * n);
}

/// トゥーン鏡面（段階量子化したハイライト）の強度を返す。
fn pop_toon_specular(input: WaterShadeInput) -> f32 {
    let light = normalize(POP_LIGHT_DIR);
    // Blinn-Phong のハーフベクトル。水面パスは実ライトを持たないので固定光で評価する。
    let h   = normalize(light + input.view_dir);
    let ndh = water_shade_saturate(dot(input.normal, h));
    let s   = water_shade_saturate(pow(ndh, POP_SPEC_POWER));
    // 段数で量子化する。`ceil` を使うので s = 0 の所は 0 のまま（背景に白がにじまない）。
    let steps = max(round(specular_steps), 1.0);
    return ceil(s * steps) / steps;
}

/// きらめき（時間で瞬く点）の強度を返す。
fn pop_sparkle(input: WaterShadeInput) -> f32 {
    let drift = input.flow + vec2<f32>(POP_SPARKLE_DRIFT, -POP_SPARKLE_DRIFT * 0.7);
    let uv    = (input.world_pos.xz - drift * input.time) * POP_SPARKLE_SCALE;
    let n     = water_shade_value_noise(uv);
    // しきい値より上だけを粒として残す（まばらな点になる）。
    let grain = smoothstep(POP_SPARKLE_THRESHOLD, 1.0, n);
    // 粒ごとに位相をずらして瞬かせる（同じ位置なら同じ位相＝ちらつかない）。
    let phase = n * 6.2831853;
    let blink = 0.5 + 0.5 * sin(input.time * POP_SPARKLE_SPEED + phase);
    // 瞬きを鋭くして「点滅」に寄せる（ポップな海の記号的なキラキラ）。
    return grain * blink * blink * POP_SPARKLE_STRENGTH;
}

/// 彩度と明度を持ち上げてポップな色に寄せる。
///
/// 輝度を保ったまま彩度だけを伸ばす（`mix` の係数が 1 を超える外挿）。
/// 外挿は負値を生み得るので、最後に 0 で切ってから明度を掛ける。
fn pop_grade(color: vec3<f32>) -> vec3<f32> {
    let luma      = water_shade_luminance(color);
    let saturated = mix(vec3<f32>(luma), color, 1.0 + max(saturation_boost, 0.0));
    return max(saturated, vec3<f32>(0.0)) * POP_TINT * POP_BRIGHTNESS;
}

/// 水面 1 ピクセルの最終色を決める（契約関数）。
fn water_shade(input: WaterShadeInput) -> vec4<f32> {
    // ── ① 段階的な深度カラー ──────────────────────────────
    let band  = pop_depth_band(input);
    let tint  = pop_band_color(input, band);

    // ── ② 背景合成（浅い所は透け、深い所は不透明）──────────
    //    シーン設定の `surface_opacity` を上限として、バンドで補間する。
    //    バンド値を使うので、透け具合も色と同じ段で切り替わる（＝見た目が一致する）。
    let opacity = clamp(input.surface_opacity, 0.0, 1.0)
                * mix(POP_OPACITY_SHALLOW, POP_OPACITY_DEEP, band);
    var color = mix(input.refraction_color, tint, opacity);

    // ── ③ 反射（フレネル）──────────────────────────────────
    //    彩度を上げる**前**に混ぜる。空の色ごとポップに寄るので統一感が出る。
    //    強度 0 の画素では重みが 0 になり、反射色は画に出ない（契約の注意どおり）。
    let fresnel = water_shade_fresnel(
        input.normal, input.view_dir, input.fresnel_power, input.fresnel_strength);
    color = mix(color, input.reflection_color,
                fresnel * clamp(input.reflection_strength, 0.0, 1.0));

    // ── ④ 彩度・明度の持ち上げ ────────────────────────────
    color = pop_grade(color);

    // ── ⑤ 泡・ハイライト（水上から見ているときだけ）────────
    //    水中視点では thickness が 0 になるため、そのまま計算すると
    //    画面全体が「岸の白フチ」で埋まってしまう。ここで丸ごと落とす
    //    （エンジン側の泡量 3 種も水中視点ではゼロになっている）。
    if (!input.underwater) {
        // 岸フチ（アセット独自）＋ 波の山の泡（アセット独自）
        //   ＋ エンジンの泡量（岸フォーム・航跡・岸波）を 1 本にまとめる。
        //   泡は「白い塗り」なので加算ではなく **色の置き換え** で乗せる。
        //   加算だと深場で白飛びし、フラットな見た目が壊れるため。
        let engine_foam = input.foam + input.ripple_foam + input.shore_foam;
        let foam_amount = min(
            pop_shore_rim(input) + pop_crest_foam(input) + engine_foam, POP_FOAM_MAX);
        color = mix(color, input.foam_color, foam_amount);

        // トゥーン鏡面ときらめきは泡の上に乗せる（泡も濡れて光る）。
        color = color + POP_SPEC_COLOR * (pop_toon_specular(input) * POP_SPEC_STRENGTH);
        color = color + sparkle_color * pop_sparkle(input);
    }

    // 背景は ② で自前合成済み。アルファは現契約ではブレンドに使われない（docs 3.1）が、
    // 「この画素がどれだけ水で覆われているか」を素直に返しておく。
    return vec4<f32>(color, opacity);
}
