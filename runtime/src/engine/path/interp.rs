// ============================================================
//  path/interp.rs — パス補間の基本演算
//
//  「点列を曲線として読む」ために必要な最小の数学だけを置く。
//  ここは **状態を持たない純関数のみ**（ECS 理念のロジック側）で、
//  川・巡回・カメラパスのいずれからも同じ実装が使われる。
//
//  Catmull-Rom の実装は Phase W4（川スプライン）で書かれたものを
//  用途非依存の位置へ引き上げたものであり、`water::spline` も本実装を使う
//  （＝「川の曲線」と「汎用パスの曲線」が二度と食い違わない）。
// ============================================================

// ─── 定数（マジックナンバー禁止）─────────────────────────────

/// Catmull-Rom のテンション係数（uniform Catmull-Rom の標準値 0.5）。
pub const CATMULL_ROM_TENSION: f32 = 0.5;

/// ゼロ除算・ゼロ長ベクトル判定の下限。
pub const PATH_EPSILON: f32 = 1.0e-6;

// ─── 補間 ────────────────────────────────────────────────────

/// Catmull-Rom スプライン（uniform, τ = `CATMULL_ROM_TENSION`）の 1 点評価。
///
/// `p1`〜`p2` の区間を `t` ∈ [0,1] で補間する。`p0` / `p3` は前後の制御点で、
/// 端点では「1 つ外側の点を折り返して複製」するのが標準的な扱い方
/// （呼び出し側が `p0 = p1` / `p3 = p2` を渡す）。
pub fn catmull_rom(p0: [f32; 3], p1: [f32; 3], p2: [f32; 3], p3: [f32; 3], t: f32) -> [f32; 3] {
    let t2 = t * t;
    let t3 = t2 * t;
    let mut out = [0.0f32; 3];
    for a in 0..3 {
        // 標準形: 0.5 * (2P1 + (-P0+P2)t + (2P0-5P1+4P2-P3)t² + (-P0+3P1-3P2+P3)t³)
        out[a] = CATMULL_ROM_TENSION
            * (2.0 * p1[a]
                + (-p0[a] + p2[a]) * t
                + (2.0 * p0[a] - 5.0 * p1[a] + 4.0 * p2[a] - p3[a]) * t2
                + (-p0[a] + 3.0 * p1[a] - 3.0 * p2[a] + p3[a]) * t3);
    }
    out
}

/// 3 要素ベクトルの線形補間。
pub fn lerp3(a: [f32; 3], b: [f32; 3], t: f32) -> [f32; 3] {
    [
        a[0] + (b[0] - a[0]) * t,
        a[1] + (b[1] - a[1]) * t,
        a[2] + (b[2] - a[2]) * t,
    ]
}

/// 3D 距離。
pub fn distance3(a: [f32; 3], b: [f32; 3]) -> f32 {
    let d = [b[0] - a[0], b[1] - a[1], b[2] - a[2]];
    (d[0] * d[0] + d[1] * d[1] + d[2] * d[2]).sqrt()
}

// ─── ユニットテスト ──────────────────────────────────────────

#[cfg(test)]
mod tests {
    use super::*;

    /// Catmull-Rom が t=0 / t=1 で制御点そのものを通ること（曲線の最重要性質）。
    #[test]
    fn catmull_rom_passes_through_control_points() {
        let p0 = [0.0, 0.0, 0.0];
        let p1 = [1.0, 0.0, 0.0];
        let p2 = [2.0, 1.0, 0.0];
        let p3 = [3.0, 1.0, 0.0];
        assert_eq!(catmull_rom(p0, p1, p2, p3, 0.0), p1);
        let end = catmull_rom(p0, p1, p2, p3, 1.0);
        for a in 0..3 {
            assert!((end[a] - p2[a]).abs() < 1.0e-5, "t=1 で p2 に一致すること");
        }
    }

    /// 一直線に並んだ制御点では、Catmull-Rom も直線を返すこと（曲率の暴走が無い）。
    #[test]
    fn catmull_rom_on_collinear_points_is_straight() {
        let mid = catmull_rom([0.0; 3], [1.0, 0.0, 0.0], [2.0, 0.0, 0.0], [3.0, 0.0, 0.0], 0.5);
        assert!((mid[0] - 1.5).abs() < 1.0e-5);
        assert!(mid[1].abs() < 1.0e-5);
        assert!(mid[2].abs() < 1.0e-5);
    }

    /// 線形補間の端と中点。
    #[test]
    fn lerp3_endpoints_and_midpoint() {
        let a = [0.0, 0.0, 0.0];
        let b = [2.0, 4.0, -6.0];
        assert_eq!(lerp3(a, b, 0.0), a);
        assert_eq!(lerp3(a, b, 1.0), b);
        assert_eq!(lerp3(a, b, 0.5), [1.0, 2.0, -3.0]);
    }

    /// 距離関数（3-4-5 の直角三角形を Z 方向へ）。
    #[test]
    fn distance3_is_euclidean() {
        assert!((distance3([0.0, 0.0, 0.0], [3.0, 4.0, 0.0]) - 5.0).abs() < 1.0e-5);
    }
}
