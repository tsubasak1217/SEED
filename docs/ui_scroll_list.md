# スクロールと一覧（W2-3 の正典。2026-09-28）

キャンバス UI の**スクロール**（指のドラッグ・離した後の慣性・端の跳ね返り・スナップ・入れ子・スクリプトからの ScrollTo）と、
**一覧**（見えている行だけをプレハブから作って使い回す `SEED.UI.ListView`）、**スワイプの操作**（行を横へずらすと削除などのボタンが出る
`SEED.UI.SwipeActions`）の規則。背景と段階は [app_platform_roadmap.md](app_platform_roadmap.md) §3.3 の「スクロール」「一覧」と §3.8.5 の W2-3。

| 置き場 | 役割 |
|---|---|
| `runtime/src/engine/components/canvas_scroll_component.rs` | スクロールの領域の設定（`CanvasScrollComponent`。保存する） |
| `runtime/src/engine/core/canvas_scroll/` | スクロールの本体（World・FFI に触れない純ロジック。各ファイルの冒頭に規則） |
| `canvas_scroll/constants.rs` | 物理の定数の表（出典つき。§3 と同じ） |
| `canvas_scroll/physics.rs` | 1 次元のシミュレーション（端で止める慣性・減衰・ばね・跳ね返り・ScrollTo） |
| `canvas_scroll/overscroll.rs` | 指のドラッグを位置へ当てる（範囲・端の外の摩擦） |
| `canvas_scroll/ballistic.rs` | 離した後の動きを選ぶ（慣性・跳ね返り・ページ・間隔のスナップ） |
| `canvas_scroll/state.rs` | 実行中の状態（`CanvasScrollState`。同じスロットのエンティティ・保存しない） |
| `canvas_scroll/controller.rs` | スクロール 1 つの段階の移り変わり |
| `canvas_scroll/nesting.rs` | 入れ子の受け渡し |
| `canvas_scroll/visibility.rs` | 見える範囲の外の部分木を飛ばす |
| `canvas_scroll/events.rs` | スクロールのイベント（開始・位置・終了） |
| `runtime/src/engine/core/canvas_layout/scroll_view.rs`・`pass.rs` | レイアウトの走査の側（中身の平行移動・窓と中身の大きさ・ノードの範囲） |
| `runtime/src/engine/core/app_base/app/scroll_events.rs` | フレームの処理（ジェスチャーの受け取り・物理・イベントの配達・大きさの受け取り・描く理由） |
| `runtime/src/engine/core/scripting/canvas_scroll_api.rs`・`scroll_ffi.rs` | スクリプトの欄・イベントの FFI |
| `scripting/src/Api/CanvasScroll.cs`・`ScrollEvent.cs` | `SEED.CanvasScroll`・`SEED.ScrollEvent`・`SEEDScript.OnScroll*` |
| `scripting/src/Api/UI/` | `SEED.UI.ListView`・`ListViewLayout`・`ListViewRecycler`・`SwipeActions`・`SwipeGroup`・`SwipeMath` |
| `editor/src/Panels/InspectorPanel.CanvasScroll.cs` | インスペクタ（「コンポーネント追加 → UI → Canvas Scroll」） |

---

## 1. 使い方

2D キャンバスのノードに **Canvas Scroll** を付けると、そのノードが中身（子）をずらして見せる**窓**になる。窓の外を切るには同じノードに
**Canvas Clip** も付ける（付けると窓の外の子を描画アイテムと当たり判定から外す＝§5）。指のドラッグは CanvasGesture を付けなくても受ける。

```
ListRoot（Canvas 360×640・CanvasClip・CanvasScroll 縦・一覧のスクリプト）
└─ Content（Canvas・CanvasStack 縦・fit_height・CanvasLayoutItem fill_width）…… 行を縦に並べる（中身の大きさ = Content の高さ）
   ├─ Row0（Sprite・CanvasGesture タップ）
   └─ …
```

窓自身に CanvasStack を付けてもよい（スクロールの軸は**箱の長さを決めずに**並べる＝行は窓に合わせて縮まない。中身の大きさは並べた長さ＋余白）。
行の多い一覧は `SEED.UI.ListView`（§6）で、見えている行だけを作る。

| 欄（serde の名前） | 既定 | 意味 |
|---|---|---|
| 有効（`enabled`） | true | false の間は位置 0 として置き、指も取らない |
| 向き（`direction`） | vertical | `vertical` / `horizontal` / `both` |
| 端（`edge`） | bounce | `bounce`（端を越えて引っぱれ、ばねで戻る）/ `clamp`（端で止まる） |
| 慣性（`inertia`） | true | false なら離した所で止まる（跳ね返りの戻り・スナップはする） |
| スナップ（`snap`） | none | `none` / `page`（1 回のフリックで最大 1 ページ）/ `interval`（止まる位置に最も近い間隔の倍数） |
| スナップの長さ（`snap_interval`） | 0 | Page で 0 なら窓の長さ。Interval で 0 ならスナップしない |
| 入れ子で渡す（`hand_off_to_parent`） | true | 同じ向きの外側のスクロールへ、端に達した残りのドラッグとフリックを渡す（§4） |
| 中身の大きさ（`content_size`） | auto | `auto`（子の矩形のいちばん遠い端。窓がコンテナなら並べた中身＋余白も）/ `fixed`（`content_width`・`content_height`） |
| 見える範囲の外を飛ばす（`cull_outside`） | true | 切り抜きが有効なときだけ効く（§5） |
| 飛ばす判定の余白（`cache_extent`） | 250 | キャンバスの単位（Flutter の defaultCacheExtent） |
| Clamp の摩擦（`fling_friction`） | 0.015 | Android の ScrollFriction |
| Bounce の減衰（`bounce_drag`） | 0.135 | 1 秒で速度が何倍になるか |

値（位置・大きさ・速度）はすべて**キャンバスの単位**（dp のキャンバスなら dp）。位置 0 は中身の先頭が窓の先頭で、下・右へスクロールすると増える。
物理の定数は dp で決まっているので、端末の表示倍率に依らず同じ手触りになる（1 単位の画素数と 1 dp の画素数で換算する）。

## 2. 分担（Rust と C#）

| 側 | 受け持つもの | 理由 |
|---|---|---|
| Rust（ECS のコンポーネントとシステム） | スクロールの位置・物理（慣性・跳ね返り・スナップ）・入れ子の受け渡し・触れて止める・レイアウトへの反映・見える範囲の外を飛ばす・「動いている」の申告 | 位置はレイアウトの走査の入力で、見える範囲の外を飛ばす判定もレイアウトの走査の中でしかできない。入れ子の受け渡しはジェスチャーのアリーナの捕捉と組み合わせる。60 fps の慣性を FFI の往復なしで回し、動いている間を描く理由に直接つなぐ。インスペクタで設定を編集できる |
| C#（`SEED.UI`。SEEDScripting に同梱） | 一覧の仮想化（見える行の範囲・使い回しの割り当て・行の配置・データの結び付け）・スワイプの操作・部品の見た目の振る舞い | データ（行の数・中身）はスクリプトが持つ。行のプレハブと Bind はゲームごとに違う |

roadmap §3.2 の W2-P1 は当初「スクロールの物理は C#」としていたが、上の理由で W2-3 で Rust へ寄せた（W2-2 の input_gestures.md §11 の
「スクロールの物理は C# の部品で」も同じく改めた）。C# からは `SEED.CanvasScroll`（位置の読み書き・ScrollTo）とイベントで使う。

## 3. 物理（出典と定数の表）

2026-09-28 に Flutter の master のソース（`scroll_simulation.dart`・`scroll_physics.dart`・`friction_simulation.dart`・`page_view.dart`・
`curves.dart`・`viewport.dart`）を取得して値と式を確かめた。Android の値は Flutter のソースの注記（「See DECELERATION_RATE」等）が指す
`OverScroller` の定数。コードの正典は `canvas_scroll/constants.rs`。

| 定数 | 値 | 使う所 | 出典 |
|---|---|---|---|
| 減速の指数 `DECELERATION_RATE` | ln(0.78) / ln(0.9) ≈ 2.3582 | Clamp の慣性 | Android `OverScroller`・Flutter `ClampingScrollSimulation._kDecelerationRate` |
| 変曲点 `INFLEXION` | 0.35 | Clamp の慣性 | 同上 `_kInflexion` |
| 係数 | 9.80665 × 39.37 × 160 × 0.84 ≈ 51,890 dp/s² | Clamp の慣性 | 同上 `_physicalCoeff`（重力 × インチ/m × dp/インチ × 手触りの調整） |
| 摩擦 | 0.015 | Clamp の慣性（`fling_friction`） | Android `ViewConfiguration.getScrollFriction()`・Flutter `friction` の既定 |
| 範囲の中の減衰 | 0.135（1 秒で速度が 0.135 倍） | Bounce の慣性（`bounce_drag`） | Flutter `BouncingScrollSimulation` の `FrictionSimulation(0.135, …)` |
| 端の外の摩擦 | 0.52 × (1 − はみ出し/窓の長さ)² | Bounce で端の外へ引く・戻す | Flutter `BouncingScrollPhysics.frictionFactor`（ScrollDecelerationRate.normal） |
| ばねへ渡す速度の上限 | 5,000 dp/s | Bounce の慣性が端を越えるとき | Flutter `BouncingScrollSimulation.maxSpringTransferVelocity` |
| ばね | 質量 0.5・硬さ 100・減衰比 1.1（過減衰） | 端へ戻る・ページのスナップ・ばねのスナップ | Flutter `ScrollPhysics._kDefaultSpring` |
| 止まったとみなす速度・距離 | 20 物理画素/秒・1 物理画素 | すべての動きの終わり | Flutter `ScrollPhysics.toleranceFor`（1/(0.05 × dpr) 論理画素/秒・1/dpr 論理画素） |
| ページのフリック | 位置/ページ ± 0.5 を丸めたページ | Page のスナップ | Flutter `PageScrollPhysics._getTargetPixels` |
| ScrollTo の曲線 | Cubic(0.42, 0.0, 0.58, 1.0)・逆算の誤差 0.001 | ScrollTo | Flutter `Curves.easeInOut`・`Cubic._cubicErrorBound` |
| 飛ばす判定の余白 | 250 | 見える範囲の外（§5）・一覧の前もって作る範囲 | Flutter `RenderAbstractViewport.defaultCacheExtent` |

**式**（`physics.rs`）
- **Clamp の慣性**（Flutter ClampingScrollSimulation）: 基準の速度 = 摩擦 × 係数 / 変曲点、時間 T = rate × 変曲点 × (|v| / 基準)^(1/(rate−1))、
  距離 = v × T / rate、x(t) = x₀ + 距離 × (1 − (1 − t/T)^rate)。距離は Android の `getSplineFlingDistance` と一致する（単体テストで検算）。
  **例: 1,000 dp/秒 → 194.3 dp を 0.458 秒で**。端に着いたら止まる（Android の端の光・伸びの表示は無い）。
- **Bounce の慣性**（Flutter BouncingScrollSimulation）: 範囲の中は x(t) = x₀ + v (0.135^t − 1) / ln 0.135（止まる極限 x₀ + v/2.0）、
  速度が許容を下回ったら終わり。止まる極限が端を越えるなら、端を通る時刻からばね（端の位置、その時刻の速度）へ乗り換えて端へ戻る。
  範囲の外から始める（引っぱって離した）ときは最初からばね。
- **端の外へ引く**: Flutter はフレームごとに摩擦をその時のはみ出しで 1 回掛ける（1 フレームの移動が大きいと結果が刻みに依る）。ここは同じ摩擦の曲線を
  dx/do = 0.52 (1 − x/V)² の微分方程式として解いた閉じた式（1/(1 − x/V) が指の移動に比例）で当て、フレームの刻みに依らない。
  窓 800 の端で 300 引くと約 125 はみ出す（PC の確認で 127.6）。はみ出しは窓の長さに近づくほど進まない。
- **ばね**: 過減衰 x(t) = c₁e^{r₁t} + c₂e^{r₂t}（r = (−c ± √(c² − 4mk)) / 2m）。臨界・不足減衰の解も持つ（既定のばねは過減衰で振動しない）。
- **間隔のスナップ**: スナップしない動きで止まる位置に最も近い倍数を目標にし、その向きへ十分な速度があれば「目標でちょうど止まる減衰」
  （drag = exp((v₀ − v_end)/(x₀ − 目標))。Flutter の `FrictionSimulation.through`）、無ければばねで寄せる。
- **ScrollTo**: 始め → 目標（範囲へ収める）を Curves.easeInOut で。速度はイージングの傾き（ベジェの媒介変数の微分の比）から求める。

指を離したときの速度は W2-2 のフリックの速度（直近 100ms・20 標本の最小二乗・上限 8,000 dp/秒）。フリックにならない（50 dp/秒 未満・止まってから離した）
ときは速度 0（Flutter の `ScrollDragController.end` と同じ）。ドラッグの始まりは slop（8 dp）を超えた分だけ当てる（押した位置から飛ばない）。

## 4. ドラッグ・入れ子・触れて止める

**段階**（`state.rs` の `ScrollPhase`。Flutter の ScrollActivity に当たる）: Idle → Dragging → Ballistic（慣性・跳ね返り・スナップ）→ Idle。
ScrollTo は Animating。慣性・ScrollTo の途中に触れると Held（指を離すと速度 0 で離す＝はみ出しの戻り・スナップ）。

| 規則 | 内容 |
|---|---|
| 指を取る | 窓は CanvasGesture が無くても、向きに合わせたドラッグとフリックでアリーナに参加する（縦 → 縦だけ・横 → 横だけ・両方 → 全方向。タップ・押下の見た目なし・ヒット領域を広げない）。CanvasGesture もあればタップなどはそちらの設定 |
| 向きの違う入れ子 | 縦の一覧の中の横の帯: W2-2 のアリーナの軸の競いで、最初の指の動きの向きで持ち主が決まる |
| 同じ向きの入れ子 | アリーナでは内側が勝つ。移動は内側が先に範囲の中で使い、端に達した残りを同じ軸の祖先の窓へ近い順に渡す（Android の NestedScrolling の dispatchNestedScroll と同じ順）。子の `hand_off_to_parent` が false ならそこで切る。全員が使い切れなければ内側が Bounce なら跳ね返りとして受ける |
| 入れ子のフリック | 内側から順に「はみ出している」か「速度の向きへまだ動ける」最初の窓がフリックを受ける（鎖が切れたらそこまで。誰も動けなければ内側＝Bounce なら端で跳ね返る）。他の関わった窓は速度 0 で離す（スナップ・はみ出しの戻り） |
| 触れて止める | 慣性・ScrollTo の途中の窓は、触れた指を自分で受けて止める。そのとき**中の子へ指を渡さない**（アリーナの経路で窓より葉の側を外す。Flutter の Scrollable が動いている間 IgnorePointer にする・Android の RecyclerView が慣性中の指を取るのと同じ）＝慣性の途中のタップで行が押されない |
| 大きさが変わった | 止まっている窓は速度 0 で離す（範囲の外ならばねで戻る・スナップし直す）。慣性の途中は今の速度で作り直す（Flutter の applyNewDimensions） |
| 窓と中身の大きさ | **前のフレームの描画**のレイアウトの表から写す（Play の最初の描画まで不明。中身の大きさの変化は 1 フレーム遅れて範囲に効く） |

## 5. 見える範囲の外を飛ばす

| 項目 | 規則 |
|---|---|
| 対象 | 切り抜き（CanvasClipComponent）と `cull_outside`（既定 true）が両方有効な窓の子孫。切り抜きの外なので、飛ばしても見た目は変わらない |
| 見える範囲 | 窓の切り抜きの AABB を `cache_extent`（既定 250。キャンバスの単位）だけ広げたもの（キャンバス空間） |
| 判定 | レイアウトの走査（`CanvasLayoutPass`）の中で、子孫の**部分木の範囲**（そのノードと子孫の範囲の和）を集め、見える範囲と交わらない部分木をまるごと飛ばす（表の行の `culled`）。ノードの範囲は切り抜き・当たり判定と同じ矩形（キャンバス領域・レイアウトの矩形 → 最初の Sprite）。矩形の無いノード（テキストだけ・パーティクル）は位置の 1 点（はみ出す文字は余白の 250 で受ける） |
| 飛ばすもの | スプライト・スキンスプライト・テキスト・2D パーティクルの描画アイテム、エディタの GPU の ID 描画、当たり判定（`pick_2d`＝エディタの選択とポインタのイベント）、ジェスチャーの当たり判定 |
| 飛ばさないもの | `SEED.Draw` の座標空間の登録（窓の外の空間も引ける）、エディタのキャンバス枠、2D 物理、切り抜きを当てない描画（3D ワールドキャンバス） |
| 費用 | スクロールを使わない木では範囲を集めない（従来と同じ計算）。表の行はアクター木と 1 対 1 のまま（読み手は `is_drawn_in_view` / `is_pickable_in_view` で飛ばす） |

## 6. 一覧（`SEED.UI.ListView`。C#）

見えている行（と前後の `cache_extent` の分）だけをプレハブから作って使い回す。使い方は [scripting_api.md](scripting_api.md) §7.15。

| 規則 | 内容 |
|---|---|
| 並び（`ListViewLayout`） | 先頭の余白・行（固定の長さか行ごと）・間隔・末尾の余白。行ごとのときは先頭の位置の累積和を持ち、位置から行を二分探索で引く |
| 見える行 | 窓 [位置, 位置 + 窓の長さ] を前後に cache_extent だけ広げた範囲と交わる行 |
| 使い回し（`ListViewRecycler`） | 範囲の外へ出た行のスロットを外し、範囲の中でスロットの無い行へ割り当てる（いま外したものを先に使う）。付いたままの行は動かさない。足りなければ作る数を返す |
| 行を作る | `GameObject.Instantiate(プレハブ, 窓か Content)` の直後に `Visible = false`（プレハブの位置に一瞬出ない）。構築はフレーム末尾なので**次のフレームから使える** |
| 使い回すとき | 付け替える前に `GameObject.CancelGestures()` で行と子孫の押下・ドラッグを取り消し（PressCancel・取り消しの DragEnd が次のフレームに届く＝**W2-2 の持ち越し「押している行が消えると PressCancel の届け先が無い」の手当て**）、`Recycled(行, 前の番号)` を呼ぶ |
| 中身の長さ | 行の並びの全体の長さを、スクロールの `Fixed` の中身の大きさにする |
| 行の配置 | 行のスクロールの軸の位置を「行の先頭 + pivot × 行の長さ」にする（交差の軸はプレハブの値のまま） |

`GameObject.Visible` の書き込みは、同じフレームに `Instantiate` したばかりのアクターにも効くようにした（以前は構築前のアクターを受けずに黙って失敗していた。
コマンドは発行順に当たるので、生成の後に表示フラグが当たる）。

## 7. スワイプの操作（`SEED.UI.SwipeActions`。C#）

行を横へずらすと操作のボタン（削除・編集）が出る。値の出典は Android の `ItemTouchHelper`（2026-09-28 に androidx のソースで確認）。

| 規則 | 内容 |
|---|---|
| 作り | 行: CanvasGesture（tap=false・drag・fling・drag_axis=horizontal）。子に Actions（右端の操作のボタン）と Front（ずらす見た目。**受けるジェスチャーの無い CanvasGesture＝遮る板**にして、閉じているとき後ろの操作のボタンを押せないようにする） |
| ずらす | Front を閉じた 0 〜 開いた量（操作のボタンの幅）の間でずらす（端より先へは引けない）。移動は行のローカルの位置の差（キャンバスの単位） |
| 開く・閉じる | 離したときの横の速さが 120 dp/秒（`item_touch_helper_swipe_escape_velocity`）以上なら向きで決める。遅ければ開いた量の半分（`getSwipeThreshold` = 0.5）以上ずらしていれば開く |
| 動き | 250ms（`DEFAULT_SWIPE_ANIMATION_DURATION`）・Material の fastOutSlowIn。動いている間は `Redraw.Request()` |
| 組（`SwipeGroup`） | ある行のドラッグが始まると、同じ組の他の開いている行が閉じる。一覧のスクロールが始まったら持ち主が `CloseAll`（`OnScrollStart` から） |
| 行の使い回し | `Reset()` ですぐ閉じる（ListView の `Recycled` から） |

## 8. イベントとスクリプト

- `SEEDScript.OnScrollStart / OnScroll / OnScrollEnd(SEED.ScrollEvent)`: 窓のノードのスクリプトへ。スクリプトフェーズより前に配る。
  1 フレームに最大で開始 → 位置 → 終了（位置の書き込み＝すぐ移すでも 3 つが同じフレームに届く）。
- `SEED.CanvasScroll`: 設定の読み書き・`Position`（書くとすぐ移す）・`ScrollTo(位置, 秒)`・`JumpTo`・`Velocity`・`Phase`・`ViewportSize`・`ContentSize`・`MaxPosition`。
- `GameObject.CancelGestures()`: 行の使い回しの前の取り消し。
- 診断: 環境変数 `SEED_SCROLL_LOG=1` でスクロールのイベント・レイアウトの表の数（ノード・見える範囲のノード・窓・飛ばした数）・2D の描画アイテムの数を
  変わったときにログへ出す（`[SEED SCROLL]`）。

## 9. 描く理由（W2-10a との組み合わせ）

窓が動いている間（ドラッグ・慣性・跳ね返り・ScrollTo・スクリプトの位置の要求の処理待ち）はエンジンが「動いている」（`motion`）を申告する
（`app/redraw_hooks.rs` の `redraw_motion_active` → `scroll_motion_active`）。`render_policy: on_demand` でも慣性の途中で止まらず、
止まったら 10 フレームで描画も止まる（PC で確かめた。§10）。C# の部品（SwipeActions）は動いている間 `Redraw.Request()` を呼ぶ。

## 10. 検証（2026-09-28・PC）

- **単体テスト（Rust）** `cargo test -p SEED --lib -- scroll canvas_layout gesture redraw`: Clamp の慣性の距離が Android の式と一致・単位（dp/px）に依らない・
  摩擦で短くなる、減衰の止まる位置と通る時刻と through、ばね（過減衰・行き過ぎない・速度を持って始める）、跳ね返り（端でばねへ乗り換えて端へ戻る・
  越えない慣性は許容の速度で終わる・範囲の外から戻る）、端の外の摩擦（閉じた式・刻みに依らない・戻す）、Clamp・Bounce の選び方と端で止まる、
  ページ・間隔のスナップ（ちょうど倍数で止まる）、段階（フリック → 触れて止める → 離す）、ScrollTo（半分の時間で半分・範囲へ収める・速度がなめらか）、
  大きさの縮みでばねで戻る、入れ子（受け渡し・渡さない設定・Clamp の残り・フリックの受け手）、イベントの順、レイアウト（位置だけずれる・窓と中身の大きさ・
  Edit ではずれない・1,000 行で見える範囲の外を飛ばす・切り抜きと設定が要る・Fixed・窓でもある Stack・dp の換算）、ジェスチャーの組み合わせ
  （窓は CanvasGesture 無しで参加・スクロールした行は位置のずれた所で当たる・切り抜きの外は押せない・飛ばした行は材料に入らない・動いている窓は指を吸い込む・
  使い回しの取り消し）、スクリプトの欄、FFI の大きさ。
- **単体テスト（C#）** `dotnet run --project editor/tests/UiListViewTests`: 並び（固定・行ごと）・見える行の範囲・行を窓に見せる位置・使い回し（ランダムな 2,000 回の
  スクロールで 1 行 1 スロット）・スワイプの判定と曲線（11 件）。
- **PC**（作業フォルダの UiSpike の写し。`SEED.exe` を IPC の入力の注入で操作。debug ビルド）:
  - 1,000 行の縦の一覧（行ごとに Sprite・Text・タップ。行 3 に横の帯、行 5 にスワイプの行、行 8 に同じ向きの入れ子）: 止まった一覧の行のタップ、
    ゆっくり 300 px のドラッグで位置 292（slop 8 を除く）・フリックなし、1,875 px/秒のフリックで離した後に約 1,159 まで流れて止まり、10 フレーム後に
    `on_demand` で描画が止まる、上の端で 300 px 引いて −127.6 まではみ出しばねで 0 へ、フリックの途中のタップで止まり（End・Held）行は押されない、
    行を押して 100ms 後に PressDown → 縦に動かして PressCancel → 一覧のドラッグ、横の帯の横ドラッグは帯だけ・縦ドラッグは一覧、行 5 のスワイプで開いて
    削除のタップ、一覧のスクロールでスワイプが閉じる、入れ子（最大 152）を上へ 300 px で内側が 152 まで・残り 140 が外側へ、ScrollTo（0.4 秒で 2,000 へ）
  - **性能（見える範囲の外を飛ばす）**: 同じシーンの位置 20,000 で、飛ばすとき 2D の描画アイテムは **スプライト 20・テキスト 16**（表 2,032 行のうち
    描画の対象 37 行・飛ばした 1,995 行）、飛ばさないとき **スプライト 1,019・テキスト 999**。UI の描画順の統合・GPU の積み込みは **0.67 ms ⇔ 31.9 ms**、
    スプライトの収集は 2.9 ⇔ 6.7 ms、フレーム（CPU）は **50.3 ms ⇔ 73.2 ms**（debug ビルド。スクロールのシステム自体は 0.15 ms。残りの大半は 2D の全ノードをたどる既存の処理＝2D 物理の同期・2D スクリーン座標の収集・
    ポインタのイベントの表で、ノード数に比例する。docs/backlog.md）
  - **一覧（ListView）の 1,000 件**: 作った行は 18 行で止まり（フリック・ScrollToIndex(500) で見える範囲が変わっても増えない）、表は 111 行、描画アイテムは
    スプライト 62〜74・テキスト 28〜34、フレーム（CPU）3.8 ms（debug）・60 fps。行のボタンのタップ、行のスワイプと一覧のスクロールで閉じる、
    ボタンを押したまま一覧を先頭へ移すと行が使い回されて PressCancel・離しても Tap にならない
- **回帰**: WarashibeFishing の複製の図鑑のボタンの縁 56 点のクリック（当たり 34・外れ 22）と図鑑の画素が変更の前後で一致（スクロールを使わないゲームは不変）。

## 11. 実機での確かめ方（Pixel 6a。W2-3 の時点で未実施）

利用者と一緒に行う（慣性の手触りは指でしか分からない）。UiSpike の写しの `UiScroll.scene`・`UiList.scene` を SeedAndroid の `run` で入れ、
`SEED_SCROLL_LOG` の代わりにスクリプトのログ（`[SCROLL]`・`[LIST]`・`[ROW]`・`[SWIPE]`）を logcat で見る。

1. 一覧を指で速くフリック → 慣性で流れて止まる。止まってから 10 フレームで `[SEED REDRAW] 描画を止めます`（on_demand）
2. 上の端で下へ引く → 端の外へ出て、離すとばねで戻る（Bounce）。端（Clamp）に変えた一覧では端で止まる
3. 慣性の途中でタップ → 止まり、行は押されない。止まった後のタップは行が押される
4. 行のボタンを押してすぐ縦へ動かす → ボタンは押されない（PressCancel）。横の帯は最初の指の向きで帯か一覧かが決まる（斜めの指で取り違えない）
5. 行を左へスワイプ → 開く・閉じるの閾値（速さ 120 dp/秒・半分）と 250ms の動き。一覧をスクロールすると閉じる
6. `SEED.Time.Fps` と GPU の計測（android.md §22.6）でスクロール中 60 fps を保つか（UC-2）。1,000 件の ListView と、静的な 1,000 行の一覧の両方
7. 手触りの調整が要れば `fling_friction`・`bounce_drag`（インスペクタ）で詰め、既定を変えるなら constants.rs と §3 の表を直す

**2026-09-28 の実機の回（Pixel 6a / Android 17。詳細は roadmap §3.9）**: ギャラリーのページ（一覧・ホイールを含む）を `input swipe` で払い続けると、
開発用の既定の .so（`dev`・最適化なし）は **23〜26 fps**（CPU 38 ms/フレーム）、`develop`・`release` の .so は **59.7 fps**（8.5〜8.7 ms）。止まると 10 フレームで
`描画を止めます` → 0 fps。手触りの感想（慣性・跳ね返り）と行のスワイプ（1〜5）は fps を直すまで保留（未実施）。

**2026-09-29 の手触りの確認（roadmap §3.9.2。develop の .so）**: 利用者の感想はギャラリーのページで慣性の距離「ちょうどよい」・止まり方「自然」・端の跳ね返り「自然」・
カクつきなし（1・2。払い続けた窓は 56.7〜60.0 fps、止まると 10 フレームで描画停止）。行のスワイプ（5。`UiList.scene` を dp にした写し）は開く・閉じる・別の行を開くと
閉じる・一覧のスクロールで閉じる、とも期待どおり（開く 34・閉じる 34）。要望「大きく左へ払ったらそのまま削除」（backlog）。3・4 と 1,000 行の比較（6）は行っていない。

## 12. 制限と持ち越し

- **端の表示**: Android 12 以降の伸び（stretch）・それ以前の光（glow）の表示は無い（Clamp は黙って止まる）。W2-4 以降の部品で必要なら足す
- **スクロールバー**: 無い（roadmap §3.3 で任意）。窓の `ScrollEvent` の位置と `MaxPosition` から部品で描ける
- **iOS の続けてのフリックの加速**（Flutter の `carriedMomentum`）・`ScrollDecelerationRate.fast`・ゴムの戻り（`rubberBandSpring`）は入れていない（既定のばねで戻る）
- **跳ね返りのばねへ渡す速度の上限**: Flutter は正の向きだけ min で切り詰める（負の向きは上限なし）。ここは向きによらず大きさで切り詰める
- **回転・拡大した窓**: 指の移動は窓のローカルの軸へ直すが、窓の scale は 1 を前提（窓の大きさを子の座標で測る）。見える範囲は切り抜きと同じく外接矩形
- **ページのスナップの行き過ぎ**: ばね（過減衰）で寄せるので、とても速いフリックは目標を少し越えてから戻る（Flutter の PageView と同じ式）。実機で詰める
- **窓と中身の大きさは前のフレームの描画から**（1 フレーム遅れ）。Play の最初の描画まで窓の大きさは 0（ListView は行を置かない）
- **新しく作った行は次のフレームから**（プレハブの構築がフレーム末尾のため）。速いスクロールで行が遅れて見えないよう、前後の余白（250）の分まで先に作る
- **静的な行の多い一覧は重い**: 見える範囲の外を飛ばしても、2D の全ノードをたどる既存の処理（2D 物理の同期・2D スクリーン座標の収集・ポインタのイベントの表）が
  ノード数に比例して残る（debug で 2,032 ノードのとき各 6〜12 ms。docs/backlog.md）。行の多い一覧は ListView で作る
- **W2-4 以降**: ボタン・トグルなどの部品の押下の見た目（`Recycled` で戻す）、時刻ホイール（W2-5 で済: `snap: interval` の上に C# の `SEED.UI.WheelPicker` で作った。
  スクロール自体に端をつなげるループは無く、循環は ListView の使い回しで作る。docs/ui_components.md §11）、
  シート（W2-7。下へ引いて閉じる）
- **実機の手触り**: 未確認（§11）
