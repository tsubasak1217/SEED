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
| `.actor` | 1 | 欄なしの従来形式 |

（実装時に追記する）

## 6. 段階

- **M1**: 仕組み（kind / registry / runner / error / json_walk）、`.scene` と `.actor` の読み込みフックと保存時の刻印、
  `ActorData` 共通ローダ、未来版の拒否、最初の実変換（旧 enum 表記の正規化など単純なもの）、一括アップグレードの CLI、ゴールデンテスト。
- **M2**: エディタのメニュー（一括アップグレードの実行と結果表示）、`engine_version` の確認ダイアログからの導線、
  `.anim` / 地形 JSON / `.mat` / `.postfx` への拡大、`.inputmap` の二重実装の解消、保存経路を `safe_write` に寄せる。
- **M3**: 構造的な旧対応（`RigidbodyComponent` の吸収など）を変換へ移す。パッケージ化のときに変換済みで同梱する。
