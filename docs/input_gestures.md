# ジェスチャー（入力の正典。W2-2・2026-09-28）

キャンバス UI の指の操作（タップ・長押し・ドラッグ・フリック・押下の見た目の取り消し・指ごとの捕捉）を、
**ジェスチャーアリーナ**で 1 か所で決める仕組みの規則。部品（W2-3 以降のスクロール・一覧・ボタン・スライダ・シート）は
これに乗って振る舞いを作る。

| 置き場 | 役割 |
|---|---|
| `runtime/src/engine/core/input/gesture/` | アリーナと認識器（World・FFI に触れない純ロジック。各ファイルの冒頭に規則） |
| `runtime/src/engine/components/canvas_gesture_component.rs` | ジェスチャーを受けるノード（`CanvasGestureComponent`。データだけ） |
| `runtime/src/engine/core/app_base/app/gesture_scene.rs` | レイアウトの表（`canvas_layout`）から当たり判定の材料を作る |
| `runtime/src/engine/core/app_base/app/gesture_events.rs` | フレームの処理とスクリプトへの配達・ポインタのイベントとの関係 |
| `runtime/src/engine/core/input/mod.rs`・`touch/bridge.rs`・`inject/` | 時刻つきの指のイベントの記録（実タッチ・マウスの合成の指・注入の指） |
| `scripting/src/Api/GestureEvent.cs`・`CanvasGesture.cs`・`SEEDScript.cs` | スクリプトの API（`SEED.GestureEvent`・`SEED.CanvasGesture`・`OnGesture*`） |

背景と設計の柱は `docs/app_platform_roadmap.md` §3.2 の W2-P1（ジェスチャーは Rust）と W2-P3（ジェスチャーの調停）。

---

## 1. 使い方

2D キャンバスのノードに **Canvas Gesture**（インスペクタ「コンポーネント追加 → UI → Canvas Gesture」）を付け、同じアクターの
スクリプトで `OnGesture*` を受ける。付けていないノードはアリーナに参加しない（従来の `OnPointer*` は §7 のとおり）。

| 欄（serde の名前） | 既定 | 意味 |
|---|---|---|
| 受ける（`enabled`） | true | false の間は当たり判定の候補にもならない（後ろのノードへ指が届く） |
| タップ（`tap`） | true | 押して・動かず・離す |
| 長押し（`long_press`） | false | 一定時間（500ms）動かずに押し続ける |
| ドラッグ（`drag`） | false | slop（8 dp）を超えて動かす。DragStart / DragUpdate / DragEnd |
| フリック（`fling`） | false | 離した時点の速度が 50 dp/秒 以上。ドラッグを受けなくても、指を取るためのドラッグの認識器が参加する |
| ドラッグの軸（`drag_axis`） | any | `any`（全方向）/ `horizontal`（横だけ）/ `vertical`（縦だけ）。ドラッグ・フリックの判定と、移動量・速度の射影 |
| 押下の見た目（`press_feedback`） | true | PressDown / PressCancel / PressUp を受ける（タップ・長押しを受けるノードだけ） |
| 最小のヒット領域（`min_hit_size_dp`） | 48 | 見た目がこれより小さいと、縦横それぞれ中心をそろえて広げる（dp）。0 = 広げない |

旗をすべて外したノードは「遮る板」になる（後ろのノードへ指を渡さない。ダイアログの板・覆い）。

```csharp
// ボタン（CanvasGesture の既定＝タップと押下の見た目）
public class MyButton : SEEDScript
{
    public override void OnGesturePressDown(SEED.GestureEvent e)   { /* 押した色 */ }
    public override void OnGesturePressCancel(SEED.GestureEvent e) { /* 元の色（スクロールに負けた・外へ出た） */ }
    public override void OnGesturePressUp(SEED.GestureEvent e)     { /* 元の色 */ }
    public override void OnGestureTap(SEED.GestureEvent e)         { /* 押された */ }
}

// 縦スクロールの領域（CanvasGesture: tap=false・drag=true・fling=true・drag_axis=vertical。子に CanvasClip の枠）
public class MyScroll : SEEDScript
{
    public override void OnGestureDragUpdate(SEED.GestureEvent e) { /* 中身を e.Delta.y だけ動かす */ }
    public override void OnGestureFling(SEED.GestureEvent e)      { /* e.Velocity.y から慣性（W2-3） */ }
}
```

## 2. アリーナの規則

### 2.1 参加者と並び

指（ポインタ）が触れたら、その位置の当たり判定の**経路**（§3。葉 → 根）の各ノードの認識器を「参加者」として並べる。
並びは経路の順（**葉が先**）、同じノードの中は**タップ → 長押し → ドラッグ**。この並びが「同じときに勝ちを申し出たら先が勝つ」の順になる。

### 2.2 勝ち負け（Flutter の GestureArena と同じ考え方）

| # | 規則 |
|---|---|
| 1 | 参加者が**勝ちを申し出たら**、まだ勝者がいなければその参加者が勝ち、残りはすべて負ける。ドラッグ: 軸に沿った移動が slop を超えた（§6）。長押し: 押してから 500ms。タップ: 離したとき |
| 2 | 参加者は**自分で降りる**。タップ・長押し: 押した位置からタップの許容移動（18 dp）を超えて動いた・ノードの押下の領域（最小のヒット領域 + slop 8 dp）の外へ出た・ノードが消えた。長押し・ドラッグ: 勝つ前に離した。タップ: 時間の上限（既定なし）を超えて離した |
| 3 | 離したときに勝者がいなければ、並びで最初の（まだ競っている）**タップが勝つ**（子のタップが親より先） |
| 4 | 同じ移動で複数のドラッグが申し出たら並びで先のもの（**斜めちょうどは子**）。横だけのドラッグは縦の動きでは申し出ないので、縦の一覧の中の横スクロールは**最初の動きの向き**で持ち主が決まる（W2-P3・UC-3） |
| 5 | 長押しとタップが同じ指にいれば、長押しの時間を過ぎた時点で長押しが勝つ。その前に離せばタップ（境目は**イベントの時刻**で決まる。§5） |

### 2.3 捕捉

勝った参加者はその指を**捕捉**する。以後のその指の移動・離しはすべて勝ったノードへ届く（**ノードの外へ出ても**届く）。
ドラッグなら DragUpdate が続き、離して DragEnd（とフリック）。長押しの後に動いてもドラッグにはならない。

### 2.4 複数指

| 規則 | 内容 |
|---|---|
| 指ごとに別のアリーナ | 2 本の指が別々のノードを同時にタップできる（指の番号は 0 起点で空いている最小。`Touch.FingerId` とは別） |
| 複数指になったら取り消し | 別の指が押している（タップ・長押しの競い中、または長押しの後に押している）ノードに次の指が触れたら、前の指のそのノードのタップ・長押しを降ろし（押していれば PressCancel）、次の指のアリーナにもそのノードのタップ・長押しを入れない |
| 1 ノードのドラッグは 1 本まで | 別の指がドラッグで捕捉しているノード**とその子孫**には、次の指は参加しない（スクロール中の一覧の中のボタンを別の指で押しても反応しない。Flutter でスクロール中の一覧が 2 本目の指を取るのと同じ結果。捕捉しているノードの外のボタンは押せる。ピンチは今回は無い。W2-8 のグラフのズームで足す） |

### 2.5 取り消し

| 何が起きたか | アリーナ | 届くイベント |
|---|---|---|
| 指がノードの外へ出た（押下の領域 + slop の外） | そのノードのタップ・長押しが降りる | PressCancel（押していれば） |
| スクロール（ドラッグ）に負けた | 勝ったドラッグ以外はすべて負け | PressCancel → 勝ったノードの DragStart の順 |
| 複数指になった | §2.4 | PressCancel |
| OS の取り消し（着信・システムのジェスチャーへの横取り） | その指のアリーナを取り消す | PressCancel、ドラッグ中なら DragEnd（`Canceled = true`・速度 0・フリックなし） |
| フォーカスを失った・アプリが背面へ回った | すべての指を取り消す（前面へ戻った最初のフレームで配る） | 同上 |
| Play の一時停止 | 一時停止した最初のフレームですべて取り消す（再開した最初のフレームで配る。一時停止の間の指は捨てる） | 同上 |
| Play の開始・終了・シーンの切り替え | すべて捨てる（イベントは出さない。旧シーンのノードは破棄済み） | なし |

## 3. 当たり判定（最小のヒット領域・近い方・遮り・切り抜き）

形は**見た目の矩形**: CanvasComponent があればキャンバス領域、無ければ最初の有効な Sprite の矩形（切り抜きの矩形と同じ選び方。
レイアウトが伸ばした軸は矩形の大きさ）。どちらも無いノードは参加しない。レイアウトは Play のポインタイベントと同じ文脈の
`canvas_layout` の表（dp・安全領域・コンテナ・切り抜きを含む）。回転・拡大したノードはローカルの矩形で判定する。

| # | 規則 |
|---|---|
| R1 候補 | 見た目の矩形を**最小のヒット領域**（既定 48 dp。縦横それぞれ、中心をそろえて広げる）まで広げた矩形が点を含み、**祖先のすべての切り抜きの内側**にあるノード（切り抜きの外で押した指は参加しない）。非アクティブ・非表示・無効なノードは除く |
| R2 葉 | 候補のうち、他の候補の祖先でないもの（祖先は後で経路に入る。子の広げた領域は親の見た目の中でも子が受ける） |
| R3 遮り | 見た目の矩形が点を含む候補 y が、葉 x より**手前**に描かれ（描画ゾーン → 最初の Sprite のレイヤー → 木の順）、x の祖先でなければ x は外れる（覆いの板・ダイアログの後ろへは届かない） |
| R4 近い方 | 残った葉のうち、点から**見た目の矩形までの距離**（中なら 0）が最小のもの。同じなら手前のもの。広げた領域が重なる小さなボタンの間では近い方が受ける |
| 経路 | 選んだ葉と、その祖先のうち候補であるもの（近い順） |

例: 20×20 のボタン 2 つを 10 画素あけて並べると、広げた 48×48 が重なる。間の点は近い方のボタンへ、ボタンの中の点はそのボタンへ。
ジェスチャーを受けない Sprite は遮らない（遮るときは受けるジェスチャーの無い CanvasGesture を付ける）。

## 4. 押下の見た目（PressDown / PressCancel / PressUp）

ノードごとに決める（認識器ごとではない）。

| イベント | 出るとき |
|---|---|
| PressDown | **押下の候補**（まだ競っているタップ・長押しのうち並びで最初のもののノード＝子が先。親は光らない）が「押下の見た目」を受けるとき、競いにドラッグがいなければ**すぐ**（単独のボタン）、いれば**押下の待ち（100ms）が過ぎたら**（スクロールの中のボタン。Android の TAP_TIMEOUT と同じ）。候補のタップ・長押しが勝ったときも、まだならその場で出す（速いタップでも PressDown → PressUp → Tap の順に届く） |
| PressUp | 押したノードのタップが勝った（離した）・長押しの後に離した。タップは **PressUp → Tap** の順 |
| PressCancel | 押したノードのタップ・長押しがすべて負けた（§2.5 の取り消し） |

長押しは **PressDown → LongPress → （離して）PressUp**。長押しを受ける親の中のボタンを押し続けると、ボタンの PressCancel の後に
親の PressDown → LongPress になる。

## 5. 時刻

- 判定はすべて**入力イベントの時刻**（`gesture/pointer_log.rs` の時計の秒）で行う。フレームの中では、記録を時刻の順に 1 件ずつ処理し、
  各イベントの前にその時刻までに期限の来た出来事（長押し・押下の待ち）を期限の順に起こす。フレームが遅れても、離した時刻が
  長押しの期限より前ならタップ（単体テスト `tap_long_press_boundary_uses_event_time`・`results_do_not_depend_on_frame_boundaries`）。
- **PC**: winit のイベントを受け取った時刻（イベントハンドラの `Instant::now()`）。
- **Android**: winit 0.30.13 は MotionEvent の時刻（eventTime）を `WindowEvent::Touch` に渡さない（`platform_impl/android/mod.rs` の
  `handle_input_event` を読んだ）ので、**受け取った時刻で代用**する。Android は移動を vsync ごとにまとめて届け、winit は履歴の標本
  （`MotionEvent` の historical）も捨てるので、速度の推定は 1 フレームに 1 標本・受け取りの揺れ（数 ms）を含む（docs/backlog.md）。
- **注入（エディタ・MCP の INPUT_SEQUENCE）**: 各操作の**予定の時刻**（シーケンスの `t`）から逆算した時刻。フレームの刻みに依らないので、
  時刻の間隔を指定した検査ができる（単発の INPUT_MOUSE_* は当てた時刻）。

## 6. 閾値の表

名前付きの定数（`gesture/thresholds.rs`）で、`project_settings.json` の `"gestures"` で上書きできる（書いた欄だけ。壊れた値は既定へ戻す。
起動時に読み、上書きがあれば `[SEED INIT] gestures` をログへ出す）。距離は dp（`CanvasScreenEnv::dp_scale` で画素へ。PC の 100% は 1、
Pixel 6a は 2.625。`SEED_SIM_SCALE_FACTOR` で模擬）、時間はミリ秒、速度は dp/秒。

| 鍵 | 既定 | 意味 | 出典（2026-09-28 に取得して確認） |
|---|---|---|---|
| `touch_slop_dp` | 8 | ドラッグが始まる移動 | Android `ViewConfiguration.TOUCH_SLOP` = 8。W2-2 の要件 |
| `tap_slop_dp` | 18 | タップ・長押しの許容移動 | Flutter `kTouchSlop` = 18.0（タップの preAcceptSlopTolerance）。ドラッグより大きいので、ドラッグのいる所では先にドラッグが勝つ |
| `long_press_ms` | 500 | 長押しの時間 | roadmap W2-P3 = Flutter `kLongPressTimeout`。Android 12 以降の既定は 400 |
| `press_delay_ms` | 100 | 押下の見た目の待ち（ドラッグが競いにいるとき） | Android `TAP_TIMEOUT` = 100 = Flutter `kPressTimeout` |
| `tap_max_ms` | 0 | タップの時間の上限（0 = なし） | Android の View のクリック・Flutter の onTap に上限は無い |
| `min_fling_velocity_dp` | 50 | フリックの最小の速度（dp/秒） | Android `MINIMUM_FLING_VELOCITY` = 50 = Flutter `kMinFlingVelocity` |
| `max_fling_velocity_dp` | 8000 | 速度の上限（向きを保って切り詰める） | Android `MAXIMUM_FLING_VELOCITY` = 8000 = Flutter `kMaxFlingVelocity` |
| `velocity_horizon_ms` | 100 | 速度の推定の時間の窓 | Flutter `VelocityTracker._horizonMilliseconds` = 100 |
| `velocity_max_samples` | 20 | 速度の推定の標本の数 | Flutter `_historySize` = 20 |
| `velocity_stop_ms` | 40 | 止まったとみなす標本の間隔（離す前に止まっていればフリックにしない） | Flutter `_assumePointerMoveStoppedMilliseconds` = 40 |
| `velocity_degree` | 2 | 最小二乗の次数（標本が少なければ下げる） | Flutter `LeastSquaresSolver.solve(2)`（Android の LSQ2 も同じ考え方。記憶による） |
| （コンポーネント）`min_hit_size_dp` | 48 | 最小のヒット領域 | Material Design・Android のアクセシビリティの最小の押せる大きさ |

```json
{ "gestures": { "long_press_ms": 400, "tap_slop_dp": 12 } }
```

Flutter と違う所: 全方向のドラッグの slop も 8 dp（Flutter の Pan は 2 倍の 36）。タップの許容移動の外へ出たタップは戻っても成り立たない。

## 7. 既存のポインタのイベント（OnPointer*）との関係

| ノード | 振る舞い |
|---|---|
| CanvasGesture を**付けていない** | アリーナに参加しない。`OnPointerEnter/Exit/Down/Up/Click`（`raycast_target` の Sprite。指0 だけ・最前面の 1 つ）は**従来のまま**（`pointer_events.rs` の判定と状態遷移はそのまま。シーンに CanvasGesture が 1 つも無ければ、ジェスチャーの処理は指の記録を取り出して捨てるだけで、レイアウトの表も作らない） |
| **両方ある**（有効な CanvasGesture と `raycast_target` の Sprite） | **押す・離す・クリックはジェスチャーが受け持つ**: `OnPointerDown / Up / Click` は届かない（PressDown / PressUp / Tap で受ける。スクロールに負けた押下でクリックが起きる取り違えを防ぐ）。`OnPointerEnter / Exit`（カーソルが乗った・外れた）は従来どおり届く |

WarashibeFishing は CanvasGesture を使っていないので影響が無い（2026-09-28 に複製で図鑑のボタンの縁 56 点のクリックが変更の前後で一致することを確かめた）。
ポインタのイベントの判定（最前面の raycast_target）とジェスチャーの当たり判定（§3）は別々で、互いに遮らない。

## 8. イベントの中身（`SEED.GestureEvent`）

| 欄 | 内容 |
|---|---|
| `Kind` | 種類（Tap / LongPress / DragStart / DragUpdate / DragEnd / Fling / PressDown / PressCancel / PressUp） |
| `PointerId` | 指の番号（0 起点） |
| `Position` | 今の位置（キャンバスの画素。画面の中央が原点・Y 下向き。`Input.MousePositionCanvas` と同じ） |
| `ScreenPosition` | 今の位置（画面の画素・左上原点。`Input.MousePos`・`Touch.Position` と同じ） |
| `LocalPosition` | ノードの見た目の矩形の左上が原点・ノードの単位（dp のキャンバスなら dp）。スライダの位置に使う |
| `StartPosition` | 押した位置（キャンバスの画素） |
| `Delta` / `DeltaDp` | DragStart: 押した位置からの移動（slop の分）。DragUpdate: 前のドラッグのイベントからの移動。軸のドラッグは軸へ射影 |
| `Velocity` / `VelocityDp` | 直近の標本から推定した速度（上限で切り詰め。軸のドラッグは射影）。DragUpdate / DragEnd / Fling |
| `TotalDelta` | 押した位置からの移動（射影しない） |
| `Duration` | 押してからの時間（秒。イベントの時刻） |
| `Canceled` | DragEnd が取り消しで来た（速度 0） |

ドラッグの途中（DragUpdate）は 1 フレームに指 1 本・ノード 1 つにつき 1 件へまとめる（移動量は足し合わせ、位置・速度・時間は最新）。
配るのはスクリプトフェーズ（`Update` 等）より前（同じフレームの `Update` から結果が見える）。コールバックの中から `Input`・`Physics` が使える。

## 9. 「動いている」の申告（W2-10a の口）

`GestureArenaSet::activity()`（App からは `GestureState::activity()`）が、アリーナに参加している指の数・ドラッグ中の指の数・
次に時刻で起こる出来事（長押し・押下の待ち）の時刻を返す。指が触れている間は描き続け、無ければ止まってよい。
次の出来事の時刻は `WaitUntil` に使う。**2026-09-28 の W2-10a でつないだ**: `app/redraw_hooks.rs` がフレームの末尾に読み、
指があれば描く理由 `gesture`、次の出来事の時刻（pointer_log の時計の秒）を Instant へ直して止めている間の予定にする
（[redraw_policy.md](redraw_policy.md) §3。PC で長押しの期限 483 ms 後を予定にして描き続けたのを確かめた）。

## 10. 検証（2026-09-28）

- 単体テスト（合成の指の列。`cargo test -p SEED --lib -- gesture`）: タップと長押しの境目（イベントの時刻・フレームの遅れ）、slop の内外（dp の倍率 1・2・3）、
  ドラッグの軸（横だけのドラッグが縦の移動で負けて親の縦スクロールが勝つ・斜めちょうど）、入れ子（子のタップと親のドラッグ・入れ子のボタン）、
  フリックの速度（標本の数・時間の窓・止まってから離す・最小の速度）、取り消し（外へ出る・2 本目の指・OS の取り消し・全部の取り消し）、
  捕捉（外へ出ても届く・1 ノードのドラッグは 1 本・スクロール中の一覧の中の 2 本目の指）、マルチタッチ、切り抜きの外、48 dp の最小ヒット領域と重なり、
  遮る板、フレームの区切りに依らない結果、`Input` の時刻つきの記録（マウスの合成の指・Android の指・注入の座標とシーケンスの予定の時刻）
- PC（`SEED.exe` を注入で操作。作業フォルダの UiSpike の写しの UiGesture.scene）: 17 の検査でスクリプトへ期待どおり届くこと（倍率 2 は 4 の検査）
- 実機（Pixel 6a）: **未実施**（W2-6a・W2-10a の頭で、タップ・スクロールの中のボタン・フリックの速度を指で確かめる）

## 11. 制限と持ち越し

- **W2-3（スクロールと一覧）**: → **2026-09-28 に済**（[ui_scroll_list.md](ui_scroll_list.md)）。スクロールの物理は予定を変えて **Rust**（`CanvasScrollComponent`。窓は CanvasGesture 無しで向きのドラッグとフリックでアリーナに参加する）。行の再利用で押している行が消える問題は、一覧が使い回す前に `GameObject.CancelGestures`（`GestureArenaSet::cancel_nodes`。行と子孫の押下・ドラッグを取り消し、PressCancel・取り消しの DragEnd を次のフレームに配る）で手当てした。慣性・ScrollTo の途中のタップはエンジンが止め、その指を中の行へ渡さない（`GestureHitScene::with_absorbing`。経路で動いている窓より葉の側を外す）。スクロールのシステムはこのフレームに触れた指の経路（`take_touched`）と今触れている経路（`nodes_under_pointers`）で「触れて止める」を決める
- **W2-10a**: §9 の申告を「描く理由」へつないだ（2026-09-28。済）。止まっている間に届いた指は WindowEvent が起こし、起きた最初のフレームで処理される（記録は捨てない）
- ピンチ（2 本指の拡大）・ダブルタップ・ホバー（マウスの乗る・外れる）はアリーナに無い（W2-8 でピンチ。ホバーは OnPointerEnter / Exit）
- 3D ワールドキャンバスのノードは参加しない（ポインタのイベントと同じ）
- 内部解像度固定（レターボックス）のときも dp は画面の DPI から求めるので、slop・最小のヒット領域は内部解像度の画素では少しずれる（`units.rs` と同じ扱い）
