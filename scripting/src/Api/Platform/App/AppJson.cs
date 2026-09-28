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
