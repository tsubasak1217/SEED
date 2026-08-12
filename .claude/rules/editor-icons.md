---
paths:
  - "editor/src/**"
  - "editor/gen_icons.py"
  - "docs/editor_icons.md"
---

# エディタアイコン領域のルール

正典は **`docs/editor_icons.md`**（採用セット・キー一覧・用途対応表・追加手順）。
ここには「守らないと壊れること」だけを書く。

## 絶対ルール

- **アイコン資産の置き場は `editor/resources/icons/` に統一する**。
  PNG（`common/` `folderview/` `playbar/` `toolbar/` `viewport/`）も
  ベクター定義（`Icons.xaml`）も同じ場所に置く。他の場所へ増やさない。
- **既存の PNG アイコンをベクターへ勝手に置き換えない**。
  ユーザーが用意・設定した資産であり、意図した見た目。対象は
  ①ギズモツールバー（select / translate / rotate / scale）
  ②プレイバー（play / pause / stop）③検索ボックス
  ④ファイル形式アイコン（`FileTypeIcons.PngByExtension` にある拡張子・フォルダ）
  ⑤新規作成ウィンドウの項目アイコン。
- **新規に足すアイコンはベクター（MDI）にする。PNG を新しく増やさない**。
  同時に、アイコン代わりの絵文字・記号文字・アイコンフォントも使わない。
  `▶ ■ ⏸ ⚙ ✕ ✓ ⚠ 📦 ⬡ ◆ ▲ ▼ ＋` などを新しく UI へ書かず、
  `Controls/AppIcon`（または `Controls/IconImages`）経由のベクターアイコンにする。
  フォント依存で環境ごとに字形が変わる／色が付けられない／高 DPI で滲む、が理由。
- **`editor/resources/icons/Icons.xaml` を手で編集しない**。自動生成ファイル。
  追加は `editor/gen_icons.py` の `CATALOG` に 1 行足して `python gen_icons.py` を実行する。
- **色をハードコードしない**。`AppIcon` は親の `Foreground` を継承する。
  明示したいときだけ `Foreground="..."`（XAML）か `SetBrush()`（コード）を使う。
  `ImageSource` が必要な箇所（AvalonDock の `IconSource` など）だけ `IconImages` を使い、
  色は Icons.xaml の `Icon.DefaultBrush` 1 箇所を参照する。
- **アイコンサイズをマジックナンバーで直書きしない**。`private const double XxxIconSize` に置く。

## 追加時に必須の対応表更新

新しい **パネル / ツールバーボタン / コンポーネント種別 / ファイル形式** を追加したら、
`gen_icons.py` の `CATALOG` へのアイコン登録に加えて、対応する表を必ず更新する。
表は 1 箇所へ集約してあるので、各所に switch を複製しないこと。

| 追加するもの | 更新する対応表 |
|---|---|
| ECS コンポーネント種別 | `editor/src/Controls/ComponentIcons.cs` の `IconKeyByTypeId` |
| ファイル形式（拡張子） | `editor/src/Controls/FileTypeIcons.cs`。既存 PNG があるなら `PngByExtension`、無ければ `IconKeyByExtension` |
| ドッキングパネル | `editor/src/Controls/PanelIcons.cs` の `IconKeyByContentId` |
| ツールバー / 個別ボタン | 対応表なし。使用箇所で `IconKey` を直接指定する |

登録漏れは**ビルドエラーにならない**（フォールバックアイコンや無表示になるだけ）。
そのため下の検証を必ず通すこと。

## 命名規約

`Icon.<分類>.<用途>`（分類なしの汎用操作は `Icon.<用途>`）。
既存の分類: `Component` / `File` / `Node` / `Panel` / `Platform` / `Tool` / `Debug`。

## フォールバック（削ってはいけない挙動）

- `FileTypeIcons.GetImage()` は未知の拡張子で必ず汎用 PNG（`folderview/image.png`）を返す。
- サムネイルを持てる形式でも、**生成前・生成中・生成失敗の間は形式アイコンを表示したまま**にする。
  `ProjectPanel.BuildFileItem()` の「まず形式アイコンを描き、デコード成功時だけ差し替える」順序を崩さない。
- `ComponentIcons.GetIconKey()` は未知の TypeId で `Icon.Component.Unknown` を返す。

## 検証（アイコンを触ったら必ず 3 つとも）

```bash
dotnet build editor/SEEDEditor.csproj      # 0 エラー
cd editor && python check_icons.py         # 未定義参照 0 件
pwsh -File editor/verify_icons.ps1         # 全 Geometry が WPF で解決できる
```

`check_icons.py` は Icons.xaml の `x:Key` とソース中の `"Icon.*"` 文字列を突き合わせる。
**キーの綴り間違いは C#/XAML のビルドでは検出できない**（`TryFindResource` が null を返し、
アイコンが描かれないだけで例外にもならない）ため、このチェックが唯一の防御線。

## ライセンス

Material Design Icons（Apache License 2.0）。帰属表示は `docs/editor_icons.md` と
`Icons.xaml` のヘッダーコメントで行っている。別のアイコンセットを混ぜない。
