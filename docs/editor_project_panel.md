# プロジェクトパネル（正典）

SEED エディタ（`editor/`, WPF）のプロジェクトパネル（アセットブラウザ）の仕様書。
**タイルの表示・右クリックメニュー・プレビュー生成を触るときは必ずここを見る。**

パネル本体は役割ごとに部分クラスへ分けてある。

| ファイル | 役割 |
|---|---|
| `editor/src/Panels/ProjectPanel.xaml(.cs)` | ツリー／ファイルグリッドの構築、選択・ドラッグ・リネーム・ファイル操作、右クリックメニューの組み立て |
| `editor/src/Panels/ProjectPanel.Tabs.cs` | フォルダ位置のタブ（状態切替・タブバーの構築） |
| `editor/src/Panels/ProjectPanel.StatePersistence.cs` | タブ状態の保存タイミング（デバウンス）と復元の適用（[§9](#9-状態の永続化)） |
| `editor/src/Panels/ProjectPanel.Audio.cs` | 音声アセット向けメニュー（先頭無音カット） |
| `editor/src/Panels/ProjectPanel.CopyPath.cs` | 「パスをコピー」メニュー（絶対パス / `assets://` パス） |
| `editor/src/Panels/ProjectPanel.Visibility.cs` | 隠しファイルの表示制御（トグル・薄表示・ツリー再構築） |
| `editor/src/Panels/ProjectPanel.ModelThumbnails.cs` | 3D モデルのサムネイル要求とタイルへの反映（[§5](#5-3d-モデルのサムネイル)） |
| `editor/src/Panels/ProjectPanel.TextEdit.cs` | 「テキストエディタで開く」メニュー（[docs/editor_script_panel.md](editor_script_panel.md)） |

判定・変換の**純ロジックは WPF 非依存**として `editor/src/Assets/` に置き、
単体テスト（`editor/tests/ProjectPanelLogicTests`）が直接リンクして検証している。
これらのファイルが WPF 型を使い始めるとテストのビルドが壊れる＝設計崩れの検知器になる。

| ファイル | 役割 |
|---|---|
| `editor/src/Assets/AssetUriPath.cs` | 絶対パス ⇔ `assets://` 仮想パス／アセットルート相対パスの変換（正典） |
| `editor/src/Assets/ProjectPanelStateStore.cs` | タブ状態の JSON モデルと読み書き、パスの相対化・絶対化・間引き（[§9](#9-状態の永続化)） |
| `editor/src/Assets/AssetPreviewKinds.cs` | 拡張子 → プレビュー種別（画像／フォント／モデル／無し）と寸法取得対象の対応表 |
| `editor/src/Assets/AssetPreviewCacheKey.cs` | プレビューのキャッシュキー（パス＋更新時刻＋サイズ＋派生条件）。エディタ内メモリキャッシュ用 |
| `editor/src/Assets/ModelThumbnailCacheKey.cs` | モデルサムネイル PNG の置き場とファイル名。**ランタイム（Rust）と規則を共有する**（[§5](#5-3d-モデルのサムネイル)） |
| `editor/src/Assets/ImagePixelSize.cs` | ピクセル寸法の値と表示書式（`1024×512` / `1024×512 px`） |
| `editor/src/Assets/ProjectPanelVisibilityRules.cs` | 非表示ルールの読み込みと照合（[§8](#8-隠しファイルの非表示)） |

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
| モデルサムネイル | ランタイム（wgpu）がオフスクリーン描画 → PNG キャッシュ | 別プロセス（IPC 往復。詳細は §5） |
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

読めなかったときのフォールバックは専用のフォントアイコン（`Icon.File.Font`）。

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

## 5. 3D モデルのサムネイル

対象: `.glb` / `.gltf` / `.obj`（`AssetPreviewKinds` の表で定義。ランタイムの
`loader::load_model` が読める形式と一致させてある。`.fbx` は非対応なので対象外）。

画像・フォントと違い、**エディタ単独では描けない**。glTF を解釈して PBR で描くには
レンダラが要るので、**ランタイム（wgpu）にオフスクリーンで描かせ、PNG をキャッシュして
エディタはそれを表示する**という分担にしている。

### 表示の手順（3 段構え）

1. **キャッシュ PNG があれば即表示**する。ランタイムには一切触らない。
   生成済み PNG はただの画像なので、画像サムネイルと同じ経路（`LoadImagePreviewAsync`）で読む。
2. 無ければ **ランタイムへ生成を頼み**、`THUMBNAIL_DONE` が返ってからタイルへ貼る。
3. **ランタイムが居ない／Play 中は何もしない**（形式アイコンのまま）。
   Edit へ戻ってフォルダを開き直せば、また要求が飛ぶ。

### IPC

```
THUMBNAIL:<要求ID>,<一辺px>,<assets:// パス>
  → THUMBNAIL_DONE:<要求ID>,<書き出した PNG の絶対パス>
  → THUMBNAIL_FAILED:<要求ID>,<理由>
```

- 図鑑の `RENDER_ACTOR_THUMBNAIL`（§ `docs/editor_mcp.md`）と違い、**応答に要求 ID が付く**。
  パネルは複数のタイルを並べて頼むので、要求と応答が 1 対 1 で往復するとは限らず、
  届いた PNG をどのタイルへ貼るかを ID で対応付ける必要があるため。
- **パスにカンマは使えない**（引数の区切りと区別できない）。失敗理由はカンマを含みうるので、
  エディタ側は**最初のカンマだけ**で ID と本体に割る。
- **失敗応答にも必ず要求 ID を付ける。** エディタは「その ID の応答が来るまで」
  タイル 1 枚ぶんの送信枠を握っているので、ID 無しで失敗を返すとどの枠を解放してよいか
  分からず、以降の要求が詰まる。ID を読めた後の検証（サイズ範囲・拡張子・パス空）で
  落ちた場合も ID を添える。ID 無しになるのは行の形自体が壊れているときだけ。
- 一辺は 16〜512px。**既定値 128px の所有者はエディタ側**（`ModelThumbnailCacheKey.DefaultSizePx`）で、
  ランタイムは受け取った値の範囲を検証するだけ。両方に既定値を置くと片方だけ変えたときに静かにずれる。
- 書式の正典は `runtime/src/engine/core/renderer/thumbnail/request.rs`。

### キャッシュの場所とファイル名

```
<プロジェクト>/cache/thumbnails/<ハッシュ>.png
```

- `cache/` の決め方はモデル変換キャッシュ（`.smdl`）と同じ `asset_cache.rs` の `cache_dir()`
  ＝ アセットルートの親。エディタ側の写しは `ModelThumbnailCacheKey.CacheDirForAssetsRoot`。
- ハッシュは **FNV-1a 64bit**。材料は `<正規化した相対パス>|<更新時刻(Unix秒)>|<ファイルサイズ>|<一辺px>`。
  - パスは `assets://` を外し、区切りを `/` に統一し、**ASCII 範囲だけ**小文字化する
    （Unicode 全体の小文字化は Rust と .NET で結果が違うため）。
  - 更新時刻を**秒**までにしているのは、.NET（100ns 刻みの FILETIME 由来）と Rust で
    端数の丸めが一致する保証がないから。同じ 1 秒の中でサイズを変えずに内容だけ
    差し替えるとキャッシュが古いままになるが、ファイルを触り直せば解消する。
- **探す側（C#）と書く側（Rust）が同じ規則でなければならない。** 1 文字ずれると
  エディタは永久にキャッシュを見つけられず、起動のたびに全部描き直しになる。
  そのため同じ固定入力・同じ期待値のテストを両言語に置いてある（§6）。

### 負荷の抑え方

| 仕掛け | 効果 |
|---|---|
| エディタ側の同時送信上限（2 件） | ランタイムは 1 件ずつしか描かないので、投げすぎても速くならない |
| 一覧を作り直したら要求を忘れる | フォルダを移ったら未送信分も応答待ちも捨てる。遅れて届いた応答は貼る先が無いので素通りする（PNG はキャッシュに残るので開き直せば即ヒット） |
| ランタイム側の待ち行列（上限 64 件） | 一度に届いても同時に走るのは常に 1 件。あふれた分は最古から失敗応答を返す |
| Play 中は処理しない | 撮影はワールド線とカメラを差し替えるため。Edit へ戻れば続きから再開する |

### 撮り方（現在のシーンを壊さない理由）

図鑑サムネイル（`thumbnail_ops.rs`）と**同じ状態機械を共有**している。
隔離ワールド線へ被写体を 1 体だけ読み込み、その間だけ `active_world_line` を差し替え、
撮り終えたら despawn して元へ戻す。ユーザーのシーンはエンティティごと生き残ったまま
「描かれない」だけになる。背景の抜き方（ID パスのアルファをマスクにする）も共通。

**提示フレーム（スワップチェーン）は一切使わない。** 撮影フレームは専用の
オフスクリーンターゲット（`runtime/src/engine/core/renderer/thumbnail/target.rs`）へ描き、
present しない。出力先の切り替えは `Renderer::begin_offscreen_frame` の 1 か所だけで、
途中の描画コマンド列は通常フレームとまったく同じものが通る。

| | 内容 |
|---|---|
| 切り替えの単位 | **セッション**（`thumbnail_session.is_some()` の間はすべてのフレームがオフスクリーン）。ジョブ単位で切り替えると、ジョブの隙間（次の要求を待つ最大 2 秒）に「被写体が消えた空の世界」が提示されてしまう |
| ターゲットの大きさ | 常に**描画解像度**（`Renderer::render_size()`）。G-Buffer・深度・ID バッファと同じ基準にすることで、中間バッファのサイズ規則を 1 つも変えずに済み、カラーと ID マスクの解像度が必ず一致する（合成はこの一致を要求する）。要求サイズ（16〜512px）への縮小は従来どおり読み戻し後に「中央正方形を切り出して縮小」で行う |
| ターゲットの寿命 | `Renderer` が 1 枚だけ持ち、描画解像度が変わったときだけ作り直す（連続生成のたびに確保し直さない） |
| 画面の見え方 | 生成中は present が止まるので、**ビューポートはセッション開始直前の絵で静止する**。被写体は 1 フレームも映らない |
| スクリーンショット | 環境変数のフレームダンプも `SCREENSHOT:` も「提示したフレーム」専用なので、撮影フレームは対象外。`SCREENSHOT:` 要求はセッションが畳まれた後の提示フレームで処理される |

Play 中に処理しないのは、present が止まるとゲーム画面が数秒固まって見えるため
（セッションは最後の要求から 2 秒残る）。「プレイ中の画面が一瞬別物になる」ほうは
オフスクリーン化で解消している。

違いは 3 つだけで、いずれも `ThumbnailPlan` が持つ:

| | 図鑑 | モデル一覧 |
|---|---|---|
| 被写体 | `.actor` ファイル | モデル 1 体だけを載せた**その場で作る仮アクタ** |
| 視点 | 真横 / 正面 / 真上 | 斜め前上から（方位 35°・仰角 25°） |
| 応答 | `RENDER_ACTOR_THUMBNAIL_DONE` | `THUMBNAIL_DONE:<ID>,` |

仮アクタは `ModelComponent::empty().to_data()` にモデルパスと単位行列を入れ、
`scene::build_actor` へ渡して作る。読み込み・GPU アップロード・インスタンスバッチ生成は
すべて `.actor` から読むときと同じ経路を通るので、**描画に至る道筋が実際のシーンと変わらない**
（モデルの読み込みは同期なので、ロード完了を待つ仕掛けは要らない）。

カメラの自動フィットは `renderer/thumbnail/view_basis.rs` の一般形に集約してある。
軸に沿ったビュー（図鑑）でも斜めのビュー（モデル一覧）でも同じ式で、
軸平行な箱をベクトル `a` へ射影した幅 `|a.x|·sx + |a.y|·sy + |a.z|·sz` から
正射カメラの半高と視点距離を決める。

実装:
- ランタイム: `runtime/src/engine/core/app_base/app/thumbnail_ops.rs`（進行）、
  `runtime/src/engine/core/renderer/thumbnail/`（キャッシュ鍵・プロトコル・待ち行列・構図・
  オフスクリーンターゲット）、`runtime/src/engine/core/renderer/mod.rs`
  （`begin_offscreen_frame` と `FrameOutput`）
- エディタ: `editor/src/Panels/ProjectPanel.ModelThumbnails.cs`（要求と反映）、
  `editor/src/Assets/ModelThumbnailCacheKey.cs`（キャッシュ鍵）

---

## 6. テスト・検証

| プロジェクト | 種類 | 内容 |
|---|---|---|
| `editor/tests/ProjectPanelLogicTests` | 自動（WPF 非依存） | `assets://` 相対化／絶対化、拡張子判定、キャッシュキー、寸法書式、非表示ルール、**モデルサムネイルのキャッシュ鍵**、**タブ状態の永続化**。`dotnet run` で 82 件 |
| `editor/tests/ProjectPanelPreviewProbe` | 手動（WPF 依存） | 実ファイルへ向けてフォント描画・寸法取得を走らせる検証用コンソール |

### ランタイムとのキャッシュ鍵の突き合わせ

モデルサムネイルのキャッシュ鍵は、**探す側（C#）と書く側（Rust）が別々に実装している**。
片方だけ直すと静かに壊れるので、**同じ固定入力に対する同じ期待値**を両言語のテストへ書いてある。

| 言語 | 場所 |
|---|---|
| Rust | `runtime/.../renderer/thumbnail/cache_key.rs` の `fixed_input_produces_the_agreed_file_name` ほか |
| C# | `editor/tests/ProjectPanelLogicTests` の `ModelKeyMatchesRuntimeFixture` ほか |

```
入力: assets://mainGame/models/Yasi.glb / 更新時刻 1700000000 / サイズ 123456 / 一辺 128
キー: maingame/models/yasi.glb|1700000000|123456|128
結果: 63cf730ec7b8e0eb.png
```

**この値を変えるときは必ず両方のテストを同時に直すこと。**

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

## 7. 既知の制限

- ファイル名が 2 行になる画像タイルは、サムネイル＋名前＋キャプションの合計が
  タイル高さをわずかに超える（サムネイル導入時からの既存挙動。`Border` は
  クリップしないので隣の行と詰まって見える）。
- フォントサムネイルはセッション中キャッシュへ載り続ける（1 枚あたり 96×96 の
  Pbgra32 ≒ 36KB）。フォントが数百個あるプロジェクトではメモリを見ること。
- `.ttc` は先頭に見つかったフェイスで描く（フェイス選択の UI は無い）。

---

## 8. 隠しファイルの非表示

**パネルに出す必要の無いファイル・フォルダは既定で隠す。** 対象は
「エディタ・OS・外部ツールが勝手に作る作業ファイル」と「エンジンが生成する中間データ」の 2 種類で、
どちらもユーザーが直接開く場面が無い。

### ルールの置き場（データドリブン）

| ファイル | 役割 |
|---|---|
| `editor/config/project_panel_rules.json` | 既定ルール（正典）。**ここを編集すればビルド無しで変えられる** |
| `editor/src/Assets/ProjectPanelVisibilityRules.cs` | 読み込み・照合。JSON が無い／壊れている場合の組み込み既定も持つ |

読み込みは `EditorPaths.ConfigDir`（開発配置＝`editor/config`、配布配置＝exe の隣の `config`）から 1 度だけ。
**失敗しても起動は止めない**（必ず組み込み既定へフォールバックし、理由を `EditorLog` へ出す）。
組み込み既定と JSON の内容が一致することは `ProjectPanelLogicTests` が実ファイルを読んで検証している。

### 既定で隠すもの

| 種別 | 対象 | 理由 |
|---|---|---|
| フォルダ | `.backup` | シーン保存の世代バックアップ（エディタが自動生成） |
| フォルダ | `__MACOSX` | macOS で作った zip を展開したときの残骸 |
| 両方 | 先頭がドットの名前（`hide_dot_prefixed`） | `.git` / `.vscode` / `.DS_Store` など |
| ファイル | `*.lock` / `*.tmp` / `*.bak` | エディタのロック・一時ファイル・手動バックアップ |
| ファイル | `*.blend1`・`*.blend?` | Blender の世代バックアップ |
| ファイル | `Thumbs.db` / `desktop.ini` / `.DS_Store` / `._*` | OS が作るメタデータ・リソースフォーク |
| ファイル | `*.tvox` / `*.tscatter` / `*.tcover` | **地形の中間データ**（下記） |

### 地形の中間データを隠す根拠

`.tvox`（ボクセル）・`.tscatter`（散布）・`.tcover`（地表カバー）は、いずれも
ランタイム（Rust）が書き出す**独自バイナリ**（先頭 magic は `TVOX` / `TSCT` / `TCOV`。
`runtime/src/engine/terrain/` の各 `write_chunk`）で、地形ブラシ・散布ブラシ・雪シミュレーションの
結果を機械的に記録したもの。人が開いて意味のある内容ではなく、編集手段もエディタの地形ツールしかない。

**`terrain_meta.json` は隠さない。** これだけは `serde_json` のテキストで、
チャンクごとの当たり判定 ON/OFF とデシメート強度という「読んで意味のある値」が入っている
（`runtime/src/engine/terrain/meta.rs`）。壊れても既定値へフォールバックするので手編集も安全。

### 「隠しファイル」トグル

ツールバー右（「新規作成」の左）の **`隠しファイル`** トグル。

- ON にすると隠し対象も一覧に出る。ただし**薄く（不透明度 0.45）描き**、
  ツールチップの先頭に「通常は非表示のファイル」と出して、普段触らないものだと分かるようにする。
- 状態は環境設定（`editor/settings/editor_preferences.json` の `show_hidden_project_files`）へ保存し、
  プロジェクトを跨いで保たれる。
- 切り替えるとフォルダツリーを作り直し、開いていたフォルダまで展開し直す
  （ツリーは遅延生成なのでフィルタ変更には再構築が要る）。
- 表示されているだけで、コピー・削除・リネームなどの操作は通常のファイルと同じにできる。

### 掛け忘れが起きない作り

列挙は `ProjectPanel.xaml.cs` の `EnumerateSafe` / `HasSubdirectorySafe` の 2 つに集約してあり、
フィルタはその中だけで掛けている。ツリー・ファイルグリッド・タブ復元はすべてここを通るので、
新しい表示経路を足してもフィルタが外れない。

### パッケージ収録ルール（`PackagingRules`）との関係

**目的が違うので混ぜない。**

| | 判断すること | `.tvox` の扱い |
|---|---|---|
| `ProjectPanelVisibilityRules` | パネルに**表示**するか | 隠す（人は触らない） |
| `PackagingRules` | 配布 PAK に**収録**するか | 収録する（ランタイムが読む） |

同じ拡張子が正反対の扱いになるのが正常。片方の都合でもう片方を書き換えないこと。


---

## 9. 状態の永続化

**エディタを閉じて開き直しても、プロジェクトパネルは前回の見え方で復帰する。**
保存するのは次の 4 つ。いずれも「作業者個人の見え方」であってプロジェクトの内容ではない。

| 覚えるもの | 単位 |
|---|---|
| 開いているタブの一覧と、各タブが開いているフォルダ | タブごと |
| アクティブなタブ | プロジェクトごと |
| フォルダツリーの展開集合 | タブごと |
| ファイル一覧の選択アイテムと垂直スクロール位置 | タブごと |

「隠しファイルを表示」トグルは対象外。こちらは**プロジェクトを跨ぐ好み**なので
`EditorPreferences`（`editor/settings/editor_preferences.json`）へ既に入っている（[§8](#8-隠しファイルの非表示)）。

### 置き場と書式

`editor/settings/project_panel_state.json`。プロジェクトごとに 1 エントリ。

```json
{
  "format_version": 1,
  "projects": {
    "d:/seed_projects/warashibe": {
      "active_tab": 1,
      "tabs": [
        { "path": "", "expanded": [ "" ], "selected": null, "scroll": 0 },
        {
          "path": "mainGame/textures",
          "expanded": [ "", "mainGame", "mainGame/textures" ],
          "selected": "mainGame/textures/a.png",
          "scroll": 120.0
        }
      ]
    }
  }
}
```

- **キーはプロジェクトルートの正規化パス**（絶対パス → スラッシュ区切り → 小文字）。
  1 つのエディタ設定フォルダを複数プロジェクトで共有するため。
- **`path` / `expanded` / `selected` はアセットルート相対・スラッシュ区切り**。
  空文字がアセットルート自身を表す。絶対パスで書くとプロジェクトフォルダを
  移動・リネームしただけで全タブが無効になるため、相対で持つ。
- `.scene` ごとのビュー状態（`view_state.json`）と同じ置き場・同じ流儀にしてある。
  プロジェクトフォルダへ書くと、フォルダを開いただけでチームに差分が出てしまう。

### 保存のタイミング

デバウンス方式。**最後の変更から 500ms（`TabStateSaveDebounceMs`）後に 1 回だけ書く。**

| きっかけ | 出どころ |
|---|---|
| タブの追加・削除・切替、フォルダ移動 | `ProjectPanel.Tabs.cs` の各操作関数が `RequestTabStateSave()` を呼ぶ |
| ツリーの展開・折りたたみ | `TreeViewItem.Expanded` / `Collapsed` をツリー側で一括受信（バブリング） |
| スクロール | `ScrollViewer.ScrollChanged`。デバウンスが効くので実質「スクロールが止まったら保存」 |
| エディタ終了 | `MainWindow` の `OnClosing` から `PanelProject.FlushTabState()` |

毎回書かないのは、ツリーを連続開閉しただけでファイル I/O が頻発するため。
書き込み時は画面の最新状態を `CaptureActiveTabState()` でアクティブタブへ吸い上げてから写す。

**アセットフォルダが使えない状態（警告オーバーレイ表示中）では保存しない。**
その状態はツリーもタブバーも空なので、書くと前回の正しい状態を壊す。

### 復元のタイミングと間引き

`SetAssetsPath` → アセットルートが「利用可能」と判定された後、`InitTabs` の中で復元する。
**いま実在しないパスは黙って間引く**（エディタの外でフォルダを消しても起動は壊れない）。

| 保存されていたもの | 実体が無いとき |
|---|---|
| タブのフォルダ | **そのタブごとスキップ**。残ったタブに合わせてアクティブ添字を詰め直す |
| 展開集合の要素 | その要素だけ落とす |
| 選択アイテム | 選択なしにする（ファイル・フォルダどちらでも実在すれば復元する） |
| アクティブ添字 | 範囲外なら 0 |

1 枚も残らなければ、従来どおりアセットルートを開いたタブ 1 枚から始める。
タブ数は `MaxRestoredTabs`（32 枚）で頭打ち。壊れた JSON で起動が重くならないための歯止め。

### 描画は「アクティブタブだけ」

復元時に全タブぶんファイル一覧を作り直すと、**画像・モデルのサムネイル要求がタブ枚数ぶん
一気に積まれる**（モデルは IPC でランタイムに描かせるので特に高い、[§5](#5-3d-モデルのサムネイル)）。
そのため復元では、非アクティブなタブは状態を保持するだけで描かず、
アクティブタブ 1 枚だけを `ApplyTabState` で適用する。
`InitTabs` は「復元したか」を返し、復元した場合は呼び出し側が
`RefreshFileGrid()` を重ねて呼ばない（二重構築＝要求の二重発行を防ぐ）。

### 壊れたファイルの扱い

| 状況 | 挙動 |
|---|---|
| ファイルが無い | 初回起動の正常な状態。警告も出さず既定の 1 枚で始める |
| JSON が壊れている | ログに理由を残して空状態で起動。次の保存で健全なファイルに上書きされる |
| `format_version` が無い | バージョン導入前のファイルとして v1 相当に読む |
| `format_version` が未来 | 警告を残しつつ読める範囲は読む。新しい版で作業した後に古いエディタを 1 回起動しただけでタブ構成が消えるほうが害が大きいため |
| `scroll` が NaN / 負値 / 無限大 | 0 に丸める（`ScrollViewer` へ渡してレイアウトを壊さない） |
| 相対パスが `..` でアセット外へ出る | 拒否する（保存ファイルを書き換えてアセット外を開かせられないように） |

### 検証

- 単体テスト: `editor/tests/ProjectPanelLogicTests`（相対化・絶対化、間引き、アクティブ添字の
  付け替え、JSON の往復、壊れたファイルの扱い）。
- 実機: ヘッドレス起動で復元結果をログへ出す。

```
プロジェクトパネルの状態を復元しました: タブ 3 枚 / アクティブ 1 / 場所 <絶対パス>
```
