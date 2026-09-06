# SEED エディタ MCP サーバー（seed-editor）

Claude Code / Gemini CLI などの外部エージェントから、**動作中の SEED エディタ**を
ツールとして操作するための MCP サーバー。
「変更 → 目視確認 → 再修正」のループを、人間がスクリーンショットを貼らなくても回せるようにする。

- MCP サーバー本体: `editor/SeedMcpServer/Program.cs`（stdio + JSON-RPC 2.0）
- エディタ側 HTTP ブリッジ: `editor/src/AI/SeedAIBridge.cs`（`http://localhost:7234/seed-ai/`）
- コマンド実装: `editor/src/AI/Tools/EditorCommandExecutor.cs`（シーン編集）と
  `editor/src/AI/Tools/EditorCommandExecutor.Visual.cs`（目視確認・再生制御・アニメ）
- エディタ本体への窓口: `editor/src/AI/Tools/IEditorAiHost.cs` ← 実装は `editor/src/MainWindow.AiHost.cs`
- 画面キャプチャ: `editor/src/AI/Capture/WindowScreenCapture.cs`

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

エディタ 1 つに対して MCP サーバーは何個でも接続できる（HTTP なので排他はない）。
ポートは `SeedAIBridge.PORT = 7234` 固定。

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

2. **SEED エディタを起動しておく**。
   HTTP ブリッジは設定不要・自動起動で、`MainWindow` の初期化時に生成される
   `AIAssistantPanel` のコンストラクタから `SeedAIBridge.Start()` が呼ばれる。
   起動に成功すると `editor/logs/SEEDEditor.log` に次の行が出る。

   ```
   [SeedAIBridge] 起動: http://localhost:7234/seed-ai/
   ```

   ポート衝突などで失敗した場合は `[SeedAIBridge] 起動失敗 ...` が出るだけで
   エディタは起動し続ける（＝ MCP ツールは全滅するのでログを必ず確認する）。

3. Claude Code 側で `/mcp` を実行し `seed-editor` が connected になっていれば完了。
   エディタが落ちていても MCP サーバー自体は起動し、ツール一覧は返る
   （実行時に「SEED エディタへ接続できません」というエラーが返る）。

---

## 4. ツール一覧

| ツール | 引数 | 返り値 |
|---|---|---|
| `seed_query` | `type`: `"scene"` \| `"assets"`, `dir?` | シーン情報 / アセット絶対パス一覧 |
| `seed_batch` | `operations: [{cmd, ...}]` | 各操作の成否（編集はここに集約する） |
| `seed_state` | なし | `{ok, state, runtime_connected, scene_path, selected_actor_dfs_id, actor_count, assets_path}` |
| `seed_hierarchy` | なし | `{ok, count, hierarchy:[{id,name,parent,is_2d,is_vp,active,has_canvas,is_prefab,is_folder}]}` |
| `seed_select` | `actor_dfs_id` \| `name` | `{ok, actor_dfs_id, components}`（ACTOR_COMPONENTS の JSON） |
| `seed_screenshot` | `target`: `"viewport"`\|`"game"`\|`"editor"`, `path?` | **画像（base64 PNG）** ＋ `{ok, path, width, height, warning?}` |
| `seed_play` | `action`: `play`\|`pause`\|`resume`\|`stop`, `wait_seconds?` | `{ok, action, state, waited_secs}` |
| `seed_anim_preview` | `actor_dfs_id`\|`name`, `clip_path`, `time` | `{ok, actor_dfs_id, clip_path, time}` |
| `seed_anim_preview_stop` | `actor_dfs_id` \| `name` | `{ok, actor_dfs_id}` |
| `seed_anim_reload` | `clip_path` | `{ok, clip_path}` |
| `seed_log` | `lines?`（既定 200・最大 5000） | `{ok, path, lines, content}` |
| `seed_save_scene` | なし | `{ok, scene_path}` |
| `seed_send_ipc` | `command` | `{ok, sent}` |

`seed_screenshot` 以外の追加ツールは、内部的には
`POST /seed-ai/cmd` に `{"cmd":"<コマンド名>", ...}` を投げているだけなので、
`seed_batch` の `operations` からも同じコマンド名で呼べる
（`anim_preview` / `anim_preview_stop` / `anim_reload` / `select_actor` / `play_control` /
`save_scene` / `send_ipc`）。

### エディタ側コマンド名との対応

| MCP ツール | `cmd` | 実装 |
|---|---|---|
| `seed_screenshot` | `screenshot` | `EditorCommandExecutor.Visual.cs::ExecuteScreenshot` |
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

---

## 5. 代表的なループ

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
4. seed_save_scene()
```

---

## 6. 制約・注意点

### 6.1 スクリーンショットは「画面に映っているもの」を撮る

実装は**画面 DC（`GetDC(NULL)`）からの `BitBlt`**。したがって:

- エディタウィンドウが**最小化されている / 画面外にある**と失敗する（エラーを返す）。
- **他のウィンドウに隠れている**部分は、その手前のウィンドウが写る。
  結果がほぼ真っ黒だった場合は `warning` フィールドで通知する。
- リモートデスクトップ切断中・スクリーンロック中は撮れない。

**なぜこの方式か**: ビューポートに埋め込まれるランタイムウィンドウは wgpu(DX12) の
スワップチェーンを直接 Present する GPU ウィンドウで、`PrintWindow` やウィンドウ DC への
`BitBlt` では中身が取れない（真っ黒になる。`editor/src/Debugger/FrozenFramePreview.cs` の
コメントにある既知の制約と同じ）。画面 DC は DWM が合成した実際の表示内容を保持しているため、
GPU ウィンドウでも正しく撮れる。

**ランタイム側の読み戻し（より堅牢な方式）は未実装**。`runtime/src/engine/core/renderer/screenshot.rs`
に環境変数駆動（`SEED_SCREENSHOT_DIR` / `SEED_SCREENSHOT_FRAMES`）のスワップチェーン読み戻しが
すでにあり、これを IPC 駆動に拡張すれば「隠れていても撮れる」「エディタウィンドウ全体は撮れない」
方式へ移行できる。今回は
（1）ランタイムの再ビルドなしで既存ビルドのまま使えること、
（2）`target:"editor"` はどのみちエディタ側キャプチャでしか実現できないこと、
（3）スワップチェーンの `COPY_SRC` が `screenshot::is_enabled()` 時のみ付く実装で、
起動時に有効化しないと後から読み戻せないこと、
を理由に見送った。backlog に残してある。

### 6.2 `target` の "viewport" と "game" は同じウィンドウのことがある

埋め込み Play（既定）では Edit ランタイムがそのまま Play になるため、
シーンビューとゲーム画面は同一の HWND。ウィンドウ Play（「ウィンドウを出してプレイ」）では
別プロセスの別ウィンドウになり、`RuntimeManager.RuntimeHwnd` がそちらへ差し替わる。

### 6.3 状態依存の制約

- `seed_play(action:"play")` は **Edit 状態でのみ**、`pause` は Play 中のみ、
  `resume` は Pause 中のみ、`stop` は Play/Pause 中のみ実行できる。それ以外は
  `{"ok":false,"error":"..."}` を返す（現在の状態がメッセージに入る）。
- `seed_save_scene` は Edit 状態のみ。新規（未保存）シーンは保存先が決まらないためエラー。
- `seed_anim_preview` は **Edit モード限定**（ランタイム側 `animation_ops.rs` の制約）。
- `seed_play` はプレイバーのハンドラをそのまま呼ぶため、**エディタがモーダルダイアログを
  出すことがある**（スクリプトのコンパイルエラー、Play 起動失敗など）。
  その間 UI スレッドは止まり、MCP 呼び出しはタイムアウトするまで返らない。
  人が画面を見ていない状況で `seed_play` を連打しないこと。

### 6.4 タイムアウト

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

### 6.5 その他

- `seed_hierarchy` はランタイムが**変化時にのみ push** した `HIERARCHY:` の
  キャッシュを返す。エディタ起動直後にまだ 1 度も届いていなければ `[]`。
- `name` によるアクター指定は同名が複数あると DFS 順で最初のものを選ぶ。
  厳密に指定したいときは `actor_dfs_id` を使う。
- `seed_screenshot` は 8 MB を超える PNG を画像として埋め込まず、パスだけ返す。
- `seed_send_ipc` は応答を待たず検証もしない。誤った文字列を送ってもエラーにならず
  ランタイム側で黙って無視される。結果は `seed_log` / `seed_screenshot` で確認すること。
- 追加コマンドの返り値はすべて JSON（`{"ok":true,...}` / `{"ok":false,"error":"..."}`）。
  従来のシーン編集コマンド（`add_actor` など）は AI 向けの日本語文章を返す設計のままにしてある。
