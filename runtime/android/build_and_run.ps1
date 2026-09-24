#requires -Version 7.4
# ============================================================
#  build_and_run.ps1 — SEED ランタイムを Android 向けにビルドし、端末で起動する（段階0）
#
#  【流れ】
#    1. cargo ndk で libSEED.so をビルドし app/src/main/jniLibs/<ABI>/ へ置く
#    2. gradlew assembleDebug で APK を作る
#    3. adb install -r で端末（実機／エミュレータ）へ入れる
#    4. （任意）アセットフォルダをアプリの内部専用フォルダへ送る（run-as で tar を流し込む）
#    5. am start で起動し、logcat（タグ SEED ほか）を表示・保存する
#  段階C（エディタの「実行」統合）はこのスクリプトの各関数を土台にする想定。
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
    [string]$AssetsDir,

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

# このスクリプトのあるフォルダ（runtime/android）を基準にする。
$AndroidRoot = $PSScriptRoot
$NativeCrateDir = Join-Path $AndroidRoot 'native'
$JniLibsDir = Join-Path $AndroidRoot 'app/src/main/jniLibs'
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

# ── 各工程 ─────────────────────────────────────────────────────────

# 1. cargo ndk で libSEED.so をビルドして jniLibs へ置く。
function Invoke-NativeBuild([string]$Ndk) {
    $env:ANDROID_NDK_HOME = $Ndk
    $cargoArgs = @('ndk')
    foreach ($target in $Abi) { $cargoArgs += @('-t', $target) }
    $cargoArgs += @('-P', "$AndroidApiLevel", '-o', $JniLibsDir, 'build')
    if ($Release) { $cargoArgs += '--release' }

    Write-Host "[1/5] cargo $($cargoArgs -join ' ')" -ForegroundColor Cyan
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

# 2. gradlew assembleDebug で APK を作る。
function Invoke-GradleBuild([string]$Ndk) {
    Write-Host '[2/5] gradlew assembleDebug' -ForegroundColor Cyan
    Push-Location -LiteralPath $AndroidRoot
    try {
        # -Abi で選んだ ABI だけを APK に詰める（jniLibs に残っている別 ABI の古い .so を詰めないため）。
        & (Join-Path $AndroidRoot 'gradlew.bat') assembleDebug "-Pseed.ndkPath=$Ndk" "-Pseed.abis=$($Abi -join ',')" --console=plain
        if ($LASTEXITCODE -ne 0) { throw "gradlew assembleDebug が失敗しました（終了コード $LASTEXITCODE）。" }
    }
    finally {
        Pop-Location
    }
    if (-not (Test-Path -LiteralPath $ApkPath)) { throw "APK が見つかりません: $ApkPath" }
    Write-Host ("      {0}  {1:N1} MB" -f $ApkPath, ((Get-Item -LiteralPath $ApkPath).Length / 1MB))
}

# 3. APK を端末へ入れる（既存のデータは残す）。
function Install-Apk([string]$Adb, [string[]]$TargetArgs) {
    Write-Host "[3/5] adb install -r $ApkPath" -ForegroundColor Cyan
    & $Adb @TargetArgs install -r $ApkPath
    if ($LASTEXITCODE -ne 0) { throw "adb install が失敗しました（終了コード $LASTEXITCODE）。" }
}

# 4. アセットフォルダをアプリの内部専用フォルダへ送る（-AssetsDir 指定時のみ）。
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
    Write-Host "[4/5] $AssetsDir -> (run-as $PackageName) $RemoteAssetsDir" -ForegroundColor Cyan
    $extract = "rm -rf $RemoteAssetsDir && mkdir -p $RemoteAssetsDir && tar -xf - -C $RemoteAssetsDir && chmod -R u+rwX,go-rwx $RemoteAssetsDir"
    & $HostTar -cf - -C $AssetsDir . | & $Adb @TargetArgs exec-in run-as $PackageName sh -c $extract
    if ($LASTEXITCODE -ne 0) { throw "アセットの転送が失敗しました（終了コード $LASTEXITCODE）。デバッグ版 APK がインストール済みか確認してください。" }
    # 置けたかを確かめる（exec-in は端末側の失敗を終了コードで返さないことがある）。
    $check = & $Adb @TargetArgs exec-out run-as $PackageName sh -c "test -f $RemoteAssetsDir/project_settings.json && echo ok"
    if ("$check".Trim() -ne 'ok') { throw "アセットの転送後に $RemoteAssetsDir/project_settings.json が見つかりません。" }
}

# 5. 起動して logcat を表示（・保存）する。
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
        Write-Host "[5/5] am start $LaunchActivity" -ForegroundColor Cyan
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

$sdk = Resolve-AndroidSdk
$ndk = Resolve-AndroidNdk $sdk
$adb = Join-Path $sdk 'platform-tools/adb.exe'
if (-not (Test-Path -LiteralPath $adb)) { throw "adb が見つかりません: $adb（SDK Manager で Platform-Tools を入れてください）" }

if (-not $SkipRustBuild) { Assert-CargoNdk; Invoke-NativeBuild $ndk }
if (-not $SkipGradle) { Assert-JavaHome; Invoke-GradleBuild $ndk }

$needsDevice = (-not $NoInstall) -or $AssetsDir -or (-not $NoLaunch) -or (-not $NoLogcat)
if ($needsDevice) {
    # 関数の戻り値の空配列は $null に潰れるため @() で配列に戻す。
    $targetArgs = @(Get-AdbTargetArgs $adb)
    if (-not $NoInstall) { Install-Apk $adb $targetArgs }
    Push-Assets $adb $targetArgs
    Start-AppAndWatchLog $adb $targetArgs
}
