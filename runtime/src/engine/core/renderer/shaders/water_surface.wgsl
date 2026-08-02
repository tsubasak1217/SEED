// ============================================================
//  water_surface.wgsl — 水面描画パス（Phase W1）
//
//  ## 役割（単一責任）
//  水面の格子（`water_height_field.wgsl` が生成）を描き、
//  「シーン深度から求めた水の厚み」による吸収・岸フォーム・フレネル・スクリーンスペース屈折を
//  **このシェーダ内で最終合成**して HDR へ出力する。
//
//  ## 形状と高さ場は共有モジュールが持つ
//  頂点位置の生成（格子・放射状ワープ・川リボン）と高さ場（解析波・波紋・岸波）は
//  **`water_height_field.wgsl` に切り出してある**。ID パス（`water_id.wgsl`）が
//  同じファイルを連結して同じ関数を呼ぶことで、見た目とピック形状が
//  構造的に食い違わないようにしてある（Phase W5.1）。
//  本ファイルが持つのは「水面 1 ピクセルの色をどう作るか」だけである。
//
//  ## 頂点バッファを持たない理由
//  水面のポリゴンはパラメータから完全に決まる（Ocean = カメラ追従の巨大格子／
//  Region = AABB 上面の格子／川 = リボンの 1 分割の格子）。
//  よって頂点バッファは一切使わず、`@builtin(vertex_index)` で格子セルと角を生成し、
//  `@builtin(instance_index)` でストレージバッファのパラメータ配列を引く。
//
//  ## 頂点変位（Phase W5.1）
//  頂点シェーダは `water_surface_height()` を呼んで Y を変位させる。
//  フラグメントの法線とまったく同じ高さ場なので、シルエットと陰影がズレない。
//  波を解像できないほど格子が粗い所（＝遠景）では変位を 0 へ落とすため、
//  遠景の見た目は W5.1 以前と変わらない（平面＋法線の波）。
//
//  ## 水中から見上げた水面（Phase W7）
//  カメラが水面より下にあるフラグメントは「水面の裏側」を見ている。パイプラインは
//  `cull_mode = None` なので裏面もラスタライズされており、修正が要るのは合成側だけである。
//    ・**厚み 0（素通し）**にする … 水面の向こう側は空気であって水ではないので、シーン深度
//      から求めた距離を厚みとして使ってはいけない。
//    ・**法線を反転**して視線側へ向け、フレネルをそのまま評価する。
//    ・**泡を出さない** … 泡は水面の上に浮くものであり、かつ厚み 0 では岸フォームが
//      「水深 0」と誤判定されて全面が白くなるため。
//
//  ## 深度の扱い（アタッチメント無し＋手動深度テスト）
//  本パスは **深度アタッチメントを一切持たない**（TOML の `no_depth = true`）。
//  共有深度テクスチャは他パスで「書き込み可能」としてアタッチされており、
//  同一パス内でアタッチメントとサンプルテクスチャに同時バインドすると
//  エイリアシング（read/write 競合）になるためである。DepthOnly ビューを
//  **サンプルテクスチャとして** group1 に受け取って `textureLoad` で読み、
//  「シーン深度 < 水面フラグメントの深度なら遮蔽 → discard」という手動深度テストを行う。
//  同じ 1 回の深度サンプルから **水の厚み** も復元でき、吸収と岸フォームに使い回せる。
//
//  ## 屈折の背景
//  本パス直前に「シーン HDR をそのままコピーした 1 ミップのグラブテクスチャ」を作り、
//  それを `t_scene` として読む。グラブはメインパス・WBOIT 合成の後に取るため、
//  スカイボックスも既存半透明も背景に含まれる。
//
//  ## バインディング規約
//    group 0: カメラ（CameraUniform, binding 0）
//    group 1: binding0 = 水パラメータ配列（**共有モジュールが宣言**）
//             binding1 = シーンカラーのグラブ（屈折の背景。本ファイル）
//             binding2 = そのサンプラー（線形・ClampToEdge。本ファイル）
//             binding3 = シーン深度（DepthOnly ビュー。textureLoad のみ。本ファイル）
//             binding4/5 = ショアフィールド配列とサンプラー（**共有モジュール**）
//    group 2: インタラクションフィールド一式（**共有モジュール**）
// ============================================================

// ─── Group 0: カメラ ─────────────────────────────────────────
//
// `shader_common.wgsl` は連結しない（マテリアル group2 等を要求してしまうため）。
// deferred_lighting.wgsl と同じ方針で、Rust 側 `uniforms::CameraUniform`（304 bytes）と
// 1:1 対応する同一レイアウトの構造体をここで自前宣言する。
// フィールドを 1 つでも増減すると全パスのカメラ BindGroup と食い違うため、
// Rust 側 / shader_common.wgsl / deferred_lighting.wgsl と併せて同期すること。
struct CameraUniform {
    view_proj:      mat4x4<f32>,
    view:           mat4x4<f32>,
    position:       vec3<f32>,
    /// ゲーム内累計時間（秒）。波の位相スクロールに使う（Play 非ポーズ時のみ進む）。
    time:           f32,
    /// レンダーターゲット全面の解像度（ピクセル）。グラブテクスチャのアドレッシングに使う。
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

// ─── Group 1: 屈折の背景と深度（水パラメータ・岸波は共有モジュール）─
@group(1) @binding(1) var t_scene: texture_2d<f32>;
@group(1) @binding(2) var s_scene: sampler;
@group(1) @binding(3) var t_depth: texture_depth_2d;

// ─── 合成専用の定数（マジックナンバー禁止のため全て命名する）───

/// 「シーン深度がここ以上なら遠クリップ（＝空／何も無い）」とみなす閾値。
/// 深度は `Clear(1.0)` 起点・比較 LessEqual の通常 Z なので、1.0 近傍が空にあたる。
const WATER_SKY_DEPTH_THRESHOLD: f32 = 0.999999;

/// 空に対する水の厚み（m）。実際には背景が無限遠なので「十分深い」として扱う。
const WATER_SKY_THICKNESS: f32 = 1.0e4;

/// 波紋フォームが「しきい値超過ぶん」で完全に白くなるまでの幅（しきい値に対する倍率）。
///
/// しきい値ちょうどで泡が突然出るとバンド状の縁が見えるため、
/// しきい値の 1 倍ぶんかけて滑らかに立ち上げる。
const WATER_RIPPLE_FOAM_RAMP: f32 = 1.0;

/// 波紋フォームの最大濃度（0..1）。既存の岸フォーム（foam_intensity）と重ねても
/// 白飛びしないよう、単体では 1.0 まで行かせない。
const WATER_RIPPLE_FOAM_MAX: f32 = 0.85;

/// 屈折 UV の有効範囲（画面外を舐めないようにクランプする）。
const WATER_UV_MIN: f32 = 0.0;
const WATER_UV_MAX: f32 = 1.0;

// ─── 深度まわりのヘルパー ────────────────────────────────────

/// フラグメント座標（フレームバッファ画素）＋深度からワールド座標を復元する。
///
/// NDC が写像されるのは **ビューポート矩形だけ**なので、RT 全面ではなく
/// `u_camera.viewport` で正規化する（Play のレターボックス時のズレ防止。
/// deferred_lighting.wgsl と同一の規約）。
fn water_world_from_depth(frag_xy: vec2<f32>, depth: f32) -> vec3<f32> {
    let vp = u_camera.viewport;
    let uv = (frag_xy - vp.xy) / max(vp.zw, vec2<f32>(WATER_EPSILON, WATER_EPSILON));
    // wgpu の NDC は x:[-1,1], y:[-1,1]（上が +1）, z:[0,1]。
    let ndc = vec3<f32>(uv.x * 2.0 - 1.0, 1.0 - uv.y * 2.0, depth);
    let p   = u_camera.inv_view_proj * vec4<f32>(ndc, 1.0);
    return p.xyz / p.w;
}

/// 指定のフレームバッファ画素のシーン深度を読む（範囲外はクランプ）。
fn water_load_depth(px: vec2<f32>) -> f32 {
    let dim = vec2<i32>(textureDimensions(t_depth, 0));
    let c   = clamp(vec2<i32>(px), vec2<i32>(0, 0), dim - vec2<i32>(1, 1));
    return textureLoad(t_depth, c, 0);
}

// ─── 頂点シェーダ ────────────────────────────────────────────

/// 頂点シェーダ出力。`idx` はフラグメントでパラメータ配列を引くためのインスタンス番号。
struct WaterVsOut {
    @builtin(position) clip:      vec4<f32>,
    @location(0)       world_pos: vec3<f32>,
    /// インスタンス番号（補間するとフラグメント間で混ざるため flat）
    @location(1) @interpolate(flat) idx: u32,
    /// 川の流れの向き（XZ。**関節タンジェントを頂点間で補間したもの**。Phase W6.2）。
    ///
    /// **あえて flat にしない**のが要点である。上流端の頂点には上流ノードの、
    /// 下流端の頂点には下流ノードのタンジェントを入れてあるので、
    /// 隣り合うリボン区間は共有する関節でまったく同じ値を持ち、区間境界で連続になる。
    /// 川以外のインスタンスではゼロ（未使用）。
    @location(2) flow_dir: vec2<f32>,
}

/// 頂点バッファ無しで水面の格子頂点を生成し、高さ場で変位させる（Phase W5.1）。
/// 形状生成の実体は共有モジュールの `water_grid_vertex`（ID パスと共有）。
@vertex
fn vs_water(
    @builtin(vertex_index)   vi: u32,
    @builtin(instance_index) ii: u32,
) -> WaterVsOut {
    let p = u_water[ii];
    let v = water_grid_vertex(p, vi, u_camera.time);

    var out: WaterVsOut;
    out.clip      = u_camera.view_proj * vec4<f32>(v.world, 1.0);
    out.world_pos = v.world;
    out.idx       = ii;
    out.flow_dir  = v.flow_dir;
    return out;
}

// ─── フラグメントシェーダ ────────────────────────────────────

/// 水面 1 フラグメントを合成する。処理順は
/// 波法線 → 水中判定 → 手動深度テスト → 厚み復元 → 吸収 → 屈折 → 岸フォーム → フレネル → 合成。
@fragment
fn fs_water(in: WaterVsOut) -> @location(0) vec4<f32> {
    let p = u_water[in.idx];

    // ① 波法線（ワールド空間、上向き基準）
    //    解析波（W1）・波紋（I2）・岸波（W1.5）の **勾配を合算してから 1 回だけ正規化**する。
    //    高さ場の重ね合わせ = 勾配の重ね合わせ、という関係に乗った合成であり、
    //    法線を 3 つ作って混ぜるより物理的に正しい。
    //    波紋強度 0／岸波強度 0 の水では該当項が完全に消え、W1 と 1 ビットも変わらない。
    let ripple_h = water_ripple_height(in.world_pos.xz);
    let grad     = water_surface_gradient(p, in.world_pos.xz, in.flow_dir, u_camera.time);
    let n_up     = water_normal_from_gradient(grad);

    // ①' 水中判定（Phase W7）
    //     カメラがこのフラグメントの水面高さより **下** にあるなら、見えているのは
    //     水面の裏側（＝水中から見上げている）。パイプラインは `cull_mode = None` なので
    //     裏面もラスタライズされており、必要なのは「合成の向き」を反転することだけである。
    //     判定に `in.world_pos.y` を使うのは、それが実際にラスタライズされた面の高さそのもので
    //     あるため（W5.1 で頂点が変位した後も、この値は変位後の面の高さなので正しく効く）。
    let underwater = u_camera.position.y < in.world_pos.y;
    //     視線側を向く法線。水中では上向き法線を反転して使う（フレネル・屈折の歪み方向とも
    //     整合させるため、以降は一貫してこの `n` だけを使う）。
    let n = select(n_up, -n_up, underwater);

    // ② 手動深度テスト（深度アタッチメントを持たないため自前で行う）
    //    シーン深度が水面フラグメントより手前なら、水は不透明物に隠れている。
    let scene_depth = water_load_depth(in.clip.xy);
    if (scene_depth < in.clip.z) {
        discard;
    }

    // ③ 水の厚み（水面点 → 水底/背景点までの距離）
    var thickness: f32;
    if (underwater) {
        // 水中から見上げている場合、水面の「向こう側」は水ではなく **外の空気／空** であり、
        // シーン深度から求まる距離は水の厚みではない（そのまま使うと最大厚み扱いになり、
        // 深場の色で塗り潰されて外の景色が一切見えなくなる）。
        // よって厚み 0 として **素通し**にする（absorb=1 → opacity=0 → 背景グラブが出る）。
        thickness = 0.0;
    } else if (scene_depth >= WATER_SKY_DEPTH_THRESHOLD) {
        // 背景が空（無限遠）＝ 水底が無い。最大厚みとして扱い、深場の色へ収束させる。
        thickness = WATER_SKY_THICKNESS;
    } else {
        let bottom = water_world_from_depth(in.clip.xy, scene_depth);
        thickness  = distance(bottom, in.world_pos);
    }

    // ④ 吸収（Beer-Lambert 近似）: 厚いほど deep_color へ寄る。
    let absorb_dist = max(p.shallow_color.a, WATER_EPSILON);
    let absorb      = exp(-thickness / absorb_dist);
    let tint        = mix(p.deep_color.rgb, p.shallow_color.rgb, absorb);

    // ⑤ 屈折（スクリーンスペース）: 波法線の XZ で背景 UV をずらしてグラブを読む。
    let base_uv = in.clip.xy / max(u_camera.resolution, vec2<f32>(WATER_EPSILON, WATER_EPSILON));
    let warp_uv = clamp(
        base_uv + n.xz * p.wave.w,
        vec2<f32>(WATER_UV_MIN, WATER_UV_MIN),
        vec2<f32>(WATER_UV_MAX, WATER_UV_MAX),
    );
    // 歪んだ先のシーン深度が水面より **手前** なら、そこは水より前にある物体（岸・手前のオブジェクト）で、
    // その色を水中の背景として拾うと岸際で背景が滲む典型バグになる。その場合は歪み無し UV へ戻す。
    let warp_depth = water_load_depth(warp_uv * u_camera.resolution);
    var refract_uv = warp_uv;
    if (warp_depth < in.clip.z) {
        refract_uv = base_uv;
    }
    let background = textureSampleLevel(t_scene, s_scene, refract_uv, 0.0).rgb;

    // ⑥ 最終合成: 深いほど背景を隠す（surface_opacity が最大被覆率）。
    let opacity = clamp(p.deep_color.a, 0.0, 1.0) * (1.0 - absorb);
    var color   = mix(background, tint, opacity);

    // ⑦ 泡の全体ゲート（Phase W7）
    //    泡（岸フォーム・航跡・砕け波）はいずれも **水面の上側に浮くもの**なので、
    //    水中から見上げているときは載せない。特に岸フォームは thickness=0（素通し）だと
    //    「水深 0 = 岸際」と誤判定されて画面全面が白く塗られてしまうため、この
    //    ゲートは水中視点における必須の抑制である。
    let foam_gate = select(1.0, 0.0, underwater);

    // ⑦-a 岸フォーム: 水深が foam_width 未満の帯に滑らかに乗せる。
    let foam_width = max(p.foam_color.a, WATER_EPSILON);
    let foam       = (1.0 - smoothstep(0.0, foam_width, thickness))
                   * p.reflection_color.a * foam_gate;
    color          = color + p.foam_color.rgb * foam;

    // ⑦' 航跡フォーム（Phase I2）: 波紋の高さがしきい値を超えた所へ白い泡を乗せる。
    //     岸フォームと **同じ foam_color** を流用する（水ごとの泡の色は 1 つ、という設計）。
    let ripple_threshold = max(p.fresnel.w, WATER_EPSILON);
    let ripple_foam = smoothstep(
        ripple_threshold,
        ripple_threshold * (1.0 + WATER_RIPPLE_FOAM_RAMP),
        abs(ripple_h),
    ) * WATER_RIPPLE_FOAM_MAX * clamp(p.fresnel.z, 0.0, 1.0) * foam_gate;
    color = color + p.foam_color.rgb * ripple_foam;

    // ⑦'' 岸波の泡（Phase W1.5）: 砕け波の白帯と打ち上げ（swash）の泡。
    //      ここも **同じ foam_color** を流用する。
    let shore_foam = water_shore_foam(p, in.world_pos.xz, u_camera.time) * foam_gate;
    color = color + p.foam_color.rgb * shore_foam;

    // ⑧ フレネル: 浅い角度ほど反射色へ寄せる。
    //    `n` は視線側を向く法線（水中では下向き）なので、この 1 本の式が
    //    水上＝空の反射、水中＝水面の内側での反射（全反射の粗い代用）の両方を兼ねる。
    let view_dir = normalize(u_camera.position - in.world_pos);
    let fresnel  = clamp(
        p.fresnel.y * pow(1.0 - clamp(dot(n, view_dir), 0.0, 1.0), max(p.fresnel.x, WATER_EPSILON)),
        0.0, 1.0,
    );
    color = mix(color, p.reflection_color.rgb, fresnel);

    // 背景を自前で合成済みなので、そのまま不透明（alpha=1）で書き出す（ブレンドは Replace）。
    return vec4<f32>(color, 1.0);
}
