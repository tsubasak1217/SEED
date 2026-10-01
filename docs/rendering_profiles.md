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
     ローカルメモリを確保する（資源の確保〈wgpu-hal〉は 0 で、ヒープの使用量だけが 193.6 → 644.4 MiB に跳ねる。§5）。
  2. **bindless のメガバッファ 224 MiB**（索引 128・UV 64・法線 32 MiB）。RT のヒットシェーディングの土台で、RT の無い GPU（Pixel 6a の Mali-G78）でも確保していた。
  3. **Play のピッキングの ID バッファ 39.6 MiB**（画面と同じ 1080x2400 の Rgba32Float）。Play では既定で ID パスを描かないのに確保していた。
  4. **シャドウマップ 56 MiB**（CSM 1024²×3 層＋スポット 1024²×4 層 を本体とカメラのプレビューで 2 組）、**GI（DDGI）のアトラス 13 MiB**（RT が無くても確保）。
  5. wgpu の既定の GPU メモリの方針（`MemoryHints::Performance`）は、転送用に 128 MiB からの塊を持ち、32 MiB 未満の資源を 2 の冪に切り上げて切り出す。
- **構成 `ui`**（`"render": { "profile": "ui" }`）は上の 1〜4 を作らず（影・GI は 1x1 の置き場）、レイトレーシング・G-Buffer・後処理を止め、
  GPU メモリを小さな塊で確保する（`MemoryHints::MemoryUsage`）。3D のシーン（モデル・地形・水・天球・SEED.Draw3D）は描かない（あれば 1 回警告）。
- **効果（PC・同じ条件）: プロセスのヒープの使用量 1307.1 MiB → 285.1 MiB（-78%）、追跡した資源 409.3 → 76.2 MiB**。
  残りの大半は PC の NVIDIA のスワップチェイン（約 156 MiB。Android では GL mtrack の外の EGL mtrack）とデバイス自体（23.5 MiB）（§6）。
- **画面は full と画素単位で同じ**（Wake or Pay の一覧・編集・鳴動・庭・アクティビティのグラフ 8 枚 × `mobile`/`desktop` 品質、
  templates/ui のギャラリー・グラフ・画面遷移の見本 16 枚。違う画素 0。§9）。
- **実機（Pixel 6a）の `dumpsys meminfo` での確認は次の実機確認で行う**（APK は作成済み。手順は §7・APK は §9.2）。

## 2. 設定

`project_settings.json`（エディタのプロジェクト設定の画面には欄が無い。JSON を直接書く。保存しても消えない＝`ProjectSettingsData` の `ExtraData`）:

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
| `bindless` | bindless の機能をデバイスへ求めず、テクスチャ配列（4096 枠）とメガバッファ（224 MiB）を作らない | `Renderer::new`（`supports_bindless`） |
| `ray_tracing` | RT の機能（RAY_QUERY・加速構造）をデバイスへ求めない（RT のパイプライン・TLAS・DDGI の更新・RT 影を作らない） | `Renderer::new`（`supports_rt`）・品質の上限 `translucency=raster` |
| `post` | ブルーム・ビネット・FXAA を止める（トーンマップは残す） | 品質の上限 `bloom` / `vignette` / `fxaa` = false |
| `picking` | 単体の Play（PC の SEED.exe・Android）でピッキングの ID バッファ（画面と同じ大きさの Rgba32Float）を作らない。Edit・エディタに埋め込んだ Play・`SEED_ID_PASS_IN_PLAY` では作る | `app_init.rs`・`event_handler.rs`（`App::id_buffer_wanted`） |
| `memory_hint` | `"performance"`（既定）/ `"memory_usage"`。wgpu の `MemoryHints`。gpu-alloc（wgpu 25 の Vulkan）では、performance は転送用の塊を 128〜512 MiB・32 MiB 未満の資源を 2 の冪に切り上げた塊から、memory_usage は転送用 8〜64 MiB・8 MiB 以上の資源を専用に確保する | `Renderer::new`（`DeviceDescriptor::memory_hints`） |

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
| Android | `am start … --es seed.gpu_mem_log 1`（デバッグ版の APK） |

無効（既定）なら資源の作成の記録もしない（`*_tracked` は旗を 1 つ読んで wgpu を呼ぶだけ）。

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

## 5. スキニングの compute がローカルメモリを予約する件

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
| エディタ | プロジェクト設定の画面に欄は無い（JSON を直接書く）。エディタのシーンビュー（Edit）も同じ構成で描くので、ui のプロジェクトでは 3D は出ない（警告のログが出る） |
| シェーディングアセット | デファード専用なので ui では効かない（`[SEED QUALITY][WARN]` が出る） |

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
| `runtime/src/engine/core/app_base/app/render_profile_ops.rs` | 起動時の決定・品質へ上限・ID バッファの要否・3D を描かなかったことの警告 |
| `runtime/src/engine/core/renderer/gpu_mem/` | 計測（`track.rs` の `GpuMemDeviceExt`・`category.rs` の分類の表・`size.rs`・`registry.rs`・`report.rs`・`heap_budget.rs`） |
| `runtime/src/engine/core/app_base/app/gpu_mem_ops.rs` | 計測のきっかけ（フレーム・待機・背面・IPC `GPU_MEM_REPORT`） |
| `renderer/mod.rs` | `Renderer::new`（RT・bindless・MemoryHints の構成・計測の節目）・`gpu_memory_heaps` / `gpu_hal_memory` / `swapchain_estimate` |
| `methods/drawer/mod.rs` | `DrawContext::new`（影・GI の置き場） |
| `renderer/pipeline.rs`・`skin_system.rs` | `SkinComputePipeline::layouts_only`・本体が無ければディスパッチしない |
| `renderer/shadow.rs`・`ddgi/resources.rs` | `ShadowResources::new_placeholder`・`GiResources::new_placeholder` |
| `app/frame_renderer.rs`・`terrain_scatter_ops.rs` | 3D のシーンを描かない（`scene_3d`） |
| `platform/launch_options.rs`・`android/native/src/launch.rs`・`main.rs` | `seed.gpu_mem_log` / `seed.render_profile`・`--gpu-mem-log` / `--render-profile=` |
| `platform/screen/simulated.rs` | `SEED_SIM_WINDOW_SIZE` |
| `runtime/Cargo.toml` | wgpu の `counters` 機能・`ash`（VK_EXT_memory_budget の問い合わせ） |
