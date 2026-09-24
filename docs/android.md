# Android 対応（正典）

SEED のランタイム（Rust の `runtime/`）を Android 端末で動かすための、構成・手順・現状・ロードマップの正典。
段階0（2026-09-24）と、段階A のうち複数指タッチの入力基盤（§12）・APK 内 pak からの起動（§13）・保存先の振り替え／セーブの保護／起動基盤（§14）・画面の向きと安全領域（§15）・音声（背面での停止・音声フォーカス・音量キー。§16）、段階B の C# スクリプトの実行（APK に同梱した .NET 10 の CoreCLR。§17）、段階C-1 のビルド・配置・起動の C# 化（中核 `editor/src/Android/` とコンソールツール `SeedAndroid`。§4.6・§5）とアプリの識別情報（§18）までの内容。未着手・保留の課題は [backlog.md](backlog.md) の「Android」節に集約する。

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
| 開発用端末 | 実機 Pixel 6a（Android 16 / API 36・arm64・Mali-G78）／AVD `seed_pixel6_api35`（API 35・Google APIs・x86_64・GPU host） | どちらも段階0 の項目を確認済み（§7） |

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

マシン固有のパスはリポジトリに書かない（`gradle.properties` にも書かない）。道具の場所は SeedAndroid
（`editor/src/Android/Toolchain/AndroidToolchain.cs`）が「環境変数 → 既定の場所」の順に探し、見つからない道具はそれが要る工程で
対処付きのエラーにする。

| 道具 | 探す順 |
|---|---|
| Android SDK | `ANDROID_SDK_ROOT` → `ANDROID_HOME` → `%LOCALAPPDATA%\Android\Sdk` |
| Android NDK | `ANDROID_NDK_HOME`（`source.properties` があること）→ SDK の `ndk\` の最新版（その旨を知らせる） |
| adb | SDK の `platform-tools\adb.exe` |
| JDK | `JAVA_HOME` → Android Studio 同梱の JBR（`%ProgramFiles%\Android\Android Studio\jbr`） |
| cargo | `PATH` → `CARGO_HOME\bin` → `%USERPROFILE%\.cargo\bin`（cargo-ndk の有無は libSEED.so のビルドの直前に `cargo ndk --version` で確かめる） |
| dotnet | `DOTNET_HOST_PATH` → `PATH` → `%ProgramFiles%\dotnet` |

見つけた SDK と JDK は gradlew の環境変数 `ANDROID_HOME` / `JAVA_HOME` として、NDK は cargo ndk の `ANDROID_NDK_HOME` と
Gradle の `-Pseed.ndkPath=...` として渡す（同じ表記に揃える。表記が変わると cc 系の依存が作り直されるため。§10）。
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
    src/app_dirs.rs           アプリ専用フォルダ（files・cache）をセーブ・キャッシュの書き込み先としてエンジンへ設定（§14.1）
    src/jni_exports.rs        Java から呼ばれるネイティブ関数（onDestroy 前のセーブ書き出し。§14.2／安全領域と回転の報告。§15／
                              音声フォーカスの報告。§16）
    src/launch.rs             起動モード（APK 内 pak／開発用の置き場）の判定 → エンジンの起動引数（LaunchArgs。§13）
    src/dotnet_runtime/       同梱 .NET の展開（files/dotnet/）・スクリプトの DLL の置き場の選択 → CLR の起動材料（LaunchArgs.embedded_clr。§17）
    src/apk_package/          APK の assets/seed/ を配布物として読む読み口（ApkPackageSource・ApkAsset。§13）
    src/heartbeat.rs          提示フレーム数を 3 秒ごとにログ（描画ループの生存確認）
    src/device_info.rs        起動時の端末情報ログ
    src/debug_hooks.rs        検証用フック（意図的 panic・複数指の合成タッチ列）
    src/debug_save_test.rs    検証用フック（セーブの書き出しタイミングの確認。debug.seed.save_test。§14.6）
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
  build_and_run.ps1          SeedAndroid（§5）を従来の引数で呼ぶだけの互換ラッパー（pwsh 7。手順の中身は §4.6 の中核）
  dotnet_runtime.json        APK に同梱する .NET の設定（版・パック名・coreclr / mono の切り替え。§17.2）
  settings.gradle.kts        リポジトリ（google / mavenCentral）と :app
  build.gradle.kts           AGP 9.1.0
  gradle.properties          AndroidX 等（マシン固有パスは書かない）
  gradlew / gradlew.bat / gradle/wrapper/   Gradle 9.3.1 の wrapper
  app/build.gradle.kts       minSdk 29 / targetSdk 35 / abiFilters arm64-v8a, x86_64 / 依存
  app/src/main/AndroidManifest.xml
  app/src/main/java/com/seedengine/runtime/MainActivity.java   GameActivity 派生（薄い）
  app/src/main/java/com/seedengine/runtime/ScreenReporter.java 安全領域と画面の回転を集めてネイティブへ渡す（§15）
  app/src/main/java/com/seedengine/runtime/AudioFocusController.java 音声フォーカスの要求・放棄と、変化のネイティブへの通知（§16）
  app/src/main/java/com/seedengine/runtime/DotnetJniLibraries.java 同梱 .NET の暗号ライブラリを System.loadLibrary する（JNI_OnLoad。§17.8）
  app/src/main/res/values/{strings,themes}.xml
  app/src/main/jniLibs/<ABI>/libSEED.so    ← cargo ndk の出力（生成物・追跡しない）
  app/src/main/assets/seed/assets.pak      ← SeedPak の出力（--project のときだけ。生成物・追跡しない。§13）
  app/src/main/assets/seed/bin/            ← SeedPak --scripts の出力（スクリプトの DLL。--project のときだけ。§17.7）
  app/src/seedDotnet/                      ← 同梱 .NET（jniLibs/<ABI>/・assets/seed/dotnet/<ABI>/・libs/*.jar。生成物・追跡しない。§17.3）
  app/build/seed/                          ← SeedAndroid の作業フォルダ（置き場の記録 step_stamps.json・NuGet の取り寄せ・DLL の差し替え用。§4.6）
  native/                    §4.1 の cdylib クレート
```

- pak は `androidResources { noCompress += "pak" }` で非圧縮（STORED）のまま APK に入れる（§13.2）。
- `packaging.jniLibs.useLegacyPackaging = true`（段階B）。.so をインストール時に nativeLibraryDir へ展開させる（同梱 .NET の .so を
  dotnet-root から参照するため。§17.4）。その分 APK の .so は圧縮され、インストール時に展開される。

- `applicationId`・`versionCode`・`versionName`・ランチャーの名前（`android:label="${seedAppLabel}"`）はプロジェクト設定の
  `android` 節から Gradle のプロジェクトプロパティで受け取る（§18。渡されなければ `com.seedengine.runtime` / `SEED Runtime` 等）。
  Java のクラスの名前空間（`namespace`）は `com.seedengine.runtime` のまま（JNI の関数名が結び付いているため）。
- **GameActivity の prefab（C++ の glue）は使わない**。android-activity が自前の glue を持つため
  （`buildFeatures { prefab = true }` や CMake の `find_package(game-activity)` を足してはいけない）。
- テーマは AppCompat 系が必須（GameActivity は AppCompatActivity 派生）。全画面・切り欠き側まで描画（`shortEdges`）。
- マニフェストの要点:
  - `screenOrientation="${seedScreenOrientation}"` … プロジェクト設定の `screen_orientation` からビルド時に決まる
    （既定 `fullSensor` = 4 方向に追従・端末の回転ロックも無視してセンサーに従う。`sensorPortrait` / `sensorLandscape`。§15.1）。
  - `configChanges` … 回転・画面サイズ・キーボード・フォント等の構成変更で Activity を作り直させない（広めに列挙）。
  - `launchMode="singleTask"` … Activity を 1 インスタンスに保つ（§8 の「1 プロセス 1 回」の制約のため）。

### 4.3 起動の流れ

```
MainActivity（Java）: static { System.loadLibrary("SEED"); DotnetJniLibraries.loadAvailable() … 同梱 .NET の暗号ライブラリ（§17.8） }
  onCreate の最初（super.onCreate の前）: 環境変数 TMPDIR＝cache・HOME＝files・DOTNET_EnableDiagnostics=0（Os.setenv。§14.1・§17.6）
  └ GameActivity.onCreate … android.app.lib_name=SEED を読み、GameActivity_onCreate（Rust 側 glue）へ
      └ android-activity が専用スレッドで android_main(app) を呼ぶ（runtime/android/native/src/entry.rs）
          1. logcat::init()            … android_logger・panic フック・標準出力/標準エラーの付け替え
          2. device_info::log          … SDK・機種・ABI・データパス
          2b. app_dirs::init           … セーブ（files/save）・キャッシュ（cache）の書き込み先をエンジンへ設定（§14.1）
          3. launch::launch_args       … APK に seed/assets.pak があればパッケージ実行（配布物の読み口 package_source 付き。§13）、
                                         無ければ <アプリ専用フォルダ>/assets をアセットルートにした LaunchArgs（mode=Play。§4.5）
          3b. dotnet_runtime::prepare  … 同梱 .NET を files/dotnet/ へ展開（初回）し、スクリプトの DLL の置き場を選んで
                                         CLR の起動材料を LaunchArgs.embedded_clr へ（§17）。App::new が CLR を起動する
          4. EventLoop::builder().with_android_app(app).build()
          5. heartbeat::spawn()        … 3 秒ごとの提示フレーム数ログ
          6. App::run_with_event_loop(event_loop, args)   … 以降はデスクトップと同じエンジン
               resumed（1 回目）   → handle_resumed（ウィンドウ・GPU・シーンの初期化。デスクトップと同じ）
               suspended           → enter_background（セーブ・パイプラインキャッシュの書き出し → 物理停止 → 音声の出力を一時停止。§14・§16）
                                     → handle_suspended（サーフェス破棄・イベントループを Wait へ）
               resumed（2 回目以降）→ handle_surface_resumed（サーフェス再生成・サイズ依存状態の更新・Poll へ）
                                     → enter_foreground（物理再開・背面にいた時間を捨てる・音声の出力を再開。§14.4・§16）
  onCreate の最後（super.onCreate の後）: 音量キーの対象をメディアの音量に固定（setVolumeControlStream。§16.4）・
      ScreenReporter.attach … 以降、WindowInsets・レイアウト・構成・表示の変化のたびに
      安全領域と回転を JNI（nativeOnScreenChanged）で報告し、エンジンがフレームごとに SEED.Screen の値へ反映する（§15）
  onResume → AudioFocusController.request（音声フォーカスを要求）/ onPause → abandon（手放す）。結果と OS からの変化は
      JNI（nativeOnAudioFocusChanged）で報告し、エンジンがイベントループの次の周回（about_to_wait）で音声の出力を止める・戻す・下げる（§16）
  MainActivity.onDestroy → nativeFlushSaveData（JNI。セーブの未書き出し分）→ Process.killProcess（§14.2）
```

### 4.4 プラットフォーム差の扱い（エンジン側）

OS ごとの「振る舞いの差」は cfg を散らさず、`runtime/src/engine/platform/mod.rs` の
**特性表 `PlatformTraits`** に集約した（データドリブン）。`platform::CURRENT` が現在のビルドの値。

| フラグ | デスクトップ | Android | 効く場所 |
|---|---|---|---|
| `app_sizes_window` | true | false | ウィンドウ生成に project_settings の `window_width/height` を使うか（Android は端末の画面＝サーフェス実サイズ） |
| `script_host_source` | `SearchFiles` | `EmbeddedOnly` | 起動材料（`LaunchArgs.embedded_clr`）が無いときにスクリプトホスト（CLR）をどう用意するか。PC はファイルを探す、Android は同梱 .NET だけ（無ければスクリプト無し。§17） |
| `lifecycle_diag_log` | false | true | Resized / Focused / タッチ / キー / サーフェス生成の診断ログ（`app/lifecycle_diag.rs`）と、タッチ状態のフレーム単位ログ（`app/touch_diag.rs`） |
| `touch_supported` | false | true | スクリプトの `Input.TouchSupported`（タッチ主体の端末か。§12） |
| `touch_drives_mouse` | false | true | 指0 がマウス（カーソル座標＋左ボタン）を駆動するか（`input/touch/bridge.rs`。§12.2） |
| `mouse_simulates_touch` | true | false | マウス左ボタンで指を 1 本合成するか（同上。`touch_drives_mouse` と排他） |
| `key_remap` | 空 | 戻るキー → Escape | OS 固有のキーをエンジンの KeyCode へ置き換える表（`core/input/key_remap.rs`。§14.5） |
| `reference_dpi` | 96 | 160 | 表示倍率 1.0 に当たる DPI。スクリプトの `Screen.DPI` = winit の scale_factor × この値（§15.3） |

実行時にしか分からない値（Android のアプリ専用フォルダ）は特性表ではなく `platform/paths.rs` の `PlatformPaths`
（起動時に 1 回だけ設定する値）に持つ。Android の糊が設定し、デスクトップは設定しない（§14.1）。
実行中に何度も変わる値（安全領域・表示の回転）は `platform/screen/` に持つ。Android の糊（JNI）が報告し、エンジンがフレームごとに
読んでスクリプトへ見せる写しを作る。デスクトップは報告しない（全画面・縦横比の向き。§15）。
音声フォーカスの状態も同じく `platform/audio_focus.rs` に持ち、Android の糊（JNI）が報告してエンジンがイベントループの 1 周ごとに読む。
デスクトップは報告しない（ずっと「持っている」）ので、音声の出力は止まらない（§16）。

cfg が残るのは「そもそもコンパイルできない API」の箇所だけ:
- `netcorehost` は Android では既定の機能（nethost-download）を外して依存する（`runtime/Cargo.toml`。§10）。
  PC の探索経路（nethost・パス指定の読み込み。`scripting/clr_host/desktop.rs`）は Android ではコンパイルせず、
  `ScriptingHost::load` は「使わない経路」として理由を返すだけ。同梱 .NET の起動（`clr_host/embedded.rs`）は全プラットフォーム共通。
  ヒープポインタのタグ付けの無効化（`clr_host/heap_tagging.rs`）は Android だけ。
- `engine/core/app_base/ipc.rs` の `read_loop` の `PeekNamedPipe` 呼び出し（Windows 専用。IPC は Android で使わない）。

描画サーフェスのライフサイクル（デスクトップでは一切走らない）:
- `renderer/surface_lifecycle.rs` … `Renderer` が `wgpu::Instance` と `Adapter` を保持し、サーフェスを
  `release_surface` / `recreate_surface` で破棄・再生成する。再生成は同じ形式（`config.format`）でしか行わない
  （全パイプラインがその形式で作られているため）。
- `app/surface_lifecycle.rs` … suspended / 2 回目以降の resumed の処理と、サーフェスが無い間の
  フレーム描画スキップ（`surface_missing`。`handle_redraw_requested` の先頭で判定）。
- `app/background_lifecycle.rs` … 背面・前面への出入りでのセーブとパイプラインキャッシュの書き出し、
  物理スレッドの停止・再開（`core/background_gate.rs`）、ゲーム時間の取り戻し防止（§14）、音声の出力の一時停止・再開
  （`app/audio_output_sync.rs` 経由。§16）。
- `renderer/present_counter.rs` … present した回数のアトミックカウンタ（`heartbeat` が読む）。

### 4.5 データの置き場

起動モードは APK 内の pak の有無で決まる（§13.1）。APK に `assets/seed/assets.pak` があればパッケージ実行で、
アセットは APK から読む。無ければ以下の「開発用の置き場」から読む（この節の残り）。

PC の開発時レイアウト（`<Project>/assets`）を、端末のアプリ専用フォルダへそのまま写した形。
セーブとキャッシュの置き場は起動モードに関係なく決まっている（§14.1）。

```
/data/user/0/com.seedengine.runtime/
  files/                  データルート 1（下の表）。環境変数 HOME
    assets/               アセットルート（無ければ初回起動時に空で作る）
      project_settings.json
      scenes/Main.scene ...
    save/save.json        セーブデータ（パッケージ実行でも同じ。§14.1）
  cache/                  派生データキャッシュ（モデルの .smdl・パイプラインキャッシュ）。環境変数 TMPDIR。
                          OS が容量不足のときに消すことがある（消えても再生成される）
```

段階0〜A-2 で使っていた `files/cache/`（アセットルートの親の cache）は使われなくなった（消してよい）。

データルートは次の順に見て、`assets/project_settings.json` がある最初のものを使う（`runtime/android/native/src/launch.rs`）。
どちらにも無ければ 1 を使い、空の `assets/` でエンジンは既定値（空のシーン）のまま起動してクリア色の描画まで行う。

| 順 | 場所 | 置き方 |
|---|---|---|
| 1 | 内部アプリ専用フォルダ `/data/user/0/com.seedengine.runtime/files` | `SeedAndroid run --assets-dir`（ps1 の `-AssetsDir`）。デバッグ版 APK の `run-as` でアプリの権限になり、tar を流し込む（§5）。**実機でもエミュレータでも読める** |
| 2 | 外部アプリ専用フォルダ `/sdcard/Android/data/com.seedengine.runtime/files` | 手で `adb push`。エミュレータでは読めるが、**実機（Android 11 以降）では adb push が作ったフォルダが shell の所有になりアプリから読めない**（Permission denied。起動時に警告を出す） |

APK 内の pak（パッケージ実行）は §13。パッケージ実行でもアセットルートはこの内部フォルダの `assets/`
（PAK にも APK にも無いアセットのフォールバック先。作らない）。セーブ・キャッシュの置き場は §14.1。
アプリ ID を変えたとき（§18）は `/data/user/0/<アプリ ID>/` になる（端末側のコードはパスを決め打ちしていない）。

### 4.6 ビルド・配置・起動の中核（`editor/src/Android/`。段階C-1・2026-09-25）

Android の一連の手順（段階B までは `runtime/android/build_and_run.ps1` の中身）を、C# の WPF 非依存のクラス群にした。
エディタ本体（`editor/SEEDEditor.csproj` は `src/**` を含む）とコンソールツール `editor/tools/SeedAndroid`（ファイルをリンクして取り込む。§5.1）が
同じクラスを使う（PAK の `AssetPakBuilder` をパッケージ化ウィンドウと SeedPak が共有するのと同じ関係）。単体テストは `editor/tests/AndroidPipelineTests`。

```
editor/src/Android/
  Common/     AndroidRuntimeContract（端末側のコード・Gradle と一致させる名前と値）・AndroidAbi（ABI の表と端末の abilist からの選び方）
  Toolchain/  AndroidToolchain（SDK / NDK / adb / JDK / cargo / dotnet の場所。§3）・AndroidEnginePaths（リポジトリの置き場）
  Processes/  ChildProcessRunner（子プロセスの起動・行単位の出力・標準入力・中断で子孫ごと終了）・MixedEncodingLineReader（UTF-8 と ANSI の混在）
  Adb/        AdbClient（devices -l・getprop・install・pm path・run-as の tar 展開・am start / force-stop・logcat）・AdbDeviceListParser・
              AndroidDeviceSelector・RunAsTarArchive（.NET の TarWriter。外部の tar は使わない）・AndroidLogcatSession
  Project/    AndroidProjectResolver（--project / --assets-dir）・AndroidProjectSettingsReader（screen_orientation と android 節）・
              AndroidAppIdentityResolver（アプリの識別情報の既定値と検査。§18）
  Dotnet/     DotnetRuntimeSettings（dotnet_runtime.json）・NuGetRuntimePackRestorer・DotnetRuntimeBundle（dotnet-root への組み立てと目録。§17）
  Gradle/     GradleInvocation（gradlew の引数と -P／環境変数の組み立て）
  Plan/       AndroidBuildPlan（どの工程を飛ばすか。純粋な処理）・AndroidStepFingerprints / AndroidFingerprint（指紋）・AndroidBuildInputs（入力の表）
  State/      AndroidStepStamps（置き場の中身の記録。エンジン側）・AndroidRunState（プロジェクトの実行状態）
  Steps/      工程ごとの実装（NativeBuild・PackageContent・DotnetBundle・GradleBuild・Install・PushAssets・PushScripts・Launch・Logcat）
  Pipeline/   AndroidRunPipeline（本体）・AndroidRunRequest（指定）・AndroidPipelineEvent（進み具合）・AndroidDeviceActions（一覧・停止・logcat）
```

**入口（段階C-2 のエディタ統合で使うもの）**

| やること | 呼ぶもの |
|---|---|
| 準備 | `AndroidEnginePaths.Locate(AppContext.BaseDirectory, Environment.CurrentDirectory)`・`AndroidToolchain.Detect()` |
| 実行先の一覧（実機／エミュレータ・ABI 付き） | `new AndroidDeviceActions(toolchain).ListDevicesAsync(ct)` → `AndroidDeviceEntry`（`Device.Kind` が `Physical` / `Emulator`・`Device.IsReady`・`BuildAbi`） |
| ビルド → インストール → 起動 → logcat | `new AndroidRunPipeline(engine, toolchain).RunAsync(new AndroidRunRequest { Goal = AndroidRunGoal.Run, ProjectDir = …, Serial = … }, progress, ct)` |
| スクリプトの DLL だけ差し替え | 同じ `RunAsync` を `Goal = AndroidRunGoal.Push` で |
| 停止ボタン | `ct` を取り消す（子プロセスを止める。logcat の途中なら「止めた」＝成功）＋ `AndroidDeviceActions.StopAppAsync(serial, result.Identity.ApplicationId, ct)` |
| 前回の実行先（セレクタの既定値） | `AndroidRunState.Load(AndroidRunState.PathForProject(projectRoot)).LastTarget` |

- `RunAsync` は全体をスレッドプールで動かし（UI スレッドから `await` しても止めない）、失敗しても例外は投げず `AndroidPipelineResult`
  （`Succeeded` / `Canceled` / `FailureKind` / `FailureMessage` / 工程ごとの結果 / 計画 / 端末 / アプリの識別情報）を返す。
- 進み具合は `IProgress<AndroidPipelineEvent>` に届く: `AndroidPhaseStarted`（何番目か・行う理由）/ `AndroidPhaseFinished`（成功・飛ばした・失敗・中断と
  所要時間・一行の結果。飛ばした工程は Started 無しでこれだけ）/ `AndroidLogLine`（説明・子プロセスの標準出力・標準エラー・警告・エラー・logcat の 1 行）/
  `AndroidProgressChanged`（0〜1）/ `AndroidPipelineError`（失敗の種類 `AndroidFailureKind` と説明。最後に 1 回）。子プロセスの出力を読むスレッドからも
  届くので受け手はスレッド安全にする（WPF の `Progress<T>` なら UI スレッドへ順に送られる）。

**工程を飛ばす判断（`Plan/AndroidBuildPlan.cs`。純粋な処理で、材料は準備の段階で集める）**

| 工程 | 入力の指紋（ファイルは相対パス・大きさ・更新時刻） | 出力の同一性 |
|---|---|---|
| libSEED.so（ABI ごと） | `AndroidBuildInputs.NativeSources`（Cargo.toml / Cargo.lock・runtime/src・runtime/android/native・plugin_api・埋め込むアイコン）＋ ABI・プロファイル・API レベル・NDK | `jniLibs/<ABI>/libSEED.so` |
| pak とスクリプト | プロジェクトのアセットルート全体＋ `PackageToolSources`（SeedPak・パッケージ化のコード・scripting/・runtime/src）＋プロジェクトの場所。プロジェクトを APK に入れないなら「置き場を空にする」 | `app/src/main/assets/seed/` の一覧 |
| 同梱 .NET | `dotnet_runtime.json` の中身・ABI・組み立て方の版 | `app/src/seedDotnet/` の一覧 |
| APK | 上流の出力の同一性・Gradle のソース（`GradleSources`）・渡すプロパティ（ABI・向き・識別情報・NDK） | `app-debug.apk` |
| インストール | — | 前回自分がこの端末へ入れた APK の SHA-256 と、入れた直後の `pm path`（インストールのたびに変わる）が、今の APK と端末の `pm path` に一致するか |

- 記録が無い・入力が違う・出力が無い・出力が記録と違う（外で作り直された・消された）なら行う。上流を作り直すなら Gradle とインストールも行う。
  libSEED.so は古い ABI だけを作る。開発用の転送・起動・logcat は指定どおり行う。
- 明示の `--skip-rust` / `--skip-gradle` / `--no-install` 等（ps1 の `-SkipRustBuild` 等）は自動判定より強い。`--rebuild` は自動で飛ばさない。
- 中身そのもの（数 GB になり得るアセット、1 本 450 MB の .so）は読まない。中身が変わったのに大きさも更新時刻も同じ、という稀な場合は `--rebuild`。
  入力の表（`AndroidBuildInputs`）に足し忘れた入力は「変えたのに作り直されない」になる（多めに入れる分には安全）。

**記録の置き場**

| 記録 | 置き場 | 中身 |
|---|---|---|
| 置き場の中身（`AndroidStepStamps`） | `runtime/android/app/build/seed/step_stamps.json`（エンジン側。`gradlew clean` で消える＝全部作り直すだけ） | 工程ごとに「作ったときの入力の指紋と出力の同一性」、最後の APK の SHA-256・ABI・アプリ ID |
| 実行状態（`AndroidRunState`） | `<プロジェクト>/cache/android/run_state.json`（[project_system.md](project_system.md) §1 の `cache/`。プロジェクトが無ければ `runtime/android/app/build/seed/run_state.json`） | 前回の実行先（シリアル・種類・機種・ABI・アプリ ID）、端末ごとに自分が入れた APK（SHA-256・`pm path`）、前回の実行の結果・工程ごとの判断・指紋 |

- 置き場の記録をプロジェクトの `cache/` に置かないのは、Gradle の置き場（jniLibs・assets/seed・seedDotnet・APK）がリポジトリに 1 つずつしかなく、
  別のプロジェクトをビルドすると中身が入れ替わるため（置き場と一緒に持たないと、切り替えた後に別のプロジェクトの pak のまま「変更なし」と判断してしまう）。
- 壊れた・版の違う記録は「記録なし」として扱う（作り直す・入れ直すだけ）。

---

## 5. ビルドと実行

### 5.1 SeedAndroid（推奨）

`editor/tools/SeedAndroid`（.NET 10 のコンソールアプリ。中身は §4.6 の中核）。リポジトリの中で実行する（道具の場所は §3）。

```powershell
# 端末の一覧（シリアル・種類・状態・ABI・機種）。--json で JSON
dotnet run --project editor/tools/SeedAndroid -- devices

# 一気通貫: ビルド → 同梱 → インストール → 起動 → logcat（ABI は端末から判定。Ctrl+C で logcat を止めて終える）
dotnet run --project editor/tools/SeedAndroid -- run --project D:\path\to\Project --serial emulator-5554

# 20 秒ぶんの logcat を UTF-8 で保存して終える
dotnet run --project editor/tools/SeedAndroid -- run --project D:\path\to\Project --serial <実機> --logcat-seconds 20 --log-file logcat.txt

# APK を作るだけ（端末が 1 台に決まればその ABI、決まらなければ両方）／作って入れるまで
dotnet run --project editor/tools/SeedAndroid -- build --project D:\path\to\Project --abi arm64-v8a
dotnet run --project editor/tools/SeedAndroid -- install --project D:\path\to\Project --serial <実機>

# スクリプトの DLL だけを作り直して端末の files/bin/ へ送り、起動し直す（APK は作り直さない。§17.7）
dotnet run --project editor/tools/SeedAndroid -- push --project D:\path\to\Project --serial emulator-5554

# 開発用: pak の無い APK にして、アセットを run-as で端末の files/assets へ送る（§4.5）
dotnet run --project editor/tools/SeedAndroid -- run --assets-dir D:\path\to\Project\assets --serial emulator-5554

# 止める・logcat だけを流す
dotnet run --project editor/tools/SeedAndroid -- stop --project D:\path\to\Project
dotnet run --project editor/tools/SeedAndroid -- logcat --serial emulator-5554
```

| サブコマンド | 行う工程 |
|---|---|
| `devices` | 端末の一覧（`--json` で JSON。使える端末は ABI も読む） |
| `build` | libSEED.so → pak とスクリプト → 同梱 .NET → APK |
| `install` | build ＋ インストール（Gradle の `installDebug` と同じく、要ればビルドする） |
| `run` | install ＋（`--assets-dir` のアセット・`--push-scripts` の DLL の転送）＋ 起動 ＋ logcat |
| `push` | スクリプトの DLL（と `--assets-dir` のアセット）の転送 ＋ 起動 ＋ logcat |
| `stop` | `am force-stop <アプリ ID>`（アプリ ID は `--app-id`、無ければ `--project` / `--assets-dir` の設定から） |
| `logcat` | logcat（`--since <端末の時刻>` から。省略時は今から） |

| オプション | 意味 |
|---|---|
| `--project <フォルダ>` | プロジェクト（`.seedproj` か `assets/` を持つフォルダ、またはアセットルートそのもの。規則は SeedPak と共有の `ProjectFolderResolver`）。APK に pak とスクリプトを入れる（パッケージ実行。§13・§17）。画面の向き・アプリの識別情報（§18）もここの `project_settings.json` から読む |
| `--assets-dir <フォルダ>` | 開発用: pak の無い APK にして、このアセットフォルダを run-as で端末の `files/assets` へ送る（`--project` と排他。端末は APK の pak を優先するため） |
| `--serial <シリアル>` | 対象の端末。省略時は使える端末がちょうど 1 台のときそれ（2 台以上ならエラー。前回の実行先を添える。私物の実機へ勝手に入れないため） |
| `--abi <ABI[,ABI]>` | `arm64-v8a` / `x86_64`。省略時は端末の `ro.product.cpu.abilist` の先頭から選ぶ（端末が決まらなければ両方） |
| `--release` | Rust 側を `--release` でビルド（APK はデバッグ署名のまま） |
| `--config <JSON>` | 指定をまとめた設定 JSON（キーは `AndroidRunRequest` の snake_case: `project` / `assets_dir` / `serial` / `abis` / `release` / `skip_rust_build` / `skip_gradle` / `no_install` / `no_launch` / `no_logcat` / `push_scripts` / `rebuild` / `logcat_seconds` / `log_file`。相対パスは JSON のフォルダから。コマンドラインが優先） |
| `--skip-rust` / `--skip-gradle` / `--no-install` / `--no-launch` / `--no-logcat` | 工程を飛ばす（`--skip-gradle` は pak とスクリプト・同梱 .NET・Gradle をまとめて飛ばす） |
| `--push-scripts` | `run` でもスクリプトの DLL を作り直して `files/bin/` へ送る |
| `--rebuild` | 変更の有無で工程を自動で飛ばさない（すべて作り直し、入れ直す） |
| `--logcat-seconds <秒>` / `--log-file <パス>` | logcat を流す秒数（0 か省略で止めるまで）／保存先（UTF-8） |
| `--app-id <ID>` / `--since <時刻>` / `--json` | `stop` のアプリ ID ／ `logcat` の起点／ `devices` の JSON |

- 入力が前回から変わっていない工程は自動で飛ばす（§4.6）。準備の段階で「行う／飛ばす」と理由を一覧で出し、最後に工程ごとの結果と所要時間をまとめる。
- 終了コード: `0` 成功 / `1` 指定の誤り / `2` 道具が無い / `3` 端末が無い・選べない / `4` ビルドの失敗 / `5` 端末の操作の失敗 / `130` 中断（Ctrl+C）。
- Ctrl+C は中断の合図として、自分が起動した子プロセス（cargo・Gradle・adb）とその子孫を止めて終わる。logcat を流している間の Ctrl+C は「止めた」＝成功。
- 子プロセスの出力は行ごとに「厳密な UTF-8 として読めるか」で文字コードを見分ける（Gradle・dotnet が ANSI コードページで書く行も化けない）。
  SeedAndroid 自身の出力をファイル・パイプへ向けたときと logcat の保存（`--log-file`）は UTF-8（以前の `-LogFile` の文字化けは解消）。
- gradlew（バッチファイル＝cmd.exe を通る）へ渡す値のうち、cmd.exe が解釈する文字（`" % ! ^ & | < > ( )`）を含むもの（アプリ名等）は `-P` ではなく
  環境変数 `ORG_GRADLE_PROJECT_seed.*` で渡す（Gradle の仕様で `-P` と同じプロジェクトプロパティになる。引数の破損・コマンドの注入を防ぐ）。

**build_and_run.ps1（互換ラッパー）**

`runtime/android/build_and_run.ps1` は従来の引数を受け取り、`SeedAndroid run` の引数へ置き換えて `dotnet run` で呼ぶだけ（手順の中身は持たない）。
pwsh 7 以降で実行する（日本語を含むため Windows PowerShell 5.1 は対象外。tar のパイプが無くなったので 7.4 の制限は無い）。終了コードは SeedAndroid のもの。

| ps1 の引数 | SeedAndroid の引数 |
|---|---|
| `-Abi a,b` | `--abi a,b`（**省略時は渡さない**＝端末から判定。従来は両方） |
| `-Release` / `-Serial` | `--release` / `--serial` |
| `-ProjectDir` / `-AssetsDir` | `--project` / `--assets-dir`（呼んだ場所からの相対パスは絶対パスにして渡す） |
| `-PushScripts` | `--push-scripts` |
| `-SkipRustBuild` / `-SkipGradle` / `-NoInstall` / `-NoLaunch` / `-NoLogcat` | `--skip-rust` / `--skip-gradle` / `--no-install` / `--no-launch` / `--no-logcat` |
| `-LogcatSeconds` / `-LogFile` | `--logcat-seconds` / `--log-file` |

従来との違い: 変わっていない工程を自動で飛ばす（すべて作り直すには SeedAndroid の `--rebuild`）、`-Abi` の省略時は端末の ABI だけを作る、
`-ProjectDir` と `-SkipGradle` を（`-PushScripts` 無しで）一緒に指定できる（プロジェクトのアプリ ID でインストール・起動する）、`-LogFile` が UTF-8。

```powershell
pwsh -File runtime/android/build_and_run.ps1 -Abi x86_64 -Serial emulator-5554 -ProjectDir D:\path\to\Project -LogcatSeconds 20
pwsh -File runtime/android/build_and_run.ps1 -Serial emulator-5554 -SkipRustBuild -SkipGradle -NoInstall -ProjectDir D:\path\to\Project -PushScripts
```

### 5.2 手で 1 段ずつ行う場合

```powershell
cd runtime/android/native
cargo ndk -t arm64-v8a -t x86_64 -P 29 -o ../app/src/main/jniLibs build      # 1. libSEED.so
cd ..
# 1b.（パッケージ実行にするときだけ）APK に入れる pak を作る。開発用の APK にするなら app/src/main/assets/seed を消す
dotnet run --project ../../editor/tools/SeedPak -- --project D:\path\to\Project --out app/src/main/assets/seed
.\gradlew.bat assembleDebug "-Pseed.ndkPath=$env:ANDROID_NDK_HOME"            # 2. APK
adb -s emulator-5554 install -r app/build/outputs/apk/debug/app-debug.apk     # 3. インストール
# 4. アセット（run-as でアプリの権限になり、tar を内部フォルダへ展開する。pwsh 7.4 以降）
& "$env:SystemRoot\System32\tar.exe" -cf - -C D:\path\to\Project\assets . |
    adb -s emulator-5554 exec-in run-as com.seedengine.runtime sh -c "rm -rf files/assets && mkdir -p files/assets && tar -xf - -C files/assets"
adb -s emulator-5554 shell am start -n com.seedengine.runtime/.MainActivity   # 5. 起動
adb -s emulator-5554 logcat -s SEED RustPanic                                  # 6. ログ
```

`-P 29` は `minSdk` と同じ API レベルでリンクするため（cargo-ndk の既定は 21）。
`adb exec-in` / `exec-out` は 2 つ目以降の引数を 1 つずつ引用して端末へ渡す（`adb shell` は引用せずに連結する）ため、
`sh -c` のスクリプトはそのまま 1 引数で渡す。

### 5.3 所要時間と大きさ（段階0 の実測・debug）

| 項目 | 実測 |
|---|---|
| 初回クロスビルド（x86_64、依存込み） | 約 6 分 16 秒 |
| arm64 追加ビルド（ホスト側の成果物は共有） | 約 4 分 10 秒 |
| エンジンだけ変えたときの再ビルド | 約 20 秒 |
| `gradlew assembleDebug`（初回・依存取得込み） | 約 2〜3.5 分 |
| `gradlew assembleDebug`（1 ABI・.so だけ変わったとき） | 約 1 分 50 秒（大半は .so のシンボル削り） |
| `libSEED.so`（未ストリップ・フルデバッグ情報） | 約 445 MB / ABI |
| APK 内の `libSEED.so`（AGP がシンボルを削ったもの） | 約 43〜53 MB / ABI（arm64 が小さい） |
| APK（2 ABI／arm64 のみ／x86_64 のみ） | 約 108 MB／50.4 MB／57.8 MB |
| `adb install -r`（実機 Pixel 6a・arm64 のみ 50.4 MB） | 約 4.0 秒 |
| アセット転送（run-as ＋ tar・最小アセット 3.2 MB） | 1 秒未満 |
| SeedPak（最小アセット 3 ファイル・3.0 MB）| 書き出し 0.1〜0.2 秒（`dotnet run` の起動・ビルド確認込みで約 2 秒） |
| `-ProjectDir` の SeedPak → Gradle → install（x86_64・.so は既存） | 約 28 秒 |

段階C-1（SeedAndroid。2026-09-25・エミュレータ x86_64・最小構成＋確認用スクリプトのプロジェクト）:

| 項目 | 実測 |
|---|---|
| 準備（道具・プロジェクト・端末・各工程の指紋。runtime/src 674 ファイルほか） | 0.1〜0.4 秒 |
| `run` 1 回目（記録なし。.so は cargo の増分ビルド） | 75.0 秒（.so 9.2・SeedPak 8.4・同梱 .NET 1.3・Gradle 11.7・install 9.3・起動 9.7（端末の .NET の展開込み）・logcat 25） |
| `run` 2 回目（何も変えない） | 15.2 秒（5 工程を飛ばし、起動 2.2＋logcat 12） |
| `.cs` を変えた `run`（build_and_run.ps1 経由） | 26.8 秒（SeedPak 4.5・Gradle 2.3・install 2.6・起動 1.9・logcat 12。.so と同梱 .NET は飛ばす） |
| `push`（スクリプトの DLL だけ） | 8.3 秒（SeedPak `--scripts-only`＋転送 6.5・起動 1.8。logcat を除く） |
| arm64 の `build`（別の ABI へ切り替え。.so は増分ビルド） | 47.1 秒（.so 19.2・SeedPak 6.7・同梱 .NET 1.2・Gradle 19.9） |

---

## 6. logcat の見方

エンジン・糊・MainActivity の出力はすべて **タグ `SEED`** に集まる。C# スクリプトの `SEED.Debug.Log`（`Console` の出力）は、
CoreCLR では **タグ `DOTNET`**（.NET の Android 版 `Console` が logcat へ直接書く）、Mono では標準出力を経由してタグ `SEED` に出る（§17.10）。

```powershell
adb logcat -s SEED DOTNET RustPanic     # 普段はこれで十分（DOTNET = C# スクリプトのログ）
adb logcat -v threadtime SEED:V RustPanic:V GameActivity:V AndroidRuntime:E DEBUG:V libc:F *:S   # 起動失敗・クラッシュ時
adb logcat -d -v threadtime -T "09-24 17:00:00.000" SEED:V *:S   # その時刻以降だけ（共用端末で logcat -c しない）
```

実機は他の作業者・エージェントと共用することがあるため、**`adb logcat -c`（全消去）は使わない**。
SeedAndroid（と build_and_run.ps1）も起動直前の端末の時刻を控えて `logcat -T` で今回分だけを取り出す。

| 行の印 | 出どころ | 読み方 |
|---|---|---|
| `[SEED INIT] ...` | エンジンの起動ログ（eprintln! → 標準エラー転送） | Windows 版の起動ログと同じ内容。アダプタ名・バックエンド（`backend=Vulkan`）もここ |
| `APK 内の pak で起動します（パッケージ実行）: apk:seed/assets.pak  3.0 MiB・非圧縮（APK 内の位置 N）` | 起動モードの判定（launch.rs） | 「非圧縮」が出ないときは noCompress の設定漏れ（警告も出る）。pak が無ければ「APK に … がありません。開発用の置き場…から読みます」 |
| `[SEED INIT] asset_fs: packaged pak=apk:seed/assets.pak entries=N` | PAK を開けた（app_init.rs。Windows の配布物ではファイルパスが出る） | 開けなければ `[App][ERROR] assets.pak を開けません: …` |
| `[SEED SURFACE] created / released / recreated` | サーフェスの生成・破棄・再生成 | 大きさ・形式・提示モード |
| `[SEED LIFECYCLE] suspended / resumed / Resized / Focused ...` | ライフサイクル診断 | 回転・バックグラウンド復帰の確認 |
| `[SEED TOUCH] Started / Ended ...` / `[SEED KEY] ...` | winit から届いた生のタッチ・キー | Moved は件数だけ Ended 行にまとめる |
| `[SEED TOUCH FRAME] f=.. n=.. #0:Began(x,y)d(dx,dy) ... \| mouse=(x,y) L=PD-` | 入力状態（`Input.TouchCount` / `GetTouch` とタッチ由来のマウス）のフレーム末の値（`app/touch_diag.rs`） | 変化のあったフレームだけ 1 行。`L` は左ボタンの押下中 P / 押した瞬間 D / 離した瞬間 U（§12.3） |
| `[SEED TOUCH TEST] Started id=0x5eed000N ...` | 検証用の合成タッチ列（`debug.seed.touch_test=1` のときだけ。§12.3） | 合成したイベントそのもの。反映結果は `[SEED TOUCH FRAME]` で見る |
| `[SEED HEARTBEAT] presented_frames total=N +d in 3.0s (x fps)` | 生存確認（3 秒ごと） | バックグラウンド中は `+0`、復帰で再び増える |
| `書き込み先: データ（セーブ）=… / キャッシュ=…`・`環境変数 TMPDIR=…` | 書き込み先の設定（app_dirs.rs） | §14.1 |
| `[SEED SAVE] save file: …` / `suspended: …` / `onDestroy（プロセス終了前）: …` | セーブの置き場と自動書き出しの結果 | 「未書き出しの変更を書き出しました」「未書き出しの変更なし」「セーブ未使用」（§14.2） |
| `[SEED PIPELINE CACHE] 読込 N KiB → 採用後 M KiB` / `保存 …` / `変化なし …` | パイプラインキャッシュ（§14.3） | 起動時の生成時間は `[SEED INIT] DrawContext created (N ms)`・`描画パイプライン生成 合計 N ms` |
| `[SEED LIFECYCLE] background / foreground: …` / `[SEED PHYSICS] 3D 物理スレッド: …` | 背面での停止・前面での再開（§14.4） | 物理スレッドは止めた・再開したを 1 回ずつ出す |
| `[SEED AUDIO] 出力ストリームを開きました（2ch・44100 Hz・F32）` | 音声の出力を初めて使ったとき（`core/audio/output/`。§16.1） | 開けた設定。開けなければ `出力ストリームを開けません: …` |
| `[SEED AUDIO] Java: 音声フォーカス: … → 状態 N` / `音声フォーカスの報告を受け取りました: …` | 音声フォーカスの要求・放棄の結果と OS からの変化（`AudioFocusController.java`・`jni_exports.rs`。§16.3） | Java の行は Android の値（`AUDIOFOCUS_LOSS_TRANSIENT` 等）、ネイティブの行は変わったときだけ |
| `[SEED AUDIO] 音声を一時停止しました（背面=… ・音声フォーカス=…）` / `音声を再開しました（全体音量 ×1.00・…）` / `全体音量を ×0.20 にしました（…）` | 出力全体の状態が変わった（`app/audio_output_sync.rs`。§16.2・§16.3） | まだ何も鳴らしていない（出力を開いていない）間は出ない |
| `[SEED KEY FRAME] f=N Escape:down+up` | 置き換えたキー（戻るキー → Escape）の入力状態（`app/key_diag.rs`。§14.5） | スクリプトの `GetKeyDown` / `GetKeyUp` が読むフレーム末の値 |
| `[SEED SCREEN] Java 報告: …` / `報告を受け取りました: …` | 安全領域・回転の報告（`ScreenReporter.java`・`jni_exports.rs`。§15.4） | 描画面の大きさ・各辺からの距離・回転・表示の自然な向きの大きさ。Java の行には WindowInsets の種類ごとの内訳も出る |
| `[SEED SCREEN] size=… safe=(x,y,幅,高さ) orientation=… dpi=… window=… report=…` | スクリプトの `SEED.Screen` が返す値（`app/screen_diag.rs`。§15.4） | 変化したフレームだけ。`report=none` は今の描画面に一致する報告が無いフレーム（回転の直後） |
| `[SEED SAVE TEST] …` | 検証用のセーブ書き換え（`debug.seed.save_test` が 1 か 2 のときだけ。§14.6） | 起動時に読んだ値と書き換えた値 |
| `[SEED DOTNET] ...` | 同梱 .NET の展開・スクリプトの DLL の置き場・CLR の起動（`dotnet_runtime/`・`clr_host/embedded.rs`。§17.10） | 展開・起動の所要時間と、どの置き場の DLL を使ったか |
| タグ `DOTNET` の `[Script] ...` / `[SEEDScripting] ...` | C# スクリプトの `SEED.Debug.Log` とスクリプトホスト（CoreCLR の `Console`） | Mono では同じ行がタグ `SEED` に出る |
| `[SEED PANIC] ...` / タグ `RustPanic` | panic フック（liblog へ同期で直接）／android-activity | 場所（ファイル:行）と backtrace |
| `wgpu_hal::... / wgpu_core::...: ...` | 依存クレートの log（android_logger） | `naga` の Info は多すぎるため Warn 以上だけ出す |

- 標準出力と標準エラーは同じ pipe で転送するので、どちらも INFO で出る（エラーは行頭の `[ERROR]` 等で判別）。
  転送スレッド経由なので、**スレッド ID は転送スレッドのもの**になる。
- `[PERF f=...]` `[PLAY_HB]` `[PLAY_DIAG n]` `[SEED FRAME n]` はエンジン既存の診断出力で、Windows 版でも同じように出ている。
- 意図的な panic で経路を確認する: `adb shell setprop debug.seed.panic_test 1` → 起動（`[SEED PANIC]` が出て
  Activity が終わり、プロセスが終了する）→ `adb shell setprop debug.seed.panic_test 0` で戻す。
- 複数指の合成タッチ列で確認する: `adb shell setprop debug.seed.touch_test 1` → 起動（最初のフレームの 2 秒後に
  3 本指の列が 1 回流れる。§12.3）→ `adb shell setprop debug.seed.touch_test 0` で戻す。
- セーブの書き出しタイミングを確認する: `adb shell setprop debug.seed.save_test 1`（または 2）→ 手順は §14.6 →
  `adb shell setprop debug.seed.save_test 0` で戻す。
- `[PLAY_WD] stuck at stage=frame_end … (A)イベントループスレッド自体がブロック` はバックグラウンド中に 5 秒ごとに出るが、
  イベントループが Wait で眠っているだけの誤報（既存の一時診断。backlog）。
- APK に入る .so はシンボルが削られているため backtrace の多くは `<unknown>`。
  未ストリップの `app/src/main/jniLibs/<ABI>/libSEED.so` と NDK の `llvm-addr2line` で後から解決できる。

---

## 7. 段階0 で確認できたこと（2026-09-24）

エミュレータと実機の両方で確認した。使ったアセットは最小構成（BrainStem.glb ＋ 平行光 1 灯。
音声だけは無音・音量 0 の AudioComponent を足した版）。実機は arm64、エミュレータは x86_64 の debug ビルド。
実機で見つかった 2 点（間接描画の feature・アセットの置き場）を直した後、エミュレータでも再確認した
（ホスト GPU では `MULTI_DRAW_INDIRECT=true` のまま従来どおり要求され、内部フォルダのアセットで描画された）。

| 確認項目 | エミュレータ（AVD `seed_pixel6_api35`・API 35・x86_64・GPU host） | 実機 Pixel 6a（Android 16 / API 36・arm64・Mali-G78 ドライバ r54p1） |
|---|---|---|
| 起動 → サーフェス作成 | `created 1080x2400 format=Rgba8UnormSrgb present_mode=Fifo` | 同じ（`alpha_mode=Inherit`） |
| wgpu アダプタ | `backend=Vulkan`（gfxstream 経由でホストの NVIDIA GPU。`ro.hardware.vulkan=ranchu`） | `name=Mali-G78 type=IntegratedGpu backend=Vulkan` |
| `request_device` | 通過 | **初回は `UnsupportedFeature(MULTI_DRAW_INDIRECT)` で失敗**。間接描画の feature を「対応していれば要求」に直して通過（§10） |
| GPU 機能の差（起動ログ） | バインドレス非対応・ワイヤーフレーム対応・BC 圧縮対応 | バインドレス対応（配列 4096）・ワイヤーフレーム非対応・BC 圧縮非対応・メッシュレットカリング非対応（CPU カリング経路） |
| 毎フレーム描画（`[SEED HEARTBEAT]`） | 約 59 fps（非フォーカス時はエンジン既定の 30 fps 上限） | 縦 約 18〜19 fps／横 約 37〜39 fps。GPU 待ちが支配的と見られる（`[PERF]` で 1 フレーム約 50 ms のうち提示待ち `finish` が 28〜41 ms、CPU 側の処理は数 ms） |
| 1 枚絵 | 空アセットではクリア色、最小アセットでモデルが陰影付きで描画 | 同じ絵が描画された |
| 回転 4 方向 | `adb emu rotate`。`Resized 2400x1080` / `1080x2400` で描画継続（段階A-4 で安全領域・向きの値とカメラ・キャンバス・タッチの追従も確認。§15.5） | `cmd window fixed-to-user-rotation enabled` ＋ `cmd window user-rotation lock 0〜3` で同じ結果（検証後は元の設定に戻した） |
| ホーム → 復帰（`KEYCODE_HOME` → `am start`） | `suspended` → `released` → heartbeat `+0` → `recreated 1080x2400` → 描画再開 | 同じ（復帰の `am start` は HOT で 28 ms） |
| タッチ（`input tap` / `input swipe`） | `[SEED TOUCH] Started ... / Ended ... moves=21` | 同じ。adb の操作とは別に、画面を指でなぞった操作も届いた |
| 戻るキー | `[SEED KEY] pressed logical=Named(BrowserBack)`。Activity は終わらない（段階A-3 で Escape に置き換え。§14.5） | 同じ |
| panic（`debug.seed.panic_test=1`） | `[SEED PANIC] ... 場所` と backtrace → Activity 終了 → `onDestroy` でプロセス終了 | 同じ |
| 音声（oboe） | 段階A-5 で確認: 2ch・44100 Hz・F32 の AAudio ストリームで再生（`dumpsys audio` の player が `state:started`、ホスト PC 側でエミュレータの音声セッションに音の波形が出る）。背面・音声フォーカスの喪失で一時停止（§16.6） | 段階0 は初期化まで（`OboeAudio: OboeVersion1.8.1`、`AAudioStreamBuilder_openStream() returns 0 = AAUDIO_OK`）。段階A-5 で再生・背面での一時停止・再開を確認（§16.6） |
| アセットの置き場 | 外部アプリ専用フォルダへの adb push でも、run-as で内部フォルダへ送っても読めた | **adb push は Permission denied**（shell 所有のフォルダになる）。run-as で内部フォルダへ送る方式で読めた（§4.5） |
| 起動時間（`handle_resumed` 開始 → 最初のフレームの終わり） | 約 3.5 秒（うちパイプライン生成 約 1.6 秒） | 約 4.5 秒（うちパイプライン生成 約 3.4 秒・シーン読込 0.24 秒）。`am start` の TotalTime は 487 ms（Activity 表示まで） |
| インストール（`adb install -r`） | 約 1.5 秒（APK 60.5 MB・x86_64 のみ） | 約 4.0 秒（APK 50.4 MB・arm64 のみ） |
| SELinux | — | `avc: denied { search }` が cgroup / cgroup2 のルートに対して 4 件（`android_main` スレッド・最初のフレーム時。CPU 数の問い合わせで cgroup を見に行ったものと推測）。動作に影響なし |
| ドライバ・wgpu の警告 | `Missing downlevel flags: SURFACE_VIEW_FORMATS` | `Unrecognized present mode SHARED_DEMAND_REFRESH / SHARED_CONTINUOUS_REFRESH`（wgpu が知らない Android 固有の提示モード。無害）。Mali ドライバの警告・検証エラーは無し |
| 16KB ページ | — | 端末は 4KB ページ（`getconf PAGE_SIZE` = 4096）。.so は 16KB 整列なのでどちらでも読める。16KB 関連のログは無し |
| キャッシュ・保存先 | 2 回目の起動でモデルキャッシュがヒット | 内部フォルダの `cache/` に書けた（初回ロード 160 ms・`bc=false`）。置き場は段階A-3 で変更（§14.1） |
| Windows | `cargo check` / `cargo build` が通り、`SEED.exe` が従来どおり生成（GPU 選択用エクスポートも維持） | 実機向けの修正後も同じ |

---

## 8. 現状の制限（段階0）

詳細と持ち越し先は [backlog.md](backlog.md) の「Android」節。

- ~~スクリプト（C#）は動かない~~ → 段階B で APK に同梱した .NET 10 の CoreCLR で動く（§17）。
- アセットは APK 内の pak（パッケージ実行。リリース版でも動く）か、デバッグ版 APK の run-as で内部アプリ専用フォルダへ
  送ったもの（開発用）を読む（§13）。
- セーブ・キャッシュは起動モードに関係なくアプリ専用フォルダ（`files/save/`・`cache/`）に書く（段階A-3 で振り替え。§14.1）。
- タッチは入力システムへつながった（§12）。スクリプトの `Input.GetTouch` 等は段階B（§17）で実機でも動くことを確かめた。
  キャンバス UI のボタン（ポインタイベントの配信先がスクリプト）が実機で反応するところは未確認。
- **Activity の破棄＝プロセス終了**。winit 0.30 は onDestroy をアプリへ通知しない（イベントループが終わらず
  GameActivity の onDestroy が android_main の終了を待ち続けて ANR になる）うえ、EventLoop はプロセスで 1 度しか
  作れないため、`MainActivity.onDestroy` でプロセスを終了させている。構成変更での作り直しは `configChanges` で防いでいる。
  セーブはバックグラウンドへ回る時点（suspended）と、終了直前の JNI 呼び出しで書き出す（段階A-3。§14.2）。
- 戻るキーは `KeyCode.Escape` としてスクリプトへ届く。アプリは自動で終了しない（段階A-3。§14.5）。
- 画面の向きはプロジェクト設定の `screen_orientation`（both / portrait / landscape）で APK を作るときに決まる。端末の回転ロックは尊重しない。
  安全領域・向き・DPI は `SEED.Screen` で読めるが、キャンバス UI へ安全領域を自動では反映しない（段階A-4。§15）。
- バックグラウンド中は物理スレッドを止め（段階A-3。§14.4）、音声は出力ストリームごと一時停止する（段階A-5。§16）。
  ゲームパッド（gilrs）のスレッドは止めていない。
- パイプラインキャッシュはアプリのキャッシュフォルダへ保存し、2 回目以降の起動で読む（段階A-3。§14.3）。
  実機（Pixel 6a）ではパイプライン生成が 3595 / 3014 ms → 528 / 522 ms に縮んだ（約 85% 減。§14.7）。
- ゲームパッド（gilrs）は Android 非対応（初期化に失敗して「パッド無効」で続行）。
- 音声（rodio → cpal → oboe）はエミュレータと実機で再生を確認した。背面へ回る・音声フォーカスを失うと出力ストリームごと一時停止し、
  戻ると再開する（段階A-5。§16）。出力デバイスの切り替え（ヘッドホン・Bluetooth）からの復帰は未対応・未確認（§16.7）。
- **実機の描画は重い**。Pixel 6a の debug ビルドで縦 約 18〜19 fps（GPU 待ちが支配的と見られる）。デスクトップ向けの描画経路
  （deferred・MRT 5 枚・シャドウ 2048・SSGI 等）を端末の実解像度 1080x2400 でそのまま回しているため。
  モバイル向け描画プリセット（描画解像度スケール・重い後処理の既定オフ）は段階D。

---

## 9. 段階ロードマップ

| 段階 | 内容 |
|---|---|
| **0（完了）** | 実機/エミュレータに 1 枚絵。libSEED.so ＋ Gradle ＋ GameActivity、logcat、サーフェスの破棄・再生成、回転追従 |
| **A** | スクリプト無しでシーンを動かす: APK 内 pak（AssetManager。**2026-09-24 実装・§13**）、保存先の振替・セーブの保護・パイプラインキャッシュ・背面での物理停止・戻るキー（**2026-09-24 実装・§14**）、縦横とサーフェス再生成の仕上げ、複数指タッチ（`Input.TouchCount` / `GetTouch(i)`。PC はマウス＝指 0。**2026-09-24 実装・§12**）、安全領域・画面の向き API（プロジェクト設定の向き・`SEED.Screen`。**2026-09-24 実装・§15**）、音声（鳴ることの確認・背面での停止・音声フォーカス・音量キー。**2026-09-24 実装・§16**）、logcat の整備 |
| **B** | スクリプト: **PC も Android も .NET 10 の CoreCLR に揃える**（PC は全 C# プロジェクトを `net10.0` へ移行済み。Android は `android-*` ランタイムパック＋同じ版の bionic パックの hostfxr / hostpolicy。§11）。ScriptPackager の事前コンパイル DLL とランタイムを同梱し、既存の hostfxr 経路を `Hostfxr::load_from_path` で使う（**2026-09-25 実装・§17**。Mono へ切り替え可）。出荷時は NativeAOT を後で検討 |
| **C** | エディタ「実行」統合: **C-1（2026-09-25 実装・§4.6・§5・§18）** ビルド・配置・起動の手順を C# の中核（`editor/src/Android/`）とコンソールツール `SeedAndroid` に移し、変わっていない工程の自動の省略・アプリの識別情報のプロジェクト設定化。**C-2** 実行先セレクタ（PC／実機／エミュレータ）、中核を呼んでビルド → install → 起動 → logcat → 停止を Output パネルへ、pak/DLL だけ push する高速経路。パッケージ化ウィンドウの Android 出力の実働化 |
| **D** | Wi-Fi 実行、実行中の差し替え、モバイル向け描画プリセット、署名／AAB／16KB ページの最終確認、NativeAOT |

---

## 10. 技術メモ（ハマりどころ）

- **netcorehost の既定機能 `nethost-download`** は `nethost-sys` の build.rs が Android で
  `platform not supported` と panic する。段階0〜A は Android では依存自体を外していた。段階B からは Android だけ
  `default-features = false, features = ["net10_0"]` で依存する（`Hostfxr::load_from_path` を使うので nethost は要らない）。
- **同梱 .NET（段階B）のハマりどころ**（詳細は §17）:
  - AGP の既定（`useLegacyPackaging = false`）では .so は APK から直接読み込まれ、端末のファイルとして存在しない
    （`dladdr` のパスが `…/base.apk!/lib/<ABI>/libSEED.so`）。hostfxr / hostpolicy / CoreCLR は dotnet-root 形式のフォルダの
    実ファイル（かシンボリックリンク）を前提にするので `useLegacyPackaging = true` にした。
  - bionic の `dladdr` はシンボリックリンクを辿った実パスを返す。hostfxr が自分の場所から推す dotnet-root は当てにならないので、
    `initialize_for_runtime_config_with_dotnet_root` で明示する。それでも `System.Private.CoreLib.dll` は dotnet-root の
    framework フォルダから読まれた（シンボリックリンクの置き方で動く。§17.4）。
  - アプリのデータフォルダ（`app_data_file`）のファイルを実行可能として読み込むと、SELinux の `avc: granted { execute }` が
    ファイルごとに 1 行ずつ出る（`auditallow`＝許可しつつ記録。R2R の DLL も対象）。将来の Android で禁止される恐れがある（backlog）。
  - Mono は、型の静的フィールドの型（ラムダを貯める隠れクラス `<>c`・`<>O` の `Func<Roslyn の型, …>` 等）をクラスの読み込み時に
    解決する。事前コンパイル DLL を読むだけの経路で Roslyn の型が見えると、Roslyn の無い端末で `TypeLoadException` になった
    （`ScriptAssemblyManager` から Roslyn を使うコードを `ScriptAssemblyEmitter` へ分けて解消。§17.9）。
  - CoreCLR の暗号ライブラリの `JNI_OnLoad` は、パックの `.jar` のクラス（`net.dot.android.crypto.*`）を `FindClass` で探し、
    無ければ `abort()` する。ネイティブのスレッドから呼ぶとシステムのクラスローダーで探すので必ず失敗する。Java の
    `System.loadLibrary` で読み込む（§17.8）。
  - debuggerd（tombstone のバックトレース）は `lib/` の下の .so しか読めない（`files/` に複製した .so は関数名が出ない）。
  - PowerShell の `$x = if (…) { @(1 要素) }` は配列が中身へ展開される（StrictMode で `.Count` が無いエラー）。配列は `if` の外で作る。
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
  `adb emu rotate` は 1 回ごとに `ROTATION_0 → 270 → 180 → 90 → 0` の順に回る（4 回で元に戻る）。
- **WindowInsets（安全領域）はネイティブへ届かない**: android-activity 0.6.1 は GameActivity の `InsetsChanged` を中身の無いイベントとして
  出すだけ（`content_rect()` はある）で、winit 0.30 は `TODO: handle Android InsetsChanged` と warn して捨てる。そのため Java（`ScreenReporter`）で
  集めて JNI で渡している。GameActivity 自身が SurfaceView の `OnApplyWindowInsetsListener` と `OnGlobalLayoutListener` なので、
  MainActivity で `onApplyWindowInsets` / `onGlobalLayout` を上書きする（super を必ず呼ぶ。IME の処理と content rect の通知があるため）。
- 没入モード（システムバーを隠す）では `getInsets(systemBars())` は 0 になる。隠れているバーの範囲は `getInsetsIgnoringVisibility` で取る（§15.3）。
- エミュレータの切り欠きの overlay（`cutout.emulation.*`）を切り替えると、動いている SEED は Activity の作り直し → プロセス終了になる。
  切り替えた後はシステムバーの高さが古いまま残ることがあり、`wm density <別の値>` → `wm density reset` で読み直される（§15.4）。
- bash（Git Bash）から adb に `/sdcard/...` を渡すとパス変換で壊れる。`MSYS_NO_PATHCONV=1` を付けるか pwsh を使う。
- 同じ NDK でもパスの表記（`/` と `\`）が違うと cc 系の依存（oboe-sys 等）が再ビルドされる。スクリプト経由に揃えるとよい。
- **Mali-G78（Pixel 6a）は Vulkan の `multiDrawIndirect` を持たない**。wgpu の `MULTI_DRAW_INDIRECT` を無条件に要求すると
  `request_device` が `UnsupportedFeature` で失敗する。エンジンで間接描画を使うのはメッシュレットカリングの
  `multi_draw_indexed_indirect_count`（wgpu 25 では `MULTI_DRAW_INDIRECT_COUNT` だけを要求）だけなので、
  `MULTI_DRAW_INDIRECT` / `INDIRECT_FIRST_INSTANCE` / `MULTI_DRAW_INDIRECT_COUNT` はどれも「対応していれば要求」にし、
  3 つが揃うときだけメッシュレットカリングを使う（`renderer/mod.rs`）。デスクトップの要求内容は変わらない。
  なお wgpu の `check_limits` は超過した limit を最後の 1 つしか返さないので、limit で落ちたときは 1 つずつ潰すことになる。
- **実機では `adb push` で外部アプリ専用フォルダ（`/sdcard/Android/data/<pkg>/files`）に作ったフォルダは shell の所有
  （`drwxrws--- shell ext_data_rw`）になり、アプリからは Permission denied で読めない**（エミュレータでは読めた）。
  `run-as` で入っても外部フォルダは Permission denied で読めなかった（原因は未特定）。デバッグ版 APK の `run-as` で内部フォルダへ
  `tar` を流し込むのが確実（§5.2）。
- `adb exec-in` / `exec-out` は 2 つ目以降の引数を 1 つずつ単一引用符で囲んで端末へ渡すが、`adb shell` は引数をそのまま
  連結して端末のシェルに解釈させる。`sh -c` のスクリプトを渡すときは、前者は引用せずに 1 引数、後者は内側で引用する。
  `adb logcat` は各引数を引用して渡すので、空白を含む時刻（`-T "09-24 17:00:00.000"`）もそのまま渡せる。
- **共用端末では `adb logcat -c` をしない**（他の人のログも消える）。起動直前の端末時刻を控えて `logcat -T` で取り出す。
  実機はログが非常に多く、リングバッファからすぐ押し出される（数分で消える）ので、事象の直後に取り出して保存する。
- 実機で回転を強制するには `cmd window fixed-to-user-rotation enabled` ＋ `cmd window user-rotation lock <0〜3>`。
  検証前に `cmd window fixed-to-user-rotation` と `cmd window user-rotation`（引数なしで現在値を表示）を控え、
  終わったら控えた値へ戻す（戻さないと端末の回転ロックが変わったままになる）。
- Activity が `singleTask` なので、動いている最中に `am start` しても前面へ出るだけで作り直されない。送り直したアセットや
  入れ直した .so を読ませるには `am force-stop` してから起動する（SeedAndroid の起動の工程は毎回そうしている）。
- **アプリ ID を変えた APK では `am start -n <ID>/.MainActivity` の省略形が使えない**（`.MainActivity` をアプリ ID の下のクラスと解釈する）。
  Activity のクラスの名前空間は `com.seedengine.runtime` のままなので、`-n <ID>/com.seedengine.runtime.MainActivity` と完全修飾で渡す（§18）。
- **gradlew.bat（バッチファイル）へ `&` 等を含む値を引数で渡すと cmd.exe が解釈する**（引数が割れる・別のコマンドとして動く）。
  プロジェクトのデータ（アプリ名等）は環境変数 `ORG_GRADLE_PROJECT_<名前>` で渡す（`providers.gradleProperty` で `-P` と同じに読める。§5.1）。
- 実機の縦画面より横画面のほうが fps が高かった（18〜19 fps 対 37〜39 fps。描画する画素数は同じ）。原因は未調査（段階D）。
- **wgpu のパイプラインキャッシュに別アダプタのデータを渡すと、`fallback: true` でも検証エラーになる**
  （wgpu-core の `PipelineCacheValidationError::DeviceMismatch` は「避けられた誤り」扱い）。エラーハンドラが無いと
  その場で panic する。ファイル名をアダプタごと（`wgpu::util::pipeline_cache_key`）に分け、読み込みはエラースコープで
  囲んで、弾かれたら空のキャッシュで作り直している（§14.3）。版違い・破損は fallback で黙って空になる。
- エミュレータ（gfxstream）の Vulkan アダプタはホストの GPU のベンダー ID・デバイス ID をそのまま名乗る
  （この PC ではデスクトップの SEED.exe と同じ `wgpu_pipeline_cache_vulkan_4318_9504`）。ホスト側のドライバが自前の
  シェーダキャッシュを持つので、エミュレータではパイプラインキャッシュの効果がほとんど見えない。効果の計測は実機で行う。
- **バックグラウンドの Activity を破棄させる（onDestroy を起こす）**には `adb shell am stack list` で SEED の
  RootTask id を調べて `adb shell am stack remove <id>`（最近のタスクから消したのと同じ）。`am kill` / `am force-stop` は
  コールバック無しでプロセスを殺すので onDestroy も suspended も来ない。
- 環境変数の書き換え（`std::env::set_var`）は Rust 2024 で unsafe（他スレッドの getenv と競合する）。
  ネイティブのスレッドが無い `MainActivity.onCreate` の super.onCreate より前に Java の `Os.setenv` で行う。
- winit の `suspended` は android-activity の `TerminateWindow` から同期で呼ばれ、glue は処理が終わるまで UI スレッドを
  待たせる（`android_app_set_window`）。ここで書いたセーブは、直後にプロセスが殺されても残る（ただし長く掛けると ANR）。
- Git Bash から `adb shell date +'%m-%d …'` のように空白を含む引数を渡すと端末側で分割される（`adb shell` は連結して
  端末のシェルへ渡す）。`adb shell "date +'%m-%d %H:%M:%S.000'"` と 1 引数にする。

## 11. .NET ランタイムのスパイク結果（2026-09-24、段階B の前提）

目的: Android 上で Rust から hostfxr 経由で .NET を起動し、`UnmanagedCallersOnly` の C# 関数を呼べるかの検証。
検証コードは `runtime/android/spikes/dotnet_host/`（README 参照）。検証環境はエミュレータ x86_64 と、その ARM 変換上の arm64。
**実機とアプリプロセス内は未検証**（adb のシェル権限で `/data/local/tmp` から実行した）。実機は §11.4、アプリプロセス内（段階B の本実装）は §17。

**段階B の方針（2026-09-24 決定）: PC も Android も .NET 10 の CoreCLR に揃える**（Mono は採らない。PC 側は全 C# プロジェクトを `net10.0` / `net10.0-windows` へ移行済みで、以下のスパイクの net9.0 / `rollForward` の記述は当時の構成）。

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

### 11.4 実機（Pixel 6a、Android 16、arm64）での結果（2026-09-24 追記）

- **既定のままでは両ランタイムとも `coreclr_initialize` で SIGSEGV**（SEGV_MAPERR、フォルトアドレス `0xb4000074…`）。
  原因は arm64 の Android 11 以降で bionic がヒープポインタの上位バイトに付けるタグ（TBI、値 0xB4）。
  x86_64 エミュレータと、その ARM 変換では再現しない。**.NET を載せた APK の確認は実機が必須。**
- 対策はどちらか 1 つ（段階B の必須項目）:
  - `AndroidManifest.xml` の `<application>` に `android:allowNativeHeapPointerTagging="false"`（仕様からの推定。APK では未検証）
  - hostfxr を読み込む前に `mallopt(M_BIONIC_SET_HEAP_TAGGING_LEVEL, M_HEAP_TAGGING_LEVEL_NONE)` を 1 回呼ぶ
    （実行ファイルでは `spikes/dotnet_host/device/notag_preload.c` の LD_PRELOAD シムで効果を確認済み。アプリプロセスでは未検証）
  - 環境変数では無効化できない（bionic の `libc_init_mte.cpp` で確認）。MTE 有効端末（Pixel 8 以降）は別論点で未検証。
- タグ付けを無効にすると、機能面はエミュレータと同じ結果になった（共通経路 OK、SEED 現行経路は CoreCLR で PNSE、
  暗号 API は両方ともプロセス即死、Mono の collectible ALC は Unload されない）。
- 所要時間（実機、各 3 回の中央値）:

| 項目 | Mono 9.0 | CoreCLR 10.0 |
|---|---|---|
| ランタイム起動 | 約 83 ms | 約 18 ms |
| 初回の関数取得 | 約 31 ms | 約 8 ms |
| 機能チェック一式（初回 / 2 回目） | 516 ms / 83 ms | 193 ms / 57 ms |
| プロセス全体 | 約 0.8 s | 約 0.34 s |

- JIT 量: Mono 約 4,100 メソッド（600〜775 ms）、CoreCLR 約 350 メソッド（73〜132 ms）。
- Invariant 無効時は system ICU 76（`/apex/com.android.i18n`）が読み込まれ、TZ 検索は 0.6〜0.7 秒
  （エミュレータで見えた 8〜12 秒は実機では起きない）。
- 実機用ランナーは `spikes/dotnet_host/device/device_run.sh`（私物端末の他アプリのログを持ち出さないよう、logcat は自プロセス分に絞って取得する）。

---

## 12. タッチ入力（段階A・2026-09-24）

複数指のタッチを入力システム（`Input`）へつなぎ、C# から Unity 風の API（`Input.TouchCount` / `GetTouch(i)` / `Touches` /
`TouchSupported`）で読めるようにした。利用者向けの API 説明は [scripting_api.md](scripting_api.md) §6.5「タッチ（複数指）」。

### 12.1 構成と流れ

```
winit WindowEvent::Touch { id, phase, location }    Android: MotionEvent の各ポインタ（Windows: WM_TOUCH / WM_POINTER）
  └ App::on_touch（app/event_handler.rs）
      └ Input::process_touch（input/mod.rs）         カーソルと同じ写像（内部解像度固定のレターボックス）で入力座標へ
          └ PointerBridge::on_touch（input/touch/bridge.rs）   入力源の調停・マウス ⇔ タッチの相互変換
              ├ TouchState::apply（input/touch/state.rs）       指の一覧（フレーム単位の状態機械）
              └ 指0 → MouseState（touch_drives_mouse の端末だけ）  カーソル座標・左ボタン
実マウス（CursorMoved / MouseInput）も Input → PointerBridge を通る（mouse_simulates_touch の端末では左ボタンで指を合成）
フレーム末: Input::end_frame … MouseState::end_frame → PointerBridge::end_frame（TouchState::end_frame → 次フレーム分をマウスへ）
スクリプト: SEED.Input.* → ffi_input_touch（scripting/host_api.rs。並びは scripting/input_bridge.rs の TOUCH_*）
```

| ファイル | 役割 |
|---|---|
| `runtime/src/engine/core/input/touch/phase.rs` | `TouchPhase`（Began / Moved / Stationary / Ended / Canceled。数値は C# と同じ） |
| `runtime/src/engine/core/input/touch/state.rs` | `TouchState`（生イベント → 指の一覧。純ロジック・単体テスト付き） |
| `runtime/src/engine/core/input/touch/bridge.rs` | `PointerBridge`（相互変換と二重駆動の防止。単体テスト付き） |
| `runtime/src/engine/core/input/touch/test_sequence.rs` | 検証用の合成タッチ列（`debug.seed.touch_test=1` のときだけ流れる） |
| `runtime/src/engine/core/app_base/app/touch_diag.rs` | `[SEED TOUCH FRAME]` ログ（`lifecycle_diag_log` の端末だけ） |
| `runtime/src/engine/core/scripting/host_api.rs`（`ffi_input_touch`）・`input_bridge.rs`（`TOUCH_*`） | FFI（`ScriptHostApi` の末尾に `input_touch` を追加） |
| `scripting/src/Api/Touch.cs`・`TouchPhase.cs`・`Input.cs`・`ScriptHost.cs` | C# API |
| `runtime/android/native/src/debug_hooks.rs` | `debug.seed.touch_test` を読んで合成タッチ列を要求する |

### 12.2 決まりごと

- **座標**: `Input.MousePos` と同じ（ウィンドウのクライアント座標の物理ピクセル・左上原点。内部解像度固定のときは
  レターボックスを通した描画解像度の座標）。`Input::process_cursor_moved` と同じ `window_pos_to_input` を通す。
  Android はウィンドウが画面全体なので、`adb shell input tap X Y` の X, Y がそのまま位置になる。
- **段階は Unity と同じ**: Began は触れ始めたフレームだけ、動かなければ Stationary、Ended / Canceled はそのフレームだけ一覧に残って
  次フレームで消える。Moved / Stationary は「前フレーム末からの位置の差」で決める（Android は 1 本が動くと全ポインタの Moved を送るため）。
- **1 フレームに 1 段階**: 触れたフレームのうちに離れた指（素早いタップ・`input tap`）は、そのフレームは Began・次フレームで Ended。
  このフレームで離れた指と同じ OS の ID で触れ直したら、次フレームへ回す（一覧に同じ指が 2 度出ない）。
- **本数**: 同時に一覧へ載るのは最大 10 本（`MAX_TOUCHES`）。超えた指は離すまで無視する。
- **FingerId**: OS の ID ではなく、一覧で空いている最小の番号（0 起点）。並びは触れ始めた順。
- **指0**: 他に触れている指が無い状態で触れ始めた指。FingerId が 0 かどうかとは別の概念で、指0 が離れても残った指は引き継がない。
- **フォーカス喪失**: `WindowEvent::Focused(false)` で実タッチの指をすべて Canceled にする（OS の取り消しが届かない場合の安全弁）。

### 12.3 マウス ⇔ タッチの相互変換（`PlatformTraits`）

| フラグ | デスクトップ | Android | 振る舞い |
|---|---|---|---|
| `touch_drives_mouse` | false | true | 指0 の位置 → カーソル座標、触れている間 → 左ボタン押下、離れたら解放。他の指はマウスに影響しない。既存のキャンバス UI のポインタイベント・スクリプトのマウス API がタッチで動く |
| `mouse_simulates_touch` | true | false | マウス左ボタンで指を 1 本合成（押下 → Began、押下中の移動 → Moved、止まれば Stationary、離す → Ended）。PC の Play で `GetTouch` を試せる |
| `touch_supported` | false | true | `Input.TouchSupported` の値（プラットフォーム単位） |

- 2 つの変換フラグは排他（`platform/mod.rs` のテストで固定）。
- **二重駆動の防止**: `touch_drives_mouse` の端末では、実タッチが触れている間（とタッチ由来の押下が続いている間）は実マウスの
  カーソル移動・左ボタンを無視する。`mouse_simulates_touch` の端末で実タッチ（タッチパネル付き PC）が触れたら、合成中の指は
  Canceled で畳み、実タッチが触れている間は新しく合成しない（OS がタッチをマウスへ昇格して送ってきても 2 本に数えない）。
- winit 0.30.13 の Android 実装は MotionEvent の Down / PointerDown / Move / Up / PointerUp / Cancel をすべて `Touch` として送り、
  ホバーやマウスのボタン操作（`ACTION_HOVER_*` / `ACTION_BUTTON_*`）は捨てる（`CursorMoved` / `MouseInput` は出さない。ソースで確認）。
  GameActivity の既定の入力フィルタは `SOURCE_TOUCHSCREEN (0x1002)` とのビット積で判定するため、ポインタ系の入力元
  （マウス 0x2002 等）も通る。つまり Android のマウスは「押している間だけの指」として届く（§12.5 で `input mouse tap` で確認）。
- タッチ由来の左ボタンは TouchState が見せる段階に合わせる。素早いタップはマウスも「押下フレーム → 次フレームで解放」になる。
  指0 が入れ替わって解放と押下が同じフレームに重なるときは、押下を次フレームへ回す。
- タッチが動かすマウスでは `Input.MouseDelta`（カーソル座標の差分）は動くが、`Input.MouseMove`（OS の Raw Input）は 0 のまま。

### 12.4 確認方法

ログ（タグ `SEED`。§6）:

| 行 | 中身 |
|---|---|
| `[SEED TOUCH] Started id=0 pos=(540.0, 1200.0) ...` | winit から届いた生イベント（`app/lifecycle_diag.rs`） |
| `[SEED TOUCH FRAME] f=12 n=1 #0:Began(540.0,1200.0)d(0.0,0.0) \| mouse=(540.0,1200.0) L=PD-` | フレーム末の入力状態。`n` = `Input.TouchCount`、`#指番号:段階(位置)d(移動量)` が `GetTouch(i)`、`mouse` と `L`（P=押下中 / D=押した瞬間 / U=離した瞬間）がタッチ由来のマウス。変化のあったフレームだけ出る |
| `[SEED TOUCH TEST] Started id=0x5eed0000 pos=(...)` | 検証用の合成タッチ列（下記） |

```powershell
# 1 本指（実機・エミュレータ共通）
adb -s <serial> shell input tap 540 1200
adb -s <serial> shell input swipe 300 1600 800 1000 800
adb -s <serial> shell input mouse tap 700 900        # マウスの入力元（エミュレータで確認）
# 複数指: 非 root の端末は sendevent が SELinux で拒否され（エミュレータでも Permission denied）、input は 1 本指だけなので、
# 検証用の合成タッチ列を使う（3 本が同時に触れ、指0 だけがマウスを動かす列。中身は input/touch/test_sequence.rs）
adb -s <serial> shell setprop debug.seed.touch_test 1
adb -s <serial> shell am force-stop com.seedengine.runtime
adb -s <serial> shell am start -n com.seedengine.runtime/.MainActivity   # 最初のフレームの 2 秒後に 1 回流れる
adb -s <serial> logcat -d -s SEED | Select-String "TOUCH"
adb -s <serial> shell setprop debug.seed.touch_test 0                     # 必ず戻す
```

- 合成タッチ列は実タッチと同じ `Input::process_touch` を通る（座標の写像・状態機械・相互変換が本番と同じ）。
  プロパティが無ければ毎フレームのアトミック変数 1 回の読み取りだけで、本番の入力経路には影響しない。
- 一連の確認は作業用スクリプト（リポジトリ外）で自動化した: ビルド → install → アセット転送 → 起動 → 最初のフレーム待ち →
  **前面が SEED であることを確かめてから** input を注入 → logcat 保存。共用・私物の端末では、SEED 以外が前面のときに注入しない。
- PC の C# API は、`SEED.exe --mode=play --assets-root=<一時プロジェクト>/assets --scene=assets://scenes/Main.scene`
  （作業フォルダは `runtime/`。`../scripting/bin/Debug/net9.0/SEEDScripting.dll` を読む）で、`TouchCount` / `GetTouch(0)` /
  `Touches` / マウス状態を `SEED.Debug.Log` するスクリプトを載せ、SEED のウィンドウにだけ `PostMessage` でマウスのメッセージ
  （WM_MOUSEMOVE / WM_LBUTTONDOWN / WM_LBUTTONUP）を送って確かめた（実カーソルや他のウィンドウは動かさない）。

### 12.5 確認結果（2026-09-24）

エミュレータ（AVD `seed_pixel6_api35`・API 35・x86_64・画面 1080x2400）と PC（Windows・`SEED.exe` 単体の Play）で確認した。
実機（Pixel 6a・Android 16・arm64）は 2026-09-24 の追加確認で、下表の「実機」の行のとおり確認した（APK 内 pak の最小構成。約 18 fps）。
それより前の試行では、端末が私物として使用中で、APK 更新直後の初回起動が約 44 秒かかる間にユーザーが別アプリへ移り、最初のフレームの
提示前にプロセスが終了した。そのとき届いた実際の指のタッチ（戻るジェスチャのなぞりと、システムに取り消された 2 回のタッチ。いずれも ID 0）の
受信の並び（同じ ID の再タッチが 1 フレームに 3 回）は `state.rs` の回帰テストに写した。

| 確認項目 | 結果 |
|---|---|
| `input tap 540 1200` | 生の Started / Ended が同じフレームに届き、`[SEED TOUCH FRAME]` はそのフレームが `#0:Began(540.0,1200.0)` ＋ `L=PD-`、次のフレームが `#0:Ended` ＋ `L=--U`（タップが 2 フレームに分かれる） |
| `input swipe 300 1600 800 1000 800` | `Began` → 約 45 フレームの `Moved`（1 フレーム約 10〜20 px の移動量）→ `Ended(800.0,1000.0)` ＋ `L=--U`。マウス座標が毎フレーム指に追従 |
| `input mouse tap 700 900`（入力元がマウス） | `Touch` として届き、指と同じく `Began` → `Ended`（§12.3 の「Android のマウスは押している間だけの指」を確認） |
| OS 経由の複数指（エミュレータのコンソール `adb emu event send` で protocol B の 3 スロット） | `n=1 → 2 → 3` と同時に追跡。1 本目は `Stationary` のまま 2 本目だけ `Moved d(0.0,100.0)`、3 本目が `Began` → `Moved`。1 本目が離れたフレームで `L=--U`、その後 2 本目を動かしてもマウスは 1 本目の最後の位置のまま（`L=---`） |
| 合成タッチ列（`debug.seed.touch_test=1`） | 上と同じ並びが `[SEED TOUCH TEST]` の合成イベントから再現（3 本同時・指0 だけがマウスを駆動）。検証後にプロパティは元（未設定）へ戻した |
| 実機: 合成タッチ列（`debug.seed.touch_test=1`） | `n=1 → 2 → 3` を追跡（f=34〜61）。指0 だけがマウスを駆動（`L=PD-` → `P--` → 指0 が離れたフレームで `--U`、以後 `L=---` でマウスは指0 の最後の位置のまま）。2 本目の `Moved d(0.0,120.0)`・3 本目の `Moved d(54.0,0.0)` も列のとおり。プロパティは元（未設定）へ戻した |
| 実機: `input tap 540 1200` / `input swipe 300 1600 800 1000 800` | タップは `#0:Began(540.0,1200.0)` ＋ `L=PD-` → 次のフレームで `#0:Ended` ＋ `L=--U`。スワイプは `Began(300.0,1600.0)` → 14 フレームの `Moved`（1 フレーム約 55 ms・31〜41 px）→ `Ended(800.0,1000.0)` ＋ `L=--U`。マウス座標が毎フレーム指に追従 |
| 実機: 実際の指 | 合成列の後に届いた実際の指の上向きのなぞり（`[SEED TOUCH] Started id=0 pos=(1043.0,1516.0)` … `moves=16`）も `Began` → `Moved` ×4 → `Ended` で追跡され、マウスが追従 |
| 座標 | `input tap X Y` の X, Y がそのまま位置（ウィンドウが画面全体・原点一致） |
| PC（`SEED.exe` 単体の Play ＋ 確認用スクリプト） | 起動時 `TouchSupported=False` / `TouchCount=0` / `GetTouch(5)` は `Touch.None`。左ボタン押下で `#0 Began`、押したまま移動で `Moved delta=(50,20)`、静止で `Stationary`、離して `Ended` → 次フレーム `n=0`。押下と解放を同じメッセージ列で送った素早いクリックも `Began` → 次フレーム `Ended`（マウス自体は従来どおり同じフレームに押下・解放）。同じフレームに `GetTouch(0)` を 2 回呼んでも同じ値・`Touches.Length == TouchCount` |

- 非 root の端末では `sendevent` が SELinux で拒否される（エミュレータの shell でも `/dev/input/event2: Permission denied`。shell は
  input グループに入っているが書けない）。エミュレータはコンソールの `event send` で `ABS_MT_*` を送ると virtio のマルチタッチ装置
  （0〜32767 の範囲を画面へ写す）へ届く。実機は合成タッチ列（`debug.seed.touch_test`）で確かめる。

### 12.6 制限・持ち越し

詳細と持ち越し先は [backlog.md](backlog.md) の「Android」節。

- 実機（Pixel 6a）の `[SEED TOUCH FRAME]` と複数指（合成タッチ列）は 2026-09-24 に確認済み（§12.5）。実機で OS 経由の複数指
  （本物の 2 本指以上）は未確認（非 root では注入できない。人の指で触る必要がある）。
- スクリプトは段階B（§17）で Android でも動くようになり、スクリプトの `Input.GetTouch`（段階・位置・移動量）と `Input.TouchCount` は
  エミュレータと実機で確かめた（§17.11）。キャンバス UI のボタン反応（ポインタイベントの配信先がスクリプト）は Android では未確認。
- ジェスチャ（ピンチ・回転・長押し・ダブルタップ）・タップ回数・圧力の組み込み API は無い（スクリプトで組み立てる）。
- キャンバス UI のポインタイベントは指0 の 1 本だけ。指を離した後もマウス位置が残るので、ボタンのホバー状態は次に触れるまで残る。
- MCP の入力注入からはタッチを合成しない。タッチパネル付き PC の実タッチ（Windows の WM_TOUCH / WM_POINTER ＋ 昇格マウス）は未検証。

---

## 13. APK 内 pak からの起動（段階A・2026-09-24）

配布版（リリース版 APK）でも動くよう、Windows のパッケージ化と同じ `assets.pak` を APK に同梱し、AAssetManager 経由で
読めるようにした。run-as でのアセット転送（§5.2）は、APK を作り直さずにアセットだけ差し替える開発用の高速経路として残る。

### 13.1 起動モードの決め方

データの有無だけで決める（設定フラグは無い。`runtime/android/native/src/launch.rs`）。

| 順 | 条件 | 起動モード | 仮想パス `assets://<相対パス>` を読む順 |
|---|---|---|---|
| 1 | APK に `assets/seed/assets.pak` がある | パッケージ実行（`asset_fs::is_packaged() == true`） | PAK → APK の `seed/assets/<相対パス>` → 内部フォルダの `files/assets/<相対パス>` |
| 2 | 無い | 開発用の置き場（§4.5） | 内部フォルダ `files/assets`（run-as で送ったもの）→ 外部フォルダ |

- パッケージ実行でもアセットルートは内部フォルダの `files/assets`（PAK にも APK にも無いアセットの最後のフォールバック先）。
  フォルダは作らない（パッケージ実行では空で正常）。
- APK に pak が入っていると、run-as で送ったアセットは「PAK に無いもの」しか使われない。SeedAndroid は Gradle を
  回す前に置き場を今回の指定どおりにする（`--project` があれば SeedPak の出力、無ければ空＝開発用の APK。§13.5）。

### 13.2 APK 内のレイアウト

Windows のパッケージ出力（`{ゲーム名}/` から実行ファイルを除いたもの）と同じ相対構成を APK の `assets/seed/` に置く。
構成の正典はエンジンの `core::package_layout`（`PAK_FILE_NAME` / `LOOSE_ASSETS_DIR_NAME`）とエディタの `PackageLayout`。

```
APK
  assets/seed/                  配布物のルート（apk_package::APK_PACKAGE_ROOT）
    assets.pak                  アセット（SeedPak／パッケージ化ウィンドウの出力と同じもの）。非圧縮（STORED）で格納
    assets/<相対パス>            任意: PAK に入れずに置くアセット（PAK に無いときのフォールバック先）
  lib/<ABI>/libSEED.so
```

- ソース側は `runtime/android/app/src/main/assets/seed/`（生成物・`runtime/android/.gitignore` 済み）。Gradle の既定の
  assets の置き場なので、Gradle 側の設定は noCompress だけ。
- pak は `app/build.gradle.kts` の `androidResources { noCompress += "pak" }` で非圧縮のまま入れる。ネイティブ側は AAsset を
  Read + Seek しながらエントリを読むので、圧縮されていると後ろ向きの Seek のたびに先頭から展開し直すことになる。
  非圧縮で入ったかは起動ログの「非圧縮（APK 内の位置 N）」で分かる（`AAsset_openFileDescriptor64` が成功する＝非圧縮）。
- `project_settings.json` は PAK の中（収集の起点として必ず入る。[packaging.md](packaging.md) §2）。PAK の外に置く必要がある
  ファイルは現状無い（Windows の `bin/`＝スクリプト DLL と .NET は段階B）。PAK の外に置いたファイルも、同じ読み口で
  `assets://` として読める（13.1 の表の 2 番目）。APK 内のパスは大文字小文字を区別する（PAK の検索だけは区別しない）。
  段階B からはスクリプトの `bin/`（`assets/seed/bin/`）と同梱 .NET（`assets/seed/dotnet/<ABI>/`）も APK に入る（§17.3）。

### 13.3 仕組み

```
launch.rs: ApkPackageSource::probe_pak()   … AAssetManager で seed/assets.pak を開けるか・大きさ・非圧縮か
  └ LaunchArgs.package_source = Some(Arc<ApkPackageSource>)
      └ App::init_asset_fs（app_init.rs）: package_source::open_pak → PakReader::from_boxed(ApkAsset)
          └ asset_fs::init_with(assets_root, pak, Some(package))
              read_bytes("assets://x") = PAK の x → package.read_all("assets/x") → std::fs::read(<アセットルート>/x)
```

| 層 | ファイル | 役割 |
|---|---|---|
| PAK 形式 | `runtime/src/engine/pak/mod.rs` | `PakReader`。ファイル専用だった読み口を `PakSource` に一般化（`open(path)` はファイル、`from_source` / `from_boxed` は任意の読み口）。エントリ表は 64 KiB の先読み越しに読む |
| 読み口の抽象 | `runtime/src/engine/pak/source.rs` | `PakSource`＝`Read + Seek + Send`（ブランケット実装。File・Cursor・ApkAsset） |
| 配布物の読み口 | `runtime/src/engine/package_source.rs` | `PackageSource`（配布物のルート相対でファイルを開く・`Send + Sync`）、`open_pak`、`loose_asset_path` |
| 読む順 | `runtime/src/engine/asset_fs.rs` | `init_with` と `read_virtual_layers`（PAK → 配布物の PAK 外 → ファイルシステム） |
| 起動引数 | `app/mod.rs` の `LaunchArgs.package_source`・`app/app_init.rs` の `init_asset_fs` | 渡されていれば読み口から PAK を開く。デスクトップは None（従来どおり実行ファイルの隣の assets.pak をファイルで開く。PAK の外は従来どおり `std::fs`） |
| Android の実装 | `runtime/android/native/src/apk_package/` | `ApkPackageSource`（AAssetManager で `seed/<相対パス>` を開く）、`ApkAsset`（ndk の Asset に Send を足した包み） |
| 起動モード | `runtime/android/native/src/launch.rs` | 13.1 |

- ndk クレートは依存に足していない。android-activity が再公開する `winit::platform::android::activity::ndk` を使う
  （`AndroidApp` と同じく、android-activity と版が食い違わないようにするため。Cargo.toml・Cargo.lock は変更なし）。
- デスクトップも PAK を開けなかったとき（壊れている等）は理由を 1 行出すようになった（`[App][ERROR] assets.pak を開けません: …`。
  以前は黙って PAK 無しで起動していた）。開けたときは `[SEED INIT] asset_fs: packaged pak=<場所> entries=N`。

### 13.4 スレッド安全性

- `asset_fs` はメインスレッドのほか、モデルの非同期ロードのワーカー（`loader/async_loader.rs`）・rayon（`loader/asset_cache.rs`）・
  音声スレッドから呼ばれる。
- PAK の読み口は 1 本で、従来どおり `Mutex<PakReader>` で直列化する（デスクトップのファイルも同じ。同時性は変わらない）。
- ndk 0.9 の `Asset`（AAsset）は Read + Seek を実装するが Send ではない（生ポインタを持ち、ndk も付けていない）。NDK の資料が
  禁じるのは「複数スレッドで共有して同時に使うこと」で、AAsset にスレッドへの結び付き（スレッドローカルな状態）は無い。
  そこで Send だけを足した `ApkAsset` で包む（`unsafe impl Send`。Sync は足さない＝`&mut` でしか使えず、Mutex の中でしか
  触られない）。元の AAssetManager は android-activity がアプリ全体の AssetManager へのグローバル参照をリークして保持しており、
  プロセスの終わりまで有効。
- `PackageSource`（PAK の外のファイル）は呼び出しごとに Asset を開き直すので、Mutex 無しで同時に呼べる（AAssetManager はスレッド安全）。
- 並列に読みたくなったら、非圧縮の pak なら `Asset::open_file_descriptor` の fd に対する pread に置き換えられる（Mutex が要らなくなる）。
  今は読み込みの直列化が問題になる場面が無いので入れていない（backlog）。

### 13.5 ビルドと実行

```powershell
# pak を作って APK に入れ、push 無しで起動する（--project は .seedproj／assets/ を持つフォルダか、アセットルートそのもの）
dotnet run --project editor/tools/SeedAndroid -- run --project D:\path\to\Project --serial emulator-5554 --logcat-seconds 20

# pak だけを作る（エディタは起動しない。パッケージ化ウィンドウと同じ収録規則・パス書き換え・PAK 形式）
dotnet run --project editor/tools/SeedPak -- --project D:\path\to\Project --out <出力フォルダ>
```

| 経路 | 使う場面 | 変更の反映 | リリース版 APK |
|---|---|---|---|
| `--project`（APK 内 pak。ps1 の `-ProjectDir`） | 配布版と同じ形での確認・リリース版 | pak と APK を作り直して入れ直す（アセットが変わったときだけ。SeedPak → Gradle → install で約 10〜30 秒） | 動く |
| `--assets-dir`（run-as 転送。ps1 の `-AssetsDir`） | 開発中の素早い差し替え | `run --assets-dir …` の 2 回目以降は APK を作り直さず（変更なし）アセットだけ送り直す（1 秒未満） | 動かない（run-as はデバッグ版だけ） |

- `--project` と `--assets-dir` は同時に指定できない（端末は pak を優先するため）。
- `--skip-gradle --assets-dir` のとき、前回のビルドで APK に pak を入れていれば警告する（そのままの APK ならパッケージ実行が優先される）。
- SeedPak の詳細（引数・アセットルートの決め方・収録ルール・終了コード）は [packaging.md](packaging.md) §10。
- logcat の保存（`--log-file`・ps1 の `-LogFile`）は UTF-8 のまま書く（段階C-1 で、以前の日本語の文字化けを解消した）。

### 13.6 確認結果（2026-09-24）

エミュレータ（AVD `seed_pixel6_api35`・API 35・x86_64）。アセットは最小構成（BrainStem.glb ＋ 平行光。§7）。

| 確認項目 | 結果 |
|---|---|
| SeedPak の出力 | 3 ファイル / 3.0 MB（`assets.pak` 3,197,760 バイト・パス書き換え 2 件）。変更前のパッケージ化ウィンドウと同じ呼び出し（`AssetCollector` → `PakWriter` の直呼び）で作った pak と SHA-256 が一致 |
| APK 内の格納 | `assets/seed/assets.pak` が STORED（非圧縮）。起動ログに「非圧縮（APK 内の位置 60153292）」 |
| push 無しで起動 | run-as で送ってあった内部フォルダの `files/assets` を退避した状態で、`[SEED INIT] asset_fs: packaged pak=apk:seed/assets.pak entries=3` → `scene registry loaded count=1` → `load_play_scene done actors=2` → BrainStem が描画（約 57 fps）。起動後も `files/assets` は作られない |
| PAK の外（APK の `seed/assets/`） | モデルを抜いた PAK（2 件）＋ `seed/assets/models/BrainStem.glb`（APK 内では DEFLATED）の APK で、モデルが APK の PAK 外から読まれて描画された |
| 開発用の経路（pak 無し APK ＋ run-as） | 「APK に apk:seed/assets.pak がありません。開発用の置き場…から読みます」→ 内部フォルダから従来どおり描画 |
| Windows の配布物 | SeedPak の pak を `SEED.exe` の隣に置いた構成（`assets/` フォルダ無し）で、起動ログに `起動形態: パッケージ実行（配布物）`・`asset_fs: packaged pak=…\assets.pak entries=3`、BrainStem が描画（`SEED_SCREENSHOT_FRAMES` で撮影） |

実機（Pixel 6a・Android 16・Mali-G78）は 2026-09-24 の追加確認で、`-Abi arm64-v8a -Serial <実機> -ProjectDir <最小構成>` の APK
（pak 3.0 MB。push 無し）が「APK 内の pak で起動します（パッケージ実行）: apk:seed/assets.pak  3.0 MiB・非圧縮（APK 内の位置 52547452）」→
`[SEED INIT] asset_fs: packaged pak=apk:seed/assets.pak entries=3` → `load_play_scene done actors=2 (278ms)` → BrainStem が描画
（約 18.3〜19.0 fps。縦画面）。`am start -W` の TotalTime は 199〜499 ms。

### 13.7 制限・持ち越し

詳細と持ち越し先は [backlog.md](backlog.md) の「Android」節。

- ~~パッケージ実行ではセーブ・キャッシュを書けない~~ → 段階A-3 でアプリ専用フォルダへ振り替えた（§14.1）。
- モデルの派生キャッシュは PAK 実行では効かない（Windows の配布物と同じ。[packaging.md](packaging.md) §8）。パイプラインキャッシュは
  段階A-3 から保存される（§14.3）。
- ~~段階B（スクリプト）: Android のパッケージ実行も DLL を配布物の `bin/` から読む必要がある~~ → 段階B で、同梱 .NET の起動材料
  （`LaunchArgs.embedded_clr`）があれば事前コンパイル DLL をバイト列で読むようにした（APK の `bin/` は `PackageSource` で読む。§17.7）。
- APK 内のパスは大文字小文字を区別する（PAK の外に置くファイルは、参照と実名を一致させる）。
- 読み出しは読み口 1 本の直列化（13.4）。

---

## 14. 保存先の振り替え・セーブの保護・起動基盤（段階A-3・2026-09-24）

パッケージ実行（APK 内 pak）で書けなかったセーブ・キャッシュをアプリ専用フォルダへ振り替え、Android 流の
「バックグラウンドへ回るときに書き出す」を入れた。あわせて、パイプラインキャッシュの保存（起動の短縮）・
バックグラウンド中の物理スレッド停止・戻るキーの Escape 化を行った。デスクトップの振る舞いは変えていない
（パイプラインキャッシュのファイル名だけアダプタごとになった。§14.3）。

### 14.1 書き込み先（`engine/platform/paths.rs`）

| 用途 | Android（パッケージ実行・開発用の置き場とも） | デスクトップ（従来どおり） |
|---|---|---|
| セーブ | `/data/user/0/<pkg>/files/save/save.json` | パッケージ実行 `{exe}/saved/`・エディタ Play `{assets}/../save/` |
| モデルの派生キャッシュ（.smdl） | `/data/user/0/<pkg>/cache/` | パッケージ実行 `{exe}/caches/`・開発 `{assets}/../cache/` |
| パイプラインキャッシュ | `/data/user/0/<pkg>/cache/wgpu_pipeline_cache_vulkan_<ベンダー>_<デバイス>.bin` | パッケージ実行 `{exe}/caches/`・開発 実行ファイルの隣（ファイル名の規則は同じ） |
| 環境変数 `TMPDIR` / `HOME` | `cache` / `files` | 触らない |

- `PlatformPaths { data_dir, cache_dir }` は「起動時に 1 回だけ設定する値」（`OnceLock`）。Android の糊（`app_dirs.rs`）が
  `internal_data_path()`（files）と、その親の `cache`（`platform::paths::android_cache_dir_for_files_dir`。Context.getCacheDir() と
  同じ場所。無ければ作る）を設定する。デスクトップは設定しない（`None`）ので、`save::path::decide_save_dir`・
  `package_layout::decide_cache_dir` は従来の規則のまま（単体テストで固定）。
- 設定されていれば起動モードに関係なく最優先（セーブの規約 2・キャッシュの規則 1）。以前は実行ファイル（app_process）の隣
  ＝ `/system/bin` を基準にしていたため、パッケージ実行では `/system/bin/saved`・`/system/bin/caches` を指して書けなかった。
- セーブのフォルダ名は `save`。段階0 からの開発用の置き場（アセットルート files/assets の親の save/）と同じ場所なので、
  既存の端末のセーブをそのまま引き継ぐ。
- `TMPDIR` / `HOME` はアプリプロセスに元々無い（zygote から受け継ぐ環境に無い。エミュレータの `/proc/<pid>/environ` で確認）。
  段階B の .NET（`Path.GetTempPath()`・ユーザーフォルダ系 API）と Rust の `std::env::temp_dir()`（未設定だと
  アプリから書けない `/data/local/tmp`）のために設定する。ネイティブで `set_var` しないのは、Rust 2024 で unsafe（他スレッドの getenv と
  競合）になったため。ネイティブのスレッドが無いうちに `MainActivity.onCreate`（super.onCreate の前）で `Os.setenv` し、
  ネイティブ側は値を読んでログに出すだけ（違っていれば警告）。

### 14.2 セーブの書き出しタイミング

| 契機 | 経路 | 備考 |
|---|---|---|
| バックグラウンドへ回る（ホーム・アプリ切り替え・画面オフ・最近のタスク） | winit の `suspended` → `app/background_lifecycle.rs` の `enter_background` → `save::flush_if_dirty()` | 同期で書く。android-activity の glue はこの処理が終わるまで UI スレッドを待たせるので、直後にプロセスが殺されても残る |
| Activity の破棄（アプリを閉じる・最近のタスクから消す） | `MainActivity.onDestroy` → JNI `nativeFlushSaveData`（`jni_exports.rs`）→ `save::flush_if_dirty()` → `Process.killProcess` | 保険。通常は onStop（ウィンドウ破棄 → suspended）で書き出し済みで「未書き出しの変更なし」になる |
| スクリプトの `SaveData.Save()` | 従来どおり | 段階B |

- JNI は命名規則（`Java_com_seedengine_runtime_MainActivity_nativeFlushSaveData`）で結び付く（`System.loadLibrary` 済みのため）。
  JNIEnv を触らないので jni クレートは使っていない。UI スレッドから呼ぶが、セーブのストアは Mutex で守られている。
  panic は JNI の境界で受け止め、ログは liblog へ直接書く（プロセス終了の直前でも消えない）。
- 背面へ回る処理の順序は「セーブ → パイプラインキャッシュ → 物理停止の印（`background_gate`）」。印を見た後の書き換えは
  その suspended の書き出しに含まれないと言い切れる（検証用フックのモード 2 がこれを使う）。
- 前面のまま `am force-stop`・OS による強制終了・クラッシュでは、直前の背面移行以降の変更は失われる（Windows の強制終了と同じ）。
- 「Activity の破棄＝プロセス終了」の方針（§8）は変えていない（winit 0.30 が onDestroy を知らせないため）。

### 14.3 パイプラインキャッシュ（`renderer/pipeline_cache/`）

wgpu 25 の `Features::PIPELINE_CACHE`（Vulkan のみ）が使える環境で、キャッシュを 1 つ作って全パイプラインの生成に渡し、
中身をファイルへ保存して次回起動で読む。非対応の環境（feature 無し）では従来どおりキャッシュ無し。

- 以前もキャッシュ自体は作っていたが、(1) 置き場が実行ファイル基準で Android では保存できなかった、(2) 一部の生成箇所
  （テキスト・2D/3D プリミティブ・軸ギズモ・アイコン・操作ガイド・Hi-Z・シェーディングアセット・水面・インタラクション場・
  屈折ピラミッド）が `cache: None` だった、(3) 保存が Renderer の Drop だけで、Android では走らなかった。
- 全生成箇所（`create_render_pipeline` / `create_compute_pipeline` の 34 か所と `RenderPipelineBuilder` 経由）がキャッシュを受け取る。
  Renderer から引数で渡せない箇所は `pipeline_cache::shared::shared()`（Renderer が登録した複製。wgpu のハンドルは複製しても
  同じキャッシュを指す）を使う。キャッシュはデバイス専用だが、アプリのデバイスは Renderer の 1 つだけ。
- ファイル名は `wgpu::util::pipeline_cache_key`（バックエンド・ベンダー ID・デバイス ID）＋ `.bin`。GPU が 2 つある PC でも
  交互に上書きしない。旧 `pipeline_cache.bin` は新しい名前のファイルが無いときだけ読む（デスクトップの移行用。保存はしない）。
- 保存: Android は `suspended` のたび、デスクトップは Renderer の Drop。内容のハッシュが前回の読み込み・保存と同じなら書かない。
  `<名前>.bin.tmp` へ書いてから rename（書き込み中に殺されても壊れたファイルを残さない）。
- 読み込み: wgpu の検証で弾かれるデータ（ドライバ更新・破損は fallback で黙って空。別アダプタは fallback でも検証エラー。§10）は
  エラースコープで捕まえて空のキャッシュで作り直す。読み込んだ大きさと採用後の大きさをログに出す（採用後が小さければ弾かれた）。
- SeedAndroid（build_and_run.ps1）の起動の工程は毎回 `am force-stop` するので、ホームへ戻さずに作業を繰り返すと保存されない（backlog）。

### 14.4 バックグラウンド中の停止（物理スレッド・ゲーム時間）

- `core/background_gate.rs`: 前面・背面の共有状態（AtomicBool ＋ Mutex/Condvar）。App の suspended（書き出しの後）で背面、
  2 回目以降の resumed（サーフェスを作り直せたとき）で前面。初回の resumed の後にも前面を確定させる。
- 物理スレッド（3D `physics/thread.rs`・2D `physics/thread2d.rs`。キャンバスごとの 2D も）はコマンドを捌いた直後に
  `physics/background_pause.rs` を呼び、背面の間はステップを進めず条件変数で眠る。前面へ戻れば即座に起きる。
  背面の間も 250 ms ごとに起きてコマンド（Stop・同期の問い合わせ）を捌く。ゲーム側の Pause（タイムライン）とは独立。
  復帰時は次ステップ時刻を今へ合わせ直し、背面にいた時間ぶんのステップを取り戻さない。
- 前面へ戻った最初のフレームは `Clock::forget_elapsed` で背面にいた時間を捨てる（delta が背面の時間になり、
  ConstantUpdate がその時間ぶん連続で回るのを防ぐ）。
- デスクトップは suspended が来ないので不変（物理スレッドは毎ループ Atomic の読み取り 1 回だけ）。
- 音声は段階A-5 で出力ストリームごと一時停止するようにした（オーディオスレッドのコールバックも止まる。§16.2）。
  ゲームパッド（gilrs）のスレッドは止めていない（backlog）。

### 14.5 戻るキー

- GameActivity は戻るキー（ナビゲーションバーの戻る・戻るジェスチャ）をネイティブへ渡し、winit は
  `physical_key=Unidentified(NativeKeyCode::Android(4))`・`logical_key=Named(BrowserBack)` で届ける。エンジンの KeyCode に
  無いので、これまでは入力状態に入らず捨てられていた。
- `core/input/key_remap.rs` の置き換え表（データ）で `KEYCODE_BACK`（4）→ `KeyCode::Escape`。どの表を使うかは
  `PlatformTraits::key_remap`（Android だけ。デスクトップは空の表）。`app/event_handler.rs` の on_keyboard_input が
  入力状態へ入れる前に引くので、スクリプトの `Input.GetKeyDown(KeyCode.Escape)` で拾える（Unity と同じ）。
- アプリは自動で終了しない（ネイティブ側が処理済みにするので onBackPressed は呼ばれない。§10）。ポーズ・終了確認はスクリプトが決める。
- 置き換え元を論理キー（BrowserBack）でなく Android のキーコードにしたのは、PC のキーボードの「ブラウザの戻る」キー
  （物理キー KeyCode::BrowserBack）と混同しないため。

### 14.6 確認方法

```bash
# 1) 書き込み先: 起動ログの「書き込み先: …」「環境変数 TMPDIR=…」「[SEED SAVE] save file: …」
adb exec-out run-as com.seedengine.runtime sh -c 'ls -la files/save cache; cat files/save/save.json'

# 2) セーブ（検証用フック。スクリプトの代わりに起動時にカウンタを書き換える。debug_save_test.rs）
adb shell setprop debug.seed.save_test 1
#   起動 → ホーム（suspended で書き出し）→ adb shell am kill com.seedengine.runtime → 起動 → 増えた値が読める
#   比較: 前面のまま adb shell am force-stop → 起動 → 増える前の値のまま（書き出しは背面へ回るときだけ）
adb shell setprop debug.seed.save_test 2    # 加えて、背面へ回った後（書き出しの後）に LATE キーを書き換える
#   起動 → ホーム → adb shell am stack list で SEED の RootTask id → adb shell am stack remove <id>
#   （Activity が破棄され onDestroy の JNI が書き出す）→ 起動 → LATE キーが読める
#   比較: ホーム → am kill → 起動 → LATE キーは前の値のまま
adb shell setprop debug.seed.save_test 0    # 必ず戻す

# 3) パイプラインキャッシュ: 起動 → ホーム（保存）→ am kill → 起動。「読込 N KiB → 採用後 M KiB」と
#    [SEED INIT] DrawContext created (N ms) を比べる。効果だけを見るには、キャッシュファイルを消した起動と比べる
adb exec-out run-as com.seedengine.runtime sh -c 'rm -f cache/wgpu_pipeline_cache_*.bin'

# 4) 物理スレッド: ホーム中に [SEED PHYSICS] の停止ログ。スレッドごとの CPU 時間は
#    run-as で /proc/<pid>/task/*/stat の utime+stime（14・15 番目）を 2 回読んで差を取る

# 5) 戻るキー（押して離す／長押し）
adb shell input keyevent KEYCODE_BACK
adb shell input keyevent --longpress KEYCODE_BACK
```

### 14.7 確認結果（2026-09-24）

エミュレータ（AVD `seed_pixel6_api35`・API 35・x86_64）。アセットは最小構成（§7）。パッケージ実行（`-ProjectDir`）で確認し、
書き込み先は開発用の置き場（`-AssetsDir`）でも確認した。デスクトップは `SEED.exe` 単体の Play（同じ最小構成）。

| 項目 | 結果 |
|---|---|
| 書き込み先 | 「書き込み先: データ（セーブ）=/data/user/0/com.seedengine.runtime/files / キャッシュ=/data/user/0/com.seedengine.runtime/cache」、`[SEED SAVE] save file: …/files/save/save.json`（パッケージ実行・開発用の置き場の両方）。`TMPDIR=…/cache`・`HOME=…/files` |
| セーブ（suspended） | save_test=1: 起動時 None → メモリ上で 1 → ホームで「[SEED SAVE] suspended: 未書き出しの変更を書き出しました」→ save.json に 1 → am kill → 再起動で Some(1)。比較（前面のまま force-stop）: メモリ上の 2 は残らず Some(1) |
| セーブ（onDestroy の JNI） | save_test=2: ホームの書き出しの後に LATE=2（メモリ上）→ `am stack remove` → 「MainActivity.onDestroy … → セーブを書き出してプロセスを終了します」「[SEED SAVE] onDestroy（プロセス終了前）: 未書き出しの変更を書き出しました」→ save.json に LATE=2 → 再起動で Some(2)。比較（ホーム → am kill）: LATE=3 は残らず Some(2) |
| パイプラインキャッシュ | 初回「保存済みのファイル無し（初回）」→ ホームで「保存 1796 KiB（2.3 ms）」→ 2 回目「読込 1796 KiB → 採用後 1796 KiB」。変化が無い背面移行は「変化なし（1796 KiB）のため保存しません」 |
| 生成時間（エミュレータ） | インストール直後の最初の起動は `描画パイプライン生成 合計` 3141 ms、2 回目 1256 ms。ただしこの差の大半はホスト側（gfxstream 経由の NVIDIA ドライバ）のシェーダキャッシュで、キャッシュファイルの有無だけを切り替えた比較（同じ温まり方で 2 回ずつ）は無し 1236 / 1236 ms、有り 1158 / 1145 ms（約 7% 減）。実機（Mali）での短縮幅は未計測 |
| 物理スレッド | ホームで「[SEED PHYSICS] 2D / 3D 物理スレッド: アプリがバックグラウンドのためステップを止めて眠ります」、前面で「…前面へ戻ったのでステップを再開します（止めていた時間 21.6 秒）」。スレッドごとの CPU 時間（1 tick＝10 ms）: 前面 約 7 秒で 3D 65・2D 16 tick、背面 約 11 秒で 2・1 tick |
| 戻るキー | `input keyevent KEYCODE_BACK` → `[SEED KEY] pressed logical=Named(BrowserBack) physical=Unidentified(Android(0x0004))` → `[SEED KEY FRAME] f=1235 Escape:down+up`。長押しは down と up が別フレーム。Activity は前面のまま |
| デスクトップ（SEED.exe の Play → ウィンドウを閉じる） | 1 回目: 「旧ファイル名から読込 3079 KiB → 採用後 3079 KiB: …\target\debug\pipeline_cache.bin（保存は …\wgpu_pipeline_cache_vulkan_4318_9504.bin）」→ 終了時「保存 3130 KiB（3.1 ms）」。2 回目: 「読込 3130 KiB → 採用後 3130 KiB」→ 終了時「変化なし」。モデルキャッシュは従来どおりアセットルートの親の `cache/` |

実機（Pixel 6a・Android 16・Mali-G78 ドライバ r54p1・arm64 の debug ビルド）は 2026-09-24 の追加確認で、APK 内 pak の最小構成により
下表のとおり確認した（`build_and_run.ps1 -ProjectDir`。各回は起動 → 描画 → ホーム → `am kill`）。

| 項目 | 実機の結果 |
|---|---|
| 書き込み先 | 「書き込み先: データ（セーブ）=/data/user/0/com.seedengine.runtime/files / キャッシュ=/data/user/0/com.seedengine.runtime/cache」、`TMPDIR=…/cache`・`HOME=…/files`、`[SEED SAVE] save file: …/files/save/save.json` |
| セーブ（suspended） | save_test=1: 起動時 None → ホームで「未書き出しの変更を書き出しました」→ am kill → 次の起動で Some(1)。以後 Some(2)・Some(3)・Some(4) と毎回残った。比較（前面のまま force-stop）: メモリ上の 5 は残らず Some(4) |
| パイプラインキャッシュ | 初回（インストール直後・ファイル無し）`描画パイプライン生成 合計 3595 ms` → ホームで「保存 618 KiB（6.2 ms）」→ 2 回目「読込 618 KiB → 採用後 618 KiB」で **528 ms**。ファイルを消して繰り返すと 3014 ms → 522 ms（**約 85% 減**）。ファイル名は `wgpu_pipeline_cache_vulkan_5045_2449604608.bin`（ARM のベンダー ID 0x13b5） |
| 物理スレッド | ホームで「2D / 3D 物理スレッド: アプリがバックグラウンドのためステップを止めて眠ります」、前面で「前面へ戻ったのでステップを再開します（止めていた時間 27.0 秒）」 |
| 戻るキー | `input keyevent KEYCODE_BACK` → `[SEED KEY] pressed logical=Named(BrowserBack) physical=Unidentified(Android(0x0004))` → `[SEED KEY FRAME] f=100 Escape:down`・`f=101 Escape:up`（実機は 1 フレーム約 55 ms のため押下と解放が別フレーム）。Activity は前面のまま |

### 14.8 制限・持ち越し

詳細と持ち越し先は [backlog.md](backlog.md) の「Android」節。

- 実機（Pixel 6a）での確認は 2026-09-24 に済んだ（§14.7。パイプライン生成 約 3.0〜3.6 秒 → 約 0.52 秒）。
- SeedAndroid（build_and_run.ps1）は毎回 force-stop してから起動するので、開発中にホームへ戻さないとパイプラインキャッシュが保存されない
  （最初のフレームの後にも 1 回保存する案）。
- 背面中もゲームパッド（gilrs）のスレッドは動く（音声は段階A-5 で止めた。§16）。`[PLAY_WD]` の監視ログが背面中に誤報を出す（既存の一時診断）。
- 前面のまま強制終了された分のセーブは失われる（仕様）。
- 起動の初期化（handle_resumed）が android_main スレッドで同期に走る問題（ANR の恐れ）は変わっていない（backlog の既存項目）。

---

## 15. 画面の向きと安全領域（段階A-4・2026-09-24）

プロジェクト設定で画面の向き（縦横どちらも／縦に固定／横に固定）を選べるようにし、スクリプトから画面の寸法・安全領域・
向き・DPI を読む `SEED.Screen`（`Width` / `Height` / `SafeArea` / `Orientation` / `DPI`）を追加した。
利用者向けの API 説明は [scripting_api.md](scripting_api.md) §7.12。

### 15.1 画面の向きの設定（プロジェクト設定 → マニフェスト）

```
エディタ「プロジェクト設定 → 解像度設定 → 画面の向き（モバイル）」（値と表示名は editor/src/ProjectSettings/ScreenOrientationSetting.cs）
  └ project_settings.json の screen_orientation（"both" / "portrait" / "landscape"。既定 "both"）
      └ SeedAndroid の AndroidProjectSettingsReader（--project / --assets-dir のアセットルートから読み、正規化する。
        どちらも無ければ both。知らない値は警告して both。editor/src/Android/Project/）
          └ gradlew assembleDebug -Pseed.orientation=<値>
              └ app/build.gradle.kts の変換表 → manifestPlaceholders["seedScreenOrientation"]
                  └ AndroidManifest.xml の android:screenOrientation="${seedScreenOrientation}"
```

| 設定値 | マニフェスト（値） | 振る舞い |
|---|---|---|
| `both`（既定） | `fullSensor`（10） | 縦横 4 方向に追従（端末の回転ロックは無視してセンサーに従う。従来どおり） |
| `portrait` | `sensorPortrait`（7） | 縦だけ。逆さの縦へ回るかは端末の設定次第（エミュレータでは 0 度のままだった） |
| `landscape` | `sensorLandscape`（6） | 横だけ。左右どちら向きの横にもセンサーに従って回る |

- **変換表は `app/build.gradle.kts` の 1 か所だけ**。SeedAndroid は値を読んで（エディタと同じ `ScreenOrientationSetting.Normalize` で）
  正規化して渡すだけ、エディタは値と表示名だけを持つ。知らない値は SeedAndroid が警告して `both` にする（手で gradlew を叩いたときは
  Gradle が警告 `SEED: screen_orientation="…" は不明な値です…` を出して `both` として扱う）。前後の空白・大文字小文字は吸収する。
- 起動時に読む値ではなく **APK（マニフェスト）に焼き込む**。値を変えたら APK を作り直す（SeedAndroid は渡すプロパティが変われば Gradle を回す。
  `--skip-gradle` では前回の APK のまま）。段階C-2 のエディタ統合も同じ中核を呼ぶので同じ判定になる。
- `--project` のアセットルートは SeedPak と同じ規則（`editor/src/Project/ProjectFolderResolver.cs`）で決める（`.seedproj` の `assets_dir` →
  `<フォルダ>/assets` → フォルダ自体）。
- 確かめ方: Gradle の出力の `SEED: screen_orientation=<値> → screenOrientation=<マニフェストの値>` と、
  `aapt2 dump xmltree --file AndroidManifest.xml app/build/outputs/apk/debug/app-debug.apk` の `screenOrientation(0x0101001e)=<数値>`。

### 15.2 安全領域と向きの流れ

```
MainActivity（Java・UI スレッド）: onApplyWindowInsets / onGlobalLayout / onConfigurationChanged ＋ 表示の変化（DisplayListener）
  └ ScreenReporter（java/.../ScreenReporter.java）
       描画面（GameActivity の SurfaceView）の大きさ・安全領域（描画面の各辺からの距離・物理ピクセル）・
       Display.getRotation・表示の自然な向きの大きさ（Display.Mode の physicalWidth / Height）。前回と同じなら送らない
      └ JNI nativeOnScreenChanged（native/src/jni_exports.rs）→ platform::screen::report::submit（Mutex。最新と直前の 2 件）
エンジン（android_main のスレッド）: 毎フレーム 1 回、Play 中のゲームロジックの先頭（ポインタイベント・スクリプトより前）
  App::publish_screen_snapshot（app/screen_publish.rs）
    ├ report::select_for_frame(今の描画面の大きさ)     大きさが一致する報告だけを使う（§15.3）
    ├ ScreenSnapshot::compute（platform/screen/snapshot.rs。純関数）  描画ターゲット座標への写像・向き・DPI
    ├ screen_bridge::publish_screen_snapshot             スクリプトが読む写し（スレッドローカル）
    └ screen_diag::observe                               [SEED SCREEN] ログ（変化したフレームだけ。lifecycle_diag_log の端末）
スクリプト: SEED.Screen.* → ffi_screen（scripting/screen_bridge.rs。kind 0=寸法 / 1=安全領域 / 2=向き / 3=DPI）
```

| ファイル | 役割 |
|---|---|
| `runtime/src/engine/platform/screen/report.rs` | OS の報告（`ScreenReport`）の保持と選び方（`ReportHistory::select_for_frame`） |
| `runtime/src/engine/platform/screen/orientation.rs` | `ScreenOrientation` と判定（回転＋表示の自然な向き／縦横比）。純関数 |
| `runtime/src/engine/platform/screen/snapshot.rs` | 1 フレーム分の値を作る純関数（描画ターゲット座標への写像・レターボックス・DPI） |
| `runtime/src/engine/platform/mod.rs` | `PlatformTraits::reference_dpi`（Windows 96 / Android 160） |
| `runtime/src/engine/core/scripting/screen_bridge.rs` | FFI（`ffi_screen`。`ScriptHostApi` の末尾に `screen`）と写しの公開口 |
| `runtime/src/engine/core/app_base/app/screen_publish.rs`・`screen_diag.rs` | 毎フレームの公開・診断ログ |
| `runtime/android/app/src/main/java/com/seedengine/runtime/ScreenReporter.java` | WindowInsets・回転を集めて JNI へ（MainActivity は契機の受け口だけ） |
| `runtime/android/native/src/jni_exports.rs` | `nativeOnScreenChanged`（ScreenReporter の static native） |
| `scripting/src/Api/Screen.cs`・`ScreenOrientation.cs`・`Rect.cs`・`ScriptHost.cs` | C# API |

### 15.3 決まりごと

- **安全領域の中身**: `max(getInsets(systemBars() | displayCutout()), getInsetsIgnoringVisibility(navigationBars()))`（辺ごとの最大）。
  没入モード（全画面。今のまま）ではシステムバーが隠れていて `systemBars()` は 0 なので、実際に効くのは「切り欠き（カメラ穴）」と
  「ナビゲーションバー（ジェスチャーバー）の範囲」。ナビゲーションバーを隠れていても数えるのは、その辺をなぞるとまずシステムがバーを出し
  （ゲームの操作として届かない）、出たバーは画面に重なるため（iOS のホームインジケータと同じ扱い）。ステータスバーは隠れている間は数えない
  （横持ちの上端が丸ごと使えなくなるのを避ける）。各種類の値は Java のログの内訳に出る（§15.4）。
- **座標**: `Input.MousePos` と同じ描画ターゲットの左上原点・Y 下向き・ピクセル。Java は窓基準の距離を描画面（SurfaceView）の各辺からの
  距離へ直して渡す（没入モードでは描画面＝窓＝画面全体なので同じ値）。エンジンは「ウィンドウに合わせて描く」ではそのまま、
  「解像度を固定」では入力と同じ `renderer/letterbox.rs` の `window_to_internal` で内部解像度の座標へ写し、描画ターゲットの外（黒帯）を切り落とす。
- **向き**: 報告があれば `Display.getRotation` と表示の自然な向きから決める（自然な向きが縦の端末: 0=Portrait / 1=LandscapeLeft /
  2=PortraitUpsideDown / 3=LandscapeRight。自然な向きが横の端末は自然な向きを LandscapeLeft として 1 つずらす）。自然な向きに窓の大きさを
  使わないのは、分割画面などで窓と表示の縦横が食い違うため。報告が無い（デスクトップ・最初の報告前・回転の直後で大きさが一致しない）ときは
  描画面の縦横比（縦長 = Portrait、それ以外 = LandscapeLeft）。
- **DPI**: winit の `scale_factor` × `PlatformTraits::reference_dpi`。winit の Android 実装は `scale_factor = densityDpi / 160` なので
  densityDpi（エミュレータ 420）に戻る。Windows は 96 × 表示スケール。
- **1 フレーム内で不変**: 写しの差し替えはフレームの決まった位置で 1 回だけ。OS の報告は別スレッドからいつ届いても次のフレームまで見えない。
  写しを作るときも描画面の実寸は 1 回だけ読み、描画ターゲットの寸法（`render_target_size_for`）・向き・安全領域をそこから求める
  （リサイズ中に 2 回読むと、寸法は新しく向きは古い大きさから、というフレームが PC で 1 回出たため）。
- **回転の直後**: Java の報告と winit の `Resized` は別経路で前後して届く。報告に「そのときの描画面の大きさ」を添え、エンジンは今の描画面と
  大きさが一致する報告だけを使う。報告が先に届いた間は直前の報告（古い向き）を使い続け、描画面が先に変わった間は一致する報告が無いので
  全画面・縦横比の向きになる（エミュレータで 1 フレーム観測）。描画面が一度でも最新の報告に追い付いたら古い報告は捨てる
  （270 → 180 → 90 度と回したとき、大きさが同じ 270 度の報告を 90 度の描画面に 1 フレーム当ててしまう不具合をエミュレータで見つけて直した。
  `report.rs` の回帰テスト）。Java 側も、回転の通知の時点ではレイアウトが前の向きのまま（描画面の大きさと WindowInsets が古い）ことがあるので、
  回転の通知からの報告は「描画面の縦横が回転後の表示の縦横と合うとき」だけにし、合わなければレイアウト完了（onGlobalLayout）からの報告に任せる。
- **回転への追従（既存の `on_resize` 経路）**: カメラのアスペクト（`on_resize` の `set_aspect_ratio`）・キャンバス UI（毎フレーム描画面の大きさで
  レイアウト）・タッチ座標（`window_pos_to_input`。内部解像度固定のときの写像は `on_resize` の `sync_input_view_map` で張り直す）を 4 方向で確かめ、
  いずれも追従していたので修正はしていない（§15.5）。

### 15.4 確認方法

| 行 | 中身 |
|---|---|
| `[SEED SCREEN] Java 報告: frame=1080x2400 insets=(0,128,0,63) rotation=0 natural=1080x2400 内訳{cutout=… bars=… navIgnoringVisibility=… statusIgnoringVisibility=…}` | Java が集めた値（描画面基準）と、窓基準の種類ごとの内訳 |
| `[SEED SCREEN] 報告を受け取りました: …` | JNI で受け取った（内容が変わったときだけ） |
| `[SEED SCREEN] size=1080x2400 safe=(0,128,1080,2209) orientation=Portrait dpi=420 window=1080x2400 report=frame=… rot=… natural=… insets=(…)` | スクリプトの `SEED.Screen` が返す値（変化したフレームだけ・Play 中）。`report=none` は今の描画面に一致する報告が無いフレーム |

```bash
# 4 方向。adb emu rotate は 1 回ごとに ROTATION_0 → 270 → 180 → 90 → 0 と回る（4 回で元に戻る）
adb -s emulator-5554 emu rotate
adb -s emulator-5554 logcat -d -v threadtime --pid=$(adb -s emulator-5554 shell pidof com.seedengine.runtime) | grep "SEED SCREEN"
# タッチ座標の確認（回転後の表示の座標で指定。右上付近）
adb -s emulator-5554 shell input tap 2300 150      # 横。[SEED TOUCH FRAME] の Began(2300.0,150.0)

# カメラ穴の模擬（エミュレータだけ。実機では触らない）。切り替えは Activity を作り直す構成変更なので、
# 動いている SEED はプロセスごと終わる（§8 の方針）→ am start で起動し直す
adb -s emulator-5554 shell cmd overlay enable com.android.internal.display.cutout.emulation.corner   # double / tall / hole も可
adb -s emulator-5554 shell cmd overlay disable com.android.internal.display.cutout.emulation.corner  # 必ず戻す
# overlay を切り替えた後、システムバーの高さが古いまま残ることがある（ナビゲーションバー 63 → 84 px・ステータスバー 128 → 126 px）。
# 表示密度を一度変えて戻すと読み直される（dumpsys window の InsetsSource で確かめる）
adb -s emulator-5554 shell wm density 400 && adb -s emulator-5554 shell wm density reset
adb -s emulator-5554 shell "dumpsys window" | grep -E "InsetsSource id=.* type=(navigationBars|statusBars|displayCutout) "

# 向きの固定: screen_orientation を portrait / landscape にしたアセットで APK を作り、マニフェストと回転を確かめる
pwsh -File runtime/android/build_and_run.ps1 -Abi x86_64 -Serial emulator-5554 -SkipRustBuild -AssetsDir <portrait にしたアセット>
aapt2 dump xmltree --file AndroidManifest.xml runtime/android/app/build/outputs/apk/debug/app-debug.apk | grep screenOrientation
```

- AVD `seed_pixel6_api35` は overlay 無しでも上端に 128 px の切り欠き（Pixel 6 のカメラ穴）を持つ。
- 共用の端末では、回転・overlay・表示密度を変えたら元に戻す（`cmd window user-rotation` / `fixed-to-user-rotation`・
  `settings get system accelerometer_rotation` と `cmd overlay list` で控えておく）。
- PC の C# API は、`SEED.exe --mode=play --assets-root=<一時プロジェクト>/assets --scene=assets://scenes/Main.scene`（作業フォルダは `runtime/`）で
  `SEED.Screen.*` を `SEED.Debug.Log` するスクリプトを載せ、SEED のウィンドウだけを `SetWindowPos` で縦長・横長に変えて確かめた。

### 15.5 確認結果（2026-09-24）

エミュレータ（AVD `seed_pixel6_api35`・API 35・x86_64・1080x2400・420 dpi・ジェスチャーナビゲーション）。アセットは最小構成（§7）に、
四隅へアンカーした色付きスプライト 4 枚と中央 1 枚のキャンバスを足したもの。値は `[SEED SCREEN]`（スクリプトの `SEED.Screen` と同じ値）。

| 条件 | 回転 | 値 |
|---|---|---|
| 内蔵の切り欠き（上 128） | 0 | `size=1080x2400 safe=(0,128,1080,2209) orientation=Portrait dpi=420`（上は穴、下はジェスチャーバー 63） |
| 〃 | 270 | `size=2400x1080 safe=(0,0,2272,1017) orientation=LandscapeRight`（穴は右、下はジェスチャーバー 63） |
| 〃 | 180 | `size=1080x2400 safe=(0,0,1080,2272) orientation=PortraitUpsideDown`（穴は下。ジェスチャーバーと重なって 128） |
| 〃 | 90 | `size=2400x1080 safe=(128,0,2272,1017) orientation=LandscapeLeft`（穴は左） |
| overlay `corner`（上 126） | 0 / 270 / 180 / 90 | `(0,126,1080,2211)` Portrait / `(0,0,2274,1017)` LandscapeRight / `(0,0,1080,2274)` PortraitUpsideDown / `(126,0,2274,1017)` LandscapeLeft |
| overlay `double`（上下 84） | 0 / 270 / 180 / 90 | `(0,84,1080,2232)` Portrait / `(84,0,2232,1017)` LandscapeRight / `(0,84,1080,2232)` PortraitUpsideDown / `(84,0,2232,1017)` LandscapeLeft |
| 解像度を固定（1920x1080） | 0 / 180 | `size=1920x1080 safe=(0,0,1920,1080)`（上下の黒帯が穴とジェスチャーバーを吸収） |
| 〃 | 270 / 90 | `size=1920x1080 safe=(0,0,1920,1017)`（左右の黒帯 240 が穴 128 を吸収。下の 63 は拡大率 1 なのでそのまま） |
| `screen_orientation=portrait` | 4 回回す | マニフェスト 7（sensorPortrait）。表示は `ROTATION_0` のまま、`Portrait` のまま |
| `screen_orientation=landscape` | 4 回回す | マニフェスト 6（sensorLandscape）。縦の端末で起動しても横（`ROTATION_90`）で始まり、90 と 270 だけを行き来（LandscapeLeft ↔ LandscapeRight、穴の辺も追従） |
| 既定（`both`）・不明な値 `sideways` | — | マニフェスト 10（fullSensor）。`sideways` は Gradle が警告して both 扱い |
| `-ProjectDir`（`.seedproj` の `assets_dir=Content`、値 `"  Landscape "`） | — | SeedPak と同じ `Content` から読み、`landscape` → マニフェスト 6 |
| 回転への追従 | 4 方向 | モデルが歪まない（カメラのアスペクト）。キャンバスの四隅のスプライトが四隅へ付き直す。`input tap 2300 150`（横）→ `Began(2300.0,150.0)`、`980 150`（縦）→ `Began(980.0,150.0)`。解像度を固定では `2300 150` → `(2060.0,150.0)`（黒帯の上＝枠外。レターボックスの写像） |

- 縦の画面ではキャンバスの自動スケール（`auto_scale`）が縦横別々に掛かるため、1920x1080 基準で作った正方形のスプライトは縦長に伸びる
  （既存の仕様。レイアウトの付き直し自体は正しい。backlog）。
- 回転の直後、`report=none`（全画面・縦横比の向き）のフレームが 1 回出ることがある（§15.3。270 → 0・180 → 90 のときに 1 回ずつ観測）。

PC（Windows・`SEED.exe` 単体の Play ＋ 確認用スクリプト）: 起動時 540x960 の窓で `size=540x960 safe=(0,0,540,960) orientation=Portrait dpi=96`、
窓を 1264x721 → 624x1001 → 784x761 に変えると `LandscapeLeft` → `Portrait` → `LandscapeLeft`（`Width` / `Height` も窓のクライアント領域に追従）。
同じフレームに 2 回読んだ値はすべて一致（`stable=True`）。解像度を固定（540x960）では窓を変えても `size=540x960` のままで、`Orientation` だけが窓の縦横比で変わった。

実機（Pixel 6a・Android 16・1080x2400・420 dpi・ジェスチャーナビゲーション）は 2026-09-24 の追加確認で、回転を
`cmd window fixed-to-user-rotation enabled` ＋ `cmd window user-rotation lock 0〜3` で強制して確かめた（検証前の `default` / `lock 0` へ戻した）。
実際のカメラ穴は 132 px で、穴のある辺だけが内側へ寄る（どの向きでもジェスチャーバーの辺は 63 px。180 度だけは穴とバーが同じ辺で 132 px）。

| 回転 | 値（`[SEED SCREEN]`） |
|---|---|
| 0 | `size=1080x2400 safe=(0,132,1080,2205) orientation=Portrait dpi=420`（Java 報告の内訳 `cutout=(0,132,0,0)`） |
| 90 | `size=2400x1080 safe=(132,0,2268,1017) orientation=LandscapeLeft`（`cutout=(132,0,0,0)`） |
| 180 | `size=1080x2400 safe=(0,0,1080,2268) orientation=PortraitUpsideDown`（`cutout=(0,0,0,132)`） |
| 270 | `size=2400x1080 safe=(0,0,2268,1017) orientation=LandscapeRight`（`cutout=(0,0,132,0)`） |

### 15.6 制限・持ち越し

詳細と持ち越し先は [backlog.md](backlog.md) の「Android」節。

- `SEED.Screen` の C# 側は、段階B（§17）でスクリプトが動くようになってから Android でも確かめた（エミュレータ・実機の縦画面で
  `SafeArea`・`Orientation`・`DPI`。§17.11）。回転中の値はログ（`[SEED SCREEN]`）でだけ確かめている。
- 安全領域をキャンバス UI へ自動で反映する仕組み（セーフエリアのパディング・アンカー）は無い。スクリプトが `SafeArea` を読んで配置する。
- 回転の直後の 1 フレーム程度は、安全領域が全画面・向きが縦横比からの値になることがある。
- 端末の回転ロックは尊重しない（`fullSensor` / `sensorPortrait` / `sensorLandscape`。`fullUser` 等の選択肢は無い）。
- 実機（Pixel 6a の実際のカメラ穴）は 2026-09-24 に確認済み（§15.5）。分割画面（描画面が表示の一部になる）・自然な向きが横のタブレットは未確認。
- `Screen.DPI` は OS の論理 DPI（Android は密度の区分値 densityDpi）で、物理的な DPI（xdpi / ydpi）ではない。

---

## 16. 音声（段階A-5・2026-09-24）

実際に音が鳴ることを確かめ、背面へ回ったときの停止と、Android の音声フォーカス（他のアプリとの音の譲り合い）・音量キーに対応した。
デスクトップの振る舞いは変えていない（出力ストリームを自前で開くようにしたが、開き方と出力される値は rodio と同じ。§16.1）。
スクリプトの音声 API の説明は [scripting_api.md](scripting_api.md) §6.7。

### 16.1 経路

```
AudioManager（core/audio/mod.rs）   BGM・効果音・AudioComponent の Sink を管理（従来どおり）
  └ AudioOutput（core/audio/output/）  rodio のミキサー（dynamic_mixer）＋ cpal の出力ストリーム ＋ 全体音量
      └ cpal 0.15 → oboe 1.8.1 → AAudio（Android）/ WASAPI（Windows）
```

- **rodio 0.20 の `OutputStream` をやめ、cpal のストリームを自分で開いて持つ**（`output/stream_builder.rs`）。`OutputStream` は中の
  `cpal::Stream` を公開しておらず、ストリームを一時停止できないため（Sink を全部 pause しても、オーディオスレッドは無音を作り続け、
  AAudio のストリームは「再生中」のまま音声経路を起こし続ける）。開く手順・既定の設定・対応する出力形式は rodio 0.20.1 の
  `OutputStream::try_default` と同じ（既定デバイスの既定設定 → 対応設定を優先順に → 他のデバイス）。Sink は `Sink::new_idle` で作って
  ミキサーへ足す（rodio の `Sink::try_new` と同じつなぎ方）。
- 違いは 3 つだけ: (1) ストリームを一時停止・再開できる、(2) 出力の全サンプルに全体音量の倍率を掛ける（倍率 1.0 では値がビット単位で
  変わらない。単体テストで固定）、(3) 符号無し整数形式の無音をその形式の中央値にした（rodio は MAX / 2。Windows の WASAPI 共有モード
  （通常 f32）と Android の oboe（i16 / f32 だけ）では使われない形式）。
- AudioComponent の左右の振り分け（rodio の `SpatialSink`）は、`OutputStreamHandle` からしか作れないため同じ仕組みを
  `core/audio/spatial_voice.rs` に移した（rodio の `Spatial` ソースと 10 ms ごとの位置の読み直し）。
- Android で開けた設定は、エミュレータ・実機とも **2ch・44100 Hz・F32**（`[SEED AUDIO] 出力ストリームを開きました`）。AAudio の player は
  `usage=USAGE_MEDIA`（cpal / oboe の既定。cpal からは変えられない）で、音量はメディアの音量（STREAM_MUSIC）に属する。

| ファイル | 役割 |
|---|---|
| `runtime/src/engine/core/audio/output/mod.rs` | `AudioOutput`: 出力ストリーム・ミキサー・全体音量。`new_sink` / `set_paused` / `set_gain` |
| `runtime/src/engine/core/audio/output/stream_builder.rs` | cpal のストリームとミキサーを開く（rodio の `try_default` と同じ手順） |
| `runtime/src/engine/core/audio/output/gain.rs` | 全体音量の倍率（原子変数）と出力バッファの書き込み（倍率の変化は 1 バッファかけて直線で移す） |
| `runtime/src/engine/core/audio/output_policy.rs` | 背面・音声フォーカスから「出力を止めるか・全体音量」を決める純関数 |
| `runtime/src/engine/core/audio/spatial_voice.rs` | rodio の `SpatialSink` と同じ仕組みの音源（AudioComponent 用） |
| `runtime/src/engine/platform/audio_focus.rs` | OS の音声フォーカスの最新の報告（JNI が書き、App が読む） |
| `runtime/src/engine/core/app_base/app/audio_output_sync.rs` | 条件を集めて AudioManager へ当てる（変わったときだけ切り替えてログ） |
| `runtime/android/app/src/main/java/com/seedengine/runtime/AudioFocusController.java` | 音声フォーカスの要求・放棄と変化のネイティブへの通知 |
| `runtime/android/native/src/jni_exports.rs` | `nativeOnAudioFocusChanged`（状態の番号 → `platform::audio_focus`） |

### 16.2 背面での停止

- `suspended`（背面へ回る）→ `enter_background` の 4 段目で**出力ストリームごと一時停止**する（AAudio の requestPause。オーディオスレッドの
  コールバックも止まり、どの音の再生位置も進まない）。2 回目以降の `resumed` → `enter_foreground` で再開する（音声フォーカスを失ったままなら止めたまま）。
- 実際には、それより前に `MainActivity.onPause` で音声フォーカスを手放した時点（§16.3）で止まる（ホームへ戻す操作では suspended より 0.3〜0.5 秒早い）。
- **ゲーム側の一時停止とは独立**。出力ストリームだけを止め、Sink ごとの状態（スクリプトの `PauseBgm` 等）には触らないので、
  止めていた BGM が再開で鳴り出す・鳴っていた BGM が止まったまま、は起きない（単体テスト `output_resume_keeps_bgm_paused_by_game`）。
- 止めている間に鳴らそうとした**単発の音（`Audio.Play` の効果音・ループしない AudioComponent）は捨てる**。積んでおくと、再開した瞬間に
  溜まった分がまとめて鳴るため（効果音はその瞬間の音）。AudioComponent の自動再生は「発火済み」として記録する。BGM とループ音は止めたまま積み、
  再開で続きから鳴る。前面で音声フォーカスを失っている間（ゲームは動き続ける）に効く決まりで、背面ではゲーム自体が止まっている。
- デスクトップは背面にも音声フォーカスの喪失にもならないので、出力は止まらない（毎周回、原子変数を 2 つ読むだけ）。

### 16.3 音声フォーカス

`AudioFocusController.java` が **前面に来たとき（onResume）に要求し、前面を離れるとき（onPause）に手放す**。要求は
`AUDIOFOCUS_GAIN`・`USAGE_GAME` / `CONTENT_TYPE_MUSIC`・遅延を許す（`setAcceptsDelayedFocusGain`）・ダッキングの通知を受ける
（`setWillPauseWhenDucked(true)`）。結果と OS からの変化は状態の番号で JNI へ渡し（番号は `AudioFocusController.STATE_*` と
`AudioFocus::from_code` の 2 か所で一致させる）、エンジンは**イベントループの 1 周ごと**（`about_to_wait`）に読んで出力へ当てる。

| Android の値 | 状態（`platform::audio_focus::AudioFocus`） | 出力 | 例 |
|---|---|---|---|
| 要求が通った / `AUDIOFOCUS_GAIN` | `Gained` | 鳴らす（全体音量 ×1.0） | 通常・一時的な喪失からの回復 |
| `AUDIOFOCUS_LOSS_TRANSIENT_CAN_DUCK` | `Ducked` | 全体音量 ×0.2（約 -14 dB。止めない） | 通知音 |
| `AUDIOFOCUS_LOSS_TRANSIENT` / 要求が保留（`REQUEST_DELAYED`） | `LostTransient` | 一時停止（`GAIN` で再開） | 着信の呼び出し音・通話中に前面へ戻った |
| `AUDIOFOCUS_LOSS` / 要求が拒否 | `Lost` | 一時停止（OS は要求を捨てるので `GAIN` は来ない。次の onResume で要求し直す） | 他のアプリが音楽の再生を始めた |
| 自分で手放した（onPause） | `Released` | 一時停止 | ホーム・他のアプリの画面が上に来た |

- 背面と音声フォーカスは独立に数え、**どちらか一方でも止める理由があれば止める**（`output_policy.rs` の `decide`。表を単体テストで固定）。
- ダッキングを OS に任せないのは、OS の自動ダッキングは再生の種類や端末で効き方が変わり得るため（AAudio の再生は自動ダッキングの
  対象外とされるが、ここでは確かめていない）。`setWillPauseWhenDucked(true)` で必ず通知を受け、自分で全体音量を下げる。
  エミュレータでは OS 側の「ducked players」は空のまま、こちらの全体音量だけが下がった。
- 反映を**フレームではなくイベントループの周回**にしたのは、ホームへ戻る途中などで描画（RedrawRequested）が止まってもループは回り続けるため。
  フレームで反映していた版では、手放した音声フォーカスの反映が suspended まで約 1.1 秒遅れ、その間も鳴り続けた（エミュレータ）。
- ログは Java の行（`[SEED AUDIO] Java: 音声フォーカス: …`。Android の値そのもの）と、ネイティブの行（受け取った状態・出力の切り替え）の 2 段。
  `MediaFocusControl` タグ（システム）にも要求・放棄が出る（§6）。

### 16.4 音量キー

GameActivity は音量キーをネイティブへ渡さずシステムへ回す（既定のキーフィルタ。§10）。`MainActivity.onCreate` で
`setVolumeControlStream(AudioManager.STREAM_MUSIC)` を呼び、音量キーの対象をメディアの音量に固定した。指定しないと対象は
「その時に鳴っているストリーム（無ければ端末の既定）」になり（エミュレータの記録では `sugg:USE_DEFAULT_STREAM_TYPE`）、端末によっては
何も鳴っていない瞬間に着信音量が変わる。指定後は `adjustSuggestedStreamVolume(sugg:STREAM_MUSIC …)` になる。

### 16.5 確認方法

```bash
# 可聴のテスト音源: 2 秒ループの中に 150 ms の小さな 440 Hz（振幅 0.25）を 1 回。AudioComponent（自動再生・ループ）で鳴らす
#   （作り方の例: Python の wave で 44.1 kHz / 16 bit / モノラルを書く。音量はコンポーネントの volume で絞る）
pwsh -File runtime/android/build_and_run.ps1 -Abi x86_64 -Serial emulator-5554 -AssetsDir <テスト音源入りのアセット> -NoLogcat

# 再生中か（player の状態・アクティブなトラック）
adb shell dumpsys audio | grep "AudioPlaybackConfiguration .*u/pid:<uid>/"      # state:started / paused
adb shell dumpsys media.audio_flinger                                            # Tracks の Active=yes / no・S=A / P、Standby
adb shell cmd package list packages -U com.seedengine.runtime                   # uid

# 背面: ホーム → [SEED AUDIO] 音声を一時停止しました → am start で戻す → 音声を再開しました
adb shell input keyevent KEYCODE_HOME
# オーディオスレッド（AudioTrack）の CPU 時間: run-as で /proc/<pid>/task/*/stat の utime+stime を 10 秒空けて 2 回読む（§14.6 と同じ）

# 音声フォーカス（エミュレータだけ。実機では他のアプリを操作しない）
adb shell cmd notification post -t "SEED" seed_duck "duck test"   # 通知音 → LOSS_TRANSIENT_CAN_DUCK → 全体音量 ×0.20 → GAIN で ×1.00
adb emu gsm call 5550100 ; adb emu gsm cancel 5550100             # 着信の呼び出し音 → LOSS_TRANSIENT → 一時停止 → GAIN で再開
adb shell dumpsys audio | sed -n '/focus commands as seen by MediaFocusControl/,/^$/p'   # 要求・放棄の記録
#   後片付け: 通知（シェードの「すべて消去」）・通話履歴（content delete --uri content://call_log/calls）を消す

# 音量キー（エミュレータだけ。終わったら元の値へ。何も鳴っていないときは最初の 1 回が UI 表示だけになる）
adb shell input keyevent KEYCODE_VOLUME_UP ; adb shell cmd media_session volume --stream 3 --get
```

- ホスト PC 側で「本当に音が出ているか」を見るには、Windows の Core Audio（`IAudioMeterInformation`）でエミュレータ（`qemu-system-x86_64`）の
  音声セッションのピーク値を読む（PowerShell の Add-Type で C# から呼べる。追加のインストールは要らない）。
- エミュレータの共有 API 35 イメージでは `cmd media_session volume --set` の音量設定が反映されなかった（音量を戻すときは音量キーを使う）。

### 16.6 確認結果（2026-09-24）

エミュレータ（AVD `seed_pixel6_api35`・API 35・x86_64）・実機（Pixel 6a・Android 16・arm64）・PC（Windows・`SEED.exe` 単体の Play）。
アセットは最小構成に §16.5 のテスト音源（AudioComponent・自動再生・ループ）を足したもの（音量はエミュレータ 0.5・実機 0.1・PC 0.05）。

| 項目 | エミュレータ | 実機 Pixel 6a |
|---|---|---|
| 鳴ること | 出力ストリーム 2ch・44100 Hz・F32。`dumpsys audio` の player `type:AAudio … state:started`。audio_flinger のトラックが `Active=yes`（float・44100 Hz・STREAM_MUSIC）・出力スレッド `Standby: no`。ホスト PC 側のエミュレータの音声セッションのピーク値が 2 秒ごとに約 0.0021（ビープ）、それ以外は約 0.00007 | 同じ設定。player `state:started`（`deviceIds:[3]`）、audio_flinger のトラック `Active=yes`（`S=A`・float・44100 Hz） |
| ホーム（背面） | onPause で音声フォーカスを手放した 12 ms 後に「音声を一時停止しました」（suspended より 0.5 秒早い）。player `state:paused`、トラック `Active=no`・`S=P`、出力スレッドが `Standby: yes`。ホスト側のピーク値は 0.000000 | 手放した 1 ms 後に一時停止。player `state:paused`、トラック `Active=no`・`S=P` |
| オーディオスレッド（`AudioTrack`）の CPU 時間（10 秒あたり） | 前面 68 tick（0.68 秒）→ 背面 0 tick | 前面 336 tick（3.36 秒。debug ビルド）→ 背面 0 tick |
| 前面へ戻す | 要求が通る → 「音声を再開しました（全体音量 ×1.00…）」→ `state:started` | 同じ（物理スレッドも「止めていた時間 27.0 秒」で再開） |
| 通知音（ダッキング） | SystemUI が `req=3`（GAIN_TRANSIENT_MAY_DUCK）→ `LOSS_TRANSIENT_CAN_DUCK` → 30 ms 後に「全体音量を ×0.20 にしました」→ 通知音の放棄 → `GAIN` → 16 ms 後に ×1.00。OS の「ducked players」は空 | 未実施（私物の端末で他のアプリを動かさない） |
| 着信（一時的な喪失） | `adb emu gsm call`: Telecom が `req=2`（GAIN_TRANSIENT・`AudioFocus_For_Phone_Ring_And_Calls`）→ `LOSS_TRANSIENT` → 一時停止（着信は通知で表示され SEED は前面のまま・player `state:paused`）→ `gsm cancel` → `GAIN` → 再開 | 未実施（同上） |
| 他のアプリの画面が上に来る | YouTube Music の音声プレビュー（`req=2`）: SEED の onPause で手放して一時停止 → 閉じると onResume で要求 → 再開 | 未実施（同上） |
| 恒久的な喪失（`AUDIOFOCUS_LOSS`） | **起こせなかった**（SEED が前面のまま他のアプリに `AUDIOFOCUS_GAIN` を要求させる手段が無い。分割画面は片側が空で解除され、プレビューは一時的な要求だけ）。状態の扱いは単体テストで固定 | 未実施 |
| 音量キー | `adjustSuggestedStreamVolume(sugg:STREAM_MUSIC …)`、ゲームの音が鳴っている間は最初の押下から効く（5 → 6 → 5 に戻した）。変更前は `sugg:USE_DEFAULT_STREAM_TYPE` で、何も鳴っていないときの最初の押下は UI 表示だけ | 未実施（音量を変えない） |

- PC: `cargo test` の出力の単体テスト（`output_pause_freezes_playback_and_resume_continues` 等。WASAPI の出力を実際に開いて一時停止・再開する）が通過。
  `SEED.exe` 単体の Play で同じテスト音源（音量 0.05）を鳴らし、Windows 側の `SEED` の音声セッションのピーク値が 2 秒ごとに約 0.0086。
  起動ログに音声の初期化の失敗は無く、`[SEED AUDIO]` の行も出ない（デスクトップのログは従来どおり）。

### 16.7 制限・持ち越し

詳細と持ち越し先は [backlog.md](backlog.md) の「Android」節。

- **出力デバイスの切り替え（ヘッドホンの抜き差し・Bluetooth）からの復帰が無い**（未確認）。cpal 0.15 の oboe はストリームの切断を
  エラーの通知で知らせるだけで開き直さない。ミキサーを残したまま出力ストリームだけを開き直す仕組みが要る（`AudioOutput` に閉じた変更で済む形にしてある）。
- 恒久的な喪失（`AUDIOFOCUS_LOSS`）はエミュレータで起こせず、実機では他のアプリを操作しないため、端末の上での確認は未実施（状態の扱いは単体テストのみ）。
- 実機での音声フォーカスの喪失・ダッキング・音量キーは未実施（私物の端末で他のアプリを動かさない・音量を変えないため）。
- AAudio の player は `USAGE_MEDIA`・性能モードは既定（低遅延ではない）のまま（cpal 0.15 から指定できない）。音声フォーカスの要求は `USAGE_GAME`。
- debug ビルドの実機では、ミキサーとデコードのオーディオスレッドが 1 コアの約 34% を使った（エミュレータは約 7%）。音声系クレート
  （rodio・cpal・symphonia 等）は dev プロファイルで最適化されていない。
- ダッキングの下げ幅（×0.2）は定数（`output_policy::DUCKED_GAIN`）。プロジェクト設定にはしていない。

---

## 17. C# スクリプトの実行（段階B・2026-09-25）

APK に .NET 10 の CoreCLR（Android 版）を同梱し、PC と同じスクリプトホスト（`SEEDScripting.dll`）と事前コンパイル DLL
（`SEEDUserScripts.dll`。ScriptPackager / SeedPak）で C# スクリプトを動かす。スクリプト側の変更は要らない（PC と同じ DLL がそのまま動く）。
構成は §11 のスパイクの結論どおり（**CoreCLR を採用し、Mono は切り替えられる逃げ道として残す**）。PC の起動経路（nethost・パス指定）は変えていない。

### 17.1 全体の流れ

```
ビルド（SeedAndroid。editor/src/Android/。§4.6）
  pak とスクリプト（Steps/PackageContentStep）… SeedPak --scripts で assets.pak と bin/（SEEDUserScripts.dll・SEEDScripting.dll・
          runtimeconfig・依存 DLL）→ app/src/main/assets/seed/
  同梱 .NET（Steps/DotnetBundleStep → Dotnet/DotnetRuntimeBundle）… dotnet_runtime.json の版・パックを NuGet から取り寄せ（~/.nuget/packages）、
          今回の ABI ごとに組み立てる
          .so（hostfxr・hostpolicy・coreclr・clrjit・System.*.Native）→ app/src/seedDotnet/jniLibs/<ABI>/（APK の lib/<ABI>/）
          BCL の DLL・deps.json・runtimeconfig・目録 bundle.json → app/src/seedDotnet/assets/seed/dotnet/<ABI>/
          暗号ライブラリの Java 側（.jar）→ app/src/seedDotnet/libs/（APK の Java クラス）
  APK（Steps/GradleBuildStep）… Gradle（useLegacyPackaging = true。.so をインストール時に nativeLibraryDir へ展開させる）
端末
  MainActivity の static 初期化: System.loadLibrary("SEED") → DotnetJniLibraries（暗号ライブラリを System.loadLibrary。JNI_OnLoad。§17.8）
  MainActivity.onCreate: 環境変数 TMPDIR / HOME / DOTNET_EnableDiagnostics=0（§17.6）
  android_main → launch::launch_args → dotnet_runtime::prepare（runtime/android/native/src/dotnet_runtime/）
      1. APK の assets/seed/dotnet/<ABI>/bundle.json を読む（無ければ「.NET の無い APK」としてスクリプト無し）
      2. スクリプトの DLL の置き場を選ぶ（files/bin → 外部の files/bin → APK の bin/。SEEDScripting.dll がある最初の置き場。
         どこにも無ければ .NET を展開せずにスクリプト無し）
      3. files/dotnet/<種類>-<版>-<content_id>/ へ展開（初回。BCL は APK の assets から複製、.so は nativeLibraryDir へのシンボリックリンク）
         2 回目以降は印（.seed_bundle_complete）と asset の大きさを確かめて使い回し、.so のリンクだけ確かめて直す
      4. その置き場の SEEDScripting.runtimeconfig.json を展開先の app/ へ写す（hostfxr にはファイルのパスが要る）
      → LaunchArgs.embedded_clr（EmbeddedClrHost）
  App::new → app/script_boot.rs → ScriptingHost::load_embedded（runtime/src/engine/core/scripting/clr_host/embedded.rs）
      mallopt（ヒープのタグ付けの無効化。§17.5）→ Hostfxr::load_from_path → initialize_for_runtime_config_with_dotnet_root
      → set_runtime_property_value（System.Globalization.Invariant=true）→ get_delegate_loader（ここで CLR が起動）
      → load_assembly_from_bytes（SEEDScripting.dll を Default の AssemblyLoadContext へ）→ エントリポイントの取り出し（PC と共通）
  → install_host_api → load_precompiled_scripts_from_bytes（SEEDUserScripts.dll。C# の ScriptBridge.LoadPrecompiledScriptsFromBytes）
  以降のスクリプトの実行（CreateComponent・OnStart・Update …）は PC と同じ経路
```

| 層 | ファイル | 役割 |
|---|---|---|
| 設定 | `runtime/android/dotnet_runtime.json` | 版・パック名・coreclr / mono・.so の置き方・ランタイムプロパティ（唯一の置き場。§17.2） |
| 組み立て | `editor/src/Android/Dotnet/`（`DotnetRuntimeSettings`・`NuGetRuntimePackRestorer`・`DotnetRuntimeBundle`）と `Steps/DotnetBundleStep` | NuGet から取り寄せ、ABI ごとに jniLibs / assets / 目録を作る（単体テスト `AndroidPipelineTests`）。DLL の差し替えは `Steps/PushScriptsStep`（`push`） |
| 目録と展開 | `runtime/src/engine/core/scripting/embedded_runtime/`（`manifest.rs`・`install.rs`） | bundle.json の検査と files/dotnet/ への展開・使い回し・修復（単体テスト付き） |
| DLL の置き場 | `runtime/src/engine/core/scripting/script_binaries.rs` | `ScriptBinarySource`（フォルダ / 配布物の bin/）と選び方の純関数（単体テスト付き） |
| CLR の起動 | `runtime/src/engine/core/scripting/clr_host/`（`embedded.rs`・`desktop.rs`・`entry_points.rs`・`heap_tagging.rs`） | 同梱 .NET と PC の 2 経路。関数ポインタの取り出しは共通（`entry_points.rs`） |
| 起動時の分岐 | `runtime/src/engine/core/app_base/app/script_boot.rs`・`platform/mod.rs`（`script_host_source`） | 起動材料の有無とプラットフォームの特性でスクリプトホストとユーザースクリプトの読み方を決める |
| Android の糊 | `runtime/android/native/src/dotnet_runtime/`（`mod.rs`・`abi.rs`・`native_library_dir.rs`・`script_sources.rs`） | ABI・nativeLibraryDir（dladdr）・DLL の置き場の候補を集めて起動材料を作る |
| Java | `DotnetJniLibraries.java`・`MainActivity.java` | 暗号ライブラリの System.loadLibrary・環境変数 |
| C# | `scripting/src/ScriptBridge.cs`（`LoadPrecompiledScriptsFromBytes`）・`ScriptAssemblyManager.cs`（`LoadPrecompiledBytes`）・`Compilation/ScriptAssemblyEmitter.cs` | バイト列からの読み込みと、Roslyn に触れるコードの分離（§17.9） |
| DLL を作る | `editor/tools/SeedPak`（`--scripts` / `--scripts-only`） | パッケージ化ウィンドウと同じ ScriptPackager で bin/ を作る（[packaging.md](packaging.md) §10.2） |

### 17.2 設定（`runtime/android/dotnet_runtime.json`）

| キー | 既定 | 意味 |
|---|---|---|
| `dotnet_runtime` | `coreclr` | `coreclr` / `mono`（§17.9）。`runtimes` の中の同名の設定を使う |
| `version` / `framework` / `target_framework` | `10.0.12` / `Microsoft.NETCore.App` / `net10.0` | パックの版と、dotnet-root の中のフォルダ名（`shared/<framework>/<version>/`） |
| `abis` | `arm64-v8a→arm64`・`x86_64→x64` | Android の ABI → パック名の `{arch}` |
| `runtimes.<種類>.runtime_pack` / `runtime_rid` | CoreCLR: `Microsoft.NETCore.App.Runtime.android-{arch}` | BCL・`libcoreclr.so` 等の出どころ（パックの `runtimes/<RID>/lib/<TFM>/`・`native/`） |
| `runtimes.<種類>.host_pack` / `host_rid` | `Microsoft.NETCore.App.Runtime.linux-bionic-{arch}` | `libhostfxr.so`・`libhostpolicy.so` の出どころ（android パックには無い） |
| `runtimes.<種類>.excluded_native_files` | CoreCLR: `libmscordaccore.so`・`libmscordbi.so` | 入れない .so（デバッガ用）。`.a`・`.dex`・`.jar` はそもそも .so / .dll でないので入らない |
| `runtimes.<種類>.java_libraries` | CoreCLR: 暗号ライブラリの `.jar` | APK の Java クラスへ入れる .jar（§17.8。Mono は無し） |
| `host_libraries` | hostfxr → `host/fxr/{version}`、hostpolicy → `shared/{framework}/{version}` | host_pack の .so の dotnet-root 内の置き場 |
| `native_library_mode` | `symlink` | .so を dotnet-root へ置く方法（`symlink` / `copy`。§17.4） |
| `runtime_properties` | `System.Globalization.Invariant=true` | CLR の起動前に hostfxr へ設定するプロパティ（§17.6） |

- 値を変えたら APK を作り直すだけでよい（ランタイムのコードは APK の目録 `bundle.json` を読む）。
- 目録の `content_id` は「目録の書式の版・ABI・設定ファイルの文字列・組み立て方の版（`DotnetRuntimeBundle.AssemblerRevision`）」の
  ハッシュ（先頭 16 桁）。同じなら組み立てを省き（`変更なし（content_id=…）`）、端末も展開を使い回す。NuGet のパックは版ごとに中身が
  変わらないため、パックの中身は材料に入れていない。組み立て方（出力の中身）を変えたら `AssemblerRevision` を上げる。
  段階B まではスクリプト（build_and_run.ps1）の文字列を材料にしていたため、C# へ移した最初のビルドで content_id が変わり、端末は 1 回だけ展開し直す。

### 17.3 APK 内のレイアウトと端末上の展開

```
APK
  lib/<ABI>/libSEED.so, libhostfxr.so, libhostpolicy.so, libcoreclr.so, libclrjit.so, libSystem.Native.so,
            libSystem.Globalization.Native.so, libSystem.IO.Compression.Native.so, libSystem.Security.Cryptography.Native.Android.so
  assets/seed/assets.pak                                      … §13
  assets/seed/bin/SEEDScripting.dll ほか                       … SeedPak --scripts（PC の配布物の bin/ と同じ中身）
  assets/seed/dotnet/<ABI>/bundle.json                        … 目録（ファイル一覧・版・content_id・プロパティ）
  assets/seed/dotnet/<ABI>/shared/Microsoft.NETCore.App/<版>/*.dll, Microsoft.NETCore.App.deps.json, .runtimeconfig.json
  classes.dex の net.dot.android.crypto.*                      … 暗号ライブラリの .jar（§17.8）

端末（/data/user/0/com.seedengine.runtime/）
  files/dotnet/coreclr-10.0.12-<content_id>/                  … dotnet-root（hostfxr へ渡すフォルダ）
    host/fxr/10.0.12/libhostfxr.so            → nativeLibraryDir/libhostfxr.so（シンボリックリンク）
    shared/Microsoft.NETCore.App/10.0.12/*.dll                … APK の assets から複製
    shared/Microsoft.NETCore.App/10.0.12/lib*.so → nativeLibraryDir/lib*.so
    app/SEEDScripting.runtimeconfig.json                      … スクリプトの置き場から毎回写す（中身が同じなら書かない）
    .seed_bundle_complete                                     … 展開の完了の印（中身は content_id。最後に書く）
  files/bin/                                                  … -PushScripts の置き場（§17.7）
```

- deps.json は、パックのものの `native` の一覧を「実際に入れた .so」だけに絞って書き直す（.a・.jar・デバッガ用 .so を載せない）。
  `runtime`（BCL）は全部入れるのでそのまま。Mono は `System.Private.CoreLib.dll` がパックの `native/` にあるので BCL と一緒に入れる。
- 展開の途中でプロセスが殺されても、印が無いので次回は最初から展開し直す。中身の違う古い版（別の content_id）のフォルダは展開の後に消す。
- 2 回目以降は印と asset（全ファイルの有無と大きさ）を確かめるだけ（実機で 4〜77 ms）。**アプリを入れ直すと nativeLibraryDir の場所
  （`/data/app/~~<乱数>/…`）が変わりシンボリックリンクが切れる**が、.so だけを張り直す（BCL は展開し直さない。実機で 8 本を張り直し 4.2 ms）。
- runtimeconfig を DLL の置き場からそのまま渡さないのは、hostfxr がそのフォルダを「アプリのフォルダ」として扱い、並んだ DLL
  （SEEDScripting.dll 等）を TPA に載せて、バイト列から読む SEEDScripting と二重になるため。空のフォルダ（`app/`）へ写してから渡す。
- 2 ABI 入りの APK では、assets（BCL）は両方の ABI のものが入る（lib/ と違って ABI で分けられない）。配布は arm64 だけの想定（§2）。

### 17.4 .so の見せ方（シンボリックリンクと複製。実機で両方を確かめてシンボリックリンクを既定にした）

hostfxr / hostpolicy / CoreCLR は dotnet-root 形式のフォルダに .so が並んでいることを前提にする。.so は APK の `lib/<ABI>/` に入れ、
`useLegacyPackaging = true` でインストール時に nativeLibraryDir へ展開させたうえで、dotnet-root から次のどちらかで参照する。

| | `symlink`（既定） | `copy` |
|---|---|---|
| 実機 Pixel 6a・エミュレータで CLR が起動しスクリプトが動くか | 動く | 動く |
| 展開の所要時間（実機・初回） | 456〜839 ms | 484 ms |
| .so の容量 | 増えない | 1 ABI あたり約 12 MB 増える |
| .so を実行する場所 | nativeLibraryDir（OS が展開した `apk_data_file`） | アプリのデータフォルダ（`app_data_file`。SELinux の auditallow の対象） |
| tombstone のバックトレース | 関数名が出る | 出ない（debuggerd は `lib/` の下しか読めない） |
| スクリプトの暗号 API（§17.8） | **動く**（Java が JNI_OnLoad した実体と同じファイル） | **プロセスごと落ちる**（CLR が別の実体を読み、初期化されていない。エミュレータで SIGSEGV を確認） |
| アプリの入れ直し | リンクが切れるので張り直す（install の修復） | 大きさが同じなら使い回す |

- 心配だった点（bionic の `dladdr` はシンボリックリンクを辿った実パスを返すので、CoreCLR が自分のフォルダ＝nativeLibraryDir から
  `System.Private.CoreLib.dll` を探すのではないか）は起きなかった。CoreLib は dotnet-root の framework フォルダから読まれた
  （SELinux の記録に `…/files/dotnet/…/System.Private.CoreLib.dll` の execute が出る）。hostfxr の自己位置からの dotnet-root の推定は
  当てにならないので、dotnet-root は常に明示する（`initialize_for_runtime_config_with_dotnet_root`）。
- 以上から既定は `symlink`。シンボリックリンクを許さない端末が見つかったときの逃げ道として `copy` を残した（`dotnet_runtime.json` の
  `native_library_mode` を変えて APK を作り直すだけ）。
- どちらの方式でも、BCL の R2R（事前コンパイル済みのネイティブコード入り DLL）はアプリのデータフォルダから実行可能として読み込まれる。
  SELinux は許可しつつ記録する（`avc: granted { execute } … tcontext=u:object_r:app_data_file`。ファイルごとに 1 行）。将来の Android で
  禁止されると、この方式（hostfxr ＋ ファイルの dotnet-root）は使えなくなる（backlog）。

### 17.5 ヒープポインタのタグ付け（実機 arm64 の必須対策）

§11.4 のとおり、arm64 の Android 11 以降は bionic がヒープポインタの上位バイトにタグ（0xB4）を付け、CoreCLR / Mono が
`coreclr_initialize` で SIGSEGV になる。2 つとも入れた。

1. `AndroidManifest.xml` の `<application android:allowNativeHeapPointerTagging="false">`（プロセスの開始時点から付かない。API 30 以降）
2. `clr_host/heap_tagging.rs`: CLR の起動直前（hostfxr を読み込む前）に `mallopt(M_BIONIC_SET_HEAP_TAGGING_LEVEL, M_HEAP_TAGGING_LEVEL_NONE)`
   （マニフェストの属性が無い APK でも起動できる保険。bionic の malloc.h は「いつでも・複数スレッドが動いていても呼んでよい」。API 31 以降）

実機 Pixel 6a（Android 16）で、`[SEED DOTNET] ヒープポインタのタグ付けを無効にしました（mallopt=1 …）` の後に CLR が起動した
（`aapt2 dump xmltree` で APK のマニフェストに `allowNativeHeapPointerTagging=false`・`extractNativeLibs=true` を確認）。

### 17.6 ランタイムプロパティ・環境変数

- `System.Globalization.Invariant=true` は `set_runtime_property_value` で設定する（runtimeconfig は PC と同じファイルなので書き換えない。
  Windows には影響しない）。アプリのプロセスからは端末の ICU（`/apex/com.android.i18n`）を読めないため。`CultureInfo("ja-JP")` 等は
  `CultureNotFoundException` になる（§11.1 のスパイクと同じ。スクリプトは文化に依存しない書式を使う）。
- runtimeconfig は `SEEDScripting.runtimeconfig.json`（net10.0・`Microsoft.NETCore.App 10.0.0`・`rollForward=LatestMinor`）をそのまま使う
  （10.0.12 へロールフォワード）。
- `MainActivity.onCreate`（ネイティブのスレッドが無いうち）に `DOTNET_EnableDiagnostics=0`（デバッガ・プロファイラ・EventPipe の待ち受けを
  止める）。`TMPDIR` / `HOME` は §14.1 のとおり（.NET の `Path.GetTempPath()` はキャッシュフォルダを返す）。

### 17.7 スクリプトの DLL の置き場と高速経路（`push`・ps1 の `-PushScripts`）

| 順 | 置き場 | 置き方 | 読めるか |
|---|---|---|---|
| 1 | 内部アプリ専用フォルダ `files/bin/` | `SeedAndroid push`・`run --push-scripts`（ps1 の `-PushScripts`。run-as ＋ tar。デバッグ版 APK だけ） | 実機・エミュレータとも読める |
| 2 | 外部アプリ専用フォルダ `/sdcard/Android/data/<pkg>/files/bin/` | 手で `adb push` | エミュレータは読める。**実機（Pixel 6a・Android 16）は読めない**（Permission denied。警告を出して飛ばす） |
| 3 | APK の `assets/seed/bin/` | `SeedAndroid run --project`（ps1 の `-ProjectDir`。SeedPak `--scripts`） | 読める（配布版と同じ形） |

- `SEEDScripting.dll` がある最初の置き場を使い、`SEEDUserScripts.dll` と runtimeconfig も同じ置き場から読む（版の違うホストと混ぜない）。
  選び方は `script_binaries::choose_binaries`（単体テスト付き）。ログの `[SEED DOTNET] スクリプトの置き場: …` で分かる。
- `push` は `--project`（無ければ `--assets-dir`）の .cs を SeedPak `--scripts-only` で事前コンパイルし、`bin/` の DLL と runtimeconfig を
  `files/bin/` へ送り（前回分は消してから）、force-stop して起動し直す。APK は作り直さない。SeedPak は `scripting/` も一緒にビルドする。
  tar は .NET の `TarWriter`（GNU 形式・0600 / 0700）で作って adb の標準入力へ直接書く（外部の tar・pwsh のパイプは使わない）。
- 差し替えを消すと APK の中のものへ戻る: `adb exec-out run-as com.seedengine.runtime rm -rf files/bin`
- 当初は外部アプリ専用フォルダへ `adb push` する形にしたが、実機では §4.5 と同じ理由（adb push が作ったフォルダは shell の所有）で
  アプリから読めなかったため、run-as の内部フォルダへ変えた。外部フォルダは手で置く場合の候補として残した（エミュレータでは使える）。
- Android ではその場コンパイル（.cs から）をしない（Roslyn の参照アセンブリが端末に無い）。`bin/` には PC と同じく Roslyn の DLL（約 9 MB）も
  入るが、Android では読み込まれない（backlog）。スクリプトのホットリロードは無い（DLL を差し替えて再起動する）。

### 17.8 暗号 API（JNI の初期化）

CoreCLR の暗号ライブラリ（`libSystem.Security.Cryptography.Native.Android.so`。`SHA256`・`RandomNumberGenerator`・TLS 等）は JNI で Java の
暗号 API を呼ぶ。`JNI_OnLoad`（中身は `AndroidCryptoNative_InitLibraryOnLoad`）で JavaVM を受け取り、パックの `.jar` のクラス
（`net.dot.android.crypto.DotnetProxyTrustManager`・`PalPbkdf2` 等）を `FindClass` で探し、**無ければ `abort()` する**。

- ネイティブのスレッドから `JNI_OnLoad` を呼ぶ案（`AndroidApp::vm_as_ptr()` の JavaVM を渡す）は採らなかった。`FindClass` がシステムの
  クラスローダーで探すため、APK のクラスが見えず必ず abort する。
- 採った方法: SeedAndroid（`DotnetRuntimeBundle.UpdateJavaLibraries`）がパックの `.jar` を `app/src/seedDotnet/libs/` へ置き、Gradle が APK の Java クラスへ入れる。
  `MainActivity` の static 初期化で `DotnetJniLibraries.loadAvailable()` が `System.loadLibrary("System.Security.Cryptography.Native.Android")`
  する（`JNI_OnLoad` はアプリのクラスローダーの文脈で呼ばれる）。.jar のクラスが APK に無い（Mono・.NET 無し）ときは読み込まない。
- CLR は dotnet-root の `libSystem.Security.Cryptography.Native.Android.so` を dlopen する。`symlink` では実体が nativeLibraryDir の同じファイルなので、
  bionic は Java が読み込んで初期化済みの実体を返す。`copy` では別の実体になり、`SHA256.HashData` で SIGSEGV（fault addr 0）になった（エミュレータ）。
- 確認: スクリプトの `SHA256.HashData("SEED")` が `DA5E401CB3FF5A14…`（PC の hashlib と一致）、`RandomNumberGenerator.GetBytes(4)` が 4 バイト
  （エミュレータ・実機とも）。TLS（`SslStream`・`HttpClient` の https）と X509 は未確認（backlog）。

### 17.9 Mono への切り替え

`dotnet_runtime.json` の `dotnet_runtime` を `mono` にして APK を作り直す（linux-bionic パックだけを使う。中身は Mono で、`libcoreclr.so` は
互換シム）。起動経路（hostfxr → バイト列 → Default ALC）は CoreCLR と同じ。

- エミュレータ（x86_64）で起動・スクリプトの実行（OnStart・null 参照 / 0 除算の例外・毎秒のログ）を確認した。所要時間は CLR の起動 791〜1350 ms・
  ユーザースクリプト 429 ms（CoreCLR の約 10 倍）。`OperatingSystem.IsAndroid()` は False、`Console` の出力は標準出力経由でタグ `SEED` に出る。
- **Mono で見つかった不具合と対処**: Mono は型の静的フィールドの型をクラスの読み込み時に解決する。`ScriptAssemblyManager` は事前コンパイル DLL を
  読むだけの処理と Roslyn を使う処理のラムダが同じ隠れクラス（`<>c`・`<>O`。`Func<Diagnostic, bool>` 等のフィールドを持つ）に入っていたため、
  読むだけの経路でも Roslyn（`Microsoft.CodeAnalysis`）を探しに行き `TypeLoadException` になった（CoreCLR は遅延して解決するので動いていた）。
  Roslyn を使うコードを `scripting/src/Compilation/ScriptAssemblyEmitter.cs` へ分け、`ScriptAssemblyManager` から Roslyn の型を無くして解消した
  （PC の挙動・公開 API は同じ。`ScriptPrecompileTests` 15 件と Windows の Play で確認）。
- 暗号 API は Mono では使えない（linux-bionic パックの暗号は OpenSSL 版で、端末の BoringSSL と合わない。§11.1）。`java_libraries` は空。
- 実機では未確認（CoreCLR の実機確認を優先した）。

### 17.10 ログと確認方法

| 行 | 中身 |
|---|---|
| `[SEED DOTNET] Java: System.Security.Cryptography.Native.Android を読み込みました（JNI_OnLoad 済み）` | 暗号ライブラリの読み込み（§17.8） |
| `[SEED DOTNET] .NET を展開しました: <dotnet-root>（182 ファイル・BCL 65.2 MiB・.so の置き方 Symlink・nativeLibraryDir=…・839.4 ms）` | 初回の展開。2 回目以降は `展開済みの .NET を使います: …（確認 N ms・182 ファイル・置き直した .so N 個）` |
| `[SEED DOTNET] スクリプトの置き場の候補: … — SEEDScripting.dll=あり / …` と `スクリプトの置き場: …` | §17.7 の選択 |
| `[SEED DOTNET] ヒープポインタのタグ付けを無効にしました（mallopt=1 …）` | §17.5 |
| `[SEED DOTNET] CLR を起動しました: coreclr 10.0.12（arm64-v8a）… hostfxr 読込 / 初期化 / ランタイム起動 / SEEDScripting 読込 / 関数の取り出し / 合計` | CLR の起動の所要時間 |
| `[SEED] precompiled scripts loaded: 1 type(s)  （<置き場>/SEEDUserScripts.dll・14 KiB・N ms）` | ユーザースクリプトの読み込み |
| タグ `DOTNET` の `[Script] …` | スクリプトの `SEED.Debug.Log`（CoreCLR） |

```bash
# ビルド → install → 起動 → 20 秒の logcat を UTF-8 で保存（ABI は端末から判定。実機は --serial <実機>）
dotnet run --project editor/tools/SeedAndroid -- run --project <プロジェクト> --serial emulator-5554 --logcat-seconds 20 --log-file log.txt
# DLL だけの差し替え（APK はそのまま）
dotnet run --project editor/tools/SeedAndroid -- push --project <プロジェクト> --serial emulator-5554 --logcat-seconds 20
# 展開の所要時間だけを測る（展開を消して起動し直す）
adb exec-out run-as com.seedengine.runtime sh -c 'rm -rf files/dotnet' ; adb shell am force-stop com.seedengine.runtime ; adb shell am start -n com.seedengine.runtime/.MainActivity
# 展開先を見る
adb exec-out run-as com.seedengine.runtime sh -c 'ls -l files/dotnet/*/shared/Microsoft.NETCore.App/10.0.12/ | head'
```

確認用のスクリプト（一時プロジェクト。リポジトリ外）は、起動時に .NET の版・RID・OS と、null 参照（SIGSEGV → `NullReferenceException`）・
整数の 0 除算・暗号 API（`[SerializeField] testCrypto`）を試し、毎秒 `Time`・`Input.TouchCount`・`Screen.SafeArea`・`Screen.Orientation` を
ログへ出し、触れ始めるたびにモデルの大きさを切り替え、指の横移動でモデルを回す。

### 17.11 確認結果（2026-09-25）

debug ビルド。アセットは最小構成（§7）に確認用のスクリプトを足したもの。

| 項目 | エミュレータ（x86_64・API 35） | 実機 Pixel 6a（arm64・Android 16） |
|---|---|---|
| 同梱 .NET の大きさ（1 ABI） | BCL 等 174 ファイル 57.9 MB ＋ .so 8 個 12.1 MB | BCL 等 174 ファイル 65.2 MB ＋ .so 8 個 11.7 MB |
| APK（1 ABI・pak 3.1 MB・bin 9.1 MB 込み） | 63.9 MB | 60.5 MB（`adb install -r` 4.3 秒） |
| 初回の展開 | 475〜506 ms（展開を消して 3 回。インストール直後の 1 回目は 5.6 秒＝インストール直後の端末の負荷と見られる） | 456〜839 ms（symlink）・484 ms（copy） |
| 2 回目以降の確認 | 2.5〜3.8 ms | 4〜77 ms（入れ直し後の張り直し 8 本を含めて 4.2 ms） |
| CLR の起動（hostfxr 読込〜関数の取り出し） | 65〜128 ms（インストール直後の 1 回目は 1159 ms） | 61〜175 ms（ランタイム起動 24〜83 ms・SEEDScripting 読込 17〜48 ms） |
| ユーザースクリプトの読み込み | 37〜48 ms | 10〜24 ms |
| `OnStart` の実行環境 | `.NET 10.0.12 rid=linux-bionic-x64 os=Android (API level 35) isAndroid=True` | `rid=linux-bionic-arm64 arch=Arm64 os=Android (API level 36)` |
| null 参照・0 除算 | 両方とも例外として捕まる（ART のシグナルチェーンの下でも CoreCLR のハードウェア例外が動く） | 同じ |
| 暗号 API（symlink） | SHA256・RandomNumberGenerator が動く | 同じ |
| 毎秒のログ | `Time`・`TouchCount`・`SafeArea=(0,128,1080,2209)`・`Portrait`・dpi 420 | `SafeArea=(0,132,1080,2205)`・`Portrait`・dpi 420（約 11〜17 fps） |
| タッチ | `input tap` で大きさが切り替わり、`input swipe` で 122° 回った（画面で確認） | 同じ（104° 回った） |
| 背面 → 復帰 | 復帰後もスクリプトが動き続け、状態（触れた回数）も残る。背面の間はゲーム時間が進まない | 同じ |
| `-PushScripts` | 置き場 `files/bin/` の v2 が使われた | 同じ（約 9 秒。うち SeedPak のビルドとコンパイル 5〜6 秒）。外部フォルダへの adb push は読めなかった |
| Mono | 起動とスクリプトの実行を確認（§17.9） | 未確認 |
| Windows（`SEED.exe`） | 開発時の Play（その場コンパイル・`dotnet root: global`）と配布物の形（`bin/SEEDUserScripts.dll`）の両方で同じスクリプトが動いた | — |

### 17.12 制限・持ち越し

詳細と持ち越し先は [backlog.md](backlog.md) の「Android」節。

- 初回の展開（実機で 0.5〜0.8 秒）と CLR の起動は android_main のスレッドで同期に行う（その間 UI スレッドはサーフェスの受け渡しで待つ）。
- BCL は絞っていない（1 ABI で 58〜65 MB。APK で約 25 MB 増）。trim は動的に読むスクリプト DLL が壊れるため §11.2 のとおり保留。
- アプリのデータフォルダのファイルを実行する方式（R2R の DLL・`copy` の .so）は SELinux の auditallow の対象（§17.4）。
- TLS・X509 は未確認。Mono の実機・Mono の暗号 API は未対応。
- スクリプトのデバッグ（netcoredbg のアタッチ）は Android では使えない（診断機能を止めている・DAC を入れていない）。
- Roslyn の DLL（約 9 MB）が Android の `bin/` にも入る（読み込まれない）。

---

## 18. アプリの識別情報（段階C-1・2026-09-25）

ゲームごとに Android のアプリ ID・ランチャーの名前・版を持てるようにした（データドリブン）。それまでは全ゲームが仮の
`com.seedengine.runtime`（名前 SEED Runtime）で、端末には 1 本しか入れられなかった。

### 18.1 設定（`project_settings.json` の `android` 節）

```json
"android": {
  "application_id": "com.example.mygame",
  "app_name": "私のゲーム",
  "version_code": 3,
  "version_name": "1.0.3"
}
```

| キー | 既定（空・無いとき） | 意味・検査 |
|---|---|---|
| `application_id` | `.seedproj` の `name` から `com.seedengine.<英数字化した名前>`。`.seedproj` が無ければ `com.seedengine.runtime` | アプリ ID（端末はこれで別のアプリかを見分ける）。2 区切り以上・各区切りは英字で始まり英数字と `_` だけ |
| `app_name` | プロジェクトの表示名（`.seedproj` の `display_name`、空なら `name`）。`.seedproj` が無ければ `SEED Runtime` | ランチャーに出る名前（マニフェストの `android:label`）。先頭（空白を除く）に `@` / `?` は使えない（リソースの参照と解釈される）・改行不可 |
| `version_code` | `1` | 整数の版（1〜2100000000。ストアへ出すたびに増やす）。数字の文字列も読む |
| `version_name` | `"1.0"` | 人が読む版の文字列。改行不可 |

- 英数字化: 全角を半角へ（NFKC）→ 小文字 → `a-z0-9` だけ残す。数字で始まれば `app` を前に付け、何も残らなければ（日本語だけの名前）
  `app` ＋ 名前の SHA-256 の先頭 8 桁（日本語名のプロジェクトどうしで ID がぶつからないように）。例 `WarashibeFishing` → `com.seedengine.warashibefishing`、
  `3DGame` → `com.seedengine.app3dgame`、`釣りゲーム` → `com.seedengine.app` ＋ 8 桁。
- **プロジェクトの名前を変えると既定の ID も変わる**（端末は別のアプリとして入れ、セーブを引き継がない）。配布するゲームは ID を明示しておく。
- 既定値と検査の正典は `editor/src/Android/Project/AndroidAppIdentityResolver.cs`（エディタのプロジェクト設定ウィンドウと SeedAndroid が同じ関数を使う）。
  設定の JSON の読み書きは `editor/src/ProjectSettings/AndroidAppSettings.cs`（型の違う値は「未設定」として読み、ProjectSettingsData 全体の読み込みを
  失敗させない。知らないキーは保存で失われない。何も設定されていなければ節ごと保存しない）。
- エディタでは「プロジェクト設定 → 解像度設定 → Android アプリ情報（モバイル）」（画面の向きの下）。空欄は既定値で、各欄の下に既定値を出す。
  保存のときにビルドと同じ規則で検査し、誤りがあれば保存しない。ランタイム（SEED.exe・libSEED.so）はこの節を読まない。

### 18.2 流れ（プロジェクト設定 → APK）

```
project_settings.json の android 節（＋ .seedproj の name / display_name）
  └ SeedAndroid: AndroidProjectResolver.ResolveIdentity（検査 → 既定値で埋める。誤りがあれば何もビルドせずに止める）
      └ gradlew assembleDebug -Pseed.applicationId=… -Pseed.versionCode=…（値が cmd.exe の解釈する文字を含めば
        環境変数 ORG_GRADLE_PROJECT_seed.appName=… 等。Gradle/GradleInvocation.cs）
          └ app/build.gradle.kts: applicationId / versionCode / versionName ／ manifestPlaceholders["seedAppLabel"]
              └ AndroidManifest.xml の android:label="${seedAppLabel}"（渡されなければ @string/app_name = SEED Runtime）
  └ 端末の操作（install の確認・run-as・am start・force-stop）は決まったアプリ ID で行う。
    Activity は完全修飾（<ID>/com.seedengine.runtime.MainActivity）で起動する（Java の名前空間は変えないため）
```

- 変換（プロパティ → APK の値）は `app/build.gradle.kts` の 1 か所。手で gradlew を叩いて値を渡さなければ従来どおり
  `com.seedengine.runtime` / `SEED Runtime` / `versionCode 1` / `versionName 0.0.1-dev`。
- 端末側のコード（libSEED.so）はパッケージ名を決め打ちしていない（データの置き場は `internal_data_path()` 等から得る）。JNI の関数名
  （`Java_com_seedengine_runtime_…`）は Java のクラスの名前空間に結び付くので、アプリ ID を変えても動く。

### 18.3 確認結果（2026-09-25・エミュレータ x86_64）

`aapt2 dump badging app-debug.apk`（build-tools 36.0.0）で確かめた。

| プロジェクト | package（versionCode / versionName） | application-label |
|---|---|---|
| `.seedproj` 無し（アセットだけ） | `com.seedengine.runtime`（1 / 1.0） | SEED Runtime（`@string/app_name`） |
| `IdentProbe.seedproj`（表示名「識別テスト」）・`android` 節なし | `com.seedengine.identprobe`（1 / 1.0） | 識別テスト |
| `android` 節 `com.seedengine.c1test`・`C1 テスト & Co`・7・`0.7 (c1)` | `com.seedengine.c1test`（7 / 0.7 (c1)） | C1 テスト & Co（`&` と括弧は環境変数で渡した） |
| 同じ節で名前を `C1 Space Test`・版を `0.8` に | `com.seedengine.c1test`（7 / 0.8） | C1 Space Test（空白だけなので `-P` の引数で渡した） |

- 別 ID のアプリ（`com.seedengine.c1test`）は `run` でインストール・`<ID>/com.seedengine.runtime.MainActivity` の起動・スクリプトの実行
  （`[PROBE v2] OnStart`）・データの置き場 `/data/user/0/com.seedengine.c1test/files` まで確かめ、終わった後にアンインストールした。

---

## 19. 段階C-1 の確認結果と制限（2026-09-25）

`SeedAndroid`（§5.1）と `build_and_run.ps1` の互換ラッパーで、エミュレータ（AVD `seed_pixel6_api35`・x86_64）に対して確かめた。
プロジェクトは段階B の確認用プロジェクト（最小構成＋確認用スクリプト）の写し。

| 項目 | 結果 |
|---|---|
| `run`（1 回目・記録なし） | 7 工程すべて（.so 9.2・SeedPak 8.4・同梱 .NET 1.3・Gradle 11.7・install 9.3・起動 9.7・logcat 25 秒）。logcat にスクリプトの `[PROBE v1] OnStart … rid=linux-bionic-x64` と毎秒の `SEED.Debug.Log`（タグ DOTNET）。保存した logcat は UTF-8 で日本語が化けない。同梱 .NET の content_id が変わったため端末は 1 回だけ展開し直した（2104 ms） |
| `run`（2 回目・変更なし） | .so・pak・同梱 .NET・APK・インストールの 5 工程を「変更なし」「端末に同じ APK が入っている」で飛ばし、起動と logcat だけ（15.2 秒） |
| `.cs` を変えて `build_and_run.ps1 -Abi x86_64 -Serial … -ProjectDir … -LogcatSeconds 12 -LogFile …` | pak とスクリプト（入力が変わった）・Gradle・install だけを行い、.so と同梱 .NET は飛ばした（26.8 秒・終了コード 0） |
| `push` | SeedPak `--scripts-only` → run-as で `files/bin/` へ 5 ファイル 9.1 MB → 起動し直し、`スクリプトの置き場: /data/user/0/com.seedengine.runtime/files/bin/` から `[PROBE v2] OnStart` |
| `run --assets-dir … --push-scripts`（開発用の経路） | pak の無い APK（57.3 MB）・アセット 4 ファイルの run-as 転送・DLL の転送 → 「APK に apk:seed/assets.pak がありません。開発用の置き場…から読みます」→ `load_play_scene done actors=2` → `[PROBE v2] OnStart` |
| `stop` / `logcat` / `devices --json` | `am force-stop` で `pidof` が空になる。誤ったアプリ ID は終了コード 1。`devices --json` はシリアル・種類・状態・ABI（`build_abi`） |
| アプリの識別情報 | §18.3 |
| arm64 の `build`（別の ABI へ切り替え） | .so（増分）19.2・SeedPak 6.7・同梱 .NET 1.2（x86_64 の前回分を消して arm64 を組み立て）・Gradle 19.9 秒 |
| エンジンのソース（コメント）・`dotnet_runtime.json`・Gradle の設定を変えた後の `build` | .so・pak・同梱 .NET を「入力が変わった」で作り直し、Gradle も回した |
| 最後の確認（既定の ID に戻して `run` を 2 回） | 1 回目 46.6 秒（pak・Gradle・install・起動・logcat。スクリプトは APK の `bin/` から）、2 回目 13.4 秒（5 工程を飛ばす）。端末に残したのは `com.seedengine.runtime`（versionName 1.0）だけ |
| 実機（Pixel 6a・arm64） | **未実施**（作業中は USB につながっていなかった。arm64 の APK を作るところまで。backlog） |

**制限・持ち越し**（詳細は [backlog.md](backlog.md) の「Android」節）

- Gradle の置き場は 1 つなので、ABI・プロジェクト・アプリ ID を行き来するたびに APK を作り直す（APK を指紋ごとに取っておく仕組みは無い）。
- pak とスクリプトは SeedPak を子プロセス（`dotnet run`）で呼ぶ（1 回 2〜6 秒のうち多くは `dotnet run` のビルドの確認）。エディタへ組み込む段階C-2 では
  同じプロセスの `AssetPakBuilder` / `ScriptPackager` を直接呼ぶ余地がある。
- 入力の指紋はファイルの大きさと更新時刻（中身は読まない）。`AndroidBuildInputs` の表に無いファイルを工程が読むようになったら表へ足す。
- インストールを飛ばす判断は「前回自分が入れた APK が、その時の場所のまま入っている」こと。他の人・他のプロジェクトが同じ ID で入れ直せば入れ直す。
