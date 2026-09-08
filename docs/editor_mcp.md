# SEED エディタ MCP サーバー（seed-editor）

Claude Code / Gemini CLI などの外部エージェントから、**動作中の SEED エディタ**を
ツールとして操作するための MCP サーバー。
「変更 → 目視確認 → 再修正」のループを、人間がスクリーンショットを貼らなくても回せるようにする。

- MCP サーバー本体: `editor/SeedMcpServer/Program.cs`（stdio + JSON-RPC 2.0）
- エディタ側 HTTP ブリッジ: `editor/src/AI/SeedAIBridge.cs`（`http://localhost:7234/seed-ai/`）
- コマンド実装: `editor/src/AI/Tools/EditorCommandExecutor.cs`（シーン編集）と
  `editor/src/AI/Tools/EditorCommandExecutor.Visual.cs`（目視確認・再生制御・アニメ）
- エディタ本体への窓口: `editor/src/AI/Tools/IEditorAiHost.cs` ← 実装は `editor/src/MainWindow.AiHost.cs`
- 画面キャプチャ（screen 方式）: `editor/src/AI/Capture/WindowScreenCapture.cs`
- GPU 読み戻しキャプチャ（gpu 方式）: ランタイム `runtime/src/engine/core/renderer/screenshot.rs`
  ＋ `runtime/src/engine/core/app_base/app/screenshot_ops.rs`
- ヘッドレス起動: `editor/src/Headless/`（`EditorStartupOptions` / `HeadlessWindow` / `EditorDialogs`）
  ＋ MCP 側 `editor/SeedMcpServer/Launcher.cs`
- **安全機構**（2026-09 のデータ損失事故を受けて追加。詳細は 7 章・8 章）
  - 実行許可ポリシー: `editor/src/AI/AiOperationPolicy.cs`
  - インスタンス束縛: `editor/SeedMcpServer/SeedInstance.cs`
  - シーンロック: `editor/src/Scene/SceneLock.cs`
  - 原子的保存＋世代バックアップ: `runtime/src/engine/core/app_base/safe_write.rs` /
    `editor/src/Assets/SafeFileWriter.cs`
  - 保存先のパス整合性: `runtime/src/engine/core/app_base/app/scene_save_ops.rs`
  - 単体テスト: `editor/tests/AiSafetyTests/`（C#）、上記 Rust ファイル内の `mod tests`

---

## 1. 構成

```
Claude Code
   │  stdio / JSON-RPC 2.0
   ▼
SeedMcpServer.exe                     ← editor/SeedMcpServer
   │  HTTP  POST http://localhost:7234/seed-ai/cmd
   ▼
SeedAIBridge（エディタ内 HttpListener）
   │  WPF Dispatcher へマーシャル
   ▼
EditorCommandExecutor
   ├─ ランタイムへ IPC（名前付きパイプ）        … シーン編集・アニメプレビュー
   └─ IEditorAiHost（= MainWindow）             … 状態取得・再生制御・保存・キャプチャ
```

**ポートは固定ではない**。

| インスタンス | ポート | トークン | AI の変更操作 |
|---|---|---|---|
| 利用者が手で起動したエディタ | 7234（既定） | なし | **禁止**（読み取り専用。環境設定で明示的に許可したときだけ可） |
| `seed_launch` が起動したヘッドレス | 7300〜7399 の空き | 起動時に生成 | 許可 |

MCP サーバーは「自分が `seed_launch` で起動した（あるいは `seed_attach` で明示的に
繋いだ）1 インスタンス」だけを操作する。束縛が無い状態では変更系ツールをすべて拒否する。
詳細は 7 章。

---

## 2. ビルド

```bash
# MCP サーバーだけをビルドする（これだけで .mcp.json は動く）
dotnet build editor/SeedMcpServer/SeedMcpServer.csproj

# エディタ本体をビルドすると MCP サーバーも自動でビルドされ、
# SEEDEditor.exe と同じディレクトリへコピーされる（SEEDEditor.csproj の BuildAndCopySeedMcpServer ターゲット）
dotnet build editor/SEEDEditor.csproj
```

出力先:

| 成果物 | パス |
|---|---|
| MCP サーバー単体ビルド | `editor/SeedMcpServer/bin/Debug/net9.0/SeedMcpServer.exe` |
| エディタビルド時のコピー先 | `editor/bin/Debug/net9.0-windows/SeedMcpServer.exe` |

`.mcp.json` は前者を指している。`dotnet run` ではなく **ビルド済み exe を直接起動する**
（`dotnet run` は毎回ビルド判定が走り、MCP のハンドシェイクが遅くなるため）。

---

## 3. 接続方法

1. リポジトリルートの **`.mcp.json`** を Claude Code が読む（プロジェクトスコープ）。

   ```json
   {
     "mcpServers": {
       "seed-editor": {
         "type": "stdio",
         "command": "editor/SeedMcpServer/bin/Debug/net9.0/SeedMcpServer.exe",
         "args": [],
         "env": {}
       }
     }
   }
   ```

   相対パスはリポジトリルートを作業ディレクトリとして解決される。
   別ディレクトリから起動する場合は絶対パスに書き換える。

2. **操作対象のエディタを用意する**。
   - 基本は `seed_launch(headless:true, scene:"...")`。MCP が空きポートとトークンを決めて
     エディタを起動し、pid とトークンの一致を確認してから成功を返す。
     **エディタが起動していなくても他ツールが勝手に起動することはない**。
   - 利用者が開いているエディタを操作したい場合だけ `seed_attach(port, token)`
     （7 章「明示的な接続（seed_attach）」）。

   ブリッジの起動は `MainWindow` 初期化時の `AIAssistantPanel` コンストラクタから
   `SeedAIBridge.Start()` が呼ばれる。成功すると `editor/logs/SEEDEditor.log` に出る。

   ```
   [SeedAIBridge] 起動: http://localhost:7234/seed-ai/ (pid=1234, headless=False, token=なし, AI操作=読み取り専用)
   ```

   - **`--ai-port` 指定あり**（＝ `seed_launch` が起動したインスタンス）でポートを掴めなかった場合、
     エディタは終了コード 78 で**即座に終了する**。別インスタンスへ乗り移らせないための仕様。
   - **`--ai-port` 指定なし**（利用者が手で起動）でポート衝突した場合は
     `[SeedAIBridge] 起動失敗 ...` を出すだけで、エディタは通常どおり動き続ける。

3. Claude Code 側で `/mcp` を実行し `seed-editor` が connected になっていれば完了。
   エディタが落ちていても MCP サーバー自体は起動し、ツール一覧は返る
   （実行時に「SEED エディタへ接続できません」というエラーが返る）。

---

## 4. ツール一覧

| ツール | 引数 | 返り値 |
|---|---|---|
| `seed_launch` | `headless?`（既定 true）, `scene?`, `wait_seconds?`（既定 60） | `{ok, already_running, pid, port, exe, headless, state}` |
| `seed_attach` | `port`, `token` | `{ok, instance, state}`（利用者の明示同意が必要） |
| `seed_instance` | なし | `{ok, bound, port, pid, headless, attached, has_token}` |
| `seed_shutdown` | なし | `{ok, shutting_down}`（束縛中インスタンスのみ） |
| `seed_query` | `type`: `"scene"` \| `"assets"`, `dir?` | シーン情報 / アセット絶対パス一覧 |
| `seed_batch` | `operations: [{cmd, ...}]` | 各操作の成否（編集はここに集約する） |
| `seed_state` | なし | `{ok, state, runtime_connected, scene_path, selected_actor_dfs_id, actor_count, assets_path}` |
| `seed_hierarchy` | なし | `{ok, count, hierarchy:[{id,name,parent,is_2d,is_vp,active,has_canvas,is_prefab,prefab_source,prefab_hash,is_folder}]}`（`prefab_source` はプレハブの `assets://` パス、`prefab_hash` はシーンへ取り込んだ版のハッシュ。非プレハブ／版が不明なら `null`） |
| `seed_select` | `actor_dfs_id` \| `name` | `{ok, actor_dfs_id, components}`（ACTOR_COMPONENTS の JSON） |
| `seed_screenshot` | `target`: `"viewport"`\|`"game"`\|`"editor"`, `method?`: `"gpu"`（既定）\|`"screen"`, `path?`, `max_width?`, `scale?`, `keep_full?` | **画像（base64 PNG）** ＋ `{ok, path, width, height, scaled, full_width, full_height, full_path?, method, warning?}` |
| `seed_play` | `action`: `play`\|`pause`\|`resume`\|`stop`, `wait_seconds?` | `{ok, action, state, waited_secs}` |
| `seed_anim_preview` | `actor_dfs_id`\|`name`, `clip_path`, `time` | `{ok, actor_dfs_id, clip_path, time}` |
| `seed_anim_preview_stop` | `actor_dfs_id` \| `name` | `{ok, actor_dfs_id}` |
| `seed_anim_reload` | `clip_path` | `{ok, clip_path}` |
| `seed_log` | `lines?`（既定 200・最大 5000） | `{ok, path, lines, content}` |
| `seed_save_scene` | `confirm?`（ヘッドレスでは必須） | `{ok, scene_path}` |
| `seed_send_ipc` | `command` | `{ok, sent}` |
| `seed_prefab_reapply` | `actor_dfs_id` \| `name` \| `prefab_path` \| `all: true` のいずれか 1 つ | `{ok, target, sent, ...}`（プレハブインスタンスを .actor の最新内容で再展開。**破壊的**だが Undo 可能。docs/editor_prefab.md） |
| `seed_profile` | `seconds?`（既定 3・範囲 0.2〜30）, `top?`（既定 40） | 要約表（テキスト）＋ `{ok, seconds, dump:{profile, merge}}` |
| `seed_generate_fish_thumbnails` | `size?`（既定 512・範囲 64〜2048） | `{ok, total, succeeded, failed, catalog_path, failures[]}`（図鑑画像の一括生成。11.x 章） |
| `seed_save_get` | `key` | `{ok, result:{op,key,found,type,value}}`（実行中ランタイムの SEED.SaveData を読む） |
| `seed_save_set` | `key`, `value`, `type?`（int/float/string）, `flush?` | `{ok, result:{op,key,type,value}(,flushed)}`（進行状態を作ってから Play する用） |
| `seed_save_delete` | `key` | `{ok, result:{op,key,deleted}}` |
| `seed_save_flush` | なし | `{ok, result:{op,saved}}`（メモリ上の内容をディスクへ書き出す） |
| `seed_find_actor` | `name`（名前 or `Root/Child` パス）, `components?`（既定 true） | `{ok, name, found, dfs_id, components}`（選択は変えない） |
| `seed_input` | `keys?`（キー名の配列）, `click?`（`{x,y,button?}`）, `hold_ms?`（既定 80） | 各操作の `{ok, sent, reply}` を改行区切り（Play 中のみ） |
| `game_input_key` | `key`（KeyCode 名）, `down`（bool） | `{ok, sent, reply}`（Play 中のみ。9 章） |
| `game_input_mouse` | `button?`+`down?` / `dx?`,`dy?` / `x?`,`y?` / `scroll?` のいずれか 1 種 | `{ok, sent, reply}` |
| `game_input_sequence` | `events`（9.3 の JSON 配列）, `wait?`（既定 true） | `{ok, sent, reply}`（wait 時は `INPUT_SEQUENCE_DONE` まで待つ） |
| `game_input_release_all` | なし | `{ok, sent, reply}` |
| `seed_script_debug` | `name`（ゲーム側が登録したコマンド名）, `arg?` | `{ok, sent, reply}`（ゲームの途中の状態を 1 手で作る。Play 中のみ。10 章） |

`seed_screenshot` 以外の追加ツールは、内部的には
`POST /seed-ai/cmd` に `{"cmd":"<コマンド名>", ...}` を投げているだけなので、
`seed_batch` の `operations` からも同じコマンド名で呼べる
（`anim_preview` / `anim_preview_stop` / `anim_reload` / `select_actor` / `play_control` /
`save_scene` / `send_ipc` / `prefab_reapply` / `profile` / `game_input_key` / `game_input_mouse` /
`game_input_sequence` / `game_input_release_all` / `save_data` / `find_actor` / `script_debug`）。
`seed_save_*` は 1 つのコマンド `save_data` に `op`（get / set / delete / save）を足したもので、
`seed_batch` からは `{"cmd":"save_data","op":"set","key":"money","value":1200}` の形で呼ぶ。
`seed_input` は `game_input_key` / `game_input_mouse` を順に撃つ **MCP サーバー側のラッパ**なので、
`seed_batch` からは元のコマンド名を並べること。

### エディタ側コマンド名との対応

| MCP ツール | `cmd` | 実装 |
|---|---|---|
| `seed_launch` | （なし／MCP サーバー内で完結） | `SeedMcpServer/Launcher.cs::LaunchAsync` |
| `seed_attach` | （なし／MCP サーバー内で完結） | `SeedMcpServer/Launcher.cs::AttachAsync` |
| `seed_instance` | （なし／MCP サーバー内で完結） | `SeedMcpServer/SeedInstance.cs::ToJson` |
| `seed_shutdown` | `shutdown` | `IEditorAiHost.RequestShutdown`（`Application.Shutdown`） |
| `seed_screenshot`（gpu） | `screenshot_gpu` | `ExecuteScreenshotGpuAsync` → IPC `SCREENSHOT:{target},{path}` |
| `seed_screenshot`（screen） | `screenshot` | `EditorCommandExecutor.Visual.cs::ExecuteScreenshot` |
| `seed_select` | `select_actor` | `IEditorAiHost.SelectActorAsync`（`SELECT:` + `GET_ACTOR_COMPONENTS:`） |
| `seed_hierarchy` | `get_hierarchy` | 最後に届いた `HIERARCHY:` のキャッシュ |
| `seed_play` | `play_control` | `MainWindow.OnPlayPause` / `OnStop`（プレイバーと同じ経路） |
| `seed_anim_preview` | `anim_preview` | IPC `ANIM_PREVIEW:{dfs},{clip},{time}` |
| `seed_anim_preview_stop` | `anim_preview_stop` | IPC `ANIM_PREVIEW_STOP:{dfs}` |
| `seed_anim_reload` | `anim_reload` | IPC `ANIM_RELOAD:{clip}` |
| `seed_log` | `get_log` | `editor/logs/SEEDEditor.log` の末尾 N 行 |
| `seed_save_scene` | `save_scene` | `MainWindow.DoQuickSave()`（Ctrl+S と同じ） |
| `seed_state` | `get_editor_state` | `IEditorAiHost` の各プロパティ |
| `seed_send_ipc` | `send_ipc` | `RuntimeManager.SendToRuntime` へ素通し |
| `seed_prefab_reapply` | `prefab_reapply` | `EditorCommandExecutor.Visual.cs::ExecutePrefabReapply` → IPC `PREFAB_REAPPLY:{dfs}` / `PREFAB_REAPPLY_PATH:{path}` / `PREFAB_REAPPLY_ALL`（実装は `runtime/.../app/prefab_ops.rs`） |
| `seed_profile` | `profile` | `IEditorAiHost.ProfileDumpAsync`（IPC `PROFILE_DUMP:{秒}` → `PROFILE_DUMP_DONE:{パス}`） |
| `seed_generate_fish_thumbnails` | `generate_fish_thumbnails` | `IEditorAiHost.RenderActorThumbnailAsync`（IPC `RENDER_ACTOR_THUMBNAIL:...` → `RENDER_ACTOR_THUMBNAIL_DONE\|_ERROR`）を魚 prefab ごとに逐次 |
| `seed_save_get` / `seed_save_set` / `seed_save_delete` / `seed_save_flush` | `save_data`（`op` 違い） | `EditorCommandExecutor.SaveData.cs` → IPC `SAVE_DATA:{json}` → `SAVE_DATA_OK:{json}` / `SAVE_DATA_ERROR:{msg}`（実装は `runtime/.../app/save_data_ops.rs`） |
| `seed_find_actor` | `find_actor` | 同上 → `HierarchyPanel.ActorDfsIdByPath`（名前／パス → DFS ID）＋ `IEditorAiHost.GetActorComponentsAsync`（`GET_ACTOR_COMPONENTS:` のみ。`SELECT:` は送らない） |
| `seed_input` | （なし／MCP サーバー内で `game_input_*` を連続実行） | `SeedMcpServer/Program.cs::ExecInputAsync` |
| `game_input_key` | `game_input_key` | `EditorCommandExecutor.GameInput.cs` → IPC `INPUT_KEY:{key},{down\|up}` |
| `game_input_mouse` | `game_input_mouse` | 同上 → `INPUT_MOUSE_BUTTON` / `INPUT_MOUSE_MOVE` / `INPUT_MOUSE_POS` / `INPUT_SCROLL` |
| `game_input_sequence` | `game_input_sequence` | 同上 → `INPUT_SEQUENCE:{json}`（応答待ちは `IEditorAiHost.InjectGameInputAsync`） |
| `game_input_release_all` | `game_input_release_all` | 同上 → `INPUT_RELEASE_ALL` |
| `seed_script_debug` | `script_debug` | `EditorCommandExecutor.ScriptDebug.cs` → IPC `SCRIPT_DEBUG:{name},{arg}` → `SCRIPT_DEBUG_OK` / `SCRIPT_DEBUG_ERROR:{reason}`（実装は `runtime/.../app/script_debug_ops.rs`。応答待ちは `IEditorAiHost.InjectGameInputAsync` に相乗り） |

---

## 5. 代表的なループ

### 5.0 ヘッドレス運用（人が画面を見ていない環境）

エディタを画面に出さないまま起動し、AI だけで一周する手順。

```
1. seed_launch(headless:true, scene:"C:/.../runtime/assets/mainGame/MainGame.scene")
2. seed_state()                                   … Edit になるまで確認
3. seed_play(action:"play", wait_seconds:3)
4. seed_screenshot(target:"game")                 … method 既定 = gpu
5. seed_batch([{cmd:"write_asset_file", relative_path:"animations/hit.anim", content:"..."}])
   （既存ファイルの上書きは自動で <assets>/.backup へ世代バックアップされる。7.7 参照）
6. seed_anim_reload(clip_path:"seed://animations/hit.anim")
7. seed_anim_preview(name:"Player", clip_path:"...", time:0.4)
8. seed_screenshot(target:"viewport")
9. seed_shutdown()                                … 後始末（必ず呼ぶ）
```

- `seed_launch` は **自動では呼ばれない**。他ツールがブリッジへ到達できないときは
  「seed_launch を呼べ」というエラーを返すだけで、勝手にエディタを起動しない
  （利用者が意図せずプロセスを増やさないため）。
- **この MCP サーバーがすでにインスタンスを束縛していれば** `already_running:true` を返す。
  利用者が別のエディタを開いていても関係なく、新しいヘッドレスインスタンスを起動できる
  （ポートは 7300〜7399 の空きから選ばれる）。
- `seed_launch` は「ブリッジが応答したら返る」ではなく、**ランタイムが接続し
  （`scene` 指定時はそのシーンの読み込みが終わる）まで待ってから返る**。
  返った直後に `seed_screenshot` を撮ってよい。
- **`seed_shutdown` は未保存の変更を破棄して終了する**（ヘッドレスでは終了時の
  「保存しますか？」を出せないため）。残したい変更があるなら先に
  `seed_save_scene(confirm:true)` を呼ぶこと（ヘッドレスでは `confirm` が必須。7.6 参照）。
- ヘッドレス時はモーダルダイアログが**すべて抑止**される。
  出るはずだったものは `editor/logs/SEEDEditor.log` に
  `[ヘッドレス:ダイアログ抑止]` 付きで記録され、既定応答（破壊的でない側）が返る。
  Play をブロックするスクリプトコンパイルエラーなどは `seed_log` で確認すること。

**ヘッドレスの実装方式**: MainWindow は `Visibility.Hidden` にせず、
`WindowStyle=None / ShowInTaskbar=false / ShowActivated=false` の通常サイズ（1920×1080）で
仮想デスクトップ外（-32000, -32000）へ置く。ビューポートには wgpu(DX12) のランタイム
ウィンドウを `SetParent` で埋め込んでいるため、`Hidden` にするとコンテナが 0×0 になり
ランタイムが 1 フレームも描けなくなる（＝撮影もできない）。画面外配置なら HWND も
レイアウトも通常どおりで、DXGI の Present も GPU 読み戻しも表示状態に依存しない。


### 5.1 「見て直す」— .anim の修正ループ

```
1. seed_state()                                   … Edit 状態か・シーンパスの確認
2. seed_hierarchy()                               … 対象アクターの DFS ID を得る
3. seed_anim_preview(name:"Player", clip_path:"seed://animations/hit.anim", time:0.4)
4. seed_screenshot(target:"viewport")             … ポーズを目視
5. seed_batch([{cmd:"write_asset_file",
                relative_path:"animations/hit.anim", content:"..."}])
6. seed_anim_reload(clip_path:"seed://animations/hit.anim")   ← 必須
7. seed_anim_preview(... time:0.4)                … 読み直した内容で再適用
8. seed_screenshot(target:"viewport")             … 変化を確認（3〜8 を繰り返す）
9. seed_anim_preview_stop(name:"Player")          … 元の値へ復元（忘れると編集中の値が残る）
```

**6 を飛ばすと変化しない。** ランタイムは一度読んだ `.anim` をキャッシュするため、
ファイルを書き換えただけでは次の `anim_preview` に反映されない。

### 5.2 実行時の見た目を確認する

```
1. seed_play(action:"play", wait_seconds:3)   … Play にして 3 秒進める
2. seed_screenshot(target:"game")             … 進行後の画面
3. seed_log(lines:200)                        … LOAD_ERROR / スクリプト例外の確認
4. seed_play(action:"stop")                   … Edit へ戻す
```

### 5.3 シーン編集の確認

```
1. seed_query(type:"scene")
2. seed_batch([...編集...])
3. seed_screenshot(target:"viewport")
4. seed_save_scene(confirm:true)   … ヘッドレスでは confirm が必須
```

### 5.4 「計測 → 修正」ループ（性能改修）

性能の作業は、**必ず `seed_profile` の数字から始めて、同じ数字で締める**。
「速くなったはず」はレビューできない。

```
seed_launch(headless:true, scene:"<絶対パス>")
seed_play(action:"play", wait_seconds:5)      # 定常状態に入れてから測る
seed_profile(seconds:5, top:40)               # ← 修正前の数字
（コードを直す → ランタイムをビルドし直す → 上を繰り返す）
seed_profile(seconds:5, top:40)               # ← 修正後の数字
seed_shutdown()
```

`seed_profile` の返り値は 2 段構成になっている。

1. **スコープ表** — `profile_scope!` で仕込まれたセクションを `avg_ms` の降順で並べたもの。
   列は `avg_ms`（1 フレームあたりの平均）/ `max_ms`（窓内の最悪フレーム）/
   `self_ms`（子を除いた自己時間）/ `share`（フレーム比）/ `calls/f`（1 フレームあたりの
   呼び出し回数）/ 階層パス。**`calls/f` は「何回やっているか」を直接示す**ので、
   時間より先にここを見ると無駄な繰り返しが見つかる。
2. **統合バッチ更新ゲート表** — 統合バッチ（`InstancedModelBatch`）ごとに、
   そのフレームに `update()` を実行したか省いたか、実行したなら**なぜ省けなかったか**。
   理由は `merge_batch_gate::MergeGateReason` の列挙子名で出る。

| 理由 | 意味 | 静止シーンで出たら |
|---|---|---|
| `Skipped` | 入力不変で `update()` を省いた | 正常（この行が多いほどよい） |
| `PoseOnly` | 行列は静止・再生指定だけが進んだ → 軽量アップロードで済ませた | 正常（アニメ再生中のモデル） |
| `Mats` | インスタンス行列が変化した | 本当に動いているか確認する |
| `AbsIds` / `Tags` / `DisableLod` | 絶対 ID / セマンティックタグ / LOD 無効フラグが変化 | 異常。毎フレーム値が揺れている |
| `Pose` | 再生指定が変化（重い入力の整定前） | 数フレームだけなら正常 |
| `Lod` | 距離 LOD のバケット割り当てが変化 | カメラ静止中に出るなら異常（バケット境界の振動） |
| `VelocityReset` | 速度バッファのリセット要求フレーム | 毎フレーム出るなら異常 |
| `FirstFrame` | スナップショット未取得（初回・バッチ再生成直後） | 毎フレーム出るならバッチが作り直され続けている |

`updates_per_frame` が「1 フレームあたり何バッチを更新しているか」。
静止した画で二桁が出ていたら、上の理由表から犯人を特定する。

計測は**プロファイラパネルを開いていなくても動く**（計測中だけランタイムが自動で
有効化し、終わったら元へ戻す）。ダンプ本体はランタイムが一時ファイルへ書き、
IPC ではそのパスだけを返す（`PROFILE_DUMP_DONE:{パス}`）。

### 5.5 自前ビルドで起動する（利用者のエディタが動いている間）

利用者のエディタが起動していると `runtime/target/debug/SEED.exe` と
`editor/bin/Debug/net9.0-windows/SEEDEditor.exe` はロックされ、上書きできない。
別の出力先へビルドしたものを `seed_launch` で起動したいときは、
**MCP サーバーを起動するプロセスの環境変数**で exe の場所を上書きする。

| 環境変数 | 何を差し替えるか | 解決箇所 |
|---|---|---|
| `SEED_EDITOR_EXE` | `seed_launch` が起動する `SEEDEditor.exe` | `SeedMcpServer/Launcher.cs::ResolveEditorExePath` |
| `SEED_RUNTIME_EXE` | エディタが起動する `SEED.exe` | `editor/src/MainWindow.xaml.cs::ResolveRuntimePath` |

どちらも「実在するファイルを指しているときだけ」採用され、未設定・不在なら
従来の探索順にそのまま落ちる。環境変数はエディタへ継承されるので、
MCP サーバー側に 1 度設定すれば両方に効く。

```bash
# 例: ロックを避けて別ディレクトリへビルドしてから測る
cargo build --manifest-path runtime/Cargo.toml --target-dir /tmp/rt_target
dotnet build editor/SEEDEditor.csproj -p:OutDir=/tmp/ed_out/
SEED_RUNTIME_EXE=/tmp/rt_target/debug/SEED.exe SEED_EDITOR_EXE=/tmp/ed_out/SEEDEditor.exe   <MCP サーバーを起動するコマンド>
```

なお `SEED_RUNTIME_EXE` を使うと、エディタの「ソース変更を検知して cargo build」
（`RuntimeSourceWatcher`）は自動的に無効になる（指定先の 2 階層上に `Cargo.toml` が
無いため）。**ランタイムのビルドは自分で回すこと。**

---

## 6. 制約・注意点

### 6.1 スクリーンショットの縮小（`max_width` / `scale` / `keep_full`）

フル解像度の PNG をそのまま埋め込むとコンテキストを大量に消費するため、
`seed_screenshot` は撮影後の共通後処理として縮小を掛けられる。
撮影方式（`gpu` / `screen`）に依らず同じ挙動になる。

| 引数 | 型 | 意味 |
|---|---|---|
| `max_width` | integer | 縮小後の最大幅（px）。元画像がこれより狭ければ何もしない。 |
| `scale` | number | 縮小率 0〜1。1 以上は「指定なし」と同じ。 |
| `keep_full` | boolean | true なら縮小前のフル解像度版を `<名前>.full.png` として隣に残す。 |

- `max_width` と `scale` を両方指定した場合は **より小さくなるほう** が採用される（どちらも上限として働く）。
- 縮小後は縦横比を保ち、高さは自動で決まる（最低 1 px）。
- **返す画像も保存されるファイル（`path`）も縮小版**になる。フル解像度が必要なときは
  `keep_full: true` を付けて `full_path` を読むこと。
- 応答 JSON には `scaled`（縮小したか）と `full_width` / `full_height`（縮小前の寸法）が入る。
- リサンプルは WPF の `BitmapScalingMode.HighQuality`（Fant）。
- 読み込み・縮小に失敗しても撮影自体は成功しているため、**エラーにはせず**
  フル解像度のまま返し、理由を `warning` に入れる。

実装: `editor/src/AI/Capture/ScreenshotDownscaler.cs`（純粋な後処理ユーティリティ）を
`EditorCommandExecutor.Visual.cs::ApplyScreenshotDownscale` から両コマンドで共用する。

目安: レイアウトや配置の確認だけなら `max_width: 800`、
テクスチャや文字の確認まで要るときは無指定（フル解像度）。

### 6.2 スクリーンショットの 2 方式

`seed_screenshot` には `method` が 2 つある。**既定は `gpu`**。

| | `gpu`（既定） | `screen` |
|---|---|---|
| 実体 | ランタイムの提示テクスチャを `copy_texture_to_buffer` で読み戻して PNG 化 | 画面 DC（`GetDC(NULL)`）からの `BitBlt` |
| 撮れるもの | ランタイムの描画結果（シーンビュー／ゲーム画面） | 画面に映っている任意のウィンドウ矩形 |
| `target="editor"` | **不可**（自動的に `screen` へ切り替わる） | 可 |
| 最小化・他ウィンドウの裏 | **撮れる** | 撮れない（手前のウィンドウが写る） |
| 画面外・ヘッドレス | **撮れる** | 撮れない |
| リモート切断中・ロック中 | **撮れる** | 撮れない |

`gpu` 方式の流れ:

```
screenshot_gpu → IPC "SCREENSHOT:{target},{abs_path}"
   → ランタイムが次に描いたフレームの提示テクスチャをコピー → PNG 書き出し
   → IPC "SCREENSHOT_DONE:{path},{w},{h}" もしくは "SCREENSHOT_ERROR:{msg}"
```

提示テクスチャ（スワップチェーン）は常時 `COPY_SRC` 付きで構成されるため、
環境変数などの事前設定なしにいつでも撮れる（追加の GPU 作業はコピーを積んだフレームだけ）。

**ウィンドウが隠れていてもフレームが回る仕組み**: 隠れている／画面外のウィンドウには
OS が `WM_PAINT` を配送しないため、winit の `RedrawRequested` によるフレームループは止まる。
ランタイムは `about_to_wait`（イベントループ自体は回っている）から
「撮影要求が残っている間だけ」フレームを強制的に 1 枚回す。
ヘッドレス起動時（環境変数 `SEED_HEADLESS=1`）はこの強制フレームが常時有効になり、
非表示のままシミュレーションも進む。ウィンドウが最小化（サイズ 0×0）のときだけは
描画自体が不可能なため、待たせずに `SCREENSHOT_ERROR` を返す。

なお `target="viewport"` と `"game"` は `gpu` 方式では**同じ絵**になる
（どちらも「いま提示しているカラーターゲット」＝ Edit ならシーンビュー、Play ならゲーム画面）。

### 6.3 `target` の "viewport" と "game" は同じウィンドウのことがある

埋め込み Play（既定）では Edit ランタイムがそのまま Play になるため、
シーンビューとゲーム画面は同一の HWND。ウィンドウ Play（「ウィンドウを出してプレイ」）では
別プロセスの別ウィンドウになり、`RuntimeManager.RuntimeHwnd` がそちらへ差し替わる。

### 6.4 状態依存の制約

- `seed_play(action:"play")` は **Edit 状態でのみ**、`pause` は Play 中のみ、
  `resume` は Pause 中のみ、`stop` は Play/Pause 中のみ実行できる。それ以外は
  `{"ok":false,"error":"..."}` を返す（現在の状態がメッセージに入る）。
- `seed_save_scene` は Edit 状態のみ。新規（未保存）シーンは保存先が決まらないためエラー。
  さらに次の場合も拒否される（いずれもデータ損失の防止。詳細は 7 章）:
  ヘッドレスで `confirm:true` が無い / 他のエディタが同じシーンを開いている（`.lock`）/
  ランタイムが実際に読み込んでいるシーンと保存先が違う。
- `seed_anim_preview` は **Edit モード限定**（ランタイム側 `animation_ops.rs` の制約）。
- `seed_play` はプレイバーのハンドラをそのまま呼ぶため、**通常起動では
  エディタがモーダルダイアログを出すことがある**（スクリプトのコンパイルエラー、
  Play 起動失敗、アセットフォルダ不在など）。その間 UI スレッドは止まり、
  MCP 呼び出しはタイムアウトするまで返らない。
  **ヘッドレス起動（`seed_launch`）ではこれらは抑止され、ログへ流れる**ので
  この問題は起きない。人が画面を見ていない環境では必ずヘッドレスで起動すること。

### 6.5 タイムアウト

| 対象 | 上限 |
|---|---|
| MCP → HTTP（`HttpClient`） | 120 秒 |
| `seed_select` の ACTOR_COMPONENTS 待ち | 10 秒 |
| `seed_save_scene` の保存完了待ち | 10 秒 |
| `seed_play("play")` の状態遷移待ち | 60 秒（スクリプト再コンパイルを挟むため） |
| `seed_play` のその他の遷移待ち | 15 秒 |
| `seed_play` の `wait_seconds` | 0〜20 秒にクランプ |

すべて `await` ベースで実装しており、UI スレッドをブロックしない
（待っている間もエディタは操作できる）。

### 6.6 その他

- `seed_hierarchy` はランタイムが**変化時にのみ push** した `HIERARCHY:` の
  キャッシュを返す。エディタ起動直後にまだ 1 度も届いていなければ `[]`。
- `name` によるアクター指定は同名が複数あると DFS 順で最初のものを選ぶ。
  厳密に指定したいときは `actor_dfs_id` を使う。
- `seed_screenshot` は 8 MB を超える PNG を画像として埋め込まず、パスだけ返す。
- ヘッドレス起動中の `target:"editor"` は **真っ黒になる**（画面外ウィンドウを画面 DC からは
  撮れない）。エディタ UI 全体を見たいときだけ `seed_launch(headless:false)` を使う。
- `seed_shutdown` は応答を返してからプロセスを落とす。呼び出し後に他ツールを叩くと
  「接続できません」になる（正常）。
- `seed_send_ipc` は応答を待たず検証もしない。誤った文字列を送ってもエラーにならず
  ランタイム側で黙って無視される。結果は `seed_log` / `seed_screenshot` で確認すること。
- 追加コマンドの返り値はすべて JSON（`{"ok":true,...}` / `{"ok":false,"error":"..."}`）。
  従来のシーン編集コマンド（`add_actor` など）は AI 向けの日本語文章を返す設計のままにしてある。

---

## 7. 安全機構 — 何が守られているか

2026-09 に「AI 操作が利用者のエディタへ届き、別シーンの内容で `.scene` を上書きし、
最後にそのエディタを終了させた」データ損失事故が起きた（経緯は 8 章）。
その再発を**コードの側から不可能にする**ために入れた仕組みを、層ごとにまとめる。

### 7.1 インスタンス束縛（誰と話しているかを確定させる）

| 仕組み | 実装 |
|---|---|
| `seed_launch` は 7300〜7399 から空きポートを選び、16 バイトのランダムトークンを生成する | `Launcher.PickFreePort` / `GenerateToken` |
| エディタへ `--ai-port N --ai-token T`（＋環境変数 `SEED_AI_PORT` / `SEED_AI_TOKEN`）で渡す | `Launcher.LaunchAsync` → `EditorStartupOptions` |
| エディタは**そのポートしか**使わない。掴めなければ終了コード 78 で即終了（フォールバック無し） | `SeedAIBridge.Start` |
| `seed_launch` は起動したプロセスの生死を見つつ `GET /seed-ai/state` を叩き、**pid とトークンの両方**が一致して初めて成功を返す | `Launcher.IsExpectedInstance` |
| 以後すべてのリクエストに `X-Seed-Token` ヘッダーを付ける。エディタはトークン設定時、不一致・欠落を 403 で拒否 | `SeedInstance.TOKEN_HEADER` / `AiOperationPolicy.IsTokenValid` |
| 束縛が無い状態では、MCP が変更系ツールを一切実行しない（観測系のみ既定ポートへ到達可） | `SeedInstance.CheckToolAllowed` |

束縛が無いのに変更系ツールを呼ぶと、こう返る:

```
ERROR: seed_launch で起動したインスタンスのみ操作できます。…
```

### 7.2 対話エディタは既定で読み取り専用

利用者が手で起動したエディタ（`--ai-port` なし）は、外部からの操作を
**観測系だけ**に限定する。

- 通す: `get_scene_info` / `list_asset_files` / `get_hierarchy` / `get_editor_state` /
  `get_log` / `screenshot` / `screenshot_gpu`
- 拒否: `save_scene` / `load`・`set_value` / `add_*` / `remove_*` / `move_actor` /
  `play_control` / `anim_*` / `write_asset_file` / `send_ipc` / `shutdown`

**エディタ内蔵の AI アシスタントパネルは従来どおり動く。** 判定は
`AiOperationPolicy.CheckAllowed(command, origin)` の 1 か所に集約されており、
パネルからの呼び出しは `AiCommandOrigin.UserInitiated`（＝画面の前の人自身の依頼）として
常に許可される。HTTP ブリッジ経由だけが `Remote` になりポリシーの対象になる。

### 7.3 明示的な接続（seed_attach）

利用者のエディタを AI に操作させたいときだけの経路。

1. 利用者がエディタで **編集 → 環境設定** を開く
2. **「AI 操作を許可（このインスタンス）」** をオンにして保存
3. 同じ画面に出ている **ポートとトークン** を利用者が AI へ渡す
4. AI が `seed_attach(port, token)` を呼ぶ

この設定は**永続化しない**。エディタを再起動すると必ずオフ（読み取り専用）へ戻る。
トークンを持たない既定エディタでは `seed_attach` は成立しない（そのため
`--ai-token` 付きで起動していないインスタンスへは接続できない）。

### 7.4 シーンパスの整合性（誤ったパスへの上書きを止める）

- ランタイムは「実際に読み込んだシーンのパス」（`App::loaded_scene_path`）を持ち、
  読み込み成功時にのみ更新する（`LOAD_SCENE` / 起動時ロード / スクリプトのシーン遷移）。
- 読み込み完了通知は **`SCENE_LOADED:<path>`**。エディタの現在シーンパスは
  この応答を出所とし、設定口は `MainWindow.ApplyCurrentScenePath` の 1 か所だけ。
- 保存 IPC は 3 種類に分かれる。

  | コマンド | 用途 | パス整合性チェック | 読み込み中パスの更新 |
  |---|---|---|---|
  | `SAVE_SCENE:<path>` | 上書き保存（Ctrl+S / AI `save_scene`） | **する** | しない |
  | `SAVE_SCENE_AS:<path>` | 名前を付けて保存 | しない | **する** |
  | `SAVE_SCENE_COPY:<path>` | Play 用一時シーンの書き出し | しない | しない |

- 上書き保存の要求先がランタイムの読み込み中シーンと違えば、**1 バイトも書かずに**
  `SAVE_ERROR:path_mismatch:<実際のパス>` を返す（地形のフラッシュも行わない）。
  エディタは「保存先とランタイムが実際に読み込んでいるシーンが違う」という
  日本語のエラーに言い換えて表示する（ヘッドレスではログのみ）。

### 7.5 シーンロック（多重編集の防止）

- シーンを開いたエディタは `<scene>.lock` を作る（`{pid, headless, started_at, machine}`）。
- 2 つ目のインスタンスが同じシーンを開くと **読み取り専用**になる。
  タイトルバーに `[読み取り専用]` が付き、保存（Ctrl+S・AI `save_scene` とも）は
  「誰が掴んでいるか」を添えて拒否される。
- ロック保持者のプロセスが死んでいれば、そのロックは無効として上書きされる
  （異常終了で誰も編集できなくなるのを避けるため）。
  別マシンが張ったロックは生死を確認できないので有効扱い（安全側）。
- 解放はシーン切り替え時とエディタ終了時。

### 7.6 ヘッドレス保存の明示同意

ヘッドレスインスタンスの `seed_save_scene` は **`confirm:true` が必須**。
利用者が画面を見ていない状態で `.scene` を書き換えないための安全弁。

```
seed_save_scene(confirm:true)
```

### 7.7 原子的保存と世代バックアップ

`.scene` / `.actor`、および AI の `write_asset_file` による書き込みはすべて:

1. 旧版を `<assets>/.backup/<アセットルート相対ディレクトリ>/<名前>.<yyyyMMdd-HHmmss><拡張子>` へ複製
2. `<file>.tmp` へ書き切ってから置換（rename）

同一ファイルにつき最新 **10 世代**（`BACKUP_KEEP` / `SafeFileWriter.BackupKeep`）を残す。

**復旧手順**:

```
1. runtime/assets/.backup/ 配下で、元ファイルと同じ相対パスのフォルダを開く
2. 目的の時刻のファイル（例 MainGame.20260907-101112.scene）を選ぶ
3. タイムスタンプ部分を取り除いた名前（MainGame.scene）で元の場所へコピーし直す
   ※ 上書きされる現在のファイルも念のため退避しておく
```

`.backup/` と `*.lock` / `*.tmp` は `.gitignore` 済み。

### 7.8 shutdown の制限

`shutdown` はヘッドレスインスタンス、または利用者が環境設定で明示的に許可した
インスタンスにしか効かない。**既定では、利用者が開いているエディタを AI が閉じることはできない。**

---

## 8. ポストモーテム: 2026-09 のシーン上書き事故

### 何が起きたか

MCP 経由の作業中に、利用者が開いていたエディタで `prologue/proLogue.scene` を開いていた。
AI が `seed_launch` でヘッドレスエディタを起動し、`MainGame.scene` を読み込んで編集・保存し、
`seed_shutdown` で終了させた——**つもりだった**。

実際には、`proLogue.scene` の中身が `MainGame.scene` の内容で上書きされ、
利用者のエディタが終了した。

### 根本原因（ログとタイムスタンプから確認）

1. AI ブリッジのポートは **7234 固定**。利用者の対話エディタが先にそれを掴んでいた。
2. `seed_launch` が起動したヘッドレスエディタはポートを掴めなかった。
   しかし**ブリッジの起動失敗は致命的ではなく**、ログ 1 行を残して起動を続けていた。
3. `seed_launch` の疎通確認は「ポート 7234 が応答するか」だけを見ており、
   応答者が自分の起動したプロセスかを確認していなかった → 成功と判定。
4. 以後の MCP コマンド（`LOAD_SCENE MainGame.scene`、各種編集、`seed_save_scene`、
   `seed_shutdown`）はすべて **利用者のエディタ**が実行した。
5. 利用者のエディタは「現在のシーンパス = proLogue.scene」を保持したまま、
   ランタイムの中身だけが MainGame に入れ替わった。
   → `save_scene` が `SAVE_SCENE:proLogue.scene` を送り、MainGame の内容が書き込まれた。
6. `seed_shutdown` が `Application.Shutdown()` を呼び、利用者のエディタが閉じた。

つまり **4 つの独立した設計欠陥**が直列に並んでいた:
固定ポート / 起動失敗の非致命扱い / 応答者の身元未確認 / 保存先の未検証。

### どう直したか（欠陥 → 対策）

| 欠陥 | 対策 | 節 |
|---|---|---|
| ポートが固定で、誰が掴んだか区別できない | 空きポート＋インスタンストークン | 7.1 |
| ブリッジの起動失敗が非致命 | `--ai-port` 指定時は終了コード 78 で即終了 | 7.1 |
| 応答者の身元を確認していない | `GET /state` の pid＋トークン照合、全リクエストにトークン | 7.1 |
| 対話エディタが AI の変更操作を無条件で受ける | 既定で読み取り専用＋明示的な `seed_attach` | 7.2 / 7.3 |
| 保存先がランタイムの実体と一致するか見ていない | `SCENE_LOADED:<path>` を単一の出所にし、`SAVE_SCENE` でパス照合 | 7.4 |
| 同じシーンを 2 つのエディタが開ける | `<scene>.lock` と読み取り専用モード | 7.5 |
| 上書きに失敗・誤爆すると復旧不能 | 原子的置換＋10 世代バックアップ | 7.7 |
| AI が対話エディタを閉じられる | `shutdown` をヘッドレス／明示許可に限定 | 7.8 |

### 事故が起きたときにやること

1. **まずエディタを閉じない**。開いているシーンの内容が正しければ、
   別名で保存して退避する。
2. `runtime/assets/.backup/` から失われたファイルの直前世代を復旧する（7.7 の手順）。
3. `editor/logs/SEEDEditor.log` で `[SeedAIBridge] 起動` の行を確認し、
   どのインスタンスがどのポートを掴んでいたかを突き合わせる。
4. `seed_instance` を呼び、MCP がどのインスタンスを束縛しているかを確認する。

---

## 9. ゲーム入力の注入（`INPUT_*` IPC）

AI が**実際にゲームを遊んで**（キーボード・マウスを送って）結果を
`seed_screenshot` で確認するための IPC。ランタイムの `Input` へ直接注入するので、
スクリプトの `SEED.Input.*` / `InputMap` のアクションがそのまま反応する。

- パース: `runtime/src/engine/core/input/inject/command.rs`（純粋関数・単体テスト付き）
- 注入状態: `runtime/src/engine/core/input/inject/state.rs`
- シーケンス再生: `runtime/src/engine/core/input/inject/sequence.rs`
- 実入力との合成: `runtime/src/engine/core/input/mod.rs`（`Input` の各クエリ）
- アプリ側ハンドラ: `runtime/src/engine/core/app_base/app/input_inject_ops.rs`
- IPC 配線: `runtime/src/engine/core/app_base/ipc.rs`（`IpcCommand::InputInject`）＋
  `app/ipc_handler.rs`（分岐 1 本と毎フレームの `tick_input_injection`）

### 9.1 コマンド一覧

| コマンド | 意味 |
|---|---|
| `INPUT_KEY:{keyName},{down\|up}` | キーの押下 / 解放 |
| `INPUT_MOUSE_BUTTON:{left\|right\|middle},{down\|up}` | マウスボタンの押下 / 解放 |
| `INPUT_MOUSE_MOVE:{dx},{dy}` | 相対移動（`MouseDelta` / `MouseMove` に加算） |
| `INPUT_MOUSE_POS:{x},{y}` | 絶対座標（ゲームビューポート左上原点 px） |
| `INPUT_SCROLL:{amount}` | ホイール（ライン数。実入力と同じ単位） |
| `INPUT_SEQUENCE:{json}` | 時間軸付きイベント列を 1 バッチで再生 |
| `INPUT_RELEASE_ALL` | 注入中の押下をすべて解放（安全弁） |

`keyName` は **InputMap と同じ表記**（`W` / `Space` / `Enter` / `LeftShift` /
`Alpha0` / `Keypad0` / `UpArrow` / `F1` …）。表の正典は
`runtime/src/engine/core/input/action_map.rs::key_from_name` で、注入側に複製は無い。
`down|up` とボタン名は大文字小文字を問わない。

### 9.2 応答

| 応答 | いつ |
|---|---|
| `INPUT_OK` | 受理した |
| `INPUT_ERROR:{reason}` | 拒否した（下表） |
| `INPUT_SEQUENCE_DONE` | シーケンスの全イベントを発火し終えた（非同期） |

| reason | 意味 |
|---|---|
| `not_playing` | Play 中でない（Edit 中は一律で拒否する） |
| `sequence_busy` | 別のシーケンスを再生中（`INPUT_SEQUENCE_DONE` を待ってから送り直す） |
| `unknown_command` | `INPUT_` で始まるが未知のコマンド |
| `bad_args` | 引数の個数が違う / 空 |
| `bad_number` | 数値として読めない・NaN・無限大 |
| `unknown_key:{name}` / `unknown_mouse_button:{name}` | 名前が表に無い |
| `bad_key_state:{s}` | `down` / `up` 以外 |
| `bad_json:{…}` / `empty_sequence` / `empty_event:{i}` / `ambiguous_event:{i}` / `missing_down:{i}` / `bad_vec2:{i}` / `bad_time:{i}` | シーケンス JSON の不備（`{i}` は要素の添字） |

`reason` は必ず 1 行・200 文字以内に整形される（IPC は 1 行 1 コマンドのため）。

### 9.3 `INPUT_SEQUENCE` の JSON

```json
[
  {"t":0.0, "key":"W", "down":true},
  {"t":0.5, "key":"W", "down":false},
  {"t":0.6, "mouse_move":[120,0]},
  {"t":0.62,"mouse_move":[120,0]},
  {"t":0.7, "mouse_button":"left", "down":true},
  {"t":0.8, "mouse_pos":[640,360]},
  {"t":0.9, "scroll":-1.0}
]
```

- `t` は**シーケンス開始からの経過秒（実時間）**。ゲーム時間ではないので
  `Time.Scale` の影響を受けない。**Play 一時停止中は進まない**。
- `t` は省略可（既定 0＝即時）。t の昇順へ安定ソートされるため、
  **同じ t の要素は書いた順に同一フレームでまとめて発火する**。
- **1 要素につき操作は 1 個**（`key` / `mouse_button` / `mouse_move` / `mouse_pos` /
  `scroll` のいずれか 1 つ）。2 つ以上書くと `ambiguous_event`。
  同時刻に複数やりたいときは、同じ `t` の要素を並べる。
- `key` / `mouse_button` には `down` が必須。
- 未知のキー名（綴り間違い）は `bad_json` で拒否する（黙って無視しない）。
- 「マウスを左から右へ振る」ような連続移動は、`mouse_move` を複数フレームに
  割って並べる（1 フレームに全部入れても速度は生まれない）。
- 再生中に次の `INPUT_SEQUENCE` を送ると `sequence_busy`。
  `INPUT_SEQUENCE_DONE` を待ってから送ること。

### 9.4 実入力との合成規約

| 種別 | 合成 |
|---|---|
| キー / マウスボタンの押下・トリガ・リリース | 実入力と **OR**（どちらかが立てば立つ） |
| 相対移動 (`MouseDelta` / `MouseMove`) | **加算** |
| ホイール | **加算** |
| 絶対座標 (`MousePos` / `MousePositionCanvas`) | 注入がある間は**注入値が優先**（実カーソルを無視） |

- 注入した押下は、**明示の `up` / `INPUT_RELEASE_ALL` / Play 停止**まで保持される。
- `GetKeyDown` / `GetKeyUp` に相当するエッジは、注入側も実入力と**同じフレーム境界**
  （`Input::end_frame`）で畳まれるので、きっちり 1 フレームだけ立つ。
- `INPUT_RELEASE_ALL` と Play 停止による解放は、解放されたキーを
  そのフレームの `GetKeyUp` として観測させる（押しっぱなしのまま消えて
  スクリプトの状態機械が壊れるのを防ぐため）。
- 絶対座標の注入は `INPUT_RELEASE_ALL` / Play 停止で解除され、実カーソルへ戻る。
- **注入が 1 つも無ければ、実入力の挙動は従来とビット単位で同一**
  （実入力側の判定式には手を入れていない）。

### 9.5 制約

- **Play 中のみ有効**。Edit 中はすべて `INPUT_ERROR:not_playing`
  （Edit で注入するとギズモ・カメラ操作と混ざり、誰の入力か分からなくなるため）。
- 一時停止（`seed_play(action:"pause")`）中でも単発コマンドは受理される
  （押下は保持され、`resume` 後に効く）。進まないのはシーケンスの時計だけ。
- 注入はランタイム内部の `Input` に対して行うもので、**OS のカーソルは動かない**。
  スクリーンショットにカーソルは写らないし、他のウィンドウにも影響しない。
- ゲームパッドの注入は未対応（必要になったら `INPUT_PAD_*` を同じ流儀で足す）。

### 9.6 エディタ / MCP 側の結線（実装済み）

ランタイム・エディタ・MCP のすべてが結線済みで、MCP ツールから直接ゲームを操作できる。

- MCP ツール定義: `editor/SeedMcpServer/Program.cs`（`GameInputKeyTool` ほか）
- コマンド実装: `editor/src/AI/Tools/EditorCommandExecutor.GameInput.cs`
- 1 行応答の待ち合わせ: `editor/src/MainWindow.AiHost.cs::InjectGameInputAsync`
  （`RuntimeManager.InputInjectReplyReceived` を購読して待つ。`SCREENSHOT_DONE` と同じ流儀だが、
  `INPUT_SEQUENCE` は「受理 → 完了」の 2 通が届くのでキュー（`Channel`）で 1 通ずつ読む）
- 実行許可: `editor/src/AI/AiOperationPolicy.cs`（`GAME_INPUT_COMMAND_PREFIX`）。
  `game_input_*` は**変更系**。読み取り専用インスタンス（利用者が手で起動したエディタ）では
  `DENY_GAME_INPUT` で拒否される。

| MCP ツール | 引数 | 説明 |
|---|---|---|
| `game_input_key` | `key`（KeyCode 名）, `down`（bool） | キーを押す / 離す。押しっぱなしは維持される |
| `game_input_mouse` | `button?`（left/right/middle）＋`down?`, `dx?`,`dy?`（相対移動）, `x?`,`y?`（絶対座標）, `scroll?` | マウス操作 1 件。**1 回につき 1 種類だけ**指定する（2 種類以上はエラー） |
| `game_input_sequence` | `events`（9.3 の JSON 配列）, `wait?`（既定 true = `INPUT_SEQUENCE_DONE` まで待つ） | 時間軸付き操作をまとめて再生 |
| `game_input_release_all` | なし | 注入中の押下をすべて解放 |

**戻り値**（4 ツール共通）:

```json
{"ok": true,  "sent": "INPUT_KEY:W,down", "reply": "INPUT_OK"}
{"ok": false, "sent": "INPUT_KEY:W,down", "reply": "INPUT_ERROR:not_playing",
 "error": "Play 中ではないため入力を注入できません（…）。seed_play(action:\"play\") で再生してから呼んでください。"}
```

`reply` はランタイムの生応答、`error` はそれを読んで対処できる日本語へ言い換えたもの
（理由コードの一覧は 9.2 節）。応答が返らなかった場合は `reply: null` とタイムアウトの旨が入る。

**タイムアウト**: 単発コマンドは 5 秒。`game_input_sequence` は `wait:true` のとき
「events の最大 `t` ＋ 5 秒」まで待つ（最大 `t` の上限は 60 秒。それより長い操作は
分割するか `wait:false` で投げて `seed_screenshot` で確認する）。

#### 使用例

W を 1 秒だけ押して前進させ、その結果を撮る（単発コマンド版。押している間の待ちは
`seed_play(wait_seconds:...)` で作る）:

```
seed_play(action:"play", wait_seconds:2)
game_input_key(key:"W", down:true)          # → {"ok":true,"reply":"INPUT_OK"}
seed_play(action:"pause", wait_seconds:1)   # 1 秒ぶん進めてから止める（押下は保持される）
game_input_key(key:"W", down:false)
seed_screenshot(target:"game", max_width:800)
```

同じことをシーケンスで（**待ち時間まで含めて 1 回で済む**ので通常はこちらを使う）:

```
game_input_sequence(events:[
  {"t":0.0, "key":"W", "down":true},
  {"t":1.0, "key":"W", "down":false}
])                                     # INPUT_SEQUENCE_DONE まで待って返る
seed_screenshot(target:"game", max_width:800)
```

マウスを左から右へ振る（1 フレームに全部入れても速度は生まれないので複数フレームへ割る）:

```
game_input_sequence(events:[
  {"t":0.00, "mouse_move":[60,0]},
  {"t":0.02, "mouse_move":[60,0]},
  {"t":0.04, "mouse_move":[60,0]},
  {"t":0.06, "mouse_move":[60,0]},
  {"t":0.08, "mouse_move":[60,0]}
])
seed_screenshot(target:"game", max_width:800)
```

典型ループ（後始末を忘れないこと）:

```
seed_play(action:"play", wait_seconds:2)
game_input_sequence(events:[{"t":0,"key":"W","down":true},{"t":1.0,"key":"W","down":false}])
seed_screenshot(target:"game", max_width:800)
game_input_release_all()
seed_play(action:"stop")
```

---

## 10. デバッグコマンド（`SCRIPT_DEBUG` IPC / `seed_script_debug`）

AI が**ゲームの奥まった場面だけを 1 手で作って**確認するための IPC。
ゲーム側の C# スクリプトが `SEED.Debug.OnCommand(name, handler)` で用意した入口を
そのまま叩くので、入力注入（9 章）で長い手順を踏まなくてよい。

**入力注入との使い分け**

| やりたいこと | 使うもの |
|---|---|
| 操作そのものを検証したい（歩ける・投げられる・当たる） | `seed_input` / `game_input_*`（9 章） |
| 操作の先にある**結果の見た目**を検証したい（釣り上げ演出・リザルト・ゲームオーバー） | `seed_script_debug`（本章） |

前者で釣り上げまで辿り着くには「構える → 投げる → 巻く → アタリを待つ → 合わせる →
やり取りに勝つ」を全部成功させる必要があり、途中で失敗すると**何が原因で画が出ないのか
切り分けられない**。デバッグコマンドは本物の入口（ゲーム側の関数）を直接呼ぶので、
確認したい場面だけを確実に、しかも**本番と同じ経路で**再現できる。

- パース: `runtime/src/engine/core/app_base/ipc.rs`（`parse_script_debug`・単体テスト付き）
- 待ち行列: `runtime/src/engine/core/scripting/debug_command.rs`（単体テスト付き）
- アプリ側ハンドラ: `runtime/src/engine/core/app_base/app/script_debug_ops.rs`
- C# への取り出し: `host_api.rs::ffi_script_debug_take` → `ScriptHost.TryTakeDebugCommand`
- 配信: `scripting/src/ScriptBridge.cs::DispatchDebugCommandsOncePerFrame` →
  `SEED.Debug.DispatchPendingCommands`
- エディタ側: `editor/src/AI/Tools/EditorCommandExecutor.ScriptDebug.cs`
- MCP ツール: `editor/SeedMcpServer/Program.cs::SeedScriptDebugTool`

### 10.1 コマンドと応答

| コマンド | 意味 |
|---|---|
| `SCRIPT_DEBUG:{name},{arg}` | 名前付きのデバッグ指示を 1 件送る（`arg` は省略可） |

- `name` と `arg` は**最初のカンマ 1 個**だけで割る。`arg` の中のカンマはそのまま渡る。
- `name` の前後の空白は落ちる。`name` が空／空白入り／改行入りは拒否。
- IPC は 1 行 1 コマンドなので、`name` にも `arg` にも改行は入れられない。

| 応答 | いつ |
|---|---|
| `SCRIPT_DEBUG_OK` | 受理して待ち行列へ積んだ |
| `SCRIPT_DEBUG_ERROR:not_playing` | Play 中でない（Edit 中は一律で拒否） |

**受理＝ハンドラが動いた、ではない**点に注意。ランタイムはコマンドの意味を知らないので、
未登録の名前でも受理する。実際に動いたかは `seed_log` で
`[Script:警告] [Debug] 登録されていないデバッグコマンド: xxx` が出ていないか確認する。

### 10.2 ゲーム側の書き方

```csharp
public override void OnStart()
{
    SEED.Debug.OnCommand("catch_test", HandleCatchTest);
}

public override void OnDestroy()
{
    SEED.Debug.OffCommand("catch_test", HandleCatchTest);   // 外し忘れ厳禁
}
```

詳細は `docs/scripting_api.md` の「Debug.OnCommand」節が正典。

### 10.3 登録済みのコマンド（mainGame）

| name | arg | 何が起きるか |
|---|---|---|
| `catch_test` | 魚の表示名（省略可） | 進行中の釣りを畳み、指定の魚（省略時は竿先に一番近い魚）でその場で釣り上げ演出（ホワイトアウト → 縦跳び → 釣果パネル）を起こす。実装は `FishingController.HandleCatchTestCommand` |
| `records_reset` | なし | 図鑑の釣果記録（`best_size:` / `best_rank:` / `catch_count:` の全魚種ぶん）だけを消して保存する。進行フラグなど他のセーブデータは触らない。<b>釣りシーンと図鑑シーンの両方</b>から同じ名前で叩ける（実装は `FishingController.HandleRecordsResetCommand` と `Zukan.HandleRecordsResetCommand`）。図鑑シーンで叩くとその場でページを組み直すので、未捕獲のシルエット表示・初捕獲演出をやり直したいときに使う。図鑑シーンでは `F12` の長押し（既定 1.5 秒。`Zukan` のインスペクタで変更可）でも同じことができる |
| `result_confirm` | なし | 釣果パネルの決定入力の代わりに 1 段進める（本体 → 図鑑登録パネル → 登録パネルを畳む → 本体を閉じる）。入力の待ち時間と `InputGate` を飛ばすので、チュートリアル中で決定が塞がれていても開閉を確認できる。実装は `ResultPanel.HandleResultConfirmCommand` |

### 10.4 使い方の例（釣り上げ演出の確認）

```
seed_launch(headless:true, scene:".../mainGame/MainGame.scene")
seed_play(action:"play", wait_seconds:8)          # 魚が湧くのを待つ
seed_save_set(key:"catch_count", value:0)         # 図鑑登録の分岐も見たいとき
seed_script_debug(name:"catch_test")              # ← 演出だけを起こす
（0.8 秒待つ）seed_screenshot(...)                # 跳んでいるところ
（3 秒待つ）  seed_screenshot(...)                # 釣果パネル
seed_input(keys:["Enter"]) → seed_screenshot(...) # 図鑑登録パネル
seed_input(keys:["Enter"]) → seed_screenshot(...) # 閉じたあと
seed_shutdown()
```

### 10.5 制約

- **Play 中のみ有効**。Edit 中はすべて `SCRIPT_DEBUG_ERROR:not_playing`。
- 待ち行列は Play の開始・停止で空になる（前回 Play の指示が突然走らない）。
- 溜められるのは 64 件まで。溢れたら**古いものから**捨てる。
- 配信は**フレーム先頭（BeginFrame フェーズ）に 1 回**。同じフレームの `OnStart` で
  登録したハンドラは、登録がそれより後になると 1 件取りこぼすことがある
  （＝コマンドは Play 開始後に送ること）。
- 変更系コマンドなので、`seed_launch` で起動した束縛済みインスタンスでのみ実行できる。

---

## 図鑑画像の一括生成（`seed_generate_fish_thumbnails`）

魚 prefab を 1 体ずつオフスクリーンで描いて、**背景が透明な横向きサムネイル PNG** を書き出し、
最後に魚図鑑のデータ表 `FishCatalog.cs` を生成し直す。

| | |
|---|---|
| 入口 | エディタ `ツール > 図鑑画像を生成` / MCP `seed_generate_fish_thumbnails(size?)` / cmd `generate_fish_thumbnails` |
| 入力 | `runtime/assets/mainGame/actors/Fish/Lv<N>/*.actor`（`Lv<N>` ディレクトリがレベルの正典） |
| 出力 | `runtime/assets/mainGame/textures/zukan/Lv<N>/<名前>.png`（正方形 RGBA）<br>`runtime/assets/mainGame/scripts/FishCatalog.cs` |
| エディタ無しでの再生成 | `python tools/gen_fish_catalog.py`（PNG は作らず `FishCatalog.cs` のみ） |

### ランタイム側の IPC

```
RENDER_ACTOR_THUMBNAIL:<actor>,<out_png>,<size_px>,<view>
  → RENDER_ACTOR_THUMBNAIL_DONE:<out_png>
  → RENDER_ACTOR_THUMBNAIL_ERROR:<理由>
```

- `<actor>` は `assets://` 仮想パスでも絶対パスでもよい。`<out_png>` は絶対パス。
- `<view>` は `side`（+X 側から。図鑑はこれ）/ `front`（+Z 側から）/ `top`（+Y 側から）。
- **パスにカンマは使えない**（引数の区切りと区別できないため、明示的にエラーを返す）。
- 実装は `runtime/src/engine/core/app_base/app/thumbnail_ops.rs`（進行）と
  `runtime/src/engine/core/renderer/actor_thumbnail.rs`（構図計算・マスク・PNG 化）。

### 仕組み（現在のシーンを壊さない理由）

1. 専用の**隔離ワールド線**へアクタを 1 体だけ読み込む。SEED の描画は
   `active_world_line` に属するアクタだけを集めるので、ユーザーのシーンは
   エンティティごと残ったまま「描かれない」状態になる。スクリプトは
   `scripting_host: None` で読み込むため生成すらされない（魚が泳ぎ出さない）。
2. AABB から正射カメラを組み、**ID パスを強制**して 1 枚撮る。
   ID テクスチャは背景に 0 を書くので、`ID != 0` が色に依存しない被写体マスクになる。
3. 撮れたマスクの実測値で構図を**追い込む**（最大 4 回）。モデルのローカル AABB は
   実際に描かれる範囲より大きいことがあり（描画されない補助メッシュなど）、
   AABB だけで決めると被写体が隅に小さく写る prefab が実在した。
4. マスクでアルファを抜き、アルファブリードを掛けてから中央の正方形を切り出して縮小し、PNG を書く。
5. 撮影が終わってもすぐにはワールド線を戻さない（**セッション**）。1 体ごとに戻すと
   その 1 フレームのためにシーン全体のレイトレーシング加速構造が組み直され、
   一括生成の途中で GPU 資源を使い切ってランタイムが落ちる。
   最後の要求から 2 秒間何も来なければ、元のシーンとカメラへ戻す。

### 注意

- 一括生成は**逐次**（1 体ずつ）。31 体で実測 35 秒前後。
- サムネイルの解像度はウィンドウの短辺が上限（中央の正方形を切り出して縮小するため）。
  ヘッドレスは 1920x1080 なので 1080px まで劣化なしで出せる。
- シーンの保存は一切行わない。読み取り専用で開いたシーンでも実行できる。
