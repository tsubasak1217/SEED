// ============================================================
//  prefab_live_patch/base_cache.rs — 「元の版」の控え（3 方向の当て直しの材料）
//
//  当て直しを 3 方向（ファイルが変えたところだけ当てる）にするには、インスタンスが作られたときの
//  ファイルの中身（元の版）が要る。保存された後ではディスクに残っていないので、Play の間だけ
//  （仮想パス, 内容ハッシュ）をキーにメモリへ控えておく。インスタンスの `prefab_hash` が
//  「どの版から作られたか」を表すので、そのハッシュで引けばそのインスタンスの元の版が分かる。
//
//  【控える時機】（いずれも Play の間だけ。Play の開始・停止で空にする）
//   - Play の開始（play_mode_ops.rs の enter_play）: シーンのインスタンスが参照するファイルを 1 本 1 回読み、
//     インスタンスの版と一致したら生テキストのまま控える（読むのは開始時の 1 回だけ。解析は使うときまで遅らせる）
//   - スクリプトの Instantiate（script_scene_ops.rs）: 組み立てたデータを控える（版ごとに最初の 1 回だけ）
//   - 当て直し（ops.rs）: 新しい版のデータを控える（次の当て直しの元の版になる）
//   - 書き戻し（write_back.rs）: 上書きする前のファイルを控える
// ============================================================

use std::collections::HashMap;

use crate::engine::structs::objects::actor::ActorData;

/// 控えの 1 件（読んだままの生テキストか、解析済みのデータ）。
enum BaseEntry {
    /// 生テキスト（Play の開始で読んだもの。使うときに解析する）。
    Raw(String),
    /// 解析済みのデータ。
    Parsed(ActorData),
    /// 解析に失敗した（2 度目は試さない）。
    Broken,
}

/// 元の版の控え（Play の間だけ使う）。
#[derive(Default)]
pub(crate) struct PrefabBaseCache {
    /// （仮想パス, 内容ハッシュ）→ 控え。
    entries: HashMap<(String, String), BaseEntry>,
}

impl PrefabBaseCache {
    /// 全部捨てる（Play の開始・停止で呼ぶ）。
    pub(crate) fn clear(&mut self) {
        self.entries.clear();
    }

    /// 控えの件数（テスト用）。
    #[cfg(test)]
    pub(crate) fn len(&self) -> usize {
        self.entries.len()
    }

    /// その版が控えてあるか。
    pub(crate) fn contains(&self, vpath: &str, hash: &str) -> bool {
        self.entries.contains_key(&(vpath.to_string(), hash.to_string()))
    }

    /// 生テキストを控える（既に控えてあれば何もしない）。
    pub(crate) fn remember_raw(&mut self, vpath: &str, hash: &str, raw: String) {
        self.entries.entry((vpath.to_string(), hash.to_string())).or_insert(BaseEntry::Raw(raw));
    }

    /// 解析済みのデータを控える（既に解析済みなら何もしない。生テキストの控えは置き換える）。
    pub(crate) fn remember_data(&mut self, vpath: &str, hash: &str, data: &ActorData) {
        let entry = self.entries.entry((vpath.to_string(), hash.to_string())).or_insert(BaseEntry::Broken);
        if !matches!(entry, BaseEntry::Parsed(_)) {
            *entry = BaseEntry::Parsed(data.clone());
        }
    }

    /// 版の中身を使える状態にする（生テキストならここで解析して置き換える）。使えるなら true。
    ///
    /// 当て直しの間は `peek`（共有参照）で引きたいので、解析（可変参照が要る）はここで先に済ませる。
    pub(crate) fn resolve(&mut self, vpath: &str, hash: &str) -> bool {
        let Some(entry) = self.entries.get_mut(&(vpath.to_string(), hash.to_string())) else { return false };
        if let BaseEntry::Raw(raw) = entry {
            // 読み込みの入口は actor_file（版の変換を含む唯一の経路）
            *entry = match crate::engine::core::app_base::actor_file::parse(raw) {
                Ok(data) => BaseEntry::Parsed(data),
                Err(e) => {
                    eprintln!("[PrefabLivePatch] 元の版を解析できません（2 方向で当て直します）: {vpath} {e}");
                    BaseEntry::Broken
                }
            };
        }
        matches!(entry, BaseEntry::Parsed(_))
    }

    /// 解析済みの版の中身を引く（`resolve` を先に呼ぶこと。生テキストのままなら None）。
    pub(crate) fn peek(&self, vpath: &str, hash: &str) -> Option<&ActorData> {
        match self.entries.get(&(vpath.to_string(), hash.to_string()))? {
            BaseEntry::Parsed(data) => Some(data),
            BaseEntry::Raw(_) | BaseEntry::Broken => None,
        }
    }

    /// 版の中身を引く（`resolve` → `peek`）。無い・読めない版は None。
    #[cfg(test)]
    pub(crate) fn get(&mut self, vpath: &str, hash: &str) -> Option<&ActorData> {
        if !self.resolve(vpath, hash) {
            return None;
        }
        self.peek(vpath, hash)
    }
}

// ============================================================
//  テスト
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;

    /// 最小の .actor の生テキスト（版の刻印なし＝旧版として読まれる）。
    const RAW: &str = r#"{"name":"Card","components":[],"children":[]}"#;

    /// 生テキストで控えた版は、引いたときに解析される。違う版・違うパスは引けない。
    #[test]
    fn raw_entry_is_parsed_on_demand() {
        let mut cache = PrefabBaseCache::default();
        cache.remember_raw("assets://a.actor", "h1", RAW.to_string());
        assert!(cache.contains("assets://a.actor", "h1"));
        assert_eq!(cache.get("assets://a.actor", "h1").map(|d| d.name.as_str()), Some("Card"));
        assert!(cache.get("assets://a.actor", "h2").is_none(), "違う版は引けない");
        assert!(cache.get("assets://b.actor", "h1").is_none(), "違うパスは引けない");
    }

    /// 解析できない生テキストは None（2 方向へ落ちる）。
    #[test]
    fn broken_raw_entry_yields_none() {
        let mut cache = PrefabBaseCache::default();
        cache.remember_raw("assets://a.actor", "h1", "{not json".to_string());
        assert!(cache.get("assets://a.actor", "h1").is_none());
    }

    /// 解析済みのデータで控え直せる。clear で空になる。
    #[test]
    fn parsed_entry_and_clear() {
        let mut cache = PrefabBaseCache::default();
        let data: ActorData = serde_json::from_str(RAW).unwrap();
        cache.remember_data("assets://a.actor", "h1", &data);
        assert_eq!(cache.len(), 1);
        assert!(cache.get("assets://a.actor", "h1").is_some());
        cache.clear();
        assert_eq!(cache.len(), 0);
    }
}
