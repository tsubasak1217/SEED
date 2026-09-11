# テンプレートライブラリ

新規作成用のテンプレート一式（シーン・アクター・モデル・シェーダ・フォント・地形…）を
**プロジェクトの外（エンジン側）** に置き、エディタから「好きなものだけをプロジェクトへ
コピーする」ための仕組み。本ドキュメントがこの機能の正典。

---

## 1. なぜプロジェクトの外に出すのか

プロジェクト機構の導入で、ゲーム 1 本は次の形になった。

```
<ProjectRoot>/
  <Name>.seedproj
  assets/      ← アセットルート。そのゲームが実際に使う物だけ
  plugins/
```

`templates/` は「新規作成の見本」であって、特定のゲームの資産ではない。
アセットルートの中に置いたままだと、

- 使っていないテンプレートがプロジェクトのファイル一覧に混ざる
- パッケージ化のたびに「参照されていないから除外」を判定させ続けることになる
- ゲームを増やすたびに同じ 500 MB が複製される

ので、**エンジン側の共有ライブラリ**へ出し、必要な物だけをコピーして使う形にした。

---

## 2. 置き場所

リポジトリ直下の **`<repo>/templates/`**。エディタは次の順で解決する
（`editor/src/Templates/TemplateLibraryLocator.cs`）。

| 優先 | 場所 | 用途 |
|---|---|---|
| 0 | 環境変数 `SEED_TEMPLATE_LIBRARY` が指すフォルダ（実在するときだけ） | 別のテンプレート集を差し替えて試す / 自動テスト |
| 1 | exe から `..\..\..\..\templates`（= リポジトリ直下） | 開発時 |
| 2 | exe の隣の `templates/` | リリース時（配布物に同梱する場合） |

`MainWindow.ResolveRuntimePath()` と同じ流儀。どれも見つからなければ `Resolve()` は
`null` を返すので、呼び出し側はメニューを無効化するなどの扱いができる。

---

## 3. ライブラリの構成

**フォルダ構成はアセットルートと同じ形**である。これが設計の要。

```
templates/
  scenes/    physicsTest.scene, animation.scene, …
  actors/    NewActor.actor, camera.actor, …
  models/    BrainStem.glb, man/, …
  textures/  symmetryORE1.png, …
  shaders/   toon.wgsl, magma.wgsl, …
  skybox/    *.hdr
  fonts/     Digital/, Dot/, English/, Pop/, Soft/
  terrain/   HeightMap/, layers.json, props.json, …
  input/     playerInput.inputmap, …
  scripts/   FollowCamera.cs, …
```

- `templates/shaders/toon.wgsl` は、プロジェクトへは **`assets/shaders/toon.wgsl`** としてコピーされる。
  `templates/` という接頭辞は**付かない**。
- ライブラリ内のシーンは `assets://shaders/toon.wgsl` のように**ルート相対**で参照している。
  つまり「ライブラリのルートをアセットルートとみなす」と参照がそのまま解決できる。
  依存の閉包計算にパッケージ化と同じ `AssetCollector` を使えるのはこのため（§5）。

### カテゴリの表示名

トップレベルフォルダ名がそのままカテゴリになる。表示名は
`editor/src/Templates/TemplateCategoryNames.cs` のデータ表で決める。

| フォルダ | 表示名 | | フォルダ | 表示名 |
|---|---|---|---|---|
| `scenes` | シーン | | `skybox` | スカイボックス |
| `actors` | アクター | | `fonts` | フォント |
| `models` | モデル | | `terrain` | 地形 |
| `textures` | テクスチャ | | `input` | 入力設定 |
| `shaders` | シェーダ | | `scripts` | スクリプト |

**表に無いフォルダはフォルダ名のまま表示される**（既知カテゴリの後ろに並ぶ）。
フォルダを 1 つ足せばカテゴリが 1 つ増えるので、走査やインポートのコードは触らなくてよい。
表示名を付けたいときだけ上記の表に 1 行足す。

### エントリの粒度

**各カテゴリフォルダ直下の子（ファイルまたはフォルダ）が 1 エントリ**。

| 例 | 種別 | 単位 |
|---|---|---|
| `scenes/physicsTest.scene` | ファイル | そのファイル |
| `shaders/toon.wgsl` | ファイル | そのファイル |
| `fonts/Digital/` | フォルダ | 配下すべて |
| `terrain/HeightMap/` | フォルダ | 配下すべて |

フォント・地形のように「フォルダ 1 式でようやく使える」テンプレートと、
シーン・シェーダのように 1 ファイルで完結するテンプレートが混在しているため、この粒度にしている。
名前の先頭がドットのファイル・フォルダ（`.backup` や OS のゴミファイル）は一覧に出さない。

---

## 4. 画面

`TemplateImportWindow`（`editor/src/Templates/TemplateImportWindow.xaml`）。

- **左**: 検索ボックス + カテゴリ別のチェックボックス付きツリー。
  カテゴリのチェックは配下すべてに伝播し、一部だけ選ぶとカテゴリは「一部選択」表示になる。
  検索は**表示の絞り込みだけ**で、隠れた項目のチェックは外れない。
- **右**: 選択の要約（エントリ数 / 依存込みのファイル数 / 合計サイズ）、
  既存ファイルと重複するパスの一覧、ライブラリ内で解決できなかった参照、
  そして「既存ファイルを上書きする」チェック。
- **下**: インポート / キャンセル。インポート後も画面は開いたままで、続けて選べる。

閉包計算とコピーはどちらも UI スレッドの外（`Task.Run`）で行う。
チェックのたびに計算しないよう、変更は約 0.2 秒まとめてから 1 回だけ走らせる。

### 呼び出し方（MainWindow への組み込み）

ウィンドウは `MainWindow` を知らない。静的入口を使う。

```csharp
var libraryRoot = TemplateLibraryLocator.Resolve();
if (libraryRoot is null) { /* メニューを無効化する等 */ }
else
{
    var result = TemplateImportWindow.ShowFor(this, libraryRoot, projectAssetsRoot);
    if (result?.HasCopied == true) { /* プロジェクトのファイル一覧を再読み込みする */ }
}
```

---

## 5. インポートの仕組み

「計画」と「実行」を分けている。計画は副作用ゼロなので、
利用者に先に見せられるし、コピーせずに単体テストできる。

```
選択エントリ（相対パス）
   │
   ├─ TemplateImporter.CreatePlan
   │      AssetCollector(libraryRoot).CollectFrom(選択)   ← 依存の閉包
   │      各ファイルについて <ProjectAssets>/<同じ相対パス> の存在を確認 ← 衝突判定
   │   ↓
   │  TemplateImportPlan（コピー対象 / 合計サイズ / 衝突一覧 / 欠落参照）
   │
   └─ TemplateImporter.Execute(plan, policy)
          衝突方針に従ってコピー（親フォルダは自動作成）
       ↓
      TemplateImportResult（コピー数 / 上書き数 / スキップ数 / 失敗 / 欠落参照）
```

### 閉包の考え方

`AssetCollector`（`editor/src/Packaging/Collect/`）はパッケージ化で使っている参照グラフ収集そのもの。
テンプレートのインポートでも**同じ実装を使う**。収集の規則を 2 つ持たないための判断で、次の利点がある。

- 参照の 4 系統（`assets://` / 絶対パス / 参照元からの相対 / ルート相対）をそのまま扱える
- 同伴ファイル（`.tvox` → `.tscatter` / `.tcover` / `terrain_meta.json`、`.obj` → `.mtl`）が付いてくる
- 解決できなかった参照が欠落として報告される

違うのは**起点だけ**。パッケージ化は `project_settings.json` → 登録シーン →
ランタイム内蔵参照 → 全 `.cs` 走査…と積むが、インポートでは「選ばれたエントリ」だけを積みたい。
そのための入口が `AssetCollector.CollectFrom(seedRelPaths)`。

| | `Collect()` | `CollectFrom(seeds)` |
|---|---|---|
| 起点 | `project_settings.json` / 登録シーン / ランタイム内蔵参照 / 追加フォルダ / 常時同梱拡張子 / 全 `.cs` | 引数で渡したパスだけ |
| フォルダ起点 | 追加同梱フォルダのみ | 渡したパスがフォルダなら配下すべて |
| 実在しない起点 | 登録シーンとして報告 | 欠落参照（参照元 = `(指定された起点)`）として報告 |
| 閉包・同伴ファイル・欠落検出 | 同じ | 同じ |
| `include_all_files` 設定 | 見る | 見ない |

`Collect()` の挙動は一切変えていない（`editor/tests/PackagingCollectorTests` が担保）。

### 衝突方針

コピー先に同名ファイルがあったときの扱いは列挙型で呼び出し側が選ぶ
（`TemplateImportConflictPolicy`）。

| 値 | 動作 |
|---|---|
| `Skip`（既定） | そのファイルはコピーしない。プロジェクト側の内容を残す |
| `Overwrite` | ライブラリの内容で置き換える |

- 衝突判定は**計画時に見せ、実行時にもう一度行う**。
  計画を見てから決めるまでの間にファイルが増減しうるため、上書き可否は必ず最新の状態で決める。
- コピー元とコピー先が同じフォルダのときは何もしない（自分自身への上書き事故の防止）。

---

## 6. 既存ゲームの移行（`--list-included`）

アセットルート直下に `templates/` を抱えたままの既存プロジェクトから、
「実際に参照されている物」だけを残す作業に使う。

```bash
dotnet run --project editor/tests/PackagingCollectorTests -- \
  <アセットルート> <runtime/src> --list-included <出力ファイル>
```

収録が決まったファイルのアセットルート相対パスを 1 行 1 件で書き出す（並びは相対パス昇順）。
`templates/` で始まる行が「テンプレート置き場に置いたまま実ゲームが参照している物」になる。

2026-09-11 時点の `D:\SEED_assets`（収録 217 ファイル / 46.6 MB）では 5 件:

```
templates/fonts/Digital/851Gkktt_005.ttf
templates/fonts/Digital/NikkyouSans-mLKax.ttf
templates/skybox/kloofendal_48d_partly_cloudy_puresky_2k.hdr
templates/terrain/textures/aerial_beach_01_1k.blend/textures/aerial_beach_01_diff_1k.jpg
templates/terrain/textures/aerial_beach_01_1k.blend/textures/aerial_beach_01_rough_1k.jpg
```

この 5 件は「テンプレートとして選ばれた物」ではなく「参照が `templates/…` を指したまま残っている物」。
移行時は参照先を `assets/` 配下の正規の場所へ移すか、同じパスで残すかを決める。

なお `templates` は `PackagingRules.DefaultExcludedFolders` にあるので、
**未参照なら配布物に入らない**（除外は参照より弱いので、参照されていれば入り、警告一覧に載る）。

### ライブラリ側のドライラン

エディタを起動せずにライブラリの中身とコピー計画を確認できる。**ファイルは書き込まない**。

```bash
# カテゴリ・エントリの一覧だけ
dotnet run --project editor/tests/TemplateImportTests -- <ライブラリルート>

# 指定エントリのコピー計画（依存・サイズ・衝突・欠落参照）
dotnet run --project editor/tests/TemplateImportTests -- \
  <ライブラリルート> <コピー先アセットルート> scenes/physicsTest.scene terrain/HeightMap
```

引数を付けずに実行すると通常どおり単体テストが走る。

---

## 7. 既知の制限

- **ライブラリ内のシーン・アクタに、旧アセットルートの絶対パスが残っている**（2026-09-11 時点）。
  `SpriteAnimation.scene` を除くすべてのシーンと全アクタが
  `C:\Users\…\SEED\runtime\assets\models\BrainStem.glb` のような絶対パスで参照を持つ
  （`physicsTest.scene` だけで 436 か所）。ライブラリのルート外を指すため閉包では辿れず、
  しかも `AssetReferenceScanner` はルート外の絶対パスを候補にしないので
  **欠落としても報告されない**（インポート画面に警告が出ない）。
  インポートしてもその参照先は付いてこないし、利用者の環境ではパスも通らない。
  ライブラリ側のデータを `assets://` のルート相対へ直す必要がある（`docs/backlog.md` に記載）。
- 依存の閉包は**静的な参照グラフ**なので、スクリプトが実行時に文字列を組み立てて読むアセットは辿れない。
  必要ならそのフォルダをエントリとして一緒に選ぶ。
- エントリの粒度はカテゴリ直下の子までで、フォルダエントリの一部だけを選ぶことはできない。

---

## 8. 関連ファイル

| ファイル | 役割 |
|---|---|
| `editor/src/Templates/TemplateLibraryLocator.cs` | ライブラリの場所解決 |
| `editor/src/Templates/TemplateCategoryNames.cs` | カテゴリ表示名の対応表（データ） |
| `editor/src/Templates/TemplateEntry.cs` | カテゴリ / エントリの値オブジェクト |
| `editor/src/Templates/TemplateLibrary.cs` | ライブラリの走査 |
| `editor/src/Templates/TemplateImportPlan.cs` | 計画・結果・衝突方針の値オブジェクト |
| `editor/src/Templates/TemplateImporter.cs` | 計画作成とコピー実行 |
| `editor/src/Templates/TemplateTreeNode.cs` | ツリー表示モデル（UI 層） |
| `editor/src/Templates/TemplateImportWindow.xaml(.cs)` | インポート画面 |
| `editor/src/Templates/ByteSizeText.cs` | バイト数の表示整形 |
| `editor/src/Packaging/Collect/AssetCollector.cs` | 参照の閉包（`Collect` / `CollectFrom`） |
| `editor/tests/TemplateImportTests/` | 単体テスト |
| `editor/tests/PackagingCollectorTests/Program.cs` | ドライラン + `--list-included` |
