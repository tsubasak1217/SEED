---
paths:
  - "editor/src/Panels/**"
  - "editor/src/MainWindow*"
---

# エディタパネル領域のルール（AvalonDock）

- **`ContentId` は一度決めたら変更禁止**。保存レイアウト `editor/settings/layout.xml` に永続化されるキーであり、
  起動時の `LoadLayout()` が `ContentId` 文字列でパネルインスタンスを紐付け直す。後から変えると既存ユーザーの
  レイアウトでパネルが復元できず空表示になる。表示名を変えたいだけなら `Title` だけ書き換え、`ContentId` は不変にする。
- **新規パネルは `OnViewMenuOpened` へ `IsChecked` 行を 1 行追加する**（`MenuItemXxx.IsChecked = IsPanelVisible("xxx");`）。
  この反映はメニュー項目を個別参照するハードコード式なので、追加を忘れると「表示」メニューのチェックが実状態とズレる。
- 新しい `ContentId` は `LoadLayout()` の `LayoutSerializationCallback` の switch 式にも同じ文字列で追加する。
- 詳細な追加手順（パネル本体・MainWindow 登録・メニュー・レイアウト永続化）は **add-editor-panel Skill** を使う。

## ボタンの見た目（絶対ルール）

- **ボタンの色を自分で決めない**。`Background` / `Foreground` / `BorderBrush` を
  ボタンへ直接書かず、`ControlTemplate` も自前で書かない。普通のボタンは
  `Style` を指定しなければ共通の暗黙スタイルが当たる（`editor/src/Theme/SeedButtonStyles.xaml`）。
  見た目を変えたいときは名前付きスタイル（`Seed.Button.Primary` / `Danger` /
  `Link` / `Icon` / `Outlined` / `Dialog` / `Swatch` など）を選ぶ。正典は **`docs/editor_ui_style.md`**。
- **ホバー色を独自に決めない**。WPF 既定テンプレートのホバー（淡い水色 `#BEE6FD` 系）は
  暗背景の白文字を消すため、共通書式がテンプレートごと置き換えている。
  `Background` だけを指定したボタンは既定テンプレートのままになるので作らないこと。
- 色を足す・変えるときは `editor/src/Theme/SeedColorTable.cs` の定数と
  `ContrastCases` を更新し、`dotnet run --project editor/tests/ThemeContrastTests` を通す
  （背景×文字のコントラスト比 4.5 以上／無効時 3.0 以上を機械的に検査する）。
- **「×」（`Icon.Close`）ボタンは `editor/src/Controls/CloseIconButton.cs` で作る**。
  アイコン（`AppIcon`）へ直接マウスハンドラを付けない（当たり判定が絵の大きさしか無くなる）。
  当たり判定はアイコンの 1.5 倍・下限 18px（`SeedButtonMetrics.IconHitAreaSize`）で、
  見た目のアイコンサイズは変えない。行やタブの高さが伸びるときは `verticalBleed` で
  周囲の余白を食わせる。詳細は `docs/editor_ui_style.md` 3 章。
