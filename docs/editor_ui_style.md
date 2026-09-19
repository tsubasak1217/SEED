# エディタ UI の書式ガイド（ボタン）

SEED エディタ（`editor/`、C# WPF、ダークテーマ）のボタンの見た目を決めるのは
**1 か所だけ** である。この文書がその正典。

- 色の定義 … `editor/src/Theme/SeedColorTable.cs`（16 進文字列の定数表。WPF 非依存）
- 寸法の定義 … `editor/src/Theme/SeedButtonMetrics.cs`
- スタイルの定義 … `editor/src/Theme/SeedButtonStyles.xaml`（`App.xaml` から結合）
- コードから使うキー … `editor/src/Theme/SeedButtonStyle.cs`
- 状態別の色を差し替える仕組み … `editor/src/Theme/ButtonChrome.cs`（添付プロパティ）
- 検査 … `editor/tests/ThemeContrastTests`

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

## 3. 色表（`SeedColorTable`）

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

## 4. 仕組み（変更するときに知っておくこと）

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

## 5. 意図的に共通書式へ寄せていないもの

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
