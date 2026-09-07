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
| `seed_hierarchy` | なし | `{ok, count, hierarchy:[{id,name,parent,is_2d,is_vp,active,has_canvas,is_prefab,is_folder}]}` |
| `seed_select` | `actor_dfs_id` \| `name` | `{ok, actor_dfs_id, components}`（ACTOR_COMPONENTS の JSON） |
| `seed_screenshot` | `target`: `"viewport"`\|`"game"`\|`"editor"`, `method?`: `"gpu"`（既定）\|`"screen"`, `path?`, `max_width?`, `scale?`, `keep_full?` | **画像（base64 PNG）** ＋ `{ok, path, width, height, scaled, full_width, full_height, full_path?, method, warning?}` |
| `seed_play` | `action`: `play`\|`pause`\|`resume`\|`stop`, `wait_seconds?` | `{ok, action, state, waited_secs}` |
| `seed_anim_preview` | `actor_dfs_id`\|`name`, `clip_path`, `time` | `{ok, actor_dfs_id, clip_path, time}` |
| `seed_anim_preview_stop` | `actor_dfs_id` \| `name` | `{ok, actor_dfs_id}` |
| `seed_anim_reload` | `clip_path` | `{ok, clip_path}` |
| `seed_log` | `lines?`（既定 200・最大 5000） | `{ok, path, lines, content}` |
| `seed_save_scene` | `confirm?`（ヘッドレスでは必須） | `{ok, scene_path}` |
| `seed_send_ipc` | `command` | `{ok, sent}` |

`seed_screenshot` 以外の追加ツールは、内部的には
`POST /seed-ai/cmd` に `{"cmd":"<コマンド名>", ...}` を投げているだけなので、
`seed_batch` の `operations` からも同じコマンド名で呼べる
（`anim_preview` / `anim_preview_stop` / `anim_reload` / `select_actor` / `play_control` /
`save_scene` / `send_ipc`）。

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
