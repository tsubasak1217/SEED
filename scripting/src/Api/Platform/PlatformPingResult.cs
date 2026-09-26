using System.Globalization;

namespace SEED.Platform;

/// <summary>
/// <see cref="PlatformDiagnostics.Ping"/> の結果（不変値型）。
/// </summary>
public readonly struct PlatformPingResult
{
    /// <summary>往復できたか（返答の ok が true で、送った nonce がそのまま返ってきた）。</summary>
    public bool Ok { get; }

    /// <summary>
    /// 往復の時間（ミリ秒。スクリプト → エンジン → JNI → Java → :seed_platform → 戻り）。
    /// 失敗したときも、失敗の返答が戻るまでの時間が入る。
    /// </summary>
    public double RoundTripMs { get; }

    /// <summary>答えたプロセスの pid（Android は :seed_platform、デスクトップの模擬は SEED.exe 自身）。失敗なら 0。</summary>
    public int ServicePid { get; }

    /// <summary>答えたプロセスの起動からの経過ミリ秒（デスクトップの模擬は模擬を作ってから）。失敗なら 0。</summary>
    public long ServiceUptimeMs { get; }

    /// <summary>デスクトップの模擬が答えたか。</summary>
    public bool Simulated { get; }

    /// <summary>失敗の理由（成功なら空文字。<see cref="Platform.LastError"/> と同じ名前か "echo_mismatch"）。</summary>
    public string Error { get; }

    /// <summary>結果を作る。</summary>
    internal PlatformPingResult(bool ok, double roundTripMs, int servicePid, long serviceUptimeMs, bool simulated, string error)
    {
        Ok = ok;
        RoundTripMs = roundTripMs;
        ServicePid = servicePid;
        ServiceUptimeMs = serviceUptimeMs;
        Simulated = simulated;
        Error = error;
    }

    /// <summary>ログ向けの 1 行（例 "ok 0.52 ms pid=12345 uptime=3021 ms"）。</summary>
    public override string ToString()
    {
        string rtt = RoundTripMs.ToString("0.###", CultureInfo.InvariantCulture);
        return Ok
            ? $"ok {rtt} ms pid={ServicePid} uptime={ServiceUptimeMs} ms{(Simulated ? " (模擬)" : string.Empty)}"
            : $"失敗 {rtt} ms error={Error}";
    }
}
