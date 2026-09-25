# エディタ UI の書式ガイド（ボタン）

SEED エディタ（`editor/`、C# WPF、ダークテーマ）のボタンの見た目を決めるのは
**1 か所だけ** である。この文書がその正典。

- 色の定義 … `editor/src/Theme/SeedColorTable.cs`（16 進文字列の定数表。WPF 非依存）
- 寸法の定義 … `editor/src/Theme/SeedButtonMetrics.cs`
  （当たり判定の算出だけは WPF 非依存の `SeedButtonMetrics.HitArea.cs`）
- スタイルの定義 … `editor/src/Theme/SeedButtonStyles.xaml`（`App.xaml` から結合）
- コードから使うキー … `editor/src/Theme/SeedButtonStyle.cs`
- 状態別の色を差し替える仕組み … `editor/src/Theme/ButtonChrome.cs`（添付プロパティ）
- 「×」ボタンの共通ファクトリ … `editor/src/Controls/CloseIconButton.cs`（3 章）
- 検査 … `editor/tests/ThemeContrastTests`（配色と当たり判定）

---

## 1. 絶対ルール

1. **ボタンの色を画面側で決めない。**
   XAML でもコードでも、`Background` / `Foreground` / `BorderBrush` を
   ボタンに直接書かない。`ControlTemplate` を自前で書かない。
2. **ホバー色を独自に決めない。** ホバー・押下・無効の見た目は共通書式が持つ。
3. 見た目を変えたいときは **名前付きスタイルを選ぶ**。合うものが無ければ
   `SeedButtonStyles.xaml` に足し、`SeedColorTable.ContrastCases` へ検査項目も足す。
4. 例外は 1 つだけ。**背景色そのものが「値」であるボタン**（色見本・選択状態の
   プリセット）は `Seed.Button.Swatch` を使い、`Background` に値の色を入れる。

### なぜこのルールがあるか

WPF 既定の `Button` テンプレートは、ホバー時に背景を淡い水色（`#BEE6FD` 系）へ
**強制的に塗り替える**。`Background` だけを指定した暗色ボタンは、
ホバーの瞬間だけ「淡い水色の上に淡い灰色の文字」になり、文字が読めなくなる。
実測では、この状態のコントラスト比は 1.4 前後（基準は 4.5 以上）だった。

この不具合はエディタの各所で同時に発生していた。原因は
「ボタンの見た目を決める場所が 40 か所以上に散っていた」ことであり、
色を 1 つずつ直しても同じことがまた起きる。そのため定義場所を 1 つに畳んだ。

---

## 2. スタイルの一覧と使い分け

`Style` を**指定しなければ**「通常ボタン」の暗黙スタイルが自動で当たる。
普通のボタンには何も書かなくてよい。

| キー | 用途 | 使う場面の例 |
|---|---|---|
| （指定なし） | 通常ボタン | パネルのツールバー、インスペクタの行内ボタン |
| `Seed.Button.Base` | 通常ボタンを明示的に指定したいとき、`BasedOn` の土台 | 各ウィンドウの名前付きスタイルの基底 |
| `Seed.Button.Primary` | **主操作**。その画面で一番押してほしいもの | OK / 作成 / 送信 / 参加 |
| `Seed.Button.Success` | **完了・生成**。実行すると成果物ができる | パッケージのビルド |
| `Seed.Button.Danger` | **危険操作**。取り返しがつかない | 削除 / 破棄 |
| `Seed.Button.Link` | リンク風。文章中の副次操作 | 「詳しくはこちら」 |
| `Seed.Button.Icon` | アイコン専用。背景は透明でホバー時だけ薄く光る | ツールバーの小さなボタン |
| `Seed.Button.Outlined` | 枠線つき。面として見せたいもの | スタート画面の大きな選択肢、行内の小ボタン |
| `Seed.Button.Dialog` | ダイアログの決定・取消（最小幅を揃える） | 「閉じる」「キャンセル」 |
| `Seed.Button.DialogPrimary` | ダイアログの主操作（Primary ＋ ダイアログ寸法） | 「OK」「作成」 |
| `Seed.Button.Swatch` | 背景色そのものが値・選択状態 | カラーフィールド、ピボット/アンカーのプリセット |

`ToggleButton` と `RepeatButton` にも同じ暗黙スタイルが当たる。
`ToggleButton` の ON 状態はアクセント色（主操作と同じ青）になる。

### 使い方

XAML:

```xml
<!-- 通常ボタン: 何も書かない -->
<Button Content="クリア" Click="OnClear" Padding="10,2" FontSize="11"/>

<!-- 主操作 -->
<Button Content="OK" Style="{StaticResource Seed.Button.DialogPrimary}"/>

<!-- 既存の名前付きスタイルは BasedOn で共通書式へ寄せる（寸法だけ足す）-->
<Style x:Key="PkgBtn" TargetType="Button"
       BasedOn="{StaticResource Seed.Button.Outlined}">
    <Setter Property="Height"   Value="28"/>
    <Setter Property="FontSize" Value="11"/>
</Style>
```

コード:

```csharp
using SEEDEditor.Theme;

// 通常ボタン: Style を指定しない（暗黙スタイルが当たる）
var btn = new Button { Content = "追加", Padding = new Thickness(8, 2, 8, 2) };

// 主操作 / 危険操作
SeedButtonStyle.Apply(okButton, SeedButtonStyle.PRIMARY);
deleteButton.Style = SeedButtonStyle.Get(SeedButtonStyle.DANGER);
```

小さなモーダルを**コードで組む**ときは `Theme/SeedDialogTheme.cs` を使う
（背景・入力欄・ラベル・**一覧**・ボタンの作り方が揃っている）。
`SeedDialogTheme.NewButton(text, onClick, isPrimary: true)` がダイアログ用の
ボタンを共通スタイル付きで返す。

選択肢を選ばせる一覧は `SeedDialogTheme.NewListBox(items, height)` を使う。
**素の `ListBox` をそのまま置かない**。WPF 既定の `ListBoxItem` は、
フォーカスが外れた選択行を明るい灰色（`SystemColors.Control` 系）で塗るため、
暗いダイアログで明るい文字色を継いだまま塗られて**選択した行だけが読めなくなる**。
ボタンのホバー色を共通書式がテンプレートごと置き換えているのと同じ理由。

---

## 3. 「×」ボタンと小さなアイコンボタンの当たり判定

### 3.1 「×」は共通の窓口で作る

閉じる・解除・削除の「×」（`Icon.Close`）は、
**`editor/src/Controls/CloseIconButton.cs` を必ず通して作る**。
アイコン（`AppIcon`）へ直接マウスハンドラを付けない。

```csharp
using SEEDEditor.Controls;

var close = CloseIconButton.Create(
    TabCloseIconSize,                    // 見た目のアイコンの大きさ（変えない）
    tooltip:       "このタブを閉じる",
    onClick:       () => CloseTab(tab),
    margin:        TabCloseButtonMargin,
    verticalBleed: TabCloseButtonVerticalBleed);
```

| 引数 | 使いどころ |
|---|---|
| `iconSize` | アイコン（見た目）の一辺。**当たり判定の計算にだけ使い、絵の大きさは変えない** |
| `tooltip` / `tag` / `onClick` | 文言・対象データ・押されたときの処理。`Click` を後から足してもよい |
| `iconBrush` / `hoverIconBrush` | 既存箇所の強調色（赤系の削除など）を保つときだけ。**ボタン自体の色は書かない** |
| `margin` / `padding` | 周囲との間隔・内側余白（既定は余白 0 の正方形） |
| `verticalBleed` | 行やタブの高さを伸ばしたくないとき（3.3） |
| `styleKey` | 既定はアイコン専用。枠線つきの小ボタンが並ぶ行だけ `OUTLINED` |

実体は `Seed.Button.Icon` を当てた **`Button`**。ボタンにしているのは、

- ホバー・押下・無効の見た目、キーボード操作、UI Automation の Invoke が共通書式で揃う
- `ButtonBase` が `MouseLeftButtonDown` を自分で処理して `Handled` を立てるので、
  **ドラッグ元や開閉トグルを兼ねた行・見出しの中に置いても誤作動しない**
  （インスペクタのコンポーネント見出しがこれにあたる）

から。以前 `MouseDown`（押した瞬間）で閉じていた箇所も、`Click`（離した瞬間）へ揃えてよい。
どうしても `Button` にできない場所だけ、`Background="Transparent"` を入れた `Border` で
当たり判定を広げる（背景が無いと空白部分がヒットしない）。

### 3.2 当たり判定の大きさ

`editor/src/Theme/SeedButtonMetrics.HitArea.cs` が唯一の出所。

```
当たり判定 = max(ceil(アイコン一辺 × 1.5), 18px)   の正方形（アイコンは中央）
```

- 倍率 `ICON_HIT_AREA_SCALE = 1.5`、下限 `ICON_HIT_AREA_MIN_PX = 18`
- 端数は切り上げる（小数の寸法は枠やホバー背景がにじむため）
- 9〜12px のアイコンはすべて下限 18px に丸まる
- 検査は `editor/tests/ThemeContrastTests`（`dotnet run --project editor/tests/ThemeContrastTests`）

ボタン側には `Width`/`Height` ではなく **`MinWidth`/`MinHeight`** として入るので、
もっと大きい寸法を持つ既存ボタン（22×22 など）を縮めることはない。
行内の並び替えボタン（`Scripting/ScriptFieldWidgets.MakeIconButton`）も同じ規定を使う。

### 3.3 行やタブの高さを伸ばさない

当たり判定はたいてい行の文字（15〜16px）より大きいので、素直に置くと行が数 px 伸びる。
その場合は `verticalBleed` に「上下へはみ出させる量」を渡し、
**周囲の余白（行やタブの padding）を食わせて**高さを保つ。
負のマージンは描画と当たり判定はそのままに、レイアウト上の要求高さだけを縮める。

```csharp
// 例: 文字 16px の行に 18px の当たり判定を載せる（行の padding は上下 4px）
private static readonly double CloseButtonVerticalBleed = Math.Max(
    0, (SeedButtonMetrics.IconHitAreaSize(TabIconSize) - RowTextHeight) / 2);
```

値は規定から計算して持つこと（直書きしない）。実測では
「タブ」パネルの行 24px、プロジェクトパネルのタブ 20px、インスペクタの見出し 28px が
いずれも変更前と同じ高さのまま、当たり判定だけ 18×18 に広がっている。

---

## 4. 色表（`SeedColorTable`）

すべて実測値。比は WCAG 2.1 のコントラスト比で、
通常の文字は 4.5 以上、無効状態とフォーカス枠は 3.0 以上を満たす。
`dotnet run --project editor/tests/ThemeContrastTests` が機械的に確かめる。

### 通常ボタン（暗黙スタイル）

| 状態 | 背景 | 文字 | 比 |
|---|---|---|---|
| 通常 | `#3A3A3D` | `#DCDCDC` | 8.27 |
| ホバー | `#4A4A4F` | `#FFFFFF` | 8.81 |
| 押下 | `#2E2E31` | `#DCDCDC` | 9.87 |
| 無効 | `#303033` | `#8A8A8A` | 3.81 |
| フォーカス枠 | `#3A3A3D` | `#9CC2F0` | 6.15 |

### 主操作（`Seed.Button.Primary`）／トグル ON

| 状態 | 背景 | 文字 | 比 |
|---|---|---|---|
| 通常 | `#0E639C` | `#FFFFFF` | 6.40 |
| ホバー | `#1177BB` | `#FFFFFF` | 4.79 |
| 押下 | `#0A4E7A` | `#FFFFFF` | 8.81 |
| 無効 | `#1E3A4D` | `#9FB6C6` | 5.65 |

### 完了・生成（`Seed.Button.Success`）

| 状態 | 背景 | 文字 | 比 |
|---|---|---|---|
| 通常 | `#1A5A1A` | `#FFFFFF` | 8.33 |
| ホバー | `#226A22` | `#FFFFFF` | 6.66 |
| 押下 | `#144714` | `#FFFFFF` | 10.82 |

### 危険操作（`Seed.Button.Danger`）

| 状態 | 背景 | 文字 | 比 |
|---|---|---|---|
| 通常 | `#A1373B` | `#FFFFFF` | 6.72 |
| ホバー | `#C04448` | `#FFFFFF` | 5.05 |
| 押下 | `#802B2E` | `#FFFFFF` | 9.12 |

### リンク風（`Seed.Button.Link`）— 背景は透明

下地はエディタで最も明るいツールバー帯 `#2D2D2D` を最悪ケースとして検査する。

| 状態 | 文字 | 比 |
|---|---|---|
| 通常 | `#4FA3E3` | 5.04 |
| ホバー | `#7FC1F0` | 7.08 |
| 押下 | `#A5D6F5` | 8.88 |
| 無効 | `#8A8A8A` | 3.99 |

> 暗い下地では「押下＝暗くする」がそのままコントラスト不足になるため、
> リンクだけは 通常 → ホバー → 押下 の順に明るくしている。

### アイコン専用（`Seed.Button.Icon`）— 背景は透明＋白の薄がけ

| 状態 | 実効背景（`#2D2D2D` 上） | アイコン | 比 |
|---|---|---|---|
| 通常 | `#2D2D2D` | `#DCDCDC` | 10.04 |
| ホバー | `#494949`（`#22FFFFFF` を重ねた結果） | `#FFFFFF` | 9.00 |
| 押下 | `#5D5D5D`（`#3AFFFFFF` を重ねた結果） | `#FFFFFF` | 6.58 |
| 無効 | `#2D2D2D` | `#8A8A8A` | 3.99 |

### ダイアログの文字と入力欄（`SeedDialogTheme`）

| 用途 | 背景 | 文字 | 比 |
|---|---|---|---|
| 本文 | `#252526` | `#DCDCDC` | 11.17 |
| 補足 | `#252526` | `#999999` | 5.38 |
| 成功 | `#252526` | `#8ACB8A` | 8.01 |
| エラー | `#252526` | `#E88F8F` | 6.39 |
| 入力欄 | `#1A1A1A` | `#DCDCDC` | 12.69 |
| 一覧の選択行 | `#264F78` | `#DCDCDC` | 6.19 |
| 一覧のホバー行 | `#2A2A2B` | `#DCDCDC` | 10.46 |

### 通知帯（スクリプトエディタのディスク追従）

タブの上に出す非モーダルの帯。見落とすと編集内容を失うので、
エディタ本体（`#1E1E1E`）に埋もれない明度差を持たせる
（正典: [docs/editor_script_panel.md](editor_script_panel.md) 5.4）。
帯の中のボタンは**通常ボタンの暗黙スタイルのまま**で、色を足していない。

| 用途 | 背景 | 前景 | 比 |
|---|---|---|---|
| 本文 | `#3A3212` | `#DCDCDC` | 9.31 |
| 警告アイコン | `#3A3212` | `#D7BA36` | 6.67 |
| 帯の下線（文字ではないので 3:1 基準） | `#1E1E1E` | `#8A7828` | 3.81 |

---

## 5. 仕組み（変更するときに知っておくこと）

### 暗黙スタイルはテンプレート内部にも効く（実測済み）

`Application.Resources` の暗黙スタイルは、`ControlTemplate` の中に置かれた
**`Style` 未指定の要素にも適用される**。自前のスクロールバーやスライダーの
テンプレート内で素の `RepeatButton` / `ToggleButton` を使っている箇所は、
`Style="{x:Null}"` を付けて明示的に除外すること。
該当箇所は `App.xaml`（コンボ・細身スクロールバー）と
`MainWindow.xaml`（スクロールバー・スライダー）。

WPF 標準の `ScrollBar` / `ComboBox` / `Slider` / `Expander` / `TreeView` /
`ToolBar` / `TabControl` と、AvalonDock・AvalonEdit の内部要素は、
いずれもテーマ側が明示スタイルを持つため影響を受けない（プローブで確認済み）。

### テンプレートは 1 つだけ

`Seed.Template.Button`（`TargetType` は `ButtonBase`）が唯一のテンプレート。
状態ごとの色は `ButtonChrome` の添付プロパティから読むため、
バリエーションを増やしてもテンプレートは複製しない。
新しいバリエーションは「`Seed.Button.Base` を `BasedOn` して
`Background` / `Foreground` と `ButtonChrome.*` を差し替えるだけ」で作れる。

### テンプレート内の Setter で Binding が使えるのは名前付き要素だけ

`ControlTemplate.Triggers` の `Setter` に `Binding` を書けるのは
`TargetName` を指定したときだけ。テンプレート親（ボタン自身）を対象にした
`Setter` で `Binding` を使うと値が解決されず、`Foreground` は黒に落ちる。
そのため文字色のトリガーは `TargetName="Content"` の
`TextElement.Foreground` に対して設定している。

### 色を変えたら

1. `SeedColorTable.cs` の定数を書き換える
2. `dotnet run --project editor/tests/ThemeContrastTests` を通す
3. XAML 側は `{x:Static}` 経由で同じ値を読むので、追随の作業は要らない

---

## 6. 意図的に共通書式へ寄せていないもの

次はボタンだが「別の見た目であること」に意味があるため、独自スタイルのまま残す。
いずれもホバー色は暗色側なので、文字が消える問題は起きない。

| 場所 | 理由 |
|---|---|
| `MainWindow.xaml` のギズモ／プレイバー／ビューポート操作 | ビューポートに重ねる半透明のアイコンバー |
| `MainWindow.xaml` の上部バー（`TopBarTextBtnStyle`） | メニューに近い見た目のテキストボタン |
| `MainWindow.xaml` の地形操作（`TerrainActionBtnStyle`） | モード表示を兼ねる |
| `Panels/ProjectPanel.xaml` のツールバー／パンくず | フォルダビュー専用の見た目 |
| `Panels/SpriteRig/SpriteRigPanel.xaml` のツールバー | ツール選択トグル |
| `CreateItemWindow.xaml` の項目カード（`ItemRowStyle`） | ボタンだが「一覧の行」 |
| `Panels/AnimationTimeline/` のドープシート上の操作 | タイムライン専用の描画 |

---

## 7. Output パネルの行の色と出どころ

Output パネル（`Panels/OutputPanel.xaml.cs`）の行の色は **5 種類の「色の種類」** で決まり、色そのもの（ブラシ）は
OutputPanel の表 1 か所にある。種類と出どころ（表示フィルタの「エンジン / ゲーム」）は WPF 非依存の
`editor/src/Logging/OutputLineStyle.cs` が持つ（単体テスト `editor/tests/AndroidRunUiTests`）。

| 色の種類 | 文字色 | 使う行 |
|---|---|---|
| `Default` | `#CCCCCC`（灰） | 通常の行・工程の結果・logcat の通常の行 |
| `Runtime` | `#6CD5F5`（水色） | 実行先からの通知（`[Runtime→Editor]`・Android の実行の開始／停止／アプリの終了・端末とアプリ ID） |
| `Build` | `#CCCC55`（黄） | ビルドの進み具合（`[cargo]`・`BUILDING`・Android の工程の見出し・子プロセスの出力） |
| `Warning` | `#CCCC55`（黄。いまは `Build` と同じ色） | 警告（Android の `警告:`・logcat の重要度 W・logcat の終わり） |
| `Error` | `#F48484`（赤） | エラー・失敗（Android の工程の失敗・失敗の種類・logcat の重要度 E/F/A） |

- 行の見た目の決め方は 2 通り:
  - **`EditorLog.Write(本文)`（従来の書き方）** … Output パネルが本文の印から決める（`Logging/OutputLineClassifier.cs`。上から順に
    `[Runtime→Editor]` → 水色、`error`（大文字小文字を問わない）/ `失敗` / `EXCEPTION` → 赤、`[cargo]` / `BUILDING` / `BuildAsync` → 黄、それ以外は灰。
    本文に `[Script` を含めば出どころ＝ゲーム）。段階C-2 で OutputPanel から表へ切り出したもので、判定は変えていない。
  - **`EditorLog.Write(本文, 見た目)`** … 書き手が色と出どころを決める（Android の実行の行。`AndroidRun/AndroidRunOutputFormatter.cs`。
    logcat のタグ `DOTNET` はゲーム）。
- 警告の色を分けたくなったら、OutputPanel の「色の種類 → ブラシ」の表だけを直す（書き手は `Warning` を出している）。
- Android の実行の行の書式は [android.md](android.md) §20.4（実行中の差し替えの行は §23.7。`AndroidRun/AndroidHotReloadOutputFormatter.cs`）。

## 8. プレイバーの実行先セレクタ

ツールバーの実行ボタンの隣の実行先コンボ（`MainWindow.xaml` の `CmbRunTarget`。[android.md](android.md) §20.2）は、
アプリ共通のダーク ComboBox 暗黙スタイル（`App.xaml`）をそのまま使い、寸法と項目の並び（アイコン＋文言）だけを書いている。

- 項目の文言とアイコンは項目（`ComboBoxItem`）の `Foreground` を継ぐ。**色を項目の中で直接指定しない**
  （ホバー・選択時は白、無効の行は減光という共通の見た目が崩れ、ホバーで文字が読めなくなるため）。
- 選べない行（使えない状態の端末・案内の行）は `ComboBoxItem.IsEnabled = false` にし、理由を `ToolTip` に出す
  （`ToolTipService.ShowOnDisabled = true`。無効の実行・停止ボタンも同じく押せない理由をツールチップに出す）。
- 実行・停止ボタン（プレイバーの PNG アイコンのボタン）は 6 章の「意図的に共通書式へ寄せていないもの」のまま。有効/無効・絵柄・ツールチップ・
  状態表示の文言と色は `AndroidRun/PlayBarPolicy.cs` が決め、`MainWindow.AndroidRun.cs` の `ApplyPlayBar` が当てる。
- **Android の実行中の一時停止（段階D-1。[android.md](android.md) §21）は PC の Play と同じ実行ボタンで行う**（Android 専用のボタンは作らない）。
  状態表示・絵柄・色は PC の PLAY / PAUSE に揃える:

  | 状態 | 状態表示（色・アイコン） | 実行ボタン | 停止ボタン |
  |---|---|---|---|
  | PC の Play（参考） | `PLAY`（水色・`Icon.Play`） | 一時停止の絵柄（橙の地）で押せる | 押せる |
  | PC の Pause（参考） | `PAUSE`（橙・`Icon.Pause`） | 再生の絵柄（緑の地）で押せる | 押せる |
  | Android・端末のアプリとつながっている | `ANDROID PLAY`（水色・`Icon.Platform.Android`） | 一時停止の絵柄で押せる（一時停止） | 端末のアプリを止める |
  | Android・一時停止中 | `ANDROID PAUSE`（橙・`Icon.Pause`） | 再生の絵柄で押せる（再開） | 端末のアプリを止める |
  | Android・つないでいる途中／つながらない | `ANDROID RUN`（水色・`Icon.Platform.Android`） | 一時停止の絵柄で**無効**（理由をツールチップに） | 端末のアプリを止める |

  絵柄の PNG（`playbar/play.png`・`pause.png`）と地の色（`_brushPlay` / `_brushPause`）は PC と同じものを使う（新しい絵柄・色は足していない）。

**項目の並びと見た目**（行を作るのは `AndroidRun/RunTargetCatalog.cs`。どの行も同じ ItemTemplate＝アイコン＋文言で、色は項目から継ぐ）

| 行 | 文言 | アイコン | 選べるか |
|---|---|---|---|
| PC | `PC` | `Icon.Platform.Windows` | いつも |
| Android（自動）（段階C-3・Android の既定） | `Android（自動）` | `Icon.Platform.Android` | Android を使える環境ならいつも（端末の一覧が無くても） |
| 端末 | `Pixel_6a（実機）`・`emulator-5554（エミュレータ）` | `Icon.Platform.Android` | 使える状態なら |
| 見えなくなった端末 | `Pixel_6a（未接続）` | `Icon.Platform.Android` | 選べる（段階C-3 から。実行するとエミュレータで実行する旨をツールチップに） |
| 使えない端末・案内 | `（未許可）`・`端末を探しています…` 等 | 端末は `Icon.Platform.Android`、案内は `Icon.Info` / `Icon.Warning` | 選べない（理由をツールチップに） |

- 並びは「PC → Android（自動） → 端末 → 見えなくなった端末 → 案内」で固定。自動の行を端末より前に置き、Android の既定であることを並びでも示す。
- 実行の前の「未保存の変更」の確認（`保存して実行 / 保存せず実行 / キャンセル`）は、ボタンの文言を決められる 3 択の窓 `Dialogs/ActionChoiceWindow`
  （`SeedDialogTheme` の色と部品・`Seed.Button.Dialog` / `Seed.Button.DialogPrimary`。主操作＝保存して実行が左・Enter、Escape はキャンセル）で出す。
  標準の MessageBox（はい / いいえ）で「押すと何が起きるか」が分からない確認を増やさない。
