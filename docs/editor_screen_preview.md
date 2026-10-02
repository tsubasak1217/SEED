# Edit 上の画面プレビュー（保存されないプレビュー。2026-10-02）

画面がすべて「実行時にスクリプトが積むプレハブ」でできているアプリ（Wake or Pay。`D:\SEED_projects\WakeOrPay`）では、
Edit 上で画面の見た目がつかめない。そこで **Edit 中に、任意のプレハブを「保存されないプレビュー」として好きなノードの下へ差し込んで見られる**ようにした。
編集画面・鳴動画面・ダイアログのように操作しないと出ない画面を、その場で見ながら直すためのもの。

- 段階 A（最初の画面をシーンにプレハブのインスタンスとして置き、`ScreenStack.RootAdoptChild` で実行時に引き取る）は
  [ui_navigation.md](ui_navigation.md) §2.7。この文書は段階 B（プレビュー）の正典。
- プレハブの編集の流儀（プレハブはアクタータブで直して保存し、シーンのインスタンスへは明示の操作で反映する）は
  [editor_prefab.md](editor_prefab.md)。プレビューもこの流儀に合わせた（§3）。

正典コード:

| 場所 | 役割 |
|---|---|
| `runtime/src/engine/core/app_base/app/editor_preview/` | ランタイムの本体（出す・消す・作り直す・Play の開始で外す・木の編集の拒否・Undo の包み・応答の書式）。**設計の要点はこのフォルダの `mod.rs` 冒頭** |
| `runtime/src/engine/structs/objects/actor/editor_preview.rs` | 印 `EditorPreviewInfo`（`Actor` / `ActorData` の `editor_preview`）と、保存系の濾過（`strip_editor_previews`） |
| `runtime/src/engine/structs/objects/actor/editor_preview_bias.rs` | プレビューの根のレイヤーの底上げ（保存されない `CanvasLayoutItem.layer_bias`）を当てる。組み立て直後と `build_actor`（Undo・Play 停止の組み直し）で呼ぶ |
| `runtime/src/engine/core/app_base/ipc.rs` | `PREVIEW_*` のワイヤ形式（`IpcCommand::PreviewPrefab` ほか） |
| `editor/src/Preview/` | エディタの WPF 非依存の部分: `ScreenPreviewIpc`（命令の組み立て・応答の解釈）・`PreviewHostCatalog`（差し込み先の表。`PreviewHostCatalogFormat`・`PreviewHost`）・`PreviewMenuModel`（右クリックの項目）・`PrefabPreviewCatalog`（プロジェクトのプレハブの一覧と検索）・`PreviewRecentList` / `PreviewRecentStore`（最近使ったもの）・`HierarchyPreviewFlags` / `InspectorPreviewInfo`（印の読み取り）・`PreviewDeletionPlanner`（Delete の振り分け）・`PreviewInsertTarget` / `ScreenPreviewRequest`。WPF の窓は `PrefabPreviewPickerWindow.xaml(.cs)`・`PrefabPreviewPickerRow` |
| `editor/src/Panels/HierarchyPanel.Preview.cs` | ヒエラルキーの右クリック・行の表示・名前の変更とドラッグの拒否 |
| `editor/src/Panels/InspectorPanel.Preview.cs` | インスペクタのプレビューの帯と差し込み先の案内（読み取り専用の当て方は `InspectorPanel.ReadOnly.cs::ApplyEditability` に一本化） |
| `editor/src/Runtime/RuntimeManager.ScreenPreview.cs` | `HIERARCHY_QUIET`・`PREVIEW_*` の受け口（受信スレッドで同期的にイベントを上げる） |
| `editor/src/MainWindow.ScreenPreview.cs` | 配線（窓を開く・親の引き直し・送る・応答のトースト・未保存にしない印・プレハブ保存後の作り直し・Delete の振り分け） |
| `editor/config/screen_preview_hosts.json` | 差し込み先の案内の表（データ。無い・読めないときは同じ内容の組み込みの表） |
| `editor/tests/ScreenPreviewTests/` | エディタ側の単体テスト（56 件。表の既定がスクリプトのソースとずれていないことも見る） |

---

## 1. 何ができるか

| 操作 | どこから | 結果 |
|---|---|---|
| プレハブをプレビュー | ヒエラルキーのノードの右クリック →「プレハブをプレビュー」→ 最近使ったもの／「プロジェクトから選ぶ...」 | そのノードの子（末尾）にプレハブの中身が出る |
| 画面としてプレビュー | `ScreenStack` を持つノードのインスペクタ →「プレビュー」の欄 | `Screens` の下に**枠（`FramePrefab`）→ 中身**の 2 段で出る（実行時と同じ姿。§4） |
| 面としてプレビュー | `ModalHost` のインスペクタ（ダイアログ・シート・覆い） | `Dialogs`・`Sheets`・`Overlays` の帯の下に面のプレハブが出る |
| ポップアップの中身をプレビュー | `PopupPlane`（Wake or Pay の中央のポップアップ）のインスペクタ | `Card/Content` の下に中身が出る |
| プレビューを消す | プレビューのノードの右クリック／インスペクタの帯の［プレビューを消す］ | そのプレビュー（入れ子ごと）が消える |
| すべてのプレビューを消す | ヒエラルキーの右クリック（ノード・空白） | 表示中のタブのプレビューが全部消える |
| 元のプレハブを開く | プレビューのノードの右クリック／インスペクタの帯の［プレハブを開く］ | そのプレハブをアクタータブで開く |
| Delete キー・シーンビューの「削除」 | プレビューの根を選んで | そのプレビューを消す（`PREVIEW_CLEAR`。中のノードだけを選んだときは消さずにトースト） |

- **プレハブを選ぶ窓**（「プロジェクトから選ぶ...」・案内の［選ぶ...］）: プロジェクトの `.actor` / `.actor2d` を「最近使ったもの」→「プロジェクトのプレハブ」の順に並べ、
  検索欄（ファイル名・フォルダの部分一致。空白区切りは全部を含む）で絞る。ダブルクリック・Enter で決定、Esc で閉じる。
  最近使ったもの（8 件まで）はプロジェクトごとに `editor/settings/screen_preview_recent.json` に残る。
  エディタを複数開いているときは、保存の直前に読み直して足すので先に保存された追加は残るが、ほぼ同時に足すと後の方の内容になり 1 件落ちることがある
  （錠は掛けない。ファイルが壊れることはない＝一時ファイルの名前は書き込みごとに固有。2026-10-03。`PreviewRecentStore`）。
- **入れ子にできる**: プレビューの中のノードへ、さらにプレビューを差し込める（例: `alarm_edit.actor` の `Column/Body` へ `alarm_edit_body.actor`。
  実行時にスクリプトが中身を入れる画面を、Edit で組み立てて見られる）。入れ子はヒエラルキーの右クリックから作るほか、プレビューの中のアクタが
  案内の表のスクリプト（例: プレビューした `center_popup.actor` の `PopupPlane`）を持つときは、インスペクタの帯の中に案内が出る
  （プレビューの中はスクリプトの欄ごと読み取り専用になるので、入れ子を足すだけの案内は押せるまま残る帯の中へ出す）。
- 入れ子のプレハブ参照は**実行時と同じに展開される**（スクリプトの `Instantiate` と同じ経路 `Scene::load_actor_into` で組み立てる。`.actor` に保存された中身がそのまま出る）。
  スクリプトが実行時に作る中身（一覧の行・画面の中身など）は、Edit ではスクリプトが動かないので出ない（必要なら入れ子のプレビューで足す）。

## 2. 保存されない・Play に持ち込まない

プレビューの根のアクタは印 `editor_preview`（`EditorPreviewInfo` = 中身のプレハブ・枠のプレハブ・枠の中の差し込み先。作り直しの材料）を持つ。
印は根だけが持ち、部分木の全体がプレビュー扱いになる。

| 経路 | 扱い | 実装 |
|---|---|---|
| シーンの保存（`SAVE_SCENE`・`SAVE_SCENE_AS`・Play 用一時シーンの `SAVE_SCENE_COPY`） | **含めない** | `Scene::to_json`（保存用の形）で取り除く |
| アクタファイルへの書き出し（`SAVE_ACTOR`・`EXPORT_ACTOR`） | 子孫のプレビューは取り除く。根がプレビューなら**断る** | `actor_file::save` |
| コピー（`COPY`） | プレビューの中のアクタはコピーしない・子孫のプレビューは取り除く。**選択がプレビューだけならクリップボードを空にして `PREVIEW_ERROR`**（前にコピーした別のものを次の Ctrl+V で黙って貼らせない。2026-10-03） | `clipboard.rs::do_copy` |
| 写しの書き出し（`SNAPSHOT_SCENE`） | 含めない（.scene のファイルなので） | `scene_snapshot/collect.rs` |
| Undo の写し・Play の開始時の写し・AI の `SCENE_INFO` | **残す**（メモリの中だけ。下の理由） | `to_data` のまま |
| Play の開始（`ENTER_PLAY`） | 写しを取った**後で**外す。Play を止めると Play 前の編集状態の復元で**戻る** | `remove_previews_for_play` |

**Play の扱い**: Play の中にはプレビューが無い（`ScreenStack` の置いてある根の引き取り・`ModalPlane` の「開く約束が無い」自己消去などに混ざらない）。
Play を止めると、Play を始めた瞬間の編集状態（プレビュー入り）へ戻るので、続けて見ながら直せる。

## 3. 編集のしかた（プレハブの編集の流儀に合わせた決定）

**プレビューのインスタンスは読み取り専用。直すのは元のプレハブ。**

- インスペクタはプレビューのアクタを選ぶと値を見せるが変えられない（帯「プレビュー（保存されません）」＋［プレハブを開く］［プレビューを消す］）。
- 直すときは［プレハブを開く］でプレハブをアクタータブで開き、直して保存（Ctrl+S）する。**保存すると、そのプレハブ（中身か枠）から作ったプレビューが自動で作り直される**
  （`PREVIEW_REFRESH_PATH`。「プレハブ保存時にシーンのインスタンスへ自動反映」の設定に関わらず行う。プレビューは見た目の確かめだけで、シーンの内容を変えないため）。
  入れ子のプレビューは、外側を作り直しても同じ名前の場所へ作り直される（場所の名前が無くなったものは消える）。
- 理由: 既存のプレハブの流儀（[editor_prefab.md](editor_prefab.md) §2）では、プレハブの中身はアクタータブで直し、シーンへは明示の操作で反映する。
  プレビューは保存されないので、プレビューの上で値を変えても**黙って消える**（保存・作り直し・Play で消える）。黙って消える編集を許さないため、編集はプレハブへ誘導する。
- ヒエラルキーでも、プレビューのノードは名前の変更・ドラッグ（並べ替え・アクタファイル化）・アクタの追加ができない（右クリックはプレビューの項目だけ）。
  行頭の目アイコン（表示の切り替え）も押せない（ツールチップで理由を出す。2026-10-03）。
  実アクタをプレビューの中へドロップすることもできない。シーンビューの右クリック「アクタファイル化」も、プレビューの中のノードでは断る（トースト。2026-10-03）。
- Delete キー・シーンビューの「削除」は選択を分ける（`PreviewDeletionPlanner`）: プレビューの根は `PREVIEW_CLEAR`（DFS の大きい順。外側の根と一緒に選んだ入れ子の根は送らない）、
  中のノードは消さない、普通のノードは「先に消えるプレビューの分だけ詰めた DFS」で従来どおり `DELETE`（ランタイムは命令ごとにその時点の木で DFS を引くため）。
- ランタイムも、**実アクタをプレビューの中へ移す・プレビューの中へアクタを足す・プレビューを外へ動かす**命令を断る（`PREVIEW_ERROR`。
  プレビューの中に入れたものは保存されず、データが消えるため）。
- ランタイムは **プレビューの中の値・構成を変える命令も断る**（`PREVIEW_ERROR:プレビューの中は編集できません…`。2026-10-03。レビュー #3）:
  インスペクタの値の編集（`field_edit.rs` の分類で対象のアクタがある `SET_*` 全部。`SET_VISIBLE`・`SET_ACTIVE` を含む）・`SET_ACTOR_TRANSFORM`・`SET_CANVAS_TRANSFORM`・
  コンポーネントの追加・削除・複製・制御点の書き換え・AI の `AI_SET_VALUE` / `AI_ADD_COMPONENT` / `AI_MOVE_ACTOR` / `AI_REMOVE_ACTOR`・
  ロジック配置 `LOGIC_PLACE` / `LOGIC_PLACE_BEGIN`（アクタの配置は `parent_dfs` が中なら「追加できません」、制御点の追記は `actor_dfs_id` が中なら「編集できません」）。
  エディタの UI はプレビューの行を読み取り専用にしていて送らないが、**AI ツール・MCP（`seed_send_ipc` など）・スクリプトは UI を通らない**ため、
  以前は普通の編集として成功し、保存・作り直し・Play のどれかで黙って消えていた。MCP の `seed_find_actor` は結果に `is_preview` を載せるので、AI は DFS ID を得た時点で分かる。
  エディタの AI ツール（`set_value`・`add_component`・`move_actor`・`remove_actor`）は送る前にヒエラルキーでプレビューの中かを確かめ、中ならエラーをツールの結果として返す
  （送るだけだと応答を待たずに「設定しました」と返し、AI には成功に見えるため。`EditorCommandExecutor.PreviewGuard.cs`）。
  名前の変更・削除はメモリの中だけの変更なので断らない（エディタの UI はプレビューの行で出さない）。

## 4. ScreenStack への差し込み（枠 → 中身の 2 段）

`Screens` の下へ直接置いたプレビューは枠（`screen_frame.actor`）無しで置かれる。実行時の見た目に近づけるため、
**ScreenStack の案内から差し込むときは、枠のプレハブ（`FramePrefab`）を先に作ってその `Body` の下へ中身を入れる**（Edit では ScreenStack のスクリプトが動かないので、
ランタイムの `PREVIEW_PREFAB` が枠 → 中身の 2 段で組み立てる）。

| 案内の項目 | 中身 | 枠 | 中身を入れる場所 |
|---|---|---|---|
| 根の画面 | `RootPrefab` | `FramePrefab` | `RootSafeArea` = true なら枠の `Body`（安全領域の中）、false なら枠の直下（実行時の根の画面と同じ） |
| 画面 | 選んだプレハブ | `FramePrefab` | 枠の `Body`（`ScreenOptions.SafeArea` の既定 = true と同じ） |

- 枠の背景の色は、枠のプレハブに保存された色のまま（実行時は `ScreenStack.PaintFrame` がテーマの `color.background` で塗り直す）。
- 出入りの動き・不透明な画面より下を隠す、は実行時だけ。

### 4.1 レイヤーの底上げ（`layer_bias`）

SEED の 2D は「ゾーン → レイヤー → 種別（スプライト → 図形 → パーティクル → テキスト）」の順に描くので、同じレイヤーのまま重ねると
**下の画面の文字が上の画面の板より手前に出る**（[ui_navigation.md](ui_navigation.md) §6）。実行時は ScreenStack が段 i の枠に i × `LayerStep`、
ModalHost が帯に 100 万・200 万・300 万を `CanvasLayoutItem.layer_bias`（保存されない欄）で付けるが、Edit ではスクリプトが動かないので誰も付けない。
そこで**プレビューの根に底上げを付ける**（値は `PREVIEW_PREFAB` の `layer_bias`。エディタが差し込み先の表から決める）。

| 差し込み | 底上げ |
|---|---|
| ScreenStack（根の画面・画面） | 1 段 = `LayerStep`（0 ならテーマの既定 10,000。タブの中のスタックは 1,000 など欄の値） |
| ModalHost の帯 | 覆い 1,000,000・シート 2,000,000・ダイアログ 3,000,000（`UiLayers` の既定） |
| PopupPlane の中身 | 0（ポップアップ自身の底上げを受け継ぐ） |
| ヒエラルキーの右クリック | 10,000（表の `default_layer_bias`。置いた所の表示より手前に描く） |

- 底上げは祖先から足し合わさる（入れ子のプレビューは外側の分も乗る）。
- `layer_bias` は保存されない欄なので、Undo/Redo・Play 停止でアクタを組み直すたびに印（`EditorPreviewInfo.layer_bias`）から当て直す（`build_actor`）。
- 根に `CanvasLayoutItem` が無いプレハブは底上げを付けられない（ログ `[Preview]` で知らせる。重なって見えることがある）。
  2026-10-02 の Wake or Pay の画面・面・枠のプレハブはすべて根に持っている。
- テーマが帯・段の値を変えていても、エディタは `UiLayers` の既定を使う（Edit では他に底上げが無いので見え方は変わらない）。

## 5. 差し込み先の案内の表（`editor/config/screen_preview_hosts.json`）

インスペクタのスクリプトの欄の下に出す「プレビュー」の案内は、データの表で決める（コードに書かない）。表が無い・読めないときは組み込みの同じ表で動く。

```jsonc
{
  "format_version": 1,
  "default_layer_bias": 10000,          // ヒエラルキーの右クリックから出すプレビューの底上げ（§4.1）
  "hosts": [
    {
      "script": "ScreenStack",            // スクリプトのクラス名（名前空間・パス・拡張子は見ない）
      "label": "画面のスタック",
      "slots": [
        {
          "label": "根の画面",
          "under_field": "ScreensChild", "under_default": "Screens",   // 差し込み先の子（欄の値 → 無ければ既定）
          "prefab_field": "RootPrefab",                                 // 中身（欄の値 → prefab_default）
          "frame_field": "FramePrefab", "frame_default": "assets://ui/prefabs/screen_frame.actor",
          "frame_body": "Body",
          "safe_area_field": "RootSafeArea", "safe_area_default": true, // false なら枠の直下
          "layer_bias_field": "LayerStep", "layer_bias_default": 10000  // 欄の値が正ならそれ（§4.1）
        },
        { "label": "画面", "under_field": "ScreensChild", "under_default": "Screens",
          "frame_field": "FramePrefab", "frame_default": "assets://ui/prefabs/screen_frame.actor",
          "frame_body": "Body", "layer_bias_field": "LayerStep", "layer_bias_default": 10000,
          "pick": true }                                                // pick: 「選ぶ...」でプレハブを選ぶ
      ]
    },
    { "script": "ModalHost", "label": "重ねる面の受け皿",
      "slots": [
        { "label": "ダイアログ", "under_field": "DialogsChild", "under_default": "Dialogs",
          "prefab_field": "DialogPrefab", "prefab_default": "assets://ui/prefabs/dialog.actor",
          "layer_bias": 3000000, "pick": true }                         // layer_bias: 固定の底上げ
        // シート（Sheets・bottom_sheet.actor・2000000）・覆い（Overlays・top_sheet.actor・1000000）も同じ形
      ]
    }
  ]
}
```

- 欄の値はシーンに保存された `[SerializeField]` の値（インスペクタの `script_fields`）を使い、保存されていない欄は `*_default`（スクリプトの既定と同じ値）を使う。
  既定がスクリプトとずれていないことは `ScreenPreviewTests` がスクリプトのソース（`ScreenStack.cs`・`ModalHost.cs`・`Model/UiLayers.cs`）と突き合わせて確かめる。
- `script` はスクリプトのクラス名で当てる（`SEED.UI.ScreenStack` のような完全名も、`assets://scripts/.../PopupPlane.cs` のようなファイルのパスも、
  最後の名前で比べる）。プロジェクトのスクリプト（`PopupPlane`）もこの表に載せる（プロジェクトごとの表は無い。backlog）。
- 2026-10-02 の登録: `ScreenStack`（根の画面・画面）、`ModalHost`（ダイアログ・シート・覆い。各帯の既定の面と「選ぶ...」）、
  `PopupPlane`（Wake or Pay の中央のポップアップ。`Card/Content` へ「選ぶ...」）。

## 6. ヒエラルキーの表示と未保存の印

- プレビューの部分木の行は薄く表示し、根の行に「（プレビュー）」を付ける。
- プレビューの出し入れ・作り直しは**シーンを未保存（`*`）にしない**。ランタイムは HIERARCHY の直前に `HIERARCHY_QUIET` を送り、
  エディタは次の HIERARCHY 1 通だけ未保存の印を付けない（`HIERARCHY_RESET` と同じ「直前の印」の流儀）。エディタの実装は、受信スレッドで
  `MainWindow._suppressHierarchyDirtyCount` を 1 増やすだけ（既存の `MarkDirtyFromHierarchy` が次の HIERARCHY で 1 減らして印を付けない。`SendNavCommand` と同じ仕組み）。
  ただし、間引きで遅らせていた本物の編集のヒエラルキーと一緒に送るときは印を付けない（未保存の取りこぼしより、余分な `*` のほうが安全）。

## 7. Undo（DFS の不変条件）

Undo の各コマンドは対象を (world_line, DFS 番号) で持つので、履歴に載らない木の変更が挟まると、それより前の Undo が別のアクタを指してしまう。
そこで**プレビューの出し入れ・作り直しは Undo 履歴へ 1 件ずつ積む**（`EditorPreviewTreeCommand`。中身は前後の木の写し `ActorTreeSnapshotCommand`）。

- Ctrl+Z で出したプレビューが消え、Ctrl+Y で戻る。その Undo/Redo も未保存にしない（`Command::is_scene_neutral`）。
- **Undo/Redo で木を組み直した後は、選択を同じアクタへ引き直して知らせる**（2026-10-03。レビュー #1。プレビューに限らず、アクタの追加・削除・付け替えの Undo/Redo も同じ）。
  組み直しは entity も作り直すので、エディタの安定キーと同じ「ルートからの名前の道筋＋同じ名前の兄弟の中で何番目か」で控えて引き直し
  （`app/undo_selection.rs`）、ヒエラルキー（組み直したときは間引かず即時）→ `SELECTED` →（主があれば）`ACTOR_COMPONENTS` の順に送る。
  道筋で引けなくても木の形（DFS 順の子の数の並び）が前後で同じなら同じ番号のまま残す（名前の変更の Undo/Redo）。
  選んでいたアクタが消えた（プレビューの中・追加を戻したアクタ）・親が変わった（道筋も形も変わる）ときは選択から外し、何も残らなければ `SELECTED:-1`。
  以前は古い DFS の選択が残り、インスペクタの次の値の編集（`SET_*:{古い番号}`）がその番号に今いる別の実アクタへ当たって保存されていた。
  エディタ側も、ヒエラルキーの同期で選択の DFS が変わったらインスペクタへ新しい番号を知らせる（`HierarchyPanel.RestoreSelectionAfterSync` / `Hierarchy/SelectionRestorePlan.cs`）。
- 作り直し（`PREVIEW_REFRESH_PATH`）は世界線ごとに 1 件（複数のタブにプレビューがあると、その数だけ Ctrl+Z が要る）。
- Play の開始で外すときは積まない（その時点の履歴は Play 用の空の履歴で、Play を止めると Play 前の写しから木が戻るので、Play 前の履歴と DFS が合う）。

## 8. IPC

| コマンド | 書式 | 応答 |
|---|---|---|
| `PREVIEW_PREFAB` | `PREVIEW_PREFAB:{world_line},{parent_dfs},{json}`。json = `{"prefab":"assets://..","under":"Screens","frame":"assets://ui/prefabs/screen_frame.actor","frame_body":"Body","layer_bias":10000}`（`prefab` だけ必須。`under` 空 = 親の直下、`frame` 無し = 枠なし、`frame_body` 空 = 枠の直下、`layer_bias` 0 = 底上げなし。±16,777,216 に収める。知らないキーは無視） | `HIERARCHY_QUIET` → `HIERARCHY` → `SELECTED…` → `PREVIEW_ADDED:{world_line},{根の DFS}`（失敗は `PREVIEW_ERROR:{理由}`。木は変わらない。json が読めない命令は捨てる。**例外**: 追加した根の位置（DFS）を引けなかったとき〈`ADDED_ROOT_LOST`。通常は起きない〉だけは、木を変え・Undo を 1 件積み・`HIERARCHY_QUIET` → `HIERARCHY` → `SELECTED…` を送った**後で** `PREVIEW_ERROR` を返す。プレビューは出ているので Ctrl+Z か `PREVIEW_CLEAR` で消す） |
| `PREVIEW_CLEAR` | `PREVIEW_CLEAR:{world_line},{dfs}`（dfs を含むプレビューを 1 つ。根でも中のノードでもよい） | `PREVIEW_CLEARED:{数}`（0 なら何も変えない） |
| `PREVIEW_CLEAR_ALL` | `PREVIEW_CLEAR_ALL:{world_line}` | `PREVIEW_CLEARED:{数}` |
| `PREVIEW_REFRESH_PATH` | `PREVIEW_REFRESH_PATH:{path}`（絶対パス or `assets://`。中身か枠がそのプレハブのプレビューを作り直す。比べるときは仮想パスの解決・区切り・大小文字をそろえる） | 組み立てに失敗した根ごとに `PREVIEW_ERROR:{理由}`（その根は古い表示のまま残す）→ 作り直した根があれば `HIERARCHY_QUIET` → `HIERARCHY` → `SELECTED…` → 最後に必ず `PREVIEW_REFRESHED:{作り直した数},{path}`（Edit 以外は 0。失敗した根は数に入らない） |

- Play 中は `PREVIEW_PREFAB`・`PREVIEW_CLEAR`・`PREVIEW_CLEAR_ALL` を断る（`PREVIEW_ERROR`。Play 中の消去は Play 用の履歴に積まれて捨てられ、Play 前の履歴と DFS がずれるため）。

- `HIERARCHY` の各ノード: `preview`（プレビューの部分木の中か）・`preview_root`（根か）・`preview_source`（根の中身のプレハブ。根以外は null）。
- `ACTOR_COMPONENTS`: `editor_preview`（プレビューの中なら `{"root_dfs":N,"is_root":bool,"prefab":"..","frame":".."|null}`、外なら null）。
- 断る命令（`PREVIEW_ERROR`）: Play 中・端末の写しの閲覧中の `PREVIEW_*`、§3 の木の編集とプレビューの中の値・構成の編集、プレビューだけを選んだ `COPY`（§2）、
  地形ルートとその部分木の中への `PREVIEW_PREFAB`（§9）。
- MCP からは専用のツール **`seed_preview`**（2026-10-02。`action: add | clear | clear_all`）で使う。差し込みは
  `parent`（DFS ID か名前パス）・`prefab_path`・`host`（差し込み先の案内の行の見出し。省くと右クリックと同じ枠なし）で、
  UI と同じ道筋（`MainWindow.ScreenPreview.cs` の判定・親の引き直し・送信）を通り、`PREVIEW_ADDED` / `PREVIEW_CLEARED` を待って結果を返す。
  Undo 履歴へ積まれ選択も動くので、**読み取り専用の対話エディタでは既定で拒否**される（docs/editor_mcp.md §4・§5.6・§7.2）。
  AI の差し込みは「最近使ったもの」へは足さない。`seed_hierarchy` の結果に上の欄が載る。

## 9. 制限と持ち越し

- プレビューを置いたノードが**プレハブの再展開**（「プレハブから更新」・プレハブ保存時の自動反映）で作り直されると、その中のプレビューは消える（Ctrl+Z で戻る）。
  例: Wake or Pay の `Shell`（`shell.actor` のインスタンス）の中のタブのスタックへプレビューを置いて `shell.actor` を保存したとき。
- ルート（親なし）へは出せない（必ず親のノードを選ぶ）。
- プレビューの中身は Edit で動かない（スクリプトが中身を作る画面は空の所が残る。入れ子のプレビューで補う）。
- テーマの塗り直し・不透明な画面より下を隠す・出入りの動き、は Edit では再現しない（§4。段・帯の底上げは §4.1 で近づけた）。
- **地形ルートとその部分木の中へは出せない**（`PREVIEW_ERROR:地形の中にはプレビューを出せません…`。2026-10-03。レビュー #7）。Undo の写しは地形ルートを位置の印だけにして
  現物を Keep し、Play も地形を現物のまま保つので、中に置いたプレビューは Ctrl+Z でも消えず・Play を止めても戻らず、DFS をずらして前に積んだ Undo が別のアクタへ当たるため。
- ランタイムの木の編集の拒否（§3）は、表示中の世界線の木で DFS を数える（エディタは表示中の世界線へしか送らない）。
- 読み取り専用はインスペクタとヒエラルキーだけ（ランタイムは IPC の値の編集を断る。§3）。シーンビューのギズモ（ランタイムの中のドラッグで IPC を通らない）では
  プレビューのアクタを動かせてしまう（メモリの中だけの変更で保存されないが、普通の編集として未保存の印が付きうる）。ヒエラルキーの右クリック「削除」（普通のノードを右クリックしたとき）は Delete キーの振り分けを通らない。
- 詳細は [backlog.md](backlog.md) の「Edit 上の画面プレビュー」節。
