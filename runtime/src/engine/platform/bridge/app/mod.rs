// ============================================================
//  platform/bridge/app/mod.rs — アプリ（SEED.Platform の App。W1-6）のエンジン側の共通部品
//
//  【置くもの】app.open_url の URL の規則（url_rules.rs）。Android では URL の判定と ACTION_VIEW はメインプロセスの Java
//  （platform/app/UrlPolicy・UrlLauncher・local/OpenUrlCommand）が行い、デスクトップでは模擬（desktop_sim/app_commands.rs）が
//  ここを使って同じ判定をする。名前・欄・理由は wire::app（Java の PlatformContract・C# の AppJson と一致させる）。
//  全体像は docs/android.md §25.15。
// ============================================================

/// app.open_url の URL の規則。
mod url_rules;

pub use url_rules::{check_open_url, read_open_url, UrlRejection};
