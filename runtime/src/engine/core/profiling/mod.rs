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

// ─── 一発計測（PROFILE_DUMP）─────────────────────────────────────────
//
// エディタのプロファイラパネルは 0.5 秒窓を延々と受け取り続けるが、
// 「いま N 秒ぶんを測って 1 個の結果として返せ」という要求（MCP の `seed_profile`）は
// それとは独立に成立させたい。そこで通常の集計器とは別に、窓長 N 秒の集計器を
// もう 1 本だけ動かし、満了時に JSON を確定させて取り出せるようにする。
//
// パネルが閉じていて計測が無効なときは、ダンプの間だけ強制的に有効化し、
// 満了時に元へ戻す（勝手に計測が残り続けないようにするため）。

/// 進行中のダンプ窓。
struct DumpState {
    /// ダンプ専用の集計器（窓長は要求秒数）。
    agg: ProfilerAggregator,
    /// ダンプのために計測を強制有効化したか（満了時に無効へ戻すか）。
    forced_enable: bool,
}

/// 進行中のダンプ窓（無ければ `None`）。
static DUMP: Mutex<Option<DumpState>> = Mutex::new(None);

/// 満了して取り出し待ちのダンプ結果 JSON。
static FINISHED_DUMP: Mutex<Option<String>> = Mutex::new(None);

/// ダンプ窓を開始する（既に進行中なら開始し直す）。
///
/// `secs` は計測する実時間（秒）。この間に流れたフレームを 1 窓へ畳み込む。
pub fn begin_dump(secs: f64) {
    // 計測が無効（＝パネルが閉じている）ならダンプの間だけ有効化する。
    let forced_enable = !scope::is_enabled();
    if forced_enable {
        scope::set_enabled(true);
    }
    let mut guard = lock_dump();
    *guard = Some(DumpState {
        agg: ProfilerAggregator::with_window_secs(secs),
        forced_enable,
    });
}

/// 満了済みのダンプ結果を取り出す（無ければ `None`）。取り出すと消える。
pub fn take_finished_dump() -> Option<String> {
    let mut guard = match FINISHED_DUMP.lock() {
        Ok(g) => g,
        Err(poisoned) => poisoned.into_inner(),
    };
    guard.take()
}

/// ダンプ窓のロック取得（毒された場合も中身をそのまま使う）。
fn lock_dump() -> std::sync::MutexGuard<'static, Option<DumpState>> {
    match DUMP.lock() {
        Ok(g) => g,
        Err(poisoned) => poisoned.into_inner(),
    }
}

/// 1 フレーム分の計測ツリーをダンプ窓へ畳み込み、満了していれば結果を確定させる。
fn accumulate_dump_frame(frame: &scope::FrameTree) {
    let mut guard = lock_dump();
    let Some(dump) = guard.as_mut() else { return };
    dump.agg.accumulate_frame(frame);
    if !dump.agg.window_elapsed() {
        return;
    }
    // 満了: JSON を確定し、強制有効化していたなら計測を元へ戻す。
    let json          = report::build_report_json(&dump.agg);
    let forced_enable = dump.forced_enable;
    *guard = None;
    drop(guard);
    if forced_enable {
        scope::set_enabled(false);
    }
    let mut finished = match FINISHED_DUMP.lock() {
        Ok(g) => g,
        Err(poisoned) => poisoned.into_inner(),
    };
    *finished = Some(json);
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

    // 一発計測（PROFILE_DUMP）の窓はパネル購読とは独立に回す。
    // パネルが閉じていて AGGREGATOR が None のときでもダンプは成立させたいので、
    // 通常集計より先に処理する。
    accumulate_dump_frame(&frame);

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

// ─── ダンプ結果のファイル書き出し ────────────────────────────────────
//
// IPC は「1 行 = 1 メッセージ」なので、数十 KB になりうるダンプ JSON を
// そのまま載せると行が極端に長くなる（読み手のバッファ・ログも汚す）。
// 一時ディレクトリへ書き出し、IPC ではパスだけを返す。
// エディタとランタイムは同一マシン上の同一ユーザーで動くため、パス受け渡しで足りる。

/// ダンプファイル名の接頭辞（他の一時ファイルと混ざらないようにするため）。
const DUMP_FILE_PREFIX: &str = "seed_profile_dump_";

/// スコープツリーの JSON（1 行）と、統合バッチ更新ゲートの理由集計をまとめて
/// 一時ファイルへ書き出し、そのフルパスを返す。
///
/// 出力スキーマ:
/// ```text
/// { "profile": <report.rs のレポート JSON>, "merge": <merge_stats の集計 JSON> }
/// ```
pub fn write_dump_file(
    profile_json: &str,
    merge_json:   &serde_json::Value,
) -> Result<String, String> {
    // レポートは既に文字列化済みなので、値へ戻してから包む（二重エスケープを避ける）。
    let profile_value: serde_json::Value = serde_json::from_str(profile_json)
        .map_err(|e| format!("プロファイル JSON の解釈に失敗: {e}"))?;
    let combined = serde_json::json!({
        "profile": profile_value,
        "merge":   merge_json,
    });

    // 同一秒に複数回ダンプしても衝突しないよう、ナノ秒までをファイル名に含める。
    let stamp = std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .map(|d| d.as_nanos())
        .unwrap_or(0);
    let path = std::env::temp_dir().join(format!("{DUMP_FILE_PREFIX}{stamp}.json"));
    std::fs::write(&path, combined.to_string())
        .map_err(|e| format!("ダンプの書き出しに失敗 ({}): {e}", path.display()))?;
    Ok(path.to_string_lossy().into_owned())
}

#[cfg(test)]
mod tests {
    /// ダンプファイルが `{profile, merge}` の形で書き出され、読み戻せること。
    #[test]
    fn writes_combined_dump_file() {
        let profile = r#"{"frames":3,"root":{"name":"Frame"}}"#;
        let merge   = serde_json::json!({ "frames": 3, "batches": [] });
        let path    = super::write_dump_file(profile, &merge).expect("書き出せること");
        let text    = std::fs::read_to_string(&path).expect("読み戻せること");
        let v: serde_json::Value = serde_json::from_str(&text).expect("有効な JSON");
        assert_eq!(v["profile"]["frames"], 3);
        assert_eq!(v["merge"]["frames"], 3);
        let _ = std::fs::remove_file(&path);
    }
}
