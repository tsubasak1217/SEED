// ============================================================
//  lod_settings.rs — モデル LOD の段数と切替距離（シーン設定で可変）
//
//  》含む処理「
//  - NUM_LODS / LOD_DISTANCE_COUNT: LOD 段数と切替境界の本数（正典）
//  - DEFAULT_LOD_DISTANCES:         既定の切替距離（旧ハードコード値と同一）
//  - sanitize_lod_distances():      入力距離列の検証（昇順・下限）— 純関数
//  - set_lod_distances() / lod_distances(): プロセス全体の現在値（アトミック保持）
//  - lod_bucket_for_dist_sq():      距離²→LOD バケット番号（LOD 選択の唯一の判定点）
//
//  【なぜプロセスグローバルなのか】
//  LOD 選択は `InstancedModelBatch::update()`（毎フレーム・全バッチ）と
//  `lod_buckets_unchanged()`（ダーティゲートの再判定）の 2 か所が **必ず同じ式** で
//  行わなければならない（ずれると「更新をスキップしたのに本来は LOD が変わっていた」＝
//  見た目の変化になる）。距離を引数で配り回すと経路が増えるほど取りこぼしが起きるため、
//  判定関数 1 本＋その入力 1 か所に集約する。1 プロセス = 1 シーン（ランタイムは
//  シーンを 1 つだけ開く）なので、シーン設定の値をここへ流し込めば足りる。
//
//  値は f32 のビットパターンを AtomicU32 で保持する（ロック不要・毎フレーム read）。
// ============================================================

use std::sync::atomic::{AtomicU32, Ordering};

/// LOD レベル数（0 = フル解像度、1〜3 = 簡略化済み）。
///
/// この値がモデル LOD の段数の正典であり、インデックスバッファ・ノードバッファ・
/// スキン資源などの配列長がすべてこれに従う。
pub const NUM_LODS: usize = 4;

/// LOD 切替境界の本数（段数 - 1）。`[LOD0→LOD1, LOD1→LOD2, LOD2→LOD3]`。
pub const LOD_DISTANCE_COUNT: usize = NUM_LODS - 1;

/// 既定の LOD 切替距離（ワールド単位）。**旧実装のハードコード値と完全に一致**させてあり、
/// `lod` 節を持たない旧 `.scene` は従来とビット単位で同じ LOD 振り分けになる。
pub const DEFAULT_LOD_DISTANCES: [f32; LOD_DISTANCE_COUNT] = [
    10.0,  // 10 ユニット以内: LOD0（フル）
    30.0,  // 30 ユニット以内: LOD1（50%）
    60.0,  // 60 ユニット以内: LOD2（25%）
           // 60 ユニット以遠: LOD3（10%）
];

/// 切替距離として許す最小値（ワールド単位）。
///
/// 0 や負値を許すと「カメラ位置と完全に一致した距離 0 のインスタンスだけ LOD0」といった
/// 実質無意味な設定になり、さらに昇順の重複で境界が潰れる。下限でガードする。
pub const LOD_DISTANCE_MIN: f32 = 0.01;

/// 切替距離として許す最大値（ワールド単位）。
/// 二乗しても f32 の精度が保てる範囲に収める（1e6² = 1e12 は f32 で表現可能）。
pub const LOD_DISTANCE_MAX: f32 = 1.0e6;

/// 現在の LOD 切替距離の **二乗値**（f32 のビットパターン）。
///
/// 初期値は `DEFAULT_LOD_DISTANCES` の二乗。`AtomicU32` の定数初期化は配列リテラルを
/// 書き下す必要があるため、`to_bits()` を `const` 文脈で使える f32::to_bits で埋める。
static LOD_DIST_SQ_BITS: [AtomicU32; LOD_DISTANCE_COUNT] = [
    AtomicU32::new((DEFAULT_LOD_DISTANCES[0] * DEFAULT_LOD_DISTANCES[0]).to_bits()),
    AtomicU32::new((DEFAULT_LOD_DISTANCES[1] * DEFAULT_LOD_DISTANCES[1]).to_bits()),
    AtomicU32::new((DEFAULT_LOD_DISTANCES[2] * DEFAULT_LOD_DISTANCES[2]).to_bits()),
];

/// 入力の切替距離列を「必ず使える形」へ正規化する純関数（GPU 非依存・テスト可能）。
///
/// 正規化の内容:
///   1. NaN / 無限大は既定値へ置換する（壊れた `.scene` でランタイムを壊さない）
///   2. `LOD_DISTANCE_MIN`〜`LOD_DISTANCE_MAX` へクランプする
///   3. 昇順が崩れていれば **昇順へソートする**（拒否せず自動修復する。
///      拒否すると「一部だけ反映された中途半端な状態」が残りうるため）
///   4. 同値が並んだ場合はそのまま許す（その LOD 段が空になるだけで破綻はしない）
///
/// 戻り値は `(正規化後の距離列, 入力から変更されたか)`。第 2 要素が true のとき、
/// 呼び出し元（エディタ・IPC ハンドラ）は警告を出して利用者へ知らせる。
pub fn sanitize_lod_distances(
    input: [f32; LOD_DISTANCE_COUNT],
) -> ([f32; LOD_DISTANCE_COUNT], bool) {
    let mut out = [0.0f32; LOD_DISTANCE_COUNT];
    for i in 0..LOD_DISTANCE_COUNT {
        let v = input[i];
        out[i] = if v.is_finite() {
            v.clamp(LOD_DISTANCE_MIN, LOD_DISTANCE_MAX)
        } else {
            DEFAULT_LOD_DISTANCES[i]
        };
    }
    // 昇順ソート（NaN は上で除去済みなので partial_cmp は必ず Some）。
    out.sort_by(|a, b| a.partial_cmp(b).expect("NaN は除去済み"));

    // 「変更されたか」は正規化後との完全一致で判定する（NaN 入力は必ず不一致になる）。
    let changed = (0..LOD_DISTANCE_COUNT).any(|i| !(input[i] == out[i]));
    (out, changed)
}

/// プロセス全体の LOD 切替距離を差し替える（シーンロード時・IPC でのライブ変更時）。
///
/// 入力は `sanitize_lod_distances` で正規化してから格納する。
/// 戻り値は `(実際に格納された距離列, 入力が正規化で変更されたか)`。
pub fn set_lod_distances(
    distances: [f32; LOD_DISTANCE_COUNT],
) -> ([f32; LOD_DISTANCE_COUNT], bool) {
    let (sane, changed) = sanitize_lod_distances(distances);
    for i in 0..LOD_DISTANCE_COUNT {
        LOD_DIST_SQ_BITS[i].store((sane[i] * sane[i]).to_bits(), Ordering::Relaxed);
    }
    (sane, changed)
}

/// 現在の LOD 切替距離（ワールド単位）を返す。
pub fn lod_distances() -> [f32; LOD_DISTANCE_COUNT] {
    let mut out = [0.0f32; LOD_DISTANCE_COUNT];
    for i in 0..LOD_DISTANCE_COUNT {
        out[i] = lod_dist_sq(i).sqrt();
    }
    out
}

/// 現在の LOD 切替距離を既定値へ戻す（テスト・シーン設定リセット用）。
pub fn reset_lod_distances_to_default() {
    set_lod_distances(DEFAULT_LOD_DISTANCES);
}

/// `i` 番目の切替境界の距離の二乗を読む。
#[inline]
fn lod_dist_sq(i: usize) -> f32 {
    f32::from_bits(LOD_DIST_SQ_BITS[i].load(Ordering::Relaxed))
}

/// カメラからの距離の二乗を距離 LOD バケット番号（0..NUM_LODS-1）へ写す。
///
/// **モデル LOD 選択の唯一の判定点**。`InstancedModelBatch::update()` の振り分けと
/// `lod_buckets_unchanged()` の再判定が必ずこの関数を通る（両者がずれると
/// 「スキップしたのに本来は LOD が変わっていた」＝見た目の変化になる）。
#[inline]
pub fn lod_bucket_for_dist_sq(dist_sq: f32) -> usize {
    for i in 0..LOD_DISTANCE_COUNT {
        if dist_sq < lod_dist_sq(i) {
            return i;
        }
    }
    LOD_DISTANCE_COUNT
}

/// 1 インスタンスの LOD バケットを決める純関数（距離 LOD ＋「LOD を適用しない」の合成）。
///
/// `disable_lod` が true のインスタンスはカメラ距離に関係なく常に `LOD_BUCKET_HIGHEST`
/// （＝LOD0・フル解像度）になる。`InstancedModelBatch` の振り分けとダーティゲートの
/// 再判定が **両方ともこの関数だけ** を通ることで、両者の判定がずれない。
#[inline]
pub fn lod_bucket_for_instance(disable_lod: bool, dist_sq: f32) -> usize {
    if disable_lod {
        return LOD_BUCKET_HIGHEST;
    }
    lod_bucket_for_dist_sq(dist_sq)
}

/// 「LOD を適用しない」インスタンスが常に割り当てられる LOD バケット（＝最高品質）。
pub const LOD_BUCKET_HIGHEST: usize = 0;

// ============================================================
//  テスト
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;
    use std::sync::Mutex;

    /// グローバル状態を触るテストを直列化する（他テストの並列実行と干渉させない）。
    static TEST_LOCK: Mutex<()> = Mutex::new(());

    /// 既定値ではバケット境界が旧ハードコード値（10 / 30 / 60）と一致すること。
    #[test]
    fn default_distances_match_legacy_buckets() {
        let _g = TEST_LOCK.lock().unwrap();
        reset_lod_distances_to_default();
        assert_eq!(lod_bucket_for_dist_sq(0.0), 0);
        assert_eq!(lod_bucket_for_dist_sq(9.9 * 9.9), 0);
        assert_eq!(lod_bucket_for_dist_sq(10.0 * 10.0), 1, "境界ちょうどは次の LOD");
        assert_eq!(lod_bucket_for_dist_sq(29.9 * 29.9), 1);
        assert_eq!(lod_bucket_for_dist_sq(30.0 * 30.0), 2);
        assert_eq!(lod_bucket_for_dist_sq(59.9 * 59.9), 2);
        assert_eq!(lod_bucket_for_dist_sq(60.0 * 60.0), 3);
        assert_eq!(lod_bucket_for_dist_sq(1.0e9), NUM_LODS - 1);
    }

    /// 設定した距離に LOD 選択が実際に従うこと（設定 → バケット境界の移動）。
    #[test]
    fn bucket_follows_configured_distances() {
        let _g = TEST_LOCK.lock().unwrap();
        let (sane, changed) = set_lod_distances([5.0, 8.0, 100.0]);
        assert!(!changed, "昇順・範囲内の入力は変更されない");
        assert_eq!(sane, [5.0, 8.0, 100.0]);
        assert_eq!(lod_distances(), [5.0, 8.0, 100.0]);

        assert_eq!(lod_bucket_for_dist_sq(4.9 * 4.9), 0);
        assert_eq!(lod_bucket_for_dist_sq(5.1 * 5.1), 1);
        assert_eq!(lod_bucket_for_dist_sq(9.0 * 9.0), 2);
        assert_eq!(lod_bucket_for_dist_sq(150.0 * 150.0), 3);

        reset_lod_distances_to_default();
    }

    /// 昇順が崩れた入力は自動ソートされ、「変更あり」が報告されること。
    #[test]
    fn descending_input_is_sorted_and_reported() {
        let (sane, changed) = sanitize_lod_distances([60.0, 10.0, 30.0]);
        assert_eq!(sane, [10.0, 30.0, 60.0]);
        assert!(changed, "並べ替えが起きたら警告のため changed=true");
    }

    /// 範囲外・非有限値は下限/上限/既定へ落ちること（壊れた .scene への耐性）。
    #[test]
    fn invalid_values_are_repaired() {
        let (sane, changed) = sanitize_lod_distances([-5.0, f32::NAN, f32::INFINITY]);
        assert!(changed);
        // -5.0 → 下限、NaN → 既定[1]=30、INFINITY → 既定[2]=60。ソート後は昇順。
        assert_eq!(sane, [LOD_DISTANCE_MIN, DEFAULT_LOD_DISTANCES[1], DEFAULT_LOD_DISTANCES[2]]);
        assert!(sane.windows(2).all(|w| w[0] <= w[1]), "出力は必ず昇順");
    }

    /// disable_lod=true のインスタンスは、どんな距離でも常に LOD0 になること。
    #[test]
    fn disable_lod_always_selects_lod0() {
        let _g = TEST_LOCK.lock().unwrap();
        reset_lod_distances_to_default();
        for dist in [0.0f32, 15.0, 45.0, 1000.0, 1.0e5] {
            let dist_sq = dist * dist;
            assert_eq!(
                lod_bucket_for_instance(true, dist_sq), LOD_BUCKET_HIGHEST,
                "距離 {dist} でも LOD0 のまま",
            );
        }
        // 対照: 同じ距離でもフラグ OFF なら距離 LOD に従う。
        assert_eq!(lod_bucket_for_instance(false, 45.0 * 45.0), 2);
        assert_eq!(lod_bucket_for_instance(false, 1000.0 * 1000.0), NUM_LODS - 1);
    }

    /// 切替距離を変えても disable_lod=true は LOD0 のまま（設定に左右されない）こと。
    #[test]
    fn disable_lod_ignores_configured_distances() {
        let _g = TEST_LOCK.lock().unwrap();
        set_lod_distances([0.5, 1.0, 2.0]);
        assert_eq!(lod_bucket_for_instance(true, 100.0 * 100.0), LOD_BUCKET_HIGHEST);
        assert_eq!(lod_bucket_for_instance(false, 100.0 * 100.0), NUM_LODS - 1);
        reset_lod_distances_to_default();
    }

    /// 同値が並んでも破綻せず、その段が空になるだけであること。
    #[test]
    fn equal_distances_collapse_one_bucket() {
        let _g = TEST_LOCK.lock().unwrap();
        set_lod_distances([10.0, 10.0, 20.0]);
        // 距離 10〜20 は LOD2（LOD1 の帯が潰れる）。
        assert_eq!(lod_bucket_for_dist_sq(5.0 * 5.0), 0);
        assert_eq!(lod_bucket_for_dist_sq(15.0 * 15.0), 2, "LOD1 の帯は空");
        reset_lod_distances_to_default();
    }
}
