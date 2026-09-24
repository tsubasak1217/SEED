// ============================================================
//  LogcatCommand.cs — logcat（タグ SEED・DOTNET ほかを流す。Ctrl+C か --logcat-seconds で止める）
//
//  起点は --since（端末の時刻）、無ければ今。共用の端末で logcat -c はしない。--log-file で UTF-8 に保存する。
// ============================================================

using System;
using System.Threading;
using System.Threading.Tasks;
using SEEDEditor.Android.Pipeline;
using SEEDEditor.Android.Toolchain;

namespace SEEDEditor.Tools.SeedAndroid.Commands;

/// <summary>logcat。</summary>
public static class LogcatCommand
{
    /// <summary>
    /// logcat を流す。
    /// </summary>
    /// <param name="toolchain">道具の場所（adb）。</param>
    /// <param name="line">コマンドラインの指定。</param>
    /// <param name="config">設定 JSON の値（無ければ null）。</param>
    /// <param name="cancellationToken">止める合図（Ctrl+C）。</param>
    /// <returns>終了コード（止めたら成功）。</returns>
    public static async Task<int> RunAsync(
        AndroidToolchain toolchain, SeedAndroidCommandLine line, AndroidRunRequest? config, CancellationToken cancellationToken)
    {
        var request = SeedAndroidArguments.ToRequest(line, config);
        var duration = request.LogcatSeconds > 0 ? TimeSpan.FromSeconds(request.LogcatSeconds) : (TimeSpan?)null;
        var gate = new object();
        var (device, since, lines) = await new AndroidDeviceActions(toolchain).StreamLogcatAsync(
            request.Serial, line.Since, duration, request.LogFile,
            text =>
            {
                lock (gate) Console.Out.WriteLine(text);
            },
            cancellationToken);
        Console.Error.WriteLine($"logcat を終えました（{device.DisplayName}・起点 {since}・{lines} 行）。");
        return SeedAndroidExitCodes.Success;
    }
}
