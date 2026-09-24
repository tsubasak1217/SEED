// ============================================================
//  AdbDeviceListParser.cs — `adb devices -l` の出力を端末の一覧にする（純粋な処理）
//
//  【出力の形（adb 35〜36 で確認）】
//    List of devices attached
//    emulator-5554          device product:sdk_gphone64_x86_64 model:sdk_gphone64_x86_64 device:emu64xa transport_id:13
//    2B011JEGR02535         device usb:1-1 product:bluejay model:Pixel_6a device:bluejay transport_id:14
//    R58M12345XY            unauthorized usb:1-2 transport_id:15
//    0123456789             no permissions (missing udev rules? user is in the plugdev group); see [http://...] usb:1-3 transport_id:2
//  1 列目がシリアル、その後ろ「key:value」の前までが状態（no permissions のように空白を含むことがある）。
//  「* daemon not running; starting now ...」「* daemon started successfully」の行や空行は読み飛ばす。
//
//  WPF に依存しない（単体テストからリンクされる）。
// ============================================================

using System;
using System.Collections.Generic;
using System.Linq;

namespace SEEDEditor.Android.Adb;

/// <summary><c>adb devices -l</c> の出力の解釈。</summary>
public static class AdbDeviceListParser
{
    /// <summary>一覧の見出し行。</summary>
    private const string HeaderLine = "List of devices attached";

    /// <summary>adb サーバーの起動メッセージの行頭。</summary>
    private const string DaemonMessagePrefix = "*";

    /// <summary>エミュレータのシリアルの接頭辞（emulator-5554 など。コンソールのポート番号付き）。</summary>
    private const string EmulatorSerialPrefix = "emulator-";

    /// <summary>エミュレータの製品名の接頭辞（AVD のシステムイメージは sdk_gphone64_x86_64 など）。</summary>
    private const string EmulatorProductPrefix = "sdk_";

    /// <summary>「key:value」の区切り。</summary>
    private const char KeyValueSeparator = ':';

    /// <summary>adb が状態の後ろに付ける属性のキー（これが現れたら状態の文字列は終わり）。</summary>
    private static readonly HashSet<string> AttributeKeys = new(StringComparer.Ordinal)
    {
        "usb", "product", "model", "device", "transport_id",
    };

    /// <summary>状態の文字列 → 状態。</summary>
    private static readonly Dictionary<string, AdbDeviceState> StateTable = new(StringComparer.OrdinalIgnoreCase)
    {
        ["device"]       = AdbDeviceState.Ready,
        ["unauthorized"] = AdbDeviceState.Unauthorized,
        ["offline"]      = AdbDeviceState.Offline,
        ["authorizing"]  = AdbDeviceState.Authorizing,
        ["connecting"]   = AdbDeviceState.Connecting,
    };

    /// <summary>「no permissions」の状態の書き出し（後ろに説明が続く）。</summary>
    private const string NoPermissionsPrefix = "no permissions";

    /// <summary>
    /// <c>adb devices -l</c> の出力を解釈する。
    /// </summary>
    /// <param name="output">adb の標準出力（行の区切りは \n / \r\n）。</param>
    /// <returns>端末の一覧（出力の順）。</returns>
    public static IReadOnlyList<AdbDevice> Parse(string output)
    {
        var devices = new List<AdbDevice>();
        foreach (var rawLine in output.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith(HeaderLine, StringComparison.Ordinal) ||
                line.StartsWith(DaemonMessagePrefix, StringComparison.Ordinal))
            {
                continue;
            }
            var device = ParseLine(line);
            if (device is not null) devices.Add(device);
        }
        return devices;
    }

    /// <summary>1 行を解釈する（シリアルと状態が無ければ null）。</summary>
    /// <param name="line">前後の空白を落とした 1 行。</param>
    /// <returns>端末。</returns>
    private static AdbDevice? ParseLine(string line)
    {
        var tokens = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length < 2) return null;

        var serial = tokens[0];
        var stateWords = new List<string>();
        var attributes = new Dictionary<string, string>(StringComparer.Ordinal);
        var inAttributes = false;
        foreach (var token in tokens.Skip(1))
        {
            var separator = token.IndexOf(KeyValueSeparator);
            var key = separator > 0 ? token[..separator] : null;
            if (key is not null && AttributeKeys.Contains(key))
            {
                inAttributes = true;
                attributes[key] = token[(separator + 1)..];
            }
            else if (!inAttributes)
            {
                stateWords.Add(token);
            }
        }
        if (stateWords.Count == 0) return null;

        var stateText = string.Join(' ', stateWords);
        var state = StateTable.TryGetValue(stateText, out var known) ? known
            : stateText.StartsWith(NoPermissionsPrefix, StringComparison.OrdinalIgnoreCase) ? AdbDeviceState.NoPermissions
            : AdbDeviceState.Other;

        attributes.TryGetValue("product", out var product);
        attributes.TryGetValue("model", out var model);
        attributes.TryGetValue("device", out var deviceName);
        attributes.TryGetValue("transport_id", out var transportId);
        var kind = serial.StartsWith(EmulatorSerialPrefix, StringComparison.Ordinal)
                   || (product?.StartsWith(EmulatorProductPrefix, StringComparison.Ordinal) ?? false)
            ? AdbDeviceKind.Emulator
            : AdbDeviceKind.Physical;

        // 機種名は adb が空白を _ にしたもの（model:Pixel_6a）。元から _ を含む名前（sdk_gphone64_x86_64）と
        // 見分けられないので、書き戻さずにそのまま持つ
        return new AdbDevice(serial, state, stateText, kind, model, product, deviceName, transportId);
    }
}
