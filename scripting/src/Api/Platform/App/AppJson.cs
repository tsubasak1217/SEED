namespace SEED.Platform;

/// <summary>
/// アプリの命令（<see cref="App.MoveTaskToBack"/>・<see cref="App.OpenUrl"/>・<see cref="App.OpenAppSettings"/>）の JSON の名前（内部用。W1-6）。
///
/// 名前の正典は Rust 側 runtime/src/engine/platform/bridge/wire.rs の <c>wire::app</c>（Java 側は PlatformContract.java の
/// <c>*APP*</c>）。値を変えるときは 3 か所を必ず揃える。
/// </summary>
internal static class AppJson
{
    /// <summary>アプリのモジュール。</summary>
    internal const string Module = "app";

    /// <summary>閉じずに背面へ。</summary>
    internal const string MethodMoveTaskToBack = "move_task_to_back";

    /// <summary>URL を開く。</summary>
    internal const string MethodOpenUrl = "open_url";

    /// <summary>端末の「アプリ情報」の画面を開く。</summary>
    internal const string MethodOpenAppSettings = "open_app_settings";

    /// <summary>open_url の引数: 開く URL。</summary>
    internal const string KeyUrl = "url";

    /// <summary>端末の明暗の設定（W2-9）。返答 { night }。</summary>
    internal const string MethodUiMode = "ui_mode";

    /// <summary>模擬だけ: 端末の明暗を差し替える（W2-9）。引数 { night }（"system" で OS の設定へ戻す）。</summary>
    internal const string MethodSimSetUiMode = "sim_set_ui_mode";

    /// <summary>ui_mode の返答・イベントの data・sim_set_ui_mode の引数: 夜の表示か。</summary>
    internal const string KeyNight = "night";

    /// <summary>night: 夜の表示（暗い）。</summary>
    internal const string NightYes = "yes";

    /// <summary>night: 夜の表示でない（明るい）。</summary>
    internal const string NightNo = "no";

    /// <summary>night: 取れない。</summary>
    internal const string NightUnknown = "unknown";

    /// <summary>sim_set_ui_mode の night: 差し替えをやめて OS の設定へ戻す。</summary>
    internal const string NightSystem = "system";

    // ── OS の種類と版（2026-10-01）──

    /// <summary>OS の種類と版。返答 { platform, os_version }。</summary>
    internal const string MethodOsInfo = "os_info";

    /// <summary>os_info の返答: OS の種類。</summary>
    internal const string KeyPlatform = "platform";

    /// <summary>os_info の返答: OS の版の番号（Android は API レベル）。</summary>
    internal const string KeyOsVersion = "os_version";

    /// <summary>platform: Android。</summary>
    internal const string PlatformAndroid = "android";

    /// <summary>platform: Windows。</summary>
    internal const string PlatformWindows = "windows";

    /// <summary>platform: macOS。</summary>
    internal const string PlatformMacOS = "macos";

    /// <summary>platform: Linux。</summary>
    internal const string PlatformLinux = "linux";

    // ── 前面・背面の知らせ（2026-10-01）──

    /// <summary>前面へ戻った。data { count, background_ms }。</summary>
    internal const string EventResumed = "platform.resumed";

    /// <summary>前面を離れた。data { count }。</summary>
    internal const string EventPaused = "platform.paused";

    /// <summary>resumed / paused の data: 何回目か。</summary>
    internal const string KeyLifecycleCount = "count";

    /// <summary>resumed の data: 背面にいた時間（ミリ秒）。</summary>
    internal const string KeyBackgroundMs = "background_ms";

    /// <summary>模擬だけ: 前面・背面の出入りを起こす。引数 { phase }。返答 { phase, changed }。</summary>
    internal const string MethodSimLifecycle = "sim_lifecycle";

    /// <summary>sim_lifecycle の引数: 段階（<see cref="LifecycleResumed"/> / <see cref="LifecyclePaused"/>）。</summary>
    internal const string KeyPhase = "phase";

    /// <summary>phase: 前面へ戻る。</summary>
    internal const string LifecycleResumed = "resumed";

    /// <summary>phase: 前面を離れる。</summary>
    internal const string LifecyclePaused = "paused";

    /// <summary>platform の語 → OS の種類（知らない語は Unknown）。</summary>
    internal static PlatformKind ToPlatformKind(string? platform) => platform switch
    {
        PlatformAndroid => PlatformKind.Android,
        PlatformWindows => PlatformKind.Windows,
        PlatformMacOS => PlatformKind.MacOS,
        PlatformLinux => PlatformKind.Linux,
        _ => PlatformKind.Unknown,
    };

    /// <summary>段階 → sim_lifecycle の phase の語。</summary>
    internal static string ToPhaseWord(AppLifecyclePhase phase) => phase == AppLifecyclePhase.Paused ? LifecyclePaused : LifecycleResumed;

    /// <summary>イベントの名前 → 段階（前面・背面のイベントでなければ false）。</summary>
    internal static bool TryPhaseOfLifecycleEvent(string name, out AppLifecyclePhase phase)
    {
        switch (name)
        {
            case EventResumed:
                phase = AppLifecyclePhase.Resumed;
                return true;
            case EventPaused:
                phase = AppLifecyclePhase.Paused;
                return true;
            default:
                phase = AppLifecyclePhase.Resumed;
                return false;
        }
    }

    /// <summary>
    /// os_info の返答を読む（platform が文字列でない・os_version が 0 以上の int でなければ false）。
    /// </summary>
    /// <param name="reply">返答の JSON。</param>
    /// <param name="platform">OS の種類（知らない語は Unknown のまま true）。</param>
    /// <param name="osVersion">OS の版の番号。</param>
    /// <returns>読めたら true。</returns>
    internal static bool TryReadOsInfo(string reply, out PlatformKind platform, out int osVersion)
    {
        platform = PlatformKind.Unknown;
        osVersion = 0;
        try
        {
            using System.Text.Json.JsonDocument document = System.Text.Json.JsonDocument.Parse(reply);
            var root = document.RootElement;
            if (root.ValueKind != System.Text.Json.JsonValueKind.Object
                || !root.TryGetProperty(KeyPlatform, out var platformElement) || platformElement.ValueKind != System.Text.Json.JsonValueKind.String
                || !root.TryGetProperty(KeyOsVersion, out var versionElement) || versionElement.ValueKind != System.Text.Json.JsonValueKind.Number
                || !versionElement.TryGetInt32(out int version) || version < 0)
            {
                return false;
            }
            platform = ToPlatformKind(platformElement.GetString());
            osVersion = version;
            return true;
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }
    }

    /// <summary>night の語 → 明暗。</summary>
    internal static SystemUiMode ToUiMode(string? night) => night switch
    {
        NightYes => SystemUiMode.Dark,
        NightNo => SystemUiMode.Light,
        _ => SystemUiMode.Unknown,
    };

    /// <summary>明暗 → night の語。</summary>
    internal static string ToNight(SystemUiMode mode) => mode switch
    {
        SystemUiMode.Dark => NightYes,
        SystemUiMode.Light => NightNo,
        _ => NightUnknown,
    };

    /// <summary>返答の最上位の night を読む（無い・読めなければ Unknown）。</summary>
    internal static SystemUiMode ReadNight(string reply)
    {
        try
        {
            using System.Text.Json.JsonDocument document = System.Text.Json.JsonDocument.Parse(reply);
            var root = document.RootElement;
            return root.ValueKind == System.Text.Json.JsonValueKind.Object ? ToUiMode(AlarmJson.GetString(root, KeyNight)) : SystemUiMode.Unknown;
        }
        catch (System.Text.Json.JsonException)
        {
            return SystemUiMode.Unknown;
        }
    }
}
