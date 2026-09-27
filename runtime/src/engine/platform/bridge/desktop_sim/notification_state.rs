// ============================================================
//  platform/bridge/desktop_sim/notification_state.rs — 模擬の通知の状態（チャネルと出ている通知。プロセスの中だけ。W1-5）
//
//  Android の NotificationManager（チャネルと、tag = 通知の ID で出した通知）の代わり。持つのは
//    チャネル … ensure_channel で作る（同じ ID をもう一度作ると名前と説明だけ変わり、重要度は最初のまま。Android の
//               「アプリはチャネルを作った後に重要度を上げられない」に合わせた簡略）
//    通知     … show で出す（同じ ID は置き換え＝同じ場所に上書き）・cancel で消す
//  だけ。画面には何も出さない（[SEED PLATFORM] のログは notification_commands.rs）。エディタの Play の区切りで空にする。
// ============================================================

use std::sync::{Mutex, MutexGuard, PoisonError};

use crate::engine::platform::bridge::notification::{ChannelRequest, NotificationRequest};

/// 出ている通知 1 件（出した時刻つき）。
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct SimShownNotification {
    /// 検査済みの引数。
    pub request: NotificationRequest,
    /// 出した時刻（UTC の epoch ミリ秒。置き換えたときはその時刻）。
    pub shown_at_utc_ms: i64,
}

/// 状態の中身（Mutex 1 つで守る）。
#[derive(Debug, Default)]
struct BoardInner {
    /// チャネル（作った順）。
    channels: Vec<ChannelRequest>,
    /// 出ている通知（出した順。置き換えは同じ位置）。
    shown: Vec<SimShownNotification>,
}

/// 模擬の通知の状態。
#[derive(Debug, Default)]
pub struct SimNotificationBoard {
    /// チャネルと通知（持つのは出し入れの間だけ）。
    inner: Mutex<BoardInner>,
}

impl SimNotificationBoard {
    /// 空の状態。
    pub fn new() -> Self {
        Self::default()
    }

    /// ロックを取る（毒されていても中身は壊れない値だけなので使い続ける）。
    fn lock(&self) -> MutexGuard<'_, BoardInner> {
        self.inner.lock().unwrap_or_else(PoisonError::into_inner)
    }

    /// チャネルを作る（あれば名前と説明だけ変える）。
    ///
    /// # 戻り値
    /// 新しく作ったら true、既にあって更新したら false
    pub fn ensure_channel(&self, channel: ChannelRequest) -> bool {
        let mut inner = self.lock();
        if let Some(existing) = inner.channels.iter_mut().find(|existing| existing.channel_id == channel.channel_id) {
            existing.name = channel.name;
            existing.description = channel.description;
            return false;
        }
        inner.channels.push(channel);
        true
    }

    /// そのチャネルがあるか。
    pub fn has_channel(&self, channel_id: &str) -> bool {
        self.lock().channels.iter().any(|channel| channel.channel_id == channel_id)
    }

    /// 通知を出す（同じ ID は置き換え）。
    ///
    /// # 戻り値
    /// 置き換えたら true
    pub fn show(&self, request: NotificationRequest, now_utc_ms: i64) -> bool {
        let mut inner = self.lock();
        let shown = SimShownNotification { request, shown_at_utc_ms: now_utc_ms };
        if let Some(existing) = inner.shown.iter_mut().find(|existing| existing.request.id == shown.request.id) {
            *existing = shown;
            return true;
        }
        inner.shown.push(shown);
        false
    }

    /// 通知を消す（無い ID でも成功）。
    ///
    /// # 戻り値
    /// その ID の通知が出ていたら true
    pub fn cancel(&self, id: &str) -> bool {
        let mut inner = self.lock();
        let before = inner.shown.len();
        inner.shown.retain(|shown| shown.request.id != id);
        inner.shown.len() != before
    }

    /// 出ている通知の写し（出した順）。
    pub fn shown(&self) -> Vec<SimShownNotification> {
        self.lock().shown.clone()
    }

    /// Play の区切り: チャネルも通知も捨てる。
    pub fn clear(&self) {
        let mut inner = self.lock();
        inner.channels.clear();
        inner.shown.clear();
    }
}
