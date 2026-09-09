// ============================================================
//  plugin/registry.rs — プラグインレジストリ（DLL ローダー）
//
//  【設計】
//  起動時にプラグインフォルダを走査し、有効なプラグインの DLL を
//  libloading でロードして Plugin トレイトオブジェクトとして保持する。
//
//  DLL が Drop されるとプラグインのコードが解放されるため、
//  LoadedPlugin が Library を所有し続けることで生存期間を保証する。
// ============================================================

use std::path::{Path, PathBuf};

use super::manifest::{PluginEntry, PluginManifest};
use super::{PLUGIN_ENTRY_FN, Plugin, PluginCreateFn, PluginHost};

// ============================================================
//  LoadedPlugin — ロード済みプラグインの保持単位
// ============================================================

/// ロードに成功した 1 プラグインの状態。
///
/// _lib を保持することで DLL の生存期間を延命する。
/// Drop 時に plugin を先に解放し、その後 _lib（DLL のアンロード）が行われる。
pub struct LoadedPlugin {
    /// プラグイン実装（DLL 内のオブジェクト）
    pub plugin: Box<dyn Plugin>,
    /// plugin.json の内容（editor_menus など、トレイトに現れないデータの供給源）
    pub manifest: PluginManifest,
    /// フィールド定義キャッシュ（初回 field_defs() 呼び出し時に生成）
    field_defs_cache: Option<Vec<super::PluginFieldDef>>,
    /// DLL ライブラリハンドル（生存期間を plugin より長くする必要がある）
    _lib: libloading::Library,
}

impl LoadedPlugin {
    /// フィールド定義を返す（初回のみ生成してキャッシュする）。
    pub fn field_defs(&mut self) -> &[super::PluginFieldDef] {
        if self.field_defs_cache.is_none() {
            self.field_defs_cache = Some(self.plugin.field_defs());
        }
        self.field_defs_cache.as_deref().unwrap()
    }

    /// エディタメニュー定義（plugin.json の editor_menus）を返す。
    /// 空配列＝メニューを提供しないプラグイン。
    pub fn editor_menus(&self) -> &[super::EditorMenuDef] {
        &self.manifest.editor_menus
    }

    /// フィールド定義を不変参照で返す（キャッシュがなければ空スライスを返す）。
    /// send_actor_components の &self 文脈で使用する。
    /// キャッシュ更新が必要な場合は先に field_defs(&mut self) を呼ぶこと。
    pub fn field_defs_cached(&self) -> &[super::PluginFieldDef] {
        self.field_defs_cache.as_deref().unwrap_or(&[])
    }
}

// ============================================================
//  PluginRegistry — プラグインの管理・検索
// ============================================================

/// ロード済みプラグインを管理するレジストリ。
///
/// 起動時に load_from_dir() でプラグインフォルダを走査し、
/// 有効化されたプラグインだけロードする。
pub struct PluginRegistry {
    /// ロード済みプラグインのリスト（インデックス順は登録順）
    plugins: Vec<LoadedPlugin>,
    /// プラグインフォルダのパス（エディタ送信用）
    pub plugins_dir: PathBuf,
}

impl PluginRegistry {
    /// 空のレジストリを生成する。
    pub fn empty() -> Self {
        Self {
            plugins: Vec::new(),
            plugins_dir: PathBuf::new(),
        }
    }

    /// プラグインフォルダを走査し、有効なプラグインをロードしてレジストリを生成する。
    ///
    /// - plugins_dir:  プラグインが入っているフォルダ（各サブフォルダが 1 プラグイン）
    /// - enabled_list: project_settings.json の plugins リスト（有効/無効フラグ付き）
    pub fn load_from_dir(plugins_dir: &Path, enabled_list: &[PluginEntry]) -> Self {
        let mut registry = Self {
            plugins: Vec::new(),
            plugins_dir: plugins_dir.to_path_buf(),
        };

        // プラグインフォルダが存在しない場合はスキップ
        if !plugins_dir.exists() {
            return registry;
        }

        let entries = match std::fs::read_dir(plugins_dir) {
            Ok(e) => e,
            Err(e) => {
                eprintln!("[PluginRegistry] フォルダ読み込みエラー: {e}");
                return registry;
            }
        };

        for entry in entries.flatten() {
            let dir = entry.path();
            if !dir.is_dir() {
                continue;
            }

            // plugin.json を読む
            let manifest = match PluginManifest::load_from_dir(&dir) {
                Some(m) => m,
                None => {
                    eprintln!(
                        "[PluginRegistry] plugin.json が見つかりません: {}",
                        dir.display()
                    );
                    continue;
                }
            };

            // 有効化リストで enabled = false のものはスキップ
            let is_enabled = enabled_list
                .iter()
                .find(|e| e.name == manifest.name)
                .map(|e| e.enabled)
                .unwrap_or(true); // リストに存在しない場合はデフォルト有効

            if !is_enabled {
                eprintln!("[PluginRegistry] 無効化されています: {}", manifest.name);
                continue;
            }

            let dll_path = manifest.dll_path(&dir);
            let plugin_name = manifest.name.clone();
            match registry.load_dll(&dll_path, manifest) {
                Ok(()) => {
                    eprintln!("[PluginRegistry] ロード成功: {plugin_name}");
                }
                Err(e) => {
                    eprintln!("[PluginRegistry] ロード失敗 ({plugin_name}): {e}");
                }
            }
        }

        registry
    }

    /// DLL をロードしてレジストリに登録する。
    fn load_dll(
        &mut self,
        dll_path: &Path,
        manifest: PluginManifest,
    ) -> Result<(), Box<dyn std::error::Error>> {
        // Safety: DLL のエクスポート関数を呼び出す。
        // Plugin トレイトの実装が engine と同一ツールチェーンでコンパイルされている前提。
        let lib = unsafe { libloading::Library::new(dll_path)? };

        let create_fn: libloading::Symbol<PluginCreateFn> = unsafe { lib.get(PLUGIN_ENTRY_FN)? };

        // seed_create_plugin() → *mut Box<dyn Plugin>
        let raw = unsafe { create_fn() };
        if raw.is_null() {
            return Err("seed_create_plugin() が null を返しました".into());
        }

        let plugin: Box<dyn Plugin> = unsafe { *Box::from_raw(raw as *mut Box<dyn Plugin>) };

        // プラグイン名の整合性チェック（警告のみ）
        if plugin.name() != manifest.name {
            eprintln!(
                "[PluginRegistry] 警告: manifest の name ({}) と Plugin::name() ({}) が一致しません",
                manifest.name,
                plugin.name()
            );
        }

        self.plugins.push(LoadedPlugin {
            plugin,
            manifest,
            field_defs_cache: None,
            _lib: lib,
        });

        Ok(())
    }

    // ── 検索 API ───────────────────────────────────────────────

    /// 指定名のロード済みプラグインへの不変参照を返す。
    pub fn get(&self, name: &str) -> Option<&LoadedPlugin> {
        self.plugins.iter().find(|p| p.plugin.name() == name)
    }

    /// 指定名のロード済みプラグインへの可変参照を返す。
    pub fn get_mut(&mut self, name: &str) -> Option<&mut LoadedPlugin> {
        self.plugins.iter_mut().find(|p| p.plugin.name() == name)
    }

    /// 全ロード済みプラグインをイテレートする。
    pub fn iter(&self) -> impl Iterator<Item = &LoadedPlugin> {
        self.plugins.iter()
    }

    /// 全ロード済みプラグインを可変でイテレートする。
    pub fn iter_mut(&mut self) -> impl Iterator<Item = &mut LoadedPlugin> {
        self.plugins.iter_mut()
    }

    /// ロード済みプラグイン数を返す。
    pub fn len(&self) -> usize {
        self.plugins.len()
    }

    /// プラグインがロードされているかを返す。
    pub fn is_empty(&self) -> bool {
        self.plugins.is_empty()
    }

    /// ロード済みプラグインの名前一覧を返す。
    pub fn names(&self) -> Vec<&str> {
        self.plugins.iter().map(|p| p.plugin.name()).collect()
    }

    /// エディタ送信用のプラグイン情報 JSON を生成する。
    ///
    /// フォーマット:
    /// [{"name":"PhysicsPlugin","version":"0.1.0","description":"...",
    ///   "editor_menus":[{"menu":"Game","items":[...]}]},...]
    ///
    /// `editor_menus` は plugin.json の宣言をそのまま載せる
    /// （エディタはこれだけを見てメニューを構築する＝データドリブン）。
    pub fn to_json(&self) -> String {
        let entries: Vec<String> = self
            .plugins
            .iter()
            .map(|p| {
                let name = p.plugin.name().replace('"', "\\\"");
                let version = p.plugin.version().replace('"', "\\\"");
                let desc = p.plugin.description().replace('"', "\\\"");
                // メニュー定義はラベル・確認文にカンマや引用符・日本語が入るため、
                // 手組みせず serde_json に任せる（エスケープ漏れを構造的に防ぐ）。
                let menus = serde_json::to_string(&p.manifest.editor_menus)
                    .unwrap_or_else(|_| EMPTY_JSON_ARRAY.to_string());
                format!(
                    r#"{{"name":"{name}","version":"{version}","description":"{desc}","editor_menus":{menus}}}"#
                )
            })
            .collect();
        format!("[{}]", entries.join(","))
    }

    // ── エディタメニューアクションの実行 ─────────────────────────

    /// エディタのメニュー項目から要求されたアクションを、該当プラグインへ委譲する。
    ///
    /// - `plugin_name`: `plugin.json` の name（＝ PLUGIN_LIST で送った名前）
    /// - `action_id`:   `editor_menus[].items[].id`
    /// - `host`:        プラグインへ貸し出すホスト API
    ///
    /// 【マニフェストとの突き合わせを行う理由】
    /// IPC で来た任意の id をそのままプラグインへ渡すと、宣言していない
    /// アクションまで実行できてしまう。plugin.json に宣言された項目だけを
    /// 通すことで、UI に出ていない操作が外から叩かれるのを防ぐ。
    pub fn invoke_editor_action(
        &mut self,
        plugin_name: &str,
        action_id: &str,
        host: &mut dyn PluginHost,
    ) -> Result<(), String> {
        let Some(lp) = self.get_mut(plugin_name) else {
            return Err(format!("プラグインが見つかりません: {plugin_name}"));
        };

        // 宣言済みのアクションかを確認する（区切り線・id 空は対象外）
        let declared = lp
            .manifest
            .editor_menus
            .iter()
            .flat_map(|m| m.items.iter())
            .any(|it| it.is_actionable() && it.id == action_id);

        if !declared {
            return Err(format!(
                "plugin.json の editor_menus に宣言されていないアクションです: {action_id}"
            ));
        }

        lp.plugin.on_editor_action(action_id, host)
    }
}

/// serde_json のシリアライズに失敗した場合の代替値（空配列）。
/// 実際には失敗しないが、`unwrap` でプロセスを落とさないための定数。
const EMPTY_JSON_ARRAY: &str = "[]";
