// ============================================================
//  water_id.wgsl — 水面の ID パス（エディタのピッキング用）
//
//  ## 役割（単一責任）
//  水面クアッドを ID バッファ（Rgba32Float。RGB=ワールド座標／A=raw アクタ ID）へ描き、
//  「Edit で水面をクリックしたら WaterVolume を持つアクタが選択される」を成立させる。
//  見た目には一切関与しない（色・波・屈折は water_surface.wgsl の担当）。
//
//  ## 波の変位を持たない
//  W1 の波は「フラグメントで法線だけを揺らす」実装で頂点は動かない。よって
//  ピッキング形状は静的な矩形と完全に一致し、ここで時間を扱う必要はない
//  （＝ Edit 中に波が動いても、ピック形状はズレない）。
//
//  ## 深度整合（水面より手前の物体を優先して選択する）
//  水面描画本体はシェーダ内の手動深度テストを使うが、ID パスは深度アタッチメントを
//  持つ（メインパスのシーン深度を Load 済み）。したがってここでは **通常のハードウェア
//  深度テスト**（depth_compare = LessEqual / depth_write = false）に任せる:
//    ・水面より手前に不透明物 → 深度で落ちる  → 手前の物体が選択される
//    ・水面より奥に不透明物   → 深度を通過し、後から描く水面が上書き → 水面が選択される
//  水面は深度を書かない（描画順の後段にいるギズモ等の判定を汚さない）。
//
//  ## バインディング規約
//    group 0: カメラ（CameraUniform, binding 0。他パスと同一の BindGroup を共有）
//    group 1: binding0 = 水パラメータ配列（storage, read。water_surface と同一バッファ）
// ============================================================

// ─── Group 0: カメラ ─────────────────────────────────────────
//
// クアッド生成に必要なのは view_proj だけだが、Rust 側 `uniforms::CameraUniform` の
// 先頭レイアウトと一致していなければならない（このシェーダはその前半だけを読む）。
// id_pass.wgsl と同じく「先頭 3 フィールドのみ宣言」する最小形にしてある。
struct CameraUniform {
    view_proj: mat4x4<f32>,
    view:      mat4x4<f32>,
    position:  vec3<f32>,
    _pad:      f32,
}
@group(0) @binding(0) var<uniform> u_camera: CameraUniform;

// ─── Group 1: 水パラメータ ───────────────────────────────────

/// 水ボリューム 1 個ぶんの GPU パラメータ。
/// Rust 側 `renderer::water::params::WaterParams` および `water_surface.wgsl` の
/// 同名構造体と**フィールド順・オフセットを厳密一致**させること。
/// 本シェーダが読むのは center / half_extent / actor_id の 3 つだけだが、
/// 配列ストライドを合わせるため全フィールドを宣言する。
struct WaterParams {
    center:           vec4<f32>,
    half_extent:      vec4<f32>,
    shallow_color:    vec4<f32>,
    deep_color:       vec4<f32>,
    foam_color:       vec4<f32>,
    reflection_color: vec4<f32>,
    wave:             vec4<f32>,
    fresnel:          vec4<f32>,
    /// x = ピッキング用 raw アクタ ID（id_base + DFS + 1。0 = 背景）
    actor_id:         vec4<u32>,
    /// 岸波の調整値（Phase W1.5。本シェーダは読まないが配列ストライドのため宣言する）
    shore:            vec4<f32>,
    /// 岸波のショアフィールド窓（Phase W1.5。同上）
    shore_field:      vec4<f32>,
}
@group(1) @binding(0) var<storage, read> u_water: array<WaterParams>;

// ─── 定数 ────────────────────────────────────────────────────

/// クアッド 1 枚あたりの頂点数（三角形 2 枚 = 6 頂点）。
const WATER_QUAD_VERTEX_COUNT: u32 = 6u;

// ─── ヘルパー ────────────────────────────────────────────────

/// クアッドの角オフセット（-1..1 の XZ）を頂点インデックスから生成する。
/// water_surface.wgsl の `water_quad_corner` と**同一の頂点並び**にすること
/// （見た目とピック形状が一致していなければならない）。
fn water_id_quad_corner(vi: u32) -> vec2<f32> {
    if (vi == 0u) { return vec2<f32>(-1.0, -1.0); }
    if (vi == 1u) { return vec2<f32>( 1.0, -1.0); }
    if (vi == 2u) { return vec2<f32>( 1.0,  1.0); }
    if (vi == 3u) { return vec2<f32>(-1.0, -1.0); }
    if (vi == 4u) { return vec2<f32>( 1.0,  1.0); }
    return vec2<f32>(-1.0, 1.0);
}

// ─── 頂点シェーダ ────────────────────────────────────────────

struct WaterIdVsOut {
    @builtin(position) clip:      vec4<f32>,
    @location(0)       world_pos: vec3<f32>,
    /// raw アクタ ID（補間してはならない離散値なので flat）
    @location(1) @interpolate(flat) actor_id: u32,
}

/// 頂点バッファ無しでクアッドを生成する（water_surface.wgsl の vs_water と同一形状）。
@vertex
fn vs_water_id(
    @builtin(vertex_index)   vi: u32,
    @builtin(instance_index) ii: u32,
) -> WaterIdVsOut {
    let p      = u_water[ii];
    let corner = water_id_quad_corner(vi % WATER_QUAD_VERTEX_COUNT);
    let world  = vec3<f32>(
        p.center.x + corner.x * p.half_extent.x,
        p.center.y,
        p.center.z + corner.y * p.half_extent.z,
    );

    var out: WaterIdVsOut;
    out.clip      = u_camera.view_proj * vec4<f32>(world, 1.0);
    out.world_pos = world;
    out.actor_id  = p.actor_id.x;
    return out;
}

// ─── フラグメントシェーダ ────────────────────────────────────

/// RGB にワールド座標（D&D のスポーン位置に使われる＝水面へのドロップが水面上に落ちる）、
/// A に raw アクタ ID のビットパターンを書き込む（ID パス共通規約）。
@fragment
fn fs_water_id(in: WaterIdVsOut) -> @location(0) vec4<f32> {
    return vec4<f32>(in.world_pos, bitcast<f32>(in.actor_id));
}
