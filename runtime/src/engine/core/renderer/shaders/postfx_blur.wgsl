// ============================================================
//  postfx_blur.wgsl — いもす法（累積和 / 走査線ランニング和）分離ボックスブラー
//
//  【方針（rendering_roadmap.md「ブラー系実装の方針」準拠）】
//  ガウシアン相当のブラーを「ボックスフィルタ 3 回反復」で近似する。
//  ボックスフィルタは走査線ごとに幅 (2r+1) の窓の総和を保持し、
//  1 画素進むごとに「先頭を足し末尾を引く」ランニング和で更新する
//  （＝いもす法／累積和の応用）。これにより半径 r に依存しない
//  実質 O(1)/画素・O(n)/走査線 で処理でき、大カーネルほど従来のタップ法より速い。
//
//  分離可能性を使い「水平（行走査）→ 垂直（列走査）」の 2 方向に分解する。
//  1 コンピュート起動＝1 走査線（行 or 列）を担当し、その線を端から端まで
//  ランニング和で走査する。CPU 側が水平／垂直を切り替えて 3 往復（計 6 パス）呼ぶ。
//
//  入力: texture_2d<f32>（textureLoad で整数座標アクセス。サンプラー不要）
//  出力: texture_storage_2d<rgba16float, write>（作業バッファはリニア HDR）
// ============================================================

/// ブラーパラメータ（CPU 側 BlurParams と #[repr(C)] 一致）。
struct BlurParams {
    /// ボックス半径（画素。>=0）。
    radius:     i32,
    /// 走査方向: 1 = 水平（行を左右に走査） / 0 = 垂直（列を上下に走査）。
    horizontal: u32,
    /// 作業テクスチャ幅（画素）。
    width:      i32,
    /// 作業テクスチャ高さ（画素）。
    height:     i32,
};
@group(0) @binding(0) var<uniform> P: BlurParams;
@group(0) @binding(1) var src: texture_2d<f32>;
@group(0) @binding(2) var dst: texture_storage_2d<rgba16float, write>;

/// 走査線 `line` 上の走査軸位置 `pos` の画素を読む（端はクランプ）。
/// horizontal=1 なら (pos, line)、=0 なら (line, pos) の座標に対応する。
fn load_px(line: i32, pos: i32, len: i32) -> vec4<f32> {
    let c = clamp(pos, 0, len - 1);
    let coord = select(vec2<i32>(line, c), vec2<i32>(c, line), P.horizontal == 1u);
    return textureLoad(src, coord, 0);
}

/// 走査線 `line` 上の走査軸位置 `pos` へ書き込む。
fn store_px(line: i32, pos: i32, val: vec4<f32>) {
    let coord = select(vec2<i32>(line, pos), vec2<i32>(pos, line), P.horizontal == 1u);
    textureStore(dst, coord, val);
}

@compute @workgroup_size(64)
fn blur_cs(@builtin(global_invocation_id) gid: vec3<u32>) {
    let line = i32(gid.x);
    // 走査軸方向の長さ len と、走査線の本数 count を方向で切り替える。
    let len   = select(P.height, P.width,  P.horizontal == 1u);
    let count = select(P.width,  P.height, P.horizontal == 1u);
    if (line >= count || len <= 0) { return; }

    let r    = max(P.radius, 0);
    let norm = 1.0 / f32(2 * r + 1);

    // 位置 0 における窓 [-r, r] の初期総和（端はクランプ読み）。
    var sum = vec4<f32>(0.0);
    for (var k = -r; k <= r; k = k + 1) {
        sum = sum + load_px(line, k, len);
    }

    // 走査線を 1 画素ずつ進めながら平均を書き出し、窓をスライドする。
    for (var i = 0; i < len; i = i + 1) {
        store_px(line, i, sum * norm);
        // 窓を右（下）へ 1 進める: 新たに入る (i+r+1) を足し、外れる (i-r) を引く。
        sum = sum + load_px(line, i + r + 1, len) - load_px(line, i - r, len);
    }
}
