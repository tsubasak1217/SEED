---
name: debug-script-runtime-issues
description: SEEDのC#スクリプトが実行されない・ブレークポイントで止まらない・デバッグ中にエディタがフリーズするなど、スクリプト実行/デバッグ系の不具合を切り分けるときに使用する。症状別のトラブルシューティング手順書。
---

# スクリプト実行/デバッグ系トラブルシューティング

SEEDのC#スクリプト実行・デバッグ回りは、過去に「サイレント故障（エラーが出ずに全スクリプトが止まる）」「別プロセスにアタッチしてしまう」「凍結ウィンドウへの同期Win32でUIごとデッドロック」という3種の罠を踏んでいる。ここではそれらを症状別に定型化する。まず症状を1つ特定し、上から順に確認すること。

## 前提となる仕組み（切り分けの土台）

- ユーザーの `.cs` は **全ファイルを1アセンブリに一括コンパイル**する（`ScriptAssemblyManager.CompileAndLoad`）。したがって1ファイルのエラーで**全スクリプトが停止**する。
- コンパイルは Roslyn で **埋め込みポータブルPDB付き**・メモリロード（`LoadFromStream` の動的アセンブリ `SEEDUserScripts_<guid>`）。埋め込みPDBがあるため netcoredbg / VS でも行マッピング可能。
- スクリプトは **Play 中のランタイム内でのみ実行**される。しかも **Play は Edit とは別プロセスとして毎回起動**する（デバッグアタッチ先の落とし穴）。
- 停止中（ブレークポイント）はランタイムの**メインスレッド（描画・winitメッセージポンプ・スクリプトが同一スレッド）が凍結**する。凍結ウィンドウへ同期Win32を投げると呼び出し側スレッドごとデッドロックする。

---

## 症状1: スクリプトが一切実行されない（Update も呼ばれない）

ブレークポイント以前に、そもそもスクリプトが動いていないケース。ブレークポイントが「効かない」相談の真因がこれであることも多い。

### まず確認すること
- Output パネルに `[Runtime→Editor] SCRIPTS_RELOADED — N 型をコンパイル・再生成完了` が出ているか。
- `SCRIPTS_RELOADED:-1`（＝リロード失敗）や `[ScriptCompileError] ...` の行が出ていないか（`editor/src/Runtime/RuntimeManager.cs` の `SCRIPTS_RELOADED:` ハンドラが目立つ枠で表示する）。

### 原因候補（可能性が高い順）
1. **ファイル横断のコンパイルエラーによるサイレント停止**（最頻）。型名の重複など、単一ファイルでは気づかない衝突で一括コンパイルが失敗し、全スクリプトが Placeholder のまま止まる。過去の実例: あるスクリプト内の `enum` と別スクリプトのクラスが同名（例: `Test`）になり、一括コンパイルが失敗して全スクリプトがサイレント停止した。
2. **SEEDScripting.dll の再ビルド漏れ**。ランタイムは `../scripting/bin/Debug/net9.0/SEEDScripting.dll` をロードする。API・ローダを変更したのに再ビルドしていないと、古い挙動のまま or 型解決に失敗する。
3. **collectible ALC の Resolving 失敗**。基底クラス `SEEDScript` を含む SEEDScripting 本体は、Rust ランタイムが hostfxr 経由で Default とは別の独立 ALC にロードする。collectible ALC 既定のフォールバック（Default ALC）では名前解決できず `GetTypes()` が `FileNotFoundException` で落ち、スクリプトが1つもロードされない（→ Update も呼ばれず、ブレークポイントも当然止まらない）。
4. スクリプトが1つも `.cs` として存在しない／`IScriptComponent` を実装した非 abstract クラスが無い（この場合は正常に 0 型で終了する）。

### 確認方法
- **保存時検証**: エディタは保存時に `ScriptCompiler.CompileProjectDiagnostics`（`editor/src/Scripting/ScriptCompiler.cs`）で全体を事前検証し、エラー一覧パネルへ出す。ここにエラーが並んでいれば原因1。
- **Play 前検証**: `MainWindow.CheckScriptsBeforePlay`（`editor/src/MainWindow.xaml.cs`）が Play 開始前に検証し、エラーがあればエラー一覧を自動アクティブ化＋ダイアログで Play をブロックする。ブロックされたら原因1。
- 原因3は `ScriptAssemblyManager.CompileAndLoad` 内の `_context.Resolving` ハンドラ（`AppDomain.CurrentDomain.GetAssemblies()` でALC横断検索）が付いているかを確認。ここが外れていると `FileNotFoundException`。

### 対処
- 原因1: エラー一覧の `[ScriptCompileError] <file>(<line>): ...` を潰す。型名・enum 名の重複に注意。
- 原因2: `scripting/` を再ビルドしてから再度 Play。
- 原因3: `_context.Resolving` フォールバック（`AppDomain` 全体から同名アセンブリ検索）を維持する。ここは削除・改変時に壊れやすい。

---

## 症状2: ブレークポイントで止まらない（スクリプトは動いている）

Update は動いている（ログや挙動で確認できる）のに、赤丸を置いた行で停止しないケース。

### まず確認すること
- Play 中か（スクリプトは Play 中しか動かない＝停止対象も Play 中だけ）。
- Output/ログに `[デバッグ] BP設定OK <file>:<line>` が出ているか、それとも `[デバッグ] BP未解決 ...` か（`ScriptDebugSession.SetBreakpointsAsync` が verified を必ずログ化する）。

### 原因候補（可能性が高い順）
1. **アタッチ先プロセスの誤り**（最頻・過去の真因）。スクリプトは「Play 中のみ」動き、Play は Edit とは別プロセスで毎回起動する。手動で「今のプロセス」にアタッチすると通常は Edit プロセスに付き、その後 Play で起動する**別プロセスにはデバッガが付かない**。verified=true でも Update が走る Play プロセスに到達していない。
2. `justMyCode=true` になっている。ユーザースクリプトはメモリ上の動的アセンブリのため、JMC 判定で「ユーザーコードでない」と分類され、有効だとブレークポイントがスキップされる。
3. netcoredbg が見つからない／起動失敗（そもそもアタッチできていない）。
4. ソースパス不一致・PDB 未読込で netcoredbg が行をシーケンスポイントへバインドできず `verified=false`。

### 確認方法
- **自動アタッチが効いているか**が要。現在は手動アタッチは廃止され、`MainWindow._debugAutoAttach = true`（既定）で `OnStateChanged(Play)` → `TryAutoAttachDebuggerOnPlay` → `AttachDebuggerAsync(pid)` が **Play プロセスへ自動アタッチ**する（PID は `RuntimeManager.CurrentProcessId`）。Stop→Play 反復にも追従。ここが動いていれば原因1は解消済み。
- `setBreakpoints` 応答の `verified` をログで確認。`verified=false` かつメッセージがあれば原因4。全て `verified` なのに止まらないなら原因1（別プロセス）を疑う。
- netcoredbg の所在は `NetcoredbgLocator`（`SEED_NETCOREDBG` 環境変数／`tools\netcoredbg\netcoredbg.exe`／PATH の順）。未配置ならアタッチ時に案内メッセージ。

### 対処
- 原因1: 自動アタッチ経路（`TryAutoAttachDebuggerOnPlay`／`AttachDebuggerAsync`）が生きているか、`_debugAutoAttach` が true か確認。実行中にトグルした BP は `ScriptEditorPanel.BreakpointsChanged` → `OnBreakpointsChanged` → `setBreakpoints` 即送信で反映される。
- 原因2: attach 引数に `justMyCode=false` を必ず付ける（`ScriptDebugSession.StartAsync` の attach で設定済み。ここを消さない）。
- 原因3: netcoredbg を配置（`docs/scripting_debugger.md` 参照）。
- 補足: netcoredbg はメモリロードの動的アセンブリでも埋め込みPDBを読み（symbolStatus: Symbols loaded）、`verified=true` で BP を解決できることを実機確認済み。**デバッガ経路そのものは正常**なので、止まらない場合はまず「実行されているか（症状1）」「アタッチ先プロセス」を疑う。

---

## 症状3: デバッグで停止中にエディタがフリーズする

ブレークポイントで止まった瞬間、またはステップ/継続の操作でエディタ（WPF）ごと固まるケース。

### 根本原因（共通原則）
停止中はランタイムのメインスレッドが凍結する。**凍結したデバッギ（ランタイム）のウィンドウへ同期 Win32 を投げるコードは全てデッドロック候補**。同期クロスプロセス/クロススレッド Win32（`SetParent` / `SetWindowPos`（同期）/ `BringWindowToTop` / `SetForegroundWindow` 等）は相手ウィンドウのメッセージ処理完了を待つため、凍結相手だと**呼び出した UI スレッドごと固まる**。

### 原因候補と対処
1. **ウィンドウ埋め込み（Pause）による埋め込み**。`RuntimeManager.Pause()` → `EmbedRuntimeWindow()` の `SetParent` / `SetWindowPos` が凍結ウィンドウへ届くとデッドロック。
   - 対処: 停止中は `ScriptDebugSession.IsStopped` を `RuntimeManager.DebuggerSuspended` に反映し、`DebuggerSuspended` が true の間は `Pause()` が埋め込みを**抑止**する（実装済み）。Play/Pause ボタンは停止中「継続」として振る舞い、Stop は**先にデタッチ**して凍結を解除する。
2. **継続時のゲームウィンドウ前面化**。`BringGameWindowToFront` が同期 `BringWindowToTop` / `SetForegroundWindow` を呼ぶと、BP を1つ残したまま継続 → 数フレーム後に別 BP で再停止し凍結した瞬間に呼び出し中だと UI ごとデッドロック。
   - 対処: 前面化を**別スレッド（`Task.Run`）へ逃がす**か、`SWP_ASYNCWINDOWPOS`（`NativeInterop.SWP_ASYNCWINDOWPOS = 0x4000`）付きの非同期 `SetWindowPos` で Z オーダーのみ上げ、凍結してもブロックしないようにする（現行は `BringGameWindowToFront` が `Task.Run` で逃がす実装）。自ウィンドウ（エディタ）の前面化は安全なので停止時は `Activate()` を使う。

### 停止中の見え方（正常動作・故障ではない）
描画ループが止まるため自由な見回しは**原理的に不可**。凍結 Play ウィンドウの最終フレームは `FrozenFramePreview`（DWM サムネイル `DwmRegisterThumbnail`）で Viewport に静止表示する（`ShowFrozenFramePreview`）。DWM はコンポジタ保持の最終 present サーフェスを合成するため、スレッド応答不要でデッドロックせず、GPU ウィンドウでも取得できる。継続/終了で解除。**画面が止まって見えるのは仕様**であり、症状3のフリーズ（エディタ操作不能）とは区別すること。

---

## その他の既知の罠

- **[PERF] ログ氾濫**: ランタイムの `[PERF]` ログは既定で無効。`SEED_PERF_LOG` 環境変数がある時だけ出る。デバッグ時にログが埋もれるなら環境変数の有無を確認。
- **ステップ中の前面化抑止**: ステップ操作は継続→停止が瞬時に繰り返されるため、その間に前面化やツールバー切替をすると挙動が乱れる。`MainWindow._debugStepping` フラグで抑止している。
- **API 追加後の未反映**: スクリプトから使える API を足したのに補完/実行に出ない場合、`docs/scripting_api.md`（正典・AI補完の注入元）と `docs/scripting_api.html` の更新漏れ、または `runtime/src/engine/core/scripting/host_api.rs` のレジストリ登録漏れ、SEEDScripting 再ビルド漏れを疑う。

---

## 関連ファイル（調査の起点マップ）

- スクリプトのコンパイル/ロード: `scripting/src/ScriptAssemblyManager.cs`（一括コンパイル・collectible ALC・`Resolving` フォールバック・埋め込みPDB発行）、`scripting/src/ScriptBridge.cs`
- エディタ側の事前検証: `editor/src/Scripting/ScriptCompiler.cs`（`CompileProjectDiagnostics`）、`editor/src/Panels/ScriptEditorPanel.cs`
- デバッガ本体: `editor/src/Debugger/ScriptDebugSession.cs`（attach 手順・`justMyCode=false`・setBreakpoints/verified ログ・実行制御）、`editor/src/Debugger/DapClient.cs`（DAP フレーミング）、`editor/src/Debugger/NetcoredbgLocator.cs`、`editor/src/Debugger/FrozenFramePreview.cs`
- 自動アタッチ・フリーズ回避・状態遷移: `editor/src/MainWindow.xaml.cs`（`_debugAutoAttach` / `TryAutoAttachDebuggerOnPlay` / `AttachDebuggerAsync` / `OnBreakpointsChanged` / `BringGameWindowToFront` / `ShowFrozenFramePreview` / `CheckScriptsBeforePlay`）
- ランタイム制御・ログ処理: `editor/src/Runtime/RuntimeManager.cs`（`DebuggerSuspended` / `CurrentProcessId` / `Pause`+`EmbedRuntimeWindow` / `SCRIPTS_RELOADED:` ハンドラ）、`editor/src/Native/NativeInterop.cs`（`SWP_ASYNCWINDOWPOS` 等）
- ランタイム側スクリプト: `runtime/src/engine/core/scripting/`（`mod.rs`, `host_api.rs` = ECS ブリッジ・レジストリ）、`runtime/src/engine/core/app_base/app/script_ops.rs`（`SCRIPTS_RELOADED` 送出）
- ブレークポイント永続化/UI: `editor/src/Panels/ScriptEditor/Breakpoint*.cs`（`editor/settings/breakpoints.json`）
- ドキュメント: `docs/scripting_debugger.md`（セットアップ・使い方）、`docs/scripting_api.md`（API 正典）
