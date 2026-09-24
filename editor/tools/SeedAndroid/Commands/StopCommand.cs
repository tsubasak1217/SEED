// ============================================================
//  StopCommand.cs — stop（アプリを止める。am force-stop）
//
//  止めるアプリ ID は --app-id、無ければ --project / --assets-dir（設定 JSON 可）の設定から決める
//  （ビルドのときと同じ既定値。プロジェクトも無ければ com.seedengine.runtime）。
// ============================================================

using System;
using System.Threading;
using System.Threading.Tasks;
using SEEDEditor.Android.Pipeline;
using SEEDEditor.Android.Project;
using SEEDEditor.Android.Toolchain;

namespace SEEDEditor.Tools.SeedAndroid.Commands;

/// <summary>stop。</summary>
public static class StopCommand
{
    /// <summary>
    /// アプリを止める。
    /// </summary>
    /// <param name="toolchain">道具の場所（adb）。</param>
    /// <param name="line">コマンドラインの指定。</param>
    /// <param name="config">設定 JSON の値（無ければ null）。</param>
    /// <param name="cancellationToken">中断の合図。</param>
    /// <returns>終了コード。</returns>
    public static async Task<int> RunAsync(
        AndroidToolchain toolchain, SeedAndroidCommandLine line, AndroidRunRequest? config, CancellationToken cancellationToken)
    {
        var request = SeedAndroidArguments.ToRequest(line, config);
        string applicationId;
        if (!string.IsNullOrWhiteSpace(line.ApplicationId))
        {
            applicationId = line.ApplicationId.Trim();
            if (!AndroidAppIdentityResolver.IsValidApplicationId(applicationId))
            {
                Console.Error.WriteLine($"エラー: アプリ ID の形式が正しくありません: {applicationId}");
                return SeedAndroidExitCodes.InvalidRequest;
            }
        }
        else
        {
            applicationId = AndroidProjectResolver.ResolveIdentity(
                AndroidProjectResolver.Resolve(request.ProjectDir, request.AssetsDir)).ApplicationId;
        }

        var device = await new AndroidDeviceActions(toolchain).StopAppAsync(request.Serial, applicationId, cancellationToken);
        Console.Out.WriteLine($"{applicationId} を止めました（{device.DisplayName}）。");
        return SeedAndroidExitCodes.Success;
    }
}
