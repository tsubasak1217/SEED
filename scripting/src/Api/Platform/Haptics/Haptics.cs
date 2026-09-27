namespace SEED.Platform;

/// <summary>
/// 触感（UI の押した感じ・ゲームの振動。W1-6）。
///
/// <para>
/// Android ではメインプロセスが端末の振動子を鳴らして答える（IPC なし・同期）。<see cref="Tap"/> は端末が用意した「クリック」の触感
/// （VibrationEffect.EFFECT_CLICK）、<see cref="Vibrate"/> は決まった長さの振動（上限 <see cref="MaxVibrateMilliseconds"/> ミリ秒）。
/// Android 13 以降は種類（Tap はタッチの触感、Vibrate はメディア・ゲームの振動）が付き、利用者の「振動と触感」の設定がそれぞれに効く
/// （切られていれば受け付けても振動せず、戻り値は true のまま）。権限 VIBRATE は APK に常設（機能の opt-in は要らない）。振動子の無い端末では
/// false（<see cref="Platform.LastError"/> == <see cref="ErrorNoVibrator"/>）。
/// デスクトップの模擬は振動せず、受け付けて回数を記録しログに残すだけ。
/// </para>
/// </summary>
public static class Haptics
{
    /// <summary>触感のモジュール。</summary>
    private const string Module = "haptics";

    /// <summary>軽いクリックの触感。</summary>
    private const string MethodTap = "tap";

    /// <summary>決まった長さの振動。</summary>
    private const string MethodVibrate = "vibrate";

    /// <summary>vibrate の引数: 長さ（ミリ秒）。</summary>
    private const string KeyMilliseconds = "ms";

    /// <summary><see cref="Vibrate"/> の長さの下限（ミリ秒。これより短いと false・<see cref="ErrorInvalidArgument"/>）。</summary>
    public const int MinVibrateMilliseconds = 1;

    /// <summary><see cref="Vibrate"/> の長さの上限（ミリ秒。これより長い値はこの長さにそろえる）。</summary>
    public const int MaxVibrateMilliseconds = 5000;

    /// <summary><see cref="Platform.LastError"/>: 端末に振動子が無い（Android）。</summary>
    public const string ErrorNoVibrator = "no_vibrator";

    /// <summary><see cref="Platform.LastError"/>: 長さが下限より短い（<see cref="Vibrate"/>）。</summary>
    public const string ErrorInvalidArgument = "invalid_argument";

    /// <summary>
    /// 軽いクリックの触感を鳴らす（ボタンを押したときなど）。
    /// </summary>
    /// <returns>受け付けたら true（false なら <see cref="Platform.LastError"/>）。</returns>
    public static bool Tap() => Platform.TryInvoke(Module, MethodTap, PlatformJson.StringObject(), out _);

    /// <summary>
    /// 決まった長さだけ振動する（<see cref="MaxVibrateMilliseconds"/> を超える長さはそろえる。長く鳴らし続ける用途は目覚ましの
    /// <see cref="Alarms"/> を使う）。
    /// </summary>
    /// <param name="milliseconds">長さ（ミリ秒。<see cref="MinVibrateMilliseconds"/> 以上）。</param>
    /// <returns>受け付けたら true（false なら <see cref="Platform.LastError"/>）。</returns>
    public static bool Vibrate(int milliseconds) =>
        Platform.TryInvoke(Module, MethodVibrate, PlatformJson.Int32Object(KeyMilliseconds, milliseconds), out _);
}
