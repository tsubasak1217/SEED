// ============================================================
//  water_reflection_common.wgsl — 水面反射パス共通定義（Phase W5.2）
//
//  ## 役割（単一責任）
//  「水面 1 ピクセルの反射色をどう求めるか」の **入口まで**（＝どのワールド点を
//  どの向きに反射するか）だけを持つ共有モジュール。実際に色を取ってくる方法は
//  変種ファイルが担う:
//    ・`water_reflection_rt.wgsl`  … TLAS へレイを飛ばすハイブリッド RT（D6 の流用）
//    ・`water_reflection_ssr.wgsl` … スクリーンスペースマーチ（RT 非対応 GPU 用）
//
//  ## なぜ「水面用の独立パス」なのか
//  水面パイプライン（`water_surface.wgsl`）は group0〜2 を既に使っており、
//  RT 反射に必要な TLAS・ライト・GI プローブ（＋シーンカラー／深度）を足すと
//  **バインドグループ上限 5 を超える**。そこで反射だけを別パスへ切り出し、
//  水面シェーダは結果 RT を **1 枚 textureLoad するだけ**にしてある。
//  この分担は不透明側（D6: `reflection_rt.wgsl` → `reflection_composite.wgsl`）と同じ。
//
//  ## 形状は水面パスと完全に同一
//  頂点生成は `water_height_field.wgsl` の `water_grid_vertex()` を呼ぶだけである
//  （水面パス・ID パスと同じ関数）。したがって本パスと水面パスの
//  **ラスタライズ結果は 1 ピクセル単位で一致**し、水面シェーダ側は
//  補間ではなく `textureLoad`（＝1:1 の読み出し）で結果を拾える。
//
//  ## バインディング規約
//    group 0: カメラ（CameraUniform, binding0）
//    group 1: 水パラメータ配列＋ショアフィールド（**共有モジュールが宣言**）
//    group 2: インタラクションフィールド（**共有モジュールが宣言**）
//    group 3: シーンカラーのグラブ・そのサンプラー・シーン深度（本ファイル）
//    group 4: GI プローブ＋ライトメタ（本ファイル）／RT 変種のみ TLAS 等を追加宣言
//
//  ## 深度アタッチメントを持たない理由
//  水面パス（`water_surface.wgsl`）とまったく同じで、共有深度は他パスで
//  書き込み可能としてアタッチされており、同一パスでアタッチメント兼サンプルに
//  すると read/write 競合になる。よって深度は group3 のサンプルテクスチャとして
//  読み、「シーン深度が水面より手前なら遮蔽 → discard」の手動深度テストを行う。
// ============================================================

// ─── Group 0: カメラ ─────────────────────────────────────────
//
// Rust 側 `uniforms::CameraUniform`（304 bytes）と 1:1 対応する同一レイアウト。
// water_surface.wgsl の宣言と 1 バイトもズレてはならない
//（同じ BindGroup を共有するため）。
struct CameraUniform {
    view_proj:      mat4x4<f32>,
    view:           mat4x4<f32>,
    position:       vec3<f32>,
    /// ゲーム内累計時間（秒）。**水面パスと同じ値**であること
    /// （ズレると「見えている波」と「反射を計算した波」が別の時刻になる）。
    time:           f32,
    /// レンダーターゲット全面の解像度（ピクセル）。
    resolution:     vec2<f32>,
    _pad2:          vec2<f32>,
    /// 逆 ViewProjection（シーン深度 → ワールド座標の復元用）。
    inv_view_proj:  mat4x4<f32>,
    /// 速度バッファ用の前フレーム ViewProjection（本パスでは未使用。オフセット合わせ）。
    prev_view_proj: mat4x4<f32>,
    /// フレームバッファ基準のビューポート矩形 (x, y, w, h)。
    /// **NDC はこの矩形へ写像される**ため、深度 → ワールド復元・ワールド → UV 射影の
    /// 双方でこの矩形を使う（Play のレターボックス時のズレ防止）。
    viewport:       vec4<f32>,
}
@group(0) @binding(0) var<uniform> u_camera: CameraUniform;

// ─── Group 3: シーンカラー（グラブ）とシーン深度 ─────────────
//
// `t_water_scene` は **水面を描く直前にコピーした不透明＋スカイボックスのシーン HDR**
// （`WaterRenderer::record_grab` が作る屈折用グラブと同一のテクスチャ）。
// 屈折の背景と同じものを反射でも使うことで、コピーは 1 フレーム 1 回のままである。
// **水面自身は含まれない**ので、反射が水面を映して自己参照する事故は構造的に起きない。
@group(3) @binding(0) var t_water_scene: texture_2d<f32>;
@group(3) @binding(1) var s_water_scene: sampler;
@group(3) @binding(2) var t_water_depth: texture_depth_2d;

// ─── Group 4（共通部）: GI プローブとライトメタ ───────────────
//
// レイ／マーチが何にも当たらなかったときのフォールバック（環境光）に使う。
// RT 変種はこの後ろ（binding 5..7）へ TLAS・ライト・アルベドを追加宣言する。
// `GiParams` / `ddgi_sample_irradiance` は `ddgi_common.wgsl` が定義する。
@group(4) @binding(0) var<uniform> wr_gi:      GiParams;
@group(4) @binding(1) var          wr_gi_irr:  texture_2d<f32>;
@group(4) @binding(2) var          wr_gi_vis:  texture_2d<f32>;
@group(4) @binding(3) var          wr_gi_samp: sampler;

/// ライトメタ（アンビエント色・強度）。`lighting.rs::LightMeta`（32B）のミラーで、
/// `reflection_rt.wgsl::LightMetaR` と同一レイアウト。**GI 無効時のフォールバック色**に使う。
/// storage で読むのは不透明 RT 反射（`reflection_rt.wgsl`）と同じ流儀に揃えるため
/// （同じバッファを同じ型で読む経路が 2 種類あると同期漏れの温床になる）。
struct WaterReflMeta {
    count:             u32,
    rt_shadows:        u32,
    view_mode:         u32,
    _pad2:             u32,
    ambient_color:     vec3<f32>,
    ambient_intensity: f32,
}
@group(4) @binding(4) var<storage, read> wr_meta: WaterReflMeta;

// ─── 定数（マジックナンバー禁止のためすべて命名する）───────────

/// 「シーン深度がここ以上なら遠クリップ（＝空／何も無い）」とみなす閾値。
/// `water_surface.wgsl::WATER_SKY_DEPTH_THRESHOLD` と同値であること。
const WATER_REFL_SKY_DEPTH: f32 = 0.999999;

/// 反射レイが「空へ抜けた」とみなして画面へ射影しなおす距離（m）。
/// ミス時に **グラブへ描かれているスカイボックス**を拾えるなら拾うためのもので、
/// 十分遠ければ値そのものに意味は無い（射影先が画面内かどうかだけが効く）。
const WATER_REFL_SKY_DISTANCE_M: f32 = 5.0e3;

/// 波のぼけ（`reflection_roughness`）で法線を傾ける最大量（正接方向の倍率）。
///
/// 1 ピクセル 1 レイなので、粗さは「ぼけ」ではなく **散り**として出る。
/// 0.2 は「水面が細かくざらついて反射像が滲む」ところで、これ以上大きくすると
/// 反射がノイズにしか見えなくなる（＝上限として意味のある値）。
const WATER_REFL_JITTER_MAX: f32 = 0.2;

/// 粗さジッタのハッシュ格子の細かさ（セル/m）。
///
/// ワールド座標で決めるので、カメラが動いてもジッタ模様が画面内を這わない
/// （＝スクリーン空間ハッシュにありがちなちらつきが出ない）。
/// 4 セル/m は 25cm 角で、水面のさざ波と同じくらいの空間スケール。
const WATER_REFL_JITTER_FREQ: f32 = 4.0;

// ─── 頂点シェーダ ────────────────────────────────────────────

/// 頂点シェーダ出力（`water_surface.wgsl::WaterVsOut` と同じ意味・同じ生成元）。
struct WaterReflVsOut {
    @builtin(position) clip:      vec4<f32>,
    @location(0)       world_pos: vec3<f32>,
    /// インスタンス番号（補間するとフラグメント間で混ざるため flat）
    @location(1) @interpolate(flat) idx: u32,
    /// 川の流れの向き（XZ。関節タンジェントの頂点間補間。川以外はゼロ）
    @location(2) flow_dir: vec2<f32>,
}

/// 水面の格子頂点を生成する（`water_surface.wgsl::vs_water` と**同一の共有関数**を呼ぶ）。
@vertex
fn vs_water_reflection(
    @builtin(vertex_index)   vi: u32,
    @builtin(instance_index) ii: u32,
) -> WaterReflVsOut {
    let p = u_water[ii];
    let v = water_grid_vertex(p, vi, u_camera.time);

    var out: WaterReflVsOut;
    out.clip      = u_camera.view_proj * vec4<f32>(v.world, 1.0);
    out.world_pos = v.world;
    out.idx       = ii;
    out.flow_dir  = v.flow_dir;
    return out;
}

// ─── 座標変換ヘルパー（不透明反射パスと同一規約）──────────────

/// 指定のフレームバッファ画素のシーン深度を読む（範囲外はクランプ）。
fn water_refl_load_depth(px: vec2<f32>) -> f32 {
    let dim = vec2<i32>(textureDimensions(t_water_depth, 0));
    let c   = clamp(vec2<i32>(px), vec2<i32>(0, 0), dim - vec2<i32>(1, 1));
    return textureLoad(t_water_depth, c, 0);
}

/// ワールド座標のビュー空間 Z（カメラ前方が負）。深度一致判定に使う。
fn water_refl_view_z(world_pos: vec3<f32>) -> f32 {
    return (u_camera.view * vec4<f32>(world_pos, 1.0)).z;
}

/// ワールド → 画面の射影結果（**RT 全面基準**の UV と画面内フラグ）。
struct WaterReflProj { uv: vec2<f32>, valid: bool }

/// ワールド座標を view_proj で画面 UV へ射影する（`reflection_rt.wgsl::rt_refl_project` と同式）。
/// 画面内判定は **ビューポート相対**で行い（Play の黒帯の外は画面外）、
/// 返す UV は **RT 全面基準**に直す（深度・グラブはどちらも RT 全面で作られるため）。
fn water_refl_project(world_pos: vec3<f32>) -> WaterReflProj {
    var r: WaterReflProj;
    let clip = u_camera.view_proj * vec4<f32>(world_pos, 1.0);
    if clip.w <= 0.0 {
        r.uv    = vec2<f32>(0.0, 0.0);
        r.valid = false;
        return r;
    }
    let ndc   = clip.xyz / clip.w;
    let uv_vp = vec2<f32>(ndc.x * 0.5 + 0.5, 0.5 - ndc.y * 0.5);
    r.valid   = all(uv_vp >= vec2<f32>(0.0, 0.0)) && all(uv_vp <= vec2<f32>(1.0, 1.0));
    let pix   = u_camera.viewport.xy + uv_vp * u_camera.viewport.zw;
    r.uv      = pix / max(vec2<f32>(textureDimensions(t_water_scene)), vec2<f32>(1.0, 1.0));
    return r;
}

/// 深度（0..1 非線形）と **RT 全面基準 UV** からワールド座標を復元する。
/// `deferred_lighting.wgsl` / `water_surface.wgsl` と同一の規約
/// （NDC が写像されるのはビューポート矩形だけ）。
fn water_refl_world_from_depth(uv_rt: vec2<f32>, depth: f32) -> vec3<f32> {
    let pix   = uv_rt * vec2<f32>(textureDimensions(t_water_scene));
    let uv_vp = (pix - u_camera.viewport.xy) / max(u_camera.viewport.zw, vec2<f32>(1.0, 1.0));
    let ndc   = vec3<f32>(uv_vp.x * 2.0 - 1.0, 1.0 - uv_vp.y * 2.0, depth);
    let p     = u_camera.inv_view_proj * vec4<f32>(ndc, 1.0);
    return p.xyz / p.w;
}

// ─── フォールバック（レイが何にも当たらなかったとき）───────────

/// 反射方向の環境光。GI 有効なら DDGI プローブ、無効ならフラットアンビエント。
/// 不透明 RT 反射の `rt_refl_fallback` と同じ分岐方針である。
fn water_refl_env(world_pos: vec3<f32>, dir: vec3<f32>) -> vec3<f32> {
    if wr_gi.enabled != 0u {
        return ddgi_sample_irradiance(wr_gi, world_pos, dir, wr_gi_irr, wr_gi_vis, wr_gi_samp);
    }
    return wr_meta.ambient_color * wr_meta.ambient_intensity;
}

/// 反射方向のはるか先を画面へ射影し、そこが **背景（空）として描かれている**なら
/// グラブ（＝スカイボックス込みのシーンカラー）をサンプルして返す。
///
/// 水面反射で一番よく起きるミスは「レイが空へ抜けた」であり、そこを環境光の
/// 平均色で塗ると空の色・雲・夕焼けが一切映らない。画面内に空が写っている限り、
/// この経路で **本物のスカイボックス色**を拾える（水平線近くの反射で効く）。
/// 画面外なら `valid=false` を返し、呼び出し側が環境光へ落とす。
struct WaterReflSky { color: vec3<f32>, valid: bool }
fn water_refl_screen_sky(origin: vec3<f32>, dir: vec3<f32>) -> WaterReflSky {
    var s: WaterReflSky;
    s.color = vec3<f32>(0.0, 0.0, 0.0);
    s.valid = false;
    let proj = water_refl_project(origin + dir * WATER_REFL_SKY_DISTANCE_M);
    if !proj.valid {
        return s;
    }
    // その画素が背景（遠クリップ）＝空であること。手前に物があるなら空ではない。
    let d = water_refl_load_depth(proj.uv * vec2<f32>(textureDimensions(t_water_scene)));
    if d < WATER_REFL_SKY_DEPTH {
        return s;
    }
    s.color = textureSampleLevel(t_water_scene, s_water_scene, proj.uv, 0.0).rgb;
    s.valid = true;
    return s;
}

// ─── 水面フラグメントの下ごしらえ（両変種で共有）────────────────

/// 反射計算に必要な水面 1 点の情報。
struct WaterReflSurface {
    /// 水面のワールド座標（頂点変位後）。
    world_pos: vec3<f32>,
    /// 視線側を向く波法線（水中視点では反転済み）。
    n:         vec3<f32>,
    /// 反射方向（粗さジッタ適用済み）。
    r:         vec3<f32>,
    /// 反射の全体強度（`WaterVolume::reflection_intensity`。0 で無効）。
    intensity: f32,
    /// この水面ピクセルが不透明物に遮蔽されているか（true なら呼び出し側で discard）。
    occluded:  bool,
}

/// 粗さで法線を傾ける（波によるぼけ）。
///
/// 1 ピクセル 1 レイなので多方向を平均できない。代わりに **ワールド座標のハッシュ**で
/// 法線を接空間内にわずかに傾け、隣り合うピクセルが少しずつ違う方向を見るようにする
/// （＝反射像が散って滲む）。ハッシュ格子はワールド固定なのでカメラが動いても模様が這わない。
/// `roughness = 0` なら `n` をそのまま返す（＝完全な鏡面で、旧来の見た目に戻せる）。
fn water_refl_jitter_normal(n: vec3<f32>, world_xz: vec2<f32>, roughness: f32) -> vec3<f32> {
    if roughness <= 0.0 {
        return n;
    }
    let cell = vec2<i32>(floor(world_xz * WATER_REFL_JITTER_FREQ));
    let h    = water_noise_hash2(cell); // −1..1 の 2 チャンネル
    // 法線に直交する接空間の基底。水面法線はほぼ上向きなので +X 基準で安定する
    //（真横を向くことは無いため、外積が縮退する心配は無い）。
    let t = normalize(cross(n, vec3<f32>(1.0, 0.0, 0.0)) + vec3<f32>(0.0, 0.0, WATER_EPSILON));
    let b = cross(n, t);
    let k = clamp(roughness, 0.0, 1.0) * WATER_REFL_JITTER_MAX;
    return normalize(n + (t * h.x + b * h.y) * k);
}

/// 水面フラグメントの共通の下ごしらえ（波法線・水中判定・手動深度テスト・反射方向）。
///
/// **`water_surface.wgsl::fs_water` の①〜②と同じ手順**を踏むこと自体が要件である
/// （法線が食い違うと「見えている波」と「反射している波」がズレる）。
fn water_refl_surface(in: WaterReflVsOut) -> WaterReflSurface {
    let p = u_water[in.idx];

    var s: WaterReflSurface;
    s.world_pos = in.world_pos;

    // ① 波法線（解析波＋波紋＋岸波の勾配を合算してから 1 回だけ正規化）。
    let grad = water_surface_gradient(p, in.world_pos.xz, in.flow_dir, u_camera.time);
    let n_up = water_normal_from_gradient(grad);

    // ①' 水中判定（水面の裏側を見ているなら法線を反転する。water_surface と同一条件）。
    let underwater = u_camera.position.y < in.world_pos.y;
    let n          = select(n_up, -n_up, underwater);

    // ② 手動深度テスト（深度アタッチメントを持たないため自前で行う）。
    //    シーン深度が水面フラグメントより手前なら、水は不透明物に隠れている。
    let scene_depth = water_refl_load_depth(in.clip.xy);
    s.occluded      = scene_depth < in.clip.z;

    // ③ 反射方向（粗さジッタ込み）。
    let v_dir = normalize(u_camera.position - in.world_pos);
    let n_j   = water_refl_jitter_normal(n, in.world_pos.xz, p.reflection.y);
    s.n       = n_j;
    s.r       = normalize(reflect(-v_dir, n_j));
    s.intensity = max(p.reflection.x, 0.0);
    return s;
}
