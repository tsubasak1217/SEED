using System.Linq;
using SEEDEditor.Android;
using SEEDEditor.Android.Adb;
using SpriteRigTests;

namespace AndroidPipelineTests;

/// <summary>adb devices -l の解析・端末選び・ABI の選び方。</summary>
public static class AdbTests
{
    /// <summary>エミュレータ・実機・未許可・応答なしが混ざった出力（adb 36 の実際の形。daemon の起動行つき）。</summary>
    private const string MixedDevicesOutput =
        "* daemon not running; starting now at tcp:5037\r\n" +
        "* daemon started successfully\r\n" +
        "List of devices attached\r\n" +
        "emulator-5554          device product:sdk_gphone64_x86_64 model:sdk_gphone64_x86_64 device:emu64xa transport_id:13\r\n" +
        "2B011JEGR02535         device usb:1-1 product:bluejay model:Pixel_6a device:bluejay transport_id:14\r\n" +
        "R58M12345XY            unauthorized usb:1-2 transport_id:15\r\n" +
        "emulator-5556          offline transport_id:16\r\n" +
        "\r\n";

    /// <summary>テストを登録する。</summary>
    /// <param name="harness">テストランナー。</param>
    public static void Register(TestHarness harness)
    {
        harness.Add("adb devices -l: 端末ごとのシリアル・状態・種類・機種を読む", ParsesMixedOutput);
        harness.Add("adb devices -l: 空白を含む状態（no permissions）と URL を状態として読む", ParsesNoPermissionsState);
        harness.Add("adb devices -l: 端末が無い・見出しだけの出力は空", ParsesEmptyOutput);
        harness.Add("adb devices -l: 製品名 sdk_ の TCP 接続はエミュレータとみなす", EmulatorByProductPrefix);
        harness.Add("端末選び: シリアルの指定があればその端末", SelectsRequestedSerial);
        harness.Add("端末選び: 指定した端末が未許可なら許可を促すエラー", RequestedUnauthorizedFails);
        harness.Add("端末選び: 指定した端末が無ければエラー", RequestedMissingFails);
        harness.Add("端末選び: 指定が無く使える端末が 1 台ならそれ", SelectsSingleReady);
        harness.Add("端末選び: 使える端末が 2 台以上ならエラー（前回の実行先を添える）", MultipleReadyFails);
        harness.Add("端末選び: 使える端末が無ければ状態ごとの説明", NoReadyExplainsStates);
        harness.Add("ABI: 端末の abilist の先頭からビルドできるものを選ぶ", ChoosesAbiForDevice);
        harness.Add("ABI: 並びの解釈（重複をまとめ表の順に・知らない名前はエラー）", ParsesAbiList);
        harness.Add("pidof: 空白区切りの数字をプロセス ID にし、数字でない語・空は読み飛ばす", ParsesProcessIds);
    }

    /// <summary>混ざった出力を読む。</summary>
    private static void ParsesMixedOutput()
    {
        var devices = AdbDeviceListParser.Parse(MixedDevicesOutput);
        Check.Equal(4, devices.Count, "端末の数（daemon の行・見出し・空行は読み飛ばす）");

        var emulator = devices[0];
        Check.Equal("emulator-5554", emulator.Serial, "エミュレータのシリアル");
        Check.Equal(AdbDeviceState.Ready, emulator.State, "エミュレータの状態");
        Check.Equal(AdbDeviceKind.Emulator, emulator.Kind, "エミュレータの種類");
        Check.Equal("sdk_gphone64_x86_64", emulator.Model, "機種は adb の表記のまま");
        Check.Equal("13", emulator.TransportId, "transport_id");

        var phone = devices[1];
        Check.Equal(AdbDeviceKind.Physical, phone.Kind, "実機の種類");
        Check.Equal("Pixel_6a", phone.Model, "実機の機種");
        Check.Equal("bluejay", phone.Product, "製品名");
        Check.True(phone.IsReady, "実機は使える");

        Check.Equal(AdbDeviceState.Unauthorized, devices[2].State, "未許可");
        Check.True(devices[2].Model is null, "属性が無ければ機種は null");
        Check.Equal(AdbDeviceState.Offline, devices[3].State, "応答なし");
    }

    /// <summary>空白を含む状態を読む。</summary>
    private static void ParsesNoPermissionsState()
    {
        const string output = "List of devices attached\n" +
            "0123456789ABCDEF       no permissions (missing udev rules? user is in the plugdev group); see [http://developer.android.com/tools/device.html] usb:1-3 transport_id:2\n";
        var device = AdbDeviceListParser.Parse(output).Single();
        Check.Equal(AdbDeviceState.NoPermissions, device.State, "状態");
        Check.True(device.StateText.StartsWith("no permissions (missing udev rules?"), $"状態の文字列: {device.StateText}");
        Check.True(device.StateText.Contains("http://developer.android.com"), "URL（: を含む語）も状態の文字列に入る");
        Check.Equal("2", device.TransportId, "状態の後ろの属性は読める");
    }

    /// <summary>空の出力。</summary>
    private static void ParsesEmptyOutput()
    {
        Check.Equal(0, AdbDeviceListParser.Parse("List of devices attached\r\n\r\n").Count, "見出しだけ");
        Check.Equal(0, AdbDeviceListParser.Parse(string.Empty).Count, "空文字");
    }

    /// <summary>TCP 接続のエミュレータ。</summary>
    private static void EmulatorByProductPrefix()
    {
        var device = AdbDeviceListParser.Parse("localhost:5555 device product:sdk_gphone64_arm64 model:x device:emu64a transport_id:3").Single();
        Check.Equal(AdbDeviceKind.Emulator, device.Kind, "product:sdk_ はエミュレータ");
    }

    /// <summary>指定のシリアル。</summary>
    private static void SelectsRequestedSerial()
    {
        var devices = AdbDeviceListParser.Parse(MixedDevicesOutput);
        var selection = AndroidDeviceSelector.Select(devices, "2B011JEGR02535");
        Check.Equal("2B011JEGR02535", selection.Device?.Serial, "選んだ端末");
        Check.True(selection.Error is null, "エラーなし");
    }

    /// <summary>指定した端末が未許可。</summary>
    private static void RequestedUnauthorizedFails()
    {
        var selection = AndroidDeviceSelector.Select(AdbDeviceListParser.Parse(MixedDevicesOutput), "R58M12345XY");
        Check.True(selection.Device is null, "選ばない");
        Check.True(selection.Error!.Contains("許可"), $"許可を促す: {selection.Error}");
    }

    /// <summary>指定した端末が無い。</summary>
    private static void RequestedMissingFails()
    {
        var selection = AndroidDeviceSelector.Select(AdbDeviceListParser.Parse(MixedDevicesOutput), "nope");
        Check.True(selection.Device is null, "選ばない");
        Check.True(selection.Error!.Contains("emulator-5554"), $"つながっている端末を添える: {selection.Error}");
    }

    /// <summary>1 台だけ使える。</summary>
    private static void SelectsSingleReady()
    {
        const string output = "List of devices attached\nemulator-5554 device product:sdk_gphone64_x86_64 model:m device:d transport_id:1\nX unauthorized transport_id:2\n";
        var selection = AndroidDeviceSelector.Select(AdbDeviceListParser.Parse(output), null);
        Check.Equal("emulator-5554", selection.Device?.Serial, "使える 1 台");
    }

    /// <summary>2 台以上。</summary>
    private static void MultipleReadyFails()
    {
        var selection = AndroidDeviceSelector.Select(AdbDeviceListParser.Parse(MixedDevicesOutput), null, lastUsedSerial: "2B011JEGR02535");
        Check.True(selection.Device is null, "勝手に選ばない（私物の実機へ入れる事故を防ぐ）");
        Check.True(selection.Error!.Contains("2 台"), $"台数: {selection.Error}");
        Check.True(selection.Error.Contains("前回の実行先は 2B011JEGR02535"), $"前回の実行先: {selection.Error}");
    }

    /// <summary>使える端末が無い。</summary>
    private static void NoReadyExplainsStates()
    {
        var selection = AndroidDeviceSelector.Select(AdbDeviceListParser.Parse("List of devices attached\nX unauthorized transport_id:2\n"), null);
        Check.True(selection.Error!.Contains("許可"), $"未許可の対処: {selection.Error}");
        var none = AndroidDeviceSelector.Select(AdbDeviceListParser.Parse(string.Empty), null);
        Check.True(none.Error!.Contains("見つかりません"), $"0 台: {none.Error}");
    }

    /// <summary>ABI の選び方。</summary>
    private static void ChoosesAbiForDevice()
    {
        Check.Equal(AndroidAbis.X86_64, AndroidAbis.ChooseForDevice("x86_64,arm64-v8a"), "エミュレータ（ARM 変換つき）は x86_64");
        Check.Equal(AndroidAbis.Arm64, AndroidAbis.ChooseForDevice("arm64-v8a,armeabi-v7a,armeabi"), "実機は arm64-v8a");
        Check.Equal(AndroidAbis.Arm64, AndroidAbis.ChooseForDevice(" armeabi-v7a , arm64-v8a "), "合わないものを飛ばして次へ");
        Check.True(AndroidAbis.ChooseForDevice("armeabi-v7a,x86") is null, "合うものが無ければ null");
        Check.True(AndroidAbis.ChooseForDevice(null) is null, "null");
    }

    /// <summary>ABI の並びの解釈。</summary>
    private static void ParsesAbiList()
    {
        var abis = AndroidAbis.ParseList("x86_64, arm64-v8a,x86_64", out var error);
        Check.True(error is null, "エラーなし");
        Check.Equal("arm64-v8a,x86_64", AndroidAbis.Describe(abis), "重複をまとめ表の順に");
        AndroidAbis.ParseList("mips", out var unknown);
        Check.True(unknown is not null && unknown.Contains("mips"), $"知らない名前: {unknown}");
        AndroidAbis.ParseList(" , ", out var empty);
        Check.True(empty is not null, "空はエラー");
    }

    /// <summary>pidof の出力の解釈（エディタの実行がアプリの終了を見つけるのに使う）。</summary>
    private static void ParsesProcessIds()
    {
        Check.Equal("12345", string.Join(",", AdbClient.ParseProcessIds("12345\r\n")), "1 つ（改行つき）");
        Check.Equal("12345,12400", string.Join(",", AdbClient.ParseProcessIds(" 12345 12400 ")), "複数（空白区切り）");
        Check.Equal(0, AdbClient.ParseProcessIds(string.Empty).Count, "動いていなければ空");
        Check.Equal(0, AdbClient.ParseProcessIds("error: device offline").Count, "数字でない語は読み飛ばす");
        Check.Equal(0, AdbClient.ParseProcessIds("-5 0").Count, "0 以下・符号付きはプロセス ID ではない");
    }
}
