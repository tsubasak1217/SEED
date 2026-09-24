#requires -Version 7.4
# ============================================================
#  build_and_run.ps1 — SEED ランタイムを Android 向けにビルドし、端末で起動する（段階0 / 段階A）
#
#  【流れ】
#    1. cargo ndk で libSEED.so をビルドし app/src/main/jniLibs/<ABI>/ へ置く
#    2. APK に入れる配布物（app/src/main/assets/seed/）を決める
#         -ProjectDir あり … SeedPak（editor/tools/SeedPak）で assets.pak を作って置く（パッケージ実行の APK）
#         -ProjectDir なし … 置き場を空にする（pak の無い開発用の APK。アセットは 5 の run-as 転送で送る）
#    3. gradlew assembleDebug で APK を作る（画面の向きはプロジェクト設定の screen_orientation を
#       -Pseed.orientation で渡し、app/build.gradle.kts の変換表がマニフェストへ差し込む。
#       -ProjectDir / -AssetsDir の設定を読み、どちらも無ければ既定値 both）
#    4. adb install -r で端末（実機／エミュレータ）へ入れる
#    5. （任意）アセットフォルダをアプリの内部専用フォルダへ送る（run-as で tar を流し込む。開発用の高速経路）
#    6. am start で起動し、logcat（タグ SEED ほか）を表示・保存する
#  端末側は「APK に assets/seed/assets.pak があればそれで起動、無ければ内部フォルダの assets/」で決まる
#  （runtime/android/native/src/launch.rs）。段階C（エディタの「実行」統合）はこのスクリプトの各関数を土台にする想定。
#
#  【前提】pwsh（PowerShell 7.4 以降）で実行する。7.4 未満はネイティブコマンド間のパイプが
#  バイト列を壊す（アセット転送の tar が壊れる）。Windows PowerShell 5.1 は日本語を含む
#  スクリプトの解釈も不安定なため対象外（先頭の #requires で弾く）。
#
#  【マシン固有のパス】すべて環境変数から取る（リポジトリには書かない）。
#    ANDROID_SDK_ROOT（無ければ ANDROID_HOME） … Android SDK
#    ANDROID_NDK_HOME（無ければ SDK 内 ndk/ の最新版を警告付きで使う） … Android NDK（r28 以降）
#    JAVA_HOME                                  … JDK（Android Studio 同梱の JBR を推奨）
#
#  【使用例】
#    pwsh -File runtime/android/build_and_run.ps1                       # 両 ABI をビルドして接続端末で起動
#    pwsh -File runtime/android/build_and_run.ps1 -Abi x86_64 -Serial emulator-5554 -LogcatSeconds 20 -LogFile out.log
#    pwsh -File runtime/android/build_and_run.ps1 -AssetsDir D:\path\to\project\assets
#    pwsh -File runtime/android/build_and_run.ps1 -Abi x86_64 -Serial emulator-5554 -ProjectDir D:\path\to\project
#
#  詳細は docs/android.md。
# ============================================================

[CmdletBinding()]
param(
    # Rust 側を --release でビルドする（APK はデバッグ署名のまま）。
    [switch]$Release,

    # ビルドする ABI。実機は arm64-v8a、PC のエミュレータは x86_64。
    [ValidateSet('arm64-v8a', 'x86_64')]
    [string[]]$Abi = @('arm64-v8a', 'x86_64'),

    # 対象端末のシリアル（adb devices の左列）。端末が 2 台以上つながっているときは必須。
    [string]$Serial,

    # 端末へ push するアセットフォルダ（プロジェクトの assets/。project_settings.json を含む）。
    # 開発用の高速経路（APK を作り直さずアセットだけ差し替えられる）。-ProjectDir とは同時に使わない。
    [string]$AssetsDir,

    # APK に同梱する pak を作るプロジェクトフォルダ（SeedPak の --project。.seedproj か assets/ を持つフォルダ、
    # またはアセットルートそのもの）。指定すると push 無しで APK だけで起動する（パッケージ実行）。
    [string]$ProjectDir,

    # 各工程を飛ばす（途中から繰り返すとき用）。
    [switch]$SkipRustBuild,
    [switch]$SkipGradle,
    [switch]$NoInstall,
    [switch]$NoLaunch,
    [switch]$NoLogcat,

    # logcat を何秒集めて終えるか。0 なら Ctrl+C まで流し続ける。
    [int]$LogcatSeconds = 0,

    # logcat の保存先（省略時は保存しない）。
    [string]$LogFile
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# ── 定数（他ファイルの値と一致させるもの）───────────────────────────────

# アプリの ID（app/build.gradle.kts の applicationId と同じ）。
$PackageName = 'com.seedengine.runtime'
# 起動する Activity（AndroidManifest.xml の MainActivity）。
$LaunchActivity = "$PackageName/.MainActivity"
# cargo ndk がリンクする Android API レベル（app/build.gradle.kts の seedMinSdk と同じ）。
$AndroidApiLevel = 29
# logcat -T に渡す「この時刻以降」の書式（端末の date コマンドの書式。logcat の -v threadtime と同じ並び）。
$LogcatSinceFormat = '+%m-%d %H:%M:%S.000'
# アセットの送り先（run-as の作業フォルダ＝アプリの内部データフォルダ /data/user/0/<パッケージ名> からの相対。
# runtime/android/native/src/launch.rs が最優先で見る置き場）。
$RemoteAssetsDir = 'files/assets'
# アセットを tar にまとめる Windows 標準の tar（Git 等の別の tar を拾わないよう場所を固定する）。
$HostTar = Join-Path $env:SystemRoot 'System32/tar.exe'
# logcat で表示するタグ（SEED = エンジン・グルー・MainActivity。RustPanic = android-activity が
# 受け止めた panic。ほかは Java 例外・ネイティブクラッシュ・Activity の起動終了の手掛かり）。
$LogcatFilters = @('SEED:V', 'RustPanic:V', 'GameActivity:V', 'AndroidRuntime:E', 'DEBUG:V', 'libc:F', 'vulkan:W', 'ActivityTaskManager:I', '*:S')

# APK の assets/ の中で配布物のルートにするフォルダ名（native/src/apk_package/mod.rs の APK_PACKAGE_ROOT と同じ）。
$ApkPackageRootName = 'seed'
# 配布物の PAK のファイル名（エンジンの package_layout::PAK_FILE_NAME・エディタの PackageLayout.PakFileName と同じ）。
$PakFileName = 'assets.pak'

# プロジェクト設定のファイル名（アセットルートの目印。SeedPak の PakInputResolver と同じ）。
$ProjectSettingsFileName = 'project_settings.json'
# プロジェクトファイルの拡張子・アセットフォルダのキーと既定値（エディタの SeedProjectFile と同じ）。
$SeedProjectExtension = '.seedproj'
$SeedProjectAssetsDirKey = 'assets_dir'
$DefaultAssetsDirName = 'assets'
# 画面の向きのキーと既定値（エディタの ProjectSettingsData.ScreenOrientation と同じ）。
# 値 → マニフェストの screenOrientation の変換表は app/build.gradle.kts だけに置く（ここは値を読んで渡すだけ）。
$ScreenOrientationKey = 'screen_orientation'
$DefaultScreenOrientation = 'both'

# このスクリプトのあるフォルダ（runtime/android）を基準にする。
$AndroidRoot = $PSScriptRoot
$RepoRoot = Split-Path -Parent (Split-Path -Parent $AndroidRoot)
$NativeCrateDir = Join-Path $AndroidRoot 'native'
$JniLibsDir = Join-Path $AndroidRoot 'app/src/main/jniLibs'
# APK に同梱する配布物の置き場（Gradle の既定の assets ソース app/src/main/assets の下。生成物・追跡しない）。
$ApkPackageDir = Join-Path $AndroidRoot "app/src/main/assets/$ApkPackageRootName"
# pak を作るコンソールツール（パッケージ化ウィンドウと同じ収録規則・パス書き換え・PAK 形式）。
$SeedPakProject = Join-Path $RepoRoot 'editor/tools/SeedPak'
$ApkPath = Join-Path $AndroidRoot 'app/build/outputs/apk/debug/app-debug.apk'

# ── ツールチェーンの解決 ─────────────────────────────────────────────

# Android SDK のフォルダを返す（ANDROID_SDK_ROOT → ANDROID_HOME の順）。
function Resolve-AndroidSdk {
    $sdk = if ($env:ANDROID_SDK_ROOT) { $env:ANDROID_SDK_ROOT } else { $env:ANDROID_HOME }
    if (-not $sdk -or -not (Test-Path -LiteralPath $sdk)) {
        throw 'Android SDK が見つかりません。環境変数 ANDROID_SDK_ROOT（または ANDROID_HOME）に SDK のフォルダを設定してください（例: %LOCALAPPDATA%\Android\Sdk）。'
    }
    return (Resolve-Path -LiteralPath $sdk).Path
}

# Android NDK のフォルダを返す（ANDROID_NDK_HOME → SDK 内 ndk/ の最新版）。
function Resolve-AndroidNdk([string]$Sdk) {
    if ($env:ANDROID_NDK_HOME) {
        if (-not (Test-Path -LiteralPath (Join-Path $env:ANDROID_NDK_HOME 'source.properties'))) {
            throw "ANDROID_NDK_HOME=$($env:ANDROID_NDK_HOME) は NDK のフォルダではありません（source.properties がありません）。"
        }
        return (Resolve-Path -LiteralPath $env:ANDROID_NDK_HOME).Path
    }
    $ndkParent = Join-Path $Sdk 'ndk'
    $candidates = @(Get-ChildItem -LiteralPath $ndkParent -Directory -ErrorAction SilentlyContinue |
        Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName 'source.properties') } |
        Sort-Object { try { [version]$_.Name } catch { [version]'0.0' } } -Descending)
    if ($candidates.Count -eq 0) {
        throw 'Android NDK が見つかりません。環境変数 ANDROID_NDK_HOME に NDK（r28 以降）のフォルダを設定するか、Android Studio の SDK Manager で NDK を入れてください。'
    }
    $chosen = $candidates[0].FullName
    Write-Warning "ANDROID_NDK_HOME が未設定のため SDK 内の最新 NDK を使います: $chosen"
    return $chosen
}

# JAVA_HOME が JDK を指しているか確かめる（gradlew が使う）。
function Assert-JavaHome {
    if (-not $env:JAVA_HOME -or -not (Test-Path -LiteralPath (Join-Path $env:JAVA_HOME 'bin/java.exe'))) {
        throw 'JAVA_HOME が未設定か JDK を指していません。Android Studio 同梱の JBR（例: C:\Program Files\Android\Android Studio\jbr）を JAVA_HOME に設定してください。'
    }
}

# cargo と cargo-ndk が使えるか確かめる。
function Assert-CargoNdk {
    if (-not (Get-Command cargo -ErrorAction SilentlyContinue)) {
        throw 'cargo が見つかりません。Rust（rustup）を入れてください。'
    }
    & cargo ndk --version *> $null
    if ($LASTEXITCODE -ne 0) {
        throw 'cargo-ndk が見つかりません。`cargo install cargo-ndk` と `rustup target add aarch64-linux-android x86_64-linux-android` を実行してください。'
    }
}

# adb の引数（-s シリアル）を組み立てる。端末が複数あるのにシリアル未指定ならエラーにする。
function Get-AdbTargetArgs([string]$Adb) {
    if ($Serial) { return @('-s', $Serial) }
    $devices = @(& $Adb devices | Select-Object -Skip 1 | Where-Object { $_ -match '\S+\s+device$' })
    if ($devices.Count -eq 0) {
        throw 'adb で端末が見つかりません（実機の USB デバッグ、またはエミュレータの起動を確認してください）。'
    }
    if ($devices.Count -gt 1) {
        throw "端末が $($devices.Count) 台つながっています。-Serial で対象を指定してください:`n$($devices -join "`n")"
    }
    return @()
}

# ── プロジェクト設定（画面の向き）─────────────────────────────────────

# プロジェクトフォルダからアセットルートを決める（SeedPak の PakInputResolver.ResolveFromProject と同じ規則。
# 規則を変えるときは両方を直す）。決められなければ $null。
#   1. .seedproj があれば、その assets_dir（複数あればフォルダ名と同じ名前のもの、無ければ名前順の先頭）
#   2. 無ければ <フォルダ>/assets
#   3. それも無く、フォルダ自体に project_settings.json があればそのフォルダ
function Resolve-ProjectAssetsRoot([string]$Dir) {
    $root = (Resolve-Path -LiteralPath $Dir).Path
    $projects = @(Get-ChildItem -LiteralPath $root -File -Filter "*$SeedProjectExtension" -ErrorAction SilentlyContinue |
        Sort-Object Name)
    if ($projects.Count -gt 0) {
        $folderName = Split-Path -Leaf $root
        # Select-Object -First 1 は見つからなければ $null（StrictMode では空配列の [0] が例外になるため添字で取らない）。
        $chosen = $projects | Where-Object { $_.BaseName -ieq $folderName } | Select-Object -First 1
        if (-not $chosen) { $chosen = $projects[0] }
        $assetsDirName = $DefaultAssetsDirName
        try {
            $project = Get-Content -LiteralPath $chosen.FullName -Raw -Encoding utf8 | ConvertFrom-Json
            $property = $project.PSObject.Properties[$SeedProjectAssetsDirKey]
            if ($property -and $property.Value -is [string] -and $property.Value.Trim()) { $assetsDirName = $property.Value }
        }
        catch {
            Write-Warning "$($chosen.FullName) を JSON として読めません（assets_dir は既定の $DefaultAssetsDirName とみなします）: $_"
        }
        # assets_dir が絶対パスでもそのまま使えるよう Combine で結合する（エディタの ProjectPaths と同じ）。
        return [System.IO.Path]::GetFullPath([System.IO.Path]::Combine($root, $assetsDirName))
    }
    $defaultAssets = Join-Path $root $DefaultAssetsDirName
    if (Test-Path -LiteralPath $defaultAssets -PathType Container) { return $defaultAssets }
    if (Test-Path -LiteralPath (Join-Path $root $ProjectSettingsFileName)) { return $root }
    return $null
}

# アセットルートの project_settings.json から画面の向き（screen_orientation の値）を読む。
# ファイル・キーが無い、値が文字列でない・空なら既定値。前後の空白を落として小文字にそろえる。
# 値の妥当性の確認とマニフェストの値への変換は app/build.gradle.kts の変換表が行う（表を 1 か所に保つため。
# 表に無い値は Gradle が警告を出して既定値へ倒す）。
function Get-ScreenOrientationSetting([string]$AssetsRoot) {
    if (-not $AssetsRoot) { return $DefaultScreenOrientation }
    $settingsPath = Join-Path $AssetsRoot $ProjectSettingsFileName
    if (-not (Test-Path -LiteralPath $settingsPath)) { return $DefaultScreenOrientation }
    try {
        $settings = Get-Content -LiteralPath $settingsPath -Raw -Encoding utf8 | ConvertFrom-Json
    }
    catch {
        Write-Warning "$settingsPath を JSON として読めません（画面の向きは既定の $DefaultScreenOrientation にします）: $_"
        return $DefaultScreenOrientation
    }
    $property = $settings.PSObject.Properties[$ScreenOrientationKey]
    if (-not $property -or -not ($property.Value -is [string]) -or -not $property.Value.Trim()) {
        return $DefaultScreenOrientation
    }
    return $property.Value.Trim().ToLowerInvariant()
}

# このビルド（APK）の画面の向きを決める。APK を作るときに 1 回呼ぶ。
# 段階C のエディタ統合も、このスクリプトへ -ProjectDir / -AssetsDir を渡せば同じ判定になる。
#   -ProjectDir … そのプロジェクトのアセットルートの設定（SeedPak と同じ導き方）
#   -AssetsDir  … そのアセットフォルダの設定
#   どちらも無い … 既定値（both）
function Resolve-ScreenOrientation([string]$FromProjectDir, [string]$FromAssetsDir) {
    $assetsRoot = if ($FromProjectDir) { Resolve-ProjectAssetsRoot $FromProjectDir } elseif ($FromAssetsDir) { $FromAssetsDir } else { $null }
    $value = Get-ScreenOrientationSetting $assetsRoot
    $source = if ($assetsRoot) { Join-Path $assetsRoot $ProjectSettingsFileName } else { '既定値（-ProjectDir / -AssetsDir なし）' }
    Write-Host "      画面の向き（$ScreenOrientationKey）: $value  ← $source"
    return $value
}

# ── 各工程 ─────────────────────────────────────────────────────────

# 1. cargo ndk で libSEED.so をビルドして jniLibs へ置く。
function Invoke-NativeBuild([string]$Ndk) {
    $env:ANDROID_NDK_HOME = $Ndk
    $cargoArgs = @('ndk')
    foreach ($target in $Abi) { $cargoArgs += @('-t', $target) }
    $cargoArgs += @('-P', "$AndroidApiLevel", '-o', $JniLibsDir, 'build')
    if ($Release) { $cargoArgs += '--release' }

    Write-Host "[1/6] cargo $($cargoArgs -join ' ')" -ForegroundColor Cyan
    Push-Location -LiteralPath $NativeCrateDir
    try {
        & cargo @cargoArgs
        if ($LASTEXITCODE -ne 0) { throw "cargo ndk が失敗しました（終了コード $LASTEXITCODE）。" }
    }
    finally {
        Pop-Location
    }
    Get-ChildItem -LiteralPath $JniLibsDir -Recurse -Filter 'libSEED.so' |
        ForEach-Object { Write-Host ("      {0}  {1:N1} MB  {2}" -f $_.Directory.Name, ($_.Length / 1MB), $_.LastWriteTime) }
}

# 2. APK に同梱する配布物（app/src/main/assets/seed/）を決める。
#
# -ProjectDir があれば SeedPak で assets.pak を作って置き（パッケージ実行の APK）、無ければ置き場を空にする
# （pak の無い開発用の APK）。置き場に前回の pak が残ったままだと、端末はそれで起動して run-as で送った
# アセットを読まなくなるため、Gradle を回すたびに今回の引数どおりの状態へ作り直す。
function Update-ApkPackage {
    if (Test-Path -LiteralPath $ApkPackageDir) {
        Remove-Item -LiteralPath $ApkPackageDir -Recurse -Force
    }
    if (-not $ProjectDir) {
        Write-Host "[2/6] APK に pak を入れません（開発用。アセットは -AssetsDir の run-as 転送で送る）" -ForegroundColor Cyan
        return
    }
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
        throw 'dotnet が見つかりません。SeedPak（editor/tools/SeedPak）の実行に .NET SDK が必要です。'
    }
    Write-Host "[2/6] SeedPak: $ProjectDir -> $ApkPackageDir/$PakFileName" -ForegroundColor Cyan
    & dotnet run --project $SeedPakProject -- --project $ProjectDir --out $ApkPackageDir
    if ($LASTEXITCODE -ne 0) { throw "SeedPak が失敗しました（終了コード $LASTEXITCODE）。" }
    $pak = Join-Path $ApkPackageDir $PakFileName
    if (-not (Test-Path -LiteralPath $pak)) { throw "SeedPak の出力に $PakFileName がありません: $pak" }
    Write-Host ("      {0}  {1:N1} MB" -f $pak, ((Get-Item -LiteralPath $pak).Length / 1MB))
}

# 3. gradlew assembleDebug で APK を作る。
#
# 画面の向き（$Orientation = screen_orientation の値）は -Pseed.orientation で渡し、
# app/build.gradle.kts の変換表がマニフェストの screenOrientation へ差し込む（APK に焼き込まれる）。
function Invoke-GradleBuild([string]$Ndk, [string]$Orientation) {
    Write-Host '[3/6] gradlew assembleDebug' -ForegroundColor Cyan
    Push-Location -LiteralPath $AndroidRoot
    try {
        # -Abi で選んだ ABI だけを APK に詰める（jniLibs に残っている別 ABI の古い .so を詰めないため）。
        & (Join-Path $AndroidRoot 'gradlew.bat') assembleDebug "-Pseed.ndkPath=$Ndk" "-Pseed.abis=$($Abi -join ',')" "-Pseed.orientation=$Orientation" --console=plain
        if ($LASTEXITCODE -ne 0) { throw "gradlew assembleDebug が失敗しました（終了コード $LASTEXITCODE）。" }
    }
    finally {
        Pop-Location
    }
    if (-not (Test-Path -LiteralPath $ApkPath)) { throw "APK が見つかりません: $ApkPath" }
    Write-Host ("      {0}  {1:N1} MB" -f $ApkPath, ((Get-Item -LiteralPath $ApkPath).Length / 1MB))
}

# 4. APK を端末へ入れる（既存のデータは残す）。
function Install-Apk([string]$Adb, [string[]]$TargetArgs) {
    Write-Host "[4/6] adb install -r $ApkPath" -ForegroundColor Cyan
    & $Adb @TargetArgs install -r $ApkPath
    if ($LASTEXITCODE -ne 0) { throw "adb install が失敗しました（終了コード $LASTEXITCODE）。" }
}

# 5. アセットフォルダをアプリの内部専用フォルダへ送る（-AssetsDir 指定時のみ）。
#
# 【なぜ adb push ではないのか】実機（Android 11 以降）では、adb push で外部アプリ専用フォルダ
# （/sdcard/Android/data/<パッケージ名>/files）に作ったフォルダは shell の所有になり、アプリからは
# Permission denied で読めない。そこでデバッグ版 APK だけが使える run-as でアプリの権限になり、
# ホストで作った tar のストリームを内部データフォルダへ展開する（前回分は消してから置き直す）。
# 展開後は他のユーザーから読めないよう権限を絞る（Windows の tar は全員書き込み可で記録するため）。
# なお adb exec-in は 2 つ目以降の引数を 1 つずつ引用して端末へ渡すので、スクリプトは引用せずに渡す。
function Push-Assets([string]$Adb, [string[]]$TargetArgs) {
    if (-not $AssetsDir) { return }
    if (-not (Test-Path -LiteralPath (Join-Path $AssetsDir 'project_settings.json'))) {
        throw "-AssetsDir にはプロジェクトの assets/（project_settings.json を含むフォルダ）を指定してください: $AssetsDir"
    }
    if (-not (Test-Path -LiteralPath $HostTar)) { throw "tar が見つかりません: $HostTar" }
    Write-Host "[5/6] $AssetsDir -> (run-as $PackageName) $RemoteAssetsDir" -ForegroundColor Cyan
    $extract = "rm -rf $RemoteAssetsDir && mkdir -p $RemoteAssetsDir && tar -xf - -C $RemoteAssetsDir && chmod -R u+rwX,go-rwx $RemoteAssetsDir"
    & $HostTar -cf - -C $AssetsDir . | & $Adb @TargetArgs exec-in run-as $PackageName sh -c $extract
    if ($LASTEXITCODE -ne 0) { throw "アセットの転送が失敗しました（終了コード $LASTEXITCODE）。デバッグ版 APK がインストール済みか確認してください。" }
    # 置けたかを確かめる（exec-in は端末側の失敗を終了コードで返さないことがある）。
    $check = & $Adb @TargetArgs exec-out run-as $PackageName sh -c "test -f $RemoteAssetsDir/project_settings.json && echo ok"
    if ("$check".Trim() -ne 'ok') { throw "アセットの転送後に $RemoteAssetsDir/project_settings.json が見つかりません。" }
}

# 6. 起動して logcat を表示（・保存）する。
function Start-AppAndWatchLog([string]$Adb, [string[]]$TargetArgs) {
    # 今回の起動分だけを見るため、起動直前の「端末の時刻」を控えて、それ以降のログだけを出す（logcat -T）。
    # logcat -c（ログの全消去）は使わない。実機は他の作業者・エージェントと共用することがあり、
    # 消すと他の人の調査ログまで失われるため。
    # （adb shell は引数をそのまま連結して端末のシェルへ渡すので、書式は内側で引用する）
    $since = "$(& $Adb @TargetArgs shell "date '$LogcatSinceFormat'")".Trim()
    if (-not $NoLaunch) {
        # 動いていれば止めてから起動し直す。Activity は singleTask なので、動いたままだと am start は
        # 前面へ出すだけで、送り直したアセットや入れ直した .so を読まない。止めるのは自分のパッケージだけ。
        & $Adb @TargetArgs shell am force-stop $PackageName
        Write-Host "[6/6] am start $LaunchActivity" -ForegroundColor Cyan
        & $Adb @TargetArgs shell am start -W -n $LaunchActivity
        if ($LASTEXITCODE -ne 0) { throw "am start が失敗しました（終了コード $LASTEXITCODE）。" }
    }
    if ($NoLogcat) { return }

    # adb logcat は引数を 1 つずつ引用して端末へ渡すため、空白を含む時刻もそのまま渡せる。
    $logcatArgs = @('logcat', '-v', 'threadtime', '-T', $since)
    if ($LogcatSeconds -gt 0) {
        # 決めた秒数だけ待ってから、それまでのログをまとめて取り出す（-d = 取り出して終了）。
        Start-Sleep -Seconds $LogcatSeconds
        $lines = & $Adb @TargetArgs @logcatArgs -d @LogcatFilters
        $lines | ForEach-Object { Write-Host $_ }
        if ($LogFile) { $lines | Set-Content -LiteralPath $LogFile -Encoding utf8 }
    }
    else {
        Write-Host 'logcat を表示します（Ctrl+C で終了）' -ForegroundColor DarkGray
        if ($LogFile) { & $Adb @TargetArgs @logcatArgs @LogcatFilters | Tee-Object -FilePath $LogFile }
        else { & $Adb @TargetArgs @logcatArgs @LogcatFilters }
    }
}

# ── 本体 ──────────────────────────────────────────────────────────

# 引数の食い違いは何もビルドしないうちに弾く。
if ($ProjectDir -and $AssetsDir) {
    throw '-ProjectDir（APK 内の pak で起動）と -AssetsDir（run-as で送ったアセットで起動）は同時に指定できません。端末は APK に pak があればそちらを優先します。'
}
if ($ProjectDir -and $SkipGradle) {
    throw '-ProjectDir は APK を作り直すときだけ効きます（-SkipGradle と同時に指定できません）。'
}
if ($ProjectDir -and -not (Test-Path -LiteralPath $ProjectDir -PathType Container)) {
    throw "-ProjectDir のフォルダが見つかりません: $ProjectDir"
}
if ($AssetsDir -and $SkipGradle -and (Test-Path -LiteralPath (Join-Path $ApkPackageDir $PakFileName))) {
    Write-Warning "前回のビルドで APK に $PakFileName を入れています。そのままの APK ならパッケージ実行が優先され、送ったアセットは PAK に無いものしか使われません（-SkipGradle を外すと pak 無しで作り直します）。"
}

$sdk = Resolve-AndroidSdk
$ndk = Resolve-AndroidNdk $sdk
$adb = Join-Path $sdk 'platform-tools/adb.exe'
if (-not (Test-Path -LiteralPath $adb)) { throw "adb が見つかりません: $adb（SDK Manager で Platform-Tools を入れてください）" }

if (-not $SkipRustBuild) { Assert-CargoNdk; Invoke-NativeBuild $ndk }
if (-not $SkipGradle) {
    Update-ApkPackage
    Assert-JavaHome
    # 画面の向きは APK（マニフェスト）に焼き込まれる。-SkipGradle で APK を作り直さないときは前回の値のまま。
    $orientation = Resolve-ScreenOrientation -FromProjectDir $ProjectDir -FromAssetsDir $AssetsDir
    Invoke-GradleBuild $ndk $orientation
}

$needsDevice = (-not $NoInstall) -or $AssetsDir -or (-not $NoLaunch) -or (-not $NoLogcat)
if ($needsDevice) {
    # 関数の戻り値の空配列は $null に潰れるため @() で配列に戻す。
    $targetArgs = @(Get-AdbTargetArgs $adb)
    if (-not $NoInstall) { Install-Apk $adb $targetArgs }
    Push-Assets $adb $targetArgs
    Start-AppAndWatchLog $adb $targetArgs
}
