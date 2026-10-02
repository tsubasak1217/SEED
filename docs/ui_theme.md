# テーマ（W2-9 の正典。2026-09-28）

SEED.UI の部品（W2-4〜W2-8）が見た目を決めるときに読む**テーマ**の書き方・継承・明暗・切り替えと、**トークンの表**（§8）。
部品は色・角丸・大きさ・文字の大きさ・書体・動きの時間を直接書かず、トークン（例 `color.primary`・`radius.button`）で引く。
テーマはトークンの値の表で、プロジェクトのアセットの JSON（E-07 の決定）。実行中に差し替えると、表示中の全部品がその場で見た目を当て直す。
背景と段階は [app_platform_roadmap.md](app_platform_roadmap.md) §3.2 の W2-P6・§3.3 の「テーマ」・§3.5 の UC-9・§3.8.5 の W2-9。

| 置き場 | 役割 |
|---|---|
| `scripting/src/Api/UI/Theme/default_theme.json` | 組み込みの既定のテーマ（SEEDScripting に埋め込み。暗い方＋`light` の節＝明暗の両方） |
| `scripting/src/Api/UI/Theme/UiTokenCatalog.cs`（`UiTokenInfo`・`UiTokenKind`） | **トークンの表（1 か所）**: 名前・型・使う部品。読み込みの検査と §8 の表の源 |
| `UiTokens.cs`・`Navigation/Model/NavTokens.cs`・`Charts/Model/ChartTokens.cs` | トークンの名前の定数（部品のコードが使う） |
| `UiThemeSource.cs` | JSON 1 つの読み込み（知らない名前・型の誤りの警告） |
| `UiThemeResolver.cs`・`UiThemePaths.cs` | 継承（`extends`）を解いて鎖を作る・パスの解き方 |
| `UiThemeDefinition.cs`・`UiThemeTable.cs`・`UiThemeData.cs`・`UiThemeValue.cs` | 継承を解いたテーマ（明暗の組）・重ねる入れ物・1 つの明暗の平らな表・値 |
| `UiBrightness.cs` | 明暗と選び方（`UiBrightnessMode`）の規則 |
| `UiSeedColors.cs` | 種の色（`seed_color`）から主の色と選択の色を作る規則（§6） |
| `UiThemeBlend.cs`・`UiThemeTransition.cs` | 切り替えの色の補間 |
| `UiTheme.cs`・`UiThemeChange.cs`・`UiSystemBrightness.cs` | 実行中の窓口（読み込み・当てる・明暗・当て直し・知らせ）と端末の明暗 |
| `scripting/src/Api/UI/Widgets/ThemeStyle.cs`・`UiTextStyle.cs` | 部品でない飾りをトークンに結び付ける・部品の文字へ大きさ・書体・太さを当てる |
| `scripting/src/Api/Platform/App/`（`App.UiMode`・`SystemUiMode`）・`PlatformDiagnostics.SimulateUiMode` | 端末の明暗の設定（`app.ui_mode`）とその変化のイベント |
| `runtime/src/engine/platform/bridge/desktop_sim/ui_mode_*.rs`・`os_ui_mode.rs` | PC の模擬（OS の「既定のアプリ モード」を読む・差し替え・変化のイベント） |
| `runtime/android/.../platform/local/UiModeCommand.java`・`platform/app/NightMode.java` | Android（`Configuration.uiMode` の夜の bit・`onConfigurationChanged` でイベント） |
| `templates/ui/themes/`（`forest.json`・`sunrise.json`・`forest_round.json`） | 見本のテーマ（§9） |
| `templates/ui/scenes/ui_gallery.scene`・`scripts/UiGalleryThemeBar.cs`・`UiGallerySections.cs` | 全部品のギャラリーとテーマの切り替え（§9） |

---

## 1. 考え方

- **トークン**は「グループ.名前」（`color.primary`）。グループは `color`・`radius`・`space`・`size`・`text`・`font`・`motion`・`opacity`・`ratio`・`speed`・`count`・`layer`
  （§8 の表の名前の最初の部分）と、アプリ独自の `app`。
- **テーマ**はトークンの値の表。書いていないトークンは基のテーマ → 組み込みの既定のテーマの値（どのテーマも最後は既定のテーマに落ちる）。
- **明暗**はテーマが持つ（自分の明暗と、対応するもう一方の明暗）。どちらで表示するかは選び方（テーマのまま・端末に従う・強制）で決める。
- **切り替え**は `UiTheme.Apply` 1 つ。登録簿（`UiRegistry`）の全部品の `ApplyLook` をその場で呼び直す（同じフレームで全部品が変わる）。
- テーマの JSON は**例外で止めない**: 知らない名前・型の合わない値・読めない基のテーマは警告（ログ）して、その値を使わない（基・既定の値が残る）。

## 2. テーマの JSON の書き方

```jsonc
{
  "_about": "説明（先頭が _ の鍵は読まない）",
  "name": "forest",                        // 名前（ログ・UiTheme.Changed。無ければファイル名）
  "extends": "base.json",                  // 基のテーマ（§3。省略 = 組み込みの既定のテーマ）
  "brightness": "dark",                    // このテーマの明暗（§4。省略 = 基のテーマのまま）
  "seed_color": "#2E7D32",                 // 種の色（§6。主の色と選択の色を明暗の規則で作る）
  "color": { "surface": "#17221A" },       // グループの中に名前 → 値
  "color.on_surface": "#E2EEE3",           // 「グループ.名前」を最上位に直接書いてもよい
  "motion": { "push_curve": { "x1": 0.2, "y1": 0, "x2": 0, "y2": 1 } },   // 曲線は 4 つの数
  "font": { "family": "assets://fonts/rounded.ttf", "weight": 0.3 },      // 書体（空 = 組み込み）と太さ
  "app": { "kakugo": { "danger": "#FF5252" } },                            // アプリ独自（表で調べない）
  "light": { "color": { "surface": "#FFFFFF" } },                          // 明るい方で表示するときだけ上書き（§4）
  "dark":  { }                                                             // 暗い方で表示するときだけ上書き
}
```

| 値の型（§8 の「型」） | JSON | 例 |
|---|---|---|
| 色 | sRGB の `"#RRGGBB"` / `"#RRGGBBAA"`（読み込みで線形へ直す。SEED の色は線形） | `"#7C5CFF"`・`"#7C5CFF1F"` |
| 数 | 数（長さはキャンバスの単位＝dp のキャンバスなら dp、時間は秒、濃さ・割合は 0..1） | `12`・`0.25` |
| 曲線 | `{ "x1", "y1", "x2", "y2" }`（CSS の cubic-bezier。成分ごとの数のトークン `motion.push_curve.x1` などになる） | `{ "x1": 0.4, "y1": 0, "x2": 0.2, "y2": 1 }` |
| 文字列 | 文字列（今は書体の assets:// のパスだけ。空 = 組み込みの書体） | `""` |

- **予約の鍵**（最上位だけ）: `name`・`extends`・`brightness`・`seed_color`・`light`・`dark`。`light` / `dark` の節の中には書けない（警告）。
- **アプリ独自のトークン**は `app` のグループへ（`app.kakugo.danger`）。表で調べず、値の形で型を決める（`#…` の文字列は色・ほかの文字列は文字列・数は数）。
  スクリプトからは `UiTheme.Color("app.kakugo.danger")`・`ThemeStyle` の欄で使う。
- コメント（`//`・`/* */`）と末尾のカンマを許す（手で書くファイルのため）。

**警告になるもの**（`[SEED.UI] <ファイル>: <トークン>: <理由>` をログへ。その値は使わない）:

| 書き方 | 例 | 扱い |
|---|---|---|
| SEED のグループの中の表に無い名前 | `"color": { "primery": … }` | 「打ち間違い？」の警告。読まない |
| 知らないグループ | `"colour": { … }`・`"kakugo.danger"` | 警告（アプリ独自なら `app` へ） |
| 型の誤り | 色に数・`"red"`・`"#12345"`、数に `"12"`、書体に数、曲線に数 | 警告。基・既定の値のまま |
| グループをオブジェクトで書いていない | `"size": "big"` | 警告 |
| `brightness` の語の誤り・`extends` が文字列でない・`seed_color` が色でない・節がオブジェクトでない | `"brightness": "dim"` | 警告。書いていない扱い |
| 節の中の予約の鍵 | `"light": { "name": … }` | 警告 |
| 壊れた JSON | `{ bad` | `UiTheme.Load` は null（警告）。基のテーマなら既定のテーマへつなぐ（§3） |

## 3. 継承と既定への落ち方

`extends` の書き方（`UiThemePaths.Resolve`）:

| 書き方 | 読むもの |
|---|---|
| 省略・`"default"`・`"builtin:default"` | 組み込みの既定のテーマ |
| `"assets://ui/themes/base.json"` | そのパス |
| `"base.json"`・`"./sub/x.json"`・`"../common/x.json"` | このファイルのフォルダから数えたパス（`.` と `..` を解く。根より上へは出ない） |

- 葉のテーマから `extends` をたどり、**最後は必ず組み込みの既定のテーマ**に着く（根 → … → 葉 の鎖。`UiThemeDefinition.ChainOrigins`）。
- 1 つの明暗の表は、鎖の根から葉へ順に各テーマの層を重ねる（`UiThemeTable.ApplyLayer`）:
  **明暗に依らない値 → その明暗の節 → 種の色から作った値（そのテーマが明示していないトークンだけ）**。後の層が勝つので、
  近いテーマの値 → 遠い基のテーマ → 既定のテーマ の順に落ちる（**子の最上位の値は、親の明暗の節にも勝つ**）。
- 表は明暗ごとに 1 度だけ作って覚える（引くたびに鎖をたどらない）。
- 次のときは警告してそこで鎖を切り、既定のテーマへつなぐ: 基のファイルが読めない・壊れた JSON・輪（A → B → A、自分を基にする）・深すぎる（葉から 16 段。`UiThemeResolver.MaxDepth`）。

## 4. 明暗（ライト・ダーク）

**テーマの明暗と対応**（`UiThemeDefinition.Brightness`・`Supports`。鎖を根から順に見る）:

- `brightness` を書いたテーマで「その明暗だけ」に置き直し、`light` / `dark` の節を持つテーマでその明暗を足す。
- 組み込みの既定のテーマ = 暗い方（最上位）＋ `light` の節 ＝ **両方に対応**。
- `brightness` を書かずに一部だけ上書きしたテーマは、基のテーマの明暗と対応を受け継ぐ（既定のテーマを基にすれば両方）。
- `brightness` を書いた単色のテーマ（Wake or Pay の midnight・sunrise など）はその明暗だけ（もう一方の節を書けば、空でも対応する）。
- 書き方の約束: 明暗の両方に対応するテーマで**明暗で変わる色は `light` / `dark` の節に書く**（最上位に書いた色は両方の明暗に効く）。
  暗い色の面を最上位に書くなら `brightness: "dark"` も書く（その明暗だけのテーマになり、明るい方で混ざらない）。

**選び方**（`UiTheme.SetBrightnessMode(UiBrightnessMode)`。規則は `UiBrightnessRules`）:

| 選び方 | 望む明暗 | 表示する明暗 |
|---|---|---|
| `Theme`（既定） | テーマの明暗 | テーマの明暗 |
| `System` | 端末の設定（取れなければテーマの明暗） | テーマが対応していればそれ、でなければテーマの明暗 |
| `Light` / `Dark` | 強制 | テーマが対応していればそれ、でなければテーマの明暗（1 度だけ警告） |

**端末の明暗**（`SEED.Platform.App.UiMode`・`app.ui_mode`。問い合わせは選び方が `System` になったときだけ）:

| 環境 | 値 | 変化のイベント `platform.ui_mode_changed { night }` |
|---|---|---|
| Android | `Configuration.uiMode & UI_MODE_NIGHT_MASK`（YES / NO / UNDEFINED → `yes` / `no` / `unknown`）。メインプロセスが IPC なしで答える（`local/UiModeCommand`） | `MainActivity.onConfigurationChanged`（マニフェストの `configChanges` に `uiMode` があるので Activity は作り直されない）で夜の bit が前と違えば（`platform/app/NightMode`） |
| PC（デスクトップの模擬） | Windows の「既定のアプリ モード」（`HKCU\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize` の `AppsUseLightTheme`。0 = ダーク）。ほかの OS は `unknown` | ウィンドウの `WindowEvent::ThemeChanged`（単体起動の最上位のウィンドウだけに届く）と模擬の差し替え |
| 模擬の差し替え | `PlatformDiagnostics.SimulateUiMode(SystemUiMode?)`（`app.sim_set_ui_mode`。null で OS の設定へ戻す。Android では unknown_method） | 値が変わったら積む（OS の設定を変えずに「端末に従う」を試す） |

取れない（`unknown`）ときは**テーマの明暗のまま**（手動の選び方 `Light` / `Dark` で選べる）。変化のイベントは SEED.UI がエンジンの受け口
（`PlatformEvents.AddEngineListener`。スクリプトの読み直しで外れない）で受け、選び方が `System` なら当て直す（`UiTheme.AnimateSystemChanges` で補間）。
スクリプトからは `this.On(App.UiModeChangedEvent, …)` で受けられる（`App.TryParseUiModeEvent`）。

## 5. 切り替えと当て直し

- `UiTheme.Apply(theme, animate)`・明暗の選び方の変化・端末の明暗の変化で、表示する表を作り直し、**登録簿（`UiRegistry.Snapshot`）の全部品の
  `ApplyLook` をその場で呼び直す**（`UiWidget.ReapplyTheme`。隠れているタブ・積まれた画面の部品も）。`UiTheme.Version` が増え、`UiTheme.Changed` を 1 回呼ぶ。
  登録の前に替わった部品は `OnStart` で今のテーマで作る。取りこぼしは毎フレームの `UiTheme.Version` の見比べで拾う（W2-4 からの仕組み）。
- **動きありの切り替え**（`animate: true`）: 色だけを行き先のテーマの `motion.theme` 秒（既定 0.3）・`motion.theme_curve`（既定 fastOutSlowIn）で補間する
  （sRGB の成分ごと＝Flutter の `Color.lerp`。`UiThemeBlend`）。数・書体・明暗は最初から行き先の値（大きさまで補間すると並びが毎フレーム動くため）。
  補間の間は毎フレーム（`ScriptBridge.BeginFrame` から `UiTheme.TickFrame`。実時間）途中の表で全部品を当て直し、描き直しを頼む（`render_policy: on_demand` でも止まらない）。
  途中でまた切り替えたら、今の途中の表から新しく始める（色が跳ばない）。既定はすぐ切り替える。
- **部品でない飾り**（画面の背景・カード・見出し・説明の文字）は `SEED.UI.ThemeStyle` を付けてトークンを書く（§7.3）。コードで色を当てる画面は
  `UiTheme.Changed` で当て直す（補間の途中のフレームでは呼ばないので、補間させたいものは `ThemeStyle` か `UiTheme.Version` の見比べで）。
- **部品の文字**（ボタン・選択・数値欄・タブ・ダイアログ・トースト・ホイールの行・グラフの目盛り）は `UiTextStyle` で大きさ・書体（`font.family`）・
  太さ（`font.weight`、ダイアログの題は `font.weight_title`）を当てる（前と同じ値は書かない）。
- テーマの状態（当てているテーマ・選び方・補間）は SEEDScripting の静的な状態で、シーンの切り替えをまたいで残る（アプリは起動のときに 1 度当てればよい）。
  スクリプトを読み直すと（コンパイル・ホットリロード・事前コンパイル DLL の読み込み）既定へ戻り、`UiTheme.Changed` の受け手も外れる
  （古いアセンブリを掴まないため。`UiTheme.ResetForReload`）。**エディタに埋め込んだ Play の開始・停止ではスクリプトを読み直さないので、前の Play の最後の
  テーマ・選び方が残る**。アプリは起動のスクリプトの `OnStart` でテーマと選び方を当て（ギャラリーの `UiGalleryThemeBar.StartTheme` と同じ形）、
  `UiTheme.Changed` に足した受け手は `OnDestroy` で外す（受け手の例外はログに残して残りを呼ぶ）。

## 6. Wake or Pay のテーマの写し方（W3 でこの規則のテーマファイルを作る）

Wake or Pay の `assets/common/data/themes.json` は、テーマごとに `id`・`name`・`description`・`priceTokens`・`brightness`・`seedColor`
（Flutter 版の `ColorScheme.fromSeed` の入力）と、テーマの外で固定の `fixed`（覚悟の色）を持つ。色の配色・角丸・書体は W3 の画面で確定する
（themes.json の `_colorsNote`。2026-09-28 に読んだ時点では書体の欄は無い）。SEED のテーマへは次のように写す:

| Wake or Pay | SEED のテーマ（`assets/ui/themes/<id>.json`） |
|---|---|
| `id` | ファイル名と `"name"` |
| `brightness`（`dark` / `light`） | `"brightness"`（そのテーマはその明暗だけ。Flutter 版と同じくテーマが明暗を決める＝アプリは選び方 `Theme` のまま） |
| `seedColor` | `"seed_color"`（下の規則で 6 つのトークンを作る。面・文字は既定のテーマのその明暗の配色） |
| `fixed` の `kakugo.background` / `kakugo.danger` / `kakugo.onDanger` | `"app": { "kakugo": { "background", "danger", "on_danger" } }`（テーマの外の固定の色。全テーマ共通の基のファイル `wop_base.json` に書き、各テーマは `"extends": "wop_base.json"`） |
| `name`・`description`・`priceTokens` | 写さない（ショップの表示・値段はアプリのデータ。`ThemeLogic` のまま） |
| 配色・角丸・書体（W3 で確定） | そのテーマの `color.*`・`radius.*`・`font.*` に書く（明示したトークンは種の色の規則より勝つ） |

**種の色の規則**（`UiSeedColors.Derive`。混ぜ方は sRGB、比は WCAG 2 のコントラスト比。Material 3 の HCT の色調の段とは違う簡単な近似）:

| トークン | 暗い方 | 明るい方 |
|---|---|---|
| `color.primary` | 種の色。面（`color.surface`）と 3:1 に足りなければ白へ 5% ずつ寄せる | 種の色。面と 3:1 に足りなければ黒へ 5% ずつ寄せる |
| `color.on_primary` | 白で 4.5:1 に届けば白、届かず黒で届けば黒、どちらも届かなければ比の大きい方 | 同じ |
| `color.selected` | 主の色を面へ 50% 混ぜる | 主の色を面へ 80% 混ぜる |
| `color.on_selected` | 白に主の色を 15%。選択の色と 4.5:1 に足りなければさらに白へ | 主の色を黒へ 50%。足りなければさらに黒へ |
| `color.chart_series_1` / `color.chart_highlight` | 主の色 / 主の色の 12% | 同じ |

2026-09-28 の themes.json の 3 つに当てた値（既定のテーマの面の上。`editor/tests/UiComponentsTests` の種の色のテストと同じ計算）:

| テーマ | primary | on_primary | selected | on_selected | primary と面 | on_primary と primary |
|---|---|---|---|---|---|---|
| midnight（#6C4BFF・dark） | #6C4BFF | #FFFFFF | #453392 | #E9E4FF | 3.30 | 5.14 |
| sunrise（#FF7043・light） | #F26A40 | #000000 | #FCE1D9 | #793520 | 3.03 | 6.94 |
| forest（#2E7D32・dark） | #2E7D32 | #FFFFFF | #264C2C | #E0ECE0 | 3.30 | 5.13 |

W3 のテーマファイルの例（midnight）: `{ "name": "midnight", "extends": "wop_base.json", "brightness": "dark", "seed_color": "#6C4BFF" }`。
ファイルを置かずに themes.json から作るなら `UiTheme.FromJson($"{{\"brightness\":\"{b}\",\"seed_color\":\"{seed}\"}}")` でもよい
（extends の相対パスは `origin` から数える）。テーマの交換・選択（`ThemeLogic`）で選んだ id のテーマを `UiTheme.Apply` する。

## 7. 使い方（スクリプト）

### 7.1 読み込みと切り替え

```csharp
using SEED.UI;

var forest = UiTheme.Load("assets://ui/themes/forest.json");   // 継承を解いたテーマ（読めない・壊れていれば null。警告はログ）
if (forest is not null) UiTheme.Apply(forest);                 // すぐ切り替える（全部品をその場で当て直す）
UiTheme.Apply(forest, animate: true);                          // 色を motion.theme 秒で補間する
UiTheme.Apply(UiTheme.BuiltIn);                                // 組み込みの既定のテーマへ戻す（null でも同じ）
UiTheme.LoadAsset("assets://ui/themes/forest.json");           // Load ＋ Apply（読めれば true）
var fromData = UiTheme.FromJson("{\"brightness\":\"light\",\"seed_color\":\"#FF7043\"}");   // ファイルなしで作る

UiTheme.Changed += OnThemeChanged;                            // 切り替えごとに 1 回（OnDestroy で -= する）
float r = UiTheme.Number(UiTokens.RadiusButton);               // 今の値（補間の途中は途中の値）
Color c = UiTheme.Color("app.kakugo.danger");                  // アプリ独自のトークン

void OnThemeChanged(UiThemeChange change) => Debug.Log($"{change.Theme.Name} {change.Brightness} animated={change.Animated}");
```

**配布物（APK・パッケージ）に入れる**: テーマの JSON はスクリプトが実行中に文字列で読むので、パッケージの収録（[packaging.md](packaging.md) §2。参照を辿って
入れるファイルを決める）が拾えるように、**`assets://…/x.json` の完全なパスをスクリプトかシーン・データに書く**（ギャラリーの `UiGalleryThemeBar` の表と同じ）。
`extends` の相対パス（`"base.json"`）はファイルの参照として拾われないので、基のテーマも完全なパスでどこかに書くか、`extends` を `assets://` で書くか、
`packaging_settings.json` の `additional_folders` にテーマのフォルダを足す（2026-09-28 に試験のプロジェクトの APK で、完全なパスで書いたテーマの JSON が
pak に入ることを確かめた）。

| 値 | 内容 |
|---|---|
| `UiTheme.Current` | 今の値の表（`UiThemeData`。補間の途中は途中の表）。`Color`・`Number`・`Text`・`TryColor`・`Has`・`Describe` |
| `UiTheme.Definition` / `BuiltIn` | 当てているテーマ / 組み込みの既定のテーマ（`UiThemeDefinition`: `Name`・`Brightness`・`Supports`・`Resolve(明暗)`・`Warnings`・`ChainOrigins`） |
| `UiTheme.Brightness` / `BrightnessMode` / `SystemBrightness` | 表示している明暗 / 選び方 / 端末の明暗（System のときに問い合わせた値。不明は null） |
| `UiTheme.Version` / `IsTransitioning` | 見た目が変わるたびに増える番号（補間の毎フレームも）/ 補間の途中か |

### 7.2 明暗

```csharp
UiTheme.SetBrightnessMode(UiBrightnessMode.System);   // 端末に従う（Android の夜の表示・PC の既定のアプリ モード）
UiTheme.SetBrightnessMode(UiBrightnessMode.Light);    // 明るい方を強制（テーマが対応していれば）
UiTheme.AnimateSystemChanges = true;                  // 端末の明暗の変化で色を補間する（既定 false）
SEED.Platform.App.UiMode;                             // SystemUiMode.Dark / Light / Unknown（直接問い合わせる）
SEED.Platform.PlatformDiagnostics.SimulateUiMode(SystemUiMode.Light);   // PC の模擬だけ: 端末の明暗を差し替える（null で戻す）
```

### 7.3 部品でない飾り（`SEED.UI.ThemeStyle`）

アクタに ScriptComponent（型名 `SEED.UI.ThemeStyle`）を付け、欄にトークンの名前を書く。テーマが替わるたびに自分の Sprite・Text へ当てる。
空の欄は触らない（プレハブの値のまま）。引けないトークン（名前・型の誤り）は 1 度だけ警告する。

| 欄 | 当てる所 | 例 |
|---|---|---|
| `SpriteColor` / `BorderColor` / `CornerRadius` | Sprite の色 / 縁の色 / 四隅の角丸 | `color.background`・`color.surface` / `color.outline` / `radius.card` |
| `TextColor` / `TextSize` | Text の色 / 大きさ | `color.on_surface` / `text.title` |
| `TextFont` / `TextWeight` | Text の書体 / 太さ（既定 `font.family` / `font.weight`。空にすれば触らない） | 見出しは `font.weight_title` |

### 7.4 書体

`font.family`（assets:// の .ttf / .otf。空 = 組み込みの書体）と `font.weight`・`font.weight_title`（`SEED.Text.Weight`。キャンバスの単位で太らせる。0 = 書体そのまま）は、
部品の文字（`UiTextStyle`）と `ThemeStyle` の文字に当たる。**部品の文字は常にテーマの書体になる**（プレハブで部品のラベルに別の書体を指定しても上書きする。
特別な書体の文字は部品の外の Text にする）。太さの違う書体ファイル（400/500/700）を同時に使う仕組みは W2-6c。

## 8. トークンの表

名前・型・既定値（組み込みの既定のテーマの暗い方と、明るい方〈`light` の節〉。「〃」は暗い方と同じ）・使う部品。
**この表は `UiTokenCatalog`（コード）と `default_theme.json`（データ）から作る**（`dotnet run --project editor/tests/UiComponentsTests -- --update-docs docs/ui_theme.md`）。
テストが、部品の定数（`UiTokens`・`NavTokens`・`ChartTokens` を反射で）がすべて表にあり既定値を型どおりに持つこと、既定のテーマに表に無いトークンが無いこと、
この表が作り直したものと一致することを確かめる。トークンを足すときは定数・`UiTokenCatalog`・`default_theme.json` の 3 か所に足して表を作り直す。
2026-10-02 の部品の拡充で `size.icon`・`size.icon_gap`（アイコン）・`size.spinner`・`size.spinner_thickness`・`motion.spinner_cycle`・`motion.spinner_rotation`（不定の進捗）・`size.dialog_margin`・`size.dialog_actions_overflow_gap`・`size.dialog_item_height`・`size.dialog_items_inset`（ダイアログ）を足した（値の出典は `default_theme.json` の `_ui_extend`・[ui_components.md](ui_components.md) §13・[ui_navigation.md](ui_navigation.md) §3.2）。

<!-- token-table:begin（editor/tests/UiComponentsTests の --update-docs が作る。手で直さない） -->
| トークン | 型 | 既定（暗い方） | 既定（明るい方） | 使う部品 |
|---|---|---|---|---|
| `color.primary` | 色 | #7C5CFF | #6C4BFF | Button（Filled の塗り・Outlined と Text の文字）・Toggle（オンの台）・Checkbox（オンの塗り）・Slider・ProgressBar・ProgressRing・ProgressSpinner・RadioGroup（選んだ輪と点）・TextField（フォーカスの枠・カーソル） |
| `color.on_primary` | 色 | #FFFFFF | 〃 | Button（Filled の文字）・Checkbox（印） |
| `color.background` | 色 | #121018 | #F6F3FA | ScreenStack（画面の背景・動きの幕）・ThemeStyle（画面の背景） |
| `color.surface` | 色 | #1E1B26 | #FFFFFF | NumberField・TabBar・Dialog・BottomSheet・TopSheet（面）・ThemeStyle（カード・島）・LineChart（日付線のハンドルの縁） |
| `color.surface_variant` | 色 | #2E2A3A | #E8E3F0 | Toggle（オフの台）・Slider（溝）・SegmentedControl（台）・ProgressBar・ProgressRing（溝）・WheelPicker・TimeWheel（中央の帯）・TextField（Filled の塗り） |
| `color.on_surface` | 色 | #ECE6F5 | #1D1A24 | NumberField・SegmentedControl・ChipGroup・RadioGroup（文字）・TabBar（選んだタブ）・Dialog（題・進捗の札の文字）・DialogItem（選択肢の文字）・WheelPicker（行の文字）・TextField（文字・変換中の下線）・ThemeStyle |
| `color.on_surface_muted` | 色 | #A9A2B8 | #5E5970 | Checkbox（オフの枠）・RadioGroup（選んでいない輪）・TabBar（選んでいないタブ）・Dialog（本文）・DialogItem（選択肢のアイコンの既定の色）・BottomSheet・TopSheet（つまみ）・TextField（例の文）・ThemeStyle |
| `color.outline` | 色 | #6E6780 | #8C8699 | Button（Outlined の枠）・Toggle（オフの台の枠）・NumberField・ChipGroup（枠）・TextField（フォーカスの無い枠） |
| `color.selected` | 色 | #4A3AA8 | #E6DEFF | Button（Tonal）・SegmentedControl・ChipGroup（選んだ項目）・TabBar（選択の印） |
| `color.on_selected` | 色 | #EDE7FF | #22105C | Button（Tonal の文字）・SegmentedControl・ChipGroup（選んだ項目の文字） |
| `color.knob` | 色 | #FFFFFF | 〃 | Toggle（オンのつまみ） |
| `color.knob_off` | 色 | #A9A2B8 | #7A7489 | Toggle（オフのつまみ） |
| `color.state_layer` | 色 | #FFFFFF | #000000 | Button・Toggle・Checkbox・SegmentedControl・ChipGroup・RadioGroup・TabBar・DialogItem（押下の重ね色） |
| `color.disabled` | 色 | #3A3645 | #E4E0EA | Button・NumberField・SegmentedControl・ChipGroup・TextField（無効の塗り・枠） |
| `color.on_disabled` | 色 | #7C7689 | #A19CAB | Button・NumberField・Slider・SegmentedControl・ChipGroup・RadioGroup・TabBar・WheelPicker・TextField・DialogItem（無効の文字・選べない行） |
| `color.shadow` | 色 | #000000 | 〃 | （部品は読まない。プレハブ・画面の寸法の目安） |
| `color.error` | 色 | #FF5252 | #D32F2F | Button（Tone = Danger の塗り・枠・文字）・Dialog（危険のボタン）・DialogItem（危険の選択肢の文字とアイコン）・ThemeStyle（一覧の行の削除の面。templates/ui の list_row）・TextField（エラーの枠・カーソル） |
| `color.on_error` | 色 | #FFFFFF | 〃 | Button（Tone = Danger の Filled・Tonal の文字）・ThemeStyle（削除の面の上の文字。templates/ui の list_row） |
| `color.scrim` | 色 | #000000 | 〃 | Dialog・BottomSheet・TopSheet（幕） |
| `color.inverse_surface` | 色 | #ECE6F5 | #322F3A | Toast（面）・LineChart・BarChart（吹き出しの面） |
| `color.on_inverse_surface` | 色 | #1E1B26 | #F4EFFA | Toast（文字・図形のアイコンの既定の色）・LineChart・BarChart（吹き出しの文字） |
| `color.focus_ring` | 色 | #CBBEFF | #4A2FD6 | UiNavigator（方向キー・パッドのフォーカスの枠の線） |
| `color.chart_series_1` | 色 | #7C5CFF | #6C4BFF | LineChart・BarChart（系列 1） |
| `color.chart_series_2` | 色 | #FF5252 | #E53935 | LineChart・BarChart（系列 2） |
| `color.chart_series_3` | 色 | #4DD0E1 | #00838F | LineChart・BarChart（系列 3） |
| `color.chart_series_4` | 色 | #FFD54F | #F9A825 | LineChart・BarChart（系列 4） |
| `color.chart_grid` | 色 | #352F45 | #E6E1EE | LineChart・BarChart（格子線） |
| `color.chart_axis` | 色 | #6E6780 | #8C8699 | LineChart・BarChart（軸の線） |
| `color.chart_label` | 色 | #A9A2B8 | #5E5970 | LineChart・BarChart（目盛りの文字） |
| `color.chart_reference` | 色 | #A9A2B8 | #5E5970 | LineChart（基準線とその文字） |
| `color.chart_empty_bar` | 色 | #3A3645 | #DAD5E3 | BarChart（合計 0 の棒） |
| `color.chart_highlight` | 色 | #7C5CFF1F | #6C4BFF1A | BarChart（選んだ列の背景） |
| `color.selection` | 色 | #7C5CFF66 | #6C4BFF40 | TextField（選択の背景） |
| `radius.button` | 数 | 12 | 〃 | Button |
| `radius.chip` | 数 | 16 | 〃 | ChipGroup |
| `radius.card` | 数 | 20 | 〃 | ThemeStyle（カード・島） |
| `radius.field` | 数 | 12 | 〃 | NumberField・TextField |
| `radius.segment` | 数 | 20 | 〃 | SegmentedControl |
| `radius.checkbox` | 数 | 4 | 〃 | Checkbox・UiIcon（四角のアイコンの角丸） |
| `radius.progress` | 数 | 4 | 〃 | ProgressBar |
| `radius.wheel_band` | 数 | 8 | 〃 | WheelPicker・TimeWheel（中央の帯） |
| `radius.dialog` | 数 | 28 | 〃 | Dialog |
| `radius.sheet` | 数 | 28 | 〃 | BottomSheet・TopSheet |
| `radius.toast` | 数 | 8 | 〃 | Toast |
| `radius.popup` | 数 | 28 | 〃 | Popup（札） |
| `radius.tab_indicator` | 数 | 16 | 〃 | TabBar（選択の印） |
| `radius.focus_ring` | 数 | 4 | 〃 | UiNavigator（フォーカスの枠の角丸。角丸でない部品のとき） |
| `radius.chart_bar` | 数 | 4 | 〃 | BarChart（棒の先） |
| `radius.chart_tooltip` | 数 | 8 | 〃 | LineChart・BarChart（吹き出し） |
| `space.xs` | 数 | 4 | 〃 | （部品は読まない。プレハブ・画面の寸法の目安） |
| `space.s` | 数 | 8 | 〃 | ToastHost（トーストの間隔） |
| `space.m` | 数 | 12 | 〃 | （部品は読まない。プレハブ・画面の寸法の目安） |
| `space.l` | 数 | 16 | 〃 | ToastHost（画面の端との間）・SwipeActions（フルスワイプの文字と行の見た目の端の間） |
| `space.xl` | 数 | 24 | 〃 | （部品は読まない。プレハブ・画面の寸法の目安） |
| `size.touch_min` | 数 | 48 | 〃 | （部品は読まない。CanvasGesture の最小のヒット領域 48 dp の目安） |
| `size.border` | 数 | 1 | 〃 | Button（Outlined）・NumberField・ChipGroup・TextField（細い枠） |
| `size.check_border` | 数 | 2 | 〃 | Checkbox・RadioGroup（枠）・Toggle（オフの台の枠）・UiIcon（輪のアイコンの太さ） |
| `size.toggle_knob` | 数 | 24 | 〃 | Toggle（オン・押している間のつまみ） |
| `size.toggle_knob_off` | 数 | 16 | 〃 | Toggle（オフのつまみ） |
| `size.toggle_inset` | 数 | 4 | 〃 | （部品は読まない。プレハブ・画面の寸法の目安） |
| `size.slider_track` | 数 | 4 | 〃 | Slider（溝の太さ） |
| `size.slider_thumb` | 数 | 20 | 〃 | Slider（つまみ） |
| `size.slider_thumb_pressed` | 数 | 24 | 〃 | Slider（ドラッグ中のつまみ） |
| `size.slider_tick` | 数 | 3 | 〃 | Slider（刻みの点の直径。TickCount > 0 のとき） |
| `size.progress_bar` | 数 | 8 | 〃 | （部品は読まない。プレハブ・画面の寸法の目安） |
| `size.ring_thickness` | 数 | 6 | 〃 | ProgressRing（輪の太さ。部品の Thickness が 0 のとき） |
| `size.radio_dot` | 数 | 10 | 〃 | （部品は読まない。プレハブ・画面の寸法の目安） |
| `size.shadow_blur` | 数 | 8 | 〃 | （部品は読まない。プレハブ・画面の寸法の目安） |
| `size.shadow_offset` | 数 | 2 | 〃 | （部品は読まない。プレハブ・画面の寸法の目安） |
| `size.wheel_item` | 数 | 32 | 〃 | WheelPicker・TimeWheel（行の高さ） |
| `size.wheel_band_inset` | 数 | 9 | 〃 | WheelPicker（帯の左右の余白） |
| `size.icon` | 数 | 24 | 〃 | Toast（先頭のアイコン）・DialogItem（選択肢のアイコン）。UiIcon.Size が 0 のとき |
| `size.icon_gap` | 数 | 12 | 〃 | Toast・DialogItem（アイコンと文字の間） |
| `size.spinner` | 数 | 36 | 〃 | ProgressSpinner（大きさ。部品の Size が 0 のとき）・Dialog（進捗の札のスピナー） |
| `size.spinner_thickness` | 数 | 4 | 〃 | ProgressSpinner（弧の太さ。部品の Thickness が 0 のとき） |
| `size.tab_bar` | 数 | 64 | 〃 | TabBar（高さ） |
| `size.tab_indicator_width` | 数 | 56 | 〃 | TabBar（選択の印の幅） |
| `size.tab_indicator_height` | 数 | 28 | 〃 | TabBar（選択の印の高さ） |
| `size.dialog_width` | 数 | 312 | 〃 | Dialog（札の幅） |
| `size.dialog_padding` | 数 | 24 | 〃 | Dialog（内側の余白）・DialogItem（選択肢の左右の余白） |
| `size.dialog_title_gap` | 数 | 16 | 〃 | Dialog（題 → 本文の間隔） |
| `size.dialog_actions_gap` | 数 | 24 | 〃 | Dialog（本文〈無ければ題〉→ ボタンの行の間隔） |
| `size.dialog_button_height` | 数 | 40 | 〃 | Dialog（ボタンの高さ） |
| `size.dialog_margin` | 数 | 24 | 〃 | Dialog（札と画面の上下の端の最小の間。超える本文・選択肢の一覧はスクロール） |
| `size.dialog_actions_overflow_gap` | 数 | 0 | 〃 | Dialog（ボタンが幅に入らず縦に積むときの間） |
| `size.dialog_item_height` | 数 | 48 | 〃 | Dialog・DialogItem（選択肢の一覧の 1 行の高さ） |
| `size.dialog_items_inset` | 数 | 12 | 〃 | Dialog（選択肢の一覧の上下の空き） |
| `size.popup_margin` | 数 | 16 | 〃 | Popup（札と画面の端の余白） |
| `size.popup_max_width` | 数 | 560 | 〃 | Popup（札の幅の上限） |
| `size.popup_padding` | 数 | 8 | 〃 | Popup（札の内側の余白） |
| `size.handle_width` | 数 | 32 | 〃 | BottomSheet・TopSheet（つまみの幅） |
| `size.handle_height` | 数 | 4 | 〃 | BottomSheet・TopSheet（つまみの太さ） |
| `size.handle_area` | 数 | 24 | 〃 | （部品は読まない。プレハブ・画面の寸法の目安） |
| `size.toast_height` | 数 | 48 | 〃 | Toast（高さ） |
| `size.drag_dismiss` | 数 | 96 | 〃 | Toast・TopSheet（引いて閉じる距離） |
| `size.back_preview_shift` | 数 | 8 | 〃 | BackDispatcher（予測型の戻るのプレビューで画面をずらす量） |
| `size.focus_ring_width` | 数 | 3 | 〃 | UiNavigator（フォーカスの枠の線の太さ） |
| `size.focus_ring_gap` | 数 | 2 | 〃 | UiNavigator（フォーカスの枠の線と部品の辺の間） |
| `size.chart_line` | 数 | 2 | 〃 | LineChart（線の太さ） |
| `size.chart_dot` | 数 | 2.5 | 〃 | LineChart（点の半径） |
| `size.chart_dot_selected` | 数 | 5 | 〃 | LineChart（選んだ点の半径） |
| `size.chart_dot_min_spacing` | 数 | 6 | 〃 | LineChart（点を打つ最小の間隔） |
| `size.chart_grid` | 数 | 1 | 〃 | LineChart・BarChart（格子線の太さ） |
| `size.chart_axis` | 数 | 1 | 〃 | LineChart・BarChart（軸の線の太さ） |
| `size.chart_reference` | 数 | 1.5 | 〃 | LineChart（基準線の太さ） |
| `size.chart_y_axis` | 数 | 46 | 〃 | LineChart・BarChart（縦軸の文字の列の幅） |
| `size.chart_x_axis` | 数 | 26 | 〃 | LineChart・BarChart（横軸の文字の行の高さ） |
| `size.chart_x_label_spacing` | 数 | 48 | 〃 | LineChart・BarChart（横軸の文字の最小の間隔） |
| `size.chart_y_label_spacing` | 数 | 32 | 〃 | LineChart・BarChart（縦軸の文字の最小の間隔） |
| `size.chart_label_gap` | 数 | 6 | 〃 | LineChart・BarChart（目盛りの文字と面の間） |
| `size.chart_plot_pad` | 数 | 8 | 〃 | LineChart・BarChart（面の上と右の余白） |
| `size.chart_touch_slop` | 数 | 24 | 〃 | LineChart・BarChart（吹き出しの点を選ぶ横の距離） |
| `size.chart_smooth_step` | 数 | 2 | 〃 | LineChart（滑らかな曲線の刻み） |
| `size.chart_bar_min` | 数 | 2 | 〃 | BarChart（棒の最小の太さ） |
| `size.chart_tooltip_padding` | 数 | 8 | 〃 | LineChart・BarChart（吹き出しの内側の余白） |
| `size.chart_tooltip_gap` | 数 | 10 | 〃 | LineChart・BarChart（吹き出しと点の間） |
| `size.chart_handle` | 数 | 18 | 〃 | LineChart（日付線のハンドルの直径） |
| `size.chart_handle_border` | 数 | 2 | 〃 | LineChart（日付線のハンドルの縁の太さ） |
| `size.field_height` | 数 | 52 | 〃 | （部品は読まない。入力欄のプレハブ text_field・number_input の高さの目安） |
| `size.field_padding` | 数 | 16 | 〃 | TextField（左右の内側の余白） |
| `size.field_focus_border` | 数 | 2 | 〃 | TextField（フォーカス・エラーの枠の太さ） |
| `size.caret` | 数 | 2 | 〃 | TextField（カーソルの太さ） |
| `size.composition_underline` | 数 | 2 | 〃 | TextField（変換中の文字の下線の太さ） |
| `size.keyboard_gap` | 数 | 16 | 〃 | TextField（キーボードを避けるときの欄とキーボードの間） |
| `text.title` | 数 | 20 | 〃 | Dialog（題）・ThemeStyle（見出し） |
| `text.body` | 数 | 16 | 〃 | NumberField・Toast・Dialog（本文・進捗の札の文字）・DialogItem（選択肢の文字）・LineChart・BarChart（データが無いときの文字）・ThemeStyle |
| `text.label` | 数 | 14 | 〃 | Button・SegmentedControl・ChipGroup・RadioGroup（LabelSize の既定）・Dialog（ボタン）・ThemeStyle |
| `text.caption` | 数 | 12 | 〃 | TabBar・ThemeStyle（注記） |
| `text.wheel` | 数 | 21 | 〃 | WheelPicker・TimeWheel（行の文字） |
| `text.chart_axis` | 数 | 11 | 〃 | LineChart・BarChart（目盛りの文字） |
| `text.chart_tooltip` | 数 | 12 | 〃 | LineChart・BarChart（吹き出しの文字） |
| `text.field` | 数 | 16 | 〃 | TextField（文字の欄の文字・例の文） |
| `text.field_number` | 数 | 24 | 〃 | TextField（数値の欄〈number_input〉の文字） |
| `font.family` | 文字列 | （空＝組み込み） | 〃 | 文字を持つ全部品（Button・SegmentedControl・ChipGroup・RadioGroup・NumberField・TextField・TabBar・Dialog・DialogItem・Toast・WheelPicker・LineChart・BarChart・ThemeStyle） |
| `font.weight` | 数 | 0 | 〃 | 文字を持つ全部品（Button・SegmentedControl・ChipGroup・RadioGroup・NumberField・TextField・TabBar・Dialog・DialogItem・Toast・WheelPicker・LineChart・BarChart・ThemeStyle） |
| `font.weight_title` | 数 | 0 | 〃 | Dialog（題）・ThemeStyle（見出し） |
| `motion.short` | 数 | 0.15 | 〃 | Toggle（つまみ）・Toast・TopSheet（引いた後の戻り） |
| `motion.medium` | 数 | 0.25 | 〃 | ProgressBar・ProgressRing（値の伸び縮み） |
| `motion.repeat_interval` | 数 | 0.12 | 〃 | NumberField（長押しの連続の最初の間隔） |
| `motion.repeat_min_interval` | 数 | 0.03 | 〃 | NumberField（長押しの連続の最短の間隔） |
| `motion.repeat_accel` | 数 | 0.5 | 〃 | NumberField（長押しの連続の加速） |
| `motion.wheel` | 数 | 0.3 | 〃 | WheelPicker・TimeWheel（タップ・キー・スクリプトで動かす時間） |
| `motion.wheel_correct` | 数 | 0.2 | 〃 | WheelPicker（選べない行から戻す時間） |
| `motion.theme` | 数 | 0.3 | 〃 | UiTheme（動きありの切り替えの色の補間の時間） |
| `motion.theme_curve` | 曲線 | 0.4, 0, 0.2, 1 | 〃 | UiTheme（色の補間の曲線） |
| `motion.swipe_full` | 数 | 0.15 | 〃 | SwipeActions（フルスワイプの文字の置き場の補間） |
| `motion.swipe_dismiss` | 数 | 0.2 | 〃 | SwipeActions（確定で行を外へ流し切る） |
| `motion.swipe_collapse` | 数 | 0.2 | 〃 | （部品は読まない。一覧の持ち主が消した行の高さを畳む時間。見本の UiGallerySections） |
| `motion.spinner_cycle` | 数 | 1.333 | 〃 | ProgressSpinner（弧が伸びて縮む 1 周期） |
| `motion.spinner_rotation` | 数 | 2.222 | 〃 | ProgressSpinner（全体が 1 回転する時間） |
| `motion.caret_blink` | 数 | 0.5 | 〃 | TextField（カーソルの点滅の半周期） |
| `motion.push` | 数 | 0.3 | 〃 | ScreenStack（押し込み） |
| `motion.push_curve` | 曲線 | 0.2, 0, 0, 1 | 〃 | ScreenStack（押し込み） |
| `motion.cover` | 数 | 0.3 | 〃 | ScreenStack（覆う画面） |
| `motion.cover_curve` | 曲線 | 0.2, 0, 0, 1 | 〃 | ScreenStack（覆う画面） |
| `motion.fade` | 数 | 0.3 | 〃 | ScreenStack（フェード） |
| `motion.fade_curve` | 曲線 | 0.4, 0, 0.2, 1 | 〃 | ScreenStack（フェード） |
| `motion.overlay` | 数 | 0.22 | 〃 | TopSheet |
| `motion.overlay_curve` | 曲線 | 0, 0, 0.58, 1 | 〃 | TopSheet |
| `motion.dialog` | 数 | 0.2 | 〃 | Dialog |
| `motion.dialog_curve` | 曲線 | 0, 0, 0, 1 | 〃 | Dialog |
| `motion.sheet` | 数 | 0.25 | 〃 | BottomSheet |
| `motion.toast` | 数 | 0.2 | 〃 | Toast（出入り） |
| `motion.toast_curve` | 曲線 | 0.4, 0, 0.2, 1 | 〃 | Toast（出入り） |
| `motion.toast_short` | 数 | 2 | 〃 | ToastHost（短いトーストを出しておく時間） |
| `motion.toast_long` | 数 | 3.5 | 〃 | ToastHost（長いトーストを出しておく時間） |
| `motion.back_preview_curve` | 曲線 | 0, 0, 0, 1 | 〃 | BackDispatcher（予測型の戻るのプレビューの縮み具合） |
| `motion.chart_zoom` | 数 | 0.25 | 〃 | LineChart・BarChart（± の拡大縮小） |
| `opacity.pressed` | 数 | 0.16 | 0.1 | Button・Toggle・Checkbox・SegmentedControl・ChipGroup・RadioGroup・TabBar・DialogItem（押下の重ね色の濃さ） |
| `opacity.disabled` | 数 | 0.38 | 〃 | Toggle・Checkbox・Slider・ProgressBar・ProgressRing・ProgressSpinner・WheelPicker・TimeWheel（無効の濃さ） |
| `opacity.shadow` | 数 | 0.35 | 0.18 | （部品は読まない。プレハブ・画面の寸法の目安） |
| `opacity.wheel_dim` | 数 | 0.447 | 〃 | WheelPicker（帯の外の行の濃さ） |
| `opacity.scrim` | 数 | 0.54 | 〃 | BottomSheet・TopSheet（幕の濃さ） |
| `opacity.dialog_scrim` | 数 | 0.32 | 〃 | Dialog（幕の濃さ） |
| `opacity.chart_area` | 数 | 0.35 | 〃 | LineChart（線の下の塗りの上端） |
| `ratio.swipe_full` | 数 | 0.6 | 〃 | SwipeActions（フルスワイプで構えるずらし量。行の幅に対する） |
| `ratio.swipe_full_cancel` | 数 | 0.55 | 〃 | SwipeActions（構えを解くずらし量。行の幅に対する） |
| `ratio.push_parallax` | 数 | 0.3 | 〃 | ScreenStack（押し込みの視差） |
| `ratio.dialog_scale_from` | 数 | 0.9 | 〃 | Dialog（出るときの最初の大きさ） |
| `ratio.sheet_max_height` | 数 | 0.9 | 〃 | BottomSheet（最大の高さ） |
| `ratio.popup_max_height` | 数 | 0.8 | 〃 | Popup（札の高さの上限。安全領域の高さに対する割合） |
| `ratio.back_preview_scale` | 数 | 0.9 | 〃 | BackDispatcher（予測型の戻るのプレビューのいちばん小さい倍率） |
| `ratio.chart_bar_width` | 数 | 0.7 | 〃 | BarChart（棒の太さ） |
| `ratio.chart_empty_bar` | 数 | 0.015 | 〃 | BarChart（合計 0 の棒の高さ） |
| `ratio.chart_fling_drag` | 数 | 0.135 | 〃 | LineChart・BarChart（払った後の慣性の減速） |
| `ratio.chart_zoom_step` | 数 | 2 | 〃 | LineChart・BarChart（± の 1 回の倍率） |
| `speed.fling_dismiss` | 数 | 300 | 〃 | Toast・TopSheet（払って閉じる速さ） |
| `speed.chart_fling_stop` | 数 | 20 | 〃 | LineChart・BarChart（慣性が止まる速さ） |
| `count.toast_visible` | 数 | 3 | 〃 | ToastHost（同時に見せる数） |
| `count.chart_y_intervals` | 数 | 4 | 〃 | LineChart・BarChart（縦軸の区間の数の上限） |
| `layer.stack_step` | 数 | 10000 | 〃 | ScreenStack（1 段ぶん） |
| `layer.modal_step` | 数 | 10000 | 〃 | ModalHost（帯の中の 1 つぶん） |
| `layer.overlay` | 数 | 1000000 | 〃 | ModalHost（上からの覆いの帯） |
| `layer.sheet` | 数 | 2000000 | 〃 | ModalHost（下からのシートの帯） |
| `layer.dialog` | 数 | 3000000 | 〃 | ModalHost（ダイアログの帯） |
| `layer.toast` | 数 | 4000000 | 〃 | ToastHost（トーストの帯） |
| `layer.focus_ring` | 数 | 5000000 | 〃 | UiNavigator（フォーカスの枠。いちばん手前） |
<!-- token-table:end -->

値の出典: 色は Wake or Pay の既定のテーマ midnight（seedColor #6C4BFF・dark）から作った暗い配色（W2-4）と、同じ種の色の明るい配色（W2-9。
面・文字は Material 3 の明るい配色の段を目安）。大きさ・時間は Material 3 の部品の寸法と Android の既定の動きの時間（W2-4）、ホイールは Flutter の
CupertinoDatePicker（[ui_components.md](ui_components.md) §11）、画面の組み立ては Material 3 と Flutter 版（[ui_navigation.md](ui_navigation.md) §9）、
グラフは Flutter 版の wake_time_chart.dart・penalty_bar_chart.dart（[ui_charts.md](ui_charts.md) §7）。

## 9. 見本

- **見本のテーマ**（`templates/ui/themes/`。テンプレートライブラリの `ui` を取り込むと `assets/ui/themes/`）:

  | ファイル | 見せるもの |
  |---|---|
  | `forest.json` | Wake or Pay の forest を §6 の写し方で（`brightness: dark`・`seed_color: #2E7D32`）＋面と文字を緑がかった暗い色に置き直す。暗い方だけ |
  | `sunrise.json` | Wake or Pay の sunrise を §6 の写し方で（`brightness: light`・`seed_color: #FF7043`）＋背景と溝を暖かい色に。明るい方だけ（面・文字の残りは既定の `light` の節） |
  | `forest_round.json` | 継承と一部だけの上書き: `extends: forest.json` で角丸・文字の太さ・主の色（青緑）だけを変える。ほかは forest → 既定の順に落ちる |

- **ギャラリー**（`templates/ui/scenes/ui_gallery.scene`。ルートは dp。画面の幅と安全領域に合わせる＝W2 の手直し P2-5）: 上に固定の**テーマの帯**（`ThemeBar`・`UiGalleryThemeBar.cs`:
  テーマの 4 つのボタン〈既定・森・朝焼け・森・丸〉・明暗の選び方のセグメント〈テーマ・端末・明・暗〉・「ゆっくり」のトグル〈色の補間〉。始めに `StartTheme`〈空 = 組み込みの既定のテーマ〉と選び方「テーマのまま」を当てる）、下は縦のスクロールのページ
  （`Page/Content`）に W2-4 の形と塗り・基本の部品、W2-5 の時刻ホイール、W2-3 の一覧（100 行の `ListView`。行 `prefabs/list_row.actor` は `ThemeStyle` で結び付け。W2 の手直し P2-3 で左へ払うと削除のボタン・大きく払うとそのまま削除〈フルスワイプ。
行のスクリプト `UiGalleryListRow.cs`・削除の面は `color.error`・文字は `color.on_error`。[ui_scroll_list.md](ui_scroll_list.md) §7.1〉）、
  W2-7 の呼び出し（ダイアログ・下のシート・上の覆い・トースト。根の `ModalHost`・`ToastHost`）、W2-8 のグラフ（折れ線 30 日・積み上げの棒 12 か月。
  `UiGallerySections.cs`）。部品でない飾り（背景・見出し・島・カード・説明の文字・進捗の文字）には `ThemeStyle` を付けた。
  グラフのプレハブ（`line_chart.actor`・`bar_chart.actor`）の面と、シート・覆いの中身（`nav_sheet_content`・`nav_overlay_content`）の文字にも `ThemeStyle` を付けた。
- **ギャラリーの作り**（W2 の手直し P2-5。2026-09-29。それまでは 540×1200 dp 固定の絶対配置で、Pixel 6a〈411×914 dp〉では右が切れ、帯がステータスバーに重なった）:
  部品の名前は変えていない（見本のスクリプトは名前を深さ優先で引く）。レイアウトの規則は [canvas_camera_rework.md](canvas_camera_rework.md) §6.3〜§6.6。

  ```
  UiGallery（dp のルート）
  ├─ Background（親いっぱい。画面の端まで塗る）
  ├─ Body（CanvasComponent・親いっぱい・CanvasSafeArea 4 辺・縦の CanvasStack〈cross_align stretch〉）
  │   ├─ ThemeBar（縦の Stack・余白 左右 16 上下 8・間隔 8）: ThemeHeading・ThemeButtons（横の Stack。4 つのボタンを flex で等分・文字は真ん中）・
  │   │   ThemeOptions（横の Stack・間隔 12: 明暗の BrightnessMode〈flex・最大 300 dp。中の 4 項目も flex で等分〉・「ゆっくり」のトグルと文字）
  │   └─ Page（flex 1・切り抜き・縦のスクロール。中身の大きさは auto＝中身の並びに合う。窓そのものが縦の Stack〈cross_align stretch〉）
  │       └─ Content（縦の Stack〈余白 16・末尾 24・間隔 24〉。幅は窓の Stack が渡す）
  │           ├─ Title
  │           ├─ ShapesSection（見出し・Shapes〈CanvasWrap。形と塗りを折り返す〉）
  │           ├─ WidgetsSection（見出し・ButtonRow・ControlRow・ValueRow・SelectRow〈CanvasWrap〉・Chips〈ChipGroup に CanvasWrap〉・
  │           │   ProgressRow〈棒の上の文字の分だけ上の余白〉・ToggleAllRow）
  │           ├─ WheelsSection（見出し・Wheels〈CanvasWrap。列 = 説明の文字 ＋ 島。時刻ホイールは島の子＝重ねたまま並ぶ〉）
  │           ├─ ListSection（見出し・List〈幅は親に合わせる。行 list_row.actor も一覧の幅に合う〉）
  │           ├─ NavSection（見出し・NavButtons〈横の Stack。4 つを flex で等分〉）
  │           └─ ChartsSection（見出し・GalleryLine・GalleryBars〈幅は親に合わせる。グラフは LayoutSize を読む〉）
  ├─ Modals・Toasts（画面全体。重ねる面・トースト）
  ```

  段の縦の位置は画面の幅で変わる（折り返しの行の数が変わる。中身の高さは窓 540 で 2,230 dp・411 dp で 2,826 dp・360 dp で 3,050 dp）。
  PC の確かめ（窓 540・411 dp の模擬〈`SEED_SIM_SCALE_FACTOR=1.3139`・`SEED_SIM_SAFE_AREA=0,32,0,21`〉・360 dp〈倍率 1.5〉）: ページの右の余白の列が
  すべて背景の色（違う画素 0）、帯の 4 つのボタンの文字は 76 dp（360 dp）でも枠の中、411 dp の模擬で帯は y=32 px（ステータスバーの模擬の下）から、
  ページの下の端は 1,179 px（ジェスチャーの帯の模擬の上）。テーマ・明暗・トグルのタップ、ボタン・チップ、ホイールの値、一覧のフルスワイプの削除、ダイアログが動く。
- **デバッグの命令**（`SCRIPT_DEBUG:`）: `theme,<default|forest|sunrise|round|assets://….json>`・`theme,mode,<theme|system|light|dark>`・
  `theme,sysmode,<dark|light|unknown|system>`（模擬の端末の明暗）・`theme,animate,<on|off>`・`theme,info`（今のテーマ・明暗・版・鎖・トークンの値・部品の数）、
  `gallery,scroll,<位置>`・`gallery,scroll,<ノードの名前>`（そのノードの上の端がページの窓の上の端に来る位置へ。範囲へ収める。段の位置は画面の幅で変わるので
  名前で指す。W2 の手直し P2-5）・`gallery,open,<dialog|sheet|overlay|toast>`・`gallery,stats`・`gallery,haptic,<none|tap|vibrate>`（一覧の行のフルスワイプで構えたときの触感。
  見本の既定は vibrate）・`gallery,list`（件数・開いている行・畳んでいる数・一覧の位置と画面の矩形。W2 の手直し P2-3）、W2-4・W2-5 の `ui,…`（`ui,theme,<path>` も残す）。

## 10. 検証（2026-09-28・PC）

- **単体テスト（C#）** `dotnet run --project editor/tests/UiComponentsTests`（86 件。うち W2-9 の 16 件）: 表の完全性（定数〈反射〉・表・既定のテーマの揃いと型、
  既定のテーマに警告が無い）と docs の表の一致、読み込み（グループ・直書き・曲線・書体・app・説明・コメントと末尾のカンマ）、知らない名前・知らないグループ・
  型の誤り（15 種の警告と既定の値が残ること）、継承（近い方 → 基 → 既定・相対 / 絶対 / .. のパス・default）、壊れた鎖（読めない基・壊れた基・輪・自分・深すぎ）、
  明暗の対応（既定は両方・brightness でその明暗だけ・節で足す・受け継ぐ）と重ねる順、選び方の規則、色の補間（端・sRGB の中間・数と文字列は行き先・片側だけの色）、
  補間の進み（時間・曲線・0 秒・負と NaN の dt）、種の色（6 つのトークン・コントラストの約束・明示が勝つ・そのテーマの面で作る）、見やすさ（既定と見本の明暗で
  本文 4.5:1・主の色の上 3:1）、見本のテーマ、パス、sRGB ↔ 線形と 16 進の往復・コントラスト比。
- **Rust**: `cargo test -p SEED --lib -- platform::bridge`（102 件。模擬の `app.ui_mode`・差し替えとイベント・ThemeChanged・Play の区切り、Java の PlatformContract との名前の一致）、
  `-- redraw`（34 件。ウィンドウのイベント → 描く理由は変えていない）、`cargo build`。
- **Java**: `javac -Xlint:all`（android-36・`--release 17`・AAR の classes.jar・仮の R。main・debug の 114 ファイル）で注意 156 件＝W2-10a と同じ（AAR の classfile と
  MainActivity の this-escape だけ。足した `NightMode`・`UiModeCommand` は 0）。`cargo ndk -t arm64-v8a -P 29 build`・SeedAndroid の APK の組み立て（作業フォルダの
  ギャラリーのプロジェクト。59.8 MB）が通り、pak に見本のテーマ 3 つと重ねる面・トーストのプレハブが入った（§7.1 の配布物の約束）。
- **ギャラリー**（`templates/ui` を作業フォルダのプロジェクトの `assets/ui` へ写し、PC の Play〈540×1200・on_demand〉をデバッグの命令と IPC の指の注入で操作。
  テーマごとにページの上と途中を撮った。始めに `StartTheme` の既定のテーマを当てる〈その時点の登録 78 部品・全部で 128 部品〉）:
  既定 → 森 → 朝焼け → 森・丸で全部品（ボタン・トグル・チェック・スライダ・数値欄・選択・進捗・ホイール・一覧の行・呼び出しのボタン・グラフの線と棒と目盛り・
  テーマの帯・背景・見出し・島・カード）の色が変わり、森・丸は角丸と太さも変わる（鎖 `builtin:default>forest.json>forest_round.json`）。明るい方の強制・暗い方の強制、
  森（暗いだけ）で明るい方を強制 → 森のまま（警告 1 回）、端末に従う（この PC の OS はダーク → 暗い方。模擬の差し替えで明るい方 → `platform.ui_mode_changed` → 明るい方へ、
  暗い方へ戻る）、書体の差し替え（検査用のテーマ `font.family` = M PLUS Rounded 1c Black → 全部品の文字が太い書体）、朝焼けでのダイアログ・シート・覆い・トースト、
  3 秒の補間の途中（1.2 秒で主の色 #5D69AD＝紫と緑の間）と終わり（`UiTheme.Version` が 151 進んだ＝約 50 回/秒の当て直し）、知らない名前・型の誤り・読めない基のテーマ
  （警告 6 件・既定の値で表示）。テーマの帯のボタンの指のタップ（部品の Clicked の中から `UiTheme.Apply`）で朝焼け・明るい方の強制の既定・「ゆっくり」オンの森
  （0.3 秒の補間の途中の 1 枚と終わり）へ切り替わる。
- **回帰**（WarashibeFishing の複製。変更前の HEAD の SEED.exe・SEEDScripting.dll と変更後で撮った）: 図鑑の画面 3 フレームとも差 0 画素、図鑑のボタンの縁の 56 点のクリックは
  当たり 34・外れ 22 で、各クリックの後の画面まで変更前と一致（開始直後の 1 枚は前後とも撮る時刻で変わるので比べない。W2-10a と同じ）。

## 11. 制限と持ち越し

- **実機（Pixel 6a）は未確認**: 端末の設定の切り替え（ダークテーマ）で `onConfigurationChanged` → `platform.ui_mode_changed` → テーマが切り替わること、
  `app.ui_mode` の値。手順: ギャラリーを SeedAndroid の `run` で入れ、「端末」を選んで、端末のクイック設定のダークテーマを切り替える（`[UI] theme:` を logcat で見る）。
  → 2026-09-28 の実機の回（roadmap §3.9）: 「端末」を押したときの `app.ui_mode` は `dark`（`[SEED.UI] 端末の明暗: dark`・`mode System → Dark（端末 Dark）`）。
  テーマの 4 つのボタンの切り替え（端末に従うのまま。朝焼けは dark に非対応の警告の後 light）も実機で動いた。**ダークテーマの切り替えの追従は未実施**（fps の計測へ切り替えた）。
  → 2026-09-29（roadmap §3.9.2）: 「端末」で端末のダークテーマをオフ → オン。2 回とも `platform.ui_mode_changed` → Light / Dark（利用者「すぐ切り替わる」。設定アプリで
  変えたので知らせはアプリが前面へ戻ったときに届く）。デバッグの命令 `theme,mode,system` で選ぶと帯のセグメントの表示が「テーマ」のまま（見本の表示の同期。backlog）。
- **PC のエディタに埋め込んだ Play では OS の明暗の変化が届かない**（子のウィンドウに WM_SETTINGCHANGE が来ない。問い合わせは効く）。単体起動の SEED.exe では届く。
- **種の色の規則は Material 3 の fromSeed（HCT）の近似ではない**（色相を保って明るさだけを動かす）。第 3・第 4 の色・面の色みは作らない。
- **補間は色だけ**（大きさ・角丸・書体は最初から行き先）。補間の間は全部品を毎フレーム当て直す（部品 128・グラフ 2 のギャラリーの 3 秒の補間で、debug の PC は当て直しが約 50 回/秒＝`UiTheme.Version` が 151 進んだ）。
- **部品の文字の書体は常にテーマのもの**（部品ごとの書体の上書きは無い）。太さの違う書体ファイルの同時使用は W2-6c。
- **テーマのトークンの値の範囲は調べない**（型だけ。負の角丸・1 を超える濃さは部品の計算で収める）。
- 見本のシーン `ui_navigation.scene`・`ui_charts.scene` の飾り（背景・見出し・画面のプレハブの文字）には `ThemeStyle` を付けていない（テーマを替えても焼き込みの色のまま）。
- 既定の暗い方の `color.on_primary`（#FFFFFF）と `color.primary`（#7C5CFF）のコントラストは 4.35:1（WCAG の AA の 4.5 に少し足りない。W2-4 の値のまま）。
- エディタでテーマを編集する画面（E-07 の (b) 辞書コンポーネント）は無い（JSON を手で書く）。
- **エディタに埋め込んだ Play の区切りでテーマの状態が戻らない**（§5。SEEDScripting の静的な状態は Play の開始・停止で作り直されない＝W2-4 の `UiTheme.Use` から
  同じ。Play の区切りで C# へ知らせる入口〈例 `ResetPlaySession`〉が無い。backlog）。
