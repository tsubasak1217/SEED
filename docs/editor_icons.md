# エディタのアイコン（正典）

SEED エディタ（`editor/`, WPF）のアイコン運用の正典ドキュメント。
**エディタに新しいボタン・パネル・コンポーネント種別・ファイル形式を足すときは必ずここを見る。**
運用ルール（何を必須とするか）は `.claude/rules/editor-icons.md` にまとめてある。

## 1. 2 系統のアイコンと使い分け

エディタのアイコンには **PNG（ユーザー資産）** と **ベクター（MDI）** の 2 系統がある。
**アイコン資産の置き場は `editor/resources/icons/` に統一**してある。

| 系統 | 置き場 | 使う場所 |
|---|---|---|
| PNG（ユーザーが自分で用意したもの） | `editor/resources/icons/{common,folderview,playbar,toolbar,viewport}/*.png` | **既に PNG が割り当てられている箇所はそのまま PNG を使う**。具体的には ①ビューポートのギズモツールバー（select / translate / rotate / scale）②プレイバーの Play / Pause / Stop ③検索ボックス ④プロジェクトパネル等のファイル形式アイコン（従来から割り当てのある拡張子・フォルダ）⑤新規作成ウィンドウの項目アイコン |
| ベクター（Material Design Icons） | `editor/resources/icons/Icons.xaml` | **新規に足す箇所はすべてこちら**。PNG の用意が無い場所（パネルタブ、メニュー、コンポーネント種別、エラー一覧、パッケージング、後発のファイル形式など） |

**PNG を新しく増やさない／既存の PNG をベクターへ置き換えない。**
どちらもユーザーが意図して選んだ見た目であり、勝手に差し替えない。

### ベクター側の管理

| 項目 | 内容 |
|---|---|
| アイコンセット | Material Design Icons（Iconify プレフィックス `mdi`） |
| ライセンス | Apache License 2.0（再配布・改変可。帰属表示のみ必要） |
| 形式 | 24x24 viewBox の単一 `<path>`。SVG のパス data をそのまま WPF の `Geometry` として埋め込む |
| 定義場所 | `editor/resources/icons/Icons.xaml`（**自動生成。手で編集しない**） |
| 生成器 | `editor/gen_icons.py` |
| 整合チェッカ | `editor/check_icons.py`（キー参照の突き合わせ） |
| 読み込み検証 | `editor/verify_icons.ps1`（WPF で実際に読めるかの確認） |

ベクターは高 DPI でも滲まず、色は Foreground の継承で切り替わる。
新規箇所にベクターを使う理由はここにある。

## 2. 使い方

### 2-1. 基本は `AppIcon`（`editor/src/Controls/AppIcon.cs`）

色を**ハードコードしない**のが原則。`AppIcon` の塗り色は
`TextElement.Foreground` にバインドしてあるので、親の Button / TextBlock /
パネルの `Foreground` をそのまま継承する。

XAML（ルート要素に `xmlns:ctrl="clr-namespace:SEEDEditor.Controls"` を足す）:

```xml
<Button Foreground="#CCCCCC" ToolTip="再生">
    <ctrl:AppIcon IconKey="Icon.Play" Width="16" Height="16"/>
</Button>
```

「アイコン＋ラベル」のボタン（旧 `Content="⚙ 設定"` 相当）:

```xml
<Button Click="OnTerrainSettings" ToolTip="地形設定ウィンドウを開く">
    <StackPanel Orientation="Horizontal" VerticalAlignment="Center">
        <ctrl:AppIcon IconKey="Icon.Settings" Width="13" Height="13"/>
        <TextBlock Text="設定" Margin="4,0,0,0" VerticalAlignment="Center"/>
    </StackPanel>
</Button>
```

コードビハインド:

```csharp
var icon = AppIcon.Create("Icon.Play", size: 14);   // 親の Foreground を継承
icon.SetBrush(Brushes.OrangeRed);                   // 明示指定したいときだけ
button.Content = AppIcon.WithText("Icon.Back", "戻る", 12);  // アイコン＋ラベル
```

`TextBlock` の `Inlines` の中へ差し込みたい場合は `InlineUIContainer` で包む
（実例: `HierarchyPanel.MakeInlineIcon`）。

### 2-2. `ImageSource` を要求される箇所は `IconImages`

AvalonDock の `LayoutContent.IconSource` や既存の `Image.Source` のように
WPF が `ImageSource` を要求する API では `AppIcon` を置けない。
その場合だけ `IconImages.Get(key)` が返す凍結済み `DrawingImage` を使う。

```csharp
content.IconSource = IconImages.Get("Icon.Panel.Project");
```

ファイル形式アイコンだけは例外で、PNG かベクターかの判断を `FileTypeIcons` が
内部で行うため、呼び出し側は `IconImages` を通さず `ImageSource` を直接受け取る。

```csharp
image.Source = FileTypeIcons.GetImage(ext);        // 拡張子 → PNG or ベクター
image.Source = FileTypeIcons.GetFolderImage(isEmpty);
```

`ImageSource` は Foreground を継承できないため、色は Icons.xaml の
`Icon.DefaultBrush`（`#DCDCDC`）1 箇所だけを参照する。
別色にしたいときは `IconImages.Get(key, brush)` を使う。

### 2-3. 対応表は 4 つのクラスに集約されている

| 対応表 | 場所 | 用途 |
|---|---|---|
| コンポーネント種別 TypeId → キー | `editor/src/Controls/ComponentIcons.cs` | インスペクタのヘッダー、コンポーネント追加ウィンドウ |
| ファイル拡張子 → PNG / キー | `editor/src/Controls/FileTypeIcons.cs` | プロジェクトパネル |
| パネル ContentId → キー | `editor/src/Controls/PanelIcons.cs` | AvalonDock のタブ見出し |
| キー → MDI 名 | `editor/gen_icons.py` の `CATALOG` | Icons.xaml の生成元 |

**各所に switch を複製しない。**必ずこの 4 つのどれかへ 1 行足す。

## 3. ファイル形式アイコンとフォールバック仕様

`FileTypeIcons` が唯一の対応表で、PNG かベクターかの判断もここで完結する。
呼び出し側は `GetImage(ext)` / `GetFolderImage(isEmpty)` が返す `ImageSource`
を載せるだけでよい。優先順位とフォールバックは次のとおり。

1. **PNG の割り当てがある拡張子**（`PngByExtension`）→ その PNG。
   ユーザーが用意した既存アイコンなので、ベクターより優先する。
2. **PNG が無くベクターキーがある拡張子**（`IconKeyByExtension`）→ ベクター。
   後発の形式（音声・データ・テキスト等）はこちら。
3. **どちらにも無い拡張子** → `image.png`（従来からの汎用フォールバック）。
   新しい拡張子が増えてもアイコンが欠けることはない。
   フォルダは `GetFolderImage(isEmpty)` が `folder.png` / `folder_empty.png`
   を出し分ける。
4. **サムネイルを持てる形式** → プレビューの**生成前・生成中・生成失敗**の
   すべての期間、形式アイコンを表示したままにする。
   `ProjectPanel.BuildFileItem()` が実装例で、
   - まず `FileTypeIcons.GetImage(ext)` の形式アイコンで `Image` を作り、
   - `FileTypeIcons.SupportsThumbnail(ext)` が true のときだけ
     `LoadImagePreviewAsync()` を走らせ、
   - **デコードに成功した場合に限り** `Image.Source` を実画像へ差し替える。

   デコードが失敗した場合は `Source` を触らないので形式アイコンが残る。
   サムネイル対象は現状 `.png .jpg .jpeg .bmp .gif .tga .hdr .exr .webp` のみで、
   3D モデルや音声は常に形式アイコン表示。

現在の拡張子対応:

| 拡張子 | アイコン |
|---|---|
| `.scene` | `folderview/scene.png` |
| `.actor` / `.actor2d` | `folderview/actor.png` / `folderview/actor2d.png` |
| `.inputmap` | `folderview/script.png` |
| `.cs` / `.lua` / `.py` / `.wgsl` | `folderview/script.png` |
| `.glb` / `.gltf` / `.obj` / `.fbx` | `folderview/model.png` |
| `.png` `.jpg` `.jpeg` `.bmp` `.gif` `.tga` `.hdr` `.exr` `.webp` | `folderview/image.png`（サムネイル対象） |
| フォルダ | `folderview/folder.png` / `folder_empty.png` |
| `.anim` | `Icon.File.Anim`（ベクター） |
| `.mat` | `Icon.File.Material`（ベクター） |
| `.postfx` | `Icon.File.PostFx`（ベクター） |
| `.tvox` | `Icon.File.Terrain`（ベクター） |
| `.rs` | `Icon.File.ScriptGeneric`（ベクター） |
| `.wav` / `.ogg` / `.mp3` / `.flac` | `Icon.File.Audio`（ベクター） |
| `.json` | `Icon.File.Json`（ベクター） |
| `.toml` / `.yaml` / `.yml` / `.ini` / `.cfg` / `.lock` | `Icon.File.Config`（ベクター） |
| `.txt` / `.md` / `.log` | `Icon.File.Text`（ベクター） |
| 上記以外 | `folderview/image.png`（フォールバック） |

新しい拡張子を足すときは、対応する PNG がユーザー資産として既にある場合のみ
`PngByExtension` へ、無ければ `IconKeyByExtension` へ 1 行足す。

## 4. アイコンを追加する手順

1. 使いたい MDI アイコンを https://icon-sets.iconify.design/mdi/ で探す。
   実在確認は次のコマンドでもよい:

   ```bash
   curl -s "https://api.iconify.design/mdi.json?icons=play,stop,content-save"
   ```

2. `editor/gen_icons.py` の `CATALOG` に `("Icon.<分類>.<用途>", "<mdi名>")` を 1 行足す。
   命名規約は `Icon.<分類>.<用途>`（分類なしの汎用操作は `Icon.<用途>`）。
   既存の分類は `Component` / `File` / `Node` / `Panel` / `Platform` / `Tool` / `Debug`。

3. 再生成する（Icons.xaml が丸ごと書き直される）:

   ```bash
   cd editor && python gen_icons.py
   ```

4. 該当する対応表（`ComponentIcons` / `FileTypeIcons` / `PanelIcons`）にも 1 行足す。

5. 検証する:

   ```bash
   dotnet build editor/SEEDEditor.csproj             # 0 エラー
   cd editor && python check_icons.py                # 未定義参照 0 件
   pwsh -File editor/verify_icons.ps1                # 全 Geometry が WPF で解決できる
   ```

   `check_icons.py` は Icons.xaml の `x:Key` とソース中の `"Icon.*"` 文字列を
   突き合わせる。**キーの綴り間違いは C#/XAML のビルドでは検出できない**
   （`TryFindResource` が null を返してアイコンが描かれないだけ）ので、
   このチェックが唯一の防御線。

6. このドキュメントのキー一覧（第 5 節）を更新する。

### 実装上の注意

- Icons.xaml の各 `Geometry` は先頭に **`F1`（FillRule=Nonzero）** を付けている。
  SVG の既定塗り規則に合わせるために必須で、省略すると WPF 既定の EvenOdd に
  なり、穴あき・自己交差のあるアイコンが欠けて描画される。`gen_icons.py` が自動で付ける。
- `AppIcon` は 24x24 の `Canvas` を `Viewbox` で等比縮小する構造。
  `Path` を直接 `Stretch="Uniform"` で描くとアイコンごとの余白量の差で
  見かけの大きさがバラつくため、この viewBox を挟む構造を崩さないこと。
- サイズはマジックナンバーで直書きせず、各クラスの `private const double ...IconSize` に置く。

## 5. アイコンキー一覧

#### 再生・実行制御

| アイコンキー | MDI アイコン名 |
|---|---|
| `Icon.Play` | `play` |
| `Icon.Pause` | `pause` |
| `Icon.Stop` | `stop` |

#### 汎用編集操作

| アイコンキー | MDI アイコン名 |
|---|---|
| `Icon.Save` | `content-save` |
| `Icon.Undo` | `undo` |
| `Icon.Redo` | `redo` |
| `Icon.Add` | `plus` |
| `Icon.Remove` | `minus` |
| `Icon.Close` | `close` |
| `Icon.Delete` | `trash-can-outline` |
| `Icon.Reset` | `backup-restore` |
| `Icon.Search` | `magnify` |
| `Icon.Back` | `arrow-left` |
| `Icon.MoveUp` | `chevron-up` |
| `Icon.MoveDown` | `chevron-down` |
| `Icon.Settings` | `cog` |
| `Icon.ProjectSettings` | `application-cog` |
| `Icon.Apply` | `check` |
| `Icon.Warning` | `alert-outline` |
| `Icon.Error` | `alert-circle-outline` |
| `Icon.Info` | `information-outline` |
| `Icon.Build` | `hammer-wrench` |
| `Icon.Browse` | `dots-horizontal` |
| `Icon.DragHandle` | `drag-horizontal-variant` |
| `Icon.Lock` | `lock-outline` |
| `Icon.Dirty` | `circle-medium` |
| `Icon.Prefab` | `package-variant-closed` |

#### スクリプトデバッグ

| アイコンキー | MDI アイコン名 |
|---|---|
| `Icon.Debug.StepOver` | `debug-step-over` |
| `Icon.Debug.StepInto` | `debug-step-into` |
| `Icon.Debug.StepOut` | `debug-step-out` |

#### ギズモ / ビューポート

| アイコンキー | MDI アイコン名 |
|---|---|
| `Icon.Tool.Select` | `cursor-default` |
| `Icon.Tool.Move` | `arrow-all` |
| `Icon.Tool.Rotate` | `rotate-3d-variant` |
| `Icon.Tool.Scale` | `resize` |

#### ドッキングパネル

| アイコンキー | MDI アイコン名 |
|---|---|
| `Icon.Panel.Hierarchy` | `file-tree` |
| `Icon.Panel.OpenDocuments` | `tab` |
| `Icon.Panel.Viewport` | `monitor` |
| `Icon.Panel.ScriptEditor` | `file-code` |
| `Icon.Panel.Project` | `folder-open` |
| `Icon.Panel.Output` | `console` |
| `Icon.Panel.AnimationTimeline` | `animation` |
| `Icon.Panel.ErrorList` | `alert-circle-outline` |
| `Icon.Panel.Profiler` | `speedometer` |
| `Icon.Panel.Inspector` | `tune-variant` |
| `Icon.Panel.AiAssistant` | `robot-outline` |
| `Icon.Panel.Terrain` | `terrain` |

#### コンポーネント種別（ComponentKind 対応）

| アイコンキー | MDI アイコン名 |
|---|---|
| `Icon.Component.Transform` | `axis-arrow` |
| `Icon.Component.Model` | `cube-outline` |
| `Icon.Component.Skybox` | `panorama-sphere` |
| `Icon.Component.WaterVolume` | `water` |
| `Icon.Component.WaterLink` | `pipe` |
| `Icon.Component.InteractionSource` | `grass` |
| `Icon.Component.CoverEmitter` | `snowflake` |
| `Icon.Component.Canvas` | `rectangle-outline` |
| `Icon.Component.Sprite` | `image-outline` |
| `Icon.Component.Light` | `lightbulb-on-outline` |
| `Icon.Component.JointAttach` | `bone` |
| `Icon.Component.ParticleEmitter` | `shimmer` |
| `Icon.Component.ControlPoint` | `vector-polyline` |
| `Icon.Component.Camera` | `camera` |
| `Icon.Component.Collider` | `shape-outline` |
| `Icon.Component.Collider2d` | `vector-rectangle` |
| `Icon.Component.Audio` | `volume-high` |
| `Icon.Component.Animator` | `animation-play-outline` |
| `Icon.Component.InputMap` | `gamepad-variant-outline` |
| `Icon.Component.Script` | `script-text-outline` |
| `Icon.Component.Plugin` | `puzzle-outline` |
| `Icon.Component.TerrainChunk` | `terrain` |
| `Icon.Component.Unknown` | `hexagon-outline` |

#### ヒエラルキーのノード種別

| アイコンキー | MDI アイコン名 |
|---|---|
| `Icon.Node.Folder` | `folder-outline` |
| `Icon.Node.Group` | `folder-multiple-outline` |
| `Icon.Node.Actor3D` | `cube` |
| `Icon.Node.Actor2D` | `vector-square` |

#### ファイル形式（プロジェクトパネル）

| アイコンキー | MDI アイコン名 |
|---|---|
| `Icon.File.Generic` | `file-outline` |
| `Icon.File.Folder` | `folder` |
| `Icon.File.FolderEmpty` | `folder-outline` |
| `Icon.File.Scene` | `movie-open-outline` |
| `Icon.File.Actor` | `cube` |
| `Icon.File.Actor2D` | `vector-square` |
| `Icon.File.Model` | `cube-outline` |
| `Icon.File.Image` | `file-image` |
| `Icon.File.Script` | `language-csharp` |
| `Icon.File.Shader` | `brush-variant` |
| `Icon.File.ScriptGeneric` | `script-text-outline` |
| `Icon.File.InputMap` | `gamepad-variant-outline` |
| `Icon.File.Anim` | `animation-play-outline` |
| `Icon.File.Material` | `palette-outline` |
| `Icon.File.PostFx` | `auto-fix` |
| `Icon.File.Audio` | `file-music-outline` |
| `Icon.File.Json` | `code-json` |
| `Icon.File.Config` | `file-cog-outline` |
| `Icon.File.Text` | `file-document-outline` |
| `Icon.File.Terrain` | `terrain` |

#### パッケージング対象プラットフォーム

| アイコンキー | MDI アイコン名 |
|---|---|
| `Icon.Platform.Windows` | `microsoft-windows` |
| `Icon.Platform.MacOS` | `apple` |
| `Icon.Platform.Android` | `android` |
| `Icon.Platform.iOS` | `apple-ios` |
| `Icon.Platform.PlayStation` | `sony-playstation` |
| `Icon.Platform.Switch` | `nintendo-switch` |
