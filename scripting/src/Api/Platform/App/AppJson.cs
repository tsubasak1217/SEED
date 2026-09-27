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
}
