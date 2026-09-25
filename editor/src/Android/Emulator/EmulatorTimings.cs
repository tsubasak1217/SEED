// ============================================================
//  EmulatorTimings.cs — エミュレータの起動待ちの時間の決まり（データ）
//
//  AndroidDeviceProvisioner が使う。単体テストは仮の時計（WaitClock）で進めるので、既定値のままでも一瞬で終わる。
//
//  WPF に依存しない（コンソールツール・単体テストからリンクされる）。
// ============================================================

using System;

namespace SEEDEditor.Android.Emulator;

/// <summary>エミュレータの起動待ちの時間の決まり。</summary>
/// <param name="BootTimeout">起動の完了（adb に現れる → device → sys.boot_completed=1）を待つ上限。</param>
/// <param name="PollInterval">adb で状態を確かめる間隔。</param>
/// <param name="ProgressInterval">待っている旨を Output パネル／コンソールへ出す間隔。</param>
public sealed record EmulatorTimings(TimeSpan BootTimeout, TimeSpan PollInterval, TimeSpan ProgressInterval)
{
    /// <summary>
    /// 既定の起動待ちの上限（秒）。quick boot（スナップショットからの起動）なら 20〜60 秒、スナップショットが無い・壊れた
    /// ときの cold boot は 2〜4 分かかる（PC のメモリが少ないと更に延びる）ので、cold boot も収まる長さにする。
    /// </summary>
    public const double DefaultBootTimeoutSeconds = 300.0;

    /// <summary>既定の確認の間隔（秒）。adb の子プロセスを起こすので短くしすぎない。</summary>
    public const double DefaultPollIntervalSeconds = 2.0;

    /// <summary>既定の「待っています」の行の間隔（秒）。</summary>
    public const double DefaultProgressIntervalSeconds = 10.0;

    /// <summary>既定値（エディタ・SeedAndroid が使う）。</summary>
    public static readonly EmulatorTimings Default = new(
        TimeSpan.FromSeconds(DefaultBootTimeoutSeconds),
        TimeSpan.FromSeconds(DefaultPollIntervalSeconds),
        TimeSpan.FromSeconds(DefaultProgressIntervalSeconds));
}
