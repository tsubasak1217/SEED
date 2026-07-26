# SEED エンジン — 設計のこだわりと注目機能

Rust 製ゲームランタイム（`runtime/`）＋ C#/WPF エディタ（`editor/`）で構成される自作ゲームエンジン **SEED** の、
設計上のこだわりと技術的なハイライトをまとめた紹介資料です。

クラス図は [class_diagrams.md](class_diagrams.md) にまとめています（Mermaid 形式）。

---

## 全体アーキテクチャ

```
┌─────────────────────────────┐        Named Pipe (テキストIPC)        ┌──────────────────────────────┐
│  C# / WPF エディタ (editor/) │ ◄──────────────────────────────────► │  Rust ランタイム (runtime/)    │
│  AvalonDock ドッキングUI      │                                       │  wgpu レンダラ / ECS / 物理    │
│  インスペクタ / ヒエラルキー   │        HWND 埋め込み (SetParent)       │  地形 / アニメーション / 音声   │
│  スクリプトエディタ + AI補完   │ ◄──────────────────────────────────► │  C# スクリプトホスト (hostfxr) │
└─────────────────────────────┘                                       └──────────────────────────────┘
```

- **プロセス分離 + HWND 埋め込み**: エディタとランタイムは別プロセス。ランタイムのウィンドウを
  `HwndHost`（`editor/src/Viewport/ViewportHost.cs`）経由で `SetParent` し、WPF のドッキングパネル内に
  ネイティブ描画のビューポートとして埋め込む。ランタイムがクラッシュしてもエディタは巻き込まれない。
- **改行区切りテキスト IPC**: `\\.\pipe\SEED_{guid}` の Named Pipe 上で `COMMAND:arg1,arg2,...` 形式の
  テキストプロトコルを流す（Rust 側 `runtime/src/engine/core/app_base/ipc.rs`、C# 側 `editor/src/Ipc/PipeServer.cs`）。
  100 種超のコマンドを `IpcCommand` enum に集約し、パーサは副作用のない純関数群としてユニットテスト可能。
- **読み書きスレッド分離**: Rust 側 IPC は read/write 専用スレッド＋`mpsc` チャネル構成。
  同期 write がパイプ詰まり時にレンダースレッドを数百 ms ブロックした実バグの反省から、
  レンダーループを一切ブロックしない構造にした。

---

## 1. 自作 ECS — 「スロット専用エンティティ」による同型コンポーネント多重化

`runtime/src/engine/ecs/`（entity / storage / system / schedule / world）に、外部クレートに依存しない ECS を実装。

**こだわりポイント**

- **世代カウンタ付き Entity**: `Entity { index: u32, generation: u32 }`。despawn 後にインデックスを再利用しても
  世代不一致でダングリング参照を検出できる。
- **SparseSet ストレージ**: `sparse: Vec<u32>`（`u32::MAX` をセンチネルにして `Option` のタグ分を節約）＋
  `dense: Vec<(Entity, T)>`。insert / get / remove すべて O(1)、remove は swap_remove で dense 配列を密に保ち
  イテレーションのキャッシュ効率を確保。
- **データ / ロジック完全分離**: `Component` は `Any + Send + Sync` のマーカートレイトのみ。
  ロジックは `System` トレイト（`Phase` 7 段階: BeginFrame → EarlyUpdate → Update → ConstantUpdate →
  LateUpdate → Render → EndFrame）に置く。この 7 フェーズは C# スクリプトのライフサイクルとも 1:1 対応。
- **スロット専用エンティティ**（独自設計）: 上位レイヤの `Actor`（Unity の GameObject 相当）は
  `ComponentSlot` の Vec を持ち、**スロットごとに専用の ECS エンティティを spawn** する。
  実データはスロット専用エンティティに insert されるため、TypeId で 1 エンティティ 1 コンポーネントに縛られる
  素朴な ECS の制約を破らずに、**同一 Actor へ同型コンポーネント（Model×2 など）を複数アタッチ**できる。
- **world_line による多世界線編集**: `Actor.world_line: u32` で「メインシーン / アクター編集タブ /
  AI 編集用の隔離空間 / キャンバス編集」を同一 World 内でフィルタし、タブごとに独立した編集空間を実現。

---

## 2. レンダリング — wgpu 25 による Deferred + Clustered パイプライン

`runtime/src/engine/core/renderer/`。WGSL シェーダ約 70 ファイル。

**こだわりポイント**

- **naga リフレクションによる宣言的パイプライン構築**: WGSL を naga で解析し、
  `RenderPipelineBuilder`（`pipeline_config.rs`）が TOML + WGSL の宣言から BindGroupLayout を自動導出。
  シェーダを書けばパイプライン定義の大半が付いてくる。
- **Deferred + Clustered ライティング**: G-Buffer（MRT）→ フルスクリーン三角形でのライティング復元
  （`gbuffer.rs` / `deferred.rs`）。視錐台を **16×9×24 = 3456 フロクセル**（Doom 2016 準拠）に分割し、
  compute shader（`cluster_build.wgsl`）が atomic カーソルでクラスタ毎の影響ライトを詰める
  （`clustered.rs`、クラスタあたり最大 256 灯・全体 1024 灯）。
- **GPU 能力による段階的グレースフルデグレード**: ライティングパイプラインは
  「RT + バインドレス色付き影 / RT / 非 RT」の 3 バリアントを実行時の GPU 能力で自動切替。
  反射も RT → SSR → OFF のように機能マトリクス型 `RenderFeatures` / `ResolvedFeatures` で一元管理。
- **カメラ固有リソースの型安全な使い分け**: `LightingPass` enum（MainCamera / CameraPreview）で
  クラスタ・CSM リソースをカメラ別に持ち、「別カメラ用の BindGroup を誤って使う」事故を型で防止。
- **フル装備のポスト/GI スタック**: DDGI（レイトレ GI）、SSGI、SSR / RT リフレクション、RT-AO / SSAO、
  RT シャドウ + バイラテラルデノイズ、WBOIT 半透明、Bloom、FXAA、Hi-Z オクルージョン、
  bindless テクスチャ、草の GPU インスタンシング、GPU パーティクル。
- **A/B パリティ検証**: IPC の `SetPostFx.deferred` フラグで Forward 経路へフォールバックでき、
  Deferred 化の画質検証をワンタッチで行える。

---

## 3. ボクセル地形 — SDF + Marching Cubes、洞窟も掘れる地形エディタ

`runtime/src/engine/terrain/` は **ECS / GPU 非依存の純粋データ＋アルゴリズム層**として実装。

**こだわりポイント**

- **密度ベース地形**: 1 チャンク = 33³ サンプルの f32 密度グリッド（SDF）を Marching Cubes でメッシュ化。
  ハイトマップ地形と違い**洞窟・オーバーハングを掘れる**。
- **用途別の精度選択**: 密度は勾配（法線）精度を重視して f32、ペイントスプラット
  （レイヤ番号 4 + 重み 4 + 優先度）は u8 量子化してメモリ節約 — 1 チャンク約 467KB。
- **独自バイナリフォーマット `.tvox`**: マジックナンバー付き、v1 → v2 → v3 の後方互換読み込みを実装。
  散布データも `.tscatter` 独自形式。
- **ブラシ / レイヤ / 散布**: `BrushOp`（Add / Subtract / Smooth / Flatten）によるスカルプト、
  斜度・高度ルールの自動下地と手ペイントをブレンドするレイヤシステム、
  ルールベース自動散布＋ブラシ手描き散布の両対応（草は GPU インスタンシング＋風パラメータ）。
- **地形専用 Undo スタック**: シーン全体の Undo と分離した `TerrainUndo` / `TerrainRedo` /
  `TerrainStrokeEnd` を持ち、ストローク単位で確定する。
- **テスト容易性**: エンジン非依存の純関数群のため `tests.rs` / `tests_layers.rs` / `tests_scatter.rs` /
  `bench.rs` でロジック単体をテスト・ベンチできる。
- **エディタ UX**: シーンタブに Blender 風の common / terrain モード切替を常設し、
  terrain モード時のみ地形ツールバー行が出現（`MainWindow.Terrain.cs` + `TerrainSettingsWindow`）。

---

## 4. C# スクリプティング — hostfxr の in-process ホスト + ホットリロード

`runtime/src/engine/core/scripting/`（Rust 側）＋ `scripting/`（C# 側）＋ 正典 `docs/scripting_api.md`。

**こだわりポイント**

- **.NET CLR を Rust プロセスに直接ホスト**: `netcorehost` クレートで hostfxr をロードし、
  `UnmanagedCallersOnly` な C# 静的メソッドの関数ポインタ（ライフサイクル 7 フェーズ、コンパイル、
  フィールド設定など）を取得して直接呼ぶ。プロセス間マーシャリングなしの低レイテンシ実行。
- **ホットリロード**: DLL をプロセス専用一時ディレクトリへ**シャドウコピー**してからロードすることで
  ビルド出力をロックせず、collectible AssemblyLoadContext による再コンパイル・アンロードで
  エディタを再起動せずスクリプトを差し替えられる。
- **FFI 境界の徹底単純化**: Rust ⇔ C# のコンポーネント読み書きは `ScriptHostApi` 関数ポインタ表 1 枚に集約し、
  データは「f32 配列 1〜4 要素」に統一変換（bool = 0/1、Vector3 = 3 要素）。
  新しいコンポーネントの公開は「Rust レジストリ登録 → C# ラッパー → docs 追記」の 3 点セットで完結し、
  FFI シグネチャを増やさない。
- **遅延コマンドキュー**: スクリプトからの Instantiate / Destroy はキューに積み、フェーズ実行後に
  まとめて適用（Unity の遅延 Destroy と同じ思想）。フェーズ実行中の Actor ツリー変更を構造的に排除。
- **`#[repr(C)]` レイアウト契約**: `RawFrameContext` / `RawPhysicsEvent` は C# 側構造体と
  メモリレイアウト完全一致をプロジェクトルールとして明文化。
- **Unity ライクだが独自判断の API**: `GetComponent<T>()` は `T?`（Nullable）を返す、
  `using SEED;` をあえて自動挿入しない（`System.Random` との衝突回避）、
  Transform.Position への代入だけで押し戻しが解決されるキャラクターコントローラ（KCC、60Hz 物理同期）など。

---

## 5. 埋め込みインプレース Play — 「ロード実質ゼロ」の Play 遷移

`runtime/src/engine/core/app_base/app/play_mode_ops.rs` ＋ `editor/src/Runtime/RuntimeManager.cs`。

**こだわりポイント**

- **Keep / Restore の非対称スナップショット**: Play 開始時、地形ルートやアクター編集タブは
  `Keep(Entity)` で**現物保持**、通常のシーンアクターだけ `Restore(ActorData)` でシリアライズ退避。
  Play 終了時は Restore 対象のみ despawn → 再構築する。地形再構築 ≒17 秒 + BLAS/草構築 ≒17 秒を
  丸ごとスキップし、GPU リソース・モデルキャッシュを保持したまま**その場で Play 化**する。
- **段階的に築いた高速化の歴史**（`RuntimeManager` の状態機械）:
  1. Edit ランタイム常駐 — Play 中も Edit プロセスを Kill せず非表示保持（GPU 再初期化 22 秒を回避）
  2. Play プロセス常駐再利用 — Stop 時に `PAUSE_RENDER` で眠らせ、次回は `LOAD_SCENE` だけで再開
     （コールドスタート数十秒 → 数秒）
  3. インプレース Play（現行） — `ENTER_PLAY` / `EXIT_PLAY` で別プロセスすら起動しない
- **細部の泥臭い作り込み**: `WM_PARENTNOTIFY` フックで子 HWND へのクリックを AvalonDock の
  ペインアクティブ化へ転送、`SetWinEventHook` で最小化検知 → Pause 遷移、
  デバッガ停止中の Win32 同期呼び出しデッドロック回避（`DebuggerSuspended` フラグ）など。

---

## 6. 汎用プロパティトラック・アニメーション

`runtime/src/engine/animation/` ＋ エディタ側ドープシート（`editor/src/Panels/AnimationTimeline/`）。

**こだわりポイント**

- **プロパティレジストリ方式**: トラックのターゲットは
  `TrackTarget { actor_path（"/"区切り相対パス）, component, property }` の文字列 3 点。
  `registry.rs` が (component, property) 文字列を ECS の getter / setter に解決するため、
  **どのコンポーネントの任意プロパティでも**アニメーション対象にできる（Transform 専用機ではない）。
- **f32 成分列への統一**: `AnimValue`（Float / Vec2 / Vec3 / Color / Bool）は `to_components()` で
  f32 列に変換され、Step / Linear / Bezier（エルミート、Catmull-Rom 風自動タンジェント）の補間ロジックは
  型を知らない。値型を増やしても補間コードは触らない。
- **Raw → 型付きの 2 段デシリアライズ**: `.anim`（JSON）はまず Raw 構造体で受けてから型付き変換。
  1 トラックのパース失敗がクリップ全体を巻き込まない。
- **「ボーン = 子アクタ」ソケット機構**: `JointAttachComponent` が
  「モデルの World 行列 × ジョイント行列 × オフセット」を毎フレーム自 Actor に書き込む。
  ボーンを Actor として持たずに「剣を手ボーンに持たせる」等の Unity 的階層アタッチを実現。

---

## 7. プラグインシステム — 薄い共有クレートと DLL 解放順序の設計

`plugin_api/`（独立クレート）＋ `runtime/src/engine/plugin/` ＋ エディタのプロジェクト設定 UI。

**こだわりポイント**

- **`seed-plugin-api` を薄い共有クレートに分離**: プラグイン作者はエンジン全体に依存せず、
  `Plugin` トレイト（name / version / field_defs / on_field_changed）だけ実装して
  `extern "C" fn seed_create_plugin()` をエクスポートすれば DLL が完成する。
- **宣言的インスペクタ UI**: `PluginFieldKind`（Float / Int / String / Bool / Color / FilePath / Enum）を
  JSON で宣言すると、エディタのインスペクタが対応する UI を自動生成する。
- **use-after-free をフィールド順序で防止**: `LoadedPlugin` は `plugin: Box<dyn Plugin>` より**後に**
  `_lib: libloading::Library` を宣言し、Rust の Drop 順序保証で「DLL アンロード前に vtable が消える」事故を防ぐ。
- **マニフェスト駆動**: `plugin.json` ＋ `project_settings.json` の有効化リストを突合し、
  enabled なもののみロード。エディタとランタイムで同一のマニフェスト構造体を維持する運用ルール。

---

## 8. AI 統合 — API ドキュメントを「唯一の正典」として AI に注入する設計

`editor/src/Panels/ScriptEditor/InlineCompletion/` ＋ `editor/src/AI/`。

**こだわりポイント**

- **docs = AI の知識源という一元化**: `docs/scripting_api.md` はスクリプト API の唯一の正典であると同時に、
  **AI 補完のシステムプロンプトへそのまま注入**される（`ScriptApiReference.Load()`）。
  API を追加したら docs を書く → AI が即座にそれを知る、という運用が構造的に成立する。
- **Copilot 風ゴースト補完を自作**: AvalonEdit の `BackgroundRenderers` に `GhostTextRenderer` を挿し、
  `InlineCompletionController` がデバウンス 2 段階（250ms / 600ms、コメント行直後は強トリガ）で
  プロバイダ（`IInlineCompletionProvider`）に問い合わせる。バックエンドは Groq のクラウド推論
  （OpenAI 互換 API をストリーミング利用）。
- **LLM の癖への実戦的対処**: Unity API 混入（`UnityEngine` / `MonoBehaviour`）を検知して破棄する
  `LooksLikeWrongApi`、推論モデルの `<think>` タグ除去、429 レート制限のクールダウン
  （retry-after 尊重＋上限キャップ）、既存コードを覆わない行数へ予測を切り詰める `CapToAvailableLines`。
- **AI アシスタントはマルチプロバイダ**: チャットパネルは `IAIProvider` 抽象の下に
  Anthropic / Gemini / OpenAI 互換 / CLI エージェントを実装し、ローカル LLM
  （llama-server + Qwen2.5-Coder-7B、`LocalLlmManager`）も OpenAI 互換エンドポイントとして差し込める。
  `EditorCommandExecutor` 経由で AI がシーンを直接操作（AiAddActor / AiSetValue 等の専用 IPC コマンド）できる。

---

## 9. スクリプトデバッガ — netcoredbg + DAP を自前実装

`editor/src/Debugger/`。

- **DAP クライアントをフルスクラッチ**: `DapClient` が Content-Length フレーミング・seq 相関・イベント配信の
  プロトコル層のみを担い、`ScriptDebugSession` が netcoredbg（`--interpreter=vscode`）の起動 →
  initialize → attach → setBreakpoints → configurationDone のセッション手順を実装。
- **Roslyn 動的アセンブリ対応**: `justMyCode=false` を必須化（動的アセンブリが Just My Code 判定で
  スキップされる問題への対処）。
- **エディタ統合**: ガターのブレークポイントマージン、行ハイライト、ホバーポップアップ、
  ブレークポイントの永続化（`breakpoints.json`）まで実装済み。

---

## 10. データドリブンと後方互換への執着

- **すべて serde でデータ化**: シーン（`.scene` = JSON の再帰木構造）、アニメーション（`.anim`）、
  地形（`.tvox` / `.tscatter`）、入力マップ、プロジェクト設定 — ゲーム内容はデータ差し替えで構成できる。
- **`#[serde(default)]` の徹底**: シーンの全フィールドに default を付け、旧バージョンのファイルを
  壊さず読めることを最重要ルールとして明文化。`.tvox` も v1 〜 v3 の後方互換読み込みを実装。
- **失敗から学んだプレハブ設計**: プレハブの「ロード時自動再展開」「保存時自動伝播」は
  データ損失バグの温床となったため**撤去**し、現在は明示的なユーザー操作
  （`PREFAB_REAPPLY` / `PREFAB_REAPPLY_ALL`）のみで再展開する。ルートの Transform / name は維持、
  子ツリーはファイル内容で置換、というルールを明確化。Undo はツリースナップショットで 1 操作として記録。

---

## まとめ — このエンジンの「らしさ」

1. **既製品に頼らない自作主義**: ECS、レンダラ、地形、DAP クライアント、ゴースト補完まで自前実装。
2. **実バグから設計を更新する文化**: IPC スレッド分離、プレハブ自動伝播の撤去、DLL 解放順序 —
   コメントに「なぜこうしたか」の経緯が残っている。
3. **型と構造で事故を防ぐ**: 世代カウンタ、LightingPass enum、Keep/Restore の非対称スナップショット。
4. **データドリブンと後方互換**: serde default 徹底、独自バイナリのバージョン互換、docs を AI と共有する正典運用。
