// ============================================================
//  AdbDevice.cs — adb がつながっている端末 1 台の情報（adb devices -l の 1 行）
//
//  WPF に依存しない（コンソールツール・単体テストからリンクされる）。
// ============================================================

namespace SEEDEditor.Android.Adb;

/// <summary>端末の状態（adb devices の 2 列目）。</summary>
public enum AdbDeviceState
{
    /// <summary>使える（"device"）。</summary>
    Ready,

    /// <summary>USB デバッグが端末側でまだ許可されていない（"unauthorized"）。</summary>
    Unauthorized,

    /// <summary>応答しない（"offline"。起動中のエミュレータ・接続の不調）。</summary>
    Offline,

    /// <summary>OS から USB 機器へ触る権限が無い（"no permissions"。主に Linux）。</summary>
    NoPermissions,

    /// <summary>許可の確認中（"authorizing"）。</summary>
    Authorizing,

    /// <summary>接続中（"connecting"）。</summary>
    Connecting,

    /// <summary>それ以外（recovery・sideload・bootloader 等。アプリは入れられない）。</summary>
    Other,
}

/// <summary>端末の種類（エディタの実行先の「実機 / エミュレータ」）。</summary>
public enum AdbDeviceKind
{
    /// <summary>実機（USB・Wi-Fi）。</summary>
    Physical,

    /// <summary>PC のエミュレータ（AVD）。</summary>
    Emulator,
}

/// <summary>adb がつながっている端末 1 台。</summary>
/// <param name="Serial">シリアル（adb -s に渡す値）。</param>
/// <param name="State">状態。</param>
/// <param name="StateText">adb が出した状態の文字列そのまま（表示・エラー用）。</param>
/// <param name="Kind">実機かエミュレータか。</param>
/// <param name="Model">機種（adb の表記のまま。空白は _ になっている。例 Pixel_6a。無ければ null）。</param>
/// <param name="Product">製品名（product:）。</param>
/// <param name="DeviceName">デバイス名（device:）。</param>
/// <param name="TransportId">adb の接続番号（transport_id:）。</param>
public sealed record AdbDevice(
    string Serial,
    AdbDeviceState State,
    string StateText,
    AdbDeviceKind Kind,
    string? Model,
    string? Product,
    string? DeviceName,
    string? TransportId)
{
    /// <summary>アプリを入れて動かせる状態か。</summary>
    public bool IsReady => State == AdbDeviceState.Ready;

    /// <summary>表示名（機種があれば「機種（シリアル）」、無ければシリアル）。</summary>
    public string DisplayName => Model is null ? Serial : $"{Model}（{Serial}）";

    /// <summary>種類の表示名。</summary>
    public string KindLabel => Kind == AdbDeviceKind.Emulator ? "エミュレータ" : "実機";
}
