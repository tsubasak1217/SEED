// ============================================================
// gbuffer_debug.wgsl — G-Buffer 各チャンネルのデバッグ可視化（シーンビュー表示モード）
//
// ## 役割（単一責任）
// G-Buffer（RT0..RT3 + 深度 + 速度 RT4）から uniform で指定された 1 チャンネルだけを取り出し、
// 人間が読める形（そのまま／グレースケール／疑似カラー）でシーン HDR へ **上書き** する
// フルスクリーンパス。ライティング計算は一切行わない。
//
// ## どこに挿さるのか（重要な設計判断）
// エディタのシーンビュー表示モードが「G-Buffer: 〜」のとき、frame_renderer は
// **デファードを有効に保ったまま**（deferred_active = true）、デファード・ライティングの
// フルスクリーンパスを本シェーダへ差し替える。
//
// unlit/wireframe と同じ仕組み（SceneViewMode::is_lit() == false）に乗せてはいけない。
// is_lit() が false になると deferred_active が落ちてフォワード経路になり、
// **可視化したい G-Buffer が生成されなくなる**（自己矛盾）。view_mode.rs の
// SceneViewMode::is_lit() のコメントが正典。
//
// ## 後段の扱い
// frame_renderer 側で、ブルーム／SSGI／反射／AO／シャドウマスク／半透明（距離ソート・WBOIT）／
// 屈折／パーティクル／スカイボックスを **全てスキップ** し、トーンマップも素通し
// （TonemapOperator::None）にする。よって画面には G-Buffer の値がそのまま出る。
// ギズモ／グリッド／アイコンオーバーレイだけは残す（どのアクタを見ているのか分からなくなると
// デバッグ表示として使い物にならないため）。
//
// ## パイプライン構成
// 出力は単一カラーターゲット（シーン HDR）。深度アタッチメントは持たない
// （全画素を無条件に上書きするため深度テストは不要かつ有害）。
// 頂点はフルスクリーン三角形（頂点バッファなし。deferred_lighting.wgsl と同じ流儀）。
// ============================================================

// ─── Group 0: G-Buffer 入力 + パラメータ ─────────────────────
//
// フォーマットは Rust 側 renderer/gbuffer.rs の GBUFFER*_FORMAT と一致させること。
// いずれも textureLoad で直読みするためサンプラーは持たない（フィルタ不要）。
@group(0) @binding(0) var t_gbuffer0: texture_2d<f32>;   // RT0: base_color.rgb + occlusion.a
@group(0) @binding(1) var t_gbuffer1: texture_2d<f32>;   // RT1: world normal.xyz + authored フラグ.w
@group(0) @binding(2) var t_gbuffer2: texture_2d<f32>;   // RT2: metallic.r + roughness.g + transmission.b + user_data.a
@group(0) @binding(3) var t_gbuffer3: texture_2d<f32>;   // RT3: emissive.rgb (HDR) + surface_id.a
@group(0) @binding(4) var t_depth:    texture_depth_2d;  // 深度（textureLoad 専用）
@group(0) @binding(5) var t_velocity: texture_2d<f32>;   // RT4: 速度（モーションベクタ, Rg16Float）

/// 可視化パラメータ（CPU 側 gbuffer_debug.rs の GBufferDebugParams と #[repr(C)] 一致）。
struct GBufferDebugParams {
    /// 可視化チャンネル（下記 GB_DEBUG_* 定数。Rust の GBufferDebugChannel と一致）。
    channel:    u32,
    /// カメラの near 平面距離（深度の線形化に使う）。
    near_plane: f32,
    /// カメラの far 平面距離（深度の線形化に使う）。
    far_plane:  f32,
    _pad:       u32,
}
@group(0) @binding(6) var<uniform> u_debug: GBufferDebugParams;

// ─── 可視化チャンネル ID（Rust GBufferDebugChannel と厳密に一致させること）───
const GB_DEBUG_BASE_COLOR:   u32 = 0u;
const GB_DEBUG_OCCLUSION:    u32 = 1u;
const GB_DEBUG_NORMAL:       u32 = 2u;
const GB_DEBUG_ROUGHNESS:    u32 = 3u;
const GB_DEBUG_METALLIC:     u32 = 4u;
const GB_DEBUG_TRANSMISSION: u32 = 5u;
const GB_DEBUG_EMISSIVE:     u32 = 6u;
const GB_DEBUG_DEPTH:        u32 = 7u;
const GB_DEBUG_VELOCITY:     u32 = 8u;
const GB_DEBUG_RENDER_TAG:   u32 = 9u;
const GB_DEBUG_USER_DATA:    u32 = 10u;

// ─── 速度可視化の定数（velocity_debug.wgsl と同一規約・同一値）─────────────
//
// 灰色＝速度 0 / 赤・シアン＝水平 / 緑・マゼンタ＝垂直 / 青＝飽和。
// 読み方の正典は velocity_debug.wgsl の冒頭コメント（環境変数版と見た目を揃えるため
// 値をここへ複製している。片方だけ変えないこと）。
const VELOCITY_DEBUG_GAIN:       f32 = 20.0;
const VELOCITY_DEBUG_SATURATION: f32 = 1.0;
const VELOCITY_DEBUG_MID:        f32 = 0.5;

// ─── surface_id のビットレイアウト（surface.wgsl / Rust surface_id.rs と一致）───
/// セマンティックタグのビット幅。
const RENDER_TAG_BITS: u32 = 4u;
/// セマンティックタグのビットマスク（4bit ＝ 0..15）。
const RENDER_TAG_MASK: u32 = 15u;
/// レンダータグの総数（4bit ぶん）。パレット配列の要素数と一致させること。
const RENDER_TAG_COUNT: u32 = 16u;

/// レンダータグのカラーパレット（4bit ＝ 16 色）。
///
/// 隣接する ID どうしが目視で区別できるよう、色相を大きく飛ばして配置している。
/// index 0（RENDER_TAG_NONE ＝ タグ未設定）だけは「無彩色の暗灰」にして、
/// 「タグが付いていない面」と「タグが付いている面」を一目で分けられるようにする。
const RENDER_TAG_PALETTE: array<vec3<f32>, 16> = array<vec3<f32>, 16>(
    vec3<f32>(0.15, 0.15, 0.15),  //  0: NONE（タグ未設定）
    vec3<f32>(0.90, 0.10, 0.10),  //  1: 赤
    vec3<f32>(0.10, 0.80, 0.20),  //  2: 緑
    vec3<f32>(0.15, 0.35, 0.95),  //  3: 青
    vec3<f32>(0.95, 0.80, 0.10),  //  4: 黄
    vec3<f32>(0.85, 0.15, 0.85),  //  5: マゼンタ
    vec3<f32>(0.10, 0.85, 0.85),  //  6: シアン
    vec3<f32>(0.95, 0.50, 0.10),  //  7: オレンジ
    vec3<f32>(0.55, 0.20, 0.90),  //  8: 紫
    vec3<f32>(0.60, 0.90, 0.20),  //  9: 黄緑
    vec3<f32>(0.20, 0.55, 0.55),  // 10: 暗シアン
    vec3<f32>(0.90, 0.55, 0.65),  // 11: ピンク
    vec3<f32>(0.45, 0.35, 0.15),  // 12: 茶
    vec3<f32>(0.35, 0.45, 0.90),  // 13: 空色
    vec3<f32>(0.70, 0.70, 0.70),  // 14: 明灰
    vec3<f32>(1.00, 1.00, 1.00),  // 15: 白
);

// ─── 深度の扱い ─────────────────────────────────────────────
/// 深度クリア値。この値以上＝「何も描かれていない背景ピクセル」。
/// deferred_lighting.wgsl の DEFERRED_BACKGROUND_DEPTH と同じ規約。
const GB_DEBUG_BACKGROUND_DEPTH: f32 = 1.0;

/// 背景ピクセルの表示色（黒）。
///
/// 深度可視化では「線形深度 1.0 ＝ 白」になるが、それだと「far 直前の実在サーフェス」と
/// 「何も無い背景」が同じ白で潰れて区別できない。背景は明示的に黒へ倒す。
const GB_DEBUG_BACKGROUND_COLOR: vec3<f32> = vec3<f32>(0.0, 0.0, 0.0);

/// 非線形深度（DirectX/wgpu 流儀の LH 透視投影, z∈[0,1]）を 0..1 の線形深度へ変換する。
///
/// 投影行列は Mat4x4::perspective_lh:
///   z_ndc = far * (z_view - near) / (z_view * (far - near))
/// を z_view について解くと
///   z_view = far * near / (far - z_ndc * (far - near))
/// となる。これを (z_view - near) / (far - near) で 0..1 へ正規化する
/// （near で 0＝黒 / far で 1＝白）。
///
/// 注: 直交投影（2D シーンビュー）では成立しないが、2D シーンビューは deferred_active が
/// 落ちるため本パス自体が走らない（frame_renderer の edit_view_2d 判定）。
fn linearize_depth(z_ndc: f32, near_plane: f32, far_plane: f32) -> f32 {
    let range  = far_plane - near_plane;
    // 0 除算防止（near == far の縮退設定でも NaN を出さない）。
    let denom  = max(far_plane - z_ndc * range, 1e-6);
    let z_view = far_plane * near_plane / denom;
    return clamp((z_view - near_plane) / max(range, 1e-6), 0.0, 1.0);
}

/// 速度ベクタを velocity_debug.wgsl と同一規約の疑似カラーへ変換する。
fn velocity_pseudo_color(velocity: vec2<f32>) -> vec3<f32> {
    let v  = velocity * VELOCITY_DEBUG_GAIN;
    // R = 右向き / G = 下向き を 0.5 中心の符号付き表示にする。
    let rg = clamp(vec2<f32>(VELOCITY_DEBUG_MID) + v * VELOCITY_DEBUG_MID,
                   vec2<f32>(0.0), vec2<f32>(1.0));
    // 表示レンジを超えた画素は青を混ぜて飽和を明示する。
    let over = max(abs(v.x), abs(v.y)) - VELOCITY_DEBUG_SATURATION;
    return vec3<f32>(rg.x, rg.y, clamp(over, 0.0, 1.0));
}

/// surface_id（RT3.a のパック値）からレンダータグ（下位 4bit）を取り出す。
/// surface.wgsl の unpack_surface_id と同じビット規約（パック値は非負整数の f32）。
fn unpack_render_tag(packed: f32) -> u32 {
    return u32(max(packed, 0.0)) & RENDER_TAG_MASK;
}

// ─── フルスクリーン三角形 ────────────────────────────────────

/// 3 頂点で画面全体を覆う巨大三角形（deferred_lighting.wgsl と同一手法）。
@vertex
fn vs_fullscreen(@builtin(vertex_index) vi: u32) -> @builtin(position) vec4<f32> {
    // (-1,-1) / (3,-1) / (-1,3)
    let x = f32((vi << 1u) & 2u) * 2.0 - 1.0;
    let y = f32(vi & 2u) * 2.0 - 1.0;
    return vec4<f32>(x, y, 0.0, 1.0);
}

// ─── 可視化フラグメント ──────────────────────────────────────

@fragment
fn fs_gbuffer_debug(@builtin(position) frag_pos: vec4<f32>) -> @location(0) vec4<f32> {
    // G-Buffer・速度 RT・深度はいずれもシーン HDR と同解像度・同ピクセル配置のため
    // textureLoad で直読みできる（UV 変換もサンプラーも不要）。
    let px = vec2<i32>(frag_pos.xy);

    let z_ndc = textureLoad(t_depth, px, 0);
    // 背景（何も描かれていないピクセル）は全チャンネル共通で黒に倒す。
    // G-Buffer は背景ではクリア値（0）のままなので黒表示でも実害はないが、
    // 深度だけは「far ＝ 白」になって実サーフェスと紛らわしいため明示的に落とす。
    if (z_ndc >= GB_DEBUG_BACKGROUND_DEPTH) {
        return vec4<f32>(GB_DEBUG_BACKGROUND_COLOR, 1.0);
    }

    let g0 = textureLoad(t_gbuffer0, px, 0);
    let g1 = textureLoad(t_gbuffer1, px, 0);
    let g2 = textureLoad(t_gbuffer2, px, 0);
    let g3 = textureLoad(t_gbuffer3, px, 0);

    var color = vec3<f32>(0.0);
    switch (u_debug.channel) {
        // ベースカラー（RT0.rgb）。そのまま出す。
        case GB_DEBUG_BASE_COLOR: {
            color = g0.rgb;
        }
        // オクルージョン（RT0.a）。1 ＝ 遮蔽なし（白）／0 ＝ 完全遮蔽（黒）。
        case GB_DEBUG_OCCLUSION: {
            color = vec3<f32>(g0.a);
        }
        // ワールド法線（RT1.xyz, -1..1）を 0..1 へ再マップ。
        // +X ＝ 赤 / +Y ＝ 緑 / +Z ＝ 青が強くなる（灰 (0.5) が 0 成分）。
        case GB_DEBUG_NORMAL: {
            color = clamp(g1.xyz * 0.5 + vec3<f32>(0.5), vec3<f32>(0.0), vec3<f32>(1.0));
        }
        // ラフネス（RT2.g）。黒 ＝ 鏡面 / 白 ＝ 完全拡散。
        case GB_DEBUG_ROUGHNESS: {
            color = vec3<f32>(g2.g);
        }
        // メタリック（RT2.r）。黒 ＝ 誘電体 / 白 ＝ 金属。
        case GB_DEBUG_METALLIC: {
            color = vec3<f32>(g2.r);
        }
        // 拡散透過（RT2.b）。白いほど光を透かす（葉・布など）。
        case GB_DEBUG_TRANSMISSION: {
            color = vec3<f32>(g2.b);
        }
        // エミッシブ（RT3.rgb, HDR）。トーンマップを素通しにしているため、
        // 1.0 を超える発光は白飛びして見える（＝レンジ超過が一目で分かる）。
        case GB_DEBUG_EMISSIVE: {
            color = g3.rgb;
        }
        // 深度。near ＝ 黒 / far ＝ 白の線形グレースケール。
        case GB_DEBUG_DEPTH: {
            color = vec3<f32>(linearize_depth(z_ndc, u_debug.near_plane, u_debug.far_plane));
        }
        // 速度（RT4）。velocity_debug.wgsl と同一の疑似カラー。
        case GB_DEBUG_VELOCITY: {
            color = velocity_pseudo_color(textureLoad(t_velocity, px, 0).rg);
        }
        // レンダータグ（RT3.a の下位 4bit）をカラーパレットで。
        case GB_DEBUG_RENDER_TAG: {
            color = RENDER_TAG_PALETTE[unpack_render_tag(g3.a) % RENDER_TAG_COUNT];
        }
        // ユーザーデータ（RT2.a）。用途はマテリアル任意の 8bit 値。
        case GB_DEBUG_USER_DATA: {
            color = vec3<f32>(g2.a);
        }
        // 未知のチャンネル ID（CPU/シェーダの定数不一致）は黒で明示的に潰す。
        default: {
            color = vec3<f32>(0.0);
        }
    }

    return vec4<f32>(color, 1.0);
}
