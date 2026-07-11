// ============================================================
//  camera_preview_blit.wgsl — カメラプレビューブリットシェーダー
//
//  オフスクリーンのカメラプレビューテクスチャをスクリーン上の
//  指定矩形（NDC 座標）に貼り付ける。
//  矩形の縁には固定幅のアウトラインを描画する。
//
//  Group 0: 矩形位置（NDC 座標 x0,y0,x1,y1）
//  Group 1: プレビューテクスチャ + サンプラー
//
//  頂点バッファは不要。@builtin(vertex_index) で 6 頂点を生成する。
// ============================================================

// ─── 定数 ─────────────────────────────────────────────────────

/// アウトラインの幅（スクリーンピクセル単位）。
const BORDER_PX: f32 = 2.0;
/// アウトラインの色（白に近い明るい色で視認性を確保）。
const BORDER_COLOR: vec4<f32> = vec4<f32>(0.95, 0.95, 0.95, 1.0);

// ─── バインドグループ ─────────────────────────────────────────

/// NDC 矩形定義（x0=左, y0=下, x1=右, y1=上）
struct BlitRect {
    x0: f32,
    y0: f32,
    x1: f32,
    y1: f32,
}
@group(0) @binding(0) var<uniform> u_rect: BlitRect;

@group(1) @binding(0) var t_preview: texture_2d<f32>;
@group(1) @binding(1) var s_preview: sampler;

// ─── 頂点出力 ─────────────────────────────────────────────────

struct VertOut {
    @builtin(position) clip_pos: vec4<f32>,
    @location(0) uv: vec2<f32>,
}

// ─── 頂点シェーダー ───────────────────────────────────────────

/// 頂点バッファなし版クワッドシェーダー（6 頂点で 2 三角形）。
/// UV v=0 がテクスチャ上端（NDC y1 = 画面上）になるよう Y を反転する。
@vertex
fn vs_main(@builtin(vertex_index) vi: u32) -> VertOut {
    // クワッドの UV 座標（左上=0,0、右下=1,1）
    var uvs: array<vec2<f32>, 6> = array<vec2<f32>, 6>(
        vec2<f32>(0.0, 0.0),  // 左上
        vec2<f32>(1.0, 0.0),  // 右上
        vec2<f32>(0.0, 1.0),  // 左下
        vec2<f32>(1.0, 0.0),  // 右上
        vec2<f32>(1.0, 1.0),  // 右下
        vec2<f32>(0.0, 1.0),  // 左下
    );
    let uv = uvs[vi];

    // NDC 空間の矩形に UV をマッピングする
    // uv.x: 0=x0, 1=x1 (左→右)
    // uv.y: 0=y1, 1=y0 (テクスチャ上端=NDC y1、テクスチャ下端=NDC y0)
    let ndc_x = u_rect.x0 + uv.x * (u_rect.x1 - u_rect.x0);
    let ndc_y = u_rect.y1 - uv.y * (u_rect.y1 - u_rect.y0);

    var out: VertOut;
    out.clip_pos = vec4<f32>(ndc_x, ndc_y, 0.0, 1.0);
    out.uv       = uv;
    return out;
}

// ─── フラグメントシェーダー ───────────────────────────────────

@fragment
fn fs_main(in: VertOut) -> @location(0) vec4<f32> {
    // fwidth でスクリーン座標系における UV の微分を取得し、
    // BORDER_PX ピクセル分のボーダー幅を UV 空間に変換する。
    let duv = fwidth(in.uv);
    let bw  = duv * BORDER_PX;

    // UV が端から bw 以内にある場合はアウトラインとして描画する
    let at_edge = in.uv.x < bw.x || in.uv.y < bw.y
               || in.uv.x > 1.0 - bw.x || in.uv.y > 1.0 - bw.y;

    // プレビューは HDR オフスクリーン（Rgba16Float, Phase R3）のため、ここで
    // トーンマップ（tonemap_ops.wgsl の輝度ベース Reinhard）を適用してから表示する。
    // メインシーンのトーンマップパスと同一演算子で見た目を揃える。
    let hdr       = textureSample(t_preview, s_preview, in.uv);
    let mapped    = tonemap_apply(hdr.rgb, TONEMAP_REINHARD_LUMA);
    let tex_color = vec4<f32>(mapped, hdr.a);
    // ボーダーは表示色（LDR）のためトーンマップ対象外。
    return select(tex_color, BORDER_COLOR, at_edge);
}
