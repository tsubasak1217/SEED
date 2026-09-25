// ============================================================
//  SeedAndroidArguments.cs — SeedAndroid のコマンドライン引数の解釈（純粋な処理）
//
//  【役割】
//  文字列の配列をサブコマンドと指定の値に変換し、設定 JSON（--config）の値の上へ重ねて AndroidRunRequest を作る。
//  ファイルシステムには触らない（設定 JSON の読み込みは RunRequestConfig、フォルダの実在確認は中核の役目）。
//
//  【重ね方】
//  設定 JSON の値を土台にし、コマンドラインで指定したものだけで上書きする（文字列は指定があれば置き換え、
//  スイッチは指定があれば true にする）。目的（goal）はサブコマンドで決まる。
// ============================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using SEEDEditor.Android;
using SEEDEditor.Android.Adb;
using SEEDEditor.Android.Ipc;
using SEEDEditor.Android.Pipeline;

namespace SEEDEditor.Tools.SeedAndroid;

/// <summary>サブコマンド。</summary>
public enum SeedAndroidCommand
{
    /// <summary>つながっている端末の一覧。</summary>
    Devices,

    /// <summary>APK を作るまで。</summary>
    Build,

    /// <summary>APK を作って端末へ入れるまで。</summary>
    Install,

    /// <summary>作って入れて起動し、logcat を流す（一気通貫）。</summary>
    Run,

    /// <summary>スクリプトの DLL（と開発用のアセット）だけを送って起動し直す（高速経路）。</summary>
    Push,

    /// <summary>アプリを止める。</summary>
    Stop,

    /// <summary>logcat を流す。</summary>
    Logcat,

    /// <summary>動いているアプリを一時停止する（IPC の PAUSE。段階D-1）。</summary>
    Pause,

    /// <summary>動いているアプリの一時停止を解く（IPC の RESUME。段階D-1）。</summary>
    Resume,

    /// <summary>動いているアプリの画面を撮って PC へ取り出す（IPC の SCREENSHOT。段階D-1）。</summary>
    Screenshot,

    /// <summary>動いているアプリへ差し替えを頼む（IPC の RELOAD_SCENE / RELOAD_SCRIPTS / RELOAD_ASSET。docs/android.md §23）。</summary>
    Reload,
}

/// <summary>reload の対象（docs/android.md §23）。</summary>
public enum SeedAndroidReloadTarget
{
    /// <summary>今のシーンを読み直す（RELOAD_SCENE）。</summary>
    Scene,

    /// <summary>スクリプトの DLL を作り直して送り、読み直す（SeedPak --scripts-only → files/bin/ → RELOAD_SCRIPTS）。</summary>
    Scripts,

    /// <summary>アセットを差し替える（RELOAD_ASSET:{相対パス}。先に push --assets で送っておく）。</summary>
    Asset,
}

/// <summary>コマンドラインで指定された値（指定されなかったものは null / false）。</summary>
public sealed record SeedAndroidCommandLine
{
    /// <summary>サブコマンド。</summary>
    public required SeedAndroidCommand Command { get; init; }

    /// <summary>--config の値（設定 JSON）。</summary>
    public string? ConfigPath { get; init; }

    /// <summary>--project の値。</summary>
    public string? ProjectDir { get; init; }

    /// <summary>--assets-dir の値。</summary>
    public string? AssetsDir { get; init; }

    /// <summary>--serial の値（"auto" は自動。段階C-3）。</summary>
    public string? Serial { get; init; }

    /// <summary>--avd の値（エミュレータを起動するときの AVD。段階C-3）。</summary>
    public string? Avd { get; init; }

    /// <summary>--scene の値（起動するシーン。アセットルートからの相対パス等。段階C-3）。</summary>
    public string? ScenePath { get; init; }

    /// <summary>--abi の値（カンマ区切りを分けたもの）。</summary>
    public IReadOnlyList<string>? Abis { get; init; }

    /// <summary>--logcat-seconds の値。</summary>
    public int? LogcatSeconds { get; init; }

    /// <summary>--log-file の値。</summary>
    public string? LogFile { get; init; }

    /// <summary>--app-id の値（stop でプロジェクトを指定しないとき）。</summary>
    public string? ApplicationId { get; init; }

    /// <summary>--since の値（logcat の起点。端末の時刻 "MM-dd HH:mm:ss.fff"）。</summary>
    public string? Since { get; init; }

    /// <summary>--ipc-port の値（端末のランタイムが IPC を待ち受けるポート。0 なら使わない。段階D-1）。</summary>
    public int? IpcPort { get; init; }

    /// <summary>--out の値（screenshot の書き先。段階D-1）。</summary>
    public string? OutputPath { get; init; }

    /// <summary>
    /// push の --assets の値（端末の上書き層 files/assets へ、端末と違うアセットだけを送るフォルダ。アセットルートかその中。§23）。
    /// </summary>
    public string? OverlayAssetsDir { get; init; }

    /// <summary>reload の対象（reload のときだけ）。</summary>
    public SeedAndroidReloadTarget? ReloadTarget { get; init; }

    /// <summary>reload asset のアセット（アセットルートからの相対パス）。</summary>
    public string? ReloadPath { get; init; }

    /// <summary>--release。</summary>
    public bool Release { get; init; }

    /// <summary>--skip-rust。</summary>
    public bool SkipNativeBuild { get; init; }

    /// <summary>--skip-gradle。</summary>
    public bool SkipGradle { get; init; }

    /// <summary>--no-install。</summary>
    public bool NoInstall { get; init; }

    /// <summary>--no-launch。</summary>
    public bool NoLaunch { get; init; }

    /// <summary>--no-logcat。</summary>
    public bool NoLogcat { get; init; }

    /// <summary>--push-scripts。</summary>
    public bool PushScripts { get; init; }

    /// <summary>--rebuild。</summary>
    public bool Rebuild { get; init; }

    /// <summary>--json（devices の出力を JSON にする）。</summary>
    public bool Json { get; init; }
}

/// <summary>引数を解釈した結果（成功・ヘルプ要求・エラーのどれか）。</summary>
/// <param name="CommandLine">成功したときの値。</param>
/// <param name="ShowHelp">ヘルプの表示を求められた。</param>
/// <param name="Error">エラーの説明（成功時は null）。</param>
public sealed record SeedAndroidParseResult(SeedAndroidCommandLine? CommandLine, bool ShowHelp, string? Error);

/// <summary>SeedAndroid のコマンドライン引数の解釈。</summary>
public static class SeedAndroidArguments
{
    // ── サブコマンドの名前 ─────────────────────────────────

    /// <summary>サブコマンドの名前 → サブコマンド。</summary>
    private static readonly Dictionary<string, SeedAndroidCommand> Commands = new(StringComparer.OrdinalIgnoreCase)
    {
        ["devices"] = SeedAndroidCommand.Devices,
        ["build"]   = SeedAndroidCommand.Build,
        ["install"] = SeedAndroidCommand.Install,
        ["run"]     = SeedAndroidCommand.Run,
        ["push"]    = SeedAndroidCommand.Push,
        ["stop"]    = SeedAndroidCommand.Stop,
        ["logcat"]  = SeedAndroidCommand.Logcat,
        ["pause"]      = SeedAndroidCommand.Pause,
        ["resume"]     = SeedAndroidCommand.Resume,
        ["screenshot"] = SeedAndroidCommand.Screenshot,
        ["reload"]     = SeedAndroidCommand.Reload,
    };

    /// <summary>reload の対象の名前 → 対象。</summary>
    private static readonly Dictionary<string, SeedAndroidReloadTarget> ReloadTargets = new(StringComparer.OrdinalIgnoreCase)
    {
        ["scene"]   = SeedAndroidReloadTarget.Scene,
        ["scripts"] = SeedAndroidReloadTarget.Scripts,
        ["asset"]   = SeedAndroidReloadTarget.Asset,
    };

    /// <summary>オプションの接頭辞（これで始まらない語は reload の対象・アセットのパス）。</summary>
    private const string OptionPrefix = "--";

    /// <summary>ヘルプを求める語（サブコマンドの位置）。</summary>
    private const string HelpCommand = "help";

    // ── オプションの名前（値を取るもの）────────────────────────

    /// <summary>設定 JSON。</summary>
    public const string ConfigOption = "--config";

    /// <summary>プロジェクトフォルダ。</summary>
    public const string ProjectOption = "--project";

    /// <summary>開発用のアセットフォルダ。</summary>
    public const string AssetsDirOption = "--assets-dir";

    /// <summary>端末のシリアル。</summary>
    public const string SerialOption = "--serial";

    /// <summary>ABI。</summary>
    public const string AbiOption = "--abi";

    /// <summary>logcat の秒数。</summary>
    public const string LogcatSecondsOption = "--logcat-seconds";

    /// <summary>logcat の保存先。</summary>
    public const string LogFileOption = "--log-file";

    /// <summary>アプリ ID（stop）。</summary>
    public const string ApplicationIdOption = "--app-id";

    /// <summary>logcat の起点。</summary>
    public const string SinceOption = "--since";

    /// <summary>エミュレータを起動するときの AVD。</summary>
    public const string AvdOption = "--avd";

    /// <summary>起動するシーン。</summary>
    public const string SceneOption = "--scene";

    /// <summary>端末のランタイムが IPC を待ち受けるポート（段階D-1）。</summary>
    public const string IpcPortOption = "--ipc-port";

    /// <summary>screenshot の書き先（段階D-1）。</summary>
    public const string OutOption = "--out";

    /// <summary>push で端末の上書き層へ差分だけを送るフォルダ（§23）。</summary>
    public const string OverlayAssetsOption = "--assets";

    // ── オプションの名前（値を取らないもの）──────────────────────

    /// <summary>--release。</summary>
    public const string ReleaseOption = "--release";

    /// <summary>--skip-rust。</summary>
    public const string SkipRustOption = "--skip-rust";

    /// <summary>--skip-gradle。</summary>
    public const string SkipGradleOption = "--skip-gradle";

    /// <summary>--no-install。</summary>
    public const string NoInstallOption = "--no-install";

    /// <summary>--no-launch。</summary>
    public const string NoLaunchOption = "--no-launch";

    /// <summary>--no-logcat。</summary>
    public const string NoLogcatOption = "--no-logcat";

    /// <summary>--push-scripts。</summary>
    public const string PushScriptsOption = "--push-scripts";

    /// <summary>--rebuild。</summary>
    public const string RebuildOption = "--rebuild";

    /// <summary>--json。</summary>
    public const string JsonOption = "--json";

    /// <summary>ヘルプ。</summary>
    private static readonly HashSet<string> HelpOptions = new(StringComparer.Ordinal) { "--help", "-h", "/?" };

    /// <summary>使い方の説明文。</summary>
    public const string Usage = """
        SeedAndroid — SEED のゲームを Android の実機・エミュレータでビルド・インストール・起動する（段階C。docs/android.md §5）

        使い方:
          dotnet run --project editor/tools/SeedAndroid -- <サブコマンド> [オプション]

        サブコマンド:
          devices   つながっている端末の一覧（シリアル・種類・機種・状態・ABI）。--json で JSON
          build     APK を作る（libSEED.so → pak とスクリプト → 同梱 .NET → Gradle）
          install   build ＋ 端末へ入れる
          run       install ＋ 起動 ＋ logcat（一気通貫。Ctrl+C で logcat を止めて終える）
          push      スクリプトの DLL（と --assets-dir のアセット）だけを送って起動し直す（APK は作り直さない）
                    push --assets <フォルダ> は、端末と違うアセットだけを上書き層 files/assets へ送る（起動し直さない。
                    差し替えの差分転送。取り込ませるのは reload。docs/android.md §23）
          stop      アプリを止める（am force-stop）
          logcat    logcat を流す（タグ SEED・DOTNET ほか。Ctrl+C で止める）
          pause     動いているアプリを一時停止する（adb forward → IPC の PAUSE → 切り離して閉じる。一時停止のまま残る）
          resume    動いているアプリの一時停止を解く（IPC の RESUME）
          screenshot 動いているアプリの画面を撮って PC へ取り出す（IPC の SCREENSHOT → run-as で PNG を取り出す。--out）
          reload scene | reload scripts | reload asset <相対パス>
                    動いているアプリへ差し替えを頼む（実行中の差し替え。docs/android.md §23）
                      scene   … 今のシーンをディスク（上書き層 files/assets → APK の pak）から読み直す（RELOAD_SCENE）
                      scripts … SeedPak --scripts-only で DLL を作り直して files/bin/ へ送り、読み直す（RELOAD_SCRIPTS）
                      asset   … そのアセットのキャッシュを捨てて取り込み直す（RELOAD_ASSET。先に push --assets で送る）
                    （pause / resume / screenshot / reload は run / push で起動したアプリだけ。起動の工程がプロジェクトの
                     cache/android/run_state.json に記録した接続トークンでつなぐ。エディタの実行中はエディタが使うので使えない）

        オプション:
          --project <フォルダ>      プロジェクト（.seedproj か assets/ を持つフォルダ、またはアセットルートそのもの）。
                                    APK に pak とスクリプトを入れる。アプリの識別情報・画面の向きもここから読む
          --assets-dir <フォルダ>   開発用: pak の無い APK にして、このアセットフォルダを run-as で端末へ送る（--project と排他）
          --serial <シリアル>       対象の端末（省略時は使える端末がちょうど 1 台のときそれ）。
                                    auto: 実機（前回使ったものを優先）→ 起動中のエミュレータ → どちらも無ければ AVD を起動して
                                    起動の完了を待つ（install / run / push。build では端末を起動しない。stop / logcat には使えない）
          --avd <AVD>               auto でエミュレータを起動するときの AVD（省略時は seed_pixel6_api35、無ければ
                                    emulator -list-avds の先頭）
          --scene <シーン>          起動するシーン（アセットルートからの相対パス 例 scenes/Main.scene・assets://…・
                                    アセットルートの中の絶対パス。省略時は project_settings.json の開始シーン。
                                    シーンマネージャに未登録のシーンは pak の収録の起点に足す〈SeedPak --extra-scene。
                                    切り替えた最初の run は pak・APK を作り直す〉。プロジェクトに無いシーンは
                                    端末が警告を出して開始シーンで起動する）
          --abi <ABI[,ABI]>         arm64-v8a / x86_64（省略時は端末から判定。端末が無ければ両方）
          --release                 Rust 側を --release でビルドする（APK はデバッグ署名のまま）
          --config <JSON>           指定をまとめた設定 JSON（キーは project / assets_dir / serial / emulator_fallback / avd /
                                    scene / abis / release / skip_rust_build / skip_gradle / no_install / no_launch / no_logcat /
                                    push_scripts / rebuild / logcat_seconds / log_file / ipc_port。project・assets_dir・log_file の相対パスは
                                    JSON のフォルダから、scene はアセットルートから。コマンドラインが優先）
          --skip-rust               libSEED.so のビルドを飛ばす
          --skip-gradle             APK の作成（pak とスクリプト・同梱 .NET・Gradle）を飛ばす
          --no-install / --no-launch / --no-logcat   インストール / 起動 / logcat を飛ばす
          --push-scripts            run でもスクリプトの DLL を作り直して端末の files/bin/ へ送る
          --rebuild                 変更の有無で工程を自動で飛ばさない（すべて作り直し、入れ直す）
          --logcat-seconds <秒>     logcat を流す秒数（0 か省略で止めるまで）
          --log-file <パス>         logcat の保存先（UTF-8）
          --app-id <ID>             stop / pause / resume / screenshot の対象のアプリ（省略時は --project の設定から。
                                    無ければ com.seedengine.runtime）
          --since <時刻>            logcat の起点（端末の時刻 "MM-dd HH:mm:ss.fff"。省略時は今）
          --ipc-port <ポート>       端末のランタイムが一時停止などの IPC を待ち受けるポート（run / push が起動オプションで渡す。
                                    省略時は 52735、0 なら渡さない。pause / resume / screenshot は省略時に起動の記録のポートへつなぐ）
          --out <パス>              screenshot の書き先（省略時はカレントフォルダの android_screenshot_<日時>.png）
          --assets <フォルダ>       push: 端末と違うアセットだけを上書き層 files/assets へ送る（アセットルートかその中。
                                    今 pak を作ると入るもの＋全シーンから辿れるもののうち、手元の中身が端末〈送った記録 → APK の pak〉と
                                    違うものだけ。--project が無ければこのフォルダからプロジェクトを探す。--assets-dir とは別物で同時に使えない）
          --json                    devices の出力を JSON にする
          --help, -h                この説明を表示する

        入力が前回から変わっていない工程（libSEED.so・pak・同梱 .NET・APK・インストール）は自動で飛ばす。

        終了コード: 0 成功 / 1 指定の誤り / 2 道具が無い / 3 端末が無い・選べない / 4 ビルドの失敗 / 5 端末の操作の失敗 / 130 中断
        """;

    /// <summary>
    /// コマンドライン引数を解釈する。
    /// </summary>
    /// <param name="args">コマンドライン引数（先頭がサブコマンド）。</param>
    /// <returns>解釈結果。</returns>
    public static SeedAndroidParseResult Parse(IReadOnlyList<string> args)
    {
        if (args.Count == 0 || HelpOptions.Contains(args[0]) || string.Equals(args[0], HelpCommand, StringComparison.OrdinalIgnoreCase))
        {
            return new SeedAndroidParseResult(null, ShowHelp: true, Error: null);
        }
        if (!Commands.TryGetValue(args[0], out var command))
        {
            return Fail($"不明なサブコマンドです: {args[0]}（{string.Join(" / ", Commands.Keys)}）");
        }

        var line = new SeedAndroidCommandLine { Command = command };
        for (var i = 1; i < args.Count; i++)
        {
            var arg = args[i];
            if (HelpOptions.Contains(arg)) return new SeedAndroidParseResult(null, ShowHelp: true, Error: null);

            // 値を取らないスイッチ
            switch (arg)
            {
                case ReleaseOption:     line = line with { Release = true }; continue;
                case SkipRustOption:    line = line with { SkipNativeBuild = true }; continue;
                case SkipGradleOption:  line = line with { SkipGradle = true }; continue;
                case NoInstallOption:   line = line with { NoInstall = true }; continue;
                case NoLaunchOption:    line = line with { NoLaunch = true }; continue;
                case NoLogcatOption:    line = line with { NoLogcat = true }; continue;
                case PushScriptsOption: line = line with { PushScripts = true }; continue;
                case RebuildOption:     line = line with { Rebuild = true }; continue;
                case JsonOption:        line = line with { Json = true }; continue;
            }

            // reload の対象（scene / scripts / asset）とアセットのパス（オプションでない語。§23）
            if (command == SeedAndroidCommand.Reload && !arg.StartsWith(OptionPrefix, StringComparison.Ordinal))
            {
                if (line.ReloadTarget is null)
                {
                    if (!ReloadTargets.TryGetValue(arg, out var target))
                    {
                        return Fail($"reload の対象は {string.Join(" / ", ReloadTargets.Keys)} のどれかです: {arg}");
                    }
                    line = line with { ReloadTarget = target };
                    continue;
                }
                if (line.ReloadTarget == SeedAndroidReloadTarget.Asset && line.ReloadPath is null)
                {
                    line = line with { ReloadPath = arg };
                    continue;
                }
                return Fail($"reload の余分な引数です: {arg}");
            }

            // 以降のオプションはすべて値を 1 つ取る
            if (arg is not (ConfigOption or ProjectOption or AssetsDirOption or SerialOption or AbiOption or LogcatSecondsOption
                or LogFileOption or ApplicationIdOption or SinceOption or AvdOption or SceneOption or IpcPortOption or OutOption
                or OverlayAssetsOption))
            {
                return Fail($"不明な引数です: {arg}");
            }
            if (i + 1 >= args.Count || string.IsNullOrWhiteSpace(args[i + 1]))
            {
                return Fail($"{arg} には値が必要です");
            }
            var value = args[++i];
            switch (arg)
            {
                case ConfigOption:        line = line with { ConfigPath = value }; break;
                case ProjectOption:       line = line with { ProjectDir = value }; break;
                case AssetsDirOption:     line = line with { AssetsDir = value }; break;
                case SerialOption:        line = line with { Serial = value }; break;
                case LogFileOption:       line = line with { LogFile = value }; break;
                case ApplicationIdOption: line = line with { ApplicationId = value }; break;
                case SinceOption:         line = line with { Since = value }; break;
                case AvdOption:           line = line with { Avd = value }; break;
                case SceneOption:         line = line with { ScenePath = value }; break;
                case OutOption:           line = line with { OutputPath = value }; break;
                case OverlayAssetsOption: line = line with { OverlayAssetsDir = value }; break;
                case IpcPortOption:
                    if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var ipcPort)
                        || AndroidIpcSettings.Validate(ipcPort) is { } ipcPortError)
                    {
                        return Fail($"{IpcPortOption} には {AndroidIpcSettings.DisabledPort}（使わない）か " +
                                    $"{AndroidIpcSettings.MinPort}〜{AndroidIpcSettings.MaxPort} の整数を指定してください: {value}");
                    }
                    line = line with { IpcPort = ipcPort };
                    break;
                case AbiOption:
                    AndroidAbis.ParseList(value, out var abiError);
                    if (abiError is not null) return Fail($"{AbiOption}: {abiError}");
                    line = line with { Abis = value.Split(AndroidAbis.ListSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) };
                    break;
                case LogcatSecondsOption:
                    if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds) || seconds < 0)
                    {
                        return Fail($"{LogcatSecondsOption} には 0 以上の整数を指定してください: {value}");
                    }
                    line = line with { LogcatSeconds = seconds };
                    break;
            }
        }

        if (line.ProjectDir is not null && line.AssetsDir is not null)
        {
            return Fail($"{ProjectOption} と {AssetsDirOption} は同時に指定できません（端末は APK の pak を優先します）");
        }
        // 「自動」は端末を用意する（要ればエミュレータを起動する）経路なので、既にある端末を操作するだけのサブコマンドでは使わない
        if (AndroidDeviceTarget.IsAutoSerial(line.Serial) && OperatesRunningDevice(command))
        {
            return Fail($"{SerialOption} {AndroidDeviceTarget.AutoSerial} は build / install / run / push で使えます" +
                        "（stop / logcat / pause / resume / screenshot / reload にはシリアルを指定してください）");
        }
        if (line.OutputPath is not null && command != SeedAndroidCommand.Screenshot)
        {
            return Fail($"{OutOption} は screenshot で使います");
        }
        // ── 実行中の差し替え（§23）──
        if (command == SeedAndroidCommand.Reload)
        {
            if (line.ReloadTarget is null)
            {
                return Fail($"reload には対象（{string.Join(" / ", ReloadTargets.Keys)}）を指定してください");
            }
            if (line.ReloadTarget == SeedAndroidReloadTarget.Asset && string.IsNullOrWhiteSpace(line.ReloadPath))
            {
                return Fail("reload asset には差し替えるアセット（アセットルートからの相対パス）を指定してください");
            }
        }
        if (line.OverlayAssetsDir is not null)
        {
            if (command != SeedAndroidCommand.Push)
            {
                return Fail($"{OverlayAssetsOption} は push で使います");
            }
            if (line.AssetsDir is not null)
            {
                return Fail($"{OverlayAssetsOption}（差分の転送）と {AssetsDirOption}（開発用の APK）は同時に指定できません");
            }
            if (AndroidDeviceTarget.IsAutoSerial(line.Serial))
            {
                return Fail($"push {OverlayAssetsOption} では {SerialOption} {AndroidDeviceTarget.AutoSerial} を使えません（動いている端末へ送るだけのため）");
            }
        }
        return new SeedAndroidParseResult(line, ShowHelp: false, Error: null);
    }

    /// <summary>既にある端末（動いているアプリ）を操作するだけのサブコマンドか（ビルド・インストールをしない）。</summary>
    /// <param name="command">サブコマンド。</param>
    /// <returns>そうなら true。</returns>
    public static bool OperatesRunningDevice(SeedAndroidCommand command) => command is
        SeedAndroidCommand.Stop or SeedAndroidCommand.Logcat or
        SeedAndroidCommand.Pause or SeedAndroidCommand.Resume or SeedAndroidCommand.Screenshot or SeedAndroidCommand.Reload;

    /// <summary>
    /// 設定 JSON の値（無ければ既定値）の上にコマンドラインの指定を重ね、サブコマンドの目的を入れる。
    /// </summary>
    /// <param name="line">コマンドラインの指定。</param>
    /// <param name="config">設定 JSON の値（無ければ null）。</param>
    /// <returns>中核へ渡す指定。</returns>
    public static AndroidRunRequest ToRequest(SeedAndroidCommandLine line, AndroidRunRequest? config)
    {
        var baseline = config ?? new AndroidRunRequest();
        // プロジェクトとアセットフォルダは排他なので、どちらかをコマンドラインで指定したら設定 JSON の他方は使わない
        var cliChoosesSource = line.ProjectDir is not null || line.AssetsDir is not null;
        return baseline with
        {
            Goal            = GoalFor(line.Command),
            ProjectDir      = cliChoosesSource ? line.ProjectDir : baseline.ProjectDir,
            AssetsDir       = cliChoosesSource ? line.AssetsDir : baseline.AssetsDir,
            Serial          = line.Serial ?? baseline.Serial,
            Avd             = line.Avd ?? baseline.Avd,
            ScenePath       = line.ScenePath ?? baseline.ScenePath,
            Abis            = line.Abis ?? baseline.Abis,
            Release         = line.Release || baseline.Release,
            SkipNativeBuild = line.SkipNativeBuild || baseline.SkipNativeBuild,
            SkipGradle      = line.SkipGradle || baseline.SkipGradle,
            NoInstall       = line.NoInstall || baseline.NoInstall,
            NoLaunch        = line.NoLaunch || baseline.NoLaunch,
            NoLogcat        = line.NoLogcat || baseline.NoLogcat,
            PushScripts     = line.PushScripts || baseline.PushScripts,
            Rebuild         = line.Rebuild || baseline.Rebuild,
            LogcatSeconds   = line.LogcatSeconds ?? baseline.LogcatSeconds,
            LogFile         = line.LogFile ?? baseline.LogFile,
            IpcPort         = line.IpcPort ?? baseline.IpcPort,
        };
    }

    /// <summary>サブコマンド → 中核の目的（端末の操作だけのサブコマンドは Run として扱う。使われない）。</summary>
    /// <param name="command">サブコマンド。</param>
    /// <returns>目的。</returns>
    public static AndroidRunGoal GoalFor(SeedAndroidCommand command) => command switch
    {
        SeedAndroidCommand.Build   => AndroidRunGoal.Build,
        SeedAndroidCommand.Install => AndroidRunGoal.Install,
        SeedAndroidCommand.Push    => AndroidRunGoal.Push,
        _                          => AndroidRunGoal.Run,
    };

    /// <summary>エラーの解釈結果を作る。</summary>
    /// <param name="message">エラーの説明。</param>
    /// <returns>解釈結果。</returns>
    private static SeedAndroidParseResult Fail(string message) => new(null, ShowHelp: false, Error: message);
}
