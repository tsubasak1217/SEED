# 形と塗り・基本の部品（W2-4 の正典。2026-09-28）

キャンバス UI の**形と塗り**（スプライトの角丸・楕円・弧・縁の線・グラデーション・9 スライス・ぼかしの影、角丸・楕円の切り抜き、
形に合わせた当たり判定）と、**基本の部品**（`SEED.UI` のボタン・トグル・チェックボックス・スライダ・数値欄・セグメント・チップ・ラジオ・
進捗の棒と輪）、部品が読む**テーマのトークン**の規則。背景と段階は [app_platform_roadmap.md](app_platform_roadmap.md) §3.3 の
「ボタン」〜「形と塗り」・§3.8.4（切り抜きの決定）・§3.8.5 の W2-4。

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
- **W2-5 以降**: 時刻ホイール（W2-5）、文字入力の欄（W2-6。数値欄のキーボード入力は `TrySetText` だけ用意）、テーマの交換とギャラリーの切り替え（W2-9）、
  部品の押下の見た目の `Recycled` での戻し（一覧の行に部品を置くとき）
