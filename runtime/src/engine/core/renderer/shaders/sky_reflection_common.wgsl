// ============================================================
//  sky_reflection_common.wgsl — 反射のミス経路が使う「天球テクスチャ直接サンプル」共有モジュール
//
//  ## 役割（単一責任）
//  反射レイが何にも当たらなかったとき（＝空へ抜けたとき）に
//  **equirectangular 天球テクスチャを直接サンプルして本物の空の色を返す**、それだけを持つ。
//  バインディングは一切宣言しない（純粋な型・定数・関数のみ）。宣言は利用側が行う:
//    ・不透明反射 D6（`reflection_common.wgsl`）      … group2 に uniform で宣言
//    ・水面反射 W5.2（`water_reflection_common.wgsl`）… group3 に storage で宣言
//  storage / uniform の別は「その bind group に `binding_array` が同居するか」だけで決まる
//  （WebGPU 制約: binding_array と uniform buffer は同一 bind group に置けない）。
//  レイアウトは 64B で完全に同一なので、どちらで読んでも値は変わらない。
//
//  ## なぜ共有モジュールなのか
//  水面反射（W5.2）と不透明反射（D6）が **同じ方向へ同じ色を返す**ことが要件である。
//  UV 変換式（equirect の経度・緯度）・実効色の乗算・有効フラグの判定を別々に書くと、
//  水面と不透明面の反射が「同じ空を見ているのに違う色」になり、水際で不連続な境目が出る。
//  よって式は本ファイル 1 本だけに置き、両パスから連結して使う。
//
//  ## 連結順
//  本ファイルは **利用側の共通ファイルより前**に連結すること
//  （WGSL は宣言前の識別子を参照できない）。
//    D6 SSR : [sky_reflection_common, reflection_common, ddgi_common, reflection_ssr]
//    D6 RT  : [cluster_common, sky_reflection_common, reflection_common, ddgi_common, reflection_rt, …]
//    水面    : [water_height_field, …, sky_reflection_common, water_reflection_common, …]
//    背景描画: [sky_reflection_common, skybox.wgsl]（skybox.toml の shader_sources）
//
//  ## 色調整（色相／彩度／明度／コントラスト）もここが正典
//  「背景に映る空」と「反射に映る空」で色が食い違わないことが本モジュールの存在理由なので、
//  色調整（`sky_apply_color_adjust`）も**このファイル 1 本**に置き、
//  背景描画（`skybox.wgsl::fs_main`）も反射のミス経路（`sky_refl_sample`）も同じ関数を通す。
//  そのため本ファイルは天球を描く `skybox.toml` にも連結される（バインディングを
//  一切宣言しないので、どのパイプラインへ連結しても副作用が無い）。
// ============================================================

// ─── 定数（マジックナンバー禁止のためすべて命名する）───────────

/// equirectangular の経度換算係数 1 / (2π)。
const SKY_REFL_INV_2PI: f32 = 0.15915494309189;
/// 同 緯度換算係数 1 / π。
const SKY_REFL_INV_PI: f32 = 0.31830988618379;
/// 有効フラグ（`tint_enabled.w`）の判定しきい値。
/// 値は 0（無効）か 1（有効）しか入らないので中点で判定する。
const SKY_REFL_ENABLED_EPS: f32 = 0.5;

// ─── 色調整の定数 ────────────────────────────────────────────

/// 「既定値（無変換）とみなす」許容幅。
/// これより内側のパラメータは**計算そのものを飛ばす**。丸め誤差すら混ぜず、
/// 既定値では従来の出力とビット一致にするための分岐である（単なる高速化ではない）。
const SKY_ADJ_EPS: f32 = 1.0e-6;
/// 度 → ラジアン（色相シフト用）。
const SKY_ADJ_DEG_TO_RAD: f32 = 0.017453292519943295;
/// 輝度係数 R（Rec.709 相当。彩度・色相回転の基準輝度に使う）。
const SKY_ADJ_LUMA_R: f32 = 0.213;
/// 輝度係数 G。
const SKY_ADJ_LUMA_G: f32 = 0.715;
/// 輝度係数 B。
const SKY_ADJ_LUMA_B: f32 = 0.072;
/// 1 - SKY_ADJ_LUMA_R（色相回転行列の係数。実行時に引き算しないため定数化する）。
const SKY_ADJ_LUMA_R_INV: f32 = 0.787;
/// 1 - SKY_ADJ_LUMA_G。
const SKY_ADJ_LUMA_G_INV: f32 = 0.285;
/// 1 - SKY_ADJ_LUMA_B。
const SKY_ADJ_LUMA_B_INV: f32 = 0.928;
/// 色相回転行列の非輝度項（G 行）。標準の hue-rotate 行列（SVG feColorMatrix
/// type="hueRotate" と同一）の定数で、輝度を保ったまま色相環を回すために必要。
const SKY_ADJ_HUE_GR: f32 = 0.143;
/// 同（G 行・G 列）。
const SKY_ADJ_HUE_GG: f32 = 0.140;
/// 同（G 行・B 列）。
const SKY_ADJ_HUE_GB: f32 = 0.283;
/// コントラストの基準点（中間グレー）。**リニア空間の 0.5** を軸に伸縮する。
const SKY_ADJ_CONTRAST_PIVOT: f32 = 0.5;
/// 色調整後の下限（負値クランプ）。彩度 >1・コントラスト >1 は容易に負へ振れ、
/// 負の放射輝度は Bloom / トーンマップで黒い斑や NaN の原因になるため必ず落とす。
/// 上限は設けない（HDR の太陽をそのまま通す）。
const SKY_ADJ_MIN_RGB: f32 = 0.0;

// ─── 色調整（背景描画・反射・水面反射で共有する唯一の実装）────

/// 空の色に色調整（色相シフト／彩度／明度／コントラスト）を掛ける。
///
/// **HDR 前提**の実装である。色相と彩度は線形空間の輝度基準（Rec.709）で、
/// HSV への往復のような 0..1 前提の変換を通さない（1.0 超の太陽が壊れないため）。
/// コントラストは中間グレー基準の線形補間なので、これも 1.0 超で破綻しない。
///
/// - `color` … 天球テクスチャの生の色（tint / intensity を掛ける**前**）。
/// - `adj`   … x=色相シフト[度] / y=彩度 / z=明度 / w=コントラスト。
///
/// 適用順は 色相 → 彩度 → 明度 → コントラスト。既定値
/// （0, 1, 1, 1）では**どの段も実行されず入力をそのまま返す**（ビット一致）。
fn sky_apply_color_adjust(color: vec3<f32>, adj: vec4<f32>) -> vec3<f32> {
    var c = color;
    var changed = false;

    // ── 1. 色相シフト（輝度保存の回転行列。線形空間で完結する）──
    if abs(adj.x) > SKY_ADJ_EPS {
        let a  = adj.x * SKY_ADJ_DEG_TO_RAD;
        let cs = cos(a);
        let sn = sin(a);
        // 標準の hue-rotate 行列（行ベクトル 3 本）。cs=1, sn=0 で厳密に単位行列になる。
        let r0 = vec3<f32>(
            SKY_ADJ_LUMA_R + cs * SKY_ADJ_LUMA_R_INV - sn * SKY_ADJ_LUMA_R,
            SKY_ADJ_LUMA_G - cs * SKY_ADJ_LUMA_G     - sn * SKY_ADJ_LUMA_G,
            SKY_ADJ_LUMA_B - cs * SKY_ADJ_LUMA_B     + sn * SKY_ADJ_LUMA_B_INV,
        );
        let r1 = vec3<f32>(
            SKY_ADJ_LUMA_R - cs * SKY_ADJ_LUMA_R     + sn * SKY_ADJ_HUE_GR,
            SKY_ADJ_LUMA_G + cs * SKY_ADJ_LUMA_G_INV + sn * SKY_ADJ_HUE_GG,
            SKY_ADJ_LUMA_B - cs * SKY_ADJ_LUMA_B     - sn * SKY_ADJ_HUE_GB,
        );
        let r2 = vec3<f32>(
            SKY_ADJ_LUMA_R - cs * SKY_ADJ_LUMA_R     - sn * SKY_ADJ_LUMA_R_INV,
            SKY_ADJ_LUMA_G - cs * SKY_ADJ_LUMA_G     + sn * SKY_ADJ_LUMA_G,
            SKY_ADJ_LUMA_B + cs * SKY_ADJ_LUMA_B_INV + sn * SKY_ADJ_LUMA_B,
        );
        c = vec3<f32>(dot(r0, c), dot(r1, c), dot(r2, c));
        changed = true;
    }

    // ── 2. 彩度（同輝度のグレーとの線形補間。>1 の外挿も許す）──
    if abs(adj.y - 1.0) > SKY_ADJ_EPS {
        let luma = dot(c, vec3<f32>(SKY_ADJ_LUMA_R, SKY_ADJ_LUMA_G, SKY_ADJ_LUMA_B));
        c = mix(vec3<f32>(luma, luma, luma), c, adj.y);
        changed = true;
    }

    // ── 3. 明度（単純乗算。intensity と直交する“色調整側”のゲイン）──
    if abs(adj.z - 1.0) > SKY_ADJ_EPS {
        c = c * adj.z;
        changed = true;
    }

    // ── 4. コントラスト（中間グレー基準の線形補間／外挿）──
    if abs(adj.w - 1.0) > SKY_ADJ_EPS {
        c = (c - vec3<f32>(SKY_ADJ_CONTRAST_PIVOT)) * adj.w + vec3<f32>(SKY_ADJ_CONTRAST_PIVOT);
        changed = true;
    }

    // 何か掛けたときだけ負値を落とす（既定値のビット一致を壊さないため）。
    if changed {
        c = max(c, vec3<f32>(SKY_ADJ_MIN_RGB));
    }
    return c;
}

// ─── 型 ──────────────────────────────────────────────────────

/// 反射のミス経路が使うスカイボックスパラメータ（Rust 側 `reflection_sky.rs::ReflectionSkyUniform`
/// と 1:1・64B のミラー）。
///
/// **`skybox.wgsl` の `SkyboxUniform` とは別物**である。あちらは球メッシュを配置するための
/// 描画用行列（平行移動・スケール込み）を持つが、こちらが要るのは
/// 「ワールド方向 → 天球ローカル方向」の逆回転と実効色だけである。
struct ReflectionSkyUniform {
    /// ワールド → 天球ローカルの逆回転行列 第 1 行（.xyz。.w は未使用）。
    /// CameraLocked は単位行列（ワールド方向でそのままサンプルする）。
    rot_inv_0: vec4<f32>,
    /// 同 第 2 行。
    rot_inv_1: vec4<f32>,
    /// 同 第 3 行。
    rot_inv_2: vec4<f32>,
    /// rgb = tint × intensity（実効色の乗算項）／**a = 有効フラグ（0 でスカイボックス無し）**。
    tint_enabled: vec4<f32>,
    /// 色調整（x=色相シフト[度] / y=彩度 / z=明度 / w=コントラスト）。
    /// `skybox.wgsl::SkyboxUniform.adjust` と同じ値が入る（背景と反射で必ず一致させる）。
    adjust: vec4<f32>,
}

/// 天球サンプルの結果。`valid = false` は「このシーンにスカイボックスが無い」の意で、
/// 呼び出し側は GI プローブ／アンビエントへフォールバックする。
struct SkyReflSample {
    color: vec3<f32>,
    valid: bool,
}

// ─── 本体 ────────────────────────────────────────────────────

/// **天球テクスチャを直接**サンプルする（画面外の空を映すための唯一の窓口）。
///
/// equirectangular の UV 変換は `skybox.wgsl::fs_main` と**同一式**である
/// （経度 = atan2(z, x)/2π + 0.5、緯度 = acos(y)/π。ミップ継ぎ目回避で level 0 固定）。
/// これを揃えないと「画面内に写っている空」と「画面外の空（本経路）」で
/// 同じ方向なのに違う色が出て、反射像に不連続な色の境目が走る。
///
/// - `sky` … 逆回転＋実効色＋有効フラグ（uniform / storage のどちらから読んでもよい）。
/// - `tex` … equirectangular 天球テクスチャ。
/// - `smp` … **経度方向 Repeat** のサンプラー（clamp すると経度 0°の継ぎ目に縦線が出る）。
/// - `dir` … ワールド空間の反射方向（正規化済みでなくてもよい。内部で正規化する）。
fn sky_refl_sample(
    sky: ReflectionSkyUniform,
    tex: texture_2d<f32>,
    smp: sampler,
    dir: vec3<f32>,
) -> SkyReflSample {
    var s: SkyReflSample;
    s.color = vec3<f32>(0.0, 0.0, 0.0);
    s.valid = false;
    if sky.tint_enabled.w < SKY_REFL_ENABLED_EPS {
        return s; // このシーンにスカイボックスが無い（ダミーがバインドされている）。
    }
    // ワールド方向 → 天球ローカル方向（CameraLocked は単位行列なので実質そのまま）。
    let d = normalize(vec3<f32>(
        dot(sky.rot_inv_0.xyz, dir),
        dot(sky.rot_inv_1.xyz, dir),
        dot(sky.rot_inv_2.xyz, dir),
    ));
    let u = atan2(d.z, d.x) * SKY_REFL_INV_2PI + 0.5;
    let v = acos(clamp(d.y, -1.0, 1.0)) * SKY_REFL_INV_PI;
    // 生のテクスチャ色 → 色調整 → 実効色（tint × intensity）の順。
    // この順序は `skybox.wgsl::fs_main` と厳密に一致させること
    //（先に tint/intensity を掛けるとコントラストの中間グレー基準がズレ、
    //  背景の空と反射の空で色が食い違う）。
    let tex_rgb = textureSampleLevel(tex, smp, vec2<f32>(u, v), 0.0).rgb;
    s.color = sky_apply_color_adjust(tex_rgb, sky.adjust) * sky.tint_enabled.rgb;
    s.valid = true;
    return s;
}
