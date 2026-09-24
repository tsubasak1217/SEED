# Android 対応（正典）

SEED のランタイム（Rust の `runtime/`）を Android 端末で動かすための、構成・手順・現状・ロードマップの正典。
段階0（2026-09-24）と、段階A のうち複数指タッチの入力基盤（§12）・APK 内 pak からの起動（§13）・保存先の振り替え／セーブの保護／起動基盤（§14）・画面の向きと安全領域（§15）までの内容。未着手・保留の課題は [backlog.md](backlog.md) の「Android」節に集約する。

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
    src/app_dirs.rs           アプリ専用フォルダ（files・cache）をセーブ・キャッシュの書き込み先としてエンジンへ設定（§14.1）
    src/jni_exports.rs        Java から呼ばれるネイティブ関数（onDestroy 前のセーブ書き出し。§14.2／安全領域と回転の報告。§15）
    src/launch.rs             起動モード（APK 内 pak／開発用の置き場）の判定 → エンジンの起動引数（LaunchArgs。§13）
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
  build_and_run.ps1          ビルド → install → 起動 → logcat の一括スクリプト（pwsh）
  settings.gradle.kts        リポジトリ（google / mavenCentral）と :app
  build.gradle.kts           AGP 9.1.0
  gradle.properties          AndroidX 等（マシン固有パスは書かない）
  gradlew / gradlew.bat / gradle/wrapper/   Gradle 9.3.1 の wrapper
  app/build.gradle.kts       minSdk 29 / targetSdk 35 / abiFilters arm64-v8a, x86_64 / 依存
  app/src/main/AndroidManifest.xml
  app/src/main/java/com/seedengine/runtime/MainActivity.java   GameActivity 派生（薄い）
  app/src/main/java/com/seedengine/runtime/ScreenReporter.java 安全領域と画面の回転を集めてネイティブへ渡す（§15）
  app/src/main/res/values/{strings,themes}.xml
  app/src/main/jniLibs/<ABI>/libSEED.so    ← cargo ndk の出力（生成物・追跡しない）
  app/src/main/assets/seed/assets.pak      ← SeedPak の出力（-ProjectDir のときだけ。生成物・追跡しない。§13）
  native/                    §4.1 の cdylib クレート
```

- pak は `androidResources { noCompress += "pak" }` で非圧縮（STORED）のまま APK に入れる（§13.2）。

- `applicationId` は仮に `com.seedengine.runtime`。段階C でプロジェクト設定からデータドリブンに生成する。
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
MainActivity（Java）: static { System.loadLibrary("SEED") }
  onCreate の最初（super.onCreate の前）: 環境変数 TMPDIR＝cache・HOME＝files（Os.setenv。§14.1）
  └ GameActivity.onCreate … android.app.lib_name=SEED を読み、GameActivity_onCreate（Rust 側 glue）へ
      └ android-activity が専用スレッドで android_main(app) を呼ぶ（runtime/android/native/src/entry.rs）
          1. logcat::init()            … android_logger・panic フック・標準出力/標準エラーの付け替え
          2. device_info::log          … SDK・機種・ABI・データパス
          2b. app_dirs::init           … セーブ（files/save）・キャッシュ（cache）の書き込み先をエンジンへ設定（§14.1）
          3. launch::launch_args       … APK に seed/assets.pak があればパッケージ実行（配布物の読み口 package_source 付き。§13）、
                                         無ければ <アプリ専用フォルダ>/assets をアセットルートにした LaunchArgs（mode=Play。§4.5）
          4. EventLoop::builder().with_android_app(app).build()
          5. heartbeat::spawn()        … 3 秒ごとの提示フレーム数ログ
          6. App::run_with_event_loop(event_loop, args)   … 以降はデスクトップと同じエンジン
               resumed（1 回目）   → handle_resumed（ウィンドウ・GPU・シーンの初期化。デスクトップと同じ）
               suspended           → enter_background（セーブ・パイプラインキャッシュの書き出し → 物理停止。§14）
                                     → handle_suspended（サーフェス破棄・イベントループを Wait へ）
               resumed（2 回目以降）→ handle_surface_resumed（サーフェス再生成・サイズ依存状態の更新・Poll へ）
                                     → enter_foreground（物理再開・背面にいた時間を捨てる。§14.4）
  onCreate の最後（super.onCreate の後）: ScreenReporter.attach … 以降、WindowInsets・レイアウト・構成・表示の変化のたびに
      安全領域と回転を JNI（nativeOnScreenChanged）で報告し、エンジンがフレームごとに SEED.Screen の値へ反映する（§15）
  MainActivity.onDestroy → nativeFlushSaveData（JNI。セーブの未書き出し分）→ Process.killProcess（§14.2）
```

### 4.4 プラットフォーム差の扱い（エンジン側）

OS ごとの「振る舞いの差」は cfg を散らさず、`runtime/src/engine/platform/mod.rs` の
**特性表 `PlatformTraits`** に集約した（データドリブン）。`platform::CURRENT` が現在のビルドの値。

| フラグ | デスクトップ | Android | 効く場所 |
|---|---|---|---|
| `app_sizes_window` | true | false | ウィンドウ生成に project_settings の `window_width/height` を使うか（Android は端末の画面＝サーフェス実サイズ） |
| `scripting_supported` | true | false | スクリプトホスト（CLR）を探すか（Android は段階B まで無し） |
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
- `app/background_lifecycle.rs` … 背面・前面への出入りでのセーブとパイプラインキャッシュの書き出し、
  物理スレッドの停止・再開（`core/background_gate.rs`）、ゲーム時間の取り戻し防止（§14）。
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
| 1 | 内部アプリ専用フォルダ `/data/user/0/com.seedengine.runtime/files` | `build_and_run.ps1 -AssetsDir`。デバッグ版 APK の `run-as` でアプリの権限になり、tar を流し込む（§5.2）。**実機でもエミュレータでも読める** |
| 2 | 外部アプリ専用フォルダ `/sdcard/Android/data/com.seedengine.runtime/files` | 手で `adb push`。エミュレータでは読めるが、**実機（Android 11 以降）では adb push が作ったフォルダが shell の所有になりアプリから読めない**（Permission denied。起動時に警告を出す） |

APK 内の pak（パッケージ実行）は §13。パッケージ実行でもアセットルートはこの内部フォルダの `assets/`
（PAK にも APK にも無いアセットのフォールバック先。作らない）。セーブ・キャッシュの置き場は §14.1。

---

## 5. ビルドと実行

### 5.1 一括スクリプト（推奨）

`runtime/android/build_and_run.ps1`（**pwsh 7.4 以降**で実行。アセット転送の tar をネイティブコマンド間のパイプで
バイト列のまま渡すため。Windows PowerShell 5.1 は対象外）。

```powershell
# 環境変数（例。ANDROID_NDK_HOME が無ければ SDK 内 ndk/ の最新版を警告付きで使う）
$env:ANDROID_SDK_ROOT = "$env:LOCALAPPDATA\Android\Sdk"
$env:ANDROID_NDK_HOME = "$env:LOCALAPPDATA\Android\Sdk\ndk\28.2.13676358"
$env:JAVA_HOME        = "C:\Program Files\Android\Android Studio\jbr"

# 両 ABI をビルドして、つながっている 1 台で起動し logcat を流す（Ctrl+C で終了）
pwsh -File runtime/android/build_and_run.ps1

# エミュレータ向けだけ・アセットを送る・20 秒ぶんの logcat をファイルへ
pwsh -File runtime/android/build_and_run.ps1 -Abi x86_64 -Serial emulator-5554 `
     -AssetsDir D:\path\to\Project\assets -LogcatSeconds 20 -LogFile logcat.txt

# 実機（arm64）。ビルド済みなら APK は作り直さず、アセットだけ差し替えて再起動する
pwsh -File runtime/android/build_and_run.ps1 -Abi arm64-v8a -Serial <実機のシリアル> `
     -SkipRustBuild -SkipGradle -NoInstall -AssetsDir D:\path\to\Project\assets

# 配布版と同じ形（APK 内 pak）。SeedPak で pak を作って APK に入れ、push 無しで起動する（§13）
pwsh -File runtime/android/build_and_run.ps1 -Abi x86_64 -Serial emulator-5554 `
     -ProjectDir D:\path\to\Project -LogcatSeconds 20
```

| 引数 | 意味 |
|---|---|
| `-Abi arm64-v8a,x86_64` | ビルドする ABI（既定は両方）。APK にもこの ABI だけを詰める（Gradle へ `-Pseed.abis` で渡す） |
| `-Release` | Rust 側を `--release` でビルド（APK はデバッグ署名のまま） |
| `-Serial <adb のシリアル>` | 対象端末。**2 台以上つながっているときは必須** |
| `-AssetsDir <assets フォルダ>` | `project_settings.json` を含むフォルダを §4.5 の内部アプリ専用フォルダの `assets/` へ送る（前回分は消して置き直す）。開発用の高速経路。APK を作るときは、このフォルダ（`-ProjectDir` ならそのアセットルート）の `screen_orientation` で画面の向きを決める（どちらも無ければ `both`。§15.1） |
| `-ProjectDir <プロジェクトフォルダ>` | SeedPak（`editor/tools/SeedPak`）で pak を作り `app/src/main/assets/seed/` に置いてから APK を作る（パッケージ実行・push 無し。§13）。`.seedproj`／`assets/` を持つフォルダか、アセットルートそのもの。`-AssetsDir`・`-SkipGradle` とは同時に指定できない。**指定しないで Gradle を回すと置き場を空にする**（pak の無い開発用の APK になる） |
| `-SkipRustBuild` / `-SkipGradle` / `-NoInstall` / `-NoLaunch` / `-NoLogcat` | 工程を飛ばす |
| `-LogcatSeconds <秒>` / `-LogFile <パス>` | logcat を何秒集めるか（0 = Ctrl+C まで）／保存先 |

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

---

## 6. logcat の見方

エンジン・糊・MainActivity の出力はすべて **タグ `SEED`** に集まる。

```powershell
adb logcat -s SEED RustPanic            # 普段はこれで十分
adb logcat -v threadtime SEED:V RustPanic:V GameActivity:V AndroidRuntime:E DEBUG:V libc:F *:S   # 起動失敗・クラッシュ時
adb logcat -d -v threadtime -T "09-24 17:00:00.000" SEED:V *:S   # その時刻以降だけ（共用端末で logcat -c しない）
```

実機は他の作業者・エージェントと共用することがあるため、**`adb logcat -c`（全消去）は使わない**。
`build_and_run.ps1` も起動直前の端末の時刻を控えて `logcat -T` で今回分だけを取り出す。

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
| `[SEED KEY FRAME] f=N Escape:down+up` | 置き換えたキー（戻るキー → Escape）の入力状態（`app/key_diag.rs`。§14.5） | スクリプトの `GetKeyDown` / `GetKeyUp` が読むフレーム末の値 |
| `[SEED SCREEN] Java 報告: …` / `報告を受け取りました: …` | 安全領域・回転の報告（`ScreenReporter.java`・`jni_exports.rs`。§15.4） | 描画面の大きさ・各辺からの距離・回転・表示の自然な向きの大きさ。Java の行には WindowInsets の種類ごとの内訳も出る |
| `[SEED SCREEN] size=… safe=(x,y,幅,高さ) orientation=… dpi=… window=… report=…` | スクリプトの `SEED.Screen` が返す値（`app/screen_diag.rs`。§15.4） | 変化したフレームだけ。`report=none` は今の描画面に一致する報告が無いフレーム（回転の直後） |
| `[SEED SAVE TEST] …` | 検証用のセーブ書き換え（`debug.seed.save_test` が 1 か 2 のときだけ。§14.6） | 起動時に読んだ値と書き換えた値 |
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
| 音声（oboe） | 未確認 | 初期化まで確認（`OboeAudio: OboeVersion1.8.1`、`AAudioStreamBuilder_openStream() returns 0 = AAUDIO_OK`。音は出していない） |
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

- **スクリプト（C#）は動かない**（段階B）。
- アセットは APK 内の pak（パッケージ実行。リリース版でも動く）か、デバッグ版 APK の run-as で内部アプリ専用フォルダへ
  送ったもの（開発用）を読む（§13）。
- セーブ・キャッシュは起動モードに関係なくアプリ専用フォルダ（`files/save/`・`cache/`）に書く（段階A-3 で振り替え。§14.1）。
- タッチは入力システムへつながった（§12）。ただしスクリプト（`Input.GetTouch` 等）は段階B まで Android で動かないため、
  実機で効くのは「指0 → マウス」経由のもの（キャンバス UI のポインタイベントの判定・入力状態）だけ。
  ポインタイベントの配信先もスクリプトなので、実機でボタンが反応するところまでは段階B で確認する。
- **Activity の破棄＝プロセス終了**。winit 0.30 は onDestroy をアプリへ通知しない（イベントループが終わらず
  GameActivity の onDestroy が android_main の終了を待ち続けて ANR になる）うえ、EventLoop はプロセスで 1 度しか
  作れないため、`MainActivity.onDestroy` でプロセスを終了させている。構成変更での作り直しは `configChanges` で防いでいる。
  セーブはバックグラウンドへ回る時点（suspended）と、終了直前の JNI 呼び出しで書き出す（段階A-3。§14.2）。
- 戻るキーは `KeyCode.Escape` としてスクリプトへ届く。アプリは自動で終了しない（段階A-3。§14.5）。
- 画面の向きはプロジェクト設定の `screen_orientation`（both / portrait / landscape）で APK を作るときに決まる。端末の回転ロックは尊重しない。
  安全領域・向き・DPI は `SEED.Screen` で読めるが、キャンバス UI へ安全領域を自動では反映しない（段階A-4。§15）。
- バックグラウンド中は物理スレッドを止める（段階A-3。§14.4）。音声・ゲームパッド（gilrs）のスレッドは止めていない。
- パイプラインキャッシュはアプリのキャッシュフォルダへ保存し、2 回目以降の起動で読む（段階A-3。§14.3）。
  実機での短縮幅は未計測（エミュレータはホスト側ドライバのキャッシュが効くため差が小さい。§14.7）。
- ゲームパッド（gilrs）は Android 非対応（初期化に失敗して「パッド無効」で続行）。
- 音声（rodio → cpal → oboe）は実機で出力ストリームを開くところまで確認。実際に音が鳴るかは未確認。
- **実機の描画は重い**。Pixel 6a の debug ビルドで縦 約 18〜19 fps（GPU 待ちが支配的と見られる）。デスクトップ向けの描画経路
  （deferred・MRT 5 枚・シャドウ 2048・SSGI 等）を端末の実解像度 1080x2400 でそのまま回しているため。
  モバイル向け描画プリセット（描画解像度スケール・重い後処理の既定オフ）は段階D。

---

## 9. 段階ロードマップ

| 段階 | 内容 |
|---|---|
| **0（完了）** | 実機/エミュレータに 1 枚絵。libSEED.so ＋ Gradle ＋ GameActivity、logcat、サーフェスの破棄・再生成、回転追従 |
| **A** | スクリプト無しでシーンを動かす: APK 内 pak（AssetManager。**2026-09-24 実装・§13**）、保存先の振替・セーブの保護・パイプラインキャッシュ・背面での物理停止・戻るキー（**2026-09-24 実装・§14**）、縦横とサーフェス再生成の仕上げ、複数指タッチ（`Input.TouchCount` / `GetTouch(i)`。PC はマウス＝指 0。**2026-09-24 実装・§12**）、安全領域・画面の向き API（プロジェクト設定の向き・`SEED.Screen`。**2026-09-24 実装・§15**）、音声、logcat の整備 |
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
  入れ直した .so を読ませるには `am force-stop` してから起動する（`build_and_run.ps1` は毎回そうしている）。
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
**実機（Pixel 6a・arm64）は未完**: arm64 の APK は作成・インストールでき、起動中に届いた実際の指のタッチ（戻るジェスチャのなぞりと、
システムに取り消された 2 回のタッチ。いずれも ID 0）が新しい経路で受信された（`[SEED TOUCH] Started / Ended / Cancelled`、panic なし）。
ただし端末が私物として使用中で、APK 更新直後の初回起動が約 44 秒かかる間にユーザーが別アプリへ移り、最初のフレームの提示前に
バックグラウンドでプロセスが終了したため、`[SEED TOUCH FRAME]`（フレーム単位の状態）と合成タッチ列による複数指の確認はできていない。
この受信の並び（同じ ID の再タッチが 1 フレームに 3 回）は `state.rs` の回帰テストに写した。

| 確認項目 | 結果 |
|---|---|
| `input tap 540 1200` | 生の Started / Ended が同じフレームに届き、`[SEED TOUCH FRAME]` はそのフレームが `#0:Began(540.0,1200.0)` ＋ `L=PD-`、次のフレームが `#0:Ended` ＋ `L=--U`（タップが 2 フレームに分かれる） |
| `input swipe 300 1600 800 1000 800` | `Began` → 約 45 フレームの `Moved`（1 フレーム約 10〜20 px の移動量）→ `Ended(800.0,1000.0)` ＋ `L=--U`。マウス座標が毎フレーム指に追従 |
| `input mouse tap 700 900`（入力元がマウス） | `Touch` として届き、指と同じく `Began` → `Ended`（§12.3 の「Android のマウスは押している間だけの指」を確認） |
| OS 経由の複数指（エミュレータのコンソール `adb emu event send` で protocol B の 3 スロット） | `n=1 → 2 → 3` と同時に追跡。1 本目は `Stationary` のまま 2 本目だけ `Moved d(0.0,100.0)`、3 本目が `Began` → `Moved`。1 本目が離れたフレームで `L=--U`、その後 2 本目を動かしてもマウスは 1 本目の最後の位置のまま（`L=---`） |
| 合成タッチ列（`debug.seed.touch_test=1`） | 上と同じ並びが `[SEED TOUCH TEST]` の合成イベントから再現（3 本同時・指0 だけがマウスを駆動）。検証後にプロパティは元（未設定）へ戻した |
| 座標 | `input tap X Y` の X, Y がそのまま位置（ウィンドウが画面全体・原点一致） |
| PC（`SEED.exe` 単体の Play ＋ 確認用スクリプト） | 起動時 `TouchSupported=False` / `TouchCount=0` / `GetTouch(5)` は `Touch.None`。左ボタン押下で `#0 Began`、押したまま移動で `Moved delta=(50,20)`、静止で `Stationary`、離して `Ended` → 次フレーム `n=0`。押下と解放を同じメッセージ列で送った素早いクリックも `Began` → 次フレーム `Ended`（マウス自体は従来どおり同じフレームに押下・解放）。同じフレームに `GetTouch(0)` を 2 回呼んでも同じ値・`Touches.Length == TouchCount` |

- 非 root の端末では `sendevent` が SELinux で拒否される（エミュレータの shell でも `/dev/input/event2: Permission denied`。shell は
  input グループに入っているが書けない）。エミュレータはコンソールの `event send` で `ABS_MT_*` を送ると virtio のマルチタッチ装置
  （0〜32767 の範囲を画面へ写す）へ届く。実機は合成タッチ列（`debug.seed.touch_test`）で確かめる。

### 12.6 制限・持ち越し

詳細と持ち越し先は [backlog.md](backlog.md) の「Android」節。

- 実機（Pixel 6a）での `[SEED TOUCH FRAME]` と複数指の確認が未完（§12.5）。
- Android ではスクリプトが動かない（段階B）ため、`Input.GetTouch` の値とキャンバス UI のボタン反応（配信先がスクリプト）は Android では未確認。
  入力状態（指の一覧・タッチ由来のマウス）までは `[SEED TOUCH FRAME]` で確認した（エミュレータ）。
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
- APK に pak が入っていると、run-as で送ったアセットは「PAK に無いもの」しか使われない。`build_and_run.ps1` は Gradle を
  回すたびに置き場を作り直す（`-ProjectDir` があれば SeedPak の出力、無ければ空＝開発用の APK。§13.5）。

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
# pak を作って APK に入れ、push 無しで起動する（-ProjectDir は .seedproj／assets/ を持つフォルダか、アセットルートそのもの）
pwsh -File runtime/android/build_and_run.ps1 -Abi x86_64 -Serial emulator-5554 -ProjectDir D:\path\to\Project -LogcatSeconds 20

# pak だけを作る（エディタは起動しない。パッケージ化ウィンドウと同じ収録規則・パス書き換え・PAK 形式）
dotnet run --project editor/tools/SeedPak -- --project D:\path\to\Project --out <出力フォルダ>
```

| 経路 | 使う場面 | 変更の反映 | リリース版 APK |
|---|---|---|---|
| `-ProjectDir`（APK 内 pak） | 配布版と同じ形での確認・リリース版 | pak と APK を作り直して入れ直す（SeedPak → Gradle → install で約 30 秒〜） | 動く |
| `-AssetsDir`（run-as 転送） | 開発中の素早い差し替え | `-SkipRustBuild -SkipGradle -NoInstall -AssetsDir …` でアセットだけ送り直し（1 秒未満） | 動かない（run-as はデバッグ版だけ） |

- `-ProjectDir` と `-AssetsDir` は同時に指定できない（端末は pak を優先するため）。`-ProjectDir` は `-SkipGradle` とも併用できない。
- `-SkipGradle -AssetsDir` のとき、前回のビルドで APK に pak を入れていれば警告する（そのままの APK ならパッケージ実行が優先される）。
- SeedPak の詳細（引数・アセットルートの決め方・収録ルール・終了コード）は [packaging.md](packaging.md) §10。
- `-LogFile` に保存される logcat は、日本語が文字化けすることがある（pwsh が adb の UTF-8 出力をコンソールのコードページで
  読むため。以前からの挙動。backlog）。確実に残すなら `adb logcat -d -v threadtime -T "<時刻>" SEED:V *:S > file` を
  bash 等から直接実行する。

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

実機（Pixel 6a）は、作業中ずっと端末が私物として使用中（別アプリが前面）だったため未実施。端末が空いているときに
`-Abi arm64-v8a -Serial <実機> -ProjectDir <プロジェクト>` で同じ確認をする（backlog）。

### 13.7 制限・持ち越し

詳細と持ち越し先は [backlog.md](backlog.md) の「Android」節。

- ~~パッケージ実行ではセーブ・キャッシュを書けない~~ → 段階A-3 でアプリ専用フォルダへ振り替えた（§14.1）。
- モデルの派生キャッシュは PAK 実行では効かない（Windows の配布物と同じ。[packaging.md](packaging.md) §8）。パイプラインキャッシュは
  段階A-3 から保存される（§14.3）。
- 段階B（スクリプト）: `App::new` は「アセットルートがあればソースをコンパイル、無ければ事前コンパイル DLL」で分けるが、
  Android のパッケージ実行もアセットルートを持つ。段階B では `package_source` の有無でも分け、DLL を配布物の `bin/` から
  （`PackageSource` で）読む必要がある。
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
- `build_and_run.ps1` は起動のたびに `am force-stop` するので、ホームへ戻さずに作業を繰り返すと保存されない（backlog）。

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
- 音声（rodio → oboe）とゲームパッド（gilrs）のスレッドは止めていない（backlog）。

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

実機（Pixel 6a）は、作業中は端末が私物として使用中（別アプリが前面）で、その後 USB の接続も外れたため未実施。

### 14.8 制限・持ち越し

詳細と持ち越し先は [backlog.md](backlog.md) の「Android」節。

- 実機（Pixel 6a）での確認が未実施。特にパイプラインキャッシュの短縮幅（段階0 の実測ではパイプライン生成 約 3.4 秒）。
- `build_and_run.ps1` は毎回 force-stop してから起動するので、開発中にホームへ戻さないとパイプラインキャッシュが保存されない
  （最初のフレームの後にも 1 回保存する案）。
- 背面中も音声・ゲームパッド（gilrs）のスレッドは動く。`[PLAY_WD]` の監視ログが背面中に誤報を出す（既存の一時診断）。
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
      └ build_and_run.ps1 の Resolve-ScreenOrientation（-ProjectDir / -AssetsDir のアセットルートから読む。どちらも無ければ both）
          └ gradlew assembleDebug -Pseed.orientation=<値>
              └ app/build.gradle.kts の変換表 → manifestPlaceholders["seedScreenOrientation"]
                  └ AndroidManifest.xml の android:screenOrientation="${seedScreenOrientation}"
```

| 設定値 | マニフェスト（値） | 振る舞い |
|---|---|---|
| `both`（既定） | `fullSensor`（10） | 縦横 4 方向に追従（端末の回転ロックは無視してセンサーに従う。従来どおり） |
| `portrait` | `sensorPortrait`（7） | 縦だけ。逆さの縦へ回るかは端末の設定次第（エミュレータでは 0 度のままだった） |
| `landscape` | `sensorLandscape`（6） | 横だけ。左右どちら向きの横にもセンサーに従って回る |

- **変換表は `app/build.gradle.kts` の 1 か所だけ**。`build_and_run.ps1` は値を読んで渡すだけ、エディタは値と表示名だけを持つ。
  表に無い値は Gradle が警告（`SEED: screen_orientation="…" は不明な値です…`）を出して `both` として扱う。前後の空白・大文字小文字は吸収する。
- 起動時に読む値ではなく **APK（マニフェスト）に焼き込む**。値を変えたら Gradle を回して APK を作り直す（`-SkipGradle` では前回の APK のまま）。
  段階C のエディタ統合も `build_and_run.ps1` へ `-ProjectDir` / `-AssetsDir` を渡せば同じ判定になる。
- `-ProjectDir` のアセットルートは SeedPak（`PakInputResolver`）と同じ規則で決める（`.seedproj` の `assets_dir` → `<フォルダ>/assets` → フォルダ自体）。
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

実機（Pixel 6a）は未実施（作業の開始時は別のアプリが前面で使用中、その後 USB の接続が外れた）。

### 15.6 制限・持ち越し

詳細と持ち越し先は [backlog.md](backlog.md) の「Android」節。

- Android ではスクリプトが動かない（段階B）ため、`SEED.Screen` の C# 側は PC でだけ確かめた。Android では同じ値をログで確かめた。
- 安全領域をキャンバス UI へ自動で反映する仕組み（セーフエリアのパディング・アンカー）は無い。スクリプトが `SafeArea` を読んで配置する。
- 回転の直後の 1 フレーム程度は、安全領域が全画面・向きが縦横比からの値になることがある。
- 端末の回転ロックは尊重しない（`fullSensor` / `sensorPortrait` / `sensorLandscape`。`fullUser` 等の選択肢は無い）。
- 実機（Pixel 6a の実際のカメラ穴）・分割画面（描画面が表示の一部になる）・自然な向きが横のタブレットは未確認。
- `Screen.DPI` は OS の論理 DPI（Android は密度の区分値 densityDpi）で、物理的な DPI（xdpi / ydpi）ではない。
