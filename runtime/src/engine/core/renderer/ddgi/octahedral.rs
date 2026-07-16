// ============================================================
// ddgi/octahedral.rs — 八面体マップ（Octahedral mapping）の Rust 実装
//
// ## 役割（単一責任）
// 単位方向ベクトル ↔ 正方 UV（[0,1]^2）の相互変換だけを持つ純関数群。
// GPU 側（ddgi_common.wgsl の oct_encode / oct_decode）と**同一の式**であること。
// ここに Rust ミラーを持つのは:
//   - 往復（方向→UV→方向）が単位ベクトルで一致することを cargo test で機械的に担保し、
//   - 「WGSL と式がズレてプローブの向きが化ける」種の無言バグを CI で止めるため。
//
// ## 式の由来（八面体投影）
// 単位球を八面体へ射影し、上半球はそのまま、下半球は折り返して正方形 [-1,1]^2 に敷く。
// Cigolle et al. "A Survey of Efficient Representations for Independent Unit Vectors"
// の標準式で、RTXGI/DDGI のプローブ格納で使われるものと同一。
//   - 方向 → oct: p = dir.xy / (|x|+|y|+|z|); 下半球は (1-|p.yx|)*sign(p) で折り返す。
//   - oct → 方向: z = 1-|u|-|v|; z<0 のとき xy を (1-|vu|)*sign 折り返す。
// 返す UV は [-1,1] を [0,1] に写像したもの（タイル内テクセル座標に使いやすい）。
// ============================================================

/// 2 成分の符号（0 は +1 に寄せる。折り返しの安定化のため WGSL 実装と一致させる）。
#[inline]
fn sign2(x: f32, y: f32) -> (f32, f32) {
    let sx = if x >= 0.0 { 1.0 } else { -1.0 };
    let sy = if y >= 0.0 { 1.0 } else { -1.0 };
    (sx, sy)
}

/// 単位方向ベクトル（正規化済み想定）を八面体 UV（[0,1]^2）へ符号化する。
/// WGSL の `oct_encode`（ddgi_common.wgsl）と厳密に同一の式であること。
pub fn oct_encode(dir: [f32; 3]) -> [f32; 2] {
    let (x, y, z) = (dir[0], dir[1], dir[2]);
    let l1 = x.abs() + y.abs() + z.abs();
    let inv = if l1 > 1e-8 { 1.0 / l1 } else { 0.0 };
    let mut px = x * inv;
    let mut py = y * inv;
    if z < 0.0 {
        let (sx, sy) = sign2(px, py);
        let nx = (1.0 - py.abs()) * sx;
        let ny = (1.0 - px.abs()) * sy;
        px = nx;
        py = ny;
    }
    [px * 0.5 + 0.5, py * 0.5 + 0.5]
}

/// 八面体 UV（[0,1]^2）を単位方向ベクトルへ復号する。
/// WGSL の `oct_decode`（ddgi_common.wgsl）と厳密に同一の式であること。
pub fn oct_decode(uv: [f32; 2]) -> [f32; 3] {
    let mut x = uv[0] * 2.0 - 1.0;
    let mut y = uv[1] * 2.0 - 1.0;
    let z = 1.0 - x.abs() - y.abs();
    if z < 0.0 {
        let (sx, sy) = sign2(x, y);
        let nx = (1.0 - y.abs()) * sx;
        let ny = (1.0 - x.abs()) * sy;
        x = nx;
        y = ny;
    }
    let len = (x * x + y * y + z * z).sqrt();
    let inv = if len > 1e-8 { 1.0 / len } else { 0.0 };
    [x * inv, y * inv, z * inv]
}

#[cfg(test)]
mod tests {
    use super::*;

    fn dot(a: [f32; 3], b: [f32; 3]) -> f32 {
        a[0] * b[0] + a[1] * b[1] + a[2] * b[2]
    }

    /// 往復（方向 → UV → 方向）が単位ベクトルで一致すること（フィボナッチ球 4096 方向）。
    /// 式が WGSL とズレたり下半球の折り返しを誤ると内積が 1 から外れるため機械的に止める。
    #[test]
    fn octahedral_roundtrip_is_identity() {
        const N: usize = 4096;
        let golden = std::f32::consts::PI * (3.0 - 5f32.sqrt());
        let mut worst = 1.0f32;
        for i in 0..N {
            let t = (i as f32 + 0.5) / N as f32;
            let z = 1.0 - 2.0 * t;
            let r = (1.0 - z * z).max(0.0).sqrt();
            let phi = golden * i as f32;
            let dir = [r * phi.cos(), r * phi.sin(), z];
            let uv = oct_encode(dir);
            assert!(
                (0.0..=1.0).contains(&uv[0]) && (0.0..=1.0).contains(&uv[1]),
                "oct_encode の UV が [0,1] を外れました: {uv:?} (dir={dir:?})"
            );
            let back = oct_decode(uv);
            worst = worst.min(dot(dir, back));
        }
        assert!(
            worst > 0.9999,
            "八面体往復の最悪内積が {worst}（1.0 に近いはず）。式ズレを疑うこと"
        );
    }

    /// 主要軸方向の健全性（+Z は中心、-Z は往復で戻る）。
    #[test]
    fn octahedral_axis_directions() {
        let uv = oct_encode([0.0, 0.0, 1.0]);
        assert!((uv[0] - 0.5).abs() < 1e-5 && (uv[1] - 0.5).abs() < 1e-5, "+Z は中心へ: {uv:?}");
        let back = oct_decode(oct_encode([0.0, 0.0, -1.0]));
        assert!(dot([0.0, 0.0, -1.0], back) > 0.9999, "-Z 往復: {back:?}");
    }
}
