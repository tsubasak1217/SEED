// ============================================================
//  water_shading_contract.wgsl — 水面シェーディング契約 v1（Phase W8）
//
//  ## 役割（単一責任）
//  「水面 1 フラグメントの最終色をどう作るか」だけを切り出した契約を定義する。
//  提供するものは 3 つだけである:
//    1. `WaterShadeInput`      … エンジンが用意した全入力を 1 つにまとめた構造体
//    2. `water_shade_default`  … エンジン標準の水の見た目（W7 までの合成そのもの）
//    3. 補助関数群             … NaN ガード・飽和・輝度など、アセットから呼べる小道具
//
//  ## このファイルはバインディングを一切持たない
//  テクスチャ・ストレージバッファ・uniform を参照しない **純粋な関数の集まり**である。
//  必要な値はすべて `WaterShadeInput` に詰めて渡される。理由は 2 つ:
//    ・アセット作者が「どの group の何番を読めばいいか」を知らなくて済む
//    ・`WaterParams`（GPU レイアウト）が将来 vec4 を足しても契約が壊れない
//      （契約はレイアウトではなく **意味のある名前** で入力を渡すため）
//
//  ## ユーザーアセットとの関係
//  シェーディングアセット（`.wgsl`）が実装するのは
//      `fn water_shade(input: WaterShadeInput) -> vec4<f32>`
//  の 1 本だけである。エンジンは `water_shade_entry` 経由でこれを呼ぶ
//  （既定版は `water_shade_dispatch.wgsl`、アセット指定時は Rust が生成する。
//   詳細は `renderer/water/shading_asset.rs` と docs/water_shading_asset.md）。
//
//  ## アセットが変えられるのは「色」だけ
//  形状（格子・波の高さ場）・物性（波・流れ・波紋・岸波）は共有高さ場
//  `water_height_field.wgsl` が正典であり、ID パス・水面反射パス・コースティクスパスが
//  同じ関数を呼んで一致させている。アセットはこれらを **一切変更できない**。
//  変えられるのは `water_shade` が返す最終色（＋アルファ）だけである。
//
//  ## 連結順序
//  `water_height_field.wgsl` の**直後**、`water_shade_dispatch.wgsl`（またはアセット）の**直前**。
//  正典は `pipelines/water_surface.toml` の `shader_sources`。
//
//  ## 依存
//   - water_height_field.wgsl : `WATER_EPSILON`
// ============================================================

/// 水面シェーディング契約のバージョン。
///
/// Rust 側 `water/shading_asset.rs` の `WATER_SHADING_CONTRACT_VERSION` と**必ず一致**させる
/// （ユニットテスト `rust_and_wgsl_water_contract_versions_match` が固定する）。
/// アセットは先頭コメントに `// @water_shading_contract 1` と宣言する。
const WATER_SHADING_CONTRACT_VERSION: u32 = 1;

/// 出力色の上限（リニア HDR 放射輝度）。
///
/// アセットは任意の式を書けるため、発散した値がそのままブルーム・トーンマップへ流れると
/// 画面全体が白飛びする。`water_shade_nan_guard` がこの値でクランプする。
/// デファードの `SHADING_RADIANCE_MAX` と同じ意図の定数だが、水面パスは
/// `shading_contract.wgsl` を連結しないため、ここで独立に持つ。
const WATER_SHADE_RADIANCE_MAX: f32 = 1.0e4;

/// Rec.709 の相対輝度係数（`water_shade_luminance` 用）。
const WATER_SHADE_LUMA_REC709: vec3<f32> = vec3<f32>(0.2126, 0.7152, 0.0722);

// ============================================================
//  入力構造体
// ============================================================

/// 水面 1 フラグメントぶんの、シェーディングに必要な全入力。
///
/// エンジン（`water_surface.wgsl::fs_water`）が組み立てて `water_shade_entry` へ渡す。
/// **フィールドの削除・意味の変更は契約バージョンを上げること**（追加は末尾へ）。
///
/// 単位系は一貫してワールド空間・メートル・秒・リニア色である。
struct WaterShadeInput {
    // ── 幾何 ────────────────────────────────────────────────
    /// このフラグメントのワールド座標（頂点変位後の水面上の点）。
    world_pos: vec3<f32>,
    /// 波法線（ワールド空間・正規化済み）。
    ///
    /// 解析波・プロシージャルノイズ・波紋（インタラクション）・岸波の**勾配を合算**して
    /// 作った法線で、**常に視線側を向く**（水中から見上げているフラグメントでは反転済み）。
    /// フレネルと屈折の歪みはこの法線で評価するのが正しい。
    normal: vec3<f32>,
    /// 上向き基準の波法線（反転していない生の法線）。
    ///
    /// 「水面の傾き」そのものが欲しいとき（流れの斜面表現など）に使う。
    /// 水上から見ているフラグメントでは `normal` と一致する。
    normal_up: vec3<f32>,
    /// フラグメント → カメラ方向の単位ベクトル。
    view_dir: vec3<f32>,
    /// カメラが水面より下にある（＝水面を裏側から見ている）か。
    ///
    /// `true` のとき `thickness` は 0（水面の向こう側は水ではなく空気）で、
    /// 泡量（`foam` / `ripple_foam` / `shore_foam`）はすべて 0 に落としてある。
    underwater: bool,

    // ── 水の厚み ────────────────────────────────────────────
    /// 水面点から水底（＝シーン深度が示す背景点）までの距離 [m]。
    ///
    /// 背景が空（無限遠）のときは「十分深い」を表す大きな値が入る。
    /// 水中視点（`underwater = true`）では 0。
    thickness: f32,

    // ── 泡（すべて 0..1・水中視点ではゼロ）──────────────────
    /// 岸フォーム量。水深が `foam_width` 未満の帯で立ち上がり、強度を掛け済み。
    foam: f32,
    /// 航跡フォーム量（インタラクションの波紋が高いところ）。
    ripple_foam: f32,
    /// 岸波の泡量（砕け波の白帯と打ち上げ）。
    shore_foam: f32,
    /// 波紋（インタラクション）の生の波高 [m]。符号付き。
    /// 独自の泡表現を作りたいアセット向けの生値。
    ripple_height: f32,

    // ── 背景と反射（すでにサンプル済み）──────────────────────
    /// 屈折（グラブ）サンプル。波法線で UV をずらして読んだ **水中の背景色**（リニア HDR）。
    /// 岸際などで歪み先が不正になるケースはエンジン側で補正済み。
    refraction_color: vec3<f32>,
    /// 反射サンプルの色（リニア HDR）。プリマルチプライドの逆算済み＝**素の反射色**。
    /// 反射が無い画素では意味を持たない（`reflection_strength` が 0 になる）。
    reflection_color: vec3<f32>,
    /// 反射の強度 0..1。反射パスが走らないフレーム・非対応 GPU・反射強度 0 の水域では 0。
    /// **この値を掛けずに `reflection_color` を使ってはならない**（反射なしの画素で黒が滲む）。
    reflection_strength: f32,

    // ── 流れと時間 ──────────────────────────────────────────
    /// 流速ベクトル（ワールド XZ・[m/s]）。川以外の水域ではゼロ。
    flow: vec2<f32>,
    /// ゲーム内累計時間 [秒]（Play 非ポーズ時のみ進む）。明滅・スクロールに使う。
    time: f32,

    // ── 水域パラメータ（インスペクタの設定値）────────────────
    /// 浅場の色（リニア）。
    shallow_color: vec3<f32>,
    /// 深場の色（リニア）。
    deep_color: vec3<f32>,
    /// 吸収距離 [m]。小さいほど浅い所で深場の色へ寄る。
    absorption_distance: f32,
    /// 深場での最大不透明度 0..1（背景をどこまで隠すか）。
    surface_opacity: f32,
    /// 泡の色（リニア）。3 種の泡がすべてこの色を使う。
    foam_color: vec3<f32>,
    /// フレネル指数（大きいほど反射が縁だけに寄る）。
    fresnel_power: f32,
    /// フレネル寄与率 0..1（反射の効き具合の上限）。
    fresnel_strength: f32,
    /// 波の振幅 [m]（形状には既に反映済み。色側で「荒れ具合」を測る用）。
    wave_amplitude: f32,
    /// 粘度 0..1（0 = 水／1 = 溶岩・スライム。波速と波紋減衰へ既に反映済み）。
    /// **色には自動で効かない**ので、粘つく艶を出したいアセットが自分で使う。
    viscosity: f32,

    // ── パラメータ注釈のための内部値（Phase W8.2）────────────
    /// この水域の描画インスタンス番号（`u_water` / `u_water_shade_params` の添字）。
    ///
    /// **アセットから直接読む必要はない**。エンジンが生成するディスパッチが、
    /// アセットの `//! param` 宣言に対応する `var<private>` へ値を代入するために使う。
    /// 契約 v1 への**追加**フィールドであり、既存アセットの意味は何も変わらない。
    instance_index: u32,
}

// ============================================================
//  標準実装（エンジン既定の水の見た目）
// ============================================================

/// エンジン標準の水面シェーディング（Phase W7 までの合成と**完全に同一**）。
///
/// 処理順は 吸収 → 背景合成 → 泡加算 → フレネル反射混合。
///
/// アセット未指定・ロード失敗・契約バージョン不一致では、エンジンがこの関数を直接呼ぶ。
/// アセット側からも自由に呼べるので、
///   `return water_shade_default(input) * tint;`
/// のような「標準の水を少しだけ加工する」書き方や、
/// 一部の項だけ差し替えるための部分流用ができる。
fn water_shade_default(input: WaterShadeInput) -> vec4<f32> {
    // ── ① 吸収（Beer-Lambert 近似）: 厚いほど深場の色へ寄る ──
    let absorb_dist = max(input.absorption_distance, WATER_EPSILON);
    let absorb      = exp(-input.thickness / absorb_dist);
    let tint        = mix(input.deep_color, input.shallow_color, absorb);

    // ── ② 背景合成: 深いほど背景（屈折グラブ）を隠す ──────────
    let opacity = clamp(input.surface_opacity, 0.0, 1.0) * (1.0 - absorb);
    var color   = mix(input.refraction_color, tint, opacity);

    // ── ③ 泡（岸フォーム・航跡・岸波）を加算する ───────────────
    //    3 種とも同じ `foam_color` を使う（水ごとの泡の色は 1 つ、という設計）。
    //    水中視点では 3 つとも 0 になっているので、ここに分岐は要らない。
    color = color + input.foam_color * (input.foam + input.ripple_foam + input.shore_foam);

    // ── ④ フレネル: 浅い角度ほど反射像へ寄せる ────────────────
    //    `normal` は視線側を向く法線なので、この 1 本の式が
    //    水上＝空・景色の反射、水中＝水面の内側での反射の両方を兼ねる。
    let fresnel = water_shade_fresnel(
        input.normal, input.view_dir, input.fresnel_power, input.fresnel_strength);

    // ── ⑤ 反射像の混合 ──────────────────────────────────────
    //    強度 0 の画素では `mix` の重みが 0 になり、`reflection_color` の値は画に出ない。
    color = mix(color, input.reflection_color,
                fresnel * clamp(input.reflection_strength, 0.0, 1.0));

    // 背景を自前で合成済みなので不透明（alpha = 1）で返す。
    return vec4<f32>(color, 1.0);
}

// ============================================================
//  補助関数（アセットから自由に呼べる小道具）
// ============================================================

/// Schlick 近似のフレネル項（標準実装が使うものと同一）。
///
/// `power` が 0 以下だと `pow` が発散するため下限を掛ける。
fn water_shade_fresnel(n: vec3<f32>, v: vec3<f32>, power: f32, strength: f32) -> f32 {
    let facing = clamp(dot(n, v), 0.0, 1.0);
    return clamp(strength * pow(1.0 - facing, max(power, WATER_EPSILON)), 0.0, 1.0);
}

/// Beer-Lambert 近似の透過率（厚み → 0..1 の残存率）。
/// `water_shade_default` の ① と同じ式で、独自実装が吸収だけ流用したいとき用。
fn water_shade_absorption(thickness: f32, absorption_distance: f32) -> f32 {
    return exp(-thickness / max(absorption_distance, WATER_EPSILON));
}

/// 0..1 への飽和。
fn water_shade_saturate(x: f32) -> f32 {
    return clamp(x, 0.0, 1.0);
}

/// Rec.709 の相対輝度。
fn water_shade_luminance(c: vec3<f32>) -> f32 {
    return dot(c, WATER_SHADE_LUMA_REC709);
}

/// 値ノイズの土台になるハッシュ（0..1 の擬似乱数）。
///
/// マグマ・毒池のような「模様のある液体」を書くために必ず要るので、契約側で 1 本用意する。
/// 決定的（同じ入力＝同じ出力）で、フレーム間のちらつきが出ない。
fn water_shade_hash21(p: vec2<f32>) -> f32 {
    let h = dot(p, vec2<f32>(127.1, 311.7));
    return fract(sin(h) * 43758.5453123);
}

/// バイリニア補間の値ノイズ（0..1）。`water_shade_hash21` を格子で補間したもの。
fn water_shade_value_noise(p: vec2<f32>) -> f32 {
    let i = floor(p);
    let f = fract(p);
    // smoothstep 相当の補間重み（1 階微分が連続＝格子が見えない）。
    let u = f * f * (3.0 - 2.0 * f);
    let a = water_shade_hash21(i + vec2<f32>(0.0, 0.0));
    let b = water_shade_hash21(i + vec2<f32>(1.0, 0.0));
    let c = water_shade_hash21(i + vec2<f32>(0.0, 1.0));
    let d = water_shade_hash21(i + vec2<f32>(1.0, 1.0));
    return mix(mix(a, b, u.x), mix(c, d, u.x), u.y);
}

/// 4 オクターブの fBm ノイズ（0..1 付近）。溶岩の亀裂・毒の澱みなどの模様に使う。
fn water_shade_fbm(p: vec2<f32>) -> f32 {
    var sum   = 0.0;
    var amp   = 0.5;
    var coord = p;
    // オクターブ数はループ回数として固定する（動的ループは GPU で不利なため）。
    for (var i = 0; i < 4; i = i + 1) {
        sum   = sum + amp * water_shade_value_noise(coord);
        coord = coord * 2.03;   // 2 の整数倍を避けて格子の重なりを崩す
        amp   = amp * 0.5;
    }
    return sum;
}

/// アセットの返り値をエンジンへ入れる前に通す門。
///
/// NaN → 0、±Inf と過大値 → `WATER_SHADE_RADIANCE_MAX` でクランプ、負値 → 0。
/// アルファは 0..1 へクランプする。
/// **エンジン標準経路（`water_shade_default` 直行）には掛からない**
/// ＝アセット未指定時の出力は W7 以前と 1 ビットも変わらない。
fn water_shade_nan_guard(c: vec4<f32>) -> vec4<f32> {
    // NaN 判定は `x != x`（NaN は自分自身と等しくならない唯一の値）。
    let is_nan = vec4<bool>(c.x != c.x, c.y != c.y, c.z != c.z, c.w != c.w);
    let no_nan = select(c, vec4<f32>(0.0), is_nan);
    // 有限判定。NaN は上で 0 に潰れているので、ここに残る非有限値は ±Inf だけ。
    let is_finite = abs(no_nan) <= vec4<f32>(WATER_SHADE_RADIANCE_MAX);
    let finite    = select(vec4<f32>(0.0), no_nan, is_finite);
    let rgb = clamp(finite.rgb, vec3<f32>(0.0), vec3<f32>(WATER_SHADE_RADIANCE_MAX));
    return vec4<f32>(rgb, clamp(finite.w, 0.0, 1.0));
}
