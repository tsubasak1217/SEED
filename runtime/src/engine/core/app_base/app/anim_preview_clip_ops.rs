// ============================================================
//  anim_preview_clip_ops.rs — 未保存クリップのライブプレビュー（ANIM_PREVIEW_CLIP）
//
//  【役割】
//  エディタのアニメーションタイムラインが「編集中（未保存）のクリップ本文」を
//  そのまま送り込み、プレビューキャッシュ（anim_preview_cache）の該当エントリを
//  差し替えるための処理。
//
//  【なぜ必要か】
//  従来の ANIM_PREVIEW はクリップをディスクの .anim からロードしてキャッシュし、
//  ANIM_RELOAD（保存直後に送信）でしか捨てられなかった。そのため
//  「キーを動かす → 見た目が変わらない → 保存して初めて反映される」という
//  編集不能に近い体験になっていた。本コマンドはキャッシュを直接差し替えるので、
//  保存せずに ANIM_PREVIEW を撃ち直すだけで結果が見える。
//
//  【責務の境界】
//  ・ここはキャッシュの差し替えだけを行う。実際の適用（値の書き込み）は
//    従来どおり handle_anim_preview（animation_ops.rs）が行う。
//  ・ディスクは一切触らない。保存は引き続きエディタ側の明示操作。
//  ・ANIM_RELOAD は従来どおりエントリを破棄するため、保存後は .anim が再ロードされる。
// ============================================================

use std::sync::Arc;

use crate::engine::animation::AnimationClip;

use super::{App, RuntimeMode};

impl App {
    /// エディタが送ってきた未保存クリップ本文でプレビューキャッシュを差し替える
    /// （ANIM_PREVIEW_CLIP IPC）。
    ///
    /// - Edit モード専用（Play 中はランタイムのアニメーションが正であり、
    ///   編集中の本文を割り込ませない）。
    /// - JSON のパースに失敗した場合は**既存のキャッシュを残したまま**警告のみ出す。
    ///   編集途中の壊れた本文でプレビューを消してしまわないため。
    ///
    /// # 引数
    /// * `clip_path` - キャッシュキー。ANIM_PREVIEW で使うのと同じ仮想パス。
    /// * `json`      - .anim と同じ形式の JSON 本文（IPC 側で Base64 復号済み）。
    pub(super) fn handle_anim_preview_clip(&mut self, clip_path: &str, json: &str) {
        // Edit モード専用（ANIM_PREVIEW と同じ制約）
        if self.mode != RuntimeMode::Edit {
            eprintln!("[SEED anim] ANIM_PREVIEW_CLIP は Edit モード専用（無視）");
            return;
        }

        match AnimationClip::from_json(clip_path, json) {
            Ok(clip) => {
                self.anim_preview_cache
                    .insert(clip_path.to_string(), Arc::new(clip));
            }
            Err(err) => {
                // 既存キャッシュは残す（編集途中の不正 JSON でプレビューを壊さない）
                eprintln!("[SEED anim] ANIM_PREVIEW_CLIP: JSON 解析失敗 {clip_path}: {err}");
            }
        }
    }
}
