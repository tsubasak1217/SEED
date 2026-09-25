// ============================================================
//  AndroidIpcSettings.cs — Android の IPC（端末のアプリとエディタをつなぐ TCP の通信路）のポートの決まり（段階D-1）
//
//  【ポートの流れ】
//    指定（AndroidRunRequest.IpcPort。エディタは環境設定 android.ipc_port、SeedAndroid は --ipc-port、設定 JSON は ipc_port）
//      null → 既定の DefaultDevicePort ／ 0 → 通信路を使わない ／ 1〜65535 → そのポート
//    → 起動の工程（Steps/LaunchStep）が am start の extra seed.ipc_port で端末へ渡す
//    → 端末のランタイムが 127.0.0.1:<ポート> で待ち受ける（runtime/src/engine/core/app_base/ipc_transport/）
//    → エディタ／SeedAndroid が adb forward tcp:0 tcp:<ポート>（PC 側は adb が空きポートを選ぶ）で forward してつなぐ
//      （Ipc/AndroidIpcConnector）
//
//  端末側のポートは端末の中だけで使う（PC のポートとはぶつからない）。PC 側のポートは adb が OS の空きポート
//  （Windows の動的ポート範囲 49152〜65535）から選ぶので、本番の Lore（41337 等）・AI ブリッジ（7234）とはぶつからない。
//
//  WPF に依存しない（SeedAndroid・単体テストからリンクされる）。
// ============================================================

namespace SEEDEditor.Android.Ipc;

/// <summary>Android の IPC のポートの決まり。</summary>
public static class AndroidIpcSettings
{
    /// <summary>
    /// 端末のランタイムが待ち受けるポートの既定値（端末の 127.0.0.1 だけ）。40000 台（本番の Lore のポートの並び）を避け、
    /// Android の標準のサービスが使わない高い番号にした。端末の別のアプリとぶつかるときは設定で変える。
    /// </summary>
    public const int DefaultDevicePort = 52735;

    /// <summary>「通信路を使わない」の指定（起動オプションを渡さず、一時停止などは使えない）。</summary>
    public const int DisabledPort = 0;

    /// <summary>使えるポートの最小値。</summary>
    public const int MinPort = 1;

    /// <summary>使えるポートの最大値。</summary>
    public const int MaxPort = 65535;

    /// <summary>
    /// 指定から、端末で待ち受けるポートを決める（純粋な処理）。
    /// </summary>
    /// <param name="requested">指定（null なら既定、<see cref="DisabledPort"/> なら使わない）。</param>
    /// <returns>ポート（使わないなら null）。</returns>
    public static int? ResolveDevicePort(int? requested) => requested switch
    {
        null => DefaultDevicePort,
        DisabledPort => null,
        var port => port,
    };

    /// <summary>
    /// 指定を確かめる（純粋な処理）。
    /// </summary>
    /// <param name="requested">指定。</param>
    /// <returns>誤りの説明（正しければ null）。</returns>
    public static string? Validate(int? requested) =>
        requested is null or DisabledPort or (>= MinPort and <= MaxPort)
            ? null
            : $"IPC のポート {requested} は使えません（{MinPort}〜{MaxPort}、使わないなら {DisabledPort}）。";
}
