# Android 対応（正典）

SEED のランタイム（Rust の `runtime/`）を Android 端末で動かすための、構成・手順・現状・ロードマップの正典。
段階0（2026-09-24）時点の内容。未着手・保留の課題は [backlog.md](backlog.md) の「Android」節に集約する。

---

## 1. 目的と到達イメージ

SEED で作ったゲームを、そのまま Android 端末で動かせるようにする。
最終的には、エディタの「実行」で実行先に **PC／実機／エミュレータ** を選ぶだけで、
ビルド → インストール → 起動 → ログ表示 → 停止までが一続きで回る開発体験（段階C）を目指す。

段階0 のゴールは「エンジンを Android の共有ライブラリ `libSEED.so` にして APK に詰め、
エミュレータ／実機で **ウィンドウが開き、wgpu でフレームが描画され続ける** こと」。

---

## 2. 前提（対象端末の方針）

| 項目 | 方針 | 理由・備考 |
|---|---|---|
| ABI | 配布は **arm64-v8a のみ**。x86_64 は PC のエミュレータで開発するためだけに作る | 現行の Android 実機はほぼ arm64 |
| 最低 OS | **Android 10（API 29）** | `minSdk = 29`。Vulkan 1.1 がほぼ行き渡る世代 |
| 対象 API | API 35（Android 15） | `targetSdk = compileSdk = 35` |
| GPU | **Vulkan 1.1 必須**（wgpu の Vulkan バックエンド。GLES へは落とさない） | マニフェストで `android.hardware.vulkan.version` 0x401000 を必須宣言 |
| 16KB ページ | 対応済み（NDK r28 の既定で LOAD セグメントが 16KB 整列） | `llvm-readelf -l libSEED.so` で `0x4000` を確認済み。最終確認は段階D |
| 開発用端末 | 実機 Pixel 6a（arm64・Mali-G78）／AVD `seed_pixel6_api35`（API 35・Google APIs・x86_64・GPU host） | 実機検証は段階0 では未実施（§7） |

---

## 3. ツールチェーン

| 道具 | 版（段階0 で使用） | 入れ方・備考 |
|---|---|---|
| Rust | 1.98 | `rustup target add aarch64-linux-android x86_64-linux-android` |
| cargo-ndk | 4.1.2 | `cargo install cargo-ndk`。NDK の clang をリンカとして差し込み、`-o` で jniLibs へ .so を写す |
| Android NDK | r28（28.2.13676358） | 環境変数 `ANDROID_NDK_HOME`。r28 は 16KB ページ整列が既定 |
| Android SDK | platform 35 / build-tools 35 / platform-tools | 環境変数 `ANDROID_SDK_ROOT`（または `ANDROID_HOME`） |
| JDK | Android Studio 同梱の JBR（25） | 環境変数 `JAVA_HOME` |
| Gradle | 9.3.1（wrapper 同梱） | `runtime/android/gradlew.bat` |
| Android Gradle Plugin | **9.1.0** | AGP 9.1.0 は Gradle **9.3.1 以上**が必須（9.2.1 は 9.4.1 以上を要求するため採らなかった） |
| GameActivity | `androidx.games:games-activity:4.4.0` | winit 0.30 が使う android-activity 0.6.1 が同梱する C 側 GameActivity が 4.4.0。**必ず一致させる** |
| AppCompat / Core | `androidx.appcompat:appcompat:1.7.1` / `androidx.core:core:1.13.1` | games-activity の POM は依存を宣言していないため明示する |

マシン固有のパスはリポジトリに書かない（`gradle.properties` にも書かない）。
SDK は AGP が `ANDROID_HOME` を読み、NDK は `build_and_run.ps1` が `-Pseed.ndkPath=...` で Gradle へ渡す。
`local.properties` を Android Studio が作っても追跡されない（`runtime/android/.gitignore`）。

---

## 4. 構成

### 4.1 クレート構成

```
runtime/                      パッケージ SEED
  src/lib.rs                  ライブラリ seed_engine（エンジン本体。engine モジュールの入口だけ）
  src/main.rs                 bin SEED → SEED.exe（起動引数の解釈・Windows 固有の起動前処理）
  android/native/             パッケージ seed-android → cdylib SEED → libSEED.so
    src/entry.rs              android_main（GameActivity から呼ばれる入口）
    src/logcat/               log / 標準出力 / 標準エラー / panic を logcat（タグ SEED）へ
    src/launch.rs             アプリ専用フォルダ → エンジンの起動引数（LaunchArgs）
    src/heartbeat.rs          提示フレーム数を 3 秒ごとにログ（描画ループの生存確認）
    src/device_info.rs        起動時の端末情報ログ
    src/debug_hooks.rs        検証用フック（意図的 panic）
    src/sysprop.rs            システムプロパティの読み取り
```

- エンジン本体をライブラリ（rlib）にし、**Android 用の糊は別の小さな cdylib クレートに分けた**。
  同じクレートに `crate-type = ["cdylib", "rlib"]` を足す案は採らなかった。Windows のビルドで不要な
  `SEED.dll` が毎回作られるうえ、bin（SEED.exe）と PDB の出力名が衝突するため。
- ライブラリ名は bin 名 `SEED` と衝突させないため `seed_engine`。`doctest = false`（説明用のコード断片が多く、
  bin だった頃は doctest 自体が走らなかったので従来と同じ扱い）。
- `build.rs` の `/EXPORT:NvOptimusEnablement` 等は `rustc-link-arg-bins` に限定した
  （ライブラリの単体テスト実行ファイルには当該シンボルが無く、リンクに失敗するため）。SEED.exe のエクスポートは従来どおり。
- `runtime/android/native` はワークスペース（ルート `Cargo.toml`）のメンバー。
  **Cargo.lock と `[profile.*]`（wgpu 等の個別最適化）を Windows 版と共有する**ため。
  ルートで引数なしに `cargo build` したときの対象は `default-members` で従来の 4 クレートに固定してある。
  依存はすべて Android ターゲット限定で `src/lib.rs` も `#![cfg(target_os = "android")]` なので、
  Windows 向けにビルドされても空の DLL になるだけ（`cargo check -p seed-android` で確認済み）。
  ただしルートで `cargo build --workspace` すると、その空の `SEED.dll` と `SEED.exe` の PDB 名が同じ出力先で
  重なり、cargo が出力名の衝突を警告する恐れがある（未検証）。runtime/ で `cargo build` する通常の手順では起きない。
- 出力名 `libSEED.so` は Gradle 側（`MainActivity` の `System.loadLibrary("SEED")`、マニフェストの
  `android.app.lib_name`）とエディタのパッケージ化ウィンドウとの契約。
- ビルド成果物は Windows 版と同じ `runtime/target/`（`runtime/.cargo/config.toml`）に
  `x86_64-linux-android/` `aarch64-linux-android/` として並ぶ。Windows 版との交互ビルドで
  ホスト側の proc-macro が作り直されないことは確認済み。

### 4.2 Gradle プロジェクト（`runtime/android/`）

```
runtime/android/
  build_and_run.ps1          ビルド → install → 起動 → logcat の一括スクリプト（pwsh）
  settings.gradle.kts        リポジトリ（google / mavenCentral）と :app
  build.gradle.kts           AGP 9.1.0
  gradle.properties          AndroidX 等（マシン固有パスは書かない）
  gradlew / gradlew.bat / gradle/wrapper/   Gradle 9.3.1 の wrapper
  app/build.gradle.kts       minSdk 29 / targetSdk 35 / abiFilters arm64-v8a, x86_64 / 依存
  app/src/main/AndroidManifest.xml
  app/src/main/java/com/seedengine/runtime/MainActivity.java   GameActivity 派生（薄い）
  app/src/main/res/values/{strings,themes}.xml
  app/src/main/jniLibs/<ABI>/libSEED.so    ← cargo ndk の出力（生成物・追跡しない）
  native/                    §4.1 の cdylib クレート
```

- `applicationId` は仮に `com.seedengine.runtime`。段階C でプロジェクト設定からデータドリブンに生成する。
- **GameActivity の prefab（C++ の glue）は使わない**。android-activity が自前の glue を持つため
  （`buildFeatures { prefab = true }` や CMake の `find_package(game-activity)` を足してはいけない）。
- テーマは AppCompat 系が必須（GameActivity は AppCompatActivity 派生）。全画面・切り欠き側まで描画（`shortEdges`）。
- マニフェストの要点:
  - `screenOrientation="fullSensor"` … 4 方向に追従（端末の回転ロックも無視してセンサーに従う）。
  - `configChanges` … 回転・画面サイズ・キーボード・フォント等の構成変更で Activity を作り直させない（広めに列挙）。
  - `launchMode="singleTask"` … Activity を 1 インスタンスに保つ（§8 の「1 プロセス 1 回」の制約のため）。

### 4.3 起動の流れ

```
MainActivity（Java）: static { System.loadLibrary("SEED") }
  └ GameActivity.onCreate … android.app.lib_name=SEED を読み、GameActivity_onCreate（Rust 側 glue）へ
      └ android-activity が専用スレッドで android_main(app) を呼ぶ（runtime/android/native/src/entry.rs）
          1. logcat::init()            … android_logger・panic フック・標準出力/標準エラーの付け替え
          2. device_info::log          … SDK・機種・ABI・データパス
          3. launch::launch_args       … <外部アプリ専用フォルダ>/assets をアセットルートにした LaunchArgs（mode=Play）
          4. EventLoop::builder().with_android_app(app).build()
          5. heartbeat::spawn()        … 3 秒ごとの提示フレーム数ログ
          6. App::run_with_event_loop(event_loop, args)   … 以降はデスクトップと同じエンジン
               resumed（1 回目）   → handle_resumed（ウィンドウ・GPU・シーンの初期化。デスクトップと同じ）
               suspended           → handle_suspended（サーフェス破棄・イベントループを Wait へ）
               resumed（2 回目以降）→ handle_surface_resumed（サーフェス再生成・サイズ依存状態の更新・Poll へ）
```

### 4.4 プラットフォーム差の扱い（エンジン側）

OS ごとの「振る舞いの差」は cfg を散らさず、`runtime/src/engine/platform/mod.rs` の
**特性表 `PlatformTraits`** に集約した（データドリブン）。`platform::CURRENT` が現在のビルドの値。

| フラグ | デスクトップ | Android | 効く場所 |
|---|---|---|---|
| `app_sizes_window` | true | false | ウィンドウ生成に project_settings の `window_width/height` を使うか（Android は端末の画面＝サーフェス実サイズ） |
| `scripting_supported` | true | false | スクリプトホスト（CLR）を探すか（Android は段階B まで無し） |
| `lifecycle_diag_log` | false | true | Resized / Focused / タッチ / キー / サーフェス生成の診断ログ（`app/lifecycle_diag.rs`） |

cfg が残るのは「そもそもコンパイルできない API」の箇所だけ:
- `netcorehost` は Android では依存しない（`runtime/Cargo.toml`）。`engine/core/scripting` は Android で
  `ScriptingHost::load` が常に「未対応」を返すスタブ（`scripting/unsupported_platform.rs`）になり、
  CLR コンテキストの型は値を作れない `Infallible` になる。
- `engine/core/app_base/ipc.rs` の `read_loop` の `PeekNamedPipe` 呼び出し（Windows 専用。IPC は Android で使わない）。

描画サーフェスのライフサイクル（デスクトップでは一切走らない）:
- `renderer/surface_lifecycle.rs` … `Renderer` が `wgpu::Instance` と `Adapter` を保持し、サーフェスを
  `release_surface` / `recreate_surface` で破棄・再生成する。再生成は同じ形式（`config.format`）でしか行わない
  （全パイプラインがその形式で作られているため）。
- `app/surface_lifecycle.rs` … suspended / 2 回目以降の resumed の処理と、サーフェスが無い間の
  フレーム描画スキップ（`surface_missing`。`handle_redraw_requested` の先頭で判定）。
- `renderer/present_counter.rs` … present した回数のアトミックカウンタ（`heartbeat` が読む）。

### 4.5 データの置き場（段階0）

PC の開発時レイアウト（`<Project>/assets` とその隣の `save/`・`cache/`）を、端末のアプリ専用フォルダへそのまま写した形。

```
/sdcard/Android/data/com.seedengine.runtime/files/     … 外部アプリ専用フォルダ（取れなければ内部フォルダ）
  assets/                 アセットルート（adb push 先。無ければ初回起動時に空で作る）
    project_settings.json
    scenes/Main.scene ...
  save/                   セーブデータ（エンジンの save/path.rs が assets の親に作る）
  cache/                  派生データキャッシュ（モデルの .smdl 等。2 回目以降の起動はキャッシュヒット）
```

`assets/` が空でも、エンジンは既定値（空のシーン）で起動してクリア色の描画まで行う。
段階A で APK 内の pak（AssetManager 経由）と保存先の振替に置き換える。

---

## 5. ビルドと実行

### 5.1 一括スクリプト（推奨）

`runtime/android/build_and_run.ps1`（**pwsh 7 以降**で実行。Windows PowerShell 5.1 は対象外）。

```powershell
# 環境変数（例。ANDROID_NDK_HOME が無ければ SDK 内 ndk/ の最新版を警告付きで使う）
$env:ANDROID_SDK_ROOT = "$env:LOCALAPPDATA\Android\Sdk"
$env:ANDROID_NDK_HOME = "$env:LOCALAPPDATA\Android\Sdk\ndk\28.2.13676358"
$env:JAVA_HOME        = "C:\Program Files\Android\Android Studio\jbr"

# 両 ABI をビルドして、つながっている 1 台で起動し logcat を流す（Ctrl+C で終了）
pwsh -File runtime/android/build_and_run.ps1

# エミュレータ向けだけ・アセットを push・20 秒ぶんの logcat をファイルへ
pwsh -File runtime/android/build_and_run.ps1 -Abi x86_64 -Serial emulator-5554 `
     -AssetsDir D:\path\to\Project\assets -LogcatSeconds 20 -LogFile logcat.txt
```

| 引数 | 意味 |
|---|---|
| `-Abi arm64-v8a,x86_64` | ビルドする ABI（既定は両方） |
| `-Release` | Rust 側を `--release` でビルド（APK はデバッグ署名のまま） |
| `-Serial <adb のシリアル>` | 対象端末。**2 台以上つながっているときは必須** |
| `-AssetsDir <assets フォルダ>` | `project_settings.json` を含むフォルダを §4.5 の `assets/` へ push |
| `-SkipRustBuild` / `-SkipGradle` / `-NoInstall` / `-NoLaunch` / `-NoLogcat` | 工程を飛ばす |
| `-LogcatSeconds <秒>` / `-LogFile <パス>` | logcat を何秒集めるか（0 = Ctrl+C まで）／保存先 |

### 5.2 手で 1 段ずつ行う場合

```powershell
cd runtime/android/native
cargo ndk -t arm64-v8a -t x86_64 -P 29 -o ../app/src/main/jniLibs build      # 1. libSEED.so
cd ..
.\gradlew.bat assembleDebug "-Pseed.ndkPath=$env:ANDROID_NDK_HOME"            # 2. APK
adb -s emulator-5554 install -r app/build/outputs/apk/debug/app-debug.apk     # 3. インストール
adb -s emulator-5554 shell am start -n com.seedengine.runtime/.MainActivity   # 4. 起動
adb -s emulator-5554 logcat -s SEED RustPanic                                  # 5. ログ
```

`-P 29` は `minSdk` と同じ API レベルでリンクするため（cargo-ndk の既定は 21）。

### 5.3 所要時間と大きさ（段階0 の実測・debug）

| 項目 | 実測 |
|---|---|
| 初回クロスビルド（x86_64、依存込み） | 約 6 分 16 秒 |
| arm64 追加ビルド（ホスト側の成果物は共有） | 約 4 分 10 秒 |
| エンジンだけ変えたときの再ビルド | 約 20 秒 |
| `gradlew assembleDebug`（初回・依存取得込み） | 約 2〜3.5 分 |
| `libSEED.so`（未ストリップ・フルデバッグ情報） | 約 445 MB / ABI |
| APK 内の `libSEED.so`（AGP がシンボルを削ったもの） | 約 53 MB / ABI |
| APK（2 ABI） | 約 108 MB |

---

## 6. logcat の見方

エンジン・糊・MainActivity の出力はすべて **タグ `SEED`** に集まる。

```powershell
adb logcat -s SEED RustPanic            # 普段はこれで十分
adb logcat -v threadtime SEED:V RustPanic:V GameActivity:V AndroidRuntime:E DEBUG:V libc:F *:S   # 起動失敗・クラッシュ時
```

| 行の印 | 出どころ | 読み方 |
|---|---|---|
| `[SEED INIT] ...` | エンジンの起動ログ（eprintln! → 標準エラー転送） | Windows 版の起動ログと同じ内容。アダプタ名・バックエンド（`backend=Vulkan`）もここ |
| `[SEED SURFACE] created / released / recreated` | サーフェスの生成・破棄・再生成 | 大きさ・形式・提示モード |
| `[SEED LIFECYCLE] suspended / resumed / Resized / Focused ...` | ライフサイクル診断 | 回転・バックグラウンド復帰の確認 |
| `[SEED TOUCH] Started / Ended ...` / `[SEED KEY] ...` | タッチ・キーの受信（段階0 は受信の確認だけ） | Moved は件数だけ Ended 行にまとめる |
| `[SEED HEARTBEAT] presented_frames total=N +d in 3.0s (x fps)` | 生存確認（3 秒ごと） | バックグラウンド中は `+0`、復帰で再び増える |
| `[SEED PANIC] ...` / タグ `RustPanic` | panic フック（liblog へ同期で直接）／android-activity | 場所（ファイル:行）と backtrace |
| `wgpu_hal::... / wgpu_core::...: ...` | 依存クレートの log（android_logger） | `naga` の Info は多すぎるため Warn 以上だけ出す |

- 標準出力と標準エラーは同じ pipe で転送するので、どちらも INFO で出る（エラーは行頭の `[ERROR]` 等で判別）。
  転送スレッド経由なので、**スレッド ID は転送スレッドのもの**になる。
- `[PERF f=...]` `[PLAY_HB]` `[PLAY_DIAG n]` `[SEED FRAME n]` はエンジン既存の診断出力で、Windows 版でも同じように出ている。
- 意図的な panic で経路を確認する: `adb shell setprop debug.seed.panic_test 1` → 起動（`[SEED PANIC]` が出て
  Activity が終わり、プロセスが終了する）→ `adb shell setprop debug.seed.panic_test 0` で戻す。
- APK に入る .so はシンボルが削られているため backtrace の多くは `<unknown>`。
  未ストリップの `app/src/main/jniLibs/<ABI>/libSEED.so` と NDK の `llvm-addr2line` で後から解決できる。

---

## 7. 段階0 で確認できたこと（2026-09-24）

エミュレータ `seed_pixel6_api35`（API 35・x86_64・GPU host）で確認。**実機（Pixel 6a・arm64）は未接続のため未確認**
（arm64 の `libSEED.so` はビルドでき、AArch64・16KB 整列・依存 .so が system のものだけであることまでは確認済み）。

| 確認項目 | 結果 |
|---|---|
| 起動 → サーフェス作成 | `[SEED SURFACE] created 1080x2400 format=Rgba8UnormSrgb present_mode=Fifo` |
| wgpu アダプタ | `backend=Vulkan`（エミュレータは gfxstream 経由でホストの GPU。`ro.hardware.vulkan=ranchu`） |
| 毎フレーム描画 | `[SEED HEARTBEAT]` で約 59 fps（フォーカス時。非フォーカス時はエンジン既定の 30 fps 上限） |
| 1 枚絵 | 空アセットではクリア色、最小アセット（BrainStem.glb ＋ 平行光 1 灯）でモデルが陰影付きで描画された |
| 回転（`adb emu rotate` で 4 方向） | `ScaleFactorChanged` → `Resized 2400x1080` / `1080x2400`。Activity は作り直されず描画継続 |
| ホーム → 復帰（`KEYCODE_HOME` → `am start`） | `suspended` → `released` → heartbeat `+0` → `recreated 1080x2400` → 描画再開 |
| タッチ（`input tap` / `input swipe`） | `[SEED TOUCH] Started ... / Ended ... moves=21` |
| 戻るキー | `[SEED KEY] pressed logical=Named(BrowserBack)`。ネイティブ側が受け取るので Activity は終わらない |
| panic | `[SEED PANIC] ... panicked at ...:行:列` と backtrace → Activity 終了 → `onDestroy` でプロセス終了 |
| キャッシュ・保存先 | 2 回目の起動でモデルキャッシュがヒット（`<外部フォルダ>/cache` が書ける） |
| Windows | `cargo check` / `cargo build` が通り、`SEED.exe` が従来どおり生成（GPU 選択用エクスポートも維持） |

---

## 8. 現状の制限（段階0）

詳細と持ち越し先は [backlog.md](backlog.md) の「Android」節。

- **スクリプト（C#）は動かない**（段階B）。
- アセットは adb push した外部アプリ専用フォルダから読む。APK 内の pak は未対応（段階A）。
- タッチはログに出るだけで、入力システム（`SEED.Input`）へはつながっていない（段階A）。
- **Activity の破棄＝プロセス終了**。winit 0.30 は onDestroy をアプリへ通知しない（イベントループが終わらず
  GameActivity の onDestroy が android_main の終了を待ち続けて ANR になる）うえ、EventLoop はプロセスで 1 度しか
  作れないため、`MainActivity.onDestroy` でプロセスを終了させている。構成変更での作り直しは `configChanges` で防いでいる。
- 戻るキーは何もしない（ネイティブ側で消費される）。
- バックグラウンド中もエンジンの物理スレッド等は回り続ける（イベントループ自体は `ControlFlow::Wait` で眠る）。
- パイプラインキャッシュの置き場が「実行ファイルの隣」前提のため Android では保存されず、毎回シェーダを作り直す
  （エミュレータで 2〜6 秒）。
- ゲームパッド（gilrs）は Android 非対応（初期化に失敗して「パッド無効」で続行）。
- 音声（rodio → cpal → oboe）はビルド・リンクまで。鳴ることは未確認。
- 実機の GPU（Mali 等）でエンジンが要求する features / limits（`INDIRECT_FIRST_INSTANCE`、bind group 5 個、
  頂点シェーダでの storage buffer 等）が満たせるかは未確認。

---

## 9. 段階ロードマップ

| 段階 | 内容 |
|---|---|
| **0（完了）** | 実機/エミュレータに 1 枚絵。libSEED.so ＋ Gradle ＋ GameActivity、logcat、サーフェスの破棄・再生成、回転追従 |
| **A** | スクリプト無しでシーンを動かす: APK 内 pak（AssetManager）、保存先の振替、縦横とサーフェス再生成の仕上げ、複数指タッチ（`Input.TouchCount` / `GetTouch(i)`。PC はマウス＝指 0）、安全領域・画面の向き API、音声、logcat の整備 |
| **B** | スクリプト: ScriptPackager の事前コンパイル DLL と linux-bionic 向け CoreCLR ランタイムパックを同梱し、既存の hostfxr 経路を `Hostfxr::load_from_path` で使う。出荷時は NativeAOT を後で検討 |
| **C** | エディタ「実行」統合: 実行先セレクタ（PC／実機／エミュレータ）、ビルド → install → 起動 → logcat → 停止、pak/DLL だけ push する高速経路、パッケージ化ウィンドウの Android 出力の実働化（`build_and_run.ps1` の各関数が土台） |
| **D** | Wi-Fi 実行、実行中の差し替え、モバイル向け描画プリセット、署名／AAB／16KB ページの最終確認、NativeAOT |

---

## 10. 技術メモ（ハマりどころ）

- **netcorehost の既定機能 `nethost-download`** は `nethost-sys` の build.rs が Android で
  `platform not supported` と panic する。Android では依存自体を外した（`[target.'cfg(not(target_os = "android"))'.dependencies]`）。
- **winit 0.30 の Android**: `EventLoop::new()` は panic する（`with_android_app` 必須）。`MainEvent::Destroy` は
  warn ログだけでアプリへ届かない。`resumed` / `suspended` はネイティブウィンドウの生成・破棄ごとに何度も来る。
  winit は `ConfigChanged` のたびに `ScaleFactorChanged` を出し、続く `WindowResized` で `Resized` を出す。
- **GameActivity は戻るキーをネイティブへ渡す**（既定のキーフィルタは音量・カメラ・ズームだけ除外）。
  ネイティブ側が処理済み扱いにするので `onBackPressed` は呼ばれない。
- **android-activity の `android_main` は Rust ABI**（`#[unsafe(no_mangle)] fn android_main(app: AndroidApp)`）。
  `AndroidApp` は android-activity を直接依存にせず `winit::platform::android::activity` から使う（版の食い違い防止）。
- **C++ 標準ライブラリ**: oboe-sys と android-activity は `c++_static` を静的リンク。meshopt は build.rs の
  「linux を含むターゲット」分岐で system の `libstdc++.so` にリンクする（全端末に存在）。結果として
  `libc++_shared.so` の同梱は不要（`llvm-readelf -d` の NEEDED は liblog / libstdc++ / libandroid / libdl /
  libOpenSLES / libm / libc だけ）。
- **初回起動の没入モード確認ダイアログ**（「全画面表示中」）が出ている間はウィンドウのフォーカスが外れ、
  エンジンの非フォーカス時 30 fps 上限が掛かる。「OK」を押せば 60 fps に戻る。
- **`screenOrientation="fullSensor"` は端末の回転ロックを無視する**ため、エミュレータで
  `settings put system user_rotation` は効かない。回転の確認は `adb emu rotate`（またはエミュレータの回転ボタン）で行う。
- bash（Git Bash）から adb に `/sdcard/...` を渡すとパス変換で壊れる。`MSYS_NO_PATHCONV=1` を付けるか pwsh を使う。
- 同じ NDK でもパスの表記（`/` と `\`）が違うと cc 系の依存（oboe-sys 等）が再ビルドされる。スクリプト経由に揃えるとよい。

## 11. .NET ランタイムのスパイク結果（2026-09-24、段階B の前提）

目的: Android 上で Rust から hostfxr 経由で .NET を起動し、`UnmanagedCallersOnly` の C# 関数を呼べるかの検証。
検証コードは `runtime/android/spikes/dotnet_host/`（README 参照）。検証環境はエミュレータ x86_64 と、その ARM 変換上の arm64。
**実機とアプリプロセス内は未検証**（adb のシェル権限で `/data/local/tmp` から実行した）。

### 11.1 分かったこと

- `Microsoft.NETCore.App.Runtime.linux-bionic-{x64,arm64}`（9.0.20 / 10.0.12）の中身は **Mono**。`libcoreclr.so` は互換シムで
  `mono_jit_init` 等をエクスポートする。hostfxr 経由で起動でき、`UnmanagedCallersOnly` の呼び出しも動く。
- 本物の CoreCLR は .NET 10 の `Microsoft.NETCore.App.Runtime.android-{x64,arm64}`（10.0.12）に、**同じ版の bionic パックの
  `libhostfxr.so` / `libhostpolicy.so` を組み合わせる**と hostfxr 経由で起動できる（Microsoft 非サポートの組み合わせ）。
- 比較（x86_64 エミュレータ、3 回の中央値）:

| 項目 | Mono 9.0 | CoreCLR 10.0 |
|---|---|---|
| ランタイム起動 | 約 320 ms | 約 30 ms |
| 初回の関数取得 | 約 410 ms | 約 30 ms |
| 機能チェック一式（初回 / 2 回目） | 2.6 s / 0.15 s | 0.4 s / 0.07 s |
| プロセス全体 | 約 3.7 s | 約 0.6 s |
| collectible ALC の Unload | 回収されない（Mono 側の未実装） | 回収される |
| 暗号 API（SHA256 / RNG） | プロセス即死（端末の BoringSSL と非互換） | プロセス即死（JNI 未初期化。初期化で直る見込み） |
| 必要サイズ（1 ABI、圧縮前 / zip） | 28.5 MiB / 11.6 MiB | 70 MiB / 30.6 MiB |
| `Console.WriteLine` の出力先 | stdout | logcat（タグ DOTNET） |

- リフレクション（`GetTypes` / `Activator.CreateInstance` / `GetFields`）、`MakeGenericType(List<struct>)`、collectible ALC での別 DLL ロード、
  `DynamicMethod` / `Expression.Compile`、スレッド / GC / ファイル IO / Deflate / System.Text.Json、ハードウェア例外は、どちらのランタイムでも OK。

### 11.2 どちらでも動く呼び出し経路（段階B で採用する）

```rust
let fxr = Hostfxr::load_from_path(root.join("host/fxr/<ver>/libhostfxr.so"))?;      // dotnet-root 形式
let ctx = fxr.initialize_for_runtime_config(PdCString::from_os_str(cfg.as_os_str())?)?;
ctx.load_assembly_from_bytes(&dll_bytes, [])?;    // パス指定の load_assembly は Android 版 CoreCLR で PNSE
let loader = ctx.get_delegate_loader()?;          // get_function_with_unmanaged_callers_only へ
```

- **現行の `get_delegate_loader_for_assembly`（`load_assembly_and_get_function_pointer`）は Android 版 CoreCLR で
  `PlatformNotSupportedException`（0x80131539）になる**。Mono では動く。
- 自己完結の平坦ディレクトリ + `initialize_for_runtime_config` は「self-contained components は非対応」で NG。
  dotnet-root 形式（`host/fxr/<ver>/` と `shared/Microsoft.NETCore.App/<ver>/`、後者に `Microsoft.NETCore.App.deps.json` が必要）を使う。
- Cargo: `netcorehost = { version = "0.20", default-features = false, features = ["net9_0"] }`（`nethost-download` を外す）。
- runtimeconfig: `"framework": {"name": "Microsoft.NETCore.App", "version": "9.0.0"}` と
  `"configProperties": {"System.Globalization.Invariant": true}`（bionic に ICU が無い）。
  net9.0 の DLL を 10 のランタイムで動かすなら `"rollForward": "LatestMajor"`。
- 環境変数: 必須なし。`LD_LIBRARY_PATH` 不要（ネイティブ .so はレイアウト内から解決された）。
  推奨: `TMPDIR` / `HOME` をアプリのディレクトリへ。CoreCLR は `DOTNET_EnableDiagnostics=0`（デバッガ用 FIFO の作成が SELinux で拒否されるため）。
- trim（`PublishTrimmed`）は危険: `ComponentActivator` や型フォワードが削られ、動的ロードするスクリプト DLL が
  `TypeLoadException` になる。開発中は trim しない。

### 11.3 段階B への示唆

- APK 同梱: .so は `lib/<abi>/`、BCL の DLL は assets から filesDir 配下（版のハッシュ付きディレクトリ）へ初回起動時に展開する。
  hostfxr は framework ディレクトリに hostpolicy / coreclr があることを前提とするため、同じ場所へ展開するか
  nativeLibraryDir へのシンボリックリンクを置く（アプリプロセスでは未検証）。
- SEEDScripting とユーザースクリプトはバイト列からロードできるので、開発中は `/sdcard/Android/data/<pkg>/files/` へ
  push するだけで差し替えられる。
- スクリプト側は Android では暗号 API と `OperatingSystem.IsAndroid()` に依存しない（Mono では False を返す）。
- 未検証のリスク: アプリの private dir からの dlopen（SELinux と linker 名前空間）、ART のシグナルチェーンとの共存、
  CoreCLR の暗号 API に必要な JNI 初期化、ICU の利用可否。
