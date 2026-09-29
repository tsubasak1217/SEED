# グラフ（W2-8 の正典。2026-09-28）

キャンバス UI の**グラフ**（`SEED.UI` の `LineChart`・`BarChart`）の規則: 折れ線（点の印・滑らかな曲線・線の下の塗り・基準線）、棒（縦・横・積み上げ・角丸・
空の棒）、軸（目盛り・格子線・目盛りの文字の書式）、吹き出し（タップ・長押しで最寄りの点・列）、パンとズーム（横のドラッグ・慣性・2 本指のピンチ・± のボタン）、
日付線のハンドル（折れ線の選んだ点の縦の線の下の丸を横に引いて選びを変える。W2 の手直し P2-4。§6.1）、親の幅に合わせる大きさ（レイアウトの大きさを読む。P2-4。§2.1）。
背景と段階は [app_platform_roadmap.md](app_platform_roadmap.md) §3.3 の「グラフ」・§3.8.5 の W2-8。使う画面は Wake or Pay のアクティビティ
（起床時間の遷移〈30 日〉・起床時間の全期間・ペナルティ履歴。`D:\SEED_projects\WakeOrPay\docs\WAKEORPAY_SEED_SPEC.md` の S-05・S-05a・S-05b）。
Flutter 版は `fl_chart`（`lib/features/activity/wake_time_chart.dart`・`penalty_bar_chart.dart`）。

| 置き場 | 役割 |
|---|---|
| `scripting/src/Api/UI/Charts/Model/` | 純粋な計算（エンジンに触れない。`editor/tests/UiComponentsTests` の `ChartTests` で検算）: 範囲（`ChartRange`）・データの形（`ChartData`: `ChartPoint`・`BarDatum`・`GapMode`・`ChartValueFormat`・`BarOrientation`）・目盛り（`ChartTicks`）・軸 1 本の目盛りと文字（`ChartAxis`）・範囲の自動（`ChartAutoRange`）・書式（`ChartFormat`）・値 ⇔ 位置（`ChartMapping`）・パンとズーム（`ChartViewport`）・慣性（`ChartFling`）・単調な 3 次補間（`MonotoneCubic`）・当たり（`ChartHit`。ハンドルの吸い付き `NearestValuedX` も）・棒の形（`BarGeometry`）・枠と吹き出し・ハンドルの置き場（`ChartLayout`）・線の点（`LinePath`）・大きさの読み方と最初のレイアウトの待ち（`ChartSizing`・`ChartLayoutWait`。P2-4）・トークンの名前（`ChartTokens`）・テーマ → 見た目の値（`ChartLook`） |
| `scripting/src/Api/UI/Charts/` | 部品のスクリプト: 共通の土台 `ChartView`（枠・軸・文字・吹き出し・パンとズーム・描き方・日付線のハンドルの受け持ち）、`LineChart`、`BarChart`、目盛りの文字のノードの使い回し `ChartLabelPool`、日付線のハンドルのノードの読み書き `ChartHandle`（P2-4） |
| `scripting/src/Api/Draw.cs`・`DrawTypes.cs` | `SEED.Draw` の見た目の拡張（`DrawStyle`: 画面の 1 画素のアンチエイリアス・線形グラデーション）・線の下の塗り（`Draw.Area`）・点列を `ReadOnlySpan` で渡すメソッド |
| `runtime/src/engine/core/renderer/primitive2d/` | 見た目の拡張の受け口（`queue.rs` の `PrimitiveStyle`）・`PrimitiveKind::Area`・軽い三角形分割（`tessellate.rs` の `stroke_polyline_lean`・`fill_convex`）・画面の画素のフェザーと頂点の色（`pass.rs`）・座標空間のレイヤーの底上げ（`pass.rs` の `apply_space_layer_bias`） |
| `runtime/src/engine/core/input/gesture/pinch.rs` | 2 本指のピンチ（アリーナとは別にすべての指を見る。[input_gestures.md](input_gestures.md) §2.6） |
| `templates/ui/prefabs/line_chart.actor`・`bar_chart.actor`・`chart_label.actor` | 部品のプレハブ |
| `templates/ui/scenes/ui_charts.scene`・`scripts/UiChartsDemo.cs` | 見本（Wake or Pay と同じ形のサンプルデータの 5 つのグラフ） |

---

## 1. 使い方

グラフはプレハブ（`templates/ui/prefabs/line_chart.actor`・`bar_chart.actor`）を置き、画面のスクリプトからデータを渡す。
大きさは**レイアウトが決めた大きさ**（`CanvasTransform.LayoutSize`。縦の `CanvasStack` の cross_align stretch・`CanvasLayoutItem` の fill_width などで伸ばされた大きさ
＝描かれる背景と同じ。W2 の手直し P2-4）。レイアウトの表に無い所（3D ワールドキャンバスの下など）ではグラフのノードの **Sprite の幅・高さ**（dp のキャンバスでは dp。§2.1）。
画面の幅に合わせるには、グラフを縦のスタック（cross_align stretch）に入れるか fill_width にする（Sprite の幅は伸ばす前の値のままでよい）。
書式や操作はインスペクタの欄（ScriptComponent の fields）かスクリプトで決める。

```csharp
// 起床時間の遷移（30 日の島。Wake or Pay の S-05）
var chart = UiWidget.Of<LineChart>(GameObject.Find("WakeWeek"))!;
chart.Interactive = false;                        // 固定（パンとズームなし）
chart.XFormat = ChartValueFormat.Date;            // X = 日の番号 → 目盛りは M/d
chart.YFormat = ChartValueFormat.TimeOfDay;       // 値 = 0 時からの分 → 目盛りは H:mm
chart.FixedXRange = new ChartRange(first.DayNumber, today.DayNumber);
chart.TooltipFormatter = p => $"{ChartFormat.Date(p.X)} {ChartFormat.TimeOfDay(p.Y ?? 0, padHour: true)}"; // M/d HH:mm
chart.SetSeries(points);                          // IReadOnlyList<ChartPoint>（ChartPoint.Minutes(day, timeOfDay)。記録の無い日は null）
chart.SetReferenceLine(average, $"平均 {ChartFormat.TimeOfDay(average)}");

// ペナルティ履歴（S-05a。全期間の日別・コインとカードの積み上げ・パンとピンチ 1〜6 倍）
var bars = UiWidget.Of<BarChart>(GameObject.Find("PenaltyHistory"))!;
bars.SetData(days.Select(d => new BarDatum(d.Day.DayNumber, d.Coins, d.Yen)).ToList());
bars.BarSelected += (_, i) => ShowLogOf(i);       // 列をタップ → その日のログ
bars.Select(lastLossIndex);                       // 既定は最後に何か失った日
```

| 部品 | プレハブ | 主な欄（インスペクタ） | スクリプト |
|---|---|---|---|
| 共通（`ChartView`） | — | `Interactive`（パンとズーム）・`MaxZoom`（6）・`ShowTooltip`・`XFormat`・`YFormat`・`XSteps`・`YSteps`（刻みの候補 "15,30,60"）・`ShowYAxis`・`ShowXAxis`・`ShowGrid`・`EmptyText`（「まだ記録はありません」）・`LabelPrefab`・`HandleHaptic`（日付線のハンドルで点が変わるたびの触感。既定 true。P2-4） | `XLabelFormatter`・`YLabelFormatter`・`ValueRangeOptions`・`FixedValueRange`・`FixedXRange`・`ZoomIn/ZoomOut/ZoomBy`・`ShowRange`・`ScrollToEnd`・`ClearSelection`・`Viewport`・`ViewChanged`・`MarkDirty` |
| `LineChart` | `line_chart.actor`（子 `Handle` = 日付線のハンドル。§6.1） | `Smooth`（単調な 3 次補間）・`FillArea`（線の下の塗り）・`ShowDots`・`Gaps`（`Connect`・`Break`） | `SetSeries(index, points)`・`ClearSeries`・`SetReferenceLine`・`Select`・`Selected`・`PointSelected`（タップ・ハンドルで選んだ点）・`TooltipFormatter`・`DefaultRangeOptions(format)` |
| `BarChart` | `bar_chart.actor` | `Orientation`（`Vertical`・`Horizontal`）・`SlotWidth`（列の間隔。日なら 1）・`ShowEmptyBars`・`HighlightSelection` | `SetData(bars)`・`Select`・`SelectX`・`SelectedIndex`・`BarSelected`・`TooltipFormatter`・`StackColors` |

## 2. 作り（ノードと分担）

```
Chart（Sprite = 全体〈背景〉・CanvasGesture〈タップ・長押し・横のドラッグ・フリック・ピンチ。押下の見た目なし〉・SEED.UI.LineChart / BarChart）
├─ Plot（Sprite〈透明〉= 描く面・CanvasClip）
│   ├─ Ink（空のノード。SEED.Draw の座標空間＝面の左上が原点。Plot の切り抜きで切れる）
│   └─ Labels（面の中の文字: 基準線の「平均 7:12」）
├─ XLabels（Sprite〈透明〉・CanvasClip。横軸の文字。パンで動き、グラフの左右の端で切れる）
├─ YLabels（縦軸の文字）
├─ Empty（Text。データが無いときだけ）
├─ Handle（任意。折れ線の日付線のハンドル。Sprite〈楕円〉・CanvasGesture〈横のドラッグだけ・当たり 48 dp〉・GestureRelay・レイヤー +1。§6.1。
│          Tooltip の直前＝吹き出しと重なれば吹き出しが上。無いプレハブは今までどおり）
├─ Tooltip（Sprite〈角丸〉・レイヤー +1）└─ Text
└─ ZoomIn・ZoomOut（任意。SEED.UI.Button。子にあれば ChartView がつなぎ、上限・下限で押せなくする）
```

子の pivot・anchor は 0（位置 = 左上。ChartView が位置と大きさを書く）。

| 側 | 受け持つもの |
|---|---|
| Rust | 図形の三角形分割と描画（`SEED.Draw`。見た目の拡張・`Area`・軽い三角形分割・レイヤーの底上げ）・切り抜き（W2-1a）・ピンチの判定（アリーナ）・描かない判断（W2-10a） |
| C#（`SEED.UI`） | 範囲・目盛り・書式・位置の計算・パンとズームと慣性・吹き出しと選び・文字のノードの使い回し（`ChartLabelPool`。1 つの文字 = `chart_label.actor` のノード 1 つ。足りない分は作って翌フレームから使う＝ListView の行と同じ）・子の位置と大きさ |

**フレームの流れ**（`ChartView.OnWidgetUpdate`）: 大きさを読む → 慣性・± の動きを進める → データ・見える範囲・大きさ・テーマが変わったフレームだけ
位置を計算し直す（`Rebuild`: 範囲 → 目盛り → 値 ⇔ 位置 → 線の点・棒の輪郭 → 文字のノードを書き換える〈前の値から変わったものだけ〉）→
**毎フレーム** `SEED.Draw` を積む（格子線・軸の線 → データ → 選んだ印）と吹き出しを置く。`render_policy: on_demand` で止まっている間はフレームが回らない
（積まない）。見た目が変わるとき（タップ・パン・データ）は `Redraw.Request()`、慣性・± の動きの間は毎フレーム頼む。

**データの単位**: X・値は軸の書式に合わせた数（日付 = `DateOnly.DayNumber`・時刻 = 0 時からの分・数）。`ChartPoint.Day(day, y)`・`ChartPoint.Minutes(day, timeOfDay)`。
欠けた値（`Y = null`）の点は打たず、前後の点を線でつなぐ（`GapMode.Connect`。Wake or Pay の「記録の無い日は点を打たず線をつなぐ」）。`Break` で線を切る。

### 2.1 大きさ（親の幅に合わせる。W2 の手直し P2-4・2026-09-29）

| 項目 | 規則 |
|---|---|
| 読む値 | `CanvasTransform.HasLayout` なら `LayoutSize`（レイアウトが決めた大きさ＝コンテナ・親に合わせる・セル・安全領域で伸ばされた大きさ。描かれる背景のスプライトと同じ。[scripting_api.md](scripting_api.md) の CanvasTransform）、無ければ Sprite の幅・高さ。`LayoutSize` が壊れた値（非数・無限・負）のときも Sprite（`ChartSizing.Choose`） |
| 1 フレーム遅れ | `LayoutSize` は**前のフレームの描画**の値。コンテナが大きさを変えたフレーム（画面の回転・隣の部品の出し入れ）は、背景（その場の大きさ）と中身（前の大きさ）が 1 フレームずれ、次のフレームで作り直す（`_layoutDirty` の流れ） |
| 最初のフレーム | 表は描画で作るので、グラフの最初のフレーム（Play の開始・生成した直後）は `HasLayout = false`。**レイアウトを 1 度読めるまで描かない**（格子線・線・棒・吹き出し・ハンドル・空の文字を出さない。位置の計算は Sprite の大きさで進め、目盛りの文字のノードは先に作っておく）。待つのは最大 `ChartSizing.DefaultMaxLayoutWaitFrames` = 3 フレーム（`ChartLayoutWait`）で、超えたら Sprite の大きさで描く。待つ間は `Redraw.Request()` で次のフレームを回す（on_demand でも止まらない） |
| 決めた理由 | Sprite の大きさで 1 フレーム描くと、伸ばされた・縮められたグラフは最初のフレームだけ中身が背景からはみ出す（右が見切れる・右が空く）。空の背景を 1 フレーム見せる方が目立たない（最初のフレームは目盛りの文字もまだ無い＝従来も中身が揃うのは 2 フレーム目）。普通は次のフレームで読めるので待ちは 1 フレーム。上限は 3D ワールドキャンバスの下など表に載らない所のため（そこでは最初の 3 フレームだけ出ない）。1 度描き始めたら待ちに戻らない |
| 読む頻度 | 毎フレーム読む（大きさが変わった次のフレームに追従する）。費用は §3.1 の末尾（行の多いギャラリーで PC の最適化なしの exe で 1 フレーム約 0.19 ms。フレームに 1 回の表の索引の作り直しが主で、読むスクリプトの間で共有。on_demand の止まっている間は 0） |

## 3. 描き方（SEED.Draw と Rust への足し分）

線・面・点・棒・格子線は `SEED.Draw`（イミディエイトモード）で、グラフの子の `Ink` の座標空間（面の左上が原点・dp）へ積む。レイヤーはグラフの Sprite の
レイヤー（面の図形）と +1（吹き出し・日付線のハンドル）。同じレイヤーでは「スプライト → 図形 → 文字」の順なので、目盛りの文字は線の上に出る。

### 3.1 判断と数値（SEED.Draw だけでは足りなかった所）

| 要件 | 既存の SEED.Draw | 足したもの（Rust・C#） |
|---|---|---|
| 線の太さの dp | 太さは描画空間の単位＝dp のキャンバスでは dp（**足りた**） | なし |
| アンチエイリアス | フェザーが描画空間の **1 単位**（dp のキャンバスの 2.625 倍の端末では 2.6 画素ににじむ） | 見た目の拡張 `DrawStyle.PixelFeather`: フェザーを**画面の 1 画素**にする（`pass.rs` の `pixel_feather_units`: 座標空間の行列とスクリーンスペースの行列を同じ射影で比べ、1 画素が何単位かを求める）。既定（拡張なし）は従来どおり |
| 線の下の塗りのグラデーション | 単色だけ・多角形は耳刈り（365 点の面は O(n²) 以上） | `Draw.Area`（`PrimitiveKind::Area`: 折れ線と基準線の間を縦の台形の帯で塗る。点の数に比例）と `DrawStyle.WithLinearGradient`（頂点の色の線形補間。線形のグラデーションは頂点の色だけで正確） |
| 性能（365 点の折れ線） | 線分ごとの四角形を耳刈り＋全部の辺にフェザー、つなぎごとに 8 角形の円を耳刈り（1 点あたり約 44 枚の三角形と配列の確保） | **軽い三角形分割**（拡張つきの図形だけ）: 折れ線は線分ごとの帯（6 枚）＋曲がる外側だけの丸いつなぎ（弦のずれ 1/4 画素から 1〜8 枚）、凸の塗り（円・角丸の棒）は扇。365 点の折れ線の三角形 **15,986 → 4,757** |
| 画面のスタックの中 | 図形は `layer_bias`（W2-7）の底上げを受けない（積んだ画面の背景の下に隠れる） | 座標空間の持ち主のノードの底上げを図形のレイヤーに足す（`apply_space_layer_bias`。底上げ 0 の既存のシーンは同じ並び） |
| 点の数の上限 | 1 図形 1,024 点 | 足さない（`LinePath.Chunk` で 1,024 点ごとの塊に分け、隣と 1 点重ねる） |

**計測**（2026-09-28・PC・`runtime/target/debug`〈最適化なし〉・RTX 3060 Laptop・540×1200・毎フレーム描く〈continuous〉・IPC の `PROFILE_DUMP` 3 秒。
「UI」はフレームの中の `描画/UI 描画順の統合・GPU 積み込み`〈図形の三角形分割を含む〉）:

| 場面 | 軽い三角形分割の前: フレーム / UI（ms） | 後: フレーム / UI（ms） | SEED.Draw の図形の数 |
|---|---|---|---|
| グラフなし | 8.51 / 1.00 | 3.94 / 0.67 | 0 |
| 全期間の折れ線だけ（365 点・倍率 1） | 19.18 / 13.33 | 6.35 / 2.94 | 6（格子線 3・軸・基準線・線 1） |
| 同じ＋滑らかな曲線＋線の下の塗り | 92.05 / 85.34 | 7.39 / 3.95 | 7 |
| 6 倍（61 点・点の印あり） | 9.56 / 4.93 | 6.21 / 2.58 | 64 |
| ペナルティ履歴だけ（365 本の積み上げ・倍率 1） | 27.13 / 21.23 | 11.18 / 7.20 | 401 |
| 見本の 5 つ全部 | 46.41 / 40.52 | 18.00 / 13.56 | 約 460 |

C# の側（位置の計算 `Rebuild`・積む `Paint`）は 1 つのグラフで 0.05〜0.9 ms（`UiChartsDemo` の `chart,stats`。変わったフレームだけ計算し直す）。
最適化なしの build の値なので、配布版（release）はずっと軽い（未計測）。「グラフなし」の前の値 8.51 は計測の窓の最初のフレームの揺れを含む（最悪 49.6 ms）。

**大きさを毎フレーム読む費用**（W2 の手直し P2-4・2026-09-29・PC・`runtime/target/debug`〈最適化なし〉・540×1200・continuous・何もしない 5 秒の `PROFILE_DUMP`。
行の多いギャラリー〈`ui_gallery.scene`。折れ線 1・棒 1〉）: `スクリプト/Update/LineChart` は変更前（HEAD の DLL）0.074 ms → 変更後 0.259 ms（+0.185）、
`Update/BarChart` は 0.035 → 0.045 ms（+0.010）。フレームで最初に `HasLayout` を読むとエンジンがレイアウトの表の索引（Entity → 行。表の行数に比例）を作り直すので、
先に読んだ折れ線がそれを払い、後の棒は引くだけ。探りのデバッグの命令で測ると、索引を作る最初の読み出しは 149〜270 µs、2 回目は 2〜4 µs。
グラフは 2 回とも作り直しの回数が増えない（大きさが揺れない）。最適化した .so（実機）ではずっと小さい見込み（未計測）で、render_policy: on_demand の
止まっている間はフレームが回らないので 0。**読む頻度は下げていない**（下げると、コンテナ・画面の回転で大きさが変わってから追従するまでが延びる）。
索引を表と一緒に持ち越す（Rust）と最初の読み出しの分も消える（docs/backlog.md の「W2 の手直し P1 の残り」の (6)）。

### 3.2 見た目の値

| 図形 | 値（トークン） |
|---|---|
| 線 | 太さ `size.chart_line` 2・系列の色 `color.chart_series_1〜4`・画面の 1 画素のアンチエイリアス |
| 点の印 | 半径 `size.chart_dot` 2.5。点の間隔（X の中央値 × 今の倍率の 1 単位の長さ）が `size.chart_dot_min_spacing` 6 未満なら打たない（全期間を縮めると点が詰まる。倍率だけで決まるのでパンで点が出たり消えたりしない） |
| 選んだ点 | 縦の案内線（日付線。`color.chart_reference`）と半径 `size.chart_dot_selected` 5 の点 |
| 日付線のハンドル（P2-4） | 直径 `size.chart_handle` 18 の丸。塗りは選んだ系列の色、縁は面の色 `color.surface`（太さ `size.chart_handle_border` 2。同じ色の線・点の上でも見分けられる）。丸の下端が面の下の縁（横軸の線）に乗る。レイヤー +1（吹き出しと同じ） |
| 滑らかな曲線 | 単調な 3 次補間（Fritsch–Carlson。点と点の間で行き過ぎない）。区間を横の間隔 ÷ `size.chart_smooth_step` 2 に分ける（上限 16。横に詰まった区間は分けない） |
| 線の下の塗り | 線の色 × `opacity.chart_area` 0.35 → 基準線（面の下の縁）で透明。グラデーションの上端は見えている線のいちばん上 |
| 基準線 | 太さ `size.chart_reference` 1.5・`color.chart_reference`・線の上に左揃えの文字 |
| 棒 | 太さ = 列の幅 × `ratio.chart_bar_width` 0.7（下限 `size.chart_bar_min` 2）。先の角だけ `radius.chart_bar` 4 で丸める（弦のずれ 1/4 dp から 1〜6 本の線分。半径 0.5 未満は矩形）。積み上げの上の段だけ丸める。合計 0 の列は値の軸の幅 × `ratio.chart_empty_bar` 0.015 の高さの `color.chart_empty_bar` |
| 選んだ列 | 列の幅・面の全体の高さに `color.chart_highlight`（主の色の 12%） |
| 格子線・軸の線 | 値の目盛りの位置に `color.chart_grid`（`size.chart_grid` 1）。軸の線は値の 0 側の縁（縦のグラフは下・横の棒は左）に `color.chart_axis` |

## 4. 目盛り・範囲・書式

### 4.1 目盛り（`ChartTicks`・`ChartAxis`）

| 軸の書式 | 刻みの選び方 | 例 |
|---|---|---|
| 数（Number） | 1・2・5 × 10^n のうち、幅を区間の上限以下に分ける最小のもの（`NiceStep`） | 0〜1,340 を 4 区間以下 → 500 |
| 時刻（TimeOfDay） | 15・30・60・120・180・360・720 分から、幅 ÷ 刻み ≤ 区間の上限の最初のもの（Flutter 版の `_clockInterval` と同じ。どれでも多すぎれば最後の候補の切りの良い倍数） | 2 時間 → 30 分、1 日 → 6 時間 |
| 日付（Date） | 1・2・3・7・14・30・61・91・182・365 日から同じく | 30 日を 6 区間 → 1 週、1 年を 6 区間 → 61 日 |

- 候補はインスペクタの `XSteps`・`YSteps`（"1,2,3,6" など）で差し替えられる（データ駆動）。
- 区間の上限: 横の軸 = 面の幅 ÷ `size.chart_x_label_spacing` 48、縦の値の軸 = `count.chart_y_intervals` 4 と 面の高さ ÷ `size.chart_y_label_spacing` 32 の小さい方、
  縦の列の軸（横の棒）= 面の高さ ÷ 文字 1 行（文字の大きさ × 1.6）。
- 位置 = 起点 + 刻みの倍数（範囲の中・両端を含む）。**日付の軸の起点はデータの最初の日**なので、パンで見える範囲が動いても目盛りは同じ日に付いたまま（跳ねない）。
  丸めの誤差（0.30000000000000004）は刻みの倍数へ寄せる。

### 4.2 範囲（`ChartAutoRange`・`ChartViewport`）

| 軸 | 決め方 |
|---|---|
| X（列） | `FixedXRange`、無ければデータの最初〜最後（1 点なら ±0.5。棒は列の幅の半分ずつ外）。見える範囲はパンとズーム（§5） |
| 値（時刻の既定） | 上下 30 分の余白・最小 2 時間の幅（中央から広げる）・0〜1440 分へ**幅を保って**ずらして収める。値が無ければ 4:00〜12:00（Flutter 版と同じ。Flutter 版は切っていたので 0:10 の朝が多いと幅が 2 時間より狭くなった） |
| 値（数の既定） | 上下に幅の 5% を足し、切りの良い刻みの倍数へ外向きに丸める |
| 値（棒） | 0 から（0 の側に余白を足さない）、上に 5% を足して切りの良い刻みへ丸める |

値の範囲は**全部のデータ**から決める（見える範囲からではない）ので、パンで縦の目盛りがぶれない。`ValueRangeOptions`（`AutoRangeOptions`）・`FixedValueRange` で差し替える。
基準線の値も範囲に含める（Flutter 版と同じ）。

### 4.3 書式（`ChartFormat`）

| 書式 | 文字 |
|---|---|
| 時刻 | `H:mm`（7:05）。`TimeOfDay(m, padHour: true)` で `HH:mm`（07:05）。0〜1440 分へ収め、1440 は `24:00` |
| 日付 | `M/d`（9/28）。日の番号の範囲の外は `-` |
| 数 | 3 桁区切り。小数の桁は刻みに合わせる（刻み 0.25 → 2 桁）。-0 は 0 |

吹き出しの既定の文字は「X の書式 値の書式」（`9/28 7:05`、積み上げの棒は `9/28 120 + 50`）。`TooltipFormatter` で差し替える。

## 5. パンとズーム

| 操作 | 振る舞い |
|---|---|
| 横のドラッグ（W2-2 の横だけのドラッグ） | 指の下の値が指に付いてくる（右へ引くと前の日）。端で止まる（跳ね返りは無い） |
| 払う（フリック） | 指の速度から慣性（Flutter の `FrictionSimulation` と同じ式: 1 秒で速度が `ratio.chart_fling_drag` 0.135 倍。`speed.chart_fling_stop` 20 dp/秒で止まる。時刻の閉じた式なのでフレームの刻みに依らない）。端に着いたら止まる。触れると止まる |
| 2 本指のピンチ | 倍率 = 始めたときの倍率 × `e.Scale`。始めたときのフォーカス（2 本の指の中点）の下の値を、今のフォーカスの位置へ置く（ズームと 2 本指のパンが同時に効く） |
| ± のボタン（`ZoomIn`・`ZoomOut`） | 真ん中を中心に `ratio.chart_zoom_step` 2 倍・½ 倍。`motion.chart_zoom` 0.25 秒で倍率の対数を補間し、終わりはちょうどの倍率。上限・下限で押せなくする |
| 範囲を見せる（`ShowRange`）・右端へ（`ScrollToEnd`） | スクリプトから。倍率の上下限と端で収める |

- 倍率は 1（全体）〜 `MaxZoom`（既定 6 = Wake or Pay の「ピンチ 1〜6 倍」）。データが増えたとき、右端を見ていれば右端に付いたまま（見えていた幅を保つ）。
- **パンできないとき（`Interactive = false` か倍率 1）はドラッグ・フリックを受けない**（`CanvasGesture.Drag`・`Fling` を外す）。縦の一覧・横のページ送りへ指を渡す。
  ピンチは `Interactive` のときだけ。
- 入れ子: 縦のスクロール（`CanvasScroll`）の中のグラフは、最初の動きの向きで持ち主が決まる（W2-2 の R4。UC-3）。縦の一覧がスクロールしている指では
  ピンチにならない（[input_gestures.md](input_gestures.md) §2.6）。
- 横の棒（`Orientation = Horizontal`）は列の軸が縦なので、パンとズームは縦（ドラッグの軸も縦）。

## 6. 吹き出しと選ぶ

| 部品 | タップ・長押し | 吹き出し |
|---|---|---|
| `LineChart` | 押した位置から横の距離が `size.chart_touch_slop` 24 dp 以内で最も近い点（同じなら縦の距離が近い方。複数の系列から）。近くに点が無ければ選びを外す | 点の上（入らなければ下）。左右はグラフの枠の中へ収める |
| `BarChart` | 押した位置の X の列（列の幅の中なら**縦は問わない＝空の高さまで当たり**。Wake or Pay の「列全体が当たり判定」）。列の外を押しても選びは変えない（Flutter 版と同じ） | 棒の先（空の棒は最低の高さ）の上 |

- 選んだ点・列がパンで見える範囲の外へ出たら吹き出しを隠す（選びは保つ。戻れば出る）。
- 吹き出しの大きさは文字の見積もり（`DialogLayout.EstimateWidth`＝組み込みの書体の送り幅の表。2026-09-29 の W2 の手直し P2-1 で文字の幅にほぼ合うようになり、
  以前より小さくなった。W2-6c の `Text.Measure` へ替える）。面は `color.inverse_surface`・`radius.chart_tooltip` 8、
  文字は `color.on_inverse_surface`・`text.chart_tooltip` 12（W2-7 のトーストと同じ inverse surface）。
- イベント: `LineChart.PointSelected(chart, series, index)`（外したら −1, −1）・`BarChart.BarSelected(chart, index)`。スクリプトから `Select` で選べる（`notify` で出す）。

### 6.1 日付線のハンドル（折れ線。W2 の手直し P2-4・2026-09-29）

利用者の要望（2026-09-29 の実機の確認）: 「日付線の下部に丸いハンドルを用意して、そこを押して左右に動かすと日付線だけを動かせるといい」。

| 項目 | 規則 |
|---|---|
| 作り | プレハブの子 `Handle`（§2）: Sprite〈楕円〉・`CanvasGesture`（`drag` = true・`drag_axis` = horizontal・`fling`・`tap`・`long_press`・`pinch`・`press_feedback` = false・`min_hit_size_dp` 48）・`SEED.UI.GestureRelay`（子のノードのドラッグをグラフの部品へ渡す）。はじめは非表示。子 `Handle` が無いプレハブ（利用者が作った古いもの）ではハンドルを出さず今までどおり。見本の折れ線（`ui_charts.scene` の WakeWeek・WakeHistory、`ui_gallery.scene` の GalleryLine）はプレハブの参照ではなく中身の写しなので、同じ `Handle` を足した（テストがプレハブと同じかを確かめる） |
| 出すとき | 選んだ点があり、日付線が面の中に見えている間だけ（`ShowTooltip`・データあり。吹き出しと同じ半単位の許容 `ChartLayout.InsidePlot`）。パンで外へ出たら隠す（選びは保つ）。最初のレイアウトを待つ間（§2.1）も出さない |
| 置き場 | 丸の中心の X = 日付線の X、丸の下端 = 面の下の縁（中心の Y = 面の下端 − 半径。`ChartLayout.HandlePosition`）。**理由**: 横軸の文字の行（面の下）に重ならず、日付線の下の端に付いて見え、グラフの子（面の切り抜きの外）なので面の端でも切れない。左端の点では丸の左半分が縦軸の列へ、右端の点では右の余白（8 dp）から 1 dp 出る |
| 見た目 | 塗り = 選んだ系列の色、縁 = 面の色（§3.2・§7）。レイヤー = 吹き出しと同じ手前（+1）。木の順は Tooltip の直前なので、重なれば吹き出しが上（同じレイヤーでは文字がスプライトより手前に描かれるため、吹き出しの文字だけが上に出る食い違いを避けた）。見せる・隠す・動かす・色は値が変わったときだけ FFI で書く（`ChartHandle`） |
| 当たりとアリーナ | 当たりは 48 dp（見た目 18 dp を中心をそろえて広げる。[input_gestures.md](input_gestures.md) §3）。ハンドルは葉なので、同じ横の移動ではグラフのパンより先に指を取る（§2.2 の規則 4）。縦の移動は取らない＝縦のスクロールへ渡る。倍率 1・`Interactive = false` でグラフがドラッグを受けないときも効く。タップ・長押しは受けない（グラフのタップ＝最寄りの点の選びへ届く） |
| 指 → 面の中の X | 指の X（グラフの単位）= ハンドルの左上（前のフレームに書いた位置）＋ イベントの `LocalPosition.x`、面の中の X = それ − 面の左（`ChartLayout.HandleFingerPlotX`）。**`LocalPosition` を選んだ理由**: エンジンがジェスチャーを配る前に、そのときの World（＝前のフレームに書いたハンドルの位置）でノードの行列の逆を求め、ノードの単位（dp）へ直した値（`hit_slop.rs` の `local_position_in_units`・`gesture_events.rs`）なので、dp のキャンバス・祖先の `Scale`・`VisualScale` の下でもグラフの単位と一致する。`DeltaDp`・`TotalDelta`（画素）は倍率の下でずれ、`Position` は画面の矩形（1 フレーム遅れの `LayoutRect`）との換算が要る。ハンドルは指に付いて動くが、位置を書くのは毎フレームの `Paint` だけ（イベントの中では書かない）なので、イベントの座標の原点と覚えた位置が食い違わない |
| 吸い付き | 選んでいる系列の、値のある点のうち見えている範囲（端の半単位を含む）の中で、X の距離が最小の点（縦の距離は見ない・距離の上限なし・同じ距離なら小さい添字。`ChartHit.NearestValuedX`）。日付線・大きな点・吹き出し・ハンドルはその点へ付いてくる。指が面の外（左右）へ出たら見えている範囲の端の点で止まる（自動のパンは無い＝backlog）。押した瞬間はハンドルの中心を押していなくても指の X で決める（slop の 8 dp を超えた所で最寄りの点） |
| 知らせと触感 | 点が変わるたびに `LineChart.PointSelected` とログ `[UI] chart: line handle …`。軽い触感 `Haptics.Tap`（`HandleHaptic`。既定 true・1 フレームに 1 回まで＝ドラッグの始まりと途中が同じフレームでも 1 回）。選びだけが変わるので位置の計算（`Rebuild`）はやり直さず、描き直しだけ頼む |
| 離す・取り消し | 離しても**取り消されても選びは残す**（戻さない）。取り消しは 2 本目の指のピンチ・OS の横取り・背面へ回るなど選びと関係の無い理由で来るうえ、途中の点ごとに `PointSelected` を出し済みで、戻すともう 1 度知らせて吹き出しが跳ねるため（パンの取り消しも見える範囲を戻さないのと同じ） |
| その他 | 引き始めに慣性・± の動きを止める（指の下で面が流れない）。引いている間は `Redraw.Request()` で描き続ける。引いている間にハンドルが隠れた（データの差し替えで選びが消えた・別の指のパンで外へ出た）らその指のドラッグは終える。棒グラフ（`BarChart`）はハンドルを出さない（土台の `HandleAnchor`・`SelectNearestX` を上書きすれば足せる。backlog） |

## 7. テーマのトークン（`ChartTokens`。既定の値は `default_theme.json`）

グラフのトークン（系列の色・格子線・軸・目盛りの文字・基準線・空の棒・選んだ列の強調・吹き出し、線・点・枠の割り付け・当たり・棒の大きさ、角丸、文字、
棒の割合・慣性・± の倍率、塗りの濃さ・動き・縦軸の区間、日付線のハンドルの直径 `size.chart_handle`・縁 `size.chart_handle_border`〈P2-4〉）の
**一覧（名前・型・既定値・使う部品）の正典は [ui_theme.md](ui_theme.md) §8**（W2-9 で 1 つの表にまとめた）。ハンドルの縁の色は面の `color.surface`（`ChartTokens.ColorHandleBorder` は別名）。
値の出典: 線の太さ 2・点の半径 2.5・基準線 1.5・縦軸の文字の列 46・横軸の文字の行 26 は Flutter 版の wake_time_chart.dart、棒の太さ 0.7・空の棒の高さ 0.015・
最小の太さ 2・選んだ列の強調（主の色の 12%）は penalty_bar_chart.dart、慣性の減速 0.135 は Flutter の BouncingScrollSimulation、系列 2 は Wake or Pay のカードの赤。
ハンドルの直径 18 は利用者の要望の案（backlog: 見た目 16〜20 dp）の中ほどで、スライダのつまみ（`size.slider_thumb` 20）より一回り小さくして面の下の線と点を隠しすぎない。
縁 2 は折れ線の太さと同じ（どちらも決めた値。当たりはプレハブの 48 dp）。

テーマを差し替えると（`UiTheme.Apply`。W2-9）その場で見た目の値を読み直し（`ChartLook.From`）、次の更新で位置を計算し直す。グラフの面の色は部品が当てないので、
プレハブ（`line_chart.actor`・`bar_chart.actor`）の根に `ThemeStyle`（`color.surface`）を付けた（W2-9）。

## 8. 見本（`templates/ui/scenes/ui_charts.scene`）

ルートは dp。画面の幅と安全領域に合わせる（W2 の手直し P2-5。それまでは 540×1200 dp 固定の絶対配置で、Pixel 6a では右が切れ見出しがステータスバーに重なった）:

```
UiCharts（dp のルート・UiChartsDemo）
├─ Background（親いっぱい。画面の端まで塗る）
└─ Body（CanvasComponent・親いっぱい・CanvasSafeArea 4 辺・縦の CanvasStack〈cross_align stretch〉）
    └─ Page（flex 1・切り抜き・縦のスクロール。中身の大きさは auto＝中身の並びに合う。窓そのものが縦の Stack〈cross_align stretch〉）
        └─ Content（縦の Stack〈余白 16・間隔 16〉。幅は窓の Stack が渡す）
            ├─ Title
            └─ WakeWeekSection・WakeHistorySection・MonthlySection・PenaltySection・WeekdaySection
                （縦の Stack・間隔 4: 見出し ＋ グラフ。グラフの幅は親に合わせる〈LayoutSize を読む。§2.1〉・高さは Sprite のまま）
```

`WakeHistory` の ± のボタン（`ZoomOut`・`ZoomIn`）は anchor x 1（グラフの右の端に付く）・位置 x −84・−40・y −34（見出しの行の右）。コンテナが置いたグラフの子の
anchor の基準は伸ばされた矩形なので、グラフの幅が変わっても右の端から同じ位置に来る。`scripts/UiChartsDemo.cs` がサンプルデータ（種の固定の乱数）を入れる（名前で引く）。

| ノード | 中身 |
|---|---|
| `WakeWeek` | 起床時間の遷移（30 日・固定・滑らかな曲線・線の下の塗り・平均の基準線・7 日に 1 日の記録なし。点を選ぶと日付線のハンドル〈P2-4〉） |
| `WakeHistory` | 起床時間の全期間（365 日・12% の記録なし・パン・ピンチ・± のボタン。吹き出しは `M/d HH:mm`。日付線のハンドル〈P2-4〉） |
| `MonthlyPenalty` | 月ごとの寝坊ペナルティ（12 か月・コインとカードの積み上げ・固定・横軸は「9月」） |
| `PenaltyHistory` | ペナルティ履歴（365 日・積み上げ・0 の日は最低の高さ・パンとピンチ・最後に何か失った日を選んでおく） |
| `WeekdayBars` | 曜日ごとの寝坊（横の棒・固定） |

デバッグの命令（`SCRIPT_DEBUG:chart,<名前>[,<値>]`）: `stats`（倍率・見える範囲・選び・作り直しの回数・図形の数・時間）・`zoom <グラフ>,<倍率>`・
`show <グラフ>,<最初からの日数>,<日数>`・`select <グラフ>,<添字>`（折れ線は値のある点なら日付線のハンドルも出る）・`clear <グラフ>`・`mark <文字>`・`only <グラフ|none|all>`・`style <グラフ>,<滑らか 0/1>,<塗り 0/1>`。
見本のシーンの配置は P2-4 では変えず、W2 の手直し P2-5 で上の作り（画面の幅と安全領域に合わせる）にした。
テンプレートライブラリの「UI 部品」（`ui` のフォルダ）からプロジェクトへ取り込むと `assets/ui/...` になる（`LabelPrefab` の既定 `assets://ui/prefabs/chart_label.actor`）。

## 9. 検証（2026-09-28・PC）

- **単体テスト（C#）** `dotnet run --project editor/tests/UiComponentsTests`（70 件。うち W2-8 の 19 件）: 目盛りの自動（数の 1・2・5〈500 通りの幅で区間の数が上限以下〉・
  時刻と日付の候補・Flutter 版の刻みと一致）・目盛りの位置（起点・丸めの誤差・上限）・範囲の自動（時刻の余白と最小の幅と幅を保つずらし・棒の 0 から）・書式（時刻・24:00・
  日付・数の桁）・値 ⇔ 位置の往復（上下逆・幅 0）・パンとズーム（倍率 1〜6・端で止まる・フォーカスの下の値・ピンチの錨・範囲を見せる・データが増えたら右端・
  1.0000001 倍はパンしない）・慣性（止まる時刻と距離・60 fps と 30 fps で同じ位置）・単調な 3 次補間（点を通る・ランダムな 40 点と段と起床時間で行き過ぎない・
  山と谷の接線 0・詰まった点は分けない）・当たり（横の距離・同じなら縦・棒の列）・棒の形（太さ・積み上げ・角丸の輪郭と弧の分け方）・枠と吹き出しの置き場・線の点（見える範囲 ± 1 点・
  欠けた値・塊）・軸の文字・テーマの値。
- **単体テスト（Rust）**: ピンチ（`cargo test -p SEED --lib -- gesture`: 距離の変化が slop を超えたら始まる・倍率は始めたときからの比・ドラッグを取り上げる・外側の縦の一覧が
  取っている指では始めない・ピンチを受けないノード・3 本目の指・取り消し・「動いている」・縦に並んだ指の倍率）、図形（`-- primitive tessellate`: 見た目の拡張の読み方・
  グラデーションの色・画面の画素のフェザー・`Area` の面積と帯・基準線をまたぐ線分・軽い三角形分割の面積と三角形の数・つなぎの扇の分け方・凸の扇と凹の耳刈り・
  レイヤーの底上げ）、FFI の構造体の大きさ（22 × 4 バイト）。
- **見本**（`templates/ui` を作業フォルダのプロジェクトの `assets/ui` へ写し、PC の Play〈540×1200・on_demand〉を IPC の入力の注入で操作。ログ `[UI] chart:`）:
  30 日の折れ線の 10 日目（記録なし）をタップ → 隣の 9/8 を選び吹き出し「9/8 06:20」、全期間の + を 2 回 → 倍率 2（見える範囲 91〜273 日）→ 4（136.5〜227.5）、
  左へ 200 dp ゆっくり引く → 40.09 日動く（200 × 91 / 454）、右へ払う → 慣性で流れて左端で止まる（0〜91）、真ん中の点をタップ → 「11/14 06:46」、
  大きく引いて選んだ点を外へ → 吹き出しが隠れる、ペナルティ履歴の面の上の端（棒の無い高さ）をタップ → その列（12/29・合計 0）を選び吹き出し、
  縦に引く → ページがスクロールしグラフは動かない（見える範囲そのまま）、− を 2 回 → 倍率 1 に戻り（`canPan=False`）、倍率 1 で横に引いても動かない。
  `SEED_SIM_SCALE_FACTOR=1.5`（窓 810×1800）でも線・点・吹き出し・文字が dp の倍率どおりで、縁は画面の 1 画素のアンチエイリアス。
  ピンチは PC の入力の注入が 1 本の指（マウス）だけなので、単体テスト（Rust のアリーナ・C# の `ChartViewport.ApplyZoom`）で確かめた。
- **性能**: §3.1 の表。
- **回帰**（WarashibeFishing の複製。変更前の SEED.exe と SEEDScripting.dll〈HEAD〉で撮った基準と比べた）: 図鑑の画面 3 フレーム × 2 回とも差 0 画素、
  図鑑のボタンの縁の 56 点のクリックは当たり 34・外れ 22 で、各クリックの後の画面まで基準と一致。

**W2 の手直し P2-4（2026-09-29・PC）**: 単体テスト `UiComponentsTests` 121 件（うち P2-4 の 6 件: 大きさの選び方〈LayoutSize・Sprite・壊れた値〉、
最初のレイアウトの待ち〈次のフレームで読める・上限 3 フレーム・戻らない〉、ハンドルの吸い付き〈X だけ・値の無い点を飛ばす・同じ距離・範囲の外は端の点・
点 0 個と 1 個・同じ X の並び・30 日の見本の並びで 1 点ずつ〉、置き場と指の X、トークン、プレハブと見本の折れ線 3 つの `Handle` の作り）。
PC の確かめは作業フォルダの写しのプロジェクト（`templates/ui` の写し＋`ui_charts.scene` を縦の `CanvasStack`〈cross_align stretch〉と fill_width の下に
組み直した写し。グラフの Sprite の幅はわざと 300）を `SEED.exe` で開き、IPC の注入で操作した（起動 5 回。撮影は `tmp/w2_fix2/item4/shots/`）:

- 窓 540: 5 つのグラフが Sprite の幅 300 ではなく伸ばされた 508 dp（面 454）で描かれた。411 dp の模擬（`SEED_SIM_SCALE_FACTOR=1.3139`）では 379 dp（面 325）で、
  右の端は画面の 519 px（窓 540）で切れない。曜日の棒（高さ 170 の Panel の下で fill_width）も同じ。
- WakeWeek（固定・倍率 1）の 12 日目を選ぶ → 日付線の下にハンドル（面の下の縁に乗る）。ハンドルを押して右へ 3.96 日ぶん・左へ 4.98 日ぶん 2 px ずつ引くと、
  選びが 13・14・15・16 → 15・14・13・12・11 と 1 つずつ変わり、触感 9 回（`moves=9 haptics=9`）。離した後も 11 のまま。411 dp の模擬でも同じ 9 回。
- 見た目の倍率 0.8（中身の `CanvasLayoutItem.VisualScale`）の下でも、1 日が 12.52 px に縮んだぶんだけ引くと同じく 1 つずつ 9 回（`DeltaDp` で換算していたら 3 回になる）。
- 右の端の近く（26）から面の右の外（面の右の端 516 px より右の 529 px）まで引くと 29 で止まり、左の外（29 px）まで戻すと 0 で止まった。
- WakeHistory の倍率 1（パンできない）と 4 倍（パンできる）の両方で、ハンドルのドラッグは選びだけが変わり見える範囲は動かない（4 倍: [136.5, 227.5] のまま）。
  4 倍でハンドルの外の横のドラッグ 60 px はパンになった（[124.47, 215.47]。60 × 91 / 454 = 12.03 日）。ハンドルは選んだ点に付いて一緒に動いた。
- 411 dp の模擬で、ハンドルの上から縦に 150 px 引くとページがスクロールし（グラフの矩形の上が 100.9 → −38.6 px）、選びは変わらない（ハンドルのドラッグは始まらない）。
- ギャラリー（行の多いシーン）で大きさを毎フレーム読む費用: §3.1 の末尾。

**W2 の手直し P2-5（2026-09-29・PC）**: 見本（§8）を画面の幅と安全領域に合わせる作りにした後、作業フォルダの写しのプロジェクト（`templates/ui` の写し＋
探りのスクリプト）を `SEED.exe` で開いて撮影した（`tmp/w2_fix2/item5/shots/c540`・`c411`）:

- 窓 540: 5 つのグラフが 508 dp（面 454）で右の端 524 px。中身は 1,176.4 dp で画面に収まる（スクロールしない。以前は中身の高さを 1,300 に固定していた）。
  ± は − 440〜476・+ 484〜520 px（変更前と同じ位置）。
- 411 dp の模擬（`SEED_SIM_SCALE_FACTOR=1.3139`・`SEED_SIM_SAFE_AREA=0,32,0,21`）: グラフは 378.99 dp（面 325）で右の端 518.98 px。ページは y 32〜1,179 px
  （ステータスバーとジェスチャーの帯の模擬の内側）で、見出しは y 53 px から。いちばん下までスクロールした曜日の棒の下の端は 1,158.0 px。± の + の右の端は 513.72 px
  （グラフの右の端 − 4 dp）。撮影の右の余白の列（x ≧ 520）・上の 31 行・下の 20 行は背景の色だけ（違う画素 0）。
- 両方で: WakeWeek の点を選ぶと日付線のハンドルが面の下の縁に出る（411 dp で丸の下の端 327.36 px ＝ 面の下の端）、± のタップで倍率 1 → 2 → 4 → 2 → 1、
  WakeHistory の選択。`[UI]` のログに警告・例外なし。

## 10. 実機での確かめ方（Pixel 6a。W2-8 の時点で未実施）

利用者と一緒に行う。見本のプロジェクト（`templates/ui` を `assets/ui` へ写し、`start_scene` を `assets://ui/scenes/ui_charts.scene`、`render_policy: on_demand`）を
SeedAndroid の `run` で入れ、`[UI] chart:` を logcat で見る。

1. 2.625 倍の画面で線・点・棒の縁がにじまない（画面の 1 画素のアンチエイリアス）・目盛りの文字が読める（11 dp）
2. 全期間の折れ線を 2 本の指で広げる → 倍率が上がり、指の間の日がそのまま指の下に残る。すぼめる → 1 倍で止まる。6 倍で止まる
3. 横に払う → 慣性で流れて端で止まる。縦に払う → ページのスクロール（斜めの指で取り違えない。UC-3）
4. 点・列のタップで吹き出し、長押しでも出る。ペナルティ履歴の細い列（1 倍で 1.2 dp の間隔）を指で選べるか（列の幅が狭すぎる所は拡大してから選ぶ）
5. `SEED.Time.Fps`・`chart,stats` でパン・ピンチの間の 60 fps（配布版の .so で。最適化なしでは 365 本の棒の積み上げが重い〈§3.1〉）
6. （W2 の手直し P2-4）点を選ぶと日付線の下にハンドル。ハンドルを押して左右へ動かす → 日付線・大きな点・吹き出しが最寄りの点へ吸い付いて 1 点ずつ動き、
   点が変わるたびに軽い触感（`[UI] chart: line handle …` のログと手触り）。4 倍に拡大した全期間で、ハンドルを引いてもパンにならない・ハンドルの外を引くとパンになる。
   ハンドルの上から縦に払う → ページがスクロールする。ハンドルを引きながら 2 本目の指でピンチ → ハンドルのドラッグが取り消され（`handle end (canceled)`）選びは残る。
   丸の大きさ（18 dp）・当たり（48 dp）・面の下の縁に乗せた置き場が指で押しやすいか。画面の幅に合わせた見本（P2-5 の後）で右が切れないか

**2026-09-28 の実機の回（roadmap §3.9）**: ページの縦のスクロール中、dev の .so は 19〜24 fps（「描画/UI 描画順の統合・GPU 積み込み」が 30.4 ms＝73%）、
release の .so は 59.3〜60.0 fps（同 3.72 ms＝40%。5 つのグラフを `chart,only,none` で隠すと 0.39 ms・GPU 7.6 → 5.4 ms）。毎フレームの `SEED.Draw` の積み直しと
三角形分割が主（backlog。同じ日の夕に三角形分割の使い回しと画面の外の図形の省略を入れた。結果は roadmap §3.9.1）。利用者が合間に試したピンチ（release）は倍率 1.41 → 4.84 → 6 で上限に止まり、すぼめて 1 で止まった。棒のタップで吹き出しが出た
（ログのみ。中点の固定・縦のスクロールとの取り合い・手触りの感想は未確認）。見本のルートが 540 dp 固定で、Pixel 6a では右端が切れ、見出しがステータスバーに重なる。

**2026-09-29 の手触りの確認（roadmap §3.9.2）**: 2〜4 は期待どおり（利用者: 拡大・縮小「問題なし」・横と縦の取り合い「大丈夫」・吹き出し「出る」）。倍率は 1 と 6 で止まり、
指の中点の値は続けたピンチ 6 組中 4 組で予測との差 ±5 dp 以内（生の指の座標で照合）。要望: 右の見切れ（大きさは Sprite から読むのでコンテナに伸ばされても追従しない）と
日付線のハンドル（backlog）。1 の縁の目視と 5 の計測は行っていない。

## 11. 制限と持ち越し

- ~~**グラフの大きさはノードの Sprite の幅・高さ**~~ → **2026-09-29 の W2 の手直し P2-4 でレイアウトの大きさ（`LayoutSize`）を読むようにした**（§2.1）。残り:
  コンテナが大きさを変えたフレームは背景と中身が 1 フレームずれる（`LayoutSize` が前のフレームの描画の値のため）。最初のフレームは描かない（3D ワールドキャンバスの下では
  最初の 3 フレーム）。見本のシーン（ページが 540 dp 固定・グラフは固定の位置）の画面の幅に合わせる作り直しは P2-5（WakeHistory の ± のボタンは固定の位置〈x 424・468〉
  なので、グラフが細くなると右の外へ出る。右の端に寄せる置き方が要る）→ **P2-5 で直した**（§8。± は anchor x 1 で右の端に付く）
- **日付線のハンドルは折れ線だけ**（棒グラフは付けていない。土台の `HandleAnchor`・`SelectNearestX` を `BarChart` で上書きすれば足せる）。**面の端へ寄せても自動でパンしない**
  （見えている範囲の端の点で止まる。拡大しているときは、ハンドルを離してパンしてから続ける）。ハンドルを押しても大きくならない（押した見た目なし）。
  右の端の点ではハンドルの丸が面の右の余白（8 dp）から 1 dp はみ出す。ピンチでの取り消し・実機の手触りは未確認（§10 の 6）
- **吹き出しの大きさは文字の数からの見積もり**（W2-6c の `Text.Measure` へ替える）。長い文字・複数行の吹き出しは位置がずれうる
- **隠れた（祖先が非表示の）グラフも毎フレーム `SEED.Draw` を積む**（座標空間が解決できないので Rust 側で捨てる。FFI の呼び出しだけが残る。棒 365 本で約 0.4 ms）。2026-09-28 夕に `Paint` で描く面から根までの `Visible` を辿って省く形を試したが、`GameObject.Parent`（`ffi_parent_of`）が呼ぶたびにアクタの木全体をたどるため、見えているグラフでも 1 つ 1 フレーム約 0.02〜0.04 ms 増え（実機・develop。グラフの見本の `Update/BarChart` 0.397 → 0.473 ms・`Update/LineChart` 0.109 → 0.150 ms）、普通のスクロールで損になったので入れていない。入れるなら実効の表示を 1 回の FFI で引ける口（描画の表の `is_drawn`）を先に足す（backlog）
- **ピンチは倍率と中点だけ**（回転は無い）。ピンチを 1 本離すと残った指は何もしない（Flutter の InteractiveViewer は残った指でパンを続ける）。PC で 2 本の指の入力を注入できない
- **慣性に跳ね返りは無い**（端で止まる。W2-3 のスクロールの Bounce とは別の手触り）。払った速度は W2-2 の指の速度（Android は受け取った時刻で代用。input_gestures.md §5）
- **軽い三角形分割の折れ線はつなぎの内側で本体が重なる**（半透明の太い線ではつなぎの内側がわずかに濃い。従来の丸いつなぎと同じ性質）。つなぎは丸だけ（留め継ぎ・面取りは無い）
- **軸の線・格子線は値の目盛りの向きだけ**（縦の格子線〈X の目盛りの線〉は無い。Flutter 版も drawVerticalLine = false）。凡例は部品に無い（プレハブ・画面で置く）
- **目盛りの文字の衝突の検出は無い**（文字の最小の間隔から刻みを選ぶだけ。長い独自の文字は重なりうる）
- 最適化なしの build の値しか測っていない（§3.1）。配布版（release）・実機の値は未計測（§10）
