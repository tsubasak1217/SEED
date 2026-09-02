// ============================================================
//  caustics.wgsl — 水中コースティクス（集光模様）生成パス（Phase W5.3）
//
//  ## 役割（単一責任）
//  シーン深度から復元したワールド座標が「水面より下」なら、そこへ届く直達光が
//  水面でどう変化するかを **2 枚の MRT** へ書き出すだけ。影の判定そのものは行わない。
//    location0（Rgba16Float）: rgb = 水を通った直達光の透過率／a = 集光強度
//    location1（Rg16Float）  : 影のサンプル位置に足すワールド XZ オフセット（m）
//  消費側は
//    - deferred ライティング  : `Surface.caustics` / `Surface.shadow_refract_offset`
//    - RT ソフト影マスク生成  : `shadow_mask.wgsl` がレイ原点をオフセットする
//  である（オフセットは平行光の影にしか適用されない）。
//
//  ## なぜ独立パスなのか
//  コースティクスは「水面から見た模様」ではなく「**水底のピクセル**が受け取る光量」である。
//  水面フォワードパスは水面のフラグメントしか走らないため、水底へ模様を焼くことができない。
//  そこで G-Buffer 完成後・deferred ライティング前にフルスクリーン 1 パスを挟み、
//  ライティングの入力として供給する形にしてある。
//
//  ## 高さ場は絶対に新造しない
//  模様の元になる水面形状は `water_height_field.wgsl`（W5.1 の共有モジュール）の
//  `water_surface_height` / `water_surface_gradient` **だけ**を使う。独自のノイズや波を
//  足すと、水面の見た目（解析波＋波紋＋岸波）と水底の模様が食い違って一気に嘘くさくなる。
//  時間も水面パスと同一の `u_camera.time` を使うので、模様と水面は完全に同期する。
//
//  ## バインディング規約
//    group 0: カメラ（CameraUniform, binding 0。本ファイルが宣言）
//    group 1: binding0 = 水パラメータ配列／binding4,5 = ショアフィールドとサンプラー
//             （**すべて共有モジュール `water_height_field.wgsl` が宣言済み。再宣言しない**）
//    group 2: インタラクションフィールド一式（同上・共有モジュール）
//    group 3: binding0 = シーン深度（DepthOnly ビュー。textureLoad 専用。本ファイル）
//             binding1 = 本パス専用パラメータ UBO（本ファイル）
//
//  頂点段は高さ場を一切読まない（ただのフルスクリーン三角形）ため、
//  パイプライン TOML に `vertex_visible_groups` は指定しない（FRAGMENT 可視のみでよい）。
// ============================================================

// ─── Group 0: カメラ ─────────────────────────────────────────
//
// `shader_common.wgsl` は連結しない（マテリアル group を要求してしまうため）。
// `water_surface.wgsl` / `deferred_lighting.wgsl` と同じ方針で、Rust 側
// `uniforms::CameraUniform`（304 bytes）と 1:1 対応する構造体をここで自前宣言する。
// **フィールドを 1 つでも増減させないこと**（全パス共通のカメラ BindGroup と食い違う）。
struct CameraUniform {
    view_proj:      mat4x4<f32>,
    view:           mat4x4<f32>,
    position:       vec3<f32>,
    /// ゲーム内累計時間（秒）。**水面パスと同一ソース**＝模様と水面が同期する。
    time:           f32,
    /// レンダーターゲット全面の解像度（ピクセル）。本パスでは未使用（レイアウト合わせ）。
    resolution:     vec2<f32>,
    _pad2:          vec2<f32>,
    /// 逆 ViewProjection（シーン深度 → ワールド座標の復元用）。
    inv_view_proj:  mat4x4<f32>,
    /// 速度バッファ用の前フレーム ViewProjection（本パスでは未使用。オフセット合わせ）。
    prev_view_proj: mat4x4<f32>,
    /// フレームバッファ基準のビューポート矩形 (x, y, w, h)。
    /// NDC はこの矩形へ写像されるため、深度 → ワールド復元はこれで正規化する。
    viewport:       vec4<f32>,
}
@group(0) @binding(0) var<uniform> u_camera: CameraUniform;

// ─── Group 3: シーン深度と本パス専用パラメータ ────────────────

/// シーン深度（共有深度の DepthOnly ビュー）。`textureLoad` 専用（比較サンプラー不要）。
@group(3) @binding(0) var t_scene_depth: texture_depth_2d;

/// コースティクス生成パス専用の UBO。
///
/// Rust 側 `renderer::caustics::CausticsUniform` と **フィールド順・サイズ一致必須**
/// （`caustics.rs` の naga 実測サイズ照合テストが番人）。
struct CausticsUniform {
    /// x = クアッド系インスタンス数（探索範囲）／y,z,w = 予約(0)。
    ///
    /// 水パラメータ配列は `[クアッド…, 川…]` の順に並んでいる（`water/mod.rs`）。
    /// コースティクスはクアッド（Ocean / Region）だけを対象にするので、
    /// 先頭 `counts.x` 個だけを線形に走査すれば足りる。
    counts: vec4<u32>,
}
@group(3) @binding(1) var<uniform> u_caustics: CausticsUniform;

// ─── 定数（マジックナンバー禁止のため全て命名する）───────────
//
// 注意: `WATER_EPSILON` / `WATER_TAU` などは共有モジュール
// `water_height_field.wgsl` が既に宣言しているので**再宣言しない**（重複定義になる）。

/// 「シーン深度がこの値以上なら背景（空／何も描かれていない）」の閾値。
/// 深度は Clear(1.0) 起点・比較 LessEqual の通常 Z なので 1.0 が背景にあたる。
/// deferred_lighting.wgsl の `DEFERRED_BACKGROUND_DEPTH` と同一規約。
const CAUSTICS_BACKGROUND_DEPTH: f32 = 1.0;

/// 水の屈折率（可視光・20℃ の実測値）。
const WATER_IOR: f32 = 1.333;

/// 屈折による横ずれ係数（無次元）。
///
/// 太陽光をほぼ鉛直入射と近似すると、水面の傾き（勾配）θ に対して屈折光線は
/// スネルの法則より `θ − asin(sinθ / n) ≒ θ (1 − 1/n)` だけ傾く。
/// したがって水深 d の点へ届く光が通った水面位置は、その点の真上から
/// **勾配 × d × (1 − 1/n)** だけずれている。集光の計算はこのずらした位置で行う
/// （収束ゲイン自体は振幅正規化の形状式へ置き換えたため、この係数は屈折オフセット専用）。
const CAUSTICS_REFRACT_K: f32 = 1.0 - 1.0 / WATER_IOR;

/// 細かさ倍率の下限（無次元）。0 以下だと差分ステップが発散するので切る。
const CAUSTICS_SCALE_MIN: f32 = 1.0e-3;

/// ラプラシアンを取る差分ステップの基準幅（m。細かさ倍率 1.0 のときの値）。
///
/// **ステップが小さいほど高周波成分＝細かい網目を拾い、大きいほど大きなうねりだけを拾う。**
/// 既定の解析波（波長 ≒ 50m の基本波と、その 1.6〜6.9 倍の高調波）に対して、
/// 実際に水底へ出したい網目の間隔が数 m 程度になる幅として 0.35m を選んだ。
const CAUSTICS_BASE_STEP_M: f32 = 0.35;

/// 集光係数のシャープ化指数（無次元）。
///
/// 生のラプラシアンは滑らかな山なので、そのままだと「ぼんやり明るい」だけになる。
/// 累乗で低い値を潰して高い値だけ残すと、**輝線が細く鋭く走る**（コースティクス表現の定石）。
const CAUSTICS_SHARPNESS: f32 = 2.5;

/// 深度フェード距離の下限（m）。0 除算の防止。
const CAUSTICS_DEPTH_FADE_MIN: f32 = 1.0e-2;

/// 振幅正規化の下限（m）。
///
/// 集光係数は波の**形状**（振幅で正規化したラプラシアン）から作る。
/// 解析波の振幅が 0 でも波紋・岸波が高さ場に残ることがあるため、
/// 0 除算と過剰増幅をこの下限（5mm）で防ぐ。
const CAUSTICS_AMP_MIN: f32 = 5.0e-3;

/// 正規化後の集光形状に掛けるコントラスト係数（無次元）。
///
/// `focus = −∇²h · step² / 振幅` は「1 周期あたりの曲がり具合」を表す無次元量で、
/// 波長がステップに対して長いほど小さくなる（波長 λ の正弦波で `(2π·step/λ)²`）。
/// 既定ステップ 0.35m・数 m 級の波長で saturate 前に 0.3〜1.0 程度へ乗るよう 1.5 とした。
const CAUSTICS_CONTRAST: f32 = 1.5;

/// 出力ゲイン（無次元）。強度 1.0 のときに直達光を何倍まで増幅しうるかの目安。
/// 消費側（`lighting_eval.wgsl`）は `radiance × (1 + caustics)` で使う。
const CAUSTICS_OUTPUT_GAIN: f32 = 2.0;

/// 出力の上限（無次元）。
///
/// 波が急峻な瞬間（振幅を上げた／波紋が重なった）ではラプラシアンが跳ね上がり、
/// 増幅率が発散して白飛びしうる。物理的な上限ではなく**破綻防止のクランプ**である。
const CAUSTICS_MAX_GAIN: f32 = 4.0;

/// 影の屈折オフセットの上限（m。片側・XZ 各成分の絶対値）。
///
/// オフセットは「水面勾配 × 深さ × 屈折係数 × 誇張倍率」なので、深い水域で誇張倍率を
/// 大きくすると数十 m 級まで発散しうる。そこまでずらすと影が完全に別の場所の遮蔽物を
/// 拾い、CSM のカスケード境界も跨いで破綻する（物理的な上限ではなく**破綻防止のクランプ**）。
///
/// 【2.0m → 0.05m に絞った理由】現実の屈折ずれは水深 2m・勾配 0.05 でも 2〜3cm 程度
/// （下の ④ のコメント参照）。2m という逃げ幅は「誇張倍率をいくら上げても破綻しない」ための
/// 上限だったが、実際には**水中と誤判定されたピクセル**（海面 Y より低い陸上など）で影の
/// レイ原点を毎フレーム ±2m 振り回し、Play 中のチカチカの主因になっていた。物理値の 2 倍弱を
/// 逃げ幅とする 5cm なら、誤判定が残っても影のずれは知覚できない大きさに収まる。
const CAUSTICS_SHADOW_OFFSET_MAX_M: f32 = 0.05;

/// 影の屈折オフセットを出すのに必要な最小水深（m。水面 Y からの深さ）。
///
/// 【なぜ必要か】水中判定は「水域クアッドの XZ 内かつ水面 Y より下」なので、Ocean のように
/// XZ が巨大な水域では**海面 Y よりわずかに低い陸地に立つキャラクタ**まで水中扱いになる。
/// 岸辺・海抜以下の低地がこれに該当し、その影がフレームごとに揺れてチカチカに見えていた。
/// 「水面のすぐ下」は屈折ずれ自体もほぼゼロ（ずれ ∝ 深さ）なので、浅いところを捨てても
/// 演出上の損失はない。0.5m は「膝下の浅瀬では揺らさない」相当のしきい値。
///
/// **この判定が効くのは影オフセットだけ**で、集光（網目模様）の光は従来どおり水面直下から
/// 出す（30cm の水たまりでも模様は見えてほしいため）。
const CAUSTICS_SHADOW_MIN_DEPTH_M: f32 = 0.5;

/// `wave_axis.w` に入る「水域の下端までの深さ」の無効値（負＝下端なし＝Ocean・川）。
/// Rust 側 `WATER_SUBMERGE_DEPTH_UNBOUNDED`（water/params.rs）と一致させること。
const CAUSTICS_SUBMERGE_DEPTH_UNBOUNDED: f32 = -1.0;

// ─── フルスクリーン三角形 ────────────────────────────────────
//
// 3 頂点で画面全体を覆う巨大三角形（`deferred_lighting.wgsl` の vs_fullscreen と同一）。
// NDC 座標: (-1,-1) (3,-1) (-1,3)。UV は持たせず、フラグメントで
// `@builtin(position)` から画素座標を直接使う。
const CAUSTICS_FULLSCREEN_TRIANGLE_POS: array<vec2<f32>, 3> = array<vec2<f32>, 3>(
    vec2<f32>(-1.0, -1.0),
    vec2<f32>( 3.0, -1.0),
    vec2<f32>(-1.0,  3.0),
);

@vertex
fn vs_fullscreen(@builtin(vertex_index) vi: u32) -> @builtin(position) vec4<f32> {
    return vec4<f32>(CAUSTICS_FULLSCREEN_TRIANGLE_POS[vi], 0.0, 1.0);
}

// ─── フレームバッファ画素 → ビューポート相対 UV ───────────────
//
// **`deferred_lighting.wgsl` の `deferred_vp_uv_from_pix` と同一規約**（コピー）。
// `@builtin(position)` はフレームバッファ基準の画素座標だが、NDC `[-1,1]` が写像されるのは
// ビューポート矩形だけ（Play のレターボックスで set_viewport する矩形）である。
// **ここが deferred 側と食い違うと、レターボックス時にコースティクスの模様だけが
// 水底の上を横滑りする**（ライティングが読む座標と模様を焼いた座標がずれるため）。
// Edit モード・黒帯なしでは viewport = (0, 0, RT幅, RT高さ) となり従来式と完全に同値。
fn caustics_vp_uv_from_pix(pix: vec2<f32>) -> vec2<f32> {
    return (pix - u_camera.viewport.xy) / max(u_camera.viewport.zw, vec2<f32>(1.0, 1.0));
}

/// フレームバッファ画素＋深度からワールド座標を復元する。
/// wgpu の NDC は x:[-1,1] / y:[-1,1]（上が +1）/ z:[0,1]。
fn caustics_world_from_depth(pix: vec2<f32>, depth: f32) -> vec3<f32> {
    let uv   = caustics_vp_uv_from_pix(pix);
    let ndc  = vec3<f32>(uv.x * 2.0 - 1.0, 1.0 - uv.y * 2.0, depth);
    let clip = u_camera.inv_view_proj * vec4<f32>(ndc, 1.0);
    return clip.xyz / clip.w;
}

// ─── コースティクス生成フラグメント ──────────────────────────

/// 本パスの MRT 出力（2 枚）。
///
/// **2 枚に分けた理由（案 A の採用根拠）**
/// 影の屈折オフセットは「透過率 rgb ＋ 集光強度 a」で既に埋まっている RGBA には入らない。
/// 透過率を輝度＋色相へ圧縮して同居させる案（案 B）は、**水の色が乗算項として直達光に
/// 掛かる**（W5.3 の「色付きガラス」表現）ため、圧縮誤差がそのまま画面全体の色ズレになる。
/// 一方 Rg16Float の 2ch を足すコストはフル解像度でも 4 byte/px と G-Buffer 1 枚より軽い。
/// 精度と単純さの両方で 2 枚に分ける方が優れるため案 A を採った。
///
/// **クリア値 0 が中立**（オフセット無し＝従来と同一の影）である点が肝で、
/// 非水中ピクセル（discard）・機能 OFF のダミー 1x1・範囲外 textureLoad のいずれも
/// 0 を返す。加算項なので消費側に「無効時の分岐」が一切要らない。
struct CausticsOut {
    /// rgb = 水を通った平行光の透過率／a = 集光強度（従来と同一の意味）。
    @location(0) light: vec4<f32>,
    /// 影のサンプル位置に足すワールド XZ オフセット（m）。誇張倍率まで適用済み。
    @location(1) shadow_offset: vec2<f32>,
}

@fragment
fn fs_caustics(@builtin(position) frag: vec4<f32>) -> CausticsOut {
    let pix   = vec2<i32>(frag.xy);
    let depth = textureLoad(t_scene_depth, pix, 0);
    // 背景（空）は水中ではありえない。ターゲットのクリア値 0（＝寄与なし）を残す。
    if (depth >= CAUSTICS_BACKGROUND_DEPTH) {
        discard;
    }
    let world_pos = caustics_world_from_depth(frag.xy, depth);

    // ── ① このピクセルがどの水域の「水中」にあるかを特定する ────────────
    //    早期棄却の要。強度 0 の水域・矩形の外・水面より上は即座に捨てる。
    //    走査対象はクアッド系（Ocean / Region）のみ＝配列の先頭 counts.x 個。
    //    川リボンは「XZ 矩形」で表せないうえ、川底は形状が細く模様が見えないので対象外。
    var p: WaterParams;
    var found = false;
    // 水面からの深さ（m）。正なら水中。
    var d = 0.0;
    for (var i: u32 = 0u; i < u_caustics.counts.x; i = i + 1u) {
        let q = u_water[i];
        // 強度 0 ＝ この水域はコースティクス完全無効（ユーザーによる無効化手段）。
        if (q.caustics.x <= 0.0) {
            continue;
        }
        // XZ の矩形内か（Ocean はカメラ追従の巨大クアッド、Region は AABB 上面）。
        if (abs(world_pos.x - q.center.x) > q.half_extent.x
         || abs(world_pos.z - q.center.z) > q.half_extent.z) {
            continue;
        }
        // 水面（base plane の Y）より下か。頂点変位ぶんの誤差は無視する（既知の制限）。
        let below = q.center.y - world_pos.y;
        if (below <= 0.0) {
            continue;
        }
        // 水域の**下端**より上か（Region の AABB 判定。CPU 側 `water/query.rs::volume_contains`
        // の Region 規則「y <= surface_y かつ y >= bottom_y」と同一）。
        // `wave_axis.w` = 水面から下端までの深さ（m）。負値（Ocean・川）は「下端なし」。
        // これが無いと、池の**真下にある洞窟や地下室**まで水中扱いになっていた。
        let bottom_depth = q.wave_axis.w;
        if (bottom_depth >= 0.0 && below > bottom_depth) {
            continue;
        }
        p     = q;
        d     = below;
        found = true;
        break;
    }
    if (!found) {
        discard;
    }

    // ── ② 集光係数 ────────────────────────────────────────────
    //    物理的な根拠: 平行光が水面で屈折すると、光線は水面の傾きに比例して曲がる。
    //    隣り合う光線の間隔の変化率＝傾きの発散＝**高さ場のラプラシアン**であり、
    //    水中を距離 d 進む間に光線束は `−∇²h · d · K` の割合で収束（＝集光）する。
    //    負のラプラシアン（＝凸の峰）で光が集まる、というのが網目模様の正体である。
    let t = u_camera.time;
    // 細かさ倍率。大きいほどステップが縮み、高周波成分＝細かい網目を拾う。
    let s           = max(p.caustics.y, CAUSTICS_SCALE_MIN);
    let sample_step = CAUSTICS_BASE_STEP_M / s;
    // クアッドは川ではないので流れ項は効かない（`water_is_river` 分岐で無視される）。
    // それでも明示的にゼロを渡し、「川ではない」ことをコード上でも読めるようにする。
    let flow_dir = vec2<f32>(0.0, 0.0);

    // 屈折オフセット: この水底点へ届く光が通った水面位置は、真上から
    // 「水面勾配 × 深さ × 屈折係数」だけずれている（定数のコメント参照）。
    // ずらさないと模様が水深にかかわらず真上に貼り付き、平板に見える。
    let g0  = water_surface_gradient(p, world_pos.xz, flow_dir, t);
    let sxz = world_pos.xz - g0 * (d * CAUSTICS_REFRACT_K);

    // 5 点ラプラシアン（中央差分）。高さ場は解析関数ではない（波紋・岸波はテクスチャ由来）ので
    // 解析微分できず、差分で取るのが唯一の手である。
    let h0  = water_surface_height(p, sxz, flow_dir, t);
    let hxp = water_surface_height(p, sxz + vec2<f32>(sample_step, 0.0), flow_dir, t);
    let hxm = water_surface_height(p, sxz - vec2<f32>(sample_step, 0.0), flow_dir, t);
    let hzp = water_surface_height(p, sxz + vec2<f32>(0.0, sample_step), flow_dir, t);
    let hzm = water_surface_height(p, sxz - vec2<f32>(0.0, sample_step), flow_dir, t);
    let lap = (hxp + hxm + hzp + hzm - 4.0 * h0) / (sample_step * sample_step);

    // 集光の「形状」。負のラプラシアン（凸の峰）ほど強く集光する。
    //
    // 【物理式からの意図的な乖離】物理的な収束は `−∇²h · d · K` だが、この式は
    // 振幅×深さに比例するため、現実的なパラメータ（振幅 1〜2cm・波長数 m の池）では
    // ほぼゼロになり模様が見えない。ここでは**振幅とステップで正規化した無次元の形状**
    // を使い、「模様の見えやすさ」を波の物理量から切り離す。強さはユーザーの
    // caustics_intensity が一手に握る（深さの寄与はフェードのみに残す）。
    let amp   = max(p.wave.x, CAUSTICS_AMP_MIN);
    let focus = -lap * sample_step * sample_step / amp;
    // シャープ化（輝線を細く鋭く走らせるための定石）。負の側（発散＝影）は 0 に潰す。
    let c = pow(saturate(focus * CAUSTICS_CONTRAST), CAUSTICS_SHARPNESS);
    // 深度フェード。実際の水中でも集光は数 m で拡散して消える。
    let fade = exp(-d / max(p.caustics.z, CAUSTICS_DEPTH_FADE_MIN));

    // ── ③ 水を通った直達光の透過率（色付きガラスと同じ乗算項）────────
    //    水底へ届く光は水中を深さ d だけ通過して吸収されるので、水面シェーダーの
    //    吸収則（water_surface.wgsl ④）と**同じ式**で浅場色→深場色へ寄せた色を作り、
    //    「無色（=1）からその色へ、吸収が進むほど寄る」透過率にする:
    //      d = 0   → ほぼ vec3(1)（水面直下は染まらない）
    //      d 大    → deep_color（深場は深場色に沈んだ光だけが届く）
    //    同じ式を使うことで「水の色設定を変えれば光の色も一緒に変わる」が
    //    パラメータ追加なしで成立する。ライトの色・強さ・影は消費側
    //    （lighting_eval が直達光 radiance へ乗算）で自然に掛かる。
    let absorb_dist  = max(p.shallow_color.a, CAUSTICS_DEPTH_FADE_MIN);
    let absorb       = exp(-d / absorb_dist);
    let tint         = mix(p.deep_color.rgb, p.shallow_color.rgb, absorb);
    let transmission = mix(vec3<f32>(1.0), tint, 1.0 - absorb);

    // 波が急峻なときは lap が跳ね上がって増幅率が発散しうるので、上限で止める。
    // 集光の輝きにも同じ透過率が掛かる（消費側で radiance × T × (1 + strength)）ため、
    // ここでは無次元の強度だけを A へ格納する。
    let strength = min(c * fade * p.caustics.x * CAUSTICS_OUTPUT_GAIN, CAUSTICS_MAX_GAIN);

    // ── ④ 影の屈折オフセット（水中に落ちる影を水面のうねりで揺らす）────
    //    このピクセルへ届く直達光は、真上ではなく `sxz`（②で求めた屈折点）で水面を
    //    通っている。つまり「遮蔽物があるか」を判定すべきなのは真上の光線ではなく
    //    **sxz を通る光線**である。したがって影のサンプル位置を `sxz − world_pos.xz`
    //    だけずらせば、水面のうねりに同期して水中の影がゆらぐ。
    //
    //    現実の屈折ずれは（水深 2m・勾配 0.05 で 2〜3cm 程度と）目に見えないほど小さいため、
    //    ユーザーパラメータ `shadow_refraction_strength`（caustics.w）で誇張できるようにする。
    //    **0 なら完全にゼロ**＝クリア値と同値になり、影は従来と 1 ビットも変わらない。
    //    集光強度と違い深度フェード（fade）は掛けない。屈折そのものは深いほど強くなる現象で、
    //    「深いほど揺らぎが大きい」方が水中の見え方として正しいためである。
    //
    //    【水中判定の厳密化（追補）】上の①は「XZ 矩形内かつ水面より下（かつ水域の下端より上）」
    //    でしかない。Ocean は XZ が巨大なので、**海面 Y よりわずかに低い陸地**に立つキャラクタも
    //    ①を通ってしまう。時間駆動のオフセットがそこへ掛かると影のレイ原点が毎フレーム動き、
    //    Play 中のチカチカになる。そこで**オフセットに限り**最小水深を要求する
    //    （集光の光は浅瀬でも出したいので①のままにする＝この判定は光には掛けない）。
    //    条件を満たさなければ 0＝クリア値と同値＝影は従来と 1 ビットも変わらない。
    var shadow_offset = vec2<f32>(0.0, 0.0);
    if (d >= CAUSTICS_SHADOW_MIN_DEPTH_M) {
        shadow_offset = clamp(
            (sxz - world_pos.xz) * p.caustics.w,
            vec2<f32>(-CAUSTICS_SHADOW_OFFSET_MAX_M),
            vec2<f32>( CAUSTICS_SHADOW_OFFSET_MAX_M),
        );
    }

    var out: CausticsOut;
    out.light         = vec4<f32>(transmission, strength);
    out.shadow_offset = shadow_offset;
    return out;
}
