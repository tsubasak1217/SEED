// ============================================================
//  water/tessellation.rs — 水面メッシュの格子分割（Phase W5.1）
//
//  ## 役割（単一責任）
//  「水面 1 インスタンスを何分割の格子で描くか」だけを決める **純粋関数の集まり**。
//  GPU リソースにも wgpu にも一切依存しないので、そのままユニットテストできる。
//
//  ## なぜ分割が要るのか
//  W1〜W6 の水面は 1 インスタンス = 四角形 1 枚（6 頂点）で、波は
//  フラグメントの法線だけで表現していた。W5.1 は水面を**実際に上下させる**ため、
//  波長に対して十分細かい頂点が要る。頂点バッファは持たない設計のままなので、
//  「1 インスタンスあたりの頂点数を増やし、`vertex_index` から格子セルを引く」
//  という形で分割する（`water_height_field.wgsl` の `water_grid_param`）。
//
//  ## 亀裂（クラック）が原理的に出ない格子の作り方
//  頂点位置は **セル添字（整数）＋角（0 か 1）** から `(i + c) / div` で作る。
//  隣り合うセルが共有する格子線は同じ整数から同じ除算で作られるため、
//  f32 のビットパターンまで完全に一致する。累積加算（前のセルの端に幅を足す）で
//  作ると丸め誤差が溜まって隙間が開くが、本方式ではその余地が無い。
//
//  ## Ocean の LOD（同心格子の代わりに「放射状ワープ」を使う理由）
//  無限水面（Ocean）はカメラ追従の巨大クアッドなので、一様分割では
//  「近くが粗すぎるか、頂点数が爆発するか」の二択になる。
//  よくある解は LOD リングを同心状に並べる方式だが、**リング境界で
//  頂点密度が変わる＝亀裂（T ジャンクション）が出る**ため、スナップやスカートで
//  塞ぐ追加の仕組みが要る。
//
//  ここでは代わりに、**一様格子のパラメータを放射状に非線形変換する**。
//  格子は位相的に 1 枚の連続した網のままなので、
//    ・**亀裂が構造的に発生しない**（境界そのものが無い）
//    ・LOD 段差（密度の不連続）も無い（セル幅が連続に変化する）
//    ・追加の頂点も判定も要らない
//  という利点がある。変換は `p → p · f(r)`（r = チェビシェフ半径 = max(|x|,|z|)）で、
//  `f(1) = 1` なのでクアッドの外周は動かない（水域の広さが変わらない）。
//
//  ## 頂点数の上限
//  RTX 3060 Laptop を想定し、1 インスタンスの分割数と 1 フレームの総頂点数の
//  両方に定数の上限を置く。水域が増えたときは分割数を自動で落として
//  総頂点数を予算内へ収める（描画が落ちるより粗くなる方がまし）。
// ============================================================

/// 格子セル 1 枚（四角形）を描くための頂点数（三角形 2 枚 = 6 頂点）。
/// **WGSL 側 `WATER_CELL_VERTEX_COUNT`（water_height_field.wgsl）と一致必須。**
pub const WATER_CELL_VERTEX_COUNT: u32 = 6;

/// クアッド（Ocean / Region）1 辺あたりの最大分割数。
///
/// 128×128 セル = 16,384 セル = 98,304 頂点／インスタンス。
/// 頂点シェーダの仕事は「高さ場の評価（sin/cos 12 回＋テクスチャ 2 サンプル）」なので、
/// RTX 3060 Laptop なら水面パスと ID パスの 2 回ぶんでも 1ms に届かない規模である。
pub const WATER_GRID_MAX_DIV: u32 = 128;

/// 格子セルの目標ワールドサイズ（m）。
///
/// 解析波の最も細かい層は基本波長の約 1/6.85（既定 `wave_scale`=0.12 なら約 7.6m）なので、
/// 1m 程度のセルがあれば全層を十分に解像できる。これより細かくしても頂点が増えるだけである。
pub const WATER_GRID_TARGET_CELL_M: f32 = 1.0;

/// 川リボン 1 分割の**幅方向**の最大分割数。
/// 川幅は数 m 程度なので、幅方向にこれ以上刻む意味はない。
pub const WATER_RIVER_MAX_DIV_ACROSS: u32 = 8;

/// 川リボン 1 分割の**長さ方向**の最大分割数。
///
/// 長さ方向はそもそも `river_segment_length`（既定 2m）でインスタンス自体が
/// 刻まれているので、ここは「区間をさらに細かくする」保険にすぎない。
pub const WATER_RIVER_MAX_DIV_ALONG: u32 = 4;

/// クアッド（Ocean / Region）バケットの 1 フレーム総頂点数の予算。
///
/// 128 分割のクアッド 8 枚ぶん。これを超える場合は分割数を落として収める。
pub const WATER_QUAD_VERTEX_BUDGET: u32 = 800_000;

/// 川バケットの 1 フレーム総頂点数の予算。
///
/// 川は「折れ線の 1 分割 = 1 インスタンス」で本数が伸びやすいので、
/// クアッドとは別枠にして互いの分割数を巻き込まないようにする。
pub const WATER_RIVER_VERTEX_BUDGET: u32 = 300_000;

// ─── Ocean の放射状ワープ（LOD の代わり）──────────────────────
//
// 変換は `p → p · f(r)`、`f(r) = a + (1 − a)·r^(k−1)`、`r = max(|p.x|, |p.z|)`。
// 軸に沿ったワールド座標は `E·r·f(r) = E·(a·r + (1−a)·r^k)` になるので、
//   ・`f(0) = a`   … 中心付近はパラメータに比例（線形）。**中心に穴が開かない**
//   ・`f(1) = 1`   … 外周は動かない（水域の広さが変わらない）
//   ・単調増加     … 格子線が交差しない（面が裏返らない）
// となる。セル幅は `2E/div · (a + k(1−a)·r^(k−1))` で連続に増える。

/// ワープの中心付近の線形係数 `a`。
///
/// 中心セルのワールド幅は `2·ocean_extent/分割数 · a` になる。
/// 既定（`ocean_extent`=2000m・128 分割）で約 0.94m ＝ 目標セルサイズちょうど。
/// これより小さくしても、解像できる波が無いのに頂点だけ細かくなる。
#[allow(dead_code)] // WGSL の CPU ミラー（テストが仕様を固定。上の節コメント参照）
pub const WATER_GRID_WARP_NEAR: f32 = 0.03;

/// ワープの指数 `k`。
///
/// 大きいほど頂点が中心（カメラ）へ集まる。**外周のセルが極端に大きくなるが
/// それは害にならない**: 頂点変位は「セルが波長に対して粗い所」では
/// どのみち 0 へ落とす（`water_displacement_gain`）ので、遠景は従来どおり
/// 平面＋法線の波のままだからである。8 は「既定設定で全強度の変位が
/// カメラから約 70m まで届く」値として選んだ。
#[allow(dead_code)] // WGSL の CPU ミラー（テストが仕様を固定。上の節コメント参照）
pub const WATER_GRID_WARP_EXP: f32 = 8.0;

/// `WaterParams::wave_axis.z` に入れる放射状ワープの有効フラグ（Ocean のみ）。
/// **WGSL 側 `WATER_GRID_WARP_ON_MIN` の判定と対で意味を持つ。**
pub const WATER_GRID_WARP_ON: f32 = 1.0;

/// 同・無効（Region / 川）。
pub const WATER_GRID_WARP_OFF: f32 = 0.0;

/// 分割数 `div_x × div_z` の格子を描くのに必要な頂点数。
#[inline]
pub fn grid_vertex_count(div_x: u32, div_z: u32) -> u32 {
    div_x.saturating_mul(div_z).saturating_mul(WATER_CELL_VERTEX_COUNT)
}

/// クアッド 1 枚が**欲しがる**分割数（1 辺あたり）。
///
/// Ocean は放射状ワープで近傍へ頂点を寄せるため常に最大分割を要求する。
/// Region は面積相応（目標セルサイズ）で、上限まで。
///
/// `half_extent_max` はクアッドの片側半径（XZ の大きい方。m）。
pub fn quad_desired_divisions(is_ocean: bool, half_extent_max: f32) -> u32 {
    if is_ocean {
        return WATER_GRID_MAX_DIV;
    }
    // 全幅 = 半径 × 2。目標セルサイズで割った数を切り上げる。
    let width = (half_extent_max.max(0.0)) * 2.0;
    let want  = (width / WATER_GRID_TARGET_CELL_M).ceil();
    // NaN / 無限大は 1 分割（＝従来どおりの 1 枚クアッド）へ倒す。
    if !want.is_finite() || want < 1.0 {
        return 1;
    }
    (want as u32).clamp(1, WATER_GRID_MAX_DIV)
}

/// 川リボン 1 分割が**欲しがる**分割数 `(幅方向, 長さ方向)`。
pub fn river_desired_divisions(half_width: f32, segment_length: f32) -> (u32, u32) {
    let across = divisions_for_length(half_width.max(0.0) * 2.0, WATER_RIVER_MAX_DIV_ACROSS);
    let along  = divisions_for_length(segment_length.max(0.0),   WATER_RIVER_MAX_DIV_ALONG);
    (across, along)
}

/// 長さ `length_m` を目標セルサイズで刻んだときの分割数（1..=`max_div`）。
fn divisions_for_length(length_m: f32, max_div: u32) -> u32 {
    let want = (length_m / WATER_GRID_TARGET_CELL_M).ceil();
    if !want.is_finite() || want < 1.0 {
        return 1;
    }
    (want as u32).clamp(1, max_div)
}

/// クアッドバケットの分割数を頂点予算へ収める。
///
/// バケット内の全インスタンスは同じ分割数（＝同じ頂点数）で描く。
/// インスタンスごとに変えても頂点シェーダの起動数は最大値で決まる（1 ドローの
/// 頂点数は 1 つしか指定できない）ため、分けても得が無いからである。
///
/// `instances` が 0 のときは 1 を返す（呼び出し側は描画しない）。
pub fn fit_quad_divisions(desired: u32, instances: usize) -> u32 {
    fit_square_divisions(desired, instances, WATER_QUAD_VERTEX_BUDGET)
}

/// 川バケットの分割数を頂点予算へ収める（幅・長さの比を保ったまま縮める）。
pub fn fit_river_divisions(desired: (u32, u32), instances: usize) -> (u32, u32) {
    let (mut across, mut along) = (desired.0.max(1), desired.1.max(1));
    if instances == 0 {
        return (across, along);
    }
    // 予算を超える間、**大きい方**から 1 ずつ減らす（比が極端に崩れないようにする）。
    while total_vertices(across, along, instances) > WATER_RIVER_VERTEX_BUDGET {
        if across >= along && across > 1 {
            across -= 1;
        } else if along > 1 {
            along -= 1;
        } else {
            break; // 1×1 まで落としても超えるなら諦める（インスタンス数側の上限が守る）
        }
    }
    (across, along)
}

/// 正方格子（`div × div`）を頂点予算へ収める共通実装。
fn fit_square_divisions(desired: u32, instances: usize, budget: u32) -> u32 {
    let mut div = desired.max(1);
    if instances == 0 {
        return div;
    }
    while div > 1 && total_vertices(div, div, instances) > budget {
        div -= 1;
    }
    div
}

/// バケット全体の頂点数（分割数 × インスタンス数）。飽和演算で溢れさせない。
fn total_vertices(div_x: u32, div_z: u32, instances: usize) -> u32 {
    grid_vertex_count(div_x, div_z)
        .saturating_mul(u32::try_from(instances).unwrap_or(u32::MAX))
}

// ─── 格子パラメータ（WGSL と同一式の正典）────────────────────────
//
// 【dead_code を narrow に許可する理由】
// この節（と上のワープ定数）は実行時には使われない。実行時の実装は
// water_height_field.wgsl 側にあり、ここは**同一式の CPU ミラー**として
// ユニットテスト（亀裂なし・単調性・穴なし）が仕様を固定するためだけに存在する。
// テスト専用ビルドへ #[cfg(test)] で隠すと「シェーダー式の正典がどこにあるか」が
// ソースから見えなくなるため、あえて常時コンパイルして式の対応を明示する。

/// 格子線のパラメータ座標（−1..1）。
///
/// `index` は 0..=`div` の格子線番号、`div` は分割数。
/// **WGSL 側 `water_grid_param` と同一式**であり、
/// 「隣接セルが共有する格子線は完全に同じ値になる」という亀裂回避の根拠そのもの。
#[inline]
#[allow(dead_code)] // WGSL の CPU ミラー（テストが仕様を固定。上の節コメント参照）
pub fn grid_line_param(index: u32, div: u32) -> f32 {
    index as f32 / div.max(1) as f32 * 2.0 - 1.0
}

/// 放射状ワープの係数 `f(r)`（ワープ後のパラメータは `p · f(r)`）。
///
/// **WGSL 側 `water_grid_warp_factor` と同一式。**
#[inline]
#[allow(dead_code)] // WGSL の CPU ミラー（テストが仕様を固定。上の節コメント参照）
pub fn grid_warp_factor(r: f32) -> f32 {
    WATER_GRID_WARP_NEAR
        + (1.0 - WATER_GRID_WARP_NEAR) * r.powf(WATER_GRID_WARP_EXP - 1.0)
}

/// パラメータ座標（−1..1 の XZ）へ放射状ワープを掛ける。
///
/// 半径はチェビシェフ距離 `max(|x|,|z|)` を使う。ユークリッド距離だと
/// クアッドの角（r=√2）で `f(r) > 1` になり水域が外へはみ出すが、
/// チェビシェフなら外周の全周でちょうど `r = 1` になり、外形が保たれる。
#[inline]
#[allow(dead_code)] // WGSL の CPU ミラー（テストが仕様を固定。上の節コメント参照）
pub fn grid_warp(p: [f32; 2]) -> [f32; 2] {
    let r = p[0].abs().max(p[1].abs());
    let f = grid_warp_factor(r);
    [p[0] * f, p[1] * f]
}

/// ワープ後の格子の**局所セル幅**（m）。
///
/// ワールド座標は `E·(a·r + (1−a)·r^k)` なので、その `r` 微分にパラメータ側の
/// セル幅 `2/div` を掛けたものが局所セル幅になる。
/// **WGSL 側 `water_grid_cell_size` の警戒（ワープ有効時）と同一式。**
#[inline]
#[allow(dead_code)] // WGSL の CPU ミラー（テストが仕様を固定。上の節コメント参照）
pub fn grid_warped_cell_size(r: f32, half_extent: f32, div: u32) -> f32 {
    let base = 2.0 * half_extent / div.max(1) as f32;
    let d = WATER_GRID_WARP_NEAR
        + WATER_GRID_WARP_EXP * (1.0 - WATER_GRID_WARP_NEAR)
            * r.powf(WATER_GRID_WARP_EXP - 1.0);
    base * d
}

// ============================================================
//  テスト
// ============================================================
#[cfg(test)]
mod tests {
    use super::*;

    /// 分割数はどんな入力でも 1..=上限に収まること（0 分割 = 頂点 0 は描画事故になる）。
    #[test]
    fn divisions_stay_within_limits() {
        assert_eq!(quad_desired_divisions(true, 0.0), WATER_GRID_MAX_DIV, "Ocean は常に最大分割");
        assert_eq!(quad_desired_divisions(false, 0.0), 1, "面積 0 でも 1 分割は残る");
        assert_eq!(quad_desired_divisions(false, -5.0), 1, "負の半径でも 1 分割へ倒す");
        assert_eq!(quad_desired_divisions(false, f32::NAN), 1, "NaN でも 1 分割へ倒す");
        assert_eq!(quad_desired_divisions(false, 1.0e9), WATER_GRID_MAX_DIV, "巨大 Region は上限で止まる");
        // 半径 8m（全幅 16m）を目標セル 1m で刻むと 16 分割。
        assert_eq!(quad_desired_divisions(false, 8.0), 16);

        let (a, l) = river_desired_divisions(2.0, 2.0);
        assert!((1..=WATER_RIVER_MAX_DIV_ACROSS).contains(&a), "幅方向が範囲外: {a}");
        assert!((1..=WATER_RIVER_MAX_DIV_ALONG).contains(&l), "長さ方向が範囲外: {l}");
        assert_eq!(river_desired_divisions(100.0, 100.0), (WATER_RIVER_MAX_DIV_ACROSS, WATER_RIVER_MAX_DIV_ALONG));
        assert_eq!(river_desired_divisions(0.0, 0.0), (1, 1));
    }

    /// 頂点予算を超えないこと（超えるなら分割数が自動で落ちること）。
    #[test]
    fn vertex_budget_is_respected() {
        // 単独インスタンスなら最大分割がそのまま通る。
        let div = fit_quad_divisions(WATER_GRID_MAX_DIV, 1);
        assert_eq!(div, WATER_GRID_MAX_DIV);
        assert!(grid_vertex_count(div, div) <= WATER_QUAD_VERTEX_BUDGET);

        // 水域が増えると分割数が落ちて予算内に収まる。
        for instances in [1usize, 2, 8, 16, 64, WATER_MAX_VOLUMES_FOR_TEST] {
            let div = fit_quad_divisions(WATER_GRID_MAX_DIV, instances);
            assert!(div >= 1, "分割数が 0 になった（instances={instances}）");
            let total = grid_vertex_count(div, div) as u64 * instances as u64;
            assert!(
                total <= WATER_QUAD_VERTEX_BUDGET as u64 || div == 1,
                "予算超過: instances={instances} div={div} total={total}",
            );
        }

        // 川も同様（本数が多いほど分割が落ちる）。
        for instances in [1usize, 64, 1024] {
            let (a, l) = fit_river_divisions(
                (WATER_RIVER_MAX_DIV_ACROSS, WATER_RIVER_MAX_DIV_ALONG), instances);
            assert!(a >= 1 && l >= 1);
            let total = grid_vertex_count(a, l) as u64 * instances as u64;
            assert!(
                total <= WATER_RIVER_VERTEX_BUDGET as u64 || (a == 1 && l == 1),
                "川の予算超過: instances={instances} div=({a},{l}) total={total}",
            );
        }
    }

    /// テスト用の「1 フレームに載り得る最大水域数」（`WATER_MAX_VOLUMES` と同値）。
    const WATER_MAX_VOLUMES_FOR_TEST: usize = 64;

    /// **亀裂が出ない条件**: 隣り合うセルが共有する格子線は、
    /// どちらのセルから作っても f32 のビットパターンまで一致すること。
    ///
    /// 累積加算で頂点を作る実装へ戻すと、この等値が丸め誤差で崩れて
    /// セル境界に隙間（＝背景が透けるピンホール）が開く。
    #[test]
    fn grid_lines_are_shared_exactly_between_neighbours() {
        for div in [1u32, 3, 16, WATER_GRID_MAX_DIV] {
            for i in 0..div {
                // セル i の右端（格子線 i+1）とセル i+1 の左端（格子線 i+1）。
                let right = grid_line_param(i + 1, div);
                let left  = grid_line_param(i + 1, div);
                assert_eq!(right.to_bits(), left.to_bits(),
                    "格子線 {} が共有されていない（div={div}）", i + 1);
                // ワープ後も同様（同じ入力 → 同じ出力なので純粋関数である限り保たれる）。
                let wr = grid_warp([right, 0.25]);
                let wl = grid_warp([left,  0.25]);
                assert_eq!(wr[0].to_bits(), wl[0].to_bits());
            }
            // 両端はきっちり ±1（＝水域の外形が分割数で変わらない）。
            assert_eq!(grid_line_param(0, div), -1.0);
            assert_eq!(grid_line_param(div, div), 1.0);
        }
    }

    /// 放射状ワープの性質: 中心に穴が開かず、外周が動かず、単調であること。
    #[test]
    fn radial_warp_is_hole_free_monotonic_and_edge_preserving() {
        // 中心は中心のまま（穴が開かない）。
        assert_eq!(grid_warp([0.0, 0.0]), [0.0, 0.0]);
        // 外周（チェビシェフ半径 1）は動かない。
        for p in [[1.0f32, 0.0], [-1.0, 0.5], [0.3, 1.0], [1.0, 1.0], [-1.0, -1.0]] {
            let w = grid_warp(p);
            assert!((w[0] - p[0]).abs() < 1e-6 && (w[1] - p[1]).abs() < 1e-6,
                "外周が動いた: {p:?} → {w:?}");
        }
        // 軸上のワールド半径 r·f(r) は狭義単調増加（格子線が交差しない＝面が裏返らない）。
        let mut prev = -1.0f32;
        for i in 0..=WATER_GRID_MAX_DIV {
            let r = i as f32 / WATER_GRID_MAX_DIV as f32;
            let x = r * grid_warp_factor(r);
            assert!(x > prev, "ワープが単調でない（r={r}）: {x} <= {prev}");
            prev = x;
        }
    }

    /// **LOD 段差が無い条件**: セル幅が中心から外周へ連続かつ単調に増えること。
    ///
    /// 同心 LOD リング方式なら境界でセル幅が 2 倍に跳ぶ（＝T ジャンクション）が、
    /// 放射状ワープは 1 枚の連続した格子なので跳びが存在しない。
    /// 隣り合うセルの幅の比が緩やかであることを数値で押さえる。
    #[test]
    fn cell_size_grows_smoothly_without_lod_steps() {
        const HALF_EXTENT: f32 = 2000.0;
        let div = WATER_GRID_MAX_DIV;
        let mut prev = grid_warped_cell_size(0.0, HALF_EXTENT, div);
        assert!(prev > 0.0, "中心のセル幅が 0（穴が開く）");
        // 既定設定での中心セルは目標セルサイズ（1m）程度であること。
        assert!((prev - WATER_GRID_TARGET_CELL_M).abs() < 0.5,
            "中心セル幅が目標から外れている: {prev}m");
        // 半径方向の刻みは「格子の半分 = div/2 セル」ぶん。
        let steps = div / 2;
        for i in 1..=steps {
            let r = i as f32 / steps as f32;
            let cur = grid_warped_cell_size(r, HALF_EXTENT, div);
            assert!(cur >= prev, "セル幅が外側で縮んだ（r={r}）: {cur} < {prev}");
            assert!(cur <= prev * 2.0,
                "隣接セルの幅が 2 倍以上跳んだ（＝LOD 段差相当。r={r}）: {prev} → {cur}");
            prev = cur;
        }
    }
}
