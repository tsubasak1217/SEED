# 画面の組み立て（W2-7 の正典。2026-09-28）

1 つのシーン（Wake or Pay の `App.scene`。U-19 の決定）に画面をプレハブとして出し入れするための部品の規則:
**画面のスタック**（`ScreenStack`。押し込み・覆う・フェードの出入り）、**下のタブ**（`TabHost`・`TabBar`。タブごとのスタック）、
**重ねる面**（`ModalHost` と `Dialog`・`BottomSheet`・`TopSheet`）、**トースト**（`ToastHost`・`Toast`）、**戻るの段**（`BackDispatcher`）、
**フォーカス**（`UiFocus`・`FocusScope`）。背景と段階は [app_platform_roadmap.md](app_platform_roadmap.md) §3.3 の「タブ」「ダイアログ」「シート」
「画面のスタック」「トースト」・W2-P4（戻るの段）・§4 の X-3（戻るの最上位は閉じずに背面へ）。使い方の早見は [scripting_api.md](scripting_api.md) §7.18。

| 置き場 | 役割 |
|---|---|
| `scripting/src/Api/UI/Navigation/Model/` | 純粋な計算（エンジンに触れない。`editor/tests/UiComponentsTests` で検算）: 出入りの置き方（`NavTransition.cs`）・スタック（`ScreenStackModel.cs`）・タブ（`TabModel.cs`）・戻るの段（`BackChain.cs`）・ダイアログ（`DialogModel.cs`）・シート（`SheetMath.cs`）・トースト（`ToastQueue.cs`）・フォーカス（`FocusModel.cs`）・重なりのレイヤー（`UiLayers.cs`）・トークンの名前（`NavTokens.cs`）・覆いの指定（`OverlayOptions.cs`） |
| `scripting/src/Api/UI/Navigation/` | 部品のスクリプト: `ScreenStack`・`UiScreen`（画面の土台と手札 `ScreenHandle`）・`TabHost`・`TabBar`・`TabItem`・`ModalHost`・`ModalPlane`（面の土台）・`ModalHandle`（`DialogHandle`）・`Dialog`・`BottomSheet`・`TopSheet`・`ToastHost`・`Toast`・`BackDispatcher`・`UiFocus`・`NavigatorRegistry`・`GestureRelay`・`SafeInsets`・`NavNode` |
| `scripting/src/Api/UI/Looks/UiCurve.cs`・`TabLook.cs` | 動きの曲線（3 次ベジェ）・タブの項目の見た目 |
| `scripting/src/Api/UI/Theme/default_theme.json` | 画面の組み立てのトークン（§9） |
| `runtime/src/engine/components/canvas_layout_item_component.rs`・`core/canvas_layout/pass.rs`・`placement.rs` | 見た目の上書き（`translate`・`translate_fraction`・`layer_bias`。§6） |
| `runtime/src/engine/core/app_base/app/canvas_collect.rs`・`pick_2d.rs`・`gesture_scene.rs` | 底上げしたレイヤーで描く・当たりを決める（§6） |
| `runtime/src/engine/core/scripting/screen_bridge.rs`・`scripting/src/Api/Screen.cs` | `Screen.DpScale`（1 dp の画素数） |
| `templates/ui/prefabs/` | 部品: `screen_stack`・`screen_frame`・`tab_host`・`modal_host`・`dialog`・`bottom_sheet`・`top_sheet`・`toast_host`・`toast`。見本の画面: `nav_*` |
| `templates/ui/scenes/ui_navigation.scene`・`scripts/UiNavigationDemo.cs`・`NavSampleScreen.cs` | 見本（§10） |

---

## 1. シーンの骨組み

```
UiNavigation（Canvas・単位 dp）
├─ Background（Sprite・親に合わせる）…………………… 画面の端まで塗る
├─ RootStack（SEED.UI.ScreenStack。根 = シェル。RootSafeArea = false）…… 全体に積む画面（覆う画面・フェードの画面・鳴動）
│   └─ Screens └─ 画面の枠 └─ シェル（nav_shell.actor）
│        ├─ Header（安全領域の上の分だけ高くする）
│        └─ TabHost
│            ├─ Pages └─ Tab0・Tab1・Tab2（SEED.UI.ScreenStack。1 段のレイヤー 1000）…… タブの中に積む画面
│            └─ TabBar └─ Item0..（SEED.UI.TabItem）
├─ Modals（SEED.UI.ModalHost）└─ Overlays・Sheets・Dialogs …… 重ねる面の帯
└─ Toasts（SEED.UI.ToastHost）…………………………………… いちばん手前
```

- 前後はレイヤーの底上げ（§6）で決まる: 画面のスタックの段 < 覆い < シート < ダイアログ < トースト。木の順は同じレイヤーの中の前後だけ。
- 部品は**プレハブの作り**（子の名前）で見つけ合う。子が無ければその機能だけが働かない（落ちない）。

## 2. 画面のスタック（`ScreenStack`）

| 操作 | 振る舞い |
|---|---|
| `Push(prefab, transition?, args?, options?)` | 積む。出入りは引数 → `ScreenOptions.Transition` → スタックの `DefaultTransition`（既定 Push）。手札 `ScreenHandle` を返す |
| `Pop(result?)` | 1 つ下ろす（根だけなら false）。動きは下ろす画面が積まれたときと同じ種類の逆。結果は下ろした画面の手札へ（`Closed`・`WhenClosed`） |
| `Replace(prefab, …)` | いちばん上を置き換える（外れた画面の手札は結果 null で閉じる） |
| `PopToRoot()` | 根まで下ろす（途中の段は動かさずに外し、いちばん上だけが戻る動きをする） |
| `SetRoot(prefab, transition = None, …)` | 根からやり直す（旧いいちばん上が動いて退き、残りは動かさずに外す） |
| `RootPrefab`（フィールド） | 最初の根（空なら積まない）。`RootSafeArea` = false で根を安全領域の外（シェルが自分で扱う） |

**画面の枠**（`screen_frame.actor`）: `ScreenFrame`（Canvas・親に合わせる・背景の Sprite〈不透明の画面は `color.background`、透ける画面は透明。
`raycast_target`〉・受けるジェスチャーの無い CanvasGesture＝遮る板）→ `Body`（親に合わせる・`CanvasSafeArea`）→ 画面のプレハブの根。
`ScreenOptions.SafeArea` = false なら枠の直下に作る。背景は画面の端まで、画面の中身は安全領域の中。

**作り方**: `Instantiate` は次のフレームにできあがるので、枠 → 中身の 2 フレームで作り、できあがるまで枠を隠す（最初のフレームに既定の位置で
ちらつかない）。中身の根に `UiScreen` があれば `OnScreenEnter(args)` を 1 回（最大 30 フレーム探す。付いていないプレハブも積める）。

**積み下ろしと動き**: 操作は並び（`ScreenStackModel`）をすぐ変え、出入りの計画を順に流す。動いている途中に次の操作が来たら、今の動きを終わりまで
飛ばしてから次を始める。動きの間は `Redraw.KeepAlive`（on_demand でも途中で止まらない）。

| 種類 | 進む（積む・置き換える） | 戻る（下ろす） | 時間・曲線（テーマ） |
|---|---|---|---|
| `Push` | 入る画面が右の外から（ずらし x: 1 → 0）、下の画面は左へ `ratio.push_parallax`（0.3）だけずれる | 上の画面が右へ出る（0 → 1）、下の画面が戻る | `motion.push` 0.3 秒・`motion.push_curve`（Material 3 standard） |
| `Cover` | 入る画面が上の外から降りる（y: −1 → 0）。下の画面は動かない | 上の画面が上へ戻る | `motion.cover`・`motion.cover_curve` |
| `Fade` | スタックの幕（`Veil`・`color.background`）が濃くなり、半分で入れ替わって薄くなる | 同じ | `motion.fade`・`motion.fade_curve`（fastOutSlowIn） |
| `None` | すぐ入れ替わる | 同じ | — |

ずらしは画面の大きさに対する割合（`CanvasLayoutItem.TranslateFraction`。§6）なので、スタックは画面の大きさを知らなくてよい。

**落ち着いた状態**（`ScreenStackModel.Settle`）: いちばん上は見せる。上から下へ、不透明（`Opaque`。既定 true）な画面より下は隠す（`Visible = false`
＝描かない・当たり判定に出ない＝入力を受けない）。透ける画面（`Opaque = false`）の下は見せる。隠した画面のうち `KeepState = false` のものは実体を手放し、
戻ってきたら作り直す（既定 true = スクロールの位置・入力の途中が残る）。

**入力の遮り**: 枠は遮る板を持つ（後ろの画面へ指が届かない。W2-2 の R3）。出入りの間はスタックの `Blocker`（いちばん手前）を見せて、動いている画面の
部品を押させない。`raycast_target` の背景は旧来の `OnPointer*` も遮る。

**知らせ**（`UiScreen`）: `OnScreenEnter(args)`（作られた・作り直された）→ `OnScreenShown()`（上の画面になった。動きの後・タブへ戻ったとき）・
`OnScreenHidden()`（覆われた・タブを離れた）→ `OnScreenExit()`（下ろされる直前）。`Close(result)` で自分を下ろす。スタックの `Changed` は落ち着いた後。

## 3. 重ねる面

### 3.1 `ModalHost`（覆い・シート・ダイアログを開く所。シーンに 1 つ）

面のプレハブを種類の帯（`Overlays`・`Sheets`・`Dialogs`）の下に作り、面のスクリプト（`ModalPlane` の派生）が `OnStart` で開く約束を受け取る。
帯の底上げは `layer.overlay`・`layer.sheet`・`layer.dialog`、帯の中の j 番目は j × `layer.modal_step`（後から開いた面が手前）。
面は準備ができるまで隠し、開いている間はフォーカスの範囲を前へ出す。手札（`ModalHandle`・`DialogHandle`）で閉じるのを待つ・スクリプトから閉じる。

### 3.2 ダイアログ（`Dialog`）

| 項目 | 規則 |
|---|---|
| 作り | `Scrim`（幕・タップ）と `Card`（面・角丸 `radius.dialog`・遮る板・縦の CanvasStack〈余白・間隔 0〉に Title・Message・Buttons）は兄弟（札の上のタップが幕に届かない） |
| ボタン | 1〜3 つ。左から中立・いいえ・はい（Material 3 の並び）。文字が空のボタンは出さない。どれも空なら「OK」の Positive だけ。幅 = 文字の幅の見積もり ＋ 左右 1.5 em（高さより狭くしない）、ボタンの間 8（プレハブ） |
| 結果 | `Positive` / `Negative` / `Neutral` / `Dismissed`（幕のタップ・戻る・`Dismiss()`）。1 回だけ（連打・閉じる途中の戻るで 2 度出さない） |
| 幕のタップ | `DismissOnScrimTap`（既定 true）なら Dismissed。false なら何もしない |
| 戻る | `CancelableByBack`（既定 true）なら Dismissed。false でも**戻るは受ける**（後ろの画面へ回さない＝必ず答えさせる） |
| 動き | 幕の濃さ 0 → `opacity.dialog_scrim`（0.32）、札の大きさ `ratio.dialog_scale_from`（0.9）→ 1（`motion.dialog` 0.2 秒・`motion.dialog_curve`）。出るときは逆。札の大きさは実行中の見た目の倍率（`CanvasLayoutItem.VisualScale`。札の矩形の中心の周りに背景・題・本文・ボタンが一体で縮む。§6）に「開き具合の倍率 × 予測型の戻るのプレビューの倍率（§5.1）」を書く（W2 の手直し P2-1。`DialogMetrics.CardScale`）。戻るを確定した後は、プレビューの倍率を保ったまま出る（縮めた姿勢から閉じる） |
| 大きさ | 札の幅 `size.dialog_width`（312）・余白 `size.dialog_padding`（24。上下左右）・間隔 `size.dialog_title_gap`（16。題 → 本文）と `size.dialog_actions_gap`（24。本文〈本文が無ければ題〉→ ボタンの行）・ボタンの高さ `size.dialog_button_height`（40）。札の高さは中身から決める（下の「札の縦の割り付け」） |

**札の縦の割り付け（W2 の手直し P2-1。`Navigation/Model/DialogMetrics.cs`）**: 札の高さ = 上の余白 ＋ 題 ＋ 間隔 ＋ 本文 ＋ 間隔 ＋ ボタンの行 ＋ 下の余白
（出さない区画〈空の題・空の本文〉とその間隔は数えない）。`Dialog.Layout` がこれを札の `CanvasLayoutItem.PreferredSize` と背景の `Sprite.Size` の両方へ書く
（札は親〈Dialog の CanvasStack〉が矩形を割り当てるので、札の CanvasStack の `fit_height` では背景のスプライトが伸びない。`canvas_layout/pass.rs` の
「中身に合わせる」は割り当ての無いノードだけ。以前はこれで背景がプレハブの 160 のまま中身がはみ出し、上下の余白が 7.6 dp に見えた。roadmap §3.9.2 (a)）。
間隔は区画ごとに違うので札の CanvasStack の等間隔の `spacing` は 0 にし、2 つの見える区画の間に下の区画の「上の間隔」（本文の上 = `size.dialog_title_gap`、
ボタンの行の上 = `size.dialog_actions_gap`）を入れて、**上の区画の枠（CanvasLayoutItem の高さ）に足す**（題の枠 = 題の行 ＋ 下の間隔。Text は枠の上端に置かれるので
間隔は枠の下の空きになる。Text の `BoxHeight` は行の高さだけ）。トークンの値を変えても、区画を隠しても同じ計算で合う（`DialogTests` で検算）。

| 中身（既定のテーマ） | 札の高さ（dp） | 内訳 |
|---|---|---|
| 題 ＋ 本文 1 行 ＋ ボタン | 178.4 | 24 ＋ 28（題 20 × 1.4）＋ 16 ＋ 22.4（本文 16 × 1.4）＋ 24 ＋ 40 ＋ 24 |
| 題なし（本文 1 行 ＋ ボタン） | 134.4 | 24 ＋ 22.4 ＋ 24 ＋ 40 ＋ 24 |
| 本文なし（題 ＋ ボタン） | 140 | 24 ＋ 28 ＋ 24 ＋ 40 ＋ 24 |
| 題 ＋ 本文 3 行 ＋ ボタン | 223.2 | 24 ＋ 28 ＋ 16 ＋ 67.2 ＋ 24 ＋ 40 ＋ 24 |

- 値の出典: Flutter master の `packages/flutter/lib/src/material/dialog.dart` の AlertDialog（Material 3。2026-09-29 に取得して確かめた）の titlePadding
  左上右 24・contentPadding の上 16（題があるとき）と下 24・actionsPadding（M3）の下 24・ボタンの間 8（buttonPadding.horizontal 16 ÷ 2）・角丸 28・最小の幅 280。
  **Flutter と違うところ**: 本文の無いときの題 → ボタンは Flutter が題の下の余白 20、SEED は `size.dialog_actions_gap`（24）にそろえた（トークンを増やさない）。
  題の無いときの本文の上は Flutter が contentPadding の上 16、SEED は余白の 24（`size.dialog_padding` は上下左右で 1 つ）。
- **文字の高さの見積もり**（`DialogLayout`。`Text.Measure` は W2-6c）: 題は折り返さない（改行があればその行の数）、本文は中の幅（312 − 24 × 2 = 264）で折り返す。
  行の数はエンジンの折り返しの規則（`runtime/src/engine/core/font/text_wrap.rs`: ASCII の英数字と ' - の語・空白は行末でぶら下げる・日本語は 1 文字ずつ・
  行頭禁則のぶら下げ 2 文字まで・行末禁則の追い出し・入りきらない語の強制分割）を C# で真似て（`TextWrapEstimate`）、1 文字の送り幅は組み込みの書体
  （M PLUS Rounded 1c Regular）の表（`BuiltInFontAdvance`。SEED の文字の大きさ 1 = 書体の ascent − descent〈1395 単位〉なので全角 = 1000 / 1395 = 0.7168 em・
  数字 0.4444 em・空白 0.1935 em。ASCII は文字ごと、半角カナ 0.3584 em、それ以外は全角）から取る。枠にぎりぎりの行（余裕が大きさ × 0.001 未満）は折れる側に倒す
  （行が足りなくなって本文がボタンに重なるより、1 行余る方がよい）。行の高さ = 大きさ × `DialogLayout.LineHeightEm`（1.4。M3 の本文 20 / 14 ≒ 1.43）で、
  題と本文の Text の `LineSpacing` もこの値にして、描く行送り（枠ありの Text は大きさ × 行間。行送りの余白は行の上下へ半分ずつ）と見積もりを一致させる。
  以前（全角 1 em・半角 0.55 em の 2 種類）は「寝坊で失う最大金額が 3,000 円になります。」（幅 232.8）を 2 行と見積もっていた。

### 3.3 下からのシート（`BottomSheet`）

```
BottomSheet（Canvas・親に合わせる）
├─ Scrim（幕。見た目だけ）
└─ Track（縦の CanvasScroll〈端で止まる・慣性・間隔のスナップ・入れ子で渡さない〉・縦の CanvasStack）
   ├─ Gap（高さ = 領域。タップ = 閉じる）
   └─ Panel（高さ = 板。つまみ・Content）
```

- **位置 = 板の出ている高さ**（スクロールの位置。0 = 閉じた・板の高さ = 全部）。段は半分あり（`HalfDetent`）なら {0, 板/2, 板}、無ければ {0, 板}。
  CanvasScroll の Interval のスナップ（間隔 = 板/2 か板）をそのまま使うので、**つまみ・板のドラッグとフリックの速度（W2-2）から止まる段を Rust の物理が決める**。
- **閉じる**: 止まった位置が 0（`SheetMath.Classify`）なら閉じる（下へ払う・引き下ろす）。隙間（幕の上）のタップ・戻る・`Close(result)` でも閉じる。
- **入れ子の受け渡し**: 中身の縦の一覧（`hand_off_to_parent = true`）が先頭で下へ引かれた残りは Track へ渡る（W2-3）。全部開いた状態から一覧を下へ引くと
  シートが下がる（半分の段で止まる・閉じる）。上へは一覧が先に動く（Android の「先にシートを広げる」〈nested pre-scroll〉は無い。§13）。
- 板の高さ = 領域 × `HeightFraction`（0 以下なら `ratio.sheet_max_height` 0.9）。領域の大きさは Track の `CanvasScroll.ViewportSize`（前のフレームの値）。
- 開く・閉じる動きは `CanvasScroll.ScrollTo`（`motion.sheet` 0.25 秒。曲線は ScrollTo の easeInOut）。幕の濃さは最初の段まで開く間に 0 → `opacity.scrim`。
- 板の下の余白 = 下の安全領域（`SafeInsets.BottomUnits`。背景は端まで。CanvasSafeArea はスクロールの窓の中で位置により縮み直すので使わない）。

### 3.4 上からの覆い（`TopSheet`。プロフィール・オプション）

- 画面の遷移ではなく今の画面に被さる（画面のスタックに乗らない。背後のタブは見えたまま）。
- 降りる: 板の自分の高さに対するずらし −1 → 0、幕 0 → `opacity.scrim`（`motion.overlay` 0.22 秒・`motion.overlay_curve` = easeOut。Flutter 版の top_sheet）。
- 閉じる: 幕のタップ（`DismissOnScrimTap`）・戻る（`CancelableByBack`。閉じなくても戻るは受ける）・`Close(result)`・指（`DismissGesture`）:
  `FlickDown`（既定。**Flutter 版の top_sheet.dart と同じく板を下へ `speed.fling_dismiss` 300 dp/秒 超で払う**）/ `DragUp`（板が指に付いてきて、
  `size.drag_dismiss` 96 dp 以上か上へ速く払うと閉じる）/ `None`。板そのものが縦のドラッグを受けるので、中の一覧がスクロールする所では一覧が指を取る。
- 板の上の余白 = ステータスバー（`SafeInsets.TopUnits`）。

### 3.5 トースト（`ToastHost`・`Toast`）

- `Toast.Show(文字, ToastLength)` / `ToastHost.Show(文字, 秒)`。同時に `count.toast_visible`（3）個まで、あふれた分は待たせ、空いたら古い順に出す（`ToastQueue`）。
- 見せる時間 `motion.toast_short` 2 秒 / `motion.toast_long` 3.5 秒は**実時間**で数える（on_demand で描画を止めている間も進み、次に消える時刻に
  `Redraw.RequestAfter` で起きる）。押さえている（スワイプの途中の）間は止める。
- 入る: 自分の高さだけ下から上がる。出る: 時間切れ・`Dismiss` は下へ、横へ払った（`size.drag_dismiss` 以上か `speed.fling_dismiss` 以上）はその向きへ。
- 下の余白 = `space.l` + 下の安全領域 + `BottomOffset`（タブのバーの上に出すときはバーの高さ）。**戻るは受けない**。

## 4. 下のタブ（`TabHost`・`TabBar`・`TabItem`）

| 項目 | 規則 |
|---|---|
| タブごとのスタック | `Pages` の子（`TabNames` の順）がタブ。選んでいないタブは隠して保つ（描かない・入力を受けない・状態はそのまま＝戻ると前の位置） |
| もう一度押す | 選んでいるタブを押すと根へ戻る（`PopToRoot`）＋ `TabReselected`（根にいる画面は先頭へスクロールする合図に使う） |
| 戻る | 選んでいるタブのスタック（内側のナビゲーターなので先に尋ねられる）が根より上なら 1 つ下ろす → 最初のタブ以外なら最初のタブへ（`BackToFirstTab`。既定 true）→ 受けない（アプリを背面へ） |
| 離れたら根へ | `ResetOnLeave`（既定 false） |
| バー | 高さ `size.tab_bar`（64）+ 下の安全領域。項目の見た目は `TabLooks`（選んだ: 印 `color.selected`・文字 `color.on_surface`／選んでいない: 印なし・`color.on_surface_muted`／押している: 重ね色の印）。タップで触感 |

Wake or Pay の仕様 §3.7 は「タブの最上位 → 背面へ」だけを書き、最初のタブへ戻る段は書いていない（Flutter 版の go_router の既定は背面へ）。
W2-7 の依頼（タブの根以外なら根のタブへ）に合わせて既定 true にした。仕様どおりにするなら `BackToFirstTab = false`。

## 5. 戻るの段（`BackDispatcher`）

戻る（Android の戻るキー・戻るジェスチャーは `input/key_remap.rs` で Escape になる。PC の Esc も同じ）を上の層から順に配り、最初に「受けた」層で止める。

| 順（`BackOrder`） | 層 | 受ける条件 |
|---|---|---|
| 100 Focus | 今のフォーカス | 相手が `IBackConsumer` で `HandleBack()` が true（W2-6 の入力欄が IME を閉じる・フォーカスを外す） |
| 200 Dialog | いちばん上のダイアログ | 開いていれば必ず受ける（閉じられないダイアログも） |
| 300 Sheet | いちばん上のシート | 同上 |
| 400 Overlay | いちばん上の覆い | 同上 |
| 500 Navigation | 画面のスタック・タブを**内側から**（見えていて、祖先のスタックの上の段の中にあるものだけ。祖先の数が多い順） | 上の画面の `IgnoreBack` → `UiScreen.OnBackPressed()` → 1 つ下ろす → タブなら最初のタブへ |
| — | どれも受けない | `SEED.Platform.App.MoveTaskToBack()`（閉じずに背面へ。デスクトップの模擬はログ `模擬: 閉じずに背面へ`）。`MoveTaskToBackWhenUnhandled = false` で止められる |

- キーは画面の組み立ての部品（`ScreenStack`・`TabHost`・`ModalPlane`・`ModalHost`・`ToastHost`）が毎フレーム `PollBackKey()` で読み、同じフレームに 1 回だけ配る。
- スクリプトは `AddLayer(順, 名前, 受けたら true)` で自分の層を足せる（戻り値を Dispose で外す）。`Dispatch()` は画面の「戻る」ボタンから呼ぶ。`Dispatched` で結果を知る。
- 作りかけ（スクリプトが動く前）の面がある種類の層は戻るを受けて捨てる（開いた直後の二度押しで後ろが閉じない）。閉じる動きの途中の面も受けるだけ。
- **Android の実機**: キーボードが出ているときの 1 回目の戻るは IME 自身が閉じ、アプリには届かない（W2-0 の I-11）。Android 13 以降の「予測型の戻る」
  （戻るジェスチャーの途中で閉じるものを縮めて見せる）はプロジェクト設定 `android.predictive_back` で opt-in（§5.1。既定は従来どおり）。

### 5.1 受ける層の問いと予測型の戻る（W2 の手直し 3b。2026-09-29）

**受ける層があるか**（`BackDispatcher.WouldHandle()`）: 層の handler は呼ぶと閉じる・下ろすまでするので、層は副作用の無い問い（wants）を持つ
（`BackChain.Add(順, 名前, handler, wants, preview)`・`BackChain.WouldHandle()` が上の層から問い、最初に true の層の名前と順を返す純粋な計算）。
**問いの無い層は受けるとみなす**（安全側: 受けると答えてもアプリには Escape が届き、受けなければ今の `MoveTaskToBack` で背面へ回るので、見た目が出ないだけ）。
`MoveTaskToBackWhenUnhandled = false` のアプリは常に受ける。

| 層 | 問い（副作用なし。handler と同じ決め方） | プレビューの相手 |
|---|---|---|
| Focus | 今のフォーカスが `IBackConsumer`（中身は問えないので受けるとみなす） | なし |
| Dialog・Sheet・Overlay | `ModalHost.Count(種類) > 0`（`ModalHost.WantsBack`。面があれば `HandleBack` は必ず true） | 最後に開いた面が開いていて戻るで閉じるとき（`CancelableByBack`）: ダイアログの札（真ん中）・シートの板（下の辺を留める）・覆いの板（上の辺を留める） |
| Navigation | 見えているナビゲーターのどれか（内側から）: `ScreenStack` は上の画面が `ScreenOptions.IgnoreBack` か `UiScreen.WouldConsumeBack()`（既定は `UiScreen.IgnoreBack` か `OnBackPressed` を上書きした型なら true＝受けるとみなす。画面が同じ条件で上書きすれば正確に答える）か根より上、`TabHost` は `TabModel.DecideBack != None` | 最初に受けるのが根より上のスタックのとき（戻るを無視する画面・出入りの途中・できあがっていない画面は無し）: 上の画面の枠（真ん中・指の向きへずらす）。`OnBackPressed` を上書きした画面も縮め、確定しても下ろさなければ（未保存の確認など）元へ戻す。タブの切り替えは無し |
| スクリプトの層 | `AddLayer(順, 名前, handler, wants, preview)` の wants（`AddLayer(順, 名前, handler)` は問いなし＝受ける） | preview（任意） |

出入りの途中の `ScreenStack` も同じ規則（`HandleBack` は今の動きを終えてから決めるが、並びは積み下ろしの時点で変わっているので答えは同じ。依頼の「出入りの途中は受ける」は
押した結果と食い違う〈根へ戻る途中の戻るは背面へ回る〉ので採らなかった）。検査: 128 通りの状態で押す前に毎回「`WouldHandle()` == 押した結果が背面へ回らない」と層の名前が一致。

**基盤への知らせ**（3a の `App.SetBackCallbackEnabled`）: `PollBackKey`（フレームに 1 回）の中で `WouldHandle()` を計算し、前回と違うとき（とスクリプトの読み直しの後の
最初のフレーム）だけ送る（`BackCallbackSync`）。返り値（予測型の戻るが有効か）が false なら以後は計算も送りもしない（PC・`predictive_back` の無いアプリ）。
Android 側は、受ける（true）ならアプリのコールバックを登録したまま（戻るで Escape と手ぶりの知らせが届く）、受けない（根）ならシステムに任せる
（Android 16 以上はシステムの「背面へ回す」コールバック＝ホームへ戻る見た目。13〜15 はランチャーから起動した根だけ外す。詳しくは [android.md](android.md) §25.18）。
ログ `[UI] back: callback on=… predictive=…`（送ったときだけ）。

**プレビュー**（`BackPreview.cs`・純粋な移り変わりは `Model/BackPreviewModel.cs`）: 3a の知らせをエンジンの受け口（`PlatformEvents.AddEngineListener`）で受ける。

| 知らせ・キー | すること |
|---|---|
| `platform.back_started` | 最初に受ける層の相手を問う（無ければ何も縮めない）→ 姿勢を当て始める |
| `platform.back_progressed`（毎フレーム） | 倍率 = 1 − (1 − `ratio.back_preview_scale` 0.9) × 曲線（`motion.back_preview_curve`）(進み具合)。画面は指の動く向き（左の端からなら右）へ `size.back_preview_shift`（8 dp）× 同じ曲線 |
| `platform.back_cancelled` | 今の姿勢から元へ（`motion.short`） |
| `platform.back_invoked` | Escape を待つ（姿勢はそのまま。0.5 秒〈`BackPreviewModel.KeyWaitTimeout`〉来なければ元へ） |
| Escape（`PollBackKey`） | 手ぶりの途中・確定待ちなら**確定**: 姿勢を保ったまま `Dispatch()` → 相手が閉じる・下ろす動きに入ればその姿勢から続け（終わったら元の姿勢へ。ノードは消えている）、入らなければ（別の層が受けた・画面が下ろさなかった）元へ戻す。それ以外はふつうの戻る |

- 画面のスタック: 上の画面を縮める間、下の画面の実体が隠れていれば見せる（`KeepState = false` で手放した画面は背景のまま）。確定して下ろすときは、見せていた下の画面を
  視差の位置へ跳ばさず 0 のまま見せる（`Run.FromPreview`）。取り消したら隠し直す。
- **遅れた知らせ**: invoked の知らせ（プラットフォームのイベントの箱）と Escape（入力の状態）は別の道で届き同じフレームとは限らない。手ぶりの番号で「確定・取り消し・
  諦めた手ぶりの遅れた知らせ」を捨てる。started は必ず新しい手ぶり（番号は Java はプロセスごと、PC の模擬は Play の区切りごとに 1 から）。started の来なかった
  手ぶりの progressed は新しい手ぶりとして始める。Android 13（invoked だけ）・3 ボタン・キーボード・PC の Esc だけのときは従来どおり（プレビューなし）。
- **取り残し防止**: 相手が無効になった（画面の切り替え・タブを離れた・面が閉じた・層の破棄）らすぐ元へ。スクリプトの読み直しでは次のフレームで元へ。
- ログ `[UI] back-preview: start #番号 edge=… target=…`・`invoked`・`cancel`・`commit … → 層`・`abandon`（progressed は出さない）。

## 6. 重なりのレイヤーと見た目の上書き（CanvasLayoutItem の実行中だけの欄）

SEED の 2D の描画は「ゾーン → レイヤー → 種別（スプライト → 図形 → パーティクル → テキスト）」の順（`ui_draw_order.rs`）なので、同じレイヤーの画面を重ねると
**下の画面の文字が上の画面の板より手前に出る**。W2-7 で `CanvasLayoutItemComponent` に**保存しない**（`#[serde(skip)]`）欄を 3 つ足した（W2 の手直し 3b で
見た目の倍率を 1 つ足した。規則の正典は [canvas_camera_rework.md](canvas_camera_rework.md) §6.7）。Undo・インスペクタ・シーンの保存・プレハブの差分には出ない。

| 欄（スクリプト） | 意味 |
|---|---|
| `LayerBias`（`layer_bias`） | 自分と子孫の表示（Sprite・SkinnedSprite・Text〈インライン画像〉・2D パーティクル）のレイヤーに足す値。祖先の値と足し合わせる。描画の並び・ポインタの最前面（`pick_2d`）・ジェスチャーの遮り（R3）・ID 描画が同じ値で比べる |
| `TranslateFraction`（`translate_fraction`） | 置かれた後の平行移動（自分の置かれた矩形の大きさに対する割合）。親に合わせた・コンテナが並べたノードも動く（`Position` は使われないため） |
| `Translate`（`translate`） | 同じくキャンバスの単位（× 親の累積スケール） |
| `VisualScale`（`visual_scale`。W2 の手直し 3b。既定 (1, 1)） | 自分の置かれた矩形（割り当てた矩形か CanvasComponent の領域。無ければ自分の位置）の**中心の周り**の倍率。子孫（入れ子のキャンバスの子も中心へ寄る）・描画・当たり判定・切り抜き・`LayoutRect` がそろって縮む。レイアウト（大きさ・並び・安全領域・`LayoutSize`）は倍率の前のまま。平行移動の後に掛かる |

- 画面のスタックの段 i の枠は `i × LayerStep`（既定 `layer.stack_step` = 10,000。タブの中のスタックは 1,000）。置き換え・やり直しの入る画面は出る画面より半段上。
  幕と遮る板はすべての画面より上。**画面の中の表示のレイヤーは段の値より小さく保つ**（タブの中なら 1,000 未満）。
- 覆い・シート・ダイアログ・トーストの帯は 100 万・200 万・300 万・400 万（`UiLayers`。`CanvasLayoutItem.LayerBias` が受ける上限は ±16,777,216）。
- 平行移動は子孫が付いてくる（行列を足すだけ）。当たり判定・切り抜き・`ScreenPosition`（`layout_adjusted`）も動いた位置。**祖先の平行移動は子孫の
  安全領域の計算に入れない**（横から入る画面の中身の箱が、途中で画面の端に合わせて縮み直さない。`CanvasParentFrame.visual_shift`）。
- `SEED.Draw` の図形には底上げが効かない（図形はノードではなく座標空間の持ち主を持つだけ。§13）。
- 値が 0（既定）のノードは従来とまったく同じ計算（単体テスト `zero_visual_overrides_leave_table_unchanged`）。
- **見た目の倍率**（3b）は予測型の戻るのプレビュー（§5.1）が画面の枠・ダイアログの札・シートの板に当てる（ダイアログの出入りの動きも。P2-1）。エンジンは倍率を行列に入れず大きさと子の位置への掛け算
  （サイズ倍率・子の累積スケール）で持つので、中心が動かないよう位置をずらし、子の累積スケールごと縮める（`canvas_layout/placement.rs` の `scale_placement`）。
  倍率を当てたノードのコンテナの並びは倍率の空間で求め、**子孫の安全領域は倍率の前の位置・大きさで求める**（縮めている画面の中身の箱がステータスバーの分の余白を
  失わない。`CanvasParentFrame.visual_scale` と `visual_shift` の写像）。既定 (1, 1) のノードは倍率の計算を通らず、表はビット単位で今と同じ（golden の単体テスト
  `default_visual_scale_keeps_golden_table`）。
- **ダイアログの出入りも見た目の倍率**（W2 の手直し P2-1。2026-09-29）: 以前の `Dialog` は保存される `CanvasTransform.Scale`（pivot の周りだが、
  入れ子のキャンバスの子は札の左上へ寄る）で札を縮めていたので、閉じる途中に背景は中心へ、題・本文・ボタンは左上へ縮んだ（roadmap §3.9.2 (b)）。
  出入りを `VisualScale` へ替え、プレビューと同じ欄なので「開き具合の倍率 × プレビューの倍率」を書く（§3.2 の「動き」）。`NavNode.SetScale` は消した
  （シート・覆い・トーストの出入りは平行移動だけで、`CanvasTransform.Scale` を使う所は無くなった）。

## 7. フォーカス（`UiFocus`・`FocusScope`）

- **相手**（`IFocusable`）: キーボードの入力を受ける部品（W2-5 のホイール・W2-6 の入力欄）。`UiFocus.Request(相手)` で申し出る。`WheelFocus`（W2-5）は
  `UiFocus` の窓口にした（API は同じ）。
- **範囲**（`FocusScope`）: 画面の枠・ダイアログ・シート・覆いごと。相手は祖先をたどって最初に見つかった範囲に属する。**いちばん前の範囲の相手だけが今のフォーカス**
  （ダイアログが開いている間、下の画面のホイールは矢印キーを受けない）。後ろの範囲の相手の申し出は、その範囲が前に出たときの相手として覚える。
- 範囲の前後: 画面を積む・上の画面になる → 前へ。覆われる・タブを離れる → 後ろへ（根の範囲の前）。閉じる・下ろす → 外す（前に出た範囲の覚えていた相手へ戻る）。
  重ねる面の範囲は、開いている間に下で画面の積み下ろしが終わっても前のまま（`overlay`）。外のスタックが落ち着いたら、上の画面の中の入れ子のスタック
  （シェルの中のタブ）の上の画面の範囲も前へ出す。
- 戻るの段は最初に今のフォーカスへ尋ねる（`IBackConsumer`）。

## 8. 安全領域とシステムバー

| 所 | 扱い |
|---|---|
| 画面の枠 | 背景は端まで、中身は `Body` の `CanvasSafeArea` の中（`ScreenOptions.SafeArea` = false で外。全画面の鳴動などが自分で扱う） |
| シェル（`RootSafeArea = false` の根） | ヘッダーは上の安全領域の分だけ高く（見本の `NavSampleScreen`）、タブのバーは下の安全領域の分だけ高く、項目はバーの上（`TabBar`） |
| 下からのシート | 板の下の余白 = 下の安全領域（`SafeInsets.BottomUnits`） |
| 上からの覆い | 板の上の余白 = 上の安全領域（`SafeInsets.TopUnits`） |
| トースト | 下の余白 = `space.l` + 下の安全領域 + `BottomOffset` |

`SafeInsets` は `Screen.SafeArea`（画素）を `Screen.DpScale`（W2-7 で足した 1 dp の画素数）で dp にする。PC では `SEED_SIM_SAFE_AREA=左,上,右,下`・
`SEED_SIM_SCALE_FACTOR` で模擬できる。システムバーの表示は W1 の `system_bars`・`Window.SetSystemBarsVisible`（バーの文字色は backlog）。

## 9. テーマのトークン（画面の組み立て。`NavTokens`）

画面の組み立てのトークン（幕・トーストの色、ダイアログ・シート・トースト・タブの角丸と大きさ、つまみ、出入りの時間と曲線、幕の濃さ、割合・速さ・数、重なりのレイヤー）の
**一覧（名前・型・既定値・使う部品）の正典は [ui_theme.md](ui_theme.md) §8**（W2-9 で 1 つの表にまとめた）。値の出典: 画面の出入りの push・cover は
Material 3 の standard (0.2, 0, 0, 1)、fade は fastOutSlowIn、上からの覆いは Flutter 版の top_sheet の 220ms・easeOut (0, 0, 0.58, 1)、覆い・シートの幕は
Flutter 版の 54%・ダイアログの幕は Material 3 の 32%、フリックで閉じる速さは Flutter 版の top_sheet の 300 dp/秒、つまみは M3 の drag handle、
ダイアログの区画の間隔（`size.dialog_title_gap` 16・`size.dialog_actions_gap` 24。W2 の手直し P2-1）は Flutter master の AlertDialog（Material 3）の
contentPadding の上 16・下 24（2026-09-29 にソースで確かめた。§3.2）、重なりのレイヤーの帯は §6。曲線は `motion.push_curve` の `.x1`・`.y1`・`.x2`・`.y2` の 4 つの数（CSS の cubic-bezier。`UiCurve.FromTheme`）。
予測型の戻るのプレビュー（3b。§5.1）の `ratio.back_preview_scale` 0.9 は Material 3 の予測型の戻る（画面が 90% まで縮む）、`size.back_preview_shift` 8 dp は
同じく縮めた画面を指の向きへ寄せる動き、`motion.back_preview_curve` (0, 0, 0, 1) は Android の開発者向け文書の独自の予測型の戻るの例の GestureInterpolator から
取った値で、**どれも記憶による**（実機で見比べて調整する）。元へ戻る時間は `motion.short`。

## 10. 見本（`templates/ui/scenes/ui_navigation.scene`）

§1 の骨組みに、ホーム（押し込み・覆う画面・フェード・ダイアログ・シート・トースト）・一覧（20 行のスクロール）・設定（数のホイール）の 3 タブと、
詳細（戻る・さらに積む・置き換える・根まで戻る。偶数段は「未保存」で戻ると確認のダイアログ）・覆う画面・シートの中身（30 行。行を押すと結果つきで閉じる）・
覆いの中身を置いた。ログは `[UI] nav:`・`[UI] tab:`・`[UI] modal:`・`[UI] toast:`・`[UI] back:`・`[UI] demo:`。
デバッグの命令（`SCRIPT_DEBUG:nav,<名前>`）: `state`（段・タブ・面・トースト・フォーカス・ホイールの値）・`back`・`mark <文字>`・`toast <文字>`・`tab <番号>`・
`open <dialog|dialog-modal|sheet|sheet-full|overlay>`・`where <名前>`（画面の位置）・`bias <名前|…>`（底上げの値）。
テンプレートライブラリの「UI 部品」（`ui` のフォルダ）から取り込むと `assets/ui/...`。プロジェクト設定は `render_policy: "on_demand"` を想定。

## 11. 検証（2026-09-28・PC）

- **単体テスト（C#）** `dotnet run --project editor/tests/UiComponentsTests`（51 件。うち W2-7 の 21 件）: 既定のテーマが全トークンを持つ、曲線（端点・単調・
  fastOutSlowIn が `SwipeMath.Ease` と一致・テーマから読む）、出入りの置き方（押し込み・視差・覆う・フェードの入れ替わり・なし）、スタック（積む・下ろす・根・結果・
  置き換え・根まで・やり直し・落ち着いた状態〈不透明・透ける・手放す・作り直し〉）、タブ（切り替え・もう一度押す・タブごとの段を保つ・戻る）、
  **戻るの段の全パターン**（IME・ダイアログ・シート・覆い〈各あり・なし〉× 根のスタック 2 段 × タブ 2 つ × タブのスタック 2 段 = 128 通りの状態で、
  背面へ回るまで 1 回ずつ押して毎回の受け手が仕様の順と一致）、ナビゲーターの内側からの順、ダイアログ（ボタンの並び・既定の OK・幕・戻る・結果 1 回・行の見積もり）、
  シート（段・間隔・位置 → 段・幕・安全領域の余白）、ドラッグで閉じる判定、トースト（3 つまで・待ち・時間切れ・消えたら次・押さえる・スワイプ）、
  フォーカス（範囲の前後・奪わない・戻る・重ねる範囲）、レイヤーの帯、タブの見た目。
- **単体テスト（Rust）** `cargo test -p SEED --lib -- canvas_layout canvas_layout_item layout_item_visual gesture_scene pick_2d screen_bridge …`:
  割合の平行移動で親に合わせた画面と子孫が動く・大きさは変わらない、単位の平行移動が累積スケールを掛けて割合と足される、値 0 は行列も底上げも従来と同じ、
  底上げが子孫へ足し合わさり兄弟へは伝わらない・飽和、平行移動した画面の中の安全領域の箱が縮み直さない、保存しない（書き出さず読むと 0）、スクリプトの読み書き
  （有限の 2 要素・整数の底上げだけ）、`Screen.DpScale` の FFI。既存のレイアウト・スクロール・ジェスチャー・pick_2d・切り抜き・描画の順のテストもそのまま通る。
- **見本**（PC の Play〈540×1200・on_demand〉を IPC の入力の注入で操作。1 回の起動で 49 枚の撮影）: タブの切り替えと状態の保持・もう一度押して根へ、
  押し込みの途中（2 画面が並ぶ）、未保存の詳細で Esc → 確認のダイアログ → Esc で閉じてとどまる → 「戻る」で下ろす → Esc → 根の Esc で
  `back: move_task_to_back` と `模擬: 閉じずに背面へ`、タブ 1 の根の Esc で最初のタブへ、覆う画面（途中・Esc で戻る）、フェード（幕の途中・画面の「戻る」ボタン）、
  ダイアログの幕のタップ（後ろのボタンは押されない → Dismissed）・「上げる」→ Positive・Esc → Dismissed・閉じないダイアログは Esc も幕も効かない、
  シートの行のタップ（結果 row0）・つまみを下へ払って閉じる・Esc・全体から一覧を下へ引く → 半分へ下がる（入れ子の受け渡し）・板の空いた所のタップは閉じない、
  覆い（途中・Esc・幕のタップ・板を下へ払う）、覆い → シート → ダイアログを重ねて Esc × 3 がダイアログ → シート → 覆いの順、トースト 4 つ（3 つ + 待ち 1）→
  実時間で 3 つが消えて 4 つ目 → 横へ払って消える、設定のホイールに触れて ↓ で 30 → 31・ダイアログの間は ↓ が届かない・閉じたら ↓ で 32・タブ 0 では届かない。
  動きの後は毎回 10 フレームで `[SEED REDRAW] 描画を止めます`。`SEED_SIM_SAFE_AREA=0,72,0,48`・`SEED_SIM_SCALE_FACTOR=1.5` でヘッダー・タブのバー・
  全体に積む画面の Body・シート・覆い・トーストが安全領域を避け、背景は端まで。遮り: 詳細を積んだ後に下のホームのボタンの位置を押しても・押し込みの途中に
  下の画面のボタンを押しても・ダイアログの間に後ろのタブのバーを押しても、何も起きない。
- **回帰**（WarashibeFishing の複製。変更前の SEED.exe と SEEDScripting.dll〈HEAD〉で撮った基準と比べた）: 図鑑の画面 3 フレームとも差 0 画素、図鑑のボタンの縁の
  56 点のクリックは当たり 34・外れ 22 で、各クリックの後の画面まで一致。最終のビルドの 1 回目の図鑑の撮影（2 回とも）だけ Next の矢印 1 つ（5,831 画素）が
  明るかった。図鑑の `ZukanArrow` のホバー（`OnPointerEnter` で 1.35 倍）の見た目と一致し、同じビルドで撮り直した 2 回（カーソルの位置を記録。窓の外）は差 0
  だったので、撮影の間に利用者の OS のマウスカーソルが窓のその位置にあったためと見ている（推論。その 2 回のカーソルの位置は記録していない）。
- **W2 の手直し 3b（2026-09-29・PC の単体テストだけ）**: `cargo test --lib canvas_layout`（見た目の倍率: 既定の木の表が倍率を足す前のコードとビット単位で同じ〈golden の指紋〉、
  倍率 0.9 で矩形の中心が動かない・子と入れ子のキャンバスの子が中心へ寄る・描画と当たり判定の 4 隅・切り抜き・LayoutRect がそろう・回転と pivot と Scale のノード・
  中身の大きさで測られるコンテナと伸ばされたコンテナの並び・安全領域が縮み直さない）、`UiComponentsTests`（128 通りの問いの一致・問いの無い層・知らせの頻度・
  プレビューの移り変わり〈始まり・進み・取り消し・確定の 2 つの順・遅れた知らせ・Android 13・ボタンの戻る・時間切れ・閉じなかった確定・番号の食い違い・数え直し・
  諦め・読み直し〉・姿勢の計算・テーマの値）。Play と実機は未確認（§12）。
- **W2 の手直し P2-1（2026-09-29・PC）**: `UiComponentsTests` 101/101（ダイアログの 9 件: 札の高さと区画の下の間隔・トークンの値を変えたとき・間隔のトークン・
  本文の見積もり〈1 行の本文・全角の長文の折り返しの位置・改行・空文字〉・禁則・英文の語と強制分割と数字まじりと幅・ぎりぎりの行・出入りの倍率の合成・
  プレハブの既定の値）。見積もりは、fontTools で書体の本当の送り幅を読んでエンジンの `text_wrap.rs` を真似た Python の計算と 26 の文字列で行が一致した。
  Play（540×1200・倍率 1）の撮影を画素で測った: 題 ＋ 本文 1 行 ＋ 3 ボタン（「寝坊で失う…」）の札は 312.00 × 178.43（計算 178.4）、上の余白（札の上端 →
  題の字面の上端）31.25（計算 31.30 = 24 ＋ 行送りの余白の半分 4 ＋ 字面の上の空き 3.30）、下の余白（ボタンの下端 → 札の下端）24.06（以前は 12.6 と 7.2）。
  題なし 134.4・本文なし 140・本文 3 行 223.2 も計算との差 0.9 px 以内。閉じる途中（`motion.dialog` を 3 秒・直線にした作業フォルダだけのテーマ）の 23 枚で、
  札の中心は (270, 600) から 0.05 px 以内、題の字面の左上と OK の文字の右下は「札の中心の周りに倍率を掛けた位置」から 1.6 px 以内（倍率 0.900 の枚で、
  以前の「札の左上へ寄る」予測とは 15.6 / 8.9 px 違う）。予測型の戻るの模擬（進み 1.0 = 0.9 倍）→ Esc の確定では、縮めた 0.90 から跳ばずに 0.86 まで縮みながら閉じた。
  グラフの吹き出しは文字の送り幅 ＋ 余白 8 × 2 の幅になり（例「9/11 06:49」62 px。以前の見積もり〈半角 0.55 em〉では 82 px）、字面は札の中に収まる。

## 12. 実機での確かめ方（Pixel 6a。W2-7 の時点で未実施）

利用者と一緒に行う（戻るキー・ジェスチャーの手触りは実機でしか分からない）。見本のプロジェクト（§10 を `assets/ui` へ取り込み、`start_scene` を
`assets://ui/scenes/ui_navigation.scene`、`render_policy: on_demand`、`system_bars` を表示）を SeedAndroid の `run` で入れ、`[UI] …` を logcat で見る。

1. 戻るボタン（3 ボタンのナビゲーション）と戻るジェスチャー（画面の端からのスワイプ）の両方で、ダイアログ → シート → 覆い → 画面のスタック → 最初のタブ → 背面へ、
   の順に 1 回ずつ効く（UC-6）。根で戻ると `[UI] back: move_task_to_back` の後にアプリが背面へ回り（ホーム画面が出る）、ランチャーから開き直すと同じ画面のまま
2. キーボードが出ているとき（W2-6 の入力欄ができたら）の 1 回目の戻るはキーボードが閉じるだけで、2 回目から段に届く
3. 押し込み・覆う・フェード・シート・覆いの動きが 60 fps で滑らか、止まった後 10 フレームで `[SEED REDRAW] 描画を止めます`
4. ステータスバー・ジェスチャーバーを表示した状態で、ヘッダー・タブのバー・シートの板・覆いの板・トーストがバーに重ならない（縦固定と 4 方向の回転。UC-7）
5. シートのつまみ・板を指で下へ払って閉じる・半分と全部の段・一覧の先頭で下へ引くとシートが下がる、覆いの板を下へ払って閉じる、トーストを横へ払う
6. 画面を積んだまま背面へ回して戻る・回転する（状態と位置が保たれる）

**2026-09-28 の実機の回（roadmap §3.9）**: **この見本は APK にすると真っ黒**（`Instantiate 失敗 (assets://ui/prefabs/screen_frame.actor)`。`ScreenStack` の既定の枠の
プレハブが pak に入らない。backlog）。試験ではスクリプトにパスを書いて収録させ、一覧のタブのスクロールは release の .so で 59.7 fps（CPU 7.3 ms）。
根での戻る（ギャラリーで利用者が行った）は `move_task_to_back` → 背面 → 開き直して同じ pid のまま HOT で戻った。段ごとの戻る（1〜6）は未実施。

**2026-09-29 の手触りの確認（roadmap §3.9.2）**: 戻るジェスチャーでダイアログ → シート → 押し込んだ画面 → 根（背面へ・最近のタスクから同じ pid で HOT に復帰）が期待どおり（1。
3 ボタンのナビゲーション・キーボード・回転は行っていない）。予測型の戻るのアニメーションは出ない（マニフェストの `enableOnBackInvokedCallback="false"`）。ダイアログは
上下の余白が 7.6 dp（札が 160 dp のまま中身に伸びない）、出入りで文字とボタンだけ札の左上を中心に縮む（撮影で確認。どちらも backlog）。

**予測型の戻る（W2 の手直し 3b。2026-09-29 に Pixel 6a・Android 17 で 1〜4 を確かめた。結果は [app_platform_roadmap.md](app_platform_roadmap.md) §3.9.3）**: 見本のプロジェクトのプロジェクト設定に `android.predictive_back: true` を足した APK を Pixel 6a へ入れ、
logcat を `[UI] back` と 3a の Java のログ（[android.md](android.md) §25.18）で見る。PC では Play 中のスクリプトから
`PlatformDiagnostics.SimulateBackGesture(BackGesturePhase.Started, 0, BackEdge.Left)` → `Progressed`（0.2・0.5・1.0）→ Esc キー（確定）か `Cancelled` を流し、
`[UI] back-preview:` のログと画面（上の画面・札・板が縮む）で同じ流れを確かめられる（模擬は Escape を注入しないので確定は Esc キー。`Invoked` だけ流すと 0.5 秒で戻る）。

1. 起動直後に `[UI] back: callback on=… predictive=true` が 1 回（毎フレームは出ない）。見本の画面（`NavSampleScreen`）は `OnBackPressed` と同じ条件で
   `WouldConsumeBack()`（副作用なしの問い。未保存の詳細の画面だけ true）も上書きしているので、根では `on=false`（受ける層なし）になり、根の戻るはシステムへ渡る（4）。
   `OnBackPressed` だけを上書きして `WouldConsumeBack` を上書きしない画面は「受けるかもしれない」とみなして `on=true` のまま（根の戻るは従来どおり Escape → `move_task_to_back`）
2. 積んだ画面で端からゆっくりスワイプ: 上の画面が指に付いて 90% まで縮み、指の向きへ少しずれ、後ろに下の画面が見える。指を戻すと元へ戻る（`cancel`）。
   離すと縮んだ姿勢から右へ下ろされ、下の画面は動かない（`commit … → navigation`）。未保存の詳細（偶数段）は離すと確認のダイアログが出て、縮んだ画面は元へ戻る
3. ダイアログ・シート（下の辺を留める）・覆い（上の辺を留める）で同じ。閉じないダイアログ（`CancelableByBack = false`）は縮まない
4. 根でスワイプ（見本は `WouldConsumeBack` を上書き済み）: `callback on=false` の後、アプリ全体が縮んで後ろにホームが見え、離すとホームへ戻る見た目で背面へ回る（Android 16 以上。
   13〜15 はランチャーから起動したときだけ。それ以外は従来どおり Escape → `move_task_to_back`）。開き直すと同じ画面のまま
5. 3 ボタンのナビゲーションの戻る: 見た目は変わらず、従来どおり 1 回ずつ段が効く（Android 14 以上はキーの down で started・up で invoked が届く）
6. すばやく 2 回スワイプ・スワイプの途中で画面が切り替わる（トーストの時間切れなど）・回転: 縮んだまま取り残されない（`abandon`）

## 13. 制限と持ち越し

- **部分木の透明度が無い**: フェードは「幕（背景の色）を通して」入れ替える。ダイアログの札・トーストは透明度で出入りしない（札は大きさ、トーストは位置）。
  `CanvasGroup` のような部分木の濃さは backlog
- **`SEED.Draw` の図形に底上げが効かない**: 画面の中で `SEED.Draw` を使うと、上に積んだ画面より手前に描かれることがある（画面の中は Sprite・Text で作る）
- **画面の中の表示のレイヤーは段の値（10,000・タブの中は 1,000）より小さく**: 大きなレイヤーの表示を持つ画面を積むと前後が崩れる
- **ダイアログの本文の高さは見積もり**（W2 の手直し P2-1 でエンジンの折り返しの規則と組み込みの書体の送り幅の表に合わせた。§3.2）。`Text.Measure`（W2-6c）が
  できたら測った値へ替える。テーマの `font.family` でほかの書体を当てると送り幅が違うので合わない。本文の中の埋め込みの記法（`[icon:…]` など）は文字のまま数える
  （大きめになる）。本文が長くても札はスクロールしない（札が画面より高くなる）・ボタンが中の幅に入りきらなくても Material 3 のように縦に積まない。
  開いている間にテーマを替えても札の大きさ・間隔は作り直さない（色・角丸・書体は追従）
- **シートの「先に広げる」が無い**: 半分の段で中身の一覧を上へ引くと一覧が先に動く（Android の BottomSheetBehavior・Flutter の DraggableScrollableSheet はシートが先）。
  W2-3 の入れ子は「内側が先・端の残りを外側へ」だけ。開く・閉じる曲線は ScrollTo の easeInOut 固定
- **上からの覆いの作りは最小**: Flutter 版の「固定の頭＋スクロールする中身＋一覧の外の閉じる」・左右の余白・四隅の角丸は W3 のプレハブで作る
- **予測型の戻る（3b。§5.1）の残る制限**: 問えない層（`IBackConsumer`・`OnBackPressed` の上書き・問いの無いスクリプトの層）は受けるとみなす（根でも
  システムに任せず Escape が届く）。フォーカス（入力欄）・タブの切り替え・スクリプトの層は縮めない（相手なし）。Android 13 は進み具合が届かないのでプレビューなし
  （invoked と Escape だけ）。受ける層の有無の知らせは UI スレッドを通るので約 1 フレーム遅れ、その間の戻るはシステムへ行く（3a）。手ぶりの途中に受ける層が無くなった
  ときの Android の振る舞い（cancelled が来る見込み）は未確認。回転したノードの縦横で違う倍率は、子孫の安全領域の写し方が近似。倍率・ずらし・曲線の既定値は記憶による
  （§9）。実機では未確認
- **PC の Esc**: エディタの Play（埋め込み）では Esc がエディタの操作と重なる可能性（未確認。単体の SEED.exe で確かめた）
- **W2-6（入力欄）へ**: 入力欄は `IFocusable`（範囲に属す）＋ `IBackConsumer`（Android では IME が先に閉じる。PC は戻るでフォーカスを外す）で乗る。
  フォーカスした入力欄を画面の外へ出さない（キーボードを避ける）スクロールは W2-6b
  → **2026-09-30 の W2-6b で済**（docs/ui_text_input.md §8・§10）: `SEED.UI.TextField` は `IFocusable`・`IBackConsumer`（キーボードが出ていれば〈PC は常に〉フォーカスを外して受け、
  Android でキーボードを閉じた後の戻るは外して後ろの層へ回す）。ダイアログは `DialogOptions.Input`（1 行の入力つき。`DialogHandle.InputText`）と、キーボードが札に
  重なったときの持ち上げ（`IKeyboardInsetTarget`）を持つ。札の縦の割り付けは `DialogMetrics.Sections` の入力の区画（`InputSection`）
- **W2-8 以降へ**: グラフ（横スクロール・ピンチ）はタブの中の画面に置く。テーマの実行中の切り替え（W2-9）で、開いている面・トーストも次のフレームで作り直す（今も
  `UiTheme.Version` で追従）
- **実機（Pixel 6a）で未確認**（§12）。エディタに埋め込んだ Play での動きも未確認
