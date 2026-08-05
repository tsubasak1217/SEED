# 参照ピッカー（Reference Picker）— シーン内アクタ／コンポーネント参照の共通基盤

エディタには「シーン内のアクタ、またはそのコンポーネントを指す」参照フィールドが複数ある。
以前はフィールドごとに独自の D&D・独自のラベル更新・独自の解決規則を持っていたため、
挙動（何を落とせるか・複数候補の扱い・参照先が消えたときの見た目）がばらばらだった。

本ドキュメントは、それらを 1 つに統合した**参照ピッカー**の仕様と実装地図である。

---

## 1. 何が統合されたか

| 参照フィールド | 要求型 (Kind) | 保存形 | 適用 IPC |
|---|---|---|---|
| スクリプトの `[SerializeField]` 参照 | フィールドの型から自動決定 | `"アクタ名"` / `"アクタ名｜スロット名"` | `SET_SCRIPT_FIELD` |
| `WaterVolume.control_point_ref`（川の制御点） | `ControlPoint` | `"アクタ名"` | `SET_WATER_FIELD` |
| `WaterLink.volume_a` / `volume_b`（接続先） | `WaterVolume` | `"アクタ名"` | `SET_WATER_LINK_FIELD` |
| Canvas の基準カメラ参照（`CanvasViewportRef::Camera`） | `Camera` | アクタ名 + スロット名 | `SET_CANVAS_VIEWPORT_REF_CAMERA` / `..._WINDOW`（解除） |

**ランタイム（Rust）側は無変更**。値の適用は従来どおり各 IPC をそのまま使うので、
共通 Undo 機構（`field_edit.rs`）・リネーム追従（`rename_refs.rs`）にもそのまま乗る。

統合により、全フィールドで以下が共通になった。

- 現在値の表示（未設定はフィールドごとの文言、設定済みは `アクタ名` または `アクタ名 / スロット名`）
- **ドラッグ元**: Hierarchy のアクタ行 / シーンビューのアクタ / **インスペクタのコンポーネント見出し**
- **ドラッグ中の可否表示**: 適合ゼロなら枠が赤くなり、カーソルが禁止表示になってドロップを拒否
- ✕ ボタンで参照解除
- ドロップゾーンのダブルクリックで Hierarchy の参照先へジャンプ（スクロール＋一時ハイライト）
- **参照先アクタがシーンに無いとき ⚠ 付きの警告表示**（従来はどのフィールドにも無かった）

---

## 2. 解決規則

ドロップされたものを「そのコンポーネント自身」または「その所有アクタ」として解釈し、
参照フィールドの要求型に適合するコンポーネントを数える。

1. **コンポーネントを落とした場合**で、その型が要求型に一致 → そのコンポーネントを**即設定**
2. それ以外は**所有アクタ**として解釈し、アクタ内の適合コンポーネント数で分岐
   - **0 件** → ドロップ不可（ドラッグ中に拒否表示。判定材料が無くて落ちてしまった場合は警告ダイアログ）
   - **1 件** → 即設定（ウィンドウを出さない）
   - **複数** → 選択ウィンドウ（`ReferenceSelectorWindow`）で選ばせる
3. ルート直付け型（`GameObject` / `Transform` / `CanvasTransform`）はスロットを持たないため、
   「アクタがそれを持っているか」だけを検証して確定する

### スロット名を保存できないフィールドの扱い

`control_point_ref` と `volume_a/b` は**アクタ名しか保存できない**（Rust 側の型が `String`）。
このためスロットが複数あっても選択ウィンドウは出さず、存在検証だけを行う
（ランタイムは参照先アクタの先頭スロットへ解決する規約）。
仕様上の区別は `ReferenceFieldSpec.WantSlotName` で表現している。

### ドラッグ中に「0 件」を判定できる根拠

| ドラッグ元 | 判定材料 | 結果 |
|---|---|---|
| インスペクタのコンポーネント見出し | ドラッグデータに所有アクタの全スロット構成を同梱 | 常に確定判定 |
| Hierarchy / シーンビューのアクタ行 | `ActorComponentCache`（受信済み `ACTOR_COMPONENTS` のキャッシュ） | ヒットすれば確定、未取得なら「判定不能」 |

「判定不能」のときはドロップを許可し、従来どおり `GET_ACTOR_COMPONENTS` の往復で検証する
（＝キャッシュが古くても誤った参照が入ることはない。**確定は必ず実データで行う**）。
Hierarchy はドラッグ開始時に未取得アクタの構成を先読み要求するため、実際にはほぼ確定判定になる。
キャッシュは DFS ID をキーに持つので、`HIERARCHY` 受信（ツリー再構築）のたびに全消去する。

---

## 3. 実装地図

### 共通基盤（`editor/src/Controls/`）

| ファイル | 役割 |
|---|---|
| `ReferenceKindCatalog.cs` | **要求型の唯一の対応表**。Kind → `ACTOR_COMPONENTS` の `type` / 表示名 / ルート直付け判定 |
| `ActorComponentSnapshot.cs` | `ACTOR_COMPONENTS` JSON の正規化（`ActorComponentSnapshot`）と DFS ID キーのキャッシュ（`ActorComponentCache`） |
| `ReferenceDragData.cs` | ドラッグデータ形式名（`ReferenceDragFormats`）とコンポーネントドラッグのペイロード（`ComponentDragPayload`） |
| `ReferencePicker.cs` | 参照ピッカー行の見た目と操作。要求仕様は `ReferenceFieldSpec`、解決は `IReferenceDropResolver` へ委譲 |
| `ReferenceSelectorWindow.cs` | 複数候補の選択ウィンドウ。**ページを積める構造**（後述） |

### 呼び出し側

| ファイル | 内容 |
|---|---|
| `editor/src/Panels/InspectorPanel.ReferencePicker.cs` | `IReferenceDropResolver` の実装（ドラッグ中判定・ドロップ解決・非同期待ち合わせ）とコンポーネント見出しのドラッグ開始 |
| `editor/src/Panels/InspectorPanel.xaml.cs` | 4 つの参照フィールドの組み立て（`ReferencePicker.Create` を呼ぶだけ）／見出しのマウス操作／`ACTOR_COMPONENTS` 受信時のキャッシュ投入 |
| `editor/src/Scripting/ScriptInspectorBuilder.cs` | `[SerializeField]` 参照行。保存書式 `"アクタ名｜スロット名"` の分解・組み立てだけを担当 |
| `editor/src/Scripting/ScriptReferenceCatalog.cs` | `ReferenceKindCatalog` への薄い転送（スクリプト側の呼び名を保つため） |
| `editor/src/Panels/ActorRefJump.cs` | ジャンプ要求と**アクタ存在確認**の静的フック（MainWindow が Hierarchy へ結線） |

### 新しい参照型を足すとき

`ReferenceKindCatalog` の 2 つの辞書に 1 行ずつ足し、フィールド側で
`ReferencePicker.Create(new ReferenceFieldSpec { Kind = ..., WantSlotName = ... }, ...)` を書く。
それだけで D&D・可否表示・選択ウィンドウ・ジャンプ・警告表示がすべて付いてくる。

---

## 4. コンポーネント見出しがドラッグ元になったことによる操作変更

インスペクタのコンポーネント見出し（アコーディオンのヘッダー）は参照ピッカーへのドラッグ元になった。
そのため**開閉トグルは「押した瞬間」から「ドラッグせずに離したとき」へ移した**
（押下時にトグルすると、ドラッグを始めるたびにセクションが開閉してしまうため）。

単クリックの体感は変わらない。ダブルクリックでのリネーム、右クリックメニュー、
有効チェックボックス、削除 ✕ の挙動も従来どおり。

---

## 5. 次フェーズ（@ref バインディング）への拡張点

「コンポーネントを選ぶ → さらにその内部の変数を選ぶ」という 2 段階目が載る予定である。
`ReferenceSelectorWindow` はそのために**ページのスタック**として実装してある。

```csharp
// 1 ページ目だけの現状の使い方
var path = ReferenceSelectorWindow.Show(owner, firstPage);

// 2 段階目を足すとき: 選択パスを受けて次ページを返す関数を渡す
var path = ReferenceSelectorWindow.Show(owner, componentPage,
    selected => BuildVariablePage(selected[0]));   // null を返した時点で確定
```

- 戻り値は「各ページで選んだ項目を順に並べた選択パス」。1 段階目だけなら `path[0]`。
- ページが 2 枚目以降になると「← 戻る」ボタンが自動で現れる。
- 変数一覧の供給元（スクリプトのリフレクション結果など）を `ReferenceSelectorPage.Items` に
  渡すだけでよく、ウィンドウ側の変更は不要。
