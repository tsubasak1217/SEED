// ============================================================
//  profiling — フレーム内のセクション別 CPU 時間プロファイラ
//
//  【目的】
//  「どの階層・どのセクションが重いのか」をエディタの専用パネルへ可視化するための
//  計測基盤。フレームループの各所へ `profile_scope!("名前")` を仕込むと、
//  スコープの生存期間（RAII）が計測され、呼び出しの入れ子がそのまま階層ツリーになる。
//
//  【構成】
//   - scope.rs     : RAII ガード `ScopeGuard` と `profile_scope!` マクロ、
//                    スレッドローカルのフレーム記録バッファ（親子スタック）。
//   - aggregate.rs : 1 フレーム分のツリーを「集計窓（既定 0.5 秒）」へ畳み込む
//                    永続集計ツリー（平均 / 最大 / 呼び出し回数）。
//   - report.rs    : 集計結果を JSON 文字列（IPC 送信用）へ直列化する。
//
//  【オーバーヘッド設計】
//   - 計測は既定で無効。エディタのプロファイラパネルが開いている（購読中）ときだけ
//     `set_enabled(true)` される。無効時の `profile_scope!` のコストは
//     「Relaxed な AtomicBool ロード 1 回 + 分岐」だけで、Instant も取得しない。
//   - 有効時のコストは 1 スコープあたり Instant 2 回 + 子ノードの線形探索 +
//     Vec push/pop 程度。ヒープ確保はフレーム間でバッファを使い回すため通常発生しない。
//   - 集計結果の送信は毎フレームではなく集計窓ごと（既定 0.5 秒に 1 回）。
//
//  【スレッド】
//   計測はメインスレッド（フレームループ）専用。フレーム記録はスレッドローカルで、
//   `frame_begin()` を呼んだスレッドだけが `active` になる。rayon ワーカースレッド等で
//   `profile_scope!` が実行されても、そのスレッドは非 active なので完全な no-op になり、
//   バッファが増え続けることはない。
// ============================================================

pub mod aggregate;
pub mod report;
pub mod scope;

pub use aggregate::ProfilerAggregator;
// `ScopeGuard` は `profile_scope!` マクロの展開先であり、フレームループのように
// ブロック境界と計測区間が一致しない場所では直接 `new` / `drop` して使う。
pub use scope::ScopeGuard;
// スクリプト（C#）からの手動計測 FFI（host_api::ffi_profiler）が使う。
pub use scope::{is_enabled, script_scope_begin, script_scope_end};

use std::sync::Mutex;

/// プロセス全体で 1 つの集計器。
///
/// 触るのはメインスレッド（フレームループ）だけなので実質無競合。
/// `Mutex` にしているのは `static` として安全に持つためであり、競合待ちは想定しない。
static AGGREGATOR: Mutex<Option<ProfilerAggregator>> = Mutex::new(None);

/// プロファイラの有効／無効を切り替える（エディタの購読状態に追従させる）。
///
/// 無効化時は集計器を破棄し、次に有効化されたときは新しい窓から集計を始める
/// （閉じている間の古いデータが混ざらないようにするため）。
pub fn set_profiling_enabled(enabled: bool) {
    scope::set_enabled(enabled);

    let mut guard = match AGGREGATOR.lock() {
        Ok(g) => g,
        Err(poisoned) => poisoned.into_inner(),
    };
    if enabled {
        if guard.is_none() {
            *guard = Some(ProfilerAggregator::new());
        }
    } else {
        *guard = None;
    }
}

// ─── スクリプト（C#）から渡される動的な名前のインターン ───────────────
//
// 計測ノードの名前は `&'static str` で持つ（記録時のヒープ確保と文字列比較コストを
// 抑えるため）。C# から渡る名前は動的文字列なので、ここで一度だけ leak して
// `&'static str` 化し、以降は同じ参照を使い回す。
// 無制限に leak しないよう上限を設け、超えた分は計測しない（`None` を返す）。

/// インターンできる名前の上限。これを超える種類の名前が来たら計測を諦める
/// （ループ内で `Profiler.Begin($"{i}")` のような使い方をされてもメモリが増え続けない）。
const MAX_INTERNED_NAMES: usize = 256;

/// インターン済みの名前表（文字列 → leak 済み `&'static str`）。
static INTERNED_NAMES: Mutex<Option<std::collections::HashMap<String, &'static str>>> =
    Mutex::new(None);

/// 動的な名前を `&'static str` へインターンする。上限超過時は `None`。
pub fn intern_name(name: &str) -> Option<&'static str> {
    let mut guard = match INTERNED_NAMES.lock() {
        Ok(g) => g,
        Err(poisoned) => poisoned.into_inner(),
    };
    let map = guard.get_or_insert_with(std::collections::HashMap::new);
    if let Some(&s) = map.get(name) {
        return Some(s);
    }
    if map.len() >= MAX_INTERNED_NAMES {
        return None;
    }
    let leaked: &'static str = Box::leak(name.to_string().into_boxed_str());
    map.insert(name.to_string(), leaked);
    Some(leaked)
}

/// フレーム冒頭で呼ぶ。計測が有効なら、このフレームのルートスコープを開始する。
pub fn begin_frame() {
    scope::frame_begin();
}

/// フレーム末尾で呼ぶ。
///
/// このフレームの計測ツリーを集計器へ畳み込み、集計窓が満了していれば
/// エディタへ送るための JSON 文字列を返す（満了していなければ `None`）。
pub fn end_frame() -> Option<String> {
    // 計測が無効なら何もしない（フレーム記録も空）。
    let frame = scope::frame_end_take()?;

    let mut guard = match AGGREGATOR.lock() {
        Ok(g) => g,
        Err(poisoned) => poisoned.into_inner(),
    };
    let agg = guard.as_mut()?;
    agg.accumulate_frame(&frame);
    if agg.window_elapsed() {
        let json = report::build_report_json(agg);
        agg.reset_window();
        Some(json)
    } else {
        None
    }
}
