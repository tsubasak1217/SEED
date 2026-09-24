#requires -Version 7.4
# ============================================================
#  build_and_run.ps1 — SEED ランタイムを Android 向けにビルドし、端末で起動する（段階0 / 段階A）
#
#  【流れ】
#    1. cargo ndk で libSEED.so をビルドし app/src/main/jniLibs/<ABI>/ へ置く
#    2. APK に入れる配布物（app/src/main/assets/seed/）を決める
#         -ProjectDir あり … SeedPak（editor/tools/SeedPak）で assets.pak と bin/（スクリプトの事前コンパイル DLL と
#                            スクリプトホスト。--scripts）を作って置く（パッケージ実行の APK）
#         -ProjectDir なし … 置き場を空にする（pak の無い開発用の APK。アセットは 6 の run-as 転送で送る）
#    3. 同梱 .NET（段階B）を組み立てる: runtime/android/dotnet_runtime.json の版・パック（coreclr / mono）を NuGet から
#       取り寄せ（~/.nuget/packages にキャッシュ）、ABI ごとに .so を app/src/seedDotnet/jniLibs/<ABI>/ へ、
#       BCL・deps.json・目録 bundle.json を app/src/seedDotnet/assets/seed/dotnet/<ABI>/ へ置く（docs/android.md §17）
#    4. gradlew assembleDebug で APK を作る（画面の向きはプロジェクト設定の screen_orientation を
#       -Pseed.orientation で渡し、app/build.gradle.kts の変換表がマニフェストへ差し込む。
#       -ProjectDir / -AssetsDir の設定を読み、どちらも無ければ既定値 both）
#    5. adb install -r で端末（実機／エミュレータ）へ入れる
#    6. （任意）開発用の高速経路
#         -AssetsDir   … アセットフォルダをアプリの内部専用フォルダへ送る（run-as で tar を流し込む）
#         -PushScripts … スクリプトの DLL だけを作り直して端末の files/bin/ へ送る（APK は作り直さない）
#    7. am start で起動し、logcat（タグ SEED・DOTNET ほか）を表示・保存する
#  端末側は「APK に assets/seed/assets.pak があればそれで起動、無ければ内部フォルダの assets/」で決まる
#  （runtime/android/native/src/launch.rs）。スクリプトの DLL は「files/bin/ → APK の bin/」の順に探す
#  （native/src/dotnet_runtime/script_sources.rs）。段階C（エディタの「実行」統合）はこのスクリプトの各関数を土台にする想定。
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
#    pwsh -File runtime/android/build_and_run.ps1 -Abi x86_64 -Serial emulator-5554 -SkipRustBuild -SkipGradle -NoInstall `
#         -ProjectDir D:\path\to\project -PushScripts        # スクリプトの DLL だけを差し替えて再起動
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
    # スクリプト（bin/）も同じプロジェクトから作って APK に入れる。-PushScripts のときはスクリプトの出どころにもなる。
    [string]$ProjectDir,

    # スクリプトの DLL だけを作り直して端末へ送り、アプリを再起動する（開発用の高速経路。APK を作り直さない）。
    # -ProjectDir（無ければ -AssetsDir）の .cs を SeedPak --scripts-only で事前コンパイルし、bin/ の DLL と runtimeconfig を
    # 端末の files/bin/ へ送る。端末は files/bin/ を APK の bin/ より優先して読む（消せば APK の中のものへ戻る）。
    [switch]$PushScripts,

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
# 受け止めた panic。DOTNET = 同梱 CoreCLR の Console 出力（C# スクリプトの SEED.Debug.Log。Mono は標準出力なので SEED）。
# ほかは Java 例外・ネイティブクラッシュ・Activity の起動終了の手掛かり）。
$LogcatFilters = @('SEED:V', 'DOTNET:V', 'RustPanic:V', 'GameActivity:V', 'AndroidRuntime:E', 'DEBUG:V', 'libc:F', 'vulkan:W', 'ActivityTaskManager:I', '*:S')

# APK の assets/ の中で配布物のルートにするフォルダ名（native/src/apk_package/mod.rs の APK_PACKAGE_ROOT と同じ）。
$ApkPackageRootName = 'seed'
# 配布物の PAK のファイル名（エンジンの package_layout::PAK_FILE_NAME・エディタの PackageLayout.PakFileName と同じ）。
$PakFileName = 'assets.pak'
# 配布物の副次ファイル（スクリプトの DLL）のフォルダ名（package_layout::BIN_DIR_NAME・PackageLayout.BinDirName と同じ）。
$BinDirName = 'bin'
# 端末へ送るスクリプトの DLL（SeedPak --scripts-only の bin/ の中身のうち、端末が読むもの。
# core::scripting::script_binaries が読む SEEDScripting.dll・SEEDUserScripts.dll・SEEDScripting.runtimeconfig.json を含む）。
$ScriptBinaryPatterns = @('*.dll', '*.runtimeconfig.json')

# ── 同梱 .NET（段階B。docs/android.md §17）─────────────────────────────

# 同梱 .NET の設定（版・パック名・coreclr / mono の切り替え。唯一の置き場）。
$DotnetSettingsPath = Join-Path $PSScriptRoot 'dotnet_runtime.json'
# 組み立てた同梱 .NET の置き場（app/build.gradle.kts の sourceSets が jniLibs・assets として APK へ入れる。生成物・追跡しない）。
$DotnetStagingDir = Join-Path $PSScriptRoot 'app/src/seedDotnet'
# APK の assets/seed/ の中の同梱 .NET のフォルダ名（エンジンの embedded_runtime::BUNDLE_DIR_NAME と同じ）。
$DotnetBundleDirName = 'dotnet'
# 同梱 .NET の目録のファイル名（embedded_runtime::MANIFEST_FILE_NAME と同じ）。
$DotnetBundleManifestName = 'bundle.json'
# 目録の書式の版（embedded_runtime::manifest::SUPPORTED_FORMAT_VERSION と同じ。書式を変えたら両方上げる）。
$DotnetBundleFormatVersion = 1
# 中身の識別子（content_id）の桁数（16 進）。端末の展開先のフォルダ名に入る。
$DotnetContentIdLength = 16
# hostfxr のファイル名（目録の hostfxr。エンジンが最初に読み込む .so）。
$HostfxrFileName = 'libhostfxr.so'
# 共有フレームワークの deps.json / runtimeconfig のファイル名（パックの lib/<TFM>/ にある）。
$FrameworkDepsFileSuffix = '.deps.json'
$FrameworkRuntimeConfigFileSuffix = '.runtimeconfig.json'
# NuGet のパックを取り寄せるだけの一時プロジェクトの置き場（生成物）。
$DotnetRestoreDir = Join-Path $PSScriptRoot 'app/build/seed/dotnet_restore'
# NuGet が展開を終えたパックに置く印（これがあれば取り寄せ済み）。
$NuGetCompletionMarker = '.nupkg.metadata'
# 同梱 .NET の deps.json を書き直すときの JSON の入れ子の上限（ConvertTo-Json の既定 2 では足りない）。
$JsonWriteDepth = 32
# -PushScripts で作る bin/ の置き場（SeedPak --scripts-only の出力先。生成物）。
$PushScriptsStagingDir = Join-Path $PSScriptRoot 'app/build/seed/push_scripts'
# -PushScripts の送り先（run-as の作業フォルダ＝内部データフォルダからの相対。native/src/dotnet_runtime/script_sources.rs の
# 最優先の候補）。外部アプリ専用フォルダ（/sdcard/Android/data/<pkg>/files/bin）へ adb push したものは、実機（Android 16 の
# Pixel 6a）ではアプリから読めない（Permission denied）ことを確かめたので使わない（docs/android.md §17.7）。
$RemoteScriptsDir = "files/$BinDirName"

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

    Write-Host "[1/7] cargo $($cargoArgs -join ' ')" -ForegroundColor Cyan
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

# SeedPak（editor/tools/SeedPak）を呼ぶ。引数はそのまま渡す（dotnet run がツールと scripting/ をビルドしてから走らせる）。
function Invoke-SeedPak([string[]]$SeedPakArgs) {
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
        throw 'dotnet が見つかりません。SeedPak（editor/tools/SeedPak）の実行に .NET SDK が必要です。'
    }
    & dotnet run --project $SeedPakProject -- @SeedPakArgs
    if ($LASTEXITCODE -ne 0) { throw "SeedPak が失敗しました（終了コード $LASTEXITCODE。4 はスクリプトのコンパイル失敗）。" }
}

# 2. APK に同梱する配布物（app/src/main/assets/seed/）を決める。
#
# -ProjectDir があれば SeedPak で assets.pak と bin/（スクリプトの事前コンパイル DLL とスクリプトホスト）を作って置き
# （パッケージ実行の APK）、無ければ置き場を空にする（pak の無い開発用の APK）。置き場に前回の pak が残ったままだと、
# 端末はそれで起動して run-as で送ったアセットを読まなくなるため、Gradle を回すたびに今回の引数どおりの状態へ作り直す。
function Update-ApkPackage {
    if (Test-Path -LiteralPath $ApkPackageDir) {
        Remove-Item -LiteralPath $ApkPackageDir -Recurse -Force
    }
    if (-not $ProjectDir) {
        Write-Host "[2/7] APK に pak とスクリプトを入れません（開発用。アセットは -AssetsDir、スクリプトは -PushScripts で送る）" -ForegroundColor Cyan
        return
    }
    Write-Host "[2/7] SeedPak --scripts: $ProjectDir -> $ApkPackageDir（$PakFileName と $BinDirName/）" -ForegroundColor Cyan
    Invoke-SeedPak @('--project', $ProjectDir, '--out', $ApkPackageDir, '--scripts')
    $pak = Join-Path $ApkPackageDir $PakFileName
    if (-not (Test-Path -LiteralPath $pak)) { throw "SeedPak の出力に $PakFileName がありません: $pak" }
    Write-Host ("      {0}  {1:N1} MB" -f $pak, ((Get-Item -LiteralPath $pak).Length / 1MB))
    $bin = Join-Path $ApkPackageDir $BinDirName
    $binFiles = @(Get-ChildItem -LiteralPath $bin -File -ErrorAction SilentlyContinue)
    Write-Host ("      {0}  {1} ファイル・{2:N1} MB" -f $bin, $binFiles.Count, (($binFiles | Measure-Object Length -Sum).Sum / 1MB))
}

# ── 3. 同梱 .NET（段階B）の組み立て ─────────────────────────────────────

# 同梱 .NET の設定（dotnet_runtime.json）を読む。dotnet_runtime の値が runtimes に無ければエラー。
# 戻り値: Settings（JSON の中身）・Text（ファイルの文字列。content_id の材料）・Kind（coreclr / mono）・Runtime（その種類の設定）
function Get-DotnetRuntimeSettings {
    if (-not (Test-Path -LiteralPath $DotnetSettingsPath)) { throw "同梱 .NET の設定がありません: $DotnetSettingsPath" }
    $text = Get-Content -LiteralPath $DotnetSettingsPath -Raw -Encoding utf8
    $settings = $text | ConvertFrom-Json
    $kind = "$($settings.dotnet_runtime)".Trim().ToLowerInvariant()
    $runtime = $settings.runtimes.PSObject.Properties[$kind]
    if (-not $runtime) {
        $choices = @($settings.runtimes.PSObject.Properties.Name) -join ' / '
        throw "dotnet_runtime.json の dotnet_runtime=""$($settings.dotnet_runtime)"" は runtimes にありません（使える値: $choices）。"
    }
    return [pscustomobject]@{ Settings = $settings; Text = $text; Kind = $kind; Runtime = $runtime.Value }
}

# ひな形（{arch} {version} {framework}）を埋める。
function Expand-DotnetTemplate([string]$Template, [hashtable]$Values) {
    $result = $Template
    foreach ($key in $Values.Keys) { $result = $result.Replace("{$key}", [string]$Values[$key]) }
    return $result
}

# その ABI の置き換え値（パック名・RID・フォルダのひな形に使う）。
function Get-DotnetTemplateValues($Dotnet, [string]$AbiName) {
    $arch = $Dotnet.Settings.abis.PSObject.Properties[$AbiName]
    if (-not $arch) { throw "dotnet_runtime.json の abis に $AbiName がありません。" }
    return @{ arch = $arch.Value; version = $Dotnet.Settings.version; framework = $Dotnet.Settings.framework }
}

# NuGet のグローバルパッケージフォルダ（既定 ~/.nuget/packages。環境変数 NUGET_PACKAGES があればそちら）。
function Get-NuGetPackagesRoot {
    if ($env:NUGET_PACKAGES) { return $env:NUGET_PACKAGES }
    # 出力は「global-packages: C:\Users\...\.nuget\packages\」（キーの後ろがフォルダ）。
    # 出力は全部受け取ってから先頭を取る（パイプラインの途中で Select-Object -First が止めると、dotnet が最後まで走らず
    # $LASTEXITCODE が設定されない。StrictMode では未設定の読み取りがエラーになる）。
    $lines = @(& dotnet nuget locals global-packages --list)
    $exitCode = $LASTEXITCODE
    $line = if ($lines.Count -gt 0) { "$($lines[0])" } else { '' }
    if ($exitCode -ne 0 -or -not $line.Contains(':')) { throw "NuGet のグローバルパッケージフォルダが分かりません（dotnet nuget locals global-packages --list: $line）。" }
    return ($line -replace '^[^:]+:\s*', '').Trim()
}

# 同梱 .NET のパックを NuGet から取り寄せる（キャッシュに無いものだけ。dotnet restore の PackageDownload）。
# 戻り値: パック名 → 展開済みのフォルダ（<キャッシュ>/<小文字の名前>/<版>）
function Restore-DotnetRuntimePacks([string[]]$PackageIds, [string]$Version, [string]$TargetFramework) {
    $root = Get-NuGetPackagesRoot
    $folders = @{}
    $missing = @()
    foreach ($id in ($PackageIds | Sort-Object -Unique)) {
        $folder = Join-Path $root (Join-Path $id.ToLowerInvariant() $Version)
        $folders[$id] = $folder
        if (-not (Test-Path -LiteralPath (Join-Path $folder $NuGetCompletionMarker))) { $missing += $id }
    }
    if ($missing.Count -eq 0) {
        Write-Host "      パックは取り寄せ済み（$root）: $($folders.Keys -join ', ')"
        return $folders
    }
    Write-Host "      NuGet から取り寄せます（初回は 1 パック 25〜190 MB）: $($missing -join ', ')"
    New-Item -ItemType Directory -Force -Path $DotnetRestoreDir | Out-Null
    $items = ($missing | ForEach-Object { "    <PackageDownload Include=""$_"" Version=""[$Version]"" />" }) -join "`n"
    $project = @"
<Project Sdk="Microsoft.NET.Sdk">
  <!-- build_and_run.ps1 が生成する。同梱 .NET のランタイムパックを NuGet から取り寄せるだけ（ビルドはしない） -->
  <PropertyGroup>
    <TargetFramework>$TargetFramework</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
$items
  </ItemGroup>
</Project>
"@
    $projectPath = Join-Path $DotnetRestoreDir 'SeedDotnetRuntimePacks.csproj'
    Set-Content -LiteralPath $projectPath -Value $project -Encoding utf8NoBOM
    & dotnet restore $projectPath --nologo
    if ($LASTEXITCODE -ne 0) { throw "同梱 .NET のパックを取り寄せられませんでした（dotnet restore の終了コード $LASTEXITCODE）。" }
    foreach ($id in $missing) {
        if (-not (Test-Path -LiteralPath (Join-Path $folders[$id] $NuGetCompletionMarker))) { throw "取り寄せたはずのパックがありません: $($folders[$id])" }
    }
    return $folders
}

# その ABI の同梱 .NET の中身の識別子（content_id）。設定ファイル・このスクリプト・ABI・目録の書式の版から作る。
# NuGet のパックは版ごとに中身が変わらないので、これらが同じなら組み立て結果も同じ（再組み立てを省き、端末の展開も使い回す）。
function Get-DotnetContentId([string]$SettingsText, [string]$AbiName) {
    $scriptText = Get-Content -LiteralPath $PSCommandPath -Raw -Encoding utf8
    $material = "$DotnetBundleFormatVersion`n$AbiName`n$SettingsText`n$scriptText"
    $hash = [System.Security.Cryptography.SHA256]::HashData([System.Text.Encoding]::UTF8.GetBytes($material))
    return ([Convert]::ToHexString($hash).Substring(0, $DotnetContentIdLength)).ToLowerInvariant()
}

# 1 つの ABI の同梱 .NET を組み立てる。
#   .so                          → app/src/seedDotnet/jniLibs/<ABI>/（APK の lib/<ABI>/）
#   BCL・deps.json・runtimeconfig → app/src/seedDotnet/assets/seed/dotnet/<ABI>/<dotnet-root 内の相対パス>
#   目録                          → 同じ assets の bundle.json（ランタイムはこれを読んで files/dotnet/ へ並べる）
# 前回と同じ content_id の目録があれば何もしない。
function New-DotnetBundle($Dotnet, [string]$AbiName, [hashtable]$PackFolders) {
    $settings = $Dotnet.Settings
    $runtime = $Dotnet.Runtime
    $values = Get-DotnetTemplateValues $Dotnet $AbiName
    $contentId = Get-DotnetContentId $Dotnet.Text $AbiName
    $assetsDir = Join-Path $DotnetStagingDir "assets/$ApkPackageRootName/$DotnetBundleDirName/$AbiName"
    $jniDir = Join-Path $DotnetStagingDir "jniLibs/$AbiName"
    $manifestPath = Join-Path $assetsDir $DotnetBundleManifestName
    if (Test-Path -LiteralPath $manifestPath) {
        $previous = Get-Content -LiteralPath $manifestPath -Raw -Encoding utf8 | ConvertFrom-Json
        if ($previous.content_id -eq $contentId -and (Test-Path -LiteralPath $jniDir)) {
            Write-Host "      $AbiName`: 変更なし（content_id=$contentId）"
            return
        }
    }
    foreach ($dir in @($assetsDir, $jniDir)) {
        if (Test-Path -LiteralPath $dir) { Remove-Item -LiteralPath $dir -Recurse -Force }
        New-Item -ItemType Directory -Force -Path $dir | Out-Null
    }

    # パックの中の置き場（NuGet のランタイムパックは runtimes/<RID>/lib/<TFM>/ と runtimes/<RID>/native/）。
    $runtimeRoot = Join-Path $PackFolders[(Expand-DotnetTemplate $runtime.runtime_pack $values)] "runtimes/$(Expand-DotnetTemplate $runtime.runtime_rid $values)"
    $hostRoot = Join-Path $PackFolders[(Expand-DotnetTemplate $runtime.host_pack $values)] "runtimes/$(Expand-DotnetTemplate $runtime.host_rid $values)"
    $managedDir = Join-Path $runtimeRoot "lib/$($settings.target_framework)"
    $nativeDir = Join-Path $runtimeRoot 'native'
    $hostNativeDir = Join-Path $hostRoot 'native'
    $frameworkDir = "shared/$($settings.framework)/$($settings.version)"

    $files = [System.Collections.Generic.List[object]]::new()
    $shippedNative = [System.Collections.Generic.HashSet[string]]::new()
    # asset（BCL 等）を置いて目録へ足す。
    $addAsset = {
        param([System.IO.FileInfo]$Source, [string]$RelativePath)
        $dest = Join-Path $assetsDir $RelativePath
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $dest) | Out-Null
        Copy-Item -LiteralPath $Source.FullName -Destination $dest
        $files.Add([ordered]@{ path = $RelativePath; source = 'asset'; size = $Source.Length })
    }
    # .so を jniLibs へ置いて目録へ足す（dotnet-root 内の置き場は RelativePath。端末では nativeLibraryDir から写す）。
    $addNative = {
        param([System.IO.FileInfo]$Source, [string]$RelativePath)
        Copy-Item -LiteralPath $Source.FullName -Destination (Join-Path $jniDir $Source.Name)
        [void]$shippedNative.Add($Source.Name)
        $files.Add([ordered]@{ path = $RelativePath; source = 'native_library' })
    }

    # 1) BCL（lib/<TFM>/*.dll。Mono は System.Private.CoreLib.dll が native/ にある）
    foreach ($dll in @(Get-ChildItem -LiteralPath $managedDir -File -Filter '*.dll') + @(Get-ChildItem -LiteralPath $nativeDir -File -Filter '*.dll')) {
        & $addAsset $dll "$frameworkDir/$($dll.Name)"
    }
    # 2) 共有フレームワークの runtimeconfig（そのまま）
    $runtimeConfig = Get-Item -LiteralPath (Join-Path $managedDir "$($settings.framework)$FrameworkRuntimeConfigFileSuffix")
    & $addAsset $runtimeConfig "$frameworkDir/$($runtimeConfig.Name)"
    # 3) ランタイムの .so（デバッガ用など除外するもの・hostfxr / hostpolicy は除く）
    $hostLibraries = $settings.host_libraries.PSObject.Properties
    $hostLibraryNames = @($hostLibraries.Name)
    $excluded = @($runtime.excluded_native_files)
    foreach ($so in Get-ChildItem -LiteralPath $nativeDir -File -Filter '*.so') {
        if ($excluded -contains $so.Name -or $hostLibraryNames -contains $so.Name) { continue }
        & $addNative $so "$frameworkDir/$($so.Name)"
    }
    # 4) hostfxr / hostpolicy（host_pack から。置き場は host_libraries のひな形）
    $hostfxrPath = $null
    foreach ($library in $hostLibraries) {
        $relative = "$(Expand-DotnetTemplate $library.Value $values)/$($library.Name)"
        & $addNative (Get-Item -LiteralPath (Join-Path $hostNativeDir $library.Name)) $relative
        if ($library.Name -eq $HostfxrFileName) { $hostfxrPath = $relative }
    }
    if (-not $hostfxrPath) { throw "dotnet_runtime.json の host_libraries に $HostfxrFileName がありません。" }
    # 5) 共有フレームワークの deps.json（native の一覧を実際に入れた .so だけにする。.a・.jar・デバッガ用は入れないため）
    $depsName = "$($settings.framework)$FrameworkDepsFileSuffix"
    $deps = Get-Content -LiteralPath (Join-Path $managedDir $depsName) -Raw -Encoding utf8 | ConvertFrom-Json
    foreach ($target in $deps.targets.PSObject.Properties) {
        foreach ($library in $target.Value.PSObject.Properties) {
            $native = $library.Value.PSObject.Properties['native']
            if (-not $native) { continue }
            $kept = [ordered]@{}
            foreach ($entry in $native.Value.PSObject.Properties) {
                if ($shippedNative.Contains([System.IO.Path]::GetFileName($entry.Name))) { $kept[$entry.Name] = $entry.Value }
            }
            $library.Value.native = [pscustomobject]$kept
        }
    }
    $depsDest = Join-Path $assetsDir "$frameworkDir/$depsName"
    Set-Content -LiteralPath $depsDest -Value ($deps | ConvertTo-Json -Depth $JsonWriteDepth) -Encoding utf8NoBOM
    $files.Add([ordered]@{ path = "$frameworkDir/$depsName"; source = 'asset'; size = (Get-Item -LiteralPath $depsDest).Length })

    # 6) 目録（書式は engine::core::scripting::embedded_runtime::manifest::BundleManifest）
    $manifest = [ordered]@{
        format_version      = $DotnetBundleFormatVersion
        runtime             = $Dotnet.Kind
        framework           = $settings.framework
        version             = $settings.version
        abi                 = $AbiName
        content_id          = $contentId
        hostfxr             = $hostfxrPath
        native_library_mode = $settings.native_library_mode
        runtime_properties  = $settings.runtime_properties
        files               = $files
    }
    Set-Content -LiteralPath $manifestPath -Value ($manifest | ConvertTo-Json -Depth $JsonWriteDepth) -Encoding utf8NoBOM

    $assetBytes = ($files | Where-Object { $_.source -eq 'asset' } | ForEach-Object { $_.size } | Measure-Object -Sum).Sum
    $nativeBytes = (Get-ChildItem -LiteralPath $jniDir -File | Measure-Object Length -Sum).Sum
    Write-Host ("      {0}: {1} {2}  asset {3} ファイル {4:N1} MB・.so {5} 個 {6:N1} MB（content_id={7}）" -f `
        $AbiName, $Dotnet.Kind, $settings.version, ($files | Where-Object { $_.source -eq 'asset' }).Count, ($assetBytes / 1MB),
        $shippedNative.Count, ($nativeBytes / 1MB), $contentId)
}

# 同梱 .NET の Java 側（.jar）を app/src/seedDotnet/libs/ へ置く（app/build.gradle.kts が APK の Java クラスへ入れる）。
# CoreCLR の暗号ライブラリ（libSystem.Security.Cryptography.Native.Android.so）は JNI_OnLoad でこの .jar のクラスを探し、
# 無ければ abort() する。MainActivity（DotnetJniLibraries）がクラスの有無を見て System.loadLibrary する。
# .jar は ABI に依らないので、-Abi の先頭の ABI のパックから取る。毎回置き直す（数十 KB）。
function Update-DotnetJavaLibraries($Dotnet, [hashtable]$PackFolders) {
    $libsDir = Join-Path $DotnetStagingDir 'libs'
    if (Test-Path -LiteralPath $libsDir) { Remove-Item -LiteralPath $libsDir -Recurse -Force }
    $property = $Dotnet.Runtime.PSObject.Properties['java_libraries']
    # if 式の戻り値は 1 要素の配列が中身に展開されてしまうので、配列はここで作る。
    $jars = @()
    if ($property) { $jars = @($property.Value) }
    if ($jars.Count -eq 0) { return }
    New-Item -ItemType Directory -Force -Path $libsDir | Out-Null
    $values = Get-DotnetTemplateValues $Dotnet $Abi[0]
    $packRoot = Join-Path $PackFolders[(Expand-DotnetTemplate $Dotnet.Runtime.runtime_pack $values)] "runtimes/$(Expand-DotnetTemplate $Dotnet.Runtime.runtime_rid $values)"
    foreach ($jar in $jars) {
        Copy-Item -LiteralPath (Join-Path $packRoot "native/$jar") -Destination $libsDir
        Write-Host "      Java: $jar -> $libsDir"
    }
}

# 3. 同梱 .NET を -Abi の ABI ぶん組み立てる（他の ABI の前回分は消す。APK の assets は ABI で絞られないため）。
function Update-DotnetBundles {
    $dotnet = Get-DotnetRuntimeSettings
    $settings = $dotnet.Settings
    Write-Host "[3/7] 同梱 .NET: $($dotnet.Kind) $($settings.version)（$($Abi -join ', ')。設定 $DotnetSettingsPath）" -ForegroundColor Cyan

    $packIds = foreach ($abiName in $Abi) {
        $values = Get-DotnetTemplateValues $dotnet $abiName
        Expand-DotnetTemplate $dotnet.Runtime.runtime_pack $values
        Expand-DotnetTemplate $dotnet.Runtime.host_pack $values
    }
    $folders = Restore-DotnetRuntimePacks @($packIds) $settings.version $settings.target_framework

    foreach ($kind in @('assets', 'jniLibs')) {
        $parent = if ($kind -eq 'assets') { Join-Path $DotnetStagingDir "assets/$ApkPackageRootName/$DotnetBundleDirName" } else { Join-Path $DotnetStagingDir 'jniLibs' }
        Get-ChildItem -LiteralPath $parent -Directory -ErrorAction SilentlyContinue |
            Where-Object { $Abi -notcontains $_.Name } |
            ForEach-Object { Write-Host "      前回の $($_.Name) を消します（-Abi に無い）"; Remove-Item -LiteralPath $_.FullName -Recurse -Force }
    }
    foreach ($abiName in $Abi) { New-DotnetBundle $dotnet $abiName $folders }
    Update-DotnetJavaLibraries $dotnet $folders
}

# 4. gradlew assembleDebug で APK を作る。
#
# 画面の向き（$Orientation = screen_orientation の値）は -Pseed.orientation で渡し、
# app/build.gradle.kts の変換表がマニフェストの screenOrientation へ差し込む（APK に焼き込まれる）。
function Invoke-GradleBuild([string]$Ndk, [string]$Orientation) {
    Write-Host '[4/7] gradlew assembleDebug' -ForegroundColor Cyan
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

# 5. APK を端末へ入れる（既存のデータは残す）。
function Install-Apk([string]$Adb, [string[]]$TargetArgs) {
    Write-Host "[5/7] adb install -r $ApkPath" -ForegroundColor Cyan
    & $Adb @TargetArgs install -r $ApkPath
    if ($LASTEXITCODE -ne 0) { throw "adb install が失敗しました（終了コード $LASTEXITCODE）。" }
}

# 6. アセットフォルダをアプリの内部専用フォルダへ送る（-AssetsDir 指定時のみ）。
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
    Write-Host "[6/7] $AssetsDir -> (run-as $PackageName) $RemoteAssetsDir" -ForegroundColor Cyan
    $extract = "rm -rf $RemoteAssetsDir && mkdir -p $RemoteAssetsDir && tar -xf - -C $RemoteAssetsDir && chmod -R u+rwX,go-rwx $RemoteAssetsDir"
    & $HostTar -cf - -C $AssetsDir . | & $Adb @TargetArgs exec-in run-as $PackageName sh -c $extract
    if ($LASTEXITCODE -ne 0) { throw "アセットの転送が失敗しました（終了コード $LASTEXITCODE）。デバッグ版 APK がインストール済みか確認してください。" }
    # 置けたかを確かめる（exec-in は端末側の失敗を終了コードで返さないことがある）。
    $check = & $Adb @TargetArgs exec-out run-as $PackageName sh -c "test -f $RemoteAssetsDir/project_settings.json && echo ok"
    if ("$check".Trim() -ne 'ok') { throw "アセットの転送後に $RemoteAssetsDir/project_settings.json が見つかりません。" }
}

# 6. スクリプトの DLL だけを作り直して端末へ送る（-PushScripts 指定時のみ。APK は作り直さない）。
#
# -ProjectDir（無ければ -AssetsDir）の .cs を SeedPak --scripts-only で事前コンパイルし（APK に入れるものと同じ bin/）、
# DLL と runtimeconfig を端末の内部アプリ専用フォルダの files/bin/ へ送る。端末は files/bin/ を APK の bin/ より優先して読む
# （native/src/dotnet_runtime/script_sources.rs）。送り方は -AssetsDir と同じ run-as ＋ tar（デバッグ版 APK だけ。理由は
# Push-Assets のコメント）。前回送った分は消してから置き直す（古い DLL と混ざらないように）。消せば APK の bin/ へ戻る:
#   adb exec-out run-as com.seedengine.runtime rm -rf files/bin
# 送った後の再起動（force-stop → am start）は 7 が行う。
function Push-Scripts([string]$Adb, [string[]]$TargetArgs) {
    if (-not $PushScripts) { return }
    $source = if ($ProjectDir) { @('--project', $ProjectDir) } elseif ($AssetsDir) { @('--assets', $AssetsDir) } else { $null }
    if (-not $source) { throw '-PushScripts には、スクリプト（.cs）の出どころとして -ProjectDir か -AssetsDir を指定してください。' }
    if (-not (Test-Path -LiteralPath $HostTar)) { throw "tar が見つかりません: $HostTar" }
    Write-Host "[6/7] スクリプトの DLL を作り直して送ります: $($source[1]) -> (run-as $PackageName) $RemoteScriptsDir" -ForegroundColor Cyan
    if (Test-Path -LiteralPath $PushScriptsStagingDir) { Remove-Item -LiteralPath $PushScriptsStagingDir -Recurse -Force }
    Invoke-SeedPak (@($source) + @('--out', $PushScriptsStagingDir, '--scripts-only'))

    $binDir = Join-Path $PushScriptsStagingDir $BinDirName
    $files = @(foreach ($pattern in $ScriptBinaryPatterns) { Get-ChildItem -LiteralPath $binDir -File -Filter $pattern })
    if ($files.Count -eq 0) { throw "SeedPak の出力に送るファイルがありません: $binDir" }
    $extract = "rm -rf $RemoteScriptsDir && mkdir -p $RemoteScriptsDir && tar -xf - -C $RemoteScriptsDir && chmod -R u+rwX,go-rwx $RemoteScriptsDir"
    & $HostTar -cf - -C $binDir @($files.Name) | & $Adb @TargetArgs exec-in run-as $PackageName sh -c $extract
    if ($LASTEXITCODE -ne 0) { throw "スクリプトの DLL の転送が失敗しました（終了コード $LASTEXITCODE）。デバッグ版 APK がインストール済みか確認してください。" }
    # 置けたかを確かめる（exec-in は端末側の失敗を終了コードで返さないことがある）。
    $check = & $Adb @TargetArgs exec-out run-as $PackageName sh -c "test -f $RemoteScriptsDir/SEEDScripting.dll && echo ok"
    if ("$check".Trim() -ne 'ok') { throw "スクリプトの DLL の転送後に $RemoteScriptsDir/SEEDScripting.dll が見つかりません。" }
    Write-Host ("      {0} ファイル・{1:N1} MB を送りました（{2}）" -f $files.Count, (($files | Measure-Object Length -Sum).Sum / 1MB), (($files | ForEach-Object Name) -join ', '))
}

# 7. 起動して logcat を表示（・保存）する。
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
        Write-Host "[7/7] am start $LaunchActivity" -ForegroundColor Cyan
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
if ($ProjectDir -and $SkipGradle -and -not $PushScripts) {
    throw '-ProjectDir は APK を作り直すとき（か -PushScripts でスクリプトの出どころにするとき）だけ効きます（-SkipGradle だけとは同時に指定できません）。'
}
if ($PushScripts -and -not ($ProjectDir -or $AssetsDir)) {
    throw '-PushScripts には、スクリプト（.cs）の出どころとして -ProjectDir か -AssetsDir を指定してください。'
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
    Update-DotnetBundles
    Assert-JavaHome
    # 画面の向きは APK（マニフェスト）に焼き込まれる。-SkipGradle で APK を作り直さないときは前回の値のまま。
    $orientation = Resolve-ScreenOrientation -FromProjectDir $ProjectDir -FromAssetsDir $AssetsDir
    Invoke-GradleBuild $ndk $orientation
}

$needsDevice = (-not $NoInstall) -or $AssetsDir -or $PushScripts -or (-not $NoLaunch) -or (-not $NoLogcat)
if ($needsDevice) {
    # 関数の戻り値の空配列は $null に潰れるため @() で配列に戻す。
    $targetArgs = @(Get-AdbTargetArgs $adb)
    if (-not $NoInstall) { Install-Apk $adb $targetArgs }
    Push-Assets $adb $targetArgs
    Push-Scripts $adb $targetArgs
    Start-AppAndWatchLog $adb $targetArgs
}
