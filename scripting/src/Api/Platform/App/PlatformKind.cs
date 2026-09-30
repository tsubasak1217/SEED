namespace SEED.Platform;

/// <summary>
/// 動いている OS の種類（<see cref="App.Platform"/>。2026-10-01）。
///
/// <para>
/// Android の端末では <see cref="Android"/>。デスクトップ（エディタの Play・単体起動）は SEED.Platform の模擬が動いているホストの OS
/// （<see cref="Windows"/> など。<see cref="Platform.IsSimulated"/> は true）。OS の版で分けるときは <see cref="App.OsVersion"/> を比べる。
/// </para>
/// </summary>
public enum PlatformKind
{
    /// <summary>分からない（基盤が無い・返答が読めない・知らない OS）。</summary>
    Unknown,

    /// <summary>"android": Android の端末。</summary>
    Android,

    /// <summary>"windows": Windows（デスクトップの模擬）。</summary>
    Windows,

    /// <summary>"macos": macOS（デスクトップの模擬）。</summary>
    MacOS,

    /// <summary>"linux": Linux（デスクトップの模擬）。</summary>
    Linux,
}
