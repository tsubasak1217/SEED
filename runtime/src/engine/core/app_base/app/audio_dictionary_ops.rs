// ============================================================
//  audio_dictionary_ops.rs — 音声辞書（AudioDictionaryComponent）の
//                            キー索引構築とインスペクタ編集 IPC
//
//  【責務】
//    1. シーン内の全 AudioDictionary を畳んだ「キー → (パス, 既定音量)」
//       索引を作り直し、スクリプト側（host_api）へ公開する。
//    2. インスペクタからの辞書一括更新（SET_AUDIO_DICT）を適用する。
//    3. AudioComponent の「音源 2 択」（ファイルパス直接 / 辞書のキー）を
//       実際の (パス, 音量) へ解決する唯一の入口を提供する。
//
//  【索引を持つ理由】
//    再生のたびにアクタツリーを DFS すると O(アクタ数) になる。
//    辞書は「シーンロード・コンポーネント変更」でしか変わらないので、
//    そのタイミングだけで作り直して保持するほうが構造的に正しい。
//
//  【再構築のタイミング（dirty フラグ方式）】
//    変更点（シーンロード / 辞書編集 / コンポーネント追加・削除・複製 /
//    Undo 復元）で `mark_audio_dictionary_dirty()` を呼び、
//    索引を使う直前（毎フレームの `ensure_audio_dictionary_index()`）で
//    dirty なら作り直す。こうしておくと「変更点を 1 つ取りこぼしても
//    次の変更で必ず追いつく」ため、静かに壊れたままにならない。
//
//  【ECS 理念】
//    索引の中身を作るのは純関数 `core::audio::dictionary_index::build_index`。
//    本ファイルは「シーン（World）から辞書を集めて渡す」「結果を配る」
//    という App 側の配管だけを担当する。
// ============================================================

use crate::engine::components::{
    AudioComponent, AudioDictionaryComponent, AudioDictionaryComponentData, ComponentKind,
};
use crate::engine::core::audio::dictionary_index::{
    build_index, AudioDictResolved, AudioDictionaryIndex,
};
use crate::engine::ecs::World;
use crate::engine::structs::objects::Actor;

use super::{find_actor_by_dfs, App};

impl App {
    // ── 索引の再構築 ─────────────────────────────────────────

    /// 音声辞書のキー索引に「再構築が必要」の印を付ける。
    ///
    /// シーンのロード・辞書の編集・コンポーネントの追加／削除／複製・Undo 復元など、
    /// 「シーン内の AudioDictionary の内容が変わり得る」すべての箇所から呼ぶ。
    /// 実際の再構築は次に索引を使うとき（`ensure_audio_dictionary_index`）に行う。
    pub(super) fn mark_audio_dictionary_dirty(&mut self) {
        self.audio_dict_dirty = true;
    }

    /// 索引が dirty なら作り直し、スクリプト側へ公開する。
    ///
    /// フレームの先頭（スクリプトフェーズより前）で呼ぶこと。
    /// スクリプトの `SEED.Audio.PlayDict` はこの公開済み索引だけを見る。
    pub(super) fn ensure_audio_dictionary_index(&mut self) {
        if !self.audio_dict_dirty {
            return;
        }
        self.audio_dict_dirty = false;

        // ── シーン内（アクティブ世界線）の辞書を DFS 順に集めて索引を作る ──
        // DFS 順 = 先勝ちの優先順。同じ順序をエディタのヒエラルキー表示と
        // 揃えることで「上にあるアクタの辞書が勝つ」と説明できる。
        //
        // ブロックで囲んでいるのは借用のため。`build_index` は文字列を複製して
        // 索引に詰めるので、結果はシーンを借りていない。ブロックを抜けた時点で
        // `&self.scene` の借用が終わり、`&mut self` への書き戻しができる
        //（コンポーネントを clone せずに参照のまま扱える）。
        let built = {
            let mut dicts: Vec<&AudioDictionaryComponent> = Vec::new();
            if let Some(scene) = &self.scene {
                collect_audio_dictionaries(
                    &scene.actors,
                    &scene.world,
                    self.active_world_line,
                    &mut dicts,
                );
            }
            build_index(dicts)
        };

        // ── 重複キーの警告（索引を作り直すたびに 1 度だけ）──
        // 「どちらが鳴るか分からない」状態を黙って通すと、
        // 素材差し替え時に原因不明の不具合になるため必ず知らせる。
        if !built.duplicate_keys.is_empty() && !self.audio_dict_warned {
            self.audio_dict_warned = true;
            eprintln!(
                "[SEED audio] 音声辞書のキーが重複しています（先に見つかった辞書が優先されます）: {}",
                built.duplicate_keys.join(", ")
            );
        }
        if built.duplicate_keys.is_empty() {
            // 重複が解消されたら、次に重複したときまた警告できるようにする
            self.audio_dict_warned = false;
        }

        self.audio_dict_index = built.index;
        // スクリプト（SEED.Audio.PlayDict / PlayBgmDict）が引けるよう公開する
        crate::engine::core::scripting::host_api::publish_audio_dictionary_index(
            self.audio_dict_index.clone(),
        );
    }

    /// 現在のキー索引への参照を返す（診断・テスト用）。
    pub(super) fn audio_dictionary_index(&self) -> &AudioDictionaryIndex {
        &self.audio_dict_index
    }

    // ── AudioComponent の音源解決（2 択の唯一の入口）──────────

    /// AudioComponent の音源を実際の (パス, 音量) へ解決する。
    ///
    /// `dictionary_key` が空でなければ辞書を引き、空なら `audio_path` を使う。
    /// 解決できない場合は None（呼び出し側は鳴らさない）。
    ///
    /// 辞書モードでは **音量も辞書の既定値** を使う。
    /// 「辞書側を直せば全参照に一括で反映される」ことが本機能の目的であり、
    /// コンポーネント側の音量が勝つとその目的が崩れるためである
    ///（インスペクタも辞書モードでは音量行を出さない）。
    pub(super) fn resolve_audio_component_source(
        &self,
        comp: &AudioComponent,
    ) -> Option<(String, f32)> {
        if comp.dictionary_key.is_empty() {
            // ── ファイルパス直接モード（従来）──
            if comp.audio_path.is_empty() {
                return None;
            }
            return Some((comp.audio_path.clone(), comp.volume));
        }

        // ── 辞書キーモード ──
        match self.audio_dict_index.get(&comp.dictionary_key) {
            Some(AudioDictResolved { path, volume }) => Some((path.clone(), *volume)),
            None => None,
        }
    }

    // ── インスペクタからの辞書一括更新（SET_AUDIO_DICT IPC）──

    /// インスペクタの音声辞書編集 UI からのグループ一括更新。
    ///
    /// `json` は AudioDictionaryComponentData と serde 互換（`{"groups":[...]}`）。
    /// 部分更新ではなく毎回まるごと置換する方式にしているのは、
    /// グループ追加・行削除・並べ替えを 1 つのコマンドで表現でき、
    /// エディタ側の状態とランタイムの状態が必ず一致するためである
    ///（Animator の SET_ANIMATOR_CLIPS と同じ流儀）。
    pub(super) fn handle_set_audio_dict(&mut self, actor_dfs_id: u32, slot_idx: u32, json: &str) {
        // JSON を AudioDictionaryComponentData にデコードする（失敗時は無視）
        let data: AudioDictionaryComponentData = match serde_json::from_str(json) {
            Ok(d) => d,
            Err(err) => {
                eprintln!("[SEED audio] SET_AUDIO_DICT: JSON パース失敗: {err}");
                return;
            }
        };

        let wl = self.active_world_line;
        // 対象スロットのエンティティを解決する（handle_set_audio_field と同じ流儀）
        let slot_entity = {
            let Some(scene) = &self.scene else { return };
            let mut c = 0u32;
            find_actor_by_dfs(&scene.actors, wl, actor_dfs_id, &mut c)
                .and_then(|a| a.slots().get(slot_idx as usize))
                .filter(|s| s.kind == ComponentKind::AudioDictionary)
                .map(|s| s.entity)
        };
        let Some(entity) = slot_entity else { return };
        let Some(scene) = &mut self.scene else { return };
        if let Some(d) = scene.world.get_mut::<AudioDictionaryComponent>(entity) {
            *d = AudioDictionaryComponent::from_data(data);
        }

        // 辞書が変わったので索引を作り直す（次フレームの先頭で反映される）
        self.mark_audio_dictionary_dirty();

        self.send_actor_components(actor_dfs_id, self.actor_virtual_selected_slot_idx);
        if let Some(ipc) = &self.ipc {
            ipc.send("SCENE_MODIFIED");
        }
    }
}

// ─── 収集ヘルパー ────────────────────────────────────────────

/// Actor ツリーを DFS で走査し、アクティブ世界線の AudioDictionary を参照で集める。
///
/// 走査順がそのまま「先勝ち」の優先順になる。
fn collect_audio_dictionaries<'w>(
    actors: &[Actor],
    world: &'w World,
    world_line: u32,
    out: &mut Vec<&'w AudioDictionaryComponent>,
) {
    for actor in actors {
        // 別世界線（アクター編集タブ・キャンバス編集タブ）の辞書は
        // シーンの再生には関係しないので除外する。
        if actor.world_line != world_line {
            continue;
        }
        for slot in actor.slots() {
            if slot.kind == ComponentKind::AudioDictionary {
                if let Some(d) = world.get::<AudioDictionaryComponent>(slot.entity) {
                    out.push(d);
                }
            }
        }
        collect_audio_dictionaries(actor.children(), world, world_line, out);
    }
}
