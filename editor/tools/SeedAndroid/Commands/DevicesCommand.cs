// ============================================================
//  DevicesCommand.cs — devices（つながっている端末の一覧。--json で JSON）
//
//  JSON の形（エディタの実行先セレクタ・スクリプトから読む想定）:
//    [ { "serial": "emulator-5554", "state": "device", "ready": true, "kind": "emulator", "model": "...",
//        "product": "...", "device": "...", "transport_id": "13", "abi_list": "x86_64,arm64-v8a", "build_abi": "x86_64" }, ... ]
// ============================================================

using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SEEDEditor.Android.Adb;
using SEEDEditor.Android.Pipeline;
using SEEDEditor.Android.Toolchain;

namespace SEEDEditor.Tools.SeedAndroid.Commands;

/// <summary>devices。</summary>
public static class DevicesCommand
{
    /// <summary>JSON の書き出し設定（人が読める整形・日本語をそのまま）。</summary>
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// 端末の一覧を書く。
    /// </summary>
    /// <param name="toolchain">道具の場所（adb）。</param>
    /// <param name="json">JSON で書くか。</param>
    /// <param name="cancellationToken">中断の合図。</param>
    /// <returns>終了コード（端末が 0 台でも成功）。</returns>
    public static async Task<int> RunAsync(AndroidToolchain toolchain, bool json, CancellationToken cancellationToken)
    {
        var entries = await new AndroidDeviceActions(toolchain).ListDevicesAsync(cancellationToken);
        if (json)
        {
            var rows = entries.Select(entry => new
            {
                serial       = entry.Device.Serial,
                state        = entry.Device.StateText,
                ready        = entry.Device.IsReady,
                kind         = entry.Device.Kind == AdbDeviceKind.Emulator ? "emulator" : "physical",
                model        = entry.Device.Model,
                product      = entry.Device.Product,
                device       = entry.Device.DeviceName,
                transport_id = entry.Device.TransportId,
                abi_list     = entry.AbiList,
                build_abi    = entry.BuildAbi?.Name,
            });
            Console.Out.WriteLine(JsonSerializer.Serialize(rows, JsonOptions));
            return SeedAndroidExitCodes.Success;
        }

        if (entries.Count == 0)
        {
            Console.Out.WriteLine("つながっている端末はありません（実機の USB デバッグ、またはエミュレータの起動を確認してください）。");
            return SeedAndroidExitCodes.Success;
        }
        Console.Out.WriteLine($"{"シリアル",-24} {"種類",-8} {"状態",-14} {"ABI",-10} 機種");
        foreach (var entry in entries)
        {
            var device = entry.Device;
            Console.Out.WriteLine($"{device.Serial,-24} {device.KindLabel,-8} {device.StateText,-14} {entry.BuildAbi?.Name ?? "-",-10} {device.Model ?? "-"}");
            if (!device.IsReady) Console.Out.WriteLine("    " + AndroidDeviceSelector.DescribeNotReady(device));
        }
        return SeedAndroidExitCodes.Success;
    }
}
