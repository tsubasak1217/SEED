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
// ============================================================

// ─── 定数（マジックナンバー禁止のためすべて命名する）───────────

/// equirectangular の経度換算係数 1 / (2π)。
const SKY_REFL_INV_2PI: f32 = 0.15915494309189;
/// 同 緯度換算係数 1 / π。
const SKY_REFL_INV_PI: f32 = 0.31830988618379;
/// 有効フラグ（`tint_enabled.w`）の判定しきい値。
/// 値は 0（無効）か 1（有効）しか入らないので中点で判定する。
const SKY_REFL_ENABLED_EPS: f32 = 0.5;

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
    s.color = textureSampleLevel(tex, smp, vec2<f32>(u, v), 0.0).rgb * sky.tint_enabled.rgb;
    s.valid = true;
    return s;
}
