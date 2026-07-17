// ============================================================
//  shadow_mask_bilateral.wgsl — 影マスク専用 separable バイラテラルブラー（compute）
//
//  ## なぜ専用ブラーなのか（症状の根治）
//  影マスクのアップサンプルを深度考慮（joint bilateral）にしても、その手前の
//  **いもす法（累積和）ボックスブラー自体が深度エッジを跨いで影値を混ぜている**ため、
//  カーテンのフチと床の境界で影が薄い帯（ハロー）になっていた。累積和（prefix sum）は
//  原理的にバイラテラル重みと両立しない（走査中に窓幅の総和を保持するため、途中で
//  重みを変えられない）。そこで影マスクに限り、固定タップの separable バイラテラル
//  ブラーへ切り替える（AO/SSGI/すりガラスは低周波用途でこの滲みが問題化しないため
//  いもす法のまま）。
//
//  ## アルゴリズム
//  水平→垂直の 2 パス separable。各パスは 1 走査線（行 or 列）を 1 スレッドが端から端まで
//  走査し、各テクセルで窓 [-r, r] を
//     重み w(k) = ガウス空間重み exp(-k^2/2σ^2) × 深度類似度 exp(-|dz-d_center|/tol)
//  で正規化平均する。深度ガイドは**マスク自身の .a**（shadow_mask.wgsl が全レイヤの .a に
//  同梱したビュー空間 z）を読むだけで**ブラーしない**（出力 .a は中心タップの深度をそのまま通す）。
//  別 R32Float アタッチメントを持たない理由: 4×Rgba16Float=32B に 4B を足すと
//  max_color_attachment_bytes_per_sample=32 を超過するため（.a への同梱でバイト予算内に収める）。
//  中心タップ k=0 は空間重み・深度重みとも 1 なので wsum >= 1（ゼロ除算なし）。
//  深度が全周と大きく食い違う細い特徴では中心タップだけが残り、原値を保持する。
//
//  ## いもす法との違い
//  いもす法は半径非依存 O(1)/画素だが非バイラテラル（エッジを滲ませる）。本シェーダは
//  O(2r+1)/画素だが小半径（r=3）かつ半解像度なので実用コストは軽微。エッジ保持のため
//  影マスクだけがこのコストを払う。
//
//  入力  src : マスク 1 レイヤ（rgba16float, .rgb=色付き影込み透過率, .a=ビュー空間 z ガイド）
//  出力  dst : storage rgba16float（.rgb に平均、.a は中心タップの深度を素通し）
// ============================================================

/// バイラテラルブラーパラメータ（CPU 側 ShadowMaskBilateralParams と #[repr(C)] 一致・16B）。
struct BilateralParams {
    /// ブラー半径（画素。>=0）。窓は [-radius, radius]。
    radius:     i32,
    /// 走査方向: 1 = 水平（行を左右に走査） / 0 = 垂直（列を上下に走査）。
    horizontal: u32,
    /// 対象テクスチャ幅（画素, half-res）。
    width:      i32,
    /// 対象テクスチャ高さ（画素, half-res）。
    height:     i32,
};
@group(0) @binding(0) var<uniform> P: BilateralParams;
@group(0) @binding(1) var src: texture_2d<f32>;                       // マスク 1 レイヤ（.rgb=透過率, .a=view_z ガイド）
@group(0) @binding(2) var dst: texture_storage_2d<rgba16float, write>;

/// 空間ガウスの σ を半径から決める係数（σ = radius * frac）。deferred アップサンプルの
/// 相対許容と揃えた「半径のおよそ半分」を基準にする（滑らかさとエッジ保持の妥協点）。
const BILATERAL_SIGMA_FRAC: f32 = 0.5;
/// 深度類似度の相対許容幅（ビュー空間深度に対する割合）。中心との深度差が
/// tol を超えると重みが急減し、別の深度面（奥の床など）のテクセルを混ぜない。
/// deferred アップサンプル（SHADOW_MASK_DEPTH_TOLERANCE_FRAC）と同一流儀・同値。
const BILATERAL_DEPTH_TOL_FRAC: f32 = 0.05;
/// 相対許容幅の下限（ワールド単位）。極近距離で許容幅が 0 に潰れて過敏になるのを防ぐ床値。
const BILATERAL_DEPTH_TOL_MIN: f32 = 0.05;

/// 走査線 `line` 上の走査軸位置 `pos` の 2D 整数座標（imos_blur.wgsl の座標選択と同一規約）。
/// horizontal=1 のとき pos は x（行を左右走査）、0 のとき pos は y（列を上下走査）。
fn coord_of(line: i32, pos: i32) -> vec2<i32> {
    return select(vec2<i32>(line, pos), vec2<i32>(pos, line), P.horizontal == 1u);
}

@compute @workgroup_size(64)
fn blur_cs(@builtin(global_invocation_id) gid: vec3<u32>) {
    let line = i32(gid.x);
    // 走査軸方向の長さ len と、走査線の本数 count を方向で切り替える。
    let len   = select(P.height, P.width,  P.horizontal == 1u);
    let count = select(P.width,  P.height, P.horizontal == 1u);
    if (line >= count || len <= 0) { return; }

    let r     = max(P.radius, 0);
    let sigma = max(f32(r) * BILATERAL_SIGMA_FRAC, 1e-4);
    let inv_2s2 = 1.0 / (2.0 * sigma * sigma);

    for (var i = 0; i < len; i = i + 1) {
        let cc       = coord_of(line, i);
        let center   = textureLoad(src, cc, 0);   // .rgb=透過率, .a=view_z ガイド
        let d_center = center.a;
        // 深度許容幅（中心深度に対する相対値・下限つき）。
        let tol = BILATERAL_DEPTH_TOL_FRAC * max(abs(d_center), BILATERAL_DEPTH_TOL_MIN);

        var acc  = vec3<f32>(0.0);
        var wsum = 0.0;
        for (var k = -r; k <= r; k = k + 1) {
            let p  = clamp(i + k, 0, len - 1);   // 端はクランプ読み
            let c  = coord_of(line, p);
            let fk = f32(k);
            let sample = textureLoad(src, c, 0);
            // 空間ガウス重み × 深度類似度重み。k=0 は両方 1（中心は必ず残る）。
            let ws = exp(-fk * fk * inv_2s2);
            let wd = exp(-abs(sample.a - d_center) / tol);
            let w  = ws * wd;
            acc  = acc + sample.rgb * w;
            wsum = wsum + w;
        }
        // wsum >= 中心タップの w(=1) のため常に >= 1（ゼロ除算なし）。
        // .a（view_z）はブラーしない: 中心タップの深度を素通し（次パス・下流の深度ガイドを維持）。
        textureStore(dst, cc, vec4<f32>(acc / wsum, d_center));
    }
}
