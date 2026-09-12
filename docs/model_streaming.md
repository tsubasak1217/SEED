# モデルの非同期ストリーミングロード

プレイ中に生成されるアクタ（魚・エフェクト・漂流物など）のモデルを、
**メインスレッドを止めずに**読み込むしくみ。この文書が正典。

実装:

| ファイル | 役割 |
|---|---|
| `runtime/src/engine/core/loader/async_loader.rs` | ワーカースレッド・ジョブキュー・RAM キャッシュ・プリフェッチ・設定の解釈 |
| `runtime/src/engine/core/app_base/app/model_streaming.rs` | ECS/GPU への差し込み、毎フレームのポンプ、プリフェッチ開始 |
| `runtime/src/engine/core/app_base/scene.rs` | `build_actor` の ModelComponent 分岐（同期／非同期の分かれ目） |
| `runtime/src/engine/core/app_base/app/script_scene_ops.rs` | 非同期を許可する唯一の区間（スクリプトの `Instantiate`） |
| `runtime/src/engine/asset_fs.rs` | `list_by_extension`（プリフェッチの `.actor` 列挙。PAK 対応） |

---

## 1. 何が問題だったか

魚が出現するたびに、そのモデルは **メインスレッドで同期的に** 読まれていた。

```
[SEED cache] キャッシュヒット: assets://mainGame/models/fish/Lv1/iwashi.glb (16.8 ms)
```

内蔵 NVMe では派生キャッシュ（`<project>/cache/*.smdl`）1 件 0.2〜0.5ms なので気付かなかったが、
USB 外付け SSD では 1 件 13〜50ms かかり、**出現のたびにフレームが飛んでいた**。
配布版でもプレイヤーのドライブが遅ければ同じことが起きる（PAK からの読み出しも同期）。

## 2. 何をしたか

「ディスク読み＋CPU デコード」をワーカースレッドへ追い出し、
メインスレッドは毎フレーム決まった位置で完成品を受け取り **GPU アップロードだけ** を行う。

```
[メイン] build_actor → request(path)  ──► [キュー] ──► [ワーカー] load_model()
                                                            │ 完成した Model
                                                            ▼
[メイン] pump_model_streaming()  ◄── crossbeam channel ◄────┘
              │ RAM キャッシュ（バイト上限つき LRU）へ格納
              ▼
         take_ready(path) → upload_model_with_overrides（予算内で N 件/フレーム）
              │
              ▼
         ModelComponent.model / gpu_model / instanced_batch へ差し込み
```

ロード中のアクタは `model = None` / `gpu_model = None` になる。
描画・シャドウ・RT・ピッキングは `gpu_model.is_none()` で素通しされるので**見えないだけ**で、
Transform・スクリプト・物理・親子追従は通常どおり動く。完成した時点で描画へ合流する。

## 3. 非同期にする範囲（意図的に狭い）

| 経路 | 挙動 |
|---|---|
| **スクリプトの `GameObject.Instantiate`**（`apply_script_instantiate`） | **非同期** |
| `.scene` の読み込み | 従来どおり**同期** |
| サムネイル生成（`thumbnail_ops.rs`） | 従来どおり**同期** |
| エディタ操作（コンポーネント追加・複製・プレハブ再展開・ドロップ配置） | 従来どおり**同期** |
| 地形チャンク・散布モデル | 従来どおり（別経路） |

シーン読み込みを同期のままにしているのは、エディタが `SCENE_LOADED` を
**「全モデルが揃った」合図**として使っているため（サムネイル・図鑑・`PLAY_DIAG`）。
ここを非同期にすると、まだモデルが無い状態でサムネイルを撮る等の壊れ方をする。

許可の実体は `model_streaming::AsyncModelScope`（スレッドローカルの RAII ガード）で、
`script_scene_ops.rs::apply_script_instantiate` の `Scene::load_actor_into` 呼び出しだけが入る。

## 4. プリフェッチ（先読み）

シーン据え付け直後（`install_loaded_scene` の末尾）に、低優先度で 2 系統を先読みする。
**走査も読み込みもすべてワーカー側**なので、メインスレッドはここでディスクに触らない。

1. **シーン内のプレハブ参照**（アクタの `prefab_source`）が指す `.actor` のモデル
2. **アセット配下の `.actor` 走査**（プロセスで 1 回だけ）。
   スクリプトが `Instantiate` するプレハブ（魚など）はシーン JSON に現れないため、
   「後で生成されるかもしれないプレハブ」をここで拾う。
   走査範囲は `streaming.prefetch_dirs` で絞れる（既定はアセットルート全体）。
   上限は `PREFETCH_MAX_ACTOR_FILES = 512` 件。

`.actor` の JSON はキー名 `model_path` を再帰的に拾う（構造変更に強い）。
`terrain://` と空文字は除外する。
派生キャッシュが `PREFETCH_MAX_MODEL_CACHE_BYTES`（64MiB）を超えるモデルは先読みしない
（RAM キャッシュを一掃してしまうため。実使用時に高優先度で読む）。

先読み済みモデルは RAM キャッシュ（LRU）に載り、実際に `Instantiate` された時点で
`request` が即座に `Ready` を返す ＝ **1 フレームも待たずに**従来と同じ同期構築になる。

> **先読みの失敗は実害が無い。** そのアクタが本当に生成されたときに改めて読み直す
> （`JobPriority::OnDemand` は失敗の記憶を消して必ず再試行する）。
> ログも `先読みできませんでした（実使用時に再試行します）` と区別して出る。

## 5. 常駐時間（`keep_alive_secs`）

統合バッチ（`shared_model_batches`）と RT の BLAS キャッシュは、
「そのフレームの描画対象に一定フレーム連続で不在」なら解放される（`compute_stale_batch_prune`）。
旧実装は **60 フレーム固定**（60fps で約 1 秒）で、画面外へ出入りするオブジェクト
（魚・漂流物）でバッチと BLAS の再構築が繰り返されていた。

現在は `streaming.keep_alive_secs`（既定 **30 秒**）を目標 fps でフレーム数へ換算して使う
（`target_fps` が 0＝無制限のときは 60fps 換算）。起動ログに解決結果が出る。

```
[SEED INIT] streaming keep_alive=30s -> 1800 frames (target_fps=60)
```

> これは **GPU バッチの寿命**であって、CPU モデル（`DrawContext::model_cache`）とは別。
> CPU 側は従来どおりプロセス内キャッシュに残り続ける。

## 6. 設定（`project_settings.json`）

`assets/project_settings.json` のトップレベルに `streaming` オブジェクトを置く。
**節ごと無くてよい**（全キーが既定値になる）。部分指定も可。
エディタのプロジェクト設定 UI には未対応なので、JSON を直接編集する。

```json
{
  "streaming": {
    "enabled": true,
    "worker_threads": 1,
    "upload_budget_ms": 4.0,
    "max_uploads_per_frame": 2,
    "keep_alive_secs": 30,
    "prefetch": true,
    "prefetch_dirs": ["mainGame/actors"],
    "ram_cache_mb": 256
  }
}
```

| キー | 既定 | 意味 |
|---|---|---|
| `enabled` | `true` | `false` で**完全に従来どおりの同期ロード**へ戻る（先読みも止まる） |
| `worker_threads` | `1` | ロード用ワーカー数（1〜4）。ボトルネックはシーク待ちなので増やしても伸びにくい |
| `upload_budget_ms` | `4.0` | 1 フレームで GPU アップロードに使ってよい時間。超えた分は次フレームへ |
| `max_uploads_per_frame` | `2` | 1 フレームでアップロードしてよい件数 |
| `keep_alive_secs` | `30` | 統合バッチ／BLAS の常駐時間（§5） |
| `prefetch` | `true` | 先読みを行うか |
| `prefetch_dirs` | `[]`（＝ルート全体） | 先読みで走査するディレクトリ（アセットルート相対）。大きいプロジェクトで絞る用 |
| `ram_cache_mb` | `256` | 先読み済み CPU モデルを抱える RAM 上限。超えたら最終アクセスが古い順に捨てる |

### 環境変数による上書き（切り分け用）

`SEED_STREAMING` はプロジェクト設定より優先される。

| 値 | 効果 |
|---|---|
| `0` / `off` / `false` / `no` | 非同期ロードを完全に無効化（先読みも止まる）＝ 本機能導入前と同じ経路 |
| `noprefetch` / `no_prefetch` | 非同期ロードは使うが**先読みだけ**止める |
| その他 | 何も上書きしない |

不具合が出たときに「ストリーミングが原因か」を 1 回の再起動で切り分けられる。
配布版でも使える退避手段。

## 7. ログの読み方

ローダーのログは**発生したスレッド**で接頭辞が変わる。これが性能検証の要。

| 接頭辞 | 意味 |
|---|---|
| `[SEED cache]` | **メインスレッド**で読んだ（＝そのぶんフレームが止まっている） |
| `[SEED stream/worker]` | ロードワーカーで読んだ（＝メインスレッドは止まっていない） |
| `[SEED stream]` | ストリーミング機構そのもののログ（起動・先読み走査・メイン適用・統計） |

```
[SEED stream] 非同期モデルロード開始 workers=1 upload=2件/4.0ms keep_alive=30s prefetch=true ram_cache=256MiB
[SEED stream] プリフェッチ走査: .actor 56 件（対象 アセットルート全体）
[SEED stream/worker] キャッシュヒット: assets://mainGame/models/fish/Lv1/iwashi.glb (19.0 ms)
[SEED stream] メイン適用: 2 件 / 0.85 ms（保留 3 件, RAM キャッシュ 12 件 8.4 MiB）
[SEED stream] 累計 完了 35 件 / 失敗 2 件 / ワーカー総時間 812 ms、待ち行列 0 件、RAM キャッシュ 33 件 9.8 MiB
```

**プレイ中に `[SEED cache]` が出たら、そこはまだ同期ロードしている**（＝調査対象）。
シーン読み込み中（初回フレーム前）に `[SEED cache]` が出るのは仕様どおり。

プロファイラでは `ストリーミング/モデル反映` の区間がメイン側の費用になる。

## 8. 設計上の注意

- **保留台帳はシーン差し替えで必ず破棄する**（`clear_pending_slots`）。
  台帳は ECS の `Entity`（インデックス＋世代）を持つので、`World` を作り直した後に
  持ち越すと同じ (index, generation) が別実体を指し、無関係なアクタへモデルを差し込む。
- **差し込み前に `source_path` を照合する**。保留中にインスペクタでモデルを差し替えられた
  場合、古いロード結果で上書きしない。
- **`ModelComponent::model` が `None` の間は警告を出さない**。
  `animation_ops.rs` の Model クリップ解決は毎フレーム走るため、未到着の数フレームで
  「アニメを解決できません」を出すと魚 1 匹につき毎フレームのログになる。
  モデルが有るのに解決できないときだけ警告する（＝本物の設定ミスだけ拾う）。
