// ============================================================
//  system.rs — アニメーション評価システムのコアロジック（純関数）
//
//  【役割】
//  AnimationSystem（App 統合は app/animation_ops.rs）が使う、状態を持たない
//  再利用可能なヘルパー群:
//    - normalize_time: loop_mode に応じた再生時刻の正規化と再生継続判定
//    - resolve_actor_path: Animator 保持アクタからの相対パスで子アクタを解決
//
//  実際の毎フレーム収集・World 書き込みは app/animation_ops.rs が担う
//  （アクター木は scene.actors、コンポーネント実体は World にあり、
//   その両者を跨ぐ処理は App 側でしか行えないため）。
// ============================================================

use crate::engine::structs::objects::Actor;
use super::clip::LoopMode;

/// 再生時刻を loop_mode に応じて [0, duration] 範囲のサンプル時刻へ正規化する。
///
/// 戻り値: (サンプルに使う時刻, 再生を継続すべきか)。
/// - Once: duration を超えたら末尾で停止（sample=duration, playing=false）。
/// - Loop: duration で剰余（常に playing=true）。
/// - PingPong: 2*duration 周期で往復（常に playing=true）。
///
/// duration が 0 以下のクリップは常に (0.0, false)。
pub fn normalize_time(loop_mode: LoopMode, time: f32, duration: f32) -> (f32, bool) {
    if duration <= 0.0 {
        return (0.0, false);
    }
    match loop_mode {
        LoopMode::Once => {
            if time >= duration {
                (duration, false)
            } else if time < 0.0 {
                (0.0, true)
            } else {
                (time, true)
            }
        }
        LoopMode::Loop => {
            // rem_euclid で負時刻も正しく巻き戻す
            (time.rem_euclid(duration), true)
        }
        LoopMode::PingPong => {
            let cycle = duration * 2.0;
            let t = time.rem_euclid(cycle);
            // 前半は順再生、後半は逆再生
            let sample = if t <= duration { t } else { cycle - t };
            (sample, true)
        }
    }
}

/// Animator 保持アクタからの相対パス（"/" 区切りの子アクタ名）で対象アクタを解決する。
///
/// 空文字は自分自身。各セグメントは子アクタ名で照合し、見つからなければ None。
pub fn resolve_actor_path<'a>(root: &'a Actor, path: &str) -> Option<&'a Actor> {
    if path.is_empty() {
        return Some(root);
    }
    let mut cur = root;
    for seg in path.split('/') {
        if seg.is_empty() { continue; }
        cur = cur.children().iter().find(|c| c.name == seg)?;
    }
    Some(cur)
}
