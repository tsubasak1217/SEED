# 端末プリセット — PC の Play を端末の模擬で動かす（実行先「PC（端末の模擬: …）」。2026-10-02）

ツールバーの実行先セレクタ（実行ボタンの隣のコンボ）に、端末プリセット 1 件ごとに「**PC（端末の模擬: Pixel 6a 半分）**」のような行を出し、
選んで実行ボタンを押すだけで、**別ウィンドウの Play を端末の模擬の環境変数と起動引数付きで起動する**仕組みの正典。
これまで手で付けて起動していた `SEED_SIM_WINDOW_SIZE`・`SEED_SIM_SCALE_FACTOR`・`SEED_SIM_SAFE_AREA`・`SEED_SIM_KEYBOARD_HEIGHT`・
`--render-quality=`（ランタイム側の意味は [android.md](android.md) §25.5・[rendering_profiles.md](rendering_profiles.md) §4.4・
[canvas_camera_rework.md](canvas_camera_rework.md) §6・[ui_text_input.md](ui_text_input.md)）を、データ（JSON）から組み立てて渡す。

- 端末を増やすときは `editor/config/device_presets.json` に 1 件足すだけ（コードは変えない。エディタを開き直すと読む）。
- 既存の実行先（PC・Android（自動）・Android の端末）の振る舞いは変えない。「ウィンドウを出してプレイ」の設定の値も変えない。

## 1. 置き場

| 役割 | ファイル |
|---|---|
| データ（正典） | `editor/config/device_presets.json` |
| 1 件の型（検証済み） | `editor/src/DevicePresets/DevicePreset.cs` |
| 1 件の JSON の形と検証（欠けたキーの既定） | `editor/src/DevicePresets/DevicePresetJson.cs` |
| 一覧の読み込み・フォールバック | `editor/src/DevicePresets/DevicePresetCatalog.cs` |
| 実行先の行 | `editor/src/DevicePresets/DevicePresetRunTargets.cs`（並べるのは `AndroidRun/RunTargetCatalog.cs`） |
| 環境変数と起動引数の組み立て（純粋な関数） | `editor/src/DevicePresets/DevicePresetLaunchEnvironment.cs`（`Build(preset)`） |
| 画面に収まるか | `editor/src/DevicePresets/DevicePresetScreenFit.cs` |
| 文言（要約・ツールチップの行） | `editor/src/DevicePresets/DevicePresetFormat.cs` |
| 起動に足すものの型・常駐 Play の使い回しの判断 | `editor/src/Runtime/RuntimeLaunchOverrides.cs`・`editor/src/Runtime/PlayRuntimeReusePolicy.cs` |
| WPF の結線 | `editor/src/MainWindow.DevicePresets.cs`（読み込み・起動の前の準備・画面の大きさ）、`MainWindow.xaml.cs` の `OnPlayPause`、`Runtime/RuntimeManager.cs` の `PlayAsync`・`LaunchAsync` |
| 単体テスト | `editor/tests/AndroidRunUiTests` の `DevicePresetCatalogTests`・`DevicePresetRunTargetTests` |

## 2. データ（`editor/config/device_presets.json`）

```json
{
  "format_version": 1,
  "presets": [
    {
      "id": "pixel6a-half",
      "name": "Pixel 6a 半分",
      "width_px": 540,
      "height_px": 1200,
      "scale_factor": 1.3125,
      "safe_area_px": [0, 66, 0, 32],
      "keyboard_height_px": 490,
      "render_quality": "mobile",
      "description": "…"
    }
  ]
}
```

| キー | 意味 | 無いとき | 検証（違えばその 1 件だけ捨てて Output に理由） |
|---|---|---|---|
| `id` | 識別子。実行先の行の識別子 `pcsim:<id>` と選択の記録（§3.2）に使う。大文字小文字は区別しない | **捨てる** | 空でない（前後の空白は落とす）。重複は後のものを捨てる |
| `name` | 表示名（行の文言・ツールチップ・注記） | `id` | — |
| `width_px` / `height_px` | Play の窓の最初の大きさ（物理ピクセル） | **捨てる** | 1〜16384 の整数 |
| `scale_factor` | 表示倍率（1 dp の画素数） | 1.0 | 正の有限の実数 |
| `safe_area_px` | 安全領域 `[左, 上, 右, 下]`（描画面の各辺からの距離。物理ピクセル） | `[0, 0, 0, 0]` | 4 つの 0 以上の整数・左＋右 < 幅・上＋下 < 高さ |
| `keyboard_height_px` | 入力欄にフォーカスがある間に「出ている」ことにするソフトキーボードの高さ（物理ピクセル） | 0（模擬しない） | 0 以上・高さ未満 |
| `render_quality` | 描画の品質のプリセット名（`--render-quality=` に渡す） | 指定しない（起動引数を付けない＝PC の既定） | 英数字・`-`・`_` だけ（起動引数は 1 本の文字列で渡すので空白・引用符は割れる）。ランタイムのプリセット（`runtime/config/render_presets.json` の desktop / mobile / mobile_high / mobile_low）に無い名前は、読み込み時に Output へ警告だけ出す |
| `description` | 行のツールチップの最後に出す説明 | 空 | — |

- コメント（`//`・`/* */`）と末尾のカンマを許す（人が手で書くファイル）。`_note` など知らないキーは読み飛ばす。
- **読めないときは組み込みの 1 件（Pixel 6a 半分。`DevicePresetCatalog.BuiltInPreset`）へフォールバック**して Output に警告:
  ファイルが無い・JSON の文法の誤り・根がオブジェクトでない・`presets` が配列でない・使える端末が 0 件・構成フォルダ（`editor/config`）が無い。
  組み込みの値は同梱の `pixel6a-half` と同じにしておく（`DevicePresetCatalogTests` が名前・説明まで突き合わせる）。
- `format_version` が新しすぎる（2 以上）ときは警告して読める範囲を読む。
- 読むのはエディタの起動中に 1 回（最初に実行先の一覧を作るとき）。Output に `[端末の模擬] 端末プリセット読み込み完了 — source=… 件数=…`。

### 2.1 同梱のプリセット（この順で並ぶ）

| id | 表示 | 窓（px） | 倍率 | dp | 安全領域（左,上,右,下 px） | キーボード（px） | 品質 |
|---|---|---|---|---|---|---|---|
| `pixel6a-full` | Pixel 6a 実寸 | 1080×2400 | 2.625 | 411×914 | 0,132,0,63 | 979 | mobile |
| `pixel6a-half` | Pixel 6a 半分 | 540×1200 | 1.3125 | 411×914 | 0,66,0,32 | 490 | mobile |
| `pixel6a-dp` | Pixel 6a dp 等倍 | 411×914 | 1.0 | 411×914 | 0,50,0,24 | 373 | mobile |
| `phone-small` | 小さい電話 | 720×1600 | 2.0 | 360×800 | 0,48,0,48 | 746 | mobile |
| `tablet` | タブレット | 1200×1920 | 1.5 | 800×1280 | 0,36,0,36 | 560 | mobile |

- Pixel 6a の値は実機の測定（2400x1080・420 dpi＝×2.625・安全領域 上 132 / 下 63 px は [rendering_profiles.md](rendering_profiles.md) §4.4、
  キーボード 979 px〈Simeji〉は [ui_text_input.md](ui_text_input.md) §13 の 2026-09-30 の確認）。半分・dp 等倍は dp の数が同じになるように縮めた値。
- 「小さい電話 360×800 @2」「タブレット 800×1280 @1.5」は **dp の大きさと倍率**として読み、窓は dp × 倍率（720×1600 px・1200×1920 px）にした
  （画素として読むと 180×400 dp・533×853 dp になり、電話・タブレットの模擬にならないため）。安全領域（ステータスバーとジェスチャーの帯 24 dp）と
  キーボード（Pixel 6a の実測の dp ≒ 373 dp）は仮の値。
- dp の数は Android の `Configuration.screenWidthDp` と同じく切り捨て（`DevicePreset.WidthDp`）。

## 3. 実行先の行と起動の仕組み

### 3.1 行の並び（`RunTargetCatalogBuilder`）

1. PC（常に先頭）
2. **PC（端末の模擬: 名前）**（プリセットの順に 1 件 1 行。アイコン `Icon.Platform.DeviceSimulation`〈monitor-cellphone〉。いつでも選べる。
   **Android を使えない環境でも出す**）
3. Android（自動）・Android の端末・未接続の端末・案内の行（従来どおり）

行のツールチップ: 「この PC で、Pixel 6a 半分 を模擬した別ウィンドウの Play を起動します（…）」＋ 窓（px と dp）・表示倍率・安全領域・キーボード・
描画の品質の 4 行 ＋ 説明。

### 3.2 選択の記録（`RunTargetSelectionStore`）

- 選ぶとプロジェクトの `cache/android/run_state.json` の `editor_target` に `pcsim:<id>` を書く（PC の `pc`・Android（自動）の `auto` と同じ置き場）。
- エディタの起動時（Restore）も一覧の取り直し（Keep）も、その id のプリセットが一覧にあればその行、**JSON から消えていれば PC**
  （端末の行のような「未接続」の行は作らない）。Android を使えるかには関わらない。
- `pcsim:` の記録は「Android の端末を選んでいた」に数えない（起動時に adb で端末を探しに行かない。`RunTargetMemory.PrefersAndroidDevice`）。

### 3.3 起動の流れ

1. 実行ボタン → `PlayBarPolicy` は PC と同じ判断（Edit なら `TogglePc`）→ `OnPlayPause`（PC の Play と同じ事前確認: アセット・スクリプトのコンパイル）。
2. `PrepareDevicePresetLaunch`（`MainWindow.DevicePresets.cs`）: `DevicePresetLaunchEnvironment.Build(preset)` で起動に足すものを作り、
   Output に `[端末の模擬] Pixel 6a 半分（540×1200・×1.3125） で別ウィンドウの Play を起動します: SEED_SIM_WINDOW_SIZE=540x1200 …` を出す。
   窓が画面に収まらなければトーストで警告する（§3.5。起動は止めない）。
3. **「ウィンドウを出してプレイ」がオフ（既定の埋め込み Play）でも、この行のときだけ別ウィンドウの Play**（`UsesEmbeddedPlay` が false）。
   設定の値（`EditorPreferences.WindowPlay`）は変えない。シーンは従来の別ウィンドウ Play と同じく、編集中の状態を一時ファイル（`_play_temp.scene`）に
   書いて `--scene=` で渡す（「開始シーンからプレイ」なら開始シーン）。
4. `RuntimeManager.PlayLaunchOverrides` に渡し、`LaunchAsync`（Play のときだけ）が従来の起動（`--mode=play --pipe=… --assets-root=… --scene=…
   --editor-resources=… --parent-pid=<エディタの PID>`・ヘッドレスの環境変数）の**後ろに足す**:

| 種類 | 名前 | 値（Pixel 6a 半分の例） | ランタイムの読み方 |
|---|---|---|---|
| 環境変数 | `SEED_SIM_WINDOW_SIZE` | `540x1200` | Play の窓の最初の大きさ（`app/app_init.rs`・`platform/screen/simulated.rs`） |
| 環境変数 | `SEED_SIM_SCALE_FACTOR` | `1.3125`（カルチャに依らず小数点は `.`） | 表示倍率（`app/screen_publish.rs`） |
| 環境変数 | `SEED_SIM_SAFE_AREA` | `0,66,0,32` | 安全領域（`platform/screen/simulated.rs`） |
| 環境変数 | `SEED_SIM_KEYBOARD_HEIGHT` | `490`（**0 のときは変数ごと消す**） | キーボードの模擬（`app/text_input_hooks.rs`） |
| 起動引数 | `--render-quality=<名前>` | `--render-quality=mobile`（`render_quality` が無ければ付けない） | 描画の品質（`main.rs`） |

   4 つの環境変数は、エディタ自身の環境に同じ名前があっても（手で付けてエディタを起動していたときの名残）必ずプリセットの値で上書き（または消す）する。
   ほかの環境変数（`SEED_HEADLESS` など）はそのまま受け継ぐ。Edit のランタイム（シーンパネルに埋め込むもの）には付けない。
5. 停止・一時停止・状態遷移（`EditorState` の Launching → Play → Pause / Edit）は従来の別ウィンドウ Play と同じ。

### 3.4 常駐の Play の使い回し（`PlayRuntimeReusePolicy`）

別ウィンドウの Play は Stop しても Kill せず、隠して保持する（常駐。次の Play で `LOAD_SCENE` だけ送って数秒で再生する）。
模擬の条件はプロセスの起動時の環境変数でしか与えられないので:

- 常駐を使い回すのは、従来の条件（生きている・読み直すシーンが決まっている）に加えて、**起動の条件の Key が同じ**ときだけ。
  Key は環境変数と引数から作る（`RuntimeLaunchOverrides.Key`。従来の起動は null）。違う端末・模擬から従来の別ウィンドウ Play・その逆では、常駐を閉じて新しく起動する
  （Output に「…起動の条件（端末の模擬）が違う。新規起動へフォールバック」）。
- **埋め込みの Play に戻るとき**（実行先を PC に戻して、埋め込みの設定で Play）は、模擬で起動した常駐を閉じる（`DisposePersistentPlayRuntime`。埋め込みでは
  使い回せず、端末の大きさの窓と GPU の資源を握ったまま隠れ続けるだけなので）。上書きなしの常駐は従来どおり残す。
- ビルド構成の切り替え・エディタの終了では従来どおり閉じる。

### 3.5 表示

- 実行ボタンと実行先セレクタのツールチップの最後に「**模擬: Pixel 6a 半分（540×1200・×1.3125）**」（PC の Play の実行中も。実行中は実行先を変えられないので、
  起動した端末のまま）。PC の行では従来のツールチップのまま。状態表示（EDIT / PLAY / PAUSE）は PC と同じ。
- 窓が画面に収まらないときは起動前にトースト「端末の模擬「Pixel 6a 実寸」の窓 1080×2400 px は画面（使える範囲 …×… px）に収まりません。…」と Output の行。
  使える範囲は主画面の作業領域（`SystemParameters.WorkArea`。タスクバーを除く）から普通のウィンドウの枠とタイトルバー（`SystemParameters.WindowNonClientFrameThickness`）を
  除き、エディタのウィンドウの DPI の倍率で物理ピクセルへ直したもの。1920×1080 の画面（タスクバーが下）では、計算上「Pixel 6a dp 等倍」だけが収まる
  （半分の縦 1200 も収まらない。実機の画面では未確認）。
- `seed_play`（MCP）も同じ `OnPlayPause` を通るので、実行先が端末の模擬なら模擬の別ウィンドウ Play になる。

## 4. 制限

- **実起動は未検証**（2026-10-02。このレーンではランタイムを起動しない方針。環境変数と引数の組み立て・行・選択・使い回しの判断は単体テストで確かめた）。
  初回に GUI で「Pixel 6a 半分」を選んで Play し、Output の `[SEED INIT] 窓の大きさを 540x1200 にします（SEED_SIM_WINDOW_SIZE。検証用）`・
  `PC のキーボードの模擬: 高さ 490 px`・`--render-quality=mobile` と、`Screen.DPI`（1.3125 × 基準）・`Screen.SafeArea` を見ること。
- 一時停止（最小化・Pause）すると、従来の別ウィンドウ Play と同じく窓がシーンパネルへ取り込まれ、**窓の大きさがビューポートに合わせて変わる**
  （再開で元の大きさへ戻る）。その間は模擬の画面の大きさにならない。
- 模擬の窓を手で大きくした・小さくした後に Stop すると、同じ端末の次の Play は常駐を使い回すので**その大きさのまま**になる（窓の大きさは起動時にしか与えない）。
  元の大きさに戻すには別の端末か PC を一度選んで Play する（常駐が作り直される）か、エディタを開き直す。
- 画面に収まるかは主画面とエディタの DPI で測る（DPI の違う複数の画面では誤差がある）。収まらない窓を OS がどう扱うか（はみ出す・縮める）はランタイムの起動ログの
  描画面の寸法で確かめる（1920x1080 の画面で 1080x2400 の窓になった記録がある。[rendering_profiles.md](rendering_profiles.md) §4.4）。
- 端末の向き（横向き）・切り欠きの形・ナビゲーションの種類（3 ボタン）・回転は模擬しない（ランタイムの模擬が縦の自然な向きだけ）。
- プリセットの JSON はエディタの起動中に 1 回だけ読む（書き換えたらエディタを開き直す）。
- 残件は [backlog.md](backlog.md) の「端末プリセット（PC の Play を端末の模擬で）」節。
