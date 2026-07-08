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
