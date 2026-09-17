# アセット形式のバージョンとマイグレーション（設計）

エンジンを更新しながらチームでゲームを作るための土台。**形式ごとの `format_version`** と、
**前進のみ・1 段ずつ連鎖するマイグレーション**を、ランタイム（Rust）に一本化して持つ。
2026-09-18 設計。実装状況は末尾の「段階」を参照。

## 1. 方針（決定事項）

| 項目 | 決定 |
|---|---|
| 版の単位 | **形式ごと**（`.scene`、`.actor`、`.anim` …）。エンジンの版（`.seedproj` の `engine_version`）とは別物 |
| 版の表現 | JSON はトップレベルの整数 `"format_version"`。**欄が無いファイルは 1** とみなす。バイナリは既存のヘッダ版をそのまま使う |
| 変換の向き | **前進のみ**。N → N+1 の純関数を連鎖させる。逆変換は作らない |
| 未来の版 | **読み込みを拒否**する（「新しいエンジンで作られたファイル」）。前進のみ・連鎖という前提を守るため |
| 変換の場所 | **ランタイム（Rust）に一本化**。エディタ・コマンドラインからは同じものを呼ぶ |
| 読み込み時 | **メモリ上でだけ変換**し、ファイルは書き換えない（開いただけで全員の作業コピーが書き換わると VCS で衝突するため） |
| ファイルの書き換え | (a) そのファイルを普通に保存したとき（現行版で書かれる） (b) **「プロジェクトを一括アップグレード」**を実行したとき。後者はオーナーが 1 回実行して送信する運用 |
| 項目の追加 | 版を上げない。`#[serde(default)]` で足す（従来どおり）。**版を上げるのは、キー名の変更・構造の組み替え・意味の変更（単位や座標系）**のときだけ |
| スクリプト API | 自動変換しない。旧 API を残して `[Obsolete]` で警告する別の仕組み |

## 2. 現状（2026-09-18 の棚卸し）

- `.scene` / `.actor` / `.anim` / `.mat` / `.postfx` / `layers.json` / `props.json` / `project_settings.json` には版の欄が**無い**。
- すでに版を持つ: `.inputmap`（`version` = 2）、`.sprite_mesh`（1）、`.tvox`（4）、`.tcover`（2）、`.tscatter`（1）、
  `terrain_meta.json`（1）、`.seedproj`（`format_version` = 1）、シェーディング WGSL（`@shading_contract 1`）。
  `tvox.rs` の `read_chunk`（v1→v4 を 1 関数で持ち上げ、書き出しは常に現行版）が既存の手本。
- 旧形式への対応は `#[serde(default)]` / `alias` / ロード後の読み替えとして 68 ファイルに散在
  （例: 旧 `RigidbodyComponent` → `ColliderComponent` への吸収、`BlendMode` の `alpha` → `Normal`、`rt_shadows: bool` → 影モード）。
- `.actor` の読み込みは 3 か所に重複（`scene.rs` の `load_actor` / `load_actor_into`、`prefab_ops.rs` の `load_actor_data_with_hash`）。
  プレハブのハッシュは**生テキスト**から取るので、変換はハッシュ計算の後に行う。
- **エディタ（C#）がアセットを直接読む箇所**（Rust の変換を通らない）:
  `SceneSettingsData.cs`（`.scene` の `settings` / `shading_asset`）、`FishCatalogGenerator.cs`（`.actor`）、
  `AssetCollector.cs`（参照の正規表現走査、`project_settings.json`）。`.anim` / `.inputmap` / `.sprite_mesh` / `layers.json` /
  `props.json` / `project_settings.json` は**書き手が C# にしか無い**。`.inputmap` の v1→v2 は Rust と C# に二重実装されている。
- `safe_write`（tmp → rename ＋ `.backup/` 世代）を通るのは `.scene` と `.actor`（IPC 保存）だけ。

## 3. 仕組み

```text
runtime/src/engine/core/migration/
  mod.rs            公開 API（migrate_to_current / stamp_current / FormatKind）
  kind.rs           FormatKind（Scene / Actor / …）と、形式ごとの現行版・欄の名前
  registry.rs       (FormatKind, from_version) → 変換関数 の表。抜け・重複は起動時テストで検出
  runner.rs         連鎖の実行（欄なし = 1、現行版なら何もしない、未来版は拒否）と MigrationReport
  error.rs          MigrationError（未来版・段の欠落・変換失敗）
  json_walk.rs      アクター木とコンポーネントを辿る共通ヘルパ（.scene と .actor で共用）
  steps/<kind>/vN_to_vM.rs   変換 1 段 = 1 ファイル（純関数: &mut serde_json::Value → Result）
  upgrade/          一括アップグレード（対象の列挙、dry-run、safe_write での書き込み、レポート）
```

- 変換 1 段は `serde_json::Value` の上の**純関数**。ファイルも時刻も乱数も触らない（ゴールデンテストを固定できる）。
- シーンとアクターはコンポーネントの入れ物なので、多くの変換は「ある種類のコンポーネントのデータを直す」形になる。
  版はファイル単位で 1 つ持ち、`json_walk` の「この種類のコンポーネントを全部たどる」ヘルパで書く。
- 読み込みの入口に 1 か所ずつ差し込む: `Scene::from_json`（`from_str` の直前）、**新設する `ActorData` 共通ローダ**（3 か所の重複を解消）、
  以降は `AnimationClip::from_json`、`ActionMap::parse`、`SpriteMesh::from_json`、地形 JSON の `from_json_str`。
- 保存の出口で現行版を刻む（`stamp_current`）。`.actor` は `ActorData` を入れ子にも使うので、構造体に欄を足さず、
  ファイルへ書く直前の `Value` に刻む（シーン内の各アクターには付けない）。
- 一括アップグレードは `SEED.exe --upgrade-project <プロジェクト> [--dry-run]`。対象形式のファイルを列挙し、古いものだけ変換して
  `safe_write` で書き、結果を 1 行 1 件の JSON で出す。エディタはこれを呼んで結果を表示する（VCS パネルに変更として並ぶ）。

## 4. 変換を書くときの手順（チェックリスト）

1. 本当に版を上げる必要があるか（項目の追加なら `serde(default)` で足りる）。
2. `kind.rs` の現行版を +1 し、`steps/<kind>/vN_to_vM.rs` に純関数を足して `registry.rs` に登録する。
3. `runtime/tests/fixtures/migration/<kind>/` に **変換前（vN）と期待結果（vM）** の見本を置き、ゴールデンテストを足す。
   最古の版から現行版までの連鎖テストも通す。
4. **C# 側がそのキーを直接読んでいないか**を 2 章の一覧で確認し、読んでいれば同じコミットで直す。
5. 旧形式を吸収していた `alias` / ロード後の読み替えは、変換へ移したら削除候補としてバックログに書く
   （すぐには消さない。一括アップグレード前のファイルが残っているため）。
6. `docs/asset_migration.md` の「版の履歴」に 1 行足す。

## 5. 版の履歴

| 形式 | 版 | 内容 |
|---|---|---|
| `.scene` | 1 | 欄なしの従来形式 |
| `.scene` | 2 | 旧 enum 表記の正規化（`ParticleEmitterComponent.blend` の `alpha`→`normal` / `additive`→`add`、同 `shape` の `point`→`pixel`、`CanvasComponent.gravity_mode` の `screen_down`→`world_down`） |
| `.actor` / `.actor2d` | 1 | 欄なしの従来形式 |
| `.actor` / `.actor2d` | 2 | `.scene` v2 と同じ（同じ変換をアクタ木へ適用する） |

## 6. 実装メモ（M1 時点）

### 置き場と公開 API

`runtime/src/engine/core/migration/`。外から使うのは次の 3 本だけ。

| API | 用途 |
|---|---|
| `migration::load_json::<T>(kind, raw) -> Result<T, MigrationError>` | 読み込みの入口。BOM 除去・版の判定・変換・デシリアライズを一括で行う |
| `migration::to_stamped_pretty_json(kind, body) -> Result<String, serde_json::Error>` | 保存の出口。**先頭に**現行版を刻んだ pretty JSON を作る |
| `migration::migrate_to_current(kind, &mut Value) -> Result<MigrationReport, MigrationError>` | `Value` の上で連鎖を回す（一括アップグレードが使う） |

`load_json` は**現行版のファイルで余計なコストを払わない**。まず版だけを先読みし、現行版なら
テキストから直接デシリアライズする（従来と同じ経路）。古い版のときだけ `Value` を 1 回組み立てる。

刻印は `#[serde(flatten)]` のラッパー経由なので、**本体の構造体に版の欄を足さなくてよい**。
`ActorData` はシーンの中へ入れ子で使われるため、これが必須の性質になっている
（版が付くのはファイルのトップレベルだけで、シーン内の各アクタには付かない）。

### 差し込んだ場所

| 形式 | 読み込み | 保存 |
|---|---|---|
| `.scene` | `app_base/scene.rs` の `Scene::from_json`（ファイル経路 5 か所と Play スナップショット復元が全部ここを通る） | `Scene::to_json_with_actors`（通常保存・Play 用一時シーン・スナップショットを兼ねる） |
| `.actor` / `.actor2d` | `app_base/actor_file.rs`（**新設の共通ローダ**。`Scene::load_actor` / `Scene::load_actor_into` / `prefab_ops::load_actor_data_with_hash` の 3 か所が呼ぶ） | 同 `actor_file::save`（IPC `SaveActor` とエクスポートの両方） |

- プレハブのハッシュは**生テキスト**から取る（`actor_file::load_with_raw` が生テキストも返す）。
  順序は「読む → ハッシュ → 変換」。
- `core/loader/async_loader.rs` の `.actor` 走査は `model_path` を拾うだけなので変換を通さない
  （キー名を変える変換を足すときはそこも直す。コード内にコメントあり）。
- アクタのエクスポートは素の `std::fs::write` だったが、IPC 保存と同じ `safe_write`
  （`.backup/` 世代 → `.tmp` → rename）に揃えた。

### 一括アップグレード

`SEED.exe --upgrade-project <プロジェクト|.seedproj|assets ルート> [--dry-run]`。
ウィンドウも GPU も作らずに終了する（`main` の入口で分岐する）。

- 対象は `assets/` 配下の `.scene` / `.actor` / `.actor2d`。ドットで始まるフォルダ（`.backup` など）は入らない。
- 出力は 1 ファイル 1 行の JSON ＋ 最後に集計 1 行。`failed` か `future_version` があれば終了コードは非 0。
- **差分を最小にする**: 変換段が中身を 1 つも変えなかったファイルは、元の整形を保ったまま
  **版の 1 行だけ**を差し込む。実際に変換が入ったファイルだけ `SceneData` / `ActorData` を
  経由して書き直す（`Value` をそのまま書くと欄がアルファベット順に並び替わり、全行が差分になるため）。
  書き直した場合は「保存形式の正規化により、変換手順以外の差分も含まれます」と `message` に出る。
- 本体の型として読めないファイルは `failed` にして**書き換えない**（安全弁）。
- **副作用**: `.actor` の内容が 1 行でも変わると `prefab_content_hash`（生テキストの FNV）が変わるため、
  その `.actor` を参照するシーン内インスタンスの `prefab_hash` が古くなり、エディタで「プレハブが更新された」
  と表示される。表示だけで壊れはしないが、一括アップグレードの直後は一斉に出る。
  （シーン側の `prefab_hash` を貼り直す後処理は M2 以降の課題。`prefab_hash` を 1 つも持たない
  プロジェクトでは起きない。）

## 7. 段階

- **M1（完了）**: 仕組み（kind / registry / runner / error / json_walk）、`.scene` と `.actor` の読み込みフックと保存時の刻印、
  `ActorData` 共通ローダ、未来版の拒否、最初の実変換（旧 enum 表記の正規化）、一括アップグレードの CLI、ゴールデンテスト。
- **M2**: エディタのメニュー（一括アップグレードの実行と結果表示）、`engine_version` の確認ダイアログからの導線、
  `.anim` / 地形 JSON / `.mat` / `.postfx` への拡大、`.inputmap` の二重実装の解消、保存経路を `safe_write` に寄せる。
- **M3**: 構造的な旧対応（`RigidbodyComponent` の吸収など）を変換へ移す。パッケージ化のときに変換済みで同梱する。
  一括アップグレード後に `#[serde(alias)]` の削除候補を片付ける。
