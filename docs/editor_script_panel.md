# スクリプトパネル（内蔵テキストエディタ）（正典）

SEED エディタ（`editor/`, WPF）の**スクリプトパネル**の仕様書。
C# スクリプト（`.cs`）・シェーディングアセット（`.wgsl`）に加えて、
**JSON / テキスト系のアセットをその場で編集する**ための土台でもある。
「どの拡張子を開けるか」「開いたとき何が有効になるか」を触るときは必ずここを見る。

| ファイル | 役割 |
|---|---|
| `editor/src/Panels/ScriptEditorPanel.cs` | パネル本体（タブ・エディタ生成・保存・補完・診断・デバッグ） |
| `editor/src/Panels/ScriptEditor/EditorLanguage.cs` | 言語種別（enum）と、言語ごとの機能の有無 |
| `editor/src/Panels/ScriptEditor/TextEditableCatalog.cs` | 拡張子 → 言語の対応表の読み込み・検証（WPF 非依存） |
| `editor/src/Panels/ScriptEditor/JsonHighlighting.cs` | JSON のハイライト定義（埋め込み `Syntax/Json.xshd`） |
| `editor/src/Panels/ScriptEditor/WgslHighlighting.cs` | WGSL のハイライト定義（埋め込み `Syntax/Wgsl.xshd`） |
| `editor/config/text_editable_extensions.json` | **対応拡張子の正典（データ）**。ビルド無しで増やせる |

デバッグ（ブレークポイント）まわりは [docs/scripting_debugger.md](scripting_debugger.md)、
スクリプト API そのものは [docs/scripting_api.md](scripting_api.md) を見ること。

---

## 1. 対応拡張子（データドリブン）

対応表は `editor/config/text_editable_extensions.json` にあり、
コードには**同じ内容の組み込み既定**だけが入っている（JSON を消してもエディタは動く）。
形式を増やすときは JSON へ 1 行足す。

| 拡張子 | 言語 | 備考 |
|---|---|---|
| `.cs` | `csharp` | 補完・診断・デバッグ・整形・AI 補完の全機能 |
| `.wgsl` | `wgsl` | 着色・静的辞書補完・ランタイム検証（赤下線） |
| `.json` | `json` | `layers.json` / `props.json` / `terrain_meta.json` など |
| `.txt` | `text` | クレジット・メモ |
| `.csv` | `csv` | 表データ |
| `.md` | `markdown` | ドキュメント |
| `.inputmap` | `json` | 入力マップ（中身は JSON） |
| `.icons` | `json` | アイコンセット（専用エディタが無い） |
| `.anim` | `json` | アニメーションクリップ（中身は JSON） |

読み込みは `EditorPaths.ConfigDir`（開発配置＝`editor/config`、配布配置＝exe の隣の `config`）から
起動時に 1 度だけ（`MainWindow.LoadTextEditableCatalog`）。壊れていても**起動は止めず**、
組み込み既定へフォールバックして理由を `EditorLog` へ出す。

### 対象外にしているもの（意図的）

| 拡張子 | 理由 |
|---|---|
| `.scene` / `.actor` / `.actor2d` | **エディタが常時読み込んで編集している実体**。テキストで書き換えるとランタイム側の状態と二重管理になり、「保存した瞬間に相手の変更が消える」「壊れた JSON でシーンが開けなくなる」という戻せない壊れ方をする。`TextEditableCatalog` は JSON にこれらが書かれていても**読み込み時に拒否する**（データで安全策を外させない） |
| `.tvox` / `.tscatter` / `.tcover` | 地形の中間データで**独自バイナリ**（magic `TVOX` / `TSCT` / `TCOV`）。テキストで開けば文字化けし、保存すればファイルが壊れる。プロジェクトパネルでも既定で非表示（[docs/editor_project_panel.md](editor_project_panel.md) §7） |
| `.mat` | 現状はダブルクリックで OS の既定関連付けアプリを開く（Phase R7 の最小実装のまま。[backlog](backlog.md)） |

---

## 2. 開く動線

### ダブルクリック（プロジェクトパネル）

**専用エディタを持つ形式が優先**。テキストエディタは最後のフォールバックに置いてある
（`ProjectPanel.xaml.cs` の `AttachItemEvents`）。

```
.scene → シーンを開く
.actor/.actor2d → アクタータブ
.inputmap → 入力マップエディタ
.anim → アニメーションタイムライン
.mat → OS の既定アプリ
.sprite_mesh → スプライトリグパネル
それ以外で対応拡張子 → スクリプトパネル（.cs / .wgsl / .json / .txt / .csv / .md / .icons）
```

### 右クリック「テキストエディタで開く」

`ProjectPanel.TextEdit.cs`。対応拡張子のファイルを単一選択しているときだけ出る。
**専用エディタを持つ形式（`.anim` / `.inputmap`）の中身を直接直したいとき**の入口で、
ダブルクリックの動線（＝専用エディタ）を壊さずにテキスト編集へ入れる。

どちらの動線も `ProjectPanel.ScriptFileOpened` → `MainWindow.OnScriptFileOpened` →
`ScriptEditorPanel.OpenFile(path)` と同じ経路を通る。

---

## 3. 言語ごとに有効な機能

分岐はすべて `DocTab.Language`（タブ生成時に拡張子から確定）で行う。
判定は `EditorLanguages` に集約してあり、ここが緩むと
「JSON を開いたら WGSL の補完候補が出る」といった事故になる。

| 機能 | `.cs` | `.wgsl` | JSON / CSV / Markdown / テキスト |
|---|---|---|---|
| 構文ハイライト | ○（C# ダーク調整） | ○（`Wgsl.xshd`） | JSON=○ / Markdown=○（ダーク調整） / CSV・テキスト=無着色 |
| 補完（IntelliSense） | ○ Roslyn | ○ 静的辞書 | **×** |
| 診断（赤波線） | ○ Roslyn | ○ ランタイム検証 | × |
| ブレークポイント・デバッグ | ○ | × | × |
| AI インライン補完 | ○ | × | × |
| コード整形（Ctrl+K,D） | ○ | × | × |
| 保存後のコンパイル検証・`RELOAD_SCRIPTS` | ○ | × | × |
| 保存・タブ・変更マーク・検索・置換・Undo | ○ | ○ | ○ |

保存は全種別で同じ経路（`Ctrl+S` / タブの未保存マーク / クラッシュ復元の退避）。
C# 以外はファイルへ書くだけで終わる（`.wgsl` はランタイムが mtime を見て自分でホットリロードする）。

### ハイライト定義について

- JSON は AvalonEdit に**同梱の `Json` 定義があるが使わない**。同梱版はライトテーマ向けで、
  区切り記号が黒（`#000000`）などエディタ背景（`#1E1E1E`）では読めないため、
  独自の `Syntax/Json.xshd`（定義名 `JSON`）を埋め込みリソースから登録している。
- Markdown は同梱の `MarkDown` 定義を使うが、見出し `#800000`・引用 `#00008B` と暗すぎるので、
  C# 定義と同じやり方で初回だけダークテーマ向けに塗り替える（`BuildDarkMarkdownHighlighting`）。
- JSON にコメント（`//`）の着色は入れていない。JSON 仕様にコメントは無く、
  ランタイム側（`serde_json`）も受け付けないため、書いてよいと誤解させないのが目的。

---

## 4. 大きいファイルの扱い

`max_editable_bytes`（既定 **1 MiB**）を超えるファイルは**読み取り専用タブ**で開く。

- 「開けない」ではなく「編集させない」にして、中身の確認だけはできるようにする。
- 理由（サイズと上限）は `EditorLog` へ出す。
- 上限は `editor/config/text_editable_extensions.json` で変えられる。
  0 以下を書いた場合は「何も編集できないエディタ」になるのを防ぐため既定へ丸める。

---

## 5. テスト・検証

| プロジェクト | 種類 | 内容 |
|---|---|---|
| `editor/tests/TextEditorLogicTests` | 自動（WPF 非依存） | 拡張子 → 言語、シーン・アクタの拒否、サイズ上限、JSON の読み込み・フォールバック、言語ごとの機能の有無。`dotnet run` で 21 件 |

```
dotnet run --project editor/tests/TextEditorLogicTests
```

同梱の `editor/config/text_editable_extensions.json` を実際に読んで、
組み込み既定と一致する（＝開発環境と配布環境で開ける形式が変わらない）ことも検証している。

---

## 6. 既知の制限

- **開いているタブの内容を外部の変更で読み直す仕組みは無い**（`.cs` を含む全種別）。
  自動再読込（[docs/editor_auto_reload.md](editor_auto_reload.md)）はランタイムへの
  ホットリロードとシーンの読み直しのための仕組みで、エディタのタブは対象外。
  外部エディタで書き換えたファイルは、タブを閉じて開き直すこと
  （そのまま保存するとタブの内容で上書きされる）。これはテキスト編集の追加で
  生まれた制限ではなく、従来からの挙動。
- CSV は無着色のプレーンテキスト編集（列の整列・区切りの着色は無い）。
- **専用エディタと同時に開くと保存を取り合う。** `.anim` をタイムラインで開いたまま
  テキストでも編集すると、後から保存したほうが相手の変更を丸ごと上書きする
  （タイムラインは自分のメモリ上の内容を書き出すため）。どちらか一方で編集すること。
  `.inputmap` も同じ。検出・警告の仕組みは無い（[backlog](backlog.md)）。
- `.anim` / `.inputmap` をテキストで壊すと、それぞれの専用エディタが開けなくなる。
  専用エディタで編集できることはそちらで行うこと。
