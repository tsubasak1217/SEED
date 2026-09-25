// ============================================================
//  AndroidDeviceTarget.cs — 実行先の端末の「決め方」（指定の読み替え。純粋な処理）
//
//  【4 つの決め方】（AndroidRunRequest の serial と emulator_fallback から決まる）
//    Single          … 指定なし（serial が空）。使える端末がちょうど 1 台のときそれ（従来どおり。0 台・2 台以上はエラー）
//    Exact           … シリアルの指定。その端末だけ（見えなければエラー）。SeedAndroid の --serial <シリアル>
//    ExactOrEmulator … シリアルの指定＋エミュレータへの切り替え。その端末が adb に見えなければ、起動中のエミュレータ、
//                      無ければ AVD を起動してそちらで実行する（エディタの実行先セレクタで特定の端末を選んだとき。段階C-3）
//    Auto            … serial が "auto"。実機（前回使ったものを優先）→ 起動中のエミュレータ → AVD を起動
//                      （エディタの「Android（自動）」・SeedAndroid の --serial auto。段階C-3）
//  どの端末を選ぶかの規則そのものは AndroidDeviceSelector.DecideForRun、エミュレータの起動と待ち合わせは
//  Emulator/AndroidDeviceProvisioner。
//
//  WPF に依存しない（コンソールツール・単体テストからリンクされる）。
// ============================================================

using System;

namespace SEEDEditor.Android.Adb;

/// <summary>端末の決め方。</summary>
public enum AndroidDeviceTargetMode
{
    /// <summary>指定なし: 使える端末がちょうど 1 台のときそれ（従来どおり）。</summary>
    Single,

    /// <summary>シリアルの指定: その端末だけ（見えなければエラー）。</summary>
    Exact,

    /// <summary>シリアルの指定: その端末が見えなければエミュレータで実行する（エディタで端末を選んだとき）。</summary>
    ExactOrEmulator,

    /// <summary>自動: 実機（前回使ったものを優先）→ 起動中のエミュレータ → AVD を起動。</summary>
    Auto,
}

/// <summary>実行先の端末の決め方と、指定されたシリアル。</summary>
/// <param name="Mode">決め方。</param>
/// <param name="Serial">指定されたシリアル（Exact / ExactOrEmulator のときだけ。それ以外は null）。</param>
public sealed record AndroidDeviceTarget(AndroidDeviceTargetMode Mode, string? Serial)
{
    /// <summary>
    /// 「自動」を表すシリアルの値（SeedAndroid の --serial auto・設定 JSON の "serial": "auto"・エディタの実行先「Android（自動）」）。
    /// adb のシリアル（英数字・emulator-5554・IP:ポート・mDNS 名）にこの語は無いので取り違えない。
    /// </summary>
    public const string AutoSerial = "auto";

    /// <summary>シリアルの値が「自動」か（前後の空白・大文字小文字は問わない）。</summary>
    /// <param name="serial">シリアルの値。</param>
    /// <returns>「自動」なら true。</returns>
    public static bool IsAutoSerial(string? serial) =>
        string.Equals(serial?.Trim(), AutoSerial, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 指定（シリアルとエミュレータへの切り替えの可否）から決め方を作る。
    /// </summary>
    /// <param name="serial">シリアルの指定（null・空・"auto"・シリアル）。</param>
    /// <param name="emulatorFallback">指定の端末が見えないときにエミュレータで実行するか。</param>
    /// <returns>決め方。</returns>
    public static AndroidDeviceTarget From(string? serial, bool emulatorFallback)
    {
        if (string.IsNullOrWhiteSpace(serial)) return new AndroidDeviceTarget(AndroidDeviceTargetMode.Single, null);
        if (IsAutoSerial(serial)) return new AndroidDeviceTarget(AndroidDeviceTargetMode.Auto, null);
        return new AndroidDeviceTarget(
            emulatorFallback ? AndroidDeviceTargetMode.ExactOrEmulator : AndroidDeviceTargetMode.Exact, serial.Trim());
    }

    /// <summary>エミュレータを起動することがある決め方か（端末の工程を行うときだけ起動する）。</summary>
    public bool MayLaunchEmulator => Mode is AndroidDeviceTargetMode.Auto or AndroidDeviceTargetMode.ExactOrEmulator;
}
