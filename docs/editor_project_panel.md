# プロジェクトパネル（正典）

SEED エディタ（`editor/`, WPF）のプロジェクトパネル（アセットブラウザ）の仕様書。
**タイルの表示・右クリックメニュー・プレビュー生成を触るときは必ずここを見る。**

パネル本体は役割ごとに部分クラスへ分けてある。

| ファイル | 役割 |
|---|---|
| `editor/src/Panels/ProjectPanel.xaml(.cs)` | ツリー／ファイルグリッドの構築、選択・ドラッグ・リネーム・ファイル操作、右クリックメニューの組み立て |
| `editor/src/Panels/ProjectPanel.Tabs.cs` | フォルダ位置のタブ（セッション限りの状態切替） |
| `editor/src/Panels/ProjectPanel.Audio.cs` | 音声アセット向けメニュー（先頭無音カット） |
| `editor/src/Panels/ProjectPanel.CopyPath.cs` | 「パスをコピー」メニュー（絶対パス / `assets://` パス） |

判定・変換の**純ロジックは WPF 非依存**として `editor/src/Assets/` に置き、
単体テスト（`editor/tests/ProjectPanelLogicTests`）が直接リンクして検証している。
これらのファイルが WPF 型を使い始めるとテストのビルドが壊れる＝設計崩れの検知器になる。

| ファイル | 役割 |
|---|---|
| `editor/src/Assets/AssetUriPath.cs` | 絶対パス ⇔ `assets://` 仮想パスの変換（正典） |
| `editor/src/Assets/AssetPreviewKinds.cs` | 拡張子 → プレビュー種別（画像／フォント／無し）と寸法取得対象の対応表 |
| `editor/src/Assets/AssetPreviewCacheKey.cs` | プレビューのキャッシュキー（パス＋更新時刻＋サイズ＋派生条件） |
| `editor/src/Assets/ImagePixelSize.cs` | ピクセル寸法の値と表示書式（`1024×512` / `1024×512 px`） |

実処理（WPF 依存）は `editor/src/Controls/` に置く。

| ファイル | 役割 |
|---|---|
| `editor/src/Controls/FileTypeIcons.cs` | 拡張子 → 形式アイコン（PNG / ベクター）の対応表。詳細は [docs/editor_icons.md](editor_icons.md) |
| `editor/src/Controls/FontThumbnailRenderer.cs` | フォントで描いたサンプル文字列のサムネイル生成 |
| `editor/src/Controls/ImageDimensionsProbe.cs` | 画像のピクセル寸法をヘッダだけ読んで取得 |

---

## 1. タイルの構成

タイル 1 枚は `Border`（固定サイズ）の中に `StackPanel` を縦に積んだもの。

```
[ アイコン or サムネイル ]   ← Image。形式アイコンで描き、プレビューが取れたら差し替え
[ ファイル名（最大 2 行）]   ← TextBlock。Tag = "TileNameBlock"（リネーム時の差し替え対象）
[ キャプション（1 行）   ]   ← TextBlock。画像の寸法など。内容が決まるまでは Collapsed
```

寸法・書式の定数は `ProjectPanel.xaml.cs` の「タイルの寸法・書式」節に集約してある
（`TileWidth` / `TileBaseHeight` / `TileCaptionHeight` / `TileHeight` など）。

**キャプション行の場所は全タイルで常に確保する。** 出る／出ないで高さが変わると
`WrapPanel` の行ごとに段差ができるため、`TileHeight = TileBaseHeight + TileCaptionHeight` を
フォルダも含めた全タイルに適用している。

### プレビューの原則

**まず形式アイコンを描き、プレビューが取れたときだけ差し替える。**
生成前・生成中・生成失敗の間は形式アイコンが見えたままになる。
読めないフォント・対応コーデックの無い画像でもタイルが空にならないのはこのため。

| 種別 | 生成 | スレッド |
|---|---|---|
| 画像サムネイル | `BitmapImage`（`DecodePixelWidth` 指定） | `Task.Run`（デコード後 Freeze して UI へ） |
| フォントサムネイル | `FontThumbnailRenderer.Render` | UI スレッド（`DispatcherPriority.Background` へ後回し） |
| 画像寸法 | `ImageDimensionsProbe.GetAsync` | `Task.Run` |

フォントだけ UI スレッドなのは、`GlyphRun` / `DrawingVisual` / `RenderTargetBitmap` が
**生成したスレッドに紐づく**ため。ワーカースレッドで作ったものは UI へ渡せない。
代わりに Dispatcher の Background 優先度へ回して、一覧表示と入力を先に通している。

---

## 2. 右クリック「パスをコピー」

`ProjectPanel.CopyPath.cs`。ファイル・フォルダ・種別を問わず、選択中のアイテムに対して出る。

| メニュー | 出る場所 | 内容 |
|---|---|---|
| パスをコピー → 絶対パス | アイテムの右クリック | `D:\SEED_projects\X\assets\mainGame\fonts\a.otf` |
| パスをコピー → assets:// パス | 同上 | `assets://mainGame/fonts/a.otf` |
| 現在のフォルダのパスをコピー → 絶対パス / assets:// パス | フォルダ空白部の右クリック | 開いているフォルダぶん |

- **複数選択は改行区切り**（1 行 1 パス）。
- `assets://` はアセットルート配下のものだけ作れる。ルート外を選んでいるときは項目を
  **無効化**し、理由をツールチップで出す（黙って絶対パスを入れたりしない）。
- 変換規則は `AssetUriPath` が唯一の正典。前方一致ではなく `Path.GetRelativePath` の結果で
  ルート外を判定するので、`assets` と `assetsBackup` のような紛らわしい兄弟フォルダを取り違えない。
- クリップボードは OS 共有資源で他プロセスと競合するため、失敗しても数回だけ再試行し、
  最終的に駄目ならログ（`EditorLog`）に残して諦める（作業を止めるダイアログは出さない）。

---

## 3. フォントのプレビューサムネイル

対象: `.ttf` / `.otf` / `.ttc`（`AssetPreviewKinds` の表で定義）。

1. `GlyphTypeface` としてフォントファイルを直接開く。開けなければ
   `Fonts.GetFontFamilies(Uri)` でファイル内のフェイスを列挙する（`.ttc` 対策）。
   **インストール済みフォントの検索は経由しない**ので、プロジェクト内の未インストール
   フォントもそのまま描ける。
2. サンプル文字列は候補表（`Aa あ` → `Aa` → `AB` → `123`）から、
   **そのフォントが cmap に実際に持っている文字だけ**で選ぶ。
   欧文専用フォントは `Aa` になり、豆腐（.notdef）は描かれない。
   1 文字も描けなければサムネイル生成を諦めて形式アイコンのままにする。
3. 文字サイズは「横幅で決まる上限」と「行高で決まる上限」の小さい方。中央寄せで焼く。
4. `RenderTargetBitmap`（96px 角、Pbgra32、透過背景）へ描いて Freeze。

結果は `AssetPreviewCacheKey`（パス＋更新時刻＋サイズ＋一辺）でキャッシュする。
**読めなかったこと（null）もキャッシュする**ので、壊れたフォントを一覧の再描画のたびに
開き直さない。フォントを差し替えれば更新時刻が変わって描き直される。

専用のベクターアイコンはまだ無いので、フォールバックの形式アイコンは文書アイコン
（`Icon.File.Text`）を流用している（[backlog](backlog.md) 参照）。

---

## 4. 画像の縦横サイズ表示

対象: `AssetPreviewKinds.SupportsPixelSize` が true の拡張子
（`.png .jpg .jpeg .bmp .gif .tif .tiff .ico .tga .webp .dds .hdr .exr`）。

- 取得は **ヘッダ読みだけ**。`BitmapDecoder.Create(stream, DelayCreation | IgnoreColorProfile,
  BitmapCacheOption.None)` でピクセルのデコードを遅延させ、`Frames[0]` の幅・高さだけ読む。
- 取得は `Task.Run`（ワーカースレッド）で行い、UI を止めない。
- 取れたら **名前の下のキャプション**（`1024×512`）と **ツールチップ**（絶対パスの次の行に
  `1024×512 px`）へ反映する。取れなければ何も出さない（従来どおりの見た目）。
- WIC に対応コーデックが無い形式（`.tga` / `.dds` / `.hdr` / `.exr`）は取得できないので
  キャプションが出ない。表に入れてあるのは「将来コーデックが入れば自動で出る」ため。
- 結果は成功・失敗ともキャッシュ（キーは `AssetPreviewCacheKey`）。

---

## 5. テスト・検証

| プロジェクト | 種類 | 内容 |
|---|---|---|
| `editor/tests/ProjectPanelLogicTests` | 自動（WPF 非依存） | `assets://` 相対化、拡張子判定、キャッシュキー、寸法書式。`dotnet run` で 26 件 |
| `editor/tests/ProjectPanelPreviewProbe` | 手動（WPF 依存） | 実ファイルへ向けてフォント描画・寸法取得を走らせる検証用コンソール |

プローブの使い方（入力は読むだけ。書き換えない）:

```
dotnet run --project editor/tests/ProjectPanelPreviewProbe -- "<フォルダ or ファイル>" [...] --out "<PNG 出力先>"
```

- フォントは「生成できたか」に加えて**不透明ピクセル比率**を出す。
  真っ白（何も描けていない）サムネイルを成功と誤認しないための検算で、0% なら実質失敗。
- `--out` を付けるとフォントサムネイルを PNG で書き出す（目視確認用）。
- 先頭で 1 件だけ `GetAsync`（ワーカースレッド経路）を走らせ、
  パネルが実際に通る経路が例外にならないことを確かめる。

---

## 6. 既知の制限

- ファイル名が 2 行になる画像タイルは、サムネイル＋名前＋キャプションの合計が
  タイル高さをわずかに超える（サムネイル導入時からの既存挙動。`Border` は
  クリップしないので隣の行と詰まって見える）。
- フォントサムネイルはセッション中キャッシュへ載り続ける（1 枚あたり 96×96 の
  Pbgra32 ≒ 36KB）。フォントが数百個あるプロジェクトではメモリを見ること。
- `.ttc` は先頭に見つかったフェイスで描く（フェイス選択の UI は無い）。
