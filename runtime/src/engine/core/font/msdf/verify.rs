// ============================================================
//  font/msdf/verify.rs — 字形ごとの検査（安全弁）: MTSDF から描いた形と、高い解像度の参照のラスタを比べる
//
//  【なぜ要るか】
//  画数の多い字・輪郭が重なる字・尖った細い角では、MSDF の色分けや補間が崩れて、偽の縁（ひげ・点）や欠けが出ることがある。
//  焼いた直後に字形ごとに確かめ、崩れていればその字だけ **真の SDF（アルファ）で描く**（bake.rs が RGB へアルファを写す
//  ＝ 中央値がアルファになる。アトラスの中身そのものが印）。
//
//  【比べ方】
//  - 参照: ab_glyph のラスタライザで、MTSDF の `VERIFY_OVERSAMPLE` 倍の大きさで塗った被覆率（≥ 0.5 を内側）。
//    MTSDF の作り方（輪郭の組み直し・距離の式）と独立した実装なので、組み直しの誤りも見つかる。
//  - 描いた形: 同じ画素の中心で MTSDF をバイリニアに読み（GPU と同じくテクセルの中心は i + 0.5）、中央値（またはアルファ）が
//    0.5 以上を内側とする。
//  - 食い違う画素のうち、参照の縁から `VERIFY_EDGE_TOLERANCE_PX`（0.5 テクセル）より深い所にあるものを数える
//    （縁の近くの小さなずれは補間の性質で必ず出るので数えない）。その数が `VERIFY_MAX_ARTIFACT_PX` を超えたら落ちる。
//    深さは「食い違った画素のまわり（許す幅の円の中）に参照の反対側の画素があるか」で決める（食い違いは少ないので、
//    画像全体の距離変換をしない。2026-10-02 の計測で検査が 1 字 1.2〜2.3 ms〈release〉→ 短縮）。
//  - 画素の読みは行ごとに並列（rayon）。補間の内外がセルの中で決まる所は読まない（classify_cells）。
//
//  【消えた細部（`vanished_px`。2026-10-03。レビュー #5）】
//  1 テクセルより細い線は、2 つのテクセルの中心の間に来ると両方が外側の値になり、補間しても 0.5 に届かない（線ごと消える）。
//  消えた画素は参照の縁から 0.5 テクセル以内に入るので `artifact_px` には数えない。そこで「参照では内側なのに描くと外側で、
//  まわり（許す幅の円の中）の参照の内側の画素が 1 つも描かれていない」画素を数える（縁が少しずれただけなら、線の内側の
//  画素は描かれている）。これが `VERIFY_MAX_VANISHED_PX` を超えた字は、bake.rs が em を上げて焼き直す。
// ============================================================

use ab_glyph::{point, Font, FontArc, GlyphId, PxScale};
use rayon::prelude::*;

use super::geometry::median3_f32;
use super::params::{
    MTSDF_BYTES_PER_TEXEL, VERIFY_DETAIL_TOLERANCE_PX, VERIFY_EDGE_TOLERANCE_PX, VERIFY_MAX_ARTIFACT_PX, VERIFY_MAX_VANISHED_PX, VERIFY_OVERSAMPLE,
};

/// 参照の被覆率でこれ以上を内側とする。
const REFERENCE_INSIDE_COVERAGE: f32 = 0.5;
/// 距離場の値でこれ以上を内側とする（縁）。
const FIELD_EDGE: f32 = 0.5;
/// u8 の値の最大。
const BYTE_MAX: f32 = 255.0;

/// どのチャネルで描いた形を確かめるか。
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum VerifyChannel {
    /// MSDF（赤・緑・青の中央値）。
    Median,
    /// 真の SDF（アルファ）。
    Alpha,
}

/// 検査の結果。
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct VerifyReport {
    /// 縁から許す幅より深い所で食い違った画素の数（参照のラスタの画素）。
    pub artifact_px: usize,
    /// 落ちる閾値（これを超えたら落ちる）。
    pub threshold: usize,
    /// 比べた画素の数。
    pub sampled_px: usize,
    /// 縁から `VERIFY_DETAIL_TOLERANCE_PX`（0.25 テクセル）より深い所で食い違った画素の数（細部が潰れた量。計測用）。
    pub detail_px: usize,
    /// 線の幅ごと消えた画素の数（冒頭の【消えた細部】。em を上げるかの判断。`needs_finer_field`）。
    pub vanished_px: usize,
}

impl VerifyReport {
    /// 検査に通ったか。
    pub fn passed(&self) -> bool {
        self.artifact_px <= self.threshold
    }

    /// 線の幅ごと消えた細部が多く、em を上げて焼き直すべきか（`VERIFY_MAX_VANISHED_PX` を超えた）。
    pub fn needs_finer_field(&self) -> bool {
        self.vanished_px > VERIFY_MAX_VANISHED_PX
    }

    /// 消えた細部の面積（em² の単位。検査の画素は em × `VERIFY_OVERSAMPLE` 画素で 1 em なので、違う em どうしで比べられる）。
    pub fn vanished_area_em2(&self, em_px: f32) -> f32 {
        let px_per_em = em_px * VERIFY_OVERSAMPLE as f32;
        self.vanished_px as f32 / (px_per_em * px_per_em)
    }
}

/// 焼いた MTSDF の置き場（MTSDF の 1 テクセルの単位・y 下向き・ペンの基点が原点）。
#[derive(Clone, Copy, Debug)]
pub struct FieldPlacement {
    /// 距離場の左端の x（テクセル）。
    pub left: i32,
    /// 距離場の上端の y（テクセル。y 下向き）。
    pub top: i32,
    /// 幅・高さ（テクセル）。
    pub width: usize,
    pub height: usize,
}

/// RGBA8 の MTSDF を、格子の座標（テクセルの中心が i + 0.5）でバイリニアに読む（範囲の外は 0 = 外側。GPU のアトラスの隙間と同じ）。
pub fn sample_field(data: &[u8], width: usize, height: usize, gx: f32, gy: f32, channel: VerifyChannel) -> f32 {
    let fx = gx - 0.5;
    let fy = gy - 0.5;
    let x0 = fx.floor();
    let y0 = fy.floor();
    let ax = fx - x0;
    let ay = fy - y0;
    let read = |x: i64, y: i64| -> [f32; 4] {
        if x < 0 || y < 0 || x as usize >= width || y as usize >= height {
            return [0.0; 4];
        }
        let k = (y as usize * width + x as usize) * MTSDF_BYTES_PER_TEXEL;
        [
            f32::from(data[k]) / BYTE_MAX,
            f32::from(data[k + 1]) / BYTE_MAX,
            f32::from(data[k + 2]) / BYTE_MAX,
            f32::from(data[k + 3]) / BYTE_MAX,
        ]
    };
    let (x0, y0) = (x0 as i64, y0 as i64);
    let (a, b, c, d) = (read(x0, y0), read(x0 + 1, y0), read(x0, y0 + 1), read(x0 + 1, y0 + 1));
    let mut s = [0.0f32; 4];
    for i in 0..4 {
        let top = a[i] + (b[i] - a[i]) * ax;
        let bottom = c[i] + (d[i] - c[i]) * ax;
        s[i] = top + (bottom - top) * ay;
    }
    match channel {
        VerifyChannel::Median => median3_f32(s[0], s[1], s[2]),
        VerifyChannel::Alpha => s[3],
    }
}

/// セルの塗りの分類: 外側に決まる。
const CELL_OUTSIDE: u8 = 0;
/// セルの塗りの分類: 内側に決まる。
const CELL_INSIDE: u8 = 1;
/// セルの塗りの分類: 画素ごとに読む（縁がありうる）。
const CELL_MIXED: u8 = 2;

/// テクセルの中心どうしを角にするセル（左上の角のテクセル (cx, cy)。-1 から 幅-1 まで）ごとに、
/// 補間した値の内外が決まるかを分類する（読み飛ばして検査を速くする）。
///
/// 4 隅の全てのチャネルが縁より下（上）なら、セルの中の補間した値は全チャネルが縁より下（上）＝中央値・アルファも同じ側。
fn classify_cells(data: &[u8], width: usize, height: usize, channel: VerifyChannel) -> Vec<u8> {
    let edge_byte = FIELD_EDGE * BYTE_MAX;
    // テクセルごと: 0 = 全チャネルが縁より下・1 = 全チャネルが縁より上・2 = それ以外（範囲の外は 0 = 外側）
    let texel_class = |x: i64, y: i64| -> u8 {
        if x < 0 || y < 0 || x as usize >= width || y as usize >= height {
            return CELL_OUTSIDE;
        }
        let k = (y as usize * width + x as usize) * MTSDF_BYTES_PER_TEXEL;
        let channels: &[u8] = match channel {
            VerifyChannel::Median => &data[k..k + 3],
            VerifyChannel::Alpha => &data[k + 3..k + 4],
        };
        if channels.iter().all(|&v| f32::from(v) < edge_byte) {
            CELL_OUTSIDE
        } else if channels.iter().all(|&v| f32::from(v) > edge_byte) {
            CELL_INSIDE
        } else {
            CELL_MIXED
        }
    };
    let cw = width + 1;
    let ch = height + 1;
    let mut cells = vec![CELL_MIXED; cw * ch];
    for cy in 0..ch {
        for cx in 0..cw {
            let (x, y) = (cx as i64 - 1, cy as i64 - 1);
            let c = [texel_class(x, y), texel_class(x + 1, y), texel_class(x, y + 1), texel_class(x + 1, y + 1)];
            cells[cy * cw + cx] = if c.iter().all(|&v| v == CELL_OUTSIDE) {
                CELL_OUTSIDE
            } else if c.iter().all(|&v| v == CELL_INSIDE) {
                CELL_INSIDE
            } else {
                CELL_MIXED
            };
        }
    }
    cells
}

/// 字形を確かめる。参照のラスタが作れない（輪郭が無い）字は「比べるものが無い＝通る」。
pub fn verify_glyph(font: &FontArc, glyph_id: GlyphId, em_px: f32, data: &[u8], place: FieldPlacement, channel: VerifyChannel) -> VerifyReport {
    let k = VERIFY_OVERSAMPLE as i32;
    let threshold = VERIFY_MAX_ARTIFACT_PX;
    // 参照のラスタ（MTSDF の k 倍の大きさ。ペンの基点を原点に置く）
    let glyph = glyph_id.with_scale_and_position(PxScale::from(em_px * k as f32), point(0.0, 0.0));
    let Some(outlined) = font.outline_glyph(glyph) else {
        return VerifyReport { artifact_px: 0, threshold, sampled_px: 0, detail_px: 0, vanished_px: 0 };
    };
    let bounds = outlined.px_bounds();
    let (rx0, ry0) = (bounds.min.x.round() as i32, bounds.min.y.round() as i32);
    let rw = bounds.width().round().max(0.0) as usize;
    let rh = bounds.height().round().max(0.0) as usize;
    let mut coverage = vec![0.0f32; rw * rh];
    outlined.draw(|x, y, c| {
        let idx = y as usize * rw + x as usize;
        if idx < coverage.len() {
            coverage[idx] = c;
        }
    });

    // 比べる範囲 = 距離場の全体（余白も含む。余白に出た偽の縁も数える）を k 倍の画素で
    let hw = place.width * k as usize;
    let hh = place.height * k as usize;
    // 参照の内外（この画素の参照のラスタの中の位置は整数の画素でちょうど重なる）
    let reference_at = |hx: i64, hy: i64| -> bool {
        let rx = i64::from(place.left * k - rx0) + hx;
        let ry = i64::from(place.top * k - ry0) + hy;
        rx >= 0 && ry >= 0 && (rx as usize) < rw && (ry as usize) < rh && coverage[ry as usize * rw + rx as usize] >= REFERENCE_INSIDE_COVERAGE
    };
    // 補間した値の内外がセルの中で決まる所は画素ごとに読まない
    let cells = classify_cells(data, place.width, place.height, channel);
    let cell_w = place.width + 1;
    // 描いた形の内外（画素 hx, hy。範囲の外は外側）
    let rendered_at = |hx: i64, hy: i64| -> bool {
        if hx < 0 || hy < 0 || hx as usize >= hw || hy as usize >= hh {
            return false;
        }
        // 画素の中心の格子の座標（テクセル）と、その画素が入るセル（左上の角のテクセル + 1）
        let gx = (hx as f32 + 0.5) / k as f32;
        let gy = (hy as f32 + 0.5) / k as f32;
        let cx = ((gx - 0.5).floor() as i64 + 1) as usize;
        let cy = ((gy - 0.5).floor() as i64 + 1) as usize;
        match cells[cy * cell_w + cx] {
            CELL_OUTSIDE => false,
            CELL_INSIDE => true,
            _ => sample_field(data, place.width, place.height, gx, gy, channel) >= FIELD_EDGE,
        }
    };
    let tol_sq = (VERIFY_EDGE_TOLERANCE_PX * VERIFY_EDGE_TOLERANCE_PX) as i64;
    let detail_tol_sq = (VERIFY_DETAIL_TOLERANCE_PX * VERIFY_DETAIL_TOLERANCE_PX) as i64;
    let reach = VERIFY_EDGE_TOLERANCE_PX.ceil() as i64;
    // 行ごとに（並列）: 食い違った画素を数え、そのまわりに参照の反対側があるかで深さを決める
    let (artifacts, detail, vanished) = (0..hh)
        .into_par_iter()
        .map(|hy| {
            let mut artifacts = 0usize;
            let mut detail = 0usize;
            let mut vanished = 0usize;
            for hx in 0..hw {
                let inside_ref = reference_at(hx as i64, hy as i64);
                let rendered = rendered_at(hx as i64, hy as i64);
                if rendered == inside_ref {
                    continue;
                }
                // 欠け（参照では内側なのに描くと外側）: まわりの参照の内側が 1 つも描かれていなければ、線の幅ごと消えた画素
                if inside_ref {
                    let mut any_drawn = false;
                    'search: for dy in -reach..=reach {
                        for dx in -reach..=reach {
                            let (nx, ny) = (hx as i64 + dx, hy as i64 + dy);
                            if dx * dx + dy * dy <= tol_sq && reference_at(nx, ny) && rendered_at(nx, ny) {
                                any_drawn = true;
                                break 'search;
                            }
                        }
                    }
                    if !any_drawn {
                        vanished += 1;
                    }
                }
                // 参照の反対側の画素までの二乗距離の最小（許す幅の円の中だけを見る。無ければ幅より深い）
                let mut nearest_sq = i64::MAX;
                for dy in -reach..=reach {
                    for dx in -reach..=reach {
                        let d2 = dx * dx + dy * dy;
                        if d2 <= tol_sq && d2 < nearest_sq && reference_at(hx as i64 + dx, hy as i64 + dy) != inside_ref {
                            nearest_sq = d2;
                        }
                    }
                }
                if nearest_sq > tol_sq {
                    artifacts += 1;
                }
                if nearest_sq > detail_tol_sq {
                    detail += 1;
                }
            }
            (artifacts, detail, vanished)
        })
        .reduce(|| (0, 0, 0), |a, b| (a.0 + b.0, a.1 + b.1, a.2 + b.2));
    debug_assert!(VERIFY_MAX_VANISHED_PX > 0);
    VerifyReport { artifact_px: artifacts, threshold, sampled_px: hw * hh, detail_px: detail, vanished_px: vanished }
}
