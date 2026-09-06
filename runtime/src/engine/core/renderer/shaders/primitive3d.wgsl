// ============================================================
//  primitive3d.wgsl — スクリプト 3D プリミティブ描画シェーダー（SEED.Draw3D）
//
//  【役割】
//  ワールド空間の頂点を受け取り、カメラ行列で clip space へ送る。
//  線（リボン）と点（画面向き正方形）は、ここで**画面ピクセル単位**の
//  押し出しを行う。これにより線の太さ・点の大きさがカメラ距離に依らず一定になる。
//
//  【頂点属性の使い分け】
//    - `side != 0`  : リボン頂点。`other`（相手の端点）との clip 空間方向から
//                     画面上の垂線を求め、`side`（= ±太さ/2 px）だけ押し出す。
//    - `offset != 0`: 画面向き正方形の頂点。px オフセットをそのまま足す。
//    - 両方 0       : 塗りの三角形。ワールド座標のまま素通しする。
//
//  【アンチエイリアス】
//  2D 版（primitive2d.wgsl）のフェザー帯は使わない。3D の線は深度に応じて
//  ジオメトリが動くため、帯の外縁を CPU で持つと near/far で破綻する。
//  ここでは矩形のままハードエッジで描く（デバッグ表示・ゲーム内の線として十分）。
// ============================================================

// ── 定数（マジックナンバー禁止）────────────────────────────────

/// これ未満のアルファは描かない（ブレンド段のコストを避ける）。
const PRIM3D_ALPHA_EPSILON: f32 = 0.002;

/// 画面上の線分方向がこれ未満の長さなら「潰れている」とみなし、
/// 既定の垂線（画面 X 方向）へ逃がす（0 除算とノイズ方向を防ぐ）。
const PRIM3D_DIR_EPSILON: f32 = 1e-6;

/// clip 空間 w の下限。これ以下はカメラ背後（CPU 側でクリップ済みだが
/// 数値誤差で漏れた場合の保険）。
const PRIM3D_MIN_W: f32 = 1e-6;

/// NDC の全幅（-1..1）。px ↔ NDC 変換の係数に使う。
const PRIM3D_NDC_SPAN: f32 = 2.0;

/// 押し出し方向が求まらないときの既定の垂線（画面 X 方向）。
const PRIM3D_FALLBACK_NORMAL: vec2<f32> = vec2<f32>(1.0, 0.0);

// ── カメラ uniform ────────────────────────────────────────────

/// ビュー射影行列とビューポート px サイズ。
/// `viewport` は set_viewport した矩形の幅・高さ（px）。
struct Prim3dCamera {
    view_proj : mat4x4<f32>,
    viewport  : vec2<f32>,
    _pad      : vec2<f32>,
};

@group(0) @binding(0) var<uniform> cam : Prim3dCamera;

// ── 頂点入出力 ────────────────────────────────────────────────

struct VertIn {
    /// ワールド座標。
    @location(0) position : vec3<f32>,
    /// リボンのもう一方の端点（ワールド）。リボン以外では position と同じ。
    @location(1) other    : vec3<f32>,
    /// RGBA カラー（0..1・ストレートアルファ）。
    @location(2) color    : vec4<f32>,
    /// 画面 px の追加オフセット（画面向き正方形の角に使う）。
    @location(3) offset   : vec2<f32>,
    /// リボンの押し出し量（± 太さ/2 px）。0 なら押し出さない。
    @location(4) side     : f32,
}

struct VertOut {
    @builtin(position) clip_pos : vec4<f32>,
    @location(0)       color    : vec4<f32>,
}

// ── 頂点シェーダー ────────────────────────────────────────────

@vertex
fn vs_main(in: VertIn) -> VertOut {
    var out : VertOut;
    var clip = cam.view_proj * vec4<f32>(in.position, 1.0);

    // カメラ背後（w <= 0）は押し出しの計算自体が破綻するため、
    // クリップ範囲外へ飛ばして捨てる（CPU 側で近平面クリップ済みの保険）。
    if (clip.w <= PRIM3D_MIN_W) {
        out.clip_pos = vec4<f32>(0.0, 0.0, PRIM3D_NDC_SPAN, 1.0);
        out.color    = in.color;
        return out;
    }

    // 画面 px 空間での押し出し量を組み立てる。
    var px_offset = in.offset;

    if (in.side != 0.0) {
        let clip_other = cam.view_proj * vec4<f32>(in.other, 1.0);
        // 相手側が背後なら方向が求まらないので既定の垂線を使う
        var dir_px = PRIM3D_FALLBACK_NORMAL;
        if (clip_other.w > PRIM3D_MIN_W) {
            let ndc_a = clip.xy / clip.w;
            let ndc_b = clip_other.xy / clip_other.w;
            // NDC 差分を px 差分へ（NDC 幅 2 が viewport px に対応）
            dir_px = (ndc_b - ndc_a) * cam.viewport / PRIM3D_NDC_SPAN;
        }
        let len_px = length(dir_px);
        var normal_px = PRIM3D_FALLBACK_NORMAL;
        if (len_px > PRIM3D_DIR_EPSILON) {
            // 画面上で線分に垂直な単位ベクトル
            normal_px = vec2<f32>(-dir_px.y, dir_px.x) / len_px;
        }
        px_offset = px_offset + normal_px * in.side;
    }

    // px → clip 空間（NDC は w で割られるので w を掛け戻す）
    clip = vec4<f32>(
        clip.xy + px_offset * (PRIM3D_NDC_SPAN / cam.viewport) * clip.w,
        clip.z,
        clip.w,
    );

    out.clip_pos = clip;
    out.color    = in.color;
    return out;
}

// ── フラグメントシェーダー ────────────────────────────────────

@fragment
fn fs_main(in: VertOut) -> @location(0) vec4<f32> {
    if (in.color.a < PRIM3D_ALPHA_EPSILON) { discard; }
    return in.color;
}
