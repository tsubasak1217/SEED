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

ランタイム exe の探索（`Runtime/BuildConfig/RuntimeExeLocator`）と同じ流儀。
どれも見つからなければ `Resolve()` は
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
  ui/        prefabs/（UI 部品のプレハブ。W2-5 のホイールの行・列・時刻ホイール、W2-7 の画面の組み立て〈screen_stack・screen_frame・tab_host・modal_host・
             dialog・bottom_sheet・top_sheet・toast_host・toast〉と見本の画面 nav_*、W2-8 のグラフ〈line_chart・bar_chart・chart_label〉を含む）・
             scenes/ui_gallery.scene・scenes/ui_navigation.scene・scenes/ui_charts.scene・scripts/・textures/・
             themes/（見本のテーマ forest・sunrise・forest_round。W2-9）・prefabs/list_row.actor（一覧の行。W2-9。W2 の手直し P2-3 で
             フルスワイプで削除できる行〈行のスクリプト scripts/UiGalleryListRow.cs〉に）
             （W2-4・W2-5・W2-7・W2-8・W2-9。docs/ui_components.md・docs/ui_navigation.md・docs/ui_charts.md・docs/ui_theme.md）
             template_actors.json・thumbnails/（テンプレートアクタのカタログと見本の画像。§9）
```

各トップレベルフォルダの直下に置く `template_actors.json`（テンプレートアクタのカタログ）と
`thumbnails/`（その見本の画像）は**ライブラリ自身の付帯物**で、プロジェクトへコピーするテンプレートではない。
インポート画面の一覧には出さない（名前の表は `editor/src/Templates/TemplateLibraryMetadata.cs`）。

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
| `ui` | UI 部品（W2-4） | | | |

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
| `editor/src/Templates/TemplateLibraryMetadata.cs` | ライブラリの付帯物（カタログ・サムネイルのフォルダ）の名前。インポートの一覧から外す |
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
| `editor/tests/TemplateImportTests/` | 単体テスト（テンプレートアクタは `TemplateActorTests.cs`） |
| `editor/tests/PackagingCollectorTests/Program.cs` | ドライラン + `--list-included` |

テンプレートアクタ（§9）の関連ファイルは §9.9 にまとめてある。

---

## 9. テンプレートアクタ（UI 部品などを「まっさらなアクタ」として追加する）

2026-10-01 追加（利用者の要望）。UI はエンジンの基本機能として用意する。コンポーネント単位ではなく、
**UI を構成するコンポーネントやスクリプトが既に付いた状態のアクタ**（テンプレートアクタ）をエンジン側
（このライブラリ）に置き、エディタから選んでシーンへ入れる。画像の差し替え・色・位置・大きさなどの
細かい調整は Inspector で行う。

### 9.1 決まっていること（利用者の決定。2026-10-01）

- **プレハブとは違い、まっさらなアクタとして追加する**。追加したアクタは `prefab_source` / `prefab_hash` を
  持たない（ヒエラルキーで水色にならず、「プレハブから更新」の対象にもならない）。
- **プロジェクトへテンプレートの `.actor` を暗黙に作らない**。
- 見本のサムネイルを出す。**画像は後から入れる**ので、置き場所（枠と規約）だけを先に決める（§9.4）。
  → 2026-10-02: 画像はランタイムで撮って作る道具（`editor/tools/SeedTemplateThumbnails`。§9.10）で全件を作って置いた。
- カテゴリの粒度は実装側に任された（§9.3 の表）。

### 9.2 画面（`TemplateActorPickerWindow`）

開き方: ヒエラルキー（またはシーンビュー）の右クリック「アクタを追加」→「テンプレートアクタ...」。
「アクタを追加」の中は「3D アクタ／2D アクタ／テンプレートアクタ...／ロジック配置」になる（既存の 2 つの挙動は変えていない）。

| 右クリックした場所 | 追加先 |
|---|---|
| ヒエラルキーの空白・シーンビュー | ルート（§9.6 の規則で置く） |
| ヒエラルキーのノード | そのノードの子（末尾） |

```
┌ テンプレートアクタを追加 ───────────────────────────────┐
│ 追加先: 「Canvas」の子                                      │
│ [🔍 検索（名前・説明・検索語・パスの部分一致）          ]    │
│ ┌カテゴリ──────┐┌一覧─────────────────────────────┐ │
│ │すべて      30 ││[ボ] ボタン                                │ │
│ │▽UI         28 ││     押すと Clicked が届く角丸のボタン…   │ │
│ │   基本      3 ││     UI › 基本 ・ 2D                       │ │
│ │   入力     11 ││[丸] 丸ボタン …                            │ │
│ │   …           ││                                            │ │
│ └──────────┘└──────────────────────┘ │
│ 追加しました: ボタン                       [追加] [閉じる] │
└──────────────────────────────────────┘
```

- **上**: 追加先の表示と検索欄。入力のたびに絞り込む。名前・説明・検索語（tags）・テンプレートのパス・カテゴリの
  **部分一致**。大文字小文字・全角半角・カタカナとひらがなを区別しない（NFKC → 小文字 → かな統一。
  `TemplateActorSearch`）。空白で区切ると「全部を含むもの」。
- **左**: カテゴリの木（「すべて」＋ 2 段）。件数はいまの検索語に当たる数。1 段目を選ぶとその下の 2 段目も含む。
  検索とカテゴリは組み合わさる。
- **右**: 一覧。1 行 = サムネイルの枠（64×64）＋ 名前 ＋ 説明（2 行まで。全文は行のツールチップ）＋ カテゴリと 2D/3D。
  選択中の行は一覧の選択色（`SeedDialogTheme.ListSelectionBg`）で強調する。
- **下**: 状態の行と「追加」「閉じる」。**モードレス**で、追加しても閉じない（続けて次を選べる）。
  追加したら「追加しました: 名前」と、コピーしたファイル・既にあって触らなかったファイル・見つからない参照を並べる
  （長いときは省略し、全文はツールチップ。詳細は Output パネルにも `[テンプレートアクタ]` で出る）。
- ダブルクリック・Enter でも追加、検索欄で ↓ を押すと一覧へ移る、Esc で閉じる。
- 窓は 1 つだけ。開いたまま別の場所を右クリックして選び直すと、同じ窓の追加先が切り替わる。
- 選んだテンプレートを追加先へ入れられないとき（2D の子に 3D など）は「追加」が押せず、理由を状態の行と
  ボタンのツールチップに出す（規則は既存の「子として追加」と同じ。`TemplateActorTarget.RejectReason`）。
- 見た目はエディタの UI の書式に従う（ボタンは `Seed.Button.DialogPrimary` / `Seed.Button.Dialog` に寸法だけ足す。
  色はダイアログの共通色。docs/editor_ui_style.md）。

### 9.3 カタログ（定義ファイル）

**`templates/<トップレベルフォルダ>/template_actors.json`**（フォルダに 1 つ）。載っていない `.actor` は一覧に出さない。

```jsonc
{
  "format_version": 1,        // カタログの版。エディタより新しい版は読まずに警告する
  "sort_order": 10,           // フォルダ間の並び（小さいほど先）。無ければ 1000
  "entries": [
    {
      "path": "prefabs/button.actor",   // 必須。カタログのフォルダからの相対パス（.actor / .actor2d）
      "name": "ボタン",                  // 表示名（空ならファイル名）
      "description": "押すと Clicked が届く…",  // 一覧に出す説明
      "category": "UI/基本",             // 2 段まで。'/' 区切り。空は「その他」、3 段目以降は 2 段目へ畳む
      "tags": ["button", "ボタン", "押す"],  // 検索語（日本語と英語）
      "thumbnail": "",                  // 空なら §9.4 の既定の場所
      "thumbnail_sample": { … },        // 省略可。見本の画像の撮り方（生成の道具だけが読む。エディタは読み飛ばす）§9.10
      "requires": ["prefabs/x.actor"]   // 省略可。§9.7
    }
  ]
}
```

- 手で書くデータなので、コメントと末尾のカンマを許す。
- 書き損じ（無いファイル・JSON として読めないテンプレート・同じパスの重複・未来の版）は**そのエントリだけ飛ばし**、
  理由を Output パネルへ出す（状態の行には件数）。
- 2D / 3D の別はカタログに書かない。テンプレートのルートの `actor_kind`（`Actor2D` なら 2D、無ければ 3D）から読む。
- カテゴリの並びは「フォルダの `sort_order` → カタログに現れた順」。エントリもその並びにそろえる。

2026-10-02 時点の登録（32 件。`templates/ui/template_actors.json` 30 件・`templates/actors/template_actors.json` 2 件。
2026-10-01 の 30 件に、部品の拡充で不定の進捗〈スピナー〉とダイアログの選択肢の行を足した）:

| カテゴリ | テンプレートアクタ |
|---|---|
| UI › 基本 | ボタン・丸ボタン・カード |
| UI › 入力 | トグル・チェックボックス・ラジオボタン・チップ・セグメント・スライダー・数値の欄（増減ボタン）・入力欄・数値の入力欄・ホイール・時刻ホイール |
| UI › 表示 | 進捗の棒・進捗の輪・不定の進捗（スピナー）・アバター |
| UI › 一覧とスクロール | 一覧の行 |
| UI › ナビゲーション | 画面のスタック・下のタブ・ダイアログ・シートの受け皿（ModalHost）・トーストの受け皿（ToastHost） |
| UI › 重ねる面の見た目 | ダイアログ・ダイアログの選択肢の行・下からのシート・上からの覆い・トースト（いずれも「（見た目を作る用）」） |
| UI › グラフ | 折れ線グラフ・棒グラフ |
| 3D › 見本 | 歩くモデル（BrainStem） |
| 2D › 見本 | キャンバスと当たり判定 |

**載せていないもの**: 見本の画面 `nav_*`（見本のスクリプトと互いの画面を参照する）、内部の部品
`wheel_row`・`chart_label`・`screen_frame`（他の部品が実行中に読む。§9.7）、`actors/NewActor.actor`
（参照先のモデル `models/bunny/bunny.obj` がライブラリに無い）、`actors/camera.actor`（中身の無いアクタ）、
`actors/NewActor(1) (copy)…`（BrainStem の重複）。

「重ねる面の見た目」の 4 つ（名前に「（見た目を作る用）」を付けてある）は、普段は受け皿（ModalHost / ToastHost）が**実行中に** `ui/prefabs/*.actor` から作る面で、
シーンに置いたままだと Play で消える（`ModalPlane` が「開く約束が無い」と警告して自分を消す）。見た目を作り替えるときに
置いて調整し、アクタファイル化して受け皿の `DialogPrefab` 等に指定する、という使い方のために載せてある（説明文にも書いた）。

### 9.4 サムネイルの規約

| 項目 | 規約 |
|---|---|
| 既定の場所 | **`templates/<フォルダ>/thumbnails/<テンプレートのファイル名（拡張子なし）>.png`**（例 `templates/ui/thumbnails/button.png`・`templates/actors/thumbnails/BrainStem.png`） |
| 明示 | カタログの `thumbnail` 欄（カタログのフォルダ基準の相対パス）。既定の場所より優先 |
| 大きさ | **推奨 192×192 の正方形 PNG**（透過可）。一覧では 64×64 に縮めて出す（読み込みは 128 px。表示倍率 200% までにじまない） |
| 画像が無いとき | 灰色の板（`SeedColorTable.THUMBNAIL_PLACEHOLDER_BG`）に表示名の頭文字（`THUMBNAIL_PLACEHOLDER_FG`。コントラスト 4.5 以上を `ThemeContrastTests` で検査）。枠のツールチップが置き場所と推奨の大きさを教える |
| 差し替え | ファイルの更新時刻が変わったら読み直す（窓を開き直せば反映。エディタの再起動は要らない） |

- 2026-10-02: 全件（32 件）の画像を生成の道具（§9.10）で作って置いた（192×192・α なしの PNG・1 枚 1〜22 KB）。
  手で描いた画像に差し替えてもよい（同じ場所に置くか、`thumbnail` 欄で指す）。
- git: `.gitignore` は `templates/**/*.png`（ライブラリの重い画像はローカルだけ）を除外するが、
  `!templates/**/thumbnails/*.png` の例外で見本の画像は**追跡する**。`.gitattributes` の `*.png` の規則で Git LFS に入る
  （エディタのアイコンと同じ）。
- 画像を置いたら、窓の状態の行の「見本の画像あり n 件」で数を確かめられる（プローブ `TemplateActorPickerPreviewProbe` の 01 は
  「全件に画像がある」ことを表明する）。

### 9.5 追加の流れ

```
エディタ（TemplateActorPickerWindow → TemplateActorInstaller。重い処理は Task.Run）
  1. 追加先をいまのヒエラルキーで引き直す（DFS 番号はずれるので安定キー＝名前パスで探す。見失ったら中止）
  2. テンプレートを読み、まっさらな 1 本の木にする（§9.8。TemplateActorFlattener）
  3. 動かすのに要るファイルをプロジェクトへコピーする（§9.7。既にあれば触らない）
  4. まっさらな木を一時ファイルへ書く（%TEMP%\seed_template_actors\<guid>.actor。1 時間より古いものは次の追加で掃除）
  5. ADD_TEMPLATE_ACTOR:{world_line},{parent_dfs|-1},{一時ファイルのパス} を送る
     （パスはカンマを含みうるので最後。`TemplateActorIpc`）
ランタイム（app/template_actor_ops.rs）
  6. .actor の唯一の読み込み口（actor_file::load。版の変換もここ）で読み、prefab_source / prefab_hash を木全体から外す
  7. 置き場所を決め（§9.6）、組み立てて入れる
  8. Undo を 1 件（ActorTreeSnapshotCommand。2D/3D の追加と同じ）→ ヒエラルキーを送る → 追加したアクタを選択する
     入れられない・読めないときは LOAD_ERROR で理由を返し、木には触らない
```

- ランタイムが断ったときは、既存のアクタ追加の拒否と同じ `LOAD_ERROR` の経路になり、エディタは
  「シーンの読み込みに失敗しました」のダイアログで理由を出す（文言は既存のまま。`docs/backlog.md` のテンプレートアクタの節）。
  窓の側で 2D/3D の規則・追加先の有無を先に確かめているので、ふつうは出ない。

- 一時ファイルを経由するのは、`.actor` の読み込み口を増やさない（版の変換を素通りさせない）ためと、
  プロジェクトの中に `.actor` を作らないため。
- Ctrl+Z で追加を取り消せる（1 操作）。**コピーした依存ファイルは Undo では消えない**（プロジェクトのファイルなので）。
- 閲覧専用の表示中（Android の一時停止の写し）は追加しない（エディタが止め、ランタイムも写しの表示中は捨てる）。

### 9.6 置き場所の規則

既存の「2D アクタ」「3D アクタ」の追加と同じ規則。2D の部品をルートへ入れるときは、
2D ビューへの `.actor` ドロップと同じ Canvas の規則（Canvas が無ければ作る）で置く。

| 追加先 | テンプレート | 置き場所 |
|---|---|---|
| ノードの子 | 3D | 親が 3D なら子。親が 2D なら不可（「3Dアクターは2Dアクターの子にできません」） |
| ノードの子 | 2D | 親が 2D か Canvas を持つ 3D なら子。Canvas の無い 3D は不可 |
| ルート | 3D | トップレベル。キャンバス編集タブでは不可 |
| ルート | 2D（ルートに Canvas を持つ） | トップレベル（新しいルートキャンバスとして並ぶ）。キャンバス編集タブではルートキャンバスの子 |
| ルート | 2D（Canvas を持たない部品） | キャンバス編集タブ: ルートキャンバスの子／それ以外: **最初のルートキャンバスの子**／キャンバスが無い: シーン（世界線 0）なら**既定のキャンバス（1920×1080・auto_scale）を作ってその子**、アクタ編集タブならタブのルートの子（受け付けなければトップレベル） |

位置は変えない（テンプレートに保存された `CanvasTransform` / `Transform` のまま。UI 部品はアンカー左上・位置 0 なので
キャンバスの左上に出る）。

### 9.7 動かすのに要るファイルのコピー

テンプレートが参照するファイルがプロジェクトに無いと動かない（モデルが無いとアクタの組み立て自体が失敗する）。
そこで**必要なファイルだけ**をライブラリから同じ相対パスへコピーする（`TemplateActorDependencyPlanner`）。

- 起点: まっさらにした**後**の木の JSON に書かれた参照（`assets://` など。パッケージ化と同じ `AssetReferenceScanner`）と、
  カタログの `requires`。引用符の中の一般の文字列（"ui" など）がフォルダ名に当たっても、**ファイルだけ**を起点にする。
- 閉包: `AssetCollector.CollectFrom`（参照を多段に辿る。テンプレートのインポートと同じ実装）。
- **テンプレート自身はコピーしない**。入れ子のプレハブは展開して印を外した後なので、部品の `.actor` が「参照されている」と
  誤ってコピーされることもない。
- **既にあるファイルは上書きしない**（実行時にもう一度確かめる）。状態の行に「既にあるので触らなかったファイル」として並ぶ。
- ライブラリにもプロジェクトにも無い参照は「見つからない参照」として知らせる（追加は続ける）。

**スクリプトが実行中に読む部品の `.actor`**: `SEED.UI` の部品のいくつかは、実行中に `assets://ui/prefabs/*.actor` を
`Instantiate` して中身を作る（ホイールの行 `wheel_row`・グラフの目盛りの文字 `chart_label`・画面の枠 `screen_frame`・
ModalHost が開く `dialog` / `bottom_sheet` / `top_sheet`（とダイアログの入力欄 `text_field`）・ToastHost の `toast`）。
これらは追加したアクタのプレハブではなく「動かすのに要るファイル」なので、テクスチャと同じくコピーする
（スクリプトの欄に書かれていれば自動で拾い、欄が空で既定値を使う部品はカタログの `requires` で明示する）。
**プロジェクトに `.actor` を作らない決定との兼ね合いは未決**（`docs/backlog.md` のテンプレートアクタの節）。

| テンプレートアクタ | 一緒にコピーされるもの（2026-10-01 の同梱ライブラリ） |
|---|---|
| カード・アバター | `ui/textures/avatar.png` |
| ホイール・時刻ホイール | `ui/prefabs/wheel_row.actor` |
| 折れ線グラフ・棒グラフ | `ui/prefabs/chart_label.actor` |
| 画面のスタック・下のタブ | `ui/prefabs/screen_frame.actor` |
| ダイアログ・シートの受け皿 | `ui/prefabs/dialog.actor`・`bottom_sheet.actor`・`top_sheet.actor`・`text_field.actor` |
| トーストの受け皿 | `ui/prefabs/toast.actor` |
| 一覧の行 | `ui/scripts/UiGalleryListRow.cs`（見本のスクリプト。フルスワイプで削除の動き） |
| 歩くモデル（BrainStem） | `models/BrainStem.glb`（約 3 MB）・`input/playerInput.inputmap` |
| キャンバスと当たり判定 | `textures/symmetryORE2.png` |

### 9.8 入れ子のプレハブの展開

SEED の `.actor` は、入れ子のインスタンスの中身（子の木とコンポーネント）も丸ごと保存している。ランタイムは
「保存された内容を正とする」方針（docs/editor_prefab.md §2）なので、ここでも**保存された中身をそのまま使い、
印（`prefab_source` / `prefab_hash`）だけを木全体から外す**。

- 中身が空（`components` も `children` も空）で参照だけが残ったノードは、参照先をライブラリから読んで中身を差し込む。
  このとき残すのはノードの `name` / `transform` / `canvas_transform` / `active` / `visible`（再展開で維持される値と同じ）。
- 参照先の版（`format_version`）がテンプレートと違う・循環している・8 段より深い・見つからないときは差し込まず、
  警告を状態の行と Output に出す（そのノードは印を外しただけで残る）。
- スクリプトの欄にある `.actor` のパス（`RowPrefab` など）は木の一部ではない（何個作るかは実行中に決まる）ので展開しない（§9.7）。
- 2026-10-01 の同梱ライブラリには入れ子のインスタンスが無い（`prefab_source` を持つテンプレートは 0 件）。規則は将来のための備え。

### 9.9 関連ファイル

| ファイル | 役割 |
|---|---|
| `templates/ui/template_actors.json`・`templates/actors/template_actors.json` | カタログ（データ） |
| `templates/*/thumbnails/` | 見本の画像の置き場（§9.4） |
| `editor/src/Templates/Actors/TemplateActorCatalogFormat.cs` | カタログの欄名・区切り・サムネイルの規約の定数 |
| `editor/src/Templates/Actors/TemplateActorEntry.cs` | エントリ・カテゴリの値オブジェクト |
| `editor/src/Templates/Actors/TemplateActorCatalog.cs` | カタログの読み込み（並び・カテゴリの木・警告） |
| `editor/src/Templates/Actors/TemplateActorSearch.cs` | 検索（正規化・部分一致・カテゴリとの組み合わせ） |
| `editor/src/Templates/Actors/TemplateActorFlattener.cs` | まっさらな 1 本の木にする（入れ子の展開・印の除去） |
| `editor/src/Templates/Actors/TemplateActorDependencyPlanner.cs` | 動かすのに要るファイルの計画とコピー |
| `editor/src/Templates/Actors/TemplateActorStaging.cs` | ランタイムへ渡す一時ファイル |
| `editor/src/Templates/Actors/TemplateActorInstaller.cs` | 追加の準備（読む → まっさら → コピー → 一時ファイル） |
| `editor/src/Templates/Actors/TemplateActorTarget.cs` | 追加先と入れてよいかの規則 |
| `editor/src/Templates/Actors/TemplateActorIpc.cs` | `ADD_TEMPLATE_ACTOR` の組み立て |
| `editor/src/Templates/Actors/TemplateActorPickerWindow.xaml(.cs)` | 窓 |
| `editor/src/Templates/Actors/TemplateActorListItem.cs`・`TemplateActorCategoryItem.cs`・`TemplateActorThumbnails.cs`・`TemplateActorPickerContext.cs` | 窓の表示モデル・サムネイルの読み込み・外へ頼むこと |
| `editor/src/Panels/HierarchyPanel.TemplateActors.cs` | 右クリックの項目・追加先の作成と引き直し |
| `editor/src/MainWindow.TemplateActors.cs`（と `MainWindow.Viewport.cs` のシーンビューの右クリック） | 窓を開く |
| `runtime/src/engine/core/app_base/ipc.rs`（`IpcCommand::AddTemplateActor`） | ワイヤ形式 |
| `runtime/src/engine/core/app_base/app/template_actor_ops.rs` | ランタイムの受け口（置き場所の規則・Undo・選択） |
| `editor/tests/TemplateImportTests/TemplateActorTests.cs`・`TemplateActorFixture.cs` | 単体テスト（同梱のカタログがすべて解決できることも確かめる） |
| `editor/tests/TemplateActorPickerPreviewProbe/` | 窓の実物の XAML を表示せずに組み立てて PNG に描き、状態（検索・カテゴリ・押せない理由・空の案内・見本の画像）を表明する。`dotnet run --project editor/tests/TemplateActorPickerPreviewProbe -- --out <出力先>`（エディタは起動しない。06 の仮の見本の画像は出力先の仮のライブラリにだけ描く。01 は同梱の見本の画像が全件にあることを表明する） |
| `editor/tools/SeedTemplateThumbnails/` | 見本の画像の生成の道具（§9.10） |
| `templates/ui/scripts/ThumbnailStage.cs` | 見本の画像を撮る舞台のスクリプト（見本の操作・撮ってよい合図。§9.10） |

### 9.10 見本の画像の生成（`SeedTemplateThumbnails`。2026-10-02）

利用者の要望（「見本のイメージがあるとユーザビリティがとても上がる」）で、カタログの全件の見本の画像を**ランタイムで実際に描いて撮る**
道具を作った。手で描くのではなく撮るので、部品の見た目（テーマ・形・文字の描き方）を変えたら撮り直せばそのまま追従する。

#### 使い方

```
dotnet run --project editor/tools/SeedTemplateThumbnails -- [--only <名前>] [--size 192] [--sheet <PNG>]
```

| オプション | 意味（既定） |
|---|---|
| `--only <名前>` | 撮るテンプレートを絞る（ファイル名の拡張子なし。例 `button`。`,` 区切り・繰り返し可。カタログに無い名前は誤り） |
| `--size <一辺>` | 書き出す画像の一辺（px。192 = §9.4 の推奨。64〜1024） |
| `--sheet <PNG>` | 書き出した見本を名前付きで 1 枚に並べた確認用の画像も書く（リポジトリの外に書くこと） |
| `--library <フォルダ>` | テンプレートライブラリ（上へ探した `templates/`。環境変数 `SEED_TEMPLATE_LIBRARY` も見る） |
| `--runtime <SEED.exe>` | 撮影に使うランタイム（`<リポジトリ>/runtime/target/debug/SEED.exe`。**先に `cargo build` しておく**） |
| `--runtime-cwd <フォルダ>` | ランタイムの作業フォルダ（`<リポジトリ>/runtime`。ランタイムはここから `../scripting/bin/Debug/net10.0/SEEDScripting.dll` を読むので、スクリプトの DLL も先にビルドしておく） |
| `--work <フォルダ>` | 一時のプロジェクト（`assets/`）・撮った元の画像（`shots/`）・ランタイムのログ（`runtime_<幅>x<高さ>_<何回目>.log`）の置き場（`%TEMP%\seed_template_thumbnails`。起動のたびに作り直し、終わっても残す。モデルのキャッシュ `cache/` は残す） |
| `--port <番号>` | IPC（TCP）の最初のポート（47770。使われていれば次の番号を 10 個まで試す） |

終了コード: 0 = すべて書けた（飛ばした件を含む）／1 = 引数・入力の誤り／2 = 撮れなかった件がある／3 = ランタイムを使えなかった。
最後に件ごとの結果（書けた・注意あり・飛ばした・撮れなかった＋理由・バイト数・中身の割合・かかった秒）を一覧で出す。
撮れなかった件は飛ばして次へ進む（書き損じの見本のデータ・受け皿の無い操作・ランタイムの誤り）。

#### 仕組み

```
カタログ（TemplateActorCatalog = 窓と同じ読み込み）＋ thumbnail_sample（ThumbnailSampleCatalog）
  → 件ごとの舞台のシーン（StageSceneBuilder。テンプレートをまっさらな木にして〈TemplateActorFlattener〉見本の上書きを当てる）
  → 一時のプロジェクト（StageProject: <作業>/assets。参照するファイルは「テンプレートアクタを追加」と同じ計画
     〈TemplateActorDependencyPlanner〉でライブラリから写す。ライブラリとリポジトリには書かない）
  → SEED.exe --mode=play をフォーカスを奪わずに起動（QuietProcess。窓は最初から画面の外・最背面・SW_SHOWNOACTIVATE・
     WS_EX_NOACTIVATE〈前面にならない〉・コンソールの窓なし。SEED_HEADLESS=1 でフレームを回し続け、GPU から読み戻して撮る。
     --parent-pid で道具が落ちたら終わる）
  → IPC（TCP・HELLO/READY）で件ごとに LOAD_SCENE → 舞台のスクリプトの合図「[THUMB] ready <札>」をログで待つ → SCREENSHOT:game
  → 切り出し（crop）か全体を収める（contain）→ 面積の平均で縮める（線形の光の量で平均）→ α なしの PNG
```

- **舞台**: UI の部品は dp のルートのキャンバス（360×360 dp・1 dp = 1.5 画素で描く。`SEED_SIM_SCALE_FACTOR` なので OS の表示倍率に
  左右されない）の中央に、テーマの背景（`color.background`）の上で置く。3D はカメラと平行光の前（原点）、
  ルートのキャンバスの 2D の見本は設計の解像度の窓（1920×1080）でそのまま。
- **起動は窓の大きさごとに 1 回**（ふつうは UI・3D の 540×540 と 2D の見本の 1920×1080 の 2 回。32 件で約 40 秒）。
  起動後に窓の大きさを変えると画素のルートのキャンバスの自動の拡大が最初の大きさのまま残って歪むため、大きさを変えずに起動を分ける。
- **見張り**: 合図が時間切れになった（ランタイムのフレームが止まった疑い）・ランタイムが終わったときは、起動し直してその件を 1 度だけ
  撮り直す（同じ窓の大きさで起動し直しは 3 回まで）。止まったまま残りの全件が時間切れ（1 件 約 21 秒）を待つことはない。
- **前面にしない**: 窓に WS_EX_NOACTIVATE を付ける（クリックや、前面の窓を閉じたときの Windows の自動のアクティブ化で前面にならない）。
  画面の外の窓にフォーカスの出入りが届き（`platform.paused`）、その後フレームが進まなくなって残りの件が撮れなかったことがある
  （2026-10-02 の 1 回。`docs/backlog.md`）。WS_EX_NOACTIVATE を付けた後も出入りが届くことはあり（同じ日の別の撮影で 2 回。
  このときはフレームは止まらなかった）、止まったときの備えは上の見張り。
- **舞台のスクリプト** `templates/ui/scripts/ThumbnailStage.cs`: シーンに置いただけでは見えない部品を見える状態にする
  （ダイアログ・選択肢の一覧・進捗の札・シート・覆い・トーストを開く、グラフへ値を入れる）。受け皿・部品の OnStart の順は
  決まっていないので、できるまで毎フレームやり直し、できたら `settle` 秒待って合図を出す（10 秒でできなければ「failed」の合図）。
  どのテンプレートからも参照されないので、テンプレートアクタの追加でプロジェクトへコピーされることはない
  （テンプレートのインポートで `ui/scripts/` をまるごと取り込むと一緒に入るが、置いても何もしない）。

#### 見本のデータ（カタログの `thumbnail_sample`）

ツールのコードはテンプレートの名前を知らない。ボタンの文字・一覧の 3 行・ホイールの値・グラフの点・開くダイアログの文言など、
「どう見せて撮るか」はすべてこの欄に書く（すべて省略可。省略したエントリは中央に 1 つ置いて舞台全体を撮る）。

| 欄 | 意味 |
|---|---|
| `skip` | 文字があれば撮らない（理由として一覧に出す） |
| `stage_kind` | 舞台の種類 `ui`（2D の既定）／`model`（3D の既定）／`root_canvas`（ルートのキャンバスを持つ画素の 2D をそのままルートに） |
| `stand_in` | 代わりに置くテンプレート（カタログのフォルダ基準）。自分だけでは見えない面を受け皿に開かせるときに使う（例 ダイアログの面 → `prefabs/modal_host.actor` を置いて `script` で開く。自分のファイルも一緒にコピーする） |
| `placement` | `center`（既定。大きさの決まった枠に入れて縦の Stack で中央へ）／`fill`（舞台の根の直下。親に合わせて広がる受け皿・画面） |
| `stage` | UI の舞台の大きさ（dp。既定 `[360, 360]`） |
| `size` | `center` の枠の大きさ（dp。既定はテンプレートの根の Sprite、次に Canvas の大きさ。根に大きさが無い部品〈ラジオボタン・チップ〉と、幅を親に合わせる部品〈一覧の行〉は書く） |
| `frame`・`focus` | 切り出す正方形の一辺（dp。既定は舞台の短い辺）と中心（舞台に対する割合。既定 `[0.5, 0.5]`） |
| `fit` | `crop`（正方形を切り出す。UI の既定）／`contain`（全体を正方形に収め、余りを `color.surface_variant` で埋める。3D・2D の既定） |
| `settle` | 見本の操作ができてから撮るまでの秒（既定 0.8。開く動き・伸び縮み・モデルの読み込みを待つ） |
| `patch` | 上書き `{ "<ノードのパス>": { "<コンポーネント名>": { …data へ深くマージ… } } }`。パスは根 = `""`・子は name を `/` でつなぐ。コンポーネント名 `$node` はノードそのもの（`canvas_transform` など） |
| `instances` | 同じテンプレートを並べる（それぞれに `patch` と同じ形の上書きを重ねる。例 トグルのオンとオフ・一覧の 3 行） |
| `spacing` | 並べる間隔（dp。既定 12） |
| `script` | 舞台のスクリプトの欄 `Action`（`dialog`・`menu`・`progress`・`sheet`・`overlay`・`toast`・`line_chart`・`bar_chart`・`none`）・`Title`・`Message`・`PositiveText`・`NegativeText`・`Items`（`\|` 区切り。頭の `!` で危険・`~` で選べない）・`ContentPrefab`・`Values`（`,` 区切り。棒の積み上げは `;`・折れ線の空は記録の無い日）・`StartDate`（`yyyy-MM-dd`）・`Target`（既定は置いたテンプレートの根の名前） |
| `camera`・`light` | `model` の舞台のカメラ `{position, rotation, fov}`（m・度。左手系で前は +Z）と平行光 `{rotation, intensity}` |
| `window` | `root_canvas` の舞台の窓（画素。既定 `[960, 540]`。設計の解像度にすると歪まない） |

例（ボタン: 種類の違う 2 つを並べ、文字も変える）:

```jsonc
"thumbnail_sample": {
  "frame": 200,
  "instances": [
    {"": {"Button": {"fields": {"Variant": "Filled", "Text": "保存する"}}}},
    {"": {"Button": {"fields": {"Variant": "Outlined", "Text": "キャンセル"}}}}
  ]
}
```

#### 撮り直すとき

- テンプレート（`.actor`）の見た目・カタログの `thumbnail_sample` を変えたとき → その件だけ `--only <名前>` で撮り直す。
- カタログにエントリを足したとき → `--only <名前>` で撮る（撮り忘れはプローブの 01 の表明が落ちて気付ける）。
- 部品のスクリプト（`SEED.UI` の見た目の計算）・既定のテーマ（`default_theme.json`）・文字の描き方・描画（ランタイム）を変えたとき → 全件を撮り直す。
  ランタイム（`cargo build`）とスクリプトの DLL（`dotnet build scripting`）を先にビルドしておく。
- 撮った後は `--sheet` の一覧の画像で、空白・真っ黒・切れていないか、部品だと分かるかを目で見る。
  一覧の「中身」（背景・余白の色と違う画素の割合）が 1% 未満の件は「ほぼ背景だけ」の注意が付く。

#### 検証（2026-10-02・PC。作業フォルダ `tmp/lane2/thumbs/`）

- 32 件すべて書けた（注意 0・飛ばした 0・撮れなかった 0・終了コード 0）。所要 39.7 秒（起動 2 回 × 約 3.3 秒・1 件 0.9〜1.8 秒）。
  PC が混んでいる時の最後の撮り直しは 70.6 秒（起動 5〜5.5 秒・遅い件で 1 件 12 秒）だったが、やはり 32 件すべて書けた。
  画像は 192×192・8 ビットの RGB（α なし）・1 枚 680 B〜21 KB（UI は 1〜8 KB、3D の BrainStem が 21 KB。32 枚で 145 KB）。
- 窓は最初から画面の外（見えた位置 -16000,-16000）で、前面を奪わなかった（ログの「前面を奪ったか: いいえ」）。
- 書き損じの扱い（使い捨てのライブラリの写しで確かめた）: 受け皿の無いダイアログの操作 → 10 秒で「見本の操作ができません: ModalHost is not ready」、
  `skip` → 飛ばした、知らない `stage_kind`・無いノードへの `patch` → 舞台を組み立てられません、の理由つきで、ほかの件は続けて撮れた。
  カタログに無い `--only`・範囲外の `--size` は終了コード 1、使用中のポート 47770 は 47771 へ、`--size 128` は 128×128 で書いた。
- 見張り: 撮影の途中で道具が起動したランタイムを止めると、「ランタイムが終わりました」を検知して起動し直し（3.8 秒）、その件から撮り直して
  残りも書けた（6 件すべて書けた・終了コード 0）。
- 窓のプローブ `TemplateActorPickerPreviewProbe` の 01 で、画像入りの一覧（「見本の画像あり 32 件」）を描いて表明が通った。

#### 制限と持ち越し

- Windows 専用（ランタイムの起動と窓の扱いに Windows の API、PNG の読み書きに WPF の画像の機能を使う）。
- 2D の見本（`NewActor2D.actor2d`）は、見本の画像 `textures/symmetryORE2.png` が人の写真なので、`patch` でテクスチャを外して単色の箱で撮った
  （見本の画像は git で追跡されるので、人の写真から作った画像をリポジトリに入れない）。床と壁はキャンバスの外に置いてある作りなので写らず、
  落ちる箱だけが写る。
- 撮った絵はランタイムのその時点のビルドの描き方そのもの（描画の不具合も写る）。部品・テーマ・描画を変えたら撮り直す。
- 動き続ける部品（不定の進捗のスピナー・3D のモデルのアニメーション）は撮る瞬間の実時間で絵が少し変わるので、撮り直すと
  変えていなくても差分が出る。差分を出したくなければ `--only` でその件を外して撮る。
  静止した部品もほとんど（2026-10-02 の撮り直しで 32 件中 28 件）は同じバイト列になるが、ダイアログの幕の上の文字などで
  1 階調（255 分の 1）だけ揺れる件がある（2 回撮って比べると 4〜58 画素・最大差 1。見た目は同じ。GPU の描き方の揺れ）。
  ダイアログの開く動きが撮る瞬間に残らないよう、ダイアログ・選択肢の行の `settle` は 1.5 秒にした。
- ランタイムの起動の時間は PC の混み具合で 3〜13 秒ほど揺れる（GPU の初期化）。
