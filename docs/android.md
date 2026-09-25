# Android 対応（正典）

SEED のランタイム（Rust の `runtime/`）を Android 端末で動かすための、構成・手順・現状・ロードマップの正典。
段階0（2026-09-24）と、段階A のうち複数指タッチの入力基盤（§12）・APK 内 pak からの起動（§13）・保存先の振り替え／セーブの保護／起動基盤（§14）・画面の向きと安全領域（§15）・音声（背面での停止・音声フォーカス・音量キー。§16）、段階B の C# スクリプトの実行（APK に同梱した .NET 10 の CoreCLR。§17）、段階C-1 のビルド・配置・起動の C# 化（中核 `editor/src/Android/` とコンソールツール `SeedAndroid`。§4.6・§5）とアプリの識別情報（§18）、段階C-2 のエディタからの実行（実行ボタンの実行先セレクタ・Output パネル・停止・パッケージ化ウィンドウの Android 出力。§20）、段階D の IPC の TCP 化と一時停止（§21）・描画プリセット（§22）・実行中の差し替え（§23）・配布（署名付きの release APK / AAB・アイコン・Google Play の要件・NativeAOT の評価。§24）までの内容。未着手・保留の課題は [backlog.md](backlog.md) の「Android」節に集約する。

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
| 対象 API | API 36（Android 16。段階D で 35 から上げた） | `targetSdk = compileSdk = 36`。Google Play は 2026-08-31 以降の新規・更新に 36 以上を求める（§24.2） |
| GPU | **Vulkan 1.1 必須**（wgpu の Vulkan バックエンド。GLES へは落とさない） | マニフェストで `android.hardware.vulkan.version` 0x401000 を必須宣言 |
| 16KB ページ | 対応済み（NDK r28 の既定で LOAD セグメントが 16KB 整列） | 配布用のビルドの要件チェックが、できた APK / AAB の中のすべての .so の LOAD の整列を毎回確かめる（§24.8） |
| 開発用端末 | 実機 Pixel 6a（Android 16 / API 36・arm64・Mali-G78）／AVD `seed_pixel6_api35`（API 35・Google APIs・x86_64・GPU host） | どちらも段階0 の項目を確認済み（§7） |

---

## 3. ツールチェーン

| 道具 | 版（段階0 で使用） | 入れ方・備考 |
|---|---|---|
| Rust | 1.98 | `rustup target add aarch64-linux-android x86_64-linux-android` |
| cargo-ndk | 4.1.2 | `cargo install cargo-ndk`。NDK の clang をリンカとして差し込み、`-o` で jniLibs へ .so を写す |
| Android NDK | r28（28.2.13676358） | 環境変数 `ANDROID_NDK_HOME`。r28 は 16KB ページ整列が既定 |
| Android SDK | platform 36（段階D。以前は 35）/ build-tools 36（要件チェックの aapt2・zipalign・apksigner。段階D）/ platform-tools | 環境変数 `ANDROID_SDK_ROOT`（または `ANDROID_HOME`） |
| JDK | Android Studio 同梱の JBR（25） | 環境変数 `JAVA_HOME`。`keytool`（配布用の鍵の作成・確かめ。段階D）も JDK のもの |
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
| keytool | 見つけた JDK の `bin\keytool.exe`（段階D） |
| build-tools | SDK の `build-tools\` のうち `aapt2.exe` がある最新版（段階D。配布物の要件チェック） |

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
  play_requirements.json     Google Play の要件の表（targetSdk の下限・16 KB・予約された ID 等。要件チェックが読む。段階D・§24.8）
  settings.gradle.kts        リポジトリ（google / mavenCentral）と :app
  build.gradle.kts           AGP 9.1.0
  gradle.properties          AndroidX 等（マシン固有パスは書かない）
  gradlew / gradlew.bat / gradle/wrapper/   Gradle 9.3.1 の wrapper
  app/build.gradle.kts       minSdk 29 / targetSdk 36 / abiFilters arm64-v8a, x86_64 / release の署名（seed.signing.*）/ 依存
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
  app/src/seedIcon/res/                    ← ランチャーのアイコン（mipmap-*・values の背景色。生成物・追跡しない。段階D・§24.7）
  app/build/seed/                          ← SeedAndroid の作業フォルダ（置き場の記録 step_stamps.json・NuGet の取り寄せ・DLL の差し替え用。§4.6）
  app/build/outputs/apk/{debug,release}/   ← 開発用・配布用の APK（段階D で release を追加）
  app/build/outputs/bundle/release/        ← 配布用の AAB（段階D）
  native/                    §4.1 の cdylib クレート
```

- pak は `androidResources { noCompress += "pak" }` で非圧縮（STORED）のまま APK に入れる（§13.2）。
- `packaging.jniLibs.useLegacyPackaging = true`（段階B）。.so をインストール時に nativeLibraryDir へ展開させる（同梱 .NET の .so を
  dotnet-root から参照するため。§17.4）。その分 APK の .so は圧縮され、インストール時に展開される。AAB・Google Play でも使える（根拠は §24.6）。
- ビルドの種類（段階D・§24.4）: debug（デバッグ用の鍵・debuggable・`src/debug/` の INTERNET）と release（`isDebuggable = false`・
  `isMinifyEnabled = false`・`signingConfigs.release`。署名の材料 `seed.signing.*` が揃わなければ `preReleaseBuild` で止める）。
- ランチャーのアイコン（段階D・§24.7）: `android:icon="${seedAppIcon}"`。`seed.launcherIcon=generated` なら `@mipmap/ic_launcher`
  （`src/seedIcon/res/`）、無ければシステムの既定のアイコン。
- `android:enableOnBackInvokedCallback="false"`（段階D）: targetSdk 36 の予測型の「戻る」を使わず、戻るキーをネイティブへ届ける（§24.2）。

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
  Toolchain/  AndroidToolchain（SDK / NDK / adb / emulator / JDK / cargo / dotnet の場所。§3）・AndroidEnginePaths（リポジトリの置き場）
  Processes/  ChildProcessRunner（子プロセスの起動・行単位の出力・標準入力・中断で子孫ごと終了）・MixedEncodingLineReader（UTF-8 と ANSI の混在）・
              DetachedProcess（切り離した起動。CreateProcessW・ハンドルを継承させない。エミュレータ用）・WindowsCommandLine（引数の引用）
  Adb/        AdbClient（devices -l・getprop・install・pm path・run-as の tar 展開・am start（起動オプションの extra 付き）/ force-stop・logcat・
              emu avd name・sys.boot_completed）・AdbDeviceListParser・AndroidDeviceSelector（端末の決め方。自動の規則も）・
              AndroidDeviceTarget / AndroidDeviceDecision・RunAsTarArchive（.NET の TarWriter。外部の tar は使わない）・AndroidLogcatSession
  Emulator/   AndroidDeviceProvisioner（端末の用意: 選ぶ・要ればエミュレータを起動して起動の完了を待つ。段階C-3。§20.9）・
              EmulatorLauncher（emulator -list-avds・切り離した起動）・EmulatorAvdChooser（AVD の決め方）・EmulatorHost・EmulatorTimings・WaitClock
  Project/    AndroidProjectResolver（--project / --assets-dir）・AndroidProjectSettingsReader（screen_orientation・android 節・登録シーン）・
              AndroidAppIdentityResolver（アプリの識別情報の既定値と検査。§18）・AndroidScenePath（起動するシーンの指定を揃える。§20.10）・
              AndroidPakSceneSeeds（未登録の起動シーンを pak の収録の起点に足すか。段階C-4。§20.10）・PakEntryIndex（pak のエントリ名）
  Dotnet/     DotnetRuntimeSettings（dotnet_runtime.json）・NuGetRuntimePackRestorer・DotnetRuntimeBundle（dotnet-root への組み立てと目録。§17）
  Gradle/     GradleInvocation（gradlew のタスク〈debug / release・APK / AAB〉と -P／環境変数の組み立て。パスワードは必ず環境変数）
  Signing/    AndroidSigningResolver（配布用の鍵の決め方）・AndroidSigningSecrets（パスワード。伏せ字）・AndroidKeystoreTool（keytool で作る・開けるか確かめる）（段階D・§24）
  Icons/      PngDecoder / PngEncoder / ImageResampler（.NET 標準だけの PNG と縮小）・LauncherIconGenerator / LauncherIconStager（アイコンの生成と置き場）（段階D・§24.7）
  Release/    PlayRequirements（要件の表）・AndroidRequirementChecks（ビルドの前の判定）・AndroidArtifactInspector / AndroidArtifactChecks
              （配布物の読み直しと判定）・ElfAlignmentReader・AndroidReleaseHistory（versionCode の記録）・AndroidRequirementsCheckRunner（check）（段階D・§24.8）
  Plan/       AndroidBuildPlan（どの工程を飛ばすか。純粋な処理）・AndroidStepFingerprints / AndroidFingerprint（指紋）・AndroidBuildInputs（入力の表）
  State/      AndroidStepStamps（置き場の中身の記録。エンジン側）・AndroidRunState（プロジェクトの実行状態）
  Steps/      工程ごとの実装（NativeBuild・PackageContent・DotnetBundle・GradleBuild・ReleaseCheck・Install・PushAssets・PushScripts・Launch・Logcat）
  Pipeline/   AndroidRunPipeline（本体）・AndroidRunRequest（指定）・AndroidPipelineEvent（進み具合）・
              AndroidDeviceActions（一覧・端末の用意・停止・logcat）
```

**入口（段階C-2 のエディタ統合で使うもの。エディタ側の使い方は §20）**

| やること | 呼ぶもの |
|---|---|
| 準備 | `AndroidEnginePaths.Locate(AppContext.BaseDirectory, Environment.CurrentDirectory)`・`AndroidToolchain.Detect()` |
| 実行先の一覧（実機／エミュレータ・ABI 付き） | `new AndroidDeviceActions(toolchain).ListDevicesAsync(ct)` → `AndroidDeviceEntry`（`Device.Kind` が `Physical` / `Emulator`・`Device.IsReady`・`BuildAbi`） |
| ビルド → インストール → 起動 → logcat | `new AndroidRunPipeline(engine, toolchain).RunAsync(new AndroidRunRequest { Goal = AndroidRunGoal.Run, ProjectDir = …, Serial = … }, progress, ct)` |
| スクリプトの DLL だけ差し替え | 同じ `RunAsync` を `Goal = AndroidRunGoal.Push` で |
| 停止ボタン | `ct` を取り消す（子プロセスを止める。logcat の途中なら「止めた」＝成功）＋ `AndroidDeviceActions.StopAppAsync(serial, result.Identity.ApplicationId, ct)`（取り消していない新しい `ct` で） |
| アプリが端末で終わったか（C-2 で追加） | `AndroidDeviceActions.IsAppRunningAsync(serial, applicationId, ct)`（`adb shell pidof`。`AdbClient.GetProcessIdsAsync`） |
| 準備で決まった端末・アプリ ID・ABI（C-2 で追加） | イベント `AndroidPrepared`（準備を終えたときに 1 回。工程より前に届く） |
| 前回の実行先（セレクタの既定値） | `AndroidRunState.Load(AndroidRunState.PathForProject(projectRoot)).LastTarget`、エディタで選んだもの（PC・自動を含む）は同じ記録の `EditorTarget`（C-2 で追加） |
| 端末が無ければエミュレータを起動して実行（C-3 で追加） | 指定の `Serial = "auto"`（実機 → 起動中のエミュレータ → AVD を起動）か、`Serial = <シリアル>` ＋ `EmulatorFallback = true`（見えなければエミュレータ）。`Avd` で AVD を指定。準備の中で `AndroidDeviceActions.EnsureDeviceAsync` が呼ばれ、待っている旨はログと `AndroidProgressChanged`（工程は準備）で届く（§20.9） |
| 開いているシーンから起動（C-3 で追加） | 指定の `ScenePath`（アセットルートからの相対パス）。起動の工程が `am start --es seed.scene '<パス>'` で渡す（§20.10）。シーンマネージャに未登録なら準備が pak の収録の起点に足す（C-4 で追加。`AndroidPipelineContext.PakExtraScenes` → SeedPak `--extra-scene`） |

- `RunAsync` は全体をスレッドプールで動かし（UI スレッドから `await` しても止めない）、失敗しても例外は投げず `AndroidPipelineResult`
  （`Succeeded` / `Canceled` / `FailureKind` / `FailureMessage` / 工程ごとの結果 / 計画 / 端末 / アプリの識別情報）を返す。
- 進み具合は `IProgress<AndroidPipelineEvent>` に届く: `AndroidPrepared`（準備で決まった端末・アプリの識別情報・ABI）/ `AndroidPhaseStarted`（何番目か・行う理由）/ `AndroidPhaseFinished`（成功・飛ばした・失敗・中断と
  所要時間・一行の結果。飛ばした工程は Started 無しでこれだけ）/ `AndroidLogLine`（説明・子プロセスの標準出力・標準エラー・警告・エラー・logcat の 1 行）/
  `AndroidProgressChanged`（0〜1）/ `AndroidPipelineError`（失敗の種類 `AndroidFailureKind` と説明。最後に 1 回）。子プロセスの出力を読むスレッドからも
  届くので受け手はスレッド安全にする（WPF の `Progress<T>` なら UI スレッドへ順に送られる）。

**工程を飛ばす判断（`Plan/AndroidBuildPlan.cs`。純粋な処理で、材料は準備の段階で集める）**

| 工程 | 入力の指紋（ファイルは相対パス・大きさ・更新時刻） | 出力の同一性 |
|---|---|---|
| libSEED.so（ABI ごと） | `AndroidBuildInputs.NativeSources`（Cargo.toml / Cargo.lock・runtime/src・runtime/android/native・plugin_api・埋め込むアイコン）＋ ABI・プロファイル・API レベル・NDK | `jniLibs/<ABI>/libSEED.so` |
| pak とスクリプト | プロジェクトのアセットルート全体＋ `PackageToolSources`（SeedPak・パッケージ化のコード・scripting/・runtime/src）＋プロジェクトの場所＋収録の起点に足すシーン（未登録の起動シーン。段階C-4。足さないときは材料に入れない）。プロジェクトを APK に入れないなら「置き場を空にする」 | `app/src/main/assets/seed/` の一覧 |
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
| 実行状態（`AndroidRunState`） | `<プロジェクト>/cache/android/run_state.json`（[project_system.md](project_system.md) §1 の `cache/`。プロジェクトが無ければ `runtime/android/app/build/seed/run_state.json`） | 前回の実行先（シリアル・種類・機種・ABI・アプリ ID）、エディタの実行先セレクタで最後に選んだもの（`editor_target`: `"pc"` かシリアル。段階C-2。SeedAndroid は読まずに保つ）、端末ごとに自分が入れた APK（SHA-256・`pm path`）、前回の実行の結果・工程ごとの判断・指紋 |

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

# 端末を自動で決める（実機 → 起動中のエミュレータ → どちらも無ければ AVD を起動して待つ。段階C-3）。起動するシーンも指定
dotnet run --project editor/tools/SeedAndroid -- run --project D:\path\to\Project --serial auto --scene scenes/Stage2.scene

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

# 動いているアプリ（run / push で起動したもの）を一時停止・画面を撮る・再開（IPC。段階D-1・§21）
dotnet run --project editor/tools/SeedAndroid -- pause --project D:\path\to\Project --serial <実機>
dotnet run --project editor/tools/SeedAndroid -- screenshot --project D:\path\to\Project --serial <実機> --out paused.png
dotnet run --project editor/tools/SeedAndroid -- resume --project D:\path\to\Project --serial <実機>

# 配布用（段階D・§24）: 鍵を作る → AAB（Google Play へ出す形）を作る → ビルドをせずに要件を確かめる
# パスワードは環境変数 SEED_ANDROID_KEYSTORE_PASSWORD（無ければ対話で聞く。コマンドラインには書かない）
dotnet run --project editor/tools/SeedAndroid -- keystore create --keystore D:\keys\mygame_upload.jks --key-alias upload
dotnet run --project editor/tools/SeedAndroid -- build --release --format aab --project D:\path\to\Project --keystore D:\keys\mygame_upload.jks --key-alias upload
dotnet run --project editor/tools/SeedAndroid -- check --project D:\path\to\Project --format aab
```

| サブコマンド | 行う工程 |
|---|---|
| `devices` | 端末の一覧（`--json` で JSON。使える端末は ABI も読む） |
| `build` | libSEED.so → pak とスクリプト → 同梱 .NET → APK（配布用は APK / AAB と Google Play の要件の確認。§24） |
| `install` | build ＋ インストール（Gradle の `installDebug` と同じく、要ればビルドする） |
| `run` | install ＋（`--assets-dir` のアセット・`--push-scripts` の DLL の転送）＋ 起動 ＋ logcat。`--project` で `--push-scripts` なしなら、起動の前に `push` で置いた DLL の上書き（端末の `files/bin/`）を消す（段階C-4。§17.7） |
| `push` | スクリプトの DLL（と `--assets-dir` のアセット）の転送 ＋ 起動 ＋ logcat |
| `stop` | `am force-stop <アプリ ID>`（アプリ ID は `--app-id`、無ければ `--project` / `--assets-dir` の設定から） |
| `logcat` | logcat（`--since <端末の時刻>` から。省略時は今から） |
| `pause` / `resume` | 動いているアプリへ IPC の `PAUSE` / `RESUME` を送る（adb forward → TCP → 起動の記録〈同じ --project の run_state.json〉の接続トークンで `HELLO:` → 命令 → `DETACH` → forward を外す。`pause` の後も一時停止のまま。段階D-1・§21.5・§21.11） |
| `screenshot` | 動いているアプリの画面を IPC の `SCREENSHOT` で撮り、run-as で PC へ取り出す（`--out`。§21.6） |
| `keystore create` | 配布用の鍵（アップロード鍵）のキーストアを keytool で作る（`--keystore` 必須・`--key-alias`〈既定 `upload`〉・`--cert-name`・`--project`〈アセットの中に作らない検査〉。既にあるファイルは上書きしない。段階D・§24.3） |
| `check` | ビルドをせずに Google Play の要件を確かめる（設定＋`--artifact` か前回の配布用の出力。既定は配布用・`--format`。不合格があれば終了コード 6。段階D・§24.8） |

| オプション | 意味 |
|---|---|
| `--project <フォルダ>` | プロジェクト（`.seedproj` か `assets/` を持つフォルダ、またはアセットルートそのもの。規則は SeedPak と共有の `ProjectFolderResolver`）。APK に pak とスクリプトを入れる（パッケージ実行。§13・§17）。画面の向き・アプリの識別情報（§18）もここの `project_settings.json` から読む |
| `--assets-dir <フォルダ>` | 開発用: pak の無い APK にして、このアセットフォルダを run-as で端末の `files/assets` へ送る（`--project` と排他。端末は APK の pak を優先するため） |
| `--serial <シリアル>` | 対象の端末。省略時は使える端末がちょうど 1 台のときそれ（2 台以上ならエラー。前回の実行先を添える。私物の実機へ勝手に入れないため）。見えなければエラー（エミュレータへは切り替えない。切り替えるのは設定 JSON の `emulator_fallback: true`＝エディタで端末を選んだときの動き） |
| `--serial auto` | 端末を自動で決める（段階C-3。規則は §20.9）: 使える実機（前回使ったものを優先。前回のものが無く 2 台以上ならエラー）→ 起動中のエミュレータ（前回優先）→ 起動の途中のエミュレータ（完了を待つ）→ どれも無ければ AVD を起動して `sys.boot_completed` まで待つ（時間切れ 300 秒）。`install` / `run` / `push` で使える。`build` では端末を起動しない（1 台に決まる端末があれば ABI に使う）。`stop` / `logcat` では使えない（誤りの終了コード 1） |
| `--avd <AVD>` | `--serial auto`（と `emulator_fallback`）でエミュレータを起動するときの AVD。省略時は `emulator -list-avds` の一覧に `seed_pixel6_api35` があればそれ、無ければ先頭。一覧に無い名前はエラー（別の AVD を勝手に起動しない） |
| `--scene <シーン>` | 端末で起動するシーン（段階C-3。§20.10）。アセットルートからの相対パス（`scenes/Main.scene`）・`assets://…`・アセットルートの中の絶対パス。起動の工程が `am start --es seed.scene '<相対パス>'` で渡す。省略時は `project_settings.json` の開始シーン。アセットルートの外・`..` は指定の誤り（終了コード 1）。シーンマネージャに未登録のシーンは pak の収録の起点に足す（段階C-4。SeedPak `--extra-scene`。切り替えた最初の `run` は pak・APK・インストールをやり直す）。プロジェクトに無いシーンは警告を出して渡し、端末が logcat に警告を出して開始シーンで起動する |
| `--abi <ABI[,ABI]>` | `arm64-v8a` / `x86_64`。省略時は端末の `ro.product.cpu.abilist` の先頭から選ぶ（端末が決まらなければ両方） |
| `--release` | Rust 側を `--release` でビルド（開発用の APK はデバッグ署名のまま。配布用は常に `--release`） |
| `--variant <debug\|release>` | ビルドの種類（段階D・§24.4）。既定 `debug`。`release` は debuggable でない・INTERNET なし・アップロード鍵で署名（`push`・`--push-scripts`・`--assets-dir` と一緒に使えない）。書かずに `--format aab` か `--keystore` / `--key-alias` を指定すると `release` とみなす |
| `--format <apk\|aab>` | 形式（段階D）。既定 `apk`。`aab` は配布用の `build` だけ（端末へは直接入れられない） |
| `--keystore <パス>` / `--key-alias <別名>` | 配布用の署名の鍵（段階D。省略時はプロジェクトの `packaging_settings.json` の `android.signing`）。パスワードは環境変数 `SEED_ANDROID_KEYSTORE_PASSWORD`（キーが違えば `SEED_ANDROID_KEY_PASSWORD`）か対話の入力（§24.5） |
| `--cert-name <名前>` / `--artifact <パス>` | `keystore create` の証明書の名前（CN）／ `check` で確かめる APK / AAB（段階D） |
| `--config <JSON>` | 指定をまとめた設定 JSON（キーは `AndroidRunRequest` の snake_case: `project` / `assets_dir` / `serial` / `emulator_fallback` / `avd` / `scene` / `abis` / `release` / `skip_rust_build` / `skip_gradle` / `no_install` / `no_launch` / `no_logcat` / `push_scripts` / `rebuild` / `logcat_seconds` / `log_file` / `ipc_port` / `variant` / `format` / `keystore` / `key_alias`。`project`・`assets_dir`・`log_file`・`keystore` の相対パスは JSON のフォルダから、`scene` はアセットルートから。パスワードは JSON から読まない。コマンドラインが優先） |
| `--skip-rust` / `--skip-gradle` / `--no-install` / `--no-launch` / `--no-logcat` | 工程を飛ばす（`--skip-gradle` は pak とスクリプト・同梱 .NET・Gradle をまとめて飛ばす） |
| `--push-scripts` | `run` でもスクリプトの DLL を作り直して `files/bin/` へ送る |
| `--rebuild` | 変更の有無で工程を自動で飛ばさない（すべて作り直し、入れ直す） |
| `--logcat-seconds <秒>` / `--log-file <パス>` | logcat を流す秒数（0 か省略で止めるまで）／保存先（UTF-8） |
| `--app-id <ID>` / `--since <時刻>` / `--json` | `stop` / `pause` / `resume` / `screenshot` のアプリ ID ／ `logcat` の起点／ `devices` の JSON |
| `--ipc-port <ポート>` | 端末のランタイムが一時停止などの IPC を待ち受けるポート（段階D-1）。`run` / `push` は起動オプション（`am start --es seed.ipc_port`）で渡し、`pause` / `resume` / `screenshot` はここへつなぐ。省略時は 52735、`0` なら渡さない（一時停止などは使えない）。設定 JSON の `ipc_port` |
| `--out <パス>` | `screenshot` の書き先（省略時はカレントフォルダの `android_screenshot_<日時>.png`） |

- 入力が前回から変わっていない工程は自動で飛ばす（§4.6）。準備の段階で「行う／飛ばす」と理由を一覧で出し、最後に工程ごとの結果と所要時間をまとめる。
- 終了コード: `0` 成功 / `1` 指定の誤り / `2` 道具が無い / `3` 端末が無い・選べない / `4` ビルドの失敗 / `5` 端末の操作の失敗 /
  `6` Google Play の要件に不合格がある（配布用の `build`・`check`。配布物はできている。段階D）/ `130` 中断（Ctrl+C）。
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
| `[SEED QUALITY] preset=mobile render_scale=0.75 …` / `[SEED QUALITY][WARN] …` | 描画品質プリセットの決定（`app/render_quality.rs`。§22） | 使うプリセットと実効のつまみ。知らないプリセット名・読めないつまみは WARN。`[SEED FEATURES]` の `(品質上限)` は上限で下がった機能 |
| `[SEED GPU] 3.0s cpu_frames=… \| cpu frame=… acquire=… present=… \| gpu total=… shadow=… …` | パスごとの GPU 時間（起動オプション `seed.gpu_timing=1` のときだけ。§22.6） | 3 秒ごとの平均（ms）。`present` は提示待ちの目安、`gpu` の各区間は描画の節目ごとの GPU 時間 |
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
  → 段階D-2 でモバイル向けの描画品質プリセットを入れた（§22。Android の既定は `mobile`＝ゲーム画面を 0.75 倍で描いて拡大・
  前方描画・SSGI／AO／反射／ブルーム・水面反射なし・影を軽く。UI は画面の解像度のまま）。パスごとの GPU 時間の計測
  （`seed.gpu_timing=1`）で段階D-3 に実機で測り、`proj_bench`（縦）で `desktop` 16.4 fps（GPU 59.2 ms）→ `mobile` 59.2 fps（GPU 10.5 ms）
  になった（最大の要因はデファードのライティングと SSGI。§22.7）。

---

## 9. 段階ロードマップ

| 段階 | 内容 |
|---|---|
| **0（完了）** | 実機/エミュレータに 1 枚絵。libSEED.so ＋ Gradle ＋ GameActivity、logcat、サーフェスの破棄・再生成、回転追従 |
| **A** | スクリプト無しでシーンを動かす: APK 内 pak（AssetManager。**2026-09-24 実装・§13**）、保存先の振替・セーブの保護・パイプラインキャッシュ・背面での物理停止・戻るキー（**2026-09-24 実装・§14**）、縦横とサーフェス再生成の仕上げ、複数指タッチ（`Input.TouchCount` / `GetTouch(i)`。PC はマウス＝指 0。**2026-09-24 実装・§12**）、安全領域・画面の向き API（プロジェクト設定の向き・`SEED.Screen`。**2026-09-24 実装・§15**）、音声（鳴ることの確認・背面での停止・音声フォーカス・音量キー。**2026-09-24 実装・§16**）、logcat の整備 |
| **B** | スクリプト: **PC も Android も .NET 10 の CoreCLR に揃える**（PC は全 C# プロジェクトを `net10.0` へ移行済み。Android は `android-*` ランタイムパック＋同じ版の bionic パックの hostfxr / hostpolicy。§11）。ScriptPackager の事前コンパイル DLL とランタイムを同梱し、既存の hostfxr 経路を `Hostfxr::load_from_path` で使う（**2026-09-25 実装・§17**。Mono へ切り替え可）。出荷時は NativeAOT を後で検討 |
| **C** | エディタ「実行」統合: **C-1（2026-09-25 実装・§4.6・§5・§18）** ビルド・配置・起動の手順を C# の中核（`editor/src/Android/`）とコンソールツール `SeedAndroid` に移し、変わっていない工程の自動の省略・アプリの識別情報のプロジェクト設定化。**C-2（2026-09-25 実装・§20）** 実行ボタンの隣の実行先セレクタ（PC／実機／エミュレータ）、中核を呼んでビルド → install → 起動 → logcat を Output パネルへ・停止ボタン・アプリ側の終了の検知、パッケージ化ウィンドウの Android 出力の実働化（デバッグ署名の APK）。pak/DLL だけ push する高速経路のエディタへの組み込みは持ち越し（backlog） |
| **D** | **D-1（2026-09-25 実装・§21）** エディタとランタイムの IPC を TCP（adb forward）でも使えるようにし、Android の実行中も PC の Play と同じ実行バーから一時停止・再開（SeedAndroid の `pause` / `resume` / `screenshot`。接続トークンで照合し、一時停止中もゲームの画面のまま）。**D-2（2026-09-25 実装・§22）** モバイル向けの描画品質プリセット（データドリブン。`runtime/config/render_presets.json`。Android の既定 `mobile`＝描画スケール 0.5・前方描画・重い後処理なし。UI は画面の解像度のまま）とパスごとの GPU タイムスタンプ計測。**D-3（2026-09-25・§22.7）** 実機（Pixel 6a・縦）で計測し、`desktop` 16.4 fps → `mobile` 59.2 fps（GPU 59.2 → 10.5 ms。最大の要因はデファードのライティングと SSGI）。`mobile` の描画スケールを 0.5 → 0.75、`mobile_high` を 0.75 → 1.0 に見直した。**実行中の差し替え（2026-09-25 実装・§23）** Android の実行中にシーン・アセット・スクリプトを保存すると、違うものだけを端末の上書き層 `files/assets`（デバッグ版は pak より先に読む）・`files/bin` へ送り、`RELOAD_SCENE` / `RELOAD_ASSET` / `RELOAD_SCRIPTS` でフレームの境界に取り込む（SeedAndroid の `push --assets` / `reload`。実機でモデル・シーン・スクリプトの差し替えと解除を確かめた。エディタの画面からは未確認）。**配布（2026-09-26 実装・§24）** 配布用（release）の APK / AAB（アップロード鍵で署名・debuggable でない・INTERNET なし・Rust は --release。鍵が無ければビルドしない）、鍵の作成（`keystore create`）、アイコンの生成、Google Play の要件チェック（ビルドの前と後。`check`）、targetSdk 36、16 KB ページの確認、NativeAOT の評価（実装はしない）。Wi-Fi 実行・Play Console への実際の提出は持ち越し |

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
  MainActivity.onCreate: 環境変数 TMPDIR / HOME / DOTNET_EnableDiagnostics=0（§17.6）・起動オプション（seed.* の extra。§20.10）
  android_main → launch::launch_args（起動するシーンもここで決める）→ dotnet_runtime::prepare（runtime/android/native/src/dotnet_runtime/）
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
- **`run` は APK の内容を正とする**（段階C-4）: `--project` の `run`（エディタの実行ボタンも同じ `Goal = Run`）は、起動の工程で
  アプリを止めた後・起動の前に、自分のアプリの `files/bin/` を run-as で消す（`if [ -e files/bin ]; then rm -rf files/bin && echo removed; fi`。
  `AdbClient.RunAsRemoveDirectoryAsync`）。あって消したときだけ `push した DLL の上書きを解除しました（端末の files/bin/ を消し、APK の bin/ のスクリプトを使います）。`
  の 1 行を出す（消せなければ警告して、そのまま起動する）。以前は `push` の後に `.cs` を直して `run` しても、残った `push` の DLL で動いていた。
  **APK を入れ直したとき**（`install`・`run` のインストールの工程が APK を入れた直後。`adb install -r` はアプリのデータを残すため）も同じ規則で消す
  （段階D の追加。同じ APK が入っていてインストールを飛ばしたときは消さない）。
  消さないのは `push`・`--push-scripts`（これから置く・置いた）と、開発用の `--assets-dir`（APK に `bin/` が無く `files/bin/` が唯一の置き場）。
  判断と消し方は `Steps/PushedOverrides`（`ClearsScripts`。場面は `AfterInstall` / `BeforeLaunch`。単体テスト `PushOverrideTests`）。
  差し替えで送ったアセット（`files/assets/`）も同じ場面で消す（§23.4）。
- 当初は外部アプリ専用フォルダへ `adb push` する形にしたが、実機では §4.5 と同じ理由（adb push が作ったフォルダは shell の所有）で
  アプリから読めなかったため、run-as の内部フォルダへ変えた。外部フォルダは手で置く場合の候補として残した（エミュレータでは使える）。
- Android ではその場コンパイル（.cs から）をしない（Roslyn の参照アセンブリが端末に無い）。`bin/` には PC と同じく Roslyn の DLL（約 9 MB）も
  入るが、Android では読み込まれない（backlog）。動かしたままの差し替えは §23（`files/bin/` へ送った DLL を `RELOAD_SCRIPTS` で読み直す。
  SeedAndroid の `reload scripts`、エディタは Android の実行中に `.cs` を保存したとき）。`push` は従来どおり DLL を送って起動し直す。

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
| 実機（Pixel 6a・arm64） | C-1 の作業中は未実施（USB につながっていなかった）→ 段階C-4 で `run`（arm64 のビルドから 261.9 秒）・2 回目の `run`（5 工程を飛ばして 31.4 秒）・`push`・`stop`・`--serial auto`・アプリの識別情報を実機で確かめた（§20.14） |

**制限・持ち越し**（詳細は [backlog.md](backlog.md) の「Android」節）

- Gradle の置き場は 1 つなので、ABI・プロジェクト・アプリ ID を行き来するたびに APK を作り直す（APK を指紋ごとに取っておく仕組みは無い）。
- pak とスクリプトは SeedPak を子プロセス（`dotnet run`）で呼ぶ（1 回 2〜6 秒のうち多くは `dotnet run` のビルドの確認）。エディタへ組み込む段階C-2 では
  同じプロセスの `AssetPakBuilder` / `ScriptPackager` を直接呼ぶ余地がある。
- 入力の指紋はファイルの大きさと更新時刻（中身は読まない）。`AndroidBuildInputs` の表に無いファイルを工程が読むようになったら表へ足す。
- インストールを飛ばす判断は「前回自分が入れた APK が、その時の場所のまま入っている」こと。他の人・他のプロジェクトが同じ ID で入れ直せば入れ直す。

---

## 20. エディタからの実行（段階C-2・C-3・C-4・2026-09-25）

エディタの実行ボタンの隣に **実行先セレクタ**（`PC`・`Android（自動）`・adb に見える実機・エミュレータ）を置いた。Android を選んで実行ボタンを押すと、
§4.6 の中核（SeedAndroid と同じクラス）で ビルド → pak とスクリプト → インストール → 起動 → logcat を一気通貫で行い、進み具合と logcat を
Output パネルへ流す。停止ボタンで端末のアプリを止める。2 回目以降は変更の無い工程を飛ばす（§4.6）。パッケージ化ウィンドウの Android 出力も
同じ中核で APK を作るようにした（§20.6）。

段階C-3 で次の 3 つを足した（利用者が実機で試して出た要望）:
- **端末が無ければエミュレータを自動で起動して実行する**（`Android（自動）`。選んだ端末が見えないときも同じ。§20.9）
- **PC の Play と同じく「開いているシーン」から起動する**（§20.10）
- 実行の前に**未保存の変更を「保存して実行 / 保存せず実行 / キャンセル」で尋ねる**（§20.11）

段階C-4 で、**シーンマネージャに未登録の開いているシーンも pak の収録の起点に足して、そのシーンから起動できる**ようにした（§20.10）。
あわせて段階C で端末なしに確かめていた項目を、実機 Pixel 6a でまとめて確かめた（§20.14）。

### 20.1 構成（UI と WPF 非依存の分け方）

エージェントはエディタを起動できないため、画面に出る判断（一覧・状態遷移・ボタンの有効/無効・文言・色）はすべて WPF 非依存のクラスにし、
単体テスト `editor/tests/AndroidRunUiTests` で確かめる。WPF 側は判断の結果を画面へ当てるだけ。

| 場所 | 役割 |
|---|---|
| `editor/src/AndroidRun/RunTargetEntry.cs`・`RunTargetCatalog.cs` | 実行先の 1 行と一覧の組み立て（PC + Android（自動）+ 端末・選べない状態・案内の行・選んでおく行） |
| `editor/src/AndroidRun/RunTargetSelectionStore.cs` | 前回の選択の読み書き（プロジェクトの `cache/android/run_state.json` の `editor_target` / `last_target`） |
| `editor/src/AndroidRun/PlayBarPolicy.cs` | プレイバー（状態表示・実行／停止ボタン・実行先セレクタ・進捗）の判断。PC の状態ごとの表示（従来の `ApplyUiState` の表）もここ |
| `editor/src/AndroidRun/AndroidRunStateMachine.cs`・`AndroidRunPhase.cs`・`AndroidRunSnapshot.cs` | 状態機械（§20.3） |
| `editor/src/AndroidRun/AndroidRunController.cs`・`AndroidRunBackend.cs`・`AndroidRunTimings.cs` | 実行・アプリの見張り（pidof）・停止の段取り。中核の入口は `IAndroidRunBackend`（テストは偽物） |
| `editor/src/AndroidRun/AndroidRunOutputFormatter.cs`・`LogcatLineParser.cs` | Output パネルの行（本文・色・出どころ）。§20.4 |
| `editor/src/AndroidRun/AndroidEditorRunRequests.cs` | 中核への指定（実行ボタン＝`Run`〈自動は `serial=auto`、端末は `emulator_fallback`・シーン・AVD〉、パッケージ化＝`Build`） |
| `editor/src/AndroidRun/AndroidRunSceneChoice.cs` | 端末で起動するシーン（開いているシーン／「開始シーンからプレイ」／開けないときは開始シーン。§20.10。C-3） |
| `editor/src/AndroidRun/AndroidUnsavedChangesPrompt.cs`・`editor/src/Dialogs/ActionChoiceWindow.cs` | 未保存の変更の確認の文言と選択肢（純粋な処理）と、ボタンの文言を決められる 3 択ダイアログ（WPF。§20.11。C-3） |
| `editor/src/Settings/AndroidEditorPreferences.cs` | エディタの設定の `android` 節（`emulator_avd`。§20.9。C-3） |
| `editor/src/AndroidRun/AndroidRunEnvironment.cs`・`AndroidToolchainReport.cs` | Android の実行先を使えるか（プロジェクト・リポジトリ・adb）と道具の一覧 |
| `editor/src/Logging/OutputLineStyle.cs`・`OutputLineClassifier.cs` | Output パネルの行の色の種類と出どころ。見た目の指定が無い従来の行は本文の印から決める（従来の判定をそのまま表にした） |
| `editor/src/Runtime/EditorState.cs` | PC のランタイムの状態（`RuntimeManager.cs` から切り出した。プレイバーの判断をテストから使うため） |
| `editor/src/MainWindow.AndroidRun.cs`（WPF） | 実行先コンボ・実行／停止ボタンの振り分け・プレイバーの適用・Android の実行の結線 |
| `editor/src/MainWindow.xaml`（WPF） | 実行先コンボ（`CmbRunTarget`）と進捗（`AndroidRunProgressPanel`）。実行・停止ボタンの Click は `OnPlayBarPlayClick` / `OnPlayBarStopClick` |
| `editor/src/Packaging/AndroidApkOutput.cs`・`PackagingWindow.xaml.cs` | パッケージ化ウィンドウの Android 出力（§20.6） |

中核に足したもの: `AdbClient.GetProcessIdsAsync`（pidof）・`AndroidDeviceActions.IsAppRunningAsync`・イベント `AndroidPrepared`・
`AndroidRunState.EditorTarget`・`AndroidBuildPlan.UnchangedReason`（単体テストは `AndroidPipelineTests` に追加）。
段階C-3 で `AndroidDeviceActions.EnsureDeviceAsync`・`Emulator/`・`AndroidRunRequest.ScenePath` / `EmulatorFallback` / `Avd`・
`AndroidScenePath`・`DetachedProcess` を足した（§4.6。単体テストは `AndroidPipelineTests` の `EmulatorAndSceneTests`）。
段階C-4 で `Project/AndroidPakSceneSeeds`・`AndroidProjectSettings.RegisteredScenes`・`AndroidPipelineContext.PakExtraScenes`・
`PackageContentStep.SeedPakArguments`・`LaunchStep.SceneNotInPakMessage` と、パッケージ化側の `AssetCollector.Collect(extraSeeds)`・SeedPak の `--extra-scene` を足した
（§20.10。単体テストは `AndroidPipelineTests` の `PakSceneSeedTests`・`AndroidRunUiTests`・`PackagingCollectorTests` の `ExtraSeedTests`）。
段階D-1 で、端末のアプリとの IPC（`Ipc/`・`AdbClient.ForwardTcpAsync` 等・`AndroidDeviceActions.ConnectIpcAsync`・`AndroidRunRequest.IpcPort`・起動の extra `seed.ipc_port`）と、
エディタ側の行の送受信の共通化（`editor/src/Ipc/IpcLineChannel.cs`）・実行バーの一時停止・再開（`AndroidRunController.TryPause` / `TryResume`・`PlayBarPolicy`）を足した
（§21。単体テストは `AndroidPipelineTests` の `IpcTests`・`AndroidRunUiTests` の `IpcPauseTests`）。

### 20.2 実行先セレクタ

| 行 | 文言（例） | 選べるか | ツールチップ |
|---|---|---|---|
| PC | `PC` | いつも | この PC で実行（従来の Play） |
| Android（自動）（C-3。Android の既定） | `Android（自動）` | いつも（Android を使える環境なら。端末の一覧が無くても） | 決め方（実機 → 起動中のエミュレータ → AVD を起動）と起動する AVD（設定の値か既定の規則。§20.9） |
| 使える端末 | `Pixel_6a（実機）`・`emulator-5554（エミュレータ）`（実機は機種、エミュレータはシリアル） | ○ | 種類・機種・シリアル・ビルドする ABI |
| 使えない状態の端末 | `R58M…（未許可）`・`emulator-5556（応答なし）`・`（権限なし）`・`（接続中）` | × | 状態ごとの理由と対処（`AndroidDeviceSelector.DescribeNotReady`） |
| ABI が合わない端末 | `Old_Phone（ABI 非対応）` | × | 端末の ABI とビルドできる ABI |
| 見えなくなった端末 | `Pixel_6a（未接続）` | ○（C-3 から。選んだまま残し、実行するとエミュレータで実行する） | 今は見えないのでエミュレータで実行する旨と、この端末で実行するための対処 |
| 案内 | `端末を探しています…` / `Android の端末が見つかりません` / `端末の一覧を取れません` / `Android の端末は使えません` | × | 理由と対処（見つからないときは「Android（自動）ならエミュレータを起動して実行できる」旨も） |

- **一覧はコンボを開くたびに `ListDevicesAsync` で取り直す**（取り直している間は「探しています…」の行。前回の一覧は残す）。接続・切断の自動検知はしない。
- **前回の選択はプロジェクトごとに覚える**（`cache/android/run_state.json` の `editor_target`。`pc`・`auto`・端末のシリアル。PC を選んだことも覚える）。
  エディタの起動時は、前回が PC なら PC、Android（自動）なら Android（自動）。前回が端末なら、その端末が見えていて使える状態なら戻し、
  それ以外は **Android（自動）**（前回 Android を使っていたので Android の既定へ。C-2 までは PC だった）。`editor_target` が無ければ最後に Android で
  実行した端末（`last_target`。SeedAndroid で実行した分も入る）を前回とみなす。前回が PC・Android（自動）のときは起動時に adb を呼ばない
  （adb のサーバーを無用に起こさない）。
- 一覧を取り直したときは、いまの選択を保つ。選んでいた端末が見えなくなったら「未接続」の行で選んだまま残す（勝手に別の実行先へ変えない）。
  C-3 からはこの行でも実行でき、実行すると「実機 <シリアル> が見えないためエミュレータで実行します」と Output に 1 行出してエミュレータで実行する（§20.9）。
- **Android を使えない環境**（プロジェクトが無い・エンジンのリポジトリ `runtime/android` が見つからない・Android SDK / adb が無い）では、PC と理由の行だけを出す（エラーにしない）。
  NDK・JDK・cargo・dotnet が無いことはセレクタでは見ない（実行したときに対処付きのエラーとして Output パネルへ出る）。

### 20.3 状態機械とボタン

```
Idle ──実行ボタン──▶ Building ──起動の工程が成功──▶ Running ⇄ Paused ──停止ボタン／アプリの終了──▶ Stopping ──▶ Idle
                        │  └──停止ボタン（ビルドの中止。子プロセスの終了を待つ）─────────────────▶ Stopping ──▶ Idle
                        └──失敗・logcat が自分で終わった（端末が外れた等）───────────────────────────────────▶ Idle
（Running ⇄ Paused は端末のアプリと IPC がつながっているときの実行ボタン。段階D-1・§21）
```

| 状態 | 状態表示 | 実行ボタン | 停止ボタン | 実行先セレクタ | 進捗の表示 |
|---|---|---|---|---|---|
| Idle（実行先が Android） | PC の表示（EDIT 等） | その実行先で実行（選べない端末・PC の実行中は理由付きで無効） | 無効 | 変えられる | なし |
| Building | `ANDROID BUILD...`（黄） | 無効（ビルド中の旨） | **ビルドを中止**（エミュレータの起動待ちも止める。エミュレータは残す） | 変えられない | バー＋`43% [4/7] APK の作成（Gradle）`（工程の前は `準備中…`、エミュレータの起動待ちは `準備中: エミュレータの起動を待っています（45 秒）`） |
| Running | `ANDROID RUN`（水色・Android のアイコン）。端末のアプリと IPC がつながると `ANDROID PLAY`（段階D-1） | 一時停止の絵柄。IPC がつながっていれば押せて一時停止、つながらなければ無効（理由をツールチップに。**段階D-1・§21**） | **端末のアプリを止める** | 変えられない | `Pixel_6a（実機） で実行中` |
| Paused（段階D-1） | `ANDROID PAUSE`（橙・`Icon.Pause`） | 再生の絵柄で押せる（再開） | 端末のアプリを止める | 変えられない | `Pixel_6a（実機） で一時停止中` |
| Stopping | `STOPPING...`（橙） | 無効 | 無効 | 変えられない | `停止しています…` |

- **PC の実行との排他**: PC の実行中（Launching / Play / Pause）は実行先を変えられず、実行ボタン・停止ボタンは PC の Play / Pause / Stop のまま
  （実行先が Android でも PC の一時停止・停止を優先し、Android の実行は始めない）。Android の実行中は PC の Play を始めない（AI ツールの `seed_play` も拒否。
  [editor_mcp.md](editor_mcp.md) 6.4）。PC のランタイムのビルド中・待機中（Building / Idle）でも Android の実行は始められる（Android は別の置き場で作る。
  cargo のロックで順番待ちになることはある）。
- 実行の前に PC の Play と同じ確認をする: アセットフォルダが使えるか、スクリプトの全体コンパイル（エラーがあればエラー一覧とダイアログで止める）。
- **アプリ側の終了**: Running の間、2 秒おきに `pidof <アプリ ID>` で確かめ、2 回続けて見つからなければ「アプリが終わった」として logcat を止めて Idle へ戻す
  （最近のタスクから消した・アプリ情報の強制停止・クラッシュ等でプロセスが終わったとき。直前の logcat で理由が分かる。**戻るキー・ホームではアプリは終わらない**
  〈§14.5。戻るキーはスクリプトへ Escape として渡るだけ〉ので、それでは働かない。段階C-4 に実機で確かめた。§20.14）。Output の終わりの行は
  `端末でアプリが終わったので実行を終えました（最近のタスクから消した・強制停止・クラッシュ等。直前の logcat を確認してください）。`（段階C-4 で
  「戻るキー」を理由から外した。`AndroidRunOutputFormatter.AppExitedText`）。adb の失敗（端末が外れた等）は数えない（そのときは logcat が自分で終わって Idle へ戻る）。
  間隔・回数は `AndroidRunTimings`。
- 実行の指定は `Goal = Run`・プロジェクトのルート・実行先（Android（自動）は `serial = auto`、端末はそのシリアル＋`emulator_fallback`）・
  起動するシーン（§20.10）・エミュレータの AVD（§20.9）・ABI は端末から・Rust は debug・logcat は止めるまで（`AndroidEditorRunRequests.ForPlay`）。
  ツールバーのビルド構成（Debug / Develop / Release）は PC のランタイム用で、Android の実行には効かない。
- 実行（`Goal = Run`）は APK の内容を正とするので、起動の前に SeedAndroid の `push` で置いた DLL の上書き（端末の `files/bin/`）を消す（段階C-4。
  あれば Output に `push した DLL の上書きを解除しました…` の 1 行。§17.7）。
- 進捗・Output の「〜で実行中」「〜でアプリが動いています」は、準備で決まった端末の名前（Android（自動）やエミュレータへの切り替えでは
  `emulator-5554（エミュレータ）` 等）にする。
- Android の APK はディスク上のファイルから作るので、未保存の変更があれば実行の前に尋ねる（§20.11）。起動するシーンは PC の Play と同じ「開いているシーン」（§20.10）。

### 20.4 Output パネルの見方

Android の実行の行は書き手が色と出どころを決めて出す（`AndroidRunOutputFormatter`。色の規約は [editor_ui_style.md](editor_ui_style.md) 7 章。PC の実行の
`[cargo]`・`[Runtime→Editor]` と同じ色分け）。

```
[Android] 実行を始めます: Pixel_6a（実機）（プロジェクト D:\…）。ビルド → インストール → 起動 → logcat   … 水色（実行先の通知）
[Android] [準備] 道具・プロジェクト・端末を確かめ、実行計画を立てます                                         … 黄（工程の見出し）
[Android]       飛ばす libSEED.so のビルド（cargo ndk） — 変更なし                                            … 灰（計画）
[Android] 端末: 2B011…（実機・Pixel_6a）・ABI: arm64-v8a・アプリ ID: com.seedengine.…                        … 水色
[Android]     準備完了: 3 工程を行い、4 工程を飛ばします（0.2 秒）                                            … 灰
[Android] [1/7] libSEED.so のビルド（cargo ndk） — 変更なし                                                  … 灰（飛ばした工程は 1 行）
[Android] [5/7] インストール（adb install） — 飛ばしました（端末に同じ APK が入っている）                        … 灰
[Android] [6/7] 起動 — 止めてから起動し直す                                                                … 黄
[Android]     > Task :app:assembleDebug など（子プロセスの出力）                                             … 黄（error 等を含む行は赤）
[Android]     完了: LaunchState: COLD TotalTime: 1234（2.1 秒）                                              … 灰
[Android] Pixel_6a（実機） でアプリが動いています。logcat を流します（停止ボタンでアプリを止めます）。           … 水色
[logcat] I/SEED: [SEED INIT] …                                                                           … 灰（エンジン）
[logcat] I/DOTNET: [Script] OnStart                                                                      … 灰（出どころ＝ゲーム）
[logcat] E/AndroidRuntime: FATAL EXCEPTION: main                                                         … 赤（重要度 E/F/A）
[Android] 停止しました（端末のアプリを止めました）。                                                         … 水色
[Android] 工程: 3 を行い、4 を飛ばしました（始めてから 95.3 秒）                                              … 灰
```

- logcat の行は `-v threadtime` を「`[logcat] 重要度/タグ: 本文`」に詰める（端末の時刻の代わりに Output パネルの時刻が前に付く）。重要度 E / F / A は赤、W は黄、
  それ以外は本文が error / 失敗 / EXCEPTION を含めば赤（エンジンの標準エラーは重要度 I で届くため）。形に合わない行（`--------- beginning of main`）はそのまま。
- **表示フィルタ「ゲーム」**には C# スクリプトのログ（タグ `DOTNET`、または本文に `[Script`）が入る。それ以外（工程・子プロセス・エンジンの logcat）は「エンジン」。
- 失敗したときは、工程の「失敗: …」（赤）→「エラー（種類）: 説明」（赤）→ 最後に「実行できませんでした（種類）: 何を確かめればよいか」（赤）。
- 全文はエディタのログ（`editor/logs/SEEDEditor.log`）にも残る（色は残らない）。

### 20.5 停止

| いつ | 何をするか | 終わりの行 |
|---|---|---|
| ビルド中（起動の工程の前） | 中断の合図 → 中核が子プロセス（cargo・Gradle・SeedPak・adb）とその子孫を止め、終了を待ってから戻る → Idle。アプリには触らない | `ビルドを中止しました` |
| 起動の工程の途中・実行中 | 中断の合図（logcat を止める＝成功扱い）→ 中核が戻ったら `am force-stop <アプリ ID>`（実行の合図とは別の新しい合図・15 秒の時間切れ）→ Idle | `停止しました（端末のアプリを止めました）` |
| アプリが端末で終わった（最近のタスクから消した・強制停止・クラッシュ等。戻るキー・ホームでは終わらない） | 自動で中断の合図 → Idle（アプリは既に終わっているので止めない） | `端末でアプリが終わったので実行を終えました（最近のタスクから消した・強制停止・クラッシュ等。…）` |
| logcat が自分で終わった | 端末が外れた・adb が終了した等 → Idle | `logcat が終わりました…`（黄） |
| エディタを閉じる | 中断の合図だけ（子プロセスはその場で止まる）。**端末のアプリは止めない**（閉じる操作を adb で待たせない） | — |

アプリを止められなかった（端末が外れた・時間切れ）ときは理由を赤で出して Idle へ戻る。

### 20.6 パッケージ化ウィンドウの Android 出力

「パッケージ化」→ Android で「ビルド開始」を押すと、中核を `Goal = Build`（端末は使わない）で呼び、できた APK / AAB を出力フォルダへ写す。
段階D で配布用（release）を足した（欄の実装は `editor/src/Packaging/PackagingWindow.AndroidRelease.cs`。選んだビルドの種類に関係の無い欄は出さない）。

| 項目 | 内容 |
|---|---|
| 出力 | `{出力フォルダ}/{ゲーム名}/{ゲーム名}-{ABI}-{debug\|release}.{apk\|aab}`（ABI が両方なら `arm64-v8a+x86_64`。配布用の APK と AAB は並べて置ける）。出力フォルダが空なら `<プロジェクト>/build/android`（[packaging.md](packaging.md)） |
| ビルドの種類 | `開発用（デバッグ署名。端末で試す）`（既定）／`配布用（release。アップロード鍵で署名）`（段階D） |
| 形式 | 配布用だけ: `APK`／`AAB（Google Play へ出す）`（段階D） |
| ABI | `arm64-v8a（実機・配布用）`（既定）／`x86_64（PC のエミュレータ用）`／`両方` |
| Rust の最適化 | 開発用だけ: `Release`（`cargo --release`。初回は数分）／`Debug`。配布用は常に Release |
| 署名 | 開発用は**デバッグ署名の APK**（この PC のデバッグ用の鍵。端末へ入れて試せるがストアへは出せない。画面に明記）。配布用は「署名」の欄: キーストア（参照）・別名・パスワード（「保存」でエディタの保護保存。§24.5）・「この場所に新しいキーストアを作る」（確認用のパスワード・証明書の名前）・鍵の保管の注意 |
| アイコン | プロジェクト設定の `android.icon` / `icon_background` の今の値と誤り（設定はプロジェクト設定ウィンドウ。§24.7） |
| Google Play の要件 | 配布用だけ: 「要件を確認」（ビルドをせずに設定と前回の配布物を確かめる）と、ビルドの結果の一覧（合格・知らせ・注意・不合格をアイコンと色で。§24.8） |
| アプリの識別情報・画面の向き | プロジェクト設定の「Android アプリ情報（モバイル）」「画面の向き（モバイル）」（§15・§18） |
| 道具 | 「道具」の欄に SDK・NDK・adb・JDK・cargo・dotnet・keytool・build-tools の見つかった場所か、見つからない理由と対処（赤）を出す（`AndroidToolchainReport`） |
| ログ | ウィンドウの「ビルドログ」に Output パネルと同じ書式で出す。ウィンドウを閉じると作成を中断する（子プロセスごと止める） |

- 以前の「Android NDK パス」の欄と設定（`packaging_settings.json` の `android.ndk_path`）は廃止した（道具の場所は §3 のとおり自動で探す。マシン固有のパスをプロジェクトに書かない）。
  古い設定ファイルの `ndk_path` は読み飛ばし、次の保存で消える。
- 「.NET ランタイムを同梱」の切り替えは Android では出さない（APK には常に同梱 .NET が入る。§17）。収録アセットの設定（「アセット収録」）は SeedPak が同じ
  `packaging_settings.json` から読む。
- エディタの実行・SeedAndroid と置き場を共有するので、入力が同じなら工程を飛ばしてすぐ終わる（ABI を変えると作り直す。§19）。

### 20.7 確認結果（2026-09-25）

| 項目 | 結果 |
|---|---|
| 単体テスト `editor/tests/AndroidRunUiTests` | 47 / 47（実行先の一覧・前回の選択の復元と保存・プレイバーの表（PC の従来の表を含む）と排他・状態機械・偽の中核での停止／ビルド中の停止／アプリの終了／失敗／logcat の終わり／閉じる・Output の文言と色・従来の色分け・環境の判断・パッケージ化の名前） |
| 単体テスト `editor/tests/AndroidPipelineTests` | 53 / 53（pidof の解析・`editor_target` の往復を追加） |
| エディタのビルド | エラー 0・警告は変更前と同じ 25 件（行番号のずれだけ） |
| 中核の呼び出し（UI 抜き・一時のコンソール） | `ListDevicesAsync` は端末 0 台で空の一覧（59 ms）→ セレクタは PC と「Android の端末が見つかりません」。`IsAppRunningAsync` / `StopAppAsync` は無い端末で理由付きの失敗。`Goal = Build`（x86_64）は `AndroidPrepared`（端末なし・`com.seedengine.runtime`・x86_64）→ .so と同梱 .NET を「変更なし」で飛ばし、SeedPak 4.4 秒・Gradle 14.8 秒、合計 19.3 秒で APK 64.0 MB を `ProbeGame-x86_64-debug.apk` として写した。進捗 0.25 → 0.50 → 0.75 → 1.00、イベントは 4 本のスレッドから届いた |
| 実物の XAML の組み立てと描画（エディタは起動しない一時のプローブ。VersionControlPanelPreviewProbe と同じ組み方） | `MainWindow` / `PackagingWindow` の組み立てで例外なし。プレイバー（PC の EDIT・実行先が Android の EDIT・実行先が Android のまま PC が PLAY・未許可／未接続の端末を選んだまま・Android のビルド中／実行中／停止後）、コンボの行（未接続の選択行・無効の行・案内の行）、パッケージ化ウィンドウの Android 欄を PNG に描いて確かめた。実物の adb で一覧を取り直す経路（端末 0 台 → 選んでいた端末は「未接続」で残り、実行ボタンは理由付きで無効）と、PC を選ぶ経路（`[Android] 実行先: PC` のログ）も通した |
| 端末での実行（Run・停止・アプリの終了の検知・Output） | **未実施**（作業中は端末が無く、メモリ不足のためエミュレータも起動しなかった。監督役が端末で確かめる。backlog） |
| エディタの画面（ホバー・ドロップダウンの開閉・ツールチップ・Output パネル） | **未確認**（エージェントはエディタを起動しない。利用者が目視で確かめる） |

### 20.8 制限・持ち越し（詳細は [backlog.md](backlog.md) の「Android」節）

- 端末での一気通貫（実行・停止・アプリの終了の検知・logcat の色分け）は端末で未確認。
- pak とスクリプトは SeedPak を子プロセスで呼ぶまま（エディタの中の `AssetPakBuilder` / `ScriptPackager` を直接呼ぶ高速化はしていない）。DLL だけを送る `Push` の
  高速経路もエディタには出していない（SeedAndroid の `push` は使える。実行中に `.cs` を保存したときは §23 の差し替えが DLL だけを送る）。
- 端末の一覧はコンボを開いたときだけ取り直す（`adb track-devices` による接続・切断の自動検知は無い）。
- エディタの実行は Rust を debug で作る（ツールバーのビルド構成と連動しない）。開始シーンからしか起動できない・未保存の変更は警告だけ、は段階C-3 で直した（§20.10・§20.11）。
- エディタを閉じても端末のアプリは止めない。AI ツールの `seed_play` は PC だけを扱う（Android の実行の MCP ツールは無い）。
- Gradle のデーモンは実行の後も残る（Gradle の既定。`gradlew --stop` で止まる）。

### 20.9 Android（自動）とエミュレータの自動起動（段階C-3）

**実行先の決め方**（純粋な処理 `Adb/AndroidDeviceSelector.DecideForRun`。単体テスト `EmulatorAndSceneTests`）

| 実行先 | 中核への指定 | 決め方 |
|---|---|---|
| `Android（自動）` | `serial = "auto"` | 1. 使える実機（前回使った実機を優先。前回のものが無く 2 台以上なら選ばずにエラー＝私物の端末へ勝手に入れない）→ 2. 起動中のエミュレータ（前回使ったものを優先、無ければ一覧の先頭）→ 3. 起動の途中のエミュレータ（adb の状態 offline・connecting・authorizing。完了を待つ）→ 4. どれも無ければ AVD を起動して待つ。使えない状態の実機（未許可等）は理由付きの警告を出して飛ばす |
| 特定の端末（`Pixel_6a（実機）` 等・`（未接続）` の行） | `serial = <シリアル>`・`emulator_fallback = true` | 見えていて使えればそれ。起動の途中のエミュレータなら完了を待つ。つながっているが使えない実機（未許可等）は理由付きのエラー（許可すれば使えるので切り替えない）。**見えなければ**「実機 <シリアル> が見えないためエミュレータで実行します。」と警告の 1 行を出し、上の 2〜4 へ |
| SeedAndroid の `--serial <シリアル>` / 指定なし | 従来どおり（`emulator_fallback` は立てない） | 見えなければエラー／使える端末がちょうど 1 台ならそれ（§5.1） |

- エミュレータを起動するのは端末の工程（インストール・起動・転送）を行うときだけ。`build` のために起動しない。
- エミュレータは実機より後。実機が USB につながっていれば、`Android（自動）` は実機で実行する（前回使った実機が優先）。

**エミュレータの起動と待ち合わせ**（`Emulator/AndroidDeviceProvisioner`。外の世界は `IEmulatorHost`、時間は `WaitClock` 越しに触り、単体テストは偽物）

1. AVD を決める（`Emulator/EmulatorAvdChooser`）: エディタの設定 `android.emulator_avd`（SeedAndroid は `--avd`）→ 設定なしなら `emulator -list-avds` に
   `seed_pixel6_api35` があればそれ → 無ければ一覧の先頭。設定した名前が一覧に無ければ起動せずにエラー（別の AVD を勝手に起動しない）。一覧が空でもエラー。
2. `emulator -avd <AVD> -gpu host` を**切り離して**起動する（`Processes/DetachedProcess`: CreateProcessW で、ハンドルを継承させない・コンソールの窓を出さない・
   Ctrl+C を受けない・呼び出し元のジョブから外れる。quick boot は AVD の既定のまま）。.NET の `Process.Start` はハンドルを継承させるため、
   SeedAndroid の出力をパイプへつないでいるとエミュレータが書き口を握り続けて読み手が終わらない、という問題を避ける。
3. 起動前から動いていたエミュレータを除き、新しく adb に現れた `emulator-*` を `adb -s <シリアル> emu avd name` で照合して自分が起動したものを見分ける
   （別の AVD のものは「使いません」の 1 行を出して以後聞かない。コンソールがまだ応答しなければ次の確認で聞き直す）。
4. adb の状態が `device` かつ `getprop sys.boot_completed` が `1` になるまで待つ（`device` になっても起動の途中は install が失敗するため）。
   動いているエミュレータを使うときも、この完了だけは確かめる（終わっていれば待たない）。
5. 確認は 2 秒おき、10 秒おきに Output へ「エミュレータの起動を待っています（N 秒・状態）…」、ツールバーの進捗へ「準備中: エミュレータの起動を待っています（N 秒）」
   （`AndroidProgressChanged`。工程は準備）。時間の決まりは `Emulator/EmulatorTimings`（起動待ちの上限 300 秒）。

| 起きたこと | 振る舞い |
|---|---|
| 300 秒以内に起動が終わらない | エラー（種類「端末」）: 「…起動が 300 秒以内に終わりませんでした（最後の状態）。エミュレータはそのまま残します。起動が終わってから、もう一度実行してください」 |
| emulator.exe が 0 以外の終了コードで終わった | すぐエラー（終了コードと、Device Manager から同じ AVD を起動して原因〈メモリ不足・仮想化支援〉を確かめる旨）。0 で終わったら本体へ引き継いだとみなして待ち続ける |
| 待っていたエミュレータが adb から消えた | エラー（閉じられた・起動に失敗した） |
| 停止ボタン・Ctrl+C（待っている途中） | 待つのをやめる（中断）。起動を始めたエミュレータは**止めない**（次の実行で「起動中のエミュレータ」として使う） |
| emulator.exe が無い | エラー（種類「道具」）: SDK Manager で Android Emulator を入れるか実機をつなぐ |

- **起動したエミュレータは実行の後も止めない**（起動に時間がかかるので、2 回目以降の実行で使い回す）。止めるときはエミュレータの窓を閉じるか
  `adb -s <シリアル> emu kill`。PC のメモリが少ないときは、使い終わったら閉じる（AVD `seed_pixel6_api35` は RAM 2 GB）。
- **AVD の設定**（エディタ）: `editor/settings/editor_preferences.json` の `"android": { "emulator_avd": "<AVD>" }`（`Settings/AndroidEditorPreferences`）。
  設定の画面はまだ無い（backlog）。手で書くときはエディタを閉じてから（開いている間は環境設定の保存で上書きされる）。
  `Android（自動）` の行のツールチップに、使う AVD（設定の値、または既定の規則）を出す。SeedAndroid はこの設定を読まない（`--avd` で指定）。

Output の例（端末が無い状態から `Android（自動）` で実行）:

```
[Android] 実行を始めます: Android（自動）（プロジェクト D:\…）。ビルド → インストール → 起動 → logcat
[Android] [準備] 道具・プロジェクト・端末を確かめ、実行計画を立てます
[Android]     起動するシーン: scenes/Stage2.scene（am start の extra seed.scene で渡す）
[Android]     つながっている実機が無いため、エミュレータで実行します。
[Android]     使える端末が無いため、AVD からエミュレータを起動します。
[Android]     AVD: seed_pixel6_api35（指定なし: 開発用の既定の AVD seed_pixel6_api35）
[Android]     エミュレータを起動します: emulator -avd seed_pixel6_api35 -gpu host（スナップショットが無いと 1〜3 分かかります）
[Android]     起動したエミュレータ emulator-5554（AVD seed_pixel6_api35）が adb に現れました（offline）。
[Android]     エミュレータの起動を待っています（12 秒・adb の状態 offline）…
[Android]     エミュレータの起動を待っています（22 秒・起動の完了（sys.boot_completed）を待っています）…
[Android]     エミュレータ emulator-5554 の起動が終わりました（33 秒待ちました）。
[Android]     端末: emulator-5554（エミュレータ・sdk_gphone64_x86_64）
[Android] 端末: emulator-5554（エミュレータ・sdk_gphone64_x86_64）・ABI: x86_64・アプリ ID: com.seedengine.…
…
[Android] emulator-5554（エミュレータ） でアプリが動いています。logcat を流します（停止ボタンでアプリを止めます）。
```

### 20.10 起動するシーン（開いているシーンから。段階C-3・C-4）

エディタの Android の実行は、PC の Play と同じく**開いているシーン**から起動する（`AndroidRun/AndroidRunSceneChoice`）。
段階C-4 から、**シーンマネージャに登録していないシーンも pak の収録の起点に足して APK に入れる**ので、開いている未登録のシーンからも起動できる
（下の「pak に入るシーン」）。

| 状態 | 起動するシーン | Output |
|---|---|---|
| 「開始シーンからプレイ」がオン | 開始シーン（`project_settings.json` の `start_scene`） | `起動するシーン: 開始シーン（「開始シーンからプレイ」がオン）` |
| 開いているシーンがアセットフォルダの中 | そのシーン（アセットルートからの相対パス。例 `scenes/Stage2.scene`） | `起動するシーン: scenes/Stage2.scene（開いているシーン。PC の Play と同じ）` |
| まだファイルに保存していない新規シーン | 開始シーン（APK に入らないため） | 警告の色で理由を 1 行 |
| アセットフォルダの外のシーン | 開始シーン（APK に入らないため） | 警告の色で理由を 1 行 |

**受け渡し**（中核 → 端末）

```
AndroidRunRequest.ScenePath（相対パス・assets://…・アセットルート内の絶対パス。Project/AndroidScenePath で相対パスに揃える。外・.. は指定の誤り）
  └ 起動の工程（Steps/LaunchStep）: adb shell am start -W -n <ID>/com.seedengine.runtime.MainActivity --es seed.scene '<相対パス>'
      （adb shell は引数をつないで端末のシェルへ渡すので、値は単一引用符で囲む。' は '\'' にする。空白・日本語もそのまま届く）
      └ MainActivity.onCreate の最初（super.onCreate より前。TMPDIR / HOME の設定と同じ位置）: 「seed.」で始まる文字列の extra を
        JSON 1 つにまとめる（{"scene":"scenes/Stage2.scene"}）→ nativeSetLaunchOptions(UTF-8 の byte[])。**デバッグ版の APK だけ**
        （配布版では他のアプリの Intent で途中のシーンへ飛べないよう何も渡さない）
          └ libSEED.so の jni_exports.rs → launch_options.rs（android_main まで預かる。byte[] は jni_env.rs が JNIEnv の関数表で読む）
              └ launch.rs: engine::platform::launch_options::choose_scene（純粋な処理・cargo test 付き）で決め、
                LaunchArgs.scene_path = "assets://<相対パス>"（エンジンの load_play_scene が開始シーンの代わりに読む）
```

- 端末は、エンジンが読むのと同じ順（APK の pak のエントリ → APK の PAK 外 `assets/seed/assets/` → アプリ専用フォルダの `assets/`。開発用の
  `--assets-dir` の APK はアプリ専用フォルダだけ）でシーンがあるかを確かめ、**どこにも無ければ logcat に警告を出して開始シーンで起動する**。
  判断は端末に 1 本化し、PC 側はそのまま渡したうえで理由を Output に出す:
  - 準備: プロジェクトのアセットに無ければ「シーン X がプロジェクトのアセット（…）にありません。端末は警告を出して開始シーンで起動します」
  - 起動の直前（保険。段階C-4 で文言を変えた）: ディスクにはあるのに APK の pak に入っていなければ（`Project/PakEntryIndex` で置き場の pak の表を引く）
    「シーン X が APK の pak に入っていません（理由と直し方）。端末は開始シーンで起動します」。理由は、通常は
    「起動するシーンは pak の収録の起点に入れています〈登録シーン、未登録なら SeedPak の --extra-scene〉が、収録に失敗しました。pak とスクリプトの工程のログ…を確かめてください」、
    `--skip-gradle` なら「前回の pak のまま」、`push` なら「APK の pak は作り直しません。run で実行すると…作り直します」（`Steps/LaunchStep.SceneNotInPakMessage`）
- **pak に入るシーン**（段階C-4 で変更）: pak はプロジェクト設定の開始シーン・シーン一覧（`scenes[]`）から参照をたどって作る（`Packaging/Collect/AssetCollector`。
  パッケージ化ウィンドウ・SeedPak と同じ）。**起動するシーンがシーンマネージャに未登録なら、準備でそのシーンを収録の起点に足す**
  （`Project/AndroidPakSceneSeeds` → `AndroidPipelineContext.PakExtraScenes` → pak とスクリプトの工程が SeedPak を `--extra-scene <相対パス>` 付きで呼ぶ
  → `AssetCollector.Collect(extraSeeds)`。登録シーンと同じく、そのシーンから参照をたどれるモデル・スクリプトの参照先も入る。[packaging.md](packaging.md) §2・§10.2）。
  C-3 までは未登録のシーンは pak に入らず、開始シーンで起動していた。エディタの実行（`AndroidEditorRunRequests.ForPlay` の `ScenePath`）も
  SeedAndroid の `--scene` も、中核の準備の同じ判断を通る。

  | 起動するシーン | pak の収録の起点に足すか | 準備の Output |
  |---|---|---|
  | 指定なし（開始シーン・「開始シーンからプレイ」） | 足さない | — |
  | 登録シーン（`start_scene`・`scenes[].path`。`assets://`・相対・絶対パス・区切り・大小文字の違いは同じシーンとみなす） | 足さない（既に起点） | — |
  | 未登録のシーン | **足す** | `シーン X はシーンマネージャに未登録のため、pak の収録の起点に足します（SeedPak --extra-scene。…）` |
  | アセットルートに無いシーン | 足さない（入れようが無い） | 上の「プロジェクトのアセットにありません」の警告 |
  | 開発用の `--assets-dir`（pak の無い APK） | 足さない（アセットはフォルダごと送る） | — |

- **シーンを変えたときの工程**: 足すシーンは pak の指紋（`Plan/AndroidStepFingerprints.PackageContent`）に入る（足さないときは材料に入れないので、
  C-3 までと同じ指紋）。そのため
  - 登録済みのシーン（か開始シーン）どうしで変えても工程は作り直さない（起動し直すだけ）。
  - 未登録のシーンへ切り替えた**最初の実行**で pak → APK（Gradle）→ インストールをやり直す。同じシーンのまま実行し直せば飛ばす。
  - 登録済みのシーンへ戻したときも、足さない pak に作り直す（開発中のシーンが残った pak を、パッケージ化ウィンドウの APK〈シーンを渡さない＝登録シーンだけ〉に使わないため）。
  - `--skip-gradle`（APK の作成を飛ばす）・`push` は pak を作り直さないので、前回の pak に無いシーンは開始シーンで起動する（起動の直前の警告）。
- logcat（タグ SEED）: `[SEED LAUNCH] 起動オプションを受け取りました: {"scene":"scenes\/Stage2.scene"}` → `起動するシーン: assets://scenes/Stage2.scene（起動オプションの指定。…）`
  → `[SEED INIT] load_play_scene start  scene_path=Some("assets://scenes/Stage2.scene")`。無いシーンは
  `起動オプションのシーン scenes/NoSuch.scene が見つかりません（pak・APK・アプリ専用フォルダのどれにもありません）。開始シーンで起動します`。
- 毎回アプリを止めてから起動するので extra は必ず新しいプロセスの onCreate に届く。ランチャーから起動したときは extra が無いので開始シーン。
- 起動オプションの JSON の書式と、知らないキーを読み飛ばすこと・オブジェクトでない JSON を誤りにすることはエンジンの `platform/launch_options.rs` が正典
  （Java は名前を見ずに `seed.*` をすべて渡すので、オプションを足すときは Rust の `LaunchOptions` と C# の `AndroidRuntimeContract` だけを直す）。

### 20.11 未保存の変更（段階C-3）

PC の Play は保存しなくても編集中の状態で動く（埋め込み Play はその場で Play 化、ウィンドウ Play は一時ファイル `_play_temp.scene`）が、
Android は保存済みのファイルから APK（pak）を作る。そこで実行ボタンを押したときに未保存の変更があれば、エディタの他の確認（シーンの切り替え・終了の
「未保存の変更があります。…保存しますか？」）と同じ言い回しで尋ねる（文言は `AndroidRun/AndroidUnsavedChangesPrompt`、窓は `Dialogs/ActionChoiceWindow`）。

| ボタン | 振る舞い |
|---|---|
| **保存して実行**（主操作・Enter） | Ctrl+S と同じ保存（アクターの編集中はアクター、名前の無い新規シーンは「名前を付けて保存」）。保存は非同期なので、保存が終わってから実行を始める（`OnSaveCompleted` → `ContinuePendingAndroidRun`）。保存したファイルの指紋が変わるので pak を作り直す。保存に失敗・読み取り専用・ロック等で保存できなければ実行を取りやめ、その旨を Output に 1 行 |
| 保存せず実行 | 保存済みの内容で実行する（Output に「保存せずに実行します…保存していない変更は Android の実行に含まれません」の警告を 1 行） |
| キャンセル（Escape・閉じる） | 何もしない（Output に取りやめた旨を 1 行）。ヘッドレス起動では常にこれ（`EditorDialogs.ShowActionChoice`） |

- 事前の確認の順: アセットフォルダ → スクリプトのコンパイル → 未保存の変更 → 実行（PC の Play と同じ前の 2 つ）。保存が終わった後の実行でも前の 2 つをやり直す。
- 保存が終わるまでの間に実行先が PC へ変わった・PC の実行が始まった等なら、プレイバーの判断（`PlayBarPolicy`）に従って始めない。

### 20.12 確認結果（段階C-3・2026-09-25）

エミュレータ（AVD `seed_pixel6_api35`・x86_64・RAM 2 GB・quick boot）と、段階B の確認用プロジェクトの写しにシーンを 2 つ足したもの
（`Main`＝アクタ 2・`Second`＝3・`日本語 シーン`＝4）で、SeedAndroid から確かめた。**作業中は実機 Pixel 6a が USB につながっていた**ため、
`--serial auto` で走らせると実機を選んでしまう（規則どおり）。実機には触らない約束なので、エミュレータの自動起動は同じ経路の
「選んだ端末が見えなければエミュレータ」（設定 JSON の `serial` に存在しないシリアル＋`emulator_fallback: true`）で確かめた
（`auto` と違うのは、実機を探す段だけ。そこは単体テストで確かめた）。

| 項目 | 結果 |
|---|---|
| エミュレータの自動起動（端末・エミュレータとも動いていない状態から） | 「実機 SEED-C3-NOT-CONNECTED が見えないためエミュレータで実行します。」→ AVD `seed_pixel6_api35`（既定）→ `emulator -avd seed_pixel6_api35 -gpu host` を切り離して起動 → `emulator-5554` を AVD 名で見分け（offline）→ 待っている旨を 10 秒ごと → 起動完了まで **33 秒**。続けてインストール 43.9・起動 3.3・logcat 30 秒（全体 110.4 秒）。emulator.exe は SeedAndroid が終わった後も qemu の親として動き続けた（`adb emu kill` で止めると約 18 秒で qemu ごと終了） |
| 開いているシーン（`Second`）から起動 | logcat: `[SEED LAUNCH] 起動オプションを受け取りました: {"scene":"scenes\/Second.scene"}` → `起動するシーン: assets://scenes/Second.scene` → `load_play_scene start scene_path=Some("assets://scenes/Second.scene")` → `done actors=3`。スクリプトも動いた（`[PROBE v2] OnStart`） |
| 起動中のエミュレータの使い回し | 2 回目以降は「起動中のエミュレータ emulator-5554 で実行します。」で新しく起動せず、工程を飛ばして 11.7〜18.1 秒 |
| 日本語と空白を含むシーン（`scenes/日本語 シーン.scene`） | `am start … --es seed.scene 'scenes/日本語 シーン.scene'` のまま端末の JSON に届いた（Windows → adb → 端末のシェル → Java → JNI → Rust で化けない）。シーンマネージャに未登録の間は pak に入らず、PC の「pak に入っていません」の警告と端末の警告で開始シーン（actors=2）。登録すると pak・APK を作り直して（SeedPak 6.6・Gradle 22.6・install 1.8 秒）`load_play_scene start scene_path=Some("assets://scenes/日本語 シーン.scene")` → `actors=4` |
| 無いシーン（CLI `--scene scenes/NoSuch.scene --serial emulator-5554`） | 準備で「プロジェクトのアセットにありません」の警告 → 端末の logcat に `W SEED: 起動オプションのシーン scenes/NoSuch.scene が見つかりません（…）。開始シーンで起動します` → `scene_path=None`・`actors=2` |
| シーンの指定なし | JSON は `{}`・`起動するシーン: 開始シーン（…起動オプションにシーンの指定なし）`・`scene_path=None` |
| 単体テスト | `AndroidPipelineTests` 71 / 71（決め方の規則・偽の adb と仮の時計での起動待ち〈見分け・時間切れ・起動直後の終了・中断・動いているエミュレータ〉・AVD・シーンのパス・am start の extra と引用・コマンドラインの引用〈CommandLineToArgvW で往復〉・切り離した起動〈cmd /c exit 3 の終了コード〉・pak の表・SeedAndroid の引数）。`AndroidRunUiTests` 54 / 54（自動の行・復元・未接続の行・進捗の文言・状態機械の実行先の表示・シーンの決め方・未保存の確認の文言）。`ProjectSystemTests` 55 / 55。Rust の `cargo test --lib`（`platform::launch_options` の JSON の往復・シーンの判断、`PakReader::contains`）18 / 18 |
| ビルド | エディタ（別の出力先・`--no-incremental`）エラー 0・警告 25（変更前と同じ）。SeedAndroid 警告 0。Rust の `cargo build`（PC）と libSEED.so（x86_64）は変更したファイルに警告なし |
| 未保存の確認のダイアログ | 実物の部品（`ActionChoiceWindow`・`SeedDialogTheme`・共通ボタン書式）で組み立てて PNG に描いて確かめた（本文の折り返し・主操作の色・ボタン 3 つ）。エディタの画面での操作（保存して実行 → 保存の完了 → 実行）は**未確認**（エージェントはエディタを起動しない） |
| エディタの実行先セレクタ・Output の表示 | **未確認**（判断はすべて単体テスト。画面は利用者が確かめる） |

### 20.13 制限・持ち越し（段階C-3。詳細は [backlog.md](backlog.md) の「Android」節）

- ~~**シーンマネージャに登録していないシーンは、開いていても端末では開始シーンで起動する**~~ → 段階C-4 で、起動するシーンが未登録なら
  pak の収録の起点に足すようにした（§20.10。未登録のシーンへ切り替えた最初の実行は pak・APK・インストールをやり直す）。
- ~~`Android（自動）` の「実機を優先」する経路（実機がつながっているときの選び方）は単体テストだけで確かめた~~ → 段階C-4 で SeedAndroid の
  `--serial auto` を実機 Pixel 6a で確かめた（前回使った実機・つながっている実機の両方。§20.14）。エディタの画面からの実行は未確認のまま。
- AVD の設定の画面が無い（`editor_preferences.json` を手で書く）。SeedAndroid はエディタの設定を読まない（`--avd`）。
- ~~エミュレータの一時停止・Android の実行の一時停止は無い~~ → 段階D-1 で、IPC を TCP（adb forward）にして実行バーから一時停止・再開できるようにした（§21）。
- 起動したエミュレータの標準出力（エミュレータ自身のログ）は捨てている（見えないコンソールへ）。起動に失敗したときは終了コードと Device Manager での確認を案内するだけ。
- `Android（自動）` は実機が 2 台以上つながっていて前回のものが無いと選ばない（エラー）。エミュレータは一覧の先頭を選ぶ。
- エミュレータの起動待ちの間の「状態」は adb の状態と `sys.boot_completed` だけ（起動画面の進み具合までは分からない）。

### 20.14 確認結果（段階C-4・実機 Pixel 6a・2026-09-25）

実機 Pixel 6a（`2B011JEGR02535`・arm64-v8a・Android 16）を USB でつないだ状態で、SeedAndroid から確かめた（エミュレータは起動していない）。
各手順の前に前面の窓（`dumpsys window` の `mCurrentFocus`）がランチャー・ロック画面・自分のアプリのどれかであることを確かめた（全手順で該当）。
プロジェクトは段階C-1 の確認用（`proj_probe`＝最小構成＋確認用スクリプト）、シーンの確認は段階C-3 の写しを「`Main` だけ登録・`Second` / `Third` /
`日本語 シーン` は未登録」にしたもの（`Third` は自分だけが参照するモデル `ThirdOnly.glb` を持つ）、識別情報は段階C-1 の `proj_ident`。
Gradle は各手順の後に `gradlew --stop` で止めたので、Gradle の時間はデーモンの起動を含む。

| 項目 | 結果 |
|---|---|
| `run`（1 回目・arm64 のビルドから） | 7 工程すべて: libSEED.so（arm64・449.9 MB）105.8・SeedPak 21.2・同梱 .NET（arm64 へ組み替え）13.2・Gradle 86.0（APK 60.5 MB）・install 4.1・起動 1.2（`LaunchState: COLD TotalTime: 998`）・logcat 30 秒、**合計 261.9 秒**。logcat: APK 内の pak（非圧縮）で起動 → スクリプトは `apk:seed/bin/` → `load_play_scene done actors=2` → `[PROBE v2] OnStart runtime=.NET 10.0.12 rid=linux-bionic-arm64` → NullReference / DivideByZero を捕まえた → 毎秒の `[PROBE v2]`（約 18.8〜19.0 fps）。保存した logcat は UTF-8 |
| `run`（2 回目・変更なし） | .so・pak・同梱 .NET・APK・インストールの 5 工程を「変更なし」「端末に同じ APK が入っている」で飛ばし、起動（TotalTime 758）と logcat だけ。**31.4 秒**（うち logcat 30 秒） |
| `push` / `stop` | `push`: DLL の転送（5 ファイル 9.1 MB）4.0・起動 0.7・logcat 20 秒（合計 25.0 秒）、`スクリプトの置き場: /data/user/0/com.seedengine.runtime/files/bin/` から `[PROBE v2] OnStart`。`stop`: `pidof` が空・`IsAppRunningAsync` = False・前面はランチャーへ（確認の後、送った DLL は `run-as … rm -rf files/bin` で消した） |
| `--serial auto` | 「実機 Pixel_6a（2B011JEGR02535） で実行します（前回使った実機）。」（実行の記録が無いプロジェクトでは「…（つながっている実機）。」）。エミュレータは起動せず（`emulator` / `qemu` のプロセス無し・adb の一覧は実機だけ）、工程を飛ばして 16.1 秒 |
| `--scene` 登録済み（`scenes/Main.scene`） | 収録の起点は足さない。プロジェクトを切り替えたので pak 3.5・Gradle 12.2・install 6.0（合計 37.7 秒）。置き場の pak は 5 エントリ（`Third`・`ThirdOnly.glb`・`日本語 シーン` は無い）。端末は `起動するシーン: assets://scenes/Main.scene` → `actors=2` |
| `--scene` 無いシーン（`scenes/NoSuch.scene`） | 準備で「プロジェクトのアセットにありません」の警告。足すシーンは無いので指紋は登録済みのときと同じで、ビルドの工程をすべて飛ばした（16.4 秒）。端末は `W SEED: 起動オプションのシーン scenes/NoSuch.scene が見つかりません…。開始シーンで起動します` → `scene_path=None`・`actors=2` |
| `--scene` 未登録（`scenes/Third.scene`） | 準備「シーン scenes/Third.scene はシーンマネージャに未登録のため、pak の収録の起点に足します…」→ `SeedPak … --scripts --extra-scene scenes/Third.scene` →「追加の起点: scenes/Third.scene」「収録: 7 ファイル / 6.1 MB」（`Third.scene` と、そこからだけ参照される `ThirdOnly.glb` が入った）→ Gradle・install（合計 29.4 秒）。端末は `load_play_scene start scene_path=Some("assets://scenes/Third.scene")` →「初回ロード: assets://models/ThirdOnly.glb」→ `actors=3`・`[PROBE v2] OnStart`。「pak に入っていません」の警告は出ない |
| 同じ未登録のシーンでもう一度 | 5 工程を飛ばした（16.3 秒）。端末は `Third`（`actors=3`） |
| `--scene` 未登録・日本語と空白（`scenes/日本語 シーン.scene`） | `SeedPak … --extra-scene "scenes/日本語 シーン.scene"`（子プロセスの引数で化けない）→ pak 6 エントリ → Gradle 12.3・install 6.4（合計 38.8 秒）→ 端末は `scene_path=Some("assets://scenes/日本語 シーン.scene")` → `actors=4` |
| アプリの識別情報（`proj_ident`） | `aapt2 dump badging`: `package: name='com.seedengine.c1test' versionCode='7' versionName='0.8'`・`application-label:'C1 Space Test'`。端末の `dumpsys package` も 7 / 0.8、前面の窓は `com.seedengine.c1test/com.seedengine.runtime.MainActivity`、データの置き場 `/data/user/0/com.seedengine.c1test/files`、`[PROBE v2] OnStart`（合計 42.1 秒）。確認の後 `adb uninstall com.seedengine.c1test`（Success） |
| 端末の戻るキー（`input keyevent KEYCODE_BACK`。自分のアプリが前面のときに送った） | **アプリは終わらない**（§14.5 の仕様どおり）。logcat は `[SEED KEY] pressed logical=Named(BrowserBack)…` → `[SEED KEY FRAME] … Escape:down+up`、同じ pid のまま前面に残り、`AndroidDeviceActions.IsAppRunningAsync` は 2 秒おき 5 回とも True。False になるのはプロセスが終わったとき（`stop`＝`am force-stop` の後に確かめた）。エディタの「端末でアプリが終わったので実行を終えました」は、戻るキーでは起きない（スクリプトからアプリを終える API も無い。backlog）。これを受けて、その終わりの行の理由から「戻るキー」を外した（追加修正。§20.3） |
| 追加修正: `push` の後の `run` で APK の `bin/` が使われる（`run` が起動の前に `files/bin/` を消す。§17.7） | **未実施**（端末の前面が利用者のほかのアプリだったため。14:32〜14:49 の 15 分待ったがランチャーに戻らなかった。`fix2_wait_launcher.log`）。単体テスト（消す・消さないの判断・run-as の引数・出力の読み方）だけで確かめた。手順は「`push --project <proj_probe>` → `run --project <proj_probe>` の Output に `push した DLL の上書きを解除しました…`・logcat に `スクリプトの置き場: apk:seed/bin/`・`run-as … ls files` に `bin` が無い」 |
| 単体テスト | `AndroidPipelineTests` 80 / 80（足すかの判断・登録シーンの読み取り・指紋・SeedPak の引数・SeedAndroid の `--scene` から判断まで・警告の文言、追加修正の push の上書きの解除 3 件を追加）。`AndroidRunUiTests` 55 / 55（エディタの `ForPlay` の `ScenePath` から判断まで・アプリの終了の文言）。`PackagingCollectorTests` 48 / 48（`Collect(extraSeeds)`・`AssetPakBuilder` の extraSeeds・空や登録済みなら同じ PAK・欠落の報告・SeedPak の `--extra-scene`）。`ScriptPrecompileTests` 15 / 15。`TemplateImportTests` 21 / 21 |
| ビルド | エディタ（別の出力先）エラー 0・警告 25（変更前と同じ。変更したファイルに警告なし）。SeedPak・SeedAndroid は警告 0 |

- 最後の状態: `com.seedengine.runtime`（既定の識別情報・`proj_probe` の APK・版 1 / 1.0）を入れて止めた（`pidof` 空・前面はランチャー）。送った DLL は消した。
  別 ID のアプリ（`com.seedengine.c1test`）はアンインストールした。Gradle のデーモンは止めた。エミュレータは起動していない。
- シーンの確認では、未登録の `Second` も pak に入っていた（`Second` を参照するのは `runtime/src/engine/platform/launch_options.rs` の**テストコード**の
  `"assets://scenes/Second.scene"` で、収録の「エンジン内蔵参照」の走査がそれを拾う。段階C-4 とは別の既存の振る舞い。backlog）。そのため未登録のシーンの確認は
  どこからも参照されない `Third` で行った。

### 20.15 制限・持ち越し（段階C-4。詳細は [backlog.md](backlog.md) の「Android」節）

- 登録済みか未登録かは `project_settings.json` の `start_scene`・`scenes[]` だけで決める。**参照でたどれるので元々 pak に入る未登録のシーン**
  （他のシーン・スクリプトの `assets://` から参照されるもの）でも起点に足すので、そのシーンへ切り替えると pak を作り直す（中身は同じ。Gradle・install も走る）。
- 収録の設定が全ファイル同梱（`include_all_files`）でも、足すシーンを指紋に入れるので、未登録のシーンへ切り替えるたびに作り直す（pak の中身は同じ）。
- 登録済みのシーン（か開始シーン）へ戻すと、足さない pak に作り直す（パッケージ化ウィンドウの APK に開発中のシーンを残さないため。仕様）。
- `--skip-gradle`・`push` は pak を作り直さないので、前回の pak に無いシーンは開始シーンで起動する（起動の直前に理由付きの警告）。
- エディタの画面からの Android の実行（実行先セレクタ・Output・停止・未保存の確認）は、実機でも**未確認**（エージェントはエディタを起動しない）。
- 追加修正（§20.3・§17.7）: `run` は起動の前に `push` の上書き（`files/bin/`）を消すようにした。`install` だけ（起動しない）では消さないので、
  その後ランチャーから起動すると `push` の DLL で動く（→ 段階D で、APK を入れ直したときも消すようにした。§17.7・§23.4）。アプリの終了の文言から「戻るキー」を外した（「最近のタスクから消した」でプロセスが終わることは、
  私物の端末のシステムの画面を操作しない約束のため実機では確かめていない〈AOSP の既定の振る舞い〉）。

---

## 21. 実行バーからの一時停止・再開（エディタとの IPC を TCP で。段階D-1・2026-09-25）

Android の実行中も、**PC の Play と同じ実行バー**（実行ボタン＝一時停止／再開・停止ボタン）で端末のゲームを一時停止・再開できるようにした。
PC の Play と同じ IPC の命令（1 行 1 命令の文字列。`PAUSE` / `RESUME` / `SCREENSHOT:` …。書式の正典は `runtime/src/engine/core/app_base/ipc.rs`）を、
名前付きパイプの代わりに **adb forward 越しの TCP** で送る。Android 専用のボタンは作らない（利用者の決定）。ワイヤレスデバッグ（Wi-Fi の adb）は対象外（USB の adb だけで確かめた）。

**段階D-1 の追加（同日）**: TCP の接続は起動ごとに使い捨ての**接続トークン**で照合する（同じ端末の他のアプリは命令を送れない。§21.11）。
端末の一時停止中の画面は**ゲームの画面のまま**にした（PC の PAUSE のエディタの見た目〈デバッグカメラ・グリッド〉には切り替えない。§21.4）。

### 21.1 通信路（ランタイム側。`runtime/src/engine/core/app_base/ipc_transport/`）

IPC の中身（行の解釈 `read_loop`・書き込み `write_loop`）は通信路に依存しない形（`Read` / `Write` を満たすもの）にし、通信路の違いだけをこのフォルダに閉じ込めた。

| | 名前付きパイプ（PC。従来どおり） | TCP（Android。段階D-1） |
|---|---|---|
| 選び方（`endpoint.rs`） | 起動引数 `--pipe=<名前>`（あれば最優先） | 起動オプション `ipc_port` と `ipc_token`（Android）・起動引数 `--ipc-port=<ポート>` と `--ipc-token=<トークン>`（PC での検証用）。**ポートとトークンが揃ったときだけ**待ち受ける |
| つなぐ向き | ランタイム → エディタ（起動時に 1 回。20 回 × 100 ms まで待つ） | エディタ → ランタイム（`127.0.0.1:<ポート>` で listen し、**1 本ずつ** accept） |
| つながる前 | —（つながらなければ IPC 無し） | IPC 無しの Play と同じ（`IpcClient::send` はつながっていなければ積まずに捨てる） |
| つながったとき | —（トークンは無い。従来どおり） | 相手が最初の行で `HELLO:<トークン>` を送り、合えばランタイムが**挨拶の 1 行 `READY:0`** を書く（下の「挨拶」）。合わない・5 秒来ないときは `IPC_DENIED:<理由>` を書いて閉じる（`auth.rs`。§21.11） |
| 読み取り（`pipe.rs` / `tcp.rs`） | PeekNamedPipe で「読めるデータがある」ときだけ ReadFile（従来の方式。1 回の読み取りごとに確かめるようにした） | ふつうの TcpStream（読みと書きは別の複製で並行） |
| 切れたとき | 何もしない（従来どおり） | `IpcCommand::EditorDisconnected` を App へ積み、次の接続を待つ → **一時停止中なら再開**（`session_policy.rs`） |
| 一時停止（`PAUSE`） | `paused`: 時間・物理・スクリプトを止め、描画はデバッグカメラ・グリッドのエディタの見た目（従来どおり） | `remote_paused`: 時間・物理・スクリプトだけを止め、**描画はゲームのカメラのまま**（§21.4。`session_policy::pause_keeps_game_view`） |

- **bind はループバック（127.0.0.1）だけ**。端末の外（Wi-Fi 等）からは届かず、PC からは adb forward だけが届く。
- **1 本だけ受け付ける**: つながっている間は次を accept しない（後から来た接続は OS の待ち行列で待ち、挨拶が来ない）。前の接続が切れたら次を受け付ける。
- **挨拶**: adb forward は、端末で誰も待ち受けていなくても PC 側の接続をいったん受け付け、その後に閉じる。そのためエディタは「TCP でつながった」だけでは
  ランタイムとつながったか分からない。接続トークンの照合（`HELLO:`）が通った直後にランタイムが `READY:0`（`READY:{ウィンドウハンドル}` と同じ書式。
  Android にハンドルは無いので 0）を書き、エディタはこの 1 行が届いたら「つながった」、届く前に閉じられたら「まだ待ち受けていない」、
  `IPC_DENIED:` が届いたら「断られた」（やり直さない）と見分ける。
- **切断と一時停止**（`session_policy.rs`。純粋な処理・単体テスト付き）:
  - 相手が**黙って切れた**（エディタを閉じた・落ちた・USB が外れた・エディタの実行を止めた）→ 一時停止中なら再開して Play を続ける（端末のゲームが誰にも解けない
    一時停止のまま残らないように）。
  - 相手が切る前に **`DETACH`**（意図した切り離し。段階D-1 で足した命令）を送っていた → 一時停止のまま据え置く（SeedAndroid の `pause` が「つないで 1 命令送って切る」ため）。
    印は 1 回の切断で消える（次の接続へ持ち越さない）。
- ランタイムのログ（タグ SEED。TCP のときだけ出す。PC の Output は変えない）: `[SEED IPC] エディタとの通信路: 127.0.0.1:52735 で待ち受けます…`・`エディタとつながりました`・
  `接続を断りました（<相手>・理由 token）…`・`一時停止しました（PAUSE。…画面はゲームのカメラのまま）`・`再開しました（RESUME）`・`エディタとの接続が切れました（次の接続を待ちます）`・`…一時停止を解いて Play を続けます`・
  `切り離し（DETACH）の後の切断なので一時停止のままにします`。
- 書き込みは標準ライブラリの TcpStream（Linux / Android では `send` に `MSG_NOSIGNAL`）なので、相手が閉じた後に書いても SIGPIPE でプロセスは落ちない。
  書き込みの時間切れは 10 秒（相手が読まなくなった接続は閉じる）。
- **`SEED.Application.IsEditorPlay` は変えていない**: 判定は「Play かつ**名前付きパイプ**でつながった」のまま（TCP の待ち受けは起動の時点ではエディタとつながって
  いないので数えない。Android では段階D-1 の前と同じく false）。
- **INTERNET 権限**: Android はループバックでも AF_INET のソケットに `android.permission.INTERNET` が要る（無いと bind が Permission denied）。
  **デバッグ版の APK だけ**に足した（`runtime/android/app/src/debug/AndroidManifest.xml`。Gradle が debug のビルドで合わせる）。起動オプションもデバッグ版でしか
  ネイティブへ渡らない（§20.10）ので、配布版（release）は待ち受けも権限も無い。このファイルは APK の入力の指紋（`AndroidBuildInputs.GradleSources`）に足した。

### 21.2 ポートと起動オプション

```
指定（AndroidRunRequest.IpcPort。null=既定 52735 ／ 0=使わない ／ 1〜65535）
  エディタ … 環境設定 editor_preferences.json の "android": { "ipc_port": … }（未設定なら既定。設定の画面は無い）
  SeedAndroid … --ipc-port <ポート>（設定 JSON の ipc_port）
  └ 起動の工程（Steps/LaunchStep）: am start … --es seed.ipc_port '52735' --es seed.ipc_token '<トークン>'
      （シーンの extra と並べる。0 の指定ならどちらも渡さない。Output の表示ではトークンを '***' に伏せる）
      ├ 起動の直後に、同じトークンをプロジェクトの cache/android/run_state.json の ipc_launches へ記録（§21.11）
      └ MainActivity: seed.* の文字列の extra を JSON にまとめる（{"scene":"…","ipc_port":"52735","ipc_token":"…"}。変更なし）
          └ engine::platform::launch_options（IPC_PORT_KEY: 1〜65535 の整数だけ／IPC_TOKEN_KEY: 16〜128 文字の英数字・_・-。
              読めない値は警告してその項目だけ無視＝シーンは使う。logcat へ出す JSON はトークンを "***" に伏せる）
              └ runtime/android/native/src/launch.rs → LaunchArgs.ipc_port・ipc_token（両方が正しいときだけ）→ App::new が 127.0.0.1:<ポート> で待ち受ける
エディタ／SeedAndroid … adb -s <シリアル> forward tcp:0 tcp:52735（PC 側の空きポートは adb が選んで返す）→ 127.0.0.1:<PC 側> へ TCP → 最初の行で HELLO:<トークン>
```

- 既定の端末側のポートは **52735**（`Android/Ipc/AndroidIpcSettings.DefaultDevicePort`。40000 台＝本番の Lore のポートの並びを避けた）。端末の中だけで使うので PC のポートとは
  ぶつからない。PC 側は adb が OS の動的ポート（Windows は 49152〜65535）から選ぶので、Lore（41337 等）・AI ブリッジ（7234）とはぶつからない。
- JSON のキー `ipc_port` / `ipc_token` は Rust の `IPC_PORT_KEY` / `IPC_TOKEN_KEY` と C# の `AndroidRuntimeContract.LaunchOptionIpcPortKey` /
  `LaunchOptionIpcTokenKey` で一致させる（両方のテストで確かめる）。
- 起動の工程が extra を渡すのは `run` / `push`（アプリを起動する目的）。ランチャーから起動したときは extra が無いので待ち受けない（一時停止は使えない）。

### 21.3 接続の流れ（エディタ・SeedAndroid。`editor/src/Android/Ipc/`）

| ファイル | 役割 |
|---|---|
| `editor/src/Ipc/IpcLineChannel.cs` | **行の送受信（通信路に依存しない）**。PC の名前付きパイプ（`PipeServer`）と Android の TCP（`AndroidIpcSession`）が同じこのクラスで読み書きする（受信ループ・1 行ごとの前後の空白の除去・受け手の例外で止めない・UTF-8・`Closed`）。段階D-1 の前の PipeServer の振る舞いをそのまま移した |
| `editor/src/Ipc/RuntimeIpcCommands.cs` | 命令と応答の文字列（`PAUSE` / `RESUME` / `DETACH` / `HELLO:` / `READY:` / `IPC_DENIED:` / `SCREENSHOT:` …）。`RuntimeManager`（PC の Play）と Android で共有 |
| `Android/Ipc/AndroidIpcConnector.cs` | adb forward → TCP → 最初の行で `HELLO:<トークン>` → 挨拶（`READY:`）を待つ、を上限まで繰り返す。つながらなければ理由付きの `AndroidIpcException`（forward は外す）。断られたら（`IPC_DENIED:`）やり直さずに諦める |
| `Android/Ipc/AndroidIpcToken.cs` | 接続トークン（起動ごとに使い捨て。暗号用の乱数 128 ビットの 16 進 32 文字）・書式の検査・伏せた表示（`***`）。§21.11 |
| `Android/State/AndroidRunState.cs` | 起動の記録 `ipc_launches`（端末のシリアル → アプリ ID・ポート・トークン・時刻）。起動の工程が書き、SeedAndroid の pause / resume / screenshot が読む |
| `Android/Ipc/AndroidIpcSession.cs`（`IAndroidIpcLink`） | つながった通信路。`Send`・`RequestAsync`（応答を待つ）・`CloseAsync(detach)`（DETACH を送るか、黙って閉じる。どちらも自分が張った forward を外す） |
| `Android/Ipc/AndroidIpcSettings.cs`・`AndroidIpcTimings.cs` | ポートの決まり・時間の決まり（接続の上限 20 秒・やり直し 0.5 秒・1 回の挨拶待ち 3 秒・応答待ち 20 秒） |
| `Android/Ipc/AndroidIpcScreenshot.cs` | スクリーンショット（§21.6） |
| `Adb/AdbClient.cs` | `ForwardTcpAsync`（`forward tcp:0 tcp:<端末>`・出力の数字を PC 側のポートとして読む）・`RemoveForwardAsync`（`forward --remove tcp:<PC 側>`。自分が張ったものだけ）・`RunAsReadFileAsync`（`exec-out run-as <ID> cat <相対パス>`） |

**つながらないとき**（`AndroidIpcConnector.DescribeFailure`。実行は続け、理由を Output と実行ボタンのツールチップへ）

| 最後の試し | 理由 | 確かめること |
|---|---|---|
| 挨拶の前に閉じられ続けた | 端末のアプリがポートで待ち受けていない | アプリが起動しているか・段階D-1 より前の APK（`run` で入れ直す）・INTERNET 権限の無い APK でないか |
| 挨拶が来ない | 待ち受けているが応答しない | 別の接続（エディタの実行・SeedAndroid）がつながっている間はつなげない（1 本だけ） |
| PC 側のポートへつながらない | adb forward が外れた | adb のサーバーが再起動された等 |
| 断られた（`IPC_DENIED:token`。`DescribeDenial`） | 接続トークンが違う（やり直さない） | 記録より後に別の run / push（エディタの実行を含む）で起動し直した・ランチャーから起動した → run / push で起動し直す |

**エディタの段取り**（`AndroidRun/AndroidRunController.cs`。状態の遷移の正典は `AndroidRunStateMachine.cs`）

```
起動の工程が成功（Running）── pidof の見張りを始める ＋ IPC の接続を始める（Connecting。ポート 0 の指定なら Off）
  ├ つながった（Connected）… Output「<端末> のアプリとつながりました（adb forward・端末のポート 52735）。実行バーから一時停止・再開できます。」
  │    実行ボタン（一時停止）… PAUSE を送る → Paused（送れなければ Running へ戻す）
  │    実行ボタン（再開）    … RESUME を送る → Running
  │    通信路が切れた        … すぐ pidof で確かめる（0.3 秒おきに 3 回）
  │       ├ アプリが終わっていた → 「アプリが終わった」で実行を終える（pidof の見張り〈2 秒おき × 2 回〉より先に気付く）
  │       └ アプリは動いている   → Paused なら Running へ（端末は切断で一時停止を解く）→ つなぎ直す
  ├ つながらなかった（Unavailable）… 理由を Output（警告）とツールチップへ。実行は続ける（段階D-1 の前と同じ振る舞い）
  └ 停止ボタン・アプリの終了・logcat の終わり … 通信路を黙って閉じ、forward を外してから（アプリを止める）→ Idle
エディタを閉じる … 通信路を黙って閉じる（端末のゲームは一時停止を解いて続く）。forward の解除は待たない
```

- 接続トークンは実行ごとに `AndroidRunController.TryStart` が作って起動の指定（`AndroidRunRequest.IpcToken`）に入れ、同じ値でつなぐ
  （1 回の実行の中のつなぎ直しも同じ値。次の実行では作り直す）。指定の無い起動（SeedAndroid の run / push）は中核（`AndroidRunPipeline`）が作る。

### 21.4 実行バー（`AndroidRun/PlayBarPolicy.cs`。画面の色の規約は [editor_ui_style.md](editor_ui_style.md) 8 章）

| 状態 | 状態表示 | 実行ボタン | 停止ボタン |
|---|---|---|---|
| Running・つながっている | `ANDROID PLAY`（水色・Android のアイコン） | **一時停止の絵柄で押せる**（PC の PLAY と同じ）→ 一時停止 | 端末のアプリを止める |
| Paused | `ANDROID PAUSE`（橙・`Icon.Pause`。PC の PAUSE と同じ） | **再生の絵柄で押せる**（PC の PAUSE と同じ）→ 再開 | 端末のアプリを止める |
| Running・つないでいる途中 | `ANDROID RUN`（水色） | 一時停止の絵柄で押せない（「通信路につないでいます…」） | 同上 |
| Running・つながらない／使わない指定 | `ANDROID RUN`（水色） | 一時停止の絵柄で押せない（「一時停止できません: <理由>」） | 同上 |

- 進捗の表示は `<端末> で実行中` ／ `<端末> で一時停止中`。実行先セレクタは Android の実行中は変えられない（従来どおり）。
- **一時停止中の端末の画面はゲームの画面のまま**（段階D-1 の追加。段階D-1 の時点では PC の PAUSE と同じくエディタの見た目になっていた。§21.9）。
  ランタイムは TCP の `PAUSE` を `remote_paused` として扱い、ゲームの時間・物理（3D / 2D）・スクリプト・アニメーション・入力の注入だけを止める
  （判定は `App::is_simulation_paused()` ＝ `paused || remote_paused`）。描画の条件（`mode == Play && !paused`: デバッグカメラ・グリッド・ギズモ・ビューポート・
  速度バッファの連続性）に使う `paused` は PC の PAUSE（名前付きパイプ）だけが立てるので、PC の PAUSE は従来どおりエディタの見た目。
  どちらにするかは通信路の種類で決める（`ipc_transport::pause_keeps_game_view(IpcTransportKind)`。TCP だけ true）。`RESUME`・切断での再開・シーンの読み直し・
  Play の開始／終了は両方を下ろす（`clear_pause`）。
- 時間で動くシェーダ（水面等）は一時停止中も壁時計（`ambient_time`）で動き続ける（PC の PAUSE と同じ規則。`frame_renderer.rs` の `shader_time`。backlog）。

### 21.5 SeedAndroid の pause / resume / screenshot（エディタ無しで確かめる）

```powershell
# run（または push）で起動した後に（別のターミナルからでもよい）
dotnet run --project editor/tools/SeedAndroid -- pause      --project D:\path\to\Project --serial <実機>
dotnet run --project editor/tools/SeedAndroid -- screenshot --project D:\path\to\Project --serial <実機> --out paused.png
dotnet run --project editor/tools/SeedAndroid -- resume     --project D:\path\to\Project --serial <実機>
```

- どれも「adb forward を張る → TCP でつなぎ、起動の記録の接続トークンを示して（`HELLO:`）挨拶を待つ → 命令を 1 つ送る → `DETACH` を送って閉じる →
  forward を外す」。`DETACH` の後の切断なので、
  `pause` の後もゲームは一時停止のまま（再開は `resume`。黙って切れた場合だけランタイムが自分で再開する）。
- アプリ ID は `--app-id`、無ければ `--project` の設定から（`stop` と同じ。`Commands/TargetApplication`）。端末側のポートは `--ipc-port`（省略時は起動の記録のポート）。
- 接続トークンは、同じ `--project` の `run` / `push`（エディタの実行を含む）の起動の工程がプロジェクトの `cache/android/run_state.json`（`ipc_launches`。
  端末のシリアルごとにアプリ ID・ポート・トークン・時刻）に記録したものを使う（`Commands/AppControlCommand`）。記録が無ければ理由付きのエラー（終了コード 5）。
  記録より後にアプリを別の方法で起動し直した（トークンが変わった）ときは `IPC_DENIED:token` で断られる（終了コード 5・理由付き）。
- `--serial auto` は使えない（既にある端末を操作するだけのため）。エディタで実行している間はエディタがつながっているので使えない（挨拶が来ずに理由付きのエラー・終了コード 5）。
- ステップ実行（1 フレームだけ進める）は無い（§21.7）。

### 21.6 スクリーンショット

PC の Play の `SCREENSHOT:{target},{絶対パス}`（AI ツールの `seed_screenshot`。[editor_mcp.md](editor_mcp.md)）と同じ命令を TCP で送る:

1. `SCREENSHOT:game,/data/user/0/<アプリ ID>/cache/seed_ipc_screenshot.png` → ランタイムが次に描いたフレームを PNG にして端末に書く → `SCREENSHOT_DONE:{パス},{幅},{高さ}`
2. `adb exec-out run-as <アプリ ID> cat cache/seed_ipc_screenshot.png` で PC のファイルへ（アプリの内部データフォルダは adb の shell から読めないので run-as。デバッグ版の APK だけ）
3. 端末の PNG を run-as で消す（自分のアプリのキャッシュだけ）

SeedAndroid の `screenshot` から使える。**エディタ（AI ツール）からの Android の撮影は持ち越し**（MCP の AI ツールは PC の実行だけを扱う。Android の実行中に
`seed_screenshot` を Android へ回すには、AI ホストの撮影の送り先を実行先で切り替える作業が要る。backlog）。

### 21.7 ステップ実行について

PC の Play には「1 フレームだけ進める」ステップ実行が無い（ランタイムの IPC に `STEP` の命令は無く、実行バーのステップ系のボタン〈継続・ステップオーバー等〉は
C# スクリプトのデバッガ〈netcoredbg〉のもの。Android ではスクリプトのデバッグ自体が使えない。§17.12）。そのため Android にもステップ実行は足していない
（PC と Android の両方に「一時停止中に 1 フレーム進める」を足すなら、ランタイムの命令・物理スレッドの 1 ステップ・実行バーのボタンの設計が要る。backlog）。

### 21.8 確認方法

```powershell
# 1) ランタイムの通信路の単体テスト（ループバックの TCP・メモリ上の読み書き・切断の扱い・起動オプション）
cd runtime; cargo test --lib -- ipc launch_options
# 2) エディタ側の単体テスト（プレイバー・状態機械・段取り〈偽の中核と偽の通信路〉／ポート・forward の引数・行の送受信・挨拶までのやり直し・SeedAndroid の引数）
dotnet run --project editor/tests/AndroidRunUiTests
dotnet run --project editor/tests/AndroidPipelineTests
# 3) 実機: run で起動（seed.ipc_port を渡す）→ 別のターミナルで pause / screenshot / resume → stop
dotnet run --project editor/tools/SeedAndroid -- run --project D:\path\to\Project --serial <実機> --logcat-seconds 20
dotnet run --project editor/tools/SeedAndroid -- pause --project D:\path\to\Project --serial <実機>
adb -s <実機> logcat -d -s SEED DOTNET | Select-String "SEED IPC|PROBE"   # 一時停止の行・スクリプトの毎秒のログが止まる
```

- エディタの画面では: 実行先を端末にして実行 → Output に「…のアプリとつながりました…」→ 状態表示が `ANDROID PLAY` → 実行ボタン（一時停止）→ `ANDROID PAUSE`・
  端末の画面が止まる → 実行ボタン（再開）→ `ANDROID PLAY` → 停止ボタン。
- PC でも `SEED.exe --mode=play --ipc-port=<ポート> --ipc-token=<16 文字以上の英数字>` で同じ TCP の通信路を試せる（標準エラーに `[SEED IPC] …で待ち受けます`。
  つないだら最初の行で `HELLO:<トークン>`。トークンが無いと待ち受けない）。
- adb の振る舞いの注意: `adb forward --remove` は**張った後の接続を切らない**（PC 側で新しい接続を受けなくなるだけ）。エディタ／SeedAndroid は
  TCP の接続を閉じてから forward を外す。切断の確かめに forward を外しても切れない（実機で確かめた）。

### 21.9 確認結果（2026-09-25）

実機 Pixel 6a（`2B011JEGR02535`・arm64-v8a・Android 16）を USB でつないだ状態で確かめた（エミュレータは起動していない）。各手順の前に前面の窓が
ランチャーか自分のアプリであることを確かめた。プロジェクトは段階C-1 の確認用（`proj_probe`＝最小構成＋毎秒ログを出す確認用スクリプト `[PROBE v2] t=…`）。
一時停止の効き目は「スクリプトの毎秒のログが止まる（ゲームの時間が進まない）」で見た（描画は一時停止中も続くので `[SEED HEARTBEAT]` のフレーム数は増え続ける）。

| 項目 | 結果 |
|---|---|
| Rust の単体テスト | `cargo test --lib -- ipc launch_options` 45 / 45（新規: メモリ上の `read_loop` / `write_loop` 3・通信路の選び方 2・切断の扱い 2・ループバックの TCP 6〈127.0.0.1 だけ・挨拶・PAUSE/RESUME・黙った切断で再開の判断・DETACH で据え置き・切れたら次を受け付ける・1 本だけ・つながる前の行は捨てる〉・起動オプションの `ipc_port` 2）。`cargo test --lib`（不安定な 3 つを除く）2628 成功・0 失敗 |
| ビルド | `cargo build`（PC）・libSEED.so（arm64。SeedAndroid の run の中）とも、変更したファイルに警告なし。エディタ（別の出力先）エラー 0・警告 25（変更前と同じ）。SeedAndroid 警告 0 |
| C# の単体テスト | `AndroidRunUiTests` 64 / 64（新規 9: プレイバーの接続あり／なし／つないでいる途中／一時停止中・PC の PAUSE と同じ見た目・状態機械・段取り〈偽の中核と偽の通信路で PAUSE/RESUME・つながらない・ポート 0・切断でアプリの終了・切断でつなぎ直し・送れないとき〉）。`AndroidPipelineTests` 88 / 88（新規 8: ポート・forward の引数と出力・起動の extra・行の送受信・挨拶までのやり直し〈ループバックの偽のランタイム〉・諦めるときの理由・スクリーンショットの応答・SeedAndroid の引数）。どちらも 3 回続けて全件成功 |
| PC の Play（名前付きパイプ）が従来どおり | エディタと同じ `PipeServer`（`IpcLineChannel` に載せ替えたもの）で `SEED.exe --mode=play --pipe=…` を起動: READY → 4 秒で 4 行 → `PAUSE` で 0 行 → `RESUME` で 4 行 → `SCREENSHOT` の往復（1920x1080）→ `STOP` で終了コード 0。`[SEED IPC]` の行は出ない（PC の Output は変わらない） |
| PC で TCP の通信路（`--ipc-port=0`） | 挨拶 `READY:0`（接続 3 回とも）・`PAUSE` で 0 行・黙って切ると再開（4 行）・`PAUSE`＋`DETACH` の後の切断は一時停止のまま（0 行）・次の接続の `RESUME` で 4 行・`SCREENSHOT` の往復 |
| 実機: run（1 回目） | 7 工程すべて（エンジンのソースが変わったので .so・pak・同梱 .NET・Gradle・インストールをやり直し。合計 194.4 秒・APK 60.6 MB）。`am start … --es seed.ipc_port '52735'` → logcat `起動オプションを受け取りました: {"ipc_port":"52735"}` → `127.0.0.1:52735 で待ち受けます`（デバッグ版のマニフェストに INTERNET 権限が合わさり、bind できた） |
| 実機: SeedAndroid の pause → screenshot → resume → screenshot | `pause`（1.6 秒）→ `[SEED IPC] 一時停止しました` → `DETACH の後の切断なので一時停止のままにします`。毎秒のログは 19:29:52.8（t=62.8s）で止まり、`resume` の後 19:30:10.7（**t=63.8s**）から再開（17 秒の一時停止の間ゲームの時間は進まない）。一時停止中もフレームは約 17.7 fps で描かれ続けた。`screenshot` は一時停止中・再開後とも 1080x2400 の PNG（1.5〜1.6 MB）を取り出し、端末の PNG は消した。各コマンドの後の `adb forward --list` は空 |
| 実機: エディタと同じ段取り（`AndroidRunController`＋本物の中核。WPF 抜きの一時のプローブ） | 実行 → 起動の 0.1 秒後につながる（`ANDROID RUN`→`ANDROID PLAY`）→ `TryPause` で `ANDROID PAUSE`・6 秒で 0 行 → `TryResume` で 5 行 → 一時停止中に通信路を外から黙って閉じる → 端末は `一時停止を解いて Play を続けます`、エディタは pidof でアプリが動いているのを確かめて 0.1 秒でつなぎ直し（`Running`・6 行）→ 一時停止 → 停止ボタン → `Idle`・`pidof` 空・forward 無し |
| 実機: アプリの終了（一時停止中に自分のアプリを外から `am force-stop`） | 通信路の切断からすぐ pidof で確かめ、**0.32 秒**で「端末でアプリが終わったので実行を終えました」→ `Idle`（pidof の見張りだけなら 2〜4 秒）。forward 無し |
| 一時停止中の端末の画面 | メインカメラと同じ構図のまま、エディタのグリッドが重なる（PC の PAUSE と同じくデバッグカメラ・エディタの見た目に切り替わる。再開すると元のゲームの画面）。→ 段階D-1 の追加でゲームの画面のままに変えた（§21.4・§21.12） |
| エディタの画面（実行バー・Output・ツールチップ） | **未確認**（エージェントはエディタを起動しない。判断はすべて単体テストと、実機での段取りのプローブのプレイバーの判断の記録で確かめた。利用者が画面で確かめる） |

- 最後の状態: `com.seedengine.runtime`（段階D-1 の APK・`proj_probe`）を入れて止めた（`pidof` 空・前面はランチャー）。adb forward は残していない。
  Gradle のデーモンは止めた。エミュレータは起動していない。

### 21.10 制限・持ち越し（詳細は [backlog.md](backlog.md) の「Android」節）

- ~~一時停止中の画面がエディタの見た目になる~~ → 段階D-1 の追加でゲームの画面のままにした（§21.4）。時間で動くシェーダは一時停止中も動く（PC と同じ）。
- **ステップ実行は無い**（PC にも無い。§21.7）。
- **エディタの AI ツールから Android の実行は撮れない・操作できない**（撮影は SeedAndroid の `screenshot` だけ）。
- **1 本だけ受け付ける**（エディタの実行中は SeedAndroid の `pause` 等が使えない。あきらめた接続は待ち行列に残り、後で受け付けられてすぐ切れる＝一時停止中なら再開）。
- ~~端末の他のアプリからも命令を送れる~~ → 段階D-1 の追加で接続トークンを照合するようにした（§21.11）。ただし他のアプリが先につなぐと、ランタイムは
  HELLO の時間切れ（5 秒）までその接続を持つので、その間はエディタがつなげない（1 本だけのため。つながるまでやり直す上限は 20 秒）。
- ランチャーから起動したアプリにはつなげない（起動オプションが無い）。`SEED.Application.IsEditorPlay` は Android では false のまま。
- ワイヤレスデバッグ（Wi-Fi の adb）では確かめていない。エディタの画面での操作は未確認（上の表）。

### 21.11 接続トークン（段階D-1 の追加。`ipc_transport/auth.rs`・`Android/Ipc/AndroidIpcToken.cs`）

ランタイムは 127.0.0.1 で待ち受けるので端末の外からは届かないが、同じ端末で INTERNET 権限を持つアプリは届く。そこで起動ごとに使い捨ての
トークンを作って起動オプションで渡し、TCP の接続の最初の行で照合する（名前付きパイプ〈PC〉は従来どおりトークン無し）。

| 誰が | すること |
|---|---|
| エディタ（`AndroidRunController.TryStart`）・中核（`AndroidRunPipeline`。指定が無ければ作る） | 実行ごとに新しいトークン（暗号用の乱数 128 ビットの 16 進 32 文字）を作る |
| 起動の工程（`Steps/LaunchStep`） | `am start … --es seed.ipc_token '<トークン>'`（Output の表示は `'***'`）。起動の直後にプロジェクトの `cache/android/run_state.json` の `ipc_launches` へ記録する（端末のシリアル → アプリ ID・ポート・トークン・時刻。IPC を使わない起動ではその端末の古い記録を消す） |
| ランタイム（`auth.rs`・`tcp.rs`） | 受け付けた接続の最初の行（256 バイトまで・5 秒まで待つ）が `HELLO:<トークン>` で一致したときだけ `READY:0` を書いて命令を受け付ける。違えば `IPC_DENIED:token`、HELLO でない・来なければ `IPC_DENIED:hello` を書いて閉じ、次の接続を待つ（断った接続の行は 1 行も命令として扱わない）。比較は長さと中身を一定の手間で比べる。logcat・標準エラーにトークンの値は出さない（起動オプションの JSON は `"ipc_token":"***"` に伏せる） |
| エディタ・SeedAndroid（`AndroidIpcConnector`） | つないだら最初に `HELLO:<トークン>` を送り、`READY:` を待つ。`IPC_DENIED:` が来たらやり直さずに理由付きで諦める。SeedAndroid の pause / resume / screenshot は run_state.json の記録のトークンを使う（§21.5） |

- トークンの書式は 16〜128 文字の英数字・`_`・`-`（Rust の `parse_ipc_token` と C# の `AndroidIpcToken.IsValid` が同じ規則）。読めない値の起動、ポートだけでトークンが無い起動は
  **待ち受けない**（IPC 無しの Play。logcat に理由）。
- run_state.json のトークンは平文（PC のプロジェクトの `cache/` の中）。ランタイムはトークンをファイルに書かない。
- 断った後もランタイムは次の接続を待つ。他のアプリが先につないで何も送らないと、その接続を HELLO の時間切れ（5 秒）まで持つので、その間はエディタがつなげない
  （エディタはつながるまで 20 秒やり直す。§21.10）。

### 21.12 確認結果（段階D-1 の追加・2026-09-25）

| 項目 | 結果 |
|---|---|
| Rust の単体テスト | `cargo test --lib -- ipc launch_options` 57 / 57（新規 12: 照合〈一致だけ受け付ける・一定の手間の比較・断りの行・HELLO の前の命令〉4・ループバックの TCP 4〈HELLO の直後の命令も届く・違うトークンは命令を 1 つも届けずに断る・HELLO の前の PAUSE は断る・黙った接続は時間切れで断る〉・通信路の選び方〈トークンの無いポートは待ち受けない〉1・一時停止の見た目〈TCP だけゲームの画面のまま〉1・起動オプションの `ipc_token`〈キー・書式・伏せた JSON〉2）。`cargo test --lib`（不安定な 3 つを除く）2640 成功・0 失敗 |
| C# の単体テスト | `AndroidPipelineTests` 91 / 91（新規 3: 断られたらやり直さない・トークンの作成と extra と表示の伏せ・run_state.json の記録の往復）。`AndroidRunUiTests` 65 / 65（新規 1: トークンは実行ごとに作り直す）。どちらも既存のテストの HELLO の確認を含む |
| ビルド | エディタ（別の出力先）エラー 0・警告 25（変更前と同じ。変更したファイルに警告なし）。SeedAndroid 警告 0。libSEED.so（arm64。run の中）成功 |
| PC の Play（名前付きパイプ）が従来どおり | トークン無しで READY → `PAUSE` で止まる → `RESUME` → `SCREENSHOT` → `STOP`。一時停止中の絵はエディタの見た目（実行中の絵と比べて、8 階調より大きく違う画素が画面の 10.1%・画面全体。グリッドが出る） |
| PC で TCP の通信路（`--ipc-port=0 --ipc-token=…`） | 違うトークン → `IPC_DENIED:token`（その後に送った `PAUSE` は効かず、スクリプトの毎秒のログは続く）・正しいトークンで `READY:0`（3 回）・`PAUSE` で止まる・黙った切断で再開・`DETACH` の後は据え置き・`RESUME`。一時停止中の絵は実行中とほぼ同じ（8 階調より大きく違う画素 15 個＝0.0007%。一時停止中の 2 枚どうしは 0 個）＝ゲームのカメラのまま・グリッド無し。ログにトークンの文字列 0 件 |
| 実機: run | .so・pak・Gradle・インストールをやり直し（174 秒）。表示 `am start … --es seed.ipc_port '52735' --es seed.ipc_token '***'` → logcat `起動オプションを受け取りました: {"ipc_port":"52735","ipc_token":"***"}` → `127.0.0.1:52735 で待ち受けます（…接続トークンを照合して受け付けます）`。run_state.json に 32 文字のトークンを記録。SeedAndroid の出力・logcat にトークンの値 0 件 |
| 実機: 照合（自分で張った adb forward から素の TCP） | 違うトークン → `IPC_DENIED:token`（0.06 秒で閉じられる）・HELLO の代わりに `PAUSE` → `IPC_DENIED:hello`・何も送らない → 5.05 秒で `IPC_DENIED:hello`。logcat に `接続を断りました（…理由 token）` 1 行・`理由 hello` 2 行。forward は外した |
| 実機: SeedAndroid の pause / resume（run_state.json のトークン） | どちらも `つながりました`（`READY:0`）→ 終了コード 0。logcat に `エディタとつながりました` 2 回。各コマンドの後の `adb forward --list` は空 |
| 実機: 一時停止中の画面・一時停止の効き目 | **未確認**。端末の画面が消えてロック中（`screenState=SCREEN_STATE_OFF`・前面はロック画面）で、アプリは描画できなかった（`presented_frames total=0`・サーフェスは 10 秒で手放した）。メインループが回らないので `PAUSE` / `RESUME` は積まれたまま処理されず、撮影もできない。画面の点灯・ロックの解除は私物の端末の操作になるので行っていない。PC の TCP の通信路（上）で確かめた |

- 最後の状態: `com.seedengine.runtime`（段階D-1 の追加の APK）を入れて止めた（`pidof` 空）。adb forward は残していない。Gradle のデーモンは止めた。エミュレータは起動していない。

---

## 22. モバイル向けの描画プリセット（段階D-2・2026-09-25）

デスクトップ向けに作った描画経路（デファード・MRT 5 枚・SSGI・シャドウ 2048 等）を端末の実解像度のまま回していたため、
実機（Pixel 6a・Mali-G78・1080x2400）は縦 18〜19 fps・横 37〜39 fps で、1 フレーム約 50 ms のうち提示待ちが 28〜41 ms の GPU 律速だった（§7）。
段階D-2 では、描画を**データの差し替えだけで**軽くする「描画品質プリセット」と、どのパスが重いかを実機で測る
「パスごとの GPU タイムスタンプ計測」を入れた。段階D-3 で実機で測り、`mobile` の描画スケールを 0.5 → 0.75 に見直した（§22.7）。
設計の正典は [rendering_roadmap.md](rendering_roadmap.md) の「描画品質プリセット」。

### 22.1 結論

- プリセットの定義は `runtime/config/render_presets.json`（ランタイムとエディタが同じファイルを埋め込む）。
  既定は**デスクトップ `desktop`（何も下げない＝従来の描画そのもの）・Android `mobile`**（`PlatformTraits::default_render_quality`）。
- `mobile` は、ゲーム画面（3D）を**画面の 0.75 倍の解像度で描いて拡大**し（UI は画面の解像度のまま）、G-Buffer を使わない**前方描画**、
  SSGI・AO・反射・ブルーム・水面反射・コースティクスを止め、影を 1024・50 m・4 タップへ下げる。
- スクリプトから見える座標（`Screen.Width` / `Height` / `SafeArea`・`Input.MousePosition`・`GetTouch`・キャンバス UI）は
  **描画スケールで変わらない**（論理サイズ＝従来の描画解像度のまま。§22.5）。実機でもタップの位置・`SafeArea` が `desktop` と `mobile` で一致した。
- PC の Play は従来と画素単位で同じ（§22.7）。
- **実機（Pixel 6a・縦 1080x2400・debug の .so・`proj_bench`）で `desktop` 16.4 fps（GPU 59.2 ms/フレーム）→ `mobile` 59.2 fps（GPU 10.5 ms）**
  （段階D-3。§22.7）。最大の要因は**デファードのライティング**（等倍で 41.5 ms。前方描画の約 5 倍。原因は未調査）と **SSGI**（11.0 ms）で、
  前方描画にするだけで等倍でも 59.3 fps（GPU 12.3 ms）になる。描画スケール 0.5 と 0.75 の差は GPU 約 2 ms で、0.5 は床の縁や細部のぼけが
  目立つため `mobile` を 0.75（`mobile_high` は等倍）にした。残りの約 4.5 ms は描画スケールに関係なく画面の解像度で走る固定分
  （クラスタ構築・トーンマップ・UI・提示のコピー）。横向き・動くシーン・影の深度パスは未計測（§22.8）。

### 22.2 構成

| 置き場 | 役割 |
|---|---|
| `runtime/config/render_presets.json` | プリセットの定義（名前・表示名・説明・つまみ）。ここだけ書き換えればプリセットを足せる |
| `runtime/src/engine/core/renderer/quality/` | つまみ（`knobs.rs`）・定義の読み込み（`catalog.rs`）・実効の品質の決定（`resolve.rs`）・既存設定への当て方（`apply.rs`）・描画スケールの寸法（`scale.rs`） |
| `renderer/render_features.rs` | `FeatureCaps` と `RenderFeatures::resolve_with_caps`（機能の方式の上限。`ResolvedFeatures` の解決に品質を通す） |
| `renderer/mod.rs` | `Renderer::set_render_scale` / `logical_size` / `render_size`、UI 用の深度 `overlay_depth`、`RenderFrame::logical_size` |
| `renderer/gpu_timing/` | パスごとの GPU タイムスタンプ（`GpuPassTimer`・区間名・集計）。既定オフ |
| `app/render_quality.rs` | 起動時の決定と `[SEED QUALITY]` ログ、フレームごとの描画スケールの判断 |
| `app/app_init.rs` / `app/frame_renderer.rs` | 影の品質・目標 fps の上限（起動時）、描画スケール・機能の上限・前方描画／後処理／水面の可否・GPU の節目（毎フレーム） |
| `platform/mod.rs` | `default_render_quality`（desktop / mobile）と設定の節の名前 `quality_platform_key`（desktop / android） |
| `platform/launch_options.rs`・`android/native/src/launch.rs`・`main.rs` | 起動オプション `seed.quality` / `seed.quality_overrides` / `seed.gpu_timing`（PC は `--render-quality=` / `--render-quality-overrides=` / `--gpu-timing`・`SEED_GPU_TIMING=1`） |
| エディタ `ProjectSettings/RenderQualitySettings.cs`・`RenderQualityPresetCatalog.cs`・`ProjectSettingsWindow.RenderQuality.cs` | `render_quality` 節の型（寛容な読み・知らないキーの保持）、埋め込みの定義の読み取り、プロジェクト設定「グラフィックス → レンダリング品質」 |

### 22.3 プリセットとつまみ

| プリセット | 既定の対象 | 中身（つまみ） |
|---|---|---|
| `desktop`（デスクトップ（従来どおり）） | Windows | 何も下げない |
| `mobile`（モバイル（軽量）） | Android | `render_scale` 0.75・`deferred` false・`gi` flat・`ao` off・`reflection` off・`translucency` raster・`shadow` shadowmap・`shadow_resolution` 1024・`shadow_distance` 50・`shadow_pcf_taps` 4・`bloom` false・`water_reflection` false・`water_caustics` false |
| `mobile_high`（モバイル（高画質）） | — | `mobile` の `render_scale` を 1.0（等倍）・`shadow_resolution` 2048・`shadow_pcf_taps` 8 に（`shadow_distance` は設定のまま） |
| `mobile_low`（モバイル（最軽量）） | — | `mobile` の `render_scale` を 0.5 に下げ、`shadows` false・`fxaa` false・`target_fps` 30（`shadow_*` の上限は影を描かないので無し） |

つまみの意味・当て方（上限は重い要求だけを下げ、`false` は止めるだけで動かしはしない）は rendering_roadmap.md の表。
段階D-2 の当初は `mobile` 0.5・`mobile_high` 0.75 だった（実機の計測で見直した。§22.7）。

**つまみを選んだ理由（実機の計測で確かめた。数値は §22.7 の表）**
- 1080x2400 は約 2.6 M 画素。描画スケール 0.5 で 3D のすべてのフルスクリーンの処理（G-Buffer の書き込み・ライティング・SSGI・
  トーンマップの入力）の画素数が 1/4 になる。UI は拡大でぼけると読みにくいので論理サイズのまま。
  実測: デファードのまま 0.5 にすると GPU 59.2 → 19.6 ms（39.9 fps）。前方描画の上では 1.0 → 0.75 → 0.6 → 0.5 で GPU 11.6 → 10.5 → 9.2 → 8.5 ms と
  差が小さく（どれも 59 fps）、0.5 は床の縁や細部のぼけが目立つので `mobile` は 0.75 にした。重いシーンで 60 fps を割るなら 0.6 へ下げる。
- デファードの G-Buffer は MRT 5 枚・36 byte/画素（`gbuffer::GBUFFER_BYTES_PER_SAMPLE`）で、タイルベースの GPU ではタイルメモリに
  収まらず帯域を食いやすい。前方描画（クラスタ化ライティングは前方でも効く）にすると G-Buffer・フルスクリーンのライティングが無くなる。
  代わりに SSGI・AO・反射・コースティクス・水面反射は（デファード専用なので）自動的に止まる。
  実測: `deferred=false` だけで等倍でも 16.4 → 59.3 fps（GPU 12.3 ms）。デファードのライティングは等倍で 41.5 ms と、前方描画のパス
  （forward 8.0 ms）の約 5 倍（Mali で重い原因は未調査。§22.8）。`mobile` に `deferred=true` を戻すと（0.5 で）GPU 14.8 ms・57.8 fps。
  **シェーディングアセット（L3。[shading_asset.md](shading_asset.md)）もデファードのライティング専用なので効かなくなる**
  （要求されていれば起動後に `[SEED QUALITY][WARN] … シェーディングアセット … は効きません` を 1 回出す）。
  使うゲームは `render_quality.android` に `"deferred": true` を書いて戻す（その分は描画スケールで稼ぐ）。
- Mali は RT 非対応なので、既定の `gi_enabled`（＝`rt`）は SSGI へ降格して**毎フレーム走っていた**（§7 の `[SEED FEATURES]`）。`gi` flat で止める。
- 影は 3 カスケード × 2048² の深度を描くので、1024・50 m・PCF 4 タップへ下げる（`mobile_low` は影なし）。
- ブルームはフルスクリーンのダウン／アップサンプルの連鎖なので止める（プロジェクト・シーンで有効にしていた場合だけ効く）。

### 22.4 設定の仕方

- **エディタ**: プロジェクト設定 → グラフィックス → **レンダリング品質**。デスクトップ（Windows）と Android それぞれに
  「プリセット」（既定＝プラットフォームの既定・定義済みのプリセット）、「描画スケール」（プリセットのまま／1〜0.5 倍）、
  「影」（プリセットのまま／描く／描かない）を選ぶ。選んだプリセットの説明と中身が下に出る。
- **JSON**: `project_settings.json` の `render_quality`（[project_system.md](project_system.md) §1）。例:
  `"render_quality": { "android": { "preset": "mobile", "render_scale": 0.6, "gi": "ssgi" } }`。
  画面に出ないつまみ（`gi`・`deferred`・`bloom` 等）も書け、エディタで保存しても失われない。
- **起動オプション（計測・検証用。プロジェクト設定より優先）**:
  `am start … --es seed.quality mobile_high --es seed.quality_overrides 'render_scale=0.6,shadows=false'`。PC は
  `SEED.exe --render-quality=mobile --render-quality-overrides=render_scale=0.75`。
- 反映: 起動時に 1 回だけ読む（起動ログ `[SEED QUALITY] preset=mobile render_scale=0.75 …` と `[SEED FEATURES] … gi=flat(品質上限) …`）。
  Android は project_settings.json が APK の pak に入るので、変えたら APK を作り直す（実行ボタン・SeedAndroid の `run` は入力の変化を見て作り直す）。

### 22.5 描画スケールと座標・UI

- **論理サイズ**＝従来の描画解像度（Android はサーフェス＝画面の実寸。内部解像度固定ならその内部解像度）。UI のオーバーレイ・
  トーンマップ後の LDR・提示の基準で、スクリプト・入力・安全領域・キャンバスのレイアウトと当たり判定はこの座標のまま。
- **描画解像度**＝論理サイズ × `render_scale`（Pixel 6a の縦なら `mobile` の 0.75 で 810x1800、`mobile_low` の 0.5 で 540x1200）。3D の深度・HDR と、影以外の 3D の RT すべて。
- 流れ: 3D を描画解像度で描く → トーンマップが論理サイズの LDR へ**拡大しながら**書く（バイリニア）→ UI（前面のキャンバス）を
  論理サイズで重ねる（UI 用の深度は論理サイズで別に持つ）→ 画面へ出す。
- ゲームのビューポート（カメラのスケーリングモードの帯）は論理サイズで決め、3D のパスへ渡す直前に描画解像度のピクセルへ写す
  （帯の色塗り・クラスタの分割・カメラの `resolution` も同じ比率）。スクリプト 3D プリミティブの線幅は画面の px のまま。
- 描画解像度で描かれる UI: **背景ゾーンのキャンバス**（3D より奥に描くのでメインパスの中）と **3D 空間に置いたキャンバス**。
  前面ゾーン（通常の UI）だけが論理サイズで描かれる。
- 等倍で描く条件: Edit、Play のエディタの一時停止（ID パスのピックが動く）、サムネイル撮影、`SEED_ID_PASS_IN_PLAY`。
  Android の IPC の一時停止（ゲームの画面のまま。§21）は縮小のまま。

### 22.6 計測の仕方（実機）

```bash
# 1) APK を作って入れる（計測したいプロジェクト。段階D-2 では計測用の proj_bench＝ヘルメット 9 体・影・点光源 4・UI を入れた）
dotnet run --project editor/tools/SeedAndroid -- install --project <プロジェクト> --serial <実機>
# 2) 端末の画面が点灯・ロック解除されていて前面がランチャーであることを確かめる
adb -s <実機> shell dumpsys power | grep mWakefulness        # Awake
adb -s <実機> shell dumpsys window | grep mCurrentFocus      # …launcher…
# 3) 品質と GPU 計測を起動オプションで指定して起動し、20 秒ほど待って logcat を読む（プリセットごとに繰り返す）
adb -s <実機> shell am force-stop com.seedengine.runtime
adb -s <実機> shell am start -W -n com.seedengine.runtime/com.seedengine.runtime.MainActivity \
    --es seed.gpu_timing 1 --es seed.quality desktop                       # 基準（従来の描画）
#   … --es seed.quality mobile                                            # 既定の mobile
#   … --es seed.quality desktop --es seed.quality_overrides 'deferred=false'   # つまみ 1 つずつの効き目
adb -s <実機> logcat -d -v time -s SEED | grep -E "SEED QUALITY|SEED FEATURES|SEED GPU\]|HEARTBEAT"
```

- `[SEED HEARTBEAT] … (N fps)`（3 秒ごと。提示したフレーム数から）と、`[SEED GPU] 3.0s cpu_frames=… gpu_frames=… |
  cpu frame=… acquire=… present=… | gpu total=… compute=… shadow=… rt=… cluster=… gbuffer=… ao=… lighting=… ssgi=… reflection=…
  forward=… water=… wboit=… overlay=… particles=… bloom=… tonemap=… ui=… present=…`（3 秒ごと。ms の平均）。
  `acquire` は描画先の取得待ち（[PERF] の bf）、`present` は提出と提示（[PERF] の finish）＝提示待ちの目安。
  `gpu` の各区間は「1 つ前の節目からこの節目まで」に GPU が使った時間（区間名の意味は `renderer/gpu_timing/segments.rs`）。
- 計測は Renderer が `TIMESTAMP_QUERY` + `TIMESTAMP_QUERY_INSIDE_ENCODERS` を要求したときだけ動く（`seed.gpu_timing` が無ければ
  feature も要求しない）。Mali 等のタイルベースの GPU は隣り合うパスの仕事を重ねるので、区間の境目は目安（合計は正しい）。
- アプリの画面だけを撮るには、起動オプションに `--es seed.ipc_port 52735 --es seed.ipc_token <16〜128 文字の英数字>` も付け、
  adb forward 越しに `HELLO:<トークン>` → `SCREENSHOT:game,/data/user/0/com.seedengine.runtime/cache/<名前>.png` →
  `adb exec-out run-as com.seedengine.runtime cat cache/<名前>.png > shot.png`（§21.6 と同じ。端末の画面全体は撮らない）。
- 座標の確認: `proj_bench` の確認用スクリプトが毎秒 `[PROBE v2] … screen=WxH safe=(…)` と、タッチの `pos=(x,y)`、UI のボタン
  （左上 80,120〜400,440）の押下 `[UIPROBE] down mouse=(x,y)` を出す。`desktop` と `mobile` で同じ場所を
  `adb shell input tap 240 280`（ボタンの中）・`input tap 700 1500`（外）し、値と当たり判定が同じことを見る（前面が自分のアプリのときだけ）。

### 22.7 確認結果（2026-09-25）

| 項目 | 結果 |
|---|---|
| Rust の単体テスト | `cargo test --lib`（不安定な 3 件を除外）2678 / 2678。新規: 品質のつまみ（全キー・値域・読めない値）・定義（埋め込みの妥当性・desktop が空・mobile の中身・壊れた定義）・決定（プラットフォーム既定・他の節が漏れない・プロジェクトの上書き・起動オプションの優先・知らない名前）・当て方（可否・影の上限・何も変えない）・描画スケール（等倍は恒等・端末の寸法・比率・**論理と描画の点が同じ NDC**）・機能の上限（**上限なしは resolve と全組み合わせで同一**・重い要求だけ下げる・降格の後に当てる・ログの注記）・GPU 計測の集計・目標 fps の上限・起動オプションのキー・プラットフォームの既定 |
| C# の単体テスト | `ProjectSystemTests` 61 / 61（新規 6: render_quality 節の往復〈画面に出さないつまみ・知らない節も保つ〉・空なら書かない・型違いでも読める・描画スケールの値域・埋め込みの一覧に各既定・定義の書式違い） |
| ビルド | `cargo build`（Windows）成功・変更したファイルに新しい警告なし。libSEED.so（arm64・SeedAndroid の build）成功。エディタ（別の出力先）エラー 0・警告 25（変更前と同じ） |
| PC の Play が従来どおり | 変更前の `SEED.exe`（develop・15:33 のもの）と変更後（debug）で `proj_bench` の同じフレームを撮って比べた: 通常（1280x720）フレーム 300 は画素一致、600 は変更前どうしの 2 回でも 0.031%（最大 3 階調）ずれる非決定の揺れがあり、そのうち 1 回とは画素一致。16:9 の帯付きカメラ（1280x720）・正方形のウィンドウ（1000x1000・上下に帯）もフレーム 300 / 600 とも画素一致。`[SEED QUALITY] preset=desktop （下げるつまみなし）`・`[SEED FEATURES]` は変更前と同じ行 |
| PC で `mobile`（`--render-quality=mobile`） | 3D は半分の解像度で描かれ（輪郭がやや粗い）、UI のスプライトの内側は `desktop` の絵と**画素一致**（UI は論理サイズのまま）。帯（正方形のウィンドウ）の位置も正しい。内部解像度固定（fixed 1280x720）＋描画スケール 0.75 も検証エラーなし |
| PC の GPU 計測（RTX 3060・1280x720・`proj_bench`） | `desktop`: gpu total 1.4 ms（lighting 0.85・gbuffer 0.20・rt 0.17）。`mobile`: 0.38 ms（forward 0.23・tonemap 0.06）。静止シーンでは影の深度パスは静的スキップで 0 |
| エディタの画面（レンダリング品質） | エディタは起動せず、一時のプローブ（別の出力先の SEEDEditor.dll を参照してウィンドウをオフスクリーンで描く）で確認: 2 プラットフォームの小節・プリセットの説明の切り替え・一覧に無い中間の描画スケール（0.65）の表示、選択を変えて収集 → 保存で `render_quality` 節が書かれ、画面に出さない `gi` が残ること |
| 実機の fps・内訳・スクリーンショット・座標 | 段階D-2 の時点では未実施（端末の画面が消えたままだった）。**段階D-3 で計測した（下の表）** |
| arm64 の .so・APK（最終のコード） | SeedAndroid の `install`（`proj_bench`）で libSEED.so（debug・72 秒）・pak・Gradle をやり直して入れた（75.7 MB・`com.seedengine.runtime`）。起動はしていない |

- 段階D-2 の終わりの状態: `com.seedengine.runtime`（`proj_bench`・段階D-2 の APK）を入れたまま（起動はしていない）。adb forward は張っていない。
  Gradle のデーモン・dotnet のビルドサーバーは止めた。エミュレータは起動していない。

**実機の計測（段階D-3・2026-09-25）** Pixel 6a（Mali-G78）・縦 1080x2400・debug の .so・`proj_bench`（DamagedHelmet 9 体・影ありの平行光・点光源 4・床・UI）。
§22.6 の手順で、組み合わせごとに 25 秒。fps は起動と撮影の区間を除いた平均、時間は ms の平均（`[SEED GPU]`）。
「固定分」＝クラスタ構築（約 1 ms）＋トーンマップ（約 1.9 ms）＋UI＋提示のコピー（描画スケールに関係なく画面の解像度で走る）。
「CPU の提示待ち」＝`cpu present`（提出と提示の待ち）。「mobile」は計測の時点の `mobile`（描画スケール 0.5）を指す。

| 組み合わせ | fps | GPU 合計 | lighting | ssgi | gbuffer | forward | 固定分 | CPU の提示待ち |
|---|---|---|---|---|---|---|---|---|
| desktop | 16.4 | 59.2 | 41.5 | 11.0 | 2.1 | 0.7 | 3.5 | 38.7 |
| desktop + gi=flat | 20.3 | 47.9 | 41.3 | 0 | 2.2 | 0.3 | 3.7 | 29.2 |
| desktop + render_scale=0.5 | 39.9 | 19.6 | 10.8 | 5.0 | 0.7 | 0.4 | 2.7 | 7.1 |
| desktop + deferred=false（等倍） | 59.3 | 12.3 | 0 | 0 | 0 | 8.0 | 3.9 | 4.0 |
| mobile（旧 0.5） | 59.3 | 8.5 | 0 | 0 | 0 | 3.1 | 5.2 | 2.7 |
| mobile + render_scale=0.6 | 59.3 | 9.2 | 0 | 0 | 0 | 3.9 | 4.9 | 3.1 |
| mobile + render_scale=0.75（**新しい mobile**） | 59.2 | 10.5 | 0 | 0 | 0 | 5.6 | 4.5 | 3.2 |
| mobile + render_scale=1.0（**新しい mobile_high 相当**） | 59.3 | 11.6 | 0 | 0 | 0 | 7.1 | 4.1 | 3.7 |
| mobile + shadows=false | 59.2 | 7.7 | 0 | 0 | 0 | 2.5 | 4.9 | 3.0 |
| mobile + deferred=true | 57.8 | 14.8 | 10.9 | 0 | 0.8 | 0.2 | 2.8 | 5.9 |
| mobile_high（旧 0.75・影 2048） | 59.2 | 10.3 | 0 | 0 | 0 | 5.3 | 4.5 | 3.2 |
| mobile_low（30 fps 上限） | 29.7 | 8.9 | 0 | 0 | 0 | 2.1 | 5.9 | 6.1 |

- 最大の要因は**デファードのライティング**（等倍で 41.5 ms。前方描画のパスの約 5 倍。Mali で重い原因は未調査）と **SSGI**（11.0 ms）。
  前方描画にするだけで等倍でも 59.3 fps（垂直同期の上限）に届く。デファードのまま描画スケール 0.5 にしても 39.9 fps。
- 前方描画の上では描画スケールの効きは小さい（1.0 → 0.5 で GPU 11.6 → 8.5 ms。0.75 との差は約 2 ms）。0.5 は床の縁や細部のぼけが
  目立つため、`mobile` を 0.75、`mobile_high` を 1.0 にした（`runtime/config/render_presets.json`。`mobile_low` は 0.5 のまま）。
- 描画スケールを下げても約 4.5 ms の固定分が残る（§22.8）。
- 座標: タップの位置・`SafeArea` とも `desktop` と `mobile` で一致した（§22.5 の「描画スケールで変わらない」）。
- 未計測: 横向き、動くシーン（このシーンは静止しているので影の深度パスは静的スキップで省略されている）、release の .so。
- 実行中の差し替え（§23）の実機確認でも、`proj_probe`（BrainStem 1 体・平行光）で `[SEED QUALITY] preset=mobile render_scale=0.75 …`・約 59 fps だった。

### 22.8 制限・持ち越し（詳細は [backlog.md](backlog.md) の「Android」節）

- ~~実機での計測と `mobile` の見直し~~ → 段階D-3 で計測し、`mobile` を 0.75・`mobile_high` を 1.0 に見直した（§22.7）。残りは次のとおり。
- **デファードのライティングが Mali で前方描画の約 5 倍重い原因の調査**（等倍で 41.5 ms。G-Buffer の読み・クラスタのライト走査・
  シャドウのサンプリングのどれが効いているか。シェーディングアセット（L3）や SSGI を使うゲームが `deferred: true` へ戻すと効いてくる）。
- **固定分 約 4.5 ms の削減**（クラスタ構築 約 1 ms・トーンマップ 約 1.9 ms・UI・提示のコピー。描画スケールに関係なく画面の解像度で走る）。
  トーンマップ後の LDR 中間（Rgba16Float）と提示のコピーは論理サイズ（フル解像度）のまま（UI が無いときにトーンマップと提示をまとめる余地）。
- **横向き・動くシーン・影ありの計測**（`proj_bench` は静止しているので影の深度パスは省略されていた）と release の .so での計測。
- 重いシーンで 60 fps を割るなら `mobile` の描画スケールを 0.6 へ（0.75 との差は GPU 約 1.3 ms）。
- テクスチャの最大解像度・クラスタの分割数・Hi-Z・カスケード数（3 固定）のつまみは入れていない（計測で必要と分かったら足す）。
- 背景ゾーンのキャンバスと 3D 空間のキャンバスは描画解像度で描かれる（前面の UI だけが論理サイズ）。
- `mobile` は前方描画なので、シェーディングアセット（L3）・水面反射・コースティクス・SSGI／AO／反射は効かない（見た目が変わる）。
  シェーディングアセットを要求していれば警告を 1 回出す。見た目を優先するゲームは `deferred: true` で戻す。
- 目標 fps の上限（`target_fps`）は起動時に 1 回だけ当てる（`SEED.Application.TargetFps` は上限を当てた後の値を返す）。
- SeedAndroid / エディタの実行は品質の起動オプションを渡さない（計測は `am start` を直接使う）。
- 縦より横が速い理由（§7）は未調査のまま。

---

## 23. 実行中の差し替え（ホットリロード。段階D・2026-09-25）

端末でアプリを動かしたまま、保存したシーン・アセット・スクリプトを送って取り込ませる（利用者の要望「実機で動かしたまま、直したものを
すぐ反映したい」。段階D の項目「実行中の差し替え」）。エディタの Android の実行中（端末のアプリと IPC がつながっている＝§21）は、
**保存するだけで**端末へ届く。エディタ無しでも SeedAndroid の `push --assets` と `reload` で同じことができる（§23.8）。
命令は PC の Play と同じ 1 行 1 命令の形で、PC のランタイムでも使える（TCP・名前付きパイプとも。PC のエディタは従来どおり自分の経路を使う）。

### 23.1 全体の流れ

```
エディタ（Android の実行中・IPC がつながっている）
  アセットルートの監視（AndroidRun/AndroidHotReloadController。FileSystemWatcher。実行中〈Running / Paused〉だけ）
    → 変わったファイルを表で分ける（Android/HotReload/AndroidHotReloadTable。関係ないものは落とす）
    → 最後の変更から 0.6 秒静かになったら 1 組にまとめる（AndroidHotReloadChangeQueue。連続保存は最後の 1 回に）
  差し替え（Android/HotReload/AndroidHotReloadApplier）
    .cs        → SeedPak --scripts-only で DLL を作り直す → run-as で files/bin/ へ（Steps/ScriptBinaryPusher）→ RELOAD_SCRIPTS
    .scene・他 → 変わったファイルから参照をたどる（AssetCollector.CollectFrom）→ 端末の中身と違うものだけを選ぶ（AndroidOverlayPlanner）
               → run-as で files/assets/ へ足す（AndroidAssetOverlaySync。置いてあるものは消さない）
               → 送ったものごとに RELOAD_SCENE:<相対パス>（シーン）／RELOAD_ASSET:<相対パス>（他）
    → 命令をまとめて送り、応答を待つ（AndroidReloadCommandSender）→ Output に結果と所要時間
端末（ランタイム）
  IPC の受信（ipc.rs）→ 差し替えの要求を貯める → フレームの境界（process_ipc の最後。ゲームロジックの前）でまとめて適用（app/hot_reload_ops.rs）
    差し替えたアセットのキャッシュを捨てる → 要れば今のシーンを 1 回だけ読み直す（スクリプトのシーン遷移と同じ経路）→ 応答
  RELOAD_SCRIPTS … files/bin/ の SEEDUserScripts.dll を読み直す（collectible な AssemblyLoadContext の入れ替え。scripting/script_reload.rs）
  アセットの読み順 … デバッグ版は 上書き層 files/assets → APK の pak → APK の PAK 外（asset_fs::FilesystemLayer::Overlay。§23.4）
```

### 23.2 命令と応答（書式の正典はランタイムの `runtime/src/engine/core/app_base/hot_reload/wire.rs`）

| 命令 | 何をするか | 応答の宛先 |
|---|---|---|
| `RELOAD_SCENE` | 今のシーンをディスクから読み直す（無条件） | `scene` |
| `RELOAD_SCENE:<相対パス>` | 今のシーンがそのパスのときだけ読み直す（エディタがシーンを保存したとき。違うシーンなら送っただけで、そのシーンへ遷移したときに反映） | `scene:<相対パス>` |
| `RELOAD_ASSET:<相対パス>` | そのアセットのキャッシュを捨て、種類に応じて取り込み直す（§23.3） | `asset:<相対パス>` |
| `RELOAD_SCRIPTS`（従来の命令） | PC: その場で再コンパイル（従来どおり）。Android（同梱 .NET）: files/bin/ 等の DLL を読み直す（§23.6） | 応答は従来の `SCRIPTS_RELOADED:<型数>,<再生成数>`（失敗は `SCRIPTS_RELOADED:-1,<理由>`） |

- `<相対パス>` はアセットルートからの相対パス（区切りは `/` か `\`、`assets://` 付きでもよい）。**絶対パス・`..` は受け付けない**
  （アプリのアセットの外を指させない）。受け付けないときも `RELOAD_FAILED` で理由を返す（相手を応答待ちのまま待たせない）。
- 応答（欄の区切りは `|`。Windows のファイル名に使えない文字なのでパスと混ざらない）:
  `RELOAD_DONE:<宛先>|<端末での所要ミリ秒>|<詳細>`（詳細は `inplace` か `scene:<読み直したシーン>`）／
  `RELOAD_SKIPPED:<宛先>|<理由>`（適用しなかった。失敗ではない）／`RELOAD_FAILED:<宛先>|<理由>`。
- **同じフレームに届いた要求はまとめて適用する**（シーンの読み直しは何件あっても 1 回。宛先が重なった要求の応答は 1 行）。
  状態の流れ（受付 → 計画 → 適用 → 応答）と規則は `hot_reload/batch.rs`（単体テスト付き）。
- 一時停止（`PAUSE`）中に差し替えても一時停止のまま（新しいシーンの `OnStart` は再開してから走る）。
- Edit モード（PC のエディタの埋め込みランタイム）ではシーンを読み直さない（`RELOAD_SKIPPED`。エディタは自分の `LOAD_SCENE` を使う）。
  キャッシュを捨てるだけの差し替えは Edit でも効く。
- エディタ側の文字列は `editor/src/Ipc/RuntimeIpcCommands.cs`、応答の照合は `Android/HotReload/AndroidReloadReply.cs`。

### 23.3 アセットの種類と取り込み方（ランタイムの表 `hot_reload/asset_kind.rs`）

| 種類 | 拡張子（例） | 取り込み方 | ゲームの状態 |
|---|---|---|---|
| 画像 | png・jpg・jpeg・bmp・tga・webp・gif・ktx2・dds | キャッシュを捨てるだけ（スプライト・UI・インライン画像は次のフレームで読み直す） | 保つ |
| シェーダ | wgsl・shading | シェーディングアセット・水面シェーダの追跡状態を捨てる（次のフレームで読み直してビルド） | 保つ |
| スプライトのポスト・スキンメッシュ・アイコン集 | postfx・sprite_mesh・icons | キャッシュを捨てるだけ | 保つ |
| 音声 | wav・ogg・mp3・flac | キャッシュを捨てるだけ（次に鳴らすときに読み直す。鳴っている音は鳴り終わるまで古いまま） | 保つ |
| モデル | glb・gltf・obj・mtl・bin | CPU モデル・非同期ロードの完成品・統合バッチ・RT の BLAS を捨て、**今のシーンを読み直す** | シーンの開始時へ |
| プレハブ・マテリアル・アニメーション・入力マップ・地形 | actor・actor2d・mat・anim・inputmap・tvox・tscatter・tcover | 今のシーンを読み直す | シーンの開始時へ |
| シーン | scene | 今のシーンならシーンを読み直す（違えば `RELOAD_SKIPPED`） | シーンの開始時へ |
| フォント・プロジェクト設定 | ttf・otf・ttc・`project_settings.json` | 差し替えられない（起動時に 1 回だけ読む。`RELOAD_SKIPPED`。アプリを起動し直す） | — |
| スクリプト | cs | `RELOAD_ASSET` では扱わない（`RELOAD_SCRIPTS`） | — |
| 表に無いもの | — | 今のシーンを読み直す（取り込み方が分からないものは、シーンごと読み直すのが確実） | シーンの開始時へ |

- 小さく読み直しが安いプロセス全体のキャッシュ（マテリアルの定義・`.postfx` の定義・インライン画像）は、どの差し替えでも丸ごと捨てる。
  スクリプトの `Scene.Preload` で読んでおいたシーンも捨てる（古い中身のため）。
- キャッシュのキーの表記揺れ（`assets://`・アセットルート内の絶対パス・相対パス・区切り・大文字小文字）は `hot_reload/asset_key.rs` が吸収する。
- 表だけを直せば種類を足せる（`ASSET_KIND_TABLE`・`FILE_NAME_TABLE`）。エディタ側の「どの変更で何の命令を送るか」は別の表
  （`Android/HotReload/AndroidHotReloadTable.cs`: `.cs`＝スクリプト・`.scene`＝シーン・`packaging_settings.json`・`obj/`・`bin/`・一時ファイル＝無視・他＝アセット）。

### 23.4 上書き層（`files/assets` を pak より先に読む）

| APK | 仮想パス `assets://<相対パス>` を読む順 |
|---|---|
| デバッグ版（run-as で送れる APK。SeedAndroid・エディタが作るもの）のパッケージ実行 | **上書き層 `files/assets/<相対パス>`** → APK の pak → APK の PAK 外 |
| 配布版・PC の配布物 | 従来どおり pak → APK の PAK 外 → `files/assets`（読む順は変えない） |
| pak の無い開発用の APK（`--assets-dir`） | 従来どおり `files/assets` だけ（元から唯一の層） |

- 上書き層に**置いたファイルだけ**が pak より優先される（無いものは従来どおり pak から読む）。読むたびにファイルの有無を見るので、
  起動の後に送ったものも次に読むときから効く（`run` は起動の前に上書きを消すので、起動の時点では空がふつう）。
- 「デバッグ版か」は、MainActivity がデバッグ版の APK（FLAG_DEBUGGABLE）のときだけ必ず起動オプションを渡す（§20.10）ことで見分ける
  （`runtime/android/native/src/launch_options.rs` の `was_delivered`。判断は `engine::asset_fs::overlay_allowed`。単体テスト付き）。
  起動ログ: `上書き層: …/files/assets に置いたアセットを pak より先に読みます（デバッグ版の差し替え・…）`・
  `[SEED INIT] asset_fs: 上書き層 … を pak より先に読みます`。
- 読む順は `engine::asset_fs::read_virtual_layers`（`FilesystemLayer::Fallback` / `Overlay`）。上書き層あり・なし・送った後に現れるファイル・
  どこにも無いときのエラーを単体テストで固定した。
- **解除**: APK の内容を正とする場面（`--project`）では、`files/bin/`（§17.7）と同じ規則で自分のアプリの `files/assets/` を消す
  （あれば Output に `差し替えで送ったアセットの上書きを解除しました（端末の files/assets/ を消し、APK の pak のアセットを使います）。` の 1 行）。
  場面は 2 つ: **APK を入れ直した直後**（`install`・`run` のインストールの工程。`adb install -r` はアプリのデータを残すため）と、
  **`run` の起動の前**（止めた後。APK を入れ直さなかった `run` でも消す）。同じ APK が入っていてインストールを飛ばした `install` では消さない。
  `push`・開発用の `--assets-dir` は消さない。規則・消し方・記録の作り直しは `Steps/PushedOverrides`（単体テスト `PushOverrideTests`・`HotReloadTests`）。
  消せなかったときは警告の 1 行を出して続け、送った記録（§23.5）は残す（端末に残った上書きと記録が合ったまま＝差分を取り違えない）。
  手で消すなら `adb exec-out run-as <アプリ ID> rm -rf files/assets`。

### 23.5 差分の選び方（`Android/HotReload/AndroidAssetOverlaySync`・`AndroidOverlayPlanner`）

1. 候補: エディタの差し替えは、変わったファイルを起点に参照をたどった閉包（`AssetCollector.CollectFrom`。シーンを保存したら、
   シーンと参照するアセット）。SeedAndroid の `push --assets <フォルダ>` は、今 pak を作ると入るもの＋アセットの中の全シーンから辿れるもの
   （SeedPak と同じ収録の規則）のうち、そのフォルダの中。
2. 手元の中身は **pak に入れるときと同じ形**で読む（シーン・プレハブ・`.json` 等は中の絶対パスを `assets://` へ書き換える。書き換えの正典は
   `Packaging/Pak/PakEntryContent`。PakWriter と共有）。指紋は SHA-256。
3. 端末の中身の指紋は、上書き層へ送った記録（`<プロジェクト>/cache/android/asset_overlay.json`。端末ごと）→ 無ければ APK の pak のエントリ
   （置き場の `runtime/android/app/src/main/assets/seed/assets.pak` を、必要なエントリだけ読んで SHA-256。`AndroidPakContentIndex`）。
4. 端末に無い・中身が違うものだけを送る。同じもの（保存し直しただけ・参照先の変わっていないアセット）は送らない。
- APK の pak と比べるのは、記録の土台の pak（上書きを消したとき〈§23.4〉に大きさと更新時刻を記録する）が今の置き場の pak と同じときだけ。
  記録が無い・その後にビルドして置き場の pak が変わったときは pak と比べず、送ったことの無いものを送る（多めに送るだけで、古いものが残ることはない。
  Output に黄色の 1 行で理由）。
- 送った記録は上書きを消したとき（APK を入れ直した直後・`run` の起動の前）に「送ったもの無し」に戻る。`asset_overlay.json` は `run_state.json` と別のファイル
  （エディタの実行中は中核が `run_state.json` を持ったまま最後に書き戻すため）。
- 削除したファイルは端末へ伝えない（上書き層に残れば使われ続ける。`run` で消える。§23.11）。

### 23.6 スクリプトの差し替え（`RELOAD_SCRIPTS`）

- エディタ: `.cs` を保存したら SeedPak `--scripts-only`（`push` と同じ中身。`scripting/` もビルドする）で DLL を作り直し、run-as で
  `files/bin/` へ送ってから `RELOAD_SCRIPTS`（コンパイルエラーなら送らずに Output に理由。端末は今のスクリプトのまま）。
- ランタイム（同梱 .NET）: 起動のときと同じ候補（`files/bin/` → 外部の `files/bin/` → APK の `bin/`）から「SEEDScripting.dll がある最初の置き場」を
  選び直し、そこの `SEEDUserScripts.dll` を読む（起動の後に送った `files/bin/` が選ばれる。`scripting/script_reload.rs`）。
  - **スクリプトホスト（SEEDScripting.dll）が起動時と違えば読まない**（`SCRIPTS_RELOADED:-1,スクリプトホスト…が起動時と違います…`）。
    ホストは起動時に Default の AssemblyLoadContext へ読むので差し替えられず、ユーザースクリプトはそのビルドのホストに対してコンパイルされるため。
    `scripting/` を変えたときはアプリを起動し直す（`run` / `push`）。照合は FNV-1a 64bit（`prefab_hash::content_hash_bytes`）。
  - DLL を**先に読んでから**今のスクリプトのインスタンスを捨てる（読めないときは今のスクリプトのまま続く）。
  - C# 側 `ScriptAssemblyManager.LoadPrecompiledBytes` は、新しい DLL を別のロードコンテキストへ読めてから旧アセンブリをアンロードするようにした
    （壊れた DLL では旧アセンブリのまま＝`CompileAndLoad` と同じ。起動時の 1 回目の振る舞いは変わらない。`ScriptPrecompileTests` に 1 件追加）。
  - ログ: `[SEED] スクリプトを読み直しました: N 型・再生成 M 件（<置き場>/SEEDUserScripts.dll・K KiB・T ms）`。
- PC の `RELOAD_SCRIPTS` は従来どおり（その場で再コンパイル。同梱 .NET の起動材料が無いときの経路）。
- スクリプトの差し替えは全インスタンスの作り直し（`OnStart` の再実行。PC の Play 中の自動再読み込みを既定で保留にしている理由と同じ副作用。
  [editor_auto_reload.md](editor_auto_reload.md) §1）。Android の差し替えは「動かしたまま反映する」機能なので保留しない。

### 23.7 エディタの段取り（`AndroidRun/AndroidHotReloadController`。WPF 非依存・単体テスト `AndroidRunUiTests`）

- Android の実行で端末のアプリが動いている間（Running / Paused）だけアセットルートを監視する（書き込み・作成・名前の変更）。
  実行が終わったら監視を止め、覚えた変更を捨てる。
- 変更は相対パスごとに 1 回だけ覚え、最後の変更から 0.6 秒静かになったら 1 組にする（`AndroidHotReloadChangeQueue`。ScriptAutoReloader と同じ間隔）。
  差し替えの途中に来た変更は覚えておき、終わった後に次の組にする（同時に 2 組は走らせない）。
- 端末のアプリとつながっていなければ（段階D-1 より前の APK・起動の途中）、差し替えずに警告の 1 行を出して捨てる。
- スクリプト・シーンは、エディタの設定「表示 > スクリプト > スクリプトを自動再読込」「表示 > シーン > シーンを自動再読込」がオフなら差し替えない。
  そのほかのアセットはいつも差し替える。
- シーンはディスク上のファイルから送る（未保存の変更は送らない。保存したときに送られる）。
- PC の Play の自動再読み込み（ScriptAutoReloader・SceneAutoReloader）と、PC の Play の振る舞いは変えていない。
- Output の行（`AndroidRun/AndroidHotReloadOutputFormatter`。色の規約は [editor_ui_style.md](editor_ui_style.md) 7 章）:

```
[Android] 差し替え: 2 件の変更（scenes/Main.scene ほか）を端末へ送ります                         … 水色
[Android]     > SeedPak --project … --out … --scripts-only                                  … 黄（途中の説明・SeedPak の出力）
[Android]     スクリプト: DLL を作り直して送りました（5 ファイル・9.1 MB を送りました（…）・6.2 秒）  … 灰
[Android]     アセット: 1 ファイル・0.0 MB を送りました（候補 3・端末と同じ 2・0.4 秒）             … 灰
[Android]     反映: スクリプト — スクリプトを読み直しました（1 型・再生成 1 件）                     … 水色
[Android]     反映: scenes/Main.scene — シーンを読み直しました（assets://scenes/Main.scene）（端末 123.4 ms） … 水色
[Android]     反映しませんでした: fonts/a.ttf — 起動時に 1 回だけ読むアセットのため…                 … 黄
[Android]     失敗: models/a.glb — …                                                          … 赤
[Android] 差し替え完了（7.5 秒）／差し替えで失敗がありました（…）                                  … 水色／赤
```

### 23.8 SeedAndroid の `push --assets` と `reload`（エディタ無しで確かめる）

```powershell
# run（か push）で起動したアプリへ（接続トークンは run_state.json の起動の記録。pause / resume と同じ）
# 1) 端末と違うアセットだけを上書き層へ送る（起動し直さない。アセットルートかその中のフォルダ）
dotnet run --project editor/tools/SeedAndroid -- push --assets D:\path\to\Project\assets --project D:\path\to\Project --serial <実機>
# 2) 取り込ませる
dotnet run --project editor/tools/SeedAndroid -- reload scene   --project D:\path\to\Project --serial <実機>
dotnet run --project editor/tools/SeedAndroid -- reload asset models/Fish.glb --project D:\path\to\Project --serial <実機>
# 3) スクリプト: DLL を作り直して files/bin/ へ送り、読み直す（SeedPak --scripts-only → RELOAD_SCRIPTS）
dotnet run --project editor/tools/SeedAndroid -- reload scripts --project D:\path\to\Project --serial <実機>
```

- `push --assets` は `--project` が無ければフォルダからプロジェクトを探す。`--assets-dir`（開発用の APK）とは別物で同時に使えない。`--serial auto` は使えない。
  アプリが動いていなくても送れる（次に起動したときから上書き層として読まれる）。
- `reload` の終了コード: 適用した・適用しなかった（`RELOAD_SKIPPED`。理由を出す）は 0、失敗・応答なしは 5、DLL の作り直しの失敗は 4。
  最後に `DETACH` を送ってから閉じる（端末の一時停止は据え置く）。エディタの実行中はエディタがつながっているので使えない（1 本だけ。§21.10）。

### 23.9 確認方法

```powershell
# 1) ランタイムの単体テスト（読む順・命令の解釈・フレーム内のまとめ方・キャッシュのキーの照合・スクリプトの置き場の選び直し）
cd runtime; cargo test --lib -- hot_reload asset_fs script_reload read_loop
# 2) エディタ側の単体テスト（表・応答の照合・差分の選び方〈PakWriter の pak と同じ指紋〉・記録・tar・命令の送り方・引数／まとめ方・監視・Output）
dotnet run --project editor/tests/AndroidPipelineTests
dotnet run --project editor/tests/AndroidRunUiTests
# 3) PC で TCP 越しに（SEED.exe --mode=play --assets-root=<assets> --ipc-port=<ポート> --ipc-token=<16 文字以上>。つないだら HELLO:<トークン>）
#    ファイルを書き換えて RELOAD_SCENE / RELOAD_SCRIPTS / RELOAD_ASSET:<相対パス> を送り、SCREENSHOT:game,<パス> で撮る
# 4) 実機（§23.8 の順。run で起動したデバッグ版のアプリへ）
#    アセットを書き換え → push --assets <P>\assets → reload asset <相対パス>（か reload scene）→ screenshot で見た目を比べる
#    スクリプトを書き換え → reload scripts → logcat にスクリプトの新しい文言
#    最後に通常の run → Output に「差し替えで送ったアセットの上書きを解除しました」、元の見た目・文言に戻る
```

### 23.10 確認結果（2026-09-25〜26）

| 確認 | 結果 |
|---|---|
| ランタイムの単体テスト | 命令の解釈（6）・種類の表（4）・キャッシュのキーの照合（4）・フレーム内のまとめ方と応答の状態（8）・上書き層の読む順と許可（新規 6＋既存の順のテスト）・スクリプトの置き場の選び直し（3）・IPC の受信（1）。`cargo test --lib` 全体も通過（既知の不安定なテストを除く） |
| エディタ側の単体テスト | `AndroidPipelineTests` 104 件（差し替え 12 件と上書きの解除 1 件を追加。差分の選び方・pak と同じ指紋・記録の大小無視・tar の親フォルダ・命令と応答の照合・応答待ちの時間切れ・インストールの後／起動の前の解除と記録の合わせ方）、`AndroidRunUiTests` 74 件（9 件を追加。0.6 秒のまとめ・差し替え中の変更の持ち越し・接続なしの警告・設定オフの種類の除外・Output の書式）、`PackagingCollectorTests` 48 件、`ScriptPrecompileTests` 16 件（1 件を追加。壊れた DLL の読み直しで旧アセンブリが残る） |
| PC・TCP（`SEED.exe --mode=play --ipc-port --ipc-token`、Windows） | `RELOAD_SCENE`: 太陽の色・モデルの配置を変えたシーンを読み直し、画面が変わった（端末内 33.1 ms・往復 37 ms）。`RELOAD_ASSET:models/BrainStem.glb`: 別のモデルで上書きしたファイルに差し替わった（キャッシュ 2 件を捨てシーンを読み直し 15.1 ms）。`RELOAD_SCRIPTS`: 文言 v2 → v3 に変わった（往復 335 ms。PC は従来の再コンパイル）。3 件を 1 フレームに送ると読み直しは 1 回（5.1 ms・応答 3 行）。一時停止中の差し替えは一時停止のまま（RESUME の後に新しいシーンの `OnStart`）。不正なパス（`..`）は `RELOAD_FAILED`、フォント・今でないシーン・`.cs` は `RELOAD_SKIPPED` |
| PC の Play（名前付きパイプ）が変わらないこと | エディタと同じ `PipeServer` から `RELOAD_SCRIPTS` → `SCRIPTS_RELOADED:1,1`（288 ms）、`LOAD_SCENE` → `SCENE_LOADED`（48 ms）、終了コード 0 |
| ビルド | ランタイム（Windows）・arm64 の `libSEED.so`（`cargo ndk`・111 秒）・デバッグ APK、エディタ（エラー 0・警告はすべて既存のファイル）、SeedAndroid・SeedPak |
| 実機の起動（Pixel 6a・`proj_probe`・SeedAndroid） | `run`（インストールと起動。27.7 秒）。logcat に `上書き層: …/files/assets に置いたアセットを pak より先に読みます（…まだ差し替えはありません…）`・`[SEED QUALITY] preset=mobile render_scale=0.75 …`・約 59 fps。インストールの工程が、前から端末に残っていた `files/assets` を消した（`差し替えで送ったアセットの上書きを解除しました…` の 1 行。以前の開発用の `--assets-dir` の実行の残りと見られる） |
| 実機・モデル | `models/BrainStem.glb` を別のモデル（`camera.glb`）で上書き → `push --assets`（0.9 秒。候補 4・端末と同じ 3・送ったのは 1 ファイル 3,948 バイト）→ `reload asset models/BrainStem.glb`（応答 58 ms・端末でキャッシュ 2 件を捨ててシーンを読み直し 42.1 ms）→ 画面のモデルが差し替わった |
| 実機・シーン | 太陽の色を青に変えて保存 → `push --assets`（0.9 秒・1 ファイル）→ `reload scene`（応答 39 ms・端末 15.2 ms）→ 光が青くなった |
| 実機・スクリプト | 文言 v2 → v3 → `reload scripts`（全体 11.2 秒＝SeedPak `--scripts-only` と 5 ファイル 9.1 MB の転送 10.5 秒・応答 54 ms）。同じプロセスのまま logcat に `[SEED] スクリプトを読み直しました: 1 型・再生成 1 件（…/files/bin/SEEDUserScripts.dll・14 KiB・29.7 ms）` と `[PROBE v3] OnStart` |
| 実機・上書きの解除 | 手元を元に戻して `run` → pak・Gradle をやり直し（63.7 秒）、**インストールの工程**で DLL とアセットの解除の 2 行 → `run-as … ls files` に `assets`・`bin` が無い・スクリプトの置き場は `apk:seed/bin/`・`[PROBE v2]`・画面は最初のスクリーンショットと同一（PNG の MD5 一致）。続けて上書きを作り直し（`push --assets`・`reload scripts`）、同じ APK のまま `run`（13.6 秒）→ インストールは飛ばし、**起動の工程**で同じ 2 行 → 同じく元どおり（MD5 一致） |
| エディタの画面からの操作 | 未確認（エディタを起動しない制約。WPF 非依存の段取りは単体テストで、実行時の配線は `MainWindow.AndroidRun.cs` の読み合わせのみ） |

### 23.11 制限・持ち越し（[backlog.md](backlog.md) の「Android」節）

- モデルが外部の画像を参照している（`.gltf` の外部 `uri`・`.mtl` の画像）とき、画像だけを差し替えてもモデルのテクスチャは変わらない
  （画像は `InPlace` でスプライト等のキャッシュだけを捨てる）。モデルかシーンを保存し直すと入る。
- 空（スカイボックス）・パーティクルの形状など、今回キャッシュを捨てる対象に入れていないものがある（シーンの読み直しで作り直すものは入る）。
- フォント・`project_settings.json`・スクリプトホスト（`scripting/`）の変更は差し替えられない（アプリを起動し直す）。
- 削除したファイルは端末へ伝えない（上書き層に残れば使われ続ける。`run` で消える）。
- 同じ APK が入っていてインストールを飛ばした `install`（と `run --no-launch`）では上書きを消さないので、その後ランチャーから起動すると
  差し替えた内容のまま動く（APK を入れ直すか、`run` で起動し直せば消える。§23.4）。
- `run` 以外で端末のアプリのデータを消した（設定からのデータ消去・手でのアンインストール）後は送った記録が古くなり、手元で変えていないファイルは送られない
  （`run` をやり直すと揃う）。
- 端末で展開している最中に同じファイルが読まれると途中の中身を読みうる（命令は展開の後に送るので、ふつうは起きない）。
- アプリが背面にあると、端末のフレームが回らないので命令は前面へ戻るまで処理されない（エディタは 60 秒で「応答なし」）。
- シーンの読み直しはゲームの状態をシーンの開始時へ戻す（スクリプトの変数・位置。PC の Play の自動再読み込みと同じ）。
  スクリプトの差し替えは全インスタンスの作り直し（`OnStart` の再実行）。
- エディタの画面からの確認（Android の実行中に保存 → Output の行）は未実施（§23.10。実機の差し替えは SeedAndroid で確かめた）。

---

## 24. 配布（署名付きの release APK / AAB・アイコン・Google Play の要件・NativeAOT の評価。段階D・2026-09-26）

Google Play へ出せる配布物を作れるようにした。**開発用（debug）**は従来どおりデバッグ用の鍵で署名した APK（run-as・push・差し替え・IPC が使える）、
**配布用（release）**は debuggable にせず・INTERNET 権限を入れず・アップロード鍵で署名した APK / AAB（Rust は常に `--release`）。
手順の中身は中核（`editor/src/Android/`）にあり、SeedAndroid とパッケージ化ウィンドウが同じクラスを使う（§4.6）。

### 24.1 結論

- 配布用のビルドは `SeedAndroid build --variant release --format aab --project <P> --keystore <キーストア> --key-alias <別名>`
  （パスワードは環境変数 `SEED_ANDROID_KEYSTORE_PASSWORD` か対話の入力）か、パッケージ化ウィンドウの「ビルドの種類 = 配布用」。
  鍵の場所はプロジェクトの `packaging_settings.json` の `android.signing` にも置ける（パスワードは置かない）。
- **鍵が無い・開けないときはビルドを始めない**（デバッグ署名・無署名の配布物は作らない。中核の準備で `keytool -list` まで確かめる。
  Gradle 側も `preReleaseBuild` で止める二重の守り）。
- **targetSdk を 35 → 36 に上げた**（Google Play は 2026-08-31 以降の新規・更新に API 36 以上を求める。§24.2）。Android 16 の
  予測型の「戻る」で KEYCODE_BACK が届かなくなるのを `android:enableOnBackInvokedCallback="false"` で止めた（戻るキー → Escape は従来どおり。§14.5）。
- アイコンはプロジェクト設定 `android.icon`（PNG）から各密度の mipmap とアダプティブアイコン（前景＋背景色 `android.icon_background`）を
  ビルドのときに生成する（NuGet を足さない .NET 標準だけの PNG の読み書き。§24.7）。
- 配布用のビルドは、ビルドの前（設定）と後（できた配布物を aapt2・zipalign・apksigner / keytool・ELF で読み直す）に Google Play の要件を
  確かめて一覧に出す（§24.8）。不合格があっても配布物は作り、SeedAndroid は終了コード 6 で知らせる。

### 24.2 Google Play の要件（2026-09-26 に公式のページで確かめた。表は `runtime/android/play_requirements.json`）

| 要件 | 内容 | SEED の対応 | 出どころ |
|---|---|---|---|
| targetSdk | **2026-08-31 以降の新規アプリ・更新は Android 16（API 36）以上**（延長を申請すれば 2026-11-01 まで）。既存アプリが新しい利用者に見え続けるには API 35 以上 | `seedTargetSdk = 36`（`compileSdk` も 36） | [target-sdk](https://developer.android.com/google/play/requirements/target-sdk) |
| 形式 | 新しいアプリは AAB（Play App Signing が前提。アップロード鍵で署名して出し、配る APK は Google が署名する） | `--format aab`（`bundleRelease`） | Play Console |
| 64 bit | 32 bit の .so を入れるなら 64 bit 版も必須 | 64 bit だけを作る（配布の既定は arm64-v8a） | Play Console |
| 16 KB ページ | Android 15 以上を対象とするアプリは 64 bit 端末の 16 KB ページに対応（対応していない更新は 2027-02-01 から出せない）。.so の LOAD の整列と、非圧縮の .so の zip 内の位置 | NDK r28 の既定で 0x4000 整列。.so は圧縮して入れる（`useLegacyPackaging`。§24.6） | [page-sizes](https://developer.android.com/guide/practices/page-sizes) |
| debuggable | debuggable の APK / AAB は受け付けない | release の `isDebuggable = false` | Play Console |
| アプリ ID | `com.example` で始まる ID は受け付けない（Play Console の「"com.example" is restricted」） | 要件チェックで不合格。エンジンの既定 ID `com.seedengine.runtime` は注意 | Play Console のエラー |

targetSdk 36 で効く動作の変更（[behavior-changes-16](https://developer.android.com/about/versions/16/behavior-changes-16)）と対処:

| 変更 | 影響 | 対処 |
|---|---|---|
| 予測型の「戻る」が既定になり、`onBackPressed` が呼ばれず `KEYCODE_BACK` がアプリへ届かない | GameActivity が戻るキーをネイティブへ渡せず、スクリプトの `Input.GetKeyDown(KeyCode.Escape)`（§14.5）が効かなくなる | マニフェストの `<application android:enableOnBackInvokedCallback="false">`（公式の一時的な回避。将来の Android で効かなくなったら `OnBackInvokedCallback` へ移す。backlog） |
| エッジツーエッジの無効化（`windowOptOutEdgeToEdgeEnforcement`）の廃止 | 無し（35 で既に強制。無効化はしていない。安全領域は §15） | — |
| 最小幅 600dp 以上の画面で向き・サイズ変更・縦横比の制限を無視 | ゲーム（`android:appCategory="game"`）は対象外 | 既に `appCategory="game"` |
| その他（`elegantTextHeight`・JobScheduler・健康の権限・Bluetooth 等） | SEED は使っていない | — |

targetSdk 36 の APK（開発用・配布用とも）は、ビルド・マニフェストの中身（`aapt2`）・lint vital までは確かめたが、**実機での動作（戻るキーが
アプリへ届くこと等）は未確認**（§24.11・backlog）。

### 24.3 鍵（キーストア）の作成と保管

```powershell
# 新しいアップロード鍵を作る（PKCS12・RSA 2048・10000 日。既にあるファイルは上書きしない。アセットフォルダの中には作らない）
$env:SEED_ANDROID_KEYSTORE_PASSWORD = '<パスワード>'     # 省略すると対話で 2 回聞く（画面に出さない）
dotnet run --project editor/tools/SeedAndroid -- keystore create --keystore D:\keys\mygame_upload.jks --key-alias upload --cert-name "My Game"
```

- 中身は `keytool -genkeypair … -storepass:env / -keypass:env`（パスワードは子プロセスの環境変数だけで渡す。コマンドラインに出さない）。
  パッケージ化ウィンドウの「この場所に新しいキーストアを作る」も同じ関数（`Signing/AndroidKeystoreTool.CreateAsync`）で、作ったらパスワードを保護保存する。
- これが Google Play の **アップロード鍵**になる。失うと Google に鍵の再設定を頼むまで更新を出せない:
  キーストアとパスワードを別々の安全な場所に控える・リポジトリとプロジェクトのアセットフォルダに置かない
  （アセットの中は中核が拒む。プロジェクトのフォルダの中は警告。`runtime/android/.gitignore` は `*.jks` / `*.keystore` を追跡しない）。
- PKCS12 のキーストアはキーとキーストアのパスワードが同じ（keytool は違う `-keypass` を無視する）。キーのパスワードを省くとキーストアと同じものを使う。

### 24.4 配布用のビルド

```powershell
$env:SEED_ANDROID_KEYSTORE_PASSWORD = '<パスワード>'
# AAB（Google Play へ出す）。--format aab か --keystore があれば --variant release は省ける。--release は Rust の最適化（配布用は常に）
dotnet run --project editor/tools/SeedAndroid -- build --release --format aab --project D:\path\to\Game --keystore D:\keys\mygame_upload.jks --key-alias upload
# 配布用の APK（Google Play 以外・手元の端末で試す。開発用とは署名が違うので同じアプリ ID の開発用とは共存できない）
dotnet run --project editor/tools/SeedAndroid -- install --variant release --project D:\path\to\Game --serial <実機>
# ビルドをせずに確かめる（設定と、前回の配布物）
dotnet run --project editor/tools/SeedAndroid -- check --project D:\path\to\Game --format aab
```

| 項目 | 開発用（debug） | 配布用（release） |
|---|---|---|
| Gradle のタスク | `assembleDebug` | `assembleRelease`（APK）/ `bundleRelease`（AAB） |
| 出力 | `app/build/outputs/apk/debug/app-debug.apk` | `app/build/outputs/apk/release/app-release.apk` / `app/build/outputs/bundle/release/app-release.aab` |
| 署名 | この PC のデバッグ用の鍵 | アップロード鍵（`seed.signing.*`。無ければ止める） |
| debuggable・INTERNET | あり（`src/debug/` のマニフェスト） | なし |
| Rust | `--release` を付けたときだけ最適化 | 常に `--release`（AGP が .so のシンボルを削る） |
| ABI の既定 | 端末から判定（無ければ両方） | `build` は arm64-v8a（たまたまつながっている端末で変えない）。`install` / `run` は入れる端末の ABI |
| 起動オプション（シーン・IPC）・run-as・push・差し替え | 使える | 使えない（MainActivity は debuggable のときだけ起動オプションを渡す。push・`--assets-dir`・`--push-scripts` は指定の誤り） |
| 記録のキー（`step_stamps.json`） | `gradle`（従来） | `gradle/release_apk`・`gradle/release_aab`（出力が別なので切り替えても互いを作り直さない） |

- 準備で決めたこと（署名の鍵・証明書の SHA-256・ビルドの前の要件の一覧）は Output / コンソールに出る。パスワードは `********`。
- AAB は端末へ直接入れられないので `install` / `run` と `--format aab` は一緒に使えない（試すなら §24.10 の bundletool）。
- 配布用の APK を `install` すると、署名の違う同じ ID の開発用が入っている端末では `INSTALL_FAILED_UPDATE_INCOMPATIBLE` になる。中核は
  勝手にアンインストールしない（データが消えるため。エラーに `adb uninstall` の案内を出す）。

### 24.5 パスワードの受け渡し（ファイル・コマンドラインに残さない）

| 出どころ | 使う場面 | 置き場・渡し方 |
|---|---|---|
| エディタの保護保存 | パッケージ化ウィンドウの「署名」の「保存」 | `editor/settings/android_signing_secrets.json`（追跡しない）。キーストアの絶対パス × 別名ごとに DPAPI（CurrentUser）で包んだ Base64（`Settings/AndroidSigningSecretStore`・`AndroidSigningDpapiProtector`。アカウントの秘密鍵と同じ仕組みで、追加エントロピーは別の文字列）。この PC のこの Windows ユーザーだけが解ける |
| 環境変数 | SeedAndroid・CI | `SEED_ANDROID_KEYSTORE_PASSWORD`（キーが違えば `SEED_ANDROID_KEY_PASSWORD`） |
| 対話の入力 | SeedAndroid（環境変数が無く、標準入力がコンソールのとき） | 画面に出さずに読む（`ConsoleSecretPrompt`） |

- 中核はパスワードを `AndroidRunRequest.SigningSecrets`（`[JsonIgnore]`・`ToString` は伏せ字）でメモリの中だけで持ち、Gradle へは
  環境変数 `ORG_GRADLE_PROJECT_seed.signing.storePassword` / `keyPassword`（Gradle の仕様で `-P` と同じプロジェクトプロパティ）で渡す。
  ログの一覧と `step_stamps.json` の指紋には伏せ字だけが載る（ハッシュも残さない）。keytool へは `-storepass:env` で子プロセスの環境変数から読ませる。
- Gradle のデーモンはビルドのたびにクライアントの環境変数に合わせるため、配布用のビルドの後も次のビルドまでデーモンの環境にパスワードが残る
  （同じ Windows ユーザーのプロセスからは読める。DPAPI と同じ前提）。気になるときは `gradlew --stop`（backlog: 配布用だけ `--no-daemon`）。

### 24.6 AAB と `useLegacyPackaging`（.so を圧縮して入れる設定を配布でも使う根拠）

同梱 .NET は dotnet-root 形式のフォルダの実ファイル（nativeLibraryDir へのシンボリックリンク）を前提にするので、.so をインストール時に
nativeLibraryDir へ展開させる `packaging.jniLibs.useLegacyPackaging = true` を使っている（§17.4）。これは配布（AAB・Google Play）でも使える:

- AGP の公式の設定（AGP 4.2 の「Use the DSL to package compressed C/C++ libraries」。`extractNativeLibs` の置き換え。
  [release notes](https://developer.android.com/build/releases/past-releases/agp-4-2-0-release-notes)）。非圧縮（既定）を勧めるのは
  インストールの大きさ・読み込みの速さのためで、圧縮を禁じてはいない。
- 16 KB ページの公式の手引き（[page-sizes](https://developer.android.com/guide/practices/page-sizes)）も、圧縮した .so を 16 KB 対応の
  方法の 1 つとして挙げている（zip の中の位置の 16 KB 整列が要るのは **非圧縮の** .so だけ。圧縮した .so はインストール時にファイルへ展開される）。
  .so の LOAD の 16 KB 整列（NDK r28 の既定）は圧縮の有無に関係なく要る → 要件チェックで ELF を読んで確かめる（§24.8）。
- AAB から端末ごとの APK を作る bundletool（Google Play と同じ道具）で APKs にすると、master の APK は `extractNativeLibs=true` のまま、
  ABI の分の APK の .so も圧縮のまま（＝端末はインストール時に nativeLibraryDir へ展開する。開発用の APK と同じ形）であることを確かめた（§24.11）。
  **実機へ入れて同梱 .NET が起動するところは未確認**（§24.11・backlog）。開発用の APK は同じ形で実機で動いている（§17）。
- 代わり（`useLegacyPackaging = false`）にするには、.so を APK の中から直接読ませる形へ同梱 .NET の置き方を変える必要がある
  （hostfxr / CoreCLR がファイルの dotnet-root を前提にするため。backlog）。

### 24.7 アイコン（プロジェクト設定 `android.icon` / `android.icon_background`）

| キー | 意味 |
|---|---|
| `icon` | 元の PNG（アセットルートからの相対パス・`assets://…`・絶対パス）。空ならシステムの既定のアイコン（従来どおり） |
| `icon_background` | アダプティブアイコンの背景色（`#RRGGBB` / `#AARRGGBB`。既定は白） |

ビルドのたびに（APK の工程の Gradle の前）`app/src/seedIcon/res/`（生成物・追跡しない）へ次を置き、`-Pseed.launcherIcon=generated` で
マニフェストの `android:icon` を `@mipmap/ic_launcher` にする（渡さなければ `@android:drawable/sym_def_app_icon`＝従来と同じ見た目）。

| 生成物 | 大きさ | 中身 |
|---|---|---|
| `mipmap-{mdpi,hdpi,xhdpi,xxhdpi,xxxhdpi}/ic_launcher.png` | 48dp（48・72・96・144・192 画素） | 背景色の正方形に元の画像を縦横比を保って全体に収めたもの（従来型） |
| `mipmap-*/ic_launcher_foreground.png` | 108dp（108・162・216・324・432 画素） | 透明な正方形の中央の安全域 66dp（66・99・132・198・264 画素）に元の画像を収めたもの（どの形に切り抜かれても欠けない） |
| `mipmap-anydpi-v26/ic_launcher.xml` | — | アダプティブアイコン（`@color/ic_launcher_background` ＋ 前景）。minSdk 29 なので端末は常にこれを使う |
| `values/ic_launcher_background.xml` | — | 背景色 |

- 画像の縮小は **.NET 標準の範囲**で行う（`editor/src/Android/Icons/`: PNG の読み取り〈全部の色の種類・ビットの深さ・Adam7〉と書き出し、
  面積平均の縮小・双線形の拡大〈乗算済みアルファ〉を `System.IO.Compression.ZLibStream` だけで書いた）。WPF の `BitmapDecoder` はエディタ限定、
  `System.Drawing` は Windows 専用の NuGet、Rust の小さなツールは cargo のビルドとワークスペースの変更が要り、事前生成の要求は利用者の手間が大きいため。
  中核（SeedAndroid・エディタ）と単体テストが同じコードを使え、寸法・安全域をテストで固定できる。
- 同じ画像からは同じバイト列を書き、中身が同じファイルは書かない（更新時刻を変えず Gradle の差分のビルドを邪魔しない）。設定を消すと置き場ごと消す。
- 元の画像は 512x512 以上の正方形を勧める（小さい・正方形でないときは警告）。Google Play のストアの掲載用の 512x512 のアイコンは Play Console で別に登録する。
- 設定はプロジェクト設定 → 解像度設定 → 「Android アプリ情報（モバイル）」の「アイコン（PNG）」「アイコンの背景色」（保存のときにビルドと同じ検査）。
  パッケージ化ウィンドウの Android の「アイコン」の欄は今の値と誤りを出すだけ（設定の置き場への案内）。

### 24.8 Google Play の要件チェック（`editor/src/Android/Release/`）

| 項目 | ビルドの前（設定から） | ビルドの後（できた配布物から） |
|---|---|---|
| `target_sdk` | エンジンの値（`AndroidRuntimeContract.TargetApiLevel`）≥ 表の下限 | aapt2 で読んだ targetSdk |
| `version_code` | 前回の同じアプリ・形式の配布用ビルド（`<プロジェクト>/cache/android/release_history.json`）より大きいか。小さい＝不合格、同じ＝注意、記録なし＝知らせ | — |
| `abi_64bit` | 64 bit だけ・arm64-v8a を含む（x86_64 だけは注意、AAB に両方は BCL が両方配られるので注意） | 中の `lib/<ABI>/` |
| `application_id` | `com.example.` は不合格・エンジンの既定 ID は注意 | 中のアプリ ID・versionCode・versionName が指定どおりか（`artifact_identity`） |
| `signing` / `certificate` | 鍵が決まり keytool で開けたか | 署名を確かめ（APK: `apksigner verify --print-certs`・AAB: `keytool -printcert -jarfile`）、デバッグ用の鍵でない・指定の鍵の SHA-256 と同じ |
| `format` / `icon` | AAB か（APK は知らせ）・アイコンを設定したか（未設定は注意） | — |
| `debuggable` / `permissions` | — | debuggable でない（不合格）・INTERNET が無い（あれば注意） |
| `page_size_elf` / `page_size_zip` | — | すべての .so の LOAD の p_align ≥ 0x4000（zip の中の先頭だけを読む `ElfAlignmentReader`）・APK は `zipalign -c -P 16 4`（AAB は Google Play が整列する） |

- AAB のマニフェストは proto 形式なので、`base/manifest/AndroidManifest.xml`・`base/resources.pb`・`base/res/` を proto 形式の APK として並べ直し、
  `aapt2 convert --output-format binary` で変えてから `aapt2 dump badging` で読む（bundletool を使わない）。
- 道具で調べられなかったことは「調べられなかった」として不合格にする（確かめていないものを合格にしない）。
- 判定の表は `runtime/android/play_requirements.json`（毎年 8 月末に targetSdk の下限が上がる。上がったら表と `seedTargetSdk` を直す）。
  単体テストが、表の下限 ≤ エンジンの targetSdk、`build.gradle.kts` の値 = 中核の定数、を確かめる（ずれたらテストが落ちる）。
- SeedAndroid の `build`（配布用）と `check` は不合格があれば終了コード 6。パッケージ化ウィンドウは「Google Play の要件」の欄に色とアイコンで並べる。

### 24.9 Google Play Console への提出の流れ（初めてのアプリ）

1. 鍵を作る（§24.3）。キーストアとパスワードを控える。
2. プロジェクト設定の Android アプリ情報で、自分のアプリ ID（公開後は変えられない）・名前・バージョン番号・アイコンを決める。
3. `build --variant release --format aab`（パッケージ化ウィンドウなら ビルドの種類＝配布用・形式＝AAB）。要件の一覧に不合格が無いことを確かめる。
4. Play Console でアプリを作り、「内部テスト」のリリースに AAB をアップロードする。**Play App Signing** を使う（Google がアプリ署名鍵を持ち、
   今回の鍵はアップロード鍵として登録される。アップロード鍵を失ったら Play Console から再設定を頼める）。
5. ストアの掲載情報（512x512 のアイコン・スクリーンショット・説明）・コンテンツのレーティング・データ セーフティ・対象年齢などを埋める。
6. 内部テスト → クローズド / オープン テスト → 製品版へ進める。更新のたびにバージョン番号（versionCode）を上げる（要件チェックが前回と比べる）。

未確認: 実際の Play Console へのアップロード（アカウントが要る。backlog）。

### 24.10 bundletool で AAB を端末で試す（Google Play と同じ分け方で APKs を作る）

```powershell
# bundletool（jar）は https://github.com/google/bundletool/releases から。リポジトリには入れない
java -jar bundletool-all-1.18.3.jar build-apks --bundle app-release.aab --output app.apks `
     --ks D:\keys\mygame_upload.jks --ks-key-alias upload --ks-pass file:<パスワードのファイル> --connected-device --device-id <シリアル>
java -jar bundletool-all-1.18.3.jar install-apks --apks app.apks --device-id <シリアル>
adb -s <シリアル> shell am start -n <アプリ ID>/com.seedengine.runtime.MainActivity
```

- `--ks-pass file:` のファイルは作業が終わったら消す（パスワードをファイルに残さない）。
- `bundletool dump config --bundle app-release.aab` で `uncompressNativeLibraries`（`useLegacyPackaging` なら無効）とページの整列を見られる。

### 24.11 確認結果（2026-09-26）

検証用のプロジェクト（段階B の `proj_probe` の写し。BrainStem 1 体・平行光・確認用スクリプト）に、アプリ ID `com.seedengine.release_probe`・
名前 Release Probe・版 2 / 1.0.2・アイコン（512x512 の PNG）・背景色 `#1B2A3A` を設定し、一時のキーストア（`keystore create` で作成）で確かめた。
ログ・配布物は作業の一時フォルダ（リポジトリの外）に置いた。

| 確認 | 結果 |
|---|---|
| `keystore create` | PKCS12・RSA 2048・SHA384withRSA・10000 日（期限 2054-02-11）の鍵を作り、証明書の SHA-256 を表示。アセットフォルダの中を指定すると作らずに誤り。パスワードは環境変数から（コマンドラインに出ない） |
| 配布用の APK（`build --variant release`） | 全体 570 秒（libSEED.so の release ビルド 489.5 秒〈依存込みの初回〉・SeedPak 5.9 秒・同梱 .NET は変更なしで飛ばし・Gradle 73.1 秒〈lint vital を含む〉・要件の確認 0.8 秒）。`app-release.apk` 51.2 MB（.so 14 MB・同梱 .NET の BCL 24 MB・`bin/` 3 MB〈どれも圧縮後〉） |
| 配布用の AAB（`build --release --format aab`） | .so・pak・同梱 .NET は変更なしで飛ばし、Gradle（`bundleRelease`）9.7 秒・全体 12.0 秒。`app-release.aab` 56.5 MB |
| `apksigner verify --print-certs -v`（APK） | `Verifies`・v2 で署名・`Signer #1 certificate DN: CN=Release Probe`・SHA-256 が `keystore create` の表示と同じ（v1 は minSdk 29 のため無し） |
| `aapt2 dump badging`（APK） | `package: name='com.seedengine.release_probe' versionCode='2' versionName='1.0.2'`・`minSdkVersion:'29'`・`targetSdkVersion:'36'`・**`application-debuggable` の行なし**・**INTERNET なし**（権限は AndroidX の `…DYNAMIC_RECEIVER_NOT_EXPORTED_PERMISSION` だけ）・`application-label:'Release Probe'`・アイコンは全密度でアダプティブアイコンの XML・`native-code: 'arm64-v8a'` |
| `zipalign -c -P 16 -v 4`（APK） | `Verification successful`（.so 9 個はすべて `OK - compressed`） |
| APK の中の `libSEED.so` | 圧縮 10.3 MB／展開 24.9 MB（jniLibs の release の .so 34.7 MB から AGP が `.symtab` を削った。debug の .so はシンボルを削っても 43〜53 MB）。`llvm-readelf -l` の LOAD 4 つがすべて `Align 0x4000` |
| bundletool 1.18.3（AAB） | `validate` が通る。`dump config` は `uncompressNativeLibraries` が無効（`useLegacyPackaging`）・`alignment: PAGE_ALIGNMENT_16K`・`pak` は非圧縮。`build-apks`（アップロード鍵で署名）で分けた APK は master（targetSdk 36・`extractNativeLibs=true`・`enableOnBackInvokedCallback=false`）＋ ABI の分（.so 9 個は圧縮のまま）＋密度・言語。Pixel 6a 向けに落ちてくる大きさは master 37.3 MB＋arm64 14.9 MB＋密度 48 KB＋言語 8 KB |
| SeedAndroid の要件チェック | APK: 合格 11・知らせ 2（versionCode の記録なし・形式が APK）・注意 0・不合格 0。AAB: 合格 12・知らせ 1。配布物から読み直した中身（ID・版・minSdk 29・targetSdk 36）・debuggable・権限・16 KB（9 個の .so）・署名（指定の鍵の SHA-256 と一致）がすべて合格。AAB のマニフェストは aapt2 convert の経路で読めた |
| `check`（ビルドなし） | パスワードありで不合格 0・終了コード 0。パスワードなし（非対話）は「配布用の署名」が不合格・終了コード 6 |
| 失敗の経路 | パスワードなし・違うパスワード（`keystore password was incorrect`）・キーストアの指定なし → どれも準備で止まり（0.1 秒）終了コード 1、何もビルドしない。手で `gradlew assembleRelease`（署名の材料なし）→ `:app:preReleaseBuild FAILED` と §24.4 の説明 |
| 開発用の APK（`--skip-rust` で Gradle だけ） | `application-debuggable`・INTERNET あり・targetSdk 36・アイコンあり・`enableOnBackInvokedCallback=false`。アイコンの生成物は中身が同じなので書き直さなかった（書いた 0・同じ 12） |
| 配布用ビルドの記録 | `cache/android/release_history.json` に APK と AAB を別々に記録（versionCode 2・SHA-256） |
| 単体テスト | `AndroidPipelineTests` 137 件（新規 33: 署名 11・要件 13・アイコン 9）・`AndroidRunUiTests` 79 件（新規 5・道具の一覧の並びを更新）・`ProjectSystemTests` 63 件（新規 2）・`PackagingCollectorTests` 48 件。すべて成功 |
| ビルド | エディタ（別の出力先）エラー 0・新しいファイルの警告 0。SeedAndroid・`cargo build`（Windows。Rust は変更なし）成功 |
| アイコン | 生成した前景（xxxhdpi 432 画素）を背景色に重ね、円形の切り抜き（72dp の見える範囲）でも欠けないこと・従来型（192 画素）を画像で確かめた。APK の `aapt2 dump resources` に `mipmap/ic_launcher`（5 密度＋アダプティブ）・`mipmap/ic_launcher_foreground`・`color/ic_launcher_background #ff1b2a3a` |
| **実機（Pixel 6a）** | **未実施**。確認の 20 分間ずっと端末が利用中（前面が別のアプリ）で、前面がランチャーにならなかったため（私物の端末なので割り込まない）。bundletool の `install-apks` → 起動 → fps・起動の所要時間 → 戻るキー（targetSdk 36）→ 開発用との比較 → アンインストール、の手順を用意した（作業の一時フォルダの `device_release_test.sh`。backlog） |

### 24.12 NativeAOT の評価（実装はしない。2026-09-26）

**結論**: 配布用（release）だけを NativeAOT にする価値はある（同梱 .NET が APK の約 6 割を占め、それが 1〜3 MB の .so 1 つになる。初回起動の展開と
CLR の起動も無くなる）。ただし今のスクリプト基盤は「DLL をバイト列から読み込む・入口を hostfxr で取り出す・その場コンパイル（Roslyn）」が前提なので、
配布用のビルドの形と Rust 側の入口を足す必要がある。開発用（run・push・差し替え・デバッガ）は CoreCLR のまま（動的に読み直すため）。見積もり 2〜3 週間。

**小さな検証**（作業の一時フォルダ・実機では動かしていない）: この段階の配布用の APK の `bin/` にある `SEEDScripting.dll`・`SEEDUserScripts.dll`・
Roslyn を参照に、`linux-bionic-arm64` の共有ライブラリ（`PublishAot`・`NativeLib=Shared`・`DisableUnsupportedError=true`・
`PublishAotUsingRuntimePack=true`・NDK r28 の clang を PATH に）へ変換した（ILCompiler 10.0.12 のパック 184 MB は一時フォルダへ取得。Windows からの
クロスコンパイルは公式の手引き〈[android-bionic.md](https://github.com/dotnet/runtime/blob/main/src/coreclr/nativeaot/docs/android-bionic.md)〉どおり動いた。
`objcopy` が無いという注意が出るだけ）。

| 変換の形 | .so（展開／deflate） | trim・AOT の警告（重複を除く） | 所要時間 |
|---|---|---|---|
| A. 今のまま（`SEEDScripting` と `SEEDUserScripts` を丸ごと根にする＝Roslyn も入る） | 28.4 MB／11.6 MB | 48 件（SEED 32・Roslyn / DiaSymReader 16） | 約 2 分（ILC 1 スレッド） |
| B. Android で使う入口（`ScriptBridge` の 21 個。`CompileScripts`・パス指定の `LoadPrecompiledScripts` を除く）＋ユーザースクリプトだけを根にする | **2.5 MB／1.1 MB** | 27 件（すべて SEED） | 約 1 分 |

- 公開シンボル: `[UnmanagedCallersOnly(EntryPoint = "seed_aot_entry_points")]` の関数が `.dynsym` に出て、`ScriptBridge` の入口の関数ポインタの表を返せた
  （`&ScriptBridge.CreateComponent` 等）。LOAD は 0x4000 整列（16 KB）・NEEDED は libc / libm / libdl / liblog / libz だけ。
- 比べる相手（今の CoreCLR の同梱。§24.11 の APK）: 同梱 .NET の .so 4 MB＋BCL 24 MB＋`bin/` 3 MB（いずれも圧縮後）で APK の約 31 MB。展開後は BCL 65 MB＋.so 12 MB＋
  `bin/` 9 MB。B の形なら APK は約 30 MB（約 6 割）、端末の展開後は約 80 MB 小さくなる見込み。

**壊れる所・直す所**（警告と今のコードから）:

| 今の作り | NativeAOT での扱い | 必要な変更 |
|---|---|---|
| ユーザースクリプトを `AssemblyLoadContext`（collectible）へ `LoadFromStream` で読み、読み直しで Unload（`ScriptAssemblyManager`。IL2026） | 動的な読み込みは使えない（実行時に PlatformNotSupportedException） | 配布用のビルドで `SEEDUserScripts.dll` を同じ .so へ一緒に変換し、読み込みの代わりに「組み込み済みのアセンブリから型を登録する」入口を足す。`push`・`RELOAD_SCRIPTS` は配布用では使えない（もともと debuggable の開発用だけ） |
| Roslyn でのその場コンパイル（`ScriptAssemblyEmitter`・`ScriptSourceCompiler`。Android では元から使わない） | 丸ごと根にすると .so が 11 倍（A と B の差）・警告 16 件 | Android の入口から Roslyn に届かないように分ける（B の形。`CompileScripts` を別アセンブリか条件付きビルドへ） |
| 入口の取り出しは hostfxr の `get_function_pointer`（`UnmanagedCallersOnly` に名前なし。Rust の `clr_host/`） | hostfxr・CoreCLR が無い | 公開シンボル（`EntryPoint` 付き、または表を返す 1 関数）を足し、Rust 側に `dlopen` / `dlsym` の経路（`clr_host/` に AOT 用の実装）を足す。同梱 .NET の展開（`embedded_runtime/`）は配布用では不要になる |
| リフレクションで型・フィールド・プロパティ・メソッドを探す（`GetTypes`・`GetFields`・`Activator.CreateInstance`・`GetCustomAttributesData`。IL2070 / 2075 / 2067 / 2072 など 19 件） | 型の情報が残っていれば動く（ユーザーのアセンブリを丸ごと根にすれば残る） | `TrimmerRootAssembly`（ユーザー）と SEED 側の `DynamicallyAccessedMembers` の注記で警告を消す。動作は実機で全 API を確かめ直す |
| `MakeGenericType(List<>)`・`MakeGenericMethod`（`ScriptArray`・`ScriptStructArray`・`ScriptReference`。IL3050 4 件） | 事前に作られていない値型の組み合わせは実行時に失敗し得る | 値型の配列・構造体の配列を `Array.CreateInstance` 等に置き換えるか、使う型の組み合わせを生成コードで事前に作る |
| 暗号 API（今は Android の JNI 版の暗号ライブラリ。§17.8） | linux-bionic の NativeAOT は OpenSSL を使う（手引きに「アプリが用意する」とある） | OpenSSL を同梱するか、スクリプトの暗号 API を使わない決まりにする |
| ヒープポインタのタグ付け（§17.5） | 未確認（CoreCLR / Mono と同じ問題が出るかは実機で確かめる） | マニフェストの属性と `mallopt` はそのまま残す |

**効果の見込み**: APK 約 30 MB 減・展開後約 80 MB 減（上の表）。起動は、初回の BCL の展開（実機 0.5〜0.8 秒）・CLR の起動（61〜175 ms）・JIT
（ユーザースクリプトの読み込み 10〜24 ms とその後の最初の呼び出し）が無くなる（数百 ms〜1 秒弱。実機では未計測）。R2R の DLL をアプリのデータフォルダから
実行する方式（SELinux の auditallow の対象。§17.4）も無くなるので、将来の Android での禁止への備えにもなる。

**工数の見積もり**（合計 2〜3 週間）: Roslyn の分離と組み込み済みアセンブリの登録（1〜2 日）、公開シンボルと Rust の `dlopen` の経路（2〜3 日）、
SeedPak / SeedAndroid に ILC の工程（ABI ごと・パックの取得・NDK のリンク・指紋）（2〜3 日）、リフレクションと総称の修正・注記と全 API の実機確認（3〜5 日）、
暗号の方針（1 日〜）。backlog に載せた。

### 24.13 制限・持ち越し（[backlog.md](backlog.md) の「Android」節）

- Google Play Console への実際の提出（内部テストへのアップロード）は未確認（アカウントが要る）。
- 予測型の「戻る」の無効化（`enableOnBackInvokedCallback="false"`）は公式にも一時的な回避。将来の Android で効かなくなる前に `OnBackInvokedCallback` で
  戻るをネイティブへ渡す形に移す。
- Gradle のデーモンが配布用のビルドの後も次のビルドまで署名のパスワードを環境に持つ（§24.5）。
- ネイティブのデバッグシンボル（`ndk.debugSymbolLevel`）を AAB に入れていないので、Play Console のクラッシュのスタックに関数名が出ない。
- アセットフォルダの中にアイコンの PNG を置くと、`project_settings.json` から参照されているとみなされ pak にも入る（数十 KB）。気になるならアセットの外に置いて `../` で指定する。
- AAB に x86_64 も入れると同梱 .NET の BCL（assets）が ABI で分けられず全端末に両方配られる（要件チェックが注意を出す）。
- `useLegacyPackaging`（.so を圧縮）はインストール後の大きさが増える。やめるには同梱 .NET の置き方を変えるか NativeAOT（§24.12）。
- アイコンのモノクロ層（Android 13 のテーマアイコン）・前景の余白の設定は無い（前景は安全域 66dp に収めるだけ）。
- bundletool は SeedAndroid に組み込んでいない（AAB を端末で試すのは §24.10 の手作業）。`build_and_run.ps1`（run の互換ラッパー）に配布用の引数は足していない。
- パッケージ化ウィンドウの配布用の欄（署名・要件の一覧・キーストアの作成）はエディタを起動して目で確かめていない（WPF 非依存の判断は単体テスト）。
- versionCode の記録はプロジェクトの `cache/`（PC ごと）。チームで作るなら Play Console の最後の versionCode を正とする。

