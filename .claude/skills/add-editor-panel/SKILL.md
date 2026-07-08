---
name: add-editor-panel
description: エディタに新しいパネル・ウィンドウ・ドッキング可能なUIを追加するときに使用。WPFエディタへAvalonDockのドッキングパネルを追加する定型手順を示す。
---

# エディタパネルの追加手順（AvalonDock）

SEEDエディタ（WPF, `editor/`）の各ドッキングパネル（Hierarchy, Inspector, Project, Output, エラー一覧など）は
[AvalonDock](https://github.com/Dirkster99/AvalonDock) の `LayoutAnchorable`（または `LayoutDocument`）としてホストされている。
新規パネルを追加する際は以下の手順を厳守すること。

## 1. パネル本体を作成する

`editor/src/Panels/` に `.xaml` + `.xaml.cs` のペアで配置する（`UserControl` を継承）。
最もシンプルな既存パネルは `OutputPanel.xaml` / `OutputPanel.xaml.cs`（ツールバー + 仮想化 ListBox のみで完結）なので、これを模倣テンプレートとする。

`OutputPanel.xaml` の骨格:

```xml
<UserControl x:Class="SEEDEditor.Panels.OutputPanel"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             Background="#1E1E1E">
    <Grid>
        <!-- ツールバー / 本体コンテンツ -->
    </Grid>
</UserControl>
```

`OutputPanel.xaml.cs` の骨格:

```csharp
namespace SEEDEditor.Panels;

/// <summary>
/// エンジン／ゲームのログを表示する Output パネル。
/// </summary>
public partial class OutputPanel : UserControl
{
    public OutputPanel()
    {
        InitializeComponent();
        // 初期化処理
    }
}
```

- クラスコメント・処理コメントは既存パネル同様に詳細に残すこと（プロジェクト規約）。
- ダークテーマ配色（`#1E1E1E` / `#2D2D2D` / `#CCCCCC` 等）は既存パネルに合わせる。
- 依存が薄い純粋コードでよい場合（XAMLデザイナ不要）は `ErrorListPanel.cs` のように `UserControl` を継承した通常の C# クラスのみで組んでもよいが、既定は `.xaml` ペア方式を使う。

## 2. MainWindow.xaml にドッキングパネルとして登録する

AvalonDockのレイアウトツリーは `editor/src/MainWindow.xaml` の `<avd:LayoutRoot>` 内に直接記述されている
（`xmlns:avd="https://github.com/Dirkster99/AvalonDock"`、パネル参照は `xmlns:panels="clr-namespace:SEEDEditor.Panels"`）。
既存の `LayoutAnchorablePane` の中に `<avd:LayoutAnchorable>` を1つ追加する。例（Outputパネルの実際の登録）:

```xml
<avd:LayoutAnchorable Title="Output"
                      ContentId="output"
                      CanClose="False"
                      CanHide="False"
                      CanFloat="True">
    <panels:OutputPanel x:Name="PanelOutput"/>
</avd:LayoutAnchorable>
```

新規パネルもこれに倣い、配置したい `LayoutAnchorablePane`（左/下/右のどこか既存のグループ）の子として追加する。
ドキュメントタブとして開きたい場合（Scriptエディタのように）は `LayoutDocument` を使う。

### 重要: ContentId は一度決めたら変更しない

`ContentId` は `editor/settings/layout.xml`（ユーザーのレイアウト保存ファイル）に永続化されるキーであり、
起動時の `LoadLayout()`（`MainWindow.xaml.cs`）が `ContentId` 文字列でパネルインスタンスを紐付け直す。

```csharp
serializer.LayoutSerializationCallback += (_, args) =>
{
    args.Content = args.Model.ContentId switch
    {
        "hierarchy"     => PanelHierarchy,
        "project"       => PanelProject,
        "inspector"     => PanelInspector,
        "viewport"      => ViewportGrid,
        "output"        => PanelOutput,
        "ai_assistant"  => _aiPanelUi,
        "script_editor" => PanelScriptEditor,
        "open_documents"=> _openDocsPanel,
        "error_list"    => _errorListPanel,
        _               => null,
    };
};
```

**新しい `ContentId` を追加する場合は、このswitch式にも同じ文字列でエントリを追加すること。**
既存パネルの `ContentId` を後から変更してはいけない（過去に保存された `layout.xml` を持つユーザーの環境で
そのパネルが復元できなくなり、`null` が割り当てられて空表示になる）。表示タイトル（`Title`）だけを変えたい場合は
`ContentId` は不変のまま `Title` だけ書き換える（`viewport` を "シーン" 表示に変えた実例が `MainWindow.xaml` に
コメント付きである）。

`x:Name` でフィールド化された `PanelXxx` は `MainWindow.xaml.cs` 側から直接参照できるようになる
（XAMLでインスタンス化した場合のみ。動的追加パネルは `_xxxPanel` のようにコードで生成・保持する）。

## 3. 「表示」メニューへ登録する

メニューはカテゴリ入れ子構造で、`MainWindow.xaml` の `<MenuItem Header="表示" SubmenuOpened="OnViewMenuOpened">` 配下、
さらに `<MenuItem Header="パネル">` や `<MenuItem Header="スクリプト">` のようなサブカテゴリの中に個々のトグル項目を置く。

```xml
<MenuItem Header="パネル">
    <MenuItem x:Name="MenuItemOutput"
              Header="Output"
              IsCheckable="True"
              Tag="output"
              Click="OnTogglePanel"/>
</MenuItem>
```

- `Tag` に `ContentId` と同じ文字列を入れる（`OnTogglePanel` がこれで対象パネルを検索する）。
- 新規カテゴリを作らず既存の「パネル」または「スクリプト」カテゴリに増やしてよい。独立した種類のUIなら新カテゴリを追加してもよい。
- `OnTogglePanel` / `OnViewMenuOpened` の実体は `editor/src/MainWindow.Scene.cs` にある。

## 4. トグル・チェック状態の反映（コード変更は基本不要）

`OnTogglePanel`（`MainWindow.Scene.cs`）は `sender.Tag` から `ContentId` を取り、`LayoutAnchorable.Show()/Hide()` を呼ぶ汎用実装なので、
手順2・3を正しく行えば追加のコードは不要。ただし「表示」メニューを開くたびにチェック状態を更新する
`OnViewMenuOpened` は各メニュー項目を**個別に**参照しているため、新規メニュー項目分の1行を追加する必要がある。

```csharp
private void OnViewMenuOpened(object sender, RoutedEventArgs e)
{
    MenuItemHierarchy.IsChecked = IsPanelVisible("hierarchy");
    MenuItemInspector.IsChecked = IsPanelVisible("inspector");
    MenuItemProject.IsChecked   = IsPanelVisible("project");
    MenuItemOutput.IsChecked    = IsPanelVisible("output");
    // ここに新規パネル分を追加:
    // MenuItemXxx.IsChecked = IsPanelVisible("xxx");
    ...
}
```

これを忘れると、パネルを閉じたあと「表示」メニューのチェックが実状態とズレる。

## 5. レイアウト永続化（起動時復元の仕組み）

- 保存: `MainWindow` の `Closing` イベントハンドラから `SaveLayout()` が呼ばれ、`XmlLayoutSerializer` で
  `editor/settings/layout.xml` にツリー全体（各要素の `ContentId` 込み）を書き出す。
- 復元: 起動時 `LoadLayout()` が同ファイルを読み、`LayoutSerializationCallback` で `ContentId` → パネルインスタンスの
  対応表（手順2のswitch式）を引いてコンテンツを再アタッチする。
- 旧 `layout.xml`（新パネル追加前に保存されたもの）にはそのパネルが存在しないため、`EnsureScriptEditorDocument()` /
  `EnsureScriptSidePanels()` のような「デシリアライズ後に無ければ追加する」補完処理が必要になる場合がある
  （`script_editor` / `open_documents` / `error_list` の実装を参照）。追加パネルが `CanClose="False"` の
  常設パネルであれば、同様の `EnsureXxx()` を用意して `LoadLayout()` 内から呼ぶことを検討する。

## 6. 検証手順

1. `dotnet build editor/SEEDEditor.sln` でビルドが通ることを確認する。
2. エディタを起動し、「表示」メニューから新規パネルが表示されることを確認する。
3. パネルを表示した状態でエディタを終了し、再起動してレイアウトが復元される（同じ位置・表示状態）ことを確認する。
4. 一度パネルを非表示にしてから再起動し、非表示状態も復元されることを確認する。

## よくある失敗

- **`ContentId` を途中で変更してしまう**: 既存ユーザーの `editor/settings/layout.xml` には旧 `ContentId` が
  保存されているため、`LoadLayout()` のswitch式が一致せず `null` が返り、パネルが空表示・消失する。
  タイトルや見た目を変えたいだけなら `Title` だけ変更し `ContentId` は絶対に変えない。
- **`LoadLayout()` のswitch式にエントリを追加し忘れる**: `layout.xml` 経由で再構築されたときにコンテンツが
  `null` になり、枠だけの空パネルになる。
- **「表示」メニューへの登録漏れ、または `Tag` と `ContentId` の不一致**: `OnTogglePanel` が対象パネルを
  見つけられず、メニューをクリックしても何も起こらない。
- **`OnViewMenuOpened` へのチェック状態反映を追加し忘れる**: パネルの表示/非表示自体は機能するが、
  メニューのチェックマークが実状態と食い違ったままになる。
- **`CanClose="False"` にすべきパネルを閉じられる設定のままにする**: ユーザーが閉じた後、
  「表示」メニューからしか再表示できない設計になっているか確認する（`OnTogglePanel` は `ContentId` で
  レイアウトツリーを検索するため、`LayoutAnchorable` 自体がツリーから消えていなければ再表示できるが、
  `CanClose="True"` で完全に破棄される設計にすると `IsPanelVisible` の検索対象自体が消え再表示できなくなる
  可能性があるため、既定は他パネルに倣い `CanClose="False"` にしておく）。
