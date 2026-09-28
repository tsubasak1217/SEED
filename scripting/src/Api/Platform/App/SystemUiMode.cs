namespace SEED.Platform;

/// <summary>
/// 端末の明暗の設定（<see cref="App.UiMode"/>・イベント <see cref="App.UiModeChangedEvent"/>。W2-9）。
/// Android は Configuration.uiMode の夜の bit（UI_MODE_NIGHT_YES / NO / UNDEFINED）、PC は OS の「既定のアプリ モード」
/// （Windows のレジストリの AppsUseLightTheme）。
/// </summary>
public enum SystemUiMode
{
    /// <summary>取れない（Android の UNDEFINED・PC で設定が無い・基盤が無い）。</summary>
    Unknown = 0,
    /// <summary>明るい（夜の表示でない）。</summary>
    Light = 1,
    /// <summary>暗い（夜の表示・ダークモード）。</summary>
    Dark = 2,
}
