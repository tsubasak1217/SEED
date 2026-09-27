using System;
using System.Text.Json;

namespace SEED.Platform;

/// <summary>
/// センサー（重力を除いた加速度。W1-8）。端末を振る・揺らすの判定に使う。
///
/// <para><b>仕組み</b><br/>
/// Android ではメインプロセスが SensorManager の TYPE_LINEAR_ACCELERATION（無い端末は加速度から低域通過で重力を引いた値）を
/// 専用のスレッドで受け、たまった標本を <see cref="Read"/> で返す（IPC なし。:seed_platform を起こさないので
/// <see cref="Platform.ErrorConnecting"/> にならない。権限・機能の opt-in は要らない）。標本ごとのイベントは流れない。
/// アプリが前面から外れると登録を外し（背面で電池を使わない）、戻ると同じ頻度で登録し直す（その間の <see cref="Read"/> は標本の数 0）。
/// デスクトップの模擬は PC にセンサーが無いので値は 0（<see cref="SimulateSample"/> で入れた標本だけ）。
/// </para>
///
/// <para><b>読み方</b><br/>
/// <see cref="Start"/> の後、毎フレーム <see cref="Read"/> する。<see cref="SensorSample.PeakMagnitude"/> と
/// <see cref="SensorSample.SampleCount"/> は前回の <see cref="Read"/> からの分なので、フレームの間に来た振りも落とさない。
/// 使い終わったら（画面を離れるとき）<see cref="Stop"/>。「振った」の判定（閾値・回数・時間）はアプリが決める（docs/scripting_api.md §7.13 の例）。
/// </para>
/// </summary>
public static class Sensors
{
    /// <summary><see cref="Start"/> の頻度の既定値（Hz。Android の SENSOR_DELAY_GAME＝20 ms と同じ）。</summary>
    public const int DefaultRateHz = 50;

    /// <summary><see cref="Start"/> の頻度の下限（Hz。これより小さいと false・<see cref="ErrorInvalidArgument"/>）。</summary>
    public const int MinRateHz = 1;

    /// <summary><see cref="Start"/> の頻度の上限（Hz。これより大きい値はこの値にそろえる。Android 12 以降のアプリの上限）。</summary>
    public const int MaxRateHz = 200;

    /// <summary><see cref="Platform.LastError"/>: 端末にその種類を出せるセンサーが無い（<see cref="Start"/>。以後 <see cref="IsSupported"/> は false）。</summary>
    public const string ErrorNotSupported = "not_supported";

    /// <summary><see cref="Platform.LastError"/>: <see cref="Start"/> していない種類を <see cref="Read"/> した（<see cref="Stop"/> の後も）。</summary>
    public const string ErrorNotStarted = "not_started";

    /// <summary><see cref="Platform.LastError"/>: OS がセンサーの登録を受け付けなかった（Android。<see cref="Start"/> をやり直せる）。</summary>
    public const string ErrorRegisterFailed = "register_failed";

    /// <summary><see cref="Platform.LastError"/>: 種類が約束に無い・頻度が下限より小さい。</summary>
    public const string ErrorInvalidArgument = "invalid_argument";

    /// <summary><see cref="GetSource"/>: 端末の重力を除いた加速度のセンサー（Android の TYPE_LINEAR_ACCELERATION）。</summary>
    public const string SourceLinearAcceleration = "linear_acceleration";

    /// <summary>
    /// <see cref="GetSource"/>: 加速度のセンサーから低域通過で重力を見積もって引いた値（上が無い端末の代わり。端末の向きを素早く変えた直後は
    /// 重力の差が一瞬だけ加速度に見える）。
    /// </summary>
    public const string SourceAccelerometerLowPass = "accelerometer_lowpass";

    /// <summary><see cref="GetSource"/>: デスクトップの模擬（値は <see cref="SimulateSample"/> で入れた標本だけ）。</summary>
    public const string SourceSimulated = "simulated";

    /// <summary>種類の数（種類ごとの控えの大きさ）。</summary>
    private static readonly int KindCount = Enum.GetValues<SensorKind>().Length;

    /// <summary>種類ごとの「使えないと分かった」（<see cref="Start"/> が <see cref="ErrorNotSupported"/> で失敗した後は true）。</summary>
    private static readonly bool[] _unsupported = new bool[KindCount];

    /// <summary>種類ごとの最後に成功した <see cref="Start"/> の出どころ（まだなら空文字）。</summary>
    private static readonly string[] _sources = CreateEmptySources();

    /// <summary>
    /// その種類が使えるか。IPC を通らない（毎フレーム読んでよい）: <see cref="Platform.IsSupported"/> で、<see cref="Start"/> が
    /// <see cref="ErrorNotSupported"/> で失敗していない（最初の <see cref="Start"/> の前は、端末にセンサーがあるかはまだ分からないので true）。
    /// デスクトップの模擬は true。
    /// </summary>
    /// <param name="kind">種類。</param>
    /// <returns>使える（見込み）なら true。</returns>
    public static bool IsSupported(SensorKind kind) => Platform.IsSupported && IsKnown(kind) && !_unsupported[(int)kind];

    /// <summary>
    /// 受け取りを始める（動いていれば、たまった標本を捨てて始め直す）。前面にいないとき（Android）は前面へ戻ったときに登録する。
    /// </summary>
    /// <param name="kind">種類。</param>
    /// <param name="rateHz">頻度（Hz。<see cref="MinRateHz"/>〜<see cref="MaxRateHz"/>。大きい値はそろえる）。OS への希望で、実際の標本の数は <see cref="SensorSample.SampleCount"/> で分かる。</param>
    /// <returns>受け付けたら true（false なら <see cref="Platform.LastError"/>）。</returns>
    public static bool Start(SensorKind kind, int rateHz = DefaultRateHz)
    {
        if (!IsKnown(kind))
        {
            Platform.LastError = ErrorInvalidArgument;
            return false;
        }
        bool ok = Platform.TryInvoke(SensorJson.Module, SensorJson.MethodStart, SensorJson.StartObject(kind, rateHz), out string reply);
        if (ok)
        {
            _unsupported[(int)kind] = false;
            _sources[(int)kind] = PermissionJson.ReadReplyString(reply, SensorJson.KeySource);
        }
        else if (Platform.LastError == ErrorNotSupported)
        {
            _unsupported[(int)kind] = true;
        }
        return ok;
    }

    /// <summary>受け取りを止める（動いていなくても true）。たまった標本は捨てる。</summary>
    /// <param name="kind">種類。</param>
    /// <returns>受け付けたら true（false なら <see cref="Platform.LastError"/>）。</returns>
    public static bool Stop(SensorKind kind)
    {
        if (!IsKnown(kind))
        {
            Platform.LastError = ErrorInvalidArgument;
            return false;
        }
        return Platform.TryInvoke(SensorJson.Module, SensorJson.MethodStop, SensorJson.KindObject(kind), out _);
    }

    /// <summary>
    /// 最新の標本と、前回の <see cref="Read"/> からの最大の大きさ・標本の数を読む（読むと最大と数は 0 から数え直す）。毎フレーム呼んでよい
    /// （Android でも IPC なし）。
    /// </summary>
    /// <param name="kind">種類。</param>
    /// <param name="sample">読んだ値（失敗なら既定値）。</param>
    /// <returns>読めたら true（false なら <see cref="Platform.LastError"/>。<see cref="Start"/> 前は <see cref="ErrorNotStarted"/>）。</returns>
    public static bool Read(SensorKind kind, out SensorSample sample)
    {
        sample = default;
        if (!IsKnown(kind))
        {
            Platform.LastError = ErrorInvalidArgument;
            return false;
        }
        if (!Platform.TryInvoke(SensorJson.Module, SensorJson.MethodRead, SensorJson.KindObject(kind), out string reply))
        {
            return false;
        }
        try
        {
            using JsonDocument document = JsonDocument.Parse(reply);
            sample = SensorSample.FromJson(document.RootElement);
            return true;
        }
        catch (JsonException)
        {
            Platform.LastError = Platform.ErrorInvalidReply;
            return false;
        }
    }

    /// <summary>
    /// 最後に成功した <see cref="Start"/> の値の出どころ（<see cref="SourceLinearAcceleration"/> / <see cref="SourceAccelerometerLowPass"/> /
    /// <see cref="SourceSimulated"/>。まだ始めていなければ空文字）。IPC を通らない。
    /// </summary>
    /// <param name="kind">種類。</param>
    /// <returns>出どころの名前。</returns>
    public static string GetSource(SensorKind kind) => IsKnown(kind) ? _sources[(int)kind] : string.Empty;

    /// <summary>
    /// 標本を 1 つ入れる（<b>デスクトップの模擬だけ</b>。PC で振りの判定を試すため。例: キーを押したら 15 m/s² の標本を入れる）。
    /// <see cref="Start"/> していなければ false（<see cref="ErrorNotStarted"/>）。Android では何もせず false（<see cref="Platform.LastError"/> == "unknown_method"）。
    /// </summary>
    /// <param name="kind">種類。</param>
    /// <param name="acceleration">標本（m/s²。有限の値）。</param>
    /// <returns>入れたら true。</returns>
    public static bool SimulateSample(SensorKind kind, Vector3 acceleration)
    {
        if (!IsKnown(kind) || !float.IsFinite(acceleration.x) || !float.IsFinite(acceleration.y) || !float.IsFinite(acceleration.z))
        {
            Platform.LastError = ErrorInvalidArgument;
            return false;
        }
        return Platform.TryInvoke(SensorJson.Module, SensorJson.MethodSimInject, SensorJson.InjectObject(kind, acceleration), out _);
    }

    /// <summary>約束の種類か（範囲外の値を型変換で渡されたときに送らない）。</summary>
    private static bool IsKnown(SensorKind kind) => (uint)kind < (uint)KindCount && SensorJson.KindName(kind).Length > 0;

    /// <summary>種類ごとの出どころの控えを空文字で作る。</summary>
    private static string[] CreateEmptySources()
    {
        var sources = new string[Enum.GetValues<SensorKind>().Length];
        Array.Fill(sources, string.Empty);
        return sources;
    }
}
