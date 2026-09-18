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

## 2. 現状（2026-09-18 の棚卸し。M2a 後の状態は 5 章・6 章を見ること）

- 以下は M2a で仕組みへ載せた（版の欄の現状は **5.1 の表**が正典）:
  `.scene` / `.actor` / `.anim` / `.mat` / `.postfx` / `.inputmap` / `.sprite_mesh` /
  `layers.json` / `props.json` / `cover_materials.json` / `project_settings.json`。
- まだ仕組みに載っていない（独自の版機構を持つ）: `.tvox`（4）、`.tcover`（2）、`.tscatter`（1）、
  `terrain_meta.json`（1）、`.seedproj`（`format_version` = 1）、シェーディング WGSL（`@shading_contract 1`）。
  `tvox.rs` の `read_chunk`（v1→v4 を 1 関数で持ち上げ、書き出しは常に現行版）が既存の手本。
- 旧形式への対応は `#[serde(default)]` / `alias` / ロード後の読み替えとして 68 ファイルに散在
  （例: 旧 `RigidbodyComponent` → `ColliderComponent` への吸収、`BlendMode` の `alpha` → `Normal`、`rt_shadows: bool` → 影モード）。
- `.actor` の読み込みは 3 か所に重複（`scene.rs` の `load_actor` / `load_actor_into`、`prefab_ops.rs` の `load_actor_data_with_hash`）。
  プレハブのハッシュは**生テキスト**から取るので、変換はハッシュ計算の後に行う。
- **エディタ（C#）がアセットを直接読む箇所**（Rust の変換を通らない）:
  `SceneSettingsData.cs`（`.scene` の `settings` / `shading_asset`）、`FishCatalogGenerator.cs`（`.actor`）、
  `AssetCollector.cs`（参照の正規表現走査、`project_settings.json`）。`.anim` / `.inputmap` / `.sprite_mesh` / `layers.json` /
  `props.json` / `project_settings.json` は**書き手が C# にしか無い**。
  → M2a で `--migrate-json` の口を用意し、M2b で C# 側の読み手を通した（6.6 章）。
  `SceneSettingsData.cs` / `FishCatalogGenerator.cs` / `AssetCollector.cs` は
  まだ門を通っていない（`docs/backlog.md`）。
  `.inputmap` の v1→v2 は M2a で Rust 側を変換段へ集約し、M2b で C# 側の二重実装を削除した。
- `safe_write`（tmp → rename ＋ `.backup/` 世代）の適用範囲は 6 章「保存経路」の表を見ること
  （M2a で `terrain_meta.json` / `.tcover` / `.tscatter` を追加。`.tvox` は M3）。

## 3. 仕組み

```text
runtime/src/engine/core/migration/
  mod.rs            公開 API（load_json / to_stamped_pretty_json / migrate_to_current）
  kind.rs           FormatKind と、形式ごとの現行版・版の欄名・対象ファイル・書き手の表
  registry.rs       (FormatKind, from_version) → 変換関数 の表。抜け・重複は起動時テストで検出
  runner.rs         連鎖の実行（欄なし = 1、現行版なら何もしない、未来版は拒否）と MigrationReport
  error.rs          MigrationError（未来版・段の欠落・変換失敗）
  json_walk.rs      アクター木とコンポーネントを辿る共通ヘルパ（.scene と .actor で共用）
  cli.rs            コマンドラインの入口（main から呼ばれるのはここ 1 本）
  migrate_json.rs   標準入出力 1 件だけを変換する CLI（--migrate-json <kind>）
  steps/<kind>/vN_to_vM.rs   変換 1 段 = 1 ファイル（純関数: &mut serde_json::Value → Result）
  upgrade/
    mod.rs          一括アップグレード本体（--upgrade-project）
    target.rs       アセットルートの決定と対象ファイルの列挙
    canonical.rs    形式ごとの「保存と同じテキスト化」と、読めることの検証（網羅 match）
    prefab_rehash.rs 変換後の prefab_hash 貼り直し
    report.rs       結果の表現（JSON Lines と集計）
```

- 変換 1 段は `serde_json::Value` の上の**純関数**。ファイルも時刻も乱数も触らない（ゴールデンテストを固定できる）。
- シーンとアクターはコンポーネントの入れ物なので、多くの変換は「ある種類のコンポーネントのデータを直す」形になる。
  版はファイル単位で 1 つ持ち、`json_walk` の「この種類のコンポーネントを全部たどる」ヘルパで書く。
- 読み込みの入口は**形式ごとに 1 本だけ**（6 章の表）。新しい経路を作るとマイグレーションが素通りする。
- 保存の出口で現行版を刻む（`to_stamped_pretty_json`）。`.actor` は `ActorData` を入れ子にも使うので、
  構造体に欄を足さず、ファイルへ書く直前の `Value` に刻む（シーン内の各アクターには付けない）。
- 一括アップグレードは `SEED.exe --upgrade-project <プロジェクト> [--dry-run]`。対象形式のファイルを列挙し、古いものだけ変換して
  `safe_write` で書き、結果を 1 行 1 件の JSON で出す。エディタはこれを呼んで結果を表示する（VCS パネルに変更として並ぶ）。
- C# だけが書く形式は、エディタがファイルを読む前に `SEED.exe --migrate-json <kind>` を通す（6.5 章）。

## 4. 変換を書くときの手順（チェックリスト）

1. 本当に版を上げる必要があるか（項目の追加なら `serde(default)` で足りる）。
2. `kind.rs` の現行版を +1 し、`steps/<kind>/vN_to_vM.rs` に純関数を足して `registry.rs` に登録する。
3. `runtime/tests/fixtures/migration/<kind>/` に **変換前（vN）と期待結果（vM）** の見本を置き、ゴールデンテストを足す。
   最古の版から現行版までの連鎖テストも通す。
4. **C# 側がそのキーを直接読んでいないか**を 2 章の一覧で確認し、読んでいれば同じコミットで直す。
   その形式の**書き手が C#**（5.1 の表）なら、刻印と `--migrate-json` の導入もセットで確認する。
5. 旧形式を吸収していた `alias` / ロード後の読み替えは、変換へ移したら削除候補としてバックログに書く
   （すぐには消さない。一括アップグレード前のファイルが残っているため）。
6. `docs/asset_migration.md` の「5.2 変換の履歴」に 1 行足す。

**形式を新しく足すとき**は、上に加えて:
`kind.rs` に variant と `FormatSpec`（版の欄名・現行版・拡張子か相対パス・書き手）を足し、
読み込みの入口を 1 本に絞って `migration::load_json` を通し、
`upgrade/canonical.rs` の網羅 match に検証の腕を足す（書き忘れるとビルドが落ちる）。
拡張子が他形式と衝突する場合は `asset_relative_paths` で置き場所を指定する。

## 5. 版の履歴

### 5.1 形式の一覧（`runtime/src/engine/core/migration/kind.rs` の表が正典）

| 形式 | 版の欄 | 現行版 | 対象ファイル | 版を刻む書き手 |
|---|---|---|---|---|
| `scene` | `format_version` | 2 | `*.scene` | ランタイム（Rust） |
| `actor` | `format_version` | 2 | `*.actor` / `*.actor2d` | ランタイム（Rust） |
| `anim` | `format_version` | 1 | `*.anim` | **エディタ（C#）** |
| `material` | `format_version` | 1 | `*.mat` | **エディタ（C#）** |
| `postfx` | `format_version` | 1 | `*.postfx` | **エディタ（C#）** |
| `inputmap` | **`version`** | 2 | `*.inputmap` | **エディタ（C#）** |
| `sprite_mesh` | **`version`** | 1 | `*.sprite_mesh` | **エディタ（C#）** |
| `terrain_layers` | `format_version` | 1 | `assets/terrain/layers.json` | **エディタ（C#）** |
| `terrain_props` | `format_version` | 1 | `assets/terrain/props.json` | **エディタ（C#）** |
| `terrain_cover_materials` | `format_version` | 1 | `assets/terrain/cover_materials.json` | **エディタ（C#）** |
| `project_settings` | `format_version` | 1 | `assets/project_settings.json` | **エディタ（C#）** |

- **版の欄名が 2 通りある**のは、`.inputmap` / `.sprite_mesh` が仕組みの導入前から
  `version` で版を持っていたため。既存ファイルを 1 バイトも書き換えずに載せるため綴りを尊重する。
  新しく版を持たせる形式は `format_version` を使う。
- 拡張子が `.json` の形式は**置き場所（アセットルート相対パス）**で見分ける。
  ファイル名だけで照合すると無関係な同名ファイルを巻き込むため。
- `terrain_meta.json` / `.tvox` / `.tcover` / `.tscatter` / `.seedproj` は独自の版機構を
  すでに持っており、この仕組みには載せていない（M3 以降の課題）。

### 5.2 変換の履歴

| 形式 | 版 | 内容 |
|---|---|---|
| `.scene` | 1 | 欄なしの従来形式 |
| `.scene` | 2 | 旧 enum 表記の正規化（`ParticleEmitterComponent.blend` の `alpha`→`normal` / `additive`→`add`、同 `shape` の `point`→`pixel`、`CanvasComponent.gravity_mode` の `screen_down`→`world_down`） |
| `.actor` / `.actor2d` | 1 | 欄なしの従来形式 |
| `.actor` / `.actor2d` | 2 | `.scene` v2 と同じ（同じ変換をアクタ木へ適用する） |
| `.inputmap` | 1 | 1 アクションのバインドをすべて `bindings` に平らに並べ、`WASD` 合成軸で左右・上下を 1 件で表す |
| `.inputmap` | 2 | 軸アクションが正負グループを持つ（Axis1D は `positive`/`negative`、Axis2D は `x`/`y`）。`bindings` 内の PC の `WASD` と素のキーを展開して移す。移せないバインド（ゲームパッド等）は `bindings` に残す |

上表に無い形式（`anim` / `material` / `postfx` / `sprite_mesh` / 地形 JSON / `project_settings`）は
現行版 1 で**変換段を持たない**。「版の欄を読む・未来版を拒否する・保存で刻む」だけである。

## 6. 実装メモ（M2a 時点）

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

**版の欄名が形式ごとに違う**（`format_version` / `version`）ので、先読み用の型と刻印用の型を
欄名 1 つにつき 1 組ずつ持ち、`VersionKey` の網羅 match で振り分ける（`mod.rs`）。
表に欄名を足したら match が埋まらずビルドが落ちるので、片方だけ増やす事故は起きない。
`stamped_text_is_read_back_as_the_current_version` が全形式で「刻む側と読む側が一致する」ことを固定する。

### 差し込んだ場所（読み込みの唯一の入口）

| 形式 | 読み込み | 保存（＝刻印） |
|---|---|---|
| `.scene` | `app_base/scene.rs` の `Scene::from_json`（ファイル経路 5 か所と Play スナップショット復元が全部ここを通る） | `Scene::to_json_with_actors` |
| `.actor` / `.actor2d` | `app_base/actor_file.rs`（共通ローダ。`Scene::load_actor` / `Scene::load_actor_into` / `prefab_ops::load_actor_data_with_hash` の 3 か所が呼ぶ） | 同 `actor_file::save` |
| `.anim` | `animation/clip.rs` の `AnimationClip::from_json` | ランタイムに書き手なし → **エディタ** |
| `.mat` | `core/renderer/material_asset.rs` の `load` | 同上（雛形 `default_mat_json` は刻印済み） |
| `.postfx` | `core/renderer/postfx/asset.rs` の `PostfxAsset::from_json` | 同上（雛形 `default_postfx_json` は刻印済み） |
| `.inputmap` | `core/input/action_map.rs` の `ActionMap::parse` | 同上 |
| `.sprite_mesh` | `core/loader/sprite_mesh.rs` の `SpriteMesh::from_json` | 同上 |
| `terrain/layers.json` | `terrain/layers.rs` の `TerrainLayerSet::from_json_str` | 同上 |
| `terrain/props.json` | `terrain/scatter/props.rs` の `TerrainPropSet::from_json_str` | 同上 |
| `terrain/cover_materials.json` | `terrain/cover/material.rs` の `CoverMaterialSet::from_json_str` | 同上 |
| `project_settings.json` | **新設の共通ローダ** `app_base/project_settings.rs`（`load_text` / `load_value`） | 同上 |

- プレハブのハッシュは**生テキスト**から取る（`actor_file::load_with_raw` が生テキストも返す）。
  順序は「読む → ハッシュ → 変換」。実装は `app_base/prefab_hash.rs` の 1 か所
  （実行時の stale 判定と一括アップグレードの貼り直しが同じ関数を使う）。
- `core/loader/async_loader.rs` の `.actor` 走査は `model_path` を拾うだけなので変換を通さない
  （キー名を変える変換を足すときはそこも直す。コード内にコメントあり）。
- **`project_settings.json` は共通ローダに集約した**。以前は `app/app_init.rs` の 4 か所
  （解像度・プラグイン・シーンレジストリ・グラフィックス設定＋`start_scene`）と
  `core/loader/async_loader.rs` の `parse_streaming_config_raw` が別々に読んでいた。
  `load_text()` は変換済みテキスト（読めなければ空文字＝既定へ落ちる）、
  `load_value()` は変換済み `Value`（読めなければ空オブジェクト）を返す。
- **`.inputmap` の v1→v2 は変換段へ移した**。以前は `action_map.rs` の
  `migrate_axis1d_bindings` / `migrate_axis2d_bindings` が読み込み後の型の上で移行していたが、
  `migration/steps/inputmap/v1_to_v2.rs` が JSON のまま行う形に統一した
  （`ActionMap::parse` は v2 の読み取りだけを行う）。
  **移せないバインド（ゲームパッド・PC 以外）は `bindings` に残す**。読み込みでは無視される値だが、
  一括アップグレードはこの結果をファイルへ書き戻すので、捨てるとディスクからも消えてしまう。
- `.sprite_mesh` は未来版を `SpriteMeshError::UnsupportedVersion` に写して返す（従来のエラー種別を保つ）。

### 保存経路（`safe_write`）

`safe_write`（`.tmp` へ書き切って rename、必要なら `.backup/` 世代）を通るのは:

| 対象 | 世代バックアップ | 備考 |
|---|---|---|
| `.scene` / `.actor` / `.actor2d` | あり | M1 から |
| `terrain_meta.json` | あり | 数 KB の JSON なので世代を持っても安い |
| `.tcover` / `.tscatter` | **なし**（原子的置換のみ） | 1 チャンク＝1 バイナリで数百ファイル・数十 MB になる。世代を複製するとプロジェクトが肥大化し VCS の転送量も跳ね上がる |
| `.tvox` | **未対応**（`std::fs::write` のまま） | `app/terrain_ops.rs` の 3 か所。別作業の未コミット変更があるため今回は触っていない |

### 一括アップグレード

`SEED.exe --upgrade-project <プロジェクト|.seedproj|assets ルート> [--dry-run]`。
ウィンドウも GPU も作らずに終了する（`main` の入口で `migration::cli::run_if_requested` が分岐する）。

- 対象は `assets/` 配下の **5.1 の表にある全形式**。ドットで始まるフォルダ（`.backup` など）は入らない。
  拡張子で見分けられない形式は**アセットルート相対パスの完全一致**で拾う。
- 出力は 1 行 1 件の JSON ＋ 最後に集計 1 行。行の種別は `kind` で見分ける
  （形式名 / `prefab_hash` / `summary` / `error`）。`failed` か `future_version` があれば終了コードは非 0。
- **版の欄が物理的に無いファイルは、実変換が要らなくても書き換える**。
  「欄が無い＝v1」は暗黙の規約なので、版の 1 行を刻んでおくと次に版を上げたときに規約へ頼らずに済む。
- **差分を最小にする**: 変換段が中身を 1 つも変えなかったファイルは、元の整形を保ったまま
  **版の 1 行だけ**を差し込む（改行コードは元ファイルに合わせる。CRLF のファイルへ LF を混ぜない）。
  実際に変換が入ったファイルだけ本体の型を経由して書き直す
  （`Value` をそのまま書くと欄がアルファベット順に並び替わり、全行が差分になるため）。
  書き直した場合は「保存形式の正規化により、変換手順以外の差分も含まれます」と `message` に出る。
- **ランタイムに保存側の実装が無い形式**（`.anim` / 地形 JSON / `.inputmap` など）は正準テキストを
  作れないので、`Value` をそのまま pretty 出力する。この経路に入るのは実変換が走ったときだけで、
  `message` に「欄の並びが書き直されています」と出る。
- 本体の型として読めないファイルは `failed` にして**書き換えない**（安全弁）。
  形式ごとの検証は `upgrade/canonical.rs` の網羅 match 1 か所にまとめてある。

### 一括アップグレード後の `prefab_hash` 貼り直し

`.actor` の生テキストが変わると `prefab_hash`（FNV）も変わるため、シーン内インスタンスの
`prefab_hash` が古くなり、エディタで「プレハブが更新された」と一斉に表示される。
そこで全ファイルの処理後に `upgrade/prefab_rehash.rs` が貼り直す。

- **アップグレード前のファイルと同期していたインスタンスだけ**を貼り直す。
  焼き込まれた値が「変換前のファイルのハッシュ」と一致するものが対象。
  一致しないインスタンスはアップグレードとは無関係に**本当に古い**ので、古い値のまま残す
  （ここで無条件に貼り直すと、本物の更新通知を握りつぶしてしまう）。
- 貼り直しは**テキストの上で該当する値だけ**を差し替える。`SceneData` を経由して書き直すと
  既定値の明示化や欄の並びで数千行の差分になるため。キーが `prefab_hash` で、値が 16 桁の
  16 進数のものだけを対象にする（別のキーに同じ文字列があっても触らない）。
- 結果は `{"kind":"prefab_hash","path":…,"updated":N,"message":…}` の行で出し、
  集計行に `prefab_hash_scenes` / `prefab_hash_updated` が入る。
- dry-run では `.actor` を書いていないので貼り直しも走らない。

### M2a での挙動の変更点（互換性）

| 変更 | 影響 |
|---|---|
| 版の欄を持つ全形式で**未来版を拒否**するようになった | 新しいエンジンで保存されたファイルを古いエンジンで開くと、その形式のロードが失敗する（`.mat` / `.postfx` は `None`＝スキップ、地形 JSON は既定セット、`.inputmap` は空マップ、`project_settings.json` は既定値）。現行版は全形式 1〜2 なので、**未来版のファイルはまだ存在しない** |
| `.sprite_mesh` の明示的な `"version": 0` を弾くようになった | 「欄なし」と「明示的な 0」を区別できるようになったため。欄なしのファイルは従来どおり読める。実データに `"version": 0` は無い |
| `.anim` のエラーメッセージが `"{path}: JSON parse error: …"` から `"{path}: …"` に変わった | ログ表示だけ。文字列で分岐している箇所は無い |
| 地形 JSON / `.postfx` の `from_json*` の失敗型が `serde_json::Error` から `String` になった | 呼び出し側はいずれも `{e}` で出すだけなので表示は同じ |
| `default_mat_json()` / `default_postfx_json()` が `format_version` 付きの雛形を返すようになった | Rust 側では未使用（雛形の正典としてのみ存在）。エディタが真似できるようにするため |
| 一括アップグレードが**版の欄が無いファイルを全部書き換える**ようになった | 初回実行で対象ファイルすべてに版の 1 行が入る（実データ 71 件中 70 件）。差分はその 1 行だけ |
| 一括アップグレードの出力に `prefab_hash` 行と、集計の `prefab_hash_scenes` / `prefab_hash_updated` が増えた | 読み手は `kind` で行を見分けること |

**古いエディタで新しいファイルを開いた場合**: C# の読み手は現在すべて「知らないキーは無視」なので、
版の欄が増えただけのファイルはそのまま読める。エディタが保存し直すと版の欄が消えるが、
ランタイムは欄なしを v1 として読むため壊れない（次の一括アップグレードで再び刻まれる）。

### `--migrate-json`（エディタから 1 件だけ変換する口）

`SEED.exe --migrate-json <kind>`。ウィンドウも GPU も作らず、ファイルにも一切触らない。

| 項目 | 内容 |
|---|---|
| 引数 | `--migrate-json <kind>` または `--migrate-json=<kind>`。`kind` は 5.1 の表の形式名 |
| 入力 | 標準入力に JSON テキスト 1 件（先頭 BOM は許容） |
| 出力 | 標準出力に、現行版へ変換した pretty JSON（版の欄も現行版に更新済み） |
| 失敗 | 標準エラーへ 1 行 JSON `{"kind":"error","error":"<code>","message":"<日本語>"}` |
| 終了コード | `0`=成功 / `1`=未来版（`future_version`） / `2`=引数が不正（`bad_usage`） / `3`=解析・変換の失敗（`failed`） |

出力の欄の並びは `serde_json` の既定（キー名の昇順）になり、**元の整形は保たれない**。
ファイルの整形を保ったまま書き戻したいときは `--upgrade-project` を使うこと。

## 6.5 エディタ側の実装メモ（M2b の担当者へ）

ランタイム側（M2a）は完了している。エディタ（C#）側に残っているのは次の 3 つ。

### (1) 保存時に版を刻む

C# が書き出す形式は、保存時にトップレベルへ版の欄を足す。**欄名と値は 5.1 の表のとおり**。

| ファイル | 足す欄 | 値 |
|---|---|---|
| `*.anim` | `"format_version"` | `1` |
| `*.mat` | `"format_version"` | `1` |
| `*.postfx` | `"format_version"` | `1` |
| `assets/terrain/layers.json` | `"format_version"` | `1` |
| `assets/terrain/props.json` | `"format_version"` | `1` |
| `assets/terrain/cover_materials.json` | `"format_version"` | `1` |
| `assets/project_settings.json` | `"format_version"` | `1` |
| `*.inputmap` | `"version"` | `2`（既に書いている。綴りを変えないこと） |
| `*.sprite_mesh` | `"version"` | `1`（既に書いている。綴りを変えないこと） |

- **欄はトップレベルの先頭に置く**（差分を見たときに版がすぐ分かる）。
- 値を**ハードコードしない**のが望ましい。正典は `kind.rs` の表で、`--migrate-json` を通せば
  現行版が刻まれた JSON が返るので、それをそのまま書けば追従できる。
- 刻印していない間も互換は保たれる（ランタイムは欄なしを v1 として読む）。
  ただし一括アップグレードのたびに「版の 1 行を足す」差分が出続けるので、刻むまでが移行期間。

### (2) C# の読み手を `--migrate-json` へ通す

Rust の変換を通らない C# の読み手（docs 2 章の一覧）は、ファイルを読んだら
**解釈の前に**この口を通す。通し方:

1. `SEED.exe --migrate-json <kind>` を起動（`UseShellExecute=false`、3 本のストリームをリダイレクト）。
2. 標準入力へファイルの中身を書いて閉じる。
3. 標準出力を読み切る → それが現行版の JSON。
4. 終了コードで分岐する。
   - `0` … 標準出力を使う
   - `1` … **新しいエンジンで保存されたファイル**。開かずにユーザーへ知らせる（標準エラーの `message` をそのまま出してよい）
   - `2` … 呼び出し側のバグ（形式名の綴り違い）
   - `3` … ファイルが壊れている／その形式として読めない

**デッドロックに注意**: 標準入力を書き切って閉じる前に標準出力を読み始めること
（`WaitForExit` を先に呼ぶと、出力バッファが埋まった時点で双方が止まる）。

### (3) 一括アップグレードのメニューと結果表示

`SEED.exe --upgrade-project <プロジェクト> [--dry-run]` を起動し、標準出力を 1 行ずつ JSON として読む。

- 行の種別は `kind` で見分ける。`summary` が集計、`prefab_hash` がプレハブの版の貼り直し、
  `error` が致命的な失敗（引数が不正など）、それ以外は形式名＝ファイル 1 件の結果。
- ファイル 1 件の行: `{"kind":"scene","path":"assets/…","from":1,"to":2,"status":"upgraded","message":""}`。
  `status` は `upgraded` / `up_to_date` / `future_version` / `failed`。
- 終了コードが非 0 なら `failed` か `future_version` が含まれている。
- **先に `--dry-run` で見せてから実行する**運用を推奨（何件書き換わるかが分かる）。
- 実行後は `.actor` を参照するシーンの `prefab_hash` が貼り直されるため、
  VCS パネルには `.scene` も変更として並ぶ。これは正常。

## 6.6 エディタ側の実装（M2b）

6.5 の 3 項目をエディタ（C#）へ入れたときの構成。置き場は `editor/src/Migration/`。

### 置き場と役割

| ファイル | 役割 |
|---|---|
| `AssetFormat.cs` | **`kind.rs` の表の写し**（形式・版の欄名・現行版・書き手）。`AssetFormats.All` が一覧 |
| `AssetVersionPeek.cs` | 版の欄だけを安く覗く。`Utf8JsonReader` でトップレベルを走査し、入れ子は読み飛ばす |
| `AssetVersionStamp.cs` | `JsonObject` の**先頭**へ現行版を置いた新しいオブジェクトを作る（地形 JSON が使う） |
| `MigrateJsonRunner.cs` | `--migrate-json <kind>` の起動。終了コード 0/1/2/3 を `MigrateJsonOutcome` へ写す |
| `ProjectUpgradeRunner.cs` | `--upgrade-project [--dry-run]` の起動 |
| `ProjectUpgradeReport.cs` | JSON Lines の解釈（`kind` で行種別を判定。解釈できない行は捨てずに別枠へ） |
| `AssetMigrationGateway.cs` | **C# の読み手が通る唯一の門**。覗く → 現行版なら素通し → 古ければ変換 → 未来版は開かせない |
| `ProjectUpgradeNotice.cs` | プロジェクトを開いた直後の dry-run と案内 |
| `MigrationMessages.cs` | 文言（ダイアログ・トースト・ログ）の集約 |
| `IMigrationNotifier.cs` / `Presentation/MigrationNotifier.cs` | 提示の境界と WPF 実装（モーダル＝`EditorDialogs` / 案内＝トースト） |
| `Presentation/ProjectUpgradeWindow.cs` | 一括アップグレードのダイアログ（dry-run → 実行） |

`Presentation/` 以外は **WPF 非依存**。`ProjectSettingsData` / `InputMapData` /
`AnimClipIO` / `SpriteMeshFile` が単体テストへリンクされているため、
この層が WPF を引き込むとテストがビルドできなくなる（それが検知器になっている）。

### 版の表がずれないようにする仕組み

C# は `kind.rs` の**写し**を持つ（版を刻むたびに exe を起動しないため）。
写しである以上ずれるので、`editor/tests/MigrationTests` が `kind.rs` を読んで
形式・欄名・現行版・書き手の 4 つを突き合わせる。**片方だけ直すとテストが落ちる**。

### 刻印を入れた書き手

| ファイル | 書き手 | 刻み方 |
|---|---|---|
| `*.anim` | `Panels/AnimationTimeline/AnimClipIO.Serialize` | `Utf8JsonWriter` で最初に書く |
| `*.mat` / `*.postfx` の雛形 | `CreateItemWindow.OnCreateMaterial` / `OnCreatePostFX` | 雛形テキストの先頭行 |
| `terrain/layers.json` | `Terrain/TerrainLayersDocument.ToJsonString` | `AssetVersionStamp.WithVersionFirst` |
| `terrain/props.json` | `Terrain/TerrainPropsDocument.ToJsonString` | 同上 |
| `project_settings.json` | `ProjectSettings/ProjectSettingsData` | `FormatVersion` プロパティ＋`[JsonPropertyOrder]` で先頭 |
| `*.inputmap` | `InputMap/InputMapData` | `Version` プロパティ（クラスの先頭に宣言） |
| `*.sprite_mesh` | `Panels/SpriteRig/IO/SpriteMeshFile` | `SpriteMeshDto.Version`（DTO の先頭） |

値は**すべて `AssetFormats` の表から取る**（`SpriteMeshFile.SchemaVersion` も表を指す）。
`terrain/cover_materials.json` はエディタに書き手が無い（読むだけ）。

### 門を通した読み手

`.anim` / `.inputmap` / `.sprite_mesh` / 地形 3 種 / `project_settings.json` の 7 経路。
どれも **まず版を覗き、現行版なら `SEED.exe` を起動しない**（実データはほぼこの経路）。

失敗の返し方は読み手の性格で 2 通りに分かれる。

| 読み手 | 失敗の返し方 | 門へ渡す `notify` |
|---|---|---|
| `AnimClipIO.Load` / `SpriteMeshFile.Load` | 例外（`InvalidDataException`） | **false**（呼び出し元がダイアログを出すので二重に出さない） |
| `InputMapData.LoadFrom` / `ProjectSettingsData.LoadFrom` / 地形ドキュメント | 既定値 ＋ `IsUnreadable = true` | true |

**`IsUnreadable` の扱いが要点**。これらの読み手は失敗を握りつぶして既定値を返すため、
そのまま編集させると「空の設定を保存して全部消す」事故になる。
`InputMapEditorWindow` / `ProjectSettingsWindow` は `OnLoaded` で閉じ、
`TerrainSettingsWindow` は「保存して適用」を拒否する。

`.inputmap` の C# 側 v1→v2（`InputAction.MigrateFromV1` / `ExpandWasd`）は**削除した**。
変換は Rust の `migration/steps/inputmap/v1_to_v2.rs` 一本。ここへ書き戻さないこと。

### 保存の原子化

上の書き手（＋`CreateItemWindow` の新規作成）は `File.WriteAllText` をやめ、
`Assets/SafeFileWriter.WriteAllTextAtomic`（旧版を `.backup/` へ退避 → `.tmp` へ書き切って rename）
を通す。アセットルートを渡すと世代バックアップが `<assets>/.backup/` に集まる
（渡さないとファイルの隣に `.backup` が増える）。

### 一括アップグレードのメニュー

「ツール → プロジェクトの形式をアップグレード...」（`MainWindow.Migration.cs`）。

1. 開くと **dry-run** が走り、形式ごとの件数・対象ファイルの一覧・
   未来版／失敗を別枠で表示する（この時点で 1 バイトも書かない）
2. 「アップグレードを実行」を押すと、**まず対象ファイルのロックを 1 回でまとめて確認**する
   （`LockGatekeeper.DecideForBulkWriteAsync`）。他の人のロックがあれば止める
3. 実行後は結果（更新件数・`prefab_hash` の貼り直し件数・失敗）を出し、
   `VersionControlService.RequestRefresh()` で VCS パネルへ変更を並べる

ロックの確認に `EnsureAllWritable` を使わないのは、あれが 1 件ずつ照会して
**ロックを取りに行く**ため。数十〜数百ファイルでは往復が長く、
実行した人が大量のロックを握ったままになる。判定表は送信ゲートと同じものを
`LockGatePolicy.DecideForBulkWrite` として共有している（文言だけ差し替え）。

### プロジェクトを開いたときの案内

`MainWindow.OnWindowLoaded` → `ProjectUpgradeNotice.ScanInBackground`。
バックグラウンドで dry-run を 1 回走らせ、古い形式があれば
Output ログとトーストで「古い形式のファイルが n 件あります。ツール → … で更新できます」と知らせる。
**待たない**（プロジェクトを開く処理を遅くしない）。ランタイム exe が無ければ黙ってログだけ。
ヘッドレス起動では通知しない。

### M2b で入った挙動の変更点（互換性）

| 変更 | 影響 |
|---|---|
| C# が書くファイルに版の欄が入る | 次に保存したファイルから 1 行増える。ランタイムは従来どおり読める |
| `.inputmap` の v1→v2 が Rust 経由になった | **ランタイム exe が未ビルドだと v1 の `.inputmap` を開けない**（従来は C# 側で移行していた）。一括アップグレードを 1 回通せば以後は v2 なので起きない |
| 未来版のファイルを開かなくなった | 既定値で開いて上書き保存する事故を防ぐため。ダイアログで知らせる |
| 保存が原子的置換になった | `<assets>/.backup/` に世代が残る（`.anim` / `.inputmap` / `.sprite_mesh` / 地形 JSON / `project_settings.json`）|

## 7. 段階

- **M1（完了）**: 仕組み（kind / registry / runner / error / json_walk）、`.scene` と `.actor` の読み込みフックと保存時の刻印、
  `ActorData` 共通ローダ、未来版の拒否、最初の実変換（旧 enum 表記の正規化）、一括アップグレードの CLI、ゴールデンテスト。
- **M2a（完了・ランタイム側）**: 対象形式の拡大（`.anim` / `.mat` / `.postfx` / `.inputmap` / `.sprite_mesh` /
  地形 JSON / `project_settings.json`）、形式ごとの版の欄名（`format_version` / `version`）、
  `project_settings.json` の共通ローダ、`.inputmap` の変換段化（Rust 側の二重実装の解消）、
  `--migrate-json` の追加、一括アップグレードの拡大と `prefab_hash` の貼り直し、
  `terrain_meta.json` / `.tcover` / `.tscatter` を `safe_write` へ。
- **M2b（完了・エディタ側）**: 6.5 の 3 項目（C# の書き手が版を刻む・C# の読み手を
  `--migrate-json` へ通す・一括アップグレードのメニューと結果表示）と、
  プロジェクトを開いたときの案内、C# 側 `.inputmap` 変換の削除、保存の原子化。
  実装の地図は 6.6 章。検証は `editor/tests/MigrationTests`
  （`kind.rs` との突き合わせを含む）。
  `engine_version` の確認ダイアログからの導線は未着手（`docs/backlog.md`）。
- **M3**: 構造的な旧対応（`RigidbodyComponent` の吸収など）を変換へ移す。パッケージ化のときに変換済みで同梱する。
  一括アップグレード後に `#[serde(alias)]` の削除候補を片付ける。
  `.tvox` を `safe_write` へ寄せる（`app/terrain_ops.rs` の 3 か所。M2a では別作業と衝突するため見送り）。
  `terrain_meta.json` / `.tvox` / `.tcover` / `.tscatter` / `.seedproj` の独自版機構を仕組みへ載せるかの判断。
