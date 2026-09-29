# ジェスチャー（入力の正典。W2-2・2026-09-28）

キャンバス UI の指の操作（タップ・長押し・ドラッグ・フリック・押下の見た目の取り消し・指ごとの捕捉）を、
**ジェスチャーアリーナ**で 1 か所で決める仕組みの規則。部品（W2-3 以降のスクロール・一覧・ボタン・スライダ・シート）は
これに乗って振る舞いを作る。

| 置き場 | 役割 |
|---|---|
| `runtime/src/engine/core/input/gesture/` | アリーナと認識器（World・FFI に触れない純ロジック。各ファイルの冒頭に規則）。2 本指のピンチは `pinch.rs`（W2-8。§2.6） |
| `runtime/src/engine/components/canvas_gesture_component.rs` | ジェスチャーを受けるノード（`CanvasGestureComponent`。データだけ） |
| `runtime/src/engine/core/app_base/app/gesture_scene.rs` | レイアウトの表（`canvas_layout`）から当たり判定の材料を作る |
| `runtime/src/engine/core/app_base/app/gesture_events.rs` | フレームの処理とスクリプトへの配達・ポインタのイベントとの関係 |
| `runtime/src/engine/core/input/mod.rs`・`touch/bridge.rs`・`inject/` | 時刻つきの指のイベントの記録（実タッチ・マウスの合成の指・注入の指） |
| `runtime/src/engine/core/input/touch/os_timing/`・`runtime/android/native/src/touch_timeline.rs`・`runtime/android/app/…/input/TouchTimeline.java` | Android の MotionEvent の時刻と履歴の控え（Java で控えて JNI で積み、winit の Touch と突き合わせる。§5） |
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
| ピンチ（`pinch`） | false | 同じノードに触れた 2 本の指の間の距離の変化（PinchStart / PinchUpdate / PinchEnd。W2-8。§2.6）。グラフの拡大縮小 |
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
| 1 ノードのドラッグは 1 本まで | 別の指がドラッグで捕捉しているノード**とその子孫**には、次の指は参加しない（スクロール中の一覧の中のボタンを別の指で押しても反応しない。Flutter でスクロール中の一覧が 2 本目の指を取るのと同じ結果。捕捉しているノードの外のボタンは押せる。始まったピンチのノードも同じ扱い＝3 本目の指は参加しない。ピンチは §2.6） |

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

### 2.6 ピンチ（W2-8。`pinch.rs`）

`pinch` を付けたノードに、2 本の指の間の距離の変化を届ける。指ごとのアリーナとは別に、すべての指を見て決める
（2 本目の指は §2.4 の「1 ノードのドラッグは 1 本まで」でアリーナに入らないことがあるため）。

| # | 規則 |
|---|---|
| 1 | 指が触れたら、経路（葉 → 根）でいちばん葉に近いピンチを受けるノードと、それより根の側のノード（外側）を覚える |
| 2 | 同じノードに 2 本目の指が触れたら（1 本目がまだ触れていて、そのノードの組がまだ無ければ）、その 2 本で候補の組を作る（3 本目以降は組にしない） |
| 3 | 候補の 2 本の指の間の距離が、組ができたときから touch slop（8 dp）を超えて変わったら始める。ただし、どちらかの指が**外側のノードのドラッグ**に取られていれば始めない（縦の一覧をスクロールしている指ではピンチにならない）。始めたら 2 本の指のアリーナを取り消し（PressCancel・取り消しの DragEnd。そのノード自身のドラッグ〈グラフのパン〉も取り上げる）、以後この 2 本の指の動きはピンチだけが受ける |
| 4 | 倍率 `Scale` = 今の指の間の距離 ÷ 始めたときの距離（PinchStart で 1）。`ScaleX`・`ScaleY` は横・縦の幅の比（始めたときの幅が 1 画素以下の向きは 1）。`Position` は 2 本の指の中点、`Delta` は前のピンチのイベントからの中点の移動、`Duration` は始めてから。`PointerId` は 0、速度は 0 |
| 5 | どちらかの指を離す・取り消されると PinchEnd（取り消しなら `Canceled`）。残った指は離すまで何もしない（次の指が同じノードに触れれば、残った指と新しい組を作る） |
| 6 | PinchUpdate は DragUpdate と同じく 1 フレームに 1 件へまとめる（移動量は足し、倍率は最新）。ピンチの間は「動いている」（§9）に 2 本の指を数える |

## 3. 当たり判定（最小のヒット領域・近い方・遮り・切り抜き）

形は**見た目の矩形**（W2-4 から、Sprite の矩形のノードは Sprite の形〈角丸・楕円・弧〉。最小のヒット領域まで形ごと広げ、形のある切り抜きの外は押せない。
[ui_components.md](ui_components.md) §4）: CanvasComponent があればキャンバス領域、無ければ最初の有効な Sprite の矩形（切り抜きの矩形と同じ選び方。
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
- **Android**（2026-09-29 から）: **MotionEvent の時刻（eventTime）と履歴の標本**を使う。winit 0.30.13 は時刻も履歴（`MotionEvent` の
  historical。1 回の vsync の間に溜まった途中の標本）も捨てて `WindowEvent::Touch`（ID・段階・位置だけ）にし（`platform_impl/android/mod.rs` の
  `handle_input_event`）、GameActivity の glue の入力のバッファは読むと消えるので winit の外から二重に読めない。そこで次のように控える。
  1. `MainActivity.processMotionEvent`（GameActivity の glue へ渡す**前**。UI スレッド）が `input/TouchTimeline` で、winit が Touch にするのと
     同じ指・同じ位置の控えを作る: 控える action は DOWN・POINTER_DOWN（Started）・MOVE（Moved）・UP・POINTER_UP（Ended）・CANCEL（Cancelled）。
     glue の入力のフィルタ（`source & 0x1002`）と指の上限 8 も同じ。Started / Ended は action の指 1 本、Moved / Cancelled は添字の順に全部の指。
     位置は GameActivity と同じ `getAxisValue(AXIS_X/Y)`、Moved は履歴（`getHistoricalAxisValue`）も。時刻は CLOCK_MONOTONIC の ns
     （**API 34 以上は `getEventTimeNanos` / `getHistoricalEventTimeNanos` の ns**、未満は ms × 1,000,000）。JNI は MotionEvent 1 つにつき 1 回
  2. native（`touch_timeline.rs`）が受け取った瞬間に CLOCK_MONOTONIC と `Instant` を続けて読み、ns を `Instant` へ換算して
     （同じ時計。未来は今へ丸める）エンジンの箱（`touch/os_timing/`。上限 1024 件）へ積む
  3. `Input::process_touch` が winit の Touch ごとに、箱の先頭から **ID・段階・位置の f32 のビット**が一致する最初の控えを探して取り出す
     （それより前の控えは捨てる＝winit へ届かなかった分）。一致すればその時刻で記録し、Moved なら履歴の標本を、その前に同じ指の Moved として
     ジェスチャーの記録へ積む（**スクリプトの `Input` のタッチの状態と差分は変えない**）。**見つからなければ受け取った時刻**（従来どおり）
  4. 記録の時刻は指ごとに逆行しないよう前の記録の時刻まで切り上げ、全部の取り消し（フォーカスを失った・背面へ）より後に届いた Touch は
     取り消しより前へ並べない（`gesture/time_floor.rs`。控えの時刻と受け取った時刻が混ざっても順が入れ替わらない）
  時計の起点（`pointer_log.rs`）は `App::new` の最初に決める（控えの時刻は受け取るより前なので、最初のタッチで起点が決まると潰れる）。
  確かめ方はログ `[SEED TOUCH TIME]`（§10・[android.md](android.md) §12.4）。
- **注入（エディタ・MCP の INPUT_SEQUENCE）**: 各操作の**予定の時刻**（シーケンスの `t`）から逆算した時刻。フレームの刻みに依らないので、
  時刻の間隔を指定した検査ができる（単発の INPUT_MOUSE_* は当てた時刻）。

### 5.1 速度の推定の頑健化（2026-09-29。`gesture/velocity.rs`・`recognizers/fling.rs`）

離した時点の速度（DragEnd・Fling の `Velocity`）は、直近の標本の最小二乗（窓 100 ms・次数 2。Flutter の VelocityTracker と同じ）の値に次の規則を
当てる。時刻が正しくても効く規則で、閾値は §6 の名前付きの定数（`project_settings.json` の `"gestures"` で上書きでき、0 でその規則を切る）。

| # | 規則 | 既定 | 何を防ぐか |
|---|---|---|---|
| R1 | 離した時刻 − 最新の標本の時刻 > `velocity_stop_ms` なら 0（従来から） | 40 ms | 止めてから離した指（標本が途切れた） |
| R2 | **持ち上げの揺れ**: 離した時刻 T の少し前の区間 [T − `velocity_stop_ms`, T − `velocity_lift_off_ms`]（既定 [T − 40, T − 16.7] ms）の標本を直線で当てはめた速さが、最小のフリックの速度（`min_fling_velocity_dp`。ドラッグの軸へ射影）より遅ければ 0。T − lift_off の時点で最新の標本が stop より古ければ 0（その時点を「今」として R1）。区間の標本が 2 つ未満なら T − lift_off までの新しい 2 標本で当てはめ、T − lift_off までの標本が 1 つ以下なら当てない | 16.7 ms | 離す前に指がほぼ止まっていたのに、最後の 1 フレームの指の腹の転がり（1.5〜4.2 dp）でフリックになる |
| R3 | 推定に使った標本の時間の幅（最新 − 最古）が `velocity_min_span_ms` 未満なら 0（離しに限らず、DragStart・DragUpdate の速度も） | 16.7 ms | 1 フレームより短い幅の標本だけの推定（受け取りのまとまりの中の標本。物理の速度ではない） |
| R4 | 前の標本との間隔が `velocity_min_sample_interval_ms` 未満の標本は、前の標本と入れ替える（新しい方を残す） | 1 ms | µs 差で並んだ標本（受け取りのまとまり）の傾きが膨らむ |

- 0 にした理由（R1〜R3）は離しの結果に残り、Android では `[SEED GESTURE] release id=… v_dp=(vx,vy) raw_dp=(…) rule=ok|stopped|lift_off|short_span lift_probe_dp=…`
  を 1 行出す（`lifecycle_diag_log` のとき。PC では出さない。`raw_dp` は規則を当てない推定、`lift_probe_dp` は R2 の区間の速さ）。
- **R2 を「区間の直線の当てはめ」にした理由**（「T − lift_off の時点までの標本だけで、同じ推定の規則〈窓 100 ms・次数 2・R3〉で推定する」案を退けた）:
  同じ推定の規則だと、窓が止める前の払いまで届き、2 次の当てはめの端の傾きが逆向きに振れて「止まっていた」を見落とす（同じ手順の試算で、
  払ってから 40〜120 ms 止めて離した 160 通りのうち 0 にできたのは 60 通り）。また R3 で決められない（標本が少ない）ときに 0 とみなすと、
  触れて 20 ms で離した速い払い（既存の試験 `fling_only_node`）が消える。区間を「R1 と同じ 40 ms の中から最後の 1 フレームを除いた所」に絞って
  直線で当てはめると 160 通りすべてを 0 にし、本物のフリック（等速・加速・減速）は消さない。区間の標本が 2 つ未満のときの補い（T − lift_off までの
  新しい 2 標本）は、実機の記録の 3 回目（区間の前に 50 ms 標本が無く、区間の中で 0.5 画素だけ動いた）を「止まっていた」と測るため。
- 規則が無いときの値（試験 `stop_then_lift_is_not_a_fling` 等のコメント）: 払ってから止めて離すと、2 次の当てはめで**逆向きに** 130〜270 dp/秒の
  フリックになっていた（実機の記録の「逆向き」の離し）。実機の問題の 3 回を再生すると、受け取りの時刻では 3 回とも上限の 8,000 dp/秒。
- **静止から 1 フレームほどの間に払って離した短い動きはフリックにならない**（R3 は標本の幅 16.7 ms 未満を捨て、R2 は区間でほぼ止まっていれば 0）。
  120 Hz の走査なら、払い始めてから 3 標本（16.7 ms）以上動けば残る（試験 `real_fast_flick_survives_the_rules` は 33〜120 ms の払い）。

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
| `velocity_lift_off_ms` | 16.7（1000/60） | R2 の持ち上げの間（§5.1。0 で R2 を切る。`velocity_stop_ms` 以上にすると効かない） | 1 フレーム（60 Hz）。Android は指の移動を vsync ごとにまとめて届け、2026-09-29 の実機の記録（roadmap §3.9.2 の (c)）では離す瞬間の飛びが 3 回とも離したフレームの 1 回分の中にあった |
| `velocity_min_span_ms` | 16.7（1000/60） | R3 の推定の幅の下限（0 で R3 を切る） | 1 フレーム。1 フレームより短い幅は受け取りのまとまりの中の標本だけから出た値で、物理の速度ではない |
| `velocity_min_sample_interval_ms` | 1 | R4 の標本の間隔の下限（0 で R4 を切る） | USB HID の最速の報告の間隔 1 ms（1000 Hz）。タッチの走査は速い機種でも 720 Hz ≈ 1.4 ms。これより短い間隔は受け取りのまとまり |
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
| `Kind` | 種類（Tap / LongPress / DragStart / DragUpdate / DragEnd / Fling / PressDown / PressCancel / PressUp / PinchStart / PinchUpdate / PinchEnd） |
| `PointerId` | 指の番号（0 起点） |
| `Position` | 今の位置（キャンバスの画素。画面の中央が原点・Y 下向き。`Input.MousePositionCanvas` と同じ） |
| `ScreenPosition` | 今の位置（画面の画素・左上原点。`Input.MousePos`・`Touch.Position` と同じ） |
| `LocalPosition` | ノードの見た目の矩形の左上が原点・ノードの単位（dp のキャンバスなら dp）。スライダの位置に使う |
| `StartPosition` | 押した位置（キャンバスの画素） |
| `Delta` / `DeltaDp` | DragStart: 押した位置からの移動（slop の分）。DragUpdate: 前のドラッグのイベントからの移動。軸のドラッグは軸へ射影 |
| `Velocity` / `VelocityDp` | 直近の標本から推定した速度（上限で切り詰め。軸のドラッグは射影）。DragUpdate / DragEnd / Fling。§5.1 の規則（止まっていた・持ち上げの揺れ・幅が短い）に当たれば 0 |
| `TotalDelta` | 押した位置からの移動（射影しない） |
| `Duration` | 押してからの時間（秒。イベントの時刻） |
| `Canceled` | DragEnd・PinchEnd が取り消しで来た（速度 0） |
| `Scale` / `ScaleX` / `ScaleY` | ピンチの倍率（全体・横・縦。ピンチの始まりからの比。ピンチ以外は 1。W2-8） |

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
  ピンチ（W2-8: slop を超えたら始まる・倍率は始めたときからの比・ノード自身のドラッグを取り上げる・外側の一覧が取っている指では始めない・3 本目の指・取り消し・
  「動いている」・縦に並んだ指の横の倍率は 1）、
  遮る板、フレームの区切りに依らない結果、`Input` の時刻つきの記録（マウスの合成の指・Android の指・注入の座標とシーケンスの予定の時刻）
- PC（`SEED.exe` を注入で操作。作業フォルダの UiSpike の写しの UiGesture.scene）: 17 の検査でスクリプトへ期待どおり届くこと（倍率 2 は 4 の検査）
- 実機（Pixel 6a）: **未実施**（W2-6a・W2-10a の頭で、タップ・スクロールの中のボタン・フリックの速度を指で確かめる）
- **2026-09-29（§5 の MotionEvent の時刻・§5.1 の R1〜R4）**: 単体テスト（`cargo test --lib gesture`・`core::input`）で、止めて離す（止め 40〜100 ms・
  揺れ 1 dp 未満）・離す瞬間の 1.5〜4.2 dp の飛び（受け取りの時刻を模した列と本当の時刻の列）・実機の記録の問題の 3 回の再生が速度 0、
  時刻の ±3 ms の揺れと受け取りのまとまり（300 通り）で推定が真の速度の ±30%、本物の速いフリック（3,000 dp/秒・120 Hz・等速と加速）が残ること、
  控えの突き合わせ（順どおり・取りこぼし・余り・見つからない・上限・履歴・Started / Moved / Ended / Cancelled）・時刻の換算・記録の時刻の逆行の
  切り上げ・全部の取り消しとの前後。**実機の確かめは未実施**（`[SEED TOUCH TIME]` の `rec_ns` と `ev_ns` の一致、`[SEED GESTURE] release` の `rule`）

## 11. 制限と持ち越し

- **W2-3（スクロールと一覧）**: → **2026-09-28 に済**（[ui_scroll_list.md](ui_scroll_list.md)）。スクロールの物理は予定を変えて **Rust**（`CanvasScrollComponent`。窓は CanvasGesture 無しで向きのドラッグとフリックでアリーナに参加する）。行の再利用で押している行が消える問題は、一覧が使い回す前に `GameObject.CancelGestures`（`GestureArenaSet::cancel_nodes`。行と子孫の押下・ドラッグを取り消し、PressCancel・取り消しの DragEnd を次のフレームに配る）で手当てした。慣性・ScrollTo の途中のタップはエンジンが止め、その指を中の行へ渡さない（`GestureHitScene::with_absorbing`。経路で動いている窓より葉の側を外す）。スクロールのシステムはこのフレームに触れた指の経路（`take_touched`）と今触れている経路（`nodes_under_pointers`）で「触れて止める」を決める
- **W2-10a**: §9 の申告を「描く理由」へつないだ（2026-09-28。済）。止まっている間に届いた指は WindowEvent が起こし、起きた最初のフレームで処理される（記録は捨てない）
- ダブルタップ・回転・ホバー（マウスの乗る・外れる）はアリーナに無い（ホバーは OnPointerEnter / Exit）。**ピンチは 2026-09-28 の W2-8 で足した**（§2.6。
  倍率と中点だけ。1 本離した後の残った指のパンは無い。PC の入力の注入は 1 本の指〈マウス〉だけなので、ピンチは単体テストで確かめた）
- **Android の離した速度（2026-09-29 の実機。roadmap §3.9.2 の (c)）**: 時刻ホイールで指をほぼ止めてから離した 25 回のうち 3 回で、離す瞬間の 1.5〜4.2 dp の飛びと
  同じ向きに最速に近いフリック（98〜109 行）が起きた。→ **2026-09-29 に手当て**: 記録の時刻を MotionEvent の時刻と履歴にし（§5）、速度の推定に R2〜R4 を足した（§5.1）。
  記録の 3 回を再生した単体テストは速度 0。**実機での確かめは残り**（`[SEED TOUCH TIME]`・`[SEED GESTURE] release` のログで見る）
- 控えの突き合わせは位置のビットの一致に頼る（winit と Java が同じ MotionEvent の同じ float を読むので一致する。GameActivity・winit の版を上げたら
  §5 の 1. の「同じにすること」を読み直す）。控えが見つからない Touch は受け取った時刻に戻る（`[SEED TOUCH TIME] matched=no`）
- R3・R2 のため、静止から 1 フレームほどの間に払って離した短い動きはフリックにならない（§5.1）。R3 は DragStart・DragUpdate の速度にも効く
  （ドラッグの最初の 1 フレームの間の速度は 0。スクロールの窓の `Velocity` の表示に出る）
- 3D ワールドキャンバスのノードは参加しない（ポインタのイベントと同じ）
- 内部解像度固定（レターボックス）のときも dp は画面の DPI から求めるので、slop・最小のヒット領域は内部解像度の画素では少しずれる（`units.rs` と同じ扱い）
