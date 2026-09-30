namespace SEED.Platform;

/// <summary>
/// OS の種類と版の問い合わせと控え（内部用。<see cref="App.Platform"/>・<see cref="App.OsVersion"/> の源。2026-10-01）。
///
/// <para>
/// OS の種類と版はプロセスの中で変わらない（Android の Build.VERSION.SDK_INT・デスクトップの模擬の環境変数）ので、最初に成功した
/// 返答を控えて以後は問い合わせない（毎フレーム読んでよい）。失敗（基盤が無い・返答が読めない）は控えず、次に読まれたときに問い直す。
/// SEEDScripting の static なので、スクリプトの読み直し（ホットリロード）でも控えは残る（値が変わらないので差し支えない）。
/// </para>
/// </summary>
internal static class AppOsInfo
{
    /// <summary>控えがあるか。</summary>
    private static bool _loaded;

    /// <summary>控えた OS の種類。</summary>
    private static PlatformKind _platform;

    /// <summary>控えた OS の版の番号。</summary>
    private static int _osVersion;

    /// <summary>
    /// OS の種類と版を返す（控えが無ければ問い合わせ、成功したら控える）。
    /// </summary>
    /// <param name="platform">OS の種類（失敗なら Unknown）。</param>
    /// <param name="osVersion">OS の版の番号（失敗なら 0）。</param>
    /// <returns>取れたら true（false なら <see cref="Platform.LastError"/>）。</returns>
    internal static bool TryGet(out PlatformKind platform, out int osVersion)
    {
        if (!_loaded && Load())
        {
            _loaded = true;
        }
        platform = _loaded ? _platform : PlatformKind.Unknown;
        osVersion = _loaded ? _osVersion : 0;
        return _loaded;
    }

    /// <summary>app.os_info を問い合わせて控えへ入れる。</summary>
    /// <returns>読めたら true。</returns>
    private static bool Load()
    {
        if (!Platform.TryInvoke(AppJson.Module, AppJson.MethodOsInfo, PlatformJson.StringObject(), out string reply))
        {
            return false;
        }
        if (!AppJson.TryReadOsInfo(reply, out PlatformKind platform, out int osVersion))
        {
            Platform.LastError = Platform.ErrorInvalidReply;
            return false;
        }
        _platform = platform;
        _osVersion = osVersion;
        return true;
    }
}
