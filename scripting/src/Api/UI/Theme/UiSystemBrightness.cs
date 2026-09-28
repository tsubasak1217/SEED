using SEED.Platform;

namespace SEED.UI;

// ============================================================
//  UiSystemBrightness.cs — 端末の明暗（UiTheme の System の選び方の源。W2-9。docs/ui_theme.md §4）
//
//  SEED.Platform の app.ui_mode（Android は Configuration.uiMode の夜の bit・PC は OS の「アプリのモード」）を問い合わせ、
//  変化のイベント platform.ui_mode_changed（Android の onConfigurationChanged・PC のウィンドウの ThemeChanged・模擬の命令）を
//  受けて UiTheme へ知らせる。問い合わせは選び方が System になったときだけ（使わないアプリは基盤を呼ばない）。
//  イベントの受け口はエンジンの受け口（PlatformEvents.AddEngineListener。スクリプトの読み直しで外れない）に 1 度だけ付ける。
// ============================================================

/// <summary>端末の明暗。</summary>
internal static class UiSystemBrightness
{
    /// <summary>ログの接頭辞。</summary>
    private const string LogPrefix = "[SEED.UI]";

    /// <summary>イベントの受け口を付けたか（プロセスで 1 度）。</summary>
    private static bool _listening;
    /// <summary>今の読み直しの間に問い合わせたか。</summary>
    private static bool _queried;

    /// <summary>分かっている端末の明暗（取れない・まだ問い合わせていなければ null）。</summary>
    public static UiBrightness? Known { get; private set; }

    /// <summary>問い合わせて、変化のイベントを受け始める（2 度目からは何もしない）。</summary>
    public static void Observe()
    {
        if (!_listening)
        {
            _listening = true;
            PlatformEvents.AddEngineListener(App.UiModeChangedEvent, OnEvent);
        }
        if (_queried) return;
        _queried = true;
        Known = ToBrightness(App.UiMode);
        Debug.Log($"{LogPrefix} 端末の明暗: {(Known is { } b ? UiBrightnessRules.ToWord(b) : "不明（テーマの明暗のまま）")}");
    }

    /// <summary>スクリプトの読み直し: 問い合わせ直す（受け口は付けたまま）。</summary>
    public static void Reset()
    {
        _queried = false;
        Known = null;
    }

    /// <summary>変化のイベント（platform.ui_mode_changed）。</summary>
    private static void OnEvent(string json)
    {
        if (!App.TryParseUiModeEvent(json, out var mode)) return;
        var next = ToBrightness(mode);
        if (_queried && next == Known) return;
        _queried = true;
        Known = next;
        Debug.Log($"{LogPrefix} 端末の明暗が変わりました: {(next is { } b ? UiBrightnessRules.ToWord(b) : "不明")}");
        UiTheme.OnSystemBrightnessChanged();
    }

    /// <summary>基盤の明暗 → UI の明暗（不明は null）。</summary>
    private static UiBrightness? ToBrightness(SystemUiMode mode) => mode switch
    {
        SystemUiMode.Dark => UiBrightness.Dark,
        SystemUiMode.Light => UiBrightness.Light,
        _ => null,
    };
}
