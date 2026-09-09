# パッケージ化（配布ビルド）

エディタの「パッケージ化」ウィンドウ（`editor/src/Packaging/`）が行う処理と、
`assets.pak` に何が入るかの規則をまとめる。

関連ファイル:

| ファイル | 役割 |
|---|---|
| `editor/src/Packaging/PackagingWindow.xaml.cs` | UI・cargo build 実行・進捗表示 |
| `editor/src/Packaging/PackagingData.cs` | 設定の永続化（`{assets}/packaging_settings.json`） |
| `editor/src/Packaging/Collect/PackagingRules.cs` | **収録規則の正典**（走査拡張子・同伴ファイル・既定の除外） |
| `editor/src/Packaging/Collect/AssetReferenceScanner.cs` | テキスト 1 本からの参照抽出 |
| `editor/src/Packaging/Collect/AssetCollector.cs` | 参照グラフの閉包を取り、収録ファイルを決める |
| `editor/src/Packaging/Collect/AssetPackagingSettings.cs` | 収録ルールのユーザー設定 |
| `editor/src/Packaging/Pak/PakWriter.cs` | `assets.pak` の書き出し |
| `editor/src/Packaging/Pak/AssetPathRewriter.cs` | 絶対パス → `assets://` の書き換え |
| `runtime/src/engine/pak.rs` | ランタイム側の PAK リーダー（フォーマットの正典） |
| `editor/tests/PackagingCollectorTests/` | 上記の単体テストとドライラン |

---

## 1. ビルドの流れ

1. **cargo build** — 選択したプラットフォームのターゲットでランタイムをビルドする。
   x64 Windows をホストが x64 Windows のときにビルドする場合だけ `--target` を**付けない**。
   `--target` を付けると cargo は `target/<triple>/release/` を使い、
   普段の `cargo run` が使う `target/release/` とビルドキャッシュを共有しないため、
   同じコードを 2 回フルビルドすることになる。
2. **バイナリのコピー** — `SEED.exe` を `{出力先}/{ゲーム名}/{ゲーム名}.exe` へ複製する。
3. **収録アセットの収集** — 参照グラフを辿って `assets.pak` に入れるファイルを決める（§2）。
4. **PAK 書き出し** — ストリーミングで `assets.pak` を書く（§4）。

各フェーズの所要秒数はログに `[時間] フェーズ名: N.N 秒` の形で出る。

---

## 2. 収録ファイルの決め方（参照グラフ）

「アセットフォルダを丸ごと詰める」のではなく、**起点から到達できるものだけ**を入れる。

### 起点

| 起点 | 内容 |
|---|---|
| `project_settings.json` | ランタイムが必ず読む。常に同梱する |
| `start_scene` / `scenes[].path` | 登録シーン。実体が無いものは警告して飛ばす |
| エンジン内蔵参照 | `runtime/src` の `.rs` に書かれた `assets://` のうち**実在するもの**（`terrain/layers.json` など）。テスト用ダミーは実在しないので自然に落ちる |
| 追加同梱フォルダ | 設定で指定したフォルダを丸ごと（§3 の逃げ道） |
| 常時同梱拡張子 | 既定は `.cs`。除外ルールには従う |

`.cs` を常時同梱にしているのは、スクリプトが**アセットルート配下をまとめてコンパイル**する
方式（`runtime/src/engine/core/scripting/mod.rs` の `compile_scripts`）だからで、
参照されている分だけ入れるとクラス参照が解決できずコンパイルが丸ごと落ちる。

### 参照の抽出（4 系統）

到達したファイルのうち、`PackagingRules.ScannableExtensions` にある拡張子だけを
テキストとして開き、次を参照として取り出す
（ランタイム `asset_fs::normalize_asset_path` の分類に対応）。

1. **`assets://` 形式** — 終端は `"` `'` 空白 `<` `>` `)`。**確実な参照**。
2. **アセットルートの絶対パス** — 表記ゆれ 4 形式すべて。**確実な参照**。
   - `C:/proj/assets/…`（スラッシュ）
   - `C:\proj\assets\…`（バックスラッシュ）
   - `C:\\proj\\assets\\…`（JSON エスケープ）
   - `C:\/proj\/assets\/…`（JSON エスケープ）
3. **glTF の `"uri"`** — `.bin` やテクスチャ。glTF ファイルからの相対。`%20` などの URL エンコードを解く。**確実な参照**。
4. **相対パス（推測）** — 引用符で囲まれた文字列のうち、拡張子を持ち、
   「参照元フォルダ基準」または「アセットルート基準」で**実在するもの**だけを採用する。
   `terrain/layers.json` の `"mainGame/terrain/brush/leaf2.png"` のような表記がこれ。
   外れても警告は出さない（数値・識別子の誤検出を警告にしないため）。

C# スクリプト（`.cs`）も走査対象なので、文字列リテラルに書かれた
`assets://…`（既定値・定数）は 1 の経路で拾える。

**コメント中の参照**（`// … assets://a/b.txt）から読む。` のように終端文字が現れないまま
日本語が続く形）は、拡張子の切れ目まで詰めた前方部分が実在すればそれを採用する。

### 参照グラフには現れない同伴ファイル

ランタイムが「拡張子を差し替えて隣を読む」種類の依存は、規則として
`PackagingRules` に持っている。ここに載せ忘れると**パッケージ版だけ壊れる**。

| きっかけ | 足すもの | 根拠 |
|---|---|---|
| `.tvox` | 同名の `.tscatter` / `.tcover` | `terrain_ops.rs`（拡張子を差し替えて読む） |
| `.tvox` | 同じフォルダの `terrain_meta.json` | `terrain_meta_ops.rs` |
| `.obj` | 同名の `.mtl` | OBJ のマテリアル定義 |

なお地形チャンクは**シーンの `TerrainChunkComponent` に書かれた `tvox_path` だけ**が
読み込まれる（フォルダ走査ではない。`terrain_ops.rs` の `walk`）。
そのため、シーンに対応するチャンクアクタが無い `.tvox` は収録されない。

### 拡張子の無い `assets://` 参照

`assets://terrain/Scene1` のようにフォルダを指す参照、および
スクリプトが文字列連結で組み立てるパスの前半部分は、
**同名のフォルダが存在すればその配下を丸ごと同梱**する。
フォルダも無ければ欠落報告はしない（文章の断片と区別できないため）。

---

## 3. 設定項目（パッケージ化ウィンドウ「アセット収録」）

`{assets}/packaging_settings.json` の `assets` セクションに保存される。

| 項目 | JSON キー | 既定値 | 意味 |
|---|---|---|---|
| 全ファイル同梱 | `include_all_files` | `false` | 参照解決を行わず全部入れる（従来の挙動。緊急避難用） |
| 除外フォルダ | `excluded_folders` | `.backup`, `templates`, `assets_realdir_backup_*`, `__MACOSX` | パスのどこかの階層名が一致したら除外（`*` 可） |
| 除外拡張子 | `excluded_extensions` | `.lock` `.blend` `.blend1` `.zip` `.psd` `.tmp` `.bak` | 未参照なら捨てる拡張子 |
| 除外ファイル名 | `excluded_file_names` | `.DS_Store`, `._*`, `Thumbs.db`, `desktop.ini` | OS が作るゴミファイル（`*` 可） |
| 追加同梱フォルダ | `additional_folders` | （空） | 参照グラフで辿れないアセットを丸ごと入れる**逃げ道** |
| 常時同梱拡張子 | `always_included_extensions` | `.cs` | 参照が無くても入れる拡張子（除外ルールには従う） |

### 除外は参照より弱い

除外ルールは「**未参照ファイルの掃除**」であって、参照より優先しない。
参照されていれば除外設定に当たっていても同梱し、ログに警告として出す。
たとえば `templates` は既定で除外だが、`title.scene` が
`assets://templates/fonts/Digital/NikkyouSans-mLKax.ttf` を参照しているので、
そのフォントだけは入る。

例外は 1 つだけで、`packaging_settings.json`（エディタ専用設定。出力先の絶対パスを含む）は
参照されていても**決して同梱しない**（`PackagingRules.NeverIncludedRelativePaths`）。

---

## 4. PAK の書き出し

フォーマットは `runtime/src/engine/pak.rs` の `PakReader` が読む形式で固定
（magic `"SEED"` / version 1 / entry_count / エントリ表 / データ部）。**変更しない**。

メモリを使わないよう 2 段構えで書く。

1. `.json` `.scene` `.actor` `.actor2d` `.inputmap` `.anim` `.icons` は
   絶対パスを `assets://` へ書き換えるためサイズが変わるので、**先にメモリ上で変換**して
   サイズを確定させる（合計でも数 MB）。
2. ヘッダーとエントリ表を書いてから、残りのファイルを 1 本ずつ**ストリームコピー**する。

エントリ表に書いたサイズと実際の書き込み量は必ず一致させる。
書き出し中に外部からファイルが変わった場合は 0 埋め／切り捨てで整合させ、件数を警告に出す
（PAK 自体は壊れない）。

### 書き換え後のパス表記について

エスケープ済みの絶対パス `C:\\proj\\assets\\a\\b.glb` は
`assets://a\\b.glb` になる（スキームより後ろの区切りは元のまま）。
ランタイムの `PakReader::read` が `\` を `/` に正規化して読むため、これで動く（従来と同じ挙動）。

---

## 5. ログの読み方

```
── 収録アセットの収集 ──
⚠ 登録シーンの実体がありません（スキップ）: assets://demo/scenes/demo_title.scene
エンジン内蔵参照: 4 ファイルを追加
常時同梱拡張子（.cs）: 80 ファイルを追加
参照走査: 146 ファイルを解析
[時間] 収録アセットの収集: 0.2 秒
収録: 325 ファイル / 51.5 MB
除外: 2120 ファイル / 914.1 MB（アセット全体 2445 ファイル / 965.6 MB）
⚠ 除外ルールに一致するが参照されているため同梱: 5 ファイル
❌ 参照先が見つからないパス: 4 件
    actors/kamome.actor  ← mainGame/scripts/KamomeManager.cs
```

| 行 | 見方 |
|---|---|
| `⚠ 登録シーンの実体がありません` | `project_settings.json` の `scenes` が実体と食い違っている。登録を消すか実体を作る |
| `⚠ 除外ルールに一致するが参照されているため同梱` | 除外設定が実態と合っていない。放置しても動くが、設定を見直す材料 |
| `❌ 参照先が見つからないパス` | **パッケージ版で読み込み失敗になる箇所**。`← ` の右が参照元ファイル |
| `[時間] …` | フェーズごとの所要秒数 |

---

## 6. 収録内容の事前確認（ドライラン）

PAK を書かずに「何が入って何が落ちるか」だけを確認できる。

```
dotnet run --project editor/tests/PackagingCollectorTests -- "<アセットルート>" "<runtime/src>"
```

`<アセットルート>` には**エディタが使うパスをそのまま**渡すこと
（このリポジトリでは `runtime/assets`。`runtime/assets` は実体へのジャンクションなので、
実体側のパスを渡すと `.scene` に書かれた絶対パス参照が一致せず、モデルが丸ごと落ちて見える）。

引数なしで実行すると単体テストが走る。

```
dotnet run --project editor/tests/PackagingCollectorTests
```

---

## 7. 既知の制限

- **パッケージ版ではユーザースクリプト（`.cs`）がコンパイルされない**。
  詳細と対策案は `docs/backlog.md` を参照。`.cs` を PAK に入れる準備だけは済んでいる。
- `app_init.rs` の `project_settings.json` 読み込みは `std::fs` で exe 隣の `assets/` を見るため、
  PAK モードではウィンドウサイズ・プラグイン設定が既定値になる（同じく backlog 参照）。
- 新しいアセット形式を足したときは、`PackagingRules` の
  `ScannableExtensions` / `SiblingExtensions` / `FolderCompanions` の追従を忘れないこと。
  登録漏れは**ビルドエラーにならず**、パッケージ版だけが壊れる形で出る。
