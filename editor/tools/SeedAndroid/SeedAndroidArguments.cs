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
    };

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
          stop      アプリを止める（am force-stop）
          logcat    logcat を流す（タグ SEED・DOTNET ほか。Ctrl+C で止める）

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
                                    pak に無ければ端末が警告を出して開始シーンで起動する）
          --abi <ABI[,ABI]>         arm64-v8a / x86_64（省略時は端末から判定。端末が無ければ両方）
          --release                 Rust 側を --release でビルドする（APK はデバッグ署名のまま）
          --config <JSON>           指定をまとめた設定 JSON（キーは project / assets_dir / serial / emulator_fallback / avd /
                                    scene / abis / release / skip_rust_build / skip_gradle / no_install / no_launch / no_logcat /
                                    push_scripts / rebuild / logcat_seconds / log_file。project・assets_dir・log_file の相対パスは
                                    JSON のフォルダから、scene はアセットルートから。コマンドラインが優先）
          --skip-rust               libSEED.so のビルドを飛ばす
          --skip-gradle             APK の作成（pak とスクリプト・同梱 .NET・Gradle）を飛ばす
          --no-install / --no-launch / --no-logcat   インストール / 起動 / logcat を飛ばす
          --push-scripts            run でもスクリプトの DLL を作り直して端末の files/bin/ へ送る
          --rebuild                 変更の有無で工程を自動で飛ばさない（すべて作り直し、入れ直す）
          --logcat-seconds <秒>     logcat を流す秒数（0 か省略で止めるまで）
          --log-file <パス>         logcat の保存先（UTF-8）
          --app-id <ID>             stop で止めるアプリ（省略時は --project の設定から。無ければ com.seedengine.runtime）
          --since <時刻>            logcat の起点（端末の時刻 "MM-dd HH:mm:ss.fff"。省略時は今）
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

            // 以降のオプションはすべて値を 1 つ取る
            if (arg is not (ConfigOption or ProjectOption or AssetsDirOption or SerialOption or AbiOption or LogcatSecondsOption
                or LogFileOption or ApplicationIdOption or SinceOption or AvdOption or SceneOption))
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
        if (AndroidDeviceTarget.IsAutoSerial(line.Serial) && (command is SeedAndroidCommand.Stop or SeedAndroidCommand.Logcat))
        {
            return Fail($"{SerialOption} {AndroidDeviceTarget.AutoSerial} は build / install / run / push で使えます（stop / logcat にはシリアルを指定してください）");
        }
        return new SeedAndroidParseResult(line, ShowHelp: false, Error: null);
    }

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
