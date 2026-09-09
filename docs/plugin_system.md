# プラグインシステム

SEED のプラグインは `plugin_api`（crate: `seed-plugin-api`）の `Plugin` トレイトを実装した
Rust の cdylib（`.dll`）である。エンジン本体（runtime）と各プラグイン DLL が同じ
`seed-plugin-api` に依存することで vtable レイアウトの互換性を保つ。

新規プラグインの作り方は `.claude/skills/add-plugin/SKILL.md` を参照。
本書は **プラグインがエディタへ機能を露出する仕組み** を扱う。

---

## 1. 配置とマニフェスト

```
{assets_root}/../plugins/
  GameTools/
    game_tools.dll
    plugin.json
```

`plugin.json` の実スキーマは `runtime/src/engine/plugin/manifest.rs` の `PluginManifest`。

| キー | 必須 | 意味 |
|---|---|---|
| `name` | ○ | プラグイン識別名。フォルダ名・`Plugin::name()` と一致させる |
| `version` | ○ | バージョン文字列 |
| `description` | | 説明文（エディタ表示用） |
| `author` | | 作者名 |
| `entry_dll` | | DLL ファイル名（省略時 `"{name}.dll"`） |
| `editor_menus` | | エディタのメニューバーへ追加する項目（後述） |

`editor_menus` は `serde(default)` なので、**このキーを持たない既存の plugin.json は
そのまま読める**（空配列として扱われる）。後方互換は
`manifest.rs` の `mod tests` が検査している。

---

## 2. `editor_menus` — メニューをデータで宣言する

型定義は `runtime/src/engine/plugin/editor_menu.rs`。

```json
{
  "name": "GameTools",
  "version": "0.1.0",
  "entry_dll": "game_tools.dll",
  "editor_menus": [
    {
      "menu": "Game",
      "items": [
        {
          "id": "delete_save",
          "label": "ユーザーデータ削除",
          "confirm": "セーブデータ（save.json）を削除します。よろしいですか？"
        },
        { "separator": true },
        { "id": "dump_save", "label": "セーブ内容をログ出力" }
      ]
    }
  ]
}
```

### フィールド

| キー | 意味 |
|---|---|
| `menu` | トップレベルメニュー名。**同名は複数プラグイン間でマージされる** |
| `items[].id` | アクション識別子。`Plugin::on_editor_action` にそのまま渡る |
| `items[].label` | 表示ラベル。省略時は `id` が表示される |
| `items[].confirm` | 実行前に出す確認ダイアログ本文。空/省略なら確認なしで即実行 |
| `items[].separator` | `true` なら区切り線（`id` / `label` は無視される） |

### 配置ルール（エディタ側）

- エディタに **同名の静的メニューが既にある**（「表示」など）場合は、
  新規作成せずそのメニューの末尾へ項目を追加する。
- 同名が無ければ、静的メニューすべての**右隣**（メニューバーの末尾）へ新規追加する。
- プラグイン一覧（`PLUGIN_LIST`）を受け直すたびに、
  **前回生成した分だけ**を取り除いて作り直すので重複しない（静的項目には触れない）。
- ランタイム未接続時は項目をグレーアウトする。

---

## 3. `Plugin::on_editor_action` — 実行を受け取る

```rust
fn on_editor_action(&mut self, id: &str, host: &mut dyn PluginHost) -> Result<(), String>
```

- `id`: `editor_menus[].items[].id`
- 戻り値 `Ok(())` → エディタに成功トースト、`Err(reason)` → 理由付きのエラー表示。
- 既定実装は `Err("unknown action: {id}")` を返す
  （plugin.json に項目だけ足して実装を忘れた場合に気付けるようにするため）。

実装例（`plugins/game_tools/src/lib.rs`）:

```rust
const ACTION_DELETE_SAVE: &str = "delete_save";

fn on_editor_action(&mut self, id: &str, host: &mut dyn PluginHost) -> Result<(), String> {
    match id {
        ACTION_DELETE_SAVE => {
            host.delete_save_data()?;
            host.log("ユーザーデータ（セーブデータ）を削除しました。");
            Ok(())
        }
        other => Err(format!("未実装のアクションです: {other}")),
    }
}
```

`PluginRegistry::invoke_editor_action` は、**plugin.json に宣言されている
アクションだけ**を `on_editor_action` へ通す。UI に出ていない id を IPC で
直接叩かれても実行されない。

---

## 4. `PluginHost` — プラグインからエンジンを呼ぶ

プラグイン DLL はエンジン本体のクレートに依存できない（依存の肥大化と ABI 結合を避けるため）。
そのため「エンジンにやってほしいこと」は `PluginHost` トレイト越しに間接呼び出しする。

```rust
pub trait PluginHost {
    /// ランタイムのログへ 1 行出力する（エディタの Output に流れる）。
    fn log(&mut self, message: &str);

    /// セーブデータを全削除して即座に保存する。
    fn delete_save_data(&mut self) -> Result<(), String>;

    /// セーブデータの整数キーを 1 つ書き換えて即座に保存する（他キーは保持）。
    fn set_save_int(&mut self, key: &str, value: i64) -> Result<(), String>;
}
```

- 定義: `plugin_api/src/lib.rs`
- ランタイム側の実装: `runtime/src/engine/plugin/host.rs`（`RuntimePluginHost`）

### `delete_save_data` の意味

`save::delete_all()` で **メモリ上のストアを空にしてから** `save::save()` で書き出す。
ファイルを消すだけだと、プロセス内のストアに値が残っているため Play 停止時の
自動フラッシュ（`flush_if_dirty`）で復活してしまう。

### `set_save_int` の意味

`save::set_int()` でメモリ上のストアへ 1 キーだけ書き込み、`save::save()` で書き出す。
ストアは初回アクセス時に既存の `save.json` を読み込むため、**図鑑や所持金など
他のキーはそのまま残る**。ファイルが無い場合は空のストアから始まり、
書き出し時に親ディレクトリごと新規作成される。

真偽値の専用 API を用意していないのは、C# 側の `SEED.SaveData.SetBool(key, v)` が
`SetInt(key, v ? 1 : 0)` の別名であり（`scripting/src/Api/SaveData.cs`）、
読み出す `GetBool` も「0 以外を true」と判定するため。整数 1 本でゲーム側と
完全に同じ形式を再現できる。

「どのキーへ何を書くか」というゲーム固有の知識はホスト側には置かず、
プラグイン側の定数に閉じる（`plugins/game_tools/src/lib.rs` の
`SAVE_KEY_TUTORIAL_DONE` / `SAVE_VALUE_TRUE`）。

検証: `runtime/src/engine/plugin/host.rs` のユニットテスト
`set_save_int_writes_flag_and_keeps_other_keys`
（`SEED_SAVE_DIR` を一時フォルダへ向け、既存キーの保持と JSON の値を確認）。

### 拡張時の注意

`PluginHost` にメソッドを追加するときは、既定実装を与えるか、
**runtime と全プラグインを同時に再ビルド**すること（vtable レイアウトが変わるため）。

---

## 5. IPC

| 方向 | メッセージ | 意味 |
|---|---|---|
| Runtime → Editor | `PLUGIN_LIST:{json}` | ロード済みプラグイン一覧。各要素に `editor_menus` を含む |
| Editor → Runtime | `PLUGIN_ACTION:{plugin},{id}` | メニュー項目の実行要求 |
| Runtime → Editor | `PLUGIN_ACTION_OK:{plugin},{id}` | 成功 |
| Runtime → Editor | `PLUGIN_ACTION_ERROR:{plugin},{id},{reason}` | 失敗（`reason` はカンマを含んでよい） |

`PLUGIN_LIST` の 1 要素:

```json
{ "name": "GameTools", "version": "0.1.0", "description": "...",
  "editor_menus": [ { "menu": "Game", "items": [ ... ] } ] }
```

関連コード:

| 役割 | ファイル |
|---|---|
| コマンド定義・パース | `runtime/src/engine/core/app_base/ipc.rs`（`parse_plugin_action`） |
| ハンドラ | `runtime/src/engine/core/app_base/app/ipc_handler.rs`（`handle_plugin_action`） |
| 一覧 JSON 生成・アクション委譲 | `runtime/src/engine/plugin/registry.rs` |
| エディタ側の受信・パース | `editor/src/Runtime/RuntimeManager.cs` / `editor/src/Runtime/PluginMenuModel.cs` |
| エディタ側のメニュー生成 | `editor/src/MainWindow.PluginMenu.cs` |

---

## 6. ビルドと配置

ワークスペースルート `Cargo.toml` の `members` へクレートを追記したうえで:

```
cargo build -p game_tools
```

`build.rs` がビルド成果物を自動デプロイする:

- `runtime/plugins/GameTools/game_tools.dll`
- `runtime/plugins/GameTools/plugin.json`

**注意**: `build.rs` はリンク前に走るため、まっさらな状態からの初回ビルドでは
DLL がまだ存在せずコピーされない。もう一度 `cargo build -p <crate>` を実行すること。

有効化は `runtime/assets/project_settings.json` の `plugins` 配列:

```json
{ "name": "GameTools", "enabled": true }
```

（未記載でも既定は有効。明示登録を推奨）

---

## 7. 標準添付プラグイン

| 名前 | 用途 |
|---|---|
| `SamplePlugin` | 全フィールド種別のサンプル（`plugins/sample_plugin/`） |
| `GameTools` | 「Game」メニュー（`plugins/game_tools/`）。下表のアクションを持つ |

### `GameTools` のアクション一覧

| id | ラベル | 動作 |
|---|---|---|
| `delete_save` | ユーザーデータ削除 | セーブデータを全削除して即保存する |
| `complete_tutorial` | tutorial を完了済みにする | セーブキー `tutorial_done` に `1` を書いて即保存する（他キーは保持） |

`complete_tutorial` の書き込み先・キー名・形式は、ゲーム側の読み出しと同一である。
- キー定義: `runtime/assets/common/scripts/GameProgressKeys.cs` の `TutorialDone = "tutorial_done"`
- 読み出し: `runtime/assets/mainGame/scripts/Tutorial/TutorialDirector.cs`
  `SEED.SaveData.GetBool(GameProgressKeys.TutorialDone, false)`
- 保存先ファイル: `save.json`（場所は `runtime/src/engine/core/save/path.rs` の規約に従う。
  エディタ Play なら `runtime/save/save.json`）
