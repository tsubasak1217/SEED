// ============================================================
//  platform/bridge/alarm/mod.rs — 目覚まし（SEED.Platform の Alarms。W1-3）のエンジン側の共通部品
//
//  【置くもの】
//    request      … alarm.schedule / alarm.cancel の引数の読み取りと検査（Java の AlarmRequestReader と同じ規則。
//                   デスクトップの模擬が使う。Android では :seed_platform の Java が同じ検査をする）
//    sound_export … 音源の書き出し（スクリプトの "assets://…" の音を、Java だけの :seed_platform が読める実ファイルへ。
//                   Android の糊〈runtime/android/native/src/platform_bridge/alarm_prep.rs〉が alarm.schedule を送る前に使う）
//  名前・欄・上限は wire::alarm（Java の PlatformContract・C# の AlarmJson と一致させる）。
//  デスクトップの模擬の予約表は desktop_sim/（alarm_book・alarm_commands）。全体像は docs/android.md §25.11。
// ============================================================

/// 予約の引数の読み取りと検査。
pub mod request;
/// 音源の書き出し（assets:// → 端末保護ストレージの sounds/<内容のハッシュ>.<拡張子>）。
pub mod sound_export;

pub use request::{read_id, read_schedule, AlarmRequest};
