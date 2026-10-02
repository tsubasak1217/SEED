# 描画の構成（render profile）と GPU メモリの計測（2026-10-02）

2D の UI しか使わない目覚ましアプリ Wake or Pay が、Pixel 6a で**前面 GL mtrack 約 723 MB・TOTAL PSS 約 850〜875 MB**、背面でも約 705 MB を
抱えたまま `lowmemorykiller` に止められていた（[backlog.md](backlog.md) の「Wake or Pay の実機確認 2 回目」(2)）。
この文書は、(1) どの GPU 資源がどれだけ占めるかを**測る仕組み**（`renderer/gpu_mem`）と、(2) 3D の描画資源を作らない
**「2D/UI だけ」の描画の構成**（`renderer/render_profile`。`project_settings.json` の `render.profile`）の正典。

**既定は今までどおり**（構成 `full`＝何も止めない）。`render` 節を書かないプロジェクト（WarashibeFishing など）の描画・資源は変わらない（§8）。

## 1. 結論

- **計測で分かった大きいもの**（PC・RTX 3060 Laptop・Vulkan・Wake or Pay を Pixel 6a 相当の 1080x2400・`mobile` 品質で起動。§6）:
  1. **スキニングの compute のパイプライン（`skin_compute.wgsl`）を作るだけでドライバが約 450 MiB を予約**する。
     ノードの TRS・ワールド行列をスレッドごとの `var<private>` 配列（約 10 KB / スレッド）に持つため、ドライバが「同時に走りうる全スレッド分」の
     ローカルメモリを確保する（資源の確保〈wgpu-hal〉は 0 で、ヒープの使用量だけが 193.6 → 644.4 MiB に跳ねる。§5）。→ 直した（§14.2）
  2. **bindless のメガバッファ 224 MiB**（索引 128・UV 64・法線 32 MiB）。RT のヒットシェーディングの土台で、RT の無い GPU（Pixel 6a の Mali-G78）でも確保していた。→ 直した（§14.4）
  3. **Play のピッキングの ID バッファ 39.6 MiB**（画面と同じ 1080x2400 の Rgba32Float）。Play では既定で ID パスを描かないのに確保していた。→ 直した（§14.5）
  4. **シャドウマップ 56 MiB**（CSM 1024²×3 層＋スポット 1024²×4 層 を本体とカメラのプレビューで 2 組）、**GI（DDGI）のアトラス 13 MiB**（RT が無くても確保）。
  5. wgpu の既定の GPU メモリの方針（`MemoryHints::Performance`）は、転送用に 128 MiB からの塊を持ち、32 MiB 未満の資源を 2 の冪に切り上げて切り出す。
- **構成 `ui`**（`"render": { "profile": "ui" }`）は上の 1〜4 を作らず（影・GI は 1x1 の置き場）、レイトレーシング・G-Buffer・後処理を止め、
  GPU メモリを小さな塊で確保する（`MemoryHints::MemoryUsage`）。3D のシーン（モデル・地形・水・天球・SEED.Draw3D）は描かない（あれば 1 回警告）。
- **効果（PC・同じ条件）: プロセスのヒープの使用量 1307.1 MiB → 285.1 MiB（-78%）、追跡した資源 409.3 → 76.2 MiB**。
  残りの大半は PC の NVIDIA のスワップチェイン（約 156 MiB。Android では GL mtrack の外の EGL mtrack）とデバイス自体（23.5 MiB）（§6）。
- **画面は full と画素単位で同じ**（Wake or Pay の一覧・編集・鳴動・庭・アクティビティのグラフ 8 枚 × `mobile`/`desktop` 品質、
  templates/ui のギャラリー・グラフ・画面遷移の見本 16 枚。違う画素 0。§9）。
- **実機（Pixel 6a）の `dumpsys meminfo` での確認は次の実機確認で行う**（APK は作成済み。手順は §7・APK は §9.2）。
- **full の無駄（上の 1〜3）も減らした（2026-10-02 の 2 回目。§14）**: スキニングの compute を「1 ワークグループ = 1 インスタンス・
  ワークグループの共有メモリ」に作り直し（パイプラインの生成での約 444 MiB の予約が消えた。出力は旧い作りとビット単位で同じ）、
  bindless の資源は RT が使える GPU でだけ・最初に要るときに作り、エディタに接続していない Play のピッキングの ID バッファは
  ID パスを描くとき（図鑑のサムネイルの撮影）にだけ作る。Wake or Pay を full（`mobile`）で **1307.1 → 589.1 MiB**、RT を止めた full
  （RT の無い端末と同じ条件）で 1277.2 → 554.9 MiB、WarashibeFishing のプロローグで 1398.3 → 968.0 MiB（`desktop`）・1264.3 → 778.9 MiB（`mobile`）。

## 2. 設定

`project_settings.json`（エディタでは「プロジェクト設定 → グラフィックス → 描画の構成」で選べる＝§2.1。JSON を直接書いてもよい）:

```jsonc
"render": { "profile": "ui" }                 // 2D/UI だけ
"render": { "profile": "ui", "post": true }    // ui を基に、後処理（ブルーム・ビネット・FXAA）だけ戻す
"render": { "profile": "full", "bindless": false }  // full を基に、bindless だけ止める（将来の個別の調整）
```

| キー | 既定 | 意味 |
|---|---|---|
| `render.profile` | `"full"` | 構成の名前（`runtime/config/render_profiles.json` の `name`）。大文字小文字・前後の空白は問わない。知らない名前は警告して既定のまま |
| `render.<旗>` | 構成のとおり | 構成の旗の個別の上書き（§3 の表の旗。真偽か、`memory_hint` は文字列）。読めない値はその旗だけ捨てて警告 |

- 起動時に 1 回読む（Renderer の生成より前）。起動ログ: `[SEED RENDER PROFILE] profile=ui（2D/UI だけ） source=project_settings.json flags: …`、
  止めた機能を描画品質へ足したときは `[SEED RENDER PROFILE] 描画品質へ上限を足しました: …`。読めない値は `[SEED RENDER PROFILE][WARN]`。
- Android は pak に入るので、書き換えたら APK を作り直す。
- **検証用の起動オプション**（設定より優先）: PC は `--render-profile=<名前>[,キー=値,…]`、Android は `am start … --es seed.render_profile <同じ書式>`
  （デバッグ版の MainActivity が `seed.` の extra を起動オプションとして渡す）。名前を書くと構成ごと置き換え（プロジェクトの上書きは捨てる＝素の構成で比べられる）、
  名前を空にして `,キー=値` だけ書くと旗だけを上書きする（例: `--render-profile=full`・`--render-profile=,memory_hint=memory_usage`）。

### 2.1 エディタから設定する（2026-10-02）

「プロジェクト設定 → グラフィックス → 描画の構成」（`editor/src/ProjectSettings/ProjectSettingsWindow.Render.cs`。画面の正典は
[editor_project_settings.md](editor_project_settings.md) §2）。

| 欄 | 選ぶもの | 書くもの |
|---|---|---|
| 構成 | `render_profiles.json` の構成（「3D あり（従来どおり）（full・既定）」「2D/UI だけ（ui）」）。下に選んだ構成の説明 | 既定の構成（`default_profile`＝full）を選ぶと `render.profile` を書かない。ほかは `"profile": "ui"` |
| 旗の上書き（§3 の 8 つ） | 「構成のまま（有効／無効）」・「有効」・「無効」の 3 状態（括弧は選んだ構成の値） | 「構成のまま」は書かない。有効・無効は `"<旗>": true / false` |
| GPU メモリの確保 | 「構成のまま（…）」・`performance`・`memory_usage` | 「構成のまま」は書かない。ほかは `"memory_hint": "…"` |

- 画面の下に「この設定で起動したときの実効」の要約（`scene_3d` が無効なら影・GI・bindless・RT・G-Buffer も無効と出す）、3D を描かない設定のときは
  黄色の注意を出す。反映の時期（次の起動から）と「3D のシーンがあるのに ui を選ぶと描かれない」はいつも出す。
- 何も選ばなければ節ごと書かない。選び直した欄だけを書き、手で書いた知らないキー・読めない値は保つ（黄色で出す。ランタイムは警告して捨てる）。
- 構成の一覧はエディタのビルドで同じ `runtime/config/render_profiles.json` を埋め込んで読む（`RenderProfileCatalog`）。読めなければ組み込みの
  既定の一覧（full / ui）と警告。キー名・旗の一覧・3D に依る旗は `editor/tests/ProjectSystemTests` が `flags.rs` などを読んで突き合わせる。

## 3. 構成と旗

構成の定義は `runtime/config/render_profiles.json`（ビルド時に埋め込み。構成を足す・変えるのは JSON だけ）。

| 構成 | 中身 |
|---|---|
| `full`（3D あり（従来どおり）） | すべて用意する（既定） |
| `ui`（2D/UI だけ） | `scene_3d` `deferred` `shadows` `gi` `bindless` `ray_tracing` `post` `picking` をすべて false・`memory_hint` = `memory_usage` |

| 旗 | false にすると | 効く場所 |
|---|---|---|
| `scene_3d` | 3D のシーンを描かない: モデル（地形のチャンクを含む）・水・天球・地形の散布（草・プロップ）・SEED.Draw3D をフレームの描画から外し、中身があれば種類ごとに 1 回 `[SEED RENDER PROFILE][WARN]`。3D（透視）のメインカメラがあっても警告して 3D は描かない（2D の正射カメラ・スプライト・キャンバスは描く）。スキニングの compute のパイプライン本体を作らない（BindGroupLayout だけ）。品質の上限としてデファード・AO・反射・影・GI（flat）・RT 半透明・水面反射・コースティクスも止める | `frame_renderer.rs`（`all_mcs`・`water_volumes`・天球・Draw3D）・`terrain_scatter_ops.rs`・`pipeline.rs`（`SkinComputePipeline::layouts_only`）・`render_profile/flags.rs`（`quality_caps`） |
| `deferred` | G-Buffer を使わず前方描画（G-Buffer の RT を確保しない。AO・反射・SSGI も止まる） | 描画品質の上限 `deferred=false`（`frame_renderer.rs` の `deferred_setting`） |
| `shadows` | シャドウマップを 1x1 の置き場にし、影を 1 灯も採用しない | `DrawContext::new`（`ShadowResources::new_placeholder`）・品質の上限 `shadows=false` |
| `gi` | GI のアトラスを 1x1 の置き場にし（compute も付けない）、GI は平坦な環境光 | `DrawContext::new`（`GiResources::new_placeholder`）・品質の上限 `gi=flat` |
| `bindless` | bindless の機能をデバイスへ求めず、テクスチャ配列（4096 枠）とメガバッファ（224 MiB）を作らない。true でも RT が使えなければ（GPU が非対応・`ray_tracing=false`）資源は作らず（機能の要求は従来どおり）、使えるときも最初に要るとき（3D モデルの登録・RT のパス）に作る（§14.4） | `Renderer::new`（`supports_bindless`）・`bindless::bindless_resources_wanted`・`bindless_lazy.rs` |
| `ray_tracing` | RT の機能（RAY_QUERY・加速構造）をデバイスへ求めない（RT のパイプライン・TLAS・DDGI の更新・RT 影を作らない） | `Renderer::new`（`supports_rt`）・品質の上限 `translucency=raster` |
| `post` | ブルーム・ビネット・FXAA を止める（トーンマップは残す） | 品質の上限 `bloom` / `vignette` / `fxaa` = false |
| `picking` | エディタに接続していない Play（PC の単体の SEED.exe・パッケージ実行・Android）で、ID パスを描くとき（図鑑のサムネイルの撮影）にもピッキングの ID バッファ（画面と同じ大きさの Rgba32Float）を作らない。true（full）でも単体の Play では起動時には作らず、ID パスを描くときに初めて作る。Edit・エディタの Play（埋め込み・名前付きパイプでつながった別プロセス）・`SEED_ID_PASS_IN_PLAY` では旗によらず起動時から作る（§14.5） | `app/id_buffer_ops.rs`（`App::id_buffer_wanted`・`ensure_id_buffer`）・`app_init.rs`・`event_handler.rs`・`thumbnail_ops.rs` |
| `memory_hint` | `"performance"`（既定）/ `"memory_usage"`。wgpu の `MemoryHints`。gpu-alloc（wgpu 25 の Vulkan）では、performance は転送用の塊を 128〜512 MiB・32 MiB 未満の資源を 2 の冪に切り上げた塊から、memory_usage は転送用 8〜64 MiB・8 MiB 以上の資源を専用に確保する。**文字のグリフアトラスのページの上限も決める**（performance 4 ページ＝64 MiB まで・memory_usage 2 ページ＝32 MiB まで。1 ページ 16 MiB で、字が増えたときだけ足す。`project_settings.json` の `font.atlas_pages` があればそちら。2026-10-03。[ui_components.md](ui_components.md) §12.15） | `Renderer::new`（`DeviceDescriptor::memory_hints`）・`font/field_settings.rs`（`atlas_page_limit`。`FontConfig::canvas` / `default`） |

- 旗は「止める」向きにだけ効く。資源は起動時に確保するので、実行中には変えられない（再起動が要る）。
- **`scene_3d: false` は 3D だけの資源をまとめて止める**: 影・GI・bindless・レイトレーシング・デファードは、個々の旗が true でも作らない
  （`RenderProfileFlags::allocates_shadows` / `allocates_gi` / `allocates_bindless` / `requests_ray_tracing` / `allows_deferred`。品質の上限も同じ規則）。
  `"render": { "scene_3d": false }` だけでも ui とほぼ同じ資源になる（後処理・ピッキング・GPU メモリの方針は別の旗）。
- 毎フレームの可否（デファード・影・後処理・水面・GI などの方式）は**描画品質のつまみ**（[rendering_roadmap.md](rendering_roadmap.md) の「描画品質プリセット」）が
  受け持つので、構成は止めた旗を品質の上限へ重ねるだけにしてある（判定を二重に持たない。`RenderProfileFlags::quality_caps` → `QualityKnobs::overlaid`）。
- **描画スケールは変えない**（`mobile` の 0.75 はそのまま）。変えると画面の見た目が変わるため。
- 深い所（`Renderer::new`・`DrawContext::new`・`DrawPipelines::new`）は起動時に登録した「今の構成」を `render_profile::active_flags()` で読む
  （`shadow_settings` と同じ流儀。起動後は変わらない）。

## 4. GPU メモリの計測（`renderer/gpu_mem`。デバッグ用・既定オフ）

### 4.1 有効にする

| 環境 | 指定 |
|---|---|
| PC | 環境変数 `SEED_GPU_MEM_LOG=1`、または起動引数 `--gpu-mem-log` |
| PC（MCP） | `seed_launch(gpu_mem_log:true)`（エディタへ `SEED_GPU_MEM_LOG=1` を渡し、エディタが起動する Edit・Play のランタイムが受け継ぐ。2026-10-02） |
| Android | `am start … --es seed.gpu_mem_log 1`（デバッグ版の APK） |

無効（既定）なら資源の作成の記録もしない（`*_tracked` は旗を 1 つ読んで wgpu を呼ぶだけ）。
MCP からは `seed_gpu_mem_report(top?)` で内訳（下の IPC `GPU_MEM_REPORT`）を取り、要約表（合計・分類ごと・上位）＋完全な JSON で受け取れる
（docs/editor_mcp.md §5.6 の C）。

### 4.2 何が出るか

`[SEED GPU MEM]` で始まる行を、次のときに出す。

- **起動後**: on_demand のアプリは最初に描画が待機へ入ったとき、描き続けるアプリは 120 フレーム目（`AUTO_REPORT_FRAME`）に 1 回。
- **背面へ回るとき**（Android の suspended。背面に残る資源の確認。サーフェスを捨てた後の量も「節目」の 1 行で出す）。
- **IPC `GPU_MEM_REPORT`**（一時フォルダへ）/ **`GPU_MEM_REPORT:<パス>`**: ログへ出し、JSON を書いて `GPU_MEM_REPORT_DONE:<パス>` を返す
  （計測が無効なら `GPU_MEM_REPORT_ERROR:<理由>`）。
- **起動の節目**（`[SEED GPU MEM] 節目 …`）: デバイスを作った直後・パイプラインキャッシュ・スワップチェイン・`Renderer::new` の後・
  `DrawPipelines::new` の後・影〜bindless の後・`DrawContext::new` の後・文字と図形の描画器の後・シーンを読んだ後。どこで実際の確保が増えたかを切り分ける。

1 回の内訳に並べるもの:

| 行 | 中身 |
|---|---|
| 追跡した資源（生存の推定） | エンジンが作ったテクスチャ・バッファの論理的な大きさ（形式・寸法・ミップ・層から計算）の合計と件数 |
| wgpu-hal の確保ブロック | wgpu-hal が資源ごとに確保したブロックの合計（gpu-alloc の 2 の冪への切り上げを含む）と、GPU メモリの確保（vkAllocateMemory）の生きている回数。Cargo の wgpu の `counters` 機能 |
| 実際の確保 | VK_EXT_memory_budget の heapUsage（このプロセスがヒープで使っている量。ヒープごと）。アロケータのまとめ取り・スワップチェイン・ドライバの内部（シェーダのローカルメモリなど）まで含む。Android の GL mtrack に当たる |
| スワップチェイン（見積り・別枠） | 幅 × 高さ × 画素のバイト数 × （フレーム遅延 + 1）枚 |
| 分類ごと | 深度・Hi-Z / シーンの色の中間（HDR・LDR）/ G-Buffer / 影 / GI / クラスタ / ライト / 後処理 / AO / 反射 / 半透明 / 水 / パーティクル / 天球 / 地形 / bindless / レイトレーシング / 3D モデル / スプライト / UI の文字 / UI の図形 / ピッキング / エディタ / 撮影 / 計測 / その他 |
| 上位 20 件 | 大きい順。ラベル・寸法・形式・作った場所（ソースのファイル:行） |

### 4.3 仕組みと限界

- エンジンの GPU 資源の生成（170 か所）は `GpuMemDeviceExt` の `create_texture_tracked` / `create_buffer_tracked` / `create_buffer_init_tracked` /
  `create_texture_with_data_tracked` を通す（**新しく資源を作るコードもこれを使う**。素の `create_texture` は計測から漏れる）。
  分類は呼び出し元の場所（`#[track_caller]`）とラベルを、`gpu_mem/category.rs` の規則の表（データ）に照らして決める。
- wgpu は資源の破棄を外へ知らせないので、**生存は推定**する: 「同じ場所・同じラベルで後の世代に作り直したら前のものは捨てた」（世代はフレームの始めと
  窓の大きさの変化で進む）。資産ごとに作る場所（スプライトの画像・モデル・スキン・パーティクルの形）は積み増しとして数える（**解放は追えない＝作った累計**。
  シーンを切り替えるゲームでは多めに出る）。
- 大きさは論理値。実際の確保は「実際の確保」の行で見る（追跡との差が大きいときは「節目」の行でどこで増えたかを見る）。

### 4.4 PC で端末の画面の大きさにする

環境変数 `SEED_SIM_WINDOW_SIZE=1080x2400`（Play の窓の最初の大きさ。`SEED_SIM_SCALE_FACTOR=2.625`・`SEED_SIM_SAFE_AREA=0,132,0,63` と組み合わせると
Pixel 6a に近い）。窓が画面より大きいと OS が縮めることがある（実際の大きさは起動ログ・スクリーンショットの寸法で確かめる。1920x1080 の画面で 1080x2400 の窓になった）。

エディタからは、実行先セレクタ（実行ボタンの隣）で「**PC（端末の模擬: Pixel 6a 実寸）**」を選んで実行するだけで、これらの環境変数と `--render-quality=mobile` を
付けた別ウィンドウの Play になる（端末は `editor/config/device_presets.json` のデータ。正典 [editor_device_presets.md](editor_device_presets.md)）。

## 5. スキニングの compute がローカルメモリを予約する件

**→ 2026-10-02 の 2 回目で直した（§14.2）。full でもこの予約は起きない（RTX 3060 Laptop で `DrawPipelines::new` の節目 826.9 → 382.6 MiB）。以下は直す前の記録。**

`DrawPipelines::new` の中で、パイプラインの種類ごとにヒープの使用量を測った（PC。RT を求めない ui の構成で、まだスキニングのパイプラインを作っていた途中のビルドに一時的に節目を入れて測った）。`SkinComputePipeline::new` の直後だけが
**193.6 → 644.4 MiB（+450.8 MiB）**で、ほかの 30 余りの種類（メッシュ・G-Buffer・デファード・反射・AO・SSGI・水…）は合わせて +10 MiB ほどだった。
wgpu-hal の資源の確保は 0（確保の回数も増えない）。`skin_compute.wgsl` は `node_t` / `node_r` / `node_s` / `alt_t` / `alt_r` / `alt_s`（各 64 × 16 B）と
`world`（64 × 64 B）をスレッドごとの `var<private>` に持ち（約 10 KB / スレッド）、ドライバはパイプラインの生成で「同時に走りうる全スレッド分」の
ローカルメモリを確保する（RTX 3060 Laptop は 30 SM × 1536 スレッド ≒ 4.6 万スレッド × 10 KB ≒ 450 MB と符合する。推論）。

- `ui` は 3D を描かないのでパイプライン本体を作らない（`SkinComputePipeline::layouts_only`。スキンメッシュの BindGroup のための BindGroupLayout だけ）。
- **`full` は従来どおり作る**ので、3D の作品（WarashibeFishing）でも同じ予約がある。Mali（Pixel 6a）でも同じ仕組み（スレッドローカルの記憶域）で
  大きく取られる見込み（未計測・推論）。直す案（private 配列を workgroup の共有メモリか storage の作業領域へ移す・スキンメッシュが現れたときに作る）は
  [backlog.md](backlog.md)。

## 6. Wake or Pay の内訳（PC で計測。2026-10-02）

**条件**: RTX 3060 Laptop（Vulkan・ドライバ 616.92）・debug の SEED.exe・Wake or Pay の一覧の画面が落ち着いた後（起動から約 3 秒・スクリーンショット 1 枚の後）。
窓 1080x2400（`SEED_SIM_WINDOW_SIZE`）・`SEED_SIM_SCALE_FACTOR=2.625`・`SEED_SIM_SAFE_AREA=0,132,0,63`・`--render-quality=mobile`（Android の既定と同じ。
3D の描画解像度 810x1800）。`full` は `--render-profile=full`、`ui` は設定どおり。スクリプト: `tmp/gpu_mem/measure.py`（作業フォルダ）。
（窓・倍率・安全領域・品質の組は、エディタの実行先「PC（端末の模擬: Pixel 6a 実寸）」と同じ。[editor_device_presets.md](editor_device_presets.md)）

| | full | ui |
|---|---|---|
| **実際の確保（VK_EXT_memory_budget の heapUsage の合計）** | **1307.1 MiB** | **285.1 MiB** |
| 　DEVICE_LOCAL ヒープ | 1088.5 MiB | 209.1 MiB |
| 　HOST ヒープ | 218.5 MiB | 76.0 MiB |
| wgpu-hal の確保ブロック（確保の回数） | 462.0 MiB（13 回） | 71.5 MiB（7 回） |
| 追跡した資源（テクスチャ / バッファ・件数） | 409.3 MiB（171.1 / 238.1・88 件） | 76.2 MiB（62.3 / 13.8・79 件） |
| Windows の GPU Process Memory（専用 / 共有 / 確定） | 1075.2 / 266.9 / 1332.1 MiB | 185.0 / 124.4 / 309.5 MiB |

分類ごと（追跡した資源）:

| 分類 | full | ui | 主なもの |
|---|---|---|---|
| bindless | 224.3 MiB | 0 | 索引 128・UV 64・法線 32 MiB のメガバッファ |
| 影 | 56.0 MiB | 0（1x1 の置き場） | CSM 1024²×3 層 12 MiB・スポット 1024²×4 層 16 MiB × 2 組（本体・カメラのプレビュー） |
| ピッキング（ID） | 39.6 MiB | 0 | 1080x2400 Rgba32Float |
| シーンの色の中間 | 30.9 MiB | 30.9 MiB | post_ldr 1080x2400 Rgba16Float 19.8・scene_hdr 810x1800 Rgba16Float 11.1 |
| UI の文字 | 16.3 MiB | 16.3 MiB | グリフのアトラス 4096x4096 R8 16.0 |
| 深度 | 15.4 MiB | 15.4 MiB | UI 用 1080x2400 9.9・3D 用 810x1800 5.6（Depth24PlusStencil8） |
| GI | 13.3 MiB | 0（1x1 の置き場） | 放射輝度・可視性のアトラスと履歴 |
| 撮影 | 10.0 MiB | 10.0 MiB | スクリーンショットの読み戻し（計測の手順で撮った 1 枚。普段は無い） |
| クラスタ | 3.4 MiB | 3.4 MiB | ライトの索引 |
| そのほか | 0.2 MiB 未満 | 0.2 MiB 未満 | ライト・スプライト・天球の球・3D モデルの既定の資源など |

起動の節目（ヒープの使用量。ui）: デバイスを作った直後 23.5 → スワップチェインを作った後 179.9 → `Renderer::new` の後 190.1 →
`DrawPipelines::new` の後 206.6 → 文字・図形の描画器の後 222.6 MiB → 一覧が落ち着いた後 285.1 MiB。
full は デバイスを作った直後 44.9（RT・bindless の機能を求める分）→ スワップチェインの後 201.4 → `Renderer::new` の後 233.4 → **`DrawPipelines::new` の後 826.9（+593.5。スキニングの compute の約 450 と、RT のパイプラインなど。§5）** → 影〜bindless の後 1146.9 → 描画器の後 1227.1 → 一覧が落ち着いた後 1307.1 MiB。

- **PC のスワップチェインは 156.4 MiB**（1080x2400 の 3 枚なら論理値は 29.7 MiB。NVIDIA の Windows の Vulkan の提示の仕組みの分と見る〈推論〉）。
  Android ではスワップチェインは GL mtrack ではなく EGL mtrack（実機の前面で 51.8 MB・5 枚相当）に出る。
- HOST ヒープの 76 MiB は転送用の塊（memory_usage は 8→16→32→64 MiB と倍々に取り、最後の塊を手放さない）とスクリーンショットの読み戻しの見込み（未分解）。
- `desktop` 品質（PC の既定）でも同じく ui は 3D の資源を作らない（full はデファードの G-Buffer などがさらに乗る）。数値は §6.1。

### 6.1 `desktop` 品質（PC の既定）

`--render-quality` なし（3D の描画解像度は 1080x2400・デファード・影 2048）。ほかの条件は §6 と同じ。

| | full | ui |
|---|---|---|
| **実際の確保（heapUsage の合計）** | **1499.1 MiB** | **277.2 MiB** |
| 　DEVICE_LOCAL / HOST | 1280.5 / 218.5 MiB | 201.2 / 76.0 MiB |
| wgpu-hal の確保ブロック（確保の回数） | 646.0 MiB（16 回） | 71.6 MiB（6 回） |
| 追跡した資源（テクスチャ / バッファ・件数） | 553.6 MiB（315.5 / 238.1・92 件） | 79.3 MiB（65.4 / 13.8・78 件） |
| Windows の GPU Process Memory（専用 / 共有 / 確定） | 1257.4 / 266.9 / 1524.4 MiB | 177.0 / 125.0 / 302.1 MiB |
| 分類の差 | bindless 224.3・影 128.0（CSM 2048）・G-Buffer 69.2・ピッキング 39.6・GI 13.3 | いずれも 0（シーンの色の中間 39.6・文字 16.3・深度 9.9・撮影 10.0・クラスタ 3.4 は同じ） |

`desktop` では full がデファードなので G-Buffer（5 枚 69.2 MiB）が乗り、影も 2048（Wake or Pay の `shadow.resolution`）になる。ui はどちらの品質でもほぼ同じ量になる
（描画スケールが等倍の `desktop` は UI 用の深度が要らない分だけ少し小さい）。

### 6.2 実機で期待する値（推論）

実機の前面の GL mtrack 723 MB は、追跡した資源（`full`・`mobile` の PC と同じ資源の構成なら約 400 MiB）＋アロケータの切り上げとまとめ取り
＋スキニングの compute のローカルメモリ（Mali の分は未計測）の和と見る。`ui` では資源が約 66 MiB（撮影の 10 MiB を除く）・切り上げは
memory_usage で小さく・スキニングの予約が無くなるので、**GL mtrack は 100〜150 MB 程度**になる見込み（推論。次の実機確認で `dumpsys meminfo` と
`[SEED GPU MEM]` で確かめる。§7.2）。

## 7. Android での確かめ方

### 7.1 手順（実機・次の実機確認で）

1. APK（`ui` の設定入り）を入れる（作成済み: `C:\Users\k023g\.claude\jobs\434062fd\tmp\w3_device\wakeorpay-uiprofile.apk`）。
2. 計測つきで起動: `adb shell am start -n com.wakeorpay.seed/com.seedengine.runtime.MainActivity --es seed.gpu_mem_log 1`
   （比べるときは `--es seed.render_profile full` を足すと素の full で起動する）。
3. logcat の `[SEED GPU MEM]`（起動後・節目・背面へ回るとき）と、`adb shell dumpsys meminfo com.wakeorpay.seed` の GL mtrack / EGL mtrack / TOTAL PSS を
   前面（一覧）と背面（ホームへ回して 1 分後）で記録する。
4. 背面のまま 30 分以上置いて `lowmemorykiller` に止められないか（以前は約 25 分で止められた）。

### 7.2 見るところ

- `[SEED GPU MEM] 実際の確保（VK_EXT_memory_budget …）` と `dumpsys meminfo` の GL mtrack が近いか（Mali の heapUsage の数え方の確認）。
- `[SEED GPU MEM] 節目 DrawPipelines::new の後` で、`full` のときに Mali でもスキニングの compute の予約が出るか（§5）。
- 起動ログに `[SEED RENDER PROFILE] profile=ui …` と `[SEED BINDLESS] 非対応（描画の構成が止めている …）` が出ること。

## 8. 既定（full）が変わらないこと

（この節は ui の構成を足したとき〈2026-10-02 の 1 回目〉の記録。2 回目で full の GPU メモリそのものを減らした〈描画と振る舞いは同じ〉＝§14）

- `render` 節が無ければ構成は `full`（旗はすべて true・`memory_hint` = performance＝wgpu の既定）。品質へ上限を足さない（`quality_caps` が空）。
  デバイスへ求める機能・限界・`MemoryHints`、`DrawContext` の資源（影・GI の大きさ）、スキニングのパイプライン、ID バッファの作り直しは従来と同じ。
- 計測（`*_tracked`）は無効なら記録しない。wgpu の `counters` 機能は資源の生成・破棄で原子的な足し引きをするだけ。
- 単体テスト（`render_profile`・`gpu_mem`）で「節が無い・壊れた JSON は full」「full は品質に何も当てない」を固定し、実 GPU のテスト
  （`render_profile::tests_gpu`。`--ignored`）で「full の影は設定どおりの解像度・スキニングのパイプラインあり」を確かめた。
- WarashibeFishing（`full`）の図鑑は画面全体、会話は本文の範囲で、変更の前と画素単位で同じ（会話の枠の縁に透ける動く背景は、変更前どうしでも出る揺れの範囲。§9.1）。

## 9. 確認（2026-10-02）

| 確かめたこと | 結果 |
|---|---|
| `cargo build`（runtime・debug。develop も同じ） | 0 エラー・新しい警告なし（lib の警告 311 件はすべて既存の行。足した行・新しいファイルに付いた警告は 0 件） |
| 単体テスト（`render_profile`・`gpu_mem`・`launch_options`・`simulated`・`render_quality`・`skin_system`） | 67 件すべて通過（設定の読み取り・節が無ければ full・full は品質に何も当てない・`scene_3d=false` で 3D の資源がまとめて止まる・起動オプション・窓の大きさ・大きさの計算・分類・生存の推定・内訳） |
| 実 GPU のテスト `render_profile::tests_gpu`（`--ignored`） | 通過。ui: 影のテクスチャ 4 件・最大 16 B／GI 最大 8 B／bindless 0 件／スキニングのパイプライン本体なし／RT の資源なし。full: 影 最大 50,331,648 B（CSM 2048²×3 層）・GI 5,308,416 B・スキニングのパイプラインあり（従来どおり） |
| 実 GPU のテスト `skin_system::multi_anim_blend_runs_on_gpu`（`--ignored`。`SkinComputePipeline::new` を使う既存のテスト） | 通過 |
| GPU メモリの内訳（§6・§6.1） | full → ui でヒープの使用量 1307.1 → 285.1 MiB（`mobile`）・1499.1 → 277.2 MiB（`desktop`） |
| Wake or Pay の画面の一致（full と ui を同時に起動し同じ操作。一覧〈空・アラームあり・森のテーマ〉・編集・鳴動〈`wop,alarm,ring,snooze`〉・庭・アクティビティのグラフ・一覧へ戻った後の 8 枚） | `mobile` 品質・`desktop` 品質のどちらも違う画素 0（最終のビルドでも `mobile` で 0） |
| templates/ui（ギャラリー〈暗い・明るいテーマで 600 dp ずつ 5 段＋ダイアログ〉・グラフの見本・画面遷移の見本の 16 枚） | 違う画素 0 |
| Wake or Pay の PC の確かめ（W3-1〜W3-7 の写し 14 本。設定どおり ui で起動） | すべて通過（w37 27/27・w37_411 18/18・w37_dense 11/11・w36 31/31・w36_411 4/4・w35b 55/55・w35b_411 3/3・a 21/21・b 20/20・snooze 7/7・w33 34/34・w34 43/43・w4 35/35・w4_411 35/35。27 回の起動すべてで `profile=ui`・警告なし） |
| WarashibeFishing（full・render 節なし）の図鑑・会話 | §9.1。図鑑は画面全体で違う画素 0、会話は本文の範囲で 0（枠の縁の背景の透けは変更前どうしの揺れの範囲） |
| Android の APK（ui の設定入り） | §9.2 |

### 9.1 WarashibeFishing（既定の full）の回帰

tmp/w2_6/regress と同じやり方（作業フォルダの `wf_regress.py`）。WarashibeFishing の複製（`tmp/w2_1a/wf`）を、変更前と変更後の SEED.exe で起動し、
`SEED_SCREENSHOT_FRAMES=90,180,300` で同じフレームを撮って比べた。変更前は変更を入れる前にあった `runtime/target/develop/SEED.exe`（2026-10-02 02:16 のビルド。
HEAD 14c3527a と同じ runtime のコード）の写し、変更後は同じ develop の構成でビルドした SEED.exe（最適化の違いで時間で動く絵の進み方が変わらないよう揃えた）。

| 場面 | 比べ方 | 結果 |
|---|---|---|
| 図鑑（zukan。決定的に描ける） | 画面全体 1280x720 | 3 フレームとも違う画素 0 |
| 会話（proLogue）の本文（370〜900 × 580〜615） | 画素ごと | 3 回 × 3 フレームとも違う画素 0 |
| 会話の枠（280〜1000 × 505〜685。3D の背景が縁に透けて動く） | 変更前 5 回で動いた画素を除いた残りの差 | 変更後 3 回: 4〜7 / 2〜4 / 42〜46 画素（差の最大 1〜2）。変更前どうし（4 回で 1 回を比べる）でも 0〜16 / 1〜6 / 0〜75 画素（最大 5）出る＝揺れの範囲 |

（debug の変更後と develop の変更前を比べたときは、フレーム 300 の枠の縁で 107〜112 画素〈最大 22〉の差が出た。debug は 1 フレームが遅く、時間で動く背景の進み方が
ずれるため。上の表の develop どうしではその差は消えた。）

### 9.2 Android の APK（ui の設定入り・開発用）

`dotnet run --project editor/tools/SeedAndroid -- build --project D:\SEED_projects\WakeOrPay --abi arm64-v8a`（2026-10-02 05:18〜05:24）:
libSEED.so のビルド 219.1 秒（Develop・arm64-v8a）・pak とスクリプト 16.4 秒（開発用のビルドの印あり）・同梱 .NET は変更なしで飛ばし・Gradle 102.3 秒、`exit=0`。
APK の pak（`assets/seed/assets.pak`）の `project_settings.json` に `"render": { "profile": "ui" }` が入っていることを確かめ、
`C:\Users\k023g\.claude\jobs\434062fd\tmp\w3_device\wakeorpay-uiprofile.apk`（73,789,035 バイト・SHA-256 `c419759457f32bf15189b05101fbf7447de7864ba5000058a397c8d137ef6578`）へ写した。
**実機には入れていない**（次の実機確認で §7 の手順で比べる）。

## 10. ui で動かないもの・残した制限

| 項目 | 内容 |
|---|---|
| 3D のシーン | モデル・地形・水・天球・散布・草・SEED.Draw3D は描かない（警告 1 回）。**3D のパーティクル（ParticleEmitter）と LineRenderer は止めていない**（2D の粒子と同じ仕組みで、3D だけを分けていない） |
| 3D の資源の読み込み | ModelComponent の GPU への読み込み・地形のメッシュの組み立ては止めていない（描かないだけ。メモリは使う） |
| パイプライン | 3D のパイプライン（メッシュ・G-Buffer・デファード・反射・AO・水…）は従来どおり作る（スキニングの compute の本体だけ作らない）。起動の時間は変わらない（実機の DrawContext 約 1 秒） |
| 残る大きい資源 | post_ldr（画面と同じ大きさの Rgba16Float。1080x2400 で 19.8 MiB。UI を重ねる先）・グリフのアトラス 16 MiB・深度 2 枚 15.4 MiB・scene_hdr 11.1 MiB・クラスタ 3.4 MiB |
| スワップチェイン | 形式（Rgba8UnormSrgb / Bgra8UnormSrgb）・枚数（フレーム遅延 2 → 3 枚を要求）は変えていない（§11） |
| エディタ | プロジェクト設定の画面で選べる（2026-10-02。§2.1）。エディタのシーンビュー（Edit）も同じ構成で描くので、ui のプロジェクトでは 3D は出ない（警告のログが出る）。保存しても、使い回している Edit・Play のランタイムには次の起動まで効かない |
| シェーディングアセット | デファード専用なので ui では効かない（`[SEED QUALITY][WARN]` が出る） |
| モデル／アクタのサムネイル | `scene_3d=false` では撮らない（2026-10-03）。プロジェクトパネルのモデルのサムネイル（`THUMBNAIL:`）と図鑑の `RENDER_ACTOR_THUMBNAIL` は撮影へ進まず、すぐに「この描画の構成（…scene_3d=false…）では撮影できません」で断る（`thumbnail_ops.rs` の `begin_thumbnail_job`。以前は撮影まで進み、被写体の写らない絵を成功として書くか、誤った理由で失敗していた） |

## 11. スワップチェインの見直し（評価）

- 形式: 4 バイト / 画素の sRGB（Android は Rgba8UnormSrgb）。これより小さい形式（16 bit など）は色の段差が出るので変えない。
- 枚数: wgpu の Vulkan は「要求したフレーム遅延 + 1」を minImageCount に渡す（既定 2 → 3 枚）。端末の minImageCount が 3 以上なら 1 にしても減らない。
  Android のスワップチェインは GL mtrack ではなく EGL mtrack（実機で約 52 MB）に入り、今回の停止の主因（GL mtrack 705〜723 MB）ではない。
  遅延を 1 にするとフレームの間隔が乱れる恐れがあるので、今回は変えない（必要なら旗を足して実機で測ってから）。
- UI を post_ldr（Rgba16Float）ではなくスワップチェインへ直接重ねれば 19.8 MiB 減るが、合成の精度（8 bit の sRGB で毎回丸める）が変わり full と画素が一致しなくなる。

## 12. 背面での解放（評価・未実装）

`ui` で背面へ回ったときに解放できるもの（`platform.paused` / suspended で捨て、`resumed` の次のフレームで作り直す）:

| 資源 | 大きさ（1080x2400・mobile） | 作り直し |
|---|---|---|
| post_ldr・scene_hdr（RtPool） | 30.9 MiB | 次のフレームの `ensure` が自動で作る（`RtPool` に捨てる口を足すだけ） |
| 深度（3D 用・UI 用） | 15.4 MiB | `Renderer` の深度を Option にするか、1x1 へ作り直す（サーフェスの作り直しと同じ所） |
| グリフのアトラス | 16 MiB | 捨てると全部の文字を焼き直す（前面へ戻った最初のフレームが重い）。捨てない方がよい |

見積り: **約 46 MiB**（追跡した資源の約 70%）。memory_usage では 8 MiB 以上の資源は専用の確保なので、捨てればドライバへ返る見込み（推論）。
背面の GL mtrack が ui で 100 MB 前後まで下がれば、Android が背面のプロセスを止める優先度（oom_score）への効きは小さい。
**次の実機確認で背面の値を見てから**、要れば実装する（[backlog.md](backlog.md)）。

## 13. 実装の地図

| 置き場 | 役割 |
|---|---|
| `runtime/config/render_profiles.json` | 構成の定義（データ） |
| `runtime/src/engine/core/renderer/render_profile/` | 旗（`flags.rs`）・定義の読み込み（`catalog.rs`）・実効の構成の決定（`resolve.rs`）・今の構成の登録（`mod.rs`）・実 GPU のテスト（`tests_gpu.rs`） |
| `runtime/src/engine/core/app_base/app/render_profile_ops.rs` | 起動時の決定・品質へ上限・3D を描かなかったことの警告 |
| `runtime/src/engine/core/app_base/app/id_buffer_ops.rs` | ピッキングの ID バッファの持ち方（`decide_id_buffer_policy`: 起動時から／ID パスを描くときに作る／作らない）・`ensure_id_buffer`（§14.5） |
| `runtime/src/engine/core/renderer/gpu_mem/` | 計測（`track.rs` の `GpuMemDeviceExt`・`category.rs` の分類の表・`size.rs`・`registry.rs`・`report.rs`・`heap_budget.rs`） |
| `runtime/src/engine/core/app_base/app/gpu_mem_ops.rs` | 計測のきっかけ（フレーム・待機・背面・IPC `GPU_MEM_REPORT`） |
| `renderer/mod.rs` | `Renderer::new`（RT・bindless〈機能の要求。資源は RT が使えるときだけ＝§14.4〉・MemoryHints の構成・計測の節目）・`gpu_memory_heaps` / `gpu_hal_memory` / `swapchain_estimate` |
| `renderer/bindless_lazy.rs` | `LazyBindless`（bindless の資源を最初に要るときに作る。§14.4） |
| `methods/drawer/mod.rs` | `DrawContext::new`（影・GI の置き場・bindless の置き場〈`LazyBindless`〉）・`finalize_bindless`（最初のモデルの登録で bindless を作る） |
| `renderer/pipeline.rs`・`skin_system.rs` | `SkinComputePipeline::layouts_only`・本体が無ければディスパッチしない・1 ワークグループ = 1 インスタンスのディスパッチ（`skin_dispatch_grid`）・ノード数の上限 `MAX_NODES`（§14.2） |
| `renderer/shaders/skin_compute.wgsl` | スキニングの compute（1 レーン = 1 ノード・ワークグループの共有メモリ。§14.2） |
| `renderer/skin_compute_equivalence/` | 旧いスキニングの compute の写し（`legacy_skin_compute.wgsl`）との突き合わせ（実 GPU・`--ignored`。§14.2） |
| `renderer/shadow.rs`・`ddgi/resources.rs` | `ShadowResources::new_placeholder`・`GiResources::new_placeholder` |
| `app/frame_renderer.rs`・`terrain_scatter_ops.rs` | 3D のシーンを描かない（`scene_3d`） |
| `platform/launch_options.rs`・`android/native/src/launch.rs`・`main.rs` | `seed.gpu_mem_log` / `seed.render_profile`・`--gpu-mem-log` / `--render-profile=` |
| `platform/screen/simulated.rs` | `SEED_SIM_WINDOW_SIZE` |
| `runtime/Cargo.toml` | wgpu の `counters` 機能・`ash`（VK_EXT_memory_budget の問い合わせ） |
| `editor/src/ProjectSettings/RenderProfile*.cs`・`ProjectSettingsWindow.Render.cs` | エディタのプロジェクト設定「描画の構成」（`render` 節の型・旗の表・構成の一覧の埋め込み・画面。§2.1） |
| `core/clock/fixed_frame_dt.rs` | 検証用の `SEED_FIXED_FRAME_DT`（フレームの経過時間を固定し、同じフレーム番号のゲームの時刻を実行に依らず揃える。§14.6） |

## 14. full の GPU メモリの無駄の削減（2026-10-02 の 2 回目）

§1 の 1〜3（スキニングの compute の予約・RT の無い GPU の bindless・単体の Play のピッキングの ID バッファ）を、`full`（3D の作品。既定）の
見た目と振る舞いを変えずに減らした。`ui` の構成の資源は変わらない（同じ条件でヒープの使用量 285.1 → 285.1 MiB）。

### 14.1 効果（PC・RTX 3060 Laptop・Vulkan・ドライバ 616.92・debug の SEED.exe）

**Wake or Pay を `--render-profile=full` で**（2D/UI だけの作品を full で起動。§6 と同じ条件: 1080x2400・2.625 倍・安全領域・一覧が落ち着いた後）。
単位は MiB（ヒープの使用量＝VK_EXT_memory_budget の heapUsage の合計）。「スキニングだけ」はスキニングの compute だけを直した途中のビルド。

| 条件 | 変更前 | スキニングだけ | 3 つとも | 差 |
|---|---|---|---|---|
| full・`mobile` | 1307.1 | 864.7 | **589.1** | −718.0 |
| full・`desktop` | 1499.1 | 1075.7 | **1026.1** | −472.9 |
| full・RT を止める（`--render-profile=full,ray_tracing=false`。RT の無い端末と同じ条件）・`mobile` | 1277.2 | — | **554.9** | −722.2 |
| full・RT を止める・`desktop` | 1471.2 | — | **747.4** | −723.8 |
| ui・`mobile`（参考。変えていない） | 285.1 | 285.1 | 285.1 | 0 |

| 内訳（full・`mobile`） | 変更前 | 変更後 |
|---|---|---|
| `DrawPipelines::new` の後の節目 | 826.9 | 382.6（−444.3。RT を止めると 797.1 → 348.4） |
| 追跡した資源 | 409.3 | 145.5 |
| 　bindless | 224.3 | 0（`mobile` 品質では RT のパスが走らず、最初に要るときが来ない） |
| 　ピッキング（ID） | 39.6 | 0 |
| wgpu-hal の確保ブロック（確保の回数） | 462.0（13 回） | 197.7（8 回） |
| Windows の GPU Process Memory（専用 / 確定） | 1075.2 / 1332.1 | 366.6 / 614.0 |

- `desktop` 品質では、RT の半透明（屈折）のための TLAS の組み立てが 3D の中身が無くても毎フレーム走り、そこで bindless を作る
  （`[SEED BINDLESS] 資源を作りました（最初に要った所 …/frame_renderer.rs:6076…）`）。要る所が走ったので作るのは正しい振る舞いで、
  224.3 MiB は残る（§14.7）。
- RT を止めた full は bindless の資源を作らない（`[SEED BINDLESS] レイトレーシングが使えないため、資源（…224 MiB）は作りません…`）。

**WarashibeFishing（3D の作品・既定の full）**: 複製のプロローグを単体の Play で（1280x720・起動から 12 秒後）。

| 条件 | 変更前 | 変更後 | 差 | 内訳 |
|---|---|---|---|---|
| `desktop` | 1398.3 | **968.0** | −430.3 | `DrawPipelines::new` の節目 724.3 → 279.9・ID 14.1 → 0・bindless 224.3 → 224.3（最初のモデルの登録で作る） |
| `mobile` | 1264.3 | **778.9** | −485.4 | 同上 |
| RT を止める・`mobile`（RT の無い端末と同じ条件） | 1234.4 | **518.8** | −715.6 | `DrawPipelines::new` の節目 694.4 → 245.8・bindless 224.3 → 0・ID 14.1 → 0・追跡した資源 446.5 → 208.2 |

RT の使える PC では、最初のモデルの登録（`drawer/mod.rs` の `finalize_bindless`）で bindless を作る（RT のヒットシェーディングが読むので要る）。

### 14.2 スキニングの compute（`skin_compute.wgsl`）

**旧い作り**: 1 スレッド = 1 インスタンス（64 スレッドのワークグループ）。スレッドがノードの TRS（`node_t/r/s`）・フェード元の TRS（`alt_t/r/s`）・
ワールド行列（`world`）を `var<private>` 配列に持つ（64 ノード × (16 B × 6 + 64 B) ≒ 10 KB / スレッド）。

**新しい作り**: **1 ワークグループ = 1 インスタンス、1 レーン = 1 ノード**（`@workgroup_size(MAX_NODES)`＝64）。

1. 各レーンが自分のノードの TRS を求める（バインドポーズから始め、そのアニメのチャンネルのうち自分のノードを対象とするものだけを連結列の順に上書き。
   フェード中は A も同じく求めてノードごとに混ぜる）。結果をワークグループの共有メモリ（`wg_t/r/s`）へ置く。
2. `workgroupBarrier` の後、レーン 0 が BFS 順に各ノードのローカル行列を作り、親のワールド行列に掛けて `wg_world` へ置く（旧い作りと同じ式の並び）。
3. `workgroupBarrier` の後、全レーンがジョイント行列（`world[node] * ibm`。余りの枠は単位行列）を 2 枠ずつ書く。

共有メモリは TRS 3 KB ＋ 行列 4 KB ＝ 7 KB / ワークグループ（wgpu の既定の上限 16 KB の内なので、デバイスへ求める上限は変えていない）。
スレッドごとの配列は持たない（`skin_system::tests::skin_compute_wgsl_is_valid` が「private の大域変数が無い・共有メモリがある」ことを naga で確かめる）。
ディスパッチはインスタンス数ぶんのワークグループを x 方向へ並べ、1 次元の上限 65,535 を超える分は y 方向の行へ折り返す（`skin_dispatch_grid`。単体テストあり）。

**結果が変わらない根拠**:

- ノードの TRS: 旧い作りは「全ノードの配列をバインドポーズで初期化し、チャンネルを連結列の順に対象ノードへ書く」。新しい作りの各レーンは同じ順に
  自分のノードのチャンネルだけを当てるので、各ノードに最後に書かれる値（同じノード・同じ属性の重複チャンネルは後が勝つ）は同じ。補間・ブレンドは同じ関数のまま。
- ワールド行列・ジョイント行列: 同じ BFS の順・同じ式（`world[parent] * trs_to_mat(…)`・`world[node] * ibm`）で計算する。
- **実 GPU の突き合わせのテスト**（`renderer/skin_compute_equivalence/`）: 旧い作りの写し（`legacy_skin_compute.wgsl`）と新しい作りを、同じ
  SkinComputeSystem の作り方・同じ再生指定で走らせ、ジョイント行列を浮動小数のビット単位（0 の符号まで）で比べる。RTX 3060 Laptop（Vulkan）で:

| テスト | 中身 | 比べた行列 | 違い |
|---|---|---|---|
| `random_models_match_legacy_bitwise` | 乱数のモデル 40 個（ノード 1〜64・ジョイント〜128・アニメ 1〜8 本・LINEAR / STEP / CUBICSPLINE・同じノードと属性への重複チャンネル・根から辿れないノード・ちょうど ±0 の成分・軸に沿った回転）× 131 体（範囲外のアニメ番号・端の時刻・ブレンド率 1 / 0 / 0.5 / 乱数 / NaN・Animator 非駆動） | 670,720 | 0 |
| `real_models_match_legacy_bitwise` | WarashibeFishing の glb のうちスキン＋アニメのあるもの 69 個（男 59 ノード・鳥 20 ノード・魚・ヤシ・ハイビスカス・カモメ・竿）の全アニメを 33 の時刻で＋ブレンド 64 件 | 1,034,496 | 0 |
| `random_models_in_one_pass_…`・`real_models_in_one_pass_…` | 本体と同じく複数のモデル（乱数 10 個・実際の 69 個）を 1 つの ComputePass にまとめて走らせた新しい作りを、1 つずつ走らせた旧い作りと | 11,520・79,488 | 0 |
| `nodes_beyond_limit_are_skipped` | ノード数が上限を超えるモデル（下の「ノード数の上限」）を、上限内に切り詰めたモデルの旧い作りと | 16,768 | 0 |

- 各レーンがローカル行列まで作って共有メモリへ置く形も試したところ、実際の glb で 1,034,496 個中 836 個の行列で値の 0 の符号（+0 / −0）だけが変わった
  （値の差は 0 ULP）。ローカル行列を「作ってすぐ親に掛ける」旧い作りの式の並びに戻して一致させた（コンパイラが TRS 行列の定数の 0 / 1 の行を掛け算へ
  畳み込めるかの違いと見ている＝推論）。乱数のモデルにちょうど ±0 の成分を混ぜ、テストがこの違いを捕まえることを確かめた（その形では 20,115 個が違った）。
- シェーダのコンパイラは GPU ごとに違うので、ほかの GPU では最後の桁の丸めが旧い作りと変わりうる。Intel UHD（Vulkan・同じ PC の内蔵 GPU）では
  新旧の差が 1e-6 程度（丸め）で、NVIDIA の新しい作りとの差も 1.7e-5 以下だった。なお Intel UHD では**旧い作りの方**が、続けて走らせると一部の
  インスタンス（SIMD のレーンの並びに沿った組）のジョイント行列が 1 つ前に走らせた別のモデルの出力そのものになることがあった（新しい作りでは出ない。
  調べるために書いた一時的なテストで確認して消した）。突き合わせのテストは本体と同じ選び方（独立 GPU 優先）でアダプタを選ぶ
  （環境変数 `SEED_SKIN_EQUIV_ADAPTER` で名前の一部を指定できる）。

**ノード数の上限**（`MAX_NODES` = 64。`skin_system::MAX_NODES` と WGSL の値が一致することをテストで固定）: 旧い作りも 64 ノードの配列で、64 を超える
モデルでは範囲外の添字になり結果が定まっていなかった。新しい作りは 64 番以上のノードを評価しない（そのノードのチャンネルは使わず、親が 64 番以上の
ノードは根として扱い、64 番以上のノードを指すジョイントは単位行列）と決め、読み込み時に `[SEED skin] …: ノード数 N が GPU スキニングの上限 64 を
超えています…` と警告する（WarashibeFishing の最大は男の 59 ノード）。

実行: `cargo test skin_compute_equivalence -- --ignored --nocapture`（実際の glb も比べるときは `SEED_SKIN_EQUIV_MODELS=<フォルダ>`）。

### 14.3 PC（Vulkan・NVIDIA）と Android（Mali）での予約の仕組みの違い

| | PC（RTX 3060 Laptop・Vulkan） | Android（Pixel 6a の Mali-G78 MP20・Vulkan） |
|---|---|---|
| 添字が実行時に決まる `var<private>` 配列の置き場 | レジスタに載らず「ローカルメモリ」（デバイスのメモリ。L1/L2 にキャッシュ）へ落ちる | スレッドローカルの記憶域（TLS。システムのメモリ）へ落ちる |
| 予約の単位（推論） | ドライバがデバイス全体のローカルメモリの置き場を「1 スレッドの大きさ × SM あたりの最大スレッド数 × SM 数」で確保し、より大きなシェーダが現れたら広げる | ドライバが「1 スレッドの大きさ × コアあたりの最大スレッド数 × コア数」の TLS を確保する |
| 旧い作りの量 | **実測 +444〜451 MiB**（`DrawPipelines::new` の節目。資源の確保〈wgpu-hal〉は 0）。30 SM × 1,536 スレッド × 約 10 KB ≒ 450 MB と符合する | **推定 200〜400 MB**（20 コア × 1,024〜2,048 スレッド × 約 10 KB。コアあたりのスレッド数は公開資料からの推論）。GL mtrack に入る見込み |
| `var<workgroup>`（共有メモリ）の置き場 | チップ上の shared memory（SM の L1 と分け合う）。デバイスのメモリの予約は無い | 専用のチップ上の記憶域が無く、システムのメモリ（WLS）。走っているワークグループの分だけ（推論: 数 MB） |
| 新しい作りの量 | 実測 0（節目 826.9 → 382.6 MiB。−444.3 MiB がそのまま消えた） | 推定: TLS の大きな予約は無くなり、WLS の数 MB だけ |

- Android の値は**未計測**（次の実機確認で `--es seed.gpu_mem_log 1` の `節目 DrawPipelines::new の後` と `dumpsys meminfo` の GL mtrack を、
  旧い APK と比べる＝§7）。
- 新しい作りの共有メモリ 7 KB・レーン 64 は wgpu の既定の上限（16 KB・256 レーン）の内で、本体はこの既定の上限でデバイスを作っている
  （Pixel 6a でも起動できている）ので、端末の上限を新たに求めない。

### 14.4 bindless（RT が無いときは作らない・使えるときも最初に要るときに作る）

- **何が bindless を使うか**（調べた結果）: 読むのは RT の色付き影（`deferred.rs` の `rt_bindless`・`shadow_mask`）・RT 反射（`reflection.rs`）・
  RT 半透明の屈折（`transparency.rs`）・水面反射の RT 変種（`water_reflection.rs`）・シェーディングアセットの RT 変種（`shading_asset.rs`）と、
  TLAS の組み立て（`rt_shadow.rs` の `prepare_and_build` がインスタンス表を詰める）。どれも RT の対応 GPU（`rt_shadows_supported()`）でしか走らない。
  書くのは 3D モデルの登録（`DrawContext::upload_model*` の `finalize_bindless`）だけ。地形のレイヤは `texture_2d_array` で bindless を使わない。
  → **RT が無ければ読む所が無い**。
- **RT が使えないときは資源を作らない**: `DrawContext` の置き場を `bindless::bindless_resources_wanted()`（bindless に対応し、かつ RT が使える）の
  ときだけ作る。**デバイスへ求める機能（`TEXTURE_BINDING_ARRAY` ほか）と上限は従来どおり**（デバイスの作り方を変えない。求めなくても読む所は無いが、
  full の端末の構成を変える必要は無いので安全側に倒した）。起動ログ: `[SEED BINDLESS] レイトレーシングが使えないため、資源（テクスチャ配列・
  メガバッファ 224 MiB）は作りません（…機能の要求は従来どおり）`。
- **使えるときも最初に要るときに作る**: `DrawContext.bindless` を資源そのものから置き場（`renderer/bindless_lazy.rs` の `LazyBindless`）に替え、
  使う所はすべて `get_or_create` を通す。作った直後の中身（空の登録表・空のメガバッファ・ダミーのテクスチャ）は、起動時に作って誰も触っていない状態と
  同じなので、作る時刻が変わるだけで登録の番号・オフセット・描画は変わらない。作ったときに 1 行出す:
  `[SEED BINDLESS] 資源を作りました（最初に要った所 <ファイル:行>・テクスチャ配列 N 枠・メガバッファ 224 MiB）`。
- 効果: RT の無い端末（Android の Mali など）と RT を止めた構成では 224.3 MiB が 0 に、RT のある PC で 3D のモデルも RT のパスも無い間
  （`mobile` の Wake or Pay）も 0。WarashibeFishing（RT のある PC）は最初のモデルの登録で作る（従来と同じ量）。

### 14.5 ピッキングの ID バッファ

持ち方（`app/id_buffer_ops.rs` の `decide_id_buffer_policy`。純関数・単体テストあり）:

| 状態 | 持ち方 |
|---|---|
| Edit・エディタのビューポートへ埋め込み（その場の Play を含む）・エディタの Play（`--mode=play --pipe=`＝`app_env::is_editor_play`）・`SEED_ID_PASS_IN_PLAY` | 起動時から持ち、窓の大きさの知らせのたびに作り直す（従来どおり） |
| エディタに接続していない Play（単体の `--mode=play`・パッケージ実行・Android）で構成の `picking=true`（full） | 起動時には作らない。ID パスを描くとき（図鑑のサムネイルの撮影＝`RENDER_ACTOR_THUMBNAIL` / `THUMBNAIL`）に初めて作り（`[SEED PICKING] ID バッファを作りました…`）、以後は窓の大きさに合わせる |
| 同じく `picking=false`（ui） | 作らない（従来どおり）。図鑑のサムネイルの撮影を頼まれたら、すぐに「この描画の構成（picking=false）では撮影できません」で断る（2026-10-03。以前は ID バッファの無いまま撮影へ進み、30 秒の期限切れの後「フレームが回っていない可能性」という誤った理由で失敗していた。`id_buffer_ops.rs` の `prepare_id_buffer_for_capture`） |

- エディタに接続していない Play で ID パスを描くのは図鑑のサムネイルの撮影だけ（`should_draw_id_pass`。一時停止のピックは名前付きパイプでつながった
  エディタの Play だけで、TCP の一時停止はゲームの見た目のまま）なので、ほかの読み手（ピック・D&D・配置・オービット・コントロールポイント）は
  ID パスを描いたフレームにしか読まない＝ID バッファが無くても従来と同じ結果になる。
- 振る舞いの変化（直し）: ui の構成でも、エディタが別のプロセスで起動した Play（`--mode=play --pipe=`）では ID バッファを起動時から持つようにした
  （1 回目では `is_embedded` だけを見ていたので、その Play の一時停止中のピックが ID バッファ無しで効かなかった。PC のエディタだけの話で、端末には関係しない）。

### 14.6 確かめたこと（2026-10-02）

| 確かめたこと | 結果 |
|---|---|
| `cargo build`（runtime・debug） | 0 エラー・警告 311 件（変更前と同じ数。変えた行・新しいファイルに付いた警告は 0） |
| 単体テスト（`render_profile`・`gpu_mem`・`id_buffer_ops`・`skin_system`・`skin_compute_equivalence`〈GPU の要らない分〉・`fixed_frame_dt`・`launch_options`・`simulated`・`render_quality`・`tests_skin_playback`・`bindless`） | 98 件すべて通過（GPU の要る 7 件は次の行） |
| 実 GPU のテスト（`--ignored`。RTX 3060 Laptop） | 7 件すべて通過: `skin_compute_equivalence` の 5 件（§14.2 の表）・`skin_system::multi_anim_blend_runs_on_gpu`（既存）・`render_profile::tests_gpu`（既存。ui は影と GI が置き場・bindless 0 件・スキニングのパイプライン本体なし／full はスキニングのパイプラインあり） |
| GPU メモリの計測 | §14.1 の表（Wake or Pay 5 条件・WarashibeFishing 3 条件） |
| WarashibeFishing の回帰（下の表） | 図鑑は画素単位で同じ。動く 3D の場面は、変更前と変更後の差が変更前どうし・変更後どうしの差の範囲に入る |
| 単体の Play での図鑑のサムネイル（TCP の IPC で `RENDER_ACTOR_THUMBNAIL`。魚 3 種とヤシ〈どれもスキン〉× side / front の 8 枚。変更後は ID バッファをここで初めて作る） | 変更前どうし 0 画素・変更後と変更前 0 画素（8 枚とも） |
| ui の構成（Wake or Pay。W3-7 の verify_a の写し・変更後の SEED.exe） | 21 / 21 通過（起動ログ `profile=ui`）。GPU メモリも 285.1 → 285.1 MiB |
| Android の APK（試験用のアプリ UiDevice の複製。render 節なし＝full・arm64-v8a・開発用） | 作れた（`dotnet run --project editor/tools/SeedAndroid -- build --project <複製> --abi arm64-v8a`・2026-10-02 07:24〜07:28: libSEED.so〈Develop〉182.0 秒・pak とスクリプト 9.6 秒・Gradle 30.6 秒・`exit=0`。`tmp/gpu_full/android/uidevice-full-gpufull.apk`〈58,942,473 バイト・SHA-256 `699f8d0f…b3f2`〉）。WGSL は埋め込みで、SPIR-V への変換とドライバのコンパイルは端末の起動時（naga の検証は単体テスト、Vulkan での生成は NVIDIA と Intel で確かめた）。**実機には入れていない** |

**WarashibeFishing の回帰**: 複製（tmp/w2_1a/wf）を単体の Play で開き、`SEED_FIXED_FRAME_DT=0.016666668` でゲームの時刻を揃え（300 フレーム目は全 36 回で
5.017 秒）、変更前（HEAD 65ac203a に `SEED_FIXED_FRAME_DT` の口だけを足した debug）と変更後（debug）を 3 回ずつ撮って、90・180・300 フレーム目の
違う画素の数（1280x720 = 921,600 画素のうち。3 フレームを合わせた最小〜最大）を比べた。

| 場面 | 変更前どうし | 変更後どうし | 変更前と変更後 |
|---|---|---|---|
| 図鑑（zukan）・full と RT を止めた full | 0 | 0 | 0 |
| プロローグ（島・小屋・男・鳥・ヤシ・ハイビスカスのスキンメッシュが動く）・full | 13〜98 | 31〜98 | 3〜106（差の最大 101） |
| 同・RT を止めた full | 20〜243 | 38〜267 | 11〜297（差の最大 107） |
| 釣りの場面（MainGame。海で魚が泳ぐ）・full | 1,053〜1,907 | 1,103〜3,411 | 779〜3,225（差の最大 183） |
| 同・RT を止めた full | 287〜2,269 | 285〜2,326 | 57〜2,963（差の最大 179） |

- 変更前どうしにも差が残るのは、ゲームの中の乱数（`FishManager` の `System.Random` の種が実行ごと＝魚の出方・泳ぐ位置）・実時間で進む物理のスレッド・
  非同期のモデルの読み込みの完了するフレーム（プロローグのヤシの葉の縁などが 2 通りに分かれる）のため。差のある所を切り出して見ると、MainGame は海の魚、
  プロローグは葉の縁などの数画素だった。
- 時刻を揃える前（`SEED_FIXED_FRAME_DT` なし）は、同じフレーム番号でもゲームの時刻が実行ごとに 0.1〜0.3 秒ずれ（MainGame の 300 フレーム目で 9.14〜9.53 秒）、
  動く 3D の画素の差がその揺れに埋もれた（最初のフレームに入る起動の時間が版ごとに少し違い、ずれ方が変更前と揃わなかった）。そのため上の口を足して比べ直した。
  時刻を揃える前の撮影でも、図鑑は画面全体・プロローグの会話の本文は違う画素 0、サムネイル 8 枚も 0 だった。

**決定的な回帰のための `SEED_FIXED_FRAME_DT`**（`core/clock/fixed_frame_dt.rs`。検証用・既定は未設定＝従来どおり）: 最初のフレームの delta に起動
（パイプラインの生成・シーンの読み込み）の時間が入るため、同じフレーム番号でもゲームの時刻（`[PERF]` の `anim_t`）が実行ごと・版ごとに 0.1〜0.3 秒
ずれていた（WarashibeFishing の MainGame の 300 フレーム目で 9.14〜9.53 秒）。`SEED_FIXED_FRAME_DT=<秒>` を渡すと、すべてのフレームの delta をその値に
する（300 フレーム目は全 36 回で 5.017 秒）。物理のスレッドは自分の時計で進み、ゲームの中の乱数（`System.Random` の種）・非同期の読み込みの完了する
フレームは揃わないので、それらによる差は残る（変更前どうしの差として測ってから比べる）。

### 14.7 残した課題

- **Android（Pixel 6a）での実測**: §14.3 の推定（スキニングの TLS の予約・bindless・ID バッファ）を、次の実機確認で旧い APK と比べる（§7）。
- **RT のある PC の `desktop` 品質では、3D の中身が無くても RT の半透明の TLAS の組み立てが毎フレーム走り、bindless（224 MiB）を作る**。
  RT のインスタンスが 0 のフレームで RT のパスと TLAS を飛ばせば作らずに済むが、パスの選び方が変わるので画素の一致を確かめてから
  （2D だけの作品は ui の構成を使えばよい）。
- **ノード数 64 を超えるスキンモデル**: 64 番以上のノードを評価しない（警告を出す）。上限を 128 にするには共有メモリが 14 KB（既定の上限 16 KB の内）になり、
  1 レーンが 2 ノードを受け持つ形にする。
- **単体の Play での `RENDER_ACTOR_THUMBNAIL` の構図**: 魚・ヤシの side と front が同じ絵になり、魚がごく小さく写る（変更の前後で同じ。
  エディタからの撮影は確かめていない）。
- 突き合わせのテストの旧い作りの写しは、旧い作りとの比較のためだけに残している（スキニングの式を変えるときは写しも直すか、テストを外す）。
