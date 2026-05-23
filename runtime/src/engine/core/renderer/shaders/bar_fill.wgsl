// ============================================================
//  bar_fill.wgsl — 帯塗りつぶしシェーダー
//
//  LetterBox / PillarBox スケーリングモード時に、
//  ゲーム描画エリア外の帯部分を指定カラーで塗りつぶす。
//  頂点バッファは不要。@builtin(vertex_index) で 6 頂点を生成する。
//
//  NDC 座標系: X は左=-1/右=+1、Y は下=-1/上=+1。
//  Group 0: BarFillUniform（矩形 NDC 座標 + 塗りカラー）
// ============================================================

// ─── バインドグループ ─────────────────────────────────────────

/// 帯塗りつぶしユニフォーム
struct BarFillUniform {
    /// 塗りカラー（RGBA リニア）
    color: vec4<f32>,
    /// NDC 左端（-1.0〜+1.0）
    x0:    f32,
    /// NDC 下端（-1.0〜+1.0）
    y0:    f32,
    /// NDC 右端（-1.0〜+1.0）
    x1:    f32,
    /// NDC 上端（-1.0〜+1.0）
    y1:    f32,
}
@group(0) @binding(0) var<uniform> u: BarFillUniform;

// ─── 頂点シェーダー ───────────────────────────────────────────

/// 頂点バッファなし版クワッドシェーダー（6 頂点で 2 三角形）。
@vertex
fn vs_main(@builtin(vertex_index) vi: u32) -> @builtin(position) vec4<f32> {
    // t=0 → x0/y0 側, t=1 → x1/y1 側
    var tx = array<f32, 6>(0.0, 1.0, 0.0, 1.0, 0.0, 1.0);
    var ty = array<f32, 6>(0.0, 0.0, 1.0, 0.0, 1.0, 1.0);
    let x = u.x0 + tx[vi] * (u.x1 - u.x0);
    let y = u.y0 + ty[vi] * (u.y1 - u.y0);
    return vec4<f32>(x, y, 0.0, 1.0);
}

// ─── フラグメントシェーダー ───────────────────────────────────

@fragment
fn fs_main() -> @location(0) vec4<f32> {
    return u.color;
}
