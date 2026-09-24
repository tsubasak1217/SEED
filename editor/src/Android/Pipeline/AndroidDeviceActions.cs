// ============================================================
//  AndroidDeviceActions.cs — ビルドを伴わない端末の操作（端末の一覧・アプリの停止・動いているかの確認・logcat）
//
//  エディタの実行先セレクタ（段階C-2）が端末の一覧を出す・停止ボタンでアプリを止める・アプリ側の終了を見つける
//  （pidof）・Output パネルへ logcat を流すのに使う。
//  コンソールツールの devices / stop / logcat も同じ。触るのは自分のアプリ（アプリ ID）だけ。
//
//  WPF に依存しない（コンソールツール・単体テストからリンクされる）。
// ============================================================

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SEEDEditor.Android.Adb;
using SEEDEditor.Android.Processes;
using SEEDEditor.Android.Toolchain;

namespace SEEDEditor.Android.Pipeline;

/// <summary>一覧に出す端末 1 台（ABI 付き）。</summary>
/// <param name="Device">端末。</param>
/// <param name="AbiList">端末の ro.product.cpu.abilist（使えない状態・読めなければ null）。</param>
/// <param name="BuildAbi">この端末向けにビルドする ABI（合うものが無ければ null）。</param>
public sealed record AndroidDeviceEntry(AdbDevice Device, string? AbiList, AndroidAbi? BuildAbi);

/// <summary>ビルドを伴わない端末の操作。</summary>
public sealed class AndroidDeviceActions
{
    /// <summary>道具の場所。</summary>
    private readonly AndroidToolchain _toolchain;

    /// <summary>道具の場所を指定して作る。</summary>
    /// <param name="toolchain">道具の場所。</param>
    public AndroidDeviceActions(AndroidToolchain toolchain)
    {
        _toolchain = toolchain;
    }

    /// <summary>
    /// つながっている端末の一覧（使える端末は ABI も読む）。
    /// </summary>
    /// <param name="cancellationToken">中断の合図。</param>
    /// <returns>端末の一覧。</returns>
    /// <exception cref="AndroidPipelineException">adb が無い・一覧を取れないとき。</exception>
    public async Task<IReadOnlyList<AndroidDeviceEntry>> ListDevicesAsync(CancellationToken cancellationToken)
    {
        var adb = CreateAdb();
        IReadOnlyList<AdbDevice> devices;
        try
        {
            devices = await adb.ListDevicesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is AdbCommandException or ChildProcessStartException)
        {
            throw new AndroidPipelineException(AndroidFailureKind.Device, $"端末の一覧を取れません: {ex.Message}", ex);
        }

        var entries = new List<AndroidDeviceEntry>();
        foreach (var device in devices)
        {
            string? abiList = null;
            if (device.IsReady)
            {
                try
                {
                    abiList = await adb.GetPropertyAsync(device.Serial, AdbClient.AbiListProperty, cancellationToken).ConfigureAwait(false);
                }
                catch (AdbCommandException)
                {
                    // 一覧の表示は続ける（ABI は不明）
                }
            }
            entries.Add(new AndroidDeviceEntry(device, abiList, abiList is null ? null : AndroidAbis.ChooseForDevice(abiList)));
        }
        return entries;
    }

    /// <summary>
    /// アプリを止める（am force-stop。自分のアプリだけ）。
    /// </summary>
    /// <param name="serial">端末のシリアル（null なら使える端末がちょうど 1 台のときそれ）。</param>
    /// <param name="applicationId">アプリ ID。</param>
    /// <param name="cancellationToken">中断の合図。</param>
    /// <returns>止めた端末。</returns>
    public async Task<AdbDevice> StopAppAsync(string? serial, string applicationId, CancellationToken cancellationToken)
    {
        var (adb, device) = await SelectDeviceAsync(serial, cancellationToken).ConfigureAwait(false);
        var exitCode = await adb.ForceStopAsync(device.Serial, applicationId, cancellationToken).ConfigureAwait(false);
        if (exitCode != 0)
        {
            throw new AndroidPipelineException(AndroidFailureKind.DeviceOperation, $"am force-stop {applicationId} が失敗しました（終了コード {exitCode}）。");
        }
        return device;
    }

    /// <summary>
    /// アプリが端末で動いているか（pidof。自分のアプリだけを問い合わせる）。
    /// エディタの実行（段階C-2）が、アプリ側で終了した（戻るキー・クラッシュ等）ことを見つけて「実行中」を終えるのに使う。
    /// 端末の一覧は取り直さない（数秒おきに呼ぶため。端末が外れていれば adb の失敗として例外になる）。
    /// </summary>
    /// <param name="serial">端末のシリアル。</param>
    /// <param name="applicationId">アプリ ID。</param>
    /// <param name="cancellationToken">中断の合図。</param>
    /// <returns>動いていれば true。</returns>
    /// <exception cref="AndroidPipelineException">adb が無い・問い合わせが失敗したとき。</exception>
    public async Task<bool> IsAppRunningAsync(string serial, string applicationId, CancellationToken cancellationToken)
    {
        var adb = CreateAdb();
        try
        {
            var pids = await adb.GetProcessIdsAsync(serial, applicationId, cancellationToken).ConfigureAwait(false);
            return pids.Count > 0;
        }
        catch (Exception ex) when (ex is AdbCommandException or ChildProcessStartException)
        {
            throw new AndroidPipelineException(AndroidFailureKind.DeviceOperation, ex.Message, ex);
        }
    }

    /// <summary>
    /// logcat を流す（止められるまで、または指定の長さ）。
    /// </summary>
    /// <param name="serial">端末のシリアル（null なら使える端末がちょうど 1 台のときそれ）。</param>
    /// <param name="since">この時刻以降（端末の時刻 "MM-dd HH:mm:ss.fff"。null なら今から）。</param>
    /// <param name="duration">流す長さ（null なら止められるまで）。</param>
    /// <param name="logFile">保存先（null なら保存しない）。</param>
    /// <param name="onLine">1 行ごとに呼ばれる。</param>
    /// <param name="cancellationToken">止める合図。</param>
    /// <returns>端末・起点の時刻・流した行数。</returns>
    public async Task<(AdbDevice Device, string Since, int Lines)> StreamLogcatAsync(
        string? serial, string? since, TimeSpan? duration, string? logFile, Action<string> onLine, CancellationToken cancellationToken)
    {
        var (adb, device) = await SelectDeviceAsync(serial, cancellationToken).ConfigureAwait(false);
        var start = since ?? await adb.GetLogcatSinceAsync(device.Serial, cancellationToken).ConfigureAwait(false);
        var (lines, _) = await AndroidLogcatSession.RunAsync(
            adb, device.Serial, start, AndroidRuntimeContract.LogcatFilters, duration, logFile, onLine, cancellationToken).ConfigureAwait(false);
        return (device, start, lines);
    }

    /// <summary>adb を用意する（無ければ理由付きの例外）。</summary>
    private AdbClient CreateAdb() => new(_toolchain.RequireAdb());

    /// <summary>端末を 1 台に決める。</summary>
    private async Task<(AdbClient Adb, AdbDevice Device)> SelectDeviceAsync(string? serial, CancellationToken cancellationToken)
    {
        var adb = CreateAdb();
        IReadOnlyList<AdbDevice> devices;
        try
        {
            devices = await adb.ListDevicesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is AdbCommandException or ChildProcessStartException)
        {
            throw new AndroidPipelineException(AndroidFailureKind.Device, $"端末の一覧を取れません: {ex.Message}", ex);
        }
        var selection = AndroidDeviceSelector.Select(devices, serial);
        return selection.Device is null
            ? throw new AndroidPipelineException(AndroidFailureKind.Device, selection.Error ?? "端末を決められません。")
            : (adb, selection.Device);
    }
}
