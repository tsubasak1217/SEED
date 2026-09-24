// ============================================================
//  AndroidToolchain.cs — Android のビルドに使う道具（SDK / NDK / adb / JDK / cargo / dotnet）の場所
//
//  【探し方（環境変数 → 既定の場所）】
//    Android SDK … ANDROID_SDK_ROOT → ANDROID_HOME → %LOCALAPPDATA%\Android\Sdk
//    Android NDK … ANDROID_NDK_HOME（source.properties があること）→ SDK の ndk\ の最新版（その旨を知らせる）
//    adb         … SDK の platform-tools\adb.exe
//    JDK         … JAVA_HOME（bin\java.exe があること）→ Android Studio 同梱の JBR（%ProgramFiles%\Android\Android Studio\jbr）
//    cargo       … PATH → CARGO_HOME\bin → %USERPROFILE%\.cargo\bin
//    dotnet      … DOTNET_HOST_PATH（dotnet から起動されたとき）→ PATH → %ProgramFiles%\dotnet
//  マシン固有のパスはリポジトリに書かない（従来の build_and_run.ps1 と同じ方針）。
//  見つからない道具は、その道具が要る工程に入ったときに「何をすればよいか」付きのエラーにする
//  （端末の一覧だけなら adb しか要らない、など）。
//
//  【NDK のパスの表記】
//  同じ NDK でもパスの表記（/ と \）が違うと cc 系の依存（oboe-sys 等）が作り直される。
//  ここで決めた表記（Path.GetFullPath の結果）を cargo ndk の ANDROID_NDK_HOME と Gradle の -Pseed.ndkPath の両方に使う。
//
//  WPF に依存しない（コンソールツール・単体テストからリンクされる）。
// ============================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SEEDEditor.Android.Pipeline;

namespace SEEDEditor.Android.Toolchain;

/// <summary>Android のビルドに使う道具の場所（見つからないものは理由を持つ）。</summary>
public sealed class AndroidToolchain
{
    // ── 環境変数の名前 ──────────────────────────────────────

    /// <summary>Android SDK（第 1 候補）。</summary>
    public const string SdkRootVariable = "ANDROID_SDK_ROOT";

    /// <summary>Android SDK（第 2 候補。AGP もこれを読む）。</summary>
    public const string SdkHomeVariable = "ANDROID_HOME";

    /// <summary>Android NDK。cargo ndk もこれを読む。</summary>
    public const string NdkHomeVariable = "ANDROID_NDK_HOME";

    /// <summary>JDK（gradlew が読む）。</summary>
    public const string JavaHomeVariable = "JAVA_HOME";

    /// <summary>cargo の置き場（rustup の既定は %USERPROFILE%\.cargo）。</summary>
    private const string CargoHomeVariable = "CARGO_HOME";

    /// <summary>dotnet CLI から起動された子プロセスに dotnet.exe の場所を知らせる変数。</summary>
    private const string DotnetHostPathVariable = "DOTNET_HOST_PATH";

    /// <summary>実行ファイルを探す PATH。</summary>
    private const string PathVariable = "PATH";

    /// <summary>%LOCALAPPDATA%。</summary>
    private const string LocalAppDataVariable = "LOCALAPPDATA";

    /// <summary>%ProgramFiles%。</summary>
    private const string ProgramFilesVariable = "ProgramFiles";

    /// <summary>%USERPROFILE%。</summary>
    private const string UserProfileVariable = "USERPROFILE";

    // ── 既定の場所（相対）─────────────────────────────────────

    /// <summary>Android Studio が SDK を入れる既定の場所（%LOCALAPPDATA% からの相対）。</summary>
    private static readonly string DefaultSdkRelative = Path.Combine("Android", "Sdk");

    /// <summary>Android Studio 同梱の JBR（%ProgramFiles% からの相対）。</summary>
    private static readonly string DefaultJbrRelative = Path.Combine("Android", "Android Studio", "jbr");

    /// <summary>rustup が cargo を入れる既定の場所（%USERPROFILE% からの相対）。</summary>
    private static readonly string DefaultCargoBinRelative = Path.Combine(".cargo", "bin");

    /// <summary>dotnet の既定のインストール先（%ProgramFiles% からの相対）。</summary>
    private const string DefaultDotnetDirName = "dotnet";

    /// <summary>NDK のフォルダであることの目印。</summary>
    private const string NdkMarkerFileName = "source.properties";

    // ── 解決結果 ───────────────────────────────────────────

    /// <summary>1 つの道具の解決結果（場所か、見つからない理由）。</summary>
    /// <param name="Path">場所（見つからなければ null）。</param>
    /// <param name="Problem">見つからない理由と対処（見つかれば null）。</param>
    /// <param name="Note">既定の場所を使った等の知らせ（無ければ null）。</param>
    private sealed record Resolved(string? Path, string? Problem, string? Note = null);

    private readonly Resolved _sdk;
    private readonly Resolved _ndk;
    private readonly Resolved _adb;
    private readonly Resolved _javaHome;
    private readonly Resolved _cargo;
    private readonly Resolved _dotnet;

    private AndroidToolchain(Resolved sdk, Resolved ndk, Resolved adb, Resolved javaHome, Resolved cargo, Resolved dotnet)
    {
        _sdk = sdk;
        _ndk = ndk;
        _adb = adb;
        _javaHome = javaHome;
        _cargo = cargo;
        _dotnet = dotnet;
    }

    /// <summary>
    /// 道具の場所を探す（ファイルの有無だけを見る。プロセスは起動しない）。
    /// </summary>
    /// <param name="getEnvironmentVariable">環境変数の読み口（null なら実際の環境変数。単体テストで差し替える）。</param>
    /// <returns>解決結果。</returns>
    public static AndroidToolchain Detect(Func<string, string?>? getEnvironmentVariable = null)
    {
        var env = getEnvironmentVariable ?? Environment.GetEnvironmentVariable;
        var sdk = ResolveSdk(env);
        return new AndroidToolchain(
            sdk,
            ResolveNdk(env, sdk.Path),
            ResolveAdb(sdk),
            ResolveJavaHome(env),
            ResolveCargo(env),
            ResolveDotnet(env));
    }

    /// <summary>既定の場所を使った等の知らせ（見つかった道具のぶんだけ）。</summary>
    public IReadOnlyList<string> Notes =>
        new[] { _sdk, _ndk, _adb, _javaHome, _cargo, _dotnet }
            .Where(r => r.Path is not null && r.Note is not null)
            .Select(r => r.Note!)
            .ToList();

    /// <summary>Android SDK のフォルダ（無ければ例外）。</summary>
    /// <returns>SDK のフォルダ。</returns>
    public string RequireSdk() => Require(_sdk);

    /// <summary>Android NDK のフォルダ（無ければ例外）。</summary>
    /// <returns>NDK のフォルダ。</returns>
    public string RequireNdk() => Require(_ndk);

    /// <summary>adb.exe（無ければ例外）。</summary>
    /// <returns>adb の絶対パス。</returns>
    public string RequireAdb() => Require(_adb);

    /// <summary>JDK のフォルダ（無ければ例外）。</summary>
    /// <returns>JAVA_HOME に渡すフォルダ。</returns>
    public string RequireJavaHome() => Require(_javaHome);

    /// <summary>cargo.exe（無ければ例外）。</summary>
    /// <returns>cargo の絶対パス。</returns>
    public string RequireCargo() => Require(_cargo);

    /// <summary>dotnet.exe（無ければ例外）。</summary>
    /// <returns>dotnet の絶対パス。</returns>
    public string RequireDotnet() => Require(_dotnet);

    /// <summary>見つかった場所を返すか、理由付きの例外を投げる。</summary>
    /// <param name="resolved">解決結果。</param>
    /// <returns>場所。</returns>
    private static string Require(Resolved resolved) =>
        resolved.Path ?? throw new AndroidPipelineException(AndroidFailureKind.Toolchain, resolved.Problem ?? "道具が見つかりません。");

    // ── 個々の道具 ──────────────────────────────────────────

    /// <summary>Android SDK を探す。</summary>
    private static Resolved ResolveSdk(Func<string, string?> env)
    {
        foreach (var variable in new[] { SdkRootVariable, SdkHomeVariable })
        {
            var value = env(variable);
            if (string.IsNullOrWhiteSpace(value)) continue;
            return Directory.Exists(value)
                ? new Resolved(Normalize(value), null)
                : new Resolved(null, $"環境変数 {variable}={value} のフォルダがありません。Android SDK のフォルダを設定してください（例: %LOCALAPPDATA%\\Android\\Sdk）。");
        }
        var localAppData = env(LocalAppDataVariable);
        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            var candidate = Path.Combine(localAppData, DefaultSdkRelative);
            if (Directory.Exists(candidate))
            {
                return new Resolved(Normalize(candidate), null, $"Android SDK は既定の場所を使います: {Normalize(candidate)}（{SdkRootVariable} / {SdkHomeVariable} が未設定）");
            }
        }
        return new Resolved(null,
            $"Android SDK が見つかりません。環境変数 {SdkRootVariable}（または {SdkHomeVariable}）に SDK のフォルダを設定するか、Android Studio で SDK を入れてください（既定 %LOCALAPPDATA%\\Android\\Sdk）。");
    }

    /// <summary>Android NDK を探す（環境変数 → SDK の ndk\ の最新版）。</summary>
    private static Resolved ResolveNdk(Func<string, string?> env, string? sdk)
    {
        var fromEnv = env(NdkHomeVariable);
        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            return File.Exists(Path.Combine(fromEnv, NdkMarkerFileName))
                ? new Resolved(Normalize(fromEnv), null)
                : new Resolved(null, $"{NdkHomeVariable}={fromEnv} は NDK のフォルダではありません（{NdkMarkerFileName} がありません）。");
        }
        if (sdk is not null)
        {
            var ndkParent = Path.Combine(sdk, "ndk");
            var latest = Directory.Exists(ndkParent)
                ? Directory.EnumerateDirectories(ndkParent)
                    .Where(dir => File.Exists(Path.Combine(dir, NdkMarkerFileName)))
                    .OrderByDescending(dir => ParseVersion(Path.GetFileName(dir)))
                    .FirstOrDefault()
                : null;
            if (latest is not null)
            {
                return new Resolved(Normalize(latest), null, $"{NdkHomeVariable} が未設定のため SDK 内の最新 NDK を使います: {Normalize(latest)}");
            }
        }
        return new Resolved(null,
            $"Android NDK が見つかりません。環境変数 {NdkHomeVariable} に NDK（r28 以降）のフォルダを設定するか、Android Studio の SDK Manager で NDK を入れてください。");
    }

    /// <summary>adb を探す（SDK の platform-tools）。</summary>
    private static Resolved ResolveAdb(Resolved sdk)
    {
        if (sdk.Path is null) return new Resolved(null, sdk.Problem);
        var adb = Path.Combine(sdk.Path, "platform-tools", "adb.exe");
        return File.Exists(adb)
            ? new Resolved(adb, null)
            : new Resolved(null, $"adb が見つかりません: {adb}（SDK Manager で Platform-Tools を入れてください）");
    }

    /// <summary>JDK を探す（JAVA_HOME → Android Studio 同梱の JBR）。</summary>
    private static Resolved ResolveJavaHome(Func<string, string?> env)
    {
        var fromEnv = env(JavaHomeVariable);
        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            return File.Exists(Path.Combine(fromEnv, "bin", "java.exe"))
                ? new Resolved(Normalize(fromEnv), null)
                : new Resolved(null, $"{JavaHomeVariable}={fromEnv} は JDK を指していません（bin\\java.exe がありません）。Android Studio 同梱の JBR（例: C:\\Program Files\\Android\\Android Studio\\jbr）を設定してください。");
        }
        var programFiles = env(ProgramFilesVariable);
        if (!string.IsNullOrWhiteSpace(programFiles))
        {
            var jbr = Path.Combine(programFiles, DefaultJbrRelative);
            if (File.Exists(Path.Combine(jbr, "bin", "java.exe")))
            {
                return new Resolved(Normalize(jbr), null, $"JDK は Android Studio 同梱の JBR を使います: {Normalize(jbr)}（{JavaHomeVariable} が未設定）");
            }
        }
        return new Resolved(null,
            $"JDK が見つかりません。環境変数 {JavaHomeVariable} に Android Studio 同梱の JBR（例: C:\\Program Files\\Android\\Android Studio\\jbr）を設定してください。");
    }

    /// <summary>cargo を探す（PATH → CARGO_HOME → %USERPROFILE%\.cargo\bin）。</summary>
    private static Resolved ResolveCargo(Func<string, string?> env)
    {
        var found = FindOnPath(env, "cargo.exe");
        if (found is not null) return new Resolved(found, null);
        var candidates = new[]
        {
            env(CargoHomeVariable) is { Length: > 0 } cargoHome ? Path.Combine(cargoHome, "bin", "cargo.exe") : null,
            env(UserProfileVariable) is { Length: > 0 } profile ? Path.Combine(profile, DefaultCargoBinRelative, "cargo.exe") : null,
        };
        var existing = candidates.FirstOrDefault(c => c is not null && File.Exists(c));
        return existing is not null
            ? new Resolved(Normalize(existing), null)
            : new Resolved(null, "cargo が見つかりません。Rust（rustup）を入れてください。");
    }

    /// <summary>dotnet を探す（DOTNET_HOST_PATH → PATH → %ProgramFiles%\dotnet）。</summary>
    private static Resolved ResolveDotnet(Func<string, string?> env)
    {
        var hostPath = env(DotnetHostPathVariable);
        if (!string.IsNullOrWhiteSpace(hostPath) && File.Exists(hostPath)) return new Resolved(Normalize(hostPath), null);
        var found = FindOnPath(env, "dotnet.exe");
        if (found is not null) return new Resolved(found, null);
        var programFiles = env(ProgramFilesVariable);
        if (!string.IsNullOrWhiteSpace(programFiles))
        {
            var candidate = Path.Combine(programFiles, DefaultDotnetDirName, "dotnet.exe");
            if (File.Exists(candidate)) return new Resolved(Normalize(candidate), null);
        }
        return new Resolved(null, "dotnet が見つかりません。.NET 10 SDK を入れてください（SeedPak と同梱 .NET の取り寄せに使います）。");
    }

    /// <summary>PATH のフォルダから実行ファイルを探す。</summary>
    /// <param name="env">環境変数の読み口。</param>
    /// <param name="fileName">探すファイル名。</param>
    /// <returns>見つかった絶対パス（無ければ null）。</returns>
    private static string? FindOnPath(Func<string, string?> env, string fileName)
    {
        foreach (var dir in (env(PathVariable) ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim('"'), fileName);
                if (File.Exists(candidate)) return Normalize(candidate);
            }
            catch (ArgumentException)
            {
                // PATH に壊れた要素があっても他を探す
            }
        }
        return null;
    }

    /// <summary>パスの表記を揃える（絶対パス・末尾の区切り無し）。</summary>
    /// <param name="path">パス。</param>
    /// <returns>揃えたパス。</returns>
    private static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    /// <summary>版のフォルダ名を比べられる形にする（読めなければ最小）。</summary>
    /// <param name="name">フォルダ名（例 28.2.13676358）。</param>
    /// <returns>版。</returns>
    private static Version ParseVersion(string name) =>
        Version.TryParse(name, out var version) ? version : new Version(0, 0);
}
