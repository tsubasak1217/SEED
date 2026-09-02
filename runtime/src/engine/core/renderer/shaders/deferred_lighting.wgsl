// ============================================================
// deferred_lighting.wgsl  —  フルスクリーン・ライティング復元（Phase D3 Deferred Phase A）
//
// ## 役割（単一責任）
// G-Buffer（gbuffer_write.wgsl が焼いた 4 枚の MRT + 深度）を読み、フラグメントごとに
// Surface を復元して evaluate_lighting（lighting_eval.wgsl, 既存・無改変）を呼ぶだけの
// フルスクリーン三角形パス。マテリアルのテクスチャ採取は行わない（G-Buffer 書き込み時に
// 済んでいる）ため、group 2（マテリアル 11 binding）を一切連結しない。
//
// ## 連結順序（rt_off 版。rt_on 版は rt_shadow_off → rt_shadow_on に差し替え）
//   ["cluster_common.wgsl", "pbr_common.wgsl", "light_common.wgsl", "shadow.wgsl", "rt_shadow_off.wgsl",
//    "surface.wgsl", "lighting_eval.wgsl", "deferred_lighting.wgsl"]
//   pbr_common.wgsl は lighting_eval.wgsl が使う distribution_ggx 等の PBR ヘルパー
//   （shader_common.wgsl から分離した共有定義。バインディングを持たないため安全に連結できる）。
// shader_common.wgsl と surface_gather.wgsl は**連結しない**（これが G-Buffer 化で
// group 2 を切り離す狙いそのもの）。そのため本ファイルは shader_common.wgsl の
// CameraUniform を流用できず、独自に同一レイアウトの CameraUniform を宣言する
// （下記 group 0）。フィールド・オフセットは Rust 側 uniforms::CameraUniform /
// shader_common.wgsl の正典と厳密に同期すること（3 箇所同時に直す）。
//
// ## バインディング規約
//   group 0: カメラ（CameraUniform, binding 0）
//   group 1: G-Buffer 入力（テクスチャ 5 枚 + サンプラー 1 個）
//   group 2, 3: 未使用（gap。TOML ビルダーが空 BGL で自動的に埋める）
//   group 4: ライト／シャドウ／クラスタ（light_common.wgsl 等が宣言。既存の
//            LightBuffer BindGroup をそのまま bind する＝レイアウト等価性に依拠）
// ============================================================

// ─── Group 0: カメラ（shader_common.wgsl の CameraUniform と同一レイアウト）───
//
// Rust 側 uniforms::CameraUniform（304 bytes）と 1:1 対応させること。
// 本パスが実際に使うのは position（視線ベクトル V の算出）と resolution・
// inv_view_proj・viewport（深度→ワールド座標復元）のみだが、レイアウト一致のため
// 全フィールドを宣言する。
struct CameraUniform {
    view_proj:      mat4x4<f32>,
    view:           mat4x4<f32>,
    position:       vec3<f32>,
    /// ゲーム内累計時間（秒）。旧 `_pad`（vec3 のアライン規則で必ず生じる 4 byte の
    /// 死に領域）を W0 で転用したもの。`ShadingSurface.time` の供給元であり、
    /// lighting_eval.wgsl がここから読んで契約へ写す。Play 非ポーズ時のみ進む。
    time:           f32,
    resolution:     vec2<f32>,
    _pad2:          vec2<f32>,
    inv_view_proj:  mat4x4<f32>,
    /// 速度バッファ用の前フレーム ViewProjection（本パスでは未使用。オフセット合わせ）。
    prev_view_proj: mat4x4<f32>,
    /// フレームバッファ基準のビューポート矩形 (x, y, w, h)。
    /// **NDC はこの矩形へ写像される**ため、深度→ワールド復元はこれで正規化する
    /// （Play のレターボックス時に RT 全面で正規化すると座標が横滑りする）。
    viewport:       vec4<f32>,
}
@group(0) @binding(0) var<uniform> u_camera: CameraUniform;

// ─── Group 1: G-Buffer 入力 ───────────────────────────────────
//
// フォーマットは Rust 側 renderer/gbuffer.rs の GBUFFER*_FORMAT 定数と一致させること。
// t_depth は texture_depth_2d（textureLoad で深度値のみ読む。比較サンプラー不要）。
// s_gbuffer は G-Buffer 自体には使わない想定だが、将来のフィルタ処理拡張に備えて
// non-filtering サンプラーとして受け取れるようにしておく（本パスは textureLoad のみ使用）。
@group(1) @binding(0) var t_gbuffer0: texture_2d<f32>;       // albedo.rgb + occlusion.a
@group(1) @binding(1) var t_gbuffer1: texture_2d<f32>;       // world normal.xyz
@group(1) @binding(2) var t_gbuffer2: texture_2d<f32>;       // metallic.r + roughness.g
@group(1) @binding(3) var t_gbuffer3: texture_2d<f32>;       // emissive.rgb (HDR)
@group(1) @binding(4) var t_depth:    texture_depth_2d;      // 深度（textureLoad 専用）
@group(1) @binding(5) var s_gbuffer:  sampler;               // 予約（本パスでは未使用）
// ── AO 入力（半解像度 AO をフル解像度 UV でバイリニアアップサンプル）───────────
// t_ao は AO パス（SSAO / RT-AO）の出力テクスチャ（.r に AO 値、1=遮蔽なし）。
// AO=Off 時は白 1x1（R=1）がバインドされ AO=1.0（無効果）になる。s_ao は Filtering
// （linear）サンプラーで、半解像度 AO をフル解像度へバイリニアアップサンプルする。
// gbuffer_bgl はこの 2 binding を含む 8 entry へ拡張済み（deferred.rs 参照）。
@group(1) @binding(6) var t_ao: texture_2d<f32>;
@group(1) @binding(7) var s_ao: sampler;
// ── SSGI 入力（半解像度スクリーンスペース GI を 1 フレーム遅延で読む）───────────
// t_ssgi は SSGI パス（ssgi_gen.wgsl＋いもす法カラーブラー）の**前フレーム**の出力
// （.rgb に 1 バウンス間接放射照度。ミスはフラットアンビエント色で埋め済み）。
// SSGI 非使用時（DDGI/フラット/未収束フレーム）はダミー 1x1 がバインドされる（GiParams.gi_mode で
// 分岐するため中身は参照されない）。s_ssgi は Filtering（linear）で半解像度→フル解像度のバイリニア。
// この group1 は deferred ライティング専用（フォワードの group1 とは別レイアウト）なので、
// 共有 evaluate_gi_ambient には SSGI テクスチャを持ち込まず、ここで採取して Surface.screen_gi へ渡す。
@group(1) @binding(8) var t_ssgi: texture_2d<f32>;
@group(1) @binding(9) var s_ssgi: sampler;
// ── シャドウマスク入力（Phase RT-Shadow-Denoise）: 半解像度 texture_2d_array（レイヤ=スロット）───
// deferred 専用。各レイヤ .rgb ＝そのソフト影ライトのデノイズ済み遮蔽率（色付き影込みの透過率）。
// マスク非対象フレーム（RT 影オフ・ソフト影 0 灯）はダミー 1x1×4（白＝遮蔽なし）がバインドされる。
// フル解像度化は fs_deferred 内の joint bilateral upsample（深度考慮の手動 4 テクセル補間）で行う。
@group(1) @binding(10) var t_shadow_mask: texture_2d_array<f32>;
// s_shadow_mask（Filtering サンプラー）は深度非考慮のバイリニアがフチをにじませていたため未使用に
// なった（textureLoad + 深度重みに置換）。ただし宣言は残す: gbuffer_bgl は WGSL の宣言済み全
// バインディングから自動導出される（pipeline_config::reflect_bgls）ため、削除すると create_gbuffer_bind_group
// が供給する binding 11 とレイアウトが食い違う。将来 binding を整理するなら Rust 側も同時に直すこと。
@group(1) @binding(11) var s_shadow_mask: sampler;
// ── 水中コースティクス入力（Phase W5.3）: フル解像度 1ch（R16Float）────────────────
// コースティクス生成パス（caustics.wgsl）が焼いた集光係数。**フル解像度で 1:1 対応**なので
// サンプラーは追加せず `textureLoad` で読む（バイリニア補間は輝線をにじませるだけで益がない）。
// 機能 OFF のフレームは 1x1 のダミー（黒）がバインドされるが、**WGSL 仕様では範囲外の
// textureLoad はゼロを返す**ため、1x1 でも全画面が caustics=0（＝増幅なし）になる。
// つまり「無効時に何を挿すか」でシェーダ側の分岐を増やす必要がない。
@group(1) @binding(12) var t_caustics: texture_2d<f32>;
// ── 影の屈折オフセット入力（Phase W5.3）: フル解像度 2ch（Rg16Float・単位 m）──────────
// コースティクス生成パスの 2 枚目の MRT。.xy ＝ 水中ピクセルの影サンプル位置に足す
// ワールド XZ オフセット（誇張倍率 shadow_refraction_strength 適用済み）。
// **加算項なので中立値は 0**。非水中（discard でクリア値 0 のまま）・機能 OFF の
// ダミー 1x1（ゼロ）・範囲外 textureLoad（WGSL 仕様でゼロ）がすべて「ずらさない」に
// 一致するため、透過率のような中立値への写し直しが要らない。
@group(1) @binding(13) var t_caustics_offset: texture_2d<f32>;

// ─── フルスクリーン三角形の頂点定数 ───────────────────────────
//
// 3 頂点で画面全体を覆う巨大三角形（クワッドの対角線分割より頂点シェーダ呼び出しが
// 少なく、エッジのブレンド境界も生じないため定番の手法）。
// NDC 座標: (-1,-1) (3,-1) (-1,3)。UV は不要（フラグメント側で @builtin(position) から
// 直接ピクセル座標→UV を復元するため、頂点出力に varying を持たせない）。
const FULLSCREEN_TRIANGLE_POS: array<vec2<f32>, 3> = array<vec2<f32>, 3>(
    vec2<f32>(-1.0, -1.0),
    vec2<f32>( 3.0, -1.0),
    vec2<f32>(-1.0,  3.0),
);

@vertex
fn vs_fullscreen(@builtin(vertex_index) vi: u32) -> @builtin(position) vec4<f32> {
    return vec4<f32>(FULLSCREEN_TRIANGLE_POS[vi], 0.0, 1.0);
}

// ─── ライティング復元フラグメント ─────────────────────────────

/// 深度クリア値。DepthStencil アタッチメントは 1.0 でクリアされる（renderer 既存の慣例）。
/// この値以上の深度＝「何も描かれていない背景ピクセル」とみなし、ライティングせず
/// discard する（既存のクリア色／スカイボックスをそのまま残すため）。
const DEFERRED_BACKGROUND_DEPTH: f32 = 1.0;

/// G-Buffer RT1.w に載る「authored 法線」フラグの判定しきい値。
///
/// ## 何のためのフラグか（草・地形の影がドット状ノイズに壊れる不具合の根治）
/// deferred のライティングは幾何法線 Ng を**深度バッファの画面微分**
/// （`cross(dpdx(world_pos), dpdy(world_pos))`）から復元する。これは隣接ピクセルが
/// 同一サーフェスの間は面法線として正しいが、**深度不連続**（草の細い刃の輪郭・地形の
/// シルエットや掘削穴のフチ）を跨ぐと微分が別サーフェスへ飛んで Ng が per-pixel に暴れる。
/// lighting_eval.wgsl の geo_gate は `dot(Ng,L)>0` の**ハードな 0/1** なので、この暴れが
/// そのままディザ状のドットノイズ／ビュー依存の黒斑点になる（実機の層分離可視化で確定）。
/// さらに草は G-Buffer 法線を意図的に地表向き（up）へ平坦化しているのに、深度復元 Ng は
/// 刃の真の（水平・株ごとに別方位の）向きを拾うため、約半数の刃を直接光 0 に落とす。
///
/// ## フラグの意味
/// 書き込み側（grass_gbuffer.wgsl / terrain_gbuffer_write.wgsl）が RT1.w=1 を立てた
/// サーフェス＝「G-Buffer 法線 N そのものが信頼できる幾何法線（authored）で、深度復元 Ng
/// より優先すべき」もの。これらは薄い一枚布のような裏面光漏れの risk が無い（草は両面を
/// front_facing で正しく反転済み、地形は閉じたハイトフィールド）ため、geo_gate の幾何法線に
/// 深度復元 Ng ではなく N を使う。通常メッシュ（RT1.w=0）は従来どおり深度復元 Ng を使い、
/// 法線マップ後の薄い面での光漏れ防止（geo_gate 本来の目的）を維持する。
const GBUFFER_NORMAL_AUTHORED_THRESHOLD: f32 = 0.5;

// ── RT ソフト影マスクの「深度を考慮した」アップサンプル定数（Phase RT-Shadow-Denoise 改良）──
//
// 症状: 半解像度マスク（いもす法ボックスブラー済み）をバイリニアでフル解像度へ拡大すると、
//       深度不連続な境界（カーテンと床のフチ）で手前・奥の影値が混ざり、影のフチが薄く
//       にじむ。対策として joint bilateral upsample（バイリニア重み × 深度類似度重み）に変える。
//
/// 深度類似度の許容幅（ビュー空間深度に対する相対割合）。full と half の深度差が
/// この割合×深度 を超えると重みが急減し、別の深度面（奥の床など）のテクセルを混ぜない。
/// 相対にするのはシーンのスケール非依存にするため（遠景ほど絶対深度差は大きくなる）。
///
/// 【0.05→0.01 に絞った理由（影境界の明るいハロー）】5% はビュー深度 10m で 50cm に相当し、
/// キャラクタのシルエットの向こう側にある壁・床のテクセル（遮蔽されていない＝明るい）まで
/// 「同一面」とみなして混ぜてしまう。その結果、影の境界の内側に明るい縁（ハロー）が出る。
/// 1%（10m で 10cm）なら同一面の傾きによる深度変化は通しつつ、シルエットを跨ぐ不連続は落とせる。
/// これより小さくすると急傾斜の床で全テクセル棄却が頻発し、最近傍 1 テクセル採用（＝階段状の
/// エイリアス）に落ちる頻度が増える。
const SHADOW_MASK_DEPTH_TOLERANCE_FRAC: f32 = 0.01;
/// 相対許容幅の下限（ビュー空間距離・ワールド単位）。極近距離で許容幅が 0 に潰れて
/// 過敏になる（同一面まで棄却してしまう）のを防ぐ床値。
const SHADOW_MASK_DEPTH_TOLERANCE_MIN: f32 = 0.05;
/// 4 テクセルの合計重みがこの値未満なら「全テクセル棄却」とみなし、最も深度が近い 1 テクセル
/// だけを採用する（にじみゼロを優先。境界の細い特徴で多少のエイリアスが出るのは許容）。
const SHADOW_MASK_UPSAMPLE_MIN_WEIGHT: f32 = 1e-4;
/// 法線類似度重みの指数 k（重み = max(dot(N_full, N_half), 0)^k）。
///
/// 【なぜ深度だけでは足りないのか】深度が連続でも面の向きが変わる箇所（壁と床が接する入隅、
/// 箱の稜線）では、片側だけが影の中という状況が普通に起きる。深度差は小さいので深度重みは
/// 通してしまい、明るい側のテクセルが暗い側へ漏れて境界に明るい縁（ハロー）が残る。
/// 法線の一致度を掛けると、深度が近くても向きが違うテクセルを落とせる。
/// k=8 は cos 45°(≈0.707) で重みが約 0.09 まで落ち、cos 15°(≈0.966) なら 0.75 を保つ強さ
///（同一面の法線マップ由来の揺らぎは通し、稜線は切る）。
const SHADOW_MASK_NORMAL_WEIGHT_POWER: f32 = 8.0;

// ─── フレームバッファ画素 → ビューポート相対 UV ───────────────────────
//
// `@builtin(position)` はフレームバッファ基準の画素座標だが、NDC `[-1,1]` が写像されるのは
// **ビューポート矩形だけ**（Play のレターボックス／ピラーボックスで set_viewport する矩形）。
// 深度から NDC を組み立てる箇所は必ずこの関数を通すこと。
// Edit モード・黒帯なしでは viewport = (0, 0, RT幅, RT高さ) となり、従来式と完全に同値。
fn deferred_vp_uv_from_pix(pix: vec2<f32>) -> vec2<f32> {
    return (pix - u_camera.viewport.xy) / max(u_camera.viewport.zw, vec2<f32>(1.0, 1.0));
}

/// 半解像度マスクテクセル `hcoord` の「代表ビュー空間深度」を復元する。
///
/// 深度を別テクスチャに焼かず（.a 同梱はいもす法ブラーが深度を混ぜて壊すため不可）、
/// 既にバインド済みのフル解像度深度 t_depth から、マスク生成パス（shadow_mask.wgsl の
/// mask_full_pix）と**同一の写像**で各半解像度テクセルが評価に使ったフル解像度テクセルを
/// 引き当てて深度を読む。これにより「ブラーは深度に一切触れない」かつ「マスクが評価された
/// 深度と厳密一致」を新テクスチャ・帯域・binding 追加なしで両立する。
/// - `hcoord`   : 半解像度テクセル整数座標。
/// - `half_dims`: 半解像度テクスチャの寸法（textureDimensions(t_shadow_mask)）。
/// - `full_dims`: フル解像度 G-Buffer の寸法（textureDimensions(t_gbuffer0)）。
fn mask_half_texel_full_pix(hcoord: vec2<i32>, half_dims: vec2<f32>, full_dims: vec2<f32>) -> vec2<f32> {
    // 半解像度テクセル中心の UV（fs_mask の vs_fullscreen が出す [0,1] UV 規約と一致）。
    let uv = (vec2<f32>(hcoord) + vec2<f32>(0.5, 0.5)) / half_dims;
    // fs_mask の mask_full_pix と同一のフル解像度テクセル選択（同じ深度・法線を復元するため）。
    return clamp(uv * full_dims, vec2<f32>(0.0, 0.0), full_dims - vec2<f32>(1.0, 1.0));
}

/// 半解像度マスクテクセル `hcoord` が評価に使ったフル解像度画素の G-Buffer 法線（ワールド空間）。
/// 深度復元（mask_half_texel_view_z）と**同一の写像**で引くので、深度重みと法線重みは
/// 必ず同じテクセルを見る。RT1.xyz は正規化前提だが、量子化のため念のため normalize する。
fn mask_half_texel_normal(hcoord: vec2<i32>, half_dims: vec2<f32>, full_dims: vec2<f32>) -> vec3<f32> {
    let fp = mask_half_texel_full_pix(hcoord, half_dims, full_dims);
    return normalize(textureLoad(t_gbuffer1, vec2<i32>(fp), 0).xyz);
}

fn mask_half_texel_view_z(hcoord: vec2<i32>, half_dims: vec2<f32>, full_dims: vec2<f32>) -> f32 {
    let fp   = mask_half_texel_full_pix(hcoord, half_dims, full_dims);
    let d    = textureLoad(t_depth, vec2<i32>(fp), 0);
    // 深度→ワールド→ビュー空間 z（fs_deferred / shadow_mask.wgsl の mask_world_pos と同式・同カメラ）。
    // NDC はビューポート矩形へ写像されるため、フル解像度画素をビューポート相対 UV へ直す。
    let uv_vp = deferred_vp_uv_from_pix(fp);
    let ndc  = vec3<f32>(uv_vp.x * 2.0 - 1.0, 1.0 - uv_vp.y * 2.0, d);
    let clip = u_camera.inv_view_proj * vec4<f32>(ndc, 1.0);
    let wp   = clip.xyz / clip.w;
    return (u_camera.view * vec4<f32>(wp, 1.0)).z;
}

@fragment
fn fs_deferred(@builtin(position) frag: vec4<f32>) -> @location(0) vec4<f32> {
    let pix = vec2<i32>(frag.xy);

    // ── 1) 深度読み出し＋ワールド座標復元 ─────────────────────
    // dpdx/dpdy は使わないため一様制御フローの制約はないが、既存の surface_gather.wgsl の
    // 流儀（discard より前に必要な値を計算する）に倣い、背景判定の discard より前に
    // world_pos を計算しておく（本パスでは実害はないが、将来の拡張で微分を挟む場合に
    // 備えて一貫した書き方にする）。
    let depth = textureLoad(t_depth, pix, 0);

    // uv: RT 全面基準（AO / SSGI / シャドウマスクは RT 全面で生成されるため、
    //     それらのサンプリングにはこちらを使う）。
    let uv  = frag.xy / u_camera.resolution;
    // ndc: ビューポート相対で正規化する（NDC はビューポート矩形にしか写像されない）。
    //      Play のレターボックス時にここを RT 全面で割ると、復元ワールド座標が
    //      水平方向へ数メートル滑り、RT 影のレイ原点が地形内部へ潜って黒く潰れる。
    let uv_vp = deferred_vp_uv_from_pix(frag.xy);
    let ndc = vec3<f32>(uv_vp.x * 2.0 - 1.0, 1.0 - uv_vp.y * 2.0, depth);
    let clip = u_camera.inv_view_proj * vec4<f32>(ndc, 1.0);
    let world_pos = clip.xyz / clip.w;

    // ── 2) 背景ピクセルは discard（クリア色／スカイボックスを保持）───
    if depth >= DEFERRED_BACKGROUND_DEPTH {
        discard;
    }

    // ── 3) G-Buffer 展開 ───────────────────────────────────────
    let g0 = textureLoad(t_gbuffer0, pix, 0);
    let g1 = textureLoad(t_gbuffer1, pix, 0);
    let g2 = textureLoad(t_gbuffer2, pix, 0);
    let g3 = textureLoad(t_gbuffer3, pix, 0);

    let albedo    = g0.rgb;
    let occlusion = g0.a;
    let N         = normalize(g1.xyz);
    let metallic  = g2.r;
    let roughness = g2.g;
    // 拡散透過（葉・布・紙の逆光透け）は RT2.b に焼かれている（gbuffer_write.wgsl）。
    let diffuse_transmission = g2.b;
    let emissive  = g3.rgb;
    // ── 情報系チャンネル（第 2 層の生成物として合成が読む素材）─────────────
    // RT2.a = マテリアルの汎用ユーザーデータ（0..1、8bit 量子化済み）。
    // RT3.a = セマンティックタグ | シェーディングモデル ID のパック値（無損失）。
    // このうち shading_model は **L3-a（シェーディングアセット）で実際に消費される**:
    // lighting_eval.wgsl が Surface → ShadingSurface へ写し、契約関数 shade_surface の
    // switch キーとして ID 1..3 をユーザー実装へ振り分ける（ID 0 はエンジン標準 PBR）。
    // render_tag / user_data はどのライティング経路も参照しない（アセットが任意に読む素材）。
    let user_data  = g2.a;
    let surface_id = unpack_surface_id(g3.a);

    // ── 4) 幾何法線 Ng の復元 ───────────────────────────────────
    // G-Buffer には焼いていないため、復元したワールド座標の画面微分から作る
    // （標準的なデファードの手法）。dpdx/dpdy はフラグメントの一様制御フロー内で
    // 呼ぶ必要があるが、discard は上で確定済み（depth < 1.0 の分岐にはもう入らない）
    // ので、このタイミングで呼んでも一様性解析上は問題ない
    // （discard 自体は分岐後に一度だけ発生し、以降のコードは到達しないため）。
    let ng_raw = cross(dpdx(world_pos), dpdy(world_pos));
    let ng_len = length(ng_raw);
    var Ng: vec3<f32>;
    if ng_len < 1e-8 {
        // 縮退（画面上でほぼゼロ面積）時は N で代用する（surface_gather.wgsl の
        // geometric_normal と同じフォールバック方針）。
        Ng = N;
    } else {
        Ng = ng_raw / ng_len;
    }
    // N と同じ半球へ揃える（表裏の符号を一致させる。faceforward 相当）。
    if dot(Ng, N) < 0.0 {
        Ng = -Ng;
    }
    // ── authored 法線（草・地形）は深度復元 Ng を信用しない ────────────────────
    // RT1.w=1 のサーフェスは、深度不連続で暴れる Ng の代わりに信頼できる G-Buffer 法線 N を
    // geo_gate の幾何法線として使う（草・地形の影がドット状ノイズ／黒斑点に壊れる不具合の根治）。
    // 詳細は GBUFFER_NORMAL_AUTHORED_THRESHOLD のコメントを参照。
    if g1.w >= GBUFFER_NORMAL_AUTHORED_THRESHOLD {
        Ng = N;
    }

    // ── 5) Surface を構築して既存のライト評価へ渡す ─────────────
    // vertex_normal（Nv）は焼いていないため N で代用する。これにより RT 影の
    // シャドウターミネータのランプ／レイ原点のスロープスケールバイアス／地平線テストが、
    // フォワードパスの「滑らかな補間頂点法線」ではなく「法線マップ適用後の N」を使う
    // ことになる（surface.wgsl のコメント参照・(b) 案を採用）。影響は影の減衰カーブに
    // 法線マップの高周波成分がわずかに乗る可能性がある程度で、Lit・不透明のみに限られる。
    var s: Surface;
    s.world_pos     = world_pos;
    s.normal        = N;
    s.geo_normal    = Ng;
    s.vertex_normal = N;
    s.albedo        = albedo;
    s.alpha         = 1.0; // 不透明のみ（半透明は Forward パスが別途処理する）
    s.metallic      = metallic;
    s.roughness     = roughness;
    // 拡散透過（RT2.b から復元）。逆光透け項は forward と同一の evaluate_lighting で効く。
    s.diffuse_transmission = diffuse_transmission;
    s.emissive      = emissive;
    // 情報系（RT2.a / RT3.a から復元）。shading_model はシェーディングモデルの振り分けに
    // 使われる（L3-a）。render_tag / user_data はライト評価からは参照されない。
    s.user_data     = user_data;
    s.render_tag    = surface_id.x;
    s.shading_model = surface_id.y;
    // AO 乗算: 半解像度 AO をフル解像度 UV（frag.xy/resolution）でバイリニアサンプルし
    // occlusion に掛ける。これにより AO はアンビエント（evaluate_gi_ambient）・DDGI・
    // 疑似バウンス項にのみ効く（直接光は lighting_eval.wgsl の shade_light が occlusion を
    // 使わないため暗くならない）。AO=Off 時は白テクスチャで ao=1.0＝従来と同一の見た目。
    let ao = textureSampleLevel(t_ao, s_ao, uv, 0.0).r;
    s.occlusion     = occlusion * ao;
    s.frag_coord    = frag.xy;
    // SSGI（スクリーンスペース GI）の間接光を採取し Surface へ渡す（.a=1＝deferred で有効）。
    // 半解像度 t_ssgi をフル解像度 UV でバイリニアアップサンプルする（AO と同じ流儀）。
    // GI_MODE_SSGI 以外（DDGI/フラット）のときは evaluate_gi_ambient がこの値を無視するため、
    // ダミーがバインドされていても無害。1 フレーム遅延方式（前フレームの HDR から計算した結果）。
    let ssgi = textureSampleLevel(t_ssgi, s_ssgi, uv, 0.0).rgb;
    s.screen_gi     = vec4<f32>(ssgi, 1.0);
    // 水中コースティクス（Phase W5.3）。フル解像度 1:1 なので textureLoad で直接引く。
    // 機能 OFF のダミー 1x1 では範囲外ロード＝0 が返り、増幅なしになる（上の宣言コメント参照）。
    // 全成分 0 は「水中ではない」の印（パスのクリア値・discard・機能 OFF の
    // ダミー 1x1 への範囲外 textureLoad はいずれも 0 を返す）。
    // 透過率は乗算項なので、0 のまま使うと画面が黒くなる。中立値 (1,1,1,0) へ写す。
    let caustics_raw = textureLoad(t_caustics, pix, 0);
    s.caustics = select(
        vec4<f32>(1.0, 1.0, 1.0, 0.0),
        caustics_raw,
        any(caustics_raw != vec4<f32>(0.0)),
    );
    // 影の屈折オフセット（Phase W5.3）。同じ生成パスのフル解像度 MRT なので同じ pix で 1:1。
    // 0＝ずらさない（加算の中立値）なので、非水中・機能 OFF でも写し直しは不要。
    s.shadow_refract_offset = textureLoad(t_caustics_offset, pix, 0).xy;

    // ── RT ソフト影マスク（Phase RT-Shadow-Denoise）: 深度考慮アップサンプル（joint bilateral）──
    // 従来は半解像度 4 レイヤを s_shadow_mask（Filtering）でそのままバイリニア拡大していたが、
    // 深度不連続な境界（カーテンと床のフチ）で手前・奥の影値が混ざり、影のフチが薄くにじんでいた。
    // 対策: 周囲 4 テクセルを textureLoad し、バイリニア重み × 深度類似度重み で正規化平均する。
    // 深度は mask_half_texel_view_z がフル解像度 t_depth から生成時と同一写像で復元する。マスク生成パスが
    // 各レイヤの .a へ同梱した half-res 深度と同式（view * world_pos の z）なので、生成・ブラー・
    // アップサンプルの深度基準は厳密一致する（ここでの復元は追加 binding なしの最小差分・供給源は等価。
    // .a を直接読む簡素化も可能だが、既存の復元経路が同値かつ無改変のため据え置く）。
    //
    // 【上流と下流の両断】以前は半解像度マスクの各テクセルがいもす法ボックスブラーで境界を跨いで既に
    // 汚染されており（薄い帯＝ハロー）、下流の深度アップサンプルだけでは根治しなかった。現在は上流の
    // ブラーを影マスク専用の separable バイラテラル（half-res 深度ガイドでエッジを跨がない）に切り替えた
    // ため、ブラー段階で汚染が生じない。本アップサンプルは半解像度→フル解像度の拡大時ににじみを出さない
    // 下流側の保険として維持する（上流・下流の両方でエッジを保つ）。
    //
    // deferred は常に valid=1（マスク非対象ライトは shadow_mask_slot=-1 で参照されず無害。マスク非対象
    // フレームはダミー白（半解像度 1x1）がバインドされ、どのスロットも -1＝参照されないため無影響）。
    {
        let full_dims = vec2<f32>(textureDimensions(t_gbuffer0));
        let half_dims = vec2<f32>(textureDimensions(t_shadow_mask));
        // フル解像度ピクセルのビュー空間深度（アップサンプルの基準深度）。
        let d_full = (u_camera.view * vec4<f32>(world_pos, 1.0)).z;

        // 半解像度サンプル位置（テクセル中心合わせに -0.5）と 4 近傍の整数座標・バイリニア重み。
        let hp   = uv * half_dims - vec2<f32>(0.5, 0.5);
        let base = floor(hp);
        let f    = hp - base;
        let bi   = vec2<i32>(base);
        let maxc = vec2<i32>(half_dims) - vec2<i32>(1, 1);
        var coords: array<vec2<i32>, 4>;
        coords[0] = clamp(bi + vec2<i32>(0, 0), vec2<i32>(0, 0), maxc);
        coords[1] = clamp(bi + vec2<i32>(1, 0), vec2<i32>(0, 0), maxc);
        coords[2] = clamp(bi + vec2<i32>(0, 1), vec2<i32>(0, 0), maxc);
        coords[3] = clamp(bi + vec2<i32>(1, 1), vec2<i32>(0, 0), maxc);
        var bw: array<f32, 4>;
        bw[0] = (1.0 - f.x) * (1.0 - f.y);
        bw[1] = f.x * (1.0 - f.y);
        bw[2] = (1.0 - f.x) * f.y;
        bw[3] = f.x * f.y;

        // 深度類似度重み（相対許容幅）× 法線類似度重み。full と half で「深度が離れている」か
        // 「面の向きが違う」テクセルを排除する（どちらか一方だけでは境界の明るいハローが残る）。
        // 併せて「最も深度が近いテクセル」を全棄却時のフォールバック用に控える。
        let tol = SHADOW_MASK_DEPTH_TOLERANCE_FRAC * max(abs(d_full), SHADOW_MASK_DEPTH_TOLERANCE_MIN);
        var w: array<f32, 4>;
        var w_sum   = 0.0;
        var best_i  = 0;
        var best_df = 1.0e30;
        for (var k: i32 = 0; k < 4; k = k + 1) {
            let dz   = mask_half_texel_view_z(coords[k], half_dims, full_dims);
            let diff = abs(dz - d_full);
            // 法線類似度: dot を [0,1] に切ってから k 乗（背向きは 0＝完全棄却）。
            let nz   = mask_half_texel_normal(coords[k], half_dims, full_dims);
            let wn   = pow(max(dot(nz, N), 0.0), SHADOW_MASK_NORMAL_WEIGHT_POWER);
            w[k]     = bw[k] * exp(-diff / tol) * wn;
            w_sum    = w_sum + w[k];
            if diff < best_df {
                best_df = diff;
                best_i  = k;
            }
        }

        if w_sum < SHADOW_MASK_UPSAMPLE_MIN_WEIGHT {
            // 全テクセル棄却級（深度が全近傍と大きく食い違う細い特徴）: 最も深度が近い 1 テクセルを採用。
            let c = coords[best_i];
            for (var mi: u32 = 0u; mi < SURFACE_SHADOW_MASK_SLOTS; mi = mi + 1u) {
                s.shadow_mask[mi] = vec4<f32>(textureLoad(t_shadow_mask, c, i32(mi), 0).rgb, 1.0);
            }
        } else {
            // 通常時: 深度が近いテクセルだけを重み付き平均（境界を跨がない）。
            let inv = 1.0 / w_sum;
            for (var mi: u32 = 0u; mi < SURFACE_SHADOW_MASK_SLOTS; mi = mi + 1u) {
                var acc = vec3<f32>(0.0, 0.0, 0.0);
                for (var k: i32 = 0; k < 4; k = k + 1) {
                    acc = acc + textureLoad(t_shadow_mask, coords[k], i32(mi), 0).rgb * w[k];
                }
                s.shadow_mask[mi] = vec4<f32>(acc * inv, 1.0);
            }
        }
    }
    s.shadow_mask_valid = 1.0;

    return vec4<f32>(evaluate_lighting(s), 1.0);
}
