// ============================================================
//  imos_blur.wgsl — いもす法（累積和 / 走査線ランニング和）単一チャンネル分離ボックスブラー
//
//  【アルゴリズム】postfx_blur.wgsl と**完全に同一のランニング和アルゴリズム**。
//  唯一の違いは、扱うデータを単一チャンネル（.r）に限定する点だけである。
//  走査線ごとに幅 (2r+1) の窓の総和を保持し、1 画素進むごとに
//  「先頭を足し末尾を引く」ランニング和で更新する（＝いもす法／累積和の応用）。
//  半径 r 非依存の O(1)/画素・O(n)/走査線。分離可能性を使い水平→垂直の 2 方向へ分解する。
//  1 コンピュート起動＝1 走査線（行 or 列）を担当し、端から端まで走査する。
//  CPU 側（imos_blur.rs）が水平／垂直を切り替えて iterations 回反復（＝ガウシアン近似）する。
//
//  【純ボックス和のエッジについて（既知の限界）】
//  純ボックス和は深度エッジを跨いで滲む（バイラテラルではない）。AO は低周波信号なので
//  この滲みは許容する。エッジ保持は将来課題（バイラテラルフィルタは prefix sum と
//  両立しないため、ガイド付きの別方式＝ジョイントバイラテラルアップサンプル等が必要）。
//
//  【ストレージフォーマットの注意（単一チャンネル R16Float は core WebGPU 非対応）】
//  単一チャンネル R16Float は core WebGPU の storage テクスチャフォーマット集合に**含まれない**
//  （wgpu-types guaranteed_format_features: `R16Float => (msaa_resolve, attachment)` で
//  STORAGE_BINDING 不可。TEXTURE_ADAPTER_SPECIFIC_FORMAT_FEATURES が要る）。
//  そのため storage フォーマットは Rgba16Float（storage 可・かつ filterable）とし、
//  **.r のみ**を意味あるチャンネルとして使う（他 3 チャンネルは 0 を書く）。
//  postfx_blur.wgsl（同じく rgba16float storage）と storage 型は一致する。
//
//  【将来 SSGI（カラー）への転用】
//  本シェーダを色ブラー（SSGI 等）へ転用する場合、フォーマットは Rgba16Float のままで
//  load/store を .r → .rgb（または vec4 全体）へ広げるだけでよい（フォーマット変更不要）。
//
//  入力: texture_2d<f32>（textureLoad で整数座標アクセス。サンプラー不要。.rgb を集計）
//  出力: texture_storage_2d<rgba16float, write>（.rgb に平均、a は 0）
//  ※ .rgb 集計へ広げ済み（SSGI カラー転用）。ランニング和はチャンネル独立なので、
//    AO のように .r しか使わない用途では赤チャンネルの結果は以前と不変。
// ============================================================

/// いもすブラーパラメータ（CPU 側 ImosBlurParams と #[repr(C)] 一致・16B）。
struct ImosBlurParams {
    /// ボックス半径（画素。>=0）。
    radius:     i32,
    /// 走査方向: 1 = 水平（行を左右に走査） / 0 = 垂直（列を上下に走査）。
    horizontal: u32,
    /// 対象テクスチャ幅（画素）。
    width:      i32,
    /// 対象テクスチャ高さ（画素）。
    height:     i32,
};
@group(0) @binding(0) var<uniform> P: ImosBlurParams;
@group(0) @binding(1) var src: texture_2d<f32>;
@group(0) @binding(2) var dst: texture_storage_2d<rgba16float, write>;

/// 走査線 `line` 上の走査軸位置 `pos` の画素（.rgb）を読む（端はクランプ）。
/// postfx_blur.wgsl の load_px と同一の座標選択。
///
/// 【カラー対応（SSGI 転用）】以前は .r だけを扱っていたが、カラー信号（SSGI 等）へ
/// 転用するため .rgb（3 チャンネル）を集計するよう広げた。ランニング和は**チャンネルごとに
/// 独立**なので、AO のように .r しか使わない用途では赤チャンネルの結果は以前と
/// **ビット単位で不変**（AO の raw は g=b=0 のまま流れ、AO 側は .r のみ読むため影響なし）。
fn load_rgb(line: i32, pos: i32, len: i32) -> vec3<f32> {
    let c = clamp(pos, 0, len - 1);
    let coord = select(vec2<i32>(line, c), vec2<i32>(c, line), P.horizontal == 1u);
    return textureLoad(src, coord, 0).rgb;
}

/// 走査線 `line` 上の走査軸位置 `pos` へ書き込む（.rgb に値、a は 0）。
fn store_rgb(line: i32, pos: i32, val: vec3<f32>) {
    let coord = select(vec2<i32>(line, pos), vec2<i32>(pos, line), P.horizontal == 1u);
    textureStore(dst, coord, vec4<f32>(val, 0.0));
}

@compute @workgroup_size(64)
fn blur_cs(@builtin(global_invocation_id) gid: vec3<u32>) {
    let line = i32(gid.x);
    // 走査軸方向の長さ len と、走査線の本数 count を方向で切り替える。
    let len   = select(P.height, P.width,  P.horizontal == 1u);
    let count = select(P.width,  P.height, P.horizontal == 1u);
    if (line >= count || len <= 0) { return; }

    let r    = max(P.radius, 0);
    let norm = 1.0 / f32(2 * r + 1); // postfx_blur.wgsl と同一の正規化 1/(2r+1)

    // 位置 0 における窓 [-r, r] の初期総和（端はクランプ読み・rgb 各チャンネル独立）。
    var sum = vec3<f32>(0.0);
    for (var k = -r; k <= r; k = k + 1) {
        sum = sum + load_rgb(line, k, len);
    }

    // 走査線を 1 画素ずつ進めながら平均を書き出し、窓をスライドする。
    // 窓を右（下）へ 1 進める: 新たに入る (i+r+1) を足し、外れる (i-r) を引く
    // （postfx_blur.wgsl と同一式: sum += load(i+r+1) - load(i-r)）。
    for (var i = 0; i < len; i = i + 1) {
        store_rgb(line, i, sum * norm);
        sum = sum + load_rgb(line, i + r + 1, len) - load_rgb(line, i - r, len);
    }
}
