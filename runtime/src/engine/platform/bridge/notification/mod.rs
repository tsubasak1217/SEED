// ============================================================
//  platform/bridge/notification/mod.rs — 通知（SEED.Platform の Notifications。W1-5）のエンジン側の共通部品
//
//  【置くもの】
//    request … notification.ensure_channel / show / cancel の引数の読み取りと検査（Java の :seed_platform の
//              NotificationRequestReader と同じ規則。デスクトップの模擬が使う。Android では Java が同じ検査をする）
//  名前・欄・上限は wire::notification（Java の PlatformContract・C# の NotificationJson と一致させる）。
//  デスクトップの模擬の状態と命令は desktop_sim/（notification_state・notification_commands）。全体像は docs/android.md §25.13。
// ============================================================

/// 通知の命令の引数の読み取りと検査。
pub mod request;

pub use request::{
    is_known_category, read_channel, read_id, read_show, ChannelRequest, NotificationActionRequest, NotificationRequest,
};
