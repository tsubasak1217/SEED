# 形と塗り・基本の部品・時刻ホイール（W2-4・W2-5 の正典。2026-09-28）

キャンバス UI の**形と塗り**（スプライトの角丸・楕円・弧・縁の線・グラデーション・9 スライス・ぼかしの影、角丸・楕円の切り抜き、
形に合わせた当たり判定）と、**基本の部品**（`SEED.UI` のボタン・トグル・チェックボックス・スライダ・数値欄・セグメント・チップ・ラジオ・
進捗の棒と輪）、部品が読む**テーマのトークン**、**ホイールと時刻ホイール**（W2-5。§11）の規則。背景と段階は [app_platform_roadmap.md](app_platform_roadmap.md) §3.3 の
「ボタン」〜「形と塗り」「時刻ホイール」・§3.8.4（切り抜きの決定）・§3.8.5 の W2-4・W2-5。

| 置き場 | 役割 |
|---|---|
| `runtime/src/engine/components/sprite_style/` | SpriteComponent に足した 4 つの欄のデータ（`shape`・`fill`・`nine_slice`・`shadow`。既定は保存しない） |
| `runtime/src/engine/components/canvas_clip_component.rs` | 切り抜きの形（`shape`・`corner_radii`） |
| `runtime/src/engine/core/renderer/ui_shape/` | 純関数: SDF（`sdf.rs`）・グラデーション（`fill.rs`）・9 スライス（`nine_slice.rs`）・形の当たり判定（`shape_hit.rs`）・切り抜きの形と写像（`clip_sdf.rs`）・GPU のデータの組み立て（`params.rs`） |
| `runtime/src/engine/core/renderer/shaders/sprite_shape.wgsl`・`pipelines/sprite_shape.toml` | 形と塗りのシェーダーとパイプライン |
| `runtime/src/engine/core/renderer/batch2d.rs` | 既定のスプライトは従来の経路、形と塗りのスプライトは形の経路へ振り分ける（`SpriteScratch::push_items`） |
| `runtime/src/engine/core/renderer/ui_draw_pass.rs`・`ui_clip.rs` | 切り抜きの番号 → いちばん内側の形のある領域の SDF（`clip_sdf_table`） |
| `runtime/src/engine/core/canvas_layout/clip.rs`・`pass.rs` | 切り抜きの領域へ形を持たせる・当たり判定の形（`point_inside_clip_chain`） |
| `runtime/src/engine/core/input/gesture/hit_slop.rs`・`app/gesture_scene.rs`・`app/pick_2d.rs` | ジェスチャーとポインタイベントの当たり判定を形に合わせる |
| `runtime/src/engine/core/scripting/sprite_style_api.rs` | スクリプトの欄（`Sprite`・`CanvasClip`） |
| `runtime/src/engine/core/app_base/app/sprite_style_ipc.rs` | エディタの編集（`SET_SPRITE_FIELD`・`SET_CANVAS_CLIP_FIELD`）とインスペクタへ送る JSON |
| `editor/src/Panels/InspectorPanel.SpriteStyle.cs` | インスペクタ（「形」「塗り」「9 スライス」「影」の節・切り抜きの形） |
| `scripting/src/Api/Sprite.cs`・`CanvasClip.cs`・`SpriteStyleTypes.cs` | `SEED.Sprite` の形と塗りのプロパティ・`SEED.CanvasClip.Shape` |
| `scripting/src/Api/UI/Theme/` | テーマ（`UiTokens` = トークンの名前・`default_theme.json` = 既定の値の表・`UiThemeData`・`UiTheme`・`UiColorMath`） |
| `scripting/src/Api/UI/Looks/` | 状態 → 見た目と値の計算（純粋な計算。`editor/tests/UiComponentsTests` で検算） |
| `scripting/src/Api/UI/Widgets/` | 部品のスクリプト（`UiWidget` の派生。部品ごとに 1 ファイル） |
| `scripting/src/Api/UI/Wheel/` | ホイールの純粋な計算（W2-5。行の曲面の見た目 `WheelLook`・その値の組 `WheelLookParams`・循環の添字と位置 `WheelLoop`・時刻の 12/24 時間と連動と分の刻み `TimeWheelMath`） |
| `scripting/src/Api/UI/Widgets/WheelPicker.cs`・`WheelRows.cs`・`WheelFocus.cs`・`TimeWheel.cs` | ホイールの列・行の使い回しと見た目の当て方・キーボードの相手・時刻ホイール（W2-5。§11） |
| `templates/ui/` | 部品のプレハブ（`prefabs/`）・ギャラリー（`scenes/ui_gallery.scene`・`scripts/UiGalleryDemo.cs`）・画像（`textures/`） |

---

## 1. 形と塗り（SpriteComponent の欄）

長さはスプライトの幅・高さと同じ**キャンバスの単位**（dp のルートなら dp）。形は「形の空間」＝スプライトの矩形 [0, 幅]×[0, 高さ]
（レイアウトが伸ばした軸は矩形の大きさ ÷ サイズ倍率）に置くので、端末の倍率に依らず同じ形になる。角度は度（0 = +X・時計回りが正。`SEED.Draw` と同じ）。

| 欄（serde） | 既定 | 意味 |
|---|---|---|
| `shape.kind` | `rect` | `rect`（矩形・四隅ごとの角丸）/ `ellipse`（内接する楕円。正方形なら円）/ `arc`（中心の円の弧。リング） |
| `shape.corner_radii` | 0,0,0,0 | 左上・右上・右下・左下。辺より大きい指定は CSS と同じ規則で一律に縮める（同じ辺の 2 つの和 ≤ 辺の長さ） |
| `shape.border_width`・`border_color` | 0・黒 | 縁の線（形の**内側**に引く。外形は変わらない）。色は `color`（塗り）と**独立**（塗りを透明にした輪・ラジオの輪が作れる） |
| `shape.arc_start`・`arc_sweep`・`arc_thickness`・`arc_round_caps` | -90・360・8・false | 弧: 外側の半径 = 短い辺の半分、開始角から時計回りに角度だけ、太さ、端を丸く（丸い端は端点の円・切りっぱなしは端の線分） |
| `fill.kind` | `solid` | `solid`（`color` で塗る＝従来）/ `linear` / `radial` |
| `fill.colors`・`stops` | 白・黒 / 空 | 2〜4 色（足りなければ最後の色・多ければ 4 色まで）。位置は 0..1 の昇順（空・足りなければ等間隔。範囲の外は収め、逆順は直前へ揃える） |
| `fill.angle` | 90 | 線形の角度。0 = 左 → 右、90 = 上 → 下。線の長さは四隅がちょうど 0 と 1 に乗る長さ（CSS の linear-gradient） |
| `fill.center`・`radius` | 0.5,0.5 / 0.5,0.5 | 放射の中心（矩形に対する割合）と半径（幅・高さに対する割合。既定 = 内接する楕円の縁で最後の色） |
| `nine_slice.enabled`・`border`・`scale` | false・0・1 | 画像を枠の 4 辺（左・上・右・下。**テクスチャの画素**）で 3×3 に切る。描く枠の幅 = 枠 × `scale`（2 倍の画像なら 0.5） |
| `nine_slice.edge_mode`・`center_mode`・`fill_center` | stretch・stretch・true | 辺・中央を伸ばす（`stretch`）か繰り返す（`repeat`）。中央を描かない（枠だけ）なら `fill_center` false |
| `shadow.enabled`・`color`・`offset`・`blur` | false・35% の黒・(0, 4)・8 | ぼかしの影（形を `offset` だけずらし `blur` でぼかして後ろに描く）。色は `color` と独立 |

**色**: 塗りの色 = テクスチャ × 塗り（単色なら白、グラデーションなら位置の色）× `color`。グラデーションの色にも `color` が掛かる（フェードがそのまま効く）。
補間は乗算済みアルファ（透明へのグラデーションで色が濁らない。CSS と同じ）。SEED の色は線形の値（エディタの色の選び方と同じ）。

**既定の約束**: 4 つの欄がすべて既定（直角の矩形・縁なし・単色・9 スライスなし・影なし）のスプライトは、従来のパイプライン（`sprite.wgsl`）で
**今と画素単位で同じ**に描く。既定の欄は `.scene`・`.actor` に書き出さない（既存のシーンを保存し直してもファイルは変わらない）。欄の無い旧データは既定で読める。

## 2. 描き方（パイプラインの切り分け）

| スプライト | 経路 | 頂点・シェーダーの入力 |
|---|---|---|
| 欄がすべて既定で、角丸・楕円の切り抜きの中でもない | **従来の経路**（`sprite.wgsl`・`SpriteInstance` 80 bytes・同じバッチ・同じ描画コマンド列） | 変わらない（単体テスト `plain_items_take_the_legacy_path`） |
| 形と塗りの欄のどれかが既定でない | 形と塗りの経路（`sprite_shape.wgsl`） | `ShapeInstance`（行列 + パラメータの番号。80 bytes）＋ストレージバッファの `ShapeParamsGpu`（304 bytes） |
| 欄は既定だが、角丸・楕円の切り抜きの中（§3） | 形と塗りの経路（形なし・行列はそのまま・被覆率 1 × 切り抜き） | 同上 |

- **SDF**: 形の符号付き距離（角丸の矩形 = 四隅ごとの半径の sdRoundBox・楕円 = 近似 k0(k0−1)/k1〈円なら厳密〉・弧 = 角度の範囲の中はリング、
  外は端までの距離）を画素ごとに求め、被覆率 = clamp(0.5 − d / w, 0, 1)（w = 画面の 1 画素が形の空間で何単位か。シェーダーの dpdx・dpdy の二乗平均）。
  境界は 0.5、内側へ半画素で 1。画素の格子に揃った辺は従来と同じにくっきり出る。縁は d = −太さ で塗りへ切り替える。
- **四角形の大きさ**: 形のある四角形はアンチエイリアスの半画素がはみ出すので、ワールドの 2 単位（スクリーンスペースでは 2 画素）だけ外へ広げて描く
  （行列を付け替える＝`remap_model`）。形の無いスプライト（グラデーション・9 スライスだけ・切り抜きだけ）は広げない（従来と同じ位置・同じ辺）。
- **影**: 形をずらしてぼかした別のインスタンスを**先に**積む（形の後ろ）。σ = ぼかし ÷ 2（CSS の box-shadow）、被覆率 = 0.5(1 − erf(d / σ√2))、四角形は 3σ 広げる。
- **9 スライス**: 1 枚の四角形のまま、画素ごとに位置 → UV を軸ごとの区分の写像で求める（`nine_slice.rs` の `map_axis` と同じ式）。
  繰り返しは回数を丸めてタイルを少し伸縮する（CSS の border-image-repeat: round。端で切れたタイルを作らない）。テクスチャが無ければ効かない。
- **バッチ**: 形と塗りのアイテムは同じテクスチャが続く間 1 回の描画にまとまる（従来のアイテムとの境目で分かれる）。形と塗りを使わない画面の描画は変わらない。
- 3D ワールドキャンバスでも同じに描ける（深度の規則は `sprite.toml` と同じ）。エディタの GPU の ID 描画（3D ビューでの選択）は形を見ない（外接矩形）。

## 3. 角丸・楕円の切り抜き（CanvasClipComponent の形）

| 欄（serde） | 既定 | 意味 |
|---|---|---|
| `shape` | `rect` | `rect`（従来の scissor）/ `rounded_rect`（`corner_radii`）/ `ellipse`（内接する楕円）/ `sprite_shape`（最初の有効な Sprite の角丸・楕円に合わせる。弧は矩形） |
| `corner_radii` | 0 | `rounded_rect` の四隅（ノードのキャンバスの単位） |

- **いちばん内側の 1 つだけ**（W2-0 の決定）: ノードの切り抜きの鎖（内側 → 外側）のうち、いちばん内側の形のある領域だけを画素ごとの SDF で切る。
  その領域の外接矩形と他の祖先（形があっても）は従来どおり scissor で切る。領域の 4 隅とローカルの大きさから「ワールド → 領域のローカル」の
  アフィン写像を CPU で解いてシェーダーへ渡すので、回転・拡大した領域も正しく切れる（`clip_sdf.rs`）。
- **切るもの**: 画素ごとに切るのは**スプライト（画像・形）だけ**。テキスト・`SEED.Draw` の図形・2D パーティクル・スキンスプライトは外接矩形の scissor だけ
  （docs/backlog.md）。丸いアイコンの画像は「Sprite の `shape` = ellipse ＋テクスチャ」（ノード自身の画像）、中身ごと丸く・角丸に切るのは CanvasClip の形。
- 角丸のカード: 背景の Sprite に角丸 ＋ CanvasClip の `shape` = `sprite_shape`（背景と同じ角丸で中身の画像を切る）。

## 4. 当たり判定（形に合わせる）

| 判定 | 規則 |
|---|---|
| Sprite の見た目の形 | ポインタイベント（`pick_2d`・`OnPointer*`・エディタの 2D の選択）とジェスチャー（`CanvasGesture`）は、見た目が Sprite の矩形のノードでは Sprite の形の中だけ当たる（**円のボタンの角は押せない**）。直角の矩形は従来どおり |
| 最小のヒット領域（48 dp） | 形も同じだけ広げる: 角丸の矩形は大きさを片側 e 広げ半径に min(ex, ey) を足す、楕円は広げた矩形に内接する楕円、弧は帯を太らせる（`shape_hit.rs`） |
| 遮り・近い方（W2-2 の R3・R4） | 「見た目の矩形」を「見た目の形」にする（円のボタンの角は後ろを遮らない） |
| 切り抜き | 祖先のすべての AABB の内側、かつ、いちばん内側の形のある切り抜きの形の内側（描画と同じ規則）。丸い切り抜きの外は押せない |

## 5. テーマのトークン（部品が読む値）

部品は色・角丸・大きさ・文字の大きさ・動きの時間を直接書かず、トークン（`UiTokens`）で引く。既定の値の表は `scripting/src/Api/UI/Theme/default_theme.json`
（SEEDScripting に埋め込み。Wake or Pay の既定のテーマ midnight〈seedColor #6C4BFF・dark〉から作った暗い配色）。
JSON はグループ（`color`・`radius`・`space`・`size`・`text`・`motion`・`opacity`）の中に名前 → 値。色は sRGB の `#RRGGBB` / `#RRGGBBAA`（読み込みで線形へ）、
それ以外は数。先頭が `_` の鍵は説明。最上位に `"color.primary": "#…"` と直接書いてもよい。書いていないトークンは既定のテーマの値。
`UiTheme.LoadAsset(path)`・`UiTheme.Use(theme)` で差し替えると、部品は次のフレームで見た目を作り直す（実行中のテーマ交換の本格化・ギャラリーでの切り替えは W2-9）。

| トークン | 既定 | 使う所 |
|---|---|---|
| `color.primary` / `color.on_primary` | #7C5CFF / #FFFFFF | 塗りのボタン・オンのスイッチ・スライダ・進捗・選んだラジオ／その上の文字 |
| `color.background` / `color.surface` / `color.surface_variant` | #121018 / #1E1B26 / #2E2A3A | 画面の背景／面（カード・数値欄）／溝・オフのスイッチ・セグメントの台 |
| `color.on_surface` / `color.on_surface_muted` / `color.outline` | #ECE6F5 / #A9A2B8 / #6E6780 | 面の文字／控えめな文字・オフのチェックボックス・ラジオの輪／枠線 |
| `color.selected` / `color.on_selected` | #4A3AA8 / #EDE7FF | 選んだセグメント・チップ・薄い塗りのボタン |
| `color.knob` / `color.knob_off` | #FFFFFF / #A9A2B8 | スイッチのつまみ（オン・オフ） |
| `color.state_layer` / `opacity.pressed` | #FFFFFF / 0.16 | 押下の重ね色とその濃さ |
| `color.disabled` / `color.on_disabled` / `opacity.disabled` | #3A3645 / #7C7689 / 0.38 | 無効の塗り・文字・全体の濃さ |
| `color.shadow` / `opacity.shadow` / `color.error` | #000000 / 0.35 / #FF5252 | 影・エラー（W2-6） |
| `radius.button` / `chip` / `card` / `field` / `segment` / `checkbox` / `progress` | 12 / 16 / 20 / 12 / 20 / 4 / 4 | 角丸 |
| `space.xs` / `s` / `m` / `l` / `xl` | 4 / 8 / 12 / 16 / 24 | 余白 |
| `size.touch_min` / `border` / `check_border` | 48 / 1 / 2 | 押せる最小の大きさ・細い枠・チェックボックスとラジオの枠 |
| `size.toggle_knob` / `toggle_knob_off` / `toggle_inset` | 24 / 16 / 4 | スイッチのつまみ（オン・オフ） |
| `size.slider_track` / `slider_thumb` / `slider_thumb_pressed` | 4 / 20 / 24 | スライダ |
| `size.progress_bar` / `ring_thickness` / `radio_dot` / `shadow_blur` / `shadow_offset` | 8 / 6 / 10 / 8 / 2 | 進捗・ラジオの点・影 |
| `text.title` / `body` / `label` / `caption` | 20 / 16 / 14 / 12 | 文字の大きさ |
| `motion.short` / `medium` | 0.15 / 0.25 | つまみ・進捗の動き（秒） |
| `motion.repeat_interval` / `repeat_min_interval` / `repeat_accel` | 0.12 / 0.03 / 0.5 | 数値欄の長押しの連続（最初の間隔・最短の間隔・1 秒ごとに何倍になるか） |
| `size.wheel_item` / `text.wheel` | 32 / 21 | ホイールの行の高さ（1 行ぶんのスクロール・スナップの間隔）／行の文字（W2-5。§11） |
| `radius.wheel_band` / `size.wheel_band_inset` / `opacity.wheel_dim` | 8 / 9 / 0.447 | ホイールの中央の帯の角丸・左右の余白／帯の外の行の濃さ（帯の色は `color.surface_variant`、文字は `color.on_surface`・選べない行は `color.on_disabled`） |
| `motion.wheel` / `motion.wheel_correct` | 0.3 / 0.2 | ホイールをタップ・スクリプト・キーで動かす時間と午前/午後の連動／選べない行から戻す時間 |

グラフ（W2-8）のトークン（系列の色・線の太さ・点・棒・格子線・目盛りの文字・吹き出し・慣性）は [ui_charts.md](ui_charts.md) §7（名前は `ChartTokens`）。
画面の組み立て（W2-7）のトークン（画面の出入りの時間と曲線・幕・ダイアログ・シート・トースト・タブ・重なりのレイヤー）は [ui_navigation.md](ui_navigation.md) §9
（名前は `NavTokens`。曲線は `motion.push_curve` の `.x1`・`.y1`・`.x2`・`.y2` の 4 つの数＝CSS の cubic-bezier。`UiCurve.FromTheme`）。

## 6. 基本の部品（`SEED.UI`。部品ごとの状態と見た目）

部品は**アクタ（プレハブ）に付けるスクリプト**（`UiWidget` の派生。ScriptComponent の型名に `SEED.UI.Button` などを書く）。スクリプトは
「状態 → 見た目」を `ApplyLook` の 1 か所で決め（計算は `Looks/` の純関数）、プレハブの子の Sprite・Text の色・形・位置へ当てる。
部品どうし・画面のスクリプトとは登録簿（`UiWidget.Of<T>(gameObject)`・`UiRegistry`）で引き合う（スクリプトの OnStart の順は決まっていないので、
引く側は `UiRegistry.Version` が変わるたびに引き直す）。見た目を変えたら `SEED.Redraw.Request()`、動いている間は `Redraw.KeepAlive`（W2-10a）。

| 部品 | プレハブ（templates/ui/prefabs/） | 状態 | 見た目（トークン） | 振る舞い |
|---|---|---|---|---|
| `Button` | `button.actor`（Sprite・CanvasGesture〈タップ・長押し〉・Label・Icon〈任意〉）、丸いボタン `round_button.actor` | 通常・押下・長押し中・無効（`Interactable`・`Busy`） | 種類（Filled: primary / Tonal: selected / Outlined: 枠 outline / Text）。押下は state_layer を重ねる。無効は disabled・on_disabled | タップで `Clicked`（触感）、長押しで `LongPressed`、押下の終わりで `Released`。`Busy` の間は押せない（二重押しの防止）。押下がスクロールに負けたら取り消される（W2-2） |
| `Toggle` | `toggle.actor`（Sprite = 台・Knob） | オン・オフ・押下・無効 | 台 surface_variant → primary、つまみ knob_off（16）→ knob（24）、押している間はつまみ 24 | タップで切り替え、つまみは motion.short 秒（fastOutSlowIn） |
| `Checkbox` | `checkbox.actor`（Sprite = 箱・Mark〈回転した棒 2 本〉） | オン・オフ・押下・無効 | オン: primary の塗り・印 on_primary、オフ: 枠 on_surface_muted | タップで切り替え |
| `Slider` | `slider.actor`（当たりの帯・Track・Fill・Thumb） | 値・ドラッグ中・無効 | 溝 surface_variant、塗りとつまみ primary、ドラッグ中はつまみ 24 | 横のドラッグとタップでその位置の値へ（範囲・段階〈max も止まれる〉）。`ValueChanged` |
| `NumberField` | `number_field.actor`（背景・Minus・Value・Plus〈Button〉） | 値・無効 | 背景 surface・枠 outline・文字 on_surface。端では片側のボタンが無効 | −・＋で段階ぶん、長押しで連続（押し続けるほど速く、最短 0.03 秒）。`TrySetText`（数でない入力は捨てる・範囲の外は収める） |
| `SegmentedControl` | `segmented.actor`（台・Seg0..〈SelectItem〉） | 選択（必ず 1 つ）・押下・選べない | 台 surface_variant、選んだ項目 selected・on_selected | タップで選ぶ |
| `ChipGroup` | `chip_group.actor`（Chip0..〈SelectItem〉） | 複数選択（`Multiple`）か 1 つ（外せる）・選べない | 選んだ: selected・枠なし、選んでいない: 枠 outline | タップで選ぶ・外す |
| `RadioGroup` | `radio_group.actor`（Radio0..〈SelectItem: 輪・Dot・Label〉） | 1 つ選ぶ・選べない | 選んだ: 輪 primary・点を出す、選んでいない: 輪 on_surface_muted | タップで選ぶ |
| `ProgressBar` | `progress_bar.actor`（溝・Fill・Label〈任意〉） | 値（0..1）・無効 | 溝 surface_variant・塗り primary・角丸 radius.progress | 値の変化は motion.medium 秒で伸び縮み |
| `ProgressRing` | `progress_ring.actor`（溝〈弧 360〉・Arc・Label〈任意〉） | 値（0..1）・無効 | 溝 surface_variant、弧 primary（端を丸く・太さ size.ring_thickness） | 弧の角度 = 値 × 360（形と塗りの弧＝毎フレーム `SEED.Draw` を呼ばない） |
| `WheelPicker`（W2-5） | `wheel_picker.actor`（当たり・Band・Viewport〈切り抜き・スクロール〉・Blocker）＋行 `wheel_row.actor` | 中央の項目・動いている・無効 | 帯 surface_variant・radius.wheel_band、文字 on_surface（選べない行 on_disabled）、帯の外 opacity.wheel_dim | 上下のドラッグ・慣性・行ごとのスナップ・端をつなげる・タップした行へ・キーの上下。§11 |
| `TimeWheel`（W2-5） | `time_wheel.actor`（全列の帯・Meridiem・Hour・Minute〈WheelPicker〉） | 値（TimeOnly）・24/12 時間・分の刻み | 同上 | 12 時間表記の午前/午後の連動。§11 |

選択の項目（`SelectItem`）はタップ・押下をグループへ渡すだけで、見た目はグループが `SelectionLooks` で決めて当てる。選べない項目（`Disabled`）は灰色で、押しても変わらない。
選び方（`SelectionModel`）: Single（選んだ項目を押しても同じ）/ SingleOptional（押すと外れる）/ Multiple（切り替え）。

**見本**: `templates/ui/scenes/ui_gallery.scene`（ルートは dp。形と塗り〈角丸・四隅ごと・円・楕円・縁だけの輪・グラデーション 2・3・4 色と放射・縁・
9 スライス〈伸ばす・繰り返す〉・丸いアバター・円の切り抜き・角丸のカード・影・弧〉と全部品の状態〈押下・無効・オン/オフ・値〉を並べる）。
`scripts/UiGalleryDemo.cs` がスライダ ↔ 数値欄 ↔ 進捗をつなぎ、「全部を無効にする」で全部品を無効にする（デバッグの命令 `ui,disable` / `ui,enable` / `ui,theme,<path>`）。
テンプレートライブラリの「UI 部品」（`ui` のフォルダ）からプロジェクトへ取り込むと `assets/ui/...` になる。

## 7. エディタ

- Sprite のインスペクタに「形」「塗り」「9 スライス」「影」の節（**関係のない欄は隠す**: 角丸は矩形のときだけ、弧の欄は弧のときだけ、縁の色は太さ > 0 のときだけ、
  グラデーションの欄は単色でないときだけ〈角度は線形・中心と半径は放射〉、9 スライス・影の詳細は有効なときだけ）。
- Canvas Clip のインスペクタに「形」（角丸は RoundedRect のときだけ四隅を出す）。
- 編集は `SET_SPRITE_FIELD:{actor},{slot},{key},{value}` / `SET_CANVAS_CLIP_FIELD:…`（key はスクリプトと同じ欄名。列挙は serde の名前、bool は true/false、
  数はカンマ区切り）。Undo はランタイムの共通機構（欄ごとにまとまる）。新しい欄の行には「⟲ 既定値に戻す」を付けていない（入れ子の欄は既定の JSON に無いため。docs/backlog.md）。

## 8. 検証（2026-09-28・PC）

- **単体テスト（Rust）** `cargo test -p SEED --lib -- ui_shape batch2d sprite_style canvas_clip canvas_layout canvas_scroll gesture pick_2d ui_clip ui_draw`（250 件）:
  SDF の距離（直角・四隅ごとの角丸・CSS の縮め方・円・楕円・弧の範囲の中と外・丸い端と切りっぱなし）、画素の幅のアンチエイリアス（境界 0.5・画素の格子の辺はくっきり）、
  縁の帯、影の erf、グラデーション（線形の端点が四隅に乗る・放射・2〜4 色と位置・乗算済みアルファ・色と位置の整え）、9 スライスの頂点の格子と写像（倍率・縮め方・繰り返しの丸め）、
  形の当たり判定（円の角が外れる・48 dp まで形ごと広げる）、切り抜きの写像（回転も）といちばん内側の形のある領域、GPU の構造体の大きさと WGSL の定数の照合、
  **既定の欄のスプライトは従来と同じ頂点・同じバッチ**（`plain_items_take_the_legacy_path`）、形と塗りの経路への振り分け、行列の付け替え、欄の読み書き（スクリプト・IPC）、
  既存のレイアウト・スクロール・ジェスチャー（形の当たり判定を含む）・pick_2d・切り抜き。シェーダーは naga で検証（`sprite_shaders_parse_and_validate`）。
- **単体テスト（C#）** `dotnet run --project editor/tests/UiComponentsTests`（15 件）: テーマの読み方・既定のテーマが全トークンを持つ・ボタン・スイッチ・チェックボックス・選択の見た目・
  スライダ・数値欄・進捗の値・選択の状態・動き。
- **回帰**（WarashibeFishing の複製。変更前の SEED.exe で 4 回・変更後で 2 回撮った）: 図鑑は画面全体が 3 つのフレームとも**差 0 画素**、図鑑のボタンの縁の 56 点のクリックは
  当たり 34・外れ 22 が変更前と**すべて同じ**。MainGame・proLogue（3D の背景が動く）の UI の範囲は、変更前どうしの差（動く範囲のマスクの外で最大 89 画素・最大の差 9）より
  小さい差（2 画素以下・最大の差 1）で、動く背景の揺れの範囲（`sprite.wgsl`・`sprite.toml` は変えていない）。
- **ギャラリー**（`templates/ui` をプロジェクトの `assets/ui` へ写し、PC の Play〈540×1200・on_demand〉を IPC の入力の注入で操作。形と塗りの欄を持つスプライト 65）:
  丸いボタンの中心は押せ、外接矩形の左上・右下の角（円の外）は反応しない。円の切り抜きの中のボタンも角（切り抜きの外）は反応しない。押下の見た目・長押し・
  無効のボタン・トグルのつまみの動き・48 dp の当たり（24 の箱の外 10 で押せる）・スライダのドラッグと数値欄と進捗の同期・数値欄の＋と長押しの連続（2 秒で 50 → 0）・
  セグメント・チップ（複数）・ラジオ・選べない項目が反応しない・全部を無効にして反応しない、がログ（`[UI] …`）どおり。`SEED_SIM_SCALE_FACTOR=1.5` でも角丸・円・9 スライスの枠が
  dp の倍率どおりに大きくなり、縁が滑らか。

## 9. 実機での確かめ方（Pixel 6a。W2-4 の時点で未実施）

利用者と一緒に行う（手触りと見た目は実機でしか分からない）。ギャラリーのプロジェクト（`templates/ui` を `assets/ui` へ写し、`start_scene` を
`assets://ui/scenes/ui_gallery.scene`、`render_policy: on_demand`）を SeedAndroid の `run` で入れる。スクリプトのログ（`[UI] …`）を logcat で見る。

1. 形と塗りの見た目: 角丸・円・楕円・輪の縁が 2.625 倍の画面でも滑らか（アンチエイリアスは画素の幅）。グラデーション・9 スライス・影・円の切り抜き・角丸のカード
2. 丸いボタン（左下の「+」）の中心は押せ、外接矩形の角（円の外）は押せない。円の切り抜きの中のボタンも角は押せない
3. ボタンの押下の見た目（押して 100ms 以内に色が変わる。スクロールの中では 100ms 待つ）、長押し、無効のボタンが反応しない
4. トグルのつまみの動き（0.15 秒）が滑らかで、止まった後 10 フレームで描画が止まる（`[SEED REDRAW]`）
5. スライダのドラッグと数値欄の同期、数値欄の長押しの連続の速さ（押し続けるほど速い）
6. チップ（7 つ・複数）・セグメント・ラジオ、選べない項目が反応しない
7. `SEED.Time.Fps` でギャラリーの操作中 60 fps（形と塗りの欄を持つスプライトは 65）

## 10. 制限と持ち越し

- **画素ごとに切るのはスプライトだけ**: テキスト・`SEED.Draw`・2D パーティクル・スキンスプライトは角丸・楕円の切り抜きで外接矩形になる（backlog）
- **いちばん内側の 1 つだけ**: 外側の角丸・楕円の祖先は外接矩形（W2-0 の決定のまま）
- **エディタの GPU の ID 描画**（3D ビューでのキャンバスの選択）は形を見ない（外接矩形）
- **9 スライスの境目のにじみ**: 線形の補間で隣の片の画素が半画素にじむ（テクスチャの片の間に余白を取れば出ない）
- **弧は円だけ**（楕円の弧は無い）。影は形ごとのぼかしの近似（広がり〈spread〉・内側の影は無い）。放射グラデーションの中心・半径は割合だけ
- **インスペクタの ⟲**: 形と塗りの欄の行には既定値へ戻すボタンが無い（入れ子の欄は既定の JSON に無いので、共通のリセットが効かない）
- **部品の見た目のアニメーション**: 押下の色は即座に切り替わる（色の補間なし）。スイッチ・進捗は動く
- **W2-5 以降**: 時刻ホイール（W2-5 で済。§11）、文字入力の欄（W2-6。数値欄のキーボード入力は `TrySetText` だけ用意）、テーマの交換とギャラリーの切り替え（W2-9）、
  部品の押下の見た目の `Recycled` での戻し（一覧の行に部品を置くとき）

## 11. ホイールと時刻ホイール（W2-5。2026-09-28）

Wake or Pay のアラーム編集の中心の部品（Flutter 版は `CupertinoDatePicker` の時刻）。列が上下に流れ、窓の中央の行が選ばれる。
汎用の列 `SEED.UI.WheelPicker`（数の範囲か、項目の数と文字を渡す）と、それを 2〜3 列並べた `SEED.UI.TimeWheel`（値は `TimeOnly`）。
使い方は [scripting_api.md](scripting_api.md) §7.17。値・式の出典は Flutter の master のソース（`cupertino/picker.dart`・`cupertino/date_picker.dart`・
`widgets/list_wheel_scroll_view.dart`・`rendering/list_wheel_viewport.dart`・`painting/matrix_utils.dart`・`cupertino/text_theme.dart`。2026-09-28 に取得して確かめた）。

### 11.1 作りと分担

```
WheelPicker（Sprite = 列の大きさ〈透明〉・CanvasGesture〈タップ・押下の見た目なし〉・SEED.UI.WheelPicker）
├─ Band（Sprite = 中央の帯〈角丸〉。ShowBand = false の列には無い）
├─ Viewport（Sprite〈透明〉・CanvasClip・CanvasScroll〈縦・Interval のスナップ・入れ子で渡さない・中身は Fixed〉）
│   └─（行: ListView が wheel_row.actor〈WheelRow └ Label〈Text・pivot 0.5〉〉から作って使い回す）
└─ Blocker（Sprite〈透明〉・受けるジェスチャーの無い CanvasGesture＝遮る板。表示しない。無効の間だけ見せる）

TimeWheel（Sprite = 全体〈透明〉・SEED.UI.TimeWheel）
├─ Band（全列にまたがる中央の帯）
└─ Meridiem・Hour・Minute（上の WheelPicker の作りで ShowBand = false。位置と幅は TimeWheel が全体の幅を等分して決める）
```

| 側 | 受け持つもの |
|---|---|
| Rust（W2-3 の `CanvasScroll`。**W2-5 で変えていない**） | 指のドラッグ・離した後の慣性・端の跳ね返り・**行の高さごとのスナップ**（`snap: interval`・`snap_interval` = 行の高さ。止まる位置に最も近い倍数へ `FrictionSimulation.through`、遅ければばね。終わりは目標にちょうどそろえる）・動いている途中に触れて止める（Held）・「動いている」の申告 |
| C#（`SEED.UI`） | 行の並びと端をつなげる循環（`WheelLoop`）・行の曲面の見た目（`WheelLook` を `WheelRows` が行ごとに当てる）・中央の帯・中央の項目のイベントと触感・選べない行・タップ・キーボード・スクリプトからの値・時刻の組み立て（`TimeWheelMath`） |

**並び**: 先頭と末尾の余白 =（窓の高さ − 行の高さ）/ 2。こうするとスクロールの位置 p = 行 × 行の高さ のとき、その行がちょうど窓の中央に来る
（Flutter の `_topScrollMarginExtent` と同じ置き方。CanvasScroll の Interval のスナップ〈位置 0 から数えた倍数〉がそのまま「行の中央」になる）。
中央の行 = round(p ÷ 行の高さ)（半分は先の行＝Dart の round）。行の高さは `size.wheel_item`（または `ItemExtent`）、窓の高さは前のフレームの描画が測った値。

### 11.2 行の見た目（曲面の近似。`WheelLook.Resolve`）

行は平らに並べたまま、**見た目だけ**円柱に巻いたように映す（行の根は ListView が平らな位置に置き、子の Label〈Text〉の位置・倍率・色だけを毎フレーム変える）。
入力は行の中心と窓の中心の平らな距離 d（下が正）。Flutter の `RenderListWheelViewport._paintTransformedChild` と
`MatrixUtils.createCylindricalProjectionTransform`（遠近 × 視点 × X 軸の回転 × 半径ぶんの平行移動）を行の中心 1 点で展開した閉じた式:

| 量 | 式 |
|---|---|
| 窓の端に当たる角度 θmax | 直径の比 < 1 ? π/2 : asin(1 / 直径の比)（Flutter の `_maxVisibleRadian`） |
| 行の角度 θ | (d ÷ (窓の高さ / 2)) × θmax ÷ 詰め具合（Flutter の angle と符号だけ逆） |
| 円柱の半径 r・遠近の割り算 w | r = 窓の高さ × 直径の比 / 2、w = 1 + 遠近 × r × (1 − cos θ) |
| 映る位置（窓の中心から） | r sin θ / w |
| 縦の倍率 | (cos θ × w − 遠近 × r × sin²θ) / w²（映る位置の微分。0 になる角度 cos θ = a/(1+a)〈a = 遠近 × r〉で映る位置が最も外へ出て、その先は裏側＝描かない） |
| 横の倍率 | 1 / w |
| 帯の中の度合い e | max(0, 1 − |d| ÷ 行の高さ) |
| 拡大 | 1 + (拡大の倍率 − 1) × e（縦横の倍率に掛ける） |
| 濃さ | (帯の外の濃さ + (1 − 帯の外の濃さ) × e) × (1 + (cos θ − 1) × 面の傾きの暗さ) |

| 値（`WheelPicker` のフィールド／トークン） | 既定 | 出典 |
|---|---|---|
| 円柱の直径の比（`DiameterRatio`） | 1.07 | `picker.dart` `_kDefaultDiameterRatio` |
| 遠近（`Perspective`） | 0.003（上限 0.01） | `_kDefaultPerspective`（上限は `RenderListWheelViewport`） |
| 詰め具合（`Squeeze`） | 1.25 | `date_picker.dart` `_kSqueeze`（CupertinoPicker の既定は 1.45） |
| 中央の拡大（`Magnification`） | 2.35 / 2.1 ≈ 1.119 | `date_picker.dart` `_kMagnification` |
| 帯の外の濃さ（`opacity.wheel_dim`） | 0.447 | `picker.dart` `_kOverAndUnderCenterOpacity` |
| 面の傾きの暗さ（`EdgeShade`） | 1 | SEED の足し分（面の向きの余弦＝ランバートの近似。0 で無効） |
| 行の高さ（`size.wheel_item`）・文字（`text.wheel`） | 32・21 | `date_picker.dart` `_kItemExtent`・`text_theme.dart` `_kDefaultDateTimePickerTextStyle` |
| 帯の角丸（`radius.wheel_band`）・左右の余白（`size.wheel_band_inset`）・帯の高さ | 8・9・行の高さ × 拡大 | `CupertinoPickerDefaultSelectionOverlay` の 8・9、`_buildSelectionOverlay` の itemExtent × magnification |

- **Flutter との違い**: Flutter は中央の帯の中と外を切り抜いて 2 度描く（帯の中は拡大・不透明、外は 0.447 の濃さ）。SEED は行ごとに 1 度だけ描くので、
  帯の中の度合い e で拡大と濃さを連続に補間する（行が帯の境を越える間は中間の濃さ）。窓の高さ 190・行 32 で、中央の 1 行下は映る位置 32.0・縦 0.90、
  2 行下は 58.0・0.65、3 行下は 74.2・0.33、描ける上限は平らな距離 131（映る位置 80）。
- 式は単体テストで Flutter の 4×4 の行列を実際に掛けた値と照合している（映る位置・縦の倍率が 1/1000 以内で一致）。
- 行の文字の枠は列の幅 × 行の高さ（中央揃え・pivot 0.5 で倍率の中心が文字の中心）。書き込みは前の値から変わったものだけ（止まっている行は何も書かない）。

### 11.3 端をつなげる（循環。`WheelLoop`）

無限の一覧は作らず、項目を cycles 回くり返した count × cycles 行を ListView に並べ（中身の長さ ≒ 200,000 単位。周の数は奇数・下限 3）、
**真ん中の周**から始める（行 r の項目 = r mod count。負でも 0..count−1）。見えている行と前後の余白（円柱の裏へ回る手前まで＋1 行）だけを作って使い回すので、
60 分の列でも作る行は十数行。

- 位置は float（スクリプトの Vector2）で渡るため、中身の長さを 20 万に抑える（2^17 付近で 1/64 単位の刻み。dp のキャンバスで 2.625 倍でも 0.05 画素未満）。
  真ん中から片側 10 万ぶん＝最速のフリック（跳ね返りの慣性で約 4,000）の 25 回ぶんは端に着かない。
- **止まったとき**、真ん中の行から「全体の行の数 × 1/4」より離れていれば、同じ項目の真ん中の周の行へ見えない飛び方をする（並びは周ごとに同じなので見た目は変わらない。
  指・慣性・スクリプトの動きの途中には飛ばない＝動きを止めない）。
- スクリプト・キー・タップで項目へ動かすときは、今の行（動きの途中なら行き先の行）から**近い向き**へ回る（59 → 00 は 1 行進む。ちょうど半周は進む向き）。
- 端をつながない列（午前/午後・数のホイール）は行 = 項目で、端では跳ね返る（CanvasScroll の Bounce）。

### 11.4 イベントと触感

| イベント | いつ |
|---|---|
| `WheelPicker.SelectionChanged(列, 項目)` | 中央の項目が変わるたび（指・慣性・スナップ・タップ・キー・スクリプトの動きの途中も。1 フレームに 2 行進んだら最後の項目で 1 回） |
| `WheelPicker.Settled(列, 項目)` | 止まったとき（選べない行に止まったときは戻り終えてから）。止まるたびに出す |
| `TimeWheel.ValueChanged(時刻ホイール, 値)` | 値が変わるたび（指で回している途中も）。スクリプトの `SetValue` では 1 回だけ（列が動いている途中の値は出さない） |
| `TimeWheel.ValueSettled(時刻ホイール, 値)` | 全列が止まったとき |

- **触感**（`SEED.Platform.Haptics.Tap`。`Haptic` で切れる）: 中央の項目が変わったとき、**指のドラッグとその慣性の間だけ**（スクロールの段階が Dragging・Ballistic）、
  1 フレームに 1 回まで。タップ・キー・スクリプトの動き（Animating）では鳴らさない（Flutter の tap-to-scroll と同じ）。Flutter は iOS だけで鳴らすが、
  SEED は Android の端末で鳴らす（デスクトップは模擬＝ログだけ）。強さ・間隔は実機で詰める（§11.9）。
- 値の変化で他の部品は作り直さない（中央の行が変わっても、書くのはその列の行の Label だけ。ギャラリーの `ui,stats` で、フリックの前後の
  `UiWidget.RefreshCount` が変わらないことを確かめた）。

### 11.5 選べない行・タップ・キーボード・スクリプト

- **選べない行**（数の範囲なら `LimitSelectable` と `SelectableMin`〜`SelectableMax`、Configure した列は `SetItemEnabled(述語)`）: 文字は `color.on_disabled`。
  止まった行が選べなければ最も近い選べる行へ `motion.wheel_correct` 秒で戻る（同じ近さなら進む向き。Flutter の CupertinoDatePicker の 200ms）。タップしても動かない。
  キーの 1 歩は選べない行を飛ばす。
- **タップ**: 止まっている列の行をタップするとその行へ `motion.wheel` 秒で動く（Flutter の `_kCupertinoPickerTapToScrollDuration` 300ms・easeInOut）。
  押した位置が映っている行は、見た目の式の逆（映る位置 → 平らな距離。二分法）で求める。**動いている間のタップは止めるだけ**（前のフレームも止まっていた列だけが受ける。
  慣性の途中の窓は触れた指で止まる＝W2-3 の Held）。
- **キーボード**: 最後に指で触れた（またはスクリプトが `Focus()` した）ホイール 1 つだけが上下の矢印で 1 つずつ動く（`WheelFocus`）。時刻ホイールは左右の矢印で自分の列の間を移す。
  連続で押すと行き先を積み上げる。**W2-7 で `WheelFocus` は `UiFocus`（フォーカスの範囲）の窓口になった**: ホイールはいちばん前の範囲（上の画面・
  開いているダイアログ・シート・覆い）の中にあるときだけ今の相手になり、覆われた画面・選んでいないタブのホイールは矢印キーを受けない
  （閉じる・戻ると覚えていた相手へ戻る。[ui_navigation.md](ui_navigation.md) §7）。入力欄（W2-6）も同じ仕組みに乗る。
- **スクリプト**: `SelectIndex(項目, animate)`・`SetValue(値, animate)`・`StepBy(n)`。animate = true は `motion.wheel` 秒の ScrollTo（easeInOut）、false はすぐ移す。
  最初の描画の前（窓の大きさ・中身の長さが分かる前）に呼んだら、分かったときに黙って置く（イベントを出さない）。`Configure(数, 文字, 端をつなげる, 項目)` で作り直すときも黙って置き直す。
- **無効**（`Interactable = false`）: 文字・帯を `opacity.disabled` で薄くし、遮る板（Blocker）を見せて窓のスクロールに指を渡さない（CanvasScroll の `enabled = false` は位置を 0 にするので使わない）。
- **準備の待ち**: 窓の大きさ・中身の長さ・行の作成を待つ間は描き続けを頼むが、上限 60 フレーム（祖先が隠れて測れない・行のプレハブが無いときに on_demand の描画を止められなくなるのを防ぐ。
  行が作れなければ 1 度だけ警告）。

### 11.6 時刻ホイール（`TimeWheel`）

| 項目 | 規則 |
|---|---|
| 列 | 24 時間表記: 時（24 行・つなげる）・分（60 ÷ 刻み 行・つなげる）。12 時間表記: ＋午前/午後（2 行・つながない。`MeridiemOnLeft` で左〈既定。日本語の「午前 7:30」〉か右） |
| 時の文字 | 24 時間表記は 0〜23（`HourFormat` 既定 "0"。Flutter の日本語と同じくゼロ埋めなし）、12 時間表記は 12, 1, …, 11（午前の半日）・12, 1, …, 11（午後の半日）＝24 行。分は `MinuteFormat` 既定 "00" |
| 午前/午後の連動 | Flutter の `_CupertinoDatePickerDateTimeState` と同じ: 時の列の中央の行の半日（region = 項目 ÷ 12）と選んでいる午前/午後（amPm）を別に持ち、時 = region ≠ amPm ? (項目 + 12) mod 24 : 項目。時の列が 11 ↔ 12・23 ↔ 0 を越えたら amPm も入れ替え、午前/午後の列を `motion.wheel` 秒で動かす（Flutter は 300ms・easeOut。SEED の ScrollTo は easeInOut）。午前/午後の列を指で変えると時が 12 ずれる（時の列の表示は 12 時間で同じなので動かさない）。午前/午後の列に指があるときは連動で動かさない |
| 分の刻み | 1 時間を割り切る数（1・5 など。それ以外は 1）。刻みに合わない時刻は最も近い刻みへ（ちょうど間は遅い方。23:58 を 5 分刻み → 0:00＝時・日をまたいで繰り上がる）。分の 59 → 00 で時は変えない（iOS・Flutter と同じ独立の列） |
| 値の設定 | `SetValue(TimeOnly, animate)`: 丸めてから各列を近い向きへ動かす。動いている途中は連動も値の変化も出さず、全列が止まったら（か指で触れたら）列の見た目から値を確かめ直す |
| 切り替え | `SetUse24Hour(bool)`（値はそのまま。午前/午後の列を隠し・列を並べ直す）・`SetMinuteStep(int)`（値を丸める） |

### 11.7 描く理由

列が動いている間は CanvasScroll が「動いている」（motion）を申告するので、`render_policy: on_demand` でも慣性・スナップ・スクリプトの動きの途中で止まらない。
止まって 10 フレームで描画が止まる。スクリプトの値の設定・タップ・キーは `Redraw.Request()` で次のフレームを頼む。

### 11.8 検証（2026-09-28・PC）

- **単体テスト（C#）** `dotnet run --project editor/tests/UiComponentsTests`（30 件。うち W2-5 の 15 件）: 行の見た目（中央はそのまま・拡大・不透明、
  映る位置と縦の倍率が Flutter の 4×4 の行列の積と 1/1000 以内で一致、離れるほど縮み・薄く・外へ寄る・上下対称、描ける距離の上限の内外、窓が 0 なら平ら、
  壊れた値で NaN を出さない）、映る位置 → 平らな距離の往復、循環の添字（−1 → 59・int の最大と最小）、周の数（奇数・下限 3）、近い向きの行（59 → 00・00 → 59・半周・一覧の端）、
  真ん中の周へ戻す判定、位置 ↔ 行（半分は先の行）、選べる項目の探し方とキーの 1 歩、12/24 時間の表示、11 → 12・23 → 0 の連動、午前/午後の列の変更と列の見た目からの作り直し、
  分の刻みの正規化と丸め（繰り上がり・日をまたぐ・秒を捨てる）。既定のテーマが新しいトークンを持つことも既存のテストで確かめる。
- **Rust**: 変えていない（`cargo test -p SEED --lib -- canvas_scroll` 24 件・`cargo build`・`cargo ndk -t arm64-v8a -P 29 build` が通る）。
- **ギャラリー**（`templates/ui` をプロジェクトの `assets/ui` へ写し、PC の Play〈540×1200・on_demand〉を IPC の入力の注入とデバッグの命令で操作。ログ `[UI] …`）:
  24 時間の分を 2 行ゆっくり → 7:32（止まった値も 7:32）、7:59 から 1 行 → 7:00（分の循環・時は不変）、23:00 から時を 1 行 → 0:00、
  分のフリック → 0:01〜0:28 を流れて行の上に止まり（途中で 1 フレーム 2 行の所は 1 回）、止まって 10 フレーム後に `[SEED REDRAW] 描画を止めます`、
  フリックの前後で `UiWidget.RefreshCount` 57 → 57（他の部品は作り直されない）、12 時間の時を 11 → 12 → 午後へ連動して 12:55、5 分刻みの 55 → 00 → 12:00、
  午前/午後の列を午前へ → 0:00、時を 12 → 11 → 午後へ連動して 23:00、スクリプトの 19:45（動きあり: time は 1 回）と 6:05（すぐ）、
  中央の 1 つ下の行のタップ → 7:05、映る位置 −70 のタップ → 曲面の逆で 3 行上の 4:05、選べない 25 分 → 20 分へ戻る（スクリプト・指の両方）、
  選べない行のタップは動かない、キーの ↓↓↑ → +1 分・← で時へ → +1 時、12 時間 ↔ 24 時間の切り替え（列が 3 ↔ 2）、分の刻みを 5 へ（5:06 → 5:05）、
  全部を無効にしてドラッグ → 動かない、フリックの途中のタップ → 止めるだけ（2 行下を選ばない）、分の列を 40 周先へ飛ばして 1 行動かす → 止まった所で真ん中の周へ戻る
  （位置 175,840 → 99,072 = 行 3,096 = 真ん中の周の 36 分）、テーマの差し替え（行の高さ 40・文字 26・帯の角丸 14）→ 値はそのままで並び直す。
  触感の模擬は指の動きの間だけ（タップ・キー・スクリプトでは 0 回）。`SEED_SIM_SCALE_FACTOR=1.5` でも行・帯・文字が dp の倍率どおり。
- **回帰**（WarashibeFishing の複製。変更前の SEEDScripting.dll〈HEAD〉と変更後で撮った）: 図鑑の画面 3 フレームとも差 0 画素、図鑑のボタンの縁の 56 点のクリックは
  当たり 34・外れ 22 で、各クリックの後の画面まで変更前と一致。

### 11.9 実機での確かめ方（Pixel 6a。W2-5 の時点で未実施）

利用者と一緒に行う（手触りと触感は指でしか分からない）。ギャラリーのプロジェクト（§9 と同じ作り）を SeedAndroid の `run` で入れ、`[UI] time …` を logcat で見る。

1. 分の列をゆっくり引いて離す → 最寄りの行へ吸い付く。速く払う → 慣性で流れて行の上に止まり、止まって 10 フレームで `[SEED REDRAW] 描画を止めます`
2. 行が変わるたびの触感（EFFECT_CLICK）が強すぎ・多すぎないか（速いフリックでは 1 フレーム 1 回＝60 回/秒まで）。強ければ間隔の下限を足す（backlog）
3. 23:59 → 0:00（分・時がそれぞれつながる）、12 時間表記の 11 → 12 で午前/午後が動く・午前/午後の列を指で変える
4. 曲面の見た目（2.625 倍で文字が滲まない・上下の縮み・帯の外の薄さ）を iOS／Flutter 版と見比べる。詰め具合・拡大・面の傾きの暗さはフィールドで詰める
5. 動いている途中のタップで止まるだけ・止まっている列の行のタップでその行へ・選べない行へ止めると戻る
6. `SEED.Time.Fps` でホイールを回している間 60 fps（UC-4 の「他の部分が作り直されない」は §11.8 の RefreshCount で確認済み）

### 11.10 制限と持ち越し

- **Flutter の拡大鏡の 2 度描き**はしない（帯の中の度合いで連続に補間）。帯の境をまたぐ行は帯の中も外も中間の濃さ（iOS は帯の中だけ濃い）
- **オフアクシス**（`offAxisFraction`。列ごとに左右へ傾ける）・**遠近の横の歪み**（行の中での横の倍率の変化）は無い（行ごとに一様な倍率）
- **無限のスクロールではない**: 中身の長さ ≒ 20 万の中で止まるたびに真ん中へ戻す。止めずに 25 回以上最速でフリックし続けると端に着く（跳ね返る）
- **最小・最大の時刻**（CupertinoDatePicker の minimumDate/maximumDate。時の列・分の列の選べない行が値で変わる）は無い（列ごとの `SetItemEnabled` で作れる。backlog）
- **キーボードのフォーカス**: 最後に触れたホイール（W2-7 でフォーカスの範囲へ寄せた。§11.5）。マウスのホイールで回す操作は無い（backlog）
- **読み上げ**（アクセシビリティ）は無い
- **実機の手触り・触感**: 未確認（§11.9）
