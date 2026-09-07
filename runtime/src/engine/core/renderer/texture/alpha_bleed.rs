// ============================================================
//  alpha_bleed.rs — アルファブリード（エッジ拡張 / edge dilation）
//
//  【何の問題を解くか】
//  スプライト（2D キャンバス）や UI・パーティクルのテクスチャは
//  「完全不透明（a=255）」と「完全透明（a=0）」だけで作られていることが多い。
//  ところが PNG の**完全透明テクセルの RGB は保存されていないに等しく**、
//  多くのペイントソフト／エクスポータは白（255,255,255）を書き込む。
//
//  GPU 側はストレートアルファ（src=SrcAlpha / dst=OneMinusSrcAlpha）で合成するが、
//  バイリニア補間は **RGB とアルファを独立に**混ぜる。したがって不透明テクセルと
//  透明テクセルの境界では
//      補間 RGB = (絵の色 + 白) / 2 、補間 A = (1 + 0) / 2
//  となり、「白が混ざった色」が半分のアルファで乗る＝**輪郭に白い線**が出る。
//  作者が半透明ピクセルを一切描いていなくても、サンプラーが境界を作るため必ず起きる。
//
//  【対策】
//  透明テクセルの RGB を、隣接する不透明テクセルの色で埋める（にじませる）。
//  アルファは一切変更しないので見た目の形状・抜きは変わらないが、
//  境界で混ざる相手が「白」ではなく「絵と同じ色」になるため白フチが消える。
//  ミップを生成する経路では**ミップ生成より前**に掛けること（ミップが色を継承する）。
//
//  【色空間について】
//  平均は「テクスチャに格納されている空間そのまま」（sRGB エンコード済み 8bit）で取る。
//  厳密にはリニアで平均するのが正しいが、
//    - 隣接不透明テクセルが 1 つだけのときは平均＝そのままのコピーで完全一致、
//    - 複数あるときも「境界の白フチを消す」目的に対して差は視認不能、
//    - リニア往復は 8bit の丸め誤差を増やす、
//  ため、単純平均を採用する（この判断は意図的なものであり、変更するなら
//  テクスチャが sRGB かリニアかをこの関数へ伝える必要がある）。
//
//  【適用対象／非対象（重要な運用ルール）】
//  適用してよいのは「アルファブレンド／アルファマスクで描かれる**色**テクスチャ」だけ。
//    ○ スプライト・キャンバス UI 画像・パーティクル
//    × 法線マップ・ラフネス等のデータテクスチャ、SDF アトラス、ルックアップテーブル、
//      地形レイヤのマスク（RGB が「色」ではなく「数値」なので混ぜてはいけない）
//  ゲートは呼び出し側（ローダ）が用途を知っている位置で行う。
// ============================================================

// ─── 定数（マジックナンバー排除） ─────────────────────────────

/// にじみ出しを広げる最大距離（テクセル）。
/// バイリニア補間が参照するのは隣接 1 テクセルだけだが、
/// 縮小表示（ミニファイ）や将来のミップ生成では数テクセル先まで混ざるため余裕を持たせる。
/// 大きくするほど処理時間が増える（1 段ごとに全画素 1 走査）。
pub const ALPHA_BLEED_MAX_DISTANCE_PX: u32 = 8;

/// 「透明」と見なすアルファ閾値。これ**以下**のテクセルの RGB を塗り替える。
/// 0 のみを対象にする（半透明テクセルの色は作者の意図なので触らない）。
const TRANSPARENT_ALPHA: u8 = 0;

/// RGBA8 の 1 テクセルあたりバイト数。
const BYTES_PER_TEXEL: usize = 4;

/// RGBA8 内の各成分オフセット。
const OFFSET_R: usize = 0;
const OFFSET_G: usize = 1;
const OFFSET_B: usize = 2;
const OFFSET_A: usize = 3;

/// 走査する近傍（8 近傍）。斜めも含めることで角の 1 テクセル欠けを防ぐ。
const NEIGHBOR_OFFSETS: [(i64, i64); 8] = [
    (-1, -1),
    (0, -1),
    (1, -1),
    (-1, 0),
    (1, 0),
    (-1, 1),
    (0, 1),
    (1, 1),
];

// ============================================================
//  本体
// ============================================================

/// RGBA8 画素列にアルファブリードを掛ける（in-place）。
///
/// * `width` / `height` — 画像サイズ（テクセル）。
/// * `pixels` — 長さ `width * height * 4` の RGBA8 バイト列。破壊的に書き換える。
/// * `max_distance` — にじみ出しを広げる最大距離（テクセル）。通常は
///   [`ALPHA_BLEED_MAX_DISTANCE_PX`] を渡す。
///
/// アルファは一切変更しない。戻り値は「実際に 1 テクセルでも書き換えたか」。
///
/// 早期リターン（性能ガード）:
///   - サイズ不整合・空画像
///   - 完全不透明（a<255 のテクセルが 1 つも無い）画像 … 大多数の 3D テクスチャがここで抜ける
///   - 完全透明テクセルが無い、または不透明テクセルが 1 つも無い画像
pub fn bleed_alpha_edges(width: u32, height: u32, pixels: &mut [u8], max_distance: u32) -> bool {
    let texel_count = (width as usize) * (height as usize);
    // サイズ不整合・空画像は何もしない（呼び出し側の想定外入力に対する保険）。
    if texel_count == 0 || pixels.len() != texel_count * BYTES_PER_TEXEL || max_distance == 0 {
        return false;
    }

    // ── 性能ガード＆種テクセルの収集 ─────────────────────────
    // filled[i] = 「そのテクセルの RGB は信頼できる（不透明 or 既にブリード済み）」
    let mut filled = vec![false; texel_count];
    let mut has_partial_alpha = false; // a < 255 が 1 つでもあるか
    let mut has_transparent = false; // a == 0 が 1 つでもあるか
    let mut opaque_count = 0usize; // ブリードの種になるテクセル数
    for i in 0..texel_count {
        let a = pixels[i * BYTES_PER_TEXEL + OFFSET_A];
        if a < u8::MAX {
            has_partial_alpha = true;
        }
        if a == TRANSPARENT_ALPHA {
            has_transparent = true;
        } else {
            // 半透明（0 < a < 255）も色は作者が描いた値なので「種」として扱う。
            filled[i] = true;
            opaque_count += 1;
        }
    }
    // 完全不透明画像／透明テクセルが無い／種が無い（全面透明）ならやることは無い。
    if !has_partial_alpha || !has_transparent || opaque_count == 0 {
        return false;
    }

    // ── 多段ダイレーション ───────────────────────────────
    // 「前段までに filled になったテクセル」だけを参照して 1 段広げる、を繰り返す。
    // 参照元を段ごとに固定することで、走査順に依存しない（決定的な）結果になる。
    let mut changed_any = false;
    // 次段の種候補（前段で新たに埋めたテクセル）。初段は全不透明テクセルが種。
    // **初段は「透明テクセルに隣接する不透明テクセル（＝輪郭線）」だけ**を種にする。
    // 全不透明テクセルを種にすると絵の内側が丸ごと無駄走査になり、
    // 大きなスプライトでロードヒッチになる（走査量を面積 → 周長へ落とす）。
    let mut frontier: Vec<usize> = (0..texel_count)
        .filter(|&i| filled[i] && has_unfilled_neighbor(i, width, height, &filled))
        .collect();

    // 「この段で既にキューへ積んだか」の判定用スタンプ。段ごとに Vec を確保し直すと
    // 大きな画像では確保コストが無視できないため、1 本を使い回して段番号で無効化する。
    let mut queued_stamp = vec![0u32; texel_count];

    for pass in 1..=max_distance {
        // この段で埋める対象（透明かつ未 filled で、frontier に隣接するテクセル）を集める。
        // 同じテクセルが複数の frontier から挙がるため、重複は書き込み時に弾く。
        let mut newly_filled: Vec<usize> = Vec::new();

        for &seed in &frontier {
            let sx = (seed % width as usize) as i64;
            let sy = (seed / width as usize) as i64;
            for (dx, dy) in NEIGHBOR_OFFSETS {
                let (nx, ny) = (sx + dx, sy + dy);
                if nx < 0 || ny < 0 || nx >= width as i64 || ny >= height as i64 {
                    continue;
                }
                let ni = (ny as usize) * (width as usize) + (nx as usize);
                if filled[ni] || queued_stamp[ni] == pass {
                    continue;
                }
                queued_stamp[ni] = pass;
                newly_filled.push(ni);
            }
        }
        // これ以上広げられない（全域が埋まった）なら終了。
        if newly_filled.is_empty() {
            break;
        }

        // 対象テクセルの RGB を「filled な 8 近傍の平均」で埋める。
        // 近傍が 1 つだけなら平均＝そのままのコピーになる（色を歪めない）。
        for &target in &newly_filled {
            let tx = (target % width as usize) as i64;
            let ty = (target / width as usize) as i64;
            let mut sum = [0u32; 3];
            let mut count = 0u32;
            for (dx, dy) in NEIGHBOR_OFFSETS {
                let (nx, ny) = (tx + dx, ty + dy);
                if nx < 0 || ny < 0 || nx >= width as i64 || ny >= height as i64 {
                    continue;
                }
                let ni = (ny as usize) * (width as usize) + (nx as usize);
                // 「この段の開始時点で filled だったもの」だけを参照する
                //（newly_filled はまだ filled=false のままなので自然に除外される）。
                if !filled[ni] {
                    continue;
                }
                let base = ni * BYTES_PER_TEXEL;
                sum[0] += pixels[base + OFFSET_R] as u32;
                sum[1] += pixels[base + OFFSET_G] as u32;
                sum[2] += pixels[base + OFFSET_B] as u32;
                count += 1;
            }
            if count == 0 {
                continue; // 理論上到達しない（frontier 隣接なので必ず 1 以上）
            }
            let base = target * BYTES_PER_TEXEL;
            // 四捨五入（+count/2）。アルファ（OFFSET_A）は触らない。
            pixels[base + OFFSET_R] = ((sum[0] + count / 2) / count) as u8;
            pixels[base + OFFSET_G] = ((sum[1] + count / 2) / count) as u8;
            pixels[base + OFFSET_B] = ((sum[2] + count / 2) / count) as u8;
        }

        // 今段で埋めたものを filled に反映し、次段の種にする。
        for &i in &newly_filled {
            filled[i] = true;
        }
        frontier = newly_filled;
        changed_any = true;
    }

    changed_any
}

/// テクセル `index` の 8 近傍に「まだ色が確定していない（filled=false）」テクセルがあるか。
/// 初段の種を輪郭テクセルだけに絞るために使う。
fn has_unfilled_neighbor(index: usize, width: u32, height: u32, filled: &[bool]) -> bool {
    let x = (index % width as usize) as i64;
    let y = (index / width as usize) as i64;
    NEIGHBOR_OFFSETS.iter().any(|&(dx, dy)| {
        let (nx, ny) = (x + dx, y + dy);
        if nx < 0 || ny < 0 || nx >= width as i64 || ny >= height as i64 {
            return false;
        }
        !filled[(ny as usize) * (width as usize) + (nx as usize)]
    })
}

/// 既定距離（[`ALPHA_BLEED_MAX_DISTANCE_PX`]）でアルファブリードを掛ける薄いラッパ。
/// ローダ側はこちらを呼ぶ（距離をローダごとにバラつかせないため）。
pub fn bleed_alpha_edges_default(width: u32, height: u32, pixels: &mut [u8]) -> bool {
    bleed_alpha_edges(width, height, pixels, ALPHA_BLEED_MAX_DISTANCE_PX)
}

// ============================================================
//  テスト
// ============================================================
#[cfg(test)]
mod tests {
    use super::*;

    /// テスト用: 白い透明背景＋赤い不透明矩形の画像を作る。
    /// `w`×`h` のうち `rect`（x0, y0, x1, y1 の半開区間）が不透明赤。
    fn white_bg_red_square(w: u32, h: u32, rect: (u32, u32, u32, u32)) -> Vec<u8> {
        let mut px = vec![0u8; (w * h) as usize * BYTES_PER_TEXEL];
        for y in 0..h {
            for x in 0..w {
                let i = (y * w + x) as usize * BYTES_PER_TEXEL;
                let inside = x >= rect.0 && x < rect.2 && y >= rect.1 && y < rect.3;
                if inside {
                    px[i + OFFSET_R] = 255;
                    px[i + OFFSET_G] = 0;
                    px[i + OFFSET_B] = 0;
                    px[i + OFFSET_A] = 255;
                } else {
                    // エクスポータがよく書く「白い完全透明」
                    px[i + OFFSET_R] = 255;
                    px[i + OFFSET_G] = 255;
                    px[i + OFFSET_B] = 255;
                    px[i + OFFSET_A] = 0;
                }
            }
        }
        px
    }

    /// 指定テクセルの RGBA を取り出す。
    fn texel(px: &[u8], w: u32, x: u32, y: u32) -> [u8; 4] {
        let i = (y * w + x) as usize * BYTES_PER_TEXEL;
        [px[i], px[i + 1], px[i + 2], px[i + 3]]
    }

    /// 4×4・中央 2×2 が不透明赤。ブリード後、隣接する透明テクセルが赤になること。
    #[test]
    fn transparent_neighbors_take_opaque_color() {
        let (w, h) = (4u32, 4u32);
        let mut px = white_bg_red_square(w, h, (1, 1, 3, 3));
        assert!(bleed_alpha_edges_default(w, h, &mut px));

        // 4 隅を含む全ての透明テクセルは、4×4・最大距離 8 なら全て赤で埋まる。
        for y in 0..h {
            for x in 0..w {
                let t = texel(&px, w, x, y);
                assert_eq!(
                    [t[0], t[1], t[2]],
                    [255, 0, 0],
                    "({x},{y}) が赤で埋まっていない: {t:?}"
                );
            }
        }
    }

    /// アルファは 1 バイトも変わらないこと（形状・抜きが変化しない保証）。
    #[test]
    fn alpha_channel_is_untouched() {
        let (w, h) = (4u32, 4u32);
        let mut px = white_bg_red_square(w, h, (1, 1, 3, 3));
        let before: Vec<u8> = px.iter().skip(OFFSET_A).step_by(BYTES_PER_TEXEL).copied().collect();
        bleed_alpha_edges_default(w, h, &mut px);
        let after: Vec<u8> = px.iter().skip(OFFSET_A).step_by(BYTES_PER_TEXEL).copied().collect();
        assert_eq!(before, after);
    }

    /// 完全不透明画像は 1 バイトも書き換えない（性能ガードが効く）。
    #[test]
    fn fully_opaque_image_is_untouched() {
        let (w, h) = (3u32, 3u32);
        let mut px = vec![0u8; (w * h) as usize * BYTES_PER_TEXEL];
        for (i, b) in px.iter_mut().enumerate() {
            // R=連番, G/B=0, A=255 の適当な不透明画像
            *b = match i % BYTES_PER_TEXEL {
                OFFSET_R => (i as u8).wrapping_mul(7),
                OFFSET_A => 255,
                _ => 0,
            };
        }
        let before = px.clone();
        assert!(!bleed_alpha_edges_default(w, h, &mut px));
        assert_eq!(before, px);
    }

    /// 全面透明（種が無い）画像は変化しないこと。
    #[test]
    fn fully_transparent_image_is_untouched() {
        let (w, h) = (2u32, 2u32);
        let mut px = vec![255u8; (w * h) as usize * BYTES_PER_TEXEL];
        for i in 0..(w * h) as usize {
            px[i * BYTES_PER_TEXEL + OFFSET_A] = 0;
        }
        let before = px.clone();
        assert!(!bleed_alpha_edges_default(w, h, &mut px));
        assert_eq!(before, px);
    }

    /// 半透明テクセル（0 < a < 255）の色は作者の意図なので書き換えないこと。
    #[test]
    fn semi_transparent_texels_keep_their_color() {
        let (w, h) = (3u32, 1u32);
        // [不透明赤][半透明緑][完全透明白]
        let mut px = vec![
            255, 0, 0, 255, //
            0, 255, 0, 128, //
            255, 255, 255, 0,
        ];
        assert!(bleed_alpha_edges_default(w, h, &mut px));
        assert_eq!(texel(&px, w, 1, 0), [0, 255, 0, 128], "半透明テクセルが書き換わった");
        // 透明テクセルは隣（不透明赤＋半透明緑）の平均で埋まる＝白ではなくなる。
        let t = texel(&px, w, 2, 0);
        assert_ne!([t[0], t[1], t[2]], [255, 255, 255], "透明テクセルが白のまま");
        assert_eq!(t[3], 0, "アルファが変わった");
    }

    /// 最大距離を 1 に絞ると、1 テクセルぶんしか広がらないこと（距離制御の検証）。
    #[test]
    fn max_distance_limits_spread() {
        let (w, h) = (5u32, 1u32);
        // 左端だけ不透明赤、残りは白い完全透明
        let mut px = white_bg_red_square(w, h, (0, 0, 1, 1));
        assert!(bleed_alpha_edges(w, h, &mut px, 1));
        assert_eq!(texel(&px, w, 1, 0), [255, 0, 0, 0], "隣接テクセルが埋まっていない");
        assert_eq!(texel(&px, w, 2, 0), [255, 255, 255, 0], "距離 1 を超えて広がった");
    }

    /// サイズと画素長が食い違う入力では何もしない（保険）。
    #[test]
    fn mismatched_buffer_is_rejected() {
        let mut px = vec![0u8; 5];
        assert!(!bleed_alpha_edges_default(2, 2, &mut px));
    }
}
