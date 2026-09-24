#requires -Version 7.0
# ============================================================
#  build_and_run.ps1 — SeedAndroid（Android のビルド・配置・起動のツール）を呼ぶだけの互換ラッパー（段階C）
#
#  【中身はどこにあるか】
#  以前はこのスクリプトに Android の手順（cargo ndk → SeedPak → 同梱 .NET → Gradle → adb install → run-as の転送 →
#  am start → logcat）がすべて入っていた。段階C でこれを C# の中核（editor/src/Android/。エディタの実行先セレクタと共有）へ移し、
#  コンソールツール editor/tools/SeedAndroid から使うようにした。このスクリプトは従来の引数を SeedAndroid の
#  run サブコマンドの引数へ置き換えて呼ぶだけ（手順の重複は持たない）。新しい使い方は docs/android.md §5。
#
#  【従来との違い】
#    - 入力が前回から変わっていない工程（libSEED.so・pak・同梱 .NET・APK・インストール）は自動で飛ばす
#      （すべて作り直すには SeedAndroid の --rebuild。このラッパーには無い）
#    - -Abi を省略すると、両方ではなく端末の ABI だけを作る（端末が決まらないときは従来どおり両方）
#    - -LogFile の logcat は UTF-8 のまま保存する（以前は日本語が化けた）
#    - pwsh 7.4 の制限（tar をパイプで渡すため）は無くなった。日本語を含むので Windows PowerShell 5.1 では動かさない
#
#  【使用例】（引数の意味は従来どおり。docs/android.md §5.1）
#    pwsh -File runtime/android/build_and_run.ps1 -Abi x86_64 -Serial emulator-5554 -ProjectDir D:\path\to\project -LogcatSeconds 20
#    pwsh -File runtime/android/build_and_run.ps1 -Serial emulator-5554 -AssetsDir D:\path\to\project\assets
#    pwsh -File runtime/android/build_and_run.ps1 -Serial emulator-5554 -SkipRustBuild -SkipGradle -NoInstall `
#         -ProjectDir D:\path\to\project -PushScripts        # スクリプトの DLL だけを差し替えて再起動
#  終了コードは SeedAndroid のもの（0 成功 / 1 指定の誤り / 2 道具が無い / 3 端末 / 4 ビルド / 5 端末の操作 / 130 中断）。
# ============================================================

[CmdletBinding()]
param(
    # Rust 側を --release でビルドする（APK はデバッグ署名のまま）。
    [switch]$Release,

    # ビルドする ABI。実機は arm64-v8a、PC のエミュレータは x86_64。省略すると端末から判定する（決まらなければ両方）。
    [ValidateSet('arm64-v8a', 'x86_64')]
    [string[]]$Abi = @('arm64-v8a', 'x86_64'),

    # 対象端末のシリアル（adb devices の左列）。端末が 2 台以上つながっているときは必須。
    [string]$Serial,

    # 開発用: 端末の内部アプリ専用フォルダへ送るアセットフォルダ（project_settings.json を含む）。APK は pak の無い開発用になる。
    [string]$AssetsDir,

    # APK に pak とスクリプトを入れるプロジェクトフォルダ（.seedproj か assets/ を持つフォルダ、またはアセットルートそのもの）。
    [string]$ProjectDir,

    # スクリプトの DLL だけを作り直して端末の files/bin/ へ送り、アプリを再起動する（APK を作り直さない高速経路）。
    [switch]$PushScripts,

    # 各工程を飛ばす（明示の指定は自動判定より強い）。
    [switch]$SkipRustBuild,
    [switch]$SkipGradle,
    [switch]$NoInstall,
    [switch]$NoLaunch,
    [switch]$NoLogcat,

    # logcat を何秒流して終えるか。0 なら Ctrl+C まで流し続ける。
    [int]$LogcatSeconds = 0,

    # logcat の保存先（省略時は保存しない。UTF-8）。
    [string]$LogFile
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# このスクリプトのあるフォルダ（runtime/android）からリポジトリと SeedAndroid の場所を決める。
$RepoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$SeedAndroidProject = Join-Path $RepoRoot 'editor/tools/SeedAndroid'

# 相対パスは、このスクリプトを呼んだ場所（PowerShell の現在の場所）からの相対として絶対パスへ直す
# （dotnet run の作業フォルダに左右されないように）。
function ConvertTo-FullPath([string]$Path) {
    return [System.IO.Path]::GetFullPath($Path, (Get-Location).ProviderPath)
}

# ── 従来の引数 → SeedAndroid run の引数 ─────────────────────────────
$toolArgs = [System.Collections.Generic.List[string]]::new()
$toolArgs.Add('run')
# -Abi は指定されたときだけ渡す（省略時は SeedAndroid が端末から判定する）
if ($PSBoundParameters.ContainsKey('Abi')) { $toolArgs.AddRange([string[]]@('--abi', ($Abi -join ','))) }
if ($Release) { $toolArgs.Add('--release') }
if ($Serial) { $toolArgs.AddRange([string[]]@('--serial', $Serial)) }
if ($ProjectDir) { $toolArgs.AddRange([string[]]@('--project', (ConvertTo-FullPath $ProjectDir))) }
if ($AssetsDir) { $toolArgs.AddRange([string[]]@('--assets-dir', (ConvertTo-FullPath $AssetsDir))) }
if ($PushScripts) { $toolArgs.Add('--push-scripts') }
if ($SkipRustBuild) { $toolArgs.Add('--skip-rust') }
if ($SkipGradle) { $toolArgs.Add('--skip-gradle') }
if ($NoInstall) { $toolArgs.Add('--no-install') }
if ($NoLaunch) { $toolArgs.Add('--no-launch') }
if ($NoLogcat) { $toolArgs.Add('--no-logcat') }
if ($LogcatSeconds -gt 0) { $toolArgs.AddRange([string[]]@('--logcat-seconds', "$LogcatSeconds")) }
if ($LogFile) { $toolArgs.AddRange([string[]]@('--log-file', (ConvertTo-FullPath $LogFile))) }

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw 'dotnet が見つかりません。SeedAndroid（editor/tools/SeedAndroid）の実行に .NET 10 SDK が必要です。'
}
Write-Host "dotnet run --project $SeedAndroidProject -- $($toolArgs -join ' ')" -ForegroundColor DarkGray
& dotnet run --project $SeedAndroidProject -- @toolArgs
exit $LASTEXITCODE
